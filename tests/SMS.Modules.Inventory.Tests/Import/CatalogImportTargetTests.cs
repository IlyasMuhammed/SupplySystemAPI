using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Import;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;
using static SMS.Modules.Inventory.Tests.Import.ImportHarness;

namespace SMS.Modules.Inventory.Tests.Import;

/// <summary>A40 (TINV) — PRODUCT_CATEGORY, PRODUCT, WAREHOUSE and WAREHOUSE_LOCATION import targets.</summary>
public class CatalogImportTargetTests
{
    // ── Describe ────────────────────────────────────────────────────────────────

    [Fact]
    public void Each_target_describes_its_entity_in_the_wizard_order()
    {
        var h = new ImportHarness();
        IImportTarget[] targets = [h.Categories(), h.Products(), h.Warehouses(), h.Locations(), h.PricingRules(), h.OpeningStock()];

        targets.Select(t => (t.Describe().EntityType, t.Describe().ImportOrder)).Should().Equal(
            (ImportEntityTypes.ProductCategory, 2), (ImportEntityTypes.Product, 3), (ImportEntityTypes.Warehouse, 4),
            (ImportEntityTypes.WarehouseLocation, 5), (ImportEntityTypes.PricingRule, 10), (ImportEntityTypes.OpeningStock, 11));

        var product = h.Products().Describe();
        product.MatchFields.Should().Equal("sku");
        product.Fields.Select(f => f.FieldName).Should().Equal("sku", "name", "description", "category_code", "unit_code", "sale_price",
            "cost_price", "barcode", "weight", "min_stock_level", "max_stock_level", "is_service", "is_active",
            "available_for_retail", "available_for_pos", "available_for_mir_miv", "available_for_production", "available_for_services",
            "product_type", "supply_method");
        product.Fields.Where(f => f.FieldName is "product_type" or "supply_method").Select(f => (f.FieldType, f.IsRequired, f.LookupList))
            .Should().Equal(("LOOKUP", false, "product_types"), ("LOOKUP", false, "supply_methods"));
        product.Fields.Where(f => f.FieldName.StartsWith("available_for_")).Select(f => (f.FieldType, f.IsRequired, f.SampleValue))
            .Should().Equal(("BOOLEAN", false, "Yes"), ("BOOLEAN", false, "No"), ("BOOLEAN", false, "No"), ("BOOLEAN", false, "No"), ("BOOLEAN", false, "No"));
        product.Fields.Should().ContainSingle(f => f.IsKey).Which.FieldName.Should().Be("sku");
        product.Fields.Single(f => f.FieldName == "unit_code").LookupList.Should().Be("units");

        var opening = h.OpeningStock().Describe();
        opening.IsOpeningBalance.Should().BeTrue();
        opening.AllowUpdate.Should().BeFalse();
    }

