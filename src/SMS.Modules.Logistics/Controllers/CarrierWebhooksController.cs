using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SMS.Modules.Logistics.Couriers.Tracking;
using SMS.Shared.Pagination;

namespace SMS.Modules.Logistics.Controllers;

/// <summary>
/// Where carriers push tracking events. <b>The one anonymous endpoint in the module.</b>
/// </summary>
/// <remarks>
/// A carrier cannot log in, so there is no user, no tenant and no permission here — which is why
/// there is also no <c>[RequiresFeature]</c> (an anonymous request bypasses that filter, so it would
/// check nothing) and no <c>[RequirePermission]</c>. What guards it instead, in order:
/// <list type="number">
/// <item>a rate limit per carrier account;</item>
/// <item>a 1 MB body limit;</item>
/// <item>the account, carrier, provider, integration mode and the organization's module switch all
/// matching — or the same 404 for every mismatch;</item>
/// <item>the carrier's signature, verified with that account's own secret before anything is kept.</item>
/// </list>
/// Configure the carrier to send to <c>/api/logistics/webhooks/{providerKey}/{carrierAccountUuid}</c>.
/// </remarks>
[ApiController]
[Route("api/logistics/webhooks")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicy)]
public class CarrierWebhooksController : ControllerBase
{
    /// <summary>Registered in SMS.API's Program.cs.</summary>
    public const string RateLimitPolicy = "carrier-webhooks";

    public const int MaxBodyBytes = 1024 * 1024;

    private readonly ICarrierWebhookService _webhooks;

    public CarrierWebhooksController(ICarrierWebhookService webhooks) => _webhooks = webhooks;

    [HttpPost("{providerKey}/{accountUuid:guid}")]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<IActionResult> Receive(string providerKey, Guid accountUuid, CancellationToken ct)
    {
        byte[] body;

        using (var buffer = new MemoryStream())
        {
            await Request.Body.CopyToAsync(buffer, ct);

            // Belt and braces for hosts where the attribute's limit is not applied.
            if (buffer.Length > MaxBodyBytes)
                return StatusCode(StatusCodes.Status413PayloadTooLarge, ApiResponse.Fail("The delivery is too large."));

            body = buffer.ToArray();
        }

        var headers = Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);

        var receipt = await _webhooks.ReceiveAsync(providerKey, accountUuid, headers, body, ct: ct);

        return StatusCode(receipt.StatusCode, receipt.StatusCode < 400
            ? ApiResponse.Ok(receipt.Message, new { receipt.Recorded, receipt.Duplicates, receipt.Unmatched })
            : ApiResponse.Fail(receipt.Message));
    }
}
