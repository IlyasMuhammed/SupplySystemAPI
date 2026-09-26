using Hangfire;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Models;
using SMS.Modules.Material.Repositories;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Jobs;
using SMS.WorkflowEngine.Models;

namespace SMS.Modules.Material.Services;

/// <summary>
/// A30 §18. An inspection is taken once, against the whole of what the order produced (§18.4: the
/// checked quantity must equal <see cref="Domain.ProductionOrder.ProducedQuantity"/>), and never
/// edited afterwards. The header's four buckets are derived from the lines, not chosen directly —
/// each line's whole checked quantity goes to exactly one outcome (Pass/Fail/Hold/Rework), so
/// accepted + rejected + hold + rework = inspected is true by construction rather than a rule to
/// enforce separately.
/// <para>
/// Passing does not touch inventory — nothing was ever added to stock for produced-but-uninspected
/// output, so there is nothing to credit until a <see cref="IFinishedGoodsReceiptService"/> receipt.
/// Rejecting likewise moves no stock (the same reason SCRAP issue lines do not — A30-P3-11); it is a
/// record the Production Ledger (§19A) surfaces as a debit, not a stock_transaction of its own.
/// Rework is recorded but does not automatically raise a new production cycle — a person raises one,
/// same as any other manual follow-up in this module.
/// </para>
/// </summary>
internal sealed class QualityInspectionService : IQualityInspectionService
{
    private readonly MaterialDbContext          _db;
    private readonly IProductionOrderRepository _orders;
    private readonly IDocumentNumberGenerator   _numbers;
    private readonly IBackgroundJobClient?      _jobs;
    private readonly IManufacturingNotificationService? _notify;

    public QualityInspectionService(MaterialDbContext db, IProductionOrderRepository orders, IDocumentNumberGenerator numbers,
        IBackgroundJobClient? jobs = null, IManufacturingNotificationService? notify = null)
    {
        _db      = db;
        _orders  = orders;
        _numbers = numbers;
        _jobs    = jobs;
        _notify  = notify;
    }

    public async Task<Guid> CreateAsync(Guid productionOrderUuid, CreateQualityInspectionRequest req, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (req.Lines is null || req.Lines.Count == 0)
            throw new BadRequestException("An inspection needs at least one check.");

        var po = await _orders.LoadAsync(productionOrderUuid);
        if (po.Status != ProductionOrderStatus.QualityInspection)
            throw new BadRequestException($"Production order {po.ProductionNumber} is {po.Status.ToLowerInvariant()}; only an order sent to quality inspection can be inspected.");
        if (await _db.QualityInspections.AnyAsync(q => q.ProductionOrderId == po.Id, ct))
            throw new BadRequestException($"Production order {po.ProductionNumber} has already been inspected.");
        // §18.4 separation of duties. "The production operator" is read as whoever raised the
        // order — the nearest fact this module actually tracks, the same reading BOM's four-eyes
        // rule gives "the submitter" (CreatedBy vs the approver).
        if (userId == po.CreatedBy)
            throw new BadRequestException("The person who raised this production order cannot inspect its output.");

        decimal accepted = 0, rejected = 0, hold = 0, rework = 0;
        var lines = new List<QualityInspectionLine>();
        for (var i = 0; i < req.Lines.Count; i++)
        {
            var line = req.Lines[i];
            if (string.IsNullOrWhiteSpace(line.CheckName))
                throw new BadRequestException($"Line {i + 1}: a check needs a name.");
            var result = (line.Result ?? string.Empty).Trim().ToUpperInvariant();
            if (!QiLineResult.All.Contains(result))
                throw new BadRequestException($"Line {i + 1}: '{line.Result}' is not a result. Use one of: {string.Join(", ", QiLineResult.All)}.");
            if (line.QuantityChecked <= 0)
                throw new BadRequestException($"Line {i + 1}: quantity checked must be greater than zero.");

            switch (result)
            {
                case QiLineResult.Pass:   accepted += line.QuantityChecked; break;
                case QiLineResult.Fail:   rejected += line.QuantityChecked; break;
                case QiLineResult.Hold:   hold     += line.QuantityChecked; break;
                case QiLineResult.Rework: rework   += line.QuantityChecked; break;
            }
            lines.Add(new QualityInspectionLine
            {
                CheckName = line.CheckName.Trim(), Result = result, QuantityChecked = line.QuantityChecked,
                DefectCode = line.DefectCode?.Trim(), Notes = line.Notes?.Trim()
            });
        }

        var inspected = accepted + rejected + hold + rework;
        if (inspected != po.ProducedQuantity)
            throw new BadRequestException(
                $"The checked quantity ({inspected:0.####}) must equal what this order produced ({po.ProducedQuantity:0.####}).");

        var overall =
            rejected > 0 && accepted == 0 && hold == 0 && rework == 0 ? QiOverallResult.Rejected :
            accepted > 0 && (rejected > 0 || hold > 0 || rework > 0)  ? QiOverallResult.PartiallyPassed :
            accepted > 0                                              ? QiOverallResult.Passed :
                                                                         QiOverallResult.Hold;

        var now = DateTime.UtcNow;
        var qi = new QualityInspection
        {
            InspectionNumber  = await _numbers.NextAsync(ManufacturingDocumentPrefix.QualityInspection, now),
            ProductionOrderId = po.Id,
            InspectedQuantity = inspected,
            AcceptedQuantity  = accepted,
            RejectedQuantity  = rejected,
            HoldQuantity      = hold,
            ReworkQuantity    = rework,
            OverallResult     = overall,
            InspectedBy       = userId,
            InspectedAt       = now,
            Notes             = req.Notes?.Trim(),
            CreatedAt         = now,
            Lines             = lines
        };
        _db.QualityInspections.Add(qi);

        po.RejectedQuantity += rejected;
        po.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);

