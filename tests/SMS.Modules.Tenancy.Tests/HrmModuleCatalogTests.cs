using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Tenancy.Data;
using SMS.Modules.Tenancy.Domain;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Tenancy.Tests;

/// <summary>A49 D-5 / tasks 067 — MODULE_HRM in the registry: available, no dependency (HR-only is a profile), in no plan, off for every organization.</summary>
public sealed class HrmModuleCatalogTests
{
    private static readonly string[] SubFeatures =
    [
        HrmFeatureCodes.Attendance, HrmFeatureCodes.Leave, HrmFeatureCodes.Payroll, HrmFeatureCodes.Recruitment, HrmFeatureCodes.Performance,
        HrmFeatureCodes.Training, HrmFeatureCodes.Loans, HrmFeatureCodes.Expenses,
    ];

    [Fact]
    public void Available_and_toggleable_with_no_dependency()
    {
        var entry = TenancyFeatureCatalog.ByCode[ModuleCodes.Hrm];
        entry.IsAvailable.Should().BeTrue();
        entry.IsCore.Should().BeFalse();
        entry.IsAlwaysOn.Should().BeFalse();
        TenancyFeatureCatalog.Dependencies.Should().NotContain(d => d.Dependent == ModuleCodes.Hrm);
    }

    [Fact]
    public void Sub_features_hang_off_the_module_and_none_is_core()
    {
        TenancyFeatureCatalog.Catalog.Where(e => e.Parent == ModuleCodes.Hrm).Select(e => e.Code).Should().BeEquivalentTo(SubFeatures);
        TenancyFeatureCatalog.Catalog.Where(e => e.Parent == ModuleCodes.Hrm).Should().OnlyContain(e => !e.IsCore && e.IsAvailable);
    }

    [Theory]
    [InlineData("BASIC")]
    [InlineData("STANDARD")]
    [InlineData("ENTERPRISE")]
    public void In_no_plan_template(string plan)
    {
        var codes = TenancyFeatureCatalog.GetDefaultEnabledCodes(plan);
        codes.Should().NotContain(ModuleCodes.Hrm);
        codes.Should().NotContain(SubFeatures);
    }

    [Fact]
    public async Task Off_and_unlicensed_for_an_existing_organization_after_the_seeder_runs()
    {
        var db = new TenancyDbContext(new DbContextOptionsBuilder<TenancyDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var seeder = new TenancyDataSeeder(db, Mock.Of<IUserQueryService>(u => u.GetFirstSystemAdminUserIdAsync() == Task.FromResult<int?>(null)));
        await seeder.SeedAsync();

        var hrm = db.FeatureDefinitions.Where(f => f.FeatureCode == ModuleCodes.Hrm || f.ParentModuleCode == ModuleCodes.Hrm).Select(f => f.Id).ToHashSet();
        hrm.Should().HaveCount(SubFeatures.Length + 1);

        var rows = db.OrganizationFeatures.Where(r => hrm.Contains(r.FeatureDefinitionId)).ToList();
        rows.Should().NotBeEmpty().And.OnlyContain(r => !r.IsLicensed && !r.IsEnabled);

        // Idempotent: a second start changes nothing.
        await seeder.SeedAsync();
        db.OrganizationFeatures.Where(r => hrm.Contains(r.FeatureDefinitionId)).ToList().Should().OnlyContain(r => !r.IsLicensed && !r.IsEnabled);
    }

    [Fact]
    public void Permissions_and_workflow_interfaces_belong_to_the_module()
    {
        SMS.Shared.Authorization.PermissionCodes.HrmCodes.Should().OnlyContain(c => ModuleCodeMap.ForPermission(c) == ModuleCodes.Hrm);
        ModuleCodeMap.ForWorkflowInterface(HrmWorkflowCodes.LeaveRequest).Should().Be(ModuleCodes.Hrm);
        ModuleCodeMap.ForWorkflowInterface(HrmWorkflowCodes.ExpenseClaim).Should().Be(ModuleCodes.Hrm);
        ModuleCodeMap.ForWorkflowInterface(HrmWorkflowCodes.SalaryChange).Should().Be(ModuleCodes.Hrm);
    }
}
