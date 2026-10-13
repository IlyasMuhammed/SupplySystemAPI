using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Tenancy.Data;
using SMS.Modules.Tenancy.Domain;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Tenancy.Tests;

/// <summary>A40 D-9 — MODULE_DATA_IMPORT (core, always on, every plan) and FEATURE_OPENING_BALANCE_IMPORT (Standard /
/// Enterprise), including the startup backfill of organizations that existed before A40.</summary>
public class DataImportModuleCatalogTests
{
    private const string Module = ModuleCodes.DataImport, Feature = ModuleCodes.OpeningBalanceImport;

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
    public void Catalog_entries()
    {
        TenancyFeatureCatalog.Validate();
        TenancyFeatureCatalog.ByCode[Module].Should().Match<TenancyFeatureCatalog.Entry>(e =>
            e.Category == TenancyFeatureCatalog.CategoryModule && e.IsCore && e.IsAlwaysOn && e.IsAvailable && e.Parent == null);
        TenancyFeatureCatalog.ByCode[Feature].Should().Match<TenancyFeatureCatalog.Entry>(e =>
            e.Category == TenancyFeatureCatalog.CategoryFeature && !e.IsCore && !e.IsAlwaysOn && e.IsAvailable && e.Parent == Module);
        TenancyFeatureCatalog.Dependencies.Should().NotContain(d => d.Dependent == Module || d.Dependent == Feature);
    }

    [Theory]
    [InlineData(TenancyFeatureCatalog.PlanBasic, false)]
    [InlineData(TenancyFeatureCatalog.PlanStandard, true)]
    [InlineData(TenancyFeatureCatalog.PlanEnterprise, true)]
    public void Plans(string plan, bool opening)
    {
        var codes = TenancyFeatureCatalog.GetDefaultEnabledCodes(plan);
        codes.Should().Contain(Module);
        codes.Contains(Feature).Should().Be(opening);
    }

    [Theory]
    [InlineData(TenancyFeatureCatalog.PlanBasic, false)]
    [InlineData(TenancyFeatureCatalog.PlanStandard, true)]
    [InlineData(TenancyFeatureCatalog.PlanEnterprise, true)]
    public async Task Existing_orgs_get_the_module_on_and_the_feature_from_their_plan(string plan, bool opening)
    {
        var db = NewDb();
        await Seeder(db).SeedAsync();
        var org = new Organization { Id = Guid.NewGuid(), OrgCode = "OLD-" + plan, OrgName = plan, Plan = plan, IsActive = true, CreatedDate = DateTime.UtcNow };
        db.Organizations.Add(org);
        await db.SaveChangesAsync();
        // The org as A39 left it: no rows for the two A40 entries.
        var ids = db.FeatureDefinitions.Where(f => f.FeatureCode == Module || f.FeatureCode == Feature).Select(f => f.Id).ToList();
        await Seeder(db).SeedAsync();
        db.OrganizationFeatures.RemoveRange(db.OrganizationFeatures.Where(f => f.OrganizationId == org.Id && ids.Contains(f.FeatureDefinitionId)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await Seeder(db).SeedAsync();

        Row(db, org.Id, Module).Should().Match<OrganizationFeature>(r => r.IsLicensed && r.IsEnabled);
        Row(db, org.Id, Feature).Should().Match<OrganizationFeature>(r => r.IsLicensed == opening && r.IsEnabled == opening);

        // Idempotent; an org admin switching the feature off keeps it off.
        if (opening)
        {
            var featureId = db.FeatureDefinitions.Single(d => d.FeatureCode == Feature).Id;
            var row = db.OrganizationFeatures.Single(f => f.OrganizationId == org.Id && f.FeatureDefinitionId == featureId);
            row.IsEnabled = false;
            await db.SaveChangesAsync();
        }
        var count = db.OrganizationFeatures.Count();
        await Seeder(db).SeedAsync();
        db.OrganizationFeatures.Count().Should().Be(count);
        Row(db, org.Id, Feature).IsEnabled.Should().BeFalse(opening ? "switched off by the org admin" : "Basic");
    }

    [Fact]
    public async Task A_fresh_scm_demo_gets_both()
    {
        var db = NewDb();
        await Seeder(db).SeedAsync();
        Row(db, TenantDefaults.ScmDemoOrganizationId, Module).IsEnabled.Should().BeTrue();
        Row(db, TenantDefaults.ScmDemoOrganizationId, Feature).IsEnabled.Should().BeTrue();
    }
}
