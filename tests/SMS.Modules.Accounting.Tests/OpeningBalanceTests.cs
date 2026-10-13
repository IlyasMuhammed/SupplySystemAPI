using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Accounting.Tests;

/// <summary>A42 §10 / D-10 / D-11 / D-12 / D-19 — T-OB-01..07, conversion date rules, sub-ledger reference, date lock.</summary>
public class OpeningBalanceTests
{
    private static readonly DateOnly Conversion = new(2026, 1, 1);

    private static OpeningEntryInput Dr(ChartOfAccount a, decimal amount, string? notes = null) => new() { AccountUuid = a.Uuid, DebitAmount = amount, Notes = notes };
    private static OpeningEntryInput Cr(ChartOfAccount a, decimal amount) => new() { AccountUuid = a.Uuid, CreditAmount = amount };

    private static async Task<Dictionary<string, ChartOfAccount>> ChartAndYearAsync(AccountingHarness h)
    {
        var chart = await h.SeedChartAsync(null, AccountingHarness.StandardLeaves);
        await h.AddFiscalYearAsync();
        return chart;
    }

    [Fact]
    public async Task T_OB_01_and_03_balanced_balances_post_as_one_journal_entry()
    {
        using var h = new AccountingHarness();
        var chart = await ChartAndYearAsync(h);
        var numbers = new FakeNumbers();
        var ob = h.Opening(h.DateOf(Conversion), numbers: numbers);

        var draft = await ob.SaveBatchAsync(h.Admin(), new SaveOpeningBalancesRequest
        {
            Entries = [Dr(chart["1111"], 5_000, "Petty cash"), Dr(chart["1112"], 150_000), Cr(chart["2111"], 65_000), Cr(chart["3111"], 90_000)],
        });
        draft.IsPosted.Should().BeFalse();
        draft.ConversionDate.Should().Be(Conversion);
        draft.Summary.Should().Match<OpeningSummaryModel>(s => s.TotalDebits == 155_000 && s.TotalCredits == 155_000 && s.IsBalanced && s.PlugSide == null && s.EntryCount == 4);
        draft.Groups.Select(g => g.RootType).Should().Equal(RootTypes.All);
        var assetRows = draft.Groups[0].Rows;
        assetRows.Select(r => r.AccountCode).Should().Equal("1111", "1112", "1113");
        assetRows[0].Should().Match<OpeningRowModel>(r => r.DebitAmount == 5_000 && r.Notes == "Petty cash" && !r.IsPlug);
        assetRows[2].DebitAmount.Should().Be(0, "accounts without an entry show 0");
        draft.Groups[2].Rows.Single(r => r.AccountCode == "3900").IsPlug.Should().BeTrue();

        var result = await ob.PostAsync(h.Admin(), new PostOpeningBalancesRequest());

        result.JournalEntryNumber.Should().MatchRegex(@"^JE-\d{4}-00001$");
        numbers.Calls.Should().ContainSingle().Which.Should().Be(("JE", (Guid?)h.Org));
        var events = h.Events;
        h.NewScope();
        var journal = await h.Db.JournalEntries.Include(j => j.Lines).SingleAsync();
        journal.Should().Match<JournalEntry>(j => j.EntryType == JournalEntryTypes.OpeningBalance && j.Status == JournalStatuses.Posted
            && j.EntryDate == Conversion && j.TotalDebit == 155_000 && j.TotalCredit == 155_000 && j.CurrencyCode == "PKR"
            && j.Reference == "Opening Balance Entry — 2026-01-01" && j.PostedBy == 1);
        journal.Lines.OrderBy(l => l.LineNumber).Select(l => (l.AccountId, l.DebitAmount, l.CreditAmount)).Should().Equal(
            (chart["1111"].Id, 5_000m, 0m), (chart["1112"].Id, 150_000m, 0m), (chart["2111"].Id, 0m, 65_000m), (chart["3111"].Id, 0m, 90_000m));

        // T-OB-03 — accounts, entries and the setup row.
        var accounts = await h.Db.ChartOfAccounts.ToDictionaryAsync(a => a.AccountCode);
        accounts["1111"].Should().Match<ChartOfAccount>(a => a.OpeningBalanceDebit == 5_000 && a.CurrentBalance == 5_000 && a.HasTransactions);
        accounts["2111"].Should().Match<ChartOfAccount>(a => a.OpeningBalanceCredit == 65_000 && a.CurrentBalance == 65_000 && a.HasTransactions,
            "liabilities are credit-normal");
        accounts["3900"].HasTransactions.Should().BeFalse("no difference, no plug line");
        accounts["1113"].HasTransactions.Should().BeFalse();
        (await h.Db.OpeningBalanceEntries.ToListAsync()).Should().OnlyContain(e => e.EntryStatus == OpeningEntryStatuses.Posted
            && e.JournalEntryId == journal.Id && e.ConversionDate == Conversion && e.PostedAt != null && e.PostedBy == 1);
        var status = await h.Db.CoaSetupStatuses.SingleAsync();
        status.Should().Match<CoaSetupStatus>(s => s.OpeningBalancesEntered && s.OpeningBalancesBalanced && s.OpeningBalancesPostedAt != null
            && s.ConversionDate == Conversion);

        events.Of<OpeningBalancePostedEvent>().Should().ContainSingle().Which.Should().Match<OpeningBalancePostedEvent>(e =>
            e.TotalDebit == 155_000 && e.Difference == 0 && !e.Skipped && e.JournalEntryNumber == result.JournalEntryNumber);
        (await h.Get<IOpeningDateLockSource>().GetLockedDateAsync(h.Org)).Should().Be(Conversion);

        var after = await h.Opening(h.DateOf(new DateOnly(2026, 6, 1))).GetAsync(h.Viewer());
        after.IsPosted.Should().BeTrue();
        after.ConversionDate.Should().Be(Conversion, "once posted the copy taken at posting is shown");
    }

