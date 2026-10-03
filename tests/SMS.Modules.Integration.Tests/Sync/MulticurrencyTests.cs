using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Gateway.Validation;
using SMS.Modules.Integration.Tests.Fakes;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Tests.Sync;

/// <summary>
/// Plan S-10, the whole matrix: multicurrency off → foreign records refused (CURRENCY_NOT_HOME); on but the
/// currency not active in QuickBooks → CURRENCY_NOT_ACTIVE; on and active but no SMS rate for the document
/// date → EXCHANGE_RATE_MISSING; on, active and a rate (direct or reciprocal) → the built document carries
/// ExchangeRate (home units per foreign unit) and its CurrencyRef. Home-currency documents never carry one.
/// The harness company's home currency is PKR; invoices are dated 1 Sep 2026.
/// </summary>
public sealed class MulticurrencyTests : IAsyncLifetime
{
    private static readonly DateTime Date = TestPayloads.TxnDate;   // 2026-09-01

    private readonly SyncHarness _h = new();
    private IntegrationConnection _connection = null!;

    public Task InitializeAsync() => Task.CompletedTask;
    public async Task DisposeAsync() => await _h.DisposeAsync();

    private async Task ConnectAsync(bool multiCurrency, params string[] active)
    {
        _connection = await _h.ConnectAsync();
        await SetMultiCurrencyAsync(multiCurrency, active);
    }

    /// <summary>QuickBooks' multicurrency preference, and the currencies the reference snapshot says are active.</summary>
    private async Task SetMultiCurrencyAsync(bool on, params string[] active)
    {
        await using (var db = _h.DbAs(_h.OrgId))
        {
            (await db.Connections.SingleAsync()).MultiCurrencyEnabled = on;
            await db.SaveChangesAsync();
        }
        _h.Reference.Data = new RemoteReferenceData
        {
            Currencies  = active.Select(c => new RemoteCurrency(c, c + " name")).ToList(),
            Preferences = new RemotePreferences("PKR", on, true, true)
        };
    }

    private Task<PayloadValidationResult> Validate(SyncKind kind, object payload) =>
        _h.Scoped(async sp =>
        {
            var db  = sp.GetRequiredService<IntegrationDbContext>();
            var map = new EntityMap { ConnectionId = _connection.Id, Kind = kind, ExternalId = "NEW", SourceSystem = QuickBooksSourceSystems.Scm };
            return await sp.GetRequiredService<IPayloadValidator>()
                .ValidateAsync(kind, payload, map, await db.Connections.SingleAsync(), await db.Settings.SingleAsync());
        });

    private Task<BuiltRemoteEntity> Build(SyncKind kind, object payload) =>
        _h.Scoped(async sp =>
        {
            var db  = sp.GetRequiredService<IntegrationDbContext>();
            var map = new EntityMap { ConnectionId = _connection.Id, OrganizationId = _h.OrgId, Kind = kind, ExternalId = "NEW", SourceSystem = QuickBooksSourceSystems.Scm };
            return await sp.GetRequiredService<IQboObjectBuilder>()
                .BuildAsync(map, payload, await db.Connections.SingleAsync(), await db.Settings.SingleAsync(), dryRun: true);
        });

    /// <summary>
    /// A customer/vendor/item we created in QuickBooks, holding the payload it was sent with — as the executor
    /// leaves it: LinkOrigin Created, and the stored payload's fingerprint is the one last pushed.
    /// </summary>
    private async Task InQuickBooksAsync(SyncKind kind, string externalId, object? payload = null)
    {
        var json        = payload is null ? null : SyncPayloads.Serialize(payload);
        var fingerprint = json is null ? null : SyncPayloads.Fingerprint(json);
        await using var db = _h.DbAs(_h.OrgId);
        db.EntityMaps.Add(new EntityMap
        {
            ConnectionId = _connection.Id, Kind = kind, ExternalId = externalId, SourceSystem = QuickBooksSourceSystems.Scm,
            DisplayLabel = externalId, RemoteId = "R-" + externalId, RemoteSyncToken = "0", State = SyncState.Synced,
            LinkOrigin = LinkOrigin.Created, PayloadJson = json, PayloadFingerprint = fingerprint, LastPushedFingerprint = fingerprint
        });
        await db.SaveChangesAsync();
    }

