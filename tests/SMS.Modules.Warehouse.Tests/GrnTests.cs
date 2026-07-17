using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Warehouse.Data;
using SMS.Modules.Warehouse.Events;
using SMS.Modules.Warehouse.Models;
using SMS.Modules.Warehouse.Repositories;
using SMS.Modules.Warehouse.Services;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Warehouse.Tests;

// ── Test builder ──────────────────────────────────────────────────────────────

file static class GrnBuild
{
    internal static (GrnRepository repo, WarehouseDbContext wh, DemandDbContext demand) New(
        Action<DemandDbContext>? seedDemand = null)
    {
        var dbName = Guid.NewGuid().ToString();

        var demandOpts = new DbContextOptionsBuilder<DemandDbContext>()
            .UseInMemoryDatabase(dbName).Options;
        var demand = new DemandDbContext(demandOpts);
        seedDemand?.Invoke(demand);
        demand.SaveChanges();

        var whOpts = new DbContextOptionsBuilder<WarehouseDbContext>()
            .UseInMemoryDatabase(dbName).Options;
        var wh = new WarehouseDbContext(whOpts);

        var repo = new GrnRepository(wh, demand);
        return (repo, wh, demand);
    }

    internal static PurchaseOrder SentPo(
        string status = "SENT",
        params (string desc, decimal qty)[] lines)
    {
        var po = new PurchaseOrder
        {
            UUID         = Guid.NewGuid(),
            PoNumber     = "PO-2025-00001",
            Title        = "Test PO",
            SupplierId   = Guid.NewGuid(),
            SupplierName = "Test Supplier",
            Status       = status,
            IsActive     = true,
            CreatedBy    = 1,
            CreatedDate  = DateTime.UtcNow
        };

        var lineItems = lines.Length > 0 ? lines : new[] { ("Item A", 10m) };
        int lineNo = 1;
        foreach (var (desc, qty) in lineItems)
        {
            po.Lines.Add(new PurchaseOrderLine
            {
                UUID            = Guid.NewGuid(),
                LineNo          = lineNo++,
                ItemDescription = desc,
                UnitOfMeasure   = "PC",
                Quantity        = qty,
                UnitPrice       = 100m,
                LineTotal       = qty * 100m,
                QtyReceived     = 0
            });
        }

        po.TotalAmount = po.Lines.Sum(l => l.LineTotal);
        return po;
    }

    internal static CreateGrnRequest GrnReq(Guid poUuid) => new()
    {
        PoUuid         = poUuid,
        WarehouseUuid  = Guid.NewGuid(),
        ReceivedDate   = DateTime.UtcNow,
        DeliveryNoteNo = "DN-001"
    };
}

// ── WAR-001-TC-1: Draft PO → 422 ─────────────────────────────────────────────

public class CreateGrn_DraftPo_Tests
{
    [Fact]
    public async Task Throws_UnprocessableEntity_When_PO_Is_DRAFT()
    {
        var draftPo = GrnBuild.SentPo(status: "DRAFT");
        var (repo, _, _) = GrnBuild.New(db => db.PurchaseOrders.Add(draftPo));

        var act = () => repo.CreateAsync(GrnBuild.GrnReq(draftPo.UUID), createdBy: 1);

        await act.Should().ThrowAsync<UnprocessableEntityException>()
            .WithMessage("*SENT or PARTIALLY_RECEIVED*");
    }
}

// ── WAR-001-TC-2: Sent PO → GRN created, lines pre-filled ────────────────────

