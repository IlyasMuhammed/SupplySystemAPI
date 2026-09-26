using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>A30 Phase 4 Track C / decision D1 — the manufacturing half of a sale order line's deficit.</summary>
public class SaleOrderManufacturingServiceTests
{
    private const int User = 9;

    private sealed record Harness(
        SaleOrderManufacturingService Service, DemandDbContext Db,
        Mock<IAllocationEngine> Engine, Mock<IProductionDemandService> Production);

    private static Harness NewHarness()
    {
        var db = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new StaticTenantContext { OrganizationId = Guid.NewGuid() });
        var engine = new Mock<IAllocationEngine>();
        var production = new Mock<IProductionDemandService>();
        var service = new SaleOrderManufacturingService(db, engine.Object, production.Object, NullLogger<SaleOrderManufacturingService>.Instance);
        return new Harness(service, db, engine, production);
    }

    private static async Task<(Guid OrderUuid, Guid LineUuid, Guid Variant)> Seed(Harness h)
    {
        var variant = Guid.NewGuid();
        var order = new SaleOrder
        {
            SoNumber = "SO-2026-00050", PartnerId = Guid.NewGuid(), OrderDate = DateTime.UtcNow.Date, CurrencyId = Guid.NewGuid(),
            Status = "CONFIRMED", DeliveryMode = "SELF_PICKUP", CreatedBy = 1,
            Lines = { new SaleOrderLine { VariantUuid = variant, Quantity = 50m, UnitPrice = 40m, LineTotal = 2000m, FulfillmentMode = "BACK_TO_BACK", DeficitQty = 50m, Status = "OPEN" } }
        };
        h.Db.SaleOrders.Add(order);
        await h.Db.SaveChangesAsync();
        return (order.UUID, order.Lines.Single().UUID, variant);
    }

    private static readonly Guid DemandUuid = Guid.NewGuid();

    private void RegistersDemand(Harness h, Guid orderUuid, Guid lineUuid, Guid variant, decimal deficit) =>
        h.Engine.Setup(e => e.RegisterDemandAsync(
                It.Is<AllocationDemandRegistration>(r =>
                    r.DemandType == AllocationDemandType.SalesOrder && r.DemandUuid == orderUuid &&
                    r.DemandLineUuid == lineUuid && r.VariantUuid == variant && r.RequiredQty == deficit),
                User, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DemandAllocationSummary(
                DemandUuid, AllocationDemandType.SalesOrder, orderUuid, lineUuid, "SO-2026-00050", variant, null,
                deficit, 0, 0, 0, deficit, DateTime.UtcNow, AllocationPriority.Normal, AllocationDemandStatus.Open));

    [Fact]
    public async Task Registers_the_lines_own_demand_and_raises_no_production_order_when_the_run_covers_it()
    {
        var h = NewHarness();
        var (orderUuid, lineUuid, variant) = await Seed(h);
        RegistersDemand(h, orderUuid, lineUuid, variant, 50m);
        h.Engine.Setup(e => e.GetDemandAsync(DemandUuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DemandAllocationSummary(
                DemandUuid, AllocationDemandType.SalesOrder, orderUuid, lineUuid, "SO-2026-00050", variant, null,
                50m, 50m, 0, 0, 0, DateTime.UtcNow, AllocationPriority.Normal, AllocationDemandStatus.Open));

        await h.Service.FulfillDeficitAsync(orderUuid, lineUuid, 50m, User);

        h.Engine.Verify(e => e.AllocateAsync(variant, null, User, It.IsAny<CancellationToken>()), Times.Once);
        h.Production.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Raises_a_production_order_for_whatever_the_run_leaves_short()
    {
        var h = NewHarness();
        var (orderUuid, lineUuid, variant) = await Seed(h);
        RegistersDemand(h, orderUuid, lineUuid, variant, 50m);
        h.Engine.Setup(e => e.GetDemandAsync(DemandUuid, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DemandAllocationSummary(
                DemandUuid, AllocationDemandType.SalesOrder, orderUuid, lineUuid, "SO-2026-00050", variant, null,
                50m, 20m, 0, 0, 30m, DateTime.UtcNow, AllocationPriority.Normal, AllocationDemandStatus.Open));

        await h.Service.FulfillDeficitAsync(orderUuid, lineUuid, 50m, User);

        h.Production.Verify(p => p.EnsureForSourceAsync(
            variant, 30m, It.IsAny<DateTime>(), AllocationPriority.Normal,
            ProductionSourceType.SalesOrder, orderUuid, lineUuid, "SO-2026-00050", User, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task An_unknown_line_is_a_quiet_no_op()
    {
        var h = NewHarness();
        await h.Service.FulfillDeficitAsync(Guid.NewGuid(), Guid.NewGuid(), 10m, User);

        h.Engine.VerifyNoOtherCalls();
        h.Production.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_deficit_of_zero_or_less_does_nothing()
    {
        var h = NewHarness();
        var (orderUuid, lineUuid, _) = await Seed(h);

        await h.Service.FulfillDeficitAsync(orderUuid, lineUuid, 0m, User);

        h.Engine.VerifyNoOtherCalls();
        h.Production.VerifyNoOtherCalls();
    }
}
