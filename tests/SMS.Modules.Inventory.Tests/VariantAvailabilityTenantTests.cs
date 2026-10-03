using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Inventory.Tests;

/// <summary>
/// A32 PF-05 (QA finding) — the channel-availability lookup that guards sale order and sale quotation lines answers only
/// for the caller's own organization's variants. The EF tenant filter is off for a super admin, so without an explicit
/// filter a super admin could put another organization's item on their own order or quotation.
/// </summary>
public class VariantAvailabilityTenantTests
{
    private static InventoryDbContext Db(string name, Guid org, bool superAdmin = false) => new(
        new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(name).Options,
        new StaticTenantContext { OrganizationId = org, IsSuperAdmin = superAdmin });

    private static async Task<Guid> SeedVariantAsync(InventoryDbContext db)
    {
        var category = new ProductCategory { Name = "Cable", Code = $"C{Guid.NewGuid():N}"[..8], IsActive = true };
        db.ProductCategories.Add(category);
        await db.SaveChangesAsync();
        var product = new Product
        {
            Uuid = Guid.NewGuid(), Name = "4mm cable", Sku = $"SKU{Guid.NewGuid():N}"[..12], CategoryId = category.Id,
            IsActive = true, Status = "ACTIVE"
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        var variant = new ProductVariant
        {
            Uuid = Guid.NewGuid(), ProductId = product.Id, Sku = $"V{Guid.NewGuid():N}"[..12], VariantName = "Default",
            IsDefault = true, IsActive = true, IsAvailableForRetail = true
        };
        db.ProductVariants.Add(variant);
        await db.SaveChangesAsync();
        return variant.Uuid;
    }

    [Fact]
    public async Task The_owning_organization_sees_its_own_variants_availability()
    {
        var name = Guid.NewGuid().ToString();
        var org = Guid.NewGuid();
        var variant = await SeedVariantAsync(Db(name, org));

        var availability = await new VariantAvailabilityService(Db(name, org)).GetAvailabilityAsync(variant);

        availability.Should().NotBeNull();
        availability!.IsAvailableForRetail.Should().BeTrue();
    }

    [Fact]
    public async Task A_super_admin_of_another_organization_gets_nothing_for_it()
    {
        var name = Guid.NewGuid().ToString();
        var variant = await SeedVariantAsync(Db(name, Guid.NewGuid()));

        var availability = await new VariantAvailabilityService(Db(name, Guid.NewGuid(), superAdmin: true)).GetAvailabilityAsync(variant);

        availability.Should().BeNull("another organization's item must read as absent, so the line is refused");
    }

    [Fact]
    public async Task A_background_job_scoped_to_the_owning_organization_still_sees_it()
    {
        // HangfireTenantScope makes TenantContext report that organization (not a bypass); StaticTenantContext stands in.
        var name = Guid.NewGuid().ToString();
        var org = Guid.NewGuid();
        var variant = await SeedVariantAsync(Db(name, org));

        var availability = await new VariantAvailabilityService(Db(name, org, superAdmin: false)).GetAvailabilityAsync(variant);

        availability.Should().NotBeNull();
    }
}
