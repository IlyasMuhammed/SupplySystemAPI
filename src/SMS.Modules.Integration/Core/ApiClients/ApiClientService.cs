using Microsoft.EntityFrameworkCore;
using SMS.Modules.Integration.Auth;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Settings;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Models;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Integration.Core.ApiClients;

/// <summary>
/// The systems allowed to call the data endpoints with an API key, and their keys (plan QBI-06/13).
/// Managed from SCM's screens by a person with <c>INTEGRATION_MANAGE</c>.
/// </summary>
public interface IApiClientService
{
    Task<List<ApiClientModel>> ListAsync(CancellationToken ct = default);
    Task<ApiClientModel> CreateAsync(CreateApiClientRequest request, int userId, CancellationToken ct = default);

    /// <summary>The only time the key itself is ever returned.</summary>
    Task<IssuedApiKeyModel> IssueKeyAsync(Guid clientId, IssueApiKeyRequest request, int userId, CancellationToken ct = default);

    Task<ApiClientModel> RevokeKeyAsync(Guid clientId, Guid keyId, int userId, CancellationToken ct = default);

    /// <summary>Switches the client off and revokes every key it has. Its mapped records stay.</summary>
    Task<ApiClientModel> DeactivateAsync(Guid clientId, int userId, CancellationToken ct = default);
}

internal sealed class ApiClientService : IApiClientService
{
    /// <summary>Two, so a key can be rotated without downtime: issue the new one, move the caller, revoke the old.</summary>
    internal const int MaxActiveKeys = 2;
    internal const int MaxNameLength = 100;

    private readonly IntegrationDbContext _db;
    private readonly IConnectionAccessor  _accessor;

    public ApiClientService(IntegrationDbContext db, IConnectionAccessor accessor)
    {
        _db       = db;
        _accessor = accessor;
    }

    /// <summary>
    /// The current organization's clients, limited <b>explicitly</b>, not only through the tenant query filter: a
    /// super admin's requests bypass that filter, and listing another organization's clients, minting a key for one
    /// (the key authenticates as that client's organization), revoking its keys or deactivating it from here would
    /// reach across tenants — the same reason <c>ConnectionAccessor</c> and <c>SyncAdminService.FindMapAsync</c> filter
    /// explicitly. Names are unique per organization (index (OrganizationId, Name)), so the name check is scoped too.
    /// </summary>
    private IQueryable<ApiClient> OwnClients
    {
        get
        {
            var organizationId = _db.TenantContext.OrganizationId;
            return _db.ApiClients.Where(c => c.OrganizationId == organizationId);
        }
    }

    public async Task<List<ApiClientModel>> ListAsync(CancellationToken ct = default)
    {
        var clients = await OwnClients.AsNoTracking()
            .Include(c => c.Keys)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        return clients.Select(c => ToModel(c, now)).ToList();
    }

    public async Task<ApiClientModel> CreateAsync(CreateApiClientRequest request, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length == 0)
            throw new BadRequestException("An API client needs a name — it is also the source system its records are kept under.");
        if (name.Length > MaxNameLength)
            throw new BadRequestException($"An API client's name can be at most {MaxNameLength} characters.");
        // SCM's own in-process records are kept under this source name; a client sharing it would read
        // as SCM on every screen (the gateway records it as "api:SCM", but the confusion is avoidable).
        if (string.Equals(name, SMS.Shared.Integration.QuickBooks.QuickBooksSourceSystems.Scm, StringComparison.OrdinalIgnoreCase))
            throw new BadRequestException($"'{name}' is reserved for SCM's own records. Choose another name.");

        var scopes = NormalizeScopes(request.Scopes);

        // The unique index is the real guard; this gives the common case a readable message. The
        // name is the SourceSystem this client's records are mapped under, so two clients sharing one
        // (even differing only by case) would write into each other's records.
        var lowered = name.ToLower();
        if (await OwnClients.AnyAsync(c => c.Name.ToLower() == lowered, ct))
            throw new ConflictException($"An API client named '{name}' already exists.");

        var client = new ApiClient
        {
            Name      = name,
            Scopes    = string.Join(",", scopes),
            IsActive  = true,
            CreatedBy = userId,
            CreatedAt = DateTime.UtcNow
        };
        _db.ApiClients.Add(client);

