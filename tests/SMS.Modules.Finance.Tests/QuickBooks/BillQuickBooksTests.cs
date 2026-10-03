using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Integration;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Repositories;
using SMS.Modules.Finance.Services;
using SMS.Modules.Warehouse.Data;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Integration.QuickBooks;
using Xunit;

namespace SMS.Modules.Finance.Tests.QuickBooks;

/// <summary>The real supplier invoice service and repository over one in-memory database, with a recording gateway.</summary>
internal sealed class BillRig
{
    public required Guid                               Org;
    public required FinanceDbContext                   Finance;
    public required DemandDbContext                    Demand;
    public required RecordingQuickBooksGateway         Gateway;
    public required CountingPoLineVariants             PoLines;
    public required ListLogger<BillQuickBooksSource>   SourceLog;
    public required ListLogger<BillQuickBooksPublisher> PublisherLog;
    public required BillQuickBooksSource               Source;
    public required BillQuickBooksPublisher            Publisher;
    public required InvoiceService                     Service;
    public required SupplierInvoicePoster              Poster;

    /// <param name="knownBill">What the gateway answers when asked about any bill's status (null: it knows none).</param>
    public static BillRig New(IInterceptor? financeInterceptor = null, (SyncState State, string DocNumber, string RemoteId)? knownBill = null)
    {
        var dbName = Guid.NewGuid().ToString();
        var org    = Guid.NewGuid();
        var tenant = new StaticTenantContext { OrganizationId = org };

        var financeOptions = new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(dbName);
        if (financeInterceptor is not null) financeOptions.AddInterceptors(financeInterceptor);
        var finance   = new FinanceDbContext(financeOptions.Options, tenant);
        var demand    = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(dbName).Options, tenant);
        var warehouse = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseInMemoryDatabase(dbName).Options, tenant);

        var gateway      = new RecordingQuickBooksGateway();
        var poLines      = new CountingPoLineVariants(new DemandPurchaseOrderLineVariants(demand));
        var sourceLog    = new ListLogger<BillQuickBooksSource>();
        var publisherLog = new ListLogger<BillQuickBooksPublisher>();
        var source       = new BillQuickBooksSource(finance, poLines, gateway, sourceLog);
        var repo         = new InvoiceRepository(finance, demand, warehouse, new SupplierLedgerService(finance), new FakeSupplierNameLookup());
        var publisher    = new BillQuickBooksPublisher(finance, source, publisherLog, new StatusAnsweringGateway(gateway, knownBill));

        return new BillRig
        {
            Org = org, Finance = finance, Demand = demand, Gateway = gateway, PoLines = poLines,
            SourceLog = sourceLog, PublisherLog = publisherLog, Source = source, Publisher = publisher,
            Service = new InvoiceService(repo, new Mock<IBackgroundJobClient>().Object, publisher),
            Poster  = new SupplierInvoicePoster(finance, repo)
        };
    }

    /// <summary>The recording gateway, except that it answers a status for any bill asked about.</summary>
    private sealed class StatusAnsweringGateway(RecordingQuickBooksGateway inner, (SyncState State, string DocNumber, string RemoteId)? bill) : IQuickBooksGateway
    {
        public Task<GatewayResult> UpsertCustomerAsync(CustomerPayload payload, CancellationToken ct = default)         => inner.UpsertCustomerAsync(payload, ct);
        public Task<GatewayResult> UpsertVendorAsync(VendorPayload payload, CancellationToken ct = default)             => inner.UpsertVendorAsync(payload, ct);
        public Task<GatewayResult> UpsertItemAsync(ItemPayload payload, CancellationToken ct = default)                 => inner.UpsertItemAsync(payload, ct);
        public Task<GatewayResult> UpsertSalesInvoiceAsync(SalesInvoicePayload payload, CancellationToken ct = default) => inner.UpsertSalesInvoiceAsync(payload, ct);
        public Task<GatewayResult> VoidSalesInvoiceAsync(string externalId, CancellationToken ct = default)             => inner.VoidSalesInvoiceAsync(externalId, ct);
        public Task<GatewayResult> UpsertBillAsync(BillPayload payload, CancellationToken ct = default)                 => inner.UpsertBillAsync(payload, ct);

        public Task<IReadOnlyList<SyncStatus>> GetStatusAsync(SyncKind kind, IReadOnlyCollection<string> externalIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SyncStatus>>(bill is { } b
                ? externalIds.Select(id => new SyncStatus(kind, id, b.State, b.RemoteId, b.DocNumber, null, null, DateTime.UtcNow, null)).ToList()
                : []);

        public Task<GatewayResult> FlagForAccountantAsync(SyncKind kind, string externalId, string reason, CancellationToken ct = default) =>
            inner.FlagForAccountantAsync(kind, externalId, reason, ct);
    }

    /// <summary>A purchase order whose lines ordered these variants (null: a line that named none).</summary>
    public async Task<(PurchaseOrder Po, List<PurchaseOrderLine> Lines)> PurchaseOrderAsync(Guid supplier, params Guid?[] variants)
    {
        var po = new PurchaseOrder
        {
            UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), PoNumber = $"PO-2026-{Random.Shared.Next(10000, 99999)}",
            SupplierId = supplier, SupplierName = "Karachi Steel", Status = "RECEIVED", CreatedBy = 1, CreatedDate = DateTime.UtcNow
        };
        for (var i = 0; i < variants.Length; i++)
            po.Lines.Add(new PurchaseOrderLine
            {
                UUID = Guid.NewGuid(), LineNo = i + 1, VariantUuid = variants[i], ItemDescription = $"PO item {i + 1}",
                Quantity = 10m, UnitPrice = 100m, LineTotal = 1000m, QtyReceived = 10m
            });
        po.TotalAmount = po.Lines.Sum(l => l.LineTotal);
        Demand.PurchaseOrders.Add(po);
        await Demand.SaveChangesAsync();
        Demand.ChangeTracker.Clear();
        return (po, po.Lines.OrderBy(l => l.LineNo).ToList());
    }

    /// <summary>A supplier invoice against the whole purchase order, one line per PO line, plus a line of our own.</summary>
    public Task<Guid> InvoiceAsync(PurchaseOrder po, IReadOnlyList<PurchaseOrderLine> lines, string? supplierNo = "KSW/881", decimal tax = 0m) =>
        Service.CreateAsync(new CreateInvoiceRequest
        {
            SupplierId = po.SupplierId, SupplierInvoiceNo = supplierNo, PoUuid = po.UUID,
            InvoiceDate = new DateTime(2026, 9, 15), ReceivedDate = new DateTime(2026, 9, 16), DueDate = new DateTime(2026, 10, 15),
            Currency = "PKR", TaxAmount = tax,
            Lines = [.. lines.Select(l => new InvoiceLineRequest { PoLineUuid = l.UUID, ItemDescription = l.ItemDescription, QtyInvoiced = l.Quantity, UnitPrice = l.UnitPrice })]
        }, createdBy: 1);

    public void Forget()
    {
        Gateway.Calls.Clear();
        Gateway.Bills.Clear();
        PoLines.Calls.Clear();
        Finance.ChangeTracker.Clear();
        Demand.ChangeTracker.Clear();
    }

    public Task<Invoice> LoadAsync(Guid uuid) => Finance.Invoices.AsNoTracking().SingleAsync(i => i.UUID == uuid);
}

