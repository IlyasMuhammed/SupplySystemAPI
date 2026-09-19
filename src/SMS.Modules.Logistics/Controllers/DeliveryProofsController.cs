using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Visibility;
using SMS.Shared.Authorization;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Logistics.Controllers;

/// <summary>
/// Proof that goods reached somebody — who took them, when, and the artefacts themselves.
/// </summary>
/// <remarks>
/// Reading is <c>DELIVERY_VIEW</c>. Recording and attaching are <c>POD_CAPTURE</c>, which is
/// deliberately separate: capturing a signature is what drivers and gate staff do, and it is also
/// the act that closes a movement — for a manual carrier, the only thing that ever marks one
/// delivered.
/// </remarks>
[ApiController]
[Route("api/logistics/delivery-proofs")]
[RequiresFeature("MODULE_LOGISTICS")]
public class DeliveryProofsController : ControllerBase
{
    /// <summary>
    /// Matches the service's own cap. Checked here as well so a 200 MB body is refused before it is
    /// buffered rather than after.
    /// </summary>
    private const int MaxUploadBytes = 10 * 1024 * 1024;

    private readonly IDeliveryProofService _proofs;
    public DeliveryProofsController(IDeliveryProofService proofs) => _proofs = proofs;

    /// <summary>What is delivered without evidence. The question an auditor asks first.</summary>
    [RequirePermission(PermissionCodes.DELIVERY_VIEW)]
    [HttpGet("coverage")]
    public async Task<IActionResult> GetCoverage(CancellationToken ct)
    {
        var coverage = await _proofs.GetCoverageAsync(ct);
        return Ok(ApiResponse<ProofCoverageModel>.Ok(coverage));
    }

    /// <summary>Every proof on a consignment — one for a parcel, one per drop on a multi-stop run.</summary>
    [RequirePermission(PermissionCodes.DELIVERY_VIEW)]
    [HttpGet("consignment/{consignmentUuid:guid}")]
    public async Task<IActionResult> GetForConsignment(Guid consignmentUuid, CancellationToken ct)
    {
        var proofs = await _proofs.GetForConsignmentAsync(consignmentUuid, ct);
        return Ok(ApiResponse<IReadOnlyList<DeliveryProofModel>>.Ok(proofs));
    }

    [RequirePermission(PermissionCodes.DELIVERY_VIEW)]
    [HttpGet("{uuid:guid}")]
    public async Task<IActionResult> Get(Guid uuid, CancellationToken ct)
    {
        var proof = await _proofs.GetAsync(uuid, ct);
        return proof is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<DeliveryProofModel>.Ok(proof));
    }

    /// <summary>
    /// Records a handover. Moves the consignment to DELIVERED where its state allows — the only
    /// thing that ever does for a manual carrier.
    /// </summary>
    [RequirePermission(PermissionCodes.POD_CAPTURE)]
    [HttpPost("consignment/{consignmentUuid:guid}")]
    public async Task<IActionResult> Record(
        Guid consignmentUuid, [FromBody] RecordProofRequest req, CancellationToken ct)
    {
        var uuid = await _proofs.RecordAsync(consignmentUuid, req, User.GetUserId(), ct);
        return uuid is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<Guid>.Ok(uuid.Value, "Proof of delivery recorded."));
    }

    /// <summary>
    /// Fills in what a carrier scan left blank — most often the name it never gave. The time of
    /// delivery is not amendable: that is the carrier's account of the event.
    /// </summary>
    [RequirePermission(PermissionCodes.POD_CAPTURE)]
    [HttpPatch("{uuid:guid}")]
    public async Task<IActionResult> Patch(
        Guid uuid, [FromBody] PatchProofRequest req, CancellationToken ct)
    {
        var patched = await _proofs.PatchAsync(uuid, req, User.GetUserId(), ct);
        return patched
            ? Ok(ApiResponse.Ok("Proof updated."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }

    /// <summary>
    /// Attaches the signature, a photograph or a scanned delivery note. The bytes are stored; the
    /// claimed type is checked against them, and the file name is built here.
    /// </summary>
    [RequirePermission(PermissionCodes.POD_CAPTURE)]
    [HttpPost("{uuid:guid}/files")]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<IActionResult> AttachFile(
        Guid uuid, [FromForm] string kind, IFormFile file, CancellationToken ct)
    {
        if (file is null || file.Length == 0)
            return BadRequest(ApiResponse.Fail("No file was uploaded."));

        if (file.Length > MaxUploadBytes)
            return BadRequest(ApiResponse.Fail(
                $"That file is larger than the {MaxUploadBytes / 1024 / 1024} MB limit."));

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);

        var fileUuid = await _proofs.AttachFileAsync(
            uuid, kind, buffer.ToArray(), file.ContentType, User.GetUserId(), ct);

        return fileUuid is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<Guid>.Ok(fileUuid.Value, "Attached."));
    }

    /// <summary>
    /// The artefact itself. Served as an attachment rather than inline — the content type is
    /// verified against the bytes on the way in, and this is the second lock on the same door.
    /// </summary>
    [RequirePermission(PermissionCodes.DELIVERY_VIEW)]
    [HttpGet("files/{fileUuid:guid}")]
    public async Task<IActionResult> GetFile(Guid fileUuid, CancellationToken ct)
    {
        var file = await _proofs.GetFileAsync(fileUuid, ct);

        return file is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : File(file.Content, file.ContentType, file.FileName);
    }

    /// <summary>
    /// Removes an artefact uploaded in error. Soft, always — evidence deleted by mistake and gone
    /// for good is the one deletion nobody can undo.
    /// </summary>
    [RequirePermission(PermissionCodes.POD_CAPTURE)]
    [HttpDelete("files/{fileUuid:guid}")]
    public async Task<IActionResult> RemoveFile(Guid fileUuid, CancellationToken ct)
    {
        var removed = await _proofs.RemoveFileAsync(fileUuid, User.GetUserId(), ct);
        return removed
            ? Ok(ApiResponse.Ok("Removed."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }
}
