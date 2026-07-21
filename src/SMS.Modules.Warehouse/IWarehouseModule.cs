using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SMS.Modules.Demand.Data;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Warehouse.Data;
using SMS.Modules.Warehouse.Events;
using SMS.Modules.Warehouse.Repositories;
using SMS.Modules.Warehouse.Services;
using SMS.Shared.Common;

namespace SMS.Modules.Warehouse;

public interface IWarehouseModule { }

public static class WarehouseModuleExtensions
{
    public static IApplicationBuilder UseWarehouseModule(this IApplicationBuilder app)
    {
        using var scope = app.ApplicationServices.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WarehouseDbContext>();
        db.Database.Migrate();
        return app;
    }

    public static IServiceCollection AddWarehouseModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connString = configuration["Data:mainOrg"]!;

        services.AddDbContext<WarehouseDbContext>(options =>
            options.UseSqlServer(connString, sql =>
                sql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(500), null)));

        // DemandDbContext and InventoryDbContext are registered by their own modules;
        // resolve them here so GrnRepository and EfGrnInventoryPoster can use them.
        services.AddScoped<IGrnStockPoster, EfGrnInventoryPoster>();
        // TryAddScoped, not AddScoped: this is only a fallback. If another module (e.g. Suppliers,
        // for GRN-approval-triggered scorecard scoring) registers a real IGrnEventPublisher, that
        // registration must win regardless of which module's Add*Module() runs first in Program.cs.
        services.TryAddScoped<IGrnEventPublisher, NullGrnEventPublisher>();
        services.AddScoped<IGrnRepository, GrnRepository>();
        services.AddScoped<IGrnService, GrnService>();
        services.AddScoped<ISroRepository, SroRepository>();
        services.AddScoped<ISroService, SroService>();
        services.AddScoped<ISroEscalationJob, SroEscalationJob>();

        // Workflow engine status handlers
        services.AddScoped<IDocumentStatusHandler, GrnStatusHandler>();
        services.AddScoped<IDocumentStatusHandler, GrnQcStatusHandler>();

        // Timeline trace_id resolvers
        services.AddScoped<ITraceIdResolver, GrnTraceIdResolver>();
        services.AddScoped<ITraceIdResolver, GrnQcTraceIdResolver>();

        return services;
    }
}
