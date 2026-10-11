using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using SMS.Modules.Tenancy.Data;
using SMS.Modules.Tenancy.Domain;
using SMS.Modules.Tenancy.Models;
using SMS.Modules.Tenancy.Repositories;
using SMS.Modules.Tenancy.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Tenancy.Tests;

/// <summary>
/// A38 D-2 / API-CONTRACT §10 — MODULE_POS in the registry: available, depends on Inventory/Customers/Finance, not in
/// Basic, default for NEW Standard/Enterprise organizations, and never licensed for an existing organization by the
/// startup backfill (the super admin licenses it by hand). Sub-features: core, toggleable, coming soon.
/// </summary>
public class PosModuleCatalogTests
{
    private const string Pos = ModuleCodes.Pos;

    private static readonly string[] Core =
        ["FEATURE_POS_ORDERS", "FEATURE_POS_SHIFTS", "FEATURE_POS_CASHIER_PIN", "FEATURE_POS_PAYMENTS", "FEATURE_POS_OFFLINE"];
    private static readonly string[] Toggleable =
        [PosFeatureCodes.Returns, PosFeatureCodes.CreditSales, PosFeatureCodes.Barcode, PosFeatureCodes.MultiStore,
         PosFeatureCodes.CreditNotes, PosFeatureCodes.CrossStoreReturns]; // A39
    private static readonly string[] A39 = [PosFeatureCodes.CreditNotes, PosFeatureCodes.CrossStoreReturns];
    private static readonly string[] ComingSoon = ["FEATURE_POS_ANALYTICS", "FEATURE_POS_PROMOTIONS", "FEATURE_POS_LOYALTY"];

