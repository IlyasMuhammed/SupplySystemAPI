using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Services;
using SMS.Modules.Warehouse.Data;
using SMS.Modules.Warehouse.Domain;
using Xunit;

namespace SMS.Modules.Suppliers.Tests;

file static class ScoringBuild
{
    internal static (SupplierScoringService Service, SuppliersDbContext Suppliers, WarehouseDbContext Warehouse,
                      DemandDbContext Demand, FinanceDbContext Finance) New()
    {
        var name = Guid.NewGuid().ToString();

        var suppliers = new SuppliersDbContext(new DbContextOptionsBuilder<SuppliersDbContext>().UseInMemoryDatabase(name).Options);
        var warehouse = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseInMemoryDatabase(name).Options);
        var demand    = new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(name).Options);
        var finance   = new FinanceDbContext(new DbContextOptionsBuilder<FinanceDbContext>().UseInMemoryDatabase(name).Options);

        new ScorecardDataSeeder(suppliers).SeedAsync().GetAwaiter().GetResult();

        var service = new SupplierScoringService(suppliers, warehouse, demand, finance, NullLogger<SupplierScoringService>.Instance);
        return (service, suppliers, warehouse, demand, finance);
    }

    internal static PurchaseOrder SeedPo(DemandDbContext db, Guid supplierId, DateTime? deliveryDate, params decimal[] lineQuantitiesAndPrices)
    {
        // lineQuantitiesAndPrices is read in (qty, price) pairs
        var lines = new List<PurchaseOrderLine>();
        for (var i = 0; i < lineQuantitiesAndPrices.Length; i += 2)
        {
            var qty   = lineQuantitiesAndPrices[i];
            var price = lineQuantitiesAndPrices[i + 1];
            lines.Add(new PurchaseOrderLine
            {
                UUID = Guid.NewGuid(), LineNo = (i / 2) + 1, ItemDescription = "Item",
                Quantity = qty, UnitPrice = price, LineTotal = qty * price
            });
        }

        var po = new PurchaseOrder
        {
            UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), PoNumber = "PO-1", SupplierId = supplierId,
            SupplierName = "Test Supplier", Status = "SENT", TotalAmount = lines.Sum(l => l.LineTotal),
            DeliveryDate = deliveryDate, CreatedBy = 1, CreatedDate = DateTime.UtcNow, Lines = lines
        };
        db.PurchaseOrders.Add(po);
        db.SaveChanges();
        return po;
    }

    internal static Grn SeedGrn(
        WarehouseDbContext db, Guid poUuid, Guid supplierId, DateTime receivedAt,
        bool requiresInspection, bool qcPassed, string? deliveryNoteNo,
        List<(Guid PoLineUuid, decimal QtyOrdered, decimal QtyAccepted, decimal? UnitCost, string? InspectionResult)> lines)
    {
        var grn = new Grn
        {
            UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), GrnNumber = "GRN-1", PoUuid = poUuid, PoNumber = "PO-1",
            SupplierId = supplierId, SupplierName = "Test Supplier", ReceivedAt = receivedAt, Status = "APPROVED",
            RequiresInspection = requiresInspection, QcPassed = qcPassed, DeliveryNoteNo = deliveryNoteNo,
            ReceivedBy = 1, CreatedBy = 1, CreatedDate = receivedAt,
            Lines = lines.Select((l, i) => new GrnLine
            {
                UUID = Guid.NewGuid(), LineNo = i + 1, PoLineUuid = l.PoLineUuid, ItemDescription = "Item",
                QtyOrdered = l.QtyOrdered, QtyReceived = l.QtyAccepted, QtyAccepted = l.QtyAccepted,
                UnitCost = l.UnitCost, InspectionResult = l.InspectionResult
            }).ToList()
        };
        db.Grns.Add(grn);
        db.SaveChanges();
        return grn;
    }

    internal static void SeedInvoice(FinanceDbContext db, Guid grnUuid, Guid poUuid, Guid supplierId, DateTime receivedDate, DateTime dueDate)
    {
        db.Invoices.Add(new Invoice
        {
            UUID = Guid.NewGuid(), TraceId = Guid.NewGuid(), InvoiceNumber = "INV-1", SupplierId = supplierId,
            SupplierName = "Test Supplier", PoUuid = poUuid, PoNumber = "PO-1", GrnUuid = grnUuid,
            InvoiceDate = receivedDate, ReceivedDate = receivedDate, DueDate = dueDate,
            Subtotal = 100m, TaxAmount = 0m, TotalAmount = 100m, CreatedBy = 1, CreatedDate = receivedDate
        });
        db.SaveChanges();
    }
}

