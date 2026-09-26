using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Inventory.Domain;

namespace SMS.Modules.Inventory.Data.Maps;

internal sealed class AllocationDemandMap : IEntityTypeConfiguration<AllocationDemand>
{
    public void Configure(EntityTypeBuilder<AllocationDemand> b)
    {
        b.ToTable("AllocationDemands");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Uuid).IsRequired();
        b.Property(x => x.DemandType).HasMaxLength(30).IsRequired();
        b.Property(x => x.Reference).HasMaxLength(50).IsRequired();
        b.Property(x => x.RequiredQty).HasColumnType("decimal(18,4)");
        b.Property(x => x.ConsumedQty).HasColumnType("decimal(18,4)");
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.Uuid).IsUnique();
        b.HasIndex(x => x.OrganizationId);
        // What a run reads: every open demand for one variant.
        b.HasIndex(x => new { x.OrganizationId, x.VariantUuid, x.Status });
        // What a caller re-registering the same line hits.
        b.HasIndex(x => new { x.OrganizationId, x.DemandType, x.DemandUuid, x.DemandLineUuid });
    }
}

internal sealed class AllocationSupplyMap : IEntityTypeConfiguration<AllocationSupply>
{
    public void Configure(EntityTypeBuilder<AllocationSupply> b)
    {
        b.ToTable("AllocationSupplies");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Uuid).IsRequired();
        b.Property(x => x.SupplyType).HasMaxLength(30).IsRequired();
        b.Property(x => x.Reference).HasMaxLength(50).IsRequired();
        b.Property(x => x.ExpectedQty).HasColumnType("decimal(18,4)");
        b.Property(x => x.ReceivedQty).HasColumnType("decimal(18,4)");
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.Uuid).IsUnique();
        b.HasIndex(x => x.OrganizationId);
        b.HasIndex(x => new { x.OrganizationId, x.VariantUuid, x.Status });
        b.HasIndex(x => new { x.OrganizationId, x.SupplyType, x.SupplyUuid, x.SupplyLineUuid });
    }
}

internal sealed class AllocationRecordMap : IEntityTypeConfiguration<AllocationRecord>
{
    public void Configure(EntityTypeBuilder<AllocationRecord> b)
    {
        b.ToTable("AllocationRecords");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Uuid).IsRequired();
        b.Property(x => x.AllocatedQty).HasColumnType("decimal(18,4)");
        b.Property(x => x.ConsumedQty).HasColumnType("decimal(18,4)");
        b.Property(x => x.SupplyType).HasMaxLength(30).IsRequired();
        b.Property(x => x.AllocationType).HasMaxLength(20).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.ReleaseReason).HasMaxLength(500);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Property(x => x.OrganizationId).IsRequired();
        b.Ignore(x => x.Remaining);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.HasIndex(x => x.OrganizationId);
        // §27.3 IX_Alloc_Product_WH — availability and listing by variant/warehouse.
        b.HasIndex(x => new { x.OrganizationId, x.VariantUuid, x.WarehouseUuid, x.Status });
        // §27.3 IX_Alloc_Demand — what one demand holds.
        b.HasIndex(x => new { x.DemandId, x.Status });
        b.HasOne(x => x.Demand).WithMany(d => d.Allocations)
            .HasForeignKey(x => x.DemandId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Supply).WithMany(s => s.Allocations)
            .HasForeignKey(x => x.SupplyId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class AllocationRuleMap : IEntityTypeConfiguration<AllocationRule>
{
    public void Configure(EntityTypeBuilder<AllocationRule> b)
    {
        b.ToTable("AllocationRules");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();
        b.Property(x => x.Uuid).IsRequired();
        b.Property(x => x.RuleName).HasMaxLength(100).IsRequired();
        b.Property(x => x.DemandTypeFilter).HasMaxLength(30);
        b.Property(x => x.SortField).HasMaxLength(30).IsRequired();
        b.Property(x => x.SortDirection).HasMaxLength(4).IsRequired();
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.Uuid).IsUnique();
        b.HasIndex(x => new { x.OrganizationId, x.PriorityOrder });
    }
}
