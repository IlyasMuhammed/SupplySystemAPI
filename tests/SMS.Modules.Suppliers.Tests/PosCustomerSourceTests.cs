using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Domain;
using SMS.Modules.Suppliers.Services;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Suppliers.Tests;

/// <summary>A38 — IPosCustomerSource over BusinessPartners (IsCustomer): explicit organization, delta by ModifiedAt, walk-in.</summary>
public class PosCustomerSourceTests
{
    private sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public DateTime Now { get; set; } = utc;
        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    private static readonly DateTime T0 = new(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc);

    private sealed record Bed(SuppliersDbContext Db, PosCustomerSource Source, Guid Org, FixedClock Clock);

    private static Bed New()
    {
        WalkInCustomerSeeder.ResetCache();
        // A caller of ANOTHER organization: the source must not lean on the ambient tenant.
        var (db, _, _) = SuppliersTestDb.New(organizationId: Guid.NewGuid());
        var clock = new FixedClock(T0);
        db.Clock = clock;
        return new Bed(db, new PosCustomerSource(db, new WalkInCustomerSeeder(db)), Guid.NewGuid(), clock);
    }

    private static async Task<BusinessPartner> AddAsync(Bed b, string code, DateTime at, Guid? org = null,
        bool isCustomer = true, bool isActive = true, bool isDelete = false, decimal? credit = 500m, string? type = CustomerTypes.Individual)
    {
        b.Clock.Now = at;
        var p = new BusinessPartner
        {
            UUID = Guid.NewGuid(), OrganizationId = org ?? b.Org, SupplierCode = code, SupplierName = "Name " + code,
            IsCustomer = isCustomer, IsVendor = !isCustomer, PartnerType = isCustomer ? "CUSTOMER" : "VENDOR",
            CustomerType = isCustomer ? type : null, CreditLimit = credit, Phone = "042-1", Mobile = "0300-1", Email = code + "@x.test",
            Status = "ACTIVE", IsActive = isActive, IsDelete = isDelete, CreatedDate = at
        };
        b.Db.BusinessPartners.Add(p);
        await b.Db.SaveChangesAsync();
        return p;
    }

    [Fact]
    public async Task Changed_returns_customers_of_that_org_strictly_after_since_oldest_first()
    {
        var b = New();
        b.Clock.Now = T0.AddMinutes(-10);
        await b.Source.GetWalkInAsync(b.Org);                                    // walk-in at T0-10
        var c1 = await AddAsync(b, "C-1", T0);
        var c2 = await AddAsync(b, "C-2", T0.AddMinutes(1));
        await AddAsync(b, "V-1", T0.AddMinutes(2), isCustomer: false);           // vendor only
        await AddAsync(b, "C-OTHER", T0.AddMinutes(3), org: Guid.NewGuid());     // another organization
        var gone = await AddAsync(b, "C-GONE", T0.AddMinutes(4), isActive: false);

        var all = await b.Source.GetChangedAsync(b.Org, null, 100);
        all.Select(c => c.Code).Should().Equal(CustomerTypes.WalkInCode, "C-1", "C-2", "C-GONE");

        var delta = await b.Source.GetChangedAsync(b.Org, c1.ModifiedAt, 100);
        delta.Select(c => c.Uuid).Should().Equal(c2.UUID, gone.UUID);
        delta.Single(c => c.Uuid == gone.UUID).IsActive.Should().BeFalse("a deactivated customer is synced so the device drops it");
        delta.Should().OnlyContain(c => c.ModifiedAt.Kind == DateTimeKind.Utc);
    }

    [Fact]
    public async Task A_page_never_ends_inside_a_run_of_equal_modified_at()
    {
        var b = New();
        await b.Source.GetWalkInAsync(b.Org);                                   // T0
        await AddAsync(b, "C-1", T0.AddSeconds(1));
        await AddAsync(b, "C-2", T0.AddSeconds(1));
        await AddAsync(b, "C-3", T0.AddSeconds(1));
        await AddAsync(b, "C-4", T0.AddSeconds(2));

        var page = await b.Source.GetChangedAsync(b.Org, null, 2);

        page.Select(c => c.Code).Should().Equal(CustomerTypes.WalkInCode, "C-1", "C-2", "C-3");
        (await b.Source.GetChangedAsync(b.Org, page[^1].ModifiedAt, 2)).Select(c => c.Code).Should().Equal("C-4");
    }

    [Fact]
    public async Task Get_and_get_many_map_the_customer_and_ignore_deleted_vendors_and_other_orgs()
    {
        var b = New();
        var c = await AddAsync(b, "C-1", T0, credit: null, type: null);
        var inactive = await AddAsync(b, "C-IN", T0, isActive: false);
        var deleted = await AddAsync(b, "C-DEL", T0, isDelete: true);
        var vendor = await AddAsync(b, "V-1", T0, isCustomer: false);
        var other = await AddAsync(b, "C-X", T0, org: Guid.NewGuid());

        var one = (await b.Source.GetAsync(b.Org, c.UUID))!;
        one.Should().Be(new PosCustomer(c.UUID, "C-1", "Name C-1", CustomerTypes.Company, "042-1", "0300-1", "C-1@x.test",
            0m, true, false, T0));

        (await b.Source.GetAsync(b.Org, inactive.UUID))!.IsActive.Should().BeFalse();
        (await b.Source.GetAsync(b.Org, deleted.UUID)).Should().BeNull();
        (await b.Source.GetAsync(b.Org, vendor.UUID)).Should().BeNull();
        (await b.Source.GetAsync(b.Org, other.UUID)).Should().BeNull();
        (await b.Source.GetAsync(other.OrganizationId, other.UUID)).Should().NotBeNull();

        var many = await b.Source.GetManyAsync(b.Org, [c.UUID, inactive.UUID, deleted.UUID, vendor.UUID, other.UUID, c.UUID]);
        many.Select(m => m.Uuid).Should().BeEquivalentTo([c.UUID, inactive.UUID]);
        (await b.Source.GetManyAsync(b.Org, [])).Should().BeEmpty();
    }

    [Fact]
    public async Task Walk_in_is_created_once_per_org_and_returned()
    {
        var b = New();
        var first = await b.Source.GetWalkInAsync(b.Org);
        WalkInCustomerSeeder.ResetCache();
        var again = await b.Source.GetWalkInAsync(b.Org);

        again.Uuid.Should().Be(first.Uuid);
        (first.Code, first.CustomerType, first.IsSystem, first.IsActive, first.CreditLimit)
            .Should().Be((CustomerTypes.WalkInCode, CustomerTypes.WalkIn, true, true, 0m));
        (await b.Db.BusinessPartners.IgnoreQueryFilters().CountAsync(p => p.OrganizationId == b.Org)).Should().Be(1);

        var otherOrg = Guid.NewGuid();
        (await b.Source.GetWalkInAsync(otherOrg)).Uuid.Should().NotBe(first.Uuid);
    }

    [Fact]
    public async Task A_partner_already_coded_c_walkin_is_the_walk_in()
    {
        var b = New();
        var existing = await AddAsync(b, CustomerTypes.WalkInCode, T0, credit: 0m, type: CustomerTypes.Individual);

        var walkIn = await b.Source.GetWalkInAsync(b.Org);

        walkIn.Uuid.Should().Be(existing.UUID);
        (await b.Db.BusinessPartners.IgnoreQueryFilters().CountAsync(p => p.OrganizationId == b.Org)).Should().Be(1);
    }
}