public class BillQuickBooksTests
{
    // ── Triggers ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Creating_a_supplier_invoice_sends_nothing_approving_it_sends_the_bill_once()
    {
        var rig = BillRig.New();
        var supplier = Guid.NewGuid();
        Guid rod = Guid.NewGuid(), cement = Guid.NewGuid();
        var (po, lines) = await rig.PurchaseOrderAsync(supplier, rod, cement, null);
        var uuid = await rig.InvoiceAsync(po, lines, tax: 450m);

        rig.Gateway.Calls.Should().BeEmpty("an unapproved invoice is not yet booked");

        (await rig.Service.ApproveAsync(uuid, "OK", approvedBy: 7)).Should().BeTrue();

        var bill = rig.Gateway.Bills.Should().ContainSingle().Subject;
        var invoice = await rig.LoadAsync(uuid);
        invoice.MatchStatus.Should().Be("Approved");
        bill.ExternalId.Should().Be(uuid.ToString());
        bill.DocNumber.Should().Be("KSW/881");
        bill.VendorExternalId.Should().Be(supplier.ToString());
        bill.PrivateNote.Should().Be($"{invoice.InvoiceNumber} · PO {po.PoNumber}");
        bill.ExpectedTaxAmount.Should().Be(450m);
        bill.ExpectedTotal.Should().Be(3450m);
        bill.Lines.Select(l => (l.Category, l.ItemExternalId)).Should().Equal(
            (BillLineCategory.Goods, rod.ToString()), (BillLineCategory.Goods, cement.ToString()), (BillLineCategory.Other, (string?)null));
        (bill.Lines.Sum(l => l.Amount) + bill.ExpectedTaxAmount).Should().Be(bill.ExpectedTotal, "the real repository's totals add up the gateway's way");

        rig.PoLines.Calls.Should().ContainSingle("every PO line of the invoice is resolved in one query")
            .Which.Should().BeEquivalentTo(lines.Select(l => l.UUID));
    }

