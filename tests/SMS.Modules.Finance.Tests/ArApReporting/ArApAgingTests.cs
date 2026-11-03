using FluentAssertions;
using SMS.Modules.Finance.ArAp.Reporting;
using SMS.Modules.Finance.Domain;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Finance.Tests.ArApReporting;

/// <summary>A48 RPT — TS-AG-001..008, BR-AG-001..006, base-currency totals, as-of rules, credits, isolation.</summary>
public class ArApAgingTests
{
    private static readonly DateTime Today = ArApBooks.Today;
    private static DateOnly D(DateTime d) => DateOnly.FromDateTime(d);

    [Fact]
    public async Task TS_AG_001_default_buckets_assign_by_days_past_due()
    {
        var books = new ArApBooks();
        var acme  = books.Partner("Acme");
        // due = invoice date + 30; days past due on 25 Oct: -5, 10, 45, 75, 100, 200
        await books.SaleAsync(acme, Today.AddDays(-25), 100m);
        await books.SaleAsync(acme, Today.AddDays(-40), 200m);
        await books.SaleAsync(acme, Today.AddDays(-75), 300m);
        await books.SaleAsync(acme, Today.AddDays(-105), 400m);
        await books.SaleAsync(acme, Today.AddDays(-130), 500m);
        await books.SaleAsync(acme, Today.AddDays(-230), 600m);

        await using var db = books.Db();
        var report = await books.Reports(db).GetAgedReceivablesAsync(new AgingReportRequest());

        report.BucketLabels.Should().Equal("Current", "1-30", "31-60", "61-90", "91-120", "120+");
        report.BucketDays.Should().Equal(30, 60, 90, 120);
        report.Basis.Should().Be("DueDate");
        var line = report.Lines.Should().ContainSingle().Subject;
        line.PartnerName.Should().Be("Acme");
        line.BucketAmounts.Should().Equal(100m, 200m, 300m, 400m, 500m, 600m);
        line.TotalOutstanding.Should().Be(2100m);
        line.TotalOverdue.Should().Be(2000m);
        line.DaysBeyondTerms.Should().Be(200);
        line.OldestBucket.Should().Be(5);
        report.Totals.BucketTotals.Should().Equal(100m, 200m, 300m, 400m, 500m, 600m);
        report.Totals.TotalOpenItems.Should().Be(6);
    }

    [Fact]
    public async Task TS_AG_002_payables_with_custom_buckets()
    {
        var books  = new ArApBooks();
        var vendor = books.Partner("Steel Co");
        // days past due: -1, 10, 20, 40, 50, 70
        foreach (var (age, amount) in new[] { (29, 10m), (40, 20m), (50, 30m), (70, 40m), (80, 50m), (100, 60m) })
            await books.BillAsync(vendor, Today.AddDays(-age), amount);

        await using var db = books.Db();
        var report = await books.Reports(db).GetAgedPayablesAsync(new AgingReportRequest { Buckets = "0,15,30,45,60" });

        report.ReportType.Should().Be("Payable");
        report.BucketLabels.Should().Equal("Current", "1-15", "16-30", "31-45", "46-60", "60+");
        report.Lines.Single().BucketAmounts.Should().Equal(10m, 20m, 30m, 40m, 50m, 60m);

        // The contract spelling (no leading 0) is the same set.
        var same = await books.Reports(db).GetAgedPayablesAsync(new AgingReportRequest { Buckets = "15,30,45,60" });
        same.BucketLabels.Should().Equal(report.BucketLabels);
    }

    [Theory]
    [InlineData("30", AgingBucketSet.TooFewMessage)]
    [InlineData("0,30", AgingBucketSet.TooFewMessage)]
    [InlineData("60,30", AgingBucketSet.AscendingMessage)]
    [InlineData("30,30,60", AgingBucketSet.AscendingMessage)]
    [InlineData("-5,30", AgingBucketSet.AscendingMessage)]
    [InlineData("a,b", AgingBucketSet.AscendingMessage)]
    public async Task BR_AG_006_bad_buckets_are_400(string buckets, string message)
    {
        var books = new ArApBooks();
        await using var db = books.Db();
        var act = () => books.Reports(db).GetAgedReceivablesAsync(new AgingReportRequest { Buckets = buckets });
        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage(message);
    }

