namespace SMS.Shared.Authorization;

/// <summary>
/// Marks an action as operating on a single specific supplier (REQ-2.x). Enforced by
/// SupplierAccessAuthorizationFilter, which reads the supplier id from the named route value.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequiresSupplierAccessAttribute : Attribute
{
    public string RouteParamName { get; }

    public RequiresSupplierAccessAttribute(string routeParamName = "uuid") => RouteParamName = routeParamName;
}
