using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Integration;
using SMS.Shared.Common;
using SMS.Shared.Integration.QuickBooks;
using Xunit;

namespace SMS.Modules.Inventory.Tests.QuickBooks;

/// <summary>
/// Cross-cutting security audit: the variant (item) source as the QuickBooks sync dashboard calls it — inside the
/// HTTP request of whoever clicks "Sync all" (SyncAdminService.BackfillAsync → PushAllAsync) or "Push now"
/// (SyncAdminService.PushAsync → PushAsync). The gateway files what it is handed under the CALLER's connection. A
/// super admin's request bypasses the tenant filter, so a source relying on the filter alone hands the caller's
/// QuickBooks company every organization's items. Only the current organization's may go.
/// </summary>
public class VariantQuickBooksSourceSecurityAuditTests
{
    private static (VariantQuickBooksSource Source, RecordingQuickBooksGateway Gateway) SuperAdminSource(Guid org, string dbName)
    {
        var db = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(dbName).Options,
            new StaticTenantContext { OrganizationId = org, IsSuperAdmin = true });
        var gateway = new RecordingQuickBooksGateway();
        return (new VariantQuickBooksSource(db, gateway, new ListLogger<VariantQuickBooksSource>()), gateway);
    }

    [Fact]
    public async Task SecurityAudit_sync_all_and_push_now_by_a_super_admin_never_hand_another_orgs_items_to_its_QuickBooks()
    {
        var dbName  = Guid.NewGuid().ToString();
        var product = VariantRig.New(dbName: dbName, orgId: Guid.NewGuid()).Product("Org A cement");
        var variant = product.Variants.Single();

        var (source, gateway) = SuperAdminSource(Guid.NewGuid(), dbName);   // a super admin working in org B

        await source.PushAllAsync(SyncKind.Item, changedSince: null);
        await source.PushAsync(SyncKind.Item, [variant.Uuid.ToString()]);

        gateway.Items.Should().BeEmpty("org A's items would be filed under org B's QuickBooks connection");
    }

    [Fact]
    public async Task SecurityAudit_the_variant_source_still_sends_the_callers_own_items()
    {
        var dbName  = Guid.NewGuid().ToString();
        var orgB    = Guid.NewGuid();
        var variant = VariantRig.New(dbName: dbName, orgId: orgB).Product("Org B cement").Variants.Single();

        var (source, gateway) = SuperAdminSource(orgB, dbName);
        await source.PushAllAsync(SyncKind.Item, changedSince: null);

        gateway.Items.Select(i => i.ExternalId).Should().Equal(variant.Uuid.ToString());
    }
}
