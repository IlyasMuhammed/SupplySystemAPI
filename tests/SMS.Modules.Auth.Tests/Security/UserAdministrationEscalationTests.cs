using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Auth.Domain;
using SMS.Modules.Auth.Models;
using SMS.Modules.Auth.Repositories;
using SMS.Shared.Authorization;
using SMS.Shared.Exceptions;
using Xunit;
using static SMS.Modules.Auth.Tests.Security.SecurityTestBed;

namespace SMS.Modules.Auth.Tests.Security;

/// <summary>
/// <c>api/users</c> — the user screens the frontend really uses, behind USER_MANAGE, which every Org Admin
/// holds. Until 2026-10-02 the role a user was given was never checked: an Org Admin could give themselves
/// (or a new account) the global System Admin role, which carries PLATFORM_SUPER_ADMIN, and from there manage
/// every organization; or another organization's custom role they cannot even see. Org Admins must still be
/// able to hand out every ordinary role — Supply Department Administrator included, though they do not hold
/// its SALE_ORDER_CONFIG_WRITE themselves.
/// </summary>
public class UserAdministrationEscalationTests
{
    private static CreateUserRequest NewUser(int roleId, string email = "new.hire@a.test") =>
        new() { FirstName = "Newt", LastName = "Hire", Email = email, RoleID = roleId, SupplierType = "INTERNAL" };

    private static bool Exists(SecurityTestBed bed, string email) =>
        bed.Read(db => db.UserAccounts.IgnoreQueryFilters().Any(u => u.Email == email));

    // ── which role may be handed out ───────────────────────────────────────────

    [Fact]
    public async Task An_org_admin_cannot_give_anyone_the_system_admin_role_with_its_platform_permissions()
    {
        var bed = new SecurityTestBed();

        var assign = () => bed.As(OrgAdminA, s => s.AssignRoleAsync(RequesterAId, SystemAdminRole, OrgAdminA));
        var create = () => bed.As(OrgAdminA, s => s.AdminCreateUserAsync(NewUser(SystemAdminRole), OrgAdminA));

        await assign.Should().ThrowAsync<ForbiddenException>();
        await create.Should().ThrowAsync<ForbiddenException>();
        bed.User(RequesterAId).RoleID.Should().Be(RequesterRole);
        Exists(bed, "new.hire@a.test").Should().BeFalse();
    }

    [Fact]
    public async Task An_org_admin_cannot_give_anyone_another_organizations_role()
    {
        var bed = new SecurityTestBed();

        var assign = () => bed.As(OrgAdminA, s => s.AssignRoleAsync(RequesterAId, OrgBPowerRole, OrgAdminA));
        var create = () => bed.As(OrgAdminA, s => s.AdminCreateUserAsync(NewUser(OrgBPowerRole), OrgAdminA));
        var missing = () => bed.As(OrgAdminA, s => s.AssignRoleAsync(RequesterAId, 999, OrgAdminA));

        await assign.Should().ThrowAsync<BadRequestException>();
        await create.Should().ThrowAsync<BadRequestException>();
        await missing.Should().ThrowAsync<BadRequestException>();
        bed.User(RequesterAId).RoleID.Should().Be(RequesterRole);
    }

    [Fact]
    public async Task An_org_admin_can_still_give_out_ordinary_roles_their_own_custom_ones_and_supply_department_administrator()
    {
        var bed = new SecurityTestBed();

        await bed.As(OrgAdminA, s => s.AssignRoleAsync(RequesterAId, SupplyDeptAdminRole, OrgAdminA));
        bed.User(RequesterAId).RoleID.Should().Be(SupplyDeptAdminRole);

        await bed.As(OrgAdminA, s => s.AssignRoleAsync(RequesterAId, OrgACustomRole, OrgAdminA));
        bed.User(RequesterAId).RoleID.Should().Be(OrgACustomRole);

        await bed.As(OrgAdminA, s => s.AdminCreateUserAsync(NewUser(RequesterRole), OrgAdminA));
        Exists(bed, "new.hire@a.test").Should().BeTrue();
    }

