using FluentAssertions;
using Moq;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Services;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// A29-P9-02 / P9-03 §15 R2 and R3 against the books the <i>real</i> invoice and payment services write.
/// The reports' own tests build their books by hand, which shows what the reports decide; these show that
/// what they decide is what the services did. A month is lived through day by day — invoices issued, a
/// cheque paid and bounced, a manual allocation, a receipt keyed after the day it came in — and at the end
/// of every day the balances the services held are written down. Afterwards each report is asked about
/// each of those days, and must say what the services said then.
/// </summary>
public class ReceivablesReportReconciliationTests
{
    private const int LastDay = 25;

    private static DateTime At(int day) => new(2026, 9, day, 10, 30, 0, DateTimeKind.Utc);

    /// <summary>The days a receipt keyed on the 18th but dated the 14th changes the story of: the services said one thing then, the books now say another.</summary>
    private static readonly HashSet<int> BackDatedWindow = [14, 15, 16, 17];

    private sealed record EndOfDay(
        int Day, Dictionary<string, decimal> Owing, decimal AcmeLedger, decimal AcmeOnAccount, decimal GlobexLedger, decimal GlobexOnAccount);

    private sealed record Month(
        ReceivablesWorld World, ReceivablesDesk Desk, Guid Acme, Guid Globex, IReadOnlyList<EndOfDay> Days,
        string A1, string A2, string A3, string G1);

    private static async Task<Month> LiveAsync()
    {
        var world  = new ReceivablesWorld();
        var desk   = world.For(Guid.NewGuid());
        var acme   = desk.NewCustomer("Acme Ltd");
        var globex = desk.NewCustomer("Globex Corp");
        var days   = new List<EndOfDay>();

        ReceivablesDesk.Invoiced a1 = null!, a2 = null!, a3 = null!, g1 = null!;
        Guid cheque = Guid.Empty;

        for (var day = 1; day <= LastDay; day++)
        {
            world.Clock.Value = At(day);

            switch (day)
            {
                case 1:
                    a1 = await desk.InvoiceAsync(acme, 1000m);
                    a2 = await desk.InvoiceAsync(acme, 500m);
                    g1 = await desk.InvoiceAsync(globex, 700m);
                    break;
                case 5:
                    a3 = await desk.InvoiceAsync(acme, 800m);
                    break;
                case 10:
                    // A cheque for 1200, applied oldest first: all of A1 and 200 of A2.
                    cheque = (await desk.PayAsync(acme, 1200m, "CHEQUE")).PaymentUuid;
                    break;
                case 12:
                    await desk.PayAsync(globex, 300m);
                    break;
                case 15:
                    await desk.BounceAsync(cheque);
                    break;
                case 16:
                    // 2000 applied as told: A1 and A3 in full, 200 left on account.
                    await desk.PayAsync(acme, 2000m, allocations: [new(a1.Uuid, 1000m), new(a3.Uuid, 800m)]);
                    break;
                case 18:
                    // Money that came in on the 14th, keyed on the 18th.
                    await using (var scope = desk.Scope())
                        await scope.Payments.RecordPaymentAsync(acme, 100m, "BANK_TRANSFER",
                            new CustomerPaymentDetails("PKR", PaymentDate: new DateTime(2026, 9, 14)), ReceivablesDesk.User);
                    break;
                case 20:
                    await desk.InvoiceAsync(acme, 300m);
                    break;
                case 22:
                    await desk.PayAsync(globex, 400m);
                    break;
            }

            days.Add(await EndOfDayAsync(desk, acme, globex, day));
        }

        return new Month(world, desk, acme, globex, days, a1.Number, a2.Number, a3.Number, g1.Number);
    }

    private static async Task<EndOfDay> EndOfDayAsync(ReceivablesDesk desk, Guid acme, Guid globex, int day)
    {
        var a = await desk.BooksAsync(acme);
        var g = await desk.BooksAsync(globex);

        var owing = a.Invoices.Concat(g.Invoices)
            .Where(i => i.Status != "DRAFT" && i.BalanceDue > 0m)
            .ToDictionary(i => i.InvoiceNumber, i => i.BalanceDue);

        return new EndOfDay(day, owing,
            a.Ledger.LastOrDefault()?.RunningBalance ?? 0m, a.OnAccount,
            g.Ledger.LastOrDefault()?.RunningBalance ?? 0m, g.OnAccount);
    }

