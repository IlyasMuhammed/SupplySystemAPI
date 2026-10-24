using Microsoft.Extensions.DependencyInjection;

namespace SMS.Modules.Accounting.Services;

/// <summary>A47 RR — the reversal (C7) and line-level reconciliation (C8). Called once from AccountingModuleExtensions.AddAccountingServices.</summary>
internal static class ReversalReconciliationRegistration
{
    internal static void AddAccountingReversalAndReconciliationServices(this IServiceCollection services)
    {
        services.AddScoped(sp => new JournalReconciler(
            sp.GetRequiredService<Data.AccountingDbContext>(), sp.GetRequiredService<IJournalEntrySequenceService>(), sp.GetService<TimeProvider>()));
        services.AddScoped<IReconciliationService, ReconciliationService>();
        services.AddScoped<IJournalReversalService, JournalReversalService>();
    }
}
