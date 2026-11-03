using SMS.Modules.Hrm.Services.Att;

namespace SMS.Modules.Hrm.Services.Pay;

/// <summary>
/// The default when no attendance is registered: it says nothing (0 working days), and the engine pays the employed part of the period, full month for
/// someone employed throughout. With no attendance answer there is no overtime either, so nothing to stamp.
/// </summary>
internal sealed class NullAttendancePayInput : IAttendancePayInput
{
    private static AttendancePayData Nothing(int id) => new(id, 0m, 0m, 0m, 0m, 0, 0, 0m, [], false);

    public Task<AttendancePayData> GetAsync(Guid organizationId, int employeeId, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        Task.FromResult(Nothing(employeeId));

    public Task<IReadOnlyDictionary<int, AttendancePayData>> GetManyAsync(Guid organizationId, IReadOnlyCollection<int> employeeIds, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<int, AttendancePayData>>(employeeIds.Distinct().ToDictionary(id => id, Nothing));

    public Task<int> MarkOvertimePaidAsync(Guid organizationId, int runId, int employeeId, DateOnly from, DateOnly to, CancellationToken ct = default) => Task.FromResult(0);
    public Task<int> ReleaseOvertimeAsync(Guid organizationId, int runId, CancellationToken ct = default) => Task.FromResult(0);
}
