using System.Globalization;
using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.ModuleRegistry;
using SMS.Integration.Tests.MultiCurrency;
using SMS.Integration.Tests.Pos;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using SMS.Integration.Tests.ServiceOrders;
using Xunit;
using static SMS.Integration.Tests.DataImport.Imp;

namespace SMS.Integration.Tests.DataImport;

/// <summary>
/// A40 (QA) — the opening-balance imports (C9: OPENING_STOCK, CUSTOMER_OPENING_BALANCE, SUPPLIER_OPENING_BALANCE), the
/// background path (more than 500 valid rows → Hangfire queue "import", hub progress), the one-running-import rule and
/// crash recovery (D-38 / D-39), on the real host (LocalDB) with the real Inventory, Finance, Suppliers and Reports modules.
/// <list type="bullet">
/// <item>IMP-OB-01/05 gates (date, permission, feature, strategy) + the Finance date API — <see cref="Opening_imports_are_gated_by_date_permission_and_feature"/>.</item>
/// <item>Opening stock (D-24 / D-46) — <see cref="Opening_stock_posts_stock_and_ledgers_at_the_opening_date"/>.</item>
/// <item>Customer / supplier opening balances (D-43 / D-44), date move (D-40) — <see cref="Opening_balances_are_open_documents_that_age_and_are_paid"/>.</item>
/// <item>Background import, hub progress — <see cref="More_than_500_rows_run_in_the_background_and_report_progress"/>.</item>
/// <item>IMP-FILE-08, D-38 / D-39 — <see cref="A_stuck_import_is_recovered_and_its_rerun_creates_no_duplicates"/>.</item>
/// </list>
/// <para>Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~DataImportOpeningE2ETests</c>.</para>
/// </summary>
public sealed class DataImportOpeningE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapKit _root;
    private readonly SapWebApplicationFactory _f;

    public DataImportOpeningE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _f.ShouldBeLocalDb();
        _root = new SapKit(factory, "A40");
    }

    private static readonly DateTime Today = DateTime.UtcNow.Date;
    private static readonly DateTime OpeningDate = Today.AddDays(-30);

    private static string V(Dictionary<string, object?> row, params string[] keys) =>
        string.Join("|", keys.Select(key => Convert.ToString(row[key], CultureInfo.InvariantCulture)));

    private static string Rc(JsonElement v) =>
        string.Join(" | ", v.A("errors").Select(e => $"{e.I("rowNumber")}:{e.S("fieldName")}:{e.S("errorCode")}:{e.S("message")}"));

    // ── Gates ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Opening_imports_are_gated_by_date_permission_and_feature()
    {
        var o = await _root.OrgAsync("OBG");
        var k = o.K;
        var stock = Csv(["Product SKU", "Warehouse Code", "Quantity", "Unit Cost"], [["X", "Y", "1", "1"]]);
        var cust = Csv(["Customer Code", "Invoice Date", "Amount Owed"], [["C-00001", Day(OpeningDate), "10"]]);

        // no opening balance date yet
        (await k.Ok(k.Get("/api/finance/opening-balance-date"), "read the date")).IsNull("date").Should().BeTrue();
        foreach (var (entity, file) in new[] { ("OPENING_STOCK", stock), ("CUSTOMER_OPENING_BALANCE", cust), ("SUPPLIER_OPENING_BALANCE", cust) })
            (await k.Upload(entity, file, "ob.csv")).ShouldFail(HttpStatusCode.BadRequest, $"{entity} without the date",
                "Set the opening balance date in Finance settings first.");

        // the Finance setting
        (await k.Put("/api/finance/opening-balance-date", new { date = "2026/01/31" })).ShouldFail(HttpStatusCode.BadRequest, "format", "yyyy-MM-dd");
        (await k.SetOpeningDate(new DateTime(1999, 12, 31))).Status.Should().Be(HttpStatusCode.BadRequest, "not before 2000-01-01");
        await k.Ok(k.SetOpeningDate(OpeningDate), "set the opening date");
        (await k.Ok(k.Get("/api/finance/opening-balance-date"), "read the date")).S("date").Should().Be(Day(OpeningDate));

        // opening imports always reject duplicates
        (await k.Upload("OPENING_STOCK", stock, "ob.csv", strategy: "SKIP")).ShouldFail(HttpStatusCode.BadRequest, "strategy",
            "Opening balance imports always reject duplicates (strategy ERROR).");
        (await k.Upload("CUSTOMER_OPENING_BALANCE", cust, "ob.csv", strategy: "UPDATE")).ShouldFail(HttpStatusCode.BadRequest, "strategy",
            "Opening balance imports always reject duplicates (strategy ERROR).");
        (await k.Put("/api/import/profiles/OPENING_STOCK/settings", new { duplicateStrategy = "SKIP" })).ShouldFail(HttpStatusCode.BadRequest,
            "org override", "Opening balance imports always reject duplicates (strategy ERROR).");
        var s = await k.UploadOk("OPENING_STOCK", stock, "ob.csv");
        s.S("duplicateStrategy").Should().Be("ERROR");

        // IMPORT_OPENING_BALANCE is needed (upload, validate, execute) — a data-entry user may import the masters
        var clerk = await k.LoginWithPermissionsAsync("clerk", "IMPORT_PROFILE_VIEW", "IMPORT_UPLOAD", "IMPORT_EXECUTE");
        (await k.Upload("OPENING_STOCK", stock, "ob.csv", client: clerk)).ShouldFail(HttpStatusCode.Forbidden, "no IMPORT_OPENING_BALANCE",
            "You do not have permission to import opening balances.");
        (await k.Upload("CUSTOMER_OPENING_BALANCE", cust, "ob.csv", client: clerk)).ShouldFail(HttpStatusCode.Forbidden, "no IMPORT_OPENING_BALANCE",
            "You do not have permission to import opening balances.");
        (await k.Upload("WAREHOUSE", Csv(["Code", "Name"], [["CLK", "Clerk's"]]), "w.csv", client: clerk)).Status.Should().Be(HttpStatusCode.Created);
        // the date can be read with IMPORT_OPENING_BALANCE alone (the wizard shows it)
        var ob = await k.LoginWithPermissionsAsync("obreader", "IMPORT_OPENING_BALANCE");
        (await k.Ok(k.Get("/api/finance/opening-balance-date", ob), "read with IMPORT_OPENING_BALANCE")).S("date").Should().Be(Day(OpeningDate));
        (await k.Put("/api/finance/opening-balance-date", new { date = Day(Today) }, ob)).Status.Should().Be(HttpStatusCode.Forbidden);

        // FEATURE_OPENING_BALANCE_IMPORT off (licence) → 403 for everyone, masters still import
        await _root.LicenceAsync(o.OrgId, (OpeningFeature, false));
        await _f.InvalidateAsync(o.OrgId);
        (await k.Upload("OPENING_STOCK", stock, "ob.csv")).ShouldFail(HttpStatusCode.Forbidden, "feature off",
            "Opening balance import is not enabled for your organization.");
        (await k.Validate(s.G("uuid"))).ShouldFail(HttpStatusCode.Forbidden, "validate an earlier session with the feature off",
            "Opening balance import is not enabled for your organization.");
        (await k.Upload("WAREHOUSE", Csv(["Code", "Name"], [["F1", "Feature off"]]), "w.csv")).Status.Should().Be(HttpStatusCode.Created);
        await _root.LicenceAsync(o.OrgId, (OpeningFeature, true));
        await _f.InvalidateAsync(o.OrgId);
        (await k.Validate(s.G("uuid"))).Status.Should().Be(HttpStatusCode.OK, "back on");
    }

    // ── Opening stock ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Opening_stock_posts_stock_and_ledgers_at_the_opening_date()
    {
        var o = await _root.OrgAsync("OBS");
        var k = o.K;
        var t = Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        string a = $"OS-A-{t}", b = $"OS-B-{t}", c = $"OS-C-{t}", svc = $"OS-S-{t}";
        await k.ImportXlsxAsync("PRODUCT", ["SKU", "Name", "Unit", "Cost Price", "Sale Price", "Is Service"],
            [[a, $"Opening A {t}", "PCS", "10", "20", null], [b, $"Opening B {t}", "PCS", "3", "6", null], [c, $"Opening C {t}", "PCS", "1", "2", null],
             [svc, $"Opening svc {t}", null, null, "50", "Yes"]]);
        await k.ImportXlsxAsync("WAREHOUSE", ["Code", "Name"], [["WH-OS", $"Opening WH {t}"]]);
        await k.ImportXlsxAsync("WAREHOUSE_LOCATION", ["Warehouse Code", "Zone Code", "Bin Code"], [["WH-OS", "Z1", "BIN-1"]]);
        await k.Ok(k.SetOpeningDate(OpeningDate), "set the opening date");

        string[] h = ["Product SKU", "Warehouse Code", "Bin", "Quantity", "Unit Cost"];
        var (v, s) = await k.ImportXlsxAsync("OPENING_STOCK", h,
        [
            [a, "WH-OS", "BIN-1", "40", "12.5"], [b, "WH-OS", null, "0", "3"], [svc, "WH-OS", null, "5", "1"], [a, "WH-NONE", null, "1", "1"],
            [a, "WH-OS", "NOPE", "1", "1"]
        ]);
        s.Counts().Should().Be("COMPLETED_WITH_ERRORS total=5 valid=2 invalid=3 imported=2 updated=0 skipped=0 error=0", Rc(v));
        v.A("errors").Select(e => $"{e.I("rowNumber")}:{e.S("errorCode")}").Should().Contain(new[] { "4:NOT_STOCKABLE", "5:LOOKUP_NOT_FOUND" });

        var va = (Guid)(await _f.RowAsync("SELECT Uuid FROM inventory.ProductVariants WHERE OrganizationId = @o AND Sku = @s", ("@o", o.OrgId), ("@s", a)))!["Uuid"]!;
        var vb = (Guid)(await _f.RowAsync("SELECT Uuid FROM inventory.ProductVariants WHERE OrganizationId = @o AND Sku = @s", ("@o", o.OrgId), ("@s", b)))!["Uuid"]!;

        // stock on hand, in the bin
        var onHand = (await _f.RowAsync(
            "SELECT SUM(i.QtyOnHand) AS Q, MAX(bn.Code) AS Bin FROM inventory.InventoryItems i JOIN inventory.ProductVariants v ON v.Id = i.VariantId " +
            "JOIN inventory.Warehouses w ON w.Id = i.WarehouseId LEFT JOIN inventory.Bins bn ON bn.Id = i.BinId WHERE v.Uuid = @v AND w.Code = 'WH-OS' AND w.OrganizationId = @o",
            ("@v", va), ("@o", o.OrgId)))!;
        (Convert.ToDecimal(onHand["Q"]), onHand["Bin"] as string).Should().Be((40m, "BIN-1"));

        // inventory ledger OPENING_STOCK at the opening date (B with quantity 0 still records its opening)
        var ledger = await _f.QueryAsync(
            "SELECT v.Uuid AS V, l.TransactionType, l.ReferenceType, l.TransactionDate, l.QuantityIn, l.UnitCost, l.ReferenceId FROM inventory.InventoryLedgerEntries l " +
            "JOIN inventory.ProductVariants v ON v.Id = l.VariantId WHERE l.OrganizationId = @o", ("@o", o.OrgId));
        ledger.Should().HaveCount(2);
        var la = ledger.Single(r => (Guid)r["V"]! == va);
        V(la, "TransactionType", "ReferenceType").Should().Be("OPENING_STOCK|OPENING_STOCK");
        ((DateTime)la["TransactionDate"]!).Date.Should().Be(OpeningDate, "dated at the opening balance date");
        (Convert.ToDecimal(la["QuantityIn"]), Convert.ToDecimal(la["UnitCost"])).Should().Be((40m, 12.5m));
        var lb = ledger.Single(r => (Guid)r["V"]! == vb);
        Convert.ToDecimal(lb["QuantityIn"] ?? 0m).Should().Be(0m);

        // the master product ledger mirror and the Finance product ledger (OPENING_STOCK, D-46) at the opening date
        var master = (await _f.RowAsync("SELECT TransactionType, TransactionDate, QuantityIn FROM finance.master_product_ledger WHERE ReferenceId = @r",
            ("@r", la["ReferenceId"]!)))!;
        ((DateTime)master["TransactionDate"]!).Date.Should().Be(OpeningDate);
        Convert.ToDecimal(master["QuantityIn"]).Should().Be(40m);
        var pl = await _f.QueryAsync("SELECT EntryType, EntryDate, Quantity, UnitCost FROM finance.product_ledger WHERE VariantUuid = @v", ("@v", va));
        pl.Should().ContainSingle();
        V(pl[0], "EntryType").Should().Be("OPENING_STOCK");
        ((DateTime)pl[0]["EntryDate"]!).Date.Should().Be(OpeningDate);
        (Convert.ToDecimal(pl[0]["Quantity"]), Convert.ToDecimal(pl[0]["UnitCost"])).Should().Be((40m, 12.5m));
        (await _f.CountAsync("SELECT COUNT(*) FROM finance.product_ledger WHERE VariantUuid = @v", ("@v", vb))).Should().Be(0, "quantity 0: no product ledger entry");

        // one opening per variant + warehouse (IMP-OB-03): a second import is DUPLICATE_IN_DB, nothing posts twice
        var again = (await k.UploadOk("OPENING_STOCK", Xlsx(h, [[a, "WH-OS", null, "5", "12.5"]]), "again.xlsx")).G("uuid");
        var av = await k.ValidateOk(again);
        av.A("errors").Single().S("errorCode").Should().Be("DUPLICATE_IN_DB");
        (await k.Execute(again)).ShouldFail(HttpStatusCode.BadRequest, "nothing valid", "There are no valid rows to import.");
        (await _f.CountAsync("SELECT COUNT(*) FROM inventory.InventoryLedgerEntries WHERE OrganizationId = @o", ("@o", o.OrgId))).Should().Be(2);
        // the same variant + warehouse twice in one file: one opening only
        var (dv, ds) = await k.ImportXlsxAsync("OPENING_STOCK", h, [[c, "WH-OS", null, "1", "1"], [c, "WH-OS", null, "2", "1"]]);
        dv.A("errors").Select(e => $"{e.I("rowNumber")}:{e.S("errorCode")}").Should().Contain("3:DUPLICATE_IN_FILE", Rc(dv));
        (await _f.CountAsync("SELECT COUNT(*) FROM inventory.InventoryLedgerEntries l JOIN inventory.ProductVariants v ON v.Id = l.VariantId " +
                             "WHERE v.Sku = @s AND l.OrganizationId = @o", ("@s", c), ("@o", o.OrgId))).Should().BeLessThanOrEqualTo(1, ds.Counts());
    }

    // ── Customer / supplier opening balances ─────────────────────────────────────

    [Fact]
    public async Task Opening_balances_are_open_documents_that_age_and_are_paid()
    {
        var o = await _root.OrgAsync("OBB");
        var k = o.K;
        var t = Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        await k.ImportXlsxAsync("CUSTOMER", ["Customer Code", "Customer Name", "Payment Terms (days)"], [["C-00007", $"OB Customer {t}", "30"]]);
        await k.ImportXlsxAsync("SUPPLIER", ["Supplier Code", "Supplier Name"], [["OBS-1", $"OB Supplier {t}"]]);
        var customer = (Guid)(await _f.RowAsync("SELECT UUID FROM suppliers.BusinessPartners WHERE OrganizationId = @o AND SupplierCode = 'C-00007'", ("@o", o.OrgId)))!["UUID"]!;
        var vendorRow = (await _f.RowAsync("SELECT UUID, SupplierName FROM suppliers.BusinessPartners WHERE OrganizationId = @o AND SupplierCode = 'OBS-1'", ("@o", o.OrgId)))!;
        var vendor = new Partner((Guid)vendorRow["UUID"]!, (string)vendorRow["SupplierName"]!);
        await k.Ok(k.SetOpeningDate(OpeningDate), "set the opening date");

        // customer opening balances → issued SINV, source OPENING, OPENING_BAL ledger debit
        string[] ch = ["Customer Code", "Invoice Reference", "Invoice Date", "Due Date", "Amount Owed", "Currency"];
        var invDate = OpeningDate.AddDays(-40);
        var (cv, cs) = await k.ImportXlsxAsync("CUSTOMER_OPENING_BALANCE", ch,
        [
            ["C-00007", "INV-0042", Day(invDate), Day(OpeningDate.AddDays(-10)), "15000.00", null],
            ["C-00007", "INV-0043", Day(OpeningDate.AddDays(1)), null, "10", null],
            ["OBS-1", "INV-0044", Day(invDate), null, "10", null],
            ["C-99999", "INV-0045", Day(invDate), null, "10", null],
            ["C-00007", "INV-0046", Day(invDate), null, "0", null]
        ]);
        cs.Counts().Should().Be("COMPLETED_WITH_ERRORS total=5 valid=1 invalid=4 imported=1 updated=0 skipped=0 error=0", Rc(cv));
        cv.A("errors").Select(e => $"{e.I("rowNumber")}:{e.S("errorCode")}").Should()
            .Contain(new[] { "3:AFTER_OPENING_DATE", "4:NOT_A_CUSTOMER", "6:OUT_OF_RANGE" }, Rc(cv));
        cv.A("errors").Single(e => e.I("rowNumber") == 5).S("errorCode").Should().BeOneOf("LOOKUP_NOT_FOUND", "NOT_A_CUSTOMER");

        var inv = (await _f.RowAsync("SELECT UUID, Status, SourceType, SourceNumber, GrandTotal, InvoiceDate, DueDate FROM finance.sales_invoices " +
                                     "WHERE OrganizationId = @o AND PartnerId = @p", ("@o", o.OrgId), ("@p", customer)))!;
        var invUuid = (Guid)inv["UUID"]!;
        V(inv, "Status", "SourceType", "SourceNumber").Should().Be("ISSUED|OPENING|INV-0042");
        Convert.ToDecimal(inv["GrandTotal"]).Should().Be(15000m);
        ((DateTime)inv["InvoiceDate"]!).Date.Should().Be(invDate);
        ((DateTime)inv["DueDate"]!).Date.Should().Be(OpeningDate.AddDays(-10));
        (await _f.QueryAsync("SELECT EntryType, DebitAmount FROM finance.customer_ledger WHERE PartnerId = @p AND OrganizationId = @o", ("@p", customer), ("@o", o.OrgId)))
            .Select(r => $"{r["EntryType"]}|{Convert.ToDecimal(r["DebitAmount"]):0.00}").Should().Equal("OPENING_BAL|15000.00");
        (await k.Ok(k.Get($"/api/customers/{customer}/balance"), "balance")).D("balance").Should().Be(15000m);
        (await _f.CountAsync("SELECT COUNT(*) FROM finance.product_ledger WHERE ReferenceId = @r", ("@r", invUuid))).Should().Be(0, "no stock, no COGS");

        // R3 aging receivables shows it, overdue
        var aging = await k.Ok(k.Get($"/api/reports/sales/aging-receivables?partnerId={customer}"), "aging");
        var line = aging.A("invoices").Single(i => i.G("invoiceUuid") == invUuid);
        (line.D("outstanding"), line.I("daysPastDue") > 0).Should().Be((15000m, true));

        // a customer payment pays it like any invoice
        await k.PayCustomerAsync(new Partner(customer, $"OB Customer {t}"), 5000m, "PKR", Today, (invUuid, 5000m));
        (await k.Ok(k.Get($"/api/customers/{customer}/balance"), "balance")).D("balance").Should().Be(10000m);
        aging = await k.Ok(k.Get($"/api/reports/sales/aging-receivables?partnerId={customer}"), "aging");
        aging.A("invoices").Single(i => i.G("invoiceUuid") == invUuid).D("outstanding").Should().Be(10000m);

        // a second opening for the same customer + reference → DUPLICATE_IN_DB (never updated)
        var dup = (await k.UploadOk("CUSTOMER_OPENING_BALANCE", Xlsx(ch, [["C-00007", "inv-0042", Day(invDate), null, "99", null]]), "dup.xlsx")).G("uuid");
        (await k.ValidateOk(dup)).A("errors").Single().S("errorCode").Should().Be("DUPLICATE_IN_DB");
        (await _f.CountAsync("SELECT COUNT(*) FROM finance.sales_invoices WHERE OrganizationId = @o AND SourceType = 'OPENING'", ("@o", o.OrgId))).Should().Be(1);

        // supplier opening balance → approved supplier invoice, source OPENING, paid by a supplier payment
        string[] sh = ["Supplier Code", "Bill Reference", "Bill Date", "Amount Owed"];
        var billDate = OpeningDate.AddDays(-20);
        var (sv, ss) = await k.ImportXlsxAsync("SUPPLIER_OPENING_BALANCE", sh,
            [["OBS-1", "BILL-7781", Day(billDate), "8200.00"], ["C-00007", "BILL-1", Day(billDate), "5"], ["OBS-1", "BILL-2", Day(Today), "5"]]);
        ss.Counts().Should().Be("COMPLETED_WITH_ERRORS total=3 valid=1 invalid=2 imported=1 updated=0 skipped=0 error=0", Rc(sv));
        sv.A("errors").Select(e => $"{e.I("rowNumber")}:{e.S("errorCode")}").Should().Contain(new[] { "3:NOT_A_SUPPLIER", "4:AFTER_OPENING_DATE" });
        var bill = (await _f.RowAsync("SELECT UUID, SourceType, SupplierInvoiceNo, TotalAmount, InvoiceDate, DueDate, PoUuid, MatchStatus FROM finance.invoices " +
                                      "WHERE OrganizationId = @o AND SupplierId = @s", ("@o", o.OrgId), ("@s", vendor.Uuid)))!;
        var billUuid = (Guid)bill["UUID"]!;
        V(bill, "SourceType", "SupplierInvoiceNo").Should().Be("OPENING|BILL-7781");
        bill["PoUuid"].Should().BeNull();
        Convert.ToDecimal(bill["TotalAmount"]).Should().Be(8200m);
        ((DateTime)bill["DueDate"]!).Date.Should().Be(billDate.AddDays(30));
        var read = await k.SupplierInvoiceReadAsync(billUuid);
        (read.S("matchStatus"), read.S("paymentStatus"), read.IsNull("approvedBy")).Should().Be(("Approved", "Unpaid", false), J.Short(read));
        var sl = await _f.QueryAsync("SELECT TransactionType, EntryDate FROM finance.supplier_ledger_entries WHERE ReferenceId = @r", ("@r", billUuid));
        sl.Select(r => (string)r["TransactionType"]!).Should().Equal("INVOICE_APPROVED");
        ((DateTime)sl[0]["EntryDate"]!).Date.Should().Be(billDate, "dated the bill date");
        var paid = await k.PaySupplierAsync(vendor, billUuid, 8200m, "PKR", Today);
        paid.S("status").Should().Be("POSTED");
        var after = await k.SupplierInvoiceReadAsync(billUuid);
        (after.S("paymentStatus"), after.D("paidAmount")).Should().Be(("FULLY_PAID", 8200m), J.Short(after));
        (await _f.CountAsync("SELECT COUNT(*) FROM finance.invoices WHERE OrganizationId = @o AND SourceType = 'OPENING'", ("@o", o.OrgId))).Should().Be(1);
        var sdup = (await k.UploadOk("SUPPLIER_OPENING_BALANCE", Xlsx(sh, [["OBS-1", "BILL-7781", Day(billDate), "1"]]), "sdup.xlsx")).G("uuid");
        (await k.ValidateOk(sdup)).A("errors").Single().S("errorCode").Should().Be("DUPLICATE_IN_DB");

        // D-40: the opening date cannot move before the opening documents nor be cleared
        (await k.SetOpeningDate(invDate.AddDays(-1))).ShouldFail(HttpStatusCode.Conflict, "before the opening documents",
            "the opening balance date cannot be earlier than that");
        (await k.SetOpeningDate(billDate.AddDays(-1))).ShouldFail(HttpStatusCode.Conflict, "before the bill", "already imported");
        (await k.SetOpeningDate(null)).ShouldFail(HttpStatusCode.Conflict, "clear", "cannot be cleared");
        await k.Ok(k.SetOpeningDate(billDate), "a date on the latest document is fine");
        await k.Ok(k.SetOpeningDate(OpeningDate), "and back");
    }

    // ── Background path ──────────────────────────────────────────────────────────

    [Fact]
    public async Task More_than_500_rows_run_in_the_background_and_report_progress()
    {
        var o = await _root.OrgAsync("BGR");
        var k = o.K;
        const int n = 520;
        var rows = Enumerable.Range(1, n).Select(i => (IReadOnlyList<string?>)new string?[] { $"BG-{i:D4}", $"Background {i}", "Lahore" }).ToList();
        var s = (await k.UploadOk("WAREHOUSE", Csv(["Code", "Name", "City"], rows), "big.csv")).G("uuid");
        (await k.ValidateOk(s)).P("session").I("validRows").Should().Be(n);

        await using var hub = await HubProbe.ConnectAsync(_f, HubProbe.TokenOf(k.Admin), "/hubs/import");
        hub.Closed.Should().BeFalse("the org admin may listen to /hubs/import");

        var started = await k.ExecuteOk(s);
        started.B("isBackground").Should().BeTrue("more than 500 valid rows run as a Hangfire job");
        started.S("jobId").Should().NotBeNullOrEmpty();
        started.P("session").S("status").Should().BeOneOf("IMPORTING", "COMPLETED");

        var done = await k.WaitFinishedAsync(s, 180);
        done.Counts().Should().Be($"COMPLETED total={n} valid={n} invalid=0 imported={n} updated=0 skipped=0 error=0");
        var progress = await k.Ok(k.Get($"/api/import/sessions/{s}/progress"), "progress");
        (progress.I("processed"), progress.I("total"), progress.S("status")).Should().Be((n, n, "COMPLETED"));
        (await _f.CountAsync("SELECT COUNT(*) FROM inventory.Warehouses WHERE OrganizationId = @o AND Code LIKE 'BG-%'", ("@o", o.OrgId))).Should().Be(n);

        var final = await hub.WaitForPushAsync("ImportProgress", 15, x => x.S("status") == "COMPLETED");
        final.Should().NotBeNull("the hub pushes ImportProgress per batch and at the end");
        (final!.Value.G("sessionUuid"), final.Value.I("processed"), final.Value.I("total")).Should().Be((s, n, n));
        hub.Pushes("ImportProgress").Count.Should().BeGreaterThanOrEqualTo(6, "one push per batch of 100 + the final one");
    }

    // ── IMP-FILE-08, D-38 / D-39 ─────────────────────────────────────────────────

    [Fact]
    public async Task A_stuck_import_is_recovered_and_its_rerun_creates_no_duplicates()
    {
        var o = await _root.OrgAsync("REC");
        var k = o.K;
        string[] h = ["Code", "Name"];
        var s = (await k.UploadOk("WAREHOUSE", Xlsx(h, [["WR-1", "Recover one"], ["WR-2", "Recover two"], ["WR-3", "Recover three"]]), "r.xlsx")).G("uuid");
        await k.ValidateOk(s);
        var other = (await k.UploadOk("WAREHOUSE", Xlsx(h, [["WO-1", "Other one"]]), "o.xlsx")).G("uuid");
        await k.ValidateOk(other);

        // a crash mid-import: the target committed WR-1, the importer never marked its row; the session is left IMPORTING
        await k.Ok(k.Post("/api/warehouses", new { Code = "WR-1", Name = "Recover one" }), "the batch the target committed");
        await _f.ExecuteAsync("UPDATE import.sessions SET Status = 'IMPORTING', ImportStartedAt = SYSUTCDATETIME(), ModifiedAt = SYSUTCDATETIME(), " +
                              "RowsToProcess = 3, JobId = NULL WHERE Uuid = @u", ("@u", s));

        // IMP-FILE-08: one running import per entity type and organization
        (await k.Upload("WAREHOUSE", Xlsx(h, [["WX", "x"]]), "x.xlsx")).ShouldFail(HttpStatusCode.BadRequest, "IMP-FILE-08",
            "Another Warehouses import is already running.");
        (await k.Execute(other)).ShouldFail(HttpStatusCode.BadRequest, "a second run", "Another Warehouses import is already running.");
        (await k.Upload("UNIT", Xlsx(h, [["U1", "Unit one"]]), "u.xlsx")).Status.Should().Be(HttpStatusCode.Created, "another entity type is free");
        (await k.PutMappings(s, (1, null))).ShouldFail(HttpStatusCode.BadRequest, "running", "Mappings cannot change once the import has started.");
        (await k.Validate(s)).ShouldFail(HttpStatusCode.BadRequest, "running", "This import is already running or finished.");

        // recent activity: recovery leaves it alone
        await _f.RunRecoveryJobAsync();
        (await k.SessionAsync(s)).S("status").Should().Be("IMPORTING");
        // 31 minutes without a batch save and no live job: FAILED / interrupted, which frees the slot
        await _f.ExecuteAsync("UPDATE import.sessions SET ModifiedAt = DATEADD(MINUTE, -31, SYSUTCDATETIME()) WHERE Uuid = @u", ("@u", s));
        (await _f.RunRecoveryJobAsync()).Should().BeGreaterThanOrEqualTo(1);
        var failed = await k.SessionAsync(s);
        (failed.S("status"), failed.S("errorMessage")).Should().Be(("FAILED", "Import interrupted — re-validate and re-run the remaining rows."));
        (await k.Execute(s)).ShouldFail(HttpStatusCode.BadRequest, "re-validate first", "Validate the file before importing.");

        // re-validate + re-run: the committed row is found by its code (SKIP) — no second WR-1
        var rv = await k.ValidateOk(s);
        rv.P("session").S("status").Should().Be("VALIDATED");
        var done = (await k.ExecuteOk(s)).P("session");
        done.Counts().Should().Be("COMPLETED total=3 valid=3 invalid=0 imported=2 updated=0 skipped=1 error=0");
        foreach (var code in new[] { "WR-1", "WR-2", "WR-3" })
            (await _f.CountAsync("SELECT COUNT(*) FROM inventory.Warehouses WHERE OrganizationId = @o AND Code = @c", ("@o", o.OrgId), ("@c", code))).Should().Be(1, code);
        (await k.Post($"/api/import/sessions/{s}/cancel")).ShouldFail(HttpStatusCode.BadRequest, "finished", "This import has already finished.");

        // the same with strategy ERROR: the applied-but-unmarked row becomes DUPLICATE_IN_DB, never a second record
        var e = (await k.UploadOk("WAREHOUSE", Xlsx(h, [["WE-1", "E one"], ["WE-2", "E two"]]), "e.xlsx", "ERROR")).G("uuid");
        await k.ValidateOk(e);
        await k.Ok(k.Post("/api/warehouses", new { Code = "WE-1", Name = "E one" }), "the batch the target committed");
        await _f.ExecuteAsync("UPDATE import.sessions SET Status = 'IMPORTING', ImportStartedAt = SYSUTCDATETIME(), " +
                              "ModifiedAt = DATEADD(HOUR, -1, SYSUTCDATETIME()), JobId = NULL WHERE Uuid = @u", ("@u", e));
        await _f.RunRecoveryJobAsync();
        var ev = await k.ValidateOk(e);
        ev.A("errors").Select(x => $"{x.I("rowNumber")}:{x.S("errorCode")}").Should().Equal("2:DUPLICATE_IN_DB");
        (await k.ExecuteOk(e)).P("session").Counts().Should().Be("COMPLETED_WITH_ERRORS total=2 valid=1 invalid=1 imported=1 updated=0 skipped=0 error=0");
        (await _f.CountAsync("SELECT COUNT(*) FROM inventory.Warehouses WHERE OrganizationId = @o AND Code = 'WE-1'", ("@o", o.OrgId))).Should().Be(1);

        // an IMPORT_MANAGE holder's cancel of an IMPORTING session forces the interrupted state (D-38)
        await _f.ExecuteAsync("UPDATE import.sessions SET Status = 'IMPORTING', ImportStartedAt = SYSUTCDATETIME(), ModifiedAt = SYSUTCDATETIME() WHERE Uuid = @u",
            ("@u", other));
        var cancelled = await k.Ok(k.Post($"/api/import/sessions/{other}/cancel"), "manager cancel");
        (cancelled.S("status"), cancelled.S("errorMessage")).Should().Be(("FAILED", "Import interrupted — re-validate and re-run the remaining rows."));
        await k.ValidateOk(other);
        (await k.ExecuteOk(other)).P("session").S("status").Should().Be("COMPLETED");
        // a plain cancel of a session not yet running
        var c = (await k.UploadOk("WAREHOUSE", Xlsx(h, [["WC-1", "c"]]), "c.xlsx")).G("uuid");
        (await k.Ok(k.Post($"/api/import/sessions/{c}/cancel"), "cancel")).S("status").Should().Be("CANCELLED");

        // D-47 — an UPLOADED session never blocks, and is cancelled by the recovery job after 7 untouched days
        var stale = (await k.UploadOk("WAREHOUSE", Xlsx(h, [["WS-1", "stale"]]), "stale.xlsx")).G("uuid");
        var fresh = (await k.UploadOk("WAREHOUSE", Xlsx(h, [["WS-2", "fresh"]]), "fresh.xlsx")).G("uuid");
        await _f.ExecuteAsync("UPDATE import.sessions SET ModifiedAt = DATEADD(DAY, -8, SYSUTCDATETIME()) WHERE Uuid = @u", ("@u", stale));
        await _f.RunRecoveryJobAsync();
        var gone = await k.SessionAsync(stale);
        (gone.S("status"), gone.S("errorMessage")).Should().Be(("CANCELLED", "Cancelled automatically: not mapped or validated within 7 days."));
        (await k.SessionAsync(fresh)).S("status").Should().Be("UPLOADED");
        (await k.ValidateOk(stale)).P("session").S("status").Should().Be("VALIDATED", "a cancelled session can still be validated and run");
    }
}
