using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Auth.Domain;
using SMS.Modules.Auth.Models;
using SMS.Modules.Auth.Services;
using SMS.Shared.Exceptions;
using Xunit;
using static SMS.Modules.Auth.Tests.Security.SecurityTestBed;

namespace SMS.Modules.Auth.Tests.Security;

/// <summary>
/// The two ways a platform super admin gets an organization's admin into their account when the invite e-mail went
/// nowhere: set the password directly, or resend the invite (optionally to a corrected address).
/// </summary>
public class SuperAdminAccountRecoveryTests
{
    private const string NewPassword = "Brand#New2026";

    private static void MakeInvitePending(SecurityTestBed bed, int userId, string token = "old-invite-token") => bed.Write(db =>
    {
        var u = db.UserAccounts.IgnoreQueryFilters().Single(x => x.UserID == userId);
        u.IsActive = false;
        u.LastLoginAt = null;
        u.InviteToken = token;
        u.InviteTokenExpiresAt = DateTime.UtcNow.AddDays(7);
    });

    private static Task<LoginResponseModel> Login(SecurityTestBed bed, string email, string password)
    {
        using var db = bed.AnonymousDb();
        return bed.Service(db).LoginAsync(new LoginRequestModel { Email = email, Password = password });
    }

    private static async Task<T> AsProvisioning<T>(SecurityTestBed bed, Func<OrgUserProvisioningService, Task<T>> call)
    {
        // api/system/organizations is super-admin only, so its DbContext bypasses the tenant filter.
        await using var db = bed.Db(OrgA, bypassFilter: true);
        return await call(new OrgUserProvisioningService(db, new PasswordHasher<UserAccount>(), bed.Email.Object));
    }

    // ── Set password ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Super_admin_sets_another_organizations_users_password_and_they_can_sign_in_with_it()
    {
        var bed = new SecurityTestBed();

        await bed.As(SuperAdmin, s => s.SetPasswordAsSuperAdminAsync(UserBId, NewPassword, SuperAdmin));

        var result = await Login(bed, "bruno@b.test", NewPassword);
        result.AccessToken.Should().NotBeNullOrEmpty();
        await FluentActions.Awaiting(() => Login(bed, "bruno@b.test", Password)).Should().ThrowAsync<UnauthorizedException>();
    }

