using Hangfire;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Repositories;
using SMS.Shared.Pagination;
using SMS.WorkflowEngine.Jobs;
using SMS.WorkflowEngine.Models;

namespace SMS.Modules.Finance.Services;

internal sealed class InvoiceService : IInvoiceService
{
    private readonly IInvoiceRepository _repo;
    private readonly IBackgroundJobClient _jobs;
    public InvoiceService(IInvoiceRepository repo, IBackgroundJobClient jobs)
    {
        _repo = repo;
        _jobs = jobs;
    }

    public async Task<Guid> CreateAsync(CreateInvoiceRequest req, int createdBy)
    {
        var uuid = await _repo.CreateAsync(req, createdBy);
        var inv  = await _repo.GetByUuidAsync(uuid);

        if (inv is not null)
        {
            var notes = $"Amount: {inv.TotalAmount:F2} {inv.Currency}, Match status: {inv.MatchStatus}.";
            _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
                inv.TraceId,
                new TimelineEvent("INVOICE_RECEIVED", "INVOICE", uuid, inv.InvoiceNumber, DateTime.UtcNow, createdBy, notes),
                "INVOICE", inv.InvoiceNumber));
        }

        return uuid;
    }

    public Task<PaginatedResponse<InvoiceListItemModel>> GetListAsync(InvoiceFilter filter)                      => _repo.GetListAsync(filter);
    public Task<InvoiceDetailModel?>                   GetByUuidAsync(Guid uuid)                                => _repo.GetByUuidAsync(uuid);
    public Task<bool>                                  PatchAsync(Guid uuid, PatchInvoiceRequest req, int mod)  => _repo.PatchAsync(uuid, req, mod);

    public async Task<bool> ApproveAsync(Guid uuid, string? notes, int approvedBy)
    {
        var ok = await _repo.ApproveAsync(uuid, notes, approvedBy);
        if (!ok) return false;

        var inv = await _repo.GetByUuidAsync(uuid);
        if (inv is not null)
        {
            var eventNotes = $"Amount: {inv.TotalAmount:F2} {inv.Currency}, Match status: {inv.MatchStatus}.";
            _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
                inv.TraceId,
                new TimelineEvent("INVOICE_APPROVED", "INVOICE", uuid, inv.InvoiceNumber, DateTime.UtcNow, approvedBy, eventNotes),
                "INVOICE", inv.InvoiceNumber));
        }

        return true;
    }

    public Task<bool>                                  RejectAsync(Guid uuid, string reason, int rejectedBy)    => _repo.RejectAsync(uuid, reason, rejectedBy);
    public Task<bool>                                  UploadAttachmentAsync(Guid uuid, string url, int mod)    => _repo.UploadAttachmentAsync(uuid, url, mod);
}

internal sealed class PaymentService : IPaymentService
{
    private readonly IPaymentRepository _repo;
    private readonly IInvoiceRepository _invoices;
    private readonly IBackgroundJobClient _jobs;

    public PaymentService(IPaymentRepository repo, IInvoiceRepository invoices, IBackgroundJobClient jobs)
    {
        _repo     = repo;
        _invoices = invoices;
        _jobs     = jobs;
    }

    public async Task<Guid> CreateAsync(CreatePaymentRequest req, int createdBy)
    {
        var uuid = await _repo.CreateAsync(req, createdBy);

        // "Paid" fires once the invoice is fully settled — not on partial payments.
        var invoice = await _invoices.GetByUuidAsync(req.InvoiceUuid);
        if (invoice is not null && invoice.PaymentStatus == "Paid")
        {
            var notes = $"Amount: {req.AmountPaid:F2} via {req.PaymentMethod}. Invoice total: {invoice.TotalAmount:F2} {invoice.Currency}.";
            _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
                invoice.TraceId,
                new TimelineEvent("INVOICE_PAID", "INVOICE", req.InvoiceUuid, invoice.InvoiceNumber, DateTime.UtcNow, createdBy, notes),
                "INVOICE", invoice.InvoiceNumber));
        }

        return uuid;
    }

    public Task<PaginatedResponse<PaymentListItemModel>> GetListAsync(PaymentFilter filter)                     => _repo.GetListAsync(filter);
    public Task<PaymentDetailModel?>                    GetByUuidAsync(Guid uuid)                               => _repo.GetByUuidAsync(uuid);
    public Task<bool>                                   PatchAsync(Guid uuid, PatchPaymentRequest req, int mod) => _repo.PatchAsync(uuid, req, mod);
}

internal sealed class SupplierPaymentService : ISupplierPaymentService
{
    private readonly ISupplierPaymentRepository _repo;
    public SupplierPaymentService(ISupplierPaymentRepository repo) => _repo = repo;

    public Task<Guid>                                           CreateAsync(CreateSupplierPaymentRequest req, int createdBy) => _repo.CreateAsync(req, createdBy);
    public Task<PaginatedResponse<SupplierPaymentListItemModel>> GetListAsync(SupplierPaymentFilter filter)                   => _repo.GetListAsync(filter);
    public Task<SupplierPaymentDetailModel?>                    GetByUuidAsync(Guid uuid)                                    => _repo.GetByUuidAsync(uuid);
    public Task<bool>                                           ApproveAsync(Guid uuid, int approvedBy)                      => _repo.ApproveAsync(uuid, approvedBy);
    public Task<bool>                                           CancelAsync(Guid uuid, int cancelledBy)                      => _repo.CancelAsync(uuid, cancelledBy);
    public Task<bool>                                           PostAsync(Guid uuid, int postedBy)                          => _repo.PostAsync(uuid, postedBy);
    public Task<bool>                                           BounceAsync(Guid uuid, int bouncedBy)                       => _repo.BounceAsync(uuid, bouncedBy);
    public Task<List<OutstandingInvoiceModel>>                  GetOutstandingInvoicesAsync(Guid supplierId)                 => _repo.GetOutstandingInvoicesAsync(supplierId);
    public Task<SupplierAgingModel>                              GetSupplierAgingAsync(Guid supplierId)                      => _repo.GetSupplierAgingAsync(supplierId);
    public Task<CrossSupplierAgingReport>                        GetCrossSupplierAgingAsync()                                => _repo.GetCrossSupplierAgingAsync();

    public Task<PaginatedResponse<PaymentRegisterItem>>          GetPaymentRegisterAsync(PaymentRegisterFilter filter)       => _repo.GetPaymentRegisterAsync(filter);
    public Task<PaginatedResponse<OutstandingPayablesSupplierGroup>> GetOutstandingPayablesAsync(OutstandingPayablesFilter filter) => _repo.GetOutstandingPayablesAsync(filter);
    public Task<PaymentMethodBreakdownReport>                    GetPaymentMethodBreakdownAsync(PaymentMethodBreakdownFilter filter) => _repo.GetPaymentMethodBreakdownAsync(filter);
}