public class ScoreGrnAsync_Tests
{
    [Fact]
    public async Task Perfect_Grn_Scores_100_Raw_And_100_Weighted()
    {
        var (service, suppliers, warehouse, demand, finance) = ScoringBuild.New();
        var supplierId = Guid.NewGuid();
        var deliveryDate = new DateTime(2026, 6, 10);

        var po = ScoringBuild.SeedPo(demand, supplierId, deliveryDate, 100m, 10m);
        var poLine = po.Lines.Single();

        var grn = ScoringBuild.SeedGrn(warehouse, po.UUID, supplierId, receivedAt: deliveryDate,
            requiresInspection: true, qcPassed: true, deliveryNoteNo: "DN-1",
            lines:
            [
                (poLine.UUID, 100m, 100m, 10m, "Pass")
            ]);

        ScoringBuild.SeedInvoice(finance, grn.UUID, po.UUID, supplierId, receivedDate: deliveryDate, dueDate: deliveryDate.AddDays(30));

        await service.ScoreGrnAsync(grn.UUID);

        var score = await suppliers.GrnScoreDetails.SingleAsync(s => s.GrnId == grn.UUID);
        score.DeliveryPoints.Should().Be(25m);
        score.QuantityPoints.Should().Be(25m);
        score.QualityPoints.Should().Be(25m);
        score.PricePoints.Should().Be(15m);
        score.DocumentationPoints.Should().Be(10m);
        score.TotalRawScore.Should().Be(100m);
        score.WeightedScore.Should().Be(100m);
    }

    [Fact]
    public async Task Late_Partial_Grn_Scores_40_Raw_Matching_Ticket_Example()
    {
        var (service, suppliers, warehouse, demand, finance) = ScoringBuild.New();
        var supplierId = Guid.NewGuid();
        var deliveryDate = new DateTime(2026, 6, 1);
        var receivedAt   = deliveryDate.AddDays(10); // 10 days late -> 10 delivery points

        // 4 PO lines, 25 units each ordered @ price 100, total ordered = 100
        var po = ScoringBuild.SeedPo(demand, supplierId, deliveryDate,
            25m, 100m, 25m, 100m, 25m, 100m, 25m, 100m);
        var poLines = po.Lines.OrderBy(l => l.LineNo).ToList();

        // Cumulative accepted = 25+25+25+10 = 85 -> 85% of 100 ordered -> 10 quantity points
        // Inspection: 3 Pass, 1 Fail -> 75% -> 10 quality points
        // Price: all lines 8% above PO price (100 -> 108) -> avg variance 8% -> 6 price points
        var grn = ScoringBuild.SeedGrn(warehouse, po.UUID, supplierId, receivedAt,
            requiresInspection: true, qcPassed: false /* certificates check fails */, deliveryNoteNo: "DN-1" /* delivery note check passes */,
            lines:
            [
                (poLines[0].UUID, 25m, 25m, 108m, "Pass"),
                (poLines[1].UUID, 25m, 25m, 108m, "Pass"),
                (poLines[2].UUID, 25m, 25m, 108m, "Pass"),
                (poLines[3].UUID, 25m, 10m, 108m, "Fail")
            ]);

        // No invoice seeded -> "invoice within terms" check fails.
        // Documentation checks: invoiceWithinTerms=false, deliveryNoteAttached=true, certificatesPresent=false -> 1 of 3 -> 4 points.

        await service.ScoreGrnAsync(grn.UUID);

        var score = await suppliers.GrnScoreDetails.SingleAsync(s => s.GrnId == grn.UUID);
        score.DeliveryPoints.Should().Be(10m);
        score.QuantityPoints.Should().Be(10m);
        score.QualityPoints.Should().Be(10m);
        score.PricePoints.Should().Be(6m);
        score.DocumentationPoints.Should().Be(4m);
        score.TotalRawScore.Should().Be(40m);
    }

    [Fact]
    public async Task Grn_Not_Requiring_Inspection_Gets_Default_Quality_Score_Of_20()
    {
        var (service, suppliers, warehouse, demand, _) = ScoringBuild.New();
        var supplierId = Guid.NewGuid();
        var po = ScoringBuild.SeedPo(demand, supplierId, new DateTime(2026, 6, 1), 10m, 5m);
        var poLine = po.Lines.Single();

        var grn = ScoringBuild.SeedGrn(warehouse, po.UUID, supplierId, new DateTime(2026, 6, 1),
            requiresInspection: false, qcPassed: false, deliveryNoteNo: null,
            lines: [(poLine.UUID, 10m, 10m, 5m, null)]);

        await service.ScoreGrnAsync(grn.UUID);

        var score = await suppliers.GrnScoreDetails.SingleAsync(s => s.GrnId == grn.UUID);
        score.QualityPoints.Should().Be(20m);
    }

    [Fact]
    public async Task Po_With_No_Expected_Delivery_Date_Gets_Default_Delivery_Score_Of_20()
    {
        var (service, suppliers, warehouse, demand, _) = ScoringBuild.New();
        var supplierId = Guid.NewGuid();
        var po = ScoringBuild.SeedPo(demand, supplierId, deliveryDate: null, 10m, 5m);
        var poLine = po.Lines.Single();

        var grn = ScoringBuild.SeedGrn(warehouse, po.UUID, supplierId, new DateTime(2026, 6, 1),
            requiresInspection: false, qcPassed: false, deliveryNoteNo: null,
            lines: [(poLine.UUID, 10m, 10m, 5m, null)]);

        await service.ScoreGrnAsync(grn.UUID);

        var score = await suppliers.GrnScoreDetails.SingleAsync(s => s.GrnId == grn.UUID);
        score.DeliveryPoints.Should().Be(20m);
    }

