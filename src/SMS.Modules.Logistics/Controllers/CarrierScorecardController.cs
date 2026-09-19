using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Logistics.Models;
using SMS.Modules.Logistics.Visibility;
using SMS.Shared.Authorization;
using SMS.Shared.Pagination;

namespace SMS.Modules.Logistics.Controllers;

/// <summary>
/// How each carrier has actually performed — on time, exceptions, evidence, and what it billed.
/// </summary>
/// <remarks>
/// <para>
/// Reading is <c>DELIVERY_VIEW</c>. The billing block needs <c>FREIGHT_INVOICE_VIEW</c> as well and
/// is withheld without it, for the reason <c>SHIPMENT_RATE_VIEW</c> exists: somebody who watches
/// parcels has no business seeing what the company pays to move them. When it is withheld the
/// response says so rather than showing zeroes.
/// </para>
/// <para>
/// There is no overall score. Rolling on-time, exceptions and billing accuracy into one number needs
/// weights nobody has agreed, and a carrier that is cheap and late would come out wherever those
/// weights put it.
/// </para>
/// </remarks>
[ApiController]
[Route("api/logistics/carrier-scorecard")]
[RequiresFeature("MODULE_LOGISTICS")]
public class CarrierScorecardController : ControllerBase
{
    private readonly ICarrierScorecardService _scorecard;
    public CarrierScorecardController(ICarrierScorecardService scorecard) => _scorecard = scorecard;

    [RequirePermission(PermissionCodes.DELIVERY_VIEW)]
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] ScorecardFilter filter, CancellationToken ct)
    {
        // Set here from the caller's claims, never from the request body — a flag the client can
        // send is a flag the client can lie about.
        filter.IncludeBilling = User.HasPermission(PermissionCodes.FREIGHT_INVOICE_VIEW);

        var scorecard = await _scorecard.GetAsync(filter, ct);

        return Ok(ApiResponse<CarrierScorecardModel>.Ok(scorecard));
    }
}
