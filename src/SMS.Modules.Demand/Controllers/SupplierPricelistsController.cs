using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Constants;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Demand.Controllers;

/// <summary>
/// A45 — supplier pricelists and the price resolution a purchase order line calls (docs/pricelist/ADDENDUM-45-ANALYSIS.md
/// Part B). Reads are for anybody who views, raises or edits purchase orders; changing a price record needs PO_CREATE or
/// PO_EDIT (D-8: the existing PO write codes). Rides MODULE_DEMAND (D-16). Rows are identified by UUID (D-2).
/// </summary>
[ApiController]
[Route("api/purchase")]
[RequiresFeature("MODULE_DEMAND")]
public class SupplierPricelistsController : ControllerBase
{
    private readonly ISupplierPricelistService _service;
    private readonly IPurchasePriceResolver    _resolver;
    private readonly IAuditService             _audit;

    public SupplierPricelistsController(ISupplierPricelistService service, IPurchasePriceResolver resolver, IAuditService audit)
    {
        _service  = service;
        _resolver = resolver;
        _audit    = audit;
    }

    private string Ip => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "";

    [HttpGet("supplier-pricelists")]
    [RequirePermission(PermissionCodes.PO_VIEW, PermissionCodes.PO_CREATE, PermissionCodes.PO_EDIT, PermissionCodes.PO_APPROVE)]
    public async Task<IActionResult> GetList([FromQuery] SupplierPricelistFilter filter, CancellationToken ct) =>
        Ok(ApiResponse<PaginatedResponse<PricelistItem>>.Ok(await _service.GetListAsync(filter, ct)));

    [HttpGet("supplier-pricelists/{uuid:guid}")]
    [RequirePermission(PermissionCodes.PO_VIEW, PermissionCodes.PO_CREATE, PermissionCodes.PO_EDIT, PermissionCodes.PO_APPROVE)]
    public async Task<IActionResult> GetById(Guid uuid, CancellationToken ct)
    {
        var item = await _service.GetByIdAsync(uuid, ct);
        return item is null
            ? NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound))
            : Ok(ApiResponse<PricelistItem>.Ok(item));
    }

    [HttpPost("supplier-pricelists")]
    [RequirePermission(PermissionCodes.PO_CREATE, PermissionCodes.PO_EDIT)]
    public async Task<IActionResult> Create([FromBody] CreateSupplierPricelistRequest req, CancellationToken ct)
    {
        var actor   = User.GetUserId();
        var outcome = await _service.CreateAsync(req, actor, ct);
        await _audit.LogAsync(actor, null, "Demand", "CREATE", "SupplierPricelist", outcome.Result.Item.Uuid, Ip,
            notes: $"Unit price {outcome.Result.Item.UnitPrice:0.####} {outcome.Result.Item.CurrencyCode}, min qty {outcome.Result.Item.MinQuantity:0.####}, " +
                   $"valid {outcome.Result.Item.ValidFrom} to {outcome.Result.Item.ValidTo}");
        return StatusCode(StatusCodes.Status201Created,
            ApiResponse<PricelistSaveResult>.Ok(outcome.Result, StaticResponseMessage.recordCreatedSuccessfully));
    }

    [HttpPut("supplier-pricelists/{uuid:guid}")]
    [RequirePermission(PermissionCodes.PO_CREATE, PermissionCodes.PO_EDIT)]
    public async Task<IActionResult> Update(Guid uuid, [FromBody] UpdateSupplierPricelistRequest req, CancellationToken ct)
    {
        var actor   = User.GetUserId();
        var outcome = await _service.UpdateAsync(uuid, req, actor, ct);
        await _audit.LogAsync(actor, null, "Demand", "UPDATE", "SupplierPricelist", uuid, Ip);
        foreach (var change in outcome.Changes)
            await _audit.LogAsync(actor, null, "Demand", "UPDATE", "SupplierPricelist", uuid, Ip,
                fieldChanged: change.Field, oldValue: change.OldValue, newValue: change.NewValue);
        return Ok(ApiResponse<PricelistSaveResult>.Ok(outcome.Result, StaticResponseMessage.recordUpdatedSuccessfully));
    }

    /// <summary>Soft delete (BR-16): the row stays for the purchase order lines that reference it, and is never resolved again.</summary>
    [HttpDelete("supplier-pricelists/{uuid:guid}")]
    [RequirePermission(PermissionCodes.PO_CREATE, PermissionCodes.PO_EDIT)]
    public async Task<IActionResult> Delete(Guid uuid, CancellationToken ct)
    {
        var actor = User.GetUserId();
        var item  = await _service.DeactivateAsync(uuid, actor, ct);
        if (item is null) return NotFound(ApiResponse.Fail(StaticResponseMessage.recordNotFound));

        await _audit.LogAsync(actor, null, "Demand", "DELETE", "SupplierPricelist", uuid, Ip,
            fieldChanged: "Active", oldValue: "true", newValue: "false");
        return Ok(ApiResponse.Ok(StaticResponseMessage.recordDeletedSuccessfully));
    }

    /// <summary>Resolves a line's price. Nothing found is a 200 with found:false and a warning, not an error (BR-04).</summary>
    [HttpPost("resolve-price")]
    [RequirePermission(PermissionCodes.PO_VIEW, PermissionCodes.PO_CREATE, PermissionCodes.PO_EDIT, PermissionCodes.PO_APPROVE)]
    public async Task<IActionResult> ResolvePrice([FromBody] ResolvePriceRequest req, CancellationToken ct)
    {
        if (req.VariantUuid == Guid.Empty)  throw new BadRequestException("A variant is required to resolve a price.");
        if (req.SupplierUuid == Guid.Empty) throw new BadRequestException("A supplier is required to resolve a price.");
        if (req.Quantity <= 0m)             throw new BadRequestException("Quantity must be greater than zero.");
        if (req.OrderDate == default)       throw new BadRequestException("An order date is required to resolve a price.");

        var result = await _resolver.ResolveAsync(new PriceResolutionRequest
        {
            VariantUuid          = req.VariantUuid,
            ProductUuid          = req.ProductUuid,
            SupplierUuid         = req.SupplierUuid,
            Quantity             = req.Quantity,
            OrderDate            = req.OrderDate,
            ExpectedDeliveryDate = req.ExpectedDeliveryDate,
            CurrencyId           = req.CurrencyId,
            PoCurrencyId         = req.PoCurrencyId   // A46 E-9: wins over the A45 name
        }, ct);

        return Ok(ApiResponse<ResolvePriceResponse>.Ok(new ResolvePriceResponse
        {
            Found                 = result.Found,
            UnitPrice             = result.UnitPrice,
            CurrencyId            = result.CurrencyId,
            CurrencyCode          = result.CurrencyCode,
            SupplierPricelistUuid = result.SupplierPricelistUuid,
            DateBasisUsed         = result.DateBasisUsed?.ToString(),
            EffectiveDateUsed     = result.EffectiveDateUsed?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            LeadTimeDays          = result.LeadTimeDays,
            WarningMessage        = result.WarningMessage,
            OriginalPrice            = result.OriginalPrice,
            OriginalCurrencyId       = result.OriginalCurrencyId,
            OriginalCurrencyCode     = result.OriginalCurrencyCode,
            ExchangeRateUsed         = result.ExchangeRateUsed,
            ExchangeRateDate         = result.ExchangeRateDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            CurrencyConverted        = result.CurrencyConverted,
            CurrencyMismatchWarning  = result.CurrencyMismatchWarning
        }));
    }
}
