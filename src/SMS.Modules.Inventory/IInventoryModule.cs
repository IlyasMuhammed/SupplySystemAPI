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
        services.AddScoped<IBatchSerialService, BatchSerialService>();
        services.AddScoped<IProductSearchIndexService, ProductSearchIndexService>();
        services.AddScoped<IVariantSupplierService, VariantSupplierService>();
        services.AddScoped<IVariantSupplierResolver, VariantSupplierResolver>();
        services.AddScoped<InventoryDataSeeder>();
        services.AddScoped<StaleRateAlertJob>();
        services.AddScoped<RateExpiryNotificationJob>();

        return services;
    }
}
