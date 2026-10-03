using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Finance.Domain;

namespace SMS.Modules.Finance.Data.Maps;

internal sealed class TaxCodeMap : IEntityTypeConfiguration<TaxCode>
{
    public void Configure(EntityTypeBuilder<TaxCode> b)
    {
        b.ToTable("tax_codes");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.HasIndex(x => x.Uuid).IsUnique();

        b.Property(x => x.Code).HasMaxLength(20).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Description).HasMaxLength(300);
        b.Property(x => x.RatePercent).HasColumnType("decimal(5,2)");
        b.Property(x => x.Usage).HasMaxLength(10).IsRequired().HasDefaultValue(TaxCodeUsages.Both);
        b.Property(x => x.IsActive).HasDefaultValue(true);

        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.IsActive, x.Usage });
    }
}

internal sealed class ExchangeRateMap : IEntityTypeConfiguration<ExchangeRate>
{
    public void Configure(EntityTypeBuilder<ExchangeRate> b)
    {
        b.ToTable("exchange_rates");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.HasIndex(x => x.Uuid).IsUnique();

        b.Property(x => x.FromCurrencyCode).HasMaxLength(10).IsRequired();
        b.Property(x => x.ToCurrencyCode).HasMaxLength(10).IsRequired();
        b.Property(x => x.Rate).HasColumnType("decimal(18,8)");
        b.Property(x => x.EffectiveDate).HasColumnType("date");
        b.Property(x => x.Source).HasMaxLength(20).IsRequired().HasDefaultValue("MANUAL");
        b.Property(x => x.Notes).HasMaxLength(300);

        // One live rate per pair per day; a deleted one frees the day for a correction.
        b.HasIndex(x => new { x.OrganizationId, x.FromCurrencyCode, x.ToCurrencyCode, x.EffectiveDate })
         .IsUnique()
         .HasFilter("[IsDelete] = 0");

        b.Property(x => x.OrganizationId).IsRequired();
    }
}
