using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Auth.Domain;
using SMS.Modules.Auth.Models;
using SMS.Shared.Authorization;
using SMS.Shared.Exceptions;
using Xunit;
using static SMS.Modules.Auth.Tests.Security.SecurityTestBed;

namespace SMS.Modules.Auth.Tests.Security;

/// <summary>
/// The legacy user/role administration on <c>api/auth</c> (the frontend uses api/users and api/roles). Beyond
/// the permission on each action, the service holds the line on who may be changed and what may be handed out:
/// nobody but a super admin grants a permission they do not hold, edits themselves, reaches another
/// organization, or touches a super admin. Each call runs the way production runs it — a DbContext scoped by
/// the caller's token — and, where it matters, with the tenant filter bypassed too, because a super admin, a
/// background job and an anonymous request all bypass it: the explicit checks must hold on their own.
/// </summary>
public class LegacyAuthAdministrationTests
{
    private static List<PermissionModel> Grant(params int[] permissionIds) =>
        permissionIds.Select(id => new PermissionModel { PermissionID = id, IsAllowed = true }).ToList();

    private static bool HasOverride(SecurityTestBed bed, int userId, int permissionId) =>
        bed.Read(db => db.UserPermissions.IgnoreQueryFilters().Any(p => p.UserID == userId && p.PermissionID == permissionId && p.IsAllowed));

    private static bool RoleAllows(SecurityTestBed bed, int roleId, int permissionId) =>
        bed.Read(db => db.RolePermissions.Any(p => p.RoleID == roleId && p.PermissionID == permissionId && p.IsAllowed));

    // ── user permission overrides ──────────────────────────────────────────────

    [Fact]
    public async Task A_user_cannot_grant_themselves_anything()
    {
        var bed = new SecurityTestBed();
        var requester = Caller(RequesterAId, OrgA, false, PermissionCodes.PO_VIEW, PermissionCodes.USER_MANAGE);

        var act = () => bed.As(requester, s => s.SaveUserPermissionsAsync(RequesterAId, Grant(PoView), requester));

        await act.Should().ThrowAsync<ForbiddenException>("not even a permission they already hold — nobody edits their own overrides");
        HasOverride(bed, RequesterAId, PoView).Should().BeFalse();
    }

    [Fact]
    public async Task An_admin_cannot_grant_a_permission_they_do_not_hold()
    {
        var bed = new SecurityTestBed();

        var act = () => bed.As(OrgAdminA, s => s.SaveUserPermissionsAsync(RequesterAId, Grant(SaleOrderConfigWrite), OrgAdminA));

        await act.Should().ThrowAsync<ForbiddenException>();
        HasOverride(bed, RequesterAId, SaleOrderConfigWrite).Should().BeFalse();
    }

    [Fact]
    public async Task An_admin_can_grant_a_permission_they_hold_to_someone_in_their_organization()
    {
        var bed = new SecurityTestBed();

        await bed.As(OrgAdminA, s => s.SaveUserPermissionsAsync(RequesterAId, Grant(PoApprove), OrgAdminA));

        HasOverride(bed, RequesterAId, PoApprove).Should().BeTrue();
        bed.Read(db => db.UserPermissions.IgnoreQueryFilters().Single(p => p.UserID == RequesterAId).OrganizationId).Should().Be(OrgA);
    }

    [Theory]
    [InlineData(SystemConfigure)]
    [InlineData(PlatformSuperAdmin)]
    public async Task Platform_permissions_cannot_be_granted_by_anyone_but_a_super_admin_even_one_who_holds_them(int platformPermission)
    {
        var bed = new SecurityTestBed();
        // A System Admin who is not in SuperAdminUsers: the role carries both codes, the person is not the platform.
        var systemAdmin = Caller(OtherAdminAId, OrgA, false,
            PermissionCodes.USER_MANAGE, PermissionCodes.SYSTEM_CONFIGURE, PermissionCodes.PLATFORM_SUPER_ADMIN);

        var act = () => bed.As(systemAdmin, s => s.SaveUserPermissionsAsync(RequesterAId, Grant(platformPermission), systemAdmin));

        await act.Should().ThrowAsync<ForbiddenException>();
        HasOverride(bed, RequesterAId, platformPermission).Should().BeFalse();
    }

