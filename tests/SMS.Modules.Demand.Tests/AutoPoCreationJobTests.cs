using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>A29-P5-09 — the glue that turns a confirmed order's deficit into a PO: config check, line
/// still short, supplier selection, PO creation, then the intimation email, in that order.</summary>
public class AutoPoCreationJobTests
{
    private const int User = 9;

    private sealed record Harness(
        AutoPoCreationJob Job, DemandDbContext Db, Mock<ISaleOrderConfigService> Config,
        Mock<ISupplierSelectionService> Selection, Mock<IAutoPurchaseOrderService> AutoPo, Mock<ISaleOrderEmailService> Email);

    private static Harness NewHarness(bool autoPoEnabled = true)
    {
        var db = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new StaticTenantContext { OrganizationId = Guid.NewGuid() });
        var config = new Mock<ISaleOrderConfigService>();
        config.Setup(c => c.GetConfigAsync()).ReturnsAsync(new SaleOrderConfigModel { AutoPoEnabled = autoPoEnabled });
        var selection = new Mock<ISupplierSelectionService>();
        var autoPo = new Mock<IAutoPurchaseOrderService>();
        var email = new Mock<ISaleOrderEmailService>();

        var job = new AutoPoCreationJob(db, config.Object, selection.Object, autoPo.Object, email.Object, NullLogger<AutoPoCreationJob>.Instance);
        return new Harness(job, db, config, selection, autoPo, email);
    }

    private static async Task<(Guid OrderUuid, Guid LineUuid, Guid Variant)> Seed(Harness h, decimal? deficit = 40m, string mode = "BACK_TO_BACK")
    {
        var variant = Guid.NewGuid();
        var order = new SaleOrder
        {
            SoNumber = "SO-2026-00042", PartnerId = Guid.NewGuid(), OrderDate = DateTime.UtcNow.Date, CurrencyId = Guid.NewGuid(),
            Status = "CONFIRMED", DeliveryMode = "SELF_PICKUP", CreatedBy = 1,
            Lines = { new SaleOrderLine { VariantUuid = variant, Quantity = 100m, UnitPrice = 40m, LineTotal = 4000m, FulfillmentMode = mode, DeficitQty = deficit, Status = "OPEN" } }
        };
        h.Db.SaleOrders.Add(order);
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();
        return (order.UUID, order.Lines.Single().UUID, variant);
    }

    private static readonly Guid Supplier = Guid.NewGuid();

    private static void Selects(Harness h, bool manual = false)
    {
        h.Selection.Setup(s => s.SelectAsync(It.IsAny<Guid>(), It.IsAny<decimal>(), It.IsAny<int>()))
            .ReturnsAsync(manual
                ? new SupplierSelectionResult(true, null, null, null, "MANUAL", "manual")
                : new SupplierSelectionResult(false, Supplier, "TechSupply", 25m, "BEST_MATCH", "best"));
    }

    private static void Creates(Harness h, string source = "BACK_TO_BACK", bool already = false) =>
        h.AutoPo.Setup(a => a.CreateFromSODeficitAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<int>()))
            .ReturnsAsync(new AutoPurchaseOrderResult(Guid.NewGuid(), "PO-2026-00001", "DRAFT", source, already));

    [Fact]
    public async Task Selects_a_supplier_for_the_lines_current_deficit_and_creates_the_po_from_that_choice()
    {
        var h = NewHarness();
        var (orderUuid, lineUuid, variant) = await Seed(h, deficit: 40m);
        Selects(h);
        Creates(h);

        await h.Job.CreateForDeficitAsync(orderUuid, lineUuid, User);

        h.Selection.Verify(s => s.SelectAsync(variant, 40m, User), Times.Once);
        h.AutoPo.Verify(a => a.CreateFromSODeficitAsync(lineUuid, Supplier, 40m, 25m, User), Times.Once);
    }

    [Fact]
    public async Task Emails_the_intimation_department_that_the_po_was_created()
    {
        var h = NewHarness();
        var (orderUuid, lineUuid, _) = await Seed(h);
        Selects(h);
        var poUuid = Guid.NewGuid();
        h.AutoPo.Setup(a => a.CreateFromSODeficitAsync(lineUuid, Supplier, 40m, 25m, User))
            .ReturnsAsync(new AutoPurchaseOrderResult(poUuid, "PO-2026-00001", "DRAFT", "BACK_TO_BACK", false));

        await h.Job.CreateForDeficitAsync(orderUuid, lineUuid, User);

        h.Email.Verify(e => e.SendPoCreatedAsync(orderUuid, poUuid), Times.Once);
        h.Email.Verify(e => e.SendDropShipAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task A_drop_ship_po_is_announced_as_a_drop_ship_not_a_back_to_back_po()
    {
        var h = NewHarness();
        var (orderUuid, lineUuid, _) = await Seed(h, mode: "DROP_SHIP");
        Selects(h);
        Creates(h, source: "DROP_SHIP");

        await h.Job.CreateForDeficitAsync(orderUuid, lineUuid, User);

        h.Email.Verify(e => e.SendDropShipAsync(orderUuid), Times.Once);
        h.Email.Verify(e => e.SendPoCreatedAsync(It.IsAny<Guid>(), It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task Does_nothing_when_the_org_has_auto_po_disabled()
    {
        var h = NewHarness(autoPoEnabled: false);
        var (orderUuid, lineUuid, _) = await Seed(h);

        await h.Job.CreateForDeficitAsync(orderUuid, lineUuid, User);

        h.Selection.Verify(s => s.SelectAsync(It.IsAny<Guid>(), It.IsAny<decimal>(), It.IsAny<int>()), Times.Never);
        h.AutoPo.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(null)]
    public async Task Does_nothing_for_a_line_that_is_no_longer_short(int? deficit)
    {
        var h = NewHarness();
        var (orderUuid, lineUuid, _) = await Seed(h, deficit: deficit);

        await h.Job.CreateForDeficitAsync(orderUuid, lineUuid, User);

        h.Selection.Verify(s => s.SelectAsync(It.IsAny<Guid>(), It.IsAny<decimal>(), It.IsAny<int>()), Times.Never);
        h.AutoPo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Creates_no_po_and_sends_no_email_when_selection_needs_a_human()
    {
        var h = NewHarness();
        var (orderUuid, lineUuid, _) = await Seed(h);
        Selects(h, manual: true);

        await h.Job.CreateForDeficitAsync(orderUuid, lineUuid, User);

        h.AutoPo.VerifyNoOtherCalls();
        h.Email.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_repeat_run_that_finds_the_po_already_made_sends_no_second_email()
    {
        var h = NewHarness();
        var (orderUuid, lineUuid, _) = await Seed(h);
        Selects(h);
        Creates(h, already: true);

        await h.Job.CreateForDeficitAsync(orderUuid, lineUuid, User);

        h.Email.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task An_unknown_line_or_a_line_of_a_different_order_is_a_quiet_no_op()
    {
        var h = NewHarness();
        var (orderUuid, lineUuid, _) = await Seed(h);

        await h.Job.CreateForDeficitAsync(orderUuid, Guid.NewGuid(), User);
        await h.Job.CreateForDeficitAsync(Guid.NewGuid(), lineUuid, User);

        h.Selection.Verify(s => s.SelectAsync(It.IsAny<Guid>(), It.IsAny<decimal>(), It.IsAny<int>()), Times.Never);
    }

    // ── A30 Phase 4 Track C / decision D1 — a manufactured product's deficit ───────

    private sealed record ManufacturingHarness(
        AutoPoCreationJob Job, InventoryDbContext Inventory, Mock<ISaleOrderManufacturingService> Manufacturing,
        Mock<ISupplierSelectionService> Selection, Mock<IAutoPurchaseOrderService> AutoPo);

    private static ManufacturingHarness NewManufacturingHarness(DemandDbContext db)
    {
        var config = new Mock<ISaleOrderConfigService>();
        config.Setup(c => c.GetConfigAsync()).ReturnsAsync(new SaleOrderConfigModel { AutoPoEnabled = true });
        var selection = new Mock<ISupplierSelectionService>();
        var autoPo = new Mock<IAutoPurchaseOrderService>();
        var email = new Mock<ISaleOrderEmailService>();
        var manufacturing = new Mock<ISaleOrderManufacturingService>();
        var inventory = new InventoryDbContext(
            new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new StaticTenantContext());

        var job = new AutoPoCreationJob(
            db, config.Object, selection.Object, autoPo.Object, email.Object, NullLogger<AutoPoCreationJob>.Instance,
            inventory, manufacturing.Object);
        return new ManufacturingHarness(job, inventory, manufacturing, selection, autoPo);
    }

    private static Guid SeedVariant(InventoryDbContext inv, string supplyMethod)
    {
        var product = new Product
        {
            Uuid = Guid.NewGuid(), Sku = $"SKU-{Guid.NewGuid():N}"[..12], Name = "Test Product",
            SupplyMethod = supplyMethod, ProductType = ProductType.FinishedGood, IsActive = true, CreatedBy = 1
        };
        var variant = new ProductVariant { Uuid = Guid.NewGuid(), Sku = $"{product.Sku}-1", VariantName = "Default", IsDefault = true, IsActive = true, CreatedBy = 1 };
        product.Variants.Add(variant);
        inv.Products.Add(product);
        inv.SaveChanges();
        return variant.Uuid;
    }

    [Fact]
    public async Task A_manufactured_products_deficit_goes_to_the_manufacturing_service_not_a_purchase_order()
    {
        var db = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new StaticTenantContext { OrganizationId = Guid.NewGuid() });
        var h = NewManufacturingHarness(db);
        var variant = SeedVariant(h.Inventory, SupplyMethod.Manufacture);

        var order = new SaleOrder
        {
            SoNumber = "SO-2026-00099", PartnerId = Guid.NewGuid(), OrderDate = DateTime.UtcNow.Date, CurrencyId = Guid.NewGuid(),
            Status = "CONFIRMED", DeliveryMode = "SELF_PICKUP", CreatedBy = 1,
            Lines = { new SaleOrderLine { VariantUuid = variant, Quantity = 30m, UnitPrice = 40m, LineTotal = 1200m, FulfillmentMode = "BACK_TO_BACK", DeficitQty = 30m, Status = "OPEN" } }
        };
        db.SaleOrders.Add(order);
        await db.SaveChangesAsync();
        var lineUuid = order.Lines.Single().UUID;

        await h.Job.CreateForDeficitAsync(order.UUID, lineUuid, User);

        h.Manufacturing.Verify(m => m.FulfillDeficitAsync(order.UUID, lineUuid, 30m, User, It.IsAny<CancellationToken>()), Times.Once);
        h.Selection.Verify(s => s.SelectAsync(It.IsAny<Guid>(), It.IsAny<decimal>(), It.IsAny<int>()), Times.Never);
        h.AutoPo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_purchased_products_deficit_still_goes_through_the_ordinary_auto_po_path()
    {
        var db = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new StaticTenantContext { OrganizationId = Guid.NewGuid() });
        var h = NewManufacturingHarness(db);
        var variant = SeedVariant(h.Inventory, SupplyMethod.Purchase);
        h.Selection.Setup(s => s.SelectAsync(It.IsAny<Guid>(), It.IsAny<decimal>(), It.IsAny<int>()))
            .ReturnsAsync(new SupplierSelectionResult(false, Guid.NewGuid(), "TechSupply", 25m, "BEST_MATCH", "best"));
        h.AutoPo.Setup(a => a.CreateFromSODeficitAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<int>()))
            .ReturnsAsync(new AutoPurchaseOrderResult(Guid.NewGuid(), "PO-2026-00001", "DRAFT", "BACK_TO_BACK", false));

        var order = new SaleOrder
        {
            SoNumber = "SO-2026-00098", PartnerId = Guid.NewGuid(), OrderDate = DateTime.UtcNow.Date, CurrencyId = Guid.NewGuid(),
            Status = "CONFIRMED", DeliveryMode = "SELF_PICKUP", CreatedBy = 1,
            Lines = { new SaleOrderLine { VariantUuid = variant, Quantity = 30m, UnitPrice = 40m, LineTotal = 1200m, FulfillmentMode = "BACK_TO_BACK", DeficitQty = 30m, Status = "OPEN" } }
        };
        db.SaleOrders.Add(order);
        await db.SaveChangesAsync();
        var lineUuid = order.Lines.Single().UUID;

        await h.Job.CreateForDeficitAsync(order.UUID, lineUuid, User);

        h.Manufacturing.VerifyNoOtherCalls();
        h.Selection.Verify(s => s.SelectAsync(variant, 30m, User), Times.Once);
        h.AutoPo.Verify(a => a.CreateFromSODeficitAsync(lineUuid, It.IsAny<Guid>(), 30m, 25m, User), Times.Once);
    }
}
