using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Repositories;
using SMS.Modules.Finance.Services;
using SMS.Shared.Common;

namespace SMS.Modules.Finance;

public interface IFinanceModule { }

public static class FinanceModuleExtensions
{
    public static IApplicationBuilder UseFinanceModule(this IApplicationBuilder app)
    {
        using var scope = app.ApplicationServices.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();
        db.Database.Migrate();
        return app;
    }

    public static IServiceCollection AddFinanceModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connString = configuration["Data:mainOrg"]!;

        services.AddDbContext<FinanceDbContext>(options =>
            options.UseSqlServer(connString, sql =>
                sql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(500), null)));

        services.AddScoped<IInvoiceRepository,    InvoiceRepository>();
        services.AddScoped<IPaymentRepository,    PaymentRepository>();
        services.AddScoped<ICreditNoteRepository, CreditNoteRepository>();
        services.AddScoped<IDebitNoteRepository,  DebitNoteRepository>();
        services.AddScoped<IInvoiceService,       InvoiceService>();
        services.AddScoped<IPaymentService,       PaymentService>();
        services.AddScoped<ICreditNoteService,    CreditNoteService>();
        services.AddScoped<IDebitNoteService,     DebitNoteService>();
        services.AddScoped<ISupplierLedgerService, SupplierLedgerService>();
        services.AddScoped<ISupplierPaymentRepository, SupplierPaymentRepository>();
        services.AddScoped<ISupplierPaymentService,    SupplierPaymentService>();

        // Timeline trace_id resolver
        services.AddScoped<ITraceIdResolver, InvoiceTraceIdResolver>();

        return services;
    }
}
