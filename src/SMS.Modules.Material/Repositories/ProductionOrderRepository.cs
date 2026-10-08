using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Demand.Data;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Models;
using SMS.Modules.Material.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Material.Repositories;

/// <summary>
/// Persistence and read models for production (A30 §11–§13, §16). Product, variant and warehouse
/// facts are read from Inventory's context, the arrangement <see cref="BomRepository"/> uses.
/// </summary>
internal sealed class ProductionOrderRepository : IProductionOrderRepository
{
    private static readonly string[] ShortageStatuses =
        [ProductionOrderStatus.Planned, ProductionOrderStatus.MaterialPending, ProductionOrderStatus.Ready, ProductionOrderStatus.InProgress];

    private readonly MaterialDbContext        _db;
    private readonly InventoryDbContext       _inv;
    private readonly IDocumentNumberGenerator _numbers;
    // A34 read-model decoration (optional): the sale order line number and the route's names. The route lookup is
    // resolved when used, so this repository never pulls Logistics' route graph into its own construction.
    private readonly DemandDbContext?  _demand;
    private readonly IServiceProvider? _services;

    public ProductionOrderRepository(MaterialDbContext db, InventoryDbContext inv, IDocumentNumberGenerator numbers,
        DemandDbContext? demand = null, IServiceProvider? services = null)
    {
        _db       = db;
        _inv      = inv;
        _numbers  = numbers;
        _demand   = demand;
        _services = services;
    }

    // ── Writes ────────────────────────────────────────────────────────────────

    public async Task<ProductionOrder> CreateAsync(CreateProductionOrderRequest req, int userId, int? parentProductionOrderId = null, Guid? traceId = null,
        Guid? organizationId = null, Guid? fulfillmentRouteUuid = null)
    {
        ArgumentNullException.ThrowIfNull(req);

        if (req.PlannedQuantity <= 0)
            throw new BadRequestException("Planned quantity must be greater than zero.");
        if (req.RequiredDate.Date < DateTime.UtcNow.Date)
            throw new BadRequestException("Required date cannot be in the past.");
        if (req.PlannedStartDate is { } start && start.Date > req.RequiredDate.Date)
            throw new BadRequestException("Planned start date cannot be after the required date.");
        if (!AllocationPriority.IsKnown(req.Priority))
            throw new BadRequestException("Priority must be between 0 (low) and 3 (urgent).");

        var sourceType = string.IsNullOrWhiteSpace(req.SourceType) ? ProductionSourceType.Manual : req.SourceType.Trim().ToUpperInvariant();
        if (!ProductionSourceType.All.Contains(sourceType))
            throw new BadRequestException($"'{req.SourceType}' is not a production order source. Use one of: {string.Join(", ", ProductionSourceType.All)}.");

        var product   = await ManufacturedProductAsync(req.ProductUuid, organizationId);
        var variant   = await OutputVariantAsync(product, req.ProductVariantUuid, organizationId);
        var warehouse = req.WarehouseUuid ?? product.DefaultProductionWarehouseUuid
            ?? throw new BadRequestException(
                $"Choose a production warehouse, or set a default production warehouse on product {product.Name}.");
        await EnsureWarehouseAsync(warehouse, organizationId);
        if (req.OutputWarehouseUuid is { } output && output != warehouse) await EnsureWarehouseAsync(output, organizationId);

        var bom = await ActiveBomAsync(product, variant.Uuid, organizationId);
        var now = DateTime.UtcNow;

        var po = new ProductionOrder
        {
            TraceId                 = traceId ?? Guid.NewGuid(),
            // A34: an explicit organization (make-to-order creation, R-11) stamps the order and draws its number;
            // otherwise the ambient tenant does, as before.
            OrganizationId          = organizationId ?? Guid.Empty,
            ProductionNumber        = await _numbers.NextAsync(ManufacturingDocumentPrefix.ProductionOrder, now, organizationId),
            ProductUuid             = product.Uuid,
            ProductVariantUuid      = variant.Uuid,
            BomId                   = bom.Id,
            BomVersion              = bom.Version,
            PlannedQuantity         = req.PlannedQuantity,
            WarehouseUuid           = warehouse,
            OutputWarehouseUuid     = req.OutputWarehouseUuid == warehouse ? null : req.OutputWarehouseUuid,
            SourceType              = sourceType,
            SourceUuid              = req.SourceUuid,
            SourceLineUuid          = req.SourceLineUuid,
            SourceReference         = req.SourceReference?.Trim(),
            // A34 D-18: only the make-to-order path passes it (never the public create request), in the same INSERT,
            // so no PO can exist half made-to-order.
            FulfillmentRouteUuid    = fulfillmentRouteUuid,
            ParentProductionOrderId = parentProductionOrderId,
            Priority                = req.Priority,
            RequiredDate            = req.RequiredDate.Date,
            PlannedStartDate        = req.PlannedStartDate?.Date,
            Status                  = ProductionOrderStatus.Draft,
            MaterialReadiness       = MaterialReadiness.NotChecked,
            Notes                   = req.Notes?.Trim(),
            CreatedBy               = userId,
            CreatedAt               = now,
            UpdatedAt               = now
        };

        _db.ProductionOrders.Add(po);
        await _db.SaveChangesAsync();
        return po;
    }

