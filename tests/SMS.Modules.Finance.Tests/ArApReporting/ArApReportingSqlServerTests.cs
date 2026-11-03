using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.ArAp.Reporting;
using Xunit;

namespace SMS.Modules.Finance.Tests.ArApReporting;

/// <summary>
/// A48 RPT — every reporting query translated and run on real SQL Server (LocalDB): the in-memory provider accepts LINQ
/// that SQL Server refuses (aggregates over subqueries, navigation joins under IgnoreQueryFilters). One mixed book — part
/// payments, a bounce, credit applications, an A38 note, advances, a write-off-free AP side, two currencies.
/// </summary>
public class ArApReportingSqlServerTests
{
    private static readonly DateTime Today = ArApBooks.Today;
    private static DateOnly D(DateTime d) => DateOnly.FromDateTime(d);

    [FinanceSqlServerFact]
    public async Task All_reporting_queries_run_on_sql_server_and_agree_with_in_memory()
    {
        await using var sql = await FinanceSqlServerHarness.CreateAsync(retryOnFailure: true);   // as the API registers it (D-CORE-14)
        var org = Guid.NewGuid();
        var onSql = new ArApBooks(org, o => sql.NewContext(o));
        var inMem = new ArApBooks(org);

        foreach (var books in new[] { onSql, inMem }) await SeedAsync(books);

        // SQL Server hands decimals back at the column's scale (450.00); the in-memory store keeps what was written (450).
        static string Unscaled(string s) => System.Text.RegularExpressions.Regex.Replace(s, @"(\d)\.0+\b", "$1");
        var a = Unscaled(await RunAsync(onSql));
        var b = Unscaled(await RunAsync(inMem));
        a.Should().Be(b);
        a.Should().Contain("AR=").And.Contain("AP=");
    }

    private static async Task SeedAsync(ArApBooks books)
    {
        var acme   = books.Partner("Acme", "ar@acme.test");
        var vendor = books.Partner("Steel Co");

        var i1 = await books.SaleAsync(acme, Today.AddDays(-90), 1000m);
        var i2 = await books.SaleAsync(acme, Today.AddDays(-40), 500m, currency: "USD", rate: 280m);
        var i3 = await books.SaleAsync(acme, Today.AddDays(-10), 300m);
        await books.ReceiveAsync(acme, Today.AddDays(-20), 400m, [(i1, 400m)]);
        var bounced = await books.ReceiveAsync(acme, Today.AddDays(-8), 100m, [(i3, 100m)]);
        await books.BounceAsync(bounced, Today.AddDays(-6));
        await books.ReceiveAsync(acme, Today.AddDays(-2), 75m);
        var memo = await books.CreditNoteAsync(acme, Today.AddDays(-15), 200m);
        await books.ApplyCreditAsync(memo, i1, 150m, Today.AddDays(-5));
        await books.CreditNoteAsync(acme, Today.AddDays(-9), 30m, original: i3, directlyApplied: 30m);

        var b1 = await books.BillAsync(vendor, Today.AddDays(-60), 2000m, supplierInvoiceNo: "SC-1");
        await books.BillAsync(vendor, Today.AddDays(-20), 800m, currency: "USD", rate: 281m, supplierInvoiceNo: "SC-2");
        await books.PayAsync(vendor, Today.AddDays(-5), [(b1, 500m)]);
        await books.AdvanceAsync(vendor, Today.AddDays(-1), 60m);
    }

