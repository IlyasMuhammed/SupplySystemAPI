using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Logistics.Controllers;

/// <summary>
/// Delivery orders — layer A of the four-layer model.
/// </summary>
/// <remarks>
/// Gated by <c>MODULE_LOGISTICS</c> and, per action, by <c>DELIVERY_VIEW</c>,
/// <c>DELIVERY_CREATE</c> or <c>DELIVERY_EDIT</c>. These were the first uses of
/// <c>[RequirePermission]</c> anywhere in this module — the legacy carrier and shipment
/// endpoints had only the feature gate.
/// </remarks>
[ApiController]
[Route("api/logistics/deliveries")]
[RequiresFeature("MODULE_LOGISTICS")]
public class DeliveriesController : ControllerBase
{
    private readonly IDeliveryService         _svc;
    private readonly IPickListService         _pickLists;
    private readonly IPackageService          _packages;
    private readonly IDeliveryDocumentService _documents;

    public DeliveriesController(
        IDeliveryService svc,
        IPickListService pickLists,
        IPackageService packages,
        IDeliveryDocumentService documents)
    {
        _svc       = svc;
        _pickLists = pickLists;
        _packages  = packages;
        _documents = documents;
    }

    [RequirePermission(PermissionCodes.DELIVERY_CREATE)]
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateDeliveryRequest req)
    {
        var uuid = await _svc.CreateAsync(req, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(uuid, StaticResponseMessage.recordCreatedSuccessfully));
    }

    /// <summary>
    /// Creates a delivery from an existing source document — a PO becomes an inbound ASN.
    /// </summary>
    /// <remarks>
    /// Lines and quantities come from the source, less whatever is already received or already
    /// advised on another delivery. Omit <c>Lines</c> to advise every outstanding line in full.
    /// </remarks>
    [RequirePermission(PermissionCodes.DELIVERY_CREATE)]
    [HttpPost("from-source")]
    public async Task<IActionResult> CreateFromSource([FromBody] CreateDeliveryFromSourceRequest req)
    {
        var uuid = await _svc.CreateFromSourceAsync(req, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(uuid, StaticResponseMessage.recordCreatedSuccessfully));
    }

    [RequirePermission(PermissionCodes.DELIVERY_VIEW)]
    [HttpGet]
    public async Task<IActionResult> GetList([FromQuery] DeliveryFilter filter)
    {
        var result = await _svc.GetListAsync(filter);
        return Ok(ApiResponse<PaginatedResponse<DeliveryListItemModel>>.Ok(result));
    }

    /// <remarks>
    /// Another organization's delivery returns 404, not 403 — the tenant query filter makes it
    /// simply not exist here, and answering 403 would confirm that the id is real.
    /// </remarks>
    [RequirePermission(PermissionCodes.DELIVERY_VIEW)]
    [HttpGet("{uuid:guid}")]
    public async Task<IActionResult> GetById(Guid uuid)
    {
        var detail = await _svc.GetByUuidAsync(uuid);
        return detail is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<DeliveryDetailModel>.Ok(detail));
    }

    [RequirePermission(PermissionCodes.DELIVERY_EDIT)]
    [HttpPatch("{uuid:guid}")]
    public async Task<IActionResult> Patch(Guid uuid, [FromBody] PatchDeliveryRequest req)
    {
        var updated = await _svc.PatchAsync(uuid, req, User.GetUserId());
        return updated
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordUpdatedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    /// <summary>
    /// Commits the delivery and hard-reserves its stock, so nothing else can promise the same
    /// units. Refused if any line is short — nothing is reserved, and the message says what.
    /// </summary>
    /// <remarks>
    /// Pass <c>onShortage: "SPLIT"</c> to release what stock can cover and move the balance to a
    /// new draft delivery against the same source. The default, <c>BLOCK</c>, refuses instead.
    /// </remarks>
    [RequirePermission(PermissionCodes.DELIVERY_EDIT)]
    [HttpPost("{uuid:guid}/release")]
    public async Task<IActionResult> Release(Guid uuid, [FromBody] ReleaseDeliveryRequest? req) =>
        Respond(await _svc.ReleaseAsync(uuid, req, User.GetUserId()),
                "Delivery released and stock reserved.");

    /// <summary>
    /// What this delivery could be released against right now — per line, from the same
    /// warehouse the reservation would draw from.
    /// </summary>
    [RequirePermission(PermissionCodes.DELIVERY_VIEW)]
    [HttpGet("{uuid:guid}/availability")]
    public async Task<IActionResult> Availability(Guid uuid)
    {
        var result = await _svc.GetAvailabilityAsync(uuid);
        return result is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<DeliveryAvailabilityModel>.Ok(result));
    }

    /// <summary>
    /// Generates the pick list for a released delivery and moves it to <c>PICKING</c>.
    /// </summary>
    /// <remarks>
    /// The instructions come from the stock this delivery already holds — the reservation chose
    /// the rows, FEFO, when it was released. This does not choose stock again.
    /// </remarks>
    [RequirePermission(PermissionCodes.PICKING)]
    [HttpPost("{uuid:guid}/pick-list")]
    public async Task<IActionResult> GeneratePickList(
        Guid uuid, [FromBody] GeneratePickListRequest? req)
    {
        var pickListUuid = await _pickLists.GenerateAsync(uuid, req, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(pickListUuid, "Pick list generated."));
    }

    /// <summary>The delivery's pick list — the live one, or the most recent if none is live.</summary>
    [RequirePermission(PermissionCodes.DELIVERY_VIEW)]
    [HttpGet("{uuid:guid}/pick-list")]
    public async Task<IActionResult> GetPickList(Guid uuid)
    {
        var pickList = await _pickLists.GetForDeliveryAsync(uuid);
        return pickList is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<PickListModel>.Ok(pickList));
    }

    /// <summary>
    /// Packs picked goods into a carton or pallet and returns its handling-unit id.
    /// </summary>
    /// <remarks>
    /// Only what was picked can be packed, less whatever is already in other cartons. Once every
    /// picked unit is in a box the delivery moves itself to <c>PACKED</c>. Omit
    /// <c>packageBarcode</c> to have one issued; supply it when the carton already carries a
    /// pre-printed handling-unit label.
    /// </remarks>
    [RequirePermission(PermissionCodes.DISPATCH)]
    [HttpPost("{uuid:guid}/packages")]
    public async Task<IActionResult> Pack(Guid uuid, [FromBody] PackRequest req)
    {
        var packageUuid = await _packages.PackAsync(uuid, req, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(packageUuid, "Package created."));
    }

    /// <summary>Every carton on this delivery, and what is still waiting for one.</summary>
    [RequirePermission(PermissionCodes.DELIVERY_VIEW)]
    [HttpGet("{uuid:guid}/packages")]
    public async Task<IActionResult> GetPackages(Guid uuid)
    {
        var packing = await _packages.GetForDeliveryAsync(uuid);
        return packing is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<DeliveryPackingModel>.Ok(packing));
    }

    /// <summary>Moves a packed delivery to the dock, ready to be issued.</summary>
    [RequirePermission(PermissionCodes.DISPATCH)]
    [HttpPost("{uuid:guid}/stage")]
    public async Task<IActionResult> Stage(Guid uuid) =>
        Respond(await _svc.StageAsync(uuid, User.GetUserId()), "Delivery staged.");

    /// <summary>
    /// Issues the goods — the point of no return. The stock leaves the books and the delivery can
    /// no longer be cancelled.
    /// </summary>
    /// <remarks>
    /// A delivery raised from an MIV or an SRO does <b>not</b> post the movement: those documents
    /// already did, and posting again would understate inventory by the quantity shipped. The
    /// response says which happened. A transfer posts both legs — out of one warehouse and into
    /// the other. Either way the stock reservation ends.
    /// </remarks>
    [RequirePermission(PermissionCodes.DISPATCH)]
    [HttpPost("{uuid:guid}/goods-issue")]
    public async Task<IActionResult> GoodsIssue(Guid uuid)
    {
        var result = await _svc.IssueAsync(uuid, User.GetUserId());
        return result is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<GoodsIssueResultModel>.Ok(
                result,
                result.PostedStock
                    ? "Goods issued and stock posted."
                    : "Goods issued. The source document had already posted the stock movement."));
    }

    /// <summary>
    /// The packing list — what is in each carton, batch by batch. Travels with the goods.
    /// </summary>
    [RequirePermission(PermissionCodes.DELIVERY_VIEW)]
    [HttpGet("{uuid:guid}/packing-list")]
    public async Task<IActionResult> PackingList(Guid uuid)
    {
        var (content, fileName) = await _documents.GeneratePackingListAsync(uuid);
        return File(content, "application/pdf", fileName);
    }

    /// <summary>
    /// The gate pass — how many pieces are leaving and who authorised it.
    /// </summary>
    /// <remarks>
    /// Counts and identifies packages; it does not itemise their contents. Available once the
    /// delivery is staged at the dock.
    /// </remarks>
    [RequirePermission(PermissionCodes.DELIVERY_VIEW)]
    [HttpGet("{uuid:guid}/gate-pass")]
    public async Task<IActionResult> GatePass(Guid uuid)
    {
        var (content, fileName) = await _documents.GenerateGatePassAsync(uuid);
        return File(content, "application/pdf", fileName);
    }

    /// <summary>Pauses a delivery, remembering the status to resume to.</summary>
    [RequirePermission(PermissionCodes.DELIVERY_EDIT)]
    [HttpPost("{uuid:guid}/hold")]
    public async Task<IActionResult> Hold(Guid uuid, [FromBody] DeliveryReasonRequest req) =>
        Respond(await _svc.HoldAsync(uuid, req, User.GetUserId()), "Delivery placed on hold.");

    /// <summary>Returns a held delivery to exactly the status the hold interrupted.</summary>
    [RequirePermission(PermissionCodes.DELIVERY_EDIT)]
    [HttpPost("{uuid:guid}/resume")]
    public async Task<IActionResult> Resume(Guid uuid) =>
        Respond(await _svc.ResumeAsync(uuid, User.GetUserId()), "Delivery resumed.");

    /// <summary>Abandons a delivery. Refused once the stock has been issued.</summary>
    [RequirePermission(PermissionCodes.DELIVERY_EDIT)]
    [HttpPost("{uuid:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid uuid, [FromBody] DeliveryReasonRequest req) =>
        Respond(await _svc.CancelAsync(uuid, req, User.GetUserId()), "Delivery cancelled.");

    /// <summary>Closes a delivery for less than was ordered, recording the shortfall per line.</summary>
    [RequirePermission(PermissionCodes.DELIVERY_EDIT)]
    [HttpPost("{uuid:guid}/short-close")]
    public async Task<IActionResult> ShortClose(Guid uuid, [FromBody] DeliveryReasonRequest req) =>
        Respond(await _svc.ShortCloseAsync(uuid, req, User.GetUserId()), "Delivery short-closed.");

    private IActionResult Respond(bool found, string message) =>
        found
            ? Ok(ApiResponse.Ok(message))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));

    [RequirePermission(PermissionCodes.DELIVERY_EDIT)]
    [HttpDelete("{uuid:guid}")]
    public async Task<IActionResult> Delete(Guid uuid)
    {
        var deleted = await _svc.DeleteAsync(uuid);
        return deleted
            ? Ok(ApiResponse.Ok(StaticResponseMessage.recordDeletedSuccessfully))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }
}