    public async Task UpdateAsync(Guid uuid, UpdateProductionOrderRequest req, int userId)
    {
        ArgumentNullException.ThrowIfNull(req);
        var po = await LoadAsync(uuid);
        if (po.Status != ProductionOrderStatus.Draft)
            throw new BadRequestException($"Production order {po.ProductionNumber} is {Describe(po.Status)}; only a draft can be edited. Cancel it and raise a new one instead.");

        var quantity     = req.PlannedQuantity ?? po.PlannedQuantity;
        var requiredDate = (req.RequiredDate ?? po.RequiredDate).Date;
        var plannedStart = req.PlannedStartDate?.Date ?? po.PlannedStartDate;
        var priority     = req.Priority ?? po.Priority;

        if (quantity <= 0)
            throw new BadRequestException("Planned quantity must be greater than zero.");
        if (requiredDate < DateTime.UtcNow.Date)
            throw new BadRequestException("Required date cannot be in the past.");
        if (plannedStart is { } start && start > requiredDate)
            throw new BadRequestException("Planned start date cannot be after the required date.");
        if (!AllocationPriority.IsKnown(priority))
            throw new BadRequestException("Priority must be between 0 (low) and 3 (urgent).");

        if (req.WarehouseUuid is { } warehouse && warehouse != po.WarehouseUuid)
        {
            await EnsureWarehouseAsync(warehouse);
            // Re-snapshot in case the active recipe changed since this draft was created.
            var product = await ManufacturedProductAsync(po.ProductUuid);
            var bom     = await ActiveBomAsync(product, po.ProductVariantUuid);
            po.WarehouseUuid = warehouse;
            po.BomId         = bom.Id;
            po.BomVersion    = bom.Version;
        }
        if (req.OutputWarehouseUuid is { } output)
        {
            if (output != po.WarehouseUuid) await EnsureWarehouseAsync(output);
            po.OutputWarehouseUuid = output == po.WarehouseUuid ? null : output;
        }

        po.PlannedQuantity  = quantity;
        po.RequiredDate     = requiredDate;
        po.PlannedStartDate = plannedStart;
        po.Priority         = priority;
        if (req.Notes is not null) po.Notes = req.Notes.Trim();
        po.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
    }

    public async Task<ProductionOrder> LoadAsync(Guid uuid) =>
        await Tracked().FirstOrDefaultAsync(p => p.UUID == uuid)
        ?? throw new NotFoundException("Production order", uuid);

    public async Task<ProductionOrder> LoadAsync(int id) =>
        await Tracked().FirstOrDefaultAsync(p => p.Id == id)
        ?? throw new NotFoundException("Production order", id);

    private IQueryable<ProductionOrder> Tracked() =>
        _db.ProductionOrders
            .Include(p => p.Materials)
            .Include(p => p.Bom).ThenInclude(b => b.Lines)
            .Include(p => p.Parent);

    public Task SaveAsync() => _db.SaveChangesAsync();

    // ── Reads ─────────────────────────────────────────────────────────────────

