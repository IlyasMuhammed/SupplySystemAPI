using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SMS.Modules.Warehouse.Models;
using SMS.Modules.Warehouse.Services;
using SMS.Shared.Pagination;

namespace SMS.Modules.Warehouse.Controllers;

// REQ-3.x — public, login-free supplier acknowledgment portal. Mirrors
// SMS.Modules.Demand.Controllers.RfqPortalController's anti-oracle design exactly.
[ApiController]
[Route("api/public/sro-portal")]
[AllowAnonymous]
public sealed class SroPortalController : ControllerBase
{
    private readonly ISroAckValidationService _validation;
    private readonly ISroAckSubmissionService _submission;

    public SroPortalController(ISroAckValidationService validation, ISroAckSubmissionService submission)
    {
        _validation = validation;
        _submission = submission;
    }

    /// <summary>
    /// Public endpoint: validate a supplier acknowledgment token and return the return order's
    /// public payload. Always returns HTTP 200 regardless of token state — never 404/410 — to
    /// prevent token-existence oracle attacks (same rationale as the RFQ portal).
    /// </summary>
    [HttpGet("{token}")]
    [EnableRateLimiting("sro-portal-per-ip")]
    public async Task<IActionResult> OpenAck(string token)
    {
        var result = await _validation.ValidateAsync(token);

        var response = result switch
        {
            SroAckValidationResult.Valid    v => new SroPortalResponse { Status = "VALID",    Payload = v.Payload },
            SroAckValidationResult.Consumed   => new SroPortalResponse { Status = "CONSUMED"  },
            SroAckValidationResult.Expired    => new SroPortalResponse { Status = "EXPIRED"   },
            SroAckValidationResult.Invalid    => new SroPortalResponse { Status = "INVALID"   },
            _                                 => new SroPortalResponse { Status = "INVALID"   }
        };

        return Ok(ApiResponse<SroPortalResponse>.Ok(response));
    }

    /// <summary>
    /// Public endpoint: accept the supplier's acknowledgment. Re-validates the token before writing
    /// — closes the two-tab race-condition gap. Always returns HTTP 200; rejection codes are in the
    /// Status field.
    /// </summary>
    [HttpPost("{token}/acknowledge")]
    [EnableRateLimiting("sro-portal-per-ip")]
    public async Task<IActionResult> Acknowledge(string token, [FromBody] SroAcknowledgeRequest request)
    {
        var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var result   = await _submission.SubmitAsync(token, request, clientIp);
        return Ok(ApiResponse<SroAcknowledgeResult>.Ok(result));
    }
}
