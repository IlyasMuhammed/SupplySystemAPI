using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Models;
using SMS.Modules.Suppliers.Repositories;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Suppliers.Tests;

file static class Build
{
    internal static SuppliersDbContext NewDb() =>
        new(new DbContextOptionsBuilder<SuppliersDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}

public class ScorecardDataSeeder_Tests
{
    [Fact]
    public async Task Seeds_Five_Dimensions_With_Correct_Defaults_Summing_To_100()
    {
        var db = Build.NewDb();
        var seeder = new ScorecardDataSeeder(db);

        await seeder.SeedAsync();

        var dims = await db.ScorecardDimensionWeights.ToListAsync();
        dims.Should().HaveCount(5);
        dims.Sum(d => d.WeightPercentage).Should().Be(100m);

        dims.Should().ContainSingle(d => d.DimensionCode == "DELIVERY" && d.WeightPercentage == 25m && d.MaxPoints == 25m);
        dims.Should().ContainSingle(d => d.DimensionCode == "QUANTITY" && d.WeightPercentage == 25m && d.MaxPoints == 25m);
        dims.Should().ContainSingle(d => d.DimensionCode == "QUALITY"  && d.WeightPercentage == 25m && d.MaxPoints == 25m);
        dims.Should().ContainSingle(d => d.DimensionCode == "PRICE"    && d.WeightPercentage == 15m && d.MaxPoints == 15m);
        dims.Should().ContainSingle(d => d.DimensionCode == "DOCUMENTATION" && d.WeightPercentage == 10m && d.MaxPoints == 10m);
    }

    [Fact]
    public async Task Seeding_Twice_Does_Not_Duplicate_Rows()
    {
        var db = Build.NewDb();
        var seeder = new ScorecardDataSeeder(db);

        await seeder.SeedAsync();
        await seeder.SeedAsync();

        (await db.ScorecardDimensionWeights.CountAsync()).Should().Be(5);
    }
}

public class ScorecardRepository_Tests
{
    private static async Task<SuppliersDbContext> SeededDb()
    {
        var db = Build.NewDb();
        await new ScorecardDataSeeder(db).SeedAsync();
        return db;
    }

    [Fact]
    public async Task GetWeightsAsync_Returns_All_Five_Dimensions_With_Current_Values()
    {
        var db = await SeededDb();
        var repo = new ScorecardRepository(db);

        var result = await repo.GetWeightsAsync();

        result.Should().HaveCount(5);
        result.Select(w => w.DimensionCode).Should()
            .BeEquivalentTo(["DELIVERY", "QUANTITY", "QUALITY", "PRICE", "DOCUMENTATION"]);
    }

    [Fact]
    public async Task UpdateWeightsAsync_Summing_To_99_Throws_BadRequest()
    {
        var db = await SeededDb();
        var repo = new ScorecardRepository(db);

        var weights = new List<UpdateDimensionWeightItem>
        {
            new() { DimensionCode = "DELIVERY",      WeightPercentage = 24m, MaxPoints = 25m },
            new() { DimensionCode = "QUANTITY",      WeightPercentage = 25m, MaxPoints = 25m },
            new() { DimensionCode = "QUALITY",       WeightPercentage = 25m, MaxPoints = 25m },
            new() { DimensionCode = "PRICE",         WeightPercentage = 15m, MaxPoints = 15m },
            new() { DimensionCode = "DOCUMENTATION", WeightPercentage = 10m, MaxPoints = 10m }
        };

        var act = () => repo.UpdateWeightsAsync(weights, modifiedBy: 1);

        await act.Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task UpdateWeightsAsync_Summing_To_100_Succeeds_And_Persists()
    {
        var db = await SeededDb();
        var repo = new ScorecardRepository(db);

        var weights = new List<UpdateDimensionWeightItem>
        {
            new() { DimensionCode = "DELIVERY",      WeightPercentage = 30m, MaxPoints = 30m },
            new() { DimensionCode = "QUANTITY",      WeightPercentage = 20m, MaxPoints = 20m },
            new() { DimensionCode = "QUALITY",       WeightPercentage = 25m, MaxPoints = 25m },
            new() { DimensionCode = "PRICE",         WeightPercentage = 15m, MaxPoints = 15m },
            new() { DimensionCode = "DOCUMENTATION", WeightPercentage = 10m, MaxPoints = 10m }
        };

        await repo.UpdateWeightsAsync(weights, modifiedBy: 42);

        var persisted = await repo.GetWeightsAsync();
        persisted.Single(d => d.DimensionCode == "DELIVERY").WeightPercentage.Should().Be(30m);
        persisted.Single(d => d.DimensionCode == "QUANTITY").WeightPercentage.Should().Be(20m);
        persisted.Sum(d => d.WeightPercentage).Should().Be(100m);
    }

    [Fact]
    public async Task UpdateWeightsAsync_With_Unknown_Dimension_Code_Throws_BadRequest()
    {
        var db = await SeededDb();
        var repo = new ScorecardRepository(db);

        var weights = new List<UpdateDimensionWeightItem>
        {
            new() { DimensionCode = "NOT_A_REAL_DIMENSION", WeightPercentage = 100m, MaxPoints = 100m }
        };

        var act = () => repo.UpdateWeightsAsync(weights, modifiedBy: 1);

        await act.Should().ThrowAsync<BadRequestException>();
    }
}
