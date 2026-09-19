using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Repositories;
using SMS.Modules.Finance.Services;
using SMS.Modules.Warehouse.Data;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Finance.Tests;

// Decision G10 — a payable raised by another module for something no purchase order sits behind.
// Until this existed, an approved carrier invoice reached no ledger at all.
public class SupplierInvoicePosterTests
{
    private const int User = 42;

    private static readonly DateTime T0 = new(2026, 9, 18, 9, 0, 0, DateTimeKind.Utc);

    private sealed record Harness(FinanceDbContext Db, SupplierInvoicePoster Poster);

    private static Harness NewHarness(FakeSupplierNameLookup? names = null)
    {
        var tenant = new StaticTenantContext();

        var finance = new FinanceDbContext(new DbContextOptionsBuilder<FinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);

        var demand = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);

        var warehouse = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);

        var repo = new InvoiceRepository(
            finance, demand, warehouse, new SupplierLedgerService(finance),
            names ?? new FakeSupplierNameLookup());

        return new Harness(finance, new SupplierInvoicePoster(finance, repo));
    }

    private static SupplierInvoicePosting Posting(
        Guid? supplier = null, Guid? source = null, decimal subtotal = 1260m,
        IReadOnlyList<SupplierInvoiceLine>? lines = null) =>
        new(
            SupplierId:        supplier ?? Guid.NewGuid(),
            SupplierInvoiceNo: "CINV-9001",
            InvoiceDate:       T0.Date,
            DueDate:           T0.Date.AddDays(30),
            Currency:          "PKR",
            Subtotal:          subtotal,
            TaxAmount:         0m,
            SourceType:        "CARRIER_INVOICE",
            SourceUuid:        source ?? Guid.NewGuid(),
            Notes:             "Freight, per carrier invoice CINV-9001.",
            Lines:             lines);

    // ── Raising one ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_payable_with_no_purchase_order_can_be_raised()
    {
        var h = NewHarness();

        var result = await h.Poster.PostAsync(Posting(), User);

        result.AlreadyPosted.Should().BeFalse();
        result.InvoiceNumber.Should().NotBeNullOrWhiteSpace();

        var invoice = await h.Db.Invoices.SingleAsync(i => i.UUID == result.InvoiceUuid);

        invoice.PoUuid.Should().BeNull("a freight bill has no purchase order, and none was invented");
        invoice.PoNumber.Should().BeNull();
        invoice.TotalAmount.Should().Be(1260m);
    }

    [Fact]
    public async Task It_takes_the_suppliers_name_from_suppliers_when_there_is_no_purchase_order()
    {
        var supplier = Guid.NewGuid();
        var h = NewHarness(new FakeSupplierNameLookup().Knows(supplier, "Beta Road Logistics"));

        var result = await h.Poster.PostAsync(Posting(supplier: supplier), User);

        (await h.Db.Invoices.SingleAsync(i => i.UUID == result.InvoiceUuid))
            .SupplierName.Should().Be("Beta Road Logistics");
    }

    [Fact]
    public async Task A_supplier_that_does_not_exist_is_refused()
    {
        var h = NewHarness(new FakeSupplierNameLookup { KnowsNobody = true });

        var post = async () => await h.Poster.PostAsync(Posting(), User);

        await post.Should().ThrowAsync<BadRequestException>().WithMessage("*name nobody to pay*");
    }

    [Fact]
    public async Task It_gets_its_own_trace_rather_than_borrowing_an_unrelated_ones()
    {
        var h = NewHarness();

        var first  = await h.Poster.PostAsync(Posting(), User);
        var second = await h.Poster.PostAsync(Posting(), User);

        var a = await h.Db.Invoices.SingleAsync(i => i.UUID == first.InvoiceUuid);
        var b = await h.Db.Invoices.SingleAsync(i => i.UUID == second.InvoiceUuid);

        a.TraceId.Should().NotBeEmpty();
        a.TraceId.Should().NotBe(b.TraceId);
    }

    [Fact]
    public async Task It_stays_unapproved_so_finance_still_decides_whether_to_pay_it()
    {
        var h = NewHarness();

        var result = await h.Poster.PostAsync(Posting(), User);
        var invoice = await h.Db.Invoices.SingleAsync(i => i.UUID == result.InvoiceUuid);

        invoice.MatchStatus.Should().Be("Pending",
            "Logistics decided the carrier's charge is right; Finance decides whether to pay it");
        invoice.PaymentStatus.Should().Be("Unpaid");
        invoice.ApprovedAt.Should().BeNull();
    }

    [Fact]
    public async Task Nothing_to_match_against_is_a_variance_of_nothing_not_of_everything()
    {
        var h = NewHarness();

        var result = await h.Poster.PostAsync(Posting(), User);
        var invoice = await h.Db.Invoices.SingleAsync(i => i.UUID == result.InvoiceUuid);

        invoice.VarianceAmount.Should().Be(0m,
            "with no purchase order there is nothing for the bill to differ from");
        invoice.MatchedPoValue.Should().Be(0m);
    }

    // ── Lines ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Lines_are_carried_across_without_a_purchase_order_line()
    {
        var h = NewHarness();

        var result = await h.Poster.PostAsync(Posting(subtotal: 1260m, lines:
        [
            new SupplierInvoiceLine("Base carriage", 1000m),
            new SupplierInvoiceLine("Fuel surcharge", 260m)
        ]), User);

        var invoice = await h.Db.Invoices
            .Include(i => i.Lines)
            .SingleAsync(i => i.UUID == result.InvoiceUuid);

        invoice.Lines.Should().HaveCount(2);
        invoice.Lines.Should().OnlyContain(l => l.PoLineUuid == null);
        invoice.Lines.Sum(l => l.LineTotal).Should().Be(1260m);
    }

    [Fact]
    public async Task Lines_that_do_not_add_up_to_the_invoice_are_refused()
    {
        var h = NewHarness();

        var post = async () => await h.Poster.PostAsync(Posting(subtotal: 1260m, lines:
        [
            new SupplierInvoiceLine("Base carriage", 1000m)
        ]), User);

        await post.Should().ThrowAsync<BadRequestException>()
            .WithMessage("*hide the difference*");
    }

    [Fact]
    public async Task A_credit_line_is_allowed_as_long_as_the_whole_still_adds_up()
    {
        var h = NewHarness();

        var result = await h.Poster.PostAsync(Posting(subtotal: 900m, lines:
        [
            new SupplierInvoiceLine("Base carriage", 1000m),
            new SupplierInvoiceLine("Agreed credit", -100m)
        ]), User);

        (await h.Db.Invoices.SingleAsync(i => i.UUID == result.InvoiceUuid))
            .TotalAmount.Should().Be(900m);
    }

    // ── Posting it twice ──────────────────────────────────────────────────────

    [Fact]
    public async Task The_same_source_posted_twice_raises_one_payable()
    {
        var h = NewHarness();
        var source = Guid.NewGuid();

        var first  = await h.Poster.PostAsync(Posting(source: source), User);
        var second = await h.Poster.PostAsync(Posting(source: source), User);

        second.AlreadyPosted.Should().BeTrue(
            "paying a carrier twice looks exactly as legitimate as paying it once");
        second.InvoiceUuid.Should().Be(first.InvoiceUuid);
        second.InvoiceNumber.Should().Be(first.InvoiceNumber);

        (await h.Db.Invoices.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Two_different_sources_raise_two_payables()
    {
        var h = NewHarness();

        await h.Poster.PostAsync(Posting(), User);
        await h.Poster.PostAsync(Posting(), User);

        (await h.Db.Invoices.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task A_payable_records_what_produced_it()
    {
        var h = NewHarness();
        var source = Guid.NewGuid();

        var result = await h.Poster.PostAsync(Posting(source: source), User);
        var invoice = await h.Db.Invoices.SingleAsync(i => i.UUID == result.InvoiceUuid);

        invoice.SourceType.Should().Be("CARRIER_INVOICE");
        invoice.SourceUuid.Should().Be(source);
    }

    [Fact]
    public async Task A_posting_that_cannot_say_what_produced_it_is_refused()
    {
        var h = NewHarness();

        var noType = async () => await h.Poster.PostAsync(
            Posting() with { SourceType = "  " }, User);

        var noId = async () => await h.Poster.PostAsync(
            Posting() with { SourceUuid = Guid.Empty }, User);

        await noType.Should().ThrowAsync<BadRequestException>().WithMessage("*already been posted*");
        await noId.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task A_payable_of_nothing_is_refused()
    {
        var h = NewHarness();

        var post = async () => await h.Poster.PostAsync(Posting(subtotal: 0m), User);

        await post.Should().ThrowAsync<BadRequestException>()
            .WithMessage("*greater than zero*");
    }
}
