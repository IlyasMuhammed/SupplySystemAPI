using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Services;
using InventoryWarehouse = SMS.Modules.Inventory.Domain.Warehouse;
using SMS.Modules.Warehouse.Data;
using SMS.Modules.Warehouse.Domain;
using SMS.Modules.Warehouse.Events;
using SMS.Modules.Warehouse.Models;
using SMS.Modules.Warehouse.Repositories;
using SMS.Modules.Warehouse.Services;
using Xunit;

namespace SMS.Modules.Warehouse.Tests;

// ── Stubs ─────────────────────────────────────────────────────────────────────

file sealed class NullInventoryLedgerService : IInventoryLedgerService
{
    public Task CreateEntryAsync(LedgerEntryCommand command, IDbContextTransaction? transaction = null)
        => Task.CompletedTask;
    public Task<decimal> GetCurrentBalanceAsync(int productId, int warehouseId)
        => Task.FromResult(0m);
    public Task<LedgerPagedResult> GetLedgerAsync(LedgerFilterDto filter)
        => Task.FromResult(new LedgerPagedResult());
}
// CapturingGrnEventPublisher is defined in SMS.Modules.Warehouse.Events (GrnEvents.cs) and exposed via InternalsVisibleTo

// ── Shared test infrastructure ────────────────────────────────────────────────

file static class ApprovalBuild
{
    internal static (
        GrnStatusHandler handler,
        WarehouseDbContext wh,
        DemandDbContext demand,
        InventoryDbContext inv,
        CapturingGrnEventPublisher publisher
    ) New(
        Action<DemandDbContext>?    seedDemand    = null,
        Action<InventoryDbContext>? seedInventory = null,
        IGrnStockPoster?            stockPoster   = null)
    {
        var dbName = Guid.NewGuid().ToString();

        var demandOpts = new DbContextOptionsBuilder<DemandDbContext>()
            .UseInMemoryDatabase(dbName).Options;
        var demand = new DemandDbContext(demandOpts);
        seedDemand?.Invoke(demand);
        demand.SaveChanges();

        var invOpts = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        var inv = new InventoryDbContext(invOpts);
        seedInventory?.Invoke(inv);
        inv.SaveChanges();

        var whOpts = new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase(dbName).Options;
        var wh = new WarehouseDbContext(whOpts);

        var publisher = new CapturingGrnEventPublisher();
        var poster    = stockPoster ?? new NullGrnStockPoster();
        var handler   = new GrnStatusHandler(wh, demand, poster, publisher);

        return (handler, wh, demand, inv, publisher);
    }

    internal static PurchaseOrder SentPo(params (string desc, decimal qty, decimal unitPrice)[] lines)
    {
        var po = new PurchaseOrder
        {
            UUID         = Guid.NewGuid(),
            PoNumber     = "PO-2025-00001",
            Title        = "Test PO",
            SupplierId   = Guid.NewGuid(),
            SupplierName = "Test Supplier",
            Status       = "SENT",
            IsActive     = true,
            CreatedBy    = 1,
            CreatedDate  = DateTime.UtcNow
        };

        int lineNo = 1;
        foreach (var (desc, qty, price) in lines)
        {
            po.Lines.Add(new PurchaseOrderLine
            {
                UUID            = Guid.NewGuid(),
                LineNo          = lineNo++,
                ItemDescription = desc,
                UnitOfMeasure   = "PC",
                Quantity        = qty,
                UnitPrice       = price,
                LineTotal       = qty * price,
                QtyReceived     = 0
            });
        }

        po.TotalAmount = po.Lines.Sum(l => l.LineTotal);
        return po;
    }

    // Inserts a PENDING_APPROVAL GRN (bypassing workflow engine for unit test setup)
    internal static async Task<Grn> PendingGrnAsync(
        WarehouseDbContext wh,
        DemandDbContext demand,
        PurchaseOrder po,
        decimal qtyAccepted,
        Guid? warehouseUuid = null,
        Guid? productUuid   = null)
    {
        var repo = new GrnRepository(wh, demand);
        var uuid = await repo.CreateAsync(new CreateGrnRequest
        {
            PoUuid = po.UUID,
            WarehouseUuid = Guid.NewGuid(),
            ReceivedAt = DateTime.UtcNow // Changed from ReceivedDate to ReceivedAt
        }, createdBy: 1);

        var grn  = await wh.Grns.Include(g => g.Lines).FirstAsync(g => g.UUID == uuid);
        var line = grn.Lines.First();

        if (productUuid.HasValue)
        {
            var grnLine = await wh.GrnLines.FirstAsync(l => l.UUID == line.UUID);
            grnLine.ProductUuid = productUuid.Value;
        }

        await repo.UpdateLineAsync(uuid, line.UUID, new UpdateGrnLineRequest
        {
            QtyReceived = qtyAccepted,
            QtyAccepted = qtyAccepted,
            QtyRejected = 0,
            UnitCost    = po.Lines.First().UnitPrice
        }, modifiedBy: 1);

        // Set PENDING_APPROVAL directly — workflow engine does this in production
        grn = await wh.Grns.Include(g => g.Lines).FirstAsync(g => g.UUID == uuid);
        grn.Status = "PENDING_APPROVAL";
        await wh.SaveChangesAsync();

        return grn;
    }
}

