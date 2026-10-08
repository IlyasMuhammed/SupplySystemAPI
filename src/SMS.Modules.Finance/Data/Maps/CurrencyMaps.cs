using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Finance.Domain;

namespace SMS.Modules.Finance.Data.Maps;

// A35 (D-1, D-3, D-15). Created by migration A35_CurrencyCore with guarded SQL; keep the names here and there in step.

internal sealed class OrgCurrencyMap : IEntityTypeConfiguration<OrgCurrency>
{
    public void Configure(EntityTypeBuilder<OrgCurrency> b)
    {
        b.ToTable("org_currencies", t =>
        {
            t.HasCheckConstraint("CK_org_currencies_Code", "LEN([Code]) = 3 AND [Code] = UPPER([Code]) COLLATE Latin1_General_CS_AS");
            t.HasCheckConstraint("CK_org_currencies_DecimalPlaces", "[DecimalPlaces] BETWEEN 0 AND 3");
            t.HasCheckConstraint("CK_org_currencies_Rounding", "[Rounding] > 0");
            t.HasCheckConstraint("CK_org_currencies_SymbolPosition", "[SymbolPosition] IN ('before','after')");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.HasIndex(x => x.Uuid).IsUnique();

        b.Property(x => x.Code).HasMaxLength(3).IsRequired();
        b.Property(x => x.Name).HasMaxLength(60).IsRequired();
        b.Property(x => x.Symbol).HasMaxLength(5).IsRequired();
        b.Property(x => x.DecimalPlaces).HasDefaultValue(2);
        b.Property(x => x.Rounding).HasColumnType("decimal(18,6)").HasDefaultValue(0.01m);
        b.Property(x => x.SymbolPosition).HasMaxLength(6).IsRequired().HasDefaultValue("before");
        b.Property(x => x.IsActive).HasDefaultValue(true);

        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.CurrencyId }).IsUnique();
        b.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique();
        b.HasIndex(x => new { x.OrganizationId, x.IsActive, x.DisplayOrder });
    }
}

internal sealed class CurrencyRateMap : IEntityTypeConfiguration<CurrencyRate>
{
    public void Configure(EntityTypeBuilder<CurrencyRate> b)
    {
        b.ToTable("currency_rates", t =>
        {
            t.HasCheckConstraint("CK_currency_rates_DateRange", "[EffectiveTo] >= [EffectiveFrom]");
            t.HasCheckConstraint("CK_currency_rates_RatePositive", "[Rate] > 0");
            t.HasCheckConstraint("CK_currency_rates_InversePositive", "[InverseRate] > 0");
            t.HasCheckConstraint("CK_currency_rates_Source",
                "[Source] IN ('MANUAL','API_SBP','API_ECB','API_OPENEXCHANGE','API_FOREX','SYSTEM')");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.HasIndex(x => x.Uuid).IsUnique();

        b.Property(x => x.CurrencyCode).HasMaxLength(3).IsRequired();
        b.Property(x => x.Rate).HasColumnType("decimal(18,10)");
        b.Property(x => x.InverseRate).HasColumnType("decimal(18,10)");
        b.Property(x => x.EffectiveFrom).HasColumnType("date");
        b.Property(x => x.EffectiveTo).HasColumnType("date");
        b.Property(x => x.Source).HasMaxLength(30).IsRequired().HasDefaultValue("MANUAL");
        b.Property(x => x.Notes).HasMaxLength(200);

        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.CurrencyId, x.EffectiveFrom }).IsUnique();
        b.HasIndex(x => new { x.OrganizationId, x.CurrencyId, x.EffectiveFrom, x.EffectiveTo })
         .HasDatabaseName("IX_currency_rates_Lookup");
        b.HasIndex(x => new { x.OrganizationId, x.EffectiveTo })
         .HasDatabaseName("IX_currency_rates_Active")
         .HasFilter("[EffectiveTo] = '9999-12-31'");
    }
}

internal sealed class ExchangeDifferenceMap : IEntityTypeConfiguration<ExchangeDifference>
{
    public void Configure(EntityTypeBuilder<ExchangeDifference> b)
    {
        b.ToTable("exchange_differences", t =>
        {
            t.HasCheckConstraint("CK_exchange_differences_Kind", "[Kind] IN ('REALIZED','UNREALIZED')");
            t.HasCheckConstraint("CK_exchange_differences_Side", "[Side] IN ('RECEIVABLE','PAYABLE')");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.HasIndex(x => x.Uuid).IsUnique();

        b.Property(x => x.Kind).HasMaxLength(12).IsRequired();
        b.Property(x => x.Side).HasMaxLength(12).IsRequired();
        b.Property(x => x.DocumentType).HasMaxLength(30).IsRequired();
        b.Property(x => x.DocumentNo).HasMaxLength(50);
        b.Property(x => x.PaymentType).HasMaxLength(30);
        b.Property(x => x.PaymentNo).HasMaxLength(50);
        b.Property(x => x.CurrencyCode).HasMaxLength(10).IsRequired();
        b.Property(x => x.BaseCurrencyCode).HasMaxLength(10).IsRequired();
        b.Property(x => x.AmountCurrency).HasColumnType("decimal(18,4)");
        b.Property(x => x.BookedRate).HasColumnType("decimal(18,10)");
        b.Property(x => x.SettlementRate).HasColumnType("decimal(18,10)");
        b.Property(x => x.BookedAmountBase).HasColumnType("decimal(18,4)");
        b.Property(x => x.SettledAmountBase).HasColumnType("decimal(18,4)");
        b.Property(x => x.DifferenceBase).HasColumnType("decimal(18,4)");
        b.Property(x => x.AccountCode).HasMaxLength(20);
        b.Property(x => x.RevaluationDate).HasColumnType("date");

        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.Kind, x.PostedAt });
        b.HasIndex(x => new { x.OrganizationId, x.DocumentType, x.DocumentId });
        b.HasIndex(x => new { x.OrganizationId, x.PaymentType, x.PaymentId });
        // One unrealized row per open document per revaluation date (re-run replaces them).
        b.HasIndex(x => new { x.OrganizationId, x.DocumentType, x.DocumentId, x.RevaluationDate })
         .IsUnique()
         .HasDatabaseName("UX_exchange_differences_Revaluation")
         .HasFilter("[Kind] = 'UNREALIZED' AND [RevaluationDate] IS NOT NULL");
    }
}
