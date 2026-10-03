using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Migrations;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Services;
using Xunit;

namespace SMS.Modules.Inventory.Tests;

/// <summary>
/// A33 C2 on a real SQL Server (LocalDB): the bulk assign by category (T-C2-03, BR-C2-02, D-14, R-15) is one set-based
/// update, so it runs where that update really runs; two bulk assigns at once; and the A33-PB-01 migration's own SQL,
/// up, down and up again, since the shared database replays every migration on API start.
/// </summary>
public class VariantFulfillmentRouteBulkAssignTests
{
    [SqlServerFact]
    public async Task T_C2_03_bulk_assign_sets_the_null_variants_of_the_category_and_its_sub_categories_and_skips_the_assigned_one()
    {
        await using var harness = await InventorySqlServerHarness.CreateAsync();
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();
        var routes = new FakeFulfillmentRouteLookup();
        var route = routes.Add(orgA, "PICK_AND_SHIP");
        var earlier = Guid.NewGuid();

        int categoryId, otherCategoryId;
        Guid v1, v2, v3, inactiveVariant, inactiveProductVariant, otherCategoryVariant, foreignVariant;
        await using (var db = harness.NewContext(orgA))
        {
            var category = await Seed.CategoryAsync(db, "Steel");
            var sub = await Seed.SubCategoryAsync(db, category.Id, "Rods");
            var other = await Seed.CategoryAsync(db, "Cement");
            categoryId = category.Id;
            otherCategoryId = other.Id;
            v1 = await Seed.VariantAsync(db, categoryId: category.Id);                                   // category only
            v2 = await Seed.VariantAsync(db, categoryId: category.Id, subCategoryId: sub.Id);            // both
            v3 = await Seed.VariantAsync(db, subCategoryId: sub.Id, route: earlier);                    // sub-category only, assigned
            inactiveVariant = await Seed.VariantAsync(db, categoryId: category.Id, variantActive: false);
            inactiveProductVariant = await Seed.VariantAsync(db, categoryId: category.Id, productActive: false);
            otherCategoryVariant = await Seed.VariantAsync(db, categoryId: other.Id);
        }
        await using (var db = harness.NewContext(orgB))
            foreignVariant = await Seed.VariantAsync(db, categoryId: categoryId);   // another organization's row pointing at A's category id

        // A super admin of A: the EF tenant filter is off, so only the explicit organization filter keeps B's row out.
        await using (var db = harness.NewContext(orgA, superAdmin: true))
        {
            var result = await new VariantFulfillmentRouteService(db, routes)
                .AssignByCategoryAsync(route.Uuid, new AssignRouteByCategoryRequest { CategoryId = categoryId });

            result.Updated.Should().Be(2);
            result.Skipped.Should().Be(1);
            result.Total.Should().Be(3);
        }

        var stored = await RoutesAsync(harness);
        stored[v1].Should().Be(route.Uuid);
        stored[v2].Should().Be(route.Uuid);
        stored[v3].Should().Be(earlier, "BR-C2-02: an existing assignment is never overwritten");
        stored[inactiveVariant].Should().BeNull();
        stored[inactiveProductVariant].Should().BeNull();
        stored[otherCategoryVariant].Should().BeNull();
        stored[foreignVariant].Should().BeNull();
        otherCategoryId.Should().NotBe(categoryId);
    }

    [SqlServerFact]
    public async Task A_sub_category_filter_limits_the_assignment_to_that_sub_category()
    {
        await using var harness = await InventorySqlServerHarness.CreateAsync();
        var org = Guid.NewGuid();
        var routes = new FakeFulfillmentRouteLookup();
        var route = routes.Add(org, "PICK_ONLY");

        int categoryId, rodsId;
        Guid rods, plates, loose;
        await using (var db = harness.NewContext(org))
        {
            var category = await Seed.CategoryAsync(db, "Steel");
            var rodsSub = await Seed.SubCategoryAsync(db, category.Id, "Rods");
            var platesSub = await Seed.SubCategoryAsync(db, category.Id, "Plates");
            categoryId = category.Id;
            rodsId = rodsSub.Id;
            rods = await Seed.VariantAsync(db, categoryId: category.Id, subCategoryId: rodsSub.Id);
            plates = await Seed.VariantAsync(db, categoryId: category.Id, subCategoryId: platesSub.Id);
            loose = await Seed.VariantAsync(db, categoryId: category.Id);
        }

        await using (var db = harness.NewContext(org))
        {
            var result = await new VariantFulfillmentRouteService(db, routes).AssignByCategoryAsync(
                route.Uuid, new AssignRouteByCategoryRequest { CategoryId = categoryId, SubCategoryId = rodsId });

            result.Should().BeEquivalentTo(new FulfillmentRouteBulkAssignResult { Updated = 1, Skipped = 0, Total = 1 });
        }

        var stored = await RoutesAsync(harness);
        stored[rods].Should().Be(route.Uuid);
        stored[plates].Should().BeNull();
        stored[loose].Should().BeNull();
    }

