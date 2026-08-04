using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Auth.Data;
using SMS.Modules.Auth.Domain;
using SMS.Modules.Auth.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Auth.Tests;

// MT-007 — GetFirstSystemAdminUserIdAsync backs TenancyDataSeeder's one-time Super Admin
// designation migration; "first" must mean the earliest-created active System Admin.
public class UserQueryServiceTests
{
    private static AuthDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new StaticTenantContext());

    [Fact]
    public async Task GetFirstSystemAdminUserIdAsync_ReturnsTheEarliestCreated_NotTheLowestUserId()
    {
        var db = NewDb();
        db.UserAccounts.AddRange(
            new UserAccount { UserID = 50, Email = "later@test.com", RoleID = (int)EnumRole.SystemAdmin, IsActive = true, CreatedDate = new DateTime(2026, 3, 1) },
            new UserAccount { UserID = 10, Email = "earlier@test.com", RoleID = (int)EnumRole.SystemAdmin, IsActive = true, CreatedDate = new DateTime(2026, 1, 1) });
        await db.SaveChangesAsync();

        var svc = new UserQueryService(db);
        var result = await svc.GetFirstSystemAdminUserIdAsync();

        result.Should().Be(10);
    }

    [Fact]
    public async Task GetFirstSystemAdminUserIdAsync_ExcludesInactiveAndDeletedUsers()
    {
        var db = NewDb();
        db.UserAccounts.AddRange(
            new UserAccount { UserID = 1, Email = "inactive@test.com", RoleID = (int)EnumRole.SystemAdmin, IsActive = false, CreatedDate = new DateTime(2026, 1, 1) },
            new UserAccount { UserID = 2, Email = "deleted@test.com",  RoleID = (int)EnumRole.SystemAdmin, IsActive = true, IsDelete = true, CreatedDate = new DateTime(2026, 1, 2) },
            new UserAccount { UserID = 3, Email = "active@test.com",   RoleID = (int)EnumRole.SystemAdmin, IsActive = true, CreatedDate = new DateTime(2026, 1, 3) });
        await db.SaveChangesAsync();

        var svc = new UserQueryService(db);
        var result = await svc.GetFirstSystemAdminUserIdAsync();

        result.Should().Be(3);
    }

    [Fact]
    public async Task GetFirstSystemAdminUserIdAsync_IgnoresUsersInOtherRoles()
    {
        var db = NewDb();
        db.UserAccounts.Add(
            new UserAccount { UserID = 1, Email = "requester@test.com", RoleID = (int)EnumRole.Requester, IsActive = true, CreatedDate = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var svc = new UserQueryService(db);
        var result = await svc.GetFirstSystemAdminUserIdAsync();

        result.Should().BeNull();
    }
}
