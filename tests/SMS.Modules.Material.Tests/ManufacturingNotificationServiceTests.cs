using FluentAssertions;
using Moq;
using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Material.Tests;

/// <summary>
/// A30-P5-01. <see cref="ManufacturingNotificationService"/> in isolation: what each of the ten
/// events sends and, above all, who it goes to — the one thing worth a regression guard here, since
/// every call site (ProductionOrderService, SupplyRequirementEngine, ProductionReadinessListener,
/// QualityInspectionService, FinishedGoodsReceiptService) is already exercised end to end elsewhere
/// with this service resolved to null (its optional-dependency default).
/// </summary>
public class ManufacturingNotificationServiceTests
{
    private const int Creator    = 7;
    private const int Supervisor = 3;
    private const int Actor      = 9;

    private readonly Mock<INotificationService> _notifications = new();
    private readonly Mock<IOrgChartService>     _orgChart      = new();
    private readonly ManufacturingNotificationService _svc;

    private NotificationRequest? _sent;

    public ManufacturingNotificationServiceTests()
    {
        _notifications.Setup(n => n.TryCreateAsync(It.IsAny<NotificationRequest>()))
            .Callback<NotificationRequest>(r => _sent = r)
            .Returns(Task.CompletedTask);
        _svc = new ManufacturingNotificationService(_notifications.Object, _orgChart.Object);
    }

    private static ProductionOrder Order(int createdBy = Creator) => new()
    {
        UUID = Guid.NewGuid(), ProductionNumber = "PROD-2026-00001", PlannedQuantity = 100m,
        AcceptedQuantity = 40m, CreatedBy = createdBy
    };

    [Fact]
    public async Task Production_order_created_escalates_to_the_actors_supervisor_when_one_is_configured()
    {
        _orgChart.Setup(o => o.GetSupervisorAsync(Actor)).ReturnsAsync(new UserIdentity(Supervisor, "Lead"));

        await _svc.ProductionOrderCreatedAsync(Order(), "Bolt Kit", Actor);

        _sent!.UserId.Should().Be(Supervisor);
        _sent.Type.Should().Be("PROD_CREATED");
        _sent.Message.Should().Contain("PROD-2026-00001").And.Contain("Bolt Kit").And.Contain("100");
    }

    [Fact]
    public async Task Production_order_created_falls_back_to_the_actor_when_no_supervisor_is_configured()
    {
        _orgChart.Setup(o => o.GetSupervisorAsync(Actor)).ReturnsAsync((UserIdentity?)null);

        await _svc.ProductionOrderCreatedAsync(Order(), "Bolt Kit", Actor);

        _sent!.UserId.Should().Be(Actor);
    }

    [Fact]
    public async Task Quality_inspection_required_escalates_away_from_the_orders_own_creator()
    {
        // §18.4 — the person who raised the order cannot inspect it, so the notification is
        // deliberately aimed past them rather than at them.
        _orgChart.Setup(o => o.GetSupervisorAsync(Creator)).ReturnsAsync(new UserIdentity(Supervisor, "Lead"));

        await _svc.QualityInspectionRequiredAsync(Order());

        _sent!.UserId.Should().Be(Supervisor);
        _sent.Type.Should().Be("QI_REQUIRED");
    }

