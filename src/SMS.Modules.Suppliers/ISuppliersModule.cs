using Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Integration;
using SMS.Modules.Suppliers.Models;
using SMS.Modules.Suppliers.Repositories;
using SMS.Modules.Suppliers.Services;
using SMS.Modules.Warehouse.Events;
using SMS.Shared.Common;
using SMS.Shared.Integration.QuickBooks;

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
        // Shared since F29 — carrier credentials need the same thing. TryAdd because more than one
        // module now registers it, and they must not fight over which instance wins.
        services.TryAddScoped<IEncryptionService, AesEncryptionService>();
        services.AddScoped<ISupplierEventPublisher, DefaultSupplierEventPublisher>();
        services.AddSingleton<IPhoneNumberValidationService, PhoneNumberValidationService>();
        services.AddScoped<ISuppliersService, SuppliersService>();
        services.AddScoped<ISupplierRatingJob, SupplierRatingJob>();
        services.AddScoped<ISupplierContactLookupService, SupplierContactLookupService>();
        services.AddScoped<ISupplierNameLookupService, SupplierNameLookupService>();
        // A32 — Demand's sale inquiry/quotation customer check (BR-C1-01/BR-C2-01).
        services.AddScoped<IPartnerRoleLookup, PartnerRoleLookup>();
        services.AddScoped<ISupplierScoreLookupService, SupplierScoreLookupService>();
        services.AddScoped<IScorecardRepository, ScorecardRepository>();
        services.AddScoped<IScorecardService, ScorecardService>();
        services.AddScoped<ScorecardDataSeeder>();
        services.AddScoped<ISupplierScoringService, SupplierScoringService>();
        services.AddScoped<IScorecardRecalculationService, ScorecardRecalculationService>();
        services.AddScoped<IScorecardDashboardService, ScorecardDashboardService>();
        services.AddScoped<ScorecardRecalculationJob>();
        // P1-03 — matches SMS.WorkflowEngine's AddTransient<TValidator> pattern.
        services.AddTransient<BusinessPartnerModelValidator>();
        // P1-04
        services.AddScoped<IBusinessPartnerRepository, BusinessPartnerRepository>();
        services.AddScoped<IBusinessPartnerService, BusinessPartnerService>();
        // Lookups asks every checker before it deletes a currency or changes its code.
        services.AddScoped<ILookupReferenceChecker, SuppliersCurrencyReferenceChecker>();

        // Replaces Warehouse's NullGrnEventPublisher registration — must run AFTER AddWarehouseModule()
        // in Program.cs for this override to win (last registration for a given service type wins).
        services.AddScoped<IGrnEventPublisher, SupplierScoringGrnEventPublisher>();

        // QuickBooks — business partners as customers and vendors. TryAdd: in a host without the
        // Integration module the gateway is the Null one (every call answers Disabled); the Integration
        // module replaces it with the real gateway. The source is registered once and exposed both as
        // itself (for the publisher) and as an IQuickBooksSource (for the gateway), one per scope.
        services.TryAddScoped<IQuickBooksGateway, NullQuickBooksGateway>();
        services.AddScoped<IPartnerCurrencyCodes, LookupsPartnerCurrencyCodes>();
        services.AddScoped<PartnerQuickBooksSource>();
        services.AddScoped<IQuickBooksSource>(sp => sp.GetRequiredService<PartnerQuickBooksSource>());
        services.AddScoped<PartnerQuickBooksPublisher>();

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
