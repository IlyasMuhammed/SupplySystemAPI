using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Hrm.Services.Att;
using SMS.Shared.Common;

namespace SMS.Modules.Hrm;

internal static partial class HrmRegistration
{
    static partial void AddAtt(IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<IAttendanceClock, AttendanceClock>();
        services.AddScoped<IAttendanceRecalculator, AttendanceRecalculator>();
        services.AddScoped<IShiftService, ShiftService>();
        services.AddScoped<IHolidayService, HolidayService>();
        services.AddScoped<IAttendanceService, AttendanceService>();
        services.AddScoped<IOvertimeService, OvertimeService>();
        services.AddScoped<IAttendancePayInput, AttendancePayInput>();
        services.AddScoped<ILeaveTypeService, LeaveTypeService>();
        services.AddScoped<LeaveDecisionApplier>();
        services.AddScoped<ILeaveService, LeaveService>();
        services.AddScoped<IDocumentStatusHandler, LeaveRequestStatusHandler>();
    }
}