    [SqlServerFact]
    public async Task Two_bulk_assigns_at_once_never_overwrite_each_other_and_their_counts_add_up()
    {
        await using var harness = await InventorySqlServerHarness.CreateAsync();
        var org = Guid.NewGuid();
        var routes = new FakeFulfillmentRouteLookup();
        var first = routes.Add(org, "PICK_AND_SHIP");
        var second = routes.Add(org, "PICK_ONLY");
        const int variants = 80;

        int categoryId;
        await using (var db = harness.NewContext(org))
        {
            var category = await Seed.CategoryAsync(db, "Steel");
            categoryId = category.Id;
            await Seed.ManyVariantsAsync(db, category.Id, variants);
        }

        async Task<FulfillmentRouteBulkAssignResult> AssignAsync(Guid routeUuid)
        {
            await using var db = harness.NewContext(org);
            return await new VariantFulfillmentRouteService(db, routes)
                .AssignByCategoryAsync(routeUuid, new AssignRouteByCategoryRequest { CategoryId = categoryId });
        }

        var results = await Task.WhenAll(Task.Run(() => AssignAsync(first.Uuid)), Task.Run(() => AssignAsync(second.Uuid)));

        results.Sum(r => r.Updated).Should().Be(variants, "every NULL variant is set exactly once");
        foreach (var r in results)
        {
            r.Total.Should().Be(variants);
            r.Skipped.Should().Be(variants - r.Updated);
        }
        var stored = await RoutesAsync(harness);
        stored.Values.Should().NotContainNulls();
        stored.Values.Count(v => v == first.Uuid).Should().Be(results[0].Updated);
        stored.Values.Count(v => v == second.Uuid).Should().Be(results[1].Updated);
    }

    [SqlServerFact]
    public async Task The_A33_variant_route_migration_is_additive_and_runs_up_down_up_and_replays_safely()
    {
        await using var harness = await InventorySqlServerHarness.CreateAsync();
        var stock = await harness.SeedStockAsync(onHand: 1);

        // The schema comes from the current model; take the column away so the table looks as it did before.
        await harness.ExecuteAsync($"DROP INDEX [{IndexName}] ON [inventory].[ProductVariants]");
        await harness.ExecuteAsync("ALTER TABLE [inventory].[ProductVariants] DROP COLUMN [FulfillmentRouteUuid]");
        (await harness.ColumnExistsAsync("inventory.ProductVariants", "FulfillmentRouteUuid")).Should().BeFalse();

        await harness.ApplyUpAsync(new A33_AddFulfillmentRouteToVariants());
        (await harness.ColumnExistsAsync("inventory.ProductVariants", "FulfillmentRouteUuid")).Should().BeTrue();
        (await IndexExistsAsync(harness)).Should().BeTrue();

        var replay = async () => await harness.ApplyUpAsync(new A33_AddFulfillmentRouteToVariants());
        await replay.Should().NotThrowAsync("every statement is guarded: API start replays it on the drifted shared database");

        await ApplyDownAsync(harness);
        (await harness.ColumnExistsAsync("inventory.ProductVariants", "FulfillmentRouteUuid")).Should().BeFalse();
        (await IndexExistsAsync(harness)).Should().BeFalse();
        var replayDown = async () => await ApplyDownAsync(harness);
        await replayDown.Should().NotThrowAsync();

        await harness.ApplyUpAsync(new A33_AddFulfillmentRouteToVariants());
        (await harness.ColumnExistsAsync("inventory.ProductVariants", "FulfillmentRouteUuid")).Should().BeTrue();
        (await IndexExistsAsync(harness)).Should().BeTrue();

        // The pre-existing variant survives with no route, and the model reads and writes the column.
        await using var db = harness.NewContext(stock.OrganizationId);
        var variant = await db.ProductVariants.SingleAsync(v => v.Uuid == stock.VariantUuid);
        variant.FulfillmentRouteUuid.Should().BeNull();
        var route = Guid.NewGuid();
        variant.FulfillmentRouteUuid = route;
        await db.SaveChangesAsync();
        (await RoutesAsync(harness))[stock.VariantUuid].Should().Be(route);
    }

