using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.ArAp;
using SMS.Modules.Finance.ArAp.Receivables;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Integration.Posting;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Finance.Tests;

/// <summary>A48 AR — the dunning run over real invoices (TS-DN-006..011, BR-DN-005..010, D-10 charge).</summary>
public class A48DunningServiceTests
{
    /// <summary>An invoice issued at the clock's start (due 30 days later), then the clock moved <paramref name="daysOverdue"/> past due.</summary>
    private static async Task<(A48ArWorld W, Guid Customer)> OverdueAsync(int daysOverdue = 10, decimal amount = 1_000m, string? email = "ar@acme.test")
    {
        var w = new A48ArWorld();
        var c = w.Customer("Acme Ltd", email: email);
        await w.Desk.InvoiceAsync(c, amount);
        w.Clock.Value = TestClock.Start.Date.AddDays(30 + daysOverdue).AddHours(9);
        return (w, c);
    }

    private static DunningProcedureModel Procedure(string code, bool isDefault = true, decimal? charge = null, int? blockAt = null, int grace = 0) => new()
    {
        Code = code, Name = code, IsDefault = isDefault, IsActive = true, GraceDays = grace, BlockSalesAtLevel = blockAt,
        Levels =
        [
            new() { LevelNumber = 1, DaysAfterPrevious = 0, SubjectTemplate = "Reminder {DunningLevel}", BodyTemplate = "Dear {CustomerName}, {TotalOverdue}.\n{InvoiceList}", ChargeAmount = charge },
            new() { LevelNumber = 2, DaysAfterPrevious = 14, SubjectTemplate = "Second", BodyTemplate = "Pay {Amount}" }
        ]
    };

    [Fact]
    public async Task TS_DN_006_execute_sends_the_notice_and_moves_the_level()
    {
        var (w, c) = await OverdueAsync();

        var preview = await w.InScopeAsync(s => s.Dunning.PreviewAsync(new DunningPreviewRequest(), w.Caller));
        preview.Status.Should().Be("Draft");
        var item = preview.Items.Single();
        item.Should().Match<DunningRunItemModel>(i => i.CustomerUuid == c && i.DunningLevel == 1 && i.PreviousLevel == 0
            && i.OverdueAmount == 1_000m && i.DaysOverdue == 10 && i.Status == "Pending");
        item.Subject.Should().Contain("1,000.00");              // the seeded STANDARD level 1, rendered at preview
        w.Mail.Should().BeEmpty("a preview sends nothing");

        var done = await w.InScopeAsync(s => s.Dunning.ExecuteAsync(preview.Uuid, w.Caller));
        done.Status.Should().Be("Completed");
        done.ItemsSent.Should().Be(1);
        done.Items.Single().Status.Should().Be("Sent");
        w.Mail.Should().ContainSingle(m => m.To == "ar@acme.test" && m.Body.Contains("Acme Ltd") && m.Body.Contains("<table"));

        var profile = await w.InScopeAsync(s => s.Db.PartnerFinancialProfiles.SingleAsync());
        profile.CurrentDunningLevel.Should().Be(1);
        profile.LastDunningDate.Should().Be(DateOnly.FromDateTime(w.Clock.Value));
    }

    [Fact]
    public async Task TS_DN_011_a_second_run_the_same_day_is_refused_and_a_cancelled_one_frees_the_day()
    {
        var (w, _) = await OverdueAsync();
        var first = await w.InScopeAsync(s => s.Dunning.PreviewAsync(new DunningPreviewRequest(), w.Caller));

        await FluentActions.Awaiting(() => w.InScopeAsync(s => s.Dunning.PreviewAsync(new DunningPreviewRequest(), w.Caller)))
            .Should().ThrowAsync<ConflictException>().WithMessage($"A dunning run already exists for {first.RunDate:yyyy-MM-dd} ({first.RunNumber}).");

        await w.InScopeAsync(s => s.Dunning.CancelRunAsync(first.Uuid, "wrong date", w.Caller));
        (await w.InScopeAsync(s => s.Dunning.PreviewAsync(new DunningPreviewRequest(), w.Caller))).Status.Should().Be("Draft");
    }