    private static TenancyDbContext NewInMemoryDb() =>
        new(new DbContextOptionsBuilder<TenancyDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static TenancyDataSeeder Seeder(TenancyDbContext db) =>
        new(db, Mock.Of<IUserQueryService>(u => u.GetFirstSystemAdminUserIdAsync() == Task.FromResult<int?>(null)));

    private static OrganizationFeature Row(TenancyDbContext db, Guid orgId, string code)
    {
        var def = db.FeatureDefinitions.AsNoTracking().Single(f => f.FeatureCode == code);
        return db.OrganizationFeatures.AsNoTracking().Single(f => f.OrganizationId == orgId && f.FeatureDefinitionId == def.Id);
    }

    // ── Catalog ──────────────────────────────────────────────────────────────

    [Fact]
    public void Pos_is_an_available_switchable_module_and_the_catalog_stays_valid()
    {
        TenancyFeatureCatalog.Validate();
        var entry = TenancyFeatureCatalog.ByCode[Pos];
        entry.Category.Should().Be(TenancyFeatureCatalog.CategoryModule);
        entry.IsAvailable.Should().BeTrue();
        entry.IsAlwaysOn.Should().BeFalse();
        entry.IsCore.Should().BeFalse();
        entry.Parent.Should().BeNull();
    }

    [Fact]
    public void Pos_depends_on_inventory_customers_and_finance()
    {
        TenancyFeatureCatalog.Dependencies.Where(d => d.Dependent == Pos).Select(d => d.RequiredBy)
            .Should().BeEquivalentTo([ModuleCodes.Inventory, ModuleCodes.Customers, ModuleCodes.Finance]);
    }

    [Fact]
    public void Sub_features_are_core_toggleable_or_coming_soon_under_pos()
    {
        var subs = TenancyFeatureCatalog.Catalog.Where(e => e.Parent == Pos).ToList();
        subs.Select(e => e.Code).Should().BeEquivalentTo(Core.Concat(Toggleable).Concat(ComingSoon));

        foreach (var code in Core)
            TenancyFeatureCatalog.ByCode[code].Should().Match<TenancyFeatureCatalog.Entry>(e => e.IsCore && e.IsAvailable, code);
        foreach (var code in Toggleable)
            TenancyFeatureCatalog.ByCode[code].Should().Match<TenancyFeatureCatalog.Entry>(e => !e.IsCore && e.IsAvailable, code);
        foreach (var code in ComingSoon)
            TenancyFeatureCatalog.ByCode[code].Should().Match<TenancyFeatureCatalog.Entry>(e => !e.IsCore && !e.IsAvailable, code);
    }

    [Theory]
    [InlineData(TenancyFeatureCatalog.PlanBasic, false)]
    [InlineData(TenancyFeatureCatalog.PlanStandard, true)]
    [InlineData(TenancyFeatureCatalog.PlanEnterprise, true)]
    public void Plans_include_pos_and_its_available_sub_features_except_basic(string plan, bool included)
    {
        var codes = TenancyFeatureCatalog.GetDefaultEnabledCodes(plan);
        codes.Contains(Pos).Should().Be(included);
        foreach (var code in Core.Concat(Toggleable)) codes.Contains(code).Should().Be(included, code);
        codes.Should().NotContain(ComingSoon);
    }

    // ── Existing organizations stay unlicensed ───────────────────────────────

    [Theory]
    [InlineData(TenancyFeatureCatalog.PlanStandard)]
    [InlineData(TenancyFeatureCatalog.PlanEnterprise)]
    public async Task An_existing_org_is_not_licensed_for_pos_by_the_startup_backfill(string plan)
    {
        var db = NewInMemoryDb();
        var seeder = Seeder(db);
        await seeder.SeedAsync();
        // An organization that already exists when A38 ships: the backfill creates its rows on the next start.
        var org = new Organization { Id = Guid.NewGuid(), OrgCode = "OLD-" + plan, OrgName = plan, Plan = plan, IsActive = true, CreatedDate = DateTime.UtcNow };
        db.Organizations.Add(org);
        await db.SaveChangesAsync();

        await seeder.SeedAsync();

        Row(db, org.Id, Pos).Should().Match<OrganizationFeature>(r => !r.IsLicensed && !r.IsEnabled);
        foreach (var code in Toggleable)
            Row(db, org.Id, code).Should().Match<OrganizationFeature>(r => !r.IsLicensed && !r.IsEnabled, code);
        // Everything else still follows the plan as before.
        Row(db, org.Id, ModuleCodes.Services).Should().Match<OrganizationFeature>(r => r.IsLicensed && r.IsEnabled);

        var snapshot = (await new TenantSnapshotProvider(db, new MemoryCache(new MemoryCacheOptions())).GetSnapshotAsync(org.Id))!;
        snapshot.EnabledFeatureCodes.Should().NotContain(Pos).And.NotContain(Core).And.NotContain(Toggleable);
    }

    [Fact]
    public async Task An_existing_scm_demo_missing_its_pos_row_gets_it_unlicensed()
    {
        var db = NewInMemoryDb();
        await Seeder(db).SeedAsync();
        var posId = db.FeatureDefinitions.Single(f => f.FeatureCode == Pos).Id;
        db.OrganizationFeatures.Remove(db.OrganizationFeatures.Single(f => f.OrganizationId == TenantDefaults.ScmDemoOrganizationId && f.FeatureDefinitionId == posId));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Seeder(db).SeedAsync();

        Row(db, TenantDefaults.ScmDemoOrganizationId, Pos).Should().Match<OrganizationFeature>(r => !r.IsLicensed && !r.IsEnabled);
    }

    [Fact]
    public async Task An_already_unlicensed_pos_row_is_left_alone_on_every_start()
    {
        // The A37 state: MODULE_POS existed as "coming soon", so every org has an unlicensed row. A38 must not flip it.
        var db = NewInMemoryDb();
        var seeder = Seeder(db);
        await seeder.SeedAsync();
        var org = new Organization { Id = Guid.NewGuid(), OrgCode = "OLD", OrgName = "Old", Plan = TenancyFeatureCatalog.PlanEnterprise, IsActive = true, CreatedDate = DateTime.UtcNow };
        db.Organizations.Add(org);
        await db.SaveChangesAsync();
        await seeder.SeedAsync();

        await seeder.SeedAsync();
        await seeder.SeedAsync();

        Row(db, org.Id, Pos).IsLicensed.Should().BeFalse();
    }

    // ── New organizations get it from their plan ─────────────────────────────

    [Fact]
    public async Task A_fresh_database_scm_demo_is_a_new_enterprise_org_and_gets_pos()
    {
        var db = NewInMemoryDb();
        await Seeder(db).SeedAsync();

        Row(db, TenantDefaults.ScmDemoOrganizationId, Pos).Should().Match<OrganizationFeature>(r => r.IsLicensed && r.IsEnabled);
    }

    [Theory]
    [InlineData(TenancyFeatureCatalog.PlanBasic, false)]
    [InlineData(TenancyFeatureCatalog.PlanStandard, true)]
    [InlineData(TenancyFeatureCatalog.PlanEnterprise, true)]
    public async Task A_new_org_gets_pos_from_its_plan(string plan, bool expected)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<TenancyDbContext>().UseSqlite(connection).Options;
        using (var init = new TenancyDbContext(options)) init.Database.EnsureCreated();

        Guid orgId;
        await using (var db = new TenancyDbContext(options))
        {
            await Seeder(db).SeedAsync();
            var provisioning = new Mock<IOrgUserProvisioningService>();
            var repo = new TenancyRepository(db, provisioning.Object, Mock.Of<IWorkflowSeedingService>());
            orgId = (await new TenancyService(repo, provisioning.Object, Mock.Of<ITenantSnapshotProvider>())
                .CreateOrganizationWithAdminAsync(new CreateOrganizationRequest
                {
                    OrgCode = "POS-" + plan, OrgName = plan, Plan = plan, AdminFirstName = "A", AdminEmail = $"a@pos-{plan}.test"
                }, 1)).OrganizationId;
        }

        await using var check = new TenancyDbContext(options);
        Row(check, orgId, Pos).Should().Match<OrganizationFeature>(r => r.IsLicensed == expected && r.IsEnabled == expected);

        // A later start must not take it away (the backfill only fills missing rows).
        await Seeder(check).SeedAsync();
        Row(check, orgId, Pos).IsLicensed.Should().Be(expected);

        var snapshot = (await new TenantSnapshotProvider(check, new MemoryCache(new MemoryCacheOptions())).GetSnapshotAsync(orgId))!;
        snapshot.EnabledFeatureCodes.Contains(Pos).Should().Be(expected);
        if (expected) snapshot.EnabledFeatureCodes.Should().Contain(Core).And.Contain(Toggleable).And.NotContain(ComingSoon);
    }

