using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Finance.ArAp.Reporting;
using SMS.Modules.Finance.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Finance.Tests.ArApReporting;

/// <summary>A48 RPT — the REV round: REV-16 (one plain email address), REV-17 (reversed-after-the-day applications), REV-18 (denominator rates).</summary>
public class ArApReviewFixTests
{
    private static readonly DateTime Today = ArApBooks.Today;
    private static DateOnly D(DateTime d) => DateOnly.FromDateTime(d);

    [Theory]
    [InlineData("ar@acme.test", true)]
    [InlineData("  ar@acme.test ", true)]
    [InlineData("a@x.com,b@y.com", false)]
    [InlineData("a@x.com;b@y.com", false)]
    [InlineData("Acme <ar@acme.test>", false)]
    [InlineData("ar@acme.test\r\nBcc: x@y.com", false)]
    [InlineData("ar@acme", false)]
    [InlineData("no-at-sign", false)]
    [InlineData("", false)]
    public void REV_16_only_one_plain_address_is_an_email(string value, bool ok) =>
        StatementService.LooksLikeEmail(value).Should().Be(ok);

    [Fact]
    public async Task REV_16_a_list_of_addresses_is_refused_and_nothing_is_sent()
    {
        var books = new ArApBooks();
        var acme  = books.Partner("Acme");
        await books.SaleAsync(acme, Today.AddDays(-5), 10m);

        await using var db = books.Db();
        await FluentActions.Invoking(() => books.Statements(db).EmailCustomerStatementAsync(acme,
                new EmailStatementRequest { EmailTo = "a@x.com,b@y.com" }, new StatementActor(1, "T")))
            .Should().ThrowAsync<BadRequestException>().WithMessage("'a@x.com,b@y.com' is not an email address.");
        books.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task REV_1_a_failed_delivery_is_logged_failed_single_409_and_batch_per_partner()
    {
        var books = new ArApBooks();
        var a = books.Partner("Alpha", "a@alpha.test");
        var b = books.Partner("Bravo", "b@bravo.test");
        await books.SaleAsync(a, Today.AddDays(-5), 100m);
        await books.SaleAsync(b, Today.AddDays(-5), 200m);
        books.FailEmail = true;   // the sender reports Failed (it does not throw)

        await using var db = books.Db();
        var svc = books.Statements(db);
        await FluentActions.Invoking(() => svc.EmailCustomerStatementAsync(a, new EmailStatementRequest(), new StatementActor(1, "T")))
            .Should().ThrowAsync<ConflictException>().WithMessage("The statement could not be emailed: SMTP is down");

        var batch = await svc.BatchCustomerStatementsAsync(new BatchStatementRequest { SendEmail = true }, new StatementActor(1, "T"));
        batch.Generated.Should().Be(2);
        batch.Emailed.Should().Be(0);
        batch.Failed.Should().Be(2);
        batch.Items.Should().OnlyContain(i => i.Outcome == "FAILED" && i.Error == "SMTP is down");

        var logs = await db.StatementLogs.AsNoTracking().ToListAsync();
        logs.Should().HaveCount(3).And.OnlyContain(l => l.EmailStatus == "Failed" && l.EmailError == "SMTP is down");
        books.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task REV_1_a_sender_that_answers_no_result_is_a_failure_not_sent()
    {
        var books = new ArApBooks();
        var a = books.Partner("Alpha", "a@alpha.test");
        await books.SaleAsync(a, Today.AddDays(-5), 100m);

        await using var db = books.Db();
        var silent = new Mock<IEmailSender>();   // a bare mock answers null for SendWithResultAsync
        await FluentActions.Invoking(() => books.Statements(db, silent.Object)
                .EmailCustomerStatementAsync(a, new EmailStatementRequest(), new StatementActor(1, "T")))
            .Should().ThrowAsync<ConflictException>();
        (await db.StatementLogs.AsNoTracking().SingleAsync()).EmailStatus.Should().Be("Failed");
    }

    [Fact]
    public async Task REV_17_an_application_reversed_after_the_day_counts_once_as_of_that_day()
    {
        var books = new ArApBooks();
        var acme  = books.Partner("Acme");
        var inv   = await books.SaleAsync(acme, Today.AddDays(-30), 1000m);
        var memo  = await books.CreditNoteAsync(acme, Today.AddDays(-20), 300m);
        await books.ApplyCreditAsync(memo, inv, 200m, Today.AddDays(-10));

        // Reversed on −3: both documents' CreditedAmount go back, the row keeps its ReversedAt.
        await using (var w = books.Db())
        {
            var row = await w.CustomerCreditApplications.SingleAsync();
            row.ReversedAt = Today.AddDays(-3);
            var n = await w.SalesInvoices.SingleAsync(i => i.Id == memo.Id);
            var i = await w.SalesInvoices.SingleAsync(x => x.Id == inv.Id);
            n.CreditedAmount -= 200m; i.CreditedAmount -= 200m; i.BalanceDue += 200m;
            await w.SaveChangesAsync();
        }

        await using var db = books.Db();
        var svc = books.Reports(db);

        var asOf = (await svc.GetCustomerOpenItemsAsync(acme, new OpenItemsFilter { AsOfDate = D(Today.AddDays(-5)), PageSize = 200 })).Data;
        asOf.Single(x => x.DocumentUuid == inv.UUID).OpenAmount.Should().Be(800m);
        asOf.Single(x => x.DocumentUuid == memo.UUID).OpenAmount.Should().Be(-100m);
        (await svc.GetCustomerBalanceAsync(acme, D(Today.AddDays(-5)))).NetBalance.Should().Be(700m);

        var now = (await svc.GetCustomerOpenItemsAsync(acme, new OpenItemsFilter { PageSize = 200 })).Data;
        now.Single(x => x.DocumentUuid == inv.UUID).OpenAmount.Should().Be(1000m);
        now.Single(x => x.DocumentUuid == memo.UUID).OpenAmount.Should().Be(-300m);
    }

    [Fact]
    public async Task REV_18_DSO_converts_unlocked_foreign_documents_top_and_bottom_at_the_same_current_rate()
    {
        var books = new ArApBooks();
        var acme  = books.Partner("Acme");
        await books.SaleAsync(acme, Today.AddDays(-10), 10m, currency: "EUR");       // no locked rate
        await books.SaleAsync(acme, Today.AddDays(-10), 1000m);                      // base

        var pkr = Guid.NewGuid(); var eur = Guid.NewGuid();
        var lookup = new Mock<IOrgCurrencyLookup>();
        lookup.Setup(l => l.GetByCodeAsync(It.IsAny<Guid>(), "EUR", It.IsAny<CancellationToken>()))
              .ReturnsAsync(new OrgCurrencyInfo(eur, "EUR", "Euro", "€", 2, 0.01m, "before", true, 2));
        lookup.Setup(l => l.GetByCodeAsync(It.IsAny<Guid>(), "PKR", It.IsAny<CancellationToken>()))
              .ReturnsAsync(new OrgCurrencyInfo(pkr, "PKR", "Rupee", "Rs", 2, 0.01m, "before", true, 1));
        lookup.Setup(l => l.GetAsync(It.IsAny<Guid>(), pkr, It.IsAny<CancellationToken>()))
              .ReturnsAsync(new OrgCurrencyInfo(pkr, "PKR", "Rupee", "Rs", 2, 0.01m, "before", true, 1));
        var currency = new Mock<ICurrencyService>();
        currency.Setup(c => c.GetBaseCurrencyIdAsync(It.IsAny<Guid>(), It.IsAny<TransactionDomain>(), It.IsAny<CancellationToken>())).ReturnsAsync(pkr);
        currency.Setup(c => c.ConvertAsync(It.IsAny<Guid>(), It.IsAny<decimal>(), eur, pkr, It.IsAny<DateOnly>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid _, decimal amount, Guid f, Guid t, DateOnly d, CancellationToken _) =>
                    new CurrencyConversionResult(amount, f, "EUR", amount * 300m, t, "PKR", 300m, d));

        await using var db = books.Db();
        var svc = new ArApReportingService(db, books.NameLookup(), currency.Object, lookup.Object, clock: books.Clock);
        var dso = await svc.GetDsoAsync(D(Today), 30);

        dso.Balance.Should().Be(4000m);           // 10 × 300 + 1000
        dso.PeriodAmount.Should().Be(4000m);      // the same rule, not 10 × 1 + 1000
        dso.Days.Should().Be(30m);

        var k = await svc.GetArSummaryAsync(D(Today), 30);
        k.InvoicedInPeriod.Should().Be(4000m);
    }

    [Fact]
    public async Task REV_18_DPO_and_collections_use_the_rule_too()
    {
        var books  = new ArApBooks();
        var vendor = books.Partner("Steel Co");
        var acme   = books.Partner("Acme");
        await books.BillAsync(vendor, Today.AddDays(-5), 100m, rate: 2m, currency: "USD");    // locked
        await books.BillAsync(vendor, Today.AddDays(-5), 50m);                                 // base, unlocked
        var inv = await books.SaleAsync(acme, Today.AddDays(-5), 400m);
        await books.ReceiveAsync(acme, Today.AddDays(-1), 100m, [(inv, 100m)]);

        await using var db = books.Db();
        var svc = books.Reports(db);
        (await svc.GetDpoAsync(D(Today), 10)).PeriodAmount.Should().Be(250m);
        (await svc.GetArSummaryAsync(D(Today), 10)).CollectedInPeriod.Should().Be(100m);
    }
}
