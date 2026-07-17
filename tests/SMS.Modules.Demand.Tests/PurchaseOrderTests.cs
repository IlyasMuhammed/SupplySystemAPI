using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Repositories;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Demand.Tests;

// ── Test builder ──────────────────────────────────────────────────────────────

file static class PoBuild
{
    internal static (PurchaseOrderRepository repo, DemandDbContext db) New(Action<DemandDbContext>? seed = null)
    {
        var opts = new DbContextOptionsBuilder<DemandDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new DemandDbContext(opts);
        seed?.Invoke(db);
        db.SaveChanges();
        return (new PurchaseOrderRepository(db, NullLogger<PurchaseOrderRepository>.Instance), db);
    }

    // Creates an approved PR and returns its UUID + lines in LineNo order.
    // Status is set directly in DB — workflow engine handles submit/approve in production.
    internal static async Task<(Guid prUuid, Domain.PrLine[] prLines)> ApprovedPrAsync(
        DemandDbContext db,
        params CreatePrLineRequest[] lines)
    {
        var repo   = new RequisitionRepository(db);
        var prUuid = await repo.CreateAsync(new CreatePrRequest
        {
            PrTitle       = "Test PR",
            Department    = "IT",
            RequestedDate = DateTime.UtcNow.AddDays(7),
            Priority      = "HIGH",
            Lines         = lines.Length > 0 ? [.. lines] : [PrLine()]
        }, createdBy: 1);

        var pr = await db.PurchaseRequisitions
            .Include(p => p.Lines)
            .FirstAsync(p => p.UUID == prUuid);

        pr.Status     = "APPROVED";
        pr.ApprovedBy = 99;
        pr.ApprovedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return (prUuid, pr.Lines.OrderBy(l => l.LineNo).ToArray());
    }

    internal static CreatePrLineRequest PrLine(
        string desc             = "Laptop",
        decimal qty             = 2m,
        decimal price           = 500m,
        bool requiresQuotation  = false) => new()
    {
        ItemDescription    = desc,
        UnitOfMeasure      = "PC",
        Quantity           = qty,
        EstimatedUnitPrice = price,
        RequiresQuotation  = requiresQuotation
    };

    internal static ConvertPrToPoRequest SingleVendorReq(
        Guid? supplierId    = null,
        string supplierName = "Vendor Corp") => new()
    {
        SupplierId   = supplierId ?? Guid.NewGuid(),
        SupplierName = supplierName,
        DeliveryDate = DateTime.UtcNow.AddDays(30)
    };
}

// ── AC-1: Single-vendor conversion creates one PO with all PR lines ───────────

public class ConvertSingleVendor_Tests
{
    [Fact]
    public async Task Creates_One_PO_With_All_PR_Lines_Mapped()
    {
        var (repo, db) = PoBuild.New();
        var (prUuid, _) = await PoBuild.ApprovedPrAsync(db,
            PoBuild.PrLine("Laptop", 2m, 500m),
            PoBuild.PrLine("Mouse",  5m,  30m));

        var poUuid = await repo.CreateFromPrAsync(prUuid, PoBuild.SingleVendorReq(), createdBy: 1);

        var po = await db.PurchaseOrders
            .Include(p => p.Lines)
            .Include(p => p.PrLinks)
            .FirstAsync(p => p.UUID == poUuid);

        po.Lines.Should().HaveCount(2);
        po.Lines.Should().Contain(l => l.ItemDescription == "Laptop");
        po.Lines.Should().Contain(l => l.ItemDescription == "Mouse");
        po.PrLinks.Should().HaveCount(1);
        po.PrLinks.Single().PrUuid.Should().Be(prUuid);
    }

    [Fact]
    public async Task PO_TotalAmount_Equals_Sum_Of_Lines()
    {
        var (repo, db) = PoBuild.New();
        var (prUuid, _) = await PoBuild.ApprovedPrAsync(db,
            PoBuild.PrLine("Laptop", 2m, 500m),
            PoBuild.PrLine("Mouse",  5m,  30m));

        var poUuid = await repo.CreateFromPrAsync(prUuid, PoBuild.SingleVendorReq(), createdBy: 1);

        var po = await db.PurchaseOrders
            .Include(p => p.Lines)
            .FirstAsync(p => p.UUID == poUuid);

        po.TotalAmount.Should().Be(2m * 500m + 5m * 30m);
    }
}

// ── AC-2: Split conversion with 3 vendors creates 3 POs ──────────────────────

