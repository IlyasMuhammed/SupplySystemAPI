using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Tenancy.Data;
using SMS.Modules.Tenancy.Models;
using SMS.Modules.Tenancy.Repositories;
using SMS.Modules.Tenancy.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Tenancy.Tests;

/// <summary>A49 FD-9 (E-2, E-6): both ways a module can be switched on ask its guard. D-14 (E-16, tasks 082–085): the four profiles.</summary>
public class HrmEnablementGuardTests
{
    private const int Admin = 7;

    private sealed class Guard : IModuleEnablementGuard
    {
        public bool Allow { get; set; }
        public string ModuleCode => ModuleCodes.Hrm;
        public Task CheckAsync(Guid organizationId, CancellationToken ct = default) =>
            Allow ? Task.CompletedTask : throw new UnprocessableEntityException("PAYROLL_COST_CENTER_REQUIRED: Choose a payroll cost center.");
    }

    private sealed record Bed(TenancyDbContext Db, ModuleRegistryService Modules, TenancyService Tenancy, Guard Guard, Guid Org);

    private static async Task<Bed> NewAsync()
    {
        var db = new TenancyDbContext(new DbContextOptionsBuilder<TenancyDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var users = new Mock<IUserQueryService>();
        users.Setup(u => u.GetFirstSystemAdminUserIdAsync()).ReturnsAsync((int?)null);
        users.Setup(u => u.GetUsersAsync(It.IsAny<IReadOnlyList<int>>())).ReturnsAsync([]);
        await new TenancyDataSeeder(db, users.Object).SeedAsync();

        var guard = new Guard();
        var snapshots = new Mock<ITenantSnapshotProvider>();
        var provisioning = new Mock<IOrgUserProvisioningService>();
        var repo = new TenancyRepository(db, provisioning.Object, Mock.Of<IWorkflowSeedingService>());
        return new Bed(db,
            new ModuleRegistryService(db, snapshots.Object, users.Object, guards: [guard]),
            new TenancyService(repo, provisioning.Object, snapshots.Object, guards: [guard]),
            guard, TenantDefaults.ScmDemoOrganizationId);
    }

    private static (bool Licensed, bool Enabled) Row(Bed b, string code)
    {
        var def = b.Db.FeatureDefinitions.AsNoTracking().Single(f => f.FeatureCode == code);
        var row = b.Db.OrganizationFeatures.AsNoTracking().Single(f => f.OrganizationId == b.Org && f.FeatureDefinitionId == def.Id);
        return (row.IsLicensed, row.IsEnabled);
    }

    private static Task<UpdateFeaturesResult> LicenceAsync(Bed b, bool on = true) =>
        b.Tenancy.UpdateFeaturesAsync(b.Org, [new FeatureToggleItem { FeatureCode = ModuleCodes.Hrm, IsEnabled = on }], Admin);

    [Fact]
    public async Task E6_the_org_admin_enable_route_answers_422_and_changes_nothing()
    {
        var b = await NewAsync();
        await LicenceAsync(b);

        var refused = await b.Modules.Invoking(m => m.EnableAsync(b.Org, ModuleCodes.Hrm, new EnableModuleRequest(), Admin))
            .Should().ThrowAsync<UnprocessableEntityException>();
        refused.Which.Message.Should().StartWith("PAYROLL_COST_CENTER_REQUIRED");
        Row(b, ModuleCodes.Hrm).Should().Be((true, false));

        b.Guard.Allow = true;
        (await b.Modules.EnableAsync(b.Org, ModuleCodes.Hrm, new EnableModuleRequest(), Admin)).IsEnabled.Should().BeTrue();
        Row(b, ModuleCodes.Hrm).Should().Be((true, true));
    }

    [Fact]
    public async Task E6_the_super_admin_licence_cannot_switch_it_on_past_the_guard()
    {
        var b = await NewAsync();

        var result = await LicenceAsync(b);

        Row(b, ModuleCodes.Hrm).Should().Be((true, false), "licensed, so the wizard can run; off, so §11A.2 holds");
        result.LicensedNotEnabled.Single().Reason.Should().StartWith("PAYROLL_COST_CENTER_REQUIRED");
        b.Db.OrganizationFeatureHistory.AsNoTracking().Should().Contain(h => h.FeatureCode == ModuleCodes.Hrm && h.Action == SMS.Modules.Tenancy.Domain.FeatureHistoryActions.Licensed);
    }

    [Fact]
    public async Task E6_once_the_guard_passes_the_licence_switches_it_on_as_before_and_unlicensing_still_works()
    {
        var b = await NewAsync();
        b.Guard.Allow = true;

        (await LicenceAsync(b)).LicensedNotEnabled.Should().BeEmpty();
        Row(b, ModuleCodes.Hrm).Should().Be((true, true));

        await LicenceAsync(b, on: false);
        Row(b, ModuleCodes.Hrm).Should().Be((false, false));
    }

    [Fact]
    public async Task A_module_without_a_guard_is_licensed_and_enabled_exactly_as_before()
    {
        var b = await NewAsync();
        await b.Tenancy.UpdateFeaturesAsync(b.Org, [new FeatureToggleItem { FeatureCode = "MODULE_LOGISTICS", IsEnabled = false }], Admin);
        Row(b, "MODULE_LOGISTICS").Should().Be((false, false));
        (await b.Tenancy.UpdateFeaturesAsync(b.Org, [new FeatureToggleItem { FeatureCode = "MODULE_LOGISTICS", IsEnabled = true }], Admin)).LicensedNotEnabled.Should().BeEmpty();
        Row(b, "MODULE_LOGISTICS").Should().Be((true, true));
    }

    // ── Profiles ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ModuleProfiles.P2)]
    [InlineData(ModuleProfiles.P2S)]
    [InlineData(ModuleProfiles.P3)]
    [InlineData(ModuleProfiles.PPk)]
    public async Task G8_each_profile_applies_cleanly_to_a_full_organization(string code)
    {
        var b = await NewAsync();
        var profile = ModuleProfiles.Find(code)!;

        var result = await new ModuleProfileService(b.Tenancy).ApplyAsync(b.Org, code, Admin);

        result.AutoEnabledDependencies.Should().BeEmpty("nothing a profile switches off may come back on");
        foreach (var off in profile.Off) Row(b, off).Should().Be((false, false), off);
        foreach (var on in profile.Modules.Where(m => m != ModuleCodes.Hrm)) Row(b, on).Should().Be((true, true), on);
        Row(b, ModuleCodes.Hrm).Should().Be((true, false), "§10.3: a profile never enables HRM without the payroll cost center");
        result.LicensedNotEnabled.Single().FeatureCode.Should().Be(ModuleCodes.Hrm);
    }

    [Fact]
    public async Task G8_every_profile_code_is_a_real_catalog_code_and_the_lists_do_not_overlap()
    {
        foreach (var profile in ModuleProfiles.All)
        {
            foreach (var code in profile.Modules.Concat(profile.Off).Concat(profile.Optional))
                TenancyFeatureCatalog.ByCode.Should().ContainKey(code, profile.Code);
            profile.Modules.Intersect(profile.Off).Should().BeEmpty(profile.Code);
            profile.Off.Should().NotContain(c => TenancyFeatureCatalog.ByCode[c].IsCore, "a core module cannot be switched off");
        }
        ModuleProfiles.All.Select(p => p.Code).Should().Equal("P2", "P2-S", "P3", "P-PK");

        var pk = ModuleProfiles.Find("p-pk")!;
        pk.Off.Should().Contain("MODULE_INTEGRATION");
        pk.Modules.Should().Contain(ModuleCodes.GeneralLedger);
        ModuleProfiles.Find("P2-S")!.Modules.Should().Contain("MODULE_CUSTOMERS").And.Contain("MODULE_SUPPLIERS");
        ModuleProfiles.Find("P3")!.Off.Should().Contain(ModuleCodes.Finance);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task G8_an_unknown_profile_is_refused()
    {
        var b = await NewAsync();
        await new ModuleProfileService(b.Tenancy).Invoking(s => s.ApplyAsync(b.Org, "P9", Admin)).Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public void G8_the_apply_route_is_super_admin_only()
    {
        var method = typeof(ModuleProfilesController).GetMethod(nameof(ModuleProfilesController.Apply))!;
        method.GetCustomAttributes(typeof(SMS.Shared.Authorization.RequireSuperAdminAttribute), true).Should().ContainSingle();
    }
}
