using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.FulfillmentRoutes;
using SMS.Integration.Tests.RouteClassification;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using Xunit;
using static SMS.Integration.Tests.FulfillmentRoutes.Routes;
using static SMS.Integration.Tests.ServiceOrders.ServiceKit;

namespace SMS.Integration.Tests.ServiceOrders;

/// <summary>
/// A36-P5-10 (QA) — service orders end to end on the real host (LocalDB), as the admin of a fresh ENTERPRISE organization
/// (MODULE_SERVICES, MODULE_INVENTORY and MODULE_MANUFACTURING on from the plan). docs/service-orders/*.md.
/// <list type="bullet">
/// <item>TS-01..05 — service fields through PATCH /api/products, the SVC-P / SVC-BOM refusals, a service BOM with STOCK,
/// SUBCONTRACT and INTERNAL_LABOR lines, activated.</item>
/// <item>Manual order — create, 409 on a stale rowVersion, plan (BOM exploded, stock reserved → READY), start (issued, on hand
/// drops), ad-hoc available (issued) / unavailable (WAITING), remove, complete with partial consumption (return posted,
/// ledger +issue/−return, net = consumed), close; list filters/paging, dashboard, ledger, allowedActions per status.</item>
/// <item>Shortage → MATERIAL_PENDING + supply requirement → stock arrives → READY; cancel after issue (D-16).</item>
/// <item>Labour-only T&amp;M service (TS-28, SVC-COMP-04).</item>
/// <item>Permissions (403 without SERVICE_ORDER_*) and the feature switch (403, and no service orders at confirm).</item>
/// <item>TS-17/18/19 — mixed and service-only sale orders; SO cancel cascades to a not-started service order.</item>
/// <item>TS-31 — two orders competing for scarce stock: allocation priority decides.</item>
/// </list>
/// <para>Run alone: <c>dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~ServiceOrdersE2ETests</c>.</para>
/// </summary>
public sealed class ServiceOrdersE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapKit _root;

    public ServiceOrdersE2ETests(SapWebApplicationFactory factory) => _root = new SapKit(factory, "A36");

    // ── TS-01..05 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Service_fields_and_a_service_bom_with_all_three_source_types()
    {
        var w = await _root.WorldAsync("CAT");
        var k = w.K;

        // SVC-P rules
        (await k.PatchService(w.Raw, new { serviceInvoicingPolicy = "FIXED_PRICE" }))
            .ShouldFail(HttpStatusCode.BadRequest, "SVC-P-01 on a raw material", "Invoicing policy is only applicable to service products");
        (await k.PatchService(w.Svc, new { estimatedDurationHours = -1m }))
            .ShouldFail(HttpStatusCode.BadRequest, "duration must be positive", "Estimated duration must be a positive number");
        var noRate = await k.ServiceProductAsync("No rate", "PCS", 0m, null);
        (await k.PatchService(noRate, new { serviceInvoicingPolicy = "TIME_AND_MATERIAL" }))
            .ShouldFail(HttpStatusCode.BadRequest, "SVC-P-06: T&M needs an hourly rate", "Hourly rate");

        // SVC-BOM-01: no BOM before the flag is on
        (await k.TryCreateServiceBom(w.Svc, BomLine(w.Raw, 1m)))
            .ShouldFail(HttpStatusCode.BadRequest, "SVC-BOM-01", "This product does not have service BOM enabled");

        await k.ConfigureServiceAsync(w.Svc);
        var p = await k.ProductDetailAsync(w.Svc);
        (p.S("serviceInvoicingPolicy"), p.S("serviceBillingModel"), p.D("estimatedDurationHours"), p.B("hasServiceBom"), p.B("isSubcontractable"), p.B("hasActiveServiceBom"))
            .Should().Be(("FIXED_PRICE", "INCLUSIVE", 2m, true, true, false), "TS-01, D-3: flag on, no active BOM yet");

        // SVC-BOM-02..05, D-20
        (await k.TryCreateServiceBom(w.Svc, BomLine(w.Subcon, 1m, "SUBCONTRACT")))
            .ShouldFail(HttpStatusCode.BadRequest, "SVC-BOM-02", "Subcontract supplier is required");
        (await k.TryCreateServiceBom(w.Svc, BomLine(w.Raw, 1m, "STOCK", w.Vendor.Uuid)))
            .ShouldFail(HttpStatusCode.BadRequest, "SVC-BOM-03", "Supplier reference is only valid for subcontracted lines");
        (await k.TryCreateServiceBom(w.Svc, BomLine(w.Raw, 1m, "SUBCONTRACT", w.Vendor.Uuid)))
            .ShouldFail(HttpStatusCode.BadRequest, "SVC-BOM-04", "must be a service-type product");
        (await k.TryCreateServiceBom(w.Svc, BomLine(w.Subcon, 1m, "INTERNAL_LABOR")))
            .ShouldFail(HttpStatusCode.BadRequest, "SVC-BOM-05", "Internal labor lines must use hours");
        (await k.TryCreateServiceBom(w.Svc, BomLine(w.Subcon, 1m)))
            .ShouldFail(HttpStatusCode.BadRequest, "D-20: a service is not a stock line", "cannot be a stock line");
        (await k.TryCreateServiceBom(w.Svc, BomLine(w.Raw, 1m, "SUBCONTRACT", w.Customer.Uuid)))
            .Status.Should().Be(HttpStatusCode.BadRequest, "a customer is not a subcontract vendor");

        // TS-02..05: the three source types, activated
        var bom = await w.StandardServiceBomAsync();
        var detail = await k.Ok(k.Get($"/api/boms/{bom}"), "read BOM");
        detail.S("status").Should().Be("ACTIVE");
        var lines = detail.A("lines");
        lines.Select(l => l.S("sourceType")).Should().BeEquivalentTo(new[] { "STOCK", "SUBCONTRACT", "INTERNAL_LABOR" });
        var sub = lines.Single(l => l.S("sourceType") == "SUBCONTRACT");
        (sub.NG("subcontractSupplierUuid"), sub.S("subcontractSupplierName")).Should().Be(((Guid?)w.Vendor.Uuid, w.Vendor.Name));
        lines.Single(l => l.S("sourceType") == "INTERNAL_LABOR").S("uom").Should().Be("HR");
        (await k.ProductDetailAsync(w.Svc)).B("hasActiveServiceBom").Should().BeTrue("D-3: the warning clears once a BOM is active");

        // D-19: explicit null clears the duration, false clears a flag
        await k.Ok(k.PatchService(w.Svc, new { estimatedDurationHours = (decimal?)null, isSubcontractable = false }), "clear fields");
        var cleared = await k.ProductDetailAsync(w.Svc);
        (cleared.ND("estimatedDurationHours"), cleared.B("isSubcontractable"), cleared.S("serviceInvoicingPolicy")).Should().Be(((decimal?)null, false, "FIXED_PRICE"));
    }

    // ── The manual service order, end to end ─────────────────────────────────────

    [Fact]
    public async Task A_manual_service_order_from_draft_to_closed()
    {
        var w = await _root.WorldAsync("MAN", rawStock: 10m, spareStock: 5m);
        var k = w.K;
        await w.StandardServiceBomAsync();

        // ── create ──
        var so = await w.CreateOrderAsync(w.Svc, 2m, priority: 2, time: "09:30");
        var d0 = await w.OrderAsync(so);
        (d0.S("status"), d0.S("materialReadiness"), d0.S("sourceType"), d0.S("invoicingPolicy"), d0.S("billingModel"))
            .Should().Be(("DRAFT", "NOT_CHECKED", "MANUAL", "FIXED_PRICE", "INCLUSIVE"));
        d0.S("serviceNumber").Should().StartWith("SVC-");
        (d0.D("estimatedHours"), d0.S("scheduledDate"), d0.S("scheduledTime"), d0.I("assignedUserId")).Should().Be((4m, Today, "09:30", w.AdminUserId), "EstimatedHours = 2 h × 2");
        d0.S("customerName").Should().Be(w.Customer.Name);
        d0.A("materials").Should().BeEmpty("the BOM is exploded at plan, not at create");
        d0.Actions().Should().BeEquivalentTo("EDIT", "PLAN", "CANCEL");
        (await w.Act(so, "start")).ShouldFail(HttpStatusCode.BadRequest, "a draft cannot start");

        // ── 409 on a stale rowVersion (SVC-13) ──
        var d1 = await k.Ok(w.TryUpdate(d0, "first edit"), "edit the draft");
        d1.S("notes").Should().Be("first edit");
        d1.S("rowVersion").Should().NotBe(d0.S("rowVersion"));
        (await w.TryUpdate(d0, "stale edit")).ShouldFail(HttpStatusCode.Conflict, "the draft changed since d0 was read");

        // ── plan: BOM exploded, stock reserved → READY ──
        var p = await w.DoAsync(so, "plan");
        p.S("status").Should().Be("READY", $"raw is in stock and the subcontract line is not critical — {J.Short(p)}");
        p.S("materialReadiness").Should().Be("READY", "the subcontracted line's purchase order was raised at plan (SR ORDERED, R-2), labour is always covered");
        p.I("bomVersion").Should().BeGreaterThan(0);
        p.S("bomNumber").Should().NotBeNullOrEmpty();
        var raw = p.Material(w.Raw);
        (raw.S("sourceType"), raw.D("requiredQuantity"), raw.D("reservedQuantity"), raw.D("shortageQuantity"), raw.S("status"), raw.B("isAdhoc"))
            .Should().Be(("STOCK", 4m, 4m, 0m, "FULLY_RESERVED", false), "2 per unit × 2");
        raw.D("availableQuantity").Should().Be(6m, "free stock in the warehouse = 10 on hand − 4 held");
        var sub = p.Material(w.Subcon);
        (sub.S("sourceType"), sub.D("requiredQuantity"), sub.B("isCritical")).Should().Be(("SUBCONTRACT", 2m, false));
        sub.S("supplyRequirementNumber").Should().NotBeNullOrEmpty("SVC-SR-01: the subcontracted line is bought");
        (sub.S("supplyRequirementStatus"), sub.S("status"), sub.D("shortageQuantity")).Should().Be(("ORDERED", "FULLY_RESERVED", 0m),
            "a purchase order from the BOM line's vendor was raised at plan");
        var subPo = (Guid)(await k.F.QueryAsync("SELECT SupplySourceUuid AS P FROM material.supply_requirements WHERE DemandSourceUuid = @m", ("@m", sub.G("uuid")))).Single()["P"]!;
        (await k.Ok(k.Get($"/api/purchase-orders/{subPo}"), "read the subcontract PO")).G("supplierId").Should().Be(w.Vendor.Uuid, "SVC-SR-02: bought from the BOM line's vendor");
        var lab = p.Material(w.Labor);
        (lab.S("sourceType"), lab.D("requiredQuantity"), lab.D("shortageQuantity"), lab.B("isCritical"), lab.S("uom")).Should().Be(("INTERNAL_LABOR", 3m, 0m, false, "HR"));
        p.Actions().Should().BeEquivalentTo("EDIT", "START", "CANCEL");
        (await w.DemandsAsync(so)).Should().ContainSingle("one STOCK requirement, one SERVICE_ORDER demand").Which.Should().Be((w.Raw.VariantUuid, "OPEN"));
        (await w.OnHandAsync(w.Raw)).Should().Be(10m, "a reservation does not move stock");
        (await w.Act(so, "plan")).ShouldFail(HttpStatusCode.BadRequest, "plans once");
        (await w.Act(so, "materials", new { variantUuid = w.Spare.VariantUuid, quantity = 1m })).ShouldFail(HttpStatusCode.BadRequest, "no ad-hoc before the start");

        // ── start: everything held is issued ──
        var s = await w.DoAsync(so, "start");
        s.S("status").Should().Be("IN_PROGRESS");
        s.IsNull("actualStartDate").Should().BeFalse();
        (s.Material(w.Raw).D("issuedQuantity"), s.Material(w.Raw).S("status")).Should().Be((4m, "ISSUED"));
        (await w.OnHandAsync(w.Raw)).Should().Be(6m, "TS-10: 4 issued");
        s.Actions().Should().BeEquivalentTo("EDIT", "ADD_MATERIAL", "COMPLETE", "CANCEL");

        // ── ad-hoc: available → issued at once ──
        var a1 = await w.DoAsync(so, "materials", new { variantUuid = w.Spare.VariantUuid, quantity = 2m, notes = "extra" });
        var spare = a1.Material(w.Spare);
        (spare.B("isAdhoc"), spare.D("issuedQuantity"), spare.S("status"), spare.B("canRemove")).Should().Be((true, 2m, "ISSUED", false), "TS-11");
        a1.S("status").Should().Be("IN_PROGRESS");
        (await w.OnHandAsync(w.Spare)).Should().Be(3m);
        (await w.Act(so, "materials", new { variantUuid = w.Subcon.VariantUuid, quantity = 1m }))
            .ShouldFail(HttpStatusCode.BadRequest, "a service is not an ad-hoc material", "service");

        // ── ad-hoc: unavailable → WAITING ──
        var a2 = await w.DoAsync(so, "materials", new { variantUuid = w.Scarce.VariantUuid, quantity = 1m });
        var scarce = a2.Material(w.Scarce);
        (scarce.D("issuedQuantity"), scarce.D("shortageQuantity"), scarce.B("canRemove")).Should().Be((0m, 1m, true), "TS-12");
        a2.S("status").Should().Be("WAITING");
        a2.Actions().Should().Contain("ADD_MATERIAL").And.NotContain("COMPLETE");
        scarce.S("supplyRequirementNumber").Should().NotBeNullOrEmpty("the shortage is raised as supply");
        (await w.Act(so, "complete", new { consumedMaterials = Array.Empty<object>(), customerSignature = true }))
            .ShouldFail(HttpStatusCode.BadRequest, "completes only from IN_PROGRESS");

        // ── dashboard ──
        var dash = await k.Ok(k.Get("/api/service-orders/dashboard"), "dashboard");
        dash.A("waitingForMaterials").Select(o => o.G("uuid")).Should().Contain(so);
        dash.A("today").Select(o => o.G("uuid")).Should().Contain(so);
        dash.A("mine").Select(o => o.G("uuid")).Should().Contain(so);
        dash.P("completionRate").I("scheduledThisWeek").Should().BeGreaterThan(0);

        // ── remove the unissued ad-hoc line (TS-32), not the others ──
        (await k.Delete($"/api/service-orders/{so}/materials/{a2.Material(w.Raw).G("uuid")}")).ShouldFail(HttpStatusCode.BadRequest, "BOM lines stay", "ad-hoc");
        (await k.Delete($"/api/service-orders/{so}/materials/{spare.G("uuid")}")).ShouldFail(HttpStatusCode.BadRequest, "issued lines stay", "issued");
        var r = await k.Ok(k.Delete($"/api/service-orders/{so}/materials/{scarce.G("uuid")}"), "remove the scarce ad-hoc line");
        r.A("materials").Should().NotContain(m => m.G("uuid") == scarce.G("uuid"), "D-18: hard delete");
        r.S("status").Should().Be("IN_PROGRESS", "nothing critical is missing any more");
        (await w.DemandsAsync(so)).Where(d => d.Variant == w.Scarce.VariantUuid).Should().OnlyContain(d => d.Status != "OPEN", "its demand is cancelled");
        (await w.SupplyAsync(so)).Where(x => x.Variant == w.Scarce.VariantUuid).Should().BeEmpty("the requirement row is gone with the line");

        // ── complete: refusals, then partial consumption ──
        var rawSmr = r.Material(w.Raw);
        var spareSmr = r.Material(w.Spare);
        (await w.Act(so, "complete", new { consumedMaterials = Consume((rawSmr, 5m), (spareSmr, 2m)), customerSignature = true }))
            .ShouldFail(HttpStatusCode.BadRequest, "SVC-COMP-01", "cannot exceed issued");
        (await w.Act(so, "complete", new { consumedMaterials = Consume((rawSmr, 3m)), customerSignature = true }))
            .ShouldFail(HttpStatusCode.BadRequest, "every issued material is confirmed", "All issued materials");

        var c = await w.DoAsync(so, "complete", new
        {
            consumedMaterials = Consume((rawSmr, 3m), (spareSmr, 2m)), actualHours = 3.5m, completionNotes = "done", customerSignature = true
        });
        c.S("status").Should().Be("COMPLETED");
        (c.D("actualHours"), c.S("completionNotes"), c.B("customerSignature")).Should().Be((3.5m, "done", true));
        var rawDone = c.Material(w.Raw);
        (rawDone.D("issuedQuantity"), rawDone.D("consumedQuantity"), rawDone.D("returnedQuantity"), rawDone.S("status")).Should().Be((4m, 3m, 1m, "CONSUMED"));
        (await w.OnHandAsync(w.Raw)).Should().Be(7m, "SVC-COMP-05: the unused 1 is returned");
        (await w.OnHandAsync(w.Spare)).Should().Be(3m, "spare fully consumed");
        (await w.DemandsAsync(so)).Should().OnlyContain(d => d.Status != "OPEN", "no demand stays open after completion");
        (await w.ReservedAsync(w.Raw), await w.ReservedAsync(w.Spare)).Should().Be((0m, 0m), "nothing stays held for a completed job");
        c.Actions().Should().BeEquivalentTo("EDIT", "CLOSE");

        // ── ledger: +issue, −return, net = consumed (D-9) ──
        var ledger = await k.Ok(k.Get($"/api/service-orders/{so}/ledger"), "ledger");
        var entries = ledger.A("entries");
        entries.Select(e => (e.G("variantUuid"), e.D("quantity"), e.S("movementType"))).Should().BeEquivalentTo(new[]
        {
            (w.Raw.VariantUuid, 4m, "SERVICE_ISSUE"), (w.Raw.VariantUuid, -1m, "SERVICE_RETURN"), (w.Spare.VariantUuid, 2m, "SERVICE_ISSUE")
        });
        entries.Should().OnlyContain(e => e.S("entryType") == "DEBIT" && e.S("warehouseName") == w.Wh.Name && !string.IsNullOrEmpty(e.S("sourceDocumentNumber")));
        ledger.A("netByProduct").Select(n => (n.G("variantUuid"), n.D("netQuantity"))).Should().BeEquivalentTo(new[] { (w.Raw.VariantUuid, 3m), (w.Spare.VariantUuid, 2m) });
        c.A("ledger").Should().HaveCount(3, "the detail carries the same ledger");
        (await w.Act(so, "complete", new { consumedMaterials = Consume((rawSmr, 3m), (spareSmr, 2m)), customerSignature = true }))
            .ShouldFail(HttpStatusCode.BadRequest, "completes once");
        (await w.Act(so, "cancel", new { reason = "too late" })).ShouldFail(HttpStatusCode.BadRequest, "a completed order is not cancelled");

        // ── close ──
        var closed = await w.DoAsync(so, "close");
        closed.S("status").Should().Be("CLOSED");
        closed.Actions().Should().NotContain(new[] { "CLOSE", "CANCEL", "COMPLETE", "START" });
        (await w.Act(so, "close")).ShouldFail(HttpStatusCode.BadRequest, "closes once");

        // ── list: filters and paging ──
        var other = await w.CreateOrderAsync(w.Svc, 1m, priority: 0, date: PreOrder.Day(PreOrder.Today.AddDays(1)));
        var all = await k.Ok(k.Get("/api/service-orders?page=1&pageSize=25"), "list");
        all.I("totalRecords").Should().Be(2);
        all.A("data").Select(o => o.G("uuid")).Should().Equal(so, other);   // scheduled date asc

        var page1 = await k.Ok(k.Get("/api/service-orders?page=1&pageSize=1"), "page 1");
        var page2 = await k.Ok(k.Get("/api/service-orders?page=2&pageSize=1"), "page 2");
        (page1.I("totalRecords"), page1.I("totalPages"), page1.A("data").Single().G("uuid"), page2.A("data").Single().G("uuid")).Should().Be((2, 2, so, other));

        (await k.Ok(k.Get("/api/service-orders?status=CLOSED,DRAFT&page=1&pageSize=25"), "status filter")).A("data").Should().HaveCount(2);
        (await k.Ok(k.Get("/api/service-orders?status=DRAFT&page=1&pageSize=25"), "draft only")).A("data").Single().G("uuid").Should().Be(other);
        (await k.Ok(k.Get($"/api/service-orders?fromDate={Today}&toDate={Today}&page=1&pageSize=25"), "date filter")).A("data").Single().G("uuid").Should().Be(so);
        (await k.Ok(k.Get("/api/service-orders?priority=0&page=1&pageSize=25"), "priority filter")).A("data").Single().G("uuid").Should().Be(other);
        (await k.Ok(k.Get($"/api/service-orders?customerUuid={w.Customer.Uuid}&assignedUserId={w.AdminUserId}&page=1&pageSize=25"), "customer + assignee"))
            .A("data").Should().HaveCount(2);
        (await k.Ok(k.Get($"/api/service-orders?search={Uri.EscapeDataString(closed.S("serviceNumber")!)}&page=1&pageSize=25"), "search"))
            .A("data").Single().G("uuid").Should().Be(so);
        (await k.Ok(k.Get($"/api/service-orders?customerUuid={Guid.NewGuid()}&page=1&pageSize=25"), "unknown customer")).A("data").Should().BeEmpty();
    }

    // ── Shortage, then cancel after issue (D-16) ─────────────────────────────────

    [Fact]
    public async Task A_shortage_waits_for_stock_and_a_cancel_after_issue_returns_everything()
    {
        var w = await _root.WorldAsync("SHT", rawStock: 1m, spareStock: 0m);
        var k = w.K;
        await w.StandardServiceBomAsync();

        var so = await w.CreateOrderAsync(w.Svc, 2m);
        var p = await w.DoAsync(so, "plan");
        p.S("status").Should().Be("MATERIAL_PENDING", $"4 raw needed, 1 in stock — {J.Short(p)}");
        p.S("materialReadiness").Should().BeOneOf("PARTIAL", "SHORTAGE");
        var raw = p.Material(w.Raw);
        (raw.D("reservedQuantity"), raw.D("shortageQuantity"), raw.S("status")).Should().Be((1m, 3m, "PARTIALLY_RESERVED"));
        raw.S("supplyRequirementNumber").Should().NotBeNullOrEmpty("ST-03: the shortage raises a supply requirement");
        p.Actions().Should().NotContain("START");
        (await w.Act(so, "start")).ShouldFail(HttpStatusCode.BadRequest, "only READY starts");
        var dash = await k.Ok(k.Get("/api/service-orders/dashboard"), "dashboard");
        dash.A("waitingForMaterials").Select(o => o.G("uuid")).Should().Contain(so);

        // stock arrives; "Reserve all available"
        await k.StockUpAsync(w.Vendor, w.Wh, (w.Raw, 5m, 5m));
        var a = await w.DoAsync(so, "materials/allocate");
        a.S("status").Should().Be("READY", J.Short(a));
        a.Material(w.Raw).D("reservedQuantity").Should().Be(4m);
        (await w.OnHandAsync(w.Raw)).Should().Be(6m);

        var s = await w.DoAsync(so, "start");
        s.Material(w.Raw).D("issuedQuantity").Should().Be(4m);
        (await w.OnHandAsync(w.Raw)).Should().Be(2m);

        // cancel after issue
        (await w.Act(so, "cancel", new { reason = "" })).ShouldFail(HttpStatusCode.BadRequest, "a reason is required", "reason");
        var c = await w.DoAsync(so, "cancel", new { reason = "customer withdrew" });
        c.S("status").Should().Be("CANCELLED");
        (await w.OnHandAsync(w.Raw)).Should().Be(6m, "D-16: everything issued comes back");
        var rawC = c.A("materials").Single(m => m.G("variantUuid") == w.Raw.VariantUuid);
        (rawC.S("status"), rawC.D("returnedQuantity")).Should().Be(("RETURNED", 4m));
        (await w.DemandsAsync(so)).Should().OnlyContain(d => d.Status != "OPEN", "every hold is released");
        (await w.SupplyAsync(so)).Should().NotBeEmpty().And.OnlyContain(x => x.Status == "CANCELLED" || x.Status == "FULFILLED",
            "open supply requirements are cancelled (SVC-08)");
        c.Actions().Should().BeEmpty("a cancelled order allows nothing");
        c.A("ledger").Should().BeEmpty("only completion writes the ledger");
        (await w.Act(so, "cancel", new { reason = "again" })).ShouldFail(HttpStatusCode.BadRequest, "cancels once");
    }

    // ── WAITING, then the stock arrives ──────────────────────────────────────────

    [Fact]
    public async Task A_waiting_job_gets_its_late_material_issued_once_the_stock_arrives()
    {
        var w = await _root.WorldAsync("WAI", rawStock: 0m, spareStock: 0m);
        var k = w.K;
        await k.ConfigureServiceAsync(w.Svc, hasBom: false);
        var so = await w.CreateOrderAsync(w.Svc, 1m);
        await w.DoAsync(so, "plan");
        await w.DoAsync(so, "start");
        var waiting = await w.DoAsync(so, "materials", new { variantUuid = w.Scarce.VariantUuid, quantity = 2m });
        waiting.S("status").Should().Be("WAITING");

        await k.StockUpAsync(w.Vendor, w.Wh, (w.Scarce, 5m, 5m));
        // The GRN page's "Run allocation" (receipt no longer allocates by itself, A31 C10): one run for the variant.
        await k.Ok(k.Post("/api/allocations/run", new { variantUuid = w.Scarce.VariantUuid, warehouseUuid = w.Wh.Uuid }), "run allocation from the GRN");
        var afterReceipt = await w.OrderAsync(so);
        afterReceipt.Material(w.Scarce).D("reservedQuantity").Should().Be(2m, "the run reserved the late material for the job");

        // Whatever the receipt's allocation run did, "Reserve all available" (or the next action) must get the material issued.
        var allocate = await w.Act(so, "materials/allocate");
        var now = await w.OrderAsync(so);
        var m = now.Material(w.Scarce);
        (now.S("status"), m.D("issuedQuantity"), m.S("status")).Should().Be(("IN_PROGRESS", 2m, "ISSUED"),
            $"the late material is issued once it is in stock — after receipt: {afterReceipt.S("status")} reserved {afterReceipt.Material(w.Scarce).D("reservedQuantity")}; allocate: {allocate}");
        (await w.OnHandAsync(w.Scarce)).Should().Be(3m);
    }

    // ── TS-28 / SVC-COMP-04 ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_labour_only_time_and_material_service_needs_actual_hours_and_leaves_an_empty_ledger()
    {
        var w = await _root.WorldAsync("LAB", rawStock: 0m, spareStock: 0m);
        await w.K.ConfigureServiceAsync(w.Svc, "TIME_AND_MATERIAL", hasBom: false);

        var unassigned = await w.CreateOrderAsync(w.Svc, 1m, assign: false);
        (await w.Act(unassigned, "plan")).ShouldFail(HttpStatusCode.BadRequest, "plan needs a technician or a team", "Assign");

        var so = await w.CreateOrderAsync(w.Svc, 1m);
        (await w.OrderAsync(so)).S("invoicingPolicy").Should().Be("TIME_AND_MATERIAL");
        var p = await w.DoAsync(so, "plan");
        (p.S("status"), p.S("materialReadiness"), p.A("materials").Count, p.IsNull("bomId")).Should().Be(("READY", "NOT_APPLICABLE", 0, true), "SVC-05");
        (await w.DoAsync(so, "start")).S("status").Should().Be("IN_PROGRESS");

        (await w.Act(so, "complete", new { consumedMaterials = Array.Empty<object>(), customerSignature = false }))
            .ShouldFail(HttpStatusCode.BadRequest, "SVC-COMP-04", "Actual hours required for Time & Material billing");
        var c = await w.DoAsync(so, "complete", new { consumedMaterials = Array.Empty<object>(), actualHours = 2.25m, customerSignature = false });
        (c.S("status"), c.D("actualHours")).Should().Be(("COMPLETED", 2.25m));
        var ledger = await w.K.Ok(w.K.Get($"/api/service-orders/{so}/ledger"), "ledger");
        (ledger.A("entries").Count, ledger.A("netByProduct").Count).Should().Be((0, 0), "TS-28");
    }

    // ── Permissions and the feature switch ───────────────────────────────────────

    [Fact]
    public async Task Without_a_service_permission_or_without_the_feature_the_endpoints_refuse()
    {
        var w = await _root.WorldAsync("SEC", rawStock: 0m, spareStock: 0m);
        var k = w.K;
        await k.ConfigureServiceAsync(w.Svc, hasBom: false);
        var so = await w.CreateOrderAsync(w.Svc, 1m);

        var nobody = await k.LoginWithPermissionsAsync("nosvc", "SALE_ORDER_VIEW");
        (await k.Get("/api/service-orders?page=1&pageSize=25", nobody)).ShouldFail(HttpStatusCode.Forbidden, "no SERVICE_ORDER_VIEW");
        (await k.Get($"/api/service-orders/{so}", nobody)).ShouldFail(HttpStatusCode.Forbidden, "no SERVICE_ORDER_VIEW");

        var viewer = await k.LoginWithPermissionsAsync("svcview", "SERVICE_ORDER_VIEW");
        (await k.Get($"/api/service-orders/{so}", viewer)).Status.Should().Be(HttpStatusCode.OK);
        (await w.TryCreateOrder(w.Svc, 1m, w.AdminUserId, client: viewer)).ShouldFail(HttpStatusCode.Forbidden, "no SERVICE_ORDER_CREATE");
        (await w.Act(so, "plan", null, viewer)).ShouldFail(HttpStatusCode.Forbidden, "no SERVICE_ORDER_EDIT");
        (await w.Act(so, "cancel", new { reason = "x" }, viewer)).ShouldFail(HttpStatusCode.Forbidden, "no SERVICE_ORDER_CANCEL");
        (await w.Act(so, "complete", new { consumedMaterials = Array.Empty<object>(), customerSignature = true }, viewer))
            .ShouldFail(HttpStatusCode.Forbidden, "no SERVICE_ORDER_COMPLETE");
        (await w.OrderAsync(so)).S("status").Should().Be("DRAFT", "nothing the viewer tried went through");

        // D-11: the feature off
        await _root.SetFeatureAsync(w.OrgId, "MODULE_SERVICES", false);
        // A37 D-6: an organization that once had the module keeps reading its records; every write is refused.
        (await k.Get("/api/service-orders?page=1&pageSize=25")).Status.Should().Be(HttpStatusCode.OK, "A37 D-6: read-only once unlicensed");
        (await w.Act(so, "plan")).ShouldFail(HttpStatusCode.Forbidden, "MODULE_SERVICES is off");
        (await w.TryCreateOrder(w.Svc, 1m, w.AdminUserId)).ShouldFail(HttpStatusCode.Forbidden, "MODULE_SERVICES is off");
        (await k.Get("/api/boms")).Status.Should().Be(HttpStatusCode.OK, "D-19: BOMs stay open with MODULE_MANUFACTURING");

        // …and a confirmed sale order with a service line raises no service order, nor a delivery for it.
        var sale = await k.CreateOrderAsync(w.Customer, w.Pkr, "SELF_PICKUP", null, RLine(w.Svc, 1m));
        await k.ConfirmAsync(sale);
        var detail = await k.GetSaleOrderAsync(sale);
        detail.A("serviceOrders").Should().BeEmpty();
        detail.A("lines").Single().B("isService").Should().BeTrue();
        (await k.F.QueryAsync("SELECT COUNT(*) AS N FROM material.service_orders WHERE SourceUuid = @s", ("@s", sale)))[0]["N"].Should().Be(0);
        (await k.SoDeliveriesAsync(sale)).Should().BeEmpty("service lines never get a delivery");
    }

    // ── TS-17/18/19: sale orders ─────────────────────────────────────────────────

    [Fact]
    public async Task A_mixed_sale_order_delivers_its_stock_line_and_performs_its_service_line()
    {
        var w = await _root.WorldAsync("MIX", rawStock: 10m, spareStock: 0m);
        var k = w.K;
        await w.K.ConfigureServiceAsync(w.Svc);
        await w.K.ActiveServiceBomAsync(w.Svc, BomLine(w.Raw, 2m));
        var widget = await k.CreateProductAsync("Widget", 10m, 40m);
        await k.StockUpAsync(w.Vendor, w.Wh, (widget, 5m, 10m));

        var sale = await k.CreateOrderAsync(w.Customer, w.Pkr, "SELF_PICKUP", null, RLine(widget, 2m), RLine(w.Svc, 1m));
        var confirmed = await k.ConfirmAsync(sale);
        J.Short(confirmed).Should().NotContain("deliveryCreationFailed\":true");

        var so1 = await k.GetSaleOrderAsync(sale);
        var svcLine = so1.LineOf(w.Svc);
        var widgetLine = so1.LineOf(widget);
        (svcLine.B("isService"), widgetLine.B("isService")).Should().Be((true, false));
        svcLine.D("reservedQty").Should().Be(0m, "D-10: a service line is not reserved");

        // TS-17: one delivery, the stock line only
        var deliveries = await k.SoDeliveriesAsync(sale);
        deliveries.Should().ContainSingle();
        var delivery = await k.DeliveryAsync(deliveries[0].G("uuid"));
        delivery.A("lines").Select(l => l.NG("variantUuid")).Should().Equal(widget.VariantUuid);

        // TS-18: one DRAFT service order for the service line, traced to the sale order
        var refs = so1.A("serviceOrders");
        var r = refs.Should().ContainSingle().Which;
        (r.G("soLineUuid"), r.S("status"), r.D("quantity"), r.I("lineNumber")).Should().Be((svcLine.G("uuid"), "DRAFT", 1m, 2));
        var svcOrder = r.G("serviceOrderUuid");
        var d = await w.OrderAsync(svcOrder);
        (d.S("sourceType"), d.NG("sourceUuid"), d.NG("sourceLineUuid"), d.S("sourceReference"), d.G("customerUuid"))
            .Should().Be(("SALES_ORDER", (Guid?)sale, (Guid?)svcLine.G("uuid"), so1.S("soNumber"), w.Customer.Uuid));
        d.G("traceId").Should().Be(so1.G("traceId"), "D-14: the order's trace id is the sale order's");
        (await k.TryConfirm(sale)).Status.Should().NotBe(HttpStatusCode.OK, "an order confirms once");
        (await k.GetSaleOrderAsync(sale)).A("serviceOrders").Should().ContainSingle("Ensure is idempotent per (order, line); a re-read raises nothing new");

        // TS-19: assign, plan, start, complete → the service line is fulfilled, the order partly
        var assigned = await k.Ok(w.TryUpdate(d, d.S("notes"), w.AdminUserId, w.Wh.Uuid), "assign the service order");
        assigned.I("assignedUserId").Should().Be(w.AdminUserId);
        (await w.DoAsync(svcOrder, "plan")).S("status").Should().Be("READY");
        var started = await w.DoAsync(svcOrder, "start");
        await w.DoAsync(svcOrder, "complete", new { consumedMaterials = Consume((started.Material(w.Raw), 2m)), customerSignature = true });

        var so2 = await k.GetSaleOrderAsync(sale);
        (so2.LineOf(w.Svc).D("fulfilledQty"), so2.LineOf(w.Svc).S("status")).Should().Be((1m, "FULFILLED"));
        so2.S("status").Should().Be("PARTIALLY_FULFILLED", "the stock line is still to be delivered");
        so2.A("serviceOrders").Single().S("status").Should().Be("COMPLETED");

        // deliver the stock line → FULFILLED
        await k.DeliverAsync(sale);
        var so3 = await k.GetSaleOrderAsync(sale);
        so3.S("status").Should().Be("FULFILLED", J.Short(so3));
        so3.LineOf(widget).D("fulfilledQty").Should().Be(2m);
    }

    [Fact]
    public async Task A_service_only_sale_order_is_fulfilled_by_its_service_and_a_cancel_cascades_to_a_not_started_one()
    {
        var w = await _root.WorldAsync("SVO", rawStock: 0m, spareStock: 0m);
        var k = w.K;
        await k.ConfigureServiceAsync(w.Svc, hasBom: false);

        // Service-only: FULFILLED on completion
        var sale = await k.CreateOrderAsync(w.Customer, w.Pkr, "SELF_PICKUP", null, RLine(w.Svc, 1m));
        await k.ConfirmAsync(sale);
        var so1 = await k.GetSaleOrderAsync(sale);
        var svcOrder = so1.A("serviceOrders").Should().ContainSingle().Which.G("serviceOrderUuid");
        (await k.SoDeliveriesAsync(sale)).Should().BeEmpty();
        var d = await w.OrderAsync(svcOrder);
        await k.Ok(w.TryUpdate(d, null, w.AdminUserId, w.Wh.Uuid), "assign");
        await w.DoAsync(svcOrder, "plan");
        await w.DoAsync(svcOrder, "start");
        (await k.GetSaleOrderAsync(sale)).S("status").Should().Be("CONFIRMED", "a running service fulfils nothing yet");
        await w.DoAsync(svcOrder, "complete", new { consumedMaterials = Array.Empty<object>(), customerSignature = true });
        var done = await k.GetSaleOrderAsync(sale);
        (done.S("status"), done.LineOf(w.Svc).D("fulfilledQty")).Should().Be(("FULFILLED", 1m), J.Short(done));
        await w.DoAsync(svcOrder, "close");
        (await k.GetSaleOrderAsync(sale)).S("status").Should().Be("FULFILLED", "closing keeps it fulfilled");

        // SO cancel → its not-started service order is cancelled
        var sale2 = await k.CreateOrderAsync(w.Customer, w.Pkr, "SELF_PICKUP", null, RLine(w.Svc, 2m));
        await k.ConfirmAsync(sale2);
        var svc2 = (await k.GetSaleOrderAsync(sale2)).A("serviceOrders").Single().G("serviceOrderUuid");
        var cancel = await k.Ok(k.TryCancelOrder(sale2, "customer cancelled"), "cancel the sale order");
        cancel.A("cancelledServiceOrders").Select(s => s.G("serviceOrderUuid")).Should().Equal(svc2);
        cancel.A("runningServiceOrders").Should().BeEmpty();
        (await w.OrderAsync(svc2)).S("status").Should().Be("CANCELLED");
        (await k.GetSaleOrderAsync(sale2)).S("status").Should().Be("CANCELLED");

        // …while a started one is kept and reported
        var sale3 = await k.CreateOrderAsync(w.Customer, w.Pkr, "SELF_PICKUP", null, RLine(w.Svc, 1m));
        await k.ConfirmAsync(sale3);
        var svc3 = (await k.GetSaleOrderAsync(sale3)).A("serviceOrders").Single().G("serviceOrderUuid");
        await k.Ok(w.TryUpdate(await w.OrderAsync(svc3), null, w.AdminUserId, w.Wh.Uuid), "assign");
        await w.DoAsync(svc3, "plan");
        await w.DoAsync(svc3, "start");
        var cancel3 = await k.Ok(k.TryCancelOrder(sale3, "customer cancelled"), "cancel the sale order");
        cancel3.A("cancelledServiceOrders").Should().BeEmpty();
        cancel3.A("runningServiceOrders").Select(s => s.G("serviceOrderUuid")).Should().Equal(svc3);
        (await w.OrderAsync(svc3)).S("status").Should().Be("IN_PROGRESS", "a running job is not stopped behind the technician's back");
    }

    // ── TS-31 ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Two_service_orders_competing_for_scarce_stock_are_served_by_priority()
    {
        var w = await _root.WorldAsync("PRI", rawStock: 0m, spareStock: 0m);
        var k = w.K;
        await k.ConfigureServiceAsync(w.Svc);
        await k.ActiveServiceBomAsync(w.Svc, BomLine(w.Scarce, 2m));

        var normal = await w.CreateOrderAsync(w.Svc, 1m, priority: 1);   // older, normal
        var urgent = await w.CreateOrderAsync(w.Svc, 1m, priority: 3);   // newer, urgent
        (await w.DoAsync(normal, "plan")).S("status").Should().Be("MATERIAL_PENDING");
        (await w.DoAsync(urgent, "plan")).S("status").Should().Be("MATERIAL_PENDING");

        await k.StockUpAsync(w.Vendor, w.Wh, (w.Scarce, 3m, 5m));   // enough for one and a half
        await w.DoAsync(normal, "materials/allocate");               // asked by the normal one first: the run still serves by priority

        var u = await w.OrderAsync(urgent);
        var n = await w.OrderAsync(normal);
        (u.S("status"), u.Material(w.Scarce).D("reservedQuantity")).Should().Be(("READY", 2m), $"TS-31: urgent first — {J.Short(u)}");
        (n.S("status"), n.Material(w.Scarce).D("reservedQuantity")).Should().Be(("MATERIAL_PENDING", 1m), "the normal one gets what is left");
    }
}