    [Fact]
    public async Task An_admin_cannot_reach_a_user_in_another_organization_even_when_the_tenant_filter_is_bypassed()
    {
        var bed = new SecurityTestBed();

        // Scoped exactly as the token scopes it: the filter hides org B's user.
        var scoped = () => bed.As(OrgAdminA, s => s.SaveUserPermissionsAsync(UserBId, Grant(PoView), OrgAdminA));
        await scoped.Should().ThrowAsync<NotFoundException>();

        // Filter bypassed (a super-admin-shaped context): the explicit organization check must hold by itself.
        await using (var bypassed = bed.Db(OrgA, bypassFilter: true))
        {
            var act = () => bed.Service(bypassed).SaveUserPermissionsAsync(UserBId, Grant(PoView), OrgAdminA);
            await act.Should().ThrowAsync<NotFoundException>();
        }

        HasOverride(bed, UserBId, PoView).Should().BeFalse();
    }

    [Fact]
    public async Task An_admin_cannot_touch_a_super_admin_but_a_super_admin_can_and_the_row_lands_in_the_targets_organization()
    {
        var bed = new SecurityTestBed();

        var act = () => bed.As(OrgAdminA, s => s.SaveUserPermissionsAsync(SuperAdminId, Grant(PoView), OrgAdminA));
        await act.Should().ThrowAsync<ForbiddenException>();

        // A super admin working from org A on org B's user: the override belongs to org B.
        await bed.As(SuperAdmin, s => s.SaveUserPermissionsAsync(UserBId, Grant(PoApprove), SuperAdmin));
        bed.Read(db => db.UserPermissions.IgnoreQueryFilters().Single(p => p.UserID == UserBId).OrganizationId).Should().Be(OrgB);
    }

