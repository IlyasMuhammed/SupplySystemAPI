using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Domain;
using SMS.Modules.Suppliers.Services;
using SMS.Modules.Warehouse.Data;
using SMS.Modules.Warehouse.Domain;
using Xunit;

namespace SMS.Modules.Suppliers.Tests;

file static class DashboardBuild
{
    internal static (ScorecardDashboardService Service, SuppliersDbContext Suppliers, WarehouseDbContext Warehouse) New()
    {
        var name = Guid.NewGuid().ToString();
        var suppliers = new SuppliersDbContext(new DbContextOptionsBuilder<SuppliersDbContext>().UseInMemoryDatabase(name).Options);
        var warehouse = new WarehouseDbContext(new DbContextOptionsBuilder<WarehouseDbContext>().UseInMemoryDatabase(name).Options);
        return (new ScorecardDashboardService(suppliers, warehouse), suppliers, warehouse);
    }

    internal static Supplier SeedSupplier(SuppliersDbContext db, string name)
    {
        var s = new Supplier
        {
            UUID = Guid.NewGuid(), SupplierName = name, SupplierCode = $"SUP-{Guid.NewGuid():N}"[..10],
            Status = "APPROVED", IsActive = true, CreatedBy = 1, CreatedDate = DateTime.UtcNow
        };
        db.Suppliers.Add(s);
        db.SaveChanges();
        return s;
    }

    internal static void SeedSnapshot(
        SuppliersDbContext db, Guid supplierId, DateTime periodStart, DateTime periodEnd,
        decimal totalScore, int grnCount = 1, string? trend = null, string grade = "B")
    {
        db.SupplierScoreSnapshots.Add(new SupplierScoreSnapshot
        {
            UUID = Guid.NewGuid(), SupplierId = supplierId, PeriodStart = periodStart, PeriodEnd = periodEnd,
            DeliveryScore = totalScore, QuantityScore = totalScore, QualityScore = totalScore,
            PriceScore = totalScore, DocumentationScore = totalScore,
            TotalScore = totalScore, Grade = grade, Trend = trend, GrnCount = grnCount,
            CreatedBy = 1, CreatedDate = DateTime.UtcNow
        });
        db.SaveChanges();
    }
}

public class GetRankingAsync_Tests
{
    [Fact]
    public async Task Ranks_Suppliers_By_Composite_Score_Descending()
    {
        var (svc, db, _) = DashboardBuild.New();
        var low  = DashboardBuild.SeedSupplier(db, "Low Scorer");
        var high = DashboardBuild.SeedSupplier(db, "High Scorer");

        var start = new DateTime(2026, 5, 1);
        var end   = new DateTime(2026, 8, 1);
        DashboardBuild.SeedSnapshot(db, low.UUID, new DateTime(2026, 7, 1), new DateTime(2026, 8, 1), 60m);
        DashboardBuild.SeedSnapshot(db, high.UUID, new DateTime(2026, 7, 1), new DateTime(2026, 8, 1), 95m);

        var result = await svc.GetRankingAsync(start, end);

        result.Suppliers.Should().HaveCount(2);
        result.Suppliers[0].SupplierName.Should().Be("High Scorer");
        result.Suppliers[0].Rank.Should().Be(1);
        result.Suppliers[1].SupplierName.Should().Be("Low Scorer");
        result.Suppliers[1].Rank.Should().Be(2);
    }

    [Fact]
    public async Task Excludes_Snapshots_Outside_The_Requested_Window()
    {
        var (svc, db, _) = DashboardBuild.New();
        var supplier = DashboardBuild.SeedSupplier(db, "Acme");

        DashboardBuild.SeedSnapshot(db, supplier.UUID, new DateTime(2026, 1, 1), new DateTime(2026, 2, 1), 50m);

        var result = await svc.GetRankingAsync(new DateTime(2026, 6, 1), new DateTime(2026, 7, 1));

        result.Suppliers.Should().BeEmpty();
    }

    [Fact]
    public async Task Grn_Weighted_Average_Combines_Multiple_Snapshots_In_The_Window()
    {
        var (svc, db, _) = DashboardBuild.New();
        var supplier = DashboardBuild.SeedSupplier(db, "Acme");
        var windowStart = new DateTime(2026, 6, 1);
        var windowEnd   = new DateTime(2026, 8, 1);

        // Month 1: score 80 over 3 GRNs. Month 2: score 100 over 1 GRN.
        // Weighted average = (80*3 + 100*1) / 4 = 85
        DashboardBuild.SeedSnapshot(db, supplier.UUID, new DateTime(2026, 6, 1), new DateTime(2026, 7, 1), 80m, grnCount: 3);
        DashboardBuild.SeedSnapshot(db, supplier.UUID, new DateTime(2026, 7, 1), new DateTime(2026, 8, 1), 100m, grnCount: 1);

        var result = await svc.GetRankingAsync(windowStart, windowEnd);

        var row = result.Suppliers.Single();
        row.CompositeScore.Should().Be(85m);
        row.GrnCount.Should().Be(4);
        row.Grade.Should().Be("B");
    }

