using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Core.Settings;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Gateway;
using SMS.Modules.Integration.Tests.Fakes;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Integration.Tests.Sync;

/// <summary>Sync control the test sets per document type (no row = enabled, BR-29).</summary>
internal sealed class FakeSyncControl : ISyncControlReader
{
    public Dictionary<string, SyncControlDecision> Types { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<SyncControlDecision> CheckAsync(int connectionId, string documentType, CancellationToken ct = default) =>
        Task.FromResult(Types.TryGetValue(documentType, out var d) ? d : SyncControlDecision.Enabled);

    public Task<IReadOnlyDictionary<string, SyncControlDecision>> GetAllAsync(int connectionId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, SyncControlDecision>>(
            SyncControlDocumentTypes.All.ToDictionary(t => t, t => Types.TryGetValue(t, out var d) ? d : SyncControlDecision.Enabled));
}

internal sealed class FakeProviderDefaults : IProviderDefaultsReader
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyDictionary<string, string>> GetAsync(int connectionId, string documentType, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(Values);
}

internal sealed class CountingKick : IOutboxKick
{
    public List<int> Requests { get; } = new();
    public void Request(int connectionId) { lock (Requests) Requests.Add(connectionId); }
}

/// <summary>
/// A44 (D-19..D-24, D-50..D-55): payments, bill payments, sales receipts, credit memos, refund receipts and vendor
/// credits through the gateway, the dependency machinery (documents depending on documents), the executor's void per kind,
/// the generalised hold / release, the immediate kick and the posting / entity-sync targets.
/// </summary>
public class A44DocumentKindTests : IAsyncLifetime
{
    private readonly FakeSyncControl      _control  = new();
    private readonly FakeProviderDefaults _defaults = new();
    private readonly CountingKick         _kick     = new();
    private readonly SyncHarness          _h;

    public A44DocumentKindTests()
    {
        _h = new SyncHarness(configure: s =>
        {
            s.AddSingleton<ISyncControlReader>(_control);
            s.AddSingleton<IProviderDefaultsReader>(_defaults);
            s.AddSingleton<IOutboxKick>(_kick);
        });
    }

    public async Task InitializeAsync()
    {
        await _h.ConnectAsync();
        _h.Source.Has(TestPayloads.Customer());
        _h.Source.Has(TestPayloads.Vendor());
        _h.Source.Has(TestPayloads.Item());
        _h.Source.Has(TestPayloads.Invoice());
        _h.Source.Has(TestPayloads.Bill());
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    // ── Payloads ─────────────────────────────────────────────────────────────

    private static PaymentPayload Payment(string id = "PAY-1", decimal amount = 234m, string method = PaymentMethodCodes.Cash,
                                          params (string Invoice, decimal Amount)[] applications) => new()
    {
        ExternalId         = id,
        DocNumber          = "RCPT-" + id,
        CustomerExternalId = "C-1",
        TxnDate            = TestPayloads.TxnDate,
        CurrencyCode       = "PKR",
        Amount             = amount,
        Method             = method,
        Applications       = (applications.Length == 0 ? [("SI-1", amount)] : applications)
                                .Select(a => new PaymentApplicationPayload { InvoiceExternalId = a.Invoice, Amount = a.Amount }).ToList()
    };

    private static BillPaymentPayload BillPayment(string id = "BP-1", string method = PaymentMethodCodes.BankTransfer) => new()
    {
        ExternalId       = id,
        DocNumber        = "SP-" + id,
        VendorExternalId = "V-1",
        TxnDate          = TestPayloads.TxnDate,
        CurrencyCode     = "PKR",
        Amount           = 500m,
        Method           = method,
        Applications     = [new BillPaymentApplicationPayload { BillExternalId = "B-1", Amount = 500m }]
    };

    private static T Sales<T>(T p, string id, string? customer = null) where T : SalesDocumentPayload
    {
        var invoice = TestPayloads.Invoice();
        p.ExternalId           = id;
        p.DocNumber            = "SCN-" + id;
        p.CustomerExternalId   = customer;
        p.TxnDate              = TestPayloads.TxnDate;
        p.CurrencyCode         = "PKR";
        p.Lines                = invoice.Lines;
        p.ExpectedTaxAmount    = invoice.ExpectedTaxAmount;
        p.ExpectedTotal        = invoice.ExpectedTotal;
        p.SourceType           = SalesInvoicePayloadSources.Pos;
        return p;
    }

    private static VendorCreditPayload VendorCredit(string id = "VC-1") => new()
    {
        ExternalId        = id,
        DocNumber         = "CN-" + id,
        VendorExternalId  = "V-1",
        TxnDate           = TestPayloads.TxnDate,
        CurrencyCode      = "PKR",
        Lines             = [new BillLinePayload { LineNo = 1, Category = BillLineCategory.Other, Amount = 100m }],
        ExpectedTaxAmount = 0m,
        ExpectedTotal     = 100m,
        SourceKind        = VendorCreditSources.CreditNote
    };

    private RemoteEntity LastCreated(SyncKind kind) => _h.Provider.Calls.Last(c => c.Operation == "Create" && c.Kind == kind).Entity!;

    private string RemoteIdOf(SyncKind kind, string externalId) => _h.MapAsync(kind, externalId).GetAwaiter().GetResult().RemoteId!;

    // ── Dependencies ─────────────────────────────────────────────────────────

    [Fact]
    public void Dependencies_of_the_new_kinds_include_the_documents_they_apply_to()
    {
        SyncPayloads.DependenciesOf(SyncKind.Payment, Payment()).Should().BeEquivalentTo(
            [new GatewayDependency(SyncKind.Customer, "C-1"), new GatewayDependency(SyncKind.SalesInvoice, "SI-1")]);
        SyncPayloads.DependenciesOf(SyncKind.BillPayment, BillPayment()).Should().BeEquivalentTo(
            [new GatewayDependency(SyncKind.Vendor, "V-1"), new GatewayDependency(SyncKind.Bill, "B-1")]);

        var memo = Sales(new CreditMemoPayload { OriginalInvoiceExternalId = "SI-1", ApplyToOriginal = true }, "CM-1", "C-1");
        SyncPayloads.DependenciesOf(SyncKind.CreditMemo, memo).Should().BeEquivalentTo(
            [new GatewayDependency(SyncKind.Customer, "C-1"), new GatewayDependency(SyncKind.Item, "I-1"), new GatewayDependency(SyncKind.SalesInvoice, "SI-1")]);

        memo.ApplyToOriginal = false;
        SyncPayloads.DependenciesOf(SyncKind.CreditMemo, memo).Should().NotContain(d => d.Kind == SyncKind.SalesInvoice, "store credit is not applied");

        var walkIn = Sales(new SalesReceiptPayload(), "SR-1");
        SyncPayloads.DependenciesOf(SyncKind.SalesReceipt, walkIn).Should().Contain(new GatewayDependency(SyncKind.Customer, SyncPayloads.WalkInCustomerExternalId));

        SyncPayloads.DependenciesOf(SyncKind.VendorCredit, VendorCredit()).Should().BeEquivalentTo([new GatewayDependency(SyncKind.Vendor, "V-1")]);
    }

    [Fact]
    public void Every_new_payload_round_trips_through_the_stored_json()
    {
        foreach (var (kind, payload) in new (SyncKind, object)[]
                 {
                     (SyncKind.Payment, Payment()), (SyncKind.BillPayment, BillPayment()),
                     (SyncKind.SalesReceipt, Sales(new SalesReceiptPayload(), "SR-1")),
                     (SyncKind.CreditMemo, Sales(new CreditMemoPayload(), "CM-1", "C-1")),
                     (SyncKind.RefundReceipt, Sales(new RefundReceiptPayload(), "RR-1")),
                     (SyncKind.VendorCredit, VendorCredit())
                 })
        {
            var json = SyncPayloads.Serialize(payload);
            var back = SyncPayloads.Deserialize(kind, json);
            back.Should().BeOfType(payload.GetType());
            SyncPayloads.Serialize(back).Should().Be(json, "the fingerprint of a stored payload is stable");
            SyncPayloads.KindOf(payload).Should().Be(kind);
        }
    }

    // ── Payment (D-19) ───────────────────────────────────────────────────────

    [Fact]
    public async Task A_payment_waits_for_its_customer_and_invoice_then_goes_linked_to_the_invoice()
    {
        _defaults.Values["DepositToAccountRef.CASH"] = "ACC-TILL";
        _defaults.Values["PaymentMethodRef.CASH"]    = "PM-CASH";

        var result = await _h.Gateway(g => g.UpsertPaymentAsync(Payment()));
        result.Outcome.Should().Be(GatewayOutcome.WaitingOnDependency);
        result.MissingDependencies.Should().Contain(new GatewayDependency(SyncKind.SalesInvoice, "SI-1"));

        await _h.DrainAsync();

        (await _h.MapAsync(SyncKind.Payment, "PAY-1")).State.Should().Be(SyncState.Synced);
        var payment = (RemotePayment)LastCreated(SyncKind.Payment);
        payment.Lines.Should().ContainSingle().Which.InvoiceId.Should().Be(RemoteIdOf(SyncKind.SalesInvoice, "SI-1"));
        payment.TotalAmount.Should().Be(234m);
        payment.ReferenceNumber.Should().Be("RCPT-PAY-1");
        payment.DepositToAccountId.Should().Be("ACC-TILL");
        payment.PaymentMethodId.Should().Be("PM-CASH");
        _kick.Requests.Should().NotBeEmpty("queued work asks for an immediate outbox run");
    }

    [Fact]
    public async Task A_payment_allocating_more_than_it_is_is_refused_with_every_reason()
    {
        var bad = Payment(amount: 100m, method: "STORE_CREDIT", applications: [("SI-1", 150m)]);
        var result = await _h.Gateway(g => g.UpsertPaymentAsync(bad));

        result.Outcome.Should().Be(GatewayOutcome.Invalid);
        result.Errors.Select(e => e.Code).Should().BeEquivalentTo(["PAYMENT_METHOD_INVALID", "APPLICATIONS_EXCEED_AMOUNT"]);
        (await _h.MapAsync(SyncKind.Payment, "PAY-1")).State.Should().Be(SyncState.Blocked);
    }

    [Fact]
    public async Task An_unapplied_remainder_is_sent_and_noted()
    {
        await _h.Gateway(g => g.UpsertPaymentAsync(Payment(amount: 300m, applications: [("SI-1", 234m)])));
        await _h.DrainAsync();

        var map = await _h.MapAsync(SyncKind.Payment, "PAY-1");
        map.State.Should().Be(SyncState.Synced);
        ((RemotePayment)LastCreated(SyncKind.Payment)).TotalAmount.Should().Be(300m);
        map.Warning.Should().Contain("66.00").And.Contain("unapplied credit");
    }

    // ── Bill payment (D-20) ──────────────────────────────────────────────────

    // ── Not-for-sale products (D-55a, QA-1) ──────────────────────────────────

    private void NotSaleable(string id = "I-1")
    {
        var item = TestPayloads.Item(id);
        item.IsSold = false;
        _h.Source.Has(item);
    }

    private IEnumerable<ScriptedAccountingProvider.Call> IncomeFixes =>
        _h.Provider.Calls.Where(c => c.Operation == "Update" && c.Entity is RemoteItemIncomeAccountFix);

    [Fact]
    public async Task A_not_for_sale_product_gets_its_income_account_once_before_the_first_sale()
    {
        NotSaleable();
        await _h.Send(TestPayloads.Invoice());
        await _h.DrainAsync();

        var item = await _h.MapAsync(SyncKind.Item, "I-1");
        ((RemoteItem)_h.Provider.Calls.First(c => c.Operation == "Create" && c.Kind == SyncKind.Item).Entity!).IncomeAccountId
            .Should().BeNull("the item is created as SCM sent it");
        IncomeFixes.Should().ContainSingle().Which.Should().Match<ScriptedAccountingProvider.Call>(c =>
            c.RemoteId == item.RemoteId && ((RemoteItemIncomeAccountFix)c.Entity!).IncomeAccountId == "ACC-INC");
        item.Warning.Should().Be("Sold on INV-0001; income account added in QuickBooks.");
        (await _h.MapAsync(SyncKind.SalesInvoice, "SI-1")).State.Should().Be(SyncState.Synced);

        var fingerprint = item.LastPushedFingerprint;
        await _h.Send(TestPayloads.Invoice(id: "SI-2", doc: "INV-0002"));
        await _h.DrainAsync();
        IncomeFixes.Should().HaveCount(1, "an item is fixed once");
        (await _h.MapAsync(SyncKind.Item, "I-1")).LastPushedFingerprint.Should().Be(fingerprint, "the item payload is not re-sent (D-3)");
    }

    [Fact]
    public async Task A_sales_receipt_fixes_the_item_too()
    {
        NotSaleable();
        await _h.Gateway(g => g.UpsertSalesReceiptAsync(Sales(new SalesReceiptPayload(), "SR-1", "C-1")));
        await _h.DrainAsync();

        IncomeFixes.Should().ContainSingle();
        (await _h.MapAsync(SyncKind.SalesReceipt, "SR-1")).State.Should().Be(SyncState.Synced);
    }

    [Fact]
    public async Task Without_an_income_account_the_document_is_refused_and_goes_once_one_is_set()
    {
        await _h.UpdateSettingsAsync(s => s.DefaultIncomeAccountId = null);
        NotSaleable();
        await _h.Send(TestPayloads.Item() is var i ? Clone(i, sold: false) : null!);

        var refused = await _h.Send(TestPayloads.Invoice());
        refused.Outcome.Should().Be(GatewayOutcome.Invalid);
        refused.Errors.Should().ContainSingle(e => e.Code == SaleableItems.NotSaleableCode)
            .Which.Message.Should().Be("Product Widget is not set up for sale in QuickBooks: set a default income account or mark the product saleable.");

        await _h.UpdateSettingsAsync(s => s.DefaultIncomeAccountId = "ACC-INC");
        var connection = (await _h.MapAsync(SyncKind.SalesInvoice, "SI-1")).ConnectionId;
        await _h.Scoped(sp => sp.GetRequiredService<IBlockedRecordRevalidator>().RevalidateAsync(connection, _h.OrgId, RevalidationTrigger.AfterChange));
        await _h.DrainAsync();

        (await _h.MapAsync(SyncKind.SalesInvoice, "SI-1")).State.Should().Be(SyncState.Synced);
        IncomeFixes.Should().ContainSingle();
    }

    private static ItemPayload Clone(ItemPayload item, bool sold)
    {
        item.IsSold = sold;
        return item;
    }

    [Fact]
    public async Task Saleable_and_adopted_products_are_never_touched()
    {
        await _h.Send(TestPayloads.Invoice());
        await _h.DrainAsync();
        IncomeFixes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_bill_payment_needs_the_account_it_is_paid_from_and_goes_once_one_is_set()
    {
        var refused = await _h.Gateway(g => g.UpsertBillPaymentAsync(BillPayment()));
        refused.Outcome.Should().Be(GatewayOutcome.Invalid);
        refused.Errors.Should().ContainSingle(e => e.Code == "BANK_ACCOUNT_MISSING");

        _defaults.Values["BankAccountRef.BANK_TRANSFER"] = "ACC-BANK";
        (await _h.Gateway(g => g.UpsertBillPaymentAsync(BillPayment()))).Outcome.Should().Be(GatewayOutcome.WaitingOnDependency);
        await _h.DrainAsync();

        var payment = (RemoteBillPayment)LastCreated(SyncKind.BillPayment);
        payment.PayType.Should().Be(RemoteBillPayType.Check);
        payment.AccountId.Should().Be("ACC-BANK");
        payment.Lines.Should().ContainSingle().Which.BillId.Should().Be(RemoteIdOf(SyncKind.Bill, "B-1"));
    }

    [Fact]
    public async Task A_card_bill_payment_is_a_credit_card_payment()
    {
        _defaults.Values["BankAccountRef.CARD"] = "ACC-CC";
        await _h.Gateway(g => g.UpsertBillPaymentAsync(BillPayment(method: PaymentMethodCodes.Card)));
        await _h.DrainAsync();

        var payment = (RemoteBillPayment)LastCreated(SyncKind.BillPayment);
        (payment.PayType, payment.AccountId).Should().Be((RemoteBillPayType.CreditCard, "ACC-CC"));
    }

    // ── Sales receipt / walk-in customer (D-21, D-54) ────────────────────────

    [Fact]
    public async Task Walk_in_receipts_create_the_walk_in_customer_once()
    {
        await _h.Gateway(g => g.UpsertSalesReceiptAsync(Sales(new SalesReceiptPayload(), "SR-1")));
        await _h.Gateway(g => g.UpsertSalesReceiptAsync(Sales(new SalesReceiptPayload(), "SR-2")));
        await _h.DrainAsync();

        _h.Provider.Calls.Count(c => c.Operation == "Create" && c.Entity is RemoteCustomer { DisplayName: SyncPayloads.WalkInCustomerName })
            .Should().Be(1);
        var walkIn = RemoteIdOf(SyncKind.Customer, SyncPayloads.WalkInCustomerExternalId);
        _h.Provider.Calls.Where(c => c.Operation == "Create" && c.Entity is RemoteSalesReceipt)
            .Select(c => ((RemoteSalesReceipt)c.Entity!).CustomerId).Should().AllBe(walkIn).And.HaveCount(2);
    }

    [Fact]
    public async Task With_a_POS_default_customer_walk_in_documents_go_under_it_and_nothing_is_created()
    {
        _defaults.Values[ProviderDefaultKeys.PosDefaultCustomerRef] = "QB-WALKIN-7";
        await _h.Gateway(g => g.UpsertSalesReceiptAsync(Sales(new SalesReceiptPayload(), "SR-1")));
        await _h.DrainAsync();

        _h.Provider.Calls.Where(c => c.Operation == "Create" && c.Kind == SyncKind.Customer)
            .Select(c => ((RemoteCustomer)c.Entity!).DisplayName).Should().NotContain(SyncPayloads.WalkInCustomerName);
        ((RemoteSalesReceipt)LastCreated(SyncKind.SalesReceipt)).CustomerId.Should().Be("QB-WALKIN-7");
    }

    // ── Credit memo / refund receipt (D-23) ──────────────────────────────────

    [Fact]
    public async Task An_on_account_credit_memo_waits_for_its_invoice_and_is_built_to_apply_to_it()
    {
        var memo = Sales(new CreditMemoPayload { OriginalInvoiceExternalId = "SI-1", ApplyToOriginal = true }, "CM-1", "C-1");
        (await _h.Gateway(g => g.UpsertCreditMemoAsync(memo))).Outcome.Should().Be(GatewayOutcome.WaitingOnDependency);
        await _h.DrainAsync();

        var built = (RemoteCreditMemo)LastCreated(SyncKind.CreditMemo);
        built.ApplyToInvoiceId.Should().Be(RemoteIdOf(SyncKind.SalesInvoice, "SI-1"));
        built.ApplyAmount.Should().Be(memo.ExpectedTotal);
    }

    [Fact]
    public async Task A_refund_receipt_is_paid_from_the_refund_account()
    {
        _defaults.Values["RefundFromAccountRef"] = "ACC-REFUND";
        await _h.Gateway(g => g.UpsertRefundReceiptAsync(Sales(new RefundReceiptPayload(), "RR-1", "C-1")));
        await _h.DrainAsync();

        ((RemoteRefundReceipt)LastCreated(SyncKind.RefundReceipt)).RefundFromAccountId.Should().Be("ACC-REFUND");
    }

    // ── Vendor credit (D-24) ─────────────────────────────────────────────────

    [Fact]
    public async Task A_vendor_credit_posts_unitemised_lines_to_the_default_expense_account()
    {
        await _h.Gateway(g => g.UpsertVendorCreditAsync(VendorCredit()));
        await _h.DrainAsync();

        var credit = (RemoteVendorCredit)LastCreated(SyncKind.VendorCredit);
        credit.VendorId.Should().Be(RemoteIdOf(SyncKind.Vendor, "V-1"));
        credit.Lines.Should().ContainSingle().Which.AccountId.Should().Be("ACC-EXP");
    }

    // ── Void per kind (D-51) ─────────────────────────────────────────────────

    [Fact]
    public async Task A_synced_payment_is_voided_through_the_generic_void()
    {
        await _h.Gateway(g => g.UpsertPaymentAsync(Payment()));
        await _h.DrainAsync();

        (await _h.Gateway(g => g.VoidAsync(SyncKind.Payment, "PAY-1"))).State.Should().Be(SyncState.Pending);
        await _h.DrainAsync();

        (await _h.MapAsync(SyncKind.Payment, "PAY-1")).State.Should().Be(SyncState.Voided);
        _h.Provider.GenericVoids.Should().ContainSingle().Which.Kind.Should().Be(SyncKind.Payment);
    }

    [Fact]
    public async Task A_payment_never_sent_is_voided_here_without_a_call()
    {
        await _h.Gateway(g => g.UpsertPaymentAsync(Payment()));   // waiting on its dependencies
        (await _h.Gateway(g => g.VoidAsync(SyncKind.Payment, "PAY-1"))).State.Should().Be(SyncState.Voided);
        _h.Provider.GenericVoids.Should().BeEmpty();
    }

    [Fact]
    public async Task A_credit_memo_is_never_voided_but_flagged_for_the_accountant()
    {
        await _h.Gateway(g => g.UpsertCreditMemoAsync(Sales(new CreditMemoPayload(), "CM-1", "C-1")));
        await _h.DrainAsync();

        (await _h.Gateway(g => g.VoidAsync(SyncKind.CreditMemo, "CM-1"))).State.Should().Be(SyncState.NeedsResolution);
        var map = await _h.MapAsync(SyncKind.CreditMemo, "CM-1");
        map.LastErrorCode.Should().Be(QuickBooksGateway.AccountantActionCode);
        _h.Provider.GenericVoids.Should().BeEmpty();
    }

    // ── Sync control: skip / hold / release (D-14, D-50) ─────────────────────

    [Fact]
    public async Task A_type_switched_off_without_hold_is_disabled()
    {
        _control.Types[SyncControlDocumentTypes.VendorCredit] = new SyncControlDecision(false, false);
        (await _h.Gateway(g => g.UpsertVendorCreditAsync(VendorCredit()))).Outcome.Should().Be(GatewayOutcome.Disabled);
        (await _h.FindMapAsync(SyncKind.VendorCredit, "VC-1")).Should().BeNull();
    }

    [Fact]
    public async Task A_held_type_stores_its_records_and_releases_them_once_enabled()
    {
        _control.Types[SyncControlDocumentTypes.Payment] = new SyncControlDecision(false, true);
        var payment = Payment();
        _h.Source.Has(SyncKind.Payment, payment);

        (await _h.Gateway(g => g.UpsertPaymentAsync(payment))).State.Should().Be(SyncState.NotSynced);
        var held = await _h.MapAsync(SyncKind.Payment, "PAY-1");
        held.LastErrorCode.Should().Be(SyncHolds.HeldCode);
        (await _h.EntriesAsync(SyncKind.Payment, "PAY-1")).Should().BeEmpty("nothing is queued while held");

        var count = await _h.Scoped(sp => sp.GetRequiredService<IHeldRecordReleaser>().CountHeldAsync(held.ConnectionId, SyncControlDocumentTypes.Payment));
        count.Should().Be(1);

        // Still off: release does nothing.
        (await _h.Scoped(sp => sp.GetRequiredService<IHeldRecordReleaser>().ReleaseAsync(held.ConnectionId, SyncControlDocumentTypes.Payment))).Should().Be(0);

        _control.Types.Remove(SyncControlDocumentTypes.Payment);
        (await _h.Scoped(sp => sp.GetRequiredService<IHeldRecordReleaser>().ReleaseAsync(held.ConnectionId, SyncControlDocumentTypes.Payment))).Should().Be(1);
        await _h.DrainAsync();

        var map = await _h.MapAsync(SyncKind.Payment, "PAY-1");
        (map.State, map.LastErrorCode).Should().Be((SyncState.Synced, null));
        _h.Provider.Calls.Count(c => c.Operation == "Create" && c.Kind == SyncKind.Payment).Should().Be(1);
    }

    [Fact]
    public async Task Disabling_a_type_with_hold_holds_what_is_queued_but_not_sent()
    {
        _h.Source.Has(SyncKind.Payment, Payment());
        await _h.Gateway(g => g.UpsertPaymentAsync(Payment()));   // queued, waiting on its dependencies
        var map = await _h.MapAsync(SyncKind.Payment, "PAY-1");

        _control.Types[SyncControlDocumentTypes.Payment] = new SyncControlDecision(false, true);
        (await _h.Scoped(sp => sp.GetRequiredService<IHeldRecordReleaser>().HoldQueuedAsync(map.ConnectionId, SyncControlDocumentTypes.Payment))).Should().Be(1);

        (await _h.MapAsync(SyncKind.Payment, "PAY-1")).LastErrorCode.Should().Be(SyncHolds.HeldCode);
        (await _h.EntriesAsync(SyncKind.Payment, "PAY-1")).Should().OnlyContain(e => e.Status == OutboxStatus.Done);
    }

    [Fact]
    public async Task A_POS_sale_held_as_an_invoice_and_released_as_a_sales_receipt_closes_the_old_map()
    {
        _control.Types[SyncControlDocumentTypes.PosSale] = new SyncControlDecision(false, true);
        var invoice = TestPayloads.Invoice(id: "POS-9", doc: "INV-P-9");
        invoice.SourceType = SalesInvoicePayloadSources.Pos;
        await _h.Send(invoice);
        var old = await _h.MapAsync(SyncKind.SalesInvoice, "POS-9");
        old.LastErrorCode.Should().Be(PosSales.HeldCode);

        // SCM now shapes the cash sale as a sales receipt under the same id (D-21).
        var receipt = Sales(new SalesReceiptPayload(), "POS-9", "C-1");
        _h.Source.Records.Remove((SyncKind.SalesInvoice, "POS-9"));
        _h.Source.Has(SyncKind.SalesInvoice, receipt);   // asked for the invoice, it answers with the receipt

        _control.Types.Remove(SyncControlDocumentTypes.PosSale);
        await _h.Scoped(sp => sp.GetRequiredService<IHeldRecordReleaser>().ReleaseAsync(old.ConnectionId, SyncControlDocumentTypes.PosSale));

        (await _h.MapAsync(SyncKind.SalesInvoice, "POS-9")).State.Should().Be(SyncState.Voided, "superseded, never sent");
        (await _h.MapAsync(SyncKind.SalesReceipt, "POS-9")).LastErrorCode.Should().NotBe(PosSales.HeldCode);
    }

    // ── Kick (D-11) ──────────────────────────────────────────────────────────

    [Fact]
    public void The_kick_is_debounced_per_connection()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 10, 16, 9, 0, 0, TimeSpan.Zero));
        var kick  = new HangfireOutboxKick(new ServiceCollection().BuildServiceProvider(), clock, NullLogger<HangfireOutboxKick>.Instance)
        {
            ScheduleOverride = _ => true
        };

        kick.Request(1);
        kick.Request(1);
        kick.Request(2);
        kick.Scheduled.Should().Be(2, "one per connection inside the window");

        clock.Advance(HangfireOutboxKick.Window + TimeSpan.FromMilliseconds(1));
        kick.Request(1);
        kick.Scheduled.Should().Be(3);
    }

    [Fact]
    public void The_kick_never_throws_without_Hangfire()
    {
        var kick = new HangfireOutboxKick(new ServiceCollection().BuildServiceProvider(), TimeProvider.System, NullLogger<HangfireOutboxKick>.Instance);
        kick.Invoking(k => k.Request(1)).Should().NotThrow();
        kick.Scheduled.Should().Be(0);
    }

    // ── Targets (D-2) ────────────────────────────────────────────────────────

    private Task<T> Target<T>(Func<IFinancialPostingTarget, Task<T>> call) =>
        _h.Scoped(sp => call(sp.GetServices<IFinancialPostingTarget>().Single(t => t.TargetCode == PostingTargetCodes.QuickBooks)));

    private static FinancialTransaction Tx(FinDocType type, IAccountingDocument document) => new()
    {
        DocumentType     = type,
        SourceDocumentId = Guid.NewGuid(),
        SourceModule     = PostingSourceModules.Finance,
        TransactionDate  = TestPayloads.TxnDate,
        CurrencyCode     = "PKR",
        Metadata         = new FinancialDocumentMetadata { Document = document }
    };

    [Fact]
    public async Task The_posting_target_queues_the_document_and_is_active_while_connected()
    {
        (await Target(t => t.IsActiveAsync())).Should().BeTrue();

        var result = await Target(t => t.PostAsync(Tx(FinDocType.SalesInvoice, TestPayloads.Invoice())));
        (result.TargetCode, result.Status).Should().Be((PostingTargetCodes.QuickBooks, TargetPostingStatus.Queued));

        (await Target(t => t.PostAsync(Tx(FinDocType.Payment, Payment())))).Status.Should().Be(TargetPostingStatus.Queued);
        (await Target(t => t.PostAsync(new FinancialTransaction { DocumentType = FinDocType.Expense }))).Status.Should().Be(TargetPostingStatus.Skipped);
    }

    [Fact]
    public async Task Posting_properties_apply_part_of_a_credit_and_flag_it_once_sent()
    {
        var memo = Sales(new CreditMemoPayload { OriginalInvoiceExternalId = "SI-1", ApplyToOriginal = true }, "CM-7", "C-1");
        var tx = new FinancialTransaction
        {
            DocumentType = FinDocType.CreditNote, SourceDocumentId = Guid.NewGuid(), SourceModule = PostingSourceModules.Pos,
            TransactionDate = TestPayloads.TxnDate, CurrencyCode = "PKR",
            Metadata = new FinancialDocumentMetadata
            {
                Document   = memo,
                Properties = new Dictionary<string, string> { ["AppliedAmount"] = "100.50", ["FlagForAccountant"] = "Cash refund 133.50 not entered." }
            }
        };

        (await Target(t => t.PostAsync(tx))).Status.Should().Be(TargetPostingStatus.Queued, "the upsert is queued as usual");
        await _h.DrainAsync();

        ((RemoteCreditMemo)LastCreated(SyncKind.CreditMemo)).ApplyAmount.Should().Be(100.50m);
        var map = await _h.MapAsync(SyncKind.CreditMemo, "CM-7");
        map.RemoteId.Should().NotBeNull();
        (map.State, map.LastErrorCode, map.LastError).Should().Be(
            (SyncState.NeedsResolution, QuickBooksGateway.AccountantActionCode, "Cash refund 133.50 not entered."));
    }

    [Fact]
    public async Task The_posting_target_reports_a_held_document_as_held()
    {
        _control.Types[SyncControlDocumentTypes.Payment] = new SyncControlDecision(false, true);
        (await Target(t => t.PostAsync(Tx(FinDocType.Payment, Payment())))).Status.Should().Be(TargetPostingStatus.Held);
    }

    [Fact]
    public async Task Voiding_a_document_never_sent_is_skipped_and_reversing_a_synced_bill_flags_it()
    {
        var never = await Target(t => t.VoidAsync(new VoidRequest { DocumentType = FinDocType.Payment, SourceDocumentId = Guid.NewGuid() }));
        never.Status.Should().Be(TargetPostingStatus.Skipped);
        never.Error.Should().StartWith("NEVER_SYNCED");

        var billId = Guid.NewGuid();
        var bill = TestPayloads.Bill(id: billId.ToString());
        await _h.Send(bill);
        await _h.DrainAsync();

        var reversed = await Target(t => t.ReverseAsync(new VoidRequest
        {
            DocumentType = FinDocType.PurchaseBill, SourceDocumentId = billId, SourceDocumentNumber = "SUP-INV-1", Reason = "Wrong supplier"
        }));
        reversed.Status.Should().Be(TargetPostingStatus.Posted);
        (await _h.MapAsync(SyncKind.Bill, billId.ToString())).LastErrorCode.Should().Be(QuickBooksGateway.AccountantActionCode);
        _h.Provider.Calls.Should().NotContain(c => c.Operation == "Void");
    }

    [Fact]
    public async Task The_entity_sync_target_sends_customers_and_deactivates_on_delete()
    {
        var target = (Func<IEntitySyncTarget, Task<EntitySyncResult>> call) =>
            _h.Scoped(sp => call(sp.GetServices<IEntitySyncTarget>().Single(t => t.TargetCode == PostingTargetCodes.QuickBooks)));

        var created = await target(t => t.PushAsync(new EntitySyncRequest
        {
            EntityType = SyncEntityType.Customer, Action = SyncAction.Create, Document = TestPayloads.Customer("C-9", "Nine Traders")
        }));
        created.Success.Should().BeTrue();
        created.Outcome.Should().Be(EntitySyncOutcome.Queued);

        await target(t => t.PushAsync(new EntitySyncRequest
        {
            EntityType = SyncEntityType.Customer, Action = SyncAction.Delete, Document = TestPayloads.Customer("C-9", "Nine Traders")
        }));
        var stored = await _h.MapAsync(SyncKind.Customer, "C-9");
        stored.PayloadJson.Should().Contain("\"isActive\":false");
    }
}
