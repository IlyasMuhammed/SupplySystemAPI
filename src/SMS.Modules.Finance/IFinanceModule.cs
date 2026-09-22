using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Repositories;
using SMS.Modules.Finance.Services;
using SMS.Modules.Warehouse.Events;
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

        // A29-P7-07 — daily; see InvoiceOverdueJob for what it does and why it needs no tenant.
        InvoiceOverdueJob.Schedule();

        return app;
    }

    public static IServiceCollection AddFinanceModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connString = configuration["Data:mainOrg"]!;

        services.AddDbContext<FinanceDbContext>(options =>
            options.UseSqlServer(connString, sql =>
                sql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(500), null)));

        services.AddScoped<ISupplierReferenceChecker, FinanceSupplierReferenceChecker>();
        services.AddScoped<IInvoiceRepository,    InvoiceRepository>();
        services.AddScoped<IPaymentRepository,    PaymentRepository>();
        services.AddScoped<ICreditNoteRepository, CreditNoteRepository>();
        services.AddScoped<IDebitNoteRepository,  DebitNoteRepository>();
        services.AddScoped<IInvoiceService,       InvoiceService>();
        services.AddScoped<IPaymentService,       PaymentService>();
        services.AddScoped<ICreditNoteService,    CreditNoteService>();
        services.AddScoped<IDebitNoteService,     DebitNoteService>();
        services.AddScoped<ISupplierLedgerService, SupplierLedgerService>();
        services.AddScoped<IMasterFinancialLedgerService, MasterFinancialLedgerService>();
        services.AddScoped<IMasterLedgerQueryService, MasterFinancialLedgerService>();
        services.AddScoped<IMasterProductLedgerService, MasterProductLedgerService>();
        services.AddScoped<IMasterProductLedgerQueryService, MasterProductLedgerService>();
        services.AddScoped<IOpeningBalanceService, OpeningBalanceService>();
        services.AddScoped<IDebtWriteOffService, DebtWriteOffService>();
        services.AddScoped<ISupplierPaymentRepository, SupplierPaymentRepository>();
        services.AddScoped<ISupplierPaymentService,    SupplierPaymentService>();
        services.AddScoped<IInvoiceDocumentService,    InvoiceDocumentService>();

        // Decision G10 — lets another module raise a payable for something no purchase order sits
        // behind. The contract is in SMS.Shared, so Logistics needs no reference to Finance.
        services.AddScoped<ISupplierInvoicePoster, SupplierInvoicePoster>();

        // Auto-populate an invoice from a GRN when it's approved (fans out alongside Suppliers'
        // scorecard-scoring publisher — GrnStatusHandler calls every registered IGrnEventPublisher).
        services.AddScoped<IInvoiceAutoCreationService, InvoiceAutoCreationService>();
        services.AddScoped<IGrnEventPublisher, InvoiceAutoCreateGrnEventPublisher>();

        // A29-P7-04 — the receivable side: sale invoices and the customer ledger they write.
        services.AddScoped<ICustomerLedgerService, CustomerLedgerService>();
        // The public, read-only face of the same service — the same split the master ledger has —
        // so an endpoint or a report can ask what a customer owes without being able to post to it.
        services.AddScoped<ICustomerLedgerQueryService, CustomerLedgerService>();
        services.AddScoped<ISalesInvoiceService, SalesInvoiceService>();
        services.AddScoped<ISalesInvoiceDocumentService, SalesInvoiceDocumentService>();
        services.AddScoped<ISalesInvoiceDocumentArchive, SalesInvoiceDocumentArchive>();
        services.AddScoped<ICustomerPaymentService, CustomerPaymentService>();
        services.AddScoped<InvoiceOverdueJob>();

        // A29-P8-02 — the per-variant product ledger. Finance's own code takes the writer, which can also
        // track an entry without saving; every other module takes the public contract.
        services.AddScoped<IProductLedgerWriter, ProductLedgerService>();
        services.AddScoped<IProductLedgerService>(sp => sp.GetRequiredService<IProductLedgerWriter>());
        // A29-P8-05 — the read side: a variant's history and summary, and product profitability.
        services.AddScoped<IProductLedgerQueryService, ProductLedgerQueryService>();

        // Timeline trace_id resolver
        services.AddScoped<ITraceIdResolver, InvoiceTraceIdResolver>();
        services.AddScoped<ITraceIdResolver, SupplierPaymentTraceIdResolver>();

        return services;
    }
}
