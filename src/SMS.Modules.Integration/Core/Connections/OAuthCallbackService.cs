using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SMS.Modules.Integration.Auth;
using SMS.Modules.Integration.Configuration;
using SMS.Modules.Integration.Core.Logging;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Core.Reference;
using SMS.Modules.Integration.Core.Settings;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Integration.Core.Connections;

/// <summary>Intuit's redirect back to us after consent (plan QBI-08). Anonymous.</summary>
public interface IQuickBooksCallbackService
{
    /// <summary>
    /// Completes (or refuses) a connection and returns where to send the browser: the SCM screen with
    /// <c>?result=connected</c>, or <c>?result=error&amp;reason=…</c>. Never throws.
    /// </summary>
    Task<string> HandleCallbackAsync(string? code, string? state, string? realmId, string? error, CancellationToken ct = default);
}

/// <summary>Values of the <c>reason</c> query parameter the SCM screen receives.</summary>
public static class CallbackReasons
{
    public const string StateInvalid   = "state_invalid";
    public const string StateExpired   = "state_expired";
    public const string StateUsed      = "state_used";
    public const string AccessDenied   = "access_denied";
    public const string RealmMismatch  = "realm_mismatch";
    /// <summary>The QuickBooks company is already connected to (or mapped by) a different SCM organization.</summary>
    public const string RealmInUse     = "realm_in_use";
    public const string ExchangeFailed = "exchange_failed";
    public const string NotConfigured  = "not_configured";
}

/// <summary>
/// The anonymous half of connecting.
/// <para>
/// <b>Tenant.</b> The request has no user, so <c>TenantContext.IsSuperAdmin</c> is true and every tenant
/// filter is off. The state token is looked up explicitly across organizations by its hash; from then on
/// <see cref="HangfireTenantScope"/> is set to the token's organization for the rest of the callback —
/// with no authenticated user, <c>TenantContext</c> then resolves that organization and reports
/// <c>IsSuperAdmin = false</c>, so every later query is scoped and every new row stamped correctly. It is
/// cleared in a <c>finally</c>, so nothing leaks into whatever runs next on the thread.
/// </para>
/// <para>
/// <b>One company per organization.</b> A connection that has already mapped records to one QuickBooks
/// company is never silently re-pointed at another — the mappings would all refer to the wrong books.
/// </para>
/// </summary>
internal sealed class OAuthCallbackService : IQuickBooksCallbackService
{
    private readonly IntegrationDbContext                 _db;
    private readonly IConnectionAccessor                  _accessor;
    private readonly IOAuthStateService                   _states;
    private readonly ICredentialVault                     _vault;
    private readonly IEnumerable<IAccountingAuthProvider> _authProviders;
    private readonly IReferenceDataStore                  _reference;
    private readonly ITenantSnapshotProvider              _snapshots;
    private readonly QuickBooksOptions                    _options;
    private readonly IServiceProvider                     _services;
    private readonly ILogger<OAuthCallbackService>        _logger;

    public OAuthCallbackService(
        IntegrationDbContext db, IConnectionAccessor accessor, IOAuthStateService states, ICredentialVault vault,
        IEnumerable<IAccountingAuthProvider> authProviders, IReferenceDataStore reference, ITenantSnapshotProvider snapshots,
        IOptions<QuickBooksOptions> options, IServiceProvider services, ILogger<OAuthCallbackService> logger)
    {
        _db            = db;
        _accessor      = accessor;
        _states        = states;
        _vault         = vault;
        _authProviders = authProviders;
        _reference     = reference;
        _snapshots     = snapshots;
        _options       = options.Value;
        _services      = services;
        _logger        = logger;
    }

