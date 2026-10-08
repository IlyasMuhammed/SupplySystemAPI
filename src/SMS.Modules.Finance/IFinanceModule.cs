using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Integration;
using SMS.Modules.Finance.Repositories;
using SMS.Modules.Finance.Services;
using SMS.Modules.Warehouse.Events;
using SMS.Shared.Common;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Finance;

public interface IFinanceModule { }

public static class FinanceModuleExtensions
{
    public static IApplicationBuilder UseFinanceModule(this IApplicationBuilder app)
    {
        using var scope = app.ApplicationServices.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FinanceDbContext>();
        db.Database.Migrate();

        // A35 P1-13 / E-01 (CUR) — every organization's currencies, rate-currency row and legacy rate conversion, then the
        // ICurrencyRatesReadyParticipants (Demand's locked-document backfill). Synchronous, before app.Run(), idempotent,
        // one process per organization at a time (applock). Tenancy migrates earlier, so its organizations exist.
        var organizations = scope.ServiceProvider.GetService<IOrganizationDirectory>();
        if (organizations is not null)
        {
            var orgIds = organizations.GetOrganizationIdsAsync().GetAwaiter().GetResult();
            scope.ServiceProvider.GetRequiredService<CurrencyBootstrapper>().EnsureForAllAsync(orgIds).GetAwaiter().GetResult();
        }

        // A29-P7-07 — daily; see InvoiceOverdueJob for what it does and why it needs no tenant.
        InvoiceOverdueJob.Schedule();
        // A35 P4-02 (FIN) — monthly unrealized revaluation, every organization explicitly (see ExchangeRevaluationJob).
        ExchangeRevaluationJob.Schedule();

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
        // SAP alignment (S-7) — Demand asks it before cancelling a sale order that has been billed.
        services.AddScoped<ISaleOrderInvoiceLookup, SaleOrderInvoiceLookup>();
        // A35 D-8 (FIN) — issued sales invoices lock the sale base, approved supplier invoices the purchase base.
        services.AddScoped<ICurrencyUsageChecker, FinanceDocumentCurrencyUsageChecker>();
        // A35 C7 (FIN) — the exchange-difference register writer (realized at payment, reversed on bounce).
        services.AddScoped<ExchangeDifferenceWriter>();
        services.AddScoped<Controllers.IExchangeDifferenceQueryService, Controllers.ExchangeDifferenceQueryService>();
        services.AddScoped<IExchangeRevaluationService, ExchangeRevaluationService>();
        services.AddScoped<ExchangeRevaluationJob>();
        // A35 D-11 (FIN) — foreign locked Finance documents get their rate once CUR's rates are in (startup + provisioning).
        services.AddScoped<ICurrencyRatesReadyParticipant, FinanceDocumentRatesBackfill>();

        // A29-P8-02 — the per-variant product ledger. Finance's own code takes the writer, which can also
        // track an entry without saving; every other module takes the public contract.
        services.AddScoped<IProductLedgerWriter, ProductLedgerService>();
        services.AddScoped<IProductLedgerService>(sp => sp.GetRequiredService<IProductLedgerWriter>());
        // A29-P8-05 — the read side: a variant's history and summary, and product profitability.
        services.AddScoped<IProductLedgerQueryService, ProductLedgerQueryService>();

        // Timeline trace_id resolver
        services.AddScoped<ITraceIdResolver, InvoiceTraceIdResolver>();
        services.AddScoped<ITraceIdResolver, SupplierPaymentTraceIdResolver>();

        // SAP alignment, work package A — finance master data. Tax codes and exchange rates are managed
        // here and read by other modules through SMS.Shared (ITaxCodeLookup, IExchangeRateProvider).
        services.AddScoped<ITaxCodeService, TaxCodeService>();
        services.AddScoped<IExchangeRateService, ExchangeRateService>();
        services.AddScoped<ITaxCodeLookup, TaxCodeLookup>();
        services.AddScoped<IExchangeRateProvider>(sp =>
            new ExchangeRateProvider(sp.GetRequiredService<FinanceDbContext>(), sp.GetService<IOrganizationCurrencyService>()));

        // A35 — multi-currency core (CUR): org currencies, date-ranged rates, ICurrencyService, the startup/provisioning
        // bootstrap (currencies, rate-currency row, legacy rate conversion, then ICurrencyRatesReadyParticipant).
        services.AddScoped<ICurrencyService>(sp =>
            new CurrencyService(sp.GetRequiredService<FinanceDbContext>(), sp.GetService<IOrganizationCurrencyService>()));
        services.AddScoped<IOrgCurrencyLookup, OrgCurrencyLookup>();
        services.AddScoped<IOrgCurrencyService>(sp => new OrgCurrencyService(
            sp.GetRequiredService<FinanceDbContext>(), sp.GetRequiredService<SMS.Modules.Lookups.Services.ILookupsService>(),
            sp.GetService<IOrganizationCurrencyService>(), sp.GetServices<ICurrencyUsageChecker>()));
        services.AddScoped<ICurrencyRateService>(sp =>
            new CurrencyRateService(sp.GetRequiredService<FinanceDbContext>(), sp.GetService<IOrganizationCurrencyService>()));
        services.AddScoped<ICurrencyUsageChecker, CurrencyRateUsageChecker>();
        services.AddScoped(sp => new CurrencyBootstrapper(
            sp.GetRequiredService<FinanceDbContext>(), sp.GetRequiredService<SMS.Modules.Lookups.Services.ILookupsService>(),
            sp.GetServices<ICurrencyRatesReadyParticipant>(), sp.GetService<IOrganizationCurrencyService>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<CurrencyBootstrapper>>()));
        services.AddScoped<IOrganizationProvisionedHandler>(sp => sp.GetRequiredService<CurrencyBootstrapper>());
        // Lookups asks every checker before it deletes a currency or changes its code.
        services.AddScoped<ILookupReferenceChecker, FinanceCurrencyReferenceChecker>();

        // QuickBooks — sales invoices, and approved supplier invoices as bills. TryAdd: in a host without
        // the Integration module the gateway is the Null one (every call answers Disabled); the Integration
        // module replaces it. Each source is registered once and exposed both as itself (for its publisher)
        // and as an IQuickBooksSource (for the gateway), one per scope.
        services.TryAddScoped<IQuickBooksGateway, NullQuickBooksGateway>();
        services.AddScoped<SalesInvoiceQuickBooksSource>();
        services.AddScoped<IQuickBooksSource>(sp => sp.GetRequiredService<SalesInvoiceQuickBooksSource>());
        services.AddScoped<SalesInvoiceQuickBooksPublisher>();
        services.AddScoped<IPurchaseOrderLineVariants, DemandPurchaseOrderLineVariants>();
        services.AddScoped<BillQuickBooksSource>();
        services.AddScoped<IQuickBooksSource>(sp => sp.GetRequiredService<BillQuickBooksSource>());
        services.AddScoped<BillQuickBooksPublisher>();

        return services;
    }
}
