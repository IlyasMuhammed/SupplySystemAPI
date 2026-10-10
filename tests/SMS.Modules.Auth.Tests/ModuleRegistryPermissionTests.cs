using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using SMS.Modules.Auth.Data;
using SMS.Modules.Auth.Domain;
using SMS.Modules.Auth.Models;
using SMS.Modules.Auth.Repositories;
using SMS.Modules.Auth.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Auth.Tests;

/// <summary>A37 D-14 / D-17 / §7 — module/customer permissions, their grants, Permissions.ModuleCode and moduleEnabled.</summary>
public class ModuleRegistryPermissionTests
{
    private static AuthDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AuthDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new StaticTenantContext { IsSuperAdmin = true });

    private static Task Invoke(AuthDataSeeder seeder, string method) =>
        (Task)typeof(AuthDataSeeder).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(seeder, null)!;

    private static async Task SeedAsync(AuthDbContext db)
    {
        var seeder = new AuthDataSeeder(db, new PasswordHasher<UserAccount>());
        await Invoke(seeder, "SeedPermissionsAsync");
        await Invoke(seeder, "SeedRolesAsync");
        await Invoke(seeder, "SeedRolePermissionsAsync");
        await seeder.SeedCustomerGrantsAsync();
    }

    private static async Task<HashSet<string>> AllowedAsync(AuthDbContext db, string roleCode)
    {
        var role = await db.Roles.IgnoreQueryFilters().SingleAsync(r => r.RoleCode == roleCode);
        return (await (from rp in db.RolePermissions.IgnoreQueryFilters()
                       join p in db.Permissions on rp.PermissionID equals p.PermissionID
                       where rp.RoleID == role.RoleID && rp.IsAllowed
                       select p.Code).ToListAsync()).ToHashSet();
    }

    [Fact]
    public async Task Module_permissions_go_to_the_admins_and_customer_permissions_follow_supplier_ones()
    {
        await using var db = NewDb();
        await SeedAsync(db);

        foreach (var admin in new[] { "SYSTEM_ADMIN", "ORG_ADMIN" })
            (await AllowedAsync(db, admin)).Should().Contain([PermissionCodes.MODULES_VIEW, PermissionCodes.MODULES_MANAGE]);

        (await AllowedAsync(db, "PROCUREMENT_MANAGER")).Should().Contain([PermissionCodes.CUSTOMER_VIEW, PermissionCodes.CUSTOMER_CREATE,
            PermissionCodes.CUSTOMER_EDIT, PermissionCodes.CUSTOMER_DEACTIVATE]).And.NotContain(PermissionCodes.MODULES_MANAGE);
        (await AllowedAsync(db, "PURCHASE_OFFICER")).Should().Contain(PermissionCodes.CUSTOMER_VIEW).And.NotContain(PermissionCodes.CUSTOMER_EDIT);
        (await AllowedAsync(db, "REQUESTER")).Should().NotContain(PermissionCodes.CUSTOMER_VIEW);
    }

    [Fact]
    public async Task Customer_grant_switched_off_stays_off_and_a_custom_supplier_role_gets_customers()
    {
        await using var db = NewDb();
        db.Roles.Add(new Role { RoleID = 600, Name = "Buyer", RoleCode = "BUYER_X", IsActive = true, OrganizationId = Guid.NewGuid() });
        await db.SaveChangesAsync();
        await SeedAsync(db);
        var supplierView = await db.Permissions.SingleAsync(p => p.Code == PermissionCodes.SUPPLIER_VIEW);
        db.RolePermissions.Add(new RolePermission { RoleID = 600, PermissionID = supplierView.PermissionID, IsAllowed = true, OrganizationId = TenantDefaults.ScmDemoOrganizationId });
        var auditor = await db.Roles.IgnoreQueryFilters().SingleAsync(r => r.RoleCode == "AUDITOR");
        var customerView = await db.Permissions.SingleAsync(p => p.Code == PermissionCodes.CUSTOMER_VIEW);
        (await db.RolePermissions.SingleAsync(rp => rp.RoleID == auditor.RoleID && rp.PermissionID == customerView.PermissionID)).IsAllowed = false;
        await db.SaveChangesAsync();

        await new AuthDataSeeder(db, new PasswordHasher<UserAccount>()).SeedCustomerGrantsAsync();   // the next start

        (await AllowedAsync(db, "BUYER_X")).Should().Contain(PermissionCodes.CUSTOMER_VIEW);
        (await AllowedAsync(db, "AUDITOR")).Should().NotContain(PermissionCodes.CUSTOMER_VIEW);
    }

    [Fact]
    public async Task Seeder_sets_every_permissions_module_code()
    {
        await using var db = NewDb();
        await SeedAsync(db);

        (await db.Permissions.Where(p => p.ModuleCode == null).Select(p => p.Code).ToListAsync()).Should().BeEmpty();
        (await db.Permissions.SingleAsync(p => p.Code == PermissionCodes.PROD_VIEW)).ModuleCode.Should().Be(ModuleCodes.Manufacturing);
        (await db.Permissions.SingleAsync(p => p.Code == PermissionCodes.CUSTOMER_EDIT)).ModuleCode.Should().Be(ModuleCodes.Customers);
    }

    [Fact]
    public async Task Role_permission_list_carries_module_code_and_whether_the_module_is_on()
    {
        await using var db = NewDb();
        await SeedAsync(db);
        var orgId = Guid.NewGuid();
        var snapshots = new Mock<ITenantSnapshotProvider>();
        snapshots.Setup(s => s.GetSnapshotAsync(orgId)).ReturnsAsync(new TenantSnapshot(true,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ModuleCodes.MasterData, ModuleCodes.Inventory }));
        var tenant = new StaticTenantContext { OrganizationId = orgId };
        var settings = Options.Create(new AppSettings { Secret = "test-secret-key-must-be-at-least-32-bytes!" });
        var hasher = new PasswordHasher<UserAccount>();
        var svc = new AuthService(new AuthRepository(db, hasher), Mock.Of<IEmailService>(), settings, new TokenService(settings), hasher,
            Mock.Of<IOrganizationStatusService>(), Mock.Of<ISuperAdminService>(), snapshots: snapshots.Object, tenant: tenant);
        var orgAdmin = await db.Roles.IgnoreQueryFilters().SingleAsync(r => r.RoleCode == "ORG_ADMIN");

        var items = (await svc.GetRoleDetailAsync(orgAdmin.RoleID)).PermissionGroups.SelectMany(g => g.Permissions).ToList();

        items.Single(p => p.Code == PermissionCodes.PROD_VIEW).Should().Match<PermissionItemModel>(p => p.ModuleCode == ModuleCodes.Manufacturing && !p.ModuleEnabled);
        items.Single(p => p.Code == PermissionCodes.INVENTORY_VIEW).ModuleEnabled.Should().BeTrue();

        var legacy = await svc.GetPermissionsByRoleAsync(orgAdmin.RoleID, new AuthCaller(1, orgId, true, new HashSet<string>()));
        legacy.Single(p => p.Code == PermissionCodes.SERVICE_ORDER_VIEW).ModuleEnabled.Should().BeFalse();
    }
}
