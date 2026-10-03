using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Modules.Finance.Services.Exports;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Constants;
using SMS.Shared.Files;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.Controllers;

// ── Invoices ──────────────────────────────────────────────────────────────────

[ApiController]
[Route("api/finance/invoices")]
[RequiresFeature("MODULE_FINANCE")]
public class InvoicesController : ControllerBase
{
    private readonly IInvoiceService         _svc;
    private readonly IInvoiceDocumentService _documentSvc;
    private readonly IWebHostEnvironment     _env;
    private readonly INotificationService    _notif;

    public InvoicesController(
        IInvoiceService svc, IInvoiceDocumentService documentSvc, IWebHostEnvironment env, INotificationService notif)
    {
        _svc         = svc;
        _documentSvc = documentSvc;
        _env         = env;
        _notif       = notif;
    }

    // Reading a supplier invoice is INVOICE_VIEW; entering, editing, approving, rejecting, reversing it or attaching
    // to it is INVOICE_PROCESS ("Verify and approve invoices for payment" — Finance Officer, Finance Manager, Org Admin,
    // System Admin; the Auditor reads only). PAYMENT_APPROVE stays the sign-off on supplier payments.

    [HttpPost]
    [RequirePermission(PermissionCodes.INVOICE_PROCESS)]
    public async Task<IActionResult> Create([FromBody] CreateInvoiceRequest req)
    {
        var uuid    = await _svc.CreateAsync(req, User.GetUserId());
        var actorId = User.GetUserId();
        await _notif.TryCreateAsync(new NotificationRequest(
            UserId: actorId, Type: "INVOICE_CREATED", Title: "Invoice Created",
            Message: $"A new invoice has been created and is pending approval.",
            Category: "Finance", EntityType: "Invoice", EntityUuid: uuid.ToString(),
            NavigationUrl: $"/portal/pages/finance/invoices/{uuid}",
            CreatedBy: actorId));
        return Ok(ApiResponse<Guid>.Ok(uuid, StaticResponseMessage.recordCreatedSuccessfully));
    }

    [HttpGet]
    [RequirePermission(PermissionCodes.INVOICE_VIEW)]
    public async Task<IActionResult> GetList([FromQuery] InvoiceFilter filter)
    {
        var result = await _svc.GetListAsync(filter);
        return Ok(ApiResponse<PaginatedResponse<InvoiceListItemModel>>.Ok(result));
    }

