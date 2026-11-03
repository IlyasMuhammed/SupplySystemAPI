using System.Diagnostics;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.ArAp.Reporting;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using Xunit;
using Xunit.Abstractions;

namespace SMS.Modules.Finance.Tests.ArApReporting;

/// <summary>
/// A48 RPT task 159 (+ the QA 50k target) — aging on real SQL Server (LocalDB): 10 000 open receivables (half partly paid)
/// in under 2 s, 50 000 open items in under 5 s, 10 000 payables in under 2 s. Another organization's 5 000 rows sit in
/// the same tables so the organization predicate does real work. Timings are after a warm-up call and are written to the
/// test output.
/// </summary>
[Trait("Category", "Performance")]
public class ArApAgingPerformanceSqlServerTests
{
    private readonly ITestOutputHelper _out;
    public ArApAgingPerformanceSqlServerTests(ITestOutputHelper output) => _out = output;

    private static readonly DateTime Today = ArApBooks.Today;

    [FinanceSqlServerFact]
    public async Task Receivables_aging_10k_open_items_under_2_seconds()
    {
        await using var sql = await FinanceSqlServerHarness.CreateAsync();
        var org = Guid.NewGuid();
        await SeedReceivablesAsync(sql, org, invoices: 10_000, customers: 500, payEvery: 2);
        await SeedReceivablesAsync(sql, Guid.NewGuid(), invoices: 5_000, customers: 100, payEvery: 0);

        var (elapsed, report) = await TimeAsync(sql, org, s => s.GetAgedReceivablesAsync(new AgingReportRequest()));
        _out.WriteLine($"AR aging, 10 000 open invoices / 500 customers: {elapsed.TotalMilliseconds:N0} ms");

        report.Totals.TotalOpenItems.Should().Be(10_000);
        report.Totals.TotalPartners.Should().Be(500);
        report.Totals.GrandTotal.Should().Be(5_000 * 100m + 5_000 * 60m);
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [FinanceSqlServerFact]
    public async Task Receivables_aging_50k_open_items_under_5_seconds()
    {
        await using var sql = await FinanceSqlServerHarness.CreateAsync();
        var org = Guid.NewGuid();
        await SeedReceivablesAsync(sql, org, invoices: 50_000, customers: 2_000, payEvery: 0);

        var (elapsed, report) = await TimeAsync(sql, org, s => s.GetAgedReceivablesAsync(new AgingReportRequest { IncludeDetails = false }));
        _out.WriteLine($"AR aging, 50 000 open invoices / 2 000 customers: {elapsed.TotalMilliseconds:N0} ms");

        report.Totals.TotalOpenItems.Should().Be(50_000);
        report.Totals.GrandTotal.Should().Be(50_000 * 100m);
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    [FinanceSqlServerFact]
    public async Task Payables_aging_10k_open_items_under_2_seconds()
    {
        await using var sql = await FinanceSqlServerHarness.CreateAsync();
        var org = Guid.NewGuid();
        await SeedPayablesAsync(sql, org, invoices: 10_000, vendors: 300);

        var (elapsed, report) = await TimeAsync(sql, org, s => s.GetAgedPayablesAsync(new AgingReportRequest()));
        _out.WriteLine($"AP aging, 10 000 open bills / 300 vendors (half partly paid): {elapsed.TotalMilliseconds:N0} ms");

        report.Totals.TotalOpenItems.Should().Be(10_000);
        report.Totals.GrandTotal.Should().Be(5_000 * 100m + 5_000 * 70m);
        elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    private static async Task<(TimeSpan, AgingReport)> TimeAsync(
        FinanceSqlServerHarness sql, Guid org, Func<ArApReportingService, Task<AgingReport>> run)
    {
        var clock = new FixedClock(Today);
        await using (var warm = sql.NewContext(org)) await run(new ArApReportingService(warm, clock: clock));

        // Best of three timed calls: LocalDB is shared with the other test classes (and agents) running at the same time,
        // so a single call measures their load as much as the query.
        var best = TimeSpan.MaxValue;
        AgingReport? report = null;
        for (var i = 0; i < 3; i++)
        {
            await using var db = sql.NewContext(org);
            var svc = new ArApReportingService(db, clock: clock);
            var watch = Stopwatch.StartNew();
            report = await run(svc);
            watch.Stop();
            if (watch.Elapsed < best) best = watch.Elapsed;
        }
        return (best, report!);
    }

    /// <summary>Issued invoices of 100 with their INVOICE entries; every <paramref name="payEvery"/>th one 40 paid (allocation + PAYMENT entry).</summary>
    private static async Task SeedReceivablesAsync(FinanceSqlServerHarness sql, Guid org, int invoices, int customers, int payEvery)
    {
        var partners = Enumerable.Range(0, customers).Select(_ => Guid.NewGuid()).ToArray();
        var seq      = new int[customers];
        const int chunk = 2_500;

        for (var start = 0; start < invoices; start += chunk)
        {
            await using var db = sql.NewContext(org);
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            db.Database.SetCommandTimeout(180);
            var batch = new List<SalesInvoice>();
            for (var i = start; i < Math.Min(invoices, start + chunk); i++)
            {
                var p    = i % customers;
                var date = Today.AddDays(-(i % 200));
                var inv  = new SalesInvoice
                {
                    UUID = Guid.NewGuid(), OrganizationId = org, TraceId = Guid.NewGuid(), InvoiceNumber = $"SINV-{i:000000}",
                    SaleOrderNumber = "SO", PartnerId = partners[p], PartnerName = "C", InvoiceDate = date, DueDate = date.AddDays(30),
                    Subtotal = 100m, GrandTotal = 100m, BalanceDue = 100m, Status = SalesInvoiceStatuses.Issued, CurrencyCode = "PKR",
                    CreatedBy = 1, CreatedDate = date
                };
                batch.Add(inv);
                db.SalesInvoices.Add(inv);
                db.CustomerLedgerEntries.Add(new CustomerLedgerEntry
                {
                    UUID = Guid.NewGuid(), OrganizationId = org, PartnerId = partners[p], SequenceNo = ++seq[p], EntryDate = date,
                    EntryType = CustomerLedgerEntryTypes.Invoice, ReferenceType = "SalesInvoice", ReferenceId = inv.UUID,
                    ReferenceNumber = inv.InvoiceNumber, DebitAmount = 100m, CurrencyCode = "PKR", CreatedBy = 1, CreatedDate = date
                });
            }
            await db.SaveChangesAsync();

            if (payEvery <= 0) continue;
            foreach (var inv in batch.Where((_, k) => (start + k) % payEvery == 0))
            {
                var p   = Array.IndexOf(partners, inv.PartnerId);
                var pay = new CustomerPayment
                {
                    UUID = Guid.NewGuid(), OrganizationId = org, PartnerId = inv.PartnerId, PaymentNumber = $"CPAY-{inv.InvoiceNumber[5..]}",
                    PaymentDate = Today.AddDays(-1), Amount = 40m, PaymentMethod = "BANK_TRANSFER", CurrencyCode = "PKR",
                    Status = CustomerPaymentStatuses.Received, CreatedBy = 1, CreatedDate = Today
                };
                pay.Allocations.Add(new PaymentAllocation
                {
                    UUID = Guid.NewGuid(), OrganizationId = org, SalesInvoiceId = inv.Id, AllocatedAmount = 40m, AllocatedAt = Today, AllocatedBy = 1
                });
                db.CustomerPayments.Add(pay);
                db.CustomerLedgerEntries.Add(new CustomerLedgerEntry
                {
                    UUID = Guid.NewGuid(), OrganizationId = org, PartnerId = inv.PartnerId, SequenceNo = ++seq[p], EntryDate = Today.AddDays(-1),
                    EntryType = CustomerLedgerEntryTypes.Payment, ReferenceType = "CustomerPayment", ReferenceId = pay.UUID,
                    ReferenceNumber = pay.PaymentNumber, CreditAmount = 40m, CurrencyCode = "PKR", CreatedBy = 1, CreatedDate = Today
                });
            }
            db.ChangeTracker.DetectChanges();
            await db.SaveChangesAsync();
        }
    }

    /// <summary>Approved bills of 100; every other one 30 paid by a posted supplier payment line.</summary>
    private static async Task SeedPayablesAsync(FinanceSqlServerHarness sql, Guid org, int invoices, int vendors)
    {
        var partners = Enumerable.Range(0, vendors).Select(_ => Guid.NewGuid()).ToArray();
        const int chunk = 2_500;
        for (var start = 0; start < invoices; start += chunk)
        {
            await using var db = sql.NewContext(org);
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            db.Database.SetCommandTimeout(180);
            for (var i = start; i < Math.Min(invoices, start + chunk); i++)
            {
                var date = Today.AddDays(-(i % 200));
                var inv = new Invoice
                {
                    UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), OrganizationId = org, InvoiceNumber = $"INV-{i:000000}",
                    SupplierId = partners[i % vendors], SupplierName = "V", InvoiceDate = date, ReceivedDate = date, DueDate = date.AddDays(30),
                    Currency = "PKR", Subtotal = 100m, TotalAmount = 100m, MatchStatus = "Approved", PaymentStatus = "UNPAID",
                    ApprovedAt = date, CreatedBy = 1, CreatedDate = date, PaidAmount = i % 2 == 0 ? 30m : 0m
                };
                db.Invoices.Add(inv);
                if (i % 2 == 0)
                {
                    var pay = new SupplierPayment
                    {
                        UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), OrganizationId = org, PaymentNumber = $"SPAY-{i:000000}",
                        SupplierId = inv.SupplierId, PaymentDate = Today.AddDays(-1), PaymentMethod = "CASH", TotalAmount = 30m,
                        Status = "POSTED", CurrencyCode = "PKR", CreatedBy = 1, CreatedDate = Today
                    };
                    pay.Lines.Add(new SupplierPaymentLine
                    {
                        UUID = Guid.NewGuid(), OrganizationId = org, InvoiceUuid = inv.UUID, InvoiceNumber = inv.InvoiceNumber,
                        AllocatedAmount = 30m, OutstandingBeforeAllocation = 100m
                    });
                    db.SupplierPayments.Add(pay);
                }
            }
            db.ChangeTracker.DetectChanges();
            await db.SaveChangesAsync();
        }
    }
}
