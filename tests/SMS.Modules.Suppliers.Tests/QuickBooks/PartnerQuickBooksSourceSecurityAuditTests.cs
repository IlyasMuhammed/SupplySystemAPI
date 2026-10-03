using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Integration;
using SMS.Shared.Common;
using SMS.Shared.Integration.QuickBooks;
using Xunit;

namespace SMS.Modules.Suppliers.Tests.QuickBooks;

/// <summary>
/// Cross-cutting security audit: the partner source as the QuickBooks sync dashboard calls it — inside the HTTP
/// request of whoever clicks "Sync all" (SyncAdminService.BackfillAsync → PushAllAsync) or "Push now"
/// (SyncAdminService.PushAsync → PushAsync). The gateway files what it is handed under the CALLER's connection. A
/// super admin's request bypasses the tenant filter, so a source relying on the filter alone hands the caller's
/// QuickBooks company every organization's customers and vendors. Only the current organization's may go.
/// </summary>
public class PartnerQuickBooksSourceSecurityAuditTests
{
    private static (PartnerQuickBooksSource Source, RecordingQuickBooksGateway Gateway) SuperAdminSource(Guid org, string dbName)
    {
        var db = new SuppliersDbContext(new DbContextOptionsBuilder<SuppliersDbContext>().UseInMemoryDatabase(dbName).Options,
            new StaticTenantContext { OrganizationId = org, IsSuperAdmin = true });
        var gateway = new RecordingQuickBooksGateway();
        return (new PartnerQuickBooksSource(db, gateway, new FixedCurrencyCodes(null), new ListLogger<PartnerQuickBooksSource>()), gateway);
    }

    [Fact]
    public async Task SecurityAudit_sync_all_and_push_now_by_a_super_admin_never_hand_another_orgs_partners_to_its_QuickBooks()
    {
        var dbName = Guid.NewGuid().ToString();
        var orgA   = PartnerSourceRig.New(orgId: Guid.NewGuid(), dbName: dbName);
        var customer = orgA.Add("A-CUST", customer: true);
        var vendor   = orgA.Add("A-VEND", vendor: true);

        var (source, gateway) = SuperAdminSource(Guid.NewGuid(), dbName);   // a super admin working in org B

        await source.PushAllAsync(SyncKind.Customer, changedSince: null);
        await source.PushAllAsync(SyncKind.Vendor, changedSince: null);
        await source.PushAsync(SyncKind.Customer, [customer.UUID.ToString()]);
        await source.PushAsync(SyncKind.Vendor, [vendor.UUID.ToString()]);

        gateway.Customers.Should().BeEmpty("org A's customers would be filed under org B's QuickBooks connection");
        gateway.Vendors.Should().BeEmpty("org A's vendors would be filed under org B's QuickBooks connection");
    }

    [Fact]
    public async Task SecurityAudit_the_partner_source_still_sends_the_callers_own_partners()
    {
        var dbName = Guid.NewGuid().ToString();
        var orgB   = Guid.NewGuid();
        var own    = PartnerSourceRig.New(orgId: orgB, dbName: dbName).Add("B-CUST", customer: true);

        var (source, gateway) = SuperAdminSource(orgB, dbName);
        await source.PushAllAsync(SyncKind.Customer, changedSince: null);

        gateway.Customers.Select(c => c.ExternalId).Should().Equal(own.UUID.ToString());
    }
}
