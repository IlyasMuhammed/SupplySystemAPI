using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SMS.Modules.Integration.Core.Connections;

namespace SMS.Modules.Integration.Controllers.Admin;

/// <summary>
/// Where Intuit sends the browser back after consent. <b>The one anonymous endpoint in the module.</b>
/// </summary>
/// <remarks>
/// Intuit's redirect carries no authentication of ours, so there is no user, no tenant and no permission
/// here — and no <c>[RequiresFeature]</c> either (an anonymous request bypasses that filter, so it would
/// check nothing; the service checks the organization itself). What guards it instead:
/// <list type="number">
/// <item>a per-IP rate limit;</item>
/// <item>the single-use, ten-minute, hashed state token — the only thing that says which organization
/// is connecting, looked up by its unique hash and burned atomically;</item>
/// <item>the organization's status and its <c>MODULE_INTEGRATION</c> switch, checked by the service.</item>
/// </list>
/// Always answers with a redirect to the SCM screen (<c>?result=connected</c> or
/// <c>?result=error&amp;reason=…</c>), never an error page.
/// </remarks>
[ApiController]
[Route("api/integrations/quickbooks")]
[AllowAnonymous]
[EnableRateLimiting(RateLimitPolicy)]
public class CallbackController : ControllerBase
{
    /// <summary>Registered in ConnectionsRegistration.</summary>
    public const string RateLimitPolicy = "integration-oauth-callback";

    private readonly IQuickBooksCallbackService _callback;

    public CallbackController(IQuickBooksCallbackService callback) => _callback = callback;

    [HttpGet("callback")]
    public async Task<IActionResult> Callback(
        [FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? realmId, [FromQuery] string? error,
        CancellationToken ct) =>
        Redirect(await _callback.HandleCallbackAsync(code, state, realmId, error, ct));
}
