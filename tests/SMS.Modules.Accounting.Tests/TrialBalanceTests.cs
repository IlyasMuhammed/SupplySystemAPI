using System.Text;
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
/// A47 RPT C9 — trial balance, account balance, account ledger over the in-memory store (TS-TB-001..006, BR-TB-001..005,
/// tasks 336–341, 456). The SQL translation and the 10K-entry timing are in LocalDbReportTests.
/// </summary>
public sealed class TrialBalanceTests : IDisposable
{
    private readonly ReportTestKit _k = new();

    private Task<TrialBalanceReportModel> TbAsync(DateOnly from, DateOnly to, bool opening = true, bool closing = false, bool zero = false,
        string? costCenter = null) =>
        _k.Reports.GenerateAsync(_k.Admin, new TrialBalanceQuery
        {
            FromDate = from, ToDate = to, IncludeOpeningBalances = opening, IncludeClosingEntries = closing, IncludeZeroBalances = zero, CostCenter = costCenter,
        });

    private static TrialBalanceLineModel Line(TrialBalanceReportModel tb, string code) => tb.Lines.Single(l => l.Account.AccountCode == code);

    /// <summary>Sep: capital 5000 (cash/equity); Oct: sale 1000 (AR/revenue), expense 300 (expense/cash), receipt 400 (cash/AR).</summary>
    private async Task SeedActivityAsync()
    {
        await _k.SetupAsync();
        await _k.PostAsync(D(9, 5), _k.L("1111", 5000, 0), _k.L("3111", 0, 5000));
        await _k.PostAsync(D(10, 10), _k.L("1113", 1000, 0), _k.L("4111", 0, 1000));
        await _k.PostAsync(D(10, 15), _k.L("5112", 300, 0), _k.L("1111", 0, 300));
        await _k.PostAsync(D(10, 20), _k.L("1111", 400, 0), _k.L("1113", 0, 400));
    }

    [Fact]
    public async Task TS_TB_001_october_trial_balance_balances_and_lists_active_accounts()
    {
        await SeedActivityAsync();
        var tb = await TbAsync(D(10, 1), D(10, 31));

        tb.IsBalanced.Should().BeTrue();
        tb.TotalDebit.Should().Be(tb.TotalCredit).And.Be(6700m);
        tb.Totals.PeriodDebit.Should().Be(1700m);
        tb.Totals.PeriodCredit.Should().Be(1700m);
        tb.Lines.Select(l => l.Account.AccountCode).Should().Equal("1111", "1113", "3111", "4111", "5112"); // ordered by code
        tb.CurrencyCode.Should().Be("PKR");
        tb.FiscalYear!.FiscalYearName.Should().Be("FY 2026");
        Line(tb, "1111").Balance.Should().Be(5100m);
        Line(tb, "4111").Balance.Should().Be(-1000m, "credit balance is negative (debit − credit)");
        Line(tb, "4111").Level.Should().Be(4);
    }

    [Fact]
    public async Task TS_TB_002_opening_balances_are_everything_before_the_from_date()
    {
        await SeedActivityAsync();
        var tb = await TbAsync(D(10, 1), D(10, 31));
        var cash = Line(tb, "1111");
        cash.OpeningDebit.Should().Be(5000m);
        cash.PeriodDebit.Should().Be(400m);
        cash.PeriodCredit.Should().Be(300m);
        cash.ClosingDebit.Should().Be(5400m);
        cash.ClosingCredit.Should().Be(300m);
        tb.Totals.OpeningDebit.Should().Be(5000m);
        tb.Totals.OpeningCredit.Should().Be(5000m);

        // BR-TB-003 off: opening 0, closing = period only; the September-only equity account has no activity → hidden.
        var movement = await TbAsync(D(10, 1), D(10, 31), opening: false);
        Line(movement, "1111").OpeningDebit.Should().Be(0m);
        Line(movement, "1111").ClosingDebit.Should().Be(400m);
        movement.Lines.Should().NotContain(l => l.Account.AccountCode == "3111");
        movement.IsBalanced.Should().BeTrue();
    }

