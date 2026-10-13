using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Accounting.Tests;

/// <summary>A42 D-15 (lazy init), D-16 (status recompute / complete), D-17 (reset), D-20 (history) — T-SS-01..06, 09, 10.</summary>
public class SetupStatusTests
{
    [Fact]
    public async Task First_access_creates_the_status_row_and_the_16_defaults_once()
    {
        using var h = new AccountingHarness();
        var model = await h.Setup.GetAsync(h.Viewer());

        model.Status.Should().Be(SetupStatuses.NotStarted);
        model.NextStep.Should().Be(1);
        model.Steps.Select(s => s.Title).Should().Equal("Choose Template", "Review & Customize Accounts", "Set Up Fiscal Year",
            "Configure Default Accounts", "Enter Opening Balances", "Review & Complete");
        var defaults = await h.Db.AccountingDefaults.Where(d => d.OrganizationId == h.Org).ToListAsync();
        defaults.Should().HaveCount(16);
        defaults.Select(d => d.DefaultKey).Should().BeEquivalentTo(DefaultKeys.All);
        defaults.Count(d => d.IsRequired).Should().Be(8);
        defaults.Should().OnlyContain(d => d.AccountId == null && d.HelpText != null && d.ExpectedAccountType != null);
        defaults.Single(d => d.DefaultKey == DefaultKeys.TaxPayable).Should().Match<AccountingDefault>(d =>
            d.DefaultLabel == "Tax Payable (Output VAT)" && d.Category == DefaultCategories.Tax && !d.IsRequired && d.ExpectedAccountType == AccountTypes.Tax);

        h.NewScope();
        await h.Get<IAccountingInitializer>().EnsureAsync(h.Org);
        await h.Setup.GetAsync(h.Admin());
        (await h.Db.AccountingDefaults.CountAsync()).Should().Be(16);
        (await h.Db.CoaSetupStatuses.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Init_refreshes_catalog_fields_but_keeps_the_assigned_account()
    {
        using var h = new AccountingHarness();
        var chart = await h.SeedChartAsync(null, AccountingHarness.StandardLeaves);
        await h.AssignRequiredDefaultsAsync(chart);
        var row = await h.Db.AccountingDefaults.SingleAsync(d => d.DefaultKey == DefaultKeys.Cash);
        row.DefaultLabel = "Old label";
        h.Db.AccountingDefaults.Remove(await h.Db.AccountingDefaults.SingleAsync(d => d.DefaultKey == DefaultKeys.Rounding));
        await h.Db.SaveChangesAsync();

        h.NewScope();
        await h.Get<IAccountingInitializer>().EnsureAsync(h.Org);

        var cash = await h.Db.AccountingDefaults.SingleAsync(d => d.DefaultKey == DefaultKeys.Cash);
        cash.DefaultLabel.Should().Be("Cash Account");
        cash.AccountId.Should().Be(chart["1111"].Id);
        (await h.Db.AccountingDefaults.CountAsync()).Should().Be(16, "a missing key is added back");
    }

    [Fact]
    public async Task Status_moves_to_the_furthest_consecutive_step_and_never_past_a_missing_one()
    {
        using var h = new AccountingHarness();
        (await h.Setup.RecomputeAsync(h.Org)).Should().Be(SetupStatuses.NotStarted);

        var chart = await h.SeedChartAsync(null, AccountingHarness.StandardLeaves);
        (await h.Setup.RecomputeAsync(h.Org)).Should().Be(SetupStatuses.AccountsCreated);

        // Defaults without a fiscal year do not skip the fiscal year step.
        await h.AssignRequiredDefaultsAsync(chart);
        (await h.Setup.RecomputeAsync(h.Org)).Should().Be(SetupStatuses.AccountsCreated);

        await h.AddFiscalYearAsync();
        (await h.Setup.RecomputeAsync(h.Org)).Should().Be(SetupStatuses.DefaultsSet);

        await h.MarkOpeningBalancesPostedAsync();
        (await h.Setup.RecomputeAsync(h.Org)).Should().Be(SetupStatuses.BalancesEntered);

        // An assigned required default whose account was deactivated no longer counts.
        chart["1111"].IsActive = false;
        await h.Db.SaveChangesAsync();
        (await h.Setup.RecomputeAsync(h.Org)).Should().Be(SetupStatuses.FiscalYearSet);

        var model = await h.Setup.GetAsync(h.Viewer());
        model.AccountsCreatedCount.Should().Be(22);
        model.FiscalYearConfigured.Should().BeTrue();
        model.DefaultsConfigured.Should().BeFalse();
        model.NextStep.Should().Be(4);

        h.Events.Of<CoaSetupStatusChangedEvent>().Select(e => e.NewStatus).Should().Equal(
            SetupStatuses.AccountsCreated, SetupStatuses.DefaultsSet, SetupStatuses.BalancesEntered, SetupStatuses.FiscalYearSet);
    }

    [Fact]
    public async Task Complete_lists_what_is_missing()
    {
        using var h = new AccountingHarness();
        await h.SeedChartAsync(null, AccountingHarness.StandardLeaves);

        var act = () => h.Setup.CompleteAsync(h.Admin());

        var ex = (await act.Should().ThrowAsync<SetupIncompleteException>()).Which;
        ex.Should().BeAssignableTo<ConflictException>();
        ex.Unmet.Should().Equal("Set up a fiscal year.", "Assign every required accounting default.", "Post or skip the opening balances.");
    }

    [Fact]
    public async Task Complete_needs_balanced_opening_balances()
    {
        using var h = new AccountingHarness();
        var chart = await h.SeedChartAsync(null, AccountingHarness.StandardLeaves);
        await h.AddFiscalYearAsync();
        await h.AssignRequiredDefaultsAsync(chart);
        await h.MarkOpeningBalancesPostedAsync(balanced: false);

        var ex = (await ((Func<Task>)(() => h.Setup.CompleteAsync(h.Admin()))).Should().ThrowAsync<SetupIncompleteException>()).Which;
        ex.Unmet.Should().Equal("The opening balances must balance.");
    }

    [Fact]
    public async Task Complete_finalizes_publishes_and_stays_complete()
    {
        using var h = new AccountingHarness();
        var chart = await h.ReadyToCompleteAsync();

        var model = await h.Setup.CompleteAsync(h.Admin());

        model.Status.Should().Be(SetupStatuses.Complete);
        model.SetupCompletedAt.Should().NotBeNull();
        model.SetupCompletedByName.Should().Be("Ada Admin");
        model.ConversionDate.Should().Be(new DateOnly(2025, 12, 31));
        model.NextStep.Should().Be(6);
        model.Steps.Should().OnlyContain(s => s.Done);
        h.Events.Of<CoaSetupCompletedEvent>().Should().ContainSingle().Which.AccountCount.Should().Be(23);

        // Complete is terminal (D-30): a later change does not demote it; a second complete is a no-op.
        chart["1111"].IsActive = false;
        await h.Db.SaveChangesAsync();
        (await h.Setup.RecomputeAsync(h.Org)).Should().Be(SetupStatuses.Complete);
        (await h.Setup.CompleteAsync(h.Admin())).Status.Should().Be(SetupStatuses.Complete);
        h.Events.Of<CoaSetupCompletedEvent>().Should().HaveCount(1);
    }

    [Fact]
    public async Task Reset_clears_the_setup_but_keeps_the_default_rows()
    {
        using var h = new AccountingHarness();
        var chart = await h.ReadyToCompleteAsync();
        var opening = new JournalEntry { OrganizationId = h.Org, EntryNumber = "JE-2026-00001", EntryType = JournalEntryTypes.OpeningBalance,
            EntryDate = new DateOnly(2025, 12, 31), TotalDebit = 10, TotalCredit = 10 };
        opening.Lines.Add(new JournalEntryLine { OrganizationId = h.Org, LineNumber = 1, AccountId = chart["1111"].Id, DebitAmount = 10 });
        opening.Lines.Add(new JournalEntryLine { OrganizationId = h.Org, LineNumber = 2, AccountId = chart["3900"].Id, CreditAmount = 10 });
        h.Db.JournalEntries.Add(opening);
        h.Db.OpeningBalanceEntries.Add(new OpeningBalanceEntry { OrganizationId = h.Org, AccountId = chart["1111"].Id, DebitAmount = 10,
            EntryStatus = OpeningEntryStatuses.Posted, JournalEntry = opening });
        h.Db.ExternalSystemMappings.Add(new ExternalSystemMapping { OrganizationId = h.Org, AccountId = chart["1111"].Id,
            SystemType = ExternalSystemTypes.Xero, SystemName = "Xero", ExternalAccountCode = "090" });
        await h.Db.SaveChangesAsync();
        await h.Setup.CompleteAsync(h.Admin());

        // Another organization's setup is untouched.
        var other = Guid.NewGuid();
        await h.SeedChartAsync(other, ("1111", RootTypes.Asset, AccountTypes.Cash));

        h.NewScope();
        var model = await h.Setup.ResetAsync(h.Admin());

        model.Status.Should().Be(SetupStatuses.NotStarted);
        model.ConversionDate.Should().BeNull();
        model.SetupCompletedAt.Should().BeNull();
        model.AccountsCreatedCount.Should().Be(0);
        (await h.Db.ChartOfAccounts.IgnoreQueryFilters().CountAsync(a => a.OrganizationId == h.Org)).Should().Be(0);
        (await h.Db.ChartOfAccounts.IgnoreQueryFilters().CountAsync(a => a.OrganizationId == other)).Should().Be(16);
        (await h.Db.FiscalYears.CountAsync()).Should().Be(0);
        (await h.Db.FiscalPeriods.CountAsync()).Should().Be(0);
        (await h.Db.JournalEntries.CountAsync()).Should().Be(0);
        (await h.Db.JournalEntryLines.CountAsync()).Should().Be(0);
        (await h.Db.OpeningBalanceEntries.CountAsync()).Should().Be(0);
        (await h.Db.ExternalSystemMappings.CountAsync()).Should().Be(0);
        var defaults = await h.Db.AccountingDefaults.ToListAsync();
        defaults.Should().HaveCount(16).And.OnlyContain(d => d.AccountId == null);
        h.Events.Of<CoaSetupStatusChangedEvent>().Last().Should().Match<CoaSetupStatusChangedEvent>(e =>
            e.OldStatus == SetupStatuses.Complete && e.NewStatus == SetupStatuses.NotStarted && e.Note == "Setup reset");
    }

    [Fact]
    public async Task Reset_is_refused_once_a_non_opening_journal_exists()
    {
        using var h = new AccountingHarness();
        var chart = await h.SeedChartAsync(null, AccountingHarness.StandardLeaves);
        h.Db.JournalEntries.Add(new JournalEntry { OrganizationId = h.Org, EntryNumber = "JE-2026-00002", EntryType = "GENERAL",
            EntryDate = new DateOnly(2026, 2, 1) });
        await h.Db.SaveChangesAsync();

        var act = () => h.Setup.ResetAsync(h.Admin());

        (await act.Should().ThrowAsync<ConflictException>()).Which.Message.Should().Be("Cannot reset after journal entries posted.");
        (await h.Db.ChartOfAccounts.CountAsync()).Should().Be(chart.Count);
    }

    [Fact]
    public async Task Permissions_are_enforced_by_the_service()
    {
        using var h = new AccountingHarness();
        await ((Func<Task>)(() => h.Setup.GetAsync(h.Nobody()))).Should().ThrowAsync<ForbiddenException>();
        await ((Func<Task>)(() => h.Setup.HistoryAsync(h.Nobody()))).Should().ThrowAsync<ForbiddenException>();
        await ((Func<Task>)(() => h.Setup.CompleteAsync(h.Viewer()))).Should().ThrowAsync<ForbiddenException>();
        await ((Func<Task>)(() => h.Setup.ResetAsync(h.Viewer()))).Should().ThrowAsync<ForbiddenException>();
        var accountsOnly = new AccountingCaller(h.Org, 9, [SMS.Shared.Authorization.PermissionCodes.GL_ACCOUNT_MANAGE]);
        accountsOnly.CanView.Should().BeTrue();
        accountsOnly.CanManageAccounts.Should().BeTrue();
        accountsOnly.CanManageSetup.Should().BeFalse();
        (await h.Setup.GetAsync(accountsOnly)).Status.Should().Be(SetupStatuses.NotStarted);
    }

    [Fact]
    public async Task History_records_every_event_newest_first_with_the_user()
    {
        using var h = new AccountingHarness();
        await h.ReadyToCompleteAsync();
        await h.Setup.RecomputeAsync(h.Org, 1, "Ada Admin");
        await h.Setup.CompleteAsync(h.Admin());

        var history = await h.Setup.HistoryAsync(h.Viewer());

        history.Select(x => x.EventType).Should().Equal(nameof(CoaSetupCompletedEvent), nameof(CoaSetupStatusChangedEvent), nameof(CoaSetupStatusChangedEvent));
        history[0].Summary.Should().Be("General ledger setup completed with 23 accounts.");
        history[1].Summary.Should().Be("Setup status BalancesEntered → Complete.");
        history[2].Summary.Should().Be("Setup status NotStarted → BalancesEntered.");
        history.Should().OnlyContain(x => x.UserName == "Ada Admin");
        (await h.Setup.HistoryAsync(h.Viewer(Guid.NewGuid()))).Should().BeEmpty("history is per organization");
    }
}
