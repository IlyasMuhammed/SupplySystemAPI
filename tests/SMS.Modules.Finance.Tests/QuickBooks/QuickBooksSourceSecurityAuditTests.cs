using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Integration;
using SMS.Modules.Finance.Tests.SupplierInvoices;
using SMS.Shared.Common;
using SMS.Shared.Integration.QuickBooks;
using Xunit;

namespace SMS.Modules.Finance.Tests.QuickBooks;

/// <summary>
/// Cross-cutting security audit: Finance's QuickBooks sources as the sync dashboard calls them — inside the HTTP
/// request of whoever clicks "Sync all" (SyncAdminService.BackfillAsync → PushAllAsync) or "Push now"
/// (SyncAdminService.PushAsync → PushAsync). The gateway files whatever it is handed under the CALLER's
/// connection (ConnectionAccessor.GetCurrentAsync filters by TenantContext.OrganizationId). A super admin's request
/// bypasses the tenant filter, so a source that relies on the filter alone hands the caller's QuickBooks company
/// every organization's invoices and bills. Each source must send only the current organization's documents.
/// </summary>
public class QuickBooksSourceSecurityAuditTests
{
    private static readonly Guid OrgA = Guid.NewGuid();
    private static readonly Guid OrgB = Guid.NewGuid();

    private static FinanceDbContext SuperAdminIn(Guid org, string dbName) =>
        new(new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(dbName).Options,
            new StaticTenantContext { OrganizationId = org, IsSuperAdmin = true });

    [Fact]
    public async Task SecurityAudit_sync_all_and_push_now_by_a_super_admin_never_hand_another_orgs_sales_invoices_to_its_QuickBooks()
    {
        var world   = new ReceivablesWorld();
        var desk    = world.For(OrgA);
        var invoice = await desk.InvoiceAsync(desk.NewCustomer(), 250m);   // ISSUED in org A

        var gateway = new RecordingQuickBooksGateway();
        await using (var db = SuperAdminIn(OrgB, world.DbName))
        {
            var source = new SalesInvoiceQuickBooksSource(db, gateway, NullLogger<SalesInvoiceQuickBooksSource>.Instance);

            await source.PushAllAsync(SyncKind.SalesInvoice, changedSince: null);                    // "Sync all"
            await source.PushAsync(SyncKind.SalesInvoice, [invoice.Uuid.ToString()]);               // "Push now"
        }

        gateway.Calls.Should().NotContain(c => c.EndsWith(invoice.Uuid.ToString(), StringComparison.OrdinalIgnoreCase),
            "org A's invoice would be filed under org B's QuickBooks connection and, in Live mode, created in org B's company");
        gateway.SalesInvoices.Should().BeEmpty();
    }

    [Fact]
    public async Task SecurityAudit_sync_all_and_push_now_by_a_super_admin_never_hand_another_orgs_bills_to_its_QuickBooks()
    {
        var rig = PurchaseRig.New();
        var (bill, _, _) = await rig.ApprovedAsync();   // Approved in the rig's org

        var poLines = new Mock<IPurchaseOrderLineVariants>();
        poLines.Setup(p => p.GetAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new Dictionary<Guid, Guid?>());

        var gateway = new RecordingQuickBooksGateway();
        await using (var db = SuperAdminIn(OrgB, rig.DbName))
        {
            var source = new BillQuickBooksSource(db, poLines.Object, gateway, NullLogger<BillQuickBooksSource>.Instance);

            await source.PushAllAsync(SyncKind.Bill, changedSince: null);
            await source.PushAsync(SyncKind.Bill, [bill.ToString()]);
        }

        gateway.Bills.Should().BeEmpty("another organization's supplier invoices must never reach this organization's QuickBooks");
    }

    [Fact]
    public async Task SecurityAudit_the_sources_still_send_the_callers_own_documents()
    {
        // The fix must not stop a super admin (or anyone) from syncing their own organization.
        var world   = new ReceivablesWorld();
        var desk    = world.For(OrgB);
        var invoice = await desk.InvoiceAsync(desk.NewCustomer(), 90m);

        var gateway = new RecordingQuickBooksGateway();
        await using (var db = SuperAdminIn(OrgB, world.DbName))
            await new SalesInvoiceQuickBooksSource(db, gateway, NullLogger<SalesInvoiceQuickBooksSource>.Instance)
                .PushAllAsync(SyncKind.SalesInvoice, changedSince: null);

        gateway.SalesInvoices.Select(p => p.ExternalId).Should().Equal(invoice.Uuid.ToString());
    }
}
