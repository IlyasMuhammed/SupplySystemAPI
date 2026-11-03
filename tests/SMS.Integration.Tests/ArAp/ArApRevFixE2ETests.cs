using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.FinancialPosting;
using SMS.Integration.Tests.GeneralLedger;
using SMS.Integration.Tests.ModuleRegistry;
using SMS.Integration.Tests.MultiCurrency;
using SMS.Integration.Tests.Pos;
using SMS.Integration.Tests.SapAlignment;
using SMS.Shared.Integration.QuickBooks;
using Xunit;
using static SMS.Integration.Tests.ArAp.Ar;
using static SMS.Integration.Tests.Pos.PosKit;

namespace SMS.Integration.Tests.ArAp;

/// <summary>
/// A48 (QA) — end-to-end checks of the REV fixes (ADDENDUM-48-ANALYSIS.md, REV section). Mail is NOT accepted in this class:
/// the host's disarmed real sender fails every send, which is what a broken SMTP looks like (REV-1).
/// Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~ArAp.ArApRevFixE2ETests</c>.
/// </summary>
public sealed class ArApRevFixE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _root;

    public ArApRevFixE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _f.ShouldBeOnLocalDb();
        _f.Mail.Accept = false;
        _root = new SapKit(factory, "A48RV");
    }

    private async Task StickAsync(Guid run) =>
        (await _f.ExecuteAsync("UPDATE finance.payment_runs SET Status = 'Executing' WHERE Uuid = @u", ("@u", run))).Should().Be(1);

    // ── REV-1 / REV-2 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task REV_1_2_failed_email_is_logged_failed_and_dunning_moves_nothing()
    {
        var o = await _root.NewArOrgAsync("RV1", stock: 10m);
        var email = await o.SetEmailAsync(o.Customer);
        await o.IssuedInvoiceAsync(o.Customer, 1m);

        var st = await o.EmailStatement(o.Customer.Uuid, Today.AddDays(-30), Today);
        st.Status.Should().Be(HttpStatusCode.Conflict, $"D-RPT-8: a failed send is a 409 — {st}");
        var log = Rows(await o.K.Ok(o.K.Get($"/api/finance/statements/log?role=Customer&partnerUuid={o.Customer.Uuid}"), "statement log"));
        log.Should().Contain(l => l.S("emailStatus") == "Failed" && !string.IsNullOrWhiteSpace(l.S("emailError")), "REV-1: never 'Sent'");
        log.Should().NotContain(l => l.S("emailStatus") == "Sent");

        // Dunning: a level-1 charge of 25 that must not be raised when the letter cannot go out.
        var proc = (await o.K.Ok(o.CreateProcedure("RV1PROC", 0, 0m, null, [Level(1, 7, charge: 25m), Level(2, 14)]), "procedure")).G("uuid");
        var c = await o.CustomerAsync("Undeliverable");
        await o.AssignProcedureAsync(c.Uuid, proc);
        await o.SetEmailAsync(c);
        var inv = await o.IssuedInvoiceAsync(c, 2m);
        await o.BackdateSalesInvoiceAsync(inv, 20);

        var run = await o.K.Ok(o.Preview(), "preview");
        var done = await o.K.Ok(o.ExecuteDunning(run.G("uuid")), "execute");
        var item = done.ItemFor(c.Uuid);
        item.S("status").Should().Be("Failed", $"REV-1: the send failed — {J.Short(item)}");
        item.IsNull("chargeDocumentUuid").Should().BeTrue("REV-2: no charge for an undelivered notice");
        var p = await o.ProfileAsync(c.Uuid);
        p.I("currentDunningLevel").Should().Be(0, "the level does not move");
        p.B("creditHold").Should().BeFalse();
        (await _f.RowCountAsync("SELECT COUNT(*) FROM finance.sales_invoices WHERE OrganizationId = @o AND PartnerId = @p AND SourceType = 'DUNNING_CHARGE'",
            ("@o", o.OrgId), ("@p", c.Uuid))).Should().Be(0, "no SDC- charge document");
        (await o.CustomerBalance(c.Uuid)).Result.D("totalOpen").Should().Be(200m);
    }

    // ── REV-3 ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task REV_3_pos_return_refused_when_credits_would_exceed_the_invoice_total()
    {
        var w = await _root.PosWorldAsync("RV3");
        var k = w.K;
        var sup = w.Supervisor.Client;
        var shift = await w.OpenShiftAsync(0m);
        var cashier = await w.CashierClientAsync();
        var c = (await k.Ok(k.Post("/api/customers", Mods.Customer("Credit Co")), "customer")).GetGuid();
        var sale = await w.OrderAsync((await w.SellAsync(w.Order(shift, [Line(w.A, 3m, 10m)], [Pay("CASH", 35.10m)], customer: c), cashier)).G("orderUuid"));
        var invoice = (Guid)(await SapKit.WaitForAsync(
            () => _f.QueryAsync("SELECT UUID FROM finance.sales_invoices WHERE SourceUuid = @s AND Status <> 'CREDIT_NOTE'", ("@s", sale.G("uuid"))),
            r => r.Count > 0, "the POS sale's invoice")).Single()["UUID"]!;

        await k.Ok(k.Post("/api/finance/ar/credit-memo", new
        {
            customerUuid = c, originalInvoiceUuid = invoice, date = Iso(Today), currencyCode = "PKR", reason = "Price adjustment", applyToOriginal = false,
            lines = new[] { new { description = "Goodwill", amount = 30m, taxCodeUuid = (Guid?)null } }
        }), "credit memo 30 on a 35.10 invoice");

        var reasons = (await k.Ok(k.Get("/api/pos/return-reasons?includeInactive=true"), "reasons")).Items().ToDictionary(r => r.S("code")!, r => r.G("uuid"));
        var line = sale.A("lines").Single(l => l.G("variantUuid") == w.A.VariantUuid).G("uuid");
        var ret = await k.Post($"/api/pos/orders/{sale.G("uuid")}/return", new
        {
            localId = Guid.NewGuid().ToString("N"), terminalUuid = (Guid?)null, reasonUuid = reasons["DEFECTIVE"], note = (string?)null,
            authorizationToken = (string?)null, lines = new[] { new { originalLineUuid = line, quantity = 1m } }, refundMethod = "CASH",
            storeCreditCustomerUuid = (Guid?)null
        }, sup);
        // Finance side (the REV-3 fix in CounterSaleService): the credit is never booked above the invoice total.
        (await _f.RowCountAsync("SELECT COUNT(*) FROM finance.sales_invoices WHERE OriginalInvoiceUuid = @i AND SourceType <> 'AR_CREDIT_MEMO'", ("@i", invoice)))
            .Should().Be(0, "no POS credit note booked");
        Convert.ToDecimal((await _f.QueryAsync("SELECT ReturnedAmount FROM finance.sales_invoices WHERE UUID = @i", ("@i", invoice))).Single()["ReturnedAmount"])
            .Should().Be(30m, "only the memo is credited");
        if (ret.Status == HttpStatusCode.OK)
        {
            var posted = (await _f.QueryAsync("SELECT FinancePosted, PostingError FROM pos.orders WHERE Uuid = @u", ("@u", ret.Result.G("uuid")))).Single();
            posted["FinancePosted"].Should().Be(false, "Finance refused the return's credit note");
            ((string?)posted["PostingError"]).Should().Contain("above its total");
        }
        // The till itself must refuse (QA-5): otherwise the cash refund goes out and the credit note never posts.
        ret.ShouldRefuse(HttpStatusCode.BadRequest, "above its total", "REV-3 end to end: 30 + 11.70 > 35.10 is refused at the POS return");
    }

    // ── REV-4 / REV-21 (D-AP-16) ─────────────────────────────────────────────────

    [Fact]
    public async Task REV_4_past_payment_date_is_refused()
    {
        var o = await _root.NewArOrgAsync("RV4", stock: 1m);
        await o.ApprovedBillAsync(o.Vendor, 100m, 2);
        (await o.Propose(Today.AddDays(5), paymentDate: Today.AddDays(-1))).Status.Should().Be(HttpStatusCode.BadRequest, "D-AP-16: never backdated");
    }

    [Fact]
    public async Task REV_4_a_run_executed_late_pays_today_without_the_expired_discount()
    {
        var o = await _root.NewArOrgAsync("RV4L", stock: 1m);
        var term = await o.K.Ok(o.CreateTerm("2/10N30", 2m, 10, 30), "term");
        var bill = await o.ApprovedBillAsync(o.Vendor, 1_000m, 30, invoiceDate: Today.AddDays(-5));
        var schedule = await o.ScheduleOfAsync(bill);
        await o.K.Ok(o.SetScheduleTerm(schedule.G("uuid"), term.G("uuid")), "term");
        var run = await o.K.Ok(o.Propose(Today.AddDays(30)), "propose");
        run.RunItems().Single().D("discountAmount").Should().Be(20m);
        await o.K.Ok(o.ApproveRun(run.RunUuid()), "approve");

        // Time passes: the run was for 3 days ago and the discount window closed yesterday.
        await _f.ExecuteAsync("UPDATE finance.payment_runs SET PaymentDate = @d WHERE Uuid = @u", ("@d", Today.AddDays(-3)), ("@u", run.RunUuid()));
        await _f.ExecuteAsync("UPDATE finance.invoices SET InvoiceDate = @d WHERE UUID = @u", ("@d", Today.AddDays(-11)), ("@u", bill));
        await _f.ExecuteAsync("UPDATE finance.payment_schedules SET InvoiceDate = @i, DiscountDate = @d WHERE Uuid = @u",
            ("@i", Today.AddDays(-11)), ("@d", Today.AddDays(-1)), ("@u", schedule.G("uuid")));

        var done = await o.K.Ok(o.ExecuteRun(run.RunUuid()), "execute late");
        var pay = done.RunBatches().Single().G("supplierPaymentUuid");
        var row = (await _f.QueryAsync("SELECT PaymentDate, TotalAmount FROM finance.supplier_payments WHERE UUID = @u", ("@u", pay))).Single();
        Convert.ToDateTime(row["PaymentDate"]).Date.Should().Be(Today, "D-AP-16: paid today, not on the run's past date");
        Convert.ToDecimal(row["TotalAmount"]).Should().Be(1_000m, "the expired discount is dropped");
        Convert.ToDecimal((await _f.QueryAsync("SELECT SUM(l.DiscountAmount) AS D FROM finance.supplier_payment_lines l JOIN finance.supplier_payments p ON p.Id = l.SupplierPaymentId WHERE p.UUID = @u",
            ("@u", pay))).Single()["D"]).Should().Be(0m);
    }

    [Fact]
    public async Task REV_21_an_invoice_settled_meanwhile_leaves_the_run()
    {
        var o = await _root.NewArOrgAsync("RV21", stock: 1m);
        var a = await o.ApprovedBillAsync(o.Vendor, 300m, 2);
        var b = await o.ApprovedBillAsync(o.Vendor, 400m, 2);
        var run = await o.K.Ok(o.Propose(Today.AddDays(5)), "propose");
        await o.K.Ok(o.ApproveRun(run.RunUuid()), "approve");
        await o.K.PostedSupplierPaymentAsync(o.Vendor, a, 300m); // paid by hand after approval

        var done = await o.K.Ok(o.ExecuteRun(run.RunUuid()), "execute");
        done.RunStatus().Should().Be("Completed");
        (await o.LivePaymentLinesAsync(a)).Should().Be(1, "only the manual payment");
        (await o.LivePaymentLinesAsync(b)).Should().Be(1, $"the other invoice is still paid by the run — {J.Short(done.RunBatches().Single())}");
        var removed = done.A("removedItems").Concat(done.RunItems()).Where(i => i.G("invoiceUuid") == a).ToList();
        removed.Should().OnlyContain(i => i.S("status") == "Removed", "D-AP-16: the settled invoice leaves the run");
        done.Head().D("totalAmount").Should().Be(400m, "a Completed run's totals are what was paid");
    }

    // ── REV-6 (D-AP-21) ──────────────────────────────────────────────────────────

    [Fact]
    public async Task REV_6_debit_memo_cannot_be_applied_across_currencies()
    {
        var o = await _root.NewArOrgAsync("RV6", stock: 1m);
        var usd = await o.K.UsdAsync();
        await o.K.CreateRateAsync("USD", "PKR", 280m, Today.AddDays(-400));
        var usdBill = await o.K.SupplierInvoiceAsync(o.Vendor, "USD", usd, 500m);
        await o.K.Ok(o.K.TryApproveSupplierInvoiceAsync(usdBill), "approve USD bill");
        var memo = await o.K.Ok(o.DebitMemo(o.Vendor.Uuid, 100m), "PKR debit memo");
        var no = memo.S("debitNoteNumber");
        var usdNo = (await o.SupplierInvoiceDetailAsync(usdBill)).S("invoiceNumber");
        (await o.ApplyDebitMemo(memo.UuidOf(), [(usdBill, 100m)])).ShouldRefuse(HttpStatusCode.BadRequest, $"{no} is in PKR; invoice {usdNo} is in USD.",
            "REV-6: PKR 100 is never taken off a USD invoice");
        (await o.SupplierInvoiceDetailAsync(usdBill)).D("totalAmount").Should().Be(500m);
    }

    // ── REV-8 (D-AP-19) ──────────────────────────────────────────────────────────

    [Fact]
    public async Task REV_8_two_invoices_on_the_same_received_units_only_one_is_approvable()
    {
        var o = await _root.NewArOrgAsync("RV8", stock: 1m);
        var (po, poLine) = await o.OrderedAsync(o.Vendor, 10m, 300m);
        var (grn, grnLine) = await o.ReceivedAsync(po, 10m);
        var i1 = await o.SupplierInvoiceAsync(o.Vendor, $"{o.K.Marker}-ONE", po, poLine, grn, grnLine, 10m, 300m);
        var i2 = await o.SupplierInvoiceAsync(o.Vendor, $"{o.K.Marker}-TWO", po, poLine, grn, grnLine, 10m, 300m);
        (await o.K.Ok(o.Match(i1), "match 1")).S("lineMatchResult").Should().Be("Matched");
        (await o.K.Ok(o.Match(i2), "match 2")).S("lineMatchResult").Should().Be("Matched", "both see nothing invoiced yet");

        await o.K.Ok(o.ApproveMatch(i1), "approve the first");
        var second = await o.ApproveMatch(i2);
        second.ShouldRefuse(HttpStatusCode.Conflict, "no longer matches its purchase order", "D-AP-19: 20 billed on 10 received");
        (await o.SupplierInvoiceDetailAsync(i2)).S("matchStatus").Should().NotBe("Approved");
        (await o.K.Post($"{SupplierInvoices}/{i2}/approve", new { Notes = "old path" })).Status.Should().Be(HttpStatusCode.Conflict,
            "the existing approve path is gated too");
    }

    // ── REV-9 (D-AP-22) ──────────────────────────────────────────────────────────

    [Fact]
    public async Task REV_9_background_run_writes_timeline_entries_to_its_own_organization_only()
    {
        var b = await _root.NewArOrgAsync("RV9B", stock: 1m);           // another organization, present but idle
        var o = await _root.NewArOrgAsync("RV9A", stock: 1m);
        for (var i = 0; i < 21; i++) await o.ApprovedBillAsync(await o.VendorAsync($"Payee {i}"), 10m + i, 2);
        var run = (await o.K.Ok(o.Propose(Today.AddDays(5)), "propose")).RunUuid();
        await o.K.Ok(o.ApproveRun(run), "approve");
        (await o.ExecuteRun(run)).Status.Should().Be(HttpStatusCode.Accepted, "21 batches → PaymentRunExecutionJob");
        var done = await SapKit.WaitForAsync(() => o.RunAsync(run), r => r.RunStatus() != "Executing", "the job", seconds: 300);
        done.RunStatus().Should().Be("Completed");

        var payments = done.RunBatches().Select(x => x.G("supplierPaymentUuid")).ToList();
        var traces = (await _f.QueryAsync("SELECT TraceId FROM finance.supplier_payments WHERE OrganizationId = @o AND UUID IN (" +
            string.Join(",", payments.Select(p => $"'{p}'")) + ")", ("@o", o.OrgId))).Select(r => (Guid)r["TraceId"]!).ToList();
        traces.Should().HaveCount(21);
        var inList = string.Join(",", traces.Select(t => $"'{t}'"));
        var rows = await SapKit.WaitForAsync(
            () => _f.QueryAsync($"SELECT OrganizationId, Events FROM workflow_schema.document_timelines WHERE TraceId IN ({inList})"),
            r => r.Count(x => (Guid)x["OrganizationId"]! == o.OrgId && ((string)x["Events"]!).Contains("PAYMENT_POSTED")) >= 21,
            "the run payments' timeline entries", seconds: 120);
        rows.Should().OnlyContain(r => (Guid)r["OrganizationId"]! == o.OrgId, "REV-9: never stamped with another (default) organization");
        rows.Should().NotContain(r => (Guid)r["OrganizationId"]! == b.OrgId);
    }

    // ── REV-7 / D-AP-17: re-running the posting pass never double-posts ──────────

    [Fact]
    public async Task REV_7_a_second_posting_pass_does_not_double_post_the_bill_payment()
    {
        var o = await _root.NewArOrgAsync("RV7", gl: true, qbo: true, stock: 1m);
        await o.ApprovedBillAsync(o.Vendor, 800m, 2);
        var run = (await o.K.Ok(o.Propose(Today.AddDays(5)), "propose")).RunUuid();
        await o.K.Ok(o.ApproveRun(run), "approve");
        var done = await o.K.Ok(o.ExecuteRun(run), "execute");
        var payment = done.RunBatches().Single().G("supplierPaymentUuid");
        await _f.RunJobsAsync();

        async Task<(int Postings, int Journals, int Maps, int Outbox, int QboWrites)> CountsAsync() => (
            (await _f.PostingRowsSqlAsync(o.OrgId, payment)).Count,
            (await _f.JournalsAsync(o.OrgId, payment)).Count(j => j.Status is "Posted" or "Reversed"),
            await _f.RowCountAsync("SELECT COUNT(*) FROM integration.EntityMaps m JOIN integration.Connections c ON c.Id = m.ConnectionId " +
                                   "WHERE c.OrganizationId = @o AND m.ExternalId = @e", ("@o", o.OrgId), ("@e", payment.ToString())),
            await _f.RowCountAsync("SELECT COUNT(*) FROM integration.Outbox x JOIN integration.EntityMaps m ON m.Id = x.EntityMapId JOIN integration.Connections c ON c.Id = m.ConnectionId " +
                                   "WHERE c.OrganizationId = @o AND m.ExternalId = @e", ("@o", o.OrgId), ("@e", payment.ToString())),
            _f.WritesOf(SyncKind.BillPayment).Count(w => w.StartsWith("Create", StringComparison.OrdinalIgnoreCase)));

        var first = await CountsAsync();
        first.Postings.Should().Be(1, "one posting row for the BillPayment");
        first.Journals.Should().Be(1, "one GL journal");
        first.Maps.Should().BeLessThanOrEqualTo(1);

        // A crash after the batch was paid: the run is Executing again; resume publishes every Paid batch once more.
        await StickAsync(run);
        (await o.K.Ok(Resume(o, run), "resume (second posting pass)")).RunStatus().Should().Be("Completed");
        await _f.RunJobsAsync();
        var second = await CountsAsync();
        second.Should().Be(first, "D-AP-17: the gateway dedupes the republished BillPayment by its idempotency key");
        (await _f.RowCountAsync("SELECT COUNT(*) FROM finance.supplier_payments WHERE OrganizationId = @o AND Status NOT IN ('CANCELLED','BOUNCED')", ("@o", o.OrgId)))
            .Should().Be(1, "and no second payment");
    }

    private static Task<Api> Resume(ArOrg o, Guid run) => o.K.Post($"{Runs}/{run}/resume", null);
}
