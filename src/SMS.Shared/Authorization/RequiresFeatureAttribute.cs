namespace SMS.Shared.Authorization;

/// <summary>
/// Marks a controller or action as gated behind an organization feature toggle
/// (SMS.Modules.Tenancy's OrganizationFeatures). Enforced by FeatureAuthorizationFilter.
/// <para>
/// A36 — <paramref name="alternativeFeatureCodes"/> lets an endpoint shared by two features (BOMs: manufacturing and
/// service orders) open when <b>any</b> of them is enabled. Omitted, the gate is the single feature, as before.
/// </para>
/// <para>
/// A37 D-6 — several attributes on one endpoint (controller + action, or two on the action) must <b>all</b> pass, each
/// one still any-of. A sub-feature passes only while its parent module passes too (e.g. a write gated on
/// FEATURE_BOM_MANAGEMENT under a controller gated on MODULE_INVENTORY).
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
public sealed class RequiresFeatureAttribute : Attribute
{
    public string FeatureCode { get; }

    /// <summary>Every feature that opens the endpoint: <see cref="FeatureCode"/> first, then the alternatives.</summary>
    public IReadOnlyList<string> AnyOfFeatureCodes { get; }

    public RequiresFeatureAttribute(string featureCode, params string[] alternativeFeatureCodes)
    {
        FeatureCode       = featureCode;
        AnyOfFeatureCodes = [featureCode, .. alternativeFeatureCodes];
    }
}
