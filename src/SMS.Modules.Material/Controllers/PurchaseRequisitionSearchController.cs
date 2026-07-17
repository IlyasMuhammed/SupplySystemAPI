using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Material.Models;
using SMS.Modules.Material.Services;
using SMS.Shared.Pagination;

namespace SMS.Modules.Material.Controllers;

[ApiController]
[Route("api/purchase-requisitions")]
public class PurchaseRequisitionSearchController : ControllerBase
{
    private readonly IPrLookupService _service;

    public PurchaseRequisitionSearchController(IPrLookupService service) => _service = service;

    [HttpGet("search")]
    public async Task<IActionResult> Search([FromQuery] Guid productId, [FromQuery] string status = "APPROVED")
    {
        var result = await _service.SearchAsync(productId, status);
        return Ok(ApiResponse<List<PrLineSearchResult>>.Ok(result));
    }

    [HttpGet("{prId:guid}/lines/{lineId:guid}/disbursements")]
    public async Task<IActionResult> GetDisbursements(Guid prId, Guid lineId)
    {
        var result = await _service.GetDisbursementsAsync(prId, lineId);
        return Ok(ApiResponse<List<PrLineDisbursementModel>>.Ok(result));
    }
}
