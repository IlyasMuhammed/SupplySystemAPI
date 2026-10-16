using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SMS.Modules.Integration.Configuration;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Integration.Core.Connections;

/// <summary>
/// Which Intuit app (Client ID + secret) a QuickBooks call is made as — docs/quickbooks/APP-CREDENTIALS.md.
/// <para>
/// The only code that decrypts an app secret. Every lookup names its scope explicitly: the app table is not
/// tenant-filtered, because the platform row belongs to no organization.
/// </para>
/// </summary>
internal interface IQuickBooksAppResolver
{
    /// <summary>
    /// D-2 — the app a <b>new</b> connection of this organization would use: its own app when FEATURE_QBO_OWN_APP is on
    /// and its keys are saved, else the platform's (database, then configuration). Null when there is none.
    /// </summary>
    Task<OAuthApp?> ForNewConnectionAsync(Guid organizationId, CancellationToken ct = default);

    /// <summary>
    /// D-3 — the app with this Client ID, for tokens it issued: the organization's own row (even if the feature was
    /// switched off since), then the platform row, then configuration. A null Client ID (a connection from before the
    /// keys moved to the database) means the platform's app. Null when the keys are no longer stored anywhere.
    /// </summary>
    Task<OAuthApp?> ByClientIdAsync(Guid organizationId, string? clientId, CancellationToken ct = default);

    /// <summary>The platform's app (database, then configuration), or null.</summary>
    Task<OAuthApp?> PlatformAppAsync(CancellationToken ct = default);

    /// <summary>D-5 — the callback URL every Intuit app (platform's or an organization's) must register.</summary>
    Task<string?> RedirectUriAsync(CancellationToken ct = default);

    /// <summary>D-5 — the SCM screen the callback returns the browser to.</summary>
    Task<string?> FrontendReturnUrlAsync(CancellationToken ct = default);

    /// <summary>Whether the organization may use its own app (FEATURE_QBO_OWN_APP licensed and on).</summary>
    Task<bool> OwnAppEnabledAsync(Guid organizationId, CancellationToken ct = default);
}

internal sealed class QuickBooksAppResolver : IQuickBooksAppResolver
{
    private readonly IntegrationDbContext            _db;
    private readonly IEncryptionService              _encryption;
    private readonly ITenantSnapshotProvider         _snapshots;
    private readonly QuickBooksOptions               _options;
    private readonly ILogger<QuickBooksAppResolver>  _logger;

    public QuickBooksAppResolver(
        IntegrationDbContext db, IEncryptionService encryption, ITenantSnapshotProvider snapshots,
        IOptions<QuickBooksOptions> options, ILogger<QuickBooksAppResolver> logger)
    {
        _db         = db;
        _encryption = encryption;
        _snapshots  = snapshots;
        _options    = options.Value;
        _logger     = logger;
    }

    public async Task<OAuthApp?> ForNewConnectionAsync(Guid organizationId, CancellationToken ct = default)
    {
        if (await OwnAppEnabledAsync(organizationId, ct))
        {
            var own = await TenantRowAsync(organizationId, ct);
            if (own is not null)
            {
                var app = await ToAppAsync(own, AppCredentialSource.Tenant, ct);
                if (app is not null) return app;
            }
        }

        return await PlatformAppAsync(ct);
    }

    public async Task<OAuthApp?> ByClientIdAsync(Guid organizationId, string? clientId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return await PlatformAppAsync(ct);

        var own = await TenantRowAsync(organizationId, ct);
        if (own is not null && own.ClientId == clientId)
            return await ToAppAsync(own, AppCredentialSource.Tenant, ct);

        var platform = await PlatformRowAsync(ct);
        if (platform is not null && platform.ClientId == clientId)
            return await ToAppAsync(platform, AppCredentialSource.Platform, ct);

        var configured = ConfiguredApp();
        return configured is not null && configured.ClientId == clientId ? configured : null;
    }

    public async Task<OAuthApp?> PlatformAppAsync(CancellationToken ct = default)
    {
        var platform = await PlatformRowAsync(ct);
        if (platform is not null)
        {
            var app = await ToAppAsync(platform, AppCredentialSource.Platform, ct);
            if (app is not null) return app;
        }
        return ConfiguredApp();
    }

    public async Task<string?> RedirectUriAsync(CancellationToken ct = default)
    {
        var platform = await PlatformRowAsync(ct);
        return Clean(platform?.RedirectUri) ?? Clean(_options.RedirectUri);
    }

    public async Task<string?> FrontendReturnUrlAsync(CancellationToken ct = default)
    {
        var platform = await PlatformRowAsync(ct);
        return Clean(platform?.FrontendReturnUrl) ?? Clean(_options.FrontendReturnUrl);
    }

    public async Task<bool> OwnAppEnabledAsync(Guid organizationId, CancellationToken ct = default)
    {
        var snapshot = await _snapshots.GetSnapshotAsync(organizationId);
        return snapshot is { IsActive: true } && snapshot.EnabledFeatureCodes.Contains(ModuleCodes.QuickBooksOwnApp);
    }

    private Task<QuickBooksAppCredential?> TenantRowAsync(Guid organizationId, CancellationToken ct) =>
        _db.AppCredentials.AsNoTracking().FirstOrDefaultAsync(a => a.OrganizationId == organizationId, ct);

    private Task<QuickBooksAppCredential?> PlatformRowAsync(CancellationToken ct) =>
        _db.AppCredentials.AsNoTracking().FirstOrDefaultAsync(a => a.OrganizationId == null, ct);

    /// <summary>A row as a callable app. Null when its secret can no longer be decrypted (the key changed) or no callback URL is known.</summary>
    private async Task<OAuthApp?> ToAppAsync(QuickBooksAppCredential row, AppCredentialSource source, CancellationToken ct)
    {
        string secret;
        try
        {
            secret = _encryption.Decrypt(row.EncryptedClientSecret);
        }
        catch (Exception ex)
        {
            // Never the ciphertext, never the value — which row and why.
            _logger.LogError("The stored QuickBooks app secret ({Source}, organization {OrganizationId}) could not be decrypted ({ErrorType}).",
                source, row.OrganizationId, ex.GetType().Name);
            return null;
        }

        var redirect = await RedirectUriAsync(ct);
        var app = new OAuthApp(row.ClientId, secret, redirect ?? string.Empty, row.Environment, source);
        return app.IsComplete ? app : null;
    }

    private OAuthApp? ConfiguredApp()
    {
        if (!_options.IsConfigured) return null;
        return new OAuthApp(
            _options.ClientId.Trim(), _options.ClientSecret, _options.RedirectUri.Trim(),
            _options.IsProduction ? IntegrationEnvironment.Production : IntegrationEnvironment.Sandbox,
            AppCredentialSource.Configuration);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
