using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.Data;

internal sealed class FinanceDbContext : DbContext, ITenantScopedDbContext
{
    private readonly ITenantContext _tenantContext;
    public ITenantContext TenantContext => _tenantContext;

    public FinanceDbContext(DbContextOptions<FinanceDbContext> options, ITenantContext tenantContext) : base(options) =>
        _tenantContext = tenantContext;

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
        modelBuilder.ApplyTenantQueryFilters(this);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        this.StampTenantScopedEntities(_tenantContext);
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        this.StampTenantScopedEntities(_tenantContext);
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }
}
