using Microsoft.AspNetCore.Mvc;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Pagination;

namespace SMS.Modules.Auth.Controllers;

/// <summary>A51 P7 (A51-D12) — is e-mail set up, and how did the last send go. Platform super admin only; no credentials in the answer.</summary>
[ApiController]
[Route("api/system/email/status")]
[RequirePermission(PermissionCodes.PLATFORM_SUPER_ADMIN)]
[RequireSuperAdmin]
public sealed class EmailStatusController : ControllerBase
{
    private readonly IEmailStatusProvider _status;

    public EmailStatusController(IEmailStatusProvider status) => _status = status;

    [HttpGet]
    public IActionResult Get() => Ok(ApiResponse<EmailStatusModel>.Ok(_status.GetStatus()));
}
