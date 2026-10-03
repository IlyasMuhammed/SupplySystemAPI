using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Services;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Integration;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;
using SMS.Shared.Integration.QuickBooks;
using Xunit;

namespace SMS.Modules.Finance.Tests.QuickBooks;

/// <summary>
/// The real sales invoice service, payment service and overdue job over one in-memory database, with a
/// recording QuickBooks gateway behind the invoice service's publisher.
/// </summary>
file sealed class Rig
{
    private static readonly Guid Pkr = Guid.NewGuid();

    public required string                                     DbName;
    public required Guid                                       Org;
    public required TestClock                                  Clock;
    public required FinanceDbContext                           Db;
    public required DemandDbContext                            Demand;
    public required Mock<IDeliveryFulfillmentReader>           Reader;
    public required RecordingQuickBooksGateway                 Gateway;
    public required ListLogger<SalesInvoiceQuickBooksSource>   SourceLog;
    public required ListLogger<SalesInvoiceQuickBooksPublisher> PublisherLog;
    public required SalesInvoiceService                        Invoices;
    public required CustomerPaymentService                     Payments;

    public static Rig New()
    {
        var dbName = Guid.NewGuid().ToString();
        var org    = Guid.NewGuid();
        var tenant = new StaticTenantContext { OrganizationId = org };
        var clock  = new TestClock();

        var db     = new FinanceDbContext(new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(dbName).Options, tenant);
        var demand = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(dbName).Options, tenant);

        var reader = new Mock<IDeliveryFulfillmentReader>();
        var names  = Receivables.Names();
        var lookups = new Mock<ILookupsService>();
        lookups.Setup(l => l.GetCurrencies()).Returns([new CurrencyModel { Id = Pkr, Name = "Pakistani Rupee", Code = "PKR" }]);

        var gateway      = new RecordingQuickBooksGateway();
        var sourceLog    = new ListLogger<SalesInvoiceQuickBooksSource>();
        var publisherLog = new ListLogger<SalesInvoiceQuickBooksPublisher>();
        var publisher    = new SalesInvoiceQuickBooksPublisher(new SalesInvoiceQuickBooksSource(db, gateway, sourceLog), publisherLog);
        var ledger       = new CustomerLedgerService(db, clock);

        return new Rig
        {
            DbName = dbName, Org = org, Clock = clock, Db = db, Demand = demand, Reader = reader,
            Gateway = gateway, SourceLog = sourceLog, PublisherLog = publisherLog,
            Invoices = new SalesInvoiceService(db, demand, reader.Object, ledger, new NoProductLedger(), names.Object, lookups.Object,
                new Mock<IBackgroundJobClient>().Object, NullLogger<SalesInvoiceService>.Instance, clock, publisher),
            Payments = new CustomerPaymentService(db, ledger, names.Object, lookups.Object, clock)
        };
    }

    /// <summary>A delivered sale order of the given lines, and its delivery. Returns the delivery.</summary>
    public async Task<(Guid Delivery, Guid Customer, SaleOrder Order)> DeliveredAsync(params (decimal Qty, decimal Price, decimal Disc, decimal Tax)[] lines)
    {
        var customer = Guid.NewGuid();
        var order = new SaleOrder
        {
            SoNumber = "SO-2026-00042", TraceId = Guid.NewGuid(), PartnerId = customer, OrderDate = new DateTime(2026, 9, 1),
            CurrencyId = Pkr, Status = "FULFILLED", DeliveryMode = "SHIP", CreatedBy = 1
        };
        foreach (var l in lines)
            order.Lines.Add(new SaleOrderLine
            {
                VariantUuid = Guid.NewGuid(), Quantity = l.Qty, UnitPrice = l.Price, DiscountPercent = l.Disc, TaxPercent = l.Tax,
                LineTotal = SalesInvoiceTotals.LineTotal(l.Qty, l.Price, l.Disc, l.Tax),
                FulfilledQty = l.Qty, FulfillmentMode = "IN_STOCK", Status = "FULFILLED"
            });
        Demand.SaleOrders.Add(order);
        await Demand.SaveChangesAsync();
        Demand.ChangeTracker.Clear();

        var soLines = order.Lines.OrderBy(l => l.Id).ToList();
        var delivery = Guid.NewGuid();
        Reader.Setup(r => r.GetAsync(delivery, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new DeliveryForInvoicing(delivery, "DLV-2026-00007", "DELIVERED", order.UUID,
                  soLines.Select((l, i) => new DeliveredLineForInvoicing(Guid.NewGuid(), i + 1, l.UUID, l.VariantUuid, $"Item {i + 1}", l.Quantity)).ToList()));
        return (delivery, customer, order);
    }

    public Task<SalesInvoice> LoadAsync(Guid uuid) =>
        Db.SalesInvoices.AsNoTracking().Include(i => i.Lines).SingleAsync(i => i.UUID == uuid);
}

