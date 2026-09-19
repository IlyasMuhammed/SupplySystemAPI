using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Data.Maps;

internal sealed class DeliveryProofMap : IEntityTypeConfiguration<DeliveryProof>
{
    internal const int ReceivedByMax   = 200;
    internal const int RelationshipMax = 100;
    internal const int LocationMax     = 200;
    internal const int NotesMax        = 1000;

    public void Configure(EntityTypeBuilder<DeliveryProof> b)
    {
        b.ToTable("delivery_proofs");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.ReceivedBy).HasMaxLength(ReceivedByMax);
        b.Property(x => x.Relationship).HasMaxLength(RelationshipMax);
        b.Property(x => x.Location).HasMaxLength(LocationMax);
        b.Property(x => x.Notes).HasMaxLength(NotesMax);
        b.Property(x => x.Source).HasMaxLength(20).IsRequired();

        // One proof per drop. A courier parcel has one signature and a multi-stop run has one per
        // stop; SQL Server treats NULLs as equal in a unique index, so the whole-consignment case
        // is covered by the same constraint rather than needing a second one.
        b.HasIndex(x => new { x.ConsignmentId, x.ConsignmentStopId })
         .IsUnique()
         .HasFilter("[IsDelete] = 0");

        // A resent DELIVERED scan must not produce a second proof — the same guard T-39 needed and
        // T-60 repeated.
        b.HasIndex(x => x.TrackingEventId)
         .IsUnique()
         .HasFilter("[TrackingEventId] IS NOT NULL AND [IsDelete] = 0");

        b.HasOne(x => x.Consignment)
         .WithMany()
         .HasForeignKey(x => x.ConsignmentId)
         .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(x => x.ConsignmentStop)
         .WithMany()
         .HasForeignKey(x => x.ConsignmentStopId)
         .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(x => x.TrackingEvent)
         .WithMany()
         .HasForeignKey(x => x.TrackingEventId)
         .OnDelete(DeleteBehavior.SetNull);

        b.HasMany(x => x.Files)
         .WithOne(f => f.DeliveryProof)
         .HasForeignKey(f => f.DeliveryProofId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class DeliveryProofFileMap : IEntityTypeConfiguration<DeliveryProofFile>
{
    public void Configure(EntityTypeBuilder<DeliveryProofFile> b)
    {
        b.ToTable("delivery_proof_files");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.Kind).HasMaxLength(20).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
        b.Property(x => x.FileName).HasMaxLength(255).IsRequired();
        b.Property(x => x.Content).IsRequired();
        b.Property(x => x.Sha256).HasMaxLength(64).IsFixedLength().IsRequired();

        // The same artefact uploaded twice is stored once — phones retry uploads on a bad signal,
        // and a proof carrying the same photograph four times is a proof nobody trusts.
        b.HasIndex(x => new { x.DeliveryProofId, x.Sha256 })
         .IsUnique()
         .HasFilter("[IsDelete] = 0");
    }
}
