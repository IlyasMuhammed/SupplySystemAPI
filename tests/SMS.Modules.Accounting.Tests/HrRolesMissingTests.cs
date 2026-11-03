using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Accounting.Tests;

/// <summary>A49 §10.7, analysis A-4 (task 326): the setup status reports missing HR roles beside the status and never moves it.</summary>
public class HrRolesMissingTests
{
    private sealed class HrmOn : IModuleGate
    {
        public Task<bool> IsEnabledAsync(Guid organizationId, string featureCode, CancellationToken ct = default) => Task.FromResult(featureCode == ModuleCodes.Hrm);
    }

    [Fact]
    public async Task HRM_off_reports_nothing()
    {
        using var h = new AccountingHarness();
        (await h.Setup.GetAsync(h.Admin())).HrRolesMissing.Should().BeEmpty();
    }

    [Fact]
    public async Task HRM_on_lists_the_unassigned_keys_and_a_complete_org_stays_complete()
    {
        using var h = new AccountingHarness();
        await new AccountingInitializer(h.Db, new HrmOn()).EnsureAsync(h.Org);

        var status = await h.Db.CoaSetupStatuses.IgnoreQueryFilters().SingleAsync(s => s.OrganizationId == h.Org);
        status.Status = SetupStatuses.Complete;
        await h.Db.SaveChangesAsync();

        var model = await h.Setup.GetAsync(h.Admin());

        model.Status.Should().Be(SetupStatuses.Complete, "stopping a live organization's posting because HRM was enabled is what A-4 refuses");
        model.HrRolesMissing.Should().HaveCount(16).And.Contain(DefaultKeys.SalaryExpense);

        var salary = await h.Db.AccountingDefaults.IgnoreQueryFilters().SingleAsync(d => d.OrganizationId == h.Org && d.DefaultKey == DefaultKeys.SalaryExpense);
        salary.AccountId = 1;
        await h.Db.SaveChangesAsync();
        (await h.Setup.GetAsync(h.Admin())).HrRolesMissing.Should().HaveCount(15).And.NotContain(DefaultKeys.SalaryExpense);
    }
}
