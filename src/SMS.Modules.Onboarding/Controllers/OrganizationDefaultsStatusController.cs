using Microsoft.AspNetCore.Mvc;
using SMS.Shared.Authorization;
using SMS.Shared.Pagination;
using SMS.Shared.Exceptions;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Onboarding.Controllers;

/// <summary>
/// A51 P7 (A51-D7) — progress of the default-data apply that runs in the background after a super admin creates an organization.
/// Platform super admin only; cheap to poll (one setup row, one logs query, no seeder is touched).
/// </summary>
[ApiController]
[Route("api/system/organizations/{id:guid}/defaults-status")]
[RequirePermission(PermissionCodes.PLATFORM_SUPER_ADMIN)]
[RequireSuperAdmin]
public sealed class OrganizationDefaultsStatusController : ControllerBase
{
    private readonly IOrganizationDefaultsStatusReader _reader;

    public OrganizationDefaultsStatusController(IOrganizationDefaultsStatusReader reader) => _reader = reader;

    [HttpGet]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var status = await _reader.GetAsync(id, ct) ?? throw new NotFoundException("Organization", id);
        return Ok(ApiResponse<OrganizationDefaultsStatus>.Ok(status));
    }
}
