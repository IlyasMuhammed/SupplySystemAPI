using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SMS.Modules.Logistics.Constants;
using SMS.Modules.Logistics.Couriers.Labels;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Domain.StateMachines;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Repositories;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Couriers.Booking;

/// <summary>What the booking surface offers a request. Public because the controller is.</summary>
public interface IConsignmentBookingService
{
    /// <summary>
    /// Validates, moves the consignment to BOOKING and hands the carrier call to the background.
    /// Returns immediately; asking again while it is under way sends nothing twice.
    /// </summary>
    Task<ConsignmentBookingStatusModel?> RequestBookingAsync(Guid consignmentUuid, BookConsignmentRequest req, int userId);

    Task<ConsignmentBookingStatusModel?> GetStatusAsync(Guid consignmentUuid);

    /// <summary>Settles a booking whose outcome is unknown, after a person has checked with the carrier.</summary>
    Task<ConsignmentBookingStatusModel?> ResolveAsync(Guid consignmentUuid, ResolveBookingRequest req, int userId);
}

internal enum BookingRunResult
{
    Booked,
    Failed,
    AwaitingRetry,
    NeedsResolution,
    InFlight,
    NotApplicable,
    NotFound
}

internal interface IConsignmentBookingExecutor
{
    /// <summary>One run of the booking job. Safe to call any number of times for one consignment.</summary>
    Task<BookingRunResult> ExecuteAsync(
        Guid consignmentUuid, Guid organizationId, DateTime? utcNow = null, CancellationToken ct = default);
}

/// <summary>The numbers the orchestration and its sweep agree on.</summary>
internal static class BookingPolicy
{
    /// <summary>
    /// Automatic retries of an unknown outcome stop here, even for a carrier that deduplicates. A
    /// carrier that has failed to answer five times is not going to be fixed by a sixth.
    /// </summary>
    internal const int MaxAutomaticAttempts = 5;

    /// <summary>
    /// How long a consignment sits in BOOKING untouched before the sweep takes an interest — long
    /// enough that it never races a job that was enqueued a moment ago.
    /// </summary>
    internal static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(2);

    internal static readonly TimeSpan DefaultCallTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Backs off, so a carrier outage is not met with a retry storm.</summary>
    internal static TimeSpan RetryDelayAfter(int attemptCount) => attemptCount switch
    {
        <= 1 => TimeSpan.FromMinutes(1),
        2    => TimeSpan.FromMinutes(5),
        3    => TimeSpan.FromMinutes(15),
        _    => TimeSpan.FromMinutes(60)
    };

    internal static bool Deduplicates(ICourierProviderRegistry registry, string providerKey) =>
        registry.Find(providerKey)?.Capabilities.HonoursIdempotencyKey == true;
}

/// <summary>
/// Books consignments through carrier adapters: <c>DRAFT → BOOKING → BOOKED</c>, with every call
/// going through the idempotency ledger.
/// <para>
/// <b>Split in two on purpose.</b> The request validates everything that can be known without the
/// carrier, pins what will be sent (account, service, key) and returns. The job makes the call.
/// A carrier call inside a web request is one a browser timeout, a double click or an app
/// recycle can abandon half way — and a half-made booking call is precisely the unknown outcome
/// the ledger exists to contain.
/// </para>
/// <para>
/// <b>Nothing is resolved by a user clicking again.</b> Asking to book a consignment already in
/// BOOKING sends nothing. An unknown outcome is retried by the job itself when the carrier
/// deduplicates, handed to a person when it does not, and picked up by the sweep if the job never
/// finished at all.
/// </para>
/// </summary>
internal sealed class ConsignmentBookingService : IConsignmentBookingService, IConsignmentBookingExecutor
{
    private const int FailureReasonMax = 1000;

    private static readonly ShipmentStateMachine Machine = ShipmentStateMachine.Instance;

