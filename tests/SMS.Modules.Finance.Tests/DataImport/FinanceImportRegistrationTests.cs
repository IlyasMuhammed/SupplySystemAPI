using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Finance.Controllers;
using SMS.Modules.Finance.Import;
using SMS.Modules.Finance.Services;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Finance.Tests.DataImport;

/// <summary>A40 (TFIN) — the import targets and the opening-date contract are built by the module's own registration.</summary>
public sealed class FinanceImportRegistrationTests
{
    private static ServiceProvider BuildProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Data:mainOrg"] = "Server=none;Database=none;Trusted_Connection=True;" })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(new StaticTenantContext());
        services.AddFinanceModule(configuration);
        services.AddDbContext<DemandDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddSingleton(Mock.Of<IDeliveryFulfillmentReader>());
        services.AddSingleton(Mock.Of<ISupplierNameLookupService>());
        services.AddSingleton(Mock.Of<ILookupsService>());
        services.AddSingleton(Mock.Of<IBackgroundJobClient>());
        services.AddSingleton(Mock.Of<IProductVariantResolver>());
        return services.BuildServiceProvider();
    }

    [Fact]
    public void The_three_targets_and_the_opening_date_provider_resolve_one_per_scope()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        sp.GetServices<IImportTarget>().Select(t => t.Describe().EntityType).Should().Contain(
        [
            ImportEntityTypes.TaxCode, ImportEntityTypes.CustomerOpeningBalance, ImportEntityTypes.SupplierOpeningBalance
        ]);
        sp.GetRequiredService<IOpeningBalanceDateProvider>().Should().BeOfType<OpeningBalanceDateService>()
            .And.BeSameAs(sp.GetRequiredService<IOpeningBalanceDateService>());
        ActivatorUtilities.CreateInstance<OpeningBalanceDateController>(sp).Should().NotBeNull();
        // A42 D-12 — the sub-ledger opening totals seam Accounting reads.
        sp.GetRequiredService<IOpeningSubledgerTotals>().Should().BeOfType<OpeningSubledgerTotalsService>();
    }

    [Fact]
    public void The_endpoints_carry_the_contracts_permissions()
    {
        static IReadOnlyList<string> Codes(string action) =>
            typeof(OpeningBalanceDateController).GetMethod(action)!.GetCustomAttributes(typeof(RequirePermissionAttribute), false)
                .Cast<RequirePermissionAttribute>().Single().AnyOf;

        Codes(nameof(OpeningBalanceDateController.Get)).Should().Contain(
        [
            PermissionCodes.IMPORT_OPENING_BALANCE, PermissionCodes.FINANCE_SETUP_MANAGE,
            PermissionCodes.CURRENCY_VIEW, PermissionCodes.CURRENCY_MANAGE, PermissionCodes.CURRENCY_RATE_VIEW,
            PermissionCodes.CURRENCY_RATE_MANAGE, PermissionCodes.ORG_CURRENCY_SETTINGS_MANAGE,
            PermissionCodes.GL_VIEW, PermissionCodes.GL_ACCOUNT_MANAGE, PermissionCodes.GL_SETUP_MANAGE
        ]);
        // A42 D-11 — the GL wizard sets the conversion date.
        Codes(nameof(OpeningBalanceDateController.Put)).Should().Equal(PermissionCodes.FINANCE_SETUP_MANAGE, PermissionCodes.GL_SETUP_MANAGE);
    }

    [Fact]
    public async Task A_target_refuses_an_organization_other_than_the_callers()
    {
        var rig = new FinanceImportRig();
        var act = () => rig.TaxTargetAsync(t => t.ValidateAsync(Guid.NewGuid(), []));
        await act.Should().ThrowAsync<BadRequestException>();
    }
}
