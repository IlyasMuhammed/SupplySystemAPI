using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.GeneralLedger;
using SMS.Integration.Tests.SapAlignment;
using Xunit;
using static SMS.Integration.Tests.ArAp.Ar;

namespace SMS.Integration.Tests.ArAp;

/// <summary>
/// A48 (QA) — round-2 contract additions (API-CONTRACT Changes, AP/RPT round 2): asynchronous execution (202 + polling),
/// resume of a stuck run, cancel of an Executing run, the discount-account gate (D-AP-12), matching reject + search (FE-6/7),
/// debit-memo reads (FE-8), memo-only users reading open items, and concurrent supplier-invoice numbering (QA-2 / D-AP-15).
/// A "stuck" run is made by putting an approved run into Executing in SQL — what a crash before the first batch leaves.
/// Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~ArAp.ArApRound2E2ETests</c>.
/// </summary>
public sealed class ArApRound2E2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;

    public ArApRound2E2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _f.ShouldBeOnLocalDb();
        _root = new SapKit(factory, "A48R2");
    }

    private Task<ArOrg> OrgAsync(string prefix, bool gl = false) => _root.NewArOrgAsync(prefix, gl, stock: 1m);

    private static Task<Api> Resume(ArOrg o, Guid run, HttpClient? h = null) => o.K.Post($"{Runs}/{run}/resume", null, h);

    private async Task<int> LivePaymentsAsync(ArOrg o) =>
        await _f.RowCountAsync("SELECT COUNT(*) FROM finance.supplier_payments WHERE OrganizationId = @o AND Status NOT IN ('CANCELLED','BOUNCED')", ("@o", o.OrgId));

    private async Task StickAsync(Guid run) =>
        (await _f.ExecuteAsync("UPDATE finance.payment_runs SET Status = 'Executing' WHERE Uuid = @u", ("@u", run))).Should().Be(1);

    /// <summary>An approved run over one bill each for <paramref name="vendors"/> new vendors.</summary>
    private static async Task<(Guid Run, List<Guid> Bills)> ApprovedRunAsync(ArOrg o, int vendors)
    {
        var bills = new List<Guid>();
        for (var i = 0; i < vendors; i++) bills.Add(await o.ApprovedBillAsync(await o.VendorAsync($"Payee {i}"), 100m + i, 2));
        var run = (await o.K.Ok(o.Propose(Today.AddDays(5)), "propose")).RunUuid();
        await o.K.Ok(o.ApproveRun(run), "approve (straight from Proposed, FE-2)");
        return (run, bills);
    }

    [Fact]
    public async Task Execute_of_more_than_20_batches_answers_202_and_completes_in_the_background()
    {
        var o = await OrgAsync("R2A");
        var (run, bills) = await ApprovedRunAsync(o, 21);
        var r = await o.ExecuteRun(run);
        r.Status.Should().Be(HttpStatusCode.Accepted, $"D-AP-13: 21 batches > 20 — {r}");
        r.Result.RunStatus().Should().Be("Executing");
        (await o.ExecuteRun(run)).ShouldRefuse(HttpStatusCode.Conflict, "Only an approved payment run can be executed.", "second execute");

        var done = await SapKit.WaitForAsync(() => o.RunAsync(run), x => x.RunStatus() != "Executing", "the run to complete", seconds: 300);
        done.RunStatus().Should().Be("Completed");
        done.RunBatches().Should().OnlyContain(b => b.S("status") == "Paid");
        (await LivePaymentsAsync(o)).Should().Be(21);
        foreach (var bill in bills) (await o.LivePaymentLinesAsync(bill)).Should().Be(1);
    }

    [Fact]
    public async Task Small_run_executes_in_the_request_with_200()
    {
        var o = await OrgAsync("R2B");
        var (run, _) = await ApprovedRunAsync(o, 2);
        var r = await o.ExecuteRun(run);
        r.Status.Should().Be(HttpStatusCode.OK, r.ToString());
        r.Result.RunStatus().Should().Be("Completed");
    }

    [Fact]
    public async Task A_stuck_run_resumes_and_pays_each_batch_once()
    {
        var o = await OrgAsync("R2C");
        var (run, bills) = await ApprovedRunAsync(o, 3);
        (await Resume(o, run)).ShouldRefuse(HttpStatusCode.Conflict, "Only an executing payment run can be resumed.", "an approved run");
        await StickAsync(run);

        var viewer = await o.UserWithAsync("apview", AP_VIEW);
        (await Resume(o, run, viewer)).ShouldBeForbidden("resume needs PAYMENT_RUN_EXECUTE");
        var r = await Resume(o, run);
        r.Status.Should().Be(HttpStatusCode.OK, r.ToString());
        r.Result.RunStatus().Should().Be("Completed");
        (await LivePaymentsAsync(o)).Should().Be(3);
        foreach (var bill in bills) (await o.LivePaymentLinesAsync(bill)).Should().Be(1);

        (await Resume(o, run)).ShouldRefuse(HttpStatusCode.Conflict, "Only an executing payment run can be resumed.", "a completed run");
        // Crash after the batches were paid: a second resume skips the Paid batches.
        await StickAsync(run);
        await o.K.Ok(Resume(o, run), "resume again");
        (await LivePaymentsAsync(o)).Should().Be(3, "no batch paid twice");
    }

    [Fact]
    public async Task An_executing_run_cancels_only_while_nothing_is_paid()
    {
        var o = await OrgAsync("R2D");
        var (run, bills) = await ApprovedRunAsync(o, 2);
        await StickAsync(run);
        var c = await o.K.Ok(o.CancelRun(run, "Job died"), "cancel a stuck run");
        c.RunStatus().Should().Be("Cancelled");
        c.RunBatches().Should().OnlyContain(b => b.S("status") == "Failed");
        (await LivePaymentsAsync(o)).Should().Be(0);
        foreach (var bill in bills) (await o.ScheduleOfAsync(bill)).IsNull("reservedByRunUuid").Should().BeTrue("schedules released");

        var (paidRun, _) = await ApprovedRunAsync(o, 2);
        await o.K.Ok(o.ExecuteRun(paidRun), "execute");
        await StickAsync(paidRun);
        var number = (await o.RunAsync(paidRun)).Head().S("runNumber");
        (await o.CancelRun(paidRun)).ShouldRefuse(HttpStatusCode.Conflict, $"Payment run {number} has paid batches; resume it instead of cancelling it.",
            "a run with a paid batch");
    }

    [Fact]
    public async Task Discounted_run_without_a_discount_account_cannot_be_approved()
    {
        var o = await OrgAsync("R2E", gl: true);
        (await _f.ExecuteAsync("UPDATE finance.AccountingDefaults SET AccountId = NULL WHERE OrganizationId = @o AND DefaultKey = 'DEFAULT_DISCOUNT_RECEIVED'",
            ("@o", o.OrgId))).Should().Be(1);
        var term = await o.K.Ok(o.CreateTerm("2/10N30", 2m, 10, 30), "term");
        var bill = await o.ApprovedBillAsync(o.Vendor, 1_000m, 30);
        await o.K.Ok(o.SetScheduleTerm((await o.ScheduleOfAsync(bill)).G("uuid"), term.G("uuid")), "term on schedule");

        var run = await o.K.Ok(o.Propose(Today.AddDays(30)), "propose with discount");
        run.A("warnings").Should().NotBeEmpty("D-AP-12: the proposal warns");
        (await o.ApproveRun(run.RunUuid())).ShouldRefuse(HttpStatusCode.Conflict,
            "This run takes early-payment discounts, but the general ledger has no account for DEFAULT_DISCOUNT_RECEIVED", "the discount-account gate");
        await o.K.Ok(o.CancelRun(run.RunUuid()), "cancel");

        var plain = await o.K.Ok(o.Propose(Today.AddDays(30), discounts: false), "propose without discounts");
        plain.RunItems().Single().D("discountAmount").Should().Be(0m);
        await o.K.Ok(o.ApproveRun(plain.RunUuid()), "approve the undiscounted run");
    }

    [Fact]
    public async Task Matching_reject_and_pending_search()
    {
        var o = await OrgAsync("R2F");
        var (po, poLine) = await o.OrderedAsync(o.Vendor, 10m, 300m);
        var (grn, grnLine) = await o.ReceivedAsync(po, 10m);
        var number = $"{o.K.Marker}-FINDME";
        var inv = await o.SupplierInvoiceAsync(o.Vendor, number, po, poLine, grn, grnLine, 10m, 330m);
        await o.K.Ok(o.Match(inv), "match");
        var other = await o.SupplierInvoiceAsync(o.Vendor, $"{o.K.Marker}-OTHER", null, null, null, null, 1m, 50m);

        var found = Rows(await o.K.Ok(o.K.Get($"{Matching}/pending?search={Uri.EscapeDataString("findme")}&page=1&pageSize=50"), "search"));
        found.Select(m => m.G("invoiceUuid")).Should().Equal([inv], "FE-7: supplier invoice number, case-insensitive");
        var poNumber = (await o.K.Ok(o.K.Get($"/api/purchase-orders/{po}"), "PO")).S("poNumber")!;
        Rows(await o.K.Ok(o.K.Get($"{Matching}/pending?search={Uri.EscapeDataString(poNumber)}&page=1&pageSize=50"), "search by PO"))
            .Select(m => m.G("invoiceUuid")).Should().Contain(inv).And.NotContain(other);

        (await o.K.Post($"{Matching}/invoices/{inv}/reject", new { reason = " " })).ShouldRefuse(HttpStatusCode.BadRequest,
            "Say why the invoice is being rejected.", "no reason");
        var matcher = await o.UserWithAsync("mtexec", MATCH_EXECUTE, AP_VIEW);
        (await o.K.Post($"{Matching}/invoices/{inv}/reject", new { reason = "Price not agreed" }, matcher)).ShouldBeForbidden("reject needs MATCH_APPROVE");
        var rejected = await o.K.Ok(o.K.Post($"{Matching}/invoices/{inv}/reject", new { reason = "Price not agreed" }), "reject");
        rejected.S("matchStatus").Should().Be("Rejected");
        (await o.K.Post($"{Matching}/invoices/{inv}/reject", new { reason = "again" })).Status.Should().Be(HttpStatusCode.Conflict, "already rejected");
        (await o.ScheduleOfAsync(inv)).ValueKind.Should().NotBe(JsonValueKind.Object, "never payable");
    }

    [Fact]
    public async Task Debit_memo_reads_follow_the_application()
    {
        var o = await OrgAsync("R2G");
        var bill = await o.ApprovedBillAsync(o.Vendor, 1_000m);
        var memo = (await o.K.Ok(o.DebitMemo(o.Vendor.Uuid, 150m), "debit memo")).UuidOf();

        var detail = await o.K.Ok(o.K.Get($"/api/finance/ap/debit-memo/{memo}"), "debit memo detail");
        detail.S("origin").Should().Be("MEMO");
        detail.D("remainingAmount").Should().Be(150m);
        detail.A("applications").Should().BeEmpty();
        Rows(await o.K.Ok(o.K.Get($"/api/finance/ap/vendors/{o.Vendor.Uuid}/debit-memos?openOnly=true"), "open debit memos"))
            .Should().Contain(m => m.G("uuid") == memo);

        await o.K.Ok(o.ApplyDebitMemo(memo, [(bill, 150m)]), "apply");
        var after = await o.K.Ok(o.K.Get($"/api/finance/ap/debit-memo/{memo}"), "detail after apply");
        after.D("remainingAmount").Should().Be(0m);
        after.A("applications").Should().ContainSingle(a => a.G("invoiceUuid") == bill && a.D("amount") == 150m);
        Rows(await o.K.Ok(o.K.Get($"/api/finance/ap/vendors/{o.Vendor.Uuid}/debit-memos?openOnly=true"), "open debit memos"))
            .Should().NotContain(m => m.G("uuid") == memo, "fully applied");
        Rows(await o.K.Ok(o.K.Get($"/api/finance/ap/vendors/{o.Vendor.Uuid}/debit-memos"), "all debit memos")).Should().Contain(m => m.G("uuid") == memo);

        var memoUser = await o.UserWithAsync("apdm", AP_DEBIT_MEMO);
        (await o.K.Get($"/api/finance/ap/debit-memo/{memo}", memoUser)).Status.Should().Be(HttpStatusCode.OK, "AP_DEBIT_MEMO reads");
        (await o.K.Get($"/api/finance/ap/debit-memo/{Guid.NewGuid()}")).Status.Should().Be(HttpStatusCode.NotFound);
        var b = await OrgAsync("R2GB");
        (await b.K.Get($"/api/finance/ap/debit-memo/{memo}")).Status.Should().Be(HttpStatusCode.NotFound, "another organization's memo");
    }

    [Fact]
    public async Task Memo_only_users_can_load_the_open_items_they_apply_to()
    {
        var o = await _root.NewArOrgAsync("R2H", stock: 5m);
        await o.IssuedInvoiceAsync(o.Customer, 1m);
        await o.ApprovedBillAsync(o.Vendor, 100m);
        var cm = await o.UserWithAsync("arcm", AR_CREDIT_MEMO);
        var dm = await o.UserWithAsync("apdm", AP_DEBIT_MEMO);
        (await o.CustomerOpenItems(o.Customer.Uuid, cm)).Status.Should().Be(HttpStatusCode.OK, "AR_CREDIT_MEMO reads customer open items");
        (await o.VendorOpenItems(o.Vendor.Uuid, dm)).Status.Should().Be(HttpStatusCode.OK, "AP_DEBIT_MEMO reads vendor open items");
        (await o.VendorOpenItems(o.Vendor.Uuid, cm)).ShouldBeForbidden("the credit-memo code does not open payables");
        (await o.CustomerOpenItems(o.Customer.Uuid, dm)).ShouldBeForbidden("the debit-memo code does not open receivables");
        (await o.CustomerBalance(o.Customer.Uuid, cm)).ShouldBeForbidden("only open items were widened");
    }

    [Fact]
    public async Task Concurrent_supplier_invoice_creates_get_distinct_numbers()
    {
        var o = await OrgAsync("R2I");
        var answers = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => o.K.Post(SupplierInvoices, new
        {
            SupplierInvoiceNo = $"{o.K.Marker}-PAR-{i}", SupplierId = o.Vendor.Uuid, InvoiceDate = Today, ReceivedDate = Today,
            DueDate = Today.AddDays(30), Currency = "PKR", Subtotal = 10m + i, TaxAmount = 0m
        })));
        answers.Should().OnlyContain(a => a.Status == HttpStatusCode.OK, string.Join(" | ", answers.Where(a => a.Status != HttpStatusCode.OK)));
        var numbers = new List<string>();
        foreach (var a in answers) numbers.Add((await o.SupplierInvoiceDetailAsync(a.Result.GetGuid())).S("invoiceNumber")!);
        numbers.Should().OnlyHaveUniqueItems("QA-2 / D-AP-15: numbered under a lock");
        numbers.Should().OnlyContain(n => System.Text.RegularExpressions.Regex.IsMatch(n, @"^INV-\d{4}-\d{5}$"));
    }
}
