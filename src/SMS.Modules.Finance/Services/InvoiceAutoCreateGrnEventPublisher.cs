using Hangfire;
using SMS.Modules.Warehouse.Events;

namespace SMS.Modules.Finance.Services;

// Real (non-no-op) implementation of Warehouse's IGrnEventPublisher — registered alongside
// Suppliers' SupplierScoringGrnEventPublisher. GrnStatusHandler fans out to every registered
// IGrnEventPublisher on approval (see GrnStatusHandler.ApproveGrnAsync), so this is the hook
// that auto-populates an invoice from the GRN once it's approved.
internal sealed class InvoiceAutoCreateGrnEventPublisher : IGrnEventPublisher
{
    private readonly IBackgroundJobClient _jobs;
    public InvoiceAutoCreateGrnEventPublisher(IBackgroundJobClient jobs) => _jobs = jobs;

    // Non-blocking: enqueues the invoice-creation job and returns immediately. A failure there
    // never rolls back the GRN approval that already committed.
    public Task PublishGrnApprovedAsync(GrnApprovedEvent evt)
    {
        _jobs.Enqueue<IInvoiceAutoCreationService>(s => s.CreateFromGrnAsync(evt.GrnUuid));
        return Task.CompletedTask;
    }
}
