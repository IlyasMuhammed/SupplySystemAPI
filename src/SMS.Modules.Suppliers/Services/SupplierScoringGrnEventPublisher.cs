using Hangfire;
using SMS.Modules.Warehouse.Events;

namespace SMS.Modules.Suppliers.Services;

// Real (non-no-op) implementation of Warehouse's IGrnEventPublisher — registered in place of
// NullGrnEventPublisher (see AddSuppliersModule). GrnStatusHandler already calls
// PublishGrnApprovedAsync on every GRN approval, both the normal workflow path and the
// no-workflow-record fallback, so this is the single correct hook for SC-004's scoring trigger.
internal sealed class SupplierScoringGrnEventPublisher : IGrnEventPublisher
{
    private readonly IBackgroundJobClient _jobs;
    public SupplierScoringGrnEventPublisher(IBackgroundJobClient jobs) => _jobs = jobs;

    // Non-blocking: enqueues the scoring job and returns immediately. A ScoreGrnAsync failure
    // (transient or otherwise) never rolls back the GRN approval that already committed.
    public Task PublishGrnApprovedAsync(GrnApprovedEvent evt)
    {
        _jobs.Enqueue<ISupplierScoringService>(s => s.ScoreGrnAsync(evt.GrnUuid));
        return Task.CompletedTask;
    }
}