public class ConvertSplit_Tests
{
    [Fact]
    public async Task Creates_N_POs_One_Per_Vendor()
    {
        var (repo, db) = PoBuild.New();
        var (prUuid, prLines) = await PoBuild.ApprovedPrAsync(db,
            PoBuild.PrLine("Laptop"),
            PoBuild.PrLine("Mouse"),
            PoBuild.PrLine("Keyboard"));

        var v1 = Guid.NewGuid();
        var v2 = Guid.NewGuid();
        var v3 = Guid.NewGuid();

        var req = new ConvertPrSplitRequest
        {
            Lines =
            [
                new PrLineVendorAssignment { PrLineUuid = prLines[0].UUID, SupplierId = v1, SupplierName = "Vendor 1" },
                new PrLineVendorAssignment { PrLineUuid = prLines[1].UUID, SupplierId = v2, SupplierName = "Vendor 2" },
                new PrLineVendorAssignment { PrLineUuid = prLines[2].UUID, SupplierId = v3, SupplierName = "Vendor 3" }
            ]
        };

        var poUuids = await repo.CreateFromPrSplitAsync(prUuid, req, createdBy: 1);

        poUuids.Should().HaveCount(3);
        var pos = await db.PurchaseOrders.Include(p => p.Lines).ToListAsync();
        pos.Should().HaveCount(3);
        foreach (var po in pos)
            po.Lines.Should().HaveCount(1, "each vendor gets exactly their assigned line");
    }

    [Fact]
    public async Task Each_PO_Contains_The_Correct_Subset_Of_Lines()
    {
        var (repo, db) = PoBuild.New();
        var (prUuid, prLines) = await PoBuild.ApprovedPrAsync(db,
            PoBuild.PrLine("Laptop"),
            PoBuild.PrLine("Mouse"));

        var supplierId = Guid.NewGuid();

        var req = new ConvertPrSplitRequest
        {
            Lines =
            [
                new PrLineVendorAssignment { PrLineUuid = prLines[0].UUID, SupplierId = supplierId, SupplierName = "Vendor X" },
                new PrLineVendorAssignment { PrLineUuid = prLines[1].UUID, SupplierId = supplierId, SupplierName = "Vendor X" }
            ]
        };

        var poUuids = await repo.CreateFromPrSplitAsync(prUuid, req, createdBy: 1);

        // Same supplier → one PO with both lines
        poUuids.Should().HaveCount(1);
        var po = await db.PurchaseOrders.Include(p => p.Lines).FirstAsync(p => p.UUID == poUuids[0]);
        po.Lines.Should().HaveCount(2);
    }
}

// ── AC-3: Converting a DRAFT PR returns 422 ───────────────────────────────────

public class ConvertValidation_Tests
{
    [Fact]
    public async Task Throws_UnprocessableEntity_When_PR_Is_DRAFT()
    {
        var (repo, db) = PoBuild.New();
        var prRepo  = new RequisitionRepository(db);
        var prUuid  = await prRepo.CreateAsync(new CreatePrRequest
        {
            PrTitle       = "Test PR",
            Department    = "IT",
            RequestedDate = DateTime.UtcNow.AddDays(7),
            Priority      = "MEDIUM",
            Lines         = [PoBuild.PrLine()]
        }, createdBy: 1);
        // Intentionally left as DRAFT — not submitted or approved

        var act = () => repo.CreateFromPrAsync(prUuid, PoBuild.SingleVendorReq(), createdBy: 1);

        await act.Should().ThrowAsync<UnprocessableEntityException>()
            .WithMessage("*Only APPROVED*");
    }

    // ── AC-4: requires_quotation line without awarded quotation blocks conversion

    [Fact]
    public async Task Throws_BadRequest_When_RequiresQuotation_Line_Has_No_Awarded_Quotation()
    {
        var (repo, db) = PoBuild.New();
        var (prUuid, _) = await PoBuild.ApprovedPrAsync(db,
            PoBuild.PrLine(requiresQuotation: true));

        var act = () => repo.CreateFromPrAsync(prUuid, PoBuild.SingleVendorReq(), createdBy: 1);

        await act.Should().ThrowAsync<BadRequestException>()
            .WithMessage("*require an awarded quotation*");
    }

    [Fact]
    public async Task Succeeds_When_RequiresQuotation_Line_Has_Awarded_Quotation()
    {
        var (repo, db) = PoBuild.New();
        var (prUuid, prLines) = await PoBuild.ApprovedPrAsync(db,
            PoBuild.PrLine(requiresQuotation: true));

        // Award a quotation linked to this PR line → sets QuotationStatus = "AWARDED"
        var qRepo  = new QuotationRepository(db);
        var qUuid  = await qRepo.CreateAsync(new CreateQuotationRequest
        {
            Title      = "RFQ",
            SourceType = "PR",
            SourceId   = prUuid,
            Lines      =
            [
                new CreateQuotationLineRequest
                {
                    SourcePrLineUuid = prLines[0].UUID,
                    ItemDescription  = prLines[0].ItemDescription,
                    Quantity         = prLines[0].Quantity
                }
            ]
        }, createdBy: 1);
        await qRepo.SendAsync(qUuid, new SendQuotationRequest
        {
            Suppliers = [new InviteSupplierRequest { SupplierId = Guid.NewGuid(), SupplierName = "Vendor" }]
        }, modifiedBy: 1);

        var lineUuid    = (await db.QuotationLines.FirstAsync()).UUID;
        var responseUuid = await qRepo.RecordResponseAsync(qUuid, new RecordVendorResponseRequest
        {
            SupplierId   = Guid.NewGuid(),
            SupplierName = "Vendor",
            Lines        = [new VendorResponseLineRequest { QuotationLineUuid = lineUuid, NetUnitPrice = 450m, Quantity = 2m }]
        }, createdBy: 1);
        await qRepo.AwardAsync(qUuid, new AwardQuotationRequest { VendorResponseUuid = responseUuid }, awardedBy: 99);

        // Conversion should now succeed
        var act = () => repo.CreateFromPrAsync(prUuid, PoBuild.SingleVendorReq(), createdBy: 1);
        await act.Should().NotThrowAsync();
    }
}

