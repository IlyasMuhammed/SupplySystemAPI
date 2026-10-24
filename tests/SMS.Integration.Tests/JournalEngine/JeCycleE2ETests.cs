using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.FinancialPosting;
using SMS.Integration.Tests.GeneralLedger;
using SMS.Integration.Tests.SapAlignment;
using Xunit;
using static SMS.Integration.Tests.JournalEngine.Je;

namespace SMS.Integration.Tests.JournalEngine;

/// <summary>
/// A47 (QA) — end-to-end accounting cycles on the real host (LocalDB), tasks 421–426:
/// <list type="bullet">
/// <item>422 — <see cref="E2E_422_manual_entry_create_post_trial_balance"/>.</item>
/// <item>421 + 425, TS-PC-006/008/009/010, TS-TB-003 — <see cref="E2E_421_425_full_cycle_lock_year_end_close_P_and_L_zeroed"/>.</item>
/// <item>TS-PC-011 — <see cref="Year_end_close_with_a_net_loss_debits_retained_earnings"/>; TS-PC-007 — <see cref="Year_end_close_refused_while_a_period_is_open"/>.</item>
/// <item>424, TS-RV-001..006, BR-RV-*, TS-JE-018 — <see cref="E2E_424_reverse_restores_balances_and_auto_reconciles"/>.</item>
/// <item>423 — <see cref="E2E_423_invoice_and_payment_reconcile"/>.</item>
/// <item>426, TS-JE-014/015, BR-JE-012/013 — <see cref="E2E_426_multi_currency_entry_local_amounts_and_trial_balance"/>.</item>
/// </list>
/// <para>Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~JournalEngine.JeCycleE2ETests</c>.</para>
/// </summary>
public sealed class JeCycleE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;

    public JeCycleE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _f.ShouldBeOnLocalDb();
        _root = new SapKit(factory, "A47");
    }

    private static DateTime D(int month, int day) => new(Y, month, day);

    [Fact]
    public async Task E2E_422_manual_entry_create_post_trial_balance()
    {
        var o = await _root.NewJeOrgAsync("JC1");
        var k = o.K;
        var misc = await k.TypeAsync("MISC");
        Guid cash = o.Acc("DEFAULT_CASH"), rev = o.Acc("DEFAULT_REVENUE");
        string cashCode = o.Code("DEFAULT_CASH"), revCode = o.Code("DEFAULT_REVENUE");

        var draft = await o.DraftAsync(misc, cash, rev, 1000m, description: "A47 QA 422");
        var before = await k.TrialBalanceAsync(D(1, 1), D(12, 31));
        before.TbRow(cashCode).ValueKind.Should().Be(JsonValueKind.Undefined, "TS-TB-006: a draft is not in the trial balance");

        await k.Ok(k.PostEntry(draft.G("uuid")), "post");
        var tb = await k.TrialBalanceAsync(D(1, 1), D(12, 31));
        tb.B("isBalanced").Should().BeTrue(J.Short(tb));
        (tb.TbRow(cashCode).D("periodDebit"), tb.TbRow(cashCode).D("periodCredit")).Should().Be((1000m, 0m));
        (tb.TbRow(revCode).D("periodDebit"), tb.TbRow(revCode).D("periodCredit")).Should().Be((0m, 1000m));
        tb.D("totalDebit").Should().Be(1000m);
        tb.D("totalCredit").Should().Be(1000m);
        tb.TbRow(cashCode).P("account").G("uuid").Should().Be(cash);
        TbRows(tb).Should().HaveCount(2);

        // the list and the account filter show it
        (await k.ListEntriesAsync($"accountUuid={cash}")).Should().ContainSingle(e => e.G("uuid") == draft.G("uuid") && e.S("status") == "Posted");
        (await k.ListEntriesAsync("sourceModule=manual")).Should().ContainSingle();
    }

    [Fact]
    public async Task E2E_421_425_full_cycle_lock_year_end_close_P_and_L_zeroed()
    {
        var o = await _root.NewJeOrgAsync("JC2");
        var k = o.K;
        var misc = await k.TypeAsync("MISC");
        Guid ar = o.Acc("DEFAULT_RECEIVABLE"), rev = o.Acc("DEFAULT_REVENUE"), exp = o.Acc("DEFAULT_PURCHASE"), cash = o.Acc("DEFAULT_CASH"),
            re = o.Acc("DEFAULT_RETAINED_EARNINGS");
        string revCode = o.Code("DEFAULT_REVENUE"), expCode = o.Code("DEFAULT_PURCHASE"), reCode = o.Code("DEFAULT_RETAINED_EARNINGS"),
            cashCode = o.Code("DEFAULT_CASH");

        // create → post: revenue 5000 (Feb + today), expense 3000
        await o.PostedAsync(misc, ar, rev, 2000m, D(2, 1));
        await o.PostedAsync(misc, ar, rev, 3000m);
        await o.PostedAsync(misc, exp, cash, 3000m, D(6, 30));
        var tb = await k.TrialBalanceAsync(D(1, 1), D(12, 31));
        tb.B("isBalanced").Should().BeTrue();
        tb.TbRow(revCode).D("balance").Should().Be(-5000m);
        tb.TbRow(expCode).D("balance").Should().Be(3000m);

        var year = (await k.YearAsync()).G("uuid");

        // lock 1–12
        await k.LockAllMonthsAsync(year);
        var status = await k.Ok(k.CloseStatus(year), "status");
        status.A("periods").Where(p => p.I("periodNumber") != 13).Should().OnlyContain(p => p.S("status") == "Locked");
        status.B("canCloseYear").Should().BeTrue(J.Short(status.P("yearCloseBlockers")));

        // TS-PC-009 — preview
        var preview = await k.Ok(k.YearPreview(year), "TS-PC-009 preview");
        (preview.D("totalRevenue"), preview.D("totalExpenses"), preview.D("netIncome")).Should().Be((5000m, 3000m, 2000m), J.Short(preview));
        preview.P("retainedEarningsAccount").G("uuid").Should().Be(re, "DEFAULT_RETAINED_EARNINGS by default");
        (preview.I("periodNumber"), preview.S("entryDate"), preview.B("canExecute")).Should().Be((13, Iso(D(12, 31)), true));
        preview.A("lines").Should().Contain(l => l.P("account").S("accountCode") == revCode && l.D("closingDebit") == 5000m);
        preview.A("lines").Should().Contain(l => l.P("account").S("accountCode") == expCode && l.D("closingCredit") == 3000m);

        // TS-PC-006 — execute
        var done = await k.Ok(k.YearExecute(year), "TS-PC-006 execute");
        (done.D("netIncomeTransferred"), done.B("alreadyClosed")).Should().Be((2000m, false), J.Short(done));
        done.P("fiscalYear").B("isClosed").Should().BeTrue();
        var closeUuid = done.P("closingEntry").G("uuid");
        var closing = await k.EntryAsync(closeUuid);
        (closing.P("journalType").S("code"), closing.S("entryType"), closing.B("isClosingEntry"), closing.S("status"))
            .Should().Be(("CLOSE", "YEAR_END_CLOSE", true, "Posted"), J.Short(closing));
        closing.S("entryNumber").Should().Be($"CLOSE/{Y}/0001");
        closing.S("entryDate").Should().Be(Iso(D(12, 31)));
        closing.P("fiscalPeriod").I("periodNumber").Should().Be(13);
        closing.S("idempotencyKey").Should().Be($"yearclose:{year}");
        closing.LinesOn(rev).Sum(l => l.D("debit") - l.D("credit")).Should().Be(5000m, "BR-PC-005: revenue debited");
        closing.LinesOn(exp).Sum(l => l.D("debit") - l.D("credit")).Should().Be(-3000m, "expense credited");
        closing.LinesOn(re).Sum(l => l.D("debit") - l.D("credit")).Should().Be(-2000m, "profit credited to retained earnings");

        // BR-PC-006 — the year and every period closed
        var after = await k.Ok(k.CloseStatus(year), "status after close");
        after.P("fiscalYear").B("isClosed").Should().BeTrue();
        after.A("periods").Should().OnlyContain(p => p.S("status") == "Closed");
        after.P("closingEntry").G("uuid").Should().Be(closeUuid);

        // 425 — P&L zeroed (closing entries included); TS-TB-003 — excluded by default
        var withClose = await k.TrialBalanceAsync(D(1, 1), D(12, 31), closing: true);
        withClose.TbRow(revCode).D("balance").Should().Be(0m);
        withClose.TbRow(expCode).D("balance").Should().Be(0m);
        withClose.TbRow(reCode).D("balance").Should().Be(-2000m);
        withClose.B("isBalanced").Should().BeTrue();
        var noClose = await k.TrialBalanceAsync(D(1, 1), D(12, 31));
        noClose.TbRow(revCode).D("balance").Should().Be(-5000m, "TS-TB-003");
        var chart = await k.AccountsAsync();
        chart[revCode].D("currentBalance").Should().Be(0m, "the chart's revenue balance is zeroed by the closing entry");
        chart[expCode].D("currentBalance").Should().Be(0m);
        chart[reCode].D("currentBalance").Should().Be(2000m, "credit-normal equity up by the profit");
        chart[cashCode].D("currentBalance").Should().Be(-3000m, "balance-sheet accounts untouched");

        // TS-PC-008 — a second execute returns the existing result
        var again = await k.Ok(k.YearExecute(year), "TS-PC-008 execute again");
        (again.B("alreadyClosed"), again.P("closingEntry").G("uuid")).Should().Be((true, closeUuid));
        (await _f.RowCountAsync("SELECT COUNT(*) FROM finance.JournalEntries WHERE OrganizationId = @o AND IsClosingEntry = 1", ("@o", o.OrgId)))
            .Should().Be(1, "BR-PC-009");

        // TS-PC-010 / BR-PC-007 — nothing more in a closed year
        (await k.CreateEntry(Body(misc, D(12, 31), [new JeLine(cash, 1m, 0m), new JeLine(rev, 0m, 1m)], closing: true)))
            .ShouldBeInvalid("JE-006", "Fiscal year is closed", "TS-PC-010");
        (await k.CreateEntry(Body(misc, D(6, 1), [new JeLine(cash, 1m, 0m), new JeLine(rev, 0m, 1m)])))
            .ShouldBeInvalid("JE-006", "Fiscal year is closed", "TS-PC-010 (a Closed period of a closed year)");
        (await k.ReopenPeriod((await k.PeriodAsync(D(6, 1))).G("uuid"))).Status.Should().Be(HttpStatusCode.Conflict, "a closed year refuses everything");
        (await k.ReverseEntry(closeUuid, "undo")).Status.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Year_end_close_with_a_net_loss_debits_retained_earnings()
    {
        var o = await _root.NewJeOrgAsync("JC3");
        var k = o.K;
        var misc = await k.TypeAsync("MISC");
        Guid rev = o.Acc("DEFAULT_REVENUE"), exp = o.Acc("DEFAULT_PURCHASE"), cash = o.Acc("DEFAULT_CASH"), re = o.Acc("DEFAULT_RETAINED_EARNINGS");
        await o.PostedAsync(misc, cash, rev, 1000m, D(3, 1));
        await o.PostedAsync(misc, exp, cash, 1500m, D(4, 1));
        var year = (await k.YearAsync()).G("uuid");
        await k.LockAllMonthsAsync(year);

        (await k.Ok(k.YearPreview(year), "preview")).D("netIncome").Should().Be(-500m);
        var done = await k.Ok(k.YearExecute(year), "execute");
        done.D("netIncomeTransferred").Should().Be(-500m);
        var closing = await k.EntryAsync(done.P("closingEntry").G("uuid"));
        closing.LinesOn(re).Sum(l => l.D("debit") - l.D("credit")).Should().Be(500m, "TS-PC-011: the loss debits retained earnings");
        closing.D("totalDebit").Should().Be(closing.D("totalCredit"));

        // BR-PC-008 — a non-equity retained earnings account is refused (another org, fresh year)
        var o2 = await _root.NewJeOrgAsync("JC3B");
        var y2 = (await o2.K.YearAsync()).G("uuid");
        await o2.K.LockAllMonthsAsync(y2);
        (await o2.K.YearExecute(y2, o2.Acc("DEFAULT_CASH")))
            .ShouldBeInvalid("PC-008", "Retained earnings account must be an active Level 4 Equity account", "BR-PC-008");
        // nothing to close: no entry at all, the year still closes
        var empty = await o2.K.Ok(o2.K.YearExecute(y2), "close an empty year");
        empty.P("fiscalYear").B("isClosed").Should().BeTrue();
        (empty.Has("closingEntry") ? empty.P("closingEntry").ValueKind : JsonValueKind.Null).Should().Be(JsonValueKind.Null, "contract §6: no entry when every balance is 0");
    }

    [Fact]
    public async Task Year_end_close_refused_while_a_period_is_open()
    {
        var o = await _root.NewJeOrgAsync("JC4");
        var k = o.K;
        var year = (await k.YearAsync()).G("uuid");
        foreach (var p in (await k.PeriodsAsync(year)).Where(p => p.I("periodNumber") is not (10 or 13)))
            await k.Ok(k.LockPeriod(p.G("uuid")), "lock");

        var preview = await k.Ok(k.YearPreview(year), "preview");
        preview.B("canExecute").Should().BeFalse();
        preview.A("blockers").Select(b => b.GetString()).Should().Contain("All periods must be Locked or Closed");
        (await k.YearExecute(year)).ShouldBeRefusedState("PC-005", "All periods must be Locked or Closed", "TS-PC-007");
        (await k.Ok(k.CloseStatus(year), "status")).B("canCloseYear").Should().BeFalse();

        // period 13 Locked is refused too (PC-006)
        var p10 = (await k.PeriodsAsync(year)).Single(p => p.I("periodNumber") == 10).G("uuid");
        var p13 = (await k.PeriodsAsync(year)).Single(p => p.I("periodNumber") == 13).G("uuid");
        await k.Ok(k.LockPeriod(p10), "lock October");
        await k.Ok(k.LockPeriod(p13), "lock period 13");
        (await k.YearExecute(year)).ShouldBeRefusedState("PC-006", "Period 13 must be Open or AdjustmentOnly", "PC-006");
    }

    [Fact]
    public async Task E2E_424_reverse_restores_balances_and_auto_reconciles()
    {
        var o = await _root.NewJeOrgAsync("JC5");
        var k = o.K;
        var misc = await k.TypeAsync("MISC");
        Guid cash = o.Acc("DEFAULT_CASH"), rev = o.Acc("DEFAULT_REVENUE");
        string cashCode = o.Code("DEFAULT_CASH"), revCode = o.Code("DEFAULT_REVENUE");
        var cashBefore = await k.CurrentBalanceAsync(cashCode);
        var revBefore = await k.CurrentBalanceAsync(revCode);

        var original = await o.PostedAsync(misc, cash, rev, 1000m, D(8, 15));
        var uuid = original.G("uuid");
        (await k.CurrentBalanceAsync(cashCode)).Should().Be(cashBefore + 1000m);

        // TS-RV-001 / TS-RV-006
        var result = await k.Ok(k.ReverseEntry(uuid, "Incorrect amount"), "TS-RV-001 reverse");
        // contract C-1: { reversal: JournalEntryDetail; original: EntryRef; reconcileGroupNumbers[]; warning? }
        result.P("original").G("uuid").Should().Be(uuid);
        result.A("reconcileGroupNumbers").Should().HaveCount(2, "one Full group per account (cash, revenue)");
        (result.Has("warning") ? result.S("warning") : null).Should().BeNull("a manual journal has no source document");
        var reversal = result.P("reversal");
        (reversal.S("status"), reversal.P("reversesEntry").G("uuid")).Should().Be(("Posted", uuid), "BR-RV-005 / BR-RV-007");
        reversal.S("description").Should().Be($"Reversal of {original.S("entryNumber")}: Incorrect amount", "TS-RV-006");
        reversal.S("entryDate").Should().Be(Iso(D(8, 15)), "D-14: the original's date by default");
        reversal.P("journalType").S("code").Should().Be("MISC");
        reversal.S("idempotencyKey").Should().Be($"reversal:{uuid}");
        reversal.LinesOn(cash).Single().D("credit").Should().Be(1000m, "BR-RV-004 mirror");
        reversal.LinesOn(rev).Single().D("debit").Should().Be(1000m);

        var after = await k.EntryAsync(uuid);
        (after.S("status"), after.P("reversedByEntry").G("uuid"), after.S("reversalReason")).Should().Be(("Reversed", reversal.G("uuid"), "Incorrect amount"), "BR-RV-006");
        after.B("canReverse").Should().BeFalse();

        // balances restored
        (await k.CurrentBalanceAsync(cashCode)).Should().Be(cashBefore, "424: CoA balances net zero");
        (await k.CurrentBalanceAsync(revCode)).Should().Be(revBefore);
        var tb = await k.TrialBalanceAsync(D(1, 1), D(12, 31));
        (tb.TbRow(cashCode).D("periodDebit"), tb.TbRow(cashCode).D("periodCredit"), tb.TbRow(cashCode).D("balance"))
            .Should().Be((1000m, 1000m, 0m), "D-CORE-4: the Reversed entry's lines still count, the pair nets to zero");

        // TS-RV-005 / BR-RV-008 — auto-reconciled per account
        foreach (var account in new[] { cash, rev })
        {
            var o1 = after.LinesOn(account).Single();
            var r1 = reversal.LinesOn(account).Single();
            (o1.B("reconciled"), o1.D("amountResidual"), r1.B("reconciled"), r1.D("amountResidual")).Should().Be((true, 0m, true, 0m), J.Short(o1));
            o1.NG("reconcileGroupUuid").Should().NotBeNull().And.Be(r1.NG("reconcileGroupUuid"));
            o1.P("clearingEntry").G("uuid").Should().Be(reversal.G("uuid"));
            var group = await k.Ok(k.ReconGroup(o1.G("reconcileGroupUuid")), "the reversal's reconcile group");
            group.S("reconcileType").Should().Be("Full");
        }
        (await k.OpenItemsAsync(cash)).Should().BeEmpty("both cash lines are reconciled");

        // TS-RV-002 / reversal of a reversal / TS-JE-018 / TS-RV-003
        (await k.ReverseEntry(uuid, "again")).ShouldBeRefusedState("JE-015", "Entry has already been reversed", "TS-RV-002");
        (await k.ReverseEntry(reversal.G("uuid"), "undo")).ShouldBeRefusedState("JE-015", "A reversal entry cannot be reversed", "reversal of a reversal");
        (await k.PostEntry(uuid)).ShouldBeRefusedState("JE-014", "Entry has been reversed", "TS-JE-018");
        var draft = await o.DraftAsync(misc, cash, rev, 10m);
        (await k.ReverseEntry(draft.G("uuid"), "draft")).ShouldBeRefusedState("JE-015", "Only posted entries can be reversed", "TS-RV-003");

        // reason rules
        var second = await o.PostedAsync(misc, cash, rev, 200m);
        (await k.ReverseEntry(second.G("uuid"), "")).ShouldBeInvalid(null, "A reversal reason is required", "no reason");
        (await k.ReverseEntry(second.G("uuid"), new string('x', 201))).ShouldBeInvalid(null, "The reversal reason can be at most 200 characters", "long reason");

        // TS-RV-004 — reversal date in a Closed period (January)
        var jan = (await k.PeriodAsync(D(1, 10))).G("uuid");
        await k.Ok(k.LockPeriod(jan), "lock January");
        await k.Ok(k.FinalClosePeriod(jan), "close January");
        var rv4 = await k.ReverseEntry(second.G("uuid"), "wrong period", D(1, 10));
        rv4.Status.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Conflict);
        rv4.ShouldRefuse(rv4.Status, "JE-006", "Reversal period is closed", "TS-RV-004");
        (await k.EntryAsync(second.G("uuid"))).S("status").Should().Be("Posted", "nothing changed");

        // a reversal on another date (open period) works and is dated so
        var dated = (await k.Ok(k.ReverseEntry(second.G("uuid"), "later", Today), "reverse with a date")).P("reversal");
        dated.S("entryDate").Should().Be(TodayIso);

        // the opening balance entry cannot be reversed (A42 opening via SQL insert would not be numbered; covered by RR unit tests)
    }

    [Fact]
    public async Task E2E_423_invoice_and_payment_reconcile()
    {
        var o = await _root.NewJeOrgAsync("JC6");
        var k = o.K;
        var (_, _, customer, item) = await k.StockedWorldAsync("JC6", price: 100m);
        var ar = o.Acc("DEFAULT_RECEIVABLE");

        // a real invoice and a real payment, both through the gateway
        var inv = await k.IssuedInvoiceAsync(customer, item, 2m, o.Fp.O.Pkr);
        var total = inv.D("grandTotal");
        var pay = await k.RecordPaymentAsync(customer, total, "CASH", (inv.G("uuid"), total));
        var invJ = await _f.JournalUuidAsync(o.OrgId, inv.G("uuid"));
        var payJ = await _f.JournalUuidAsync(o.OrgId, pay.G("paymentUuid"));

        var open = await k.OpenItemsAsync(ar);
        var invLine = open.Single(i => i.P("entry").G("uuid") == invJ);
        var payLine = open.Single(i => i.P("entry").G("uuid") == payJ);
        (invLine.D("amountResidual"), payLine.D("amountResidual")).Should().Be((total, -total));
        // the partner on gateway lines is C11 (GW) — asserted in JeGatewayE2ETests.TS_GW_001

        var suggestion = Rows(await k.Ok(k.Suggest(ar, [invLine.G("lineUuid")]), "suggest")).First();
        suggestion.A("lineUuids").Select(x => x.GetGuid()).Should().Contain(payLine.G("lineUuid"));

        var group = await k.Ok(k.Reconcile([invLine.G("lineUuid"), payLine.G("lineUuid")]), "423 reconcile");
        (group.S("reconcileType"), group.D("amount")).Should().Be(("Full", total));
        (await k.OpenItemsAsync(ar)).Should().NotContain(i => i.P("entry").G("uuid") == invJ);

        await k.Ok(k.Unreconcile(group.G("uuid")), "unreconcile");
        (await k.OpenItemsAsync(ar)).Should().Contain(i => i.G("lineUuid") == invLine.G("lineUuid") && i.D("amountResidual") == total);

        // a partial payment of a second invoice
        var inv2 = await k.IssuedInvoiceAsync(customer, item, 1m, o.Fp.O.Pkr);
        var t2 = inv2.D("grandTotal");
        var part = await k.RecordPaymentAsync(customer, 50m, "CASH", (inv2.G("uuid"), 50m));
        var inv2J = await _f.JournalUuidAsync(o.OrgId, inv2.G("uuid"));
        var partJ = await _f.JournalUuidAsync(o.OrgId, part.G("paymentUuid"));
        var open2 = await k.OpenItemsAsync(ar);
        var i2 = open2.Single(i => i.P("entry").G("uuid") == inv2J);
        var p2 = open2.Single(i => i.P("entry").G("uuid") == partJ);
        var pg = await k.Ok(k.Reconcile([i2.G("lineUuid"), p2.G("lineUuid")]), "partial");
        pg.S("reconcileType").Should().Be("Partial");
        pg.A("lines").Single(l => l.G("lineUuid") == i2.G("lineUuid")).D("amountResidual").Should().Be(t2 - 50m);
    }

    /// <summary>
    /// REV-1 — after FY Y's year-end close, FY Y+1's trial balance (default flags: opening included, closing entries excluded)
    /// opens with the P&amp;L at 0 and retained earnings holding the transfer; the ledger drill-down agrees.
    /// </summary>
    [Fact]
    public async Task REV_1_next_year_opens_with_P_and_L_zero_after_the_year_end_close()
    {
        var o = await _root.NewJeOrgAsync("JCR");
        var k = o.K;
        var misc = await k.TypeAsync("MISC");
        Guid ar = o.Acc("DEFAULT_RECEIVABLE"), rev = o.Acc("DEFAULT_REVENUE"), exp = o.Acc("DEFAULT_PURCHASE"), cash = o.Acc("DEFAULT_CASH");
        string arCode = o.Code("DEFAULT_RECEIVABLE"), revCode = o.Code("DEFAULT_REVENUE"), expCode = o.Code("DEFAULT_PURCHASE"),
            reCode = o.Code("DEFAULT_RETAINED_EARNINGS"), cashCode = o.Code("DEFAULT_CASH");

        await o.PostedAsync(misc, ar, rev, 5000m, D(3, 1));
        await o.PostedAsync(misc, exp, cash, 3000m, D(6, 1));
        var next = await k.Ok(k.CreateYear(Gl.Day(Y + 1, 1, 1), Gl.Day(Y + 1, 12, 31)), "fiscal year Y+1");
        var nextUuid = next.G("uuid");

        var year = (await k.YearAsync()).G("uuid");
        await k.LockAllMonthsAsync(year);
        (await k.Ok(k.YearExecute(year), "close FY Y")).D("netIncomeTransferred").Should().Be(2000m);

        // activity in Y+1
        await o.PostedAsync(misc, cash, rev, 100m, new DateTime(Y + 1, 1, 15));

        foreach (var (label, call) in new (string, Func<Task<Api>>)[]
                 {
                     ("by fiscal year", () => k.Get($"{Tb}?fiscalYearUuid={nextUuid}")),
                     ("by dates", () => k.TrialBalance(new DateTime(Y + 1, 1, 1), new DateTime(Y + 1, 12, 31), opening: true)),
                 })
        {
            var tb = await k.Ok(call(), $"FY Y+1 trial balance {label}");
            tb.B("isBalanced").Should().BeTrue($"{label} — {J.Short(tb)}");
            decimal Opening(string code) => tb.TbRow(code) is { ValueKind: System.Text.Json.JsonValueKind.Object } r ? r.D("openingDebit") - r.D("openingCredit") : 0m;
            Opening(revCode).Should().Be(0m, $"REV-1 ({label}): revenue opens at 0 after the close");
            Opening(expCode).Should().Be(0m, $"REV-1 ({label}): expense opens at 0");
            Opening(reCode).Should().Be(-2000m, $"REV-1 ({label}): retained earnings opens with the transfer");
            Opening(arCode).Should().Be(5000m, "balance-sheet accounts carry over");
            Opening(cashCode).Should().Be(-3000m);
            tb.TbRow(revCode).D("balance").Should().Be(-100m, $"{label}: only Y+1's revenue");
            tb.P("totals").D("openingDebit").Should().Be(tb.P("totals").D("openingCredit"), label);
        }

        // the ledger drill-down agrees (default flags)
        var ledger = await k.Ok(k.Ledger(rev, new DateTime(Y + 1, 1, 1), new DateTime(Y + 1, 12, 31)), "revenue ledger Y+1");
        ledger.D("openingBalance").Should().Be(0m, "REV-1: the ledger opening includes the earlier close");
        ledger.D("closingBalance").Should().Be(-100m);
        var reLedger = await k.Ok(k.Ledger(o.Acc("DEFAULT_RETAINED_EARNINGS"), new DateTime(Y + 1, 1, 1), new DateTime(Y + 1, 12, 31)), "RE ledger Y+1");
        reLedger.D("openingBalance").Should().Be(-2000m);

        // FY Y itself still shows its own P&L before the close by default (BR-TB-004)
        (await k.TrialBalanceAsync(D(1, 1), D(12, 31))).TbRow(revCode).D("balance").Should().Be(-5000m);
    }

    [Fact]
    public async Task E2E_426_multi_currency_entry_local_amounts_and_trial_balance()
    {
        var o = await _root.NewJeOrgAsync("JC7");
        var k = o.K;
        await _root.EnsureCurrencyAsync("USD", "US Dollar", "$");
        await k.EnsureOrgCurrencyAsync("USD", "US Dollar", "$");
        var misc = await k.TypeAsync("MISC");
        Guid cash = o.Acc("DEFAULT_CASH"), rev = o.Acc("DEFAULT_REVENUE");
        string cashCode = o.Code("DEFAULT_CASH"), revCode = o.Code("DEFAULT_REVENUE");
        JeLine[] lines = [new JeLine(cash, 1000m, 0m), new JeLine(rev, 0m, 1000m)];

        // TS-JE-014 / BR-JE-012
        (await k.CreateEntry(Body(misc, Today, lines, currency: "USD"))).ShouldBeInvalid("JE-012", "Exchange rate required for foreign currency", "TS-JE-014");
        (await k.CreateEntry(Body(misc, Today, lines, currency: "USD", rate: 0m))).ShouldBeInvalid("JE-012", "Exchange rate", "rate 0");
        (await k.CreateEntry(Body(misc, Today, lines, currency: "US"))).ShouldBeInvalid("JE-012", "Currency must be a 3-letter currency code", "bad code");

        // TS-JE-015 / BR-JE-013 — base = amount × 3.67
        var e = await k.CreateEntryOk(Body(misc, Today, lines, currency: "USD", rate: 3.67m, post: true), "TS-JE-015");
        (e.S("currencyCode"), e.ND("exchangeRate"), e.D("totalDebit"), e.D("totalCredit")).Should().Be(("USD", 3.67m, 3670m, 3670m), J.Short(e));
        var cl = e.LinesOn(cash).Single();
        (cl.D("debit"), cl.ND("amountCurrency"), cl.S("currencyCode"), cl.ND("exchangeRate")).Should().Be((3670m, 1000m, "USD", 3.67m));
        var rl = e.LinesOn(rev).Single();
        (rl.D("credit"), rl.ND("amountCurrency")).Should().Be((3670m, -1000m), "amountCurrency is signed, debit +");

        // rounding of the base: 333.33 × 3.67 = 1223.3211 → 1223.32
        var r = await k.CreateEntryOk(Body(misc, Today, [new JeLine(cash, 333.33m, 0m), new JeLine(rev, 0m, 333.33m)], currency: "USD", rate: 3.67m, post: true));
        r.LinesOn(cash).Single().D("debit").Should().Be(1223.32m, "contract §2: × rate rounded to 2 dp");

        // the trial balance is in the reporting (base) currency
        var tb = await k.TrialBalanceAsync(D(1, 1), D(12, 31));
        tb.TbRow(cashCode).D("periodDebit").Should().Be(3670m + 1223.32m);
        tb.TbRow(revCode).D("periodCredit").Should().Be(3670m + 1223.32m);
        tb.B("isBalanced").Should().BeTrue();
        (await k.CurrentBalanceAsync(cashCode)).Should().Be(3670m + 1223.32m);

        // the base currency itself needs no rate
        (await k.CreateEntry(Body(misc, Today, lines, currency: "PKR"))).Status.Should().Be(HttpStatusCode.OK);
    }
}
