using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Data.Maps;

internal sealed class CarrierCommandMap : IEntityTypeConfiguration<CarrierCommand>
{
    // Carrier-supplied text is truncated to these on the way in (CarrierCommandLedger), because a
    // save that fails on an over-long message right after a real booking loses the only record of it.
    internal const int AwbNumberMax        = 100;
    internal const int CarrierReferenceMax = 100;
    internal const int TrackingUrlMax      = 500;
    internal const int CarrierErrorCodeMax = 100;
    internal const int MessageMax          = 1000;
    internal const int FailureDetailMax    = 1000;
    internal const int ResolutionNoteMax   = 1000;
    internal const int RawResponseMax      = 100_000;

    public void Configure(EntityTypeBuilder<CarrierCommand> b)
    {
        b.ToTable("carrier_commands");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.CommandType).HasMaxLength(20).IsRequired();
        b.Property(x => x.IdempotencyKey).HasMaxLength(100).IsRequired();
        b.Property(x => x.RequestFingerprint).HasMaxLength(64).IsFixedLength().IsRequired();

        // The guarantee itself. Two workers racing to claim one key cannot both insert; the loser
        // rereads and finds the call already in flight.
        b.HasIndex(x => new { x.OrganizationId, x.CommandType, x.IdempotencyKey }).IsUnique();

        b.Property(x => x.ProviderKey).HasMaxLength(50).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();

        b.Property(x => x.AwbNumber).HasMaxLength(AwbNumberMax);
        b.Property(x => x.CarrierReference).HasMaxLength(CarrierReferenceMax);
        b.Property(x => x.TrackingUrl).HasMaxLength(TrackingUrlMax);
        b.Property(x => x.Cost).HasColumnType("decimal(18,2)");
        b.Property(x => x.CostCurrency).HasMaxLength(3).IsFixedLength();
        b.Property(x => x.CarrierErrorCode).HasMaxLength(CarrierErrorCodeMax);
        b.Property(x => x.Message).HasMaxLength(MessageMax);
        b.Property(x => x.RawResponse);
        b.Property(x => x.FailureDetail).HasMaxLength(FailureDetailMax);
        b.Property(x => x.ResolutionNote).HasMaxLength(ResolutionNoteMax);

        b.Property(x => x.RowVersion).IsRowVersion();

        // The exception queue and the stale-lease sweep: what is unresolved or overdue.
        b.HasIndex(x => new { x.OrganizationId, x.Status });
        // "Every call ever made for this consignment" — the dispute view.
        b.HasIndex(x => x.ConsignmentId);

        // Restrict on both: the ledger is the record of what a carrier was asked to do, and it
        // must outlive any attempt to delete what it refers to.
        b.HasOne(x => x.Consignment)
         .WithMany()
         .HasForeignKey(x => x.ConsignmentId)
         .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(x => x.CarrierAccount)
         .WithMany()
         .HasForeignKey(x => x.CarrierAccountId)
         .OnDelete(DeleteBehavior.Restrict);
    }
}