    [Fact]
    public async Task Scoring_A_Nonexistent_Grn_Does_Not_Throw()
    {
        var (service, suppliers, _, _, _) = ScoringBuild.New();

        var act = () => service.ScoreGrnAsync(Guid.NewGuid());

        await act.Should().NotThrowAsync();
        (await suppliers.GrnScoreDetails.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Weighted_Score_Adjusts_When_Weights_Change_On_Rescoring_Same_Grn()
    {
        var (service, suppliers, warehouse, demand, finance) = ScoringBuild.New();
        var supplierId = Guid.NewGuid();
        var deliveryDate = new DateTime(2026, 6, 10);

        var po = ScoringBuild.SeedPo(demand, supplierId, deliveryDate, 100m, 10m);
        var poLine = po.Lines.Single();

        var grn = ScoringBuild.SeedGrn(warehouse, po.UUID, supplierId, deliveryDate,
            requiresInspection: true, qcPassed: true, deliveryNoteNo: "DN-1",
            lines: [(poLine.UUID, 100m, 100m, 10m, "Pass")]);
        ScoringBuild.SeedInvoice(finance, grn.UUID, po.UUID, supplierId, deliveryDate, deliveryDate.AddDays(30));

        await service.ScoreGrnAsync(grn.UUID);
        var firstScore = await suppliers.GrnScoreDetails.SingleAsync(s => s.GrnId == grn.UUID);
        firstScore.WeightedScore.Should().Be(100m); // perfect GRN scores full marks regardless of weight split

        // Change weights so DELIVERY is worth much less relative to its max, keeping sum at 100.
        var deliveryDim = await suppliers.ScorecardDimensionWeights.SingleAsync(d => d.DimensionCode == "DELIVERY");
        var quantityDim = await suppliers.ScorecardDimensionWeights.SingleAsync(d => d.DimensionCode == "QUANTITY");
        deliveryDim.WeightPercentage = 5m;
        quantityDim.WeightPercentage = 45m; // 5+45+25+15+10 = 100
        await suppliers.SaveChangesAsync();

        await service.ScoreGrnAsync(grn.UUID);

        var rescored = await suppliers.GrnScoreDetails.SingleAsync(s => s.GrnId == grn.UUID);
        // Still a perfect GRN (25/25 on every dimension) so weighted total is still 100 even after
        // reshuffling weights between dimensions — but the row must have been updated in place, not duplicated.
        rescored.WeightedScore.Should().Be(100m);
        (await suppliers.GrnScoreDetails.CountAsync(s => s.GrnId == grn.UUID)).Should().Be(1);
    }

    [Fact]
    public async Task Weighted_Score_Reflects_Weight_Change_For_An_Imperfect_Grn()
    {
        var (service, suppliers, warehouse, demand, _) = ScoringBuild.New();
        var supplierId = Guid.NewGuid();
        var deliveryDate = new DateTime(2026, 6, 1);
        var receivedAt   = deliveryDate.AddDays(20); // >=15 late -> 5 delivery points (worst tier)

        var po = ScoringBuild.SeedPo(demand, supplierId, deliveryDate, 100m, 10m);
        var poLine = po.Lines.Single();

        var grn = ScoringBuild.SeedGrn(warehouse, po.UUID, supplierId, receivedAt,
            requiresInspection: true, qcPassed: true, deliveryNoteNo: "DN-1",
            lines: [(poLine.UUID, 100m, 100m, 10m, "Pass")]);

        await service.ScoreGrnAsync(grn.UUID);
        var before = await suppliers.GrnScoreDetails.AsNoTracking().SingleAsync(s => s.GrnId == grn.UUID);
        before.DeliveryPoints.Should().Be(5m); // worst delivery tier, everything else perfect
        var beforeWeighted = before.WeightedScore;

        var deliveryDim = await suppliers.ScorecardDimensionWeights.SingleAsync(d => d.DimensionCode == "DELIVERY");
        var quantityDim = await suppliers.ScorecardDimensionWeights.SingleAsync(d => d.DimensionCode == "QUANTITY");
        deliveryDim.WeightPercentage = 50m;
        quantityDim.WeightPercentage = 0m; // 50+0+25+15+10 = 100
        await suppliers.SaveChangesAsync();

        await service.ScoreGrnAsync(grn.UUID);
        var after = await suppliers.GrnScoreDetails.AsNoTracking().SingleAsync(s => s.GrnId == grn.UUID);

        // Delivery's poor 5/25 attainment now carries 50% weight instead of 25%, so the weighted
        // total must drop compared to the first scoring.
        after.WeightedScore.Should().BeLessThan(beforeWeighted);
    }
}
