using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SMS.Modules.Integration.Configuration;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Integration.Jobs;

internal sealed record TokenRefreshSummary(int Refreshed, int Failed, int Warned);

/// <summary>
/// Daily: refreshes every usable connection's token, whether or not anything was synced (plan QBI-09).
/// <para>
/// Intuit's refresh token lives about a hundred days from its last use and rotates when used. A company
/// that pushes nothing for a season would otherwise lose its grant silently and only find out on the
/// first invoice after. Refreshing on a schedule of its own keeps it alive.
/// </para>
/// <para>
/// It also warns — once a week at most — when a grant's hard expiry is inside the warning window:
/// that one no refresh can extend, and only a person reconnecting fixes it.
/// </para>
/// </summary>
internal sealed class TokenRefreshJob
{
    internal const string RecurringJobId = "integration-token-refresh";
    internal static readonly TimeSpan WarnAgainAfter = TimeSpan.FromDays(7);

    private readonly IntegrationDbContext     _db;
    private readonly IServiceScopeFactory     _scopes;
    private readonly IntegrationJobOptions    _options;
    private readonly ILogger<TokenRefreshJob> _logger;

    public TokenRefreshJob(
        IntegrationDbContext db, IServiceScopeFactory scopes, IOptions<IntegrationJobOptions> options, ILogger<TokenRefreshJob> logger)
    {
        _db      = db;
        _scopes  = scopes;
        _options = options.Value;
        _logger  = logger;
    }

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    public async Task RunAsync()
    {
        var summary = await RefreshAllAsync();
        _logger.LogInformation("QuickBooks token refresh: {Refreshed} refreshed, {Failed} failed, {Warned} reconnect warning(s).",
            summary.Refreshed, summary.Failed, summary.Warned);
    }

    internal async Task<TokenRefreshSummary> RefreshAllAsync(DateTime? utcNow = null, CancellationToken ct = default)
    {
        var now = utcNow ?? DateTime.UtcNow;

        // Every organization: this runs with no tenant. Filtered by hand, not by the tenant filter.
        var connections = await _db.Connections
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c => c.ProviderKey == ProviderKeys.QuickBooksOnline
                     && (c.Status == ConnectionStatus.Connected || c.Status == ConnectionStatus.NeedsSetup || c.Status == ConnectionStatus.Live)
                     && c.EncryptedRefreshToken != null)
            .Select(c => new { c.Id, c.OrganizationId })
            .ToListAsync(ct);

        int refreshed = 0, failed = 0, warned = 0;

        foreach (var connection in connections)
        {
            // Each connection in its own scope and under its own organization, so one company's broken
            // grant (or a bad row) never stops the others, and nothing one did is visible to the next.
            HangfireTenantScope.OrganizationId = connection.OrganizationId;
            try
            {
                using var scope = _scopes.CreateScope();

                try
                {
                    await scope.ServiceProvider.GetRequiredService<TokenManager>().ForceRefreshAsync(connection.Id, ct);
                    refreshed++;
                }
                catch (ConnectionUnavailableException ex)
                {
                    failed++;
                    _logger.LogWarning("QuickBooks connection {ConnectionId} (organization {OrganizationId}) could not be refreshed: {Status}.",
                        connection.Id, connection.OrganizationId, ex.Status);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    _logger.LogWarning("QuickBooks connection {ConnectionId} (organization {OrganizationId}) refresh failed ({ErrorType}).",
                        connection.Id, connection.OrganizationId, ex.GetType().Name);
                }

                if (await WarnIfExpiringAsync(scope.ServiceProvider, connection.Id, now, ct))
                    warned++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning("QuickBooks token job skipped connection {ConnectionId} ({ErrorType}).", connection.Id, ex.GetType().Name);
            }
            finally
            {
                HangfireTenantScope.OrganizationId = null;
            }
        }

        return new TokenRefreshSummary(refreshed, failed, warned);
    }

    /// <summary>
    /// Tells whoever connected the company that it must be reconnected before the grant runs out. When
    /// there is nobody to tell (no user, or no notification service in this host) the warning goes on
    /// the connection's LastError, which the screen shows.
    /// </summary>
    private async Task<bool> WarnIfExpiringAsync(IServiceProvider services, int connectionId, DateTime now, CancellationToken ct)
    {
        var db         = services.GetRequiredService<IntegrationDbContext>();
        var connection = await db.Connections.FirstOrDefaultAsync(c => c.Id == connectionId, ct);

        if (connection is null || !connection.IsUsable()) return false;
        if (connection.RefreshTokenExpiresAt is not { } expiresAt) return false;
        if (expiresAt >= now.AddDays(_options.ReconnectWarningDays)) return false;
        if (connection.ReconnectWarnedAt is { } warnedAt && now - warnedAt < WarnAgainAfter) return false;

        var message = $"The QuickBooks connection to {connection.CompanyName ?? "your company"} must be reconnected before "
                    + $"{expiresAt:dd MMM yyyy}. After that nothing more can be sent to QuickBooks until someone reconnects it.";

        var notifications = services.GetService<INotificationService>();
        var notified      = false;

        if (notifications is not null && connection.ConnectedByUserId is int userId and > 0)
        {
            await notifications.TryCreateAsync(new NotificationRequest(
                UserId:        userId,
                Type:          "QBO_RECONNECT_REQUIRED",
                Title:         "Reconnect QuickBooks",
                Message:       message,
                Category:      "Integration",
                EntityType:    "IntegrationConnection",
                EntityUuid:    connection.Uuid.ToString(),
                NavigationUrl: "/portal/pages/integrations/quickbooks",
                SendEmail:     true));
            notified = true;
        }
        else
        {
            _logger.LogWarning("QuickBooks connection {ConnectionId} must be reconnected before {ExpiresAt:yyyy-MM-dd}; nobody could be notified.",
                connectionId, expiresAt);
        }

        await ConnectionPersistence.SaveWithRetryAsync(db, connection, c =>
        {
            c.ReconnectWarnedAt = now;
            if (!notified) c.LastError = ConnectionPersistence.Truncate(message);
        }, ct);

        return true;
    }
}
