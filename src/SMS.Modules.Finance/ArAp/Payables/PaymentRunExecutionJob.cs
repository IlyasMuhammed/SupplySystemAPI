using Hangfire;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.ArAp.Payables;

/// <summary>
/// A48 D-AP-13 — a payment run of more than <see cref="PaymentRunService.InlineBatchLimit"/> batches executes here, in
/// the background. The organization is passed explicitly and set into <see cref="HangfireTenantScope"/> before anything is
/// read (as the other per-organization jobs do), so every context resolves the run's organization. Execution is
/// idempotent per batch: a retry (or a server restart that re-runs the job) and a manual resume only pick up the batches
/// still Pending.
/// </summary>
internal sealed class PaymentRunExecutionJob
{
    private readonly PaymentRunService _runs;

    public PaymentRunExecutionJob(PaymentRunService runs) => _runs = runs;

    [AutomaticRetry(Attempts = 3)]
    public async Task RunAsync(Guid organizationId, Guid runUuid, int? userId, string? userName)
    {
        HangfireTenantScope.OrganizationId = organizationId;
        await _runs.ContinueAsync(runUuid, new ApActor(userId, userName));
    }
}
