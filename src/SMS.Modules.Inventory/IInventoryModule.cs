using Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Repositories;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Common;

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
        // The one place stock is held and released, for every module that needs to — deliveries
        // now, sales orders later. Keeps the reservation rows and InventoryItem.QtyReserved in
        // step, which is impossible if each consumer writes its own.
        services.AddScoped<IStockReservationService, StockReservationService>();
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
        services.AddScoped<InventoryDataSeeder>();
        services.AddScoped<StaleRateAlertJob>();
        services.AddScoped<RateExpiryNotificationJob>();

        return services;
    }
}
