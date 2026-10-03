using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using SMS.Modules.Integration.Core.Reference;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Models;
using SMS.Modules.Integration.Tests.Connections;
using SMS.Modules.Integration.Tests.Fakes;
using SMS.Shared.Common;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Tests.Setup;

public class PreflightServiceTests
{
    private static readonly Guid Org = Guid.NewGuid();

    private static ConnectionsHarness Harness(bool baseConfigured = true, string? baseCode = null) =>
        ConnectionsHarness.Create(extra: s =>
        {
            s.RemoveAll<IBaseCurrencyResolver>();
            s.AddScoped<IBaseCurrencyResolver>(_ => new FixedBaseCurrency(baseConfigured, baseCode));
        });

    private static async Task<PreflightResultModel> RunAsync(ConnectionsHarness h)
    {
        await using var scope = h.Scope(Org);
        return await scope.ServiceProvider.GetRequiredService<IPreflightService>().RunAsync();
    }

    private static PreflightCheckModel Check(PreflightResultModel result, string code) => result.Checks.Single(c => c.Code == code);

    /// <summary>A connection with reference data, both default accounts and matching confirmed — everything passes.</summary>
    private static async Task<IntegrationConnection> ReadyAsync(
        ConnectionsHarness h, RemoteReferenceDataOverrides? data = null, bool matching = true, bool accounts = true)
    {
        var c = await h.SeedConnectionAsync(Org, ConnectionStatus.NeedsSetup);
        await SetupTestKit.SeedReferenceAsync(h, c, data?.Build() ?? SampleReference.Data());
        await h.SeedSettingsAsync(c, s =>
        {
            if (accounts) { s.DefaultIncomeAccountId = "1"; s.DefaultExpenseAccountId = "3"; }
            if (matching) s.MatchingConfirmedAt = DateTime.UtcNow.AddDays(-1);
        });
        return c;
    }

    internal sealed record RemoteReferenceDataOverrides(
        string HomeCurrency = "PKR", bool MultiCurrency = false, bool CustomTxnNumbers = true, string Country = "PK", bool NoTaxCodes = false,
        bool? Discounts = true)
    {
        public Core.Providers.RemoteReferenceData Build()
        {
            var sample = SampleReference.Data(HomeCurrency, MultiCurrency, CustomTxnNumbers, Country);
            var data = new Core.Providers.RemoteReferenceData
            {
                Accounts = sample.Accounts, TaxCodes = sample.TaxCodes, Terms = sample.Terms, Currencies = sample.Currencies,
                Preferences = sample.Preferences! with { DiscountsEnabled = Discounts }, CompanyInfo = sample.CompanyInfo
            };
            return NoTaxCodes
                ? new Core.Providers.RemoteReferenceData
                {
                    Accounts = data.Accounts, TaxCodes = [], Terms = data.Terms, Currencies = data.Currencies,
                    Preferences = data.Preferences, CompanyInfo = data.CompanyInfo
                }
                : data;
        }
    }

    [Fact]
    public async Task Without_a_connection_only_the_connection_check_runs_and_fails()
    {
        await using var h = Harness();

        var result = await RunAsync(h);

        result.Checks.Should().ContainSingle().Which.Should().Match<PreflightCheckModel>(c => c.Code == "CONNECTION" && c.Status == "Fail");
        result.Passed.Should().BeFalse();
        result.CanGoLive.Should().BeFalse();
    }

    [Theory]
    [InlineData(true,  "Pass")]
    [InlineData(false, "Warn")]
    [InlineData(null,  "Warn")]
    public async Task The_allow_discount_preference_is_reported_but_never_blocks(bool? discounts, string status)
    {
        await using var h = Harness(baseCode: "PKR");
        await ReadyAsync(h, new RemoteReferenceDataOverrides(Discounts: discounts));

        var result = await RunAsync(h);

        Check(result, "DISCOUNTS").Status.Should().Be(status);
        result.Passed.Should().BeTrue("an organization that never discounts is unaffected");
        if (discounts == false) Check(result, "DISCOUNTS").Message.Should().Contain("Discount");
    }

