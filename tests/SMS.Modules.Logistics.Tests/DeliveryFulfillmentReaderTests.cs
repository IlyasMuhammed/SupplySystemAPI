using FluentAssertions;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Logistics.Services;
using Xunit;

namespace SMS.Modules.Logistics.Tests;

/// <summary>
/// A29-P7-04 — the read-only view Finance bills from. It must say faithfully what the delivery is and
/// what reached the customer, and it must not decide anything: status, prices and quantities already
/// billed are Finance's to judge.
/// </summary>
public class DeliveryFulfillmentReaderTests
{
    private static readonly DateTime T0 = new(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc);

    private static DeliveryOrder NewDelivery(string number, DeliveryStatus status, Guid? saleOrder, params DeliveryOrderLine[] lines)
    {
        var delivery = new DeliveryOrder
        {
            UUID = Guid.NewGuid(), DeliveryNumber = number,
            Direction     = LogisticsCode.Of(DeliveryDirection.Outbound),
            SourceType    = LogisticsCode.Of(DeliverySourceType.SaleOrder),
            Status        = LogisticsCode.Of(status),
            SaleOrderUuid = saleOrder,
            CreatedBy = 1, CreatedDate = T0
        };
        foreach (var line in lines) delivery.Lines.Add(line);
        return delivery;
    }

    private static DeliveryOrderLine Line(int lineNo, string description, decimal delivered, Guid? soLine = null, Guid? variant = null) =>
        new()
        {
            UUID = Guid.NewGuid(), LineNo = lineNo, ItemDescription = description,
            QtyOrdered = delivered + 5m, QtyDelivered = delivered, UnitOfMeasure = "EA",
            SoLineUuid = soLine, VariantUuid = variant, CreatedBy = 1, CreatedDate = T0
        };

    [Fact]
    public async Task It_reports_the_delivery_its_sale_order_its_status_and_every_line_as_delivered()
    {
        var (db, _, _) = LogisticsTestDb.New();
        var saleOrder = Guid.NewGuid();
        var soLineA = Guid.NewGuid();
        var soLineB = Guid.NewGuid();
        var variantA = Guid.NewGuid();
        var delivery = NewDelivery("DLV-2026-00007", DeliveryStatus.Delivered, saleOrder,
            Line(2, "Junction box", 3m, soLineB),
            Line(1, "4mm cable", 40m, soLineA, variantA));
        db.DeliveryOrders.Add(delivery);
        await db.SaveChangesAsync();

        var view = await new DeliveryFulfillmentReader(db).GetAsync(delivery.UUID);

        view.Should().NotBeNull();
        view!.DeliveryUuid.Should().Be(delivery.UUID);
        view.DeliveryNumber.Should().Be("DLV-2026-00007");
        view.Status.Should().Be(LogisticsCode.Of(DeliveryStatus.Delivered));
        view.SaleOrderUuid.Should().Be(saleOrder);

        view.Lines.Select(l => l.LineNo).Should().Equal(new List<int> { 1, 2 }, "in delivery line order, however they were stored");
        var first = view.Lines[0];
        first.Description.Should().Be("4mm cable");
        first.QtyDelivered.Should().Be(40m, "what reached the customer — not what was ordered");
        first.SoLineUuid.Should().Be(soLineA);
        first.VariantUuid.Should().Be(variantA);
        first.DeliveryLineUuid.Should().Be(delivery.Lines.Single(l => l.LineNo == 1).UUID);
        view.Lines[1].SoLineUuid.Should().Be(soLineB);
    }

    [Fact]
    public async Task It_reports_a_delivery_that_is_not_yet_delivered_as_it_is_and_leaves_the_judgement_to_finance()
    {
        var (db, _, _) = LogisticsTestDb.New();
        var delivery = NewDelivery("DLV-2026-00008", DeliveryStatus.InTransit, Guid.NewGuid(), Line(1, "Cable", 0m));
        db.DeliveryOrders.Add(delivery);
        await db.SaveChangesAsync();

        var view = await new DeliveryFulfillmentReader(db).GetAsync(delivery.UUID);

        view!.Status.Should().Be(LogisticsCode.Of(DeliveryStatus.InTransit));
        view.Lines.Single().QtyDelivered.Should().Be(0m);
    }

    [Fact]
    public async Task A_delivery_that_is_not_for_a_sale_order_says_so_by_having_no_sale_order()
    {
        var (db, _, _) = LogisticsTestDb.New();
        var delivery = NewDelivery("DLV-2026-00009", DeliveryStatus.Delivered, saleOrder: null, Line(1, "Cable", 4m));
        db.DeliveryOrders.Add(delivery);
        await db.SaveChangesAsync();

        var view = await new DeliveryFulfillmentReader(db).GetAsync(delivery.UUID);

        view!.SaleOrderUuid.Should().BeNull();
    }

    [Fact]
    public async Task An_unknown_delivery_is_nothing()
    {
        var (db, _, _) = LogisticsTestDb.New();

        (await new DeliveryFulfillmentReader(db).GetAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task A_deleted_delivery_is_nothing()
    {
        var (db, _, _) = LogisticsTestDb.New();
        var delivery = NewDelivery("DLV-2026-00010", DeliveryStatus.Delivered, Guid.NewGuid(), Line(1, "Cable", 4m));
        delivery.IsDelete = true;
        db.DeliveryOrders.Add(delivery);
        await db.SaveChangesAsync();

        (await new DeliveryFulfillmentReader(db).GetAsync(delivery.UUID)).Should().BeNull();
    }

    [Fact]
    public async Task Another_organizations_delivery_is_nothing()
    {
        var (db, _, dbName) = LogisticsTestDb.New();
        var delivery = NewDelivery("DLV-2026-00011", DeliveryStatus.Delivered, Guid.NewGuid(), Line(1, "Cable", 4m));
        db.DeliveryOrders.Add(delivery);
        await db.SaveChangesAsync();

        var stranger = LogisticsTestDb.OpenAs(dbName, Guid.NewGuid());

        (await new DeliveryFulfillmentReader(stranger).GetAsync(delivery.UUID)).Should().BeNull(
            "Finance must not be able to bill another tenant's delivery by guessing its id");
    }

    [Fact]
    public async Task Reading_changes_nothing()
    {
        var (db, _, _) = LogisticsTestDb.New();
        var delivery = NewDelivery("DLV-2026-00012", DeliveryStatus.Delivered, Guid.NewGuid(), Line(1, "Cable", 4m));
        db.DeliveryOrders.Add(delivery);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await new DeliveryFulfillmentReader(db).GetAsync(delivery.UUID);

        db.ChangeTracker.Entries().Should().BeEmpty("the read is untracked, so it can never be saved by accident");
    }
}
