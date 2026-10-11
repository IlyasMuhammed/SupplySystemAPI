using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SMS.Modules.Pos.Data;
using SMS.Modules.Pos.Models;
using SMS.Shared.Authorization;
using SMS.Shared.Common;

namespace SMS.Modules.Pos.Hubs;

/// <summary>A38 D-15 — SignalR group names, always organization-qualified.</summary>
internal static class PosHubGroups
{
    public static string Organization(Guid org)                 => $"org_{org}_pos";
    public static string Store(Guid org, Guid storeUuid)        => $"org_{org}_store_{storeUuid}";
    public static string Terminal(Guid org, Guid terminalUuid)  => $"org_{org}_terminal_{terminalUuid}";
}

/// <summary>
/// A38 C11 / API-CONTRACT §8 — <c>/hubs/pos</c>. A connection needs MODULE_POS for the caller's organization and some
/// POS permission (D-15, checked on connect — [RequiresFeature] does not apply to hubs). Clients join their terminal
/// with <see cref="JoinTerminal"/>, which also puts them in the store group and the organization-wide POS group (org-wide
/// catalog / customer events), and keep <c>lastSeenAt</c> fresh with <see cref="Heartbeat"/>.
/// </summary>
[Authorize]
internal sealed class PosHub : Hub
{
    /// <summary>A heartbeat writes at most this often (keeps a busy store from hammering the terminal row).</summary>
    internal static readonly TimeSpan HeartbeatWriteInterval = TimeSpan.FromSeconds(60);

    private readonly IServiceProvider _services;
    private readonly ILogger<PosHub>  _log;

    public PosHub(IServiceProvider services, ILogger<PosHub> log)
    {
        _services = services;
        _log      = log;
    }

    private Guid OrganizationId =>
        Guid.TryParse(Context.User?.FindFirst("organizationId")?.Value, out var org) ? org : Guid.Empty;

    public override async Task OnConnectedAsync()
    {
        var org   = OrganizationId;
        var user  = Context.User;
        var gate  = _services.GetService<IModuleGate>();
        var allowed = org != Guid.Empty
                      && user is not null && user.GetPermissions().Any(p => PosPermissions.All.Contains(p))
                      && await gate.IsOnAsync(org, ModuleCodes.Pos, Context.ConnectionAborted);
        if (!allowed)
        {
            _log.LogInformation("PosHub: connection refused for user {UserId} (organization {OrganizationId}).", user?.GetUserId(), org);
            Context.Abort();
            return;
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, PosHubGroups.Organization(org));
        await base.OnConnectedAsync();
    }

    /// <summary>Joins the terminal's store and terminal groups. Another organization's terminal reads as unknown.</summary>
    public async Task JoinTerminal(Guid terminalUuid)
    {
        var org = OrganizationId;
        var db  = _services.GetRequiredService<PosDbContext>();
        var terminal = await db.Terminals.IgnoreQueryFilters().Include(t => t.Store)
            .FirstOrDefaultAsync(t => t.Uuid == terminalUuid && t.OrganizationId == org)
            ?? throw new HubException("Terminal not found.");

        await Groups.AddToGroupAsync(Context.ConnectionId, PosHubGroups.Store(org, terminal.Store.Uuid));
        await Groups.AddToGroupAsync(Context.ConnectionId, PosHubGroups.Terminal(org, terminal.Uuid));
        terminal.LastSeenAt = DateTime.UtcNow;
        await TrySaveAsync(db);
    }

    public async Task Heartbeat(Guid terminalUuid)
    {
        var org = OrganizationId;
        var db  = _services.GetRequiredService<PosDbContext>();
        var terminal = await db.Terminals.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Uuid == terminalUuid && t.OrganizationId == org)
            ?? throw new HubException("Terminal not found.");
        var now = DateTime.UtcNow;
        if (terminal.LastSeenAt is { } seen && now - seen < HeartbeatWriteInterval) return;
        terminal.LastSeenAt = now;
        await TrySaveAsync(db);
    }

    /// <summary>Presence is best-effort: a concurrent back-office edit wins and the next heartbeat writes again.</summary>
    private async Task TrySaveAsync(PosDbContext db)
    {
        try { await db.SaveChangesAsync(); }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); }
    }
}