    private static SalesInvoicePayload Invoice(string currency)
    {
        var p = TestPayloads.Invoice();
        p.CurrencyCode = currency;
        return p;
    }

    private static BillPayload Bill(string currency)
    {
        var p = TestPayloads.Bill();
        p.CurrencyCode = currency;
        return p;
    }

    private static CustomerPayload Customer(string? currency, string id = "C-1")
    {
        var p = TestPayloads.Customer(id: id);
        p.CurrencyCode = currency;
        return p;
    }

    private static VendorPayload Vendor(string? currency)
    {
        var p = TestPayloads.Vendor();
        p.CurrencyCode = currency;
        return p;
    }

    private static void ShouldFailWith(PayloadValidationResult r, string code, params string[] messageParts)
    {
        r.IsValid.Should().BeFalse();
        var error = r.Errors.Should().ContainSingle(e => e.Code == code,
            $"expected {code}; got {string.Join(", ", r.Errors.Select(e => e.Code))}").Subject;
        error.Field.Should().Be("currencyCode");
        foreach (var part in messageParts) error.Message.Should().Contain(part);
    }

    // ── Multicurrency off: the old home-currency rule ────────────────────────

    [Fact]
    public async Task Off_a_foreign_invoice_bill_and_party_are_refused_and_no_rate_is_asked_for()
    {
        await ConnectAsync(multiCurrency: false);
        _h.Rates.Add("USD", "PKR", 278.5m, Date.AddDays(-5));

        ShouldFailWith(await Validate(SyncKind.SalesInvoice, Invoice("USD")), "CURRENCY_NOT_HOME",
            "USD", "PKR", "cannot hold foreign-currency records unless multicurrency is turned on");
        ShouldFailWith(await Validate(SyncKind.Bill, Bill("usd")), "CURRENCY_NOT_HOME", "USD");
        ShouldFailWith(await Validate(SyncKind.Customer, Customer("USD")), "CURRENCY_NOT_HOME", "USD");

        _h.Rates.Asked.Should().BeEmpty();
        var message = (await Validate(SyncKind.SalesInvoice, Invoice("USD"))).Errors.Single().Message;
        message.Should().NotContain("no exchange rates", "SMS has exchange rates now");
    }

    [Fact]
    public async Task Off_the_home_currency_passes_and_builds_without_an_exchange_rate()
    {
        await ConnectAsync(multiCurrency: false);
        await InQuickBooksAsync(SyncKind.Customer, "C-1", Customer("PKR"));

        (await Validate(SyncKind.SalesInvoice, Invoice("pkr"))).IsValid.Should().BeTrue();
        var invoice = (RemoteInvoice)(await Build(SyncKind.SalesInvoice, Invoice("pkr"))).Entity;
        invoice.CurrencyCode.Should().Be("PKR");
        invoice.ExchangeRate.Should().BeNull();
        _h.Rates.Asked.Should().BeEmpty();
    }

    // ── On, but the currency is not active in QuickBooks ─────────────────────

    [Fact]
    public async Task On_a_currency_QuickBooks_does_not_have_active_is_refused_for_documents_and_parties()
    {
        await ConnectAsync(multiCurrency: true, "PKR", "USD");
        _h.Rates.Add("EUR", "PKR", 301m, Date.AddDays(-1));

        ShouldFailWith(await Validate(SyncKind.SalesInvoice, Invoice("EUR")), "CURRENCY_NOT_ACTIVE",
            "EUR", "active currencies (PKR, USD)", "Settings → Currencies", "refresh the reference data");
        ShouldFailWith(await Validate(SyncKind.Bill, Bill("EUR")), "CURRENCY_NOT_ACTIVE", "EUR");
        ShouldFailWith(await Validate(SyncKind.Vendor, Vendor("EUR")), "CURRENCY_NOT_ACTIVE", "EUR");
    }

    [Fact]
    public async Task On_with_no_currencies_loaded_yet_every_foreign_currency_is_refused()
    {
        await ConnectAsync(multiCurrency: true);
        _h.Reference.Data = null;   // never refreshed: the connection says multicurrency, the snapshot is missing

        ShouldFailWith(await Validate(SyncKind.Customer, Customer("USD")), "CURRENCY_NOT_ACTIVE", "none are loaded");
    }

    // ── On and active, but no SMS rate ───────────────────────────────────────

