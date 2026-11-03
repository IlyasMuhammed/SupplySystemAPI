using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.ArAp.Receivables;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Integration.Posting;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>A48 AR — C7 credit memos (TS-CM-001..003, D-6) over real invoices and the customer ledger.</summary>
public class A48CreditMemoTests
{
    private static CreateCreditMemoRequest Memo(Guid customer, Guid? invoice, decimal amount, bool? apply = null) => new()
    {
        CustomerUuid = customer, OriginalInvoiceUuid = invoice, Reason = "Price adjustment", ApplyToOriginal = apply,
        Lines = [new CreditMemoLineRequest { Description = "Allowance", Amount = amount }]
    };

    [Fact]
    public async Task TS_CM_001_a_memo_against_an_invoice_credits_the_ledger_and_posts_revenue_against_receivable()
    {
        var w = new A48ArWorld();
        var c = w.Customer();
        var inv = await w.Desk.InvoiceAsync(c, 1_000m);

        var memo = await w.InScopeAsync(s => s.Memos.CreateAsync(Memo(c, inv.Uuid, 200m, apply: false), w.Caller));

        memo.CreditNoteNumber.Should().StartWith("SCN-");
        memo.Should().Match<CreditMemoModel>(m => m.Total == 200m && m.Applied == 0m && m.Remaining == 200m
            && m.OriginalInvoiceUuid == inv.Uuid && m.SourceType == "AR_CREDIT_MEMO");

        var books = await w.Desk.BooksAsync(c);
        books.Ledger.Should().Contain(e => e.ReferenceId == memo.Uuid && e.EntryType == "CREDIT_NOTE" && e.CreditAmount == 200m);
        (await w.Desk.OwesAsync(c)).Should().Be(800m);
        books.Invoice(inv.Uuid).BalanceDue.Should().Be(1_000m, "not applied yet");

        await using var s = w.Scope();
        var note = await s.Db.SalesInvoices.Include(i => i.Lines).SingleAsync(i => i.UUID == memo.Uuid);
        var original = await s.Db.SalesInvoices.SingleAsync(i => i.UUID == inv.Uuid);
        var gl = SalesPostings.CreditNote(note, original, [], originalIsInvoice: true).Lines;
        gl.Sum(l => l.Debit).Should().Be(gl.Sum(l => l.Credit)).And.Be(200m);
        gl.Should().Contain(l => l.AccountRole == "DEFAULT_REVENUE" && l.Debit == 200m)
          .And.Contain(l => l.AccountRole == "DEFAULT_RECEIVABLE" && l.Credit == 200m);
    }

    [Fact]
    public async Task By_default_the_memo_is_applied_to_its_original_invoice()
    {
        var w = new A48ArWorld();
        var c = w.Customer();
        var inv = await w.Desk.InvoiceAsync(c, 1_000m);

        var memo = await w.InScopeAsync(s => s.Memos.CreateAsync(Memo(c, inv.Uuid, 1_000m), w.Caller));

        memo.Applied.Should().Be(1_000m);
        memo.Applications.Single().InvoiceUuid.Should().Be(inv.Uuid);
        var invoice = (await w.Desk.BooksAsync(c)).Invoice(inv.Uuid);
        invoice.Should().Match<SalesInvoice>(i => i.BalanceDue == 0m && i.CreditedAmount == 1_000m && i.Status == "PAID" && i.ReturnedAmount == 1_000m);
        (await w.Desk.OwesAsync(c)).Should().Be(0m);
    }

