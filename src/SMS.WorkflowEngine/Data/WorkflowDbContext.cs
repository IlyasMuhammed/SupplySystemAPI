using Microsoft.EntityFrameworkCore;
using SMS.WorkflowEngine.Domain;

namespace SMS.WorkflowEngine.Data;

internal sealed class WorkflowDbContext : DbContext
{
    public WorkflowDbContext(DbContextOptions<WorkflowDbContext> options) : base(options) { }

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
    }
}