    [Fact]
    public async Task TS_TB_003_closing_entries_are_excluded_unless_asked()
    {
        await SeedActivityAsync();
        await _k.EntryAsync(D(12, 31), true, [_k.L("4111", 1000, 0), _k.L("3111", 0, 1000)], SystemJournalTypes.Closing);

        var without = await TbAsync(D(1, 1), D(12, 31));
        Line(without, "4111").Balance.Should().Be(-1000m);
        without.IncludeClosingEntries.Should().BeFalse();

        var with = await TbAsync(D(1, 1), D(12, 31), closing: true);
        Line(with, "4111").Balance.Should().Be(0m);
        Line(with, "3111").Balance.Should().Be(-6000m);
        with.IsBalanced.Should().BeTrue();
    }

    [Fact]
    public async Task TS_TB_004_account_balance_as_of_a_date()
    {
        await SeedActivityAsync();
        var cash = await _k.Reports.GetAccountBalanceAsync(_k.Admin, _k.Chart["1111"].Uuid, D(10, 15));
        cash.Debit.Should().Be(5000m);
        cash.Credit.Should().Be(300m);
        cash.Balance.Should().Be(4700m);
        cash.Account.AccountCode.Should().Be("1111");
        cash.AsOfDate.Should().Be(D(10, 15));

        var later = await _k.Reports.GetAccountBalanceAsync(_k.Admin, _k.Chart["1111"].Uuid, D(10, 31));
        later.Balance.Should().Be(5100m);
    }

    [Fact]
    public async Task TS_TB_005_account_ledger_has_a_running_balance_across_pages()
    {
        await _k.SetupAsync();
        await _k.PostAsync(D(9, 30), _k.L("1113", 50, 0), _k.L("4111", 0, 50));
        for (var i = 1; i <= 5; i++)
            await _k.PostAsync(D(10, i), _k.L("1113", 100 * i, 0), _k.L("4111", 0, 100 * i));
        await _k.PostAsync(D(10, 6), _k.L("1111", 120, 0), _k.L("1113", 0, 120));
        await _k.PostAsync(D(11, 1), _k.L("1113", 999, 0), _k.L("4111", 0, 999));

        var q = new AccountLedgerQuery { FromDate = D(10, 1), ToDate = D(10, 31), PageSize = 4 };
        var p1 = await _k.Reports.GetAccountLedgerAsync(_k.Admin, _k.Chart["1113"].Uuid, q);
        p1.OpeningBalance.Should().Be(50m);
        p1.TotalDebit.Should().Be(1500m);
        p1.TotalCredit.Should().Be(120m);
        p1.ClosingBalance.Should().Be(1430m);
        p1.Page.TotalRecords.Should().Be(6);
        p1.Page.TotalPages.Should().Be(2);
        p1.Page.Data.Select(l => l.RunningBalance).Should().Equal(150m, 350m, 650m, 1050m);
        p1.Page.Data.Should().OnlyContain(l => l.LineUuid != Guid.Empty && l.Status == JournalStatuses.Posted && l.JournalTypeCode == "MISC");

        q.Page = 2;
        var p2 = await _k.Reports.GetAccountLedgerAsync(_k.Admin, _k.Chart["1113"].Uuid, q);
        p2.Page.Data.Select(l => l.RunningBalance).Should().Equal(1550m, 1430m);
        p2.Page.Data[^1].Credit.Should().Be(120m);
        p2.Page.Data[^1].RunningBalance.Should().Be(p2.ClosingBalance);
    }

    [Fact]
    public async Task TS_TB_006_BR_TB_001_drafts_never_count()
    {
        await SeedActivityAsync();
        await _k.EntryAsync(D(10, 25), false, [_k.L("5112", 777, 0), _k.L("1111", 0, 777)]);
        var tb = await TbAsync(D(10, 1), D(10, 31));
        Line(tb, "5112").PeriodDebit.Should().Be(300m);
        tb.IsBalanced.Should().BeTrue();
        (await _k.Reports.GetAccountBalanceAsync(_k.Admin, _k.Chart["5112"].Uuid, D(12, 31))).Debit.Should().Be(300m);
        (await _k.Reports.GetAccountLedgerAsync(_k.Admin, _k.Chart["5112"].Uuid, new AccountLedgerQuery { FromDate = D(1, 1), ToDate = D(12, 31) }))
            .Page.TotalRecords.Should().Be(1);
    }

