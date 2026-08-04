using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Auth.Data;
using SMS.Modules.Auth.Domain;
using SMS.Modules.Auth.Models;
using SMS.Modules.Auth.Repositories;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Auth.Tests;

// Covers "Org Admin can CRUD roles" (role catalog CRUD opened up beyond Super Admin): Role is now
// either global (IsGlobal=true, OrganizationId=null — the shared catalog every org can assign from,
// e.g. Procurement Manager) or org-owned (IsGlobal=false, OrganizationId set — created by that
// org's own Org Admin). An Org Admin must get full CRUD on their own org's custom roles, but stay
// completely unable to touch the shared catalog or another org's custom roles.
public class RoleCrudScopingTests
{
    private static AuthRepository Build(string dbName, Guid organizationId, bool isSuperAdmin = false)
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(dbName).Options;
        var db = new AuthDbContext(options, new StaticTenantContext { OrganizationId = organizationId, IsSuperAdmin = isSuperAdmin });
        return new AuthRepository(db, new PasswordHasher<UserAccount>());
    }

    [Fact]
    public async Task CreateRoleAsync_ByOrgAdmin_IsPrivateToTheirOwnOrg()
    {
        var dbName = Guid.NewGuid().ToString();
        var orgA = Guid.NewGuid();
        var repo = Build(dbName, orgA);

        var created = await repo.CreateRoleAsync(new CreateRoleRequest { Name = "Custom Approver", RoleCode = "CUSTOM_APPROVER" });

        created.IsGlobal.Should().BeFalse();

        var detail = await repo.GetRoleDetailAsync(created.RoleId);
        detail.Should().NotBeNull();
        detail!.IsGlobal.Should().BeFalse();
    }

    [Fact]
    public async Task CreateRoleAsync_BySuperAdmin_JoinsTheGlobalCatalog()
    {
        var dbName = Guid.NewGuid().ToString();
        var repo = Build(dbName, Guid.NewGuid(), isSuperAdmin: true);

        var created = await repo.CreateRoleAsync(new CreateRoleRequest { Name = "New Catalog Role", RoleCode = "NEW_CATALOG_ROLE" });

        created.IsGlobal.Should().BeTrue();
    }

    [Fact]
    public async Task OrgAdmin_CanFullyManageTheirOwnCustomRole()
    {
        var dbName = Guid.NewGuid().ToString();
        var orgA = Guid.NewGuid();
        var repo = Build(dbName, orgA);

        var created = await repo.CreateRoleAsync(new CreateRoleRequest { Name = "Custom Role", RoleCode = "CUSTOM_ROLE" });

        (await repo.UpdateRoleAsync(created.RoleId, new UpdateRoleRequest { Name = "Renamed", IsActive = true })).Should().BeTrue();
        (await repo.ReplaceRolePermissionsAsync(created.RoleId, [])).Should().BeTrue();
        (await repo.DeactivateRoleAsync(created.RoleId)).Should().BeTrue();
    }

    [Fact]
    public async Task OrgAdmin_CannotModifyAGlobalRole()
    {
        var dbName = Guid.NewGuid().ToString();
        var orgA = Guid.NewGuid();

        // Seed a global role as a Super Admin first (matches how the built-in catalog is seeded).
        var superAdminRepo = Build(dbName, Guid.NewGuid(), isSuperAdmin: true);
        var globalRole = await superAdminRepo.CreateRoleAsync(new CreateRoleRequest { Name = "Procurement Manager", RoleCode = "PROC_MGR" });
        globalRole.IsGlobal.Should().BeTrue();

        var orgAdminRepo = Build(dbName, orgA);

        var updateAct = () => orgAdminRepo.UpdateRoleAsync(globalRole.RoleId, new UpdateRoleRequest { Name = "Hijacked", IsActive = true });
        var replaceAct = () => orgAdminRepo.ReplaceRolePermissionsAsync(globalRole.RoleId, []);
        var deactivateAct = () => orgAdminRepo.DeactivateRoleAsync(globalRole.RoleId);

        await updateAct.Should().ThrowAsync<ForbiddenException>();
        await replaceAct.Should().ThrowAsync<ForbiddenException>();
        await deactivateAct.Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task OrgAdmin_CannotSeeOrTouchAnotherOrgsCustomRole()
    {
        var dbName = Guid.NewGuid().ToString();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();

        var orgARepo = Build(dbName, orgA);
        var orgBsRole = await Build(dbName, orgB).CreateRoleAsync(new CreateRoleRequest { Name = "Org B Only", RoleCode = "ORG_B_ONLY" });

        (await orgARepo.GetRoleDetailAsync(orgBsRole.RoleId)).Should().BeNull();
        (await orgARepo.UpdateRoleAsync(orgBsRole.RoleId, new UpdateRoleRequest { Name = "Stolen", IsActive = true })).Should().BeFalse();
    }

    [Fact]
    public async Task GetRolesAsync_ForAnOrgAdmin_ReturnsGlobalRolesPlusOnlyTheirOwnCustomRoles()
    {
        var dbName = Guid.NewGuid().ToString();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();

        await Build(dbName, Guid.NewGuid(), isSuperAdmin: true)
            .CreateRoleAsync(new CreateRoleRequest { Name = "Global Role", RoleCode = "GLOBAL_ROLE" });
        await Build(dbName, orgA).CreateRoleAsync(new CreateRoleRequest { Name = "Org A Role", RoleCode = "ORG_A_ROLE" });
        await Build(dbName, orgB).CreateRoleAsync(new CreateRoleRequest { Name = "Org B Role", RoleCode = "ORG_B_ROLE" });

        var visibleToOrgA = await Build(dbName, orgA).GetRolesAsync();

        visibleToOrgA.Select(r => r.RoleCode).Should().Contain(["GLOBAL_ROLE", "ORG_A_ROLE"]);
        visibleToOrgA.Select(r => r.RoleCode).Should().NotContain("ORG_B_ROLE");
    }

    // Regression for a real privilege-escalation gap: GuardNotGlobalUnlessSuperAdmin only checks
    // whether the TARGET ROLE is global, not which PERMISSION CODES are being granted onto it — an
    // Org Admin's own custom role passes that check freely. Without this guard, an Org Admin could
    // create a private role, grant it SYSTEM_CONFIGURE (global reference data — Countries/Cities/
    // Currencies/Lookup Types — shared across every organization), assign it to one of their org's
    // users, and hand that user edit access to every other org's shared data.
    [Fact]
    public async Task OrgAdmin_CannotGrantSystemConfigureToTheirOwnCustomRole()
    {
        var dbName = Guid.NewGuid().ToString();
        var orgA = Guid.NewGuid();

        var options = new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(dbName).Options;
        var db = new AuthDbContext(options, new StaticTenantContext { OrganizationId = orgA, IsSuperAdmin = false });
        db.Permissions.AddRange(
            new Permission { Name = "Configure System", Code = "SYSTEM_CONFIGURE" },
            new Permission { Name = "Manage Users", Code = "USER_MANAGE" });
        await db.SaveChangesAsync();

        var systemConfigureId = db.Permissions.Single(p => p.Code == "SYSTEM_CONFIGURE").PermissionID;
        var userManageId      = db.Permissions.Single(p => p.Code == "USER_MANAGE").PermissionID;

        var repo = new AuthRepository(db, new PasswordHasher<UserAccount>());
        var created = await repo.CreateRoleAsync(new CreateRoleRequest { Name = "Sneaky Role", RoleCode = "SNEAKY_ROLE" });
        created.IsGlobal.Should().BeFalse();

        var act = () => repo.ReplaceRolePermissionsAsync(created.RoleId, [systemConfigureId]);
        await act.Should().ThrowAsync<ForbiddenException>();

        // A benign permission is still freely grantable on their own custom role.
        (await repo.ReplaceRolePermissionsAsync(created.RoleId, [userManageId])).Should().BeTrue();
    }

    [Fact]
    public async Task SuperAdmin_CanStillGrantSystemConfigureToARole()
    {
        var dbName = Guid.NewGuid().ToString();

        var options = new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(dbName).Options;
        var db = new AuthDbContext(options, new StaticTenantContext { IsSuperAdmin = true });
        db.Permissions.Add(new Permission { Name = "Configure System", Code = "SYSTEM_CONFIGURE" });
        await db.SaveChangesAsync();
        var systemConfigureId = db.Permissions.Single().PermissionID;

        var repo = new AuthRepository(db, new PasswordHasher<UserAccount>());
        var created = await repo.CreateRoleAsync(new CreateRoleRequest { Name = "Global Admin Role", RoleCode = "GLOBAL_ADMIN_ROLE" });

        (await repo.ReplaceRolePermissionsAsync(created.RoleId, [systemConfigureId])).Should().BeTrue();
    }
}
