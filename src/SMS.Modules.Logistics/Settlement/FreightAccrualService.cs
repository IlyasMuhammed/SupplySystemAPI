using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Logistics.Settlement;

/// <summary>
/// What is owed to carriers for movements that have already happened.
/// <para>
/// A consignment the carrier has collected is a liability whether or not an invoice has arrived,
/// and carriers commonly bill weeks later. Without this, freight cost lands in the month the
/// invoice is keyed rather than the month the goods moved.
/// </para>
/// </summary>
public interface IFreightAccrualService
{
    /// <summary>The accrual for one consignment, or null when there is none.</summary>
    Task<FreightAccrualModel?> GetAsync(Guid consignmentUuid, CancellationToken ct = default);

    /// <summary>
    /// Accrues one consignment. Idempotent: a consignment already accrued is returned unchanged
    /// rather than accrued a second time.
    /// </summary>
    Task<FreightAccrualModel?> AccrueAsync(Guid consignmentUuid, int userId, CancellationToken ct = default);

    /// <summary>
    /// Accrues everything that has moved and has not been accrued, and reverses accruals whose
    /// consignment has since been cancelled. What a nightly job runs.
    /// </summary>
    Task<FreightAccrualSweepResult> SweepAsync(int userId, CancellationToken ct = default);

    /// <summary>What is still owed, by carrier and currency, as at a date.</summary>
    Task<FreightAccrualSummaryModel> GetSummaryAsync(DateTime? asOf = null, CancellationToken ct = default);

    /// <summary>Writes an accrual back without an invoice. The reason is required.</summary>
    Task<bool> ReverseAsync(Guid consignmentUuid, ReverseAccrualRequest req, int userId,
                            CancellationToken ct = default);
}

/// <param name="CouldNotAccrue">Dispatched, never priced — finding F44.</param>
public sealed record FreightAccrualSweepResult(
    int Accrued,
    int Reversed,
    IReadOnlyList<UnaccruableConsignmentModel> CouldNotAccrue);

internal sealed class FreightAccrualService : IFreightAccrualService
{
    /// <summary>
    /// The statuses that mean the carrier has taken the goods at some point. Everything from
    /// pick-up onward, including the end states — a delivered consignment was collected, and a
    /// lost one was collected and then lost, and both are billable.
    /// </summary>
    private static readonly string[] Dispatched =
    [
        LogisticsCode.Of(ShipmentStatus.PickedUp),
        LogisticsCode.Of(ShipmentStatus.InTransit),
        LogisticsCode.Of(ShipmentStatus.OutForDelivery),
        LogisticsCode.Of(ShipmentStatus.DeliveryAttempted),
        LogisticsCode.Of(ShipmentStatus.Exception),
        LogisticsCode.Of(ShipmentStatus.Delivered),
        LogisticsCode.Of(ShipmentStatus.ReturnedToOrigin),
        LogisticsCode.Of(ShipmentStatus.Lost)
    ];

    private static readonly string Cancelled = LogisticsCode.Of(ShipmentStatus.Cancelled);
    private static readonly string Open      = LogisticsCode.Of(FreightAccrualStatus.Accrued);

    private readonly LogisticsDbContext _db;
    public FreightAccrualService(LogisticsDbContext db) => _db = db;

    // ── Reading ───────────────────────────────────────────────────────────────

    public async Task<FreightAccrualModel?> GetAsync(Guid consignmentUuid, CancellationToken ct = default)
    {
        var accrual = await _db.FreightAccruals.AsNoTracking()
            .Include(a => a.Consignment)
            .Include(a => a.Carrier)
            .FirstOrDefaultAsync(a => a.Consignment.UUID == consignmentUuid && !a.IsDelete, ct);

        return accrual is null ? null : ToModel(accrual);
    }

    // ── Accruing ──────────────────────────────────────────────────────────────

