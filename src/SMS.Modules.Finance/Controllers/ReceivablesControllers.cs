using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;
using SMS.WorkflowEngine.Models;

namespace SMS.Modules.Finance.Controllers;

// A29-P7-08 §9.1 / §9.3–§9.5 / §10 — the receivable side's HTTP surface.
//
// Every action declares its own permission: reading and changing are different permissions, and the
// receivables ones are their own codes rather than INVOICE_*/PAYMENT_* (which are the supplier side —
// see PermissionCodes). Missing records, refused states and bad input come back from the services as
// NotFound / Conflict / BadRequest exceptions and are turned into 404 / 409 / 400 by the global
// exception middleware, so the actions themselves have nothing to translate.

// ── Sales invoices ────────────────────────────────────────────────────────────

[ApiController]
[Route("api/sales-invoices")]
[RequiresFeature("MODULE_FINANCE")]
public class SalesInvoicesController : ControllerBase
{
    private readonly ISalesInvoiceService         _svc;
    private readonly ISalesInvoiceDocumentService _documents;
    private readonly ISalesInvoiceDocumentArchive _archive;

    public SalesInvoicesController(
        ISalesInvoiceService svc, ISalesInvoiceDocumentService documents, ISalesInvoiceDocumentArchive archive)
    {
        _svc       = svc;
        _documents = documents;
        _archive   = archive;
    }

    /// <summary>Raises a draft invoice for a delivered delivery. Asking again for the same delivery returns the invoice already raised.</summary>
    [HttpPost]
    [RequirePermission(PermissionCodes.SALES_INVOICE_MANAGE)]
    public async Task<IActionResult> Create([FromBody] CreateSalesInvoiceRequest req)
    {
        var result = await _svc.CreateFromFulfillmentAsync(req.DeliveryUuid, User.GetUserId());
        return Ok(ApiResponse<SalesInvoiceCreated>.Ok(
            result,
            result.AlreadyExisted
                ? $"An invoice already exists for this delivery: {result.InvoiceNumber}."
                : StaticResponseMessage.recordCreatedSuccessfully));
    }

    [HttpGet]
    [RequirePermission(PermissionCodes.SALES_INVOICE_VIEW)]
    public async Task<IActionResult> GetList([FromQuery] SalesInvoiceFilter filter)
    {
        var result = await _svc.ListAsync(filter);
        return Ok(ApiResponse<PaginatedResponse<SalesInvoiceListItemModel>>.Ok(result));
    }

    [HttpGet("{uuid:guid}")]
    [RequirePermission(PermissionCodes.SALES_INVOICE_VIEW)]
    public async Task<IActionResult> GetById(Guid uuid)
    {
        var detail = await _svc.GetAsync(uuid);
        return detail is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<SalesInvoiceDetailModel>.Ok(detail));
    }

    /// <summary>Changes a draft's due date and notes. Nothing else about an invoice is editable.</summary>
    [HttpPut("{uuid:guid}")]
    [RequirePermission(PermissionCodes.SALES_INVOICE_MANAGE)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] UpdateSalesInvoiceRequest req)
    {
        await _svc.UpdateAsync(uuid, req, User.GetUserId());
        return Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully));
    }

    /// <summary>Deletes a draft. An issued invoice has a receivable booked against it and is refused.</summary>
    [HttpDelete("{uuid:guid}")]
    [RequirePermission(PermissionCodes.SALES_INVOICE_MANAGE)]
    public async Task<IActionResult> Delete(Guid uuid)
    {
        await _svc.DeleteAsync(uuid, User.GetUserId());
        return Ok(ApiResponse.Ok(StaticResponseMessage.recordDeletedSuccessfully));
    }

    /// <summary>
    /// DRAFT to ISSUED. Books the receivable on the customer's ledger in the same transaction, then
    /// files the invoice's PDF as an attachment. Filing is best-effort: the invoice is issued either
    /// way, and the response says if the PDF still needs filing (<c>POST /{id}/attach-pdf</c>).
    /// </summary>
    [HttpPost("{uuid:guid}/issue")]
    [RequirePermission(PermissionCodes.SALES_INVOICE_MANAGE)]
    public async Task<IActionResult> Issue(Guid uuid)
    {
        var userId = User.GetUserId();
        var result = await _svc.IssueAsync(uuid, userId);
        var filed  = await _archive.TryFileIssuedPdfAsync(uuid, userId);

        return Ok(ApiResponse<SalesInvoiceIssued>.Ok(
            result,
            filed is null
                ? "Sales invoice issued. Its PDF could not be filed as an attachment; file it from the invoice."
                : "Sales invoice issued and its PDF filed."));
    }

    /// <summary>
    /// Files the invoice's PDF as it stands now as an attachment on the invoice — after a payment, say,
    /// or for an invoice issued before filing existed. Refused for a draft. Each filing is kept.
    /// </summary>
    [HttpPost("{uuid:guid}/attach-pdf")]
    [RequirePermission(PermissionCodes.SALES_INVOICE_MANAGE)]
    public async Task<IActionResult> AttachPdf(Guid uuid)
    {
        var stored = await _archive.FilePdfAsync(uuid, User.GetUserId());
        return Ok(ApiResponse<StoredAttachment>.Ok(
            stored,
            stored.AlreadyStored ? "That copy of the invoice is already on file." : "Invoice PDF filed as an attachment."));
    }

    [HttpGet("{uuid:guid}/pdf")]
    [RequirePermission(PermissionCodes.SALES_INVOICE_VIEW)]
    public async Task<IActionResult> DownloadPdf(Guid uuid)
    {
        var pdf = await _documents.GeneratePdfAsync(uuid);
        return File(pdf.Content, "application/pdf", pdf.FileName);
    }
}

