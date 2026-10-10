using Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Integration;
using SMS.Modules.Inventory.Repositories;
using SMS.Modules.Inventory.Services;
using SMS.Modules.Inventory.Services.LeadTimes;
using SMS.Shared.Common;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Inventory;

public interface IInventoryModule { }

public static class InventoryModuleExtensions
{
    public static IApplicationBuilder UseInventoryModule(this IApplicationBuilder app)
    {
        using var scope = app.ApplicationServices.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        db.Database.Migrate();

        var seeder = scope.ServiceProvider.GetRequiredService<InventoryDataSeeder>();
        seeder.SeedAsync().GetAwaiter().GetResult();

        // RC-007 — stale-rate alerting (monthly) and rate-expiry warnings (daily), same
        // RecurringJob.AddOrUpdate registration pattern as ScorecardRecalculationJob (Suppliers).
        RecurringJob.AddOrUpdate<StaleRateAlertJob>(
            "stale-rate-alert", job => job.RunAsync(), Cron.Monthly());
        RecurringJob.AddOrUpdate<RateExpiryNotificationJob>(
            "rate-expiry-notification", job => job.RunAsync(), Cron.Daily());

        return app;
    }

    public static IServiceCollection AddInventoryModule(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connString = configuration["Data:mainOrg"]!;

        services.AddDbContext<InventoryDbContext>(options =>
            options.UseSqlServer(connString,
                sql => sql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(500), null))
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));

        services.AddScoped<IInventoryRepository, InventoryRepository>();
        services.AddScoped<IInventoryService, InventoryService>();
        services.AddScoped<IInventoryLedgerService, InventoryLedgerService>();
        services.AddScoped<IStockAvailabilityService, StockAvailabilityService>();
        // Shared contract — lets other modules bridge product-scoped lines (supplier returns) to
        // the variant that actually holds stock, without a project reference to Inventory.
        services.AddScoped<IProductVariantResolver, ProductVariantResolver>();
        // The "Available For" checkboxes on a variant, for a module (Demand's sale orders) that
        // has to refuse selling one that was never marked available for that channel.
        services.AddScoped<IVariantAvailabilityService, VariantAvailabilityService>();
        // The one place stock is held and released, for every module that needs to — deliveries
        // now, sales orders later. Keeps the reservation rows and InventoryItem.QtyReserved in
        // step, which is impossible if each consumer writes its own.
        services.AddScoped<IStockReservationService, StockReservationService>();
        // A30 §14 — decides which demand gets scarce stock, on hand or expected. Reserved
        // allocations are holds through the reservation service above, so nothing else changes.
        services.AddScoped<IAllocationEngine, AllocationEngine>();
        // Takes stock off the books against those same holds, so what leaves the ledger is what
        // was reserved, picked and packed — batch for batch.
        services.AddScoped<IGoodsIssuePoster, GoodsIssuePoster>();
        // Where a warehouse is, for modules that keep only its UUID — Logistics uses it to tell a
        // carrier where to collect a delivery from.
        services.AddScoped<IWarehouseDirectory, WarehouseDirectory>();
        services.AddScoped<IBatchSerialService, BatchSerialService>();
        services.AddScoped<IProductSearchIndexService, ProductSearchIndexService>();
        services.AddScoped<IVariantSupplierService, VariantSupplierService>();
        // A29-P2-03 — the five-tier sale-price waterfall (§2.3), shared so Demand can price a
        // sale order line without a project reference to Inventory.
        services.AddScoped<IPricingService, PricingService>();
        // A29-P2-04 — CRUD over the PricingRule rows the waterfall above reads.
        services.AddScoped<IPricingRuleService, PricingRuleService>();
        services.AddScoped<IVariantSupplierResolver, VariantSupplierResolver>();
        // Lookups asks every checker before it deletes a currency or changes its code.
        services.AddScoped<ILookupReferenceChecker, InventoryCurrencyReferenceChecker>();
        // A33 C2 — variant fulfillment routes: the gated assign endpoints, each variant's route for Demand's resolver,
        // and "still used by active product variants" for Logistics (one IFulfillmentRouteUsage per module, L-7).
        services.AddScoped<IVariantFulfillmentRouteService, VariantFulfillmentRouteService>();
        services.AddScoped<VariantFulfillmentRoutes>();
        services.AddScoped<IVariantFulfillmentRoutes>(sp => sp.GetRequiredService<VariantFulfillmentRoutes>());
        services.AddScoped<IFulfillmentRouteUsage>(sp => sp.GetRequiredService<VariantFulfillmentRoutes>());
        // A37 D-27 — a product's variants and routes for Logistics' GET api/products/{id}/routes.
        services.AddScoped<IProductVariantRoutes>(sp => sp.GetRequiredService<VariantFulfillmentRoutes>());
        // A34 C3 — lead-time defaults (one row per org, D-10) and a variant's 8 components (D-26). The loader reads
        // Suppliers' ISupplierLeadTimeLookup / ISupplierNameLookupService when the host registers them (both optional).
        services.AddScoped(sp => new LeadTimeInputsLoader(
            sp.GetRequiredService<InventoryDbContext>(),
            sp.GetService<ISupplierLeadTimeLookup>(),
            sp.GetService<ISupplierNameLookupService>()));
        services.AddScoped<ILeadTimeDefaultsService, LeadTimeDefaultsService>();
        services.AddScoped<IVariantLeadTimeService>(sp => new VariantLeadTimeService(
            sp.GetRequiredService<InventoryDbContext>(),
            sp.GetRequiredService<LeadTimeInputsLoader>(),
            sp.GetService<IFulfillmentRouteLookup>()));
        // A34 C4 — the calculator (Shared ILeadTimeCalculator, for Demand's line endpoints and D-19 dates) and its
        // per-level tree. The BOM structure comes from Material's IBomStructureReader when the host registers it. The
        // D-27 cache is the process-wide IMemoryCache (AddMemoryCache is idempotent); the org is in every key.
        services.AddMemoryCache();
        services.AddScoped(sp => new LeadTimeCalculator(
            sp.GetRequiredService<InventoryDbContext>(),
            sp.GetRequiredService<LeadTimeInputsLoader>(),
            sp.GetRequiredService<IMemoryCache>(),
            sp.GetService<IFulfillmentRouteLookup>(),
            sp.GetService<IBomStructureReader>()));
        services.AddScoped<ILeadTimeCalculator>(sp => sp.GetRequiredService<LeadTimeCalculator>());
        services.AddScoped<IManufacturingLeadTimeCalculator>(sp => sp.GetRequiredService<LeadTimeCalculator>());
        // A37 §6 — the catalog delta; tax codes / units of measure from Lookups' ISyncLookupReader when registered.
        services.AddScoped<ICatalogSyncService>(sp => new CatalogSyncService(
            sp.GetRequiredService<InventoryDbContext>(), sp.GetService<ISyncLookupReader>()));
        services.AddScoped<InventoryDataSeeder>();
        services.AddScoped<StaleRateAlertJob>();
        services.AddScoped<RateExpiryNotificationJob>();

        // QuickBooks — product variants as items. TryAdd: in a host without the Integration module the
        // gateway is the Null one (every call answers Disabled); the Integration module replaces it. The
        // source is registered once and exposed both as itself (for the publisher) and as an
        // IQuickBooksSource (for the gateway), one per scope.
        services.TryAddScoped<IQuickBooksGateway, NullQuickBooksGateway>();
        services.AddScoped<VariantQuickBooksSource>();
        services.AddScoped<IQuickBooksSource>(sp => sp.GetRequiredService<VariantQuickBooksSource>());
        services.AddScoped<VariantQuickBooksPublisher>();

        return services;
    }
}
