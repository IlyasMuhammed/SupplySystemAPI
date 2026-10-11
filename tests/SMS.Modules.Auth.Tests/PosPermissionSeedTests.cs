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
/// A38 D-13 / API-CONTRACT §9 — the 17 POS_* codes (catalog, All, seeded once, module map, role-editor group), the three
/// built-in POS roles with their exact grants (matched by code like every built-in role), admins holding every POS code,
/// and idempotent re-seeding.
/// </summary>
public class PosPermissionSeedTests
{
    private static readonly string[] PosCodes =
    [
        "POS_STORE_VIEW", "POS_STORE_MANAGE", "POS_TERMINAL_VIEW", "POS_TERMINAL_MANAGE",
        "POS_SHIFT_OPEN", "POS_SHIFT_CLOSE", "POS_SHIFT_RECONCILE", "POS_SHIFT_VIEW",
        "POS_ORDER_CREATE", "POS_ORDER_VIEW", "POS_ORDER_VOID", "POS_ORDER_RETURN", "POS_ORDER_DISCOUNT",
        "POS_CASH_MOVEMENT", "POS_PRODUCT_SETTINGS", "POS_CUSTOMER_SETTINGS", "POS_REPORTS_VIEW",
        // A39 D-9 — returns, exchanges and store credit.
        "POS_RETURN_CROSS_STORE", "POS_RETURN_OVERRIDE", "POS_RETURN_POLICY_MANAGE", "POS_RETURN_REASON_MANAGE", "POS_RETURN_REPORTS",
        "POS_CREDIT_NOTE_ISSUE", "POS_CREDIT_NOTE_REDEEM", "POS_CREDIT_NOTE_CANCEL", "POS_CREDIT_NOTE_VIEW", "POS_CREDIT_NOTE_REPORTS"
    ];

    private static readonly string[] Cashier = ["POS_ORDER_CREATE", "POS_CREDIT_NOTE_REDEEM"];
    private static readonly string[] Supervisor =
    [
        .. Cashier, "POS_ORDER_VIEW", "POS_ORDER_DISCOUNT", "POS_ORDER_VOID", "POS_ORDER_RETURN",
        "POS_SHIFT_OPEN", "POS_SHIFT_CLOSE", "POS_SHIFT_VIEW", "POS_CASH_MOVEMENT", "POS_TERMINAL_VIEW", "POS_STORE_VIEW",
        "POS_RETURN_CROSS_STORE", "POS_RETURN_REPORTS", "POS_CREDIT_NOTE_ISSUE", "POS_CREDIT_NOTE_VIEW"
    ];
    private static readonly string[] Manager =
    [
        .. Supervisor, "POS_SHIFT_RECONCILE", "POS_TERMINAL_MANAGE", "POS_PRODUCT_SETTINGS", "POS_CUSTOMER_SETTINGS",
        "POS_REPORTS_VIEW", "CUSTOMER_VIEW",
        "POS_RETURN_OVERRIDE", "POS_CREDIT_NOTE_CANCEL", "POS_CREDIT_NOTE_REPORTS"
    ];

    private static object Field(string name) =>
        typeof(AuthDataSeeder).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

    private static (string Name, string Code, string Description)[] PermissionSeed() => ((string, string, string)[])Field("PermissionSeed");
    private static (int Id, string Name, string Code, string Description)[] RoleSeed() => ((int, string, string, string)[])Field("RoleSeed");
    private static Dictionary<int, string[]> RolePermissionSeed() => (Dictionary<int, string[]>)Field("RolePermissionSeed");

    private static string Grouping(string code) =>
        (string)typeof(SMS.Modules.Auth.Repositories.AuthRepository)
            .GetMethod("GetPermissionModule", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [code])!;

    // ── Catalog ──────────────────────────────────────────────────────────────