    [Fact]
    public async Task Approving_an_invoice_that_does_not_exist_sends_nothing()
    {
        var rig = BillRig.New();

        (await rig.Service.ApproveAsync(Guid.NewGuid(), null, 7)).Should().BeFalse();

        rig.Gateway.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Editing_an_approved_invoices_bill_fields_sends_it_again()
    {
        var rig = BillRig.New();
        var (po, lines) = await rig.PurchaseOrderAsync(Guid.NewGuid(), Guid.NewGuid());
        var uuid = await rig.InvoiceAsync(po, lines);
        await rig.Service.ApproveAsync(uuid, null, 7);
        rig.Forget();

        await rig.Service.PatchAsync(uuid, new PatchInvoiceRequest { SupplierInvoiceNo = "KSW/881-R", DueDate = new DateTime(2026, 11, 1) }, 7);

        var bill = rig.Gateway.Bills.Should().ContainSingle().Subject;
        bill.DocNumber.Should().Be("KSW/881-R");
        bill.DueDate.Should().Be(new DateTime(2026, 11, 1));
    }

    [Fact]
    public async Task A_payment_or_note_only_edit_of_an_approved_invoice_sends_nothing()
    {
        var rig = BillRig.New();
        var (po, lines) = await rig.PurchaseOrderAsync(Guid.NewGuid(), Guid.NewGuid());
        var uuid = await rig.InvoiceAsync(po, lines);
        await rig.Service.ApproveAsync(uuid, null, 7);
        rig.Forget();

        // Scheduled, not Paid: since S-7 a payment status can only be set by hand to Unpaid or Scheduled
        // (the rest follows the payments recorded), so "Paid" here would now be refused before anything ran.
        await rig.Service.PatchAsync(uuid, new PatchInvoiceRequest { PaymentStatus = "Scheduled", PaymentMethod = "CHEQUE", Notes = "Paid by cheque", AttachmentUrl = "/x.pdf" }, 7);

        (await rig.LoadAsync(uuid)).PaymentStatus.Should().Be("Scheduled");
        rig.Gateway.Calls.Should().BeEmpty("payments are not synced (plan D-8), and none of these are on a bill");
    }

    [Fact]
    public async Task Approving_through_an_edit_of_the_match_status_is_refused_and_sends_nothing()
    {
        // Was: "…sends_the_bill". Patching MatchStatus to Approved booked nothing to the supplier ledger yet
        // sent QuickBooks a bill. Since S-7 it is refused — approval is its own action, which posts the
        // ledger debit — and so nothing reaches QuickBooks until that action runs.
        var rig = BillRig.New();
        var (po, lines) = await rig.PurchaseOrderAsync(Guid.NewGuid(), Guid.NewGuid());
        var uuid = await rig.InvoiceAsync(po, lines);

        await rig.Service.Invoking(s => s.PatchAsync(uuid, new PatchInvoiceRequest { MatchStatus = "Approved" }, 7))
            .Should().ThrowAsync<ConflictException>().WithMessage("*Approve action*");

        (await rig.LoadAsync(uuid)).MatchStatus.Should().Be("Matched");
        rig.Gateway.Calls.Should().BeEmpty();
        (await rig.Finance.SupplierLedgerEntries.AsNoTracking().AnyAsync(e => e.ReferenceId == uuid)).Should().BeFalse();

        (await rig.Service.ApproveAsync(uuid, null, 7)).Should().BeTrue();
        rig.Gateway.Bills.Should().ContainSingle().Which.ExternalId.Should().Be(uuid.ToString());
    }

    [Fact]
    public async Task Editing_an_invoice_that_is_not_approved_sends_nothing()
    {
        var rig = BillRig.New();
        var (po, lines) = await rig.PurchaseOrderAsync(Guid.NewGuid(), Guid.NewGuid());
        var uuid = await rig.InvoiceAsync(po, lines);

        await rig.Service.PatchAsync(uuid, new PatchInvoiceRequest { SupplierInvoiceNo = "NEW", DueDate = new DateTime(2026, 12, 1) }, 7);
        await rig.Service.RejectAsync(uuid, "Wrong prices", 7);

        rig.Gateway.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Editing_the_tax_of_an_approved_invoice_is_refused_so_it_stays_approved_and_nothing_is_sent()
    {
        // Was: "…un_approves_it_so_no_bill_is_sent". A tax edit used to recompute the match status, silently
        // un-approving an invoice whose ledger debit stayed — and the bill in QuickBooks with it. Since S-7
        // an approved invoice's tax cannot change (reverse it instead): it stays approved at the total that
        // was booked and sent, and nothing new goes to QuickBooks.
        var rig = BillRig.New();
        var (po, lines) = await rig.PurchaseOrderAsync(Guid.NewGuid(), Guid.NewGuid());
        var uuid = await rig.InvoiceAsync(po, lines);
        await rig.Service.ApproveAsync(uuid, null, 7);
        rig.Forget();

        await rig.Service.Invoking(s => s.PatchAsync(uuid, new PatchInvoiceRequest { TaxAmount = 170m }, 7))
            .Should().ThrowAsync<ConflictException>().WithMessage("*Reverse the invoice instead*");

        var invoice = await rig.LoadAsync(uuid);
        invoice.MatchStatus.Should().Be("Approved");
        invoice.TaxAmount.Should().Be(0m);
        invoice.TotalAmount.Should().Be(1000m);
        rig.Gateway.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_reversed_invoice_is_never_sent_again_by_the_publisher_push_now_or_the_reconciliation()
    {
        var rig = BillRig.New();
        var (po, lines) = await rig.PurchaseOrderAsync(Guid.NewGuid(), Guid.NewGuid());
        var uuid = await rig.InvoiceAsync(po, lines);
        await rig.Service.ApproveAsync(uuid, null, 7);
        rig.Gateway.Bills.Should().ContainSingle("the approval sent it");
        rig.Forget();

        (await rig.Service.ReverseAsync(uuid, "Duplicate of the supplier's earlier bill", 7)).Should().BeTrue();

        rig.Gateway.Bills.Should().BeEmpty("the gateway cannot void a bill, and a reversed one is not re-sent");
        await rig.Publisher.PublishIfApprovedAsync(uuid);
        await rig.Source.PushAsync(SyncKind.Bill, [uuid.ToString()]);
        (await rig.Source.PushAllAsync(SyncKind.Bill, null)).Should().Be(0);
        rig.Gateway.Bills.Should().BeEmpty();
        rig.SourceLog.At(LogLevel.Information).Should().ContainSingle().Which.Message.Should().Contain("Reversed");
    }

    [Fact]
    public async Task Reversing_an_invoice_whose_bill_reached_the_gateway_warns_that_it_must_be_voided_by_hand()
    {
        var rig = BillRig.New(knownBill: (SyncState.Synced, "QB-1042", "77"));
        var (po, lines) = await rig.PurchaseOrderAsync(Guid.NewGuid(), Guid.NewGuid());
        var uuid = await rig.InvoiceAsync(po, lines);
        await rig.Service.ApproveAsync(uuid, null, 7);

        await rig.Service.ReverseAsync(uuid, "Entered against the wrong supplier", 7);

        var warning = rig.PublisherLog.At(LogLevel.Warning).Should().ContainSingle().Subject;
        warning.Message.Should().Contain(uuid.ToString()).And.Contain("Synced").And.Contain("QB-1042").And.Contain("void or delete it in QuickBooks by hand");
    }

    [Fact]
    public async Task Reversing_an_invoice_that_never_reached_QuickBooks_only_notes_it()
    {
        var rig = BillRig.New();
        var (po, lines) = await rig.PurchaseOrderAsync(Guid.NewGuid(), Guid.NewGuid());
        var uuid = await rig.InvoiceAsync(po, lines);
        await rig.Service.ApproveAsync(uuid, null, 7);

        await rig.Service.ReverseAsync(uuid, "Entered twice", 7);

        rig.PublisherLog.At(LogLevel.Warning).Should().BeEmpty();
        rig.PublisherLog.At(LogLevel.Information).Should().ContainSingle().Which.Message.Should().Contain("never handed to QuickBooks");
    }

    // The reversal flags the bill on the gateway's dashboard (it marks one that is, or may be, in QuickBooks as
    // needing the accountant, and closes one that never got there) — not only a log line.

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reversing_flags_the_bill_for_the_accountant_with_the_reason_whether_or_not_it_reached_QuickBooks(bool reached)
    {
        var rig = reached ? BillRig.New(knownBill: (SyncState.Synced, "QB-1042", "77")) : BillRig.New();
        var (po, lines) = await rig.PurchaseOrderAsync(Guid.NewGuid(), Guid.NewGuid());
        var uuid = await rig.InvoiceAsync(po, lines);
        await rig.Service.ApproveAsync(uuid, null, 7);
        var number = (await rig.LoadAsync(uuid)).InvoiceNumber;
        rig.Forget();

        (await rig.Service.ReverseAsync(uuid, "Entered against the wrong supplier", 7)).Should().BeTrue();

        var flag = rig.Gateway.Flags.Should().ContainSingle().Subject;
        flag.Kind.Should().Be(SyncKind.Bill);
        flag.ExternalId.Should().Be(uuid.ToString());
        flag.Reason.Should().Contain(number).And.Contain("reversed in SCM").And.Contain("Entered against the wrong supplier")
            .And.Contain("Void or delete its bill in QuickBooks by hand");
        rig.Gateway.Bills.Should().BeEmpty("flagging sends nothing");
    }

    [Fact]
    public async Task A_gateway_that_fails_to_flag_never_fails_the_reversal()
    {
        var rig = BillRig.New(knownBill: (SyncState.Synced, "QB-1042", "77"));
        var (po, lines) = await rig.PurchaseOrderAsync(Guid.NewGuid(), Guid.NewGuid());
        var uuid = await rig.InvoiceAsync(po, lines);
        await rig.Service.ApproveAsync(uuid, null, 7);
        rig.Forget();
        rig.Gateway.Throw = new HttpRequestException("gateway down");

        (await rig.Service.ReverseAsync(uuid, "Entered twice", 7)).Should().BeTrue();

        (await rig.LoadAsync(uuid)).MatchStatus.Should().Be(InvoiceMatchStatus.Reversed);
        rig.PublisherLog.At(LogLevel.Warning).Should().ContainSingle().Which.Exception.Should().BeOfType<HttpRequestException>();
    }

    /// <summary>
    /// The bill is built after the approval has committed, from the invoice as stored — so it carries the exchange
    /// rate the approval snapshotted (S-5), which the payload factory sends for a foreign-currency bill.
    /// </summary>
    [Fact]
    public async Task The_bill_sent_at_approval_carries_the_rate_the_approval_snapshotted()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var finance   = new FinanceDbContext(new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(dbName).Options, tenant);
        var demand    = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(dbName).Options, tenant);
        var warehouse = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseInMemoryDatabase(dbName).Options, tenant);

        var rates = new SupplierInvoices.FakeRates();
        rates.Rates[("USD", "PKR")] = 278.5m;
        var baseCurrency = new SupplierInvoices.FakeBaseCurrency();
        baseCurrency.Set("PKR");

        var gateway   = new RecordingQuickBooksGateway();
        var source    = new BillQuickBooksSource(finance, new DemandPurchaseOrderLineVariants(demand), gateway, new ListLogger<BillQuickBooksSource>());
        var publisher = new BillQuickBooksPublisher(finance, source, new ListLogger<BillQuickBooksPublisher>(), gateway);
        var repo      = new InvoiceRepository(finance, demand, warehouse, new SupplierLedgerService(finance), new FakeSupplierNameLookup(),
            new SupplierInvoices.FakeTaxCodes(), rates, baseCurrency, baseCurrency);
        var service   = new InvoiceService(repo, new Mock<IBackgroundJobClient>().Object, publisher);

        var uuid = await service.CreateAsync(new CreateInvoiceRequest
        {
            SupplierId = Guid.NewGuid(), SupplierInvoiceNo = "ACME-77", Currency = "USD", Subtotal = 1000m,
            InvoiceDate = new DateTime(2026, 9, 15), ReceivedDate = new DateTime(2026, 9, 16), DueDate = new DateTime(2026, 10, 15)
        }, createdBy: 1);
        gateway.Bills.Should().BeEmpty();

        (await service.ApproveAsync(uuid, null, 7)).Should().BeTrue();

        var bill = gateway.Bills.Should().ContainSingle().Subject;
        bill.ExchangeRate.Should().Be(278.5m);
        bill.ExchangeRateCurrencyCode.Should().Be("PKR");
    }

