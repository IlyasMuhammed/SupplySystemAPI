using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Finance.Data;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Material.Data;
using SMS.Modules.Reports.Models;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Warehouse.Data;
using SMS.Shared.Authorization;
using SMS.Shared.Common;

namespace SMS.Modules.Reports.Services;

public interface IDashboardService
{
    /// <summary>The home dashboard's numbers, limited to what <paramref name="user"/> may see.</summary>
    Task<DashboardSummaryModel> GetSummaryAsync(ClaimsPrincipal user, CancellationToken ct = default);
}

/// <summary>
/// One read for the home dashboard. It replaces two dozen page-size-1 list calls, one per status, that the page used to
/// fire — and that could not cover sales, deliveries, production or money without doubling again.
/// <para>
/// <b>Scope:</b> every context carries the tenant query filter, so each count is the caller's organization (a super
/// admin's, unfiltered, as every list is). <b>Permissions:</b> a section the caller may not see is left null, not
/// zeroed. <b>Sequential on purpose:</b> a DbContext is not thread-safe, and these are a few grouped COUNTs each.
/// </para>
/// </summary>
internal sealed class DashboardService : IDashboardService
{
    // Statuses after which a document no longer needs anything — used for "late" and "open" counts.
    private static readonly string[] ClosedSaleOrder  = ["FULFILLED", "INVOICED", "CLOSED", "CANCELLED", "DRAFT"];
    private static readonly string[] DeliveryDone     = ["GOODS_ISSUED", "IN_TRANSIT", "DELIVERED", "PARTIALLY_DELIVERED", "CLOSED", "SHORT_CLOSED", "CANCELLED"];
    private static readonly string[] InboundDone      = ["DELIVERED", "PARTIALLY_DELIVERED", "CLOSED", "SHORT_CLOSED", "CANCELLED"];
    private static readonly string[] ProductionClosed = ["COMPLETED", "CLOSED", "CANCELLED"];
    private static readonly string[] SalesInvoiceOpen = ["ISSUED", "PARTIALLY_PAID", "OVERDUE"];
    // Supplier invoices carry two payment vocabularies (see Invoice.PaymentStatus) — both "paid" spellings count.
    private static readonly string[] PayableSettled   = ["Paid", "FULLY_PAID", "OVERPAID"];

    private readonly DemandDbContext    _demand;
    private readonly WarehouseDbContext _warehouse;
    private readonly SuppliersDbContext _suppliers;
    private readonly LogisticsDbContext _logistics;
    private readonly MaterialDbContext  _material;
    private readonly FinanceDbContext   _finance;
    private readonly InventoryDbContext _inventory;

    public DashboardService(
        DemandDbContext demand, WarehouseDbContext warehouse, SuppliersDbContext suppliers, LogisticsDbContext logistics,
        MaterialDbContext material, FinanceDbContext finance, InventoryDbContext inventory)
    {
        _demand    = demand;
        _warehouse = warehouse;
        _suppliers = suppliers;
        _logistics = logistics;
        _material  = material;
        _finance   = finance;
        _inventory = inventory;
    }