    private readonly LogisticsDbContext           _db;
    private readonly ICarrierAccountResolver      _resolver;
    private readonly ICarrierCommandLedger        _ledger;
    private readonly ICourierProviderRegistry     _registry;
    private readonly IConsignmentBookingScheduler _scheduler;
    private readonly IConsignmentLabelStore       _labels;
    private readonly ILogger<ConsignmentBookingService> _logger;
    private readonly TimeSpan                     _callTimeout;

    public ConsignmentBookingService(
        LogisticsDbContext db, ICarrierAccountResolver resolver, ICarrierCommandLedger ledger,
        ICourierProviderRegistry registry, IConsignmentBookingScheduler scheduler, IConsignmentLabelStore labels,
        IConfiguration configuration, ILogger<ConsignmentBookingService> logger)
    {
        _db        = db;
        _resolver  = resolver;
        _ledger    = ledger;
        _registry  = registry;
        _scheduler = scheduler;
        _labels    = labels;
        _logger    = logger;

        // The call must give up well before the ledger's lease runs out. Otherwise a slow but
        // healthy call is declared unknown — and possibly retried — while it is still running.
        var configured = configuration.GetValue<int?>("Logistics:Booking:CarrierCallTimeoutSeconds");
        var ceiling    = CarrierCommandLedger.LeaseDuration - TimeSpan.FromSeconds(30);
        var timeout    = configured is > 0 ? TimeSpan.FromSeconds(configured.Value) : BookingPolicy.DefaultCallTimeout;
        _callTimeout   = timeout < ceiling ? timeout : ceiling;
    }

    // ── Requesting ────────────────────────────────────────────────────────────

    public async Task<ConsignmentBookingStatusModel?> RequestBookingAsync(
        Guid consignmentUuid, BookConsignmentRequest req, int userId)
    {
        ArgumentNullException.ThrowIfNull(req);

        var consignment = await WithBookingGraph(_db.Consignments)
            .FirstOrDefaultAsync(c => c.UUID == consignmentUuid && !c.IsDelete);

        if (consignment is null) return null;

        var status = StatusOf(consignment);

        // Already under way: say so, and send nothing. This is the double click.
        if (status == ShipmentStatus.Booking)
            return await StatusModelAsync(consignment);

        if (status == ShipmentStatus.Booked)
            throw new ConflictException(
                $"Consignment {consignment.ConsignmentNumber} is already booked, airway bill {consignment.MasterAwb}.");

        Machine.EnsureCanTransition(status, ShipmentStatus.Booking);

        var carrier = consignment.Carrier
            ?? throw new BadRequestException(
                "This consignment has no carrier, so there is nobody to book it with. Assign a carrier first.");

        switch (ConsignmentRepository.IntegrationModeOf(carrier))
        {
            case CarrierIntegrationMode.Manual:
                throw new ConflictException(
                    $"{carrier.Name} has no API integration. Book it with the carrier yourself and record the " +
                    "airway bill through manual booking.");

            case CarrierIntegrationMode.File:
                throw new ConflictException(
                    $"{carrier.Name} is set to FILE integration, which has no booking adapter yet. Book it by " +
                    "hand and record the airway bill through manual booking.");
        }

        // Throws for every configuration problem — no account, several with no default, inactive,
        // someone else's, adapter missing — before anything changes.
        var resolved = await _resolver.ResolveAsync(carrier.UUID, req.CarrierAccountUuid);

        var serviceCode = Trim(req.ServiceCode) ?? consignment.CarrierServiceCode ?? resolved.DefaultServiceCode;
        consignment.CarrierServiceCode = serviceCode;

        var key = $"{consignment.ConsignmentNumber}-{Guid.NewGuid():N}";

        // Built now only to refuse early; the job builds it again from what is saved below.
        CourierBookingRequestFactory.Build(consignment, resolved, key);

        // The entity, not just its id: the status returned below reads the account's name and
        // sandbox flag, and an id alone leaves that navigation empty until the next load.
        consignment.CarrierAccount = resolved.AccountUuid == Guid.Empty
            ? null
            : await _db.CarrierAccounts.SingleAsync(a => a.UUID == resolved.AccountUuid);
        consignment.CarrierAccountId = consignment.CarrierAccount?.Id;

        consignment.BookingIdempotencyKey = key;
        consignment.BookingFailureReason  = null;
        consignment.Status                = LogisticsCode.Of(ShipmentStatus.Booking);
        consignment.ModifiedBy            = userId;
        consignment.ModifiedDate          = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        // After the save: a job must never find a consignment that is not yet BOOKING. If the
        // enqueue itself fails, the sweep finds a BOOKING consignment with no call and enqueues it.
        _scheduler.Enqueue(consignment.UUID, consignment.OrganizationId);

        return await StatusModelAsync(consignment);
    }

