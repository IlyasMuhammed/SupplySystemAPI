using System.Globalization;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Services;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Jobs;
using SMS.WorkflowEngine.Models;

namespace SMS.Modules.Material.Services;

/// <summary>
/// A36 — a service order end to end, on production's template: plan snapshots the active service BOM and explodes it
/// per source type (D-6), STOCK requirements become SERVICE_ORDER allocation demands, SUBCONTRACT ones supply
/// requirements bought from the BOM line's vendor; <see cref="ServiceReadinessListener"/> keeps readiness current after
/// every allocation run. Start issues everything held (D-7), completion returns the unused rest and writes the ledger in
/// the same transaction (D-8, D-9), cancel undoes all of it (D-16). Also <see cref="IServiceOrderDemandService"/> (D-10).
/// <para>
/// Collaborators are resolved when used (the <see cref="SaleOrderProductionService"/> arrangement): Demand's sale order
/// service takes the shared interface, and the supply/purchase graph behind planning must never close a DI cycle back.
/// </para>
/// </summary>
internal sealed partial class ServiceOrderService : IServiceOrderService, IServiceOrderDemandService
{
    private readonly MaterialDbContext  _db;
    private readonly InventoryDbContext _inv;
    private readonly IServiceProvider   _services;
    private readonly ILogger<ServiceOrderService>? _log;

    public ServiceOrderService(MaterialDbContext db, InventoryDbContext inv, IServiceProvider services, ILogger<ServiceOrderService>? log = null)
    {
        _db       = db;
        _inv      = inv;
        _services = services;
        _log      = log;
    }

    private IAllocationEngine        Engine  => _services.GetRequiredService<IAllocationEngine>();
    private ISupplyRequirementEngine Supply  => _services.GetRequiredService<ISupplyRequirementEngine>();
    private IInventoryLedgerService  Ledger  => _services.GetRequiredService<IInventoryLedgerService>();
    private IDocumentNumberGenerator Numbers => _services.GetRequiredService<IDocumentNumberGenerator>();
    private IBackgroundJobClient?    Jobs    => _services.GetService<IBackgroundJobClient>();

    // ── Create / update ───────────────────────────────────────────────────────────────────────────────────

    public async Task<Guid> CreateAsync(CreateServiceOrderRequest req, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        var product = await CatalogProductReader.FindAsync(_inv, req.ServiceProductUuid, ct)
            ?? throw new BadRequestException($"Service product {req.ServiceProductUuid} does not exist.");
        await EnsureCustomerAsync(req.CustomerUuid, ct);
        await EnsureAssigneeAsync(req.AssignedUserId, req.AssignedRoleId, ct);

        var priority = req.Priority ?? AllocationPriority.Normal;
        var date     = ParseDate(req.ScheduledDate);
        var order = await NewOrderAsync(product, req.ServiceVariantUuid, req.Quantity, req.WarehouseUuid, req.CustomerUuid, priority,
            date, ParseTime(req.ScheduledTime, date), req.EstimatedHours, ServiceOrderSource.Manual, null, null, null, null, userId, ct);
        order.AssignedUserId = req.AssignedUserId;
        order.AssignedRoleId = req.AssignedRoleId;
        order.Notes          = Trim(req.Notes);

        _db.ServiceOrders.Add(order);
        await _db.SaveChangesAsync(ct);
        Timeline(order, ServiceTimelineEventTypes.Created, userId, order.Notes);
        return order.UUID;
    }

    /// <summary>The validated new DRAFT (D-5): a service product's variant, invoicing copied from the product.</summary>
    private async Task<ServiceOrder> NewOrderAsync(
        CatalogProductFacts product, Guid? variantUuid, decimal quantity, Guid? warehouseUuid, Guid customerUuid, int priority,
        DateTime? scheduledDate, TimeSpan? scheduledTime, decimal? estimatedHours, string sourceType, Guid? sourceUuid,
        Guid? sourceLineUuid, string? sourceReference, Guid? traceId, int userId, CancellationToken ct)
    {
        if (!product.IsService)
            throw new BadRequestException($"{product.Name} is not a service product; a service order needs a product of type SERVICE.");
        if (!product.IsActive)
            throw new BadRequestException($"Service product {product.Name} is inactive.");
        if (quantity <= 0)
            throw new BadRequestException("Quantity must be greater than zero.");
        if (customerUuid == Guid.Empty)
            throw new BadRequestException("A customer is required.");
        if (!AllocationPriority.IsKnown(priority))
            throw new BadRequestException("Priority must be between 0 (low) and 3 (urgent).");
        if (estimatedHours is <= 0)
            throw new BadRequestException("Estimated hours must be greater than zero.");

        var org       = product.OrganizationId;
        var variant   = await ServiceVariantAsync(product, variantUuid, ct);
        var warehouse = warehouseUuid ?? await DefaultWarehouseAsync(product.Uuid, org, ct)
            ?? throw new BadRequestException(
                "Your organization has no active warehouse to draw service materials from. Create a warehouse (or set a default production warehouse on the service product) first.");
        await EnsureWarehouseAsync(warehouse, org, ct);

        var now = DateTime.UtcNow;
        return new ServiceOrder
        {
            OrganizationId     = org,
            TraceId            = traceId ?? Guid.NewGuid(),
            ServiceNumber      = await Numbers.NextAsync(ServiceDocumentPrefix.ServiceOrder, now, org, ct),
            ServiceProductUuid = product.Uuid,
            ServiceVariantUuid = variant,
            CustomerUuid       = customerUuid,
            Quantity           = quantity,
            WarehouseUuid      = warehouse,
            ScheduledDate      = scheduledDate,
            ScheduledTime      = scheduledTime,
            EstimatedHours     = estimatedHours ?? (product.EstimatedDurationHours is { } h ? decimal.Round(h * quantity, 2) : null),
            SourceType         = sourceType,
            SourceUuid         = sourceUuid,
            SourceLineUuid     = sourceLineUuid,
            SourceReference    = Trim(sourceReference),
            InvoicingPolicy    = product.ServiceInvoicingPolicy ?? ServiceInvoicingPolicy.FixedPrice,
            BillingModel       = product.ServiceBillingModel ?? ServiceBillingModel.Inclusive,
            Priority           = priority,
            Status             = ServiceOrderStatus.Draft,
            MaterialReadiness  = ServiceReadinessCode.NotChecked,
            CreatedBy          = userId,
            CreatedAt          = now,
            UpdatedAt          = now
        };
    }

