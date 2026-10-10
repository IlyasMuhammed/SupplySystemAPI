using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Inventory.Services;

/// <summary>A33 C2 — assigns fulfillment routes to variants (API-CONTRACT.md §4). Always the caller's own organization.</summary>
public interface IVariantFulfillmentRouteService
{
    /// <summary>
    /// Sets the variant's default route, or clears it when <paramref name="routeUuid"/> is null (T-C2-01, T-C2-04).
    /// 404 (<see cref="NotFoundException"/>) for another organization's variant, super admin included; 400 for a route
    /// that is unknown, another organization's (T-C2-02) or inactive (BR-C2-01).
    /// </summary>
    Task<VariantFulfillmentRouteModel> SetRouteAsync(Guid variantUuid, Guid? routeUuid, CancellationToken ct = default);

    /// <summary>
    /// D-14 / BR-C2-02 — sets the route on every active variant of an active product in the category (its
    /// sub-categories included) or only in <see cref="AssignRouteByCategoryRequest.SubCategoryId"/>, where the variant
    /// has no route yet; an existing assignment is never overwritten (T-C2-03). 404 for a route that is not the
    /// organization's; 400 for an inactive route or a category / sub-category that is unknown or not the category's.
    /// </summary>
    Task<FulfillmentRouteBulkAssignResult> AssignByCategoryAsync(
        Guid routeUuid, AssignRouteByCategoryRequest request, CancellationToken ct = default);
}

internal sealed class VariantFulfillmentRouteService : IVariantFulfillmentRouteService
{
    private readonly InventoryDbContext _db;
    private readonly IFulfillmentRouteLookup? _routeLookup;
    private readonly ITenantSnapshotProvider? _tenants;

    /// <param name="routeLookup">
    /// Logistics' route reader. Optional (D-11): a host without Logistics has no routes, so nothing can be assigned —
    /// clearing a route still works.
    /// </param>
    /// <param name="tenants">
    /// A34 D-9 — the organization's enabled modules, for "a MANUFACTURE route needs MODULE_MANUFACTURING". Optional
    /// (a host without Tenancy, unit tests): the check is then skipped, the A33 EffectiveRouteResolver convention.
    /// </param>
    public VariantFulfillmentRouteService(
        InventoryDbContext db, IFulfillmentRouteLookup? routeLookup = null, ITenantSnapshotProvider? tenants = null)
    {
        _db          = db;
        _routeLookup = routeLookup;
        _tenants     = tenants;
    }

    /// <summary>How long a bulk assign waits for another one of the same organization before it gives up with a 409.</summary>
    internal int LockTimeoutMilliseconds { get; init; } = 15_000;

    public async Task<VariantFulfillmentRouteModel> SetRouteAsync(Guid variantUuid, Guid? routeUuid, CancellationToken ct = default)
    {
        // The caller's own organization, explicitly: the EF tenant filter is off for a super admin, and another
        // organization's variant must read as absent to everyone (R-11).
        var org = _db.TenantContext.OrganizationId;
        var variant = await _db.ProductVariants.FirstOrDefaultAsync(v => v.Uuid == variantUuid && v.OrganizationId == org, ct)
            ?? throw new NotFoundException("Product variant not found.");

        FulfillmentRouteSummary? route = null;
        if (routeUuid is { } wanted)
        {
            // BR-C2-01 / R-12 — validated against the variant's (= the caller's) organization, never trusted as sent.
            route = await FindOwnRouteAsync(org, wanted, ct)
                ?? throw new BadRequestException(
                    "That fulfillment route was not found: the route must belong to your organization.");
            if (!route.IsActive)
                throw new BadRequestException($"Fulfillment route '{route.Code}' is inactive. Choose an active route.");
            // A37 D-12 (RTE-01) — Logistics' lookup marks a route whose module is off (MANUFACTURE without Manufacturing).
            if (!route.IsAvailable)
                throw new BadRequestException(FulfillmentRouteAvailability.ManufacturingOffMessage);

            // A34 D-9 then D-3 (API-CONTRACT §4.2): a make-to-order route needs the module and a manufactured product.
            // The product's flags are validated, never written from the route.
            if (route.IsManufacture)
            {
                await RequireManufacturingAsync(org, route, ct);
                var supplyMethod = await _db.Products.IgnoreQueryFilters()
                    .Where(p => p.Id == variant.ProductId && p.OrganizationId == org)
                    .Select(p => p.SupplyMethod)
                    .FirstOrDefaultAsync(ct);
                if (supplyMethod != SupplyMethod.Manufacture)
                    throw new BadRequestException(
                        $"Set the product's supply method to MANUFACTURE first: only manufactured products can use the make-to-order route '{route.Code}'.");
            }
        }

        // A tracked change of this one column: a concurrent edit of the variant's other fields is not overwritten.
        variant.FulfillmentRouteUuid = route?.Uuid;
        await _db.SaveChangesAsync(ct);

        return new VariantFulfillmentRouteModel
        {
            VariantUuid          = variant.Uuid,
            FulfillmentRouteUuid = route?.Uuid,
            FulfillmentRouteCode = route?.Code,
            FulfillmentRouteName = route?.Name
        };
    }

