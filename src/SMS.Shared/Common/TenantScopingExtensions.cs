using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace SMS.Shared.Common;

// Implemented by every tenant-scoped DbContext — exposes the injected ITenantContext as an
// instance property so ApplyTenantQueryFilters can close over "this" (the DbContext instance)
// rather than the ITenantContext parameter directly. This distinction matters: EF Core caches
// the compiled model per DbContext *type*, so OnModelCreating only really runs once per type for
// the process's lifetime — a query filter closure that captures the ITenantContext parameter
// directly gets permanently "stuck" referencing whichever instance happened to build the model
// first. EF Core specifically rewrites captured constants whose static type is the model's own
// DbContext type to reference the currently-executing instance at each query, so closing over
// "this" (typed as the concrete DbContext) is what makes the filter behave correctly per-request.
public interface ITenantScopedDbContext
{
    ITenantContext TenantContext { get; }
}

// Shared by every tenant-scoped DbContext so each one only needs ~3 lines: one call in
// OnModelCreating and one call at the top of its SaveChanges/SaveChangesAsync overrides. Avoids
// hand-writing a HasQueryFilter lambda per entity across ~90 entities in 8 modules.
public static class TenantScopingExtensions
{
    // Called from SaveChanges()/SaveChangesAsync() overrides, before base.SaveChanges[Async]().
    // Only fills in OrganizationId when it hasn't already been set — most service/repository code
    // relies entirely on this for auto-stamping, but a few cross-tenant operations (e.g. a Super
    // Admin creating a new organization's initial admin user) legitimately set a DIFFERENT org's
    // id explicitly, and that must not be clobbered back to the caller's own ambient tenant.
    public static void StampTenantScopedEntities(this DbContext db, ITenantContext tenantContext)
    {
        foreach (var entry in db.ChangeTracker.Entries())
        {
            if (entry.State != EntityState.Added) continue;

            switch (entry.Entity)
            {
                case ITenantScopedEntity { OrganizationId: var orgId } scoped when orgId == Guid.Empty:
                    scoped.OrganizationId = tenantContext.OrganizationId;
                    break;
                // Global rows (seeded catalog values) are stamped explicitly by the seeder itself
                // (IsGlobal=true, OrganizationId=null) — only stamp genuinely new custom rows.
                case IGloballyExemptTenantScopedEntity exempt when !exempt.IsGlobal && exempt.OrganizationId is null:
                    exempt.OrganizationId = tenantContext.OrganizationId;
                    break;
            }
        }
    }

    // Called once from OnModelCreating as modelBuilder.ApplyTenantQueryFilters(this) — the "this"
    // (not _tenantContext directly) is what makes per-instance re-evaluation work; see the
    // ITenantScopedDbContext doc comment above.
    public static void ApplyTenantQueryFilters<TContext>(this ModelBuilder modelBuilder, TContext context)
        where TContext : DbContext, ITenantScopedDbContext
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;

            if (typeof(ITenantScopedEntity).IsAssignableFrom(clrType))
            {
                typeof(TenantScopingExtensions)
                    .GetMethod(nameof(ApplyPlainFilter), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(clrType, typeof(TContext))
                    .Invoke(null, [modelBuilder, context]);
            }
            else if (typeof(IGloballyExemptTenantScopedEntity).IsAssignableFrom(clrType))
            {
                typeof(TenantScopingExtensions)
                    .GetMethod(nameof(ApplyExemptFilter), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(clrType, typeof(TContext))
                    .Invoke(null, [modelBuilder, context]);
            }
        }
    }

    /// <summary>
    /// Gives every tenant-scoped table an index that leads with <c>OrganizationId</c>, unless it
    /// already has one.
    /// <para>
    /// <b>Why (finding F35).</b> <see cref="ApplyTenantQueryFilters"/> puts
    /// <c>WHERE OrganizationId = @org</c> on <em>every</em> query against <em>every</em> one of
    /// these tables — and not one of them had a supporting index. The integration test
    /// <c>QueryFilterPerformanceTests</c> was written to catch exactly that and had been failing
    /// honestly ever since. At today's row counts the optimizer prefers a scan anyway and nothing
    /// looks wrong; the bill arrives later, on every table at once.
    /// </para>
    /// <para>
    /// <b>Only where one is missing.</b> A table whose own map already declares
    /// <c>(OrganizationId, DocumentNumber)</c> or similar is covered — SQL Server can seek on a
    /// leading-column prefix — and a second single-column index there would cost writes and buy
    /// nothing. The primary key counts too.
    /// </para>
    /// <para>
    /// <b>Done here rather than in ninety maps</b> for the same reason the filters are: the next
    /// tenant-scoped entity somebody adds gets this without having to remember it.
    /// </para>
    /// </summary>
    public static void ApplyTenantIndexes(this ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;

            var scoped = typeof(ITenantScopedEntity).IsAssignableFrom(clrType)
                      || typeof(IGloballyExemptTenantScopedEntity).IsAssignableFrom(clrType);

            if (!scoped) continue;

            // Owned types share their owner's table, and a keyless entity is a view or a raw query
            // — neither has a table of its own to index.
            if (entityType.IsOwned() || entityType.FindPrimaryKey() is null) continue;

            var organizationId = entityType.FindProperty(nameof(ITenantScopedEntity.OrganizationId));

            if (organizationId is null) continue;

            if (LeadsWithOrganizationId(entityType)) continue;

            entityType.AddIndex(organizationId);
        }
    }

    /// <summary>
    /// Whether any existing index — or the primary key — already starts with <c>OrganizationId</c>.
    /// The leading column is what matters: SQL Server can seek on a prefix of a composite key, and
    /// cannot seek on a column buried in the middle of one.
    /// </summary>
    private static bool LeadsWithOrganizationId(IMutableEntityType entityType)
    {
        static bool Leads(IReadOnlyList<IMutableProperty> properties) =>
            properties.Count > 0
         && properties[0].Name == nameof(ITenantScopedEntity.OrganizationId);

        if (entityType.FindPrimaryKey() is { } key && Leads(key.Properties)) return true;

        return entityType.GetIndexes().Any(i => Leads(i.Properties));
    }

    private static void ApplyPlainFilter<TEntity, TContext>(ModelBuilder modelBuilder, TContext context)
        where TEntity : class, ITenantScopedEntity
        where TContext : DbContext, ITenantScopedDbContext
    {
        modelBuilder.Entity<TEntity>().HasQueryFilter(e =>
            context.TenantContext.IsSuperAdmin || e.OrganizationId == context.TenantContext.OrganizationId);
    }

    private static void ApplyExemptFilter<TEntity, TContext>(ModelBuilder modelBuilder, TContext context)
        where TEntity : class, IGloballyExemptTenantScopedEntity
        where TContext : DbContext, ITenantScopedDbContext
    {
        modelBuilder.Entity<TEntity>().HasQueryFilter(e =>
            context.TenantContext.IsSuperAdmin || e.IsGlobal || e.OrganizationId == context.TenantContext.OrganizationId);
    }
}
