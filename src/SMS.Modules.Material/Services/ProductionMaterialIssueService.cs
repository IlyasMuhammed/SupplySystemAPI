using System.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Services;
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
/// A30 §16. STANDARD takes what a requirement has held for it; ADDITIONAL and SUBSTITUTION take
/// from free stock instead (over what was planned, or a different variant altogether) and need
/// PROD_MANAGER — enforced by the controller, this service only records what it is told to move.
/// RETURN gives unused issued material back to stock; SCRAP records material that already left as
/// lost, with no stock movement of its own (A30-P3 domain note).
/// <para>
/// Reversing a STANDARD/ADDITIONAL/SUBSTITUTION issue gives the physical stock back and rolls the
/// requirement's issued quantity back, but does not reinstate the original allocation hold — the
/// shared allocation engine has no "un-consume". A reversed order picks up a fresh hold on its next
/// allocation run instead, same as any other requirement still short.
/// </para>
/// </summary>
internal sealed class ProductionMaterialIssueService : IProductionMaterialIssueService
{
    private readonly MaterialDbContext          _db;
    private readonly InventoryDbContext         _inv;
    private readonly IInventoryLedgerService    _ledger;
    private readonly IAllocationEngine          _engine;
    private readonly IDocumentNumberGenerator   _numbers;
    private readonly IProductionOrderRepository _orders;
    private readonly IBackgroundJobClient?      _jobs;

    public ProductionMaterialIssueService(
        MaterialDbContext db, InventoryDbContext inv, IInventoryLedgerService ledger, IAllocationEngine engine,
        IDocumentNumberGenerator numbers, IProductionOrderRepository orders, IBackgroundJobClient? jobs = null)
    {
        _db      = db;
        _inv     = inv;
        _ledger  = ledger;
        _engine  = engine;
        _numbers = numbers;
        _orders  = orders;
        _jobs    = jobs;
    }

    public async Task<Guid> CreateAsync(Guid productionOrderUuid, CreateProductionIssueRequest req, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);

        var issueType = (req.IssueType ?? ProductionIssueType.Standard).Trim().ToUpperInvariant();
        if (!ProductionIssueType.All.Contains(issueType))
            throw new BadRequestException($"'{req.IssueType}' is not an issue type. Use one of: {string.Join(", ", ProductionIssueType.All)}.");
        if (req.Lines is null || req.Lines.Count == 0)
            throw new BadRequestException("An issue needs at least one line.");

        var po = await _orders.LoadAsync(productionOrderUuid);
        if (po.Status is not (ProductionOrderStatus.Ready or ProductionOrderStatus.InProgress))
            throw new BadRequestException($"Production order {po.ProductionNumber} is {po.Status.ToLowerInvariant()}; materials can only move while it is ready or in progress.");

        var warehouseUuid = req.WarehouseUuid ?? po.WarehouseUuid;
        if (!await _inv.Warehouses.AsNoTracking().AnyAsync(w => w.Uuid == warehouseUuid && w.IsActive, ct))
            throw new BadRequestException($"Warehouse {warehouseUuid} does not exist or is inactive.");

        var pmrByUuid = po.Materials.ToDictionary(m => m.UUID);
        var now = DateTime.UtcNow;
        var issue = new ProductionMaterialIssue
        {
            IssueNumber       = await _numbers.NextAsync(ManufacturingDocumentPrefix.ProductionIssue, now),
            ProductionOrderId = po.Id,
            WarehouseUuid     = warehouseUuid,
            IssueType         = issueType,
            Status            = ProductionIssueStatus.Draft,
            Notes             = req.Notes?.Trim(),
            CreatedBy         = userId,
            CreatedAt         = now
        };

