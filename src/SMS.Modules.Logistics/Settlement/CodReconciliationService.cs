using Microsoft.EntityFrameworkCore;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Models;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Logistics.Settlement;

/// <summary>
/// Cash a carrier collects on delivery, and whether it came back.
/// <para>
/// <b>The opposite of a freight accrual.</b> An accrual is what we owe a carrier; this is cash the
/// carrier is holding on our behalf — already taken from a customer and not yet passed on.
/// Outstanding COD is a receivable, and until now nobody was counting it.
/// </para>
/// </summary>
public interface ICodReconciliationService
{
    Task<CodCollectionModel?> GetAsync(Guid consignmentUuid, CancellationToken ct = default);

    Task<PaginatedResponse<CodCollectionModel>> GetListAsync(CodFilter filter, CancellationToken ct = default);

    /// <summary>
    /// Opens a record for every consignment carrying cash that has been dispatched and has none.
    /// What a nightly job runs.
    /// </summary>
    Task<int> OpenOutstandingAsync(int userId, CancellationToken ct = default);

    /// <summary>Records that the carrier says it took the money.</summary>
    Task<CodCollectionModel?> RecordCollectionAsync(
        Guid consignmentUuid, RecordCodCollectionRequest req, int userId, CancellationToken ct = default);

    /// <summary>Records money reaching us. Several are expected — carriers remit in batches.</summary>
    Task<CodCollectionModel?> RecordRemittanceAsync(
        Guid consignmentUuid, RecordCodRemittanceRequest req, int userId, CancellationToken ct = default);

    /// <summary>Accepts a shortfall that will not be recovered.</summary>
    Task<bool> WriteOffAsync(
        Guid consignmentUuid, WriteOffCodRequest req, int userId, CancellationToken ct = default);

    /// <summary>What carriers are still holding, by carrier and currency, as at a date.</summary>
    Task<CodSummaryModel> GetSummaryAsync(DateTime? asOf = null, CancellationToken ct = default);
}

internal sealed class CodReconciliationService : ICodReconciliationService
{
    /// <summary>Once the carrier has the goods, the cash is collectable.</summary>
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

    private static readonly string Delivered  = LogisticsCode.Of(ShipmentStatus.Delivered);
    private static readonly string Expected   = LogisticsCode.Of(CodStatus.Expected);
    private static readonly string Collected  = LogisticsCode.Of(CodStatus.Collected);
    private static readonly string Settled    = LogisticsCode.Of(CodStatus.Settled);
    private static readonly string WrittenOff = LogisticsCode.Of(CodStatus.WrittenOff);

    private readonly LogisticsDbContext _db;
    public CodReconciliationService(LogisticsDbContext db) => _db = db;

    // ── Opening ───────────────────────────────────────────────────────────────

    public async Task<int> OpenOutstandingAsync(int userId, CancellationToken ct = default)
    {
        var existing = await _db.CodCollections.AsNoTracking()
            .Where(c => !c.IsDelete)
            .Select(c => c.ConsignmentId)
            .ToListAsync(ct);

        var owed = await _db.Consignments
            .Include(c => c.Carrier)
            .Where(c => !c.IsDelete
                     && c.CodAmount > 0
                     && Dispatched.Contains(c.Status)
                     && !existing.Contains(c.Id))
            .ToListAsync(ct);

        if (owed.Count == 0) return 0;

        var now = DateTime.UtcNow;

        foreach (var consignment in owed)
            _db.CodCollections.Add(new CodCollection
            {
                UUID           = Guid.NewGuid(),
                OrganizationId = consignment.OrganizationId,
                ConsignmentId  = consignment.Id,
                CarrierId      = consignment.CarrierId,
                CarrierName    = consignment.CarrierName ?? consignment.Carrier?.Name,
                // Copied as at now. Editing the consignment afterwards must not silently change
                // what a carrier owes us.
                ExpectedAmount = consignment.CodAmount!.Value,
                Currency       = consignment.CodCurrency ?? string.Empty,
                Status         = Expected,
                CreatedBy      = userId,
                CreatedDate    = now
            });

        await _db.SaveChangesAsync(ct);

        return owed.Count;
    }

