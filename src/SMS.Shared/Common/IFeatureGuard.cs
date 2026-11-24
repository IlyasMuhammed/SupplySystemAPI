namespace SMS.Shared.Common;

/// <summary>
/// A51 P4 (spec Feature 8) — can this organization use this feature right now?
/// Accessible = the definition exists, it is visible to the org (IsVisible or grandfathered), its status lets the org use it
/// (not ComingSoon unless grandfathered; Deprecated only while the org holds the licence), and the org's own A37 state is
/// licensed + switched on with its parent module on. Plan membership never removes a feature the org is licensed for:
/// the licence is the org's entitlement (plans only seed licences), so "included in plan" is reported
/// (<see cref="FeatureState.IncludedInPlan"/>) and used by the menu, not used to revoke working features.
/// Super admin bypass stays in the callers (filter / menu), exactly as before.
/// </summary>
public interface IFeatureGuard
{
    Task<bool> IsAccessibleAsync(Guid organizationId, string featureCode, CancellationToken ct = default);

    /// <summary>One entry per feature definition, for GET api/tenant/current and the menu.</summary>
    Task<IReadOnlyList<FeatureState>> GetStatesAsync(Guid organizationId, CancellationToken ct = default);
}

/// <param name="Code">Feature code.</param>
/// <param name="Status">Live / Beta / ComingSoon / Deprecated.</param>
/// <param name="IsVisible">Visible to this org (definition visible or org grandfathered).</param>
/// <param name="Usable">The org can use it now (what enabledFeatureCodes lists).</param>
/// <param name="IncludedInPlan">The org's plan includes it, or the org holds a licence row for it.</param>
public sealed record FeatureState(string Code, string Status, bool IsVisible, bool Usable, bool IncludedInPlan);
