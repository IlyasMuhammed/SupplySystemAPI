using System.Net;
using FluentAssertions;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using SMS.Shared.Common;
using Xunit;
using static SMS.Integration.Tests.FulfillmentRoutes.Routes;

namespace SMS.Integration.Tests.FulfillmentRoutes;

/// <summary>
/// A33-PF-04: every new or changed endpoint at the permission levels of API-CONTRACT §2 (D-9), with real logins
/// whose custom roles hold exactly the codes named:
/// <list type="bullet">
/// <item><b>Routes:</b> none → 403. Each of the six read codes alone → GET 200, writes 403. MANAGE → every write
/// 200. ASSIGN → reads 200, route writes 403. The built-in Inventory Manager holds VIEW / MANAGE / ASSIGN (seeded).</item>
/// <item><b>Variant route:</b> PUT …/fulfillment-route and assign-by-category need ASSIGN (MANAGE isn't enough).
/// The ungated variant PATCH can't set a route (C-9).</item>
/// <item><b>Sale orders:</b> the line override needs only SALE_ORDER_CREATE/EDIT; the preview's GET needs
/// SALE_ORDER_VIEW and its POST any of CREATE / EDIT / VIEW. The confirmer needs no delivery code to get the
/// auto-created deliveries. The recovery create-deliveries needs DELIVERY_CREATE.</item>
/// <item><b>Deliveries:</b> DELIVERY_APPROVE gates approve. Advance needs any of EDIT / DISPATCH / APPROVE, then
/// the operation's own code (403). The existing DELIVERY_EDIT / PICKING / DISPATCH / SHIPMENT_BOOK / POD_CAPTURE
/// codes still walk an SO delivery end to end, and an Inventory Manager can't approve.</item>
/// </list>
/// <para>Run alone: <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~FulfillmentRouteRbacE2ETests</c>.</para>
/// </summary>
public sealed class FulfillmentRouteRbacE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _k;

    public FulfillmentRouteRbacE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _k = new SapKit(factory, "RBAC");
    }

    private static readonly string[] ReadCodes =
    [
        "FULFILLMENT_ROUTE_VIEW", "FULFILLMENT_ROUTE_MANAGE", "FULFILLMENT_ROUTE_ASSIGN", "SALE_ORDER_VIEW", "INVENTORY_VIEW", "DELIVERY_VIEW"
    ];

    /// <summary>Every route write, as the given user; returns the statuses by name.</summary>
    private async Task<Dictionary<string, HttpStatusCode>> RouteWritesAsync(HttpClient who, Guid existing)
    {
        var s = new Dictionary<string, HttpStatusCode>
        {
            ["POST"] = (await _k.TryCreateRouteAsync($"RB_{Guid.NewGuid():N}"[..12].ToUpperInvariant(), "rbac", ["PICK", "GOODS_ISSUE"], who)).Status,
            ["PUT"] = (await _k.Put($"/api/fulfillment-routes/{existing}", new { Name = "renamed", DisplayOrder = 99 }, who)).Status,
            ["DEACTIVATE"] = (await _k.RoutePatch(existing, "deactivate", who)).Status,
            ["ACTIVATE"] = (await _k.RoutePatch(existing, "activate", who)).Status,
            ["SET_DEFAULT"] = (await _k.RoutePatch(existing, "set-default", who)).Status,
            ["CLEAR_DEFAULT"] = (await _k.RoutePatch(existing, "clear-default", who)).Status,
            ["DELETE"] = (await _k.Delete($"/api/fulfillment-routes/{existing}", who)).Status
        };
        return s;
    }

    [Fact]
    public async Task Route_endpoints_answer_none_view_manage_and_assign_as_the_contract_says()
    {
        var pickOnly = await _k.RouteUuidAsync(PickOnly);
        var target = (await _k.CreateRouteAsync("RBT", "PICK", "GOODS_ISSUE")).G("uuid");

        // none, and an unrelated code
        foreach (var who in new[] { await _k.LoginWithPermissionsAsync("none"), await _k.LoginWithPermissionsAsync("unrelated", "REPORT_VIEW") })
        {
            (await _k.Get("/api/fulfillment-routes", who)).ShouldBe(HttpStatusCode.Forbidden, "no read code");
            (await _k.Get($"/api/fulfillment-routes/{pickOnly}", who)).ShouldBe(HttpStatusCode.Forbidden, "no read code");
            (await RouteWritesAsync(who, target)).Values.Should().OnlyContain(s => s == HttpStatusCode.Forbidden);
        }

        // each read code alone reads; only MANAGE writes
        foreach (var code in ReadCodes)
        {
            var who = await _k.LoginWithPermissionsAsync(code.Replace("_", "")[..10], code);
            (await _k.Get("/api/fulfillment-routes?includeInactive=true", who)).ShouldBe(HttpStatusCode.OK, $"{code} reads the routes");
            (await _k.Get($"/api/fulfillment-routes/{pickOnly}", who)).ShouldBe(HttpStatusCode.OK, $"{code} reads a route");
            if (code == "FULFILLMENT_ROUTE_MANAGE") continue;
            (await RouteWritesAsync(who, target)).Values.Should().OnlyContain(s => s == HttpStatusCode.Forbidden, $"{code} doesn't write routes");
        }

        // MANAGE: every write goes through (the custom route ends deleted)
        var manager = await _k.LoginWithPermissionsAsync("manage", "FULFILLMENT_ROUTE_MANAGE");
        try
        {
            var writes = await RouteWritesAsync(manager, target);
            writes.Should().OnlyContain(kv => kv.Value == HttpStatusCode.OK, string.Join(", ", writes.Select(kv => $"{kv.Key}={(int)kv.Value}")));
            (await _k.Get($"/api/fulfillment-routes/{target}")).ShouldBe(HttpStatusCode.NotFound, "the manager deleted it");
        }
        finally
        {
            await _k.Ok(_k.RoutePatch(pickOnly, "set-default"), "restore the SELF_PICKUP default");
        }
        // …but MANAGE doesn't assign
        var someVariant = (await _k.CreateProductAsync("Rbac Widget", 1m, 2m)).VariantUuid;
        (await _k.AssignVariantRoute(someVariant, pickOnly, manager)).ShouldBe(HttpStatusCode.Forbidden, "MANAGE is not ASSIGN");
        (await _k.Post($"/api/fulfillment-routes/{pickOnly}/assign-by-category", new { CategoryId = 999999 }, manager))
            .ShouldBe(HttpStatusCode.Forbidden, "MANAGE is not ASSIGN");

        // The built-in Inventory Manager is seeded VIEW + MANAGE + ASSIGN (D-9).
        var invMgr = await _k.LoginAsNewUserAsync((int)EnumRole.InventoryManager, "invmgr");
        (await _k.Get("/api/fulfillment-routes", invMgr)).ShouldBe(HttpStatusCode.OK, "Inventory Manager reads routes");
        var created = await _k.TryCreateRouteAsync($"IM_{Guid.NewGuid():N}"[..12].ToUpperInvariant(), "inv mgr", ["PICK", "GOODS_ISSUE"], invMgr);
        created.ShouldBe(HttpStatusCode.OK, "Inventory Manager manages routes");
        (await _k.Delete($"/api/fulfillment-routes/{created.Result.G("uuid")}", invMgr)).ShouldBe(HttpStatusCode.OK, "an unused custom route deletes");
    }

    [Fact]
    public async Task Variant_route_assignment_needs_ASSIGN_and_the_ungated_variant_patch_cannot_set_a_route()
    {
        var pickPackShip = await _k.RouteUuidAsync(PickPackShip);
        var p = await _k.CreateProductAsync("Assign Widget", 1m, 2m);

        var viewer = await _k.LoginWithPermissionsAsync("invview", "INVENTORY_VIEW");
        var assigner = await _k.LoginWithPermissionsAsync("assign", "FULFILLMENT_ROUTE_ASSIGN", "INVENTORY_VIEW");

        (await _k.AssignVariantRoute(p.VariantUuid, pickPackShip, viewer)).ShouldBe(HttpStatusCode.Forbidden, "INVENTORY_VIEW doesn't assign");
        (await _k.Post($"/api/fulfillment-routes/{pickPackShip}/assign-by-category", new { CategoryId = 999999 }, viewer))
            .ShouldBe(HttpStatusCode.Forbidden, "bulk assign needs ASSIGN");

        var assigned = await _k.AssignVariantRoute(p.VariantUuid, pickPackShip, assigner);
        assigned.ShouldBe(HttpStatusCode.OK, "ASSIGN assigns");
        (assigned.Result.G("variantUuid"), assigned.Result.S("fulfillmentRouteCode")).Should().Be((p.VariantUuid, PickPackShip));
        (await _k.Post($"/api/fulfillment-routes/{pickPackShip}/assign-by-category", new { CategoryId = 999999 }, assigner))
            .Status.Should().NotBe(HttpStatusCode.Forbidden, "ASSIGN reaches bulk assign (an unknown category is a 400)");

        // C-9: the variant PATCH is gated by the feature only, so it must not move the route.
        var before = (await _k.Ok(_k.Get($"/api/products/{p.ProductId}"), "read product")).A("variants").Single();
        await _k.Ok(_k.Patch($"/api/variants/{p.VariantUuid}", new
        {
            VariantName = before.S("variantName"), PurchasePrice = 1m, SellingPrice = 2m, IsDefault = true,
            IsAvailableForRetail = true, IsAvailableForMirMiv = true, FulfillmentRouteUuid = (Guid?)null
        }, viewer), "the ungated variant patch");
        (await _k.Ok(_k.Get($"/api/products/{p.ProductId}"), "read product")).A("variants").Single().NG("fulfillmentRouteUuid")
            .Should().Be(pickPackShip, "PATCH api/variants ignores route fields");

        // Clearing (T-C2-04) is ASSIGN too.
        (await _k.AssignVariantRoute(p.VariantUuid, null, viewer)).ShouldBe(HttpStatusCode.Forbidden, "clearing is ASSIGN");
        (await _k.AssignVariantRoute(p.VariantUuid, null, assigner)).ShouldBe(HttpStatusCode.OK, "ASSIGN clears");
    }

    [Fact]
    public async Task Sale_order_route_work_and_every_delivery_step_answer_their_own_codes()
    {
        var pkr = await _k.PkrBaseAsync();
        await _k.EnsureTaxCodeAsync("PGST17", 17m, "PURCHASE", isDefault: true);
        await _k.CreateApproverPlaceholdersAsync();
        var wh = await _k.CreateWarehouseAsync();
        var vendor = await _k.CreateVendorAsync("Rbac Vendor");
        var customer = await _k.CreateCustomerAsync("Rbac Customer");
        var address = await _k.ShippingAddressAsync(customer);
        var p = await _k.CreateProductAsync("Rbac Valve", 10m, 20m);
        var q = await _k.CreateProductAsync("Rbac Drum", 10m, 20m);
        await _k.StockUpAsync(vendor, wh, (p, 20m, 10m), (q, 20m, 10m));
        var approval = (await _k.CreateRouteAsync("APPR", "PICK", "PACK", "APPROVAL", "GOODS_ISSUE", "SHIP")).G("uuid");
        var pickAndShip = await _k.RouteUuidAsync(PickAndShip);

        // ── Sale order side ──
        var none     = await _k.LoginWithPermissionsAsync("sonone");
        var soView   = await _k.LoginWithPermissionsAsync("soview", "SALE_ORDER_VIEW");
        var soCreate = await _k.LoginWithPermissionsAsync("socreate", "SALE_ORDER_CREATE", "SALE_ORDER_VIEW");
        var confirmer = await _k.LoginWithPermissionsAsync("soconfirm", "SALE_ORDER_CONFIRM", "SALE_ORDER_VIEW");
        var kCreate = new SapKit(_f, "RBSO", soCreate);

        // The line override needs no route code (contract §2).
        var so = await kCreate.CreateOrderAsync(customer, pkr, "SHIP", address, RLine(p, 4m, route: approval), RLine(q, 3m, route: pickAndShip));
        (await kCreate.GetSaleOrderAsync(so)).A("lines").Select(l => l.S("routeSource")).Should().Equal("LINE_OVERRIDE", "LINE_OVERRIDE");

        (await _k.Get($"/api/sale-orders/{so}/delivery-preview", soView)).ShouldBe(HttpStatusCode.OK, "SALE_ORDER_VIEW previews");
        (await _k.Get($"/api/sale-orders/{so}/delivery-preview", none)).ShouldBe(HttpStatusCode.Forbidden, "no code, no preview");
        var unsaved = new { DeliveryMode = "SHIP", ShippingAddressId = address, Lines = new[] { new { VariantUuid = p.VariantUuid, Quantity = 1m, FulfillmentRouteUuid = (Guid?)null } } };
        foreach (var who in new[] { soView, soCreate })
            (await _k.Post("/api/sale-orders/delivery-preview", unsaved, who)).ShouldBe(HttpStatusCode.OK, "the form's preview");
        (await _k.Post("/api/sale-orders/delivery-preview", unsaved, none)).ShouldBe(HttpStatusCode.Forbidden, "no code, no preview");

        // The confirmer holds no delivery code; the auto-created deliveries are the system's doing.
        var confirmed = await new SapKit(_f, "RBCF", confirmer).ConfirmAsync(so);
        confirmed.A("deliveries").Should().HaveCount(2);
        var dAppr = confirmed.A("deliveries").Single(d => d.G("routeUuid") == approval).G("deliveryUuid");
        var dPas  = confirmed.A("deliveries").Single(d => d.G("routeUuid") == pickAndShip).G("deliveryUuid");

        // Recovery needs DELIVERY_CREATE.
        var dlvView   = await _k.LoginWithPermissionsAsync("dlvview", "DELIVERY_VIEW");
        var dlvCreate = await _k.LoginWithPermissionsAsync("dlvcreate", "DELIVERY_CREATE", "DELIVERY_VIEW");
        (await _k.Post($"/api/sale-orders/{so}/create-deliveries", null, dlvView)).ShouldBe(HttpStatusCode.Forbidden, "recovery is DELIVERY_CREATE");
        var again = await _k.Post($"/api/sale-orders/{so}/create-deliveries", null, dlvCreate);
        again.ShouldBe(HttpStatusCode.OK, "DELIVERY_CREATE recovers");
        again.Result.A("created").Should().BeEmpty("idempotent: everything is already on a delivery");

        // ── Delivery side: the existing codes walk SO deliveries; approve and advance answer their own codes ──
        var operatorClient = await _k.LoginWithPermissionsAsync("operator",
            "DELIVERY_VIEW", "DELIVERY_EDIT", "PICKING", "DISPATCH", "SHIPMENT_BOOK", "POD_CAPTURE");
        var approver = await _k.LoginWithPermissionsAsync("approver", "DELIVERY_VIEW", "DELIVERY_APPROVE");
        var invMgr = await _k.LoginAsNewUserAsync((int)EnumRole.InventoryManager, "invmgr2");
        var op = new SapKit(_f, "RBOP", operatorClient);

        foreach (var d in new[] { dAppr, dPas })
        {
            (await _k.Advance(d, null, dlvView)).ShouldBe(HttpStatusCode.Forbidden, "DELIVERY_VIEW can't advance");
            (await _k.Release(d, dlvView)).ShouldBe(HttpStatusCode.Forbidden, "DELIVERY_VIEW can't release");
        }
        // advance's own-code check: the approver may call advance, but DRAFT → release is DELIVERY_EDIT.
        (await _k.Advance(dAppr, "DRAFT", approver)).ShouldBe(HttpStatusCode.Forbidden, "release through advance needs DELIVERY_EDIT");

        // PICK_AND_SHIP, by the operator alone with the existing codes, to DELIVERED.
        await op.ReleaseAsync(dPas);
        await op.PickAllAsync(dPas);
        (await op.DeliveryStatusAsync(dPas)).Should().Be("STAGED");
        await op.Ok(op.GoodsIssue(dPas), "goods issue with DISPATCH");
        await op.ShipAndProveAsync(dPas, await _k.ManualCarrierAsync());
        (await op.DeliveryStatusAsync(dPas)).Should().Be("DELIVERED");

        // The APPROVAL route: the operator gets it to STAGED, but can't approve.
        await op.Ok(op.Advance(dAppr, "DRAFT"), "advance → release with DELIVERY_EDIT");
        await op.PickAllAsync(dAppr);
        await op.PackAllAsync(dAppr);
        (await op.DeliveryStatusAsync(dAppr)).Should().Be("STAGED", "the route has no STAGE step, so PACKED is auto-staged");
        (await op.Approve(dAppr)).ShouldBe(HttpStatusCode.Forbidden, "approve is DELIVERY_APPROVE");
        (await op.Advance(dAppr, "STAGED")).ShouldBe(HttpStatusCode.Forbidden, "the next operation is the approval, which needs DELIVERY_APPROVE");
        (await op.GoodsIssue(dAppr)).ShouldBe(HttpStatusCode.BadRequest, "not approved yet");
        (await _k.Approve(dAppr, invMgr)).ShouldBe(HttpStatusCode.Forbidden, "the Inventory Manager isn't seeded DELIVERY_APPROVE");

        // The approver approves (through advance) but can't goods-issue.
        var adv = await _k.Ok(_k.Advance(dAppr, "STAGED", approver), "advance → approve with DELIVERY_APPROVE");
        adv.S("status").Should().Be("STAGED");
        (await _k.DeliveryAsync(dAppr)).IsNull("approvedAt").Should().BeFalse();
        (await _k.Advance(dAppr, "STAGED", approver)).ShouldBe(HttpStatusCode.Forbidden, "goods issue is DISPATCH");
        (await _k.GoodsIssue(dAppr, approver)).ShouldBe(HttpStatusCode.Forbidden, "goods issue is DISPATCH");
        await op.Ok(op.Advance(dAppr, "STAGED"), "the operator issues it");
        (await op.DeliveryStatusAsync(dAppr)).Should().Be("GOODS_ISSUED");
    }
}