    // ── Recording what happened ───────────────────────────────────────────────

    public async Task<CodCollectionModel?> RecordCollectionAsync(
        Guid consignmentUuid, RecordCodCollectionRequest req, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);

        var record = await LoadAsync(consignmentUuid, ct);
        if (record is null) return null;

        EnsureOpen(record);

        if (req.Amount <= 0m)
            throw new BadRequestException("A collection of nothing is not a collection.");

        // More than the consignment asked for is almost always a keying error, and the money would
        // be attributed to the wrong place if it were not.
        if (req.Amount > record.ExpectedAmount)
            throw new BadRequestException(
                $"This consignment collects {record.Currency} {record.ExpectedAmount:N2} and "
              + $"{record.Currency} {req.Amount:N2} is being recorded. Correct the consignment if "
              + "the carrier really took more.");

        var now = DateTime.UtcNow;

        record.CollectedAmount     = req.Amount;
        record.CollectedAt         = req.CollectedAt ?? now;
        record.CollectionReference = Trim(req.Reference);
        record.ModifiedBy          = userId;
        record.ModifiedDate        = now;

        if (record.Status == Expected) record.Status = Collected;

        await _db.SaveChangesAsync(ct);

        return await GetAsync(consignmentUuid, ct);
    }

    public async Task<CodCollectionModel?> RecordRemittanceAsync(
        Guid consignmentUuid, RecordCodRemittanceRequest req, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);

        var record = await LoadAsync(consignmentUuid, ct);
        if (record is null) return null;

        EnsureOpen(record);

        if (req.Amount <= 0m)
            throw new BadRequestException("A remittance of nothing is not a remittance.");

        var remaining = record.ExpectedAmount - record.RemittedAmount;

        if (req.Amount > remaining)
            throw new BadRequestException(
                $"Only {record.Currency} {remaining:N2} is outstanding on this consignment and "
              + $"{record.Currency} {req.Amount:N2} is being recorded. Money attributed to the wrong "
              + "consignment is money nobody can trace — split the remittance across the ones it covers.");

        var now = DateTime.UtcNow;

        record.Remittances.Add(new CodRemittance
        {
            UUID           = Guid.NewGuid(),
            OrganizationId = record.OrganizationId,
            Amount         = req.Amount,
            ReceivedAt     = req.ReceivedAt ?? now,
            Reference      = Trim(req.Reference),
            Note           = Trim(req.Note),
            CreatedBy      = userId,
            CreatedDate    = now
        });

        record.RemittedAmount += req.Amount;
        record.ModifiedBy      = userId;
        record.ModifiedDate    = now;

        if (record.RemittedAmount >= record.ExpectedAmount)
        {
            record.Status    = Settled;
            record.SettledAt = now;
        }
        else if (record.Status == Expected)
        {
            // Money arrived without the carrier ever confirming it collected. It plainly did.
            record.Status = Collected;
        }

        await _db.SaveChangesAsync(ct);

        return await GetAsync(consignmentUuid, ct);
    }

    public async Task<bool> WriteOffAsync(
        Guid consignmentUuid, WriteOffCodRequest req, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);

        var record = await LoadAsync(consignmentUuid, ct);
        if (record is null) return false;

        var reason = Trim(req.Reason)
            ?? throw new BadRequestException(
                "Say why this cash is being written off. Money written off without a reason is "
              + "money nobody can account for afterwards.");

        if (record.Status == Settled)
            throw new ConflictException(
                "Everything expected on this consignment has already reached us. There is nothing "
              + "to write off.");

        if (record.Status == WrittenOff)
            throw new ConflictException("This shortfall has already been written off.");

        var now = DateTime.UtcNow;

        record.Status         = WrittenOff;
        record.WriteOffReason = reason;
        record.SettledAt      = now;
        record.ModifiedBy     = userId;
        record.ModifiedDate   = now;

        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static void EnsureOpen(CodCollection record)
    {
        if (record.Status == WrittenOff)
            throw new ConflictException(
                "This shortfall was written off. Reverse the write-off before recording anything else "
              + "against it, or the two records will disagree about what happened.");

        if (record.Status == Settled)
            throw new ConflictException(
                "Everything expected on this consignment has already reached us.");
    }

    // ── Reading ───────────────────────────────────────────────────────────────

    public async Task<CodCollectionModel?> GetAsync(Guid consignmentUuid, CancellationToken ct = default)
    {
        var record = await _db.CodCollections.AsNoTracking()
            .Include(c => c.Consignment)
            .Include(c => c.Carrier)
            .Include(c => c.Remittances)
            .FirstOrDefaultAsync(c => c.Consignment.UUID == consignmentUuid && !c.IsDelete, ct);

        return record is null ? null : ToModel(record);
    }

    public async Task<PaginatedResponse<CodCollectionModel>> GetListAsync(
        CodFilter filter, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var q = _db.CodCollections.AsNoTracking()
            .Include(c => c.Consignment)
            .Include(c => c.Carrier)
            .Include(c => c.Remittances)
            .Where(c => !c.IsDelete);

        // The default is everything still owed to us. A list that defaulted to everything would
        // bury the only rows anybody chases.
        q = string.IsNullOrWhiteSpace(filter.Status)
            ? q.Where(c => c.Status == Expected || c.Status == Collected)
            : q.Where(c => c.Status == filter.Status);

        if (filter.CarrierUuid is { } carrierUuid)
            q = q.Where(c => c.Carrier!.UUID == carrierUuid);

        var page     = filter.Page     < 1 ? 1  : filter.Page;
        var pageSize = filter.PageSize < 1 ? 20 : filter.PageSize;

        var total = await q.CountAsync(ct);

        // Largest outstanding first: the biggest sum a carrier is sitting on is the one worth
        // ringing about.
        var records = await q
            .OrderByDescending(c => c.ExpectedAmount - c.RemittedAmount)
            .ThenBy(c => c.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PaginatedResponse<CodCollectionModel>
        {
            Data         = [.. records.Select(ToModel)],
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling(total / (double)pageSize)
        };
    }

    public async Task<CodSummaryModel> GetSummaryAsync(DateTime? asOf = null, CancellationToken ct = default)
    {
        var at = asOf ?? DateTime.UtcNow;

        var open = await _db.CodCollections.AsNoTracking()
            .Include(c => c.Carrier)
            .Where(c => !c.IsDelete
                     && c.CreatedDate <= at
                     && (c.SettledAt == null || c.SettledAt > at))
            .ToListAsync(ct);

        var byCarrier = open
            .GroupBy(c => (c.Carrier?.UUID, Name: c.CarrierName ?? c.Carrier?.Name ?? "(no carrier)", c.Currency))
            .Select(g => new CodCarrierTotalModel
            {
                CarrierUuid = g.Key.UUID,
                CarrierName = g.Key.Name,
                Currency    = g.Key.Currency,
                Count       = g.Count(),
                Outstanding = g.Sum(c => c.ExpectedAmount - c.RemittedAmount)
            })
            .Where(t => t.Outstanding != 0m)
            .OrderByDescending(t => t.Outstanding)
            .ThenBy(t => t.CarrierName, StringComparer.Ordinal)
            .ToList();

        var currencies = open.Select(c => c.Currency).Distinct().ToList();

        var summary = new CodSummaryModel
        {
            AsOf      = at,
            OpenCount = open.Count,
            // Only within one currency. There is no exchange rate here (F42), and a grand total
            // across two would be a number nobody could defend.
            OutstandingTotal = currencies.Count == 1
                ? open.Sum(c => c.ExpectedAmount - c.RemittedAmount)
                : 0m,
            Currency  = currencies.Count == 1 ? currencies[0] : null,
            ByCarrier = byCarrier
        };

        if (currencies.Count > 1)
            summary.Warnings.Add(
                $"Outstanding cash is in {string.Join(", ", currencies.Order())}. There is no exchange "
              + "rate here, so it is totalled per carrier and currency rather than added together.");

        await AddNeverCollectedAsync(summary, ct);

        return summary;
    }

    /// <summary>
    /// Delivered, carrying cash, and nothing says the carrier ever took it. The analogue of F44 on
    /// the other side of the ledger — money that should have come in, and silence about whether it did.
    /// </summary>
    private async Task AddNeverCollectedAsync(CodSummaryModel summary, CancellationToken ct)
    {
        var records = await _db.CodCollections.AsNoTracking()
            .Include(c => c.Consignment)
            .Include(c => c.Carrier)
            .Where(c => !c.IsDelete
                     && c.CollectedAmount == null
                     && c.RemittedAmount == 0m
                     && c.Consignment.Status == Delivered
                     && c.Status != WrittenOff)
            .OrderByDescending(c => c.ExpectedAmount)
            .ToListAsync(ct);

        summary.NeverCollected =
        [
            .. records.Select(c => new UncollectedCodModel
            {
                ConsignmentUuid   = c.Consignment.UUID,
                ConsignmentNumber = c.Consignment.ConsignmentNumber,
                Status            = c.Consignment.Status,
                CarrierName       = c.CarrierName ?? c.Carrier?.Name,
                MasterAwb         = c.Consignment.MasterAwb,
                ExpectedAmount    = c.ExpectedAmount,
                Currency          = c.Currency,
                Reason            = "Delivered with cash to collect, and the carrier has never said it took it."
            })
        ];

        if (summary.NeverCollected.Count > 0)
            summary.Warnings.Add(
                $"{summary.NeverCollected.Count} consignment(s) were delivered carrying cash and "
              + "nothing says it was collected. Ask the carrier before the trail goes cold.");
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────

    private Task<CodCollection?> LoadAsync(Guid consignmentUuid, CancellationToken ct) =>
        _db.CodCollections
            .Include(c => c.Consignment)
            .Include(c => c.Remittances)
            .FirstOrDefaultAsync(c => c.Consignment.UUID == consignmentUuid && !c.IsDelete, ct);

    private static CodCollectionModel ToModel(CodCollection c)
    {
        var outstanding = c.ExpectedAmount - c.RemittedAmount;

        var model = new CodCollectionModel
        {
            UUID                = c.UUID,
            ConsignmentUuid     = c.Consignment.UUID,
            ConsignmentNumber   = c.Consignment.ConsignmentNumber,
            ConsignmentStatus   = c.Consignment.Status,
            MasterAwb           = c.Consignment.MasterAwb,
            CarrierUuid         = c.Carrier?.UUID,
            CarrierName         = c.CarrierName ?? c.Carrier?.Name,
            ExpectedAmount      = c.ExpectedAmount,
            Currency            = c.Currency,
            CollectedAmount     = c.CollectedAmount,
            CollectedAt         = c.CollectedAt,
            CollectionReference = c.CollectionReference,
            RemittedAmount      = c.RemittedAmount,
            OutstandingAmount   = outstanding,
            Status              = c.Status,
            SettledAt           = c.SettledAt,
            WriteOffReason      = c.WriteOffReason,
            Remittances = [.. c.Remittances.OrderBy(r => r.ReceivedAt).Select(r => new CodRemittanceModel
            {
                UUID = r.UUID, Amount = r.Amount, ReceivedAt = r.ReceivedAt,
                Reference = r.Reference, Note = r.Note
            })]
        };

        if (c.RemittedAmount > 0m && c.CollectedAmount is null)
            model.Warnings.Add(
                "Money has arrived without the carrier ever confirming it collected any. Worth "
              + "reconciling against its own records before this is closed.");

        if (c.CollectedAmount is { } collected && collected < c.ExpectedAmount)
            model.Warnings.Add(
                $"The carrier says it took {c.Currency} {collected:N2} of the {c.Currency} "
              + $"{c.ExpectedAmount:N2} expected. The shortfall is a customer's underpayment, not "
              + "the carrier's — check before chasing the wrong party.");

        if (c.Consignment.Status == LogisticsCode.Of(ShipmentStatus.ReturnedToOrigin)
         && c.Status != WrittenOff && outstanding > 0m)
            model.Warnings.Add(
                "This consignment came back. There was probably never any cash to collect — write "
              + "it off rather than leaving it outstanding forever.");

        return model;
    }

    private static string? Trim(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
