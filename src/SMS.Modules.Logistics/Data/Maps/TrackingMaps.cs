using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Data.Maps;

internal sealed class ConsignmentTrackingEventMap : IEntityTypeConfiguration<ConsignmentTrackingEvent>
{
    internal const int CarrierStatusMax = 100;
    internal const int DescriptionMax   = 500;
    internal const int LocationMax      = 200;
    internal const int SignedByMax      = 200;

    public void Configure(EntityTypeBuilder<ConsignmentTrackingEvent> b)
    {
        b.ToTable("consignment_tracking_events");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.Milestone).HasMaxLength(30).IsRequired();
        b.Property(x => x.CarrierStatus).HasMaxLength(CarrierStatusMax);
        b.Property(x => x.Description).HasMaxLength(DescriptionMax);
        b.Property(x => x.Location).HasMaxLength(LocationMax);
        b.Property(x => x.SignedBy).HasMaxLength(SignedByMax);
        b.Property(x => x.Source).HasMaxLength(20).IsRequired();
        b.Property(x => x.EventKey).HasMaxLength(64).IsFixedLength().IsRequired();
        b.Property(x => x.AppliedStatus).HasMaxLength(30);

        // One event, however many times and however many ways it arrives.
        b.HasIndex(x => new { x.ConsignmentId, x.EventKey }).IsUnique();
        // The timeline.
        b.HasIndex(x => new { x.ConsignmentId, x.OccurredAt });

        b.HasOne(x => x.Consignment)
         .WithMany()
         .HasForeignKey(x => x.ConsignmentId)
         .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class CarrierWebhookDeliveryMap : IEntityTypeConfiguration<CarrierWebhookDelivery>
{
    internal const int DedupeKeyMax = 100;
    internal const int DetailMax    = 1000;

    public void Configure(EntityTypeBuilder<CarrierWebhookDelivery> b)
    {
        b.ToTable("carrier_webhook_deliveries");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.ProviderKey).HasMaxLength(50).IsRequired();
        b.Property(x => x.DedupeKey).HasMaxLength(DedupeKeyMax).IsRequired();
        b.Property(x => x.BodySha256).HasMaxLength(64).IsFixedLength().IsRequired();
        b.Property(x => x.Body).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.Detail).HasMaxLength(DetailMax);
        b.Property(x => x.RowVersion).IsRowVersion();

        // A resent delivery finds its first arrival here instead of being applied twice.
        b.HasIndex(x => new { x.CarrierAccountId, x.DedupeKey }).IsUnique();
        b.HasIndex(x => new { x.OrganizationId, x.Status });

        b.HasOne(x => x.CarrierAccount)
         .WithMany()
         .HasForeignKey(x => x.CarrierAccountId)
         .OnDelete(DeleteBehavior.Restrict);
    }
}
