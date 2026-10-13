using Microsoft.EntityFrameworkCore;
using SMS.Modules.Crm.Domain;
using SMS.Shared.Common;

namespace SMS.Modules.Crm.Data;

/// <summary>A41 — schema <c>crm</c>. The tenant filter applies to every entity; services also filter on the organization explicitly.</summary>
internal sealed class CrmDbContext : DbContext, ITenantScopedDbContext
{
    public const string Schema = "crm";

    private readonly ITenantContext _tenantContext;
    public ITenantContext TenantContext => _tenantContext;

    public CrmDbContext(DbContextOptions<CrmDbContext> options, ITenantContext tenantContext) : base(options) =>
        _tenantContext = tenantContext;

    internal DbSet<CrmLeadSource>          LeadSources          => Set<CrmLeadSource>();
    internal DbSet<CrmCampaign>            Campaigns            => Set<CrmCampaign>();
    internal DbSet<CrmSalesTeam>           SalesTeams           => Set<CrmSalesTeam>();
    internal DbSet<CrmSalesTeamMember>     SalesTeamMembers     => Set<CrmSalesTeamMember>();
    internal DbSet<CrmPipelineStage>       PipelineStages       => Set<CrmPipelineStage>();
    internal DbSet<CrmLead>                Leads                => Set<CrmLead>();
    internal DbSet<CrmOpportunity>         Opportunities        => Set<CrmOpportunity>();
    internal DbSet<CrmActivity>            Activities           => Set<CrmActivity>();
    internal DbSet<CrmActivityParticipant> ActivityParticipants => Set<CrmActivityParticipant>();
    internal DbSet<CrmActivityDocument>    ActivityDocuments    => Set<CrmActivityDocument>();
    internal DbSet<CrmActivityFile>        ActivityFiles        => Set<CrmActivityFile>();
    internal DbSet<CrmRecordHistory>       RecordHistory        => Set<CrmRecordHistory>();
    internal DbSet<CrmLeadApiKey>          LeadApiKeys          => Set<CrmLeadApiKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CrmDbContext).Assembly);
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
        // D-11 — history is the audit trail of a record: written once.
        if (ChangeTracker.Entries<CrmRecordHistory>().Any(e => e.State is EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("CRM record history is immutable.");
    }
}
