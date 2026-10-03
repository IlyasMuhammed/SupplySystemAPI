using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Integration.Core.Sync;

/// <summary>A connection as a cross-organization job sees it before entering that organization.</summary>
internal sealed record ConnectionRef(int Id, Guid OrganizationId, ConnectionStatus Status, string? RealmId)
{
    public bool IsUsable => Status is ConnectionStatus.Connected or ConnectionStatus.NeedsSetup or ConnectionStatus.Live
                            && !string.IsNullOrEmpty(RealmId);
}

/// <summary>
/// Runs a job's work once per connection, each inside its own DI scope with
/// <see cref="HangfireTenantScope.OrganizationId"/> set — so every tenant-filtered query in that scope
/// sees exactly that organization, as in a user's request. One connection's failure is logged and
/// never stops the others.
/// </summary>
internal sealed class ConnectionJobRunner
{
    private readonly IServiceScopeFactory          _scopes;
    private readonly IntegrationDbContext          _db;
    private readonly ILogger<ConnectionJobRunner>  _logger;

    public ConnectionJobRunner(IServiceScopeFactory scopes, IntegrationDbContext db, ILogger<ConnectionJobRunner> logger)
    {
        _scopes = scopes;
        _db     = db;
        _logger = logger;
    }

    /// <summary>Every organization's connection (unfiltered: a job has no tenant of its own).</summary>
    public async Task<IReadOnlyList<ConnectionRef>> ListAsync(bool usableOnly, CancellationToken ct = default)
    {
        var rows = await _db.Connections
            .IgnoreQueryFilters()
            .AsNoTracking()
            .OrderBy(c => c.Id)
            .Select(c => new ConnectionRef(c.Id, c.OrganizationId, c.Status, c.RealmId))
            .ToListAsync(ct);

        return usableOnly ? rows.Where(r => r.IsUsable).ToList() : rows;
    }

    /// <returns>How many connections completed without an exception.</returns>
    public async Task<int> ForEachAsync(
        string jobName, bool usableOnly, Func<IServiceProvider, ConnectionRef, Task> work, CancellationToken ct = default)
    {
        var ok = 0;

        foreach (var connection in await ListAsync(usableOnly, ct))
        {
            ct.ThrowIfCancellationRequested();

            if (await InTenantAsync(connection.OrganizationId, sp => work(sp, connection), ct))
                ok++;
            else
                _logger.LogWarning("{Job}: connection {ConnectionId} (organization {OrganizationId}) failed; continuing with the others.",
                    jobName, connection.Id, connection.OrganizationId);
        }

        return ok;
    }

    /// <summary>Runs <paramref name="work"/> in a fresh scope as <paramref name="organizationId"/>. False when it threw.</summary>
    public async Task<bool> InTenantAsync(Guid organizationId, Func<IServiceProvider, Task> work, CancellationToken ct = default)
    {
        var previous = HangfireTenantScope.OrganizationId;
        HangfireTenantScope.OrganizationId = organizationId;
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            await work(scope.ServiceProvider);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "QuickBooks job work for organization {OrganizationId} failed.", organizationId);
            return false;
        }
        finally
        {
            HangfireTenantScope.OrganizationId = previous;
        }
    }
}
