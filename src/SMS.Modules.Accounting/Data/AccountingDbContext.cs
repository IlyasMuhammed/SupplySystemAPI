using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Accounting.Data;

/// <summary>
/// A42 — the GL setup tables in schema <c>finance</c> (D-2). The tenant filter applies to every entity (CoaTemplates: system
/// rows to everyone, custom rows to their organization — IGloballyExemptTenantScopedEntity); services also filter on the
/// organization explicitly, because a super admin bypasses the filter.
/// </summary>
internal sealed class AccountingDbContext : DbContext, ITenantScopedDbContext
{
    public const string Schema = "finance";

    private readonly ITenantContext _tenantContext;
    public ITenantContext TenantContext => _tenantContext;

    public AccountingDbContext(DbContextOptions<AccountingDbContext> options, ITenantContext tenantContext) : base(options) =>
        _tenantContext = tenantContext;

    internal DbSet<CoaTemplate>           CoaTemplates           => Set<CoaTemplate>();
    internal DbSet<CoaTemplateItem>       CoaTemplateItems       => Set<CoaTemplateItem>();
    internal DbSet<ChartOfAccount>        ChartOfAccounts        => Set<ChartOfAccount>();
    internal DbSet<FiscalYear>            FiscalYears            => Set<FiscalYear>();
    internal DbSet<FiscalPeriod>          FiscalPeriods          => Set<FiscalPeriod>();
    internal DbSet<AccountingDefault>     AccountingDefaults     => Set<AccountingDefault>();
    internal DbSet<CoaSetupStatus>        CoaSetupStatuses       => Set<CoaSetupStatus>();
    internal DbSet<OpeningBalanceEntry>   OpeningBalanceEntries  => Set<OpeningBalanceEntry>();
    internal DbSet<JournalEntry>          JournalEntries         => Set<JournalEntry>();
    internal DbSet<JournalEntryLine>      JournalEntryLines      => Set<JournalEntryLine>();
    internal DbSet<ExternalSystemMapping> ExternalSystemMappings => Set<ExternalSystemMapping>();
    internal DbSet<GlSetupHistory>        GlSetupHistory         => Set<GlSetupHistory>();
    // A44 — posting log + finance posting settings.
    internal DbSet<FinancialPosting>            FinancialPostings       => Set<FinancialPosting>();
    internal DbSet<FinancialPostingTargetState> FinancialPostingTargets => Set<FinancialPostingTargetState>();
    internal DbSet<TenantFinanceConfig>         TenantFinanceConfigs    => Set<TenantFinanceConfig>();
    // A47 — journal engine.
    internal DbSet<JournalType>          JournalTypes          => Set<JournalType>();
    internal DbSet<JournalEntrySequence> JournalEntrySequences => Set<JournalEntrySequence>();
    internal DbSet<ReconcileGroup>       ReconcileGroups       => Set<ReconcileGroup>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AccountingDbContext).Assembly);
        modelBuilder.ApplyTenantQueryFilters(this);
        modelBuilder.ApplyTenantIndexes();
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        BeforeSave();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        BeforeSave();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void BeforeSave()
    {
        this.StampTenantScopedEntities(_tenantContext);
        this.StampModifiedAt();
        StampNewJournalDefaults();
        // D-20 — the setup history is an audit trail: written once.
        if (ChangeTracker.Entries<GlSetupHistory>().Any(e => e.State is EntityState.Modified))
            throw new InvalidOperationException("GL setup history is immutable.");
    }

    /// <summary>
    /// A47 D-3 — the derived line columns of a NEW journal line that its writer left empty (the A42 opening post and the
    /// A44 target build lines without them): the residual starts at Debit − Credit, the transaction currency is the
    /// header's (base) currency, and the header counts its lines. The journal engine sets them itself.
    /// </summary>
    private void StampNewJournalDefaults()
    {
        foreach (var e in ChangeTracker.Entries<JournalEntryLine>().Where(e => e.State == EntityState.Added))
        {
            var line = e.Entity;
            if (line.AmountResidual == 0 && !line.Reconciled && line.ReconcileGroupId is null)
                line.AmountResidual = line.DebitAmount - line.CreditAmount;
            if (line.AmountCurrency is null && line.CurrencyCode is null)
            {
                line.CurrencyCode = line.JournalEntry?.CurrencyCode;
                line.AmountCurrency = line.DebitAmount - line.CreditAmount;
            }
        }
        foreach (var e in ChangeTracker.Entries<JournalEntry>().Where(e => e.State == EntityState.Added && e.Entity.LineCount == 0))
            e.Entity.LineCount = e.Entity.Lines.Count;
    }

    /// <summary>
    /// Runs <paramref name="work"/> in one database transaction (through the retrying execution strategy, which refuses
    /// user transactions otherwise). The in-memory test provider has no transactions: the work just runs. A47 — inside a
    /// transaction already open on this context the work joins it (the journal engine is called from posting transactions).
    /// </summary>
    internal async Task InTransactionAsync(Func<Task> work, CancellationToken ct = default)
    {
        if (!Database.IsRelational() || Database.CurrentTransaction is not null)
        {
            await work();
            return;
        }
        var strategy = Database.CreateExecutionStrategy();
        // A47 REV-3 — a transient failure (e.g. a deadlock victim, 1205) re-runs the work: what the failed attempt started
        // tracking (new journals, lines, sequence rows …) is detached first, so the retry does not insert it twice. Entities
        // the caller tracked before the call stay (the work sets them again).
        var before = ChangeTracker.Entries().Select(e => e.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
        var attempt = 0;
        await strategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0)
                foreach (var e in ChangeTracker.Entries().Where(e => !before.Contains(e.Entity)).ToList()) e.State = EntityState.Detached;
            await using var tx = await Database.BeginTransactionAsync(ct);
            await work();
            await tx.CommitAsync(ct);
        });
    }
}
