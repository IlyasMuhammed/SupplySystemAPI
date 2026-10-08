using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using SMS.Modules.Integration.Core.Reference;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Gateway.Validation;
using SMS.Modules.Integration.Models;
using SMS.Modules.Integration.Tests.Connections;
using SMS.Modules.Integration.Tests.Fakes;
using SMS.Shared.Common;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Tests.Setup;

/// <summary>A base-currency resolver with a separate purchase base (A35 D-7 / D-19).</summary>
internal sealed class DomainBaseCurrency : IBaseCurrencyResolver
{
    private readonly string? _sale;
    private readonly string? _purchase;
    public DomainBaseCurrency(string? sale, string? purchase) { _sale = sale; _purchase = purchase; }

    public Task<BaseCurrencyInfo> ResolveAsync(Guid organizationId, CancellationToken ct = default) =>
        Task.FromResult(new BaseCurrencyInfo(_sale is not null, _sale));

    public Task<BaseCurrencyInfo> ResolveAsync(Guid organizationId, TransactionDomain domain, CancellationToken ct = default) =>
        Task.FromResult(domain == TransactionDomain.Purchase
            ? new BaseCurrencyInfo(_purchase is not null, _purchase)
            : new BaseCurrencyInfo(_sale is not null, _sale));
}

/// <summary>
/// A35 E-04 / D-19 — QuickBooks keeps comparing its home currency with the <b>sale</b> base; a purchase base that differs
/// from QuickBooks' home currency is called out by preflight, and bills (purchase documents) are refused while it does,
/// because SMS books them in a base QuickBooks does not keep. Sales are not affected.
/// </summary>
public class PurchaseBaseCurrencyTests
{
    private static readonly Guid Org = Guid.NewGuid();

    private static ConnectionsHarness Harness(string? sale, string? purchase) =>
        ConnectionsHarness.Create(extra: s =>
        {
            s.RemoveAll<IBaseCurrencyResolver>();
            s.AddScoped<IBaseCurrencyResolver>(_ => new DomainBaseCurrency(sale, purchase));
        });

    private static async Task<PreflightResultModel> PreflightAsync(ConnectionsHarness h)
    {
        var c = await h.SeedConnectionAsync(Org, ConnectionStatus.NeedsSetup);
        await SetupTestKit.SeedReferenceAsync(h, c, SampleReference.Data());
        await h.SeedSettingsAsync(c, s =>
        {
            s.DefaultIncomeAccountId = "1"; s.DefaultExpenseAccountId = "3"; s.MatchingConfirmedAt = DateTime.UtcNow.AddDays(-1);
        });

        await using var scope = h.Scope(Org);
        return await scope.ServiceProvider.GetRequiredService<IPreflightService>().RunAsync();
    }

    [Fact]
    public async Task A_purchase_base_other_than_the_home_currency_is_reported_and_says_bills_are_refused_but_sales_can_go_live()
    {
        await using var h = Harness(sale: "PKR", purchase: "USD");

        var result = await PreflightAsync(h);

        var check = result.Checks.Single(c => c.Code == "PURCHASE_BASE_CURRENCY");
        check.Status.Should().Be("Warn");
        check.Message.Should().Contain("USD").And.Contain("PKR").And.Contain("refused");
        result.Checks.Single(c => c.Code == "HOME_CURRENCY").Status.Should().Be("Pass");
        result.Passed.Should().BeTrue("sales invoices are unaffected — only bills are refused");
    }

    [Fact]
    public async Task A_purchase_base_equal_to_the_home_currency_passes()
    {
        await using var h = Harness(sale: "USD", purchase: "pkr");

        var result = await PreflightAsync(h);

        result.Checks.Single(c => c.Code == "PURCHASE_BASE_CURRENCY").Status.Should().Be("Pass");
    }

    [Fact]
    public async Task One_base_for_everything_adds_no_extra_check()
    {
        await using var h = Harness(sale: "PKR", purchase: "PKR");

        (await PreflightAsync(h)).Checks.Should().NotContain(c => c.Code == "PURCHASE_BASE_CURRENCY");
    }

    [Fact]
    public async Task The_default_resolver_reads_a_domain_base_through_the_currency_service()
    {
        var usd = Guid.NewGuid();
        var currency = new Mock<ICurrencyService>();
        currency.Setup(s => s.GetBaseCurrencyIdAsync(Org, TransactionDomain.Purchase, It.IsAny<CancellationToken>())).ReturnsAsync(usd);
        var codes = new Mock<ICurrencyCodeLookup>();
        codes.Setup(c => c.GetCodeAsync(usd, It.IsAny<CancellationToken>())).ReturnsAsync("USD");

        await using var h = ConnectionsHarness.Create(extra: s => { s.AddSingleton(currency.Object); s.AddSingleton(codes.Object); });
        await using var scope = h.Scope(Org);

        var info = await scope.ServiceProvider.GetRequiredService<IBaseCurrencyResolver>().ResolveAsync(Org, TransactionDomain.Purchase);

        info.Should().Be(new BaseCurrencyInfo(true, "USD"));
    }
}

/// <summary>A35 E-04 — the bill rule, on the validator the gateway and the executor use.</summary>
public class PurchaseBaseBillValidationTests : IAsyncLifetime
{
    private SyncHarness _h = null!;

    public Task InitializeAsync()
    {
        _h = new SyncHarness(configure: s =>
        {
            s.RemoveAll<IBaseCurrencyResolver>();
            s.AddScoped<IBaseCurrencyResolver>(_ => new DomainBaseCurrency("PKR", "USD"));
        });
        return _h.ConnectAsync();
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private Task<PayloadValidationResult> Validate(SyncKind kind, object payload) =>
        _h.Scoped(async sp =>
        {
            var db         = sp.GetRequiredService<IntegrationDbContext>();
            var connection = await db.Connections.SingleAsync();
            var settings   = await db.Settings.SingleAsync();
            var map = new EntityMap { ConnectionId = connection.Id, Kind = kind, ExternalId = "NEW", SourceSystem = QuickBooksSourceSystems.Scm };
            return await sp.GetRequiredService<IPayloadValidator>().ValidateAsync(kind, payload, map, connection, settings);
        });

    [Fact]
    public async Task A_bill_is_refused_while_the_purchase_base_is_not_quickbooks_home_currency()
    {
        var r = await Validate(SyncKind.Bill, TestPayloads.Bill());

        r.Errors.Should().ContainSingle(e => e.Code == "PURCHASE_BASE_NOT_HOME")
            .Which.Message.Should().Contain("USD").And.Contain("PKR").And.Contain("purchase base");
    }

    [Fact]
    public async Task A_sales_invoice_is_not_affected()
    {
        var r = await Validate(SyncKind.SalesInvoice, TestPayloads.Invoice());

        r.Errors.Should().NotContain(e => e.Code == "PURCHASE_BASE_NOT_HOME");
    }
}