    public async Task<PaginatedResponse<ProductionOrderListItemModel>> GetListAsync(ProductionOrderListFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var query = _db.ProductionOrders.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(filter.Status)) query = query.Where(p => p.Status == filter.Status.ToUpperInvariant());
        if (filter.ProductUuid is { } product)         query = query.Where(p => p.ProductUuid == product);
        if (filter.SourceUuid is { } sourceUuid)        query = query.Where(p => p.SourceUuid == sourceUuid);
        if (filter.Priority is { } priority)           query = query.Where(p => p.Priority == priority);
        if (filter.DateFrom is { } from)               query = query.Where(p => p.RequiredDate >= from.Date);
        if (filter.DateTo is { } to)                   query = query.Where(p => p.RequiredDate <= to.Date);
        if (filter.OpenOnly)
            query = query.Where(p => p.Status != ProductionOrderStatus.Completed && p.Status != ProductionOrderStatus.Closed &&
                                     p.Status != ProductionOrderStatus.Cancelled);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToLower();
            var productUuids = await _inv.Products.AsNoTracking()
                .Where(x => x.Name.ToLower().Contains(term) || x.Sku.ToLower().Contains(term))
                .Select(x => x.Uuid).ToListAsync();
            query = query.Where(p => p.ProductionNumber.ToLower().Contains(term) ||
                                     (p.SourceReference != null && p.SourceReference.ToLower().Contains(term)) ||
                                     productUuids.Contains(p.ProductUuid));
        }

        var total    = await query.CountAsync();
        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 200);

        var orders = await query
            .Include(p => p.Materials).Include(p => p.Bom).Include(p => p.Parent)
            .OrderByDescending(p => p.UpdatedAt).ThenByDescending(p => p.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync();

        var names = await NamesAsync(orders, [], []);
        var items = orders.Select(p => Fill(new ProductionOrderListItemModel(), p, names)).ToList();
        await DecorateMakeToOrderAsync(orders.Zip(items).ToList());
        return new PaginatedResponse<ProductionOrderListItemModel>
        {
            Data         = items,
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling((double)total / pageSize)
        };
    }

    public async Task<ProductionOrderDetailModel?> GetByUuidAsync(Guid uuid)
    {
        var po = await _db.ProductionOrders.AsNoTracking()
            .Include(p => p.Materials).Include(p => p.Bom).Include(p => p.Parent)
            .FirstOrDefaultAsync(p => p.UUID == uuid);
        if (po is null) return null;

        var pmrUuids = po.Materials.Select(m => m.UUID).ToList();
        var supply = await _db.SupplyRequirements.AsNoTracking()
            .Where(s => s.DemandSourceType == SupplyDemandSourceType.ProductionMaterialRequirement && pmrUuids.Contains(s.DemandSourceUuid))
            .OrderBy(s => s.Id).ToListAsync();
        var issues = await _db.ProductionMaterialIssues.AsNoTracking().Include(i => i.Lines)
            .Where(i => i.ProductionOrderId == po.Id)
            .OrderByDescending(i => i.Id).ToListAsync();
        var children = await _db.ProductionOrders.AsNoTracking()
            .Include(p => p.Materials).Include(p => p.Bom)
            .Where(p => p.ParentProductionOrderId == po.Id)
            .OrderBy(p => p.Id).ToListAsync();

        var names = await NamesAsync([po, .. children], supply, issues);
        var pmrByUuid = po.Materials.ToDictionary(m => m.UUID);
        var pmrById   = po.Materials.ToDictionary(m => m.Id);

        var detail = Fill(new ProductionOrderDetailModel(), po, names);
        detail.TraceId             = po.TraceId;
        detail.BomUuid             = po.Bom.UUID;
        detail.OutputWarehouseUuid = po.OutputWarehouseUuid;
        detail.OutputWarehouseName = po.OutputWarehouseUuid is { } o ? names.Warehouses.GetValueOrDefault(o) : null;
        detail.SourceUuid          = po.SourceUuid;
        detail.SourceLineUuid      = po.SourceLineUuid;
        detail.ScrappedQuantity    = po.ScrappedQuantity;
        detail.Notes               = po.Notes;
        detail.CreatedBy           = po.CreatedBy;
        detail.Materials           = po.Materials.OrderBy(m => m.Sequence).ThenBy(m => m.Id).Select(m => ToMaterialModel(m, names)).ToList();
        detail.SupplyRequirements  = supply.Select(s => ToSupplyModel(s, names, pmrByUuid)).ToList();
        detail.Issues              = issues.Select(i => ToIssueModel(i, po, names, pmrById)).ToList();
        detail.ChildOrders         = children.Select(c => Fill(new ProductionOrderListItemModel(), c, names)).ToList();
        await DecorateMakeToOrderAsync([(po, detail), .. children.Zip(detail.ChildOrders)]);
        return detail;
    }

    /// <summary>
    /// A34 (API-CONTRACT §7) — the make-to-order fields: route names through <see cref="IFulfillmentRouteLookup"/> (per
    /// organization, also for a route deactivated since), the sale order line's 1-based number (by line id, A33's "Line
    /// N"), the delivery stamped by the handoff, and the shortfall (once COMPLETED: planned − accepted; at zero yield: the
    /// planned quantity). Batched; a lookup that fails leaves the names empty rather than failing the read.
    /// </summary>
    private async Task DecorateMakeToOrderAsync(IReadOnlyList<(ProductionOrder Po, ProductionOrderListItemModel Model)> rows)
    {
        if (rows.Count == 0) return;
        foreach (var (p, m) in rows)
        {
            m.IsMakeToOrder           = p.FulfillmentRouteUuid is not null;
            m.FulfillmentRouteUuid    = p.FulfillmentRouteUuid;
            m.DeliveryOrderUuid       = p.DeliveryOrderUuid;
            m.DeliveryNumber          = p.DeliveryNumber;
            m.DeliveryCreationPending = p.DeliveryCreationPendingSince is not null;
            if (p.FulfillmentRouteUuid is not null && p.Status == ProductionOrderStatus.Completed && p.PlannedQuantity > p.AcceptedQuantity)
                m.ShortfallQuantity = p.PlannedQuantity - p.AcceptedQuantity;
        }

        // Zero yield: a make-to-order order still in QUALITY_INSPECTION whose inspection accepted nothing.
        var inspecting = rows.Where(r => r.Po.FulfillmentRouteUuid is not null && r.Po.Status == ProductionOrderStatus.QualityInspection)
                             .Select(r => r.Po.Id).Distinct().ToList();
        if (inspecting.Count > 0)
        {
            var zero = (await _db.QualityInspections.IgnoreQueryFilters().AsNoTracking()
                    .Where(q => inspecting.Contains(q.ProductionOrderId) && q.AcceptedQuantity == 0)
                    .Select(q => q.ProductionOrderId).ToListAsync())
                .ToHashSet();
            foreach (var (p, m) in rows.Where(r => zero.Contains(r.Po.Id))) m.ShortfallQuantity = p.PlannedQuantity;
        }

        if (_services?.GetService<IFulfillmentRouteLookup>() is { } routes)
        {
            foreach (var org in rows.Where(r => r.Po.FulfillmentRouteUuid is not null).GroupBy(r => r.Po.OrganizationId))
            {
                try
                {
                    var found = await routes.GetAsync(org.Key, org.Select(r => r.Po.FulfillmentRouteUuid!.Value).Distinct().ToList());
                    foreach (var (p, m) in org)
                        if (found.TryGetValue(p.FulfillmentRouteUuid!.Value, out var route))
                        {
                            m.FulfillmentRouteCode     = route.Code;
                            m.FulfillmentRouteName     = route.Name;
                            m.FulfillmentRouteCategory = route.Category;
                        }
                }
                catch (Exception)
                {
                    // Names are decoration; the order itself still reads.
                }
            }
        }

        if (_demand is not null)
        {
            var linked = rows.Where(r => r.Po.SourceType == ProductionSourceType.SalesOrder && r.Po.SourceUuid is not null && r.Po.SourceLineUuid is not null).ToList();
            if (linked.Count == 0) return;
            var soUuids = linked.Select(r => r.Po.SourceUuid!.Value).Distinct().ToList();
            var lines = await _demand.SaleOrderLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => soUuids.Contains(l.SaleOrder.UUID))
                .Select(l => new { SoUuid = l.SaleOrder.UUID, l.UUID, l.Id, l.OrganizationId })
                .ToListAsync();
            var numbers = lines.GroupBy(l => l.SoUuid)
                .SelectMany(g => g.OrderBy(l => l.Id).Select((l, i) => (l.UUID, l.OrganizationId, Number: i + 1)))
                .ToDictionary(l => l.UUID);
            foreach (var (p, m) in linked)
                if (numbers.TryGetValue(p.SourceLineUuid!.Value, out var n) && n.OrganizationId == p.OrganizationId)
                    m.SaleOrderLineNumber = n.Number;
        }
    }

    public async Task<IReadOnlyList<ProductionMaterialModel>> GetMaterialsAsync(Guid uuid)
    {
        var po = await _db.ProductionOrders.AsNoTracking().Include(p => p.Materials)
            .FirstOrDefaultAsync(p => p.UUID == uuid) ?? throw new NotFoundException("Production order", uuid);
        var names = await NamesAsync([po], [], []);
        return po.Materials.OrderBy(m => m.Sequence).ThenBy(m => m.Id).Select(m => ToMaterialModel(m, names)).ToList();
    }

    public async Task<ProductionReadinessModel?> GetReadinessAsync(Guid uuid)
    {
        var po = await _db.ProductionOrders.AsNoTracking().Include(p => p.Materials)
            .FirstOrDefaultAsync(p => p.UUID == uuid);
        if (po is null) return null;

        var pmrUuids = po.Materials.Select(m => m.UUID).ToList();
        var supply = await _db.SupplyRequirements.AsNoTracking()
            .Where(s => s.DemandSourceType == SupplyDemandSourceType.ProductionMaterialRequirement && pmrUuids.Contains(s.DemandSourceUuid))
            .OrderBy(s => s.Id).ToListAsync();
        var names = await NamesAsync([po], supply, []);
        var pmrByUuid = po.Materials.ToDictionary(m => m.UUID);

        return new ProductionReadinessModel
        {
            ProductionOrderUuid = po.UUID,
            ProductionNumber    = po.ProductionNumber,
            Status              = po.Status,
            MaterialReadiness   = po.MaterialReadiness,
            AllCriticalCovered  = ProductionReadiness.AllCriticalCovered(po),
            Materials           = po.Materials.OrderBy(m => m.Sequence).ThenBy(m => m.Id).Select(m => ToMaterialModel(m, names)).ToList(),
            SupplyRequirements  = supply.Select(s => ToSupplyModel(s, names, pmrByUuid)).ToList()
        };
    }

    public async Task<IReadOnlyList<MaterialShortageModel>> GetShortagesAsync(Guid? warehouseUuid)
    {
        var query = _db.ProductionMaterialRequirements.AsNoTracking().Include(m => m.ProductionOrder)
            .Where(m => m.Status != PmrStatus.Cancelled && ShortageStatuses.Contains(m.ProductionOrder.Status) &&
                        m.RequiredQuantity - m.IssuedQuantity + m.ReturnedQuantity - m.ReservedQuantity > 0);
        if (warehouseUuid is { } wh) query = query.Where(m => m.WarehouseUuid == wh);

        var rows = await query
            .OrderByDescending(m => m.ProductionOrder.Priority).ThenBy(m => m.ProductionOrder.RequiredDate).ThenBy(m => m.Id)
            .ToListAsync();
        if (rows.Count == 0) return [];

        var pmrUuids = rows.Select(m => m.UUID).ToList();
        var supply = await _db.SupplyRequirements.AsNoTracking()
            .Where(s => s.DemandSourceType == SupplyDemandSourceType.ProductionMaterialRequirement && pmrUuids.Contains(s.DemandSourceUuid))
            .OrderByDescending(s => s.Id).ToListAsync();
        var supplyByPmr = supply.GroupBy(s => s.DemandSourceUuid)
            .ToDictionary(g => g.Key, g => g.FirstOrDefault(s => SupplyRequirementStatus.IsLive(s.Status)) ?? g.First());

        var orders = rows.Select(m => m.ProductionOrder).DistinctBy(p => p.Id).ToList();
        foreach (var po in orders) po.Materials = rows.Where(m => m.ProductionOrderId == po.Id).ToList();
        var names = await NamesAsync(orders, supply, []);

        return rows.Select(m =>
        {
            var po      = m.ProductionOrder;
            var product = names.Products.GetValueOrDefault(m.MaterialProductUuid);
            var variant = names.Variants.GetValueOrDefault(m.MaterialVariantUuid);
            var sr      = supplyByPmr.GetValueOrDefault(m.UUID);
            return new MaterialShortageModel
            {
                ProductionOrderUuid = po.UUID,
                ProductionNumber    = po.ProductionNumber,
                ProductionStatus    = po.Status,
                OutputProductName   = names.Products.GetValueOrDefault(po.ProductUuid).Name ?? string.Empty,
                Priority            = po.Priority,
                RequiredDate        = m.RequiredDate,
                RequirementUuid     = m.UUID,
                MaterialVariantUuid = m.MaterialVariantUuid,
                MaterialName        = MaterialName(product.Name, variant.VariantName),
                MaterialSku         = variant.Sku ?? string.Empty,
                RequiredQuantity    = m.RequiredQuantity,
                ReservedQuantity    = m.ReservedQuantity,
                PlannedQuantity     = m.PlannedQuantity,
                ShortageQuantity    = m.ShortageQuantity,
                Uom                 = m.Uom,
                WarehouseUuid       = m.WarehouseUuid,
                WarehouseName       = names.Warehouses.GetValueOrDefault(m.WarehouseUuid) ?? string.Empty,
                IsCritical          = m.IsCritical,
                SupplyNumber        = sr?.SupplyNumber,
                SupplyStatus        = sr?.Status,
                SupplySourceReference = sr?.SupplySourceReference
            };
        }).ToList();
    }

    public async Task<PaginatedResponse<SupplyRequirementModel>> GetSupplyRequirementsAsync(SupplyRequirementListFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var query = _db.SupplyRequirements.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(filter.Status))           query = query.Where(s => s.Status == filter.Status.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(filter.SupplyMethod))     query = query.Where(s => s.SupplyMethod == filter.SupplyMethod.ToUpperInvariant());
        if (filter.VariantUuid is { } variant)                   query = query.Where(s => s.VariantUuid == variant);
        if (filter.ProductUuid is { } product)                   query = query.Where(s => s.ProductUuid == product);
        if (!string.IsNullOrWhiteSpace(filter.DemandSourceType)) query = query.Where(s => s.DemandSourceType == filter.DemandSourceType.ToUpperInvariant());
        if (filter.DemandSourceUuid is { } demand)               query = query.Where(s => s.DemandSourceUuid == demand);
        if (filter.OpenOnly)
            query = query.Where(s => s.Status != SupplyRequirementStatus.Fulfilled && s.Status != SupplyRequirementStatus.Cancelled);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToLower();
            var productUuids = await _inv.Products.AsNoTracking()
                .Where(x => x.Name.ToLower().Contains(term) || x.Sku.ToLower().Contains(term))
                .Select(x => x.Uuid).ToListAsync();
            query = query.Where(s => s.SupplyNumber.ToLower().Contains(term) ||
                                     (s.DemandReference != null && s.DemandReference.ToLower().Contains(term)) ||
                                     (s.SupplySourceReference != null && s.SupplySourceReference.ToLower().Contains(term)) ||
                                     productUuids.Contains(s.ProductUuid));
        }

        var total    = await query.CountAsync();
        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 200);

        var rows = await query
            .OrderByDescending(s => s.UpdatedAt).ThenByDescending(s => s.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync();

        var names = await NamesAsync([], rows, []);
        var pmrs  = await PmrsForAsync(rows);
        return new PaginatedResponse<SupplyRequirementModel>
        {
            Data         = rows.Select(s => ToSupplyModel(s, names, pmrs)).ToList(),
            TotalRecords = total,
            Page         = page,
            PageSize     = pageSize,
            TotalPages   = (int)Math.Ceiling((double)total / pageSize)
        };
    }

    public async Task<SupplyRequirementModel?> GetSupplyRequirementAsync(Guid uuid)
    {
        var sr = await _db.SupplyRequirements.AsNoTracking().FirstOrDefaultAsync(s => s.UUID == uuid);
        if (sr is null) return null;
        var names = await NamesAsync([], [sr], []);
        return ToSupplyModel(sr, names, await PmrsForAsync([sr]));
    }

    public async Task<IReadOnlyList<SupplyRequirementModel>> GetSupplyRequirementsForOrderAsync(Guid productionOrderUuid)
    {
        var po = await _db.ProductionOrders.AsNoTracking().Include(p => p.Materials)
            .FirstOrDefaultAsync(p => p.UUID == productionOrderUuid) ?? throw new NotFoundException("Production order", productionOrderUuid);
        var pmrUuids = po.Materials.Select(m => m.UUID).ToList();
        var rows = await _db.SupplyRequirements.AsNoTracking()
            .Where(s => s.DemandSourceType == SupplyDemandSourceType.ProductionMaterialRequirement && pmrUuids.Contains(s.DemandSourceUuid))
            .OrderBy(s => s.Id).ToListAsync();
        var names = await NamesAsync([], rows, []);
        return rows.Select(s => ToSupplyModel(s, names, po.Materials.ToDictionary(m => m.UUID))).ToList();
    }

    public async Task<ProductionIssueModel?> GetIssueAsync(Guid uuid)
    {
        var issue = await _db.ProductionMaterialIssues.AsNoTracking().Include(i => i.Lines).Include(i => i.ProductionOrder)
            .FirstOrDefaultAsync(i => i.UUID == uuid);
        if (issue is null) return null;

        var pmrIds = issue.Lines.Select(l => l.RequirementId).Distinct().ToList();
        var pmrs   = await _db.ProductionMaterialRequirements.AsNoTracking().Where(m => pmrIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id);
        var names  = await NamesAsync([], [], [issue]);
        return ToIssueModel(issue, issue.ProductionOrder, names, pmrs);
    }

    public async Task<IReadOnlyList<ProductionIssueModel>> GetIssuesForOrderAsync(Guid productionOrderUuid)
    {
        var po = await _db.ProductionOrders.AsNoTracking().Include(p => p.Materials)
            .FirstOrDefaultAsync(p => p.UUID == productionOrderUuid) ?? throw new NotFoundException("Production order", productionOrderUuid);
        var issues = await _db.ProductionMaterialIssues.AsNoTracking().Include(i => i.Lines)
            .Where(i => i.ProductionOrderId == po.Id).OrderByDescending(i => i.Id).ToListAsync();
        var names = await NamesAsync([], [], issues);
        var pmrById = po.Materials.ToDictionary(m => m.Id);
        return issues.Select(i => ToIssueModel(i, po, names, pmrById)).ToList();
    }

    // ── Rules ─────────────────────────────────────────────────────────────────

    private sealed record ProductFacts(int Id, Guid Uuid, string Name, string? UomCode, string SupplyMethod, bool IsActive, Guid? DefaultProductionWarehouseUuid);
    private sealed record VariantFacts(int Id, Guid Uuid, string VariantName, bool IsDefault, bool IsActive);

    /// <summary>V-PR01 — only something this organization makes can be made.</summary>
    private async Task<ProductFacts> ManufacturedProductAsync(Guid productUuid, Guid? organizationId = null)
    {
        var products = organizationId is { } org
            ? _inv.Products.IgnoreQueryFilters().Where(p => p.OrganizationId == org)
            : _inv.Products;
        var product = await products.AsNoTracking()
            .Where(p => p.Uuid == productUuid)
            .Select(p => new ProductFacts(p.Id, p.Uuid, p.Name, p.UomCode, p.SupplyMethod, p.IsActive,
                p.DefaultProductionWarehouse != null ? p.DefaultProductionWarehouse.Uuid : (Guid?)null))
            .FirstOrDefaultAsync()
            ?? throw new NotFoundException("Product", productUuid);

        if (!product.IsActive)
            throw new BadRequestException($"Product {product.Name} is inactive.");
        if (product.SupplyMethod != SupplyMethod.Manufacture)
            throw new BadRequestException(
                $"Product {product.Name} is not configured for manufacturing (supply method {product.SupplyMethod}). Set its supply method to MANUFACTURE first.");
        return product;
    }

    private async Task<VariantFacts> OutputVariantAsync(ProductFacts product, Guid? variantUuid, Guid? organizationId = null)
    {
        var source = organizationId is { } org
            ? _inv.ProductVariants.IgnoreQueryFilters().Where(v => v.OrganizationId == org)
            : _inv.ProductVariants;
        var variants = await source.AsNoTracking()
            .Where(v => v.ProductId == product.Id)
            .Select(v => new VariantFacts(v.Id, v.Uuid, v.VariantName, v.IsDefault, v.IsActive))
            .ToListAsync();

        if (variantUuid is { } wanted)
        {
            var chosen = variants.FirstOrDefault(v => v.Uuid == wanted)
                ?? throw new BadRequestException($"The variant does not belong to product {product.Name}.");
            if (!chosen.IsActive) throw new BadRequestException($"Variant {chosen.VariantName} of {product.Name} is inactive.");
            return chosen;
        }

        return variants.FirstOrDefault(v => v.IsDefault && v.IsActive)
            ?? variants.FirstOrDefault(v => v.IsActive)
            ?? throw new BadRequestException($"Product {product.Name} has no active variant to produce.");
    }

    private async Task EnsureWarehouseAsync(Guid warehouseUuid, Guid? organizationId = null)
    {
        var warehouses = organizationId is { } org
            ? _inv.Warehouses.IgnoreQueryFilters().Where(w => w.OrganizationId == org)
            : _inv.Warehouses;
        if (!await warehouses.AsNoTracking().AnyAsync(w => w.Uuid == warehouseUuid && w.IsActive))
            throw new BadRequestException($"Warehouse {warehouseUuid} does not exist or is inactive.");
    }

    /// <summary>
    /// The recipe to snapshot (§11.4): the active one for this exact variant, else the product's
    /// general one. A31-C6 removed warehouse from this resolution entirely — warehouse is decided
    /// by the production order, not by which recipe applies. A34: the rule lives in
    /// <see cref="ActiveBomResolver"/>, shared with the BOM reader and the confirm gate.
    /// </summary>
    private async Task<BillOfMaterial> ActiveBomAsync(ProductFacts product, Guid variantUuid, Guid? organizationId = null)
    {
        var boms = organizationId is { } org
            ? _db.BillsOfMaterials.IgnoreQueryFilters().AsNoTracking().Where(b => b.OrganizationId == org)
            : _db.BillsOfMaterials.AsNoTracking();
        var candidates = await ActiveBomResolver.Candidates(boms, [product.Uuid], DateTime.UtcNow).ToListAsync();

        return ActiveBomResolver.Pick(candidates, product.Uuid, variantUuid)
            ?? throw new BadRequestException($"No active BOM found for product {product.Name}. Activate a bill of materials first.");
    }

    private static string Describe(string status) => status.ToLowerInvariant().Replace('_', ' ');

    private async Task<Dictionary<Guid, ProductionMaterialRequirement>> PmrsForAsync(IReadOnlyList<SupplyRequirement> rows)
    {
        var pmrUuids = rows.Where(s => s.DemandSourceType == SupplyDemandSourceType.ProductionMaterialRequirement)
            .Select(s => s.DemandSourceUuid).Distinct().ToList();
        return pmrUuids.Count == 0
            ? []
            : await _db.ProductionMaterialRequirements.AsNoTracking().Include(m => m.ProductionOrder)
                .Where(m => pmrUuids.Contains(m.UUID)).ToDictionaryAsync(m => m.UUID);
    }

    // ── Names ─────────────────────────────────────────────────────────────────

    internal sealed record Names(
        Dictionary<Guid, (string Name, string Sku, string SupplyMethod)> Products,
        Dictionary<Guid, (string Sku, string VariantName, Guid ProductUuid)> Variants,
        Dictionary<Guid, string> Warehouses);

    private async Task<Names> NamesAsync(
        IReadOnlyList<ProductionOrder> orders, IReadOnlyList<SupplyRequirement> supply, IReadOnlyList<ProductionMaterialIssue> issues)
    {
        var productUuids = orders.Select(p => p.ProductUuid)
            .Concat(orders.SelectMany(p => p.Materials).Select(m => m.MaterialProductUuid))
            .Concat(supply.Select(s => s.ProductUuid))
            .Distinct().ToList();
        var variantUuids = orders.Select(p => p.ProductVariantUuid)
            .Concat(orders.SelectMany(p => p.Materials).Select(m => m.MaterialVariantUuid))
            .Concat(supply.Select(s => s.VariantUuid))
            .Concat(issues.SelectMany(i => i.Lines).Select(l => l.MaterialVariantUuid))
            .Distinct().ToList();
        var warehouseUuids = orders.Select(p => p.WarehouseUuid)
            .Concat(orders.Where(p => p.OutputWarehouseUuid.HasValue).Select(p => p.OutputWarehouseUuid!.Value))
            .Concat(orders.SelectMany(p => p.Materials).Select(m => m.WarehouseUuid))
            .Concat(supply.Select(s => s.WarehouseUuid))
            .Concat(issues.Select(i => i.WarehouseUuid))
            .Distinct().ToList();

        var variants = variantUuids.Count == 0
            ? new Dictionary<Guid, (string, string, Guid)>()
            : await _inv.ProductVariants.AsNoTracking().Where(v => variantUuids.Contains(v.Uuid))
                .Select(v => new { v.Uuid, v.Sku, v.VariantName, ProductUuid = v.Product.Uuid })
                .ToDictionaryAsync(v => v.Uuid, v => (v.Sku, v.VariantName, v.ProductUuid));

        productUuids = productUuids.Concat(variants.Values.Select(v => v.Item3)).Distinct().ToList();
        var products = productUuids.Count == 0
            ? new Dictionary<Guid, (string, string, string)>()
            : await _inv.Products.AsNoTracking().Where(p => productUuids.Contains(p.Uuid))
                .Select(p => new { p.Uuid, p.Name, p.Sku, p.SupplyMethod })
                .ToDictionaryAsync(p => p.Uuid, p => (p.Name, p.Sku, p.SupplyMethod));

        var warehouses = warehouseUuids.Count == 0
            ? new Dictionary<Guid, string>()
            : await _inv.Warehouses.AsNoTracking().Where(w => warehouseUuids.Contains(w.Uuid))
                .ToDictionaryAsync(w => w.Uuid, w => w.Name);

        return new Names(products, variants, warehouses);
    }

    private static string MaterialName(string? product, string? variant) =>
        string.IsNullOrWhiteSpace(variant) || variant == "Default" ? product ?? string.Empty : $"{product} – {variant}";

    private static T Fill<T>(T model, ProductionOrder p, Names names) where T : ProductionOrderListItemModel
    {
        var product = names.Products.GetValueOrDefault(p.ProductUuid);
        var live    = p.Materials.Where(m => m.Status != PmrStatus.Cancelled).ToList();
        model.UUID                      = p.UUID;
        model.ProductionNumber          = p.ProductionNumber;
        model.ProductUuid               = p.ProductUuid;
        model.ProductName               = product.Name ?? string.Empty;
        model.ProductSku                = product.Sku ?? string.Empty;
        model.ProductVariantUuid        = p.ProductVariantUuid;
        model.VariantName               = names.Variants.GetValueOrDefault(p.ProductVariantUuid).VariantName ?? string.Empty;
        model.BomNumber                 = p.Bom?.BomNumber ?? string.Empty;
        model.BomVersion                = p.BomVersion;
        model.PlannedQuantity           = p.PlannedQuantity;
        model.ProducedQuantity          = p.ProducedQuantity;
        model.AcceptedQuantity          = p.AcceptedQuantity;
        model.RejectedQuantity          = p.RejectedQuantity;
        model.WarehouseUuid             = p.WarehouseUuid;
        model.WarehouseName             = names.Warehouses.GetValueOrDefault(p.WarehouseUuid) ?? string.Empty;
        model.SourceType                = p.SourceType;
        model.SourceUuid                = p.SourceUuid;
        model.SourceLineUuid            = p.SourceLineUuid;
        model.SourceReference           = p.SourceReference;
        model.ParentProductionOrderUuid = p.Parent?.UUID;
        model.ParentProductionNumber    = p.Parent?.ProductionNumber;
        model.Priority                  = p.Priority;
        model.RequiredDate              = p.RequiredDate;
        model.PlannedStartDate          = p.PlannedStartDate;
        model.ActualStartDate           = p.ActualStartDate;
        model.ActualEndDate             = p.ActualEndDate;
        model.Status                    = p.Status;
        model.MaterialReadiness         = p.MaterialReadiness;
        model.MaterialCount             = live.Count;
        model.ShortMaterialCount        = live.Count(m => !ProductionReadiness.IsCovered(m));
        model.CreatedAt                 = p.CreatedAt;
        model.UpdatedAt                 = p.UpdatedAt;
        return model;
    }

    private static ProductionMaterialModel ToMaterialModel(ProductionMaterialRequirement m, Names names)
    {
        var product = names.Products.GetValueOrDefault(m.MaterialProductUuid);
        var variant = names.Variants.GetValueOrDefault(m.MaterialVariantUuid);
        return new ProductionMaterialModel
        {
            UUID                 = m.UUID,
            Sequence             = m.Sequence,
            MaterialProductUuid  = m.MaterialProductUuid,
            MaterialProductName  = product.Name ?? string.Empty,
            MaterialSupplyMethod = product.SupplyMethod ?? string.Empty,
            MaterialVariantUuid  = m.MaterialVariantUuid,
            MaterialSku          = variant.Sku ?? string.Empty,
            MaterialVariantName  = variant.VariantName ?? string.Empty,
            NetQuantity          = m.NetQuantity,
            ScrapAllowance       = m.ScrapAllowance,
            RequiredQuantity     = m.RequiredQuantity,
            ReservedQuantity     = m.ReservedQuantity,
            PlannedQuantity      = m.PlannedQuantity,
            IssuedQuantity       = m.IssuedQuantity,
            ReturnedQuantity     = m.ReturnedQuantity,
            WastageQuantity      = m.WastageQuantity,
            ConsumedQuantity     = m.ConsumedQuantity,
            ShortageQuantity     = m.ShortageQuantity,
            Outstanding          = m.Outstanding,
            Uom                  = m.Uom,
            WarehouseUuid        = m.WarehouseUuid,
            WarehouseName        = names.Warehouses.GetValueOrDefault(m.WarehouseUuid) ?? string.Empty,
            IsCritical           = m.IsCritical,
            Status               = m.Status,
            RequiredDate         = m.RequiredDate,
            AllocationDemandUuid = m.AllocationDemandUuid,
            IsCovered            = ProductionReadiness.IsCovered(m)
        };
    }

    private static SupplyRequirementModel ToSupplyModel(SupplyRequirement s, Names names, IReadOnlyDictionary<Guid, ProductionMaterialRequirement> pmrs)
    {
        var product = names.Products.GetValueOrDefault(s.ProductUuid);
        var variant = names.Variants.GetValueOrDefault(s.VariantUuid);
        var reference = s.DemandReference;
        if (reference is null && pmrs.TryGetValue(s.DemandSourceUuid, out var pmr) && pmr.ProductionOrder is not null)
            reference = pmr.ProductionOrder.ProductionNumber;
        return new SupplyRequirementModel
        {
            UUID                  = s.UUID,
            SupplyNumber          = s.SupplyNumber,
            ProductUuid           = s.ProductUuid,
            ProductName           = product.Name ?? string.Empty,
            VariantUuid           = s.VariantUuid,
            VariantName           = variant.VariantName ?? string.Empty,
            MaterialSku           = variant.Sku ?? string.Empty,
            QuantityRequired      = s.QuantityRequired,
            QuantityOrdered       = s.QuantityOrdered,
            QuantityReceived      = s.QuantityReceived,
            QuantityOutstanding   = s.QuantityOutstanding,
            DemandSourceType      = s.DemandSourceType,
            DemandSourceUuid      = s.DemandSourceUuid,
            DemandReference       = reference,
            SupplyMethod          = s.SupplyMethod,
            SupplySourceType      = s.SupplySourceType,
            SupplySourceUuid      = s.SupplySourceUuid,
            SupplySourceReference = s.SupplySourceReference,
            WarehouseUuid         = s.WarehouseUuid,
            WarehouseName         = names.Warehouses.GetValueOrDefault(s.WarehouseUuid) ?? string.Empty,
            RequiredDate          = s.RequiredDate,
            Priority              = s.Priority,
            Status                = s.Status,
            Notes                 = s.Notes,
            CreatedAt             = s.CreatedAt,
            UpdatedAt             = s.UpdatedAt
        };
    }

    private static ProductionIssueModel ToIssueModel(
        ProductionMaterialIssue i, ProductionOrder po, Names names, IReadOnlyDictionary<int, ProductionMaterialRequirement> pmrs) =>
        new()
        {
            UUID                = i.UUID,
            IssueNumber         = i.IssueNumber,
            ProductionOrderUuid = po.UUID,
            ProductionNumber    = po.ProductionNumber,
            WarehouseUuid       = i.WarehouseUuid,
            WarehouseName       = names.Warehouses.GetValueOrDefault(i.WarehouseUuid) ?? string.Empty,
            IssueType           = i.IssueType,
            Status              = i.Status,
            CreatedBy           = i.CreatedBy,
            CreatedAt           = i.CreatedAt,
            ConfirmedBy         = i.ConfirmedBy,
            ConfirmedAt         = i.ConfirmedAt,
            ReversedAt          = i.ReversedAt,
            Notes               = i.Notes,
            TotalQuantity       = i.Lines.Sum(l => l.Quantity),
            Lines = i.Lines.OrderBy(l => l.Id).Select(l =>
            {
                var variant = names.Variants.GetValueOrDefault(l.MaterialVariantUuid);
                var product = names.Products.GetValueOrDefault(variant.ProductUuid);
                return new ProductionIssueLineModel
                {
                    UUID                = l.UUID,
                    RequirementUuid     = pmrs.TryGetValue(l.RequirementId, out var pmr) ? pmr.UUID : Guid.Empty,
                    MaterialVariantUuid = l.MaterialVariantUuid,
                    MaterialName        = MaterialName(product.Name, variant.VariantName),
                    MaterialSku         = variant.Sku ?? string.Empty,
                    Quantity            = l.Quantity,
                    Uom                 = l.Uom,
                    UnitCost            = l.UnitCost,
                    BatchNumber         = l.BatchNumber,
                    Notes               = l.Notes
                };
            }).ToList()
        };
}
