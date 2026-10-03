using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SMS.Modules.Integration.Configuration;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;

namespace SMS.Modules.Integration.Core.Connections;

/// <summary>
/// Hands out a usable access token, refreshing first when it is close to expiry (plan QBI-09).
/// <para>
/// <b>The refresh token rotates.</b> Intuit may return a new refresh token on every refresh, and the
/// one we used stops working. So the new refresh token and both expiries are written in one
/// <c>SaveChanges</c> guarded by the connection's row version: write the new token and fail to commit,
/// and the connection is gone with no way back but a reconnect.
/// </para>
/// <para>
/// <b>Two refreshes can race</b> — the daily job and a sync call, or two sync workers. Both read the
/// same row; the database lets only the first save through. The loser does not retry: it reloads and
/// uses the winner's token, because the winner's refresh token is the one Intuit now honours.
/// </para>
/// </summary>
internal sealed class TokenManager : ITokenManager
{
    private readonly IntegrationDbContext                  _db;
    private readonly ICredentialVault                      _vault;
    private readonly IEnumerable<IAccountingAuthProvider>  _authProviders;
    private readonly IntegrationJobOptions                 _jobOptions;
    private readonly IServiceProvider                      _services;
    private readonly ILogger<TokenManager>                 _logger;

    public TokenManager(
        IntegrationDbContext db, ICredentialVault vault, IEnumerable<IAccountingAuthProvider> authProviders,
        IOptions<IntegrationJobOptions> jobOptions, IServiceProvider services, ILogger<TokenManager> logger)
    {
        _db            = db;
        _vault         = vault;
        _authProviders = authProviders;
        _jobOptions    = jobOptions.Value;
        _services      = services;
        _logger        = logger;
    }

    public async Task<string> GetValidAccessTokenAsync(int connectionId, CancellationToken ct = default)
    {
        var connection = await LoadUsableAsync(connectionId, ct);
        var now        = DateTime.UtcNow;

        if (!NeedsRefresh(connection, now))
            return _vault.ReadAccessToken(connection);

        return await RefreshAsync(connection, now, ct);
    }

    /// <summary>
    /// Refreshes whatever the access token's expiry — what the daily job calls, so a company that
    /// pushes nothing for a season still keeps a live grant (Intuit's refresh-token lifetime is rolling).
    /// </summary>
    /// <returns>The access token now in force.</returns>
    public async Task<string> ForceRefreshAsync(int connectionId, CancellationToken ct = default)
    {
        var connection = await LoadUsableAsync(connectionId, ct);
        return await RefreshAsync(connection, DateTime.UtcNow, ct);
    }

    private bool NeedsRefresh(IntegrationConnection connection, DateTime now) =>
        string.IsNullOrEmpty(connection.EncryptedAccessToken)
     || connection.AccessTokenExpiresAt is null
     || connection.AccessTokenExpiresAt <= now.AddMinutes(Math.Max(0, _jobOptions.AccessTokenRefreshSkewMinutes));

    private async Task<IntegrationConnection> LoadUsableAsync(int connectionId, CancellationToken ct)
    {
        // Tenant-filtered on purpose: a job sets HangfireTenantScope before calling, a request has its
        // JWT. A connection id from another organization simply is not found.
        var connection = await _db.Connections.FirstOrDefaultAsync(c => c.Id == connectionId, ct)
            ?? throw new ConnectionUnavailableException(
                   ConnectionStatus.NotConnected, "No QuickBooks connection was found for this organization.");

        EnsureUsable(connection);
        return connection;
    }

    private static void EnsureUsable(IntegrationConnection connection)
    {
        if (!connection.IsUsable())
            throw new ConnectionUnavailableException(connection.Status, ConnectionPersistence.DescribeUnusable(connection.Status));

        if (string.IsNullOrEmpty(connection.EncryptedRefreshToken))
            throw new ConnectionUnavailableException(ConnectionStatus.NotConnected,
                "No QuickBooks tokens are stored for this organization. Reconnect QuickBooks.");
    }