    public async Task<FulfillmentRouteBulkAssignResult> AssignByCategoryAsync(
        Guid routeUuid, AssignRouteByCategoryRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var org = _db.TenantContext.OrganizationId;

        var route = await FindOwnRouteAsync(org, routeUuid, ct)
            ?? throw new NotFoundException("Fulfillment route not found.");
        if (!route.IsActive)
            throw new BadRequestException($"Fulfillment route '{route.Code}' is inactive. Activate it before assigning it.");
        if (!route.IsAvailable) // A37 D-12 (RTE-01)
            throw new BadRequestException(FulfillmentRouteAvailability.ManufacturingOffMessage);

        if (!await _db.ProductCategories.AnyAsync(c => c.Id == request.CategoryId && c.OrganizationId == org, ct))
            throw new BadRequestException("Product category not found.");
        if (request.SubCategoryId is { } subCategoryId
            && !await _db.ProductSubCategories.AnyAsync(
                s => s.Id == subCategoryId && s.CategoryId == request.CategoryId && s.OrganizationId == org, ct))
            throw new BadRequestException("That sub-category was not found in this category.");

        // A34 D-9 / D-3: a make-to-order route needs the module, and only reaches manufactured products; the others
        // in scope are left alone and counted in "skipped" (API-CONTRACT §4.2).
        if (route.IsManufacture)
            await RequireManufacturingAsync(org, route, ct);

        var inScope = VariantsInScope(org, request);
        var assignable = route.IsManufacture
            ? inScope.Where(v => v.Product.SupplyMethod == SupplyMethod.Manufacture)
            : inScope;
        Guid? assigned = route.Uuid;

        return await OneBulkAssignAtATimeAsync(org, async () =>
        {
            // R-15 — one set-based UPDATE … WHERE FulfillmentRouteUuid IS NULL: a row another assignment set first is
            // not matched, so nothing is ever overwritten, whatever runs alongside. The count follows in the same
            // transaction, so "skipped" is exactly the in-scope variants that already had a route.
            var updated = await assignable
                .Where(v => v.FulfillmentRouteUuid == null)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.FulfillmentRouteUuid, assigned)
                                          .SetProperty(v => v.ModifiedAt, DateTime.UtcNow), ct); // A37 D-16: bypasses SaveChanges
            var total = await inScope.CountAsync(ct);

            return new FulfillmentRouteBulkAssignResult
            {
                Updated = updated,
                Skipped = Math.Max(0, total - updated),
                Total   = Math.Max(total, updated)
            };
        }, ct);
    }

    /// <summary>
    /// Active variants of the organization's active products in the category — by the product's own category, or by
    /// its sub-category's parent (a product may carry only a sub-category) — or only in the given sub-category.
    /// </summary>
    private IQueryable<ProductVariant> VariantsInScope(Guid org, AssignRouteByCategoryRequest request)
    {
        var variants = _db.ProductVariants.Where(v =>
            v.OrganizationId == org && v.IsActive && v.Product.OrganizationId == org && v.Product.IsActive);

        if (request.SubCategoryId is { } subCategoryId)
            return variants.Where(v => v.Product.SubCategoryId == subCategoryId);

        var categoryId = request.CategoryId;
        return variants.Where(v =>
            v.Product.CategoryId == categoryId
            || (v.Product.SubCategory != null && v.Product.SubCategory.CategoryId == categoryId));
    }

    internal const string ManufacturingFeature = "MODULE_MANUFACTURING";

    /// <summary>A34 D-9 — a MANUFACTURE route can't be assigned in an organization without MODULE_MANUFACTURING.</summary>
    private async Task RequireManufacturingAsync(Guid org, FulfillmentRouteSummary route, CancellationToken ct)
    {
        if (_tenants is null) return;
        ct.ThrowIfCancellationRequested();
        var tenant = await _tenants.GetSnapshotAsync(org);
        if (tenant is null || !tenant.EnabledFeatureCodes.Contains(ManufacturingFeature))
            throw new BadRequestException(
                $"Manufacturing is not enabled for your organization, so the make-to-order route '{route.Code}' can't be assigned.");
    }

    private async Task<FulfillmentRouteSummary?> FindOwnRouteAsync(Guid org, Guid routeUuid, CancellationToken ct)
    {
        if (_routeLookup is null)
            throw new BadRequestException("Fulfillment routes are not available in this system.");

        var found = await _routeLookup.GetAsync(org, [routeUuid], ct);
        return found.TryGetValue(routeUuid, out var route) ? route : null;
    }

    /// <summary>
    /// Runs one bulk assign so that no other bulk assign of the same organization runs alongside it (the
    /// <c>TaxCodeService</c> arrangement): a transaction holding an exclusive application lock named after the
    /// organization, opened inside the context's execution strategy (a retrying strategy requires it). The
    /// <c>IS NULL</c> guard already keeps two assigns from overwriting each other; the lock makes each one's counts
    /// its own. Other organizations never wait. The in-memory provider has no transactions or locks.
    /// </summary>
    private async Task<T> OneBulkAssignAtATimeAsync<T>(Guid org, Func<Task<T>> work, CancellationToken ct)
    {
        if (!_db.Database.IsRelational())
            return await work();

        if (_db.Database.CurrentTransaction is not null)
        {
            await LockOrganizationsVariantRoutesAsync(org, ct);
            return await work();
        }

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(ct);
            await LockOrganizationsVariantRoutesAsync(org, ct);
            var result = await work();
            await transaction.CommitAsync(ct);
            return result;
        });
    }

    /// <summary>The application lock one organization's bulk route assignments share.</summary>
    internal static string LockResource(Guid org) => $"inventory.variant_routes/{org:N}";

    private async Task LockOrganizationsVariantRoutesAsync(Guid org, CancellationToken ct)
    {
        // sp_getapplock answers through its return value: 0 or 1 granted, below 0 not (timeout, deadlock, cancel).
        var outcome = new SqlParameter("@outcome", SqlDbType.Int) { Direction = ParameterDirection.Output };
        await _db.Database.ExecuteSqlRawAsync(
            "DECLARE @result int; "
          + "EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = @timeout; "
          + "SET @outcome = @result;",
            [
                new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = LockResource(org) },
                new SqlParameter("@timeout", SqlDbType.Int) { Value = LockTimeoutMilliseconds },
                outcome
            ],
            ct);

        if (outcome.Value is not int granted || granted < 0)
            throw new ConflictException(
                "Another route assignment for this organization is running right now, so nothing was changed. Try again in a moment.");
    }
}

