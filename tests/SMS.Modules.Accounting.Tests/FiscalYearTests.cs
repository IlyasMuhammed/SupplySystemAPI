using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Accounting.Tests;

/// <summary>A42 §7 / D-18 — T-FP-01..05, custom calendars, frozen dates, the period guard, isolation.</summary>
public class FiscalYearTests
{
    private static CreateFiscalYearRequest Apr2026() =>
        new() { FiscalYearName = "FY 2026-27", StartDate = new DateOnly(2026, 4, 1), EndDate = new DateOnly(2027, 3, 31) };

    [Fact]
    public async Task T_FP_01_creating_a_year_generates_12_monthly_periods_and_the_adjustment_period()
    {
        using var h = new AccountingHarness();
        var year = await h.Fiscal().CreateAsync(h.Admin(), Apr2026());

        year.ReportingCurrencyCode.Should().Be("PKR", "it defaults to the organization's base currency (D-13)");
        year.HasPostings.Should().BeFalse();
        year.Periods.Should().HaveCount(13);
        year.Periods[0].Should().Match<FiscalPeriodModel>(p => p.PeriodNumber == 1 && p.PeriodName == "April 2026"
            && p.StartDate == new DateOnly(2026, 4, 1) && p.EndDate == new DateOnly(2026, 4, 30) && p.Status == PeriodStatuses.Open);
        year.Periods[1].Should().Match<FiscalPeriodModel>(p => p.StartDate == new DateOnly(2026, 5, 1) && p.EndDate == new DateOnly(2026, 5, 31));
        year.Periods[11].Should().Match<FiscalPeriodModel>(p => p.PeriodNumber == 12 && p.PeriodName == "March 2027"
            && p.StartDate == new DateOnly(2027, 3, 1) && p.EndDate == new DateOnly(2027, 3, 31));
        year.Periods[12].Should().Match<FiscalPeriodModel>(p => p.PeriodNumber == 13 && p.IsAdjustment
            && p.StartDate == new DateOnly(2027, 3, 31) && p.EndDate == new DateOnly(2027, 3, 31));

        (await h.Db.FiscalPeriods.CountAsync()).Should().Be(13);
        h.Events.Of<FiscalYearCreatedEvent>().Should().ContainSingle().Which.FiscalYearName.Should().Be("FY 2026-27");
        (await h.Setup.GetAsync(h.Viewer())).FiscalYearConfigured.Should().BeTrue();
        (await h.Fiscal().ListAsync(h.Viewer())).Should().ContainSingle();
        (await h.Fiscal().GetPeriodsAsync(h.Viewer(), year.Uuid)).Should().HaveCount(13);
    }

