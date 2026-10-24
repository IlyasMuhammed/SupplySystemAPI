using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.GeneralLedger;
using SMS.Integration.Tests.SapAlignment;
using Xunit;
using static SMS.Integration.Tests.JournalEngine.Je;

namespace SMS.Integration.Tests.JournalEngine;

/// <summary>
/// A47 (QA) — the journal engine's business rules over HTTP on the real host (LocalDB), tasks 440–456 (E2E side):
/// BR-JE-001..013 / TS-JE-001..021, TS-SQ-001..005, BR-RC-* / TS-RC-001..007, BR-PC-* / TS-PC-001..005, BR-TB-* / TS-TB-001..006.
/// Texts and codes are API-CONTRACT.md's. <para>Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~JournalEngine.JeRulesE2ETests</c>.</para>
/// </summary>
public sealed class JeRulesE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;

    public JeRulesE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _f.ShouldBeOnLocalDb();
        _root = new SapKit(factory, "A47");
    }

    private static DateTime D(int month, int day) => new(Y, month, day);

    [Fact]
    public async Task BR_JE_001_to_003_line_shape_rules()
    {
        var o = await _root.NewJeOrgAsync("JR1");
        var k = o.K;
        var misc = await k.TypeAsync("MISC");
        Guid cash = o.Acc("DEFAULT_CASH"), rev = o.Acc("DEFAULT_REVENUE");

        // TS-JE-001 — balanced, two lines: Draft, numbered, totals
        var ok = await k.CreateEntryOk(Body(misc, Today, [new JeLine(cash, 1000m, 0m), new JeLine(rev, 0m, 1000m)]));
        (ok.S("status"), ok.D("totalDebit"), ok.D("totalCredit"), ok.I("lineCount")).Should().Be(("Draft", 1000m, 1000m, 2), J.Short(ok));
        ok.S("entryNumber").Should().Be($"JV/{Y}/0001", "§8 / D-7: {prefix}/{fiscal year start}/{seq:D4}, MISC prefix JV");
        ok.P("journalType").S("code").Should().Be("MISC");
        ok.P("fiscalPeriod").I("periodNumber").Should().Be(Today.Month);
        ok.B("isManual").Should().BeTrue();
        ok.A("lines").Should().OnlyContain(l => l.Has("lineUuid") && !l.IsNull("lineUuid"), "contract §2: lines carry lineUuid");
        ok.A("lines").Select(l => l.D("amountResidual")).Should().Equal(1000m, -1000m);

        // TS-JE-002 / BR-JE-001 — unbalanced
        (await k.CreateEntry(Body(misc, Today, [new JeLine(cash, 1000m, 0m), new JeLine(rev, 0m, 999m)])))
            .ShouldBeInvalid("JE-001", "Entry is not balanced", "TS-JE-002");
        // REV-2 (CORE fix): base totals must balance exactly — a 4-dp difference under 0.01 is refused too
        (await k.CreateEntry(Body(misc, Today, [new JeLine(cash, 100.004m, 0m), new JeLine(rev, 0m, 100m)])))
            .ShouldBeInvalid("JE-001", "Entry is not balanced", "REV-2: no base difference accepted");
        // 4-dp amounts that do balance are fine
        (await k.CreateEntry(Body(misc, Today, [new JeLine(cash, 100.0049m, 0m), new JeLine(rev, 0m, 100.0049m)]))).Status
            .Should().Be(HttpStatusCode.OK, "exactly balanced 4-dp base amounts");

        // TS-JE-012 / BR-JE-002 — one line
        (await k.CreateEntry(Body(misc, Today, [new JeLine(cash, 0m, 0m)])))
            .ShouldBeInvalid("JE-002", "Minimum 2 lines required", "TS-JE-012");
        (await k.CreateEntry(Body(misc, Today, [])))
            .ShouldBeInvalid("JE-002", "Minimum 2 lines required", "no lines");

        // TS-JE-003 / BR-JE-003 — both sides on one line; neither side; negative
        (await k.CreateEntry(Body(misc, Today, [new JeLine(cash, 500m, 500m), new JeLine(rev, 0m, 0m)])))
            .ShouldBeInvalid("JE-003", "Line cannot have both debit and credit", "TS-JE-003");
        (await k.CreateEntry(Body(misc, Today, [new JeLine(cash, 10m, 0m), new JeLine(rev, 0m, 0m), new JeLine(rev, 0m, 10m)])))
            .ShouldBeInvalid("JE-004", "Line must have a debit or a credit amount", "a zero line");
        (await k.CreateEntry(Body(misc, Today, [new JeLine(cash, -10m, 0m), new JeLine(rev, -10m, 0m)])))
            .ShouldBeInvalid("JE-004", "Amounts cannot be negative", "negative amounts");

        // contract §0: line errors are prefixed in message only
        var line = await k.CreateEntry(Body(misc, Today, [new JeLine(cash, 10m, 0m), new JeLine(rev, 5m, 5m)]));
        line.Message.Should().StartWith("Line 2: ", line.ToString());
        line.Result.A("errors").Should().Contain(e => e.S("message") == "Line cannot have both debit and credit" && e.I("lineNumber") == 2, line.ToString());

        // nothing refused was written
        (await k.ListEntriesAsync()).Should().HaveCount(2, "only the two valid drafts");
    }

    [Fact]
    public async Task BR_JE_004_005_period_rules()
    {
        var o = await _root.NewJeOrgAsync("JR2");
        var k = o.K;
        var misc = await k.TypeAsync("MISC");
        Guid cash = o.Acc("DEFAULT_CASH"), rev = o.Acc("DEFAULT_REVENUE");
        JeLine[] lines = [new JeLine(cash, 100m, 0m), new JeLine(rev, 0m, 100m)];

        var jan = (await k.PeriodAsync(D(1, 15))).G("uuid");
        var feb = (await k.PeriodAsync(D(2, 15))).G("uuid");
        var mar = (await k.PeriodAsync(D(3, 15))).G("uuid");
        await k.Ok(k.LockPeriod(jan), "lock January");
        await k.Ok(k.FinalClosePeriod(jan), "close January");
        await k.Ok(k.LockPeriod(feb), "lock February");
        await k.Ok(k.AdjustmentOnlyPeriod(mar), "March adjustment-only");

        (await k.CreateEntry(Body(misc, D(1, 15), lines))).ShouldBeInvalid("JE-006", "Period is closed", "TS-JE-004");
        (await k.CreateEntry(Body(misc, D(2, 15), lines))).ShouldBeInvalid("JE-007", "Period is locked", "TS-JE-005");
        (await k.CreateEntry(Body(misc, D(3, 15), lines))).ShouldBeInvalid("JE-008", "Period accepts only adjustment entries", "TS-JE-006");

        // TS-JE-007 — a closing (adjustment) entry is accepted by an AdjustmentOnly period; so is an opening one
        var closing = await k.CreateEntryOk(Body(misc, D(3, 15), lines, closing: true), "closing entry in AdjustmentOnly");
        (closing.S("status"), closing.B("isClosingEntry")).Should().Be(("Draft", true));
        (await k.CreateEntry(Body(misc, D(3, 16), lines, opening: true))).Status.Should().Be(HttpStatusCode.OK, "opening entry in AdjustmentOnly");
        (await k.CreateEntry(Body(misc, D(3, 16), lines, opening: true, closing: true)))
            .ShouldBeInvalid("JE-013", "An entry cannot be both an opening and a closing entry", "both flags");

        // TS-JE-013 — no fiscal year covers the date
        (await k.CreateEntry(Body(misc, new DateTime(Y + 5, 1, 15), lines))).ShouldBeInvalid("JE-005", "No fiscal period covers this date", "TS-JE-013");
        (await k.CreateEntry(Body(misc, new DateTime(Y - 5, 6, 1), lines))).ShouldBeInvalid("JE-005", "No fiscal period covers this date", "before every year");

        // the Closing journal type forces isClosingEntry
        var closeType = await k.TypeAsync("CLOSE");
        var viaType = await k.CreateEntryOk(Body(closeType, D(3, 20), lines), "CLOSE type in AdjustmentOnly (auto-post)");
        viaType.B("isClosingEntry").Should().BeTrue("contract §2: the Closing type forces isClosingEntry");
        viaType.S("status").Should().Be("Posted", "CLOSE is an auto-post type");

        // an Open period takes everything
        (await k.CreateEntry(Body(misc, Today, lines))).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task BR_JE_006_account_must_be_a_leaf_and_active()
    {
        var o = await _root.NewJeOrgAsync("JR3");
        var k = o.K;
        var misc = await k.TypeAsync("MISC");
        var cash = o.Acc("DEFAULT_CASH");

        // TS-JE-009 — a Level 3 (group) account
        var group = o.Accounts.Values.First(a => a.I("level") == 3 && a.B("isGroup") && a.B("isActive"));
        (await k.CreateEntry(Body(misc, Today, [new JeLine(cash, 50m, 0m), new JeLine(group.G("uuid"), 0m, 50m)])))
            .ShouldBeInvalid("JE-009", "Account is not postable", "TS-JE-009");

        // TS-JE-010 — an inactive leaf (a new unused leaf, deleted = soft-deactivated)
        var cashCode = o.Code("DEFAULT_CASH");
        var leaf = await k.NewLeafAsync(cashCode, cashCode + "9", "A47 QA inactive leaf");
        await k.Ok(k.Delete($"{Gl.Chart}/{leaf.G("uuid")}"), "deactivate the leaf");
        (await k.AccountsAsync())[cashCode + "9"].B("isActive").Should().BeFalse();
        (await k.CreateEntry(Body(misc, Today, [new JeLine(cash, 50m, 0m), new JeLine(leaf.G("uuid"), 0m, 50m)])))
            .ShouldBeInvalid("JE-010", "Account is inactive", "TS-JE-010");

        // an unknown account uuid
        (await k.CreateEntry(Body(misc, Today, [new JeLine(cash, 50m, 0m), new JeLine(Guid.NewGuid(), 0m, 50m)])))
            .ShouldBeInvalid("JE-009", "Account not found", "unknown account");

        // TS-JE-012-ish — inactive / unknown journal type
        (await k.CreateEntry(Body(Guid.NewGuid(), Today, [new JeLine(cash, 50m, 0m), new JeLine(o.Acc("DEFAULT_REVENUE"), 0m, 50m)])))
            .ShouldBeInvalid("JE-013", "Journal type not found", "unknown journal type");
        var custom = await k.Ok(k.Post(Types, new { code = "QAJ", name = "QA journal", type = "Miscellaneous" }), "custom journal type");
        await k.Ok(k.Patch($"{Types}/{custom.G("uuid")}/deactivate", null), "deactivate it");
        (await k.CreateEntry(Body(custom.G("uuid"), Today, [new JeLine(cash, 50m, 0m), new JeLine(o.Acc("DEFAULT_REVENUE"), 0m, 50m)])))
            .ShouldBeInvalid("JE-013", "Journal type is inactive", "inactive journal type");
    }

    [Fact]
    public async Task BR_JE_007_same_client_request_id_returns_the_existing_entry()
    {
        var o = await _root.NewJeOrgAsync("JR4");
        var k = o.K;
        var misc = await k.TypeAsync("MISC");
        var id = Guid.NewGuid();
        JeLine[] lines = [new JeLine(o.Acc("DEFAULT_CASH"), 250m, 0m), new JeLine(o.Acc("DEFAULT_REVENUE"), 0m, 250m)];

        var first = await k.CreateEntryOk(Body(misc, Today, lines, clientRequestId: id));
        var again = await k.CreateEntryOk(Body(misc, Today, lines, clientRequestId: id), "TS-JE-008 retry");
        again.G("uuid").Should().Be(first.G("uuid"), "TS-JE-008: the same key returns the existing entry");
        again.S("entryNumber").Should().Be(first.S("entryNumber"));
        first.S("idempotencyKey").Should().Be($"manual:{id}");

        // parallel duplicates still give one entry
        var tasks = Enumerable.Range(0, 5).Select(_ => k.CreateEntry(Body(misc, Today, lines, clientRequestId: id))).ToList();
        var answers = await Task.WhenAll(tasks);
        answers.Should().OnlyContain(a => a.Status == HttpStatusCode.OK, string.Join("\n", answers.Select(a => a.ToString())));
        answers.Select(a => a.Result.G("uuid")).Distinct().Should().ContainSingle();
        (await _f.RowCountAsync("SELECT COUNT(*) FROM finance.JournalEntries WHERE OrganizationId = @o AND IdempotencyKey = @k",
            ("@o", o.OrgId), ("@k", $"manual:{id}"))).Should().Be(1);

        // without an id every call is a new entry
        var n1 = await k.CreateEntryOk(Body(misc, Today, lines));
        var n2 = await k.CreateEntryOk(Body(misc, Today, lines));
        n1.G("uuid").Should().NotBe(n2.G("uuid"));
    }

    [Fact]
    public async Task BR_JE_008_to_010_posting_and_immutability()
    {
        var o = await _root.NewJeOrgAsync("JR5");
        var k = o.K;
        var misc = await k.TypeAsync("MISC");
        Guid cash = o.Acc("DEFAULT_CASH"), rev = o.Acc("DEFAULT_REVENUE");
        string cashCode = o.Code("DEFAULT_CASH"), revCode = o.Code("DEFAULT_REVENUE");
        var cashBefore = await k.CurrentBalanceAsync(cashCode);
        var revBefore = await k.CurrentBalanceAsync(revCode);

        var draft = await o.DraftAsync(misc, cash, rev, 1000m);
        var uuid = draft.G("uuid");
        var number = draft.S("entryNumber");
        (await k.CurrentBalanceAsync(cashCode)).Should().Be(cashBefore, "a Draft moves no balance");

        // BR-JE-010 — editing a draft keeps its number
        var edited = await k.Ok(k.UpdateEntry(uuid, new
        {
            entryDate = TodayIso, description = "A47 QA edited",
            lines = new[] { new { accountUuid = cash, debit = 1200m, credit = 0m }, new { accountUuid = rev, debit = 0m, credit = 1200m } }
        }), "edit the draft");
        (edited.S("entryNumber"), edited.D("totalDebit"), edited.S("description")).Should().Be((number, 1200m, "A47 QA edited"));
        (await k.UpdateEntry(uuid, new { entryDate = Iso(new DateTime(Y + 1, 1, 5)), lines = new[] { new { accountUuid = cash, debit = 1m, credit = 0m }, new { accountUuid = rev, debit = 0m, credit = 1m } } }))
            .Status.Should().Be(HttpStatusCode.BadRequest, "contract §2: the date must stay in the number's fiscal year");

        // TS-JE-016 / 020 / 021 — post
        var posted = await k.Ok(k.PostEntry(uuid), "TS-JE-016 post");
        posted.S("status").Should().Be("Posted");
        posted.IsNull("postedAt").Should().BeFalse();
        posted.S("entryNumber").Should().Be(number, "BR-JE-010");
        var chart = await k.AccountsAsync();
        chart[cashCode].D("currentBalance").Should().Be(cashBefore + 1200m, "TS-JE-020: debit-normal asset up on a debit");
        chart[revCode].D("currentBalance").Should().Be(revBefore + 1200m, "TS-JE-020: credit-normal revenue up on a credit (A-10)");
        chart[cashCode].B("hasTransactions").Should().BeTrue("TS-JE-021");
        chart[revCode].B("hasTransactions").Should().BeTrue("TS-JE-021");

        // TS-JE-017 / BR-JE-009 — post again
        (await k.PostEntry(uuid)).ShouldBeRefusedState("JE-014", "Entry is already posted", "TS-JE-017");
        // BR-JE-008 — posted is immutable
        (await k.UpdateEntry(uuid, new { entryDate = TodayIso, lines = new[] { new { accountUuid = cash, debit = 1m, credit = 0m }, new { accountUuid = rev, debit = 0m, credit = 1m } } }))
            .ShouldBeRefusedState("JE-014", "Posted entries cannot be modified", "BR-JE-008 edit");
        (await k.DeleteEntry(uuid)).ShouldBeRefusedState("JE-014", "Posted entries cannot be deleted", "BR-JE-008 delete");
        (await k.CurrentBalanceAsync(cashCode)).Should().Be(cashBefore + 1200m, "refusals moved nothing");

        // TS-JE-019 — the period was locked between creation and posting (simulated race: SQL, since locking with drafts is refused)
        var apr = await k.PeriodAsync(D(4, 10));
        var aprDraft = await o.DraftAsync(misc, cash, rev, 10m, D(4, 10));
        await _f.ExecuteAsync("UPDATE finance.FiscalPeriods SET Status = 'Locked' WHERE Uuid = @p", ("@p", apr.G("uuid")));
        (await k.PostEntry(aprDraft.G("uuid"))).ShouldBeInvalid("JE-007", "Period is locked", "TS-JE-019");
        (await k.EntryAsync(aprDraft.G("uuid"))).S("status").Should().Be("Draft");

        // delete a draft
        var gone = await o.DraftAsync(misc, cash, rev, 5m);
        await k.Ok(k.DeleteEntry(gone.G("uuid")), "delete a draft");
        (await k.GetEntry(gone.G("uuid"))).Status.Should().Be(HttpStatusCode.NotFound);

        // PostImmediately — one call to Posted
        var direct = await k.CreateEntryOk(Body(misc, Today, [new JeLine(cash, 7m, 0m), new JeLine(rev, 0m, 7m)], post: true), "postImmediately");
        direct.S("status").Should().Be("Posted");

        // list filters and validation texts
        (await k.ListEntriesAsync("status=Posted&pageSize=200")).Should().OnlyContain(e => e.S("status") == "Posted").And.HaveCount(2);
        (await k.ListEntriesAsync($"search={Uri.EscapeDataString(number!)}")).Should().ContainSingle(e => e.G("uuid") == uuid);
        (await k.ListEntries("status=Nope")).ShouldBeInvalid(null, "Status must be Draft, Posted or Reversed.", "bad status filter");
        (await k.ListEntries("sortBy=nope")).ShouldBeInvalid(null, "Sort by entryDate, entryNumber, totalDebit, createdAt or postedAt.", "bad sort");
        (await k.ListEntries($"fromDate={TodayIso}&toDate={Iso(Today.AddDays(-1))}")).ShouldBeInvalid(null, "The to date must not be before the from date.", "bad range");
        (await k.GetEntry(Guid.NewGuid())).ShouldRefuse(HttpStatusCode.NotFound, null, "Journal entry not found.", "unknown uuid");
    }

    [Fact]
    public async Task BR_JE_011_sale_type_requires_a_partner()
    {
        var o = await _root.NewJeOrgAsync("JR6");
        var k = o.K;
        var sale = await k.TypeAsync("SALE");
        var purch = await k.TypeAsync("PURCH");
        Guid ar = o.Acc("DEFAULT_RECEIVABLE"), rev = o.Acc("DEFAULT_REVENUE"), ap = o.Acc("DEFAULT_PAYABLE"), exp = o.Acc("DEFAULT_PURCHASE");
        var customer = await k.CreateCustomerAsync("JR6 Customer");
        var vendor = await k.CreateVendorAsync("JR6 Vendor");

        (await k.CreateEntry(Body(sale, Today, [new JeLine(ar, 100m, 0m), new JeLine(rev, 0m, 100m)])))
            .ShouldBeInvalid("JE-011", "Sales entries require a partner", "TS-JE-011");
        (await k.CreateEntry(Body(sale, Today, [new JeLine(ar, 100m, 0m, customer.Uuid, "Customer"), new JeLine(rev, 0m, 100m)])))
            .ShouldBeInvalid("JE-011", "Sales entries require a partner", "BR-JE-011: every line");
        (await k.CreateEntry(Body(purch, Today, [new JeLine(exp, 100m, 0m), new JeLine(ap, 0m, 100m)])))
            .ShouldBeInvalid("JE-011", "Purchase entries require a partner", "PURCH requires a partner");
        (await k.CreateEntry(Body(sale, Today, [new JeLine(ar, 100m, 0m, customer.Uuid, "Supplier"), new JeLine(rev, 0m, 100m, customer.Uuid, "Customer")])))
            .ShouldBeInvalid("JE-011", "Partner type must be Customer or Vendor", "bad partner type");
        (await k.CreateEntry(Body(sale, Today, [new JeLine(ar, 100m, 0m, Guid.NewGuid(), "Customer"), new JeLine(rev, 0m, 100m, Guid.NewGuid(), "Customer")])))
            .ShouldBeInvalid("JE-011", "Partner not found", "unknown partner");

        var ok = await k.CreateEntryOk(Body(sale, Today,
            [new JeLine(ar, 100m, 0m, customer.Uuid, "Customer"), new JeLine(rev, 0m, 100m, customer.Uuid, "Customer")]), "SALE with partners");
        ok.S("entryNumber").Should().Be($"SALE/{Y}/0001");
        ok.A("lines").Should().OnlyContain(l => l.NG("partnerUuid") == customer.Uuid && l.S("partnerType") == "Customer" && !string.IsNullOrEmpty(l.S("partnerName")));
        var bill = await k.CreateEntryOk(Body(purch, Today,
            [new JeLine(exp, 70m, 0m, vendor.Uuid, "Vendor"), new JeLine(ap, 0m, 70m, vendor.Uuid, "Vendor")]), "PURCH with partners");
        bill.S("entryNumber").Should().Be($"PURCH/{Y}/0001", "TS-SQ-003: own sequence");

        // the list filters by partner
        (await k.ListEntriesAsync($"partnerUuid={customer.Uuid}")).Should().ContainSingle(e => e.G("uuid") == ok.G("uuid"));
    }

    [Fact]
    public async Task Sequences_are_per_type_and_never_reused()
    {
        var o = await _root.NewJeOrgAsync("JR7");
        var k = o.K;
        var cust = await k.CreateCustomerAsync("JR7 Customer");
        var vend = await k.CreateVendorAsync("JR7 Vendor");
        Guid sale = await k.TypeAsync("SALE"), purch = await k.TypeAsync("PURCH"), misc = await k.TypeAsync("MISC"), bank = await k.TypeAsync("BANK");
        Guid ar = o.Acc("DEFAULT_RECEIVABLE"), rev = o.Acc("DEFAULT_REVENUE"), ap = o.Acc("DEFAULT_PAYABLE"), exp = o.Acc("DEFAULT_PURCHASE"),
            cash = o.Acc("DEFAULT_CASH"), bk = o.Acc("DEFAULT_BANK");

        async Task<string> SaleAsync() => (await k.CreateEntryOk(Body(sale, Today,
            [new JeLine(ar, 10m, 0m, cust.Uuid, "Customer"), new JeLine(rev, 0m, 10m, cust.Uuid, "Customer")]))).S("entryNumber")!;

        (await SaleAsync()).Should().Be($"SALE/{Y}/0001", "TS-SQ-001");
        (await SaleAsync()).Should().Be($"SALE/{Y}/0002", "TS-SQ-002");
        (await k.CreateEntryOk(Body(purch, Today, [new JeLine(exp, 10m, 0m, vend.Uuid, "Vendor"), new JeLine(ap, 0m, 10m, vend.Uuid, "Vendor")])))
            .S("entryNumber").Should().Be($"PURCH/{Y}/0001", "TS-SQ-003");
        (await k.CreateEntryOk(Body(bank, Today, [new JeLine(bk, 10m, 0m), new JeLine(cash, 0m, 10m)]))).S("entryNumber").Should().Be($"BANK/{Y}/0001");

        // TS-SQ-005 — a deleted draft's number is not reused
        var d1 = await o.DraftAsync(misc, cash, rev, 1m);
        d1.S("entryNumber").Should().Be($"JV/{Y}/0001");
        await k.Ok(k.DeleteEntry(d1.G("uuid")), "delete the draft");
        (await o.DraftAsync(misc, cash, rev, 1m)).S("entryNumber").Should().Be($"JV/{Y}/0002", "TS-SQ-005: no gap backfill");

        // TS-SQ-004 — concurrent creation: unique numbers
        var parallel = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => k.CreateEntry(Body(misc, Today, [new JeLine(cash, 3m, 0m), new JeLine(rev, 0m, 3m)]))));
        parallel.Should().OnlyContain(a => a.Status == HttpStatusCode.OK, string.Join("\n", parallel.Select(a => a.ToString())));
        var numbers = parallel.Select(a => a.Result.S("entryNumber")!).ToList();
        numbers.Should().OnlyHaveUniqueItems("TS-SQ-004");
        numbers.Select(n => int.Parse(n.Split('/')[2])).OrderBy(n => n).Should().Equal(Enumerable.Range(3, 8), "consecutive after 0002");
    }

    [Fact]
    public async Task BR_RC_reconciliation_rules()
    {
        var o = await _root.NewJeOrgAsync("JR8");
        var k = o.K;
        var misc = await k.TypeAsync("MISC");
        Guid ar = o.Acc("DEFAULT_RECEIVABLE"), rev = o.Acc("DEFAULT_REVENUE"), cash = o.Acc("DEFAULT_CASH");

        Guid ArLine(JsonElement entry) => entry.LinesOn(ar).Single().G("lineUuid");
        async Task<Guid> InvoiceAsync(decimal amount) => ArLine(await o.PostedAsync(misc, ar, rev, amount));
        async Task<Guid> PaymentAsync(decimal amount) => ArLine(await o.PostedAsync(misc, cash, ar, amount));

        // TS-RC-001 — full
        var inv1 = await InvoiceAsync(1000m);
        var pay1 = await PaymentAsync(1000m);
        var open = await k.OpenItemsAsync(ar);
        open.Select(i => i.G("lineUuid")).Should().Contain([inv1, pay1]);
        var full = await k.Ok(k.Reconcile([inv1, pay1]), "TS-RC-001 reconcile");
        (full.S("reconcileType"), full.D("amount"), full.D("residual")).Should().Be(("Full", 1000m, 0m), J.Short(full));
        full.S("groupNumber").Should().MatchRegex($"^REC/{Y}/\\d{{4}}$");
        full.A("lines").Should().HaveCount(2).And.OnlyContain(l => l.D("amountResidual") == 0m);
        (await k.OpenItemsAsync(ar)).Select(i => i.G("lineUuid")).Should().NotContain([inv1, pay1], "BR-RC-006: fully reconciled lines leave the open items");

        // TS-RC-002 — partial: invoice keeps 400, payment 0
        var inv2 = await InvoiceAsync(1000m);
        var pay2 = await PaymentAsync(600m);
        var partial = await k.Ok(k.Reconcile([inv2, pay2]), "TS-RC-002");
        (partial.S("reconcileType"), partial.D("amount")).Should().Be(("Partial", 600m), J.Short(partial));
        partial.A("lines").Single(l => l.G("lineUuid") == inv2).D("amountResidual").Should().Be(400m);
        partial.A("lines").Single(l => l.G("lineUuid") == pay2).D("amountResidual").Should().Be(0m);
        var stillOpen = (await k.OpenItemsAsync(ar)).Single(i => i.G("lineUuid") == inv2);
        (stillOpen.D("amountResidual"), stillOpen.NG("reconcileGroupUuid")).Should().Be((400m, partial.G("uuid")), "open with its partial group");

        // BR-RC-008 — a line in an active group cannot join a second one
        var pay3 = await PaymentAsync(400m);
        (await k.Reconcile([inv2, pay3])).ShouldBeRefusedState("RC-004", "unreconcile it first", "BR-RC-008");

        // TS-RC-003 — three invoices, one payment
        var a = await InvoiceAsync(1000m);
        var b = await InvoiceAsync(500m);
        var c = await InvoiceAsync(300m);
        var p = await PaymentAsync(1800m);
        var four = await k.Ok(k.Reconcile([a, b, c, p]), "TS-RC-003");
        (four.S("reconcileType"), four.A("lines").Count, four.D("amount")).Should().Be(("Full", 4, 1800m));

        // TS-RC-004 — unreconcile restores residuals and deletes the group
        var undone = await k.Ok(k.Unreconcile(four.G("uuid")), "TS-RC-004");
        undone.I("linesRestored").Should().Be(4);
        (await k.ReconGroup(four.G("uuid"))).ShouldRefuse(HttpStatusCode.NotFound, null, "Reconciliation not found", "group deleted (C-4: 404s carry no code)");
        var restored = (await k.OpenItemsAsync(ar)).Where(i => new[] { a, b, c, p }.Contains(i.G("lineUuid"))).ToList();
        restored.Should().HaveCount(4);
        restored.Single(i => i.G("lineUuid") == a).D("amountResidual").Should().Be(1000m, "BR-RC-007");
        restored.Single(i => i.G("lineUuid") == p).D("amountResidual").Should().Be(-1800m);

        // TS-RC-005 — different accounts
        var cashLine = (await o.PostedAsync(misc, cash, rev, 50m)).LinesOn(cash).Single().G("lineUuid");
        (await k.Reconcile([a, cashLine])).ShouldBeRefusedState("RC-002", "All lines must belong to the same account", "TS-RC-005");

        // TS-RC-006 — a draft's line
        var draftLine = (await o.DraftAsync(misc, cash, ar, 1000m)).LinesOn(ar).Single().G("lineUuid");
        (await k.Reconcile([a, draftLine])).ShouldBeRefusedState("RC-001", "Only lines from posted entries can be reconciled", "TS-RC-006");

        // BR-RC-003 — two lines minimum; unknown line
        (await k.Reconcile([a])).ShouldBeInvalid("RC-003", "Minimum 2 lines required", "BR-RC-003");
        (await k.Reconcile([a, Guid.NewGuid()])).ShouldRefuse(HttpStatusCode.NotFound, null, "Journal line not found", "unknown line (C-4)");

        // TS-RC-007 — suggest for an invoice line of 5000: 3000 + 2000 credits first
        var big = await InvoiceAsync(5000m);
        var c3000 = await PaymentAsync(3000m);
        var c2000 = await PaymentAsync(2000m);
        await PaymentAsync(700m);
        var suggestions = Rows(await k.Ok(k.Suggest(ar, [big]), "TS-RC-007 suggest"));
        suggestions.Should().NotBeEmpty();
        var best = suggestions.First();
        best.B("exact").Should().BeTrue(J.Short(best));
        best.A("lineUuids").Select(x => x.GetGuid()).Should().Contain([c3000, c2000]);
        best.D("residualAfter").Should().Be(0m);
        (await k.Get($"{Recon}/open-items")).ShouldBeInvalid("RC-008", "Choose an account", "accountUuid required");
    }

    [Fact]
    public async Task BR_PC_period_close_rules()
    {
        var o = await _root.NewJeOrgAsync("JR9");
        var k = o.K;
        var misc = await k.TypeAsync("MISC");
        Guid cash = o.Acc("DEFAULT_CASH"), rev = o.Acc("DEFAULT_REVENUE");
        var year = (await k.YearAsync()).G("uuid");
        var may = (await k.PeriodAsync(D(5, 1))).G("uuid");

        // TS-PC-002 / BR-PC-001 — two drafts in May
        var d1 = await o.DraftAsync(misc, cash, rev, 10m, D(5, 3));
        var d2 = await o.DraftAsync(misc, cash, rev, 20m, D(5, 4));
        await o.PostedAsync(misc, cash, rev, 30m, D(5, 5));
        (await k.LockPeriod(may)).ShouldBeRefusedState("PC-001", "Period has 2 unposted draft entries", "TS-PC-002");
        (await k.AdjustmentOnlyPeriod(may)).ShouldBeRefusedState("PC-001", "Period has 2 unposted draft entries", "AdjustmentOnly with drafts");
        var overview = await k.Ok(k.CloseStatus(year), "period-close status");
        var mayRow = overview.A("periods").Single(p => p.G("uuid") == may);
        (mayRow.I("draftEntryCount"), mayRow.I("postedEntryCount"), mayRow.B("hasUnpostedDrafts")).Should().Be((2, 1, true), J.Short(mayRow));
        overview.A("periods").Should().HaveCount(13, "12 months + period 13");

        await k.Ok(k.PostEntry(d1.G("uuid")), "post d1");
        await k.Ok(k.DeleteEntry(d2.G("uuid")), "delete d2");

        // TS-PC-001 — lock
        var locked = await k.Ok(k.LockPeriod(may), "TS-PC-001 lock");
        (locked.S("oldStatus"), locked.S("newStatus"), locked.P("period").S("status")).Should().Be(("Open", "Locked", "Locked"));
        (await k.CreateEntry(Body(misc, D(5, 6), [new JeLine(cash, 1m, 0m), new JeLine(rev, 0m, 1m)]))).ShouldBeInvalid("JE-007", "Period is locked", "locked refuses entries");

        // TS-PC-003 — reopen
        (await k.Ok(k.ReopenPeriod(may), "TS-PC-003 reopen")).S("newStatus").Should().Be("Open");
        (await k.ReopenPeriod(may)).Status.Should().Be(HttpStatusCode.Conflict, "reopen only from Locked/AdjustmentOnly");

        // final close needs Locked (PC-003), then TS-PC-005
        (await k.FinalClosePeriod(may)).ShouldBeRefusedState("PC-003", "Only a Locked period can be closed finally", "final close of an Open period");
        await k.Ok(k.LockPeriod(may), "lock again");
        (await k.Ok(k.FinalClosePeriod(may), "TS-PC-005 final close")).S("newStatus").Should().Be("Closed");

        // TS-PC-004 / BR-PC-002/003 — Closed is final
        (await k.ReopenPeriod(may)).ShouldBeRefusedState("PC-002", "Closed periods cannot be reopened", "TS-PC-004");
        (await k.LockPeriod(may)).Status.Should().Be(HttpStatusCode.Conflict, "Closed → nothing");

        // AdjustmentOnly ⇄ Open, → Locked
        var jun = (await k.PeriodAsync(D(6, 1))).G("uuid");
        (await k.Ok(k.AdjustmentOnlyPeriod(jun), "June adjustment-only")).S("newStatus").Should().Be("AdjustmentOnly");
        (await k.Ok(k.LockPeriod(jun), "AdjustmentOnly → Locked")).S("newStatus").Should().Be("Locked");
        (await k.Ok(k.ReopenPeriod(jun), "Locked → Open")).S("newStatus").Should().Be("Open");
        (await k.Post($"{Close}/{jun}/close", new { targetStatus = "Closed" })).ShouldBeInvalid(null, "Target status must be Locked or AdjustmentOnly.", "bad target");
        (await k.LockPeriod(Guid.NewGuid())).ShouldRefuse(HttpStatusCode.NotFound, null, "Fiscal period not found.", "unknown period");
        (await k.CloseStatus(Guid.NewGuid())).ShouldRefuse(HttpStatusCode.NotFound, null, "Fiscal year not found.", "unknown year");

        // allowedActions reflect the state
        var after = await k.Ok(k.CloseStatus(year), "status");
        after.A("periods").Single(p => p.G("uuid") == may).A("allowedActions").Should().BeEmpty("Closed");
        after.A("periods").Single(p => p.G("uuid") == jun).A("allowedActions").Select(x => x.GetString()).Should().Contain(["Lock", "AdjustmentOnly"]);
    }

    [Fact]
    public async Task BR_TB_trial_balance_rules()
    {
        var o = await _root.NewJeOrgAsync("JRA");
        var k = o.K;
        var misc = await k.TypeAsync("MISC");
        Guid cash = o.Acc("DEFAULT_CASH"), rev = o.Acc("DEFAULT_REVENUE");
        string cashCode = o.Code("DEFAULT_CASH"), revCode = o.Code("DEFAULT_REVENUE"), apCode = o.Code("DEFAULT_PAYABLE");

        await o.PostedAsync(misc, cash, rev, 500m, D(2, 10));
        await o.PostedAsync(misc, cash, rev, 1000m, D(9, 15));
        await o.DraftAsync(misc, cash, rev, 777m, D(9, 16));

        // TS-TB-001 / TS-TB-006 / BR-TB-001/002 — period only
        var tb = await k.TrialBalanceAsync(D(3, 1), D(12, 31), opening: false);
        tb.B("isBalanced").Should().BeTrue(J.Short(tb));
        tb.D("totalDebit").Should().Be(tb.D("totalCredit"));
        var cashRow = tb.TbRow(cashCode);
        (cashRow.D("periodDebit"), cashRow.D("closingDebit"), cashRow.D("balance")).Should().Be((1000m, 1000m, 1000m), "draft 777 excluded, opening off");
        tb.TbRow(revCode).D("balance").Should().Be(-1000m, "signs are debit − credit");

        // TS-TB-002 / BR-TB-003 — opening = before fromDate
        var withOpening = await k.TrialBalanceAsync(D(3, 1), D(12, 31), opening: true);
        var c2 = withOpening.TbRow(cashCode);
        (c2.D("openingDebit"), c2.D("periodDebit"), c2.D("closingDebit")).Should().Be((500m, 1000m, 1500m), J.Short(c2));
        withOpening.P("totals").D("openingDebit").Should().Be(withOpening.P("totals").D("openingCredit"));

        // BR-TB-005 — zero activity hidden unless asked
        tb.TbRow(apCode).ValueKind.Should().Be(JsonValueKind.Undefined, "AP has no activity");
        var zeros = await k.Ok(k.Get($"{Tb}?fromDate={Iso(D(3, 1))}&toDate={Iso(D(12, 31))}&includeZeroBalances=true"), "with zero balances");
        zeros.TbRow(apCode).ValueKind.Should().Be(JsonValueKind.Object);
        TbRows(zeros).Should().OnlyContain(l => l.I("level") == 4, "leaf accounts only");

        // fiscal year instead of dates
        var year = await k.YearAsync();
        var byYear = await k.Ok(k.Get($"{Tb}?fiscalYearUuid={year.G("uuid")}"), "TB by fiscal year");
        byYear.TbRow(cashCode).D("closingDebit").Should().Be(1500m);

        // TS-TB-004 — account balance as of a date
        var bal = await k.Ok(k.Balance(cash, D(2, 28)), "TS-TB-004");
        (bal.D("debit"), bal.D("balance")).Should().Be((500m, 500m));
        (await k.Ok(k.Balance(cash, D(12, 31)), "year end balance")).D("balance").Should().Be(1500m);

        // TS-TB-005 — ledger with running balance
        var ledger = await k.Ok(k.Ledger(cash, D(1, 1), D(12, 31)), "TS-TB-005");
        ledger.D("openingBalance").Should().Be(0m);
        var lines = ledger.P("page").A("data");
        lines.Select(l => l.D("runningBalance")).Should().Equal(500m, 1500m);
        ledger.D("closingBalance").Should().Be(1500m);
        var later = await k.Ok(k.Ledger(cash, D(3, 1), D(12, 31)), "ledger from March");
        later.D("openingBalance").Should().Be(500m);
        later.P("page").A("data").Single().D("runningBalance").Should().Be(1500m);

        // texts
        (await k.Get(Tb)).ShouldBeInvalid(null, "Enter a from and a to date, or a fiscal year.", "no dates");
        (await k.TrialBalance(D(5, 1), D(4, 1))).ShouldBeInvalid(null, "The to date must not be before the from date.", "reversed range");
        var group = o.Accounts.Values.First(a => a.I("level") == 3 && a.B("isActive"));
        (await k.Ledger(group.G("uuid"), D(1, 1), D(12, 31))).ShouldBeInvalid(null, "Choose a Level 4 account.", "group ledger");
        (await k.Ledger(Guid.NewGuid(), D(1, 1), D(12, 31))).ShouldRefuse(HttpStatusCode.NotFound, null, "Account not found.", "unknown account");

        // CSV export
        var csv = await k.Admin.GetAsync($"{Tb}/export?fromDate={Iso(D(1, 1))}&toDate={Iso(D(12, 31))}");
        csv.StatusCode.Should().Be(HttpStatusCode.OK);
        csv.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        var body = await csv.Content.ReadAsByteArrayAsync();
        Gl.CsvLines(body).Should().Contain(l => l.Contains(cashCode));
    }
}
