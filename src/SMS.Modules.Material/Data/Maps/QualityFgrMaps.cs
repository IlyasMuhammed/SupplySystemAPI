using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Material.Domain;

namespace SMS.Modules.Material.Data.Maps;

internal sealed class QualityInspectionMap : IEntityTypeConfiguration<QualityInspection>
{
    public void Configure(EntityTypeBuilder<QualityInspection> b)
    {
        b.ToTable("quality_inspections");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.InspectionNumber).HasMaxLength(50).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.InspectionNumber }).IsUnique();
        // §18.4 — one production order, one inspection.
        b.HasIndex(x => x.ProductionOrderId).IsUnique();
        foreach (var quantity in new[]
        {
            nameof(QualityInspection.InspectedQuantity), nameof(QualityInspection.AcceptedQuantity),
            nameof(QualityInspection.RejectedQuantity), nameof(QualityInspection.HoldQuantity), nameof(QualityInspection.ReworkQuantity)
        })
            b.Property(quantity).HasColumnType("decimal(18,4)");
        b.Property(x => x.OverallResult).HasMaxLength(20).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(2000);
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);

        b.HasOne(x => x.ProductionOrder).WithMany()
         .HasForeignKey(x => x.ProductionOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Lines).WithOne(x => x.Inspection)
         .HasForeignKey(x => x.InspectionId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class QualityInspectionLineMap : IEntityTypeConfiguration<QualityInspectionLine>
{
    public void Configure(EntityTypeBuilder<QualityInspectionLine> b)
    {
        b.ToTable("quality_inspection_lines");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.CheckName).HasMaxLength(200).IsRequired();
        b.Property(x => x.Result).HasMaxLength(20).IsRequired();
        b.Property(x => x.QuantityChecked).HasColumnType("decimal(18,4)");
        b.Property(x => x.DefectCode).HasMaxLength(50);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);
        b.HasIndex(x => x.InspectionId);
    }
}

internal sealed class FinishedGoodsReceiptMap : IEntityTypeConfiguration<FinishedGoodsReceipt>
{
    public void Configure(EntityTypeBuilder<FinishedGoodsReceipt> b)
    {
        b.ToTable("finished_goods_receipts");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.FgrNumber).HasMaxLength(50).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.FgrNumber }).IsUnique();
        b.Property(x => x.TotalQuantity).HasColumnType("decimal(18,4)");
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);
        b.HasIndex(x => new { x.ProductionOrderId, x.Status });
        b.HasIndex(x => x.QualityInspectionId);

        b.HasOne(x => x.ProductionOrder).WithMany()
         .HasForeignKey(x => x.ProductionOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.QualityInspection).WithMany()
         .HasForeignKey(x => x.QualityInspectionId).OnDelete(DeleteBehavior.Restrict);
    }
}
