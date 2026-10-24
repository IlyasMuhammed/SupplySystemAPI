using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using SMS.Integration.Tests.DataImport;
using SMS.Integration.Tests.ModuleRegistry;
using SMS.Integration.Tests.SapAlignment;
using SMS.Integration.Tests.ServiceOrders;
using Xunit;
using static SMS.Integration.Tests.GeneralLedger.Gl;

namespace SMS.Integration.Tests.GeneralLedger;

/// <summary>
/// A42 (QA) — fiscal years, opening balances and the setup state machine over HTTP on the real host (LocalDB), with Finance's
/// opening balance date (A40) and the A40 opening imports for the sub-ledger reference:
/// <list type="bullet">
/// <item>T-FP-01..05 — <see cref="Fiscal_years_have_13_periods_and_period_rules"/>.</item>
/// <item>T-OB-01..07, D-19, D-56, D-57 — <see cref="An_unbalanced_opening_balance_posts_its_plug_to_3900_and_locks_the_date"/>,
/// <see cref="A_balanced_opening_balance_posts_without_a_plug"/>, <see cref="Skipping_needs_no_drafts_date_or_year"/>.</item>
/// <item>T-SS-01..10, D-4 — <see cref="The_setup_runs_from_NotStarted_to_Complete_and_gates_only_the_dashboard"/>,
/// <see cref="Posted_non_opening_journals_block_reset"/>.</item>
/// </list>
/// <para>Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~GlSetupE2ETests</c>.</para>
/// </summary>
public sealed class GlSetupE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapKit _root;
    private readonly SapWebApplicationFactory _f;

    public GlSetupE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _f.ShouldBeOnLocalDb();
        _root = new SapKit(factory, "A42");
    }

    private static JsonElement Row(JsonElement opening, string code) =>
        opening.A("groups").SelectMany(g => g.A("rows")).Single(r => r.S("accountCode") == code);

    // ── T-FP ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Fiscal_years_have_13_periods_and_period_rules()
    {
        var o = await _root.OrgAsync("FP");
        var k = o.K;
        await k.ApplyAsync();

        // T-FP-01 — 12 monthly periods + period 13, base currency, default name
        var year = await k.CreateYearOk(Y);
        (year.S("fiscalYearName"), year.S("startDate"), year.S("endDate"), year.S("reportingCurrencyCode"), year.B("isClosed"), year.B("hasPostings"))
            .Should().Be(($"FY {Y}", Day(Y, 1, 1), Day(Y, 12, 31), "PKR", false, false));
        var periods = year.A("periods");
        periods.Should().HaveCount(13);
        periods.Take(12).Select(p => p.S("periodName")).Should().Equal(Enumerable.Range(1, 12).Select(m => new DateTime(Y, m, 1).ToString("MMMM yyyy", System.Globalization.CultureInfo.InvariantCulture)));
        periods.Take(12).Select(p => (p.S("startDate"), p.S("endDate"))).Should().Equal(Enumerable.Range(1, 12)
            .Select(m => (Day(Y, m, 1), Day(Y, m, DateTime.DaysInMonth(Y, m)))));
        var p13 = periods[12];
        (p13.I("periodNumber"), p13.S("periodName"), p13.S("startDate"), p13.S("endDate"), p13.B("isAdjustment"))
            .Should().Be((13, "Year-End Adjustment", Day(Y, 12, 31), Day(Y, 12, 31), true));
        periods.Should().OnlyContain(p => p.S("status") == "Open");
        (await _f.RowCountAsync("SELECT COUNT(*) FROM finance.FiscalPeriods p JOIN finance.FiscalYears y ON y.Id = p.FiscalYearId WHERE y.Uuid = @u",
            ("@u", year.G("uuid")))).Should().Be(13);
        (await k.StatusAsync()).S("status").Should().Be("DefaultsSet", "US_GENERAL assigns every default, so the year completes steps 3 and 4");

        // T-FP-02 — overlap, duplicate name, length
        (await k.CreateYear(Day(Y, 6, 1), Day(Y + 1, 5, 31))).ShouldFail(HttpStatusCode.Conflict, "overlap", "Fiscal year dates overlap with an existing year.");
        (await k.CreateYear(Day(Y + 1, 1, 1), Day(Y + 1, 12, 31), $"FY {Y}")).ShouldFail(HttpStatusCode.Conflict, "name", $"A fiscal year named \"FY {Y}\" already exists.");
        (await k.CreateYear(Day(Y + 1, 1, 1), Day(Y + 2, 1, 1))).ShouldFail(HttpStatusCode.BadRequest, "13 months", "A fiscal year can be at most 12 months long.");
        (await k.CreateYear(Day(Y + 1, 1, 1), Day(Y + 1, 12, 31), null, "PK")).ShouldFail(HttpStatusCode.BadRequest, "currency", "3-letter");

        // a short next year: 3 monthly periods + 13, custom dates move the neighbour
        var shortYear = await k.Ok(k.CreateYear(Day(Y + 1, 1, 1), Day(Y + 1, 3, 31), "Short", "usd"), "a short year");
        (shortYear.A("periods").Count, shortYear.S("reportingCurrencyCode")).Should().Be((4, "USD"));
        var sp = shortYear.A("periods");
        await k.Ok(k.Put($"/api/finance/fiscal-periods/{sp[0].G("uuid")}", new { endDate = Day(Y + 1, 1, 28) }), "4-4-5 style edit");
        var moved = await k.Ok(k.Get($"/api/finance/fiscal-years/{shortYear.G("uuid")}/periods"), "periods");
        (moved.Items()[0].S("endDate"), moved.Items()[1].S("startDate")).Should().Be((Day(Y + 1, 1, 28), Day(Y + 1, 1, 29)));
        (await k.Put($"/api/finance/fiscal-periods/{sp[0].G("uuid")}", new { startDate = Day(Y + 1, 1, 2) }))
            .ShouldFail(HttpStatusCode.BadRequest, "first start fixed", "The first period must start on the fiscal year's start date");
        (await k.Put($"/api/finance/fiscal-periods/{sp[1].G("uuid")}", new { endDate = Day(Y + 1, 1, 10) }))
            .ShouldFail(HttpStatusCode.BadRequest, "ends before it starts", "would end before it starts.");

        // new dates regenerate the periods (new uuids) while nothing is posted and every period is open
        var reshaped = await k.Ok(k.Put($"/api/finance/fiscal-years/{shortYear.G("uuid")}", new { startDate = Day(Y + 1, 1, 1), endDate = Day(Y + 1, 2, 28) }), "new dates");
        reshaped.A("periods").Select(p => $"{p.I("periodNumber")}:{p.S("startDate")}:{p.S("endDate")}").Should().Equal(
            $"1:{Day(Y + 1, 1, 1)}:{Day(Y + 1, 1, 31)}", $"2:{Day(Y + 1, 2, 1)}:{Day(Y + 1, 2, 28)}", $"13:{Day(Y + 1, 2, 28)}:{Day(Y + 1, 2, 28)}");
        reshaped.A("periods").Select(p => p.G("uuid")).Should().NotIntersectWith(sp.Select(p => p.G("uuid")));
        (await _f.RowCountAsync("SELECT COUNT(*) FROM finance.FiscalPeriods p JOIN finance.FiscalYears y ON y.Id = p.FiscalYearId WHERE y.Uuid = @u",
            ("@u", shortYear.G("uuid")))).Should().Be(3, "the old periods are gone");

        // BR-FP-004 — a draft journal entry dated in a period blocks closing it (A42 writes none: inserted here)
        await _f.ExecuteAsync(
            Gl.EnsureMiscJournalTypeSql +
            "INSERT INTO finance.JournalEntries (Uuid, OrganizationId, EntryNumber, EntryType, EntryDate, Status, TotalDebit, TotalCredit, CreatedAt, ModifiedAt, JournalTypeId) " +
            "VALUES (NEWID(), @o, 'JE-QA-DRAFT', 'MANUAL', @d, 'Draft', 0, 0, SYSUTCDATETIME(), SYSUTCDATETIME(), " + Gl.MiscJournalTypeIdSql + ")",
            ("@o", o.OrgId), ("@d", new DateTime(Y, 2, 10)));
        (await k.Put($"/api/finance/fiscal-periods/{periods[1].G("uuid")}", new { status = "Closed" })).ShouldFail(HttpStatusCode.Conflict, "draft in February",
            "draft journal entries");
        await _f.ExecuteAsync("DELETE FROM finance.JournalEntries WHERE OrganizationId = @o AND EntryNumber = 'JE-QA-DRAFT'", ("@o", o.OrgId));

        // T-FP-03 — lock / reopen
        var p1 = periods[0].G("uuid");
        (await k.Ok(k.Put($"/api/finance/fiscal-periods/{p1}", new { status = "locked" }), "lock")).S("status").Should().Be("Locked");
        (await k.Ok(k.Put($"/api/finance/fiscal-periods/{p1}", new { status = "Open" }), "reopen")).S("status").Should().Be("Open");
        (await k.Put($"/api/finance/fiscal-periods/{p1}", new { status = "Frozen" })).ShouldFail(HttpStatusCode.BadRequest, "unknown status", "Open, Locked or Closed");

        // T-FP-04 — close is final
        (await k.Ok(k.Put($"/api/finance/fiscal-periods/{p1}", new { status = "Closed" }), "close")).S("status").Should().Be("Closed");
        (await k.Put($"/api/finance/fiscal-periods/{p1}", new { status = "Open" })).ShouldFail(HttpStatusCode.Conflict, "reopen closed", "A closed period cannot be reopened.");
        (await k.Put($"/api/finance/fiscal-periods/{p1}", new { status = "Locked" })).ShouldFail(HttpStatusCode.Conflict, "lock closed", "A closed period cannot be reopened.");
        (await k.Put($"/api/finance/fiscal-years/{year.G("uuid")}", new { startDate = Day(Y, 1, 1), endDate = Day(Y, 11, 30) }))
            .ShouldFail(HttpStatusCode.Conflict, "dates with a closed period", "The dates can only be changed while every period of the year is open.");

        // T-FP-05 — the year closes only when every period is closed
        (await k.Post($"/api/finance/fiscal-years/{year.G("uuid")}/close")).ShouldFail(HttpStatusCode.Conflict, "open periods", "All periods must be closed first.");
        foreach (var p in periods.Skip(1))
            await k.Ok(k.Put($"/api/finance/fiscal-periods/{p.G("uuid")}", new { status = "Closed" }), $"close {p.S("periodName")}");
        var closed = await k.Ok(k.Post($"/api/finance/fiscal-years/{year.G("uuid")}/close"), "close the year");
        (closed.B("isClosed"), closed.IsNull("closedAt")).Should().Be((true, false));
        (await k.Ok(k.Post($"/api/finance/fiscal-years/{year.G("uuid")}/close"), "close again")).B("isClosed").Should().BeTrue();
        (await k.Put($"/api/finance/fiscal-years/{year.G("uuid")}", new { fiscalYearName = "Renamed" })).ShouldFail(HttpStatusCode.Conflict, "closed year",
            "A closed fiscal year cannot be changed.");
        (await k.Put($"/api/finance/fiscal-periods/{p13.G("uuid")}", new { periodName = "x" })).ShouldFail(HttpStatusCode.Conflict, "closed year period",
            "A closed fiscal year cannot be changed.");
        (await k.Ok(k.Get("/api/finance/fiscal-years"), "list")).Items().Select(y => y.S("fiscalYearName")).Should().Equal($"FY {Y}", "Short");
    }

    // ── T-OB ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_unbalanced_opening_balance_posts_its_plug_to_3900_and_locks_the_date()
    {
        var o = await _root.OrgAsync("OBU");
        var k = o.K;
        await k.ApplyAsync();
        var a = await k.AccountsAsync();
        Guid U(string code) => a[code].G("uuid");

        // D-19 refusals, all at once, nothing saved
        var bad = await k.Batch((U("111"), 10m, 0m), (U("1111"), 5m, 5m), (U("3900"), 0m, 10m));
        bad.ShouldFail(HttpStatusCode.BadRequest, "three bad entries", "3 opening balances are invalid.");
        bad.Result.A("errors").Select(e => $"{e.S("accountCode")}:{e.S("message")}").Should().BeEquivalentTo(
            "111:Only Level 4 accounts accept opening balances.", "1111:Cannot have both debit and credit amounts.",
            "3900:Opening Balance Equity (3900) is calculated automatically.");
        (await k.Batch((U("3900"), 0m, 10m))).ShouldFail(HttpStatusCode.BadRequest, "3900 alone", "Opening Balance Equity (3900) is calculated automatically.");
        (await k.Ok(k.Get("/api/finance/opening-balances/summary"), "summary")).I("entryCount").Should().Be(0);

        // drafts: Dr 1111 1,000 + 1121 15,000 + 1133 5,000; Cr 2111 8,200 + 3100 10,000 → difference 2,800, plug Credit
        var draft = await k.Ok(k.Batch((U("1111"), 1000m, 0m), (U("1121"), 15000m, 0m), (U("1133"), 5000m, 0m), (U("2111"), 0m, 8200m), (U("3100"), 0m, 10000m)), "drafts");
        var s = draft.P("summary");
        (s.D("totalDebits"), s.D("totalCredits"), s.D("difference"), s.B("isBalanced"), s.S("plugSide"), s.I("entryCount"))
            .Should().Be((21000m, 18200m, 2800m, false, "Credit", 5));
        (Row(draft, "3900").B("isPlug"), Row(draft, "3900").D("creditAmount"), Row(draft, "1121").D("debitAmount")).Should().Be((true, 0m, 15000m));
        draft.B("isPosted").Should().BeFalse();

        // the date and the year are checked in order (D-55)
        (await k.PostOpening(acknowledge: true)).ShouldFail(HttpStatusCode.BadRequest, "no date", "Set the opening balance date in Finance settings first.");
        await k.Ok(k.SetOpeningDate(Day(Y - 1, 12, 31)), "a date before the year");
        (await k.PostOpening(acknowledge: true)).ShouldFail(HttpStatusCode.BadRequest, "no year", "Set up a fiscal year first.");
        await k.CreateYearOk(Y);
        var outside = await k.PostOpening(acknowledge: true);
        outside.ShouldFail(HttpStatusCode.BadRequest, "outside the year");
        outside.Message.Should().Be("The opening balance date must fall within the first fiscal year.");
        await k.Ok(k.SetOpeningDate(Day(Y, 1, 31)), "the opening date inside the year");

        // D-12 — A40 customer / supplier opening balances as the sub-ledger reference
        await k.ImportXlsxAsync("CUSTOMER", ["Customer Code", "Customer Name", "Payment Terms (days)"], [["C-00042", $"GL customer {o.OrgId:N}"[..24], "30"]]);
        await k.ImportXlsxAsync("SUPPLIER", ["Supplier Code", "Supplier Name"], [["GLS-1", $"GL supplier {o.OrgId:N}"[..24]]]);
        var (cv, cs) = await k.ImportXlsxAsync("CUSTOMER_OPENING_BALANCE", ["Customer Code", "Invoice Reference", "Invoice Date", "Amount Owed"],
            [["C-00042", "INV-1", Day(Y, 1, 10), "15000.00"]]);
        cs.S("status").Should().Be("COMPLETED", J.Short(cv));
        var (sv, ss) = await k.ImportXlsxAsync("SUPPLIER_OPENING_BALANCE", ["Supplier Code", "Bill Reference", "Bill Date", "Amount Owed"],
            [["GLS-1", "BILL-1", Day(Y, 1, 15), "8200.00"]]);
        ss.S("status").Should().Be("COMPLETED", J.Short(sv));
        var withRef = await k.Ok(k.Get("/api/finance/opening-balances"), "opening balances");
        var sub = withRef.P("subledger");
        (sub.D("customerOpeningTotal"), sub.D("supplierOpeningTotal"), sub.D("openingStockValue"), sub.S("currencyCode")).Should().Be((15000m, 8200m, 0m, "PKR"));
        (sub.ND("receivableAccountBalance"), sub.S("receivableAccountCode"), sub.ND("payableAccountBalance"), sub.S("payableAccountCode"),
                sub.ND("inventoryAccountBalance"), sub.S("inventoryAccountCode"))
            .Should().Be((15000m, "1121", 8200m, "2111", 5000m, "1133"));
        withRef.S("conversionDate").Should().Be(Day(Y, 1, 31));

        // BR-OB-005 — the difference must be acknowledged
        (await k.PostOpening()).ShouldFail(HttpStatusCode.BadRequest, "unacknowledged",
            "Debits and credits differ by 2,800.00. Confirm the difference to post it to Opening Balance Equity (3900).");
        var posted = await k.Ok(k.PostOpening(acknowledge: true), "post with the difference acknowledged");
        var number = posted.S("journalEntryNumber")!;
        number.Should().MatchRegex($"^JE-{DateTime.UtcNow.Year}-\\d{{5}}$");
        posted.P("summary").D("difference").Should().Be(2800m);

        // the journal entry: balanced, dated at the conversion date, lines in code order + the plug last
        var je = (await _f.QueryAsync("SELECT Id, EntryNumber, EntryType, EntryDate, Status, TotalDebit, TotalCredit, CurrencyCode, Reference " +
                                      "FROM finance.JournalEntries WHERE OrganizationId = @o", ("@o", o.OrgId))).Single();
        (je["EntryNumber"], je["EntryType"], je["Status"], je["CurrencyCode"], je["Reference"]).Should()
            .Be((number, "OPENING_BALANCE", "Posted", "PKR", $"Opening Balance Entry — {Day(Y, 1, 31)}"));
        ((DateTime)je["EntryDate"]!).Should().Be(new DateTime(Y, 1, 31));
        (Convert.ToDecimal(je["TotalDebit"]), Convert.ToDecimal(je["TotalCredit"])).Should().Be((21000m, 21000m));
        var lines = await _f.QueryAsync("SELECT l.LineNumber, a.AccountCode, l.DebitAmount, l.CreditAmount, l.Memo FROM finance.JournalEntryLines l " +
                                        "JOIN finance.ChartOfAccounts a ON a.Id = l.AccountId WHERE l.JournalEntryId = @j ORDER BY l.LineNumber", ("@j", je["Id"]!));
        lines.Select(l => $"{l["AccountCode"]}:{Convert.ToDecimal(l["DebitAmount"]):0.##}:{Convert.ToDecimal(l["CreditAmount"]):0.##}").Should().Equal(
            "1111:1000:0", "1121:15000:0", "1133:5000:0", "2111:0:8200", "3100:0:10000", "3900:0:2800");
        lines.Last()["Memo"].Should().Be("Opening balance difference");
        lines.Sum(l => Convert.ToDecimal(l["DebitAmount"])).Should().Be(lines.Sum(l => Convert.ToDecimal(l["CreditAmount"])));
        (await _f.RowCountAsync("SELECT COUNT(*) FROM finance.OpeningBalanceEntries WHERE OrganizationId = @o AND EntryStatus = 'Posted' AND JournalEntryId = @j",
            ("@o", o.OrgId), ("@j", je["Id"]!))).Should().Be(5);

        // the accounts: opening / current balance in the normal direction, hasTransactions
        var after = await k.AccountsAsync();
        string Bal(string code) => $"{after[code].D("openingBalanceDebit"):0.##}/{after[code].D("openingBalanceCredit"):0.##}/{after[code].D("currentBalance"):0.##}/{after[code].B("hasTransactions")}";
        Bal("1111").Should().Be("1000/0/1000/True");
        Bal("2111").Should().Be("0/8200/8200/True");
        Bal("3100").Should().Be("0/10000/10000/True");
        Bal("3900").Should().Be("0/2800/2800/True");
        Bal("1112").Should().Be("0/0/0/False");

        // GET after posting: rows show the posted balances, 3900 carries the plug
        var read = await k.Ok(k.Get("/api/finance/opening-balances"), "posted opening balances");
        (read.B("isPosted"), read.B("isSkipped"), read.S("conversionDate"), read.IsNull("postedAt")).Should().Be((true, false, Day(Y, 1, 31), false));
        Row(read, "3900").D("creditAmount").Should().Be(2800m);

        // T-OB-05 — nothing changes after posting
        (await k.Batch((U("1112"), 1m, 0m))).ShouldFail(HttpStatusCode.Conflict, "edit after post", "Opening balances were already posted.");
        (await k.PostOpening(acknowledge: true)).ShouldFail(HttpStatusCode.Conflict, "post twice", "Opening balances were already posted.");
        (await k.PostOpening(skip: true)).ShouldFail(HttpStatusCode.Conflict, "skip after post", "Opening balances were already posted.");
        (await k.Delete($"{Chart}/{U("1111")}")).ShouldFail(HttpStatusCode.Conflict, "delete with transactions", "Cannot delete an account with posted transactions.");
        (await k.Post($"{Chart}/apply-template", new { templateUuid = (await k.TemplateAsync("US_GENERAL")).G("uuid") })).ShouldFail(HttpStatusCode.Conflict,
            "apply after posting", "The chart can only be replaced before the fiscal year is set and before opening balances are posted.");
        var yearRead = (await k.Ok(k.Get("/api/finance/fiscal-years"), "years")).Items().Single();
        yearRead.B("hasPostings").Should().BeTrue();
        (await k.Put($"/api/finance/fiscal-years/{yearRead.G("uuid")}", new { startDate = Day(Y, 1, 1), endDate = Day(Y, 11, 30) }))
            .ShouldFail(HttpStatusCode.Conflict, "dates with postings", "The dates cannot be changed: journal entries are posted in this fiscal year.");

        // D-57 — Finance's opening balance date is pinned to the posted date
        (await k.SetOpeningDate(Day(Y, 2, 28))).ShouldFail(HttpStatusCode.Conflict, "move the date",
            $"The opening balance date is locked by General ledger opening balances posted as at {Day(Y, 1, 31)}.");
        (await k.SetOpeningDate((string?)null)).ShouldFail(HttpStatusCode.Conflict, "clear the date", "locked by General ledger opening balances");
        await k.Ok(k.SetOpeningDate(Day(Y, 1, 31)), "re-saving the locked date is a no-op");

        var status = await k.StatusAsync();
        (status.S("status"), status.B("openingBalancesEntered"), status.B("openingBalancesBalanced"), status.S("conversionDate"))
            .Should().Be(("BalancesEntered", true, true, Day(Y, 1, 31)));
    }

    [Fact]
    public async Task A_balanced_opening_balance_posts_without_a_plug()
    {
        var o = await _root.OrgAsync("OBB");
        var k = o.K;
        var a = await k.ReadyForBalancesAsync();
        (await k.PostOpening()).ShouldFail(HttpStatusCode.BadRequest, "nothing entered", "Enter at least one opening balance, or skip this step.");

        await k.Ok(k.Batch((a["1113"].G("uuid"), 2500.12345m, 0m), (a["3100"].G("uuid"), 0m, 2500.12345m)), "balanced drafts");
        var summary = await k.Ok(k.Get("/api/finance/opening-balances/summary"), "summary");
        (summary.D("totalDebits"), summary.D("difference"), summary.B("isBalanced"), summary.IsNull("plugSide")).Should().Be((2500.1235m, 0m, true, true));

        // D-55 — two posts at once: the conditional UPDATE lets exactly one through, the other is a 409; one journal
        var both = await Task.WhenAll(k.PostOpening(), k.PostOpening());
        both.Select(r => r.Status).Should().BeEquivalentTo(new[] { HttpStatusCode.OK, HttpStatusCode.Conflict }, string.Join(" | ", both.Select(r => r.ToString())));
        var posted = both.Single(r => r.Status == HttpStatusCode.OK).Result;
        posted.S("journalEntryNumber").Should().StartWith("JE-");
        (await _f.RowCountAsync("SELECT COUNT(*) FROM finance.JournalEntries WHERE OrganizationId = @o", ("@o", o.OrgId))).Should().Be(1);
        (await k.Ok(k.Get(Chart + "/flat"), "flat")).Items().Single(x => x.S("accountCode") == "1113").D("currentBalance").Should().Be(2500.1235m, "applied once");

        var lines = await _f.QueryAsync("SELECT a.AccountCode FROM finance.JournalEntryLines l JOIN finance.ChartOfAccounts a ON a.Id = l.AccountId " +
                                        "WHERE l.OrganizationId = @o ORDER BY l.LineNumber", ("@o", o.OrgId));
        lines.Select(l => (string)l["AccountCode"]!).Should().Equal("1113", "3100");
        var after = await k.AccountsAsync();
        (after["3900"].D("currentBalance"), after["3900"].B("hasTransactions")).Should().Be((0m, false));
        after["1113"].D("currentBalance").Should().Be(2500.1235m);

        // T-OB subledger null-safety: no A40 documents → zeros, the defaults still named
        var sub = (await k.Ok(k.Get("/api/finance/opening-balances"), "read")).P("subledger");
        (sub.D("customerOpeningTotal"), sub.D("supplierOpeningTotal"), sub.D("openingStockValue"), sub.S("receivableAccountCode"), sub.ND("receivableAccountBalance"))
            .Should().Be((0m, 0m, 0m, "1121", 0m));
    }

    [Fact]
    public async Task Skipping_needs_no_drafts_date_or_year()
    {
        var o = await _root.OrgAsync("OBS");
        var k = o.K;
        await k.ApplyAsync();
        var cash = await k.AccountAsync("1111");

        await k.Ok(k.Batch((cash, 10m, 0m)), "a draft");
        (await k.PostOpening(skip: true)).ShouldFail(HttpStatusCode.Conflict, "skip with drafts",
            "Delete the draft opening balances before skipping, or post them instead.");
        await k.Ok(k.Batch((cash, 0m, 0m)), "a zero row deletes the draft");
        (await _f.RowCountAsync("SELECT COUNT(*) FROM finance.OpeningBalanceEntries WHERE OrganizationId = @o", ("@o", o.OrgId))).Should().Be(0);

        var skipped = await k.Ok(k.PostOpening(skip: true), "skip without a date or a fiscal year");
        (skipped.IsNull("journalEntryNumber"), skipped.P("summary").D("totalDebits")).Should().Be((true, 0m));
        (await _f.RowCountAsync("SELECT COUNT(*) FROM finance.JournalEntries WHERE OrganizationId = @o", ("@o", o.OrgId))).Should().Be(0, "a skip writes no journal");
        var read = await k.Ok(k.Get("/api/finance/opening-balances"), "read");
        (read.B("isPosted"), read.B("isSkipped"), read.IsNull("conversionDate")).Should().Be((true, true, true));
        var status = await k.StatusAsync();
        (status.B("openingBalancesEntered"), status.B("openingBalancesBalanced"), status.S("status")).Should().Be((true, true, "AccountsCreated"),
            "the flags are set; the status still waits for the fiscal year");
        (await k.PostOpening(skip: true)).ShouldFail(HttpStatusCode.Conflict, "skip twice", "Opening balances were already posted.");
        // skipped without a date: nothing pins Finance's date
        await k.Ok(k.SetOpeningDate(Day(Y, 3, 31)), "the date is free");
        await k.CreateYearOk(Y);
        (await k.StatusAsync()).S("status").Should().Be("BalancesEntered");
    }

    // ── T-SS ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_setup_runs_from_NotStarted_to_Complete_and_gates_only_the_dashboard()
    {
        var o = await _root.OrgAsync("SS");
        var k = o.K;

        var s0 = await k.StatusAsync();
        (s0.S("status"), s0.I("nextStep"), s0.I("accountsCreatedCount")).Should().Be(("NotStarted", 1, 0));
        s0.A("steps").Should().HaveCount(6).And.OnlyContain(x => !x.B("done"));

        // D-4 — the dashboard is gated; existing finance routes and every setup route are not
        var gate = await k.Get("/api/finance/gl/dashboard");
        gate.Status.Should().Be(HttpStatusCode.Forbidden, gate.ToString());
        (gate.Body.B("success"), gate.Body.S("error"), gate.Body.S("currentStatus"), gate.Body.S("redirectUrl"), gate.Body.IsNull("result"))
            .Should().Be((false, "FINANCE_SETUP_REQUIRED", "NotStarted", "/portal/pages/finance/gl/setup", true));
        gate.Message.Should().Be("The general ledger setup must be completed before using this feature.");
        foreach (var url in new[]
                 {
                     "/api/finance/invoices", "/api/finance/tax-codes", "/api/finance/opening-balance-date",
                     "/api/finance/templates", Chart, "/api/finance/fiscal-years", "/api/finance/accounting-defaults", "/api/finance/opening-balances",
                     "/api/finance/opening-balances/summary", "/api/finance/external-mappings", "/api/finance/setup/history", "/api/finance/setup/status"
                 })
        {
            var r = await k.Get(url);
            r.Status.Should().Be(HttpStatusCode.OK, $"{url} is not gated before Complete (D-4) — {r}");
            r.Raw.Should().NotContain("FINANCE_SETUP_REQUIRED", url);
        }
        (await k.Ok(k.Post("/api/finance/accounting-defaults/validate"), "validate is a read")).B("isValid").Should().BeFalse();

        var incomplete = await k.Post("/api/finance/setup/complete");
        incomplete.ShouldFail(HttpStatusCode.Conflict, "nothing done");
        incomplete.Result.A("unmet").Select(x => x.GetString()).Should().Equal(
            "Create the chart of accounts.", "Set up a fiscal year.", "Assign every required accounting default.", "Post or skip the opening balances.");

        // 1 → AccountsCreated
        (await k.ApplyAsync()).S("status").Should().Be("AccountsCreated");
        var a = await k.AccountsAsync();
        // hold step 4 back so step 3 shows on its own
        await k.Ok(k.SetDefault("DEFAULT_CASH", null), "unassign the cash default");
        (await k.StatusAsync()).S("status").Should().Be("AccountsCreated");
        // 2 → FiscalYearSet
        await k.CreateYearOk(Y);
        var s2 = await k.StatusAsync();
        (s2.S("status"), s2.B("fiscalYearConfigured"), s2.B("defaultsConfigured"), s2.I("nextStep")).Should().Be(("FiscalYearSet", true, false, 4));
        var validate = await k.Ok(k.Post("/api/finance/accounting-defaults/validate"), "validate");
        validate.A("missingRequired").Select(x => x.GetString()).Should().Equal("DEFAULT_CASH");
        // 3 → DefaultsSet
        await k.Ok(k.SetDefault("DEFAULT_CASH", a["1112"].G("uuid")), "assign cash");
        (await k.StatusAsync()).S("status").Should().Be("DefaultsSet");
        // 4 → BalancesEntered
        await k.Ok(k.SetOpeningDate(Day(Y, 1, 31)), "date");
        await k.Ok(k.Batch((a["1113"].G("uuid"), 750m, 0m), (a["3100"].G("uuid"), 0m, 500m), (a["2111"].G("uuid"), 0m, 250m)), "balanced");
        await k.Ok(k.PostOpening(), "post");
        (await k.StatusAsync()).S("status").Should().Be("BalancesEntered");
        var stillGated = await k.Get("/api/finance/gl/dashboard");
        (stillGated.Status, stillGated.Body.S("currentStatus")).Should().Be((HttpStatusCode.Forbidden, "BalancesEntered"));

        // 5 → Complete
        var done = await k.Ok(k.Post("/api/finance/setup/complete"), "complete");
        (done.S("status"), done.IsNull("setupCompletedAt"), done.I("nextStep")).Should().Be(("Complete", false, 6));
        done.A("steps").Should().OnlyContain(x => x.B("done"));
        (await k.Ok(k.Post("/api/finance/setup/complete"), "complete again")).S("status").Should().Be("Complete");

        // the dashboard opens with the totals
        var dash = await k.Ok(k.Get("/api/finance/gl/dashboard"), "dashboard after Complete");
        (dash.D("totalAssets"), dash.D("totalLiabilities"), dash.D("totalEquity"), dash.D("totalRevenue"), dash.D("totalExpenses"), dash.S("currencyCode"))
            .Should().Be((750m, 250m, 500m, 0m, 0m, "PKR"));
        dash.I("accountCount").Should().Be(a.Count);
        dash.P("fiscalYear").S("name").Should().Be($"FY {Y}");

        // Complete locks: required defaults cannot be cleared, roots cannot be renamed; re-pointing stays allowed
        (await k.SetDefault("DEFAULT_CASH", null)).ShouldFail(HttpStatusCode.BadRequest, "clear after complete",
            "A required default cannot be cleared after the setup is complete.");
        await k.Ok(k.SetDefault("DEFAULT_CASH", a["1111"].G("uuid")), "re-point after complete");
        (await k.Put($"{Chart}/{a["1"].G("uuid")}", new { accountName = "All assets" })).ShouldFail(HttpStatusCode.Conflict, "rename root",
            "Root accounts are locked once the setup is complete.");
        await k.Ok(k.CreateAccount(a["111"].G("uuid"), "1116", "Added after complete", "Cash"), "account CRUD stays allowed");

        // history, newest first
        var history = (await k.Ok(k.Get("/api/finance/setup/history"), "history")).Items();
        history.Select(h => h.P("at").GetDateTime()).Should().BeInDescendingOrder();
        history.Select(h => h.S("eventType")).Should().Contain(new[]
        {
            "CoaTemplateAppliedEvent", "FiscalYearCreatedEvent", "CoaSetupStatusChangedEvent", "OpeningBalancePostedEvent", "CoaSetupCompletedEvent",
            "AccountingDefaultAssignedEvent", "ChartOfAccountCreatedEvent"
        });
        history.Where(h => h.S("eventType") == "CoaSetupStatusChangedEvent").Should().HaveCountGreaterThanOrEqualTo(5, "one per step");

        // T-SS reset after only the opening balance journal: back to NotStarted, the date lock released
        (await k.SetOpeningDate(Day(Y, 2, 28))).Status.Should().Be(HttpStatusCode.Conflict, "locked before the reset");
        var reset = await k.Ok(k.Post("/api/finance/setup/reset"), "reset");
        (reset.S("status"), reset.I("accountsCreatedCount"), reset.B("openingBalancesEntered"), reset.IsNull("conversionDate"), reset.IsNull("setupCompletedAt"))
            .Should().Be(("NotStarted", 0, false, true, true));
        foreach (var table in new[] { "ChartOfAccounts", "FiscalYears", "FiscalPeriods", "JournalEntries", "JournalEntryLines", "OpeningBalanceEntries", "ExternalSystemMappings" })
            (await _f.RowCountAsync($"SELECT COUNT(*) FROM finance.{table} WHERE OrganizationId = @o", ("@o", o.OrgId))).Should().Be(0, table);
        (await _f.RowCountAsync("SELECT COUNT(*) FROM finance.AccountingDefaults WHERE OrganizationId = @o AND AccountId IS NULL", ("@o", o.OrgId)))
            .Should().Be(16, "the default rows stay, unassigned");
        await k.Ok(k.SetOpeningDate(Day(Y, 2, 28)), "the date lock is released");
        (await k.Get("/api/finance/gl/dashboard")).Body.S("currentStatus").Should().Be("NotStarted");
        (await k.Ok(k.Get("/api/finance/setup/history"), "history")).Items().First().S("summary").Should().Contain("Setup reset");
        (await k.ApplyAsync()).S("status").Should().Be("AccountsCreated", "a fresh start is possible");
    }

    [Fact]
    public async Task Posted_non_opening_journals_block_reset()
    {
        var o = await _root.OrgAsync("RST");
        var k = o.K;
        await k.ApplyAsync();
        await k.CreateYearOk(Y);
        await _f.InsertManualJournalAsync(o.OrgId);

        (await k.Post("/api/finance/setup/reset")).ShouldFail(HttpStatusCode.Conflict, "a manual journal exists", "Cannot reset after journal entries posted.");
        (await _f.RowCountAsync("SELECT COUNT(*) FROM finance.ChartOfAccounts WHERE OrganizationId = @o", ("@o", o.OrgId))).Should().BeGreaterThan(100, "nothing was deleted");
        (await k.StatusAsync()).S("status").Should().Be("DefaultsSet");
    }
}
