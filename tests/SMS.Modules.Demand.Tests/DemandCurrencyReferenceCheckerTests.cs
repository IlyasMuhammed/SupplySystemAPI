using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>
/// What Demand tells Lookups before a currency is deleted or its code changed: a sale order keeps its
/// currency by id, and every organization's sale orders count — deleted ones included.
/// </summary>
public class DemandCurrencyReferenceCheckerTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();
    private static readonly Guid Usd  = Guid.NewGuid();
    private static readonly Guid Eur  = Guid.NewGuid();

    private readonly string _dbName = Guid.NewGuid().ToString();

    private DemandDbContext Open(Guid organizationId) =>
        new(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(_dbName).Options,
            new StaticTenantContext { OrganizationId = organizationId });

    /// <summary>The orders are written in organization A; Lookups' request comes from B, not a super admin.</summary>
    private DemandCurrencyReferenceChecker Checker() => new(Open(OrgB));

    private void SeedOrder(Guid currencyId, bool deleted = false)
    {
        using var db = Open(OrgA);
        db.SaleOrders.Add(new SaleOrder
        {
            OrganizationId = OrgA, SoNumber = $"SO-2026-{Random.Shared.Next(10000, 99999)}", PartnerId = Guid.NewGuid(),
            OrderDate = DateTime.UtcNow.Date, CurrencyId = currencyId, IsDeleted = deleted, CreatedBy = 1
        });
        db.SaveChanges();
    }

    [Fact]
    public void A_currency_on_another_organizations_sale_order_is_referenced()
    {
        SeedOrder(Usd);

        using (var orgB = Open(OrgB))
            orgB.SaleOrders.Count().Should().Be(0, "the tenant filter hides organization A's orders from B");

        Checker().IsValueReferenced(Usd).Should().BeTrue();
        Checker().IsValueReferenced(Eur).Should().BeFalse();
    }

    [Fact]
    public void A_deleted_sale_order_still_counts()
    {
        SeedOrder(Usd, deleted: true);

        Checker().IsValueReferenced(Usd).Should().BeTrue("a deleted order stays on record with its currency");
    }

    [Fact]
    public void A_currency_no_sale_order_uses_is_not_referenced()
    {
        SeedOrder(Eur);

        Checker().IsValueReferenced(Usd).Should().BeFalse();
    }

    [Fact]
    public void An_id_that_is_not_a_currency_matches_nothing()
    {
        SeedOrder(Usd);

        // The same checkers answer for generic lookup values; such an id is on no sale order.
        Checker().IsValueReferenced(Guid.NewGuid()).Should().BeFalse();
    }

    [Fact]
    public void The_empty_id_is_never_referenced_even_by_an_order_that_holds_it()
    {
        SeedOrder(Guid.Empty);

        Checker().IsValueReferenced(Guid.Empty).Should().BeFalse();
    }

    [Fact]
    public void The_module_registers_the_checker_for_lookups_and_the_container_can_build_it()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Data:mainOrg"] = "Server=none;Database=none;Trusted_Connection=True;" })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantContext>(new StaticTenantContext());

        services.AddDemandModule(configuration);

        services.Should().Contain(d => d.ServiceType == typeof(ILookupReferenceChecker)
                                    && d.ImplementationType == typeof(DemandCurrencyReferenceChecker)
                                    && d.Lifetime == ServiceLifetime.Scoped);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetServices<ILookupReferenceChecker>()
            .Should().ContainSingle(c => c is DemandCurrencyReferenceChecker);
    }
}