    [Fact]
    public void All_27_codes_are_in_the_catalog_and_seeded_once_with_a_name_and_description()
    {
        PosCodes.Should().HaveCount(27, "17 from A38 + 10 from A39");
        PermissionCodes.All.Where(c => c.StartsWith("POS_")).Should().BeEquivalentTo(PosCodes);
        foreach (var code in PosCodes)
        {
            var row = PermissionSeed().Should().ContainSingle(p => p.Code == code, code).Subject;
            row.Name.Should().NotBeNullOrWhiteSpace();
            row.Description.Should().NotBeNullOrWhiteSpace();
        }
        PermissionCodes.All.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Every_pos_code_maps_to_the_pos_module_and_no_other_code_does()
    {
        foreach (var code in PosCodes) ModuleCodeMap.ForPermission(code).Should().Be(ModuleCodes.Pos, code);
        PermissionCodes.All.Except(PosCodes).Select(ModuleCodeMap.ForPermission).Should().NotContain(ModuleCodes.Pos);
        // The prefixes the POS codes could be confused with keep their own modules.
        ModuleCodeMap.ForPermission(PermissionCodes.PO_VIEW).Should().Be(ModuleCodes.Demand);
        ModuleCodeMap.ForPermission(PermissionCodes.POD_CAPTURE).Should().Be(ModuleCodes.Logistics);
    }

    [Fact]
    public void They_group_under_point_of_sale_in_the_role_editor() =>
        PosCodes.Select(Grouping).Should().OnlyContain(g => g == "Point of Sale");

    // ── Roles ────────────────────────────────────────────────────────────────

    [Fact]
    public void The_three_pos_roles_are_seeded_with_their_codes()
    {
        var roles = RoleSeed();
        roles.Should().ContainSingle(r => r.Code == "POS_CASHIER").Which.Name.Should().Be("POS Cashier");
        roles.Should().ContainSingle(r => r.Code == "POS_SUPERVISOR").Which.Name.Should().Be("POS Supervisor");
        roles.Should().ContainSingle(r => r.Code == "STORE_MANAGER").Which.Name.Should().Be("Store Manager");
        roles.Select(r => r.Id).Should().OnlyHaveUniqueItems();
        roles.Select(r => r.Code).Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData("POS_CASHIER")]
    [InlineData("POS_SUPERVISOR")]
    [InlineData("STORE_MANAGER")]
    public void Each_pos_role_gets_exactly_its_listed_grants(string roleCode)
    {
        var id = RoleSeed().Single(r => r.Code == roleCode).Id;
        var expected = roleCode switch { "POS_CASHIER" => Cashier, "POS_SUPERVISOR" => Supervisor, _ => Manager };
        RolePermissionSeed()[id].Should().BeEquivalentTo(expected);
    }

    [Theory]
    [InlineData(EnumRole.SystemAdmin)]
    [InlineData(EnumRole.OrgAdmin)]
    public void Admins_hold_every_pos_code(EnumRole role) =>
        RolePermissionSeed()[(int)role].Should().Contain(PosCodes);

    [Theory]
    [InlineData(EnumRole.ProcurementManager)]
    [InlineData(EnumRole.PurchaseOfficer)]
    [InlineData(EnumRole.InventoryManager)]
    [InlineData(EnumRole.WarehouseOperator)]
    [InlineData(EnumRole.FinanceOfficer)]
    [InlineData(EnumRole.Requester)]
    [InlineData(EnumRole.Auditor)]
    [InlineData(EnumRole.FinanceManager)]
    [InlineData(EnumRole.SupplyDeptAdmin)]
    public void No_other_built_in_role_gets_a_pos_code(EnumRole role) =>
        RolePermissionSeed()[(int)role].Should().NotContain(c => c.StartsWith("POS_"));

    // ── Seeding against a database (InMemory; SeedAsync itself migrates, so the steps run directly) ──

    private static AuthDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new StaticTenantContext { IsSuperAdmin = true });