    private static async Task<string> RunAsync(ArApBooks books)
    {
        await using var db = books.Db();
        var svc   = books.Reports(db);
        var stmts = books.Statements(db);
        var acme   = (await db.SalesInvoices.AsNoTracking().FirstAsync()).PartnerId;
        var vendor = (await db.Invoices.AsNoTracking().FirstAsync()).SupplierId;

        var ar    = await svc.GetAgedReceivablesAsync(new AgingReportRequest { IncludeDetails = true });
        var arOld = await svc.GetAgedReceivablesAsync(new AgingReportRequest { AsOfDate = D(Today.AddDays(-7)), Basis = "DocumentDate" });
        var ap    = await svc.GetAgedPayablesAsync(new AgingReportRequest { Buckets = "15,30,45" });
        var det   = await svc.GetAgingDetailsAsync(true, new AgingDetailsRequest { BucketIndex = 3 });
        var dso   = await svc.GetDsoAsync(D(Today), 90);
        var dpo   = await svc.GetDpoAsync(D(Today));
        var bal   = await svc.GetCustomerBalanceAsync(acme);
        var vbal  = await svc.GetVendorBalanceAsync(vendor, D(Today.AddDays(-3)));
        var open  = await svc.GetCustomerOpenItemsAsync(acme, new OpenItemsFilter { PageSize = 2, Page = 2 });
        var tx    = await svc.GetCustomerTransactionsAsync(acme, new PartnerTransactionFilter { From = D(Today.AddDays(-30)), Page = 2, PageSize = 3 });
        var vtx   = await svc.GetVendorTransactionsAsync(vendor, new PartnerTransactionFilter());
        var ark   = await svc.GetArSummaryAsync(D(Today));
        var apk   = await svc.GetApSummaryAsync(D(Today));
        var act   = (await stmts.GenerateCustomerStatementAsync(acme, new StatementRequest { From = D(Today.AddDays(-60)) }, new(1, "T"))).Statement;
        var oi    = (await stmts.GenerateCustomerStatementAsync(acme, new StatementRequest { Type = "OpenItems" }, new(1, "T"))).Statement;
        var vs    = (await stmts.GenerateVendorStatementAsync(vendor, new StatementRequest { From = D(Today.AddDays(-90)) }, new(1, "T"))).Statement;
        var rec   = await stmts.ReconcileAsync(vendor, new VendorReconciliationRequest { VendorClaimedBalance = 1500m, CurrencyCode = "PKR",
            VendorItems = [new() { Reference = "SC-1", Amount = 1600m }] });
        var batch = await stmts.BatchCustomerStatementsAsync(new BatchStatementRequest { MinimumBalance = 1m }, new(1, "T"));
        var log   = await stmts.GetLogAsync(new StatementLogFilter { Role = "Customer" });

        return string.Join("\n",
            $"AR={ar.Totals.GrandTotal}|{ar.Totals.UnappliedCredits}|{string.Join(",", ar.Totals.BucketTotals)}|{string.Join(",", ar.Lines.SelectMany(l => l.Details!).Select(d => $"{d.DocumentNumber}:{d.OpenAmount}:{d.BucketIndex}"))}",
            $"AROLD={arOld.Totals.GrandTotal}|{arOld.Totals.UnappliedCredits}|{string.Join(",", arOld.Totals.BucketTotals)}",
            $"AP={ap.Totals.GrandTotal}|{ap.Totals.UnappliedCredits}|{string.Join(",", ap.Totals.BucketTotals)}|{ap.BucketLabels.Length}",
            $"DET={det.TotalRecords}|{string.Join(",", det.Data.Select(d => d.OpenAmountBase))}",
            $"DSO={dso.Balance}|{dso.PeriodAmount}|{dso.Days};DPO={dpo.Balance}|{dpo.PeriodAmount}|{dpo.Days}",
            $"BAL={bal.TotalOpen}|{bal.TotalCredits}|{bal.NetBalance}|{bal.OpenItemCount};VBAL={vbal.NetBalance}",
            $"OPEN={open.TotalRecords}|{string.Join(",", open.Data.Select(i => i.OpenAmount))}",
            $"TX={tx.TotalRecords}|{string.Join(",", tx.Data.Select(t => $"{t.CurrencyCode}:{t.RunningBalance}"))}",
            $"VTX={string.Join(",", vtx.Data.Select(t => $"{t.CurrencyCode}:{t.RunningBalance}"))}",
            $"ARK={ark.TotalOutstanding}|{ark.TotalOverdue}|{ark.CollectedInPeriod}|{ark.Dso};APK={apk.TotalOutstanding}|{apk.TotalDueThisMonth}|{apk.Dpo}",
            $"ACT={string.Join(";", act.Sections.Select(s => $"{s.CurrencyCode}:{s.OpeningBalance}:{s.ClosingBalance}:{s.Lines.Count}:{string.Join("/", s.Lines.Select(l => l.Status))}"))}|{act.ClosingBalanceBase}",
            $"OI={string.Join(";", oi.Sections.Select(s => $"{s.CurrencyCode}:{s.ClosingBalance}:{s.Lines.Count}"))}",
            $"VS={string.Join(";", vs.Sections.Select(s => $"{s.CurrencyCode}:{s.ClosingBalance}:{s.Lines.Count}"))}",
            $"REC={rec.SmsBalance}|{rec.Difference}|{string.Join(",", rec.Differences.Select(d => $"{d.Type}:{d.Difference}"))}",
            $"BATCH={batch.Considered}|{batch.Generated}|{batch.Skipped};LOG={log.TotalRecords}");
    }
}