    public Task<ServiceOrderDetailModel> UpdateAsync(Guid uuid, UpdateServiceOrderRequest req, int userId, CancellationToken ct = default) =>
        GuardedAsync(uuid, async () =>
        {
            ArgumentNullException.ThrowIfNull(req);
            var order = await LoadAsync(uuid, ct);
            if (req.RowVersion is null)
                throw new BadRequestException("rowVersion is required.");
            byte[] rowVersion;
            try { rowVersion = Convert.FromBase64String(req.RowVersion); }
            catch (FormatException) { throw new BadRequestException("rowVersion is not valid base64."); }
            if (!rowVersion.AsSpan().SequenceEqual(order.RowVersion))
                throw new DbUpdateConcurrencyException("Stale row version.");
            _db.Entry(order).Property(o => o.RowVersion).OriginalValue = rowVersion;

            if (order.Status == ServiceOrderStatus.Cancelled)
                throw new BadRequestException($"Service order {order.ServiceNumber} is cancelled and cannot be edited.");

            // SVC-12 — after planning only the notes change; DRAFT/PLANNED take the whole body, null clearing.
            if (order.Status is ServiceOrderStatus.Draft or ServiceOrderStatus.Planned)
            {
                if (req.Quantity <= 0) throw new BadRequestException("Quantity must be greater than zero.");
                if (!AllocationPriority.IsKnown(req.Priority)) throw new BadRequestException("Priority must be between 0 (low) and 3 (urgent).");
                if (req.EstimatedHours is <= 0) throw new BadRequestException("Estimated hours must be greater than zero.");
                if (order.Status == ServiceOrderStatus.Planned && (req.Quantity != order.Quantity || req.WarehouseUuid != order.WarehouseUuid))
                    throw new BadRequestException("Quantity and warehouse are fixed once the service order is planned.");
                if (req.CustomerUuid != order.CustomerUuid) await EnsureCustomerAsync(req.CustomerUuid, ct);
                if (req.WarehouseUuid != order.WarehouseUuid) await EnsureWarehouseAsync(req.WarehouseUuid, order.OrganizationId, ct);
                if (req.AssignedUserId != order.AssignedUserId || req.AssignedRoleId != order.AssignedRoleId)
                    await EnsureAssigneeAsync(req.AssignedUserId, req.AssignedRoleId, ct);

                var date = ParseDate(req.ScheduledDate);
                order.CustomerUuid   = req.CustomerUuid;
                order.Quantity       = req.Quantity;
                order.WarehouseUuid  = req.WarehouseUuid;
                order.AssignedUserId = req.AssignedUserId;
                order.AssignedRoleId = req.AssignedRoleId;
                order.ScheduledDate  = date;
                order.ScheduledTime  = ParseTime(req.ScheduledTime, date);
                order.EstimatedHours = req.EstimatedHours;
                order.Priority       = req.Priority;
            }

            order.Notes     = Trim(req.Notes);
            order.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }, ct);

    // ── Plan (ST-01..04, D-6) ─────────────────────────────────────────────────────────────────────────────

