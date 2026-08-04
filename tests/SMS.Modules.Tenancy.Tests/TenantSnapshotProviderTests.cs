using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using SMS.Modules.Tenancy.Data;
using SMS.Modules.Tenancy.Domain;
using SMS.Modules.Tenancy.Services;
using Xunit;

namespace SMS.Modules.Tenancy.Tests;

// MT-004 acceptance criteria: snapshot resolution correctness (active status + enabled feature
// codes) and the 5-minute TTL cache that lets feature/status changes propagate quickly without a
// DB hit on every request.
public class TenantSnapshotProviderTests
{
    private static TenancyDbContext NewDb(string dbName) =>
        new(new DbContextOptionsBuilder<TenancyDbContext>().UseInMemoryDatabase(dbName).Options);

    private static async Task<Guid> SeedOrgWithFeatureAsync(TenancyDbContext db, bool orgActive, bool featureEnabled)
    {
        var orgId = Guid.NewGuid();
        var featureDefId = Guid.NewGuid();

        db.Organizations.Add(new Organization { Id = orgId, OrgCode = "T1", OrgName = "Test Org", IsActive = orgActive, CreatedBy = 1, CreatedDate = DateTime.UtcNow });
        db.FeatureDefinitions.Add(new FeatureDefinition { Id = featureDefId, FeatureCode = "MODULE_MIR", FeatureName = "MIR", Category = "MODULE" });
        db.OrganizationFeatures.Add(new OrganizationFeature { Id = Guid.NewGuid(), OrganizationId = orgId, FeatureDefinitionId = featureDefId, IsEnabled = featureEnabled });
        await db.SaveChangesAsync();

        return orgId;
    }

    [Fact]
    public async Task GetSnapshotAsync_ForActiveOrgWithEnabledFeature_ReturnsActiveAndFeatureCode()
    {
        using var db = NewDb(Guid.NewGuid().ToString());
        var orgId = await SeedOrgWithFeatureAsync(db, orgActive: true, featureEnabled: true);

        var provider = new TenantSnapshotProvider(db, new MemoryCache(new MemoryCacheOptions()));
        var snapshot = await provider.GetSnapshotAsync(orgId);

        snapshot.Should().NotBeNull();
        snapshot!.IsActive.Should().BeTrue();
        snapshot.EnabledFeatureCodes.Should().Contain("MODULE_MIR");
    }

    [Fact]
    public async Task GetSnapshotAsync_ForInactiveOrg_ReturnsIsActiveFalse()
    {
        using var db = NewDb(Guid.NewGuid().ToString());
        var orgId = await SeedOrgWithFeatureAsync(db, orgActive: false, featureEnabled: true);

        var provider = new TenantSnapshotProvider(db, new MemoryCache(new MemoryCacheOptions()));
        var snapshot = await provider.GetSnapshotAsync(orgId);

        snapshot.Should().NotBeNull();
        snapshot!.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task GetSnapshotAsync_DisabledFeature_IsNotInEnabledFeatureCodes()
    {
        using var db = NewDb(Guid.NewGuid().ToString());
        var orgId = await SeedOrgWithFeatureAsync(db, orgActive: true, featureEnabled: false);

        var provider = new TenantSnapshotProvider(db, new MemoryCache(new MemoryCacheOptions()));
        var snapshot = await provider.GetSnapshotAsync(orgId);

        snapshot!.EnabledFeatureCodes.Should().NotContain("MODULE_MIR");
    }

    [Fact]
    public async Task GetSnapshotAsync_UnknownOrganizationId_ReturnsNull()
    {
        using var db = NewDb(Guid.NewGuid().ToString());
        var provider = new TenantSnapshotProvider(db, new MemoryCache(new MemoryCacheOptions()));

        var snapshot = await provider.GetSnapshotAsync(Guid.NewGuid());

        snapshot.Should().BeNull();
    }

    [Fact]
    public async Task GetSnapshotAsync_SecondCallWithinTtl_ReturnsCachedValue_NotFreshDbState()
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = NewDb(dbName);
        var orgId = await SeedOrgWithFeatureAsync(db, orgActive: true, featureEnabled: true);

        var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = new TenantSnapshotProvider(db, cache);

        var first = await provider.GetSnapshotAsync(orgId);
        first!.IsActive.Should().BeTrue();

        // Mutate the underlying row directly (bypassing the provider) to simulate a Super Admin
        // deactivating the org in another request.
        var org = await db.Organizations.FirstAsync(o => o.Id == orgId);
        org.IsActive = false;
        await db.SaveChangesAsync();

        var second = await provider.GetSnapshotAsync(orgId);
        second!.IsActive.Should().BeTrue("the cached snapshot should still be served within the TTL");
    }

    [Fact]
    public async Task Invalidate_ForcesNextGetSnapshotAsync_ToReflectCurrentDbState()
    {
        var dbName = Guid.NewGuid().ToString();
        using var db = NewDb(dbName);
        var orgId = await SeedOrgWithFeatureAsync(db, orgActive: true, featureEnabled: true);

        var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = new TenantSnapshotProvider(db, cache);

        await provider.GetSnapshotAsync(orgId);

        var org = await db.Organizations.FirstAsync(o => o.Id == orgId);
        org.IsActive = false;
        await db.SaveChangesAsync();

        provider.Invalidate(orgId);

        var afterInvalidate = await provider.GetSnapshotAsync(orgId);
        afterInvalidate!.IsActive.Should().BeFalse();
    }
}