    private static async Task SeedAsync(AuthDbContext db)
    {
        var seeder = new AuthDataSeeder(db, new PasswordHasher<UserAccount>());
        foreach (var step in new[] { "SeedPermissionsAsync", "SeedRolesAsync", "SeedRolePermissionsAsync" })
            await (Task)typeof(AuthDataSeeder).GetMethod(step, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(seeder, null)!;
        await seeder.SeedCurrencyGrantsAsync();
        await seeder.SeedCustomerGrantsAsync();
    }

    private static async Task<List<string>> GrantsAsync(AuthDbContext db, string roleCode)
    {
        var roleId = (await db.Roles.IgnoreQueryFilters().SingleAsync(r => r.RoleCode == roleCode)).RoleID;
        return await db.RolePermissions.IgnoreQueryFilters().Where(rp => rp.RoleID == roleId && rp.IsAllowed)
            .Join(db.Permissions, rp => rp.PermissionID, p => p.PermissionID, (rp, p) => p.Code).ToListAsync();
    }

    [Fact]
    public async Task Seeding_creates_global_pos_roles_with_their_grants_and_the_pos_module_code()
    {
        var db = NewDb();
        await SeedAsync(db);

        foreach (var code in new[] { "POS_CASHIER", "POS_SUPERVISOR", "STORE_MANAGER" })
            (await db.Roles.IgnoreQueryFilters().SingleAsync(r => r.RoleCode == code))
                .Should().Match<Role>(r => r.IsGlobal && r.OrganizationId == null && r.IsActive, code);

        // A35 D-16 gives every role the currency read codes too.
        string[] everyRole = [PermissionCodes.CURRENCY_VIEW, PermissionCodes.CURRENCY_RATE_VIEW];
        (await GrantsAsync(db, "POS_CASHIER")).Should().BeEquivalentTo(Cashier.Concat(everyRole));
        (await GrantsAsync(db, "POS_SUPERVISOR")).Should().BeEquivalentTo(Supervisor.Concat(everyRole));
        (await GrantsAsync(db, "STORE_MANAGER")).Should().BeEquivalentTo(Manager.Concat(everyRole));
        (await GrantsAsync(db, "ORG_ADMIN")).Should().Contain(PosCodes);
        (await GrantsAsync(db, "SYSTEM_ADMIN")).Should().Contain(PosCodes);

        (await db.Permissions.Where(p => p.Code.StartsWith("POS_")).Select(p => p.ModuleCode).Distinct().ToListAsync())
            .Should().Equal(ModuleCodes.Pos);
    }

    [Fact]
    public async Task Re_seeding_adds_nothing_and_keeps_an_administrators_switch_off()
    {
        var db = NewDb();
        await SeedAsync(db);
        var cashierId = (await db.Roles.IgnoreQueryFilters().SingleAsync(r => r.RoleCode == "POS_CASHIER")).RoleID;
        var sell = await db.Permissions.SingleAsync(p => p.Code == "POS_ORDER_CREATE");
        (await db.RolePermissions.IgnoreQueryFilters().SingleAsync(rp => rp.RoleID == cashierId && rp.PermissionID == sell.PermissionID)).IsAllowed = false;
        await db.SaveChangesAsync();
        var counts = (await db.Roles.IgnoreQueryFilters().CountAsync(), await db.Permissions.CountAsync(), await db.RolePermissions.IgnoreQueryFilters().CountAsync());

        await SeedAsync(db);
        await SeedAsync(db);

        (await db.Roles.IgnoreQueryFilters().CountAsync(), await db.Permissions.CountAsync(), await db.RolePermissions.IgnoreQueryFilters().CountAsync())
            .Should().Be(counts);
        (await GrantsAsync(db, "POS_CASHIER")).Should().NotContain("POS_ORDER_CREATE");
    }

    [Fact]
    public async Task An_a38_database_picks_up_the_a39_codes_and_grants_on_the_next_start_keeping_switched_off_ones_off()
    {
        // An A38-era database: the POS roles exist with their A38 grants; the A39 permission rows do not exist yet.
        var db = NewDb();
        await SeedAsync(db);
        var a39 = await db.Permissions.Where(p => ReturnCodesOnly.Contains(p.Code)).ToListAsync();
        var a39Ids = a39.Select(p => p.PermissionID).ToList();
        db.RolePermissions.RemoveRange(await db.RolePermissions.IgnoreQueryFilters()
            .Where(rp => a39Ids.Contains(rp.PermissionID)).ToListAsync());
        db.Permissions.RemoveRange(a39);
        await db.SaveChangesAsync();
        (await GrantsAsync(db, "STORE_MANAGER")).Should().NotContain(ReturnCodesOnly);

        await SeedAsync(db);

        (await db.Permissions.CountAsync(p => ReturnCodesOnly.Contains(p.Code))).Should().Be(10);
        (await db.Permissions.Where(p => ReturnCodesOnly.Contains(p.Code)).Select(p => p.ModuleCode).Distinct().ToListAsync()).Should().Equal(ModuleCodes.Pos);
        string[] everyRole = [PermissionCodes.CURRENCY_VIEW, PermissionCodes.CURRENCY_RATE_VIEW];
        (await GrantsAsync(db, "POS_CASHIER")).Should().BeEquivalentTo(Cashier.Concat(everyRole));
        (await GrantsAsync(db, "POS_SUPERVISOR")).Should().BeEquivalentTo(Supervisor.Concat(everyRole));
        (await GrantsAsync(db, "STORE_MANAGER")).Should().BeEquivalentTo(Manager.Concat(everyRole));
        (await GrantsAsync(db, "ORG_ADMIN")).Should().Contain(ReturnCodesOnly);
        (await GrantsAsync(db, "SYSTEM_ADMIN")).Should().Contain(ReturnCodesOnly);
        (await GrantsAsync(db, "FINANCE_MANAGER")).Should().NotContain(c => c.StartsWith("POS_"));

        // An administrator takes store-credit cancelling away from the store manager: re-seeding leaves it off.
        var managerId = (await db.Roles.IgnoreQueryFilters().SingleAsync(r => r.RoleCode == "STORE_MANAGER")).RoleID;
        var cancel = await db.Permissions.SingleAsync(p => p.Code == "POS_CREDIT_NOTE_CANCEL");
        (await db.RolePermissions.IgnoreQueryFilters().SingleAsync(rp => rp.RoleID == managerId && rp.PermissionID == cancel.PermissionID)).IsAllowed = false;
        await db.SaveChangesAsync();
        var rows = await db.RolePermissions.IgnoreQueryFilters().CountAsync();

        await SeedAsync(db);

        (await db.RolePermissions.IgnoreQueryFilters().CountAsync()).Should().Be(rows);
        (await GrantsAsync(db, "STORE_MANAGER")).Should().NotContain("POS_CREDIT_NOTE_CANCEL");
    }

    private static readonly string[] ReturnCodesOnly =
    [
        "POS_RETURN_CROSS_STORE", "POS_RETURN_OVERRIDE", "POS_RETURN_POLICY_MANAGE", "POS_RETURN_REASON_MANAGE", "POS_RETURN_REPORTS",
        "POS_CREDIT_NOTE_ISSUE", "POS_CREDIT_NOTE_REDEEM", "POS_CREDIT_NOTE_CANCEL", "POS_CREDIT_NOTE_VIEW", "POS_CREDIT_NOTE_REPORTS"
    ];

    [Fact]
    public async Task A_role_squatting_a_pos_seed_id_is_left_alone_and_gains_nothing()
    {
        var db = NewDb();
        db.Roles.Add(new Role { RoleID = AuthDataSeeder.PosCashierSeedId, Name = "Night shift", RoleCode = "NIGHT", IsGlobal = false, OrganizationId = Guid.NewGuid() });
        await db.SaveChangesAsync();

        await SeedAsync(db);

        var squatter = await db.Roles.IgnoreQueryFilters().SingleAsync(r => r.RoleCode == "NIGHT");
        squatter.RoleID.Should().Be(AuthDataSeeder.PosCashierSeedId);
        (await GrantsAsync(db, "NIGHT")).Should().BeEquivalentTo([PermissionCodes.CURRENCY_VIEW, PermissionCodes.CURRENCY_RATE_VIEW]);
        (await db.Roles.IgnoreQueryFilters().SingleAsync(r => r.RoleCode == "POS_CASHIER")).RoleID.Should().NotBe(AuthDataSeeder.PosCashierSeedId);
        (await GrantsAsync(db, "POS_CASHIER")).Should().Contain("POS_ORDER_CREATE");
    }

    [Fact]
    public async Task A_pos_role_never_lands_on_an_id_with_orphaned_grants_or_users()
    {
        // A deleted role left grants (no FK) at 12 and a user at 13: a new built-in role there would inherit them.
        var db = NewDb();
        db.RolePermissions.Add(new RolePermission { RoleID = AuthDataSeeder.PosCashierSeedId, PermissionID = 999, IsAllowed = true });
        db.UserAccounts.Add(new UserAccount
        {
            UserID = 50, FirstName = "Orphan", Email = "o@x.test", Password = "x", RoleID = AuthDataSeeder.PosSupervisorSeedId,
            IsActive = true, OrganizationId = Guid.NewGuid(), CreatedDate = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        await SeedAsync(db);

        var ids = await db.Roles.IgnoreQueryFilters().Where(r => r.RoleCode.StartsWith("POS_") || r.RoleCode == "STORE_MANAGER")
            .Select(r => r.RoleID).ToListAsync();
        ids.Should().HaveCount(3).And.NotContain([AuthDataSeeder.PosCashierSeedId, AuthDataSeeder.PosSupervisorSeedId]).And.OnlyHaveUniqueItems();
        // The pre-A38 roles keep their seed ids as before.
        (await db.Roles.IgnoreQueryFilters().SingleAsync(r => r.RoleCode == "SUPPLY_DEPT_ADMIN")).RoleID.Should().Be((int)EnumRole.SupplyDeptAdmin);
    }
}
