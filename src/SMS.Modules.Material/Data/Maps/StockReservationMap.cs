using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Material.Domain;

namespace SMS.Modules.Material.Data.Maps;

internal sealed class StockReservationMap : IEntityTypeConfiguration<StockReservation>
{
    public void Configure(EntityTypeBuilder<StockReservation> b)
    {
        b.ToTable("stock_reservations");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.MirId).IsRequired();
        b.Property(x => x.MirLineId).IsRequired();
        b.Property(x => x.InventoryItemId).IsRequired();
        b.Property(x => x.VariantUuid).IsRequired();
        b.Property(x => x.WarehouseId).IsRequired();
        b.Property(x => x.ReservedQty).HasColumnType("decimal(18,4)").IsRequired();
        b.Property(x => x.Status).HasMaxLength(30).IsRequired();
        b.Property(x => x.ReservedAt).IsRequired();
        b.Property(x => x.ReleasedAt);
        b.Property(x => x.ReleaseReason).HasMaxLength(500);
        b.Property(x => x.IsFlagged);
        b.Property(x => x.FlaggedAt);
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);

        b.HasOne(x => x.MaterialIssueRequest)
         .WithMany()
         .HasForeignKey(x => x.MirId)
         .OnDelete(DeleteBehavior.Cascade);

        b.HasOne(x => x.MaterialIssueRequestDetail)
         .WithMany()
         .HasForeignKey(x => x.MirLineId)
         .OnDelete(DeleteBehavior.NoAction);

        b.HasIndex(x => new { x.MirId, x.Status });
        b.HasIndex(x => new { x.InventoryItemId, x.Status });
    }
}
