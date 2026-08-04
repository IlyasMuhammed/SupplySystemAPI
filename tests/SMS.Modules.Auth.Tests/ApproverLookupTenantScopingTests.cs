using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Auth.Data;
using SMS.Modules.Auth.Domain;
using SMS.Modules.Auth.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Auth.Tests;

// MT-006: IApproverResolutionService itself has no notion of organizations — the actual isolation
// guarantee ("a Supervisor step in Org A never resolves to a user in Org B") lives entirely in
// UserQueryService/OrgChartService, which query UserAccounts/Departments through the MT-003
// tenant-filtered AuthDbContext. These tests exercise that real guarantee directly, with two orgs'
// users sharing the same RoleID, to prove role/department/supervisor lookups never cross org
// boundaries even when the data would otherwise collide.
public class ApproverLookupTenantScopingTests
{
    private const int SharedRoleId = 5;

    private static AuthDbContext NewDb(string dbName, ITenantContext tenantContext) =>
        new(new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(dbName).Options, tenantContext);

    private static async Task<(Guid OrgA, Guid OrgB, int Alice, int Bob, int Carol, int DeptA, int DeptB)> SeedTwoOrgsAsync(string dbName)
    {
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();

        using (var db = NewDb(dbName, new StaticTenantContext { OrganizationId = orgA }))
        {
            db.UserAccounts.AddRange(
                new UserAccount { UserID = 201, FirstName = "Alice", Email = "alice@a.test", RoleID = SharedRoleId, IsActive = true, OrganizationId = orgA },
                new UserAccount { UserID = 202, FirstName = "Bob",   Email = "bob@a.test",   RoleID = SharedRoleId, IsActive = true, SupervisorId = 201, OrganizationId = orgA });
            db.Departments.Add(new Department { DepartmentId = 10, Name = "Ops A", HeadUserId = 201, OrganizationId = orgA });
            await db.SaveChangesAsync();
        }

        using (var db = NewDb(dbName, new StaticTenantContext { OrganizationId = orgB }))
        {
            db.UserAccounts.Add(
                new UserAccount { UserID = 301, FirstName = "Carol", Email = "carol@b.test", RoleID = SharedRoleId, IsActive = true, OrganizationId = orgB });
            db.Departments.Add(new Department { DepartmentId = 20, Name = "Ops B", HeadUserId = 301, OrganizationId = orgB });
            await db.SaveChangesAsync();
        }

        return (orgA, orgB, 201, 202, 301, 10, 20);
    }

    [Fact]
    public async Task GetActiveUsersByRoleAsync_FromOrgA_NeverReturnsOrgBsUserInTheSameRole()
    {
        var dbName = Guid.NewGuid().ToString();
        var (orgA, _, alice, bob, carol, _, _) = await SeedTwoOrgsAsync(dbName);

        using var db = NewDb(dbName, new StaticTenantContext { OrganizationId = orgA });
        var svc = new UserQueryService(db);

        var result = await svc.GetActiveUsersByRoleAsync(SharedRoleId);

        result.Select(u => u.UserId).Should().BeEquivalentTo([alice, bob]);
        result.Should().NotContain(u => u.UserId == carol);
    }

    [Fact]
    public async Task GetActiveUsersByRoleAsync_FromOrgB_NeverReturnsOrgAsUsersInTheSameRole()
    {
        var dbName = Guid.NewGuid().ToString();
        var (_, orgB, alice, bob, carol, _, _) = await SeedTwoOrgsAsync(dbName);

        using var db = NewDb(dbName, new StaticTenantContext { OrganizationId = orgB });
        var svc = new UserQueryService(db);

        var result = await svc.GetActiveUsersByRoleAsync(SharedRoleId);

        result.Select(u => u.UserId).Should().BeEquivalentTo([carol]);
        result.Should().NotContain(u => u.UserId == alice || u.UserId == bob);
    }

    [Fact]
    public async Task GetSupervisorAsync_ResolvesWithinTheSameOrg()
    {
        var dbName = Guid.NewGuid().ToString();
        var (orgA, _, alice, bob, _, _, _) = await SeedTwoOrgsAsync(dbName);

        using var db = NewDb(dbName, new StaticTenantContext { OrganizationId = orgA });
        var svc = new OrgChartService(db);

        var supervisor = await svc.GetSupervisorAsync(bob);

        supervisor.Should().NotBeNull();
        supervisor!.UserId.Should().Be(alice);
    }

    [Fact]
    public async Task GetSupervisorAsync_FromOrgAsContext_NeverResolvesAnOrgBUser()
    {
        var dbName = Guid.NewGuid().ToString();
        var (orgA, _, _, _, carol, _, _) = await SeedTwoOrgsAsync(dbName);

        // Org B's user id, queried while resolving as Org A — Org A's ambient tenant filter makes
        // that row invisible entirely, so this must resolve to nothing rather than leaking Carol.
        using var db = NewDb(dbName, new StaticTenantContext { OrganizationId = orgA });
        var svc = new OrgChartService(db);

        var supervisor = await svc.GetSupervisorAsync(carol);

        supervisor.Should().BeNull();
    }

    [Fact]
    public async Task GetDepartmentHeadAsync_ResolvesWithinTheSameOrg()
    {
        var dbName = Guid.NewGuid().ToString();
        var (orgA, _, alice, _, _, deptA, _) = await SeedTwoOrgsAsync(dbName);

        using var db = NewDb(dbName, new StaticTenantContext { OrganizationId = orgA });
        var svc = new OrgChartService(db);

        var head = await svc.GetDepartmentHeadAsync(deptA);

        head.Should().NotBeNull();
        head!.UserId.Should().Be(alice);
    }

    [Fact]
    public async Task GetDepartmentHeadAsync_FromOrgAsContext_NeverResolvesAnOrgBDepartment()
    {
        var dbName = Guid.NewGuid().ToString();
        var (orgA, _, _, _, _, _, deptB) = await SeedTwoOrgsAsync(dbName);

        using var db = NewDb(dbName, new StaticTenantContext { OrganizationId = orgA });
        var svc = new OrgChartService(db);

        var head = await svc.GetDepartmentHeadAsync(deptB);

        head.Should().BeNull();
    }

    [Fact]
    public async Task SuperAdmin_CanSeeUsersAcrossBothOrganizations()
    {
        var dbName = Guid.NewGuid().ToString();
        var (_, _, alice, bob, carol, _, _) = await SeedTwoOrgsAsync(dbName);

        using var db = NewDb(dbName, new StaticTenantContext { IsSuperAdmin = true });
        var svc = new UserQueryService(db);

        var result = await svc.GetActiveUsersByRoleAsync(SharedRoleId);

        result.Select(u => u.UserId).Should().BeEquivalentTo([alice, bob, carol]);
    }
}
