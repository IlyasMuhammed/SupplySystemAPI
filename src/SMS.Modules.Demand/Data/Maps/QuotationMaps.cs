using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Demand.Domain;

namespace SMS.Modules.Demand.Data.Maps;

internal sealed class QuotationMap : IEntityTypeConfiguration<Quotation>
{
    public void Configure(EntityTypeBuilder<Quotation> b)
    {
        b.ToTable("quotations");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.TraceId).IsRequired().ValueGeneratedOnAdd().HasDefaultValueSql("NEWSEQUENTIALID()");
        b.HasIndex(x => x.TraceId);
        b.Property(x => x.QuotationNumber).HasMaxLength(20).IsRequired();
        b.HasIndex(x => x.QuotationNumber).IsUnique();
        b.Property(x => x.Title).HasMaxLength(200).IsRequired();
        b.Property(x => x.SourceType).HasMaxLength(20).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).HasDefaultValue("DRAFT");
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.CancellationReason).HasMaxLength(500);
        b.Property(x => x.IsActive).HasDefaultValue(true);

        b.HasMany(x => x.Lines)
         .WithOne(x => x.Quotation)
         .HasForeignKey(x => x.QuotationId)
         .OnDelete(DeleteBehavior.Cascade);

        b.HasMany(x => x.VendorResponses)
         .WithOne(x => x.Quotation)
         .HasForeignKey(x => x.QuotationId)
         .OnDelete(DeleteBehavior.Cascade);

        b.HasMany(x => x.InvitedSuppliers)
         .WithOne(x => x.Quotation)
         .HasForeignKey(x => x.QuotationId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class QuotationLineMap : IEntityTypeConfiguration<QuotationLine>
{
    public void Configure(EntityTypeBuilder<QuotationLine> b)
    {
        b.ToTable("quotation_lines");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.ItemDescription).HasMaxLength(300).IsRequired();
        b.Property(x => x.Specification).HasMaxLength(500);
        b.Property(x => x.UnitOfMeasure).HasMaxLength(20);
        b.Property(x => x.Quantity).HasColumnType("decimal(18,4)");
        b.Property(x => x.BudgetCode).HasMaxLength(50);

        b.HasOne(x => x.Quotation)
         .WithMany(x => x.Lines)
         .HasForeignKey(x => x.QuotationId)
         .OnDelete(DeleteBehavior.Cascade);

        // NoAction to avoid multiple cascade paths to vendor_response_lines in SQL Server.
        // VendorResponseLines are deleted via the VendorResponse → Cascade path instead.
        b.HasMany(x => x.ResponseLines)
         .WithOne(x => x.QuotationLine)
         .HasForeignKey(x => x.QuotationLineId)
         .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class VendorResponseMap : IEntityTypeConfiguration<VendorResponse>
{
    public void Configure(EntityTypeBuilder<VendorResponse> b)
    {
        b.ToTable("vendor_responses");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.SupplierName).HasMaxLength(200).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).HasDefaultValue("PENDING");
        b.Property(x => x.TotalAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.Notes).HasMaxLength(500);

        b.HasOne(x => x.Quotation)
         .WithMany(x => x.VendorResponses)
         .HasForeignKey(x => x.QuotationId)
         .OnDelete(DeleteBehavior.Cascade);

        b.HasMany(x => x.Lines)
         .WithOne(x => x.VendorResponse)
         .HasForeignKey(x => x.VendorResponseId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class VendorResponseLineMap : IEntityTypeConfiguration<VendorResponseLine>
{
    public void Configure(EntityTypeBuilder<VendorResponseLine> b)
    {
        b.ToTable("vendor_response_lines");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.NetUnitPrice).HasColumnType("decimal(18,2)");
        b.Property(x => x.Quantity).HasColumnType("decimal(18,4)");
        b.Property(x => x.LineTotal).HasColumnType("decimal(18,2)");
        b.Property(x => x.Notes).HasMaxLength(500);

        b.HasOne(x => x.VendorResponse)
         .WithMany(x => x.Lines)
         .HasForeignKey(x => x.VendorResponseId)
         .OnDelete(DeleteBehavior.Cascade);

        b.HasOne(x => x.QuotationLine)
         .WithMany(x => x.ResponseLines)
         .HasForeignKey(x => x.QuotationLineId)
         .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class QuotationInvitedSupplierMap : IEntityTypeConfiguration<QuotationInvitedSupplier>
{
    public void Configure(EntityTypeBuilder<QuotationInvitedSupplier> b)
    {
        b.ToTable("quotation_invited_suppliers");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.SupplierName).HasMaxLength(200).IsRequired();

        b.HasOne(x => x.Quotation)
         .WithMany(x => x.InvitedSuppliers)
         .HasForeignKey(x => x.QuotationId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RfqAccessLinkMap : IEntityTypeConfiguration<RfqAccessLink>
{
    public void Configure(EntityTypeBuilder<RfqAccessLink> b)
    {
        b.ToTable("rfq_access_links");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.Property(x => x.Status).HasMaxLength(20).HasDefaultValue("PENDING");
        b.Property(x => x.AccessCount).HasDefaultValue(0);
        b.Property(x => x.ConsumedIp).HasMaxLength(45);  // IPv6 max length
        b.Property(x => x.SupplierEmail).HasMaxLength(254);
        b.Property(x => x.PortalLinkUrl).HasMaxLength(1000);
        b.Property(x => x.ContactMobileNumber).HasMaxLength(20);  // E.164 max 15 digits + prefix
        b.Property(x => x.WhatsAppProviderMessageId).HasMaxLength(100);
        b.Property(x => x.WhatsAppStatus).HasMaxLength(20);
        b.HasIndex(x => x.WhatsAppProviderMessageId);

        b.HasOne(x => x.Quotation)
         .WithMany()
         .HasForeignKey(x => x.QuotationId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PoLineMap : IEntityTypeConfiguration<PoLine>
{
    public void Configure(EntityTypeBuilder<PoLine> b)
    {
        b.ToTable("po_lines");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.ItemDescription).HasMaxLength(300).IsRequired();
        b.Property(x => x.Quantity).HasColumnType("decimal(18,4)");
        b.Property(x => x.UnitPrice).HasColumnType("decimal(18,2)");
    }
}
