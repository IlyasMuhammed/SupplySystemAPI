using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SMS.Modules.Hrm.Services;
using SMS.Modules.Hrm.Services.Life;
using SMS.Shared.Common;

namespace SMS.Modules.Hrm;

internal static partial class HrmRegistration
{
    static partial void AddLife(IServiceCollection services)
    {
        services.TryAddScoped<IHrmPostingHelper, HrmPostingHelper>();
        services.AddScoped<LoanLedger>();
        services.AddScoped<LifeUserNames>();
        services.AddScoped<ILoanService, LoanService>();
        services.AddScoped<IFinalSettlementService, FinalSettlementService>();
        services.AddScoped<IIncrementService, IncrementService>();
        services.AddScoped<ILetterService, LetterService>();
        services.AddScoped<ITaxCertificateService, TaxCertificateService>();
        services.AddScoped<IExpenseCategoryService, ExpenseCategoryService>();
        services.AddScoped<ExpenseClaimDecisions>();
        services.AddScoped<IExpenseClaimService, ExpenseClaimService>();
        services.AddScoped<IDocumentStatusHandler>(sp => sp.GetRequiredService<ExpenseClaimDecisions>());
        services.AddScoped<IStatutoryRemittanceService, StatutoryRemittanceService>();
        services.AddScoped<IPayrollInputProvider, LoanPayrollInputProvider>();
        services.AddScoped<IPayrollInputProvider, ExpenseReimbursementInputProvider>();
    }
}