    [Fact]
    public async Task T_OB_02_a_difference_needs_confirmation_and_is_plugged_to_3900()
    {
        using var h = new AccountingHarness();
        var chart = await ChartAndYearAsync(h);
        var ob = h.Opening(h.DateOf(Conversion));
        var draft = await ob.SaveBatchAsync(h.Admin(), new SaveOpeningBalancesRequest { Entries = [Dr(chart["1111"], 6_000), Cr(chart["2111"], 5_000)] });
        draft.Summary.Should().Match<OpeningSummaryModel>(s => s.Difference == 1_000 && !s.IsBalanced && s.PlugSide == "Credit");

        await ((Func<Task>)(() => ob.PostAsync(h.Admin(), new PostOpeningBalancesRequest()))).Should().ThrowAsync<BadRequestException>()
            .WithMessage("Debits and credits differ by 1,000.00. Confirm the difference to post it to Opening Balance Equity (3900).");
        (await h.Db.JournalEntries.CountAsync()).Should().Be(0);

        var result = await ob.PostAsync(h.Admin(), new PostOpeningBalancesRequest { AcknowledgeDifference = true });

        result.JournalEntryNumber.Should().MatchRegex(@"^JE-\d{4}-00001$", "without the shared counter the module numbers its own journal");
        h.NewScope();
        var journal = await h.Db.JournalEntries.Include(j => j.Lines).SingleAsync();
        journal.TotalDebit.Should().Be(6_000);
        journal.TotalCredit.Should().Be(6_000);
        journal.Lines.Should().HaveCount(3);
        journal.Lines.Single(l => l.LineNumber == 3).Should().Match<JournalEntryLine>(l => l.AccountId == chart["3900"].Id && l.CreditAmount == 1_000 && l.DebitAmount == 0);
        var plug = await h.Db.ChartOfAccounts.SingleAsync(a => a.AccountCode == "3900");
        plug.Should().Match<ChartOfAccount>(a => a.OpeningBalanceCredit == 1_000 && a.CurrentBalance == 1_000 && a.HasTransactions);
        (await h.Db.CoaSetupStatuses.SingleAsync()).OpeningBalancesBalanced.Should().BeTrue("the difference was accepted");

        var model = await h.Opening().GetAsync(h.Viewer());
        model.Groups[2].Rows.Single(r => r.IsPlug).CreditAmount.Should().Be(1_000, "posted rows show the account's opening balance, plug included");
        model.Summary.Difference.Should().Be(1_000);
    }

    [Fact]
    public async Task Credits_above_debits_debit_3900()
    {
        using var h = new AccountingHarness();
        var chart = await ChartAndYearAsync(h);
        var ob = h.Opening(h.DateOf(Conversion));
        await ob.SaveBatchAsync(h.Admin(), new SaveOpeningBalancesRequest { Entries = [Dr(chart["1111"], 100), Cr(chart["2111"], 250.5m)] });
        (await ob.SummaryAsync(h.Viewer())).PlugSide.Should().Be("Debit");

        await ob.PostAsync(h.Admin(), new PostOpeningBalancesRequest { AcknowledgeDifference = true });

        h.NewScope();
        var plug = await h.Db.ChartOfAccounts.SingleAsync(a => a.AccountCode == "3900");
        plug.OpeningBalanceDebit.Should().Be(150.5m);
        plug.CurrentBalance.Should().Be(-150.5m, "equity is credit-normal");
    }