    public async Task<FreightAccrualModel?> AccrueAsync(
        Guid consignmentUuid, int userId, CancellationToken ct = default)
    {
        var consignment = await _db.Consignments
            .Include(c => c.Carrier)
            .FirstOrDefaultAsync(c => c.UUID == consignmentUuid && !c.IsDelete, ct);

        if (consignment is null) return null;

        var existing = await _db.FreightAccruals
            .Include(a => a.Consignment)
            .Include(a => a.Carrier)
            .FirstOrDefaultAsync(a => a.ConsignmentId == consignment.Id && !a.IsDelete, ct);

        // Idempotent by design: this runs from a nightly sweep as well as by hand, and accruing a
        // second time would double a liability in a way that looks entirely legitimate.
        if (existing is not null) return ToModel(existing);

        if (!Dispatched.Contains(consignment.Status))
            throw new ConflictException(
                $"Consignment {consignment.ConsignmentNumber} is {consignment.Status}. There is "
              + "nothing to accrue until the carrier has taken the goods.");

        if (consignment.FreightCost is not { } cost || string.IsNullOrWhiteSpace(consignment.FreightCurrency))
            throw new ConflictException(
                $"Consignment {consignment.ConsignmentNumber} has moved but was never priced, so "
              + "there is no figure to accrue. Price it first — accruing zero would understate what "
              + "is owed and look correct doing it.");

        var accrual = await BuildAsync(consignment, cost, userId, ct);

        _db.FreightAccruals.Add(accrual);
        await _db.SaveChangesAsync(ct);

        return ToModel(accrual);
    }

    private async Task<FreightAccrual> BuildAsync(
        Consignment consignment, decimal cost, int userId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        // What the carrier said when it accepted the booking. Copied here rather than left on the
        // command ledger (F45), so all three legs of the match end up in one place — the same
        // reasoning that put the quote on the consignment in T-47.
        var booking = await _db.CarrierCommands.AsNoTracking()
            .Where(c => c.ConsignmentId == consignment.Id
                     && c.CommandType == LogisticsCode.Of(CarrierCommandType.Book)
                     && c.Status == LogisticsCode.Of(CarrierCommandStatus.Succeeded)
                     && c.Cost != null)
            .OrderByDescending(c => c.CompletedAt)
            .FirstOrDefaultAsync(ct);

        return new FreightAccrual
        {
            UUID           = Guid.NewGuid(),
            OrganizationId = consignment.OrganizationId,
            ConsignmentId  = consignment.Id,
            CarrierId      = consignment.CarrierId,
            CarrierName    = consignment.CarrierName ?? consignment.Carrier?.Name,
            AccruedAmount  = cost,
            Currency       = consignment.FreightCurrency!,
            QuoteSource    = consignment.FreightRateSource,
            BookedAmount   = booking?.Cost,
            BookedCurrency = booking?.CostCurrency,
            Status         = Open,
            AccruedAt      = now,
            AccruedBy      = userId,
            CreatedBy      = userId,
            CreatedDate    = now
        };
    }

    // ── The sweep ─────────────────────────────────────────────────────────────

