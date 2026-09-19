using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Visibility;
using SMS.Shared.Authorization;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Logistics.Controllers;

/// <summary>
/// The inside half of the public tracking page — issuing a consignee's link, and taking it away.
/// </summary>
/// <remarks>
/// <para>
/// Issuing and revoking are <c>DELIVERY_EDIT</c>, not <c>DELIVERY_VIEW</c>. Creating a link is
/// creating an unauthenticated way into a consignment's progress, and re-issuing silently breaks
/// the one the consignee already has; neither is a reading act.
/// </para>
/// <para>
/// The path returned is relative. This module does not know what host it is served on, and a link
/// built from a guessed one is a link that goes nowhere.
/// </para>
/// </remarks>
[ApiController]
[Route("api/logistics/consignments/{consignmentUuid:guid}/tracking-link")]
[RequiresFeature("MODULE_LOGISTICS")]
public class TrackingLinksController : ControllerBase
{
    private readonly IPublicTrackingService _tracking;

    public TrackingLinksController(IPublicTrackingService tracking) => _tracking = tracking;

    /// <summary>The live link, or 404 when none has been issued. Never creates one.</summary>
    [RequirePermission(PermissionCodes.DELIVERY_VIEW)]
    [HttpGet]
    public async Task<IActionResult> Get(Guid consignmentUuid, CancellationToken ct)
    {
        var link = await _tracking.GetLinkAsync(consignmentUuid, ct);

        return link is null
            ? NotFound(ApiResponse.Fail("No tracking link has been issued for this consignment."))
            : Ok(ApiResponse<TrackingLinkModel>.Ok(link));
    }

    /// <summary>
    /// Issues a link. Re-issuing replaces the old one, which stops working at that moment — so it
    /// is also how a link sent to the wrong person is dealt with.
    /// </summary>
    [RequirePermission(PermissionCodes.DELIVERY_EDIT)]
    [HttpPost]
    public async Task<IActionResult> Issue(Guid consignmentUuid, CancellationToken ct)
    {
        var link = await _tracking.IssueAsync(consignmentUuid, User.GetUserId(), ct);

        return link is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<TrackingLinkModel>.Ok(
                link,
                link.ReplacedPrevious
                    ? "New link issued. The previous one has stopped working."
                    : "Link issued."));
    }

    [RequirePermission(PermissionCodes.DELIVERY_EDIT)]
    [HttpDelete]
    public async Task<IActionResult> Revoke(Guid consignmentUuid, CancellationToken ct)
    {
        var revoked = await _tracking.RevokeAsync(consignmentUuid, User.GetUserId(), ct);

        return revoked
            ? Ok(ApiResponse.Ok("Link revoked. It no longer opens anything."))
            : NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));
    }
}
