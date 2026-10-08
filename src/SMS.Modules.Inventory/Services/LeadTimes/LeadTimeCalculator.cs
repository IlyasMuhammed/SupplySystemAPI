using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Inventory.Services.LeadTimes;

/// <summary>A34 C4 — <c>POST api/lead-time/calculate-manufacturing</c>: the BOM-aware manufacturing lead time, level by level.</summary>
public interface IManufacturingLeadTimeCalculator
{
    /// <exception cref="SMS.Shared.Exceptions.NotFoundException">The variant is not the organization's.</exception>
    /// <exception cref="SMS.Shared.Exceptions.BadRequestException">Quantity ≤ 0, or the product is not MANUFACTURE.</exception>
    Task<ManufacturingLeadTimeNodeModel> CalculateManufacturingAsync(
        Guid organizationId, Guid variantUuid, decimal quantity, CancellationToken ct = default);
}

/// <summary>D-27 cache key: the organization is part of it, and the route is the <b>resolved</b> one (REV-03).</summary>
internal sealed record LeadTimeCacheKey(Guid OrganizationId, Guid VariantUuid, Guid? RouteUuid, decimal Quantity, DateTime UtcDate);

/// <summary>
/// A34 PC-02..04 — the lead-time calculator (API-CONTRACT §4.6, analysis §4.5):
/// <list type="number">
/// <item>route = the request's (400 when it isn't the organization's), else the variant's, else the SHIP default;</item>
/// <item>MANUFACTURE: MANUFACTURING (BOM-aware total, D-12/13) + MFG_BUFFER; otherwise SUPPLIER (D-11), 0 "in stock"
/// when the best single warehouse covers the quantity (D-14);</item>
/// <item>then QC, TRANSFER, PICK_PACK, SHIPPING (route has SHIP), SALES_BUFFER. Optional components with 0 days are
/// left out; PICK_PACK always shows, SHIPPING whenever the route has SHIP.</item>
/// </list>
/// The date-independent part is cached for 5 minutes per (org, variant, resolved route, exact quantity, UTC date)
/// (D-27, REV-03); the dates are computed from it on every call. Dates are calendar days, date-only.
/// </summary>
internal sealed class LeadTimeCalculator : ILeadTimeCalculator, IManufacturingLeadTimeCalculator
{
    internal static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly InventoryDbContext _db;
    private readonly LeadTimeInputsLoader _loader;
    private readonly IMemoryCache _cache;
    private readonly IFulfillmentRouteLookup? _routes;
    private readonly IBomStructureReader? _boms;

    /// <param name="routes">Logistics' route reader; optional (no routes: judged as STOCK without SHIP).</param>
    /// <param name="boms">Material's BOM reader; optional (no BOMs: only the variant's own level days count, with a warning).</param>
    public LeadTimeCalculator(
        InventoryDbContext db, LeadTimeInputsLoader loader, IMemoryCache cache,
        IFulfillmentRouteLookup? routes = null, IBomStructureReader? boms = null)
    {
        _db     = db;
        _loader = loader;
        _cache  = cache;
        _routes = routes;
        _boms   = boms;
    }

    public async Task<LeadTimeResult> CalculateAsync(Guid organizationId, LeadTimeRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        RequirePositive(request.Quantity);

        // The variant first: another organization's variant is a 404 before any cache entry can answer (R-11).
        var variant = await _db.ProductVariants.IgnoreQueryFilters().AsNoTracking()
                          .Where(v => v.Uuid == request.VariantUuid && v.OrganizationId == organizationId)
                          .Select(v => new { v.FulfillmentRouteUuid })
                          .FirstOrDefaultAsync(ct)
                      ?? throw new NotFoundException("Product variant not found.");

        var route = await ResolveRouteAsync(organizationId, request.RouteUuid, variant.FulfillmentRouteUuid, ct);

        var today = DateTime.UtcNow.Date;
        var key = new LeadTimeCacheKey(organizationId, request.VariantUuid, route?.Uuid, request.Quantity, today);
        if (!_cache.TryGetValue(key, out CachedLeadTime? cached) || cached is null)
        {
            cached = await ComputeAsync(organizationId, request.VariantUuid, request.Quantity, route, ct);
            _cache.Set(key, cached, CacheDuration);
        }

        // Per call (REV-03): the dates depend on the request, not on the cached breakdown.
        var earliest = AsDate(today.AddDays(cached.TotalDays));
        DateTime? latestStart = null;
        bool? meets = null;
        if (request.RequestedDate is { } requestedAt)
        {
            var requested = AsDate(requestedAt.Date);
            latestStart = AsDate(requested.AddDays(-cached.TotalDays));
            meets       = earliest <= requested;
        }

        return new LeadTimeResult(
            cached.TotalDays, earliest, latestStart, meets, cached.RouteUuid, cached.RouteCode, cached.RouteCategory,
            cached.Components, cached.CalculatedAt);
    }

    public async Task<ManufacturingLeadTimeNodeModel> CalculateManufacturingAsync(
        Guid organizationId, Guid variantUuid, decimal quantity, CancellationToken ct = default)
    {
        RequirePositive(quantity);
        var defaults = await _loader.ReadDefaultsAsync(organizationId, ct);
        var facts = (await _loader.LoadAsync(organizationId, [variantUuid], defaults, ct)).GetValueOrDefault(variantUuid)
            ?? throw new NotFoundException("Product variant not found.");
        if (!facts.IsManufactured)
            throw new BadRequestException(
                $"{facts.DisplayName} is not manufactured: set the product's supply method to MANUFACTURE to calculate a manufacturing lead time.");

        return await Tree().BuildAsync(organizationId, facts, quantity, defaults, ct);
    }

