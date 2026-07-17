using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Models;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Material.Services;

internal sealed class PrLookupService : IPrLookupService
{
    // MIR lines linked to a PR line while in these statuses no longer hold a claim on its quantity.
    private static readonly HashSet<string> NonConsumingMirStatuses = ["CANCELLED", "REJECTED"];

    private readonly DemandDbContext   _demand;
    private readonly MaterialDbContext _material;

    public PrLookupService(DemandDbContext demand, MaterialDbContext material)
    {
        _demand   = demand;
        _material = material;
    }

    public async Task<List<PrLineSearchResult>> SearchAsync(Guid productId, string status)
    {
        var lines = await _demand.PrLines
            .Where(l => l.ProductId == productId
                     && l.PurchaseRequisition.Status == status
                     && !l.PurchaseRequisition.IsDelete)
            .Select(l => new
            {
                l.Id,
                l.UUID,
                l.Quantity,
                l.ItemDescription,
                PrNumber = l.PurchaseRequisition.PrNumber,
                PrTitle  = l.PurchaseRequisition.PrTitle
            })
            .ToListAsync();

        if (lines.Count == 0) return [];

        var lineIds = lines.Select(l => l.Id).ToList();

        var disbursedByLineId = await _material.MaterialIssueRequestDetails
            .Where(d => d.PrLineId.HasValue && lineIds.Contains(d.PrLineId.Value)
                     && !d.MaterialIssueRequest.IsDelete
                     && !NonConsumingMirStatuses.Contains(d.MaterialIssueRequest.Status))
            .GroupBy(d => d.PrLineId!.Value)
            .Select(g => new { PrLineId = g.Key, DisbursedQty = g.Sum(x => x.RequestedQty) })
            .ToDictionaryAsync(x => x.PrLineId, x => x.DisbursedQty);

        var results = new List<PrLineSearchResult>();
        foreach (var l in lines)
        {
            var disbursedQty = disbursedByLineId.TryGetValue(l.Id, out var d) ? d : 0m;
            if (disbursedQty >= l.Quantity) continue;

            results.Add(new PrLineSearchResult
            {
                PrNumber                = l.PrNumber,
                PrTitle                 = l.PrTitle,
                LineDescription         = l.ItemDescription,
                RequestedQty            = l.Quantity,
                RemainingUndisbursedQty = l.Quantity - disbursedQty,
                PrLineId                = l.UUID
            });
        }
        return results;
    }

    // Drill-down for the PR-line disbursement badge: lists every MIR recorded in
    // disbursed_mir_ids, in the order they were approved, with the qty each one
    // actually disbursed against this specific PR line.
    public async Task<List<PrLineDisbursementModel>> GetDisbursementsAsync(Guid prId, Guid lineId)
    {
        var prLine = await _demand.PrLines
            .FirstOrDefaultAsync(l => l.UUID == lineId && l.PurchaseRequisition.UUID == prId)
            ?? throw new NotFoundException("PrLine", lineId);

        var mirUuids = DeserializeMirIds(prLine.DisbursedMirIds);
        if (mirUuids.Count == 0) return [];

        var mirs = await _material.MaterialIssueRequests
            .Include(m => m.Project)
            .Where(m => mirUuids.Contains(m.UUID))
            .ToListAsync();

        var mirIds = mirs.Select(m => m.Id).ToList();

        var lineIdByMirId = await _material.MaterialIssueRequestDetails
            .Where(d => d.PrLineId == prLine.Id && mirIds.Contains(d.MaterialIssueRequestId))
            .ToDictionaryAsync(d => d.MaterialIssueRequestId, d => d.Id);

        var mirLineIds = lineIdByMirId.Values.ToList();
        var approvedQtyByMirLineId = mirLineIds.Count > 0
            ? await _material.MirLineApprovals
                .Where(a => mirLineIds.Contains(a.LineId))
                .GroupBy(a => a.LineId)
                .Select(g => new { LineId = g.Key, Qty = g.OrderByDescending(a => a.StepNumber).First().ApprovedQty })
                .ToDictionaryAsync(x => x.LineId, x => x.Qty)
            : new Dictionary<int, decimal>();

        var results = new List<PrLineDisbursementModel>();
        foreach (var mirUuid in mirUuids)
        {
            var mir = mirs.FirstOrDefault(m => m.UUID == mirUuid);
            if (mir is null) continue; // defensive — MIR no longer resolvable

            decimal approvedQty = 0m;
            if (lineIdByMirId.TryGetValue(mir.Id, out var mirLineId) && approvedQtyByMirLineId.TryGetValue(mirLineId, out var qty))
                approvedQty = qty;

            results.Add(new PrLineDisbursementModel
            {
                MirUuid       = mir.UUID,
                MirNumber     = mir.RequestNo,
                ApprovedDate  = mir.ApprovedAt,
                ApprovedQty   = approvedQty,
                ProjectOrDept = mir.RequestType == "PROJECT" ? mir.Project?.ProjectName : (mir.Department ?? mir.MaintenanceRef)
            });
        }
        return results;
    }

    private static List<Guid> DeserializeMirIds(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<Guid>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
