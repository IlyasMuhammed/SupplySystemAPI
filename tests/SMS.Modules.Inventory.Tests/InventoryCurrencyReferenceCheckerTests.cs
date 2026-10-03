using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Inventory.Tests;

/// <summary>
/// What Inventory tells Lookups before a currency is deleted or its code changed: supplier rates and pricing
/// rules keep a currency by id, and every organization's rows count — the catalog is shared.
/// </summary>
public class InventoryCurrencyReferenceCheckerTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();
    private static readonly Guid Usd  = Guid.NewGuid();
    private static readonly Guid Eur  = Guid.NewGuid();

    private readonly string _dbName = Guid.NewGuid().ToString();

    private InventoryDbContext Open(Guid organizationId) =>
        new(new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(_dbName).Options,
            new StaticTenantContext { OrganizationId = organizationId });

    /// <summary>The rows are written in organization A; Lookups' request comes from B, not a super admin.</summary>
    private InventoryCurrencyReferenceChecker Checker() => new(Open(OrgB));

    public static TheoryData<string> Tables => new() { nameof(VariantSupplier), nameof(PricingRule) };

    private void Seed(string table, Guid currencyId, bool activeAndCurrent = true)
    {
        using var db = Open(OrgA);
        var from = activeAndCurrent ? DateTime.UtcNow.Date : DateTime.UtcNow.Date.AddDays(-90);
        DateTime? to = activeAndCurrent ? null : DateTime.UtcNow.Date.AddDays(-30);

        switch (table)
        {
            case nameof(VariantSupplier):
                db.VariantSuppliers.Add(new VariantSupplier
                {
                    OrganizationId = OrgA, VariantId = 1, SupplierId = Guid.NewGuid(), VendorUnitCost = 10m,
                    CurrencyId = currencyId, IsActive = activeAndCurrent, EffectiveFrom = from, EffectiveTo = to
                });
                break;

            case nameof(PricingRule):
                db.PricingRules.Add(new PricingRule
                {
                    OrganizationId = OrgA, VariantId = 1, UnitPrice = 12m,
                    CurrencyId = currencyId, IsActive = activeAndCurrent, EffectiveFrom = from, EffectiveTo = to
                });
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(table), table, null);
        }

        db.SaveChanges();
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public void A_currency_on_another_organizations_row_is_referenced(string table)
    {
        Seed(table, Usd);

        using (var orgB = Open(OrgB))
        {
            orgB.VariantSuppliers.Count().Should().Be(0, "the tenant filter hides organization A's rows from B");
            orgB.PricingRules.Count().Should().Be(0);
        }

        Checker().IsValueReferenced(Usd).Should().BeTrue();
        Checker().IsValueReferenced(Eur).Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public void Inactive_and_expired_rows_still_count_neither_table_has_a_soft_delete(string table)
    {
        Seed(table, Usd, activeAndCurrent: false);

        Checker().IsValueReferenced(Usd).Should().BeTrue("an inactive rate or rule can be switched back on, and an expired one is price history");
    }

    [Fact]
    public void A_currency_nothing_uses_is_not_referenced()
    {
        Seed(nameof(VariantSupplier), Eur);
        Seed(nameof(PricingRule), Eur);

        Checker().IsValueReferenced(Usd).Should().BeFalse();
    }

    [Fact]
    public void An_id_that_is_not_a_currency_matches_nothing()
    {
        Seed(nameof(VariantSupplier), Usd);
        Seed(nameof(PricingRule), Usd);

        // The same checkers answer for generic lookup values; such an id is on no price.
        Checker().IsValueReferenced(Guid.NewGuid()).Should().BeFalse();
    }

    [Fact]
    public void The_empty_id_is_never_referenced_even_by_rows_that_hold_it()
    {
        Seed(nameof(VariantSupplier), Guid.Empty);
        Seed(nameof(PricingRule), Guid.Empty);

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

        services.AddInventoryModule(configuration);

        services.Should().Contain(d => d.ServiceType == typeof(ILookupReferenceChecker)
                                    && d.ImplementationType == typeof(InventoryCurrencyReferenceChecker)
                                    && d.Lifetime == ServiceLifetime.Scoped);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetServices<ILookupReferenceChecker>()
            .Should().ContainSingle(c => c is InventoryCurrencyReferenceChecker);
    }
}
