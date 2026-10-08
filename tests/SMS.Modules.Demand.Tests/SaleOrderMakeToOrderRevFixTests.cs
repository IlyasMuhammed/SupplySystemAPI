using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>
/// A34 REV-06 / REV-07 / REV-08 (lead decisions): a make-to-order order left with an unplanned DRAFT production order
/// still offers "Create production orders", which plans it; "Reserve every line" leaves make-to-order lines alone (the
/// single-line reserve stays the D-28 escape); and one BOM level's days come from Inventory per D-12.
/// </summary>
public class SaleOrderMakeToOrderRevFixTests
{
    private const int User = A34SoHarness.User;

    // ── REV-06 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task REV_06_a_production_order_left_in_DRAFT_keeps_the_button_and_the_button_plans_it()
    {
        var h = A34SoHarness.Create();
        var mto = h.MakeToOrderVariant();
        h.Production.ThrowOnPlan = new BadRequestException("The BOM changed since the order was created.");
        var so = await h.DraftAsync((mto, null));
        var confirmed = (await h.Service.ConfirmWithResultAsync(so, User))!;
        confirmed.ProductionCreationFailed.Should().BeTrue();
        (await h.ReadAsync(so)).ProductionCreationPendingSince.Should().BeNull("a 400 clears the flag");

        (await h.Service.GetByIdAsync(so))!.ProductionCreationPending.Should().BeTrue("the DRAFT order is not made yet");

        h.Production.ThrowOnPlan = null;
        var pressed = (await h.Service.CreateProductionOrdersAsync(so, User))!;

        pressed.ProductionCreationFailed.Should().BeFalse();
        pressed.ProductionOrders.Should().ContainSingle().Which.Created.Should().BeFalse();
        h.Production.Pos.Should().ContainSingle().Which.Status.Should().Be("PLANNED");
        (await h.Service.GetByIdAsync(so))!.ProductionCreationPending.Should().BeFalse();
    }

    [Fact]
    public async Task Planning_refusals_gathered_by_Material_count_as_a_final_refusal_and_do_not_loop_the_sweep()
    {
        // MFG plans every DRAFT order and rethrows an AggregateException when several were refused.
        var h = A34SoHarness.Create();
        var mto = h.MakeToOrderVariant();
        h.Production.ThrowOnPlan = new AggregateException(new BadRequestException("BOM changed."), new BadRequestException("No warehouse."));
        var so = await h.DraftAsync((mto, null));

        var result = (await h.Service.ConfirmWithResultAsync(so, User))!;

        result.ProductionCreationFailed.Should().BeTrue();
        result.ProductionMessage.Should().Contain("BOM changed.");
        (await h.ReadAsync(so)).ProductionCreationPendingSince.Should().BeNull("every inner error is a business refusal");
    }

    // ── REV-07 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task REV_07_reserve_every_line_skips_make_to_order_lines_and_reserves_the_rest()
    {
        var tenant = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var db = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);
        var order = new SaleOrder
        {
            SoNumber = "SO-2026-00031", PartnerId = Guid.NewGuid(), OrderDate = DateTime.UtcNow.Date, CurrencyId = Guid.NewGuid(),
            Status = "CONFIRMED", DeliveryMode = "SHIP", CreatedBy = User,
            Lines =
            {
                new SaleOrderLine { VariantUuid = Guid.NewGuid(), Quantity = 5m, UnitPrice = 1m, Status = "OPEN", FulfillmentMode = "BACK_TO_BACK", DeficitQty = 5m },
                new SaleOrderLine { VariantUuid = Guid.NewGuid(), Quantity = 8m, UnitPrice = 1m, Status = "OPEN", FulfillmentMode = "MAKE_TO_ORDER", DeficitQty = 8m }
            }
        };
        db.SaleOrders.Add(order);
        await db.SaveChangesAsync();
        var stockLine = order.Lines.Single(l => l.FulfillmentMode == "BACK_TO_BACK");
        var mtoLine = order.Lines.Single(l => l.FulfillmentMode == "MAKE_TO_ORDER");

