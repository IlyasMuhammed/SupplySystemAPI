using System.Reflection;
using FluentAssertions;
using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Controllers;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Services;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>
/// A32 PE-02..PE-06, PE-10, PE-11 (T-C4-01..10) — manual reserve / release on sale order lines against the real
/// Inventory <see cref="StockReservationService"/>, so the computed ReservedQty, the ledger and the line fields are
/// proven to agree. ReservedQty is never stored: it is read back from the ledger every time.
/// </summary>
public class SaleOrderReservationServiceTests
{
    private const int User = 9;

    internal sealed class Harness
    {
        public required DemandDbContext Demand { get; init; }
        public required InventoryDbContext Inventory { get; init; }
        public required StaticTenantContext Tenant { get; init; }
        public required StockReservationService Stock { get; init; }
        public required SaleOrderReservationService Reservations { get; init; }
        public required SaleOrderService Orders { get; init; }
        public required Mock<ISaleOrderDeliveryQuantities> Deliveries { get; init; }
        public Guid WarehouseUuid { get; set; }
        public int WarehouseId { get; set; }
    }

    internal static Harness NewHarness(int ttlHours = 72, bool superAdmin = false, Guid? orgId = null, string? demandDb = null, string? inventoryDb = null)
    {
        var tenant = new StaticTenantContext { OrganizationId = orgId ?? Guid.NewGuid(), IsSuperAdmin = superAdmin };
        var demand = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(demandDb ?? Guid.NewGuid().ToString()).Options, tenant);
        var inventory = new InventoryDbContext(
            new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(inventoryDb ?? Guid.NewGuid().ToString()).Options, tenant);
        var stock = new StockReservationService(inventory);

        var config = new Mock<ISaleOrderConfigService>();
        config.Setup(c => c.GetConfigAsync()).ReturnsAsync(new SaleOrderConfigModel { ReservationTtlHours = ttlHours });

        var deliveries = new Mock<ISaleOrderDeliveryQuantities>();
        deliveries.Setup(d => d.GetInFlightBySoLineAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, decimal>());

        var reservations = new SaleOrderReservationService(
            demand, tenant, stock, config.Object, Mock.Of<IBackgroundJobClient>(), deliveries.Object);

        var orders = new SaleOrderService(
            demand, tenant, Mock.Of<IOrganizationCurrencyService>(), Mock.Of<IDocumentNumberGenerator>(),
            Mock.Of<IPricingService>(), stock, Mock.Of<ITimelineService>(), Mock.Of<IBackgroundJobClient>(),
            Mock.Of<IAvailabilityCheckService>(), Mock.Of<IPurchaseOrderService>(), Mock.Of<ISaleOrderEmailService>(),
            deliveries: deliveries.Object);

