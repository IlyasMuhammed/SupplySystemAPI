namespace SMS.Modules.Finance.Models;

// A35 — API models for api/currencies, api/currency-rates, api/currency/convert (docs/multi-currency/API-CONTRACT.md §1–3).

public sealed class OrgCurrencyModel
{
    /// <summary>The org_currencies row Uuid.</summary>
    public Guid     Id             { get; set; }
    /// <summary>The global lookups.Currencies Id.</summary>
    public Guid     CurrencyId     { get; set; }
    public string   Code           { get; set; } = string.Empty;
    public string   Name           { get; set; } = string.Empty;
    public string   Symbol         { get; set; } = string.Empty;
    public int      DecimalPlaces  { get; set; }
    public decimal  Rounding       { get; set; }
    public string   SymbolPosition { get; set; } = "before";
    public bool     IsActive       { get; set; }
    public int      DisplayOrder   { get; set; }
    /// <summary>SALE / PURCHASE / SERVICE — domains whose base this currency is.</summary>
    public List<string> BaseFor    { get; set; } = [];
    public bool     IsRateCurrency { get; set; }
    public DateTime  CreatedAt     { get; set; }
    public DateTime? UpdatedAt     { get; set; }
}

public sealed class SaveOrgCurrencyRequest
{
    public string?  Code           { get; set; }
    public string?  Name           { get; set; }
    public string?  Symbol         { get; set; }
    public int?     DecimalPlaces  { get; set; }
    public decimal? Rounding       { get; set; }
    public string?  SymbolPosition { get; set; }
    public bool?    IsActive       { get; set; }
    public int?     DisplayOrder   { get; set; }
}

public sealed class CurrencyRateModel
{
    public Guid      Id               { get; set; }
    public Guid      CurrencyId       { get; set; }
    public string    CurrencyCode     { get; set; } = string.Empty;
    public string?   CurrencyName     { get; set; }
    public decimal   Rate             { get; set; }
    public decimal   InverseRate      { get; set; }
    /// <summary>yyyy-MM-dd</summary>
    public string    EffectiveFrom    { get; set; } = string.Empty;
    /// <summary>yyyy-MM-dd; 9999-12-31 = current.</summary>
    public string    EffectiveTo      { get; set; } = string.Empty;
    public bool      IsCurrent        { get; set; }
    public string    Source           { get; set; } = string.Empty;
    public string?   Notes            { get; set; }
    public Guid?     RateCurrencyId   { get; set; }
    public string?   RateCurrencyCode { get; set; }
    public DateTime  CreatedAt        { get; set; }
    public int?      CreatedBy        { get; set; }
    public DateTime? ModifiedAt       { get; set; }
    public int?      ModifiedBy       { get; set; }
}

/// <summary>POST (insert) and PUT (correct) a rate.</summary>
public sealed class SaveCurrencyRateRequest
{
    public Guid?   CurrencyId    { get; set; }
    public decimal Rate          { get; set; }
    /// <summary>yyyy-MM-dd</summary>
    public string? EffectiveFrom { get; set; }
    /// <summary>yyyy-MM-dd; null = open-ended (current).</summary>
    public string? EffectiveTo   { get; set; }
    public string? Notes         { get; set; }
}

public sealed class CurrencyRateInsertResult
{
    public CurrencyRateModel  Rate           { get; set; } = new();
    public CurrencyRateModel? ClosedPrevious { get; set; }
}

public sealed class ConvertCurrencyRequest
{
    public decimal Amount         { get; set; }
    public Guid?   FromCurrencyId { get; set; }
    /// <summary>Null = the organization's base for <see cref="Domain"/>.</summary>
    public Guid?   ToCurrencyId   { get; set; }
    /// <summary>yyyy-MM-dd; null = today.</summary>
    public string? Date           { get; set; }
    /// <summary>SALE (default) / PURCHASE / SERVICE.</summary>
    public string? Domain         { get; set; }
}

public sealed class CurrencyRefModel
{
    public Guid   Id            { get; set; }
    public string Code          { get; set; } = string.Empty;
    public string Symbol        { get; set; } = string.Empty;
    public int    DecimalPlaces { get; set; }
}

public sealed class ConvertCurrencyResponse
{
    public decimal          OriginalAmount  { get; set; }
    public CurrencyRefModel FromCurrency    { get; set; } = new();
    public decimal          ConvertedAmount { get; set; }
    public CurrencyRefModel ToCurrency      { get; set; } = new();
    public decimal          RateUsed        { get; set; }
    public string           RateDate        { get; set; } = string.Empty;
    public string           EffectiveFrom   { get; set; } = string.Empty;
    public string           EffectiveTo     { get; set; } = string.Empty;
    public string           Domain          { get; set; } = "SALE";
}
