using FluentAssertions;
using Xunit;
using SMS.Modules.Reports.Models;
using static SMS.Modules.Reports.Tests.ReceivablesReportWorld;

namespace SMS.Modules.Reports.Tests;

/// <summary>
/// A29-P9-02 / P9-03 §15 R2 and R3 — what the in-memory provider cannot show: that the receivables
/// queries translate to SQL Server at all (the nested correlated sums and existence checks that age an
/// invoice as of a day, the opening-balance grouping, the day bounds on a real datetime2, the tenant
/// filter) and that SQL Server and the in-memory provider agree on every answer.
/// </summary>
public class ReceivablesReportSqlServerTests
{
    /// <summary>
    /// The same scattered books written into a real SQL Server database and into the in-memory one:
    /// thirty invoices for two customers in two currencies, twenty payments, part-payments, back-dated
    /// receipts and bounced cheques. Every id the two stores generate differs, so what is compared is
    /// numbers, dates and amounts, never ids.
    /// </summary>
    private static async Task<(ReceivablesReportWorld Sql, ReceivablesReportWorld Memory, SqlServerHarness Harness)> BothAsync()
    {
        var harness = await SqlServerHarness.CreateAsync(finance: true);

        var sql    = new ReceivablesReportWorld { ContextFactory = org => harness.NewFinanceContext(org) };
        var memory = new ReceivablesReportWorld();

        sql.SeedScatter();
        memory.SeedScatter();

        // Another organization's books, which neither report may touch.
        foreach (var world in new[] { sql, memory })
        {
            var theirs = world.Invoice(world.Acme, "SINV-THEIRS", D(8, 1), 9999m, org: OtherOrg);
            world.Payment(world.Acme, "CPAY-THEIRS", D(8, 5), 100m, [(theirs, 100m)], org: OtherOrg);
        }

        return (sql, memory, harness);
    }

    private static string Fingerprint(CustomerLedgerReport r) => string.Join('\n',
        new[]
        {
            $"records {r.TotalRecords} page {r.Page} size {r.PageSize} pages {r.TotalPages}",
            $"criteria {r.Criteria.PartnerId} {r.Criteria.CustomerName} {r.Criteria.DateFrom:O} {r.Criteria.DateTo:O}"
        }
        .Concat(r.Summaries.Select(s => $"summary {s.CurrencyCode} {s.OpeningBalance:F2} {s.TotalDebit:F2} {s.TotalCredit:F2} {s.ClosingBalance:F2} {s.EntryCount}"))
        .Concat(r.Entries.Select(e =>
            $"{e.SequenceNo} {e.EntryDate:O} {e.EntryType} {e.ReferenceNumber} {e.CurrencyCode} {e.DebitAmount:F2} {e.CreditAmount:F2} {e.Balance:F2}")));

    private static string Fingerprint(AgingReceivablesReport r) => string.Join('\n',
        new[]
        {
            $"records {r.TotalRecords} page {r.Page} size {r.PageSize} pages {r.TotalPages}",
            $"criteria {r.Criteria.AsOf:O} {r.Criteria.PartnerId} {r.Criteria.CustomerName}"
        }
        .Concat(r.Totals.Select(t => $"total {t.CurrencyCode} {t.InvoiceCount} {t.Days0To30:F2} {t.Days31To60:F2} {t.Days61To90:F2} {t.Over90:F2} {t.Total:F2}"))
        .Concat(r.Customers.Select(c => $"customer {c.PartnerId} {c.CustomerName} {c.CurrencyCode} {c.InvoiceCount} {c.Days0To30:F2} {c.Days31To60:F2} {c.Days61To90:F2} {c.Over90:F2} {c.Total:F2}"))
        .Concat(r.Invoices.Select(i =>
            $"{i.InvoiceNumber} {i.SaleOrderNumber} {i.PartnerId} {i.CustomerName} {i.CurrencyCode} {i.InvoiceDate:O} {i.DueDate:O} {i.DaysPastDue} {i.Bucket} " +
            $"{i.GrandTotal:F2} {i.AmountPaid:F2} {i.Outstanding:F2}")));

