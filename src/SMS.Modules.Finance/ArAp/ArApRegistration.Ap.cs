using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SMS.Modules.Finance.ArAp.Payables;
using SMS.Shared.Finance.Posting;

namespace SMS.Modules.Finance.ArAp;

/// <summary>A48 AP — matching, payment schedules, early-payment terms, payment runs, debit memos (API-CONTRACT §6–§9).</summary>
internal static partial class ArApRegistration
{
    static partial void AddAp(IServiceCollection services)
    {
        services.AddScoped<MatchSourceReader>();
        services.AddScoped<IInvoiceMatchingService, InvoiceMatchingService>();
        services.AddScoped<IToleranceRuleService, ToleranceRuleService>();

        services.AddScoped<PaymentScheduleService>();
        services.AddScoped<IPaymentScheduleService>(sp => sp.GetRequiredService<PaymentScheduleService>());
        services.AddScoped<IPaymentScheduleSync>(sp => sp.GetRequiredService<PaymentScheduleService>());
        services.AddScoped<IEarlyPaymentTermService, EarlyPaymentTermService>();

        services.AddScoped<PaymentRunService>();
        services.AddScoped<IPaymentRunService>(sp => sp.GetRequiredService<PaymentRunService>());
        services.AddScoped<PaymentRunExecutionJob>();
        services.AddScoped<IApDebitMemoService, ApDebitMemoService>();

        // D-AP-12 — the Accounting module replaces it with the internal GL's answer; a host without it checks nothing.
        services.TryAddScoped<IPostingRoleReadiness, NullPostingRoleReadiness>();
    }
}