    [Fact]
    public async Task On_and_active_a_foreign_document_without_a_rate_is_refused_but_a_foreign_party_needs_none()
    {
        await ConnectAsync(multiCurrency: true, "USD");

        ShouldFailWith(await Validate(SyncKind.SalesInvoice, Invoice("USD")), "EXCHANGE_RATE_MISSING",
            "USD", "PKR", "01 Sep 2026", "Settings → Exchange Rates");
        ShouldFailWith(await Validate(SyncKind.Bill, Bill("USD")), "EXCHANGE_RATE_MISSING", "Settings → Exchange Rates");
        _h.Rates.Asked.Should().Contain(("USD", "PKR", Date));

        (await Validate(SyncKind.Customer, Customer("USD"))).IsValid.Should().BeTrue("a customer has no date and needs no rate");
        var customer = (RemoteCustomer)(await Build(SyncKind.Customer, Customer("usd"))).Entity;
        customer.CurrencyCode.Should().Be("USD", "a party is sent in its currency (CurrencyRef)");
    }

    [Fact]
    public async Task A_rate_dated_after_the_document_does_not_count()
    {
        await ConnectAsync(multiCurrency: true, "USD");
        _h.Rates.Add("USD", "PKR", 280m, Date.AddDays(1));

        ShouldFailWith(await Validate(SyncKind.SalesInvoice, Invoice("USD")), "EXCHANGE_RATE_MISSING");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(0.000000001)]
    public async Task A_rate_that_is_not_positive_at_eight_places_is_no_rate(double rate)
    {
        await ConnectAsync(multiCurrency: true, "USD");
        _h.Rates.Add("USD", "PKR", (decimal)rate, Date);

        ShouldFailWith(await Validate(SyncKind.SalesInvoice, Invoice("USD")), "EXCHANGE_RATE_MISSING");
    }

    [Fact]
    public async Task Without_an_exchange_rate_source_at_all_foreign_documents_are_refused_not_crashed()
    {
        await using var bare = new SyncHarness(withExchangeRates: false);
        var connection = await bare.ConnectAsync();
        await using (var db = bare.DbAs(bare.OrgId))
        {
            (await db.Connections.SingleAsync()).MultiCurrencyEnabled = true;
            await db.SaveChangesAsync();
        }
        bare.Reference.Data = new RemoteReferenceData { Currencies = [new RemoteCurrency("USD", "US Dollar")] };

        var result = await bare.Scoped(async sp =>
        {
            var db  = sp.GetRequiredService<IntegrationDbContext>();
            var map = new EntityMap { ConnectionId = connection.Id, Kind = SyncKind.SalesInvoice, ExternalId = "NEW", SourceSystem = QuickBooksSourceSystems.Scm };
            return await sp.GetRequiredService<IPayloadValidator>()
                .ValidateAsync(SyncKind.SalesInvoice, Invoice("USD"), map, await db.Connections.SingleAsync(), await db.Settings.SingleAsync());
        });

        ShouldFailWith(result, "EXCHANGE_RATE_MISSING");
    }

    // ── On, active and a rate: ExchangeRate + CurrencyRef ────────────────────

    [Fact]
    public async Task A_direct_rate_is_sent_as_the_invoices_exchange_rate()
    {
        await ConnectAsync(multiCurrency: true, "USD");
        await InQuickBooksAsync(SyncKind.Customer, "C-1", Customer("USD"));
        _h.Rates.Add("USD", "PKR", 270m, Date.AddDays(-30)).Add("USD", "PKR", 278.5m, Date.AddDays(-2)).Add("USD", "PKR", 290m, Date.AddDays(3));

        (await Validate(SyncKind.SalesInvoice, Invoice("USD"))).IsValid.Should().BeTrue();
        var built   = await Build(SyncKind.SalesInvoice, Invoice("usd"));
        var invoice = (RemoteInvoice)built.Entity;

        invoice.CurrencyCode.Should().Be("USD");
        invoice.ExchangeRate.Should().Be(278.5m, "the latest rate on or before the invoice date: PKR per 1 USD");
        built.Warnings.Should().BeEmpty("a direct rate needs no note");
    }

