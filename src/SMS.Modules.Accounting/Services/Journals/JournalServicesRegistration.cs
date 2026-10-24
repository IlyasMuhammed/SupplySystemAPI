using Microsoft.Extensions.DependencyInjection;
using SMS.Shared.Common;

namespace SMS.Modules.Accounting.Services;

/// <summary>A47 CORE — the journal engine (balance mover, sequences, period rule, journal types, writer, journal entry API).</summary>
internal static class JournalServicesRegistration
{
    internal static void AddAccountingJournalServices(this IServiceCollection services)
    {
        services.AddScoped<GlBalanceMover>();
        services.AddScoped<IJournalEntrySequenceService, JournalSequenceService>();
        services.AddScoped<JournalPeriodResolver>();
        services.AddScoped<JournalTypeSeeder>();
        services.AddScoped<IAccountingEventHandler<CoaSetupCompletedEvent>>(sp => sp.GetRequiredService<JournalTypeSeeder>()); // D-6
        services.AddScoped<JournalTypeResolver>();
        services.AddScoped(sp => new JournalEntryWriter(
            sp.GetRequiredService<Data.AccountingDbContext>(), sp.GetRequiredService<IJournalEntrySequenceService>(),
            sp.GetRequiredService<JournalPeriodResolver>(), sp.GetRequiredService<GlBalanceMover>(), sp.GetRequiredService<JournalTypeResolver>(),
            sp.GetService<IPartnerRoleLookup>(), sp.GetService<TimeProvider>()));
        services.AddScoped<IJournalEntryService>(sp => new JournalEntryService(
            sp.GetRequiredService<Data.AccountingDbContext>(), sp.GetRequiredService<JournalEntryWriter>(), sp.GetRequiredService<JournalTypeSeeder>(),
            sp.GetRequiredService<IAccountingEventPublisher>(), sp.GetService<IJournalReversalService>(),
            sp.GetService<JournalReconciler>())); // REV-10 — posting a draft reversal completes it (RR registers the reconciler)
        services.AddScoped<IJournalTypeService, JournalTypeService>();
    }
}
