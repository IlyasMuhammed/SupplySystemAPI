using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Material.Data;
using SMS.Modules.Material.Repositories;
using SMS.Modules.Material.Services;
using SMS.Shared.Common;
using SMS.WorkflowEngine.Events;

namespace SMS.Modules.Material;

public interface IMaterialModule { }

public static class MaterialModuleExtensions
{
    public static IServiceCollection AddMaterialModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connString = configuration["Data:mainOrg"]!;

        services.AddDbContext<MaterialDbContext>(options =>
            options.UseSqlServer(connString, sql =>
                sql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(500), null))
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));

        services.AddScoped<IProjectRepository, ProjectRepository>();
        services.AddScoped<IMirRepository, MirRepository>();
        services.AddScoped<IProjectService, ProjectService>();
        services.AddScoped<IMirService, MirService>();
        services.AddScoped<IMirWorkflowService, MirWorkflowService>();
        services.AddScoped<IMirDocumentService, MirDocumentService>();
        services.AddScoped<IMivService, MivService>();
        services.AddScoped<IMivDocumentService, MivDocumentService>();
        services.AddScoped<IChainOfCustodyService, ChainOfCustodyService>();
        services.AddScoped<ICostAllocationService, CostAllocationService>();
        services.AddScoped<IMaterialReturnService, MaterialReturnService>();
        services.AddScoped<IWastageService, WastageService>();
        services.AddScoped<IMaterialConsumptionService, MaterialConsumptionService>();
        services.AddScoped<IPrLookupService, PrLookupService>();
        services.AddScoped<IMirReservationMigrationService, MirReservationMigrationService>();

        // A30 §7–§9 — bills of materials. Numbers come from Logistics' IDocumentNumberGenerator.
        services.AddScoped<IBomRepository, BomRepository>();
        services.AddScoped<IBomService, BomService>();
        services.AddScoped<IBomCostService, BomCostService>();

        // A30 §11–§13, §16 — production orders, their materials, supply requirements and floor issues.
        services.AddScoped<IProductionOrderRepository, ProductionOrderRepository>();
        // Registered as itself too: ISupplyRequirementEngine resolves the concrete class for the one
        // internal-entity method (CreateChildForSupplyAsync) the public interface cannot carry — see
        // that method's doc comment. Both resolve to the same scoped instance.
        services.AddScoped<ProductionOrderService>();
        services.AddScoped<IProductionOrderService>(sp => sp.GetRequiredService<ProductionOrderService>());
        // Phase 4 Track C — Sales reaches this for a manufactured product's own deficit, with no
        // project reference to this module at all.
        services.AddScoped<IProductionDemandService>(sp => sp.GetRequiredService<ProductionOrderService>());
        services.AddScoped<ISupplyRequirementEngine, SupplyRequirementEngine>();
        // A31 C9 — Consolidated Purchase Required dashboard.
        services.AddScoped<IPurchaseRequiredService, PurchaseRequiredService>();
        services.AddScoped<IProductionMaterialIssueService, ProductionMaterialIssueService>();
        // Told after every allocation run (A30-P3-08); resolved by IAllocationEngine's IEnumerable<IAllocationRunListener>.
        services.AddScoped<IAllocationRunListener, ProductionReadinessListener>();
        // A31 C10 §12.2 Step 1 — told immediately when a receipt closes expected supply, so a supply
        // requirement's fulfilment status does not wait for someone to separately run allocation.
        services.AddScoped<IAllocationReceiptListener, ProductionReadinessListener>();

        // A30 §18-19A — quality inspection, finished goods receipt, and the ledger read over both.
        services.AddScoped<IQualityInspectionService, QualityInspectionService>();
        services.AddScoped<IFinishedGoodsReceiptService, FinishedGoodsReceiptService>();
        services.AddScoped<IProductionLedgerService, ProductionLedgerService>();

        // A30-P5-01 — the ten manufacturing-lifecycle notifications (FSD §30).
        services.AddScoped<IManufacturingNotificationService, ManufacturingNotificationService>();

        // Workflow status handlers (MIR_PROJECT and MIR_GENERAL)
        services.AddScoped<IDocumentStatusHandler, MirProjectStatusHandler>();
        services.AddScoped<IDocumentStatusHandler, MirGeneralStatusHandler>();

        // Clone handlers for reissue flow
        services.AddScoped<IDocumentCloneHandler, MirProjectCloneHandler>();
        services.AddScoped<IDocumentCloneHandler, MirGeneralCloneHandler>();

        // Timeline trace_id resolvers
        services.AddScoped<ITraceIdResolver, MirProjectTraceIdResolver>();
        services.AddScoped<ITraceIdResolver, MirGeneralTraceIdResolver>();
        services.AddScoped<ITraceIdResolver, BomTraceIdResolver>();
        services.AddScoped<ITraceIdResolver, ProdTraceIdResolver>();
        services.AddScoped<ITraceIdResolver, SrTraceIdResolver>();

        // PV-007 — lets Inventory ask "has this variant ever been transacted" cross-module
        services.AddScoped<IVariantReferenceChecker, MirLineVariantReferenceChecker>();

        // PR-line disbursement tracking on MIR approval (registered manually — MediatR's assembly
        // scan in AddWorkflowEngineModule only covers the WorkflowEngine assembly itself).
        services.AddScoped<INotificationHandler<DocumentApprovedEvent>, MirPrLineDisbursementHandler>();

        return services;
    }

    public static IApplicationBuilder UseMaterialModule(this IApplicationBuilder app)
    {
        using var scope = app.ApplicationServices.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MaterialDbContext>();
        db.Database.Migrate();

        // Carries MIR reservations into the shared inventory ledger. It runs here rather than in
        // Inventory because this module owns the data being moved, and because Program.cs
        // migrates Inventory first — so both schemas exist by the time this runs. Idempotent.
        scope.ServiceProvider.GetRequiredService<IMirReservationMigrationService>()
             .MigrateAsync().GetAwaiter().GetResult();

        return app;
    }
}
