using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Reports.Data;
using SMS.Modules.Reports.Repositories;
using SMS.Modules.Reports.Services;
using SMS.Shared.Common;

namespace SMS.Modules.Reports;

public interface IReportsModule { }

public static class ReportsModuleExtensions
{
    public static IServiceCollection AddReportsModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connString = configuration["Data:mainOrg"]!;

        services.AddDbContext<ReportsDbContext>(options =>
            options.UseSqlServer(connString, sql =>
                sql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(500), null)));

        services.AddScoped<IReportsRepository, ReportsRepository>();
        services.AddScoped<IReportsService,    ReportsService>();
        services.AddScoped<ISalesReportService, SalesReportService>();
        services.AddScoped<IReceivablesReportService, ReceivablesReportService>();
        services.AddScoped<ISalesAnalysisReportService, SalesAnalysisReportService>();
        services.AddScoped<IFulfilmentReportService, FulfilmentReportService>();
        services.AddScoped<IMarginAnalysisReportService, MarginAnalysisReportService>();
        services.AddScoped<IProductLedgerReportService, ProductLedgerReportService>();
        services.AddScoped<IManufacturingReportService, ManufacturingReportService>();
        services.AddScoped<IAuditService,      AuditService>();

        return services;
    }
}