    public async Task<DashboardSummaryModel> GetSummaryAsync(ClaimsPrincipal user, CancellationToken ct = default)
    {
        bool Can(params string[] any) => any.Any(user.HasPermission);

        var now        = DateTime.UtcNow;
        var today      = now.Date;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var summary    = new DashboardSummaryModel { GeneratedAt = now };

        // ── Procurement ───────────────────────────────────────────────────────
        if (Can(PermissionCodes.PO_VIEW))
        {
            summary.Procurement = new DashboardProcurementModel
            {
                PurchaseOrders = await ByStatus(_demand.PurchaseOrders.Where(p => !p.IsDelete).Select(p => p.Status), ct),
                Requisitions   = await ByStatus(_demand.PurchaseRequisitions.Where(p => !p.IsDelete).Select(p => p.Status), ct)
            };

            if (Can(PermissionCodes.SUPPLIER_VIEW))
            {
                var vendors = _suppliers.BusinessPartners.Where(b => !b.IsDelete && b.IsVendor);
                summary.Procurement.TotalSuppliers  = await vendors.CountAsync(ct);
                summary.Procurement.ActiveSuppliers = await vendors.CountAsync(b => b.Status == "ACTIVE", ct);
            }
        }

        if (Can(PermissionCodes.INVENTORY_VIEW, PermissionCodes.GRN_APPROVE, PermissionCodes.GRN_QC_CONFIRM, PermissionCodes.GRN_FINANCE_APPROVE))
            summary.Receiving = new DashboardReceivingModel
            {
                Grns = await ByStatus(_warehouse.Grns.Where(g => !g.IsDelete).Select(g => g.Status), ct)
            };

        // ── Sales ─────────────────────────────────────────────────────────────
        var seesOrders = Can(PermissionCodes.SALE_ORDER_VIEW);
        if (seesOrders || Can(PermissionCodes.SALE_INQUIRY_VIEW, PermissionCodes.SALE_QUOTATION_VIEW))
        {
            var sales = new DashboardSalesModel();

            if (Can(PermissionCodes.SALE_INQUIRY_VIEW))
                sales.Inquiries = await ByStatus(_demand.SaleInquiries.Select(i => i.Status), ct);

            if (Can(PermissionCodes.SALE_QUOTATION_VIEW))
                sales.Quotations = await ByStatus(_demand.SaleQuotations.Select(q => q.Status), ct);

            if (seesOrders)
            {
                var orders = _demand.SaleOrders.Where(s => !s.IsDeleted);
                sales.SaleOrders      = await ByStatus(orders.Select(s => s.Status), ct);
                sales.LateSaleOrders  = await orders.CountAsync(s => !ClosedSaleOrder.Contains(s.Status)
                                                                  && s.ExpectedDeliveryDate != null && s.ExpectedDeliveryDate < today, ct);
                sales.OrdersThisMonth = await orders.CountAsync(s => s.Status != "CANCELLED" && s.OrderDate >= monthStart, ct);
                sales.ActiveCustomers = await _suppliers.BusinessPartners.CountAsync(b => !b.IsDelete && b.IsCustomer && b.IsActive, ct);
            }

            summary.Sales = sales;
        }

        // ── Deliveries ────────────────────────────────────────────────────────
        if (Can(PermissionCodes.DELIVERY_VIEW))
        {
            var live     = _logistics.DeliveryOrders.Where(d => !d.IsDelete);
            var outbound = live.Where(d => d.Direction != "INBOUND");

            summary.Deliveries = new DashboardDeliveryModel
            {
                Outbound         = await ByStatus(outbound.Select(d => d.Status), ct),
                InboundExpected  = await live.CountAsync(d => d.Direction == "INBOUND" && !InboundDone.Contains(d.Status), ct),
                Late             = await outbound.CountAsync(d => !DeliveryDone.Contains(d.Status)
                                                              && (d.PromisedDate ?? d.RequestedDate) != null
                                                              && (d.PromisedDate ?? d.RequestedDate) < today, ct),
                ShippedThisMonth = await outbound.CountAsync(d => d.GoodsIssuedAt != null && d.GoodsIssuedAt >= monthStart, ct)
            };
        }

        // ── Production ────────────────────────────────────────────────────────
        if (Can(PermissionCodes.PROD_VIEW, PermissionCodes.BOM_VIEW))
        {
            var production = new DashboardProductionModel();

            if (Can(PermissionCodes.PROD_VIEW))
            {
                var orders = _material.ProductionOrders.AsQueryable();
                production.Orders          = await ByStatus(orders.Select(p => p.Status), ct);
                production.Late            = await orders.CountAsync(p => !ProductionClosed.Contains(p.Status) && p.RequiredDate < today, ct);
                production.OpenMakeToOrder = await orders.CountAsync(p => !ProductionClosed.Contains(p.Status)
                                                                       && p.SourceType == ProductionSourceType.SalesOrder, ct);
            }

            if (Can(PermissionCodes.BOM_VIEW))
                production.Boms = await ByStatus(_material.BillsOfMaterials.Select(b => b.Status), ct);

            summary.Production = production;
        }

        // ── Money ─────────────────────────────────────────────────────────────
        if (Can(PermissionCodes.SALES_INVOICE_VIEW))
        {
            var invoices = _finance.SalesInvoices.Where(i => !i.IsDelete);
            var open     = invoices.Where(i => SalesInvoiceOpen.Contains(i.Status) && i.BalanceDue > 0);

            summary.Receivables = new DashboardMoneyModel
            {
                Invoices    = await ByStatus(invoices.Select(i => i.Status), ct),
                Outstanding = await open.GroupBy(i => i.CurrencyCode)
                    .Select(g => new CurrencyAmount { Currency = g.Key, Amount = g.Sum(i => i.BalanceDue), Count = g.Count() })
                    .ToListAsync(ct),
                Overdue     = await open.Where(i => i.DueDate < today).GroupBy(i => i.CurrencyCode)
                    .Select(g => new CurrencyAmount { Currency = g.Key, Amount = g.Sum(i => i.BalanceDue), Count = g.Count() })
                    .ToListAsync(ct)
            };
        }

        if (Can(PermissionCodes.INVOICE_VIEW))
        {
            var bills = _finance.Invoices.Where(i => !i.IsDelete && i.ReversedAt == null);
            // Owed = approved and not settled. PaidAmount only moves under the supplier-payment flow; a bill settled
            // under the legacy single-payment flow says "Paid" instead, and is excluded by status.
            var owed  = bills.Where(i => i.MatchStatus == "Approved" && !PayableSettled.Contains(i.PaymentStatus)
                                      && i.TotalAmount - i.PaidAmount > 0);

            summary.Payables = new DashboardMoneyModel
            {
                Invoices    = await ByStatus(bills.Select(i => i.MatchStatus), ct),
                Outstanding = await owed.GroupBy(i => i.Currency)
                    .Select(g => new CurrencyAmount { Currency = g.Key, Amount = g.Sum(i => i.TotalAmount - i.PaidAmount), Count = g.Count() })
                    .ToListAsync(ct),
                Overdue     = await owed.Where(i => i.DueDate < today).GroupBy(i => i.Currency)
                    .Select(g => new CurrencyAmount { Currency = g.Key, Amount = g.Sum(i => i.TotalAmount - i.PaidAmount), Count = g.Count() })
                    .ToListAsync(ct)
            };
        }

        // ── Materials & inventory ─────────────────────────────────────────────
        if (Can(PermissionCodes.MATERIAL_VIEW))
            summary.Material = new DashboardMaterialModel
            {
                Mirs = await ByStatus(_material.MaterialIssueRequests.Where(m => !m.IsDelete).Select(m => m.Status), ct)
            };

        if (Can(PermissionCodes.INVENTORY_VIEW))
            summary.Inventory = new DashboardInventoryModel
            {
                ActiveProducts       = await _inventory.Products.CountAsync(p => p.IsActive, ct),
                ManufacturedProducts = await _inventory.Products.CountAsync(p => p.IsActive && p.IsManufacturable, ct)
            };

        summary.Attention = Attention(summary);
        return summary;
    }

