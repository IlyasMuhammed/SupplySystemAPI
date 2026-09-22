using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Finance.Domain;

namespace SMS.Modules.Finance.Data.Maps;

// A29-P7-01 §9.1/§9.2/§17.2.

internal sealed class SalesInvoiceMap : IEntityTypeConfiguration<SalesInvoice>
{
    public void Configure(EntityTypeBuilder<SalesInvoice> b)
    {
        b.ToTable("sales_invoices");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.TraceId).IsRequired();
        b.HasIndex(x => x.TraceId);

        b.Property(x => x.InvoiceNumber).HasMaxLength(25).IsRequired();
        // Composite, not global — each org generates its own sequence, as every Finance document does.
        b.HasIndex(x => new { x.OrganizationId, x.InvoiceNumber }).IsUnique();

        b.Property(x => x.SaleOrderNumber).HasMaxLength(25).IsRequired();
        // §9.5 allows several invoices per order; this is the "invoices of this order" lookup.
        b.HasIndex(x => new { x.OrganizationId, x.SaleOrderUuid });

        b.Property(x => x.PartnerName).HasMaxLength(200).IsRequired();

        // A29-P7-04 — the guard behind "one invoice per delivery": a cancelled or deleted invoice
        // frees its delivery to be invoiced again, so the filter excludes both.
        b.Property(x => x.DeliveryNumber).HasMaxLength(25);
        b.HasIndex(x => new { x.OrganizationId, x.DeliveryUuid })
         .IsUnique()
         .HasFilter($"[DeliveryUuid] IS NOT NULL AND [IsDelete] = 0 AND [Status] <> '{SalesInvoiceStatuses.Cancelled}'");

        b.Property(x => x.Subtotal).HasColumnType("decimal(18,2)");
        b.Property(x => x.TaxAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.DiscountAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.GrandTotal).HasColumnType("decimal(18,2)");
        b.Property(x => x.AmountPaid).HasColumnType("decimal(18,2)").HasDefaultValue(0m);
        b.Property(x => x.BalanceDue).HasColumnType("decimal(18,2)");

        b.Property(x => x.Status).HasMaxLength(20).IsRequired().HasDefaultValue(SalesInvoiceStatuses.Draft);
        b.Property(x => x.CurrencyCode).HasMaxLength(10).IsRequired().HasDefaultValue("PKR");
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.IsActive).HasDefaultValue(true);

        // Optimistic concurrency without a new column: every writer that changes an invoice sets
        // ModifiedDate to a fresh value, so a save made from a stale read matches no row and fails
        // rather than overwriting. Payments, allocations, issue, the overdue sweep and edits all
        // change balances or status and all bump it — a writer that forgot to would be the one gap.
        b.Property(x => x.ModifiedDate).IsConcurrencyToken();

        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);

        // §17.2 — the customer statement and the aging report.
        b.HasIndex(x => new { x.OrganizationId, x.PartnerId, x.Status });
        b.HasIndex(x => new { x.OrganizationId, x.DueDate, x.BalanceDue });

        b.HasMany(x => x.Lines)
         .WithOne(x => x.SalesInvoice)
         .HasForeignKey(x => x.SalesInvoiceId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SalesInvoiceLineMap : IEntityTypeConfiguration<SalesInvoiceLine>
{
    public void Configure(EntityTypeBuilder<SalesInvoiceLine> b)
    {
        b.ToTable("sales_invoice_lines");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.HasIndex(x => new { x.SalesInvoiceId, x.LineNo }).IsUnique();
        // "How much of this order line is already invoiced" — the §9.5 ceiling check.
        b.HasIndex(x => x.SoLineUuid);

        b.Property(x => x.Description).HasMaxLength(300).IsRequired();
        b.Property(x => x.Quantity).HasColumnType("decimal(18,4)");
        b.Property(x => x.UnitPrice).HasColumnType("decimal(18,2)");
        b.Property(x => x.DiscountPercent).HasColumnType("decimal(5,2)");
        b.Property(x => x.TaxPercent).HasColumnType("decimal(5,2)");
        b.Property(x => x.LineTotal).HasColumnType("decimal(18,2)");

        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);
    }
}
