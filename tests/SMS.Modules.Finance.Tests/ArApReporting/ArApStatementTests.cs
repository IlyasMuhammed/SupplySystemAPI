using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.ArAp.Reporting;
using SMS.Modules.Finance.Domain;
using SMS.Shared.Exceptions;
using UglyToad.PdfPig;
using Xunit;

namespace SMS.Modules.Finance.Tests.ArApReporting;

/// <summary>A48 RPT — TS-ST-001..007, BR-ST-001..005, per-currency sections, the statement log, vendor reconciliation.</summary>
public class ArApStatementTests
{
    private static readonly DateTime Today = ArApBooks.Today;
    private static readonly StatementActor Actor = new(7, "Fatima Accounts");
    private static DateOnly D(DateTime d) => DateOnly.FromDateTime(d);

    [Fact]
    public async Task TS_ST_001_activity_statement_shows_every_transaction_with_a_running_balance()
    {
        var books = new ArApBooks();
        var acme  = books.Partner("Acme", "ar@acme.test");
        var day   = new DateTime(2026, 10, 1);
        var invoices = new List<SalesInvoice>();
        for (var i = 0; i < 6; i++) invoices.Add(await books.SaleAsync(acme, day.AddDays(i), 100m * (i + 1)));
        for (var i = 0; i < 4; i++) await books.ReceiveAsync(acme, day.AddDays(10 + i), 50m, [(invoices[i], 50m)]);

        await using var db = books.Db();
        var output = await books.Statements(db).GenerateCustomerStatementAsync(acme,
            new StatementRequest { From = D(day), To = D(Today) }, Actor);

        var s = output.Statement;
        output.Pdf.Should().BeNull();
        s.Type.Should().Be("Activity");
        var section = s.Sections.Should().ContainSingle().Subject;
        section.Lines.Should().HaveCount(10);
        section.OpeningBalance.Should().Be(0m);
        section.TotalDebits.Should().Be(2100m);
        section.TotalCredits.Should().Be(200m);
        section.ClosingBalance.Should().Be(1900m);                                   // BR-ST-004
        section.Lines.Select(l => l.RunningBalance).Should().Equal(100m, 300m, 600m, 1000m, 1500m, 2100m, 2050m, 2000m, 1950m, 1900m);
        section.Lines[0].Status.Should().Be("Partially Paid");
        section.Lines[5].Status.Should().Be("Open");
        section.Lines[6].Status.Should().Be("Applied");
        section.Lines[0].DueDate.Should().Be(D(day.AddDays(30)));
        s.ClosingBalanceBase.Should().Be(1900m);
        s.PartnerName.Should().Be("Acme");
        s.PartnerEmail.Should().Be("ar@acme.test");
        s.CompanyName.Should().Be("Acme Supplies");
        section.Aging.Should().NotBeNull();
        section.Aging!.Sum().Should().Be(1900m);
    }

    [Fact]
    public async Task TS_ST_002_open_items_statement_excludes_paid_invoices()
    {
        var books = new ArApBooks();
        var acme  = books.Partner("Acme");
        var paid  = await books.SaleAsync(acme, Today.AddDays(-30), 400m);
        var part  = await books.SaleAsync(acme, Today.AddDays(-20), 300m);
        await books.SaleAsync(acme, Today.AddDays(-10), 200m);
        await books.ReceiveAsync(acme, Today.AddDays(-5), 400m, [(paid, 400m)]);
        await books.ReceiveAsync(acme, Today.AddDays(-4), 150m, [(part, 100m)]);    // 50 on account

        await using var db = books.Db();
        var s = (await books.Statements(db).GenerateCustomerStatementAsync(acme,
            new StatementRequest { Type = "openitems", To = D(Today) }, Actor)).Statement;

        s.Type.Should().Be("OpenItems");
        var section = s.Sections.Single();
        section.Lines.Select(l => l.EntryNumber).Should().NotContain(paid.InvoiceNumber);
        section.Lines.Should().HaveCount(3);                         // part (200 open), the 200 one, the 50 credit
        section.TotalDebits.Should().Be(400m);
        section.TotalCredits.Should().Be(50m);
        section.ClosingBalance.Should().Be(350m);
        section.Lines.Last().RunningBalance.Should().Be(350m);
        section.Lines.Single(l => l.Credit is not null).Status.Should().Be("Partially Applied");
    }