    // ── Categories ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_parent_earlier_in_the_batch_counts_as_existing_and_deeper_nesting_is_refused()
    {
        var h = new ImportHarness();
        h.Db.ProductCategories.Add(new Domain.ProductCategory { OrganizationId = h.Org, Code = "TOOLS", Name = "Tools" });
        h.Db.ProductCategories.Add(new Domain.ProductCategory { OrganizationId = h.OtherOrg, Code = "FOOD", Name = "Food" });
        await h.Db.SaveChangesAsync();
        var tools = await h.Db.ProductCategories.IgnoreQueryFilters().SingleAsync(c => c.Code == "TOOLS");
        h.Db.ProductSubCategories.Add(new Domain.ProductSubCategory { OrganizationId = h.Org, CategoryId = tools.Id, Code = "HAND", Name = "Hand tools" });
        await h.Db.SaveChangesAsync();

        var checks = await h.Categories().ValidateAsync(h.Org,
        [
            Row(2, ("code", "ELEC"), ("name", "Electronics")),
            Row(3, ("code", "CAB"), ("name", "Cables"), ("parent_code", "ELEC")),        // in-batch parent
            Row(4, ("code", "SAW"), ("name", "Saws"), ("parent_code", "HAND")),          // HAND is a sub-category
            Row(5, ("code", "X"), ("name", "X"), ("parent_code", "CAB")),                // CAB is a sub-category row
            Row(6, ("code", "FRUIT"), ("name", "Fruit"), ("parent_code", "FOOD")),       // another organization's category
            Row(7, ("code", "PH"), ("name", "Phones"), ("parent_code", "MOBILE")),       // parent further down
            Row(8, ("code", "MOBILE"), ("name", "Mobile")),
            Row(9, ("code", "TOOLS"), ("name", "Tools & Hardware")),                     // existing → match
            Row(10, ("code", "elec"), ("name", "Electronics 2"))                         // duplicate in file
        ]);

        checks.Should().HaveCount(9);
        checks.Single(c => c.RowNumber == 2).Errors.Should().BeEmpty();
        checks.Single(c => c.RowNumber == 3).Errors.Should().BeEmpty();
        checks.Single(c => c.RowNumber == 3).ExistingEntityUuid.Should().BeNull();
        checks.Single(c => c.RowNumber == 4).Errors.Should().ContainSingle(e => e.Code == "MISSING_DEPENDENCY" && e.Message.Contains("two category levels"));
        checks.Single(c => c.RowNumber == 5).Errors.Should().ContainSingle(e => e.Code == "MISSING_DEPENDENCY" && e.Message.Contains("two category levels"));
        checks.Single(c => c.RowNumber == 6).Errors.Should().ContainSingle(e => e.Code == "MISSING_DEPENDENCY" && e.Message.Contains("does not exist"));
        checks.Single(c => c.RowNumber == 7).Errors.Should().ContainSingle(e => e.Code == "MISSING_DEPENDENCY" && e.Message.Contains("further down"));
        checks.Single(c => c.RowNumber == 9).ExistingEntityUuid.Should().Be(ImportSurrogateIds.For(ImportSurrogateIds.Category, tools.Id));
        checks.Single(c => c.RowNumber == 9).Errors.Should().BeEmpty();
        checks.Single(c => c.RowNumber == 10).Errors.Should().Contain(e => e.Code == "DUPLICATE_IN_FILE");
    }

    [Fact]
    public async Task Categories_and_their_sub_categories_are_created_in_one_batch_and_an_update_keeps_empty_fields()
    {
        var h = new ImportHarness();
        var outcomes = await h.Categories().ApplyAsync(h.Org, 7,
        [
            Create(2, ("code", "ELEC"), ("name", "Electronics"), ("description", "All electronics")),
            Create(3, ("code", "CAB"), ("name", "Cables"), ("parent_code", "ELEC"), ("is_active", "true"))
        ]);

        outcomes.Select(o => o.Status).Should().Equal("IMPORTED", "IMPORTED");
        var elec = await h.Db.ProductCategories.IgnoreQueryFilters().SingleAsync(c => c.Code == "ELEC");
        elec.OrganizationId.Should().Be(h.Org);
        var cab = await h.Db.ProductSubCategories.IgnoreQueryFilters().SingleAsync(s => s.Code == "CAB");
        cab.CategoryId.Should().Be(elec.Id);
        cab.OrganizationId.Should().Be(h.Org);
        outcomes[0].EntityUuid.Should().Be(ImportSurrogateIds.For(ImportSurrogateIds.Category, elec.Id));
        outcomes[1].EntityUuid.Should().Be(ImportSurrogateIds.For(ImportSurrogateIds.SubCategory, cab.Id));

        var update = await h.Categories().ApplyAsync(h.Org, 7,
            [Update(2, outcomes[0].EntityUuid, ("code", "ELEC"), ("name", "Electronics & Power"), ("description", null))]);

        update.Single().Status.Should().Be("UPDATED");
        await using var fresh = h.NewContext();
        var reread = await fresh.ProductCategories.IgnoreQueryFilters().SingleAsync(c => c.Code == "ELEC");
        reread.Name.Should().Be("Electronics & Power");
        reread.Description.Should().Be("All electronics", "a null field on UPDATE leaves the value unchanged");
    }

