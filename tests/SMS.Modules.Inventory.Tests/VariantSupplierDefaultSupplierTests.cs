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

/// <summary>A29-P5-02 §2.1/§3.3 — VariantSupplierService.GetDefaultSupplierIdAsync, the one input
/// DEFAULT_SUPPLIER supplier-selection mode reads from ProductVariant.DefaultSupplierId.</summary>
public class VariantSupplierDefaultSupplierTests
{
    private static async Task<(VariantSupplierService Service, Guid VariantUuid)> NewHarness(Guid? defaultSupplierId)
    {
        var tenant = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var db = new InventoryDbContext(
            new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options, tenant);

        var variantUuid = Guid.NewGuid();
        var product = new Product { Uuid = Guid.NewGuid(), Sku = "P", Name = "P", Status = "ACTIVE", IsActive = true, CreatedBy = 1 };
        product.Variants.Add(new ProductVariant
        {
            Uuid = variantUuid, Sku = "P-1", VariantName = "Default", IsDefault = true, IsActive = true,
            DefaultSupplierId = defaultSupplierId, CreatedDate = DateTime.UtcNow
        });
        db.Products.Add(product);
        await db.SaveChangesAsync();

        var service = new VariantSupplierService(
            db, tenant, Mock.Of<IOrganizationCurrencyService>(), Mock.Of<IPurchaseOrderPriceLookupService>(),
            Mock.Of<ISupplierNameLookupService>(), Mock.Of<ISupplierScoreLookupService>(), Mock.Of<IUserQueryService>(),
            new ConfigurationBuilder().Build());

        return (service, variantUuid);
    }

    [Fact]
    public async Task Returns_the_variants_configured_default_supplier()
    {
        var supplier = Guid.NewGuid();
        var (service, variantUuid) = await NewHarness(supplier);

        (await service.GetDefaultSupplierIdAsync(variantUuid)).Should().Be(supplier);
    }

    [Fact]
    public async Task Returns_null_when_the_variant_has_no_default_supplier_set()
    {
        var (service, variantUuid) = await NewHarness(defaultSupplierId: null);

        (await service.GetDefaultSupplierIdAsync(variantUuid)).Should().BeNull();
    }

    [Fact]
    public async Task Returns_null_for_a_variant_that_does_not_exist()
    {
        var (service, _) = await NewHarness(Guid.NewGuid());

        (await service.GetDefaultSupplierIdAsync(Guid.NewGuid())).Should().BeNull();
    }
}
