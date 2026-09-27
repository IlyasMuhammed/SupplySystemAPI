using Hangfire;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Models;
using SMS.Modules.Material.Repositories;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;
using SMS.WorkflowEngine.Jobs;
using SMS.WorkflowEngine.Models;

namespace SMS.Modules.Material.Services;

/// <summary>
/// A production order end to end (A30 §11, §12, §17). Planning explodes the snapshotted recipe,
/// registers what it needs with the shared allocation engine and lets that engine's decision —
/// applied by <see cref="ProductionReadinessListener"/> after every run — say whether the order is
/// ready for the floor. What a run cannot cover becomes a supply requirement
/// (<see cref="ISupplyRequirementEngine"/>), which is how a manufactured shortage becomes a child
/// production order and a purchased one becomes a purchase order.
/// <para>
/// Also implements <see cref="IProductionDemandService"/> (Phase 4 Track C) — the same "raise a
/// production order for a shortage" the supply requirement engine already does, reached instead by
/// a caller with no project reference to this module at all (Sales, for a manufactured product's own
/// deficit), resolved purely through the shared interface.
/// </para>
/// </summary>
internal sealed class ProductionOrderService : IProductionOrderService, IProductionDemandService
{
    private const int MaxAttempts = 3;

    private readonly IProductionOrderRepository _repo;
    private readonly MaterialDbContext          _db;
    private readonly InventoryDbContext         _inv;
    private readonly IAllocationEngine          _engine;
    private readonly ISupplyRequirementEngine   _supplyEngine;
    // Optional the way every other DI-only-in-production dependency in this codebase is: production
    // DI always supplies the real Hangfire client; this module's own unit tests wire every other
    // dependency by hand and have no use for a real job queue. Same reasoning for the notification
    // service — production DI supplies it, the hand-built ServiceCollection tests never register it.
    private readonly IBackgroundJobClient?              _jobs;
    private readonly IManufacturingNotificationService? _notify;

    public ProductionOrderService(
        IProductionOrderRepository repo, MaterialDbContext db, InventoryDbContext inv,
        IAllocationEngine engine, ISupplyRequirementEngine supplyEngine, IBackgroundJobClient? jobs = null,
        IManufacturingNotificationService? notify = null)
    {
        _repo         = repo;
        _db           = db;
        _inv          = inv;
        _engine       = engine;
        _supplyEngine = supplyEngine;
        _jobs         = jobs;
        _notify       = notify;
    }

    private async Task<string> ProductNameAsync(Guid productUuid, CancellationToken ct) =>
        await _inv.Products.AsNoTracking().Where(p => p.Uuid == productUuid).Select(p => p.Name).FirstOrDefaultAsync(ct)
        ?? productUuid.ToString();

