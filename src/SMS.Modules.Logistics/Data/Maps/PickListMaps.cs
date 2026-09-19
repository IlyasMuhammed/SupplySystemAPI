using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Data.Maps;

internal sealed class PickListMap : IEntityTypeConfiguration<PickList>
{
    public void Configure(EntityTypeBuilder<PickList> b)
    {
        b.ToTable("pick_lists");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.PickListNumber).HasMaxLength(20).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.PickListNumber }).IsUnique();

        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.WarehouseName).HasMaxLength(200);
        b.Property(x => x.CancelReason).HasMaxLength(500);
        b.Property(x => x.Notes).HasMaxLength(1000);

        b.Property(x => x.IsActive).HasDefaultValue(true);

        // "What is open in my warehouse" — the picking screen's only query.
        b.HasIndex(x => new { x.OrganizationId, x.WarehouseUuid, x.Status });
        b.HasIndex(x => x.DeliveryOrderId);

        b.HasOne(x => x.DeliveryOrder)
         .WithMany()
         .HasForeignKey(x => x.DeliveryOrderId)
         .OnDelete(DeleteBehavior.Cascade);

        b.HasMany(x => x.Lines)
         .WithOne(x => x.PickList)
         .HasForeignKey(x => x.PickListId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PickListLineMap : IEntityTypeConfiguration<PickListLine>
{
    public void Configure(EntityTypeBuilder<PickListLine> b)
    {
        b.ToTable("pick_list_lines");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.HasIndex(x => new { x.PickListId, x.SeqNo }).IsUnique();

        // One reservation row produces exactly one pick instruction. A unique index rather than a
        // comment: two instructions against one hold would let the same units be picked twice.
        b.HasIndex(x => new { x.PickListId, x.ReservationUuid }).IsUnique();

        b.Property(x => x.ItemDescription).HasMaxLength(300).IsRequired();
        b.Property(x => x.UnitOfMeasure).HasMaxLength(20);
        b.Property(x => x.ZoneName).HasMaxLength(100);
        b.Property(x => x.BinCode).HasMaxLength(50);
        b.Property(x => x.BatchNumber).HasMaxLength(50);
        b.Property(x => x.SerialNumber).HasMaxLength(100);
        b.Property(x => x.ShortReasonCode).HasMaxLength(30);
        b.Property(x => x.ShortReason).HasMaxLength(300);

        foreach (var qty in new[] { nameof(PickListLine.QtyToPick), nameof(PickListLine.QtyPicked),
                                    nameof(PickListLine.QtyShort) })
            b.Property(qty).HasColumnType("decimal(18,3)");

        // Deleting a delivery line out from under an instruction to pick it would leave the
        // picker holding paper for something that no longer exists.
        b.HasOne(x => x.DeliveryOrderLine)
         .WithMany()
         .HasForeignKey(x => x.DeliveryOrderLineId)
         .OnDelete(DeleteBehavior.Restrict);
    }
}