// ── Customer payments ─────────────────────────────────────────────────────────

[ApiController]
[Route("api/customer-payments")]
[RequiresFeature("MODULE_FINANCE")]
public class CustomerPaymentsController : ControllerBase
{
    private readonly ICustomerPaymentService _svc;

    public CustomerPaymentsController(ICustomerPaymentService svc) => _svc = svc;

    /// <summary>
    /// Records money received. Applied to the customer's oldest unpaid invoices first unless
    /// <c>allocations</c> says otherwise; the whole amount is credited to their ledger either way.
    /// </summary>
    [HttpPost]
    [RequirePermission(PermissionCodes.CUSTOMER_PAYMENT_RECORD)]
    public async Task<IActionResult> Record([FromBody] RecordCustomerPaymentRequest req)
    {
        var details = new CustomerPaymentDetails(
            req.CurrencyCode, req.PaymentDate, req.ChequeNumber, req.BankReference, req.Notes, req.Allocations);

        var result = await _svc.RecordPaymentAsync(req.PartnerId, req.Amount, req.Method, details, User.GetUserId());
        return Ok(ApiResponse<CustomerPaymentRecorded>.Ok(result, "Payment recorded."));
    }

    [HttpGet]
    [RequirePermission(PermissionCodes.CUSTOMER_PAYMENT_VIEW)]
    public async Task<IActionResult> GetList([FromQuery] CustomerPaymentFilter filter)
    {
        var result = await _svc.ListAsync(filter);
        return Ok(ApiResponse<PaginatedResponse<CustomerPaymentListItemModel>>.Ok(result));
    }

    [HttpGet("{uuid:guid}")]
    [RequirePermission(PermissionCodes.CUSTOMER_PAYMENT_VIEW)]
    public async Task<IActionResult> GetById(Guid uuid)
    {
        var detail = await _svc.GetAsync(uuid);
        return detail is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<CustomerPaymentDetailModel>.Ok(detail));
    }

    /// <summary>
    /// Applies what is left of a received payment to invoices — FIFO with no body, or exactly the
    /// allocations given. The customer's ledger is not touched: its credit was booked in full when
    /// the payment was recorded.
    /// </summary>
    [HttpPost("{uuid:guid}/allocate")]
    [RequirePermission(PermissionCodes.CUSTOMER_PAYMENT_RECORD)]
    public async Task<IActionResult> Allocate(Guid uuid, [FromBody] AllocateCustomerPaymentRequest? req)
    {
        var result = await _svc.AllocateAsync(uuid, req?.Allocations, User.GetUserId());
        return Ok(ApiResponse<CustomerPaymentAllocated>.Ok(result, "Payment applied."));
    }
}

// ── Customer ledger ───────────────────────────────────────────────────────────

/// <summary>
/// What a customer owes, entry by entry. §1.6 gives <c>/api/partners/{id}/ledger</c> "payable or
/// receivable by type"; this is the receivable half. The payable half is what it already was —
/// <c>/api/suppliers/{id}/ledger</c> — and is not moved here.
/// </summary>
[ApiController]
[Route("api/partners/{partnerId:guid}")]
[RequiresFeature("MODULE_FINANCE")]
public class CustomerLedgerController : ControllerBase
{
    private readonly ICustomerLedgerQueryService _svc;

    public CustomerLedgerController(ICustomerLedgerQueryService svc) => _svc = svc;

    [HttpGet("ledger")]
    [RequirePermission(PermissionCodes.CUSTOMER_LEDGER_VIEW)]
    public async Task<IActionResult> GetLedger(Guid partnerId, [FromQuery] CustomerLedgerFilter filter)
    {
        var result = await _svc.GetLedgerAsync(partnerId, filter);
        return Ok(ApiResponse<PaginatedResponse<CustomerLedgerEntryModel>>.Ok(result));
    }
}