    // ── The job ───────────────────────────────────────────────────────────────

    public async Task<BookingRunResult> ExecuteAsync(
        Guid consignmentUuid, Guid organizationId, DateTime? utcNow = null, CancellationToken ct = default)
    {
        DateTime Now() => utcNow ?? DateTime.UtcNow;

        // Explicit organization, filter off: a job from the sweep runs with no tenant at all.
        var consignment = await WithBookingGraph(_db.Consignments.IgnoreQueryFilters())
            .FirstOrDefaultAsync(c => c.UUID == consignmentUuid && c.OrganizationId == organizationId && !c.IsDelete, ct);

        if (consignment is null) return BookingRunResult.NotFound;

        // Booked already, or never asked to be. Either way there is nothing for this run to do.
        if (StatusOf(consignment) != ShipmentStatus.Booking) return BookingRunResult.NotApplicable;

        var key = consignment.BookingIdempotencyKey;
        if (string.IsNullOrWhiteSpace(key))
            return await FailAsync(consignment, Now(),
                "The booking has no idempotency key, so it cannot be sent safely. Request the booking again.");

        var existing = await _ledger.FindAsync(CarrierCommandType.Book, key, organizationId, ct);

        // An answer is already on the ledger — this run is catching the consignment up with it.
        if (existing is { Status: CarrierCommandStatus.Succeeded or CarrierCommandStatus.Refused or CarrierCommandStatus.NotPerformed })
            return await ApplyAsync(consignment, existing, Now());

        if (existing is { Status: CarrierCommandStatus.Unknown } && existing.AttemptCount >= BookingPolicy.MaxAutomaticAttempts)
            return await AwaitResolutionAsync(consignment, Now(),
                $"Retried {existing.AttemptCount} times and the outcome is still unknown. Check with the carrier " +
                "and resolve the booking.");

        ResolvedCarrierAccount resolved;
        CourierBookingRequest  request;

        try
        {
            var carrier = consignment.Carrier
                ?? throw new ConflictException("The consignment's carrier has been removed.");

            resolved = await _resolver.ResolveAsync(carrier.UUID, consignment.CarrierAccount?.UUID, ct);
            request  = CourierBookingRequestFactory.Build(consignment, resolved, key);
        }
        catch (Exception ex) when (ex is BadRequestException or ConflictException or NotFoundException)
        {
            // Something changed since the booking was requested — an account deactivated, a
            // package voided. If nothing has been sent under this key, that is a clean failure.
            // If something has, the carrier may already hold a booking, and failing here would
            // invite a second one.
            return existing is null
                ? await FailAsync(consignment, Now(), ex.Message)
                : await AwaitResolutionAsync(consignment, Now(),
                    $"{ex.Message} An earlier attempt's outcome is still unknown, so the booking cannot simply be " +
                    "retried — check with the carrier and resolve it.");
        }

        var decision = await _ledger.BeginAsync(
            new CarrierCommandClaim(
                CarrierCommandType.Book, key, CourierRequestFingerprint.For(request), consignment.Id,
                consignment.CarrierAccountId, resolved.Provider.Key, consignment.ModifiedBy ?? consignment.CreatedBy),
            organizationId, Now(), ct);

        switch (decision.Kind)
        {
            case LedgerDecisionKind.Proceed:
            {
                var (command, label) = await CallCarrierAsync(resolved.Provider, request, decision, utcNow, ct);
                var outcome = await ApplyAsync(consignment, command, Now());

                if (outcome == BookingRunResult.Booked && label is not null)
                    await TryStoreLabelAsync(consignment, label, resolved.Provider.Key, Now());

                return outcome;
            }

            case LedgerDecisionKind.AlreadySucceeded:
            case LedgerDecisionKind.AlreadyRefused:
                return await ApplyAsync(consignment, decision.Command, Now());

            case LedgerDecisionKind.InFlight:
                // Another worker is on it. It will apply its own answer.
                return BookingRunResult.InFlight;

            case LedgerDecisionKind.NeedsResolution:
                return await AwaitResolutionAsync(consignment, Now(), decision.Explanation!);

            case LedgerDecisionKind.KeyRetired:
                return await FailAsync(consignment, Now(), decision.Explanation!);

            default:
                throw new InvalidOperationException($"Unhandled ledger decision {decision.Kind}.");
        }
    }

