using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Demand.Domain;

namespace SMS.Modules.Demand.Data.Maps;

internal sealed class SaleOrderConfigMap : IEntityTypeConfiguration<SaleOrderConfig>
{
    public void Configure(EntityTypeBuilder<SaleOrderConfig> b)
    {
        b.ToTable("sale_order_config");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.Uuid).IsRequired();
        b.HasIndex(x => x.Uuid).IsUnique();

        b.Property(x => x.SupplierSelectionMode).HasMaxLength(20).IsRequired();
        b.Property(x => x.AutoPoApprovalMode).HasMaxLength(20).IsRequired();
        b.Property(x => x.DefaultFulfillmentMode).HasMaxLength(20).IsRequired();
        b.Property(x => x.IntimationCcEmails).HasMaxLength(500);

        b.Property(x => x.AutoPoEnabled).HasDefaultValue(true);
        b.Property(x => x.DropShipEnabled).HasDefaultValue(false);
        b.Property(x => x.SelfPickupEnabled).HasDefaultValue(true);
        b.Property(x => x.ReservationTtlHours).HasDefaultValue(72);
        b.Property(x => x.PartialFulfillmentAllowed).HasDefaultValue(true);
        b.Property(x => x.EmailIntimationEnabled).HasDefaultValue(true);
        b.Property(x => x.ShipmentRequiredDefault).HasDefaultValue(true);

        // §3.2 — "one row per org."
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId).IsUnique();

        b.HasMany(x => x.AuditEntries)
         .WithOne(x => x.SaleOrderConfig)
         .HasForeignKey(x => x.SaleOrderConfigId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SaleOrderConfigAuditMap : IEntityTypeConfiguration<SaleOrderConfigAudit>
{
    public void Configure(EntityTypeBuilder<SaleOrderConfigAudit> b)
    {
        b.ToTable("sale_order_config_audit");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.FieldChanged).HasMaxLength(100).IsRequired();
        b.Property(x => x.OldValue).HasMaxLength(500);
        b.Property(x => x.NewValue).HasMaxLength(500);

        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.SaleOrderConfigId);
    }
}
