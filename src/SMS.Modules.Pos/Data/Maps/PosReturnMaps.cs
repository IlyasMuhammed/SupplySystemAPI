using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Pos.Domain;

namespace SMS.Modules.Pos.Data.Maps;

// A39 M039.1/.2/.10/.11 + authorization tokens (D-8). Money decimal(18,2); percentages decimal(9,4).

internal sealed class PosReturnReasonMap : IEntityTypeConfiguration<PosReturnReason>
{
    public void Configure(EntityTypeBuilder<PosReturnReason> b)
    {
        b.ToTable("return_reasons");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.Code).HasMaxLength(20).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.Code }).IsUnique();
        b.HasIndex(x => new { x.OrganizationId, x.ModifiedAt });
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.AppliesTo).HasMaxLength(10).IsRequired();
        b.Property(x => x.RestockingFeeOverride).HasColumnType(PosColumn.Rate);
    }
}

internal sealed class PosReturnPolicyMap : IEntityTypeConfiguration<PosReturnPolicy>
{
    public void Configure(EntityTypeBuilder<PosReturnPolicy> b)
    {
        b.ToTable("return_policies");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.Name).HasMaxLength(100).IsRequired();
        b.Property(x => x.RestockingFeePct).HasColumnType(PosColumn.Rate);
        b.Property(x => x.MaxReturnNoAuth).HasColumnType(PosColumn.Money);
        b.Property(x => x.RowVersion).IsRowVersion();
        // D-4 — one override per store, one default (StoreId NULL) per organization.
        b.HasIndex(x => new { x.OrganizationId, x.StoreId }).IsUnique().HasFilter("[StoreId] IS NOT NULL")
            .HasDatabaseName("UX_return_policies_Store");
        b.HasIndex(x => x.OrganizationId).IsUnique().HasFilter("[StoreId] IS NULL").HasDatabaseName("UX_return_policies_OrgDefault");
        b.HasOne(x => x.Store).WithMany().HasForeignKey(x => x.StoreId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PosStoreCreditNoteMap : IEntityTypeConfiguration<PosStoreCreditNote>
{
    public void Configure(EntityTypeBuilder<PosStoreCreditNote> b)
    {
        b.ToTable("store_credit_notes", t => t.HasCheckConstraint("CK_store_credit_notes_Balance", "[RemainingBalance] >= 0"));
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.Number).HasMaxLength(50).IsRequired();
        b.HasIndex(x => new { x.OrganizationId, x.Number }).IsUnique();
        b.Property(x => x.OfflineNumber).HasMaxLength(50);
        b.HasIndex(x => new { x.OrganizationId, x.OfflineNumber }).HasFilter("[OfflineNumber] IS NOT NULL");
        b.Property(x => x.LocalId).HasMaxLength(64);
        b.HasIndex(x => new { x.OrganizationId, x.LocalId }).IsUnique().HasFilter("[LocalId] IS NOT NULL");
        b.Property(x => x.CustomerName).HasMaxLength(300);
        b.HasIndex(x => new { x.OrganizationId, x.CustomerUuid });
        b.HasIndex(x => new { x.OrganizationId, x.Status, x.ExpiresAt });
        b.Property(x => x.OriginalAmount).HasColumnType(PosColumn.Money);
        b.Property(x => x.RemainingBalance).HasColumnType(PosColumn.Money);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.Note).HasMaxLength(500);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne<PosStore>().WithMany().HasForeignKey(x => x.IssuedStoreId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.ReturnOrder).WithMany().HasForeignKey(x => x.ReturnOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(x => x.Transactions).WithOne(x => x.CreditNote).HasForeignKey(x => x.CreditNoteId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PosStoreCreditTransactionMap : IEntityTypeConfiguration<PosStoreCreditTransaction>
{
    public void Configure(EntityTypeBuilder<PosStoreCreditTransaction> b)
    {
        b.ToTable("store_credit_transactions");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.Uuid).IsUnique();
        b.Property(x => x.Type).HasMaxLength(10).IsRequired();
        b.Property(x => x.Amount).HasColumnType(PosColumn.Money);
        b.Property(x => x.BalanceAfter).HasColumnType(PosColumn.Money);
        b.Property(x => x.Note).HasMaxLength(500);
        b.HasIndex(x => x.CreditNoteId);
        b.HasOne(x => x.Order).WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PosReturnAuthorizationMap : IEntityTypeConfiguration<PosReturnAuthorization>
{
    public void Configure(EntityTypeBuilder<PosReturnAuthorization> b)
    {
        b.ToTable("return_authorizations");
        b.HasKey(x => x.Id);
        b.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.Property(x => x.AmountCeiling).HasColumnType(PosColumn.Money);
        b.Property(x => x.UsedAt).IsConcurrencyToken();
        b.HasIndex(x => new { x.OrganizationId, x.ExpiresAt });
    }
}
