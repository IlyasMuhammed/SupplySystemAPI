using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SMS.Modules.Integration.Configuration;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Core.Settings;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Integration.Core.Connections;

/// <summary>
/// The two screens that hold Intuit app keys (docs/quickbooks/APP-CREDENTIALS.md): the platform's (super admin) and an
/// organization's own (FEATURE_QBO_OWN_APP). Secrets go in encrypted and never come back out (D-4).
/// </summary>
public interface IQuickBooksAppSettingsService
{
    Task<PlatformAppKeysModel> GetPlatformAsync(CancellationToken ct = default);
    Task<PlatformAppKeysModel> SavePlatformAsync(SavePlatformAppKeysRequest request, int userId, CancellationToken ct = default);
    /// <summary>Back to the configuration fallback (or nothing).</summary>
    Task<PlatformAppKeysModel> DeletePlatformAsync(bool confirmBreakingChange, int userId, CancellationToken ct = default);

    /// <summary>The caller's organization.</summary>
    Task<TenantAppKeysModel> GetTenantAsync(CancellationToken ct = default);
    Task<TenantAppKeysModel> SaveTenantAsync(SaveTenantAppKeysRequest request, int userId, CancellationToken ct = default);
    Task<TenantAppKeysModel> DeleteTenantAsync(int userId, CancellationToken ct = default);
}

internal sealed class QuickBooksAppSettingsService : IQuickBooksAppSettingsService
{
    private const int MaxClientId = 200;
    private const int MaxSecret   = 500;
    private const int MaxUrl      = 500;

    private readonly IntegrationDbContext                  _db;
    private readonly IQuickBooksAppResolver                _apps;
    private readonly IEncryptionService                    _encryption;
    private readonly ITenantContext                        _tenant;
    private readonly QuickBooksOptions                     _options;
    private readonly ILogger<QuickBooksAppSettingsService> _logger;

    public QuickBooksAppSettingsService(
        IntegrationDbContext db, IQuickBooksAppResolver apps, IEncryptionService encryption, ITenantContext tenant,
        IOptions<QuickBooksOptions> options, ILogger<QuickBooksAppSettingsService> logger)
    {
        _db         = db;
        _apps       = apps;
        _encryption = encryption;
        _tenant     = tenant;
        _options    = options.Value;
        _logger     = logger;
    }

    // ── Platform ────────────────────────────────────────────────────────────────────────────

    public async Task<PlatformAppKeysModel> GetPlatformAsync(CancellationToken ct = default)
    {
        var row     = await PlatformRowAsync(tracked: false, ct);
        var current = await _apps.PlatformAppAsync(ct);

        return new PlatformAppKeysModel
        {
            IsConfigured      = current is not null,
            Source            = current?.Source.ToString(),
            SavedInDatabase   = row is not null,
            ClientId          = row?.ClientId ?? current?.ClientId,
            HasSecret         = row is not null ? !string.IsNullOrEmpty(row.EncryptedClientSecret) : current is not null,
            Environment       = (row?.Environment ?? current?.Environment ?? IntegrationEnvironment.Sandbox).ToString(),
            RedirectUri       = await _apps.RedirectUriAsync(ct),
            FrontendReturnUrl = await _apps.FrontendReturnUrlAsync(ct),
            ConfigurationAvailable = _options.IsConfigured,
            ConfigurationClientId  = _options.IsConfigured ? _options.ClientId.Trim() : null,
            ConnectionsUsingPlatformKeys = current is null ? 0 : await CountLiveConnectionsUsingAsync(current.ClientId, includeUnpinned: true, ct),
            OrganizationsWithOwnKeys     = await _db.AppCredentials.CountAsync(a => a.OrganizationId != null, ct),
            ModifiedAt        = row?.ModifiedAt ?? row?.CreatedAt,
            ModifiedBy        = row?.ModifiedBy ?? row?.CreatedBy
        };
    }

    public async Task<PlatformAppKeysModel> SavePlatformAsync(SavePlatformAppKeysRequest request, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var clientId    = RequiredText(request.ClientId, MaxClientId, "Client ID");
        var secret      = OptionalText(request.ClientSecret, MaxSecret, "Client secret");
        var environment = ParseEnvironment(request.Environment);
        var redirect    = RequiredUrl(request.RedirectUri, "Callback URL");
        var returnUrl   = OptionalUrl(request.FrontendReturnUrl, "Return page URL");

        var row = await PlatformRowAsync(tracked: true, ct);
        if (row is null && secret is null)
            throw new BadRequestException("Client secret is required.");

        var before = await _apps.PlatformAppAsync(ct);
        await GuardPlatformChangeAsync(before, clientId, environment, request.ConfirmBreakingChange, ct);

        var now = DateTime.UtcNow;
        if (row is null)
        {
            row = new QuickBooksAppCredential { OrganizationId = null, CreatedAt = now, CreatedBy = userId };
            _db.AppCredentials.Add(row);
        }
        else
        {
            row.ModifiedAt = now;
            row.ModifiedBy = userId;
        }

        row.ClientId          = clientId;
        row.Environment       = environment;
        row.RedirectUri       = redirect;
        row.FrontendReturnUrl = returnUrl;
        if (secret is not null) row.EncryptedClientSecret = _encryption.Encrypt(secret);

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("QuickBooks platform app keys saved by user {UserId}: client {ClientId}, {Environment}.",
            userId, clientId, environment);

        return await GetPlatformAsync(ct);
    }

