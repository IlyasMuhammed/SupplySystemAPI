using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Jobs;
using SMS.WorkflowEngine.Models;

namespace SMS.Modules.Material.Services;

/// <summary>
/// A30 §13, BR-S01–S07. A purchased shortage raises a purchase order; a manufactured shortage
/// raises and plans a child production order (chained manufacturing, §6.4.1); a transferred one is
/// left as a note for a person, this codebase having no transfer-order module yet.
/// <para>
/// Resolves <see cref="IProductionOrderService"/> from the container rather than taking it as a
/// constructor dependency: that service raises supply requirements for what its own plan is short
/// of, and a constructor cycle between the two would not resolve. Both are scoped, so the instance
/// this fetches mid-call is the same one already doing the planning that got here.
/// </para>
/// </summary>
internal sealed class SupplyRequirementEngine : ISupplyRequirementEngine
{
    /// <summary>Bounds a purchase-or-manufacture chain the same way <see cref="BomRepository"/> bounds a recipe chain.</summary>
    private const int MaxChainDepth = 10;

    private readonly MaterialDbContext           _db;
    private readonly InventoryDbContext          _inv;
    private readonly IDocumentNumberGenerator    _numbers;
    private readonly IAllocationEngine           _engine;
    private readonly IPurchaseOrderService       _purchaseOrders;
    private readonly ISupplierNameLookupService  _supplierNames;
    private readonly IVariantSupplierResolver    _rates;
    private readonly IServiceProvider            _services;
    private readonly IBackgroundJobClient?       _jobs;
    private readonly IManufacturingNotificationService? _notify;

    public SupplyRequirementEngine(
        MaterialDbContext db, InventoryDbContext inv, IDocumentNumberGenerator numbers, IAllocationEngine engine,
        IPurchaseOrderService purchaseOrders, ISupplierNameLookupService supplierNames, IVariantSupplierResolver rates,
        IServiceProvider services, IBackgroundJobClient? jobs = null, IManufacturingNotificationService? notify = null)
    {
        _db             = db;
        _inv            = inv;
        _numbers        = numbers;
        _engine         = engine;
        _purchaseOrders = purchaseOrders;
        _supplierNames  = supplierNames;
        _rates          = rates;
        _services       = services;
        _jobs           = jobs;
        _notify         = notify;
    }

