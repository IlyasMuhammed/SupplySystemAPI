using Microsoft.EntityFrameworkCore;
using SMS.WorkflowEngine.Domain;
using SMS.Shared.Common;

namespace SMS.WorkflowEngine.Data;

internal sealed class WorkflowDbContext : DbContext, ITenantScopedDbContext
{
    private readonly ITenantContext _tenantContext;
    public ITenantContext TenantContext => _tenantContext;

    public WorkflowDbContext(DbContextOptions<WorkflowDbContext> options, ITenantContext tenantContext) : base(options) =>
        _tenantContext = tenantContext;

    internal DbSet<WorkflowDefinition>   WorkflowDefinitions   => Set<WorkflowDefinition>();
    internal DbSet<WorkflowStep>         WorkflowSteps         => Set<WorkflowStep>();
    internal DbSet<DocumentApproval>     DocumentApprovals     => Set<DocumentApproval>();
    internal DbSet<DocumentApprovalStep> DocumentApprovalSteps => Set<DocumentApprovalStep>();
    internal DbSet<WorkflowAuditLog>     WorkflowAuditLogs     => Set<WorkflowAuditLog>();
    internal DbSet<WorkflowGroup>        WorkflowGroups        => Set<WorkflowGroup>();
    internal DbSet<WorkflowGroupMember>  WorkflowGroupMembers  => Set<WorkflowGroupMember>();
    internal DbSet<DocumentTimeline>     DocumentTimelines     => Set<DocumentTimeline>();
    internal DbSet<DocumentAttachment>   DocumentAttachments   => Set<DocumentAttachment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("workflow_schema");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WorkflowDbContext).Assembly);
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
