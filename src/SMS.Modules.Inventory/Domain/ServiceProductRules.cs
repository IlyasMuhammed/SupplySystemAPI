using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Inventory.Domain;

/// <summary>A36 D-2 — a product's service configuration as stored.</summary>
internal sealed record ServiceConfiguration(
    string?  InvoicingPolicy,
    string?  BillingModel,
    decimal? EstimatedDurationHours,
    bool     HasServiceBom,
    bool     IsSubcontractable,
    string?  Category          = null,
    bool     RequiresSiteVisit = false)
{
    public static readonly ServiceConfiguration None = new(null, null, null, false, false);

    public static ServiceConfiguration Of(Product p) =>
        new(p.ServiceInvoicingPolicy, p.ServiceBillingModel, p.EstimatedDurationHours, p.HasServiceBom, p.IsSubcontractable,
            p.ServiceCategory, p.RequiresSiteVisit);

    public void ApplyTo(Product p)
    {
        p.ServiceInvoicingPolicy = InvoicingPolicy;
        p.ServiceBillingModel    = BillingModel;
        p.EstimatedDurationHours = EstimatedDurationHours;
        p.HasServiceBom          = HasServiceBom;
        p.IsSubcontractable      = IsSubcontractable;
        p.ServiceCategory        = Category;
        p.RequiresSiteVisit      = RequiresSiteVisit;
    }

    public bool IsTimeAndMaterial => InvoicingPolicy == ServiceInvoicingPolicy.TimeAndMaterial;
}

/// <summary>
/// A36 SVC-P-01..07 (D-2, D-3). A request overlays the product's current configuration (null = unchanged); a value
/// actually sent for a non-service product is refused, and a non-service product keeps no service configuration (a
/// type change away from SERVICE clears it). SVC-P-05 is deliberately not enforced (D-3): it is circular with
/// SVC-BOM-01. SVC-P-06 needs the default variant's selling price, so it is <see cref="EnsureHourlyRate"/>, called by
/// the repository once that price is known.
/// </summary>
internal static class ServiceProductRules
{
    public const string InvoicingPolicyNotService = "Invoicing policy is only applicable to service products";
    public const string BillingModelNotService    = "Billing model is only applicable to service products";
    public const string DurationNotPositive       = "Estimated duration must be a positive number";
    public const string ServiceBomNotService      = "Service BOM is only applicable to service products";
    public const string SubcontractNotService     = "Subcontract flag is only applicable to service products";
    public const string HourlyRateRequired        = "Hourly rate (selling price of the default variant) is required for Time & Material services";
    // A37 D-10 (API-CONTRACT §2).
    public const string CategoryNotService        = "Service category is only applicable to service products";
    public const string SiteVisitNotService       = "Site visit is only applicable to service products";

    /// <summary>Create: everything is "sent" (there is nothing to keep).</summary>
    /// <exception cref="BadRequestException">The first rule the combination breaks.</exception>
    public static ServiceConfiguration Resolve(
        string productType, ServiceConfiguration? current,
        string? invoicingPolicy, string? billingModel, decimal? estimatedDurationHours,
        bool? hasServiceBom, bool? isSubcontractable,
        string? serviceCategory = null, bool? requiresSiteVisit = null) =>
        Resolve(productType, current,
            invoicingPolicy, invoicingPolicy is not null, billingModel, billingModel is not null,
            estimatedDurationHours, estimatedDurationHours.HasValue, hasServiceBom, isSubcontractable,
            serviceCategory, serviceCategory is not null, requiresSiteVisit);

    /// <summary>
    /// Patch: a code or duration that was <b>sent</b> replaces the stored one — an explicit null clears it; one not
    /// sent keeps it. The flags are null = unchanged, false clears.
    /// </summary>
    /// <exception cref="BadRequestException">The first rule the combination breaks.</exception>
    public static ServiceConfiguration Resolve(
        string productType, ServiceConfiguration? current,
        string? invoicingPolicy, bool policySent, string? billingModel, bool billingSent,
        decimal? estimatedDurationHours, bool durationSent,
        bool? hasServiceBom, bool? isSubcontractable,
        string? serviceCategory = null, bool categorySent = false, bool? requiresSiteVisit = null)
    {
        var policy   = Normalise(invoicingPolicy);
        var billing  = Normalise(billingModel);
        var category = Normalise(serviceCategory);

        if (productType != ProductType.Service)
        {
            // SVC-P-01, 02, 03, 04, 07 — in the spec's order. "false" is not a value worth refusing.
            if (policy is not null)                    throw new BadRequestException(InvoicingPolicyNotService);
            if (billing is not null)                   throw new BadRequestException(BillingModelNotService);
            if (estimatedDurationHours is <= 0)        throw new BadRequestException(DurationNotPositive);
            if (hasServiceBom == true)                 throw new BadRequestException(ServiceBomNotService);
            if (isSubcontractable == true)             throw new BadRequestException(SubcontractNotService);
            if (category is not null)                  throw new BadRequestException(CategoryNotService);
            if (requiresSiteVisit == true)             throw new BadRequestException(SiteVisitNotService);
            return ServiceConfiguration.None;
        }

        var baseline = current ?? ServiceConfiguration.None;
        var resolved = new ServiceConfiguration(
            policySent   ? policy  : baseline.InvoicingPolicy,
            billingSent  ? billing : baseline.BillingModel,
            durationSent ? estimatedDurationHours : baseline.EstimatedDurationHours,
            hasServiceBom          ?? baseline.HasServiceBom,
            isSubcontractable      ?? baseline.IsSubcontractable,
            categorySent ? category : baseline.Category,
            requiresSiteVisit      ?? baseline.RequiresSiteVisit);

        if (resolved.InvoicingPolicy is { } p && !ServiceInvoicingPolicy.IsKnown(p))
            throw new BadRequestException(
                $"'{p}' is not an invoicing policy. Use one of: {string.Join(", ", ServiceInvoicingPolicy.All)}.");
        if (resolved.BillingModel is { } b && !ServiceBillingModel.IsKnown(b))
            throw new BadRequestException(
                $"'{b}' is not a billing model. Use one of: {string.Join(", ", ServiceBillingModel.All)}.");
        if (resolved.EstimatedDurationHours is <= 0)
            throw new BadRequestException(DurationNotPositive);
        if (resolved.Category is { } sc && !ServiceCategory.IsKnown(sc))
            throw new BadRequestException(
                $"'{sc}' is not a service category. Use one of: {string.Join(", ", ServiceCategory.All)}.");

        return resolved;
    }

    /// <summary>SVC-P-06 (D-2) — a Time &amp; Material service is priced per hour by its default variant's selling price.</summary>
    public static void EnsureHourlyRate(ServiceConfiguration configuration, decimal? defaultVariantSellingPrice)
    {
        if (configuration.IsTimeAndMaterial && !(defaultVariantSellingPrice > 0))
            throw new BadRequestException(HourlyRateRequired);
    }

    private static string? Normalise(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();
}
