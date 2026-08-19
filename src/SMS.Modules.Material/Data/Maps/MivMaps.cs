using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Material.Domain;

namespace SMS.Modules.Material.Data.Maps;

internal sealed class MaterialIssueVoucherMap : IEntityTypeConfiguration<MaterialIssueVoucher>
{
    public void Configure(EntityTypeBuilder<MaterialIssueVoucher> b)
    {
        b.ToTable("material_issue_vouchers");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.IssueNo).HasMaxLength(20).IsRequired();
        // Composite, not global — each org generates its own MIV issue number sequence.
        b.HasIndex(x => new { x.OrganizationId, x.IssueNo }).IsUnique();

        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.IssuedTo).HasMaxLength(200);
        b.Property(x => x.IssueDate).IsRequired();
        b.Property(x => x.TotalValue).HasColumnType("decimal(18,2)");
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);

        b.HasOne(x => x.MaterialIssueRequest)
         .WithMany()
         .HasForeignKey(x => x.MirId)
         .OnDelete(DeleteBehavior.Restrict);

        b.HasMany(x => x.Lines)
         .WithOne(x => x.MaterialIssueVoucher)
         .HasForeignKey(x => x.MivId)
         .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => x.MirId);
    }
}

internal sealed class MaterialIssueVoucherLineMap : IEntityTypeConfiguration<MaterialIssueVoucherLine>
{
    public void Configure(EntityTypeBuilder<MaterialIssueVoucherLine> b)
    {
        b.ToTable("material_issue_voucher_lines");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.InventoryItemId).IsRequired();
        b.Property(x => x.VariantUuid).IsRequired();
        b.Property(x => x.ItemDescription).HasMaxLength(300).IsRequired();
        b.Property(x => x.UnitOfMeasure).HasMaxLength(20);
        b.Property(x => x.IssuedQty).HasColumnType("decimal(18,4)").IsRequired();
        b.Property(x => x.UnitCost).HasColumnType("decimal(18,4)").IsRequired();
        b.Property(x => x.LineValue).HasColumnType("decimal(18,2)").IsRequired();
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);

        b.HasOne(x => x.MaterialIssueVoucher)
         .WithMany(x => x.Lines)
         .HasForeignKey(x => x.MivId)
         .OnDelete(DeleteBehavior.Cascade);

        b.HasOne(x => x.MirLine)
         .WithMany()
         .HasForeignKey(x => x.MirLineId)
         .OnDelete(DeleteBehavior.NoAction);

        b.HasMany(x => x.BatchSerials)
         .WithOne(x => x.MivLine)
         .HasForeignKey(x => x.MivLineId)
         .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => x.MirLineId);
    }
}

internal sealed class MivLineBatchSerialMap : IEntityTypeConfiguration<MivLineBatchSerial>
{
    public void Configure(EntityTypeBuilder<MivLineBatchSerial> b)
    {
        b.ToTable("miv_line_batch_serials");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.InventoryItemId).IsRequired();
        b.Property(x => x.BatchNumber).HasMaxLength(100);
        b.Property(x => x.SerialNumber).HasMaxLength(50);
        b.Property(x => x.IssuedQty).HasColumnType("decimal(18,4)").IsRequired();
        b.Property(x => x.UnitCost).HasColumnType("decimal(18,4)").IsRequired();
        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);

        b.HasOne(x => x.MivLine)
         .WithMany(x => x.BatchSerials)
         .HasForeignKey(x => x.MivLineId)
         .OnDelete(DeleteBehavior.Cascade);

        b.HasIndex(x => x.MivLineId);
        b.HasIndex(x => x.InventoryItemId);
        // Ensure one serial per MIV posting: prevents the same InventoryItemId being selected twice on the same MIV
        b.HasIndex(x => new { x.MivLineId, x.InventoryItemId }).IsUnique();
    }
}
