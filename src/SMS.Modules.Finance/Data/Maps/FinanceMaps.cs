using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Finance.Domain;

namespace SMS.Modules.Finance.Data.Maps;

internal sealed class InvoiceMap : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> b)
    {
        b.ToTable("invoices");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.TraceId).IsRequired().ValueGeneratedOnAdd().HasDefaultValueSql("NEWSEQUENTIALID()");
        b.HasIndex(x => x.TraceId);
        b.Property(x => x.InvoiceNumber).HasMaxLength(20).IsRequired();
        b.HasIndex(x => x.InvoiceNumber).IsUnique();
        b.Property(x => x.SupplierInvoiceNo).HasMaxLength(50);
        b.Property(x => x.SupplierName).HasMaxLength(200).IsRequired();
        b.Property(x => x.PoNumber).HasMaxLength(20).IsRequired();
        b.Property(x => x.GrnNumber).HasMaxLength(20);
        b.Property(x => x.Currency).HasMaxLength(10).HasDefaultValue("PKR");
        b.Property(x => x.Subtotal).HasColumnType("decimal(18,2)");
        b.Property(x => x.TaxAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.TotalAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.MatchedPoValue).HasColumnType("decimal(18,2)");
        b.Property(x => x.MatchedGrnValue).HasColumnType("decimal(18,2)");
        b.Property(x => x.VarianceAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.MatchStatus).HasMaxLength(20).HasDefaultValue("Pending");
        b.Property(x => x.PaymentStatus).HasMaxLength(20).HasDefaultValue("Unpaid");
        b.Property(x => x.PaidAmount).HasColumnType("decimal(18,2)").HasDefaultValue(0m);
        b.Property(x => x.PaymentMethod).HasMaxLength(50);
        b.Property(x => x.Notes).HasMaxLength(300);
        b.Property(x => x.AttachmentUrl).HasMaxLength(500);
        b.Property(x => x.IsActive).HasDefaultValue(true);

        b.HasMany(x => x.Lines)
         .WithOne(x => x.Invoice)
         .HasForeignKey(x => x.InvoiceId)
         .OnDelete(DeleteBehavior.Cascade);

        b.HasMany(x => x.Payments)
         .WithOne(x => x.Invoice)
         .HasForeignKey(x => x.InvoiceId)
         .OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class InvoiceLineMap : IEntityTypeConfiguration<InvoiceLine>
{
    public void Configure(EntityTypeBuilder<InvoiceLine> b)
    {
        b.ToTable("invoice_lines");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.ItemDescription).HasMaxLength(300).IsRequired();
        b.Property(x => x.UnitOfMeasure).HasMaxLength(20);
        b.Property(x => x.QtyInvoiced).HasColumnType("decimal(18,4)");
        b.Property(x => x.UnitPrice).HasColumnType("decimal(18,2)");
        b.Property(x => x.LineTotal).HasColumnType("decimal(18,2)");
    }
}

internal sealed class DebitNoteMap : IEntityTypeConfiguration<DebitNote>
{
    public void Configure(EntityTypeBuilder<DebitNote> b)
    {
        b.ToTable("debit_notes");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.DebitNoteNumber).HasMaxLength(20).IsRequired();
        b.HasIndex(x => x.DebitNoteNumber).IsUnique();
        b.Property(x => x.SroNumber).HasMaxLength(25).IsRequired();
        b.Property(x => x.SupplierName).HasMaxLength(200).IsRequired();
        b.Property(x => x.SupplierContactEmail).HasMaxLength(200);
        b.Property(x => x.DebitReason).HasMaxLength(50).IsRequired();
        b.Property(x => x.DebitReasonDetail).HasMaxLength(500);
        b.Property(x => x.DebitAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.Status).HasMaxLength(20).HasDefaultValue("ISSUED");
        b.Property(x => x.DisputeNotes).HasMaxLength(500);
        b.Property(x => x.Notes).HasMaxLength(300);
        b.Property(x => x.IsActive).HasDefaultValue(true);
    }
}

internal sealed class SupplierLedgerEntryMap : IEntityTypeConfiguration<SupplierLedgerEntry>
{
    public void Configure(EntityTypeBuilder<SupplierLedgerEntry> b)
    {
        b.ToTable("supplier_ledger_entries");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.SupplierId).IsRequired();
        // Concurrency guard for PostEntryAsync — see comment on the entity.
        b.HasIndex(x => new { x.SupplierId, x.SequenceNo }).IsUnique();
        b.HasIndex(x => new { x.SupplierId, x.EntryDate });
        b.Property(x => x.TransactionType).HasMaxLength(30).IsRequired();
        b.Property(x => x.ReferenceType).HasMaxLength(30).IsRequired();
        b.Property(x => x.ReferenceNo).HasMaxLength(30).IsRequired();
        b.Property(x => x.DebitAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.CreditAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.BalanceAfter).HasColumnType("decimal(18,2)");
        b.Property(x => x.Narration).HasMaxLength(500);
    }
}

