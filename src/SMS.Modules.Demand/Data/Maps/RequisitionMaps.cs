using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Demand.Domain;

namespace SMS.Modules.Demand.Data.Maps;

internal sealed class PurchaseRequisitionMap : IEntityTypeConfiguration<PurchaseRequisition>
{
    public void Configure(EntityTypeBuilder<PurchaseRequisition> b)
    {
        b.ToTable("purchase_requisitions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.TraceId).IsRequired().ValueGeneratedOnAdd().HasDefaultValueSql("NEWSEQUENTIALID()");
        b.HasIndex(x => x.TraceId);
        b.Property(x => x.PrNumber).HasMaxLength(20).IsRequired();
        b.HasIndex(x => x.PrNumber).IsUnique();

        b.Property(x => x.PrTitle).HasMaxLength(200).IsRequired();
        b.Property(x => x.Department).HasMaxLength(100);
        b.Property(x => x.Priority).HasMaxLength(10);
        b.Property(x => x.PrType).HasMaxLength(50);
        b.Property(x => x.Justification).HasMaxLength(1000);
        b.Property(x => x.EstimatedTotal).HasColumnType("decimal(18,2)");
        b.Property(x => x.Status).HasMaxLength(20).HasDefaultValue("DRAFT");
        b.Property(x => x.RejectionReason).HasMaxLength(500);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.IsActive).HasDefaultValue(true);

        b.HasMany(x => x.Lines)
         .WithOne(x => x.PurchaseRequisition)
         .HasForeignKey(x => x.PurchaseRequisitionId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PrLineMap : IEntityTypeConfiguration<PrLine>
{
    public void Configure(EntityTypeBuilder<PrLine> b)
    {
        b.ToTable("pr_lines");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.ItemDescription).HasMaxLength(300).IsRequired();
        b.Property(x => x.Specification).HasMaxLength(500);
        b.Property(x => x.UnitOfMeasure).HasMaxLength(20);
        b.Property(x => x.Quantity).HasColumnType("decimal(18,4)");
        b.Property(x => x.EstimatedUnitPrice).HasColumnType("decimal(18,2)");
        b.Property(x => x.LineTotal).HasColumnType("decimal(18,2)");
        b.Property(x => x.QuotationStatus).HasMaxLength(20);
        b.Property(x => x.LineStatus).HasMaxLength(20);
        b.Property(x => x.BudgetCode).HasMaxLength(50);
        b.Property(x => x.DisbursedQty).HasColumnType("decimal(18,4)").HasDefaultValue(0m);
        b.Property(x => x.DisbursedMirIds).HasColumnType("nvarchar(max)").HasDefaultValue("[]");

        b.HasOne(x => x.PurchaseRequisition)
         .WithMany(x => x.Lines)
         .HasForeignKey(x => x.PurchaseRequisitionId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}