    /// <summary>A30-P5-07 — one production order's own timeline append, on its own trace.</summary>
    private void EnqueueTimeline(ProductionOrder po, string eventType, int userId, string? notes) =>
        _jobs?.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
            po.TraceId,
            new TimelineEvent(eventType, ManufacturingInterfaceCodes.ProductionOrder, po.UUID, po.ProductionNumber, DateTime.UtcNow, userId, notes),
            ManufacturingInterfaceCodes.ProductionOrder, po.ProductionNumber));

    // ── Create / update ───────────────────────────────────────────────────────

    public async Task<Guid> CreateAsync(CreateProductionOrderRequest req, int userId, CancellationToken ct = default)
    {
        var po = await _repo.CreateAsync(req, userId);
        EnqueueTimeline(po, ManufacturingTimelineEventTypes.ProdCreated, userId, req.Notes);
        if (_notify is not null) await _notify.ProductionOrderCreatedAsync(po, await ProductNameAsync(po.ProductUuid, ct), userId);
        // CreateAsync's own INSERT never populates the Bom navigation (the active BOM is read via a
        // no-tracking lookup purely for its Id/Version) — PlanCoreAsync dereferences po.Bom.Lines
        // directly, so it needs the fully-tracked, Bom-included reload LoadAsync's own query already
        // does for every other caller of PlanCoreAsync. A30-P4-21 found this the hard way: every unit
        // test happens to share one DbContext across BOM setup and order creation, so a BOM tracked
        // earlier in the same test's own context silently fixed this up by accident — a fresh scope
        // (any real HTTP request or Hangfire job) has nothing tracked yet and threw a NullReferenceException.
        if (req.Plan) await PlanCoreAsync(await _repo.LoadAsync(po.UUID), userId, depth: 0, ct);
        return po.UUID;
    }

    public Task UpdateAsync(Guid uuid, UpdateProductionOrderRequest req, int userId, CancellationToken ct = default) =>
        RetryOnConcurrencyAsync(() => _repo.UpdateAsync(uuid, req, userId), ct);

    // ── Reads (no concurrency concern) ───────────────────────────────────────

    public Task<PaginatedResponse<ProductionOrderListItemModel>> GetListAsync(ProductionOrderListFilter filter) => _repo.GetListAsync(filter);
    public Task<ProductionOrderDetailModel?> GetByUuidAsync(Guid uuid) => _repo.GetByUuidAsync(uuid);
    public Task<IReadOnlyList<ProductionMaterialModel>> GetMaterialsAsync(Guid uuid) => _repo.GetMaterialsAsync(uuid);

    public async Task<IReadOnlyList<AllocationRunResult>> RunAllocationAsync(Guid uuid, int userId, CancellationToken ct = default)
    {
        var materials = await _repo.GetMaterialsAsync(uuid);
        var pairs = materials
            .Select(m => (m.MaterialVariantUuid, m.WarehouseUuid))
            .Distinct()
            .ToList();

        var results = new List<AllocationRunResult>(pairs.Count);
        foreach (var (variantUuid, warehouseUuid) in pairs)
            results.Add(await _engine.AllocateAsync(variantUuid, warehouseUuid, userId, ct));
        return results;
    }
    public Task<ProductionReadinessModel?> GetReadinessAsync(Guid uuid) => _repo.GetReadinessAsync(uuid);
    public Task<IReadOnlyList<MaterialShortageModel>> GetShortagesAsync(Guid? warehouseUuid) => _repo.GetShortagesAsync(warehouseUuid);
    public Task<PaginatedResponse<SupplyRequirementModel>> GetSupplyRequirementsAsync(SupplyRequirementListFilter filter) => _repo.GetSupplyRequirementsAsync(filter);
    public Task<SupplyRequirementModel?> GetSupplyRequirementAsync(Guid uuid) => _repo.GetSupplyRequirementAsync(uuid);
    public Task<IReadOnlyList<SupplyRequirementModel>> GetSupplyRequirementsForOrderAsync(Guid productionOrderUuid) => _repo.GetSupplyRequirementsForOrderAsync(productionOrderUuid);
    public Task<IReadOnlyList<ProductionIssueModel>> GetIssuesForOrderAsync(Guid productionOrderUuid) => _repo.GetIssuesForOrderAsync(productionOrderUuid);

    // ── Supply requirements — the manual side ────────────────────────────────

    public Task<Guid> CreateSupplyRequirementAsync(CreateSupplyRequirementRequest req, int userId, CancellationToken ct = default) =>
        _supplyEngine.CreateManualAsync(req, userId, ct);

    public Task CancelSupplyRequirementAsync(Guid uuid, CancelSupplyRequirementRequest req, int userId, CancellationToken ct = default) =>
        _supplyEngine.CancelAsync(uuid, req?.Reason ?? string.Empty, userId, ct);

    // ── Planning (BR-PR03) ────────────────────────────────────────────────────

    public async Task<ProductionReadinessModel> PlanAsync(Guid uuid, int userId, CancellationToken ct = default)
    {
        var result = await RetryOnConcurrencyAsync(async () =>
        {
            var po = await _repo.LoadAsync(uuid);
            if (po.Status != ProductionOrderStatus.Draft)
                throw new BadRequestException($"Production order {po.ProductionNumber} is {Describe(po.Status)}; only a draft can be planned.");
            await PlanCoreAsync(po, userId, depth: 0, ct);
            return po.UUID;
        }, ct);

        return (await _repo.GetReadinessAsync(result))!;
    }

    /// <summary>
    /// Explodes the recipe into requirements (net = line qty × planned ÷ BOM base, plus scrap
    /// allowance), registers each as demand, allocates per material, then raises supply for
    /// whatever the run left short. Shared by an explicit plan and a create-and-plan in one call,
    /// and by <see cref="CreateChildForSupplyAsync"/> planning a chained order at its own depth.
    /// </summary>
    private async Task PlanCoreAsync(ProductionOrder po, int userId, int depth, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var materialRequiredDate = po.PlannedStartDate ?? po.RequiredDate;

        foreach (var line in po.Bom.Lines.OrderBy(l => l.Sequence))
        {
            var net    = po.Bom.BaseQuantity > 0 ? line.Quantity * po.PlannedQuantity / po.Bom.BaseQuantity : line.Quantity * po.PlannedQuantity;
            var scrap  = decimal.Round(net * line.ScrapPercentage / 100m, 6);
            po.Materials.Add(new ProductionMaterialRequirement
            {
                BomLineId           = line.Id,
                Sequence            = line.Sequence,
                MaterialProductUuid = line.MaterialProductUuid,
                MaterialVariantUuid = line.MaterialVariantUuid,
                NetQuantity         = net,
                ScrapAllowance      = scrap,
                RequiredQuantity    = decimal.Round(net + scrap, 6),
                Uom                 = line.Uom,
                // A31-C6/BR-C6-01 — BOM lines no longer carry their own warehouse override; every
                // material requirement sources from the production order's own warehouse.
                WarehouseUuid       = po.WarehouseUuid,
                IsCritical          = line.IsCritical,
                Status              = PmrStatus.Pending,
                RequiredDate        = materialRequiredDate,
                CreatedAt           = now,
                UpdatedAt           = now
            });
        }

        po.Status        = ProductionOrderStatus.Planned;
        po.UpdatedAt      = now;
        await _repo.SaveAsync();

        foreach (var pmr in po.Materials)
        {
            var demand = await _engine.RegisterDemandAsync(new AllocationDemandRegistration(
                AllocationDemandType.ProductionMaterial, po.UUID, pmr.UUID, po.ProductionNumber,
                pmr.MaterialVariantUuid, pmr.WarehouseUuid, pmr.RequiredQuantity, pmr.RequiredDate, po.Priority, po.CreatedAt), userId, ct);
            pmr.AllocationDemandUuid = demand.Uuid;
        }
        await _repo.SaveAsync();

        EnqueueTimeline(po, ManufacturingTimelineEventTypes.ProdPlanned, userId,
            $"{po.Materials.Count} material{(po.Materials.Count == 1 ? "" : "s")} exploded from {po.Bom.BomNumber} v{po.BomVersion}.");

        foreach (var variantUuid in po.Materials.Select(m => m.MaterialVariantUuid).Distinct())
            await _engine.AllocateAsync(variantUuid, null, userId, ct);

        // The listener above already applied readiness from what the run decided; what is left
        // short after that is what genuinely needs a purchase or a child order.
        var shortMaterials = po.Materials.Where(m => m.Status != PmrStatus.Cancelled && m.ShortageQuantity > 0).ToList();
        foreach (var pmr in shortMaterials)
            await _supplyEngine.EnsureForShortageAsync(pmr, po, userId, depth, ct);

        if (po.MaterialReadiness == MaterialReadiness.Partial || po.MaterialReadiness == MaterialReadiness.Shortage)
            EnqueueTimeline(po, ManufacturingTimelineEventTypes.ProdMaterialPending, userId,
                $"{shortMaterials.Count} material line(s) short.");

        // A30-P5-01 — one alert per material still short after planning's own allocation run.
        if (_notify is not null && shortMaterials.Count > 0)
        {
            var names = await _inv.Products.AsNoTracking()
                .Where(p => shortMaterials.Select(m => m.MaterialProductUuid).Contains(p.Uuid))
                .ToDictionaryAsync(p => p.Uuid, p => p.Name, ct);
            foreach (var pmr in shortMaterials)
                await _notify.ShortageAlertAsync(po, names.GetValueOrDefault(pmr.MaterialProductUuid, pmr.MaterialProductUuid.ToString()), pmr.ShortageQuantity, pmr.Uom);
        }
    }

    /// <summary>
    /// Raised and planned by <see cref="ISupplyRequirementEngine"/> for a manufactured shortage.
    /// Not on <see cref="IProductionOrderService"/> — that interface is public (the controllers take
    /// it) and cannot expose the internal <see cref="ProductionOrder"/> or <see cref="SupplyRequirement"/>
    /// types, so the engine resolves this concrete class from the container instead, which avoids a
    /// constructor cycle between the two services. <paramref name="depth"/> is the child's own place
    /// in the chain.
    /// </summary>
    internal async Task<ProductionOrder> CreateChildForSupplyAsync(
        SupplyRequirement sr, int? parentProductionOrderId, int userId, int depth, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sr);

        var child = await _repo.CreateAsync(new CreateProductionOrderRequest
        {
            ProductUuid        = sr.ProductUuid,
            ProductVariantUuid = sr.VariantUuid,
            PlannedQuantity    = sr.QuantityRequired,
            WarehouseUuid      = sr.WarehouseUuid,
            RequiredDate       = sr.RequiredDate,
            Priority           = sr.Priority,
            SourceType         = ProductionSourceType.SupplyRequirement,
            SourceUuid         = sr.UUID,
            SourceReference    = sr.SupplyNumber,
            Notes              = $"Raised automatically to cover {sr.DemandReference ?? sr.SupplyNumber} ({sr.SupplyNumber})."
        }, userId, parentProductionOrderId, traceId: sr.TraceId);

        EnqueueTimeline(child, ManufacturingTimelineEventTypes.ProdCreated, userId, child.Notes);
        if (_notify is not null)
        {
            var materialName = await ProductNameAsync(sr.ProductUuid, ct);
            var parent = parentProductionOrderId is { } parentId ? await _repo.LoadAsync(parentId) : null;
            if (parent is not null) await _notify.ChainedProductionOrderCreatedAsync(parent, child, materialName);
            else await _notify.ProductionOrderCreatedAsync(child, materialName, userId);
        }
        // See CreateAsync's own note: CreateAsync's INSERT never populates Bom, which PlanCoreAsync needs.
        child = await _repo.LoadAsync(child.UUID);
        await PlanCoreAsync(child, userId, depth, ct);
        return child;
    }

    // ── IProductionDemandService (Phase 4 Track C) ──────────────────────────────

    public async Task<Guid> EnsureForSourceAsync(
        Guid variantUuid, decimal quantity, DateTime requiredDate, int priority,
        string sourceType, Guid sourceUuid, Guid? sourceLineUuid, string? sourceReference,
        int userId, Guid? traceId = null, CancellationToken ct = default)
    {
        if (quantity <= 0)
            throw new BadRequestException("Quantity must be greater than zero.");

        var productUuid = await _inv.ProductVariants.AsNoTracking()
            .Where(v => v.Uuid == variantUuid).Select(v => (Guid?)v.Product.Uuid).FirstOrDefaultAsync(ct)
            ?? throw new BadRequestException($"Variant {variantUuid} does not exist.");

        var existing = await _db.ProductionOrders.FirstOrDefaultAsync(p =>
            p.SourceType == sourceType && p.SourceUuid == sourceUuid && p.SourceLineUuid == sourceLineUuid &&
            p.Status != ProductionOrderStatus.Cancelled, ct);

        if (existing is null)
            return await CreateForSourceAsync(productUuid, variantUuid, quantity, requiredDate, priority, sourceType, sourceUuid, sourceLineUuid, sourceReference, userId, traceId, ct);

        if (quantity <= existing.PlannedQuantity)
            return existing.UUID;

        // The source's own need grew since this order was raised. Only a still-DRAFT order's
        // quantity can change here — anything already planned keeps its snapshot (§11.4), so the
        // extra becomes a further order against the same source rather than silently growing a
        // plan that already committed materials.
        if (existing.Status == ProductionOrderStatus.Draft)
        {
            existing.PlannedQuantity = quantity;
            existing.UpdatedAt       = DateTime.UtcNow;
            await _repo.SaveAsync();
            return existing.UUID;
        }

        return await CreateForSourceAsync(
            productUuid, variantUuid, quantity - existing.PlannedQuantity, requiredDate, priority,
            sourceType, sourceUuid, sourceLineUuid, sourceReference, userId, traceId, ct);
    }

    private async Task<Guid> CreateForSourceAsync(
        Guid productUuid, Guid variantUuid, decimal quantity, DateTime requiredDate, int priority,
        string sourceType, Guid sourceUuid, Guid? sourceLineUuid, string? sourceReference, int userId,
        Guid? traceId, CancellationToken ct)
    {
        var po = await _repo.CreateAsync(new CreateProductionOrderRequest
        {
            ProductUuid        = productUuid,
            ProductVariantUuid = variantUuid,
            PlannedQuantity    = quantity,
            RequiredDate       = requiredDate,
            Priority           = priority,
            SourceType         = sourceType,
            SourceUuid         = sourceUuid,
            SourceLineUuid     = sourceLineUuid,
            SourceReference    = sourceReference,
            Notes              = sourceReference is null ? null : $"Raised automatically to cover {sourceReference}."
        }, userId, traceId: traceId);

        EnqueueTimeline(po, ManufacturingTimelineEventTypes.ProdCreated, userId, $"Raised automatically to cover {sourceReference ?? sourceType}.");
        if (_notify is not null) await _notify.ProductionOrderCreatedAsync(po, await ProductNameAsync(productUuid, ct), userId);
        // See CreateAsync's own note: CreateAsync's INSERT never populates Bom, which PlanCoreAsync needs.
        await PlanCoreAsync(await _repo.LoadAsync(po.UUID), userId, depth: 0, ct);
        return po.UUID;
    }

    // ── Execution (§17) ───────────────────────────────────────────────────────

    public Task StartAsync(Guid uuid, int userId, CancellationToken ct = default) =>
        RetryOnConcurrencyAsync(async () =>
        {
            var po = await _repo.LoadAsync(uuid);
            if (po.Status != ProductionOrderStatus.Ready)
                throw new BadRequestException($"Production order {po.ProductionNumber} is {Describe(po.Status)}; only a ready order can be started.");

            po.Status          = ProductionOrderStatus.InProgress;
            po.ActualStartDate = DateTime.UtcNow;
            po.UpdatedAt       = DateTime.UtcNow;
            await _repo.SaveAsync();
            EnqueueTimeline(po, ManufacturingTimelineEventTypes.ProdStarted, userId, null);
        }, ct);

    public Task ReportOutputAsync(Guid uuid, ReportOutputRequest req, int userId, CancellationToken ct = default) =>
        RetryOnConcurrencyAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(req);
            if (req.Quantity <= 0)
                throw new BadRequestException("Reported quantity must be greater than zero.");

            var po = await _repo.LoadAsync(uuid);
            if (po.Status != ProductionOrderStatus.InProgress)
                throw new BadRequestException($"Production order {po.ProductionNumber} is {Describe(po.Status)}; only an order in progress can report output.");
            // BR-PR06/07 — the floor cannot report more than the order asked for; a real over-run
            // is a new or amended order, not a bigger number on this one.
            if (po.ProducedQuantity + req.Quantity > po.PlannedQuantity)
                throw new BadRequestException(
                    $"Reporting {req.Quantity:0.####} would take produced quantity to {po.ProducedQuantity + req.Quantity:0.####}, more than the {po.PlannedQuantity:0.####} planned.");

            po.ProducedQuantity += req.Quantity;
            if (!string.IsNullOrWhiteSpace(req.Notes)) po.Notes = AppendNote(po.Notes, req.Notes.Trim());
            po.UpdatedAt = DateTime.UtcNow;
            await _repo.SaveAsync();
        }, ct);

    public Task CompleteAsync(Guid uuid, int userId, CancellationToken ct = default) =>
        RetryOnConcurrencyAsync(async () =>
        {
            var po = await _repo.LoadAsync(uuid);
            if (po.Status != ProductionOrderStatus.InProgress)
                throw new BadRequestException($"Production order {po.ProductionNumber} is {Describe(po.Status)}; only an order in progress can be completed.");
            if (po.ProducedQuantity <= 0)
                throw new BadRequestException("Report output before completing this order.");

            po.Status        = ProductionOrderStatus.QualityInspection;
            po.ActualEndDate = DateTime.UtcNow;
            po.UpdatedAt     = DateTime.UtcNow;
            await _repo.SaveAsync();
            EnqueueTimeline(po, ManufacturingTimelineEventTypes.ProdCompleted, userId, $"{po.ProducedQuantity:0.####} produced; sent to quality inspection.");
            if (_notify is not null) await _notify.QualityInspectionRequiredAsync(po);
        }, ct);

    public Task CancelAsync(Guid uuid, CancelProductionOrderRequest req, int userId, CancellationToken ct = default) =>
        RetryOnConcurrencyAsync(async () =>
        {
            ArgumentNullException.ThrowIfNull(req);
            if (string.IsNullOrWhiteSpace(req.Reason))
                throw new BadRequestException("A reason is required to cancel a production order.");

            var po = await _repo.LoadAsync(uuid);
            if (!ProductionOrderStatus.CanCancel(po.Status))
                throw new BadRequestException($"Production order {po.ProductionNumber} is {Describe(po.Status)} and cannot be cancelled.");

            foreach (var pmr in po.Materials.Where(m => m.Status != PmrStatus.Cancelled))
            {
                if (pmr.AllocationDemandUuid is { } demandUuid)
                    await _engine.CancelDemandAsync(demandUuid, $"Production order cancelled: {req.Reason}", userId, ct);
                pmr.Status    = PmrStatus.Cancelled;
                pmr.UpdatedAt = DateTime.UtcNow;
            }

            var pmrUuids = po.Materials.Select(m => m.UUID).ToList();
            var liveSupply = await _db.SupplyRequirements
                .Where(s => s.DemandSourceType == SupplyDemandSourceType.ProductionMaterialRequirement &&
                            pmrUuids.Contains(s.DemandSourceUuid) && SupplyRequirementStatus.LiveStatuses.Contains(s.Status))
                .Select(s => s.UUID)
                .ToListAsync(ct);
            foreach (var srUuid in liveSupply)
                await _supplyEngine.CancelAsync(srUuid, $"Production order cancelled: {req.Reason}", userId, ct);

            po.Status    = ProductionOrderStatus.Cancelled;
            po.Notes     = AppendNote(po.Notes, $"Cancelled: {req.Reason.Trim()}");
            po.UpdatedAt = DateTime.UtcNow;
            await _repo.SaveAsync();
        }, ct);

    // ── Pieces ────────────────────────────────────────────────────────────────

    private static string Describe(string status) => status.ToLowerInvariant().Replace('_', ' ');

    private static string AppendNote(string? existing, string note) =>
        string.IsNullOrWhiteSpace(existing) ? note : $"{existing}\n{note}";

    /// <summary>Same shape as StockReservationService/AllocationEngine's retry: a losing writer forgets what it read and tries again.</summary>
    private async Task<T> RetryOnConcurrencyAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
            {
                ForgetTrackedState();
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(20, 60) * attempt), ct);
            }
        }
    }

    private Task RetryOnConcurrencyAsync(Func<Task> operation, CancellationToken ct) =>
        RetryOnConcurrencyAsync(async () => { await operation(); return true; }, ct);

    private void ForgetTrackedState()
    {
        foreach (var entry in _db.ChangeTracker.Entries().ToList())
            if (entry.Entity is ProductionOrder or ProductionMaterialRequirement)
                entry.State = EntityState.Detached;
    }
}