    private static ReceivablesReportService ReportsFor(Month m)
    {
        var db = Receivables.Db(m.Desk.Org, m.World.DbName);

        var names = new Mock<ISupplierNameLookupService>();
        names.Setup(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()))
             .ReturnsAsync((IReadOnlyList<Guid> ids) => m.World.NamesFor(m.Desk.Org, ids));

        var templates = new Mock<IPoDocumentTemplateService>();
        templates.Setup(t => t.GetActiveAsync()).ReturnsAsync((PoDocumentTemplateModel?)null);

        return new ReceivablesReportService(db, names.Object, templates.Object, m.World.Clock);
    }

    private static DateTime Sept(int day) => new(2026, 9, day);

    // ── The scenario itself ──────────────────────────────────────────────────

    [Fact]
    public async Task The_month_really_does_what_the_scenario_claims_so_the_comparisons_below_mean_something()
    {
        var m = await LiveAsync();
        EndOfDay Day(int d) => m.Days.Single(x => x.Day == d);

        Day(1).Owing.Should().HaveCount(3);
        Day(10).Owing.Should().NotContainKey(m.A1, "the cheque paid A1 in full").And.Contain(m.A2, 300m);
        Day(15).Owing.Should().Contain(m.A1, 1000m).And.Contain(m.A2, 500m, "the cheque bounced and both are owed again");
        Day(16).Owing.Should().NotContainKey(m.A1).And.NotContainKey(m.A3, "2000 was applied to A1 and A3 by hand");
        Day(16).AcmeOnAccount.Should().Be(200m);
        Day(22).Owing.Should().NotContainKey(m.G1);
        Day(25).Owing.Keys.Should().NotBeEmpty();
    }

    // ── R3 aging ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task On_every_day_the_aging_says_what_the_real_services_held_that_evening()
    {
        var m = await LiveAsync();

        var compared = 0;
        foreach (var day in m.Days.Where(d => !BackDatedWindow.Contains(d.Day)))
        {
            var report = await ReportsFor(m).GetAgingReceivablesForExportAsync(new AgingReceivablesFilter { AsOf = Sept(day.Day) });

            report.Invoices.ToDictionary(i => i.InvoiceNumber, i => i.Outstanding).Should().BeEquivalentTo(day.Owing, $"as of 2026-09-{day.Day:00}");
            compared += day.Owing.Count;
        }

        compared.Should().BeGreaterThan(30);
    }

    [Fact]
    public async Task What_customers_are_aged_at_plus_what_they_have_paid_on_account_is_what_their_ledger_says_they_owe()
    {
        var m = await LiveAsync();

        foreach (var day in m.Days.Where(d => !BackDatedWindow.Contains(d.Day)))
            foreach (var (customer, ledger, onAccount) in new[] { (m.Acme, day.AcmeLedger, day.AcmeOnAccount), (m.Globex, day.GlobexLedger, day.GlobexOnAccount) })
            {
                var report = await ReportsFor(m).GetAgingReceivablesAsync(new AgingReceivablesFilter { AsOf = Sept(day.Day), PartnerId = customer });

                var aged = report.Totals.SingleOrDefault()?.Total ?? 0m;
                (aged - onAccount).Should().Be(ledger, $"customer {customer} as of 2026-09-{day.Day:00}: aged {aged}, on account {onAccount}");
            }
    }

    [Fact]
    public async Task A_receipt_dated_the_14th_and_keyed_the_18th_reduces_the_invoice_from_the_14th_in_the_report_though_the_services_only_knew_on_the_18th()
    {
        var m = await LiveAsync();
        var fifteenth = m.Days.Single(d => d.Day == 15);

        var report = await ReportsFor(m).GetAgingReceivablesForExportAsync(new AgingReceivablesFilter { AsOf = Sept(15) });

        fifteenth.Owing[m.A2].Should().Be(500m, "on the 15th the services did not yet know of the 100");
        report.Invoices.Single(i => i.InvoiceNumber == m.A2).Outstanding.Should().Be(400m, "but it came in on the 14th, and the books now say so");
        report.Invoices.Single(i => i.InvoiceNumber == m.A2).AmountPaid.Should().Be(100m);

        var thirteenth = await ReportsFor(m).GetAgingReceivablesForExportAsync(new AgingReceivablesFilter { AsOf = Sept(13) });
        thirteenth.Invoices.Single(i => i.InvoiceNumber == m.A2).Outstanding.Should().Be(300m, "before it came in: A2 less the 200 the cheque paid, which had not yet bounced");
    }

    [Fact]
    public async Task After_the_month_the_aging_is_exactly_what_the_invoices_themselves_say_is_owing()
    {
        var m = await LiveAsync();
        var final = m.Days.Last();

        var report = await ReportsFor(m).GetAgingReceivablesForExportAsync(new AgingReceivablesFilter { AsOf = Sept(LastDay) });

        // The back-dated receipt is in the books by now, so the invoices and the report agree on the 25th too.
        report.Invoices.ToDictionary(i => i.InvoiceNumber, i => i.Outstanding).Should().BeEquivalentTo(final.Owing);
        report.Invoices.Sum(i => i.Outstanding).Should().Be(final.Owing.Values.Sum());
    }

    // ── R2 customer ledger ───────────────────────────────────────────────────

    [Fact]
    public async Task On_every_day_the_customer_ledger_closes_at_the_balance_the_real_ledger_held_that_evening()
    {
        var m = await LiveAsync();

        foreach (var day in m.Days.Where(d => !BackDatedWindow.Contains(d.Day)))
            foreach (var (customer, ledger) in new[] { (m.Acme, day.AcmeLedger), (m.Globex, day.GlobexLedger) })
            {
                var report = await ReportsFor(m).GetCustomerLedgerAsync(new CustomerLedgerReportFilter { PartnerId = customer, DateTo = Sept(day.Day) });

                (report.Summaries.SingleOrDefault()?.ClosingBalance ?? 0m).Should().Be(ledger, $"customer {customer} as of 2026-09-{day.Day:00}");
            }
    }

    [Fact]
    public async Task With_no_range_the_customer_ledger_closes_at_the_last_running_balance_the_ledger_holds_back_dated_receipt_and_all()
    {
        var m = await LiveAsync();
        var final = m.Days.Last();

        var acme   = await ReportsFor(m).GetCustomerLedgerForExportAsync(new CustomerLedgerReportFilter { PartnerId = m.Acme });
        var globex = await ReportsFor(m).GetCustomerLedgerForExportAsync(new CustomerLedgerReportFilter { PartnerId = m.Globex });

        acme.Summaries.Single().ClosingBalance.Should().Be(final.AcmeLedger);
        globex.Summaries.Single().ClosingBalance.Should().Be(final.GlobexLedger);
    }

    [Fact]
    public async Task The_customer_ledger_lists_every_document_the_services_posted_each_once_with_a_balance_that_chains()
    {
        var m = await LiveAsync();

        var report = await ReportsFor(m).GetCustomerLedgerForExportAsync(new CustomerLedgerReportFilter { PartnerId = m.Acme });
        var books  = await m.Desk.BooksAsync(m.Acme);

        report.Entries.Select(e => e.SequenceNo).Should().BeEquivalentTo(books.Ledger.Select(e => e.SequenceNo));
        report.Entries.Select(e => e.ReferenceNumber).Should().Contain([m.A1, m.A2, m.A3]);

        var before = 0m;
        foreach (var e in report.Entries)
        {
            e.Balance.Should().Be(before + e.DebitAmount - e.CreditAmount, $"entry {e.SequenceNo} {e.ReferenceNumber}");
            before = e.Balance;
        }

        // The receipt keyed on the 18th sits on the 14th, between the entries of the 12th and the 15th.
        var order = report.Entries.Select(e => e.EntryDate.Date).ToList();
        order.Should().BeInAscendingOrder();
        report.Entries.Should().Contain(e => e.EntryDate.Date == Sept(14) && e.CreditAmount == 100m);
    }

    [Fact]
    public async Task The_customer_ledger_and_the_aging_tell_one_story_at_the_end_of_the_month()
    {
        var m = await LiveAsync();

        var ledger = await ReportsFor(m).GetCustomerLedgerForExportAsync(new CustomerLedgerReportFilter { PartnerId = m.Acme });
        var aging  = await ReportsFor(m).GetAgingReceivablesAsync(new AgingReceivablesFilter { AsOf = Sept(LastDay), PartnerId = m.Acme });
        var books  = await m.Desk.BooksAsync(m.Acme);

        (aging.Totals.Single().Total - books.OnAccount).Should().Be(ledger.Summaries.Single().ClosingBalance);
    }
}
