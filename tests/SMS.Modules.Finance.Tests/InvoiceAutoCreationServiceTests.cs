using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Repositories;
using SMS.Modules.Finance.Services;
using SMS.Modules.Warehouse.Data;
using SMS.Modules.Warehouse.Domain;
using Xunit;

namespace SMS.Modules.Finance.Tests;

file static class Build
{
    internal static (InvoiceAutoCreationService svc, FinanceDbContext finance, WarehouseDbContext wh) New(
        Action<DemandDbContext>? seedDemand = null)
    {
        var finance = new FinanceDbContext(new DbContextOptionsBuilder<FinanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var demand = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        seedDemand?.Invoke(demand);
        demand.SaveChanges();

        var wh = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        var ledger = new SupplierLedgerService(finance);
        var invoiceRepo = new InvoiceRepository(finance, demand, wh, ledger);
        var svc = new InvoiceAutoCreationService(wh, finance, invoiceRepo);

        return (svc, finance, wh);
    }

    internal static PurchaseOrder Po(Guid uuid, Guid supplierId, decimal totalAmount) => new()
    {
        UUID = uuid, PoNumber = "PO-TEST-1", Title = "Test PO",
        SupplierId = supplierId, SupplierName = "Test Supplier",
        Status = "SENT", IsActive = true, CreatedBy = 1, CreatedDate = DateTime.UtcNow,
        TotalAmount = totalAmount
    };

    internal static Grn ApprovedGrn(
        Guid grnUuid, Guid poUuid, Guid supplierId, string status,
        params (decimal qtyAccepted, decimal? unitCost)[] lines)
    {
        var grn = new Grn
        {
            UUID = grnUuid, TraceId = Guid.NewGuid(), GrnNumber = "GRN-TEST-1",
            PoUuid = poUuid, PoNumber = "PO-TEST-1", SupplierId = supplierId, SupplierName = "Test Supplier",
            WarehouseUuid = Guid.NewGuid(), ReceivedAt = DateTime.UtcNow, Status = status,
            IsActive = true, CreatedBy = 1, CreatedDate = DateTime.UtcNow
        };

        int lineNo = 1;
        foreach (var (qtyAccepted, unitCost) in lines)
        {
            grn.Lines.Add(new GrnLine
            {
                UUID = Guid.NewGuid(), PoLineUuid = Guid.NewGuid(), LineNo = lineNo++,
                ItemDescription = "Test Item", UnitOfMeasure = "PC",
                QtyOrdered = qtyAccepted, QtyReceived = qtyAccepted, QtyAccepted = qtyAccepted, QtyRejected = 0,
                UnitCost = unitCost
            });
        }
        return grn;
    }
}

public class CreateFromGrnAsync_Tests
{
    [Fact]
    public async Task Creates_Invoice_Linked_To_Grn_With_Lines_From_Accepted_Quantities()
    {
        var supplierId = Guid.NewGuid();
        var poUuid      = Guid.NewGuid();
        var grnUuid     = Guid.NewGuid();

        var (svc, finance, wh) = Build.New(db => db.PurchaseOrders.Add(Build.Po(poUuid, supplierId, 1000m)));
        wh.Grns.Add(Build.ApprovedGrn(grnUuid, poUuid, supplierId, "APPROVED", (10m, 100m)));
        await wh.SaveChangesAsync();

        await svc.CreateFromGrnAsync(grnUuid);

        var invoice = await finance.Invoices.Include(i => i.Lines).SingleAsync(i => i.GrnUuid == grnUuid);
        invoice.SupplierId.Should().Be(supplierId);
        invoice.PoUuid.Should().Be(poUuid);
        invoice.Subtotal.Should().Be(1000m);
        invoice.TotalAmount.Should().Be(1000m);
        invoice.Lines.Should().HaveCount(1);
        invoice.Lines.Single().QtyInvoiced.Should().Be(10m);
    }

    [Fact]
    public async Task Second_Call_For_Same_Grn_Does_Not_Create_A_Duplicate_Invoice()
    {
        var supplierId = Guid.NewGuid();
        var poUuid      = Guid.NewGuid();
        var grnUuid     = Guid.NewGuid();

        var (svc, finance, wh) = Build.New(db => db.PurchaseOrders.Add(Build.Po(poUuid, supplierId, 1000m)));
        wh.Grns.Add(Build.ApprovedGrn(grnUuid, poUuid, supplierId, "APPROVED", (10m, 100m)));
        await wh.SaveChangesAsync();

        await svc.CreateFromGrnAsync(grnUuid);
        await svc.CreateFromGrnAsync(grnUuid);

        var count = await finance.Invoices.CountAsync(i => i.GrnUuid == grnUuid);
        count.Should().Be(1);
    }

    [Fact]
    public async Task Grn_Not_Yet_APPROVED_Does_Not_Create_An_Invoice()
    {
        var supplierId = Guid.NewGuid();
        var poUuid      = Guid.NewGuid();
        var grnUuid     = Guid.NewGuid();

        var (svc, finance, wh) = Build.New(db => db.PurchaseOrders.Add(Build.Po(poUuid, supplierId, 1000m)));
        wh.Grns.Add(Build.ApprovedGrn(grnUuid, poUuid, supplierId, "PENDING_APPROVAL", (10m, 100m)));
        await wh.SaveChangesAsync();

        await svc.CreateFromGrnAsync(grnUuid);

        (await finance.Invoices.AnyAsync(i => i.GrnUuid == grnUuid)).Should().BeFalse();
    }

    [Fact]
    public async Task Grn_With_No_Costed_Lines_Does_Not_Create_A_Zero_Value_Invoice()
    {
        var supplierId = Guid.NewGuid();
        var poUuid      = Guid.NewGuid();
        var grnUuid     = Guid.NewGuid();

        var (svc, finance, wh) = Build.New(db => db.PurchaseOrders.Add(Build.Po(poUuid, supplierId, 1000m)));
        wh.Grns.Add(Build.ApprovedGrn(grnUuid, poUuid, supplierId, "APPROVED", (10m, null)));
        await wh.SaveChangesAsync();

        await svc.CreateFromGrnAsync(grnUuid);

        (await finance.Invoices.AnyAsync(i => i.GrnUuid == grnUuid)).Should().BeFalse();
    }
}
