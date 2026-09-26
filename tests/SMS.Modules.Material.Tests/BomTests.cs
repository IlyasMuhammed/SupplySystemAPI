using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Models;
using SMS.Modules.Material.Repositories;
using SMS.Modules.Material.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Material.Tests;

/// <summary>
/// Bills of materials (A30 §7–§9; A30-P2-01..08, P2-12). T-PB02..T-PB09 and the product side of
/// T-CM02. Products and variants are read from Inventory's context, so both are seeded here.
/// </summary>
public class BomTests
{
    private const int Author   = 7;
    private const int Reviewer = 8;

    private sealed class Harness
    {
        public MaterialDbContext Material { get; }
        public InventoryDbContext Inventory { get; }
        public BomRepository Repo { get; }
        public BomCostService Cost { get; }
        public Guid Plant { get; }
        private int _sequence;

        public Harness()
        {
            var tenant = new StaticTenantContext();
            Material = new MaterialDbContext(new DbContextOptionsBuilder<MaterialDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);
            Inventory = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);

            var numbers = new Mock<IDocumentNumberGenerator>();
            numbers.Setup(n => n.NextAsync("BOM", It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(() => $"BOM-2026-{++_sequence:D5}");

            var plant = new Warehouse { Uuid = Guid.NewGuid(), Code = "PLANT", Name = "Plant", IsActive = true, CreatedBy = 1 };
            Inventory.Warehouses.Add(plant);
            Inventory.SaveChanges();
            Plant = plant.Uuid;

            Repo = new BomRepository(Material, Inventory, numbers.Object);
            Cost = new BomCostService(Material, Inventory);
        }

        /// <summary>One product with one default variant. Returns (productUuid, variantUuid).</summary>
        public (Guid Product, Guid Variant) Product(
            string name, string type = SMS.Shared.Common.ProductType.RawMaterial, string? supplyMethod = null,
            bool forProduction = true, decimal purchasePrice = 10m, decimal? lastPurchasePrice = null, string uom = "PCS")
        {
            var product = new Product
            {
                Uuid = Guid.NewGuid(), Sku = $"SKU-{Guid.NewGuid():N}"[..12], Name = name, UomCode = uom,
                ProductType = type, SupplyMethod = supplyMethod ?? ProductTypeRules.DefaultSupplyMethod(type),
                IsActive = true, CreatedBy = 1
            };
            var variant = new ProductVariant
            {
                Uuid = Guid.NewGuid(), Sku = $"{product.Sku}-1", VariantName = "Default", IsDefault = true, IsActive = true,
                PurchasePrice = purchasePrice, LastPurchasePrice = lastPurchasePrice,
                IsAvailableForProduction = forProduction, CreatedBy = 1
            };
            product.Variants.Add(variant);
            Inventory.Products.Add(product);
            Inventory.SaveChanges();
            return (product.Uuid, variant.Uuid);
        }
    }

    private static BomLineRequest Line(Guid variant, decimal qty = 1m, decimal scrap = 0m, bool critical = true, Guid? alternate = null) =>
        new() { MaterialVariantUuid = variant, Quantity = qty, ScrapPercentage = scrap, IsCritical = critical, AlternateVariantUuid = alternate };

    private static CreateBomRequest Recipe(Guid product, params BomLineRequest[] lines) =>
        new() { ProductUuid = product, BaseQuantity = 1m, Lines = [.. lines] };

    private static async Task<Guid> ActiveRecipeAsync(Harness h, Guid product, params BomLineRequest[] lines)
    {
        var uuid = await h.Repo.CreateAsync(Recipe(product, lines), Author);
        await h.Repo.SubmitAsync(uuid, Author);
        await h.Repo.ApproveAsync(uuid, Reviewer);
        await h.Repo.ActivateAsync(uuid, Reviewer);
        return uuid;
    }

    private static async Task<BomDetailModel> Detail(Harness h, Guid uuid) => (await h.Repo.GetByUuidAsync(uuid))!;

    // ── Creating (T-PB01/02/03) ───────────────────────────────────────────────

