using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace SMS.Modules.Integration.Auth;

/// <summary>The API-key authentication scheme used by the data endpoints (/api/gateway/quickbooks/v1).</summary>
internal static class ApiKeyDefaults
{
    public const string Scheme       = "IntegrationApiKey";
    public const string TenantHeader = "X-Tenant-Id";
    public const string KeyHeader    = "X-Api-Key";

    /// <summary>Keys look like "sqb_" + 40 url-safe characters; the first <see cref="PrefixLength"/> characters are the lookup prefix.</summary>
    public const string KeyPrefixMarker = "sqb_";
    public const int    PrefixLength    = 12;

    public const string RateLimitPolicy = "integration-api-key";
}

/// <summary>Claims the API-key handler puts on the principal (besides the tenant's "organizationId").</summary>
internal static class IntegrationClaims
{
    public const string ApiClientId   = "api_client_id";
    public const string ApiClientName = "api_client_name";
    public const string Scope         = "api_scope";
}

/// <summary>
/// Refuses (403) an API-key request whose client lacks the scope. Put on data-endpoint actions.
/// A request with no API-key principal at all never reaches this — the scheme's [Authorize] 401s it first.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
internal sealed class RequireApiScopeAttribute : Attribute, IAuthorizationFilter
{
    public string Scope { get; }

    public RequireApiScopeAttribute(string scope) => Scope = scope;

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var user = context.HttpContext.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        if (!user.HasClaim(IntegrationClaims.Scope, Scope))
            context.Result = new ObjectResult(new { code = "insufficient_scope", message = $"This API key lacks the '{Scope}' scope." })
            {
                StatusCode = 403
            };
    }
}