    [Fact]
    public async Task Reading_another_organizations_user_permissions_is_not_found()
    {
        var bed = new SecurityTestBed();

        await using var bypassed = bed.Db(OrgA, bypassFilter: true);
        var act = () => bed.Service(bypassed).GetUserPermissionsAsync(UserBId, OrgAdminA);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    // ── role permissions ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_global_role_shared_by_every_organization_can_be_changed_only_by_a_super_admin()
    {
        var bed = new SecurityTestBed();
        var platformHolder = Caller(OtherAdminAId, OrgA, false, PermissionCodes.PLATFORM_SUPER_ADMIN, PermissionCodes.USER_MANAGE,
            PermissionCodes.PO_VIEW, PermissionCodes.PO_APPROVE);

        var act = () => bed.As(platformHolder, s => s.SaveRolePermissionsAsync(RequesterRole, Grant(PoApprove), platformHolder));
        await act.Should().ThrowAsync<ForbiddenException>("holding the PLATFORM_SUPER_ADMIN code is not being the platform super admin");
        RoleAllows(bed, RequesterRole, PoApprove).Should().BeFalse();

        await bed.As(SuperAdmin, s => s.SaveRolePermissionsAsync(RequesterRole, Grant(PoApprove), SuperAdmin));
        RoleAllows(bed, RequesterRole, PoApprove).Should().BeTrue();
    }

    [Fact]
    public async Task An_organizations_own_role_cannot_be_given_a_permission_the_editor_does_not_hold_or_a_platform_one()
    {
        var bed = new SecurityTestBed();
        var editor = Caller(OtherAdminAId, OrgA, false, PermissionCodes.PLATFORM_SUPER_ADMIN, PermissionCodes.USER_MANAGE, PermissionCodes.PO_VIEW);

        var unheld = () => bed.As(editor, s => s.SaveRolePermissionsAsync(OrgACustomRole, Grant(PoApprove), editor));
        var platform = () => bed.As(editor, s => s.SaveRolePermissionsAsync(OrgACustomRole, Grant(PlatformSuperAdmin), editor));

        await unheld.Should().ThrowAsync<ForbiddenException>();
        await platform.Should().ThrowAsync<ForbiddenException>();
        RoleAllows(bed, OrgACustomRole, PoApprove).Should().BeFalse();
        RoleAllows(bed, OrgACustomRole, PlatformSuperAdmin).Should().BeFalse();
    }

    [Fact]
    public async Task Another_organizations_role_cannot_be_read_or_changed()
    {
        var bed = new SecurityTestBed();
        var editor = Caller(OtherAdminAId, OrgA, false, PermissionCodes.PLATFORM_SUPER_ADMIN, PermissionCodes.USER_MANAGE, PermissionCodes.PO_VIEW);

        await using var bypassed = bed.Db(OrgA, bypassFilter: true);
        var read = () => bed.Service(bypassed).GetPermissionsByRoleAsync(OrgBPowerRole, editor);
        var write = () => bed.Service(bypassed).SaveRolePermissionsAsync(OrgBPowerRole, [new PermissionModel { PermissionID = PlatformSuperAdmin, IsAllowed = false }], editor);

        await read.Should().ThrowAsync<NotFoundException>();
        await write.Should().ThrowAsync<NotFoundException>();
        RoleAllows(bed, OrgBPowerRole, PlatformSuperAdmin).Should().BeTrue();
    }

    // ── user list and deactivation ─────────────────────────────────────────────

    [Fact]
    public async Task The_user_list_holds_only_the_callers_organization_even_with_the_filter_bypassed()
    {
        var bed = new SecurityTestBed();

        await using var bypassed = bed.Db(OrgA, bypassFilter: true);
        var page = await bed.Service(bypassed).GetAllUsersAsync(1, 50, OrgAdminA);

        page.Data.Select(u => u.UserID!.Value).Should().BeEquivalentTo(new[] { SuperAdminId, OrgAdminAId, RequesterAId, OtherAdminAId });
    }

    [Fact]
    public async Task A_super_admin_sees_every_organizations_users_and_a_page_is_capped_at_one_hundred()
    {
        var bed = new SecurityTestBed();

        var page = await bed.As(SuperAdmin, s => s.GetAllUsersAsync(1, 100_000, SuperAdmin));

        page.Data.Select(u => u.UserID!.Value).Should().Contain(UserBId);
        page.PageSize.Should().Be(100);
    }

    [Fact]
    public async Task Deactivating_ends_the_users_sessions_and_stays_inside_the_organization()
    {
        var bed = new SecurityTestBed();
        bed.Write(db => db.UserSessions.Add(new UserSession
        {
            Id = Guid.NewGuid(), UserID = RequesterAId, TokenHash = "h", ExpiresAt = DateTime.UtcNow.AddDays(1), CreatedAt = DateTime.UtcNow, OrganizationId = OrgA
        }));

        await bed.As(OrgAdminA, s => s.DeactivateUserAsync(RequesterAId, OrgAdminA));

        bed.User(RequesterAId).IsActive.Should().BeFalse();
        bed.Read(db => db.UserSessions.Where(s => s.UserID == RequesterAId).All(s => s.RevokedAt != null)).Should().BeTrue();

        await using var bypassed = bed.Db(OrgA, bypassFilter: true);
        var otherOrg = () => bed.Service(bypassed).DeactivateUserAsync(UserBId, OrgAdminA);
        await otherOrg.Should().ThrowAsync<NotFoundException>();
        bed.User(UserBId).IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task Nobody_deactivates_themselves_and_only_a_super_admin_deactivates_a_super_admin()
    {
        var bed = new SecurityTestBed();

        var self = () => bed.As(OrgAdminA, s => s.DeactivateUserAsync(OrgAdminAId, OrgAdminA));
        var superAdmin = () => bed.As(OrgAdminA, s => s.DeactivateUserAsync(SuperAdminId, OrgAdminA));

        await self.Should().ThrowAsync<ForbiddenException>();
        await superAdmin.Should().ThrowAsync<ForbiddenException>();
        bed.User(OrgAdminAId).IsActive.Should().BeTrue();
        bed.User(SuperAdminId).IsActive.Should().BeTrue();
    }
}
