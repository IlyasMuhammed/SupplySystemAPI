using System.Security.Claims;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Logistics.Domain;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Modules.Reports.Models;
using SMS.Modules.Reports.Services;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Warehouse.Data;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Reports.Tests;

/// <summary>
/// The home dashboard's one-call summary: what each section counts, that a section the user may not see is left out
/// (null, never a misleading zero), and that money is kept per currency. In-memory, over one shared store per test.
/// </summary>
public class DashboardServiceTests
{
    private static readonly Guid Org   = Guid.Parse("d0000000-0000-0000-0000-000000000001");
    private static readonly Guid Other = Guid.Parse("d0000000-0000-0000-0000-000000000002");

    private readonly string _db    = Guid.NewGuid().ToString();
    private readonly DateTime _today = DateTime.UtcNow.Date;

    private T Ctx<T>(Func<DbContextOptions<T>, ITenantContext, T> make, Guid? org = null) where T : DbContext =>
        make(new DbContextOptionsBuilder<T>().UseInMemoryDatabase(_db).Options, new StaticTenantContext { OrganizationId = org ?? Org });

    private DemandDbContext    Demand(Guid? org = null)    => Ctx<DemandDbContext>((o, t) => new(o, t), org);
    private LogisticsDbContext Logistics(Guid? org = null) => Ctx<LogisticsDbContext>((o, t) => new(o, t), org);
    private MaterialDbContext  Material(Guid? org = null)  => Ctx<MaterialDbContext>((o, t) => new(o, t), org);
    private FinanceDbContext   Finance(Guid? org = null)   => Ctx<FinanceDbContext>((o, t) => new(o, t), org);

    private DashboardService Service() => new(
        Demand(), Ctx<WarehouseDbContext>((o, t) => new(o, t)), Ctx<SuppliersDbContext>((o, t) => new(o, t)),
        Logistics(), Material(), Finance(), Ctx<InventoryDbContext>((o, t) => new(o, t)));

    private static ClaimsPrincipal UserWith(params string[] permissions) =>
        new(new ClaimsIdentity(permissions.Select(p => new Claim("permission", p)), "test"));

    // ── Seed helpers ──────────────────────────────────────────────────────────

