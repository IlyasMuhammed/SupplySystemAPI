using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Constants;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Data.Maps;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Domain.StateMachines;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Couriers;

/// <summary>What the caller intends to send, presented before it is sent.</summary>
/// <param name="RequestFingerprint">From <see cref="CourierRequestFingerprint"/>.</param>
/// <param name="CarrierAccountId">Null for a manual carrier.</param>
internal sealed record CarrierCommandClaim(
    CarrierCommandType Type,
    string             IdempotencyKey,
    string             RequestFingerprint,
    int                ConsignmentId,
    int?               CarrierAccountId,
    string             ProviderKey,
    int                UserId);

internal enum LedgerDecisionKind
{
    /// <summary>The claim is yours. Make the call, then record what came back.</summary>
    Proceed,

    /// <summary>It already happened. Use the stored result; do not call the carrier.</summary>
    AlreadySucceeded,

    /// <summary>The carrier already said no to this exact request. Change something and use a new key.</summary>
    AlreadyRefused,

    /// <summary>Another worker holds the claim right now. Wait; do not call.</summary>
    InFlight,

    /// <summary>
    /// The outcome is unknown and this carrier does not deduplicate, so a retry could create a second
    /// real parcel. A person must check with the carrier and resolve it.
    /// </summary>
    NeedsResolution,

    /// <summary>A person confirmed the carrier never acted on this key. Start again under a new one.</summary>
    KeyRetired
}

/// <param name="Explanation">Why, in words a user or a log reader can act on. Null for a fresh claim.</param>
internal sealed record LedgerDecision(
    LedgerDecisionKind     Kind,
    CarrierCommandSnapshot Command,
    string?                Explanation)
{
    internal bool ShouldCallCarrier => Kind == LedgerDecisionKind.Proceed;

    /// <summary>True when the claim reopened a call whose first attempt never resolved.</summary>
    internal bool IsRetry => ShouldCallCarrier && Command.AttemptCount > 1;
}

/// <summary>A ledger row as callers see it — read-only, and never tracked.</summary>
internal sealed record CarrierCommandSnapshot(
    Guid                 Uuid,
    CarrierCommandType   Type,
    string               IdempotencyKey,
    CarrierCommandStatus Status,
    int                  ConsignmentId,
    int?                 CarrierAccountId,
    string               ProviderKey,
    int                  AttemptCount,
    DateTime             FirstAttemptAt,
    DateTime             LastAttemptAt,
    DateTime?            LeaseExpiresAt,
    DateTime?            CompletedAt,
    string?              AwbNumber,
    string?              CarrierReference,
    string?              TrackingUrl,
    decimal?             Cost,
    string?              CostCurrency,
    string?              CarrierErrorCode,
    string?              Message,
    string?              FailureDetail,
    int?                 ResolvedBy,
    DateTime?            ResolvedAt,
    string?              ResolutionNote)
{
    internal bool WasResolvedByPerson => ResolvedAt is not null;
}

internal interface ICarrierCommandLedger
{
    /// <summary>
    /// Asks, before a carrier call, whether it may go out. <b>Call the carrier only when the
    /// decision says <see cref="LedgerDecision.ShouldCallCarrier"/>.</b>
    /// </summary>
    /// <param name="organizationId">
    /// Whose ledger. Defaults to the ambient tenant; pass it explicitly from a background job,
    /// where the ambient tenant is not the organization being worked on.
    /// </param>
    Task<LedgerDecision> BeginAsync(
        CarrierCommandClaim claim, Guid? organizationId = null, DateTime? utcNow = null,
        CancellationToken ct = default);

    Task<CarrierCommandSnapshot> RecordBookingResultAsync(
        Guid commandUuid, CourierBookingResult result, DateTime? utcNow = null, CancellationToken ct = default);

    Task<CarrierCommandSnapshot> RecordCancelResultAsync(
        Guid commandUuid, CourierCancelResult result, DateTime? utcNow = null, CancellationToken ct = default);

    /// <summary>
    /// The adapter threw. Per the contract that means the call came apart, so the outcome is
    /// recorded as unknown — never as "it did not happen".
    /// </summary>
    Task<CarrierCommandSnapshot> RecordExceptionAsync(
        Guid commandUuid, Exception exception, DateTime? utcNow = null, CancellationToken ct = default);