    private const string IndexName = "IX_ProductVariants_OrganizationId_FulfillmentRouteUuid";

    private static async Task ApplyDownAsync(InventorySqlServerHarness harness)
    {
        foreach (var sql in new A33_AddFulfillmentRouteToVariants().DownOperations.OfType<SqlOperation>())
            await harness.ExecuteAsync(sql.Sql);
    }

    private static async Task<bool> IndexExistsAsync(InventorySqlServerHarness harness)
    {
        await using var db = harness.NewContext(Guid.NewGuid());
        return await db.Database.SqlQueryRaw<int>(
                $"SELECT COUNT(*) AS [Value] FROM sys.indexes WHERE name = '{IndexName}' AND object_id = OBJECT_ID('inventory.ProductVariants') AND has_filter = 1")
            .SingleAsync() == 1;
    }

    private static async Task<Dictionary<Guid, Guid?>> RoutesAsync(InventorySqlServerHarness harness)
    {
        await using var db = harness.NewContext(Guid.NewGuid(), superAdmin: true);
        return await db.ProductVariants.IgnoreQueryFilters().ToDictionaryAsync(v => v.Uuid, v => v.FulfillmentRouteUuid);
    }

    private static class Seed
    {
        internal static async Task<ProductCategory> CategoryAsync(InventoryDbContext db, string name)
        {
            var category = new ProductCategory { Name = name, Code = $"C{Guid.NewGuid():N}"[..8], IsActive = true };
            db.ProductCategories.Add(category);
            await db.SaveChangesAsync();
            return category;
        }

        internal static async Task<ProductSubCategory> SubCategoryAsync(InventoryDbContext db, int categoryId, string name)
        {
            var sub = new ProductSubCategory { CategoryId = categoryId, Name = name, Code = $"S{Guid.NewGuid():N}"[..8], IsActive = true };
            db.ProductSubCategories.Add(sub);
            await db.SaveChangesAsync();
            return sub;
        }

        internal static async Task<Guid> VariantAsync(
            InventoryDbContext db, int? categoryId = null, int? subCategoryId = null, Guid? route = null,
            bool variantActive = true, bool productActive = true)
        {
            var product = NewProduct(categoryId, subCategoryId, productActive);
            var variant = NewVariant(route, variantActive);
            product.Variants.Add(variant);
            db.Products.Add(product);
            await db.SaveChangesAsync();
            return variant.Uuid;
        }

        internal static async Task ManyVariantsAsync(InventoryDbContext db, int categoryId, int count)
        {
            for (var i = 0; i < count; i++)
            {
                var product = NewProduct(categoryId, null, productActive: true);
                product.Variants.Add(NewVariant(null, variantActive: true));
                db.Products.Add(product);
            }
            await db.SaveChangesAsync();
        }

        private static Product NewProduct(int? categoryId, int? subCategoryId, bool productActive) => new()
        {
            Uuid = Guid.NewGuid(), Name = $"Item {Guid.NewGuid():N}"[..20], Sku = $"SKU{Guid.NewGuid():N}"[..12],
            CategoryId = categoryId, SubCategoryId = subCategoryId, IsActive = productActive,
            Status = productActive ? "ACTIVE" : "INACTIVE"
        };

        private static ProductVariant NewVariant(Guid? route, bool variantActive) => new()
        {
            Uuid = Guid.NewGuid(), Sku = $"V{Guid.NewGuid():N}"[..12], VariantName = "Default", IsDefault = true,
            IsActive = variantActive, FulfillmentRouteUuid = route
        };
    }
}
