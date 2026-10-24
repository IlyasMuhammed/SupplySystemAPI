using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Exceptions;
using Xunit;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Accounting.Tests;

/// <summary>
/// A47 RR — journal reversal (C7: TS-RV-001..006, BR-RV-001..008, task 453) and line-level reconciliation (C8: TS-RC-001..007,
/// BR-RC-001..008, task 454) over the in-memory store, through the services the API calls.
/// </summary>
public sealed class ReversalReconciliationTests : IDisposable
{
    private static readonly Guid Acme = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Bolt = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");

    private readonly AccountingHarness _h = new();
    private Dictionary<string, ChartOfAccount> _chart = null!;

    private AccountingCaller Clerk => new(_h.Org, 7, AccountingCaller.AllGlCodes, "Cleo Clerk");
    private IJournalEntryService Entries => _h.Get<IJournalEntryService>();
    private IJournalReversalService Reversals => _h.Get<IJournalReversalService>();
    private IReconciliationService Recon => _h.Get<IReconciliationService>();

    private async Task SetupAsync()
    {
        _chart = await _h.SeedChartAsync(null, AccountingHarness.StandardLeaves);
        await _h.AddFiscalYearAsync();
        await _h.Get<JournalTypeSeeder>().EnsureAsync(_h.Org);
    }

    private async Task<Guid> TypeAsync(string code) =>
        (await _h.Db.JournalTypes.IgnoreQueryFilters().SingleAsync(t => t.OrganizationId == _h.Org && t.Code == code)).Uuid;

    private JournalEntryLineRequest L(string code, decimal debit, decimal credit, Guid? partner = null, string? partnerType = null) =>
        new() { AccountUuid = _chart[code].Uuid, Debit = debit, Credit = credit, PartnerUuid = partner, PartnerType = partnerType, Description = $"{code} line" };

    private async Task<JournalEntryDetailModel> EntryAsync(bool post, string type, DateOnly date, params JournalEntryLineRequest[] lines) =>
        await Entries.CreateAsync(Clerk, new CreateJournalEntryRequest
        {
            JournalTypeUuid = await TypeAsync(type), EntryDate = date, Description = "Test entry", Reference = "REF-1", PostImmediately = post, Lines = [.. lines],
        });

    /// <summary>A posted expense 1000 Dr 5111 / Cr 1111 dated 2026-10-15 (JV/2026/0001 on a fresh org).</summary>
    private Task<JournalEntryDetailModel> PostedExpenseAsync(decimal amount = 1000) =>
        EntryAsync(true, SystemJournalTypes.Misc, new DateOnly(2026, 10, 15), L("5111", amount, 0), L("1111", 0, amount));

    /// <summary>A posted sales invoice: AR 1113 Dr / revenue 4111 Cr for the customer.</summary>
    private Task<JournalEntryDetailModel> InvoiceAsync(decimal amount, DateOnly date, Guid? customer = null) =>
        EntryAsync(true, SystemJournalTypes.Sale, date, L("1113", amount, 0, customer ?? Acme, "Customer"), L("4111", 0, amount, customer ?? Acme, "Customer"));

    /// <summary>A posted customer payment: cash 1111 Dr / AR 1113 Cr.</summary>
    private Task<JournalEntryDetailModel> PaymentAsync(decimal amount, DateOnly date, Guid? customer = null) =>
        EntryAsync(true, SystemJournalTypes.Cash, date, L("1111", amount, 0), L("1113", 0, amount, customer ?? Acme, "Customer"));

    private static Guid Ar(JournalEntryDetailModel e) => e.Lines.Single(l => l.Account.AccountCode == "1113").LineUuid;

    private Task<JournalEntryDetailModel> DetailAsync(Guid uuid) => Entries.GetByIdAsync(Clerk, uuid);

    private Task<JournalEntryLine> LineAsync(Guid lineUuid) =>
        _h.Db.JournalEntryLines.IgnoreQueryFilters().AsNoTracking().SingleAsync(l => l.Uuid == lineUuid);

