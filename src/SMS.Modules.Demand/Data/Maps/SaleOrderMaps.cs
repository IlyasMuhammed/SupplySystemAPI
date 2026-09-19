using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Demand.Domain;

namespace SMS.Modules.Demand.Data.Maps;

internal sealed class SaleOrderMap : IEntityTypeConfiguration<SaleOrder>
{
    public void Configure(EntityTypeBuilder<SaleOrder> b)
    {
        b.ToTable("sale_orders");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.TraceId).IsRequired().ValueGeneratedOnAdd().HasDefaultValueSql("NEWSEQUENTIALID()");
        b.HasIndex(x => x.TraceId);

        b.Property(x => x.SoNumber).HasMaxLength(20).IsRequired();
        // Composite, not global — each org generates its own SO number sequence (matches PoNumber).
        b.HasIndex(x => new { x.OrganizationId, x.SoNumber }).IsUnique();

        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.DeliveryMode).HasMaxLength(20).IsRequired();
        b.Property(x => x.Subtotal).HasColumnType("decimal(18,2)");
        b.Property(x => x.TaxAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.DiscountAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.GrandTotal).HasColumnType("decimal(18,2)");
        b.Property(x => x.RequiresShipment).HasDefaultValue(true);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.IsDeleted).HasDefaultValue(false);

        b.Property(x => x.OrganizationId).IsRequired();
        // §17.2 — SaleOrders (org, status, order_date), (org, partner).
        b.HasIndex(x => new { x.OrganizationId, x.Status, x.OrderDate });
        b.HasIndex(x => new { x.OrganizationId, x.PartnerId });

        b.HasMany(x => x.Lines)
         .WithOne(x => x.SaleOrder)
         .HasForeignKey(x => x.SaleOrderId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SaleOrderLineMap : IEntityTypeConfiguration<SaleOrderLine>
{
    public void Configure(EntityTypeBuilder<SaleOrderLine> b)
    {
        b.ToTable("sale_order_lines");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.Quantity).HasColumnType("decimal(18,4)");
        b.Property(x => x.UnitPrice).HasColumnType("decimal(18,4)");
        b.Property(x => x.DiscountPercent).HasColumnType("decimal(5,2)");
        b.Property(x => x.TaxPercent).HasColumnType("decimal(5,2)");
        b.Property(x => x.LineTotal).HasColumnType("decimal(18,2)");
        b.Property(x => x.FulfilledQty).HasColumnType("decimal(18,4)");
        b.Property(x => x.InvoicedQty).HasColumnType("decimal(18,4)");
        b.Property(x => x.AvailableQtyAtConfirm).HasColumnType("decimal(18,4)");
        b.Property(x => x.DeficitQty).HasColumnType("decimal(18,4)");
        b.Property(x => x.Margin).HasColumnType("decimal(18,2)");
        b.Property(x => x.MarginPercent).HasColumnType("decimal(5,2)");
        b.Property(x => x.FulfillmentMode).HasMaxLength(20);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.Notes).HasMaxLength(500);

        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.SaleOrderId);
        // §17.2 — SaleOrderLines (variant, status).
        b.HasIndex(x => new { x.VariantUuid, x.Status });
    }
}