    [Fact]
    public async Task The_reporting_currency_is_normalized_and_required_when_no_base_currency_is_known()
    {
        using var h = new AccountingHarness();
        var noBase = h.Fiscal(baseCurrency: null);

        var act = () => noBase.CreateAsync(h.Admin(), Apr2026());
        await act.Should().ThrowAsync<BadRequestException>();

        var req = Apr2026();
        req.ReportingCurrencyCode = "usd";
        (await noBase.CreateAsync(h.Admin(), req)).ReportingCurrencyCode.Should().Be("USD");

        var bad = new CreateFiscalYearRequest { StartDate = new DateOnly(2028, 1, 1), EndDate = new DateOnly(2028, 12, 31), ReportingCurrencyCode = "US" };
        await ((Func<Task>)(() => noBase.CreateAsync(h.Admin(), bad))).Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task A_name_is_suggested_and_ranges_are_validated()
    {
        using var h = new AccountingHarness();
        var fiscal = h.Fiscal();
        (await fiscal.CreateAsync(h.Admin(), new CreateFiscalYearRequest { StartDate = new DateOnly(2025, 1, 1), EndDate = new DateOnly(2025, 12, 31) }))
            .FiscalYearName.Should().Be("FY 2025");

        // A short first year: one period per month started, the last one cut at the end date, plus period 13.
        var shortYear = await fiscal.CreateAsync(h.Admin(), new CreateFiscalYearRequest { StartDate = new DateOnly(2024, 7, 1), EndDate = new DateOnly(2024, 12, 15) });
        shortYear.FiscalYearName.Should().Be("FY 2024");
        shortYear.Periods.Select(p => (int)p.PeriodNumber).Should().Equal(1, 2, 3, 4, 5, 6, 13);
        shortYear.Periods[5].EndDate.Should().Be(new DateOnly(2024, 12, 15));

        await ((Func<Task>)(() => fiscal.CreateAsync(h.Admin(), new CreateFiscalYearRequest
            { StartDate = new DateOnly(2030, 1, 1), EndDate = new DateOnly(2031, 1, 1) }))).Should().ThrowAsync<BadRequestException>()
            .WithMessage("A fiscal year can be at most 12 months long.");
        await ((Func<Task>)(() => fiscal.CreateAsync(h.Admin(), new CreateFiscalYearRequest
            { StartDate = new DateOnly(2030, 1, 1), EndDate = new DateOnly(2030, 1, 1) }))).Should().ThrowAsync<BadRequestException>();
        await ((Func<Task>)(() => fiscal.CreateAsync(h.Admin(), new CreateFiscalYearRequest
            { FiscalYearName = "FY 2025", StartDate = new DateOnly(2031, 1, 1), EndDate = new DateOnly(2031, 12, 31) })))
            .Should().ThrowAsync<ConflictException>().WithMessage("A fiscal year named \"FY 2025\" already exists.");
    }

    [Fact]
    public async Task T_FP_02_overlapping_years_are_refused()
    {
        using var h = new AccountingHarness();
        var fiscal = h.Fiscal();
        await fiscal.CreateAsync(h.Admin(), Apr2026());

        var overlap = new CreateFiscalYearRequest { FiscalYearName = "Calendar 2027", StartDate = new DateOnly(2027, 1, 1), EndDate = new DateOnly(2027, 12, 31) };
        await ((Func<Task>)(() => fiscal.CreateAsync(h.Admin(), overlap))).Should().ThrowAsync<ConflictException>()
            .WithMessage("Fiscal year dates overlap with an existing year.");

        // Back to back is fine; another organization's years never overlap ours.
        await fiscal.CreateAsync(h.Admin(), new CreateFiscalYearRequest { FiscalYearName = "FY 2027-28", StartDate = new DateOnly(2027, 4, 1), EndDate = new DateOnly(2028, 3, 31) });
        await fiscal.CreateAsync(h.Admin(Guid.NewGuid()), overlap);
    }

    [Fact]
    public async Task T_FP_03_a_locked_period_blocks_postings_and_can_be_reopened()
    {
        using var h = new AccountingHarness();
        var year = await h.Fiscal().CreateAsync(h.Admin(), Apr2026());
        var guard = h.Get<IFiscalPeriodGuard>();
        var may = year.Periods[1];
        (await guard.IsPostingAllowedAsync(h.Org, new DateOnly(2026, 5, 10))).Should().BeTrue();

        (await h.Fiscal().UpdatePeriodAsync(h.Admin(), may.Uuid, new UpdateFiscalPeriodRequest { Status = "locked" })).Status.Should().Be(PeriodStatuses.Locked);
        (await guard.IsPostingAllowedAsync(h.Org, new DateOnly(2026, 5, 10))).Should().BeFalse();
        (await guard.IsPostingAllowedAsync(h.Org, new DateOnly(2026, 6, 10))).Should().BeTrue();

        (await h.Fiscal().UpdatePeriodAsync(h.Admin(), may.Uuid, new UpdateFiscalPeriodRequest { Status = PeriodStatuses.Open })).Status.Should().Be(PeriodStatuses.Open);
        (await guard.IsPostingAllowedAsync(h.Org, new DateOnly(2026, 5, 10))).Should().BeTrue();

        h.Events.Of<FiscalPeriodStatusChangedEvent>().Select(e => (e.OldStatus, e.NewStatus)).Should().Equal(
            (PeriodStatuses.Open, PeriodStatuses.Locked), (PeriodStatuses.Locked, PeriodStatuses.Open));
        (await h.Setup.HistoryAsync(h.Viewer())).Should().Contain(x => x.Summary == "Period May 2026: Open → Locked.");
    }

    [Fact]
    public async Task T_FP_04_a_closed_period_blocks_postings_and_cannot_be_reopened()
    {
        using var h = new AccountingHarness();
        var year = await h.Fiscal().CreateAsync(h.Admin(), Apr2026());
        var april = year.Periods[0];

        await h.Fiscal().UpdatePeriodAsync(h.Admin(), april.Uuid, new UpdateFiscalPeriodRequest { Status = PeriodStatuses.Locked });
        await h.Fiscal().UpdatePeriodAsync(h.Admin(), april.Uuid, new UpdateFiscalPeriodRequest { Status = PeriodStatuses.Closed });
        (await h.Get<IFiscalPeriodGuard>().IsPostingAllowedAsync(h.Org, new DateOnly(2026, 4, 15))).Should().BeFalse();

        foreach (var to in new[] { PeriodStatuses.Open, PeriodStatuses.Locked })
            await ((Func<Task>)(() => h.Fiscal().UpdatePeriodAsync(h.Admin(), april.Uuid, new UpdateFiscalPeriodRequest { Status = to })))
                .Should().ThrowAsync<ConflictException>().WithMessage("A closed period cannot be reopened.");

        await ((Func<Task>)(() => h.Fiscal().UpdatePeriodAsync(h.Admin(), april.Uuid, new UpdateFiscalPeriodRequest { Status = "Frozen" })))
            .Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task A_period_with_draft_journal_entries_cannot_be_closed()
    {
        using var h = new AccountingHarness();
        var year = await h.Fiscal().CreateAsync(h.Admin(), Apr2026());
        h.Db.JournalEntries.Add(new JournalEntry { OrganizationId = h.Org, EntryNumber = "JE-2026-00009", EntryType = "GENERAL",
            EntryDate = new DateOnly(2026, 4, 20), Status = JournalStatuses.Draft });
        await h.Db.SaveChangesAsync();

        await ((Func<Task>)(() => h.Fiscal().UpdatePeriodAsync(h.Admin(), year.Periods[0].Uuid, new UpdateFiscalPeriodRequest { Status = PeriodStatuses.Closed })))
            .Should().ThrowAsync<ConflictException>();
        (await h.Fiscal().UpdatePeriodAsync(h.Admin(), year.Periods[1].Uuid, new UpdateFiscalPeriodRequest { Status = PeriodStatuses.Closed }))
            .Status.Should().Be(PeriodStatuses.Closed);
    }

    [Fact]
    public async Task T_FP_05_a_year_closes_only_when_every_period_is_closed()
    {
        using var h = new AccountingHarness();
        var year = await h.Fiscal().CreateAsync(h.Admin(), Apr2026());

        await ((Func<Task>)(() => h.Fiscal().CloseAsync(h.Admin(), year.Uuid))).Should().ThrowAsync<ConflictException>()
            .WithMessage("All periods must be closed first.");

        foreach (var p in year.Periods)
            await h.Fiscal().UpdatePeriodAsync(h.Admin(), p.Uuid, new UpdateFiscalPeriodRequest { Status = PeriodStatuses.Closed });
        var closed = await h.Fiscal().CloseAsync(h.Admin(), year.Uuid);

        closed.IsClosed.Should().BeTrue();
        closed.ClosedAt.Should().NotBeNull();
        (await h.Fiscal().CloseAsync(h.Admin(), year.Uuid)).IsClosed.Should().BeTrue("closing again is a no-op");
        await ((Func<Task>)(() => h.Fiscal().UpdateAsync(h.Admin(), year.Uuid, new UpdateFiscalYearRequest { FiscalYearName = "Renamed" })))
            .Should().ThrowAsync<ConflictException>().WithMessage("A closed fiscal year cannot be changed.");
    }

    [Fact]
    public async Task Custom_calendars_move_the_neighbouring_period_and_keep_the_year_covered()
    {
        using var h = new AccountingHarness();
        var year = await h.Fiscal().CreateAsync(h.Admin(), new CreateFiscalYearRequest { StartDate = SetupTestKit.Fy2026Start, EndDate = new DateOnly(2026, 12, 31) });
        var fiscal = h.Fiscal();

        // 4-4-5 style: January ends on the 28th, February starts on the 29th.
        var jan = await fiscal.UpdatePeriodAsync(h.Admin(), year.Periods[0].Uuid, new UpdateFiscalPeriodRequest { EndDate = new DateOnly(2026, 1, 28), PeriodName = "Period 1" });
        jan.PeriodName.Should().Be("Period 1");
        var periods = await fiscal.GetPeriodsAsync(h.Viewer(), year.Uuid);
        periods[0].EndDate.Should().Be(new DateOnly(2026, 1, 28));
        periods[1].StartDate.Should().Be(new DateOnly(2026, 1, 29));

        // Moving March's start earlier moves February's end.
        await fiscal.UpdatePeriodAsync(h.Admin(), year.Periods[2].Uuid, new UpdateFiscalPeriodRequest { StartDate = new DateOnly(2026, 2, 26) });
        periods = await fiscal.GetPeriodsAsync(h.Viewer(), year.Uuid);
        periods[1].EndDate.Should().Be(new DateOnly(2026, 2, 25));

        await ((Func<Task>)(() => fiscal.UpdatePeriodAsync(h.Admin(), year.Periods[0].Uuid, new UpdateFiscalPeriodRequest { StartDate = new DateOnly(2026, 1, 2) })))
            .Should().ThrowAsync<BadRequestException>();
        await ((Func<Task>)(() => fiscal.UpdatePeriodAsync(h.Admin(), year.Periods[11].Uuid, new UpdateFiscalPeriodRequest { EndDate = new DateOnly(2026, 12, 30) })))
            .Should().ThrowAsync<BadRequestException>();
        // Swallowing the next period entirely is refused (it would end before it starts).
        await ((Func<Task>)(() => fiscal.UpdatePeriodAsync(h.Admin(), year.Periods[3].Uuid, new UpdateFiscalPeriodRequest { EndDate = new DateOnly(2026, 6, 15) })))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Period May 2026 would end before it starts.");
        // The adjustment period stays inside the year.
        await ((Func<Task>)(() => fiscal.UpdatePeriodAsync(h.Admin(), year.Periods[12].Uuid, new UpdateFiscalPeriodRequest { EndDate = new DateOnly(2027, 1, 5) })))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Period dates must fall within the fiscal year.");

        h.NewScope();
        var stored = await h.Db.FiscalYears.Include(y => y.Periods).SingleAsync();
        FiscalYearService.ValidatePeriods(stored); // still covered, nothing half-saved
    }

    [Fact]
    public async Task Year_dates_regenerate_the_periods_until_something_is_posted()
    {
        using var h = new AccountingHarness();
        var year = await h.Fiscal().CreateAsync(h.Admin(), Apr2026());

        var moved = await h.Fiscal().UpdateAsync(h.Admin(), year.Uuid, new UpdateFiscalYearRequest
            { FiscalYearName = "FY 2026-27 (Jul)", ReportingCurrencyCode = "usd", StartDate = new DateOnly(2026, 7, 1), EndDate = new DateOnly(2027, 6, 30) });
        moved.FiscalYearName.Should().Be("FY 2026-27 (Jul)");
        moved.ReportingCurrencyCode.Should().Be("USD");
        moved.Periods.Should().HaveCount(13);
        moved.Periods[0].PeriodName.Should().Be("July 2026");
        (await h.Db.FiscalPeriods.CountAsync()).Should().Be(13);

        h.Db.JournalEntries.Add(new JournalEntry { OrganizationId = h.Org, EntryNumber = "JE-2026-00001", EntryDate = new DateOnly(2026, 7, 1) });
        await h.Db.SaveChangesAsync();
        h.NewScope();

        (await h.Fiscal().GetAsync(h.Viewer(), year.Uuid)).HasPostings.Should().BeTrue();
        await ((Func<Task>)(() => h.Fiscal().UpdateAsync(h.Admin(), year.Uuid, new UpdateFiscalYearRequest { StartDate = new DateOnly(2026, 8, 1), EndDate = new DateOnly(2027, 7, 31) })))
            .Should().ThrowAsync<ConflictException>().WithMessage(FiscalYearService.DatesFrozenMessage);
        await ((Func<Task>)(() => h.Fiscal().UpdatePeriodAsync(h.Admin(), moved.Periods[0].Uuid, new UpdateFiscalPeriodRequest { EndDate = new DateOnly(2026, 7, 20) })))
            .Should().ThrowAsync<ConflictException>();
        // Name and currency can still change.
        (await h.Fiscal().UpdateAsync(h.Admin(), year.Uuid, new UpdateFiscalYearRequest { FiscalYearName = "Main year" })).FiscalYearName.Should().Be("Main year");
    }

    [Fact]
    public async Task Writes_need_setup_manage_and_reads_need_view()
    {
        using var h = new AccountingHarness();
        await ((Func<Task>)(() => h.Fiscal().CreateAsync(h.Viewer(), Apr2026()))).Should().ThrowAsync<ForbiddenException>();
        var accountsOnly = new AccountingCaller(h.Org, 9, [SMS.Shared.Authorization.PermissionCodes.GL_ACCOUNT_MANAGE]);
        await ((Func<Task>)(() => h.Fiscal().CreateAsync(accountsOnly, Apr2026()))).Should().ThrowAsync<ForbiddenException>();
        await ((Func<Task>)(() => h.Fiscal().ListAsync(h.Nobody()))).Should().ThrowAsync<ForbiddenException>();
        (await h.Fiscal().ListAsync(accountsOnly)).Should().BeEmpty();
    }

    [Fact]
    public async Task Fiscal_years_and_the_period_guard_are_per_organization()
    {
        using var h = new AccountingHarness();
        var year = await h.Fiscal().CreateAsync(h.Admin(), Apr2026());
        var other = Guid.NewGuid();

        (await h.Fiscal().ListAsync(h.Viewer(other))).Should().BeEmpty();
        await ((Func<Task>)(() => h.Fiscal().GetAsync(h.Viewer(other), year.Uuid))).Should().ThrowAsync<NotFoundException>();
        await ((Func<Task>)(() => h.Fiscal().UpdateAsync(h.Admin(other), year.Uuid, new UpdateFiscalYearRequest { FiscalYearName = "x" })))
            .Should().ThrowAsync<NotFoundException>();
        await ((Func<Task>)(() => h.Fiscal().UpdatePeriodAsync(h.Admin(other), year.Periods[0].Uuid, new UpdateFiscalPeriodRequest { Status = PeriodStatuses.Closed })))
            .Should().ThrowAsync<NotFoundException>();
        await ((Func<Task>)(() => h.Fiscal().CloseAsync(h.Admin(other), year.Uuid))).Should().ThrowAsync<NotFoundException>();

        var guard = h.Get<IFiscalPeriodGuard>();
        (await guard.IsPostingAllowedAsync(other, new DateOnly(2026, 5, 1))).Should().BeFalse();
        (await guard.IsPostingAllowedAsync(h.Org, new DateOnly(2026, 5, 1))).Should().BeTrue();
        (await guard.IsPostingAllowedAsync(h.Org, new DateOnly(2027, 5, 1))).Should().BeFalse("no fiscal year covers the date");
    }
}
