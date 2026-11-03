using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using SMS.Modules.Hrm.Data;
using SMS.Modules.Hrm.Security;
using SMS.Modules.Hrm.Services;
using SMS.Shared.Common;
using SMS.Shared.Finance.Posting;

namespace SMS.Modules.Hrm;

public interface IHrmModule { }

/// <summary>A49 — HRM (docs/hrm/ADDENDUM-49-ANALYSIS.md, API-CONTRACT.md).</summary>
public static class HrmModuleExtensions
{
    public static IServiceCollection AddHrmModule(this IServiceCollection services, IConfiguration configuration)
    {
        var connString = configuration["Data:mainOrg"]!;

        services.AddDbContext<HrmDbContext>(options =>
            options.UseSqlServer(connString, sql => sql.EnableRetryOnFailure(3, TimeSpan.FromMilliseconds(500), null))
                   .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));

        AddHrmServices(services);
        return services;
    }

    /// <summary>Everything but the DbContext (tests bring their own). Optional seams (IOrgUserDirectory, IModuleGate, IOrgCurrencyLookup…) are nullable constructor parameters.</summary>
    internal static void AddHrmServices(IServiceCollection services)
    {
        services.TryAddScoped<IEncryptionService, AesEncryptionService>();
        services.TryAddSingleton<IFinancialPostingGateway, NullFinancialPostingGateway>();
        services.TryAddSingleton<IPostingRoleReadiness, NullPostingRoleReadiness>();

        services.AddScoped<HrmUserNames>();
        services.AddScoped<IHrmSensitiveData, HrmSensitiveData>();
        services.AddScoped<IHrmScopeService, HrmScopeService>();
        services.AddScoped<IHrmSeeder, HrmSeeder>();
        services.AddScoped<IHrmEmployeeDirectory, HrmEmployeeDirectory>();
        services.AddScoped<IHrmApprovalGateway, HrmApprovalGateway>();
        services.AddScoped<IHrmDocumentStore, HrmDocumentStore>();
        services.AddScoped<IHrmAttachmentGuard, HrmAttachmentGuard>();
        services.AddScoped<IHrmAttachmentAccess, HrmAttachmentAccess>();

        services.AddScoped<IBranchService, BranchService>();
        services.AddScoped<ICostCenterService, CostCenterService>();
        services.AddScoped<IDepartmentService, DepartmentService>();
        services.AddScoped<IDesignationService, DesignationService>();
        services.AddScoped<IGradeService, GradeService>();
        services.AddScoped<IHrmSettingsService, HrmSettingsService>();
        services.AddScoped<IHrUserScopeService, HrUserScopeService>();
        services.AddScoped<IEmployeeService, EmployeeService>();
        services.AddScoped<EmploymentRecordService>();
        services.AddScoped<IEmploymentRecordService>(sp => sp.GetRequiredService<EmploymentRecordService>());
        services.AddScoped<IEmploymentRecordFactory>(sp => sp.GetRequiredService<EmploymentRecordService>());
        services.AddScoped<IEmployeeBankAccountService, EmployeeBankAccountService>();
        services.AddScoped<IModuleImpactProvider, HrmImpactProvider>();

        // One file per agent (docs/hrm/API-CONTRACT.md §13): each implements its partial method in Registration/HrmRegistration.<Agent>.cs.
        HrmRegistration.AddAll(services);
    }

    /// <summary>
    /// Applies the hrm schema migrations (guarded/idempotent — shared database). Seeds only organizations with MODULE_HRM on (none until
    /// licensed by hand); any organization is also seeded on its first HRM request.
    /// </summary>
    public static IApplicationBuilder UseHrmModule(this IApplicationBuilder app)
    {
        using var scope = app.ApplicationServices.CreateScope();
        scope.ServiceProvider.GetRequiredService<HrmDbContext>().Database.Migrate();
        try
        {
            if (scope.ServiceProvider.GetService<IOrganizationDirectory>() is { } organizations
                && scope.ServiceProvider.GetService<IModuleGate>() is { } gate)
            {
                var seeder = scope.ServiceProvider.GetRequiredService<IHrmSeeder>();
                foreach (var org in organizations.GetOrganizationIdsAsync().GetAwaiter().GetResult())
                    if (gate.IsEnabledAsync(org, ModuleCodes.Hrm).GetAwaiter().GetResult())
                        seeder.EnsureAsync(org).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            scope.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger("SMS.Modules.Hrm")
                .LogError(ex, "HRM seed data could not be created at startup; each organization is seeded on its first HRM call.");
        }
        return app;
    }
}

/// <summary>Agents' registrations. CORE owns this file; each agent adds Registration/HrmRegistration.&lt;Agent&gt;.cs implementing its partial.</summary>
internal static partial class HrmRegistration
{
    internal static void AddAll(IServiceCollection services)
    {
        AddAtt(services);
        AddPay(services);
        AddTal(services);
        AddPost(services);
        AddLife(services);
    }

    static partial void AddAtt(IServiceCollection services);
    static partial void AddPay(IServiceCollection services);
    static partial void AddTal(IServiceCollection services);
    static partial void AddPost(IServiceCollection services);
    static partial void AddLife(IServiceCollection services);
}