    private async Task<decimal> BalanceAsync(string code) =>
        (await _h.Db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == _chart[code].Id)).CurrentBalance;

    private async Task SetPeriodAsync(int number, string status)
    {
        var p = await _h.Db.FiscalPeriods.IgnoreQueryFilters().SingleAsync(x => x.OrganizationId == _h.Org && x.PeriodNumber == number);
        p.Status = status;
        await _h.Db.SaveChangesAsync();
    }

    /// <summary>D-16 — REC/{year of the reconciliation (today)}/{seq:D4}.</summary>
    private static string Rec(int n) => $"REC/{DateTime.UtcNow.Year}/{n:D4}";

    private static ReverseJournalEntryRequest Because(string reason = "Incorrect amount", DateOnly? date = null) => new() { Reason = reason, ReversalDate = date };

    // ══ C7 — reversal ═══════════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TS_RV_001_reverse_posted_entry_mirrors_posts_and_nets_balances_to_zero()
    {
        await SetupAsync();
        var original = await PostedExpenseAsync();
        (await BalanceAsync("5111")).Should().Be(1000);
        (await BalanceAsync("1111")).Should().Be(-1000);

        var result = await Reversals.ReverseAsync(Clerk, original.Uuid, Because());

        var reversal = result.Reversal;
        reversal.Status.Should().Be(JournalStatuses.Posted, "BR-RV-005 — auto-posted, no Draft");
        reversal.EntryNumber.Should().Be("JV/2026/0002");
        reversal.JournalType.Code.Should().Be(SystemJournalTypes.Misc);
        reversal.EntryDate.Should().Be(original.EntryDate, "no reversal date = the original's");
        reversal.ReversesEntry!.Uuid.Should().Be(original.Uuid, "BR-RV-007");
        reversal.IdempotencyKey.Should().Be(JournalKeys.Reversal(original.Uuid));
        reversal.EntryType.Should().Be(original.EntryType);
        reversal.Reference.Should().Be("REF-1");
        reversal.TotalDebit.Should().Be(1000);
        reversal.TotalCredit.Should().Be(1000);
        // BR-RV-004 — debit ↔ credit on every line, same account and description.
        reversal.Lines.Select(l => (l.Account.AccountCode, l.Debit, l.Credit, l.Description, l.AmountCurrency))
            .Should().Equal(("5111", 0m, 1000m, "5111 line", -1000m), ("1111", 1000m, 0m, "1111 line", 1000m));
        result.Original.Uuid.Should().Be(original.Uuid);
        result.Warning.Should().BeNull("a manual entry has no source document");

        var after = await DetailAsync(original.Uuid);
        after.Status.Should().Be(JournalStatuses.Reversed, "BR-RV-006");
        after.ReversedByEntry!.Uuid.Should().Be(reversal.Uuid);
        after.CanReverse.Should().BeFalse();
        (await BalanceAsync("5111")).Should().Be(0);
        (await BalanceAsync("1111")).Should().Be(0);
    }

    [Fact]
    public async Task TS_RV_002_reversing_twice_is_refused()
    {
        await SetupAsync();
        var original = await PostedExpenseAsync();
        await Reversals.ReverseAsync(Clerk, original.Uuid, Because());

        var ex = (await FluentActions.Awaiting(() => Reversals.ReverseAsync(Clerk, original.Uuid, Because())).Should()
            .ThrowAsync<JournalConflictException>()).Which;
        ex.ErrorCode.Should().Be(JournalErrorCodes.Reversal);
        ex.Message.Should().Be("Entry has already been reversed");
        (await _h.Db.JournalEntries.IgnoreQueryFilters().CountAsync(j => j.OrganizationId == _h.Org)).Should().Be(2);
        (await BalanceAsync("5111")).Should().Be(0, "balances moved once");
    }

    [Fact]
    public async Task TS_RV_003_reversing_a_draft_is_refused()
    {
        await SetupAsync();
        var draft = await EntryAsync(false, SystemJournalTypes.Misc, new DateOnly(2026, 10, 15), L("5111", 10, 0), L("1111", 0, 10));
        var ex = (await FluentActions.Awaiting(() => Reversals.ReverseAsync(Clerk, draft.Uuid, Because())).Should()
            .ThrowAsync<JournalConflictException>()).Which;
        ex.ErrorCode.Should().Be(JournalErrorCodes.Reversal);
        ex.Message.Should().Be("Only posted entries can be reversed");
    }

    [Theory]
    [InlineData(PeriodStatuses.Closed, JournalErrorCodes.PeriodClosed, "Reversal period is closed")]   // TS-RV-004
    [InlineData(PeriodStatuses.Locked, JournalErrorCodes.PeriodLocked, "Reversal period is locked")]
    [InlineData(PeriodStatuses.AdjustmentOnly, JournalErrorCodes.AdjustmentOnly, "Reversal period accepts only adjustment entries")]
    public async Task TS_RV_004_reversal_date_period_must_accept_the_entry(string status, string code, string message)
    {
        await SetupAsync();
        var original = await PostedExpenseAsync();
        await SetPeriodAsync(11, status);

        var ex = (await FluentActions.Awaiting(() => Reversals.ReverseAsync(Clerk, original.Uuid, Because(date: new DateOnly(2026, 11, 3)))).Should()
            .ThrowAsync<JournalValidationException>()).Which;
        ex.ErrorCode.Should().Be(code);
        ex.Message.Should().Be(message);
        (await DetailAsync(original.Uuid)).Status.Should().Be(JournalStatuses.Posted, "nothing changed");
    }

    [Fact]
    public async Task TS_RV_004_reversal_date_outside_every_period_is_refused()
    {
        await SetupAsync();
        var original = await PostedExpenseAsync();
        var ex = (await FluentActions.Awaiting(() => Reversals.ReverseAsync(Clerk, original.Uuid, Because(date: new DateOnly(2031, 1, 1)))).Should()
            .ThrowAsync<JournalValidationException>()).Which;
        ex.ErrorCode.Should().Be(JournalErrorCodes.NoPeriod);
        ex.Message.Should().Be("No fiscal period covers the reversal date");
    }

    [Fact]
    public async Task Reversal_into_a_closed_fiscal_year_says_so()
    {
        await SetupAsync();
        var original = await PostedExpenseAsync();
        var year = await _h.Db.FiscalYears.IgnoreQueryFilters().SingleAsync(y => y.OrganizationId == _h.Org);
        year.IsClosed = true;
        await _h.Db.SaveChangesAsync();

        var ex = (await FluentActions.Awaiting(() => Reversals.ReverseAsync(Clerk, original.Uuid, Because())).Should()
            .ThrowAsync<JournalValidationException>()).Which;
        ex.ErrorCode.Should().Be(JournalErrorCodes.PeriodClosed);
        ex.Message.Should().Be("Fiscal year is closed");
    }

    [Fact]
    public async Task TS_RV_005_BR_RV_008_original_and_reversal_lines_are_auto_reconciled_per_account()
    {
        await SetupAsync();
        var original = await PostedExpenseAsync();
        var result = await Reversals.ReverseAsync(Clerk, original.Uuid, Because(date: new DateOnly(2026, 10, 20)));

        result.ReconcileGroupNumbers.Should().Equal(Rec(1), Rec(2));
        var lines = await _h.Db.JournalEntryLines.IgnoreQueryFilters().AsNoTracking().Include(l => l.ReconcileGroup).Include(l => l.ClearingEntry)
            .Where(l => l.OrganizationId == _h.Org).ToListAsync();
        lines.Should().HaveCount(4).And.OnlyContain(l => l.Reconciled && l.AmountResidual == 0);
        lines.Should().OnlyContain(l => l.ClearingEntry!.Uuid == result.Reversal.Uuid && l.ClearingDate == new DateOnly(2026, 10, 20));
        foreach (var account in lines.GroupBy(l => l.AccountId))
        {
            account.Select(l => l.ReconcileGroupId).Distinct().Should().ContainSingle("one group per account");
            var g = account.First().ReconcileGroup!;
            g.ReconcileType.Should().Be(ReconcileTypes.Full);
            g.Amount.Should().Be(1000);
            g.AccountId.Should().Be(account.Key);
            g.CurrencyCode.Should().Be("PKR");
        }
        // The detail shows the group on each line (contract §2 JournalEntryLine).
        (await DetailAsync(original.Uuid)).Lines.Should().OnlyContain(l => l.Reconciled && l.ReconcileGroupNumber != null);
        // Nothing left open on either account.
        (await Recon.GetOpenItemsAsync(Clerk, new OpenItemsQuery { AccountUuid = _chart["5111"].Uuid })).Should().BeEmpty();
    }

    [Fact]
    public async Task TS_RV_006_reason_is_stored_and_described()
    {
        await SetupAsync();
        var original = await PostedExpenseAsync();
        var result = await Reversals.ReverseAsync(Clerk, original.Uuid, Because("  Incorrect amount "));

        result.Reversal.Description.Should().Be("Reversal of JV/2026/0001: Incorrect amount");
        result.Reversal.ReversalReason.Should().Be("Incorrect amount");
        (await DetailAsync(original.Uuid)).ReversalReason.Should().Be("Incorrect amount");
    }

    [Fact]
    public async Task Reversal_publishes_the_event_into_the_history()
    {
        await SetupAsync();
        var original = await PostedExpenseAsync();
        var result = await Reversals.ReverseAsync(Clerk, original.Uuid, Because());

        var evt = _h.Events.Of<JournalEntryReversedEvent>().Should().ContainSingle().Which;
        evt.JournalEntryId.Should().Be(original.Uuid);
        evt.ReversalEntryId.Should().Be(result.Reversal.Uuid);
        evt.Reason.Should().Be("Incorrect amount");
        evt.UserName.Should().Be("Cleo Clerk");
        (await _h.Db.GlSetupHistory.IgnoreQueryFilters().AnyAsync(x => x.EventType == nameof(JournalEntryReversedEvent)
            && x.Summary == "Journal entry JV/2026/0001 reversed by JV/2026/0002: Incorrect amount.")).Should().BeTrue();
    }

    [Fact]
    public async Task Journal_entry_service_delegates_reverse_to_the_reversal_service()
    {
        await SetupAsync();
        var original = await PostedExpenseAsync();
        var result = await Entries.ReverseAsync(Clerk, original.Uuid, Because());
        result.Reversal.ReversesEntry!.Uuid.Should().Be(original.Uuid);
    }

    [Fact]
    public async Task A_reversal_entry_cannot_be_reversed()
    {
        await SetupAsync();
        var original = await PostedExpenseAsync();
        var result = await Reversals.ReverseAsync(Clerk, original.Uuid, Because());
        result.Reversal.CanReverse.Should().BeFalse();

        var ex = (await FluentActions.Awaiting(() => Reversals.ReverseAsync(Clerk, result.Reversal.Uuid, Because())).Should()
            .ThrowAsync<JournalConflictException>()).Which;
        ex.Message.Should().Be("A reversal entry cannot be reversed");
    }

    [Theory]
    [InlineData(null, "A reversal reason is required")]
    [InlineData("   ", "A reversal reason is required")]
    public async Task Reason_is_required(string? reason, string message)
    {
        await SetupAsync();
        var original = await PostedExpenseAsync();
        var ex = (await FluentActions.Awaiting(() => Reversals.ReverseAsync(Clerk, original.Uuid, new ReverseJournalEntryRequest { Reason = reason })).Should()
            .ThrowAsync<JournalValidationException>()).Which;
        ex.ErrorCode.Should().Be(JournalErrorCodes.Reversal);
        ex.Message.Should().Be(message);
    }

    [Fact]
    public async Task Reason_longer_than_200_is_refused()
    {
        await SetupAsync();
        var original = await PostedExpenseAsync();
        var ex = (await FluentActions.Awaiting(() => Reversals.ReverseAsync(Clerk, original.Uuid, Because(new string('x', 201)))).Should()
            .ThrowAsync<JournalValidationException>()).Which;
        ex.Message.Should().Be("The reversal reason can be at most 200 characters");
    }

    [Fact]
    public async Task Reverse_needs_GL_JOURNAL_REVERSE_and_the_callers_organization()
    {
        await SetupAsync();
        var original = await PostedExpenseAsync();
        var poster = new AccountingCaller(_h.Org, 8, [P.GL_VIEW, P.GL_JOURNAL_CREATE, P.GL_JOURNAL_POST], "Pat");
        await FluentActions.Awaiting(() => Reversals.ReverseAsync(poster, original.Uuid, Because())).Should().ThrowAsync<ForbiddenException>();

        var stranger = new AccountingCaller(Guid.NewGuid(), 9, AccountingCaller.AllGlCodes, "Other Org");
        (await FluentActions.Awaiting(() => Reversals.ReverseAsync(stranger, original.Uuid, Because())).Should().ThrowAsync<NotFoundException>())
            .Which.Message.Should().Be("Journal entry not found.");
        await FluentActions.Awaiting(() => Reversals.ReverseAsync(Clerk, Guid.NewGuid(), Because())).Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Reversal_works_on_an_account_deactivated_since()
    {
        await SetupAsync();
        var original = await PostedExpenseAsync();
        var account = await _h.Db.ChartOfAccounts.IgnoreQueryFilters().SingleAsync(a => a.Id == _chart["5111"].Id);
        account.IsActive = false;
        await _h.Db.SaveChangesAsync();

        (await Reversals.ReverseAsync(Clerk, original.Uuid, Because())).Reversal.Status.Should().Be(JournalStatuses.Posted);
        (await BalanceAsync("5111")).Should().Be(0);
    }

    [Fact]
    public async Task Inactive_journal_type_falls_back_to_MISC()
    {
        await SetupAsync();
        var invoice = await InvoiceAsync(500, new DateOnly(2026, 10, 1));
        var sale = await _h.Db.JournalTypes.IgnoreQueryFilters().SingleAsync(t => t.OrganizationId == _h.Org && t.Code == SystemJournalTypes.Sale);
        sale.IsActive = false;
        await _h.Db.SaveChangesAsync();

        var result = await Reversals.ReverseAsync(Clerk, invoice.Uuid, Because());
        result.Reversal.JournalType.Code.Should().Be(SystemJournalTypes.Misc);
        result.Reversal.Lines.Should().OnlyContain(l => l.PartnerUuid == Acme && l.PartnerType == "Customer", "partners are mirrored");
    }

    [Fact]
    public async Task Foreign_currency_entry_reverses_with_the_same_rate_and_negated_currency_amounts()
    {
        await SetupAsync();
        var usd = await Entries.CreateAsync(Clerk, new CreateJournalEntryRequest
        {
            JournalTypeUuid = await TypeAsync(SystemJournalTypes.Misc), EntryDate = new DateOnly(2026, 10, 15), CurrencyCode = "USD", ExchangeRate = 3.67m,
            PostImmediately = true, Lines = [L("5111", 1000, 0), L("1111", 0, 1000)],
        });
        var r = (await Reversals.ReverseAsync(Clerk, usd.Uuid, Because())).Reversal;

        r.CurrencyCode.Should().Be("USD");
        r.ExchangeRate.Should().Be(3.67m);
        r.Lines.Select(l => (l.Debit, l.Credit, l.AmountCurrency, l.CurrencyCode)).Should().Equal((0m, 3670m, -1000m, "USD"), (3670m, 0m, 1000m, "USD"));
        (await BalanceAsync("5111")).Should().Be(0);
    }

    [Fact]
    public async Task REV_8_zero_lines_of_an_A44_journal_are_not_mirrored()
    {
        await SetupAsync();
        var sale = await _h.Db.JournalTypes.IgnoreQueryFilters().SingleAsync(t => t.OrganizationId == _h.Org && t.Code == SystemJournalTypes.Sale);
        var source = Guid.NewGuid();
        var written = await _h.Get<JournalEntryWriter>().CreateAsync(new JournalDraft
        {
            OrganizationId = _h.Org, JournalTypeId = sale.Id, EntryDate = new DateOnly(2026, 10, 5), EntryType = "SALES_INVOICE", SourceModule = "finance",
            SourceDocumentUuid = source, IdempotencyKey = $"finance:SalesInvoice:{source}", AmountsInBase = true,
            Lines = [new() { AccountId = _chart["1113"].Id, Debit = 118 }, new() { AccountId = _chart["4111"].Id, Credit = 118 }],
        }, post: true);
        await _h.Db.SaveChangesAsync();
        // A pre-C11 A44 journal: a line whose base amount rounded to 0.00 sits between the others (lines 1, 2 = zero, 3).
        var entry = await _h.Db.JournalEntries.IgnoreQueryFilters().Include(j => j.Lines).SingleAsync(j => j.Id == written.Entry.Id);
        entry.Lines.Single(l => l.LineNumber == 2).LineNumber = 3;
        entry.Lines.Add(new JournalEntryLine { OrganizationId = _h.Org, LineNumber = 2, AccountId = _chart["5111"].Id, Memo = "rounded away" });
        entry.LineCount = 3;
        await _h.Db.SaveChangesAsync();
        (await BalanceAsync("1113")).Should().Be(118);

        var result = await Reversals.ReverseAsync(Clerk, entry.Uuid, Because("Posted twice"));

        result.Reversal.Lines.Select(l => (l.LineNumber, l.Account.AccountCode, l.Debit, l.Credit))
            .Should().Equal((1, "1113", 0m, 118m), (2, "4111", 118m, 0m));
        result.ReconcileGroupNumbers.Should().HaveCount(2, "one per account with amounts; the zero line needs none");
        (await LineAsync(entry.Lines.Single(l => l.LineNumber == 2).Uuid)).ReconcileGroupId.Should().BeNull();
        (await BalanceAsync("1113")).Should().Be(0);
        (await BalanceAsync("4111")).Should().Be(0);
        (await BalanceAsync("5111")).Should().Be(0);
        (await DetailAsync(entry.Uuid)).Status.Should().Be(JournalStatuses.Reversed);
    }

    [Fact]
    public async Task Source_document_journal_may_be_reversed_by_hand_with_a_warning()
    {
        await SetupAsync();
        var misc = await _h.Db.JournalTypes.IgnoreQueryFilters().SingleAsync(t => t.OrganizationId == _h.Org && t.Code == SystemJournalTypes.Sale);
        var source = Guid.NewGuid();
        var writer = _h.Get<JournalEntryWriter>();
        var written = await writer.CreateAsync(new JournalDraft
        {
            OrganizationId = _h.Org, JournalTypeId = misc.Id, EntryDate = new DateOnly(2026, 10, 5), EntryType = "SALES_INVOICE", SourceModule = "finance",
            SourceDocumentUuid = source, IdempotencyKey = $"finance:SalesInvoice:{source}", AmountsInBase = true,
            Lines = [new() { AccountId = _chart["1113"].Id, Debit = 118 }, new() { AccountId = _chart["4111"].Id, Credit = 118 }],
        }, post: true);
        await _h.Db.SaveChangesAsync();

        var result = await Reversals.ReverseAsync(Clerk, written.Entry.Uuid, Because("Posted twice"));
        result.Warning.Should().Contain("source document").And.Contain("finance");
        result.Reversal.SourceModule.Should().Be("finance");
        result.Reversal.SourceDocumentUuid.Should().Be(source);
        result.Reversal.EntryType.Should().Be("SALES_INVOICE");
        result.Reversal.IsManual.Should().BeFalse();
    }

    [Fact]
    public async Task Account_whose_original_line_is_already_reconciled_is_skipped()
    {
        await SetupAsync();
        var invoice = await InvoiceAsync(1000, new DateOnly(2026, 10, 1));
        var payment = await PaymentAsync(1000, new DateOnly(2026, 10, 5));
        var paid = await Recon.ReconcileAsync(Clerk, new ReconcileRequest { LineUuids = [Ar(invoice), Ar(payment)] });

        var result = await Reversals.ReverseAsync(Clerk, invoice.Uuid, Because());

        result.ReconcileGroupNumbers.Should().ContainSingle("only the revenue account");
        (await LineAsync(Ar(invoice))).ReconcileGroupId.Should().NotBeNull();
        (await _h.Db.ReconcileGroups.IgnoreQueryFilters().AnyAsync(g => g.Uuid == paid.Uuid)).Should().BeTrue("the payment match stays");
        var mirrorAr = result.Reversal.Lines.Single(l => l.Account.AccountCode == "1113");
        mirrorAr.Reconciled.Should().BeFalse();
        mirrorAr.AmountResidual.Should().Be(-1000, "the customer now has an open credit");
    }

    [Fact]
    public void Opening_balance_and_year_end_closing_entries_cannot_be_reversed()
    {
        var opening = new JournalEntry { Status = JournalStatuses.Posted, EntryType = JournalEntryTypes.OpeningBalance };
        FluentActions.Invoking(() => JournalReversalService.EnsureReversible(opening)).Should().Throw<JournalConflictException>()
            .WithMessage("The opening balance entry cannot be reversed");
        var close = new JournalEntry { Status = JournalStatuses.Posted, EntryType = JournalEntryTypes.YearEndClose };
        FluentActions.Invoking(() => JournalReversalService.EnsureReversible(close)).Should().Throw<JournalConflictException>()
            .WithMessage("The year-end closing entry cannot be reversed");
        FluentActions.Invoking(() => JournalReversalService.EnsureReversible(new JournalEntry { Status = JournalStatuses.Posted, EntryType = "SALES_INVOICE" }))
            .Should().NotThrow();
    }

    // ══ C8 — reconciliation ═════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TS_RC_001_invoice_and_full_payment_reconcile_fully()
    {
        await SetupAsync();
        var invoice = await InvoiceAsync(1000, new DateOnly(2026, 10, 1));
        var payment = await PaymentAsync(1000, new DateOnly(2026, 10, 9));

        var g = await Recon.ReconcileAsync(Clerk, new ReconcileRequest { LineUuids = [Ar(invoice), Ar(payment)] });

        g.ReconcileType.Should().Be(ReconcileTypes.Full);
        g.GroupNumber.Should().Be(Rec(1));
        g.Amount.Should().Be(1000);
        g.Residual.Should().Be(0);
        g.Account.AccountCode.Should().Be("1113");
        g.CreatedByName.Should().Be("Cleo Clerk");
        g.Lines.Select(l => l.LineUuid).Should().Equal(Ar(invoice), Ar(payment));
        foreach (var uuid in new[] { Ar(invoice), Ar(payment) })
        {
            var line = await LineAsync(uuid);
            line.Reconciled.Should().BeTrue();
            line.AmountResidual.Should().Be(0);
            line.ClearingDate.Should().Be(new DateOnly(2026, 10, 9), "the latest entry's date");
        }
        var invoiceDetail = await DetailAsync(invoice.Uuid);
        invoiceDetail.Lines.Single(l => l.Account.AccountCode == "1113").ClearingEntry!.Uuid.Should().Be(payment.Uuid);
    }

    [Fact]
    public async Task TS_RC_002_partial_payment_leaves_the_invoice_residual()
    {
        await SetupAsync();
        var invoice = await InvoiceAsync(1000, new DateOnly(2026, 10, 1));
        var payment = await PaymentAsync(600, new DateOnly(2026, 10, 9));

        var g = await Recon.ReconcileAsync(Clerk, new ReconcileRequest { LineUuids = [Ar(payment), Ar(invoice)] });

        g.ReconcileType.Should().Be(ReconcileTypes.Partial);
        g.Amount.Should().Be(600);
        g.Residual.Should().Be(400);
        var inv = await LineAsync(Ar(invoice));
        inv.AmountResidual.Should().Be(400);
        inv.Reconciled.Should().BeFalse();
        inv.ReconcileGroupId.Should().NotBeNull("D-16 — it keeps the group until fully cleared");
        inv.ClearingEntryId.Should().BeNull();
        var pay = await LineAsync(Ar(payment));
        pay.AmountResidual.Should().Be(0);
        pay.Reconciled.Should().BeTrue();

        // The partially reconciled invoice is still an open item, showing its group.
        var open = await Recon.GetOpenItemsAsync(Clerk, new OpenItemsQuery { AccountUuid = _chart["1113"].Uuid });
        open.Should().ContainSingle().Which.ReconcileGroupNumber.Should().Be(g.GroupNumber);
    }

    [Fact]
    public async Task TS_RC_003_three_invoices_and_one_payment_reconcile_fully()
    {
        await SetupAsync();
        var a = await InvoiceAsync(1000, new DateOnly(2026, 10, 1));
        var b = await InvoiceAsync(500, new DateOnly(2026, 10, 2));
        var c = await InvoiceAsync(300, new DateOnly(2026, 10, 3));
        var pay = await PaymentAsync(1800, new DateOnly(2026, 10, 9));

        var g = await Recon.ReconcileAsync(Clerk, new ReconcileRequest { LineUuids = [Ar(a), Ar(b), Ar(c), Ar(pay)] });

        g.ReconcileType.Should().Be(ReconcileTypes.Full);
        g.Amount.Should().Be(1800);
        g.Lines.Should().HaveCount(4).And.OnlyContain(l => l.AmountResidual == 0);
    }

    [Fact]
    public async Task Partial_uses_the_larger_side_oldest_first()
    {
        await SetupAsync();
        var older = await InvoiceAsync(1000, new DateOnly(2026, 10, 1));
        var newer = await InvoiceAsync(500, new DateOnly(2026, 10, 2));
        var pay = await PaymentAsync(1200, new DateOnly(2026, 10, 9));

        var g = await Recon.ReconcileAsync(Clerk, new ReconcileRequest { LineUuids = [Ar(newer), Ar(pay), Ar(older)] });

        g.ReconcileType.Should().Be(ReconcileTypes.Partial);
        (await LineAsync(Ar(older))).AmountResidual.Should().Be(0);
        (await LineAsync(Ar(older))).Reconciled.Should().BeTrue();
        (await LineAsync(Ar(newer))).AmountResidual.Should().Be(300);
        (await LineAsync(Ar(pay))).Reconciled.Should().BeTrue();
    }

    [Fact]
    public async Task TS_RC_004_unreconcile_restores_residuals_and_deletes_the_group()
    {
        await SetupAsync();
        var invoice = await InvoiceAsync(1000, new DateOnly(2026, 10, 1));
        var payment = await PaymentAsync(1000, new DateOnly(2026, 10, 9));
        var g = await Recon.ReconcileAsync(Clerk, new ReconcileRequest { LineUuids = [Ar(invoice), Ar(payment)] });

        var undone = await Recon.UnreconcileAsync(Clerk, g.Uuid);

        undone.GroupNumber.Should().Be(g.GroupNumber);
        undone.LinesRestored.Should().Be(2);
        (await LineAsync(Ar(invoice))).Should().Match<JournalEntryLine>(l =>
            !l.Reconciled && l.AmountResidual == 1000 && l.ReconcileGroupId == null && l.ClearingEntryId == null && l.ClearingDate == null);
        (await LineAsync(Ar(payment))).AmountResidual.Should().Be(-1000);
        (await _h.Db.ReconcileGroups.IgnoreQueryFilters().AnyAsync(x => x.Uuid == g.Uuid)).Should().BeFalse();
        await FluentActions.Awaiting(() => Recon.GetGroupAsync(Clerk, g.Uuid)).Should().ThrowAsync<NotFoundException>()
            .WithMessage("Reconciliation not found");
        // and the lines can be matched again (new number, no reuse)
        (await Recon.ReconcileAsync(Clerk, new ReconcileRequest { LineUuids = [Ar(invoice), Ar(payment)] })).GroupNumber.Should().Be(Rec(2));
    }

    [Fact]
    public async Task Unreconcile_of_a_partial_restores_both_sides()
    {
        await SetupAsync();
        var invoice = await InvoiceAsync(1000, new DateOnly(2026, 10, 1));
        var payment = await PaymentAsync(600, new DateOnly(2026, 10, 9));
        var g = await Recon.ReconcileAsync(Clerk, new ReconcileRequest { LineUuids = [Ar(invoice), Ar(payment)] });
        await Recon.UnreconcileAsync(Clerk, g.Uuid);
        (await LineAsync(Ar(invoice))).AmountResidual.Should().Be(1000);
        (await LineAsync(Ar(payment))).AmountResidual.Should().Be(-600);
    }

    [Fact]
    public async Task TS_RC_005_lines_of_different_accounts_are_refused()
    {
        await SetupAsync();
        var invoice = await InvoiceAsync(1000, new DateOnly(2026, 10, 1));
        var revenue = invoice.Lines.Single(l => l.Account.AccountCode == "4111").LineUuid;
        var ex = (await FluentActions.Awaiting(() => Recon.ReconcileAsync(Clerk, new ReconcileRequest { LineUuids = [Ar(invoice), revenue] })).Should()
            .ThrowAsync<JournalConflictException>()).Which;
        ex.ErrorCode.Should().Be(ReconcileErrorCodes.DifferentAccounts);
        ex.Message.Should().Be("All lines must belong to the same account");
    }

    [Fact]
    public async Task TS_RC_006_a_line_of_a_draft_is_refused()
    {
        await SetupAsync();
        var invoice = await InvoiceAsync(1000, new DateOnly(2026, 10, 1));
        var draft = await EntryAsync(false, SystemJournalTypes.Cash, new DateOnly(2026, 10, 9), L("1111", 1000, 0), L("1113", 0, 1000, Acme, "Customer"));
        var ex = (await FluentActions.Awaiting(() => Recon.ReconcileAsync(Clerk, new ReconcileRequest { LineUuids = [Ar(invoice), Ar(draft)] })).Should()
            .ThrowAsync<JournalConflictException>()).Which;
        ex.ErrorCode.Should().Be(ReconcileErrorCodes.NotPosted);
        ex.Message.Should().Be("Only lines from posted entries can be reconciled");
    }

    [Fact]
    public async Task TS_RC_007_suggest_for_an_invoice_of_5000_returns_offsetting_credit_lines()
    {
        await SetupAsync();
        var invoice = await InvoiceAsync(5000, new DateOnly(2026, 10, 1));
        var p3000 = await PaymentAsync(3000, new DateOnly(2026, 10, 2));
        var p2000 = await PaymentAsync(2000, new DateOnly(2026, 10, 3));
        var p5000 = await PaymentAsync(5000, new DateOnly(2026, 10, 4));
        await PaymentAsync(700, new DateOnly(2026, 10, 5));
        await InvoiceAsync(5000, new DateOnly(2026, 10, 6)); // a debit: never suggested for a debit selection

        var s = await Recon.SuggestMatchesAsync(Clerk, new ReconcileSuggestRequest { AccountUuid = _chart["1113"].Uuid, LineUuids = [Ar(invoice)] });

        s.Should().NotBeEmpty();
        s[0].LineUuids.Should().Equal(Ar(p5000));
        s[0].Exact.Should().BeTrue();
        s[0].Total.Should().Be(-5000);
        s[0].ResidualAfter.Should().Be(0);
        s.Should().Contain(x => x.Exact && x.LineUuids.Count == 2 && x.LineUuids.Contains(Ar(p3000)) && x.LineUuids.Contains(Ar(p2000)));
        s.TakeWhile(x => x.Exact).Count().Should().Be(s.Count(x => x.Exact), "exact matches first");
        s.Should().OnlyContain(x => x.Total < 0, "only credit lines for a debit selection");
    }

    [Fact]
    public async Task Suggest_without_an_exact_match_returns_the_nearest()
    {
        await SetupAsync();
        var invoice = await InvoiceAsync(1000, new DateOnly(2026, 10, 1));
        var p900 = await PaymentAsync(900, new DateOnly(2026, 10, 2));
        await PaymentAsync(300, new DateOnly(2026, 10, 3));

        var s = await Recon.SuggestMatchesAsync(Clerk, new ReconcileSuggestRequest { AccountUuid = _chart["1113"].Uuid, LineUuids = [Ar(invoice)], MaxResults = 2 });
        s.Should().HaveCount(2).And.OnlyContain(x => !x.Exact);
        s[0].LineUuids.Should().Equal(Ar(p900));
        s[0].ResidualAfter.Should().Be(100);
    }

    [Fact]
    public async Task Suggest_needs_an_account_matching_the_selection()
    {
        await SetupAsync();
        var invoice = await InvoiceAsync(1000, new DateOnly(2026, 10, 1));
        var ex = (await FluentActions.Awaiting(() => Recon.SuggestMatchesAsync(Clerk, new ReconcileSuggestRequest { LineUuids = [Ar(invoice)] })).Should()
            .ThrowAsync<JournalValidationException>()).Which;
        ex.ErrorCode.Should().Be(ReconcileErrorCodes.AccountRequired);
        ex.Message.Should().Be("Choose an account");
        (await FluentActions.Awaiting(() => Recon.SuggestMatchesAsync(Clerk,
                new ReconcileSuggestRequest { AccountUuid = _chart["4111"].Uuid, LineUuids = [Ar(invoice)] })).Should()
            .ThrowAsync<JournalConflictException>()).Which.ErrorCode.Should().Be(ReconcileErrorCodes.DifferentAccounts);
    }

    [Fact]
    public async Task BR_RC_003_one_line_is_not_enough()
    {
        await SetupAsync();
        var invoice = await InvoiceAsync(1000, new DateOnly(2026, 10, 1));
        var ex = (await FluentActions.Awaiting(() => Recon.ReconcileAsync(Clerk, new ReconcileRequest { LineUuids = [Ar(invoice), Ar(invoice)] })).Should()
            .ThrowAsync<JournalValidationException>()).Which;
        ex.ErrorCode.Should().Be(ReconcileErrorCodes.TooFewLines);
        ex.Message.Should().Be("Minimum 2 lines required");
    }

    [Fact]
    public async Task BR_RC_008_a_line_already_in_a_reconciliation_is_refused()
    {
        await SetupAsync();
        var invoice = await InvoiceAsync(1000, new DateOnly(2026, 10, 1));
        var first = await PaymentAsync(600, new DateOnly(2026, 10, 9));
        var second = await PaymentAsync(400, new DateOnly(2026, 10, 10));
        var g = await Recon.ReconcileAsync(Clerk, new ReconcileRequest { LineUuids = [Ar(invoice), Ar(first)] });

        var ex = (await FluentActions.Awaiting(() => Recon.ReconcileAsync(Clerk, new ReconcileRequest { LineUuids = [Ar(invoice), Ar(second)] })).Should()
            .ThrowAsync<JournalConflictException>()).Which;
        ex.ErrorCode.Should().Be(ReconcileErrorCodes.AlreadyInGroup);
        ex.Message.Should().Be($"Line 1 of {invoice.EntryNumber} is already in reconciliation {g.GroupNumber}: unreconcile it first");

        // D-16 — undo the partial, then reconcile all three together.
        await Recon.UnreconcileAsync(Clerk, g.Uuid);
        (await Recon.ReconcileAsync(Clerk, new ReconcileRequest { LineUuids = [Ar(invoice), Ar(first), Ar(second)] })).ReconcileType
            .Should().Be(ReconcileTypes.Full);
    }

    [Fact]
    public async Task Lines_on_one_side_only_have_nothing_to_reconcile()
    {
        await SetupAsync();
        var a = await InvoiceAsync(1000, new DateOnly(2026, 10, 1));
        var b = await InvoiceAsync(500, new DateOnly(2026, 10, 2));
        var ex = (await FluentActions.Awaiting(() => Recon.ReconcileAsync(Clerk, new ReconcileRequest { LineUuids = [Ar(a), Ar(b)] })).Should()
            .ThrowAsync<JournalValidationException>()).Which;
        ex.ErrorCode.Should().Be(ReconcileErrorCodes.NothingToReconcile);
        ex.Message.Should().Be("The lines have nothing left to reconcile");
    }

    [Fact]
    public async Task Unknown_line_and_another_organizations_lines_are_not_found()
    {
        await SetupAsync();
        var invoice = await InvoiceAsync(1000, new DateOnly(2026, 10, 1));
        var payment = await PaymentAsync(1000, new DateOnly(2026, 10, 9));
        await FluentActions.Awaiting(() => Recon.ReconcileAsync(Clerk, new ReconcileRequest { LineUuids = [Ar(invoice), Guid.NewGuid()] })).Should()
            .ThrowAsync<NotFoundException>().WithMessage("Journal line not found");

        var other = new AccountingCaller(Guid.NewGuid(), 9, AccountingCaller.AllGlCodes, "Other Org");
        await FluentActions.Awaiting(() => Recon.ReconcileAsync(other, new ReconcileRequest { LineUuids = [Ar(invoice), Ar(payment)] })).Should()
            .ThrowAsync<NotFoundException>();
        var g = await Recon.ReconcileAsync(Clerk, new ReconcileRequest { LineUuids = [Ar(invoice), Ar(payment)] });
        await FluentActions.Awaiting(() => Recon.GetGroupAsync(other, g.Uuid)).Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => Recon.UnreconcileAsync(other, g.Uuid)).Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => Recon.GetOpenItemsAsync(other, new OpenItemsQuery { AccountUuid = _chart["1113"].Uuid })).Should()
            .ThrowAsync<NotFoundException>().WithMessage("Account not found.");
    }

    [Fact]
    public async Task Reconciliation_permissions_view_versus_execute()
    {
        await SetupAsync();
        var invoice = await InvoiceAsync(1000, new DateOnly(2026, 10, 1));
        var payment = await PaymentAsync(1000, new DateOnly(2026, 10, 9));
        var viewer = new AccountingCaller(_h.Org, 10, [P.GL_RECONCILE_VIEW], "Val Viewer");
        var none = new AccountingCaller(_h.Org, 11, [P.GL_VIEW], "Gil");
        var query = new OpenItemsQuery { AccountUuid = _chart["1113"].Uuid };

        (await Recon.GetOpenItemsAsync(viewer, query)).Should().HaveCount(2);
        await FluentActions.Awaiting(() => Recon.ReconcileAsync(viewer, new ReconcileRequest { LineUuids = [Ar(invoice), Ar(payment)] })).Should()
            .ThrowAsync<ForbiddenException>();
        await FluentActions.Awaiting(() => Recon.GetOpenItemsAsync(none, query)).Should().ThrowAsync<ForbiddenException>();

        var executor = new AccountingCaller(_h.Org, 12, [P.GL_RECONCILE_EXECUTE], "Eve");
        var g = await Recon.ReconcileAsync(executor, new ReconcileRequest { LineUuids = [Ar(invoice), Ar(payment)] });
        (await Recon.GetGroupAsync(executor, g.Uuid)).Lines.Should().HaveCount(2, "EXECUTE implies VIEW");
        await FluentActions.Awaiting(() => Recon.UnreconcileAsync(viewer, g.Uuid)).Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task Open_items_filter_by_partner_and_dates_exclude_drafts_and_cleared_lines()
    {
        await SetupAsync();
        var acme = await InvoiceAsync(1000, new DateOnly(2026, 10, 1), Acme);
        var bolt = await InvoiceAsync(700, new DateOnly(2026, 10, 3), Bolt);
        var acmePay = await PaymentAsync(1000, new DateOnly(2026, 10, 9), Acme);
        await EntryAsync(false, SystemJournalTypes.Cash, new DateOnly(2026, 10, 9), L("1111", 50, 0), L("1113", 0, 50, Acme, "Customer")); // draft
        var ar = _chart["1113"].Uuid;

        var all = await Recon.GetOpenItemsAsync(Clerk, new OpenItemsQuery { AccountUuid = ar });
        all.Select(i => i.LineUuid).Should().Equal(Ar(acme), Ar(bolt), Ar(acmePay));
        all[0].Entry.EntryNumber.Should().Be(acme.EntryNumber);
        all[0].JournalTypeCode.Should().Be(SystemJournalTypes.Sale);
        all[0].PartnerUuid.Should().Be(Acme);
        all[0].AmountResidual.Should().Be(1000);
        all[2].AmountResidual.Should().Be(-1000);

        (await Recon.GetOpenItemsAsync(Clerk, new OpenItemsQuery { AccountUuid = ar, PartnerUuid = Bolt, PartnerType = "customer" }))
            .Select(i => i.LineUuid).Should().Equal(Ar(bolt));
        (await Recon.GetOpenItemsAsync(Clerk, new OpenItemsQuery { AccountUuid = ar, FromDate = new DateOnly(2026, 10, 2), ToDate = new DateOnly(2026, 10, 5) }))
            .Select(i => i.LineUuid).Should().Equal(Ar(bolt));

        await Recon.ReconcileAsync(Clerk, new ReconcileRequest { LineUuids = [Ar(acme), Ar(acmePay)] });
        (await Recon.GetOpenItemsAsync(Clerk, new OpenItemsQuery { AccountUuid = ar })).Select(i => i.LineUuid).Should().Equal(Ar(bolt));

        await FluentActions.Awaiting(() => Recon.GetOpenItemsAsync(Clerk, new OpenItemsQuery())).Should()
            .ThrowAsync<JournalValidationException>().WithMessage("Choose an account");
    }

    [Fact]
    public async Task Lines_of_a_reversed_entry_can_be_matched_again_after_the_auto_group_is_undone()
    {
        await SetupAsync();
        var invoice = await InvoiceAsync(1000, new DateOnly(2026, 10, 1));
        var result = await Reversals.ReverseAsync(Clerk, invoice.Uuid, Because());
        var arGroup = (await LineAsync(Ar(invoice))).ReconcileGroupId!.Value;
        var groupUuid = await _h.Db.ReconcileGroups.IgnoreQueryFilters().Where(g => g.Id == arGroup).Select(g => g.Uuid).SingleAsync();

        await Recon.UnreconcileAsync(Clerk, groupUuid);
        var open = await Recon.GetOpenItemsAsync(Clerk, new OpenItemsQuery { AccountUuid = _chart["1113"].Uuid });
        open.Should().HaveCount(2, "the Reversed entry's line counts like a posted one (D-CORE-4)");
        (await Recon.ReconcileAsync(Clerk, new ReconcileRequest { LineUuids = open.Select(o => o.LineUuid).ToList() })).ReconcileType
            .Should().Be(ReconcileTypes.Full);
        result.Reversal.Status.Should().Be(JournalStatuses.Posted);
    }

    // ── the allocation and the suggestion search (pure) ────────────────────────────────────────────────────────────

    [Fact]
    public void Allocate_full_partial_and_nothing()
    {
        JournalReconciler.Allocate([1000m, -1000m], [0, 1])!.Should().Match<JournalReconciler.Allocation>(a =>
            a.ReconcileType == ReconcileTypes.Full && a.Matched == 1000 && a.NewResiduals.All(r => r == 0));
        JournalReconciler.Allocate([1000m, -999.995m], [0, 1])!.ReconcileType.Should().Be(ReconcileTypes.Full, "within 0.01");

        var partial = JournalReconciler.Allocate([1000m, -600m], [0, 1])!;
        partial.ReconcileType.Should().Be(ReconcileTypes.Partial);
        partial.Matched.Should().Be(600);
        partial.NewResiduals.Should().Equal(400m, 0m);

        // credit side larger: the oldest credit (index 2) is used up first
        JournalReconciler.Allocate([-500m, 300m, -400m], [2, 0, 1])!.NewResiduals.Should().Equal(-500m, 0m, -100m);
        JournalReconciler.Allocate([-500m, 300m, -400m], [0, 1, 2])!.NewResiduals.Should().Equal(-200m, 0m, -400m);

        JournalReconciler.Allocate([100m, 50m], [0, 1]).Should().BeNull();
        JournalReconciler.Allocate([0m, 0m], [0, 1]).Should().BeNull();
    }

    [Fact]
    public void Suggest_search_finds_combinations_and_respects_the_limit()
    {
        var c = Enumerable.Range(1, 6).Select(i => new ReconciliationService.Candidate(Guid.NewGuid(), -100m * i)).ToList(); // -100 … -600
        var s = ReconciliationService.Suggest(600m, c, 50);
        s[0].LineUuids.Should().Equal(c[5].LineUuid);                                   // the single 600
        s.Where(x => x.Exact).Should().HaveCountGreaterThan(2);                          // 100+500, 200+400, 100+200+300 …
        s.Where(x => x.Exact).Should().OnlyContain(x => x.ResidualAfter == 0 && x.Total == -600);
        ReconciliationService.Suggest(600m, c, 2).Should().HaveCount(2);
        ReconciliationService.Suggest(-150m, [new(Guid.NewGuid(), 100m)], 5).Should().ContainSingle()
            .Which.Should().Match<ReconcileSuggestionModel>(x => !x.Exact && x.ResidualAfter == -50);
    }

    [Fact]
    public async Task SR_FE_1_groups_list_newest_first_paged_and_filtered()
    {
        await SetupAsync();
        var acmeInv = await InvoiceAsync(1000, new DateOnly(2026, 10, 1), Acme);
        var acmePay = await PaymentAsync(1000, new DateOnly(2026, 10, 2), Acme);
        var boltInv = await InvoiceAsync(500, new DateOnly(2026, 10, 3), Bolt);
        var boltPay = await PaymentAsync(300, new DateOnly(2026, 10, 4), Bolt);
        var full = await Recon.ReconcileAsync(Clerk, new ReconcileRequest { LineUuids = [Ar(acmeInv), Ar(acmePay)] });
        var partial = await Recon.ReconcileAsync(Clerk, new ReconcileRequest { LineUuids = [Ar(boltInv), Ar(boltPay)] });
        var expense = await PostedExpenseAsync();
        await Reversals.ReverseAsync(Clerk, expense.Uuid, Because()); // two more groups, on 5111 and 1111

        var all = await Recon.ListGroupsAsync(Clerk, new ReconcileGroupQuery());
        all.TotalRecords.Should().Be(4);
        all.Data[2].Uuid.Should().Be(partial.Uuid, "newest first");
        all.Data[3].Uuid.Should().Be(full.Uuid);

        var ar = await Recon.ListGroupsAsync(Clerk, new ReconcileGroupQuery { AccountUuid = _chart["1113"].Uuid });
        ar.Data.Select(g => g.Uuid).Should().Equal(partial.Uuid, full.Uuid);
        ar.Data[0].Should().Match<ReconcileGroupListItemModel>(g =>
            g.ReconcileType == ReconcileTypes.Partial && g.Amount == 300 && g.LineCount == 2 && g.Account.AccountCode == "1113"
            && g.GroupNumber == partial.GroupNumber && g.CreatedByName == "Cleo Clerk");
        ar.Data[1].ReconcileType.Should().Be(ReconcileTypes.Full);

        (await Recon.ListGroupsAsync(Clerk, new ReconcileGroupQuery { PartnerUuid = Acme })).Data.Select(g => g.Uuid).Should().Equal(full.Uuid);

        var paged = await Recon.ListGroupsAsync(Clerk, new ReconcileGroupQuery { Page = 2, PageSize = 3 });
        paged.Data.Should().ContainSingle().Which.Uuid.Should().Be(full.Uuid);
        paged.TotalPages.Should().Be(2);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        (await Recon.ListGroupsAsync(Clerk, new ReconcileGroupQuery { From = today, To = today })).TotalRecords.Should().Be(4);
        (await Recon.ListGroupsAsync(Clerk, new ReconcileGroupQuery { ToDate = today.AddDays(-1) })).TotalRecords.Should().Be(0);
        await FluentActions.Awaiting(() => Recon.ListGroupsAsync(Clerk, new ReconcileGroupQuery { FromDate = today, ToDate = today.AddDays(-1) }))
            .Should().ThrowAsync<BadRequestException>();

        // Other organization / no permission.
        var other = new AccountingCaller(Guid.NewGuid(), 9, AccountingCaller.AllGlCodes, "Other Org");
        (await Recon.ListGroupsAsync(other, new ReconcileGroupQuery())).TotalRecords.Should().Be(0);
        await FluentActions.Awaiting(() => Recon.ListGroupsAsync(other, new ReconcileGroupQuery { AccountUuid = _chart["1113"].Uuid }))
            .Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => Recon.ListGroupsAsync(new AccountingCaller(_h.Org, 11, [P.GL_VIEW], "Gil"), new ReconcileGroupQuery()))
            .Should().ThrowAsync<ForbiddenException>();
        (await Recon.ListGroupsAsync(new AccountingCaller(_h.Org, 10, [P.GL_RECONCILE_VIEW], "Val"), new ReconcileGroupQuery())).TotalRecords.Should().Be(4);
    }

    public void Dispose() => _h.Dispose();
}