    [Fact]
    public async Task Setting_the_password_completes_a_pending_invite_and_voids_the_old_link()
    {
        var bed = new SecurityTestBed();
        MakeInvitePending(bed, UserBId);

        await bed.As(SuperAdmin, s => s.SetPasswordAsSuperAdminAsync(UserBId, NewPassword, SuperAdmin));

        var u = bed.User(UserBId);
        u.IsActive.Should().BeTrue();
        u.InviteToken.Should().BeNull();
        u.InviteTokenExpiresAt.Should().BeNull();
        await FluentActions.Awaiting(() => bed.As(SuperAdmin, s => s.AcceptInviteAsync("old-invite-token", "Other#Pass99")))
            .Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Setting_the_password_clears_a_lockout_and_ends_the_users_sessions()
    {
        var bed = new SecurityTestBed();
        bed.Write(db =>
        {
            var u = db.UserAccounts.IgnoreQueryFilters().Single(x => x.UserID == UserBId);
            u.FailedLoginAttempts = 4;
            u.LockedUntil = DateTime.UtcNow.AddMinutes(20);
            db.UserSessions.Add(new UserSession { Id = Guid.NewGuid(), UserID = UserBId, TokenHash = "h", ExpiresAt = DateTime.UtcNow.AddDays(1), CreatedAt = DateTime.UtcNow, OrganizationId = OrgB });
        });

        await bed.As(SuperAdmin, s => s.SetPasswordAsSuperAdminAsync(UserBId, NewPassword, SuperAdmin));

        var user = bed.User(UserBId);
        user.LockedUntil.Should().BeNull();
        user.FailedLoginAttempts.Should().Be(0);
        bed.Read(db => db.UserSessions.IgnoreQueryFilters().Where(s => s.UserID == UserBId).All(s => s.RevokedAt != null)).Should().BeTrue();
    }

    [Fact]
    public async Task An_org_admin_cannot_set_a_password_even_in_their_own_organization()
    {
        var bed = new SecurityTestBed();

        await FluentActions.Awaiting(() => bed.As(OrgAdminA, s => s.SetPasswordAsSuperAdminAsync(RequesterAId, NewPassword, OrgAdminA)))
            .Should().ThrowAsync<ForbiddenException>();
        new PasswordHasher<UserAccount>().VerifyHashedPassword(bed.User(RequesterAId), bed.User(RequesterAId).Password, Password)
            .Should().NotBe(PasswordVerificationResult.Failed);
    }

    [Fact]
    public async Task A_super_admin_cannot_set_their_own_password_this_way()
    {
        var bed = new SecurityTestBed();

        await FluentActions.Awaiting(() => bed.As(SuperAdmin, s => s.SetPasswordAsSuperAdminAsync(SuperAdminId, NewPassword, SuperAdmin)))
            .Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task An_unknown_user_is_not_found()
    {
        var bed = new SecurityTestBed();

        await FluentActions.Awaiting(() => bed.As(SuperAdmin, s => s.SetPasswordAsSuperAdminAsync(99999, NewPassword, SuperAdmin)))
            .Should().ThrowAsync<NotFoundException>();
    }

    // ── Resend invite ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Resending_issues_a_new_link_and_the_old_one_stops_working()
    {
        var bed = new SecurityTestBed();
        MakeInvitePending(bed, UserBId);

        var summary = await AsProvisioning(bed, p => p.ReinviteUserAsync(OrgB, UserBId, null, "Org B"));

        summary.InvitePending.Should().BeTrue();
        var u = bed.User(UserBId);
        u.InviteToken.Should().NotBeNullOrEmpty().And.NotBe("old-invite-token");
        u.InviteTokenExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddDays(7), TimeSpan.FromMinutes(1));
        await FluentActions.Awaiting(() => bed.As(SuperAdmin, s => s.AcceptInviteAsync("old-invite-token", "Other#Pass99")))
            .Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Resending_to_a_corrected_address_changes_the_email_first()
    {
        var bed = new SecurityTestBed();
        MakeInvitePending(bed, UserBId);

        var summary = await AsProvisioning(bed, p => p.ReinviteUserAsync(OrgB, UserBId, "  Bruno.Real@B.Test ", "Org B"));

        summary.Email.Should().Be("bruno.real@b.test");
        bed.User(UserBId).Email.Should().Be("bruno.real@b.test");
    }

    [Fact]
    public async Task Resending_is_refused_once_the_account_is_set_up()
    {
        var bed = new SecurityTestBed(); // seeded users are active with a password: set up

        await FluentActions.Awaiting(() => AsProvisioning(bed, p => p.ReinviteUserAsync(OrgB, UserBId, "attacker@evil.test", "Org B")))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*already set up*");
        bed.User(UserBId).Email.Should().Be("bruno@b.test");
        bed.User(UserBId).InviteToken.Should().BeNull();
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("olivia.admin@a.test")] // someone else's address
    public async Task Resending_refuses_an_invalid_or_taken_address(string email)
    {
        var bed = new SecurityTestBed();
        MakeInvitePending(bed, UserBId);

        await FluentActions.Awaiting(() => AsProvisioning(bed, p => p.ReinviteUserAsync(OrgB, UserBId, email, "Org B")))
            .Should().ThrowAsync<BadRequestException>();
        bed.User(UserBId).InviteToken.Should().Be("old-invite-token");
    }

    [Fact]
    public async Task Resending_for_a_user_of_another_organization_is_refused()
    {
        var bed = new SecurityTestBed();
        MakeInvitePending(bed, UserBId);

        await FluentActions.Awaiting(() => AsProvisioning(bed, p => p.ReinviteUserAsync(OrgA, UserBId, null, "Org A")))
            .Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task The_org_users_list_shows_a_pending_invite()
    {
        var bed = new SecurityTestBed();
        MakeInvitePending(bed, UserBId);

        var users = await AsProvisioning(bed, p => p.GetOrgUsersAsync(OrgB));

        users.Single(u => u.UserId == UserBId).InvitePending.Should().BeTrue();
    }
}
