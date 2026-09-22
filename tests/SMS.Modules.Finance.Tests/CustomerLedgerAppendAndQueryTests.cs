using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>
/// A29-P7-06 §10 — appending to a customer's ledger with a running balance, safely under a race, and
/// reading it back a page at a time within a date range.
/// </summary>
public class CustomerLedgerAppendAndQueryTests
{
    private const int User = 42;
    private static readonly Guid Customer = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 9, 20, 10, 30, 0, DateTimeKind.Utc);

    private sealed class FakeClock : TimeProvider
    {
        public DateTime Value { get; set; } = Now;
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(Value, DateTimeKind.Utc));
    }

    private static (FinanceDbContext Db, CustomerLedgerService Service, Guid Org, string DbName) New(
        Guid? org = null, string? dbName = null, Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor? interceptor = null)
    {
        org    ??= Guid.NewGuid();
        dbName ??= Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(dbName);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        var db = new FinanceDbContext(options.Options, new StaticTenantContext { OrganizationId = org.Value });
        return (db, new CustomerLedgerService(db, new FakeClock()), org.Value, dbName);
    }

    private static CustomerLedgerReference Ref(string number = "SINV-1", string type = "SalesInvoice") => new(type, Guid.NewGuid(), number);

    private static Task<CustomerLedgerEntryModel> Debit(CustomerLedgerService s, decimal amount, Guid? partner = null, DateTime? date = null, string type = "INVOICE") =>
        s.AppendEntryAsync(partner ?? Customer, type, amount, 0m, Ref(), "PKR", User, entryDate: date);

    private static Task<CustomerLedgerEntryModel> Credit(CustomerLedgerService s, decimal amount, Guid? partner = null, DateTime? date = null, string type = "PAYMENT") =>
        s.AppendEntryAsync(partner ?? Customer, type, 0m, amount, Ref("CPAY-1", "CustomerPayment"), "PKR", User, entryDate: date);

    private static Task<List<CustomerLedgerEntry>> Stored(FinanceDbContext db) =>
        db.CustomerLedgerEntries.AsNoTracking().OrderBy(e => e.PartnerId).ThenBy(e => e.SequenceNo).ToListAsync();

    // ── AppendEntry ──────────────────────────────────────────────────────────

    [Fact]
    public async Task An_appended_entry_is_saved_with_its_sequence_and_running_balance()
    {
        var (db, ledger, org, _) = New();
        var reference = new CustomerLedgerReference("SalesInvoice", Guid.NewGuid(), "SINV-20260920-0001");

        var entry = await ledger.AppendEntryAsync(Customer, "INVOICE", 4000m, 0m, reference, "PKR", User,
            narration: "Invoice SINV-20260920-0001", entryDate: new DateTime(2026, 9, 19));

        entry.SequenceNo.Should().Be(1);
        entry.RunningBalance.Should().Be(4000m);
        entry.PartnerId.Should().Be(Customer);
        entry.EntryType.Should().Be("INVOICE");
        entry.DebitAmount.Should().Be(4000m);
        entry.CreditAmount.Should().Be(0m);
        entry.ReferenceType.Should().Be("SalesInvoice");
        entry.ReferenceId.Should().Be(reference.Id);
        entry.ReferenceNumber.Should().Be("SINV-20260920-0001");
        entry.CurrencyCode.Should().Be("PKR");
        entry.Narration.Should().Be("Invoice SINV-20260920-0001");
        entry.EntryDate.Should().Be(new DateTime(2026, 9, 19));
        entry.CreatedBy.Should().Be(User);
        entry.CreatedDate.Should().Be(Now);

        var stored = (await Stored(db)).Should().ContainSingle().Subject;
        stored.UUID.Should().Be(entry.Uuid, "unlike TrackEntry, append commits");
        stored.OrganizationId.Should().Be(org);
    }

    [Fact]
    public async Task The_entry_date_defaults_to_now()
    {
        var (_, ledger, _, _) = New();

        var entry = await Debit(ledger, 10m);

        entry.EntryDate.Should().Be(Now);
    }

    [Fact]
    public async Task Running_balance_is_previous_plus_debit_minus_credit()
    {
        // §10: 4,000 invoiced; 2,500 received; 500 debit note; 2,000 received (overpaying).
        var (_, ledger, _, _) = New();

        var invoice = await Debit(ledger, 4000m);
        var part    = await Credit(ledger, 2500m);
        var note    = await Debit(ledger, 500m, type: "DEBIT_NOTE");
        var over    = await Credit(ledger, 2500m);

        new[] { invoice, part, note, over }.Select(e => (e.SequenceNo, e.RunningBalance)).Should().Equal(
            (1, 4000m), (2, 1500m), (3, 2000m), (4, -500m));
    }

    [Fact]
    public async Task Each_customer_has_their_own_sequence_and_balance_and_each_organization_its_own_ledger()
    {
        var (dbA, a, _, dbName) = New();
        var other = Guid.NewGuid();

        await Debit(a, 100m);
        await Debit(a, 100m);
        var theirs = await Debit(a, 40m, other);
        theirs.SequenceNo.Should().Be(1);
        theirs.RunningBalance.Should().Be(40m);

        var (_, b, _, _) = New(dbName: dbName);
        var strangers = await Debit(b, 7m);
        strangers.SequenceNo.Should().Be(1, "another organization's entries do not feed this one's");
        strangers.RunningBalance.Should().Be(7m);

        (await dbA.CustomerLedgerEntries.IgnoreQueryFilters().CountAsync()).Should().Be(4);
    }

    [Fact]
    public async Task A_correction_is_a_new_offsetting_entry_and_never_touches_the_original()
    {
        var (db, ledger, _, _) = New();
        var original = await Debit(ledger, 750m);
        var before = (await Stored(db)).Single();

        await Credit(ledger, 750m, type: "CREDIT_NOTE");

        var rows = await Stored(db);
        rows.Should().HaveCount(2);
        rows[0].Should().BeEquivalentTo(before, "the first entry is exactly as it was posted");
        rows[1].RunningBalance.Should().Be(0m, "the correction nets the account to nothing");
        original.RunningBalance.Should().Be(750m);
    }

    [Fact]
    public async Task Append_commits_whatever_else_the_caller_has_tracked_on_the_same_context()
    {
        // The supplier ledger behaves the same way, and callers rely on it to make their own write
        // atomic with the ledger entry — so it is a contract, not an accident.
        var (db, ledger, _, _) = New();
        db.CustomerPayments.Add(new CustomerPayment
        {
            UUID = Guid.NewGuid(), PartnerId = Customer, PartnerName = "Acme", PaymentNumber = "CPAY-20260920-0001",
            PaymentDate = Now.Date, Amount = 10m, PaymentMethod = "CASH", CreatedBy = 1, CreatedDate = Now
        });

        await Credit(ledger, 10m);

        (await db.CustomerPayments.AsNoTracking().CountAsync()).Should().Be(1);
        (await Stored(db)).Should().ContainSingle();
    }

    [Fact]
    public async Task Losing_the_race_for_the_next_sequence_retries_against_the_fresh_last_entry()
    {
        // A rival appends this customer's entry 1 (a 500 debit) between our read and our save.
        var dbName = Guid.NewGuid().ToString();
        var org    = Guid.NewGuid();
        var (_, rival, _, _) = New(org, dbName);

        var race = new LoseTheRaceOnce(db => db.ChangeTracker.Entries<CustomerLedgerEntry>().Any(e => e.State == EntityState.Added),
            async () => await Debit(rival, 500m));

        var (db, ledger, _, _) = New(org, dbName, race);

        var entry = await Debit(ledger, 4000m);

        race.Fired.Should().BeTrue();
        entry.SequenceNo.Should().Be(2);
        entry.RunningBalance.Should().Be(4500m, "the rival's 500 plus ours — the balance did not fork");
        (await Stored(db)).Select(e => (e.SequenceNo, e.DebitAmount, e.RunningBalance)).Should().Equal(
            (1, 500m, 500m), (2, 4000m, 4500m));
    }

    [Fact]
    public async Task A_persistent_failure_is_retried_then_surfaced_and_nothing_is_left_behind()
    {
        var refusing = new AlwaysFail();
        var (db, ledger, _, _) = New(interceptor: refusing);

        var act = async () => await Debit(ledger, 100m);

        (await act.Should().ThrowAsync<DbUpdateException>()).WithMessage("*refusing writes*");
        refusing.Attempts.Should().Be(5);
        db.ChangeTracker.Entries<CustomerLedgerEntry>().Where(e => e.State == EntityState.Added).Should().BeEmpty(
            "the last failed attempt is not left waiting to be saved by some later, unrelated call");
    }

    [Theory]
    [InlineData("WRITE_OFF", 10, 0)]
    [InlineData("INVOICE", 0, 0)]
    [InlineData("INVOICE", 10, 10)]
    [InlineData("INVOICE", -1, 0)]
    [InlineData("PAYMENT", 0, -1)]
    [InlineData("INVOICE", 10.005, 0)]
    [InlineData("PAYMENT", 0, 0.001)]
    public async Task An_invalid_entry_is_refused_and_nothing_is_saved(string type, double debit, double credit)
    {
        var (db, ledger, _, _) = New();

        var act = async () => await ledger.AppendEntryAsync(Customer, type, (decimal)debit, (decimal)credit, Ref(), "PKR", User);

        await act.Should().ThrowAsync<ArgumentException>();
        (await Stored(db)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_ledger_entry_needs_a_customer_a_currency_and_a_reference()
    {
        var (db, ledger, _, _) = New();
        var good = Ref();

        var noCustomer = async () => await ledger.AppendEntryAsync(Guid.Empty, "INVOICE", 1m, 0m, good, "PKR", User);
        (await noCustomer.Should().ThrowAsync<ArgumentException>()).WithMessage("*customer*");

        var noCurrency = async () => await ledger.AppendEntryAsync(Customer, "INVOICE", 1m, 0m, good, " ", User);
        (await noCurrency.Should().ThrowAsync<ArgumentException>()).WithMessage("*currency code*");

        var noNumber = async () => await ledger.AppendEntryAsync(Customer, "INVOICE", 1m, 0m, good with { Number = "" }, "PKR", User);
        (await noNumber.Should().ThrowAsync<ArgumentException>()).WithMessage("*reference number*");

        var noType = async () => await ledger.AppendEntryAsync(Customer, "INVOICE", 1m, 0m, good with { Type = "" }, "PKR", User);
        (await noType.Should().ThrowAsync<ArgumentException>()).WithMessage("*reference type*");

        (await Stored(db)).Should().BeEmpty();
    }

    [Fact]
    public async Task Over_long_fields_are_refused_before_they_reach_the_database()
    {
        var (db, ledger, _, _) = New();

        (await FluentActions.Awaiting(() => ledger.AppendEntryAsync(Customer, "INVOICE", 1m, 0m, Ref(new string('N', 31)), "PKR", User)).Should().ThrowAsync<ArgumentException>()).WithMessage("*31*".Replace("31", "30"));
        (await FluentActions.Awaiting(() => ledger.AppendEntryAsync(Customer, "INVOICE", 1m, 0m, Ref(), "PKR", User, narration: new string('x', 501))).Should().ThrowAsync<ArgumentException>()).WithMessage("*narration*");
        (await FluentActions.Awaiting(() => ledger.AppendEntryAsync(Customer, "INVOICE", 1m, 0m, Ref(), "PKR-PKR-PKR", User)).Should().ThrowAsync<ArgumentException>()).WithMessage("*currency code*");
        (await Stored(db)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_opening_balance_may_have_no_document_behind_it()
    {
        var (_, ledger, _, _) = New();

        var entry = await ledger.AppendEntryAsync(Customer, "OPENING_BAL", 12000m, 0m,
            new CustomerLedgerReference("OpeningBalance", Guid.Empty, "OPENING"), "PKR", User);

        entry.ReferenceId.Should().Be(Guid.Empty);
        entry.RunningBalance.Should().Be(12000m);
    }

    // ── GetLedger ────────────────────────────────────────────────────────────

    private static async Task Post(CustomerLedgerService s, int count, Guid? partner = null)
    {
        for (var i = 1; i <= count; i++) await Debit(s, i, partner, Now.AddMinutes(i));
    }

    [Fact]
    public async Task The_ledger_lists_the_newest_posting_first_with_every_column()
    {
        var (_, ledger, _, _) = New();
        var reference = new CustomerLedgerReference("SalesInvoice", Guid.NewGuid(), "SINV-20260920-0001");
        await ledger.AppendEntryAsync(Customer, "INVOICE", 4000m, 0m, reference, "PKR", 7, "Invoice raised", new DateTime(2026, 9, 18, 9, 0, 0));
        await Credit(ledger, 1000m, date: new DateTime(2026, 9, 19));

        var page = await ledger.GetLedgerAsync(Customer, new CustomerLedgerFilter());

        page.Data.Select(e => e.SequenceNo).Should().Equal(new List<int> { 2, 1 });
        var first = page.Data[1];
        first.Uuid.Should().NotBeEmpty();
        first.PartnerId.Should().Be(Customer);
        first.EntryType.Should().Be("INVOICE");
        first.ReferenceType.Should().Be("SalesInvoice");
        first.ReferenceId.Should().Be(reference.Id);
        first.ReferenceNumber.Should().Be("SINV-20260920-0001");
        first.DebitAmount.Should().Be(4000m);
        first.CreditAmount.Should().Be(0m);
        first.RunningBalance.Should().Be(4000m);
        first.CurrencyCode.Should().Be("PKR");
        first.Narration.Should().Be("Invoice raised");
        first.EntryDate.Should().Be(new DateTime(2026, 9, 18, 9, 0, 0));
        first.CreatedBy.Should().Be(7);
        first.CreatedDate.Should().Be(Now);
        page.Data[0].RunningBalance.Should().Be(3000m);
    }

    [Fact]
    public async Task Every_rows_balance_follows_from_the_one_below_it_even_when_a_receipt_is_back_dated()
    {
        // Posted 1) invoice dated the 20th, then 2) a receipt dated the 15th. By entry date the receipt
        // is the older row, but its balance was computed after the invoice — so the page follows
        // posting order, and the balances stay a chain a reader can check.
        var (_, ledger, _, _) = New();
        await Debit(ledger, 1000m, date: new DateTime(2026, 9, 20));
        await Credit(ledger, 400m, date: new DateTime(2026, 9, 15));
        await Debit(ledger, 250m, date: new DateTime(2026, 9, 21));

        var rows = (await ledger.GetLedgerAsync(Customer, new CustomerLedgerFilter())).Data;

        rows.Select(r => r.SequenceNo).Should().Equal(new List<int> { 3, 2, 1 });
        rows.Select(r => r.EntryDate.Day).Should().Equal(new List<int> { 21, 15, 20 }, "the business dates, as entered");

        for (var i = 0; i < rows.Count - 1; i++)
            rows[i].RunningBalance.Should().Be(rows[i + 1].RunningBalance + rows[i].DebitAmount - rows[i].CreditAmount,
                $"row {rows[i].SequenceNo}'s balance follows from row {rows[i + 1].SequenceNo}'s");
        rows[^1].RunningBalance.Should().Be(rows[^1].DebitAmount - rows[^1].CreditAmount, "the oldest row starts from nothing");
    }

    [Fact]
    public async Task The_date_range_is_whole_days_inclusive_at_both_ends()
    {
        var (_, ledger, _, _) = New();
        await Debit(ledger, 1m, date: new DateTime(2026, 9, 9, 23, 59, 59));   // before
        await Debit(ledger, 2m, date: new DateTime(2026, 9, 10, 0, 0, 0));      // first instant of the start day
        await Debit(ledger, 3m, date: new DateTime(2026, 9, 15, 12, 0, 0));
        await Debit(ledger, 4m, date: new DateTime(2026, 9, 20, 15, 30, 0));    // afternoon of the end day
        await Debit(ledger, 5m, date: new DateTime(2026, 9, 21, 0, 0, 0));      // first instant after it

        var page = await ledger.GetLedgerAsync(Customer, new CustomerLedgerFilter
        {
            DateFrom = new DateTime(2026, 9, 10, 18, 0, 0),   // time of day on the bounds is ignored
            DateTo   = new DateTime(2026, 9, 20)
        });

        page.Data.Select(e => e.DebitAmount).Should().Equal(new List<decimal> { 4m, 3m, 2m });
        page.TotalRecords.Should().Be(3);
    }

    [Fact]
    public async Task Either_end_of_the_range_may_be_left_open()
    {
        var (_, ledger, _, _) = New();
        foreach (var day in new[] { 5, 10, 15, 20, 25 }) await Debit(ledger, day, date: new DateTime(2026, 9, day, 8, 0, 0));

        var from  = await ledger.GetLedgerAsync(Customer, new CustomerLedgerFilter { DateFrom = new DateTime(2026, 9, 15) });
        var to    = await ledger.GetLedgerAsync(Customer, new CustomerLedgerFilter { DateTo = new DateTime(2026, 9, 10) });
        var all   = await ledger.GetLedgerAsync(Customer, new CustomerLedgerFilter());
        var one   = await ledger.GetLedgerAsync(Customer, new CustomerLedgerFilter { DateFrom = new DateTime(2026, 9, 20), DateTo = new DateTime(2026, 9, 20) });

        from.Data.Select(e => e.DebitAmount).Should().Equal(new List<decimal> { 25m, 20m, 15m });
        to.Data.Select(e => e.DebitAmount).Should().Equal(new List<decimal> { 10m, 5m });
        all.TotalRecords.Should().Be(5);
        one.Data.Should().ContainSingle().Which.DebitAmount.Should().Be(20m, "a single day is a valid range");
    }

    [Fact]
    public async Task A_range_that_ends_before_it_starts_is_refused()
    {
        var (_, ledger, _, _) = New();

        var act = async () => await ledger.GetLedgerAsync(Customer, new CustomerLedgerFilter
        {
            DateFrom = new DateTime(2026, 9, 20), DateTo = new DateTime(2026, 9, 10)
        });

        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage("*start date is after*");
    }

    [Fact]
    public async Task Pages_cover_the_ledger_once_each_without_overlap()
    {
        var (_, ledger, _, _) = New();
        await Post(ledger, 25);

        var p1 = await ledger.GetLedgerAsync(Customer, new CustomerLedgerFilter { Page = 1, PageSize = 10 });
        var p2 = await ledger.GetLedgerAsync(Customer, new CustomerLedgerFilter { Page = 2, PageSize = 10 });
        var p3 = await ledger.GetLedgerAsync(Customer, new CustomerLedgerFilter { Page = 3, PageSize = 10 });

        p1.Data.Should().HaveCount(10);
        p2.Data.Should().HaveCount(10);
        p3.Data.Should().HaveCount(5);
        p1.Data.Concat(p2.Data).Concat(p3.Data).Select(e => e.SequenceNo).Should().Equal(Enumerable.Range(1, 25).Reverse().ToList());

        foreach (var p in new[] { p1, p2, p3 })
        {
            p.TotalRecords.Should().Be(25);
            p.TotalPages.Should().Be(3);
            p.PageSize.Should().Be(10);
        }
        (p1.Page, p1.HasPrevious, p1.HasNext).Should().Be((1, false, true));
        (p2.Page, p2.HasPrevious, p2.HasNext).Should().Be((2, true, true));
        (p3.Page, p3.HasPrevious, p3.HasNext).Should().Be((3, true, false));
    }

    [Fact]
    public async Task A_page_past_the_end_is_empty_not_an_error()
    {
        var (_, ledger, _, _) = New();
        await Post(ledger, 5);

        var page = await ledger.GetLedgerAsync(Customer, new CustomerLedgerFilter { Page = 9, PageSize = 10 });

        page.Data.Should().BeEmpty();
        page.TotalRecords.Should().Be(5);
        page.HasNext.Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 20, 1, 20)]
    [InlineData(-3, 20, 1, 20)]
    [InlineData(1, 0, 1, 1)]
    [InlineData(1, -5, 1, 1)]
    [InlineData(1, 1000, 1, 100)]
    public async Task Page_and_page_size_are_clamped_to_sensible_values(int page, int pageSize, int expectedPage, int expectedSize)
    {
        var (_, ledger, _, _) = New();
        await Post(ledger, 3);

        var result = await ledger.GetLedgerAsync(Customer, new CustomerLedgerFilter { Page = page, PageSize = pageSize });

        (result.Page, result.PageSize).Should().Be((expectedPage, expectedSize));
    }

    [Fact]
    public async Task The_default_page_is_the_first_twenty()
    {
        var (_, ledger, _, _) = New();
        await Post(ledger, 30);

        var result = await ledger.GetLedgerAsync(Customer, new CustomerLedgerFilter());

        result.Data.Should().HaveCount(20);
        (result.Page, result.PageSize, result.TotalPages).Should().Be((1, 20, 2));
    }

    [Fact]
    public async Task The_totals_describe_the_filtered_ledger_not_the_whole_of_it()
    {
        var (_, ledger, _, _) = New();
        foreach (var day in Enumerable.Range(1, 12)) await Debit(ledger, day, date: new DateTime(2026, 9, day));

        var page = await ledger.GetLedgerAsync(Customer, new CustomerLedgerFilter
        {
            DateFrom = new DateTime(2026, 9, 4), DateTo = new DateTime(2026, 9, 10), PageSize = 3
        });

        page.TotalRecords.Should().Be(7);
        page.TotalPages.Should().Be(3);
        page.Data.Should().HaveCount(3);
    }

    [Fact]
    public async Task A_customer_with_no_entries_gets_an_empty_page()
    {
        var (_, ledger, _, _) = New();

        var page = await ledger.GetLedgerAsync(Guid.NewGuid(), new CustomerLedgerFilter());

        page.Data.Should().BeEmpty();
        (page.TotalRecords, page.TotalPages, page.HasNext, page.HasPrevious).Should().Be((0, 0, false, false));
    }

    [Fact]
    public async Task Only_this_customers_entries_in_this_organization_are_listed()
    {
        var (_, ledger, _, dbName) = New();
        var other = Guid.NewGuid();
        await Post(ledger, 3);
        await Post(ledger, 2, other);

        var (_, strangers, _, _) = New(dbName: dbName);
        await Post(strangers, 4);

        (await ledger.GetLedgerAsync(Customer, new CustomerLedgerFilter())).TotalRecords.Should().Be(3);
        (await ledger.GetLedgerAsync(other, new CustomerLedgerFilter())).TotalRecords.Should().Be(2);
        (await strangers.GetLedgerAsync(Customer, new CustomerLedgerFilter())).TotalRecords.Should().Be(4,
            "another organization sees only its own ledger for the same customer id");
    }

    [Fact]
    public async Task Reading_the_ledger_tracks_nothing()
    {
        var (db, ledger, _, _) = New();
        await Post(ledger, 3);
        db.ChangeTracker.Clear();

        await ledger.GetLedgerAsync(Customer, new CustomerLedgerFilter());

        db.ChangeTracker.Entries().Should().BeEmpty("a read can never be saved back by accident");
    }

    [Fact]
    public async Task A_null_filter_is_a_programming_error()
    {
        var (_, ledger, _, _) = New();

        var act = async () => await ledger.GetLedgerAsync(Customer, null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task What_the_invoice_and_payment_flows_write_is_what_the_ledger_reads_back()
    {
        // Track (the join-your-transaction path) and Append (the save-it-yourself path) write the
        // same rows into the same sequence, and GetLedger reads both.
        var (db, ledger, _, _) = New();

        await ledger.TrackEntryAsync(new CustomerLedgerPosting(
            Customer, "INVOICE", "SalesInvoice", Guid.NewGuid(), "SINV-1", 3780m, 0m, "PKR", null, Now, User));
        await db.SaveChangesAsync();
        await Credit(ledger, 1000m);
        await ledger.TrackEntryAsync(new CustomerLedgerPosting(
            Customer, "PAYMENT", "CustomerPayment", Guid.NewGuid(), "CPAY-2", 0m, 2780m, "PKR", null, Now, User));
        await db.SaveChangesAsync();

        var rows = (await ledger.GetLedgerAsync(Customer, new CustomerLedgerFilter())).Data;

        rows.Select(r => (r.SequenceNo, r.EntryType, r.RunningBalance)).Should().Equal(
            (3, "PAYMENT", 0m), (2, "PAYMENT", 2780m), (1, "INVOICE", 3780m));
    }
}
