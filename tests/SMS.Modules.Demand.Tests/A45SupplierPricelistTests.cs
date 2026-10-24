using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Repositories;
using SMS.Modules.Demand.Services;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using Xunit;

namespace SMS.Modules.Demand.Tests;

/// <summary>
/// A45 â€” the supplier pricelist resolver (TS-01..09, 24, 26; BR-01..03, 15), the pricelist CRUD (TS-21..23, BR-11..16), and the
/// purchase order's save-side check of a line's price source (D-10, TS-10/11/12/25). In-memory; the migration and the real
/// HTTP surface are covered by A45SupplierPricelistMigrationTests and SMS.Integration.Tests.
/// </summary>
public class A45SupplierPricelistTests
{
    private static readonly Guid Pkr = Guid.NewGuid();
    private static readonly Guid Usd = Guid.NewGuid();
    private static readonly DateTime Oct17 = new(2026, 10, 17);

    private sealed class W
    {
        public required Guid Org;
        public required DemandDbContext Db;
        public required InventoryDbContext Inv;
        public required PurchasePriceResolver Resolver;
        public required SupplierPricelistService Service;
        public required PurchaseOrderRepository Repo;
        public Guid Supplier = Guid.NewGuid();
        public Guid Product, Variant, Variant2, Product2, OtherProductVariant;
    }