    public async Task<FreightAccrualSweepResult> SweepAsync(int userId, CancellationToken ct = default)
    {
        var accruedIds = await _db.FreightAccruals.AsNoTracking()
            .Where(a => !a.IsDelete)
            .Select(a => a.ConsignmentId)
            .ToListAsync(ct);

        var moved = await _db.Consignments
            .Include(c => c.Carrier)
            .Where(c => !c.IsDelete && Dispatched.Contains(c.Status) && !accruedIds.Contains(c.Id))
            .ToListAsync(ct);

        var accrued = 0;
        var exceptions = new List<UnaccruableConsignmentModel>();

        foreach (var consignment in moved)
        {
            if (consignment.FreightCost is { } cost && !string.IsNullOrWhiteSpace(consignment.FreightCurrency))
            {
                _db.FreightAccruals.Add(await BuildAsync(consignment, cost, userId, ct));
                accrued++;
                continue;
            }

            // Not accrued at zero. A zero accrual understates the liability and looks correct doing
            // it; a named exception is something somebody can act on. Finding F44.
            exceptions.Add(new UnaccruableConsignmentModel
            {
                ConsignmentUuid   = consignment.UUID,
                ConsignmentNumber = consignment.ConsignmentNumber,
                Status            = consignment.Status,
                CarrierName       = consignment.CarrierName ?? consignment.Carrier?.Name,
                MasterAwb         = consignment.MasterAwb,
                DispatchedAt      = consignment.ActualDispatchAt,
                Reason            = "Dispatched but never priced, so there is no figure to accrue."
            });
        }

        // An accrual whose consignment was cancelled after it was made is a liability for a
        // movement that did not happen. Written back here rather than left to be noticed.
        var stale = await _db.FreightAccruals
            .Include(a => a.Consignment)
            .Where(a => !a.IsDelete && a.Status == Open && a.Consignment.Status == Cancelled)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;

        foreach (var accrual in stale)
        {
            accrual.Status        = LogisticsCode.Of(FreightAccrualStatus.Reversed);
            accrual.ReleasedAt    = now;
            accrual.ReleaseReason = "The consignment was cancelled after this was accrued.";
            accrual.ModifiedBy    = userId;
            accrual.ModifiedDate  = now;
        }

        if (accrued > 0 || stale.Count > 0) await _db.SaveChangesAsync(ct);

        return new FreightAccrualSweepResult(accrued, stale.Count, exceptions);
    }

    // ── The balance ───────────────────────────────────────────────────────────

    public async Task<FreightAccrualSummaryModel> GetSummaryAsync(
        DateTime? asOf = null, CancellationToken ct = default)
    {
        var at = (asOf ?? DateTime.UtcNow);

        // Open as at the date, not open today: an accrual released last week was still a liability
        // at month end, and a balance that forgets that cannot be reconciled to anything.
        var open = await _db.FreightAccruals.AsNoTracking()
            .Include(a => a.Carrier)
            .Where(a => !a.IsDelete
                     && a.AccruedAt <= at
                     && (a.ReleasedAt == null || a.ReleasedAt > at))
            .ToListAsync(ct);

        var byCarrier = open
            .GroupBy(a => (a.Carrier?.UUID, Name: a.CarrierName ?? a.Carrier?.Name ?? "(no carrier)", a.Currency))
            .Select(g => new FreightAccrualTotalModel
            {
                CarrierUuid = g.Key.UUID,
                CarrierName = g.Key.Name,
                Currency    = g.Key.Currency,
                Count       = g.Count(),
                Total       = g.Sum(a => a.AccruedAmount)
            })
            .OrderByDescending(t => t.Total)
            .ThenBy(t => t.CarrierName, StringComparer.Ordinal)
            .ToList();

        var currencies = open.Select(a => a.Currency).Distinct().ToList();

        var summary = new FreightAccrualSummaryModel
        {
            AsOf      = at,
            OpenCount = open.Count,
            // Only meaningful within one currency. This module holds no exchange rate (F42), and a
            // grand total across two is a number nobody could defend.
            OpenTotal = currencies.Count == 1 ? open.Sum(a => a.AccruedAmount) : 0m,
            Currency  = currencies.Count == 1 ? currencies[0] : null,
            ByCarrier = byCarrier
        };

        if (currencies.Count > 1)
            summary.Warnings.Add(
                $"Open accruals are in {string.Join(", ", currencies.Order())}. There is no exchange "
              + "rate here, so they are totalled per carrier and currency rather than added together.");

        await AddUnaccruableAsync(summary, at, ct);

        return summary;
    }

