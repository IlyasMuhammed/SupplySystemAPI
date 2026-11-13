using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SMS.Modules.Hrm.Services;
using SMS.Modules.Hrm.Services.Att;
using SMS.Modules.Hrm.Services.Pay;
using SMS.Shared.Common;

namespace SMS.Modules.Hrm;

internal static partial class HrmRegistration
{
    static partial void AddPay(IServiceCollection services)
    {
        // Null defaults: ATT registers before this runs; POST registers after, with AddScoped, and takes over.
        services.TryAddScoped<IAttendancePayInput, NullAttendancePayInput>();
        services.TryAddScoped<IPayrollPostingReadinessService, DefaultPayrollPostingReadiness>();

        services.AddSingleton<ISalaryFormulaEngine, SalaryFormulaEngine>();
        services.AddSingleton<PayrollProgressTracker>();
        services.AddScoped<ISalaryComponentService, SalaryComponentService>();
        services.AddScoped<ISalaryStructureService, SalaryStructureService>();
        services.AddScoped<IStatutoryService, StatutoryService>();
        services.AddScoped<SalaryDecisions>();
        services.AddScoped<SalaryAssignmentService>();
        services.AddScoped<ISalaryAssignmentService>(sp => sp.GetRequiredService<SalaryAssignmentService>());
        services.AddScoped<IDocumentStatusHandler, SalaryChangeStatusHandler>();

        services.AddScoped<IPayrollInputProvider, PayrollAdjustmentInputProvider>();
        services.AddScoped<IPayrollCalculationEngine, PayrollCalculationEngine>();
        services.AddScoped<PayrollCalculationJob>();
        services.AddScoped<IPayrollPostingBuilder, PayrollPostingBuilder>();
        services.AddScoped<IPayrollRunService, PayrollRunService>();
        services.AddScoped<IPayslipService, PayslipService>();
        services.AddScoped<IPayslipEmailService, PayslipEmailService>();   // A50 D-93
        services.AddScoped<IPayrollAdjustmentService, PayrollAdjustmentService>();
    }
}