public class CreateGrn_SentPo_Tests
{
    [Fact]
    public async Task Creates_GRN_With_Status_DRAFT_And_Lines_From_PO()
    {
        var sentPo = GrnBuild.SentPo(lines: [("Laptop", 5m), ("Mouse", 10m)]);
        var (repo, wh, _) = GrnBuild.New(db => db.PurchaseOrders.Add(sentPo));

        var grnUuid = await repo.CreateAsync(GrnBuild.GrnReq(sentPo.UUID), createdBy: 1);

        var grn = await wh.Grns.Include(g => g.Lines).FirstAsync(g => g.UUID == grnUuid);
        grn.Status.Should().Be("DRAFT");
        grn.PoUuid.Should().Be(sentPo.UUID);
        grn.Lines.Should().HaveCount(2);
        grn.Lines.Should().Contain(l => l.ItemDescription == "Laptop" && l.QtyOrdered == 5m);
        grn.Lines.Should().Contain(l => l.ItemDescription == "Mouse"  && l.QtyOrdered == 10m);
    }

    [Fact]
    public async Task GRN_Number_Follows_GRN_YYYY_NNNNN_Format()
    {
        var sentPo = GrnBuild.SentPo();
        var (repo, wh, _) = GrnBuild.New(db => db.PurchaseOrders.Add(sentPo));

        var grnUuid = await repo.CreateAsync(GrnBuild.GrnReq(sentPo.UUID), createdBy: 1);

        var grn = await wh.Grns.FirstAsync(g => g.UUID == grnUuid);
        grn.GrnNumber.Should().MatchRegex(@"^GRN-\d{4}-\d{5}$");
    }

    [Fact]
    public async Task GRN_Inherits_TraceId_From_PO()
    {
        var sentPo = GrnBuild.SentPo();
        sentPo.TraceId = Guid.NewGuid();
        var (repo, wh, _) = GrnBuild.New(db => db.PurchaseOrders.Add(sentPo));

        var grnUuid = await repo.CreateAsync(GrnBuild.GrnReq(sentPo.UUID), createdBy: 1);

        var grn = await wh.Grns.FirstAsync(g => g.UUID == grnUuid);
        grn.TraceId.Should().Be(sentPo.TraceId);
    }
}

// ── WAR-001-TC-3: Over-receipt tolerance → 400 ───────────────────────────────

public class UpdateGrnLine_OverReceipt_Tests
{
    [Fact]
    public async Task Throws_BadRequest_When_QtyReceived_Exceeds_103_Percent_Of_Ordered()
    {
        var sentPo = GrnBuild.SentPo(lines: [("Laptop", 10m)]);
        var (repo, wh, _) = GrnBuild.New(db => db.PurchaseOrders.Add(sentPo));

        var grnUuid = await repo.CreateAsync(GrnBuild.GrnReq(sentPo.UUID), createdBy: 1);
        var line    = (await wh.Grns.Include(g => g.Lines).FirstAsync(g => g.UUID == grnUuid)).Lines.First();

        var act = () => repo.UpdateLineAsync(grnUuid, line.UUID, new UpdateGrnLineRequest
        {
            QtyReceived = 10.4m,
            QtyAccepted = 10.4m,
            QtyRejected = 0
        }, modifiedBy: 1);

        await act.Should().ThrowAsync<BadRequestException>()
            .WithMessage("*over-receipt tolerance*");
    }

