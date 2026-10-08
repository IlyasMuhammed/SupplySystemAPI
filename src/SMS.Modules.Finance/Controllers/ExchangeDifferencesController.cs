using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Constants;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.Controllers;

/// <summary>API-CONTRACT §7.1 — one row of the exchange-difference register.</summary>
public sealed class ExchangeDifferenceModel
{
    public Guid      Id                { get; set; }
    public string    Kind              { get; set; } = string.Empty;
    public string    Side              { get; set; } = string.Empty;
    public string    DocumentType      { get; set; } = string.Empty;
    public int       DocumentId        { get; set; }
    public Guid      DocumentUuid      { get; set; }
    public string?   DocumentNo        { get; set; }
    public string?   PaymentType       { get; set; }
    public int?      PaymentId         { get; set; }
    public Guid?     PaymentUuid       { get; set; }
    public string?   PaymentNo         { get; set; }
    public int?      AllocationId      { get; set; }
    public Guid?     PartnerId         { get; set; }
    public Guid?     CurrencyId        { get; set; }
    public string    CurrencyCode      { get; set; } = string.Empty;
    public decimal   AmountCurrency    { get; set; }
    public decimal   BookedRate        { get; set; }
    public decimal   SettlementRate    { get; set; }
    public Guid?     BaseCurrencyId    { get; set; }
    public string    BaseCurrencyCode  { get; set; } = string.Empty;
    public decimal   BookedAmountBase  { get; set; }
    public decimal   SettledAmountBase { get; set; }
    public decimal   DifferenceBase    { get; set; }
    public string?   AccountCode       { get; set; }
    public DateTime  PostedAt          { get; set; }
    public DateOnly? RevaluationDate   { get; set; }
    public int?      CreatedBy         { get; set; }
}

public sealed class ExchangeDifferenceFilter
{
    public string?   Kind         { get; set; }
    public string?   Side         { get; set; }
    public Guid?     CurrencyId   { get; set; }
    public DateOnly? From         { get; set; }
    public DateOnly? To           { get; set; }
    public string?   DocumentType { get; set; }
    public Guid?     DocumentId   { get; set; }
    public string?   PaymentType  { get; set; }
    public Guid?     PaymentId    { get; set; }
    public int       Page         { get; set; } = 1;
    public int       PageSize     { get; set; } = 50;
}

public sealed class RunRevaluationRequest
{
    /// <summary>Default: today (UTC).</summary>
    public DateOnly? RevaluationDate { get; set; }
}

/// <summary>A35 C7 — reads the caller's own organization's exchange-difference register.</summary>
public interface IExchangeDifferenceQueryService
{
    Task<PaginatedResponse<ExchangeDifferenceModel>> ListAsync(ExchangeDifferenceFilter filter);
}

internal sealed class ExchangeDifferenceQueryService : IExchangeDifferenceQueryService
{
    private readonly FinanceDbContext _db;
    public ExchangeDifferenceQueryService(FinanceDbContext db) => _db = db;

