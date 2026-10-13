using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Accounting.Controllers;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Accounting.Tests;

/// <summary>A42 0641–0643 / D-4 — GL dashboard totals, its setup gate, and the BE2 DI wiring.</summary>
public class DashboardTests
{
    [Fact]
    public async Task The_dashboard_sums_leaf_balances_per_root_type()
    {
        using var h = new AccountingHarness();
        var chart = await h.SeedChartAsync(null, AccountingHarness.StandardLeaves);
        await h.AddFiscalYearAsync();
        chart["1111"].CurrentBalance = 100;
        chart["1112"].CurrentBalance = 250.5m;
        chart["2111"].CurrentBalance = 80;
        chart["3111"].CurrentBalance = 270.5m;
        chart["4111"].CurrentBalance = 40;
        chart["5111"].CurrentBalance = 15;
        chart["11"].CurrentBalance = 9_999; // a group's own figure is never summed
        await h.Db.SaveChangesAsync();
        await h.SeedChartAsync(Guid.NewGuid(), ("1111", RootTypes.Asset, AccountTypes.Cash));

        var d = await h.Get<IDashboardService>().GetAsync(h.Viewer());

        d.Should().Match<GlDashboardModel>(x => x.TotalAssets == 350.5m && x.TotalLiabilities == 80 && x.TotalEquity == 270.5m
            && x.TotalRevenue == 40 && x.TotalExpenses == 15 && x.AccountCount == 23 && x.CurrencyCode == "PKR");
        d.FiscalYear!.Name.Should().Be("FY 2026");
        await ((Func<Task>)(() => h.Get<IDashboardService>().GetAsync(h.Nobody()))).Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public void The_dashboard_endpoint_is_gated_by_the_setup_and_needs_gl_view()
    {
        var action = typeof(GlDashboardController).GetMethod(nameof(GlDashboardController.Get))!;
        action.GetCustomAttribute<RequiresGlSetupAttribute>().Should().NotBeNull("D-4: only post-setup GL endpoints are gated");
        action.GetCustomAttribute<HttpGetAttribute>()!.Template.Should().Be("dashboard");
        typeof(GlDashboardController).GetCustomAttribute<RouteAttribute>()!.Template.Should().Be("api/finance/gl");
        action.GetCustomAttribute<RequirePermissionAttribute>().Should().NotBeNull();

        // The setup endpoints are NOT gated.
        foreach (var setup in new[] { typeof(FiscalYearsController), typeof(AccountingDefaultsController), typeof(OpeningBalancesController), typeof(ExternalMappingsController) })
        {
            setup.GetCustomAttribute<RequiresGlSetupAttribute>().Should().BeNull();
            setup.GetMethods().Should().OnlyContain(m => m.GetCustomAttribute<RequiresGlSetupAttribute>() == null);
            setup.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Should().OnlyContain(m => m.GetCustomAttribute<RequirePermissionAttribute>() != null, $"{setup.Name} actions declare their permission");
        }
    }

    [Fact]
    public void BE2_services_and_shared_seams_are_registered()
    {
        using var h = new AccountingHarness();
        h.Get<IFiscalYearService>().Should().BeOfType<FiscalYearService>();
        h.Get<IFiscalPeriodGuard>().Should().BeOfType<FiscalPeriodGuard>();
        h.Get<IDefaultsService>().Should().BeOfType<DefaultsService>();
        h.Get<IOpeningBalanceService>().Should().BeOfType<OpeningBalanceService>();
        h.Get<IExternalMappingService>().Should().BeOfType<ExternalMappingService>();
        h.Get<IDashboardService>().Should().BeOfType<DashboardService>();
        h.Services.GetServices<IOpeningDateLockSource>().Should().ContainSingle().Which.Should().BeOfType<GlOpeningDateLockSource>();
    }
}
