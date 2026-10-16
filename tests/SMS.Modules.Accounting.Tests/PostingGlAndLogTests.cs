using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Modules.Accounting.Posting;
using SMS.Shared.Exceptions;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;
using Xunit;
using static SMS.Modules.Accounting.Tests.PostingHarness;

namespace SMS.Modules.Accounting.Tests;

/// <summary>A44 CORE — internal GL target (§3, D-5, D-8, D-9, D-26), retry/DeadLetter, posting log, settings, entity sync, account map.</summary>
public sealed class PostingGlAndLogTests
{
    private static string Key(Guid id) => $"finance:SalesInvoice:{id}";

    [Fact]
    public async Task GL_journal_has_type_reference_source_and_moves_balances()
    {
        using var h = new PostingHarness(withExternal: false);
        await h.SetupGlAsync();
        var id = Guid.NewGuid();

        await h.Gateway.PostAsync(Invoice(id));

        var j = (await h.JournalsAsync()).Single();
        j.EntryType.Should().Be("SALES_INVOICE");
        j.Reference.Should().Be("SI-2026-00001");
        j.SourceModule.Should().Be("finance");
        j.SourceDocumentUuid.Should().Be(id);
        j.Status.Should().Be(JournalStatuses.Posted);
        j.EntryDate.Should().Be(new DateOnly(2026, 10, 15));
        j.CurrencyCode.Should().Be("PKR");
        j.TotalDebit.Should().Be(118m);
        j.TotalCredit.Should().Be(118m);
        j.Lines.Should().HaveCount(3);
        (await h.BalanceAsync("1113")).Should().Be(118m);
        (await h.BalanceAsync("4111")).Should().Be(100m);
        (await h.BalanceAsync("2112")).Should().Be(18m);
        (await h.Db.ChartOfAccounts.IgnoreQueryFilters().SingleAsync(a => a.OrganizationId == h.OrgA && a.AccountCode == "1113"))
            .HasTransactions.Should().BeTrue();
    }

    [Fact]
    public async Task Explicit_account_code_wins_over_the_role()
    {
        using var h = new PostingHarness(withExternal: false);
        await h.SetupGlAsync();
        var tx = new FinancialTransaction
        {
            DocumentType = FinDocType.Payment, SourceDocumentId = Guid.NewGuid(), SourceModule = "finance", SourceDocumentNumber = "RCPT-1",
            TransactionDate = new DateTime(2026, 10, 15), CurrencyCode = "PKR",
            Lines = [new FinancialTransactionLine { LineNumber = 1, AccountRole = DefaultKeys.Cash, AccountCode = "1112", Debit = 50m },
                     L(2, DefaultKeys.Receivable, 0, 50m)],
        };
        await h.Gateway.PostAsync(tx);
        (await h.BalanceAsync("1112")).Should().Be(50m);
        (await h.BalanceAsync("1111")).Should().Be(0m);
        (await h.JournalsAsync()).Single().EntryType.Should().Be("PAYMENT");
    }

    [Fact]
    public async Task Foreign_currency_is_converted_to_base_and_the_rounding_difference_goes_to_DEFAULT_ROUNDING()
    {
        using var h = new PostingHarness(withExternal: false);
        await h.SetupGlAsync();
        var tx = new FinancialTransaction
        {
            DocumentType = FinDocType.SalesInvoice, SourceDocumentId = Guid.NewGuid(), SourceModule = "finance", SourceDocumentNumber = "SI-USD",
            TransactionDate = new DateTime(2026, 10, 15), CurrencyCode = "USD", ExchangeRate = 1.5m,
            Lines = [L(1, DefaultKeys.Receivable, 10m, 0), L(2, DefaultKeys.Revenue, 0, 3.33m), L(3, DefaultKeys.Revenue, 0, 3.33m),
                     L(4, DefaultKeys.TaxPayable, 0, 3.34m)],
        };

        (await h.Gateway.PostAsync(tx)).Status.Should().Be(PostingStatus.Success);

        var j = (await h.JournalsAsync()).Single();
        j.TotalDebit.Should().Be(j.TotalCredit);
        j.TotalDebit.Should().Be(15.01m); // 15.00 + 0.01 rounding
        var rounding = j.Lines.Single(l => l.Memo == "Rounding difference");
        rounding.DebitAmount.Should().Be(0.01m);
        (await h.BalanceAsync("5112")).Should().Be(0.01m);
        (await h.BalanceAsync("1113")).Should().Be(15m);
    }

