using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace SMS.Integration.Tests.SapAlignment;

/// <summary>
/// Supplier-invoice hardening (purchase reviewer C', items A–E), end to end through the real host on LocalDB:
/// <list type="bullet">
/// <item><b>C</b> — the three-way match of a partial invoice is on what it bills at PO prices; billing more than was
/// received and not yet invoiced is a Variance; an invoice with no lines matches against its GRN's accepted value.</item>
/// <item><b>A</b> — two invoices of one PO approved (or one approved, one reversed) at the same moment both reach
/// the PO's invoiced quantities.</item>
/// <item><b>E</b> — only Approved invoices are payables: absent from outstanding, aging and payables lists until
/// approved, and neither payment flow pays them; a payment recorded before the rule can still be cancelled.</item>
/// <item><b>B</b> — payments and reversals of the same invoice are serialized: exactly one of a racing pair wins, and
/// two payments posted at once both count.</item>
/// </list>
/// No tax codes in this class, so every amount is net. Run alone:
/// <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~SupplierInvoiceHardeningE2ETests</c>.
/// </summary>
public sealed class SupplierInvoiceHardeningE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private const string Invoices = "/api/finance/invoices";
    private const decimal Price = 300m;

    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _k;

    public SupplierInvoiceHardeningE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _k = new SapKit(factory, "SH");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // C — three-way match on partial invoices
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task C_partial_invoices_match_on_what_they_bill_at_po_prices()
    {
        var (wh, vendor, item) = await SetupAsync();

        // A PO for 10 received in two GRNs of 5: each GRN's own invoice bills 5 at the PO price — a match.
        var po = await OrderedAsync(vendor, wh, item, 10m);
        var grn1 = await _k.ReceiveAllAsync(po, wh, qty: 5m);
        var auto1 = await AutoInvoiceAsync(grn1);
        var grn2 = await _k.ReceiveAllAsync(po, wh, qty: 5m);
        var auto2 = await AutoInvoiceAsync(grn2);
        foreach (var auto in new[] { auto1, auto2 })
        {
            var a = await GetAsync(auto);
            Console.WriteLine($"PROBE auto {a.S("invoiceNumber")}: subtotal={a.D("subtotal")} po={a.D("matchedPoValue")} grn={a.D("matchedGrnValue")} variance={a.D("varianceAmount")} {a.S("matchStatus")}");
            a.D("subtotal").Should().Be(1500m);
            a.S("matchStatus").Should().Be("Matched", "the GRN's own invoice bills the 5 received at the PO price, not half the PO");
        }

        // A second PO, 5 of 10 received; its automatic invoice is rejected so nothing else bills those 5.
        var poB = await OrderedAsync(vendor, wh, item, 10m);
        var grnB = await _k.ReceiveAllAsync(poB, wh, qty: 5m);
        await Reject(await AutoInvoiceAsync(grnB));
        var poLine  = (await _k.Ok(_k.Get($"/api/purchase-orders/{poB}"), "read PO")).A("lines").Single().G("uuid");
        var grnLine = (await _k.Ok(_k.Get($"/api/grns/{grnB}"), "read GRN")).A("lines").Single().G("uuid");

        async Task<JsonElement> Billing(string label, decimal qty, decimal price, bool lines = true) =>
            await GetAsync(await CreateAsync(new
            {
                SupplierInvoiceNo = $"{_k.Marker}-{label}", SupplierId = vendor.Uuid, PoUuid = poB, GrnUuid = grnB,
                InvoiceDate = Today, ReceivedDate = Today, DueDate = Today.AddDays(30), Currency = "PKR",
                Subtotal = qty * price, TaxAmount = 0m,
                Lines = lines
                    ? new[] { new { PoLineUuid = poLine, GrnLineUuid = grnLine, ItemDescription = item.Name, QtyInvoiced = qty, UnitPrice = price } }
                    : null
            }));

        var six = await Billing("SIX", 6m, Price);
        six.S("matchStatus").Should().Be("Variance", $"6 billed against 5 received — {Probe(six)}");
        await Reject(six.G("uuid"));

        var dear = await Billing("DEAR", 5m, 330m);
        dear.S("matchStatus").Should().Be("Variance", $"5 billed at 330 against a PO price of 300 — {Probe(dear)}");
        await Reject(dear.G("uuid"));

        var headerOnly = await Billing("HDR", 5m, Price, lines: false);
        headerOnly.S("matchStatus").Should().Be("Matched", $"no lines: matched against the GRN's accepted value, 1,500 — {Probe(headerOnly)}");
        await Reject(headerOnly.G("uuid"));

        var five = await Billing("FIVE", 5m, Price);
        five.S("matchStatus").Should().Be("Matched", $"5 billed at the PO price against 5 received — {Probe(five)}");
        await Approve(five.G("uuid"));
        (await PoQtyInvoicedAsync(poB)).Should().Be(5m);

        var oneMore = await Billing("ONE", 1m, Price);
        oneMore.S("matchStatus").Should().Be("Variance", $"all 5 received are already invoiced — {Probe(oneMore)}");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // A — concurrent approvals / reversals of different invoices on one PO
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task A_two_invoices_on_one_po_approved_at_the_same_moment_both_reach_the_po()
    {
        var (wh, vendor, item) = await SetupAsync();

        for (var round = 1; round <= 3; round++)
        {
            var (po, inv1, inv2) = await PoWithTwoInvoicesAsync(vendor, wh, item);

            var results = await Task.WhenAll(
                _k.Post($"{Invoices}/{inv1}/approve", new { Notes = "a" }),
                _k.Post($"{Invoices}/{inv2}/approve", new { Notes = "b" }));
            results.Should().OnlyContain(r => r.Status == HttpStatusCode.OK, $"round {round}: {string.Join(" | ", results.Select(r => r.ToString()))}");

            (await PoQtyInvoicedAsync(po)).Should().Be(10m, $"round {round}: both approvals reach the PO line (5 + 5)");
            (await PoStatusAsync(po)).Should().Be("CLOSED", $"round {round}");
            (await ApprovalDebitsAsync(inv1)).Should().Be(1);
            (await ApprovalDebitsAsync(inv2)).Should().Be(1);
        }
    }

    [Fact]
    public async Task A_one_invoice_approved_while_another_on_the_same_po_is_reversed_both_reach_the_po()
    {
        var (wh, vendor, item) = await SetupAsync();

        for (var round = 1; round <= 3; round++)
        {
            var (po, inv1, inv2) = await PoWithTwoInvoicesAsync(vendor, wh, item);
            await Approve(inv1);
            (await PoQtyInvoicedAsync(po)).Should().Be(5m);

            var results = await Task.WhenAll(
                _k.Post($"{Invoices}/{inv2}/approve", new { Notes = "approve" }),
                _k.Post($"{Invoices}/{inv1}/reverse", new { Reason = $"round {round}" }));
            results.Should().OnlyContain(r => r.Status == HttpStatusCode.OK, $"round {round}: {string.Join(" | ", results.Select(r => r.ToString()))}");

            (await PoQtyInvoicedAsync(po)).Should().Be(5m, $"round {round}: +5 for the approval, −5 for the reversal, neither lost");
            (await PoStatusAsync(po)).Should().Be("PARTIALLY_INVOICED", $"round {round}");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // E — only Approved invoices are payables
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task E_only_approved_invoices_are_payables_and_payable()
    {
        var (wh, _, item) = await SetupAsync();
        var vendor = await _k.CreateVendorAsync("Payables Vendor");

        // Pending (no PO), Matched (auto-invoice of a received PO) and Variance (billed over the PO).
        var pending = await CreateAsync(Direct(vendor, 1000m, "PEND"));
        var po = await OrderedAsync(vendor, wh, item, 10m);
        var grn = await _k.ReceiveAllAsync(po, wh);
        var matched = await AutoInvoiceAsync(grn);
        var variance = await CreateAsync(new
        {
            SupplierInvoiceNo = $"{_k.Marker}-VAR", SupplierId = vendor.Uuid, PoUuid = po, GrnUuid = grn,
            InvoiceDate = Today, ReceivedDate = Today, DueDate = Today.AddDays(30), Currency = "PKR", Subtotal = 4000m, TaxAmount = 0m
        });
        (await GetAsync(pending)).S("matchStatus").Should().Be("Pending");
        (await GetAsync(matched)).S("matchStatus").Should().Be("Matched");
        (await GetAsync(variance)).S("matchStatus").Should().Be("Variance");

        foreach (var (inv, what) in new[] { (pending, "Pending"), (matched, "Matched"), (variance, "Variance") })
        {
            var number = (await GetAsync(inv)).S("invoiceNumber")!;
            var seen = await PayablesViewsAsync(vendor, inv, number);
            seen.Where(v => v.Value).Select(v => v.Key).Should().BeEmpty($"a {what} invoice is not a payable yet");

            var sp = await PaySupplierAsync(vendor, inv, 100m);
            sp.Status.Should().Be(HttpStatusCode.BadRequest, $"a {what} invoice cannot be put on a supplier payment — {sp}");
            sp.Message.Should().Contain("not approved", "the refusal says why");
            var legacy = await PayLegacyAsync(inv, 100m);
            legacy.Status.Should().Be(HttpStatusCode.BadRequest, $"a {what} invoice cannot be paid through the single-invoice flow — {legacy}");
        }
        (await LivePaymentsAsync(pending)).Should().Be(0);

        // Approved: now a payable, everywhere, and payable through both flows.
        await Approve(pending);
        await Approve(matched);
        var pendingNo = (await GetAsync(pending)).S("invoiceNumber")!;
        var nowSeen = await PayablesViewsAsync(vendor, pending, pendingNo);
        nowSeen.Where(v => !v.Value).Select(v => v.Key).Should().BeEmpty("an approved invoice is a payable in every list");

        var paid400 = (await _k.Ok(PaySupplierAsync(vendor, pending, 400m), "an approved invoice can be paid")).GetGuid();
        await _k.Ok(_k.Post($"/api/supplier-payments/{paid400}/approve"), "approve");
        await _k.Ok(_k.Post($"/api/supplier-payments/{paid400}/post"), "post");
        var agingRow = (await _k.Ok(_k.Get("/api/reports/invoice-aging"), "Reports invoice aging")).A("items").Single(i => i.S("invoiceNumber") == pendingNo);
        agingRow.D("outstandingAmount").Should().Be(600m, $"the report ages what is still owed: 1,000 − 400 paid — {J.Short(agingRow)}");
        var legacyOk = await PayLegacyAsync(matched, 1000m);
        legacyOk.Status.Should().Be(HttpStatusCode.OK, $"the single-invoice flow pays an approved invoice — {legacyOk}");
    }

    [Fact]
    public async Task E_a_payment_recorded_against_an_unapproved_invoice_before_the_rule_can_still_be_cancelled()
    {
        await SetupAsync();
        var vendor = await _k.CreateVendorAsync("Legacy Payments Vendor");

        // Recorded while the invoice was approved, then the invoice is put back to Pending in SQL: the state an
        // organization has when its payments were taken before only Approved invoices could be paid.
        var draftInv    = await ApprovedDirectAsync(vendor, 1000m, "OLD1");
        var approvedInv = await ApprovedDirectAsync(vendor, 1000m, "OLD2");
        var legacyInv   = await ApprovedDirectAsync(vendor, 1000m, "OLD3");

        var draftPayment    = (await _k.Ok(PaySupplierAsync(vendor, draftInv, 500m), "draft payment")).GetGuid();
        var approvedPayment = (await _k.Ok(PaySupplierAsync(vendor, approvedInv, 500m), "payment to approve")).GetGuid();
        await _k.Ok(_k.Post($"/api/supplier-payments/{approvedPayment}/approve"), "approve payment");
        var legacyPayment   = (await _k.Ok(PayLegacyAsync(legacyInv, 500m), "legacy payment")).GetGuid();

        foreach (var inv in new[] { draftInv, approvedInv, legacyInv })
            await _f.ExecuteAsync("UPDATE finance.invoices SET MatchStatus = 'Pending', ApprovedAt = NULL, ApprovedBy = NULL WHERE UUID = @u", ("@u", inv));

        var post = await _k.Post($"/api/supplier-payments/{approvedPayment}/post");
        post.Status.Should().Be(HttpStatusCode.Conflict, $"no money moves to an invoice that is not approved — {post}");
        (await GetAsync(approvedInv)).D("paidAmount").Should().Be(0m);

        (await _k.Post($"/api/supplier-payments/{draftPayment}/cancel")).Status.Should().Be(HttpStatusCode.OK, "a draft can still be cancelled");
        (await _k.Post($"/api/supplier-payments/{approvedPayment}/cancel")).Status.Should().Be(HttpStatusCode.OK, "an approved one too");
        var reversed = await _k.Patch($"/api/finance/payments/{legacyPayment}", new { Status = "Reversed" });
        reversed.Status.Should().Be(HttpStatusCode.OK, $"a single-invoice payment can still be reversed — {reversed}");
        (await _k.Patch($"/api/finance/payments/{legacyPayment}", new { Status = "Processed" })).Status
            .Should().Be(HttpStatusCode.Conflict, "a payment's status only moves forward; nothing comes out of Reversed");
        (await _k.Patch($"/api/finance/payments/{legacyPayment}", new { Status = "Bogus" })).Status
            .Should().Be(HttpStatusCode.BadRequest, "an unknown status");

        (await LivePaymentsAsync(draftInv)).Should().Be(0);
        (await LivePaymentsAsync(approvedInv)).Should().Be(0);
        (await LivePaymentsAsync(legacyInv)).Should().Be(0);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // B — payments and reversals of the same invoice are serialized
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task B_a_payment_racing_a_reversal_of_the_same_invoice_ends_with_exactly_one_winner()
    {
        await SetupAsync();
        var vendor = await _k.CreateVendorAsync("Race Vendor");
        var outcomes = new List<string>();

        for (var round = 1; round <= 10; round++)
        {
            var inv = await ApprovedDirectAsync(vendor, 1000m, $"RACE{round}");
            var legacyFlow = round % 2 == 0;

            var pay     = legacyFlow ? PayLegacyAsync(inv, 1000m) : PaySupplierAsync(vendor, inv, 1000m);
            var reverse = _k.Post($"{Invoices}/{inv}/reverse", new { Reason = $"race {round}" });
            await Task.WhenAll(pay, reverse);
            var (p, r) = (await pay, await reverse);

            var paid = p.Status == HttpStatusCode.OK;
            var rev  = r.Status == HttpStatusCode.OK;
            outcomes.Add($"{(legacyFlow ? "legacy" : "supplier")}:{(paid ? "payment" : "")}{(rev ? "reversal" : "")}");

            (paid ^ rev).Should().BeTrue($"round {round} ({(legacyFlow ? "single-invoice" : "supplier")} payment): exactly one wins — payment {p} / reversal {r}");

            var status = (await GetAsync(inv)).S("matchStatus");
            var live   = await LivePaymentsAsync(inv);
            if (rev)
            {
                status.Should().Be("Reversed");
                live.Should().Be(0, $"round {round}: a reversed invoice carries no live payment");
            }
            else
            {
                status.Should().Be("Approved");
                live.Should().Be(1);
                (await ReversalCreditsAsync(inv)).Should().Be(0);
            }
        }
        Console.WriteLine($"PROBE race outcomes: {string.Join(", ", outcomes)}");
    }

    [Fact]
    public async Task B_two_payments_of_one_invoice_posted_at_once_both_count_and_over_allocation_is_refused()
    {
        await SetupAsync();
        var vendor = await _k.CreateVendorAsync("Posting Vendor");

        for (var round = 1; round <= 3; round++)
        {
            // Two halves, posted at the same moment: both reach PaidAmount.
            var inv = await ApprovedDirectAsync(vendor, 1000m, $"POST{round}");
            var p1 = (await _k.Ok(PaySupplierAsync(vendor, inv, 500m), "half 1")).GetGuid();
            var p2 = (await _k.Ok(PaySupplierAsync(vendor, inv, 500m), "half 2")).GetGuid();
            await _k.Ok(_k.Post($"/api/supplier-payments/{p1}/approve"), "approve 1");
            await _k.Ok(_k.Post($"/api/supplier-payments/{p2}/approve"), "approve 2");

            var posts = await Task.WhenAll(_k.Post($"/api/supplier-payments/{p1}/post"), _k.Post($"/api/supplier-payments/{p2}/post"));
            posts.Should().OnlyContain(r => r.Status == HttpStatusCode.OK, $"round {round}: {string.Join(" | ", posts.Select(r => r.ToString()))}");
            var after = await GetAsync(inv);
            after.D("paidAmount").Should().Be(1000m, $"round {round}: neither posting overwrote the other");
            after.S("paymentStatus").Should().Be("FULLY_PAID");

            // Two payments of the whole amount created at once: only one fits.
            var inv2 = await ApprovedDirectAsync(vendor, 1000m, $"OVER{round}");
            var creates = await Task.WhenAll(PaySupplierAsync(vendor, inv2, 1000m), PaySupplierAsync(vendor, inv2, 1000m));
            creates.Count(c => c.Status == HttpStatusCode.OK).Should().Be(1,
                $"round {round}: the second would allocate more than is outstanding — {string.Join(" | ", creates.Select(c => c.ToString()))}");
        }

        // Two multi-invoice payments naming the same two invoices in opposite orders: no deadlock, both fit.
        var i1 = await ApprovedDirectAsync(vendor, 1000m, "X1");
        var i2 = await ApprovedDirectAsync(vendor, 1000m, "X2");
        var crossed = await Task.WhenAll(
            _k.Post("/api/supplier-payments", Payment(vendor, (i1, 300m), (i2, 300m))),
            _k.Post("/api/supplier-payments", Payment(vendor, (i2, 300m), (i1, 300m))));
        crossed.Should().OnlyContain(r => r.Status == HttpStatusCode.OK, string.Join(" | ", crossed.Select(r => r.ToString())));
        foreach (var c in crossed) await _k.Ok(_k.Post($"/api/supplier-payments/{c.Result.GetGuid()}/approve"), "approve crossed");
        var crossedPosts = await Task.WhenAll(crossed.Select(c => _k.Post($"/api/supplier-payments/{c.Result.GetGuid()}/post")));
        crossedPosts.Should().OnlyContain(r => r.Status == HttpStatusCode.OK, string.Join(" | ", crossedPosts.Select(r => r.ToString())));
        (await GetAsync(i1)).D("paidAmount").Should().Be(600m);
        (await GetAsync(i2)).D("paidAmount").Should().Be(600m);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Payment permissions (PAYMENT_PROCESS to change, PAYMENT_VIEW to read, PAYMENT_APPROVE to approve)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Payments_need_the_payment_permissions()
    {
        await SetupAsync();
        var vendor = await _k.CreateVendorAsync("Permission Vendor");
        var inv = await ApprovedDirectAsync(vendor, 1000m, "PERM");
        var payment = (await _k.Ok(PaySupplierAsync(vendor, inv, 100m), "admin payment")).GetGuid();
        var legacy  = (await _k.Ok(PayLegacyAsync(inv, 100m), "admin legacy payment")).GetGuid();

        var auditor   = await _k.LoginAsNewUserAsync((int)SMS.Shared.Common.EnumRole.Auditor, "pay-auditor");      // PAYMENT_VIEW only
        var requester = await _k.LoginAsNewUserAsync((int)SMS.Shared.Common.EnumRole.Requester, "pay-requester");  // neither
        var manager   = await _k.LoginAsNewUserAsync((int)SMS.Shared.Common.EnumRole.FinanceManager, "pay-manager"); // all three

        (await _k.Get($"/api/supplier-payments/{payment}", auditor)).Status.Should().Be(HttpStatusCode.OK, "PAYMENT_VIEW reads");
        (await _k.Get($"/api/finance/payments/{legacy}", auditor)).Status.Should().Be(HttpStatusCode.OK);
        (await _k.Get($"/api/supplier-payments/{payment}", requester)).Status.Should().Be(HttpStatusCode.Forbidden, "no PAYMENT_VIEW");
        (await _k.Get($"/api/finance/payments/{legacy}", requester)).Status.Should().Be(HttpStatusCode.Forbidden);

        (await _k.Post("/api/supplier-payments", Payment(vendor, (inv, 50m)), auditor)).Status.Should().Be(HttpStatusCode.Forbidden, "creating needs PAYMENT_PROCESS");
        (await _k.Post("/api/finance/payments", new { InvoiceUuid = inv, PaymentDate = Today, AmountPaid = 50m, PaymentMethod = "CASH" }, auditor))
            .Status.Should().Be(HttpStatusCode.Forbidden);
        (await _k.Post($"/api/supplier-payments/{payment}/approve", null, auditor)).Status.Should().Be(HttpStatusCode.Forbidden, "approving needs PAYMENT_APPROVE");
        (await _k.Post($"/api/supplier-payments/{payment}/cancel", null, auditor)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await _k.Post($"/api/supplier-payments/{payment}/post", null, auditor)).Status.Should().Be(HttpStatusCode.Forbidden);
        (await _k.Patch($"/api/finance/payments/{legacy}", new { Status = "Reversed" }, auditor)).Status.Should().Be(HttpStatusCode.Forbidden);

        var managed = await _k.Post("/api/supplier-payments", Payment(vendor, (inv, 50m)), manager);
        managed.Status.Should().Be(HttpStatusCode.OK, $"a Finance Manager holds PAYMENT_PROCESS — {managed}");
        (await _k.Post($"/api/supplier-payments/{managed.Result.GetGuid()}/approve", null, manager)).Status.Should().Be(HttpStatusCode.OK);
        (await _k.Post($"/api/supplier-payments/{managed.Result.GetGuid()}/post", null, manager)).Status.Should().Be(HttpStatusCode.OK);
        (await GetAsync(inv)).D("paidAmount").Should().Be(50m);

        // A single-invoice payment's status moves forward only.
        (await _k.Patch($"/api/finance/payments/{legacy}", new { Status = "Processed" }, manager)).Status.Should().Be(HttpStatusCode.OK);
        (await _k.Patch($"/api/finance/payments/{legacy}", new { Status = "Cleared" }, manager)).Status.Should().Be(HttpStatusCode.OK);
        (await _k.Patch($"/api/finance/payments/{legacy}", new { Status = "Pending" }, manager)).Status
            .Should().Be(HttpStatusCode.Conflict, "a cleared payment does not go back to pending");
    }

    // ── setup helpers ────────────────────────────────────────────────────────────

    private static DateTime Today => DateTime.UtcNow.Date;

    private async Task<(Warehouse Wh, Partner Vendor, Product Item)> SetupAsync()
    {
        var pkr = await _k.EnsureCurrencyAsync("PKR", "Pakistani Rupee", "Rs");
        await _k.SetBaseCurrencyAsync(pkr);
        await _k.CreateApproverPlaceholdersAsync();
        var wh = await _k.CreateWarehouseAsync();
        var vendor = await _k.CreateVendorAsync("Hardening Vendor");
        var item = await _k.CreateProductAsync("Hardening Item", purchasePrice: Price, sellingPrice: 400m);
        return (wh, vendor, item);
    }

    private async Task<Guid> OrderedAsync(Partner vendor, Warehouse wh, Product item, decimal qty)
    {
        var po = await _k.CreatePurchaseOrderAsync(vendor, wh, (item, qty, Price));
        await _k.SubmitApproveAndSendPoAsync(po);
        return po;
    }

    /// <summary>A PO for 10 received in two GRNs of 5, and the invoice each GRN raised by itself.</summary>
    private async Task<(Guid Po, Guid Inv1, Guid Inv2)> PoWithTwoInvoicesAsync(Partner vendor, Warehouse wh, Product item)
    {
        var po = await OrderedAsync(vendor, wh, item, 10m);
        var inv1 = await AutoInvoiceAsync(await _k.ReceiveAllAsync(po, wh, qty: 5m));
        var inv2 = await AutoInvoiceAsync(await _k.ReceiveAllAsync(po, wh, qty: 5m));
        (await PoQtyInvoicedAsync(po)).Should().Be(0m);
        return (po, inv1, inv2);
    }

    /// <summary>The invoice GRN approval raised in the background (Hangfire).</summary>
    private async Task<Guid> AutoInvoiceAsync(Guid grn)
    {
        var rows = await SapKit.WaitForAsync(
            () => _f.QueryAsync("SELECT UUID FROM finance.invoices WHERE GrnUuid = @g AND IsDelete = 0", ("@g", grn)),
            r => r.Count > 0, $"the invoice auto-created from GRN {grn}");
        return (Guid)rows.Single()["UUID"]!;
    }

    private object Direct(Partner vendor, decimal subtotal, string tag) => new
    {
        SupplierInvoiceNo = $"{_k.Marker}-{tag}", SupplierId = vendor.Uuid,
        InvoiceDate = Today, ReceivedDate = Today, DueDate = Today.AddDays(30), Currency = "PKR", Subtotal = subtotal, TaxAmount = 0m
    };

    private async Task<Guid> ApprovedDirectAsync(Partner vendor, decimal subtotal, string tag)
    {
        var inv = await CreateAsync(Direct(vendor, subtotal, tag));
        await Approve(inv);
        return inv;
    }

    private async Task<Guid> CreateAsync(object body) => (await _k.Ok(_k.Post(Invoices, body), "create supplier invoice")).GetGuid();
    private Task<JsonElement> GetAsync(Guid uuid) => _k.Ok(_k.Get($"{Invoices}/{uuid}"), "read supplier invoice");
    private async Task Approve(Guid uuid) => await _k.Ok(_k.Post($"{Invoices}/{uuid}/approve", new { Notes = "ok" }), "approve supplier invoice");
    private async Task Reject(Guid uuid) => await _k.Ok(_k.Post($"{Invoices}/{uuid}/reject", new { Reason = "set aside by the test" }), "reject supplier invoice");

    private static string Probe(JsonElement inv) =>
        $"{inv.S("invoiceNumber")}: subtotal={inv.D("subtotal")} po={inv.D("matchedPoValue")} grn={inv.D("matchedGrnValue")} variance={inv.D("varianceAmount")} {inv.S("matchStatus")}";

    private static object Payment(Partner vendor, params (Guid Invoice, decimal Amount)[] lines) => new
    {
        SupplierId = vendor.Uuid, SupplierName = vendor.Name, PaymentDate = Today, PaymentMethod = "CASH",
        TotalAmount = lines.Sum(l => l.Amount),
        Lines = lines.Select(l => new { InvoiceUuid = l.Invoice, AllocatedAmount = l.Amount }).ToArray()
    };

    private Task<Api> PaySupplierAsync(Partner vendor, Guid invoice, decimal amount) =>
        _k.Post("/api/supplier-payments", Payment(vendor, (invoice, amount)));

    private Task<Api> PayLegacyAsync(Guid invoice, decimal amount) =>
        _k.Post("/api/finance/payments", new { InvoiceUuid = invoice, PaymentDate = Today, AmountPaid = amount, PaymentMethod = "CASH" });

    // ── read-side helpers ────────────────────────────────────────────────────────

    /// <summary>Whether the invoice shows in each place payables are listed.</summary>
    private async Task<Dictionary<string, bool>> PayablesViewsAsync(Partner vendor, Guid invoice, string number)
    {
        var id = invoice.ToString();
        var outstanding = await _k.Ok(_k.Get($"/api/suppliers/{vendor.Uuid}/outstanding-invoices"), "outstanding invoices");
        var aging       = await _k.Ok(_k.Get($"/api/suppliers/{vendor.Uuid}/aging"), "supplier aging");
        var crossAging  = await _k.Ok(_k.Get("/api/reports/supplier-aging"), "cross-supplier aging");
        var payables    = await _k.Ok(_k.Get("/api/reports/outstanding-payables?page=1&pageSize=100"), "outstanding payables");
        var reportAging = await _k.Ok(_k.Get("/api/reports/invoice-aging"), "Reports invoice aging");

        return new Dictionary<string, bool>
        {
            ["outstanding-invoices"]  = outstanding.Items().Any(i => i.G("invoiceUuid") == invoice),
            ["supplier aging"]        = aging.GetRawText().Contains(id, StringComparison.OrdinalIgnoreCase),
            ["cross-supplier aging"]  = crossAging.GetRawText().Contains(vendor.Uuid.ToString(), StringComparison.OrdinalIgnoreCase),
            ["outstanding payables"]  = payables.GetRawText().Contains(id, StringComparison.OrdinalIgnoreCase),
            ["Reports invoice aging"] = reportAging.A("items").Any(i => i.S("invoiceNumber") == number)
        };
    }

    /// <summary>Supplier-payment lines (not cancelled or bounced) plus single-invoice payments (not reversed) naming the invoice.</summary>
    private async Task<int> LivePaymentsAsync(Guid invoice)
    {
        var lines = (int)(await _f.QueryAsync(
            "SELECT COUNT(*) AS N FROM finance.supplier_payment_lines l JOIN finance.supplier_payments p ON p.Id = l.SupplierPaymentId " +
            "WHERE l.InvoiceUuid = @i AND p.Status NOT IN ('CANCELLED', 'BOUNCED')", ("@i", invoice))).Single()["N"]!;
        var legacy = (int)(await _f.QueryAsync(
            "SELECT COUNT(*) AS N FROM finance.payments WHERE InvoiceUuid = @i AND IsDelete = 0 AND Status <> 'Reversed'", ("@i", invoice))).Single()["N"]!;
        return lines + legacy;
    }

    private async Task<int> ApprovalDebitsAsync(Guid invoice) =>
        (int)(await _f.QueryAsync(
            "SELECT COUNT(*) AS N FROM finance.supplier_ledger_entries WHERE ReferenceId = @i AND TransactionType = 'INVOICE_APPROVED'", ("@i", invoice)))
        .Single()["N"]!;

    private async Task<int> ReversalCreditsAsync(Guid invoice) =>
        (int)(await _f.QueryAsync(
            "SELECT COUNT(*) AS N FROM finance.supplier_ledger_entries WHERE ReferenceId = @i AND TransactionType = 'INVOICE_REVERSED'", ("@i", invoice)))
        .Single()["N"]!;

    private async Task<decimal> PoQtyInvoicedAsync(Guid po) =>
        (decimal)(await _f.QueryAsync(
            "SELECT SUM(l.QtyInvoiced) AS Q FROM demand.purchase_order_lines l JOIN demand.purchase_orders p ON p.Id = l.PurchaseOrderId WHERE p.UUID = @p",
            ("@p", po))).Single()["Q"]!;

    private async Task<string> PoStatusAsync(Guid po) => (await _k.Ok(_k.Get($"/api/purchase-orders/{po}"), "read PO")).S("status")!;
}
