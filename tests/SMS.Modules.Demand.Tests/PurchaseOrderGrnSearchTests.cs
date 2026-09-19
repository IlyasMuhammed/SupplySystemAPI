using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Repositories;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>A29-P5-05 §4.3 scenario 4 — SearchForGrnAsync feeds the "which PO are we receiving
/// against" picker, so a drop-ship PO (vendor ships straight to the customer, "no warehouse stock
/// impact") must never appear in it.</summary>
public class PurchaseOrderGrnSearchTests
{
    private static async Task<PurchaseOrderRepository> NewRepo(params (string poNumber, string source, string status)[] pos)
    {
        var db = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new StaticTenantContext { OrganizationId = Guid.NewGuid() });

        foreach (var (poNumber, source, status) in pos)
            db.PurchaseOrders.Add(new PurchaseOrder
            {
                UUID = Guid.NewGuid(), PoNumber = poNumber, SupplierId = Guid.NewGuid(), SupplierName = "Vendor",
                Status = status, Source = source, CreatedBy = 1, CreatedDate = DateTime.UtcNow
            });
        await db.SaveChangesAsync();

        return new PurchaseOrderRepository(db, NullLogger<PurchaseOrderRepository>.Instance);
    }

    [Fact]
    public async Task A_sent_drop_ship_po_is_not_offered_for_receiving()
    {
        var repo = await NewRepo(
            ("PO-1", "MANUAL", "SENT"), ("PO-2", "BACK_TO_BACK", "SENT"), ("PO-3", "DROP_SHIP", "SENT"));

        var receivable = await repo.SearchForGrnAsync(q: null, receivableOnly: true);

        receivable.Select(p => p.PoNumber).Should().BeEquivalentTo(["PO-1", "PO-2"]);
    }

    [Fact]
    public async Task A_partially_received_po_still_qualifies_and_a_drop_ship_one_still_does_not()
    {
        var repo = await NewRepo(("PO-1", "BACK_TO_BACK", "PARTIALLY_RECEIVED"), ("PO-2", "DROP_SHIP", "PARTIALLY_RECEIVED"));

        var receivable = await repo.SearchForGrnAsync(q: null, receivableOnly: true);

        receivable.Should().ContainSingle().Which.PoNumber.Should().Be("PO-1");
    }

    [Fact]
    public async Task A_drop_ship_po_is_still_found_by_a_general_search()
    {
        var repo = await NewRepo(("PO-1", "MANUAL", "SENT"), ("PO-3", "DROP_SHIP", "SENT"));

        var all = await repo.SearchForGrnAsync(q: null, receivableOnly: false);

        all.Select(p => p.PoNumber).Should().BeEquivalentTo(["PO-1", "PO-3"]);
    }
}
