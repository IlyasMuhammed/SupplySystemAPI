using Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Models;
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
        services.AddScoped<IPurchaseOrderPriceLookupService, PurchaseOrderPriceLookupService>();
        services.AddScoped<IPurchaseOrderService, PurchaseOrderService>();
        services.AddScoped<IPoDocumentService, PoDocumentService>();
        services.AddScoped<IWhatsAppStatusUpdateHandler, DemandWhatsAppStatusHandler>();
        // A29-P3-03 — sale order administration & configuration (§3.2/§3.5).
        services.AddScoped<ISaleOrderConfigService, SaleOrderConfigService>();
        // A29-P3-05 — SaleOrder/SaleOrderLine model validation (§4.1/§4.2).
        services.AddTransient<SaleOrderValidator>();
        services.AddTransient<SaleOrderLineValidator>();
        // A29-P4-02 — §4.3's four availability-check scenarios, called by ConfirmAsync below.
        services.AddScoped<IAvailabilityCheckService, AvailabilityCheckService>();
        // A29-P4-03 — what ConfirmAsync delegates to rather than implements itself.
        services.AddScoped<IAutoPoCreationJob, AutoPoCreationJob>();
        services.AddScoped<ISaleOrderEmailJob, SaleOrderEmailJob>();
        // A29-P4-07 — the real §5.1 email service and the Hangfire unit it enqueues for the send.
        services.AddScoped<ISaleOrderEmailService, SaleOrderEmailService>();
        services.AddScoped<ISaleOrderIntimationDispatchJob, SaleOrderIntimationDispatchJob>();
        // A29-P5-02 — §3.3's three supplier-selection modes for a back-to-back PO deficit line.
        services.AddScoped<ISupplierSelectionService, SupplierSelectionService>();
        // A29-P5-03 — §6.1/§6.2's PO-from-deficit creation, with §3.4's per-mode PO status.
        services.AddScoped<IAutoPurchaseOrderService, AutoPurchaseOrderService>();
        // A29-P5-06 — what Warehouse calls once a GRN's stock is posted, §6.4's auto-reservation.
        services.AddScoped<ISaleOrderGrnLinkService, SaleOrderGrnLinkService>();
        // A29-P6-06 — what Logistics calls once a sale-order delivery reaches the customer, §7.6.
        services.AddScoped<ISaleOrderFulfillmentService, SaleOrderFulfillmentService>();
        // A29-P3-06 — SaleOrder CRUD, document numbering, price resolution (§4.1/§4.2/§4.5).
        services.AddScoped<ISaleOrderService, SaleOrderService>();
        // A29-P4-05 — hourly sweep releasing expired SALES_ORDER reservations, §4.4/§5.1.
        services.AddScoped<ReservationExpirySweepJob>();

        // Workflow engine handlers
        services.AddScoped<IDocumentStatusHandler, PrStatusHandler>();
        services.AddScoped<IDocumentStatusHandler, PoStatusHandler>();
        services.AddScoped<IDocumentCloneHandler, PoCloneHandler>();

        // Timeline trace_id resolvers
        services.AddScoped<ITraceIdResolver, PrTraceIdResolver>();
        services.AddScoped<ITraceIdResolver, QuotationTraceIdResolver>();
        services.AddScoped<ITraceIdResolver, PoTraceIdResolver>();
        services.AddScoped<ITraceIdResolver, SoTraceIdResolver>();

        // PV-007 — lets Inventory ask "has this variant ever been transacted" cross-module
        services.AddScoped<IVariantReferenceChecker, PoLineVariantReferenceChecker>();
        services.AddScoped<ISupplierReferenceChecker, DemandSupplierReferenceChecker>();

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

        // A29-P4-05 — hourly, not daily: the 24h expiry warning needs finer granularity than a
        // daily sweep gives (see ReservationExpirySweepJob's own remarks).
        RecurringJob.AddOrUpdate<ReservationExpirySweepJob>(
            ReservationExpirySweepJob.RecurringJobId,
            job => job.RunAsync(),
            Cron.Hourly);

        return app;
    }
}