    // ── Products ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Product_validation_resolves_lookups_and_refuses_what_the_product_rules_refuse()
    {
        var h = new ImportHarness("PCS", "KG");
        h.Db.ProductCategories.Add(new Domain.ProductCategory { OrganizationId = h.Org, Code = "ELEC", Name = "Electronics" });
        await h.Db.SaveChangesAsync();
        var existing = await h.ProductAsync("OLD-1", "Old cable");
        var existingService = await h.ProductAsync("SVC-1", "Install", type: ProductType.Service, stockable: false);
        await h.ProductAsync("OTHER", "Other org product", org: h.OtherOrg);
        existing.Barcode = "111";
        await h.Db.SaveChangesAsync();

        var checks = await h.Products().ValidateAsync(h.Org,
        [
            Row(2, ("sku", "NEW-1"), ("name", "New cable"), ("category_code", "ELEC"), ("unit_code", "pcs"), ("sale_price", "10"), ("cost_price", "7")),
            Row(3, ("sku", "NEW-2"), ("name", "Thing"), ("category_code", "NOPE"), ("unit_code", "BOX")),
            Row(4, ("sku", "NEW-1"), ("name", "Dup")),
            Row(5, ("sku", "OLD-1"), ("name", "Old cable v2"), ("is_service", "true")),
            Row(6, ("sku", "NEW-3"), ("name", "Old cable")),                                       // name taken
            Row(7, ("sku", "NEW-4"), ("name", "Barcode clash"), ("barcode", "111")),
            Row(8, ("sku", "NEW-5"), ("name", "Levels"), ("min_stock_level", "10"), ("max_stock_level", "5")),
            Row(9, ("sku", "SVC-1"), ("is_service", "true")),                                       // service stays a service
            Row(10, ("sku", "OTHER"), ("name", "Mine now")),                                        // another org's SKU is free
            Row(11, ("sku", "SVC-1"), ("is_service", "false"))                                      // service → what? needs product_type
        ]);

        checks.Single(c => c.RowNumber == 2).Errors.Should().BeEmpty();
        checks.Single(c => c.RowNumber == 3).Errors.Select(e => (e.Field, e.Code)).Should().BeEquivalentTo(
            new[] { ("category_code", "LOOKUP_NOT_FOUND"), ("unit_code", "LOOKUP_NOT_FOUND") });
        checks.Single(c => c.RowNumber == 4).Errors.Should().Contain(e => e.Code == "DUPLICATE_IN_FILE");
        var row5 = checks.Single(c => c.RowNumber == 5);
        row5.ExistingEntityUuid.Should().Be((await h.Db.Products.IgnoreQueryFilters().SingleAsync(p => p.Sku == "OLD-1")).Uuid);
        row5.Errors.Should().BeEmpty("the product edit screen may turn a stock item into a service");
        checks.Single(c => c.RowNumber == 11).Errors.Should().Contain(e => e.Code == "TYPE_CHANGE_REFUSED" && e.Field == "is_service");
        checks.Single(c => c.RowNumber == 6).Errors.Should().ContainSingle(e => e.Code == "DUPLICATE_NAME");
        checks.Single(c => c.RowNumber == 7).Errors.Should().ContainSingle(e => e.Code == "DUPLICATE_IN_DB" && e.Field == "barcode");
        checks.Single(c => c.RowNumber == 8).Errors.Should().ContainSingle(e => e.Field == "max_stock_level");
        checks.Single(c => c.RowNumber == 9).Errors.Should().BeEmpty();
        checks.Single(c => c.RowNumber == 9).ExistingEntityUuid.Should().NotBeNull();
        checks.Single(c => c.RowNumber == 10).ExistingEntityUuid.Should().BeNull();
        existingService.Should().NotBeNull();
    }