    /// <summary>A person checked with the carrier: it did act. A booking needs the airway bill they were given.</summary>
    Task<CarrierCommandSnapshot> ResolveAsPerformedAsync(
        Guid commandUuid, string? awbNumber, string note, int userId, DateTime? utcNow = null,
        CancellationToken ct = default);

    /// <summary>A person checked with the carrier: it has no record of the call.</summary>
    Task<CarrierCommandSnapshot> ResolveAsNotPerformedAsync(
        Guid commandUuid, string note, int userId, DateTime? utcNow = null, CancellationToken ct = default);

    /// <summary>
    /// Moves every in-flight call whose lease has run out to UNKNOWN, across all organizations.
    /// For a sweep job; returns how many moved.
    /// </summary>
    Task<int> ExpireStaleLeasesAsync(DateTime? utcNow = null, CancellationToken ct = default);

    Task<CarrierCommandSnapshot?> FindAsync(
        CarrierCommandType type, string idempotencyKey, Guid? organizationId = null, CancellationToken ct = default);

    /// <summary>The exception queue: calls whose outcome is unknown, oldest first.</summary>
    Task<IReadOnlyList<CarrierCommandSnapshot>> ListUnresolvedAsync(
        Guid? organizationId = null, CancellationToken ct = default);
}

/// <summary>
/// The command ledger a retry consults instead of booking a second real parcel.
/// <para>
/// <b>The protocol.</b> Before a carrier call, <see cref="BeginAsync"/> writes a row — or finds the
/// one already there. After it, the result is recorded against that row. A retry presents the same
/// key and gets the stored answer instead of a second call.
/// </para>
/// <para>
/// <b>The one hard case is the unresolved call.</b> A timeout may have created a real parcel. The
/// ledger retries it automatically only when the carrier itself deduplicates on our key
/// (<see cref="CourierCapabilities.HonoursIdempotencyKey"/>), because then a retry returns the
/// original booking instead of making another. For every other carrier it stops and asks for a
/// person — a phone call to the carrier is cheap next to a second shipment, label and invoice.
/// </para>
/// <para>
/// <b>What is enforced by the database, not by this code:</b> one row per (organization, command,
/// key), through a unique index; and one winner per retry, through <c>RowVersion</c>. Two workers
/// racing on one key therefore cannot both be told to proceed — the loser rereads and is told the
/// call is in flight.
/// </para>
/// </summary>
internal sealed class CarrierCommandLedger : ICarrierCommandLedger
{
    /// <summary>
    /// How long a worker holds a claim before its silence is read as "the outcome is unknown".
    /// Must outlast the longest carrier call the booking job allows (T-37 sets its HTTP timeout
    /// below this); too short, and a slow but healthy call is declared unknown while it is still
    /// running.
    /// </summary>
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    /// <summary>Bounded, as in <c>DocumentNumberGenerator</c>: contention is per key, so this is plenty.</summary>
    private const int MaxAttempts = 10;

    private static readonly CarrierCommandStateMachine Machine = CarrierCommandStateMachine.Instance;

    private readonly LogisticsDbContext       _db;
    private readonly ITenantContext           _tenant;
    private readonly ICourierProviderRegistry _registry;

    public CarrierCommandLedger(LogisticsDbContext db, ITenantContext tenant, ICourierProviderRegistry registry)
    {
        _db       = db;
        _tenant   = tenant;
        _registry = registry;
    }

    // ── Claiming ──────────────────────────────────────────────────────────────