    [Fact]
    public async Task Only_the_opposite_rate_on_file_its_reciprocal_is_sent_and_noted()
    {
        await ConnectAsync(multiCurrency: true, "USD");
        _h.Rates.Add("PKR", "USD", 0.0036m, Date.AddDays(-1));

        (await Validate(SyncKind.Bill, Bill("USD"))).IsValid.Should().BeTrue();
        var built = await Build(SyncKind.Bill, Bill("USD"));
        var bill  = (RemoteBill)built.Entity;

        bill.CurrencyCode.Should().Be("USD");
        bill.ExchangeRate.Should().Be(277.77777778m, "1 / 0.0036, to the eight places Finance keeps rates in");
        built.Warnings.Should().ContainSingle().Which.Should()
            .Contain("reciprocal").And.Contain("31 Aug 2026").And.Contain("1 USD = 277.77777778 PKR");
    }

    [Fact]
    public async Task On_a_home_currency_document_carries_no_exchange_rate_and_asks_for_none()
    {
        await ConnectAsync(multiCurrency: true, "USD");

        (await Validate(SyncKind.SalesInvoice, Invoice("PKR"))).IsValid.Should().BeTrue();
        var invoice = (RemoteInvoice)(await Build(SyncKind.SalesInvoice, Invoice("PKR"))).Entity;
        var bill    = (RemoteBill)(await Build(SyncKind.Bill, Bill("PKR"))).Entity;

        invoice.ExchangeRate.Should().BeNull();
        invoice.CurrencyCode.Should().Be("PKR");
        bill.ExchangeRate.Should().BeNull();
        _h.Rates.Asked.Should().BeEmpty();
    }

    // ── A party's currency must match its documents' (QuickBooks rule) ───────

    [Fact]
    public async Task On_an_invoice_in_another_currency_than_its_customer_is_refused()
    {
        await ConnectAsync(multiCurrency: true, "USD");
        _h.Rates.Add("USD", "PKR", 278.5m, Date);
        await InQuickBooksAsync(SyncKind.Customer, "C-1", Customer(null));      // none sent = the home currency

        var r = await Validate(SyncKind.SalesInvoice, Invoice("USD"));

        ShouldFailWith(r, "CURRENCY_PARTY_MISMATCH", "in USD", "customer 'Acme Traders' is in PKR", "Raise the invoice in PKR");
    }

    [Fact]
    public async Task On_a_home_currency_bill_for_a_foreign_vendor_is_refused_too()
    {
        await ConnectAsync(multiCurrency: true, "USD");
        await InQuickBooksAsync(SyncKind.Vendor, "V-1", Vendor("USD"));

        ShouldFailWith(await Validate(SyncKind.Bill, Bill("PKR")), "CURRENCY_PARTY_MISMATCH", "vendor 'Karachi Supplies' is in USD");
    }

    [Fact]
    public async Task The_party_check_needs_the_party_and_multicurrency()
    {
        await ConnectAsync(multiCurrency: true, "USD");
        _h.Rates.Add("USD", "PKR", 278.5m, Date);

        (await Validate(SyncKind.SalesInvoice, Invoice("USD"))).IsValid.Should().BeTrue("the customer has not been sent yet: nothing to compare");

        await InQuickBooksAsync(SyncKind.Customer, "C-1", Customer("usd"));
        (await Validate(SyncKind.SalesInvoice, Invoice("USD"))).IsValid.Should().BeTrue("same currency, any case");

        await SetMultiCurrencyAsync(false);
        await InQuickBooksAsync(SyncKind.Customer, "C-2", Customer("USD", id: "C-2"));
        var homeInvoice = Invoice("PKR");
        homeInvoice.CustomerExternalId = "C-2";
        (await Validate(SyncKind.SalesInvoice, homeInvoice)).Errors.Should().NotContain(e => e.Code == "CURRENCY_PARTY_MISMATCH",
            "with multicurrency off the party itself is refused instead");
    }

    [Fact]
    public async Task A_customer_adopted_from_QuickBooks_without_a_currency_of_ours_is_not_second_guessed()
    {
        await ConnectAsync(multiCurrency: true, "USD");
        _h.Rates.Add("USD", "PKR", 278.5m, Date);
        await using (var db = _h.DbAs(_h.OrgId))
        {
            db.EntityMaps.Add(new EntityMap
            {
                ConnectionId = _connection.Id, Kind = SyncKind.Customer, ExternalId = "C-1", SourceSystem = QuickBooksSourceSystems.Scm,
                DisplayLabel = "Acme", RemoteId = "58", State = SyncState.Synced, LinkOrigin = LinkOrigin.Adopted,
                PayloadJson = SyncPayloads.Serialize(Customer(null))
            });
            await db.SaveChangesAsync();
        }

        (await Validate(SyncKind.SalesInvoice, Invoice("USD"))).IsValid.Should().BeTrue(
            "the accountant's record may well be in USD; QuickBooks has the final word");
    }

