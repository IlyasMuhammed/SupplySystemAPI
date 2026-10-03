using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Demand.Domain;

namespace SMS.Modules.Demand.Data.Maps;

// A32 (Sales Pre-Order Pipeline) — PA-02/PB-02/PC-02. Table names snake_case like sale_orders, columns PascalCase.
// Lines cascade from their header; every other Demand-internal FK is NoAction (SQL Server refuses a second cascade
// path, and a header must never take a quotation, order or reason down with it). Status columns of the two new
// documents are concurrency tokens: a transition that lost a race fails its save (409 via the global middleware).
// Date-only columns are SQL `date` (the spec's DATE): received/deadline/validity/delivery/response dates.

internal sealed class SaleInquiryMap : IEntityTypeConfiguration<SaleInquiry>
{
    public void Configure(EntityTypeBuilder<SaleInquiry> b)
    {
        b.ToTable("sale_inquiries");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.TraceId).IsRequired().ValueGeneratedOnAdd().HasDefaultValueSql("NEWSEQUENTIALID()");
        b.HasIndex(x => x.TraceId);

        b.Property(x => x.OrganizationId).IsRequired();
        b.Property(x => x.InquiryNumber).HasMaxLength(20).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.InquiryNumber }).IsUnique();

        b.Property(x => x.CustomerReference).HasMaxLength(50);
        b.Property(x => x.CustomerReferenceDate).HasColumnType("date");
        b.Property(x => x.Status).HasMaxLength(20).IsRequired().IsConcurrencyToken();
        b.Property(x => x.ReceivedDate).HasColumnType("date");
        b.Property(x => x.ResponseDeadline).HasColumnType("date");
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.Property(x => x.DeclineReason).HasMaxLength(500);

        b.HasIndex(x => new { x.OrganizationId, x.Status });
        b.HasIndex(x => x.PartnerId);
        b.HasIndex(x => x.AssignedToUserId).HasFilter("[AssignedToUserId] IS NOT NULL");

        b.HasMany(x => x.Lines)
         .WithOne(x => x.SaleInquiry)
         .HasForeignKey(x => x.SaleInquiryId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SaleInquiryLineMap : IEntityTypeConfiguration<SaleInquiryLine>
{
    public void Configure(EntityTypeBuilder<SaleInquiryLine> b)
    {
        b.ToTable("sale_inquiry_lines");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.OrganizationId).IsRequired();

        b.HasIndex(x => new { x.SaleInquiryId, x.LineNumber }).IsUnique();

        b.Property(x => x.ProductDescription).HasMaxLength(500).IsRequired();
        b.Property(x => x.RequestedQuantity).HasColumnType("decimal(18,4)");
        b.Property(x => x.RequestedUomCode).HasMaxLength(20);
        b.Property(x => x.RequestedDeliveryDate).HasColumnType("date");
        b.Property(x => x.LineStatus).HasMaxLength(20).IsRequired();
        b.Property(x => x.CanSupplyQuantity).HasColumnType("decimal(18,4)");
        b.Property(x => x.EstimatedDeliveryDate).HasColumnType("date");
        b.Property(x => x.RejectionNotes).HasMaxLength(500);
        b.Property(x => x.AlternativeNotes).HasMaxLength(500);
        b.Property(x => x.RequiresProcurement).HasDefaultValue(false);
        b.Property(x => x.Notes).HasMaxLength(1000);

        b.HasIndex(x => x.ProductUuid).HasFilter("[ProductUuid] IS NOT NULL");

        b.HasOne(x => x.RejectionReason)
         .WithMany()
         .HasForeignKey(x => x.RejectionReasonId)
         .OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(x => x.RejectionReasonId).HasFilter("[RejectionReasonId] IS NOT NULL");
    }
}

internal sealed class SaleQuotationMap : IEntityTypeConfiguration<SaleQuotation>
{
    public void Configure(EntityTypeBuilder<SaleQuotation> b)
    {
        b.ToTable("sale_quotations");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.TraceId).IsRequired().ValueGeneratedOnAdd().HasDefaultValueSql("NEWSEQUENTIALID()");
        b.HasIndex(x => x.TraceId);