    // ── A39: store credit and cross-store returns ────────────────────────────

    [Fact]
    public void The_a39_sub_features_are_toggleable_pos_features_with_the_shared_codes()
    {
        PosFeatureCodes.CreditNotes.Should().Be("FEATURE_POS_CREDIT_NOTES");
        PosFeatureCodes.CrossStoreReturns.Should().Be("FEATURE_POS_CROSS_STORE_RETURNS");
        foreach (var code in A39)
            TenancyFeatureCatalog.ByCode[code].Should().Match<TenancyFeatureCatalog.Entry>(e =>
                e.Parent == Pos && !e.IsCore && e.IsAvailable && !e.IsAlwaysOn && !e.IsHidden && e.Category == TenancyFeatureCatalog.CategoryFeature, code);
    }

    [Fact]
    public async Task Existing_orgs_get_the_a39_rows_on_where_pos_is_on_licensed_off_where_switched_off_and_absent_where_unlicensed()
    {
        var db = NewInMemoryDb();
        var seeder = Seeder(db);
        await seeder.SeedAsync();

        // Three organizations as they stand when A39 ships: POS licensed + on, POS licensed but switched off, no POS.
        Organization Org(string code) => new() { Id = Guid.NewGuid(), OrgCode = code, OrgName = code, Plan = TenancyFeatureCatalog.PlanEnterprise, IsActive = true, CreatedDate = DateTime.UtcNow };
        var on = Org("POS-ON"); var off = Org("POS-OFF"); var none = Org("NO-POS");
        db.Organizations.AddRange(on, off, none);
        await db.SaveChangesAsync();
        await seeder.SeedAsync();

        var posId = db.FeatureDefinitions.Single(f => f.FeatureCode == Pos).Id;
        var returnsId = db.FeatureDefinitions.Single(f => f.FeatureCode == PosFeatureCodes.Returns).Id;
        foreach (var (org, enabled) in new[] { (on, true), (off, false) })
            foreach (var row in db.OrganizationFeatures.Where(f => f.OrganizationId == org.Id && (f.FeatureDefinitionId == posId || f.FeatureDefinitionId == returnsId)))
            {
                row.IsLicensed = true;
                row.IsEnabled  = enabled;
                row.EnabledAt  = enabled ? DateTime.UtcNow : null;
            }
        // Pretend the A39 rows were never created (the database as A38 left it).
        var a39Ids = db.FeatureDefinitions.Where(f => A39.Contains(f.FeatureCode)).Select(f => f.Id).ToList();
        db.OrganizationFeatures.RemoveRange(db.OrganizationFeatures.Where(f => a39Ids.Contains(f.FeatureDefinitionId)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Seeder(db).SeedAsync();

        foreach (var code in A39)
        {
            Row(db, on.Id, code).Should().Match<OrganizationFeature>(r => r.IsLicensed && r.IsEnabled && r.EnabledAt != null, code);
            Row(db, off.Id, code).Should().Match<OrganizationFeature>(r => r.IsLicensed && !r.IsEnabled, code);
            Row(db, none.Id, code).Should().Match<OrganizationFeature>(r => !r.IsLicensed && !r.IsEnabled, code);
        }
        Row(db, none.Id, Pos).Should().Match<OrganizationFeature>(r => !r.IsLicensed && !r.IsEnabled, "an org without POS stays as it is");

        var snapshot = (await new TenantSnapshotProvider(db, new MemoryCache(new MemoryCacheOptions())).GetSnapshotAsync(on.Id))!;
        snapshot.EnabledFeatureCodes.Should().Contain(A39);

        // Idempotent: an org admin switching store credit off keeps it off on later starts.
        var credit = db.OrganizationFeatures.Single(f => f.OrganizationId == on.Id && f.FeatureDefinitionId == a39Ids[0]);
        credit.IsEnabled = false;
        credit.EnabledAt = null;
        await db.SaveChangesAsync();
        var rows = db.OrganizationFeatures.Count();
        await Seeder(db).SeedAsync();
        await Seeder(db).SeedAsync();
        db.OrganizationFeatures.Count().Should().Be(rows);
        db.OrganizationFeatures.AsNoTracking().Single(f => f.Id == credit.Id).IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task The_seeded_dependency_table_carries_the_pos_dependencies()
    {
        var db = NewInMemoryDb();
        await Seeder(db).SeedAsync();

        db.FeatureDependencies.Where(d => d.FeatureCode == Pos).Select(d => d.DependsOnCode).ToList()
            .Should().BeEquivalentTo([ModuleCodes.Inventory, ModuleCodes.Customers, ModuleCodes.Finance]);
    }
}
