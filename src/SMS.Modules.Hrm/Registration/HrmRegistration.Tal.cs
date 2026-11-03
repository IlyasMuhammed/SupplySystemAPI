using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Hrm.Services.Tal;

namespace SMS.Modules.Hrm;

internal static partial class HrmRegistration
{
    static partial void AddTal(IServiceCollection services)
    {
        services.AddScoped<IRecruitmentService, RecruitmentService>();
        services.AddScoped<IPerformanceService, PerformanceService>();
        services.AddScoped<ITrainingService, TrainingService>();
    }
}