    private async Task<string> RefreshAsync(IntegrationConnection connection, DateTime now, CancellationToken ct)
    {
        var provider      = AuthProvider(connection.ProviderKey);
        var usedCiphertext = connection.EncryptedRefreshToken;
        var refreshToken  = _vault.ReadRefreshToken(connection);

        TokenGrant grant;
        try
        {
            grant = await provider.RefreshAsync(refreshToken, ct);
        }
        catch (AuthorizationRevokedException ex)
        {
            return await HandleRevokedAsync(connection, usedCiphertext, ex, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await HandleTransientFailureAsync(connection, now, ex, ct);
        }

        _vault.Store(connection, grant);
        connection.LastRefreshAt = DateTime.UtcNow;
        connection.LastError     = null;
        connection.ModifiedDate  = DateTime.UtcNow;

        try
        {
            await _db.SaveChangesAsync(ct);
            return grant.AccessToken;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Someone refreshed (or changed) the row after we read it and saved first. Theirs is the
            // live token; ours is discarded rather than written over it.
            await _db.Entry(connection).ReloadAsync(ct);
            _logger.LogInformation(
                "QuickBooks token refresh for connection {ConnectionId} lost a race; using the token the other refresh stored.",
                connection.Id);

            EnsureUsable(connection);
            return _vault.ReadAccessToken(connection);
        }
    }

    /// <summary>
    /// Intuit said the grant is gone (<c>invalid_grant</c>). Before believing it, re-read the row: if the
    /// refresh token changed since we read it, another worker refreshed first and ours was merely stale.
    /// Otherwise the connection is Expired (its refresh token's lifetime had passed) or Revoked (someone
    /// disconnected the app inside QuickBooks), everything queued is suspended, and a person must reconnect.
    /// </summary>
    private async Task<string> HandleRevokedAsync(
        IntegrationConnection connection, string? usedCiphertext, AuthorizationRevokedException ex, CancellationToken ct)
    {
        await _db.Entry(connection).ReloadAsync(ct);

        if (connection.IsUsable()
            && !string.IsNullOrEmpty(connection.EncryptedRefreshToken)
            && connection.EncryptedRefreshToken != usedCiphertext
            && !string.IsNullOrEmpty(connection.EncryptedAccessToken))
        {
            return _vault.ReadAccessToken(connection);
        }

        // Already marked by whoever got here first (or disconnected meanwhile): report what is there.
        if (!connection.IsUsable())
            throw new ConnectionUnavailableException(
                connection.Status, ConnectionPersistence.DescribeUnusable(connection.Status), ex);

        var now    = DateTime.UtcNow;
        var status = connection.RefreshTokenExpiresAt is { } expiry && expiry <= now
            ? ConnectionStatus.Expired
            : ConnectionStatus.Revoked;

        var message = status == ConnectionStatus.Expired
            ? $"The QuickBooks authorization expired on {connection.RefreshTokenExpiresAt:dd MMM yyyy}. Reconnect QuickBooks."
            : "QuickBooks refused the stored authorization — the app was probably disconnected inside QuickBooks. Reconnect QuickBooks.";

        await ConnectionPersistence.SaveWithRetryAsync(_db, connection, c =>
        {
            // A disconnect, a reconnect or another refresh that landed meanwhile wins over this verdict:
            // it is only about the refresh token we actually sent.
            if (!c.IsUsable() || c.EncryptedRefreshToken != usedCiphertext) return;

            c.Status    = status;
            c.LastError = message;
            // A dead grant is still a secret sitting in the database. The expiries stay, so the screen
            // can say when it ran out.
            c.EncryptedAccessToken  = null;
            c.EncryptedRefreshToken = null;
        }, ct);

        _logger.LogWarning(
            "QuickBooks connection {ConnectionId} (organization {OrganizationId}) is now {Status}: the refresh was refused ({Reason}).",
            connection.Id, connection.OrganizationId, status, ex.Message);

        await ConnectionPersistence.TrySuspendOutboxAsync(_services, connection.Id, message, _logger, ct);

        throw new ConnectionUnavailableException(status, message, ex);
    }

    /// <summary>
    /// The refresh failed for a reason that says nothing about the grant. The status stays as it is. If
    /// the current access token is still good (we were only inside the refresh window) it is used; if it
    /// has already run out there is nothing to hand out, and the caller retries later.
    /// </summary>
    private async Task<string> HandleTransientFailureAsync(
        IntegrationConnection connection, DateTime now, Exception ex, CancellationToken ct)
    {
        _logger.LogWarning(
            "QuickBooks token refresh for connection {ConnectionId} failed ({ErrorType}); the connection is unchanged.",
            connection.Id, ex.GetType().Name);

        var reason = ex is AccountingAuthException
            ? ex.Message
            : $"Could not reach QuickBooks to refresh the connection ({ex.GetType().Name}).";

        try
        {
            connection.LastError    = ConnectionPersistence.Truncate(reason);
            connection.ModifiedDate = now;
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Someone else wrote the row — very likely a successful refresh. Take whatever is there now.
            await _db.Entry(connection).ReloadAsync(ct);
        }

        if (!string.IsNullOrEmpty(connection.EncryptedAccessToken)
            && connection.IsUsable()
            && connection.AccessTokenExpiresAt is { } expiresAt && expiresAt > now)
        {
            return _vault.ReadAccessToken(connection);
        }

        throw new TokenRefreshFailedException(reason, ex);
    }

    private IAccountingAuthProvider AuthProvider(string providerKey) =>
        _authProviders.FirstOrDefault(p => string.Equals(p.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase))
        ?? throw new ConnectionUnavailableException(ConnectionStatus.NotConnected,
               $"No OAuth provider is registered for '{providerKey}'.");
}
