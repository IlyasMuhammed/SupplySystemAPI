using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;
using P = SMS.Shared.Authorization.PermissionCodes;

namespace SMS.Modules.Accounting.Tests;

/// <summary>
/// A47 CORE — the journal engine over the in-memory store: sequences (TS-SQ-001/002/003/005), create (TS-JE-001..015),
/// post (TS-JE-016..021), create-and-post, update/delete drafts, get/list, journal types (tasks 249/250), the period guard
/// (D-11) and the balance mover. TS-SQ-004 (concurrency) and the migration are on LocalDB (LocalDbJournalEngineTests).
/// </summary>
public sealed class JournalEngineTests : IDisposable
{
    private readonly AccountingHarness _h = new();
    private Dictionary<string, ChartOfAccount> _chart = null!;

    private AccountingCaller Clerk => new(_h.Org, 7, AccountingCaller.AllGlCodes, "Cleo Clerk");
    private IJournalEntryService Entries => _h.Get<IJournalEntryService>();
    private IJournalTypeService Types => _h.Get<IJournalTypeService>();

    private async Task SetupAsync()
    {
        _chart = await _h.SeedChartAsync(null, AccountingHarness.StandardLeaves);
        await _h.AddFiscalYearAsync();
        await _h.Get<JournalTypeSeeder>().EnsureAsync(_h.Org);
    }

    private async Task<Guid> TypeAsync(string code) =>
        (await _h.Db.JournalTypes.IgnoreQueryFilters().SingleAsync(t => t.OrganizationId == _h.Org && t.Code == code)).Uuid;

    private JournalEntryLineRequest L(string code, decimal debit, decimal credit, Guid? partner = null, string? partnerType = null) =>
        new() { AccountUuid = _chart[code].Uuid, Debit = debit, Credit = credit, PartnerUuid = partner, PartnerType = partnerType };

    private async Task<CreateJournalEntryRequest> RequestAsync(string type = SystemJournalTypes.Misc, DateOnly? date = null, params JournalEntryLineRequest[] lines) => new()
    {
        JournalTypeUuid = await TypeAsync(type),
        EntryDate = date ?? new DateOnly(2026, 10, 15),
        Description = "Test entry",
        Lines = lines.Length > 0 ? [.. lines] : [L("5111", 1000, 0), L("1111", 0, 1000)],
    };

    private async Task SetPeriodAsync(int number, string status)
    {
        var p = await _h.Db.FiscalPeriods.IgnoreQueryFilters().SingleAsync(x => x.OrganizationId == _h.Org && x.PeriodNumber == number);
        p.Status = status;
        await _h.Db.SaveChangesAsync();
    }