public class SalesInvoiceQuickBooksTriggerTests
{
    private const int User = 42;

    [Fact]
    public async Task Raising_editing_and_deleting_a_draft_sends_nothing()
    {
        var rig = Rig.New();
        var (delivery, _, _) = await rig.DeliveredAsync((10m, 25m, 0m, 0m));

        var created = await rig.Invoices.CreateFromFulfillmentAsync(delivery, User);
        await rig.Invoices.UpdateAsync(created.InvoiceUuid, new UpdateSalesInvoiceRequest { DueDate = new DateTime(2026, 11, 30), Notes = "Edited" }, User);
        await rig.Invoices.DeleteAsync(created.InvoiceUuid, User);

        rig.Gateway.Calls.Should().BeEmpty("a draft is not yet anything the customer owes");
    }

    [Fact]
    public async Task Issuing_sends_the_invoice_once_as_issued_with_totals_that_pass_the_gateways_check()
    {
        var rig = Rig.New();
        var (delivery, customer, order) = await rig.DeliveredAsync(
            (100m, 40m, 10m, 5m), (3.5m, 12.345m, 2.5m, 16m), (7m, 0.333m, 0m, 18m), (1m, 1.005m, 0m, 0m));
        var created = await rig.Invoices.CreateFromFulfillmentAsync(delivery, User);

        var issued = await rig.Invoices.IssueAsync(created.InvoiceUuid, User);

        issued.Status.Should().Be("ISSUED");
        var payload = rig.Gateway.SalesInvoices.Should().ContainSingle().Subject;
        rig.Gateway.Calls.Should().ContainSingle();

        var invoice = await rig.LoadAsync(created.InvoiceUuid);
        payload.ExternalId.Should().Be(invoice.UUID.ToString());
        payload.DocNumber.Should().Be(invoice.InvoiceNumber);
        payload.CustomerExternalId.Should().Be(customer.ToString());
        payload.Status.Should().Be(SalesInvoicePayloadStatus.Issued);
        payload.CurrencyCode.Should().Be("PKR");
        payload.PrivateNote.Should().Be($"SO {order.SoNumber} · DLV DLV-2026-00007");
        payload.ExpectedTotal.Should().Be(invoice.GrandTotal);
        payload.ExpectedTaxAmount.Should().Be(invoice.TaxAmount);
        payload.Lines.Select(l => l.ItemExternalId).Should().Equal(invoice.Lines.OrderBy(l => l.LineNo).Select(l => l.VariantUuid.ToString()));
        payload.HeaderDiscountAmount.Should().Be(0m, "SCM has no header discount");
        SalesInvoicePayloadFactoryTests.GatewayDifference(payload).Should().Be(0m, "built by the real service, checked the gateway's way");
    }