    [Fact]
    public async Task A_product_is_created_with_one_default_variant_and_a_service_is_not_stockable()
    {
        var h = new ImportHarness();
        h.Db.ProductCategories.Add(new Domain.ProductCategory { OrganizationId = h.Org, Code = "ELEC", Name = "Electronics" });
        await h.Db.SaveChangesAsync();
        var elec = await h.Db.ProductCategories.IgnoreQueryFilters().SingleAsync();
        h.Db.ProductSubCategories.Add(new Domain.ProductSubCategory { OrganizationId = h.Org, CategoryId = elec.Id, Code = "CAB", Name = "Cables" });
        await h.Db.SaveChangesAsync();

        var outcomes = await h.Products().ApplyAsync(h.Org, 42,
        [
            Create(2, ("sku", "CAB-4MM"), ("name", "4mm cable"), ("category_code", "CAB"), ("unit_code", "kg"), ("sale_price", "150"),
                ("cost_price", "100.5"), ("barcode", "8900001"), ("weight", "1.25"), ("min_stock_level", "5"), ("max_stock_level", "50")),
            Create(3, ("sku", "INST"), ("name", "Installation"), ("is_service", "true"), ("sale_price", "2000"))
        ]);

        outcomes.Select(o => o.Status).Should().Equal("IMPORTED", "IMPORTED");
        await using var db = h.NewContext();
        var cable = await db.Products.IgnoreQueryFilters().Include(p => p.Variants).SingleAsync(p => p.Sku == "CAB-4MM");
        cable.Uuid.Should().Be(outcomes[0].EntityUuid);
        cable.OrganizationId.Should().Be(h.Org);
        cable.CategoryId.Should().Be(elec.Id, "a sub-category code gives the product that sub-category and its parent");
        cable.SubCategoryId.Should().NotBeNull();
        cable.UomCode.Should().Be("KG");
        cable.CreatedBy.Should().Be(42);
        (cable.ProductType, cable.SupplyMethod, cable.IsStockable).Should().Be((ProductType.StockItem, SupplyMethod.Purchase, true));
        var variant = cable.Variants.Should().ContainSingle().Subject;
        (variant.Sku, variant.IsDefault, variant.Barcode, variant.SellingPrice, variant.PurchasePrice, variant.WeightKg, variant.CreatedBy)
            .Should().Be(("CAB-4MM", true, "8900001", 150m, 100.5m, 1.25m, 42));
        variant.OrganizationId.Should().Be(h.Org);

        var service = await db.Products.IgnoreQueryFilters().Include(p => p.Variants).SingleAsync(p => p.Sku == "INST");
        (service.ProductType, service.SupplyMethod, service.IsStockable, service.IsSaleable).Should()
            .Be((ProductType.Service, SupplyMethod.Service, false, true));
        service.Variants.Single().PurchasePrice.Should().Be(0m);
    }

    [Fact]
    public async Task A_product_update_changes_only_the_sent_fields()
    {
        var h = new ImportHarness();
        var created = await h.Products().ApplyAsync(h.Org, 1,
            [Create(2, ("sku", "A1"), ("name", "Alpha"), ("description", "First"), ("sale_price", "10"), ("cost_price", "6"), ("barcode", "B1"))]);

        var check = await h.Products().ValidateAsync(h.Org, [Row(2, ("sku", "A1"), ("sale_price", "12"))]);
        check.Single().ExistingEntityUuid.Should().Be(created.Single().EntityUuid);
        check.Single().Errors.Should().BeEmpty();

        var updated = await h.Products().ApplyAsync(h.Org, 2,
            [Update(2, created.Single().EntityUuid, ("sku", "A1"), ("name", null), ("description", null), ("sale_price", "12"),
                ("cost_price", null), ("barcode", null), ("is_active", "false"))]);

        updated.Single().Status.Should().Be("UPDATED");
        await using var db = h.NewContext();
        var p = await db.Products.IgnoreQueryFilters().Include(x => x.Variants).SingleAsync(x => x.Sku == "A1");
        (p.Name, p.Description, p.IsActive, p.Status).Should().Be(("Alpha", "First", false, "INACTIVE"));
        var v = p.Variants.Single();
        (v.SellingPrice, v.PurchasePrice, v.Barcode).Should().Be((12m, 6m, "B1"));
    }

