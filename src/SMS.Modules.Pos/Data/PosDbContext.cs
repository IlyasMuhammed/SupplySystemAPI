using Microsoft.EntityFrameworkCore;
using SMS.Modules.Pos.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Pos.Data;

internal sealed class PosDbContext : DbContext, ITenantScopedDbContext
{
    public const string Schema = "pos";

    private readonly ITenantContext _tenantContext;
    public ITenantContext TenantContext => _tenantContext;

    public PosDbContext(DbContextOptions<PosDbContext> options, ITenantContext tenantContext) : base(options) =>
        _tenantContext = tenantContext;

    internal DbSet<PosStore>            Stores            => Set<PosStore>();
    internal DbSet<PosTerminal>         Terminals         => Set<PosTerminal>();
    internal DbSet<PosStoreStaff>       StoreStaff        => Set<PosStoreStaff>();
    internal DbSet<PosShift>            Shifts            => Set<PosShift>();
    internal DbSet<PosCashMovement>     CashMovements     => Set<PosCashMovement>();
    internal DbSet<PosCashierSession>   CashierSessions   => Set<PosCashierSession>();
    internal DbSet<PosOrder>            Orders            => Set<PosOrder>();
    internal DbSet<PosOrderLine>        OrderLines        => Set<PosOrderLine>();
    internal DbSet<PosPayment>          Payments          => Set<PosPayment>();
    internal DbSet<PosProductSettings>  ProductSettings   => Set<PosProductSettings>();
    internal DbSet<PosCustomerSettings> CustomerSettings  => Set<PosCustomerSettings>();
    internal DbSet<PosNumberSequence>   NumberSequences   => Set<PosNumberSequence>();
    // A39
    internal DbSet<PosReturnReason>           ReturnReasons           => Set<PosReturnReason>();
    internal DbSet<PosReturnPolicy>           ReturnPolicies          => Set<PosReturnPolicy>();
    internal DbSet<PosStoreCreditNote>        StoreCreditNotes        => Set<PosStoreCreditNote>();
    internal DbSet<PosStoreCreditTransaction> StoreCreditTransactions => Set<PosStoreCreditTransaction>();
    internal DbSet<PosReturnAuthorization>    ReturnAuthorizations    => Set<PosReturnAuthorization>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PosDbContext).Assembly);
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
        this.StampModifiedAt(); // A37 D-16 — app-maintained, no triggers
        // A38 A5 — a cash movement is written once; a correction is an opposite movement.
        if (ChangeTracker.Entries<PosCashMovement>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Cash movements are immutable; record an opposite movement instead.");
        // A39 — a store credit transaction is the audit trail of a balance change: written once.
        if (ChangeTracker.Entries<PosStoreCreditTransaction>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Store credit transactions are immutable.");
    }
}