    public async Task<string> HandleCallbackAsync(
        string? code, string? state, string? realmId, string? error, CancellationToken ct = default)
    {
        try
        {
            var redemption = await _states.RedeemAsync(state, ct: ct);

            if (!redemption.Succeeded)
            {
                // A declined consent is reported as such even when its state is stale: that is what
                // the person did, and it is what they need to see.
                if (!string.IsNullOrWhiteSpace(error))
                    return Error(IsAccessDenied(error) ? CallbackReasons.AccessDenied : CallbackReasons.ExchangeFailed);

                return Error(redemption.Status switch
                {
                    StateRedemptionStatus.Expired     => CallbackReasons.StateExpired,
                    StateRedemptionStatus.AlreadyUsed => CallbackReasons.StateUsed,
                    _                                 => CallbackReasons.StateInvalid
                });
            }

            HangfireTenantScope.OrganizationId = redemption.OrganizationId;
            try
            {
                return await CompleteAsync(redemption, code, realmId, error, ct);
            }
            finally
            {
                HangfireTenantScope.OrganizationId = null;
            }
        }
        catch (Exception ex)
        {
            // The browser must land back on the SCM screen whatever happened here.
            _logger.LogError("QuickBooks callback failed unexpectedly ({ErrorType}: {Message}).",
                ex.GetType().Name, Redactor.Redact(ex.Message));
            return Error(CallbackReasons.ExchangeFailed);
        }
    }

    private async Task<string> CompleteAsync(
        StateRedemption redemption, string? code, string? realmId, string? error, CancellationToken ct)
    {
        var connection = await _accessor.GetCurrentAsync(ct);

        if (!string.IsNullOrWhiteSpace(error))
        {
            await AbandonAsync(connection, redemption.UserId, $"Consent was not given ({Clip(error, 50)}).", ct);
            return Error(IsAccessDenied(error) ? CallbackReasons.AccessDenied : CallbackReasons.ExchangeFailed);
        }

        if (!_options.IsConfigured)
        {
            await AbandonAsync(connection, redemption.UserId, "QuickBooks app keys are not configured on the server.", ct);
            return Error(CallbackReasons.NotConfigured);
        }

        // The organization may have been switched off (or the module removed from it) in the minutes
        // since Connect was pressed. Starting again then fails at Connect with the precise reason.
        var snapshot = await _snapshots.GetSnapshotAsync(redemption.OrganizationId);
        if (snapshot is null || !snapshot.IsActive || !snapshot.EnabledFeatureCodes.Contains(IntegrationFeature.Code))
        {
            await AbandonAsync(connection, redemption.UserId, "The organization or its QuickBooks feature is not active.", ct);
            return Error(CallbackReasons.StateInvalid);
        }

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(realmId) || realmId.Trim().Length > 50)
        {
            await AbandonAsync(connection, redemption.UserId, "Intuit's redirect had no authorization code or company id.", ct);
            return Error(CallbackReasons.ExchangeFailed);
        }

        realmId = realmId.Trim();

        // One QuickBooks company ↔ one SCM organization, in both directions: two organizations pushing
        // into the same books would each create their own copy of every customer, item and invoice.
        // Checked across organizations on purpose (the tenant filter would hide exactly the rows that matter).
        var organizationId = redemption.OrganizationId;
        var realmTakenElsewhere = await _db.Connections
            .IgnoreQueryFilters()
            .Where(c => c.RealmId == realmId && c.ProviderKey == ProviderKeys.QuickBooksOnline
                     && c.OrganizationId != organizationId)
            .AnyAsync(c => c.Status != ConnectionStatus.NotConnected
                        || _db.EntityMaps.IgnoreQueryFilters().Any(m => m.ConnectionId == c.Id), ct);

        if (realmTakenElsewhere)
        {
            _logger.LogWarning(
                "QuickBooks callback for organization {OrganizationId} refused: company {RealmId} is connected to another organization.",
                organizationId, realmId);
            await AbandonAsync(connection, redemption.UserId,
                $"QuickBooks company {realmId} is already connected to another organization. Each QuickBooks company can be connected to one organization only.", ct);
            return Error(CallbackReasons.RealmInUse);
        }

        if (connection is null)
        {
            connection = new IntegrationConnection
            {
                OrganizationId = redemption.OrganizationId,
                ProviderKey    = ProviderKeys.QuickBooksOnline,
                Status         = ConnectionStatus.NotConnected
            };
            _db.Connections.Add(connection);
            await _db.SaveChangesAsync(ct);
        }