    // ── R2 ───────────────────────────────────────────────────────────────────

    private static readonly (DateTime? From, DateTime? To)[] Ranges =
    [
        (null, null),
        (D(8, 1), D(8, 31)),
        (D(9, 1), null),
        (null, D(9, 15)),
        (D(9, 5), D(9, 5)),
        (D(12, 1), null),
    ];

    [SqlServerFact]
    public async Task SQL_Server_and_the_in_memory_provider_give_the_same_customer_ledger_for_every_customer_and_range()
    {
        var (sql, memory, harness) = await BothAsync();
        await using var _ = harness;

        var compared = 0;
        foreach (var customer in new[] { sql.Acme, sql.Globex })
            foreach (var (from, to) in Ranges)
                foreach (var (page, size) in new[] { (1, 20), (2, 7) })
                {
                    var filter  = new CustomerLedgerReportFilter { PartnerId = customer, DateFrom = from, DateTo = to, Page = page, PageSize = size };
                    var onSql   = await sql.Service().GetCustomerLedgerAsync(filter);
                    var inMemory = await memory.Service().GetCustomerLedgerAsync(filter);

                    Fingerprint(onSql).Should().Be(Fingerprint(inMemory), $"customer {customer}, {from:d} to {to:d}, page {page}");
                    compared += onSql.TotalRecords;
                }

        compared.Should().BeGreaterThan(100, "guards the comparison: books that matched nothing would agree with anything");
    }

    [SqlServerFact]
    public async Task The_customer_ledger_export_reads_every_entry_in_the_same_order_on_SQL_Server()
    {
        var (sql, memory, harness) = await BothAsync();
        await using var _ = harness;

        var filter = new CustomerLedgerReportFilter { PartnerId = sql.Acme, Page = 3, PageSize = 2 };
        var onSql    = await sql.Service().GetCustomerLedgerForExportAsync(filter);
        var inMemory = await memory.Service().GetCustomerLedgerForExportAsync(filter);

        onSql.Entries.Count.Should().BeGreaterThan(10);
        Fingerprint(onSql).Should().Be(Fingerprint(inMemory));
    }

    [SqlServerFact]
    public async Task The_ledger_day_bounds_hold_to_the_last_tick_of_the_day_on_a_real_datetime2_and_the_opening_takes_everything_earlier()
    {
        await using var harness = await SqlServerHarness.CreateAsync(finance: true);
        var w = new ReceivablesReportWorld { ContextFactory = org => harness.NewFinanceContext(org) };

        w.Ledger(w.Acme, "INVOICE", D(9, 4).AddDays(1).AddTicks(-1), 10m, 0m, referenceNumber: "E-BEFORE");
        w.Ledger(w.Acme, "INVOICE", D(9, 5), 20m, 0m, referenceNumber: "E-FIRST");
        w.Ledger(w.Acme, "INVOICE", D(9, 10).AddDays(1).AddTicks(-1), 40m, 0m, referenceNumber: "E-LAST-TICK");
        w.Ledger(w.Acme, "INVOICE", D(9, 11), 80m, 0m, referenceNumber: "E-NEXT-DAY");

        var report = await w.Service().GetCustomerLedgerAsync(new CustomerLedgerReportFilter { PartnerId = w.Acme, DateFrom = D(9, 5), DateTo = D(9, 10) });

        report.Entries.Select(e => e.ReferenceNumber).Should().Equal("E-FIRST", "E-LAST-TICK");
        var s = report.Summaries.Single();
        (s.OpeningBalance, s.TotalDebit, s.ClosingBalance).Should().Be((10m, 60m, 70m));
    }

    // ── R3 ───────────────────────────────────────────────────────────────────

    public static IEnumerable<object[]> Days() =>
        [.. new[] { "2026-07-15", "2026-08-01", "2026-08-15", "2026-09-01", "2026-09-10", "2026-09-20", "2026-10-15", "2026-12-31" }
            .Select(d => new object[] { DateTime.Parse(d) })];

