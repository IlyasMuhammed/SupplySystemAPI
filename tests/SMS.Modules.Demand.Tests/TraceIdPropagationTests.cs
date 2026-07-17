using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Repositories;
using Xunit;

namespace SMS.Modules.Demand.Tests;

// ── Test builder ──────────────────────────────────────────────────────────────

file static class TraceBuild
{
    internal static DemandDbContext NewDb() =>
        new(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    internal static RequisitionRepository PrRepo(DemandDbContext db) => new(db);
    internal static QuotationRepository QuotationRepo(DemandDbContext db) => new(db);
    internal static PurchaseOrderRepository PoRepo(DemandDbContext db) => new(db, NullLogger<PurchaseOrderRepository>.Instance);

    internal static async Task<Guid> ApprovedPrAsync(DemandDbContext db, RequisitionRepository repo)
    {
        var prUuid = await repo.CreateAsync(new CreatePrRequest
        {
            PrTitle       = "Test PR",
            Department    = "IT",
            RequestedDate = DateTime.UtcNow.AddDays(7),
            Priority      = "HIGH",
            Lines =
            [
                new CreatePrLineRequest
                {
                    ItemDescription    = "Laptop",
                    UnitOfMeasure      = "PC",
                    Quantity           = 2m,
                    EstimatedUnitPrice = 500m
                }
            ]
        }, createdBy: 1);

        var pr = await db.PurchaseRequisitions.FirstAsync(p => p.UUID == prUuid);
        pr.Status     = "APPROVED";
        pr.ApprovedBy = 99;
        pr.ApprovedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return prUuid;
    }

    internal static ConvertPrToPoRequest SingleVendorReq() => new()
    {
        SupplierId   = Guid.NewGuid(),
        SupplierName = "Vendor Corp",
        DeliveryDate = DateTime.UtcNow.AddDays(30)
    };
}

// ── TL-006: trace_id propagation across the procurement chain ──────────────────

public class TraceIdPropagation_Tests
{
    [Fact]
    public async Task Pr_Gets_System_Generated_TraceId()
    {
        var db  = TraceBuild.NewDb();
        var prUuid = await TraceBuild.ApprovedPrAsync(db, TraceBuild.PrRepo(db));

        var pr = await db.PurchaseRequisitions.FirstAsync(p => p.UUID == prUuid);
        pr.TraceId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task Quotation_From_Pr_Inherits_Pr_TraceId()
    {
        var db     = TraceBuild.NewDb();
        var prUuid = await TraceBuild.ApprovedPrAsync(db, TraceBuild.PrRepo(db));
        var pr     = await db.PurchaseRequisitions.FirstAsync(p => p.UUID == prUuid);

        var quotationUuid = await TraceBuild.QuotationRepo(db).CreateAsync(new CreateQuotationRequest
        {
            Title      = "RFQ for Laptops",
            SourceType = "PR",
            SourceId   = prUuid,
            Lines      = [new CreateQuotationLineRequest { ItemDescription = "Laptop", Quantity = 2m, UnitOfMeasure = "PC" }]
        }, createdBy: 1);

        var quotation = await db.Quotations.FirstAsync(q => q.UUID == quotationUuid);
        quotation.TraceId.Should().Be(pr.TraceId);
    }

    [Fact]
    public async Task Standalone_Quotation_Gets_Unique_TraceId()
    {
        var db = TraceBuild.NewDb();

        var quotationUuid = await TraceBuild.QuotationRepo(db).CreateAsync(new CreateQuotationRequest
        {
            Title      = "Standalone RFQ",
            SourceType = "STANDALONE",
            Lines      = [new CreateQuotationLineRequest { ItemDescription = "Chairs", Quantity = 10m, UnitOfMeasure = "EA" }]
        }, createdBy: 1);

        var quotation = await db.Quotations.FirstAsync(q => q.UUID == quotationUuid);
        quotation.TraceId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task Po_From_Pr_Inherits_Pr_TraceId_Completing_The_Chain()
    {
        var db     = TraceBuild.NewDb();
        var prRepo = TraceBuild.PrRepo(db);
        var prUuid = await TraceBuild.ApprovedPrAsync(db, prRepo);
        var pr     = await db.PurchaseRequisitions.FirstAsync(p => p.UUID == prUuid);

        var quotationUuid = await TraceBuild.QuotationRepo(db).CreateAsync(new CreateQuotationRequest
        {
            Title      = "RFQ for Laptops",
            SourceType = "PR",
            SourceId   = prUuid,
            Lines      = [new CreateQuotationLineRequest { ItemDescription = "Laptop", Quantity = 2m, UnitOfMeasure = "PC" }]
        }, createdBy: 1);
        var quotation = await db.Quotations.FirstAsync(q => q.UUID == quotationUuid);

        var poUuid = await TraceBuild.PoRepo(db).CreateFromPrAsync(prUuid, TraceBuild.SingleVendorReq(), createdBy: 1);
        var po     = await db.PurchaseOrders.FirstAsync(p => p.UUID == poUuid);

        // Full chain confirmed: PR, Quotation-from-PR, and PO-from-PR all share one trace_id.
        po.TraceId.Should().Be(pr.TraceId);
        quotation.TraceId.Should().Be(pr.TraceId);
    }

    [Fact]
    public async Task Manual_Po_With_No_Pr_Gets_Unique_TraceId()
    {
        var db = TraceBuild.NewDb();

        var poUuid = await TraceBuild.PoRepo(db).CreateAsync(new CreatePoRequest
        {
            Title        = "Manual PO",
            SupplierId   = Guid.NewGuid(),
            SupplierName = "Direct Vendor",
            Lines        = [new CreatePoLineRequest { ItemDescription = "Cables", Quantity = 50m, UnitPrice = 2m }]
        }, createdBy: 1);

        var po = await db.PurchaseOrders.FirstAsync(p => p.UUID == poUuid);
        po.TraceId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task Manual_Po_And_Pr_Po_Have_Different_TraceIds()
    {
        var db     = TraceBuild.NewDb();
        var prUuid = await TraceBuild.ApprovedPrAsync(db, TraceBuild.PrRepo(db));

        var chainPoUuid = await TraceBuild.PoRepo(db).CreateFromPrAsync(prUuid, TraceBuild.SingleVendorReq(), createdBy: 1);
        var manualPoUuid = await TraceBuild.PoRepo(db).CreateAsync(new CreatePoRequest
        {
            Title        = "Manual PO",
            SupplierId   = Guid.NewGuid(),
            SupplierName = "Direct Vendor",
            Lines        = [new CreatePoLineRequest { ItemDescription = "Cables", Quantity = 50m, UnitPrice = 2m }]
        }, createdBy: 1);

        var chainPo  = await db.PurchaseOrders.FirstAsync(p => p.UUID == chainPoUuid);
        var manualPo = await db.PurchaseOrders.FirstAsync(p => p.UUID == manualPoUuid);

        manualPo.TraceId.Should().NotBe(chainPo.TraceId);
    }

    [Fact]
    public async Task Editing_Pr_Does_Not_Change_TraceId()
    {
        var db     = TraceBuild.NewDb();
        var prRepo = TraceBuild.PrRepo(db);
        var prUuid = await TraceBuild.ApprovedPrAsync(db, prRepo);
        var originalTraceId = (await db.PurchaseRequisitions.FirstAsync(p => p.UUID == prUuid)).TraceId;

        // Revert to DRAFT so UpdateAsync's status guard allows the edit.
        var pr = await db.PurchaseRequisitions.FirstAsync(p => p.UUID == prUuid);
        pr.Status = "DRAFT";
        await db.SaveChangesAsync();

        await prRepo.UpdateAsync(prUuid, new PatchPrRequest { PrTitle = "Updated Title" }, modifiedBy: 1);

        var updated = await db.PurchaseRequisitions.FirstAsync(p => p.UUID == prUuid);
        updated.TraceId.Should().Be(originalTraceId);
    }

    [Fact]
    public async Task Second_Po_From_Same_Pr_Also_Inherits_TraceId()
    {
        var db     = TraceBuild.NewDb();
        var prRepo = TraceBuild.PrRepo(db);
        var prUuid = await TraceBuild.ApprovedPrAsync(db, prRepo);
        var pr     = await db.PurchaseRequisitions.FirstAsync(p => p.UUID == prUuid);

        // Simulate the PR remaining convertible for a second split-vendor conversion.
        pr.Status = "PARTIALLY_CONVERTED";
        await db.SaveChangesAsync();

        var poUuid = await TraceBuild.PoRepo(db).CreateFromPrAsync(prUuid, TraceBuild.SingleVendorReq(), createdBy: 1);
        var po     = await db.PurchaseOrders.FirstAsync(p => p.UUID == poUuid);

        po.TraceId.Should().Be(pr.TraceId);
    }
}