// ── AC-5: Multi-PR PO creation links all pr_ids and copies their lines ────────

public class MultiPrPo_Tests
{
    [Fact]
    public async Task Creates_PO_Linked_To_Multiple_PRs_With_All_Lines()
    {
        var (repo, db) = PoBuild.New();
        var (prUuid1, _) = await PoBuild.ApprovedPrAsync(db, PoBuild.PrLine("Laptop"));
        var (prUuid2, _) = await PoBuild.ApprovedPrAsync(db, PoBuild.PrLine("Mouse"));

        var poUuid = await repo.CreateAsync(new CreatePoRequest
        {
            SupplierId   = Guid.NewGuid(),
            SupplierName = "Big Vendor",
            PrIds        = [prUuid1, prUuid2]
        }, createdBy: 1);

        var po = await db.PurchaseOrders
            .Include(p => p.Lines)
            .Include(p => p.PrLinks)
            .FirstAsync(p => p.UUID == poUuid);

        po.Lines.Should().HaveCount(2);
        po.PrLinks.Should().HaveCount(2);
        po.PrLinks.Select(l => l.PrUuid).Should().Contain(prUuid1);
        po.PrLinks.Select(l => l.PrUuid).Should().Contain(prUuid2);
    }
}

// ── AC-6: PR line status FULLY_CONVERTED after conversion ────────────────────

public class PrLineStatusTracking_Tests
{
    [Fact]
    public async Task PR_Line_Status_Is_FULLY_CONVERTED_After_Single_Vendor_Conversion()
    {
        var (repo, db) = PoBuild.New();
        var (prUuid, prLines) = await PoBuild.ApprovedPrAsync(db, PoBuild.PrLine());

        await repo.CreateFromPrAsync(prUuid, PoBuild.SingleVendorReq(), createdBy: 1);

        var line = await db.PrLines.FirstAsync(l => l.UUID == prLines[0].UUID);
        line.LineStatus.Should().Be("FULLY_CONVERTED");
    }

    [Fact]
    public async Task PR_Status_Is_FULLY_CONVERTED_After_All_Lines_Converted()
    {
        var (repo, db) = PoBuild.New();
        var (prUuid, _) = await PoBuild.ApprovedPrAsync(db,
            PoBuild.PrLine("Laptop"),
            PoBuild.PrLine("Mouse"));

        await repo.CreateFromPrAsync(prUuid, PoBuild.SingleVendorReq(), createdBy: 1);

        var pr = await db.PurchaseRequisitions.FirstAsync(p => p.UUID == prUuid);
        pr.Status.Should().Be("FULLY_CONVERTED");
    }

    // ── AC-7: PR status PARTIALLY_CONVERTED when only some lines are converted

    [Fact]
    public async Task PR_Status_Is_PARTIALLY_CONVERTED_When_Only_Some_Lines_Converted()
    {
        var (repo, db) = PoBuild.New();
        var (prUuid, prLines) = await PoBuild.ApprovedPrAsync(db,
            PoBuild.PrLine("Laptop"),
            PoBuild.PrLine("Mouse"));

        // Only assign one of the two lines to a vendor
        var req = new ConvertPrSplitRequest
        {
            Lines =
            [
                new PrLineVendorAssignment
                {
                    PrLineUuid   = prLines[0].UUID,
                    SupplierId   = Guid.NewGuid(),
                    SupplierName = "Vendor 1"
                }
            ]
        };

        await repo.CreateFromPrSplitAsync(prUuid, req, createdBy: 1);

        var pr = await db.PurchaseRequisitions
            .Include(p => p.Lines)
            .FirstAsync(p => p.UUID == prUuid);

        pr.Status.Should().Be("PARTIALLY_CONVERTED");
        pr.Lines.Single(l => l.UUID == prLines[0].UUID).LineStatus.Should().Be("FULLY_CONVERTED");
        pr.Lines.Single(l => l.UUID == prLines[1].UUID).LineStatus.Should().NotBe("FULLY_CONVERTED");
    }
}
