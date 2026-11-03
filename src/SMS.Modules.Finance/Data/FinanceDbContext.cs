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
    // A29-P7-01/P7-02 — the receivable side.
    internal DbSet<SalesInvoice>      SalesInvoices      => Set<SalesInvoice>();
    internal DbSet<SalesInvoiceLine>  SalesInvoiceLines  => Set<SalesInvoiceLine>();
    internal DbSet<CustomerPayment>   CustomerPayments   => Set<CustomerPayment>();
    internal DbSet<PaymentAllocation> PaymentAllocations => Set<PaymentAllocation>();
    internal DbSet<CustomerLedgerEntry> CustomerLedgerEntries => Set<CustomerLedgerEntry>();
    // A38 (FIN, D-10) — refunds paid out against customer credit notes.
    internal DbSet<CustomerRefund>    CustomerRefunds    => Set<CustomerRefund>();
    // A29-P8-01 — per-variant cost and revenue history.
    internal DbSet<ProductLedgerEntry> ProductLedgerEntries => Set<ProductLedgerEntry>();
    // SAP-alignment — finance master data (docs/finance/SAP-ALIGNMENT-PLAN.md).
    internal DbSet<TaxCode>      TaxCodes      => Set<TaxCode>();
    internal DbSet<ExchangeRate> ExchangeRates => Set<ExchangeRate>();
    // A35 — multi-currency (docs/multi-currency/ADDENDUM-35-ANALYSIS.md D-1, D-3, D-15).
    internal DbSet<OrgCurrency>        OrgCurrencies       => Set<OrgCurrency>();
    internal DbSet<CurrencyRate>       CurrencyRates       => Set<CurrencyRate>();
    internal DbSet<ExchangeDifference> ExchangeDifferences => Set<ExchangeDifference>();
    // A40 (TFIN, D-7) — per-organization Finance settings (the opening balance date).
    internal DbSet<FinanceOrgSettings> OrgSettings => Set<FinanceOrgSettings>();

    // A48 (CORE) — AR & AP sub-ledgers (docs/ar-ap-subledgers/API-CONTRACT.md §11).
    internal DbSet<PartnerFinancialProfile>   PartnerFinancialProfiles   => Set<PartnerFinancialProfile>();
    internal DbSet<CreditHoldRequest>         CreditHoldRequests         => Set<CreditHoldRequest>();
    internal DbSet<StatementLog>              StatementLogs              => Set<StatementLog>();
    internal DbSet<DunningProcedure>          DunningProcedures          => Set<DunningProcedure>();
    internal DbSet<DunningLevel>              DunningLevels              => Set<DunningLevel>();
    internal DbSet<DunningRun>                DunningRuns                => Set<DunningRun>();
    internal DbSet<DunningRunItem>            DunningRunItems            => Set<DunningRunItem>();
    internal DbSet<MatchToleranceRule>        MatchToleranceRules        => Set<MatchToleranceRule>();
    internal DbSet<InvoiceMatchLine>          InvoiceMatchLines          => Set<InvoiceMatchLine>();
    internal DbSet<EarlyPaymentTerm>          EarlyPaymentTerms          => Set<EarlyPaymentTerm>();
    internal DbSet<PaymentSchedule>           PaymentSchedules           => Set<PaymentSchedule>();
    internal DbSet<PaymentRun>                PaymentRuns                => Set<PaymentRun>();
    internal DbSet<PaymentRunBatch>           PaymentRunBatches          => Set<PaymentRunBatch>();
    internal DbSet<PaymentRunItem>            PaymentRunItems            => Set<PaymentRunItem>();
    internal DbSet<CustomerCreditApplication> CustomerCreditApplications => Set<CustomerCreditApplication>();
    internal DbSet<SupplierNoteApplication>   SupplierNoteApplications   => Set<SupplierNoteApplication>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("finance");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FinanceDbContext).Assembly);
        modelBuilder.ApplyTenantQueryFilters(this);
        
        // F35 — each of those filters puts WHERE OrganizationId = @org on every query against
        // every one of these tables, and none of them had an index leading with it.
        modelBuilder.ApplyTenantIndexes();
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
