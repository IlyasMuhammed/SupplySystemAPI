using Hangfire;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SMS.Shared.Common;
using SMS.WorkflowEngine.Data;
using SMS.WorkflowEngine.Jobs;
using SMS.WorkflowEngine.Repositories;
using SMS.WorkflowEngine.Services;
using SMS.WorkflowEngine.Validation;

namespace SMS.WorkflowEngine;

public static class WorkflowEngineModuleExtensions
{
    public static IServiceCollection AddWorkflowEngineModule(
        this IServiceCollection services,
        IConfiguration config)
    {
        var connString = config["Data:mainOrg"]
            ?? throw new InvalidOperationException("Connection string 'Data:mainOrg' is missing.");

        services.AddDbContext<WorkflowDbContext>(o =>
            o.UseSqlServer(connString,
                sql => sql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(500), null)));

        // Repositories
        services.AddScoped<IWorkflowDefinitionRepository, WorkflowDefinitionRepository>();
        services.AddScoped<IWorkflowGroupRepository, WorkflowGroupRepository>();

        // Validators (transient — stateless)
        services.AddTransient<CreateWorkflowDefinitionValidator>();
        services.AddTransient<UpdateWorkflowDefinitionValidator>();
        services.AddTransient<WorkflowStepValidator>();

        // Services
        services.AddScoped<IWorkflowDefinitionService, WorkflowDefinitionService>();
        services.AddScoped<IWorkflowGroupService, WorkflowGroupService>();
        services.AddScoped<IApproverResolutionService, ApproverResolutionService>();
        services.AddScoped<IWorkflowResolutionService, WorkflowResolutionService>();
        services.AddScoped<IDocumentApprovalStepWriter, DocumentApprovalStepWriter>();
        services.AddScoped<IDocumentStatusService, DocumentStatusService>();
        services.AddScoped<IDocumentCloneService, DocumentCloneService>();
        services.AddScoped<IWorkflowActionService, WorkflowActionService>();
        services.AddScoped<ITimelineService, TimelineService>();
        services.AddScoped<IAttachmentService, AttachmentService>();
        services.AddScoped<IWorkflowHistoryService, WorkflowHistoryService>();
        services.AddScoped<IWorkflowInboxService, WorkflowInboxService>();
        services.AddScoped<IWorkflowConfigService, WorkflowConfigService>();
        services.AddScoped<IWorkflowEscalationJob, WorkflowEscalationJob>();
        services.AddScoped<IWorkflowNotificationJob, WorkflowNotificationJob>();
        services.AddScoped<ITimelineAppendJob, TimelineAppendJob>();
        services.AddTransient<SubmitDocumentCommandValidator>();
        services.AddTransient<ApproveCommandValidator>();
        services.AddTransient<RejectCommandValidator>();
        services.AddTransient<RecallCommandValidator>();
        services.AddTransient<CancelCommandValidator>();
        services.AddTransient<ReissueCommandValidator>();
        services.AddTransient<DelegateCommandValidator>();

        services.AddMediatR(cfg =>
            cfg.RegisterServicesFromAssembly(typeof(WorkflowEngineModuleExtensions).Assembly));

        // Seeder (scoped so it can resolve WorkflowDbContext)
        services.AddScoped<WorkflowDefinitionSeeder>();

        return services;
    }

    /// <summary>
    /// Applies the WorkflowEngine EF migrations and seeds default workflow definitions
    /// (PR 1-step, PO 4-tier, GRN 2-tier). Call after UseHangfireDashboard in Program.cs.
    /// </summary>
    public static IApplicationBuilder UseWorkflowEngineModule(this IApplicationBuilder app)
    {
        using var scope = app.ApplicationServices.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<WorkflowDefinitionSeeder>();
        seeder.SeedAsync().GetAwaiter().GetResult();
        return app;
    }
}
