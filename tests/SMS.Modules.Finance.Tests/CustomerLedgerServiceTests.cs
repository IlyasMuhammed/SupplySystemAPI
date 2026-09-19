using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>A29-P7-04/§10 — the receivables ledger writer: running balance, per-customer sequence, and
/// the promise not to save, so the caller's business change and the entry commit as one.</summary>
public class CustomerLedgerServiceTests
{
    private static readonly DateTime When = new(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);

    private static (FinanceDbContext Db, CustomerLedgerService Service) New(Guid? org = null, string? dbName = null)
    {
        var db = new FinanceDbContext(
            new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString()).Options,
            new StaticTenantContext { OrganizationId = org ?? Guid.NewGuid() });
        return (db, new CustomerLedgerService(db));
    }

    private static CustomerLedgerPosting Post(
        Guid partner, decimal debit = 0m, decimal credit = 0m, string type = "INVOICE", string number = "SINV-1") =>
        new(partner, type, "SalesInvoice", Guid.NewGuid(), number, debit, credit, "PKR", "narration", When, 7);

    [Fact]
    public async Task The_first_entry_starts_the_sequence_and_the_balance()
    {
        var (db, ledger) = New();
        var partner = Guid.NewGuid();

        var entry = await ledger.TrackEntryAsync(Post(partner, debit: 4000m));

        entry.SequenceNo.Should().Be(1);
        entry.RunningBalance.Should().Be(4000m);
        entry.DebitAmount.Should().Be(4000m);
        entry.CreditAmount.Should().Be(0m);
        entry.PartnerId.Should().Be(partner);
        entry.EntryType.Should().Be("INVOICE");
        entry.CreatedBy.Should().Be(7);
    }

    [Fact]
    public async Task Running_balance_is_previous_plus_debit_minus_credit_and_can_go_negative()
    {
        // §10. Invoice 4000, payment 2500, payment 2000 — the customer has overpaid by 500.
        var (db, ledger) = New();
        var partner = Guid.NewGuid();

        await ledger.TrackEntryAsync(Post(partner, debit: 4000m));
        await db.SaveChangesAsync();
        var part = await ledger.TrackEntryAsync(Post(partner, credit: 2500m, type: "PAYMENT"));
        await db.SaveChangesAsync();
        var over = await ledger.TrackEntryAsync(Post(partner, credit: 2000m, type: "PAYMENT"));
        await db.SaveChangesAsync();

        part.RunningBalance.Should().Be(1500m);
        over.RunningBalance.Should().Be(-500m);
        (await db.CustomerLedgerEntries.OrderBy(e => e.SequenceNo).Select(e => e.SequenceNo).ToListAsync()).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task It_tracks_the_entry_but_does_not_save_it_that_is_the_callers_unit_of_work()
    {
        var (db, ledger) = New();

        var entry = await ledger.TrackEntryAsync(Post(Guid.NewGuid(), debit: 100m));

        db.Entry(entry).State.Should().Be(EntityState.Added);
        (await db.CustomerLedgerEntries.AsNoTracking().CountAsync()).Should().Be(0, "nothing is committed until the caller saves");

        await db.SaveChangesAsync();
        (await db.CustomerLedgerEntries.AsNoTracking().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Two_entries_built_before_either_is_saved_chain_rather_than_collide()
    {
        var (db, ledger) = New();
        var partner = Guid.NewGuid();

        var first  = await ledger.TrackEntryAsync(Post(partner, debit: 300m));
        var second = await ledger.TrackEntryAsync(Post(partner, debit: 200m));

        second.SequenceNo.Should().Be(first.SequenceNo + 1);
        second.RunningBalance.Should().Be(500m);

        await db.SaveChangesAsync();
        (await db.CustomerLedgerEntries.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Each_customer_has_their_own_sequence_and_balance()
    {
        var (db, ledger) = New();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        await ledger.TrackEntryAsync(Post(a, debit: 100m));
        await ledger.TrackEntryAsync(Post(a, debit: 100m));
        var other = await ledger.TrackEntryAsync(Post(b, debit: 50m));

        other.SequenceNo.Should().Be(1);
        other.RunningBalance.Should().Be(50m);
    }

    [Fact]
    public async Task Another_organizations_entries_do_not_feed_this_ones_balance()
    {
        var dbName  = Guid.NewGuid().ToString();
        var partner = Guid.NewGuid();
        var (dbA, ledgerA) = New(Guid.NewGuid(), dbName);
        await ledgerA.TrackEntryAsync(Post(partner, debit: 999m));
        await dbA.SaveChangesAsync();

        var (_, ledgerB) = New(Guid.NewGuid(), dbName);
        var entry = await ledgerB.TrackEntryAsync(Post(partner, debit: 10m));

        entry.SequenceNo.Should().Be(1);
        entry.RunningBalance.Should().Be(10m);
    }

    [Theory]
    [InlineData("INVOICE")]
    [InlineData("PAYMENT")]
    [InlineData("CREDIT_NOTE")]
    [InlineData("DEBIT_NOTE")]
    [InlineData("ADVANCE")]
    [InlineData("REFUND")]
    [InlineData("OPENING_BAL")]
    public async Task Every_section_10_entry_type_is_accepted(string type)
    {
        var (_, ledger) = New();

        var act = async () => await ledger.TrackEntryAsync(Post(Guid.NewGuid(), debit: 1m, type: type));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task An_unknown_entry_type_is_refused()
    {
        var (_, ledger) = New();

        var act = async () => await ledger.TrackEntryAsync(Post(Guid.NewGuid(), debit: 1m, type: "WRITE_OFF"));

        (await act.Should().ThrowAsync<ArgumentException>()).WithMessage("*WRITE_OFF*").WithMessage("*INVOICE*");
    }

    [Theory]
    [InlineData(0, 0)]      // neither
    [InlineData(10, 10)]    // both
    [InlineData(-5, 0)]     // negative debit
    [InlineData(0, -5)]     // negative credit
    public async Task An_entry_is_exactly_one_of_a_debit_or_a_credit_and_never_negative(double debit, double credit)
    {
        var (_, ledger) = New();

        var act = async () => await ledger.TrackEntryAsync(Post(Guid.NewGuid(), (decimal)debit, (decimal)credit));

        await act.Should().ThrowAsync<ArgumentException>();
    }
}

/// <summary>A29-P7-04 §4.1/§4.2 — the invoice arithmetic that must agree with the sale order's own.</summary>
public class SalesInvoiceTotalsTests
{
    [Fact]
    public void A_line_total_is_qty_times_price_less_discount_plus_tax_rounded_to_two_places()
    {
        SalesInvoiceTotals.LineTotal(100m, 40m, 10m, 5m).Should().Be(3780m);
        SalesInvoiceTotals.LineTotal(3m, 33.33m, 0m, 7.5m).Should().Be(107.49m, "99.99 × 1.075 = 107.48925");
    }

    [Fact]
    public void Rounding_is_away_from_zero_like_the_order()
    {
        SalesInvoiceTotals.LineTotal(1m, 0.005m, 0m, 0m).Should().Be(0.01m);
        SalesInvoiceTotals.LineTotal(1m, 2.675m, 0m, 0m).Should().Be(2.68m, "midpoint goes up, not to even");
    }

    [Fact]
    public void The_header_is_each_component_summed_over_the_lines_and_rounded_once()
    {
        var (subtotal, discount, tax, grand) = SalesInvoiceTotals.Header(
        [
            (100m, 40m, 10m, 5m),     // 4000, −400, +180
            (3m, 33.33m, 0m, 7.5m)    // 99.99, 0, +7.49925
        ]);

        subtotal.Should().Be(4099.99m);
        discount.Should().Be(400m);
        tax.Should().Be(187.50m, "180 + 7.49925, rounded once");
        grand.Should().Be(3887.49m, "subtotal − discount + tax");
    }

    [Fact]
    public void An_empty_invoice_is_zero()
    {
        SalesInvoiceTotals.Header([]).Should().Be((0m, 0m, 0m, 0m));
    }
}
