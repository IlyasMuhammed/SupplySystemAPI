using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SMS.Modules.Demand.Data;
using SMS.Modules.Finance.ArAp.Receivables;
using SMS.Modules.Finance.ArAp.Reporting;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Services;
using SMS.Shared.Common;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Finance.ArAp;

/// <summary>A48 AR — credit control (ICreditCheck for Demand + the sales invoice), dunning, AR credit memos (API-CONTRACT §2–§4).</summary>
internal static partial class ArApRegistration
{
    static partial void AddAr(IServiceCollection services)
    {
        services.AddScoped(sp => new CreditExposureReader(sp.GetRequiredService<FinanceDbContext>(), sp.GetRequiredService<DemandDbContext>()));
        services.AddScoped(sp => new CreditControlService(
            sp.GetRequiredService<FinanceDbContext>(),
            sp.GetRequiredService<CreditExposureReader>(),
            sp.GetRequiredService<IPartnerFinancialProfileService>(),
            sp.GetRequiredService<ILogger<CreditControlService>>(),
            sp.GetService<IPartnerLookupService>(),
            sp.GetService<ICurrencyService>(),
            sp.GetService<IOrgCurrencyLookup>(),
            sp.GetService<TimeProvider>()));
        services.AddScoped<ICreditCheck>(sp => sp.GetRequiredService<CreditControlService>());

        services.AddScoped(sp => new ArDocumentCurrency(sp.GetService<ICurrencyService>(), sp.GetService<IOrgCurrencyLookup>()));
        services.AddScoped(sp => new ArDocumentPostings(
            sp.GetRequiredService<FinanceDbContext>(),
            sp.GetRequiredService<ILogger<ArDocumentPostings>>(),
            sp.GetService<IQuickBooksGateway>(),
            sp.GetService<IFinancialPostingGateway>()));
        services.AddScoped(sp => new DunningChargeWriter(
            sp.GetRequiredService<FinanceDbContext>(),
            sp.GetRequiredService<ICustomerLedgerService>(),
            sp.GetRequiredService<ArDocumentCurrency>(),
            sp.GetRequiredService<ArDocumentPostings>(),
            sp.GetService<TimeProvider>()));
        // A49 E-13
        services.AddScoped<IServiceInvoiceService>(sp => new ServiceInvoiceService(
            sp.GetRequiredService<FinanceDbContext>(),
            sp.GetRequiredService<ISalesInvoiceService>(),
            sp.GetRequiredService<ArDocumentCurrency>(),
            sp.GetService<IPartnerLookupService>(),
            sp.GetService<TimeProvider>(),
            sp.GetService<IExternalAccountMapReader>()));
        services.AddSingleton<IDunningTemplateEngine, DunningTemplateEngine>();
        services.AddScoped(sp => new DunningService(
            sp.GetRequiredService<FinanceDbContext>(),
            sp.GetRequiredService<IPartnerFinancialProfileService>(),
            sp.GetRequiredService<ArApDefaultsSeeder>(),
            sp.GetRequiredService<CreditControlService>(),
            sp.GetRequiredService<DunningChargeWriter>(),
            sp.GetRequiredService<IDunningTemplateEngine>(),
            sp.GetRequiredService<ILogger<DunningService>>(),
            sp.GetService<IPartnerLookupService>(),
            sp.GetService<IEmailSender>(),
            sp.GetService<IStatementService>(),
            sp.GetService<IDocumentNumberGenerator>(),
            sp.GetService<TimeProvider>()));
        services.AddScoped(sp => new ArCreditMemoService(
            sp.GetRequiredService<FinanceDbContext>(),
            sp.GetRequiredService<ICustomerLedgerService>(),
            sp.GetRequiredService<ArDocumentCurrency>(),
            sp.GetRequiredService<ILogger<ArCreditMemoService>>(),
            sp.GetService<IPartnerLookupService>(),
            sp.GetRequiredService<ArDocumentPostings>(),
            sp.GetService<TimeProvider>()));
    }
}
