namespace SMS.Modules.Finance.Models;

// SAP alignment — finance master data (docs/finance/SAP-ALIGNMENT-PLAN.md): tax codes and exchange rates.
// The shapes match SupplyChainFrontend/src/app/services/finance-setup.service.ts (camelCase over the wire).
// Dates that are only dates (an exchange rate's effective date) travel as "yyyy-MM-dd" strings, so no
// time zone can move them to the day before.

// ── Tax codes ────────────────────────────────────────────────────────────────

public sealed class TaxCodeModel
{
    public Guid    Uuid        { get; set; }
    public string  Code        { get; set; } = string.Empty;
    public string  Name        { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal RatePercent { get; set; }
    /// <summary>SALES, PURCHASE or BOTH.</summary>
    public string  Usage       { get; set; } = string.Empty;
    public bool    IsDefault   { get; set; }
    public bool    IsActive    { get; set; }
}

/// <summary>Create (POST) and change (PUT) a tax code. The whole code is sent each time.</summary>
public sealed class SaveTaxCodeRequest
{
    /// <summary>Trimmed and upper-cased; 1–20 of A–Z, 0–9, '_' and '-'; unique in the organization.</summary>
    public string? Code        { get; set; }
    public string? Name        { get; set; }
    public string? Description { get; set; }
    /// <summary>0–100, at most two decimals.</summary>
    public decimal RatePercent { get; set; }
    /// <summary>SALES, PURCHASE or BOTH.</summary>
    public string? Usage       { get; set; }
    /// <summary>Makes this the pre-selected code for its side(s); the code that was the default there stops being it.</summary>
    public bool    IsDefault   { get; set; }
    /// <summary>False deactivates the code (it is never deleted). An inactive code is never a default.</summary>
    public bool    IsActive    { get; set; } = true;
}

/// <summary>What a save did, in words for the person who did it — e.g. that another code stopped being the default.</summary>
public sealed record TaxCodeSaved(TaxCodeModel TaxCode, string Message);

public sealed class TaxCodesFromRatesResult
{
    public List<TaxCodeModel> Created      { get; set; } = [];
    /// <summary>Rates already covered by an active sales code of the same rate.</summary>
    public List<decimal>      SkippedRates { get; set; } = [];
}

// ── Exchange rates ───────────────────────────────────────────────────────────

public sealed class ExchangeRateModel
{
    public Guid     Uuid             { get; set; }
    public string   FromCurrencyCode { get; set; } = string.Empty;
    public string   ToCurrencyCode   { get; set; } = string.Empty;
    /// <summary>1 unit of FromCurrencyCode = Rate units of ToCurrencyCode.</summary>
    public decimal  Rate             { get; set; }
    /// <summary>yyyy-MM-dd</summary>
    public string   EffectiveDate    { get; set; } = string.Empty;
    public string   Source           { get; set; } = string.Empty;
    public string?  Notes            { get; set; }
    public DateTime CreatedDate      { get; set; }
}

/// <summary>Create (POST) and change (PUT) a rate.</summary>
public sealed class SaveExchangeRateRequest
{
    public string? FromCurrencyCode { get; set; }
    public string? ToCurrencyCode   { get; set; }
    /// <summary>Greater than 0, at most eight decimals.</summary>
    public decimal Rate             { get; set; }
    /// <summary>yyyy-MM-dd. A full ISO date-time is accepted too; only its date part, as written, is used.</summary>
    public string? EffectiveDate    { get; set; }
    public string? Notes            { get; set; }
}

public sealed class ExchangeRateQuoteModel
{
    public string  FromCurrencyCode { get; set; } = string.Empty;
    public string  ToCurrencyCode   { get; set; } = string.Empty;
    public decimal Rate             { get; set; }
    /// <summary>yyyy-MM-dd — the date of the stored rate used.</summary>
    public string  EffectiveDate    { get; set; } = string.Empty;
    /// <summary>True when only the opposite pair was on file and this is its reciprocal.</summary>
    public bool    Inverted         { get; set; }
}
