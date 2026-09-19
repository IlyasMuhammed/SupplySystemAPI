using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Repositories;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>A29-P4-04 §4.5 — PurchaseOrderRepository.CancelAsync, the piece SaleOrderService's
/// own cancel delegates to for any linked DRAFT PO.</summary>
public class PurchaseOrderCancelTests
{
    private static async Task<(PurchaseOrderRepository Repo, DemandDbContext Db, Guid PoUuid)> NewDraftPo()
    {
        var db = new DemandDbContext(
            new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new StaticTenantContext());
        var repo = new PurchaseOrderRepository(db, NullLogger<PurchaseOrderRepository>.Instance);

        var poUuid = await repo.CreateAsync(new CreatePoRequest
        {
            SupplierId   = Guid.NewGuid(),
            SupplierName = "Test Vendor",
            Lines = [new CreatePoLineRequest { ItemDescription = "Cable", UnitOfMeasure = "PC", Quantity = 10m, UnitPrice = 5m }]
        }, createdBy: 1);

        return (repo, db, poUuid);
    }

    [Fact]
    public async Task Cancelling_a_draft_po_sets_its_status_to_cancelled()
    {
        var (repo, db, poUuid) = await NewDraftPo();

        await repo.CancelAsync(poUuid, "No longer needed", modifiedBy: 2);

        var po = await db.PurchaseOrders.AsNoTracking().SingleAsync(p => p.UUID == poUuid);
        po.Status.Should().Be("CANCELLED");
        po.ModifiedBy.Should().Be(2);
    }

    [Fact]
    public async Task Cancelling_a_draft_po_records_the_reason_in_internal_notes()
    {
        var (repo, db, poUuid) = await NewDraftPo();

        await repo.CancelAsync(poUuid, "Linked sale order SO-2026-00001 was cancelled.", modifiedBy: 2);

        var po = await db.PurchaseOrders.AsNoTracking().SingleAsync(p => p.UUID == poUuid);
        po.InternalNotes.Should().Contain("Linked sale order SO-2026-00001 was cancelled.");
    }

    [Fact]
    public async Task Cancelling_a_po_that_is_not_draft_is_refused()
    {
        var (repo, db, poUuid) = await NewDraftPo();
        var po = await db.PurchaseOrders.FirstAsync(p => p.UUID == poUuid);
        po.Status = "APPROVED";
        await db.SaveChangesAsync();

        var act = () => repo.CancelAsync(poUuid, "Reason", modifiedBy: 2);

        await act.Should().ThrowAsync<UnprocessableEntityException>();
    }

    [Fact]
    public async Task Cancelling_an_unknown_po_throws_not_found()
    {
        var (repo, _, _) = await NewDraftPo();

        var act = () => repo.CancelAsync(Guid.NewGuid(), "Reason", modifiedBy: 2);

        await act.Should().ThrowAsync<NotFoundException>();
    }
}
