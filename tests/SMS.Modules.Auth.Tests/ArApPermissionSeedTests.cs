using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Auth.Data;
using SMS.Modules.Auth.Domain;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Auth.Tests;

/// <summary>
/// A48 D-14 / D-CORE-7 — the twenty AR/AP codes: catalog, module Finance, role-editor group, grants by built-in role and by
/// what a (custom) role holds, only missing grants added, a switched-off grant kept off.
/// </summary>
public class ArApPermissionSeedTests
{
    private static readonly string[] ArAp =
    [
        "AR_VIEW", "AR_STATEMENT_SEND", "AR_CREDIT_MEMO", "CREDIT_CHECK", "CREDIT_MANAGE", "CREDIT_OVERRIDE", "DUNNING_EXECUTE",
        "DUNNING_ADMIN", "AP_VIEW", "AP_EXECUTE", "AP_DEBIT_MEMO", "MATCH_EXECUTE", "MATCH_OVERRIDE", "MATCH_APPROVE",
        "PAYMENT_RUN_PROPOSE", "PAYMENT_RUN_EDIT", "PAYMENT_RUN_APPROVE", "PAYMENT_RUN_EXECUTE", "PAYMENT_RUN_ADMIN", "FINANCE_AR_AP_ADMIN",
    ];

    private static object Field(string name) =>
        typeof(AuthDataSeeder).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static string Grouping(string code) =>
        (string)typeof(SMS.Modules.Auth.Repositories.AuthRepository)
            .GetMethod("GetPermissionModule", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [code])!;

    [Fact]
    public void Codes_are_in_the_catalog_seeded_once_mapped_to_finance_and_grouped()
    {
        PermissionCodes.ArApCodes.Should().BeEquivalentTo(ArAp);
        PermissionCodes.All.Should().Contain(ArAp).And.OnlyHaveUniqueItems();
        var seed = ((string Name, string Code, string Description)[])Field("PermissionSeed");
        seed.Select(s => s.Name).Should().OnlyHaveUniqueItems();
        foreach (var code in ArAp)
        {
            seed.Should().ContainSingle(p => p.Code == code).Which.Description.Should().NotBeNullOrWhiteSpace();
            ModuleCodeMap.ForPermission(code).Should().Be(ModuleCodes.Finance, code);
            Grouping(code).Should().Be("Receivables & Payables", code);
        }
        // The older PAYMENT_ / FINANCE_SETUP_ codes keep their groups and modules.
        Grouping(PermissionCodes.PAYMENT_VIEW).Should().Be("Finance");
        ModuleCodeMap.ForPermission(PermissionCodes.FINANCE_SETUP_MANAGE).Should().Be(ModuleCodes.MasterData);
    }

    private static AuthDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new StaticTenantContext { IsSuperAdmin = true });

    private static async Task SeedAsync(AuthDbContext db)
    {
        var seeder = new AuthDataSeeder(db, new PasswordHasher<UserAccount>());
        foreach (var step in new[] { "SeedPermissionsAsync", "SeedRolesAsync", "SeedRolePermissionsAsync" })
            await (Task)typeof(AuthDataSeeder).GetMethod(step, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(seeder, null)!;
        await seeder.SeedGlGrantsAsync();
        await seeder.SeedArApGrantsAsync();
    }

    private static async Task<List<string>> ArApGrantsAsync(AuthDbContext db, int roleId) =>
        await db.RolePermissions.IgnoreQueryFilters().Where(rp => rp.RoleID == roleId && rp.IsAllowed)
            .Join(db.Permissions, rp => rp.PermissionID, p => p.PermissionID, (rp, p) => p.Code)
            .Where(c => ArAp.Contains(c)).ToListAsync();

    private static async Task<int> RoleIdAsync(AuthDbContext db, string code) =>
        (await db.Roles.IgnoreQueryFilters().SingleAsync(r => r.RoleCode == code)).RoleID;

    [Theory]
    [InlineData("SYSTEM_ADMIN", "*")]
    [InlineData("ORG_ADMIN", "*")]
    [InlineData("FINANCE_MANAGER", "*")]
    [InlineData("FINANCE_OFFICER", "AR_VIEW,AP_VIEW,AR_STATEMENT_SEND,CREDIT_CHECK,AP_EXECUTE,MATCH_EXECUTE,PAYMENT_RUN_PROPOSE,PAYMENT_RUN_EDIT,DUNNING_EXECUTE")]
    [InlineData("AUDITOR", "AR_VIEW,AP_VIEW")]
    [InlineData("PROCUREMENT_MANAGER", "")]
    [InlineData("POS_CASHIER", "")]
    public async Task Built_in_roles_get_the_D14_grants(string role, string expected)
    {
        await using var db = NewDb();
        await SeedAsync(db);

        var codes = await ArApGrantsAsync(db, await RoleIdAsync(db, role));
        var want = expected == "*" ? ArAp : expected.Split(',', StringSplitOptions.RemoveEmptyEntries);
        codes.Should().BeEquivalentTo(want);
    }

    [Fact]
    public async Task A_custom_role_reading_invoices_gets_the_view_codes_and_a_switched_off_grant_stays_off()
    {
        await using var db = NewDb();
        await SeedAsync(db);
        int Perm(string code) => db.Permissions.Single(p => p.Code == code).PermissionID;

        db.Roles.Add(new Role { RoleID = 900, Name = "Receivables Clerk", RoleCode = "AR_CLERK", IsActive = true, OrganizationId = Guid.NewGuid() });
        db.Roles.Add(new Role { RoleID = 901, Name = "Payables Clerk", RoleCode = "AP_CLERK", IsActive = true, OrganizationId = Guid.NewGuid() });
        db.RolePermissions.Add(new RolePermission { RoleID = 900, PermissionID = Perm("CUSTOMER_LEDGER_VIEW"), IsAllowed = true });
        db.RolePermissions.Add(new RolePermission { RoleID = 901, PermissionID = Perm("PAYMENT_VIEW"), IsAllowed = true });
        db.RolePermissions.Add(new RolePermission { RoleID = 901, PermissionID = Perm("AP_VIEW"), IsAllowed = false });
        await db.SaveChangesAsync();

        var seeder = new AuthDataSeeder(db, new PasswordHasher<UserAccount>());
        await seeder.SeedArApGrantsAsync();
        await seeder.SeedArApGrantsAsync();

        (await ArApGrantsAsync(db, 900)).Should().BeEquivalentTo(["AR_VIEW"]);
        (await ArApGrantsAsync(db, 901)).Should().BeEmpty("an administrator switched AP_VIEW off for this role");
        (await db.RolePermissions.IgnoreQueryFilters().CountAsync(rp => rp.RoleID == 900)).Should().Be(2, "no duplicates on a re-run");
    }
}
