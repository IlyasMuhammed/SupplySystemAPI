using FluentAssertions;
using SMS.Modules.Demand.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>
/// A34 PA-08 / D-5 (T-C2-03): the confirm gate's manufacturing blockers for make-to-order lines — one batched
/// IManufacturingReadiness call, one blocker per line (first match), the exact API-CONTRACT §6.2 messages — shown live on
/// the DRAFT detail and the preview, and refusing confirm. Also the preview's productionLines[] (§6.3), each line's
/// effectiveRouteCategory (§6.1) and the SELF_PICKUP hint (C-16 / R-17).
/// </summary>
public class SaleOrderManufactureGateTests
{
    [Fact]
    public async Task A_ready_make_to_order_line_has_no_blocker_and_shows_its_MANUFACTURE_category()
    {
        var h = A34SoHarness.Create();
        var mto = h.MakeToOrderVariant();
        var stock = Guid.NewGuid();
        var so = await h.DraftAsync((mto, null), (stock, null));

        var model = (await h.Service.GetByIdAsync(so))!;

        model.ConfirmBlockers.Should().BeEmpty();
        model.Lines.Single(l => l.VariantUuid == mto).EffectiveRouteCategory.Should().Be("MANUFACTURE");
        model.Lines.Single(l => l.VariantUuid == stock).EffectiveRouteCategory.Should().Be("STOCK");
        h.Readiness.Calls.Should().ContainSingle("one batched call").Which.Should().BeEquivalentTo([mto]);
    }

