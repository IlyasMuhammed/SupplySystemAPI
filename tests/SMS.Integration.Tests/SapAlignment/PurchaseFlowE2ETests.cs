using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace SMS.Integration.Tests.SapAlignment;

/// <summary>
/// SAP alignment scenario 3 — supplier invoices through the real host: a purchase tax code works the tax out
/// from the net Subtotal (S-3), the three-way match compares net with net (S-8), approval snapshots the rate
/// (S-5) and happens once, an approved invoice's tax and match status are frozen, and it is reversed — the
/// opposite ledger entry, the PO's invoiced quantities rolled back — rather than edited (S-7).
/// <para>Run alone: <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~PurchaseFlowE2ETests</c>.</para>
/// </summary>
public sealed class PurchaseFlowE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private const string Invoices = "/api/finance/invoices";

    private readonly SapKit _k;
    private readonly SapWebApplicationFactory _f;

    public PurchaseFlowE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _k = new SapKit(factory, "PF");
    }

    [Fact]
    public async Task A_coded_supplier_invoice_is_approved_once_frozen_and_reversed_by_an_opposite_ledger_entry()
    {
        var today = DateTime.UtcNow.Date;
        var pkr = await _k.EnsureCurrencyAsync("PKR", "Pakistani Rupee", "Rs");
        await _k.EnsureCurrencyAsync("USD", "US Dollar", "$");
        await _k.EnsureCurrencyAsync("EUR", "Euro", "€");
        await _k.SetBaseCurrencyAsync(pkr);
        var pgst17 = await _k.EnsureTaxCodeAsync("PGST17", 17m, "PURCHASE", isDefault: true);
        var gst17  = await _k.EnsureTaxCodeAsync("GST17", 17m, "SALES", isDefault: true);
        var exempt = await _k.EnsureTaxCodeAsync("EXEMPT", 0m, "BOTH", isDefault: false);
        var retired = (await _k.CreateTaxCodeAsync("PRETIRED", 5m, "PURCHASE", isDefault: false)).G("uuid");
        await _k.Ok(_k.Put($"/api/finance/tax-codes/{retired}", new
        {
            code = "PRETIRED", name = "retired", ratePercent = 5m, usage = "PURCHASE", isDefault = false, isActive = false
        }), "deactivate PRETIRED");
        await _k.CreateRateAsync("USD", "PKR", 280m, today.AddDays(-30));

        var vendor = await _k.CreateVendorAsync("Coded Vendor");

        // ── The code decides the tax; the amount sent is ignored ─────────────────
        var coded = await CreateAsync(Direct(vendor, "PKR", 1000m, taxAmount: 999m, taxCode: pgst17));
        var inv = await GetAsync(coded);
        (inv.D("subtotal"), inv.D("taxAmount"), inv.D("totalAmount")).Should().Be((1000m, 170m, 1170m));
        (inv.S("taxCode"), inv.ND("taxPercent"), inv.NG("taxCodeUuid")).Should().Be(("PGST17", (decimal?)17m, (Guid?)pgst17));
        inv.S("matchStatus").Should().Be("Pending", "with no purchase order there is nothing to match");
        inv.ND("exchangeRate").Should().BeNull("the rate is snapshotted at approval");

        var manual = await GetAsync(await CreateAsync(Direct(vendor, "PKR", 400m, taxAmount: 50m)));
        (manual.D("taxAmount"), manual.D("totalAmount"), manual.S("taxCode")).Should().Be((50m, 450m, (string?)null), "no code: the amount as entered");

        foreach (var (code, why) in new[] { (gst17, "a SALES-only code"), (retired, "an inactive code"), (Guid.NewGuid(), "an unknown code") })
        {
            var refused = await _k.Post(Invoices, Direct(vendor, "PKR", 100m, taxAmount: 0m, taxCode: code));
            refused.Status.Should().Be(HttpStatusCode.BadRequest, $"{why} is refused on a supplier invoice — {refused}");
        }

        // Before approval the tax can still change: a code recomputes it, a bare amount is refused while a code applies.
        await _k.Ok(_k.Patch($"{Invoices}/{manual.G("uuid")}", new { TaxCodeUuid = exempt }), "apply EXEMPT before approval");
        (await GetAsync(manual.G("uuid"))).D("totalAmount").Should().Be(400m);
        (await _k.Patch($"{Invoices}/{manual.G("uuid")}", new { TaxAmount = 10m })).Status
            .Should().Be(HttpStatusCode.BadRequest, "the tax comes from a code; remove the code to type an amount");

        // ── Approve: snapshot, one ledger debit ──────────────────────────────────
        await Approve(coded);
        inv = await GetAsync(coded);
        inv.S("matchStatus").Should().Be("Approved");
        (inv.ND("exchangeRate"), inv.S("baseCurrencyCode"), inv.ND("baseTotalAmount")).Should().Be(((decimal?)1m, "PKR", (decimal?)1170m));

        var reApprove = await _k.Post($"{Invoices}/{coded}/approve", new { Notes = "again" });
        reApprove.Status.Should().Be(HttpStatusCode.Conflict, $"approving twice would book it twice — {reApprove}");
        (await LedgerAsync(coded)).Should().Equal([("INVOICE_APPROVED", 1170m, 0m)], "exactly one debit for the approval");
        (await MasterLedgerAsync(coded)).Should().Equal([("INVOICE_APPROVED", 1170m, 0m)], "mirrored once on the master ledger");

        // ── Approved: tax and match status are frozen; harmless fields still change ──
        foreach (var (body, why) in new (object, string)[]
                 {
                     (new { TaxAmount = 10m }, "a tax amount"),
                     (new { TaxCodeUuid = exempt }, "another tax code"),
                     (new { TaxCodeUuid = Guid.Empty }, "removing the code"),
                     (new { MatchStatus = "Matched" }, "the match status"),
                     (new { MatchStatus = "Approved" }, "approving by edit"),
                     (new { MatchStatus = "pending" }, "un-approving by edit, any case"),
                 })
        {
            var refused = await _k.Patch($"{Invoices}/{coded}", body);
            refused.Status.Should().Be(HttpStatusCode.Conflict, $"{why} cannot change once approved — {refused}");
        }
        await _k.Ok(_k.Patch($"{Invoices}/{coded}", new { Notes = "checked by AP", DueDate = today.AddDays(45) }), "notes and due date still change");
        await _k.Ok(_k.Patch($"{Invoices}/{coded}", new { TaxCodeUuid = pgst17, TaxAmount = 170m, Notes = "resent form" }),
            "a whole form resent with the tax it already has is not a change");
        await _k.Ok(_k.Patch($"{Invoices}/{coded}", new { TaxAmount = 170m }), "the same tax amount again is not a change either");
        (await GetAsync(coded)).D("totalAmount").Should().Be(1170m);
        (await _k.Post($"{Invoices}/{coded}/reject", new { Reason = "no" })).Status
            .Should().Be(HttpStatusCode.Conflict, "an approved invoice is reversed, not rejected");

        // ── Reverse: the opposite entry, status Reversed ─────────────────────────
        (await _k.Post($"{Invoices}/{coded}/reverse", new { Reason = "" })).Status.Should().Be(HttpStatusCode.BadRequest, "a reason is required");
        var reversed = await _k.Ok(_k.Post($"{Invoices}/{coded}/reverse", new { Reason = "Supplier sent a corrected invoice" }), "reverse");
        reversed.S("matchStatus").Should().Be("Reversed");
        reversed.S("reversalReason").Should().Be("Supplier sent a corrected invoice");
        reversed.IsNull("reversedAt").Should().BeFalse();
        reversed.D("totalAmount").Should().Be(1170m, "nothing on the invoice is edited");

        (await LedgerAsync(coded)).Should().Equal([("INVOICE_APPROVED", 1170m, 0m), ("INVOICE_REVERSED", 0m, 1170m)]);
        (await MasterLedgerAsync(coded)).Should().Equal([("INVOICE_APPROVED", 1170m, 0m), ("INVOICE_REVERSED", 0m, 1170m)]);
        var balance = await _k.Ok(_k.Get($"/api/suppliers/{vendor.Uuid}/balance"), "supplier balance");
        J.Short(balance).Should().NotBeNullOrEmpty();
        (await SupplierRunningBalanceAsync(vendor.Uuid)).Should().Be(0m, "the reversal cancels the approval on the supplier's account");

        // Nothing is owed on it any more, wherever payables are listed.
        (await _k.Ok(_k.Get($"/api/suppliers/{vendor.Uuid}/outstanding-invoices"), "outstanding invoices")).Items()
            .Should().NotContain(i => i.G("invoiceUuid") == coded);
        J.Short(await _k.Ok(_k.Get($"/api/suppliers/{vendor.Uuid}/aging"), "supplier aging")).Should().NotContain(coded.ToString());
        J.Short(await _k.Ok(_k.Get("/api/reports/outstanding-payables?page=1&pageSize=100"), "outstanding payables")).Should().NotContain(coded.ToString());
        var payReversed = await _k.Post("/api/supplier-payments", new
        {
            SupplierId = vendor.Uuid, SupplierName = vendor.Name, PaymentDate = today, PaymentMethod = "CASH", TotalAmount = 1170m,
            Lines = new[] { new { InvoiceUuid = coded, AllocatedAmount = 1170m } }
        });
        payReversed.Status.Should().Be(HttpStatusCode.BadRequest, $"a reversed invoice cannot be paid — {payReversed}");

        (await _k.Post($"{Invoices}/{coded}/reverse", new { Reason = "again" })).Status.Should().Be(HttpStatusCode.Conflict, "already reversed");
        (await _k.Post($"{Invoices}/{coded}/approve", new { })).Status.Should().Be(HttpStatusCode.Conflict, "a reversed invoice is not re-approved");
        (await _k.Patch($"{Invoices}/{coded}", new { DueDate = today.AddDays(60) })).Status.Should().Be(HttpStatusCode.Conflict, "reversed: only notes/attachment");
        await _k.Ok(_k.Patch($"{Invoices}/{coded}", new { Notes = "superseded" }), "notes on a reversed invoice");
        (await _k.Post($"{Invoices}/{manual.G("uuid")}/reverse", new { Reason = "never approved" })).Status
            .Should().Be(HttpStatusCode.Conflict, "only an approved invoice can be reversed");

        var rejected = await CreateAsync(Direct(vendor, "PKR", 100m, taxAmount: 0m, taxCode: pgst17));
        await _k.Ok(_k.Post($"{Invoices}/{rejected}/reject", new { Reason = "Not our order" }), "reject");
        (await _k.Post($"{Invoices}/{rejected}/approve", new { })).Status.Should().Be(HttpStatusCode.Conflict, "a rejected invoice is not approved");
        (await _k.Post($"{Invoices}/{rejected}/reverse", new { Reason = "x" })).Status.Should().Be(HttpStatusCode.Conflict, "nothing was booked for it");
        (await LedgerAsync(rejected)).Should().BeEmpty();
        (await _k.Post($"{Invoices}/{rejected}/reject", new { Reason = "again" })).Status.Should().Be(HttpStatusCode.Conflict, "already rejected");
        (await _k.Ok(_k.Get($"/api/suppliers/{vendor.Uuid}/outstanding-invoices"), "outstanding invoices")).Items()
            .Should().NotContain(i => i.G("invoiceUuid") == rejected, "a rejected invoice is owed to nobody");

        // ── Foreign currency: snapshot with the rate at approval; A35 D-5 (supersedes S-5): without one, approval is refused ──
        var usdInvoice = await CreateAsync(Direct(vendor, "USD", 100m, taxAmount: 0m, taxCode: pgst17));
        await Approve(usdInvoice);
        var usd = await GetAsync(usdInvoice);
        (usd.D("totalAmount"), usd.ND("exchangeRate"), usd.S("baseCurrencyCode"), usd.ND("baseTotalAmount"))
            .Should().Be((117m, (decimal?)280m, "PKR", (decimal?)32760m));

        var eurInvoice = await CreateAsync(Direct(vendor, "EUR", 100m, taxAmount: 0m));
        var noRate = await _k.Post($"{Invoices}/{eurInvoice}/approve", new { Notes = "ok" });
        noRate.Status.Should().Be(HttpStatusCode.BadRequest, $"no EUR rate on file — {noRate}");
        noRate.Message.Should().Be($"No exchange rate for EUR on {today:yyyy-MM-dd}. Add one under Settings → Exchange Rates.");
        var eur = await GetAsync(eurInvoice);
        eur.S("matchStatus").Should().Be("Pending", "nothing was approved or booked");
        eur.ND("exchangeRate").Should().BeNull();
        (await LedgerAsync(eurInvoice)).Should().BeEmpty();
        await _k.CreateRateAsync("EUR", "PKR", 316.48m, today.AddDays(-1));
        await Approve(eurInvoice);
        eur = await GetAsync(eurInvoice);
        (eur.ND("exchangeRate"), eur.ND("baseTotalAmount")).Should().Be(((decimal?)316.48m, (decimal?)31_648m));

        // ── Something paid against it: no reversal ───────────────────────────────
        var paid = await CreateAsync(Direct(vendor, "PKR", 500m, taxAmount: 0m, taxCode: pgst17));
        await Approve(paid);
        var payment = (await _k.Ok(_k.Post("/api/supplier-payments", new
        {
            SupplierId = vendor.Uuid, SupplierName = vendor.Name, PaymentDate = today, PaymentMethod = "CASH", TotalAmount = 585m,
            Lines = new[] { new { InvoiceUuid = paid, AllocatedAmount = 585m } }
        }), "draft supplier payment")).GetGuid();

        var onDraftPayment = await _k.Post($"{Invoices}/{paid}/reverse", new { Reason = "duplicate" });
        onDraftPayment.Status.Should().Be(HttpStatusCode.Conflict, $"the invoice is on a payment — {onDraftPayment}");

        await _k.Ok(_k.Post($"/api/supplier-payments/{payment}/approve"), "approve payment");
        await _k.Ok(_k.Post($"/api/supplier-payments/{payment}/post"), "post payment");
        (await GetAsync(paid)).D("paidAmount").Should().Be(585m);
        var onPosted = await _k.Post($"{Invoices}/{paid}/reverse", new { Reason = "duplicate" });
        onPosted.Status.Should().Be(HttpStatusCode.Conflict, $"a paid invoice cannot be reversed — {onPosted}");
        (await LedgerAsync(paid)).Should().ContainSingle(e => e.Type == "INVOICE_APPROVED").And.NotContain(e => e.Type == "INVOICE_REVERSED");
    }

    [Fact]
    public async Task A_po_backed_invoice_matches_on_its_net_subtotal_and_a_reversal_rolls_the_po_back()
    {
        var pkr = await _k.EnsureCurrencyAsync("PKR", "Pakistani Rupee", "Rs");
        await _k.SetBaseCurrencyAsync(pkr);
        var pgst17 = await _k.EnsureTaxCodeAsync("PGST17", 17m, "PURCHASE", isDefault: true);
        await _k.CreateApproverPlaceholdersAsync();

        var wh = await _k.CreateWarehouseAsync();
        var vendor = await _k.CreateVendorAsync("PO Vendor");
        var item = await _k.CreateProductAsync("Steel Sheet", purchasePrice: 300m, sellingPrice: 400m);
        var (po, grn) = await _k.StockUpAsync(vendor, wh, (item, 10m, 300m));
        (await PoStatusAsync(po)).Should().Be("RECEIVED");

        // ── GRN approval raises an invoice by itself, with the default purchase code (S-3) ──
        var auto = await SapKit.WaitForAsync(
            () => _f.QueryAsync("SELECT UUID, Subtotal, TaxAmount, TotalAmount, TaxCode, MatchStatus FROM finance.invoices WHERE GrnUuid = @g AND IsDelete = 0", ("@g", grn)),
            rows => rows.Count > 0, "the invoice auto-created from the approved GRN (Hangfire)");
        var autoRow = auto.Single();
        ((decimal)autoRow["Subtotal"]!, (decimal)autoRow["TaxAmount"]!, (decimal)autoRow["TotalAmount"]!, autoRow["TaxCode"] as string)
            .Should().Be((3000m, 510m, 3510m, "PGST17"), "the organization's default purchase code is applied");
        ((string)autoRow["MatchStatus"]!).Should().Be("Matched",
            "S-8: 3,000 net against a 3,000 PO and a 3,000 GRN — before, 3,510 with tax was a 17% 'Variance'");

        // ── A hand-entered PO-backed invoice: net compared with net ───────────────
        var matched = await CreateAsync(new
        {
            SupplierInvoiceNo = $"{_k.Marker}-M", SupplierId = vendor.Uuid, PoUuid = po, GrnUuid = grn,
            InvoiceDate = DateTime.UtcNow.Date, ReceivedDate = DateTime.UtcNow.Date, DueDate = DateTime.UtcNow.Date.AddDays(30),
            Currency = "PKR", Subtotal = 3000m, TaxAmount = 0m, TaxCodeUuid = pgst17
        });
        var m = await GetAsync(matched);
        (m.S("matchStatus"), m.D("matchedPoValue"), m.D("matchedGrnValue"), m.D("varianceAmount"), m.D("totalAmount"))
            .Should().Be(("Matched", 3000m, 3000m, 0m, 3510m));

        var otherVendor = await _k.CreateVendorAsync("Wrong Vendor");
        var wrongSupplier = await _k.Post(Invoices, new
        {
            SupplierInvoiceNo = $"{_k.Marker}-W", SupplierId = otherVendor.Uuid, PoUuid = po, GrnUuid = grn,
            InvoiceDate = DateTime.UtcNow.Date, ReceivedDate = DateTime.UtcNow.Date, DueDate = DateTime.UtcNow.Date.AddDays(30),
            Currency = "PKR", Subtotal = 3000m, TaxAmount = 0m, TaxCodeUuid = pgst17
        });
        wrongSupplier.Status.Should().Be(HttpStatusCode.BadRequest, $"a PO-backed invoice must name the PO's supplier — {wrongSupplier}");

        var over = await GetAsync(await CreateAsync(new
        {
            SupplierInvoiceNo = $"{_k.Marker}-V", SupplierId = vendor.Uuid, PoUuid = po, GrnUuid = grn,
            InvoiceDate = DateTime.UtcNow.Date, ReceivedDate = DateTime.UtcNow.Date, DueDate = DateTime.UtcNow.Date.AddDays(30),
            Currency = "PKR", Subtotal = 3300m, TaxAmount = 0m, TaxCodeUuid = pgst17
        }));
        (over.S("matchStatus"), over.D("varianceAmount")).Should().Be(("Variance", 300m), "10% over the PO net is still a variance");

        // ── Approve → PO invoiced; reverse → PO rolled back ───────────────────────
        await Approve(matched);
        (await PoQtyInvoicedAsync(po)).Should().Be(10m);
        (await PoStatusAsync(po)).Should().Be("CLOSED");

        await _k.Ok(_k.Post($"{Invoices}/{matched}/reverse", new { Reason = "Entered against the wrong GRN" }), "reverse PO-backed invoice");
        (await PoQtyInvoicedAsync(po)).Should().Be(0m, "the reversal takes the invoiced quantity back off the PO");
        (await PoStatusAsync(po)).Should().Be("RECEIVED", "nothing invoiced, everything received");
        (await LedgerAsync(matched)).Should().Equal([("INVOICE_APPROVED", 3510m, 0m), ("INVOICE_REVERSED", 0m, 3510m)]);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private object Direct(Partner vendor, string currency, decimal subtotal, decimal taxAmount, Guid? taxCode = null) => new
    {
        SupplierInvoiceNo = $"{_k.Marker}-{Guid.NewGuid():N}"[..20], SupplierId = vendor.Uuid,
        InvoiceDate = DateTime.UtcNow.Date, ReceivedDate = DateTime.UtcNow.Date, DueDate = DateTime.UtcNow.Date.AddDays(30),
        Currency = currency, Subtotal = subtotal, TaxAmount = taxAmount, TaxCodeUuid = taxCode
    };

    private async Task<Guid> CreateAsync(object body) => (await _k.Ok(_k.Post(Invoices, body), "create supplier invoice")).GetGuid();

    private Task<JsonElement> GetAsync(Guid uuid) => _k.Ok(_k.Get($"{Invoices}/{uuid}"), "read supplier invoice");

    private async Task Approve(Guid uuid) => await _k.Ok(_k.Post($"{Invoices}/{uuid}/approve", new { Notes = "ok" }), "approve supplier invoice");

    private async Task<List<(string Type, decimal Debit, decimal Credit)>> LedgerAsync(Guid invoice) =>
        (await _f.QueryAsync(
            "SELECT TransactionType, DebitAmount, CreditAmount FROM finance.supplier_ledger_entries WHERE ReferenceId = @i ORDER BY SequenceNo",
            ("@i", invoice)))
        .Select(r => ((string)r["TransactionType"]!, (decimal)r["DebitAmount"]!, (decimal)r["CreditAmount"]!)).ToList();

    private async Task<List<(string Type, decimal Debit, decimal Credit)>> MasterLedgerAsync(Guid invoice) =>
        (await _f.QueryAsync(
            "SELECT TransactionType, DebitAmount, CreditAmount FROM finance.master_financial_ledger WHERE ReferenceId = @i ORDER BY SequenceNo",
            ("@i", invoice)))
        .Select(r => ((string)r["TransactionType"]!, (decimal)r["DebitAmount"]!, (decimal)r["CreditAmount"]!)).ToList();

    private async Task<decimal> SupplierRunningBalanceAsync(Guid supplier) =>
        (decimal)(await _f.QueryAsync(
            "SELECT TOP 1 BalanceAfter FROM finance.supplier_ledger_entries WHERE SupplierId = @s ORDER BY SequenceNo DESC", ("@s", supplier)))
        .Single()["BalanceAfter"]!;

    private async Task<decimal> PoQtyInvoicedAsync(Guid po) =>
        (decimal)(await _f.QueryAsync(
            "SELECT SUM(l.QtyInvoiced) AS Q FROM demand.purchase_order_lines l JOIN demand.purchase_orders p ON p.Id = l.PurchaseOrderId WHERE p.UUID = @p",
            ("@p", po))).Single()["Q"]!;

    private async Task<string> PoStatusAsync(Guid po) => (await _k.Ok(_k.Get($"/api/purchase-orders/{po}"), "read PO")).S("status")!;
}
