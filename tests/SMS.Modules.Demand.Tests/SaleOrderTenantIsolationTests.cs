using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.WorkflowEngine.Services;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>
/// A32 PF-05 (QA finding) — every sale order read and write filters on the caller's own organization explicitly. The EF
/// tenant filter is off for a super admin, so without that another organization's order was readable, editable,
/// confirmable and cancellable by one (contract §1: another organization's record is a 404, super admin included).
/// </summary>
public class SaleOrderTenantIsolationTests
{
    private const int User = 3;

    private sealed record Harness(DemandDbContext Db, SaleOrderService Service, Mock<IStockReservationService> Stock,
        Mock<IAvailabilityCheckService> Availability, Mock<ITimelineService> Timeline);

    private static Harness NewHarness(string dbName, Guid org, bool superAdmin)
    {
        var tenant = new StaticTenantContext { OrganizationId = org, IsSuperAdmin = superAdmin };
        var db = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(dbName).Options, tenant);
        var stock = new Mock<IStockReservationService>();
        var availability = new Mock<IAvailabilityCheckService>();
        var timeline = new Mock<ITimelineService>();
        var service = new SaleOrderService(
            db, tenant, Mock.Of<IOrganizationCurrencyService>(), Mock.Of<IDocumentNumberGenerator>(), Mock.Of<IPricingService>(),
            stock.Object, timeline.Object, Mock.Of<IBackgroundJobClient>(), availability.Object,
            Mock.Of<IPurchaseOrderService>(), Mock.Of<ISaleOrderEmailService>());
        return new Harness(db, service, stock, availability, timeline);
    }

    /// <summary>One organization's DRAFT order, and a super admin of another organization looking at the same database.</summary>
    private static async Task<(Harness Admin, Harness Owner, SaleOrder Order)> SetupAsync()
    {
        var dbName = Guid.NewGuid().ToString();
        var owner = NewHarness(dbName, Guid.NewGuid(), superAdmin: false);
        var order = new SaleOrder
        {
            SoNumber = "SO-2026-00001", PartnerId = Guid.NewGuid(), OrderDate = DateTime.UtcNow.Date, CurrencyId = Guid.NewGuid(),
            Status = "DRAFT", DeliveryMode = "SELF_PICKUP", Notes = "theirs", CreatedBy = User, CustomerPoReference = "PO-THEIRS",
            Lines = { new SaleOrderLine { VariantUuid = Guid.NewGuid(), Quantity = 1m, UnitPrice = 5m, LineTotal = 5m, Status = "OPEN" } }
        };
        owner.Db.SaleOrders.Add(order);
        await owner.Db.SaveChangesAsync();
        owner.Db.ChangeTracker.Clear();
        return (NewHarness(dbName, Guid.NewGuid(), superAdmin: true), owner, order);
    }

    [Fact]
    public async Task A_super_admin_cannot_read_another_organizations_order_its_timeline_or_its_availability()
    {
        var (admin, _, order) = await SetupAsync();

        (await admin.Service.GetByIdAsync(order.UUID)).Should().BeNull();
        (await admin.Service.GetTimelineAsync(order.UUID)).Should().BeNull();
        (await admin.Service.GetAvailabilityAsync(order.UUID)).Should().BeNull();
        admin.Timeline.Verify(t => t.GetTimelineDetailAsync(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task A_super_admins_list_holds_only_their_own_organizations_orders()
    {
        var (admin, _, _) = await SetupAsync();

        (await admin.Service.GetListAsync(new SaleOrderListFilter())).TotalRecords.Should().Be(0);
        (await admin.Service.GetListAsync(new SaleOrderListFilter { Search = "PO-THEIRS" })).Data.Should().BeEmpty();
    }

    [Fact]
    public async Task A_super_admin_cannot_edit_confirm_or_cancel_another_organizations_order()
    {
        var (admin, owner, order) = await SetupAsync();

        (await admin.Service.UpdateAsync(order.UUID, new UpdateSaleOrderRequest
        {
            CurrencyId = Guid.NewGuid(), DeliveryMode = "SELF_PICKUP", Notes = "hijacked",
            Lines = [new CreateSaleOrderLineRequest { VariantUuid = Guid.NewGuid(), Quantity = 9 }]
        }, User)).Should().BeFalse();
        (await admin.Service.ConfirmAsync(order.UUID, User)).Should().BeFalse();
        (await admin.Service.CancelAsync(order.UUID, User, "hijacked")).Should().BeFalse();

        admin.Availability.Verify(a => a.CheckAndReserveAsync(It.IsAny<Guid>(), It.IsAny<int>()), Times.Never);
        admin.Stock.Verify(s => s.ReleaseBySourceAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        var stored = await owner.Db.SaleOrders.AsNoTracking().Include(o => o.Lines).SingleAsync();
        stored.Status.Should().Be("DRAFT");
        stored.Notes.Should().Be("theirs");
        stored.Lines.Single().Quantity.Should().Be(1m);
    }

    [Fact]
    public async Task The_confirm_time_availability_check_only_reserves_for_the_callers_own_order()
    {
        var (admin, _, order) = await SetupAsync();
        var config = new Mock<ISaleOrderConfigService>();
        config.Setup(c => c.GetConfigAsync()).ReturnsAsync(new SaleOrderConfigModel { ReservationTtlHours = 72 });
        var check = new AvailabilityCheckService(admin.Db, admin.Stock.Object, config.Object);

        var act = () => check.CheckAndReserveAsync(order.UUID, User);

        await act.Should().ThrowAsync<NotFoundException>();
        admin.Stock.Verify(s => s.ReserveAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyList<ReservationRequest>>(),
            It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task The_owner_still_sees_and_works_its_own_order()
    {
        var (_, owner, order) = await SetupAsync();

        (await owner.Service.GetByIdAsync(order.UUID)).Should().NotBeNull();
        (await owner.Service.GetListAsync(new SaleOrderListFilter())).TotalRecords.Should().Be(1);
        (await owner.Service.CancelAsync(order.UUID, User, null)).Should().BeTrue();
    }
}