    /// <summary>A30-P5-07 — on the same trace as whatever it was raised to cover.</summary>
    private void EnqueueTimeline(SupplyRequirement sr, int userId) =>
        _jobs?.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
            sr.TraceId,
            new TimelineEvent(ManufacturingTimelineEventTypes.SrCreated, ManufacturingInterfaceCodes.SupplyRequirement, sr.UUID, sr.SupplyNumber, DateTime.UtcNow, userId,
                $"{sr.QuantityRequired:0.####} of {sr.SupplyMethod.ToLowerInvariant()}, for {sr.DemandReference ?? sr.SupplyNumber}."),
            ManufacturingInterfaceCodes.SupplyRequirement, sr.SupplyNumber));

    public async Task<SupplyRequirement?> EnsureForShortageAsync(
        ProductionMaterialRequirement pmr, ProductionOrder productionOrder, int userId, int depth, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pmr);
        ArgumentNullException.ThrowIfNull(productionOrder);

        var existing = await _db.SupplyRequirements.FirstOrDefaultAsync(s =>
            s.DemandSourceType == SupplyDemandSourceType.ProductionMaterialRequirement &&
            s.DemandSourceUuid == pmr.UUID && SupplyRequirementStatus.LiveStatuses.Contains(s.Status), ct);

        var shortage = pmr.ShortageQuantity;
        if (shortage <= 0)
        {
            // The next allocation run covered it (stock arrived, a hold moved) before anyone acted
            // on the paperwork. Only an un-acted one is safe to drop; an ordered one stays as the
            // record of what was done, even if the run has since re-covered the requirement another way.
            if (existing is { Status: SupplyRequirementStatus.Open or SupplyRequirementStatus.Planned })
            {
                existing.Status    = SupplyRequirementStatus.Cancelled;
                existing.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
            }
            return null;
        }

        if (existing is not null)
        {
            if (existing.Status == SupplyRequirementStatus.Open)
            {
                existing.QuantityRequired = shortage;
                existing.UpdatedAt        = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
            }
            return existing;
        }

        var material = await _inv.Products.AsNoTracking()
            .Where(p => p.Uuid == pmr.MaterialProductUuid)
            .Select(p => new { p.Name, p.SupplyMethod })
            .FirstOrDefaultAsync(ct);
        var supplyMethod = material?.SupplyMethod is SMS.Shared.Common.SupplyMethod.Manufacture or SMS.Shared.Common.SupplyMethod.Transfer
            ? material.SupplyMethod
            : SMS.Shared.Common.SupplyMethod.Purchase;

        var now = DateTime.UtcNow;
        var sr = new SupplyRequirement
        {
            // A30-P5-07 — the same trace as the production order it covers, so SO→PO→SR→...→FGR
            // reads as one chain rather than starting fresh here.
            TraceId          = productionOrder.TraceId,
            SupplyNumber     = await _numbers.NextAsync(ManufacturingDocumentPrefix.SupplyRequirement, now),
            ProductUuid      = pmr.MaterialProductUuid,
            VariantUuid      = pmr.MaterialVariantUuid,
            QuantityRequired = shortage,
            DemandSourceType = SupplyDemandSourceType.ProductionMaterialRequirement,
            DemandSourceUuid = pmr.UUID,
            DemandReference  = productionOrder.ProductionNumber,
            SupplyMethod     = supplyMethod,
            WarehouseUuid    = pmr.WarehouseUuid,
            RequiredDate     = pmr.RequiredDate,
            Priority         = productionOrder.Priority,
            Status           = SupplyRequirementStatus.Open,
            CreatedBy        = userId,
            CreatedAt        = now,
            UpdatedAt        = now
        };
        _db.SupplyRequirements.Add(sr);
        await _db.SaveChangesAsync(ct);
        EnqueueTimeline(sr, userId);
        if (_notify is not null) await _notify.SupplyRequirementCreatedAsync(sr, material?.Name ?? sr.ProductUuid.ToString());

        await ActAsync(sr, productionOrder.Id, userId, depth, ct);
        return sr;
    }

    public async Task<Guid> CreateManualAsync(CreateSupplyRequirementRequest req, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (req.QuantityRequired <= 0)
            throw new BadRequestException("Quantity required must be greater than zero.");
        if (!AllocationPriority.IsKnown(req.Priority))
            throw new BadRequestException("Priority must be between 0 (low) and 3 (urgent).");

        var variant = await _inv.ProductVariants.AsNoTracking().Include(v => v.Product)
            .FirstOrDefaultAsync(v => v.Uuid == req.VariantUuid && v.IsActive, ct)
            ?? throw new BadRequestException($"Variant {req.VariantUuid} does not exist or is inactive.");
        if (!await _inv.Warehouses.AsNoTracking().AnyAsync(w => w.Uuid == req.WarehouseUuid && w.IsActive, ct))
            throw new BadRequestException($"Warehouse {req.WarehouseUuid} does not exist or is inactive.");

        var supplyMethod = string.IsNullOrWhiteSpace(req.SupplyMethod)
            ? variant.Product.SupplyMethod
            : req.SupplyMethod.Trim().ToUpperInvariant();

        var now = DateTime.UtcNow;
        var sr = new SupplyRequirement
        {
            // A manual SR starts a trace of its own — there is no document above it to inherit from.
            TraceId          = Guid.NewGuid(),
            SupplyNumber     = await _numbers.NextAsync(ManufacturingDocumentPrefix.SupplyRequirement, now),
            ProductUuid      = variant.Product.Uuid,
            VariantUuid      = variant.Uuid,
            QuantityRequired = req.QuantityRequired,
            // No formal replenishment document exists in this codebase yet, so a person's own
            // request is its own demand record — the nearest of SupplyDemandSourceType's codes to
            // "somebody manually decided more stock is needed".
            DemandSourceType = SupplyDemandSourceType.Replenishment,
            SupplyMethod     = supplyMethod,
            WarehouseUuid    = req.WarehouseUuid,
            RequiredDate     = req.RequiredDate.Date,
            Priority         = req.Priority,
            Status           = SupplyRequirementStatus.Open,
            Notes            = req.Notes?.Trim(),
            CreatedBy        = userId,
            CreatedAt        = now,
            UpdatedAt        = now
        };
        sr.DemandSourceUuid = sr.UUID;
        _db.SupplyRequirements.Add(sr);
        await _db.SaveChangesAsync(ct);
        EnqueueTimeline(sr, userId);
        if (_notify is not null) await _notify.SupplyRequirementCreatedAsync(sr, variant.Product.Name);

        if (req.Act) await ActAsync(sr, null, userId, 0, ct);
        return sr.UUID;
    }

    public async Task CancelAsync(Guid uuid, string reason, int userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new BadRequestException("A reason is required to cancel a supply requirement.");

        var sr = await _db.SupplyRequirements.FirstOrDefaultAsync(s => s.UUID == uuid, ct)
            ?? throw new NotFoundException("Supply requirement", uuid);
        if (!SupplyRequirementStatus.IsLive(sr.Status)) return;

        if (sr.SupplySourceUuid is { } sourceUuid)
        {
            var supplyType = sr.SupplySourceType == SupplySourceType.ProductionOrder
                ? AllocationSupplyType.ProductionOrder : AllocationSupplyType.PurchaseOrder;
            await _engine.CancelSupplyAsync(supplyType, sourceUuid, sr.SupplySourceLineUuid, ct);
        }

        sr.Status    = SupplyRequirementStatus.Cancelled;
        sr.Notes     = AppendNote(sr.Notes, $"Cancelled: {reason.Trim()}");
        sr.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    // ── Acting on a requirement ───────────────────────────────────────────────

    private Task ActAsync(SupplyRequirement sr, int? parentProductionOrderId, int userId, int depth, CancellationToken ct) =>
        sr.SupplyMethod switch
        {
            SMS.Shared.Common.SupplyMethod.Manufacture => ActManufactureAsync(sr, parentProductionOrderId, userId, depth, ct),
            SMS.Shared.Common.SupplyMethod.Transfer    => ActTransferAsync(sr, ct),
            _                                          => ActPurchaseAsync(sr, userId, ct)
        };

    private async Task ActPurchaseAsync(SupplyRequirement sr, int userId, CancellationToken ct)
    {
        var variant = await _inv.ProductVariants.AsNoTracking().Include(v => v.Product)
            .FirstOrDefaultAsync(v => v.Uuid == sr.VariantUuid, ct);

        if (variant?.DefaultSupplierId is not { } supplierId)
        {
            sr.Notes     = AppendNote(sr.Notes, "No default supplier is set on this variant; raise the purchase order manually.");
            sr.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return;
        }

        var warehouseName = await _inv.Warehouses.AsNoTracking()
            .Where(w => w.Uuid == sr.WarehouseUuid).Select(w => w.Name).FirstOrDefaultAsync(ct) ?? string.Empty;
        var supplierNames = await _supplierNames.GetNamesAsync([supplierId]);
        var supplierName  = supplierNames.GetValueOrDefault(supplierId, "Unknown supplier");
        var rate          = await _rates.GetActiveRateAsync(sr.VariantUuid, supplierId, DateOnly.FromDateTime(DateTime.UtcNow));
        var unitPrice     = rate?.VendorUnitCost ?? variant.LastPurchasePrice ?? variant.PurchasePrice;
        var description   = variant.IsDefault ? variant.Product.Name : $"{variant.Product.Name} – {variant.VariantName}";

        var poUuid = await _purchaseOrders.CreateAsync(new CreatePoRequest
        {
            SupplierId            = supplierId,
            SupplierName          = supplierName,
            Lines =
            [
                new CreatePoLineRequest
                {
                    VariantUuid       = sr.VariantUuid,
                    ItemDescription   = description,
                    UnitOfMeasure     = variant.Product.UomCode,
                    Quantity          = sr.QuantityRequired,
                    UnitPrice         = unitPrice,
                    RequiredDate      = sr.RequiredDate,
                    WarehouseId       = sr.WarehouseUuid,
                    WarehouseName     = warehouseName
                }
            ],
            DeliveryDate          = sr.RequiredDate,
            DeliveryWarehouseId   = sr.WarehouseUuid,
            DeliveryWarehouseName = warehouseName,
            Title                 = $"Production supply — {sr.SupplyNumber}",
            Notes                 = $"Raised automatically for supply requirement {sr.SupplyNumber} ({sr.DemandReference})."
        }, userId);

        var po   = await _purchaseOrders.GetByIdAsync(poUuid);
        var line = po?.Lines.FirstOrDefault();

        sr.SupplySourceType      = SupplySourceType.PurchaseOrder;
        sr.SupplySourceUuid      = poUuid;
        sr.SupplySourceLineUuid  = line?.UUID;
        sr.SupplySourceReference = po?.PoNumber;
        sr.QuantityOrdered       = sr.QuantityRequired;
        sr.Status                = SupplyRequirementStatus.Ordered;
        sr.UpdatedAt             = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        if (line is not null)
            await _engine.RegisterSupplyAsync(new AllocationSupplyRegistration(
                AllocationSupplyType.PurchaseOrder, poUuid, line.UUID, po!.PoNumber, sr.VariantUuid, sr.WarehouseUuid,
                sr.QuantityRequired, sr.RequiredDate), ct);
    }

    private async Task ActManufactureAsync(SupplyRequirement sr, int? parentProductionOrderId, int userId, int depth, CancellationToken ct)
    {
        if (depth >= MaxChainDepth)
        {
            sr.Notes     = AppendNote(sr.Notes, $"This manufacturing chain is already {MaxChainDepth} levels deep; raise the next order manually.");
            sr.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
            return;
        }

        // The concrete class, not IProductionOrderService: that interface is public (controllers
        // take it) and cannot expose this internal-entity signature. See CreateChildForSupplyAsync's
        // own doc comment for why this also avoids a constructor cycle between the two services.
        var productionOrders = _services.GetRequiredService<ProductionOrderService>();
        var child = await productionOrders.CreateChildForSupplyAsync(sr, parentProductionOrderId, userId, depth + 1, ct);

        sr.SupplySourceType      = SupplySourceType.ProductionOrder;
        sr.SupplySourceUuid      = child.UUID;
        sr.SupplySourceReference = child.ProductionNumber;
        sr.QuantityOrdered       = sr.QuantityRequired;
        sr.Status                = SupplyRequirementStatus.Ordered;
        sr.UpdatedAt             = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _engine.RegisterSupplyAsync(new AllocationSupplyRegistration(
            AllocationSupplyType.ProductionOrder, child.UUID, null, child.ProductionNumber, sr.VariantUuid, sr.WarehouseUuid,
            sr.QuantityRequired, child.RequiredDate), ct);
    }

    private async Task ActTransferAsync(SupplyRequirement sr, CancellationToken ct)
    {
        sr.Notes     = AppendNote(sr.Notes, "This material transfers between warehouses; raise the transfer manually — there is no transfer-order module yet.");
        sr.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    private static string? AppendNote(string? existing, string note) =>
        string.IsNullOrWhiteSpace(existing) ? note : $"{existing}\n{note}";
}