    public async Task<LedgerDecision> BeginAsync(
        CarrierCommandClaim claim, Guid? organizationId = null, DateTime? utcNow = null,
        CancellationToken ct = default)
    {
        Validate(claim);

        var owner = organizationId ?? _tenant.OrganizationId;
        var type  = LogisticsCode.Of(claim.Type);
        var key   = claim.IdempotencyKey.Trim();

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var now = utcNow ?? DateTime.UtcNow;

            try
            {
                // Explicit organization predicate with the filter off, as DocumentNumberGenerator
                // does: the ambient filter bypasses for background jobs, and a booking job must
                // never find — and reuse — another organization's key.
                var existing = await _db.CarrierCommands
                    .IgnoreQueryFilters()
                    .FirstOrDefaultAsync(c => c.OrganizationId == owner
                                           && c.CommandType    == type
                                           && c.IdempotencyKey == key, ct);

                return existing is null
                    ? await ClaimNewAsync(claim, owner, type, key, now, ct)
                    : await DecideAsync(existing, claim, now, ct);
            }
            catch (DbUpdateException) when (attempt < MaxAttempts)
            {
                // Either another worker inserted this key first (unique index), or changed the row
                // between our read and our write (RowVersion). Both mean: forget what we read and
                // look again — we will now see what the winner did.
                DetachCommands();
            }
        }

