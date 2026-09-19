using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Visibility;
using SMS.Shared.Pagination;

namespace SMS.Modules.Logistics.Controllers;

/// <summary>
/// Where a consignee looks up their own delivery. <b>The second anonymous endpoint in the module</b>,
/// after the carrier webhook receiver.
/// </summary>
/// <remarks>
/// <para>
/// There is no user, no tenant and no permission here — which is why there is also no
/// <c>[RequiresFeature]</c> (an anonymous request bypasses that filter, so it would check nothing);
/// the service checks the organization's module switch itself instead.
/// </para>
/// <para>What guards it, in order:</para>
/// <list type="number">
/// <item>the token is 256 bits of randomness, so it cannot be guessed or walked;</item>
/// <item>a rate limit per IP, so it cannot be ground through either;</item>
/// <item>the same empty 404 for a wrong token, a revoked one, a deleted consignment, a deactivated
/// organization and one without Logistics — a different answer for any of them would confirm which
/// of the five it was;</item>
/// <item>the payload is a whitelist, so nothing added to <c>Consignment</c> later can leak here.</item>
/// </list>
/// <para>
/// <b>Deliberately not here:</b> addresses, the consignee's name, the value of the goods, any COD
/// amount, the airway bill, the carrier account, and the carrier's own free-text scan descriptions
/// — which are whatever the carrier chose to write, and carriers write street addresses in them.
/// </para>
/// </remarks>
[ApiController]
[Route("api/public/tracking")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicy)]
public class PublicTrackingController : ControllerBase
{
    /// <summary>Registered in SMS.API's Program.cs.</summary>
    public const string RateLimitPolicy = "public-tracking";

    private readonly IPublicTrackingService _tracking;

    public PublicTrackingController(IPublicTrackingService tracking) => _tracking = tracking;

    [HttpGet("{token}")]
    public async Task<IActionResult> Track(string token, CancellationToken ct)
    {
        var page = await _tracking.TrackAsync(token, ct);

        // One answer for every kind of miss. "No such consignment" and "that link was revoked" are
        // the same sentence on purpose.
        return page is null
            ? NotFound(ApiResponse.Fail("No delivery matches that link."))
            : Ok(ApiResponse<PublicTrackingModel>.Ok(page));
    }
}
