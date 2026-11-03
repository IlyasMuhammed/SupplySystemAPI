using Microsoft.Extensions.Logging;
using SMS.Shared.Common;

namespace SMS.Modules.Hrm.Security;

/// <summary>
/// The display name behind every "...ByName" column. SMS access tokens carry no name claim, so a name read from the token is null on a
/// real host: the organization's user directory answers, by user id, in requests and in jobs alike (the organization is explicit). A name
/// is display only: a missing or failing directory falls back to <c>known</c> and never fails the action.
/// </summary>
public sealed class HrmUserNames
{
    private readonly IOrgUserDirectory? _users;
    private readonly ILogger<HrmUserNames>? _log;

    public HrmUserNames(IOrgUserDirectory? users = null, ILogger<HrmUserNames>? log = null)
    {
        _users = users;
        _log = log;
    }

    public async Task<string?> NameAsync(Guid organizationId, int? userId, string? known = null, CancellationToken ct = default)
    {
        if (_users is null || userId is not > 0 || organizationId == Guid.Empty) return known;
        try
        {
            var name = (await _users.GetUsersAsync(organizationId, [userId.Value], ct)).FirstOrDefault(u => u.UserId == userId)?.Name;
            return string.IsNullOrWhiteSpace(name) ? known : name.Trim();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log?.LogWarning(ex, "The name of user {UserId} could not be read; the record is saved without it.", userId);
            return known;
        }
    }
}