    // ── Through the executor: the rate is visible in the sync log ────────────

    [Fact]
    public async Task A_dry_run_logs_the_exchange_rate_and_currency_it_would_send_and_notes_a_reciprocal()
    {
        await ConnectAsync(multiCurrency: true, "USD");
        await _h.UpdateSettingsAsync(s => s.Mode = SyncMode.DryRun);
        await InQuickBooksAsync(SyncKind.Customer, "C-1", Customer("USD"));
        await InQuickBooksAsync(SyncKind.Item, "I-1");
        _h.Rates.Add("PKR", "USD", 0.004m, Date.AddDays(-10));

        (await _h.Send(Invoice("USD"))).Outcome.Should().Be(GatewayOutcome.Accepted);
        await _h.DrainAsync();

        var map = await _h.MapAsync(SyncKind.SalesInvoice, "SI-1");
        map.State.Should().Be(SyncState.DryRunOk);
        map.Warning.Should().Contain("reciprocal").And.Contain("1 USD = 250 PKR");
        var log = (await _h.LogAsync(map.Id)).Last();
        log.RequestJson.Should().Contain("\"currencyCode\":\"USD\"").And.Contain("\"exchangeRate\":250");
    }

    [Fact]
    public async Task Live_the_provider_receives_the_rate_and_a_home_currency_invoice_has_none()
    {
        await ConnectAsync(multiCurrency: true, "USD");
        await InQuickBooksAsync(SyncKind.Customer, "C-1", Customer("USD"));
        await InQuickBooksAsync(SyncKind.Customer, "C-9", Customer("PKR", id: "C-9"));
        await InQuickBooksAsync(SyncKind.Item, "I-1");
        _h.Rates.Add("USD", "PKR", 281.25m, Date);

        await _h.Send(Invoice("USD"));
        var home = TestPayloads.Invoice(id: "SI-2", doc: "INV-0002", customer: "C-9");
        await _h.Send(home);
        await _h.DrainAsync();

        var sent = _h.Provider.Calls.Where(c => c.Operation == "Create" && c.Kind == SyncKind.SalesInvoice)
                                    .Select(c => (RemoteInvoice)c.Entity!).ToList();
        sent.Should().HaveCount(2);
        sent.Single(i => i.DocNumber == "INV-0001").Should().Match<RemoteInvoice>(i => i.CurrencyCode == "USD" && i.ExchangeRate == 281.25m);
        sent.Single(i => i.DocNumber == "INV-0002").Should().Match<RemoteInvoice>(i => i.CurrencyCode == "PKR" && i.ExchangeRate == null);
        (await _h.MapAsync(SyncKind.SalesInvoice, "SI-1")).State.Should().Be(SyncState.Synced);
    }

    [Fact]
    public async Task A_foreign_document_blocked_for_want_of_a_rate_goes_once_the_rate_exists()
    {
        await ConnectAsync(multiCurrency: true, "USD");
        await InQuickBooksAsync(SyncKind.Customer, "C-1", Customer("USD"));
        await InQuickBooksAsync(SyncKind.Item, "I-1");

        var first = await _h.Send(Invoice("USD"));
        first.Outcome.Should().Be(GatewayOutcome.Invalid);
        first.Errors.Should().ContainSingle(e => e.Code == "EXCHANGE_RATE_MISSING");
        (await _h.MapAsync(SyncKind.SalesInvoice, "SI-1")).LastErrorCode.Should().Be("EXCHANGE_RATE_MISSING");

        _h.Rates.Add("USD", "PKR", 278.5m, Date);
        (await _h.Send(Invoice("USD"))).Outcome.Should().Be(GatewayOutcome.Accepted, "the same payload is checked again, not short-circuited");
        await _h.DrainAsync();

        ((RemoteInvoice)_h.Provider.Calls.Single(c => c.Kind == SyncKind.SalesInvoice).Entity!).ExchangeRate.Should().Be(278.5m);
    }
}
