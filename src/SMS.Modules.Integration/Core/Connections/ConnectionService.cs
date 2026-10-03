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

/// <summary>The connection card: status, connect, test, disconnect (plan QBI-08, QBI-10).</summary>
public interface IQuickBooksConnectionService
{
    Task<ConnectionStatusModel> GetStatusAsync(CancellationToken ct = default);

    /// <summary>Starts a connection: mints a state token and returns Intuit's consent URL.</summary>
    Task<ConnectResponse> ConnectAsync(int userId, CancellationToken ct = default);

    /// <summary>A cheap authenticated read (CompanyInfo), refreshing the token first if it needs it.</summary>
    Task<TestConnectionResult> TestAsync(CancellationToken ct = default);

    /// <summary>
    /// Removes our side only: revokes the grant at Intuit (best effort) and forgets the tokens. The realm
    /// and every mapping stay, so reconnecting the same company re-links instead of re-creating.
    /// </summary>
    Task<ConnectionStatusModel> DisconnectAsync(int userId, CancellationToken ct = default);
}

internal sealed class ConnectionService : IQuickBooksConnectionService
{
    private readonly IntegrationDbContext                 _db;
    private readonly IConnectionAccessor                  _accessor;
    private readonly ITenantContext                       _tenant;
    private readonly IOAuthStateService                   _states;
    private readonly ICredentialVault                     _vault;
    private readonly IEnumerable<IAccountingAuthProvider> _authProviders;
    private readonly IAccountingProviderRegistry          _providers;
    private readonly IConnectionHealth                    _health;
    private readonly QuickBooksOptions                    _options;
    private readonly IntegrationJobOptions                _jobOptions;
    private readonly IServiceProvider                     _services;
    private readonly ILogger<ConnectionService>           _logger;

    public ConnectionService(
        IntegrationDbContext db, IConnectionAccessor accessor, ITenantContext tenant, IOAuthStateService states,
        ICredentialVault vault, IEnumerable<IAccountingAuthProvider> authProviders, IAccountingProviderRegistry providers,
        IConnectionHealth health, IOptions<QuickBooksOptions> options, IOptions<IntegrationJobOptions> jobOptions,
        IServiceProvider services, ILogger<ConnectionService> logger)
    {
        _db            = db;
        _accessor      = accessor;
        _tenant        = tenant;
        _states        = states;
        _vault         = vault;
        _authProviders = authProviders;
        _providers     = providers;
        _health        = health;
        _options       = options.Value;
        _jobOptions    = jobOptions.Value;
        _services      = services;
        _logger        = logger;
    }

    public async Task<ConnectionStatusModel> GetStatusAsync(CancellationToken ct = default)
    {
        var connection = await _accessor.GetCurrentAsync(ct);

        // A consent that was abandoned leaves the connection in Connecting. Once no live state token is
        // left for it, nobody can finish it, so say what it really is.
        if (connection is { Status: ConnectionStatus.Connecting })
        {
            var now = DateTime.UtcNow;
            var pending = await _db.OAuthStateTokens.AnyAsync(t => t.UsedAt == null && t.ExpiresAt > now, ct);
            if (!pending)
                await ConnectionPersistence.SaveWithRetryAsync(_db, connection, c =>
                {
                    if (c.Status == ConnectionStatus.Connecting) c.Status = ConnectionStatus.NotConnected;
                }, ct);
        }

        return await ToModelAsync(connection, ct);
    }

    public async Task<ConnectResponse> ConnectAsync(int userId, CancellationToken ct = default)
    {
        if (!_options.IsConfigured)
            throw new ConflictException(
                "QuickBooks is not configured on this server. An administrator must set QuickBooks:ClientId, "
              + "QuickBooks:ClientSecret and QuickBooks:RedirectUri (user-secrets or the secret store) first.");

        var connection = await _accessor.GetCurrentAsync(ct);

        if (connection is { Status: ConnectionStatus.Connected or ConnectionStatus.NeedsSetup or ConnectionStatus.Live })
            throw new ConflictException(
                $"QuickBooks is already connected to {connection.CompanyName ?? "a company"}. Disconnect first to connect a different one.");

        if (connection is null)
        {
            connection = new IntegrationConnection
            {
                OrganizationId = _tenant.OrganizationId,
                ProviderKey    = ProviderKeys.QuickBooksOnline,
                Environment    = _options.IsProduction ? IntegrationEnvironment.Production : IntegrationEnvironment.Sandbox,
                Status         = ConnectionStatus.NotConnected
            };
            _db.Connections.Add(connection);
        }

        var state = _states.Issue(connection.OrganizationId, userId);

        string consentUrl;
        try
        {
            consentUrl = AuthProvider(connection.ProviderKey).BuildConsentUrl(state);
        }
        catch (AccountingAuthException ex)
        {
            // Nothing saved: the state row (and a new connection row) are dropped with the context.
            throw new ConflictException(ex.Message);
        }

        // A first connect needs the connection's id before anything can be audited against it.
        if (connection.Id == 0)
            await _db.SaveChangesAsync(ct);

        IntegrationAudit.Add(_db, connection.Id, AuditAreas.Connection, "ConnectStarted", null,
            new { Environment = _options.IsProduction ? "Production" : "Sandbox" }, userId);

        // A Revoked or Expired connection keeps saying so until the new consent actually completes;
        // only a never-connected one shows "Connecting" meanwhile.
        await ConnectionPersistence.SaveWithRetryAsync(_db, connection, c =>
        {
            if (c.Status is ConnectionStatus.NotConnected) c.Status = ConnectionStatus.Connecting;
        }, ct);

        return new ConnectResponse { ConsentUrl = consentUrl };
    }

