using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Domain;
using SMS.Modules.Suppliers.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Suppliers.Tests;

/// <summary>
/// A32 — what Demand reads to refuse a sale inquiry or quotation for a partner that is not a customer
/// (BR-C1-01/BR-C2-01). Own organization only: another organization's partner is absent, super admin included.
/// </summary>
public class PartnerRoleLookupTests
{
    private static (SuppliersDbContext Db, PartnerRoleLookup Lookup) New(string dbName, Guid org, bool superAdmin = false)
    {
        var tenant = new StaticTenantContext { OrganizationId = org, IsSuperAdmin = superAdmin };
        var db = new SuppliersDbContext(new DbContextOptionsBuilder<SuppliersDbContext>().UseInMemoryDatabase(dbName).Options, tenant);
        return (db, new PartnerRoleLookup(db, tenant));
    }

    private static BusinessPartner Partner(Guid org, bool customer, bool vendor, bool active = true, bool deleted = false) => new()
    {
        UUID = Guid.NewGuid(), OrganizationId = org, SupplierName = customer ? "GlobalTech Co" : "Steel Supplier",
        SupplierCode = $"P{Guid.NewGuid():N}"[..8], PartnerType = customer ? "CUSTOMER" : "VENDOR",
        IsCustomer = customer, IsVendor = vendor, IsActive = active, IsDelete = deleted,
        Status = "ACTIVE", CreatedBy = 1, CreatedDate = DateTime.UtcNow
    };

    [Fact]
    public async Task A_customer_reads_as_a_customer_with_its_name_and_a_vendor_as_not_one()
    {
        var org = Guid.NewGuid();
        var (db, lookup) = New(Guid.NewGuid().ToString(), org);
        var customer = Partner(org, customer: true, vendor: false);
        var vendor = Partner(org, customer: false, vendor: true);
        db.BusinessPartners.AddRange(customer, vendor);
        await db.SaveChangesAsync();

        var c = await lookup.GetAsync(customer.UUID);
        var v = await lookup.GetAsync(vendor.UUID);

        c.Should().BeEquivalentTo(new PartnerRoleInfo(customer.UUID, "GlobalTech Co", true, false, true));
        v!.IsCustomer.Should().BeFalse();
    }

    [Fact]
    public async Task A_deactivated_partner_is_returned_inactive_and_a_deleted_one_is_absent()
    {
        var org = Guid.NewGuid();
        var (db, lookup) = New(Guid.NewGuid().ToString(), org);
        var inactive = Partner(org, customer: true, vendor: false, active: false);
        var deleted = Partner(org, customer: true, vendor: false, deleted: true);
        db.BusinessPartners.AddRange(inactive, deleted);
        await db.SaveChangesAsync();

        (await lookup.GetAsync(inactive.UUID))!.IsActive.Should().BeFalse();
        (await lookup.GetAsync(deleted.UUID)).Should().BeNull();
        (await lookup.GetAsync(Guid.Empty)).Should().BeNull();
    }

    [Fact]
    public async Task Another_organizations_partner_is_absent_even_for_a_super_admin()
    {
        var dbName = Guid.NewGuid().ToString();
        var theirOrg = Guid.NewGuid();
        var (theirDb, _) = New(dbName, theirOrg);
        var theirs = Partner(theirOrg, customer: true, vendor: false);
        theirDb.BusinessPartners.Add(theirs);
        await theirDb.SaveChangesAsync();
        var (_, admin) = New(dbName, Guid.NewGuid(), superAdmin: true);

        (await admin.GetAsync(theirs.UUID)).Should().BeNull();
    }
}
