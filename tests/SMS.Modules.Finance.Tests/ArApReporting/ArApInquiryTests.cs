using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Routing;
using Moq;
using SMS.Modules.Finance.ArAp.Reporting;
using SMS.Modules.Finance.Controllers;
using SMS.Modules.Lookups.Services;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Finance.Tests.ArApReporting;

/// <summary>A48 RPT — C6 / C12 inquiry (balance, open items, transactions, KPIs), the HTTP surface, and agreement with A29 R3.</summary>
public class ArApInquiryTests
{
    private static readonly DateTime Today = ArApBooks.Today;
    private static DateOnly D(DateTime d) => DateOnly.FromDateTime(d);

    [Fact]
    public async Task Customer_balance_nets_open_invoices_and_credits_per_currency_and_in_base()
    {
        var books = new ArApBooks();
        var acme  = books.Partner("Acme");
        var inv   = await books.SaleAsync(acme, Today.AddDays(-60), 1000m);
        await books.SaleAsync(acme, Today.AddDays(-5), 10m, currency: "USD", rate: 280m);
        await books.ReceiveAsync(acme, Today.AddDays(-1), 1200m, [(inv, 1000m)]);    // 200 on account

        await using var db = books.Db();
        var b = await books.Reports(db).GetCustomerBalanceAsync(acme);

        b.PartnerName.Should().Be("Acme");
        b.PartnerRole.Should().Be("Customer");
        b.TotalOpen.Should().Be(2800m);
        b.TotalCredits.Should().Be(200m);
        b.NetBalance.Should().Be(2600m);
        b.TotalOverdue.Should().Be(0m);
        b.OpenItemCount.Should().Be(2);
        b.OldestOpenDate.Should().Be(D(Today.AddDays(-5)));
        b.ByCurrency.Select(c => (c.CurrencyCode, c.NetBalance)).Should().Equal(("PKR", -200m), ("USD", 10m));

        var past = await books.Reports(db).GetCustomerBalanceAsync(acme, D(Today.AddDays(-2)));
        past.NetBalance.Should().Be(1000m + 2800m);
        past.TotalOverdue.Should().Be(1000m);
    }

    [Fact]
    public async Task Unknown_partner_is_404()
    {
        var books = new ArApBooks();
        await using var db = books.Db();
        await FluentActions.Invoking(() => books.Reports(db).GetVendorBalanceAsync(Guid.NewGuid()))
            .Should().ThrowAsync<NotFoundException>().WithMessage("Business partner with key '*' was not found.");
    }

    [Fact]
    public async Task Open_items_page_sort_and_filter()
    {
        var books = new ArApBooks();
        var acme  = books.Partner("Acme");
        for (var i = 1; i <= 7; i++) await books.SaleAsync(acme, Today.AddDays(-10 * i), 100m * i);
        await books.ReceiveAsync(acme, Today.AddDays(-1), 25m);

        await using var db = books.Db();
        var svc = books.Reports(db);

        var first = await svc.GetCustomerOpenItemsAsync(acme, new OpenItemsFilter { PageSize = 3 });
        first.TotalRecords.Should().Be(8);
        first.TotalPages.Should().Be(3);
        first.HasNext.Should().BeTrue();
        first.Data.Select(i => i.OpenAmount).Should().Equal(700m, 600m, 500m);   // by due date, oldest first

        var overdue = await svc.GetCustomerOpenItemsAsync(acme, new OpenItemsFilter { OverdueOnly = true, PageSize = 200 });
        overdue.Data.Should().OnlyContain(i => i.IsOverdue && !i.IsCredit);
        overdue.Data.Should().HaveCount(4);                                       // 40..70 days old with 30-day terms

        var credit = (await svc.GetCustomerOpenItemsAsync(acme, new OpenItemsFilter { PageSize = 200 })).Data.Single(i => i.IsCredit);
        credit.OpenAmount.Should().Be(-25m);
        credit.DocumentType.Should().Be(OpenItemTypes.CustomerPayment);

        (await svc.GetCustomerOpenItemsAsync(acme, new OpenItemsFilter { IncludeCredits = false })).TotalRecords.Should().Be(7);
        (await svc.GetCustomerOpenItemsAsync(acme, new OpenItemsFilter { PageSize = 1000 })).PageSize.Should().Be(200);
    }

