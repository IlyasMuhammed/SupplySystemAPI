using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.GeneralLedger;
using SMS.Integration.Tests.ModuleRegistry;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using Xunit;
using static SMS.Integration.Tests.FinancialPosting.Fp;

namespace SMS.Integration.Tests.FinancialPosting;

/// <summary>
/// A44 (QA) — CORE's HTTP surface <c>api/finance/posting/*</c> exactly as API-CONTRACT "Details › CORE" (D-40, D-41, D-47, D-49):
/// <list type="bullet">
/// <item>config GET/PUT, readiness, validation — <see cref="Config_reports_readiness_validates_and_GET_creates_nothing"/>.</item>
/// <item>D-40/D-41 QuickBooks-only org, INTEGRATION_VIEW / _MANAGE — <see cref="A_QuickBooks_only_organization_opens_the_log_with_integration_permissions"/>.</item>
/// <item>D-47 PERIOD_CLOSED → retry job → Posted, DeadLetter after the 6th failure, manual retry/skip, summary, filters —
/// <see cref="Period_closed_is_retried_by_the_job_and_dead_letters_after_the_sixth_failure"/>.</item>
/// </list>
/// <para>Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~PostingApiE2ETests</c>.</para>
/// </summary>
public sealed class PostingApiE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;

    public PostingApiE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _f.ShouldBeOnLocalDb();
        _root = new SapKit(factory, "A44");
    }

    private async Task<int> ConfigRowsAsync(Guid org) =>
        Convert.ToInt32((await _f.QueryAsync("SELECT COUNT(*) AS N FROM finance.TenantFinanceConfigs WHERE OrganizationId = @o", ("@o", org))).Single()["N"]);

    /// <summary>The GL target row of a posting: (status, attempts, nextAttemptAt, lastError).</summary>
    private async Task<(string Status, int Attempts, DateTime? Next, string? Error)> GlTargetAsync(Guid org, Guid source)
    {
        var r = (await _f.QueryAsync(
            "SELECT t.Status, t.Attempts, t.NextAttemptAt, t.LastError FROM finance.FinancialPostingTargets t " +
            "JOIN finance.FinancialPostings p ON p.Id = t.PostingId WHERE p.OrganizationId = @o AND p.SourceDocumentUuid = @s " +
            "AND p.Kind = 'Post' AND t.TargetCode = 'internal_gl'", ("@o", org), ("@s", source))).Should().ContainSingle().Subject;
        return ((string)r["Status"]!, Convert.ToInt32(r["Attempts"]), r["NextAttemptAt"] as DateTime?, r["LastError"] as string);
    }

    /// <summary>Makes the GL target's next attempt due now (the backoff "passed").</summary>
    private Task DueNowAsync(Guid org, Guid source) => _f.ExecuteAsync(
        "UPDATE t SET NextAttemptAt = DATEADD(SECOND, -5, SYSUTCDATETIME()) FROM finance.FinancialPostingTargets t " +
        "JOIN finance.FinancialPostings p ON p.Id = t.PostingId WHERE p.OrganizationId = @o AND p.SourceDocumentUuid = @s " +
        "AND t.TargetCode = 'internal_gl' AND t.NextAttemptAt IS NOT NULL", ("@o", org), ("@s", source));

    [Fact]
    public async Task Config_reports_readiness_validates_and_GET_creates_nothing()
    {
        var o = await _root.NewOrgAsync("API1");
        var k = o.K;

        var fresh = await k.Ok(k.GetConfig(), "GET config");
        (fresh.B("internalAccountingEnabled"), fresh.B("autoPostToGL"), fresh.I("fiscalYearStartMonth"), fresh.B("glFeatureEnabled"),
                fresh.B("glReady"), fresh.B("glActive"), fresh.S("notReadyReason"), fresh.S("defaultCurrencyCode"))
            .Should().Be((false, true, 1, true, false, false, "SETUP_INCOMPLETE", "PKR"), J.Short(fresh));
        fresh.IsNull("modifiedAt").Should().BeTrue("defaults shown, no row yet");
        (await ConfigRowsAsync(o.OrgId)).Should().Be(0, "GET never creates a row");

        var bad = await k.Put($"{Posting}/config", new { fiscalYearStartMonth = 13 });
        bad.Status.Should().Be(HttpStatusCode.BadRequest, bad.ToString());
        bad.Message.Should().Be("The fiscal year start month must be between 1 and 12.");

        var early = await k.Put($"{Posting}/config", new { internalAccountingEnabled = true });
        early.Status.Should().Be(HttpStatusCode.OK, early.ToString());
        early.Message.Should().Be("Posting settings saved.");
        (early.Result.B("internalAccountingEnabled"), early.Result.B("glActive"), early.Result.S("notReadyReason"), early.Result.B("autoPostToGL"))
            .Should().Be((true, false, "SETUP_INCOMPLETE", true), "saved while the setup is incomplete; omitted fields kept");
        early.Result.IsNull("modifiedAt").Should().BeFalse();
        (await ConfigRowsAsync(o.OrgId)).Should().Be(1);

        await k.CompleteGlSetupAsync();
        var ready = await k.Ok(k.GetConfig(), "after setup");
        (ready.S("setupStatus"), ready.B("glReady"), ready.B("glActive"), ready.S("notReadyReason")).Should().Be(("Complete", true, true, null));

        // licence off: FEATURE_OFF (the posting API stays open through MODULE_INTEGRATION, D-40)
        await _root.LicenceAsync(o.OrgId, (Gl.Feature, false));
        await _f.InvalidateAsync(o.OrgId);
        var off = await k.Ok(k.GetConfig(), "feature off");
        (off.B("glFeatureEnabled"), off.B("glActive"), off.S("notReadyReason")).Should().Be((false, false, "FEATURE_OFF"));
    }

    [Fact]
    public async Task A_QuickBooks_only_organization_opens_the_log_with_integration_permissions()
    {
        var o = await _root.NewOrgAsync("API2");
        await _root.LicenceAsync(o.OrgId, (Gl.Feature, false));
        await _f.InvalidateAsync(o.OrgId);
        o = await o.ConnectLiveAsync();
        var k = o.K;
        var (_, _, customer, item) = await k.StockedWorldAsync("API2");
        var inv = await k.IssuedInvoiceAsync(customer, item, 1m, o.O.Pkr);

        var view = await k.LoginWithPermissionsAsync("intview", "INTEGRATION_VIEW");
        var manage = await k.LoginWithPermissionsAsync("intmanage", "INTEGRATION_MANAGE");

        // D-40/D-41: INTEGRATION_VIEW opens log, detail and summary of a QuickBooks-only org
        var page = await k.Ok(k.Get($"{Posting}/log?page=1&pageSize=25", view), "log as INTEGRATION_VIEW");
        var row = page.A("data").Single(r => r.G("sourceDocumentUuid") == inv.G("uuid"));
        (row.S("kind"), row.S("documentType"), row.S("sourceModule"), row.S("sourceDocumentNumber"), row.I("version"))
            .Should().Be(("Post", "SalesInvoice", "finance", inv.S("invoiceNumber"), 1), J.Short(row));
        row.S("idempotencyKey").Should().Be($"finance:SalesInvoice:{inv.G("uuid")}");
        row.D("totalAmount").Should().Be(inv.D("grandTotal"));
        row.TargetStatus("quickbooks").Should().BeOneOf(new[] { "Queued", "Posted" });
        row.TargetStatus("internal_gl").Should().BeNull("the GL is not licensed");
        foreach (var name in new[] { "totalRecords", "page", "pageSize", "totalPages", "hasNext", "hasPrevious" }) page.Has(name).Should().BeTrue(name);

        var detail = await k.Ok(k.Get($"{Posting}/log/{row.G("uuid")}", view), "detail as INTEGRATION_VIEW");
        (detail.S("documentPayloadType"), detail.S("partnerType")).Should().Be(("SalesInvoicePayload", "Customer"));
        detail.A("lines").Should().NotBeEmpty();
        detail.A("journals").Should().BeEmpty();
        var summary = await k.Ok(k.Get($"{Posting}/summary", view), "summary as INTEGRATION_VIEW");
        summary.A("targets").Should().Contain(t => t.S("targetCode") == "quickbooks");
        summary.I("postings").Should().BeGreaterThanOrEqualTo(1);

        // config is GL-only; retry/skip need INTEGRATION_MANAGE
        (await k.GetConfig(view)).ShouldBeForbidden("config GET is GL_VIEW|GL_ACCOUNT_MANAGE|GL_SETUP_MANAGE");
        (await k.RetryPosting(row, c: view)).ShouldBeForbidden("INTEGRATION_VIEW cannot retry");
        (await k.SkipPosting(row, c: view)).ShouldBeForbidden("INTEGRATION_VIEW cannot skip");
        var nothing = await k.RetryPosting(row, c: manage);
        if (row.TargetStatus("quickbooks") is "Queued" or "Posted")
        {
            nothing.Status.Should().Be(HttpStatusCode.Conflict, nothing.ToString());
            nothing.Message.Should().Be("Nothing to retry: no target of this posting is failed.");
            var noSkip = await k.SkipPosting(row, c: manage);
            noSkip.Status.Should().Be(HttpStatusCode.Conflict);
            noSkip.Message.Should().Be("Nothing to skip: no target of this posting is failed or pending.");
        }

        // neither GL nor Integration licensed: the posting API is closed
        var none = await _root.NewOrgAsync("API2N");
        await _root.LicenceAsync(none.OrgId, (Gl.Feature, false), ("MODULE_INTEGRATION", false));
        await _f.InvalidateAsync(none.OrgId);
        // A37 D-6: a licence removed from an org that had it is read-only — reads pass, writes are refused
        (await none.K.Get($"{Posting}/log")).Status.Should().Be(HttpStatusCode.OK, "read-only after the licence is removed (A37 D-6)");
        (await none.K.PutConfig(true)).ShouldBeForbidden("D-40: no target module licensed — writes refused");
    }

    [Fact]
    public async Task Period_closed_is_retried_by_the_job_and_dead_letters_after_the_sixth_failure()
    {
        var o = await _root.NewOrgAsync("API3");
        var k = o.K;
        await k.EnableGlAsync();
        var (_, _, customer, item) = await k.StockedWorldAsync("API3");
        var period = await k.PeriodOfAsync(Today);
        await k.Ok(k.SetPeriodStatus(period, "Locked"), "lock this month");

        // ── PERIOD_CLOSED → the retry job posts it once the period is open and the attempt is due ──
        var a = await k.IssuedInvoiceAsync(customer, item, 1m, o.O.Pkr);
        var first = await GlTargetAsync(o.OrgId, a.G("uuid"));
        (first.Status, first.Attempts).Should().Be(("Failed", 1), first.ToString());
        first.Error.Should().StartWith("PERIOD_CLOSED");
        first.Next.Should().NotBeNull().And.BeCloseTo(DateTime.UtcNow.AddSeconds(30), TimeSpan.FromSeconds(20), "first backoff 30 s");

        (await _f.RunGlRetryJobAsync()).Should().BeTrue("PostingRetryJob exists");
        (await GlTargetAsync(o.OrgId, a.G("uuid"))).Attempts.Should().Be(1, "not due yet: the job leaves it");

        // ── B stays closed: DeadLetter after the 6th failure (initial + 5 retries), then never retried automatically ──
        var b = await k.IssuedInvoiceAsync(customer, item, 2m, o.O.Pkr);
        var expectedBackoff = new[] { 120, 480, 1920, 7200 };
        for (var attempt = 2; attempt <= 6; attempt++)
        {
            await DueNowAsync(o.OrgId, b.G("uuid"));
            await _f.RunGlRetryJobAsync();
            var t = await GlTargetAsync(o.OrgId, b.G("uuid"));
            t.Attempts.Should().Be(attempt, t.ToString());
            if (attempt < 6)
            {
                t.Status.Should().Be("Failed");
                t.Next.Should().NotBeNull().And.BeCloseTo(DateTime.UtcNow.AddSeconds(expectedBackoff[attempt - 2]), TimeSpan.FromSeconds(30),
                    $"backoff after failure {attempt}");
            }
            else
            {
                (t.Status, t.Next).Should().Be(("DeadLetter", (DateTime?)null), "the 6th failure dead-letters");
            }
        }
        await _f.RunGlRetryJobAsync();
        (await GlTargetAsync(o.OrgId, b.G("uuid"))).Should().Match<(string Status, int Attempts, DateTime? Next, string? Error)>(
            t => t.Status == "DeadLetter" && t.Attempts == 6);

        var summary = await k.Ok(k.Get($"{Posting}/summary"), "summary");
        var gl = summary.A("targets").Single(t => t.S("targetCode") == "internal_gl");
        (gl.I("deadLetter"), gl.I("failed")).Should().Be((1, 1), J.Short(summary));
        summary.I("needsAttention").Should().Be(2);
        var dead = await k.Ok(k.Get($"{Posting}/log?status=DeadLetter&targetCode=internal_gl"), "log filtered DeadLetter");
        dead.A("data").Select(r => r.G("sourceDocumentUuid")).Should().Equal(b.G("uuid"));
        dead.A("data").Single().S("overallStatus").Should().Be("DeadLetter");

        // ── reopen: A goes by the job; B (dead letter) only by a manual retry ──
        await k.Ok(k.SetPeriodStatus(period, "Open"), "reopen");
        await DueNowAsync(o.OrgId, a.G("uuid"));
        await _f.RunGlRetryJobAsync();
        (await GlTargetAsync(o.OrgId, a.G("uuid"))).Status.Should().Be("Posted", "the job posted it");
        (await _f.JournalsAsync(o.OrgId, a.G("uuid"))).Should().ContainSingle();
        (await GlTargetAsync(o.OrgId, b.G("uuid"))).Status.Should().Be("DeadLetter", "a dead letter is not retried automatically");

        var bRow = (await k.PostingRowsAsync(b.G("uuid"))).Single();
        var retried = await k.RetryPosting(bRow, "internal_gl");
        retried.Status.Should().Be(HttpStatusCode.OK, retried.ToString());
        retried.Message.Should().Be("Retried.");
        retried.Result.TargetStatus("internal_gl").Should().Be("Posted");
        retried.Result.A("journals").Should().ContainSingle();
        (await GlTargetAsync(o.OrgId, b.G("uuid"))).Attempts.Should().Be(0, "a manual retry resets the attempts");

        // ── skip with a reason; filters ──
        await k.Ok(k.SetPeriodStatus(period, "Locked"), "lock again");
        var c = await k.IssuedInvoiceAsync(customer, item, 3m, o.O.Pkr);
        var cRow = (await k.PostingRowsAsync(c.G("uuid"))).Single();
        var skipped = await k.SkipPosting(cRow, "internal_gl", "Entered by hand");
        skipped.Status.Should().Be(HttpStatusCode.OK, skipped.ToString());
        skipped.Message.Should().Be("Skipped.");
        skipped.Result.TargetStatus("internal_gl").Should().Be("Skipped");
        skipped.Result.TargetError("internal_gl").Should().StartWith("Skipped by ").And.EndWith(": Entered by hand");
        await k.Ok(k.SetPeriodStatus(period, "Open"), "reopen");
        await DueNowAsync(o.OrgId, c.G("uuid"));
        await _f.RunGlRetryJobAsync();
        (await _f.JournalsAsync(o.OrgId, c.G("uuid"))).Should().BeEmpty("skipped is final");

        var byNumber = await k.Ok(k.Get($"{Posting}/log?search={Uri.EscapeDataString(c.S("invoiceNumber")!)}"), "search");
        byNumber.A("data").Select(r => r.G("sourceDocumentUuid")).Should().Equal(c.G("uuid"));
        var byType = await k.Ok(k.Get($"{Posting}/log?docType=SalesInvoice&pageSize=200"), "docType");
        byType.A("data").Should().HaveCount(3).And.OnlyContain(r => r.S("documentType") == "SalesInvoice");
        (await k.Ok(k.Get($"{Posting}/log?docType=Payment"), "docType Payment")).A("data").Should().BeEmpty();
        var future = Today.AddDays(10).ToString("yyyy-MM-dd");
        (await k.Ok(k.Get($"{Posting}/log?from={future}"), "from the future")).A("data").Should().BeEmpty();
        var newestFirst = byType.A("data").Select(r => r.P("modifiedAt").GetDateTime()).ToList();
        newestFirst.Should().BeInDescendingOrder();
    }
}
