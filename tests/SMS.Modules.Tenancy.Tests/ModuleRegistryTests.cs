using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
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

/// <summary>A37 — module registry: catalog, MOD-01..11, licence vs. switch, history, RowVersion, grace, gate, filter.</summary>
public class ModuleRegistryTests
{
    private const string Logistics = ModuleCodes.Logistics, Warehouse = ModuleCodes.Warehouse, Mfg = ModuleCodes.Manufacturing,
        Services = ModuleCodes.Services, Bom = ModuleCodes.BomManagement, PickLists = ModuleCodes.PickLists;
    private const int Admin = 7;

    private sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public DateTime Utc { get; set; } = utc;
        public override DateTimeOffset GetUtcNow() => new(Utc, TimeSpan.Zero);
    }

    private sealed class Impact(string module, params ModuleImpactItem[] items) : IModuleImpactProvider
    {
        public string ModuleCode => module;
        public Task<IReadOnlyList<ModuleImpactItem>> GetInProgressAsync(Guid organizationId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ModuleImpactItem>>(items);
    }

    private sealed record Bed(TenancyDbContext Db, ModuleRegistryService Modules, ITenancyService Tenancy,
        Mock<ITenantSnapshotProvider> Snapshots, Guid OrgId, FixedClock Clock);

    private static async Task<Bed> NewAsync(string plan = TenancyFeatureCatalog.PlanEnterprise)
    {
        var db = new TenancyDbContext(new DbContextOptionsBuilder<TenancyDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var users = new Mock<IUserQueryService>();
        users.Setup(u => u.GetFirstSystemAdminUserIdAsync()).ReturnsAsync((int?)null);
        users.Setup(u => u.GetUsersAsync(It.IsAny<IReadOnlyList<int>>()))
            .ReturnsAsync((IReadOnlyList<int> ids) => ids.Select(i => new UserIdentity(i, $"User Name {i}")).ToList());
        var seeder = new TenancyDataSeeder(db, users.Object);
        await seeder.SeedAsync();

        var orgId = TenantDefaults.ScmDemoOrganizationId;
        if (plan != TenancyFeatureCatalog.PlanEnterprise)
        {
            orgId = Guid.NewGuid();
            db.Organizations.Add(new Organization { Id = orgId, OrgCode = "ORG-" + plan, OrgName = plan, Plan = plan, IsActive = true, CreatedDate = DateTime.UtcNow });
            await db.SaveChangesAsync();
            await seeder.SeedAsync();
        }

        var snapshots = new Mock<ITenantSnapshotProvider>();
        var clock = new FixedClock(new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc));
        var provisioning = new Mock<IOrgUserProvisioningService>();
        var repo = new TenancyRepository(db, provisioning.Object, Mock.Of<IWorkflowSeedingService>());
        var modules = new ModuleRegistryService(db, snapshots.Object, users.Object,
            [new Impact(Mfg, new ModuleImpactItem("Production orders in progress", 3))], clock);
        return new Bed(db, modules, new TenancyService(repo, provisioning.Object, snapshots.Object), snapshots, orgId, clock);
    }

    private static OrganizationFeature Row(Bed b, string code)
    {
        var def = b.Db.FeatureDefinitions.AsNoTracking().Single(f => f.FeatureCode == code);
        return b.Db.OrganizationFeatures.AsNoTracking().Single(f => f.OrganizationId == b.OrgId && f.FeatureDefinitionId == def.Id);
    }

    private static Task<ModuleCardModel> Disable(Bed b, string code, int? grace = null) =>
        b.Modules.DisableAsync(b.OrgId, code, new DisableModuleRequest { GraceDays = grace }, Admin);

    private static async Task<string> RefusalAsync(Func<Task> act) =>
        (await act.Should().ThrowAsync<BadRequestException>()).Which.Message;

    private static async Task<TenantSnapshot> SnapshotAsync(Bed b, DateTime? at = null) =>
        (await new TenantSnapshotProvider(b.Db, new MemoryCache(new MemoryCacheOptions()), new FixedClock(at ?? b.Clock.Utc))
            .GetSnapshotAsync(b.OrgId))!;

    // ── Catalog (D-1..D-3, §1.3) ─────────────────────────────────────────────

    [Fact]
    public void Catalog_is_valid_and_customers_is_always_on_in_every_plan()
    {
        TenancyFeatureCatalog.Validate();
        TenancyFeatureCatalog.ByCode[ModuleCodes.Customers].IsAlwaysOn.Should().BeTrue();
        foreach (var plan in TenancyFeatureCatalog.AllPlans)
            TenancyFeatureCatalog.GetDefaultEnabledCodes(plan).Should().Contain(ModuleCodes.Customers);
    }

    [Fact]
    public void Dependency_cycle_is_refused()
    {
        var act = () => TenancyFeatureCatalog.EnsureAcyclic([("A", "B"), ("B", "C"), ("C", "A")]);
        act.Should().Throw<InvalidOperationException>().WithMessage("*cycle*A -> B -> C -> A*");
    }

    [Theory]
    [InlineData(TenancyFeatureCatalog.PlanBasic, false)]
    [InlineData(TenancyFeatureCatalog.PlanStandard, true)]
    public void Bom_management_is_in_a_plan_exactly_with_manufacturing_or_services(string plan, bool expected)
    {
        var codes = TenancyFeatureCatalog.GetDefaultEnabledCodes(plan);
        codes.Contains(Bom).Should().Be(expected);
        codes.Should().NotContain("MODULE_POS").And.NotContain("FEATURE_BLANKET_ORDERS");
    }

    [Fact]
    public async Task Seeder_mirrors_dependencies_and_licenses_customers_for_a_basic_org()
    {
        var b = await NewAsync(TenancyFeatureCatalog.PlanBasic);

        b.Db.FeatureDependencies.Count().Should().Be(TenancyFeatureCatalog.Dependencies.Count);
        Row(b, ModuleCodes.Customers).Should().Match<OrganizationFeature>(r => r.IsLicensed && r.IsEnabled);
        Row(b, ModuleCodes.Finance).IsLicensed.Should().BeFalse();
        Row(b, Bom).IsEnabled.Should().BeFalse();
        b.Db.FeatureDefinitions.Single(f => f.FeatureCode == PickLists).ParentModuleCode.Should().Be(Logistics);
    }

    [Fact]
    public async Task A_new_sub_feature_follows_what_an_existing_org_has_now()
    {
        // A Basic org given Manufacturing by hand, then BOM management added to the catalog (simulated by removing it).
        var b = await NewAsync(TenancyFeatureCatalog.PlanBasic);
        await b.Tenancy.UpdateFeaturesAsync(b.OrgId, [new FeatureToggleItem { FeatureCode = Mfg, IsEnabled = true }], 1);
        b.Db.OrganizationFeatures.Remove(b.Db.OrganizationFeatures.Single(f => f.Id == Row(b, Bom).Id));
        await b.Db.SaveChangesAsync();

        await new TenancyDataSeeder(b.Db, Mock.Of<IUserQueryService>()).SeedAsync();

        Row(b, Bom).Should().Match<OrganizationFeature>(r => r.IsEnabled && r.IsLicensed && r.IsSystemManaged);
    }

    private static readonly string[] EnforcedSubFeatures =
    [
        Bom, ModuleCodes.QualityInspection, ModuleCodes.RfqManagement, PickLists, ModuleCodes.ShipmentTracking,
        ModuleCodes.PurchaseReturns, ModuleCodes.CreditManagement
    ];

    [Theory]
    [InlineData(TenancyFeatureCatalog.PlanStandard)]
    [InlineData(TenancyFeatureCatalog.PlanEnterprise)]
    public async Task A_new_standard_or_enterprise_org_can_write_boms_and_use_every_enforced_sub_feature(string plan)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<TenancyDbContext>().UseSqlite(connection).Options;
        using (var init = new TenancyDbContext(options)) init.Database.EnsureCreated();

        Guid orgId;
        await using (var db = new TenancyDbContext(options))
        {
            await new TenancyDataSeeder(db, Mock.Of<IUserQueryService>()).SeedAsync();
            var provisioning = new Mock<IOrgUserProvisioningService>();
            var repo = new TenancyRepository(db, provisioning.Object, Mock.Of<IWorkflowSeedingService>());
            orgId = (await new TenancyService(repo, provisioning.Object, Mock.Of<ITenantSnapshotProvider>())
                .CreateOrganizationWithAdminAsync(new CreateOrganizationRequest
                {
                    OrgCode = "NEW-" + plan, OrgName = plan, Plan = plan, AdminFirstName = "A", AdminEmail = $"a@{plan}.test"
                }, 1)).OrganizationId;
        }

        await using var check = new TenancyDbContext(options);
        var snapshot = (await new TenantSnapshotProvider(check, new MemoryCache(new MemoryCacheOptions())).GetSnapshotAsync(orgId))!;
        snapshot.EnabledFeatureCodes.Should().Contain(EnforcedSubFeatures).And.Contain([ModuleCodes.Customers, Mfg]);

        // The BomsController shape: MODULE_INVENTORY on the class, FEATURE_BOM_MANAGEMENT on a write.
        (await FilterAsync(snapshot, "POST", false, DateTime.UtcNow,
            new RequiresFeatureAttribute(ModuleCodes.Inventory), new RequiresFeatureAttribute(Bom))).Passed.Should().BeTrue();
    }

    [Fact]
    public async Task An_existing_org_gets_every_enforced_sub_feature_from_the_backfill()
    {
        // A Standard org that predates A37: its rows for the new sub-features do not exist yet.
        var b = await NewAsync(TenancyFeatureCatalog.PlanStandard);
        var newIds = b.Db.FeatureDefinitions.Where(f => EnforcedSubFeatures.Contains(f.FeatureCode)).Select(f => f.Id).ToList();
        b.Db.OrganizationFeatures.RemoveRange(b.Db.OrganizationFeatures.Where(f => f.OrganizationId == b.OrgId && newIds.Contains(f.FeatureDefinitionId)));
        await b.Db.SaveChangesAsync();
        b.Db.ChangeTracker.Clear();

        await new TenancyDataSeeder(b.Db, Mock.Of<IUserQueryService>()).SeedAsync();

        foreach (var code in EnforcedSubFeatures)
            Row(b, code).Should().Match<OrganizationFeature>(r => r.IsLicensed && r.IsEnabled, code);
        Row(b, Bom).IsSystemManaged.Should().BeTrue();
        (await SnapshotAsync(b)).EnabledFeatureCodes.Should().Contain(EnforcedSubFeatures);
    }

    // ── MOD-01..03, grace, MOD-06, MOD-10 ────────────────────────────────────

    [Fact]
    public async Task MOD01_always_on_module_cannot_be_disabled()
    {
        var b = await NewAsync();
        (await RefusalAsync(() => Disable(b, ModuleCodes.Inventory))).Should().Be("Inventory is always on.");
    }

    [Fact]
    public async Task MOD02_enable_needs_dependencies_and_MOD03_disable_needs_no_enabled_dependents()
    {
        var b = await NewAsync();
        (await RefusalAsync(() => Disable(b, Warehouse))).Should().Be("Disable Logistics first.");

        await Disable(b, Logistics, 0);
        await Disable(b, Warehouse, 0);
        (await RefusalAsync(() => b.Modules.EnableAsync(b.OrgId, Logistics, new(), Admin))).Should().Be("Enable Warehouse first.");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(366)]
    public async Task Grace_days_must_be_0_to_365(int days)
    {
        var b = await NewAsync();
        (await RefusalAsync(() => Disable(b, Logistics, days))).Should().Be("Grace period must be between 0 and 365 days.");
    }

    [Fact]
    public async Task Disable_starts_a_30_day_grace_logs_it_switches_features_off_and_invalidates_the_snapshot()
    {
        var b = await NewAsync();
        var card = await b.Modules.DisableAsync(b.OrgId, Logistics, new DisableModuleRequest { Notes = "season over" }, Admin);

        card.Status.Should().Be("GRACE");
        card.GraceEndsAt.Should().Be(b.Clock.Utc.AddDays(30));
        card.DisabledByName.Should().Be($"User Name {Admin}");
        Row(b, Logistics).Should().Match<OrganizationFeature>(r => !r.IsEnabled && r.IsLicensed && r.DisabledAt == b.Clock.Utc);
        b.Db.OrganizationFeatureHistory.Should().ContainSingle(h => h.FeatureCode == Logistics && h.Action == "DISABLED"
            && h.GraceDays == 30 && h.PerformedBy == Admin && h.Notes == "season over");
        b.Snapshots.Verify(s => s.Invalidate(b.OrgId), Times.Once);

        // MOD-06 — its features are effectively off (grace, blocked by the module) though their own rows are on.
        var snapshot = await SnapshotAsync(b);
        snapshot.EnabledFeatureCodes.Should().NotContain(Logistics).And.NotContain(PickLists);
        Row(b, PickLists).IsEnabled.Should().BeTrue();
        var access = snapshot.AccessFor(PickLists, b.Clock.Utc);
        access.Level.Should().Be(FeatureAccessLevel.Grace);
        access.BlockingCode.Should().Be(Logistics);

        var enabled = await b.Modules.GetEnabledAsync(b.OrgId);
        enabled.Modules.Should().NotContain(Logistics);
        enabled.Grace.Should().ContainSingle(g => g.Code == Logistics);
    }

    [Fact]
    public async Task MOD10_re_enable_clears_grace_and_the_switch_off_record()
    {
        var b = await NewAsync();
        await Disable(b, Logistics);
        var card = await b.Modules.EnableAsync(b.OrgId, Logistics, new(), Admin);

        card.Status.Should().Be("ACTIVE");
        Row(b, Logistics).Should().Match<OrganizationFeature>(r => r.IsEnabled && r.GracePeriodEndsAt == null && r.DisabledAt == null && r.EnabledBy == Admin);
        (await SnapshotAsync(b)).EnabledFeatureCodes.Should().Contain(PickLists);
    }

    [Fact]
    public async Task Coming_soon_and_unlicensed_modules_cannot_be_enabled()
    {
        var b = await NewAsync(TenancyFeatureCatalog.PlanBasic);
        (await RefusalAsync(() => b.Modules.EnableAsync(b.OrgId, "MODULE_POS", new(), Admin))).Should().Be("Point of Sale is not yet available.");
        (await RefusalAsync(() => b.Modules.EnableAsync(b.OrgId, Mfg, new(), Admin)))
            .Should().Be("Manufacturing is not included in your plan — contact your administrator.");
    }

    // ── Features (core, parent, history) ─────────────────────────────────────

    [Fact]
    public async Task Feature_toggle_rules()
    {
        var b = await NewAsync();
        (await RefusalAsync(() => b.Modules.SetFeatureAsync(b.OrgId, ModuleCodes.Inventory, "FEATURE_PRODUCT_VARIANTS",
            new ToggleModuleFeatureRequest { Enabled = false }, Admin))).Should().Be("Core features cannot be switched off.");

        var card = await b.Modules.SetFeatureAsync(b.OrgId, Logistics, PickLists, new ToggleModuleFeatureRequest { Enabled = false }, Admin);
        card.Features.Single(f => f.Code == PickLists).IsEnabled.Should().BeFalse();
        b.Db.OrganizationFeatureHistory.Should().Contain(h => h.FeatureCode == PickLists && h.Action == "FEATURE_DISABLED");

        await Disable(b, Logistics, 0);
        (await RefusalAsync(() => b.Modules.SetFeatureAsync(b.OrgId, Logistics, PickLists, new ToggleModuleFeatureRequest { Enabled = true }, Admin)))
            .Should().Be("Switch on Logistics first.");

        await b.Invoking(x => x.Modules.SetFeatureAsync(b.OrgId, Warehouse, PickLists, new ToggleModuleFeatureRequest { Enabled = true }, Admin))
            .Should().ThrowAsync<NotFoundException>();
    }

    // ── MOD-08 ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task MOD08_bom_management_follows_manufacturing_and_services_unless_set_by_hand()
    {
        var b = await NewAsync();
        await Disable(b, Mfg, 10);
        Row(b, Bom).IsEnabled.Should().BeTrue("Service Orders is still on");

        await Disable(b, Services, 10);
        Row(b, Bom).Should().Match<OrganizationFeature>(r => !r.IsEnabled && r.GracePeriodEndsAt == b.Clock.Utc.AddDays(10));
        b.Db.OrganizationFeatureHistory.Should().Contain(h => h.FeatureCode == Bom && h.Action == "AUTO_DISABLED" && h.PerformedBy == null);

        var card = await b.Modules.EnableAsync(b.OrgId, Mfg, new(), Admin);
        Row(b, Bom).Should().Match<OrganizationFeature>(r => r.IsEnabled && r.IsSystemManaged);
        card.Status.Should().Be("ACTIVE");

        // By hand: switched on while both drivers are off, it stays on when they go off again.
        await Disable(b, Mfg, 0);
        await b.Modules.SetFeatureAsync(b.OrgId, ModuleCodes.Inventory, Bom, new ToggleModuleFeatureRequest { Enabled = true }, Admin);
        await b.Modules.EnableAsync(b.OrgId, Mfg, new(), Admin);
        await Disable(b, Mfg, 0);
        Row(b, Bom).Should().Match<OrganizationFeature>(r => r.IsEnabled && !r.IsSystemManaged);

        var history = await b.Modules.GetHistoryAsync(b.OrgId, ModuleCodes.Inventory);
        history.Should().Contain(h => h.FeatureCode == Bom && h.PerformedByName == "System");
        history.Select(h => h.PerformedAt).Should().BeInDescendingOrder();
    }

    // ── Licence (super admin) vs switch (org admin) ──────────────────────────

    [Fact]
    public async Task Super_admin_toggle_is_the_licence_and_is_logged()
    {
        var b = await NewAsync();
        await b.Tenancy.UpdateFeaturesAsync(b.OrgId, [new FeatureToggleItem { FeatureCode = ModuleCodes.Integration, IsEnabled = false }], 1);

        Row(b, ModuleCodes.Integration).Should().Match<OrganizationFeature>(r => !r.IsLicensed && !r.IsEnabled && r.DisabledAt != null);
        b.Db.OrganizationFeatureHistory.Should().Contain(h => h.FeatureCode == ModuleCodes.Integration && h.Action == "UNLICENSED");
        (await RefusalAsync(() => b.Modules.EnableAsync(b.OrgId, ModuleCodes.Integration, new(), Admin)))
            .Should().Contain("not included in your plan");

        // Org admin switched Logistics off; the super admin's "on" re-licenses AND switches it back on.
        await Disable(b, Logistics, 5);
        await b.Tenancy.UpdateFeaturesAsync(b.OrgId, [new FeatureToggleItem { FeatureCode = Logistics, IsEnabled = true }], 1);
        Row(b, Logistics).Should().Match<OrganizationFeature>(r => r.IsLicensed && r.IsEnabled && r.GracePeriodEndsAt == null);

        var rows = await b.Tenancy.GetOrganizationFeaturesAsync(b.OrgId);
        rows.Single(r => r.FeatureCode == ModuleCodes.Integration).Status.Should().Be("NOT_LICENSED");
        rows.Single(r => r.FeatureCode == PickLists).ParentModuleCode.Should().Be(Logistics);
        rows.Single(r => r.FeatureCode == "MODULE_POS").Status.Should().Be("COMING_SOON");

        var all = await b.Modules.GetHistoryAsync(b.OrgId, null);
        all.Should().Contain(h => h.FeatureCode == ModuleCodes.Integration && h.Action == "UNLICENSED");
    }

    [Fact]
    public async Task Stale_row_version_is_a_conflict()
    {
        var b = await NewAsync();
        var act = () => b.Modules.DisableAsync(b.OrgId, Logistics,
            new DisableModuleRequest { RowVersion = Convert.ToBase64String([1, 2, 3, 4, 5, 6, 7, 8]) }, Admin);

        await act.Should().ThrowAsync<ConflictException>();
        Row(b, Logistics).IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task Cards_sort_and_carry_dependencies_and_impact_lists_dependents_and_providers()
    {
        var b = await NewAsync();
        await Disable(b, Logistics);
        var cards = await b.Modules.GetModulesAsync(b.OrgId);

        cards.Select(c => ModuleState.SortRank(c.Status)).Should().BeInAscendingOrder();
        cards.Should().NotContain(c => c.Code == "MODULE_PROCUREMENT");
        cards.Last().Status.Should().Be("COMING_SOON");
        cards.Single(c => c.Code == Warehouse).Dependents.Should().ContainSingle(d => d.Code == Logistics && !d.IsEnabled);
        cards.Single(c => c.Code == ModuleCodes.Inventory).Features.Single(f => f.Code == Bom).AutoManaged.Should().BeTrue();

        var impact = await b.Modules.GetImpactAsync(b.OrgId, Mfg);
        impact.InProgress.Should().ContainSingle(i => i.Label == "Production orders in progress" && i.Count == 3);
        impact.DefaultGraceDays.Should().Be(30);
        (await b.Modules.GetImpactAsync(b.OrgId, ModuleCodes.Inventory)).Dependents.Select(d => d.Code)
            .Should().Contain([Mfg, Services, ModuleCodes.Demand, Warehouse]);
    }

    // ── IModuleGate, grace expiry (MOD-05) ───────────────────────────────────

    [Fact]
    public async Task Module_gate_counts_grace_and_a_disabled_parent_as_off()
    {
        var b = await NewAsync();
        await Disable(b, Logistics);
        var snapshots = new TenantSnapshotProvider(b.Db, new MemoryCache(new MemoryCacheOptions()), b.Clock);
        var gate = new ModuleGate(snapshots);

        (await gate.IsEnabledAsync(b.OrgId, Warehouse)).Should().BeTrue();
        (await gate.IsEnabledAsync(b.OrgId, Logistics)).Should().BeFalse();
        (await gate.IsEnabledAsync(b.OrgId, PickLists)).Should().BeFalse();
    }

    [Fact]
    public async Task Grace_expiry_job_ends_expired_grace_and_logs_it()
    {
        var b = await NewAsync();
        await Disable(b, Logistics, 1);
        await Disable(b, Services, 30);

        b.Clock.Utc = b.Clock.Utc.AddDays(2);
        var cleared = await new ModuleGraceExpiryJob(b.Db, b.Snapshots.Object, clock: b.Clock).SweepAsync();

        cleared.Should().Be(1);
        Row(b, Logistics).Should().Match<OrganizationFeature>(r => r.GracePeriodEndsAt == null && r.DisabledAt != null);
        Row(b, Services).GracePeriodEndsAt.Should().NotBeNull();
        b.Db.OrganizationFeatureHistory.Should().ContainSingle(h => h.Action == "GRACE_EXPIRED" && h.FeatureCode == Logistics && h.PerformedBy == null);
        (await b.Modules.GetModulesAsync(b.OrgId)).Single(c => c.Code == Logistics).Status.Should().Be("DISABLED");
        (await SnapshotAsync(b)).AccessFor(Logistics, b.Clock.Utc).Level.Should().Be(FeatureAccessLevel.ReadOnly);
    }

    // ── Filter rules (D-6) ───────────────────────────────────────────────────

    private static async Task<(bool Passed, ObjectResult? Refusal)> FilterAsync(
        TenantSnapshot snapshot, string method, bool routeParam, DateTime now, params RequiresFeatureAttribute[] attributes)
    {
        var tenant = new Mock<ITenantContext>();
        tenant.SetupGet(t => t.OrganizationId).Returns(Guid.NewGuid());
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Items[SMS.Shared.Middleware.TenantMiddleware.SnapshotItemKey] = snapshot;
        var routeData = new RouteData();
        routeData.Values["controller"] = "X";
        if (routeParam) routeData.Values["uuid"] = Guid.NewGuid();
        var actionContext = new ActionContext(http, routeData, new ActionDescriptor { EndpointMetadata = attributes.Cast<object>().ToList() });
        var context = new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), new object());
        var passed = false;
        await new FeatureAuthorizationFilter(tenant.Object, Mock.Of<ITenantSnapshotProvider>(), new FixedClock(now))
            .OnActionExecutionAsync(context, () => { passed = true; return Task.FromResult(new ActionExecutedContext(actionContext, [], new object())); });
        return (passed, context.Result as ObjectResult);
    }

    [Fact]
    public async Task Filter_grace_read_and_create_rules()
    {
        var b = await NewAsync();
        await Disable(b, Logistics, 30);
        var snapshot = await SnapshotAsync(b);
        var now = b.Clock.Utc;
        var gate = new RequiresFeatureAttribute(Logistics);

        (await FilterAsync(snapshot, "GET", false, now, gate)).Passed.Should().BeTrue();
        (await FilterAsync(snapshot, "POST", true, now, gate)).Passed.Should().BeTrue("a write on an existing record passes during grace");
        var create = await FilterAsync(snapshot, "POST", false, now, gate);
        create.Passed.Should().BeFalse();
        create.Refusal!.StatusCode.Should().Be(403);
        var body = (ModuleNotEnabledResponse)create.Refusal.Value!;
        body.Message.Should().Be("The Logistics module is not enabled for your organization.");
        body.Result.Should().BeEquivalentTo(new { Module = Logistics, GraceEndsAt = (DateTime?)now.AddDays(30) });

        // A sub-feature is refused by its module, which the body names.
        var pick = await FilterAsync(snapshot, "POST", false, now, new RequiresFeatureAttribute(PickLists));
        ((ModuleNotEnabledResponse)pick.Refusal!.Value!).Result.Module.Should().Be(Logistics);

        // After grace (even before the job runs): reads only.
        var later = now.AddDays(31);
        (await FilterAsync(snapshot, "GET", false, later, gate)).Passed.Should().BeTrue();
        (await FilterAsync(snapshot, "PUT", true, later, gate)).Passed.Should().BeFalse();
    }

    [Fact]
    public async Task Filter_never_licensed_refuses_reads_and_every_attribute_must_pass()
    {
        var b = await NewAsync(TenancyFeatureCatalog.PlanBasic);
        var snapshot = await SnapshotAsync(b);
        var now = b.Clock.Utc;

        (await FilterAsync(snapshot, "GET", false, now, new RequiresFeatureAttribute(Mfg))).Passed.Should().BeFalse();
        (await FilterAsync(snapshot, "GET", false, now, new RequiresFeatureAttribute(Mfg, ModuleCodes.Inventory))).Passed.Should().BeTrue("any-of");
        (await FilterAsync(snapshot, "GET", false, now,
            new RequiresFeatureAttribute(ModuleCodes.Inventory), new RequiresFeatureAttribute(Bom))).Passed.Should().BeFalse("all attributes must pass");
    }

    [Fact]
    public void Requires_feature_allows_multiple()
    {
        typeof(RequiresFeatureAttribute).GetCustomAttributes(typeof(AttributeUsageAttribute), false)
            .Cast<AttributeUsageAttribute>().Single().AllowMultiple.Should().BeTrue();
    }

    [Fact]
    public void Permission_and_workflow_module_maps()
    {
        ModuleCodeMap.ForPermission(PermissionCodes.CUSTOMER_PAYMENT_VIEW).Should().Be(ModuleCodes.Finance);
        ModuleCodeMap.ForPermission(PermissionCodes.CUSTOMER_VIEW).Should().Be(ModuleCodes.Customers);
        ModuleCodeMap.ForPermission(PermissionCodes.POD_CAPTURE).Should().Be(ModuleCodes.Logistics);
        ModuleCodeMap.ForPermission(PermissionCodes.PO_VIEW).Should().Be(ModuleCodes.Demand);
        ModuleCodeMap.ForPermission(PermissionCodes.PRODUCT_LEDGER_VIEW).Should().Be(ModuleCodes.Finance);
        ModuleCodeMap.ForPermission(PermissionCodes.PROD_VIEW).Should().Be(ModuleCodes.Manufacturing);
        ModuleCodeMap.ForPermission(PermissionCodes.SERVICE_ORDER_VIEW).Should().Be(ModuleCodes.Services);
        ModuleCodeMap.ForWorkflowInterface("GRN_QC").Should().Be(ModuleCodes.Warehouse);
        ModuleCodeMap.ForWorkflowInterface("MIR_PROJECT").Should().Be(ModuleCodes.Mir);
        ModuleCodeMap.ForWorkflowInterface("UNKNOWN").Should().BeNull();
        PermissionCodes.All.Where(c => ModuleCodeMap.ForPermission(c) is null).Should().BeEmpty("every permission has a module");
    }
}
