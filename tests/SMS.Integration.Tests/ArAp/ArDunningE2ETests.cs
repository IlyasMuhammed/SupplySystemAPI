using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.FinancialPosting;
using SMS.Integration.Tests.GeneralLedger;
using SMS.Integration.Tests.SapAlignment;
using Xunit;
using static SMS.Integration.Tests.ArAp.Ar;

namespace SMS.Integration.Tests.ArAp;

/// <summary>
/// A48 (QA) â€” dunning end to end (C5, D-10, D-11, D-16; TS-DN-001..011, BR-DN-001..010, task 584). Each test has its own
/// organization (one run per org per day, BR-DN-010) and the procedure QA-D: grace 5, minimum 50, levels 1..4 at 7 / 14 / 30 /
/// 30 days, a 25 charge at level 2, block sales at level 4. Overdue states are made by back-dating issued invoices and the
/// profile's last dunning date in SQL. Mail goes to the host's capture (<c>Mail.Accept = true</c>), never to SMTP.
/// Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~ArAp.ArDunningE2ETests</c>.
/// </summary>
public sealed class ArDunningE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;

    public ArDunningE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _f.ShouldBeOnLocalDb();
        _f.Mail.Accept = true;
        _root = new SapKit(factory, "A48DN");
    }

    private static object[] QaLevels() => [Level(1, 7), Level(2, 14, charge: 25m), Level(3, 30), Level(4, 30)];

    private static async Task<Guid> QaProcedureAsync(ArOrg o, bool isDefault = false) =>
        (await o.K.Ok(o.CreateProcedure($"QAD{Guid.NewGuid():N}"[..10].ToUpperInvariant(), 5, 50m, 4, QaLevels(), isDefault), "create procedure")).G("uuid");

    /// <summary>A customer with one issued invoice of <paramref name="units"/> Ã— 100 due <paramref name="dueDaysAgo"/> days ago.</summary>
    private static async Task<(Partner C, Guid Inv)> OverdueAsync(ArOrg o, Guid procedure, string label, int dueDaysAgo, decimal units = 1m)
    {
        var c = await o.CustomerAsync(label);
        await o.AssignProcedureAsync(c.Uuid, procedure);
        await o.SetEmailAsync(c); // an Email level with no address is Failed and moves nothing
        var inv = await o.IssuedInvoiceAsync(c, units);
        await o.BackdateSalesInvoiceAsync(inv, dueDaysAgo);
        return (c, inv);
    }

    private static async Task<JsonElement> PreviewOkAsync(ArOrg o) => await o.K.Ok(o.Preview(), "dunning preview");

    private static bool Proposed(JsonElement run, Guid c, out JsonElement item)
    {
        item = run.ItemFor(c);
        return item.ValueKind == JsonValueKind.Object && item.S("status") is not ("Skipped" or "Cancelled");
    }

    [Fact]
    public async Task Seeds_standard_procedure_and_default_tolerance()
    {
        var o = await _root.NewArOrgAsync("DN0", stock: 5m);
        var procs = Rows(await o.K.Ok(o.Procedures(), "procedures"));
        var std = procs.Single(p => p.S("code") == "STANDARD");
        std.B("isDefault").Should().BeTrue("D-16");
        var levels = std.A("levels").OrderBy(l => l.I("levelNumber")).ToList();
        levels.Select(l => l.I("levelNumber")).Should().Equal(1, 2, 3, 4);
        levels.Select(l => l.I("daysAfterPrevious")).Should().Equal(0, 14, 14, 14);
        levels.Should().OnlyContain(l => l.IsNull("chargeAmount"), "D-16: currency-neutral, no charges");

        var rule = Rows(await o.K.Ok(o.Tolerances(), "tolerances")).Single(r => r.B("isDefault"));
        rule.S("name").Should().Be("DEFAULT");
        rule.D("priceTolerancePercent").Should().Be(2m);
        rule.D("priceToleranceAmount").Should().Be(0m);
        rule.D("quantityTolerancePercent").Should().Be(0m);
        rule.B("autoApproveWithin").Should().BeFalse();
    }

    [Fact]
    public async Task Procedure_levels_must_be_contiguous()
    {
        var o = await _root.NewArOrgAsync("DNV", stock: 5m);
        (await o.CreateProcedure("GAPPY", 0, 0m, null, [Level(1, 7), Level(3, 14)])).ShouldRefuse(HttpStatusCode.BadRequest,
            "Dunning levels must be numbered 1 to 2 without gaps.", "BR-DN-004: levels 1, 3");
    }

    [Fact]
    public async Task TS_DN_001_to_005_eligibility_in_one_preview()
    {
        var o = await _root.NewArOrgAsync("DN1", stock: 50m);
        var proc = await QaProcedureAsync(o);
        var (c1, _) = await OverdueAsync(o, proc, "Twelve days", 12);
        var (c2, _) = await OverdueAsync(o, proc, "Three days", 3);
        var (c3, _) = await OverdueAsync(o, proc, "L2 twenty", 60);
        await o.SetDunningStateAsync(c3.Uuid, 2, 20);
        var (c4, _) = await OverdueAsync(o, proc, "L2 thirty-five", 80);
        await o.SetDunningStateAsync(c4.Uuid, 2, 35);
        var (c5, inv5) = await OverdueAsync(o, proc, "Thirty owed", 40);
        await o.PayCustomerAsync(c5, 70m, (inv5, 70m)); // 30 left < minimum 50

        var run = await PreviewOkAsync(o);
        run.S("status").Should().Be("Draft");

        Proposed(run, c1.Uuid, out var i1).Should().BeTrue($"TS-DN-001: 12 days overdue, grace 5, level 1 at 7 â€” {J.Short(run)}");
        i1.I("dunningLevel").Should().Be(1);
        i1.D("overdueAmount").Should().Be(100m);
        Proposed(run, c2.Uuid, out _).Should().BeFalse("TS-DN-002 / BR-DN-001: within the grace period");
        Proposed(run, c3.Uuid, out _).Should().BeFalse("TS-DN-003 / BR-DN-003: 20 of the 30 days since level 2");
        Proposed(run, c4.Uuid, out var i4).Should().BeTrue("TS-DN-004: 35 days since level 2");
        i4.I("dunningLevel").Should().Be(3, "BR-DN-004: 2 â†’ 3, never further");
        Proposed(run, c5.Uuid, out _).Should().BeFalse("TS-DN-005 / BR-DN-002: 30 overdue is below the minimum of 50");
    }

    [Fact]
    public async Task TS_DN_006_execute_sends_the_letters_and_moves_the_levels()
    {
        var o = await _root.NewArOrgAsync("DN6", stock: 10m);
        var proc = await QaProcedureAsync(o);
        var (c, _) = await OverdueAsync(o, proc, "Mail me", 12, units: 3m);
        var email = await o.SetEmailAsync(c);

        var run = await PreviewOkAsync(o);
        Proposed(run, c.Uuid, out _).Should().BeTrue(J.Short(run));
        var done = await o.K.Ok(o.ExecuteDunning(run.G("uuid")), "execute dunning");
        done.S("status").Should().Be("Completed");
        var item = done.ItemFor(c.Uuid);
        item.S("status").Should().Be("Sent", J.Short(item));

        var mails = _f.Mail.To(email);
        mails.Should().ContainSingle("one letter per customer");
        mails[0].Subject.Should().Be($"Reminder 1 for {c.Name}", "D-11 placeholders filled");
        mails[0].Body.Should().Contain(c.Name).And.Contain("300").And.NotContain("{TotalOverdue}").And.NotContain("{InvoiceList}");

        var p = await o.ProfileAsync(c.Uuid);
        p.I("currentDunningLevel").Should().Be(1);
        p.S("lastDunningDate").Should().StartWith(Iso(Today));
        (await o.K.Ok(o.DunningHistory(c.Uuid), "dunning history")).A("events").Should().Contain(e => e.I("level") == 1 && e.S("status") == "Sent");
    }

    [Fact]
    public async Task TS_DN_007_charge_is_a_customer_document_posted_dr_receivable_cr_revenue()
    {
        var o = await _root.NewArOrgAsync("DN7", gl: true, stock: 10m);
        var proc = await QaProcedureAsync(o);
        var (c, inv) = await OverdueAsync(o, proc, "Charge me", 40, units: 2m);
        await o.SetDunningStateAsync(c.Uuid, 1, 20); // level 2 (charge 25) is due
        var before = await o.SalesBalanceAsync(inv);

        var run = await PreviewOkAsync(o);
        Proposed(run, c.Uuid, out var item).Should().BeTrue(J.Short(run));
        item.I("dunningLevel").Should().Be(2);
        item.D("chargeAmount").Should().Be(25m);
        var done = await o.K.Ok(o.ExecuteDunning(run.G("uuid")), "execute");
        var charge = done.ItemFor(c.Uuid).NG("chargeDocumentUuid");
        charge.Should().NotBeNull("D-10: the charge is a customer document");

        var doc = await o.K.GetInvoiceAsync(charge!.Value);
        doc.G("partnerId").Should().Be(c.Uuid);
        doc.D("grandTotal").Should().Be(25m);
        (await o.SalesBalanceAsync(inv)).Should().Be(before, "the charge is its own document, the invoice is untouched");
        (await o.K.Ok(o.CustomerBalance(c.Uuid), "balance")).D("totalOpen").Should().Be(225m);

        await _f.RunJobsAsync();
        var codes = await o.K.DefaultCodesAsync();
        var journals = await SapKit.WaitForAsync(() => _f.JournalsAsync(o.OrgId, charge.Value), j => j.Count > 0, "the charge's A44 journal");
        var je = journals.Last();
        je.Balanced.Should().BeTrue(je.ToString());
        je.Net(codes["DEFAULT_RECEIVABLE"]).Should().Be(25m, $"TS-DN-007: Dr receivable â€” {je}");
        je.Lines.Where(l => l.Code != codes["DEFAULT_RECEIVABLE"]).Sum(l => l.Credit - l.Debit).Should().Be(25m, "Cr revenue");
    }

    [Fact]
    public async Task TS_DN_008_reaching_the_block_level_puts_the_customer_on_hold()
    {
        var o = await _root.NewArOrgAsync("DN8", stock: 10m);
        var proc = await QaProcedureAsync(o);
        var (c, _) = await OverdueAsync(o, proc, "Block me", 120);
        await o.K.Ok(o.PutProfile(c.Uuid, new { blockSalesAtDunning = true }), "block sales at dunning");
        await o.SetDunningStateAsync(c.Uuid, 3, 40);

        var run = await PreviewOkAsync(o);
        Proposed(run, c.Uuid, out var item).Should().BeTrue(J.Short(run));
        item.I("dunningLevel").Should().Be(4);
        await o.K.Ok(o.ExecuteDunning(run.G("uuid")), "execute");

        var p = await o.ProfileAsync(c.Uuid);
        p.I("currentDunningLevel").Should().Be(4);
        p.B("creditHold").Should().BeTrue("BR-DN-005: level â‰¥ block level with block-sales on â†’ credit hold");
        (await o.CreditHistoryAsync(c.Uuid)).Should().Contain(h => h.S("action") == "DunningHold");
    }

    [Fact]
    public async Task TS_DN_009_paying_everything_resets_the_level()
    {
        var o = await _root.NewArOrgAsync("DN9", stock: 10m);
        var proc = await QaProcedureAsync(o);
        var (c, inv) = await OverdueAsync(o, proc, "Pay up", 50, units: 2m);
        await o.SetDunningStateAsync(c.Uuid, 2, 10);
        await o.PayCustomerAsync(c, 200m, (inv, 200m));
        await o.K.Ok(o.Preview(), "preview (D-AR-9: the auto-reset runs at every preview)");
        (await o.ProfileAsync(c.Uuid)).I("currentDunningLevel").Should().Be(0, "BR-DN-006: open balance 0 -> level 0");
        (await o.CreditHistoryAsync(c.Uuid)).Should().Contain(h => h.S("action") == "DunningReset");
    }

    [Fact]
    public async Task Contract_3_reset_also_happens_on_profile_read()
    {
        // API-CONTRACT §3 / D-CORE-9: "Reset to 0 when the customer's open balance is 0 (at preview time and on profile read)".
        var o = await _root.NewArOrgAsync("DN9R", stock: 10m);
        var proc = await QaProcedureAsync(o);
        var (c, inv) = await OverdueAsync(o, proc, "Paid, no preview", 50, units: 2m);
        await o.SetDunningStateAsync(c.Uuid, 2, 10);
        await o.PayCustomerAsync(c, 200m, (inv, 200m));
        (await o.ProfileAsync(c.Uuid)).I("currentDunningLevel").Should().Be(0, "contract §3: reset on profile read too");
    }

    [Fact]
    public async Task TS_DN_010_manual_reset_is_admin_only()
    {
        var o = await _root.NewArOrgAsync("DN10", stock: 10m);
        var proc = await QaProcedureAsync(o);
        var (c, _) = await OverdueAsync(o, proc, "Reset me", 50);
        await o.SetDunningStateAsync(c.Uuid, 2, 10);

        var executor = await o.UserWithAsync("dnexec", DUNNING_EXECUTE, AR_VIEW);
        (await o.DunningReset(c.Uuid, h: executor)).ShouldBeForbidden("BR-DN-009: DUNNING_EXECUTE cannot reset");
        var admin = await o.UserWithAsync("dnadmin", DUNNING_ADMIN, AR_VIEW);
        (await o.DunningReset(c.Uuid, "", h: admin)).Status.Should().Be(HttpStatusCode.BadRequest, "a reason is required");
        await o.K.Ok(o.DunningReset(c.Uuid, "Agreed payment plan", h: admin), "reset by admin");
        (await o.ProfileAsync(c.Uuid)).I("currentDunningLevel").Should().Be(0);
        (await o.CreditHistoryAsync(c.Uuid)).Should().Contain(h => h.S("action") == "DunningReset");
    }

    [Fact]
    public async Task TS_DN_011_one_run_per_day_and_completed_runs_are_immutable()
    {
        var o = await _root.NewArOrgAsync("DN11", stock: 10m);
        var proc = await QaProcedureAsync(o);
        await OverdueAsync(o, proc, "Once a day", 12);

        var run = await PreviewOkAsync(o);
        (await o.Preview()).ShouldRefuse(HttpStatusCode.Conflict, $"A dunning run already exists for", "BR-DN-010 while the draft exists");
        var done = await o.K.Ok(o.ExecuteDunning(run.G("uuid")), "execute");
        var number = done.S("runNumber");

        (await o.Preview()).ShouldRefuse(HttpStatusCode.Conflict, $"({number})", "TS-DN-011: second run the same day");
        (await o.ExecuteDunning(run.G("uuid"))).ShouldRefuse(HttpStatusCode.Conflict, $"Dunning run {number} is Completed; it can no longer change.",
            "BR-DN-008: completed runs are immutable");
        (await o.CancelDunning(run.G("uuid"))).ShouldRefuse(HttpStatusCode.Conflict, "it can no longer change.", "cancel a completed run");
    }

    [Fact]
    public async Task Cancelled_draft_frees_the_day()
    {
        var o = await _root.NewArOrgAsync("DNC", stock: 10m);
        var proc = await QaProcedureAsync(o);
        await OverdueAsync(o, proc, "Again", 12);
        var run = await PreviewOkAsync(o);
        await o.K.Ok(o.CancelDunning(run.G("uuid")), "cancel draft");
        (await PreviewOkAsync(o)).G("uuid").Should().NotBe(run.G("uuid"), "a cancelled run does not hold the day");
    }
}
