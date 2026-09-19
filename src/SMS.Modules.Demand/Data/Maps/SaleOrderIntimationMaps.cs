using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Demand.Domain;

namespace SMS.Modules.Demand.Data.Maps;

internal sealed class SaleOrderIntimationMap : IEntityTypeConfiguration<SaleOrderIntimation>
{
    public void Configure(EntityTypeBuilder<SaleOrderIntimation> b)
    {
        b.ToTable("sale_order_intimations");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.OrganizationId).IsRequired();
        b.Property(x => x.EventType).HasMaxLength(20).IsRequired();
        b.Property(x => x.Recipients).HasMaxLength(1000).IsRequired();
        b.Property(x => x.Subject).HasMaxLength(300).IsRequired();
        b.Property(x => x.BodyHtml).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.ErrorMessage).HasMaxLength(2000);
        b.Property(x => x.HangfireJobId).HasMaxLength(50);

        // Lookups this table actually serves: a sale order's own intimation history, and finding
        // what a QUEUED/FAILED sweep still needs to act on.
        b.HasIndex(x => x.SaleOrderId);
        b.HasIndex(x => new { x.OrganizationId, x.Status });

        b.HasOne(x => x.SaleOrder)
         .WithMany()
         .HasForeignKey(x => x.SaleOrderId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}
