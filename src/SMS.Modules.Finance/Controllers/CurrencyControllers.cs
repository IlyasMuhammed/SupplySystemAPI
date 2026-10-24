using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Finance.Models;
using SMS.Modules.Finance.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Constants;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.Controllers;

// A35 — multi-currency (docs/multi-currency/API-CONTRACT.md §1–3, D-16, D-17). No MODULE_FINANCE feature gate: an organization
// sells and buys in currencies without the Finance module switched on. Bad input / clashes / missing rows come back from the
// services as BadRequest / Conflict / NotFound and become 400 / 409 / 404 in the global exception middleware.

[ApiController]
[Route("api/currencies")]
public class CurrenciesController : ControllerBase
{
    private readonly IOrgCurrencyService _svc;
    public CurrenciesController(IOrgCurrencyService svc) => _svc = svc;

    [HttpGet]
    [RequirePermission(PermissionCodes.CURRENCY_VIEW)]
    public async Task<IActionResult> GetList([FromQuery] bool includeInactive = false) =>
        Ok(ApiResponse<IReadOnlyList<OrgCurrencyModel>>.Ok(await _svc.ListAsync(includeInactive)));

    [HttpGet("{currencyId:guid}")]
    [RequirePermission(PermissionCodes.CURRENCY_VIEW)]
    public async Task<IActionResult> Get(Guid currencyId) =>
        Ok(ApiResponse<OrgCurrencyModel>.Ok(await _svc.GetAsync(currencyId)));

    [HttpPost]
    [RequirePermission(PermissionCodes.CURRENCY_MANAGE)]
    public async Task<IActionResult> Create([FromBody] SaveOrgCurrencyRequest req) =>
        Ok(ApiResponse<OrgCurrencyModel>.Ok(await _svc.CreateAsync(req, User.GetUserId()), StaticResponseMessage.recordCreatedSuccessfully));

    [HttpPut("{currencyId:guid}")]
    [RequirePermission(PermissionCodes.CURRENCY_MANAGE)]
    public async Task<IActionResult> Update(Guid currencyId, [FromBody] SaveOrgCurrencyRequest req) =>
        Ok(ApiResponse<OrgCurrencyModel>.Ok(await _svc.UpdateAsync(currencyId, req, User.GetUserId()), "Currency updated."));
}

[ApiController]
[Route("api/currency-rates")]
public class CurrencyRatesController : ControllerBase
{
    private readonly ICurrencyRateService _svc;
    public CurrencyRatesController(ICurrencyRateService svc) => _svc = svc;

    /// <summary>Rows whose range overlaps [from, to] (either optional), narrowed to one currency.</summary>
    [HttpGet]
    [RequirePermission(PermissionCodes.CURRENCY_RATE_VIEW)]
    public async Task<IActionResult> GetList([FromQuery] Guid? currencyId, [FromQuery] string? from, [FromQuery] string? to) =>
        Ok(ApiResponse<IReadOnlyList<CurrencyRateModel>>.Ok(await _svc.ListAsync(currencyId, from, to)));

    [HttpGet("active")]
    [RequirePermission(PermissionCodes.CURRENCY_RATE_VIEW)]
    public async Task<IActionResult> GetActive() =>
        Ok(ApiResponse<IReadOnlyList<CurrencyRateModel>>.Ok(await _svc.ActiveAsync()));

    [HttpGet("history/{currencyId:guid}")]
    [RequirePermission(PermissionCodes.CURRENCY_RATE_VIEW)]
    public async Task<IActionResult> GetHistory(Guid currencyId) =>
        Ok(ApiResponse<IReadOnlyList<CurrencyRateModel>>.Ok(await _svc.HistoryAsync(currencyId)));

    /// <summary>The rate covering <c>date</c> (default today); 400 with the missing-rate message when none.</summary>
    [HttpGet("{currencyId:guid}")]
    [RequirePermission(PermissionCodes.CURRENCY_RATE_VIEW)]
    public async Task<IActionResult> GetForDate(Guid currencyId, [FromQuery] string? date) =>
        Ok(ApiResponse<CurrencyRateModel>.Ok(await _svc.ForDateAsync(currencyId, date)));

