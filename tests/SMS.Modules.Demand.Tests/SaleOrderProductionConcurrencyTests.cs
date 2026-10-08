using FluentAssertions;
using Hangfire;
using Microsoft.Data.SqlClient;
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
/// A34 D-17 / D-22 / R-6 / R-8 on real SQL Server (LocalDB), where the per-order lock is a real <c>sp_getapplock</c>:
/// production orders are created while the order's lock is held (after confirm's commit) and planned after it is released;
/// and a cancel racing the "Create production orders" button never leaves a live production order on a cancelled order.
/// </summary>
public sealed class SaleOrderProductionConcurrencyTests : IAsyncLifetime
{
    private const string Server = @"Server=(localdb)\MSSQLLocalDB;Trusted_Connection=True;TrustServerCertificate=True;";
    private readonly string _connection = $"{Server}Database=A34_ProdRace_{Guid.NewGuid():N};";
    private readonly Guid _org = Guid.NewGuid();
    private readonly A34Routes _routes = new();
    private readonly A34Tenants _tenants = new();
    private readonly FakeReadiness _readiness = new();
    private readonly FakeProduction _production = new();
    private FulfillmentRouteSummary _mfg = null!;
    private Guid _variant;

    private DemandDbContext NewContext() => new(
        new DbContextOptionsBuilder<DemandDbContext>().UseSqlServer(_connection).Options,
        new StaticTenantContext { OrganizationId = _org });

    public async Task InitializeAsync()
    {
        await using var db = NewContext();
        await db.Database.EnsureCreatedAsync();
        _tenants.Enable(_org, A34.Demand, A34.Logistics, A34.Inventory, A34.Manufacturing);
        _mfg = _routes.Add(_org, "MFG_PICK_SHIP", FulfillmentRouteCategory.Manufacture, "PICK", "GOODS_ISSUE", "SHIP");
        _routes.Defaults[_org] = new FulfillmentRouteDefaults(_mfg, null);
        _variant = Guid.NewGuid();
        _routes.VariantRoutes[(_org, _variant)] = _mfg.Uuid;
        _readiness.Ready(_org, _variant);
    }

    public async Task DisposeAsync()
    {
        await using var db = NewContext();
        await db.Database.EnsureDeletedAsync();
    }

