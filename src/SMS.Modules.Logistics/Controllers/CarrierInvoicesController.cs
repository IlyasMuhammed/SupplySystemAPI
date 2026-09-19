using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Settlement;
using SMS.Shared.Authorization;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Logistics.Controllers;

/// <summary>
/// Carrier invoices — bills from carriers, and the third leg of the three-way match.
/// </summary>
/// <remarks>
/// Recording one is gated by <c>FREIGHT_INVOICE_RECONCILE</c> rather than the view permission: it
/// is the act that decides what the company accepts it owes.
/// </remarks>
[ApiController]
[Route("api/logistics/carrier-invoices")]
[RequiresFeature("MODULE_LOGISTICS")]
public class CarrierInvoicesController : ControllerBase
{
    private readonly ICarrierInvoiceService     _invoices;
    private readonly IInvoiceSettlementService  _settlement;

    public CarrierInvoicesController(
        ICarrierInvoiceService invoices, IInvoiceSettlementService settlement)
    {
        _invoices   = invoices;
        _settlement = settlement;
    }

    /// <summary>Where a bill stands, and what the company has accepted it owes.</summary>
    [RequirePermission(PermissionCodes.FREIGHT_INVOICE_VIEW)]
    [HttpGet("{uuid:guid}/settlement")]
    public async Task<IActionResult> GetSettlement(Guid uuid, CancellationToken ct)
    {
        var settlement = await _settlement.GetAsync(uuid, ct);
        return settlement is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<CarrierInvoiceSettlementModel>.Ok(settlement));
    }

    /// <summary>
    /// Accepts a bill for payment and closes the accruals it covers.
    /// </summary>
    /// <remarks>
    /// Refused while any line is still untied to a movement: approving then would accept charges
    /// nobody has checked. Approving for less than was billed needs a note — the carrier will ask,
    /// and the difference is what it will ask about.
    /// <para>
    /// <b>Approving does not pay anything.</b> Whether it goes on to Finance is decision G10.
    /// </para>
    /// </remarks>
    [RequirePermission(PermissionCodes.FREIGHT_INVOICE_RECONCILE)]
    [HttpPost("{uuid:guid}/approve")]
    public async Task<IActionResult> Approve(
        Guid uuid, [FromBody] ApproveCarrierInvoiceRequest? req, CancellationToken ct)
    {
        var settlement = await _settlement.ApproveAsync(
            uuid, req ?? new ApproveCarrierInvoiceRequest(), User.GetUserId(), ct);

        return settlement is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<CarrierInvoiceSettlementModel>.Ok(settlement, "Approved."));
    }

    /// <summary>Raises a query with the carrier. The accruals stay open — it is still owed.</summary>
    [RequirePermission(PermissionCodes.FREIGHT_INVOICE_RECONCILE)]
    [HttpPost("{uuid:guid}/dispute")]
    public async Task<IActionResult> RaiseDispute(
        Guid uuid, [FromBody] RaiseDisputeRequest req, CancellationToken ct)
    {
        var settlement = await _settlement.RaiseDisputeAsync(uuid, req, User.GetUserId(), ct);
        return settlement is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<CarrierInvoiceSettlementModel>.Ok(settlement, "Query raised."));
    }

    /// <summary>Records how the query ended, and approves the bill at whatever was settled.</summary>
    [RequirePermission(PermissionCodes.FREIGHT_INVOICE_RECONCILE)]
    [HttpPost("{uuid:guid}/dispute/resolve")]
    public async Task<IActionResult> ResolveDispute(
        Guid uuid, [FromBody] ResolveDisputeRequest req, CancellationToken ct)
    {
        var settlement = await _settlement.ResolveDisputeAsync(uuid, req, User.GetUserId(), ct);
        return settlement is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<CarrierInvoiceSettlementModel>.Ok(settlement, "Query closed."));
    }

    [RequirePermission(PermissionCodes.FREIGHT_INVOICE_RECONCILE)]
    [HttpPost]
    public async Task<IActionResult> Create(
        [FromBody] CreateCarrierInvoiceRequest req, CancellationToken ct)
    {
        var uuid = await _invoices.CreateAsync(req, User.GetUserId(), ct);
        return Ok(ApiResponse<Guid>.Ok(uuid, StaticResponseMessage.recordCreatedSuccessfully));
    }

    /// <summary>
    /// Brings in many bills at once. Each is judged on its own and reported separately.
    /// </summary>
    /// <remarks>
    /// One bad bill never fails the batch: an import that stopped at the first problem would leave
    /// somebody re-running it and re-importing everything before it, which is how duplicates get
    /// created. Parsing a carrier's own file layout belongs with that carrier, not here.
    /// </remarks>
    [RequirePermission(PermissionCodes.FREIGHT_INVOICE_RECONCILE)]
    [HttpPost("import")]
    public async Task<IActionResult> Import(
        [FromBody] ImportCarrierInvoicesRequest req, CancellationToken ct)
    {
        var result = await _invoices.ImportAsync(req, User.GetUserId(), ct);
        return Ok(ApiResponse<CarrierInvoiceImportModel>.Ok(result));
    }

    /// <summary>Bills, newest first. Searchable by invoice number, carrier, or airway bill.</summary>
    [RequirePermission(PermissionCodes.FREIGHT_INVOICE_VIEW)]
    [HttpGet]
    public async Task<IActionResult> GetList([FromQuery] CarrierInvoiceFilter filter, CancellationToken ct)
    {
        var result = await _invoices.GetListAsync(filter, ct);
        return Ok(ApiResponse<PaginatedResponse<CarrierInvoiceModel>>.Ok(result));
    }

    [RequirePermission(PermissionCodes.FREIGHT_INVOICE_VIEW)]
    [HttpGet("{uuid:guid}")]
    public async Task<IActionResult> GetById(Guid uuid, CancellationToken ct)
    {
        var invoice = await _invoices.GetByUuidAsync(uuid, ct);
        return invoice is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<CarrierInvoiceModel>.Ok(invoice));
    }

    /// <remarks>
    /// Sending <c>lines</c> replaces them all. A bill is corrected as a whole, so its lines and its
    /// header never describe two different bills.
    /// </remarks>
    [RequirePermission(PermissionCodes.FREIGHT_INVOICE_RECONCILE)]
    [HttpPatch("{uuid:guid}")]
    public async Task<IActionResult> Patch(
        Guid uuid, [FromBody] PatchCarrierInvoiceRequest req, CancellationToken ct)
    {
        var updated = await _invoices.PatchAsync(uuid, req, User.GetUserId(), ct);
        return updated
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    /// <summary>Withdraws a bill — a duplicate, or one keyed against the wrong carrier.</summary>
    [RequirePermission(PermissionCodes.FREIGHT_INVOICE_RECONCILE)]
    [HttpPost("{uuid:guid}/cancel")]
    public async Task<IActionResult> Cancel(
        Guid uuid, [FromBody] CancelCarrierInvoiceRequest req, CancellationToken ct)
    {
        var cancelled = await _invoices.CancelAsync(uuid, req, User.GetUserId(), ct);
        return cancelled
            ? Ok(ApiResponse.Ok("Invoice withdrawn."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }
}