    [Fact]
    public async Task An_approved_carrier_bill_goes_as_freight()
    {
        var rig = BillRig.New();
        var carrier = Guid.NewGuid();
        var posted = await rig.Poster.PostAsync(new SupplierInvoicePosting(
            carrier, "TCS-7781", new DateTime(2026, 9, 10), new DateTime(2026, 10, 10), "PKR", 12500m, 0m,
            "CARRIER_INVOICE", Guid.NewGuid(), "Freight, per carrier invoice TCS-7781.",
            [new SupplierInvoiceLine("Karachi → Lahore, 3 pallets", 12000m), new SupplierInvoiceLine("Loading", 500m)]), userId: 1);

        await rig.Service.ApproveAsync(posted.InvoiceUuid, null, 7);

        var bill = rig.Gateway.Bills.Should().ContainSingle().Subject;
        bill.VendorExternalId.Should().Be(carrier.ToString());
        bill.DocNumber.Should().Be("TCS-7781");
        bill.Lines.Should().HaveCount(2).And.OnlyContain(l => l.Category == BillLineCategory.Freight && l.ItemExternalId == null);
        bill.Lines.Sum(l => l.Amount).Should().Be(12500m);
        rig.PoLines.Calls.Should().ContainSingle().Which.Should().BeEmpty("nothing on a carrier bill is looked up in Demand");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_throwing_gateway_does_not_fail_the_approval(bool faultedTask)
    {
        var rig = BillRig.New();
        var (po, lines) = await rig.PurchaseOrderAsync(Guid.NewGuid(), Guid.NewGuid());
        var uuid = await rig.InvoiceAsync(po, lines);
        rig.Gateway.Throw = new HttpRequestException("gateway unreachable");
        rig.Gateway.FaultTask = faultedTask;

        (await rig.Service.ApproveAsync(uuid, null, 7)).Should().BeTrue();

        (await rig.LoadAsync(uuid)).MatchStatus.Should().Be("Approved");
        (await rig.Finance.SupplierLedgerEntries.AsNoTracking().CountAsync(e => e.ReferenceId == uuid)).Should().Be(1, "the payable is booked");
        var warning = rig.SourceLog.At(LogLevel.Warning).Should().ContainSingle().Subject;
        warning.Message.Should().Contain(uuid.ToString());
        warning.Exception.Should().BeOfType<HttpRequestException>();
    }

    [Fact]
    public async Task A_failure_resolving_the_PO_lines_is_swallowed_and_logged()
    {
        var rig = BillRig.New();
        var approved = Seeded(rig.Org, "A", "Approved");
        await Receivables.Seed(rig.Finance, approved);

        var goneDemand = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new StaticTenantContext { OrganizationId = rig.Org });
        await goneDemand.DisposeAsync();
        var failing = new BillQuickBooksPublisher(rig.Finance, new BillQuickBooksSource(rig.Finance,
            new DemandPurchaseOrderLineVariants(goneDemand), rig.Gateway, rig.SourceLog), rig.PublisherLog);

        await failing.Invoking(p => p.PublishIfApprovedAsync(approved.UUID)).Should().NotThrowAsync();

        rig.Gateway.Calls.Should().BeEmpty();
        var warning = rig.PublisherLog.At(LogLevel.Warning).Should().ContainSingle().Subject;
        warning.Message.Should().Contain(approved.UUID.ToString());
        warning.Exception.Should().BeOfType<ObjectDisposedException>();
    }

