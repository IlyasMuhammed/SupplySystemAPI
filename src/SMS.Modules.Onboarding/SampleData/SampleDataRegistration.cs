using Microsoft.Extensions.DependencyInjection;

namespace SMS.Modules.Onboarding.SampleData;

/// <summary>A50 D-27: sample data (demo records). Called by AddOnboardingModule. The contributors are registered by the
/// modules that own the data (Suppliers, Inventory, Demand) as <c>ISampleDataContributor</c>.</summary>
internal static class SampleDataRegistration
{
    public static IServiceCollection AddOnboardingSampleData(this IServiceCollection services)
    {
        services.AddScoped<ISampleDataService, SampleDataService>();
        return services;
    }
}
