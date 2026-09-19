using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Data.Maps;

internal sealed class CarrierInvoiceMap : IEntityTypeConfiguration<CarrierInvoice>
{
    public void Configure(EntityTypeBuilder<CarrierInvoice> b)
    {
        b.ToTable("carrier_invoices");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.InvoiceNumber).HasMaxLength(60).IsRequired();
        b.Property(x => x.CarrierName).HasMaxLength(100);
        b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.Source).HasMaxLength(20);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.CancelReason).HasMaxLength(500);

        b.Property(x => x.TotalAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.TaxAmount).HasColumnType("decimal(18,2)");

        // Querying it, and accepting it (T-56).
        b.Property(x => x.DisputeReason).HasMaxLength(1000);
        b.Property(x => x.DisputeReference).HasMaxLength(100);
        b.Property(x => x.DisputeOutcome).HasMaxLength(30);
        b.Property(x => x.DisputeResolutionNote).HasMaxLength(1000);
        b.Property(x => x.ApprovalNote).HasMaxLength(1000);
        b.Property(x => x.ExpectedCreditAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.ApprovedAmount).HasColumnType("decimal(18,2)");

        // One bill per number per carrier. Keying the same one twice is how it gets paid twice,
        // and the duplicate looks exactly as legitimate as the original. Filtered on IsDelete
        // because removal here is soft — a cancelled bill keeps its number reserved, which is
        // correct: the carrier's number has not been reissued.
        b.HasIndex(x => new { x.OrganizationId, x.CarrierId, x.InvoiceNumber })
         .IsUnique()
         .HasFilter("[IsDelete] = 0");

        // "Which bills are outstanding, by carrier" — what the settlement screen opens on.
        b.HasIndex(x => new { x.OrganizationId, x.Status, x.InvoiceDate });

        b.HasOne(x => x.Carrier)
         .WithMany()
         .HasForeignKey(x => x.CarrierId)
         .OnDelete(DeleteBehavior.Restrict);

        b.HasMany(x => x.Lines)
         .WithOne(x => x.CarrierInvoice)
         .HasForeignKey(x => x.CarrierInvoiceId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class CarrierInvoiceLineMap : IEntityTypeConfiguration<CarrierInvoiceLine>
{
    public void Configure(EntityTypeBuilder<CarrierInvoiceLine> b)
    {
        b.ToTable("carrier_invoice_lines");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.Description).HasMaxLength(300).IsRequired();
        b.Property(x => x.AwbNumber).HasMaxLength(100);
        b.Property(x => x.ConsignmentReference).HasMaxLength(40);
        b.Property(x => x.ChargeCode).HasMaxLength(30);
        b.Property(x => x.ServiceCode).HasMaxLength(50);

        b.Property(x => x.Amount).HasColumnType("decimal(18,2)");
        b.Property(x => x.ChargeableWeightKg).HasColumnType("decimal(18,3)");

        b.HasIndex(x => new { x.CarrierInvoiceId, x.LineNo }).IsUnique();

        // Matching (T-54) looks lines up by airway bill, which is the one reference a carrier
        // always prints and always knows.
        b.HasIndex(x => new { x.OrganizationId, x.AwbNumber });

        b.Property(x => x.MatchStatus).HasMaxLength(20).IsRequired();
        b.Property(x => x.MatchMethod).HasMaxLength(20);
        b.Property(x => x.MatchNote).HasMaxLength(500);

        // The unmatched queue is the only list that is worked daily.
        b.HasIndex(x => new { x.OrganizationId, x.MatchStatus });

        // Deliberately not unique: base carriage and fuel are two lines for one movement.
        b.HasOne(x => x.MatchedConsignment)
         .WithMany()
         .HasForeignKey(x => x.MatchedConsignmentId)
         .OnDelete(DeleteBehavior.Restrict);
    }
}