    [HttpPost]
    [RequirePermission(PermissionCodes.CURRENCY_RATE_MANAGE)]
    public async Task<IActionResult> Insert([FromBody] SaveCurrencyRateRequest req)
    {
        var result = await _svc.InsertAsync(req, User.GetUserId());
        return Ok(ApiResponse<CurrencyRateInsertResult>.Ok(result,
            result.ClosedPrevious is null
                ? "Exchange rate saved."
                : $"Exchange rate saved. The previous rate now ends on {result.ClosedPrevious.EffectiveTo}."));
    }

    /// <summary>Historical correction. Documents that locked a rate keep it.</summary>
    [HttpPut("{id:guid}")]
    [RequirePermission(PermissionCodes.CURRENCY_RATE_MANAGE)]
    public async Task<IActionResult> Update(Guid id, [FromBody] SaveCurrencyRateRequest req) =>
        Ok(ApiResponse<CurrencyRateModel>.Ok(await _svc.UpdateAsync(id, req, User.GetUserId()),
            "Exchange rate updated. Documents that already locked a rate keep it."));
}

[ApiController]
[Route("api/currency")]
public class CurrencyConversionController : ControllerBase
{
    private readonly ICurrencyService  _currency;
    private readonly IOrgCurrencyLookup _lookup;
    private readonly ITenantContext    _tenant;

    public CurrencyConversionController(ICurrencyService currency, IOrgCurrencyLookup lookup, ITenantContext tenant)
    {
        _currency = currency;
        _lookup   = lookup;
        _tenant   = tenant;
    }

    [HttpPost("convert")]
    [RequirePermission(PermissionCodes.CURRENCY_VIEW, PermissionCodes.CURRENCY_RATE_VIEW)]
    public async Task<IActionResult> Convert([FromBody] ConvertCurrencyRequest req) =>
        Ok(ApiResponse<ConvertCurrencyResponse>.Ok(await ConvertAsync(_currency, _lookup, _tenant.OrganizationId, req)));

    internal static async Task<ConvertCurrencyResponse> ConvertAsync(
        ICurrencyService currency, IOrgCurrencyLookup lookup, Guid org, ConvertCurrencyRequest? req, CancellationToken ct = default)
    {
        if (req is null) throw new BadRequestException("Send the amount to convert.");
        if (req.FromCurrencyId is not Guid from || from == Guid.Empty)
            throw new BadRequestException("Choose the currency to convert from (fromCurrencyId).");

        var domain = (req.Domain?.Trim().ToUpperInvariant()) switch
        {
            null or "" or "SALE" => TransactionDomain.Sale,
            "PURCHASE"           => TransactionDomain.Purchase,
            "SERVICE"            => TransactionDomain.Service,
            var d                => throw new BadRequestException($"Domain must be SALE, PURCHASE or SERVICE; '{d}' is not.")
        };
        var date = CurrencyRateService.ParseDate(req.Date, "The date") ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var to   = req.ToCurrencyId is Guid t && t != Guid.Empty ? t : await currency.GetBaseCurrencyIdAsync(org, domain, ct);

        var result = await currency.ConvertAsync(org, req.Amount, from, to, date, ct);

        DateOnly effFrom = CurrencyConventions.SystemStart, effTo = CurrencyConventions.OpenEnd;
        if (from != to)
        {
            var f = await currency.GetRateAsync(org, from, date, ct);
            var g = await currency.GetRateAsync(org, to, date, ct);
            effFrom = f.EffectiveFrom > g.EffectiveFrom ? f.EffectiveFrom : g.EffectiveFrom;
            effTo   = f.EffectiveTo < g.EffectiveTo ? f.EffectiveTo : g.EffectiveTo;
        }

        return new ConvertCurrencyResponse
        {
            OriginalAmount  = req.Amount,
            FromCurrency    = await RefAsync(lookup, org, from, result.FromCurrencyCode, ct),
            ConvertedAmount = result.ConvertedAmount,
            ToCurrency      = await RefAsync(lookup, org, to, result.ToCurrencyCode, ct),
            RateUsed        = result.RateUsed,
            RateDate        = CurrencyRateService.Fmt(date),
            EffectiveFrom   = CurrencyRateService.Fmt(effFrom),
            EffectiveTo     = CurrencyRateService.Fmt(effTo),
            Domain          = CurrencyConventions.DomainCode(domain)
        };
    }

