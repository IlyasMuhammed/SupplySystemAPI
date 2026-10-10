using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Tenancy.Data;
using SMS.Modules.Tenancy.Domain;
using SMS.Modules.Tenancy.Models;
using SMS.Modules.Tenancy.Repositories;
using SMS.Modules.Tenancy.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Tenancy.Tests;

/// <summary>A36 D-11 (A36-X-01) — the MODULE_SERVICES feature: catalog, plans, dependency, backfill, any-of gate.</summary>
public class ServiceOrdersFeatureTests
{
    private const string Services  = "MODULE_SERVICES";
    private const string Inventory = "MODULE_INVENTORY";

    private static (TenancyDbContext Db, TenancyDataSeeder Seeder, ITenancyService Service) New()
    {
        var db = new TenancyDbContext(new DbContextOptionsBuilder<TenancyDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var users = Mock.Of<IUserQueryService>(u => u.GetFirstSystemAdminUserIdAsync() == Task.FromResult<int?>(null));
        var provisioning = new Mock<IOrgUserProvisioningService>();
        var repo = new TenancyRepository(db, provisioning.Object, Mock.Of<IWorkflowSeedingService>());
        return (db, new TenancyDataSeeder(db, users), new TenancyService(repo, provisioning.Object, Mock.Of<ITenantSnapshotProvider>()));
    }

    private static bool IsEnabled(TenancyDbContext db, Guid orgId, string code)
    {
        var feature = db.FeatureDefinitions.Single(f => f.FeatureCode == code);
        return db.OrganizationFeatures.Single(f => f.OrganizationId == orgId && f.FeatureDefinitionId == feature.Id).IsEnabled;
    }

    [Fact]
    public void Catalog_has_service_orders_as_a_module_next_to_manufacturing()
    {
        var entry = TenancyFeatureCatalog.Catalog.Single(e => e.Code == Services);

        entry.Name.Should().Be("Service Orders");
        entry.Category.Should().Be(TenancyFeatureCatalog.CategoryModule);
        entry.IsCore.Should().BeFalse();
        entry.DisplayOrder.Should().Be(117);
        entry.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Service_orders_depend_on_inventory()
    {
        TenancyFeatureCatalog.Dependencies.Should().Contain((Services, Inventory));
    }

    [Theory]
    [InlineData(TenancyFeatureCatalog.PlanBasic,      false)]
    [InlineData(TenancyFeatureCatalog.PlanStandard,   true)]
    [InlineData(TenancyFeatureCatalog.PlanEnterprise, true)]
    public void Plans_include_service_orders_except_basic(string plan, bool included)
    {
        TenancyFeatureCatalog.GetDefaultEnabledCodes(plan).Contains(Services).Should().Be(included);
    }

    [Theory]
    [InlineData(TenancyFeatureCatalog.PlanBasic,    false)]
    [InlineData(TenancyFeatureCatalog.PlanStandard, true)]
    public async Task An_existing_org_gets_the_new_feature_row_from_its_plan_on_the_next_startup(string plan, bool expected)
    {
        // The catalog backfill (same path MODULE_MANUFACTURING took): an org that predates the feature has no row;
        // the next seeder run adds one, enabled exactly when the org's plan template enables it.
        var (db, seeder, _) = New();
        await seeder.SeedAsync();
        var org = new Organization
        {
            Id = Guid.NewGuid(), OrgCode = $"ORG-{plan}", OrgName = plan, Plan = plan, IsActive = true, CreatedDate = DateTime.UtcNow
        };
        db.Organizations.Add(org);
        await db.SaveChangesAsync();
        await seeder.SeedAsync();

        var services = db.FeatureDefinitions.Single(f => f.FeatureCode == Services);
        db.OrganizationFeatures.Remove(db.OrganizationFeatures.Single(f => f.OrganizationId == org.Id && f.FeatureDefinitionId == services.Id));
        await db.SaveChangesAsync();

        await seeder.SeedAsync();

        IsEnabled(db, org.Id, Services).Should().Be(expected);
    }

    [Fact]
    public async Task Enabling_service_orders_auto_enables_inventory()
    {
        var (db, seeder, service) = New();
        await seeder.SeedAsync();
        var org = db.Organizations.Single(o => o.OrgCode == "SCM-DEMO");
        // A37 D-3 — Demand, Warehouse and (through Warehouse) Logistics depend on Inventory too.
        foreach (var code in new[] { Services, "MODULE_MANUFACTURING", "MODULE_MIR", "MODULE_LOGISTICS", "MODULE_WAREHOUSE", "MODULE_DEMAND", Inventory })
            await service.UpdateFeaturesAsync(org.Id, [new FeatureToggleItem { FeatureCode = code, IsEnabled = false }], modifiedBy: 1);

        var result = await service.UpdateFeaturesAsync(
            org.Id, [new FeatureToggleItem { FeatureCode = Services, IsEnabled = true }], modifiedBy: 1);

        result.AutoEnabledDependencies.Should().Contain(Inventory);
        IsEnabled(db, org.Id, Inventory).Should().BeTrue();
    }

    [Fact]
    public async Task Inventory_cannot_be_disabled_while_service_orders_are_on()
    {
        var (db, seeder, service) = New();
        await seeder.SeedAsync();
        var org = db.Organizations.Single(o => o.OrgCode == "SCM-DEMO");
        foreach (var code in new[] { "MODULE_MANUFACTURING", "MODULE_MIR" })
            await service.UpdateFeaturesAsync(org.Id, [new FeatureToggleItem { FeatureCode = code, IsEnabled = false }], modifiedBy: 1);

        var act = () => service.UpdateFeaturesAsync(
            org.Id, [new FeatureToggleItem { FeatureCode = Inventory, IsEnabled = false }], modifiedBy: 1);

        (await act.Should().ThrowAsync<UnprocessableEntityException>()).Which.Message.Should().Contain(Services);
        IsEnabled(db, org.Id, Inventory).Should().BeTrue();
    }

    // ── [RequiresFeature] any-of (the BOM endpoints open for manufacturing OR service orders) ──

    private static async Task<bool> PassesAsync(RequiresFeatureAttribute attribute, params string[] enabled)
    {
        var orgId  = Guid.NewGuid();
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(t => t.IsSuperAdmin).Returns(false);
        tenant.SetupGet(t => t.OrganizationId).Returns(orgId);
        var snapshots = new Mock<ITenantSnapshotProvider>();
        snapshots.Setup(s => s.GetSnapshotAsync(orgId)).ReturnsAsync(new TenantSnapshot(true, enabled.ToHashSet()));

        var actionContext = new Microsoft.AspNetCore.Mvc.ActionContext(new DefaultHttpContext(), new RouteData(),
            new ActionDescriptor { EndpointMetadata = [attribute] });
        var context = new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), new object());
        var called = false;
        await new FeatureAuthorizationFilter(tenant.Object, snapshots.Object).OnActionExecutionAsync(context, () =>
        {
            called = true;
            return Task.FromResult(new ActionExecutedContext(actionContext, [], new object()));
        });
        return called;
    }

    [Theory]
    [InlineData("MODULE_MANUFACTURING")]
    [InlineData(Services)]
    public async Task An_any_of_gate_opens_for_either_feature(string enabled)
    {
        (await PassesAsync(new RequiresFeatureAttribute("MODULE_MANUFACTURING", Services), enabled)).Should().BeTrue();
    }

    [Fact]
    public async Task An_any_of_gate_refuses_when_neither_is_on()
    {
        // A37 — refused with the MODULE_NOT_LICENSED result (asserted in FeatureAuthorizationFilterTests), not a throw.
        (await PassesAsync(new RequiresFeatureAttribute("MODULE_MANUFACTURING", Services), Inventory)).Should().BeFalse();
    }

    [Fact]
    public void A_single_feature_gate_is_unchanged()
    {
        var attribute = new RequiresFeatureAttribute("MODULE_MIR");

        attribute.FeatureCode.Should().Be("MODULE_MIR");
        attribute.AnyOfFeatureCodes.Should().Equal("MODULE_MIR");
    }
}
