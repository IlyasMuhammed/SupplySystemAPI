namespace SMS.Shared.Common;

/// <summary>A50 D-22/D-26 — who has just accepted an invite (the account is now active and has its password).</summary>
/// <param name="OrganizationName">Not known to Auth — always null today; resolve it through IOrganizationNameLookup.</param>
/// <param name="LoginUrl">The app's sign-in page (AppSettings.BaseUrl + "/auth/login"), or null when no base URL is configured.</param>
public sealed record InviteAcceptedContext(
    Guid OrganizationId, int UserId, string Email, string FirstName, string? LastName, string RoleName,
    string? OrganizationName = null, string? LoginUrl = null);

/// <summary>
/// A50 D-26 — the hook point Auth calls after accept-invite has committed (the welcome e-mail; implemented by Onboarding's
/// templates). Every registered handler is called in turn; one that throws is logged and never fails the accept.
/// The accept-invite endpoint is anonymous, so there is no ambient tenant: use <see cref="InviteAcceptedContext.OrganizationId"/>.
/// </summary>
public interface IUserInviteAcceptedHandler
{
    Task OnInviteAcceptedAsync(InviteAcceptedContext context, CancellationToken ct = default);
}
