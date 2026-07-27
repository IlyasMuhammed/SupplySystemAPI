using Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Repositories;
using SMS.Modules.Suppliers.Services;
using SMS.Modules.Warehouse.Events;
using SMS.Shared.Common;

namespace SMS.Modules.Suppliers;

public interface ISuppliersModule { }

public static class SuppliersModuleExtensions
{
    public static IServiceCollection AddSuppliersModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connString = configuration["Data:mainOrg"]!;

        services.AddDbContext<SuppliersDbContext>(options =>
            options.UseSqlServer(connString, sql => sql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(500), null)));

        services.AddScoped<ISuppliersRepository, SuppliersRepository>();
        services.AddScoped<IEncryptionService, AesEncryptionService>();
        services.AddScoped<ISupplierEventPublisher, DefaultSupplierEventPublisher>();
        services.AddSingleton<IPhoneNumberValidationService, PhoneNumberValidationService>();
        services.AddScoped<ISuppliersService, SuppliersService>();
        services.AddScoped<ISupplierRatingJob, SupplierRatingJob>();
        services.AddScoped<ISupplierContactLookupService, SupplierContactLookupService>();
        services.AddScoped<IScorecardRepository, ScorecardRepository>();
        services.AddScoped<IScorecardService, ScorecardService>();
        services.AddScoped<ScorecardDataSeeder>();
        services.AddScoped<ISupplierScoringService, SupplierScoringService>();
        services.AddScoped<IScorecardRecalculationService, ScorecardRecalculationService>();
        services.AddScoped<IScorecardDashboardService, ScorecardDashboardService>();
        services.AddScoped<ScorecardRecalculationJob>();

        // Replaces Warehouse's NullGrnEventPublisher registration — must run AFTER AddWarehouseModule()
        // in Program.cs for this override to win (last registration for a given service type wins).
        services.AddScoped<IGrnEventPublisher, SupplierScoringGrnEventPublisher>();

        return services;
    }

    public static IApplicationBuilder UseSuppliersModule(this IApplicationBuilder app)
    {
        using var scope = app.ApplicationServices.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SuppliersDbContext>();
        db.Database.Migrate();

        var seeder = scope.ServiceProvider.GetRequiredService<ScorecardDataSeeder>();
        seeder.SeedAsync().GetAwaiter().GetResult();

        var config    = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var frequency = config["SupplierScorecard:RecalculationFrequency"] ?? "daily";
        var cron = frequency.Trim().ToLowerInvariant() switch
        {
            "weekly"  => Cron.Weekly(),
            "monthly" => Cron.Monthly(),
            _         => Cron.Daily()
        };
        RecurringJob.AddOrUpdate<ScorecardRecalculationJob>(
            "supplier-scorecard-recalculation",
            job => job.RunAsync(),
            cron);

        return app;
    }
}
