using System.Security.Claims;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Modules.Reports.Data;
using SMS.Modules.Reports.Repositories;
using SMS.Modules.Reports.Services;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Warehouse.Domain;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.WorkflowEngine.Services;
using Xunit;
using WarehouseDbContext = SMS.Modules.Warehouse.Data.WarehouseDbContext;

namespace SMS.Modules.Reports.Tests;

/// <summary>
/// The KPI dashboard: the original procurement / inventory / payables KPIs (on-time delivery fix, "no data" rather
/// than a failing zero) and the operations KPIs for sales, fulfilment, manufacturing and receivables.
/// </summary>
public class KpiDashboardTests
{
    private readonly string   _db    = Guid.NewGuid().ToString();
    private readonly StaticTenantContext _tenant = new();
    private readonly DateTime _today = DateTime.UtcNow.Date;
    private static int _n;

    private DbContextOptions<T> Options<T>() where T : DbContext => new DbContextOptionsBuilder<T>().UseInMemoryDatabase(_db).Options;

    private DemandDbContext    Demand()    => new(Options<DemandDbContext>(), _tenant);
    private WarehouseDbContext Warehouse() => new(Options<WarehouseDbContext>(), _tenant);
    private LogisticsDbContext Logistics() => new(Options<LogisticsDbContext>(), _tenant);
    private MaterialDbContext  Material()  => new(Options<MaterialDbContext>(), _tenant);
    private FinanceDbContext   Finance()   => new(Options<FinanceDbContext>(), _tenant);

    private ReportsRepository Repo() => new(
        db: new ReportsDbContext(Options<ReportsDbContext>(), _tenant), demand: Demand(), warehouse: Warehouse(),
        inventory: new InventoryDbContext(Options<InventoryDbContext>(), _tenant), finance: Finance(), logistics: Logistics(),
        suppliers: new SuppliersDbContext(Options<SuppliersDbContext>(), _tenant), material: Material(),
        userQuery: new Mock<IUserQueryService>().Object, timeline: new Mock<ITimelineService>().Object,
        supplierAccess: new UnrestrictedSupplierAccess());

    private OperationsKpiService Operations() => new(Demand(), Logistics(), Material(), Finance());

    private static ClaimsPrincipal UserWith(params string[] permissions) =>
        new(new ClaimsIdentity(permissions.Select(p => new Claim("permission", p)), "test"));

    private static string No(string prefix) => $"{prefix}-{Interlocked.Increment(ref _n):D6}";

    // ── The original KPIs ─────────────────────────────────────────────────────

