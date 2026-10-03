using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Core.Reference;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Tests.Connections;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Integration.Tests.Setup;

public class ReferenceDataServiceTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    [Fact]
    public async Task Store_writes_one_snapshot_per_kind_and_the_company_facts()
    {
        await using var h = ConnectionsHarness.Create();
        var c = await h.SeedConnectionAsync(OrgA);

        await using (var scope = h.Scope(OrgA))
        {
            var db         = scope.ServiceProvider.GetRequiredService<Data.IntegrationDbContext>();
            var connection = await db.Connections.SingleAsync();
            await scope.ServiceProvider.GetRequiredService<IReferenceDataStore>()
                .StoreAsync(connection, SampleReference.Data(homeCurrency: "usd", multiCurrency: true, country: "GB", companyName: "UK Ltd"));
        }

        await using var check = h.OpenAs(OrgA);
        (await check.ReferenceSnapshots.Select(s => s.Kind).OrderBy(k => k).ToListAsync())
            .Should().Equal("Accounts", "CompanyInfo", "Currencies", "Preferences", "TaxCodes", "Terms");
        var stored = await check.Connections.SingleAsync();
        stored.CompanyName.Should().Be("UK Ltd");
        stored.HomeCurrencyCode.Should().Be("USD");
        stored.MultiCurrencyEnabled.Should().BeTrue();
        stored.Country.Should().Be("GB");
    }

    [Fact]
    public async Task Storing_again_updates_in_place()
    {
        await using var h = ConnectionsHarness.Create();
        var c = await h.SeedConnectionAsync(OrgA);

        for (var i = 0; i < 2; i++)
        {
            await using var scope = h.Scope(OrgA);
            var db = scope.ServiceProvider.GetRequiredService<Data.IntegrationDbContext>();
            await scope.ServiceProvider.GetRequiredService<IReferenceDataStore>()
                .StoreAsync(await db.Connections.SingleAsync(), SampleReference.Data(companyName: $"Name {i}"));
        }

        await using var check = h.OpenAs(OrgA);
        (await check.ReferenceSnapshots.CountAsync()).Should().Be(6);
        (await check.Connections.SingleAsync()).CompanyName.Should().Be("Name 1");
    }

    [Fact]
    public async Task A_partial_answer_does_not_blank_what_was_known()
    {
        await using var h = ConnectionsHarness.Create();
        await h.SeedConnectionAsync(OrgA);

        await using (var scope = h.Scope(OrgA))
        {
            var db = scope.ServiceProvider.GetRequiredService<Data.IntegrationDbContext>();
            await scope.ServiceProvider.GetRequiredService<IReferenceDataStore>()
                .StoreAsync(await db.Connections.SingleAsync(), new RemoteReferenceData());
        }

        await using var check = h.OpenAs(OrgA);
        var stored = await check.Connections.SingleAsync();
        stored.CompanyName.Should().Be("Seeded Company");
        stored.HomeCurrencyCode.Should().Be("PKR");
    }

    [Fact]
    public async Task The_cache_round_trips_every_kind()
    {
        await using var h = ConnectionsHarness.Create();
        var c = await h.SeedConnectionAsync(OrgA);
        await SetupTestKit.SeedReferenceAsync(h, c);

        await using var scope = h.Scope(OrgA);
        var cached = await scope.ServiceProvider.GetRequiredService<IReferenceDataReader>().GetCachedAsync(c.Id);

        var expected = SampleReference.Data();
        cached.Should().NotBeNull();
        cached!.Accounts.Should().BeEquivalentTo(expected.Accounts);
        cached.TaxCodes.Should().BeEquivalentTo(expected.TaxCodes);
        cached.Terms.Should().BeEquivalentTo(expected.Terms);
        cached.Currencies.Should().BeEquivalentTo(expected.Currencies);
        cached.Preferences.Should().Be(expected.Preferences);
        cached.CompanyInfo.Should().Be(expected.CompanyInfo);
    }

    [Fact]
    public async Task Nothing_cached_is_null()
    {
        await using var h = ConnectionsHarness.Create();
        var c = await h.SeedConnectionAsync(OrgA);

        await using var scope = h.Scope(OrgA);
        (await scope.ServiceProvider.GetRequiredService<IReferenceDataReader>().GetCachedAsync(c.Id)).Should().BeNull();
        (await scope.ServiceProvider.GetRequiredService<IReferenceDataService>().GetAsync()).Accounts.Should().BeEmpty();
    }

    [Fact]
    public async Task The_model_maps_accounts_tax_codes_terms_and_currencies()
    {
        await using var h = ConnectionsHarness.Create();
        var c = await h.SeedConnectionAsync(OrgA);
        await SetupTestKit.SeedReferenceAsync(h, c);

        await using var scope = h.Scope(OrgA);
        var model = await scope.ServiceProvider.GetRequiredService<IReferenceDataService>().GetAsync();

        model.FetchedAt.Should().NotBeNull();
        model.Accounts.Should().Contain(a => a.Id == "4" && a.Type == "Cost of Goods Sold" && a.Active);
        model.Accounts.Should().Contain(a => a.Id == "7" && !a.Active);
        model.TaxCodes.Should().Contain(t => t.Id == "10" && t.Type == "Taxable" && t.Rate == 17m);
        model.TaxCodes.Should().Contain(t => t.Id == "11" && t.Type == "NonTaxable");
        model.Terms.Should().Contain(t => t.Id == "20" && t.Days == 30);
        model.Currencies.Should().Contain(x => x.Id == "PKR" && x.Name == "Pakistani Rupee");
    }

    [Fact]
    public async Task Snapshots_are_tenant_scoped()
    {
        await using var h = ConnectionsHarness.Create();
        var a = await h.SeedConnectionAsync(OrgA);
        await SetupTestKit.SeedReferenceAsync(h, a);
        await h.SeedConnectionAsync(OrgB, realmId: "B");

        await using var scope = h.Scope(OrgB);
        (await scope.ServiceProvider.GetRequiredService<IReferenceDataReader>().GetCachedAsync(a.Id)).Should().BeNull();
        (await scope.ServiceProvider.GetRequiredService<IReferenceDataService>().GetAsync()).Accounts.Should().BeEmpty();
    }

    [Fact]
    public async Task Refresh_fetches_through_the_provider_and_returns_the_new_model()
    {
        await using var h = ConnectionsHarness.Create();
        var c = await h.SeedConnectionAsync(OrgA);

        await using var scope = h.Scope(OrgA);
        var model = await scope.ServiceProvider.GetRequiredService<IReferenceDataService>().RefreshAsync(7);

        model.Accounts.Should().HaveCount(8);
        h.Provider.Verify(p => p.GetReferenceDataAsync(
            It.Is<ProviderContext>(x => x.ConnectionId == c.Id && x.RealmId == "9130000000000001"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("NotConnected")]
    [InlineData("Revoked")]
    public async Task Refresh_needs_a_usable_connection(string statusName)
    {
        var status = Enum.Parse<ConnectionStatus>(statusName);
        await using var h = ConnectionsHarness.Create();
        await h.SeedConnectionAsync(OrgA, status);

        await using var scope = h.Scope(OrgA);
        await FluentActions.Awaiting(() => scope.ServiceProvider.GetRequiredService<IReferenceDataService>().RefreshAsync(7))
            .Should().ThrowAsync<ConflictException>().WithMessage("Connect QuickBooks*");
    }

    [Fact]
    public async Task A_provider_refusal_is_a_conflict_with_the_reason()
    {
        await using var h = ConnectionsHarness.Create();
        await h.SeedConnectionAsync(OrgA);
        h.Provider.Setup(p => p.GetReferenceDataAsync(It.IsAny<ProviderContext>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(ProviderResult<RemoteReferenceData>.Fail(ProviderOutcomeKind.Throttled, "429", "Too many requests"));

        await using var scope = h.Scope(OrgA);
        await FluentActions.Awaiting(() => scope.ServiceProvider.GetRequiredService<IReferenceDataService>().RefreshAsync(7))
            .Should().ThrowAsync<ConflictException>().WithMessage("*Throttled*Too many requests*");
    }

    [Fact]
    public async Task An_auth_refusal_marks_the_connection_Revoked()
    {
        await using var h = ConnectionsHarness.Create();
        var c = await h.SeedConnectionAsync(OrgA);
        h.Provider.Setup(p => p.GetReferenceDataAsync(It.IsAny<ProviderContext>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync(ProviderResult<RemoteReferenceData>.Fail(ProviderOutcomeKind.AuthRevoked, "401", "AuthenticationFailed"));

        await using var scope = h.Scope(OrgA);
        await FluentActions.Awaiting(() => scope.ServiceProvider.GetRequiredService<IReferenceDataService>().RefreshAsync(7))
            .Should().ThrowAsync<ConflictException>();
        (await h.ReloadConnectionAsync(c.Id)).Status.Should().Be(ConnectionStatus.Revoked);
    }

    [Fact]
    public async Task An_unusable_token_becomes_a_conflict()
    {
        await using var h = ConnectionsHarness.Create();
        await h.SeedConnectionAsync(OrgA);
        h.Provider.Setup(p => p.GetReferenceDataAsync(It.IsAny<ProviderContext>(), It.IsAny<CancellationToken>()))
                  .ThrowsAsync(new TokenRefreshFailedException("Could not reach QuickBooks to refresh the connection."));

        await using var scope = h.Scope(OrgA);
        await FluentActions.Awaiting(() => scope.ServiceProvider.GetRequiredService<IReferenceDataService>().RefreshAsync(7))
            .Should().ThrowAsync<ConflictException>().WithMessage("Could not reach*");
    }
}