    [Fact]
    public async Task BR_DN_008_a_completed_run_is_immutable()
    {
        var (w, _) = await OverdueAsync();
        var run = await w.InScopeAsync(s => s.Dunning.PreviewAsync(new DunningPreviewRequest(), w.Caller));
        await w.InScopeAsync(s => s.Dunning.ExecuteAsync(run.Uuid, w.Caller));

        var message = $"Dunning run {run.RunNumber} is Completed; it can no longer change.";
        await FluentActions.Awaiting(() => w.InScopeAsync(s => s.Dunning.ExecuteAsync(run.Uuid, w.Caller)))
            .Should().ThrowAsync<ConflictException>().WithMessage(message);
        await FluentActions.Awaiting(() => w.InScopeAsync(s => s.Dunning.SkipItemAsync(run.Uuid, run.Items[0].Uuid, "x", w.Caller)))
            .Should().ThrowAsync<ConflictException>().WithMessage(message);
        await FluentActions.Awaiting(() => w.InScopeAsync(s => s.Dunning.CancelRunAsync(run.Uuid, "x", w.Caller)))
            .Should().ThrowAsync<ConflictException>().WithMessage(message);
        w.Mail.Should().HaveCount(1);
    }

    [Fact]
    public async Task TS_DN_007_the_charge_is_a_balanced_customer_document_on_the_ledger()
    {
        var (w, c) = await OverdueAsync();
        await w.InScopeAsync(s => s.Dunning.CreateProcedureAsync(Procedure("FEES", charge: 25m), w.Caller));

        var run = await w.InScopeAsync(s => s.Dunning.PreviewAsync(new DunningPreviewRequest(), w.Caller));
        run.Items.Single().ChargeAmount.Should().Be(25m);
        var done = await w.InScopeAsync(s => s.Dunning.ExecuteAsync(run.Uuid, w.Caller));
        var item = done.Items.Single();
        item.ChargeDocumentNumber.Should().StartWith("SDC-");

        var books = await w.Desk.BooksAsync(c);
        var charge = books.Invoice(item.ChargeDocumentUuid!.Value);
        charge.Should().Match<SalesInvoice>(i => i.SourceType == "DUNNING_CHARGE" && i.Status == "ISSUED"
            && i.GrandTotal == 25m && i.BalanceDue == 25m && i.SourceUuid == item.Uuid);
        books.Ledger.Should().Contain(e => e.ReferenceId == charge.UUID && e.EntryType == "INVOICE" && e.DebitAmount == 25m);
        (await w.Desk.OwesAsync(c)).Should().Be(1_025m);

        await using var s2 = w.Scope();
        var withLines = await s2.Db.SalesInvoices.Include(i => i.Lines).SingleAsync(i => i.UUID == charge.UUID);
        var gl = SalesPostings.Invoice(withLines).Lines;
        gl.Sum(l => l.Debit).Should().Be(gl.Sum(l => l.Credit)).And.Be(25m);
        gl.Should().Contain(l => l.AccountRole == "DEFAULT_RECEIVABLE" && l.Debit == 25m)
          .And.Contain(l => l.AccountRole == "DEFAULT_REVENUE" && l.Credit == 25m);
    }

    [Fact]
    public async Task TS_DN_008_reaching_the_block_level_puts_an_opted_in_customer_on_hold()
    {
        var (w, c) = await OverdueAsync();
        await w.InScopeAsync(s => s.Dunning.CreateProcedureAsync(Procedure("BLOCK1", blockAt: 1), w.Caller));
        await w.InScopeAsync(async s =>
        {
            var p = await s.Profiles.GetOrCreateAsync(w.Org, c, "Customer");
            p.BlockSalesAtDunning = true;
            await s.Db.SaveChangesAsync();
            return 0;
        });

        var run = await w.InScopeAsync(s => s.Dunning.PreviewAsync(new DunningPreviewRequest(), w.Caller));
        await w.InScopeAsync(s => s.Dunning.ExecuteAsync(run.Uuid, w.Caller));

        var profile = await w.InScopeAsync(s => s.Db.PartnerFinancialProfiles.SingleAsync());
        profile.CreditHold.Should().BeTrue();
        profile.CreditHoldReason.Should().Be("Dunning Level 1");
        (await w.InScopeAsync(s => s.Db.CreditHoldRequests.SingleAsync())).Action.Should().Be("DunningHold");
    }

