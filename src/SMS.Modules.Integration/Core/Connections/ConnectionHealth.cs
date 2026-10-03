using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;

namespace SMS.Modules.Integration.Core.Connections;

/// <summary>
/// Called when a live call proves a connection unusable — QuickBooks answered 401 to a token we had
/// just refreshed, i.e. the app was disconnected inside QuickBooks. Marks the connection, records why,
/// and suspends everything queued so the outbox does not fill with retries that cannot succeed.
/// </summary>
internal sealed class ConnectionHealth : IConnectionHealth
{
    private readonly IntegrationDbContext     _db;
    private readonly IServiceProvider         _services;
    private readonly ILogger<ConnectionHealth> _logger;

    public ConnectionHealth(IntegrationDbContext db, IServiceProvider services, ILogger<ConnectionHealth> logger)
    {
        _db       = db;
        _services = services;
        _logger   = logger;
    }

    public async Task MarkUnavailableAsync(int connectionId, ConnectionStatus status, string reason, CancellationToken ct = default)
    {
        if (status is not (ConnectionStatus.Revoked or ConnectionStatus.Expired))
            throw new ArgumentOutOfRangeException(nameof(status), status,
                "A connection can only be marked Revoked or Expired from outside its lifecycle.");

        var connection = await _db.Connections.FirstOrDefaultAsync(c => c.Id == connectionId, ct);
        if (connection is null) return;

        // Only a usable connection changes here. NotConnected (the admin disconnected) and an existing
        // Revoked/Expired verdict are both more precise than whatever a failing call can infer.
        if (connection.IsUsable())
        {
            await ConnectionPersistence.SaveWithRetryAsync(_db, connection, c =>
            {
                if (!c.IsUsable()) return;
                c.Status    = status;
                c.LastError = ConnectionPersistence.Truncate(reason);
            }, ct);

            _logger.LogWarning("QuickBooks connection {ConnectionId} marked {Status}: {Reason}", connectionId, status, reason);
        }

        await ConnectionPersistence.TrySuspendOutboxAsync(_services, connectionId, reason, _logger, ct);
    }
}