        return new Harness
        {
            Demand = demand, Inventory = inventory, Tenant = tenant, Stock = stock,
            Reservations = reservations, Orders = orders, Deliveries = deliveries
        };
    }

    /// <summary>A variant with <paramref name="onHand"/> free in one warehouse; returns the variant's uuid.</summary>
    internal static async Task<Guid> SeedStockAsync(Harness h, decimal onHand)
    {
        var category = new ProductCategory { Name = "Cable", Code = $"C{Guid.NewGuid():N}"[..8], IsActive = true };
        h.Inventory.ProductCategories.Add(category);
        await h.Inventory.SaveChangesAsync();
        var product = new Product
        {
            Uuid = Guid.NewGuid(), Name = "4mm cable", Sku = $"SKU{Guid.NewGuid():N}"[..12],
            CategoryId = category.Id, IsActive = true, Status = "ACTIVE"
        };
        h.Inventory.Products.Add(product);
        await h.Inventory.SaveChangesAsync();
        var variant = new ProductVariant
        {
            Uuid = Guid.NewGuid(), ProductId = product.Id, Sku = $"V{Guid.NewGuid():N}"[..12],
            VariantName = "Default", IsDefault = true, IsActive = true
        };
        h.Inventory.ProductVariants.Add(variant);
        if (h.WarehouseId == 0)
        {
            var warehouse = new Warehouse { Uuid = Guid.NewGuid(), Name = "Central", Code = $"W{Guid.NewGuid():N}"[..6], IsActive = true };
            h.Inventory.Warehouses.Add(warehouse);
            await h.Inventory.SaveChangesAsync();
            h.WarehouseId = warehouse.Id;
            h.WarehouseUuid = warehouse.Uuid;
        }
        await h.Inventory.SaveChangesAsync();
        h.Inventory.InventoryItems.Add(new InventoryItem
        {
            Uuid = Guid.NewGuid(), VariantId = variant.Id, WarehouseId = h.WarehouseId, QtyOnHand = onHand, QtyReserved = 0m
        });
        await h.Inventory.SaveChangesAsync();
        h.Inventory.ChangeTracker.Clear();
        return variant.Uuid;
    }

    internal static async Task<SaleOrder> SeedOrderAsync(
        Harness h, string status, params (Guid Variant, decimal Qty, string LineStatus, decimal Fulfilled, string? Mode)[] lines)
    {
        var order = new SaleOrder
        {
            SoNumber = $"SO-{Guid.NewGuid():N}"[..12], PartnerId = Guid.NewGuid(), OrderDate = DateTime.UtcNow.Date,
            CurrencyId = Guid.NewGuid(), Status = status, DeliveryMode = "SELF_PICKUP", CreatedBy = User
        };
        foreach (var (variant, qty, lineStatus, fulfilled, mode) in lines)
            order.Lines.Add(new SaleOrderLine
            {
                VariantUuid = variant, Quantity = qty, UnitPrice = 10m, LineTotal = qty * 10m, Status = lineStatus,
                FulfilledQty = fulfilled, FulfillmentMode = mode, DeficitQty = qty - fulfilled
            });
        h.Demand.SaleOrders.Add(order);
        await h.Demand.SaveChangesAsync();
        return order;
    }

    private static (Guid, decimal, string, decimal, string?) Open(Guid variant, decimal qty) => (variant, qty, "OPEN", 0m, null);

    private async Task<SaleOrderLineModel> LineAsync(Harness h, Guid orderUuid, Guid lineUuid)
    {
        h.Demand.ChangeTracker.Clear();
        return (await h.Orders.GetByIdAsync(orderUuid))!.Lines.Single(l => l.Uuid == lineUuid);
    }

    // ── PE-02 read model ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_confirmed_line_with_nothing_held_or_shipped_reads_zero_reserved_and_red()
    {
        var h = NewHarness();
        var variant = await SeedStockAsync(h, 100m);
        var order = await SeedOrderAsync(h, "CONFIRMED", Open(variant, 10m));

        var line = await LineAsync(h, order.UUID, order.Lines.Single().UUID);

        line.ReservedQty.Should().Be(0m);
        line.ReservableQty.Should().Be(10m);
        line.DeliveryIndicator.Should().Be("RED");
    }

    [Theory]
    [InlineData("CONFIRMED", "OPEN", 10, 0, 0, 0, "RED")]
    [InlineData("CONFIRMED", "RESERVED", 10, 0, 10, 0, "BLUE")]
    [InlineData("CONFIRMED", "RESERVED", 10, 0, 4, 0, "YELLOW")]
    [InlineData("CONFIRMED", "OPEN", 10, 0, 0, 10, "BLUE")]      // all of it on a delivery not yet issued
    [InlineData("PARTIALLY_FULFILLED", "PARTIALLY_FULFILLED", 600, 300, 0, 0, "YELLOW")] // T-C4-08
    [InlineData("FULFILLED", "FULFILLED", 10, 10, 0, 0, "GREEN")]                      // T-C4-09
    [InlineData("CONFIRMED", "OPEN", 10, 10, 0, 0, "GREEN")]
    [InlineData("INVOICED", "INVOICED", 10, 10, 0, 0, "GREEN")]
    [InlineData("CONFIRMED", "CANCELLED", 10, 0, 0, 0, "GREY")]
    [InlineData("CANCELLED", "RESERVED", 10, 0, 10, 0, "GREY")]
    public void The_delivery_indicator_follows_section_6_3(
        string orderStatus, string lineStatus, decimal qty, decimal fulfilled, decimal reserved, decimal inFlight, string expected)
    {
        SaleOrderHolds.Indicator(orderStatus, lineStatus, qty, fulfilled, reserved + inFlight).Should().Be(expected);
    }

    [Fact]
    public async Task What_the_orders_deliveries_already_hold_is_not_reservable_again()
    {
        var h = NewHarness();
        var variant = await SeedStockAsync(h, 100m);
        var order = await SeedOrderAsync(h, "CONFIRMED", Open(variant, 10m));
        var lineUuid = order.Lines.Single().UUID;
        h.Deliveries.Setup(d => d.GetInFlightBySoLineAsync(order.UUID, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<Guid, decimal> { [lineUuid] = 6m });

        var line = await LineAsync(h, order.UUID, lineUuid);
        var act = () => h.Reservations.ReserveLineAsync(order.UUID, lineUuid, new ReserveSaleOrderLineRequest { Quantity = 5m }, User);

        line.ReservableQty.Should().Be(4m);
        await act.Should().ThrowAsync<BadRequestException>();
    }

    // ── PE-03 reserve ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Reserving_the_whole_line_holds_it_in_the_ledger_with_the_ttl_and_turns_it_blue()
    {
        var h = NewHarness(ttlHours: 48);
        var variant = await SeedStockAsync(h, 100m);
        var order = await SeedOrderAsync(h, "CONFIRMED", Open(variant, 10m));
        var lineUuid = order.Lines.Single().UUID;

        var result = await h.Reservations.ReserveLineAsync(order.UUID, lineUuid, new ReserveSaleOrderLineRequest(), User);

        result!.Outcome.Should().Be("RESERVED");
        result.ChangedQty.Should().Be(10m);
        result.ReservedQty.Should().Be(10m);
        result.ReservableQty.Should().Be(0m);
        result.DeliveryIndicator.Should().Be("BLUE");
        result.LineStatus.Should().Be("RESERVED");

        var hold = (await h.Stock.GetBySourceAsync(ReservationSourceType.SalesOrder, order.UUID)).Single();
        hold.SourceLineUuid.Should().Be(lineUuid);
        hold.ReservedQty.Should().Be(10m);
        hold.Status.Should().Be("ACTIVE");
        var row = await h.Inventory.StockReservations.AsNoTracking().SingleAsync();
        row.ExpiresAt.Should().BeCloseTo(DateTime.UtcNow.AddHours(48), TimeSpan.FromMinutes(1));

        h.Demand.ChangeTracker.Clear();
        var stored = await h.Demand.SaleOrderLines.AsNoTracking().SingleAsync(l => l.UUID == lineUuid);
        stored.Status.Should().Be("RESERVED");
        stored.DeficitQty.Should().Be(0m, "the GRN link reserves up to the deficit — it must not hold this again");
        (await LineAsync(h, order.UUID, lineUuid)).ReservedQty.Should().Be(10m);
    }

    [Fact]
    public async Task Short_stock_without_allow_partial_holds_nothing_and_asks_for_confirmation()
    {
        var h = NewHarness();
        var variant = await SeedStockAsync(h, 6m);
        var order = await SeedOrderAsync(h, "CONFIRMED", Open(variant, 10m));
        var lineUuid = order.Lines.Single().UUID;

        var result = await h.Reservations.ReserveLineAsync(order.UUID, lineUuid, new ReserveSaleOrderLineRequest(), User);

        result!.Outcome.Should().Be("NEEDS_CONFIRMATION");
        result.RequestedQty.Should().Be(10m);
        result.AvailableQty.Should().Be(6m);
        result.ChangedQty.Should().Be(0m);
        (await h.Stock.GetBySourceAsync(ReservationSourceType.SalesOrder, order.UUID)).Should().BeEmpty();
    }

    [Fact]
    public async Task Short_stock_with_allow_partial_holds_what_is_free_and_turns_yellow()
    {
        var h = NewHarness();
        var variant = await SeedStockAsync(h, 6m);
        var order = await SeedOrderAsync(h, "CONFIRMED", Open(variant, 10m));
        var lineUuid = order.Lines.Single().UUID;

        var result = await h.Reservations.ReserveLineAsync(order.UUID, lineUuid, new ReserveSaleOrderLineRequest { AllowPartial = true }, User);

        result!.Outcome.Should().Be("PARTIAL");
        result.ChangedQty.Should().Be(6m);
        result.ReservedQty.Should().Be(6m);
        result.ReservableQty.Should().Be(4m);
        result.DeliveryIndicator.Should().Be("YELLOW");
        h.Demand.ChangeTracker.Clear();
        (await h.Demand.SaleOrderLines.AsNoTracking().SingleAsync(l => l.UUID == lineUuid)).DeficitQty.Should().Be(4m);
    }

    [Fact]
    public async Task Nothing_free_holds_nothing()
    {
        var h = NewHarness();
        var variant = await SeedStockAsync(h, 0m);
        var order = await SeedOrderAsync(h, "CONFIRMED", Open(variant, 10m));

        var result = await h.Reservations.ReserveLineAsync(order.UUID, order.Lines.Single().UUID, new ReserveSaleOrderLineRequest { AllowPartial = true }, User);

        result!.Outcome.Should().Be("NONE_AVAILABLE");
        result.ChangedQty.Should().Be(0m);
    }

    [Fact]
    public async Task A_second_reserve_adds_to_what_the_line_already_holds()
    {
        var h = NewHarness();
        var variant = await SeedStockAsync(h, 100m);
        var order = await SeedOrderAsync(h, "CONFIRMED", Open(variant, 10m));
        var lineUuid = order.Lines.Single().UUID;

        await h.Reservations.ReserveLineAsync(order.UUID, lineUuid, new ReserveSaleOrderLineRequest { Quantity = 3m }, User);
        var second = await h.Reservations.ReserveLineAsync(order.UUID, lineUuid, new ReserveSaleOrderLineRequest(), User);

        second!.RequestedQty.Should().Be(7m, "omitting the quantity means the rest of the line");
        second.ReservedQty.Should().Be(10m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(11)]
    public async Task A_quantity_that_is_not_positive_or_more_than_the_line_still_needs_is_refused(decimal qty)
    {
        var h = NewHarness();
        var variant = await SeedStockAsync(h, 100m);
        var order = await SeedOrderAsync(h, "CONFIRMED", Open(variant, 10m));

        var act = () => h.Reservations.ReserveLineAsync(order.UUID, order.Lines.Single().UUID, new ReserveSaleOrderLineRequest { Quantity = qty }, User);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task A_line_already_fully_held_has_nothing_left_to_reserve()
    {
        var h = NewHarness();
        var variant = await SeedStockAsync(h, 100m);
        var order = await SeedOrderAsync(h, "CONFIRMED", Open(variant, 10m));
        var lineUuid = order.Lines.Single().UUID;
        await h.Reservations.ReserveLineAsync(order.UUID, lineUuid, new ReserveSaleOrderLineRequest(), User);

        var act = () => h.Reservations.ReserveLineAsync(order.UUID, lineUuid, new ReserveSaleOrderLineRequest(), User);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Theory]
    [InlineData("DRAFT", "OPEN", null)]
    [InlineData("CANCELLED", "OPEN", null)]
    [InlineData("FULFILLED", "FULFILLED", null)]
    [InlineData("CONFIRMED", "CANCELLED", null)]
    [InlineData("CONFIRMED", "FULFILLED", null)]
    [InlineData("CONFIRMED", "OPEN", "DROP_SHIP")]
    public async Task Orders_and_lines_that_cannot_hold_stock_are_refused(string orderStatus, string lineStatus, string? mode)
    {
        var h = NewHarness();
        var variant = await SeedStockAsync(h, 100m);
        var order = await SeedOrderAsync(h, orderStatus, (variant, 10m, lineStatus, 0m, mode));

        var act = () => h.Reservations.ReserveLineAsync(order.UUID, order.Lines.Single().UUID, new ReserveSaleOrderLineRequest(), User);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task A_partially_fulfilled_order_can_still_hold_the_rest_of_a_line()
    {
        var h = NewHarness();
        var variant = await SeedStockAsync(h, 100m);
        var order = await SeedOrderAsync(h, "PARTIALLY_FULFILLED", (variant, 600m, "PARTIALLY_FULFILLED", 300m, "IN_STOCK"));

        var result = await h.Reservations.ReserveLineAsync(order.UUID, order.Lines.Single().UUID, new ReserveSaleOrderLineRequest { AllowPartial = true }, User);

        result!.RequestedQty.Should().Be(300m);
        result.ChangedQty.Should().Be(100m);
        result.DeliveryIndicator.Should().Be("YELLOW");
        result.LineStatus.Should().Be("PARTIALLY_FULFILLED", "a fulfilment status is never walked back to RESERVED");
    }

    [Fact]
    public async Task Another_organizations_order_is_not_found_even_for_a_super_admin()
    {
        var demandDb = Guid.NewGuid().ToString();
        var inventoryDb = Guid.NewGuid().ToString();
        var theirs = NewHarness(demandDb: demandDb, inventoryDb: inventoryDb);
        var variant = await SeedStockAsync(theirs, 100m);
        var order = await SeedOrderAsync(theirs, "CONFIRMED", Open(variant, 10m));
        var admin = NewHarness(superAdmin: true, demandDb: demandDb, inventoryDb: inventoryDb);
        var lineUuid = order.Lines.Single().UUID;

        (await admin.Reservations.ReserveLineAsync(order.UUID, lineUuid, new ReserveSaleOrderLineRequest(), User)).Should().BeNull();
        (await admin.Reservations.ReleaseLineAsync(order.UUID, lineUuid, new ReleaseSaleOrderLineRequest(), User)).Should().BeNull();
        (await admin.Reservations.ReserveAllAsync(order.UUID, new ReserveAllSaleOrderLinesRequest(), User)).Should().BeNull();
    }

    [Fact]
    public async Task A_line_of_another_order_is_not_found()
    {
        var h = NewHarness();
        var variant = await SeedStockAsync(h, 100m);
        var first = await SeedOrderAsync(h, "CONFIRMED", Open(variant, 10m));
        var second = await SeedOrderAsync(h, "CONFIRMED", Open(variant, 10m));

        (await h.Reservations.ReserveLineAsync(first.UUID, second.Lines.Single().UUID, new ReserveSaleOrderLineRequest(), User)).Should().BeNull();
    }

    // ── PE-03 release ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Releasing_frees_everything_the_order_holds_for_the_line_and_turns_it_red()
    {
        var h = NewHarness();
        var variant = await SeedStockAsync(h, 100m);
        var order = await SeedOrderAsync(h, "CONFIRMED", Open(variant, 10m));
        var lineUuid = order.Lines.Single().UUID;
        await h.Reservations.ReserveLineAsync(order.UUID, lineUuid, new ReserveSaleOrderLineRequest { Quantity = 4m }, User);
        await h.Reservations.ReserveLineAsync(order.UUID, lineUuid, new ReserveSaleOrderLineRequest { Quantity = 6m }, User);

        var result = await h.Reservations.ReleaseLineAsync(order.UUID, lineUuid, new ReleaseSaleOrderLineRequest { Reason = "Customer wants it later" }, User);

        result!.Outcome.Should().Be("RELEASED");
        result.ChangedQty.Should().Be(10m);
        result.ReservedQty.Should().Be(0m);
        result.DeliveryIndicator.Should().Be("RED");
        result.LineStatus.Should().Be("OPEN");
        (await h.Stock.GetBySourceAsync(ReservationSourceType.SalesOrder, order.UUID)).Should().OnlyContain(r => r.Status != "ACTIVE");
        (await h.Stock.GetAvailableAsync([variant], null)).Single().Available.Should().Be(100m, "the stock is back in the free pool");
        h.Demand.ChangeTracker.Clear();
        (await h.Demand.SaleOrderLines.AsNoTracking().SingleAsync(l => l.UUID == lineUuid)).DeficitQty.Should().Be(10m);
    }

    [Fact]
    public async Task Releasing_part_leaves_the_rest_held()
    {
        var h = NewHarness();
        var variant = await SeedStockAsync(h, 100m);
        var order = await SeedOrderAsync(h, "CONFIRMED", Open(variant, 10m));
        var lineUuid = order.Lines.Single().UUID;
        await h.Reservations.ReserveLineAsync(order.UUID, lineUuid, new ReserveSaleOrderLineRequest(), User);

        var result = await h.Reservations.ReleaseLineAsync(order.UUID, lineUuid, new ReleaseSaleOrderLineRequest { Quantity = 3m }, User);

        result!.ChangedQty.Should().Be(3m);
        result.ReservedQty.Should().Be(7m);
        result.DeliveryIndicator.Should().Be("YELLOW");
        result.LineStatus.Should().Be("RESERVED");
    }

    [Fact]
    public async Task Releasing_only_touches_its_own_line()
    {
        var h = NewHarness();
        var variant = await SeedStockAsync(h, 100m);
        var order = await SeedOrderAsync(h, "CONFIRMED", Open(variant, 10m), Open(variant, 5m));
        var (a, b) = (order.Lines.First().UUID, order.Lines.Last().UUID);
        await h.Reservations.ReserveAllAsync(order.UUID, new ReserveAllSaleOrderLinesRequest(), User);

        await h.Reservations.ReleaseLineAsync(order.UUID, a, new ReleaseSaleOrderLineRequest(), User);

        (await LineAsync(h, order.UUID, a)).ReservedQty.Should().Be(0m);
        (await LineAsync(h, order.UUID, b)).ReservedQty.Should().Be(5m);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task Releasing_a_line_that_holds_nothing_or_a_non_positive_quantity_is_refused(int? qty)
    {
        var h = NewHarness();
        var variant = await SeedStockAsync(h, 100m);
        var order = await SeedOrderAsync(h, "CONFIRMED", Open(variant, 10m));
        var lineUuid = order.Lines.Single().UUID;
        if (qty is not null) await h.Reservations.ReserveLineAsync(order.UUID, lineUuid, new ReserveSaleOrderLineRequest(), User);

        var act = () => h.Reservations.ReleaseLineAsync(order.UUID, lineUuid, new ReleaseSaleOrderLineRequest { Quantity = qty }, User);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    // ── PE-06 reserve-all (T-C4-10) ──────────────────────────────────────────

    [Fact]
    public async Task Reserve_all_holds_every_eligible_line_and_skips_the_rest()
    {
        var h = NewHarness();
        var plenty = await SeedStockAsync(h, 100m);
        var scarce = await SeedStockAsync(h, 3m);
        var order = await SeedOrderAsync(h, "CONFIRMED",
            Open(plenty, 10m), Open(scarce, 5m), (plenty, 2m, "OPEN", 0m, "DROP_SHIP"), (plenty, 1m, "CANCELLED", 0m, null));

        var result = await h.Reservations.ReserveAllAsync(order.UUID, new ReserveAllSaleOrderLinesRequest(), User);

        result!.Lines.Should().HaveCount(4);
        result.Lines.Select(l => l.Outcome).Should().Equal("RESERVED", "PARTIAL", "SKIPPED", "SKIPPED");
        result.ReservedLineCount.Should().Be(1);
        result.PartialLineCount.Should().Be(1);
        result.UnchangedLineCount.Should().Be(2);
        result.Lines.Where(l => l.Outcome == "SKIPPED").Should().OnlyContain(l => !string.IsNullOrEmpty(l.Message));
    }

    [Fact]
    public async Task Reserve_all_without_partial_leaves_a_short_line_unheld()
    {
        var h = NewHarness();
        var scarce = await SeedStockAsync(h, 3m);
        var order = await SeedOrderAsync(h, "CONFIRMED", Open(scarce, 5m));

        var result = await h.Reservations.ReserveAllAsync(order.UUID, new ReserveAllSaleOrderLinesRequest { AllowPartial = false }, User);

        result!.Lines.Single().Outcome.Should().Be("NEEDS_CONFIRMATION");
        (await h.Stock.GetBySourceAsync(ReservationSourceType.SalesOrder, order.UUID)).Should().BeEmpty();
    }

    [Fact]
    public async Task Reserve_all_on_an_order_that_cannot_hold_stock_is_refused()
    {
        var h = NewHarness();
        var variant = await SeedStockAsync(h, 100m);
        var order = await SeedOrderAsync(h, "DRAFT", Open(variant, 10m));

        var act = () => h.Reservations.ReserveAllAsync(order.UUID, new ReserveAllSaleOrderLinesRequest(), User);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    // ── PE-04 cancel (T-C4-06) ────────────────────────────────────────────────

    [Fact]
    public async Task Cancelling_the_order_releases_every_hold_and_closes_its_unshipped_lines()
    {
        var h = NewHarness();
        var variant = await SeedStockAsync(h, 100m);
        var order = await SeedOrderAsync(h, "CONFIRMED", Open(variant, 10m), Open(variant, 5m));
        await h.Reservations.ReserveAllAsync(order.UUID, new ReserveAllSaleOrderLinesRequest(), User);
        h.Demand.ChangeTracker.Clear();

        (await h.Orders.CancelAsync(order.UUID, User, "Customer withdrew")).Should().BeTrue();

        (await h.Stock.GetBySourceAsync(ReservationSourceType.SalesOrder, order.UUID)).Should().OnlyContain(r => r.Status != "ACTIVE");
        (await h.Stock.GetAvailableAsync([variant], null)).Single().Available.Should().Be(100m);
        h.Demand.ChangeTracker.Clear();
        var model = (await h.Orders.GetByIdAsync(order.UUID))!;
        model.Status.Should().Be("CANCELLED");
        model.Lines.Should().OnlyContain(l => l.ReservedQty == 0m && l.DeliveryIndicator == "GREY" && l.Status == "CANCELLED");
    }

    // ── PE-10 expiry (T-C4-07) ────────────────────────────────────────────────

    [Fact]
    public async Task An_expired_manual_hold_is_released_by_the_sweep_and_the_line_reads_what_is_left()
    {
        var h = NewHarness();
        var variant = await SeedStockAsync(h, 100m);
        var order = await SeedOrderAsync(h, "CONFIRMED", Open(variant, 10m));
        var lineUuid = order.Lines.Single().UUID;
        await h.Reservations.ReserveLineAsync(order.UUID, lineUuid, new ReserveSaleOrderLineRequest { Quantity = 4m }, User);
        await h.Reservations.ReserveLineAsync(order.UUID, lineUuid, new ReserveSaleOrderLineRequest { Quantity = 6m }, User);
        // Only the first hold has run out.
        var first = await h.Inventory.StockReservations.OrderBy(r => r.Id).FirstAsync();
        first.ExpiresAt = DateTime.UtcNow.AddHours(-1);
        await h.Inventory.SaveChangesAsync();
        h.Inventory.ChangeTracker.Clear();
        h.Demand.ChangeTracker.Clear();
        var sweep = new ReservationExpirySweepJob(h.Demand, h.Stock, Mock.Of<INotificationService>(), NullLogger<ReservationExpirySweepJob>.Instance);

        await sweep.RunAsync();

        var line = await LineAsync(h, order.UUID, lineUuid);
        line.ReservedQty.Should().Be(6m);
        line.DeliveryIndicator.Should().Be("YELLOW");
        line.Status.Should().Be("RESERVED", "the line still holds 6 — only the expired 4 went back");
        h.Demand.ChangeTracker.Clear();
        (await h.Demand.SaleOrderLines.AsNoTracking().SingleAsync(l => l.UUID == lineUuid)).DeficitQty.Should().Be(4m);
    }

    // ── PE-05 gating (server = frontend guard) ────────────────────────────────

    [Theory]
    [InlineData(nameof(SaleOrdersController.ReserveLine), "SALE_ORDER_RESERVE")]
    [InlineData(nameof(SaleOrdersController.ReserveAll), "SALE_ORDER_RESERVE")]
    [InlineData(nameof(SaleOrdersController.ReleaseLine), "SALE_ORDER_RELEASE_RESERVATION")]
    [InlineData(nameof(SaleOrdersController.UpdateCustomerPo), "SALE_ORDER_EDIT")]
    [InlineData(nameof(SaleOrdersController.CheckCustomerPo), "SALE_ORDER_VIEW SALE_ORDER_CREATE SALE_ORDER_EDIT")]
    public void The_new_sale_order_actions_ask_for_exactly_the_contracts_permissions(string action, string codes)
    {
        var attribute = typeof(SaleOrdersController).GetMethod(action)!.GetCustomAttribute<RequirePermissionAttribute>();

        attribute.Should().NotBeNull();
        attribute!.AnyOf.Should().BeEquivalentTo(codes.Split(' '));
    }
}