    [Fact]
    public async Task TS_ST_003_opening_balance_is_everything_before_from()
    {
        var books = new ArApBooks();
        var acme  = books.Partner("Acme");
        var old   = await books.SaleAsync(acme, new DateTime(2026, 8, 5), 1000m);
        await books.ReceiveAsync(acme, new DateTime(2026, 9, 10), 250m, [(old, 250m)]);
        await books.SaleAsync(acme, new DateTime(2026, 10, 3), 500m);

        await using var db = books.Db();
        var s = (await books.Statements(db).GenerateCustomerStatementAsync(acme,
            new StatementRequest { From = new DateOnly(2026, 10, 1), To = new DateOnly(2026, 10, 31) }, Actor)).Statement;

        var section = s.Sections.Single();
        section.OpeningBalance.Should().Be(750m);
        section.Lines.Should().ContainSingle();
        section.ClosingBalance.Should().Be(1250m);
        s.OpeningBalanceBase.Should().Be(750m);
    }

    [Fact]
    public async Task Default_period_is_the_month_of_to_and_bad_input_is_400()
    {
        var books = new ArApBooks();
        var acme  = books.Partner("Acme");
        await books.SaleAsync(acme, Today.AddDays(-2), 10m);

        await using var db = books.Db();
        var svc = books.Statements(db);
        var s = (await svc.GenerateCustomerStatementAsync(acme, new StatementRequest(), Actor)).Statement;
        s.FromDate.Should().Be(new DateOnly(2026, 10, 1));
        s.ToDate.Should().Be(D(Today));

        await FluentActions.Invoking(() => svc.GenerateCustomerStatementAsync(acme, new StatementRequest { Type = "Summary" }, Actor))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Unknown type 'Summary'. Allowed: Activity, OpenItems.");
        await FluentActions.Invoking(() => svc.GenerateCustomerStatementAsync(acme,
                new StatementRequest { From = D(Today), To = D(Today.AddDays(-1)) }, Actor))
            .Should().ThrowAsync<BadRequestException>();
        await FluentActions.Invoking(() => svc.GenerateCustomerStatementAsync(Guid.NewGuid(), new StatementRequest(), Actor))
            .Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Each_currency_is_its_own_section()
    {
        var books = new ArApBooks();
        var acme  = books.Partner("Acme");
        await books.SaleAsync(acme, Today.AddDays(-5), 100m, currency: "USD", rate: 280m);
        await books.SaleAsync(acme, Today.AddDays(-5), 1000m);

        await using var db = books.Db();
        var svc = books.Statements(db);
        var s = (await svc.GenerateCustomerStatementAsync(acme, new StatementRequest { From = new DateOnly(2026, 10, 1) }, Actor)).Statement;
        s.Sections.Select(x => (x.CurrencyCode, x.ClosingBalance)).Should().Equal(("PKR", 1000m), ("USD", 100m));
        s.ClosingBalanceBase.Should().Be(29000m);

        var log = await db.StatementLogs.AsNoTracking().SingleAsync();
        log.CurrencyCode.Should().BeNull();                 // several sections → base figures
        log.ClosingBalance.Should().Be(29000m);

        var usd = (await svc.GenerateCustomerStatementAsync(acme, new StatementRequest { CurrencyCode = "USD" }, Actor)).Statement;
        usd.Sections.Should().ContainSingle(x => x.CurrencyCode == "USD");
    }

    [Fact]
    public async Task TS_ST_004_statement_pdf_is_generated_and_readable()
    {
        var books = new ArApBooks();
        var acme  = books.Partner("Acme");
        await books.SaleAsync(acme, Today.AddDays(-40), 4321.50m);

        await using var db = books.Db();
        var output = await books.Statements(db).GenerateCustomerStatementAsync(acme, new StatementRequest { Format = "pdf",
            From = D(Today.AddDays(-60)) }, Actor);

        output.Pdf.Should().NotBeNull();
        output.FileName.Should().EndWith(".pdf");
        using var pdf = PdfDocument.Open(output.Pdf!);
        var text = string.Join(" ", pdf.GetPages().Select(p => p.Text));
        text.Should().Contain("STATEMENT OF ACCOUNT").And.Contain("Acme").And.Contain("4,321.50").And.Contain("Acme Supplies");

        (await db.StatementLogs.AsNoTracking().SingleAsync()).Format.Should().Be("PDF");
    }

    [Fact]
    public async Task Every_generation_writes_a_log_row()
    {
        var books = new ArApBooks();
        var acme  = books.Partner("Acme");
        await books.SaleAsync(acme, Today.AddDays(-3), 75m);

        await using var db = books.Db();
        var svc = books.Statements(db);
        var s = (await svc.GenerateCustomerStatementAsync(acme, new StatementRequest(), Actor)).Statement;

        var log = await db.StatementLogs.AsNoTracking().SingleAsync();
        log.Uuid.Should().Be(s.StatementId!.Value);
        log.PartnerUuid.Should().Be(acme);
        log.PartnerRole.Should().Be("Customer");
        log.StatementType.Should().Be("Activity");
        log.CurrencyCode.Should().Be("PKR");
        log.ClosingBalance.Should().Be(75m);
        log.LineCount.Should().Be(1);
        log.Format.Should().Be("JSON");
        log.EmailStatus.Should().Be("NotSent");
        log.GeneratedBy.Should().Be(7);
        log.GeneratedByName.Should().Be("Fatima Accounts");
        log.OrganizationId.Should().Be(books.Org);

        var page = await svc.GetLogAsync(new StatementLogFilter { Role = "customer", PartnerUuid = acme });
        page.Data.Should().ContainSingle(x => x.Uuid == log.Uuid);
        (await svc.GetLogAsync(new StatementLogFilter { Role = "Vendor" })).Data.Should().BeEmpty();
        await FluentActions.Invoking(() => svc.GetLogAsync(new StatementLogFilter()))
            .Should().ThrowAsync<BadRequestException>().WithMessage("role is required: Customer or Vendor.");
    }

    [Fact]
    public async Task TS_ST_005_batch_only_for_customers_at_or_above_the_minimum_balance()
    {
        var books = new ArApBooks();
        var big   = books.Partner("Big", "big@x.test");
        var small = books.Partner("Small", "small@x.test");
        var none  = books.Partner("NoMail");
        var paid  = books.Partner("Paid", "paid@x.test");
        await books.SaleAsync(big, Today.AddDays(-20), 5000m);
        await books.SaleAsync(small, Today.AddDays(-20), 999.99m);
        await books.SaleAsync(none, Today.AddDays(-20), 1000m);
        var p = await books.SaleAsync(paid, Today.AddDays(-20), 3000m);
        await books.ReceiveAsync(paid, Today.AddDays(-2), 3000m, [(p, 3000m)]);

        await using var db = books.Db();
        var result = await books.Statements(db).BatchCustomerStatementsAsync(
            new BatchStatementRequest { MinimumBalance = 1000m, SendEmail = true }, Actor);

        result.Considered.Should().Be(4);
        result.Skipped.Should().Be(2);
        result.Generated.Should().Be(2);
        result.Emailed.Should().Be(1);
        result.NoEmail.Should().Be(1);
        result.Items.Select(i => i.PartnerName).Should().BeEquivalentTo(["Big", "NoMail"]);
        books.Sent.Should().ContainSingle(m => m.To == "big@x.test");

        var logs = await db.StatementLogs.AsNoTracking().ToListAsync();
        logs.Should().HaveCount(2);
        logs.Select(l => l.BatchUuid).Distinct().Should().ContainSingle().Which.Should().NotBeNull();
        logs.Single(l => l.PartnerUuid == big).EmailStatus.Should().Be("Sent");
        logs.Single(l => l.PartnerUuid == none).EmailStatus.Should().Be("NotSent");
    }

    [Fact]
    public async Task TS_ST_006_email_sends_the_pdf_and_logs_it()
    {
        var books = new ArApBooks();
        var acme  = books.Partner("Acme", "ar@acme.test");
        await books.SaleAsync(acme, Today.AddDays(-10), 640m);

        await using var db = books.Db();
        var result = await books.Statements(db).EmailCustomerStatementAsync(acme, new EmailStatementRequest(), Actor);

        result.EmailedTo.Should().Be("ar@acme.test");
        result.ClosingBalanceBase.Should().Be(640m);
        var mail = books.Sent.Single();
        mail.Subject.Should().Contain("Statement of account");
        mail.Attachments!.Single().ContentType.Should().Be("application/pdf");
        System.Text.Encoding.ASCII.GetString(mail.Attachments!.Single().Content, 0, 4).Should().Be("%PDF");

        var log = await db.StatementLogs.AsNoTracking().SingleAsync();
        log.EmailStatus.Should().Be("Sent");
        log.EmailedTo.Should().Be("ar@acme.test");
        log.EmailedAt.Should().NotBeNull();
        log.Uuid.Should().Be(result.StatementId!.Value);
    }

    [Fact]
    public void FE_5_the_optional_message_is_in_the_email_body_html_encoded()
    {
        var s = new StatementResult { PartnerName = "Acme", CompanyName = "Acme Supplies", FromDate = new DateOnly(2026, 10, 1), ToDate = new DateOnly(2026, 10, 25) };
        StatementService.EmailBody(s, "Please pay <today>").Should().Contain("Please pay &lt;today&gt;");
        StatementService.EmailBody(s, null).Should().NotContain("&lt;");
    }

    [Fact]
    public async Task Email_override_address_missing_address_and_failure()
    {
        var books = new ArApBooks();
        var acme  = books.Partner("Acme");
        await books.SaleAsync(acme, Today.AddDays(-10), 640m);

        await using var db = books.Db();
        var svc = books.Statements(db);

        await FluentActions.Invoking(() => svc.EmailCustomerStatementAsync(acme, new EmailStatementRequest(), Actor))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Acme has no email address on file; give one in emailTo.");

        (await svc.EmailCustomerStatementAsync(acme, new EmailStatementRequest { EmailTo = "boss@acme.test" }, Actor))
            .EmailedTo.Should().Be("boss@acme.test");

        books.FailEmail = true;
        await FluentActions.Invoking(() => svc.EmailCustomerStatementAsync(acme, new EmailStatementRequest { EmailTo = "boss@acme.test" }, Actor))
            .Should().ThrowAsync<ConflictException>().WithMessage("The statement could not be emailed: SMTP is down");
        var failed = await db.StatementLogs.AsNoTracking().OrderByDescending(l => l.Id).FirstAsync();
        failed.EmailStatus.Should().Be("Failed");
        failed.EmailError.Should().Be("SMTP is down");
    }

    [Fact]
    public async Task TS_ST_007_vendor_statement_and_reconciliation_differences()
    {
        var books  = new ArApBooks();
        var vendor = books.Partner("Steel Co");
        var a = await books.BillAsync(vendor, Today.AddDays(-40), 1000m, supplierInvoiceNo: "SC-101");
        var b = await books.BillAsync(vendor, Today.AddDays(-30), 500m, supplierInvoiceNo: "SC-102");
        await books.BillAsync(vendor, Today.AddDays(-20), 300m, supplierInvoiceNo: "SC-103");
        await books.PayAsync(vendor, Today.AddDays(-10), [(a, 1000m), (b, 100m)]);

        await using var db = books.Db();
        var svc = books.Statements(db);

        var s = (await svc.GenerateVendorStatementAsync(vendor, new StatementRequest { From = D(Today.AddDays(-60)) }, Actor)).Statement;
        s.PartnerType.Should().Be("Vendor");
        var section = s.Sections.Single();
        section.TotalDebits.Should().Be(1800m);
        section.TotalCredits.Should().Be(1100m);
        section.ClosingBalance.Should().Be(700m);
        section.Lines.Select(l => l.RunningBalance).Should().Equal(1000m, 1500m, 1800m, 700m);
        section.Lines[0].Status.Should().Be("Paid");
        section.Lines[1].Status.Should().Be("Partially Paid");

        // The vendor says: SC-102 is 450 open (we say 400), SC-103 is not on their list, and SC-104 (1200) is.
        var rec = await svc.ReconcileAsync(vendor, new VendorReconciliationRequest
        {
            VendorClaimedBalance = 1650m,
            VendorItems = [new() { Reference = "sc-102", Amount = 450m }, new() { Reference = "SC-104", Amount = 1200m }]
        });

        rec.SmsBalance.Should().Be(700m);
        rec.Difference.Should().Be(-950m);
        rec.IsReconciled.Should().BeFalse();
        rec.MatchedCount.Should().Be(1);
        rec.Differences.Should().HaveCount(3);
        rec.Differences.Should().ContainSingle(d => d.Type == ReconciliationDifferenceTypes.AmountDifference
            && d.Reference == "SC-102" && d.SmsAmount == 400m && d.VendorAmount == 450m && d.Difference == -50m);
        rec.Differences.Should().ContainSingle(d => d.Type == ReconciliationDifferenceTypes.InVendorNotSms
            && d.Reference == "SC-104" && d.VendorAmount == 1200m);
        rec.Differences.Should().ContainSingle(d => d.Type == ReconciliationDifferenceTypes.InSmsNotVendor
            && d.Reference == "SC-103" && d.SmsAmount == 300m);
    }

    [Fact]
    public async Task Vendor_reconciliation_is_reconciled_when_nothing_differs()
    {
        var books  = new ArApBooks();
        var vendor = books.Partner("Steel Co");
        await books.BillAsync(vendor, Today.AddDays(-20), 300m, supplierInvoiceNo: "SC-1");
        var inv = await books.BillAsync(vendor, Today.AddDays(-10), 200m);
        await books.AdvanceAsync(vendor, Today.AddDays(-3), 50m);

        await using var db = books.Db();
        var svc = books.Statements(db);

        // By balance only.
        (await svc.ReconcileAsync(vendor, new VendorReconciliationRequest { VendorClaimedBalance = 450m })).IsReconciled.Should().BeTrue();
        // By items: their own number, our number, and the advance as a negative item.
        var advanceNo = (await db.SupplierPayments.AsNoTracking().SingleAsync(p => p.PaymentType == "ADVANCE_PAYMENT")).PaymentNumber;
        var rec = await svc.ReconcileAsync(vendor, new VendorReconciliationRequest
        {
            VendorClaimedBalance = 450.004m,
            VendorItems =
            [
                new() { Reference = "SC-1", Amount = 300m },
                new() { Reference = inv.InvoiceNumber, Amount = 200m },
                new() { Reference = advanceNo, Amount = -50m }
            ]
        });
        rec.IsReconciled.Should().BeTrue();
        rec.MatchedCount.Should().Be(3);
        rec.Differences.Should().BeEmpty();

        await FluentActions.Invoking(() => svc.ReconcileAsync(vendor, new VendorReconciliationRequest { VendorItems = [new() { Reference = " " }] }))
            .Should().ThrowAsync<BadRequestException>();
    }
}