        var reservedLines = new List<Guid?>();
        var stock = new Mock<IStockReservationService>();
        stock.Setup(s => s.GetBySourceAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<ReservationSummary>());
        stock.Setup(s => s.GetAvailableAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Guid> ids, Guid? _, CancellationToken _) =>
                (IReadOnlyList<VariantAvailability>)ids.Select(v => new VariantAvailability(v, Guid.NewGuid(), "Central", 100m)).ToList());
        stock.Setup(s => s.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ReservationRequest>>(), It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, Guid _, IReadOnlyList<ReservationRequest> reqs, int _, DateTime? _, CancellationToken _) =>
            {
                reservedLines.AddRange(reqs.Select(r => r.SourceLineUuid));
                return new ReservationResult(true, reqs.Select(r => new ReservationLineResult(r.VariantUuid, r.SourceLineUuid, r.Quantity, r.Quantity, 0m, 100m, null)).ToList());
            });
        var config = new Mock<ISaleOrderConfigService>();
        config.Setup(c => c.GetConfigAsync()).ReturnsAsync(new SaleOrderConfigModel { ReservationTtlHours = 72 });
        var service = new SaleOrderReservationService(db, tenant, stock.Object, config.Object, Mock.Of<IBackgroundJobClient>());

        var result = (await service.ReserveAllAsync(order.UUID, new ReserveAllSaleOrderLinesRequest { AllowPartial = true }, User))!;

        reservedLines.Should().Equal(stockLine.UUID);
        var skipped = result.Lines.Single(l => l.LineUuid == mtoLine.UUID);
        skipped.Outcome.Should().Be("SKIPPED");
        skipped.Message.Should().Be("Line 2 is made to order: its production order makes it. Reserve it on its own to use stock instead.");
        result.ReservedLineCount.Should().Be(1);
    }

    // ── REV-08: one BOM level's days (D-12) ──────────────────────────────

    private static (InventoryDbContext Inv, Guid Org) NewInventory()
    {
        var org = Guid.NewGuid();
        var inv = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options,
            new StaticTenantContext { OrganizationId = org });
        return (inv, org);
    }

    private static Guid Seed(InventoryDbContext inv, Guid org, string supplyMethod, int? productDays, int? variantDays)
    {
        var product = new Product
        {
            Uuid = Guid.NewGuid(), Sku = $"SKU-{Guid.NewGuid():N}"[..12], Name = "Widget", OrganizationId = org,
            SupplyMethod = supplyMethod, ProductType = ProductType.FinishedGood, IsActive = true, CreatedBy = 1, LeadTimeDays = productDays
        };
        var variant = new ProductVariant
        {
            Uuid = Guid.NewGuid(), Sku = $"{product.Sku}-1", VariantName = "Default", IsDefault = true, IsActive = true, CreatedBy = 1,
            OrganizationId = org, ManufacturingLeadTimeDays = variantDays
        };
        product.Variants.Add(variant);
        inv.Products.Add(product);
        inv.SaveChanges();
        return variant.Uuid;
    }

    [Fact]
    public async Task REV_08_level_days_are_the_variants_then_a_manufactured_products_then_one()
    {
        var (inv, org) = NewInventory();
        var own      = Seed(inv, org, SupplyMethod.Manufacture, productDays: 6, variantDays: 4);
        var product  = Seed(inv, org, SupplyMethod.Manufacture, productDays: 6, variantDays: null);
        var bought   = Seed(inv, org, SupplyMethod.Purchase, productDays: 9, variantDays: null);
        var reader = new InventoryManufacturingLevelDays(inv);

        (await reader.GetAsync(org, own)).Should().Be(4);
        (await reader.GetAsync(org, product)).Should().Be(6);
        (await reader.GetAsync(org, bought)).Should().Be(1, "a purchased product's lead time is a supplier lead, not manufacturing days");
        (await reader.GetAsync(Guid.NewGuid(), own)).Should().Be(1, "another organization's variant is absent");
    }
}
