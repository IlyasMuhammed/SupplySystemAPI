using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Onboarding.Models;
using SMS.Modules.Onboarding.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Onboarding.Controllers;

/// <summary>A51 P1 — the industries master for the Super Admin. Platform super admin only ([RequireSuperAdmin] checks the
/// is_super_admin claim; the permission code alone is carried by roles any organization can hold).</summary>
[ApiController]
[Route("api/system/industries")]
[RequirePermission(PermissionCodes.PLATFORM_SUPER_ADMIN)]
[RequireSuperAdmin]
public sealed class IndustriesAdminController : ControllerBase
{
    private readonly IIndustryService _industries;

    public IndustriesAdminController(IIndustryService industries) => _industries = industries;

    private int? UserId => User.GetUserId() is var id && id > 0 ? id : null;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(ApiResponse<List<IndustryModel>>.Ok(await _industries.ListAsync(ct)));

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken ct) =>
        Ok(ApiResponse<IndustryModel>.Ok(await _industries.GetAsync(id, ct)));

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveIndustryRequest request, CancellationToken ct) =>
        Ok(ApiResponse<IndustryModel>.Ok(await _industries.CreateAsync(request ?? new(), UserId, ct), "Industry created."));

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] SaveIndustryRequest request, CancellationToken ct) =>
        Ok(ApiResponse<IndustryModel>.Ok(await _industries.UpdateAsync(id, request ?? new(), UserId, ct), "Industry saved."));

    [HttpPost("{id:int}/countries")]
    public async Task<IActionResult> AssignToCountry(int id, [FromBody] AssignIndustryCountryRequest request, CancellationToken ct) =>
        Ok(ApiResponse<IndustryModel>.Ok(await _industries.AssignToCountryAsync(id, request?.CountryCode ?? string.Empty, UserId, ct), "Country assigned."));

    [HttpDelete("{id:int}/countries/{countryCode}")]
    public async Task<IActionResult> RemoveFromCountry(int id, string countryCode, CancellationToken ct) =>
        Ok(ApiResponse<IndustryModel>.Ok(await _industries.RemoveFromCountryAsync(id, countryCode, ct), "Country removed."));
}

/// <summary>A51 P1 — the read side for ordinary screens (the create-organization form): any signed-in user, like the other
/// reference-data reads. <c>GET api/industries?country=PK</c> returns the active industries offered in that country.</summary>
[ApiController]
[Route("api/industries")]
public sealed class IndustriesController : ControllerBase
{
    private readonly IIndustryService _industries;

    public IndustriesController(IIndustryService industries) => _industries = industries;

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? country, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(country)) throw new BadRequestException("A country code is required (?country=PK).");
        return Ok(ApiResponse<List<IndustryModel>>.Ok(await _industries.GetByCountryAsync(country, ct)));
    }
}