    [Fact]
    public async Task T_OB_04_posted_balances_cannot_be_changed_or_posted_again()
    {
        using var h = new AccountingHarness();
        var chart = await ChartAndYearAsync(h);
        var ob = h.Opening(h.DateOf(Conversion));
        await ob.SaveBatchAsync(h.Admin(), new SaveOpeningBalancesRequest { Entries = [Dr(chart["1111"], 10), Cr(chart["3111"], 10)] });
        await ob.PostAsync(h.Admin(), new PostOpeningBalancesRequest());

        await ((Func<Task>)(() => ob.SaveBatchAsync(h.Admin(), new SaveOpeningBalancesRequest { Entries = [Dr(chart["1111"], 20)] })))
            .Should().ThrowAsync<ConflictException>().WithMessage("Opening balances were already posted.");
        await ((Func<Task>)(() => ob.PostAsync(h.Admin(), new PostOpeningBalancesRequest()))).Should().ThrowAsync<ConflictException>()
            .WithMessage("Opening balances were already posted.");
        await ((Func<Task>)(() => ob.PostAsync(h.Admin(), new PostOpeningBalancesRequest { Skip = true }))).Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task T_OB_05_skip_posts_nothing_and_lets_the_setup_proceed()
    {
        using var h = new AccountingHarness();
        var chart = await ChartAndYearAsync(h);
        await h.AssignRequiredDefaultsAsync(chart);
        var ob = h.Opening(h.DateOf(null));

        var result = await ob.PostAsync(h.Admin(), new PostOpeningBalancesRequest { Skip = true });

        result.JournalEntryNumber.Should().BeNull();
        result.Summary.TotalDebits.Should().Be(0);
        (await h.Db.JournalEntries.CountAsync()).Should().Be(0);
        (await h.Db.ChartOfAccounts.AnyAsync(a => a.HasTransactions || a.CurrentBalance != 0)).Should().BeFalse();
        var status = await h.Setup.GetAsync(h.Viewer());
        status.Should().Match<SetupStatusModel>(s => s.OpeningBalancesEntered && s.OpeningBalancesBalanced && s.Status == SetupStatuses.BalancesEntered);
        h.Events.Of<OpeningBalancePostedEvent>().Should().ContainSingle().Which.Skipped.Should().BeTrue();
        (await ob.GetAsync(h.Viewer())).IsSkipped.Should().BeTrue();
        (await h.Get<IOpeningDateLockSource>().GetLockedDateAsync(h.Org)).Should().BeNull("skipped without a date: nothing to lock");
        (await h.Setup.CompleteAsync(h.Admin())).Status.Should().Be(SetupStatuses.Complete);
    }

    [Fact]
    public async Task Skip_is_refused_while_drafts_exist()
    {
        using var h = new AccountingHarness();
        var chart = await ChartAndYearAsync(h);
        var ob = h.Opening(h.DateOf(Conversion));
        await ob.SaveBatchAsync(h.Admin(), new SaveOpeningBalancesRequest { Entries = [Dr(chart["1111"], 10)] });

        await ((Func<Task>)(() => ob.PostAsync(h.Admin(), new PostOpeningBalancesRequest { Skip = true }))).Should().ThrowAsync<ConflictException>()
            .WithMessage(OpeningBalanceService.SkipWithDraftsMessage);
    }

    [Fact]
    public async Task T_OB_06_07_entries_are_validated_all_at_once()
    {
        using var h = new AccountingHarness();
        var chart = await ChartAndYearAsync(h);
        var foreign = await h.SeedChartAsync(Guid.NewGuid(), ("1111", RootTypes.Asset, AccountTypes.Cash));
        var ob = h.Opening(h.DateOf(Conversion));

        async Task<string> Refusal(params OpeningEntryInput[] entries) =>
            (await ((Func<Task>)(() => ob.SaveBatchAsync(h.Admin(), new SaveOpeningBalancesRequest { Entries = [.. entries] })))
                .Should().ThrowAsync<OpeningBalanceValidationException>()).Which.Message;

        (await Refusal(Dr(chart["111"], 10))).Should().Be("Only Level 4 accounts accept opening balances.");                 // T-OB-06
        (await Refusal(new OpeningEntryInput { AccountUuid = chart["1111"].Uuid, DebitAmount = 5, CreditAmount = 5 }))
            .Should().Be("Cannot have both debit and credit amounts.");                                                        // T-OB-07
        (await Refusal(Cr(chart["3900"], 10))).Should().Be("Opening Balance Equity (3900) is calculated automatically.");
        (await Refusal(Dr(chart["1111"], -1))).Should().Be("Opening balances cannot be negative.");
        (await Refusal(Dr(foreign["1111"], 10))).Should().Be("Account not found.");

        var all = (await ((Func<Task>)(() => ob.SaveBatchAsync(h.Admin(), new SaveOpeningBalancesRequest
            { Entries = [Dr(chart["111"], 1), Cr(chart["3900"], 1), Dr(chart["1112"], 1), Dr(chart["1112"], 2)] })))
            .Should().ThrowAsync<OpeningBalanceValidationException>()).Which;
        all.Errors.Should().HaveCount(3);
        all.Errors.Select(e => e.AccountCode).Should().Equal("111", "3900", "1112");
        (await h.Db.OpeningBalanceEntries.CountAsync()).Should().Be(0, "nothing is saved when any entry is refused");

        chart["1113"].IsActive = false;
        await h.Db.SaveChangesAsync();
        (await Refusal(Dr(chart["1113"], 1))).Should().Be("Account 1113 is inactive.");
    }

    [Fact]
    public async Task Saving_upserts_and_a_zero_row_deletes_the_draft()
    {
        using var h = new AccountingHarness();
        var chart = await ChartAndYearAsync(h);
        var ob = h.Opening(h.DateOf(Conversion));
        await ob.SaveBatchAsync(h.Admin(), new SaveOpeningBalancesRequest { Entries = [Dr(chart["1111"], 100), Cr(chart["4111"], 40)] });
        var model = await ob.SaveBatchAsync(h.Admin(), new SaveOpeningBalancesRequest { Entries = [Dr(chart["1111"], 0), Cr(chart["4111"], 55.123456m)] });

        (await h.Db.OpeningBalanceEntries.CountAsync()).Should().Be(1);
        var revenue = model.Groups.Single(g => g.RootType == RootTypes.Revenue).Rows.Single();
        revenue.CreditAmount.Should().Be(55.1235m);
        revenue.Warning.Should().Be(OpeningBalanceService.ProfitAndLossWarning, "BR-OB-007 warns but does not block");
        model.Groups[0].Rows.Should().OnlyContain(r => r.Warning == null);
        (await h.Db.OpeningBalanceEntries.SingleAsync()).Should().Match<OpeningBalanceEntry>(e => e.EntryStatus == OpeningEntryStatuses.Draft && e.ConversionDate == null);
    }

    [Fact]
    public async Task Posting_needs_the_opening_balance_date_inside_the_first_fiscal_year()
    {
        using var h = new AccountingHarness();
        var chart = await h.SeedChartAsync(null, AccountingHarness.StandardLeaves);
        await h.Opening().SaveBatchAsync(h.Admin(), new SaveOpeningBalancesRequest { Entries = [Dr(chart["1111"], 10), Cr(chart["3111"], 10)] });
        var post = new PostOpeningBalancesRequest();

        await ((Func<Task>)(() => h.Opening().PostAsync(h.Admin(), post))).Should().ThrowAsync<BadRequestException>()
            .WithMessage("Set the opening balance date in Finance settings first.");
        await ((Func<Task>)(() => h.Opening(h.DateOf(null)).PostAsync(h.Admin(), post))).Should().ThrowAsync<BadRequestException>()
            .WithMessage("Set the opening balance date in Finance settings first.");
        await ((Func<Task>)(() => h.Opening(h.DateOf(Conversion)).PostAsync(h.Admin(), post))).Should().ThrowAsync<BadRequestException>()
            .WithMessage("Set up a fiscal year first.");

        await h.AddFiscalYearAsync(); // 2026
        await h.Fiscal().CreateAsync(h.Admin(), new CreateFiscalYearRequest { StartDate = new DateOnly(2027, 1, 1), EndDate = new DateOnly(2027, 12, 31) });
        await ((Func<Task>)(() => h.Opening(h.DateOf(new DateOnly(2027, 1, 1))).PostAsync(h.Admin(), post))).Should().ThrowAsync<BadRequestException>()
            .WithMessage("The opening balance date must fall within the first fiscal year.");
        await ((Func<Task>)(() => h.Opening(h.DateOf(new DateOnly(2025, 12, 31))).PostAsync(h.Admin(), post))).Should().ThrowAsync<BadRequestException>()
            .WithMessage("The opening balance date must fall within the first fiscal year.");

        (await h.Opening(h.DateOf(new DateOnly(2026, 12, 31))).PostAsync(h.Admin(), post)).JournalEntryNumber.Should().NotBeNull();
    }

    [Fact]
    public async Task Nothing_to_post_is_refused()
    {
        using var h = new AccountingHarness();
        await ChartAndYearAsync(h);
        await ((Func<Task>)(() => h.Opening(h.DateOf(Conversion)).PostAsync(h.Admin(), new PostOpeningBalancesRequest())))
            .Should().ThrowAsync<BadRequestException>().WithMessage(OpeningBalanceService.NothingToPostMessage);
    }

    [Fact]
    public async Task The_subledger_reference_compares_finance_totals_with_the_default_accounts()
    {
        using var h = new AccountingHarness();
        var chart = await ChartAndYearAsync(h);
        await h.AssignRequiredDefaultsAsync(chart); // A/R 1113, A/P 2111, no inventory default
        var totals = new FakeSubledger { Totals = new OpeningSubledgerTotals(45_000, 65_000, 80_000, "PKR") };
        var ob = h.Opening(h.DateOf(Conversion), totals);
        await ob.SaveBatchAsync(h.Admin(), new SaveOpeningBalancesRequest { Entries = [Dr(chart["1113"], 45_000), Cr(chart["2111"], 60_000)] });

        var subledger = (await ob.GetAsync(h.Viewer())).Subledger!;

        subledger.Should().Match<SubledgerReferenceModel>(s => s.CustomerOpeningTotal == 45_000 && s.SupplierOpeningTotal == 65_000
            && s.OpeningStockValue == 80_000 && s.CurrencyCode == "PKR"
            && s.ReceivableAccountBalance == 45_000 && s.ReceivableAccountCode == "1113"
            && s.PayableAccountBalance == 60_000 && s.InventoryAccountBalance == null);

        (await h.Opening(h.DateOf(Conversion)).GetAsync(h.Viewer())).Subledger.Should().BeNull("Finance is optional");
        (await h.Opening(h.DateOf(Conversion), new FakeSubledger { Throw = true }).GetAsync(h.Viewer())).Subledger.Should().BeNull("information only");
    }

    [Fact]
    public async Task The_date_lock_source_pins_the_conversion_date_only_once_posted_and_only_for_its_organization()
    {
        using var h = new AccountingHarness();
        var chart = await ChartAndYearAsync(h);
        var source = h.Get<IOpeningDateLockSource>();
        source.Should().BeOfType<GlOpeningDateLockSource>();
        source.Description.Should().Be("General ledger opening balances");
        (await source.GetLockedDateAsync(h.Org)).Should().BeNull("no status row yet");

        var ob = h.Opening(h.DateOf(Conversion));
        await ob.SaveBatchAsync(h.Admin(), new SaveOpeningBalancesRequest { Entries = [Dr(chart["1111"], 10), Cr(chart["3111"], 10)] });
        (await source.GetLockedDateAsync(h.Org)).Should().BeNull("drafts do not lock the date");

        await ob.PostAsync(h.Admin(), new PostOpeningBalancesRequest());
        (await source.GetLockedDateAsync(h.Org)).Should().Be(Conversion);
        (await source.GetLockedDateAsync(Guid.NewGuid())).Should().BeNull();

        // D-44 — the source must not depend on Finance's date service/provider (DI cycle).
        typeof(GlOpeningDateLockSource).GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType)
            .Should().NotContain(t => t == typeof(IOpeningBalanceDateProvider) || t.Name == "IOpeningBalanceDateService");

        await h.Setup.ResetAsync(h.Admin());
        (await source.GetLockedDateAsync(h.Org)).Should().BeNull("reset releases the lock (D-17)");
    }

    [Fact]
    public async Task Opening_balances_are_per_organization_and_need_the_right_permissions()
    {
        using var h = new AccountingHarness();
        var chart = await ChartAndYearAsync(h);
        await h.Opening().SaveBatchAsync(h.Admin(), new SaveOpeningBalancesRequest { Entries = [Dr(chart["1111"], 10)] });
        var other = Guid.NewGuid();

        var theirs = await h.Opening().GetAsync(h.Viewer(other));
        theirs.Groups.Should().OnlyContain(g => g.Rows.Count == 0);
        theirs.Summary.EntryCount.Should().Be(0);

        await ((Func<Task>)(() => h.Opening().SaveBatchAsync(h.Viewer(), new SaveOpeningBalancesRequest()))).Should().ThrowAsync<ForbiddenException>();
        await ((Func<Task>)(() => h.Opening().PostAsync(h.Viewer(), new PostOpeningBalancesRequest()))).Should().ThrowAsync<ForbiddenException>();
        await ((Func<Task>)(() => h.Opening().GetAsync(h.Nobody()))).Should().ThrowAsync<ForbiddenException>();
    }
}
