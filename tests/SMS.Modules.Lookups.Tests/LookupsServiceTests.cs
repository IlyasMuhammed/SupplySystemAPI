using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Lookups.Data;
using SMS.Modules.Lookups.Domain;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Repositories;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Lookups.Tests;

// ── Test helpers ──────────────────────────────────────────────────────────────

file static class Helpers
{
    internal static (LookupsService svc, LookupsDbContext db) Build(Action<LookupsDbContext>? seed = null)
    {
        var options = new DbContextOptionsBuilder<LookupsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new LookupsDbContext(options);
        seed?.Invoke(db);
        db.SaveChanges();

        var repo = new LookupsRepository(db);
        var svc = new LookupsService(repo);
        return (svc, db);
    }

    internal static LookupType SupplierType(LookupsDbContext db)
    {
        var type = new LookupType
        {
            Id = Guid.NewGuid(),
            Slug = "supplier-type",
            Name = "Supplier Type",
            IsActive = true
        };
        db.LookupTypes.Add(type);
        return type;
    }

    internal static LookupType CurrencyType(LookupsDbContext db)
    {
        var type = new LookupType
        {
            Id = Guid.NewGuid(),
            Slug = "currency",
            Name = "Currency",
            IsActive = true
        };
        db.LookupTypes.Add(type);
        return type;
    }

    internal static LookupValue AddValue(LookupsDbContext db, Guid typeId, string name, bool isActive = true, int sortOrder = 0)
    {
        var v = new LookupValue { Id = Guid.NewGuid(), TypeId = typeId, DisplayName = name, IsActive = isActive, SortOrder = sortOrder };
        db.LookupValues.Add(v);
        return v;
    }
}

// ── Tests ─────────────────────────────────────────────────────────────────────

public class LookupsServiceTests
{
    [Fact]
    public async Task GetByType_SupplierType_ReturnsFiveValues()
    {
        var (svc, _) = Helpers.Build(db =>
        {
            var t = Helpers.SupplierType(db);
            Helpers.AddValue(db, t.Id, "Local Vendor", sortOrder: 1);
            Helpers.AddValue(db, t.Id, "International Supplier", sortOrder: 2);
            Helpers.AddValue(db, t.Id, "Manufacturer", sortOrder: 3);
            Helpers.AddValue(db, t.Id, "Distributor", sortOrder: 4);
            Helpers.AddValue(db, t.Id, "Service Provider", sortOrder: 5);
        });

        var result = await svc.GetValuesByTypeAsync("supplier-type");

        result.Should().HaveCount(5);
        result.Select(v => v.DisplayName).Should().Contain("Local Vendor");
    }

    [Fact]
    public async Task GetByType_Currency_IncludesPKR()
    {
        var (svc, _) = Helpers.Build(db =>
        {
            var t = Helpers.CurrencyType(db);
            Helpers.AddValue(db, t.Id, "Pakistani Rupee (PKR)", sortOrder: 1);
            Helpers.AddValue(db, t.Id, "US Dollar (USD)", sortOrder: 2);
            Helpers.AddValue(db, t.Id, "Euro (EUR)", sortOrder: 3);
            Helpers.AddValue(db, t.Id, "British Pound (GBP)", sortOrder: 4);
            Helpers.AddValue(db, t.Id, "UAE Dirham (AED)", sortOrder: 5);
            Helpers.AddValue(db, t.Id, "Saudi Riyal (SAR)", sortOrder: 6);
            Helpers.AddValue(db, t.Id, "Chinese Yuan (CNY)", sortOrder: 7);
            Helpers.AddValue(db, t.Id, "Japanese Yen (JPY)", sortOrder: 8);
        });

        var result = await svc.GetValuesByTypeAsync("currency");

        result.Count.Should().BeGreaterThanOrEqualTo(6);
        result.Should().Contain(v => v.DisplayName.Contains("PKR"));
    }

    [Fact]
    public async Task GetByType_DeactivatedValue_NotReturned()
    {
        var (svc, _) = Helpers.Build(db =>
        {
            var t = Helpers.SupplierType(db);
            Helpers.AddValue(db, t.Id, "Active Vendor", isActive: true);
            Helpers.AddValue(db, t.Id, "Deactivated Vendor", isActive: false);
        });

        var result = await svc.GetValuesByTypeAsync("supplier-type");

        result.Should().HaveCount(1);
        result.Should().NotContain(v => v.DisplayName == "Deactivated Vendor");
    }

    [Fact]
    public async Task SoftDelete_ReferencedValue_ThrowsConflict()
    {
        var (svc, db) = Helpers.Build(db =>
        {
            var t = Helpers.SupplierType(db);
            Helpers.AddValue(db, t.Id, "Local Vendor");
        });

        var valueId = db.LookupValues.First().Id;
        var checkerMock = new Mock<ILookupReferenceChecker>();
        checkerMock.Setup(c => c.IsValueReferenced(valueId)).Returns(true);

        var act = async () => await svc.SoftDeleteValueAsync("supplier-type", valueId, new[] { checkerMock.Object });

        await act.Should().ThrowAsync<ConflictException>()
            .WithMessage("*referenced*");
    }

    [Fact]
    public async Task SoftDelete_UnreferencedValue_SetsIsActiveFalse()
    {
        var (svc, db) = Helpers.Build(db =>
        {
            var t = Helpers.SupplierType(db);
            Helpers.AddValue(db, t.Id, "Local Vendor");
        });

        var valueId = db.LookupValues.First().Id;
        var checkers = Enumerable.Empty<ILookupReferenceChecker>();

        var result = await svc.SoftDeleteValueAsync("supplier-type", valueId, checkers);

        result.Should().BeTrue();
        db.LookupValues.First(v => v.Id == valueId).IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task CreateValueByType_DuplicateDisplayName_ThrowsConflict()
    {
        var (svc, _) = Helpers.Build(db =>
        {
            var t = Helpers.SupplierType(db);
            Helpers.AddValue(db, t.Id, "Local Vendor");
        });

        var req = new CreateLookupValueByTypeRequest { DisplayName = "Local Vendor", SortOrder = 10 };

        var act = async () => await svc.CreateValueByTypeAsync("supplier-type", req);

        await act.Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task SeedAsync_Idempotent_RunTwiceProducesNoDuplicates()
    {
        var options = new DbContextOptionsBuilder<LookupsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new LookupsDbContext(options);
        var seeder = new LookupsDataSeeder(db);

        await seeder.SeedAsync();
        var countAfterFirst = db.LookupTypes.Count();

        await seeder.SeedAsync();
        var countAfterSecond = db.LookupTypes.Count();

        countAfterSecond.Should().Be(countAfterFirst);
        countAfterFirst.Should().Be(12);
    }
}