    /// <summary>Own organization only (explicit filter — a super admin included). Newest first.</summary>
    public async Task<PaginatedResponse<ExchangeDifferenceModel>> ListAsync(ExchangeDifferenceFilter filter)
    {
        var org = _db.TenantContext.OrganizationId;
        var q = _db.ExchangeDifferences.IgnoreQueryFilters().AsNoTracking().Where(d => d.OrganizationId == org);

        string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().ToUpperInvariant();
        var kind = Norm(filter.Kind);
        if (kind is not null && kind != ExchangeDifferenceKinds.Realized && kind != ExchangeDifferenceKinds.Unrealized)
            throw new BadRequestException("kind must be REALIZED or UNREALIZED.");
        var side = Norm(filter.Side);
        if (side is not null && side != ExchangeDifferenceSides.Receivable && side != ExchangeDifferenceSides.Payable)
            throw new BadRequestException("side must be RECEIVABLE or PAYABLE.");
        if (filter.From is { } f && filter.To is { } t && t < f)
            throw new BadRequestException("'to' must be on or after 'from'.");

        if (kind is not null)                    q = q.Where(d => d.Kind == kind);
        if (side is not null)                    q = q.Where(d => d.Side == side);
        if (filter.CurrencyId is { } cur)        q = q.Where(d => d.CurrencyId == cur);
        if (Norm(filter.DocumentType) is { } dt) q = q.Where(d => d.DocumentType == dt);
        if (filter.DocumentId is { } doc)        q = q.Where(d => d.DocumentUuid == doc);
        if (Norm(filter.PaymentType) is { } pt)  q = q.Where(d => d.PaymentType == pt);
        if (filter.PaymentId is { } pay)         q = q.Where(d => d.PaymentUuid == pay);
        if (filter.From is { } from)
        {
            var start = from.ToDateTime(TimeOnly.MinValue);
            q = q.Where(d => d.RevaluationDate != null ? d.RevaluationDate >= from : d.PostedAt >= start);
        }
        if (filter.To is { } to)
        {
            var end = to.AddDays(1).ToDateTime(TimeOnly.MinValue);
            q = q.Where(d => d.RevaluationDate != null ? d.RevaluationDate <= to : d.PostedAt < end);
        }

        var page     = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 200);
        var total    = await q.CountAsync();
        var items = await q.OrderByDescending(d => d.PostedAt).ThenByDescending(d => d.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(d => new ExchangeDifferenceModel
            {
                Id = d.Uuid, Kind = d.Kind, Side = d.Side,
                DocumentType = d.DocumentType, DocumentId = d.DocumentId, DocumentUuid = d.DocumentUuid, DocumentNo = d.DocumentNo,
                PaymentType = d.PaymentType, PaymentId = d.PaymentId, PaymentUuid = d.PaymentUuid, PaymentNo = d.PaymentNo,
                AllocationId = d.AllocationId, PartnerId = d.PartnerId,
                CurrencyId = d.CurrencyId, CurrencyCode = d.CurrencyCode, AmountCurrency = d.AmountCurrency,
                BookedRate = d.BookedRate, SettlementRate = d.SettlementRate,
                BaseCurrencyId = d.BaseCurrencyId, BaseCurrencyCode = d.BaseCurrencyCode,
                BookedAmountBase = d.BookedAmountBase, SettledAmountBase = d.SettledAmountBase, DifferenceBase = d.DifferenceBase,
                AccountCode = d.AccountCode, PostedAt = d.PostedAt, RevaluationDate = d.RevaluationDate, CreatedBy = d.CreatedBy
            })
            .ToListAsync();

        return PagedResults.Of(items, total, page, pageSize);
    }
}

/// <summary>
/// A35 C7 (FIN) — the exchange-difference register (D-15) and the manual revaluation run (P4-02). Both are the caller's own
/// organization only — an explicit filter, so a super admin sees and revalues their own organization, never another's.
/// </summary>
[ApiController]
public class ExchangeDifferencesController : ControllerBase
{
    private readonly IExchangeDifferenceQueryService _query;
    private readonly IExchangeRevaluationService     _revaluation;
    private readonly ITenantContext                  _tenant;

    public ExchangeDifferencesController(IExchangeDifferenceQueryService query, IExchangeRevaluationService revaluation, ITenantContext tenant)
    {
        _query       = query;
        _revaluation = revaluation;
        _tenant      = tenant;
    }

    /// <summary>
    /// Paged register, newest first. <c>from</c>/<c>to</c> bound the posting date (realized) or the revaluation date
    /// (unrealized), inclusive. <c>documentId</c>/<c>paymentId</c> are the documents' uuids.
    /// </summary>
    [HttpGet("api/finance/exchange-differences")]
    [RequirePermission(PermissionCodes.INVOICE_VIEW, PermissionCodes.SALES_INVOICE_VIEW, PermissionCodes.CUSTOMER_PAYMENT_VIEW,
                       PermissionCodes.PAYMENT_VIEW, PermissionCodes.EXCHANGE_REVALUATION_RUN)]
    public async Task<IActionResult> List([FromQuery] ExchangeDifferenceFilter filter) =>
        Ok(ApiResponse<PaginatedResponse<ExchangeDifferenceModel>>.Ok(await _query.ListAsync(filter)));

    /// <summary>Revalues the caller's organization's open foreign receivables and payables (idempotent per date).</summary>
    [HttpPost("api/finance/exchange-revaluation/run")]
    [RequirePermission(PermissionCodes.EXCHANGE_REVALUATION_RUN)]
    public async Task<IActionResult> Run([FromBody] RunRevaluationRequest? req)
    {
        var date = req?.RevaluationDate ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var result = await _revaluation.RunAsync(_tenant.OrganizationId, date, User.GetUserId());
        return Ok(ApiResponse<ExchangeRevaluationResult>.Ok(result, "Revaluation complete."));
    }
}