    private SaleOrderService Service(DemandDbContext db)
    {
        var tenant = new StaticTenantContext { OrganizationId = _org };
        var stock = new Mock<IStockReservationService>();
        stock.Setup(s => s.GetAvailableAsync(It.IsAny<IReadOnlyList<Guid>>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Guid> ids, Guid? _, CancellationToken _) =>
                (IReadOnlyList<VariantAvailability>)ids.Select(v => new VariantAvailability(v, Guid.Empty, "Central", 0m)).ToList());
        stock.Setup(s => s.GetBySourceAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<ReservationSummary>());
        stock.Setup(s => s.ReleaseBySourceAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);
        var config = new Mock<ISaleOrderConfigService>();
        config.Setup(c => c.GetConfigAsync()).ReturnsAsync(new SaleOrderConfigModel { ReservationTtlHours = 72 });
        var allocation = new Mock<IAllocationEngine>();
        allocation.Setup(a => a.GetDemandsAsync(It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<Guid?>(), It.IsAny<bool>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DemandAllocationSummary>());
        return new SaleOrderService(
            db, tenant, Mock.Of<IOrganizationCurrencyService>(), Mock.Of<IDocumentNumberGenerator>(), Mock.Of<IPricingService>(),
            stock.Object, Mock.Of<ITimelineService>(), Mock.Of<IBackgroundJobClient>(),
            new AvailabilityCheckService(db, stock.Object, config.Object), Mock.Of<IPurchaseOrderService>(), Mock.Of<ISaleOrderEmailService>(),
            routes: new EffectiveRouteResolver(_routes, _routes, _tenants, _readiness),
            production: _production, leadTimes: new FakeLeadTimes(), allocation: allocation.Object, tenants: _tenants);
    }

    private async Task<Guid> SeedDraftAsync()
    {
        await using var seed = NewContext();
        var order = new SaleOrder
        {
            SoNumber = $"SO-A34-{Guid.NewGuid():N}"[..20], PartnerId = Guid.NewGuid(), OrderDate = DateTime.UtcNow.Date, CurrencyId = Guid.NewGuid(),
            Status = "DRAFT", DeliveryMode = "SHIP", ShippingAddressId = Guid.NewGuid(), CreatedBy = 1,
            Lines = { new SaleOrderLine { VariantUuid = _variant, Quantity = 10m, UnitPrice = 1m, LineTotal = 10m, Status = "OPEN" } }
        };
        seed.SaleOrders.Add(order);
        await seed.SaveChangesAsync();
        return order.UUID;
    }

    /// <summary>Tries the order's lock from another connection without waiting: true = someone holds it.</summary>
    private async Task<bool> LockIsHeldAsync(Guid orderUuid)
    {
        await using var conn = new SqlConnection(_connection);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DECLARE @r int; EXEC @r = sp_getapplock @Resource = @res, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; " +
                          "IF @r >= 0 EXEC sp_releaseapplock @Resource = @res, @LockOwner = 'Session'; SELECT @r;";
        cmd.Parameters.AddWithValue("@res", SaleOrderLocks.HoldsResource(orderUuid));
        return (int)(await cmd.ExecuteScalarAsync())! < 0;
    }

    [Fact]
    public async Task D_17_orders_are_created_under_the_order_lock_after_the_confirm_commits_and_planned_after_it()
    {
        var so = await SeedDraftAsync();
        bool? heldAtCreate = null, heldAtPlan = null;
        string? statusAtCreate = null;
        _production.OnCreate = async () =>
        {
            heldAtCreate = await LockIsHeldAsync(so);
            await using var fresh = NewContext();
            statusAtCreate = await fresh.SaleOrders.Where(o => o.UUID == so).Select(o => o.Status).SingleAsync();
        };
        _production.OnPlan = async () => heldAtPlan = await LockIsHeldAsync(so);

        await using var db = NewContext();
        var result = (await Service(db).ConfirmWithResultAsync(so, 1))!;

        result.ProductionCreationFailed.Should().BeFalse();
        result.ProductionOrders.Should().ContainSingle().Which.Created.Should().BeTrue();
        heldAtCreate.Should().BeTrue("CreateDraftsAsync runs inside the order's lock");
        statusAtCreate.Should().Be("CONFIRMED", "the confirm had committed before");
        heldAtPlan.Should().BeFalse("planning runs after the lock is released");
        await using var check = NewContext();
        (await check.SaleOrders.SingleAsync(o => o.UUID == so)).ProductionCreationPendingSince.Should().BeNull();
    }

    [Fact]
    public async Task R_8_a_cancel_racing_the_create_button_never_leaves_a_live_production_order()
    {
        var so = await SeedDraftAsync();
        _production.ThrowOnCreate = new InvalidOperationException("down at confirm");
        await using (var db = NewContext())
            await Service(db).ConfirmWithResultAsync(so, 1);
        _production.ThrowOnCreate = null;
        _production.OnCreate = () => Task.Delay(300);

        async Task<object?> Create()
        {
            await using var db = NewContext();
            try { return await Service(db).CreateProductionOrdersAsync(so, 1); }
            catch (BadRequestException ex) { return ex; }
        }
        async Task<object?> Cancel()
        {
            await Task.Delay(50);
            await using var db = NewContext();
            return await Service(db).CancelWithResultAsync(so, 1, "race");
        }

        await Task.WhenAll(Create(), Cancel());

        _production.Pos.Should().OnlyContain(p => p.Status == "CANCELLED", "whichever ran second saw the other's result");
        await using var check = NewContext();
        var saved = await check.SaleOrders.SingleAsync(o => o.UUID == so);
        saved.Status.Should().Be("CANCELLED");
        saved.ProductionCreationPendingSince.Should().BeNull();
    }
}