    [Fact]
    public async Task A_recipe_for_a_manufactured_product_is_numbered_versioned_and_reads_back_with_names()
    {
        var h = new Harness();
        var (shirt, _)  = h.Product("Printed T-Shirt", SMS.Shared.Common.ProductType.FinishedGood);
        var (plain, plainV) = h.Product("Plain T-Shirt");
        var (ink, inkV)     = h.Product("Printing Ink", uom: "L");

        var uuid = await h.Repo.CreateAsync(new CreateBomRequest
        {
            ProductUuid = shirt, BaseQuantity = 1, WarehouseUuid = h.Plant, Notes = "Screen print, front only.",
            Lines = [Line(plainV, 1), Line(inkV, 0.05m, scrap: 5)]
        }, Author);

        var bom = await Detail(h, uuid);
        bom.BomNumber.Should().Be("BOM-2026-00001");
        bom.Version.Should().Be(1);
        bom.Status.Should().Be(BomStatus.Draft);
        bom.ProductName.Should().Be("Printed T-Shirt");
        bom.BaseUom.Should().Be("PCS", "defaulted from the product");
        bom.WarehouseName.Should().Be("Plant");
        bom.Lines.Should().HaveCount(2);
        bom.Lines[0].Sequence.Should().Be(10);
        bom.Lines[0].MaterialProductName.Should().Be("Plain T-Shirt");
        bom.Lines[1].Uom.Should().Be("L", "defaulted from the material");
        bom.Lines[1].GrossQuantity.Should().Be(0.0525m, "5% scrap allowance on 0.05");
        bom.Lines[1].MaterialSupplyMethod.Should().Be(SupplyMethod.Purchase);
    }

    [Fact]
    public async Task A_recipe_cannot_be_made_for_a_product_that_is_not_manufactured()
    {
        var h = new Harness();
        var (laptop, _) = h.Product("Laptop", SMS.Shared.Common.ProductType.StockItem);
        var (_, partV)  = h.Product("Part");

        var act = () => h.Repo.CreateAsync(Recipe(laptop, Line(partV)), Author);

        await act.Should().ThrowAsync<BadRequestException>().WithMessage("*Laptop is not configured for manufacturing*");
    }

    [Fact]
    public async Task A_line_must_be_a_variant_that_is_available_for_production()
    {
        // Decision D2: the spec's is_bom_input is the variant's IsAvailableForProduction.
        var h = new Harness();
        var (shirt, _) = h.Product("Printed T-Shirt", SMS.Shared.Common.ProductType.FinishedGood);
        var (_, sofaV) = h.Product("Showroom Sofa", forProduction: false);

        var act = () => h.Repo.CreateAsync(Recipe(shirt, Line(sofaV)), Author);

        await act.Should().ThrowAsync<BadRequestException>()
            .WithMessage("*Showroom Sofa*not configured as a BOM input*Available for production*");
    }

    [Fact]
    public async Task A_recipe_cannot_use_its_own_product_as_an_input()
    {
        var h = new Harness();
        var (bolt, boltV) = h.Product("Steel Bolt", SMS.Shared.Common.ProductType.FinishedGood);

        var act = () => h.Repo.CreateAsync(Recipe(bolt, Line(boltV)), Author);

        await act.Should().ThrowAsync<BadRequestException>().WithMessage("*Circular BOM reference*input of itself*");
    }

    [Fact]
    public async Task A_cycle_through_another_recipe_is_refused_and_a_plain_chain_is_not()
    {
        // T-CM02 / T-PB03: bolt -> kit is chained manufacturing and fine; kit -> bolt -> kit is a cycle.
        var h = new Harness();
        var (bolt, boltV) = h.Product("Steel Bolt M10", SMS.Shared.Common.ProductType.FinishedGood);
        var (kit,  kitV)  = h.Product("Bolt Assembly Kit", SMS.Shared.Common.ProductType.FinishedGood);
        var (_, rodV)     = h.Product("Steel Rod");

        await h.Repo.CreateAsync(Recipe(bolt, Line(rodV)), Author);
        var kitBom = await h.Repo.CreateAsync(Recipe(kit, Line(boltV, 10)), Author);
        (await Detail(h, kitBom)).Lines.Single().MaterialProductName.Should().Be("Steel Bolt M10");

        // Now try to make the bolt out of the kit.
        var cycle = () => h.Repo.CreateAsync(Recipe(bolt, Line(kitV)), Author);
        await cycle.Should().ThrowAsync<BadRequestException>().WithMessage("*Circular BOM reference*Steel Bolt M10*");
    }

