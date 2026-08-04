using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Lookups.Domain;

namespace SMS.Modules.Lookups.Data.Maps;

internal sealed class CityMap : IEntityTypeConfiguration<City>
{
    public void Configure(EntityTypeBuilder<City> b)
    {
        b.ToTable("Cities");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
    }
}

internal sealed class CountryMap : IEntityTypeConfiguration<Country>
{
    public void Configure(EntityTypeBuilder<Country> b)
    {
        b.ToTable("Countries");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Code).HasMaxLength(10);
    }
}

internal sealed class CurrencyMap : IEntityTypeConfiguration<Currency>
{
    public void Configure(EntityTypeBuilder<Currency> b)
    {
        b.ToTable("Currencies");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Code).HasMaxLength(10);
        b.Property(x => x.Symbol).HasMaxLength(10);
    }
}

internal sealed class DeliveryTermMap : IEntityTypeConfiguration<DeliveryTerm>
{
    public void Configure(EntityTypeBuilder<DeliveryTerm> b)
    {
        b.ToTable("DeliveryTerms");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);
    }
}

internal sealed class PaymentTermMap : IEntityTypeConfiguration<PaymentTerm>
{
    public void Configure(EntityTypeBuilder<PaymentTerm> b)
    {
        b.ToTable("PaymentTerms");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
    }
}

internal sealed class LookupTypeMap : IEntityTypeConfiguration<LookupType>
{
    public void Configure(EntityTypeBuilder<LookupType> b)
    {
        b.ToTable("LookupTypes");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Slug).HasMaxLength(50).IsRequired();
        b.HasIndex(x => x.Slug).IsUnique();
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.IsActive).HasDefaultValue(true);
    }
}

internal sealed class LookupValueMap : IEntityTypeConfiguration<LookupValue>
{
    public void Configure(EntityTypeBuilder<LookupValue> b)
    {
        b.ToTable("LookupValues");
        b.HasKey(x => x.Id);
        b.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.IsActive).HasDefaultValue(true);
        b.Property(x => x.SortOrder).HasDefaultValue(0);
        b.Property(x => x.IsGlobal).HasDefaultValue(true);
        b.HasIndex(x => x.OrganizationId);

        b.HasOne(x => x.Type)
         .WithMany(x => x.Values)
         .HasForeignKey(x => x.TypeId)
         .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PoDocumentTemplateMap : IEntityTypeConfiguration<PoDocumentTemplate>
{
    public void Configure(EntityTypeBuilder<PoDocumentTemplate> b)
    {
        b.ToTable("PoDocumentTemplates");
        b.HasKey(x => x.Id);
        b.Property(x => x.CompanyName).HasMaxLength(200);
        b.Property(x => x.CompanyAddress).HasMaxLength(500);
        b.Property(x => x.CompanyLogoUrl).HasMaxLength(1000);
        b.Property(x => x.CompanyTaxId).HasMaxLength(50);
        b.Property(x => x.CompanyPhone).HasMaxLength(50);
        b.Property(x => x.CompanyEmail).HasMaxLength(200);
        b.Property(x => x.BodyHtml).HasColumnType("nvarchar(max)");
        b.Property(x => x.SignatureDisclaimer).HasMaxLength(500);
        b.Property(x => x.PreparedByLabel).HasMaxLength(100);
        b.Property(x => x.ApprovedByLabel).HasMaxLength(100);
        b.Property(x => x.AuthorizedSignatoryLabel).HasMaxLength(100);
        b.Property(x => x.FooterText).HasMaxLength(500);
        b.Property(x => x.ShowSignatureBlock).HasDefaultValue(true);
        b.Property(x => x.IsActive).HasDefaultValue(true);
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);
    }
}
