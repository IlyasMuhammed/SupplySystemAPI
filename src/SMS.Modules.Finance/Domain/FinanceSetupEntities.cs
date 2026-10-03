using SMS.Shared.Common;

namespace SMS.Modules.Finance.Domain;

// Finance master data in the SAP mould (docs/finance/SAP-ALIGNMENT-PLAN.md): tax codes and exchange
// rates. Both live in Finance because Finance migrates itself at startup (Lookups does not), and both
// are read by other modules through SMS.Shared contracts (ITaxCodeLookup, IExchangeRateProvider) —
// Demand cannot reference Finance without a cycle.

/// <summary>
/// A named tax rate an organization charges or pays — "GST17", "EXEMPT". Document lines reference the
/// code <b>and keep the rate as a snapshot</b>, so changing a code's rate later never changes an
/// existing document. Deactivated, never deleted, once used.
/// </summary>
internal class TaxCode : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     Uuid           { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId { get; set; }

    /// <summary>Short, unique per organization, upper-case — what prints on documents. Max 20.</summary>
    public string   Code           { get; set; } = string.Empty;
    public string   Name           { get; set; } = string.Empty;
    public string?  Description    { get; set; }
    /// <summary>0–100, two decimals — the same precision as the TaxPercent columns it fills.</summary>
    public decimal  RatePercent    { get; set; }
    /// <summary>See <see cref="TaxCodeUsages"/>.</summary>
    public string   Usage          { get; set; } = TaxCodeUsages.Both;
    /// <summary>Pre-selected on new sale-order lines (sales) / supplier invoices (purchase). At most one per usage.</summary>
    public bool     IsDefault      { get; set; }
    public bool     IsActive       { get; set; } = true;

    public int       CreatedBy    { get; set; }
    public DateTime  CreatedDate  { get; set; } = DateTime.UtcNow;
    public int?      ModifiedBy   { get; set; }
    public DateTime? ModifiedDate { get; set; }
}

/// <summary>The Usage vocabulary, mirrored by SMS.Shared.Common.TaxCodeUsage.</summary>
internal static class TaxCodeUsages
{
    public const string Sales    = "SALES";
    public const string Purchase = "PURCHASE";
    public const string Both     = "BOTH";

    public static readonly IReadOnlyList<string> All = [Sales, Purchase, Both];
}

/// <summary>
/// One unit of <see cref="FromCurrencyCode"/> is worth <see cref="Rate"/> units of
/// <see cref="ToCurrencyCode"/>, from <see cref="EffectiveDate"/> until the next rate for the same
/// pair. Per organization (each organization's accountant owns its rates), keyed by ISO codes — the
/// form Finance documents already store currencies in. Soft-deleted, so a document that recorded a
/// rate can always be explained.
/// </summary>
internal class ExchangeRate : ITenantScopedEntity
{
    public int      Id               { get; set; }
    public Guid     Uuid             { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId   { get; set; }

    public string   FromCurrencyCode { get; set; } = string.Empty;
    public string   ToCurrencyCode   { get; set; } = string.Empty;
    /// <summary>decimal(18,8) — enough for both PKR→USD (0.0036…) and USD→PKR (278.5…).</summary>
    public decimal  Rate             { get; set; }
    /// <summary>Date only (no time): the rate applies from the start of this day.</summary>
    public DateTime EffectiveDate    { get; set; }
    /// <summary>MANUAL today; a bank/central-bank feed can write FEED later.</summary>
    public string   Source           { get; set; } = "MANUAL";
    public string?  Notes            { get; set; }

    public bool      IsDelete     { get; set; }
    public int       CreatedBy    { get; set; }
    public DateTime  CreatedDate  { get; set; } = DateTime.UtcNow;
    public int?      ModifiedBy   { get; set; }
    public DateTime? ModifiedDate { get; set; }
}