    [Fact]
    public async Task Does_Not_Throw_When_QtyReceived_Within_Tolerance()
    {
        var sentPo = GrnBuild.SentPo(lines: [("Laptop", 10m)]);
        var (repo, wh, _) = GrnBuild.New(db => db.PurchaseOrders.Add(sentPo));

        var grnUuid = await repo.CreateAsync(GrnBuild.GrnReq(sentPo.UUID), createdBy: 1);
        var line    = (await wh.Grns.Include(g => g.Lines).FirstAsync(g => g.UUID == grnUuid)).Lines.First();

        var act = () => repo.UpdateLineAsync(grnUuid, line.UUID, new UpdateGrnLineRequest
        {
            QtyReceived = 10.3m,
            QtyAccepted = 10.3m,
            QtyRejected = 0
        }, modifiedBy: 1);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Sets_HasVariance_When_Qty_Differs_By_More_Than_5_Percent()
    {
        var sentPo = GrnBuild.SentPo(lines: [("Item", 10m)]);
        var (repo, wh, _) = GrnBuild.New(db => db.PurchaseOrders.Add(sentPo));

        var grnUuid  = await repo.CreateAsync(GrnBuild.GrnReq(sentPo.UUID), createdBy: 1);
        var lineUuid = (await wh.Grns.Include(g => g.Lines).FirstAsync(g => g.UUID == grnUuid)).Lines.First().UUID;

        await repo.UpdateLineAsync(grnUuid, lineUuid, new UpdateGrnLineRequest
        {
            QtyReceived = 9m,
            QtyAccepted = 9m,
            QtyRejected = 0
        }, modifiedBy: 1);

        var updatedLine = await wh.GrnLines.FirstAsync(l => l.UUID == lineUuid);
        updatedLine.HasVariance.Should().BeTrue();
    }
}

// ── WAR-001-TC-4: Submit with partial quantities → IsPartialReceipt = true ───
// SubmitAsync validates DRAFT status and sets IsPartialReceipt;
// status transition to PENDING_APPROVAL is handled by the workflow engine.

public class SubmitGrn_Partial_Tests
{
    [Fact]
    public async Task Sets_IsPartialReceipt_When_Some_Lines_Are_Short()
    {
        var sentPo = GrnBuild.SentPo(lines: [("Laptop", 10m)]);
        var (repo, wh, _) = GrnBuild.New(db => db.PurchaseOrders.Add(sentPo));

        var grnUuid = await repo.CreateAsync(GrnBuild.GrnReq(sentPo.UUID), createdBy: 1);
        var line    = (await wh.Grns.Include(g => g.Lines).FirstAsync(g => g.UUID == grnUuid)).Lines.First();

        await repo.UpdateLineAsync(grnUuid, line.UUID, new UpdateGrnLineRequest
        {
            QtyReceived = 6m, QtyAccepted = 6m, QtyRejected = 0
        }, modifiedBy: 1);

        await repo.SubmitAsync(grnUuid, modifiedBy: 1);

        var grn = await wh.Grns.FirstAsync(g => g.UUID == grnUuid);
        grn.IsPartialReceipt.Should().BeTrue();
    }

    [Fact]
    public async Task IsPartialReceipt_False_When_All_Lines_Fully_Received()
    {
        var sentPo = GrnBuild.SentPo(lines: [("Laptop", 10m)]);
        var (repo, wh, _) = GrnBuild.New(db => db.PurchaseOrders.Add(sentPo));

        var grnUuid = await repo.CreateAsync(GrnBuild.GrnReq(sentPo.UUID), createdBy: 1);
        var line    = (await wh.Grns.Include(g => g.Lines).FirstAsync(g => g.UUID == grnUuid)).Lines.First();

        await repo.UpdateLineAsync(grnUuid, line.UUID, new UpdateGrnLineRequest
        {
            QtyReceived = 10m, QtyAccepted = 10m, QtyRejected = 0
        }, modifiedBy: 1);

        await repo.SubmitAsync(grnUuid, modifiedBy: 1);

        var grn = await wh.Grns.FirstAsync(g => g.UUID == grnUuid);
        grn.IsPartialReceipt.Should().BeFalse();
    }

    [Fact]
    public async Task Throws_UnprocessableEntity_When_GRN_Not_DRAFT()
    {
        var sentPo = GrnBuild.SentPo();
        var (repo, wh, _) = GrnBuild.New(db => db.PurchaseOrders.Add(sentPo));
        var grnUuid = await repo.CreateAsync(GrnBuild.GrnReq(sentPo.UUID), createdBy: 1);

        // Simulate submitted state directly
        var grn = await wh.Grns.FirstAsync(g => g.UUID == grnUuid);
        grn.Status = "PENDING_APPROVAL";
        await wh.SaveChangesAsync();

        var act = () => repo.SubmitAsync(grnUuid, modifiedBy: 1);

        await act.Should().ThrowAsync<UnprocessableEntityException>()
            .WithMessage("*Only DRAFT*");
    }
}
