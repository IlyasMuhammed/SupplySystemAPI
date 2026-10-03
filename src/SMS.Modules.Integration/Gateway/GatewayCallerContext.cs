using Microsoft.AspNetCore.Http;
using SMS.Modules.Integration.Auth;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Gateway;

/// <summary>Who is calling the gateway — decides the SourceSystem records are mapped under.</summary>
internal interface IGatewayCallerContext
{
    /// <summary><see cref="QuickBooksSourceSystems.Scm"/> in-process; the API client's name over HTTP.</summary>
    string SourceSystem { get; }

    /// <summary>
    /// True for an API-key client (another system over HTTP). Such callers have no
    /// <see cref="IQuickBooksSource"/>: when a document waits on a record, they are told which records
    /// to send instead of the gateway asking for them.
    /// </summary>
    bool IsExternalClient { get; }

    string? ApiClientId { get; }
}

internal sealed class HttpGatewayCallerContext : IGatewayCallerContext
{
    private const int MaxSourceSystemLength = 100;

    private readonly IHttpContextAccessor _accessor;

    public HttpGatewayCallerContext(IHttpContextAccessor accessor) => _accessor = accessor;

    private System.Security.Claims.ClaimsPrincipal? User => _accessor.HttpContext?.User;

    public string? ApiClientId => User?.FindFirst(IntegrationClaims.ApiClientId)?.Value;

    public bool IsExternalClient =>
        User?.Identity?.IsAuthenticated == true
        && (User.HasClaim(c => c.Type == IntegrationClaims.ApiClientName) || User.HasClaim(c => c.Type == IntegrationClaims.ApiClientId));

    public string SourceSystem
    {
        get
        {
            if (!IsExternalClient) return QuickBooksSourceSystems.Scm;

            var name = User!.FindFirst(IntegrationClaims.ApiClientName)?.Value?.Trim();
            if (string.IsNullOrEmpty(name)) name = "api:" + ApiClientId;

            // An external client can never write into SCM's own records, whatever it is called.
            if (string.Equals(name, QuickBooksSourceSystems.Scm, StringComparison.OrdinalIgnoreCase)) name = "api:" + name;

            return name.Length <= MaxSourceSystemLength ? name : name[..MaxSourceSystemLength];
        }
    }
}
