using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Logistics.Data;
using SMS.Modules.Material.Data;
using SMS.Modules.Reports.Data;
using SMS.Modules.Reports.Repositories;
using SMS.Modules.Suppliers.Data;
using SMS.Shared.Common;
using SMS.WorkflowEngine.Services;
using Xunit;
using WarehouseDbContext = SMS.Modules.Warehouse.Data.WarehouseDbContext;

namespace SMS.Modules.Reports.Tests;

/// <summary>Reports' figures about supplier invoices, after the SAP alignment's work package C.</summary>
public class SupplierInvoiceAgingStatusTests
{
    private static (ReportsRepository Repo, FinanceDbContext Finance) NewRepo()
    {
        var name    = Guid.NewGuid().ToString();
        var tenant  = new StaticTenantContext();
        var finance = new FinanceDbContext(new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(name).Options, tenant);

        var repo = new ReportsRepository(
            db:        new ReportsDbContext(new DbContextOptionsBuilder<ReportsDbContext>().UseInMemoryDatabase(name).Options, tenant),
            demand:    new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(name).Options, tenant),
            warehouse: new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseInMemoryDatabase(name).Options, tenant),
            inventory: new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(name).Options, tenant),
            finance:   finance,
            logistics: new LogisticsDbContext(new DbContextOptionsBuilder<LogisticsDbContext>().UseInMemoryDatabase(name).Options, tenant),
            suppliers: new SuppliersDbContext(new DbContextOptionsBuilder<SuppliersDbContext>().UseInMemoryDatabase(name).Options, tenant),
            material:  new MaterialDbContext(new DbContextOptionsBuilder<MaterialDbContext>().UseInMemoryDatabase(name).Options, tenant),
            userQuery: new Mock<IUserQueryService>().Object,
            timeline:  new Mock<ITimelineService>().Object,
            supplierAccess: new UnrestrictedSupplierAccess());

        return (repo, finance);
    }

    private static int _n;

    private static Invoice Invoice(string match, string payment = "Unpaid", decimal total = 0m) => new()
    {
        UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), InvoiceNumber = $"INV-2026-{Interlocked.Increment(ref _n):D5}", SupplierId = Guid.NewGuid(),
        SupplierName = $"Supplier {match}", InvoiceDate = new DateTime(2026, 9, 1), ReceivedDate = new DateTime(2026, 9, 1),
        DueDate = new DateTime(2026, 9, 30), Currency = "PKR", Subtotal = total, TotalAmount = total,
        MatchStatus = match, PaymentStatus = payment, CreatedBy = 1, CreatedDate = DateTime.UtcNow
    };

    /// <summary>
    /// The invoice-aging report ages what is owed: an approved invoice (item E — the user's decision: a supplier
    /// invoice is a payable only once it is Approved, the SAP way), not one still Pending, Matched or Variance, nor one
    /// reversed (S-7), rejected or fully paid — and on what is still owed, as Finance's own aging (SFM-006) does.
    /// </summary>
    [Fact]
    public async Task Only_approved_unpaid_supplier_invoices_are_aged()
    {
        var (repo, finance) = NewRepo();
        var partlyPaid = Invoice("Approved", "PARTIALLY_PAID", 600m);
        partlyPaid.SupplierName = "Supplier part-paid";
        partlyPaid.PaidAmount   = 250m;
        var overpaid = Invoice("Approved", "OVERPAID", 700m);
        overpaid.SupplierName = "Supplier overpaid";
        overpaid.PaidAmount   = 800m;
        finance.Invoices.AddRange(
            Invoice("Pending", total: 100m), Invoice("Approved", total: 200m), Invoice("Reversed", total: 300m),
            Invoice("Rejected", total: 400m), Invoice("Approved", "FULLY_PAID", 500m), partlyPaid, overpaid);
        await finance.SaveChangesAsync();

        var (items, buckets) = await repo.GetInvoiceAgingAsync();

        items.Select(i => i.SupplierName).Should().BeEquivalentTo(["Supplier Approved", "Supplier part-paid"],
            "only an approved invoice with something still owed is a payable (item E) — not one still Pending, nor a reversed, rejected or paid one");
        items.Single(i => i.SupplierName == "Supplier part-paid").Should().BeEquivalentTo(new { TotalAmount = 600m, OutstandingAmount = 350m });
        buckets.Sum(b => b.TotalAmount).Should().Be(200m + 350m, "the buckets age what is still owed, as Finance's own aging (SFM-006) does");
    }

    /// <summary>
    /// The KPI dashboard's three-way match rate used to count only invoices still in status "Matched" — and approving
    /// one overwrites that status, so every approval lowered the rate and a fully processed, perfectly matched book
    /// showed close to 0%. An approved (or later reversed) invoice now counts as matched when its net Subtotal was
    /// within 5% of the value it was matched against (MatchedPoValue: what it bills at PO prices, item C).
    /// </summary>
    [Fact]
    public async Task The_three_way_match_rate_still_counts_a_matched_invoice_once_it_is_approved()
    {
        var (repo, finance) = NewRepo();
        Invoice WithGrn(string match, decimal expected, decimal subtotal)
        {
            var invoice = Invoice(match, total: subtotal);
            invoice.GrnUuid        = Guid.NewGuid();
            invoice.PoUuid         = Guid.NewGuid();
            invoice.MatchedPoValue = expected;
            invoice.VarianceAmount = subtotal - expected;
            return invoice;
        }
        finance.Invoices.AddRange(
            WithGrn("Matched",  1000m, 1000m),     // matched, not yet approved
            WithGrn("Approved", 1000m, 1040m),     // matched (4%), then approved
            WithGrn("Reversed", 500m,  500m),      // matched, approved, later reversed
            WithGrn("Approved", 1000m, 1200m),     // a variance someone approved anyway
            WithGrn("Variance", 1000m, 800m));     // a variance still waiting
        await finance.SaveChangesAsync();

        var kpis = await repo.GetKpiDashboardAsync();

        kpis.ThreeWayMatchRate.Should().Be(60.0, "3 of the 5 invoices with a GRN matched — approval does not undo that");
    }

    /// <summary>
    /// The pending-approvals list left out invoices in status Pending — exactly the invoices with no purchase order
    /// (G10 freight bills), which stay Pending until a person approves them, so they never appeared on it.
    /// </summary>
    [Fact]
    public async Task An_invoice_with_no_purchase_order_waiting_for_approval_is_on_the_pending_approvals_list()
    {
        var (repo, finance) = NewRepo();
        var pending  = Invoice("Pending",  total: 250m);
        var matched  = Invoice("Matched",  total: 300m);
        var approved = Invoice("Approved", total: 400m);
        approved.ApprovedBy = 7;
        finance.Invoices.AddRange(pending, matched, approved, Invoice("Rejected", total: 100m), Invoice("Reversed", total: 100m));
        await finance.SaveChangesAsync();

        var items = (await repo.GetPendingApprovalsAsync()).Where(i => i.EntityType == "Invoice").ToList();

        items.Select(i => i.EntityUuid).Should().BeEquivalentTo([pending.UUID, matched.UUID]);
        items.Single(i => i.EntityUuid == pending.UUID).Status.Should().Be("Awaiting Approval (Pending)");
    }
}
