using FluentAssertions;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Services.Exports;
using SMS.Shared.Common;
using UglyToad.PdfPig;
using Xunit;

namespace SMS.Modules.Reports.Tests;

/// <summary>
/// QuestPDF corrupts documents rendered at the same moment: the text of bold runs comes out as NUL
/// characters. These tests hold the gate that keeps the reports' renders apart, and prove the reports'
/// documents come out intact when many are asked for at once.
/// </summary>
public class PdfRenderGateTests
{
    // ── The gate ─────────────────────────────────────────────────────────────

    [Fact]
    public void It_returns_what_the_render_returns()
    {
        PdfRenderGate.Run(() => 42).Should().Be(42);
    }

    [Fact]
    public void A_null_render_is_a_programming_error()
    {
        var act = () => PdfRenderGate.Run<int>(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void No_two_renders_ever_run_at_once_however_many_are_asked_for_together()
    {
        var running = 0;
        var most    = 0;

        Parallel.For(0, 300, new ParallelOptions { MaxDegreeOfParallelism = 16 }, _ =>
            PdfRenderGate.Run(() =>
            {
                var now = Interlocked.Increment(ref running);
                InterlockedMax(ref most, now);
                Thread.SpinWait(2_000);
                Interlocked.Decrement(ref running);
                return 0;
            }));

        most.Should().Be(1);
    }

    [Fact]
    public void A_render_that_fails_reaches_its_caller_and_does_not_leave_the_gate_shut()
    {
        var act = () => PdfRenderGate.Run<int>(() => throw new InvalidOperationException("layout failed"));

        act.Should().Throw<InvalidOperationException>().WithMessage("layout failed");
        PdfRenderGate.Run(() => 7).Should().Be(7);
    }

    [Fact]
    public async Task A_render_waiting_for_the_gate_gets_it_once_the_holder_is_done()
    {
        using var holding = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var first  = Task.Run(() => PdfRenderGate.Run(() => { holding.Set(); release.Wait(); return "first"; }));
        holding.Wait();
        var second = Task.Run(() => PdfRenderGate.Run(() => "second"));

        (await Task.WhenAny(second, Task.Delay(200))).Should().NotBeSameAs(second, "the gate is held");
        release.Set();

        (await first, await second).Should().Be(("first", "second"));
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }

    // ── The reports' documents ───────────────────────────────────────────────

    private static string TextOf(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return string.Join('\n', doc.GetPages().Select(p => string.Join(' ', p.GetWords().Select(w => w.Text))));
    }

    private static readonly Func<byte[]>[] Documents =
    [
        () => SalesOrderRegisterPdfExporter.Export(RegisterWorld.Report(
        [
            RegisterWorld.Item("SO-2026-00003", "Globex Corp", "PARTIALLY_FULFILLED", "SELF_PICKUP", "USD", 1000m, 50m, 152m, 1102m, 3),
            RegisterWorld.Item("SO-2026-00002", "Acme Ltd", "CONFIRMED", "SHIP", "PKR", 2500000.5m, 0m, 0m, 2500000.5m, 1),
        ])),
        () => CustomerLedgerPdfExporter.Export(new CustomerLedgerReport
        {
            CompanyName = "Northwind Trading", GeneratedAt = new DateTime(2026, 9, 20, 10, 30, 0), Criteria = new CustomerLedgerReportCriteria { CustomerName = "Acme Ltd" },
            Summaries = [new CustomerLedgerReportSummary { CurrencyCode = "PKR", OpeningBalance = 250m, TotalDebit = 1500m, TotalCredit = 300m, ClosingBalance = 1450m, EntryCount = 1 }],
            Entries = [new CustomerLedgerReportEntry { SequenceNo = 1, EntryDate = new DateTime(2026, 9, 1), EntryType = "INVOICE", ReferenceNumber = "SINV-1", CurrencyCode = "PKR", DebitAmount = 1500m, Balance = 1750m }],
            TotalRecords = 1, Page = 1, PageSize = 1, TotalPages = 1
        }),
        () => AgingReceivablesPdfExporter.Export(new AgingReceivablesReport
        {
            CompanyName = "Northwind Trading", GeneratedAt = new DateTime(2026, 9, 20, 10, 30, 0), Criteria = new AgingReceivablesCriteria { AsOf = new DateTime(2026, 9, 20) },
            Totals = [new AgingReceivablesTotal { CurrencyCode = "PKR", InvoiceCount = 1, Days31To60 = 200m, Total = 200m }],
            Customers = [new AgingReceivablesCustomer { CustomerName = "Acme Ltd", CurrencyCode = "PKR", InvoiceCount = 1, Days31To60 = 200m, Total = 200m }],
            Invoices = [new AgingReceivablesInvoice { InvoiceNumber = "SINV-1", CustomerName = "Acme Ltd", CurrencyCode = "PKR", DaysPastDue = 40, Bucket = "31-60", GrandTotal = 250m, AmountPaid = 50m, Outstanding = 200m }],
            TotalRecords = 1, Page = 1, PageSize = 1, TotalPages = 1
        }),
        () => SalesByProductPdfExporter.Export(new SalesByProductReport
        {
            CompanyName = "Northwind Trading", GeneratedAt = new DateTime(2026, 9, 20, 10, 30, 0), Criteria = new SalesByProductCriteria(),
            Totals = [new SalesByProductTotal { CurrencyCode = "PKR", ProductCount = 1, Revenue = 360m }],
            Items = [new SalesByProductItem { ProductUuid = Guid.NewGuid(), ProductName = "Laptop", CurrencyCode = "PKR", QuantitySold = 10m, Revenue = 360m, AverageUnitPrice = 36m }],
            TotalRecords = 1, Page = 1, PageSize = 1, TotalPages = 1
        }),
        () => SalesByCustomerPdfExporter.Export(new SalesByCustomerReport
        {
            CompanyName = "Northwind Trading", GeneratedAt = new DateTime(2026, 9, 20, 10, 30, 0), Criteria = new SalesByCustomerCriteria(),
            Totals = [new SalesByCustomerTotal { CurrencyCode = "PKR", CustomerCount = 1, OrderCount = 2, InvoiceCount = 3, Revenue = 1400m, AverageOrderValue = 700m }],
            Items = [new SalesByCustomerItem { PartnerId = Guid.NewGuid(), CustomerName = "Acme Ltd", CurrencyCode = "PKR", OrderCount = 2, InvoiceCount = 3, Revenue = 1400m, AverageOrderValue = 700m }],
            TotalRecords = 1, Page = 1, PageSize = 1, TotalPages = 1
        }),
        () => FulfilmentStatusPdfExporter.Export(new FulfilmentStatusReport
        {
            CompanyName = "Northwind Trading", GeneratedAt = new DateTime(2026, 9, 20, 10, 30, 0), Criteria = new FulfilmentStatusCriteria(),
            ByStatus = [new FulfilmentStatusCount { Status = "PICKING", Count = 1 }],
            ByWarehouse = [new FulfilmentWarehouseCount { WarehouseName = "Lahore Main", Count = 1 }],
            ByDeliveryMode = [new FulfilmentModeCount { DeliveryMode = "SHIP", Count = 1 }],
            Items = [new FulfilmentStatusItem { DeliveryNumber = "DLV-1", SaleOrderNumber = "SO-1", CustomerName = "Acme Ltd", Status = "PICKING", DeliveryMode = "SHIP", WarehouseName = "Lahore Main", DaysOpen = 3, LineCount = 1, QuantityOrdered = 10m }],
            TotalRecords = 1, Page = 1, PageSize = 1, TotalPages = 1
        }),
    ];

    [Fact]
    public void Every_report_document_asked_for_at_the_same_moment_comes_out_intact()
    {
        var references = Documents.Select(d => TextOf(d())).ToArray();
        references.Should().OnlyContain(t => !t.Contains('\0'), "the reference renders are made one at a time");

        const int count = 720;
        var made = new (int Kind, byte[] Pdf)[count];
        Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i => made[i] = (i % Documents.Length, Documents[i % Documents.Length]()));

        var corrupted = made.Count(m => TextOf(m.Pdf) != references[m.Kind]);

        corrupted.Should().Be(0, "of the documents rendered at once, none may lose its text");
    }
}