    [Fact]
    public async Task Without_the_profile_opt_in_the_block_level_does_not_hold()
    {
        var (w, _) = await OverdueAsync();
        await w.InScopeAsync(s => s.Dunning.CreateProcedureAsync(Procedure("BLOCK1", blockAt: 1), w.Caller));
        var run = await w.InScopeAsync(s => s.Dunning.PreviewAsync(new DunningPreviewRequest(), w.Caller));
        await w.InScopeAsync(s => s.Dunning.ExecuteAsync(run.Uuid, w.Caller));
        (await w.InScopeAsync(s => s.Db.PartnerFinancialProfiles.SingleAsync())).CreditHold.Should().BeFalse();
    }

    [Fact]
    public async Task TS_DN_009_a_customer_who_pays_everything_is_reset_to_level_0()
    {
        var (w, c) = await OverdueAsync();
        var run = await w.InScopeAsync(s => s.Dunning.PreviewAsync(new DunningPreviewRequest(), w.Caller));
        await w.InScopeAsync(s => s.Dunning.ExecuteAsync(run.Uuid, w.Caller));

        await w.Desk.PayAsync(c, 1_000m);

        // D-AR-13 (QA-3) — reads show the effective level 0 at once; the stored level is reset by the next preview.
        (await w.InScopeAsync(s => s.Profiles.GetModelAsync(w.Caller, c, "Customer"))).CurrentDunningLevel.Should().Be(0);
        (await w.InScopeAsync(s => s.Profiles.ListAsync(w.Caller, new PartnerProfileListQuery { Role = "Customer", MinDunningLevel = 1 })))
            .Data.Should().BeEmpty();
        (await w.InScopeAsync(s => s.Dunning.CustomerHistoryAsync(c, w.Caller))).CurrentDunningLevel.Should().Be(0);
        (await w.InScopeAsync(s => s.Db.PartnerFinancialProfiles.SingleAsync())).CurrentDunningLevel.Should().Be(1, "a read never writes");

        w.Clock.Value = w.Clock.Value.AddDays(1);
        var next = await w.InScopeAsync(s => s.Dunning.PreviewAsync(new DunningPreviewRequest(), w.Caller));
        next.AutoResetCount.Should().Be(1);
        next.Items.Should().BeEmpty();

        var history = await w.InScopeAsync(s => s.Dunning.CustomerHistoryAsync(c, w.Caller));
        history.CurrentDunningLevel.Should().Be(0);
        history.Events.Should().Contain(e => e.Kind == "Reset" && e.Detail == "Balance cleared");
    }

    [Fact]
    public async Task TS_DN_010_manual_reset_by_admin_is_audited()
    {
        var (w, c) = await OverdueAsync();
        var run = await w.InScopeAsync(s => s.Dunning.PreviewAsync(new DunningPreviewRequest(), w.Caller));
        await w.InScopeAsync(s => s.Dunning.ExecuteAsync(run.Uuid, w.Caller));

        await FluentActions.Awaiting(() => w.InScopeAsync(s => s.Dunning.ResetAsync(c, "x", 3, w.Caller)))
            .Should().ThrowAsync<BadRequestException>();
        var h = await w.InScopeAsync(s => s.Dunning.ResetAsync(c, "Agreed payment plan", null, w.Caller));
        h.CurrentDunningLevel.Should().Be(0);
        h.Events.Select(e => e.Kind).Should().Contain(["Reset", "Notice"]);
        (await w.InScopeAsync(s => s.Db.CreditHoldRequests.SingleAsync())).Should()
            .Match<CreditHoldRequest>(r => r.Action == "DunningReset" && r.PreviousValue == "1" && r.NewValue == "0" && r.RequestedByName == "Tester");
    }

    [Fact]
    public async Task Level_2_follows_only_after_its_days_since_the_last_notice()
    {
        var (w, _) = await OverdueAsync();
        var run = await w.InScopeAsync(s => s.Dunning.PreviewAsync(new DunningPreviewRequest(), w.Caller));
        await w.InScopeAsync(s => s.Dunning.ExecuteAsync(run.Uuid, w.Caller));

        w.Clock.Value = w.Clock.Value.AddDays(5);
        (await w.InScopeAsync(s => s.Dunning.PreviewAsync(new DunningPreviewRequest(), w.Caller))).Items.Should().BeEmpty();

        w.Clock.Value = w.Clock.Value.AddDays(10);   // 15 days since level 1; STANDARD level 2 is at 14
        var second = await w.InScopeAsync(s => s.Dunning.PreviewAsync(new DunningPreviewRequest(), w.Caller));
        second.Items.Single().Should().Match<DunningRunItemModel>(i => i.DunningLevel == 2 && i.PreviousLevel == 1);
    }