internal sealed class SupplierPaymentMap : IEntityTypeConfiguration<SupplierPayment>
{
    public void Configure(EntityTypeBuilder<SupplierPayment> b)
    {
        b.ToTable("supplier_payments");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.PaymentNumber).HasMaxLength(20).IsRequired();
        b.HasIndex(x => x.PaymentNumber).IsUnique();
        b.Property(x => x.SupplierId).IsRequired();
        b.HasIndex(x => x.SupplierId);
        b.Property(x => x.SupplierName).HasMaxLength(200).IsRequired();
        b.Property(x => x.PaymentMethod).HasMaxLength(20).IsRequired();
        b.Property(x => x.TotalAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.BankAccount).HasMaxLength(100);
        b.Property(x => x.ChequeNo).HasMaxLength(30);
        b.Property(x => x.Status).HasMaxLength(20).HasDefaultValue("DRAFT");
        b.Property(x => x.Notes).HasMaxLength(300);
        b.Property(x => x.PaymentType).HasMaxLength(30).HasDefaultValue("STANDARD");

        b.HasMany(x => x.Lines)
         .WithOne(x => x.SupplierPayment)
         .HasForeignKey(x => x.SupplierPaymentId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class SupplierAdvancePaymentMap : IEntityTypeConfiguration<SupplierAdvancePayment>
{
    public void Configure(EntityTypeBuilder<SupplierAdvancePayment> b)
    {
        b.ToTable("supplier_advance_payments");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.SupplierId).IsRequired();
        b.HasIndex(x => x.SupplierId);
        b.Property(x => x.SupplierPaymentUuid).IsRequired();
        b.Property(x => x.OriginalAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.AvailableBalance).HasColumnType("decimal(18,2)");
    }
}

internal sealed class SupplierPaymentLineMap : IEntityTypeConfiguration<SupplierPaymentLine>
{
    public void Configure(EntityTypeBuilder<SupplierPaymentLine> b)
    {
        b.ToTable("supplier_payment_lines");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.InvoiceUuid).IsRequired();
        b.HasIndex(x => x.InvoiceUuid);
        b.Property(x => x.InvoiceNumber).HasMaxLength(20).IsRequired();
        b.Property(x => x.AllocatedAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.OutstandingBeforeAllocation).HasColumnType("decimal(18,2)");
        b.Property(x => x.Notes).HasMaxLength(300);
    }
}

internal sealed class CreditNoteMap : IEntityTypeConfiguration<CreditNote>
{
    public void Configure(EntityTypeBuilder<CreditNote> b)
    {
        b.ToTable("credit_notes");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.CreditNoteNumber).HasMaxLength(20).IsRequired();
        b.HasIndex(x => x.CreditNoteNumber).IsUnique();
        b.Property(x => x.SupplierCreditNoteNo).HasMaxLength(100).IsRequired();
        b.Property(x => x.SroNumber).HasMaxLength(25).IsRequired();
        b.Property(x => x.SupplierName).HasMaxLength(200).IsRequired();
        b.Property(x => x.InvoiceNumber).HasMaxLength(20);
        b.Property(x => x.CreditAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.ApplicationStatus).HasMaxLength(30).HasDefaultValue("PENDING");
        b.Property(x => x.AppliedToInvoiceNumber).HasMaxLength(20);
        b.Property(x => x.CarriedForwardAmount).HasColumnType("decimal(18,2)");
        b.Property(x => x.Notes).HasMaxLength(300);
        b.Property(x => x.IsActive).HasDefaultValue(true);
    }
}

internal sealed class PaymentMap : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> b)
    {
        b.ToTable("payments");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();
        b.Property(x => x.PaymentNumber).HasMaxLength(20).IsRequired();
        b.HasIndex(x => x.PaymentNumber).IsUnique();
        b.Property(x => x.SupplierName).HasMaxLength(200).IsRequired();
        b.Property(x => x.AmountPaid).HasColumnType("decimal(18,2)");
        b.Property(x => x.PaymentMethod).HasMaxLength(50).IsRequired();
        b.Property(x => x.BankReference).HasMaxLength(100);
        b.Property(x => x.ChequeNumber).HasMaxLength(30);
        b.Property(x => x.AccountDebited).HasMaxLength(100);
        b.Property(x => x.Status).HasMaxLength(20).HasDefaultValue("Pending");
        b.Property(x => x.Notes).HasMaxLength(200);
        b.Property(x => x.IsActive).HasDefaultValue(true);
    }
}