    [Fact]
    public async Task Channel_flags_are_set_on_the_default_variant_blank_means_unticked_on_create_and_unchanged_on_update()
    {
        var h = new ImportHarness();
        var created = await h.Products().ApplyAsync(h.Org, 1,
        [
            Create(2, ("sku", "RM-1"), ("name", "Raw one"), ("available_for_retail", "true"), ("available_for_pos", "false"),
                ("available_for_mir_miv", "Yes"), ("available_for_services", "true")),
            Create(3, ("sku", "PLAIN"), ("name", "Plain"))
        ]);

        await using (var db = h.NewContext())
        {
            var v = await db.ProductVariants.IgnoreQueryFilters().SingleAsync(x => x.Sku == "RM-1");
            (v.IsAvailableForRetail, v.IsAvailableForPos, v.IsAvailableForMirMiv, v.IsAvailableForProduction, v.IsAvailableForServices)
                .Should().Be((true, false, true, false, true));
            var plain = await db.ProductVariants.IgnoreQueryFilters().SingleAsync(x => x.Sku == "PLAIN");
            (plain.IsAvailableForRetail, plain.IsAvailableForPos, plain.IsAvailableForMirMiv, plain.IsAvailableForProduction, plain.IsAvailableForServices)
                .Should().Be((false, false, false, false, false), "a blank column keeps the unticked default");
        }

        await h.Products().ApplyAsync(h.Org, 1,
            [Update(2, created[0].EntityUuid, ("sku", "RM-1"), ("available_for_retail", null), ("available_for_pos", "true"), ("available_for_mir_miv", "false"))]);

        await using var after = h.NewContext();
        var updated = await after.ProductVariants.IgnoreQueryFilters().SingleAsync(x => x.Sku == "RM-1");
        (updated.IsAvailableForRetail, updated.IsAvailableForPos, updated.IsAvailableForMirMiv, updated.IsAvailableForServices)
            .Should().Be((true, true, false, true), "blank leaves a flag unchanged");
    }

    [Fact]
    public async Task Channel_flags_respect_the_product_type_rules()
    {
        var h = new ImportHarness();
        await h.ProductAsync("RAW", "Copper", type: ProductType.RawMaterial);

        var checks = await h.Products().ValidateAsync(h.Org,
        [
            Row(2, ("sku", "SVC"), ("name", "Repair"), ("is_service", "true"), ("available_for_mir_miv", "true")),
            Row(3, ("sku", "SVC2"), ("name", "Install"), ("is_service", "true"), ("available_for_production", "true"), ("available_for_retail", "true")),
            Row(4, ("sku", "ITEM"), ("name", "Item"), ("available_for_production", "true")),        // stock item: not a BOM input
            Row(5, ("sku", "RAW"), ("available_for_production", "true")),                            // raw material: allowed
            Row(6, ("sku", "ODD"), ("name", "Odd"), ("available_for_pos", "maybe")),
            Row(7, ("sku", "SVC3"), ("name", "Clean"), ("is_service", "true"), ("available_for_mir_miv", "false"))
        ]);

        checks[0].Errors.Should().BeEmpty("the product screens do not refuse MIR/MIV for a service, so neither does import");
        checks[1].Errors.Should().BeEmpty("a service may be a production (service BOM) input, as the product screens allow");
        checks[2].Errors.Should().ContainSingle(e => e.Field == "available_for_production" && e.Code == "RULE_VIOLATION");
        checks[3].Errors.Should().BeEmpty();
        checks[4].Errors.Should().ContainSingle(e => e.Field == "available_for_pos" && e.Code == "INVALID_TYPE");
        checks[5].Errors.Should().BeEmpty("No is always allowed");
    }