    public async Task<TestConnectionResult> TestAsync(CancellationToken ct = default)
    {
        var connection = await _accessor.GetCurrentAsync(ct);
        if (connection is null || !connection.IsUsable())
            return new TestConnectionResult
            {
                Ok      = false,
                Message = ConnectionPersistence.DescribeUnusable(connection?.Status ?? ConnectionStatus.NotConnected)
            };

        ProviderResult<RemoteCompanyInfo> result;
        try
        {
            result = await _providers.Get(connection.ProviderKey).GetCompanyInfoAsync(connection.ToProviderContext(), ct);
        }
        catch (ConnectionUnavailableException ex)
        {
            return new TestConnectionResult { Ok = false, CompanyName = connection.CompanyName, Message = ex.Message };
        }
        catch (TokenRefreshFailedException ex)
        {
            return new TestConnectionResult { Ok = false, CompanyName = connection.CompanyName, Message = ex.Message };
        }

        if (result.IsSuccess && result.Value is not null)
        {
            var name = result.Value.CompanyName;
            await ConnectionPersistence.SaveWithRetryAsync(_db, connection, c =>
            {
                if (!string.IsNullOrWhiteSpace(name)) c.CompanyName = name.Length <= 200 ? name : name[..200];
                c.LastError = null;
            }, ct);

            return new TestConnectionResult { Ok = true, CompanyName = connection.CompanyName, Message = "QuickBooks answered." };
        }

        var message = result.Message ?? $"QuickBooks did not answer ({result.Outcome}).";
        if (result.Outcome == ProviderOutcomeKind.AuthRevoked)
            await _health.MarkUnavailableAsync(connection.Id, ConnectionStatus.Revoked, message, ct);

        return new TestConnectionResult { Ok = false, CompanyName = connection.CompanyName, Message = message };
    }

    public async Task<ConnectionStatusModel> DisconnectAsync(int userId, CancellationToken ct = default)
    {
        var connection = await _accessor.GetCurrentAsync(ct);
        if (connection is null || connection.Status == ConnectionStatus.NotConnected)
            return await ToModelAsync(connection, ct);

        var before = new { Status = connection.Status.ToString(), connection.RealmId, connection.CompanyName };

        // Best effort at Intuit: the admin asked for the connection to be gone, and it goes on our side
        // whatever Intuit says. A refresh token we cannot even decrypt is simply not sent.
        if (!string.IsNullOrEmpty(connection.EncryptedRefreshToken))
        {
            try
            {
                var refreshToken = _vault.ReadRefreshToken(connection);
                await AuthProvider(connection.ProviderKey).RevokeAsync(refreshToken, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("Revoking the QuickBooks grant for connection {ConnectionId} failed ({ErrorType}); disconnecting locally anyway.",
                    connection.Id, ex.GetType().Name);
            }
        }

        IntegrationAudit.Add(_db, connection.Id, AuditAreas.Connection, "Disconnected", before,
            new { Status = ConnectionStatus.NotConnected.ToString(), connection.RealmId }, userId);

        await ConnectionPersistence.SaveWithRetryAsync(_db, connection, c =>
        {
            _vault.Clear(c);
            c.Status            = ConnectionStatus.NotConnected;
            c.LastError         = null;
            c.ReconnectWarnedAt = null;
            // RealmId, company facts and every EntityMap are kept on purpose (plan B.2 step 10).
        }, ct);

        await ConnectionPersistence.TrySuspendOutboxAsync(_services, connection.Id, "QuickBooks was disconnected.", _logger, ct);

        return await ToModelAsync(connection, ct);
    }

    private async Task<ConnectionStatusModel> ToModelAsync(IntegrationConnection? connection, CancellationToken ct)
    {
        if (connection is null)
            return new ConnectionStatusModel
            {
                AppConfigured = _options.IsConfigured,
                Status        = ConnectionStatus.NotConnected.ToString(),
                Environment   = (_options.IsProduction ? IntegrationEnvironment.Production : IntegrationEnvironment.Sandbox).ToString()
            };

        var mode = await _db.Settings.AsNoTracking()
            .Where(s => s.ConnectionId == connection.Id)
            .Select(s => (SyncMode?)s.Mode)
            .FirstOrDefaultAsync(ct) ?? SyncMode.DryRun;

        var usable = connection.IsUsable();

        return new ConnectionStatusModel
        {
            AppConfigured         = _options.IsConfigured,
            Status                = connection.Status.ToString(),
            IsConnected           = usable,
            CompanyName           = connection.CompanyName,
            RealmId               = connection.RealmId,
            Environment           = connection.Environment.ToString(),
            HomeCurrencyCode      = connection.HomeCurrencyCode,
            MultiCurrencyEnabled  = connection.MultiCurrencyEnabled,
            Country               = connection.Country,
            ConnectedAt           = connection.ConnectedAt,
            ConnectedByUserId     = connection.ConnectedByUserId,
            AccessTokenExpiresAt  = connection.AccessTokenExpiresAt,
            RefreshTokenExpiresAt = connection.RefreshTokenExpiresAt,
            ReconnectSoon         = usable
                                 && connection.RefreshTokenExpiresAt is { } expiry
                                 && expiry < DateTime.UtcNow.AddDays(_jobOptions.ReconnectWarningDays),
            Mode                  = mode.ToString(),
            LastError             = connection.LastError
        };
    }

    private IAccountingAuthProvider AuthProvider(string providerKey) =>
        _authProviders.FirstOrDefault(p => string.Equals(p.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase))
        ?? throw new ConflictException($"No OAuth provider is registered for '{providerKey}'.");
}