    /// <summary>
    /// Supplier on-time delivery read a GRN date that was never set (always 0001-01-01), so every GRN looked early and
    /// the KPI could only ever show 100%. A late GRN now counts as late.
    /// </summary>
    [Fact]
    public async Task Supplier_on_time_delivery_counts_a_late_receipt_as_late()
    {
        var due = new DateTime(2026, 9, 10);
        Guid Po()
        {
            using var db = Demand();
            var po = new PurchaseOrder
            {
                UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), PoNumber = No("PO"), SupplierId = Guid.NewGuid(),
                SupplierName = "Acme", Status = "RECEIVED", DeliveryDate = due, CreatedDate = due.AddDays(-20)
            };
            db.PurchaseOrders.Add(po);
            db.SaveChanges();
            return po.UUID;
        }
        void Grn(Guid po, DateTime received)
        {
            using var db = Warehouse();
            db.Grns.Add(new Grn
            {
                UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), GrnNumber = No("GRN"), PoUuid = po, PoNumber = "PO",
                SupplierId = Guid.NewGuid(), SupplierName = "Acme", ReceivedAt = received, Status = "APPROVED"
            });
            db.SaveChanges();
        }

        Grn(Po(), due.AddDays(-1));   // early
        Grn(Po(), due);               // on the day
        Grn(Po(), due.AddDays(3));    // late
        Grn(Po(), due.AddDays(9));    // late

        var kpis = await Repo().GetKpiDashboardAsync();

        kpis.SupplierOnTimeDeliveryRate.Should().Be(50.0);
    }

    [Fact]
    public async Task With_nothing_to_measure_the_original_kpis_are_no_data_not_zero()
    {
        var kpis = await Repo().GetKpiDashboardAsync();

        kpis.PoCycleTimeDays.Should().BeNull();
        kpis.SupplierOnTimeDeliveryRate.Should().BeNull();
        kpis.PoFillRate.Should().BeNull();
        kpis.StockTurnoverRatio.Should().BeNull();
        kpis.InventoryAccuracy.Should().BeNull("with no stock records there is nothing to be accurate about — it used to say 100%");
        kpis.InvoiceProcessingTimeDays.Should().BeNull();
        kpis.ThreeWayMatchRate.Should().BeNull();
        kpis.BudgetVariancePercent.Should().BeNull();
        kpis.GrnRejectionRate.Should().BeNull();
        kpis.ReorderTriggerCount.Should().Be(0, "a count of zero is a real answer");
    }

    // ── Operations KPIs: permissions ──────────────────────────────────────────

    [Fact]
    public async Task Each_operations_section_needs_its_own_view_permission()
    {
        var none = await Operations().GetAsync(UserWith());
        none.Sales.Should().BeNull();
        none.Fulfilment.Should().BeNull();
        none.Manufacturing.Should().BeNull();
        none.Receivables.Should().BeNull();

        var some = await Operations().GetAsync(UserWith(PermissionCodes.PROD_VIEW));
        some.Manufacturing.Should().NotBeNull();
        some.Sales.Should().BeNull();
    }

    [Fact]
    public async Task An_empty_system_has_no_data_with_a_basis_of_zero()
    {
        var k = await Operations().GetAsync(UserWith(
            PermissionCodes.SALE_ORDER_VIEW, PermissionCodes.SALE_QUOTATION_VIEW, PermissionCodes.SALE_INQUIRY_VIEW,
            PermissionCodes.DELIVERY_VIEW, PermissionCodes.PROD_VIEW, PermissionCodes.SALES_INVOICE_VIEW));

        k.Sales!.QuoteWinRate.Value.Should().BeNull();
        k.Sales.QuoteWinRate.Basis.Should().Be(0);
        k.Fulfilment!.OnTimeShipmentRate.Value.Should().BeNull();
        k.Manufacturing!.FirstPassYield.Value.Should().BeNull();
        k.Receivables!.DaysSalesOutstanding.Value.Should().BeNull();
        k.WindowDays.Should().Be(90);
    }

    // ── Operations KPIs: values ───────────────────────────────────────────────

    [Fact]
    public async Task Sales_kpis_measure_wins_conversions_cancellations_and_late_orders()
    {
        using (var db = Demand())
        {
            foreach (var status in new[] { "ACCEPTED", "CONVERTED", "REJECTED", "EXPIRED", "SENT", "DRAFT" })   // 2 won of 4 decided
                db.SaleQuotations.Add(new SaleQuotation
                {
                    UUID = Guid.NewGuid(), QuotationNumber = No("SQ"), PartnerId = Guid.NewGuid(), CurrencyId = Guid.NewGuid(),
                    ValidFrom = _today, ValidTo = _today.AddDays(30), Status = status, CreatedDate = _today.AddDays(-5)
                });
            db.SaleQuotations.Add(new SaleQuotation   // outside the window
            {
                UUID = Guid.NewGuid(), QuotationNumber = No("SQ"), PartnerId = Guid.NewGuid(), CurrencyId = Guid.NewGuid(),
                ValidFrom = _today, ValidTo = _today, Status = "REJECTED", CreatedDate = _today.AddDays(-200)
            });

            foreach (var status in new[] { "QUOTED", "QUOTED", "QUOTED", "DECLINED", "RECEIVED" })            // 3 of 4 decided
                db.SaleInquiries.Add(new SaleInquiry
                {
                    UUID = Guid.NewGuid(), InquiryNumber = No("INQ"), PartnerId = Guid.NewGuid(), ReceivedDate = _today.AddDays(-3), Status = status
                });

            SaleOrder Order(string status, DateTime? expected = null) => new()
            {
                UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), SoNumber = No("SO"), PartnerId = Guid.NewGuid(), CurrencyId = Guid.NewGuid(),
                OrderDate = _today.AddDays(-10), ExpectedDeliveryDate = expected, Status = status, DeliveryMode = "SHIP", CreatedBy = 1
            };
            db.SaleOrders.AddRange(
                Order("CONFIRMED", _today.AddDays(-2)),           // open, late
                Order("CONFIRMED", _today.AddDays(5)),            // open, on time
                Order("PARTIALLY_FULFILLED", _today.AddDays(-1)), // open, late
                Order("FULFILLED"), Order("CANCELLED"),
                Order("DRAFT"));                                  // never placed: not in the cancellation basis
            db.SaveChanges();
        }

        var sales = (await Operations().GetAsync(UserWith(
            PermissionCodes.SALE_ORDER_VIEW, PermissionCodes.SALE_QUOTATION_VIEW, PermissionCodes.SALE_INQUIRY_VIEW))).Sales!;

        sales.QuoteWinRate.Value.Should().Be(50.0);
        sales.QuoteWinRate.Basis.Should().Be(4);
        sales.InquiryConversionRate.Value.Should().Be(75.0);
        sales.OrderCancellationRate.Value.Should().Be(20.0);   // 1 of 5 placed
        sales.LateOpenOrderRate.Value.Should().Be(66.7);        // 2 of 3 open
    }

    [Fact]
    public async Task Fulfilment_kpis_measure_on_time_in_full_and_order_to_ship()
    {
        Guid soUuid;
        using (var db = Demand())
        {
            var so = new SaleOrder
            {
                UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), SoNumber = No("SO"), PartnerId = Guid.NewGuid(), CurrencyId = Guid.NewGuid(),
                OrderDate = _today.AddDays(-6), Status = "FULFILLED", DeliveryMode = "SHIP", CreatedBy = 1
            };
            db.SaleOrders.Add(so);
            db.SaveChanges();
            soUuid = so.UUID;
        }

        using (var db = Logistics())
        {
            DeliveryOrder D(string status, DateTime issued, DateTime? promised, decimal ordered, decimal delivered, Guid? so = null)
            {
                var d = new DeliveryOrder
                {
                    UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), DeliveryNumber = No("DLV"), Direction = "OUTBOUND",
                    SourceType = so is null ? "MANUAL" : "SALE_ORDER", SaleOrderUuid = so, Status = status,
                    GoodsIssuedAt = issued, PromisedDate = promised, CreatedBy = 1, CreatedDate = issued
                };
                d.Lines.Add(new DeliveryOrderLine { UUID = Guid.NewGuid(), LineNo = 1, ItemDescription = "x", QtyOrdered = ordered, QtyDelivered = delivered });
                return d;
            }
            db.DeliveryOrders.AddRange(
                D("DELIVERED", _today.AddDays(-2), _today.AddDays(-1), 10, 10, soUuid),  // on time, in full, 4 days after order
                D("DELIVERED", _today.AddDays(-1), _today.AddDays(-3), 10, 7),           // late, short
                D("IN_TRANSIT", _today, _today, 5, 0),                                   // on time, not yet delivered
                D("DELIVERED", _today.AddDays(-1), null, 4, 4));                          // no date: not in on-time basis
            db.SaveChanges();
        }

        var f = (await Operations().GetAsync(UserWith(PermissionCodes.DELIVERY_VIEW))).Fulfilment!;

        f.OnTimeShipmentRate.Value.Should().Be(66.7);   // 2 of 3 dated
        f.OnTimeShipmentRate.Basis.Should().Be(3);
        f.InFullRate.Value.Should().Be(66.7);           // 2 of 3 completed
        f.OrderToShipDays.Value.Should().Be(4.0);
        f.OrderToShipDays.Basis.Should().Be(1);
    }

    [Fact]
    public async Task Manufacturing_kpis_measure_on_time_attainment_yield_and_cycle_time()
    {
        using (var db = Material())
        {
            ProductionOrder P(string status, DateTime required, DateTime start, DateTime end, decimal planned, decimal produced, decimal accepted) => new()
            {
                UUID = Guid.NewGuid(), ProductionNumber = No("PRD"), ProductUuid = Guid.NewGuid(), ProductVariantUuid = Guid.NewGuid(),
                WarehouseUuid = Guid.NewGuid(), Status = status, RequiredDate = required, ActualStartDate = start, ActualEndDate = end,
                PlannedQuantity = planned, ProducedQuantity = produced, AcceptedQuantity = accepted, CreatedBy = 1
            };
            db.ProductionOrders.AddRange(
                P("COMPLETED", _today.AddDays(-1), _today.AddDays(-5), _today.AddDays(-2), 100, 100, 95),  // on time, 3 days
                P("CLOSED",    _today.AddDays(-4), _today.AddDays(-9), _today.AddDays(-4), 100,  80, 80),  // on time (same day), 5 days
                P("COMPLETED", _today.AddDays(-6), _today.AddDays(-8), _today.AddDays(-1),  50,  50, 45),  // late, 7 days
                P("IN_PROGRESS", _today, _today.AddDays(-1), _today, 10, 0, 0));                            // not finished
            db.SaveChanges();
        }

        var m = (await Operations().GetAsync(UserWith(PermissionCodes.PROD_VIEW))).Manufacturing!;

        m.OnTimeCompletionRate.Value.Should().Be(66.7);
        m.PlanAttainment.Value.Should().Be(92.0);        // 230 / 250
        m.FirstPassYield.Value.Should().Be(95.7);        // 220 / 230
        m.CycleTimeDays.Value.Should().Be(5.0);
    }

    [Fact]
    public async Task Receivables_kpis_convert_to_base_currency_and_set_aside_what_cannot_be()
    {
        using (var db = Finance())
        {
            SalesInvoice I(string status, decimal total, decimal balance, string currency, decimal? rate, DateTime invoiced, DateTime due) => new()
            {
                UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), InvoiceNumber = No("SINV"), SaleOrderUuid = Guid.NewGuid(), SaleOrderNumber = "SO",
                PartnerId = Guid.NewGuid(), PartnerName = "x", InvoiceDate = invoiced, DueDate = due, GrandTotal = total, BalanceDue = balance,
                Status = status, CurrencyCode = currency, BaseCurrencyCode = "PKR", ExchangeRate = rate,
                BaseGrandTotal = rate is { } r ? total * r : null, CreatedBy = 1, CreatedDate = invoiced
            };
            db.SalesInvoices.AddRange(
                I("ISSUED", 9000m, 9000m, "PKR", 1m, _today.AddDays(-40), _today.AddDays(-10)),   // overdue 9000
                I("PAID",   9000m, 0m,    "PKR", 1m, _today.AddDays(-60), _today.AddDays(-30)),   // billed, settled
                I("ISSUED", 10m,   10m,   "USD", 300m, _today.AddDays(-5), _today.AddDays(25)),   // 3000 base, not due
                I("ISSUED", 50m,   50m,   "EUR", null, _today.AddDays(-5), _today.AddDays(25)));  // no rate: set aside
            db.SaveChanges();
        }

        var r = (await Operations().GetAsync(UserWith(PermissionCodes.SALES_INVOICE_VIEW))).Receivables!;

        // Owed 12,000 base; billed in 90 days 9,000 + 9,000 + 3,000 = 21,000 (the EUR one has no base value) → 12/21 × 90.
        r.DaysSalesOutstanding.Value.Should().Be(51.4);
        r.OverdueRate.Value.Should().Be(75.0);          // 9,000 of 12,000
        r.UnconvertedInvoices.Should().Be(1);
    }

    // ── Real SQL Server ───────────────────────────────────────────────────────

    /// <summary>Every operations KPI query translates and runs on SQL Server. Skipped without LocalDB.</summary>
    [Fact]
    public async Task Every_operations_query_runs_on_sql_server()
    {
        if (!SqlServerHarness.IsAvailable) return;

        var database = $"SMS_KpiTest_{Guid.NewGuid():N}";
        var master   = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(SqlServerHarness.MasterConnectionString) { InitialCatalog = "master" };
        var conn     = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(master.ConnectionString) { InitialCatalog = database }.ConnectionString;

        async Task Exec(string sql)
        {
            await using var c = new Microsoft.Data.SqlClient.SqlConnection(master.ConnectionString);
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }
        T Sql<T>(Func<DbContextOptions<T>, ITenantContext, T> make) where T : DbContext =>
            make(new DbContextOptionsBuilder<T>().UseSqlServer(conn).Options, _tenant);

        await Exec($"CREATE DATABASE [{database}]");
        try
        {
            var demand    = Sql<DemandDbContext>((o, t) => new(o, t));
            var logistics = Sql<LogisticsDbContext>((o, t) => new(o, t));
            var material  = Sql<MaterialDbContext>((o, t) => new(o, t));
            var finance   = Sql<FinanceDbContext>((o, t) => new(o, t));
            DbContext[] all = [demand, logistics, material, finance];
            foreach (var db in all)
                await Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions
                    .GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator>(db).CreateTablesAsync();

            var so = new SaleOrder
            {
                UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), OrganizationId = _tenant.OrganizationId, SoNumber = "SO-1", PartnerId = Guid.NewGuid(),
                CurrencyId = Guid.NewGuid(), OrderDate = _today.AddDays(-3), Status = "FULFILLED", DeliveryMode = "SHIP", CreatedBy = 1
            };
            demand.SaleOrders.Add(so);
            await demand.SaveChangesAsync();
            var delivery = new DeliveryOrder
            {
                UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), OrganizationId = _tenant.OrganizationId, DeliveryNumber = "DLV-1",
                Direction = "OUTBOUND", SourceType = "SALE_ORDER", SaleOrderUuid = so.UUID, Status = "DELIVERED",
                GoodsIssuedAt = _today.AddDays(-1), PromisedDate = _today, CreatedBy = 1, CreatedDate = _today
            };
            delivery.Lines.Add(new DeliveryOrderLine { UUID = Guid.NewGuid(), OrganizationId = _tenant.OrganizationId, LineNo = 1, ItemDescription = "x", QtyOrdered = 2, QtyDelivered = 2 });
            logistics.DeliveryOrders.Add(delivery);
            await logistics.SaveChangesAsync();

            var k = await new OperationsKpiService(demand, logistics, material, finance).GetAsync(UserWith(
                PermissionCodes.SALE_ORDER_VIEW, PermissionCodes.SALE_QUOTATION_VIEW, PermissionCodes.SALE_INQUIRY_VIEW,
                PermissionCodes.DELIVERY_VIEW, PermissionCodes.PROD_VIEW, PermissionCodes.SALES_INVOICE_VIEW));

            k.Fulfilment!.OnTimeShipmentRate.Value.Should().Be(100.0);
            k.Fulfilment.InFullRate.Value.Should().Be(100.0);
            k.Fulfilment.OrderToShipDays.Value.Should().Be(2.0);
            k.Manufacturing!.CycleTimeDays.Value.Should().BeNull();
            k.Receivables!.DaysSalesOutstanding.Value.Should().BeNull();

            foreach (var db in all) await db.DisposeAsync();
        }
        finally
        {
            Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
            await Exec($"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
        }
    }
}
