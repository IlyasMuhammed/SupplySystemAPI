using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SMS.Modules.Integration.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Integration.Auth;

internal sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions { }

/// <summary>
/// Authenticates another system calling the data endpoints with <c>X-Tenant-Id</c> + <c>X-Api-Key</c>
/// (plan §2.5).
/// <para>
/// <b>Why this handler checks so much itself.</b> <c>TenantMiddleware</c> and the feature filter run on
/// the default (JWT) principal; an API-key request is anonymous to them, and an anonymous principal is
/// exactly what makes <c>TenantContext.IsSuperAdmin</c> true and bypasses every tenant filter. So the
/// organization's status and its <c>MODULE_INTEGRATION</c> switch are checked here, and the principal
/// built here carries the key's <c>organizationId</c> and <b>never</b> <c>is_super_admin</c> — once it is
/// the request's user, the tenant filter scopes to the key's organization like any other caller.
/// </para>
/// <para>
/// Every refusal looks the same from outside ("invalid API key or tenant") so a caller cannot probe
/// which part was wrong; the log says which, by key prefix only.
/// </para>
/// </summary>
internal sealed class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    private const string GenericFailure = "Invalid API key or tenant.";
    private static readonly TimeSpan LastUsedGranularity = TimeSpan.FromMinutes(1);

    private readonly IntegrationDbContext    _db;
    private readonly ITenantSnapshotProvider _snapshots;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options, ILoggerFactory logger, UrlEncoder encoder,
        IntegrationDbContext db, ITenantSnapshotProvider snapshots)
        : base(options, logger, encoder)
    {
        _db        = db;
        _snapshots = snapshots;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // No key at all: not ours to judge. Another scheme (or [Authorize]'s 401) decides.
        if (!Request.Headers.TryGetValue(ApiKeyDefaults.KeyHeader, out var keyValues) || string.IsNullOrWhiteSpace(keyValues))
            return AuthenticateResult.NoResult();

        var presented = keyValues.ToString().Trim();

        if (!ApiKeyGenerator.IsWellFormed(presented))
            return Refuse("malformed key");

        var prefix = ApiKeyGenerator.PrefixOf(presented);

        if (!Request.Headers.TryGetValue(ApiKeyDefaults.TenantHeader, out var tenantValues)
            || !Guid.TryParse(tenantValues.ToString().Trim(), out var tenantId))
            return Refuse("missing or malformed X-Tenant-Id", prefix);

        // Before authentication there is no tenant, so the lookup is explicitly across organizations —
        // by the unique prefix, one row, never a listing.
        var key = await _db.ApiClientKeys
            .IgnoreQueryFilters()
            .Include(k => k.ApiClient)
            .FirstOrDefaultAsync(k => k.KeyPrefix == prefix, Context.RequestAborted);

        if (key is null)                                     return Refuse("unknown key", prefix);
        if (!ApiKeyGenerator.Matches(presented, key.KeyHash)) return Refuse("hash mismatch", prefix);

        var now = DateTime.UtcNow;
        if (key.RevokedAt is not null)                       return Refuse("revoked key", prefix);
        if (key.ExpiresAt is { } expiresAt && expiresAt <= now) return Refuse("expired key", prefix);
        if (!key.ApiClient.IsActive)                         return Refuse("inactive client", prefix);

        // The tenant header must name the key's own organization — a key never speaks for another.
        if (tenantId != key.OrganizationId)                  return Refuse("tenant mismatch", prefix);

        var snapshot = await _snapshots.GetSnapshotAsync(key.OrganizationId);
        if (snapshot is null || !snapshot.IsActive)          return Refuse("organization inactive", prefix);
        if (!snapshot.EnabledFeatureCodes.Contains(IntegrationFeature.Code))
            return Refuse("integration feature disabled", prefix);

        await TouchLastUsedAsync(key, now);

        var claims = new List<Claim>
        {
            // The same claim a JWT carries, so ITenantContext resolves this organization and the tenant
            // filter scopes every query to it.
            new("organizationId",                key.OrganizationId.ToString()),
            new(IntegrationClaims.ApiClientId,   key.ApiClient.Uuid.ToString()),
            new(IntegrationClaims.ApiClientName, key.ApiClient.Name),
            new(ClaimTypes.Name,                 key.ApiClient.Name)
        };
        claims.AddRange(key.ApiClient.Scopes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(scope => new Claim(IntegrationClaims.Scope, scope)));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    /// <summary>
    /// "Last used" for the screen, written at most once a minute per key so a busy caller does not
    /// turn every request into a write. Never allowed to fail the request it is recording.
    /// </summary>
    private async Task TouchLastUsedAsync(Domain.ApiClientKey key, DateTime now)
    {
        if (key.LastUsedAt is { } last && now - last < LastUsedGranularity) return;

        try
        {
            key.LastUsedAt = now;
            await _db.SaveChangesAsync(Context.RequestAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogDebug(ex, "Could not record last use of API key {KeyPrefix}.", key.KeyPrefix);
            _db.Entry(key).State = EntityState.Unchanged;
        }
    }

    private AuthenticateResult Refuse(string reason, string? prefix = null)
    {
        Logger.LogInformation("API key authentication refused ({Reason}) for key {KeyPrefix}.", reason, prefix ?? "(none)");
        return AuthenticateResult.Fail(GenericFailure);
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode  = StatusCodes.Status401Unauthorized;
        Response.ContentType = "application/json";
        await Response.WriteAsync(JsonSerializer.Serialize(new
        {
            code    = "unauthorized",
            message = $"Send {ApiKeyDefaults.TenantHeader} and a valid {ApiKeyDefaults.KeyHeader}.",
            errors  = Array.Empty<object>()
        }));
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode  = StatusCodes.Status403Forbidden;
        Response.ContentType = "application/json";
        await Response.WriteAsync(JsonSerializer.Serialize(new
        {
            code    = "forbidden",
            message = "This API key is not allowed to do that.",
            errors  = Array.Empty<object>()
        }));
    }
}