    [Fact]
    public async Task TS_CM_002_and_003_partial_application_reduces_the_residual_then_the_rest_applies_elsewhere()
    {
        var w = new A48ArWorld();
        var c = w.Customer();
        var first = await w.Desk.InvoiceAsync(c, 1_000m);
        var second = await w.Desk.InvoiceAsync(c, 300m);
        var memo = await w.InScopeAsync(s => s.Memos.CreateAsync(Memo(c, first.Uuid, 200m, apply: false), w.Caller));

        var after = await w.InScopeAsync(s => s.Memos.ApplyAsync(memo.Uuid,
            new ApplyCreditMemoRequest { Applications = [new() { InvoiceUuid = first.Uuid, Amount = 50m }] }, w.Caller));
        after.Remaining.Should().Be(150m);
        var books = await w.Desk.BooksAsync(c);
        books.Invoice(first.Uuid).Should().Match<SalesInvoice>(i => i.BalanceDue == 950m && i.Status == "PARTIALLY_PAID");

        await FluentActions.Awaiting(() => w.InScopeAsync(s => s.Memos.ApplyAsync(memo.Uuid,
                new ApplyCreditMemoRequest { Applications = [new() { InvoiceUuid = second.Uuid, Amount = 151m }] }, w.Caller)))
            .Should().ThrowAsync<BadRequestException>().WithMessage($"{memo.CreditNoteNumber} has only 150.00 left to apply.");

        after = await w.InScopeAsync(s => s.Memos.ApplyAsync(memo.Uuid,
            new ApplyCreditMemoRequest { Applications = [new() { InvoiceUuid = second.Uuid, Amount = 150m }] }, w.Caller));
        after.Remaining.Should().Be(0m);
        after.Applications.Select(a => a.Amount).Should().Equal(50m, 150m);
        (await w.Desk.BooksAsync(c)).Invoice(second.Uuid).BalanceDue.Should().Be(150m);

        // Applying moves credit between documents; the ledger was credited once, when the memo was raised.
        (await w.Desk.OwesAsync(c)).Should().Be(1_100m);
        (await w.InScopeAsync(s => s.Memos.OpenCreditsAsync(c, w.Caller))).Should().BeEmpty();
    }

    [Fact]
    public async Task Application_refusals()
    {
        var w = new A48ArWorld();
        var c = w.Customer("Acme Ltd");
        var other = w.Customer("Beta");
        var inv = await w.Desk.InvoiceAsync(c, 100m);
        var foreign = await w.Desk.InvoiceAsync(other, 100m);
        var memo = await w.InScopeAsync(s => s.Memos.CreateAsync(Memo(c, null, 500m), w.Caller));
        memo.Remaining.Should().Be(500m, "a standalone memo stays as open credit");

        await FluentActions.Awaiting(() => w.InScopeAsync(s => s.Memos.ApplyAsync(memo.Uuid,
                new ApplyCreditMemoRequest { Applications = [new() { InvoiceUuid = foreign.Uuid, Amount = 10m }] }, w.Caller)))
            .Should().ThrowAsync<BadRequestException>().WithMessage($"Invoice {foreign.Number} belongs to another customer.");
        await FluentActions.Awaiting(() => w.InScopeAsync(s => s.Memos.ApplyAsync(memo.Uuid,
                new ApplyCreditMemoRequest { Applications = [new() { InvoiceUuid = inv.Uuid, Amount = 101m }] }, w.Caller)))
            .Should().ThrowAsync<BadRequestException>().WithMessage($"Invoice {inv.Number} has only 100.00 open.");
        await FluentActions.Awaiting(() => w.InScopeAsync(s => s.Memos.ApplyAsync(inv.Uuid,
                new ApplyCreditMemoRequest { Applications = [new() { InvoiceUuid = inv.Uuid, Amount = 1m }] }, w.Caller)))
            .Should().ThrowAsync<ConflictException>().WithMessage($"{inv.Number} is not an issued credit note.");

        (await w.InScopeAsync(s => s.Memos.OpenCreditsAsync(c, w.Caller))).Single().Uuid.Should().Be(memo.Uuid);
    }

    [Fact]
    public async Task A_memo_cannot_credit_more_than_the_invoice()
    {
        var w = new A48ArWorld();
        var c = w.Customer();
        var inv = await w.Desk.InvoiceAsync(c, 100m);
        await w.InScopeAsync(s => s.Memos.CreateAsync(Memo(c, inv.Uuid, 60m), w.Caller));

        await FluentActions.Awaiting(() => w.InScopeAsync(s => s.Memos.CreateAsync(Memo(c, inv.Uuid, 41m), w.Caller)))
            .Should().ThrowAsync<BadRequestException>().WithMessage("*above its total of 100.00 (60.00 already credited).");
    }

    [Fact]
    public async Task Line_tax_is_carved_out_of_the_gross_amount_from_the_tax_code()
    {
        var w = new A48ArWorld();
        var c = w.Customer();
        var code = new TaxCode { OrganizationId = w.Org, Code = "GST17", Name = "GST 17%", RatePercent = 17m, Usage = TaxCodeUsages.Both };
        await w.InScopeAsync(async s => { s.Db.TaxCodes.Add(code); await s.Db.SaveChangesAsync(); return 0; });

        var req = Memo(c, null, 117m);
        req.Lines[0].TaxCodeUuid = code.Uuid;
        var memo = await w.InScopeAsync(s => s.Memos.CreateAsync(req, w.Caller));

        memo.TaxAmount.Should().Be(17m);
        memo.Subtotal.Should().Be(100m);
        memo.Lines.Single().TaxCode.Should().Be("GST17");
    }
}
