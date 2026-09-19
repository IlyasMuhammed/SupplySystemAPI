using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Logistics.Domain;

namespace SMS.Modules.Logistics.Data.Maps;

internal sealed class FreightAccrualMap : IEntityTypeConfiguration<FreightAccrual>
{
    public void Configure(EntityTypeBuilder<FreightAccrual> b)
    {
        b.ToTable("freight_accruals");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.CarrierName).HasMaxLength(100);
        b.Property(x => x.Currency).HasMaxLength(3).IsFixedLength().IsRequired();
        b.Property(x => x.BookedCurrency).HasMaxLength(3).IsFixedLength();
        b.Property(x => x.QuoteSource).HasMaxLength(20);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.ReleaseReason).HasMaxLength(500);

        b.Property(x => x.AccruedAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.BookedAmount).HasColumnType("decimal(18,2)");

        // The third leg (T-55), beside the other two.
        b.Property(x => x.InvoicedAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.VarianceAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.VarianceReason).HasMaxLength(20);
        b.Property(x => x.VarianceNote).HasMaxLength(500);

        b.HasOne(x => x.CarrierInvoice)
         .WithMany()
         .HasForeignKey(x => x.CarrierInvoiceId)
         .OnDelete(DeleteBehavior.Restrict);

        // One accrual per consignment. Two would double the liability, and the second would look
        // exactly as legitimate as the first. Filtered on IsDelete because removal here is soft.
        b.HasIndex(x => x.ConsignmentId).IsUnique().HasFilter("[IsDelete] = 0");

        // "What is still owed, by carrier" — the only read that matters, and the one a month-end
        // report runs.
        b.HasIndex(x => new { x.OrganizationId, x.Status, x.CarrierId });

        b.HasOne(x => x.Consignment)
         .WithMany()
         .HasForeignKey(x => x.ConsignmentId)
         .OnDelete(DeleteBehavior.Restrict);

        b.HasOne(x => x.Carrier)
         .WithMany()
         .HasForeignKey(x => x.CarrierId)
         .OnDelete(DeleteBehavior.Restrict);
    }
}