        IntegrationAudit.Add(_db, await AuditConnectionIdAsync(ct), AuditAreas.ApiClient, "Created",
            null, new { client.Uuid, client.Name, client.Scopes }, userId);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException)
        {
            throw new ConflictException($"An API client named '{name}' already exists.");
        }

        return ToModel(client, DateTime.UtcNow);
    }

    public async Task<IssuedApiKeyModel> IssueKeyAsync(
        Guid clientId, IssueApiKeyRequest request, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var client = await LoadAsync(clientId, ct);
        var now    = DateTime.UtcNow;

        if (!client.IsActive)
            throw new BadRequestException($"API client '{client.Name}' is deactivated; it cannot be given new keys.");

        if (request.ExpiresAt is { } expiresAt && ToUtc(expiresAt) <= now)
            throw new BadRequestException("A key's expiry must be in the future.");

        var active = client.Keys.Count(k => IsKeyActive(k, now));
        if (active >= MaxActiveKeys)
            throw new BadRequestException(
                $"API client '{client.Name}' already has {MaxActiveKeys} active keys. Revoke one before issuing another.");

        // The prefix is unique across every organization (it is how a request finds its key before
        // anyone is authenticated). 48 random bits make a clash vanishingly rare; check anyway.
        string key, prefix;
        do
        {
            key    = ApiKeyGenerator.NewKey();
            prefix = ApiKeyGenerator.PrefixOf(key);
        }
        while (await _db.ApiClientKeys.IgnoreQueryFilters().AnyAsync(k => k.KeyPrefix == prefix, ct));

        var row = new ApiClientKey
        {
            OrganizationId = client.OrganizationId,
            ApiClientId    = client.Id,
            KeyPrefix      = prefix,
            KeyHash        = ApiKeyGenerator.Hash(key),
            CreatedAt      = now,
            CreatedBy      = userId,
            ExpiresAt      = request.ExpiresAt is { } e ? ToUtc(e) : null
        };
        client.Keys.Add(row);

        IntegrationAudit.Add(_db, await AuditConnectionIdAsync(ct), AuditAreas.ApiClient, "KeyIssued",
            null, new { Client = client.Uuid, Key = row.Uuid, row.KeyPrefix, row.ExpiresAt }, userId);
        await _db.SaveChangesAsync(ct);

        return new IssuedApiKeyModel
        {
            KeyId     = row.Uuid,
            ApiKey    = key,
            KeyPrefix = prefix,
            TenantId  = client.OrganizationId,
            ExpiresAt = row.ExpiresAt
        };
    }

    public async Task<ApiClientModel> RevokeKeyAsync(Guid clientId, Guid keyId, int userId, CancellationToken ct = default)
    {
        var client = await LoadAsync(clientId, ct);
        var key    = client.Keys.FirstOrDefault(k => k.Uuid == keyId)
            ?? throw new NotFoundException("API key", keyId);

        if (key.RevokedAt is null)
        {
            key.RevokedAt = DateTime.UtcNow;
            IntegrationAudit.Add(_db, await AuditConnectionIdAsync(ct), AuditAreas.ApiClient, "KeyRevoked",
                null, new { Client = client.Uuid, Key = key.Uuid, key.KeyPrefix }, userId);
            await _db.SaveChangesAsync(ct);
        }

        return ToModel(client, DateTime.UtcNow);
    }

    public async Task<ApiClientModel> DeactivateAsync(Guid clientId, int userId, CancellationToken ct = default)
    {
        var client = await LoadAsync(clientId, ct);
        var now    = DateTime.UtcNow;

        var revoked = client.Keys.Where(k => k.RevokedAt is null).ToList();
        foreach (var key in revoked) key.RevokedAt = now;

        if (client.IsActive || revoked.Count > 0)
        {
            client.IsActive = false;
            IntegrationAudit.Add(_db, await AuditConnectionIdAsync(ct), AuditAreas.ApiClient, "Deactivated",
                new { client.Uuid, client.Name, IsActive = true },
                new { client.Uuid, client.Name, IsActive = false, RevokedKeys = revoked.Select(k => k.KeyPrefix).ToList() },
                userId);
            await _db.SaveChangesAsync(ct);
        }

        return ToModel(client, now);
    }

    /// <summary>Another organization's client is a 404 here, super admin or not — see <see cref="OwnClients"/>.</summary>
    private async Task<ApiClient> LoadAsync(Guid clientId, CancellationToken ct) =>
        await OwnClients.Include(c => c.Keys).FirstOrDefaultAsync(c => c.Uuid == clientId, ct)
        ?? throw new NotFoundException("API client", clientId);

    /// <summary>
    /// API clients exist independently of a connection (a key can be issued before QuickBooks is
    /// connected), but the audit table is keyed by connection. 0 marks "no connection yet".
    /// </summary>
    private async Task<int> AuditConnectionIdAsync(CancellationToken ct) =>
        (await _accessor.GetCurrentAsync(ct))?.Id ?? 0;

    internal static List<string> NormalizeScopes(IEnumerable<string>? requested)
    {
        var scopes = new List<string>();

        foreach (var raw in requested ?? [])
        {
            var match = ApiScopes.All.FirstOrDefault(s => string.Equals(s, raw?.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new BadRequestException(
                       $"'{raw}' is not an API scope. Use any of: {string.Join(", ", ApiScopes.All)}.");
            if (!scopes.Contains(match)) scopes.Add(match);
        }

        if (scopes.Count == 0)
            throw new BadRequestException($"Choose at least one scope: {string.Join(", ", ApiScopes.All)}.");

        // Canonical order, so the stored value does not depend on the order they were ticked in.
        return ApiScopes.All.Where(scopes.Contains).ToList();
    }

    internal static bool IsKeyActive(ApiClientKey key, DateTime now) =>
        key.RevokedAt is null && (key.ExpiresAt is null || key.ExpiresAt > now);

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local       => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _                        => value
    };

    internal static ApiClientModel ToModel(ApiClient client, DateTime now) => new()
    {
        Id        = client.Uuid,
        Name      = client.Name,
        Scopes    = client.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        IsActive  = client.IsActive,
        CreatedAt = client.CreatedAt,
        Keys      = client.Keys
            .OrderByDescending(k => k.CreatedAt)
            .Select(k => new ApiClientKeyModel
            {
                Id         = k.Uuid,
                KeyPrefix  = k.KeyPrefix,
                CreatedAt  = k.CreatedAt,
                ExpiresAt  = k.ExpiresAt,
                RevokedAt  = k.RevokedAt,
                LastUsedAt = k.LastUsedAt,
                IsActive   = IsKeyActive(k, now)
            }).ToList()
    };
}
