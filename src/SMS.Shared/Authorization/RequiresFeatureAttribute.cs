namespace SMS.Shared.Authorization;

/// <summary>
/// Marks a controller or action as gated behind an organization feature toggle
/// (SMS.Modules.Tenancy's OrganizationFeatures). Enforced by FeatureAuthorizationFilter.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequiresFeatureAttribute : Attribute
{
    public string FeatureCode { get; }

    public RequiresFeatureAttribute(string featureCode) => FeatureCode = featureCode;
}
