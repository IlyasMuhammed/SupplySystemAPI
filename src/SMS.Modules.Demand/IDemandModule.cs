using Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Repositories;
using SMS.Modules.Demand.Services;
using SMS.Shared.Common;

namespace SMS.Modules.Demand;

public interface IDemandModule { }

public static class DemandModuleExtensions
{
    public static IServiceCollection AddDemandModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connString = configuration["Data:mainOrg"]!;

        services.AddDbContext<DemandDbContext>(options =>
            options.UseSqlServer(connString, sql =>
                sql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(500), null)));

        services.AddScoped<IRequisitionRepository, RequisitionRepository>();
        services.AddScoped<IRequisitionService, RequisitionService>();
        services.AddScoped<IQuotationRepository, QuotationRepository>();
        services.AddScoped<IRfqLinkTokenService, RfqLinkTokenService>();
        services.AddScoped<IRfqLinkValidationService, RfqLinkValidationService>();
        services.AddScoped<IRfqSubmissionService, RfqSubmissionService>();
        services.AddScoped<RfqLinkExpiryJob>();
        services.AddScoped<RfqResponseNotificationJob>();
        services.AddScoped<RfqEmailDispatchJob>();
        services.AddScoped<RfqWhatsAppDispatchJob>();
        services.AddScoped<PoWhatsAppDispatchJob>();
        services.AddScoped<IQuotationService, QuotationService>();
        services.AddScoped<IPurchaseOrderRepository, PurchaseOrderRepository>();
        services.AddScoped<IPurchaseOrderService, PurchaseOrderService>();
        services.AddScoped<IPoDocumentService, PoDocumentService>();

        // Workflow engine handlers
        services.AddScoped<IDocumentStatusHandler, PrStatusHandler>();
        services.AddScoped<IDocumentStatusHandler, PoStatusHandler>();
        services.AddScoped<IDocumentCloneHandler, PoCloneHandler>();

        // Timeline trace_id resolvers
        services.AddScoped<ITraceIdResolver, PrTraceIdResolver>();
        services.AddScoped<ITraceIdResolver, QuotationTraceIdResolver>();
        services.AddScoped<ITraceIdResolver, PoTraceIdResolver>();

        return services;
    }

    public static IApplicationBuilder UseDemandModule(this IApplicationBuilder app)
    {
        using (var scope = app.ApplicationServices.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DemandDbContext>();
            db.Database.Migrate();
        }

        RecurringJob.AddOrUpdate<RfqLinkExpiryJob>(
            "rfq-link-expiry-sweep",
            job => job.RunAsync(),
            Cron.Daily);

        return app;
    }
}