    [Theory]
    [InlineData(0, 1, 0, "Base quantity")]
    [InlineData(1, 0, 0, "quantity must be greater than zero")]
    [InlineData(1, 1, 100, "scrap percentage")]
    [InlineData(1, 1, -1, "scrap percentage")]
    public async Task Bad_quantities_are_refused(decimal baseQty, decimal lineQty, decimal scrap, string message)
    {
        var h = new Harness();
        var (shirt, _) = h.Product("Printed T-Shirt", SMS.Shared.Common.ProductType.FinishedGood);
        var (_, plainV) = h.Product("Plain T-Shirt");

        var act = () => h.Repo.CreateAsync(new CreateBomRequest
        {
            ProductUuid = shirt, BaseQuantity = baseQty, Lines = [Line(plainV, lineQty, scrap)]
        }, Author);

        await act.Should().ThrowAsync<BadRequestException>().WithMessage($"*{message}*");
    }

    [Fact]
    public async Task Duplicate_materials_reversed_dates_and_a_self_alternate_are_refused()
    {
        var h = new Harness();
        var (shirt, _) = h.Product("Printed T-Shirt", SMS.Shared.Common.ProductType.FinishedGood);
        var (_, plainV) = h.Product("Plain T-Shirt");

        var twice = () => h.Repo.CreateAsync(Recipe(shirt, Line(plainV), Line(plainV, 2)), Author);
        await twice.Should().ThrowAsync<BadRequestException>().WithMessage("*more than one line*");

        var dates = () => h.Repo.CreateAsync(new CreateBomRequest
        {
            ProductUuid = shirt, EffectiveFrom = new DateTime(2026, 12, 1), EffectiveTo = new DateTime(2026, 1, 1), Lines = [Line(plainV)]
        }, Author);
        await dates.Should().ThrowAsync<BadRequestException>().WithMessage("*Effective from must be before*");

        var selfAlternate = () => h.Repo.CreateAsync(Recipe(shirt, Line(plainV, alternate: plainV)), Author);
        await selfAlternate.Should().ThrowAsync<BadRequestException>().WithMessage("*cannot be the material itself*");
    }

    // ── Editing (§8.1, T-PB08) ────────────────────────────────────────────────

    [Fact]
    public async Task A_draft_is_edited_in_place_a_submitted_one_is_not_and_a_rejected_one_returns_to_draft()
    {
        var h = new Harness();
        var (shirt, _)  = h.Product("Printed T-Shirt", SMS.Shared.Common.ProductType.FinishedGood);
        var (_, plainV) = h.Product("Plain T-Shirt");
        var (_, inkV)   = h.Product("Printing Ink");
        var uuid = await h.Repo.CreateAsync(Recipe(shirt, Line(plainV)), Author);

        await h.Repo.UpdateAsync(uuid, new UpdateBomRequest { BaseQuantity = 10, Lines = [Line(plainV, 10), Line(inkV, 0.5m)] }, Author);
        var edited = await Detail(h, uuid);
        edited.BaseQuantity.Should().Be(10);
        edited.Lines.Should().HaveCount(2);
        edited.Version.Should().Be(1, "editing a draft is not a new version");

        await h.Repo.SubmitAsync(uuid, Author);
        var underReview = () => h.Repo.UpdateAsync(uuid, new UpdateBomRequest { Notes = "x" }, Author);
        await underReview.Should().ThrowAsync<BadRequestException>().WithMessage("*under review*");

        await h.Repo.RejectAsync(uuid, Reviewer, "Ink quantity looks wrong.");
        (await Detail(h, uuid)).RejectionReason.Should().Be("Ink quantity looks wrong.");

        await h.Repo.UpdateAsync(uuid, new UpdateBomRequest { Lines = [Line(plainV, 10), Line(inkV, 0.7m)] }, Author);
        (await Detail(h, uuid)).Status.Should().Be(BomStatus.Draft, "a revised rejection is a draft again");
    }