    [SqlServerFact]
    public async Task SQL_Server_and_the_in_memory_provider_give_the_same_aging_for_every_day_and_customer()
    {
        var (sql, memory, harness) = await BothAsync();
        await using var _ = harness;

        var compared = 0;
        foreach (var asOf in Days().Select(d => (DateTime)d[0]))
            foreach (var customer in new Guid?[] { null, sql.Acme, sql.Globex })
                foreach (var (page, size) in new[] { (1, 20), (2, 7) })
                {
                    var filter   = new AgingReceivablesFilter { AsOf = asOf, PartnerId = customer, Page = page, PageSize = size };
                    var onSql    = await sql.Service().GetAgingReceivablesAsync(filter);
                    var inMemory = await memory.Service().GetAgingReceivablesAsync(filter);

                    Fingerprint(onSql).Should().Be(Fingerprint(inMemory), $"as of {asOf:d}, customer {customer}, page {page}");
                    compared += onSql.TotalRecords;
                }

        compared.Should().BeGreaterThan(100, "guards the comparison: books with nothing owing would agree with anything");
    }

    [SqlServerFact]
    public async Task The_aging_export_reads_every_invoice_in_the_same_order_on_SQL_Server()
    {
        var (sql, memory, harness) = await BothAsync();
        await using var _ = harness;

        var filter   = new AgingReceivablesFilter { AsOf = D(9, 20), Page = 3, PageSize = 2 };
        var onSql    = await sql.Service().GetAgingReceivablesForExportAsync(filter);
        var inMemory = await memory.Service().GetAgingReceivablesForExportAsync(filter);

        onSql.Invoices.Count.Should().BeGreaterThan(5);
        Fingerprint(onSql).Should().Be(Fingerprint(inMemory));
    }

    [SqlServerFact]
    public async Task The_aging_day_holds_to_the_last_tick_of_the_day_on_a_real_datetime2()
    {
        await using var harness = await SqlServerHarness.CreateAsync(finance: true);
        var w = new ReceivablesReportWorld { ContextFactory = org => harness.NewFinanceContext(org) };

        var invoice = w.Invoice(w.Acme, "SINV-1", D(9, 10).AddDays(1).AddTicks(-1), 1000m);
        w.Payment(w.Acme, "CPAY-1", D(9, 11), 1000m, [(invoice, 1000m)]);

        (await w.Service().GetAgingReceivablesAsync(new AgingReceivablesFilter { AsOf = D(9, 9) })).Invoices.Should().BeEmpty("not yet issued");
        (await w.Service().GetAgingReceivablesAsync(new AgingReceivablesFilter { AsOf = D(9, 10) })).Invoices.Should().ContainSingle("issued on the last tick of the day");
        (await w.Service().GetAgingReceivablesAsync(new AgingReceivablesFilter { AsOf = D(9, 11) })).Invoices.Should().BeEmpty("paid that day");
    }

    // ── Tenancy ──────────────────────────────────────────────────────────────

    [SqlServerFact]
    public async Task Another_organizations_books_never_reach_either_report_on_SQL_Server()
    {
        var (sql, _, harness) = await BothAsync();
        await using var __ = harness;

        var aging = await sql.Service().GetAgingReceivablesForExportAsync(new AgingReceivablesFilter { AsOf = D(12, 31) });
        aging.Invoices.Should().NotBeEmpty().And.OnlyContain(i => i.InvoiceNumber != "SINV-THEIRS");

        var ledger = await sql.Service().GetCustomerLedgerForExportAsync(new CustomerLedgerReportFilter { PartnerId = sql.Acme });
        ledger.Entries.Select(e => e.ReferenceNumber).Should().NotContain(["SINV-THEIRS", "CPAY-THEIRS"]);

        var theirs = await sql.Service(OtherOrg).GetAgingReceivablesForExportAsync(new AgingReceivablesFilter { AsOf = D(12, 31) });
        theirs.Invoices.Select(i => i.InvoiceNumber).Should().Equal("SINV-THEIRS");
        theirs.Invoices.Single().Outstanding.Should().Be(9899m);
    }
}
