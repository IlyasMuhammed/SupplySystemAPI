using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Services;
using SMS.WorkflowEngine.Events;
using Xunit;

namespace SMS.Modules.Material.Tests;

// ── Test builder ──────────────────────────────────────────────────────────────

file static class DisbursementBuild
{
    internal static DemandDbContext NewDemandDb(string dbName) =>
        new(new DbContextOptionsBuilder<DemandDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    internal static MaterialDbContext NewMaterialDb(string dbName) =>
        new(new DbContextOptionsBuilder<MaterialDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    internal static (MaterialDbContext material, DemandDbContext demand, MirPrLineDisbursementHandler handler) NewHandler()
    {
        var material = NewMaterialDb(Guid.NewGuid().ToString());
        var demand   = NewDemandDb(Guid.NewGuid().ToString());
        return (material, demand, new MirPrLineDisbursementHandler(material, demand));
    }

    internal static PrLine SeedPrLine(DemandDbContext db, decimal qty = 100m, string lineStatus = "OPEN")
    {
        var pr = new PurchaseRequisition
        {
            UUID          = Guid.NewGuid(),
            PrNumber      = $"PR-{Guid.NewGuid():N}"[..12],
            PrTitle       = "Test Requisition",
            RequesterId   = 1,
            RequestedDate = DateTime.UtcNow,
            Status        = "APPROVED",
            CreatedBy     = 1,
            CreatedDate   = DateTime.UtcNow
        };
        var line = new PrLine
        {
            UUID            = Guid.NewGuid(),
            LineNo          = 1,
            ItemDescription = "Lenovo Laptop",
            Quantity        = qty,
            LineStatus      = lineStatus
        };
        pr.Lines.Add(line);
        db.PurchaseRequisitions.Add(pr);
        db.SaveChanges();
        return line;
    }

    internal static MaterialIssueRequest SeedApprovedMir(MaterialDbContext db, int? prLineId, decimal approvedQty, string requestNo)
    {
        var line = new MaterialIssueRequestDetail
        {
            UUID            = Guid.NewGuid(),
            LineNo          = 1,
            ProductUuid     = Guid.NewGuid(),
            ItemDescription = "Lenovo Laptop",
            RequestedQty    = approvedQty,
            PrLineId        = prLineId
        };
        var mir = new MaterialIssueRequest
        {
            UUID        = Guid.NewGuid(),
            RequestNo   = requestNo,
            RequestType = "DEPARTMENT",
            Department  = "IT",
            RequestedBy = 1,
            Status      = "APPROVED",
            CreatedBy   = 1,
            CreatedDate = DateTime.UtcNow,
            Lines       = [line]
        };
        db.MaterialIssueRequests.Add(mir);
        db.SaveChanges();

        db.MirLineApprovals.Add(new MirLineApproval
        {
            UUID                 = Guid.NewGuid(),
            MirId                = mir.Id,
            LineId               = line.Id,
            WorkflowApprovalUUID = Guid.NewGuid(),
            StepNumber           = 1,
            ApprovedBy           = 1,
            ApprovedAt           = DateTime.UtcNow,
            ApprovedQty          = approvedQty
        });
        db.SaveChanges();
        return mir;
    }

    internal static DocumentApprovedEvent ApprovedEvent(MaterialIssueRequest mir, string interfaceCode = "MIR_GENERAL") =>
        new(Guid.NewGuid(), interfaceCode, mir.UUID, mir.RequestNo, 1, DateTime.UtcNow);
}

// ── PR-line disbursement tracking on MIR approval (TL-003) ─────────────────────

public class MirPrLineDisbursementHandler_Tests
{
    [Fact]
    public async Task First_Mir_Approval_Sets_DisbursedQty_And_Leaves_LineStatus_Unchanged()
    {
        var (material, demand, handler) = DisbursementBuild.NewHandler();
        var prLine = DisbursementBuild.SeedPrLine(demand, qty: 100m, lineStatus: "OPEN");
        var mir = DisbursementBuild.SeedApprovedMir(material, prLine.Id, approvedQty: 60m, requestNo: "MIR-2026-00001");

        await handler.Handle(DisbursementBuild.ApprovedEvent(mir), CancellationToken.None);

        var updated = await demand.PrLines.FirstAsync(l => l.Id == prLine.Id);
        updated.DisbursedQty.Should().Be(60m);
        updated.LineStatus.Should().Be("OPEN");
    }

    [Fact]
    public async Task Second_Mir_Approval_Accumulates_DisbursedQty_And_Still_Leaves_LineStatus_Unchanged()
    {
        var (material, demand, handler) = DisbursementBuild.NewHandler();
        var prLine = DisbursementBuild.SeedPrLine(demand, qty: 100m, lineStatus: "OPEN");

        var mir1 = DisbursementBuild.SeedApprovedMir(material, prLine.Id, approvedQty: 60m, requestNo: "MIR-2026-00001");
        await handler.Handle(DisbursementBuild.ApprovedEvent(mir1), CancellationToken.None);

        var mir2 = DisbursementBuild.SeedApprovedMir(material, prLine.Id, approvedQty: 40m, requestNo: "MIR-2026-00002");
        await handler.Handle(DisbursementBuild.ApprovedEvent(mir2), CancellationToken.None);

        var updated = await demand.PrLines.FirstAsync(l => l.Id == prLine.Id);
        updated.DisbursedQty.Should().Be(100m);
        updated.LineStatus.Should().Be("OPEN");

        var mirIds = JsonSerializer.Deserialize<List<Guid>>(updated.DisbursedMirIds)!;
        mirIds.Should().Contain(mir1.UUID);
        mirIds.Should().Contain(mir2.UUID);
        mirIds.Should().HaveCount(2);
    }

    [Fact]
    public async Task Mir_Approval_With_No_Linked_PrLine_Touches_Nothing()
    {
        var (material, demand, handler) = DisbursementBuild.NewHandler();
        var prLine = DisbursementBuild.SeedPrLine(demand, qty: 100m, lineStatus: "OPEN");

        var mir = DisbursementBuild.SeedApprovedMir(material, prLineId: null, approvedQty: 5m, requestNo: "MIR-2026-00003");

        await handler.Handle(DisbursementBuild.ApprovedEvent(mir), CancellationToken.None);

        var untouched = await demand.PrLines.FirstAsync(l => l.Id == prLine.Id);
        untouched.DisbursedQty.Should().Be(0m);
        untouched.LineStatus.Should().Be("OPEN");
        untouched.DisbursedMirIds.Should().Be("[]");
    }

    [Fact]
    public async Task NonMir_InterfaceCode_Is_Ignored()
    {
        var (material, demand, handler) = DisbursementBuild.NewHandler();
        var prLine = DisbursementBuild.SeedPrLine(demand, qty: 100m, lineStatus: "OPEN");
        var mir = DisbursementBuild.SeedApprovedMir(material, prLine.Id, approvedQty: 60m, requestNo: "MIR-2026-00004");

        // Same MIR document id but a foreign interface code — must be a strict no-op.
        await handler.Handle(DisbursementBuild.ApprovedEvent(mir, interfaceCode: "PO"), CancellationToken.None);

        var untouched = await demand.PrLines.FirstAsync(l => l.Id == prLine.Id);
        untouched.DisbursedQty.Should().Be(0m);
        untouched.LineStatus.Should().Be("OPEN");
    }

    [Fact]
    public void PrLine_With_Zero_Linked_Mirs_Keeps_Disbursed_Zero_And_Status_Unchanged()
    {
        // Non-regression: a PR line that was never touched by this handler must default correctly.
        var demand = DisbursementBuild.NewDemandDb(Guid.NewGuid().ToString());
        var prLine = DisbursementBuild.SeedPrLine(demand, qty: 50m, lineStatus: "PENDING_QUOTE");

        prLine.DisbursedQty.Should().Be(0m);
        prLine.DisbursedMirIds.Should().Be("[]");
        prLine.LineStatus.Should().Be("PENDING_QUOTE");
    }

    [Fact]
    public async Task Concurrent_Approvals_Against_Same_PrLine_Both_Apply_Without_Lost_Update()
    {
        // Two independent DbContext instances (simulating two separate HTTP request scopes)
        // pointed at the same underlying in-memory store, approved in parallel.
        // Note: EF Core's InMemory provider does not enforce true SQL Server serializable
        // isolation, so this exercises the handler's read-modify-write code path under
        // concurrent invocation rather than proving real engine-level lock behavior —
        // that guarantee ultimately rests on the SERIALIZABLE transaction issued against
        // SQL Server in production and needs integration-level verification there.
        var materialDbName = Guid.NewGuid().ToString();
        var demandDbName   = Guid.NewGuid().ToString();

        using var materialSeed = DisbursementBuild.NewMaterialDb(materialDbName);
        using var demandSeed   = DisbursementBuild.NewDemandDb(demandDbName);

        var prLine = DisbursementBuild.SeedPrLine(demandSeed, qty: 100m, lineStatus: "OPEN");
        var mir1 = DisbursementBuild.SeedApprovedMir(materialSeed, prLine.Id, approvedQty: 30m, requestNo: "MIR-2026-00010");
        var mir2 = DisbursementBuild.SeedApprovedMir(materialSeed, prLine.Id, approvedQty: 20m, requestNo: "MIR-2026-00011");

        using var material1 = DisbursementBuild.NewMaterialDb(materialDbName);
        using var demand1   = DisbursementBuild.NewDemandDb(demandDbName);
        var handler1 = new MirPrLineDisbursementHandler(material1, demand1);

        using var material2 = DisbursementBuild.NewMaterialDb(materialDbName);
        using var demand2   = DisbursementBuild.NewDemandDb(demandDbName);
        var handler2 = new MirPrLineDisbursementHandler(material2, demand2);

        await Task.WhenAll(
            handler1.Handle(DisbursementBuild.ApprovedEvent(mir1), CancellationToken.None),
            handler2.Handle(DisbursementBuild.ApprovedEvent(mir2), CancellationToken.None));

        using var verify = DisbursementBuild.NewDemandDb(demandDbName);
        var updated = await verify.PrLines.FirstAsync(l => l.Id == prLine.Id);
        updated.DisbursedQty.Should().Be(50m);

        var mirIds = JsonSerializer.Deserialize<List<Guid>>(updated.DisbursedMirIds)!;
        mirIds.Should().Contain(mir1.UUID);
        mirIds.Should().Contain(mir2.UUID);
    }
}
