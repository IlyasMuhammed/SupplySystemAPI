using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SMS.Modules.Tenancy.Data;
using SMS.Modules.Tenancy.Domain;
using SMS.Modules.Tenancy.Models;
using SMS.Modules.Tenancy.Repositories;
using SMS.Modules.Tenancy.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Tenancy.Tests;

/// <summary>
/// SAP alignment, work package A — the organization profile edit used to set BaseCurrency to null on every
/// save, because the form never sent one. A null BaseCurrency now means "leave it"; removing it is the
/// explicit ClearBaseCurrency. A base currency must be a catalog currency with a code.
/// </summary>
public class OrganizationBaseCurrencyTests
{
    private static readonly Guid Pkr = Guid.NewGuid();
    private static readonly Guid Usd = Guid.NewGuid();
    private static readonly Guid NoCode = Guid.NewGuid();

    private readonly TenancyDbContext _db =
        new(new DbContextOptionsBuilder<TenancyDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private TenancyService Service(bool withCatalog = true)
    {
        ICurrencyCodeLookup? catalog = null;
        if (withCatalog)
        {
            var lookup = new Mock<ICurrencyCodeLookup>();
            lookup.Setup(l => l.GetCodeAsync(Pkr, It.IsAny<CancellationToken>())).ReturnsAsync("PKR");
            lookup.Setup(l => l.GetCodeAsync(Usd, It.IsAny<CancellationToken>())).ReturnsAsync("USD");
            lookup.Setup(l => l.GetCodeAsync(NoCode, It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
            catalog = lookup.Object;
        }

        var repo = new TenancyRepository(_db, Mock.Of<IOrgUserProvisioningService>(), Mock.Of<IWorkflowSeedingService>());
        return new TenancyService(repo, Mock.Of<IOrgUserProvisioningService>(), Mock.Of<ITenantSnapshotProvider>(), catalog);
    }

    private async Task<Guid> SeedOrg(Guid? baseCurrency)
    {
        var org = new Organization
        {
            Id = Guid.NewGuid(), OrgCode = "ACME", OrgName = "Acme", Plan = "BASIC", BaseCurrency = baseCurrency,
            ContactEmail = "old@acme.test", CreatedBy = 1, CreatedDate = DateTime.UtcNow
        };
        _db.Organizations.Add(org);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        return org.Id;
    }

    private async Task<Organization> Load(Guid id)
    {
        _db.ChangeTracker.Clear();
        return await _db.Organizations.SingleAsync(o => o.Id == id);
    }

    [Fact]
    public async Task A_profile_edit_that_sends_no_base_currency_keeps_it_and_still_saves_everything_else()
    {
        var id = await SeedOrg(Pkr);

        var updated = await Service().UpdateOrganizationAsync(id, new UpdateOrganizationRequest
        {
            OrgName = "Acme Corporation", ContactEmail = "new@acme.test", Country = "Pakistan"
        }, modifiedBy: 9);

        updated.Should().BeTrue();
        var org = await Load(id);
        org.BaseCurrency.Should().Be(Pkr, "the edit did not mention the base currency");
        org.OrgName.Should().Be("Acme Corporation");
        org.ContactEmail.Should().Be("new@acme.test");
        org.Country.Should().Be("Pakistan");
        org.ModifiedBy.Should().Be(9);
    }

    [Fact]
    public async Task Sending_a_base_currency_sets_or_changes_it()
    {
        var id = await SeedOrg(null);

        await Service().UpdateOrganizationAsync(id, new UpdateOrganizationRequest { OrgName = "Acme", BaseCurrency = Pkr }, 1);
        (await Load(id)).BaseCurrency.Should().Be(Pkr);

        await Service().UpdateOrganizationAsync(id, new UpdateOrganizationRequest { OrgName = "Acme", BaseCurrency = Usd }, 1);
        (await Load(id)).BaseCurrency.Should().Be(Usd);
    }

    [Fact]
    public async Task Clearing_is_explicit()
    {
        var id = await SeedOrg(Pkr);

        await Service().UpdateOrganizationAsync(id, new UpdateOrganizationRequest { OrgName = "Acme", ClearBaseCurrency = true }, 1);

        (await Load(id)).BaseCurrency.Should().BeNull();
    }

    [Fact]
    public async Task Clearing_and_choosing_at_once_is_refused_and_nothing_changes()
    {
        var id = await SeedOrg(Pkr);

        var act = () => Service().UpdateOrganizationAsync(id,
            new UpdateOrganizationRequest { OrgName = "Changed", BaseCurrency = Usd, ClearBaseCurrency = true }, 1);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*not both*");
        var org = await Load(id);
        org.BaseCurrency.Should().Be(Pkr);
        org.OrgName.Should().Be("Acme");
    }

    [Fact]
    public async Task A_base_currency_must_be_a_catalog_currency_with_a_code()
    {
        var id = await SeedOrg(Pkr);

        foreach (var bad in new[] { Guid.NewGuid(), NoCode, Guid.Empty })
        {
            var act = () => Service().UpdateOrganizationAsync(id, new UpdateOrganizationRequest { OrgName = "Acme", BaseCurrency = bad }, 1);
            await act.Should().ThrowAsync<BadRequestException>();
        }

        (await Load(id)).BaseCurrency.Should().Be(Pkr);
    }

    [Fact]
    public async Task Without_the_lookups_module_any_non_empty_id_is_taken_as_given()
    {
        var id = await SeedOrg(null);
        var someId = Guid.NewGuid();

        await Service(withCatalog: false).UpdateOrganizationAsync(id, new UpdateOrganizationRequest { OrgName = "Acme", BaseCurrency = someId }, 1);

        (await Load(id)).BaseCurrency.Should().Be(someId);
    }

    [Fact]
    public async Task Sending_back_the_base_currency_the_organization_already_has_is_not_rechecked_even_if_it_left_the_catalog()
    {
        // A legacy organization whose base currency is no longer a usable catalog currency.
        var gone = Guid.NewGuid();
        var id   = await SeedOrg(gone);

        // An edit form that always sends its picker's value still saves the rest…
        (await Service().UpdateOrganizationAsync(id, new UpdateOrganizationRequest { OrgName = "Acme Renamed", BaseCurrency = gone }, 1))
            .Should().BeTrue();
        var org = await Load(id);
        org.OrgName.Should().Be("Acme Renamed");
        org.BaseCurrency.Should().Be(gone);

        // …while choosing another unusable currency is still refused.
        var act = () => Service().UpdateOrganizationAsync(id, new UpdateOrganizationRequest { OrgName = "Acme", BaseCurrency = NoCode }, 1);
        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task An_unknown_organization_is_not_updated()
    {
        (await Service().UpdateOrganizationAsync(Guid.NewGuid(), new UpdateOrganizationRequest { OrgName = "x" }, 1)).Should().BeFalse();
        (await Service().UpdateOrganizationAsync(Guid.NewGuid(), new UpdateOrganizationRequest { OrgName = "x", BaseCurrency = Usd }, 1))
            .Should().BeFalse("an unknown organization is a 404, whatever currency was sent");
    }

    [Fact]
    public async Task Creating_an_organization_with_an_unknown_base_currency_is_refused_before_anything_is_written()
    {
        var act = () => Service().CreateOrganizationWithAdminAsync(new CreateOrganizationRequest
        {
            OrgCode = "NEW", OrgName = "New Org", Plan = "BASIC", BaseCurrency = Guid.NewGuid(),
            AdminFirstName = "Jane", AdminLastName = "Doe", AdminEmail = "jane@new.test"
        }, createdBy: 1);

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*base currency*");
        (await _db.Organizations.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task The_detail_read_returns_the_base_currency_for_the_edit_form()
    {
        var id = await SeedOrg(Usd);

        (await Service().GetOrganizationByIdAsync(id))!.BaseCurrency.Should().Be(Usd);
    }

    // ── What Tenancy tells Lookups ───────────────────────────────────────────

    [Fact]
    public async Task A_currency_that_is_any_organizations_base_currency_is_referenced()
    {
        await SeedOrg(Pkr);
        var checker = new TenancyCurrencyReferenceChecker(_db);

        checker.IsValueReferenced(Pkr).Should().BeTrue();
        checker.IsValueReferenced(Usd).Should().BeFalse();
        checker.IsValueReferenced(Guid.Empty).Should().BeFalse();
    }

    [Fact]
    public void The_module_registers_the_checker_for_lookups()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Data:mainOrg"] = "Server=none;Database=none;Trusted_Connection=True;" })
            .Build();
        var services = new ServiceCollection();

        services.AddTenancyModule(configuration);

        services.Should().Contain(d => d.ServiceType == typeof(ILookupReferenceChecker)
                                    && d.ImplementationType == typeof(TenancyCurrencyReferenceChecker)
                                    && d.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void The_container_builds_the_service_with_the_catalog_when_lookups_is_there_and_without_it_when_not()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Data:mainOrg"] = "Server=none;Database=none;Trusted_Connection=True;" })
            .Build();

        ServiceProvider Build(bool withCatalog)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddTenancyModule(configuration);
            services.AddSingleton(Mock.Of<IOrgUserProvisioningService>());
            services.AddSingleton(Mock.Of<IWorkflowSeedingService>());
            if (withCatalog) services.AddSingleton(Mock.Of<ICurrencyCodeLookup>());
            return services.BuildServiceProvider();
        }

        object? Catalog(ServiceProvider sp)
        {
            using var scope = sp.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<ITenancyService>();
            return svc.GetType().GetField("_currencies", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(svc);
        }

        using (var with = Build(true))     Catalog(with).Should().NotBeNull();
        using (var without = Build(false)) Catalog(without).Should().BeNull();
    }
}
