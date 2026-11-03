using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Finance.ArAp;
using SMS.Modules.Finance.ArAp.Reporting;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.Controllers;

// A48 RPT — API-CONTRACT §5: AR / AP inquiry, aging (+ export), DSO / DPO, summaries, statements (+ PDF, email, batch,
// log) and vendor reconciliation. Every figure from the Finance documents (D-1); totals base currency (D-13). Reads carry
// no feature gate; the writes (statement email, batch) carry MODULE_FINANCE (contract §0).

internal static class ArApReportsHttp
{
    internal const string PdfContentType = "application/pdf";

    internal static async Task<StatementActor> ActorAsync(ControllerBase c, ITenantContext tenant, IOrgUserDirectory? users)
    {
        var caller = await ArApCaller.FromAsync(c.User, tenant, users, c.HttpContext.RequestAborted);
        return new StatementActor(caller.UserIdOrNull, caller.UserName);
    }

    internal static string Format(string? format) =>
        string.IsNullOrWhiteSpace(format) ? "json"
        : format.Trim().ToLowerInvariant() is "json" or "pdf" or "xlsx" ? format.Trim().ToLowerInvariant()
        : throw new BadRequestException($"Unknown format '{format}'. Allowed: json, pdf.");
}

// ── Receivables ───────────────────────────────────────────────────────────────

[ApiController]
[Route("api/finance/ar")]
public class ArReportsController : ControllerBase
{
    private readonly IAgingReportService         _aging;
    private readonly IArInquiryService           _inquiry;
    private readonly IStatementService           _statements;
    private readonly ITenantContext              _tenant;
    private readonly IOrgUserDirectory?          _users;
    private readonly IPoDocumentTemplateService? _templates;

    public ArReportsController(
        IAgingReportService aging, IArInquiryService inquiry, IStatementService statements, ITenantContext tenant,
        IServiceProvider services)
    {
        _aging      = aging;
        _inquiry    = inquiry;
        _statements = statements;
        _tenant     = tenant;
        _users      = services.GetService(typeof(IOrgUserDirectory)) as IOrgUserDirectory;
        _templates  = services.GetService(typeof(IPoDocumentTemplateService)) as IPoDocumentTemplateService;
    }

    [HttpGet("aging")]
    [RequirePermission(PermissionCodes.AR_VIEW, PermissionCodes.REPORT_VIEW)]
    public async Task<IActionResult> Aging([FromQuery] AgingReportRequest request, CancellationToken ct) =>
        Ok(ApiResponse<AgingReport>.Ok(await _aging.GetAgedReceivablesAsync(request, ct)));

    /// <summary>The documents behind the aging: one customer and/or one bucket (0-based), paged.</summary>
    [HttpGet("aging/details")]
    [RequirePermission(PermissionCodes.AR_VIEW, PermissionCodes.REPORT_VIEW)]
    public async Task<IActionResult> AgingDetails([FromQuery] AgingDetailsRequest request, CancellationToken ct) =>
        Ok(ApiResponse<PaginatedResponse<AgingDetailLine>>.Ok(await _aging.GetAgingDetailsAsync(false, request, ct)));

    [HttpGet("aging/export")]
    [RequirePermission(PermissionCodes.AR_VIEW)]
    [RequirePermission(PermissionCodes.REPORT_EXPORT)]
    public async Task<IActionResult> AgingExport([FromQuery] AgingReportRequest request, [FromQuery] string? format, CancellationToken ct)
    {
        var report = await _aging.GetAgingForExportAsync(false, request, ct);
        return ExportResult.Of(this, report, format, (await _templates.SafeActiveAsync())?.CompanyName);
    }

    [HttpGet("dso")]
    [RequirePermission(PermissionCodes.AR_VIEW)]
    public async Task<IActionResult> Dso([FromQuery] DateOnly? asOfDate, [FromQuery] int periodDays = 365, CancellationToken ct = default) =>
        Ok(ApiResponse<DaysOutstandingResult>.Ok(await _aging.GetDsoAsync(asOfDate, periodDays, ct)));

