using FluentAssertions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Repositories;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Inventory.Tests;

// PV-007 — variant CRUD on an existing product: add/edit variant, default-toggle, and the
// soft-delete-if-transacted / hard-delete-if-never-transacted rule.
file static class VariantCrudBuild
{
    internal static (InventoryRepository repo, InventoryDbContext db) NewRepo() =>
        NewRepoWithDb(new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new StaticTenantContext()));

    private static (InventoryRepository, InventoryDbContext) NewRepoWithDb(InventoryDbContext db) =>
        (new InventoryRepository(db, new Mock<IInventoryLedgerService>().Object), db);

    internal static InventoryService NewService(
        InventoryDbContext db, IEnumerable<IVariantReferenceChecker>? checkers = null)
    {
        var repo = new InventoryRepository(db, new Mock<IInventoryLedgerService>().Object);
        var searchIndex = new ProductSearchIndexService(db);
        // A loose mock's Create(...) returns null without throwing — enough for
        // IBackgroundJobClient.Enqueue<T>(...) calls made by the service under test to complete
        // without needing a real Hangfire storage/connection.
        var jobs = new Mock<IBackgroundJobClient>();
        return new InventoryService(repo, searchIndex, jobs.Object, checkers ?? []);
    }

    // "Create product 'HP EliteBook 850': product_code='HP-EB-850', category='Laptop'."
    internal static async Task<Product> SeedHpEliteBookAsync(InventoryDbContext db)
    {
        var category = new ProductCategory { Name = "Laptop", Code = "LAPTOP", IsActive = true };
        db.ProductCategories.Add(category);
        await db.SaveChangesAsync();

        var product = new Product
        {
            Uuid = Guid.NewGuid(), Sku = "HP-EB-850", Name = "HP EliteBook 850",
            CategoryId = category.Id, Status = "ACTIVE", IsActive = true, CreatedBy = 1
        };
        product.Variants.Add(new ProductVariant
        {
            Uuid = Guid.NewGuid(), Sku = "HP-EB-850-DEFAULT", VariantName = "HP EliteBook 850",
            PurchasePrice = 150000m, IsDefault = true, IsActive = true, CreatedDate = DateTime.UtcNow, CreatedBy = 1
        });
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product;
    }
}

public class CreateVariantAsyncTests
{
    [Fact]
    public async Task AddingASecondVariant_ProductNowHasTwoVariants()
    {
        var (repo, db) = VariantCrudBuild.NewRepo();
        var product = await VariantCrudBuild.SeedHpEliteBookAsync(db);

        var result = await repo.CreateVariantAsync(product.Id, new CreateProductVariantRequest
        {
            Sku = "HP-EB-850-I7-32-1T", VariantName = "i7 / 32GB / 1TB",
            PurchasePrice = 195000m, SellingPrice = 220000m
        }, userId: 1);

        result.Should().NotBeNull();
        result!.Value.sku.Should().Be("HP-EB-850-I7-32-1T");

        var variants = await db.ProductVariants.Where(v => v.ProductId == product.Id).ToListAsync();
        variants.Should().HaveCount(2);
        var newVariant = variants.Single(v => v.Sku == "HP-EB-850-I7-32-1T");
        newVariant.SellingPrice.Should().Be(220000m);
        newVariant.IsDefault.Should().BeFalse();       // existing default untouched
        variants.Count(v => v.IsDefault).Should().Be(1);
    }

    [Fact]
    public async Task UnknownProductId_ReturnsNull()
    {
        var (repo, _) = VariantCrudBuild.NewRepo();

        var result = await repo.CreateVariantAsync(999999, new CreateProductVariantRequest
        {
            VariantName = "X", PurchasePrice = 1m
        }, userId: 1);

        result.Should().BeNull();
    }

    [Fact]
    public async Task DuplicateSku_ThrowsConflict()
    {
        var (repo, db) = VariantCrudBuild.NewRepo();
        var product = await VariantCrudBuild.SeedHpEliteBookAsync(db);

        var act = () => repo.CreateVariantAsync(product.Id, new CreateProductVariantRequest
        {
            Sku = "HP-EB-850-DEFAULT", VariantName = "Dup", PurchasePrice = 1m
        }, userId: 1);

        await act.Should().ThrowAsync<ConflictException>();
    }
}

public class DefaultVariantToggleTests
{
    [Fact]
    public async Task SettingIsDefaultOnNewVariant_UnsetsThePreviousDefault()
    {
        var (repo, db) = VariantCrudBuild.NewRepo();
        var product = await VariantCrudBuild.SeedHpEliteBookAsync(db);
        var originalDefault = await db.ProductVariants.SingleAsync(v => v.ProductId == product.Id);

        var created = await repo.CreateVariantAsync(product.Id, new CreateProductVariantRequest
        {
            Sku = "HP-EB-850-I7-32-1T", VariantName = "i7 / 32GB / 1TB",
            PurchasePrice = 195000m, IsDefault = true
        }, userId: 1);

        await db.Entry(originalDefault).ReloadAsync();
        originalDefault.IsDefault.Should().BeFalse();

        var newVariant = await db.ProductVariants.SingleAsync(v => v.Uuid == created!.Value.uuid);
        newVariant.IsDefault.Should().BeTrue();

        (await db.ProductVariants.CountAsync(v => v.ProductId == product.Id && v.IsDefault)).Should().Be(1);
    }