        // A30-P5-07 — on the order's own trace.
        _jobs?.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
            po.TraceId,
            new TimelineEvent(ManufacturingTimelineEventTypes.QiRecorded, ManufacturingInterfaceCodes.ProductionOrder, qi.UUID, qi.InspectionNumber, DateTime.UtcNow, userId,
                $"{overall.ToLowerInvariant().Replace('_', ' ')} — {accepted:0.####} accepted, {rejected:0.####} rejected, {hold:0.####} hold, {rework:0.####} rework."),
            ManufacturingInterfaceCodes.ProductionOrder, po.ProductionNumber));

        if (_notify is not null) await _notify.QualityInspectionCompletedAsync(po, qi);

        return qi.UUID;
    }

    public async Task<QualityInspectionModel?> GetAsync(Guid uuid)
    {
        var qi = await _db.QualityInspections.AsNoTracking().Include(q => q.Lines).Include(q => q.ProductionOrder)
            .FirstOrDefaultAsync(q => q.UUID == uuid);
        return qi is null ? null : await FillAsync(qi);
    }

    public async Task<QualityInspectionModel?> GetForOrderAsync(Guid productionOrderUuid)
    {
        var qi = await _db.QualityInspections.AsNoTracking().Include(q => q.Lines).Include(q => q.ProductionOrder)
            .FirstOrDefaultAsync(q => q.ProductionOrder.UUID == productionOrderUuid);
        return qi is null ? null : await FillAsync(qi);
    }

    private async Task<QualityInspectionModel> FillAsync(QualityInspection qi)
    {
        var received = await _db.FinishedGoodsReceipts.AsNoTracking()
            .Where(f => f.QualityInspectionId == qi.Id && f.Status != FgrStatus.Reversed)
            .SumAsync(f => (decimal?)f.TotalQuantity) ?? 0m;

        return new QualityInspectionModel
        {
            UUID                = qi.UUID,
            InspectionNumber    = qi.InspectionNumber,
            ProductionOrderUuid = qi.ProductionOrder.UUID,
            ProductionNumber    = qi.ProductionOrder.ProductionNumber,
            InspectedQuantity   = qi.InspectedQuantity,
            AcceptedQuantity    = qi.AcceptedQuantity,
            RejectedQuantity    = qi.RejectedQuantity,
            HoldQuantity        = qi.HoldQuantity,
            ReworkQuantity      = qi.ReworkQuantity,
            OverallResult       = qi.OverallResult,
            InspectedBy         = qi.InspectedBy,
            InspectedAt         = qi.InspectedAt,
            Notes               = qi.Notes,
            OutstandingForFgr   = Math.Max(0m, qi.AcceptedQuantity - received),
            Lines = qi.Lines.Select(l => new QualityInspectionLineModel
            {
                UUID = l.UUID, CheckName = l.CheckName, Result = l.Result, QuantityChecked = l.QuantityChecked,
                DefectCode = l.DefectCode, Notes = l.Notes
            }).ToList()
        };
    }
}