    public Task<ServiceOrderDetailModel> PlanAsync(Guid uuid, int userId, CancellationToken ct = default) =>
        GuardedAsync(uuid, async () =>
        {
            var order = await LoadAsync(uuid, ct);
            if (order.Status != ServiceOrderStatus.Draft)
                throw new BadRequestException($"Service order {order.ServiceNumber} is {Describe(order.Status)}; only a draft can be planned.");
            if (order.AssignedUserId is null && order.AssignedRoleId is null)
                throw new BadRequestException("Assign a technician or a team before planning this service order.");

            var product = (await CatalogProductReader.GetAsync(_inv, order.OrganizationId, [order.ServiceProductUuid], ct))
                .GetValueOrDefault(order.ServiceProductUuid)
                ?? throw new BadRequestException($"Service product {order.ServiceProductUuid} no longer exists.");

            // SVC-02 / D-3 — the active service BOM is snapshotted now; none (or the flag off) plans as an ad-hoc service.
            BillOfMaterial? bom = null;
            if (product.HasServiceBom &&
                (await ActiveBomResolver.ResolveAsync(_db, _inv, order.OrganizationId, [order.ServiceVariantUuid], includeLines: true, ct))
                    .TryGetValue(order.ServiceVariantUuid, out var resolved))
                bom = resolved.Bom;

            var now          = DateTime.UtcNow;
            var requiredDate = (order.ScheduledDate ?? now).Date;
            if (bom is not null)
            {
                order.BomId      = bom.Id;
                order.BomVersion = bom.Version;
                foreach (var line in bom.Lines.OrderBy(l => l.Sequence).ThenBy(l => l.Id))
                    order.Materials.Add(Explode(order, bom, line, requiredDate, now));
            }
            order.Status    = ServiceOrderStatus.Planned;
            order.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);

            await RegisterDemandsAsync(order, order.Materials, userId, ct);
            Timeline(order, ServiceTimelineEventTypes.Planned, userId, bom is null
                ? "No service BOM; planned without materials."
                : $"{order.Materials.Count} requirement{(order.Materials.Count == 1 ? "" : "s")} exploded from {bom.BomNumber} v{bom.Version}.");

            await AllocateStockAsync(order, order.Materials, userId, ct);
            await EnsureSupplyAsync(order, order.Materials, userId, ct);
            await RefreshAsync(order, ct);
            await _db.SaveChangesAsync(ct);
        }, ct);

    /// <summary>FSD §6.4 — net = line × quantity ÷ base; scrap only on STOCK; labour never critical, never short.</summary>
    private static ServiceMaterialRequirement Explode(ServiceOrder order, BillOfMaterial bom, BillOfMaterialLine line, DateTime requiredDate, DateTime now)
    {
        var net      = bom.BaseQuantity > 0 ? line.Quantity * order.Quantity / bom.BaseQuantity : line.Quantity * order.Quantity;
        var stock    = line.SourceType == BomLineSourceType.Stock;
        var labor    = line.SourceType == BomLineSourceType.InternalLabor;
        var scrap    = stock ? decimal.Round(net * line.ScrapPercentage / 100m, 6) : 0m;
        var required = decimal.Round(net + scrap, 6);
        return new ServiceMaterialRequirement
        {
            OrganizationId          = order.OrganizationId,
            BomLineId               = line.Id,
            Sequence                = line.Sequence,
            SourceType              = line.SourceType,
            MaterialProductUuid     = line.MaterialProductUuid,
            MaterialVariantUuid     = line.MaterialVariantUuid,
            NetQuantity             = net,
            ScrapAllowance          = scrap,
            RequiredQuantity        = required,
            ShortageQuantity        = labor ? 0m : required,
            Uom                     = line.Uom,
            WarehouseUuid           = order.WarehouseUuid,
            IsCritical              = !labor && line.IsCritical,
            SubcontractSupplierUuid = line.SourceType == BomLineSourceType.Subcontract ? line.SubcontractSupplierUuid : null,
            Status                  = SmrStatus.Pending,
            RequiredDate            = requiredDate,
            CreatedAt               = now,
            UpdatedAt               = now
        };
    }

    private async Task RegisterDemandsAsync(ServiceOrder order, IEnumerable<ServiceMaterialRequirement> smrs, int userId, CancellationToken ct)
    {
        var any = false;
        foreach (var smr in smrs.Where(m => m.IsStock && m.AllocationDemandUuid is null && m.RequiredQuantity > 0))
        {
            var demand = await Engine.RegisterDemandAsync(new AllocationDemandRegistration(
                AllocationDemandType.ServiceOrder, order.UUID, smr.UUID, order.ServiceNumber, smr.MaterialVariantUuid,
                smr.WarehouseUuid, smr.RequiredQuantity, smr.RequiredDate, order.Priority, order.CreatedAt), userId, ct);
            smr.AllocationDemandUuid = demand.Uuid;
            any = true;
        }
        if (any) await _db.SaveChangesAsync(ct);
    }

    /// <summary>One run per material variant; <see cref="ServiceReadinessListener"/> writes what it decided onto the requirements.</summary>
    private async Task AllocateStockAsync(ServiceOrder order, IEnumerable<ServiceMaterialRequirement> smrs, int userId, CancellationToken ct)
    {
        foreach (var smr in smrs.Where(m => m.IsStock && ServiceReadiness.IsLive(m) && !SmrStatus.IsFinal(m.Status) &&
                                            m.AllocationDemandUuid is not null && m.Outstanding > 0)
                                .DistinctBy(m => m.MaterialVariantUuid).ToList())
            await Engine.AllocateForDemandAsync(smr.AllocationDemandUuid!.Value, userId, ct);
    }

    /// <summary>SVC-SR-01/02 — subcontracted lines are bought; what allocation left a stock line short of is raised (idempotent).</summary>
    private async Task EnsureSupplyAsync(ServiceOrder order, IEnumerable<ServiceMaterialRequirement> smrs, int userId, CancellationToken ct)
    {
        foreach (var smr in smrs.Where(m => m.SourceType != BomLineSourceType.InternalLabor && ServiceReadiness.IsLive(m) &&
                                            !SmrStatus.IsFinal(m.Status)).ToList())
            await Supply.EnsureForServiceAsync(smr, order, userId, ct);
    }

    /// <summary>Re-reads what the engine holds for each STOCK requirement and each supply requirement's status, then the order.</summary>
    private async Task RefreshAsync(ServiceOrder order, CancellationToken ct)
    {
        foreach (var smr in order.Materials.Where(m => m.IsStock && ServiceReadiness.IsLive(m) && !SmrStatus.IsFinal(m.Status)))
            ServiceReadiness.ApplyStock(smr, smr.AllocationDemandUuid is { } d ? await Engine.GetDemandAsync(d, ct) : null);

        var supply = await ServiceReadiness.SupplyStatusesAsync(_db, order.OrganizationId,
            order.Materials.Where(m => !m.IsStock).Select(m => m.UUID).ToList(), ct);
        foreach (var smr in order.Materials.Where(m => !m.IsStock))
            ServiceReadiness.ApplyNonStock(smr, supply.GetValueOrDefault(smr.UUID));
        ServiceReadiness.ApplyOrder(order, supply);
    }

    // ── Execution (ST-05..07, D-7, D-18) ──────────────────────────────────────────────────────────────────

    public Task<ServiceOrderDetailModel> StartAsync(Guid uuid, int userId, CancellationToken ct = default) =>
        GuardedAsync(uuid, async () =>
        {
            var order = await LoadAsync(uuid, ct);
            if (order.Status != ServiceOrderStatus.Ready)
                throw new BadRequestException($"Service order {order.ServiceNumber} is {Describe(order.Status)}; only a ready service order can be started.");

            // TS-10 — one issue for everything held.
            var issue = await NewIssueIfHeldAsync(order, order.Materials, ServiceIssueType.Issue, userId, ct);
            await MaterialStockMovements.RunAcrossContextsAsync(_db, _inv, async () =>
            {
                if (issue is not null) await IssueHeldAsync(order, issue, order.Materials, userId, ct);
                order.Status          = ServiceOrderStatus.InProgress;
                order.ActualStartDate = DateTime.UtcNow;
                await RefreshAsync(order, ct);
            }, ct);

            Timeline(order, ServiceTimelineEventTypes.Started, userId, null);
            TimelineIssued(order, issue, userId);
            TimelineIfWaiting(order, ServiceOrderStatus.InProgress, userId);
        }, ct);

    public Task<ServiceOrderDetailModel> AddMaterialAsync(Guid uuid, AddAdhocMaterialRequest req, int userId, CancellationToken ct = default) =>
        GuardedAsync(uuid, async () =>
        {
            ArgumentNullException.ThrowIfNull(req);
            var order = await LoadAsync(uuid, ct);
            if (!ServiceOrderStatus.IsRunning(order.Status))
                throw new BadRequestException("Materials can only be added during service execution.");
            if (req.Quantity <= 0)
                throw new BadRequestException("Quantity must be greater than zero.");

            var variant = await _inv.ProductVariants.IgnoreQueryFilters().AsNoTracking()
                .Where(v => v.OrganizationId == order.OrganizationId && v.Uuid == req.VariantUuid)
                .Select(v => new { v.Uuid, v.IsActive, ProductUuid = v.Product.Uuid, v.Product.Name, v.Product.ProductType, ProductActive = v.Product.IsActive, v.Product.UomCode })
                .FirstOrDefaultAsync(ct)
                ?? throw new BadRequestException($"Variant {req.VariantUuid} does not exist.");
            if (!variant.IsActive || !variant.ProductActive)
                throw new BadRequestException($"{variant.Name} is inactive.");
            if (variant.ProductType == ProductType.Service)
                throw new BadRequestException($"{variant.Name} is a service and cannot be issued as a material.");

            var before = order.Status;
            var now    = DateTime.UtcNow;
            var smr = new ServiceMaterialRequirement
            {
                OrganizationId      = order.OrganizationId,
                Sequence            = order.Materials.Count == 0 ? 1 : order.Materials.Max(m => m.Sequence) + 1,
                SourceType          = BomLineSourceType.Stock,
                MaterialProductUuid = variant.ProductUuid,
                MaterialVariantUuid = variant.Uuid,
                NetQuantity         = req.Quantity,
                RequiredQuantity    = req.Quantity,
                ShortageQuantity    = req.Quantity,
                Uom                 = string.IsNullOrWhiteSpace(variant.UomCode) ? "PCS" : variant.UomCode.Trim(),
                WarehouseUuid       = order.WarehouseUuid,
                IsCritical          = true,
                IsAdhoc             = true,
                Status              = SmrStatus.Pending,
                RequiredDate        = now.Date,
                AddedBy             = userId,
                Notes               = Trim(req.Notes),
                CreatedAt           = now,
                UpdatedAt           = now
            };
            order.Materials.Add(smr);
            order.UpdatedAt = now;
            await _db.SaveChangesAsync(ct);

            // TS-11/12 — held and issued at once when there is stock; otherwise raised as supply and the job waits.
            await RegisterDemandsAsync(order, [smr], userId, ct);
            await AllocateStockAsync(order, [smr], userId, ct);
            var issue = await IssueAndRefreshAsync(order, [smr], userId, ct);
            await EnsureSupplyAsync(order, [smr], userId, ct);
            await RefreshAsync(order, ct);
            await _db.SaveChangesAsync(ct);

            TimelineIssued(order, issue, userId);
            TimelineIfWaiting(order, before, userId);
        }, ct);

    public Task<ServiceOrderDetailModel> RemoveMaterialAsync(Guid uuid, Guid smrUuid, int userId, CancellationToken ct = default) =>
        GuardedAsync(uuid, async () =>
        {
            var order = await LoadAsync(uuid, ct);
            var smr = order.Materials.FirstOrDefault(m => m.UUID == smrUuid)
                ?? throw new NotFoundException("Service material", smrUuid);
            if (ServiceOrderStatus.IsTerminal(order.Status))
                throw new BadRequestException($"Service order {order.ServiceNumber} is {Describe(order.Status)}; its materials can no longer change.");
            if (!smr.IsAdhoc)
                throw new BadRequestException("Only ad-hoc materials can be removed.");
            if (smr.IssuedQuantity > 0)
                throw new BadRequestException("This material was already issued and cannot be removed.");

            // D-18 / TS-32 — never issued: release its hold, drop its supply, hard delete.
            if (smr.AllocationDemandUuid is { } demand)
                await Engine.CancelDemandAsync(demand, $"Ad-hoc material removed from {order.ServiceNumber}.", userId, ct);
            await CancelSupplyAsync(order, [smr], $"Ad-hoc material removed from {order.ServiceNumber}.", userId, ct);

            order.Materials.Remove(smr);
            _db.ServiceMaterialRequirements.Remove(smr);
            await RefreshAsync(order, ct);
            await _db.SaveChangesAsync(ct);
        }, ct);

    /// <summary>
    /// 11a "Reserve all available": re-runs allocation for the STOCK demands; while running, issues what is now held.
    /// Also open IN_PROGRESS (A36-P5-10 QA): an allocation run from elsewhere (the GRN page) can reserve a waiting job's
    /// late material and the listener then resumes the job (WAITING → IN_PROGRESS, ST-07) without issuing it — this is
    /// the only action that can issue that held stock afterwards.
    /// </summary>
    public Task<ServiceOrderDetailModel> AllocateAsync(Guid uuid, int userId, CancellationToken ct = default) =>
        GuardedAsync(uuid, async () =>
        {
            var order = await LoadAsync(uuid, ct);
            if (order.Status is not (ServiceOrderStatus.Planned or ServiceOrderStatus.MaterialPending or ServiceOrderStatus.Waiting or
                                     ServiceOrderStatus.Ready or ServiceOrderStatus.InProgress))
                throw new BadRequestException($"Service order {order.ServiceNumber} is {Describe(order.Status)}; materials can only be reserved before the job starts or while it waits.");

            var before = order.Status;
            await AllocateStockAsync(order, order.Materials, userId, ct);
            ServiceMaterialIssue? issue = null;
            if (ServiceOrderStatus.IsRunning(order.Status)) issue = await IssueAndRefreshAsync(order, order.Materials, userId, ct);
            await RefreshAsync(order, ct);
            await EnsureSupplyAsync(order, order.Materials, userId, ct);
            await RefreshAsync(order, ct);
            await _db.SaveChangesAsync(ct);

            TimelineIssued(order, issue, userId);
            TimelineIfWaiting(order, before, userId);
        }, ct);

    private async Task<ServiceMaterialIssue?> IssueAndRefreshAsync(
        ServiceOrder order, IEnumerable<ServiceMaterialRequirement> smrs, int userId, CancellationToken ct)
    {
        var issue = await NewIssueIfHeldAsync(order, smrs, ServiceIssueType.Issue, userId, ct);
        if (issue is null) return null;
        await MaterialStockMovements.RunAcrossContextsAsync(_db, _inv, async () =>
        {
            await IssueHeldAsync(order, issue, smrs, userId, ct);
            await RefreshAsync(order, ct);
        }, ct);
        return issue.Lines.Count > 0 ? issue : null;
    }

    /// <summary>A numbered issue document when anything of <paramref name="smrs"/> is held — numbered before the transaction.</summary>
    private async Task<ServiceMaterialIssue?> NewIssueIfHeldAsync(
        ServiceOrder order, IEnumerable<ServiceMaterialRequirement> smrs, string type, int userId, CancellationToken ct)
    {
        if (type == ServiceIssueType.Issue && !smrs.Any(m => IssueCandidate(m) && m.ReservedQuantity > 0)) return null;
        var now = DateTime.UtcNow;
        return new ServiceMaterialIssue
        {
            OrganizationId = order.OrganizationId,
            IssueNumber    = await Numbers.NextAsync(ServiceDocumentPrefix.ServiceIssue, now, order.OrganizationId, ct),
            ServiceOrderId = order.Id,
            WarehouseUuid  = order.WarehouseUuid,
            IssueType      = type,
            CreatedBy      = userId,
            CreatedAt      = now
        };
    }

    private static bool IssueCandidate(ServiceMaterialRequirement m) =>
        m.IsStock && ServiceReadiness.IsLive(m) && !SmrStatus.IsFinal(m.Status) && m.AllocationDemandUuid is not null && m.Outstanding > 0;

    /// <summary>
    /// SVC-MI-02/05/06/07 — takes what each requirement has held for it (never more than it still needs), FIFO over its
    /// allocations, and posts SERVICE_ISSUE stock movements. Runs inside <see cref="MaterialStockMovements.RunAcrossContextsAsync"/>.
    /// </summary>
    private async Task IssueHeldAsync(ServiceOrder order, ServiceMaterialIssue issue, IEnumerable<ServiceMaterialRequirement> smrs, int userId, CancellationToken ct)
    {
        var reference = new MaterialStockMovements.Reference(InventoryTransactionType.ServiceIssue, "SERVICE_ISSUE", issue.UUID, issue.IssueNumber, userId);
        foreach (var smr in smrs.Where(IssueCandidate).ToList())
        {
            var held = (await Engine.GetAllocationsAsync(new AllocationListFilter(DemandUuid: smr.AllocationDemandUuid, Status: AllocationStatus.Active, PageSize: 200), ct))
                .Items.Where(a => a.SupplyType == AllocationSupplyType.OnHand).OrderBy(a => a.AllocatedAt).ToList();

            var remaining = Math.Min(smr.Outstanding, held.Sum(a => a.AllocatedQty - a.ConsumedQty));
            var issued    = 0m;
            var unitCost  = 0m;
            foreach (var allocation in held)
            {
                if (remaining <= 0) break;
                var take = Math.Min(remaining, allocation.AllocatedQty - allocation.ConsumedQty);
                if (take <= 0) continue;

                var consumed = await Engine.ConsumeAsync(allocation.Uuid, take, userId, ct);
                unitCost = await MaterialStockMovements.DeductOnHandAsync(_inv, Ledger, smr.MaterialVariantUuid, allocation.WarehouseUuid, consumed,
                    reference, $"Service issue {issue.IssueNumber} for {order.ServiceNumber}", ct);
                remaining -= consumed;
                issued    += consumed;
            }
            if (issued <= 0) continue;

            issue.Lines.Add(new ServiceMaterialIssueLine
            {
                OrganizationId = order.OrganizationId, RequirementId = smr.Id, MaterialVariantUuid = smr.MaterialVariantUuid,
                Quantity = issued, Uom = smr.Uom, UnitCost = unitCost
            });
            smr.IssuedQuantity += issued;
            smr.UpdatedAt       = DateTime.UtcNow;
        }
        if (issue.Lines.Count > 0 && issue.Id == 0) _db.ServiceMaterialIssues.Add(issue);
    }

    // ── Completion (§8, SVC-COMP-01..06, D-8, D-9) ────────────────────────────────────────────────────────

    public Task<ServiceOrderDetailModel> CompleteAsync(Guid uuid, CompleteServiceOrderRequest req, int userId, CancellationToken ct = default) =>
        GuardedAsync(uuid, async () =>
        {
            ArgumentNullException.ThrowIfNull(req);
            var order = await LoadAsync(uuid, ct);
            if (order.Status != ServiceOrderStatus.InProgress)
                throw new BadRequestException("Service can only be completed from In Progress status");
            if (req.ActualHours is < 0)
                throw new BadRequestException("Actual hours cannot be negative.");
            if (order.InvoicingPolicy == ServiceInvoicingPolicy.TimeAndMaterial && req.ActualHours is not > 0)
                throw new BadRequestException("Actual hours required for Time & Material billing");

            var live = order.Materials.Where(ServiceReadiness.IsLive).ToDictionary(m => m.UUID);
            var consumed = new Dictionary<Guid, decimal>();
            foreach (var line in req.ConsumedMaterials ?? [])
            {
                if (!live.TryGetValue(line.SmrUuid, out var smr))
                    throw new BadRequestException($"Material {line.SmrUuid} does not belong to this service order.");
                if (!consumed.TryAdd(line.SmrUuid, line.ConsumedQuantity))
                    throw new BadRequestException("Each material may be confirmed only once.");
                if (line.ConsumedQuantity < 0)
                    throw new BadRequestException("Consumed quantity cannot be negative");
                if (smr.IsStock && line.ConsumedQuantity > smr.IssuedQuantity - smr.ReturnedQuantity)
                    throw new BadRequestException("Consumed quantity cannot exceed issued quantity");
            }
            if (live.Values.Any(m => m.IsStock && m.IssuedQuantity > 0 && !consumed.ContainsKey(m.UUID)))
                throw new BadRequestException("All issued materials must have consumed quantity confirmed");

            var returns = live.Values
                .Where(m => m.IsStock && m.IssuedQuantity - m.ReturnedQuantity - consumed.GetValueOrDefault(m.UUID) > 0)
                .Select(m => (Smr: m, Qty: m.IssuedQuantity - m.ReturnedQuantity - consumed.GetValueOrDefault(m.UUID)))
                .ToList();
            var returnIssue = returns.Count > 0 ? await NewIssueIfHeldAsync(order, [], ServiceIssueType.Return, userId, ct) : null;

            await MaterialStockMovements.RunAcrossContextsAsync(_db, _inv, async () =>
            {
                // SVC-COMP-05 / SVC-10 — what was issued and not consumed goes back, as one RETURN.
                if (returnIssue is not null) await ReturnAsync(order, returnIssue, returns, userId, ct);

                foreach (var smr in live.Values)
                {
                    if (smr.IsStock)
                    {
                        smr.ConsumedQuantity = consumed.GetValueOrDefault(smr.UUID);
                        if (smr.AllocationDemandUuid is { } demand)
                            await Engine.CancelDemandAsync(demand, $"Service order {order.ServiceNumber} completed.", userId, ct);
                        smr.Status = smr.ConsumedQuantity > 0 ? SmrStatus.Consumed
                                   : smr.IssuedQuantity > 0   ? SmrStatus.Returned
                                                              : SmrStatus.Cancelled;
                        smr.ReservedQuantity = 0m;
                    }
                    else
                    {
                        // Labour and subcontracted work have no stock side: confirmed, or as planned.
                        smr.ConsumedQuantity = consumed.TryGetValue(smr.UUID, out var q) ? q : smr.RequiredQuantity;
                        smr.Status           = SmrStatus.Consumed;
                    }
                    smr.ShortageQuantity = 0m;
                    smr.UpdatedAt        = DateTime.UtcNow;
                }

                await WriteLedgerAsync(order, returnIssue, ct);

                order.ActualHours       = req.ActualHours;
                order.ActualEndDate     = DateTime.UtcNow;
                order.CompletionNotes   = Trim(req.CompletionNotes);
                order.CustomerSignature = req.CustomerSignature;
                order.Status            = ServiceOrderStatus.Completed;
                order.UpdatedAt         = DateTime.UtcNow;
            }, ct);

            Timeline(order, ServiceTimelineEventTypes.Completed, userId, order.CompletionNotes);
            await NotifySaleOrderAsync(order, userId, ct);
        }, ct);

    private async Task ReturnAsync(
        ServiceOrder order, ServiceMaterialIssue issue, IReadOnlyList<(ServiceMaterialRequirement Smr, decimal Qty)> returns, int userId, CancellationToken ct)
    {
        var reference = new MaterialStockMovements.Reference(InventoryTransactionType.ServiceReturn, "SERVICE_ISSUE", issue.UUID, issue.IssueNumber, userId);
        var lastCost = await _db.ServiceMaterialIssueLines.AsNoTracking()
            .Where(l => l.Issue.ServiceOrderId == order.Id && l.Issue.IssueType == ServiceIssueType.Issue)
            .GroupBy(l => l.RequirementId).Select(g => new { g.Key, Cost = g.OrderByDescending(l => l.Id).Select(l => l.UnitCost).First() })
            .ToDictionaryAsync(x => x.Key, x => x.Cost, ct);

        foreach (var (smr, qty) in returns)
        {
            var cost = await MaterialStockMovements.ReturnToStockAsync(_inv, Ledger, smr.MaterialVariantUuid, order.WarehouseUuid, qty,
                lastCost.GetValueOrDefault(smr.Id), reference, $"Return against service order {order.ServiceNumber} ({issue.IssueNumber})", ct);
            issue.Lines.Add(new ServiceMaterialIssueLine
            {
                OrganizationId = order.OrganizationId, RequirementId = smr.Id, MaterialVariantUuid = smr.MaterialVariantUuid,
                Quantity = qty, Uom = smr.Uom, UnitCost = cost
            });
            smr.ReturnedQuantity += qty;
        }
        _db.ServiceMaterialIssues.Add(issue);
    }

    /// <summary>D-9 / FSD §9.4 — one positive DEBIT per issue line, one negative per return line; net = consumed.</summary>
    private async Task WriteLedgerAsync(ServiceOrder order, ServiceMaterialIssue? returnIssue, CancellationToken ct)
    {
        var issues = await _db.ServiceMaterialIssues.AsNoTracking().Include(i => i.Lines)
            .Where(i => i.ServiceOrderId == order.Id && i.IssueType == ServiceIssueType.Issue)
            .OrderBy(i => i.Id).ToListAsync(ct);
        var rows = issues.SelectMany(i => i.Lines.OrderBy(l => l.Id).Select(l => (Issue: i, Line: l, Sign: 1m))).ToList();
        if (returnIssue is not null) rows.AddRange(returnIssue.Lines.Select(l => (Issue: returnIssue, Line: l, Sign: -1m)));
        if (rows.Count == 0) return;

        var names = await MaterialNamesAsync(order.OrganizationId, rows.Select(r => r.Line.MaterialVariantUuid).ToList(), ct);
        var warehouse = await _inv.Warehouses.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.Uuid == order.WarehouseUuid).Select(w => w.Name).FirstOrDefaultAsync(ct) ?? string.Empty;
        var now = DateTime.UtcNow;

        foreach (var (issue, line, sign) in rows)
        {
            var name = names.GetValueOrDefault(line.MaterialVariantUuid);
            var movement = sign > 0 ? InventoryTransactionType.ServiceIssue : InventoryTransactionType.ServiceReturn;
            _db.ServiceLedgerEntries.Add(new ServiceLedgerEntry
            {
                OrganizationId       = order.OrganizationId,
                TraceId              = order.TraceId,
                ServiceOrderId       = order.Id,
                ServiceNumber        = order.ServiceNumber,
                EntryType            = ServiceLedgerCodes.Debit,
                ProductUuid          = name?.ProductUuid ?? Guid.Empty,
                VariantUuid          = line.MaterialVariantUuid,
                ProductName          = name?.DisplayName ?? line.MaterialVariantUuid.ToString(),
                ProductType          = name?.ProductType ?? string.Empty,
                Quantity             = sign * line.Quantity,
                Uom                  = line.Uom,
                WarehouseUuid        = issue.WarehouseUuid,
                WarehouseName        = warehouse,
                SourceDocumentType   = movement,
                SourceDocumentUuid   = issue.UUID,
                SourceDocumentNumber = issue.IssueNumber,
                MovementType         = movement,
                TransactionDate      = issue.CreatedAt,
                CreatedAt            = now
            });
        }
    }

    // ── Close / cancel (D-13, ST-10, D-16) ────────────────────────────────────────────────────────────────

    public Task<ServiceOrderDetailModel> CloseAsync(Guid uuid, int userId, CancellationToken ct = default) =>
        GuardedAsync(uuid, async () =>
        {
            var order = await LoadAsync(uuid, ct);
            if (order.Status != ServiceOrderStatus.Completed)
                throw new BadRequestException($"Service order {order.ServiceNumber} is {Describe(order.Status)}; only a completed service order can be closed.");
            order.Status    = ServiceOrderStatus.Closed;
            order.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            Timeline(order, ServiceTimelineEventTypes.Closed, userId, null);
            await NotifySaleOrderAsync(order, userId, ct);
        }, ct);

    public Task<ServiceOrderDetailModel> CancelAsync(Guid uuid, string? reason, int userId, CancellationToken ct = default) =>
        GuardedAsync(uuid, () => CancelCoreAsync(uuid, reason, userId, notifySaleOrder: true, ct), ct);

    private async Task<ServiceOrder> CancelCoreAsync(Guid uuid, string? reason, int userId, bool notifySaleOrder, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new BadRequestException("A reason is required to cancel a service order.");
        var why   = reason.Trim();
        var order = await LoadAsync(uuid, ct);
        if (!ServiceOrderStatus.CanCancel(order.Status))
            throw new BadRequestException($"Service order {order.ServiceNumber} is {Describe(order.Status)} and cannot be cancelled.");

        // SVC-08 — open supply first (idempotent, so a failed cancel can simply be retried).
        await CancelSupplyAsync(order, order.Materials, $"Service order cancelled: {why}", userId, ct);

        // D-16 — whatever was issued comes back as one RETURN, then every hold is released.
        var returns = order.Materials
            .Where(m => m.IsStock && ServiceReadiness.IsLive(m) && m.IssuedQuantity - m.ReturnedQuantity > 0)
            .Select(m => (Smr: m, Qty: m.IssuedQuantity - m.ReturnedQuantity)).ToList();
        var returnIssue = returns.Count > 0 ? await NewIssueIfHeldAsync(order, [], ServiceIssueType.Return, userId, ct) : null;

        await MaterialStockMovements.RunAcrossContextsAsync(_db, _inv, async () =>
        {
            if (returnIssue is not null) await ReturnAsync(order, returnIssue, returns, userId, ct);
            foreach (var smr in order.Materials.Where(m => ServiceReadiness.IsLive(m) && !SmrStatus.IsFinal(m.Status)))
            {
                if (smr.AllocationDemandUuid is { } demand)
                    await Engine.CancelDemandAsync(demand, $"Service order cancelled: {why}", userId, ct);
                smr.Status           = smr.IssuedQuantity > 0 ? SmrStatus.Returned : SmrStatus.Cancelled;
                smr.ReservedQuantity = 0m;
                smr.ShortageQuantity = 0m;
                smr.UpdatedAt        = DateTime.UtcNow;
            }
            order.Status    = ServiceOrderStatus.Cancelled;
            order.Notes     = string.IsNullOrWhiteSpace(order.Notes) ? $"Cancelled: {why}" : $"{order.Notes}\nCancelled: {why}";
            order.UpdatedAt = DateTime.UtcNow;
        }, ct);

        Timeline(order, ServiceTimelineEventTypes.Cancelled, userId, why);
        if (notifySaleOrder) await NotifySaleOrderAsync(order, userId, ct);
        return order;
    }

    private async Task CancelSupplyAsync(ServiceOrder order, IEnumerable<ServiceMaterialRequirement> smrs, string reason, int userId, CancellationToken ct)
    {
        var smrUuids = smrs.Select(m => m.UUID).ToList();
        if (smrUuids.Count == 0) return;
        var live = await _db.SupplyRequirements.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.OrganizationId == order.OrganizationId && s.DemandSourceType == SupplyDemandSourceType.ServiceOrder &&
                        smrUuids.Contains(s.DemandSourceUuid) && SupplyRequirementStatus.LiveStatuses.Contains(s.Status))
            .Select(s => s.UUID).ToListAsync(ct);
        foreach (var sr in live)
            await Supply.CancelAsync(sr, reason, userId, ct);
    }

    // ── IServiceOrderDemandService (D-10) ─────────────────────────────────────────────────────────────────

    public async Task<SaleOrderServiceOrderRef> EnsureForSaleOrderLineAsync(
        Guid saleOrderUuid, Guid saleOrderLineUuid, string saleOrderNumber, Guid customerUuid,
        Guid variantUuid, decimal quantity, DateTime? scheduledDate, Guid? warehouseUuid,
        int userId, Guid? traceId = null, CancellationToken ct = default)
    {
        // Organization from the variant itself, so a retry outside a request (no ambient tenant) still stamps it right.
        var variant = await _inv.ProductVariants.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.Uuid == variantUuid)
            .Select(v => new { v.OrganizationId, ProductUuid = v.Product.Uuid })
            .FirstOrDefaultAsync(ct)
            ?? throw new BadRequestException($"Sale order {saleOrderNumber}: variant {variantUuid} does not exist.");

        var existing = await SaleOrderServiceOrders(variant.OrganizationId, saleOrderUuid).AsNoTracking()
            .Where(o => o.SourceLineUuid == saleOrderLineUuid)
            .OrderBy(o => o.Id).FirstOrDefaultAsync(ct);
        if (existing is not null) return ToRef(existing);

        var product = (await CatalogProductReader.GetAsync(_inv, variant.OrganizationId, [variant.ProductUuid], ct)).GetValueOrDefault(variant.ProductUuid)
            ?? throw new BadRequestException($"Sale order {saleOrderNumber}: the product of variant {variantUuid} does not exist.");

        var order = await NewOrderAsync(product, variantUuid, quantity, warehouseUuid, customerUuid, AllocationPriority.Normal,
            scheduledDate?.Date, null, null, ServiceOrderSource.SalesOrder, saleOrderUuid, saleOrderLineUuid, saleOrderNumber, traceId, userId, ct);
        order.Notes = $"Raised for sale order {saleOrderNumber}.";
        _db.ServiceOrders.Add(order);
        await _db.SaveChangesAsync(ct);

        Timeline(order, ServiceTimelineEventTypes.Created, userId, order.Notes);
        return ToRef(order);
    }

    public async Task<IReadOnlyList<SaleOrderServiceOrderRef>> GetForSaleOrderAsync(Guid organizationId, Guid saleOrderUuid, CancellationToken ct = default) =>
        (await SaleOrderServiceOrders(organizationId, saleOrderUuid).AsNoTracking().OrderBy(o => o.Id).ToListAsync(ct))
            .Select(ToRef).ToList();

    public async Task<SaleOrderServiceCancellation> CancelForSaleOrderAsync(Guid saleOrderUuid, string reason, int userId, CancellationToken ct = default)
    {
        var orders = await _db.ServiceOrders.IgnoreQueryFilters().AsNoTracking()
            .Where(o => o.SourceType == ServiceOrderSource.SalesOrder && o.SourceUuid == saleOrderUuid && o.Status != ServiceOrderStatus.Cancelled)
            .OrderBy(o => o.Id).ToListAsync(ct);

        var why = string.IsNullOrWhiteSpace(reason) ? "Sale order cancelled." : reason.Trim();
        var cancelled = new List<SaleOrderServiceOrderRef>();
        var running   = new List<SaleOrderServiceOrderRef>();
        foreach (var o in orders)
        {
            // Not started = before IN_PROGRESS; anything running or done is kept and reported (like production).
            if (o.Status is ServiceOrderStatus.InProgress or ServiceOrderStatus.Waiting || ServiceOrderStatus.IsTerminal(o.Status))
            {
                running.Add(ToRef(o));
                continue;
            }
            var after = await CancelCoreAsync(o.UUID, $"Sale order {o.SourceReference ?? saleOrderUuid.ToString()} cancelled: {why}", userId, notifySaleOrder: false, ct);
            cancelled.Add(ToRef(after));
        }
        return new SaleOrderServiceCancellation(cancelled, running);
    }

    private IQueryable<ServiceOrder> SaleOrderServiceOrders(Guid organizationId, Guid saleOrderUuid) =>
        _db.ServiceOrders.IgnoreQueryFilters()
           .Where(o => o.OrganizationId == organizationId && o.SourceType == ServiceOrderSource.SalesOrder && o.SourceUuid == saleOrderUuid);

    private static SaleOrderServiceOrderRef ToRef(ServiceOrder o) =>
        new(o.UUID, o.ServiceNumber, o.SourceLineUuid ?? Guid.Empty, o.Status, o.Quantity);

    /// <summary>D-10 — after the commit; the sale order recounts the line. A failure is logged, never thrown back.</summary>
    private async Task NotifySaleOrderAsync(ServiceOrder order, int userId, CancellationToken ct)
    {
        if (order.SourceType != ServiceOrderSource.SalesOrder || order.SourceUuid is not { } so || order.SourceLineUuid is not { } line) return;
        if (_services.GetService<ISaleOrderServiceFulfillmentListener>() is not { } listener) return;
        try
        {
            await listener.OnServiceOrderChangedAsync(order.OrganizationId, so, line, userId, ct);
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "Sale order {SaleOrder} was not told that service order {Number} is {Status}.", so, order.ServiceNumber, order.Status);
        }
    }

    // ── Pieces ────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<ServiceOrder> LoadAsync(Guid uuid, CancellationToken ct) =>
        await _db.ServiceOrders.Include(o => o.Materials).FirstOrDefaultAsync(o => o.UUID == uuid, ct)
        ?? throw new NotFoundException("Service order", uuid);

    /// <summary>Runs an action and answers with the fresh detail; a lost concurrency race is a 409 (SVC-13).</summary>
    private async Task<ServiceOrderDetailModel> GuardedAsync(Guid uuid, Func<Task> action, CancellationToken ct)
    {
        try
        {
            await action();
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("This service order was changed by someone else. Reload it and try again.");
        }
        return await GetAsync(uuid, ct) ?? throw new NotFoundException("Service order", uuid);
    }

    private Task<ServiceOrderDetailModel> GuardedAsync(Guid uuid, Func<Task<ServiceOrder>> action, CancellationToken ct) =>
        GuardedAsync(uuid, async () => { await action(); }, ct);

    private async Task<Guid> ServiceVariantAsync(CatalogProductFacts product, Guid? variantUuid, CancellationToken ct)
    {
        if (variantUuid is not { } wanted)
            return product.DefaultVariantUuid ?? throw new BadRequestException($"Service product {product.Name} has no default variant.");
        var variant = await _inv.ProductVariants.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.Uuid == wanted && v.ProductId == product.Id && v.OrganizationId == product.OrganizationId)
            .Select(v => new { v.IsActive, v.VariantName }).FirstOrDefaultAsync(ct)
            ?? throw new BadRequestException($"The variant does not belong to service product {product.Name}.");
        if (!variant.IsActive) throw new BadRequestException($"Variant {variant.VariantName} of {product.Name} is inactive.");
        return wanted;
    }

    /// <summary>D-10 — no warehouse given: the product's default production warehouse (the production path's default), else the organization's first active one.</summary>
    private async Task<Guid?> DefaultWarehouseAsync(Guid productUuid, Guid organizationId, CancellationToken ct) =>
        await _inv.Products.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.Uuid == productUuid && p.OrganizationId == organizationId && p.DefaultProductionWarehouse != null && p.DefaultProductionWarehouse.IsActive)
            .Select(p => (Guid?)p.DefaultProductionWarehouse!.Uuid).FirstOrDefaultAsync(ct)
        ?? await _inv.Warehouses.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.OrganizationId == organizationId && w.IsActive)
            .OrderBy(w => w.Id).Select(w => (Guid?)w.Uuid).FirstOrDefaultAsync(ct);

    private async Task EnsureWarehouseAsync(Guid warehouseUuid, Guid organizationId, CancellationToken ct)
    {
        if (!await _inv.Warehouses.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(w => w.Uuid == warehouseUuid && w.OrganizationId == organizationId && w.IsActive, ct))
            throw new BadRequestException($"Warehouse {warehouseUuid} does not exist or is inactive.");
    }

    private async Task EnsureCustomerAsync(Guid customerUuid, CancellationToken ct)
    {
        if (customerUuid == Guid.Empty) throw new BadRequestException("A customer is required.");
        if (_services.GetService<IPartnerRoleLookup>() is not { } partners) return;
        var partner = await partners.GetAsync(customerUuid, ct);
        if (partner is null || !partner.IsCustomer)
            throw new BadRequestException("The customer must be a business partner of your organization flagged as a customer.");
        if (!partner.IsActive)
            throw new BadRequestException($"Customer {partner.Name} is inactive.");
    }

    private async Task EnsureAssigneeAsync(int? userId, int? roleId, CancellationToken ct)
    {
        if (userId is { } user && _services.GetService<IUserLookupService>() is { } users && !await users.UserExistsAsync(user))
            throw new BadRequestException($"User {user} does not exist or is inactive.");
        if (roleId is { } role && _services.GetService<IRoleNameLookup>() is { } roles && !(await roles.GetNamesAsync([role], ct)).ContainsKey(role))
            throw new BadRequestException($"Team (role) {role} does not exist.");
    }

    internal static DateTime? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.Length >= 10 && DateTime.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date.Date;
        throw new BadRequestException($"'{value}' is not a date (yyyy-MM-dd).");
    }

    /// <summary>Contract note 3 — HH:mm or HH:mm:ss, and only with a date.</summary>
    internal static TimeSpan? ParseTime(string? value, DateTime? date)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (date is null) throw new BadRequestException("A scheduled time needs a scheduled date.");
        if (TimeSpan.TryParseExact(value.Trim(), [@"hh\:mm", @"hh\:mm\:ss", @"h\:mm"], CultureInfo.InvariantCulture, out var time) && time < TimeSpan.FromDays(1))
            return new TimeSpan(time.Hours, time.Minutes, 0);
        throw new BadRequestException($"'{value}' is not a time (HH:mm).");
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Describe(string status) => status.ToLowerInvariant().Replace('_', ' ');

    // ── Timeline (D-14) ───────────────────────────────────────────────────────────────────────────────────

    private void Timeline(ServiceOrder order, string eventType, int userId, string? notes)
    {
        var traceId = order.TraceId;
        var evt     = new TimelineEvent(eventType, ServiceTimelineEventTypes.InterfaceCode, order.UUID, order.ServiceNumber, DateTime.UtcNow, userId, notes);
        var number  = order.ServiceNumber;
        Jobs?.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(traceId, evt, ServiceTimelineEventTypes.InterfaceCode, number));
    }

    private void TimelineIssued(ServiceOrder order, ServiceMaterialIssue? issue, int userId)
    {
        if (issue is { Lines.Count: > 0 })
            Timeline(order, ServiceTimelineEventTypes.MaterialIssued, userId,
                $"{issue.IssueNumber}: {issue.Lines.Count} material line{(issue.Lines.Count == 1 ? "" : "s")} issued.");
    }

    private void TimelineIfWaiting(ServiceOrder order, string before, int userId)
    {
        if (order.Status == ServiceOrderStatus.Waiting && before != ServiceOrderStatus.Waiting)
            Timeline(order, ServiceTimelineEventTypes.Waiting, userId, "Waiting for materials.");
    }
}
