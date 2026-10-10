namespace SMS.Shared.Common;

/// <summary>
/// A37 D-12 (OPSB) — a fulfillment route is available only when the module its category needs is on: MANUFACTURE needs
/// MODULE_MANUFACTURING (RTE-01); STOCK always is. One wording for Logistics (route list, create/update, the lookup),
/// Demand (SO line override, resolver warning) and Inventory (variant assignment).
/// </summary>
public static class FulfillmentRouteAvailability
{
    /// <summary>The <c>unavailableReason</c> on route lists (API-CONTRACT §4).</summary>
    public const string ManufacturingOffReason = "Manufacturing is switched off";

    /// <summary>The 400 when an unavailable route is created, changed to, or assigned (API-CONTRACT §4).</summary>
    public const string ManufacturingOffMessage = "Manufacturing is switched off for your organization.";

    /// <summary>
    /// MODULE_MANUFACTURING for the organization: the module gate when registered (grace counts as off), else the tenant
    /// snapshot, else on (hosts and tests without Tenancy).
    /// </summary>
    public static async Task<bool> ManufacturingOnAsync(
        IModuleGate? gate, ITenantSnapshotProvider? tenants, Guid organizationId, CancellationToken ct = default)
    {
        if (gate is not null) return await gate.IsEnabledAsync(organizationId, ModuleCodes.Manufacturing, ct);
        if (tenants is null) return true;
        var tenant = await tenants.GetSnapshotAsync(organizationId);
        return tenant is not null && tenant.EnabledFeatureCodes.Contains(ModuleCodes.Manufacturing);
    }

    /// <summary>Null when a route of <paramref name="category"/> is available, else the reason.</summary>
    public static string? UnavailableReason(string? category, bool manufacturingOn) =>
        !manufacturingOn && FulfillmentRouteCategory.IsManufacture(category) ? ManufacturingOffReason : null;
}
