using SMS.Shared.Common;

namespace SMS.Modules.Finance.Domain;

// A35 — multi-currency (docs/multi-currency/ADDENDUM-35-ANALYSIS.md D-1, D-3, D-15). Owner: CUR.

/// <summary>
/// A35 C1 / D-1 — one of an organization's currencies. Identity is the global <c>lookups.Currencies</c> Guid
/// (<see cref="CurrencyId"/>); this row carries the per-organization configuration (formatting, active, order) and an
/// editable name/symbol (seeded from the catalog).
/// </summary>
internal class OrgCurrency : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     Uuid           { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId { get; set; }

    /// <summary>lookups.Currencies.Id (global).</summary>
    public Guid     CurrencyId     { get; set; }
    /// <summary>ISO 4217, 3 upper-case letters; immutable once created.</summary>
    public string   Code           { get; set; } = string.Empty;
    public string   Name           { get; set; } = string.Empty;
    public string   Symbol         { get; set; } = string.Empty;
    /// <summary>0..3 — drives all rounding of amounts in this currency (BR-C1-05).</summary>
    public int      DecimalPlaces  { get; set; } = 2;
    /// <summary>Smallest monetary unit (0.01, 1, 0.001).</summary>
    public decimal  Rounding       { get; set; } = 0.01m;
    /// <summary>"before" | "after".</summary>
    public string   SymbolPosition { get; set; } = CurrencyConventions.SymbolBefore;
    public bool     IsActive       { get; set; } = true;
    public int      DisplayOrder   { get; set; }

    public int?      CreatedBy    { get; set; }
    public DateTime  CreatedDate  { get; set; } = DateTime.UtcNow;
    public int?      ModifiedBy   { get; set; }
    public DateTime? ModifiedDate { get; set; }
}

/// <summary>
/// A35 C2 / D-3 — a date-ranged rate: <see cref="Rate"/> units of the organization's rate currency per 1 unit of
/// <see cref="CurrencyId"/>, for every day from <see cref="EffectiveFrom"/> to <see cref="EffectiveTo"/> inclusive.
/// The current row ends 9999-12-31. Ranges of one currency never overlap (service, SERIALIZABLE + app lock).
/// </summary>
internal class CurrencyRate : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     Uuid           { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId { get; set; }

    public Guid     CurrencyId     { get; set; }
    /// <summary>Snapshot of the currency code, for logs and the legacy endpoint.</summary>
    public string   CurrencyCode   { get; set; } = string.Empty;
    public decimal  Rate           { get; set; }
    public decimal  InverseRate    { get; set; }
    public DateOnly EffectiveFrom  { get; set; }
    public DateOnly EffectiveTo    { get; set; } = CurrencyConventions.OpenEnd;
    /// <summary>MANUAL | SYSTEM (the rate currency's permanent 1.0 row) | API_* (reserved).</summary>
    public string   Source         { get; set; } = CurrencyConventions.SourceManual;
    public string?  Notes          { get; set; }

    public int?      CreatedBy    { get; set; }
    public DateTime  CreatedDate  { get; set; } = DateTime.UtcNow;
    public int?      ModifiedBy   { get; set; }
    public DateTime? ModifiedDate { get; set; }
}

/// <summary>
/// A35 C7 / D-15 — one exchange difference (there is no GL: this row is the spec's "journal entry"). REALIZED rows are
/// written per allocation when a payment posts; UNREALIZED rows by the revaluation, one per open document per date.
/// <see cref="DifferenceBase"/> is + gain / − loss in <see cref="BaseCurrencyCode"/>. Written by FIN's services.
/// </summary>
internal class ExchangeDifference : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     Uuid           { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId { get; set; }

    /// <summary><see cref="ExchangeDifferenceKinds"/>.</summary>
    public string   Kind           { get; set; } = ExchangeDifferenceKinds.Realized;
    /// <summary><see cref="ExchangeDifferenceSides"/>.</summary>
    public string   Side           { get; set; } = ExchangeDifferenceSides.Receivable;

    /// <summary>SALES_INVOICE | SUPPLIER_INVOICE.</summary>
    public string   DocumentType   { get; set; } = string.Empty;
    public int      DocumentId     { get; set; }
    public Guid     DocumentUuid   { get; set; }
    public string?  DocumentNo     { get; set; }

    /// <summary>CUSTOMER_PAYMENT | SUPPLIER_PAYMENT; null for UNREALIZED.</summary>
    public string?  PaymentType    { get; set; }
    public int?     PaymentId      { get; set; }
    public Guid?    PaymentUuid    { get; set; }
    public string?  PaymentNo      { get; set; }
    /// <summary>PaymentAllocation.Id / SupplierPaymentLine.Id.</summary>
    public int?     AllocationId   { get; set; }
    /// <summary>suppliers.BusinessPartners.UUID.</summary>
    public Guid?    PartnerId      { get; set; }

    public Guid?    CurrencyId        { get; set; }
    public string   CurrencyCode      { get; set; } = string.Empty;
    public decimal  AmountCurrency    { get; set; }
    public decimal  BookedRate        { get; set; }
    public decimal  SettlementRate    { get; set; }
    public Guid?    BaseCurrencyId    { get; set; }
    public string   BaseCurrencyCode  { get; set; } = string.Empty;
    public decimal  BookedAmountBase  { get; set; }
    public decimal  SettledAmountBase { get; set; }
    public decimal  DifferenceBase    { get; set; }
    public string?  AccountCode       { get; set; }

    public DateTime  PostedAt        { get; set; } = DateTime.UtcNow;
    public DateOnly? RevaluationDate { get; set; }
    public int?      CreatedBy       { get; set; }
    public DateTime  CreatedDate     { get; set; } = DateTime.UtcNow;
}

internal static class ExchangeDifferenceKinds
{
    public const string Realized   = "REALIZED";
    public const string Unrealized = "UNREALIZED";
}

internal static class ExchangeDifferenceSides
{
    public const string Receivable = "RECEIVABLE";
    public const string Payable    = "PAYABLE";
}
