using SMS.Modules.Onboarding.Models;
using SMS.Shared.Common;

namespace SMS.Modules.Onboarding.Services.Setup;

/// <summary>
/// A50 D-22 / task 059 — Step 7 invites. Wraps Auth's <see cref="IOrgUserProvisioningService.InviteUserAsync"/> so the
/// wizard (and its tests) do not need Auth in the host.
/// </summary>
internal interface ISetupUserInviter
{
    Task<SetupInviteResult> InviteAsync(Guid organizationId, string organizationName, string email, string? firstName, string? lastName,
        int roleId, int invitedBy, CancellationToken ct = default);
}

internal sealed class SetupUserInviter : ISetupUserInviter
{
    private readonly IOrgUserProvisioningService? _users;

    public SetupUserInviter(IOrgUserProvisioningService? users = null) => _users = users;

    public async Task<SetupInviteResult> InviteAsync(Guid organizationId, string organizationName, string email, string? firstName, string? lastName,
        int roleId, int invitedBy, CancellationToken ct = default)
    {
        if (_users is null)
            return new SetupInviteResult { Email = email, Outcome = SetupInviteOutcomes.Failed, Message = "User invitations are not available on this server." };

        var r = await _users.InviteUserAsync(new OrgUserInviteRequest(organizationId, email, firstName, lastName, roleId, organizationName, invitedBy), ct);
        return new SetupInviteResult { Email = r.Email, Outcome = r.Outcome, UserId = r.UserId, Message = r.Message };
    }
}