    [Fact]
    public async Task An_invalid_answer_is_information_and_the_approval_stands()
    {
        var rig = BillRig.New();
        var (po, lines) = await rig.PurchaseOrderAsync(Guid.NewGuid(), Guid.NewGuid());
        var uuid = await rig.InvoiceAsync(po, lines);
        rig.Gateway.Result = GatewayResult.Invalid([new GatewayError("CurrencyCode", "NOT_HOME", "Only home currency.")]);

        (await rig.Service.ApproveAsync(uuid, null, 7)).Should().BeTrue();

        rig.SourceLog.At(LogLevel.Information).Should().ContainSingle().Which.Message.Should().Contain("CurrencyCode");
        rig.SourceLog.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
    }

    // ── Source ───────────────────────────────────────────────────────────────

    private static Invoice Seeded(Guid org, string number, string matchStatus, Guid? poLine = null,
        DateTime? created = null, DateTime? modified = null, DateTime? approved = null, bool deleted = false)
    {
        var invoice = new Invoice
        {
            UUID = Guid.NewGuid(), OrganizationId = org, TraceId = Guid.NewGuid(), InvoiceNumber = number, SupplierId = Guid.NewGuid(),
            SupplierName = "S", InvoiceDate = new DateTime(2026, 9, 1), ReceivedDate = new DateTime(2026, 9, 1), DueDate = new DateTime(2026, 10, 1),
            Subtotal = 100m, TotalAmount = 100m, MatchStatus = matchStatus, IsDelete = deleted,
            CreatedBy = 1, CreatedDate = created ?? new DateTime(2026, 1, 1), ModifiedDate = modified, ApprovedAt = approved
        };
        invoice.Lines.Add(new InvoiceLine { UUID = Guid.NewGuid(), LineNo = 1, PoLineUuid = poLine ?? Guid.NewGuid(), ItemDescription = "x", QtyInvoiced = 1m, UnitPrice = 100m, LineTotal = 100m });
        return invoice;
    }