    [Fact]
    public async Task An_org_admin_cannot_appoint_another_org_admin_only_a_super_admin_can()
    {
        var bed = new SecurityTestBed();

        var create = () => bed.As(OrgAdminA, s => s.AdminCreateUserAsync(NewUser(OrgAdminRole), OrgAdminA));
        await create.Should().ThrowAsync<ForbiddenException>();
        Exists(bed, "new.hire@a.test").Should().BeFalse();

        var promote = () => bed.As(OrgAdminA, s => s.AssignRoleAsync(RequesterAId, OrgAdminRole, OrgAdminA));
        await promote.Should().ThrowAsync<ForbiddenException>();
        bed.User(RequesterAId).RoleID.Should().Be(RequesterRole);

        await bed.As(SuperAdmin, s => s.AssignRoleAsync(RequesterAId, OrgAdminRole, SuperAdmin));
        bed.User(RequesterAId).RoleID.Should().Be(OrgAdminRole);
    }

    [Fact]
    public async Task The_role_picker_offers_an_org_admin_neither_system_admin_nor_organization_admin()
    {
        var bed = new SecurityTestBed();

        var forOrgAdmin = await bed.As(OrgAdminA, s => s.GetAssignableRolesAsync(OrgAdminA));
        forOrgAdmin.Select(r => r.RoleId).Should().NotContain(new[] { SystemAdminRole, OrgAdminRole })
            .And.Contain(new[] { RequesterRole, SupplyDeptAdminRole, OrgACustomRole })
            .And.NotContain(OrgBPowerRole); // another organization's role is not even visible

        var forSuperAdmin = await bed.As(SuperAdmin, s => s.GetAssignableRolesAsync(SuperAdmin));
        forSuperAdmin.Select(r => r.RoleId).Should().Contain(new[] { SystemAdminRole, OrgAdminRole });
    }

    [Fact]
    public async Task A_super_admin_can_give_out_the_system_admin_role()
    {
        var bed = new SecurityTestBed();

        await bed.As(SuperAdmin, s => s.AssignRoleAsync(RequesterAId, SystemAdminRole, SuperAdmin));

        bed.User(RequesterAId).RoleID.Should().Be(SystemAdminRole);
    }

    [Fact]
    public async Task A_custom_role_holding_a_platform_permission_cannot_be_given_out_either()
    {
        var bed = new SecurityTestBed();
        // Org A's own custom role, but it carries PLATFORM_SUPER_ADMIN (as ORG-PSO's hand-made roles reportedly do).
        bed.Write(db => db.RolePermissions.Add(new RolePermission { RoleID = OrgACustomRole, PermissionID = PlatformSuperAdmin, IsAllowed = true }));

        var assign = () => bed.As(OrgAdminA, s => s.AssignRoleAsync(RequesterAId, OrgACustomRole, OrgAdminA));

        await assign.Should().ThrowAsync<ForbiddenException>();
    }

    // ── who may be changed ─────────────────────────────────────────────────────

    [Fact]
    public async Task Nobody_but_a_super_admin_changes_their_own_role()
    {
        var bed = new SecurityTestBed();

        var self = () => bed.As(OrgAdminA, s => s.AssignRoleAsync(OrgAdminAId, SupplyDeptAdminRole, OrgAdminA));

        await self.Should().ThrowAsync<ForbiddenException>();
        bed.User(OrgAdminAId).RoleID.Should().Be(OrgAdminRole);
    }