    private async Task<decimal> BalanceAsync(string code) =>
        (await _h.Db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == _chart[code].Id)).CurrentBalance;

    private static async Task<JournalValidationException> Refused(Func<Task> act) =>
        (await act.Should().ThrowAsync<JournalValidationException>()).Which;

    // ── sequences ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TS_SQ_001_002_003_numbers_per_type_and_year()
    {
        await SetupAsync();
        var p = Guid.NewGuid();
        (await Entries.CreateAsync(Clerk, await RequestAsync(SystemJournalTypes.Sale, null, L("1113", 100, 0, p, "Customer"), L("4111", 0, 100, p, "Customer"))))
            .EntryNumber.Should().Be("SALE/2026/0001");
        (await Entries.CreateAsync(Clerk, await RequestAsync(SystemJournalTypes.Sale, null, L("1113", 100, 0, p, "Customer"), L("4111", 0, 100, p, "Customer"))))
            .EntryNumber.Should().Be("SALE/2026/0002");
        (await Entries.CreateAsync(Clerk, await RequestAsync(SystemJournalTypes.Purchase, null, L("5111", 50, 0, p, "Vendor"), L("2111", 0, 50, p, "Vendor"))))
            .EntryNumber.Should().Be("PURCH/2026/0001");
        (await Entries.CreateAsync(Clerk, await RequestAsync())).EntryNumber.Should().Be("JV/2026/0001");
    }

    [Fact]
    public async Task TS_SQ_005_a_deleted_draft_leaves_a_gap()
    {
        await SetupAsync();
        var first = await Entries.CreateAsync(Clerk, await RequestAsync());
        await Entries.DeleteDraftAsync(Clerk, first.Uuid);
        (await Entries.CreateAsync(Clerk, await RequestAsync())).EntryNumber.Should().Be("JV/2026/0002");
    }

    [Fact]
    public void Sequence_format_grows_past_9999() =>
        JournalSequenceService.Format("JV", "2026", 12345).Should().Be("JV/2026/12345");

    [Fact]
    public async Task Outside_every_fiscal_year_a_legacy_draft_is_numbered_by_calendar_year()
    {
        await SetupAsync();
        var type = await _h.Db.JournalTypes.IgnoreQueryFilters().SingleAsync(t => t.OrganizationId == _h.Org && t.Code == "MISC");
        (await _h.Get<IJournalEntrySequenceService>().NextEntryNumberAsync(_h.Org, type.Id, null, new DateOnly(2030, 2, 1)))
            .Should().Be("JV/2030/0001");
        (await _h.Get<IJournalEntrySequenceService>().NextReconcileGroupNumberAsync(_h.Org, new DateOnly(2026, 5, 1))).Should().Be("REC/2026/0001");
    }

    // ── create (TS-JE-001..015) ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TS_JE_001_balanced_entry_is_a_draft_with_number_and_totals()
    {
        await SetupAsync();
        var e = await Entries.CreateAsync(Clerk, await RequestAsync());
        e.Status.Should().Be(JournalStatuses.Draft);
        e.EntryNumber.Should().Be("JV/2026/0001");
        e.TotalDebit.Should().Be(1000);
        e.TotalCredit.Should().Be(1000);
        e.LineCount.Should().Be(2);
        e.IsManual.Should().BeTrue();
        e.EntryType.Should().Be(JournalEntryTypes.ManualJournal);
        e.FiscalPeriod!.PeriodNumber.Should().Be(10);
        e.Lines.Select(l => l.AmountResidual).Should().Equal(1000m, -1000m);
        e.Lines.Should().OnlyContain(l => l.LineUuid != Guid.Empty && l.CurrencyCode == "PKR");
        e.CanPost.Should().BeTrue();
        e.CanEdit.Should().BeTrue();
        (await BalanceAsync("5111")).Should().Be(0, "a draft moves no balance");
    }

    [Fact]
    public async Task TS_JE_002_unbalanced_is_refused()
    {
        await SetupAsync();
        var ex = await Refused(async () => await Entries.CreateAsync(Clerk, await RequestAsync(lines: [L("5111", 1000, 0), L("1111", 0, 999)])));
        ex.ErrorCode.Should().Be(JournalErrorCodes.NotBalanced);
        ex.Message.Should().StartWith("Entry is not balanced");
    }

    [Fact]
    public async Task TS_JE_003_both_sides_on_a_line_is_refused()
    {
        await SetupAsync();
        var ex = await Refused(async () => await Entries.CreateAsync(Clerk, await RequestAsync(lines: [L("5111", 1000, 5), L("1111", 0, 995)])));
        ex.ErrorCode.Should().Be(JournalErrorCodes.BothSides);
        ex.Errors[0].LineNumber.Should().Be(1);
        ex.Message.Should().Be("Line 1: Line cannot have both debit and credit");
    }

    [Theory]
    [InlineData(PeriodStatuses.Closed, JournalErrorCodes.PeriodClosed, "Period is closed")]           // TS-JE-004
    [InlineData(PeriodStatuses.Locked, JournalErrorCodes.PeriodLocked, "Period is locked")]           // TS-JE-005
    [InlineData(PeriodStatuses.AdjustmentOnly, JournalErrorCodes.AdjustmentOnly, "Period accepts only adjustment entries")] // TS-JE-006
    public async Task TS_JE_004_005_006_period_status_refuses(string status, string code, string message)
    {
        await SetupAsync();
        await SetPeriodAsync(10, status);
        var ex = await Refused(async () => await Entries.CreateAsync(Clerk, await RequestAsync()));
        ex.ErrorCode.Should().Be(code);
        ex.Message.Should().Be(message);
    }

    [Fact]
    public async Task TS_JE_007_a_closing_entry_goes_into_an_adjustment_only_period()
    {
        await SetupAsync();
        await SetPeriodAsync(10, PeriodStatuses.AdjustmentOnly);
        var request = await RequestAsync();
        request.IsClosingEntry = true;
        var e = await Entries.CreateAsync(Clerk, request);
        e.IsClosingEntry.Should().BeTrue();
        e.Status.Should().Be(JournalStatuses.Draft);
    }

    [Fact]
    public async Task TS_JE_008_same_client_request_returns_the_existing_entry()
    {
        await SetupAsync();
        var request = await RequestAsync();
        request.ClientRequestId = Guid.NewGuid();
        var first = await Entries.CreateAsync(Clerk, request);
        var again = await Entries.CreateAsync(Clerk, request);
        again.Uuid.Should().Be(first.Uuid);
        (await _h.Db.JournalEntries.IgnoreQueryFilters().CountAsync(j => j.OrganizationId == _h.Org)).Should().Be(1);
    }

    [Fact]
    public async Task TS_JE_009_010_group_and_inactive_accounts_are_refused()
    {
        await SetupAsync();
        var group = await Refused(async () => await Entries.CreateAsync(Clerk, await RequestAsync(lines: [L("511", 10, 0), L("1111", 0, 10)])));
        group.ErrorCode.Should().Be(JournalErrorCodes.AccountNotPostable);
        group.Errors[0].Message.Should().Be("Account is not postable");

        var acc = await _h.Db.ChartOfAccounts.IgnoreQueryFilters().SingleAsync(a => a.Id == _chart["5111"].Id);
        acc.IsActive = false;
        await _h.Db.SaveChangesAsync();
        var inactive = await Refused(async () => await Entries.CreateAsync(Clerk, await RequestAsync()));
        inactive.ErrorCode.Should().Be(JournalErrorCodes.AccountInactive);
        inactive.Message.Should().Be("Line 1: Account is inactive");
    }

    [Fact]
    public async Task TS_JE_011_sales_entries_require_a_partner_on_manual_entries()
    {
        await SetupAsync();
        var ex = await Refused(async () => await Entries.CreateAsync(Clerk, await RequestAsync(SystemJournalTypes.Sale, null, L("1113", 10, 0), L("4111", 0, 10))));
        ex.ErrorCode.Should().Be(JournalErrorCodes.PartnerRequired);
        ex.Errors.Should().HaveCount(2).And.OnlyContain(e => e.Message == "Sales entries require a partner");

        var badType = await Refused(async () => await Entries.CreateAsync(Clerk, await RequestAsync(lines: [L("1113", 10, 0, Guid.NewGuid(), "Supplier"), L("4111", 0, 10)])));
        badType.Errors[0].Message.Should().Be("Partner type must be Customer or Vendor");
    }

    [Fact]
    public async Task TS_JE_012_a_single_line_is_refused()
    {
        await SetupAsync();
        var ex = await Refused(async () => await Entries.CreateAsync(Clerk, await RequestAsync(lines: [L("5111", 0, 0)])));
        ex.Errors.Select(e => e.ErrorCode).Should().Contain(JournalErrorCodes.TooFewLines);
        ex.Errors.Should().Contain(e => e.Message == "Minimum 2 lines required");
    }

    [Fact]
    public async Task TS_JE_013_date_outside_every_period_is_refused()
    {
        await SetupAsync();
        var ex = await Refused(async () => await Entries.CreateAsync(Clerk, await RequestAsync(date: new DateOnly(2031, 1, 1))));
        ex.ErrorCode.Should().Be(JournalErrorCodes.NoPeriod);
        ex.Message.Should().Be("No fiscal period covers this date");
    }

    [Fact]
    public async Task TS_JE_014_015_foreign_currency_needs_a_rate_and_converts()
    {
        await SetupAsync();
        var noRate = await RequestAsync();
        noRate.CurrencyCode = "USD";
        (await Refused(() => Entries.CreateAsync(Clerk, noRate))).Message.Should().Be("Exchange rate required for foreign currency");

        var request = await RequestAsync(lines: [L("5111", 100, 0), L("1111", 0, 100)]);
        request.CurrencyCode = "usd";
        request.ExchangeRate = 3.67m;
        var e = await Entries.CreateAsync(Clerk, request);
        e.CurrencyCode.Should().Be("USD");
        e.ExchangeRate.Should().Be(3.67m);
        e.TotalDebit.Should().Be(367m);
        e.Lines[0].Debit.Should().Be(367m);
        e.Lines[0].AmountCurrency.Should().Be(100m);
        e.Lines[1].Credit.Should().Be(367m);
        e.Lines[1].AmountCurrency.Should().Be(-100m);
        e.Lines[0].AmountResidual.Should().Be(367m, "residuals are base amounts");
    }

    [Fact]
    public async Task An_inactive_journal_type_takes_no_entries_and_the_opening_type_forces_the_flag()
    {
        await SetupAsync();
        await Types.SetActiveAsync(Clerk, await TypeAsync(SystemJournalTypes.Cash), false);
        (await Refused(async () => await Entries.CreateAsync(Clerk, await RequestAsync(SystemJournalTypes.Cash)))).Message.Should().Be("Journal type is inactive");

        var open = await Entries.CreateAsync(Clerk, await RequestAsync(SystemJournalTypes.Opening, null, L("1111", 10, 0), L("3111", 0, 10)));
        open.IsOpeningEntry.Should().BeTrue();
        open.Status.Should().Be(JournalStatuses.Posted, "the Opening type auto-posts");
    }

    [Theory]
    [InlineData("MISC")]
    [InlineData("OPEN")]
    [InlineData("CLOSE")]
    public async Task REV_6_misc_open_and_close_cannot_be_deactivated(string code)
    {
        await SetupAsync();
        (await FluentActions.Awaiting(async () => await Types.SetActiveAsync(Clerk, await TypeAsync(code), false)).Should().ThrowAsync<ConflictException>())
            .Which.Message.Should().Be($"The {code} journal type cannot be deactivated: system entries (fallback, opening and year-end entries) are written to it.");
        (await Types.SetActiveAsync(Clerk, await TypeAsync("BANK"), false)).IsActive.Should().BeFalse("SALE/PURCH/CASH/BANK fall back to MISC");
    }

    [Fact]
    public async Task REV_10_posting_a_draft_reversal_reverses_and_reconciles_its_original()
    {
        await SetupAsync();
        var sale = await _h.Db.JournalTypes.IgnoreQueryFilters().SingleAsync(t => t.OrganizationId == _h.Org && t.Code == "SALE");
        var writer = _h.Get<JournalEntryWriter>();
        JournalDraft Draft(string key, int? reverses, decimal dr1113, decimal cr1113) => new()
        {
            OrganizationId = _h.Org, JournalTypeId = sale.Id, EntryDate = new DateOnly(2026, 10, 1), EntryType = "SALES_INVOICE",
            SourceModule = "finance", IdempotencyKey = key, AmountsInBase = true, ReversesEntryId = reverses,
            Lines = [new JournalDraftLine { AccountId = _chart["1113"].Id, Debit = dr1113, Credit = cr1113 },
                     new JournalDraftLine { AccountId = _chart["4111"].Id, Debit = cr1113, Credit = dr1113 }],
        };
        var original = (await writer.CreateAsync(Draft("finance:SalesInvoice:r10", null, 118, 0), post: true)).Entry;
        await _h.Db.SaveChangesAsync();
        var draft = (await writer.CreateAsync(Draft("finance:SalesInvoice:r10", original.Id, 0, 118), post: false)).Entry;
        await _h.Db.SaveChangesAsync();
        var (originalUuid, draftUuid) = (original.Uuid, draft.Uuid);
        _h.NewScope();
        (await Entries.GetByIdAsync(Clerk, originalUuid)).Status.Should().Be(JournalStatuses.Posted, "a draft reversal moved nothing yet");

        await Entries.PostAsync(Clerk, draftUuid);
        _h.NewScope();
        var o = await Entries.GetByIdAsync(Clerk, originalUuid);
        o.Status.Should().Be(JournalStatuses.Reversed);
        o.ReversedByEntry!.Uuid.Should().Be(draftUuid);
        o.Lines.Should().OnlyContain(l => l.Reconciled && l.AmountResidual == 0 && l.ReconcileGroupNumber != null);
        (await Entries.GetByIdAsync(Clerk, draftUuid)).Lines.Should().OnlyContain(l => l.Reconciled);
        (await BalanceAsync("1113")).Should().Be(0);
        (await BalanceAsync("4111")).Should().Be(0);
    }

    [Fact]
    public async Task REV_2_base_totals_must_balance_exactly()
    {
        await SetupAsync();
        var ex = await Refused(async () => await Entries.CreateAsync(Clerk, await RequestAsync(lines: [L("5111", 100.0049m, 0), L("1111", 0, 100m)])));
        ex.ErrorCode.Should().Be(JournalErrorCodes.NotBalanced);
        (await Entries.CreateAsync(Clerk, await RequestAsync(lines: [L("5111", 100.0049m, 0), L("1111", 0, 100.0049m)]))).TotalDebit.Should().Be(100.0049m);
    }

    [Fact]
    public async Task REV_7_opening_and_year_end_entries_are_not_reversible()
    {
        await SetupAsync();
        var open = await Entries.CreateAsync(Clerk, await RequestAsync(SystemJournalTypes.Opening, null, L("1111", 10, 0), L("3111", 0, 10)));
        open.CanReverse.Should().BeTrue("a manual Opening-type entry is not the A42 opening balance journal");
        var row = await _h.Db.JournalEntries.IgnoreQueryFilters().SingleAsync(j => j.Uuid == open.Uuid);
        row.EntryType = JournalEntryTypes.OpeningBalance;
        await _h.Db.SaveChangesAsync();
        _h.NewScope();
        (await Entries.GetByIdAsync(Clerk, open.Uuid)).CanReverse.Should().BeFalse();
        var r2 = await _h.Db.JournalEntries.IgnoreQueryFilters().SingleAsync(j => j.Uuid == open.Uuid);
        r2.EntryType = JournalEntryTypes.YearEndClose;
        await _h.Db.SaveChangesAsync();
        _h.NewScope();
        (await Entries.GetByIdAsync(Clerk, open.Uuid)).CanReverse.Should().BeFalse();
    }

    [Fact]
    public async Task Creating_needs_the_create_code_and_posting_at_once_the_post_code()
    {
        await SetupAsync();
        var viewer = new AccountingCaller(_h.Org, 2, [P.GL_VIEW], "Vic");
        await FluentActions.Awaiting(async () => await Entries.CreateAsync(viewer, await RequestAsync())).Should().ThrowAsync<ForbiddenException>();
        var creator = new AccountingCaller(_h.Org, 3, [P.GL_JOURNAL_CREATE], "Cy");
        var request = await RequestAsync();
        request.PostImmediately = true;
        await FluentActions.Awaiting(() => Entries.CreateAsync(creator, request)).Should().ThrowAsync<ForbiddenException>();
        request.PostImmediately = false;
        (await Entries.CreateAsync(creator, request)).CanPost.Should().BeFalse();
    }

    // ── post (TS-JE-016..021) ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TS_JE_016_020_021_posting_moves_balances_and_marks_accounts()
    {
        await SetupAsync();
        var draft = await Entries.CreateAsync(Clerk, await RequestAsync(lines: [L("5111", 1000, 0), L("4111", 0, 1000)]));
        var posted = await Entries.PostAsync(Clerk, draft.Uuid);
        posted.Status.Should().Be(JournalStatuses.Posted);
        posted.PostedAt.Should().NotBeNull();
        posted.PostedByName.Should().Be("Cleo Clerk");
        // TS-JE-020 — the A42 normal direction: the expense (debit-normal) and the revenue (credit-normal) both go up.
        (await BalanceAsync("5111")).Should().Be(1000);
        (await BalanceAsync("4111")).Should().Be(1000);
        // TS-JE-021
        (await _h.Db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking().Where(a => a.Id == _chart["5111"].Id || a.Id == _chart["4111"].Id)
            .AllAsync(a => a.HasTransactions)).Should().BeTrue();
        _h.Events.Of<JournalEntryPostedEvent>().Should().ContainSingle(e => e.EntryNumber == draft.EntryNumber);
    }

    [Fact]
    public async Task TS_JE_017_018_posted_and_reversed_entries_cannot_be_posted()
    {
        await SetupAsync();
        var e = await Entries.CreateAsync(Clerk, await RequestAsync());
        await Entries.PostAsync(Clerk, e.Uuid);
        (await FluentActions.Awaiting(() => Entries.PostAsync(Clerk, e.Uuid)).Should().ThrowAsync<JournalConflictException>())
            .Which.Message.Should().Be("Entry is already posted");

        var row = await _h.Db.JournalEntries.IgnoreQueryFilters().SingleAsync(j => j.Uuid == e.Uuid);
        row.Status = JournalStatuses.Reversed;
        await _h.Db.SaveChangesAsync();
        _h.NewScope();
        (await FluentActions.Awaiting(() => Entries.PostAsync(Clerk, e.Uuid)).Should().ThrowAsync<JournalConflictException>())
            .Which.Message.Should().Be("Entry has been reversed");
    }

    [Fact]
    public async Task TS_JE_019_period_locked_after_creation_refuses_the_post()
    {
        await SetupAsync();
        var e = await Entries.CreateAsync(Clerk, await RequestAsync());
        await SetPeriodAsync(10, PeriodStatuses.Locked);
        _h.NewScope();
        (await Refused(() => Entries.PostAsync(Clerk, e.Uuid))).Message.Should().Be("Period is locked");
        (await BalanceAsync("5111")).Should().Be(0);
    }

    [Fact]
    public async Task TS_PC_010_a_closed_fiscal_year_refuses_entries()
    {
        await SetupAsync();
        var y = await _h.Db.FiscalYears.IgnoreQueryFilters().SingleAsync(x => x.OrganizationId == _h.Org);
        y.IsClosed = true;
        await _h.Db.SaveChangesAsync();
        (await Refused(async () => await Entries.CreateAsync(Clerk, await RequestAsync()))).Message.Should().Be("Fiscal year is closed");
    }

    [Fact]
    public async Task Task_207_create_and_post_in_one_step()
    {
        await SetupAsync();
        var e = await Entries.CreateAndPostAsync(Clerk, await RequestAsync());
        e.Status.Should().Be(JournalStatuses.Posted);
        (await BalanceAsync("1111")).Should().Be(-1000);
    }

    // ── update / delete (tasks 215–218) ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Task_215_216_update_a_draft_but_not_a_posted_entry()
    {
        await SetupAsync();
        var e = await Entries.CreateAsync(Clerk, await RequestAsync());
        var updated = await Entries.UpdateDraftAsync(Clerk, e.Uuid, new UpdateJournalEntryRequest
        {
            EntryDate = new DateOnly(2026, 11, 2), Description = "Changed",
            Lines = [L("5111", 300, 0), L("5111", 200, 0), L("1111", 0, 500)],
        });
        updated.EntryNumber.Should().Be(e.EntryNumber, "BR-JE-010");
        updated.TotalDebit.Should().Be(500);
        updated.LineCount.Should().Be(3);
        updated.FiscalPeriod!.PeriodNumber.Should().Be(11);
        updated.ModifiedByName.Should().Be("Cleo Clerk");

        await Entries.PostAsync(Clerk, e.Uuid);
        (await FluentActions.Awaiting(() => Entries.UpdateDraftAsync(Clerk, e.Uuid, new UpdateJournalEntryRequest
        {
            EntryDate = new DateOnly(2026, 11, 2), Lines = [L("5111", 1, 0), L("1111", 0, 1)],
        })).Should().ThrowAsync<JournalConflictException>()).Which.Message.Should().Be("Posted entries cannot be modified");
    }

    [Fact]
    public async Task Task_217_218_delete_a_draft_but_not_a_posted_entry()
    {
        await SetupAsync();
        var d = await Entries.CreateAsync(Clerk, await RequestAsync());
        await Entries.DeleteDraftAsync(Clerk, d.Uuid);
        (await _h.Db.JournalEntryLines.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        await FluentActions.Awaiting(() => Entries.GetByIdAsync(Clerk, d.Uuid)).Should().ThrowAsync<NotFoundException>();

        var p = await Entries.CreateAndPostAsync(Clerk, await RequestAsync());
        (await FluentActions.Awaiting(() => Entries.DeleteDraftAsync(Clerk, p.Uuid)).Should().ThrowAsync<JournalConflictException>())
            .Which.Message.Should().Be("Posted entries cannot be deleted");
    }

    [Fact]
    public async Task A_source_document_draft_can_only_be_posted()
    {
        await SetupAsync();
        var type = await _h.Db.JournalTypes.IgnoreQueryFilters().SingleAsync(t => t.OrganizationId == _h.Org && t.Code == "SALE");
        var writer = _h.Get<JournalEntryWriter>();
        var result = await writer.CreateAsync(new JournalDraft
        {
            OrganizationId = _h.Org, JournalTypeId = type.Id, EntryDate = new DateOnly(2026, 10, 1), EntryType = "SALES_INVOICE",
            SourceModule = "finance", SourceDocumentUuid = Guid.NewGuid(), IdempotencyKey = "finance:SalesInvoice:x", AmountsInBase = true,
            Lines = [new JournalDraftLine { AccountId = _chart["1113"].Id, Debit = 50 }, new JournalDraftLine { AccountId = _chart["4111"].Id, Credit = 50 }],
        }, post: false);
        await _h.Db.SaveChangesAsync();
        var uuid = result.Entry.Uuid;
        _h.NewScope();
        (await FluentActions.Awaiting(() => Entries.DeleteDraftAsync(Clerk, uuid)).Should().ThrowAsync<JournalConflictException>())
            .Which.Message.Should().Be(JournalMessages.SourceDraft);
        (await Entries.PostAsync(Clerk, uuid)).Status.Should().Be(JournalStatuses.Posted, "R-CORE-1: a gateway draft is reviewed and posted");
    }

    // ── get / list (tasks 225–228) ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Task_225_to_228_get_and_list_with_filters_and_pages()
    {
        await SetupAsync();
        for (var i = 1; i <= 5; i++)
        {
            var r = await RequestAsync(date: new DateOnly(2026, i, 10), lines: [L("5111", i * 10, 0), L("1111", 0, i * 10)]);
            var e = await Entries.CreateAsync(Clerk, r);
            if (i % 2 == 1) await Entries.PostAsync(Clerk, e.Uuid);
        }
        var detail = await Entries.GetByIdAsync(Clerk, (await Entries.ListAsync(Clerk, new JournalEntryQuery())).Data[0].Uuid);
        detail.Lines.Should().HaveCount(2);
        detail.Lines[0].Account.AccountCode.Should().Be("5111");

        (await Entries.ListAsync(Clerk, new JournalEntryQuery { Status = "posted" })).TotalRecords.Should().Be(3);
        (await Entries.ListAsync(Clerk, new JournalEntryQuery { FromDate = new DateOnly(2026, 2, 1), ToDate = new DateOnly(2026, 3, 31) }))
            .Data.Select(d => d.EntryDate.Month).Should().Equal(3, 2);
        var page = await Entries.ListAsync(Clerk, new JournalEntryQuery { Page = 2, PageSize = 2, SortBy = "entryDate", SortDirection = "asc" });
        page.TotalRecords.Should().Be(5);
        page.TotalPages.Should().Be(3);
        page.Data.Select(d => d.EntryDate.Month).Should().Equal(3, 4);
        (await Entries.ListAsync(Clerk, new JournalEntryQuery { SourceModule = "manual", Search = "JV/2026/000" })).TotalRecords.Should().Be(5);
        await FluentActions.Awaiting(() => Entries.ListAsync(Clerk, new JournalEntryQuery { Status = "Lost" })).Should().ThrowAsync<BadRequestException>();
        (await Entries.ListAsync(new AccountingCaller(Guid.NewGuid(), 9, AccountingCaller.AllGlCodes), new JournalEntryQuery())).TotalRecords
            .Should().Be(0, "another organization sees none");
    }

    // ── journal types (tasks 016–026, 249, 250) ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Seven_system_types_are_seeded_once()
    {
        await SetupAsync();
        (await _h.Get<JournalTypeSeeder>().EnsureAsync(_h.Org)).Should().Be(0);
        var types = await Types.ListAsync(Clerk);
        types.Select(t => (t.Code, t.SequencePrefix, t.Type, t.RequiresPartner, t.AutoPost)).Should().Equal(
            ("SALE", "SALE", "Sale", true, false), ("PURCH", "PURCH", "Purchase", true, false), ("CASH", "CASH", "Cash", false, false),
            ("BANK", "BANK", "Bank", false, false), ("MISC", "JV", "Miscellaneous", false, false), ("OPEN", "OPEN", "Opening", false, true),
            ("CLOSE", "CLOSE", "Closing", false, true));
        types.Should().OnlyContain(t => t.IsSystem && t.IsActive && !t.CanDelete);
    }

    [Fact]
    public async Task Setup_completion_seeds_the_types()
    {
        var org = Guid.NewGuid();
        await _h.Get<IAccountingEventPublisher>().PublishAsync(new CoaSetupCompletedEvent(org, DateTime.UtcNow, 1, 10));
        (await _h.Db.JournalTypes.IgnoreQueryFilters().CountAsync(t => t.OrganizationId == org)).Should().Be(7);
    }

    [Fact]
    public async Task Task_249_250_system_types_are_not_deleted_and_opening_closing_are_single()
    {
        await SetupAsync();
        (await FluentActions.Awaiting(async () => await Types.DeleteAsync(Clerk, await TypeAsync("SALE"))).Should().ThrowAsync<ConflictException>())
            .Which.Message.Should().Be("System journal types cannot be deleted, only deactivated.");
        (await FluentActions.Awaiting(() => Types.CreateAsync(Clerk, new CreateJournalTypeRequest { Code = "OPEN2", Name = "Second opening", Type = "Opening" }))
            .Should().ThrowAsync<ConflictException>()).Which.Message.Should().Be("An organization can have only one Opening journal type.");
        (await FluentActions.Awaiting(() => Types.CreateAsync(Clerk, new CreateJournalTypeRequest { Code = "X", Name = "Clash", Type = "Miscellaneous", SequencePrefix = "jv" }))
            .Should().ThrowAsync<ConflictException>()).Which.Message.Should().Be("Sequence prefix JV is already used by journal type MISC.");

        var proj = await Types.CreateAsync(Clerk, new CreateJournalTypeRequest { Code = "proj", Name = "Project Billing", Type = "sale" });
        proj.Code.Should().Be("PROJ");
        proj.SequencePrefix.Should().Be("PROJ");
        proj.CanDelete.Should().BeTrue();
        await Types.DeleteAsync(Clerk, proj.Uuid);
        await FluentActions.Awaiting(() => Types.GetAsync(Clerk, proj.Uuid)).Should().ThrowAsync<NotFoundException>();

        await FluentActions.Awaiting(async () => await Types.UpdateAsync(Clerk, await TypeAsync("SALE"),
                new UpdateJournalTypeRequest { Code = "SALES", Name = "Sales", Type = "Sale", SequencePrefix = "SALE", RequiresPartner = true }))
            .Should().ThrowAsync<BadRequestException>();
        await FluentActions.Awaiting(() => Types.CreateAsync(Clerk, new CreateJournalTypeRequest { Code = "BAD-1", Name = "x", Type = "Sale" }))
            .Should().ThrowAsync<BadRequestException>().WithMessage("The code must be 1–10 letters or digits.");
    }

    [Fact]
    public async Task A_used_prefix_and_a_used_custom_type_are_kept()
    {
        await SetupAsync();
        var proj = await Types.CreateAsync(Clerk, new CreateJournalTypeRequest { Code = "PROJ", Name = "Project", Type = "Miscellaneous" });
        var request = await RequestAsync();
        request.JournalTypeUuid = proj.Uuid;
        (await Entries.CreateAsync(Clerk, request)).EntryNumber.Should().Be("PROJ/2026/0001");
        await FluentActions.Awaiting(() => Types.UpdateAsync(Clerk, proj.Uuid, new UpdateJournalTypeRequest { Code = "PROJ", Name = "Project", Type = "Miscellaneous", SequencePrefix = "PRJ" }))
            .Should().ThrowAsync<ConflictException>().WithMessage("The sequence prefix cannot be changed once entries are numbered with it.");
        await FluentActions.Awaiting(() => Types.DeleteAsync(Clerk, proj.Uuid))
            .Should().ThrowAsync<ConflictException>().WithMessage("A journal type with entries cannot be deleted; deactivate it instead.");
    }

    // ── period guard (D-11) and resolver (D-12) ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Guard_accepts_adjustment_entries_in_adjustment_only_periods_only()
    {
        await SetupAsync();
        var guard = _h.Get<IFiscalPeriodGuard>();
        var date = new DateOnly(2026, 10, 5);
        (await guard.IsPostingAllowedAsync(_h.Org, date)).Should().BeTrue();
        await SetPeriodAsync(10, PeriodStatuses.AdjustmentOnly);
        (await guard.IsPostingAllowedAsync(_h.Org, date)).Should().BeFalse();
        (await guard.IsPostingAllowedAsync(_h.Org, date, isAdjustmentEntry: true)).Should().BeTrue();
        await SetPeriodAsync(10, PeriodStatuses.Locked);
        (await guard.IsPostingAllowedAsync(_h.Org, date, isAdjustmentEntry: true)).Should().BeFalse();
    }

    [Fact]
    public async Task Year_end_day_prefers_period_12_for_regular_and_13_for_closing_entries()
    {
        await SetupAsync();
        var year = await _h.Db.FiscalYears.IgnoreQueryFilters().SingleAsync(y => y.OrganizationId == _h.Org);
        _h.Db.FiscalPeriods.Add(new FiscalPeriod { OrganizationId = _h.Org, FiscalYearId = year.Id, PeriodNumber = 13, PeriodName = "Adj",
            StartDate = new DateOnly(2026, 12, 31), EndDate = new DateOnly(2026, 12, 31), Status = PeriodStatuses.AdjustmentOnly });
        await _h.Db.SaveChangesAsync();
        var resolver = _h.Get<JournalPeriodResolver>();
        var periods = await _h.Db.FiscalPeriods.IgnoreQueryFilters().Where(p => p.OrganizationId == _h.Org).ToDictionaryAsync(p => p.Id, p => p.PeriodNumber);
        var day = new DateOnly(2026, 12, 31);
        periods[(await resolver.ResolveAsync(_h.Org, day, false, false)).FiscalPeriodId!.Value].Should().Be(12);
        periods[(await resolver.ResolveAsync(_h.Org, day, false, true)).FiscalPeriodId!.Value].Should().Be(13);
        await SetPeriodAsync(12, PeriodStatuses.Locked);
        (await resolver.ResolveAsync(_h.Org, day, false, false)).ErrorCode.Should().Be(JournalErrorCodes.PeriodLocked);
        (await resolver.ResolveAsync(_h.Org, day, false, true)).Allowed.Should().BeTrue();
        // REV-4 — an Open period 13 still takes no regular entry.
        await SetPeriodAsync(13, PeriodStatuses.Open);
        (await resolver.ResolveAsync(_h.Org, day, false, false)).ErrorCode.Should().Be(JournalErrorCodes.PeriodLocked);
        (await _h.Get<IFiscalPeriodGuard>().IsPostingAllowedAsync(_h.Org, day)).Should().BeFalse();
    }

    [Theory]
    [InlineData("SALES_INVOICE", false, "SALE")]
    [InlineData("POS_RETURN", false, "SALE")]
    [InlineData("VENDOR_CREDIT", false, "PURCH")]
    [InlineData("PAYMENT", true, "CASH")]
    [InlineData("BILL_PAYMENT", false, "BANK")]
    [InlineData("INVENTORY_ADJUSTMENT", false, "MISC")]
    [InlineData("MANUAL_JOURNAL", false, "MISC")]
    [InlineData("OPENING_BALANCE", false, "OPEN")]
    [InlineData("YEAR_END_CLOSE", false, "CLOSE")]
    public void D12_resolver_map(string entryType, bool cash, string code) =>
        SystemJournalTypes.ForEntryType(entryType, cash).Should().Be(code);

    [Fact]
    public async Task D12_cash_by_the_default_cash_account_and_inactive_falls_back_to_misc()
    {
        await SetupAsync();
        await _h.AssignRequiredDefaultsAsync(_chart);
        var resolver = _h.Get<JournalTypeResolver>();
        (await resolver.ResolveAsync(_h.Org, "PAYMENT", [_chart["1111"].Id, _chart["1113"].Id])).Code.Should().Be("CASH");
        (await resolver.ResolveAsync(_h.Org, "PAYMENT", [_chart["1112"].Id, _chart["1113"].Id])).Code.Should().Be("BANK");
        await Types.SetActiveAsync(Clerk, await TypeAsync("SALE"), false);
        (await resolver.ResolveAsync(_h.Org, "SALES_INVOICE", [])).Code.Should().Be("MISC");
    }

    // ── balance mover (tasks 121–122 instead of the trigger, D-10) ────────────────────────────────────────────────

    [Fact]
    public async Task Balance_mover_marks_drafts_and_moves_only_posts()
    {
        await SetupAsync();
        var mover = _h.Get<GlBalanceMover>();
        await mover.MoveAsync([(_chart["3111"].Id, RootTypes.Equity, 0m, 40m)], moveBalance: false);
        await _h.Db.SaveChangesAsync();
        var equity = await _h.Db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == _chart["3111"].Id);
        equity.HasTransactions.Should().BeTrue();
        equity.CurrentBalance.Should().Be(0);
        await mover.MoveAsync([(_chart["3111"].Id, RootTypes.Equity, 0m, 40m), (_chart["3111"].Id, RootTypes.Equity, 10m, 0m)], moveBalance: true);
        await _h.Db.SaveChangesAsync();
        (await BalanceAsync("3111")).Should().Be(30, "credit-normal: credit − debit, grouped per account");
    }

    public void Dispose() => _h.Dispose();
}
