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
/// CURRENCY_PARTY_MISMATCH (plan S-10) judges a document by the currency its customer/vendor has <b>in QuickBooks</b>.
/// SMS only knows that currency when the party's stored payload is exactly what QuickBooks was given (or what it
/// will be created with). When the stored payload is newer than what reached QuickBooks, or the party is the
/// accountant's own record (adopted), QuickBooks' currency is unknown here — the rule must not guess, because a
/// wrong guess blocks a document QuickBooks would accept, with advice ("raise it in X") that is wrong.
/// Multicurrency is on, USD active; home currency PKR.
/// </summary>
public sealed class PartyCurrencyRuleTests : IAsyncLifetime
{
    private static readonly DateTime Date = TestPayloads.TxnDate;

    private readonly SyncHarness _h = new();
    private IntegrationConnection _connection = null!;

    public async Task InitializeAsync()
    {
        _connection = await _h.ConnectAsync();
        await using (var db = _h.DbAs(_h.OrgId))
        {
            (await db.Connections.SingleAsync()).MultiCurrencyEnabled = true;
            await db.SaveChangesAsync();
        }
        _h.Reference.Data = new RemoteReferenceData
        {
            Currencies  = [new RemoteCurrency("USD", "US Dollar"), new RemoteCurrency("EUR", "Euro")],
            Preferences = new RemotePreferences("PKR", true, true, true)
        };
        _h.Rates.Add("USD", "PKR", 278.5m, Date).Add("EUR", "PKR", 301m, Date);
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private static CustomerPayload Customer(string? currency)
    {
        var p = TestPayloads.Customer();
        p.CurrencyCode = currency;
        return p;
    }

    private static SalesInvoicePayload Invoice(string currency)
    {
        var p = TestPayloads.Invoice();
        p.CurrencyCode = currency;
        return p;
    }

    /// <summary>A customer map. <paramref name="pushed"/>: the payload QuickBooks was last given (null: never written by us).</summary>
    private async Task CustomerMapAsync(CustomerPayload stored, CustomerPayload? pushed, string? remoteId, LinkOrigin? origin)
    {
        var json = SyncPayloads.Serialize(stored);
        await using var db = _h.DbAs(_h.OrgId);
        db.EntityMaps.Add(new EntityMap
        {
            ConnectionId = _connection.Id, Kind = SyncKind.Customer, ExternalId = "C-1", SourceSystem = QuickBooksSourceSystems.Scm,
            DisplayLabel = "Acme Traders", RemoteId = remoteId, RemoteSyncToken = remoteId is null ? null : "0",
            State = remoteId is null ? SyncState.NotSynced : SyncState.Synced, LinkOrigin = origin,
            PayloadJson = json, PayloadFingerprint = SyncPayloads.Fingerprint(json),
            LastPushedFingerprint = pushed is null ? null : SyncPayloads.Fingerprint(SyncPayloads.Serialize(pushed))
        });
        await db.SaveChangesAsync();
    }

    private Task<PayloadValidationResult> Validate(SalesInvoicePayload payload) =>
        _h.Scoped(async sp =>
        {
            var db  = sp.GetRequiredService<IntegrationDbContext>();
            var map = new EntityMap { ConnectionId = _connection.Id, Kind = SyncKind.SalesInvoice, ExternalId = "NEW", SourceSystem = QuickBooksSourceSystems.Scm };
            return await sp.GetRequiredService<IPayloadValidator>()
                .ValidateAsync(SyncKind.SalesInvoice, payload, map, await db.Connections.SingleAsync(), await db.Settings.SingleAsync());
        });

    private static IEnumerable<string> Codes(PayloadValidationResult r) => r.Errors.Select(e => e.Code);

    // ── What SMS knows: enforced ─────────────────────────────────────────────

    [Fact]
    public async Task A_customer_we_created_whose_stored_data_is_what_QuickBooks_has_is_enforced()
    {
        var usd = Customer("USD");
        await CustomerMapAsync(stored: usd, pushed: usd, remoteId: "58", origin: LinkOrigin.Created);

        Codes(await Validate(Invoice("EUR"))).Should().ContainSingle().Which.Should().Be("CURRENCY_PARTY_MISMATCH");
        (await Validate(Invoice("USD"))).IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task A_customer_created_without_a_currency_is_in_the_home_currency_and_is_enforced()
    {
        var none = Customer(null);
        await CustomerMapAsync(stored: none, pushed: none, remoteId: "58", origin: LinkOrigin.Created);

        var r = await Validate(Invoice("USD"));

        r.Errors.Should().ContainSingle(e => e.Code == "CURRENCY_PARTY_MISMATCH").Which.Message.Should().Contain("is in PKR");
    }

    [Fact]
    public async Task A_customer_not_in_QuickBooks_yet_is_judged_by_the_currency_it_will_be_created_with()
    {
        await CustomerMapAsync(stored: Customer("EUR"), pushed: null, remoteId: null, origin: null);

        Codes(await Validate(Invoice("USD"))).Should().Contain("CURRENCY_PARTY_MISMATCH");
        (await Validate(Invoice("EUR"))).IsValid.Should().BeTrue();
    }

    // ── What SMS does not know: not guessed ──────────────────────────────────

    [Fact]
    public async Task A_customer_whose_newer_currency_has_not_reached_QuickBooks_does_not_block_an_invoice_in_its_QuickBooks_currency()
    {
        // Created in QuickBooks as USD; SCM then changed the customer to EUR. QuickBooks never changes a customer's
        // currency once set, so it still holds USD — a USD invoice is exactly what it accepts.
        await CustomerMapAsync(stored: Customer("EUR"), pushed: Customer("USD"), remoteId: "58", origin: LinkOrigin.Created);

        var r = await Validate(Invoice("USD"));

        Codes(r).Should().NotContain("CURRENCY_PARTY_MISMATCH", "the stored EUR is not what QuickBooks has");
        r.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task A_customer_adopted_from_QuickBooks_is_not_judged_by_the_currency_SCM_sends_for_it()
    {
        // Linked to the accountant's existing record: nothing of ours was written, so its currency is QuickBooks' own.
        var pkr = Customer("PKR");
        await CustomerMapAsync(stored: pkr, pushed: pkr, remoteId: "58", origin: LinkOrigin.Adopted);

        (await Validate(Invoice("USD"))).IsValid.Should().BeTrue("the accountant's customer may well be in USD; QuickBooks decides");
    }

    // ── Through the gateway and executor ─────────────────────────────────────

    [Fact]
    public async Task Once_the_newer_customer_data_reaches_QuickBooks_the_rule_applies_again_at_send_time()
    {
        // QuickBooks accepted the EUR customer (stored = pushed): a USD invoice is now refused before any call.
        var eur = Customer("EUR");
        await CustomerMapAsync(stored: eur, pushed: eur, remoteId: "58", origin: LinkOrigin.Created);
        await using (var db = _h.DbAs(_h.OrgId))
        {
            db.EntityMaps.Add(new EntityMap
            {
                ConnectionId = _connection.Id, Kind = SyncKind.Item, ExternalId = "I-1", SourceSystem = QuickBooksSourceSystems.Scm,
                DisplayLabel = "Widget", RemoteId = "R-I-1", RemoteSyncToken = "0", State = SyncState.Synced
            });
            await db.SaveChangesAsync();
        }

        var result = await _h.Send(Invoice("USD"));

        result.Outcome.Should().Be(GatewayOutcome.Invalid);
        result.Errors.Should().ContainSingle(e => e.Code == "CURRENCY_PARTY_MISMATCH");
        _h.Provider.WriteCalls.Should().Be(0);
    }
}
