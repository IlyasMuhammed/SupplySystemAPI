using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Constants;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.Controllers;

// SAP alignment — finance master data (docs/finance/SAP-ALIGNMENT-PLAN.md, "API contract").
//
// Reads need only a sign-in, and no MODULE_FINANCE: they feed the tax-code picker on sale orders and the
// rate shown on supplier invoices, and an organization can sell without the Finance module switched on.
// Writes need FINANCE_SETUP_MANAGE and the Finance module — these decide the tax and conversion every new
// document uses. Bad input, clashes and missing rows come back from the services as BadRequest / Conflict
// / NotFound exceptions and become 400 / 409 / 404 in the global exception middleware.

// ── Tax codes ─────────────────────────────────────────────────────────────────

[ApiController]
[Route("api/finance/tax-codes")]
public class TaxCodesController : ControllerBase
{
    private readonly ITaxCodeService _svc;

    public TaxCodesController(ITaxCodeService svc) => _svc = svc;

    /// <summary><c>side</c> SALES or PURCHASE limits to codes usable there (BOTH included). Active only unless <c>includeInactive</c>.</summary>
    [HttpGet]
    public async Task<IActionResult> GetList([FromQuery] string? side, [FromQuery] bool includeInactive = false)
    {
        var result = await _svc.ListAsync(side, includeInactive);
        return Ok(ApiResponse<IReadOnlyList<TaxCodeModel>>.Ok(result));
    }

    [HttpPost]
    [RequirePermission(PermissionCodes.FINANCE_SETUP_MANAGE)]
    [RequiresFeature("MODULE_FINANCE")]
    public async Task<IActionResult> Create([FromBody] SaveTaxCodeRequest req)
    {
        var saved = await _svc.CreateAsync(req, User.GetUserId());
        return Ok(ApiResponse<TaxCodeModel>.Ok(saved.TaxCode, saved.Message));
    }

    /// <summary>Changes a code; <c>isActive: false</c> deactivates it. Its rate may change — documents keep the rate they were raised with.</summary>
    [HttpPut("{uuid:guid}")]
    [RequirePermission(PermissionCodes.FINANCE_SETUP_MANAGE)]
    [RequiresFeature("MODULE_FINANCE")]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] SaveTaxCodeRequest req)
    {
        var saved = await _svc.UpdateAsync(uuid, req, User.GetUserId());
        return Ok(ApiResponse<TaxCodeModel>.Ok(saved.TaxCode, saved.Message));
    }

    /// <summary>One SALES code per distinct tax % already used on sale orders that no active code covers yet.</summary>
    [HttpPost("from-rates-in-use")]
    [RequirePermission(PermissionCodes.FINANCE_SETUP_MANAGE)]
    [RequiresFeature("MODULE_FINANCE")]
    public async Task<IActionResult> CreateFromRatesInUse()
    {
        var outcome = await _svc.CreateFromRatesInUseAsync(User.GetUserId());
        return Ok(ApiResponse<TaxCodesFromRatesResult>.Ok(outcome.Result, outcome.Message));
    }
}

// ── Exchange rates ────────────────────────────────────────────────────────────

[ApiController]
[Route("api/finance/exchange-rates")]
public class ExchangeRatesController : ControllerBase
{
    private readonly IExchangeRateService _svc;

    public ExchangeRatesController(IExchangeRateService svc) => _svc = svc;

    /// <summary>Live rates, newest effective date first; <c>from</c> / <c>to</c> narrow to a currency.</summary>
    [HttpGet]
    public async Task<IActionResult> GetList([FromQuery] string? from, [FromQuery] string? to)
    {
        var result = await _svc.ListAsync(from, to);
        return Ok(ApiResponse<IReadOnlyList<ExchangeRateModel>>.Ok(result));
    }

    /// <summary>The rate a document dated <c>date</c> (yyyy-MM-dd) would use for from→to. <c>result</c> is null when none is on file.</summary>
    [HttpGet("quote")]
    public async Task<IActionResult> Quote([FromQuery] string? from, [FromQuery] string? to, [FromQuery] string? date)
    {
        var quote = await _svc.QuoteAsync(from, to, date);
        return Ok(ApiResponse<ExchangeRateQuoteModel?>.Ok(
            quote,
            quote is null
                ? $"No {from?.Trim().ToUpperInvariant()} → {to?.Trim().ToUpperInvariant()} rate is on file for that date, directly or the other way round."
                : quote.Inverted
                    ? $"Worked out from the {quote.ToCurrencyCode} → {quote.FromCurrencyCode} rate of {quote.EffectiveDate}."
                    : $"The {quote.FromCurrencyCode} → {quote.ToCurrencyCode} rate of {quote.EffectiveDate}."));
    }

    [HttpPost]
    [RequirePermission(PermissionCodes.FINANCE_SETUP_MANAGE)]
    [RequiresFeature("MODULE_FINANCE")]
    public async Task<IActionResult> Create([FromBody] SaveExchangeRateRequest req)
    {
        var created = await _svc.CreateAsync(req, User.GetUserId());
        return Ok(ApiResponse<ExchangeRateModel>.Ok(created, StaticResponseMessage.recordCreatedSuccessfully));
    }

    [HttpPut("{uuid:guid}")]
    [RequirePermission(PermissionCodes.FINANCE_SETUP_MANAGE)]
    [RequiresFeature("MODULE_FINANCE")]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] SaveExchangeRateRequest req)
    {
        var updated = await _svc.UpdateAsync(uuid, req, User.GetUserId());
        return Ok(ApiResponse<ExchangeRateModel>.Ok(
            updated, "Exchange rate updated. Documents that already recorded the old rate keep it."));
    }

    /// <summary>Soft delete. Documents that recorded this rate keep it; new ones fall back to the previous rate for the pair.</summary>
    [HttpDelete("{uuid:guid}")]
    [RequirePermission(PermissionCodes.FINANCE_SETUP_MANAGE)]
    [RequiresFeature("MODULE_FINANCE")]
    public async Task<IActionResult> Delete(Guid uuid)
    {
        await _svc.DeleteAsync(uuid, User.GetUserId());
        return Ok(ApiResponse.Ok(StaticResponseMessage.recordDeletedSuccessfully));
    }
}