    [Fact]
    public async Task Payments_and_the_overdue_sweep_move_the_status_but_send_nothing_more()
    {
        var rig = Rig.New();
        var (delivery, customer, _) = await rig.DeliveredAsync((10m, 100m, 0m, 0m));
        var created = await rig.Invoices.CreateFromFulfillmentAsync(delivery, User);
        await rig.Invoices.IssueAsync(created.InvoiceUuid, User);
        rig.Gateway.Calls.Should().ContainSingle();

        await rig.Payments.RecordPaymentAsync(customer, 300m, "BANK_TRANSFER", new CustomerPaymentDetails("PKR"), User);
        (await rig.LoadAsync(created.InvoiceUuid)).Status.Should().Be("PARTIALLY_PAID");

        rig.Clock.Value = rig.Clock.Value.AddDays(45);
        await using (var auditor = Receivables.Auditor(rig.DbName))
            await new InvoiceOverdueJob(auditor, NullLogger<InvoiceOverdueJob>.Instance, rig.Clock).SweepAsync();
        (await rig.LoadAsync(created.InvoiceUuid)).Status.Should().Be("OVERDUE");

        await rig.Payments.RecordPaymentAsync(customer, 700m, "BANK_TRANSFER", new CustomerPaymentDetails("PKR"), User);
        (await rig.LoadAsync(created.InvoiceUuid)).Status.Should().Be("PAID");

        rig.Gateway.Calls.Should().ContainSingle("payments are not synced (plan D-8): only the issue was sent");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_throwing_gateway_does_not_fail_the_issue_the_invoice_is_still_issued_and_booked(bool faultedTask)
    {
        var rig = Rig.New();
        var (delivery, customer, _) = await rig.DeliveredAsync((10m, 25m, 0m, 0m));
        var created = await rig.Invoices.CreateFromFulfillmentAsync(delivery, User);
        rig.Gateway.Throw = new InvalidOperationException("QuickBooks gateway exploded");
        rig.Gateway.FaultTask = faultedTask;

        var issued = await rig.Invoices.IssueAsync(created.InvoiceUuid, User);

        issued.Status.Should().Be("ISSUED");
        issued.PartnerBalance.Should().Be(250m);
        (await rig.LoadAsync(created.InvoiceUuid)).Status.Should().Be("ISSUED");
        (await rig.Db.CustomerLedgerEntries.AsNoTracking().CountAsync(e => e.PartnerId == customer)).Should().Be(1, "the receivable is booked");
        var warning = rig.SourceLog.At(LogLevel.Warning).Should().ContainSingle().Subject;
        warning.Message.Should().Contain(created.InvoiceUuid.ToString());
        warning.Exception.Should().BeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task An_invalid_answer_is_logged_at_information_and_the_issue_stands()
    {
        var rig = Rig.New();
        var (delivery, _, _) = await rig.DeliveredAsync((10m, 25m, 0m, 0m));
        var created = await rig.Invoices.CreateFromFulfillmentAsync(delivery, User);
        rig.Gateway.Result = GatewayResult.Invalid([new GatewayError("Lines[0].TaxPercent", "TAX_UNMAPPED", "0% has no QuickBooks tax code.")]);

        (await rig.Invoices.IssueAsync(created.InvoiceUuid, User)).Status.Should().Be("ISSUED");

        rig.SourceLog.At(LogLevel.Information).Should().ContainSingle().Which.Message.Should().Contain("TAX_UNMAPPED").And.Contain("TaxPercent");
        rig.SourceLog.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
    }
}

/// <summary>The sales invoice source, as the gateway calls it.</summary>
public class SalesInvoiceQuickBooksSourceTests
{
    private static (FinanceDbContext Db, RecordingQuickBooksGateway Gateway, ListLogger<SalesInvoiceQuickBooksSource> Log, SalesInvoiceQuickBooksSource Source, Guid Org)
        NewSource(Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor? interceptor = null)
    {
        var org = Guid.NewGuid();
        var db = Receivables.Db(org, Guid.NewGuid().ToString(), interceptor);
        var gateway = new RecordingQuickBooksGateway();
        var log = new ListLogger<SalesInvoiceQuickBooksSource>();
        return (db, gateway, log, new SalesInvoiceQuickBooksSource(db, gateway, log), org);
    }

    private static SalesInvoice Invoice(Guid org, string number, string status, DateTime? created = null, DateTime? modified = null, bool deleted = false)
    {
        var i = Receivables.Invoice(org, Guid.NewGuid(), number, new DateTime(2026, 9, 1), 100m, status, deleted: deleted);
        i.CreatedDate = created ?? new DateTime(2026, 1, 1);
        i.ModifiedDate = modified;
        i.Lines.Add(new SalesInvoiceLine { UUID = Guid.NewGuid(), LineNo = 1, VariantUuid = Guid.NewGuid(), Description = "x", Quantity = 1m, UnitPrice = 100m, LineTotal = 100m });
        return i;
    }

    [Fact]
    public async Task It_serves_sales_invoices_only()
    {
        var (_, _, _, source, _) = NewSource();

        source.Kinds.Should().Equal(SyncKind.SalesInvoice);
        await source.Invoking(s => s.PushAsync(SyncKind.Bill, ["x"])).Should().ThrowAsync<ArgumentOutOfRangeException>();
        await source.Invoking(s => s.PushAllAsync(SyncKind.Item, null)).Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task PushAsync_upserts_what_is_issued_voids_what_is_cancelled_and_skips_drafts_deleted_and_unknown()
    {
        var (db, gateway, _, source, org) = NewSource();
        var issued    = Invoice(org, "I-1", "ISSUED");
        var paid      = Invoice(org, "I-2", "PAID");
        var cancelled = Invoice(org, "I-3", "CANCELLED");
        var credit    = Invoice(org, "I-4", "CREDIT_NOTE");
        var draft     = Invoice(org, "I-5", "DRAFT");
        var deleted   = Invoice(org, "I-6", "DRAFT", deleted: true);
        await Receivables.Seed(db, issued, paid, cancelled, credit, draft, deleted);

        await source.PushAsync(SyncKind.SalesInvoice,
            [issued.UUID.ToString(), paid.UUID.ToString(), cancelled.UUID.ToString(), credit.UUID.ToString(),
             draft.UUID.ToString(), deleted.UUID.ToString(), Guid.NewGuid().ToString(), "garbage"]);

        gateway.SalesInvoices.Select(p => p.DocNumber).Should().BeEquivalentTo(["I-1", "I-2", "I-4"]);
        gateway.SalesInvoices.Single(p => p.DocNumber == "I-4").Status.Should().Be(SalesInvoicePayloadStatus.CreditNote);
        gateway.Voids.Should().Equal(cancelled.UUID.ToString());
    }

    [Fact]
    public async Task PushAllAsync_takes_every_invoice_past_draft_changed_since()
    {
        var since = new DateTime(2026, 9, 1);
        var (db, gateway, _, source, org) = NewSource();
        await Receivables.Seed(db,
            Invoice(org, "OLD", "ISSUED", created: since.AddDays(-10)),
            Invoice(org, "NEW", "ISSUED", created: since),
            Invoice(org, "PAID-SINCE", "PAID", created: since.AddDays(-10), modified: since.AddDays(1)),
            Invoice(org, "CANCELLED-SINCE", "CANCELLED", created: since.AddDays(-10), modified: since.AddDays(2)),
            Invoice(org, "DRAFT-NEW", "DRAFT", created: since.AddDays(1)),
            Invoice(org, "DELETED-NEW", "ISSUED", created: since.AddDays(1), deleted: true));

        var sent = await source.PushAllAsync(SyncKind.SalesInvoice, since);

        sent.Should().Be(3);
        gateway.SalesInvoices.Select(p => p.DocNumber).Should().BeEquivalentTo(["NEW", "PAID-SINCE"]);
        gateway.Voids.Should().ContainSingle();

        gateway.Calls.Clear();
        (await source.PushAllAsync(SyncKind.SalesInvoice, null)).Should().Be(4, "with no date, every invoice past draft");
    }

    [Fact]
    public async Task PushAllAsync_loads_and_sends_in_batches()
    {
        var events = new List<string>();
        var (db, gateway, _, source, org) = NewSource(new InvoiceMaterializationRecorder(events));
        var total = QuickBooksSupport.BatchSize + 7;
        await Receivables.Seed(db, Enumerable.Range(0, total).Select(i => (object)Invoice(org, $"I-{i:D4}", "ISSUED")).ToArray());
        events.Clear();
        gateway.OnCall = call => events.Add($"send:{call}");

        (await source.PushAllAsync(SyncKind.SalesInvoice, null)).Should().Be(total);

        EventRuns.Of(events).Should().Equal(("load", 200), ("send", 200), ("load", 7), ("send", 7));
    }

    [Fact]
    public async Task PushAllAsync_carries_on_past_a_failing_invoice()
    {
        var (db, gateway, log, source, org) = NewSource();
        var bad = Invoice(org, "BAD", "ISSUED");
        await Receivables.Seed(db, Invoice(org, "OK-1", "ISSUED"), bad, Invoice(org, "OK-2", "ISSUED"));
        gateway.FailWhen = id => id == bad.UUID.ToString();

        (await source.PushAllAsync(SyncKind.SalesInvoice, null)).Should().Be(2);

        gateway.SalesInvoices.Should().HaveCount(3);
        log.At(LogLevel.Warning).Should().ContainSingle().Which.Message.Should().Contain(bad.UUID.ToString());
    }
}

internal sealed class InvoiceMaterializationRecorder : Microsoft.EntityFrameworkCore.Diagnostics.IMaterializationInterceptor
{
    private readonly List<string> _events;
    public InvoiceMaterializationRecorder(List<string> events) => _events = events;

    public object InitializedInstance(Microsoft.EntityFrameworkCore.Diagnostics.MaterializationInterceptionData materializationData, object entity)
    {
        switch (entity)
        {
            case SalesInvoice s: _events.Add($"load:{s.UUID}"); break;
            case Invoice i:      _events.Add($"load:{i.UUID}"); break;
        }
        return entity;
    }
}
