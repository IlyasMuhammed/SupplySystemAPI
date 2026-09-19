using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Suppliers.Domain;

namespace SMS.Modules.Suppliers.Data.Maps;

internal sealed class BusinessPartnerMap : IEntityTypeConfiguration<BusinessPartner>
{
    public void Configure(EntityTypeBuilder<BusinessPartner> b)
    {
        // P1-01 (Addendum 29 §1.1) renamed the table; P1-03 renames the C# entity to match
        // (contained to this module — see the note on the entity class itself).
        b.ToTable("BusinessPartners");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.SupplierName).HasMaxLength(200).IsRequired();
        b.Property(x => x.SupplierCode).HasMaxLength(10).IsRequired();
        // Composite, not global — each org assigns its own supplier codes independently.
        b.HasIndex(x => new { x.OrganizationId, x.SupplierCode }).IsUnique();

        b.Property(x => x.RegistrationNo).HasMaxLength(50);
        b.Property(x => x.TaxId).HasMaxLength(30);

        b.Property(x => x.Country).HasMaxLength(200);
        b.Property(x => x.ProvinceState).HasMaxLength(100);
        b.Property(x => x.City).HasMaxLength(100);
        b.Property(x => x.AddressLine1).HasMaxLength(200);
        b.Property(x => x.AddressLine2).HasMaxLength(200);
        b.Property(x => x.PostalCode).HasMaxLength(20);

        b.Property(x => x.Phone).HasMaxLength(20);
        b.Property(x => x.Fax).HasMaxLength(20);
        b.Property(x => x.Email).HasMaxLength(150);
        b.Property(x => x.Website).HasMaxLength(200);

        b.Property(x => x.PrimaryContactName).HasMaxLength(100);
        b.Property(x => x.PrimaryContactTitle).HasMaxLength(100);
        b.Property(x => x.PrimaryContactPhone).HasMaxLength(20);
        b.Property(x => x.PrimaryContactEmail).HasMaxLength(150);

        b.Property(x => x.CreditLimit).HasColumnType("decimal(18,2)");
        b.Property(x => x.Rating).HasColumnType("decimal(3,1)");
        b.Property(x => x.Status).HasMaxLength(20).HasDefaultValue("PENDING");
        b.Property(x => x.RejectedReason).HasMaxLength(500);
        b.Property(x => x.BlacklistedReason).HasMaxLength(500);
        b.Property(x => x.SuspendedReason).HasMaxLength(500);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.IsActive).HasDefaultValue(true);
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);

        // P1-02 — every row so far came through the vendor-only legacy path, so the defaults
        // themselves are the backfill: SQL Server applies a NOT NULL column's DEFAULT to existing
        // rows at ADD COLUMN time, same as it would to a row inserted a moment before the migration
        // ran. is_customer/is_carrier/is_service_provider default false for the same reason.
        b.Property(x => x.PartnerType).HasMaxLength(20).HasDefaultValue("VENDOR");
        b.Property(x => x.IsVendor).HasDefaultValue(true);
        b.Property(x => x.IsCustomer).HasDefaultValue(false);
        b.Property(x => x.IsCarrier).HasDefaultValue(false);
        b.Property(x => x.IsServiceProvider).HasDefaultValue(false);
        b.Property(x => x.VehicleTypes).HasMaxLength(500);
        b.Property(x => x.ServiceCategories).HasMaxLength(500);
        b.HasIndex(x => new { x.OrganizationId, x.PartnerType, x.IsActive });
        b.HasIndex(x => new { x.OrganizationId, x.IsVendor, x.IsCustomer, x.IsCarrier, x.IsServiceProvider });
    }
}

internal sealed class SupplierTypeMappingMap : IEntityTypeConfiguration<SupplierTypeMapping>
{
    public void Configure(EntityTypeBuilder<SupplierTypeMapping> b)
    {
        b.ToTable("SupplierTypeMappings");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.LookupValueId).IsRequired();
        b.Property(x => x.AssignedAt).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(200);
        b.HasIndex(x => new { x.SupplierId, x.LookupValueId }).IsUnique();
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);

        b.HasOne(x => x.Supplier)
         .WithMany(x => x.TypeMappings)
         .HasForeignKey(x => x.SupplierId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SupplierIndustryMappingMap : IEntityTypeConfiguration<SupplierIndustryMapping>
{
    public void Configure(EntityTypeBuilder<SupplierIndustryMapping> b)
    {
        b.ToTable("SupplierIndustryMappings");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.LookupValueId).IsRequired();
        b.Property(x => x.AssignedAt).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(200);
        b.HasIndex(x => new { x.SupplierId, x.LookupValueId }).IsUnique();
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);

        b.HasOne(x => x.Supplier)
         .WithMany(x => x.IndustryMappings)
         .HasForeignKey(x => x.SupplierId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SupplierContactMap : IEntityTypeConfiguration<SupplierContact>
{
    public void Configure(EntityTypeBuilder<SupplierContact> b)
    {
        b.ToTable("SupplierContacts");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.ContactName).HasMaxLength(200).IsRequired();
        b.Property(x => x.Title).HasMaxLength(100);
        b.Property(x => x.Phone).HasMaxLength(20);
        b.Property(x => x.Email).HasMaxLength(150);
        b.Property(x => x.IsActive).HasDefaultValue(true);
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);

        b.HasOne(x => x.Supplier)
         .WithMany(x => x.Contacts)
         .HasForeignKey(x => x.SupplierId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SupplierDocumentMap : IEntityTypeConfiguration<SupplierDocument>
{
    public void Configure(EntityTypeBuilder<SupplierDocument> b)
    {
        b.ToTable("SupplierDocuments");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.FileName).HasMaxLength(500).IsRequired();
        b.Property(x => x.FileUrl).HasMaxLength(1000).IsRequired();
        b.Property(x => x.DocumentType).HasMaxLength(100);
        b.Property(x => x.IsActive).HasDefaultValue(true);
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);

        b.HasOne(x => x.Supplier)
         .WithMany(x => x.Documents)
         .HasForeignKey(x => x.SupplierId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SupplierBankDetailMap : IEntityTypeConfiguration<SupplierBankDetail>
{
    public void Configure(EntityTypeBuilder<SupplierBankDetail> b)
    {
        b.ToTable("SupplierBankDetails");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.BankName).HasMaxLength(100);
        // Encrypted columns — stored as base64 strings; max 500 chars covers AES-256 overhead
        b.Property(x => x.BankAccountNo).HasMaxLength(500);
        b.Property(x => x.BankIban).HasMaxLength(500);
        b.Property(x => x.BankSwift).HasMaxLength(500);
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);

        b.HasOne(x => x.Supplier)
         .WithOne(x => x.BankDetail)
         .HasForeignKey<SupplierBankDetail>(x => x.SupplierId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SupplierTypeMap : IEntityTypeConfiguration<SupplierType>
{
    public void Configure(EntityTypeBuilder<SupplierType> b)
    {
        b.ToTable("SupplierTypes");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);
    }
}

internal sealed class SupplierCategoryMap : IEntityTypeConfiguration<SupplierCategory>
{
    public void Configure(EntityTypeBuilder<SupplierCategory> b)
    {
        b.ToTable("SupplierCategories");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);
    }
}