    [Fact]
    public async Task TS_DN_005_below_minimum_is_listed_as_skipped_and_never_sent()
    {
        var (w, _) = await OverdueAsync(amount: 30m);
        var p = Procedure("MIN50");
        p.MinimumAmount = 50m;
        await w.InScopeAsync(s => s.Dunning.CreateProcedureAsync(p, w.Caller));

        var run = await w.InScopeAsync(s => s.Dunning.PreviewAsync(new DunningPreviewRequest(), w.Caller));
        run.Items.Single().Should().Match<DunningRunItemModel>(i => i.Status == "Skipped" && i.SkipReason!.Contains("minimum"));
        await w.InScopeAsync(s => s.Dunning.ExecuteAsync(run.Uuid, w.Caller));
        w.Mail.Should().BeEmpty();
    }

    [Fact]
    public async Task A_customer_without_email_fails_alone_and_the_run_completes()
    {
        var (w, c) = await OverdueAsync(email: null);
        var other = w.Customer("Beta", email: "beta@x.test");
        var clock = w.Clock.Value;
        w.Clock.Value = TestClock.Start;
        await w.Desk.InvoiceAsync(other, 200m);
        w.Clock.Value = clock;

        var run = await w.InScopeAsync(s => s.Dunning.PreviewAsync(new DunningPreviewRequest(), w.Caller));
        var done = await w.InScopeAsync(s => s.Dunning.ExecuteAsync(run.Uuid, w.Caller));

        done.Status.Should().Be("Completed");
        done.ItemsFailed.Should().Be(1);
        done.ItemsSent.Should().Be(1);
        done.Items.Single(i => i.CustomerUuid == c).FailureReason.Should().Contain("no email address");
        (await w.InScopeAsync(s => s.Db.PartnerFinancialProfiles.SingleAsync(p => p.PartnerUuid == c))).CurrentDunningLevel.Should().Be(0);
    }