/// <summary>
/// A33 — Inventory's side of two shared contracts: each variant's route for Demand's resolver
/// (<see cref="IVariantFulfillmentRoutes"/>), and the "still in use by active product variants" count Logistics asks
/// before it deactivates or deletes a route (<see cref="IFulfillmentRouteUsage"/>, BR-C1-07, L-7). Both are given the
/// organization explicitly and answer for it alone, whoever the ambient caller is — a super admin (no tenant filter),
/// a Hangfire job (D-12 sweep) — so the EF tenant filter is set aside for the explicit one (R-12, R-13).
/// </summary>
internal sealed class VariantFulfillmentRoutes : IVariantFulfillmentRoutes, IFulfillmentRouteUsage, IProductVariantRoutes
{
    private readonly InventoryDbContext _db;

    public VariantFulfillmentRoutes(InventoryDbContext db) => _db = db;

    /// <summary>A37 D-12 / D-27 — a product's active variants and their configured routes, for Logistics' product routes view.</summary>
    public async Task<IReadOnlyList<ProductVariantRoute>?> GetForProductAsync(
        Guid organizationId, int? productId, Guid? productUuid, CancellationToken ct = default)
    {
        var products = _db.Products.IgnoreQueryFilters().Where(p => p.OrganizationId == organizationId);
        products = productId is { } id ? products.Where(p => p.Id == id) : products.Where(p => p.Uuid == productUuid);
        var product = await products.Select(p => (int?)p.Id).FirstOrDefaultAsync(ct);
        if (product is null) return null;

        // The product page's order (ProductDetail: SortOrder, then Id).
        return await _db.ProductVariants.IgnoreQueryFilters()
            .Where(v => v.ProductId == product && v.OrganizationId == organizationId && v.IsActive)
            .OrderBy(v => v.SortOrder ?? 0).ThenBy(v => v.Id)
            .Select(v => new ProductVariantRoute(v.Uuid, v.VariantName, v.Sku, v.FulfillmentRouteUuid))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<Guid, Guid>> GetRouteUuidsAsync(
        Guid organizationId, IReadOnlyCollection<Guid> variantUuids, CancellationToken ct = default)
    {
        var wanted = (variantUuids ?? []).Where(u => u != Guid.Empty).Distinct().ToList();
        if (wanted.Count == 0) return new Dictionary<Guid, Guid>();

        // The route is the variant's own setting, so it is reported whether or not the variant is still active;
        // whether an inactive variant may be sold is the sale order's rule, not this one's.
        var rows = await _db.ProductVariants.IgnoreQueryFilters()
            .Where(v => v.OrganizationId == organizationId && v.FulfillmentRouteUuid != null && wanted.Contains(v.Uuid))
            .Select(v => new { v.Uuid, Route = v.FulfillmentRouteUuid!.Value })
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.Uuid, r => r.Route);
    }

    public async Task<FulfillmentRouteUsageCount> CountUsageAsync(Guid organizationId, Guid routeUuid, CancellationToken ct = default)
    {
        // Variants nobody can sell (inactive, or of a deleted product) don't hold a route up: the gate re-checks the
        // route at confirm anyway (R-8), so one revived later with a deactivated route is blocked, not mis-shipped.
        var count = await _db.ProductVariants.IgnoreQueryFilters()
            .CountAsync(v => v.OrganizationId == organizationId && v.FulfillmentRouteUuid == routeUuid
                             && v.IsActive && v.Product.IsActive, ct);

        return new FulfillmentRouteUsageCount("active product variants", count);
    }
}