        b.Property(x => x.OrganizationId).IsRequired();
        b.Property(x => x.QuotationNumber).HasMaxLength(20).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.QuotationNumber }).IsUnique();

        b.Property(x => x.CustomerReference).HasMaxLength(50);
        b.Property(x => x.CustomerReferenceDate).HasColumnType("date");
        b.Property(x => x.ValidFrom).HasColumnType("date");
        b.Property(x => x.ValidTo).HasColumnType("date");
        b.Property(x => x.Status).HasMaxLength(20).IsRequired().IsConcurrencyToken();
        b.Property(x => x.PaymentTerms).HasMaxLength(200);
        b.Property(x => x.DeliveryTerms).HasMaxLength(200);
        b.Property(x => x.Subtotal).HasColumnType("decimal(18,2)");
        b.Property(x => x.TaxAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.DiscountAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.GrandTotal).HasColumnType("decimal(18,2)");
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.Property(x => x.InternalNotes).HasMaxLength(2000);

        b.HasIndex(x => new { x.OrganizationId, x.Status });
        b.HasIndex(x => x.PartnerId);
        // The expiry job's scan: SENT quotations past their valid_to, per organization.
        b.HasIndex(x => new { x.OrganizationId, x.ValidTo });

        b.HasOne(x => x.SourceInquiry)
         .WithMany()
         .HasForeignKey(x => x.SourceInquiryId)
         .OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(x => x.SourceInquiryId).HasFilter("[SourceInquiryId] IS NOT NULL");

        b.HasMany(x => x.Lines)
         .WithOne(x => x.SaleQuotation)
         .HasForeignKey(x => x.SaleQuotationId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SaleQuotationLineMap : IEntityTypeConfiguration<SaleQuotationLine>
{
    public void Configure(EntityTypeBuilder<SaleQuotationLine> b)
    {
        b.ToTable("sale_quotation_lines");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.OrganizationId).IsRequired();

        b.HasIndex(x => new { x.SaleQuotationId, x.LineNumber }).IsUnique();
        b.HasIndex(x => new { x.SaleQuotationId, x.CustomerResponse });

        b.Property(x => x.ProductDescription).HasMaxLength(500).IsRequired();
        b.Property(x => x.Quantity).HasColumnType("decimal(18,4)");
        b.Property(x => x.UomCode).HasMaxLength(20);
        b.Property(x => x.UnitPrice).HasColumnType("decimal(18,4)");
        b.Property(x => x.DiscountPercent).HasColumnType("decimal(5,2)");
        b.Property(x => x.TaxPercent).HasColumnType("decimal(5,2)");
        b.Property(x => x.TaxCode).HasMaxLength(20);
        b.Property(x => x.TaxAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.LineTotal).HasColumnType("decimal(18,2)");
        b.Property(x => x.PromisedDeliveryDate).HasColumnType("date");
        b.Property(x => x.LineType).HasMaxLength(15).IsRequired();
        b.Property(x => x.RejectionNotes).HasMaxLength(500);
        b.Property(x => x.AlternativeNotes).HasMaxLength(500);
        b.Property(x => x.CustomerResponse).HasMaxLength(15).IsRequired();
        b.Property(x => x.CustomerResponseDate).HasColumnType("date");
        b.Property(x => x.CustomerResponseNotes).HasMaxLength(500);
        b.Property(x => x.CustomerCounterPrice).HasColumnType("decimal(18,4)");
        b.Property(x => x.Notes).HasMaxLength(1000);

        b.HasIndex(x => x.VariantUuid);

        b.HasOne(x => x.SourceInquiryLine)
         .WithMany()
         .HasForeignKey(x => x.SourceInquiryLineId)
         .OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(x => x.SourceInquiryLineId).HasFilter("[SourceInquiryLineId] IS NOT NULL");

        b.HasOne(x => x.RejectionReason)
         .WithMany()
         .HasForeignKey(x => x.RejectionReasonId)
         .OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(x => x.RejectionReasonId).HasFilter("[RejectionReasonId] IS NOT NULL");

        b.HasOne(x => x.AlternativeForLine)
         .WithMany()
         .HasForeignKey(x => x.AlternativeForLineId)
         .OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(x => x.AlternativeForLineId).HasFilter("[AlternativeForLineId] IS NOT NULL");
    }
}

internal sealed class RejectionReasonMap : IEntityTypeConfiguration<RejectionReason>
{
    public void Configure(EntityTypeBuilder<RejectionReason> b)
    {
        b.ToTable("rejection_reasons");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.OrganizationId).IsRequired();

        b.Property(x => x.Code).HasMaxLength(10).IsRequired();
        // BR-C5-01 — unique per organization. SQL Server's default collation is case-insensitive, and the service
        // stores codes upper case, so "oos" and "OOS" are one code either way.
        b.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique();

        b.Property(x => x.Description).HasMaxLength(200).IsRequired();
        // No database default on IsActive: with a default of true EF would treat an explicit false as "unset".
        b.Property(x => x.IsSystem).HasDefaultValue(false);
        b.Property(x => x.DisplayOrder).HasDefaultValue(0);
    }
}