    [Fact]
    public async Task A_ready_company_passes_and_can_go_live()
    {
        await using var h = Harness(baseCode: "PKR");
        await ReadyAsync(h);

        var result = await RunAsync(h);

        result.Checks.Select(c => c.Code).Should().Equal(
            "CONNECTION", "REFERENCE_DATA", "HOME_CURRENCY", "MULTICURRENCY", "COUNTRY", "CUSTOM_TXN_NUMBERS",
            "DISCOUNTS", "TAX_CODES", "ACCOUNTS_MAPPED", "MATCHING_CONFIRMED");
        result.Checks.Should().OnlyContain(c => c.Status == "Pass");
        result.Passed.Should().BeTrue();
        result.CanGoLive.Should().BeTrue();
    }

    [Fact]
    public async Task A_revoked_connection_fails_the_connection_check_but_the_rest_still_report()
    {
        await using var h = Harness(baseCode: "PKR");
        var c = await h.SeedConnectionAsync(Org, ConnectionStatus.Revoked);
        await SetupTestKit.SeedReferenceAsync(h, c);

        var result = await RunAsync(h);

        Check(result, "CONNECTION").Status.Should().Be("Fail");
        Check(result, "REFERENCE_DATA").Status.Should().Be("Pass");
        result.Passed.Should().BeFalse();
    }

    [Fact]
    public async Task Reference_data_never_loaded_fails()
    {
        await using var h = Harness();
        var c = await h.SeedConnectionAsync(Org, ConnectionStatus.NeedsSetup);

        var result = await RunAsync(h);

        Check(result, "REFERENCE_DATA").Status.Should().Be("Fail");
        Check(result, "CUSTOM_TXN_NUMBERS").Status.Should().Be("Warn");
        Check(result, "TAX_CODES").Status.Should().Be("Warn");
        result.Passed.Should().BeFalse();
    }

    // ── HOME_CURRENCY ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Home_currency_matching_the_base_currency_passes()
    {
        await using var h = Harness(baseCode: "pkr");
        await ReadyAsync(h);

        Check(await RunAsync(h), "HOME_CURRENCY").Status.Should().Be("Pass");
    }

    [Fact]
    public async Task Home_currency_differing_from_the_base_currency_fails()
    {
        await using var h = Harness(baseCode: "PKR");
        await ReadyAsync(h, new RemoteReferenceDataOverrides(HomeCurrency: "USD"));
        await using (var db = h.OpenAs(Org))
        {
            // The connection row carries the home currency too; keep it consistent with the snapshot.
            var c = db.Connections.Single();
            c.HomeCurrencyCode = "USD";
            await db.SaveChangesAsync();
        }

        var check = Check(await RunAsync(h), "HOME_CURRENCY");

        check.Status.Should().Be("Fail");
        check.Message.Should().Contain("USD").And.Contain("PKR").And.NotContain("SCM has no exchange rates");
    }

    [Fact]
    public async Task An_unreadable_base_currency_is_a_warning_to_confirm_by_eye()
    {
        await using var h = Harness(baseConfigured: true, baseCode: null);
        await ReadyAsync(h);

        var check = Check(await RunAsync(h), "HOME_CURRENCY");

        check.Status.Should().Be("Warn");
        check.Message.Should().Contain("Confirm it is PKR");
    }

    [Fact]
    public async Task No_base_currency_set_is_a_warning()
    {
        await using var h = Harness(baseConfigured: false, baseCode: null);
        await ReadyAsync(h);

        Check(await RunAsync(h), "HOME_CURRENCY").Message.Should().Contain("No base currency is set");
    }

    [Fact]
    public async Task Stored_records_in_other_currencies_are_called_out()
    {
        await using var h = Harness(baseConfigured: true, baseCode: null);
        var c = await ReadyAsync(h);
        await SetupTestKit.SeedPayloadAsync(h, c, SyncKind.SalesInvoice, "i-1", SetupTestKit.Invoice("USD", 17));
        await SetupTestKit.SeedPayloadAsync(h, c, SyncKind.SalesInvoice, "i-2", SetupTestKit.Invoice("PKR", 17));

        Check(await RunAsync(h), "HOME_CURRENCY").Message.Should().Contain("USD (1)");
    }

    [Fact]
    public async Task Home_currency_unknown_is_a_warning()
    {
        await using var h = Harness(baseCode: "PKR");
        var c = await h.SeedConnectionAsync(Org, ConnectionStatus.NeedsSetup);
        await using (var db = h.OpenAs(Org))
        {
            db.Connections.Single().HomeCurrencyCode = null;
            await db.SaveChangesAsync();
        }

        Check(await RunAsync(h), "HOME_CURRENCY").Status.Should().Be("Warn");
    }

