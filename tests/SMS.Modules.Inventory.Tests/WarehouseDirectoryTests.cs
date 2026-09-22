using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Common;
using Xunit;
using InventoryWarehouse = SMS.Modules.Inventory.Domain.Warehouse;

namespace SMS.Modules.Inventory.Tests;

// Logistics keeps a warehouse as a bare UUID. This is how it learns where that warehouse is, so a carrier
// can be told where to collect a delivery from.
public class WarehouseDirectoryTests
{
    private static InventoryDbContext Open(string dbName, StaticTenantContext tenant) =>
        new(new DbContextOptionsBuilder<InventoryDbContext>().UseInMemoryDatabase(dbName).Options, tenant);

    private static async Task<Guid> Seed(InventoryDbContext db)
    {
        var warehouse = new InventoryWarehouse
        {
            Code = "3232", Name = "Faisalabad", Address = "Gulberg Road", City = "Faisalabad",
            Country = "Pakistan", ContactName = "Asif", ContactPhone = "987876765", IsActive = true
        };

        db.Warehouses.Add(warehouse);
        await db.SaveChangesAsync();

        return warehouse.Uuid;
    }

    [Fact]
    public async Task A_warehouse_is_found_by_its_uuid_with_where_it_is_and_who_to_call()
    {
        var tenant = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var db     = Open(Guid.NewGuid().ToString(), tenant);
        var uuid   = await Seed(db);

        var found = await new WarehouseDirectory(db).FindAsync(uuid);

        found.Should().NotBeNull();
        found!.Uuid.Should().Be(uuid);
        found.Code.Should().Be("3232");
        found.Name.Should().Be("Faisalabad");
        found.Address.Should().Be("Gulberg Road");
        found.City.Should().Be("Faisalabad");
        found.Country.Should().Be("Pakistan");
        found.ContactName.Should().Be("Asif");
        found.ContactPhone.Should().Be("987876765");
        found.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task An_unknown_warehouse_is_null_not_an_error()
    {
        var db = Open(Guid.NewGuid().ToString(), new StaticTenantContext { OrganizationId = Guid.NewGuid() });

        (await new WarehouseDirectory(db).FindAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task Another_organizations_warehouse_cannot_be_read()
    {
        // The same isolation as everywhere else: knowing a warehouse's UUID is not a way to read its address.
        var dbName = Guid.NewGuid().ToString();
        var uuid   = await Seed(Open(dbName, new StaticTenantContext { OrganizationId = Guid.NewGuid() }));

        var stranger = Open(dbName, new StaticTenantContext { OrganizationId = Guid.NewGuid() });

        (await new WarehouseDirectory(stranger).FindAsync(uuid)).Should().BeNull();
    }
}
