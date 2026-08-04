using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace SMS.Shared.Common;

public static class TenantContextExtensions
{
    // Registered once, centrally (SMS.API/Program.cs), before any module registration — every
    // tenant-scoped DbContext across every module constructor-injects ITenantContext.
    public static IServiceCollection AddTenantContext(this IServiceCollection services)
    {
        services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        services.AddScoped<ITenantContext, TenantContext>();
        return services;
    }
}