    [Fact]
    public async Task Trend_Comes_From_The_Most_Recent_Snapshot_In_The_Window()
    {
        var (svc, db, _) = DashboardBuild.New();
        var supplier = DashboardBuild.SeedSupplier(db, "Acme");

        DashboardBuild.SeedSnapshot(db, supplier.UUID, new DateTime(2026, 6, 1), new DateTime(2026, 7, 1), 70m, trend: "STABLE");
        DashboardBuild.SeedSnapshot(db, supplier.UUID, new DateTime(2026, 7, 1), new DateTime(2026, 8, 1), 90m, trend: "IMPROVING");

        var result = await svc.GetRankingAsync(new DateTime(2026, 6, 1), new DateTime(2026, 8, 1));

        result.Suppliers.Single().Trend.Should().Be("IMPROVING");
    }

    [Fact]
    public async Task ScoreDelta_Compares_Two_Most_Recent_Snapshots_In_The_Window()
    {
        var (svc, db, _) = DashboardBuild.New();
        var supplier = DashboardBuild.SeedSupplier(db, "Acme");

        DashboardBuild.SeedSnapshot(db, supplier.UUID, new DateTime(2026, 6, 1), new DateTime(2026, 7, 1), 70m);
        DashboardBuild.SeedSnapshot(db, supplier.UUID, new DateTime(2026, 7, 1), new DateTime(2026, 8, 1), 90m);

        var result = await svc.GetRankingAsync(new DateTime(2026, 6, 1), new DateTime(2026, 8, 1));

        result.Suppliers.Single().ScoreDelta.Should().Be(20m);
    }
}

public class GetSupplierDetailAsync_Tests
{
    [Fact]
    public async Task Returns_Null_For_A_Supplier_Never_Scored()
    {
        var (svc, db, _) = DashboardBuild.New();
        var supplier = DashboardBuild.SeedSupplier(db, "Never Scored Co");

        var result = await svc.GetSupplierDetailAsync(supplier.UUID);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Returns_Latest_Snapshot_Breakdown_Trend_History_And_Grn_Scores_With_Grn_Numbers()
    {
        var (svc, db, warehouse) = DashboardBuild.New();
        var supplier = DashboardBuild.SeedSupplier(db, "Acme");

        DashboardBuild.SeedSnapshot(db, supplier.UUID, new DateTime(2026, 4, 1), new DateTime(2026, 5, 1), 70m);
        DashboardBuild.SeedSnapshot(db, supplier.UUID, new DateTime(2026, 5, 1), new DateTime(2026, 6, 1), 75m);
        DashboardBuild.SeedSnapshot(db, supplier.UUID, new DateTime(2026, 6, 1), new DateTime(2026, 7, 1), 80m);
        DashboardBuild.SeedSnapshot(db, supplier.UUID, new DateTime(2026, 7, 1), new DateTime(2026, 8, 1), 92m, trend: "IMPROVING", grade: "A");

        var grnId = Guid.NewGuid();
        warehouse.Grns.Add(new Grn
        {
            UUID = grnId, TraceId = Guid.NewGuid(), GrnNumber = "GRN-2026-00042", PoUuid = Guid.NewGuid(), PoNumber = "PO-1",
            SupplierId = supplier.UUID, SupplierName = "Acme", ReceivedAt = DateTime.UtcNow, Status = "APPROVED",
            ReceivedBy = 1, CreatedBy = 1, CreatedDate = DateTime.UtcNow
        });
        warehouse.SaveChanges();

        db.GrnScoreDetails.Add(new GrnScoreDetail
        {
            GrnId = grnId, SupplierId = supplier.UUID, DeliveryPoints = 25m, QuantityPoints = 25m, QualityPoints = 25m,
            PricePoints = 15m, DocumentationPoints = 10m, TotalRawScore = 100m, WeightedScore = 100m, ScoredAt = DateTime.UtcNow
        });
        db.SaveChanges();

        var result = await svc.GetSupplierDetailAsync(supplier.UUID);

        result.Should().NotBeNull();
        result!.CompositeScore.Should().Be(92m);
        result.Grade.Should().Be("A");
        result.Trend.Should().Be("IMPROVING");
        result.TrendHistory.Should().HaveCount(4);
        result.TrendHistory[0].CompositeScore.Should().Be(70m); // oldest first
        result.TrendHistory[3].CompositeScore.Should().Be(92m); // most recent last

        result.GrnScores.Should().ContainSingle();
        result.GrnScores[0].GrnNumber.Should().Be("GRN-2026-00042");
        result.GrnScores[0].WeightedScore.Should().Be(100m);
    }
}

public class GetScoreSummaryAsync_Tests
{
    [Fact]
    public async Task Returns_Null_Grade_For_A_Supplier_Never_Scored()
    {
        var (svc, db, _) = DashboardBuild.New();
        var supplier = DashboardBuild.SeedSupplier(db, "Never Scored Co");

        var result = await svc.GetScoreSummaryAsync(supplier.UUID);

        result.Grade.Should().BeNull();
        result.CompositeScore.Should().BeNull();
    }

    [Fact]
    public async Task Returns_Grade_And_Composite_From_The_Most_Recent_Snapshot()
    {
        var (svc, db, _) = DashboardBuild.New();
        var supplier = DashboardBuild.SeedSupplier(db, "Acme");

        DashboardBuild.SeedSnapshot(db, supplier.UUID, new DateTime(2026, 5, 1), new DateTime(2026, 6, 1), 90m, grade: "A");
        DashboardBuild.SeedSnapshot(db, supplier.UUID, new DateTime(2026, 6, 1), new DateTime(2026, 7, 1), 55m, grade: "F");

        var result = await svc.GetScoreSummaryAsync(supplier.UUID);

        result.Grade.Should().Be("F");
        result.CompositeScore.Should().Be(55m);
    }
}