    [Fact]
    public async Task An_approved_or_active_recipe_cannot_be_edited_or_deleted_only_versioned()
    {
        var h = new Harness();
        var (shirt, _)  = h.Product("Printed T-Shirt", SMS.Shared.Common.ProductType.FinishedGood);
        var (_, plainV) = h.Product("Plain T-Shirt");
        var uuid = await ActiveRecipeAsync(h, shirt, Line(plainV));

        var edit = () => h.Repo.UpdateAsync(uuid, new UpdateBomRequest { Notes = "x" }, Author);
        await edit.Should().ThrowAsync<BadRequestException>().WithMessage("*Create a new version instead*");

        var delete = () => h.Repo.DeleteAsync(uuid, Author);
        await delete.Should().ThrowAsync<BadRequestException>();

        var draft = await h.Repo.CreateAsync(Recipe(shirt, Line(plainV)), Author);
        await h.Repo.DeleteAsync(draft, Author);
        (await h.Repo.GetByUuidAsync(draft)).Should().BeNull();
    }

    // ── Workflow (§9.1, §9.3; T-PB04/05/07) ──────────────────────────────────

    [Fact]
    public async Task Submit_approve_activate_walks_the_states_and_stamps_who_did_what()
    {
        var h = new Harness();
        var (shirt, _)  = h.Product("Printed T-Shirt", SMS.Shared.Common.ProductType.FinishedGood);
        var (_, plainV) = h.Product("Plain T-Shirt");
        var uuid = await h.Repo.CreateAsync(Recipe(shirt, Line(plainV)), Author);

        await h.Repo.SubmitAsync(uuid, Author);
        (await Detail(h, uuid)).Status.Should().Be(BomStatus.Submitted);

        await h.Repo.ApproveAsync(uuid, Reviewer);
        var approved = await Detail(h, uuid);
        approved.Status.Should().Be(BomStatus.Approved);
        approved.SubmittedBy.Should().Be(Author);
        approved.ApprovedBy.Should().Be(Reviewer);

        await h.Repo.ActivateAsync(uuid, Reviewer);
        var active = await Detail(h, uuid);
        active.Status.Should().Be(BomStatus.Active);
        active.ActivatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task The_submitter_cannot_approve_their_own_recipe()
    {
        var h = new Harness();
        var (shirt, _)  = h.Product("Printed T-Shirt", SMS.Shared.Common.ProductType.FinishedGood);
        var (_, plainV) = h.Product("Plain T-Shirt");
        var uuid = await h.Repo.CreateAsync(Recipe(shirt, Line(plainV)), Author);
        await h.Repo.SubmitAsync(uuid, Author);

        var act = () => h.Repo.ApproveAsync(uuid, Author);

        await act.Should().ThrowAsync<BadRequestException>().WithMessage("*cannot approve it*");
    }

    [Fact]
    public async Task Out_of_order_transitions_and_an_empty_recipe_are_refused()
    {
        var h = new Harness();
        var (shirt, _)  = h.Product("Printed T-Shirt", SMS.Shared.Common.ProductType.FinishedGood);
        var (_, plainV) = h.Product("Plain T-Shirt");
        var empty = await h.Repo.CreateAsync(Recipe(shirt), Author);

        var submitEmpty = () => h.Repo.SubmitAsync(empty, Author);
        await submitEmpty.Should().ThrowAsync<BadRequestException>().WithMessage("*has no lines*");

        var uuid = await h.Repo.CreateAsync(Recipe(shirt, Line(plainV)), Author);
        var approveDraft = () => h.Repo.ApproveAsync(uuid, Reviewer);
        await approveDraft.Should().ThrowAsync<BadRequestException>().WithMessage("*only a submitted recipe*");
        var activateDraft = () => h.Repo.ActivateAsync(uuid, Reviewer);
        await activateDraft.Should().ThrowAsync<BadRequestException>().WithMessage("*only an approved recipe*");
        var rejectNoReason = async () => { await h.Repo.SubmitAsync(uuid, Author); await h.Repo.RejectAsync(uuid, Reviewer, " "); };
        await rejectNoReason.Should().ThrowAsync<BadRequestException>().WithMessage("*reason is required*");
    }

    [Fact]
    public async Task Activating_a_newer_version_retires_the_active_one_and_new_orders_would_use_the_new()
    {
        // T-PB06/07: V2 from V1, activated, V1 becomes obsolete.
        var h = new Harness();
        var (shirt, _)  = h.Product("Printed T-Shirt", SMS.Shared.Common.ProductType.FinishedGood);
        var (_, plainV) = h.Product("Plain T-Shirt");
        var (_, inkV)   = h.Product("Printing Ink");
        var v1 = await ActiveRecipeAsync(h, shirt, Line(plainV), Line(inkV, 0.05m));

        var v2 = await h.Repo.NewVersionAsync(v1, Author);
        var draft = await Detail(h, v2);
        draft.Version.Should().Be(2);
        draft.Status.Should().Be(BomStatus.Draft);
        draft.BomNumber.Should().Be("BOM-2026-00002");
        draft.Lines.Should().HaveCount(2, "the lines were copied");
        (await Detail(h, v1)).Status.Should().Be(BomStatus.Active, "the draft does not disturb the live recipe");

        await h.Repo.UpdateAsync(v2, new UpdateBomRequest { Lines = [Line(plainV), Line(inkV, 0.07m)] }, Author);
        await h.Repo.SubmitAsync(v2, Author);
        await h.Repo.ApproveAsync(v2, Reviewer);
        await h.Repo.ActivateAsync(v2, Reviewer);

        (await Detail(h, v1)).Status.Should().Be(BomStatus.Obsolete);
        (await Detail(h, v2)).Status.Should().Be(BomStatus.Active);

        var versions = await h.Repo.GetVersionsAsync(shirt, null);
        versions.Select(v => (v.Version, v.Status)).Should().Equal((2, BomStatus.Active), (1, BomStatus.Obsolete));

        var draftUuid = await h.Repo.CreateAsync(Recipe(shirt, Line(plainV)), Author);
        var fromDraft = () => h.Repo.NewVersionAsync(draftUuid, Author);
        await fromDraft.Should().ThrowAsync<BadRequestException>().WithMessage("*still a draft*");
    }

    [Fact]
    public async Task Comparing_two_versions_lists_what_was_added_removed_and_changed()
    {
        // T-PB09
        var h = new Harness();
        var (shirt, _)  = h.Product("Printed T-Shirt", SMS.Shared.Common.ProductType.FinishedGood);
        var (_, plainV) = h.Product("Plain T-Shirt");
        var (_, inkV)   = h.Product("Printing Ink");
        var (_, boxV)   = h.Product("Packaging Box");
        var v1 = await ActiveRecipeAsync(h, shirt, Line(plainV), Line(inkV, 0.05m), Line(boxV));
        var v2 = await h.Repo.NewVersionAsync(v1, Author);
        await h.Repo.UpdateAsync(v2, new UpdateBomRequest { Lines = [Line(plainV), Line(inkV, 0.07m, scrap: 5)] }, Author);

        var diff = await h.Repo.CompareAsync(v1, v2);

        diff.Added.Should().BeEmpty();
        diff.Removed.Select(l => l.MaterialProductName).Should().Equal("Packaging Box");
        diff.Changed.Should().ContainSingle().Which.Fields.Should().Equal("Quantity", "Scrap %");
        diff.Changed.Single().Before.Quantity.Should().Be(0.05m);
        diff.Changed.Single().After.Quantity.Should().Be(0.07m);
        diff.LeftVersion.Should().Be(1);
        diff.RightVersion.Should().Be(2);

        var (kit, _) = h.Product("Kit", SMS.Shared.Common.ProductType.FinishedGood);
        var other = await h.Repo.CreateAsync(Recipe(kit, Line(plainV)), Author);
        var apples = () => h.Repo.CompareAsync(v1, other);
        await apples.Should().ThrowAsync<BadRequestException>().WithMessage("*same product*");
    }

    [Fact]
    public async Task Obsoleting_an_active_recipe_retires_it_and_appends_the_reason()
    {
        var h = new Harness();
        var (shirt, _)  = h.Product("Printed T-Shirt", SMS.Shared.Common.ProductType.FinishedGood);
        var (_, plainV) = h.Product("Plain T-Shirt");
        var uuid = await ActiveRecipeAsync(h, shirt, Line(plainV));

        await h.Repo.ObsoleteAsync(uuid, Reviewer, "Line discontinued.");

        var bom = await Detail(h, uuid);
        bom.Status.Should().Be(BomStatus.Obsolete);
        bom.ObsoletedBy.Should().Be(Reviewer);
        bom.Notes.Should().Contain("Obsoleted: Line discontinued.");
    }

    // ── Listing ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task The_list_filters_by_product_status_and_search()
    {
        var h = new Harness();
        var (shirt, _)  = h.Product("Printed T-Shirt", SMS.Shared.Common.ProductType.FinishedGood);
        var (kit, _)    = h.Product("Bolt Assembly Kit", SMS.Shared.Common.ProductType.FinishedGood);
        var (_, plainV) = h.Product("Plain T-Shirt");
        await ActiveRecipeAsync(h, shirt, Line(plainV));
        await h.Repo.CreateAsync(Recipe(kit, Line(plainV)), Author);

        (await h.Repo.GetListAsync(new BomListFilter())).TotalRecords.Should().Be(2);
        (await h.Repo.GetListAsync(new BomListFilter { Status = "active" })).Data.Single().ProductName.Should().Be("Printed T-Shirt");
        (await h.Repo.GetListAsync(new BomListFilter { ProductUuid = kit })).Data.Single().Status.Should().Be(BomStatus.Draft);
        (await h.Repo.GetListAsync(new BomListFilter { Search = "bolt" })).Data.Single().ProductName.Should().Be("Bolt Assembly Kit");
        (await h.Repo.GetListAsync(new BomListFilter { Search = "BOM-2026-00001" })).Data.Single().ProductName.Should().Be("Printed T-Shirt");
    }

    // ── Cost roll-up (A30-P2-08, T-PB05) ─────────────────────────────────────

    [Fact]
    public async Task Cost_uses_the_last_purchase_price_then_the_list_price_and_includes_scrap()
    {
        var h = new Harness();
        var (shirt, _)  = h.Product("Printed T-Shirt", SMS.Shared.Common.ProductType.FinishedGood);
        var (_, plainV) = h.Product("Plain T-Shirt", purchasePrice: 3m, lastPurchasePrice: 3.5m);
        var (_, inkV)   = h.Product("Printing Ink", purchasePrice: 100m);
        var (_, freeV)  = h.Product("Thread", purchasePrice: 0m);
        var uuid = await h.Repo.CreateAsync(new CreateBomRequest
        {
            ProductUuid = shirt, BaseQuantity = 10, Lines = [Line(plainV, 10), Line(inkV, 0.5m, scrap: 10), Line(freeV, 1)]
        }, Author);

        var cost = await h.Cost.CalculateAsync(uuid);

        cost.Lines[0].CostSource.Should().Be("LAST_PURCHASE_PRICE");
        cost.Lines[0].LineCost.Should().Be(35m);
        cost.Lines[1].CostSource.Should().Be("PURCHASE_PRICE");
        cost.Lines[1].GrossQuantity.Should().Be(0.55m);
        cost.Lines[1].LineCost.Should().Be(55m);
        cost.Lines[2].CostSource.Should().Be("NONE");
        cost.Warnings.Should().ContainSingle(w => w.Contains("Thread"));
        cost.TotalCost.Should().Be(90m);
        cost.CostPerUnit.Should().Be(9m, "90 for a base quantity of 10");
    }

    [Fact]
    public async Task A_manufactured_input_is_costed_from_its_own_active_recipe()
    {
        // Chained manufacturing: the kit's bolt line costs what a bolt costs to make, not a purchase price.
        var h = new Harness();
        var (bolt, boltV) = h.Product("Steel Bolt M10", SMS.Shared.Common.ProductType.FinishedGood, purchasePrice: 0m);
        var (kit, _)      = h.Product("Bolt Assembly Kit", SMS.Shared.Common.ProductType.FinishedGood);
        var (_, rodV)     = h.Product("Steel Rod", purchasePrice: 2m);
        var (_, boxV)     = h.Product("Plastic Box", purchasePrice: 5m);
        var boltBom = await ActiveRecipeAsync(h, bolt, Line(rodV, 1));
        var kitBom  = await h.Repo.CreateAsync(Recipe(kit, Line(boltV, 10), Line(boxV, 1)), Author);

        var cost = await h.Cost.CalculateAsync(kitBom);

        var boltLine = cost.Lines.Single(l => l.MaterialName.StartsWith("Steel Bolt"));
        boltLine.CostSource.Should().Be("BOM_ROLLUP");
        boltLine.NestedBomUuid.Should().Be(boltBom);
        boltLine.UnitCost.Should().Be(2m);
        boltLine.LineCost.Should().Be(20m);
        cost.TotalCost.Should().Be(25m);
        cost.Warnings.Should().BeEmpty();
    }
}
