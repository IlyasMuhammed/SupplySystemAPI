using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Tenancy.Data;
using SMS.Modules.Tenancy.Domain;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Tenancy.Tests;

/// <summary>A42 D-3 — FEATURE_GENERAL_LEDGER: a sub-feature of MODULE_FINANCE on every plan that has Finance; organizations
/// that existed before A42 follow their plan (and are on only while Finance is on).</summary>
public class GeneralLedgerFeatureCatalogTests
{
    private const string Feature = ModuleCodes.GeneralLedger;

    private static TenancyDbContext NewDb() =>
        new(new DbContextOptionsBuilder<TenancyDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static TenancyDataSeeder Seeder(TenancyDbContext db) =>
        new(db, Mock.Of<IUserQueryService>(u => u.GetFirstSystemAdminUserIdAsync() == Task.FromResult<int?>(null)));

    private static OrganizationFeature Row(TenancyDbContext db, Guid orgId, string code)
    {
        var def = db.FeatureDefinitions.AsNoTracking().Single(f => f.FeatureCode == code);
        return db.OrganizationFeatures.AsNoTracking().Single(f => f.OrganizationId == orgId && f.FeatureDefinitionId == def.Id);
    }

    [Fact]
    public void Catalog_entry()
    {
        TenancyFeatureCatalog.Validate();
        Feature.Should().Be("FEATURE_GENERAL_LEDGER");
        TenancyFeatureCatalog.ByCode[Feature].Should().Match<TenancyFeatureCatalog.Entry>(e =>
            e.Category == TenancyFeatureCatalog.CategoryFeature && !e.IsCore && !e.IsAlwaysOn && e.IsAvailable && e.Parent == ModuleCodes.Finance);
        TenancyFeatureCatalog.PlanDefaultForExistingOrgs.Should().Contain(Feature);
    }

    [Theory]
    [InlineData(TenancyFeatureCatalog.PlanBasic, false)]
    [InlineData(TenancyFeatureCatalog.PlanStandard, true)]
    [InlineData(TenancyFeatureCatalog.PlanEnterprise, true)]
    public void Plans_with_finance_include_it(string plan, bool gl)
    {
        var codes = TenancyFeatureCatalog.GetDefaultEnabledCodes(plan);
        codes.Contains(ModuleCodes.Finance).Should().Be(gl);
        codes.Contains(Feature).Should().Be(gl);
    }

    [Theory]
    [InlineData(TenancyFeatureCatalog.PlanBasic, false)]
    [InlineData(TenancyFeatureCatalog.PlanStandard, true)]
    [InlineData(TenancyFeatureCatalog.PlanEnterprise, true)]
    public async Task Existing_orgs_follow_their_plan(string plan, bool gl)
    {
        var db = NewDb();
        await Seeder(db).SeedAsync();
        var org = new Organization { Id = Guid.NewGuid(), OrgCode = "OLD-" + plan, OrgName = plan, Plan = plan, IsActive = true, CreatedDate = DateTime.UtcNow };
        db.Organizations.Add(org);
        await db.SaveChangesAsync();
        await Seeder(db).SeedAsync();
        // The org as A41 left it: no row for the A42 feature.
        var featureId = db.FeatureDefinitions.Single(f => f.FeatureCode == Feature).Id;
        db.OrganizationFeatures.RemoveRange(db.OrganizationFeatures.Where(f => f.OrganizationId == org.Id && f.FeatureDefinitionId == featureId));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Seeder(db).SeedAsync();

        Row(db, org.Id, Feature).Should().Match<OrganizationFeature>(r => r.IsLicensed == gl && r.IsEnabled == gl);

        // Idempotent; an org admin switching it off keeps it off.
        if (gl)
        {
            var row = db.OrganizationFeatures.Single(f => f.OrganizationId == org.Id && f.FeatureDefinitionId == featureId);
            row.IsEnabled = false;
            await db.SaveChangesAsync();
        }
        var count = db.OrganizationFeatures.Count();
        await Seeder(db).SeedAsync();
        db.OrganizationFeatures.Count().Should().Be(count);
        Row(db, org.Id, Feature).IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task An_existing_org_with_finance_switched_off_gets_it_licensed_but_off()
    {
        var db = NewDb();
        await Seeder(db).SeedAsync();
        var org = new Organization { Id = Guid.NewGuid(), OrgCode = "OLD-FOFF", OrgName = "Finance off", Plan = TenancyFeatureCatalog.PlanStandard,
            IsActive = true, CreatedDate = DateTime.UtcNow };
        db.Organizations.Add(org);
        await db.SaveChangesAsync();
        await Seeder(db).SeedAsync();
        var financeId = db.FeatureDefinitions.Single(f => f.FeatureCode == ModuleCodes.Finance).Id;
        var featureId = db.FeatureDefinitions.Single(f => f.FeatureCode == Feature).Id;
        db.OrganizationFeatures.Single(f => f.OrganizationId == org.Id && f.FeatureDefinitionId == financeId).IsEnabled = false;
        db.OrganizationFeatures.RemoveRange(db.OrganizationFeatures.Where(f => f.OrganizationId == org.Id && f.FeatureDefinitionId == featureId));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Seeder(db).SeedAsync();

        Row(db, org.Id, Feature).Should().Match<OrganizationFeature>(r => r.IsLicensed && !r.IsEnabled);
    }

    [Fact]
    public async Task A_fresh_scm_demo_gets_it()
    {
        var db = NewDb();
        await Seeder(db).SeedAsync();
        Row(db, TenantDefaults.ScmDemoOrganizationId, Feature).IsEnabled.Should().BeTrue();
    }
}