    [HttpGet("{uuid:guid}")]
    [RequirePermission(PermissionCodes.INVOICE_VIEW)]
    public async Task<IActionResult> GetById(Guid uuid)
    {
        var detail = await _svc.GetByUuidAsync(uuid);
        return detail is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<InvoiceDetailModel>.Ok(detail));
    }

    [HttpGet("{uuid:guid}/pdf")]
    [RequirePermission(PermissionCodes.INVOICE_VIEW)]
    public async Task<IActionResult> DownloadPdf(Guid uuid)
    {
        var bytes = await _documentSvc.GeneratePdfAsync(uuid);
        return File(bytes, "application/pdf", $"Invoice-{uuid}.pdf");
    }

    [HttpPatch("{uuid:guid}")]
    [RequirePermission(PermissionCodes.INVOICE_PROCESS)]
    public async Task<IActionResult> Patch(Guid uuid, [FromBody] PatchInvoiceRequest req)
    {
        var updated = await _svc.PatchAsync(uuid, req, User.GetUserId());
        return updated
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    [HttpPost("{uuid:guid}/approve")]
    [RequirePermission(PermissionCodes.INVOICE_PROCESS)]
    public async Task<IActionResult> Approve(Guid uuid, [FromBody] ApproveInvoiceRequest req)
    {
        var updated = await _svc.ApproveAsync(uuid, req.Notes, User.GetUserId());
        if (updated)
        {
            var inv     = await _svc.GetByUuidAsync(uuid);
            var actorId = User.GetUserId();
            if (inv is not null)
                await _notif.TryCreateAsync(new NotificationRequest(
                    UserId: inv.CreatedBy, Type: "INVOICE_APPROVED", Title: "Invoice Approved",
                    Message: $"Invoice {inv.InvoiceNumber} has been approved.",
                    Category: "Finance", EntityType: "Invoice", EntityUuid: uuid.ToString(),
                    NavigationUrl: $"/portal/pages/finance/invoices/{uuid}",
                    CreatedBy: actorId, SendEmail: true));
        }
        return updated
            ? Ok(ApiResponse.Ok("Invoice approved."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    [HttpPost("{uuid:guid}/reject")]
    [RequirePermission(PermissionCodes.INVOICE_PROCESS)]
    public async Task<IActionResult> Reject(Guid uuid, [FromBody] RejectInvoiceRequest req)
    {
        var updated = await _svc.RejectAsync(uuid, req.Reason, User.GetUserId());
        if (updated)
        {
            var inv     = await _svc.GetByUuidAsync(uuid);
            var actorId = User.GetUserId();
            if (inv is not null)
                await _notif.TryCreateAsync(new NotificationRequest(
                    UserId: inv.CreatedBy, Type: "INVOICE_REJECTED", Title: "Invoice Rejected",
                    Message: $"Invoice {inv.InvoiceNumber} has been rejected. Reason: {req.Reason}",
                    Category: "Finance", EntityType: "Invoice", EntityUuid: uuid.ToString(),
                    NavigationUrl: $"/portal/pages/finance/invoices/{uuid}",
                    CreatedBy: actorId, SendEmail: true));
        }
        return updated
            ? Ok(ApiResponse.Ok("Invoice rejected."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    /// <summary>
    /// SAP alignment (S-7): reverse an approved, unpaid invoice — the opposite supplier/master ledger entry,
    /// status Reversed, the purchase order's invoiced quantities rolled back. 409 when it is not approved,
    /// already reversed, or anything is paid or deducted against it. Returns the invoice as it now stands.
    /// </summary>
    [HttpPost("{uuid:guid}/reverse")]
    [RequirePermission(PermissionCodes.INVOICE_PROCESS)]
    public async Task<IActionResult> Reverse(Guid uuid, [FromBody] ReverseInvoiceRequest req)
    {
        var actorId  = User.GetUserId();
        var reversed = await _svc.ReverseAsync(uuid, req.Reason, actorId);
        if (!reversed) return NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));

        var inv = await _svc.GetByUuidAsync(uuid);
        if (inv is null) return NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));

        await _notif.TryCreateAsync(new NotificationRequest(
            UserId: inv.CreatedBy, Type: "INVOICE_REVERSED", Title: "Invoice Reversed",
            Message: $"Invoice {inv.InvoiceNumber} has been reversed. Reason: {inv.ReversalReason}",
            Category: "Finance", EntityType: "Invoice", EntityUuid: uuid.ToString(),
            NavigationUrl: $"/portal/pages/finance/invoices/{uuid}",
            CreatedBy: actorId, SendEmail: true));

        return Ok(ApiResponse<InvoiceDetailModel>.Ok(inv, "Invoice reversed."));
    }

    [HttpPost("{uuid:guid}/attachment/upload")]
    [RequirePermission(PermissionCodes.INVOICE_PROCESS)]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UploadAttachment(Guid uuid, IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest(ApiResponse.Fail("No file was provided."));

        // The file is served back from this API's own origin, typed by its extension: only the shared allow-list's
        // extensions are kept (an .html, .svg or .hxt "scan" would be stored cross-site scripting), and the size
        // ceiling is UploadRules.MaxFileBytes. Checked before anything touches the disk or the invoice.
        if (UploadRules.Refusal(file.FileName, file.Length, file.ContentType) is { } refusal)
            return BadRequest(ApiResponse.Fail(refusal));

        var uploadsDir = Path.Combine(
            _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"),
            "uploads", "invoices");
        Directory.CreateDirectory(uploadsDir);

        var safeName = $"{Guid.NewGuid()}{UploadRules.DiskExtension(file.FileName)}";
        var filePath = Path.Combine(uploadsDir, safeName);

        await using (var stream = new FileStream(filePath, FileMode.CreateNew))
            await file.CopyToAsync(stream);

        var url     = $"/uploads/invoices/{safeName}";
        var updated = await _svc.UploadAttachmentAsync(uuid, url, User.GetUserId());
        return updated
            ? Ok(ApiResponse<string>.Ok(url, "Attachment uploaded."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }
}

// ── Supplier Ledger ───────────────────────────────────────────────────────────

[ApiController]
[Route("api/suppliers/{supplierId:guid}")]
[RequiresFeature("MODULE_FINANCE")]
public class SupplierLedgerController : ControllerBase
{
    private readonly ISupplierLedgerService _svc;
    public SupplierLedgerController(ISupplierLedgerService svc) => _svc = svc;

    // One supplier's payables. The supplier-detail and partner-detail pages (SUPPLIER_VIEW/EDIT/MANAGE) load the
    // ledger and balance unconditionally on their Ledger tab, and finance's readers see the same rows on the master
    // payables ledger — so any of those, or PAYMENT_PROCESS (whoever pays the supplier), may read them.

    [HttpGet("ledger")]
    [RequiresSupplierAccess("supplierId")]
    [RequirePermission(PermissionCodes.SUPPLIER_VIEW, PermissionCodes.SUPPLIER_EDIT, PermissionCodes.SUPPLIER_MANAGE,
        PermissionCodes.INVOICE_VIEW, PermissionCodes.PAYMENT_VIEW, PermissionCodes.PAYMENT_PROCESS)]
    public async Task<IActionResult> GetLedger(Guid supplierId, [FromQuery] SupplierLedgerFilter filter)
    {
        var result = await _svc.GetLedgerAsync(supplierId, filter);
        return Ok(ApiResponse<PaginatedResponse<SupplierLedgerEntryModel>>.Ok(result));
    }

    [HttpGet("balance")]
    [RequiresSupplierAccess("supplierId")]
    [RequirePermission(PermissionCodes.SUPPLIER_VIEW, PermissionCodes.SUPPLIER_EDIT, PermissionCodes.SUPPLIER_MANAGE,
        PermissionCodes.INVOICE_VIEW, PermissionCodes.PAYMENT_VIEW, PermissionCodes.PAYMENT_PROCESS)]
    public async Task<IActionResult> GetBalance(Guid supplierId)
    {
        var result = await _svc.GetBalanceAsync(supplierId);
        return Ok(ApiResponse<SupplierBalanceSummary>.Ok(result));
    }
}

// ── Supplier Payments (multi-invoice allocation) ────────────────────────────

[ApiController]
[Route("api/supplier-payments")]
[RequiresFeature("MODULE_FINANCE")]
public class SupplierPaymentsController : ControllerBase
{
    private readonly ISupplierPaymentService _svc;
    private readonly ISupplierLedgerService  _ledgerSvc;
    public SupplierPaymentsController(ISupplierPaymentService svc, ISupplierLedgerService ledgerSvc)
    {
        _svc       = svc;
        _ledgerSvc = ledgerSvc;
    }

    // Recording, posting, bouncing and cancelling a payment is PAYMENT_PROCESS ("Approve and execute supplier
    // payments" — Finance Officer and Manager); approving one stays the Finance Manager's PAYMENT_APPROVE. Reading
    // them is what finance/payments and finance/payments/:uuid admit: PAYMENT_VIEW or PAYMENT_PROCESS.

    [HttpPost]
    [RequirePermission(PermissionCodes.PAYMENT_PROCESS)]
    public async Task<IActionResult> Create([FromBody] CreateSupplierPaymentRequest req)
    {
        var uuid = await _svc.CreateAsync(req, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(uuid, StaticResponseMessage.recordCreatedSuccessfully));
    }

    [HttpGet]
    [RequirePermission(PermissionCodes.PAYMENT_VIEW, PermissionCodes.PAYMENT_PROCESS)]
    public async Task<IActionResult> GetList([FromQuery] SupplierPaymentFilter filter)
    {
        var result = await _svc.GetListAsync(filter);
        return Ok(ApiResponse<PaginatedResponse<SupplierPaymentListItemModel>>.Ok(result));
    }

    [HttpGet("{uuid:guid}")]
    [RequirePermission(PermissionCodes.PAYMENT_VIEW, PermissionCodes.PAYMENT_PROCESS)]
    public async Task<IActionResult> GetById(Guid uuid)
    {
        var detail = await _svc.GetByUuidAsync(uuid);
        return detail is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<SupplierPaymentDetailModel>.Ok(detail));
    }

    [HttpPost("{uuid:guid}/approve")]
    [RequirePermission(PermissionCodes.PAYMENT_APPROVE)]
    public async Task<IActionResult> Approve(Guid uuid)
    {
        var updated = await _svc.ApproveAsync(uuid, User.GetUserId());
        return updated
            ? Ok(ApiResponse.Ok("Payment approved."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    [HttpPost("{uuid:guid}/cancel")]
    [RequirePermission(PermissionCodes.PAYMENT_PROCESS)]
    public async Task<IActionResult> Cancel(Guid uuid)
    {
        var updated = await _svc.CancelAsync(uuid, User.GetUserId());
        return updated
            ? Ok(ApiResponse.Ok("Payment cancelled."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    [HttpPost("{uuid:guid}/post")]
    [RequirePermission(PermissionCodes.PAYMENT_PROCESS)]
    public async Task<IActionResult> Post(Guid uuid)
    {
        var updated = await _svc.PostAsync(uuid, User.GetUserId());
        return updated
            ? Ok(ApiResponse.Ok("Payment posted."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    [HttpPost("{uuid:guid}/bounce")]
    [RequirePermission(PermissionCodes.PAYMENT_PROCESS)]
    public async Task<IActionResult> Bounce(Guid uuid)
    {
        var updated = await _svc.BounceAsync(uuid, User.GetUserId());
        return updated
            ? Ok(ApiResponse.Ok("Payment marked as bounced; ledger and invoices reversed."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    // Absolute route so it lives alongside the SFM-001 supplier-scoped endpoints
    // (api/suppliers/{id}/ledger, api/suppliers/{id}/balance) despite this controller's
    // own [Route] prefix being api/supplier-payments.
    // One supplier's payables, as SupplierLedgerController's: supplier-detail's Outstanding Invoices tab loads this
    // for every SUPPLIER_VIEW/EDIT/MANAGE holder, and finance/payments/create (PAYMENT_PROCESS) for the allocation grid.
    [HttpGet("/api/suppliers/{supplierId:guid}/outstanding-invoices")]
    [RequiresSupplierAccess("supplierId")]
    [RequirePermission(PermissionCodes.SUPPLIER_VIEW, PermissionCodes.SUPPLIER_EDIT, PermissionCodes.SUPPLIER_MANAGE,
        PermissionCodes.INVOICE_VIEW, PermissionCodes.PAYMENT_VIEW, PermissionCodes.PAYMENT_PROCESS)]
    public async Task<IActionResult> GetOutstandingInvoices(Guid supplierId)
    {
        var result = await _svc.GetOutstandingInvoicesAsync(supplierId);
        return Ok(ApiResponse<List<OutstandingInvoiceModel>>.Ok(result));
    }

    // SFM-006 — per-supplier aging breakdown.
    [HttpGet("/api/suppliers/{supplierId:guid}/aging")]
    [RequiresSupplierAccess("supplierId")]
    [RequirePermission(PermissionCodes.SUPPLIER_VIEW, PermissionCodes.SUPPLIER_EDIT, PermissionCodes.SUPPLIER_MANAGE,
        PermissionCodes.INVOICE_VIEW, PermissionCodes.PAYMENT_VIEW, PermissionCodes.PAYMENT_PROCESS)]
    public async Task<IActionResult> GetSupplierAging(Guid supplierId)
    {
        var result = await _svc.GetSupplierAgingAsync(supplierId);
        return Ok(ApiResponse<SupplierAgingModel>.Ok(result));
    }

    // Cross-supplier payables reports: finance's readers, and whoever the finance reports page admits
    // (REPORT_VIEW or REPORT_EXPORT) — it already shows invoice aging and payment summaries to them.

    // SFM-006 — cross-supplier aging report, one row per supplier plus a grand total row.
    [HttpGet("/api/reports/supplier-aging")]
    [RequirePermission(PermissionCodes.PAYMENT_VIEW, PermissionCodes.PAYMENT_PROCESS, PermissionCodes.INVOICE_VIEW,
        PermissionCodes.REPORT_VIEW, PermissionCodes.REPORT_EXPORT)]
    public async Task<IActionResult> GetCrossSupplierAging()
    {
        var result = await _svc.GetCrossSupplierAgingAsync();
        return Ok(ApiResponse<CrossSupplierAgingReport>.Ok(result));
    }

    // ── SFM-007 reports ──────────────────────────────────────────────────────

    [HttpGet("/api/reports/supplier-payment-register")]
    [RequirePermission(PermissionCodes.PAYMENT_VIEW, PermissionCodes.PAYMENT_PROCESS, PermissionCodes.INVOICE_VIEW,
        PermissionCodes.REPORT_VIEW, PermissionCodes.REPORT_EXPORT)]
    public async Task<IActionResult> GetPaymentRegister([FromQuery] PaymentRegisterFilter filter)
    {
        var result = await _svc.GetPaymentRegisterAsync(filter);
        return Ok(ApiResponse<PaginatedResponse<PaymentRegisterItem>>.Ok(result));
    }

    // Chronological per-supplier ledger with running balance (SupplierLedgerEntryModel.BalanceAfter),
    // exposed as a report — reuses ISupplierLedgerService.GetLedgerAsync directly so this report's
    // balance can never drift from GET /api/suppliers/{id}/balance. The same rows as GET
    // /api/suppliers/{id}/ledger, so the same gate: they must not be readable by more people here.
    [HttpGet("/api/reports/supplier-ledger")]
    [RequiresSupplierAccess("supplierId")]
    [RequirePermission(PermissionCodes.SUPPLIER_VIEW, PermissionCodes.SUPPLIER_EDIT, PermissionCodes.SUPPLIER_MANAGE,
        PermissionCodes.INVOICE_VIEW, PermissionCodes.PAYMENT_VIEW, PermissionCodes.PAYMENT_PROCESS)]
    public async Task<IActionResult> GetSupplierLedgerReport([FromQuery] Guid supplierId, [FromQuery] SupplierLedgerFilter filter)
    {
        var result = await _ledgerSvc.GetLedgerAsync(supplierId, filter);
        return Ok(ApiResponse<PaginatedResponse<SupplierLedgerEntryModel>>.Ok(result));
    }

    [HttpGet("/api/reports/outstanding-payables")]
    [RequirePermission(PermissionCodes.PAYMENT_VIEW, PermissionCodes.PAYMENT_PROCESS, PermissionCodes.INVOICE_VIEW,
        PermissionCodes.REPORT_VIEW, PermissionCodes.REPORT_EXPORT)]
    public async Task<IActionResult> GetOutstandingPayables([FromQuery] OutstandingPayablesFilter filter)
    {
        var result = await _svc.GetOutstandingPayablesAsync(filter);
        return Ok(ApiResponse<PaginatedResponse<OutstandingPayablesSupplierGroup>>.Ok(result));
    }

    [HttpGet("/api/reports/payment-method-breakdown")]
    [RequirePermission(PermissionCodes.PAYMENT_VIEW, PermissionCodes.PAYMENT_PROCESS, PermissionCodes.INVOICE_VIEW,
        PermissionCodes.REPORT_VIEW, PermissionCodes.REPORT_EXPORT)]
    public async Task<IActionResult> GetPaymentMethodBreakdown([FromQuery] PaymentMethodBreakdownFilter filter)
    {
        var result = await _svc.GetPaymentMethodBreakdownAsync(filter);
        return Ok(ApiResponse<PaymentMethodBreakdownReport>.Ok(result));
    }
}

// ── Payments ──────────────────────────────────────────────────────────────────

[ApiController]
[Route("api/finance/payments")]
[RequiresFeature("MODULE_FINANCE")]
public class PaymentsController : ControllerBase
{
    private readonly IPaymentService _svc;
    public PaymentsController(IPaymentService svc) => _svc = svc;

    // Legacy single-invoice payments: read where finance/payments-legacy(/:uuid) admits (PAYMENT_VIEW or
    // PAYMENT_PROCESS); recording or changing one is PAYMENT_PROCESS, as for supplier payments.

    [HttpPost]
    [RequirePermission(PermissionCodes.PAYMENT_PROCESS)]
    public async Task<IActionResult> Create([FromBody] CreatePaymentRequest req)
    {
        var uuid = await _svc.CreateAsync(req, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(uuid, StaticResponseMessage.recordCreatedSuccessfully));
    }

    [HttpGet]
    [RequirePermission(PermissionCodes.PAYMENT_VIEW, PermissionCodes.PAYMENT_PROCESS)]
    public async Task<IActionResult> GetList([FromQuery] PaymentFilter filter)
    {
        var result = await _svc.GetListAsync(filter);
        return Ok(ApiResponse<PaginatedResponse<PaymentListItemModel>>.Ok(result));
    }

    [HttpGet("{uuid:guid}")]
    [RequirePermission(PermissionCodes.PAYMENT_VIEW, PermissionCodes.PAYMENT_PROCESS)]
    public async Task<IActionResult> GetById(Guid uuid)
    {
        var detail = await _svc.GetByUuidAsync(uuid);
        return detail is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<PaymentDetailModel>.Ok(detail));
    }

    [HttpPatch("{uuid:guid}")]
    [RequirePermission(PermissionCodes.PAYMENT_PROCESS)]
    public async Task<IActionResult> Patch(Guid uuid, [FromBody] PatchPaymentRequest req)
    {
        var updated = await _svc.PatchAsync(uuid, req, User.GetUserId());
        return updated
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }
}

// ── Master Financial Ledger (FSD Addendum 24, ML-002) ──────────────────────────

[ApiController]
[Route("api/finance/master-ledger")]
[RequiresFeature("FEATURE_MASTER_LEDGERS")]
public class MasterLedgerController : ControllerBase
{
    private readonly IMasterLedgerQueryService _svc;
    private readonly IOpeningBalanceService    _openingBalance;
    private readonly IDebtWriteOffService      _writeOff;
    private readonly IPoDocumentTemplateService _templateService;
    private readonly IWebHostEnvironment        _env;

    public MasterLedgerController(
        IMasterLedgerQueryService svc, IOpeningBalanceService openingBalance, IDebtWriteOffService writeOff,
        IPoDocumentTemplateService templateService, IWebHostEnvironment env)
    {
        _svc             = svc;
        _openingBalance  = openingBalance;
        _writeOff        = writeOff;
        _templateService = templateService;
        _env             = env;
    }

    // Logo is stored as a web-relative URL by the generic attachment upload endpoint — resolve
    // straight off disk, same as PoDocumentService/InvoiceDocumentService.
    private byte[]? TryLoadLogoBytes(string? logoUrl)
    {
        if (string.IsNullOrWhiteSpace(logoUrl)) return null;

        var relative = logoUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var webRoot  = _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
        var fullPath = Path.Combine(webRoot, relative);

        return System.IO.File.Exists(fullPath) ? System.IO.File.ReadAllBytes(fullPath) : null;
    }

    // Reading the master payables ledger (and exporting it) is what finance/master-ledger admits: PAYMENT_VIEW or
    // INVOICE_VIEW. The opening-balance import and the write-off actions keep their own, stricter, gates.

    [HttpGet]
    [RequirePermission(PermissionCodes.PAYMENT_VIEW, PermissionCodes.INVOICE_VIEW)]
    public async Task<IActionResult> GetLedger([FromQuery] MasterLedgerFilter filter)
    {
        var result = await _svc.GetLedgerAsync(filter);
        return Ok(ApiResponse<PaginatedResponse<MasterLedgerEntryModel>>.Ok(result));
    }

    [HttpGet("summary")]
    [RequirePermission(PermissionCodes.PAYMENT_VIEW, PermissionCodes.INVOICE_VIEW)]
    public async Task<IActionResult> GetSummary([FromQuery] MasterLedgerFilter filter)
    {
        var result = await _svc.GetSummaryAsync(filter);
        return Ok(ApiResponse<MasterLedgerSummaryModel>.Ok(result));
    }

    [HttpGet("balance")]
    [RequirePermission(PermissionCodes.PAYMENT_VIEW, PermissionCodes.INVOICE_VIEW)]
    public async Task<IActionResult> GetBalance()
    {
        var result = await _svc.GetCurrentBalanceAsync();
        return Ok(ApiResponse<MasterLedgerBalanceModel>.Ok(result));
    }

    [HttpGet("export/pdf")]
    [RequirePermission(PermissionCodes.PAYMENT_VIEW, PermissionCodes.INVOICE_VIEW)]
    public async Task<IActionResult> ExportPdf([FromQuery] MasterLedgerFilter filter)
    {
        filter.Page     = 1;
        filter.PageSize = 5000;
        var entries  = await _svc.GetLedgerAsync(filter);
        var summary  = await _svc.GetSummaryAsync(filter);
        var template = await _templateService.GetActiveAsync();
        var logoBytes = TryLoadLogoBytes(template?.CompanyLogoUrl);
        var bytes    = MasterLedgerPdfExporter.Export(entries.Data, summary, template, logoBytes, filter.DateFrom, filter.DateTo);
        return File(bytes, "application/pdf", $"master-payables-ledger-{DateTime.UtcNow:yyyyMMdd-HHmmss}.pdf");
    }

    [HttpGet("export/excel")]
    [RequirePermission(PermissionCodes.PAYMENT_VIEW, PermissionCodes.INVOICE_VIEW)]
    public async Task<IActionResult> ExportExcel([FromQuery] MasterLedgerFilter filter)
    {
        filter.Page     = 1;
        filter.PageSize = 5000;
        var entries = await _svc.GetLedgerAsync(filter);
        var bytes   = MasterLedgerExcelExporter.Export(entries.Data);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"master-payables-ledger-{DateTime.UtcNow:yyyyMMdd-HHmmss}.xlsx");
    }

    // ── ML-005: Opening Balance Import (one-time go-live migration) ────────────

    [HttpPost("opening-balance")]
    [RequirePermission(PermissionCodes.SYSTEM_CONFIGURE)]
    public async Task<IActionResult> ImportOpeningBalance([FromBody] OpeningBalanceImportRequest req)
    {
        var result = await _openingBalance.ImportAsync(req, User.GetUserId());
        return Ok(ApiResponse<OpeningBalanceImportResult>.Ok(result, "Opening balance imported."));
    }

    // ── ML-005: Bad Debt Write-off (Finance Manager approval required) ─────────

    [HttpPost("write-off")]
    [RequirePermission(PermissionCodes.PAYMENT_PROCESS)]
    public async Task<IActionResult> CreateWriteOff([FromBody] CreateWriteOffRequest req)
    {
        var uuid = await _writeOff.CreateAsync(req, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(uuid, StaticResponseMessage.recordCreatedSuccessfully));
    }

    // A write-off is a master-ledger posting; read it where the master ledger is read.
    [HttpGet("write-off/{uuid:guid}")]
    [RequirePermission(PermissionCodes.PAYMENT_VIEW, PermissionCodes.INVOICE_VIEW)]
    public async Task<IActionResult> GetWriteOff(Guid uuid)
    {
        var result = await _writeOff.GetByIdAsync(uuid);
        return result is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<WriteOffModel>.Ok(result));
    }

    [HttpPost("write-off/{uuid:guid}/approve")]
    [RequirePermission(PermissionCodes.PAYMENT_APPROVE)]
    public async Task<IActionResult> ApproveWriteOff(Guid uuid)
    {
        var ok = await _writeOff.ApproveAsync(uuid, User.GetUserId());
        return ok
            ? Ok(ApiResponse.Ok("Write-off approved and posted to the master ledger."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    [HttpPost("write-off/{uuid:guid}/reject")]
    [RequirePermission(PermissionCodes.PAYMENT_APPROVE)]
    public async Task<IActionResult> RejectWriteOff(Guid uuid, [FromBody] RejectWriteOffRequest req)
    {
        var ok = await _writeOff.RejectAsync(uuid, req.Reason, User.GetUserId());
        return ok
            ? Ok(ApiResponse.Ok("Write-off rejected."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }
}

// ── Master Product Ledger (FSD Addendum 24, ML-004) ─────────────────────────────

[ApiController]
[Route("api/inventory/master-product-ledger")]
[RequiresFeature("FEATURE_MASTER_LEDGERS")]
public class MasterProductLedgerController : ControllerBase
{
    private readonly IMasterProductLedgerQueryService _svc;
    public MasterProductLedgerController(IMasterProductLedgerQueryService svc) => _svc = svc;

    // Everything here is a read of inventory/master-product-ledger, which admits INVENTORY_VIEW or STOCK_MANAGE.

    [HttpGet]
    [RequirePermission(PermissionCodes.INVENTORY_VIEW, PermissionCodes.STOCK_MANAGE)]
    public async Task<IActionResult> GetLedger([FromQuery] MasterProductLedgerFilter filter)
    {
        var result = await _svc.GetProductLedgerAsync(filter);
        return Ok(ApiResponse<PaginatedResponse<MasterProductLedgerEntryModel>>.Ok(result));
    }

    [HttpGet("summary")]
    [RequirePermission(PermissionCodes.INVENTORY_VIEW, PermissionCodes.STOCK_MANAGE)]
    public async Task<IActionResult> GetSummary([FromQuery] MasterProductLedgerFilter filter)
    {
        var result = await _svc.GetProductLedgerSummaryAsync(filter);
        return Ok(ApiResponse<MasterProductLedgerSummaryModel>.Ok(result));
    }

    [HttpGet("variant-journey/{variantId:int}")]
    [RequirePermission(PermissionCodes.INVENTORY_VIEW, PermissionCodes.STOCK_MANAGE)]
    public async Task<IActionResult> GetProductJourney(int variantId)
    {
        var result = await _svc.GetProductJourneyAsync(variantId);
        return Ok(ApiResponse<List<MasterProductLedgerEntryModel>>.Ok(result));
    }

    [HttpGet("export/pdf")]
    [RequirePermission(PermissionCodes.INVENTORY_VIEW, PermissionCodes.STOCK_MANAGE)]
    public async Task<IActionResult> ExportPdf([FromQuery] MasterProductLedgerFilter filter)
    {
        filter.Page     = 1;
        filter.PageSize = 5000;
        var entries = await _svc.GetProductLedgerAsync(filter);
        var summary = await _svc.GetProductLedgerSummaryAsync(filter);
        var bytes   = MasterProductLedgerPdfExporter.Export(entries.Data, summary);
        return File(bytes, "application/pdf", $"master-product-ledger-{DateTime.UtcNow:yyyyMMdd-HHmmss}.pdf");
    }

    [HttpGet("export/excel")]
    [RequirePermission(PermissionCodes.INVENTORY_VIEW, PermissionCodes.STOCK_MANAGE)]
    public async Task<IActionResult> ExportExcel([FromQuery] MasterProductLedgerFilter filter)
    {
        filter.Page     = 1;
        filter.PageSize = 5000;
        var entries = await _svc.GetProductLedgerAsync(filter);
        var bytes   = MasterProductLedgerExcelExporter.Export(entries.Data);
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"master-product-ledger-{DateTime.UtcNow:yyyyMMdd-HHmmss}.xlsx");
    }
}
