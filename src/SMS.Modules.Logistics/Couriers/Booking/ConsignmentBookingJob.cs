using Hangfire;
using Microsoft.Extensions.Logging;

namespace SMS.Modules.Logistics.Couriers.Booking;

/// <summary>The Hangfire entry point for one booking run.</summary>
internal sealed class ConsignmentBookingJob
{
    private readonly IConsignmentBookingExecutor    _executor;
    private readonly ILogger<ConsignmentBookingJob> _logger;

    public ConsignmentBookingJob(IConsignmentBookingExecutor executor, ILogger<ConsignmentBookingJob> logger)
    {
        _executor = executor;
        _logger   = logger;
    }

    /// <remarks>
    /// <b>No automatic retries.</b> Hangfire's retry re-runs a job because it threw, knowing nothing
    /// about whether the carrier acted. The ledger and the orchestration decide retries here, with
    /// backoff and a limit; a run that throws before recording anything is picked up by
    /// <see cref="ConsignmentBookingSweepJob"/>, which asks the ledger first.
    /// </remarks>
    [AutomaticRetry(Attempts = 0)]
    [Queue("default")]
    public async Task RunAsync(Guid consignmentUuid, Guid organizationId)
    {
        var result = await _executor.ExecuteAsync(consignmentUuid, organizationId);

        // Never the request or the result object: both can carry credentials or personal data.
        _logger.LogInformation(
            "Consignment booking run for {ConsignmentUuid} (organization {OrganizationId}): {Result}",
            consignmentUuid, organizationId, result);
    }
}
