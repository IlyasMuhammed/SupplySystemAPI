using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Auth.Data;
using SMS.Modules.Auth.Domain;
using SMS.Modules.Auth.Models;
using SMS.Modules.Auth.Repositories;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Auth.Tests;

/// <summary>
/// A35 X-01 / D-16 / REV-09 — the six multi-currency codes: catalogued, admins get all, the FINANCE_SETUP_MANAGE holders get
/// rate management and revaluation, every role (built-in, custom, existing or created later) reads currencies and rates,
/// and a grant an administrator switched off stays off.
/// </summary>
public class CurrencyPermissionSeedTests
{
    private static readonly string[] Codes =
    [
        PermissionCodes.CURRENCY_VIEW, PermissionCodes.CURRENCY_MANAGE, PermissionCodes.CURRENCY_RATE_VIEW,
        PermissionCodes.CURRENCY_RATE_MANAGE, PermissionCodes.ORG_CURRENCY_SETTINGS_MANAGE, PermissionCodes.EXCHANGE_REVALUATION_RUN
    ];

    private static (string Name, string Code, string Description)[] PermissionSeed() =>
        (ValueTuple<string, string, string>[])typeof(AuthDataSeeder)
            .GetField("PermissionSeed", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static Dictionary<int, string[]> RolePermissionSeed() =>
        (Dictionary<int, string[]>)typeof(AuthDataSeeder)
            .GetField("RolePermissionSeed", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static AuthDbContext NewDb(string name, Guid? org = null, bool superAdmin = true) =>
        new(new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(name).Options,
            new StaticTenantContext { OrganizationId = org ?? Guid.Empty, IsSuperAdmin = superAdmin });

    private static Task Invoke(AuthDataSeeder seeder, string method) =>
        (Task)typeof(AuthDataSeeder).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(seeder, null)!;

    private static async Task SeedAllButAdminAsync(AuthDbContext db)
    {
        var seeder = new AuthDataSeeder(db, new PasswordHasher<UserAccount>());
        await Invoke(seeder, "SeedPermissionsAsync");
        await Invoke(seeder, "SeedRolesAsync");
        await Invoke(seeder, "SeedRolePermissionsAsync");
        await seeder.SeedCurrencyGrantsAsync();
    }

    private static async Task<HashSet<string>> AllowedAsync(AuthDbContext db, int roleId) =>
        (await (from rp in db.RolePermissions.IgnoreQueryFilters()
                join p in db.Permissions on rp.PermissionID equals p.PermissionID
                where rp.RoleID == roleId && rp.IsAllowed
                select p.Code).ToListAsync()).ToHashSet();

    [Fact]
    public void Every_code_is_in_the_catalog_and_seeded_once_and_the_admins_get_all_six()
    {
        PermissionCodes.All.Should().Contain(Codes);
        foreach (var code in Codes)
            PermissionSeed().Should().ContainSingle(p => p.Code == code).Which.Description.Should().NotBeNullOrWhiteSpace();

        RolePermissionSeed()[(int)EnumRole.SystemAdmin].Should().Contain(Codes);
        RolePermissionSeed()[(int)EnumRole.OrgAdmin].Should().Contain(Codes);
        RolePermissionSeed()[(int)EnumRole.FinanceManager].Should().Contain([PermissionCodes.CURRENCY_RATE_MANAGE, PermissionCodes.EXCHANGE_REVALUATION_RUN]);
    }

    [Fact]
    public async Task Every_existing_role_reads_currencies_and_only_finance_setup_holders_manage_rates()
    {
        var name = Guid.NewGuid().ToString();
        await using var db = NewDb(name);
        // A custom role of some organization, and one holding FINANCE_SETUP_MANAGE, both created before A35.
        db.Roles.AddRange(
            new Role { RoleID = 500, Name = "Custom", RoleCode = "CUSTOM_X", IsActive = true, OrganizationId = Guid.NewGuid() },
            new Role { RoleID = 501, Name = "Treasury", RoleCode = "TREASURY_X", IsActive = true, OrganizationId = Guid.NewGuid() });
        await db.SaveChangesAsync();
        await SeedAllButAdminAsync(db);
        var setup = await db.Permissions.SingleAsync(p => p.Code == PermissionCodes.FINANCE_SETUP_MANAGE);
        db.RolePermissions.Add(new RolePermission { RoleID = 501, PermissionID = setup.PermissionID, IsAllowed = true, OrganizationId = TenantDefaults.ScmDemoOrganizationId });
        await db.SaveChangesAsync();

        await new AuthDataSeeder(db, new PasswordHasher<UserAccount>()).SeedCurrencyGrantsAsync();   // the next start

        var requester = await db.Roles.IgnoreQueryFilters().SingleAsync(r => r.RoleCode == "REQUESTER");
        (await AllowedAsync(db, requester.RoleID)).Should().Contain([PermissionCodes.CURRENCY_VIEW, PermissionCodes.CURRENCY_RATE_VIEW])
            .And.NotContain(PermissionCodes.CURRENCY_RATE_MANAGE);
        (await AllowedAsync(db, 500)).Should().BeEquivalentTo([PermissionCodes.CURRENCY_VIEW, PermissionCodes.CURRENCY_RATE_VIEW]);
        (await AllowedAsync(db, 501)).Should().Contain([PermissionCodes.CURRENCY_RATE_MANAGE, PermissionCodes.EXCHANGE_REVALUATION_RUN])
            .And.NotContain(PermissionCodes.CURRENCY_MANAGE);

        var orgAdmin = await db.Roles.IgnoreQueryFilters().SingleAsync(r => r.RoleCode == "ORG_ADMIN");
        (await AllowedAsync(db, orgAdmin.RoleID)).Should().Contain(Codes);
    }

    [Fact]
    public async Task A_grant_switched_off_by_an_administrator_stays_off_and_reruns_add_nothing()
    {
        var name = Guid.NewGuid().ToString();
        await using var db = NewDb(name);
        await SeedAllButAdminAsync(db);
        var requester = await db.Roles.IgnoreQueryFilters().SingleAsync(r => r.RoleCode == "REQUESTER");
        var view = await db.Permissions.SingleAsync(p => p.Code == PermissionCodes.CURRENCY_VIEW);
        (await db.RolePermissions.SingleAsync(rp => rp.RoleID == requester.RoleID && rp.PermissionID == view.PermissionID)).IsAllowed = false;
        await db.SaveChangesAsync();
        var before = await db.RolePermissions.CountAsync();

        await new AuthDataSeeder(db, new PasswordHasher<UserAccount>()).SeedCurrencyGrantsAsync();

        (await db.RolePermissions.CountAsync()).Should().Be(before);
        (await AllowedAsync(db, requester.RoleID)).Should().NotContain(PermissionCodes.CURRENCY_VIEW);
    }

    [Fact]
    public async Task A_role_created_later_reads_currencies_and_rates_from_the_start()
    {
        var name = Guid.NewGuid().ToString();
        await using (var seedDb = NewDb(name)) await SeedAllButAdminAsync(seedDb);

        var org = Guid.NewGuid();
        await using var db = NewDb(name, org, superAdmin: false);
        var created = await new AuthRepository(db, new PasswordHasher<UserAccount>())
            .CreateRoleAsync(new CreateRoleRequest { Name = "Late Role", RoleCode = "LATE_ROLE" });

        await using var check = NewDb(name);
        (await AllowedAsync(check, created.RoleId)).Should().BeEquivalentTo([PermissionCodes.CURRENCY_VIEW, PermissionCodes.CURRENCY_RATE_VIEW]);
        created.PermissionCount.Should().Be(2);
    }
}