    [Fact]
    public async Task Procedures_must_number_levels_without_gaps_and_keep_one_default()
    {
        var w = new A48ArWorld();
        var p = Procedure("GAP");
        p.Levels[1].LevelNumber = 3;
        await FluentActions.Awaiting(() => w.InScopeAsync(s => s.Dunning.CreateProcedureAsync(p, w.Caller)))
            .Should().ThrowAsync<BadRequestException>().WithMessage("Dunning levels must be numbered 1 to 2 without gaps.");

        var created = await w.InScopeAsync(s => s.Dunning.CreateProcedureAsync(Procedure("NEWDEF"), w.Caller));
        var all = await w.InScopeAsync(s => s.Dunning.ListProceduresAsync(w.Caller));
        all.Count(x => x.IsDefault).Should().Be(1);
        all.Single(x => x.IsDefault).Uuid.Should().Be(created.Uuid);
        all.Should().Contain(x => x.Code == "STANDARD" && !x.IsDefault);

        await FluentActions.Awaiting(() => w.InScopeAsync(s => s.Dunning.CreateProcedureAsync(Procedure("NEWDEF"), w.Caller)))
            .Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task Plain_text_templates_become_html_with_line_breaks()
    {
        var model = new DunningTemplateModel("A & B", null, 1, 10m, "PKR", null, []);
        new DunningTemplateEngine().RenderBody("Dear {CustomerName},\nPay in < 5 days", model)
            .Should().Be("Dear A &amp; B,<br>Pay in &lt; 5 days");
    }

    // ── REV round ──────────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task REV1_REV2_a_failed_send_is_Failed_with_the_reason_and_charges_nothing_however_often_it_is_retried()
    {
        var (w, c) = await OverdueAsync();
        await w.InScopeAsync(s => s.Dunning.CreateProcedureAsync(Procedure("FEES", charge: 25m), w.Caller));
        w.MailFailure = "SMTP 550 mailbox unavailable";

        for (var day = 0; day < 2; day++)
        {
            var run = await w.InScopeAsync(s => s.Dunning.PreviewAsync(new DunningPreviewRequest(), w.Caller));
            var done = await w.InScopeAsync(s => s.Dunning.ExecuteAsync(run.Uuid, w.Caller));
            done.Items.Single().Should().Match<DunningRunItemModel>(i => i.Status == "Failed" && i.ChargeDocumentUuid == null
                && i.FailureReason!.Contains("SMTP 550 mailbox unavailable"));
            w.Clock.Value = w.Clock.Value.AddDays(1);
        }

        (await w.Desk.BooksAsync(c)).Invoices.Should().NotContain(i => i.SourceType == "DUNNING_CHARGE");
        (await w.InScopeAsync(s => s.Db.PartnerFinancialProfiles.SingleAsync())).CurrentDunningLevel.Should().Be(0);

        w.MailFailure = null;
        var ok = await w.InScopeAsync(s => s.Dunning.PreviewAsync(new DunningPreviewRequest(), w.Caller));
        var sent = await w.InScopeAsync(s => s.Dunning.ExecuteAsync(ok.Uuid, w.Caller));
        sent.Items.Single().Status.Should().Be("Sent");
        (await w.Desk.BooksAsync(c)).Invoices.Count(i => i.SourceType == "DUNNING_CHARGE").Should().Be(1, "charged once, for the notice that went");
    }

    [Fact]
    public async Task REV10_a_run_stuck_in_Processing_can_be_resumed_or_cancelled_but_not_a_live_one()
    {
        var (w, _) = await OverdueAsync();
        var run = await w.InScopeAsync(s => s.Dunning.PreviewAsync(new DunningPreviewRequest(), w.Caller));

        async Task StickAsync(TimeSpan ago)
        {
            await using var s = w.Scope();
            var r = await s.Db.DunningRuns.SingleAsync(x => x.Uuid == run.Uuid);
            r.Status = "Processing";
            r.ExecutedAt = w.Clock.Value - ago;
            await s.Db.SaveChangesAsync();
        }

        await StickAsync(TimeSpan.FromMinutes(2));
        await FluentActions.Awaiting(() => w.InScopeAsync(s => s.Dunning.ExecuteAsync(run.Uuid, w.Caller))).Should().ThrowAsync<ConflictException>();
        await FluentActions.Awaiting(() => w.InScopeAsync(s => s.Dunning.CancelRunAsync(run.Uuid, "x", w.Caller))).Should().ThrowAsync<ConflictException>();

        await StickAsync(DunningService.StuckAfter + TimeSpan.FromMinutes(1));
        var resumed = await w.InScopeAsync(s => s.Dunning.ExecuteAsync(run.Uuid, w.Caller));
        resumed.Status.Should().Be("Completed");
        resumed.ItemsSent.Should().Be(1);

        // A stuck run can also be cancelled: its pending items are skipped and the day is free again.
        w.Clock.Value = w.Clock.Value.AddDays(20);
        var next = await w.InScopeAsync(s => s.Dunning.PreviewAsync(new DunningPreviewRequest(), w.Caller));
        run = next;
        await StickAsync(DunningService.StuckAfter + TimeSpan.FromMinutes(1));
        var cancelled = await w.InScopeAsync(s => s.Dunning.CancelRunAsync(next.Uuid, "request died", w.Caller));
        cancelled.Status.Should().Be("Cancelled");
        cancelled.Items.Should().OnlyContain(i => i.Status == "Skipped");
        (await w.InScopeAsync(s => s.Dunning.PreviewAsync(new DunningPreviewRequest(), w.Caller))).Status.Should().Be("Draft");
    }

    [Fact]
    public async Task REV15_the_fallback_run_number_stays_unique_after_a_cancelled_run()
    {
        var (w, _) = await OverdueAsync();
        var first = await w.InScopeAsync(s => s.Dunning.PreviewAsync(new DunningPreviewRequest(), w.Caller));
        await w.InScopeAsync(s => s.Dunning.CancelRunAsync(first.Uuid, null, w.Caller));
        var second = await w.InScopeAsync(s => s.Dunning.PreviewAsync(new DunningPreviewRequest(), w.Caller));

        first.RunNumber.Should().Be($"DUN-{w.Clock.Value:yyyyMMdd}");
        second.RunNumber.Should().Be($"DUN-{w.Clock.Value:yyyyMMdd}-2");
    }
}
