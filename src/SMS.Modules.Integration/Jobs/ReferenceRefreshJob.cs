using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Reference;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Integration.Jobs;

internal sealed record ReferenceRefreshSummary(int Refreshed, int Failed);

/// <summary>
/// Daily: reloads every usable connection's accounts, tax codes, terms, currencies, preferences and
/// company info (plan QBI-12), so an account the accountant added or deactivated in QuickBooks shows up
/// in the mapping dropdowns and the preflight without anyone pressing Refresh.
/// </summary>
internal sealed class ReferenceRefreshJob
{
    internal const string RecurringJobId = "integration-reference-refresh";

    private readonly IntegrationDbContext         _db;
    private readonly IServiceScopeFactory         _scopes;
    private readonly ILogger<ReferenceRefreshJob> _logger;

    public ReferenceRefreshJob(IntegrationDbContext db, IServiceScopeFactory scopes, ILogger<ReferenceRefreshJob> logger)
    {
        _db     = db;
        _scopes = scopes;
        _logger = logger;
    }

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(timeoutInSeconds: 600)]
    public async Task RunAsync()
    {
        var summary = await RefreshAllAsync();
        _logger.LogInformation("QuickBooks reference refresh: {Refreshed} refreshed, {Failed} failed.", summary.Refreshed, summary.Failed);
    }

    internal async Task<ReferenceRefreshSummary> RefreshAllAsync(CancellationToken ct = default)
    {
        var connections = await _db.Connections
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(c => c.ProviderKey == ProviderKeys.QuickBooksOnline
                     && (c.Status == ConnectionStatus.Connected || c.Status == ConnectionStatus.NeedsSetup || c.Status == ConnectionStatus.Live)
                     && c.RealmId != null)
            .Select(c => new { c.Id, c.OrganizationId })
            .ToListAsync(ct);

        int refreshed = 0, failed = 0;

        foreach (var item in connections)
        {
            HangfireTenantScope.OrganizationId = item.OrganizationId;
            try
            {
                using var scope = _scopes.CreateScope();
                var db         = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
                var connection = await db.Connections.FirstOrDefaultAsync(c => c.Id == item.Id, ct);
                if (connection is null || !connection.IsUsable()) continue;

                var result = await scope.ServiceProvider.GetRequiredService<IReferenceDataStore>().FetchAndStoreAsync(connection, ct);
                if (result.IsSuccess)
                {
                    refreshed++;
                }
                else
                {
                    failed++;
                    _logger.LogWarning("QuickBooks reference data for connection {ConnectionId} was not refreshed ({Outcome}: {Message}).",
                        item.Id, result.Outcome, result.Message);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                _logger.LogWarning("QuickBooks reference refresh failed for connection {ConnectionId} ({ErrorType}).",
                    item.Id, ex.GetType().Name);
            }
            finally
            {
                HangfireTenantScope.OrganizationId = null;
            }
        }

        return new ReferenceRefreshSummary(refreshed, failed);
    }
}