    [Fact]
    public async Task A_bad_row_fails_the_whole_product_batch_and_nothing_is_written()
    {
        var h = new ImportHarness();
        await h.ProductAsync("TAKEN", "Taken");

        var act = () => h.Products().ApplyAsync(h.Org, 1,
        [
            Create(2, ("sku", "FINE-1"), ("name", "Fine one")),
            Create(3, ("sku", "TAKEN"), ("name", "Clash"))
        ]);

        await act.Should().ThrowAsync<UnprocessableEntityException>().WithMessage("*Row 3*");
        await using var db = h.NewContext();
        (await db.Products.IgnoreQueryFilters().AnyAsync(p => p.Sku == "FINE-1")).Should().BeFalse();
        h.Db.ChangeTracker.Entries().Should().BeEmpty("the scoped context is clean for the next batch");
    }

    [Fact]
    public async Task Product_template_lists_carry_categories_sub_categories_and_units()
    {
        var h = new ImportHarness("PCS", "BOX");
        h.Db.ProductCategories.Add(new Domain.ProductCategory { OrganizationId = h.Org, Code = "ELEC", Name = "Electronics" });
        h.Db.ProductCategories.Add(new Domain.ProductCategory { OrganizationId = h.OtherOrg, Code = "FOOD", Name = "Food" });
        await h.Db.SaveChangesAsync();

        var lists = await h.Products().GetTemplateListsAsync(h.Org);

        lists.Single(l => l.Key == "categories").Values.Should().Equal("ELEC");
        lists.Single(l => l.Key == "sub_categories").Values.Should().BeEmpty();
        lists.Single(l => l.Key == "units").Values.Should().Equal("PCS", "BOX");
        lists.Single(l => l.Key == "product_types").Values.Should().Equal(ProductType.All);
        lists.Single(l => l.Key == "supply_methods").Values.Should().Equal(SupplyMethod.All);
    }

    [Fact]
    public async Task Product_type_and_supply_method_follow_the_classification_rules_on_create()
    {
        var h = new ImportHarness();
        var checks = await h.Products().ValidateAsync(h.Org,
        [
            Row(2, ("sku", "CU"), ("name", "Copper rod"), ("product_type", "raw_material"), ("available_for_production", "true")),
            Row(3, ("sku", "FG"), ("name", "Cable drum"), ("product_type", "FINISHED_GOOD")),
            Row(4, ("sku", "X1"), ("name", "Clash"), ("product_type", "RAW_MATERIAL"), ("is_service", "true")),
            Row(5, ("sku", "X2"), ("name", "Bought FG"), ("product_type", "FINISHED_GOOD"), ("supply_method", "PURCHASE")),
            Row(6, ("sku", "X3"), ("name", "Made raw"), ("product_type", "RAW_MATERIAL"), ("supply_method", "MANUFACTURE")),
            Row(7, ("sku", "X4"), ("name", "Gadget"), ("product_type", "GADGET")),
            Row(8, ("sku", "X5"), ("name", "Odd"), ("supply_method", "STEAL")),
            Row(9, ("sku", "SV"), ("name", "Survey"), ("product_type", "SERVICE"), ("is_service", "true"))
        ]);

        checks[0].Errors.Should().BeEmpty("a raw material is a production input");
        checks[1].Errors.Should().BeEmpty();
        checks[2].Errors.Should().ContainSingle(e => e.Field == "is_service" && e.Code == "RULE_VIOLATION");
        checks[3].Errors.Should().ContainSingle(e => e.Code == "RULE_VIOLATION" && e.Message.Contains("MANUFACTURE"));
        checks[4].Errors.Should().ContainSingle(e => e.Code == "RULE_VIOLATION" && e.Message.Contains("cannot be manufactured"));
        checks[5].Errors.Should().ContainSingle(e => e.Field == "product_type" && e.Code == "LOOKUP_NOT_FOUND");
        checks[6].Errors.Should().ContainSingle(e => e.Field == "supply_method" && e.Code == "LOOKUP_NOT_FOUND");
        checks[7].Errors.Should().BeEmpty();

        await h.Products().ApplyAsync(h.Org, 1,
        [
            Create(2, ("sku", "CU"), ("name", "Copper rod"), ("product_type", "RAW_MATERIAL"), ("available_for_production", "true")),
            Create(3, ("sku", "FG"), ("name", "Cable drum"), ("product_type", "FINISHED_GOOD"))
        ]);
        await using var db = h.NewContext();
        var cu = await db.Products.IgnoreQueryFilters().Include(p => p.Variants).SingleAsync(p => p.Sku == "CU");
        (cu.ProductType, cu.SupplyMethod, cu.IsSaleable, cu.IsPurchasable, cu.IsStockable, cu.IsManufacturable).Should()
            .Be((ProductType.RawMaterial, SupplyMethod.Purchase, false, true, true, false));
        cu.Variants.Single().IsAvailableForProduction.Should().BeTrue();
        var fg = await db.Products.IgnoreQueryFilters().SingleAsync(p => p.Sku == "FG");
        (fg.ProductType, fg.SupplyMethod, fg.IsSaleable, fg.IsPurchasable, fg.IsManufacturable).Should()
            .Be((ProductType.FinishedGood, SupplyMethod.Manufacture, true, false, true));
    }

