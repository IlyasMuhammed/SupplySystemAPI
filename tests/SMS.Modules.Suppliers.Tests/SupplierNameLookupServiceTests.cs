using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Domain;
using SMS.Modules.Suppliers.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Suppliers.Tests;

// P1-06 (Addendum 29 §1.7) introduced this as the shared cross-module lookup Rate Card
// (Addendum 28) and Finance's vendor invoice/payment name resolution depend on, filtered to
// is_vendor=1 since neither consumer could ever ask about anything else at the time. A29-P4-07
// dropped that filter — a sale order's PartnerId is a customer, and this is the same lookup it
// needs for one — so it now resolves any BusinessPartner regardless of type.
public class SupplierNameLookupServiceTests
{
    private static SuppliersDbContext NewDb() =>
        new(new DbContextOptionsBuilder<SuppliersDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options, new StaticTenantContext());

    private static BusinessPartner Seed(SuppliersDbContext db, string name, bool isVendor = true)
    {
        var p = new BusinessPartner
        {
            UUID = Guid.NewGuid(), SupplierName = name, SupplierCode = $"SUP-{Guid.NewGuid():N}"[..10],
            Status = "APPROVED", IsActive = true, IsVendor = isVendor,
            IsCustomer = !isVendor, CreatedBy = 1, CreatedDate = DateTime.UtcNow
        };
        db.BusinessPartners.Add(p);
        db.SaveChanges();
        return p;
    }

    [Fact]
    public async Task Resolves_names_for_vendors()
    {
        var db = NewDb();
        var vendor = Seed(db, "Acme Corp");
        var svc = new SupplierNameLookupService(db);

        var names = await svc.GetNamesAsync([vendor.UUID]);

        names.Should().ContainKey(vendor.UUID).WhoseValue.Should().Be("Acme Corp");
    }

    [Fact]
    public async Task Resolves_names_for_a_partner_that_is_not_a_vendor_too()
    {
        var db = NewDb();
        var customer = Seed(db, "Retail Customer", isVendor: false);
        var svc = new SupplierNameLookupService(db);

        var names = await svc.GetNamesAsync([customer.UUID]);

        names.Should().ContainKey(customer.UUID).WhoseValue.Should().Be("Retail Customer");
    }

    [Fact]
    public async Task Resolves_every_partner_in_a_mixed_id_list_regardless_of_type()
    {
        var db = NewDb();
        var vendor = Seed(db, "Vendor Co");
        var customer = Seed(db, "Customer Co", isVendor: false);
        var svc = new SupplierNameLookupService(db);

        var names = await svc.GetNamesAsync([vendor.UUID, customer.UUID]);

        names.Should().HaveCount(2);
        names[vendor.UUID].Should().Be("Vendor Co");
        names[customer.UUID].Should().Be("Customer Co");
    }

    [Fact]
    public async Task Empty_input_returns_empty_without_querying()
    {
        var db = NewDb();
        var svc = new SupplierNameLookupService(db);

        var names = await svc.GetNamesAsync([]);

        names.Should().BeEmpty();
    }
}