    private void SaleOrder(string status, DateTime? expected = null, bool deleted = false, Guid? org = null, DateTime? orderDate = null)
    {
        using var db = Demand(org);
        db.SaleOrders.Add(new SaleOrder
        {
            UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), OrganizationId = org ?? Org, SoNumber = $"SO-{Guid.NewGuid():N}"[..12],
            PartnerId = Guid.NewGuid(), OrderDate = orderDate ?? _today, ExpectedDeliveryDate = expected, CurrencyId = Guid.NewGuid(),
            Status = status, DeliveryMode = "SHIP", IsDeleted = deleted, CreatedBy = 1
        });
        db.SaveChanges();
    }

    private void Delivery(string status, string direction = "OUTBOUND", DateTime? promised = null, DateTime? issuedAt = null)
    {
        using var db = Logistics();
        db.DeliveryOrders.Add(new DeliveryOrder
        {
            UUID = Guid.NewGuid(), OrganizationId = Org, TraceId = Guid.NewGuid(), DeliveryNumber = $"DLV-{Guid.NewGuid():N}"[..12],
            Direction = direction, SourceType = "MANUAL", Status = status, PromisedDate = promised, GoodsIssuedAt = issuedAt,
            CreatedBy = 1, CreatedDate = _today
        });
        db.SaveChanges();
    }

    private void Production(string status, DateTime required, string sourceType = "MANUAL")
    {
        using var db = Material();
        db.ProductionOrders.Add(new ProductionOrder
        {
            UUID = Guid.NewGuid(), OrganizationId = Org, ProductionNumber = $"PRD-{Guid.NewGuid():N}"[..12], ProductUuid = Guid.NewGuid(),
            ProductVariantUuid = Guid.NewGuid(), WarehouseUuid = Guid.NewGuid(), PlannedQuantity = 1, Status = status,
            RequiredDate = required, SourceType = sourceType, CreatedAt = _today, UpdatedAt = _today, CreatedBy = 1
        });
        db.SaveChanges();
    }

    private void SalesInvoice(string status, decimal balance, string currency, DateTime due)
    {
        using var db = Finance();
        db.SalesInvoices.Add(new SalesInvoice
        {
            UUID = Guid.NewGuid(), OrganizationId = Org, TraceId = Guid.NewGuid(), InvoiceNumber = $"SINV-{Guid.NewGuid():N}"[..14],
            SaleOrderUuid = Guid.NewGuid(), SaleOrderNumber = "SO-1", PartnerId = Guid.NewGuid(), PartnerName = "x",
            InvoiceDate = due.AddDays(-30), DueDate = due, GrandTotal = balance, BalanceDue = balance, Status = status,
            CurrencyCode = currency, CreatedBy = 1, CreatedDate = _today
        });
        db.SaveChanges();
    }

    // ── Permissions ───────────────────────────────────────────────────────────

    [Fact]
    public async Task A_user_with_no_view_permissions_gets_no_sections_rather_than_zeros()
    {
        SaleOrder("CONFIRMED");
        Delivery("RELEASED");

        var summary = await Service().GetSummaryAsync(UserWith());

        summary.Procurement.Should().BeNull();
        summary.Receiving.Should().BeNull();
        summary.Sales.Should().BeNull();
        summary.Deliveries.Should().BeNull();
        summary.Production.Should().BeNull();
        summary.Receivables.Should().BeNull();
        summary.Payables.Should().BeNull();
        summary.Material.Should().BeNull();
        summary.Inventory.Should().BeNull();
        summary.Attention.Should().BeEmpty();
    }

    [Fact]
    public async Task Each_section_appears_only_with_its_own_permission()
    {
        var summary = await Service().GetSummaryAsync(UserWith(PermissionCodes.DELIVERY_VIEW));

        summary.Deliveries.Should().NotBeNull();
        summary.Sales.Should().BeNull();
        summary.Production.Should().BeNull();
    }

    [Fact]
    public async Task Sales_lists_only_the_document_types_the_user_may_see()
    {
        var summary = await Service().GetSummaryAsync(UserWith(PermissionCodes.SALE_QUOTATION_VIEW));

        summary.Sales.Should().NotBeNull();
        summary.Sales!.Quotations.Should().NotBeNull();
        summary.Sales.SaleOrders.Should().BeNull("sale orders need SALE_ORDER_VIEW");
        summary.Sales.Inquiries.Should().BeNull();
    }

    // ── Counts ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Sale_orders_are_counted_by_status_and_late_ones_are_flagged()
    {
        SaleOrder("CONFIRMED", expected: _today.AddDays(-3));          // late
        SaleOrder("PARTIALLY_FULFILLED", expected: _today.AddDays(-1)); // late
        SaleOrder("CONFIRMED", expected: _today.AddDays(5));           // on time
        SaleOrder("FULFILLED", expected: _today.AddDays(-10));         // done, never late
        SaleOrder("DRAFT", expected: _today.AddDays(-10));             // not promised yet
        SaleOrder("CONFIRMED", deleted: true);                         // deleted
        SaleOrder("CONFIRMED", org: Other);                            // another organization

        var sales = (await Service().GetSummaryAsync(UserWith(PermissionCodes.SALE_ORDER_VIEW))).Sales!;

        sales.SaleOrders.Should().BeEquivalentTo(new[]
        {
            new StatusCount { Status = "CONFIRMED", Count = 2 },
            new StatusCount { Status = "PARTIALLY_FULFILLED", Count = 1 },
            new StatusCount { Status = "FULFILLED", Count = 1 },
            new StatusCount { Status = "DRAFT", Count = 1 }
        });
        sales.LateSaleOrders.Should().Be(2);
    }

    [Fact]
    public async Task Deliveries_split_outbound_from_inbound_and_count_late_and_shipped()
    {
        Delivery("PICKING", promised: _today.AddDays(-2));           // late
        Delivery("STAGED", promised: _today.AddDays(2));             // on time
        Delivery("IN_TRANSIT", promised: _today.AddDays(-5), issuedAt: DateTime.UtcNow); // already left: not late, shipped
        Delivery("PENDING_APPROVAL");
        Delivery("RELEASED", direction: "INBOUND");                  // an ASN still expected
        Delivery("DELIVERED", direction: "INBOUND");

        var d = (await Service().GetSummaryAsync(UserWith(PermissionCodes.DELIVERY_VIEW))).Deliveries!;

        d.Outbound.Sum(s => s.Count).Should().Be(4);
        d.InboundExpected.Should().Be(1);
        d.Late.Should().Be(1);
        d.ShippedThisMonth.Should().Be(1);
    }

    [Fact]
    public async Task Production_counts_late_and_open_make_to_order_orders()
    {
        Production("IN_PROGRESS", _today.AddDays(-1), ProductionSourceType.SalesOrder); // late, MTO
        Production("PLANNED", _today.AddDays(4), ProductionSourceType.SalesOrder);      // MTO
        Production("COMPLETED", _today.AddDays(-9), ProductionSourceType.SalesOrder);   // done
        Production("MATERIAL_PENDING", _today.AddDays(-2));                             // late

        var p = (await Service().GetSummaryAsync(UserWith(PermissionCodes.PROD_VIEW))).Production!;

        p.Orders!.Sum(s => s.Count).Should().Be(4);
        p.Late.Should().Be(2);
        p.OpenMakeToOrder.Should().Be(2);
        p.Boms.Should().BeNull("BOMs need BOM_VIEW");
    }

    [Fact]
    public async Task Receivables_are_kept_per_currency_and_overdue_is_past_due_with_a_balance()
    {
        SalesInvoice("ISSUED", 1000m, "PKR", _today.AddDays(-10));        // overdue
        SalesInvoice("PARTIALLY_PAID", 400m, "PKR", _today.AddDays(10));  // outstanding, not due
        SalesInvoice("OVERDUE", 50m, "USD", _today.AddDays(-1));          // overdue, USD
        SalesInvoice("PAID", 0m, "PKR", _today.AddDays(-40));             // settled
        SalesInvoice("DRAFT", 999m, "PKR", _today.AddDays(-40));          // not issued: not a receivable

        var ar = (await Service().GetSummaryAsync(UserWith(PermissionCodes.SALES_INVOICE_VIEW))).Receivables!;

        ar.Outstanding.Should().BeEquivalentTo(new[]
        {
            new CurrencyAmount { Currency = "PKR", Amount = 1400m, Count = 2 },
            new CurrencyAmount { Currency = "USD", Amount = 50m,   Count = 1 }
        });
        ar.Overdue.Should().BeEquivalentTo(new[]
        {
            new CurrencyAmount { Currency = "PKR", Amount = 1000m, Count = 1 },
            new CurrencyAmount { Currency = "USD", Amount = 50m,   Count = 1 }
        });
    }

    // ── Real SQL Server ───────────────────────────────────────────────────────

    /// <summary>
    /// Every query translates and runs on SQL Server — the in-memory store above accepts shapes SQL Server may not
    /// (array Contains, grouped projections, coalesced dates). A throwaway LocalDB database with all seven modules'
    /// tables; skipped when LocalDB is not there.
    /// </summary>
    [Fact]
    public async Task Every_query_runs_on_sql_server()
    {
        if (!SqlServerHarness.IsAvailable) return;

        var database = $"SMS_DashTest_{Guid.NewGuid():N}";
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
            make(new DbContextOptionsBuilder<T>().UseSqlServer(conn).Options, new StaticTenantContext { OrganizationId = Org });

        await Exec($"CREATE DATABASE [{database}]");
        try
        {
            var demand    = Sql<DemandDbContext>((o, t) => new(o, t));
            var warehouse = Sql<WarehouseDbContext>((o, t) => new(o, t));
            var suppliers = Sql<SuppliersDbContext>((o, t) => new(o, t));
            var logistics = Sql<LogisticsDbContext>((o, t) => new(o, t));
            var material  = Sql<MaterialDbContext>((o, t) => new(o, t));
            var finance   = Sql<FinanceDbContext>((o, t) => new(o, t));
            var inventory = Sql<InventoryDbContext>((o, t) => new(o, t));

            foreach (DbContext db in new DbContext[] { demand, warehouse, suppliers, logistics, material, finance, inventory })
                await Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions
                    .GetService<Microsoft.EntityFrameworkCore.Storage.IRelationalDatabaseCreator>(db).CreateTablesAsync();

            logistics.DeliveryOrders.Add(new DeliveryOrder
            {
                UUID = Guid.NewGuid(), OrganizationId = Org, TraceId = Guid.NewGuid(), DeliveryNumber = "DLV-1", Direction = "OUTBOUND",
                SourceType = "MANUAL", Status = "PICKING", PromisedDate = _today.AddDays(-1), CreatedBy = 1, CreatedDate = _today
            });
            await logistics.SaveChangesAsync();
            finance.SalesInvoices.Add(new SalesInvoice
            {
                UUID = Guid.NewGuid(), OrganizationId = Org, TraceId = Guid.NewGuid(), InvoiceNumber = "SINV-1", SaleOrderUuid = Guid.NewGuid(),
                SaleOrderNumber = "SO-1", PartnerId = Guid.NewGuid(), PartnerName = "x", InvoiceDate = _today.AddDays(-40),
                DueDate = _today.AddDays(-10), GrandTotal = 500m, BalanceDue = 500m, Status = "ISSUED", CurrencyCode = "PKR",
                CreatedBy = 1, CreatedDate = _today
            });
            await finance.SaveChangesAsync();

            var everything = UserWith(
                PermissionCodes.PO_VIEW, PermissionCodes.SUPPLIER_VIEW, PermissionCodes.INVENTORY_VIEW, PermissionCodes.SALE_ORDER_VIEW,
                PermissionCodes.SALE_INQUIRY_VIEW, PermissionCodes.SALE_QUOTATION_VIEW, PermissionCodes.DELIVERY_VIEW,
                PermissionCodes.PROD_VIEW, PermissionCodes.BOM_VIEW, PermissionCodes.SALES_INVOICE_VIEW, PermissionCodes.INVOICE_VIEW,
                PermissionCodes.MATERIAL_VIEW);

            var summary = await new DashboardService(demand, warehouse, suppliers, logistics, material, finance, inventory)
                .GetSummaryAsync(everything);

            summary.Deliveries!.Late.Should().Be(1);
            summary.Receivables!.Overdue.Should().ContainSingle().Which.Amount.Should().Be(500m);
            summary.Procurement.Should().NotBeNull();
            summary.Production!.Orders.Should().BeEmpty();

            foreach (DbContext db in new DbContext[] { demand, warehouse, suppliers, logistics, material, finance, inventory })
                await db.DisposeAsync();
        }
        finally
        {
            Microsoft.Data.SqlClient.SqlConnection.ClearAllPools();
            await Exec($"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]");
        }
    }

    // ── Attention ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Attention_lists_only_non_empty_queues_the_user_can_see()
    {
        SaleOrder("DRAFT");
        SaleOrder("CONFIRMED", expected: _today.AddDays(-1));
        Delivery("PENDING_APPROVAL");
        SalesInvoice("ISSUED", 10m, "PKR", _today.AddDays(-1));

        var summary = await Service().GetSummaryAsync(UserWith(PermissionCodes.SALE_ORDER_VIEW, PermissionCodes.DELIVERY_VIEW));

        summary.Attention.Select(a => a.Key).Should().BeEquivalentTo("SO_DRAFT", "SO_LATE", "DELIVERY_PENDING_APPROVAL");
        summary.Attention.Should().NotContain(a => a.Key == "AR_OVERDUE", "the user cannot see sales invoices");
    }
}
