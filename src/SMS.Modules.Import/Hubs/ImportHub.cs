using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Import.Models;
using SMS.Shared.Authorization;
using SMS.Shared.Common;

namespace SMS.Modules.Import.Hubs;

internal static class ImportHubGroups
{
    /// <summary>One group per user, organization-qualified.</summary>
    public static string User(Guid org, int userId) => $"org_{org}_import_user_{userId}";
}

/// <summary>
/// A40 C8 — <c>/hubs/import</c>: a connection joins its own user group; the importer pushes <c>ImportProgress</c>
/// { sessionUuid, processed, total, status } after every batch. Needs MODULE_DATA_IMPORT and an IMPORT_* permission.
/// </summary>
[Authorize]
internal sealed class ImportHub : Hub
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ImportHub> _log;

    public ImportHub(IServiceProvider services, ILogger<ImportHub> log)
    {
        _services = services;
        _log = log;
    }

    public override async Task OnConnectedAsync()
    {
        var user = Context.User;
        var org = Guid.TryParse(user?.FindFirst("organizationId")?.Value, out var o) ? o : Guid.Empty;
        var userId = user?.GetUserId() ?? 0;
        var gate = _services.GetService<IModuleGate>();
        var allowed = org != Guid.Empty && userId > 0
                      && user!.GetPermissions().Any(p => p.StartsWith("IMPORT_", StringComparison.OrdinalIgnoreCase))
                      && await gate.IsOnAsync(org, ModuleCodes.DataImport, Context.ConnectionAborted);
        if (!allowed)
        {
            _log.LogInformation("ImportHub: connection refused for user {UserId} (organization {OrganizationId}).", userId, org);
            Context.Abort();
            return;
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, ImportHubGroups.User(org, userId));
        await base.OnConnectedAsync();
    }
}

internal interface IImportHubNotifier
{
    Task ProgressAsync(Guid org, IEnumerable<int> userIds, ImportProgressModel progress);
}

internal sealed class ImportHubNotifier : IImportHubNotifier
{
    public const string ProgressEvent = "ImportProgress";

    private readonly IHubContext<ImportHub> _hub;
    private readonly ILogger _log;

    public ImportHubNotifier(IHubContext<ImportHub> hub, ILogger<ImportHubNotifier>? log = null)
    {
        _hub = hub;
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    public async Task ProgressAsync(Guid org, IEnumerable<int> userIds, ImportProgressModel progress)
    {
        foreach (var userId in userIds.Where(u => u > 0).Distinct())
        {
            try
            {
                await _hub.Clients.Group(ImportHubGroups.User(org, userId)).SendAsync(ProgressEvent, progress);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "ImportHub: progress for session {Session} failed.", progress.SessionUuid);
            }
        }
    }
}
