using System.Net;
using System.Text.Json;
using FluentAssertions;
using SMS.Integration.Tests.SapAlignment;
using SMS.Integration.Tests.FulfillmentRoutes;
using Xunit;
using static SMS.Integration.Tests.SalesPreOrder.PreOrder;

namespace SMS.Integration.Tests.SalesPreOrder;

/// <summary>
/// A32-PF-06 — the sale order flows that existed before A32 still work on the real host (LocalDB), and the computed
/// reservedQty / delivery indicator agree with every other path that holds stock:
/// <list type="bullet">
/// <item>an order made the A29 way (no source, no customer PO) confirms, is delivered in two parts and invoiced; its
/// indicator walks RED → BLUE → (a delivery holding part) BLUE → YELLOW (part fulfilled, T-C4-08) → GREEN (T-C4-09),
/// and Release/Reserve leave what a delivery holds alone;</item>
/// <item>a material issue request's hold (source MIR) is neither counted in the order's reservedQty nor freed by the
/// order's release or cancel, and the order's manual reserve sees only what MIR left free.</item>
/// </list>
/// The other consumers (production, the MIR/MIV cycle, allocation inside delivery release) are covered by re-running
/// their own classes — see the A32 QA report.
/// <para>Run alone: <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~SalesPreOrderRegressionE2ETests</c>.</para>
/// </summary>
public sealed class SalesPreOrderRegressionE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _k;

    public SalesPreOrderRegressionE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _k = new SapKit(factory, "PF6");
    }

    private async Task<(Guid Pkr, Guid Gst, Warehouse Wh, Partner Vendor, Partner Customer)> SetupAsync()
    {
        var pkr = await _k.PkrBaseAsync();
        var gst = await _k.EnsureTaxCodeAsync("GST17", 17m, "SALES", isDefault: true);
        await _k.EnsureTaxCodeAsync("PGST17", 17m, "PURCHASE", isDefault: true);
        await _k.CreateApproverPlaceholdersAsync();
        return (pkr, gst, await _k.CreateWarehouseAsync(), await _k.CreateVendorAsync("Reg Vendor"), await _k.CreateCustomerAsync("Reg Customer"));
    }

    private async Task<JsonElement> LineOf(Guid so) => (await _k.GetSaleOrderAsync(so)).A("lines").Single();

    private async Task<decimal> LedgerAsync(string sourceType, Guid source)
    {
        var rows = await _f.QueryAsync(
            "SELECT ISNULL(SUM(ReservedQty), 0) AS Q FROM inventory.StockReservations WHERE SourceType = @t AND SourceUuid = @s AND Status = 'ACTIVE'",
            ("@t", sourceType), ("@s", source));
        return Convert.ToDecimal(rows[0]["Q"]);
    }

    /// <summary>SapKit.DeliverAsync, but for part of a line: create-delivery with a quantity → release → pick → pack → pickup.</summary>
    private async Task<Guid> DeliverPartAsync(Guid so, Guid soLine, decimal qty, Func<Task>? afterRelease = null)
    {
        var delivery = (await _k.Ok(_k.Post($"/api/sale-orders/{so}/create-delivery", new
        {
            Lines = new[] { new { SourceLineUuid = soLine, Qty = qty } }
        }), "create a delivery for part of the line")).GetGuid();
        await _k.Ok(_k.Post($"/api/logistics/deliveries/{delivery}/release", new { OnShortage = "BLOCK" }), "release delivery");
        if (afterRelease is not null) await afterRelease();

        var pickList = (await _k.Ok(_k.Post($"/api/logistics/deliveries/{delivery}/pick-list", new { Notes = "a32 qa" }), "pick list")).GetGuid();
        var pl = await _k.Ok(_k.Get($"/api/logistics/pick-lists/{pickList}"), "read pick list");
        await _k.Ok(_k.Post($"/api/logistics/pick-lists/{pickList}/confirm", new
        {
            Lines = pl.A("lines").Select(l => new { LineUuid = l.G("uuid"), QtyPicked = l.D("qtyToPick") }).ToArray()
        }), "confirm picks");
        var detail = await _k.Ok(_k.Get($"/api/logistics/deliveries/{delivery}"), "read delivery");
        await _k.Ok(_k.Post($"/api/logistics/deliveries/{delivery}/packages", new
        {
            PackageType = "BOX",
            Contents = detail.A("lines").Where(l => l.D("qtyPicked") > 0).Select(l => new { DeliveryLineUuid = l.G("uuid"), Qty = l.D("qtyPicked") }).ToArray()
        }), "pack");
        await _k.Ok(_k.Post($"/api/logistics/deliveries/{delivery}/pickup", new
        {
            PickupPersonName = "QA Collector", PickupPersonIdType = "CNIC", PickupPersonIdNumber = "35202-1234567-1"
        }), "pickup");
        (await _k.Ok(_k.Get($"/api/logistics/deliveries/{delivery}"), "read delivery")).S("status").Should().Be("DELIVERED");
        return delivery;
    }

    [Fact]
    public async Task An_order_made_the_A29_way_confirms_delivers_in_parts_and_invoices_with_the_indicator_following_along()
    {
        var (pkr, gst, wh, vendor, customer) = await SetupAsync();
        // The A29 way raises each delivery by hand, part by part. Since A33 (D-1) confirming creates the deliveries
        // itself unless the org switches that off, so this org does, as one that keeps manual deliveries would.
        await _k.SetSaleOrderConfigAsync(("autoCreateDeliveriesOnConfirm", false));
        var item = await _k.CreateProductAsync("Reg Widget", purchasePrice: 60m, sellingPrice: 100m);
        await _k.StockUpAsync(vendor, wh, (item, 20m, 60m));

        // The A29 request shape: no source, no customer PO.
        var so = await _k.CreateSaleOrderAsync(customer, pkr, SapKit.Line(item, 6m, taxCode: gst));
        var draft = await _k.GetSaleOrderAsync(so);
        (draft.S("status"), draft.S("sourceType"), draft.IsNull("sourceQuotation"), draft.IsNull("customerPoReference")).Should().Be(("DRAFT", "MANUAL", true, true));
        (draft.A("lines").Single().S("deliveryIndicator"), draft.D("grandTotal")).Should().Be(("RED", 702m));

        await _k.ConfirmSaleOrderAsync(so);
        var line = await LineOf(so);
        var lineUuid = line.G("uuid");
        (line.S("status"), line.D("reservedQty"), line.D("reservableQty"), line.S("deliveryIndicator")).Should().Be(("RESERVED", 6m, 0m, "BLUE"));

        // Part 1: 2 of 6. While the delivery holds them, the order holds the other 4 — still BLUE, nothing reservable,
        // and Release frees only what the order itself holds (never the delivery's).
        var d1 = await DeliverPartAsync(so, lineUuid, 2m, afterRelease: async () =>
        {
            var held = await LineOf(so);
            (held.D("reservedQty"), held.D("reservableQty"), held.S("deliveryIndicator")).Should().Be((4m, 0m, "BLUE"),
                "4 held by the order + 2 on the released delivery cover the line");
            (await LedgerAsync("SALES_ORDER", so)).Should().Be(4m);
            (await _k.Post($"/api/sale-orders/{so}/lines/{lineUuid}/reserve", new { })).Status
                .Should().Be(HttpStatusCode.BadRequest, "nothing left to reserve while a delivery carries the rest");
        });

        var part = await _k.GetSaleOrderAsync(so);
        part.S("status").Should().Be("PARTIALLY_FULFILLED");
        var pl = part.A("lines").Single();
        (pl.D("fulfilledQty"), pl.D("reservedQty"), pl.D("reservableQty"), pl.S("deliveryIndicator")).Should().Be((2m, 4m, 0m, "YELLOW"),
            "T-C4-08: part fulfilled is YELLOW whatever is held");

        var inv1 = (await _k.RaiseInvoiceAsync(d1)).G("invoiceUuid");
        await _k.IssueAsync(inv1);
        (await _k.GetInvoiceAsync(inv1)).S("status").Should().NotBe("DRAFT");

        // A manual release and re-reserve on a partly fulfilled order still work, and only on what the order holds.
        (await _k.Ok(_k.Post($"/api/sale-orders/{so}/lines/{lineUuid}/release", new { Quantity = 1m }), "release 1"))
            .D("reservedQty").Should().Be(3m);
        (await _k.Ok(_k.Post($"/api/sale-orders/{so}/lines/{lineUuid}/reserve", new { }), "reserve it back"))
            .D("reservedQty").Should().Be(4m);

        // Part 2: the rest → GREEN (T-C4-09).
        var d2 = await DeliverPartAsync(so, lineUuid, 4m);
        var done = await _k.GetSaleOrderAsync(so);
        done.S("status").Should().Be("FULFILLED");
        var dl = done.A("lines").Single();
        (dl.D("fulfilledQty"), dl.D("reservedQty"), dl.D("reservableQty"), dl.S("deliveryIndicator")).Should().Be((6m, 0m, 0m, "GREEN"));
        (await LedgerAsync("SALES_ORDER", so)).Should().Be(0m, "everything was consumed at goods issue");
        (await _k.Post($"/api/sale-orders/{so}/lines/{lineUuid}/release", new { })).Status.Should().Be(HttpStatusCode.BadRequest);
        (await _k.Post($"/api/sale-orders/{so}/reserve-all", new { })).Status.Should().Be(HttpStatusCode.BadRequest, "a FULFILLED order holds nothing more");

        var inv2 = (await _k.RaiseInvoiceAsync(d2)).G("invoiceUuid");
        await _k.IssueAsync(inv2);
        (await LineOf(so)).S("deliveryIndicator").Should().Be("GREEN");
    }

    [Fact]
    public async Task A_material_issue_requests_hold_is_neither_counted_nor_freed_by_the_sale_order()
    {
        var (pkr, _, wh, vendor, customer) = await SetupAsync();
        var item = await _k.CreateProductAsync("Shared Widget", purchasePrice: 5m, sellingPrice: 9m);
        await _k.StockUpAsync(vendor, wh, (item, 10m, 5m));

        // A MIR approved for 6 holds them in the shared ledger (source MIR).
        var adminId = int.Parse(new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler()
            .ReadJwtToken(_f.AdminAccessToken).Claims.First(c => c.Type == "sub").Value);
        await _f.ExecuteAsync("UPDATE auth.UserAccounts SET SupervisorId = UserID WHERE UserID = @id", ("@id", adminId));
        var whId = (await _k.Ok(_k.Get("/api/warehouses"), "warehouses")).Items().Single(w => w.G("uuid") == wh.Uuid).I("id");
        var project = (await _k.Ok(_k.Post("/api/projects", new
        {
            ProjectCode = $"P{Guid.NewGuid():N}"[..10].ToUpperInvariant(), ProjectName = _k.Next("Project"), ProjectManagerId = adminId
        }), "project")).GetGuid();
        var mir = (await _k.Ok(_k.Post("/api/material-issue-requests", new
        {
            RequestType = "PROJECT", ProjectUuid = project,
            Lines = new[] { new { VariantUuid = item.VariantUuid, RequestedQty = 6m, WarehouseId = whId } }
        }), "MIR")).GetGuid();
        var approval = (await _k.Ok(_k.Post($"/api/material-issue-requests/{mir}/workflow/submit"), "MIR submit")).GetGuid();
        for (var i = 0; i < 8; i++)
        {
            await _k.Ok(_k.Post($"/api/material-issue-requests/{mir}/workflow/approve", new { ApprovalUUID = approval, LineApprovals = Array.Empty<object>() }), "MIR approve");
            if ((await _k.Ok(_k.Get($"/api/material-issue-requests/{mir}"), "MIR")).S("status") is "APPROVED" or "PARTIALLY_APPROVED") break;
        }
        (await LedgerAsync("MIR", mir)).Should().Be(6m, "the approved MIR holds its stock");

        // The sale order for 10 confirms against the 4 left: a SPLIT hold of 4, and MIR's 6 are not "reserved" for it.
        var so = await _k.CreateSaleOrderAsync(customer, pkr, SapKit.Line(item, 10m));
        await _k.Ok(_k.Post($"/api/sale-orders/{so}/confirm"), "confirm");
        var line = await LineOf(so);
        (line.S("fulfillmentMode"), line.D("reservedQty"), line.D("reservableQty"), line.S("deliveryIndicator")).Should().Be(("SPLIT", 4m, 6m, "YELLOW"));

        var none = await _k.Ok(_k.Post($"/api/sale-orders/{so}/lines/{line.G("uuid")}/reserve", new { AllowPartial = true }), "reserve the rest");
        none.S("outcome").Should().Be("NONE_AVAILABLE", "the 6 MIR holds are not free");
        (await LedgerAsync("MIR", mir)).Should().Be(6m);

        (await _k.Ok(_k.Post($"/api/sale-orders/{so}/lines/{line.G("uuid")}/release", new { }), "release the order's hold")).D("changedQty").Should().Be(4m);
        (await LedgerAsync("MIR", mir)).Should().Be(6m, "the order's release never touches another source's hold");
        await _k.Ok(_k.Post($"/api/sale-orders/{so}/reserve-all", new { }), "reserve all again");
        await _k.Ok(_k.Post($"/api/sale-orders/{so}/cancel", new { Reason = "regression" }), "cancel");
        (await LedgerAsync("SALES_ORDER", so)).Should().Be(0m);
        (await LedgerAsync("MIR", mir)).Should().Be(6m, "cancelling the order frees its holds only");
        (await _k.Ok(_k.Get($"/api/material-issue-requests/{mir}"), "MIR")).S("status").Should().BeOneOf("APPROVED", "PARTIALLY_APPROVED");
    }
}
