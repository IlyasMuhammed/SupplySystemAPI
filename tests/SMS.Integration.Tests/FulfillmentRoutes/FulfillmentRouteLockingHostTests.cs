using System.Data;
using System.Net;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using SMS.Integration.Tests.SalesPreOrder;
using SMS.Integration.Tests.SapAlignment;
using SMS.Shared.Common;
using Xunit;
using static SMS.Integration.Tests.FulfillmentRoutes.Routes;

namespace SMS.Integration.Tests.FulfillmentRoutes;

/// <summary>
/// A33 FLOW host tests (real Program.cs host on LocalDB) for what only SQL Server can show:
/// <list type="bullet">
/// <item><b>REV-02:</b> the delivery creator takes the sale order's own application lock
/// (<see cref="SaleOrderLocks.HoldsResource"/>) on the Logistics connection before it re-reads the order. A cancel
/// holding that lock (here: held from a raw connection, as Demand's cancel holds it) makes the creator wait, and once
/// the cancel commits the creator sees CANCELLED and creates nothing — no orphan DRAFT delivery on a cancelled order.</item>
/// <item><b>REV-04:</b> on SQL Server, the auto LOOSE unit of an auto-staged PICK_AND_SHIP delivery can still be
/// weighed through the API, and is frozen once the goods are issued.</item>
/// </list>
/// <para>Run alone: <c>dotnet test &lt;out&gt;\SMS.Integration.Tests.dll --filter FullyQualifiedName~FulfillmentRouteLockingHostTests</c>.</para>
/// </summary>
public sealed class FulfillmentRouteLockingHostTests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _k;

    public FulfillmentRouteLockingHostTests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _k = new SapKit(factory, "FLW");
    }

    private string ConnectionString =>
        $"Server=(localdb)\\mssqllocaldb;Database={_f.DatabaseName};Trusted_Connection=True;MultipleActiveResultSets=true";

    private async Task<(Guid So, Product Product)> ConfirmedShipOrderAsync(decimal qty, bool autoCreate)
    {
        var pkr = await _k.PkrBaseAsync();
        var gst = await _k.EnsureTaxCodeAsync("GST17", 17m, "SALES", isDefault: true);
        await _k.EnsureTaxCodeAsync("PGST17", 17m, "PURCHASE", isDefault: true);
        await _k.CreateApproverPlaceholdersAsync();
        var wh       = await _k.CreateWarehouseAsync();
        var vendor   = await _k.CreateVendorAsync("Flow Vendor");
        var customer = await _k.CreateCustomerAsync("Flow Customer");
        var address  = await _k.ShippingAddressAsync(customer);
        var product  = await _k.CreateProductAsync("Drums", purchasePrice: 100m, sellingPrice: 150m);
        await _k.StockUpAsync(vendor, wh, (product, qty * 2, 100m));

        await _k.SetSaleOrderConfigAsync(("autoCreateDeliveriesOnConfirm", autoCreate));
        try
        {
            // No line override and no variant route: the org's default for SHIP orders, PICK_AND_SHIP (D-6, L-1).
            var so = await _k.CreateOrderAsync(customer, pkr, "SHIP", address, RLine(product, qty, taxCode: gst));
            await _k.ConfirmAsync(so);
            return (so, product);
        }
        finally
        {
            if (!autoCreate) await _k.SetSaleOrderConfigAsync(("autoCreateDeliveriesOnConfirm", true));
        }
    }

    [Fact]
    public async Task REV_02_the_creator_waits_for_a_cancel_holding_the_orders_lock_and_then_creates_nothing()
    {
        var (so, _) = await ConfirmedShipOrderAsync(10m, autoCreate: false);
        (await _k.SoDeliveriesAsync(so)).Should().BeEmpty("auto-create was off at confirm");

        // A cancel in progress, as Demand runs it: its transaction holds the order's lock.
        await using var cancel = new SqlConnection(ConnectionString);
        await cancel.OpenAsync();
        await using var tx = (SqlTransaction)await cancel.BeginTransactionAsync();
        await using (var take = new SqlCommand(
            "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @res, @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 5000; SELECT @r;",
            cancel, tx))
        {
            take.Parameters.Add(new SqlParameter("@res", SqlDbType.NVarChar, 255) { Value = SaleOrderLocks.HoldsResource(so) });
            Convert.ToInt32(await take.ExecuteScalarAsync()).Should().BeGreaterOrEqualTo(0, "the test holds the order's lock");
        }

        // The recovery button pressed meanwhile (the D-12 sweep would do the same).
        var recovery = _k.Post($"/api/sale-orders/{so}/create-deliveries");
        await Task.WhenAny(recovery, Task.Delay(TimeSpan.FromSeconds(3)));
        recovery.IsCompleted.Should().BeFalse("the creator must wait for the order's lock, not read around it");

        // The cancel commits.
        await using (var cancelOrder = new SqlCommand(
            "UPDATE demand.sale_orders SET Status = 'CANCELLED' WHERE UUID = @so", cancel, tx))
        {
            cancelOrder.Parameters.AddWithValue("@so", so);
            (await cancelOrder.ExecuteNonQueryAsync()).Should().Be(1);
        }
        await tx.CommitAsync();

        (await recovery).ShouldBe(HttpStatusCode.BadRequest, "under the lock the creator re-read a CANCELLED order");
        (await _k.SoDeliveriesAsync(so)).Should().BeEmpty("no DRAFT delivery is left behind on a cancelled order");
    }

    [Fact]
    public async Task REV_04_the_auto_loose_unit_of_an_auto_staged_delivery_can_be_weighed_until_goods_issue()
    {
        var (so, _) = await ConfirmedShipOrderAsync(4m, autoCreate: true);
        var d = await _k.SoDeliveryOnRouteAsync(so, PickAndShip);

        await _k.ReleaseAsync(d);
        await _k.PickAllAsync(d);
        (await _k.DeliveryStatusAsync(d)).Should().Be("STAGED", "D-3: no PACK → auto LOOSE unit, no STAGE → auto-staged");
        var unit = (await _k.PackagesAsync(d)).Single();
        unit.S("packageType").Should().Be("LOOSE");

        await _k.Ok(_k.Patch($"/api/logistics/packages/{unit.G("uuid")}", new { GrossWeightKg = 48m }), "weigh the auto unit");
        (await _k.PackagesAsync(d)).Single().D("grossWeightKg").Should().Be(48m);

        await _k.Ok(_k.GoodsIssue(d), "goods issue");
        (await _k.Patch($"/api/logistics/packages/{unit.G("uuid")}", new { GrossWeightKg = 50m }))
            .ShouldBe(HttpStatusCode.Conflict, "after goods issue the carton is off the books");
    }
}
