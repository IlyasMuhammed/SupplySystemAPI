using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Exceptions;
using Xunit;
using static SMS.Modules.Accounting.Tests.ReportTestKit;

namespace SMS.Modules.Accounting.Tests;

/// <summary>
/// A47 RPT C10 — the period close state machine (D-11) and the year-end close (D-18) over the in-memory store:
/// TS-PC-001..011, BR-PC-001..009 (tasks 374–384, 455).
/// </summary>
public sealed class PeriodCloseTests : IDisposable
{
    private readonly ReportTestKit _k = new();

    private static async Task<JournalConflictException> Conflict(Func<Task> act, string code) =>
        (await act.Should().ThrowAsync<JournalConflictException>().Where(e => e.ErrorCode == code)).Which;

    private async Task<FiscalPeriod> PeriodAsync(int number) =>
        await _k.H.Db.FiscalPeriods.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.FiscalYearId == _k.Year.Id && p.PeriodNumber == number);

    /// <summary>Revenue 1000 + other income 200 (credit), expenses 500 + 100 (debit) in 2026 → net income 600.</summary>
    private async Task SeedProfitAsync()
    {
        await _k.PostAsync(D(3, 1), _k.L("1113", 1000, 0), _k.L("4111", 0, 1000));
        await _k.PostAsync(D(6, 1), _k.L("1111", 200, 0), _k.L("4112", 0, 200));
        await _k.PostAsync(D(9, 1), _k.L("5111", 500, 0), _k.L("1111", 0, 500));
        await _k.PostAsync(D(11, 1), _k.L("5112", 100, 0), _k.L("1112", 0, 100));
    }

    // ── period transitions ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TS_PC_001_lock_a_period_without_drafts()
    {
        await _k.SetupAsync();
        var r = await _k.Close.ClosePeriodAsync(_k.Closer, await _k.PeriodUuidAsync(10), "Locked");
        r.OldStatus.Should().Be(PeriodStatuses.Open);
        r.NewStatus.Should().Be(PeriodStatuses.Locked);
        r.Period.Status.Should().Be(PeriodStatuses.Locked);
        r.Period.AllowedActions.Should().Equal(PeriodCloseActions.Reopen); // a closer cannot final-close
        (await PeriodAsync(10)).Status.Should().Be(PeriodStatuses.Locked);
        _k.H.Events.Of<FiscalPeriodStatusChangedEvent>().Should().ContainSingle(e => e.OldStatus == "Open" && e.NewStatus == "Locked");

        // Missing target = Locked.
        (await _k.Close.ClosePeriodAsync(_k.Closer, await _k.PeriodUuidAsync(9), null)).NewStatus.Should().Be(PeriodStatuses.Locked);
        // A locked period refuses regular postings (engine, D-11).
        (await FluentActions.Awaiting(() => _k.PostAsync(D(10, 5), _k.L("5112", 1, 0), _k.L("1111", 0, 1)))
            .Should().ThrowAsync<JournalValidationException>()).Which.ErrorCode.Should().Be(JournalErrorCodes.PeriodLocked);
    }

    [Fact]
    public async Task TS_PC_002_BR_PC_001_lock_or_adjustment_only_with_drafts_is_refused()
    {
        await _k.SetupAsync();
        await _k.EntryAsync(D(10, 3), false, [_k.L("5112", 10, 0), _k.L("1111", 0, 10)]);
        await _k.EntryAsync(D(10, 4), false, [_k.L("5112", 20, 0), _k.L("1111", 0, 20)]);
        var period = await _k.PeriodUuidAsync(10);

        (await Conflict(() => _k.Close.ClosePeriodAsync(_k.Closer, period, "Locked"), PeriodCloseErrorCodes.PeriodDrafts))
            .Message.Should().Be("Period has 2 unposted draft entries");
        await Conflict(() => _k.Close.ClosePeriodAsync(_k.Closer, period, "AdjustmentOnly"), PeriodCloseErrorCodes.PeriodDrafts);

        var overview = await _k.Close.GetPeriodStatusesAsync(_k.CloseViewer, _k.Year.Uuid);
        var oct = overview.Periods.Single(p => p.PeriodNumber == 10);
        oct.DraftEntryCount.Should().Be(2);
        oct.HasUnpostedDrafts.Should().BeTrue();
        // A draft of another period does not block this one.
        (await _k.Close.ClosePeriodAsync(_k.Closer, await _k.PeriodUuidAsync(9), "Locked")).NewStatus.Should().Be(PeriodStatuses.Locked);
    }

    [Fact]
    public async Task TS_PC_003_reopen_a_locked_period()
    {
        await _k.SetupAsync();
        await _k.SetPeriodAsync(10, PeriodStatuses.Locked);
        var r = await _k.Close.ReopenPeriodAsync(_k.Closer, await _k.PeriodUuidAsync(10));
        r.OldStatus.Should().Be(PeriodStatuses.Locked);
        r.NewStatus.Should().Be(PeriodStatuses.Open);
        // Reopen from Open is not a transition (PC-004).
        (await Conflict(() => _k.Close.ReopenPeriodAsync(_k.Closer, r.Period.Uuid), PeriodCloseErrorCodes.Transition))
            .Message.Should().Be("Cannot change a Open period to Open");
    }

    [Fact]
    public async Task TS_PC_004_BR_PC_002_a_closed_period_cannot_be_reopened()
    {
        await _k.SetupAsync();
        await _k.SetPeriodAsync(10, PeriodStatuses.Closed);
        var period = await _k.PeriodUuidAsync(10);
        (await Conflict(() => _k.Close.ReopenPeriodAsync(_k.Closer, period), PeriodCloseErrorCodes.ClosedNoReopen))
            .Message.Should().Be("Closed periods cannot be reopened");
        await Conflict(() => _k.Close.ClosePeriodAsync(_k.Closer, period, "Locked"), PeriodCloseErrorCodes.Transition);
    }

    [Fact]
    public async Task TS_PC_005_BR_PC_003_final_close_only_from_locked_and_irreversible()
    {
        await _k.SetupAsync();
        var period = await _k.PeriodUuidAsync(10);
        (await Conflict(() => _k.Close.FinalClosePeriodAsync(_k.Admin, period), PeriodCloseErrorCodes.OnlyLockedFinal))
            .Message.Should().Be("Only a Locked period can be closed finally");

        await _k.Close.ClosePeriodAsync(_k.Admin, period, "Locked");
        var r = await _k.Close.FinalClosePeriodAsync(_k.Admin, period);
        r.NewStatus.Should().Be(PeriodStatuses.Closed);
        r.Period.AllowedActions.Should().BeEmpty();
        await Conflict(() => _k.Close.ReopenPeriodAsync(_k.Admin, period), PeriodCloseErrorCodes.ClosedNoReopen);
        await Conflict(() => _k.Close.FinalClosePeriodAsync(_k.Admin, period), PeriodCloseErrorCodes.OnlyLockedFinal);
        (await FluentActions.Awaiting(() => _k.PostAsync(D(10, 5), _k.L("5112", 1, 0), _k.L("1111", 0, 1)))
            .Should().ThrowAsync<JournalValidationException>()).Which.ErrorCode.Should().Be(JournalErrorCodes.PeriodClosed);
    }

    [Fact]
    public async Task D_11_adjustment_only_transitions_and_postings()
    {
        await _k.SetupAsync();
        var period = await _k.PeriodUuidAsync(12);
        (await _k.Close.ClosePeriodAsync(_k.Closer, period, "adjustmentonly")).NewStatus.Should().Be(PeriodStatuses.AdjustmentOnly);

        // Regular entries refused, adjustment (closing-flagged) entries accepted.
        (await FluentActions.Awaiting(() => _k.PostAsync(D(12, 5), _k.L("5112", 1, 0), _k.L("1111", 0, 1)))
            .Should().ThrowAsync<JournalValidationException>()).Which.ErrorCode.Should().Be(JournalErrorCodes.AdjustmentOnly);
        (await _k.EntryAsync(D(12, 5), true, [_k.L("5112", 1, 0), _k.L("1111", 0, 1)], isClosingEntry: true)).Status.Should().Be(JournalStatuses.Posted);

        var overview = await _k.Close.GetPeriodStatusesAsync(_k.Closer, _k.Year.Uuid);
        overview.Periods.Single(p => p.PeriodNumber == 12).AllowedActions.Should().BeEquivalentTo([PeriodCloseActions.Reopen, PeriodCloseActions.Lock]);
        overview.Periods.Single(p => p.PeriodNumber == 1).AllowedActions.Should().BeEquivalentTo([PeriodCloseActions.Lock, PeriodCloseActions.AdjustmentOnly]);
        overview.Periods.Single(p => p.PeriodNumber == 12).PostedEntryCount.Should().Be(1);

        (await _k.Close.ReopenPeriodAsync(_k.Closer, period)).NewStatus.Should().Be(PeriodStatuses.Open);
        await _k.Close.ClosePeriodAsync(_k.Closer, period, "AdjustmentOnly");
        (await _k.Close.ClosePeriodAsync(_k.Closer, period, "Locked")).NewStatus.Should().Be(PeriodStatuses.Locked);
        // Locked → AdjustmentOnly is not a D-11 transition.
        (await Conflict(() => _k.Close.ClosePeriodAsync(_k.Closer, period, "AdjustmentOnly"), PeriodCloseErrorCodes.Transition))
            .Message.Should().Be("Cannot change a Locked period to AdjustmentOnly");
        await FluentActions.Awaiting(() => _k.Close.ClosePeriodAsync(_k.Closer, period, "Closed"))
            .Should().ThrowAsync<BadRequestException>().WithMessage(PeriodCloseMessages.TargetInvalid);
    }

    [Fact]
    public async Task Permissions_and_allowed_actions()
    {
        await _k.SetupAsync();
        var period = await _k.PeriodUuidAsync(10);
        await FluentActions.Awaiting(() => _k.Close.ClosePeriodAsync(_k.CloseViewer, period, "Locked")).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Awaiting(() => _k.Close.GetPeriodStatusesAsync(_k.GlViewerOnly, _k.Year.Uuid)).Should().ThrowAsync<ForbiddenException>();
        await _k.Close.ClosePeriodAsync(_k.Closer, period, "Locked");
        await FluentActions.Awaiting(() => _k.Close.FinalClosePeriodAsync(_k.Closer, period)).Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Awaiting(() => _k.Close.CloseYearAsync(_k.Closer, _k.Year.Uuid, null)).Should().ThrowAsync<ForbiddenException>();

        (await _k.Close.GetPeriodStatusesAsync(_k.CloseViewer, _k.Year.Uuid)).Periods.Should().OnlyContain(p => p.AllowedActions.Count == 0);
        (await _k.Close.GetPeriodStatusesAsync(_k.Admin, _k.Year.Uuid)).Periods.Single(p => p.PeriodNumber == 10)
            .AllowedActions.Should().BeEquivalentTo([PeriodCloseActions.Reopen, PeriodCloseActions.FinalClose]);

        var other = new AccountingCaller(Guid.NewGuid(), 1, AccountingCaller.AllGlCodes);
        await FluentActions.Awaiting(() => _k.Close.ClosePeriodAsync(other, period, "Locked")).Should().ThrowAsync<NotFoundException>();
        await FluentActions.Awaiting(() => _k.Close.GetPeriodStatusesAsync(other, _k.Year.Uuid)).Should().ThrowAsync<NotFoundException>();
    }

    // ── year-end close ─────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task TS_PC_009_preview_lists_the_closing_lines_and_net_income_without_posting()
    {
        await _k.SetupAsync();
        await SeedProfitAsync();
        var preview = await _k.Close.PreviewYearEndCloseAsync(_k.CloseViewer, _k.Year.Uuid, null);

        preview.TotalRevenue.Should().Be(1200m);
        preview.TotalExpenses.Should().Be(600m);
        preview.NetIncome.Should().Be(600m);
        preview.EntryDate.Should().Be(D(12, 31));
        preview.PeriodNumber.Should().Be(13);
        preview.RetainedEarningsAccount!.AccountCode.Should().Be("3111");
        preview.Lines.Select(l => (l.Account.AccountCode, l.Balance, l.ClosingDebit, l.ClosingCredit)).Should().Equal(
            ("4111", 1000m, 1000m, 0m), ("4112", 200m, 200m, 0m), ("5111", 500m, 0m, 500m), ("5112", 100m, 0m, 100m), ("3111", 0m, 0m, 600m));
        preview.Lines[^1].RootType.Should().Be(RootTypes.Equity);
        preview.CanExecute.Should().BeFalse("periods 1–12 are still open");
        preview.Blockers.Should().Equal(PeriodCloseMessages.AllPeriodsLocked);
        (await _k.H.Db.JournalEntries.IgnoreQueryFilters().CountAsync(j => j.IsClosingEntry)).Should().Be(0);

        await _k.LockMonthlyPeriodsAsync();
        (await _k.Close.PreviewYearEndCloseAsync(_k.CloseViewer, _k.Year.Uuid, null)).CanExecute.Should().BeTrue();
    }

    [Fact]
    public async Task TS_PC_006_BR_PC_005_006_year_end_close_posts_the_closing_entry_and_closes_the_year()
    {
        await _k.SetupAsync();
        await SeedProfitAsync();
        await _k.LockMonthlyPeriodsAsync();

        var r = await _k.Close.CloseYearAsync(_k.Admin, _k.Year.Uuid, null);
        r.AlreadyClosed.Should().BeFalse();
        r.TotalRevenueClosing.Should().Be(1200m);
        r.TotalExpenseClosing.Should().Be(600m);
        r.NetIncomeTransferred.Should().Be(600m);
        r.FiscalYear.IsClosed.Should().BeTrue();
        r.ClosingEntry!.EntryNumber.Should().Be("CLOSE/2026/0001");

        var je = await _k.H.Db.JournalEntries.IgnoreQueryFilters().AsNoTracking().Include(j => j.Lines).Include(j => j.JournalType).Include(j => j.FiscalPeriod)
            .SingleAsync(j => j.Uuid == r.ClosingEntry.Uuid);
        je.Status.Should().Be(JournalStatuses.Posted);
        je.IsClosingEntry.Should().BeTrue();
        je.JournalType.Code.Should().Be(SystemJournalTypes.Closing);
        je.EntryType.Should().Be(JournalEntryTypes.YearEndClose);
        je.IdempotencyKey.Should().Be(JournalKeys.YearClose(_k.Year.Uuid));
        je.EntryDate.Should().Be(D(12, 31));
        je.FiscalPeriod!.PeriodNumber.Should().Be(13);
        je.TotalDebit.Should().Be(1200m).And.Be(je.TotalCredit);
        je.Lines.Should().HaveCount(5);

        // Balances moved by the engine: P&L zeroed, retained earnings credited with the profit (normal direction).
        (await _k.CurrentBalanceAsync("4111")).Should().Be(0m);
        (await _k.CurrentBalanceAsync("4112")).Should().Be(0m);
        (await _k.CurrentBalanceAsync("5111")).Should().Be(0m);
        (await _k.CurrentBalanceAsync("5112")).Should().Be(0m);
        (await _k.CurrentBalanceAsync("3111")).Should().Be(600m);

        // BR-PC-006 — year closed, every period Closed.
        var year = await _k.H.Db.FiscalYears.IgnoreQueryFilters().AsNoTracking().Include(y => y.Periods).SingleAsync(y => y.Id == _k.Year.Id);
        year.IsClosed.Should().BeTrue();
        year.ClosedAt.Should().NotBeNull();
        year.ClosedBy.Should().Be(7);
        year.Periods.Should().HaveCount(13).And.OnlyContain(p => p.Status == PeriodStatuses.Closed);

        var evt = _k.H.Events.Of<FiscalYearClosedEvent>().Should().ContainSingle().Which;
        evt.NetIncome.Should().Be(600m);
        evt.ClosingEntryNumber.Should().Be("CLOSE/2026/0001");
        _k.H.Events.Of<JournalEntryPostedEvent>().Should().Contain(e => e.EntryNumber == "CLOSE/2026/0001");
        (await _k.H.Db.GlSetupHistory.IgnoreQueryFilters().CountAsync(h => h.EventType == nameof(FiscalYearClosedEvent))).Should().Be(1);

        // The trial balance: closing entries excluded by default (P&L still visible), included → P&L zero, still balanced.
        var tb = await _k.Reports.GenerateAsync(_k.Admin, new TrialBalanceQuery { FiscalYearUuid = _k.Year.Uuid });
        tb.Lines.Single(l => l.Account.AccountCode == "4111").Balance.Should().Be(-1000m);
        var withClose = await _k.Reports.GenerateAsync(_k.Admin, new TrialBalanceQuery { FiscalYearUuid = _k.Year.Uuid, IncludeClosingEntries = true });
        withClose.Lines.Where(l => l.RootType is RootTypes.Revenue or RootTypes.Expense).Should().OnlyContain(l => l.Balance == 0m);
        withClose.IsBalanced.Should().BeTrue();

        var overview = await _k.Close.GetPeriodStatusesAsync(_k.Admin, _k.Year.Uuid);
        overview.ClosingEntry!.Uuid.Should().Be(r.ClosingEntry.Uuid);
        overview.CanCloseYear.Should().BeFalse();
        overview.Periods.Should().OnlyContain(p => p.AllowedActions.Count == 0);
    }

    [Fact]
    public async Task REV_1_the_next_year_opens_with_the_close_applied_by_default()
    {
        await _k.SetupAsync();
        await SeedProfitAsync();
        await _k.LockMonthlyPeriodsAsync();
        await _k.Close.CloseYearAsync(_k.Admin, _k.Year.Uuid, null);

        // Default flags (closing entries excluded): the earlier year's close stays in the opening balance.
        var next = await _k.Reports.GenerateAsync(_k.Admin, new TrialBalanceQuery { FromDate = new DateOnly(2027, 1, 1), ToDate = new DateOnly(2027, 1, 31) });
        next.Lines.Where(l => l.RootType is RootTypes.Revenue or RootTypes.Expense).Should().NotBeEmpty()
            .And.OnlyContain(l => l.Balance == 0m && l.OpeningDebit == l.OpeningCredit);
        var re = next.Lines.Single(l => l.Account.AccountCode == "3111");
        re.OpeningCredit.Should().Be(600m);
        re.Balance.Should().Be(-600m);
        next.IsBalanced.Should().BeTrue();

        // The ledger drill-down with the trial balance's flag agrees.
        var ledger = await _k.Reports.GetAccountLedgerAsync(_k.Admin, _k.Chart["4111"].Uuid,
            new AccountLedgerQuery { FromDate = new DateOnly(2027, 1, 1), ToDate = new DateOnly(2027, 1, 31), IncludeClosingEntries = false });
        ledger.OpeningBalance.Should().Be(0m);

        // Inside the closed year the flag still hides its own close (BR-TB-004).
        var same = await _k.Reports.GenerateAsync(_k.Admin, new TrialBalanceQuery { FiscalYearUuid = _k.Year.Uuid });
        same.Lines.Single(l => l.Account.AccountCode == "4111").Balance.Should().Be(-1000m);
    }

    [Fact]
    public async Task TS_PC_007_BR_PC_004_an_open_period_blocks_the_year_end_close()
    {
        await _k.SetupAsync();
        await SeedProfitAsync();
        await _k.LockMonthlyPeriodsAsync();
        await _k.SetPeriodAsync(10, PeriodStatuses.Open);
        (await Conflict(() => _k.Close.CloseYearAsync(_k.Admin, _k.Year.Uuid, null), PeriodCloseErrorCodes.AllPeriodsLocked))
            .Message.Should().Be("All periods must be Locked or Closed");
        (await _k.H.Db.FiscalYears.IgnoreQueryFilters().AsNoTracking().SingleAsync(y => y.Id == _k.Year.Id)).IsClosed.Should().BeFalse();
        (await _k.H.Db.JournalEntries.IgnoreQueryFilters().CountAsync(j => j.IsClosingEntry)).Should().Be(0);
    }

    [Fact]
    public async Task TS_PC_008_BR_PC_009_a_second_close_returns_the_existing_result()
    {
        await _k.SetupAsync();
        await SeedProfitAsync();
        await _k.LockMonthlyPeriodsAsync();
        var first = await _k.Close.CloseYearAsync(_k.Admin, _k.Year.Uuid, null);
        _k.H.NewScope();
        var second = await _k.Close.CloseYearAsync(_k.Admin, _k.Year.Uuid, null);

        second.AlreadyClosed.Should().BeTrue();
        second.ClosingEntry!.Uuid.Should().Be(first.ClosingEntry!.Uuid);
        second.NetIncomeTransferred.Should().Be(600m);
        second.TotalRevenueClosing.Should().Be(1200m);
        second.TotalExpenseClosing.Should().Be(600m);
        second.RetainedEarningsAccount!.AccountCode.Should().Be("3111");
        (await _k.H.Db.JournalEntries.IgnoreQueryFilters().CountAsync(j => j.IsClosingEntry)).Should().Be(1);
        (await _k.CurrentBalanceAsync("3111")).Should().Be(600m, "posted once");

        var preview = await _k.Close.PreviewYearEndCloseAsync(_k.Admin, _k.Year.Uuid, null);
        preview.ExistingClosingEntry!.Uuid.Should().Be(first.ClosingEntry.Uuid);
        preview.Blockers.Should().Equal(PeriodCloseMessages.YearClosed);
        preview.NetIncome.Should().Be(600m);
        preview.Lines.Should().HaveCount(5);
    }

    [Fact]
    public async Task TS_PC_010_BR_PC_007_nothing_posts_to_a_closed_fiscal_year()
    {
        await _k.SetupAsync();
        await SeedProfitAsync();
        await _k.LockMonthlyPeriodsAsync();
        await _k.Close.CloseYearAsync(_k.Admin, _k.Year.Uuid, null);

        var refused = (await FluentActions.Awaiting(() => _k.EntryAsync(D(12, 31), true, [_k.L("5112", 1, 0), _k.L("1111", 0, 1)], isClosingEntry: true))
            .Should().ThrowAsync<JournalValidationException>()).Which;
        refused.ErrorCode.Should().Be(JournalErrorCodes.PeriodClosed);
        refused.Message.Should().Be("Fiscal year is closed");
        // Every period transition is refused too (PC-010).
        (await Conflict(() => _k.Close.ReopenPeriodAsync(_k.Admin, _k.Year.Periods.First().Uuid), PeriodCloseErrorCodes.YearClosed))
            .Message.Should().Be("Fiscal year is closed");
    }

    [Fact]
    public async Task TS_PC_011_a_net_loss_debits_retained_earnings()
    {
        await _k.SetupAsync();
        await _k.PostAsync(D(2, 1), _k.L("1113", 300, 0), _k.L("4111", 0, 300));
        await _k.PostAsync(D(4, 1), _k.L("5111", 800, 0), _k.L("2111", 0, 800));
        await _k.LockMonthlyPeriodsAsync();

        var r = await _k.Close.CloseYearAsync(_k.Admin, _k.Year.Uuid, null);
        r.NetIncomeTransferred.Should().Be(-500m);
        var lines = await _k.H.Db.JournalEntryLines.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.JournalEntry.Uuid == r.ClosingEntry!.Uuid).OrderBy(l => l.LineNumber).ToListAsync();
        lines.Select(l => (l.AccountId, l.DebitAmount, l.CreditAmount)).Should().Equal(
            (_k.Chart["4111"].Id, 300m, 0m), (_k.Chart["5111"].Id, 0m, 800m), (_k.Chart["3111"].Id, 500m, 0m));
        (await _k.CurrentBalanceAsync("3111")).Should().Be(-500m, "a debit lowers a credit-normal equity account");
    }

    [Fact]
    public async Task BR_PC_008_retained_earnings_must_be_an_active_level_4_equity_account()
    {
        await _k.SetupAsync();
        await SeedProfitAsync();
        await _k.LockMonthlyPeriodsAsync();

        (await FluentActions.Awaiting(() => _k.Close.CloseYearAsync(_k.Admin, _k.Year.Uuid, _k.Chart["4111"].Uuid))
            .Should().ThrowAsync<JournalValidationException>()).Which.ErrorCode.Should().Be(PeriodCloseErrorCodes.RetainedEarningsInvalid);
        (await FluentActions.Awaiting(() => _k.Close.CloseYearAsync(_k.Admin, _k.Year.Uuid, _k.Chart["31"].Uuid))
            .Should().ThrowAsync<JournalValidationException>()).Which.Message.Should().Be(PeriodCloseMessages.RetainedEarnings);
        (await FluentActions.Awaiting(() => _k.Close.CloseYearAsync(_k.Admin, _k.Year.Uuid, Guid.NewGuid()))
            .Should().ThrowAsync<JournalValidationException>()).Which.ErrorCode.Should().Be(PeriodCloseErrorCodes.RetainedEarningsInvalid);

        // No default and none given → PC-009.
        var d = await _k.H.Db.AccountingDefaults.IgnoreQueryFilters().SingleAsync(x => x.OrganizationId == _k.H.Org && x.DefaultKey == DefaultKeys.RetainedEarnings);
        d.AccountId = null;
        await _k.H.Db.SaveChangesAsync();
        (await FluentActions.Awaiting(() => _k.Close.CloseYearAsync(_k.Admin, _k.Year.Uuid, null))
            .Should().ThrowAsync<JournalValidationException>()).Which.ErrorCode.Should().Be(PeriodCloseErrorCodes.RetainedEarningsMissing);

        // An explicit equity leaf works without the default.
        var r = await _k.Close.CloseYearAsync(_k.Admin, _k.Year.Uuid, _k.Chart["3900"].Uuid);
        r.RetainedEarningsAccount!.AccountCode.Should().Be("3900");
        (await _k.CurrentBalanceAsync("3900")).Should().Be(600m);
    }

    [Fact]
    public async Task D_18_drafts_and_a_locked_period_13_block_the_close()
    {
        await _k.SetupAsync();
        await SeedProfitAsync();
        await _k.EntryAsync(D(12, 20), false, [_k.L("5112", 5, 0), _k.L("1111", 0, 5)]);
        await _k.LockMonthlyPeriodsAsync(); // the draft was written before the lock (a legacy state)
        await _k.SetPeriodAsync(13, PeriodStatuses.Locked);

        var preview = await _k.Close.PreviewYearEndCloseAsync(_k.Admin, _k.Year.Uuid, null);
        preview.Blockers.Should().Equal(PeriodCloseMessages.Period13, "The fiscal year has 1 unposted draft entries");
        (await Conflict(() => _k.Close.CloseYearAsync(_k.Admin, _k.Year.Uuid, null), PeriodCloseErrorCodes.Period13))
            .Message.Should().Be("Period 13 must be Open or AdjustmentOnly");
        await _k.SetPeriodAsync(13, PeriodStatuses.AdjustmentOnly);
        await Conflict(() => _k.Close.CloseYearAsync(_k.Admin, _k.Year.Uuid, null), PeriodCloseErrorCodes.YearDrafts);
    }

    [Fact]
    public async Task D_RPT_6_without_period_13_the_closing_entry_goes_to_locked_period_12()
    {
        await _k.SetupAsync(withPeriod13: false);
        await SeedProfitAsync();
        await _k.LockMonthlyPeriodsAsync();

        (await _k.Close.PreviewYearEndCloseAsync(_k.Admin, _k.Year.Uuid, null)).PeriodNumber.Should().Be(12);
        var r = await _k.Close.CloseYearAsync(_k.Admin, _k.Year.Uuid, null);
        var je = await _k.H.Db.JournalEntries.IgnoreQueryFilters().AsNoTracking().Include(j => j.FiscalPeriod).SingleAsync(j => j.Uuid == r.ClosingEntry!.Uuid);
        je.FiscalPeriod!.PeriodNumber.Should().Be(12);
        je.Status.Should().Be(JournalStatuses.Posted);
        (await _k.CurrentBalanceAsync("3111")).Should().Be(600m);
    }

    [Fact]
    public async Task D_RPT_8_a_year_without_profit_and_loss_closes_without_an_entry()
    {
        await _k.SetupAsync();
        await _k.PostAsync(D(5, 1), _k.L("1111", 100, 0), _k.L("3111", 0, 100));
        await _k.LockMonthlyPeriodsAsync();
        var r = await _k.Close.CloseYearAsync(_k.Admin, _k.Year.Uuid, null);
        r.ClosingEntry.Should().BeNull();
        r.NetIncomeTransferred.Should().Be(0m);
        r.FiscalYear.IsClosed.Should().BeTrue();
        _k.H.Events.Of<FiscalYearClosedEvent>().Single().ClosingEntryNumber.Should().BeNull();
        (await _k.Close.CloseYearAsync(_k.Admin, _k.Year.Uuid, null)).AlreadyClosed.Should().BeTrue();
    }

    [Fact]
    public async Task D_RPT_5_closing_flagged_adjustments_in_period_13_are_closed_too()
    {
        await _k.SetupAsync();
        await SeedProfitAsync();
        await _k.LockMonthlyPeriodsAsync();
        // A year-end accrual flagged as an adjustment entry, posted into period 13.
        await _k.EntryAsync(D(12, 31), true, [_k.L("5112", 50, 0), _k.L("2111", 0, 50)], isClosingEntry: true);

        var r = await _k.Close.CloseYearAsync(_k.Admin, _k.Year.Uuid, null);
        r.TotalExpenseClosing.Should().Be(650m);
        r.NetIncomeTransferred.Should().Be(550m);
        (await _k.CurrentBalanceAsync("5112")).Should().Be(0m);
    }

    [Fact]
    public async Task D_RPT_1_reversed_pairs_count_in_the_year_end_balances()
    {
        await _k.SetupAsync();
        await SeedProfitAsync();
        var wrong = await _k.PostAsync(D(7, 1), _k.L("5111", 70, 0), _k.L("1111", 0, 70));
        await _k.PostAsync(D(7, 2), _k.L("1111", 70, 0), _k.L("5111", 0, 70));
        var row = await _k.H.Db.JournalEntries.IgnoreQueryFilters().SingleAsync(j => j.Uuid == wrong.Uuid);
        row.Status = JournalStatuses.Reversed;
        await _k.H.Db.SaveChangesAsync();
        await _k.LockMonthlyPeriodsAsync();

        (await _k.Close.CloseYearAsync(_k.Admin, _k.Year.Uuid, null)).TotalExpenseClosing.Should().Be(600m);
        (await _k.CurrentBalanceAsync("5111")).Should().Be(0m);
    }

    [Fact]
    public void D_11_rule_table()
    {
        PeriodCloseRules.AllowedFrom(PeriodStatuses.Open).Should().BeEquivalentTo([PeriodStatuses.AdjustmentOnly, PeriodStatuses.Locked]);
        PeriodCloseRules.AllowedFrom(PeriodStatuses.AdjustmentOnly).Should().BeEquivalentTo([PeriodStatuses.Open, PeriodStatuses.Locked]);
        PeriodCloseRules.AllowedFrom(PeriodStatuses.Locked).Should().BeEquivalentTo([PeriodStatuses.Open, PeriodStatuses.Closed]);
        PeriodCloseRules.AllowedFrom(PeriodStatuses.Closed).Should().BeEmpty();
        PeriodCloseRules.IsAllowed(PeriodStatuses.Open, PeriodStatuses.Closed).Should().BeFalse();
        PeriodCloseRules.Actions(PeriodStatuses.Locked, canExecute: true, canAdminister: false).Should().Equal(PeriodCloseActions.Reopen);
        PeriodCloseRules.Actions(PeriodStatuses.Locked, canExecute: false, canAdminister: true).Should().Equal(PeriodCloseActions.FinalClose);
    }

    public void Dispose() => _k.Dispose();
}
