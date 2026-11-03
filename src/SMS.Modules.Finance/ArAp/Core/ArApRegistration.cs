using Microsoft.Extensions.DependencyInjection;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.ArAp;

/// <summary>
/// A48 — dependency injection for the AR & AP sub-ledgers (API-CONTRACT §10.3). CORE registers its own services here and calls
/// one partial method per agent; AR, RPT and AP each implement theirs in their own file
/// (<c>ArAp/ArApRegistration.Ar.cs</c>, <c>.Rpt.cs</c>, <c>.Ap.cs</c>: <c>static partial void AddAr(IServiceCollection services) { … }</c>).
/// An unimplemented partial compiles to nothing. Nobody but CORE edits IFinanceModule.cs.
/// </summary>
internal static partial class ArApRegistration
{
    internal static IServiceCollection AddArApSubledgers(this IServiceCollection services)
    {
        services.AddScoped<IPartnerFinancialProfileService, PartnerFinancialProfileService>();
        services.AddScoped<IPartnerProfileEndpoints>(sp => sp.GetRequiredService<IPartnerFinancialProfileService>());
        services.AddScoped<ArApDefaultsSeeder>();
        services.AddScoped<IOrganizationProvisionedHandler>(sp => sp.GetRequiredService<ArApDefaultsSeeder>());

        AddAr(services);
        AddRpt(services);
        AddAp(services);
        return services;
    }

    static partial void AddAr(IServiceCollection services);
    static partial void AddRpt(IServiceCollection services);
    static partial void AddAp(IServiceCollection services);
}
