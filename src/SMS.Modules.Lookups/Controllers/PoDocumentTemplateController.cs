using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Lookups.Controllers;

[ApiController]
[Route("api/po-document-template")]
[RequirePermission(PermissionCodes.SYSTEM_CONFIGURE)]
public class PoDocumentTemplateController : ControllerBase
{
    private readonly IPoDocumentTemplateService _service;

    public PoDocumentTemplateController(IPoDocumentTemplateService service) => _service = service;

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var template = await _service.GetActiveAsync();
        return Ok(ApiResponse<PoDocumentTemplateModel?>.Ok(template));
    }

    [HttpPut]
    public async Task<IActionResult> Upsert([FromBody] UpsertPoDocumentTemplateRequest req)
    {
        var id = await _service.UpsertAsync(req, User.GetUserId());
        return Ok(ApiResponse<Guid>.Ok(id, StaticResponseMessage.recordUpdatedSuccessfully));
    }

    // Metadata only (token names + labels) for the "Insert Variable" picker in the template editor.
    [HttpGet("tokens")]
    public IActionResult GetTokens()
    {
        return Ok(ApiResponse<IReadOnlyList<PoDocumentTokenModel>>.Ok(_service.GetAvailableTokens()));
    }
}