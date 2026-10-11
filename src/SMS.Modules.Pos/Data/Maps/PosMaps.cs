using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Pos.Domain;

namespace SMS.Modules.Pos.Data.Maps;

// A38 A1–A12 (D-1). Money decimal(18,2) in the org sale base; quantities decimal(18,4); rates decimal(9,4).
// Cross-module references are Guid scalars without FKs; users are int auth user ids.

internal static class PosColumn
{
    public const string Money = "decimal(18,2)", Quantity = "decimal(18,4)", Rate = "decimal(9,4)";
}

internal sealed class PosStoreMap : IEntityTypeConfiguration<PosStore>
{
    public void Configure(EntityTypeBuilder<PosStore> b)
    {
        b.ToTable("stores");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.Code).HasMaxLength(20).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.WarehouseName).HasMaxLength(200).IsRequired();
        // POS-STR-02 / D-3 — a warehouse backs at most one active store.
        b.HasIndex(x => new { x.OrganizationId, x.WarehouseUuid }).IsUnique().HasFilter("[IsActive] = 1");
        b.Property(x => x.AddressLine1).HasMaxLength(200);
        b.Property(x => x.AddressLine2).HasMaxLength(200);
        b.Property(x => x.City).HasMaxLength(100);
        b.Property(x => x.StateProvince).HasMaxLength(100);
        b.Property(x => x.PostalCode).HasMaxLength(20);
        b.Property(x => x.Country).HasMaxLength(100);
        b.Property(x => x.Phone).HasMaxLength(50);
        b.Property(x => x.Email).HasMaxLength(200);
        b.Property(x => x.Timezone).HasMaxLength(64).IsRequired();
        b.Property(x => x.DefaultTaxCode).HasMaxLength(50);
        b.Property(x => x.DefaultTaxPercent).HasColumnType(PosColumn.Rate);
        b.Property(x => x.ReceiptHeader).HasMaxLength(1000);
        b.Property(x => x.ReceiptFooter).HasMaxLength(1000);
        b.Property(x => x.OfflineCreditTolerancePct).HasColumnType(PosColumn.Rate);
        b.Property(x => x.VarianceThreshold).HasColumnType(PosColumn.Money);
        b.Property(x => x.RowVersion).IsRowVersion();

        b.HasMany(x => x.Terminals).WithOne(x => x.Store).HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Staff).WithOne(x => x.Store).HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PosTerminalMap : IEntityTypeConfiguration<PosTerminal>
{
    public void Configure(EntityTypeBuilder<PosTerminal> b)
    {
        b.ToTable("terminals");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.Code).HasMaxLength(20).IsRequired();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.StoreId, x.Code }).IsUnique();
        // One terminal per device.
        b.HasIndex(x => new { x.OrganizationId, x.DeviceUuid }).IsUnique().HasFilter("[DeviceUuid] IS NOT NULL");
        // D-17 (POSA) — app-rotated stamp, see PosTerminal.RowVersion.
        b.Property(x => x.RowVersion).HasColumnType("binary(8)").IsConcurrencyToken().IsRequired();
    }
}

internal sealed class PosStoreStaffMap : IEntityTypeConfiguration<PosStoreStaff>
{
    public void Configure(EntityTypeBuilder<PosStoreStaff> b)
    {
        b.ToTable("store_staff");
        b.HasKey(x => x.Id);
        b.Property(x => x.PosRole).HasMaxLength(20).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.StoreId, x.UserId }).IsUnique();
        b.HasIndex(x => new { x.OrganizationId, x.UserId });
    }
}

