using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Tenancy.Data;
using SMS.Modules.Tenancy.Domain;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Tenancy.Tests;

/// <summary>A41 D-6 — MODULE_CRM becomes available (Standard / Enterprise), its 8 sub-features, the Customers dependency, and
/// existing organizations' untouched "Coming soon" rows taking their plan's default once.</summary>
public class CrmModuleCatalogTests
{
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
        TenancyFeatureCatalog.ByCode[ModuleCodes.Crm].Should().Match<TenancyFeatureCatalog.Entry>(e =>
            e.Category == TenancyFeatureCatalog.CategoryModule && !e.IsCore && !e.IsAlwaysOn && e.IsAvailable);
        TenancyFeatureCatalog.Dependencies.Should().Contain((ModuleCodes.Crm, ModuleCodes.Customers));
        var features = TenancyFeatureCatalog.Catalog.Where(e => e.Parent == ModuleCodes.Crm).ToList();
        features.Select(f => f.Code).Should().BeEquivalentTo([
            CrmFeatureCodes.LeadManagement, CrmFeatureCodes.OpportunityManagement, CrmFeatureCodes.ActivityTracking, CrmFeatureCodes.CampaignTracking,
            CrmFeatureCodes.LeadAssignment, CrmFeatureCodes.LeadScoring, CrmFeatureCodes.LeadWidget, CrmFeatureCodes.ExternalSync]);
        features.Where(f => f.IsCore).Select(f => f.Code).Should().BeEquivalentTo([
            CrmFeatureCodes.LeadManagement, CrmFeatureCodes.OpportunityManagement, CrmFeatureCodes.ActivityTracking]);
        features.Where(f => !f.IsAvailable).Select(f => f.Code).Should().BeEquivalentTo([
            CrmFeatureCodes.LeadAssignment, CrmFeatureCodes.LeadScoring, CrmFeatureCodes.LeadWidget, CrmFeatureCodes.ExternalSync]);
    }

    [Theory]
    [InlineData(TenancyFeatureCatalog.PlanBasic, false)]
    [InlineData(TenancyFeatureCatalog.PlanStandard, true)]
    [InlineData(TenancyFeatureCatalog.PlanEnterprise, true)]
    public void Plans(string plan, bool on)
    {
        var codes = TenancyFeatureCatalog.GetDefaultEnabledCodes(plan);
        codes.Contains(ModuleCodes.Crm).Should().Be(on);
        codes.Contains(CrmFeatureCodes.CampaignTracking).Should().Be(on);
        codes.Should().NotContain(CrmFeatureCodes.LeadScoring);
    }

    /// <summary>The organization as A40 left it: CRM was "Coming soon", so its row is unlicensed and off; no CRM sub-feature rows.</summary>
    private static async Task<Organization> OldOrgAsync(TenancyDbContext db, string plan)
    {
        await Seeder(db).SeedAsync();
        var org = new Organization { Id = Guid.NewGuid(), OrgCode = "OLD-" + plan, OrgName = plan, Plan = plan, IsActive = true, CreatedDate = DateTime.UtcNow };
        db.Organizations.Add(org);
        await db.SaveChangesAsync();
        await Seeder(db).SeedAsync();
        var crmIds = db.FeatureDefinitions.Where(f => f.FeatureCode == ModuleCodes.Crm || f.ParentModuleCode == ModuleCodes.Crm).Select(f => f.Id).ToList();
        db.OrganizationFeatures.RemoveRange(db.OrganizationFeatures.Where(f => f.OrganizationId == org.Id && crmIds.Contains(f.FeatureDefinitionId)));
        db.OrganizationFeatureHistory.RemoveRange(db.OrganizationFeatureHistory.Where(h => h.OrganizationId == org.Id && h.FeatureCode == ModuleCodes.Crm));
        var moduleId = db.FeatureDefinitions.Single(f => f.FeatureCode == ModuleCodes.Crm).Id;
        db.OrganizationFeatures.Add(new OrganizationFeature
        {
            Id = Guid.NewGuid(), OrganizationId = org.Id, FeatureDefinitionId = moduleId, IsLicensed = false, IsEnabled = false
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return org;
    }

    [Theory]
    [InlineData(TenancyFeatureCatalog.PlanBasic, false)]
    [InlineData(TenancyFeatureCatalog.PlanStandard, true)]
    [InlineData(TenancyFeatureCatalog.PlanEnterprise, true)]
    public async Task An_existing_orgs_untouched_coming_soon_row_takes_its_plan_once_and_the_sub_features_follow(string plan, bool on)
    {
        var db = NewDb();
        var org = await OldOrgAsync(db, plan);

        await Seeder(db).SeedAsync();

        Row(db, org.Id, ModuleCodes.Crm).Should().Match<OrganizationFeature>(r => r.IsLicensed == on && r.IsEnabled == on);
        // A core sub-feature's row is always on (A37 §1.3); it counts only while the module is on (IModuleGate).
        Row(db, org.Id, CrmFeatureCodes.LeadManagement).IsEnabled.Should().BeTrue();
        Row(db, org.Id, CrmFeatureCodes.CampaignTracking).IsEnabled.Should().Be(on);
        Row(db, org.Id, CrmFeatureCodes.LeadScoring).IsEnabled.Should().BeFalse("coming soon");
        db.OrganizationFeatureHistory.Count(h => h.OrganizationId == org.Id && h.FeatureCode == ModuleCodes.Crm && h.Action == FeatureHistoryActions.AutoEnabled)
            .Should().Be(on ? 1 : 0);
    }

    [Fact]
    public async Task A_row_an_administrator_changed_is_left_alone_and_the_step_runs_once()
    {
        var db = NewDb();
        var org = await OldOrgAsync(db, TenancyFeatureCatalog.PlanEnterprise);
        db.OrganizationFeatureHistory.Add(new OrganizationFeatureHistory
        {
            Id = Guid.NewGuid(), OrganizationId = org.Id, FeatureCode = ModuleCodes.Crm, Action = FeatureHistoryActions.Unlicensed, PerformedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        await Seeder(db).SeedAsync();
        Row(db, org.Id, ModuleCodes.Crm).IsEnabled.Should().BeFalse("someone decided about this organization's CRM before");

        var other = await OldOrgAsync(db, TenancyFeatureCatalog.PlanStandard);
        await Seeder(db).SeedAsync();
        var row = Row(db, other.Id, ModuleCodes.Crm);
        row.IsEnabled.Should().BeTrue();
        // Switched off by the org admin afterwards: the next start does not switch it back on.
        var tracked = db.OrganizationFeatures.Single(f => f.Id == row.Id);
        tracked.IsEnabled = false;
        tracked.DisabledAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        await Seeder(db).SeedAsync();
        Row(db, other.Id, ModuleCodes.Crm).IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task A_fresh_scm_demo_gets_the_crm()
    {
        var db = NewDb();
        await Seeder(db).SeedAsync();
        Row(db, TenantDefaults.ScmDemoOrganizationId, ModuleCodes.Crm).IsEnabled.Should().BeTrue();
        Row(db, TenantDefaults.ScmDemoOrganizationId, CrmFeatureCodes.ActivityTracking).IsEnabled.Should().BeTrue();
    }
}