    private static W NewWorld(Guid? org = null, DemandDbContext? sharedDb = null, bool supplierIsVendor = true)
    {
        var orgId = org ?? Guid.NewGuid();
        var tenant = new StaticTenantContext { OrganizationId = orgId };
        var dbName = Guid.NewGuid().ToString();
        var db = sharedDb ?? new DemandDbContext(new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase(dbName).Options, tenant);
        var inv = new InventoryDbContext(new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options,
            new StaticTenantContext { OrganizationId = orgId });

        var p1 = new Product { Uuid = Guid.NewGuid(), OrganizationId = orgId, Sku = "WDG-001", Name = "Widget-X", IsActive = true, CreatedBy = 1 };
        var v1 = new ProductVariant { Uuid = Guid.NewGuid(), OrganizationId = orgId, Sku = "WDG-001-R", VariantName = "Red", IsDefault = true, IsActive = true, CreatedBy = 1 };
        var v2 = new ProductVariant { Uuid = Guid.NewGuid(), OrganizationId = orgId, Sku = "WDG-001-B", VariantName = "Blue", IsActive = true, CreatedBy = 1 };
        p1.Variants.Add(v1); p1.Variants.Add(v2);
        var p2 = new Product { Uuid = Guid.NewGuid(), OrganizationId = orgId, Sku = "GAD-002", Name = "Gadget", IsActive = true, CreatedBy = 1 };
        var v3 = new ProductVariant { Uuid = Guid.NewGuid(), OrganizationId = orgId, Sku = "GAD-002-1", VariantName = "Std", IsDefault = true, IsActive = true, CreatedBy = 1 };
        p2.Variants.Add(v3);
        inv.Products.AddRange(p1, p2);
        inv.SaveChanges();

        var orgCurrency = new Mock<IOrganizationCurrencyService>();
        orgCurrency.Setup(c => c.GetBaseCurrencyIdAsync(It.IsAny<Guid>(), It.IsAny<TransactionDomain>(), It.IsAny<CancellationToken>())).ReturnsAsync(Pkr);
        var codes = new Mock<ICurrencyCodeLookup>();
        codes.Setup(c => c.GetCodeAsync(Pkr, It.IsAny<CancellationToken>())).ReturnsAsync("PKR");
        codes.Setup(c => c.GetCodeAsync(Usd, It.IsAny<CancellationToken>())).ReturnsAsync("USD");
        codes.Setup(c => c.GetCodeAsync(It.Is<Guid>(g => g != Pkr && g != Usd), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

        var w = new W
        {
            Org = orgId, Db = db, Inv = inv, Product = p1.Uuid, Variant = v1.Uuid, Variant2 = v2.Uuid, Product2 = p2.Uuid, OtherProductVariant = v3.Uuid,
            Resolver = null!, Service = null!, Repo = null!
        };
        var names = new Mock<ISupplierNameLookupService>();
        names.Setup(n => n.GetNamesAsync(It.IsAny<IReadOnlyList<Guid>>()))
             .ReturnsAsync((IReadOnlyList<Guid> ids) => (IReadOnlyDictionary<Guid, string>)ids.ToDictionary(i => i, _ => "Acme Corp"));
        var partners = new Mock<IPartnerRoleLookup>();
        partners.Setup(p => p.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid id, CancellationToken _) => new PartnerRoleInfo(id, "Acme Corp", false, supplierIsVendor, true));

        var resolver = new PurchasePriceResolver(db, inv, orgCurrency.Object, codes.Object, names.Object);
        var seq = 0;
        var numbers = new Mock<IDocumentNumberGenerator>();
        numbers.Setup(n => n.NextAsync(It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(() => $"PO-2026-{++seq:D5}");
        var repo = new PurchaseOrderRepository(db, NullLogger<PurchaseOrderRepository>.Instance, numbers.Object, inv,
            orgCurrency: orgCurrency.Object, currencyCodes: codes.Object, priceResolver: resolver);
        var service = new SupplierPricelistService(db, inv, partners.Object, names.Object, codes.Object, null, orgCurrency.Object);

        return new W { Org = orgId, Db = db, Inv = inv, Product = w.Product, Variant = w.Variant, Variant2 = w.Variant2, Product2 = w.Product2,
            OtherProductVariant = w.OtherProductVariant, Resolver = resolver, Service = service, Repo = repo, Supplier = w.Supplier };
    }

    private static SupplierPricelist Row(W w, decimal price, decimal minQty = 1m, int priority = 0, string from = "2026-01-01", string to = "2026-12-31",
        PricingDateBasis basis = PricingDateBasis.OrderDate, Guid? variant = null, Guid? currency = null, bool active = true, Guid? product = null) =>
        new()
        {
            OrganizationId = w.Org, ProductUuid = product ?? w.Product, VariantUuid = variant, SupplierUuid = w.Supplier, MinQuantity = minQty,
            UnitPrice = price, CurrencyId = currency ?? Pkr, PricingDateBasis = basis, ValidFrom = DateTime.Parse(from), ValidTo = DateTime.Parse(to),
            Priority = priority, IsActive = active, CreatedBy = 1, LeadTimeDays = 14
        };

    private static async Task<SupplierPricelist[]> Seed(W w, params SupplierPricelist[] rows)
    {
        w.Db.SupplierPricelists.AddRange(rows);
        await w.Db.SaveChangesAsync();
        return rows;
    }

    private static PriceResolutionRequest Req(W w, decimal qty = 1m, DateTime? order = null, DateTime? delivery = null, Guid? variant = null, Guid? currency = null) =>
        new() { VariantUuid = variant ?? w.Variant, SupplierUuid = w.Supplier, Quantity = qty, OrderDate = order ?? Oct17, ExpectedDeliveryDate = delivery, CurrencyId = currency };

    // â”€â”€ Resolver â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    [Fact]
    public async Task TS_01_a_valid_row_is_found_with_its_price_currency_and_lead_time()
    {
        var w = NewWorld();
        var rows = await Seed(w, Row(w, 12.5m));
        var r = await w.Resolver.ResolveAsync(Req(w));
        r.Found.Should().BeTrue();
        r.UnitPrice.Should().Be(12.5m);
        r.CurrencyId.Should().Be(Pkr);
        r.CurrencyCode.Should().Be("PKR");
        r.SupplierPricelistUuid.Should().Be(rows[0].UUID);
        r.DateBasisUsed.Should().Be(PricingDateBasis.OrderDate);
        r.EffectiveDateUsed.Should().Be(Oct17);
        r.LeadTimeDays.Should().Be(14);
        r.WarningMessage.Should().BeNull();
    }

    [Fact]
    public async Task TS_02_nothing_configured_is_found_false_with_the_product_supplier_and_date_in_the_warning()
    {
        var w = NewWorld();
        var r = await w.Resolver.ResolveAsync(Req(w));
        r.Found.Should().BeFalse();
        r.UnitPrice.Should().BeNull();
        r.WarningMessage.Should().Be("No configured price found for Widget-X from Acme Corp effective 2026-10-17. Enter price manually or create an RFQ.");
    }

    [Fact]
    public async Task TS_03_a_date_outside_the_window_is_not_matched_and_the_boundaries_are_inclusive()
    {
        var w = NewWorld();
        await Seed(w, Row(w, 10m, from: "2026-10-17", to: "2026-10-18"));
        (await w.Resolver.ResolveAsync(Req(w, order: new DateTime(2026, 10, 16)))).Found.Should().BeFalse();
        (await w.Resolver.ResolveAsync(Req(w, order: new DateTime(2026, 10, 17, 15, 30, 0)))).Found.Should().BeTrue("ValidFrom is inclusive and the time of day is ignored");
        (await w.Resolver.ResolveAsync(Req(w, order: new DateTime(2026, 10, 18)))).Found.Should().BeTrue("ValidTo is inclusive");
        (await w.Resolver.ResolveAsync(Req(w, order: new DateTime(2026, 10, 19)))).Found.Should().BeFalse();
    }

    [Fact]
    public async Task TS_04_a_quantity_below_every_minimum_is_not_matched()
    {
        var w = NewWorld();
        await Seed(w, Row(w, 12.5m, minQty: 100m));
        (await w.Resolver.ResolveAsync(Req(w, qty: 99m))).Found.Should().BeFalse();
        (await w.Resolver.ResolveAsync(Req(w, qty: 100m))).Found.Should().BeTrue();
    }

    [Fact]
    public async Task TS_05_a_delivery_date_row_is_checked_against_the_delivery_date()
    {
        var w = NewWorld();
        await Seed(w, Row(w, 14m, from: "2026-11-01", to: "2026-11-30", basis: PricingDateBasis.DeliveryDate));
        var r = await w.Resolver.ResolveAsync(Req(w, order: Oct17, delivery: new DateTime(2026, 11, 15)));
        r.Found.Should().BeTrue();
        r.DateBasisUsed.Should().Be(PricingDateBasis.DeliveryDate);
        r.EffectiveDateUsed.Should().Be(new DateTime(2026, 11, 15));
        (await w.Resolver.ResolveAsync(Req(w, order: new DateTime(2026, 11, 15), delivery: new DateTime(2026, 12, 20)))).Found
            .Should().BeFalse("the order date is irrelevant to a delivery-date row");
    }

    [Fact]
    public async Task TS_06_a_delivery_date_row_falls_back_to_the_order_date_without_a_delivery_date()
    {
        var w = NewWorld();
        await Seed(w, Row(w, 14m, basis: PricingDateBasis.DeliveryDate));
        var r = await w.Resolver.ResolveAsync(Req(w, delivery: null));
        r.Found.Should().BeTrue();
        r.EffectiveDateUsed.Should().Be(Oct17);
    }

    [Fact]
    public async Task TS_07_the_lowest_priority_number_wins()
    {
        var w = NewWorld();
        await Seed(w, Row(w, 20m, priority: 5), Row(w, 18m, priority: 1), Row(w, 19m, priority: 3));
        (await w.Resolver.ResolveAsync(Req(w))).UnitPrice.Should().Be(18m);
    }

    [Fact]
    public async Task TS_08_09_quantity_breaks_pick_the_highest_qualifying_tier()
    {
        var w = NewWorld();
        await Seed(w, Row(w, 15m, minQty: 1m), Row(w, 12.5m, minQty: 100m), Row(w, 10m, minQty: 500m));
        (await w.Resolver.ResolveAsync(Req(w, qty: 50m))).UnitPrice.Should().Be(15m);
        (await w.Resolver.ResolveAsync(Req(w, qty: 200m))).UnitPrice.Should().Be(12.5m);
        (await w.Resolver.ResolveAsync(Req(w, qty: 150m))).UnitPrice.Should().Be(12.5m);
        (await w.Resolver.ResolveAsync(Req(w, qty: 500m))).UnitPrice.Should().Be(10m);
        (await w.Resolver.ResolveAsync(Req(w, qty: 499m))).UnitPrice.Should().Be(12.5m);
    }

    [Fact]
    public async Task Priority_beats_a_bigger_quantity_tier()
    {
        var w = NewWorld();
        await Seed(w, Row(w, 12.5m, minQty: 100m, priority: 2), Row(w, 15m, minQty: 1m, priority: 0));
        (await w.Resolver.ResolveAsync(Req(w, qty: 200m))).UnitPrice.Should().Be(15m);
    }

    [Fact]
    public async Task TS_24_an_inactive_row_is_skipped()
    {
        var w = NewWorld();
        await Seed(w, Row(w, 9m, active: false));
        (await w.Resolver.ResolveAsync(Req(w))).Found.Should().BeFalse();
        await Seed(w, Row(w, 11m, priority: 9));
        (await w.Resolver.ResolveAsync(Req(w))).UnitPrice.Should().Be(11m, "the next active row is used");
    }

    [Fact]
    public async Task A_variant_row_ranks_before_a_product_level_row_at_equal_priority_and_a_product_row_serves_every_variant()
    {
        var w = NewWorld();
        await Seed(w, Row(w, 10m, minQty: 100m), Row(w, 14m, minQty: 1m, variant: w.Variant));
        (await w.Resolver.ResolveAsync(Req(w, qty: 200m))).UnitPrice.Should().Be(14m, "variant-specific first, D-3");
        (await w.Resolver.ResolveAsync(Req(w, qty: 200m, variant: w.Variant2))).UnitPrice.Should().Be(10m, "the product-level row prices the other variant");
        (await w.Resolver.ResolveAsync(Req(w, qty: 200m, variant: w.OtherProductVariant))).Found.Should().BeFalse("another product");
    }

    [Fact]
    public async Task A_row_in_another_currency_is_not_matched_and_the_warning_says_so()
    {
        var w = NewWorld();
        await Seed(w, Row(w, 12m, currency: Usd));
        var r = await w.Resolver.ResolveAsync(Req(w));
        r.Found.Should().BeFalse("the purchase base is PKR");
        r.WarningMessage.Should().Contain("USD").And.Contain("PKR").And.Contain("Widget-X");
        (await w.Resolver.ResolveAsync(Req(w, currency: Usd))).UnitPrice.Should().Be(12m);
    }

    [Fact]
    public async Task Another_organizations_rows_are_never_resolved()
    {
        var w = NewWorld();
        await Seed(w, Row(w, 12m));
        w.Db.SupplierPricelists.Add(new SupplierPricelist
        {
            OrganizationId = Guid.NewGuid(), ProductUuid = w.Product, VariantUuid = w.Variant, SupplierUuid = w.Supplier, MinQuantity = 1, UnitPrice = 1m,
            CurrencyId = Pkr, ValidFrom = new DateTime(2026, 1, 1), ValidTo = new DateTime(2026, 12, 31), Priority = -1, CreatedBy = 1
        });
        await w.Db.SaveChangesAsync();
        (await w.Resolver.ResolveAsync(Req(w))).UnitPrice.Should().Be(12m);
    }

    [Fact]
    public async Task TS_26_concurrent_resolutions_do_not_interfere()
    {
        var w = NewWorld();
        await Seed(w, Row(w, 12m));
        var results = await Task.WhenAll(Enumerable.Range(0, 1).Select(_ => w.Resolver.ResolveAsync(Req(w))));
        results.Should().OnlyContain(r => r.Found && r.UnitPrice == 12m);
        (await w.Db.SupplierPricelists.IgnoreQueryFilters().CountAsync()).Should().Be(1, "resolving writes nothing");
    }

    // â”€â”€ CRUD â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static CreateSupplierPricelistRequest Create(W w, decimal price = 12.5m, decimal minQty = 1m, string from = "2026-01-01", string to = "2026-12-31") =>
        new()
        {
            ProductUuid = w.Product, SupplierUuid = w.Supplier, UnitPrice = price, MinQuantity = minQty, CurrencyId = Pkr,
            ValidFrom = DateTime.Parse(from), ValidTo = DateTime.Parse(to), VendorProductCode = " ACM-1 "
        };

    private static UpdateSupplierPricelistRequest UpdateFrom(PricelistItem i) =>
        new()
        {
            ProductUuid = i.ProductUuid, VariantUuid = i.VariantUuid, SupplierUuid = i.SupplierUuid, UnitPrice = i.UnitPrice, MinQuantity = i.MinQuantity,
            CurrencyId = i.CurrencyId, PricingDateBasis = i.PricingDateBasis, ValidFrom = DateTime.Parse(i.ValidFrom), ValidTo = DateTime.Parse(i.ValidTo),
            Priority = i.Priority, RowVersion = i.RowVersion
        };

    [Fact]
    public async Task Create_returns_the_item_with_joined_names_and_labels()
    {
        var w = NewWorld();
        var saved = (await w.Service.CreateAsync(Create(w), 7)).Result;
        var i = saved.Item;
        i.ProductName.Should().Be("Widget-X");
        i.ProductSku.Should().Be("WDG-001");
        i.SupplierName.Should().Be("Acme Corp");
        i.CurrencyCode.Should().Be("PKR");
        i.PricingDateBasisLabel.Should().Be("Order Date");
        i.ValidFrom.Should().Be("2026-01-01");
        i.ValidTo.Should().Be("2026-12-31");
        i.VendorProductCode.Should().Be("ACM-1");
        i.IsActive.Should().BeTrue();
        saved.OverlapWarning.Should().BeNull();
        (await w.Db.SupplierPricelists.IgnoreQueryFilters().SingleAsync()).CreatedBy.Should().Be(7);
        (await w.Service.GetByIdAsync(i.Uuid))!.Uuid.Should().Be(i.Uuid);
    }

    [Theory]
    [InlineData(0, 1, "2026-01-01", "2026-12-31", "Unit price")]
    [InlineData(-5, 1, "2026-01-01", "2026-12-31", "Unit price")]
    [InlineData(10, -1, "2026-01-01", "2026-12-31", "Minimum quantity")]
    [InlineData(10, 1, "2026-12-31", "2026-01-01", "Valid to")]
    public async Task TS_22_23_invalid_values_are_refused_with_a_400(double price, double minQty, string from, string to, string mention)
    {
        var w = NewWorld();
        var act = () => w.Service.CreateAsync(Create(w, (decimal)price, (decimal)minQty, from, to), 1);
        (await act.Should().ThrowAsync<BadRequestException>()).WithMessage($"*{mention}*");
        (await w.Db.SupplierPricelists.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Zero_minimum_quantity_means_any_quantity_and_is_allowed()
    {
        var w = NewWorld();
        (await w.Service.CreateAsync(Create(w, minQty: 0m), 1)).Result.Item.MinQuantity.Should().Be(0m);
    }

    [Fact]
    public async Task Unknown_product_variant_vendor_and_currency_are_refused()
    {
        var w = NewWorld();
        var bad = Create(w); bad.ProductUuid = Guid.NewGuid();
        (await FluentActions.Awaiting(() => w.Service.CreateAsync(bad, 1)).Should().ThrowAsync<BadRequestException>()).WithMessage("*product*");
        bad = Create(w); bad.VariantUuid = w.OtherProductVariant;
        (await FluentActions.Awaiting(() => w.Service.CreateAsync(bad, 1)).Should().ThrowAsync<BadRequestException>()).WithMessage("*variant*");
        bad = Create(w); bad.CurrencyId = Guid.NewGuid();
        (await FluentActions.Awaiting(() => w.Service.CreateAsync(bad, 1)).Should().ThrowAsync<BadRequestException>()).WithMessage("*currency*");
        bad = Create(w); bad.PricingDateBasis = 2;
        (await FluentActions.Awaiting(() => w.Service.CreateAsync(bad, 1)).Should().ThrowAsync<BadRequestException>()).WithMessage("*basis*");

        var notVendor = NewWorld(supplierIsVendor: false);
        (await FluentActions.Awaiting(() => notVendor.Service.CreateAsync(Create(notVendor), 1)).Should().ThrowAsync<BadRequestException>()).WithMessage("*not a vendor*");
    }

    [Fact]
    public async Task An_exact_duplicate_of_an_active_row_is_a_conflict_but_an_inactive_one_does_not_count()
    {
        var w = NewWorld();
        var first = (await w.Service.CreateAsync(Create(w), 1)).Result.Item;
        (await FluentActions.Awaiting(() => w.Service.CreateAsync(Create(w), 1)).Should().ThrowAsync<ConflictException>()).WithMessage("*already exists*");

        await w.Service.DeactivateAsync(first.Uuid, 1);
        // The in-memory provider does not generate rowversions; stamp the row the way SQL Server would.
        (await w.Db.SupplierPricelists.IgnoreQueryFilters().SingleAsync()).RowVersion = [1, 2, 3, 4, 5, 6, 7, 8];
        await w.Db.SaveChangesAsync();
        (await w.Service.CreateAsync(Create(w), 1)).Result.Item.Should().NotBeNull("the inactive duplicate is out of the way");

        // ...and the old one cannot come back while the new one is active.
        var again = UpdateFrom((await w.Service.GetByIdAsync(first.Uuid))!);
        again.IsActive = true;
        await FluentActions.Awaiting(() => w.Service.UpdateAsync(first.Uuid, again, 1)).Should().ThrowAsync<ConflictException>();
    }

    [Fact]
    public async Task TS_21_an_overlapping_row_is_saved_with_a_warning_naming_the_other_rows_priority_and_dates()
    {
        var w = NewWorld();
        var a = Create(w, 12m, from: "2026-01-01", to: "2026-06-30");
        a.Priority = 3;
        await w.Service.CreateAsync(a, 1);
        var saved = (await w.Service.CreateAsync(Create(w, 11m, from: "2026-06-01", to: "2026-12-31"), 1)).Result;
        saved.Item.Should().NotBeNull("an overlap is a warning, not a block");
        saved.OverlapWarning.Should().Contain("Priority 3").And.Contain("2026-01-01").And.Contain("2026-06-30").And.Contain("lowest priority number");

        var other = Create(w, 11m, minQty: 100m, from: "2026-06-01", to: "2026-12-31");
        (await w.Service.CreateAsync(other, 1)).Result.OverlapWarning.Should().BeNull("a different minimum quantity is a different tier");
    }

    [Fact]
    public async Task Update_with_a_stale_row_version_is_a_409_and_a_current_one_saves_and_audits_the_changes()
    {
        var w = NewWorld();
        var item = (await w.Service.CreateAsync(Create(w), 1)).Result.Item;

        // The in-memory provider does not generate rowversions; give the row one the way SQL Server would.
        var row = await w.Db.SupplierPricelists.IgnoreQueryFilters().SingleAsync();
        row.RowVersion = [1, 2, 3, 4, 5, 6, 7, 8];
        await w.Db.SaveChangesAsync();
        w.Db.ChangeTracker.Clear();
        item = (await w.Service.GetByIdAsync(item.Uuid))!;

        var stale = UpdateFrom(item);
        stale.RowVersion = Convert.ToBase64String(new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 });
        await FluentActions.Awaiting(() => w.Service.UpdateAsync(item.Uuid, stale, 1)).Should().ThrowAsync<ConflictException>();

        var fresh = UpdateFrom(item);
        fresh.UnitPrice = 13m;
        var outcome = await w.Service.UpdateAsync(item.Uuid, fresh, 4);
        outcome.Result.Item.UnitPrice.Should().Be(13m);
        outcome.Changes.Should().ContainSingle(c => c.Field == "Unit price" && c.OldValue == "12.5" && c.NewValue == "13");
        (await w.Db.SupplierPricelists.IgnoreQueryFilters().SingleAsync()).ModifiedBy.Should().Be(4);

        var missing = UpdateFrom(item); missing.RowVersion = null;
        await FluentActions.Awaiting(() => w.Service.UpdateAsync(item.Uuid, missing, 1)).Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task Delete_is_soft_hides_the_row_from_the_default_list_and_a_second_delete_finds_nothing()
    {
        var w = NewWorld();
        var item = (await w.Service.CreateAsync(Create(w), 1)).Result.Item;
        (await w.Service.DeactivateAsync(item.Uuid, 1))!.IsActive.Should().BeFalse();
        (await w.Service.DeactivateAsync(item.Uuid, 1)).Should().BeNull();

        (await w.Service.GetListAsync(new SupplierPricelistFilter())).Data.Should().BeEmpty();
        (await w.Service.GetListAsync(new SupplierPricelistFilter { ActiveOnly = false })).Data.Should().ContainSingle();
        (await w.Service.GetByIdAsync(item.Uuid)).Should().NotBeNull("the record stays for the PO lines that reference it");
        (await w.Resolver.ResolveAsync(Req(w))).Found.Should().BeFalse();
    }

    [Fact]
    public async Task List_filters_by_product_variant_supplier_and_search_and_pages()
    {
        var w = NewWorld();
        await w.Service.CreateAsync(Create(w, 10m, minQty: 1m), 1);
        await w.Service.CreateAsync(Create(w, 9m, minQty: 10m), 1);
        var gadget = Create(w); gadget.ProductUuid = w.Product2; gadget.VariantUuid = w.OtherProductVariant;
        await w.Service.CreateAsync(gadget, 1);

        (await w.Service.GetListAsync(new SupplierPricelistFilter { ProductUuid = w.Product })).TotalRecords.Should().Be(2);
        (await w.Service.GetListAsync(new SupplierPricelistFilter { VariantUuid = w.OtherProductVariant })).TotalRecords.Should().Be(1);
        (await w.Service.GetListAsync(new SupplierPricelistFilter { SupplierUuid = Guid.NewGuid() })).TotalRecords.Should().Be(0);
        (await w.Service.GetListAsync(new SupplierPricelistFilter { Search = "gadget" })).TotalRecords.Should().Be(1);
        (await w.Service.GetListAsync(new SupplierPricelistFilter { Search = "acme" })).TotalRecords.Should().Be(3, "matches the vendor name");
        (await w.Service.GetListAsync(new SupplierPricelistFilter { Search = "ACM-1" })).TotalRecords.Should().Be(3, "matches the vendor product code");
        var page = await w.Service.GetListAsync(new SupplierPricelistFilter { PageSize = 2, Page = 2 });
        page.Data.Should().HaveCount(1);
        page.TotalPages.Should().Be(2);
    }

    [Fact]
    public async Task Another_organizations_row_reads_as_absent()
    {
        var shared = new DbContextOptionsBuilder<DemandDbContext>().UseInMemoryDatabase("iso-" + Guid.NewGuid()).Options;
        var orgA = Guid.NewGuid();
        var dbA = new DemandDbContext(shared, new StaticTenantContext { OrganizationId = orgA });
        var dbB = new DemandDbContext(shared, new StaticTenantContext { OrganizationId = Guid.NewGuid() });
        dbA.SupplierPricelists.Add(new SupplierPricelist
        {
            OrganizationId = orgA, ProductUuid = Guid.NewGuid(), SupplierUuid = Guid.NewGuid(), UnitPrice = 1, CurrencyId = Pkr,
            ValidFrom = new DateTime(2026, 1, 1), ValidTo = new DateTime(2026, 2, 1), CreatedBy = 1
        });
        await dbA.SaveChangesAsync();
        var svcB = new SupplierPricelistService(dbB);
        (await svcB.GetListAsync(new SupplierPricelistFilter { ActiveOnly = false })).TotalRecords.Should().Be(0);
        (await new SupplierPricelistService(dbA).GetListAsync(new SupplierPricelistFilter())).TotalRecords.Should().Be(1);
        var uuid = (await dbA.SupplierPricelists.IgnoreQueryFilters().SingleAsync()).UUID;
        (await svcB.GetByIdAsync(uuid)).Should().BeNull();
        (await svcB.DeactivateAsync(uuid, 1)).Should().BeNull();
    }

    // â”€â”€ PO save-side price source (D-10) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€

    private static CreatePoRequest Po(W w, decimal price, int? source, Guid? pricelist = null, decimal? resolved = null, string? reason = null,
        decimal qty = 10m, Guid? sourcePrLine = null) =>
        new()
        {
            SupplierId = w.Supplier, SupplierName = "Acme Corp",
            Lines =
            [
                new CreatePoLineRequest
                {
                    VariantUuid = w.Variant, ItemDescription = "Widget-X", Quantity = qty, UnitPrice = price, PriceSource = source,
                    SupplierPricelistUuid = pricelist, SystemResolvedPrice = resolved, PriceOverrideReason = reason, SourcePrLineUuid = sourcePrLine
                }
            ]
        };

    private static async Task<PurchaseOrderLine> StoredLine(W w, Guid po) =>
        (await w.Db.PurchaseOrders.AsNoTracking().Include(p => p.Lines).SingleAsync(p => p.UUID == po)).Lines.Single();

    [Fact]
    public async Task TS_01_10_a_pricelist_line_stays_Pricelist_and_an_edited_price_becomes_Override_keeping_the_system_price()
    {
        var w = NewWorld();
        var row = (await Seed(w, Row(w, 12.5m)))[0];

        var asResolved = await StoredLine(w, await w.Repo.CreateAsync(Po(w, 12.5m, 1, row.UUID, 12.5m), 1));
        asResolved.PriceSource.Should().Be(PriceSource.Pricelist);
        asResolved.SystemResolvedPrice.Should().Be(12.5m);
        asResolved.SupplierPricelistUuid.Should().Be(row.UUID);

        var overridden = await StoredLine(w, await w.Repo.CreateAsync(Po(w, 11m, 2, row.UUID, 12.5m, "Spot discount"), 1));
        overridden.PriceSource.Should().Be(PriceSource.Override);
        overridden.SystemResolvedPrice.Should().Be(12.5m, "BR-06");
        overridden.UnitPrice.Should().Be(11m);
        overridden.PriceOverrideReason.Should().Be("Spot discount");

        // A buyer who claims Pricelist but sends another price is caught: it is stored as Override.
        var cheated = await StoredLine(w, await w.Repo.CreateAsync(Po(w, 9m, 1, row.UUID, 9m), 1));
        cheated.PriceSource.Should().Be(PriceSource.Override);
        cheated.SystemResolvedPrice.Should().Be(12.5m, "the server's own figure, not the client's");

        // A reason is only kept on an Override.
        var noReason = await StoredLine(w, await w.Repo.CreateAsync(Po(w, 12.5m, 1, row.UUID, 12.5m, "ignored"), 1));
        noReason.PriceOverrideReason.Should().BeNull();
    }

    [Fact]
    public async Task A_claim_without_a_pricelist_row_behind_it_is_Manual_and_so_is_a_legacy_caller()
    {
        var w = NewWorld();
        (await StoredLine(w, await w.Repo.CreateAsync(Po(w, 5m, 1, Guid.NewGuid(), 5m), 1))).PriceSource.Should().Be(PriceSource.Manual);
        (await StoredLine(w, await w.Repo.CreateAsync(Po(w, 5m, 2, null, 7m, "x"), 1))).PriceSource.Should().Be(PriceSource.Manual);
        var legacy = await StoredLine(w, await w.Repo.CreateAsync(Po(w, 5m, null), 1));
        legacy.PriceSource.Should().Be(PriceSource.Manual);
        legacy.SystemResolvedPrice.Should().BeNull();
        legacy.SupplierPricelistUuid.Should().BeNull();

        // Even with a matching row, a line that says Manual stays Manual (TS-12).
        await Seed(w, Row(w, 12.5m));
        (await StoredLine(w, await w.Repo.CreateAsync(Po(w, 12.5m, 0), 1))).PriceSource.Should().Be(PriceSource.Manual);
    }

    [Fact]
    public async Task The_server_finds_the_row_itself_when_the_client_names_none()
    {
        var w = NewWorld();
        var row = (await Seed(w, Row(w, 12.5m, minQty: 5m)))[0];
        var line = await StoredLine(w, await w.Repo.CreateAsync(Po(w, 12.5m, 1), 1));
        line.PriceSource.Should().Be(PriceSource.Pricelist);
        line.SupplierPricelistUuid.Should().Be(row.UUID);
    }

    [Fact]
    public async Task RFQ_is_kept_only_for_a_line_from_a_requisition_line_whose_RFQ_was_awarded()
    {
        var w = NewWorld();
        var prLine = new PrLine { UUID = Guid.NewGuid(), ItemDescription = "Widget", Quantity = 1, QuotationStatus = "AWARDED", OrganizationId = w.Org };
        w.Db.PrLines.Add(prLine);
        await w.Db.SaveChangesAsync();

        (await StoredLine(w, await w.Repo.CreateAsync(Po(w, 8m, 3, sourcePrLine: prLine.UUID), 1))).PriceSource.Should().Be(PriceSource.RFQ);
        (await StoredLine(w, await w.Repo.CreateAsync(Po(w, 8m, 3), 1))).PriceSource.Should().Be(PriceSource.Manual, "no requisition line behind it");
        (await StoredLine(w, await w.Repo.CreateAsync(Po(w, 8m, 3, sourcePrLine: Guid.NewGuid()), 1))).PriceSource.Should().Be(PriceSource.Manual);
    }

    [Fact]
    public async Task An_unknown_price_source_or_a_too_long_reason_is_a_400()
    {
        var w = NewWorld();
        var row = (await Seed(w, Row(w, 12.5m)))[0];
        await FluentActions.Awaiting(() => w.Repo.CreateAsync(Po(w, 5m, 7), 1)).Should().ThrowAsync<BadRequestException>();
        await FluentActions.Awaiting(() => w.Repo.CreateAsync(Po(w, 5m, -1), 1)).Should().ThrowAsync<BadRequestException>();
        await FluentActions.Awaiting(() => w.Repo.CreateAsync(Po(w, 11m, 2, row.UUID, 12.5m, new string('x', 501)), 1)).Should().ThrowAsync<BadRequestException>();
    }

    [Fact]
    public async Task TS_25_a_line_still_reads_its_price_source_after_the_pricelist_row_is_soft_deleted_and_the_edit_keeps_it()
    {
        var w = NewWorld();
        var row = (await Seed(w, Row(w, 12.5m, basis: PricingDateBasis.DeliveryDate)))[0];
        var uuid = await w.Repo.CreateAsync(Po(w, 11m, 2, row.UUID, 12.5m, "why"), 1);
        await w.Service.DeactivateAsync(row.UUID, 1);

        var detail = (await w.Repo.GetByIdAsync(uuid))!.Lines.Single();
        detail.PriceSource.Should().Be((int)PriceSource.Override);
        detail.SystemResolvedPrice.Should().Be(12.5m);
        detail.SupplierPricelistUuid.Should().Be(row.UUID);
        detail.PriceOverrideReason.Should().Be("why");
        detail.PricingDateBasis.Should().Be("DeliveryDate");

        // Editing the PO resends the line; the inactive row cannot be re-resolved but the claimed row still backs the price.
        await w.Repo.UpdateAsync(uuid, new PatchPoRequest
        {
            Lines = [new CreatePoLineRequest { VariantUuid = w.Variant, ItemDescription = "Widget-X", Quantity = 10m, UnitPrice = 11m, PriceSource = 2,
                SupplierPricelistUuid = row.UUID, SystemResolvedPrice = 12.5m, PriceOverrideReason = "why" }]
        }, 1);
        var after = (await w.Repo.GetByIdAsync(uuid))!.Lines.Single();
        after.PriceSource.Should().Be((int)PriceSource.Override);
        after.SystemResolvedPrice.Should().Be(12.5m);
    }

    [Fact]
    public async Task A_line_without_a_pricelist_reads_Manual_and_a_null_date_basis()
    {
        var w = NewWorld();
        var line = (await w.Repo.GetByIdAsync(await w.Repo.CreateAsync(Po(w, 5m, null), 1)))!.Lines.Single();
        line.PriceSource.Should().Be(0);
        line.PricingDateBasis.Should().BeNull();
        line.SystemResolvedPrice.Should().BeNull();
    }

    [Fact]
    public async Task A_reissued_PO_keeps_the_price_source_of_its_lines()
    {
        var w = NewWorld();
        var row = (await Seed(w, Row(w, 12.5m)))[0];
        var uuid = await w.Repo.CreateAsync(Po(w, 11m, 2, row.UUID, 12.5m, "why"), 1);
        var clone = await new PoCloneHandler(w.Db).CloneDocumentAsync(uuid);
        var line = await StoredLine(w, clone);
        line.PriceSource.Should().Be(PriceSource.Override);
        line.SystemResolvedPrice.Should().Be(12.5m);
        line.SupplierPricelistUuid.Should().Be(row.UUID);
    }
}

