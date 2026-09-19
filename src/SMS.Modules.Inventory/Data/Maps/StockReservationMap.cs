using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Inventory.Domain;

namespace SMS.Modules.Inventory.Data.Maps;

internal sealed class StockReservationMap : IEntityTypeConfiguration<StockReservation>
{
    public void Configure(EntityTypeBuilder<StockReservation> b)
    {
        b.ToTable("StockReservations");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityColumn();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.ReservedQty).HasColumnType("decimal(18,4)");
        b.Property(x => x.SourceType).HasMaxLength(30).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.ReleaseReason).HasMaxLength(500);

        b.Property(x => x.IsFlagged);
        b.Property(x => x.FlaggedAt);

        b.Property(x => x.OrganizationId).IsRequired();

        // The lookup every consumer makes: "what does this document hold?" — for release, for
        // consume, and for the reconciliation that compares held quantities against the counter.
        b.HasIndex(x => new { x.OrganizationId, x.SourceType, x.SourceUuid });

        // "What is holding this stock row?" — the other direction, used when reconciling a single
        // inventory item.
        b.HasIndex(x => new { x.InventoryItemId, x.Status });

        // Restrict: an inventory row that something is still holding must not vanish underneath
        // it, or the counter would be left asserting a hold with no record of who placed it.
        b.HasOne(x => x.InventoryItem)
         .WithMany()
         .HasForeignKey(x => x.InventoryItemId)
         .OnDelete(DeleteBehavior.Restrict);
    }
}
