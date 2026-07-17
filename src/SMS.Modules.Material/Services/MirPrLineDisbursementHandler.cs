using System.Text.Json;
using MediatR;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Material.Data;
using SMS.WorkflowEngine.Events;

namespace SMS.Modules.Material.Services;

/// <summary>
/// Tracks PR-line disbursement quantity when an MIR's workflow approval completes.
/// This handler ONLY ever reads/writes PrLine.DisbursedQty and PrLine.DisbursedMirIds — it never
/// reads or writes PrLine.LineStatus or PurchaseRequisition.Status. Those fields are entirely
/// out of scope here; disbursement tracking is a quantity ledger, not a status transition.
/// </summary>
internal sealed class MirPrLineDisbursementHandler : INotificationHandler<DocumentApprovedEvent>
{
    private static readonly HashSet<string> MirInterfaceCodes = ["MIR_PROJECT", "MIR_GENERAL"];

    private readonly MaterialDbContext _material;
    private readonly DemandDbContext   _demand;

    public MirPrLineDisbursementHandler(MaterialDbContext material, DemandDbContext demand)
    {
        _material = material;
        _demand   = demand;
    }

    public async Task Handle(DocumentApprovedEvent notification, CancellationToken cancellationToken)
    {
        if (!MirInterfaceCodes.Contains(notification.InterfaceCode)) return;

        var mir = await _material.MaterialIssueRequests
            .Include(m => m.Lines)
            .FirstOrDefaultAsync(m => m.UUID == notification.DocumentId, cancellationToken);
        if (mir is null) return;

        var lineIds = mir.Lines.Select(l => l.Id).ToList();
        if (lineIds.Count == 0) return;

        // Latest approved qty per line (highest step number) — mirrors MirWorkflowService's own logic.
        var latestQtys = await _material.MirLineApprovals
            .Where(a => a.MirId == mir.Id && lineIds.Contains(a.LineId))
            .GroupBy(a => a.LineId)
            .Select(g => new { LineId = g.Key, ApprovedQty = g.OrderByDescending(a => a.StepNumber).First().ApprovedQty })
            .ToDictionaryAsync(x => x.LineId, x => x.ApprovedQty, cancellationToken);

        // Sum approved qty per linked PR line (defensive against >1 MIR line pointing at the same PR line).
        var deltaByPrLineId = new Dictionary<int, decimal>();
        foreach (var line in mir.Lines)
        {
            if (!line.PrLineId.HasValue) continue;
            var approvedQty = latestQtys.TryGetValue(line.Id, out var q) ? q : line.RequestedQty;
            deltaByPrLineId[line.PrLineId.Value] = deltaByPrLineId.GetValueOrDefault(line.PrLineId.Value) + approvedQty;
        }

        if (deltaByPrLineId.Count == 0) return;

        var mirUuid = mir.UUID;

        // Serializable transaction: read-modify-write of disbursed_qty must not lose updates when
        // two MIR approvals against the same PR line commit concurrently.
        var strategy = _demand.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await _demand.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, cancellationToken);

            var prLines = await _demand.PrLines
                .Where(l => deltaByPrLineId.Keys.Contains(l.Id))
                .ToListAsync(cancellationToken);

            foreach (var prLine in prLines)
            {
                var delta = deltaByPrLineId[prLine.Id];

                var mirIds = DeserializeMirIds(prLine.DisbursedMirIds);
                if (!mirIds.Contains(mirUuid))
                    mirIds.Add(mirUuid);

                prLine.DisbursedQty    += delta;
                prLine.DisbursedMirIds  = JsonSerializer.Serialize(mirIds);
            }

            await _demand.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        });
    }

    private static List<Guid> DeserializeMirIds(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<Guid>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
