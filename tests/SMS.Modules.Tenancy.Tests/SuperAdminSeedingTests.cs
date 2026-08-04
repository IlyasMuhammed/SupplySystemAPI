using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Tenancy.Data;
using SMS.Modules.Tenancy.Domain;
using SMS.Modules.Tenancy.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Tenancy.Tests;

// MT-007, FSD Section 7.1: SuperAdminUsers is the authoritative record of platform Super Admins,
// seeded once by designating the first existing System Admin user.
public class SuperAdminSeedingTests
{
    private static TenancyDbContext NewDb() =>
        new(new DbContextOptionsBuilder<TenancyDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static Mock<IUserQueryService> UserQueryReturning(int? firstSystemAdminUserId)
    {
        var mock = new Mock<IUserQueryService>();
        mock.Setup(x => x.GetFirstSystemAdminUserIdAsync()).ReturnsAsync(firstSystemAdminUserId);
        return mock;
    }

    [Fact]
    public async Task SeedAsync_DesignatesTheFirstSystemAdmin_WhenTableIsEmpty()
    {
        var db = NewDb();
        var seeder = new TenancyDataSeeder(db, UserQueryReturning(firstSystemAdminUserId: 7).Object);

        await seeder.SeedAsync();

        var row = await db.SuperAdminUsers.SingleAsync();
        row.UserId.Should().Be(7);
        row.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task SeedAsync_IsIdempotent_DoesNotAddASecondRow_OrChangeTheDesignatedUser()
    {
        var db = NewDb();
        var seederRun1 = new TenancyDataSeeder(db, UserQueryReturning(firstSystemAdminUserId: 7).Object);
        await seederRun1.SeedAsync();

        // A second run — even if GetFirstSystemAdminUserIdAsync would now resolve to a *different*
        // user (e.g. user 7 was deactivated and a new System Admin created later) — must not touch
        // the already-designated Super Admin. Later designations are a deliberate admin action, not
        // something this startup seeder re-decides.
        var seederRun2 = new TenancyDataSeeder(db, UserQueryReturning(firstSystemAdminUserId: 99).Object);
        await seederRun2.SeedAsync();

        var rows = await db.SuperAdminUsers.ToListAsync();
        rows.Should().ContainSingle();
        rows.Single().UserId.Should().Be(7);
    }

    [Fact]
    public async Task SeedAsync_NoSystemAdminExists_DoesNotInsertAnyRow_AndDoesNotThrow()
    {
        var db = NewDb();
        var seeder = new TenancyDataSeeder(db, UserQueryReturning(firstSystemAdminUserId: null).Object);

        await seeder.SeedAsync();

        (await db.SuperAdminUsers.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task IsSuperAdminAsync_ReturnsTrue_OnlyForADesignatedUser()
    {
        var db = NewDb();
        db.SuperAdminUsers.Add(new SuperAdminUser { UserId = 7, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var svc = new SuperAdminService(db);

        (await svc.IsSuperAdminAsync(7)).Should().BeTrue();
        (await svc.IsSuperAdminAsync(8)).Should().BeFalse();
    }
}
