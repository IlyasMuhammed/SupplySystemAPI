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
        // D-20 — the setup history is an audit trail: written once.
        if (ChangeTracker.Entries<GlSetupHistory>().Any(e => e.State is EntityState.Modified))
            throw new InvalidOperationException("GL setup history is immutable.");
    }

    /// <summary>
    /// Runs <paramref name="work"/> in one database transaction (through the retrying execution strategy, which refuses
    /// user transactions otherwise). The in-memory test provider has no transactions: the work just runs.
    /// </summary>
    internal async Task InTransactionAsync(Func<Task> work, CancellationToken ct = default)
    {
        if (!Database.IsRelational())
        {
            await work();
            return;
        }
        var strategy = Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await Database.BeginTransactionAsync(ct);
            await work();
            await tx.CommitAsync(ct);
        });
    }
}