// ── WAR-002-TC-1: APPROVED → PO status PARTIALLY_RECEIVED ────────────────────

public class GrnStatusHandler_Approve_Partial_Tests
{
    [Fact]
    public async Task PO_Status_Is_PARTIALLY_RECEIVED_After_Partial_GRN_Approval()
    {
        var po = ApprovalBuild.SentPo(("Laptop", 10m, 100m), ("Mouse", 5m, 50m));
        var (handler, wh, demand, _, _) = ApprovalBuild.New(db => db.PurchaseOrders.Add(po));

        // Receive only partial qty on each line
        var repo = new GrnRepository(wh, demand);
        var uuid = await repo.CreateAsync(new CreateGrnRequest
        {
            PoUuid        = po.UUID,
            WarehouseUuid = Guid.NewGuid(),
            ReceivedDate  = DateTime.UtcNow
        }, createdBy: 1);

        var grn = await wh.Grns.Include(g => g.Lines).FirstAsync(g => g.UUID == uuid);
        foreach (var line in grn.Lines)
        {
            await repo.UpdateLineAsync(uuid, line.UUID, new UpdateGrnLineRequest
            {
                QtyReceived = line.QtyOrdered / 2,
                QtyAccepted = line.QtyOrdered / 2,
                QtyRejected = 0
            }, modifiedBy: 1);
        }

        grn.Status = "PENDING_APPROVAL";
        await wh.SaveChangesAsync();

        await handler.UpdateStatusAsync(uuid, "APPROVED");

        var dbPo = await demand.PurchaseOrders.FirstAsync(p => p.UUID == po.UUID);
        dbPo.Status.Should().Be("PARTIALLY_RECEIVED");
    }

    [Fact]
    public async Task PO_Status_Is_RECEIVED_When_All_Quantities_Fully_Received()
    {
        var po = ApprovalBuild.SentPo(("Laptop", 5m, 100m));
        var (handler, wh, demand, _, _) = ApprovalBuild.New(db => db.PurchaseOrders.Add(po));

        var grn = await ApprovalBuild.PendingGrnAsync(wh, demand, po, qtyAccepted: 5m);

        await handler.UpdateStatusAsync(grn.UUID, "APPROVED");

        var dbPo = await demand.PurchaseOrders.FirstAsync(p => p.UUID == po.UUID);
        dbPo.Status.Should().Be("RECEIVED");
    }
}

// ── WAR-002-TC-2: APPROVED → GRN status = APPROVED, ApprovedAt set ───────────

public class GrnStatusHandler_Approve_Status_Tests
{
    [Fact]
    public async Task GRN_Status_Is_APPROVED_And_ApprovedAt_Set()
    {
        var po = ApprovalBuild.SentPo(("Item", 5m, 100m));
        var (handler, wh, demand, _, _) = ApprovalBuild.New(db => db.PurchaseOrders.Add(po));

        var grn = await ApprovalBuild.PendingGrnAsync(wh, demand, po, qtyAccepted: 5m);

        await handler.UpdateStatusAsync(grn.UUID, "APPROVED");

        var dbGrn = await wh.Grns.FirstAsync(g => g.UUID == grn.UUID);
        dbGrn.Status.Should().Be("APPROVED");
        dbGrn.ApprovedAt.Should().NotBeNull();
    }
}

// ── WAR-002-TC-3: REJECTED → GRN status = REJECTED ───────────────────────────

