using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Suppliers.Domain;
using SMS.Modules.Suppliers.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Suppliers.Tests;

/// <summary>
/// What Suppliers tells Lookups before a currency is deleted or its code changed: a partner's preferred
/// currency, by id, in every organization — except on deleted partners, which nothing reads again.
/// </summary>
public class SuppliersCurrencyReferenceCheckerTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();
    private static readonly Guid Usd  = Guid.NewGuid();
    private static readonly Guid Eur  = Guid.NewGuid();

    private readonly string _dbName = Guid.NewGuid().ToString();

    /// <summary>The partners are written in organization A; Lookups' request comes from B, not a super admin.</summary>
    private SuppliersCurrencyReferenceChecker Checker() => new(SuppliersTestDb.OpenAs(_dbName, OrgB));

    private void SeedPartner(Guid? preferredCurrency, bool deleted = false)
    {
        using var db = SuppliersTestDb.OpenAs(_dbName, OrgA);
        var n = Random.Shared.Next(1000, 9999);
        db.BusinessPartners.Add(new BusinessPartner
        {
            UUID = Guid.NewGuid(), OrganizationId = OrgA, SupplierName = $"Partner {n}", SupplierCode = $"P{n}",
            PreferredCurrency = preferredCurrency, IsDelete = deleted, CreatedBy = 1, CreatedDate = DateTime.UtcNow
        });
        db.SaveChanges();
    }

    [Fact]
    public void A_currency_another_organizations_partner_prefers_is_referenced()
    {
        SeedPartner(Usd);

        using (var orgB = SuppliersTestDb.OpenAs(_dbName, OrgB))
            orgB.BusinessPartners.Count().Should().Be(0, "the tenant filter hides organization A's partners from B");

        Checker().IsValueReferenced(Usd).Should().BeTrue();
        Checker().IsValueReferenced(Eur).Should().BeFalse();
    }

    [Fact]
    public void A_deleted_partners_preference_does_not_count()
    {
        SeedPartner(Usd, deleted: true);

        Checker().IsValueReferenced(Usd).Should().BeFalse("nothing reads a deleted partner's preferred currency again");
    }

    [Fact]
    public void A_live_partner_still_counts_beside_a_deleted_one()
    {
        SeedPartner(Usd, deleted: true);
        SeedPartner(Usd);

        Checker().IsValueReferenced(Usd).Should().BeTrue();
    }

    [Fact]
    public void Partners_without_a_preference_or_with_another_one_do_not_reference_it()
    {
        SeedPartner(null);
        SeedPartner(Eur);

        Checker().IsValueReferenced(Usd).Should().BeFalse();
    }

    [Fact]
    public void An_id_that_is_not_a_currency_matches_nothing()
    {
        SeedPartner(Usd);

        // The same checkers answer for generic lookup values; no partner prefers such an id.
        Checker().IsValueReferenced(Guid.NewGuid()).Should().BeFalse();
    }

    [Fact]
    public void The_empty_id_is_never_referenced_even_by_a_partner_that_holds_it()
    {
        SeedPartner(Guid.Empty);

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

        services.AddSuppliersModule(configuration);

        services.Should().Contain(d => d.ServiceType == typeof(ILookupReferenceChecker)
                                    && d.ImplementationType == typeof(SuppliersCurrencyReferenceChecker)
                                    && d.Lifetime == ServiceLifetime.Scoped);

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetServices<ILookupReferenceChecker>()
            .Should().ContainSingle(c => c is SuppliersCurrencyReferenceChecker);
    }
}
