using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Hangfire.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Repositories;
using SMS.Modules.Demand.Services;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Models;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>A29-P5-03 §6.1/§6.2 — a PO raised from one sale order line's deficit, wired to a real
/// PurchaseOrderRepository and a real Inventory context (so the line's description and unit of
/// measure are genuinely resolved, not assumed), with the workflow engine, config and supplier-name
/// lookup mocked.</summary>
public class AutoPurchaseOrderServiceTests
{
    private const int Creator = 11;

    private sealed record Harness(
        DemandDbContext Db, AutoPurchaseOrderService Service, Guid OrgId,
        Mock<IPurchaseOrderService> PurchaseOrders, Mock<ISupplierNameLookupService> Names,
        List<Job> CapturedJobs, Guid SupplierId, Guid VariantUuid);

    private static (Mock<IBackgroundJobClient> Mock, List<Job> Captured) MockJobs()
    {
        var captured = new List<Job>();
        var mock = new Mock<IBackgroundJobClient>();
        mock.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()))
            .Callback<Job, IState>((job, _) => captured.Add(job))
            .Returns("fake-job-id");
        return (mock, captured);
    }

    private static Harness NewHarness(string approvalMode = AutoPoApprovalModes.DraftOnly, bool dropShipEnabled = true)
    {
        var orgId = Guid.NewGuid();
        var db = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new StaticTenantContext { OrganizationId = orgId });
        var inventoryDb = new InventoryDbContext(
            new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new StaticTenantContext { OrganizationId = orgId });

        var variantUuid = Guid.NewGuid();
        var product = new Product
        {
            Uuid = Guid.NewGuid(), Sku = "CBL", Name = "4mm cable", Status = "ACTIVE", IsActive = true,
            UomCode = "MTR", CreatedBy = 1
        };
        product.Variants.Add(new ProductVariant
        {
            Uuid = variantUuid, Sku = "CBL-4MM", VariantName = "Default", IsDefault = true, IsActive = true,
            CreatedDate = DateTime.UtcNow
        });
        inventoryDb.Products.Add(product);
        inventoryDb.SaveChanges();

        var supplierId = Guid.NewGuid();
        var names = new Mock<ISupplierNameLookupService>();
        names.Setup(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()))
            .ReturnsAsync(new Dictionary<Guid, string> { [supplierId] = "TechSupply Co." });

        var config = new Mock<ISaleOrderConfigService>();
        config.Setup(c => c.GetConfigAsync()).ReturnsAsync(
            new SaleOrderConfigModel { AutoPoApprovalMode = approvalMode, DropShipEnabled = dropShipEnabled });

        var purchaseOrders = new Mock<IPurchaseOrderService>();
        var (jobsMock, captured) = MockJobs();
        var repo = new PurchaseOrderRepository(db, NullLogger<PurchaseOrderRepository>.Instance, inventoryDb);

        var service = new AutoPurchaseOrderService(
            db, repo, purchaseOrders.Object, config.Object, names.Object, jobsMock.Object,
            NullLogger<AutoPurchaseOrderService>.Instance);

        return new Harness(db, service, orgId, purchaseOrders, names, captured, supplierId, variantUuid);
    }

    private static async Task<(Guid OrderUuid, Guid LineUuid)> SeedOrder(
        Harness h, string orderStatus = "CONFIRMED", string fulfillmentMode = "BACK_TO_BACK",
        Guid? shippingAddressId = null, bool noShippingAddress = false)
    {
        var order = new SaleOrder
        {
            SoNumber = "SO-2026-00042", PartnerId = Guid.NewGuid(), OrderDate = DateTime.UtcNow.Date,
            CurrencyId = Guid.NewGuid(), Status = orderStatus, DeliveryMode = noShippingAddress ? "SELF_PICKUP" : "SHIP",
            ShippingAddressId = noShippingAddress ? null : shippingAddressId ?? Guid.NewGuid(),
            CreatedBy = Creator, TraceId = Guid.NewGuid(),
            Lines =
            {
                new SaleOrderLine
                {
                    VariantUuid = h.VariantUuid, Quantity = 100m, UnitPrice = 10m, LineTotal = 1000m,
                    FulfillmentMode = fulfillmentMode, AvailableQtyAtConfirm = 60m, DeficitQty = 40m, Status = "OPEN"
                }
            }
        };
        h.Db.SaleOrders.Add(order);
        await h.Db.SaveChangesAsync();
        var lineUuid = order.Lines.Single().UUID;
        h.Db.ChangeTracker.Clear();
        return (order.UUID, lineUuid);
    }

    private static Task<PurchaseOrder> LoadPo(Harness h, Guid poUuid) =>
        h.Db.PurchaseOrders.AsNoTracking().Include(p => p.Lines).SingleAsync(p => p.UUID == poUuid);

    // ── What gets created ────────────────────────────────────────────────────

    [Fact]
    public async Task Creates_a_back_to_back_po_linked_to_the_sale_order_line_and_on_its_trace()
    {
        var h = NewHarness();
        var (orderUuid, lineUuid) = await SeedOrder(h);

        var result = await h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 25m, Creator);

        var order = await h.Db.SaleOrders.AsNoTracking().Include(o => o.Lines).SingleAsync(o => o.UUID == orderUuid);
        var po = await LoadPo(h, result.PoUuid);
        po.Source.Should().Be("BACK_TO_BACK");
        po.LinkedSoId.Should().Be(order.Id);
        po.LinkedSoLineId.Should().Be(order.Lines.Single().Id);
        po.TraceId.Should().Be(order.TraceId, "§6.3.1: one trace spans SO to PO to GRN");
        po.SupplierId.Should().Be(h.SupplierId);
        po.SupplierName.Should().Be("TechSupply Co.");
        po.CreatedBy.Should().Be(Creator);
        po.OrganizationId.Should().Be(h.OrgId);
        result.Source.Should().Be("BACK_TO_BACK");
        result.AlreadyExisted.Should().BeFalse();
    }

    [Fact]
    public async Task The_po_line_carries_the_variant_description_unit_of_measure_quantity_and_price()
    {
        var h = NewHarness();
        var (_, lineUuid) = await SeedOrder(h);

        var result = await h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 25m, Creator);

        var po = await LoadPo(h, result.PoUuid);
        var line = po.Lines.Should().ContainSingle().Subject;
        line.VariantUuid.Should().Be(h.VariantUuid);
        line.ItemDescription.Should().Be("4mm cable (CBL-4MM)", "a default variant is described by product and SKU, not 'Default'");
        line.UnitOfMeasure.Should().Be("MTR");
        line.Quantity.Should().Be(40m);
        line.UnitPrice.Should().Be(25m);
        line.LineTotal.Should().Be(1000m);
        po.TotalAmount.Should().Be(1000m);
    }

    [Fact]
    public async Task Auto_generated_qty_price_and_supplier_are_stored_for_audit()
    {
        var h = NewHarness();
        var (_, lineUuid) = await SeedOrder(h);

        var result = await h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 25m, Creator);

        var po = await LoadPo(h, result.PoUuid);
        po.AutoGeneratedQty.Should().Be(40m);
        po.AutoGeneratedPrice.Should().Be(25m);
        po.AutoSelectedSupplierId.Should().Be(h.SupplierId);
    }

    [Fact]
    public async Task A_drop_ship_line_creates_a_drop_ship_po_carrying_the_customers_address()
    {
        var h = NewHarness();
        var address = Guid.NewGuid();
        var (_, lineUuid) = await SeedOrder(h, fulfillmentMode: "DROP_SHIP", shippingAddressId: address);

        var result = await h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 25m, Creator);

        var po = await LoadPo(h, result.PoUuid);
        po.Source.Should().Be("DROP_SHIP");
        po.CustomerShippingAddressId.Should().Be(address);
        result.Source.Should().Be("DROP_SHIP");
    }

    // ── A29-P5-05 §4.3 scenario 4 — drop ship ────────────────────────────────

    [Fact]
    public async Task A_drop_ship_po_names_no_delivery_warehouse_because_nothing_is_received_into_stock()
    {
        var h = NewHarness();
        var (_, lineUuid) = await SeedOrder(h, fulfillmentMode: "DROP_SHIP");

        var result = await h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 25m, Creator);

        var po = await LoadPo(h, result.PoUuid);
        po.DeliveryWarehouseId.Should().BeNull();
        po.Lines.Single().WarehouseId.Should().BeNull();
    }

    [Fact]
    public async Task A_drop_ship_line_is_refused_while_the_org_has_drop_ship_disabled()
    {
        var h = NewHarness(dropShipEnabled: false);
        var (_, lineUuid) = await SeedOrder(h, fulfillmentMode: "DROP_SHIP");

        var act = () => h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 25m, Creator);

        await act.Should().ThrowAsync<UnprocessableEntityException>().WithMessage("*drop ship is disabled*");
        (await h.Db.PurchaseOrders.CountAsync()).Should().Be(0, "it must not be quietly downgraded to a back-to-back PO");
        (await h.Db.SaleOrderLines.AsNoTracking().SingleAsync(l => l.UUID == lineUuid)).LinkedPoId.Should().BeNull();
    }

    [Fact]
    public async Task Disabling_drop_ship_does_not_affect_a_back_to_back_line()
    {
        var h = NewHarness(dropShipEnabled: false);
        var (_, lineUuid) = await SeedOrder(h, fulfillmentMode: "BACK_TO_BACK");

        var result = await h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 25m, Creator);

        result.Source.Should().Be("BACK_TO_BACK");
    }

    [Fact]
    public async Task A_drop_ship_line_on_an_order_with_no_shipping_address_is_refused()
    {
        var h = NewHarness();
        var (_, lineUuid) = await SeedOrder(h, fulfillmentMode: "DROP_SHIP", noShippingAddress: true);

        var act = () => h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 25m, Creator);

        await act.Should().ThrowAsync<UnprocessableEntityException>().WithMessage("*no shipping address*");
        (await h.Db.PurchaseOrders.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_drop_ship_po_follows_the_configured_approval_mode_like_any_other()
    {
        var h = NewHarness(AutoPoApprovalModes.AutoSend);
        var (_, lineUuid) = await SeedOrder(h, fulfillmentMode: "DROP_SHIP");

        var result = await h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 25m, Creator);

        result.Source.Should().Be("DROP_SHIP");
        result.Status.Should().Be("APPROVED");
    }

    [Fact]
    public async Task A_back_to_back_po_carries_no_customer_address()
    {
        var h = NewHarness();
        var (_, lineUuid) = await SeedOrder(h, shippingAddressId: Guid.NewGuid());

        var result = await h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 25m, Creator);

        (await LoadPo(h, result.PoUuid)).CustomerShippingAddressId.Should().BeNull();
    }

    [Fact]
    public async Task A_split_line_is_treated_as_back_to_back_for_its_deficit()
    {
        var h = NewHarness();
        var (_, lineUuid) = await SeedOrder(h, fulfillmentMode: "SPLIT");

        var result = await h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 25m, Creator);

        result.Source.Should().Be("BACK_TO_BACK");
    }

    [Fact]
    public async Task The_sale_order_line_is_linked_back_to_the_po_and_its_supplier()
    {
        var h = NewHarness();
        var (_, lineUuid) = await SeedOrder(h);

        var result = await h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 25m, Creator);

        var po = await LoadPo(h, result.PoUuid);
        var line = await h.Db.SaleOrderLines.AsNoTracking().SingleAsync(l => l.UUID == lineUuid);
        line.LinkedPoId.Should().Be(po.Id, "SaleOrderService.CancelAsync finds a line's PO through this");
        line.SelectedSupplierId.Should().Be(h.SupplierId);
    }

    // ── A29-P5-10 §14.2 — margin ─────────────────────────────────────────────

    [Fact]
    public async Task Margin_and_margin_percent_are_stored_on_the_sale_order_line()
    {
        // The line sells at 10 (SeedOrder); buying at 4 leaves 6 per unit, 60 percent.
        var h = NewHarness();
        var (_, lineUuid) = await SeedOrder(h);

        await h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 4m, Creator);

        var line = await h.Db.SaleOrderLines.AsNoTracking().SingleAsync(l => l.UUID == lineUuid);
        line.Margin.Should().Be(6.00m);
        line.MarginPercent.Should().Be(60.00m);
    }

    [Fact]
    public async Task A_purchase_price_above_the_selling_price_stores_a_negative_margin()
    {
        var h = NewHarness();
        var (_, lineUuid) = await SeedOrder(h);

        await h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 25m, Creator);

        var line = await h.Db.SaleOrderLines.AsNoTracking().SingleAsync(l => l.UUID == lineUuid);
        line.Margin.Should().Be(-15.00m);
        line.MarginPercent.Should().Be(-150.00m);
    }

    [Fact]
    public async Task A_drop_ship_line_gets_a_margin_too()
    {
        var h = NewHarness();
        var (_, lineUuid) = await SeedOrder(h, fulfillmentMode: "DROP_SHIP");

        await h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 4m, Creator);

        (await h.Db.SaleOrderLines.AsNoTracking().SingleAsync(l => l.UUID == lineUuid)).Margin.Should().Be(6.00m);
    }

    [Fact]
    public async Task A_line_with_no_po_yet_has_no_margin()
    {
        var h = NewHarness();
        var (_, lineUuid) = await SeedOrder(h);

        var line = await h.Db.SaleOrderLines.AsNoTracking().SingleAsync(l => l.UUID == lineUuid);

        line.Margin.Should().BeNull();
        line.MarginPercent.Should().BeNull();
    }

    [Fact]
    public async Task A_repeat_call_leaves_the_margin_alone()
    {
        var h = NewHarness();
        var (_, lineUuid) = await SeedOrder(h);
        await h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 4m, Creator);

        // A retried job with a different price must not rewrite what the first PO established.
        await h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 9m, Creator);

        (await h.Db.SaleOrderLines.AsNoTracking().SingleAsync(l => l.UUID == lineUuid)).Margin.Should().Be(6.00m);
    }

    // ── PO status per SaleOrderConfig.AutoPoApprovalMode (§3.4) ──────────────

    [Fact]
    public async Task Draft_only_mode_creates_a_draft_and_never_submits()
    {
        var h = NewHarness(AutoPoApprovalModes.DraftOnly);
        var (_, lineUuid) = await SeedOrder(h);

        var result = await h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 25m, Creator);

        result.Status.Should().Be("DRAFT");
        (await LoadPo(h, result.PoUuid)).Status.Should().Be("DRAFT");
        h.PurchaseOrders.Verify(p => p.SubmitForApprovalAsync(It.IsAny<Guid>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Auto_send_mode_creates_an_approved_po_without_going_through_the_workflow()
    {
        var h = NewHarness(AutoPoApprovalModes.AutoSend);
        var (_, lineUuid) = await SeedOrder(h);

        var result = await h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 25m, Creator);

        result.Status.Should().Be("APPROVED");
        (await LoadPo(h, result.PoUuid)).Status.Should().Be("APPROVED");
        h.PurchaseOrders.Verify(p => p.SubmitForApprovalAsync(It.IsAny<Guid>(), It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Require_workflow_mode_submits_the_po_and_reports_pending_approval()
    {
        var h = NewHarness(AutoPoApprovalModes.RequireWorkflow);
        var (_, lineUuid) = await SeedOrder(h);
        // The real workflow engine flips the status through PoStatusHandler; the mock stands in for that.
        h.PurchaseOrders.Setup(p => p.SubmitForApprovalAsync(It.IsAny<Guid>(), Creator))
            .Returns(async (Guid uuid, int _) =>
            {
                var po = await h.Db.PurchaseOrders.FirstAsync(x => x.UUID == uuid);
                po.Status = "PENDING_APPROVAL";
                await h.Db.SaveChangesAsync();
            });

        var result = await h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 25m, Creator);

        result.Status.Should().Be("PENDING_APPROVAL");
        h.PurchaseOrders.Verify(p => p.SubmitForApprovalAsync(result.PoUuid, Creator), Times.Once);
        (await LoadPo(h, result.PoUuid)).Status.Should().Be("PENDING_APPROVAL");
    }

    [Fact]
    public async Task A_failed_workflow_submission_leaves_the_po_as_a_draft_rather_than_failing()
    {
        var h = NewHarness(AutoPoApprovalModes.RequireWorkflow);
        var (_, lineUuid) = await SeedOrder(h);
        h.PurchaseOrders.Setup(p => p.SubmitForApprovalAsync(It.IsAny<Guid>(), It.IsAny<int>()))
            .ThrowsAsync(new BadRequestException("No workflow is configured for purchase orders."));

        var result = await h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 25m, Creator);

        result.Status.Should().Be("DRAFT");
        (await LoadPo(h, result.PoUuid)).Status.Should().Be("DRAFT");
        (await h.Db.SaleOrderLines.AsNoTracking().SingleAsync(l => l.UUID == lineUuid)).LinkedPoId
            .Should().NotBeNull("the PO exists and is linked even though approval routing failed");
    }

    [Fact]
    public async Task An_unrecognised_approval_mode_creates_a_draft()
    {
        var h = NewHarness("SOMETHING_ELSE");
        var (_, lineUuid) = await SeedOrder(h);

        var result = await h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 25m, Creator);

        result.Status.Should().Be("DRAFT");
    }

    // ── Idempotency (the caller is a retried Hangfire job) ───────────────────

    [Fact]
    public async Task Calling_twice_for_the_same_line_returns_the_first_po_and_creates_no_second()
    {
        var h = NewHarness();
        var (_, lineUuid) = await SeedOrder(h);

        var first = await h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 25m, Creator);
        var second = await h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 25m, Creator);

        second.PoUuid.Should().Be(first.PoUuid);
        second.AlreadyExisted.Should().BeTrue();
        (await h.Db.PurchaseOrders.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task With_several_live_pos_on_the_line_a_repeat_call_returns_the_original_not_the_newest()
    {
        // A29-P5-11 — a split adds a second PO to the same line; the line's PO stays the first.
        var h = NewHarness();
        var (_, lineUuid) = await SeedOrder(h);
        var first = await h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 25m, Creator);
        var original = await h.Db.PurchaseOrders.AsNoTracking().SingleAsync(p => p.UUID == first.PoUuid);
        h.Db.PurchaseOrders.Add(new PurchaseOrder
        {
            UUID = Guid.NewGuid(), PoNumber = "PO-SPLIT", SupplierId = Guid.NewGuid(), SupplierName = "Vendor B", Status = "DRAFT",
            Source = "BACK_TO_BACK", LinkedSoId = original.LinkedSoId, LinkedSoLineId = original.LinkedSoLineId,
            CreatedBy = Creator, CreatedDate = DateTime.UtcNow
        });
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var repeat = await h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 25m, Creator);

        repeat.PoUuid.Should().Be(first.PoUuid);
        (await h.Db.SaleOrderLines.AsNoTracking().SingleAsync(l => l.UUID == lineUuid)).LinkedPoId.Should().Be(original.Id);
    }

    [Fact]
    public async Task A_retry_repairs_a_missing_back_pointer_on_the_line()
    {
        var h = NewHarness();
        var (_, lineUuid) = await SeedOrder(h);
        var first = await h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 25m, Creator);
        // Simulates the previous attempt dying after the PO was saved but before the line was updated.
        var line = await h.Db.SaleOrderLines.SingleAsync(l => l.UUID == lineUuid);
        line.LinkedPoId = null;
        line.SelectedSupplierId = null;
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var retry = await h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 25m, Creator);

        retry.AlreadyExisted.Should().BeTrue();
        var repaired = await h.Db.SaleOrderLines.AsNoTracking().SingleAsync(l => l.UUID == lineUuid);
        repaired.LinkedPoId.Should().Be((await LoadPo(h, first.PoUuid)).Id);
        repaired.SelectedSupplierId.Should().Be(h.SupplierId);
    }

    [Fact]
    public async Task A_cancelled_po_does_not_block_a_new_one_for_the_same_line()
    {
        var h = NewHarness();
        var (_, lineUuid) = await SeedOrder(h);
        var first = await h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 25m, Creator);
        var cancelled = await h.Db.PurchaseOrders.SingleAsync(p => p.UUID == first.PoUuid);
        cancelled.Status = "CANCELLED";
        await h.Db.SaveChangesAsync();
        h.Db.ChangeTracker.Clear();

        var second = await h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 25m, Creator);

        second.AlreadyExisted.Should().BeFalse();
        second.PoUuid.Should().NotBe(first.PoUuid);
        var line = await h.Db.SaleOrderLines.AsNoTracking().SingleAsync(l => l.UUID == lineUuid);
        line.LinkedPoId.Should().Be((await LoadPo(h, second.PoUuid)).Id);
    }

    // ── Guards ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_unknown_sale_order_line_throws_not_found()
    {
        var h = NewHarness();

        var act = () => h.Service.CreateFromSODeficitAsync(Guid.NewGuid(), h.SupplierId, 40m, 25m, Creator);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Theory]
    [InlineData("DRAFT")]
    [InlineData("CANCELLED")]
    [InlineData("CLOSED")]
    public async Task A_sale_order_that_is_not_confirmed_cannot_have_a_po_raised_against_it(string status)
    {
        var h = NewHarness();
        var (_, lineUuid) = await SeedOrder(h, orderStatus: status);

        var act = () => h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 25m, Creator);

        await act.Should().ThrowAsync<UnprocessableEntityException>();
        (await h.Db.PurchaseOrders.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_partially_fulfilled_sale_order_can_still_have_a_po_raised()
    {
        var h = NewHarness();
        var (_, lineUuid) = await SeedOrder(h, orderStatus: "PARTIALLY_FULFILLED");

        var result = await h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 25m, Creator);

        result.PoUuid.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task A_non_positive_quantity_is_refused(int qty)
    {
        var h = NewHarness();
        var (_, lineUuid) = await SeedOrder(h);

        var act = () => h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, qty, 25m, Creator);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task A_negative_price_is_refused_but_a_zero_price_is_allowed()
    {
        var h = NewHarness();
        var (_, lineUuid) = await SeedOrder(h);

        var negative = () => h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, -1m, Creator);
        await negative.Should().ThrowAsync<BadRequestException>();

        var zero = await h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 0m, Creator);
        (await LoadPo(h, zero.PoUuid)).TotalAmount.Should().Be(0m);
    }

    [Fact]
    public async Task An_unknown_supplier_is_refused_and_creates_nothing()
    {
        var h = NewHarness();
        var (_, lineUuid) = await SeedOrder(h);

        var act = () => h.Service.CreateFromSODeficitAsync(lineUuid, Guid.NewGuid(), 40m, 25m, Creator);

        await act.Should().ThrowAsync<NotFoundException>();
        (await h.Db.PurchaseOrders.CountAsync()).Should().Be(0);
        (await h.Db.SaleOrderLines.AsNoTracking().SingleAsync(l => l.UUID == lineUuid)).LinkedPoId.Should().BeNull();
    }

    // ── Timeline (§6.3.1) ────────────────────────────────────────────────────

    [Fact]
    public async Task Enqueues_a_po_created_from_so_event_on_the_sale_orders_trace()
    {
        var h = NewHarness();
        var (orderUuid, lineUuid) = await SeedOrder(h);
        var traceId = (await h.Db.SaleOrders.AsNoTracking().SingleAsync(o => o.UUID == orderUuid)).TraceId;

        var result = await h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 25m, Creator);

        var job = h.CapturedJobs.Should().ContainSingle(j => j.Method.Name == "AppendAsync").Subject;
        job.Args[0].Should().Be(traceId);
        // A29-P5-04 §13.7 — the organization is a job argument, taken from the sale order itself.
        job.Args.Should().HaveCount(5);
        job.Args[4].Should().Be(h.OrgId);
        var evt = job.Args[1].Should().BeOfType<TimelineEvent>().Subject;
        evt.EventType.Should().Be("PO_CREATED_FROM_SO");
        evt.InterfaceCode.Should().Be("PO");
        evt.DocumentId.Should().Be(result.PoUuid);
        evt.PerformedBy.Should().Be(Creator);
        evt.Notes.Should().Be("40 units of CBL-4MM → TechSupply Co.");
    }

    [Fact]
    public async Task The_enqueued_timeline_job_survives_hangfires_own_serialization_round_trip()
    {
        // ITimelineAppendJob.AppendAsync now has two overloads (with and without an explicit org).
        // Hangfire stores a job as method name + parameter types and re-resolves it in the worker,
        // so this is the one place an overload could go wrong that a plain unit test can't see.
        var h = NewHarness();
        var (orderUuid, lineUuid) = await SeedOrder(h);
        var order = await h.Db.SaleOrders.AsNoTracking().SingleAsync(o => o.UUID == orderUuid);
        await h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 25m, Creator);
        var enqueued = h.CapturedJobs.Single(j => j.Method.Name == "AppendAsync");

        var revived = InvocationData.SerializeJob(enqueued).DeserializeJob();

        revived.Method.GetParameters().Should().HaveCount(5);
        revived.Args[0].Should().Be(order.TraceId);
        revived.Args[4].Should().Be(order.OrganizationId);
        revived.Args[1].Should().BeOfType<TimelineEvent>().Which.EventType.Should().Be("PO_CREATED_FROM_SO");
    }

    [Fact]
    public async Task The_sale_order_and_its_auto_created_po_resolve_to_the_same_trace()
    {
        // A29-P5-08 §13.5 — "shared trace_id, so SO events appear too": both documents' timeline
        // endpoints resolve their trace through these resolvers, so one trace id means one timeline.
        var h = NewHarness();
        var (orderUuid, lineUuid) = await SeedOrder(h);
        var result = await h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 25m, Creator);

        var soTrace = await new SoTraceIdResolver(h.Db).ResolveTraceIdAsync(orderUuid);
        var poTrace = await new PoTraceIdResolver(h.Db).ResolveTraceIdAsync(result.PoUuid);

        soTrace.Should().NotBeNull();
        poTrace.Should().Be(soTrace);
    }

    [Fact]
    public async Task A_repeat_call_enqueues_no_second_timeline_event()
    {
        var h = NewHarness();
        var (_, lineUuid) = await SeedOrder(h);
        await h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 25m, Creator);
        h.CapturedJobs.Clear();

        await h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 25m, Creator);

        h.CapturedJobs.Should().BeEmpty();
    }

    // ── Integration with what P4-04/P4-07 already resolve through the link ───

    [Fact]
    public async Task The_email_service_can_now_resolve_the_sale_order_of_an_auto_created_po()
    {
        // SaleOrderEmailService.SendPoApprovedAsync (P4-07) was correct but a no-op until now: it
        // finds a PO's sale order through SaleOrderLine.LinkedPoId, which nothing used to set.
        var h = NewHarness();
        var (orderUuid, lineUuid) = await SeedOrder(h);
        var result = await h.Service.CreateFromSODeficitAsync(lineUuid, h.SupplierId, 40m, 25m, Creator);

        var users = new Mock<IUserQueryService>();
        users.Setup(u => u.GetUserEmailAsync(Creator)).ReturnsAsync("creator@x.com");
        var partnerNames = new Mock<ISupplierNameLookupService>();
        partnerNames.Setup(p => p.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>())).ReturnsAsync(new Dictionary<Guid, string>());
        var (emailJobs, _) = MockJobs();
        var emailService = new SaleOrderEmailService(
            h.Db, Mock.Of<IStockReservationService>(), Mock.Of<ISaleOrderConfigService>(), Mock.Of<IOrgChartService>(),
            users.Object, partnerNames.Object, emailJobs.Object, NullLogger<SaleOrderEmailService>.Instance);

        await emailService.SendPoApprovedAsync(result.PoUuid);

        var row = await h.Db.SaleOrderIntimations.AsNoTracking().SingleAsync();
        row.EventType.Should().Be("PO_APPROVED");
        row.SaleOrderId.Should().Be((await h.Db.SaleOrders.AsNoTracking().SingleAsync(o => o.UUID == orderUuid)).Id);
        row.Recipients.Should().Contain("creator@x.com");
    }
}