    [Fact]
    public async Task D_RPT_1_a_reversed_entry_and_its_reversal_both_count_and_net_to_zero()
    {
        await SeedActivityAsync();
        var original = await _k.PostAsync(D(10, 21), _k.L("5111", 250, 0), _k.L("1112", 0, 250));
        await _k.PostAsync(D(10, 22), _k.L("1112", 250, 0), _k.L("5111", 0, 250));
        var row = await _k.H.Db.JournalEntries.IgnoreQueryFilters().SingleAsync(j => j.Uuid == original.Uuid);
        row.Status = JournalStatuses.Reversed;
        await _k.H.Db.SaveChangesAsync();

        var tb = await TbAsync(D(10, 1), D(10, 31));
        Line(tb, "5111").PeriodDebit.Should().Be(250m);
        Line(tb, "5111").PeriodCredit.Should().Be(250m);
        Line(tb, "5111").Balance.Should().Be(0m);
        tb.IsBalanced.Should().BeTrue();
        var ledger = await _k.Reports.GetAccountLedgerAsync(_k.Admin, _k.Chart["5111"].Uuid, new AccountLedgerQuery { FromDate = D(10, 1), ToDate = D(10, 31) });
        ledger.Page.Data.Select(l => l.Status).Should().Equal(JournalStatuses.Reversed, JournalStatuses.Posted);
    }

    [Fact]
    public async Task BR_TB_005_zero_activity_accounts_are_hidden_unless_asked()
    {
        await SeedActivityAsync();
        var tb = await TbAsync(D(10, 1), D(10, 31));
        tb.Lines.Should().NotContain(l => l.Account.AccountCode == "1112" || l.Account.AccountCode == "4112");

        var all = await TbAsync(D(10, 1), D(10, 31), zero: true);
        all.Lines.Select(l => l.Account.AccountCode).Should().Contain(["1112", "2111", "3900", "4112", "5111"]);
        all.Lines.Should().OnlyContain(l => l.Level == 4, "level 4 only");
        Line(all, "1112").Balance.Should().Be(0m);
    }

    [Fact]
    public async Task D_RPT_2_an_inactive_account_with_activity_is_still_listed()
    {
        await SeedActivityAsync();
        var acc = await _k.H.Db.ChartOfAccounts.IgnoreQueryFilters().SingleAsync(a => a.Id == _k.Chart["5112"].Id);
        acc.IsActive = false;
        await _k.H.Db.SaveChangesAsync();

        var tb = await TbAsync(D(10, 1), D(10, 31));
        Line(tb, "5112").IsActive.Should().BeFalse();
        tb.IsBalanced.Should().BeTrue();
    }

    [Fact]
    public async Task Task_332_cost_center_filter()
    {
        await _k.SetupAsync();
        await _k.PostAsync(D(10, 1), _k.L("5112", 100, 0, "HQ"), _k.L("1111", 0, 100, "HQ"));
        await _k.PostAsync(D(10, 2), _k.L("5112", 40, 0, "BR"), _k.L("1111", 0, 40, "BR"));
        var hq = await TbAsync(D(10, 1), D(10, 31), costCenter: "HQ");
        Line(hq, "5112").PeriodDebit.Should().Be(100m);
        hq.IsBalanced.Should().BeTrue();
    }

