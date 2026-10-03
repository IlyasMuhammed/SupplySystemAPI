using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;

namespace SMS.Modules.Integration.Core.Connections;

/// <summary>Small shared pieces of the connection lifecycle.</summary>
internal static class ConnectionPersistence
{
    private const int MaxSaveAttempts = 3;

    /// <summary>
    /// Applies a change to a connection and saves it, re-reading and re-applying when the row version
    /// says someone else wrote the row in between.
    /// <para>
    /// The someone else is nearly always a token refresh — the daily job, or a sync call that found its
    /// token close to expiry — and it touches only the token columns. A status change made by an admin
    /// must not fail with a 409 because of that, so the change is replayed onto the fresh row instead.
    /// Only for changes that are safe to replay; the token refresh itself resolves its race differently
    /// (see <see cref="TokenManager"/>).
    /// </para>
    /// </summary>
    public static async Task SaveWithRetryAsync(
        IntegrationDbContext db, IntegrationConnection connection, Action<IntegrationConnection> apply, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            apply(connection);
            connection.ModifiedDate = DateTime.UtcNow;

            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxSaveAttempts)
            {
                await db.Entry(connection).ReloadAsync(ct);
            }
        }
    }

    /// <summary>
    /// Asks the sync engine to suspend everything queued for a connection. Best effort and optional:
    /// <see cref="IOutboxControl"/> belongs to the sync engine, which may not be registered (unit tests,
    /// or a host without it), and a failure to suspend must never undo a revoke or a disconnect — the
    /// sync jobs refuse to push through an unusable connection anyway.
    /// </summary>
    public static async Task TrySuspendOutboxAsync(
        IServiceProvider services, int connectionId, string reason, ILogger logger, CancellationToken ct)
    {
        var outbox = services.GetService<IOutboxControl>();
        if (outbox is null) return;

        try
        {
            var suspended = await outbox.SuspendAllAsync(connectionId, reason, ct);
            if (suspended > 0)
                logger.LogInformation("Suspended {Count} queued QuickBooks push(es) for connection {ConnectionId}: {Reason}",
                    suspended, connectionId, reason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not suspend the QuickBooks outbox for connection {ConnectionId}.", connectionId);
        }
    }

    /// <summary>The reverse of <see cref="TrySuspendOutboxAsync"/>, after the same company reconnects.</summary>
    public static async Task TryResumeOutboxAsync(
        IServiceProvider services, int connectionId, ILogger logger, CancellationToken ct)
    {
        var outbox = services.GetService<IOutboxControl>();
        if (outbox is null) return;

        try
        {
            var resumed = await outbox.ResumeAllAsync(connectionId, ct);
            if (resumed > 0)
                logger.LogInformation("Resumed {Count} suspended QuickBooks push(es) for connection {ConnectionId}.",
                    resumed, connectionId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not resume the QuickBooks outbox for connection {ConnectionId}.", connectionId);
        }
    }

    /// <summary>Column-safe copy of a message for <see cref="IntegrationConnection.LastError"/> (max 1000).</summary>
    public static string? Truncate(string? message, int max = 1000) =>
        message is null ? null : message.Length <= max ? message : message[..(max - 1)] + "…";

    /// <summary>What a person needs to do about a connection in this status.</summary>
    public static string DescribeUnusable(ConnectionStatus status) => status switch
    {
        ConnectionStatus.NotConnected => "QuickBooks is not connected for this organization.",
        ConnectionStatus.Connecting   => "The QuickBooks connection is not finished — Intuit's consent is still pending.",
        ConnectionStatus.Revoked      => "QuickBooks access was revoked. Reconnect QuickBooks.",
        ConnectionStatus.Expired      => "The QuickBooks authorization expired. Reconnect QuickBooks.",
        _                             => "The QuickBooks connection cannot be used."
    };
}
