using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Suppliers.Domain;

namespace SMS.Modules.Suppliers.Data.Maps;

internal sealed class ScorecardDimensionWeightMap : IEntityTypeConfiguration<ScorecardDimensionWeight>
{
    public void Configure(EntityTypeBuilder<ScorecardDimensionWeight> b)
    {
        b.ToTable("ScorecardDimensionWeights");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.DimensionCode).HasMaxLength(30).IsRequired();
        b.HasIndex(x => x.DimensionCode).IsUnique();
        b.Property(x => x.DimensionName).HasMaxLength(100).IsRequired();
        b.Property(x => x.WeightPercentage).HasColumnType("decimal(5,2)");
        b.Property(x => x.MaxPoints).HasColumnType("decimal(5,2)");
        b.Property(x => x.IsActive).HasDefaultValue(true);
    }
}

internal sealed class SupplierScoreSnapshotMap : IEntityTypeConfiguration<SupplierScoreSnapshot>
{
    public void Configure(EntityTypeBuilder<SupplierScoreSnapshot> b)
    {
        b.ToTable("SupplierScoreSnapshots");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.SupplierId).IsRequired();
        b.HasIndex(x => new { x.SupplierId, x.PeriodStart, x.PeriodEnd }).IsUnique();
        b.Property(x => x.DeliveryScore).HasColumnType("decimal(5,2)");
        b.Property(x => x.QuantityScore).HasColumnType("decimal(5,2)");
        b.Property(x => x.QualityScore).HasColumnType("decimal(5,2)");
        b.Property(x => x.PriceScore).HasColumnType("decimal(5,2)");
        b.Property(x => x.DocumentationScore).HasColumnType("decimal(5,2)");
        b.Property(x => x.TotalScore).HasColumnType("decimal(5,2)");
        b.Property(x => x.Grade).HasMaxLength(2).IsRequired();
        b.Property(x => x.Trend).HasMaxLength(12);
    }
}

internal sealed class GrnScoreDetailMap : IEntityTypeConfiguration<GrnScoreDetail>
{
    public void Configure(EntityTypeBuilder<GrnScoreDetail> b)
    {
        b.ToTable("GrnScoreDetails");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.GrnId).IsRequired();
        b.HasIndex(x => x.GrnId).IsUnique(); // one score row per GRN
        b.Property(x => x.SupplierId).IsRequired();
        b.Property(x => x.DeliveryPoints).HasColumnType("decimal(5,2)");
        b.Property(x => x.QuantityPoints).HasColumnType("decimal(5,2)");
        b.Property(x => x.QualityPoints).HasColumnType("decimal(5,2)");
        b.Property(x => x.PricePoints).HasColumnType("decimal(5,2)");
        b.Property(x => x.DocumentationPoints).HasColumnType("decimal(5,2)");
        b.Property(x => x.TotalRawScore).HasColumnType("decimal(5,2)");
        b.Property(x => x.WeightedScore).HasColumnType("decimal(5,2)");
        b.Property(x => x.ScoredAt).IsRequired();
    }
}
