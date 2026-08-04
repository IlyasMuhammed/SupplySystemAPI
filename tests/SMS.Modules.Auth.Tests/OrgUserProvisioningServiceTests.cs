using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Auth.Data;
using SMS.Modules.Auth.Domain;
using SMS.Modules.Auth.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Auth.Tests;

// Covers the Super Admin "view / change organization admin" feature: Organization has no
// AdminUserId column, so "the org admin" is purely implicit (UserAccount.OrganizationId +
// RoleID == OrgAdmin) — these tests exercise that logic directly against OrgUserProvisioningService,
// the SMS.Shared.Common cross-module interface impl that OrganizationsController (Tenancy) calls into.
public class OrgUserProvisioningServiceTests
{
    private const int OrgAdminRoleId = (int)EnumRole.OrgAdmin;
    private const int RequesterRoleId = (int)EnumRole.Requester;

    private static (OrgUserProvisioningService Svc, AuthDbContext Db) Build(string dbName)
    {
        var options = new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(dbName).Options;
        // IsSuperAdmin=true: matches the real caller of this service (OrganizationsController is
        // PLATFORM_SUPER_ADMIN-gated), which is also what makes the tenant query filter irrelevant here.
        var db = new AuthDbContext(options, new StaticTenantContext { IsSuperAdmin = true });

        db.Roles.AddRange(
            new Role { RoleID = OrgAdminRoleId, Name = "Org Admin", RoleCode = "ORG_ADMIN", IsActive = true },
            new Role { RoleID = RequesterRoleId, Name = "Requester", RoleCode = "REQUESTER", IsActive = true }
        );
        db.SaveChanges();

        var hasher = new PasswordHasher<UserAccount>();
        var svc = new OrgUserProvisioningService(db, hasher, Mock.Of<IEmailService>());
        return (svc, db);
    }

    private static UserAccount NewUser(Guid orgId, int roleId, string email, bool isActive = true, bool isDelete = false) => new()
    {
        FirstName = "Test", LastName = "User", Email = email, Password = "x",
        RoleID = roleId, OrganizationId = orgId, IsActive = isActive, IsDelete = isDelete,
        CreatedDate = DateTime.UtcNow
    };

    [Fact]
    public async Task GetOrgUsersAsync_ReturnsOnlyUsersOfThatOrg_WithRoleName()
    {
        var (svc, db) = Build(Guid.NewGuid().ToString());
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();

        db.UserAccounts.AddRange(
            NewUser(orgA, OrgAdminRoleId, "admin-a@test.com"),
            NewUser(orgA, RequesterRoleId, "requester-a@test.com"),
            NewUser(orgB, OrgAdminRoleId, "admin-b@test.com")
        );
        await db.SaveChangesAsync();

        var result = await svc.GetOrgUsersAsync(orgA);

        result.Should().HaveCount(2);
        result.Should().OnlyContain(u => u.Email.EndsWith("-a@test.com"));
        result.Single(u => u.Email == "admin-a@test.com").RoleName.Should().Be("Org Admin");
    }

    [Fact]
    public async Task GetOrgUsersAsync_ExcludesDeletedUsers()
    {
        var (svc, db) = Build(Guid.NewGuid().ToString());
        var org = Guid.NewGuid();

        db.UserAccounts.AddRange(
            NewUser(org, OrgAdminRoleId, "active@test.com"),
            NewUser(org, RequesterRoleId, "deleted@test.com", isDelete: true)
        );
        await db.SaveChangesAsync();

        var result = await svc.GetOrgUsersAsync(org);

        result.Should().ContainSingle().Which.Email.Should().Be("active@test.com");
    }

    [Fact]
    public async Task ReassignOrgAdminAsync_PromotesNewAdmin_AndDemotesPreviousAdminToRequester()
    {
        var (svc, db) = Build(Guid.NewGuid().ToString());
        var org = Guid.NewGuid();

        var oldAdmin = NewUser(org, OrgAdminRoleId, "old-admin@test.com");
        var candidate = NewUser(org, RequesterRoleId, "candidate@test.com");
        db.UserAccounts.AddRange(oldAdmin, candidate);
        await db.SaveChangesAsync();

        await svc.ReassignOrgAdminAsync(org, candidate.UserID);

        (await db.UserAccounts.FindAsync(candidate.UserID))!.RoleID.Should().Be(OrgAdminRoleId);
        (await db.UserAccounts.FindAsync(oldAdmin.UserID))!.RoleID.Should().Be(RequesterRoleId);
    }

    [Fact]
    public async Task ReassignOrgAdminAsync_NoExistingAdmin_JustPromotesTheChosenUser()
    {
        var (svc, db) = Build(Guid.NewGuid().ToString());
        var org = Guid.NewGuid();

        var candidate = NewUser(org, RequesterRoleId, "candidate@test.com");
        db.UserAccounts.Add(candidate);
        await db.SaveChangesAsync();

        await svc.ReassignOrgAdminAsync(org, candidate.UserID);

        (await db.UserAccounts.FindAsync(candidate.UserID))!.RoleID.Should().Be(OrgAdminRoleId);
    }

    [Fact]
    public async Task ReassignOrgAdminAsync_UserBelongsToAnotherOrg_ThrowsBadRequest()
    {
        var (svc, db) = Build(Guid.NewGuid().ToString());
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();

        var outsider = NewUser(orgB, RequesterRoleId, "outsider@test.com");
        db.UserAccounts.Add(outsider);
        await db.SaveChangesAsync();

        var act = () => svc.ReassignOrgAdminAsync(orgA, outsider.UserID);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task ReassignOrgAdminAsync_DoesNotDemoteAdminsOfOtherOrgs()
    {
        var (svc, db) = Build(Guid.NewGuid().ToString());
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();

        var otherOrgAdmin = NewUser(orgB, OrgAdminRoleId, "other-org-admin@test.com");
        var candidate = NewUser(orgA, RequesterRoleId, "candidate@test.com");
        db.UserAccounts.AddRange(otherOrgAdmin, candidate);
        await db.SaveChangesAsync();

        await svc.ReassignOrgAdminAsync(orgA, candidate.UserID);

        (await db.UserAccounts.FindAsync(otherOrgAdmin.UserID))!.RoleID.Should().Be(OrgAdminRoleId);
    }
}
