using Hangfire;

namespace SMS.Modules.Logistics.Couriers.Booking;

/// <summary>
/// Where booking work is handed to the background.
/// <para>
/// A seam rather than <c>IBackgroundJobClient</c> directly, so the orchestration can be tested by
/// asserting what it asked to run instead of mocking Hangfire's extension-method surface.
/// </para>
/// </summary>
internal interface IConsignmentBookingScheduler
{
    void Enqueue(Guid consignmentUuid, Guid organizationId);

    void ScheduleRetry(Guid consignmentUuid, Guid organizationId, TimeSpan delay);
}

internal sealed class HangfireConsignmentBookingScheduler : IConsignmentBookingScheduler
{
    private readonly IBackgroundJobClient _jobs;

    public HangfireConsignmentBookingScheduler(IBackgroundJobClient jobs) => _jobs = jobs;

    // The organization travels as an argument, not only through TenantPropagatingJobFilter: the
    // sweep enqueues from a recurring job, where there is no request to capture it from.
    public void Enqueue(Guid consignmentUuid, Guid organizationId) =>
        _jobs.Enqueue<ConsignmentBookingJob>(j => j.RunAsync(consignmentUuid, organizationId));

    public void ScheduleRetry(Guid consignmentUuid, Guid organizationId, TimeSpan delay) =>
        _jobs.Schedule<ConsignmentBookingJob>(j => j.RunAsync(consignmentUuid, organizationId), delay);
}