    public async Task<PlatformAppKeysModel> DeletePlatformAsync(bool confirmBreakingChange, int userId, CancellationToken ct = default)
    {
        var row = await PlatformRowAsync(tracked: true, ct);
        if (row is null) return await GetPlatformAsync(ct);

        var before = await _apps.PlatformAppAsync(ct);
        // Afterwards only the configuration is left.
        var after = _options.IsConfigured ? _options.ClientId.Trim() : null;
        var afterEnvironment = _options.IsProduction ? IntegrationEnvironment.Production : IntegrationEnvironment.Sandbox;
        await GuardPlatformChangeAsync(before, after, afterEnvironment, confirmBreakingChange, ct);

        _db.AppCredentials.Remove(row);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("QuickBooks platform app keys removed by user {UserId}.", userId);

        return await GetPlatformAsync(ct);
    }

    /// <summary>
    /// D-8 — a new Client ID (or environment) leaves every live connection made with the old keys unable to refresh.
    /// Refused unless confirmed; when it goes ahead, connections from before D-3 are pinned to the old keys first, so they
    /// fail with "keys no longer stored" instead of trying the new app.
    /// </summary>
    private async Task GuardPlatformChangeAsync(
        OAuthApp? before, string? newClientId, IntegrationEnvironment newEnvironment, bool confirmed, CancellationToken ct)
    {
        if (before is null) return;
        if (before.ClientId == newClientId && before.Environment == newEnvironment) return;

        var affected = await CountLiveConnectionsUsingAsync(before.ClientId, includeUnpinned: true, ct);
        if (affected == 0) return;

        if (!confirmed)
            throw new ConflictException(
                $"{affected} organization(s) are connected to QuickBooks with the current platform keys (Client ID {before.ClientId}). "
              + "After this change they cannot refresh and must reconnect. Confirm the change to go ahead.");

        var unpinned = await _db.Connections.IgnoreQueryFilters()
            .Where(c => c.AppClientId == null && c.RealmId != null
                     && (c.Status == ConnectionStatus.Connected || c.Status == ConnectionStatus.NeedsSetup || c.Status == ConnectionStatus.Live))
            .ToListAsync(ct);
        foreach (var c in unpinned)
        {
            c.AppClientId = before.ClientId;
            c.AppSource   = before.Source;
        }
    }

    private async Task<int> CountLiveConnectionsUsingAsync(string clientId, bool includeUnpinned, CancellationToken ct) =>
        await _db.Connections.IgnoreQueryFilters()
            .Where(c => c.RealmId != null
                     && (c.Status == ConnectionStatus.Connected || c.Status == ConnectionStatus.NeedsSetup || c.Status == ConnectionStatus.Live)
                     && (c.AppClientId == clientId || (includeUnpinned && c.AppClientId == null)))
            .CountAsync(ct);

    private Task<QuickBooksAppCredential?> PlatformRowAsync(bool tracked, CancellationToken ct)
    {
        var query = _db.AppCredentials.Where(a => a.OrganizationId == null);
        return (tracked ? query : query.AsNoTracking()).FirstOrDefaultAsync(ct);
    }

    // ── Organization ────────────────────────────────────────────────────────────────────────

    public async Task<TenantAppKeysModel> GetTenantAsync(CancellationToken ct = default)
    {
        var org        = _tenant.OrganizationId;
        var row        = await TenantRowAsync(org, tracked: false, ct);
        var connection = await CurrentConnectionAsync(org, ct);
        var next       = await _apps.ForNewConnectionAsync(org, ct);

        return new TenantAppKeysModel
        {
            FeatureEnabled        = await _apps.OwnAppEnabledAsync(org, ct),
            HasOwnKeys            = row is not null,
            ClientId              = row?.ClientId,
            HasSecret             = row is not null && !string.IsNullOrEmpty(row.EncryptedClientSecret),
            Environment           = (row?.Environment ?? IntegrationEnvironment.Sandbox).ToString(),
            RedirectUriToRegister = await _apps.RedirectUriAsync(ct),
            NewConnectionsUse     = next?.Source.ToString(),
            ConnectedWithOwnKeys  = connection is not null && connection.IsUsable() && connection.AppSource == AppCredentialSource.Tenant,
            ModifiedAt            = row?.ModifiedAt ?? row?.CreatedAt
        };
    }

