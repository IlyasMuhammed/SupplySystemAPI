using System.Net;
using FluentAssertions;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using SMS.Shared.Common;
using Xunit;
using static SMS.Integration.Tests.FulfillmentRoutes.Routes;
using static SMS.Integration.Tests.RouteClassification.Rc;
using static SMS.Integration.Tests.SalesPreOrder.PreOrder;

namespace SMS.Integration.Tests.RouteClassification;

/// <summary>
/// A34 D-24 on the real host (LocalDB): every new action at the permission levels of API-CONTRACT §2, with real logins
/// whose custom roles hold exactly the codes named (the frontend guards ask for the same codes, so these are also the
/// frontend-parity checks: a user the page lets in gets a 200).
/// <list type="bullet">
/// <item><b>Lead times:</b> defaults read any-of / write LEAD_TIME_DEFAULTS_MANAGE (seeded to Inventory Manager and Supply
/// Dept Admin); variant lead times read any-of / write STOCK_MANAGE; calculate any of nine codes.</item>
/// <item><b>Sales lines:</b> ⏱ needs the document's EDIT code; the delivery date SALE_ORDER_EDIT.</item>
/// <item><b>Production:</b> the recovery any of SALE_ORDER_CONFIRM / PROD_CREATE; "Create delivery now" DELIVERY_CREATE;
/// the SO detail's productionOrders[] needs only SALE_ORDER_VIEW; the PO's live deliveries DELIVERY_VIEW.</item>
/// </list>
/// The ratchet (every action gated) and the anonymous-endpoint list are Security/*'s; they run in the PF-06 regression.
/// <para>Run alone: <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~RouteClassificationRbacE2ETests</c>.</para>
/// </summary>
public sealed class RouteClassificationRbacE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _k;

    public RouteClassificationRbacE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _k = new SapKit(factory, "RB");
    }

    private static readonly string[] LeadReadCodes = ["LEAD_TIME_DEFAULTS_MANAGE", "INVENTORY_VIEW", "STOCK_MANAGE", "SALE_ORDER_VIEW"];

    private static readonly string[] CalculateCodes =
    [
        "SALE_ORDER_VIEW", "SALE_ORDER_CREATE", "SALE_ORDER_EDIT", "SALE_INQUIRY_VIEW", "SALE_INQUIRY_EDIT",
        "SALE_QUOTATION_VIEW", "SALE_QUOTATION_EDIT", "INVENTORY_VIEW", "STOCK_MANAGE"
    ];

    private static readonly object Defaults = new
    {
        PickPackDays = 1, ShippingLeadTimeDays = 3, SalesBufferDays = 1, ManufacturingBufferDays = 0, QualityInspectionDays = 0, InternalTransferDays = 0
    };

    private static string Tag(string code) => code.Replace("_", "")[..Math.Min(10, code.Replace("_", "").Length)];

    [Fact]
    public async Task Lead_time_endpoints_answer_exactly_their_codes()
    {
        (await _f.QueryAsync("SELECT 1 AS X FROM auth.Permissions WHERE Code = 'LEAD_TIME_DEFAULTS_MANAGE'"))
            .Should().ContainSingle("D-24: the new code is seeded");
        var p = await _k.CreateProductAsync("Rbac Lead Widget", 5m, 9m);
        Task<Api> GetDefaults(HttpClient c) => _k.Get("/api/lead-time/defaults", c);
        Task<Api> PutDefaults(HttpClient c) => _k.Put("/api/lead-time/defaults", Defaults, c);
        Task<Api> GetVariant(HttpClient c) => _k.Get($"/api/variants/{p.VariantUuid}/lead-times", c);
        Task<Api> PutVariant(HttpClient c) => _k.Put($"/api/variants/{p.VariantUuid}/lead-times", new { PickPackDays = 2 }, c);
        Task<Api> Calculate(HttpClient c) => _k.Post("/api/lead-time/calculate", new { VariantUuid = p.VariantUuid, Quantity = 1m }, c);

        // none, and an unrelated code
        foreach (var who in new[] { await _k.LoginWithPermissionsAsync("ltnone"), await _k.LoginWithPermissionsAsync("ltunrel", "DELIVERY_VIEW") })
            foreach (var api in new[] { await GetDefaults(who), await PutDefaults(who), await GetVariant(who), await PutVariant(who), await Calculate(who) })
                api.ShouldBe(HttpStatusCode.Forbidden, "no lead-time code");

        // each read code reads; only its own write code writes
        foreach (var code in LeadReadCodes)
        {
            var who = await _k.LoginWithPermissionsAsync(Tag(code), code);
            (await GetDefaults(who)).ShouldBe(HttpStatusCode.OK, $"{code} reads the defaults (the settings page guard)");
            (await GetVariant(who)).ShouldBe(HttpStatusCode.OK, $"{code} reads a variant's lead times");
            (await PutDefaults(who)).ShouldBe(code == "LEAD_TIME_DEFAULTS_MANAGE" ? HttpStatusCode.OK : HttpStatusCode.Forbidden, $"{code} on PUT defaults");
            (await PutVariant(who)).ShouldBe(code == "STOCK_MANAGE" ? HttpStatusCode.OK : HttpStatusCode.Forbidden, $"{code} on PUT variant lead times");
        }

        // calculate: any of nine
        foreach (var code in CalculateCodes)
            (await Calculate(await _k.LoginWithPermissionsAsync($"c{Tag(code)}", code))).ShouldBe(HttpStatusCode.OK, $"{code} calculates");
        (await _k.Post("/api/lead-time/calculate-manufacturing", new { VariantUuid = p.VariantUuid, Quantity = 1m },
            await _k.LoginWithPermissionsAsync("cmnone", "DELIVERY_VIEW"))).ShouldBe(HttpStatusCode.Forbidden, "calculate-manufacturing has the same gate");

        // seeded by role code (D-24): Inventory Manager and Supply Dept Admin save the defaults; a warehouse operator can't.
        (await PutDefaults(await _k.LoginAsNewUserAsync((int)EnumRole.InventoryManager, "ltim"))).ShouldBe(HttpStatusCode.OK, "Inventory Manager");
        (await PutDefaults(await _k.LoginAsNewUserAsync((int)EnumRole.SupplyDeptAdmin, "ltsda"))).ShouldBe(HttpStatusCode.OK, "Supply Dept Admin");
        (await PutDefaults(await _k.LoginAsNewUserAsync((int)EnumRole.WarehouseOperator, "ltwo"))).ShouldBe(HttpStatusCode.Forbidden, "not seeded");
    }

    [Fact]
    public async Task Sales_line_and_production_actions_answer_exactly_their_codes()
    {
        var w = await _k.MtoWorldAsync("RBS", rawStock: 30m);

        // Three open documents, one line each.
        var inquiry = (await _k.Ok(_k.Post("/api/sale-inquiries", new
        {
            PartnerId = w.Customer.Uuid, CustomerReference = _k.Next("RFQ"),
            Lines = new object[] { new { VariantUuid = w.Fg.VariantUuid, ProductDescription = w.Fg.Name, RequestedQuantity = 1m } }
        }), "inquiry")).GetGuid();
        var inquiryLine = (await _k.Ok(_k.Get($"/api/sale-inquiries/{inquiry}"), "read")).A("lines").Single().G("uuid");
        var quotation = await _k.DraftQuotationAsync(w.Customer, w.Pkr, w.Fg, 1m);
        var quotationLine = (await _k.Ok(_k.Get($"/api/sale-quotations/{quotation}"), "read")).A("lines").Single().G("uuid");
        var so = await _k.CreateOrderAsync(w.Customer, w.Pkr, "SHIP", w.Address, RLine(w.Fg, 2m));
        var soLine = (await _k.GetSaleOrderAsync(so)).LineOf(w.Fg).G("uuid");

        Task<Api> InqLt(HttpClient c) => _k.Post($"/api/sale-inquiries/{inquiry}/lines/{inquiryLine}/lead-time", new { }, c);
        Task<Api> QuoLt(HttpClient c) => _k.Post($"/api/sale-quotations/{quotation}/lines/{quotationLine}/lead-time", new { }, c);
        Task<Api> SoLt(HttpClient c) => _k.Post($"/api/sale-orders/{so}/lines/{soLine}/lead-time", new { }, c);
        Task<Api> SoDate(HttpClient c) => _k.Put($"/api/sale-orders/{so}/lines/{soLine}/delivery-date", new { ManualDeliveryDate = Day(Today.AddDays(30)) }, c);

        var inqView = await _k.LoginWithPermissionsAsync("iview", "SALE_INQUIRY_VIEW");
        var inqEdit = await _k.LoginWithPermissionsAsync("iedit", "SALE_INQUIRY_EDIT");
        var quoView = await _k.LoginWithPermissionsAsync("qview", "SALE_QUOTATION_VIEW");
        var quoEdit = await _k.LoginWithPermissionsAsync("qedit", "SALE_QUOTATION_EDIT");
        var soViewCreate = await _k.LoginWithPermissionsAsync("sview", "SALE_ORDER_VIEW", "SALE_ORDER_CREATE");
        var soEdit = await _k.LoginWithPermissionsAsync("sedit", "SALE_ORDER_EDIT");

        (await InqLt(inqView)).ShouldBe(HttpStatusCode.Forbidden, "inquiry ⏱ needs SALE_INQUIRY_EDIT");
        (await InqLt(inqEdit)).ShouldBe(HttpStatusCode.OK, "SALE_INQUIRY_EDIT");
        (await QuoLt(quoView)).ShouldBe(HttpStatusCode.Forbidden, "quotation ⏱ needs SALE_QUOTATION_EDIT");
        (await QuoLt(quoEdit)).ShouldBe(HttpStatusCode.OK, "SALE_QUOTATION_EDIT");
        (await SoLt(soViewCreate)).ShouldBe(HttpStatusCode.Forbidden, "SO ⏱ needs SALE_ORDER_EDIT");
        (await SoLt(soEdit)).ShouldBe(HttpStatusCode.OK, "SALE_ORDER_EDIT");
        (await SoDate(soViewCreate)).ShouldBe(HttpStatusCode.Forbidden, "the delivery date needs SALE_ORDER_EDIT");
        (await SoDate(soEdit)).ShouldBe(HttpStatusCode.OK, "SALE_ORDER_EDIT");
        (await SoLt(inqEdit)).ShouldBe(HttpStatusCode.Forbidden, "another document's EDIT code is not enough");

        // ── Production recovery: SALE_ORDER_CONFIRM or PROD_CREATE ──
        var po = (await _k.ConfirmAsync(so)).A("productionOrders").Single().G("productionOrderUuid");
        foreach (var (code, expected) in new[]
                 {
                     ("SALE_ORDER_CONFIRM", HttpStatusCode.OK), ("PROD_CREATE", HttpStatusCode.OK),
                     ("SALE_ORDER_EDIT", HttpStatusCode.Forbidden), ("PROD_VIEW", HttpStatusCode.Forbidden)
                 })
        {
            var r = await _k.TryCreateProductionOrders(so, await _k.LoginWithPermissionsAsync($"r{Tag(code)}", code));
            r.ShouldBe(expected, $"create-production-orders with {code}");
            if (expected == HttpStatusCode.OK) r.Result.A("productionOrders").Single().B("created").Should().BeFalse("idempotent");
        }

        // ── D-25: the detail's productionOrders[] needs only SALE_ORDER_VIEW ──
        var viewer = await _k.LoginWithPermissionsAsync("soview", "SALE_ORDER_VIEW");
        var seen = await _k.Ok(_k.Get($"/api/sale-orders/{so}", viewer), "SO detail with SALE_ORDER_VIEW only");
        seen.A("productionOrders").Single().G("productionOrderUuid").Should().Be(po);
        (await _k.Get($"/api/production-orders/{po}", viewer)).ShouldBe(HttpStatusCode.Forbidden, "the PO itself still needs PROD_VIEW");

        // ── "Create delivery now": DELIVERY_CREATE ──
        await _k.IssueAndStartAsync(po);
        await _k.ReportAndCompleteAsync(po, 2m);
        await _k.Ok(_k.TryInspect(po, await _k.InspectorAsync(), 2m, 0m), "QI");
        await _k.Ok(_k.TryFgr(po, 1m), "FGR 1 of 2: received, not yet COMPLETED");
        foreach (var code in new[] { "PROD_CREATE", "FGR_CREATE", "DELIVERY_VIEW" })
            (await _k.TryCreateDeliveryNow(po, await _k.LoginWithPermissionsAsync($"d{Tag(code)}", code))).ShouldBe(HttpStatusCode.Forbidden, $"{code} is not DELIVERY_CREATE");
        var creator = await _k.LoginWithPermissionsAsync("dcreate", "DELIVERY_CREATE");
        (await _k.Ok(_k.TryCreateDeliveryNow(po, creator), "DELIVERY_CREATE creates the delivery")).D("quantityCreated").Should().Be(1m);

        // The PO's live deliveries: DELIVERY_VIEW.
        var dv = await _k.LoginWithPermissionsAsync("dview", "DELIVERY_VIEW");
        (await _k.DeliveriesFromPoAsync(po, dv)).Should().ContainSingle();
        (await _k.Get($"/api/logistics/deliveries?productionOrderUuid={po}", viewer)).ShouldBe(HttpStatusCode.Forbidden, "SALE_ORDER_VIEW is not DELIVERY_VIEW");

        // Route categories are route writes: FULFILLMENT_ROUTE_MANAGE only.
        var route = await _k.CreateCategoryRouteAsync("RBC", Stock, "PICK", "GOODS_ISSUE");
        (await _k.TryUpdateRouteCategory(route, Manufacture, await _k.LoginWithPermissionsAsync("rview", "FULFILLMENT_ROUTE_VIEW", "FULFILLMENT_ROUTE_ASSIGN")))
            .ShouldBe(HttpStatusCode.Forbidden, "VIEW / ASSIGN don't write routes");
        (await _k.TryUpdateRouteCategory(route, Manufacture, await _k.LoginWithPermissionsAsync("rmanage", "FULFILLMENT_ROUTE_MANAGE")))
            .ShouldBe(HttpStatusCode.OK, "MANAGE changes the category");
    }
}
