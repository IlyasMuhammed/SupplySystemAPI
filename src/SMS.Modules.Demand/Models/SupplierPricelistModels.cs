using SMS.Modules.Demand.Domain;

namespace SMS.Modules.Demand.Models;

// A45 — docs/pricelist/ADDENDUM-45-ANALYSIS.md Part B (frozen API contract). JSON is camelCase; `id` of the spec is `uuid` here (D-2).

/// <summary>Writable fields of a pricelist row, shared by create and update.</summary>
public class SupplierPricelistWriteRequest
{
    public Guid ProductUuid { get; set; }
    /// <summary>Null = the row applies to every variant of the product.</summary>
    public Guid? VariantUuid { get; set; }
    public Guid SupplierUuid { get; set; }
    public string? VendorProductCode { get; set; }
    public string? VendorProductName { get; set; }
    public decimal MinQuantity { get; set; } = 1m;
    public decimal UnitPrice { get; set; }
    public Guid CurrencyId { get; set; }
    /// <summary>0 = Order date, 1 = Delivery date.</summary>
    public int PricingDateBasis { get; set; }
    public DateTime ValidFrom { get; set; }
    public DateTime ValidTo { get; set; }
    public int? LeadTimeDays { get; set; }
    public int Priority { get; set; }
    public string? Notes { get; set; }
    /// <summary>Optional: false deactivates, true (re-)activates (the dialog's Active toggle). Null = active on create, unchanged on update.</summary>
    public bool? IsActive { get; set; }
}

public class CreateSupplierPricelistRequest : SupplierPricelistWriteRequest { }

public class UpdateSupplierPricelistRequest : SupplierPricelistWriteRequest
{
    /// <summary>Base64 of the row's RowVersion as last read. Required; a stale value is a 409.</summary>
    public string? RowVersion { get; set; }
}

public class SupplierPricelistFilter
{
    public Guid? ProductUuid { get; set; }
    public Guid? VariantUuid { get; set; }
    public Guid? SupplierUuid { get; set; }
    public bool ActiveOnly { get; set; } = true;
    public string? Search { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public class PricelistItem
{
    public Guid Uuid { get; set; }
    public Guid ProductUuid { get; set; }
    public string? ProductName { get; set; }
    public string? ProductSku { get; set; }
    public Guid? VariantUuid { get; set; }
    public string? VariantName { get; set; }
    public Guid SupplierUuid { get; set; }
    public string? SupplierName { get; set; }
    public string? VendorProductCode { get; set; }
    public string? VendorProductName { get; set; }
    public decimal MinQuantity { get; set; }
    public decimal UnitPrice { get; set; }
    public Guid CurrencyId { get; set; }
    public string? CurrencyCode { get; set; }
    public int PricingDateBasis { get; set; }
    public string PricingDateBasisLabel { get; set; } = string.Empty;
    /// <summary>yyyy-MM-dd</summary>
    public string ValidFrom { get; set; } = string.Empty;
    /// <summary>yyyy-MM-dd</summary>
    public string ValidTo { get; set; } = string.Empty;
    public int? LeadTimeDays { get; set; }
    public int Priority { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ModifiedAt { get; set; }
    /// <summary>Base64.</summary>
    public string RowVersion { get; set; } = string.Empty;
}

/// <summary>Create / update response: the saved row, plus a warning (never a block) when another active row overlaps it (C6).</summary>
public class PricelistSaveResult
{
    public PricelistItem Item { get; set; } = new();
    public string? OverlapWarning { get; set; }
}

// ── Resolver ──────────────────────────────────────────────────────────────────

public class PriceResolutionRequest
{
    public Guid VariantUuid { get; set; }
    /// <summary>Optional; derived from the variant when Inventory is available.</summary>
    public Guid? ProductUuid { get; set; }
    public Guid SupplierUuid { get; set; }
    public decimal Quantity { get; set; }
    public DateTime OrderDate { get; set; }
    public DateTime? ExpectedDeliveryDate { get; set; }
    /// <summary>The purchase order's currency (the A45 name); null = the organization's purchase base. <see cref="PoCurrencyId"/> wins when both are given.</summary>
    public Guid? CurrencyId { get; set; }
    /// <summary>A46 C10 / E-9 — the currency the price must be expressed in (the PO's). Same meaning as <see cref="CurrencyId"/>; wins over it.</summary>
    public Guid? PoCurrencyId { get; set; }
    internal Guid? EffectiveCurrencyId => PoCurrencyId is { } p && p != Guid.Empty ? p : CurrencyId;
}

public class PriceResolutionResult
{
    public bool Found { get; set; }
    public decimal? UnitPrice { get; set; }
    public Guid? CurrencyId { get; set; }
    public string? CurrencyCode { get; set; }
    public Guid? SupplierPricelistUuid { get; set; }
    public PricingDateBasis? DateBasisUsed { get; set; }
    public DateTime? EffectiveDateUsed { get; set; }
    public int? LeadTimeDays { get; set; }
    public string? WarningMessage { get; set; }
    // A46 C10 / E-9 — the row's own price/currency and the conversion applied (see ResolvePriceResponse).
    public decimal? OriginalPrice { get; set; }
    public Guid? OriginalCurrencyId { get; set; }
    public string? OriginalCurrencyCode { get; set; }
    public decimal? ExchangeRateUsed { get; set; }
    public DateTime? ExchangeRateDate { get; set; }
    public bool CurrencyConverted { get; set; }
    public bool CurrencyMismatchWarning { get; set; }
}

/// <summary>Body of POST /api/purchase/resolve-price.</summary>
public class ResolvePriceRequest
{
    public Guid VariantUuid { get; set; }
    public Guid? ProductUuid { get; set; }
    public Guid SupplierUuid { get; set; }
    public decimal Quantity { get; set; }
    public DateTime OrderDate { get; set; }
    public DateTime? ExpectedDeliveryDate { get; set; }
    /// <summary>A45 name of the PO currency; accepted as before.</summary>
    public Guid? CurrencyId { get; set; }
    /// <summary>A46 C10 — the PO currency. Same meaning as <c>currencyId</c>; wins when both are sent.</summary>
    public Guid? PoCurrencyId { get; set; }
}

/// <summary>Response of POST /api/purchase/resolve-price. Not found is found:false with a warning, still a 200.</summary>
public class ResolvePriceResponse
{
    public bool Found { get; set; }
    public decimal? UnitPrice { get; set; }
    public Guid? CurrencyId { get; set; }
    public string? CurrencyCode { get; set; }
    public Guid? SupplierPricelistUuid { get; set; }
    /// <summary>"OrderDate" or "DeliveryDate".</summary>
    public string? DateBasisUsed { get; set; }
    /// <summary>yyyy-MM-dd</summary>
    public string? EffectiveDateUsed { get; set; }
    public int? LeadTimeDays { get; set; }
    public string? WarningMessage { get; set; }
    // A46 C10 / E-9 — set when the pricelist row is in another currency than the PO's: unitPrice is then the converted price (4dp) in
    // the PO currency, and these say what the row itself holds and which rate was used. All null / false for a same-currency price.
    // A price with no rate to convert it is found:true, unitPrice:null, currencyMismatchWarning:true (originalPrice/Currency still set).
    public decimal? OriginalPrice { get; set; }
    public Guid? OriginalCurrencyId { get; set; }
    public string? OriginalCurrencyCode { get; set; }
    public decimal? ExchangeRateUsed { get; set; }
    /// <summary>yyyy-MM-dd</summary>
    public string? ExchangeRateDate { get; set; }
    public bool CurrencyConverted { get; set; }
    public bool CurrencyMismatchWarning { get; set; }
}