    [HttpGet("summary")]
    [RequirePermission(PermissionCodes.AR_VIEW)]
    public async Task<IActionResult> Summary([FromQuery] DateOnly? asOfDate, [FromQuery] int periodDays = 365, CancellationToken ct = default) =>
        Ok(ApiResponse<ArSummaryKpis>.Ok(await _inquiry.GetArSummaryAsync(asOfDate, periodDays, ct)));

    [HttpGet("customers/{uuid:guid}/balance")]
    [RequirePermission(PermissionCodes.AR_VIEW)]
    public async Task<IActionResult> Balance(Guid uuid, [FromQuery] DateOnly? asOfDate, CancellationToken ct) =>
        Ok(ApiResponse<PartnerBalanceResult>.Ok(await _inquiry.GetCustomerBalanceAsync(uuid, asOfDate, ct)));

    /// <summary>Also AR_CREDIT_MEMO: the credit-memo page picks the invoices to apply to from here (FE round 2).</summary>
    [HttpGet("customers/{uuid:guid}/open-items")]
    [RequirePermission(PermissionCodes.AR_VIEW, PermissionCodes.AR_CREDIT_MEMO)]
    public async Task<IActionResult> OpenItems(Guid uuid, [FromQuery] OpenItemsFilter filter, CancellationToken ct) =>
        Ok(ApiResponse<PaginatedResponse<OpenItemDto>>.Ok(await _inquiry.GetCustomerOpenItemsAsync(uuid, filter, ct)));

    [HttpGet("customers/{uuid:guid}/transactions")]
    [RequirePermission(PermissionCodes.AR_VIEW)]
    public async Task<IActionResult> Transactions(Guid uuid, [FromQuery] PartnerTransactionFilter filter, CancellationToken ct) =>
        Ok(ApiResponse<PaginatedResponse<PartnerTransactionDto>>.Ok(await _inquiry.GetCustomerTransactionsAsync(uuid, filter, ct)));

    [HttpGet("customers/{uuid:guid}/statement")]
    [RequirePermission(PermissionCodes.AR_VIEW)]
    public async Task<IActionResult> Statement(Guid uuid, [FromQuery] StatementRequest request, CancellationToken ct)
    {
        request.Format = ArApReportsHttp.Format(request.Format) == "pdf" ? "pdf" : "json";
        var output = await _statements.GenerateCustomerStatementAsync(uuid, request, await ArApReportsHttp.ActorAsync(this, _tenant, _users), ct);
        return output.Pdf is { } pdf
            ? File(pdf, ArApReportsHttp.PdfContentType, output.FileName)
            : Ok(ApiResponse<StatementResult>.Ok(output.Statement));
    }

    [HttpPost("customers/{uuid:guid}/statement/email")]
    [RequiresFeature("MODULE_FINANCE")]
    [RequirePermission(PermissionCodes.AR_STATEMENT_SEND)]
    public async Task<IActionResult> EmailStatement(Guid uuid, [FromBody] EmailStatementRequest request, CancellationToken ct)
    {
        var result = await _statements.EmailCustomerStatementAsync(uuid, request, await ArApReportsHttp.ActorAsync(this, _tenant, _users), ct);
        return Ok(ApiResponse<StatementEmailResult>.Ok(result, $"Statement emailed to {result.EmailedTo}."));
    }

    [HttpPost("statements/batch")]
    [RequiresFeature("MODULE_FINANCE")]
    [RequirePermission(PermissionCodes.AR_STATEMENT_SEND)]
    public async Task<IActionResult> Batch([FromBody] BatchStatementRequest request, CancellationToken ct)
    {
        var result = await _statements.BatchCustomerStatementsAsync(request, await ArApReportsHttp.ActorAsync(this, _tenant, _users), ct);
        return Ok(ApiResponse<BatchStatementResult>.Ok(result,
            $"{result.Generated} statement(s) generated, {result.Emailed} emailed, {result.Skipped} below the minimum, {result.Failed} failed."));
    }
}

// ── Payables ──────────────────────────────────────────────────────────────────

