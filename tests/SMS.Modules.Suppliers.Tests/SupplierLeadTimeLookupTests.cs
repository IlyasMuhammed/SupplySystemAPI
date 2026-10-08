using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Domain;
using SMS.Modules.Suppliers.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Suppliers.Tests;

/// <summary>
/// A34 D-11 tier 4 — <see cref="ISupplierLeadTimeLookup"/>: BusinessPartner.LeadTimeDays for the lead-time calculator.
/// The organization is explicit: another organization's supplier is absent, whoever the caller is.
/// </summary>
public class SupplierLeadTimeLookupTests
{
    private static SuppliersDbContext Db(string dbName, Guid org, bool superAdmin = false) => new(
        new DbContextOptionsBuilder<SuppliersDbContext>().UseInMemoryDatabase(dbName).Options,
        new StaticTenantContext { OrganizationId = org, IsSuperAdmin = superAdmin });

    private static BusinessPartner Supplier(Guid org, int? leadTimeDays, bool active = true, bool deleted = false) => new()
    {
        UUID = Guid.NewGuid(), OrganizationId = org, SupplierName = "Steel Supplier", SupplierCode = $"P{Guid.NewGuid():N}"[..8],
        PartnerType = "VENDOR", IsVendor = true, IsActive = active, IsDelete = deleted, Status = "ACTIVE",
        LeadTimeDays = leadTimeDays, CreatedBy = 1, CreatedDate = DateTime.UtcNow
    };

    [Fact]
    public async Task Returns_the_lead_time_days_of_the_organizations_suppliers_that_have_one()
    {
        var dbName = Guid.NewGuid().ToString();
        var org = Guid.NewGuid();
        var withDays = Supplier(org, 12);
        var inactive = Supplier(org, 9, active: false);
        var none = Supplier(org, null);
        var deleted = Supplier(org, 4, deleted: true);
        var foreign = Supplier(Guid.NewGuid(), 30);
        await using (var db = Db(dbName, org))
        {
            db.BusinessPartners.AddRange(withDays, inactive, none, deleted, foreign);
            await db.SaveChangesAsync();
        }

        // A super admin of yet another organization: no tenant filter, so only the explicit organization keeps others out.
        var lookup = new SupplierLeadTimeLookup(Db(dbName, Guid.NewGuid(), superAdmin: true));
        var days = await lookup.GetAsync(org, [withDays.UUID, inactive.UUID, none.UUID, deleted.UUID, foreign.UUID, Guid.NewGuid()]);

        days.Should().BeEquivalentTo(new Dictionary<Guid, int> { [withDays.UUID] = 12, [inactive.UUID] = 9 });
    }

    [Fact]
    public async Task An_empty_request_reads_nothing()
    {
        var lookup = new SupplierLeadTimeLookup(Db(Guid.NewGuid().ToString(), Guid.NewGuid()));

        (await lookup.GetAsync(Guid.NewGuid(), [])).Should().BeEmpty();
    }
}