        for (var i = 0; i < req.Lines.Count; i++)
        {
            var line = req.Lines[i];
            if (!pmrByUuid.TryGetValue(line.RequirementUuid, out var pmr))
                throw new BadRequestException($"Line {i + 1}: requirement {line.RequirementUuid} does not belong to this production order.");
            if (pmr.Status == PmrStatus.Cancelled)
                throw new BadRequestException($"Line {i + 1}: this requirement was cancelled.");
            if (line.Quantity <= 0)
                throw new BadRequestException($"Line {i + 1}: quantity must be greater than zero.");

            var materialVariantUuid = pmr.MaterialVariantUuid;
            if (issueType == ProductionIssueType.Substitution)
            {
                if (line.MaterialVariantUuid is not { } alt || alt == pmr.MaterialVariantUuid)
                    throw new BadRequestException($"Line {i + 1}: a substitution needs a different material variant.");
                if (!await _inv.ProductVariants.AsNoTracking().AnyAsync(v => v.Uuid == alt && v.IsActive, ct))
                    throw new BadRequestException($"Line {i + 1}: the substitute variant does not exist or is inactive.");
                materialVariantUuid = alt;
            }
            else if (issueType == ProductionIssueType.Return)
            {
                var alreadyReturnable = pmr.IssuedQuantity - pmr.ReturnedQuantity;
                if (line.Quantity > alreadyReturnable)
                    throw new BadRequestException($"Line {i + 1}: only {alreadyReturnable:0.####} is issued and not yet returned.");
            }
            else if (issueType == ProductionIssueType.Scrap)
            {
                var wastable = pmr.IssuedQuantity - pmr.ReturnedQuantity - pmr.WastageQuantity;
                if (line.Quantity > wastable)
                    throw new BadRequestException($"Line {i + 1}: only {wastable:0.####} issued material is left to record as scrap.");
            }

            issue.Lines.Add(new ProductionMaterialIssueLine
            {
                RequirementId       = pmr.Id,
                MaterialVariantUuid = materialVariantUuid,
                Quantity            = line.Quantity,
                Uom                 = pmr.Uom,
                BatchNumber         = line.BatchNumber?.Trim(),
                Notes               = line.Notes?.Trim()
            });
        }

        _db.ProductionMaterialIssues.Add(issue);
        await _db.SaveChangesAsync(ct);

