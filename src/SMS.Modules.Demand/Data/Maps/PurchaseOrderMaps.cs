using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Demand.Domain;

namespace SMS.Modules.Demand.Data.Maps;

internal sealed class PurchaseOrderMap : IEntityTypeConfiguration<PurchaseOrder>
{
    public void Configure(EntityTypeBuilder<PurchaseOrder> b)
    {
        b.ToTable("purchase_orders");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.TraceId).IsRequired().ValueGeneratedOnAdd().HasDefaultValueSql("NEWSEQUENTIALID()");
        b.HasIndex(x => x.TraceId);
        b.Property(x => x.PoNumber).HasMaxLength(20).IsRequired();
        b.HasIndex(x => x.PoNumber).IsUnique();
        b.Property(x => x.Title).HasMaxLength(200);
        b.Property(x => x.SupplierName).HasMaxLength(200).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).HasDefaultValue("DRAFT");
        b.Property(x => x.TotalAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.InternalNotes).HasMaxLength(2000);
        b.Property(x => x.DeliveryWarehouseName).HasMaxLength(150);
        b.Property(x => x.SupplierContactMobile).HasMaxLength(20);
        b.Property(x => x.IsActive).HasDefaultValue(true);

        b.HasMany(x => x.Lines)
         .WithOne(x => x.PurchaseOrder)
         .HasForeignKey(x => x.PurchaseOrderId)
         .OnDelete(DeleteBehavior.Cascade);

        b.HasMany(x => x.PrLinks)
         .WithOne(x => x.PurchaseOrder)
         .HasForeignKey(x => x.PurchaseOrderId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PurchaseOrderLineMap : IEntityTypeConfiguration<PurchaseOrderLine>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderLine> b)
    {
        b.ToTable("purchase_order_lines");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.ItemDescription).HasMaxLength(300).IsRequired();
        b.Property(x => x.Specification).HasMaxLength(500);
        b.Property(x => x.UnitOfMeasure).HasMaxLength(20);
        b.Property(x => x.Quantity).HasColumnType("decimal(18,4)");
        b.Property(x => x.UnitPrice).HasColumnType("decimal(18,2)");
        b.Property(x => x.LineTotal).HasColumnType("decimal(18,2)");
        b.Property(x => x.QtyReceived).HasColumnType("decimal(18,4)").HasDefaultValue(0m);
        b.Property(x => x.QtyInvoiced).HasColumnType("decimal(18,4)").HasDefaultValue(0m);
        b.Property(x => x.BudgetCode).HasMaxLength(50);
        b.Property(x => x.WarehouseName).HasMaxLength(150);
        b.Property(x => x.RequiresInspection).HasDefaultValue(true);
    }
}

internal sealed class PurchaseOrderPrLinkMap : IEntityTypeConfiguration<PurchaseOrderPrLink>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderPrLink> b)
    {
        b.ToTable("purchase_order_pr_links");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.PrUuid).IsRequired();
    }
}
