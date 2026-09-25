using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Inventory.Tests;

/// <summary>
/// VariantSupplierService.SetPreferredAsync — RC-002's "one preferred rate per variant," and the
/// A29-P5-02 auto-PO input it feeds. Sale order confirmation's DEFAULT_SUPPLIER mode reads
/// ProductVariant.DefaultSupplierId (VariantSupplierDefaultSupplierTests), and this is the only
/// place in the codebase that ever writes it — kept in step with whichever rate is preferred, so
/// the "Set preferred" action a rate card already has is what makes DEFAULT_SUPPLIER mode usable.
/// </summary>
public class VariantSupplierSetPreferredTests
{
    private const int User = 7;

    private sealed record Harness(InventoryDbContext Db, VariantSupplierService Service, int VariantId, Guid VariantUuid);

    private static async Task<Harness> NewHarness()
    {
        var tenant = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var db = new InventoryDbContext(
            new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);

        var variantUuid = Guid.NewGuid();
        var product = new Product { Uuid = Guid.NewGuid(), Sku = "P", Name = "P", Status = "ACTIVE", IsActive = true, CreatedBy = 1 };
        product.Variants.Add(new ProductVariant
        {
            Uuid = variantUuid, Sku = "P-1", VariantName = "Default", IsDefault = true, IsActive = true,
            CreatedDate = DateTime.UtcNow
        });
        db.Products.Add(product);
        await db.SaveChangesAsync();

        var service = new VariantSupplierService(
            db, tenant, Mock.Of<IOrganizationCurrencyService>(), Mock.Of<IPurchaseOrderPriceLookupService>(),
            Mock.Of<ISupplierNameLookupService>(), Mock.Of<ISupplierScoreLookupService>(), Mock.Of<IUserQueryService>(),
            new ConfigurationBuilder().Build());

        return new Harness(db, service, product.Variants.Single().Id, variantUuid);
    }

    private static async Task<VariantSupplier> AddRate(Harness h, Guid supplierId, decimal cost = 10m)
    {
        var rate = new VariantSupplier
        {
            Uuid = Guid.NewGuid(), VariantId = h.VariantId, SupplierId = supplierId, VendorUnitCost = cost,
            CurrencyId = Guid.NewGuid(), IsActive = true
        };
        h.Db.VariantSuppliers.Add(rate);
        await h.Db.SaveChangesAsync();
        return rate;
    }

    private Task<Guid?> DefaultSupplierOf(Harness h) =>
        h.Db.ProductVariants.AsNoTracking().Where(v => v.Id == h.VariantId).Select(v => v.DefaultSupplierId).SingleAsync();

    [Fact]
    public async Task Marking_a_rate_preferred_makes_it_the_variants_default_supplier()
    {
        var h = await NewHarness();
        var supplier = Guid.NewGuid();
        var rate = await AddRate(h, supplier);

        (await h.Service.SetPreferredAsync(rate.Uuid, User)).Should().BeTrue();

        (await DefaultSupplierOf(h)).Should().Be(supplier);
    }

    [Fact]
    public async Task Moving_preferred_to_another_supplier_moves_the_default_supplier_with_it()
    {
        var h = await NewHarness();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var firstRate = await AddRate(h, first);
        var secondRate = await AddRate(h, second);
        await h.Service.SetPreferredAsync(firstRate.Uuid, User);

        await h.Service.SetPreferredAsync(secondRate.Uuid, User);

        (await DefaultSupplierOf(h)).Should().Be(second);
        (await h.Db.VariantSuppliers.AsNoTracking().SingleAsync(x => x.Uuid == firstRate.Uuid)).IsPreferred.Should().BeFalse();
    }

    [Fact]
    public async Task A_variant_with_no_preferred_rate_has_no_default_supplier()
    {
        var h = await NewHarness();
        await AddRate(h, Guid.NewGuid());

        (await DefaultSupplierOf(h)).Should().BeNull();
    }

    [Fact]
    public async Task An_unknown_rate_id_is_refused_and_changes_nothing()
    {
        var h = await NewHarness();

        (await h.Service.SetPreferredAsync(Guid.NewGuid(), User)).Should().BeFalse();
        (await DefaultSupplierOf(h)).Should().BeNull();
    }

    [Fact]
    public async Task Preferring_a_rate_on_a_different_variant_does_not_touch_this_ones_default_supplier()
    {
        var h = await NewHarness();
        var otherVariantId = h.VariantId + 1000; // no such variant row — the update must simply not find one
        var otherRate = new VariantSupplier
        {
            Uuid = Guid.NewGuid(), VariantId = otherVariantId, SupplierId = Guid.NewGuid(), VendorUnitCost = 5m,
            CurrencyId = Guid.NewGuid(), IsActive = true
        };
        h.Db.VariantSuppliers.Add(otherRate);
        await h.Db.SaveChangesAsync();

        (await h.Service.SetPreferredAsync(otherRate.Uuid, User)).Should().BeTrue();

        (await DefaultSupplierOf(h)).Should().BeNull();
    }
}
