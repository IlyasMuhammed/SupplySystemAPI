using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Inventory.Domain;

namespace SMS.Modules.Inventory.Data.Maps;

/// <summary>
/// A34 C3 (D-10) — <c>inventory.LeadTimeDefaults</c>, one row per organization. The 1/3/1/0/0/0 column defaults exist
/// only in the migration's SQL, not as <c>HasDefaultValue</c> here: with a store default EF leaves out a value equal to
/// the CLR default (0) on insert, so a first PUT of "pick/pack 0" would silently store 1. The entity's initializers
/// carry the same defaults on the C# side.
/// </summary>
internal sealed class LeadTimeDefaultsMap : IEntityTypeConfiguration<LeadTimeDefaults>
{
    public void Configure(EntityTypeBuilder<LeadTimeDefaults> b)
    {
        b.ToTable("LeadTimeDefaults");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Uuid).IsRequired();
        b.HasIndex(x => x.Uuid).IsUnique();

        b.Property(x => x.OrganizationId).IsRequired();
        // One row per organization: a concurrent first save of two PUTs collides here and the loser re-reads and
        // updates (LeadTimeDefaultsService). Also the tenant-filter index (it leads with OrganizationId).
        b.HasIndex(x => x.OrganizationId).IsUnique();

        b.Property(x => x.RowVersion).IsRowVersion();
    }
}
