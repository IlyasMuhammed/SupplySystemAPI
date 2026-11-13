using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SMS.Shared.Common;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Onboarding.Checklist;

/// <summary>A50 D-28: the onboarding checklist. Called by AddOnboardingModule. The signals are registered by the modules
/// that own the data (Suppliers, Inventory, Demand, Integration, Auth, Import) as <c>IOnboardingSignal</c>.</summary>
internal static class ChecklistRegistration
{
    public static IServiceCollection AddOnboardingChecklist(this IServiceCollection services)
    {
        services.AddScoped<IOnboardingChecklistService, OnboardingChecklistService>();
        services.AddScoped<IOnboardingSignal, SetupCompletedSignal>();
        // Replaces the Null default whichever registered first (CORE TryAdds its Null after this call).
        services.RemoveAll<IOnboardingProgress>();
        services.AddScoped<IOnboardingProgress, OnboardingProgress>();
        services.AddScoped<IEntitySyncTarget, OnboardingEntitySyncTarget>();
        services.AddScoped<IOrganizationProvisionedHandler, ChecklistProvisioningHandler>();
        services.AddScoped<OnboardingChecklistJob>();
        return services;
    }
}