        var previousRealm = connection.RealmId;
        var realmChanged  = previousRealm is not null && previousRealm != realmId;

        // Checked before the code is exchanged, so a refused company never even yields tokens.
        if (realmChanged && await _db.EntityMaps.AnyAsync(m => m.ConnectionId == connection.Id, ct))
        {
            _logger.LogWarning(
                "QuickBooks callback for organization {OrganizationId} refused: company {NewRealm} is not the company {OldRealm} its records are mapped to.",
                redemption.OrganizationId, realmId, previousRealm);

            IntegrationAudit.Add(_db, connection.Id, AuditAreas.Connection, "ConnectRefusedRealmMismatch",
                new { RealmId = previousRealm }, new { RealmId = realmId }, redemption.UserId, redemption.OrganizationId);
            await ConnectionPersistence.SaveWithRetryAsync(_db, connection, c =>
            {
                if (c.Status == ConnectionStatus.Connecting) c.Status = ConnectionStatus.NotConnected;
                c.LastError = $"QuickBooks company {realmId} was refused: this organization's records are mapped to company {previousRealm}. "
                            + "Reconnect that company, or ask support to reset the mappings.";
            }, ct);

            return Error(CallbackReasons.RealmMismatch);
        }

        TokenGrant grant;
        try
        {
            grant = await AuthProvider(connection.ProviderKey).ExchangeCodeAsync(code.Trim(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("QuickBooks code exchange failed for organization {OrganizationId} ({ErrorType}).",
                redemption.OrganizationId, ex.GetType().Name);
            await AbandonAsync(connection, redemption.UserId,
                ex is AccountingAuthException ? ex.Message : $"The authorization code could not be exchanged ({ex.GetType().Name}).", ct);
            return Error(CallbackReasons.ExchangeFailed);
        }

        var settings = await _accessor.GetOrCreateSettingsAsync(connection, ct);
        var before   = new { Status = connection.Status.ToString(), RealmId = previousRealm };

        if (realmChanged)
            await ResetForNewCompanyAsync(connection, settings, redemption.UserId, ct);

        var now    = DateTime.UtcNow;
        var status = settings.MatchingConfirmedAt is not null ? ConnectionStatus.Live : ConnectionStatus.NeedsSetup;

        IntegrationAudit.Add(_db, connection.Id, AuditAreas.Connection, "Connected", before,
            new { Status = status.ToString(), RealmId = realmId, Environment = EnvironmentFromOptions().ToString() },
            redemption.UserId, redemption.OrganizationId);

        await ConnectionPersistence.SaveWithRetryAsync(_db, connection, c =>
        {
            if (realmChanged)
            {
                // The old company's facts; the reference load below fills in the new one's.
                c.CompanyName          = null;
                c.HomeCurrencyCode     = null;
                c.MultiCurrencyEnabled = null;
                c.Country              = null;
            }

            _vault.Store(c, grant);
            c.RealmId           = realmId;
            c.Environment       = EnvironmentFromOptions();
            c.Status            = status;
            c.ConnectedByUserId = redemption.UserId;
            c.ConnectedAt       = now;
            c.LastRefreshAt     = now;
            c.LastError         = null;
            c.ReconnectWarnedAt = null;
        }, ct);

        _logger.LogInformation("QuickBooks connected for organization {OrganizationId}: realm {RealmId}, status {Status}.",
            redemption.OrganizationId, realmId, status);

        await TryLoadReferenceDataAsync(connection, ct);

        // Anything suspended by an earlier revoke or disconnect goes again — same company, same maps.
        await ConnectionPersistence.TryResumeOutboxAsync(_services, connection.Id, _logger, ct);

        return Connected();
    }

    /// <summary>
    /// A different company, and nothing was ever mapped to the old one: everything that named the old
    /// company's records — accounts, tax codes, terms, the matching sign-off, Live — is cleared, so the
    /// new company starts from setup instead of inheriting ids that mean nothing in its books.
    /// </summary>
    private async Task ResetForNewCompanyAsync(
        IntegrationConnection connection, IntegrationSettings settings, int userId, CancellationToken ct)
    {
        IntegrationAudit.Add(_db, connection.Id, AuditAreas.Settings, "ResetForNewCompany",
            new
            {
                connection.RealmId, settings.MatchingConfirmedAt, Mode = settings.Mode.ToString(),
                settings.DefaultIncomeAccountId, settings.DefaultExpenseAccountId, settings.FreightExpenseAccountId,
                settings.DiscountAccountId, settings.DefaultPurchaseTaxCodeId
            },
            null, userId, connection.OrganizationId);

        settings.MatchingConfirmedAt      = null;
        settings.Mode                     = SyncMode.DryRun;
        settings.DefaultIncomeAccountId   = null;
        settings.DefaultExpenseAccountId  = null;
        settings.FreightExpenseAccountId  = null;
        settings.DiscountAccountId        = null;
        settings.DefaultPurchaseTaxCodeId = null;
        settings.ModifiedBy               = userId;
        settings.ModifiedDate             = DateTime.UtcNow;

        _db.TaxCodeMappings.RemoveRange(
            await _db.TaxCodeMappings.Where(m => m.ConnectionId == connection.Id).ToListAsync(ct));
        _db.PaymentTermMappings.RemoveRange(
            await _db.PaymentTermMappings.Where(m => m.ConnectionId == connection.Id).ToListAsync(ct));
        _db.ReferenceSnapshots.RemoveRange(
            await _db.ReferenceSnapshots.Where(s => s.ConnectionId == connection.Id).ToListAsync(ct));
        _db.MatchCandidates.RemoveRange(
            await _db.MatchCandidates.Where(m => m.ConnectionId == connection.Id).ToListAsync(ct));
    }

    /// <summary>
    /// Company name, currency, accounts, tax codes and terms, straight away, so the screen the person
    /// lands on can show them. Best effort: a failure here must not undo a connection that worked — the
    /// daily job and the Refresh button fetch it again.
    /// </summary>
    private async Task TryLoadReferenceDataAsync(IntegrationConnection connection, CancellationToken ct)
    {
        try
        {
            var result = await _reference.FetchAndStoreAsync(connection, ct);
            if (!result.IsSuccess)
                _logger.LogWarning("QuickBooks reference data was not loaded after connecting ({Outcome}: {Message}).",
                    result.Outcome, result.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("QuickBooks reference data was not loaded after connecting ({ErrorType}).", ex.GetType().Name);
        }
    }

    /// <summary>Records why a connect did not complete; a connection that was only Connecting goes back.</summary>
    private async Task AbandonAsync(IntegrationConnection? connection, int userId, string reason, CancellationToken ct)
    {
        if (connection is null) return;

        IntegrationAudit.Add(_db, connection.Id, AuditAreas.Connection, "ConnectFailed", null, new { Reason = reason },
            userId, connection.OrganizationId);

        await ConnectionPersistence.SaveWithRetryAsync(_db, connection, c =>
        {
            if (c.Status == ConnectionStatus.Connecting) c.Status = ConnectionStatus.NotConnected;
            c.LastError = ConnectionPersistence.Truncate(reason);
        }, ct);
    }

    private IntegrationEnvironment EnvironmentFromOptions() =>
        _options.IsProduction ? IntegrationEnvironment.Production : IntegrationEnvironment.Sandbox;

    private IAccountingAuthProvider AuthProvider(string providerKey) =>
        _authProviders.FirstOrDefault(p => string.Equals(p.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase))
        ?? throw new AccountingAuthException($"No OAuth provider is registered for '{providerKey}'.");

    private static bool IsAccessDenied(string error) =>
        string.Equals(error.Trim(), CallbackReasons.AccessDenied, StringComparison.OrdinalIgnoreCase);

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];

    private string Connected() => Redirect("result=connected");

    private string Error(string reason) => Redirect($"result=error&reason={Uri.EscapeDataString(reason)}");

    private string Redirect(string query)
    {
        var target = string.IsNullOrWhiteSpace(_options.FrontendReturnUrl) ? "/" : _options.FrontendReturnUrl.Trim();
        return target + (target.Contains('?') ? "&" : "?") + query;
    }
}
