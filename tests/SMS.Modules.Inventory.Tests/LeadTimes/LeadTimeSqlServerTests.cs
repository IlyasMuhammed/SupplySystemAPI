using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Services.LeadTimes;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Inventory.Tests;

/// <summary>
/// A34 C3/C4 on SQL Server (LocalDB): the loader's, the stock read's and the variant service's queries translate and
/// return what the in-memory tests assume — supplier rates by effective date, the defaults row, free stock per
/// warehouse, all with the organization explicit for a super admin.
/// </summary>
public class LeadTimeSqlServerTests
{
    [SqlServerFact]
    public async Task A_three_level_calculation_and_the_variant_tab_run_on_SQL_Server()
    {
        await using var harness = await InventorySqlServerHarness.CreateAsync();
        var org = Guid.NewGuid();
        var routes = new FakeFulfillmentRouteLookup();
        var mfg = routes.Add(org, "MFG_PICK_PACK_SHIP", category: FulfillmentRouteCategory.Manufacture);
        var boms = new FakeBomStructureReader();
        var acme = Guid.NewGuid();

        Domain.Warehouse plant;
        ProductVariant fg, sub, raw;
        await using (var db = harness.NewContext(org))
        {
            plant = new Domain.Warehouse { Uuid = Guid.NewGuid(), Name = "Plant", Code = $"P{Guid.NewGuid():N}"[..6], IsActive = true };
            db.Warehouses.Add(plant);
            await db.SaveChangesAsync();
            fg  = await AddAsync(db, "FG", SupplyMethod.Manufacture, plant.Id, v => { v.ManufacturingLeadTimeDays = 5; v.FulfillmentRouteUuid = mfg.Uuid; });
            sub = await AddAsync(db, "Sub", SupplyMethod.Manufacture, plant.Id, v => v.ManufacturingLeadTimeDays = 3);
            raw = await AddAsync(db, "Raw", SupplyMethod.Purchase, null, v => v.DefaultSupplierId = acme);
            db.VariantSuppliers.Add(new VariantSupplier
            {
                Uuid = Guid.NewGuid(), VariantId = raw.Id, SupplierId = acme, VendorUnitCost = 1m, CurrencyId = Guid.NewGuid(),
                LeadTimeDays = 4, IsPreferred = true, IsActive = true, EffectiveFrom = DateTime.UtcNow.Date.AddDays(-1)
            });
            db.InventoryItems.Add(new InventoryItem { Uuid = Guid.NewGuid(), VariantId = sub.Id, WarehouseId = plant.Id, QtyOnHand = 3, QtyReserved = 1 });
            db.LeadTimeDefaults.Add(new LeadTimeDefaults { PickPackDays = 2, ShippingLeadTimeDays = 4, SalesBufferDays = 1, CreatedBy = 1 });
            await db.SaveChangesAsync();
        }
        boms.Add(org, fg.Uuid, 1m, (sub.Uuid, 1m, 0m));
        boms.Add(org, sub.Uuid, 1m, (raw.Uuid, 2m, 0m));

        // A super admin of the organization: no tenant filter, so every read must name the organization itself.
        await using (var db = harness.NewContext(org, superAdmin: true))
        {
            var calculator = new LeadTimeCalculator(db, new LeadTimeInputsLoader(db), new MemoryCache(new MemoryCacheOptions()), routes, boms);
            var result = await calculator.CalculateAsync(org, new LeadTimeRequest(fg.Uuid, 10));

            // FG 5 + (Sub: shortfall 10 − 2 free = 8 → 3 + (Raw 16 short → 4)) = 12; + pick/pack 2 + shipping 4 + buffer 1.
            result.Components.Single(c => c.Code == LeadTimeComponentCode.Manufacturing).Days.Should().Be(12);
            result.TotalLeadTimeDays.Should().Be(19);

            var tree = await calculator.CalculateManufacturingAsync(org, fg.Uuid, 10);
            var subIn = tree.Inputs.Single();
            subIn.FreeQty.Should().Be(2m);
            subIn.Node!.Inputs.Single().Should().BeEquivalentTo(new { RequiredQty = 16m, WaitDays = 4, Source = LeadTimeSource.SupplierRate });
        }

        await using (var db = harness.NewContext(org, superAdmin: true))
        {
            var tab = await new VariantLeadTimeService(db, new LeadTimeInputsLoader(db), routes).GetAsync(fg.Uuid);
            tab.RouteCategory.Should().Be(FulfillmentRouteCategory.Manufacture);
            tab.Components.Single(c => c.Code == LeadTimeComponentCode.PickPack).ResolvedDays.Should().Be(2);
            tab.TotalDays.Should().Be(5 + 0 + 0 + 0 + 2 + 4 + 1);
        }
    }

    private static async Task<ProductVariant> AddAsync(
        InventoryDbContext db, string name, string supplyMethod, int? productionWarehouseId, Action<ProductVariant> configure)
    {
        var product = new Product
        {
            Uuid = Guid.NewGuid(), Name = name, Sku = $"SKU{Guid.NewGuid():N}"[..12], IsActive = true, SupplyMethod = supplyMethod,
            IsManufacturable = supplyMethod == SupplyMethod.Manufacture,
            ProductType = supplyMethod == SupplyMethod.Manufacture ? ProductType.SemiFinished : ProductType.RawMaterial,
            DefaultProductionWarehouseId = productionWarehouseId
        };
        var variant = new ProductVariant { Uuid = Guid.NewGuid(), Sku = $"V{Guid.NewGuid():N}"[..12], VariantName = "Std", IsDefault = true, IsActive = true };
        configure(variant);
        product.Variants.Add(variant);
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return variant;
    }
}