    [Fact]
    public async Task A_type_change_on_update_follows_the_product_edit_screen()
    {
        var h = new ImportHarness();
        var raw = await h.ProductAsync("RAW", "Copper", type: ProductType.RawMaterial);
        raw.IsAvailableForProduction = true;
        await h.Db.SaveChangesAsync();
        var svc = await h.ProductAsync("SVC", "Repair", type: ProductType.Service, stockable: false);
        var svcProduct = await h.Db.Products.IgnoreQueryFilters().SingleAsync(p => p.Sku == "SVC");
        svcProduct.SupplyMethod = SupplyMethod.Service;
        svcProduct.ServiceCategory = ServiceCategory.Repair;
        await h.Db.SaveChangesAsync();
        var rawUuid = (await h.Db.Products.IgnoreQueryFilters().SingleAsync(p => p.Sku == "RAW")).Uuid;

        var refused = await h.Products().ValidateAsync(h.Org, [Row(2, ("sku", "RAW"), ("product_type", "STOCK_ITEM"))]);
        refused.Single().Errors.Should().ContainSingle(e => e.Field == "product_type" && e.Message.Contains("available for production"));

        // Unticking production on the same row lets the type change through; the flags re-default to the new type's.
        await h.Products().ApplyAsync(h.Org, 1,
        [
            Update(2, rawUuid, ("sku", "RAW"), ("product_type", "STOCK_ITEM"), ("available_for_production", "false")),
            Update(3, svcProduct.Uuid, ("sku", "SVC"), ("product_type", "CONSUMABLE"))
        ]);

        await using var db = h.NewContext();
        var p = await db.Products.IgnoreQueryFilters().Include(x => x.Variants).SingleAsync(x => x.Sku == "RAW");
        (p.ProductType, p.SupplyMethod, p.IsSaleable, p.IsPurchasable, p.IsStockable).Should()
            .Be((ProductType.StockItem, SupplyMethod.Purchase, true, true, true));
        p.Variants.Single().IsAvailableForProduction.Should().BeFalse();
        var s = await db.Products.IgnoreQueryFilters().SingleAsync(x => x.Sku == "SVC");
        (s.ProductType, s.SupplyMethod, s.IsStockable, s.ServiceCategory).Should()
            .Be((ProductType.Consumable, SupplyMethod.Purchase, true, (string?)null), "a product that stops being a service keeps no service configuration");
        svc.Should().NotBeNull();
    }

    // ── Warehouses and locations ────────────────────────────────────────────────

