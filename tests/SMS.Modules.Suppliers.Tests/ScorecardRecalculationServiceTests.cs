using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Domain;
using SMS.Modules.Suppliers.Services;
using Xunit;

namespace SMS.Modules.Suppliers.Tests;

file static class RecalcBuild
{
    internal static SuppliersDbContext NewDb() =>
        new(new DbContextOptionsBuilder<SuppliersDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    internal static Supplier SeedSupplier(SuppliersDbContext db, bool isActive = true)
    {
        var s = new Supplier
        {
            UUID = Guid.NewGuid(), SupplierName = "Test Supplier", SupplierCode = $"SUP-{Guid.NewGuid():N}"[..10],
            Status = "APPROVED", IsActive = isActive, CreatedBy = 1, CreatedDate = DateTime.UtcNow
        };
        db.Suppliers.Add(s);
        db.SaveChanges();
        return s;
    }

    internal static void SeedGrnScore(SuppliersDbContext db, Guid supplierId, decimal weightedScore, DateTime scoredAt)
    {
        db.GrnScoreDetails.Add(new GrnScoreDetail
        {
            GrnId = Guid.NewGuid(), SupplierId = supplierId,
            DeliveryPoints = weightedScore, QuantityPoints = weightedScore, QualityPoints = weightedScore,
            PricePoints = weightedScore, DocumentationPoints = weightedScore,
            TotalRawScore = weightedScore, WeightedScore = weightedScore, ScoredAt = scoredAt
        });
        db.SaveChanges();
    }

    internal static SupplierScoreSnapshot SeedSnapshot(SuppliersDbContext db, Guid supplierId, DateTime periodStart, DateTime periodEnd, decimal totalScore)
    {
        var snap = new SupplierScoreSnapshot
        {
            UUID = Guid.NewGuid(), SupplierId = supplierId, PeriodStart = periodStart, PeriodEnd = periodEnd,
            TotalScore = totalScore, Grade = "B", GrnCount = 1, CreatedBy = 1, CreatedDate = DateTime.UtcNow
        };
        db.SupplierScoreSnapshots.Add(snap);
        db.SaveChanges();
        return snap;
    }
}

public class RecalculateSupplierAsync_Tests
{
    [Fact]
    public async Task Four_Grns_Scoring_90_85_80_95_Gives_Composite_87_5_And_Grade_B()
    {
        var db = RecalcBuild.NewDb();
        var supplier = RecalcBuild.SeedSupplier(db);
        var periodStart = new DateTime(2026, 7, 1);
        var periodEnd   = new DateTime(2026, 7, 8);

        RecalcBuild.SeedGrnScore(db, supplier.UUID, 90m, periodStart.AddDays(1));
        RecalcBuild.SeedGrnScore(db, supplier.UUID, 85m, periodStart.AddDays(2));
        RecalcBuild.SeedGrnScore(db, supplier.UUID, 80m, periodStart.AddDays(3));
        RecalcBuild.SeedGrnScore(db, supplier.UUID, 95m, periodStart.AddDays(4));

        var svc = new ScorecardRecalculationService(db);
        var scored = await svc.RecalculateSupplierAsync(supplier.UUID, periodStart, periodEnd, triggeredBy: 1);

        scored.Should().BeTrue();
        var snapshot = await db.SupplierScoreSnapshots.SingleAsync(s => s.SupplierId == supplier.UUID);
        snapshot.TotalScore.Should().Be(87.5m);
        snapshot.Grade.Should().Be("B");
        snapshot.GrnCount.Should().Be(4);
    }

    [Fact]
    public async Task Previous_70_Current_82_Is_Improving()
    {
        var db = RecalcBuild.NewDb();
        var supplier = RecalcBuild.SeedSupplier(db);
        var prevStart = new DateTime(2026, 6, 1);
        var prevEnd   = new DateTime(2026, 7, 1);
        var currStart = prevEnd;
        var currEnd   = new DateTime(2026, 8, 1);

        RecalcBuild.SeedSnapshot(db, supplier.UUID, prevStart, prevEnd, totalScore: 70m);
        RecalcBuild.SeedGrnScore(db, supplier.UUID, 82m, currStart.AddDays(1));

        var svc = new ScorecardRecalculationService(db);
        await svc.RecalculateSupplierAsync(supplier.UUID, currStart, currEnd, triggeredBy: 1);

        var snapshot = await db.SupplierScoreSnapshots.SingleAsync(s => s.SupplierId == supplier.UUID && s.PeriodStart == currStart);
        snapshot.Trend.Should().Be("IMPROVING");
    }

    [Fact]
    public async Task Previous_85_Current_83_Is_Stable()
    {
        var db = RecalcBuild.NewDb();
        var supplier = RecalcBuild.SeedSupplier(db);
        var prevStart = new DateTime(2026, 6, 1);
        var prevEnd   = new DateTime(2026, 7, 1);
        var currStart = prevEnd;
        var currEnd   = new DateTime(2026, 8, 1);

        RecalcBuild.SeedSnapshot(db, supplier.UUID, prevStart, prevEnd, totalScore: 85m);
        RecalcBuild.SeedGrnScore(db, supplier.UUID, 83m, currStart.AddDays(1));

        var svc = new ScorecardRecalculationService(db);
        await svc.RecalculateSupplierAsync(supplier.UUID, currStart, currEnd, triggeredBy: 1);

        var snapshot = await db.SupplierScoreSnapshots.SingleAsync(s => s.SupplierId == supplier.UUID && s.PeriodStart == currStart);
        snapshot.Trend.Should().Be("STABLE");
    }

    [Fact]
    public async Task No_Previous_Snapshot_Gives_Null_Trend()
    {
        var db = RecalcBuild.NewDb();
        var supplier = RecalcBuild.SeedSupplier(db);
        var periodStart = new DateTime(2026, 7, 1);
        var periodEnd   = new DateTime(2026, 8, 1);
        RecalcBuild.SeedGrnScore(db, supplier.UUID, 80m, periodStart.AddDays(1));

        var svc = new ScorecardRecalculationService(db);
        await svc.RecalculateSupplierAsync(supplier.UUID, periodStart, periodEnd, triggeredBy: 1);

        var snapshot = await db.SupplierScoreSnapshots.SingleAsync(s => s.SupplierId == supplier.UUID);
        snapshot.Trend.Should().BeNull();
    }

    [Fact]
    public async Task Zero_Grns_In_Period_Produces_No_Snapshot()
    {
        var db = RecalcBuild.NewDb();
        var supplier = RecalcBuild.SeedSupplier(db);

        var svc = new ScorecardRecalculationService(db);
        var scored = await svc.RecalculateSupplierAsync(supplier.UUID, new DateTime(2026, 7, 1), new DateTime(2026, 8, 1), triggeredBy: 1);

        scored.Should().BeFalse();
        (await db.SupplierScoreSnapshots.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Recalculating_The_Same_Period_Twice_Overwrites_The_Existing_Snapshot()
    {
        var db = RecalcBuild.NewDb();
        var supplier = RecalcBuild.SeedSupplier(db);
        var periodStart = new DateTime(2026, 7, 1);
        var periodEnd   = new DateTime(2026, 8, 1);
        RecalcBuild.SeedGrnScore(db, supplier.UUID, 80m, periodStart.AddDays(1));

        var svc = new ScorecardRecalculationService(db);
        await svc.RecalculateSupplierAsync(supplier.UUID, periodStart, periodEnd, triggeredBy: 1);

        // A new GRN gets scored within the same period before the admin re-triggers recalculation.
        RecalcBuild.SeedGrnScore(db, supplier.UUID, 100m, periodStart.AddDays(2));
        await svc.RecalculateSupplierAsync(supplier.UUID, periodStart, periodEnd, triggeredBy: 2);

        var snapshots = await db.SupplierScoreSnapshots.Where(s => s.SupplierId == supplier.UUID).ToListAsync();
        snapshots.Should().ContainSingle(); // overwritten in place, not duplicated
        snapshots[0].TotalScore.Should().Be(90m); // average of 80 and 100
        snapshots[0].GrnCount.Should().Be(2);
        snapshots[0].ModifiedBy.Should().Be(2);
    }
}

public class RecalculateAllAsync_Tests
{
    [Fact]
    public async Task Recalculates_Every_Active_Supplier_With_A_Scored_Grn_In_The_Period()
    {
        var db = RecalcBuild.NewDb();
        var withScores    = RecalcBuild.SeedSupplier(db);
        var noScores      = RecalcBuild.SeedSupplier(db);
        var inactiveButScored = RecalcBuild.SeedSupplier(db, isActive: false);

        var periodStart = new DateTime(2026, 7, 1);
        var periodEnd   = new DateTime(2026, 8, 1);
        RecalcBuild.SeedGrnScore(db, withScores.UUID, 80m, periodStart.AddDays(1));
        RecalcBuild.SeedGrnScore(db, inactiveButScored.UUID, 80m, periodStart.AddDays(1));

        var svc = new ScorecardRecalculationService(db);
        var count = await svc.RecalculateAllAsync(periodStart, periodEnd, triggeredBy: 0);

        count.Should().Be(1);
        (await db.SupplierScoreSnapshots.CountAsync()).Should().Be(1);
        (await db.SupplierScoreSnapshots.AnyAsync(s => s.SupplierId == withScores.UUID)).Should().BeTrue();
        (await db.SupplierScoreSnapshots.AnyAsync(s => s.SupplierId == noScores.UUID)).Should().BeFalse();
        (await db.SupplierScoreSnapshots.AnyAsync(s => s.SupplierId == inactiveButScored.UUID)).Should().BeFalse();
    }
}

public class ScorecardPeriodResolver_Tests
{
    [Fact]
    public void Daily_Resolves_To_Yesterday()
    {
        var now = new DateTime(2026, 7, 20);
        var (start, end) = ScorecardPeriodResolver.ResolvePreviousPeriod("daily", now);
        start.Should().Be(new DateTime(2026, 7, 19));
        end.Should().Be(new DateTime(2026, 7, 20));
    }

    [Fact]
    public void Monthly_Resolves_To_Previous_Calendar_Month()
    {
        var now = new DateTime(2026, 7, 20);
        var (start, end) = ScorecardPeriodResolver.ResolvePreviousPeriod("monthly", now);
        start.Should().Be(new DateTime(2026, 6, 1));
        end.Should().Be(new DateTime(2026, 7, 1));
    }
}
