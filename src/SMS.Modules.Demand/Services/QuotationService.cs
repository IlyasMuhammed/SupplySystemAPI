using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Repositories;
using SMS.Shared.Pagination;
using SMS.WorkflowEngine.Jobs;
using SMS.WorkflowEngine.Models;

namespace SMS.Modules.Demand.Services;

internal sealed class QuotationService : IQuotationService
{
    private readonly IQuotationRepository         _repo;
    private readonly IRfqLinkTokenService         _tokenSvc;
    private readonly RfqEmailDispatchJob          _emailJob;
    private readonly RfqWhatsAppDispatchJob       _whatsAppJob;
    private readonly ILogger<QuotationService>    _logger;
    private readonly IBackgroundJobClient         _jobs;
    private readonly string                       _portalBase;

    public QuotationService(
        IQuotationRepository      repo,
        IRfqLinkTokenService      tokenSvc,
        RfqEmailDispatchJob       emailJob,
        RfqWhatsAppDispatchJob    whatsAppJob,
        ILogger<QuotationService> logger,
        IBackgroundJobClient      jobs,
        IConfiguration            config)
    {
        _repo        = repo;
        _tokenSvc    = tokenSvc;
        _emailJob    = emailJob;
        _whatsAppJob = whatsAppJob;
        _logger      = logger;
        _jobs        = jobs;
        _portalBase  = (config["AppSettings:BaseUrl"] ?? "http://localhost:4200").TrimEnd('/');
    }

    public async Task<Guid> CreateAsync(CreateQuotationRequest req, int createdBy)
    {
        var uuid = await _repo.CreateAsync(req, createdBy);
        var q    = await _repo.GetByIdAsync(uuid);

        if (q is not null)
            _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
                q.TraceId,
                new TimelineEvent("QUOTATION_CREATED", "QUOTATION", uuid, q.QuotationNumber, DateTime.UtcNow, createdBy,
                    req.SourceType == "PR" ? "Created from PR" : req.SourceType == "PO" ? "Created from PO" : "Standalone"),
                "QUOTATION", q.QuotationNumber));

        return uuid;
    }

    public Task UpdateAsync(Guid uuid, PatchQuotationRequest req, int modifiedBy) =>
        _repo.UpdateAsync(uuid, req, modifiedBy);

    public Task<PaginatedResponse<QuotationListItemModel>> GetListAsync(QuotationListFilter filter) =>
        _repo.GetListAsync(filter);

    public Task<QuotationDetailModel?> GetByIdAsync(Guid uuid) =>
        _repo.GetByIdAsync(uuid);

    public async Task SendAsync(Guid uuid, SendQuotationRequest req, int modifiedBy)
    {
        await _repo.SendAsync(uuid, req, modifiedBy);

        var q = await _repo.GetByIdAsync(uuid);
        if (q is not null)
            _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
                q.TraceId,
                new TimelineEvent("QUOTATION_SENT", "QUOTATION", uuid, q.QuotationNumber, DateTime.UtcNow, modifiedBy, null),
                "QUOTATION", q.QuotationNumber));
    }

    public Task<Guid> RecordResponseAsync(Guid uuid, RecordVendorResponseRequest req, int createdBy) =>
        _repo.RecordResponseAsync(uuid, req, createdBy);

    public Task<List<VendorResponseModel>> GetComparisonAsync(Guid uuid) =>
        _repo.GetComparisonAsync(uuid);

    public async Task OpenBidsAsync(Guid uuid, int openedBy)
    {
        await _repo.OpenBidsAsync(uuid, openedBy);

        var q = await _repo.GetByIdAsync(uuid);
        if (q is not null)
            _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
                q.TraceId,
                new TimelineEvent("QUOTATION_BIDS_OPENED", "QUOTATION", uuid, q.QuotationNumber, DateTime.UtcNow, openedBy, null),
                "QUOTATION", q.QuotationNumber));
    }

    public async Task AwardAsync(Guid uuid, AwardQuotationRequest req, int awardedBy)
    {
        await _repo.AwardAsync(uuid, req, awardedBy);

        var q = await _repo.GetByIdAsync(uuid);
        if (q is not null)
            _jobs.Enqueue<ITimelineAppendJob>(j => j.AppendAsync(
                q.TraceId,
                new TimelineEvent("QUOTATION_AWARDED", "QUOTATION", uuid, q.QuotationNumber, DateTime.UtcNow, awardedBy, null),
                "QUOTATION", q.QuotationNumber));
    }

    public Task CancelAsync(Guid uuid, string reason, int modifiedBy) =>
        _repo.CancelAsync(uuid, reason, modifiedBy);

    public async Task<SendWithLinkResult> SendWithLinkAsync(
        Guid uuid, SendWithLinkRequest req, int createdBy)
    {
        var (quotationId, dueDate) = await _repo.SendWithLinkAsync(uuid, req, createdBy);

        var links           = new List<GeneratedLinkModel>();
        string? emailWarning   = null;
        string? whatsAppWarning = null;

        foreach (var pair in req.Suppliers)
        {
            var (rawToken, linkId, expiresAt) = await _tokenSvc.GenerateTokenAsync(
                quotationId, pair.SupplierId, pair.ContactId, dueDate, createdBy,
                pair.SupplierEmail, _portalBase, pair.ContactMobileNumber);

            var linkUrl = $"{_portalBase}/supplier-portal/rfq/{rawToken}";

            try
            {
                await _emailJob.SendRfqResponseRequestAsync(linkId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Email dispatch failed for {Supplier}: {Error}", pair.SupplierName, ex.Message);
                emailWarning ??= ex.Message;
            }

            try
            {
                await _whatsAppJob.SendRfqResponseRequestWhatsAppAsync(linkId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WhatsApp dispatch failed for {Supplier}: {Error}", pair.SupplierName, ex.Message);
                whatsAppWarning ??= ex.Message;
            }

            links.Add(new GeneratedLinkModel
            {
                SupplierId = pair.SupplierId,
                ContactId  = pair.ContactId,
                LinkUrl    = linkUrl,
                ExpiresAt  = expiresAt
            });
        }

        return new SendWithLinkResult
        {
            Links           = links,
            EmailWarning    = emailWarning,
            WhatsAppWarning = whatsAppWarning
        };
    }

    public Task<List<RfqAccessLinkModel>> GetAccessLinksAsync(Guid quotationUuid) =>
        _repo.GetAccessLinksAsync(quotationUuid);

    public async Task ResendLinkAsync(Guid quotationUuid, int linkId)
    {
        await _repo.ValidateLinkForResendAsync(quotationUuid, linkId);
        await _emailJob.SendRfqResponseRequestAsync(linkId);
        await _whatsAppJob.SendRfqResponseRequestWhatsAppAsync(linkId);
    }
}