    [Fact]
    public async Task UpdateVariantAsync_TogglingDefaultToVariantB_RemovesFlagFromVariantA()
    {
        var (repo, db) = VariantCrudBuild.NewRepo();
        var product = await VariantCrudBuild.SeedHpEliteBookAsync(db);
        var variantA = await db.ProductVariants.SingleAsync(v => v.ProductId == product.Id);
        var createdB = await repo.CreateVariantAsync(product.Id, new CreateProductVariantRequest
        {
            Sku = "HP-EB-850-I7-32-1T", VariantName = "i7 / 32GB / 1TB", PurchasePrice = 195000m
        }, userId: 1);

        var updated = await repo.UpdateVariantAsync(createdB!.Value.uuid, new CreateProductVariantRequest
        {
            Sku = "HP-EB-850-I7-32-1T", VariantName = "i7 / 32GB / 1TB", PurchasePrice = 195000m, IsDefault = true
        });

        updated.Should().NotBeNull();
        await db.Entry(variantA).ReloadAsync();
        variantA.IsDefault.Should().BeFalse();
        var variantB = await db.ProductVariants.SingleAsync(v => v.Uuid == createdB.Value.uuid);
        variantB.IsDefault.Should().BeTrue();
    }
}

// Which channels/documents a variant may be used from — independent flags a variant can hold any
// combination of, added against the variant rather than the product because two SKUs of the same
// product can legitimately serve different channels (e.g. a bulk pack for Production, a retail unit
// for POS).
public class VariantChannelAvailabilityTests
{
    [Fact]
    public async Task ANewVariantThatNamesNoChannel_IsAvailableNowhere()
    {
        var (repo, db) = VariantCrudBuild.NewRepo();
        var product = await VariantCrudBuild.SeedHpEliteBookAsync(db);

        var created = await repo.CreateVariantAsync(product.Id, new CreateProductVariantRequest
        {
            Sku = "HP-EB-850-I7-32-1T", VariantName = "i7 / 32GB / 1TB", PurchasePrice = 195000m
        }, userId: 1);

        var variant = await db.ProductVariants.SingleAsync(v => v.Uuid == created!.Value.uuid);
        variant.IsAvailableForRetail.Should().BeFalse();
        variant.IsAvailableForPos.Should().BeFalse();
        variant.IsAvailableForMirMiv.Should().BeFalse();
        variant.IsAvailableForProduction.Should().BeFalse();
        variant.IsAvailableForServices.Should().BeFalse();
    }

    [Fact]
    public async Task ANewVariantCanBeMarkedAvailableForSeveralChannelsAtOnce()
    {
        var (repo, db) = VariantCrudBuild.NewRepo();
        var product = await VariantCrudBuild.SeedHpEliteBookAsync(db);

        var created = await repo.CreateVariantAsync(product.Id, new CreateProductVariantRequest
        {
            Sku = "HP-EB-850-I7-32-1T", VariantName = "i7 / 32GB / 1TB", PurchasePrice = 195000m,
            IsAvailableForRetail = true, IsAvailableForServices = true
        }, userId: 1);

        var variant = await db.ProductVariants.SingleAsync(v => v.Uuid == created!.Value.uuid);
        variant.IsAvailableForRetail.Should().BeTrue();
        variant.IsAvailableForServices.Should().BeTrue();
        variant.IsAvailableForPos.Should().BeFalse();
        variant.IsAvailableForMirMiv.Should().BeFalse();
        variant.IsAvailableForProduction.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateVariantAsync_ReplacesTheWholeSetOfChannels()
    {
        var (repo, db) = VariantCrudBuild.NewRepo();
        var product = await VariantCrudBuild.SeedHpEliteBookAsync(db);
        var created = (await repo.CreateVariantAsync(product.Id, new CreateProductVariantRequest
        {
            Sku = "HP-EB-850-I7-32-1T", VariantName = "i7 / 32GB / 1TB", PurchasePrice = 195000m,
            IsAvailableForRetail = true
        }, userId: 1))!.Value;

        await repo.UpdateVariantAsync(created.uuid, new CreateProductVariantRequest
        {
            Sku = "HP-EB-850-I7-32-1T", VariantName = "i7 / 32GB / 1TB", PurchasePrice = 195000m,
            IsAvailableForPos = true, IsAvailableForMirMiv = true
        });

        var variant = await db.ProductVariants.SingleAsync(v => v.Uuid == created.uuid);
        variant.IsAvailableForPos.Should().BeTrue();
        variant.IsAvailableForMirMiv.Should().BeTrue();
        // Not carried over from the previous save — an update states the whole set, same as every
        // other field on this request (VariantName, PurchasePrice, IsDefault all behave the same way).
        variant.IsAvailableForRetail.Should().BeFalse();
    }

    [Fact]
    public async Task TheProductsListOfVariants_ReadsBackTheChannelsUnchanged()
    {
        var db = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new StaticTenantContext());
        var repo = new InventoryRepository(db, new Mock<IInventoryLedgerService>().Object);
        var product = await VariantCrudBuild.SeedHpEliteBookAsync(db);
        await repo.CreateVariantAsync(product.Id, new CreateProductVariantRequest
        {
            Sku = "HP-EB-850-I7-32-1T", VariantName = "i7 / 32GB / 1TB", PurchasePrice = 195000m,
            IsAvailableForProduction = true
        }, userId: 1);

        var read = await repo.GetProductByIdAsync(product.Id);

        var variant = read!.Variants.Single(v => v.Sku == "HP-EB-850-I7-32-1T");
        variant.IsAvailableForProduction.Should().BeTrue();
        variant.IsAvailableForRetail.Should().BeFalse();
    }
}