    /// <summary>The queues that wait on a decision, from the counts already read. Only what the caller can see.</summary>
    private static List<DashboardAttentionItem> Attention(DashboardSummaryModel s)
    {
        var items = new List<DashboardAttentionItem>();
        void Add(string key, int count) { if (count > 0) items.Add(new DashboardAttentionItem { Key = key, Count = count }); }

        if (s.Procurement is { } p)
        {
            Add("PR_SUBMITTED",         Of(p.Requisitions,   "SUBMITTED"));
            Add("PO_PENDING_APPROVAL",  Of(p.PurchaseOrders, "PENDING_APPROVAL"));
        }
        if (s.Receiving is { } r)
        {
            Add("GRN_PENDING_QC",       Of(r.Grns, "PENDING_QC"));
            Add("GRN_PENDING_FINANCE",  Of(r.Grns, "PENDING_FINANCE"));
            Add("GRN_PENDING_APPROVAL", Of(r.Grns, "PENDING_APPROVAL"));
        }
        if (s.Sales is { } sales)
        {
            Add("INQUIRY_NEW",          Of(sales.Inquiries, "RECEIVED") + Of(sales.Inquiries, "UNDER_REVIEW"));
            Add("SO_DRAFT",             Of(sales.SaleOrders, "DRAFT"));
            Add("SO_LATE",              sales.LateSaleOrders ?? 0);
        }
        if (s.Deliveries is { } d)
        {
            Add("DELIVERY_PENDING_APPROVAL", Of(d.Outbound, "PENDING_APPROVAL"));
            Add("DELIVERY_ON_HOLD",          Of(d.Outbound, "ON_HOLD"));
            Add("DELIVERY_LATE",             d.Late);
        }
        if (s.Production is { } prod)
        {
            Add("BOM_SUBMITTED",        Of(prod.Boms, "SUBMITTED"));
            Add("PROD_MATERIAL_PENDING", Of(prod.Orders, "MATERIAL_PENDING"));
            Add("PROD_LATE",            prod.Late ?? 0);
        }
        if (s.Material is { } m)
            Add("MIR_PENDING_APPROVAL", Of(m.Mirs, "PENDING_APPROVAL"));
        if (s.Receivables is { } ar)
            Add("AR_OVERDUE",           ar.Overdue.Sum(x => x.Count));
        if (s.Payables is { } ap)
        {
            Add("AP_VARIANCE",          Of(ap.Invoices, "Variance"));
            Add("AP_OVERDUE",           ap.Overdue.Sum(x => x.Count));
        }

        return items;
    }

    private static int Of(List<StatusCount>? counts, string status) =>
        counts?.FirstOrDefault(c => c.Status == status)?.Count ?? 0;

    private static Task<List<StatusCount>> ByStatus(IQueryable<string> statuses, CancellationToken ct) =>
        statuses.GroupBy(s => s)
                .Select(g => new StatusCount { Status = g.Key, Count = g.Count() })
                .ToListAsync(ct);
}
