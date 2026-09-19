using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Logistics.Tests;

// T-01 — proves the test harness itself works before anything is built on top of it:
// internals are visible, the tenant query filter isolates organizations, the super-admin
// bypass is real, and StampTenantScopedEntities behaves as documented.
public class HarnessTests
{
    // TC-01.3 — internals of SMS.Modules.Logistics are reachable from this assembly and a
    // round-trip through LogisticsDbContext works.
    [Fact]
    public async Task Carrier_round_trips_through_the_in_memory_context()
    {
        var (db, tenant, _) = LogisticsTestDb.New();

        db.Carriers.Add(TestData.Carrier(code: "LEOPARDS", name: "Leopards Courier"));
        await db.SaveChangesAsync();

        var read = await db.Carriers.SingleAsync();

        read.Name.Should().Be("Leopards Courier");
        read.Code.Should().Be("LEOPARDS");
        read.OrganizationId.Should().Be(tenant.OrganizationId);
    }

    // TC-01.4 — the global query filter hides another organization's rows.
    [Fact]
    public async Task Rows_of_another_organization_are_not_visible()
    {
        var orgA = Guid.NewGuid();
        var orgB = Guid.NewGuid();

        var (dbA, _, dbName) = LogisticsTestDb.New(orgA);
        dbA.Carriers.Add(TestData.Carrier());
        await dbA.SaveChangesAsync();

        await using var dbB = LogisticsTestDb.OpenAs(dbName, orgB);

        (await dbB.Carriers.ToListAsync()).Should().BeEmpty();
        (await dbA.Carriers.ToListAsync()).Should().HaveCount(1);
    }

    // TC-01.5 — IsSuperAdmin bypasses the filter. This is deliberate (see ITenantContext's
    // remarks: anonymous paths and the login flow rely on it), so it is pinned by a test
    // rather than left as an undocumented surprise.
    [Fact]
    public async Task Super_admin_sees_across_organizations()
    {
        var orgA = Guid.NewGuid();

        var (dbA, _, dbName) = LogisticsTestDb.New(orgA);
        dbA.Carriers.Add(TestData.Carrier());
        await dbA.SaveChangesAsync();

        await using var superAdmin = LogisticsTestDb.OpenAs(dbName, Guid.NewGuid(), isSuperAdmin: true);

        (await superAdmin.Carriers.ToListAsync()).Should().HaveCount(1);
    }

    // TC-01.6 — an entity added with OrganizationId unset is stamped with the ambient tenant.
    [Fact]
    public async Task Unset_organization_id_is_stamped_on_save()
    {
        var (db, tenant, _) = LogisticsTestDb.New();

        var carrier = TestData.Carrier();
        carrier.OrganizationId.Should().Be(Guid.Empty, "the builder leaves it unset");

        db.Carriers.Add(carrier);
        await db.SaveChangesAsync();

        carrier.OrganizationId.Should().Be(tenant.OrganizationId);
    }

    // TC-01.7 — an explicitly-set OrganizationId is NOT clobbered back to the ambient tenant.
    // StampTenantScopedEntities only fills Guid.Empty; cross-tenant writes (super admin
    // provisioning another org) depend on that, and a regression here would be silent.
    [Fact]
    public async Task Explicit_organization_id_is_not_overwritten()
    {
        var ambient = Guid.NewGuid();
        var other   = Guid.NewGuid();

        var (db, _, dbName) = LogisticsTestDb.New(ambient, isSuperAdmin: true);

        var carrier = TestData.Carrier(organizationId: other);
        db.Carriers.Add(carrier);
        await db.SaveChangesAsync();

        carrier.OrganizationId.Should().Be(other);

        // And it really landed under the other org, not the ambient one.
        await using var asOther = LogisticsTestDb.OpenAs(dbName, other);
        (await asOther.Carriers.ToListAsync()).Should().HaveCount(1);
    }

    // TC-02.1 / TC-02.4 (brought forward) — separate calls to New() never share a database,
    // so tests running in parallel cannot see each other's rows.
    [Fact]
    public async Task Each_harness_instance_gets_its_own_database()
    {
        var (first, _, firstName)   = LogisticsTestDb.New();
        var (second, _, secondName) = LogisticsTestDb.New();

        firstName.Should().NotBe(secondName);

        first.Carriers.Add(TestData.Carrier());
        await first.SaveChangesAsync();

        (await second.Carriers.ToListAsync()).Should().BeEmpty();
    }

    // Sanity check on the other existing entity, so the second half of the current schema is
    // also known to map correctly before T-07 starts adding tables beside it.
    [Fact]
    public async Task Shipment_round_trips_and_is_tenant_scoped()
    {
        var orgA = Guid.NewGuid();
        var (db, _, dbName) = LogisticsTestDb.New(orgA);

        db.Shipments.Add(TestData.Shipment());
        await db.SaveChangesAsync();

        (await db.Shipments.SingleAsync()).ShipmentNumber.Should().Be("SHP-2026-00001");

        await using var other = LogisticsTestDb.OpenAs(dbName, Guid.NewGuid());
        (await other.Shipments.ToListAsync()).Should().BeEmpty();
    }
}