public class DeleteVariantAsyncTests
{
    [Fact]
    public async Task NeverTransactedVariant_IsHardDeleted()
    {
        var db = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new StaticTenantContext());
        var product = await VariantCrudBuild.SeedHpEliteBookAsync(db);
        var created = (await new InventoryRepository(db, new Mock<IInventoryLedgerService>().Object)
            .CreateVariantAsync(product.Id, new CreateProductVariantRequest
            {
                Sku = "HP-EB-850-I7-32-1T", VariantName = "i7 / 32GB / 1TB", PurchasePrice = 195000m
            }, userId: 1))!.Value;
        var svc = VariantCrudBuild.NewService(db); // no reference checkers registered -> never "transacted"

        var result = await svc.DeleteVariantAsync(created.uuid);

        result.Found.Should().BeTrue();
        result.SoftDeleted.Should().BeFalse();
        (await db.ProductVariants.AnyAsync(v => v.Uuid == created.uuid)).Should().BeFalse();
    }

    [Fact]
    public async Task TransactedVariant_IsSoftDeleted_NotRemoved()
    {
        var db = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new StaticTenantContext());
        var product = await VariantCrudBuild.SeedHpEliteBookAsync(db);
        var created = (await new InventoryRepository(db, new Mock<IInventoryLedgerService>().Object)
            .CreateVariantAsync(product.Id, new CreateProductVariantRequest
            {
                Sku = "HP-EB-850-I7-32-1T", VariantName = "i7 / 32GB / 1TB", PurchasePrice = 195000m
            }, userId: 1))!.Value;

        // Simulate "has appeared on a PO line" via a fake cross-module checker.
        var checker = new Mock<IVariantReferenceChecker>();
        checker.Setup(c => c.IsVariantReferencedAsync(created.uuid)).ReturnsAsync(true);
        var svc = VariantCrudBuild.NewService(db, [checker.Object]);

        var result = await svc.DeleteVariantAsync(created.uuid);

        result.Found.Should().BeTrue();
        result.SoftDeleted.Should().BeTrue();
        var variant = await db.ProductVariants.SingleAsync(v => v.Uuid == created.uuid);
        variant.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task DeletingTheOnlyVariantOfAProduct_Throws_UnprocessableEntity()
    {
        var db = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new StaticTenantContext());
        var product = await VariantCrudBuild.SeedHpEliteBookAsync(db);
        var onlyVariant = await db.ProductVariants.SingleAsync(v => v.ProductId == product.Id);
        var svc = VariantCrudBuild.NewService(db);

        var act = () => svc.DeleteVariantAsync(onlyVariant.Uuid);

        await act.Should().ThrowAsync<UnprocessableEntityException>();
        (await db.ProductVariants.AnyAsync(v => v.Uuid == onlyVariant.Uuid)).Should().BeTrue();
    }

    [Fact]
    public async Task DeletingTheDefaultVariant_PromotesAnotherRemainingVariantToDefault()
    {
        var db = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, new StaticTenantContext());
        var product = await VariantCrudBuild.SeedHpEliteBookAsync(db);
        var defaultVariant = await db.ProductVariants.SingleAsync(v => v.ProductId == product.Id);
        var created = (await new InventoryRepository(db, new Mock<IInventoryLedgerService>().Object)
            .CreateVariantAsync(product.Id, new CreateProductVariantRequest
            {
                Sku = "HP-EB-850-I7-32-1T", VariantName = "i7 / 32GB / 1TB", PurchasePrice = 195000m
            }, userId: 1))!.Value;
        var svc = VariantCrudBuild.NewService(db);

        await svc.DeleteVariantAsync(defaultVariant.Uuid);

        var remaining = await db.ProductVariants.SingleAsync(v => v.Uuid == created.uuid);
        remaining.IsDefault.Should().BeTrue();
    }
}