    [Fact]
    public async Task Minimum_three_buckets_is_accepted()
    {
        var set = AgingBucketSet.Parse("30,60");
        set.Labels.Should().Equal("Current", "1-30", "31-60", "60+");
        AgingBucketSet.Parse(null).Labels.Should().HaveCount(6);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Unknown_basis_or_sort_is_400()
    {
        var books = new ArApBooks();
        await using var db = books.Db();
        await FluentActions.Invoking(() => books.Reports(db).GetAgedReceivablesAsync(new AgingReportRequest { Basis = "Posting" }))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Unknown basis 'Posting'. Allowed: DueDate, DocumentDate.");
        await FluentActions.Invoking(() => books.Reports(db).GetAgedReceivablesAsync(new AgingReportRequest { SortBy = "Colour" }))
            .Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Document_date_basis_ages_from_the_invoice_date()
    {
        var books = new ArApBooks();
        var acme  = books.Partner("Acme");
        await books.SaleAsync(acme, Today.AddDays(-45), 100m, termsDays: 60); // not due yet, but 45 days old

        await using var db = books.Db();
        var due = await books.Reports(db).GetAgedReceivablesAsync(new AgingReportRequest());
        var doc = await books.Reports(db).GetAgedReceivablesAsync(new AgingReportRequest { Basis = "DocumentDate" });

        due.Lines.Single().BucketAmounts[0].Should().Be(100m);       // Current = not yet due
        doc.Lines.Single().BucketAmounts[2].Should().Be(100m);       // 31-60 days old
        doc.Lines.Single().TotalOverdue.Should().Be(0m);             // overdue is always by due date
    }

    [Fact]
    public async Task TS_AG_003_DSO_with_a_90_day_period()
    {
        var books = new ArApBooks();
        var acme  = books.Partner("Acme");
        var a = await books.SaleAsync(acme, Today.AddDays(-80), 3000m);
        await books.SaleAsync(acme, Today.AddDays(-20), 6000m);
        await books.SaleAsync(acme, Today.AddDays(-200), 5000m);     // outside the period, still owed
        await books.ReceiveAsync(acme, Today.AddDays(-5), 3000m, [(a, 3000m)]);
        // Balance = 6000 + 5000 = 11000; credit sales in 90 days = 9000 → 11000 / 9000 × 90 = 110.0

        await using var db = books.Db();
        var dso = await books.Reports(db).GetDsoAsync(D(Today), 90);

        dso.Measure.Should().Be("DSO");
        dso.Balance.Should().Be(11000m);
        dso.PeriodAmount.Should().Be(9000m);
        dso.Days.Should().Be(110.0m);
    }

    [Fact]
    public async Task TS_AG_004_DPO_uses_supplier_invoice_purchases()
    {
        var books  = new ArApBooks();
        var vendor = books.Partner("Steel Co");
        var bill   = await books.BillAsync(vendor, Today.AddDays(-100), 4000m);
        await books.BillAsync(vendor, Today.AddDays(-10), 2000m);
        await books.BillAsync(vendor, Today.AddDays(-5), 999m, matchStatus: "Pending");   // not a payable, not a purchase
        await books.PayAsync(vendor, Today.AddDays(-3), [(bill, 1000m)]);
        // AP = 3000 + 2000 = 5000; purchases in 365 days = 6000 → 5000 / 6000 × 365 = 304.2

        await using var db = books.Db();
        var dpo = await books.Reports(db).GetDpoAsync(D(Today));

        dpo.Balance.Should().Be(5000m);
        dpo.PeriodAmount.Should().Be(6000m);
        dpo.Days.Should().Be(304.2m);
    }

    [Fact]
    public async Task DSO_is_zero_when_nothing_was_invoiced()
    {
        var books = new ArApBooks();
        await using var db = books.Db();
        (await books.Reports(db).GetDsoAsync(D(Today), 30)).Days.Should().Be(0m);
        await FluentActions.Invoking(() => books.Reports(db).GetDsoAsync(D(Today), 0)).Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task TS_AG_005_drill_down_from_a_bucket_to_its_documents()
    {
        var books = new ArApBooks();
        var acme  = books.Partner("Acme");
        var beta  = books.Partner("Beta");
        await books.SaleAsync(acme, Today.AddDays(-75), 300m);   // 45 days past due → 31-60
        await books.SaleAsync(beta, Today.AddDays(-80), 250m);   // 50 → 31-60
        await books.SaleAsync(acme, Today.AddDays(-10), 50m);    // Current

        await using var db = books.Db();
        var svc = books.Reports(db);

        var bucket = await svc.GetAgingDetailsAsync(false, new AgingDetailsRequest { BucketIndex = 2 });
        bucket.TotalRecords.Should().Be(2);
        bucket.Data.Select(d => d.OpenAmount).Should().Equal(250m, 300m);   // oldest first
        bucket.Data.Should().OnlyContain(d => d.BucketLabel == "31-60");
        bucket.Data.Select(d => d.PartnerName).Should().Equal("Beta", "Acme");

        var acmeOnly = await svc.GetAgingDetailsAsync(false, new AgingDetailsRequest { CustomerUuid = acme });
        acmeOnly.Data.Select(d => d.OpenAmount).Should().BeEquivalentTo([300m, 50m]);

        var withDetails = await svc.GetAgedReceivablesAsync(new AgingReportRequest { IncludeDetails = true, CustomerUuid = acme });
        withDetails.Lines.Single().Details!.Select(d => d.DocumentNumber).Should().HaveCount(2);

        await FluentActions.Invoking(() => svc.GetAgingDetailsAsync(false, new AgingDetailsRequest { BucketIndex = 9 }))
            .Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task TS_AG_006_partially_paid_invoice_ages_its_residual()
    {
        var books = new ArApBooks();
        var acme  = books.Partner("Acme");
        var inv   = await books.SaleAsync(acme, Today.AddDays(-50), 1000m);
        await books.ReceiveAsync(acme, Today.AddDays(-2), 400m, [(inv, 400m)]);

        await using var db = books.Db();
        var details = await books.Reports(db).GetAgingDetailsAsync(false, new AgingDetailsRequest());

        var d = details.Data.Single();
        d.OriginalAmount.Should().Be(1000m);
        d.OpenAmount.Should().Be(600m);
        d.Status.Should().Be("PARTIALLY_PAID");
        d.BucketLabel.Should().Be("1-30");
    }

    [Fact]
    public async Task TS_AG_007_reversed_documents_are_excluded()
    {
        var books  = new ArApBooks();
        var acme   = books.Partner("Acme");
        var vendor = books.Partner("Steel Co");
        var cancelled = await books.SaleAsync(acme, Today.AddDays(-20), 500m);
        await books.CancelAsync(cancelled, Today.AddDays(-10));
        var reversed = await books.BillAsync(vendor, Today.AddDays(-20), 700m);
        await books.ReverseAsync(reversed, Today.AddDays(-10));
        var kept = await books.SaleAsync(acme, Today.AddDays(-20), 80m);
        var bounced = await books.ReceiveAsync(acme, Today.AddDays(-15), 80m, [(kept, 80m)]);
        await books.BounceAsync(bounced, Today.AddDays(-12));

        await using var db = books.Db();
        var svc = books.Reports(db);
        var ar  = await svc.GetAgedReceivablesAsync(new AgingReportRequest());
        var ap  = await svc.GetAgedPayablesAsync(new AgingReportRequest());

        ar.Totals.GrandTotal.Should().Be(80m);                      // the bounced payment no longer reduces it
        ap.Totals.GrandTotal.Should().Be(0m);

        // Before the cancellation / reversal they were owed.
        var earlier = D(Today.AddDays(-11));
        (await svc.GetAgedReceivablesAsync(new AgingReportRequest { AsOfDate = D(Today.AddDays(-11)) })).Totals.GrandTotal.Should().Be(580m);
        (await svc.GetAgedPayablesAsync(new AgingReportRequest { AsOfDate = earlier })).Totals.GrandTotal.Should().Be(700m);
    }

    [Fact]
    public async Task TS_AG_008_drafts_and_unapproved_bills_are_excluded()
    {
        var books  = new ArApBooks();
        var acme   = books.Partner("Acme");
        var vendor = books.Partner("Steel Co");
        await books.SaleAsync(acme, Today.AddDays(-20), 500m, status: SalesInvoiceStatuses.Draft);
        await books.BillAsync(vendor, Today.AddDays(-20), 700m, matchStatus: "Pending");
        await books.BillAsync(vendor, Today.AddDays(-20), 710m, matchStatus: "Variance");
        await books.BillAsync(vendor, Today.AddDays(-20), 720m, matchStatus: "Rejected");

        await using var db = books.Db();
        var svc = books.Reports(db);
        (await svc.GetAgedReceivablesAsync(new AgingReportRequest())).Lines.Should().BeEmpty();
        (await svc.GetAgedPayablesAsync(new AgingReportRequest())).Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task BR_AG_001_as_of_a_past_day_ignores_later_documents_and_payments()
    {
        var books = new ArApBooks();
        var acme  = books.Partner("Acme");
        var inv   = await books.SaleAsync(acme, Today.AddDays(-40), 1000m);
        await books.ReceiveAsync(acme, Today.AddDays(-5), 1000m, [(inv, 1000m)]);
        await books.SaleAsync(acme, Today.AddDays(-3), 50m);

        await using var db = books.Db();
        var svc = books.Reports(db);
        (await svc.GetAgedReceivablesAsync(new AgingReportRequest { AsOfDate = D(Today.AddDays(-10)) })).Totals.GrandTotal.Should().Be(1000m);
        (await svc.GetAgedReceivablesAsync(new AgingReportRequest())).Totals.GrandTotal.Should().Be(50m);
    }

    [Fact]
    public async Task Unapplied_credits_sit_beside_the_buckets_and_net_the_balance()
    {
        var books = new ArApBooks();
        var acme  = books.Partner("Acme");
        await books.SaleAsync(acme, Today.AddDays(-10), 1000m);
        await books.ReceiveAsync(acme, Today.AddDays(-1), 300m);          // on account

        await using var db = books.Db();
        var line = (await books.Reports(db).GetAgedReceivablesAsync(new AgingReportRequest())).Lines.Single();
        line.TotalOutstanding.Should().Be(1000m);
        line.UnappliedCredits.Should().Be(300m);
        line.NetBalance.Should().Be(700m);
    }

    [Fact]
    public async Task Credit_notes_reduce_invoices_by_their_application_rows_as_of_the_day_and_directly_A38_style()
    {
        var books = new ArApBooks();
        var acme  = books.Partner("Acme");
        var inv   = await books.SaleAsync(acme, Today.AddDays(-30), 1000m);
        var other = await books.SaleAsync(acme, Today.AddDays(-30), 500m);
        var memo  = await books.CreditNoteAsync(acme, Today.AddDays(-20), 300m);                       // standalone
        await books.ApplyCreditAsync(memo, inv, 200m, Today.AddDays(-10));
        await books.CreditNoteAsync(acme, Today.AddDays(-15), 50m, original: other, directlyApplied: 50m);  // A38 direct

        await using var db = books.Db();
        var svc = books.Reports(db);

        var now = await svc.GetCustomerOpenItemsAsync(acme, new OpenItemsFilter { PageSize = 200 });
        now.Data.Single(i => i.DocumentUuid == inv.UUID).OpenAmount.Should().Be(800m);
        now.Data.Single(i => i.DocumentUuid == other.UUID).OpenAmount.Should().Be(450m);
        now.Data.Single(i => i.DocumentUuid == memo.UUID).OpenAmount.Should().Be(-100m);
        now.Data.Should().HaveCount(3);                                                       // the A38 note is used up

        var before = await svc.GetCustomerOpenItemsAsync(acme, new OpenItemsFilter { AsOfDate = D(Today.AddDays(-12)), PageSize = 200 });
        before.Data.Single(i => i.DocumentUuid == inv.UUID).OpenAmount.Should().Be(1000m);
        before.Data.Single(i => i.DocumentUuid == memo.UUID).OpenAmount.Should().Be(-300m);

        (await svc.GetCustomerBalanceAsync(acme)).NetBalance.Should().Be(1500m - 300m - 50m);
    }

    [Fact]
    public async Task A_memo_applied_to_its_own_original_by_a_row_counts_once_and_one_applied_elsewhere_reduces_that_invoice()
    {
        var books = new ArApBooks();
        var acme  = books.Partner("Acme");
        var orig  = await books.SaleAsync(acme, Today.AddDays(-30), 1000m);
        var other = await books.SaleAsync(acme, Today.AddDays(-25), 600m);
        var memo  = await books.CreditNoteAsync(acme, Today.AddDays(-20), 400m, original: orig);   // SCN, AR_CREDIT_MEMO
        await books.ApplyCreditAsync(memo, orig, 100m, Today.AddDays(-19));                         // its own original: a row too
        await books.ApplyCreditAsync(memo, other, 250m, Today.AddDays(-4));                         // a different invoice

        await using var db = books.Db();
        var svc   = books.Reports(db);
        var items = (await svc.GetCustomerOpenItemsAsync(acme, new OpenItemsFilter { PageSize = 200 })).Data;
        items.Single(i => i.DocumentUuid == orig.UUID).OpenAmount.Should().Be(900m);     // not 800: no double count
        items.Single(i => i.DocumentUuid == other.UUID).OpenAmount.Should().Be(350m);
        items.Single(i => i.DocumentUuid == memo.UUID).OpenAmount.Should().Be(-50m);

        var before = (await svc.GetCustomerOpenItemsAsync(acme, new OpenItemsFilter { AsOfDate = D(Today.AddDays(-10)), PageSize = 200 })).Data;
        before.Single(i => i.DocumentUuid == other.UUID).OpenAmount.Should().Be(600m);
        before.Single(i => i.DocumentUuid == memo.UUID).OpenAmount.Should().Be(-300m);

        var aging = await svc.GetAgedReceivablesAsync(new AgingReportRequest());
        aging.Totals.GrandTotal.Should().Be(1250m);
        aging.Totals.NetBalance.Should().Be(1200m);
        (await svc.GetCustomerBalanceAsync(acme)).NetBalance.Should().Be(1600m - 400m);

        var statement = (await books.Statements(db).GenerateCustomerStatementAsync(acme,
            new StatementRequest { Type = "OpenItems" }, new StatementActor(1, "T"))).Statement;
        statement.Sections.Single().ClosingBalance.Should().Be(1200m);
    }

    [Fact]
    public async Task A_dunning_charge_is_an_open_receivable()
    {
        var books = new ArApBooks();
        var acme  = books.Partner("Acme");
        await books.SaleAsync(acme, Today.AddDays(-3), 25m, termsDays: 0, source: "DUNNING_CHARGE");

        await using var db = books.Db();
        var line = (await books.Reports(db).GetAgedReceivablesAsync(new AgingReportRequest())).Lines.Single();
        line.TotalOutstanding.Should().Be(25m);
        line.TotalOverdue.Should().Be(25m);
    }

    [Fact]
    public async Task Totals_are_base_currency_at_each_documents_rate_and_by_currency_in_its_own()
    {
        var books = new ArApBooks();
        var acme  = books.Partner("Acme");
        await books.SaleAsync(acme, Today.AddDays(-10), 100m, currency: "USD", rate: 280m);
        await books.SaleAsync(acme, Today.AddDays(-10), 1000m);
        await books.SaleAsync(acme, Today.AddDays(-10), 10m, currency: "EUR");   // no rate locked, no rate service → 1:1, flagged

        await using var db = books.Db();
        var svc = books.Reports(db);
        var report = await svc.GetAgedReceivablesAsync(new AgingReportRequest());

        report.BaseCurrencyCode.Should().Be("PKR");
        report.Totals.GrandTotal.Should().Be(28000m + 1000m + 10m);
        report.ByCurrency.Select(c => (c.CurrencyCode, c.Total)).Should().Equal(("EUR", 10m), ("PKR", 1000m), ("USD", 100m));
        var usd = await svc.GetAgedReceivablesAsync(new AgingReportRequest { CurrencyCode = "usd" });
        usd.Totals.GrandTotal.Should().Be(28000m);

        var details = await svc.GetAgingDetailsAsync(false, new AgingDetailsRequest());
        details.Data.Single(d => d.CurrencyCode == "EUR").RateMissing.Should().BeTrue();
        details.Data.Single(d => d.CurrencyCode == "USD").RateMissing.Should().BeFalse();
        details.Data.Single(d => d.CurrencyCode == "PKR").RateMissing.Should().BeFalse();
    }

    [Fact]
    public async Task Sorting_and_paging_of_partner_lines()
    {
        var books = new ArApBooks();
        var a = books.Partner("Alpha"); var b = books.Partner("Bravo"); var c = books.Partner("Charlie");
        await books.SaleAsync(a, Today.AddDays(-10), 300m);
        await books.SaleAsync(b, Today.AddDays(-200), 100m);
        await books.SaleAsync(c, Today.AddDays(-60), 200m);

        await using var db = books.Db();
        var svc = books.Reports(db);
        (await svc.GetAgedReceivablesAsync(new AgingReportRequest())).Lines.Select(l => l.PartnerName).Should().Equal("Alpha", "Charlie", "Bravo");
        (await svc.GetAgedReceivablesAsync(new AgingReportRequest { SortBy = "Partner" })).Lines.Select(l => l.PartnerName).Should().Equal("Alpha", "Bravo", "Charlie");
        (await svc.GetAgedReceivablesAsync(new AgingReportRequest { SortBy = "OldestBucket" })).Lines.Select(l => l.PartnerName).Should().Equal("Bravo", "Charlie", "Alpha");
        var page2 = await svc.GetAgedReceivablesAsync(new AgingReportRequest { SortBy = "Partner", Page = 2, PageSize = 2 });
        page2.Lines.Select(l => l.PartnerName).Should().Equal("Charlie");
        page2.TotalRecords.Should().Be(3);
        page2.TotalPages.Should().Be(2);
        page2.Totals.GrandTotal.Should().Be(600m);   // totals are the whole report, not the page
    }

    [Fact]
    public async Task Another_organizations_documents_never_appear_even_for_a_super_admin()
    {
        var books = new ArApBooks();
        var acme  = books.Partner("Acme");
        var other = Guid.NewGuid();
        await books.SaleAsync(acme, Today.AddDays(-10), 100m);
        await books.SaleAsync(acme, Today.AddDays(-10), 999m, org: other);

        await using var db = books.SuperAdminDb(books.Org);
        var report = await books.Reports(db).GetAgedReceivablesAsync(new AgingReportRequest());
        report.Totals.GrandTotal.Should().Be(100m);
    }

    [Fact]
    public async Task Payables_credits_advances_and_partial_payments()
    {
        var books  = new ArApBooks();
        var vendor = books.Partner("Steel Co");
        var bill   = await books.BillAsync(vendor, Today.AddDays(-40), 1000m);
        await books.PayAsync(vendor, Today.AddDays(-5), [(bill, 250m)]);
        await books.PayAsync(vendor, Today.AddDays(-4), [(bill, 100m)], status: "DRAFT");   // not posted: not paid
        await books.AdvanceAsync(vendor, Today.AddDays(-2), 90m);

        await using var db = books.Db();
        var line = (await books.Reports(db).GetAgedPayablesAsync(new AgingReportRequest())).Lines.Single();
        line.TotalOutstanding.Should().Be(750m);
        line.UnappliedCredits.Should().Be(90m);
        line.NetBalance.Should().Be(660m);
    }

    [Fact]
    public async Task Export_renders_pdf_and_xlsx()
    {
        var books = new ArApBooks();
        var acme  = books.Partner("Acme");
        await books.SaleAsync(acme, Today.AddDays(-50), 123.45m);

        await using var db = books.Db();
        var report = await books.Reports(db).GetAgingForExportAsync(false, new AgingReportRequest());
        report.Lines.Single().Details.Should().ContainSingle();

        var pdf = AgingExporters.Pdf(report, "Acme Supplies");
        System.Text.Encoding.ASCII.GetString(pdf, 0, 4).Should().Be("%PDF");
        using (var doc = UglyToad.PdfPig.PdfDocument.Open(pdf))
            string.Join(" ", doc.GetPages().Select(p => p.Text)).Should().Contain("AGED RECEIVABLES").And.Contain("123.45");

        var xlsx = AgingExporters.Excel(report);
        xlsx.Take(2).Should().Equal((byte)'P', (byte)'K');
    }
}