[ApiController]
[Route("api/finance/ap")]
public class ApReportsController : ControllerBase
{
    private readonly IAgingReportService          _aging;
    private readonly IApInquiryService            _inquiry;
    private readonly IStatementService            _statements;
    private readonly IVendorReconciliationService _reconciliation;
    private readonly ITenantContext               _tenant;
    private readonly IOrgUserDirectory?           _users;
    private readonly IPoDocumentTemplateService?  _templates;

    public ApReportsController(
        IAgingReportService aging, IApInquiryService inquiry, IStatementService statements,
        IVendorReconciliationService reconciliation, ITenantContext tenant, IServiceProvider services)
    {
        _aging          = aging;
        _inquiry        = inquiry;
        _statements     = statements;
        _reconciliation = reconciliation;
        _tenant         = tenant;
        _users          = services.GetService(typeof(IOrgUserDirectory)) as IOrgUserDirectory;
        _templates      = services.GetService(typeof(IPoDocumentTemplateService)) as IPoDocumentTemplateService;
    }

    [HttpGet("aging")]
    [RequirePermission(PermissionCodes.AP_VIEW, PermissionCodes.REPORT_VIEW)]
    public async Task<IActionResult> Aging([FromQuery] AgingReportRequest request, CancellationToken ct) =>
        Ok(ApiResponse<AgingReport>.Ok(await _aging.GetAgedPayablesAsync(request, ct)));

    [HttpGet("aging/details")]
    [RequirePermission(PermissionCodes.AP_VIEW, PermissionCodes.REPORT_VIEW)]
    public async Task<IActionResult> AgingDetails([FromQuery] AgingDetailsRequest request, CancellationToken ct) =>
        Ok(ApiResponse<PaginatedResponse<AgingDetailLine>>.Ok(await _aging.GetAgingDetailsAsync(true, request, ct)));

    [HttpGet("aging/export")]
    [RequirePermission(PermissionCodes.AP_VIEW)]
    [RequirePermission(PermissionCodes.REPORT_EXPORT)]
    public async Task<IActionResult> AgingExport([FromQuery] AgingReportRequest request, [FromQuery] string? format, CancellationToken ct)
    {
        var report = await _aging.GetAgingForExportAsync(true, request, ct);
        return ExportResult.Of(this, report, format, (await _templates.SafeActiveAsync())?.CompanyName);
    }

    [HttpGet("dpo")]
    [RequirePermission(PermissionCodes.AP_VIEW)]
    public async Task<IActionResult> Dpo([FromQuery] DateOnly? asOfDate, [FromQuery] int periodDays = 365, CancellationToken ct = default) =>
        Ok(ApiResponse<DaysOutstandingResult>.Ok(await _aging.GetDpoAsync(asOfDate, periodDays, ct)));

    [HttpGet("summary")]
    [RequirePermission(PermissionCodes.AP_VIEW)]
    public async Task<IActionResult> Summary([FromQuery] DateOnly? asOfDate, [FromQuery] int periodDays = 365, CancellationToken ct = default) =>
        Ok(ApiResponse<ApSummaryKpis>.Ok(await _inquiry.GetApSummaryAsync(asOfDate, periodDays, ct)));

    [HttpGet("vendors/{uuid:guid}/balance")]
    [RequirePermission(PermissionCodes.AP_VIEW)]
    public async Task<IActionResult> Balance(Guid uuid, [FromQuery] DateOnly? asOfDate, CancellationToken ct) =>
        Ok(ApiResponse<PartnerBalanceResult>.Ok(await _inquiry.GetVendorBalanceAsync(uuid, asOfDate, ct)));

    /// <summary>Also AP_DEBIT_MEMO: the debit-memo page picks the invoices to apply to from here (FE round 2).</summary>
    [HttpGet("vendors/{uuid:guid}/open-items")]
    [RequirePermission(PermissionCodes.AP_VIEW, PermissionCodes.AP_DEBIT_MEMO)]
    public async Task<IActionResult> OpenItems(Guid uuid, [FromQuery] OpenItemsFilter filter, CancellationToken ct) =>
        Ok(ApiResponse<PaginatedResponse<OpenItemDto>>.Ok(await _inquiry.GetVendorOpenItemsAsync(uuid, filter, ct)));

