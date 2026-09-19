using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Data.Maps;

internal sealed class DeliveryOrderMap : IEntityTypeConfiguration<DeliveryOrder>
{
    public void Configure(EntityTypeBuilder<DeliveryOrder> b)
    {
        b.ToTable("delivery_orders");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.DeliveryNumber).HasMaxLength(20).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.DeliveryNumber }).IsUnique();

        b.Property(x => x.Direction).HasMaxLength(20).IsRequired();
        b.Property(x => x.SourceType).HasMaxLength(20).IsRequired();
        b.Property(x => x.SourceNumber).HasMaxLength(30);
        b.Property(x => x.Priority).HasMaxLength(20).IsRequired();
        b.Property(x => x.Incoterm).HasMaxLength(10);

        b.Property(x => x.Status).HasMaxLength(30).IsRequired();
        b.Property(x => x.StatusBeforeHold).HasMaxLength(30);
        b.Property(x => x.HoldReason).HasMaxLength(500);
        b.Property(x => x.ClosureReason).HasMaxLength(500);
        b.Property(x => x.Notes).HasMaxLength(1000);

        b.Property(x => x.IsActive).HasDefaultValue(true);
        b.Property(x => x.RowVersion).IsRowVersion();

        // The cockpit's default view: open deliveries for this org, newest first.
        b.HasIndex(x => new { x.OrganizationId, x.Status });
        // "Show me the delivery for this PO" — the lookup every source module makes.
        b.HasIndex(x => new { x.OrganizationId, x.SourceType, x.SourceUuid });

        b.HasMany(x => x.Lines)
         .WithOne(x => x.DeliveryOrder)
         .HasForeignKey(x => x.DeliveryOrderId)
         .OnDelete(DeleteBehavior.Cascade);

        b.HasMany(x => x.Packages)
         .WithOne(x => x.DeliveryOrder)
         .HasForeignKey(x => x.DeliveryOrderId)
         .OnDelete(DeleteBehavior.Cascade);

        // Addresses are snapshots referenced by deliveries; deleting one out from under a
        // delivery would erase where the goods went.
        b.HasOne(x => x.ShipFromAddress)
         .WithMany()
         .HasForeignKey(x => x.ShipFromAddressId)
         .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(x => x.ShipToAddress)
         .WithMany()
         .HasForeignKey(x => x.ShipToAddressId)
         .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class DeliveryOrderLineMap : IEntityTypeConfiguration<DeliveryOrderLine>
{
    public void Configure(EntityTypeBuilder<DeliveryOrderLine> b)
    {
        b.ToTable("delivery_order_lines");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.HasIndex(x => new { x.DeliveryOrderId, x.LineNo }).IsUnique();

        b.Property(x => x.ItemDescription).HasMaxLength(300).IsRequired();
        b.Property(x => x.UnitOfMeasure).HasMaxLength(20);
        b.Property(x => x.BatchNumber).HasMaxLength(50);
        b.Property(x => x.SerialNumber).HasMaxLength(100);
        b.Property(x => x.ShortReason).HasMaxLength(300);

        // Quantities match the precision used by GRN and MIV lines elsewhere in the system.
        foreach (var qty in new[] { nameof(DeliveryOrderLine.QtyOrdered), nameof(DeliveryOrderLine.QtyPicked),
                                    nameof(DeliveryOrderLine.QtyPacked), nameof(DeliveryOrderLine.QtyShipped),
                                    nameof(DeliveryOrderLine.QtyDelivered), nameof(DeliveryOrderLine.QtyShort) })
            b.Property(qty).HasColumnType("decimal(18,3)");

        b.Property(x => x.UnitValue).HasColumnType("decimal(18,4)");

        // Cross-module lookups: "which delivery lines carry this variant".
        b.HasIndex(x => new { x.OrganizationId, x.VariantUuid });
    }
}
