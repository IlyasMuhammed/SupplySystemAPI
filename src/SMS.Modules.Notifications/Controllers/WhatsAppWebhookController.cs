using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SMS.Modules.Notifications.Data;
using SMS.Shared.Common;
using Twilio.Security;

namespace SMS.Modules.Notifications.Controllers;

// Public — Twilio calls this server-to-server, there is no authenticated user. Protected instead
// by validating Twilio's request signature against the configured auth token, so only requests
// that actually originated from Twilio (for this exact URL + payload) are accepted.
[ApiController]
[Route("api/whatsapp")]
[AllowAnonymous]
public sealed class WhatsAppWebhookController : ControllerBase
{
    private readonly NotificationsDbContext _db;
    private readonly IConfiguration _config;
    private readonly IEnumerable<IWhatsAppStatusUpdateHandler> _statusHandlers;
    private readonly ILogger<WhatsAppWebhookController> _logger;

    public WhatsAppWebhookController(
        NotificationsDbContext db, IConfiguration config,
        IEnumerable<IWhatsAppStatusUpdateHandler> statusHandlers,
        ILogger<WhatsAppWebhookController> logger)
    {
        _db = db;
        _config = config;
        _statusHandlers = statusHandlers;
        _logger = logger;
    }

    [HttpPost("status-callback")]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> StatusCallback()
    {
        var form = await Request.ReadFormAsync();

        if (!IsValidTwilioSignature(form))
        {
            _logger.LogWarning("WhatsApp status callback rejected — invalid Twilio signature from {Ip}",
                HttpContext.Connection.RemoteIpAddress);
            return Unauthorized();
        }

        var messageSid   = form["MessageSid"].ToString();
        var status       = form["MessageStatus"].ToString(); // queued|sent|delivered|read|failed|undelivered
        var errorCode    = form["ErrorCode"].ToString();
        var errorMessage = form["ErrorMessage"].ToString();

        if (string.IsNullOrWhiteSpace(messageSid) || string.IsNullOrWhiteSpace(status))
            return Ok(); // malformed/irrelevant callback — acknowledge so Twilio doesn't retry

        var log = await _db.WhatsAppMessageLogs
            .FirstOrDefaultAsync(l => l.ProviderMessageId == messageSid);

        if (log is not null)
        {
            log.Status = status.ToUpperInvariant();
            log.ErrorCode = string.IsNullOrWhiteSpace(errorCode) ? null : errorCode;
            log.ErrorMessage = string.IsNullOrWhiteSpace(errorMessage) ? null : errorMessage;
            log.StatusUpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        else
        {
            _logger.LogWarning("WhatsApp status callback for unknown MessageSid {Sid} — no matching log row", messageSid);
        }

        foreach (var handler in _statusHandlers)
        {
            try
            {
                await handler.HandleStatusUpdateAsync(messageSid, status.ToUpperInvariant(),
                    string.IsNullOrWhiteSpace(errorCode) ? null : errorCode,
                    string.IsNullOrWhiteSpace(errorMessage) ? null : errorMessage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "WhatsApp status handler {Handler} failed for MessageSid {Sid}",
                    handler.GetType().Name, messageSid);
            }
        }

        return Ok();
    }

    private bool IsValidTwilioSignature(IFormCollection form)
    {
        var authToken = _config["WhatsApp:AuthToken"];
        if (string.IsNullOrWhiteSpace(authToken)) return false;

        if (!Request.Headers.TryGetValue("X-Twilio-Signature", out var signatureHeader))
            return false;

        // Must match the exact URL Twilio was configured to call — reconstruct from the request
        // rather than trusting anything client-suppliable beyond what ASP.NET Core resolves it to.
        var url = $"{Request.Scheme}://{Request.Host}{Request.Path}{Request.QueryString}";

        var parameters = form.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
        var validator = new RequestValidator(authToken);
        return validator.Validate(url, parameters, signatureHeader.ToString());
    }
}
