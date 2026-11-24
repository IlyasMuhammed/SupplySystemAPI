using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Pagination;

namespace SMS.Modules.Lookups.Controllers;

/// <summary>
/// A51 P1 — the country master (currency, timezone, fiscal start, date format, phone code). Platform super admin only: the
/// permission code is carried by roles any organization can hold, so [RequireSuperAdmin] checks the is_super_admin claim too.
/// Reads for ordinary screens stay on <c>api/lookups/countries</c>.
/// </summary>
[ApiController]
[Route("api/system/countries")]
[RequirePermission(PermissionCodes.PLATFORM_SUPER_ADMIN)]
[RequireSuperAdmin]
public sealed class CountriesAdminController : ControllerBase
{
    private readonly ICountryAdminService _service;

    public CountriesAdminController(ICountryAdminService service) => _service = service;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(ApiResponse<List<CountryAdminModel>>.Ok(await _service.ListAsync(ct)));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        Ok(ApiResponse<CountryAdminModel>.Ok(await _service.GetAsync(id, ct)));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveCountryRequest request, CancellationToken ct) =>
        Ok(ApiResponse<CountryAdminModel>.Ok(await _service.CreateAsync(request ?? new(), ct), "Country created."));

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] SaveCountryRequest request, CancellationToken ct) =>
        Ok(ApiResponse<CountryAdminModel>.Ok(await _service.UpdateAsync(id, request ?? new(), ct), "Country saved."));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await _service.DeleteAsync(id, ct);
        return Ok(ApiResponse<bool>.Ok(true, "Country deleted."));
    }

    [HttpPost("{id:guid}/activate")]
    public async Task<IActionResult> Activate(Guid id, CancellationToken ct) =>
        Ok(ApiResponse<CountryAdminModel>.Ok(await _service.SetActiveAsync(id, true, ct), "Country activated."));

    [HttpPost("{id:guid}/deactivate")]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct) =>
        Ok(ApiResponse<CountryAdminModel>.Ok(await _service.SetActiveAsync(id, false, ct), "Country deactivated."));
}