    private async Task<CachedLeadTime> ComputeAsync(
        Guid organizationId, Guid variantUuid, decimal quantity, FulfillmentRouteSummary? route, CancellationToken ct)
    {
        var defaults = await _loader.ReadDefaultsAsync(organizationId, ct);
        var facts = (await _loader.LoadAsync(organizationId, [variantUuid], defaults, ct)).GetValueOrDefault(variantUuid)
            ?? throw new NotFoundException("Product variant not found.");

        var manufacture = route?.IsManufacture ?? false;
        var shipping    = route?.RequiresShipping ?? false;
        var components  = new List<LeadTimeComponentResult>();

        void Add(string code, ResolvedLeadTime value) =>
            components.Add(new LeadTimeComponentResult(code, LeadTimeResolver.Name(code), value.Days, value.Source, value.Detail));
        void AddUnlessZero(string code)
        {
            var value = LeadTimeResolver.Resolve(code, facts.Inputs);
            if (value.Days > 0) Add(code, value);
        }

        if (manufacture)
        {
            var tree = await Tree().BuildAsync(organizationId, facts, quantity, defaults, ct);
            Add(LeadTimeComponentCode.Manufacturing, ManufacturingComponent(tree));
            AddUnlessZero(LeadTimeComponentCode.MfgBuffer);
        }
        else
        {
            var supplier = await SupplierComponentAsync(organizationId, facts, quantity, ct);
            if (supplier.Days > 0 || supplier.Source == LeadTimeSource.InStock)
                Add(LeadTimeComponentCode.Supplier, supplier);
        }

        AddUnlessZero(LeadTimeComponentCode.Qc);
        AddUnlessZero(LeadTimeComponentCode.Transfer);
        Add(LeadTimeComponentCode.PickPack, LeadTimeResolver.Resolve(LeadTimeComponentCode.PickPack, facts.Inputs));
        if (shipping)
            Add(LeadTimeComponentCode.Shipping, LeadTimeResolver.Resolve(LeadTimeComponentCode.Shipping, facts.Inputs));
        AddUnlessZero(LeadTimeComponentCode.SalesBuffer);

        return new CachedLeadTime(
            components.Sum(c => c.Days), route?.Uuid, route?.Code,
            manufacture ? FulfillmentRouteCategory.Manufacture : FulfillmentRouteCategory.Stock,
            components.AsReadOnly(), DateTime.UtcNow);   // shared by every caller of the cached entry
    }

    /// <summary>D-14: the supplier lead counts only when no single warehouse has the quantity free.</summary>
    private async Task<ResolvedLeadTime> SupplierComponentAsync(
        Guid organizationId, VariantLeadTimeFacts facts, decimal quantity, CancellationToken ct)
    {
        var stock = await Tree().FreeStockAsync(organizationId, [facts.VariantId], ct);
        var free = stock.FreeFor(facts.VariantId, warehouseId: null);
        return free >= quantity
            ? new ResolvedLeadTime(0, LeadTimeSource.InStock, $"{free:0.####} free in one warehouse")
            : LeadTimeResolver.Resolve(LeadTimeComponentCode.Supplier, facts.Inputs);
    }

    /// <summary>The MANUFACTURING component: the tree's total; source BOM when a BOM was found, else the level days' own.</summary>
    private static ResolvedLeadTime ManufacturingComponent(ManufacturingLeadTimeNodeModel tree)
    {
        var parts = new List<string>();
        if (tree.BomUuid is not null)
        {
            var levels = ManufacturingLeadTimeTree.Depth(tree);
            parts.Add($"{tree.BomNumber} v{tree.BomVersion}, {levels} level{(levels == 1 ? "" : "s")}");
        }
        parts.AddRange(tree.Warnings);
        return new ResolvedLeadTime(
            tree.TotalDays, tree.BomUuid is not null ? LeadTimeSource.Bom : tree.LevelDaysSource,
            parts.Count == 0 ? null : string.Join("; ", parts));
    }

    private async Task<FulfillmentRouteSummary?> ResolveRouteAsync(
        Guid organizationId, Guid? requested, Guid? variantRoute, CancellationToken ct)
    {
        if (requested is { } wanted)
        {
            // As A33 BR-C3-01: a route is only ever taken from the caller's own organization.
            var found = _routes is null
                ? new Dictionary<Guid, FulfillmentRouteSummary>()
                : await _routes.GetAsync(organizationId, [wanted], ct);
            return found.TryGetValue(wanted, out var route)
                ? route
                : throw new BadRequestException("Route not found in your organization.");
        }

        return (await LeadTimeRoutes.JudgeAsync(_routes, organizationId, variantRoute, ct)).Route;
    }

    private ManufacturingLeadTimeTree Tree() => new(_db, _loader, _boms);

    private static void RequirePositive(decimal quantity)
    {
        if (quantity <= 0) throw new BadRequestException("Quantity must be greater than zero.");
    }

    /// <summary>Date-only values travel as "yyyy-MM-ddT00:00:00" with no Z (API-CONTRACT §1).</summary>
    private static DateTime AsDate(DateTime value) => DateTime.SpecifyKind(value.Date, DateTimeKind.Unspecified);

    /// <summary>The date-independent part of a result: what the cache holds.</summary>
    private sealed record CachedLeadTime(
        int TotalDays, Guid? RouteUuid, string? RouteCode, string RouteCategory,
        IReadOnlyList<LeadTimeComponentResult> Components, DateTime CalculatedAt);
}
