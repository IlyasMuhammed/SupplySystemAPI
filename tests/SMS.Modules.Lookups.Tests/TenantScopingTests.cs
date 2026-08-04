using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Lookups.Data;
using SMS.Modules.Lookups.Domain;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Lookups.Tests;

// MT-003 acceptance criteria: global lookup values (e.g. currencies) are visible to every org;
// custom lookup values are scoped to the org that created them.

public class TenantScopingTests
{
    private static LookupsDbContext NewDb(string dbName, ITenantContext tenantContext) =>
        new(new DbContextOptionsBuilder<LookupsDbContext>().UseInMemoryDatabase(dbName).Options, tenantContext);

    [Fact]
    public async Task GlobalLookupValue_IsVisibleToEveryOrg()
    {
        var dbName = Guid.NewGuid().ToString();
        var seeder = new StaticTenantContext(); // no HttpContext-equivalent — matches app-startup seeding
        var typeId = Guid.NewGuid();

        using (var db = NewDb(dbName, seeder))
        {
            db.LookupTypes.Add(new LookupType { Id = typeId, Slug = "currency", Name = "Currency", IsActive = true });
            db.LookupValues.Add(new LookupValue
            {
                Id = Guid.NewGuid(), TypeId = typeId, DisplayName = "Pakistani Rupee (PKR)",
                IsActive = true, IsGlobal = true, OrganizationId = null
            });
            await db.SaveChangesAsync();
        }

        var orgA = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var orgB = new StaticTenantContext { OrganizationId = Guid.NewGuid() };

        using var dbA = NewDb(dbName, orgA);
        using var dbB = NewDb(dbName, orgB);

        (await dbA.LookupValues.AnyAsync(v => v.DisplayName == "Pakistani Rupee (PKR)")).Should().BeTrue();
        (await dbB.LookupValues.AnyAsync(v => v.DisplayName == "Pakistani Rupee (PKR)")).Should().BeTrue();
    }

    [Fact]
    public async Task CustomLookupValue_IsScopedToItsOwningOrg_NotVisibleToOtherOrgs()
    {
        var dbName = Guid.NewGuid().ToString();
        var typeId = Guid.NewGuid();
        var orgA = new StaticTenantContext { OrganizationId = Guid.NewGuid() };
        var orgB = new StaticTenantContext { OrganizationId = Guid.NewGuid() };

        using (var dbA = NewDb(dbName, orgA))
        {
            dbA.LookupTypes.Add(new LookupType { Id = typeId, Slug = "pr-type", Name = "PR Type", IsActive = true });
            // IsGlobal = false, OrganizationId left unset — relies on auto-stamp, matching
            // LookupsRepository.CreateLookupValue's real behaviour for admin-created values.
            dbA.LookupValues.Add(new LookupValue { Id = Guid.NewGuid(), TypeId = typeId, DisplayName = "Org A Custom Value", IsActive = true, IsGlobal = false });
            await dbA.SaveChangesAsync();
        }

        using var queryAsOrgA = NewDb(dbName, orgA);
        using var queryAsOrgB = NewDb(dbName, orgB);

        (await queryAsOrgA.LookupValues.AnyAsync(v => v.DisplayName == "Org A Custom Value")).Should().BeTrue();
        (await queryAsOrgB.LookupValues.AnyAsync(v => v.DisplayName == "Org A Custom Value")).Should().BeFalse();

        var stamped = await queryAsOrgA.LookupValues.SingleAsync(v => v.DisplayName == "Org A Custom Value");
        stamped.OrganizationId.Should().Be(orgA.OrganizationId);
    }
}
