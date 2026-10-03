using Microsoft.EntityFrameworkCore;
using SMS.Modules.Integration.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Integration.Data;

internal sealed class IntegrationDbContext : DbContext, ITenantScopedDbContext
{
    public const string Schema = "integration";

    private readonly ITenantContext _tenantContext;
    public ITenantContext TenantContext => _tenantContext;

    public IntegrationDbContext(DbContextOptions<IntegrationDbContext> options, ITenantContext tenantContext) : base(options) =>
        _tenantContext = tenantContext;

    internal DbSet<IntegrationConnection> Connections        => Set<IntegrationConnection>();
    internal DbSet<OAuthStateToken>       OAuthStateTokens   => Set<OAuthStateToken>();
    internal DbSet<ApiClient>             ApiClients         => Set<ApiClient>();
    internal DbSet<ApiClientKey>          ApiClientKeys      => Set<ApiClientKey>();
    internal DbSet<IntegrationSettings>   Settings           => Set<IntegrationSettings>();
    internal DbSet<SettingsAuditEntry>    SettingsAudit      => Set<SettingsAuditEntry>();
    internal DbSet<TaxCodeMapping>        TaxCodeMappings    => Set<TaxCodeMapping>();
    internal DbSet<PaymentTermMapping>    PaymentTermMappings => Set<PaymentTermMapping>();
    internal DbSet<ReferenceSnapshot>     ReferenceSnapshots => Set<ReferenceSnapshot>();
    internal DbSet<EntityMap>             EntityMaps         => Set<EntityMap>();
    internal DbSet<SyncOutboxEntry>       Outbox             => Set<SyncOutboxEntry>();
    internal DbSet<SyncCommandClaim>      CommandClaims      => Set<SyncCommandClaim>();
    internal DbSet<SyncLogEntry>          SyncLog            => Set<SyncLogEntry>();
    internal DbSet<MatchCandidate>        MatchCandidates    => Set<MatchCandidate>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(IntegrationDbContext).Assembly);
        modelBuilder.ApplyTenantQueryFilters(this);
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
