using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Inventory.Tests;

/// <summary>A34-PA-07 (D-3, API-CONTRACT §4.2) on SQL Server: a bulk assign of a MANUFACTURE route touches only MANUFACTURE products.</summary>
public class VariantRouteCategoryBulkTests
{
    [SqlServerFact]
    public async Task Bulk_assign_of_a_manufacture_route_narrows_to_manufactured_products_and_counts_the_rest_as_skipped()
    {
        await using var harness = await InventorySqlServerHarness.CreateAsync();
        var org = Guid.NewGuid();
        var routes = new FakeFulfillmentRouteLookup();
        var mfg = routes.Add(org, "MFG_PICK_PACK_SHIP", category: FulfillmentRouteCategory.Manufacture);
        var earlier = Guid.NewGuid();

        int categoryId;
        Guid made1, made2, madeAssigned, bought;
        await using (var db = harness.NewContext(org))
        {
            var category = new ProductCategory { Name = "Gears", Code = $"C{Guid.NewGuid():N}"[..8], IsActive = true };
            db.ProductCategories.Add(category);
            await db.SaveChangesAsync();
            categoryId = category.Id;
            made1 = await VariantAsync(db, categoryId, SupplyMethod.Manufacture);
            made2 = await VariantAsync(db, categoryId, SupplyMethod.Manufacture);
            madeAssigned = await VariantAsync(db, categoryId, SupplyMethod.Manufacture, earlier);
            bought = await VariantAsync(db, categoryId, SupplyMethod.Purchase);
        }

        await using (var db = harness.NewContext(org))
        {
            var result = await new VariantFulfillmentRouteService(db, routes, FakeTenantSnapshots.With(org, "MODULE_MANUFACTURING"))
                .AssignByCategoryAsync(mfg.Uuid, new AssignRouteByCategoryRequest { CategoryId = categoryId });

            result.Should().BeEquivalentTo(new FulfillmentRouteBulkAssignResult { Updated = 2, Skipped = 2, Total = 4 });
        }

        await using var check = harness.NewContext(org);
        var stored = await check.ProductVariants.ToDictionaryAsync(v => v.Uuid, v => v.FulfillmentRouteUuid);
        stored[made1].Should().Be(mfg.Uuid);
        stored[made2].Should().Be(mfg.Uuid);
        stored[madeAssigned].Should().Be(earlier);
        stored[bought].Should().BeNull("D-3: a purchased product can't be made to order");
        (await check.Products.CountAsync(p => p.SupplyMethod == SupplyMethod.Manufacture)).Should().Be(3, "flags never written");
    }

    private static async Task<Guid> VariantAsync(InventoryDbContext db, int categoryId, string supplyMethod, Guid? route = null)
    {
        var product = new Product
        {
            Uuid = Guid.NewGuid(), Name = $"Item {Guid.NewGuid():N}"[..20], Sku = $"SKU{Guid.NewGuid():N}"[..12],
            CategoryId = categoryId, IsActive = true, SupplyMethod = supplyMethod,
            IsManufacturable = supplyMethod == SupplyMethod.Manufacture
        };
        var variant = new ProductVariant
        {
            Uuid = Guid.NewGuid(), Sku = $"V{Guid.NewGuid():N}"[..12], VariantName = "Default", IsDefault = true,
            IsActive = true, FulfillmentRouteUuid = route
        };
        product.Variants.Add(variant);
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return variant.Uuid;
    }
}