    [Fact]
    public async Task Production_order_ready_goes_straight_to_the_orders_own_creator_with_no_escalation()
    {
        await _svc.ProductionOrderReadyAsync(Order());

        _sent!.UserId.Should().Be(Creator);
        _sent.Type.Should().Be("PROD_READY");
        _orgChart.Verify(o => o.GetSupervisorAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Production_order_completed_names_the_accepted_quantity()
    {
        await _svc.ProductionOrderCompletedAsync(Order());

        _sent!.UserId.Should().Be(Creator);
        _sent.Type.Should().Be("PROD_COMPLETED");
        _sent.Message.Should().Contain("40");
    }

    [Fact]
    public async Task Shortage_alert_goes_to_the_orders_creator_and_requests_email()
    {
        await _svc.ShortageAlertAsync(Order(), "Steel Rod", 15m, "KG");

        _sent!.UserId.Should().Be(Creator);
        _sent.Type.Should().Be("PROD_SHORTAGE");
        _sent.SendEmail.Should().BeTrue();
        _sent.Message.Should().Contain("Steel Rod").And.Contain("15").And.Contain("KG");
    }

    [Fact]
    public async Task Quality_inspection_completed_is_attributed_to_the_inspector_but_sent_to_the_orders_creator()
    {
        var qi = new QualityInspection { UUID = Guid.NewGuid(), AcceptedQuantity = 38m, RejectedQuantity = 2m, InspectedBy = Actor };

        await _svc.QualityInspectionCompletedAsync(Order(), qi);

        _sent!.UserId.Should().Be(Creator);
        _sent.CreatedBy.Should().Be(Actor);
        _sent.Message.Should().Contain("38").And.Contain("2");
    }

    [Fact]
    public async Task Finished_goods_receipt_confirmed_names_product_and_warehouse()
    {
        var fgr = new FinishedGoodsReceipt { UUID = Guid.NewGuid(), FgrNumber = "FGR-2026-00001", TotalQuantity = 40m, ReceivedBy = Actor };

        await _svc.FinishedGoodsReceiptConfirmedAsync(Order(), fgr, "Bolt Kit", "Main Plant");

        _sent!.UserId.Should().Be(Creator);
        _sent.Message.Should().Contain("FGR-2026-00001").And.Contain("Bolt Kit").And.Contain("Main Plant");
    }

    [Fact]
    public async Task Supply_requirement_created_escalates_away_from_whoever_raised_it()
    {
        var sr = new SupplyRequirement { UUID = Guid.NewGuid(), SupplyNumber = "SR-2026-00001", QuantityRequired = 25m, CreatedBy = Actor };
        _orgChart.Setup(o => o.GetSupervisorAsync(Actor)).ReturnsAsync(new UserIdentity(Supervisor, "Lead"));

        await _svc.SupplyRequirementCreatedAsync(sr, "Steel Rod");

        _sent!.UserId.Should().Be(Supervisor);
        _sent.Type.Should().Be("SR_CREATED");
        _sent.Message.Should().Contain("SR-2026-00001").And.Contain("Steel Rod").And.Contain("25");
    }

    [Fact]
    public async Task Purchase_order_drafted_escalates_away_from_whoever_raised_the_shortage()
    {
        var sr = new SupplyRequirement { UUID = Guid.NewGuid(), SupplyNumber = "SR-2026-00002", CreatedBy = Actor };
        _orgChart.Setup(o => o.GetSupervisorAsync(Actor)).ReturnsAsync(new UserIdentity(Supervisor, "Lead"));

        await _svc.PurchaseOrderDraftCreatedAsync(sr, "PO-2026-00099", isNewPo: true);

        _sent!.UserId.Should().Be(Supervisor);
        _sent.Type.Should().Be("PO_DRAFT_CREATED");
        _sent.Message.Should().Contain("PO-2026-00099").And.Contain("SR-2026-00002");
    }

    [Fact]
    public async Task Purchase_order_appended_to_says_updated_not_drafted()
    {
        var sr = new SupplyRequirement { UUID = Guid.NewGuid(), SupplyNumber = "SR-2026-00003", CreatedBy = Actor };
        _orgChart.Setup(o => o.GetSupervisorAsync(Actor)).ReturnsAsync(new UserIdentity(Supervisor, "Lead"));

        await _svc.PurchaseOrderDraftCreatedAsync(sr, "PO-2026-00050", isNewPo: false);

        _sent!.Title.Should().Be("Purchase Order Updated");
        _sent.Message.Should().Contain("updated").And.Contain("PO-2026-00050");
    }

    [Fact]
    public async Task Allocation_completed_goes_to_the_orders_creator_with_no_escalation()
    {
        await _svc.AllocationCompletedAsync(Order(), "Steel Rod", 25m, "KG");

        _sent!.UserId.Should().Be(Creator);
        _orgChart.Verify(o => o.GetSupervisorAsync(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task Chained_production_order_created_notifies_the_parents_creator_not_the_childs()
    {
        var parent = Order(createdBy: Creator);
        var child  = Order(createdBy: Actor);
        child.ProductionNumber = "PROD-2026-00002";

        await _svc.ChainedProductionOrderCreatedAsync(parent, child, "Steel Bolt");

        _sent!.UserId.Should().Be(Creator);
        _sent.Message.Should().Contain(parent.ProductionNumber).And.Contain(child.ProductionNumber).And.Contain("Steel Bolt");
    }
}