        throw new InvalidOperationException(
            $"Could not claim carrier command {type} '{key}' after {MaxAttempts} attempts. The ledger " +
            "row is under sustained contention or failing to save.");
    }

    private async Task<LedgerDecision> ClaimNewAsync(
        CarrierCommandClaim claim, Guid owner, string type, string key, DateTime now, CancellationToken ct)
    {
        var command = new CarrierCommand
        {
            UUID               = Guid.NewGuid(),
            OrganizationId     = owner,
            CommandType        = type,
            IdempotencyKey     = key,
            RequestFingerprint = claim.RequestFingerprint,
            ConsignmentId      = claim.ConsignmentId,
            CarrierAccountId   = claim.CarrierAccountId,
            ProviderKey        = claim.ProviderKey.Trim(),
            Status             = LogisticsStatuses.CarrierCommand.InFlight,
            AttemptCount       = 1,
            FirstAttemptAt     = now,
            LastAttemptAt      = now,
            LeaseExpiresAt     = now + LeaseDuration,
            CreatedBy          = claim.UserId,
            CreatedDate        = now
        };

        _db.CarrierCommands.Add(command);
        await _db.SaveChangesAsync(ct);

        return new LedgerDecision(LedgerDecisionKind.Proceed, Snapshot(command), null);
    }

    private async Task<LedgerDecision> DecideAsync(
        CarrierCommand command, CarrierCommandClaim claim, DateTime now, CancellationToken ct)
    {
        // A key is a promise about one request. Reused for a changed one, a carrier that
        // deduplicates would hand back the original parcel for the new booking — the wrong address
        // on the label, and nothing anywhere saying so.
        if (!string.Equals(command.RequestFingerprint, claim.RequestFingerprint, StringComparison.Ordinal))
            throw new ConflictException(
                $"Idempotency key '{command.IdempotencyKey}' was already used for a different " +
                $"{command.CommandType} request. A changed request needs a new key.");

        // A retry through a different adapter is not a retry at all: the other carrier has never
        // seen the key and would book a second parcel without hesitation.
        if (!string.Equals(command.ProviderKey, claim.ProviderKey.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new ConflictException(
                $"Idempotency key '{command.IdempotencyKey}' was first sent through provider " +
                $"'{command.ProviderKey}', not '{claim.ProviderKey.Trim()}'. The carrier configuration " +
                "changed underneath an unresolved call; resolve that call before booking elsewhere.");

        var status = StatusOf(command);

        if (status == CarrierCommandStatus.InFlight)
        {
            if (!LeaseExpired(command, now))
                return new LedgerDecision(LedgerDecisionKind.InFlight, Snapshot(command),
                    $"This call is already with the carrier (attempt {command.AttemptCount}, claim held " +
                    $"until {command.LeaseExpiresAt:u}). Wait for it rather than sending another.");

            // The worker went quiet. Whether the carrier acted is now genuinely unknown, so say so
            // on the row before deciding anything else.
            MarkLeaseExpired(command, now);
            status = CarrierCommandStatus.Unknown;
        }

        switch (status)
        {
            case CarrierCommandStatus.Succeeded:
                return new LedgerDecision(LedgerDecisionKind.AlreadySucceeded, Snapshot(command),
                    command.CommandType == LogisticsCode.Of(CarrierCommandType.Book)
                        ? $"Already booked, airway bill {command.AwbNumber}. Not calling the carrier again."
                        : "Already done. Not calling the carrier again.");

            case CarrierCommandStatus.Refused:
                return new LedgerDecision(LedgerDecisionKind.AlreadyRefused, Snapshot(command),
                    $"The carrier already refused this exact request: {command.Message ?? command.CarrierErrorCode ?? "no reason given"}. " +
                    "Sending it again earns the same answer; change the request and use a new key.");

            case CarrierCommandStatus.NotPerformed:
                return new LedgerDecision(LedgerDecisionKind.KeyRetired, Snapshot(command),
                    "A person confirmed the carrier never acted on this key. Start again under a new key, " +
                    "so the record of this attempt stays true.");
        }

        // UNKNOWN. The only question is whether sending it again is safe.
        var provider = _registry.Find(command.ProviderKey);

        if (provider?.Capabilities.HonoursIdempotencyKey != true)
        {
            // Persists a lease expiry noticed just now, so the exception queue sees it too.
            // A no-op when nothing changed.
            await _db.SaveChangesAsync(ct);

            return new LedgerDecision(LedgerDecisionKind.NeedsResolution, Snapshot(command),
                provider is null
                    ? $"The outcome is unknown and provider '{command.ProviderKey}' is no longer registered, " +
                      "so nothing can say whether a retry is safe. Check with the carrier and resolve it."
                    : $"The outcome is unknown, and {provider.DisplayName} does not deduplicate on our key — " +
                      "a retry could create a second real parcel. Check with the carrier and resolve it.");
        }

        Machine.EnsureCanTransition(CarrierCommandStatus.Unknown, CarrierCommandStatus.InFlight);

        command.Status         = LogisticsStatuses.CarrierCommand.InFlight;
        command.AttemptCount  += 1;
        command.LastAttemptAt  = now;
        command.LeaseExpiresAt = now + LeaseDuration;
        command.FailureDetail  = null;
        command.ModifiedBy     = claim.UserId;
        command.ModifiedDate   = now;

        await _db.SaveChangesAsync(ct);

        return new LedgerDecision(LedgerDecisionKind.Proceed, Snapshot(command),
            $"Retrying an unresolved call (attempt {command.AttemptCount}). The carrier deduplicates on " +
            "the key, so if the first attempt did book, this returns that booking rather than a new one.");
    }

    // ── Recording ─────────────────────────────────────────────────────────────

    public Task<CarrierCommandSnapshot> RecordBookingResultAsync(
        Guid commandUuid, CourierBookingResult result, DateTime? utcNow = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        return RecordAsync(commandUuid, utcNow, ct, (command, now) =>
        {
            RequireType(command, CarrierCommandType.Book);

            var awb = Clip(result.AwbNumber, CarrierCommandMap.AwbNumberMax);

            // A success with no airway bill breaks the contract and gives us nothing to track or
            // to prove the booking with. It is not a success we can act on, so it is not recorded
            // as one — but the carrier may well have booked, so it is not a failure either.
            var incoming = result.Outcome switch
            {
                CourierOutcome.Succeeded when awb is null => CarrierCommandStatus.Unknown,
                CourierOutcome.Succeeded                  => CarrierCommandStatus.Succeeded,
                CourierOutcome.Refused or
                CourierOutcome.Unsupported                => CarrierCommandStatus.Refused,
                _                                         => CarrierCommandStatus.Unknown
            };

            var failure = result.Outcome switch
            {
                CourierOutcome.Succeeded when awb is null =>
                    "The carrier reported success without an airway bill, so the booking cannot be confirmed.",
                CourierOutcome.Failed =>
                    "The carrier call did not resolve. Whether a parcel was booked is unknown.",
                _ => null
            };

            Apply(command, now, incoming, failure, awb, result.CarrierReference, result.TrackingUrl,
                  result.Cost, result.CostCurrency, result.CarrierErrorCode, result.Message, result.RawResponse);
        });
    }

    public Task<CarrierCommandSnapshot> RecordCancelResultAsync(
        Guid commandUuid, CourierCancelResult result, DateTime? utcNow = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        return RecordAsync(commandUuid, utcNow, ct, (command, now) =>
        {
            RequireType(command, CarrierCommandType.Cancel);

            var incoming = result.Outcome switch
            {
                CourierOutcome.Succeeded                  => CarrierCommandStatus.Succeeded,
                CourierOutcome.Refused or
                CourierOutcome.Unsupported                => CarrierCommandStatus.Refused,
                _                                         => CarrierCommandStatus.Unknown
            };

            Apply(command, now, incoming,
                  result.Outcome == CourierOutcome.Failed
                      ? "The cancellation call did not resolve. Whether the carrier cancelled is unknown."
                      : null,
                  awb: null, reference: null, trackingUrl: null, cost: null, costCurrency: null,
                  errorCode: null, message: result.Message, raw: null);
        });
    }

    public Task<CarrierCommandSnapshot> RecordExceptionAsync(
        Guid commandUuid, Exception exception, DateTime? utcNow = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return RecordAsync(commandUuid, utcNow, ct, (command, now) =>
        {
            // An exception never overwrites an answer. If a result already landed — a late
            // success from this very call, or a retry's — that is the truth.
            if (StatusOf(command) is not (CarrierCommandStatus.InFlight or CarrierCommandStatus.Unknown))
                return;

            var detail = $"The carrier call threw {exception.GetType().Name}: {exception.Message} " +
                         "Whether the carrier acted is unknown.";

            if (StatusOf(command) == CarrierCommandStatus.InFlight)
            {
                Machine.EnsureCanTransition(CarrierCommandStatus.InFlight, CarrierCommandStatus.Unknown);
                command.Status         = LogisticsStatuses.CarrierCommand.Unknown;
                command.LeaseExpiresAt = null;
            }

            command.FailureDetail = Clip(detail, CarrierCommandMap.FailureDetailMax);
            command.ModifiedDate  = now;
        });
    }

    /// <summary>
    /// Folds one result into a row, whatever state the row reached in the meantime.
    /// <para>
    /// Results arrive late — a call outlives its lease, and by the time it answers the row is
    /// UNKNOWN, or a retry has already succeeded. The rules: a real answer settles an unknown; an
    /// answer never overwrites another answer; and two answers that <em>disagree</em> are the
    /// signature of a duplicate booking, so the second one is kept as evidence and raised rather
    /// than silently dropped.
    /// </para>
    /// </summary>
    private static void Apply(
        CarrierCommand command, DateTime now, CarrierCommandStatus incoming, string? failure,
        string? awb, string? reference, string? trackingUrl, decimal? cost, string? costCurrency,
        string? errorCode, string? message, string? raw)
    {
        var current = StatusOf(command);

        switch (current)
        {
            case CarrierCommandStatus.InFlight:
            case CarrierCommandStatus.Unknown:
                if (incoming == CarrierCommandStatus.Unknown && current == CarrierCommandStatus.Unknown)
                {
                    // Still unknown — keep the newest detail, change nothing else.
                    command.FailureDetail = Clip(failure, CarrierCommandMap.FailureDetailMax) ?? command.FailureDetail;
                    Fill(command, awb, reference, trackingUrl, cost, costCurrency, errorCode, message, raw);
                    command.ModifiedDate = now;
                    return;
                }

                Machine.EnsureCanTransition(current, incoming);

                command.Status         = LogisticsCode.Of(incoming);
                command.FailureDetail  = Clip(failure, CarrierCommandMap.FailureDetailMax);
                command.LeaseExpiresAt = null;
                command.CompletedAt    = incoming == CarrierCommandStatus.Unknown ? null : now;
                command.ModifiedDate   = now;
                Fill(command, awb, reference, trackingUrl, cost, costCurrency, errorCode, message, raw);
                return;

            case CarrierCommandStatus.Succeeded when incoming == CarrierCommandStatus.Succeeded:
                // The same booking reported twice — expected when a deduplicating carrier answers
                // both the original call and its retry. Anything else is a second parcel.
                if (awb is null || SameAwb(command.AwbNumber, awb)) return;

                RaiseContradiction(command, now,
                    $"Recorded airway bill {command.AwbNumber}, but the carrier has now also reported {awb}. " +
                    "This may be a duplicate booking: two parcels, two labels, two invoices. Check with the " +
                    "carrier and cancel one.");
                return;

            case CarrierCommandStatus.Refused or CarrierCommandStatus.NotPerformed
                when incoming == CarrierCommandStatus.Succeeded:
                RaiseContradiction(command, now,
                    $"This call was recorded as {command.Status}, but the carrier has now reported it " +
                    $"succeeded{(awb is null ? "" : $" with airway bill {awb}")}. A parcel may exist that " +
                    "nothing here points at. Check with the carrier.");
                return;

            default:
                // A late refusal or failure after an answer is already recorded changes nothing.
                return;
        }
    }

    private static void RaiseContradiction(CarrierCommand command, DateTime now, string evidence)
    {
        // Kept on the row before raising, so the evidence survives even if nobody reads the error.
        command.FailureDetail = Clip(
            string.IsNullOrEmpty(command.FailureDetail) ? evidence : $"{command.FailureDetail} | {evidence}",
            CarrierCommandMap.FailureDetailMax);
        command.ModifiedDate = now;

        throw new CarrierCommandContradictionException(evidence);
    }

    private static void Fill(
        CarrierCommand command, string? awb, string? reference, string? trackingUrl, decimal? cost,
        string? costCurrency, string? errorCode, string? message, string? raw)
    {
        command.AwbNumber        = Clip(awb, CarrierCommandMap.AwbNumberMax)                 ?? command.AwbNumber;
        command.CarrierReference = Clip(reference, CarrierCommandMap.CarrierReferenceMax)    ?? command.CarrierReference;
        command.TrackingUrl      = Clip(trackingUrl, CarrierCommandMap.TrackingUrlMax)       ?? command.TrackingUrl;
        command.Cost             = cost                                                      ?? command.Cost;
        command.CostCurrency     = Clip(costCurrency, 3)?.ToUpperInvariant()                 ?? command.CostCurrency;
        command.CarrierErrorCode = Clip(errorCode, CarrierCommandMap.CarrierErrorCodeMax)    ?? command.CarrierErrorCode;
        command.Message          = Clip(message, CarrierCommandMap.MessageMax)               ?? command.Message;
        command.RawResponse      = ClipRaw(raw)                                              ?? command.RawResponse;
    }

    // ── Human resolution ──────────────────────────────────────────────────────

    public Task<CarrierCommandSnapshot> ResolveAsPerformedAsync(
        Guid commandUuid, string? awbNumber, string note, int userId, DateTime? utcNow = null,
        CancellationToken ct = default) =>
        ResolveAsync(commandUuid, note, userId, utcNow, ct, CarrierCommandStatus.Succeeded, command =>
        {
            if (command.CommandType != LogisticsCode.Of(CarrierCommandType.Book)) return;

            var awb = Clip(awbNumber, CarrierCommandMap.AwbNumberMax)
                ?? throw new BadRequestException(
                    "Confirming a booking needs the airway bill the carrier gave you — without it there " +
                    "is nothing to track and nothing to prove the booking with.");

            command.AwbNumber = awb;
        });

    public Task<CarrierCommandSnapshot> ResolveAsNotPerformedAsync(
        Guid commandUuid, string note, int userId, DateTime? utcNow = null, CancellationToken ct = default) =>
        ResolveAsync(commandUuid, note, userId, utcNow, ct, CarrierCommandStatus.NotPerformed, _ => { });

    private async Task<CarrierCommandSnapshot> ResolveAsync(
        Guid commandUuid, string note, int userId, DateTime? utcNow, CancellationToken ct,
        CarrierCommandStatus outcome, Action<CarrierCommand> applyOutcome)
    {
        // Required, because "resolved" alone does not say who at the carrier was asked or what
        // they said — and that is exactly what the next person to question it will need.
        var trimmedNote = Clip(note, CarrierCommandMap.ResolutionNoteMax)
            ?? throw new BadRequestException(
                "Say how this was confirmed — who at the carrier, or what their portal shows.");

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var now = utcNow ?? DateTime.UtcNow;

            // Filtered, unlike the worker paths: this is a person acting through a request, and
            // they must not be able to settle another organization's call.
            var command = await _db.CarrierCommands.FirstOrDefaultAsync(c => c.UUID == commandUuid, ct)
                ?? throw new NotFoundException("Carrier command", commandUuid);

            var status = StatusOf(command);

            if (status == CarrierCommandStatus.InFlight)
            {
                if (!LeaseExpired(command, now))
                    throw new ConflictException(
                        $"This call is still with the carrier until {command.LeaseExpiresAt:u}. Resolving it now " +
                        "could contradict the answer that is about to arrive.");

                MarkLeaseExpired(command, now);
                status = CarrierCommandStatus.Unknown;
            }

            if (status != CarrierCommandStatus.Unknown)
                throw new ConflictException(
                    $"Only a call whose outcome is unknown can be resolved by hand. This one is already " +
                    $"{command.Status}.");

            Machine.EnsureCanTransition(CarrierCommandStatus.Unknown, outcome);
            applyOutcome(command);

            command.Status         = LogisticsCode.Of(outcome);
            command.LeaseExpiresAt = null;
            command.CompletedAt    = now;
            command.ResolvedBy     = userId;
            command.ResolvedAt     = now;
            command.ResolutionNote = trimmedNote;
            command.ModifiedBy     = userId;
            command.ModifiedDate   = now;

            try
            {
                await _db.SaveChangesAsync(ct);
                return Snapshot(command);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
            {
                // A result or another person got there first. Reread and let the status speak.
                DetachCommands();
            }
        }

        throw new InvalidOperationException($"Could not resolve carrier command {commandUuid}.");
    }

    // ── Sweeping and reading ──────────────────────────────────────────────────

    public async Task<int> ExpireStaleLeasesAsync(DateTime? utcNow = null, CancellationToken ct = default)
    {
        var now = utcNow ?? DateTime.UtcNow;

        var stale = await _db.CarrierCommands
            .IgnoreQueryFilters()
            .Where(c => c.Status == LogisticsStatuses.CarrierCommand.InFlight && c.LeaseExpiresAt < now)
            .ToListAsync(ct);

        var moved = 0;

        foreach (var command in stale)
        {
            MarkLeaseExpired(command, now);

            try
            {
                await _db.SaveChangesAsync(ct);
                moved++;
            }
            catch (DbUpdateConcurrencyException)
            {
                // It answered, or was retried, between our read and our write. Whatever it became
                // is newer than our opinion that it went quiet.
                _db.Entry(command).State = EntityState.Detached;
            }
        }

        return moved;
    }

    public async Task<CarrierCommandSnapshot?> FindAsync(
        CarrierCommandType type, string idempotencyKey, Guid? organizationId = null, CancellationToken ct = default)
    {
        var owner = organizationId ?? _tenant.OrganizationId;
        var code  = LogisticsCode.Of(type);
        var key   = (idempotencyKey ?? string.Empty).Trim();

        var command = await _db.CarrierCommands
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.OrganizationId == owner && c.CommandType == code && c.IdempotencyKey == key, ct);

        return command is null ? null : Snapshot(command);
    }

    public async Task<IReadOnlyList<CarrierCommandSnapshot>> ListUnresolvedAsync(
        Guid? organizationId = null, CancellationToken ct = default)
    {
        var owner = organizationId ?? _tenant.OrganizationId;

        var rows = await _db.CarrierCommands
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c => c.OrganizationId == owner && c.Status == LogisticsStatuses.CarrierCommand.Unknown)
            .OrderBy(c => c.FirstAttemptAt)
            .ToListAsync(ct);

        return [.. rows.Select(Snapshot)];
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────

    private async Task<CarrierCommandSnapshot> RecordAsync(
        Guid commandUuid, DateTime? utcNow, CancellationToken ct, Action<CarrierCommand, DateTime> apply)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var now = utcNow ?? DateTime.UtcNow;

            // Unfiltered: the worker recording a result holds the row's own id from its claim, and
            // may be running with no tenant at all.
            var command = await _db.CarrierCommands
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(c => c.UUID == commandUuid, ct)
                ?? throw new NotFoundException("Carrier command", commandUuid);

            try
            {
                apply(command, now);
            }
            catch (CarrierCommandContradictionException)
            {
                // Persist the evidence first; then raise.
                await _db.SaveChangesAsync(ct);
                throw;
            }

            try
            {
                await _db.SaveChangesAsync(ct);
                return Snapshot(command);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
            {
                // Another result, a retry or the sweep moved the row. Re-apply against what it is now.
                DetachCommands();
            }
        }

        throw new InvalidOperationException($"Could not record the result of carrier command {commandUuid}.");
    }

    private static void Validate(CarrierCommandClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);

        if (string.IsNullOrWhiteSpace(claim.IdempotencyKey))
            throw new BadRequestException("A carrier command needs an idempotency key.");
        if (claim.IdempotencyKey.Trim().Length > 100)
            throw new BadRequestException("An idempotency key can be at most 100 characters.");
        if (claim.RequestFingerprint is not { Length: 64 } fp || !fp.All(Uri.IsHexDigit))
            throw new BadRequestException("A carrier command needs a request fingerprint — see CourierRequestFingerprint.");
        if (string.IsNullOrWhiteSpace(claim.ProviderKey))
            throw new BadRequestException("A carrier command needs the provider it is sent through.");
        if (claim.ConsignmentId <= 0)
            throw new BadRequestException("A carrier command belongs to a consignment.");
    }

    private static void RequireType(CarrierCommand command, CarrierCommandType expected)
    {
        if (command.CommandType != LogisticsCode.Of(expected))
            throw new ConflictException(
                $"Carrier command {command.UUID} is a {command.CommandType}, not a {LogisticsCode.Of(expected)}.");
    }

    private static void MarkLeaseExpired(CarrierCommand command, DateTime now)
    {
        Machine.EnsureCanTransition(CarrierCommandStatus.InFlight, CarrierCommandStatus.Unknown);

        command.Status        = LogisticsStatuses.CarrierCommand.Unknown;
        command.FailureDetail = Clip(
            $"No result was recorded before the claim expired at {command.LeaseExpiresAt:u} — the worker " +
            "may have stopped mid-call. Whether the carrier acted is unknown.",
            CarrierCommandMap.FailureDetailMax);
        command.LeaseExpiresAt = null;
        command.ModifiedDate   = now;
    }

    private static bool LeaseExpired(CarrierCommand command, DateTime now) =>
        command.LeaseExpiresAt is null || command.LeaseExpiresAt <= now;

    private static CarrierCommandStatus StatusOf(CarrierCommand command) =>
        LogisticsCode.Parse<CarrierCommandStatus>(command.Status);

    private static bool SameAwb(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    // Carrier-supplied text is clipped to its column. A save that fails on an over-long message
    // straight after a real booking would lose the only record that the booking happened.
    private static string? Clip(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private static string? ClipRaw(string? raw) =>
        string.IsNullOrEmpty(raw) ? null
        : raw.Length <= CarrierCommandMap.RawResponseMax ? raw
        : raw[..CarrierCommandMap.RawResponseMax];

    private void DetachCommands()
    {
        foreach (var entry in _db.ChangeTracker.Entries<CarrierCommand>().ToList())
            entry.State = EntityState.Detached;
    }

    private static CarrierCommandSnapshot Snapshot(CarrierCommand c) => new(
        c.UUID,
        LogisticsCode.Parse<CarrierCommandType>(c.CommandType),
        c.IdempotencyKey,
        StatusOf(c),
        c.ConsignmentId,
        c.CarrierAccountId,
        c.ProviderKey,
        c.AttemptCount,
        c.FirstAttemptAt,
        c.LastAttemptAt,
        c.LeaseExpiresAt,
        c.CompletedAt,
        c.AwbNumber,
        c.CarrierReference,
        c.TrackingUrl,
        c.Cost,
        c.CostCurrency,
        c.CarrierErrorCode,
        c.Message,
        c.FailureDetail,
        c.ResolvedBy,
        c.ResolvedAt,
        c.ResolutionNote);
}

/// <summary>
/// Two answers from a carrier that cannot both be true — most often, a second airway bill for one
/// booking. Raised after the evidence is saved on the ledger row, so it is never lost.
/// </summary>
internal sealed class CarrierCommandContradictionException(string message) : ConflictException(message);