    [Fact]
    public async Task The_source_serves_bills_only()
    {
        var rig = BillRig.New();

        rig.Source.Kinds.Should().Equal(SyncKind.Bill);
        await rig.Source.Invoking(s => s.PushAsync(SyncKind.SalesInvoice, ["x"])).Should().ThrowAsync<ArgumentOutOfRangeException>();
        await rig.Source.Invoking(s => s.PushAllAsync(SyncKind.Vendor, null)).Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task PushAsync_sends_approved_invoices_skips_the_rest_and_resolves_all_their_PO_lines_in_one_query()
    {
        var rig = BillRig.New();
        var (_, lines) = await rig.PurchaseOrderAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        lines.Should().HaveCount(4);
        var a = Seeded(rig.Org, "A", "Approved", lines[0].UUID);
        var b = Seeded(rig.Org, "B", "Approved", lines[1].UUID);
        b.Lines.Add(new InvoiceLine { UUID = Guid.NewGuid(), LineNo = 2, PoLineUuid = lines[2].UUID, ItemDescription = "y", QtyInvoiced = 1m, UnitPrice = 0m, LineTotal = 0m });
        var pending = Seeded(rig.Org, "P", "Pending", lines[3].UUID);
        var deleted = Seeded(rig.Org, "D", "Approved", deleted: true);
        await Receivables.Seed(rig.Finance, a, b, pending, deleted);

        await rig.Source.PushAsync(SyncKind.Bill, [a.UUID.ToString(), b.UUID.ToString(), pending.UUID.ToString(), deleted.UUID.ToString(), "junk", Guid.NewGuid().ToString()]);

        rig.Gateway.Bills.Select(x => x.DocNumber).Should().BeEquivalentTo(["A", "B"]);
        rig.Gateway.Bills.SelectMany(x => x.Lines).Should().OnlyContain(l => l.Category == BillLineCategory.Goods && l.ItemExternalId != null);
        rig.PoLines.Calls.Should().ContainSingle().Which.Should().BeEquivalentTo([lines[0].UUID, lines[1].UUID, lines[2].UUID]);
        rig.SourceLog.At(LogLevel.Information).Should().ContainSingle().Which.Message.Should().Contain(pending.UUID.ToString());
    }

    [Fact]
    public async Task PushAllAsync_takes_approved_invoices_created_modified_or_approved_since()
    {
        var since = new DateTime(2026, 9, 1);
        var old = since.AddDays(-20);
        var rig = BillRig.New();
        await Receivables.Seed(rig.Finance,
            Seeded(rig.Org, "OLD", "Approved", created: old),
            Seeded(rig.Org, "NEW", "Approved", created: since),
            Seeded(rig.Org, "EDITED", "Approved", created: old, modified: since.AddDays(1)),
            Seeded(rig.Org, "APPROVED", "Approved", created: old, approved: since.AddHours(2)),
            Seeded(rig.Org, "MATCHED-NEW", "Matched", created: since.AddDays(1)),
            Seeded(rig.Org, "DELETED-NEW", "Approved", created: since.AddDays(1), deleted: true));

        (await rig.Source.PushAllAsync(SyncKind.Bill, since)).Should().Be(3);
        rig.Gateway.Bills.Select(b => b.DocNumber).Should().BeEquivalentTo(["NEW", "EDITED", "APPROVED"]);

        rig.Gateway.Bills.Clear();
        (await rig.Source.PushAllAsync(SyncKind.Bill, null)).Should().Be(4);
    }

    [Fact]
    public async Task PushAllAsync_loads_sends_and_looks_up_PO_lines_one_batch_at_a_time()
    {
        var events = new List<string>();
        var rig = BillRig.New(new InvoiceMaterializationRecorder(events));
        var total = QuickBooksSupport.BatchSize * 2 + 3;
        await Receivables.Seed(rig.Finance, Enumerable.Range(0, total).Select(i => (object)Seeded(rig.Org, $"B-{i:D4}", "Approved")).ToArray());
        events.Clear();
        rig.Gateway.OnCall = call => events.Add($"send:{call}");

        (await rig.Source.PushAllAsync(SyncKind.Bill, null)).Should().Be(total);

        EventRuns.Of(events).Should().Equal(("load", 200), ("send", 200), ("load", 200), ("send", 200), ("load", 3), ("send", 3));
        rig.PoLines.Calls.Select(c => c.Count).Should().Equal(200, 200, 3);
    }

    [Fact]
    public async Task PushAllAsync_carries_on_past_a_failing_bill()
    {
        var rig = BillRig.New();
        var bad = Seeded(rig.Org, "BAD", "Approved");
        await Receivables.Seed(rig.Finance, Seeded(rig.Org, "OK", "Approved"), bad);
        rig.Gateway.FailWhen = id => id == bad.UUID.ToString();

        (await rig.Source.PushAllAsync(SyncKind.Bill, null)).Should().Be(1);

        rig.SourceLog.At(LogLevel.Warning).Should().ContainSingle().Which.Message.Should().Contain(bad.UUID.ToString());
    }

    [Fact]
    public async Task The_Demand_lookup_answers_for_lines_that_exist_including_ones_with_no_variant()
    {
        var rig = BillRig.New();
        var variant = Guid.NewGuid();
        var (_, lines) = await rig.PurchaseOrderAsync(Guid.NewGuid(), variant, null);
        var lookup = new DemandPurchaseOrderLineVariants(rig.Demand);

        var answer = await lookup.GetAsync([lines[0].UUID, lines[1].UUID, Guid.NewGuid()]);

        answer.Should().HaveCount(2);
        answer[lines[0].UUID].Should().Be(variant);
        answer[lines[1].UUID].Should().BeNull();
        (await lookup.GetAsync([])).Should().BeEmpty();
    }
}
