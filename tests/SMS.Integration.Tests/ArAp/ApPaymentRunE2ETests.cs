using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.FinancialPosting;
using SMS.Integration.Tests.GeneralLedger;
using SMS.Integration.Tests.SapAlignment;
using Xunit;
using static SMS.Integration.Tests.ArAp.Ar;

namespace SMS.Integration.Tests.ArAp;

/// <summary>
/// A48 (QA) — payment schedules, early-payment discount and payment runs end to end (C10, C11, C15, D-8, D-9, D-15;
/// TS-PR-001..012, TS-EP-001..004, BR-PR-001..010, BR-EP-001..004, tasks 586–587). Bills are direct supplier invoices approved
/// through the existing path (each one gets its schedule, D-8); a run executes as one SupplierPayment per vendor batch (D-9).
/// Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~ArAp.ApPaymentRunE2ETests</c>.
/// </summary>
public sealed class ApPaymentRunE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;

    public ApPaymentRunE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _f.ShouldBeOnLocalDb();
        _root = new SapKit(factory, "A48PR");
    }

    private Task<ArOrg> OrgAsync(string prefix, bool gl = false) => _root.NewArOrgAsync(prefix, gl, stock: 1m);

    private static async Task<JsonElement> ProposeOkAsync(ArOrg o, DateTime cutoff, Guid? vendor = null, bool discounts = true) =>
        await o.K.Ok(o.Propose(cutoff, vendor, discounts), "propose payment run");

    private static async Task<JsonElement> ApproveAndExecuteAsync(ArOrg o, Guid run)
    {
        await o.K.Ok(o.ApproveRun(run), "approve run");
        return await o.K.Ok(o.ExecuteRun(run), "execute run");
    }

    private static async Task<int> PaymentsOfVendorAsync(ArOrg o, Guid vendor) =>
        await o.F.RowCountAsync("SELECT COUNT(*) FROM finance.supplier_payments WHERE OrganizationId = @o AND SupplierId = @v AND Status NOT IN ('CANCELLED','BOUNCED')",
            ("@o", o.OrgId), ("@v", vendor));

    [Fact]
    public async Task TS_PR_001_to_007_five_vendors_fifteen_items_propose_approve_execute()
    {
        var o = await OrgAsync("PR1");
        var vendors = new List<Partner>();
        var bills = new Dictionary<Guid, (Guid Vendor, decimal Amount)>();
        for (var v = 0; v < 5; v++)
        {
            var vendor = await o.VendorAsync($"Run vendor {v}");
            vendors.Add(vendor);
            for (var i = 0; i < 3; i++)
            {
                var amount = 100m * (v + 1) + 10m * i;
                bills[await o.ApprovedBillAsync(vendor, amount, dueInDays: 5)] = (vendor.Uuid, amount);
            }
        }
        var late = await o.ApprovedBillAsync(vendors[0], 999m, dueInDays: 60); // beyond the cutoff (BR-PR-001)

        // TS-PR-001
        var run = await ProposeOkAsync(o, Today.AddDays(10));
        run.RunStatus().Should().Be("Proposed");
        run.RunBatches().Should().HaveCount(5, "BR-PR-003: one group per vendor");
        var items = run.RunItems();
        items.Should().HaveCount(15);
        items.Select(i => i.G("invoiceUuid")).Should().NotContain(late);
        run.Head().D("totalAmount").Should().Be(bills.Values.Sum(b => b.Amount));
        run.Head().I("totalVendors").Should().Be(5);
        run.Head().I("totalItems").Should().Be(15);
        foreach (var g in run.RunBatches())
            g.D("totalAmount").Should().Be(bills.Values.Where(b => b.Vendor == g.G("vendorUuid")).Sum(b => b.Amount));
        J.Short(run).Should().NotContain(late.ToString());

        // TS-PR-003 — remove one item, totals follow.
        var removed = items.First();
        (await o.RemoveItem(run.RunUuid(), removed.G("itemUuid"), " ")).ShouldRefuse(HttpStatusCode.BadRequest, "A removal needs a reason.", "no reason");
        await o.K.Ok(o.RemoveItem(run.RunUuid(), removed.G("itemUuid")), "remove item");
        var after = await o.RunAsync(run.RunUuid());
        after.Head().D("totalAmount").Should().Be(bills.Values.Sum(b => b.Amount) - removed.D("originalAmount"), "TS-PR-003");
        after.Head().I("totalItems").Should().Be(14);
        after.A("removedItems").Should().ContainSingle(i => i.G("itemUuid") == removed.G("itemUuid"));

        // TS-PR-004 / BR-PR-006
        (await o.ExecuteRun(run.RunUuid())).ShouldRefuse(HttpStatusCode.Conflict, "Only an approved payment run can be executed.", "execute before approve");
        var approved = await o.K.Ok(o.ApproveRun(run.RunUuid()), "approve");
        approved.RunStatus().Should().Be("Approved");

        // TS-PR-005..007
        var done = await o.K.Ok(o.ExecuteRun(run.RunUuid()), "execute");
        done.RunStatus().Should().Be("Completed");
        done.Head().I("failedBatchCount").Should().Be(0);
        foreach (var g in done.RunBatches())
        {
            g.S("status").Should().Be("Paid");
            g.NG("supplierPaymentUuid").Should().NotBeNull("D-9: a SupplierPayment per vendor batch");
        }
        foreach (var vendor in vendors)
            (await PaymentsOfVendorAsync(o, vendor.Uuid)).Should().Be(1, $"TS-PR-005: one payment for {vendor.Name}");

        var paid = bills.Keys.Where(b => b != removed.G("invoiceUuid")).ToList();
        foreach (var bill in paid)
        {
            (await o.LivePaymentLinesAsync(bill)).Should().Be(1, "each scheduled invoice is allocated once");
            (await o.ScheduleOfAsync(bill)).S("status").Should().Be("Paid", "TS-PR-006");
            (await o.SupplierInvoiceDetailAsync(bill)).S("paymentStatus").Should().BeOneOf("PAID", "Paid", "FULLY_PAID");
        }
        var removedSchedule = await o.ScheduleOfAsync(removed.G("invoiceUuid"));
        removedSchedule.S("status").Should().Be("Open", "the removed item stays payable");
        removedSchedule.IsNull("reservedByRunUuid").Should().BeTrue("and is no longer held by the run");

        // TS-PR-007 / BR-PR-008: the vendor's open items are what is left.
        var v1 = vendors[1];
        Rows(await o.K.Ok(o.VendorOpenItems(v1.Uuid), "vendor open items")).Where(i => !i.B("isCredit")).Should().BeEmpty(
            "every invoice of vendor 1 was paid in full and settled");
        (await _f.RowCountAsync("SELECT COUNT(*) FROM finance.supplier_ledger_entries WHERE SupplierId = @v AND DebitAmount > 0", ("@v", v1.Uuid)))
            .Should().BeGreaterThan(0, "the payment hit the supplier ledger");

        // TS-PR-011 / BR-PR-009
        var again = await o.ExecuteRun(run.RunUuid());
        again.Status.Should().Be(HttpStatusCode.Conflict, again.ToString());
        foreach (var vendor in vendors) (await PaymentsOfVendorAsync(o, vendor.Uuid)).Should().Be(1, "no second payment");
        (await o.CancelRun(run.RunUuid())).ShouldRefuse(HttpStatusCode.Conflict,
            "Only a draft, proposed, reviewing or approved payment run can be cancelled.", "BR-PR-010: a completed run");
        (await o.RemoveItem(run.RunUuid(), items.Last().G("itemUuid"))).ShouldRefuse(HttpStatusCode.Conflict, "it can no longer change.", "BR-PR-009");
    }

    [Fact]
    public async Task TS_PR_002_blocked_items_are_excluded_and_listed()
    {
        var o = await OrgAsync("PR2");
        var keep = await o.ApprovedBillAsync(o.Vendor, 100m, 3);
        var blocked = await o.ApprovedBillAsync(o.Vendor, 200m, 3);
        var schedule = await o.ScheduleOfAsync(blocked);
        await o.K.Ok(o.Block(schedule.G("uuid"), "Disputed"), "block");
        (await o.Block(schedule.G("uuid"))).ShouldRefuse(HttpStatusCode.Conflict, "is already blocked.", "block twice");

        var run = await ProposeOkAsync(o, Today.AddDays(10));
        run.RunItems().Select(i => i.G("invoiceUuid")).Should().Equal([keep], "BR-PR-002");
        run.A("blockedItems").Should().ContainSingle(b => b.G("invoiceUuid") == blocked, "shown separately");
        await o.K.Ok(o.CancelRun(run.RunUuid()), "cancel");
        await o.K.Ok(o.Unblock(schedule.G("uuid")), "unblock");
        (await o.Unblock(schedule.G("uuid"))).ShouldRefuse(HttpStatusCode.Conflict, "is not blocked.", "unblock twice");
    }

    [Fact]
    public async Task TS_PR_010_cancel_an_approved_run_pays_nothing_and_frees_the_schedules()
    {
        var o = await OrgAsync("PR10");
        var bill = await o.ApprovedBillAsync(o.Vendor, 500m, 2);
        var run = await ProposeOkAsync(o, Today.AddDays(5));
        (await o.ScheduleOfAsync(bill)).NG("reservedByRunUuid").Should().Be(run.RunUuid(), "held by the run");
        (await o.Block((await o.ScheduleOfAsync(bill)).G("uuid"))).ShouldRefuse(HttpStatusCode.Conflict, "remove it from the run first.",
            "a reserved schedule cannot be blocked");
        await o.K.Ok(o.ApproveRun(run.RunUuid()), "approve");
        var cancelled = await o.K.Ok(o.CancelRun(run.RunUuid(), "Bank holiday"), "cancel approved");
        cancelled.RunStatus().Should().Be("Cancelled", "TS-PR-010");
        (await PaymentsOfVendorAsync(o, o.Vendor.Uuid)).Should().Be(0, "no payment created");
        (await o.ScheduleOfAsync(bill)).IsNull("reservedByRunUuid").Should().BeTrue();
        (await ProposeOkAsync(o, Today.AddDays(5))).RunItems().Should().ContainSingle(i => i.G("invoiceUuid") == bill, "free for the next run");
    }

    [Fact]
    public async Task TS_PR_012_concurrent_proposals_never_pay_an_invoice_twice()
    {
        var o = await OrgAsync("PR12");
        var bills = new List<Guid>();
        for (var i = 0; i < 6; i++) bills.Add(await o.ApprovedBillAsync(o.Vendor, 100m + i, 2));

        var answers = await Task.WhenAll(o.Propose(Today.AddDays(5)), o.Propose(Today.AddDays(5)));
        var runs = answers.Where(a => a.Status == HttpStatusCode.OK).Select(a => a.Result).ToList();
        answers.Where(a => a.Status != HttpStatusCode.OK).Should().OnlyContain(a => a.Status == HttpStatusCode.BadRequest || a.Status == HttpStatusCode.Conflict,
            string.Join(" | ", answers.Select(a => a.ToString())));
        var claimed = runs.SelectMany(r => r.RunItems().Select(i => i.G("invoiceUuid"))).ToList();
        claimed.Should().OnlyHaveUniqueItems("TS-PR-012: a schedule is in one run at most");

        foreach (var r in runs) await ApproveAndExecuteAsync(o, r.RunUuid());
        foreach (var bill in bills) (await o.LivePaymentLinesAsync(bill)).Should().BeLessThanOrEqualTo(1, "never paid twice");
        claimed.Count.Should().Be(6, "between them the runs took every bill");
    }

    [Fact]
    public async Task TS_PR_008_and_EP_001_003_discount_in_the_run_and_the_three_line_journal()
    {
        var o = await OrgAsync("PR8", gl: true);
        var term = await o.K.Ok(o.CreateTerm("2/10N30", 2m, 10, 30), "term 2/10 net 30");
        var bill = await o.ApprovedBillAsync(o.Vendor, 1_000m, dueInDays: 30);
        var schedule = await o.ScheduleOfAsync(bill);
        var withTerm = await o.K.Ok(o.SetScheduleTerm(schedule.G("uuid"), term.G("uuid")), "term on the schedule");
        withTerm.D("discountAmount").Should().Be(20m, "BR-EP-002: 1 000 × 2 %");
        withTerm.S("discountDate").Should().StartWith(Iso(Today.AddDays(10)), "BR-EP-001: invoice date + 10");

        var calc = await o.K.Ok(o.K.Get($"{Schedules}/{schedule.G("uuid")}/discount?paymentDate={Iso(Today)}"), "discount calculation");
        J.Short(calc).Should().Contain("20");

        var run = await ProposeOkAsync(o, Today.AddDays(30));
        var item = run.RunItems().Single();
        item.D("discountAmount").Should().Be(20m, "BR-PR-004");
        item.D("netPaymentAmount").Should().Be(980m, "TS-EP-001: net = original − discount");
        var done = await ApproveAndExecuteAsync(o, run.RunUuid());
        var payment = done.RunBatches().Single().G("supplierPaymentUuid");

        var pay = (await _f.QueryAsync("SELECT TotalAmount FROM finance.supplier_payments WHERE UUID = @u", ("@u", payment))).Single();
        Convert.ToDecimal(pay["TotalAmount"]).Should().Be(980m, "the cash paid");
        var line = (await _f.QueryAsync("SELECT AllocatedAmount, DiscountAmount FROM finance.supplier_payment_lines l JOIN finance.supplier_payments p ON p.Id = l.SupplierPaymentId WHERE p.UUID = @u",
            ("@u", payment))).Single();
        Convert.ToDecimal(line["AllocatedAmount"]).Should().Be(1_000m, "AP-1: the gross amount settles the invoice");
        Convert.ToDecimal(line["DiscountAmount"]).Should().Be(20m);
        (await o.SupplierInvoiceDetailAsync(bill)).S("paymentStatus").Should().BeOneOf("PAID", "Paid", "FULLY_PAID");

        await _f.RunJobsAsync();
        var codes = await o.K.DefaultCodesAsync();
        var journals = await SapKit.WaitForAsync(() => _f.JournalsAsync(o.OrgId, payment), j => j.Count > 0, "the payment's A44 journal");
        var je = journals.Last();
        je.Balanced.Should().BeTrue(je.ToString());
        je.Net(codes["DEFAULT_PAYABLE"]).Should().Be(1_000m, $"TS-EP-003: Dr AP full — {je}");
        je.Net(codes["DEFAULT_DISCOUNT_RECEIVED"]).Should().Be(-20m, $"BR-EP-003 / BR-PR-005: Cr discount — {je}");
        je.Lines.Where(l => l.Code != codes["DEFAULT_PAYABLE"] && l.Code != codes["DEFAULT_DISCOUNT_RECEIVED"]).Sum(l => l.Credit - l.Debit)
            .Should().Be(980m, "Cr bank net");
        je.Lines.Should().HaveCount(3, "TS-EP-003: a balanced 3-line journal");
    }

    [Fact]
    public async Task TS_EP_002_after_the_window_the_full_amount_is_paid()
    {
        var o = await OrgAsync("EP2");
        var term = await o.K.Ok(o.CreateTerm("2/10N30", 2m, 10, 30), "term");
        var bill = await o.ApprovedBillAsync(o.Vendor, 1_000m, dueInDays: 10, invoiceDate: Today.AddDays(-20));
        var schedule = await o.ScheduleOfAsync(bill);
        await o.K.Ok(o.SetScheduleTerm(schedule.G("uuid"), term.G("uuid")), "term on the schedule");
        var run = await ProposeOkAsync(o, Today.AddDays(30));
        var item = run.RunItems().Single();
        item.D("discountAmount").Should().Be(0m, "BR-EP-001: paid 20 days after the invoice, window 10");
        item.D("netPaymentAmount").Should().Be(1_000m);
    }

    [Fact]
    public async Task TS_EP_004_discount_opportunities_and_term_rules()
    {
        var o = await OrgAsync("EP4");
        (await o.CreateTerm("BAD", 2m, 30, 30)).ShouldRefuse(HttpStatusCode.BadRequest, "Discount days must be fewer than net days.", "BR-EP-004");
        (await o.CreateTerm("ZERO", 0m, 10, 30)).ShouldRefuse(HttpStatusCode.BadRequest, "The discount percent must be greater than 0 and less than 100.", "0 %");
        var term = await o.K.Ok(o.CreateTerm("1/7N30", 1m, 7, 30), "term");
        (await o.CreateTerm("1/7N30", 1m, 7, 30)).ShouldRefuse(HttpStatusCode.Conflict, "An early payment term with code 1/7N30 already exists.", "duplicate code");

        var bill = await o.ApprovedBillAsync(o.Vendor, 2_500m, 30);
        await o.K.Ok(o.SetScheduleTerm((await o.ScheduleOfAsync(bill)).G("uuid"), term.G("uuid")), "term");
        var list = Rows(await o.K.Ok(o.Discounts(30), "discount opportunities"));
        var row = list.Should().ContainSingle(r => r.G("invoiceUuid") == bill).Which;
        row.D("discountAmount").Should().Be(25m);
        row.S("discountDate").Should().StartWith(Iso(Today.AddDays(7)));
    }

    [Fact]
    public async Task TS_PR_009_export_csv_carries_one_row_per_vendor_batch()
    {
        var o = await OrgAsync("PR9");
        var v2 = await o.VendorAsync("Second payee");
        await o.ApprovedBillAsync(o.Vendor, 300m, 2);
        await o.ApprovedBillAsync(v2, 400m, 2);
        var run = await ProposeOkAsync(o, Today.AddDays(5));
        await o.K.Ok(o.ApproveRun(run.RunUuid()), "approve");

        using var resp = await o.K.Admin.GetAsync($"{Runs}/{run.RunUuid()}/export?format=csv");
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        var text = Encoding.UTF8.GetString(await resp.Content.ReadAsByteArrayAsync()).TrimStart('﻿');
        var lines = text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
        lines.Length.Should().Be(3, $"header + 2 vendors — {text}");
        text.Should().Contain(o.Vendor.Name).And.Contain(v2.Name).And.Contain("300").And.Contain("400").And.Contain("PKR");

        var viewer = await o.UserWithAsync("apview", AP_VIEW);
        using var denied = await viewer.GetAsync($"{Runs}/{run.RunUuid()}/export?format=csv");
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden, "export needs PAYMENT_RUN_EXECUTE");
    }

    [Fact]
    public async Task Proposal_with_nothing_due_is_refused()
    {
        var o = await OrgAsync("PR0");
        await o.ApprovedBillAsync(o.Vendor, 100m, 90);
        (await o.Propose(Today.AddDays(5))).ShouldRefuse(HttpStatusCode.BadRequest, $"Nothing is due by", "no item in the proposal");
    }

    [Fact]
    public async Task Schedules_list_cash_flow_and_upcoming_answer()
    {
        var o = await OrgAsync("PRCF");
        var bill = await o.ApprovedBillAsync(o.Vendor, 750m, 3);
        (await o.ScheduleOfAsync(bill)).D("originalAmount").Should().Be(750m);
        var cf = await o.K.Ok(o.CashFlow(), "cash flow");
        cf.D("totalDue").Should().Be(750m);
        Rows(await o.K.Ok(o.Upcoming(), "upcoming")).Should().Contain(r => r.G("invoiceUuid") == bill);
    }
}
