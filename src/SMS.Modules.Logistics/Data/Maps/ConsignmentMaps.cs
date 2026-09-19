using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Data.Maps;

/// <summary>Itemised freight charges (T-47) — the base carriage, then each surcharge.</summary>
internal sealed class ConsignmentChargeMap : IEntityTypeConfiguration<ConsignmentCharge>
{
    public void Configure(EntityTypeBuilder<ConsignmentCharge> b)
    {
        b.ToTable("consignment_charges");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.Code).HasMaxLength(30).IsRequired();
        b.Property(x => x.Description).HasMaxLength(200);
        b.Property(x => x.Amount).HasColumnType("decimal(18,2)");

        // One line per charge code per consignment: a quote listing FUEL twice does not add up to
        // anything anybody can reconcile.
        b.HasIndex(x => new { x.ConsignmentId, x.Code }).IsUnique();
    }
}

internal sealed class ConsignmentMap : IEntityTypeConfiguration<Consignment>
{
    public void Configure(EntityTypeBuilder<Consignment> b)
    {
        b.ToTable("consignments");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.ConsignmentNumber).HasMaxLength(20).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.ConsignmentNumber }).IsUnique();

        b.Property(x => x.CarrierName).HasMaxLength(100);
        b.Property(x => x.CarrierServiceCode).HasMaxLength(50);
        b.Property(x => x.Mode).HasMaxLength(20).IsRequired();
        b.Property(x => x.MasterAwb).HasMaxLength(100);
        b.Property(x => x.CarrierReference).HasMaxLength(100);
        b.Property(x => x.FreightTerms).HasMaxLength(20).IsRequired();

        b.Property(x => x.CodAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.CodCurrency).HasMaxLength(3).IsFixedLength();

        // Rating (T-47, finding F37).
        b.Property(x => x.FreightCost).HasColumnType("decimal(18,2)");
        b.Property(x => x.FreightCurrency).HasMaxLength(3).IsFixedLength();
        b.Property(x => x.FreightRateSource).HasMaxLength(20);
        b.Property(x => x.FreightRateNote).HasMaxLength(500);
        b.Property(x => x.RatedChargeableWeightKg).HasColumnType("decimal(18,3)");
        b.Property(x => x.RatedServiceCode).HasMaxLength(50);

        b.HasMany(x => x.Charges)
         .WithOne(x => x.Consignment)
         .HasForeignKey(x => x.ConsignmentId)
         .OnDelete(DeleteBehavior.Cascade);

        // The public tracking page's address (T-62, decision G11). Unique across the whole table
        // rather than per organization: the lookup is anonymous and has no organization to scope
        // by, so two tenants holding the same token would make the page ambiguous.
        b.Property(x => x.TrackingToken).HasMaxLength(64);
        b.HasIndex(x => x.TrackingToken)
         .IsUnique()
         .HasFilter("[TrackingToken] IS NOT NULL");

        b.Property(x => x.VehicleNumber).HasMaxLength(30);
        b.Property(x => x.DriverName).HasMaxLength(100);
        b.Property(x => x.DriverPhone).HasMaxLength(30);

        b.Property(x => x.Status).HasMaxLength(30).IsRequired();
        b.Property(x => x.BookingIdempotencyKey).HasMaxLength(100);
        b.Property(x => x.BookingFailureReason).HasMaxLength(1000);
        b.Property(x => x.Notes).HasMaxLength(1000);

        b.Property(x => x.IsActive).HasDefaultValue(true);
        b.Property(x => x.RowVersion).IsRowVersion();

        // The ledger guard against double booking: one consignment can only ever hold one key,
        // so a retry that reuses it cannot produce a second row.
        b.HasIndex(x => new { x.OrganizationId, x.BookingIdempotencyKey })
         .IsUnique()
         .HasFilter("[BookingIdempotencyKey] IS NOT NULL");

        // Tracking board: live consignments by status.
        b.HasIndex(x => new { x.OrganizationId, x.Status });

        b.Property(x => x.TrackingLastError).HasMaxLength(500);
        b.Property(x => x.StuckReason).HasMaxLength(500);

        // The poll's question, across every organization: which live consignments are due?
        b.HasIndex(x => new { x.Status, x.TrackingNextPollAt });
        // The stuck list.
        b.HasIndex(x => new { x.OrganizationId, x.StuckSince });
        // Webhook and poll lookups arrive with nothing but an AWB.
        b.HasIndex(x => new { x.OrganizationId, x.MasterAwb });

        b.HasOne(x => x.Carrier)
         .WithMany()
         .HasForeignKey(x => x.CarrierId)
         .OnDelete(DeleteBehavior.SetNull);

        // Restrict: an account a consignment was booked on is part of that booking's record.
        b.HasOne(x => x.CarrierAccount)
         .WithMany()
         .HasForeignKey(x => x.CarrierAccountId)
         .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(x => x.ShipFromAddress)
         .WithMany()
         .HasForeignKey(x => x.ShipFromAddressId)
         .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(x => x.ShipToAddress)
         .WithMany()
         .HasForeignKey(x => x.ShipToAddressId)
         .OnDelete(DeleteBehavior.Restrict);

        b.HasMany(x => x.Stops)
         .WithOne(x => x.Consignment)
         .HasForeignKey(x => x.ConsignmentId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class ConsignmentDeliveryMap : IEntityTypeConfiguration<ConsignmentDelivery>
{
    public void Configure(EntityTypeBuilder<ConsignmentDelivery> b)
    {
        b.ToTable("consignment_deliveries");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        // A delivery can ride on many consignments and a consignment can carry many deliveries,
        // but the same pair exactly once — a duplicate would double-count the load.
        b.HasIndex(x => new { x.ConsignmentId, x.DeliveryOrderId }).IsUnique();

        b.HasOne(x => x.Consignment)
         .WithMany(x => x.Deliveries)
         .HasForeignKey(x => x.ConsignmentId)
         .OnDelete(DeleteBehavior.Cascade);

        // Restrict: deleting a delivery must not silently strip it off a consignment the carrier
        // has already been told about. Detaching it is an explicit action, not a side effect.
        b.HasOne(x => x.DeliveryOrder)
         .WithMany(x => x.Consignments)
         .HasForeignKey(x => x.DeliveryOrderId)
         .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(x => x.ConsignmentStop)
         .WithMany(x => x.Deliveries)
         .HasForeignKey(x => x.ConsignmentStopId)
         .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class ConsignmentStopMap : IEntityTypeConfiguration<ConsignmentStop>
{
    public void Configure(EntityTypeBuilder<ConsignmentStop> b)
    {
        b.ToTable("consignment_stops");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.HasIndex(x => new { x.ConsignmentId, x.Sequence }).IsUnique();

        b.Property(x => x.StopType).HasMaxLength(20).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(500);

        b.HasOne(x => x.Address)
         .WithMany()
         .HasForeignKey(x => x.AddressId)
         .OnDelete(DeleteBehavior.Restrict);
    }
}
