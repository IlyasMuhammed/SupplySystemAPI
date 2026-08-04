using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Domain;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Inventory.Tests;

// MT-003 acceptance criteria: query filter active, auto-stamp on create, Super Admin bypass,
// no cross-org collision on a code that's only unique per-tenant.

public class TenantScopingTests
{
    private static InventoryDbContext NewDb(string dbName, ITenantContext tenantContext) =>
        new(new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(dbName).Options, tenantContext);

    [Fact]
    public async Task Query_WithoutExplicitFilter_ReturnsOnlyCurrentOrgsProducts()
    {
        var dbName = Guid.NewGuid().ToString();
        var orgA = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var orgB = new StaticTenantContext { OrganizationId = Guid.NewGuid() };

        using (var dbA = NewDb(dbName, orgA))
        {
            dbA.Products.Add(new Product { Uuid = Guid.NewGuid(), Sku = "SKU-A", Name = "Org A Product", Status = "ACTIVE", IsActive = true, CreatedBy = 1 });
            await dbA.SaveChangesAsync();
        }
        using (var dbB = NewDb(dbName, orgB))
        {
            dbB.Products.Add(new Product { Uuid = Guid.NewGuid(), Sku = "SKU-B", Name = "Org B Product", Status = "ACTIVE", IsActive = true, CreatedBy = 1 });
            await dbB.SaveChangesAsync();
        }

        using var queryAsOrgA = NewDb(dbName, orgA);
        var visible = await queryAsOrgA.Products.ToListAsync();

        visible.Should().ContainSingle();
        visible.Single().Sku.Should().Be("SKU-A");
    }

    [Fact]
    public async Task Creating_Product_AutoStampsCurrentTenantsOrganizationId_WithoutExplicitCode()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantContext = new StaticTenantContext { OrganizationId = Guid.NewGuid() };

        using (var db = NewDb(dbName, tenantContext))
        {
            // Deliberately does not set OrganizationId — relies entirely on the DbContext's
            // SaveChanges override, mirroring how ordinary service/repository code creates entities.
            db.Products.Add(new Product { Uuid = Guid.NewGuid(), Sku = "SKU-AUTO", Name = "Auto-stamped", Status = "ACTIVE", IsActive = true, CreatedBy = 1 });
            await db.SaveChangesAsync();
        }

        var superAdmin = new StaticTenantContext { IsSuperAdmin = true };
        using var verify = NewDb(dbName, superAdmin);
        var product = await verify.Products.SingleAsync(p => p.Sku == "SKU-AUTO");

        product.OrganizationId.Should().Be(tenantContext.OrganizationId);
    }

    [Fact]
    public async Task SuperAdminContext_BypassesFilter_SeesAllOrgsData()
    {
        var dbName = Guid.NewGuid().ToString();
        var orgA = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var orgB = new StaticTenantContext { OrganizationId = Guid.NewGuid() };

        using (var dbA = NewDb(dbName, orgA))
        {
            dbA.Products.Add(new Product { Uuid = Guid.NewGuid(), Sku = "SKU-A2", Name = "Org A Product", Status = "ACTIVE", IsActive = true, CreatedBy = 1 });
            await dbA.SaveChangesAsync();
        }
        using (var dbB = NewDb(dbName, orgB))
        {
            dbB.Products.Add(new Product { Uuid = Guid.NewGuid(), Sku = "SKU-B2", Name = "Org B Product", Status = "ACTIVE", IsActive = true, CreatedBy = 1 });
            await dbB.SaveChangesAsync();
        }

        var superAdmin = new StaticTenantContext { IsSuperAdmin = true };
        using var queryAsSuperAdmin = NewDb(dbName, superAdmin);
        var all = await queryAsSuperAdmin.Products.ToListAsync();

        all.Select(p => p.Sku).Should().BeEquivalentTo(["SKU-A2", "SKU-B2"]);
    }

    [Fact]
    public async Task TwoOrganizations_WithSameProductCode_EachSeesOnlyTheirOwn_NoCollision()
    {
        var dbName = Guid.NewGuid().ToString();
        var orgA = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var orgB = new StaticTenantContext { OrganizationId = Guid.NewGuid() };

        // Same SKU, two different orgs — must not throw a unique-index violation, since the
        // index is composite (OrganizationId, Sku), not global on Sku alone.
        using (var dbA = NewDb(dbName, orgA))
        {
            dbA.Products.Add(new Product { Uuid = Guid.NewGuid(), Sku = "SHARED-CODE", Name = "Org A's Widget", Status = "ACTIVE", IsActive = true, CreatedBy = 1 });
            await dbA.SaveChangesAsync();
        }

        var act = async () =>
        {
            using var dbB = NewDb(dbName, orgB);
            dbB.Products.Add(new Product { Uuid = Guid.NewGuid(), Sku = "SHARED-CODE", Name = "Org B's Widget", Status = "ACTIVE", IsActive = true, CreatedBy = 1 });
            await dbB.SaveChangesAsync();
        };
        await act.Should().NotThrowAsync();

        using var queryAsOrgA = NewDb(dbName, orgA);
        using var queryAsOrgB = NewDb(dbName, orgB);

        (await queryAsOrgA.Products.SingleAsync(p => p.Sku == "SHARED-CODE")).Name.Should().Be("Org A's Widget");
        (await queryAsOrgB.Products.SingleAsync(p => p.Sku == "SHARED-CODE")).Name.Should().Be("Org B's Widget");
    }
}
