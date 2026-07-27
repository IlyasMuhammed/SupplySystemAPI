using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Warehouse.Domain;

namespace SMS.Modules.Warehouse.Data.Maps;

internal sealed class GrnMap : IEntityTypeConfiguration<Grn>
{
    public void Configure(EntityTypeBuilder<Grn> b)
    {
        b.ToTable("grns");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.TraceId).IsRequired().ValueGeneratedOnAdd().HasDefaultValueSql("NEWSEQUENTIALID()");
        b.HasIndex(x => x.TraceId);
        b.Property(x => x.GrnNumber).HasMaxLength(20).IsRequired();
        b.HasIndex(x => x.GrnNumber).IsUnique();
        b.Property(x => x.PoNumber).HasMaxLength(20).IsRequired();
        b.Property(x => x.SupplierName).HasMaxLength(200).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).HasDefaultValue("DRAFT");
        b.Property(x => x.DeliveryNoteNo).HasMaxLength(50);
        b.Property(x => x.VehicleNo).HasMaxLength(30);
        b.Property(x => x.DriverName).HasMaxLength(100);
        b.Property(x => x.InvoiceNo).HasMaxLength(50);
        b.Property(x => x.Status).HasMaxLength(25).HasDefaultValue("DRAFT");
        b.Property(x => x.QcNotes).HasMaxLength(300);
        b.Property(x => x.Notes).HasMaxLength(300);
        b.Property(x => x.RequiresInspection).HasDefaultValue(true);
        b.Property(x => x.RejectionReason).HasMaxLength(500);
        b.Property(x => x.IsActive).HasDefaultValue(true);

        b.HasMany(x => x.Lines)
         .WithOne(x => x.Grn)
         .HasForeignKey(x => x.GrnId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class GrnLineMap : IEntityTypeConfiguration<GrnLine>
{
    public void Configure(EntityTypeBuilder<GrnLine> b)
    {
        b.ToTable("grn_lines");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.PoLineUuid).IsRequired();
        b.Property(x => x.RequiresInspection).HasDefaultValue(true);
        b.Property(x => x.ItemDescription).HasMaxLength(300).IsRequired();
        b.Property(x => x.UnitOfMeasure).HasMaxLength(20);
        b.Property(x => x.QtyOrdered).HasColumnType("decimal(18,4)");
        b.Property(x => x.QtyReceived).HasColumnType("decimal(18,4)");
        b.Property(x => x.QtyAccepted).HasColumnType("decimal(18,4)");
        b.Property(x => x.QtyRejected).HasColumnType("decimal(18,4)");
        b.Property(x => x.UnitCost).HasColumnType("decimal(18,2)");
        b.Property(x => x.RejectionReason).HasMaxLength(500);
        b.Property(x => x.BatchNumber).HasMaxLength(100);
        b.Property(x => x.SerialNumber).HasMaxLength(50);
        b.Property(x => x.QcResult).HasMaxLength(20);
        b.Property(x => x.InspectionResult).HasMaxLength(15);
        b.Property(x => x.InspectorRemarks).HasMaxLength(500);
    }
}

internal sealed class SupplierReturnOrderMap : IEntityTypeConfiguration<SupplierReturnOrder>
{
    public void Configure(EntityTypeBuilder<SupplierReturnOrder> b)
    {
        b.ToTable("supplier_return_orders");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.ReturnNumber).HasMaxLength(25).IsRequired();
        b.HasIndex(x => x.ReturnNumber).IsUnique();
        b.Property(x => x.SroType).HasMaxLength(30).HasDefaultValue("POST_RECEIPT_DEFECT");
        b.Property(x => x.SupplierName).HasMaxLength(200).IsRequired();
        b.Property(x => x.GrnNumber).HasMaxLength(25);
        b.Property(x => x.WarehouseUuid);
        b.Property(x => x.OriginalPoNumber).HasMaxLength(25);
        b.Property(x => x.ReturnReason).HasMaxLength(30).HasDefaultValue("DAMAGED");
        b.Property(x => x.ReturnReasonDetail).HasMaxLength(500);
        b.Property(x => x.RmaNumber).HasMaxLength(100);
        b.Property(x => x.DispatchCarrier).HasMaxLength(150);
        b.Property(x => x.DispatchTrackingRef).HasMaxLength(100);
        b.Property(x => x.ResolutionType).HasMaxLength(20);
        b.Property(x => x.Status).HasMaxLength(30).HasDefaultValue("DRAFT");
        b.Property(x => x.RejectionReason).HasMaxLength(500);
        b.Property(x => x.SlaDeadline);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.IsActive).HasDefaultValue(true);

        b.HasOne(x => x.Grn)
         .WithMany(x => x.ReturnOrders)
         .HasForeignKey(x => x.GrnId)
         .IsRequired(false)
         .OnDelete(DeleteBehavior.Restrict);

        b.HasMany(x => x.Lines)
         .WithOne(x => x.ReturnOrder)
         .HasForeignKey(x => x.ReturnOrderId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SupplierReturnOrderLineMap : IEntityTypeConfiguration<SupplierReturnOrderLine>
{
    public void Configure(EntityTypeBuilder<SupplierReturnOrderLine> b)
    {
        b.ToTable("supplier_return_order_lines");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.ItemDescription).HasMaxLength(300).IsRequired();
        b.Property(x => x.UnitOfMeasure).HasMaxLength(20);
        b.Property(x => x.QtyToReturn).HasColumnType("decimal(18,4)");
        b.Property(x => x.ReturnReason).HasMaxLength(30).HasDefaultValue("DAMAGED");
        b.Property(x => x.ReturnReasonDetail).HasMaxLength(500);
        b.Property(x => x.Condition).HasMaxLength(200);
        b.Property(x => x.UnitCost).HasColumnType("decimal(18,2)");
    }
}
