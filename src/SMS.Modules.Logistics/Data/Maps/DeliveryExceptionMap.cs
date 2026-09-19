using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Data.Maps;

internal sealed class DeliveryExceptionMap : IEntityTypeConfiguration<DeliveryException>
{
    public void Configure(EntityTypeBuilder<DeliveryException> b)
    {
        b.ToTable("delivery_exceptions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.ExceptionType).HasMaxLength(30).IsRequired();
        b.Property(x => x.Severity).HasMaxLength(20).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.Source).HasMaxLength(20).IsRequired();
        b.Property(x => x.CarrierName).HasMaxLength(100);
        b.Property(x => x.Description).HasMaxLength(1000).IsRequired();
        b.Property(x => x.Resolution).HasMaxLength(1000);

        // "What is still open, worst first" — the only list anybody works.
        b.HasIndex(x => new { x.OrganizationId, x.Status, x.Severity });

        // Raised from a carrier event at most once. A carrier resending the same scan must not
        // open a second piece of work for one problem — webhooks are retried, and T-39 already
        // proved that the hard way.
        b.HasIndex(x => x.TrackingEventId)
         .IsUnique()
         .HasFilter("[TrackingEventId] IS NOT NULL AND [IsDelete] = 0");

        b.HasOne(x => x.Consignment)
         .WithMany()
         .HasForeignKey(x => x.ConsignmentId)
         .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(x => x.Carrier)
         .WithMany()
         .HasForeignKey(x => x.CarrierId)
         .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(x => x.TrackingEvent)
         .WithMany()
         .HasForeignKey(x => x.TrackingEventId)
         .OnDelete(DeleteBehavior.SetNull);
    }
}
