using Microsoft.EntityFrameworkCore;
using SMS.Modules.Auth.Data;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Auth.Onboarding;

/// <summary>A50 D-28: INVITE_TEAM, meaning the organization has a second internal user (invited counts; accepting is the
/// invitee's part). Deleted users and external (supplier-portal) accounts do not count.</summary>
internal sealed class AuthOnboardingSignal : IOnboardingSignal
{
    internal const string Internal = "INTERNAL";

    private readonly AuthDbContext _db;
    public AuthOnboardingSignal(AuthDbContext db) => _db = db;

    public IReadOnlyCollection<string> StepCodes { get; } = [OnboardingStepCodes.InviteTeam];

    public async Task<IReadOnlySet<string>> CompletedAsync(IReadOnlySet<Guid> demoEntityIds, CancellationToken ct = default)
    {
        var org = _db.TenantContext.OrganizationId;
        var users = await _db.UserAccounts.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.OrganizationId == org && !u.IsDelete && u.SupplierType == Internal).Take(2).CountAsync(ct);
        return users >= 2 ? new HashSet<string> { OnboardingStepCodes.InviteTeam } : new HashSet<string>();
    }
}