    [Fact]
    public async Task Customer_transactions_page_with_a_running_balance_that_spans_pages_and_the_opening()
    {
        var books = new ArApBooks();
        var acme  = books.Partner("Acme");
        await books.SaleAsync(acme, new DateTime(2026, 9, 1), 1000m);
        for (var i = 0; i < 5; i++) await books.SaleAsync(acme, new DateTime(2026, 10, 1 + i), 100m);
        await books.SaleAsync(acme, new DateTime(2026, 10, 3), 7m, currency: "USD", rate: 280m);

        await using var db = books.Db();
        var svc = books.Reports(db);
        var page2 = await svc.GetCustomerTransactionsAsync(acme, new PartnerTransactionFilter
        {
            From = new DateOnly(2026, 10, 1), To = new DateOnly(2026, 10, 31), Page = 2, PageSize = 2
        });

        page2.TotalRecords.Should().Be(6);
        // Order: 1 Oct 100, 2 Oct 100, 3 Oct 100, 3 Oct USD 7 (posted later), 4 Oct, 5 Oct
        page2.Data.Select(t => (t.CurrencyCode, t.RunningBalance)).Should().Equal(("PKR", 1300m), ("USD", 7m));

        var currency = await svc.GetCustomerTransactionsAsync(acme, new PartnerTransactionFilter { CurrencyCode = "USD" });
        currency.Data.Should().ContainSingle().Which.RunningBalance.Should().Be(7m);
    }

    [Fact]
    public async Task Vendor_transactions_take_each_documents_currency()
    {
        var books  = new ArApBooks();
        var vendor = books.Partner("Steel Co");
        var usd = await books.BillAsync(vendor, Today.AddDays(-20), 100m, currency: "USD", rate: 280m);
        await books.BillAsync(vendor, Today.AddDays(-15), 5000m);
        await books.PayAsync(vendor, Today.AddDays(-5), [(usd, 40m)], currency: "USD", rate: 281m);

        await using var db = books.Db();
        var page = await books.Reports(db).GetVendorTransactionsAsync(vendor, new PartnerTransactionFilter());

        page.Data.Select(t => (t.CurrencyCode, t.Debit, t.Credit, t.RunningBalance))
            .Should().Equal(("USD", 100m, 0m, 100m), ("PKR", 5000m, 0m, 5000m), ("USD", 0m, 40m, 60m));
    }

    [Fact]
    public async Task AR_summary_kpis()
    {
        var books = new ArApBooks();
        var a = books.Partner("Alpha"); var b = books.Partner("Bravo");
        var inv = await books.SaleAsync(a, Today.AddDays(-100), 2000m);     // 70 days overdue
        await books.SaleAsync(a, Today.AddDays(-10), 1000m);                // current
        await books.SaleAsync(b, Today.AddDays(-40), 1000m);                // 10 days overdue
        await books.ReceiveAsync(a, Today.AddDays(-3), 500m, [(inv, 500m)]);

        await using var db = books.Db();
        var k = await books.Reports(db).GetArSummaryAsync(D(Today));

        k.TotalOutstanding.Should().Be(3500m);
        k.TotalOverdue.Should().Be(2500m);
        k.TotalCurrent.Should().Be(1000m);
        k.NetReceivable.Should().Be(3500m);
        k.TotalCustomersWithBalance.Should().Be(2);
        k.TotalCustomersOverdue.Should().Be(2);
        k.InvoicedInPeriod.Should().Be(4000m);
        k.CollectedInPeriod.Should().Be(500m);
        k.CollectionRate.Should().Be(12.5m);
        k.Dso.Should().Be(Math.Round(3500m / 4000m * 365m, 1));
        k.DaysBeyondTerms.Should().Be(Math.Round((1500m * 70 + 1000m * 10) / 2500m, 1));
        k.AgingBuckets.Sum().Should().Be(3500m);
        k.AgingLabels.Should().HaveCount(6);
    }

    [Fact]
    public async Task AP_summary_kpis_with_due_windows()
    {
        var books  = new ArApBooks();
        var vendor = books.Partner("Steel Co");
        await books.BillAsync(vendor, Today.AddDays(-25), 100m);   // due in 5 days
        await books.BillAsync(vendor, Today.AddDays(-10), 200m);   // due in 20 days
        await books.BillAsync(vendor, Today.AddDays(-5), 400m, termsDays: 60);   // due in 55 days
        await books.BillAsync(vendor, Today.AddDays(-45), 800m);   // overdue

        await using var db = books.Db();
        var k = await books.Reports(db).GetApSummaryAsync(D(Today));

        k.TotalOutstanding.Should().Be(1500m);
        k.TotalOverdue.Should().Be(800m);
        k.TotalCurrent.Should().Be(700m);
        k.TotalDueThisWeek.Should().Be(100m);
        k.TotalDueThisMonth.Should().Be(300m);
        k.TotalVendorsWithBalance.Should().Be(1);
        k.Dpo.Should().Be(Math.Round(1500m / 1500m * 365m, 1));
        k.DiscountOpportunityAmount.Should().Be(0m);
    }

