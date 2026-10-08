using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Inventory.Services.LeadTimes;

/// <summary>
/// A34 PB-05 (D-26) — a variant's 8 lead-time components (API-CONTRACT §4.5). Always the caller's own organization:
/// another organization's variant is 404, super admin included.
/// </summary>
public interface IVariantLeadTimeService
{
    Task<VariantLeadTimesModel> GetAsync(Guid variantUuid, CancellationToken ct = default);

    /// <summary>Replaces all 8 overrides (null = use the default); each 0–3650.</summary>
    Task<VariantLeadTimesModel> UpdateAsync(Guid variantUuid, UpdateVariantLeadTimesRequest request, CancellationToken ct = default);
}

internal sealed class VariantLeadTimeService : IVariantLeadTimeService
{
    private readonly InventoryDbContext _db;
    private readonly LeadTimeInputsLoader _loader;
    private readonly IFulfillmentRouteLookup? _routes;

    /// <param name="routes">Logistics' route reader; optional (no Logistics = no route: judged as STOCK without SHIP).</param>
    public VariantLeadTimeService(InventoryDbContext db, LeadTimeInputsLoader loader, IFulfillmentRouteLookup? routes = null)
    {
        _db     = db;
        _loader = loader;
        _routes = routes;
    }

    public async Task<VariantLeadTimesModel> GetAsync(Guid variantUuid, CancellationToken ct = default)
    {
        var org = _db.TenantContext.OrganizationId;
        var defaults = await _loader.ReadDefaultsAsync(org, ct);
        var facts = (await _loader.LoadAsync(org, [variantUuid], defaults, ct)).GetValueOrDefault(variantUuid)
            ?? throw new NotFoundException("Product variant not found.");

        var (route, fromOrgDefault) = await LeadTimeRoutes.JudgeAsync(_routes, org, facts.FulfillmentRouteUuid, ct);
        var manufacture = route?.IsManufacture ?? false;
        var shipping    = route?.RequiresShipping ?? false;

        var components = LeadTimeComponentCode.All.Select(code =>
        {
            var resolved = LeadTimeResolver.Resolve(code, facts.Inputs);
            var fallback = LeadTimeResolver.Default(code, facts.Inputs);
            return new VariantLeadTimeComponentModel
            {
                Code            = code,
                Name            = LeadTimeResolver.Name(code),
                Field           = LeadTimeResolver.Field(code),
                StoredDays      = facts.Inputs.Overrides.For(code),
                ResolvedDays    = resolved.Days,
                Source          = resolved.Source,
                Detail          = resolved.Detail,
                Visible         = LeadTimeResolver.IsVisible(code, manufacture, shipping),
                IncludedInTotal = LeadTimeResolver.IsIncludedInTotal(code, manufacture, shipping),
                DefaultDays     = fallback.Days,
                DefaultSource   = fallback.Source
            };
        }).ToList();

        return new VariantLeadTimesModel
        {
            VariantUuid         = facts.VariantUuid,
            ProductId           = facts.ProductId,
            Sku                 = facts.Sku,
            VariantName         = facts.VariantName,
            RouteUuid           = route?.Uuid,
            RouteCode           = route?.Code,
            RouteName           = route?.Name,
            RouteCategory       = manufacture ? FulfillmentRouteCategory.Manufacture : FulfillmentRouteCategory.Stock,
            RouteFromOrgDefault = fromOrgDefault,
            RequiresShipping    = shipping,
            Components          = components,
            TotalDays           = components.Where(c => c.IncludedInTotal).Sum(c => c.ResolvedDays)
        };
    }

    public async Task<VariantLeadTimesModel> UpdateAsync(Guid variantUuid, UpdateVariantLeadTimesRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var org = _db.TenantContext.OrganizationId;

        // Own organization explicitly (a super admin has no tenant filter); another organization's variant is absent.
        var variant = await _db.ProductVariants.IgnoreQueryFilters()
                          .FirstOrDefaultAsync(v => v.Uuid == variantUuid && v.OrganizationId == org, ct)
                      ?? throw new NotFoundException("Product variant not found.");

        var supplier      = Check(request.SupplierLeadTimeDays,      LeadTimeComponentCode.Supplier);
        var manufacturing = Check(request.ManufacturingLeadTimeDays, LeadTimeComponentCode.Manufacturing);
        var mfgBuffer     = Check(request.ManufacturingBufferDays,   LeadTimeComponentCode.MfgBuffer);
        var qc            = Check(request.QualityInspectionDays,     LeadTimeComponentCode.Qc);
        var transfer      = Check(request.InternalTransferDays,      LeadTimeComponentCode.Transfer);
        var pickPack      = Check(request.PickPackDays,              LeadTimeComponentCode.PickPack);
        var shipping      = Check(request.ShippingLeadTimeDays,      LeadTimeComponentCode.Shipping);
        var salesBuffer   = Check(request.SalesBufferDays,           LeadTimeComponentCode.SalesBuffer);

        // A tracked change of these eight columns only: a concurrent edit of the variant's other fields survives.
        variant.LeadTimeDays              = supplier;      // D-11: the supplier override
        variant.ManufacturingLeadTimeDays = manufacturing;
        variant.ManufacturingBufferDays   = mfgBuffer;
        variant.QualityInspectionDays     = qc;
        variant.InternalTransferDays      = transfer;
        variant.PickPackDays              = pickPack;
        variant.ShippingLeadTimeDays      = shipping;
        variant.SalesBufferDays           = salesBuffer;
        await _db.SaveChangesAsync(ct);

        return await GetAsync(variantUuid, ct);
    }

    /// <summary>Null clears the override; otherwise 0–3650 (BR-C3-01; API-CONTRACT §4.5 message).</summary>
    private static int? Check(int? value, string code)
    {
        if (value is int days && (days < 0 || days > LeadTimeResolver.MaxVariantDays))
            throw new BadRequestException(
                $"{LeadTimeResolver.Name(code)} days must be between 0 and {LeadTimeResolver.MaxVariantDays}.");
        return value;
    }
}

/// <summary>Which route a lead time is judged by (BR-C3-04/05, §4.6): the given one, else the variant's, else the SHIP default.</summary>
internal static class LeadTimeRoutes
{
    /// <returns>
    /// The variant's own route when it still resolves in the organization (active or not); otherwise the organization's
    /// default for SHIP orders, with <c>FromOrgDefault</c> true. Null when neither exists or routes are off.
    /// </returns>
    public static async Task<(FulfillmentRouteSummary? Route, bool FromOrgDefault)> JudgeAsync(
        IFulfillmentRouteLookup? routes, Guid organizationId, Guid? variantRouteUuid, CancellationToken ct)
    {
        if (routes is null) return (null, true);

        if (variantRouteUuid is { } own)
        {
            var found = await routes.GetAsync(organizationId, [own], ct);
            if (found.TryGetValue(own, out var route)) return (route, false);
        }

        var defaults = await routes.GetOrgDefaultsAsync(organizationId, ct);
        return (defaults.Shipping, true);
    }
}