    private static async Task<CurrencyRefModel> RefAsync(IOrgCurrencyLookup lookup, Guid org, Guid id, string code, CancellationToken ct)
    {
        var c = await lookup.GetAsync(org, id, ct);
        return new CurrencyRefModel
        {
            Id = id, Code = c?.Code ?? code, Symbol = c?.Symbol ?? code,
            DecimalPlaces = c?.DecimalPlaces ?? CurrencyConventions.DefaultDecimalPlaces
        };
    }
}

/// <summary>A46 C8 — body of POST api/finance/convert-currency. Each side by id or ISO code (id wins); date yyyy-MM-dd, default today.</summary>
public sealed class ConvertCurrencyA46Request
{
    public decimal Amount           { get; set; }
    public Guid?   FromCurrencyId   { get; set; }
    public string? FromCurrencyCode { get; set; }
    public Guid?   ToCurrencyId     { get; set; }
    public string? ToCurrencyCode   { get; set; }
    public string? EffectiveDate    { get; set; }
    /// <summary>0..6, default 4.</summary>
    public int?    DecimalPlaces    { get; set; }
}

/// <summary>A46 C8 — the converter's result. A missing rate is 200 with <c>Success=false</c> and an <c>ErrorMessage</c>.</summary>
public sealed class ConvertCurrencyA46Response
{
    public bool    Success          { get; set; }
    public decimal ConvertedAmount  { get; set; }
    public decimal RateUsed         { get; set; }
    public string  RateDate         { get; set; } = string.Empty;
    public string  FromCurrencyCode { get; set; } = string.Empty;
    public string  ToCurrencyCode   { get; set; } = string.Empty;
    public string? ErrorMessage     { get; set; }
}

/// <summary>
/// A46 C8 — POST api/finance/convert-currency over <see cref="ICurrencyConverter"/> (direct, inverse, cross via the base
/// currency). Additive: <c>api/currency/convert</c> (A35) is unchanged. Malformed input (no amount body, bad date) is 400;
/// "no rate on file" is 200 + success:false.
/// </summary>
[ApiController]
[Route("api/finance/convert-currency")]
public class FinanceConvertCurrencyController : ControllerBase
{
    private readonly ICurrencyConverter _converter;
    public FinanceConvertCurrencyController(ICurrencyConverter converter) => _converter = converter;

    [HttpPost]
    [RequirePermission(PermissionCodes.CURRENCY_VIEW, PermissionCodes.CURRENCY_RATE_VIEW)]
    public async Task<IActionResult> Convert([FromBody] ConvertCurrencyA46Request? req, CancellationToken ct)
    {
        if (req is null) throw new BadRequestException("Send the amount to convert.");
        if (req.DecimalPlaces is < 0 or > 6) throw new BadRequestException("decimalPlaces must be between 0 and 6.");
        var date = CurrencyRateService.ParseDate(req.EffectiveDate, "effectiveDate") ?? DateOnly.FromDateTime(DateTime.UtcNow);

        var r = await _converter.ConvertAsync(new CurrencyConvertRequest(
            req.Amount, req.FromCurrencyId, req.FromCurrencyCode, req.ToCurrencyId, req.ToCurrencyCode,
            date.ToDateTime(TimeOnly.MinValue), req.DecimalPlaces ?? 4), ct);

        return Ok(ApiResponse<ConvertCurrencyA46Response>.Ok(new ConvertCurrencyA46Response
        {
            Success = r.Success, ConvertedAmount = r.ConvertedAmount, RateUsed = r.RateUsed,
            RateDate = CurrencyRateService.Fmt(DateOnly.FromDateTime(r.RateDate)),
            FromCurrencyCode = r.FromCurrencyCode, ToCurrencyCode = r.ToCurrencyCode, ErrorMessage = r.ErrorMessage
        }));
    }
}
