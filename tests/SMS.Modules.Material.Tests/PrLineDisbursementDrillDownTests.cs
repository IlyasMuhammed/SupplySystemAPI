using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Services;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Material.Tests;

// ── Test builder ──────────────────────────────────────────────────────────────

file static class DrillDownBuild
{
    internal static (PrLookupService service, MaterialDbContext material, DemandDbContext demand) NewService()
    {
        var material = new MaterialDbContext(new DbContextOptionsBuilder<MaterialDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var demand = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

        return (new PrLookupService(demand, material), material, demand);
    }

    internal static (PurchaseRequisition pr, PrLine line) SeedPrLineWithDisbursements(
        DemandDbContext db, decimal qty, params Guid[] mirUuids)
    {
        var pr = new PurchaseRequisition
        {
            UUID = Guid.NewGuid(), PrNumber = $"PR-{Guid.NewGuid():N}"[..12], PrTitle = "Test PR",
            RequesterId = 1, RequestedDate = DateTime.UtcNow, Status = "APPROVED",
            CreatedBy = 1, CreatedDate = DateTime.UtcNow
        };
        var line = new PrLine
        {
            UUID = Guid.NewGuid(), LineNo = 1, ItemDescription = "Lenovo Laptop", Quantity = qty,
            LineStatus = "OPEN",
            DisbursedMirIds = JsonSerializer.Serialize(mirUuids.ToList())
        };
        pr.Lines.Add(line);
        db.PurchaseRequisitions.Add(pr);
        db.SaveChanges();
        return (pr, line);
    }

    internal static MaterialIssueRequest SeedMirLinkedToPrLine(
        MaterialDbContext db, int prLineId, decimal approvedQty, string requestNo, Guid mirUuid,
        string requestType = "DEPARTMENT", string? department = "IT", DateTime? approvedAt = null)
    {
        var line = new MaterialIssueRequestDetail
        {
            UUID = Guid.NewGuid(), LineNo = 1, ProductUuid = Guid.NewGuid(),
            ItemDescription = "Lenovo Laptop", RequestedQty = approvedQty, PrLineId = prLineId
        };
        var mir = new MaterialIssueRequest
        {
            UUID = mirUuid, RequestNo = requestNo, RequestType = requestType, Department = department,
            RequestedBy = 1, Status = "APPROVED", ApprovedAt = approvedAt ?? DateTime.UtcNow,
            CreatedBy = 1, CreatedDate = DateTime.UtcNow, Lines = [line]
        };
        db.MaterialIssueRequests.Add(mir);
        db.SaveChanges();

        db.MirLineApprovals.Add(new MirLineApproval
        {
            UUID = Guid.NewGuid(), MirId = mir.Id, LineId = line.Id,
            WorkflowApprovalUUID = Guid.NewGuid(), StepNumber = 1,
            ApprovedBy = 1, ApprovedAt = DateTime.UtcNow, ApprovedQty = approvedQty
        });
        db.SaveChanges();
        return mir;
    }
}

// ── GET /api/purchase-requisitions/{prId}/lines/{lineId}/disbursements (TL-004) ─

public class PrLineDisbursementDrillDown_Tests
{
    [Fact]
    public async Task Returns_Linked_Mirs_With_Correct_Fields()
    {
        var (service, material, demand) = DrillDownBuild.NewService();

        var mir1Uuid = Guid.NewGuid();
        var mir2Uuid = Guid.NewGuid();
        var (pr, line) = DrillDownBuild.SeedPrLineWithDisbursements(demand, qty: 100m, mir1Uuid, mir2Uuid);

        // Seed the actual MIRs with the same UUIDs referenced in disbursed_mir_ids.
        DrillDownBuild.SeedMirLinkedToPrLine(material, line.Id, approvedQty: 60m, requestNo: "MIR-2026-00001", mirUuid: mir1Uuid);
        DrillDownBuild.SeedMirLinkedToPrLine(material, line.Id, approvedQty: 40m, requestNo: "MIR-2026-00002", mirUuid: mir2Uuid);

        var results = await service.GetDisbursementsAsync(pr.UUID, line.UUID);

        results.Should().HaveCount(2);
        results[0].MirNumber.Should().Be("MIR-2026-00001");
        results[0].ApprovedQty.Should().Be(60m);
        results[0].ProjectOrDept.Should().Be("IT");
        results[0].MirUuid.Should().Be(mir1Uuid);
        results[1].MirNumber.Should().Be("MIR-2026-00002");
        results[1].ApprovedQty.Should().Be(40m);
    }

    [Fact]
    public async Task Empty_DisbursedMirIds_Returns_Empty_List()
    {
        var (service, _, demand) = DrillDownBuild.NewService();
        var (pr, line) = DrillDownBuild.SeedPrLineWithDisbursements(demand, qty: 100m);

        var results = await service.GetDisbursementsAsync(pr.UUID, line.UUID);

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task Unknown_Line_Throws_NotFound()
    {
        var (service, _, demand) = DrillDownBuild.NewService();
        var (pr, _) = DrillDownBuild.SeedPrLineWithDisbursements(demand, qty: 100m);

        var act = () => service.GetDisbursementsAsync(pr.UUID, Guid.NewGuid());

        await act.Should().ThrowAsync<NotFoundException>();
    }

    [Fact]
    public async Task Mismatched_PrId_Throws_NotFound()
    {
        var (service, _, demand) = DrillDownBuild.NewService();
        var (_, line) = DrillDownBuild.SeedPrLineWithDisbursements(demand, qty: 100m);

        // Correct lineId but a PR id that doesn't own it.
        var act = () => service.GetDisbursementsAsync(Guid.NewGuid(), line.UUID);

        await act.Should().ThrowAsync<NotFoundException>();
    }
}
