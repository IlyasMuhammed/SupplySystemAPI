using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using SMS.Shared.Common;
using Xunit;

namespace SMS.Modules.Logistics.Tests;

/// <summary>
/// F35 — every tenant-scoped table must have an index leading with <c>OrganizationId</c>.
/// <para>
/// <b>Why this test is here and not only in the integration suite.</b> The assertion already
/// existed, in <c>SMS.Integration.Tests.QueryFilterPerformanceTests</c> — but that suite needs a
/// live SQL Server it currently cannot reach, so it has been failing for a reason unrelated to
/// what it checks, and nobody could tell the two apart. This asks the same question of the
/// <em>model</em>, which needs no database, and so actually runs.
/// </para>
/// <para>
/// It covers Logistics only, because a test project can only see its own module's context. The
/// rule it pins is enforced for every module by <see cref="TenantScopingExtensions.ApplyTenantIndexes"/>.
/// </para>
/// </summary>
public class TenantIndexTests
{
    [Fact]
    public void Every_tenant_scoped_table_has_an_index_leading_with_the_organization()
    {
        var (db, _, _) = LogisticsTestDb.New();

        var missing = db.Model.GetEntityTypes()
            .Where(IsTenantScopedTable)
            .Where(e => !LeadsWithOrganizationId(e))
            .Select(e => e.ClrType.Name)
            .OrderBy(n => n)
            .ToList();

        missing.Should().BeEmpty(
            "the tenant query filter puts WHERE OrganizationId = @org on every query against every "
          + "one of these tables, and without a leading-column index that cannot seek");
    }

    [Fact]
    public void The_check_actually_looks_at_something()
    {
        // Guards the guard: a query that silently matched nothing would pass the test above
        // however broken the rule became.
        var (db, _, _) = LogisticsTestDb.New();

        db.Model.GetEntityTypes().Count(IsTenantScopedTable)
            .Should().BeGreaterThan(20);
    }

    [Fact]
    public void No_table_carries_a_redundant_second_index_on_the_organization_alone()
    {
        // ApplyTenantIndexes adds one only where none leads with OrganizationId already. A table
        // with both a composite starting with it and a bare one is paying for an index twice.
        var (db, _, _) = LogisticsTestDb.New();

        var redundant = db.Model.GetEntityTypes()
            .Where(IsTenantScopedTable)
            .Where(e => e.GetIndexes().Count(i =>
                i.Properties.Count == 1 &&
                i.Properties[0].Name == nameof(ITenantScopedEntity.OrganizationId)) > 0
                     && e.GetIndexes().Any(i =>
                i.Properties.Count > 1 &&
                i.Properties[0].Name == nameof(ITenantScopedEntity.OrganizationId)))
            .Select(e => e.ClrType.Name)
            .ToList();

        redundant.Should().BeEmpty();
    }

    private static bool IsTenantScopedTable(IEntityType entityType) =>
        (typeof(ITenantScopedEntity).IsAssignableFrom(entityType.ClrType)
      || typeof(IGloballyExemptTenantScopedEntity).IsAssignableFrom(entityType.ClrType))
      && !entityType.IsOwned()
      && entityType.FindPrimaryKey() is not null;

    private static bool LeadsWithOrganizationId(IEntityType entityType)
    {
        static bool Leads(IReadOnlyList<IProperty> properties) =>
            properties.Count > 0
         && properties[0].Name == nameof(ITenantScopedEntity.OrganizationId);

        if (entityType.FindPrimaryKey() is { } key && Leads(key.Properties)) return true;

        return entityType.GetIndexes().Any(i => Leads(i.Properties));
    }
}