    /// <returns>What the ledger now knows, and the label if the carrier returned one with the booking.</returns>
    private async Task<(CarrierCommandSnapshot command, CourierLabel? label)> CallCarrierAsync(
        ICourierProvider provider, CourierBookingRequest request, LedgerDecision decision, DateTime? utcNow,
        CancellationToken ct)
    {
        CourierBookingResult result;

        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(_callTimeout);

            try
            {
                result = await provider.BookAsync(request, timeout.Token);
            }
            catch (Exception ex)
            {
                // Per the adapter contract an exception means the call came apart: unknown, not failed.
                // Recorded with CancellationToken.None — whatever cancelled the call must not also
                // stop us writing down that it happened.
                return (await _ledger.RecordExceptionAsync(decision.Command.Uuid, ex, utcNow ?? DateTime.UtcNow, CancellationToken.None), null);
            }
        }

        // The carrier answered. From here nothing may abandon the record of it.
        var command = await _ledger.RecordBookingResultAsync(decision.Command.Uuid, result, utcNow ?? DateTime.UtcNow, CancellationToken.None);
        return (command, result.Label);
    }

    /// <summary>
    /// Keeps a label the carrier returned with the booking. Best effort, and deliberately so: the
    /// booking is already real and recorded, a bad label must not undo that, and a missing label is
    /// fetched from the carrier the first time someone prints.
    /// </summary>
    private async Task TryStoreLabelAsync(Consignment consignment, CourierLabel label, string providerKey, DateTime now)
    {
        try
        {
            await _labels.StoreAsync(consignment, label, ConsignmentLabelSource.Booking, providerKey,
                                     consignment.ModifiedBy ?? consignment.CreatedBy, now, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Consignment {ConsignmentUuid} booked, but its label could not be stored ({Reason}). It will be " +
                "fetched from the carrier when first printed.", consignment.UUID, ex.Message);
        }
    }

    /// <summary>Brings the consignment into line with what the ledger knows.</summary>
    private async Task<BookingRunResult> ApplyAsync(Consignment consignment, CarrierCommandSnapshot command, DateTime now)
    {
        switch (command.Status)
        {
            case CarrierCommandStatus.Succeeded:
                Machine.EnsureCanTransition(StatusOf(consignment), ShipmentStatus.Booked);

                consignment.Status               = LogisticsCode.Of(ShipmentStatus.Booked);
                consignment.MasterAwb            = command.AwbNumber;
                consignment.CarrierReference     = command.CarrierReference ?? consignment.CarrierReference;
                consignment.BookingFailureReason = null;
                consignment.ModifiedDate         = now;

                await _db.SaveChangesAsync();
                return BookingRunResult.Booked;

            case CarrierCommandStatus.Refused:
                return await FailAsync(consignment, now,
                    $"The carrier refused the booking: {command.Message ?? command.CarrierErrorCode ?? "no reason given"}.");

            case CarrierCommandStatus.NotPerformed:
                return await FailAsync(consignment, now,
                    "Confirmed with the carrier that it was never booked. Request the booking again.");

            case CarrierCommandStatus.Unknown:
                var deduplicates = BookingPolicy.Deduplicates(_registry, command.ProviderKey);

                if (deduplicates && command.AttemptCount < BookingPolicy.MaxAutomaticAttempts)
                {
                    var delay = BookingPolicy.RetryDelayAfter(command.AttemptCount);

                    SetReason(consignment, now,
                        $"{command.FailureDetail} Retrying automatically in {Describe(delay)} " +
                        $"(attempt {command.AttemptCount + 1} of {BookingPolicy.MaxAutomaticAttempts}); the carrier " +
                        "deduplicates, so a retry cannot book twice.");
                    await _db.SaveChangesAsync();

                    _scheduler.ScheduleRetry(consignment.UUID, consignment.OrganizationId, delay);
                    return BookingRunResult.AwaitingRetry;
                }

                return await AwaitResolutionAsync(consignment, now, deduplicates
                    ? $"{command.FailureDetail} Retried {command.AttemptCount} times without an answer. Check with " +
                      "the carrier and resolve the booking."
                    : $"{command.FailureDetail} This carrier does not deduplicate, so it is not retried automatically " +
                      "— a retry could book a second parcel. Check with the carrier and resolve the booking.");

            default:
                return BookingRunResult.InFlight;
        }
    }

    private async Task<BookingRunResult> FailAsync(Consignment consignment, DateTime now, string reason)
    {
        Machine.EnsureCanTransition(StatusOf(consignment), ShipmentStatus.BookingFailed);

        consignment.Status = LogisticsCode.Of(ShipmentStatus.BookingFailed);
        SetReason(consignment, now, reason);

        // A failed booking is retried as a new command under a new key, so the ledger's record of
        // this one stays true. The next request issues the new key.
        consignment.BookingIdempotencyKey = null;

        await _db.SaveChangesAsync();
        return BookingRunResult.Failed;
    }

    private async Task<BookingRunResult> AwaitResolutionAsync(Consignment consignment, DateTime now, string reason)
    {
        // Stays BOOKING. Moving it to BOOKING_FAILED would invite a fresh booking under a new key —
        // the second parcel this whole mechanism exists to prevent.
        SetReason(consignment, now, reason);
        await _db.SaveChangesAsync();
        return BookingRunResult.NeedsResolution;
    }

    // ── Resolution and status ─────────────────────────────────────────────────

    public async Task<ConsignmentBookingStatusModel?> ResolveAsync(
        Guid consignmentUuid, ResolveBookingRequest req, int userId)
    {
        ArgumentNullException.ThrowIfNull(req);

        var consignment = await _db.Consignments
            .Include(c => c.Carrier)
            .Include(c => c.CarrierAccount)
            .FirstOrDefaultAsync(c => c.UUID == consignmentUuid && !c.IsDelete);

        if (consignment is null) return null;

        if (StatusOf(consignment) != ShipmentStatus.Booking || consignment.BookingIdempotencyKey is null)
            throw new ConflictException(
                $"Consignment {consignment.ConsignmentNumber} is {consignment.Status}, not waiting on a booking.");

        var command = await _ledger.FindAsync(CarrierCommandType.Book, consignment.BookingIdempotencyKey, consignment.OrganizationId)
            ?? throw new ConflictException(
                "No call has been sent to the carrier for this booking yet, so there is nothing to resolve. " +
                "It will be sent shortly.");

        var now = DateTime.UtcNow;

        var settled = req.CarrierBooked
            ? await _ledger.ResolveAsPerformedAsync(command.Uuid, req.Awb, req.Note, userId, now)
            : await _ledger.ResolveAsNotPerformedAsync(command.Uuid, req.Note, userId, now);

        consignment.ModifiedBy = userId;
        await ApplyAsync(consignment, settled, now);

        return await StatusModelAsync(consignment);
    }

    public async Task<ConsignmentBookingStatusModel?> GetStatusAsync(Guid consignmentUuid)
    {
        var consignment = await _db.Consignments
            .Include(c => c.Carrier)
            .Include(c => c.CarrierAccount)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.UUID == consignmentUuid && !c.IsDelete);

        return consignment is null ? null : await StatusModelAsync(consignment);
    }

    private async Task<ConsignmentBookingStatusModel> StatusModelAsync(Consignment consignment)
    {
        // The latest booking call, whether or not its key is still current — after a refusal the
        // key is retired, and the refusal is still what the screen needs to show.
        var book = LogisticsCode.Of(CarrierCommandType.Book);

        var command = await _db.CarrierCommands
            .AsNoTracking()
            .Where(c => c.ConsignmentId == consignment.Id && c.CommandType == book)
            .OrderByDescending(c => c.FirstAttemptAt)
            .FirstOrDefaultAsync();

        var awb = consignment.MasterAwb?.Trim();
        var labelStoredAt = awb is null
            ? null
            : await _db.ConsignmentLabels
                .Where(l => l.ConsignmentId == consignment.Id && l.AwbNumber == awb)
                .Select(l => (DateTime?)l.CreatedDate)
                .MaxAsync();

        var booking      = StatusOf(consignment) == ShipmentStatus.Booking;
        var unknown      = command?.Status == LogisticsStatuses.CarrierCommand.Unknown;
        var deduplicates = command is not null && BookingPolicy.Deduplicates(_registry, command.ProviderKey);
        var retryable    = deduplicates && command!.AttemptCount < BookingPolicy.MaxAutomaticAttempts;

        return new ConsignmentBookingStatusModel
        {
            ConsignmentUuid        = consignment.UUID,
            ConsignmentNumber      = consignment.ConsignmentNumber,
            Status                 = consignment.Status,
            MasterAwb              = consignment.MasterAwb,
            TrackingUrl            = ConsignmentRepository.BuildTrackingUrl(consignment.Carrier, consignment.MasterAwb)
                                     ?? (command?.Status == LogisticsStatuses.CarrierCommand.Succeeded ? command.TrackingUrl : null),
            FailureReason          = consignment.BookingFailureReason,
            CarrierAccountUuid     = consignment.CarrierAccount?.UUID,
            CarrierAccountName     = consignment.CarrierAccount?.AccountName,
            IsSandbox              = consignment.CarrierAccount?.IsSandbox ?? false,
            CommandStatus          = command?.Status,
            AttemptCount           = command?.AttemptCount ?? 0,
            FirstAttemptAt         = command?.FirstAttemptAt,
            LastAttemptAt          = command?.LastAttemptAt,
            HasStoredLabel         = labelStoredAt is not null,
            LabelStoredAt          = labelStoredAt,
            NeedsResolution        = booking && unknown && !retryable,
            WillRetryAutomatically = booking && unknown && retryable
        };
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────

    private static IQueryable<Consignment> WithBookingGraph(IQueryable<Consignment> query) => query
        .Include(c => c.Carrier)
        .Include(c => c.CarrierAccount)
        .Include(c => c.ShipFromAddress)
        .Include(c => c.ShipToAddress)
        .Include(c => c.Deliveries).ThenInclude(cd => cd.DeliveryOrder).ThenInclude(d => d.ShipFromAddress)
        .Include(c => c.Deliveries).ThenInclude(cd => cd.DeliveryOrder).ThenInclude(d => d.ShipToAddress)
        .Include(c => c.Deliveries).ThenInclude(cd => cd.DeliveryOrder).ThenInclude(d => d.Packages)
        .AsSplitQuery();

    private static ShipmentStatus StatusOf(Consignment consignment) =>
        LogisticsCode.Parse<ShipmentStatus>(consignment.Status);

    private static void SetReason(Consignment consignment, DateTime now, string reason)
    {
        var trimmed = reason.Trim();
        consignment.BookingFailureReason = trimmed.Length <= FailureReasonMax ? trimmed : trimmed[..FailureReasonMax];
        consignment.ModifiedDate         = now;
    }

    private static string Describe(TimeSpan delay) =>
        delay.TotalMinutes >= 60 ? $"{delay.TotalHours:0} hour(s)" : $"{delay.TotalMinutes:0} minute(s)";

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
