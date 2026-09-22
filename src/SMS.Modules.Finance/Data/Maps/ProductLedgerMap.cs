using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Finance.Domain;

namespace SMS.Modules.Finance.Data.Maps;

// A29-P8-01 §11/§17.2 — the per-variant product ledger, in the shape of CustomerLedgerEntryMap.
internal sealed class ProductLedgerEntryMap : IEntityTypeConfiguration<ProductLedgerEntry>
{
    public void Configure(EntityTypeBuilder<ProductLedgerEntry> b)
    {
        b.ToTable("product_ledger");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.VariantUuid).IsRequired();
        b.Property(x => x.ProductUuid).IsRequired();

        // The concurrency guard for the ledger writer — see the note on SequenceNo. Organization
        // first, like every tenant-filtered lookup here, so it also serves as the tenant index.
        b.HasIndex(x => new { x.OrganizationId, x.VariantUuid, x.SequenceNo }).IsUnique();

        // §17.2 — a variant's history and a product's history, each in date order.
        b.HasIndex(x => new { x.OrganizationId, x.VariantUuid, x.EntryDate });
        b.HasIndex(x => new { x.OrganizationId, x.ProductUuid, x.EntryDate });

        b.Property(x => x.EntryType).HasMaxLength(20).IsRequired();
        b.Property(x => x.Direction).HasMaxLength(3).IsRequired();
        b.Property(x => x.ReferenceType).HasMaxLength(30).IsRequired();
        b.Property(x => x.ReferenceNumber).HasMaxLength(50).IsRequired();

        b.Property(x => x.Quantity).HasColumnType("decimal(18,4)");
        b.Property(x => x.UnitCost).HasColumnType("decimal(18,4)");
        b.Property(x => x.TotalCost).HasColumnType("decimal(18,2)");
        b.Property(x => x.RunningQty).HasColumnType("decimal(18,4)");
        b.Property(x => x.RunningValue).HasColumnType("decimal(18,2)");

        b.Property(x => x.Narration).HasMaxLength(500);

        b.Property(x => x.OrganizationId).IsRequired();
    }
}