    [Fact]
    public async Task Warehouses_are_created_matched_by_code_and_updated_without_clearing()
    {
        var h = new ImportHarness();
        var created = await h.Warehouses().ApplyAsync(h.Org, 5,
            [Create(2, ("code", "WH-1"), ("name", "Main"), ("address", "1 Mall Road"), ("city", "Lahore"), ("country_code", "PK"))]);

        var check = await h.Warehouses().ValidateAsync(h.Org, [Row(2, ("code", "wh-1"), ("name", "Main 2")), Row(3, ("code", "WH-2"))]);
        check[0].ExistingEntityUuid.Should().Be(created.Single().EntityUuid);
        check[1].Errors.Should().ContainSingle(e => e.Code == "REQUIRED" && e.Field == "name");

        await h.Warehouses().ApplyAsync(h.Org, 5,
            [Update(2, created.Single().EntityUuid, ("code", "WH-1"), ("name", "Main Store"), ("address", null), ("is_active", "false"))]);

        await using var db = h.NewContext();
        var w = await db.Warehouses.IgnoreQueryFilters().SingleAsync();
        (w.Name, w.Address, w.City, w.Country, w.IsActive, w.CreatedBy, w.OrganizationId).Should()
            .Be(("Main Store", "1 Mall Road", "Lahore", "PK", false, 5, h.Org));
    }

    [Fact]
    public async Task Locations_create_zones_and_bins_and_match_existing_ones()
    {
        var h = new ImportHarness();
        await h.WarehouseAsync("WH1");

        var checks = await h.Locations().ValidateAsync(h.Org,
        [
            Row(2, ("warehouse_code", "WH1"), ("zone_code", "A"), ("zone_name", "Zone A")),
            Row(3, ("warehouse_code", "WH1"), ("zone_code", "A"), ("bin_code", "A-01"), ("bin_name", "Top shelf")),
            Row(4, ("warehouse_code", "WH1"), ("zone_code", "B"), ("bin_code", "B-01")),
            Row(5, ("warehouse_code", "NOPE"), ("zone_code", "A")),
            Row(6, ("warehouse_code", "WH1"), ("zone_code", "a"), ("bin_code", "a-01"))
        ]);
        checks.Take(3).Should().OnlyContain(c => c.Errors.Count == 0);
        checks[3].Errors.Should().ContainSingle(e => e.Code == "LOOKUP_NOT_FOUND");
        checks[4].Errors.Should().ContainSingle(e => e.Code == "DUPLICATE_IN_FILE");

        var outcomes = await h.Locations().ApplyAsync(h.Org, 1,
        [
            Create(2, ("warehouse_code", "WH1"), ("zone_code", "A"), ("zone_name", "Zone A")),
            Create(3, ("warehouse_code", "WH1"), ("zone_code", "A"), ("bin_code", "A-01"), ("bin_name", "Top shelf")),
            Create(4, ("warehouse_code", "WH1"), ("zone_code", "B"), ("bin_code", "b-01"))
        ]);

        await using var db = h.NewContext();
        var zones = await db.Zones.IgnoreQueryFilters().OrderBy(z => z.Code).ToListAsync();
        zones.Select(z => (z.Code, z.Name, z.OrganizationId)).Should().Equal(("A", "Zone A", h.Org), ("B", "B", h.Org));
        var bins = await db.Bins.IgnoreQueryFilters().OrderBy(b => b.Code).ToListAsync();
        bins.Select(b => (b.Code, b.Description, b.ZoneId)).Should().Equal(("A-01", "Top shelf", zones[0].Id), ("B-01", null, zones[1].Id));
        outcomes[1].EntityUuid.Should().Be(ImportSurrogateIds.For(ImportSurrogateIds.Bin, bins[0].Id));

        var again = await h.Locations().ValidateAsync(h.Org, [Row(2, ("warehouse_code", "WH1"), ("zone_code", "A"), ("bin_code", "A-01"))]);
        again.Single().ExistingEntityUuid.Should().Be(outcomes[1].EntityUuid);
    }

    [Fact]
    public void Surrogate_ids_round_trip_and_never_decode_as_another_kind()
    {
        var uuid = ImportSurrogateIds.For(ImportSurrogateIds.Zone, 1234);
        ImportSurrogateIds.TryRead(ImportSurrogateIds.Zone, uuid, out var id).Should().BeTrue();
        id.Should().Be(1234);
        ImportSurrogateIds.TryRead(ImportSurrogateIds.Bin, uuid, out _).Should().BeFalse();
        ImportSurrogateIds.For(ImportSurrogateIds.Zone, 1234).Should().Be(uuid);
    }
}
