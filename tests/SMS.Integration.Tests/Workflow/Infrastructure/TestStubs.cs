using SMS.Shared.Common;

namespace SMS.Integration.Tests.Workflow.Infrastructure;

// ── User/OrgChart stubs ───────────────────────────────────────────────────────

internal sealed class StubUserQueryService : IUserQueryService
{
    private static readonly Dictionary<int, UserIdentity> _users = new()
    {
        [101] = new UserIdentity(101, "Alice"),
        [102] = new UserIdentity(102, "Bob"),
        [103] = new UserIdentity(103, "Carol"),
    };

    public Task<UserIdentity?> GetUserAsync(int userId)
        => Task.FromResult(_users.GetValueOrDefault(userId));

    public Task<IReadOnlyList<UserIdentity>> GetUsersAsync(IReadOnlyList<int> userIds)
    {
        IReadOnlyList<UserIdentity> result = userIds
            .Select(id => _users.GetValueOrDefault(id))
            .Where(u => u is not null)
            .Select(u => u!)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<IReadOnlyList<UserIdentity>> GetActiveUsersByRoleAsync(int roleId)
        => Task.FromResult<IReadOnlyList<UserIdentity>>(Array.Empty<UserIdentity>());

    public Task<bool> IsSystemAdminAsync(int userId)
        => Task.FromResult(false);
}

internal sealed class StubOrgChartService : IOrgChartService
{
    public Task<UserIdentity?> GetSupervisorAsync(int userId)      => Task.FromResult<UserIdentity?>(null);
    public Task<UserIdentity?> GetDepartmentHeadAsync(int deptId)  => Task.FromResult<UserIdentity?>(null);
}

// ── Notification stub ─────────────────────────────────────────────────────────

internal sealed class StubNotificationService : INotificationService
{
    public Task CreateAsync(NotificationRequest request)    => Task.CompletedTask;
    public Task TryCreateAsync(NotificationRequest request) => Task.CompletedTask;
    public Task BroadcastAsync(string type, string category, string title, string message, int createdBy = 0)
        => Task.CompletedTask;
    public Task SendEmailAsync(string to, string subject, string htmlBody) => Task.CompletedTask;
}

// ── Document status / clone stubs ─────────────────────────────────────────────

internal sealed class NoOpDocumentStatusService : IDocumentStatusService
{
    public Task<string?> GetStatusAsync(string interfaceCode, Guid documentId)
        => Task.FromResult<string?>("DRAFT");

    public Task UpdateStatusAsync(string interfaceCode, Guid documentId, string newStatus)
        => Task.CompletedTask;
}

internal sealed class TrackingDocumentCloneService : IDocumentCloneService
{
    private readonly Dictionary<Guid, Guid> _cloneMap = new();

    public Task<Guid> CloneDocumentAsync(string interfaceCode, Guid documentId)
    {
        var clone = Guid.NewGuid();
        _cloneMap[documentId] = clone;
        return Task.FromResult(clone);
    }

    public Guid GetCloneId(Guid original)
        => _cloneMap.TryGetValue(original, out var id) ? id : Guid.Empty;
}
