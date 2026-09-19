using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Lookups.Data;
using SMS.Modules.Lookups.Repositories;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;

namespace SMS.Modules.Lookups;

public interface ILookupsModule { }

public static class LookupsModuleExtensions
{
    public static IServiceCollection AddLookupsModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connString = configuration["Data:mainOrg"]!;

        services.AddDbContext<LookupsDbContext>(options =>
            options.UseSqlServer(connString, sql => sql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(500), null)));

        services.AddScoped<ILookupsRepository, LookupsRepository>();
        services.AddScoped<ILookupsService, LookupsService>();
        services.AddScoped<IPoDocumentTemplateRepository, PoDocumentTemplateRepository>();
        services.AddScoped<IPoDocumentTemplateService, PoDocumentTemplateService>();
        services.AddScoped<LookupsDataSeeder>();

        // Shared contract — lets other modules resolve a city from this catalog without a
        // project reference to Lookups. Consumed by SMS.Modules.Logistics' structured addresses.
        services.AddScoped<ICityLookupService, CityLookupService>();

        return services;
    }

    public static IApplicationBuilder UseLookupsModule(this IApplicationBuilder app)
    {
        using var scope = app.ApplicationServices.CreateScope();
        var seeder = scope.ServiceProvider.GetRequiredService<LookupsDataSeeder>();
        seeder.SeedAsync().GetAwaiter().GetResult();
        return app;
    }
}