    /// <summary>
    /// Dispatched, never priced. These are missing from the totals, and a balance that did not say
    /// so would be understated by however many of them there are.
    /// </summary>
    private async Task AddUnaccruableAsync(
        FreightAccrualSummaryModel summary, DateTime at, CancellationToken ct)
    {
        var accruedIds = await _db.FreightAccruals.AsNoTracking()
            .Where(a => !a.IsDelete)
            .Select(a => a.ConsignmentId)
            .ToListAsync(ct);

        var missing = await _db.Consignments.AsNoTracking()
            .Include(c => c.Carrier)
            .Where(c => !c.IsDelete
                     && Dispatched.Contains(c.Status)
                     && !accruedIds.Contains(c.Id)
                     && c.FreightCost == null)
            .OrderBy(c => c.ConsignmentNumber)
            .ToListAsync(ct);

        summary.CouldNotAccrue =
        [
            .. missing.Select(c => new UnaccruableConsignmentModel
            {
                ConsignmentUuid   = c.UUID,
                ConsignmentNumber = c.ConsignmentNumber,
                Status            = c.Status,
                CarrierName       = c.CarrierName ?? c.Carrier?.Name,
                MasterAwb         = c.MasterAwb,
                DispatchedAt      = c.ActualDispatchAt,
                Reason            = "Dispatched but never priced, so there is no figure to accrue."
            })
        ];

        if (summary.CouldNotAccrue.Count > 0)
            summary.Warnings.Add(
                $"{summary.CouldNotAccrue.Count} consignment(s) have moved without ever being priced. "
              + "They are not in the totals above, so what is owed is understated by whatever they "
              + "come to.");

        _ = at;
    }

    // ── Reversing ─────────────────────────────────────────────────────────────

    public async Task<bool> ReverseAsync(
        Guid consignmentUuid, ReverseAccrualRequest req, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);

        var accrual = await _db.FreightAccruals
            .Include(a => a.Consignment)
            .FirstOrDefaultAsync(a => a.Consignment.UUID == consignmentUuid && !a.IsDelete, ct);

        if (accrual is null) return false;

        var reason = req.Reason?.Trim();

        if (string.IsNullOrWhiteSpace(reason))
            throw new BadRequestException(
                "Say why this accrual is being written back. An accrual that vanishes without a "
              + "reason is a hole in a ledger nobody can explain later.");

        if (accrual.Status != Open)
            throw new ConflictException(
                $"This accrual is {accrual.Status}, not {Open}. Only an open accrual can be written back.");

        var now = DateTime.UtcNow;

        accrual.Status        = LogisticsCode.Of(FreightAccrualStatus.Reversed);
        accrual.ReleasedAt    = now;
        accrual.ReleaseReason = reason;
        accrual.ModifiedBy    = userId;
        accrual.ModifiedDate  = now;

        await _db.SaveChangesAsync(ct);
        return true;
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────

    private static FreightAccrualModel ToModel(FreightAccrual a) => new()
    {
        UUID              = a.UUID,
        ConsignmentUuid   = a.Consignment.UUID,
        ConsignmentNumber = a.Consignment.ConsignmentNumber,
        ConsignmentStatus = a.Consignment.Status,
        CarrierUuid       = a.Carrier?.UUID,
        CarrierName       = a.CarrierName ?? a.Carrier?.Name,
        AccruedAmount     = a.AccruedAmount,
        Currency          = a.Currency,
        QuoteSource       = a.QuoteSource,
        BookedAmount      = a.BookedAmount,
        BookedCurrency    = a.BookedCurrency,
        // Only across the same currency. Comparing a rupee quote with a dollar booking would
        // produce a variance that means nothing at all.
        BookedVariance    = a.BookedAmount is { } booked
                         && string.Equals(a.BookedCurrency, a.Currency, StringComparison.OrdinalIgnoreCase)
                            ? booked - a.AccruedAmount
                            : null,
        Status            = a.Status,
        AccruedAt         = a.AccruedAt,
        ReleasedAt        = a.ReleasedAt,
        ReleaseReason     = a.ReleaseReason
    };
}