    [HttpGet("vendors/{uuid:guid}/transactions")]
    [RequirePermission(PermissionCodes.AP_VIEW)]
    public async Task<IActionResult> Transactions(Guid uuid, [FromQuery] PartnerTransactionFilter filter, CancellationToken ct) =>
        Ok(ApiResponse<PaginatedResponse<PartnerTransactionDto>>.Ok(await _inquiry.GetVendorTransactionsAsync(uuid, filter, ct)));

    [HttpGet("vendors/{uuid:guid}/statement")]
    [RequirePermission(PermissionCodes.AP_VIEW)]
    public async Task<IActionResult> Statement(Guid uuid, [FromQuery] StatementRequest request, CancellationToken ct)
    {
        request.Format = ArApReportsHttp.Format(request.Format) == "pdf" ? "pdf" : "json";
        var output = await _statements.GenerateVendorStatementAsync(uuid, request, await ArApReportsHttp.ActorAsync(this, _tenant, _users), ct);
        return output.Pdf is { } pdf
            ? File(pdf, ArApReportsHttp.PdfContentType, output.FileName)
            : Ok(ApiResponse<StatementResult>.Ok(output.Statement));
    }

    [HttpPost("vendors/{uuid:guid}/reconciliation")]
    [RequirePermission(PermissionCodes.AP_EXECUTE)]
    public async Task<IActionResult> Reconcile(Guid uuid, [FromBody] VendorReconciliationRequest request, CancellationToken ct)
    {
        var result = await _reconciliation.ReconcileAsync(uuid, request, ct);
        return Ok(ApiResponse<VendorReconciliationResult>.Ok(result,
            result.IsReconciled ? "Reconciled: no differences." : $"{result.Differences.Count} difference(s); balance differs by {result.Difference:N2}."));
    }
}

// ── Statement log ─────────────────────────────────────────────────────────────

[ApiController]
[Route("api/finance/statements")]
public class StatementLogController : ControllerBase
{
    private readonly IStatementService _statements;

    public StatementLogController(IStatementService statements) => _statements = statements;

    /// <summary>Customer rows need AR_VIEW, vendor rows AP_VIEW (checked against the role asked for).</summary>
    [HttpGet("log")]
    [RequirePermission(PermissionCodes.AR_VIEW, PermissionCodes.AP_VIEW)]
    public async Task<IActionResult> Log([FromQuery] StatementLogFilter filter, CancellationToken ct)
    {
        var role = StatementService.NormalizeRole(filter.Role);
        var needed = role == PartnerRoles.Customer ? PermissionCodes.AR_VIEW : PermissionCodes.AP_VIEW;
        if (!User.GetPermissions().Contains(needed))
            throw new ForbiddenException("You do not have permission to perform this action.");
        filter.Role = role;
        return Ok(ApiResponse<PaginatedResponse<StatementLogModel>>.Ok(await _statements.GetLogAsync(filter, ct)));
    }
}

internal static class ExportResult
{
    internal static IActionResult Of(ControllerBase c, AgingReport report, string? format, string? companyName)
    {
        var f = string.IsNullOrWhiteSpace(format) ? "pdf" : format.Trim().ToLowerInvariant();
        return f switch
        {
            "pdf"  => c.File(AgingExporters.Pdf(report, companyName), ArApReportsHttp.PdfContentType, AgingExporters.FileName(report, "pdf")),
            "xlsx" => c.File(AgingExporters.Excel(report), AgingExporters.ExcelContentType, AgingExporters.FileName(report, "xlsx")),
            _      => throw new BadRequestException($"Unknown format '{format}'. Allowed: pdf, xlsx.")
        };
    }

    internal static async Task<SMS.Modules.Lookups.Models.PoDocumentTemplateModel?> SafeActiveAsync(this IPoDocumentTemplateService? templates)
    {
        if (templates is null) return null;
        try { return await templates.GetActiveAsync(); }
        catch (Exception) { return null; }
    }
}