        if (req.Confirm) await ConfirmCoreAsync(issue, po, userId, ct);
        return issue.UUID;
    }

    public async Task ConfirmAsync(Guid uuid, int userId, CancellationToken ct = default)
    {
        var issue = await _db.ProductionMaterialIssues.Include(i => i.Lines)
            .FirstOrDefaultAsync(i => i.UUID == uuid, ct) ?? throw new NotFoundException("Production material issue", uuid);
        if (issue.Status != ProductionIssueStatus.Draft)
            throw new BadRequestException($"Issue {issue.IssueNumber} is already {issue.Status.ToLowerInvariant()}.");

        var po = await _orders.LoadAsync(issue.ProductionOrderId);
        await ConfirmCoreAsync(issue, po, userId, ct);
    }

    /// <summary>Posts the physical stock movement per line, then the requirement and order effects, all on the shared connection MIV uses for the same reason: one commit across two DbContexts.</summary>
    private async Task ConfirmCoreAsync(ProductionMaterialIssue issue, ProductionOrder po, int userId, CancellationToken ct)
    {
        await RunAcrossContextsAsync(async () =>
        {
            var pmrById = po.Materials.ToDictionary(m => m.Id);
            var now     = DateTime.UtcNow;

            foreach (var line in issue.Lines)
            {
                var pmr = pmrById[line.RequirementId];
                switch (issue.IssueType)
                {
                    case ProductionIssueType.Standard:
                        await ConsumeHeldAsync(pmr, issue, line, userId, ct);
                        pmr.IssuedQuantity += line.Quantity;
                        break;

                    case ProductionIssueType.Additional:
                    case ProductionIssueType.Substitution:
                        await DeductFreeStockAsync(issue.WarehouseUuid, line, issue, userId, ct);
                        pmr.IssuedQuantity += line.Quantity;
                        break;

                    case ProductionIssueType.Return:
                        await ReturnStockAsync(issue.WarehouseUuid, line, issue, userId, ct);
                        pmr.ReturnedQuantity += line.Quantity;
                        break;

                    case ProductionIssueType.Scrap:
                        // Already left stock when originally issued — a record only (domain note).
                        pmr.WastageQuantity += line.Quantity;
                        break;
                }

                var demand = pmr.AllocationDemandUuid is { } demandUuid ? await _engine.GetDemandAsync(demandUuid, ct) : null;
                ProductionReadiness.Apply(pmr, demand);
            }

            ProductionReadiness.Apply(po);

            issue.Status      = ProductionIssueStatus.Confirmed;
            issue.ConfirmedBy = userId;
            issue.ConfirmedAt = now;
        }, ct);

        // A30-P5-07 — after the commit, on the order's own trace.
        _jobs?.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
            po.TraceId,
            new TimelineEvent(ManufacturingTimelineEventTypes.MiConfirmed, ManufacturingInterfaceCodes.ProductionOrder, issue.UUID, issue.IssueNumber, DateTime.UtcNow, userId,
                $"{issue.IssueType.ToLowerInvariant()} issue against {po.ProductionNumber}."),
            ManufacturingInterfaceCodes.ProductionOrder, po.ProductionNumber));
    }

    public async Task ReverseAsync(Guid uuid, string reason, int userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new BadRequestException("A reason is required to reverse an issue.");

        var issue = await _db.ProductionMaterialIssues.Include(i => i.Lines)
            .FirstOrDefaultAsync(i => i.UUID == uuid, ct) ?? throw new NotFoundException("Production material issue", uuid);
        if (issue.Status != ProductionIssueStatus.Confirmed)
            throw new BadRequestException($"Issue {issue.IssueNumber} is {issue.Status.ToLowerInvariant()}; only a confirmed issue can be reversed.");

        var po      = await _orders.LoadAsync(issue.ProductionOrderId);
        var pmrById = po.Materials.ToDictionary(m => m.Id);
        var now     = DateTime.UtcNow;

        await RunAcrossContextsAsync(async () =>
        {
            foreach (var line in issue.Lines)
            {
                var pmr = pmrById[line.RequirementId];
                switch (issue.IssueType)
                {
                    case ProductionIssueType.Standard:
                    case ProductionIssueType.Additional:
                    case ProductionIssueType.Substitution:
                        await ReturnStockAsync(issue.WarehouseUuid, line, issue, userId, ct, reversal: true, reason: reason);
                        pmr.IssuedQuantity = Math.Max(0m, pmr.IssuedQuantity - line.Quantity);
                        break;

                    case ProductionIssueType.Return:
                        await DeductFreeStockAsync(issue.WarehouseUuid, line, issue, userId, ct, reversal: true, reason: reason);
                        pmr.ReturnedQuantity = Math.Max(0m, pmr.ReturnedQuantity - line.Quantity);
                        break;

                    case ProductionIssueType.Scrap:
                        pmr.WastageQuantity = Math.Max(0m, pmr.WastageQuantity - line.Quantity);
                        break;
                }

                var demand = pmr.AllocationDemandUuid is { } demandUuid ? await _engine.GetDemandAsync(demandUuid, ct) : null;
                ProductionReadiness.Apply(pmr, demand);
            }

            ProductionReadiness.Apply(po);

            issue.Status     = ProductionIssueStatus.Reversed;
            issue.ReversedBy = userId;
            issue.ReversedAt = now;
            issue.Notes      = string.IsNullOrWhiteSpace(issue.Notes) ? $"Reversed: {reason.Trim()}" : $"{issue.Notes}\nReversed: {reason.Trim()}";
        }, ct);
    }

    /// <summary>
    /// Runs <paramref name="work"/> then saves both contexts. On a relational provider they share
    /// one connection and one transaction, the arrangement MIV uses for the same reason — the
    /// commit has to be one thing across two DbContexts. On a non-relational provider (unit tests'
    /// InMemory database) there is no such thing as a shared connection, so this just runs the work
    /// and saves each context in turn.
    /// </summary>
    private Task RunAcrossContextsAsync(Func<Task> work, CancellationToken ct) =>
        MaterialStockMovements.RunAcrossContextsAsync(_db, _inv, work, ct);

    public async Task<ProductionIssueModel?> GetAsync(Guid uuid)
    {
        var issue = await _db.ProductionMaterialIssues.AsNoTracking().FirstOrDefaultAsync(i => i.UUID == uuid);
        return issue is null ? null : await _orders.GetIssueAsync(uuid);
    }

    // ── Stock movement ────────────────────────────────────────────────────────

    /// <summary>STANDARD — takes the physical stock and closes out the requirement's own hold, FIFO across whatever the engine reserved for it.</summary>
    private async Task ConsumeHeldAsync(ProductionMaterialRequirement pmr, ProductionMaterialIssue issue, ProductionMaterialIssueLine line, int userId, CancellationToken ct)
    {
        if (pmr.AllocationDemandUuid is not { } demandUuid)
            throw new BadRequestException($"Requirement for {line.MaterialVariantUuid} has nothing held for it yet; plan the order first.");

        var held = (await _engine.GetAllocationsAsync(new AllocationListFilter(DemandUuid: demandUuid, Status: AllocationStatus.Active), ct))
            .Items.Where(a => a.SupplyType == AllocationSupplyType.OnHand).OrderBy(a => a.AllocatedAt).ToList();

        // All or nothing, checked before touching anything: consuming what little is held and then
        // refusing the line would leave the requirement short of exactly the stock this call just
        // took, with no way back short of a manual correction.
        var available = held.Sum(a => a.AllocatedQty - a.ConsumedQty);
        if (available < line.Quantity)
            throw new BadRequestException(
                $"Only {available:0.####} of {line.Quantity:0.####} is held for this requirement. Issue an ADDITIONAL line for the rest.");

        var remaining = line.Quantity;
        foreach (var allocation in held)
        {
            if (remaining <= 0) break;
            var take = Math.Min(remaining, allocation.AllocatedQty - allocation.ConsumedQty);
            if (take <= 0) continue;

            var consumed = await _engine.ConsumeAsync(allocation.Uuid, take, userId, ct);
            await DeductOnHandAsync(pmr.MaterialVariantUuid, allocation.WarehouseUuid, consumed, issue, ct);
            remaining -= consumed;
        }
    }

    /// <summary>ADDITIONAL / SUBSTITUTION — takes from free stock, FEFO, nothing held first. Also how reversing a RETURN takes the given-back stock out again.</summary>
    private Task DeductFreeStockAsync(
        Guid warehouseUuid, ProductionMaterialIssueLine line, ProductionMaterialIssue issue, int userId, CancellationToken ct,
        bool reversal = false, string? reason = null) =>
        DeductOnHandAsync(line.MaterialVariantUuid, warehouseUuid, line.Quantity, issue, ct, reversalNote: reversal ? reason : null);

    /// <summary>Always a material-leaves-stock movement; the reversal note is what distinguishes an undo from an original issue.</summary>
    private Task<decimal> DeductOnHandAsync(
        Guid variantUuid, Guid warehouseUuid, decimal quantity, ProductionMaterialIssue issue,
        CancellationToken ct, string? reversalNote = null) =>
        MaterialStockMovements.DeductOnHandAsync(_inv, _ledger, variantUuid, warehouseUuid, quantity,
            new MaterialStockMovements.Reference(InventoryTransactionType.ProductionIssue, "PRODUCTION_ISSUE", issue.UUID, issue.IssueNumber,
                issue.ConfirmedBy ?? issue.CreatedBy),
            reversalNote is null ? $"Production issue {issue.IssueNumber}" : $"Reversal: {reversalNote}", ct);

    /// <summary>RETURN — gives issued material back to stock.</summary>
    private Task ReturnStockAsync(
        Guid warehouseUuid, ProductionMaterialIssueLine line, ProductionMaterialIssue issue, int userId, CancellationToken ct,
        bool reversal = false, string? reason = null) =>
        MaterialStockMovements.ReturnToStockAsync(_inv, _ledger, line.MaterialVariantUuid, warehouseUuid, line.Quantity, line.UnitCost,
            new MaterialStockMovements.Reference(InventoryTransactionType.ProductionReturn, "PRODUCTION_ISSUE", issue.UUID, issue.IssueNumber,
                issue.ConfirmedBy ?? issue.CreatedBy),
            reversal ? $"Reversal: {reason}" : $"Return against production issue {issue.IssueNumber}", ct);
}