    [Fact]
    public async Task Rounding_without_a_DEFAULT_ROUNDING_account_fails_and_writes_nothing()
    {
        using var h = new PostingHarness(withExternal: false);
        await h.SetupGlAsync(rounding: false);
        var id = Guid.NewGuid();
        var tx = new FinancialTransaction
        {
            DocumentType = FinDocType.SalesInvoice, SourceDocumentId = id, SourceModule = "finance", TransactionDate = new DateTime(2026, 10, 15),
            CurrencyCode = "USD", ExchangeRate = 1.5m,
            Lines = [L(1, DefaultKeys.Receivable, 10m, 0), L(2, DefaultKeys.Revenue, 0, 3.33m), L(3, DefaultKeys.Revenue, 0, 3.33m),
                     L(4, DefaultKeys.Revenue, 0, 3.34m)],
        };
        (await h.Gateway.PostAsync(tx)).Status.Should().Be(PostingStatus.PartialFailure);
        (await h.PostingAsync(Key(id))).Targets.Single().LastError.Should().StartWith("UNMAPPED_ROLE: DEFAULT_ROUNDING");
        (await h.JournalsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Foreign_currency_without_a_rate_fails()
    {
        using var h = new PostingHarness(withExternal: false);
        await h.SetupGlAsync();
        var id = Guid.NewGuid();
        await h.Gateway.PostAsync(Invoice(id, currency: "USD"));
        (await h.PostingAsync(Key(id))).Targets.Single().LastError.Should().StartWith("EXCHANGE_RATE_MISSING");
    }

    [Fact]
    public async Task Closed_period_fails_with_PERIOD_CLOSED_and_the_job_posts_it_once_the_period_is_open()
    {
        using var h = new PostingHarness(withExternal: false);
        await h.SetupGlAsync();
        await h.SetPeriodStatusAsync(10, PeriodStatuses.Locked);
        var id = Guid.NewGuid();

        var result = await h.Gateway.PostAsync(Invoice(id));

        result.Status.Should().Be(PostingStatus.PartialFailure);
        var row = (await h.PostingAsync(Key(id))).Targets.Single();
        row.Status.Should().Be(PostingTargetStatuses.Failed);
        row.LastError.Should().StartWith("PERIOD_CLOSED: 2026-10-15");

        // Not due yet: the job leaves it alone.
        h.NewScope();
        (await h.Get<PostingRetryJob>().RunAsync()).Should().Be(0);

        await h.SetPeriodStatusAsync(10, PeriodStatuses.Open);
        h.Clock.Advance(TimeSpan.FromSeconds(31));
        h.NewScope();
        (await h.Get<PostingRetryJob>().RunAsync()).Should().Be(1);

        h.NewScope();
        row = (await h.PostingAsync(Key(id))).Targets.Single();
        row.Status.Should().Be(PostingTargetStatuses.Posted);
        row.Attempts.Should().Be(0);
        (await h.JournalsAsync()).Should().ContainSingle();
        (await h.BalanceAsync("1113")).Should().Be(118m);
    }

    [Fact]
    public async Task D9_backoff_30s_2m_8m_32m_2h_then_DeadLetter()
    {
        using var h = new PostingHarness();
        h.External.Throw = new InvalidOperationException("still down");
        var id = Guid.NewGuid();
        await h.Gateway.PostAsync(Invoice(id));
        var waits = new List<TimeSpan>();

        for (var i = 0; i < 6; i++)
        {
            h.NewScope();
            var row = (await h.PostingAsync(Key(id))).Targets.Single();
            if (row.Status == PostingTargetStatuses.DeadLetter) break;
            waits.Add(row.NextAttemptAt!.Value - row.LastAttemptAt!.Value);
            h.Clock.Now = new DateTimeOffset(row.NextAttemptAt.Value, TimeSpan.Zero);
            (await h.Get<PostingRetryJob>().RunAsync()).Should().Be(1);
        }

        waits.Should().Equal(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(8), TimeSpan.FromMinutes(32), TimeSpan.FromHours(2));
        h.NewScope();
        var dead = (await h.PostingAsync(Key(id))).Targets.Single();
        dead.Status.Should().Be(PostingTargetStatuses.DeadLetter);
        dead.Attempts.Should().Be(6);
        dead.NextAttemptAt.Should().BeNull();
        h.External.Posts.Should().HaveCount(6);

        h.Clock.Advance(TimeSpan.FromDays(1));
        (await h.Get<PostingRetryJob>().RunAsync()).Should().Be(0, "a dead letter is not retried automatically");
    }

    [Fact]
    public async Task T26_void_posts_a_reversing_journal_once()
    {
        using var h = new PostingHarness();
        await h.SetupGlAsync();
        var id = Guid.NewGuid();
        await h.Gateway.PostAsync(Invoice(id));

        var v = await h.Gateway.VoidAsync(VoidOf(id));

        v.Status.Should().Be(PostingStatus.Success);
        var journals = await h.JournalsAsync();
        journals.Should().HaveCount(2);
        journals[1].ReversesEntryId.Should().Be(journals[0].Id);
        journals[1].IdempotencyKey.Should().Be(Key(id) + ":void");
        journals[1].EntryDate.Should().Be(new DateOnly(2026, 10, 16));
        (await h.BalanceAsync("1113")).Should().Be(0m);
        (await h.BalanceAsync("4111")).Should().Be(0m);
        h.External.Voids.Should().ContainSingle();
        (await h.PostingAsync(Key(id) + ":void")).Kind.Should().Be(PostingKinds.Void);

        (await h.Gateway.VoidAsync(VoidOf(id))).TargetResults
            .Single(r => r.TargetCode == PostingTargetCodes.InternalGl).Status.Should().Be(TargetPostingStatus.Posted);
        (await h.JournalsAsync()).Should().HaveCount(2);
    }

    [Fact]
    public async Task T60_void_of_a_document_never_posted_to_the_GL_is_Skipped_NOT_POSTED()
    {
        using var h = new PostingHarness(withExternal: false);
        await h.SetupGlAsync();
        var id = Guid.NewGuid();

        var v = await h.Gateway.VoidAsync(VoidOf(id));

        v.Status.Should().Be(PostingStatus.Success);
        v.TargetResults.Single().Status.Should().Be(TargetPostingStatus.Skipped);
        v.TargetResults.Single().Error.Should().StartWith("NOT_POSTED");
        (await h.JournalsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task T61_reverse_is_a_GL_reversal_and_the_external_ReverseAsync()
    {
        using var h = new PostingHarness();
        await h.SetupGlAsync();
        var id = Guid.NewGuid();
        var bill = new FinancialTransaction
        {
            DocumentType = FinDocType.PurchaseBill, SourceDocumentId = id, SourceModule = "suppliers", SourceDocumentNumber = "BILL-1",
            TransactionDate = new DateTime(2026, 10, 10), CurrencyCode = "PKR", PartnerType = PartnerType.Vendor,
            Lines = [L(1, DefaultKeys.Cogs, 500m, 0), L(2, DefaultKeys.Payable, 0, 500m)],
        };
        await h.Gateway.PostAsync(bill);

        var r = await h.Gateway.ReverseAsync(VoidOf(id, FinDocType.PurchaseBill, "suppliers"));

        r.Status.Should().Be(PostingStatus.Success);
        h.External.Reverses.Should().ContainSingle();
        h.External.Voids.Should().BeEmpty();
        (await h.BalanceAsync("2111")).Should().Be(0m);
        (await h.PostingAsync("suppliers:PurchaseBill:" + id + ":void")).Kind.Should().Be(PostingKinds.Reverse);
    }

    [Fact]
    public async Task REV2_a_void_before_the_GL_post_succeeded_stops_the_original_for_good()
    {
        using var h = new PostingHarness(withExternal: false);
        await h.SetupGlAsync();
        await h.SetPeriodStatusAsync(10, PeriodStatuses.Locked);
        var id = Guid.NewGuid();
        await h.Gateway.PostAsync(Invoice(id));

        await h.Gateway.VoidAsync(VoidOf(id));

        var original = (await h.PostingAsync(Key(id))).Targets.Single();
        original.Status.Should().Be(PostingTargetStatuses.Skipped);
        original.LastError.Should().StartWith("VOIDED_BEFORE_POSTING");
        await h.SetPeriodStatusAsync(10, PeriodStatuses.Open);
        h.Clock.Advance(TimeSpan.FromHours(3));
        h.NewScope();
        (await h.Get<PostingRetryJob>().RunAsync()).Should().Be(0);
        (await h.JournalsAsync()).Should().BeEmpty();

        // A late re-post of the voided document does not reach the GL either.
        var late = await h.Gateway.PostAsync(Invoice(id, net: 150m, tax: 27m));
        late.TargetResults.Single().Error.Should().StartWith("VOIDED");
        (await h.JournalsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task D8_AutoPostToGL_off_creates_Draft_journals_that_move_no_balance()
    {
        using var h = new PostingHarness(withExternal: false);
        await h.SetupGlAsync(autoPost: false);
        var id = Guid.NewGuid();

        await h.Gateway.PostAsync(Invoice(id));
        var draft = (await h.JournalsAsync()).Single();
        draft.Status.Should().Be(JournalStatuses.Draft);
        draft.PostedAt.Should().BeNull();
        (await h.BalanceAsync("1113")).Should().Be(0m);

        // Update replaces the draft; void deletes it.
        await h.Gateway.PostAsync(Invoice(id, net: 50m, tax: 9m));
        (await h.JournalsAsync()).Should().ContainSingle(j => j.TotalDebit == 59m && j.Status == JournalStatuses.Draft);
        var v = await h.Gateway.VoidAsync(VoidOf(id));
        v.TargetResults.Single().Error.Should().StartWith("DRAFT_DISCARDED");
        (await h.JournalsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Disabled_internal_accounting_is_inactive_and_a_retry_marks_it_Skipped()
    {
        using var h = new PostingHarness(withExternal: false);
        await h.SetupGlAsync();
        await h.SetPeriodStatusAsync(10, PeriodStatuses.Locked);
        var id = Guid.NewGuid();
        await h.Gateway.PostAsync(Invoice(id));
        await h.Get<ITenantFinanceConfigService>().UpdateAsync(h.Admin(), new UpdatePostingConfigRequest { InternalAccountingEnabled = false });

        h.Clock.Advance(TimeSpan.FromMinutes(1));
        h.NewScope();
        await h.Get<PostingRetryJob>().RunAsync();

        var row = (await h.PostingAsync(Key(id))).Targets.Single();
        row.Status.Should().Be(PostingTargetStatuses.Skipped);
        row.LastError.Should().StartWith("TARGET_INACTIVE");
    }

    // ── posting log ────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Log_lists_filters_and_shows_detail_with_journals()
    {
        using var h = new PostingHarness();
        await h.SetupGlAsync();
        h.External.Throw = new InvalidOperationException("down");
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await h.Gateway.PostAsync(Invoice(a, number: "SI-A"));
        h.External.Throw = null;
        await h.Gateway.PostAsync(Invoice(b, number: "SI-B"));
        await h.Gateway.PostAsync(Invoice(b, net: 10m, tax: 0m, number: "SI-B"));

        var all = await h.Log.ListAsync(h.IntegrationViewer(), new PostingLogQuery());
        all.TotalRecords.Should().Be(2);
        var failed = await h.Log.ListAsync(h.Viewer(), new PostingLogQuery { Status = PostingTargetStatuses.Failed });
        failed.Data.Should().ContainSingle(x => x.SourceDocumentNumber == "SI-A" && x.OverallStatus == PostingTargetStatuses.Failed);
        (await h.Log.ListAsync(h.Viewer(), new PostingLogQuery { Search = "SI-B" })).Data.Single().Version.Should().Be(2);
        (await h.Log.ListAsync(h.Viewer(), new PostingLogQuery { Status = PostingTargetStatuses.Failed, TargetCode = PostingTargetCodes.InternalGl }))
            .TotalRecords.Should().Be(0);

        var uuid = (await h.PostingAsync(Key(b))).Uuid;
        var detail = await h.Log.GetAsync(h.Viewer(), uuid);
        detail.Lines.Should().HaveCount(3);
        detail.Lines.First().AccountRole.Should().Be(DefaultKeys.Receivable);
        detail.Journals.Should().HaveCount(3);
        detail.Journals[0].IsReversed.Should().BeTrue();
        detail.Journals[1].ReversesEntryNumber.Should().Be(detail.Journals[0].EntryNumber);
        detail.Journals[2].IsReversed.Should().BeFalse();
    }

    [Fact]
    public async Task Log_retry_resets_and_dispatches_now_and_skip_dismisses()
    {
        using var h = new PostingHarness();
        h.External.Throw = new InvalidOperationException("down");
        var id = Guid.NewGuid();
        await h.Gateway.PostAsync(Invoice(id));
        var uuid = (await h.PostingAsync(Key(id))).Uuid;

        h.External.Throw = null;
        var retried = await h.Log.RetryAsync(h.IntegrationAdmin(), uuid, null);
        retried.Posting.Targets.Single().Status.Should().Be(PostingTargetStatuses.Queued);
        retried.Message("Retried").Should().Be("Retried.");
        await FluentActions.Invoking(() => h.Log.RetryAsync(h.IntegrationAdmin(), uuid, null)).Should().ThrowAsync<ConflictException>();

        var other = Guid.NewGuid();
        h.External.Throw = new InvalidOperationException("down");
        await h.Gateway.PostAsync(Invoice(other));
        var otherUuid = (await h.PostingAsync(Key(other))).Uuid;
        var skipped = await h.Log.SkipAsync(h.IntegrationAdmin(), otherUuid, PostingTargetCodes.QuickBooks, "entered by hand");
        skipped.Posting.Targets.Single().Status.Should().Be(PostingTargetStatuses.Skipped);
        skipped.Posting.Targets.Single().LastError.Should().Be("Skipped by Ian Integrator: entered by hand");
        await FluentActions.Invoking(() => h.Log.SkipAsync(h.IntegrationAdmin(), otherUuid, null, null)).Should().ThrowAsync<ConflictException>();

        await FluentActions.Invoking(() => h.Log.RetryAsync(h.Viewer(), uuid, null)).Should().ThrowAsync<ForbiddenException>();
    }

    [Fact]
    public async Task REV18_retry_and_skip_respect_segregation_of_duties_per_target()
    {
        using var h = new PostingHarness();
        await h.SetupGlAsync();
        await h.SetPeriodStatusAsync(10, PeriodStatuses.Locked);           // GL fails: PERIOD_CLOSED
        h.External.Throw = new InvalidOperationException("down");         // QuickBooks fails
        var id = Guid.NewGuid();
        await h.Gateway.PostAsync(Invoice(id));
        var uuid = (await h.PostingAsync(Key(id))).Uuid;

        // An integration-only admin cannot skip (suppress) the GL journal.
        await FluentActions.Invoking(() => h.Log.SkipAsync(h.IntegrationAdmin(), uuid, PostingTargetCodes.InternalGl, "nope"))
            .Should().ThrowAsync<ForbiddenException>().WithMessage(PostingLogService.NoTouchableTarget);
        // A GL-only admin cannot touch the QuickBooks row.
        await FluentActions.Invoking(() => h.Log.RetryAsync(h.Admin(), uuid, PostingTargetCodes.QuickBooks))
            .Should().ThrowAsync<ForbiddenException>().WithMessage(PostingLogService.NoTouchableTarget);

        // No target code: only the caller's own side is acted on, and the message says what was left out.
        var skipped = await h.Log.SkipAsync(h.IntegrationAdmin(), uuid, null, null);
        skipped.Acted.Should().Equal(PostingTargetCodes.QuickBooks);
        skipped.NotPermitted.Should().Equal(PostingTargetCodes.InternalGl);
        skipped.Message("Skipped").Should().Be("Skipped: quickbooks. Not skipped (no permission): internal_gl.");
        skipped.Posting.Targets.Single(t => t.TargetCode == PostingTargetCodes.InternalGl).Status.Should().Be(PostingTargetStatuses.Failed);
        skipped.Posting.Targets.Single(t => t.TargetCode == PostingTargetCodes.QuickBooks).Status.Should().Be(PostingTargetStatuses.Skipped);

        // Nothing left the integration admin may touch → 403; the GL admin retries the GL row.
        await FluentActions.Invoking(() => h.Log.RetryAsync(h.IntegrationAdmin(), uuid, null))
            .Should().ThrowAsync<ForbiddenException>().WithMessage(PostingLogService.NoTouchableTarget);
        await h.SetPeriodStatusAsync(10, PeriodStatuses.Open);
        var retried = await h.Log.RetryAsync(h.Admin(), uuid, null);
        retried.Acted.Should().Equal(PostingTargetCodes.InternalGl);
        retried.NotPermitted.Should().BeEmpty();
        retried.Posting.Targets.Single(t => t.TargetCode == PostingTargetCodes.InternalGl).Status.Should().Be(PostingTargetStatuses.Posted);

        // A caller holding both may act on every target.
        var other = Guid.NewGuid();
        await h.Gateway.PostAsync(Invoice(other));
        var both = await h.Log.SkipAsync(h.Both(), (await h.PostingAsync(Key(other))).Uuid, null, null);
        both.Acted.Should().Equal(PostingTargetCodes.QuickBooks);
        both.NotPermitted.Should().BeEmpty();
    }

    [Fact]
    public async Task D33_by_source_lists_post_and_void_with_canAct_per_caller()
    {
        using var h = new PostingHarness();
        await h.SetupGlAsync();
        h.External.Throw = new InvalidOperationException("down");
        var id = Guid.NewGuid();
        await h.Gateway.PostAsync(Invoice(id));                          // GL Posted, QuickBooks Failed
        await h.Gateway.VoidAsync(VoidOf(id));                           // GL reversal Posted, QuickBooks Failed

        var forIntegration = await h.Log.BySourceAsync(h.IntegrationAdmin(), id);
        forIntegration.Select(p => p.Kind).Should().Equal(PostingKinds.Post, PostingKinds.Void);
        forIntegration.Should().OnlyContain(p => p.SourceDocumentUuid == id);
        forIntegration[0].Targets.Single(t => t.TargetCode == PostingTargetCodes.QuickBooks).CanAct.Should().BeFalse(
            "the void skipped the original's open QuickBooks row (D-45), so nothing is left to act on");
        forIntegration[1].Targets.Single(t => t.TargetCode == PostingTargetCodes.QuickBooks).CanAct.Should().BeTrue();
        forIntegration[1].Targets.Single(t => t.TargetCode == PostingTargetCodes.InternalGl).CanAct.Should().BeFalse();

        var forGl = await h.Log.BySourceAsync(h.Admin(), id);
        forGl[1].Targets.Single(t => t.TargetCode == PostingTargetCodes.QuickBooks).CanAct.Should().BeFalse("GL admin may not touch QuickBooks");

        // Unknown document → empty, not 404; other organization → empty; no read permission → 403.
        (await h.Log.BySourceAsync(h.Viewer(), Guid.NewGuid())).Should().BeEmpty();
        (await h.Log.BySourceAsync(h.Admin(h.OrgB), id)).Should().BeEmpty();
        await FluentActions.Invoking(() => h.Log.BySourceAsync(new SMS.Modules.Accounting.Services.AccountingCaller(h.OrgA, 9, [], "x"), id))
            .Should().ThrowAsync<ForbiddenException>();

        // The list shape carries no canAct.
        (await h.Log.ListAsync(h.Viewer(), new PostingLogQuery())).Data.SelectMany(p => p.Targets).Should().OnlyContain(t => t.CanAct == null);
    }

    [Fact]
    public async Task Log_summary_counts_by_target_and_status()
    {
        using var h = new PostingHarness();
        await h.SetupGlAsync();
        await h.Gateway.PostAsync(Invoice(Guid.NewGuid()));
        h.External.Throw = new InvalidOperationException("down");
        await h.Gateway.PostAsync(Invoice(Guid.NewGuid()));

        var s = await h.Log.SummaryAsync(h.Viewer());

        s.Postings.Should().Be(2);
        s.NeedsAttention.Should().Be(1);
        s.Targets.Single(t => t.TargetCode == PostingTargetCodes.InternalGl).Posted.Should().Be(2);
        var qb = s.Targets.Single(t => t.TargetCode == PostingTargetCodes.QuickBooks);
        (qb.Queued, qb.Failed, qb.Total).Should().Be((1, 1, 2));
        s.LastPostingAt.Should().NotBeNull();
    }

    // ── settings ───────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Config_GET_creates_nothing_and_PUT_validates_and_saves()
    {
        using var h = new PostingHarness();
        var config = h.Get<ITenantFinanceConfigService>();

        var initial = await config.GetAsync(h.Viewer());
        initial.InternalAccountingEnabled.Should().BeFalse();
        initial.AutoPostToGL.Should().BeTrue();
        initial.FiscalYearStartMonth.Should().Be(1);
        initial.ModifiedAt.Should().BeNull();
        initial.NotReadyReason.Should().Be(GlReadiness.SetupIncomplete);
        (await h.Db.TenantFinanceConfigs.IgnoreQueryFilters().CountAsync()).Should().Be(0);

        await FluentActions.Invoking(() => config.UpdateAsync(h.Admin(), new UpdatePostingConfigRequest { FiscalYearStartMonth = 13 }))
            .Should().ThrowAsync<BadRequestException>().WithMessage(TenantFinanceConfigService.MonthMessage);
        await FluentActions.Invoking(() => config.UpdateAsync(h.Viewer(), new UpdatePostingConfigRequest { InternalAccountingEnabled = true }))
            .Should().ThrowAsync<ForbiddenException>();

        var saved = await config.UpdateAsync(h.Admin(), new UpdatePostingConfigRequest { InternalAccountingEnabled = true, FiscalYearStartMonth = 7 });
        saved.InternalAccountingEnabled.Should().BeTrue();
        saved.GlActive.Should().BeFalse("the GL setup is not Complete");
        saved.FiscalYearStartMonth.Should().Be(7);
        saved.ModifiedAt.Should().NotBeNull();

        await h.SetupGlAsync(enable: false);
        var ready = await config.GetAsync(h.Viewer());
        (ready.GlReady, ready.GlActive, ready.DefaultCurrencyCode).Should().Be((true, true, "PKR"));
    }

    // ── entity sync gateway ────────────────────────────────────────────────────────────────────────────────────────

    private sealed class FakeEntityTarget(string code, Func<EntitySyncRequest, EntitySyncResult> answer) : IEntitySyncTarget
    {
        public List<EntitySyncRequest> Calls { get; } = [];
        public string TargetCode => code;
        public bool Active { get; set; } = true;
        public Task<bool> IsActiveAsync(CancellationToken ct = default) => Task.FromResult(Active);
        public Task<EntitySyncResult> PushAsync(EntitySyncRequest request, CancellationToken ct = default)
        {
            Calls.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    private static EntitySyncRequest Customer(Guid id) => new()
    {
        EntityType = SyncEntityType.Customer, Action = SyncAction.Update, EntityId = id, IdempotencyKey = $"customer:{id}",
        Document = new CustomerPayload { ExternalId = id.ToString(), DisplayName = "Acme" },
    };

    [Fact]
    public async Task Entity_sync_routes_to_active_targets_and_never_throws()
    {
        var tenant = new ScopeTenant { Current = Guid.NewGuid() };
        (await new EntitySyncGateway(tenant, []).PushAsync(Customer(Guid.NewGuid()))).Outcome.Should().Be(EntitySyncOutcome.NoTargetConfigured);

        var ok = new FakeEntityTarget("quickbooks", _ => new EntitySyncResult { Success = true, Outcome = EntitySyncOutcome.Queued, TargetsQueued = 1 });
        var gw = new EntitySyncGateway(tenant, [ok]);
        var r = await gw.PushAsync(Customer(Guid.NewGuid()));
        (r.Success, r.Outcome, r.TargetsQueued).Should().Be((true, EntitySyncOutcome.Queued, 1));

        var bad = new FakeEntityTarget("other", _ => throw new InvalidOperationException("boom"));
        var both = await new EntitySyncGateway(tenant, [ok, bad]).PushAsync(Customer(Guid.NewGuid()));
        both.Success.Should().BeFalse();
        both.Outcome.Should().Be(EntitySyncOutcome.Failed, "an exception is Failed, not ValidationFailed (D-48)");
        both.TargetsQueued.Should().Be(1);
        both.ErrorMessage.Should().Contain("other: boom");

        (await gw.PushAsync(new EntitySyncRequest { EntityType = SyncEntityType.Vendor, EntityId = Guid.NewGuid() }))
            .Outcome.Should().Be(EntitySyncOutcome.ValidationFailed);

        var gate = new FakeGate();
        (await new EntitySyncGateway(tenant, [ok], gate).PushAsync(Customer(Guid.NewGuid()))).Outcome.Should().Be(EntitySyncOutcome.ModuleNotLicensed);
    }

    [Fact]
    public async Task Entity_sync_batch_counts_queued_duplicates_and_errors()
    {
        var tenant = new ScopeTenant { Current = Guid.NewGuid() };
        var dup = Guid.NewGuid();
        var target = new FakeEntityTarget("quickbooks", r => r.EntityId == dup
            ? new EntitySyncResult { Success = true, Outcome = EntitySyncOutcome.DuplicateSkipped }
            : new EntitySyncResult { Success = true, Outcome = EntitySyncOutcome.Queued, TargetsQueued = 1 });
        var gw = new EntitySyncGateway(tenant, [target]);

        var batch = await gw.PushBatchAsync([Customer(Guid.NewGuid()), Customer(dup), new EntitySyncRequest { EntityId = Guid.NewGuid() }]);

        (batch.TotalRequests, batch.Queued, batch.DuplicatesSkipped, batch.Errors.Count).Should().Be((3, 1, 1, 1));
    }

    // ── account map reader ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task D16_account_map_reader_upserts_and_reads_by_code_and_default()
    {
        using var h = new PostingHarness();
        await h.SetupGlAsync();
        var reader = h.Get<IExternalAccountMapReader>();
        var leaves = await reader.ListLeafAccountsAsync();
        leaves.Should().HaveCount(Leaves.Length);
        var revenue = leaves.Single(a => a.Code == "4111");
        var cash = leaves.Single(a => a.Code == "1111");

        (await reader.UpsertMappingsAsync("quickbooks_online", [
            new AccountMappingUpsert(revenue.AccountUuid, "79", "Sales", "Auto-mapped by name"),
            new AccountMappingUpsert(cash.AccountUuid, "35", "Cash on hand", null)])).Should().Be(2);
        (await reader.UpsertMappingsAsync(SMS.Shared.Finance.Posting.ExternalSystemTypes.QuickBooksOnline,
            [new AccountMappingUpsert(revenue.AccountUuid, "79", "Sales", "Auto-mapped by name")])).Should().Be(0, "unchanged");

        var map = await reader.GetAsync(SMS.Shared.Finance.Posting.ExternalSystemTypes.QuickBooksOnline);
        map.ByAccountCode["4111"].ExternalAccountCode.Should().Be("79");
        map.ByDefaultKey[DefaultKeys.Revenue].Should().Be("79");
        map.ByDefaultKey[DefaultKeys.Cash].Should().Be("35");
        map.ByDefaultKey.Should().NotContainKey(DefaultKeys.Receivable);

        (await reader.UpsertMappingsAsync("QUICKBOOKS_ONLINE", [new AccountMappingUpsert(cash.AccountUuid, null, null, null)])).Should().Be(1);
        (await reader.GetAsync("QUICKBOOKS_ONLINE")).ByAccountCode.Should().NotContainKey("1111");

        var group = await h.Db.ChartOfAccounts.IgnoreQueryFilters().FirstAsync(a => a.OrganizationId == h.OrgA && a.IsGroup);
        await FluentActions.Invoking(() => reader.UpsertMappingsAsync("QUICKBOOKS_ONLINE", [new AccountMappingUpsert(group.Uuid, "1", null, null)]))
            .Should().ThrowAsync<BadRequestException>();

        h.Tenant.Current = h.OrgB;
        (await reader.GetAsync("QUICKBOOKS_ONLINE")).ByAccountCode.Should().BeEmpty();
    }
}