public class GrnStatusHandler_Reject_Tests
{
    [Fact]
    public async Task GRN_Status_Is_REJECTED_After_Reject_Transition()
    {
        var po = ApprovalBuild.SentPo(("Item", 5m, 100m));
        var (handler, wh, demand, _, _) = ApprovalBuild.New(db => db.PurchaseOrders.Add(po));

        var grn = await ApprovalBuild.PendingGrnAsync(wh, demand, po, qtyAccepted: 5m);

        await handler.UpdateStatusAsync(grn.UUID, "REJECTED");

        var dbGrn = await wh.Grns.FirstAsync(g => g.UUID == grn.UUID);
        dbGrn.Status.Should().Be("REJECTED");
    }
}

// ── WAR-002-TC-4: APPROVED publishes GrnApprovedEvent ────────────────────────

public class GrnStatusHandler_Approve_Event_Tests
{
    [Fact]
    public async Task Approve_Publishes_GrnApprovedEvent()
    {
        var po = ApprovalBuild.SentPo(("Item", 5m, 100m));
        var (handler, wh, demand, _, publisher) = ApprovalBuild.New(db => db.PurchaseOrders.Add(po));

        var grn = await ApprovalBuild.PendingGrnAsync(wh, demand, po, qtyAccepted: 5m);

        await handler.UpdateStatusAsync(grn.UUID, "APPROVED");

        publisher.Published.Should().HaveCount(1);
        publisher.Published[0].GrnUuid.Should().Be(grn.UUID);
    }
}

// ── WAR-002-TC-5: Atomicity — stock post fails → GRN stays PENDING_APPROVAL ──

public class GrnStatusHandler_Atomicity_Tests
{
    [Fact]
    public async Task GRN_Status_Unchanged_When_Stock_Post_Throws()
    {
        var po = ApprovalBuild.SentPo(("Item", 5m, 100m));
        var (handler, wh, demand, _, _) = ApprovalBuild.New(
            seedDemand:  db => db.PurchaseOrders.Add(po),
            stockPoster: new ThrowingGrnStockPoster());

        var grn = await ApprovalBuild.PendingGrnAsync(wh, demand, po, qtyAccepted: 5m);

        var act = () => handler.UpdateStatusAsync(grn.UUID, "APPROVED");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*inventory post failure*");

        var dbGrn = await wh.Grns.FirstAsync(g => g.UUID == grn.UUID);
        dbGrn.Status.Should().Be("PENDING_APPROVAL");
    }
}

// ── WAR-002-TC-6: APPROVED → stock incremented in InventoryItem ──────────────

public class EfGrnInventoryPoster_Tests
{
    [Fact]
    public async Task Approve_Increments_InventoryItem_QtyOnHand()
    {
        var productUuid   = Guid.NewGuid();
        var warehouseUuid = Guid.NewGuid();

        var po = ApprovalBuild.SentPo(("Laptop", 10m, 1_000m));
        var (_, wh, demand, inv, _) = ApprovalBuild.New(
            seedDemand: db => db.PurchaseOrders.Add(po),
            seedInventory: db =>
            {
                db.Products.Add(new Product
                {
                    Uuid = productUuid, Sku = "LAP-001", Name = "Laptop",
                    Status = "ACTIVE", IsActive = true, CreatedBy = 1
                });
                db.Warehouses.Add(new InventoryWarehouse
                {
                    Uuid = warehouseUuid, Code = "WH1", Name = "Main WH",
                    IsActive = true, CreatedBy = 1
                });
            });

        var grn = await ApprovalBuild.PendingGrnAsync(
            wh, demand, po,
            qtyAccepted:   10m,
            warehouseUuid: warehouseUuid,
            productUuid:   productUuid);

        var efPoster = new EfGrnInventoryPoster(inv, new NullInventoryLedgerService());
        await efPoster.PostToInventoryAsync(grn, approvedBy: 1);

        var product   = await inv.Products.FirstAsync(p => p.Uuid == productUuid);
        var warehouse = await inv.Warehouses.FirstAsync(w => w.Uuid == warehouseUuid);
        var item      = await inv.InventoryItems
            .FirstOrDefaultAsync(i => i.ProductId == product.Id && i.WarehouseId == warehouse.Id);

        item.Should().NotBeNull();
        item!.QtyOnHand.Should().Be(10m);
    }
}