    [Fact]
    public async Task Fiscal_year_instead_of_dates_and_range_errors()
    {
        await SeedActivityAsync();
        var tb = await _k.Reports.GenerateAsync(_k.Admin, new TrialBalanceQuery { FiscalYearUuid = _k.Year.Uuid });
        tb.FromDate.Should().Be(D(1, 1));
        tb.ToDate.Should().Be(D(12, 31));
        tb.Totals.OpeningDebit.Should().Be(0m);
        tb.Totals.PeriodDebit.Should().Be(6700m);

        var period = await _k.Reports.GenerateAsync(_k.Admin, new TrialBalanceQuery { FiscalPeriodUuid = await _k.PeriodUuidAsync(10) });
        period.FromDate.Should().Be(D(10, 1));

        await FluentActions.Awaiting(() => _k.Reports.GenerateAsync(_k.Admin, new TrialBalanceQuery()))
            .Should().ThrowAsync<BadRequestException>().WithMessage(TrialBalanceMessages.RangeRequired);
        await FluentActions.Awaiting(() => TbAsync(D(10, 31), D(10, 1)))
            .Should().ThrowAsync<BadRequestException>().WithMessage(TrialBalanceMessages.RangeOrder);
        await FluentActions.Awaiting(() => _k.Reports.GetAccountLedgerAsync(_k.Admin, _k.Chart["1111"].Uuid, new AccountLedgerQuery()))
            .Should().ThrowAsync<BadRequestException>().WithMessage(TrialBalanceMessages.RangeRequired);
    }

    [Fact]
    public async Task Group_and_foreign_accounts_are_refused()
    {
        await SeedActivityAsync();
        await FluentActions.Awaiting(() => _k.Reports.GetAccountBalanceAsync(_k.Admin, _k.Chart["111"].Uuid, D(10, 1)))
            .Should().ThrowAsync<BadRequestException>().WithMessage(TrialBalanceMessages.LeafOnly);
        await FluentActions.Awaiting(() => _k.Reports.GetAccountLedgerAsync(_k.Admin, _k.Chart["1"].Uuid, new AccountLedgerQuery { FromDate = D(1, 1), ToDate = D(1, 2) }))
            .Should().ThrowAsync<BadRequestException>().WithMessage(TrialBalanceMessages.LeafOnly);

        var other = new AccountingCaller(Guid.NewGuid(), 1, AccountingCaller.AllGlCodes);
        await FluentActions.Awaiting(() => _k.Reports.GetAccountBalanceAsync(other, _k.Chart["1111"].Uuid, D(10, 1)))
            .Should().ThrowAsync<NotFoundException>();
        (await _k.Reports.GenerateAsync(other, new TrialBalanceQuery { FromDate = D(1, 1), ToDate = D(12, 31) })).Lines.Should().BeEmpty("another organization sees nothing");
    }

    [Fact]
    public async Task GL_REPORTS_VIEW_is_required()
    {
        await SeedActivityAsync();
        await FluentActions.Awaiting(() => _k.Reports.GenerateAsync(_k.GlViewerOnly, new TrialBalanceQuery { FromDate = D(1, 1), ToDate = D(1, 2) }))
            .Should().ThrowAsync<ForbiddenException>();
        await FluentActions.Awaiting(() => _k.Reports.GetAccountBalanceAsync(_k.GlViewerOnly, _k.Chart["1111"].Uuid, null))
            .Should().ThrowAsync<ForbiddenException>();
        (await _k.Reports.GenerateAsync(_k.CloseViewer, new TrialBalanceQuery { FromDate = D(1, 1), ToDate = D(12, 31) })).IsBalanced.Should().BeTrue();
    }

    [Fact]
    public async Task Csv_export_has_bom_header_lines_and_total()
    {
        await SeedActivityAsync();
        var file = await _k.Reports.ExportCsvAsync(_k.Admin, new TrialBalanceQuery { FromDate = D(10, 1), ToDate = D(10, 31) });
        file.FileName.Should().Be("trial-balance-2026-10-01-2026-10-31.csv");
        file.ContentType.Should().Be("text/csv");
        file.Content.Take(3).Should().Equal(Encoding.UTF8.GetPreamble());
        var lines = Encoding.UTF8.GetString(file.Content[3..]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        lines[0].Should().StartWith("account_code,account_name,root_type");
        lines.Should().HaveCount(1 + 5 + 1);
        lines[1].Should().Be("1111,Account 1111,Asset,Cash,5000.00,0.00,400.00,300.00,5400.00,300.00,5100.00");
        lines[^1].Should().Be(",TOTAL,,,5000.00,5000.00,1700.00,1700.00,6700.00,6700.00,0.00");
    }

    public void Dispose() => _k.Dispose();
}
