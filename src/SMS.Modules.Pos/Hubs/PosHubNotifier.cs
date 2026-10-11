using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace SMS.Modules.Pos.Hubs;

/// <summary>
/// A38 API-CONTRACT §8 — one method per server → client event. Best-effort: a push failure is logged, never thrown, and
/// callers invoke it after their commit. Org-wide catalog/customer events go to the organization's POS group (every
/// joined terminal of the org); stock and config go to the store group; ForceSync / ShiftAlert / ForceLogout to one terminal.
/// </summary>
internal interface IPosHubNotifier
{
    Task PriceChangedAsync(Guid org, Guid variantUuid, decimal? oldPrice, decimal? newPrice);
    Task StockUpdatedAsync(Guid org, Guid storeUuid, Guid variantUuid, decimal available);
    Task ProductUpdatedAsync(Guid org, Guid variantUuid);
    Task ProductDeactivatedAsync(Guid org, Guid variantUuid);
    Task CustomerBalanceChangedAsync(Guid org, Guid customerUuid, decimal balance, decimal creditLimit);
    /// <summary><paramref name="configType"/>: STORE, TERMINAL, STAFF, TAX, …</summary>
    Task ConfigChangedAsync(Guid org, Guid storeUuid, string configType);
    /// <summary><paramref name="syncType"/>: FULL or DELTA.</summary>
    Task ForceSyncAsync(Guid org, Guid terminalUuid, string syncType);
    /// <summary><paramref name="severity"/>: INFO, WARNING, CRITICAL.</summary>
    Task ShiftAlertAsync(Guid org, Guid terminalUuid, string message, string severity);
    Task ForceLogoutAsync(Guid org, Guid terminalUuid, string reason);
    /// <summary>A39 §6 — ReturnProcessed, CreditNoteIssued/Redeemed/Expired, ReturnPolicyChanged to a store group.</summary>
    Task StoreEventAsync(Guid org, Guid storeUuid, string eventName, object payload);
}

internal static class PosHubEvents
{
    public const string PriceChanged = "PriceChanged", StockUpdated = "StockUpdated", ProductUpdated = "ProductUpdated",
        ProductDeactivated = "ProductDeactivated", CustomerBalanceChanged = "CustomerBalanceChanged", ConfigChanged = "ConfigChanged",
        ForceSync = "ForceSync", ShiftAlert = "ShiftAlert", ForceLogout = "ForceLogout";
    // A39 §6
    public const string ReturnProcessed = "ReturnProcessed", CreditNoteIssued = "CreditNoteIssued", CreditNoteRedeemed = "CreditNoteRedeemed",
        CreditNoteExpired = "CreditNoteExpired", ReturnPolicyChanged = "ReturnPolicyChanged";
}

internal sealed class PosHubNotifier : IPosHubNotifier
{
    private readonly IHubContext<PosHub> _hub;
    private readonly ILogger _log;

    public PosHubNotifier(IHubContext<PosHub> hub, ILogger<PosHubNotifier>? log = null)
    {
        _hub = hub;
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    public Task PriceChangedAsync(Guid org, Guid variantUuid, decimal? oldPrice, decimal? newPrice) =>
        Send(PosHubGroups.Organization(org), PosHubEvents.PriceChanged, new { variantUuid, oldPrice, newPrice });

    public Task StockUpdatedAsync(Guid org, Guid storeUuid, Guid variantUuid, decimal available) =>
        Send(PosHubGroups.Store(org, storeUuid), PosHubEvents.StockUpdated, new { variantUuid, available });

    public Task ProductUpdatedAsync(Guid org, Guid variantUuid) =>
        Send(PosHubGroups.Organization(org), PosHubEvents.ProductUpdated, new { variantUuid });

    public Task ProductDeactivatedAsync(Guid org, Guid variantUuid) =>
        Send(PosHubGroups.Organization(org), PosHubEvents.ProductDeactivated, new { variantUuid });

    public Task CustomerBalanceChangedAsync(Guid org, Guid customerUuid, decimal balance, decimal creditLimit) =>
        Send(PosHubGroups.Organization(org), PosHubEvents.CustomerBalanceChanged, new { customerUuid, balance, creditLimit });

    public Task ConfigChangedAsync(Guid org, Guid storeUuid, string configType) =>
        Send(PosHubGroups.Store(org, storeUuid), PosHubEvents.ConfigChanged, new { configType });

    public Task ForceSyncAsync(Guid org, Guid terminalUuid, string syncType) =>
        Send(PosHubGroups.Terminal(org, terminalUuid), PosHubEvents.ForceSync, new { syncType });

    public Task ShiftAlertAsync(Guid org, Guid terminalUuid, string message, string severity) =>
        Send(PosHubGroups.Terminal(org, terminalUuid), PosHubEvents.ShiftAlert, new { message, severity });

    public Task ForceLogoutAsync(Guid org, Guid terminalUuid, string reason) =>
        Send(PosHubGroups.Terminal(org, terminalUuid), PosHubEvents.ForceLogout, new { reason });

    public Task StoreEventAsync(Guid org, Guid storeUuid, string eventName, object payload) =>
        Send(PosHubGroups.Store(org, storeUuid), eventName, payload);

    private async Task Send(string group, string method, object payload)
    {
        try
        {
            await _hub.Clients.Group(group).SendAsync(method, payload);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PosHub: {Event} to {Group} failed.", method, group);
        }
    }
}
