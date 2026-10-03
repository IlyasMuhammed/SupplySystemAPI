using System.Net;
using FluentAssertions;
using SMS.Integration.Tests.SapAlignment;
using Xunit;

namespace SMS.Integration.Tests.SalesPreOrder;

/// <summary>
/// Two confirmations of the same DRAFT sale order at the same moment. Confirming reserves the order's stock
/// (AvailabilityCheckService) and the ledger is Inventory's, committing on its own — so without the order's lock both
/// requests passed the DRAFT check and both held the stock. Confirm now runs under the same per-order lock as
/// reserve, release and cancel: the second waits, reads CONFIRMED and is refused.
/// </summary>
public sealed class SaleOrderConfirmRaceE2ETests : IClassFixture<SapWebApplicationFactory>
{
    private readonly SapWebApplicationFactory _f;
    private readonly SapKit _k;

    public SaleOrderConfirmRaceE2ETests(SapWebApplicationFactory factory)
    {
        _f = factory;
        _k = new SapKit(factory, "CRACE");
    }

    private async Task<decimal> LedgerHeldAsync(Guid so)
    {
        var rows = await _f.QueryAsync(
            "SELECT ISNULL(SUM(ReservedQty), 0) AS Q FROM inventory.StockReservations " +
            "WHERE SourceType = 'SALES_ORDER' AND SourceUuid = @so AND Status = 'ACTIVE'", ("@so", so));
        return Convert.ToDecimal(rows[0]["Q"]);
    }

    [Fact]
    public async Task Two_simultaneous_confirms_reserve_the_order_once()
    {
        var pkr = await _k.PkrBaseAsync();
        await _k.EnsureTaxCodeAsync("PGST17", 17m, "PURCHASE", isDefault: true);
        await _k.CreateApproverPlaceholdersAsync();
        var wh       = await _k.CreateWarehouseAsync();
        var vendor   = await _k.CreateVendorAsync("Race Vendor");
        var customer = await _k.CreateCustomerAsync("Race Customer");

        for (var round = 1; round <= 3; round++)
        {
            var p = await _k.CreateProductAsync($"Race Widget {round}", purchasePrice: 30m, sellingPrice: 70m);
            await _k.StockUpAsync(vendor, wh, (p, 10m, 30m));
            var so = await _k.CreateSaleOrderAsync(customer, pkr, SapKit.Line(p, 4m));

            var results = await Task.WhenAll(
                _k.Post($"/api/sale-orders/{so}/confirm"),
                _k.Post($"/api/sale-orders/{so}/confirm"));

            results.Count(r => r.Status == HttpStatusCode.OK).Should().Be(1, $"round {round}: exactly one confirmation wins — {string.Join(" / ", results.Select(r => r.ToString()))}");
            results.Should().ContainSingle(r => r.Status != HttpStatusCode.OK)
                .Which.Status.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.Conflict);
            (await LedgerHeldAsync(so)).Should().Be(4m, $"round {round}: the order is held once, not twice");
            (await _k.GetSaleOrderAsync(so)).S("status").Should().Be("CONFIRMED");
        }
    }
}
