using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Finance.Domain;

namespace SMS.Modules.Finance.Data.Maps;

// A29-P7-03 §10/§17.2 — mirrors SupplierLedgerEntryMap.
internal sealed class CustomerLedgerEntryMap : IEntityTypeConfiguration<CustomerLedgerEntry>
{
    public void Configure(EntityTypeBuilder<CustomerLedgerEntry> b)
    {
        b.ToTable("customer_ledger");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.PartnerId).IsRequired();

        // The concurrency guard for the ledger writer — see the note on SequenceNo. Organization
        // first, like every tenant-filtered lookup here, so it also serves as the tenant index.
        b.HasIndex(x => new { x.OrganizationId, x.PartnerId, x.SequenceNo }).IsUnique();

        // §17.2 — a customer's statement, in date order.
        b.HasIndex(x => new { x.OrganizationId, x.PartnerId, x.EntryDate });

        b.Property(x => x.EntryType).HasMaxLength(20).IsRequired();
        b.Property(x => x.ReferenceType).HasMaxLength(30).IsRequired();
        b.Property(x => x.ReferenceNumber).HasMaxLength(30).IsRequired();

        b.Property(x => x.DebitAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.CreditAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.RunningBalance).HasColumnType("decimal(18,2)");

        b.Property(x => x.CurrencyCode).HasMaxLength(10).IsRequired().HasDefaultValue("PKR");
        b.Property(x => x.Narration).HasMaxLength(500);

        b.Property(x => x.OrganizationId).IsRequired();
    }
}