internal sealed class PosShiftMap : IEntityTypeConfiguration<PosShift>
{
    public void Configure(EntityTypeBuilder<PosShift> b)
    {
        b.ToTable("shifts");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.ShiftNumber).HasMaxLength(50).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.ShiftNumber }).IsUnique();
        b.Property(x => x.BusinessDate).HasColumnType("date");
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        foreach (var money in new[]
        {
            nameof(PosShift.OpeningFloat), nameof(PosShift.TotalCashSales), nameof(PosShift.TotalCardSales), nameof(PosShift.TotalMobileSales),
            nameof(PosShift.TotalOtherSales), nameof(PosShift.TotalCreditSales), nameof(PosShift.TotalCashRefunds), nameof(PosShift.TotalOtherRefunds),
            nameof(PosShift.TotalCashIn), nameof(PosShift.TotalCashOut), nameof(PosShift.ExpectedCashClosing), nameof(PosShift.ActualCashClosing),
            nameof(PosShift.CashVariance), nameof(PosShift.VarianceThreshold), nameof(PosShift.GrossTotal), nameof(PosShift.NetTotal),
            nameof(PosShift.TotalStoreCreditIssued), nameof(PosShift.TotalStoreCreditRedeemed)
        })
            b.Property(money).HasColumnType(PosColumn.Money);
        b.Property(x => x.VarianceNote).HasMaxLength(1000);
        b.Property(x => x.ReconcileNotes).HasMaxLength(4000);
        b.Property(x => x.RowVersion).IsRowVersion();
        // POS-SHF-01 — at most one OPEN/CLOSING shift per terminal.
        b.HasIndex(x => x.TerminalId).IsUnique().HasFilter("[Status] IN ('OPEN','CLOSING')").HasDatabaseName("UX_shifts_TerminalId_Active");
        b.HasIndex(x => new { x.OrganizationId, x.StoreId, x.OpenedAt });
        b.HasIndex(x => new { x.OrganizationId, x.Status });

        b.HasOne(x => x.Store).WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Terminal).WithMany().HasForeignKey(x => x.TerminalId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PosCashMovementMap : IEntityTypeConfiguration<PosCashMovement>
{
    public void Configure(EntityTypeBuilder<PosCashMovement> b)
    {
        b.ToTable("cash_movements");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.Direction).HasMaxLength(10).IsRequired();
        b.Property(x => x.Amount).HasColumnType(PosColumn.Money);
        b.Property(x => x.Reason).HasMaxLength(30).IsRequired();
        b.Property(x => x.Note).HasMaxLength(500);
        b.Property(x => x.LocalId).HasMaxLength(64);
        b.HasIndex(x => new { x.OrganizationId, x.LocalId }).IsUnique().HasFilter("[LocalId] IS NOT NULL");
        b.HasIndex(x => x.ShiftId);
        b.HasOne(x => x.Shift).WithMany().HasForeignKey(x => x.ShiftId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PosCashierSessionMap : IEntityTypeConfiguration<PosCashierSession>
{
    public void Configure(EntityTypeBuilder<PosCashierSession> b)
    {
        b.ToTable("cashier_sessions");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.EndReason).HasMaxLength(20);
        b.Property(x => x.LocalId).HasMaxLength(64);
        b.HasIndex(x => new { x.OrganizationId, x.LocalId }).IsUnique().HasFilter("[LocalId] IS NOT NULL");
        b.HasIndex(x => new { x.TerminalId, x.IsActive });
        b.HasIndex(x => x.ShiftId);
        b.HasOne(x => x.Shift).WithMany().HasForeignKey(x => x.ShiftId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PosTerminal>().WithMany().HasForeignKey(x => x.TerminalId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PosOrderMap : IEntityTypeConfiguration<PosOrder>
{
    public void Configure(EntityTypeBuilder<PosOrder> b)
    {
        b.ToTable("orders");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.OrderNumber).HasMaxLength(50).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.OrderNumber }).IsUnique();
        b.Property(x => x.LocalId).HasMaxLength(64).IsRequired();
        // POS-SYN-05 — a pushed order is stored once.
        b.HasIndex(x => new { x.OrganizationId, x.LocalId }).IsUnique();
        b.Property(x => x.OfflineNumber).HasMaxLength(50);
        b.Property(x => x.OrderType).HasMaxLength(10).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.CustomerCode).HasMaxLength(50).IsRequired();
        b.Property(x => x.CustomerName).HasMaxLength(300).IsRequired();
        b.Property(x => x.DiscountType).HasMaxLength(10).IsRequired();
        b.Property(x => x.DiscountValue).HasColumnType(PosColumn.Quantity);
        foreach (var money in new[]
        {
            nameof(PosOrder.Subtotal), nameof(PosOrder.DiscountAmount), nameof(PosOrder.TaxTotal), nameof(PosOrder.GrandTotal),
            nameof(PosOrder.PaidAmount), nameof(PosOrder.ChangeAmount), nameof(PosOrder.BalanceDue), nameof(PosOrder.ClientGrandTotal),
            nameof(PosOrder.RestockingFeeAmount)
        })
            b.Property(money).HasColumnType(PosColumn.Money);
        b.Property(x => x.PaymentStatus).HasMaxLength(20).IsRequired();
        b.Property(x => x.TaxCode).HasMaxLength(50);
        b.Property(x => x.TaxPercent).HasColumnType(PosColumn.Rate);
        b.Property(x => x.ReturnReason).HasMaxLength(500);
        b.Property(x => x.VoidReason).HasMaxLength(500);
        b.Property(x => x.Note).HasMaxLength(1000);
        b.Property(x => x.Flags).HasMaxLength(200);
        b.Property(x => x.PostingError).HasMaxLength(1000);
        b.Property(x => x.SalesInvoiceNumber).HasMaxLength(50);
        b.Property(x => x.CreditNoteNumber).HasMaxLength(50);
        b.Property(x => x.BusinessDate).HasColumnType("date");
        b.Property(x => x.RowVersion).IsRowVersion();
        b.Ignore(x => x.FlagList);
        b.HasIndex(x => new { x.OrganizationId, x.StoreId, x.CreatedAt });
        b.HasIndex(x => x.ShiftId);
        b.HasIndex(x => x.SessionId).HasFilter("[SessionId] IS NOT NULL");
        b.HasIndex(x => new { x.OrganizationId, x.CustomerUuid });
        b.HasIndex(x => x.OriginalOrderId).HasFilter("[OriginalOrderId] IS NOT NULL");
        // PosPostingRetryJob — orders whose stock or finance posting is still pending (a filtered index cannot use OR).
        b.HasIndex(x => new { x.StockPosted, x.FinancePosted }).HasDatabaseName("IX_orders_PostingPending");
        // A39 — weekly cross-store report.
        b.HasIndex(x => new { x.OrganizationId, x.IsCrossStore, x.CreatedAt }).HasFilter("[IsCrossStore] = 1");
        b.HasOne<PosReturnReason>().WithMany().HasForeignKey(x => x.ReturnReasonId).OnDelete(DeleteBehavior.Restrict);

        b.HasOne(x => x.Shift).WithMany().HasForeignKey(x => x.ShiftId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PosStore>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PosTerminal>().WithMany().HasForeignKey(x => x.TerminalId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<PosCashierSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.OriginalOrder).WithMany().HasForeignKey(x => x.OriginalOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Lines).WithOne(x => x.Order).HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(x => x.Payments).WithOne(x => x.Order).HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PosOrderLineMap : IEntityTypeConfiguration<PosOrderLine>
{
    public void Configure(EntityTypeBuilder<PosOrderLine> b)
    {
        b.ToTable("order_lines");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.LocalLineId).HasMaxLength(64);
        b.Property(x => x.ProductName).HasMaxLength(300).IsRequired();
        b.Property(x => x.VariantName).HasMaxLength(200);
        b.Property(x => x.Sku).HasMaxLength(100).IsRequired();
        b.Property(x => x.Barcode).HasMaxLength(100);
        b.Property(x => x.Quantity).HasColumnType(PosColumn.Quantity);
        b.Property(x => x.ReturnedQuantity).HasColumnType(PosColumn.Quantity);
        b.Property(x => x.DiscountValue).HasColumnType(PosColumn.Quantity);
        b.Property(x => x.TaxPercent).HasColumnType(PosColumn.Rate);
        foreach (var money in new[]
        {
            nameof(PosOrderLine.UnitPrice), nameof(PosOrderLine.CatalogPrice), nameof(PosOrderLine.DiscountAmount),
            nameof(PosOrderLine.NetAmount), nameof(PosOrderLine.TaxAmount), nameof(PosOrderLine.LineTotal)
        })
            b.Property(money).HasColumnType(PosColumn.Money);
        b.Property(x => x.DiscountType).HasMaxLength(10).IsRequired();
        b.Property(x => x.Note).HasMaxLength(500);
        b.HasIndex(x => new { x.OrderId, x.LineNumber });
        b.HasIndex(x => new { x.OrganizationId, x.VariantUuid });
        b.HasOne<PosOrderLine>().WithMany().HasForeignKey(x => x.OriginalLineId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PosPaymentMap : IEntityTypeConfiguration<PosPayment>
{
    public void Configure(EntityTypeBuilder<PosPayment> b)
    {
        b.ToTable("payments");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.LocalId).HasMaxLength(64);
        b.HasIndex(x => new { x.OrganizationId, x.LocalId }).IsUnique().HasFilter("[LocalId] IS NOT NULL");
        b.Property(x => x.Method).HasMaxLength(20).IsRequired();
        b.Property(x => x.Amount).HasColumnType(PosColumn.Money);
        b.Property(x => x.ChangeGiven).HasColumnType(PosColumn.Money);
        b.Property(x => x.Reference).HasMaxLength(100);
        b.HasIndex(x => new { x.ShiftId, x.Method });
        b.HasOne<PosShift>().WithMany().HasForeignKey(x => x.ShiftId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.CreditNote).WithMany().HasForeignKey(x => x.CreditNoteId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PosProductSettingsMap : IEntityTypeConfiguration<PosProductSettings>
{
    public void Configure(EntityTypeBuilder<PosProductSettings> b)
    {
        b.ToTable("product_pos_settings");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.OrganizationId, x.ProductUuid }).IsUnique();
        b.HasIndex(x => new { x.OrganizationId, x.ModifiedAt });
        b.Property(x => x.PosName).HasMaxLength(100);
        b.Property(x => x.Color).HasMaxLength(7);
        b.Property(x => x.ImageUrl).HasMaxLength(500);
    }
}

internal sealed class PosCustomerSettingsMap : IEntityTypeConfiguration<PosCustomerSettings>
{
    public void Configure(EntityTypeBuilder<PosCustomerSettings> b)
    {
        b.ToTable("customer_pos_settings");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.OrganizationId, x.CustomerUuid }).IsUnique();
        b.HasIndex(x => new { x.OrganizationId, x.ModifiedAt });
        b.Property(x => x.OfflineCreditLimitOverride).HasColumnType(PosColumn.Money);
        b.Property(x => x.MaxSingleCreditOrder).HasColumnType(PosColumn.Money);
        b.Property(x => x.Notes).HasMaxLength(1000);
    }
}

internal sealed class PosNumberSequenceMap : IEntityTypeConfiguration<PosNumberSequence>
{
    public void Configure(EntityTypeBuilder<PosNumberSequence> b)
    {
        b.ToTable("number_sequences");
        b.HasKey(x => x.Id);
        b.Property(x => x.Prefix).HasMaxLength(10).IsRequired();
        b.Property(x => x.BusinessDate).HasColumnType("date");
        b.HasIndex(x => new { x.OrganizationId, x.StoreId, x.Prefix, x.BusinessDate }).IsUnique();
        b.HasOne<PosStore>().WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Restrict);
    }
}
