using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SMS.Modules.Hrm.Services;
using SMS.Modules.Hrm.Services.Post;
using SMS.Shared.Common;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Hrm;

internal static partial class HrmRegistration
{
    static partial void AddPost(IServiceCollection services)
    {
        services.TryAddScoped<IQuickBooksPostingReadiness, NullQuickBooksPostingReadiness>();

        services.AddScoped<IModuleEnablementGuard, HrmModuleEnablementGuard>();
        services.AddScoped<IHrmCostCenterSource, HrmCostCenterSource>();
        services.AddScoped<IHrmPersonalIdentifierSource, HrmPersonalIdentifierSource>();
        services.AddScoped<IHrmSetupService, HrmSetupService>();
        // AddPost runs after AddPay, whose null default is a TryAdd (PayrollRunSeams.cs).
        services.AddScoped<IPayrollPostingReadinessService, PayrollPostingReadinessService>();
        services.AddScoped<IPayrollJournalExportService, PayrollJournalExportService>();
        services.AddScoped<IImportTarget, HrmEmployeeImportTarget>();
    }
}
