using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SMS.Modules.Finance.Domain;

namespace SMS.Modules.Finance.Data.Maps;

// A29-P7-02 §9.3/§9.4.

internal sealed class CustomerPaymentMap : IEntityTypeConfiguration<CustomerPayment>
{
    public void Configure(EntityTypeBuilder<CustomerPayment> b)
    {
        b.ToTable("customer_payments");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        b.Property(x => x.PaymentNumber).HasMaxLength(25).IsRequired();
        // Composite, not global — each org numbers its own receipts, as every Finance document does.
        b.HasIndex(x => new { x.OrganizationId, x.PaymentNumber }).IsUnique();

        b.Property(x => x.PartnerName).HasMaxLength(200).IsRequired();
        // The customer's statement: their receipts, in date order.
        b.HasIndex(x => new { x.OrganizationId, x.PartnerId, x.PaymentDate });

        b.Property(x => x.Amount).HasColumnType("decimal(18,2)");
        b.Property(x => x.PaymentMethod).HasMaxLength(20).IsRequired();
        b.Property(x => x.ChequeNumber).HasMaxLength(30);
        b.Property(x => x.BankReference).HasMaxLength(100);
        b.Property(x => x.CurrencyCode).HasMaxLength(10).IsRequired().HasDefaultValue("PKR");
        b.Property(x => x.Notes).HasMaxLength(500);
        b.Property(x => x.Status).HasMaxLength(20).IsRequired().HasDefaultValue(CustomerPaymentStatuses.Received);

        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);

        b.HasMany(x => x.Allocations)
         .WithOne(x => x.CustomerPayment)
         .HasForeignKey(x => x.CustomerPaymentId)
         .OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PaymentAllocationMap : IEntityTypeConfiguration<PaymentAllocation>
{
    public void Configure(EntityTypeBuilder<PaymentAllocation> b)
    {
        b.ToTable("payment_allocations");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();

        b.Property(x => x.UUID).IsRequired();
        b.HasIndex(x => x.UUID).IsUnique();

        // "What has been paid against this invoice" — the balance an invoice's status hangs off.
        b.HasIndex(x => x.SalesInvoiceId);

        b.Property(x => x.AllocatedAmount).HasColumnType("decimal(18,2)");

        b.Property(x => x.OrganizationId).IsRequired();
        b.HasIndex(x => x.OrganizationId);

        // Restrict, not cascade: money applied to an invoice pins the invoice. Undo the allocation
        // (or reverse the payment) first.
        b.HasOne(x => x.SalesInvoice)
         .WithMany(x => x.Allocations)
         .HasForeignKey(x => x.SalesInvoiceId)
         .OnDelete(DeleteBehavior.Restrict);
    }
}
