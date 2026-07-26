using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Domain;

namespace SMS.Modules.Finance.Data;

internal sealed class FinanceDbContext : DbContext
{
    public FinanceDbContext(DbContextOptions<FinanceDbContext> options) : base(options) { }

    internal DbSet<Invoice>     Invoices     => Set<Invoice>();
    internal DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
    internal DbSet<Payment>     Payments     => Set<Payment>();
    internal DbSet<CreditNote>  CreditNotes  => Set<CreditNote>();
    internal DbSet<DebitNote>   DebitNotes   => Set<DebitNote>();
    internal DbSet<SupplierLedgerEntry> SupplierLedgerEntries => Set<SupplierLedgerEntry>();
    internal DbSet<MasterFinancialLedger> MasterFinancialLedgers => Set<MasterFinancialLedger>();
    internal DbSet<MasterProductLedger> MasterProductLedgers => Set<MasterProductLedger>();
    internal DbSet<DebtWriteOff> DebtWriteOffs => Set<DebtWriteOff>();
    internal DbSet<SupplierPayment>     SupplierPayments      => Set<SupplierPayment>();
    internal DbSet<SupplierPaymentLine> SupplierPaymentLines  => Set<SupplierPaymentLine>();
    internal DbSet<SupplierAdvancePayment> SupplierAdvancePayments => Set<SupplierAdvancePayment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("finance");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FinanceDbContext).Assembly);
    }
}