    // ── MULTICURRENCY / COUNTRY / CUSTOM_TXN_NUMBERS ────────────────────────────────────────

    // Plan S-10: multicurrency on is supported now — foreign records go when QuickBooks has the currency
    // active and (documents) SMS has a rate. The check passes, explaining that, unless stored records say
    // otherwise.

    private static ConnectionsHarness HarnessWithRates(FakeExchangeRates rates) =>
        ConnectionsHarness.Create(extra: s =>
        {
            s.RemoveAll<IBaseCurrencyResolver>();
            s.AddScoped<IBaseCurrencyResolver>(_ => new FixedBaseCurrency(true, "PKR"));
            s.AddSingleton<IExchangeRateProvider>(rates);
        });

    private static async Task SetMultiCurrencyAsync(ConnectionsHarness h, bool on)
    {
        await using var db = h.OpenAs(Org);
        db.Connections.Single().MultiCurrencyEnabled = on;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Multicurrency_off_passes_and_says_foreign_records_are_refused()
    {
        await using var h = Harness(baseCode: "PKR");
        await ReadyAsync(h);

        var check = Check(await RunAsync(h), "MULTICURRENCY");

        check.Status.Should().Be("Pass");
        check.Message.Should().Contain("Multicurrency is off").And.Contain("only PKR").And.Contain("refused")
            .And.Contain("SMS's exchange rates").And.NotContain("SCM has no exchange rates");
    }

    [Fact]
    public async Task Multicurrency_on_with_nothing_foreign_stored_passes_and_explains_the_rules()
    {
        await using var h = Harness(baseCode: "PKR");
        await ReadyAsync(h, new RemoteReferenceDataOverrides(MultiCurrency: true));
        await SetMultiCurrencyAsync(h, true);

        var check = Check(await RunAsync(h), "MULTICURRENCY");

        check.Status.Should().Be("Pass");
        check.Message.Should().Contain("Multicurrency is on").And.Contain("active in QuickBooks")
            .And.Contain("Settings → Exchange Rates").And.Contain("exchange rate");
    }

    [Fact]
    public async Task Multicurrency_on_warns_about_stored_records_in_currencies_QuickBooks_lacks()
    {
        await using var h = Harness(baseCode: "PKR");
        var c = await ReadyAsync(h, new RemoteReferenceDataOverrides(MultiCurrency: true));   // PKR, USD active
        await SetMultiCurrencyAsync(h, true);
        await SetupTestKit.SeedPayloadAsync(h, c, SyncKind.Customer, "c-1",
            new CustomerPayload { ExternalId = "c-1", DisplayName = "Euro Client", CurrencyCode = "EUR" });

        var check = Check(await RunAsync(h), "MULTICURRENCY");

        check.Status.Should().Be("Warn");
        check.Message.Should().Contain("EUR (1)").And.Contain("not have active");
    }

    [Fact]
    public async Task Multicurrency_on_warns_about_document_currencies_with_no_SMS_rate_and_passes_once_one_exists()
    {
        var rates = new FakeExchangeRates();
        await using var h = HarnessWithRates(rates);
        var c = await ReadyAsync(h, new RemoteReferenceDataOverrides(MultiCurrency: true));
        await SetMultiCurrencyAsync(h, true);
        await SetupTestKit.SeedPayloadAsync(h, c, SyncKind.SalesInvoice, "i-1", SetupTestKit.Invoice("USD", 17));
        await using (var db = h.OpenAs(Org))
        {
            db.TaxCodeMappings.Add(new TaxCodeMapping { OrganizationId = Org, ConnectionId = c.Id, TaxPercent = 17, QboTaxCodeId = "10" });
            await db.SaveChangesAsync();
        }

        var missing = Check(await RunAsync(h), "MULTICURRENCY");
        missing.Status.Should().Be("Warn");
        missing.Message.Should().Contain("no exchange rate on file for USD → PKR").And.Contain("Settings → Exchange Rates");

        rates.Add("PKR", "USD", 0.0036m, DateTime.UtcNow.Date.AddDays(-1));   // the opposite pair counts too
        Check(await RunAsync(h), "MULTICURRENCY").Status.Should().Be("Pass");
    }

    [Fact]
    public async Task Home_currency_check_speaks_of_multicurrency_not_of_missing_exchange_rates()
    {
        await using var h = Harness(baseConfigured: true, baseCode: null);
        var c = await ReadyAsync(h);
        await SetupTestKit.SeedPayloadAsync(h, c, SyncKind.SalesInvoice, "i-1", SetupTestKit.Invoice("USD", 17));

        Check(await RunAsync(h), "HOME_CURRENCY").Message.Should()
            .Contain("refused while QuickBooks' multicurrency is off: USD (1)").And.NotContain("no exchange rates");

        await SetMultiCurrencyAsync(h, true);
        Check(await RunAsync(h), "HOME_CURRENCY").Message.Should().Contain("sent in their own currency").And.Contain("USD (1)");
    }

    [Theory]
    [InlineData("US", "Fail")]
    [InlineData("usa", "Fail")]
    [InlineData("United States", "Fail")]
    [InlineData("PK", "Pass")]
    [InlineData("GB", "Pass")]
    public async Task US_companies_are_refused(string country, string status)
    {
        await using var h = Harness(baseCode: "PKR");
        await ReadyAsync(h, new RemoteReferenceDataOverrides(Country: country));
        await using (var db = h.OpenAs(Org))
        {
            db.Connections.Single().Country = null;   // so the snapshot's country is what counts
            await db.SaveChangesAsync();
        }

        var result = await RunAsync(h);

        Check(result, "COUNTRY").Status.Should().Be(status);
        result.Passed.Should().Be(status == "Pass");
    }

    [Fact]
    public async Task Custom_transaction_numbers_off_fails()
    {
        await using var h = Harness(baseCode: "PKR");
        await ReadyAsync(h, new RemoteReferenceDataOverrides(CustomTxnNumbers: false));

        var result = await RunAsync(h);

        Check(result, "CUSTOM_TXN_NUMBERS").Status.Should().Be("Fail");
        Check(result, "CUSTOM_TXN_NUMBERS").Message.Should().Contain("invoice numbers could not be sent");
        result.Passed.Should().BeFalse();
        result.CanGoLive.Should().BeFalse();
    }

    // ── TAX_CODES ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task No_tax_codes_in_QuickBooks_is_a_warning()
    {
        await using var h = Harness(baseCode: "PKR");
        await ReadyAsync(h, new RemoteReferenceDataOverrides(NoTaxCodes: true));

        var result = await RunAsync(h);

        Check(result, "TAX_CODES").Status.Should().Be("Warn");
        result.Passed.Should().BeTrue("a warning never blocks");
    }

    [Fact]
    public async Task Tax_rates_seen_without_a_mapping_are_listed()
    {
        await using var h = Harness(baseCode: "PKR");
        var c = await ReadyAsync(h);
        await SetupTestKit.SeedPayloadAsync(h, c, SyncKind.SalesInvoice, "i-1", SetupTestKit.Invoice("PKR", 17, 5));
        await SetupTestKit.SeedPayloadAsync(h, c, SyncKind.Bill, "b-1", SetupTestKit.Bill("PKR", 17, null));
        await using (var db = h.OpenAs(Org))
        {
            db.TaxCodeMappings.Add(new TaxCodeMapping { OrganizationId = Org, ConnectionId = c.Id, TaxPercent = 17, QboTaxCodeId = "10" });
            await db.SaveChangesAsync();
        }

        var check = Check(await RunAsync(h), "TAX_CODES");

        check.Status.Should().Be("Warn");
        check.Message.Should().Contain("5%").And.NotContain("17%");
    }

    [Fact]
    public async Task SMS_tax_codes_seen_without_a_code_mapping_are_listed_and_a_percent_row_does_not_cover_them()
    {
        await using var h = Harness(baseCode: "PKR");
        var c = await ReadyAsync(h);
        var invoice = SetupTestKit.Invoice("PKR", 17, 0, 0);
        invoice.Lines[0].TaxCode = "GST17";
        invoice.Lines[1].TaxCode = "EXEMPT";
        await SetupTestKit.SeedPayloadAsync(h, c, SyncKind.SalesInvoice, "i-1", invoice);
        await using (var db = h.OpenAs(Org))
        {
            db.TaxCodeMappings.Add(new TaxCodeMapping { OrganizationId = Org, ConnectionId = c.Id, TaxPercent = 17, QboTaxCodeId = "10" });
            db.TaxCodeMappings.Add(new TaxCodeMapping { OrganizationId = Org, ConnectionId = c.Id, TaxPercent = 0, QboTaxCodeId = "11" });
            db.TaxCodeMappings.Add(new TaxCodeMapping { OrganizationId = Org, ConnectionId = c.Id, SourceTaxCode = "EXEMPT", TaxPercent = 0, QboTaxCodeId = "11" });
            await db.SaveChangesAsync();
        }

        var check = Check(await RunAsync(h), "TAX_CODES");

        check.Status.Should().Be("Warn");
        check.Message.Should().Contain("1 SMS tax code(s)").And.Contain("GST17").And.NotContain("EXEMPT")
            .And.NotContain("tax rate(s)", "the code-less 0% line is mapped by its percent row");
    }

    [Fact]
    public async Task Every_code_and_rate_mapped_passes()
    {
        await using var h = Harness(baseCode: "PKR");
        var c = await ReadyAsync(h);
        var invoice = SetupTestKit.Invoice("PKR", 17, 0);
        invoice.Lines[0].TaxCode = "GST17";
        await SetupTestKit.SeedPayloadAsync(h, c, SyncKind.SalesInvoice, "i-1", invoice);
        await using (var db = h.OpenAs(Org))
        {
            db.TaxCodeMappings.Add(new TaxCodeMapping { OrganizationId = Org, ConnectionId = c.Id, SourceTaxCode = "GST17", TaxPercent = 17, QboTaxCodeId = "10" });
            db.TaxCodeMappings.Add(new TaxCodeMapping { OrganizationId = Org, ConnectionId = c.Id, TaxPercent = 0, QboTaxCodeId = "11" });
            await db.SaveChangesAsync();
        }

        Check(await RunAsync(h), "TAX_CODES").Should().Match<PreflightCheckModel>(x => x.Status == "Pass" && x.Message.Contains("every SMS tax code"));
    }

    // ── ACCOUNTS_MAPPED / MATCHING_CONFIRMED ────────────────────────────────────────────────

    [Fact]
    public async Task Missing_default_accounts_fail()
    {
        await using var h = Harness(baseCode: "PKR");
        await ReadyAsync(h, accounts: false);

        var result = await RunAsync(h);

        Check(result, "ACCOUNTS_MAPPED").Status.Should().Be("Fail");
        result.Passed.Should().BeFalse();
    }

    [Fact]
    public async Task A_default_account_that_is_no_longer_active_fails()
    {
        await using var h = Harness(baseCode: "PKR");
        var c = await ReadyAsync(h);
        await using (var db = h.OpenAs(Org))
        {
            db.Settings.Single().DefaultIncomeAccountId = "7";   // inactive in the sample chart
            await db.SaveChangesAsync();
        }

        var check = Check(await RunAsync(h), "ACCOUNTS_MAPPED");

        check.Status.Should().Be("Fail");
        check.Message.Should().Contain("id 7");
    }

    [Fact]
    public async Task Unconfirmed_matching_warns_passes_preflight_but_blocks_going_live()
    {
        await using var h = Harness(baseCode: "PKR");
        await ReadyAsync(h, matching: false);

        var result = await RunAsync(h);

        Check(result, "MATCHING_CONFIRMED").Status.Should().Be("Warn");
        result.Passed.Should().BeTrue();
        result.CanGoLive.Should().BeFalse();
    }

    [Fact]
    public async Task The_default_resolver_reads_the_base_currency_through_Tenancys_service()
    {
        var currencies = new Mock<IOrganizationCurrencyService>();
        currencies.Setup(s => s.GetBaseCurrencyIdAsync(Org)).ReturnsAsync(Guid.NewGuid());
        await using var h = ConnectionsHarness.Create(extra: s => s.AddSingleton(currencies.Object));

        await using var scope = h.Scope(Org);
        var info = await scope.ServiceProvider.GetRequiredService<IBaseCurrencyResolver>().ResolveAsync(Org);

        info.Configured.Should().BeTrue();
        info.Code.Should().BeNull("nothing in SMS.Shared maps a currency id to its code yet");
    }

    [Fact]
    public async Task The_default_resolver_copes_without_Tenancy()
    {
        await using var h = ConnectionsHarness.Create();

        await using var scope = h.Scope(Org);
        var info = await scope.ServiceProvider.GetRequiredService<IBaseCurrencyResolver>().ResolveAsync(Org);

        info.Should().Be(new BaseCurrencyInfo(false, null));
    }
}