    public async Task<TenantAppKeysModel> SaveTenantAsync(SaveTenantAppKeysRequest request, int userId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var org = _tenant.OrganizationId;

        // The endpoint carries [RequiresFeature]; checked here too, so no other caller can bypass D-7.
        if (!await _apps.OwnAppEnabledAsync(org, ct))
            throw new ForbiddenException("Using your own Intuit app is not enabled for this organization. Ask the platform administrator.");

        var clientId    = RequiredText(request.ClientId, MaxClientId, "Client ID");
        var secret      = OptionalText(request.ClientSecret, MaxSecret, "Client secret");
        var environment = ParseEnvironment(request.Environment);

        var platform = await _apps.PlatformAppAsync(ct);
        if (platform is not null && platform.ClientId == clientId)
            throw new BadRequestException("These are the platform's QuickBooks keys. Remove your own keys to use the platform's app.");

        var row = await TenantRowAsync(org, tracked: true, ct);
        if (row is null && secret is null)
            throw new BadRequestException("Client secret is required.");

        var connection = await CurrentConnectionAsync(org, ct);
        if (row is not null && (row.ClientId != clientId || row.Environment != environment))
            RefuseWhileConnectedWithOwnKeys(connection, "change the Client ID or environment");

        var before = row is null ? null : new { row.ClientId, Environment = row.Environment.ToString() };
        var now = DateTime.UtcNow;
        if (row is null)
        {
            row = new QuickBooksAppCredential { OrganizationId = org, CreatedAt = now, CreatedBy = userId };
            _db.AppCredentials.Add(row);
        }
        else
        {
            row.ModifiedAt = now;
            row.ModifiedBy = userId;
        }

        row.ClientId    = clientId;
        row.Environment = environment;
        if (secret is not null) row.EncryptedClientSecret = _encryption.Encrypt(secret);

        if (connection is not null)
            IntegrationAudit.Add(_db, connection.Id, AuditAreas.AppKeys, "OwnAppKeysSaved", before,
                new { ClientId = clientId, Environment = environment.ToString(), SecretChanged = secret is not null }, userId);

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("QuickBooks app keys of organization {OrganizationId} saved by user {UserId}: client {ClientId}, {Environment}.",
            org, userId, clientId, environment);

        return await GetTenantAsync(ct);
    }

    public async Task<TenantAppKeysModel> DeleteTenantAsync(int userId, CancellationToken ct = default)
    {
        var org = _tenant.OrganizationId;
        var row = await TenantRowAsync(org, tracked: true, ct);
        if (row is null) return await GetTenantAsync(ct);

        var connection = await CurrentConnectionAsync(org, ct);
        RefuseWhileConnectedWithOwnKeys(connection, "remove them");

        if (connection is not null)
            IntegrationAudit.Add(_db, connection.Id, AuditAreas.AppKeys, "OwnAppKeysRemoved",
                new { row.ClientId, Environment = row.Environment.ToString() }, null, userId);

        _db.AppCredentials.Remove(row);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("QuickBooks app keys of organization {OrganizationId} removed by user {UserId}.", org, userId);

        return await GetTenantAsync(ct);
    }

    /// <summary>D-8 — the live connection's tokens belong to the organization's app; changing that app strands them.</summary>
    private static void RefuseWhileConnectedWithOwnKeys(IntegrationConnection? connection, string action)
    {
        if (connection is not null && connection.IsUsable() && connection.AppSource == AppCredentialSource.Tenant)
            throw new ConflictException(
                $"QuickBooks is connected through your own Intuit app. Disconnect QuickBooks first, then {action}. "
              + "(Replacing only the client secret is allowed while connected.)");
    }

    private Task<QuickBooksAppCredential?> TenantRowAsync(Guid organizationId, bool tracked, CancellationToken ct)
    {
        var query = _db.AppCredentials.Where(a => a.OrganizationId == organizationId);
        return (tracked ? query : query.AsNoTracking()).FirstOrDefaultAsync(ct);
    }

    private Task<IntegrationConnection?> CurrentConnectionAsync(Guid organizationId, CancellationToken ct) =>
        _db.Connections.AsNoTracking()
            .FirstOrDefaultAsync(c => c.OrganizationId == organizationId && c.ProviderKey == ProviderKeys.QuickBooksOnline, ct);

    // ── Validation ──────────────────────────────────────────────────────────────────────────

    private static string RequiredText(string? value, int max, string label)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) throw new BadRequestException($"{label} is required.");
        if (text.Length > max) throw new BadRequestException($"{label} must be at most {max} characters.");
        if (text.Any(char.IsWhiteSpace)) throw new BadRequestException($"{label} cannot contain spaces.");
        return text;
    }

    private static string? OptionalText(string? value, int max, string label) =>
        string.IsNullOrWhiteSpace(value) ? null : RequiredText(value, max, label);

    private static IntegrationEnvironment ParseEnvironment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return IntegrationEnvironment.Sandbox;
        return Enum.TryParse<IntegrationEnvironment>(value.Trim(), ignoreCase: true, out var env) && Enum.IsDefined(env)
            ? env
            : throw new BadRequestException("Environment must be Sandbox or Production.");
    }

    private static string RequiredUrl(string? value, string label) =>
        OptionalUrl(value, label) ?? throw new BadRequestException($"{label} is required.");

    private static string? OptionalUrl(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (text.Length > MaxUrl) throw new BadRequestException($"{label} must be at most {MaxUrl} characters.");
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new BadRequestException($"{label} must be a full http(s) address.");
        return text;
    }
}