    [Fact]
    public async Task An_org_admin_cannot_change_delete_reset_or_re_role_a_super_admin()
    {
        var bed = new SecurityTestBed();
        var before = bed.User(SuperAdminId);

        var role = () => bed.As(OrgAdminA, s => s.AssignRoleAsync(SuperAdminId, RequesterRole, OrgAdminA));
        var patch = () => bed.As(OrgAdminA, s => s.PatchUserAsync(SuperAdminId, new PatchUserRequest { IsActive = false }, OrgAdminA));
        var rename = () => bed.As(OrgAdminA, s => s.PatchUserAsync(SuperAdminId, new PatchUserRequest { FirstName = "Mallory" }, OrgAdminA));
        var delete = () => bed.As(OrgAdminA, s => s.SoftDeleteUserAsync(SuperAdminId, OrgAdminA));
        var reset = () => bed.As(OrgAdminA, s => s.AdminResetPasswordAsync(SuperAdminId, OrgAdminA));

        await role.Should().ThrowAsync<ForbiddenException>();
        await patch.Should().ThrowAsync<ForbiddenException>();
        await rename.Should().ThrowAsync<ForbiddenException>();
        await delete.Should().ThrowAsync<ForbiddenException>();
        await reset.Should().ThrowAsync<ForbiddenException>();
        bed.User(SuperAdminId).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task Nobody_deletes_or_deactivates_themselves_or_widens_their_own_supplier_access()
    {
        var bed = new SecurityTestBed();

        var delete = () => bed.As(OrgAdminA, s => s.SoftDeleteUserAsync(OrgAdminAId, OrgAdminA));
        var deactivate = () => bed.As(OrgAdminA, s => s.PatchUserAsync(OrgAdminAId, new PatchUserRequest { IsActive = false }, OrgAdminA));
        var supplierType = () => bed.As(OrgAdminA, s => s.PatchUserAsync(OrgAdminAId, new PatchUserRequest { SupplierType = "EXTERNAL" }, OrgAdminA));
        var suppliers = () => bed.As(OrgAdminA, s => s.PatchUserAsync(OrgAdminAId, new PatchUserRequest { SupplierIds = [Guid.NewGuid()] }, OrgAdminA));
        var superDelete = () => bed.As(SuperAdmin, s => s.SoftDeleteUserAsync(SuperAdminId, SuperAdmin));

        await delete.Should().ThrowAsync<ForbiddenException>();
        await deactivate.Should().ThrowAsync<ForbiddenException>();
        await supplierType.Should().ThrowAsync<ForbiddenException>();
        await suppliers.Should().ThrowAsync<ForbiddenException>();
        await superDelete.Should().ThrowAsync<ForbiddenException>("a super admin deleting themselves can leave the platform with nobody to run it");
        bed.User(OrgAdminAId).Should().Match<UserAccount>(u => u.IsActive && !u.IsDelete && u.SupplierType == "INTERNAL");
    }

    [Fact]
    public async Task Editing_your_own_name_and_running_another_users_account_still_work()
    {
        var bed = new SecurityTestBed();

        await bed.As(OrgAdminA, s => s.PatchUserAsync(OrgAdminAId, new PatchUserRequest { FirstName = "Liv", Department = "Ops" }, OrgAdminA));
        await bed.As(OrgAdminA, s => s.PatchUserAsync(OtherAdminAId, new PatchUserRequest { IsActive = false }, OrgAdminA));
        await bed.As(OrgAdminA, s => s.AdminResetPasswordAsync(RequesterAId, OrgAdminA));
        await bed.As(OrgAdminA, s => s.SoftDeleteUserAsync(RequesterAId, OrgAdminA));

        bed.User(OrgAdminAId).FirstName.Should().Be("Liv");
        bed.User(OtherAdminAId).IsActive.Should().BeFalse();
        bed.User(RequesterAId).IsDelete.Should().BeTrue();
    }

    [Fact]
    public async Task Another_organizations_user_is_not_found_even_with_the_filter_bypassed()
    {
        var bed = new SecurityTestBed();

        await using var bypassed = bed.Db(OrgA, bypassFilter: true);
        var svc = bed.Service(bypassed);

        await ((Func<Task>)(() => svc.AssignRoleAsync(UserBId, RequesterRole, OrgAdminA))).Should().ThrowAsync<NotFoundException>();
        await ((Func<Task>)(() => svc.PatchUserAsync(UserBId, new PatchUserRequest { IsActive = false }, OrgAdminA))).Should().ThrowAsync<NotFoundException>();
        await ((Func<Task>)(() => svc.SoftDeleteUserAsync(UserBId, OrgAdminA))).Should().ThrowAsync<NotFoundException>();
        await ((Func<Task>)(() => svc.AdminResetPasswordAsync(UserBId, OrgAdminA))).Should().ThrowAsync<NotFoundException>();
        bed.User(UserBId).Should().Match<UserAccount>(u => u.IsActive && !u.IsDelete && u.RoleID == RequesterRole);
    }

    // ── roles: no path puts a platform permission on an organization's role ────

    [Fact]
    public async Task A_new_custom_role_never_inherits_grants_left_behind_under_its_id()
    {
        // RoleID has no identity; a new role takes MAX(RoleID)+1, and RolePermissions has no foreign key to
        // Roles. Rows left behind by a role deleted by hand would silently become the new role's grants.
        var bed = new SecurityTestBed();
        var nextId = OrgBPowerRole + 1;
        bed.Write(db => db.RolePermissions.Add(new RolePermission { RoleID = nextId, PermissionID = PlatformSuperAdmin, IsAllowed = true }));

        await using var db = bed.Db(OrgA, bypassFilter: false);
        var repo = new AuthRepository(db, new PasswordHasher<UserAccount>());
        var created = await repo.CreateRoleAsync(new CreateRoleRequest { Name = "Fresh Role", RoleCode = "FRESH_ROLE" });

        created.RoleId.Should().Be(nextId);
        var detail = await repo.GetRoleDetailAsync(created.RoleId);
        // A35 D-16: a new role starts with only the every-role currency reads.
        detail!.PermissionGroups.SelectMany(g => g.Permissions).Where(p => p.IsAllowed).Select(p => p.Code)
            .Should().BeSubsetOf(SMS.Modules.Auth.Data.AuthDataSeeder.EveryRoleCodes);
    }

    [Theory]
    [InlineData(SystemConfigure)]
    [InlineData(PlatformSuperAdmin)]
    public async Task Replacing_an_organizations_role_permissions_cannot_add_a_platform_permission(int platformPermission)
    {
        var bed = new SecurityTestBed();

        await using var db = bed.Db(OrgA, bypassFilter: false);
        var repo = new AuthRepository(db, new PasswordHasher<UserAccount>());
        var act = () => repo.ReplaceRolePermissionsAsync(OrgACustomRole, [PoView, platformPermission]);

        await act.Should().ThrowAsync<ForbiddenException>();
        bed.Read(d => d.RolePermissions.Any(p => p.RoleID == OrgACustomRole && p.PermissionID == platformPermission && p.IsAllowed)).Should().BeFalse();
    }

    [Fact]
    public void Creating_or_renaming_a_role_carries_no_permissions_at_all()
    {
        // The only doors to a role's permissions are api/roles/{id}/permissions and the legacy
        // api/auth/roles/{id}/permissions, both guarded. If create or update ever grows a permission list,
        // it needs the same guard — this fails first.
        static IEnumerable<string> PermissionLike(Type t) => t.GetProperties()
            .Where(p => p.Name.Contains("Permission", StringComparison.OrdinalIgnoreCase)
                     || (p.PropertyType != typeof(string) && p.PropertyType != typeof(bool)))
            .Select(p => p.Name);

        PermissionLike(typeof(CreateRoleRequest)).Should().BeEmpty();
        PermissionLike(typeof(UpdateRoleRequest)).Should().BeEmpty();
    }
}