    [Fact]
    public async Task New_aging_agrees_with_the_existing_A29_R3_report()
    {
        var books = new ArApBooks();
        var a = books.Partner("Alpha"); var b = books.Partner("Bravo");
        var inv = await books.SaleAsync(a, Today.AddDays(-100), 2000m);
        await books.SaleAsync(a, Today.AddDays(-10), 1000m);
        await books.SaleAsync(b, Today.AddDays(-40), 1000m);
        var c = await books.SaleAsync(b, Today.AddDays(-15), 300m);
        await books.CancelAsync(c, Today.AddDays(-1));
        await books.ReceiveAsync(a, Today.AddDays(-3), 500m, [(inv, 500m)]);

        await using var db = books.Db();
        var templates = new Mock<IPoDocumentTemplateService>();
        var r3 = await new ReceivablesReportService(db, books.NameLookup(), templates.Object, books.Clock)
            .GetAgingReceivablesAsync(new AgingReceivablesFilter { AsOf = Today, PageSize = 100 });
        var mine = await books.Reports(db).GetAgingDetailsAsync(false, new AgingDetailsRequest { PageSize = 200 });

        mine.Data.Sum(d => d.OpenAmount).Should().Be(r3.Totals.Sum(t => t.Total));
        mine.Data.Select(d => (d.DocumentUuid, d.OpenAmount))
            .Should().BeEquivalentTo(r3.Invoices.Select(i => (i.InvoiceUuid, i.Outstanding)));
    }

    // ── HTTP surface ─────────────────────────────────────────────────────────

    private static IEnumerable<MethodInfo> Actions(Type t) =>
        t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
         .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any());

    [Theory]
    [InlineData(typeof(ArReportsController))]
    [InlineData(typeof(ApReportsController))]
    [InlineData(typeof(StatementLogController))]
    public void Every_action_names_a_permission_and_only_writes_carry_the_feature_gate(Type controller)
    {
        foreach (var action in Actions(controller))
        {
            action.GetCustomAttributes<RequirePermissionAttribute>().Should().NotBeEmpty($"{controller.Name}.{action.Name} needs a permission");
            var isWrite = action.GetCustomAttributes<HttpMethodAttribute>().Any(h => h.HttpMethods.Contains("POST"))
                       && action.Name is "EmailStatement" or "Batch";
            action.GetCustomAttributes<RequiresFeatureAttribute>().Any().Should().Be(isWrite, $"{controller.Name}.{action.Name}");
        }
    }

    [Theory]
    [InlineData(typeof(ArReportsController), "Aging", "AR_VIEW|REPORT_VIEW")]
    [InlineData(typeof(ArReportsController), "AgingExport", "AR_VIEW&REPORT_EXPORT")]
    [InlineData(typeof(ArReportsController), "EmailStatement", "AR_STATEMENT_SEND")]
    [InlineData(typeof(ArReportsController), "Batch", "AR_STATEMENT_SEND")]
    [InlineData(typeof(ArReportsController), "Statement", "AR_VIEW")]
    [InlineData(typeof(ApReportsController), "Aging", "AP_VIEW|REPORT_VIEW")]
    [InlineData(typeof(ApReportsController), "Reconcile", "AP_EXECUTE")]
    [InlineData(typeof(ApReportsController), "Statement", "AP_VIEW")]
    [InlineData(typeof(ApReportsController), "Dpo", "AP_VIEW")]
    [InlineData(typeof(ArReportsController), "OpenItems", "AR_VIEW|AR_CREDIT_MEMO")]
    [InlineData(typeof(ApReportsController), "OpenItems", "AP_VIEW|AP_DEBIT_MEMO")]
    [InlineData(typeof(ArReportsController), "Balance", "AR_VIEW")]
    [InlineData(typeof(ArReportsController), "Transactions", "AR_VIEW")]
    [InlineData(typeof(ApReportsController), "Balance", "AP_VIEW")]
    [InlineData(typeof(ApReportsController), "Transactions", "AP_VIEW")]
    public void Permissions_match_the_contract(Type controller, string action, string expected)
    {
        var attributes = controller.GetMethod(action)!.GetCustomAttributes<RequirePermissionAttribute>()
            .Select(a => string.Join("|", a.AnyOf)).ToList();
        string.Join("&", attributes).Should().Be(expected);
    }

    [Fact]
    public void Routes_match_the_contract()
    {
        static string Route(Type t, string action) =>
            t.GetCustomAttribute<Microsoft.AspNetCore.Mvc.RouteAttribute>()!.Template + "/" +
            t.GetMethod(action)!.GetCustomAttributes<HttpMethodAttribute>().Single().Template;

        Route(typeof(ArReportsController), "Aging").Should().Be("api/finance/ar/aging");
        Route(typeof(ArReportsController), "OpenItems").Should().Be("api/finance/ar/customers/{uuid:guid}/open-items");
        Route(typeof(ArReportsController), "EmailStatement").Should().Be("api/finance/ar/customers/{uuid:guid}/statement/email");
        Route(typeof(ArReportsController), "Batch").Should().Be("api/finance/ar/statements/batch");
        Route(typeof(ApReportsController), "Reconcile").Should().Be("api/finance/ap/vendors/{uuid:guid}/reconciliation");
        Route(typeof(StatementLogController), "Log").Should().Be("api/finance/statements/log");
    }
}