    [Fact]
    public async Task An_order_with_only_stock_lines_never_asks_Material()
    {
        var h = A34SoHarness.Create();
        var so = await h.DraftAsync((Guid.NewGuid(), null), (Guid.NewGuid(), h.PickOnly.Uuid));

        (await h.Service.GetByIdAsync(so))!.ConfirmBlockers.Should().BeEmpty();
        await h.Service.GetDeliveryPreviewAsync(so);

        h.Readiness.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task T_C2_03_each_readiness_failure_is_one_blocker_per_line_first_match_with_the_contract_message()
    {
        var h = A34SoHarness.Create();
        var notMade = h.MakeToOrderVariant("Pipe — 2in");
        h.Readiness.Ready(h.OrgId, notMade, "Pipe — 2in", manufactured: false, bom: false, warehouse: false);
        var noBom = h.MakeToOrderVariant("Widget — Red");
        h.Readiness.Ready(h.OrgId, noBom, "Widget — Red", bom: false, warehouse: false);
        var noWarehouse = h.MakeToOrderVariant("Gear — Blue");
        h.Readiness.Ready(h.OrgId, noWarehouse, "Gear — Blue", warehouse: false);
        var so = await h.DraftAsync((notMade, null), (noBom, null), (noWarehouse, null));

        var model = (await h.Service.GetByIdAsync(so))!;

        model.ConfirmBlockers.Select(b => (b.LineNumber, b.Code, b.Message)).Should().Equal(
            (1, "NOT_MANUFACTURED", "Line 1: Pipe — 2in is not a manufactured product, so it can't use the make-to-order route 'MFG_PICK_SHIP'. Choose a stock route, or set the product's supply method to MANUFACTURE."),
            (2, "BOM_MISSING", "Line 2: Widget — Red has no active bill of materials, so it can't be made to order. Activate a BOM, or choose a stock route for this line."),
            (3, "PRODUCTION_WAREHOUSE_MISSING", "Line 3: Gear — Blue has no default production warehouse, so it can't be made to order. Set one on the product, or choose a stock route for this line."));
        model.Lines.OrderBy(l => model.Lines.IndexOf(l)).Select(l => l.RouteBlocker)
            .Should().Equal("NOT_MANUFACTURED", "BOM_MISSING", "PRODUCTION_WAREHOUSE_MISSING");
        h.Readiness.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task Without_MODULE_MANUFACTURING_a_make_to_order_line_is_blocked_and_Material_is_not_asked()
    {
        var h = A34SoHarness.Create(manufacturing: false);
        var mto = h.MakeToOrderVariant();
        var so = await h.DraftAsync((mto, null));

        var blocker = (await h.Service.GetByIdAsync(so))!.ConfirmBlockers.Should().ContainSingle().Subject;

        blocker.Code.Should().Be("MANUFACTURING_DISABLED");
        blocker.Message.Should().Be("Line 1: 'MFG_PICK_SHIP' is a make-to-order route, but manufacturing is not enabled for your organization. Choose a stock route for this line.");
        h.Readiness.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Confirm_is_refused_with_every_blocker_and_nothing_is_reserved()
    {
        var h = A34SoHarness.Create();
        var noBom = h.MakeToOrderVariant("Widget — Red");
        h.Readiness.Ready(h.OrgId, noBom, "Widget — Red", bom: false);
        var stock = Guid.NewGuid();
        h.Available[stock] = 100m;
        var so = await h.DraftAsync((stock, null), (noBom, null));

        var refused = await FluentActions.Awaiting(() => h.Service.ConfirmWithResultAsync(so, A34SoHarness.User))
            .Should().ThrowAsync<BadRequestException>();

        refused.Which.Message.Should().Be("Line 2: Widget — Red has no active bill of materials, so it can't be made to order. Activate a BOM, or choose a stock route for this line.");
        h.Reserved.Should().BeEmpty();
        (await h.ReadAsync(so)).Status.Should().Be("DRAFT");
        h.Production.CreateCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task The_preview_lists_make_to_order_lines_apart_and_counts_only_stock_deliveries()
    {
        var h = A34SoHarness.Create();
        var mto = h.MakeToOrderVariant();
        var so = await h.DraftAsync((Guid.NewGuid(), null), (mto, null));

        var preview = (await h.Service.GetDeliveryPreviewAsync(so))!;

        preview.CanConfirm.Should().BeTrue();
        preview.DeliveryCount.Should().Be(1);
        preview.Groups.Should().ContainSingle().Which.LineNumbers.Should().Equal(1);
        var production = preview.ProductionLines.Should().ContainSingle().Subject;
        production.LineNumber.Should().Be(2);
        production.VariantUuid.Should().Be(mto);
        production.Quantity.Should().Be(10m);
        production.RouteUuid.Should().Be(h.MfgShip.Uuid);
        production.RouteCode.Should().Be("MFG_PICK_SHIP");
        production.Steps.Should().Equal("PICK", "GOODS_ISSUE", "SHIP");
        production.Message.Should().Be("A production order will be created; its delivery follows when production completes.");
        preview.Lines.Select(l => l.EffectiveRouteCategory).Should().Equal("STOCK", "MANUFACTURE");
    }

    [Fact]
    public async Task The_unsaved_form_preview_shows_the_blockers_too()
    {
        var h = A34SoHarness.Create();
        var noBom = h.MakeToOrderVariant("Widget — Red");
        h.Readiness.Ready(h.OrgId, noBom, "Widget — Red", bom: false);

        var preview = await h.Service.PreviewDeliveriesAsync(new SaleOrderDeliveryPreviewRequest
        {
            DeliveryMode = "SHIP", ShippingAddressId = Guid.NewGuid(),
            Lines = [new SaleOrderDeliveryPreviewLineRequest { VariantUuid = noBom, Quantity = 4m }]
        });

        preview.CanConfirm.Should().BeFalse();
        preview.Blockers.Should().ContainSingle().Which.Code.Should().Be("BOM_MISSING");
        preview.ProductionLines.Should().BeEmpty("a blocked line is not made");
        preview.Lines.Single().RouteBlocker.Should().Be("BOM_MISSING");
    }

    [Fact]
    public async Task R_17_a_collected_order_whose_make_to_order_line_ships_is_told_how_to_collect_it()
    {
        var h = A34SoHarness.Create();
        var mto = h.MakeToOrderVariant();
        var so = await h.DraftAsync("SELF_PICKUP", null, null, (mto, null));

        var blocker = (await h.Service.GetByIdAsync(so))!.ConfirmBlockers.Should().ContainSingle().Subject;

        blocker.Code.Should().Be("SHIPPING_ADDRESS_REQUIRED");
        blocker.Message.Should().EndWith(" For collection, create a MANUFACTURE route without the SHIP step.");
    }

    [Fact]
    public async Task A_manufacture_route_without_SHIP_is_collected_without_an_address()
    {
        var h = A34SoHarness.Create();
        var mto = h.MakeToOrderVariant();
        var so = await h.DraftAsync("SELF_PICKUP", null, null, (mto, h.MfgPickup.Uuid));

        (await h.Service.GetByIdAsync(so))!.ConfirmBlockers.Should().BeEmpty();
    }

    [Fact]
    public async Task A_stock_order_collecting_keeps_the_A33_message_unchanged()
    {
        var h = A34SoHarness.Create();
        var so = await h.DraftAsync("SELF_PICKUP", null, null, (Guid.NewGuid(), h.PickAndShip.Uuid));

        var blocker = (await h.Service.GetByIdAsync(so))!.ConfirmBlockers.Should().ContainSingle().Subject;

        blocker.Message.Should().Be("Cannot confirm: some lines are shipped (their fulfillment route has a Ship step), so the order needs a shipping address.");
    }
}
