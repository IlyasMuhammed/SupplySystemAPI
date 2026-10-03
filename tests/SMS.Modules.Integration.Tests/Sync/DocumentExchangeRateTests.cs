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
/// S-5 + S-10: SCM fixes a foreign document's exchange rate when it becomes final (invoice issued, bill approved)
/// and sends it on the payload. QuickBooks must book the document at THAT rate — not at whatever SMS's rate table
/// says when the outbox runs — or SMS's base amounts and QuickBooks' home amounts differ for the same document after
/// an accountant corrects a rate. Found by the end-to-end tester (rate corrected 281.5 → 295 before the outbox ran;
/// QuickBooks got 295). The table is the fallback only. Home PKR, multicurrency on, USD active.
/// </summary>
public sealed class DocumentExchangeRateTests : IAsyncLifetime
{
    private static readonly DateTime Date = TestPayloads.TxnDate;

    private readonly SyncHarness _h = new();
    private IntegrationConnection _connection = null!;

    public async Task InitializeAsync()
    {
        _connection = await _h.ConnectAsync();
        await using var db = _h.DbAs(_h.OrgId);
        (await db.Connections.SingleAsync()).MultiCurrencyEnabled = true;
        foreach (var (kind, id, payload) in new (SyncKind, string, object)[]
                 {
                     (SyncKind.Customer, "C-1", Usd(TestPayloads.Customer())), (SyncKind.Vendor, "V-1", Usd(TestPayloads.Vendor())),
                     (SyncKind.Item, "I-1", TestPayloads.Item())
                 })
        {
            var json = SyncPayloads.Serialize(payload);
            db.EntityMaps.Add(new EntityMap
            {
                ConnectionId = _connection.Id, Kind = kind, ExternalId = id, SourceSystem = QuickBooksSourceSystems.Scm, DisplayLabel = id,
                RemoteId = "R-" + id, RemoteSyncToken = "0", State = SyncState.Synced, LinkOrigin = LinkOrigin.Created,
                PayloadJson = json, PayloadFingerprint = SyncPayloads.Fingerprint(json), LastPushedFingerprint = SyncPayloads.Fingerprint(json)
            });
        }
        await db.SaveChangesAsync();
        _h.Reference.Data = new RemoteReferenceData
        {
            Currencies  = [new RemoteCurrency("USD", "US Dollar")],
            Preferences = new RemotePreferences("PKR", true, true, true)
        };
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private static T Usd<T>(T party) where T : PartyPayload { party.CurrencyCode = "USD"; return party; }

    private static SalesInvoicePayload Invoice(decimal? ownRate, string? ownRateCurrency = "PKR", string currency = "USD")
    {
        var p = TestPayloads.Invoice();
        p.CurrencyCode             = currency;
        p.ExchangeRate             = ownRate;
        p.ExchangeRateCurrencyCode = ownRate is null ? null : ownRateCurrency;
        return p;
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

    [Fact]
    public async Task The_documents_own_rate_is_sent_even_when_SMSs_rate_table_was_corrected_since()
    {
        _h.Rates.Add("USD", "PKR", 295m, Date);   // the corrected table rate

        await _h.Send(Invoice(ownRate: 281.5m));
        await _h.DrainAsync();

        var sent = (RemoteInvoice)_h.Provider.Calls.Single(c => c.Operation == "Create" && c.Kind == SyncKind.SalesInvoice).Entity!;
        sent.ExchangeRate.Should().Be(281.5m, "the rate SMS booked the invoice at");
        (await _h.MapAsync(SyncKind.SalesInvoice, "SI-1")).Warning.Should().BeNull();
    }

    [Fact]
    public async Task With_its_own_rate_a_document_needs_no_table_rate_at_all()
    {
        (await Validate(SyncKind.SalesInvoice, Invoice(ownRate: 281.5m))).IsValid.Should().BeTrue();
        ((RemoteInvoice)(await Build(SyncKind.SalesInvoice, Invoice(ownRate: 281.5m))).Entity).ExchangeRate.Should().Be(281.5m);
        _h.Rates.Asked.Should().BeEmpty("the table is not asked when the document has a rate of its own");
    }

    [Fact]
    public async Task A_bill_is_sent_at_its_approval_rate_too()
    {
        _h.Rates.Add("USD", "PKR", 300m, Date);
        var bill = TestPayloads.Bill();
        bill.CurrencyCode             = "USD";
        bill.ExchangeRate             = 279.25m;
        bill.ExchangeRateCurrencyCode = "pkr";

        ((RemoteBill)(await Build(SyncKind.Bill, bill)).Entity).ExchangeRate.Should().Be(279.25m, "the currency is compared case-insensitively");
    }

    [Fact]
    public async Task An_own_rate_into_another_currency_than_QuickBooks_home_is_not_sent_the_table_decides_and_says_so()
    {
        _h.Rates.Add("USD", "PKR", 280m, Date);

        var built = await Build(SyncKind.SalesInvoice, Invoice(ownRate: 0.92m, ownRateCurrency: "EUR"));

        ((RemoteInvoice)built.Entity).ExchangeRate.Should().Be(280m);
        built.Warnings.Should().ContainSingle().Which.Should().Contain("into EUR, not QuickBooks' home currency PKR");

        _h.Rates.Clear();
        (await Validate(SyncKind.SalesInvoice, Invoice(ownRate: 0.92m, ownRateCurrency: "EUR"))).Errors
            .Should().ContainSingle(e => e.Code == "EXCHANGE_RATE_MISSING");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(0.000000001)]
    public async Task An_own_rate_that_is_no_rate_at_eight_places_falls_back_to_the_table(double rate)
    {
        _h.Rates.Add("USD", "PKR", 280m, Date);

        ((RemoteInvoice)(await Build(SyncKind.SalesInvoice, Invoice(ownRate: (decimal)rate))).Entity).ExchangeRate.Should().Be(280m);
    }

    [Fact]
    public async Task An_own_rate_is_sent_to_the_eight_places_QuickBooks_is_given()
    {
        ((RemoteInvoice)(await Build(SyncKind.SalesInvoice, Invoice(ownRate: 281.123456785m))).Entity).ExchangeRate.Should().Be(281.12345679m);
    }

    [Fact]
    public async Task A_home_currency_document_never_carries_an_exchange_rate_whatever_the_payload_says()
    {
        var p = Invoice(ownRate: 1m, currency: "PKR");
        p.CustomerExternalId = "C-PKR";

        ((RemoteInvoice)(await Build(SyncKind.SalesInvoice, p)).Entity).ExchangeRate.Should().BeNull();
    }

    [Fact]
    public void The_own_rate_is_written_last_and_only_when_present_so_older_payloads_keep_their_fingerprints()
    {
        var without = SyncPayloads.Serialize(Invoice(ownRate: null));
        without.Should().NotContain("exchangeRate");

        var with = SyncPayloads.Serialize(Invoice(ownRate: 281.50m));
        with.Should().EndWith(",\"exchangeRate\":281.5,\"exchangeRateCurrencyCode\":\"PKR\"}");
        ((SalesInvoicePayload)SyncPayloads.Deserialize(SyncKind.SalesInvoice, with)).ExchangeRate.Should().Be(281.5m);
    }
}
