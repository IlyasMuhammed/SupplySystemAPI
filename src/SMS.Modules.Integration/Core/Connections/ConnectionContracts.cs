using Microsoft.EntityFrameworkCore;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;

namespace SMS.Modules.Integration.Core.Connections;

/// <summary>Thrown when a connection cannot be used: not connected, revoked, expired, or app keys missing.</summary>
internal sealed class ConnectionUnavailableException : Exception
{
    public ConnectionStatus Status { get; }

    public ConnectionUnavailableException(ConnectionStatus status, string message, Exception? inner = null)
        : base(message, inner) => Status = status;
}

/// <summary>
/// Hands out a usable access token for a connection, refreshing it first when it is close to expiry
/// and persisting the rotated refresh token atomically. The only place plaintext tokens exist outside
/// the OAuth exchange itself.
/// </summary>
internal interface ITokenManager
{
    /// <exception cref="ConnectionUnavailableException">Not connected, revoked or expired.</exception>
    Task<string> GetValidAccessTokenAsync(int connectionId, CancellationToken ct = default);
}

/// <summary>Marks a connection unusable when a call proves it is (auth revoked mid-sync).</summary>
internal interface IConnectionHealth
{
    Task MarkUnavailableAsync(int connectionId, ConnectionStatus status, string reason, CancellationToken ct = default);
}

/// <summary>
/// Implemented by the sync engine. Called by the connection lifecycle: suspend everything queued when a
/// connection is revoked/expired/disconnected, resume it when the same realm reconnects.
/// </summary>
internal interface IOutboxControl
{
    Task<int> SuspendAllAsync(int connectionId, string reason, CancellationToken ct = default);
    Task<int> ResumeAllAsync(int connectionId, CancellationToken ct = default);
}

/// <summary>Cached QuickBooks reference data (accounts, tax codes, terms, currencies, preferences, company).</summary>
internal interface IReferenceDataReader
{
    /// <summary>Null when nothing has been fetched yet.</summary>
    Task<RemoteReferenceData?> GetCachedAsync(int connectionId, CancellationToken ct = default);
}

/// <summary>Reads the current organization's connection and settings (tenant-filtered).</summary>
internal interface IConnectionAccessor
{
    /// <summary>The current tenant's QuickBooks connection in any status, or null if never started.</summary>
    Task<IntegrationConnection?> GetCurrentAsync(CancellationToken ct = default);

    /// <summary>The settings row for a connection, created with defaults if missing.</summary>
    Task<IntegrationSettings> GetOrCreateSettingsAsync(IntegrationConnection connection, CancellationToken ct = default);
}

internal static class ConnectionExtensions
{
    /// <summary>A connection the gateway may push through (the settings' mode decides dry-run vs live).</summary>
    public static bool IsUsable(this IntegrationConnection c) =>
        c.Status is ConnectionStatus.Connected or ConnectionStatus.NeedsSetup or ConnectionStatus.Live
        && !string.IsNullOrEmpty(c.RealmId);

    public static ProviderContext ToProviderContext(this IntegrationConnection c) =>
        new(c.Id, c.OrganizationId, c.RealmId ?? string.Empty, c.Environment);
}

internal sealed class ConnectionAccessor : IConnectionAccessor
{
    private readonly IntegrationDbContext _db;

    public ConnectionAccessor(IntegrationDbContext db) => _db = db;

    /// <remarks>
    /// Filtered by the current organization <b>explicitly</b>, not only through the tenant query filter:
    /// a super admin's requests bypass that filter, and "the current company" must still mean the one of
    /// the organization they are acting in — never whichever organization's connection happens to come first.
    /// </remarks>
    public Task<IntegrationConnection?> GetCurrentAsync(CancellationToken ct = default)
    {
        var organizationId = _db.TenantContext.OrganizationId;
        return _db.Connections.FirstOrDefaultAsync(
            c => c.ProviderKey == ProviderKeys.QuickBooksOnline && c.OrganizationId == organizationId, ct);
    }

    public async Task<IntegrationSettings> GetOrCreateSettingsAsync(IntegrationConnection connection, CancellationToken ct = default)
    {
        var settings = await _db.Settings.FirstOrDefaultAsync(s => s.ConnectionId == connection.Id, ct);
        if (settings is not null) return settings;

        settings = new IntegrationSettings
        {
            ConnectionId   = connection.Id,
            OrganizationId = connection.OrganizationId
        };
        _db.Settings.Add(settings);
        await _db.SaveChangesAsync(ct);
        return settings;
    }
}
