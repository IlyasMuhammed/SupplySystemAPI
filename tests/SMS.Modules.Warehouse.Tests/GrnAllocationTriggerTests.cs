using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Services;
using SMS.Modules.Warehouse.Data;
using SMS.Modules.Warehouse.Domain;
using SMS.Modules.Warehouse.Models;
using SMS.Modules.Warehouse.Repositories;
using SMS.Modules.Warehouse.Services;
using SMS.Shared.Common;
using Xunit;
using InventoryWarehouse = SMS.Modules.Inventory.Domain.Warehouse;

namespace SMS.Modules.Warehouse.Tests;

/// <summary>
/// A30 §15 (A30-P1-11) — a goods receipt hands the new stock to the allocation engine: the
/// expected supply it was registered as is booked received, and the engine runs for the variant
/// so a waiting demand gets the stock by the rules. Nothing about the posting itself changes.
/// </summary>
public class GrnAllocationTriggerTests
{
    private const int User = 1;

    private sealed record Setup(
        WarehouseDbContext Wh, DemandDbContext Demand, InventoryDbContext Inv,
        PurchaseOrder Po, Guid VariantUuid, Guid WarehouseUuid, AllocationEngine Engine);

    private static async Task<Setup> NewAsync()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = new StaticTenantContext();

        var demand = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(dbName).Options, tenant);
        var po = new PurchaseOrder
        {
            UUID = Guid.NewGuid(), PoNumber = "PO-2026-00042", Title = "Shirts", SupplierId = Guid.NewGuid(),
            SupplierName = "Textile Supplier Co", Status = "SENT", IsActive = true, CreatedBy = User, CreatedDate = DateTime.UtcNow
        };
        po.Lines.Add(new PurchaseOrderLine
        {
            UUID = Guid.NewGuid(), LineNo = 1, ItemDescription = "Plain T-Shirt", UnitOfMeasure = "PCS",
            Quantity = 10m, UnitPrice = 3m, LineTotal = 30m, QtyReceived = 0
        });
        po.TotalAmount = 30m;
        demand.PurchaseOrders.Add(po);
        await demand.SaveChangesAsync();

        var inv = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options, tenant);
        var product = new Product { Uuid = Guid.NewGuid(), Sku = "TSHIRT", Name = "Plain T-Shirt", Status = "ACTIVE", IsActive = true, CreatedBy = User };
        inv.Products.Add(product);
        var warehouse = new InventoryWarehouse { Uuid = Guid.NewGuid(), Code = "WH1", Name = "Main", IsActive = true, CreatedBy = User };
        inv.Warehouses.Add(warehouse);
        await inv.SaveChangesAsync();
        var variant = new ProductVariant
        {
            Uuid = Guid.NewGuid(), ProductId = product.Id, Sku = "TSHIRT-DEFAULT", VariantName = "Default",
            PurchasePrice = 3m, IsDefault = true, IsActive = true, CreatedBy = User
        };
        inv.ProductVariants.Add(variant);
        await inv.SaveChangesAsync();

        var wh = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseInMemoryDatabase(dbName).Options, tenant);

        return new Setup(wh, demand, inv, po, variant.Uuid, warehouse.Uuid, new AllocationEngine(inv, new StockReservationService(inv)));
    }

    private static async Task<Grn> PendingGrnAsync(Setup s, decimal qty)
    {
        var repo = new GrnRepository(s.Wh, s.Demand);
        var uuid = await repo.CreateAsync(new CreateGrnRequest { PoUuid = s.Po.UUID, WarehouseUuid = s.WarehouseUuid, ReceivedAt = DateTime.UtcNow }, User);

        var grn  = await s.Wh.Grns.Include(g => g.Lines).FirstAsync(g => g.UUID == uuid);
        var line = grn.Lines.First();
        (await s.Wh.GrnLines.FirstAsync(l => l.UUID == line.UUID)).VariantUuid = s.VariantUuid;
        await repo.UpdateLineAsync(uuid, line.UUID, new UpdateGrnLineRequest
        {
            QtyReceived = qty, QtyAccepted = qty, QtyRejected = 0, UnitCost = 3m
        }, User);

        grn = await s.Wh.Grns.Include(g => g.Lines).FirstAsync(g => g.UUID == uuid);
        grn.Status = "PENDING_APPROVAL";
        await s.Wh.SaveChangesAsync();
        return grn;
    }

    private static Task<DemandAllocationSummary> DemandAsync(Setup s, decimal qty) =>
        s.Engine.RegisterDemandAsync(new AllocationDemandRegistration(
            AllocationDemandType.ProductionMaterial, Guid.NewGuid(), null, "PROD-2026-00001",
            s.VariantUuid, s.WarehouseUuid, qty, new DateTime(2026, 9, 30)), User);

    private static async Task<InventoryItem> ItemAsync(Setup s)
    {
        var variant   = await s.Inv.ProductVariants.AsNoTracking().FirstAsync(v => v.Uuid == s.VariantUuid);
        var warehouse = await s.Inv.Warehouses.AsNoTracking().FirstAsync(w => w.Uuid == s.WarehouseUuid);
        return await s.Inv.InventoryItems.AsNoTracking().SingleAsync(i => i.VariantId == variant.Id && i.WarehouseId == warehouse.Id);
    }

    [Fact]
    public async Task A_receipt_runs_the_engine_so_a_waiting_demand_gets_the_stock()
    {
        var s = await NewAsync();
        var demand = await DemandAsync(s, 6);
        (await s.Engine.GetDemandAsync(demand.Uuid))!.Shortage.Should().Be(6, "nothing has been received yet");
        var grn = await PendingGrnAsync(s, 10m);

        var poster = new EfGrnInventoryPoster(s.Inv, Mock.Of<IInventoryLedgerService>(), allocation: s.Engine);
        await poster.PostToInventoryAsync(grn, approvedBy: User);

        var item = await ItemAsync(s);
        item.QtyOnHand.Should().Be(10m);
        item.QtyReserved.Should().Be(6m, "the engine ran after the stock was posted");
        var after = (await s.Engine.GetDemandAsync(demand.Uuid))!;
        after.ReservedQty.Should().Be(6);
        after.Shortage.Should().Be(0);
    }

    [Fact]
    public async Task A_receipt_is_booked_against_the_supply_registered_for_its_purchase_order_line()
    {
        var s = await NewAsync();
        await s.Engine.RegisterSupplyAsync(new AllocationSupplyRegistration(
            AllocationSupplyType.PurchaseOrder, s.Po.Lines.Single().UUID, null, s.Po.PoNumber,
            s.VariantUuid, s.WarehouseUuid, 10m, new DateTime(2026, 9, 26)));
        var demand = await DemandAsync(s, 6);
        await s.Engine.AllocateForDemandAsync(demand.Uuid, User);
        (await s.Engine.GetDemandAsync(demand.Uuid))!.PlannedQty.Should().Be(6, "planned against the purchase order before it arrives");
        var grn = await PendingGrnAsync(s, 10m);

        await new EfGrnInventoryPoster(s.Inv, Mock.Of<IInventoryLedgerService>(), allocation: s.Engine)
            .PostToInventoryAsync(grn, approvedBy: User);

        var after = (await s.Engine.GetDemandAsync(demand.Uuid))!;
        after.PlannedQty.Should().Be(0);
        after.ReservedQty.Should().Be(6);
        (await s.Inv.AllocationSupplies.AsNoTracking().SingleAsync()).Status.Should().Be(AllocationSupplyStatus.Received);
    }

    [Fact]
    public async Task An_allocation_failure_does_not_undo_the_stock_posting()
    {
        var s = await NewAsync();
        var grn = await PendingGrnAsync(s, 10m);
        var broken = new Mock<IAllocationEngine>();
        broken.Setup(e => e.SupplyReceivedAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new InvalidOperationException("allocation is down"));

        var act = async () => await new EfGrnInventoryPoster(s.Inv, Mock.Of<IInventoryLedgerService>(), allocation: broken.Object)
            .PostToInventoryAsync(grn, approvedBy: User);

        await act.Should().NotThrowAsync("failing the approval would post the stock twice on retry");
        (await ItemAsync(s)).QtyOnHand.Should().Be(10m);
    }

    [Fact]
    public async Task Without_an_engine_a_receipt_posts_stock_exactly_as_before()
    {
        var s = await NewAsync();
        var grn = await PendingGrnAsync(s, 10m);

        await new EfGrnInventoryPoster(s.Inv, Mock.Of<IInventoryLedgerService>()).PostToInventoryAsync(grn, approvedBy: User);

        (await ItemAsync(s)).QtyOnHand.Should().Be(10m);
    }
}
