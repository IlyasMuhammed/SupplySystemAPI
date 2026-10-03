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

        // A32 C3 — source linking and the customer's PO (§5.2).
        b.Property(x => x.SourceType).HasMaxLength(20).IsRequired().HasDefaultValue("MANUAL");
        b.Property(x => x.CustomerPoReference).HasMaxLength(50);
        b.Property(x => x.CustomerPoDate).HasColumnType("date");
        b.HasIndex(x => new { x.OrganizationId, x.CustomerPoReference }).HasFilter("[CustomerPoReference] IS NOT NULL");
        // Unique, not the spec's plain index: a quotation converts to at most one order (§2.1 "0..1"), and this is what
        // makes a second, concurrent conversion fail rather than create a twin.
        b.HasOne<SaleQuotation>()
         .WithMany()
         .HasForeignKey(x => x.SourceQuotationId)
         .OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(x => x.SourceQuotationId).IsUnique().HasFilter("[SourceQuotationId] IS NOT NULL");
        b.HasOne<SaleInquiry>()
         .WithMany()
         .HasForeignKey(x => x.SourceInquiryId)
         .OnDelete(DeleteBehavior.NoAction);
        b.HasIndex(x => x.SourceInquiryId).HasFilter("[SourceInquiryId] IS NOT NULL");

        // A33 D-12: the sweep reads only the few orders still waiting for their deliveries.
        b.HasIndex(x => x.DeliveryCreationPendingSince).HasFilter("[DeliveryCreationPendingSince] IS NOT NULL");

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
        b.Property(x => x.TaxCode).HasMaxLength(20);
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

        // A33 C3: the route (override while DRAFT, snapshot after confirm). Filtered, for "is this route in use" (L-7).
        // Not led by OrganizationId on purpose: a filtered index leading with it would make ApplyTenantIndexes drop the
        // table's plain OrganizationId index, which the tenant filter's queries need (and dropping is unsafe on SMSGlobal).
        b.Property(x => x.FulfillmentRouteCode).HasMaxLength(30);
        b.Property(x => x.RouteSource).HasMaxLength(20);
        b.HasIndex(x => x.FulfillmentRouteUuid).HasFilter("[FulfillmentRouteUuid] IS NOT NULL");
    }
}
