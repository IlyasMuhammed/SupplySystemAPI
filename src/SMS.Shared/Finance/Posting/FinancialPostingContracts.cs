using System.Text.Json.Serialization;

namespace SMS.Shared.Finance.Posting;

// A44 (SMS-FSD-ADD-044) — the single entry point for every SCM document that has a financial effect.
// Callers build a FinancialTransaction from their own data and call IFinancialPostingGateway after their
// own commit; the gateway (SMS.Modules.Accounting) sends it to every active target: the internal GL
// (Accounting) and external accounting systems (SMS.Modules.Integration: QuickBooks). Callers never
// reference a target. See docs/financial-posting/ADDENDUM-44-ANALYSIS.md (D-2, D-4, D-9, D-13).

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FinDocType
{
    SalesInvoice,
    CreditNote,
    Payment,
    PurchaseBill,
    BillPayment,
    VendorCredit,
    POSSale,
    POSReturn,
    SalesReceipt,
    RefundReceipt,
    SalesReturn,
    PurchaseReturn,
    InventoryAdjustment,
    ProductionCost,
    ProductionCompletion,
    ManualJournal,
    BankFee,
    Expense,
    DepositSlip,
    OpeningBalance
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PartnerType { Customer, Vendor }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PostingStatus { Success, PartialFailure, NoTargetConfigured, DuplicateSkipped, ValidationFailed, ModuleNotLicensed }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TargetPostingStatus { Posted, Queued, Held, Failed, Skipped }

/// <summary>Codes of the targets the gateway knows about.</summary>
public static class PostingTargetCodes
{
    public const string InternalGl = "internal_gl";
    public const string QuickBooks = "quickbooks";
}

/// <summary>Values of <see cref="FinancialTransaction.SourceModule"/>.</summary>
public static class PostingSourceModules
{
    public const string Finance   = "finance";
    public const string Pos       = "pos";
    public const string Suppliers = "suppliers";
    public const string Inventory = "inventory";
}

/// <summary>
/// A provider-neutral document an external target builds its native document from (D-4): the Shared payloads
/// in SMS.Shared.Integration.QuickBooks implement it. SCM-shaped (SCM codes and uuids), never provider-shaped.
/// </summary>
public interface IAccountingDocument
{
    /// <summary>The SCM row's uuid as a string — the upsert key at every target.</summary>
    string ExternalId { get; }
}

/// <summary>One posting. Lines are in document currency; <see cref="ExchangeRate"/> converts them to base.</summary>
public sealed class FinancialTransaction
{
    public FinDocType   DocumentType         { get; init; }
    /// <summary>The source document's Uuid.</summary>
    public Guid         SourceDocumentId     { get; init; }
    public string       SourceDocumentNumber { get; init; } = string.Empty;
    /// <summary>See <see cref="PostingSourceModules"/>.</summary>
    public string       SourceModule         { get; init; } = string.Empty;
    public DateTime     TransactionDate      { get; init; }
    /// <summary>The business partner's Uuid.</summary>
    public Guid?        PartnerId            { get; init; }
    public PartnerType? PartnerType          { get; init; }
    public string?      PartnerName          { get; init; }
    public string       CurrencyCode         { get; init; } = string.Empty;
    /// <summary>1 document-currency unit = ExchangeRate base units. Null = the document is in base currency.</summary>
    public decimal?     ExchangeRate         { get; init; }
    public string?      Description          { get; init; }
    public IReadOnlyList<FinancialTransactionLine> Lines { get; init; } = [];
    public FinancialDocumentMetadata? Metadata { get; init; }

    /// <summary>D-13: "{SourceModule}:{DocumentType}:{SourceDocumentId}".</summary>
    [JsonIgnore]
    public string IdempotencyKey => $"{SourceModule}:{DocumentType}:{SourceDocumentId}";
}

public sealed class FinancialTransactionLine
{
    public int      LineNumber  { get; init; }
    /// <summary>An A42 accounting default key (e.g. "DEFAULT_RECEIVABLE"), resolved to the tenant's account (D-5).</summary>
    public string?  AccountRole { get; init; }
    /// <summary>An explicit leaf account code; wins over <see cref="AccountRole"/>.</summary>
    public string?  AccountCode { get; init; }
    public decimal  Debit       { get; init; }
    public decimal  Credit      { get; init; }
    public string?  TaxCode     { get; init; }
    public decimal? TaxAmount   { get; init; }
    public string?  Label       { get; init; }
    public Guid?    PartnerId   { get; init; }
    public string?  CostCenter  { get; init; }
    public string?  Project     { get; init; }
}

public sealed class FinancialDocumentMetadata
{
    public DateTime? DueDate          { get; init; }
    public string?   PaymentTerms     { get; init; }
    public string?   Reference        { get; init; }
    public string?   WarehouseCode    { get; init; }
    /// <summary>The document this one relates to (the invoice a credit note credits, the original of a void…).</summary>
    public Guid?     LinkedDocumentId { get; init; }
    public IReadOnlyDictionary<string, string>? Properties { get; init; }
    /// <summary>
    /// The provider-neutral document external targets build from (D-4). Not serialized by the gateway's own
    /// posting log (the GL needs only the lines); external targets store it themselves.
    /// </summary>
    [JsonIgnore]
    public IAccountingDocument? Document { get; init; }
}

public sealed class VoidRequest
{
    public Guid       SourceDocumentId     { get; init; }
    public string     SourceModule         { get; init; } = string.Empty;
    public FinDocType DocumentType         { get; init; }
    public string     SourceDocumentNumber { get; init; } = string.Empty;
    public string     Reason               { get; init; } = string.Empty;
    public int?       VoidedBy             { get; init; }
    public DateTime?  VoidDate             { get; init; }

    [JsonIgnore]
    public string IdempotencyKey => $"{SourceModule}:{DocumentType}:{SourceDocumentId}:void";
}

public sealed class TargetResult
{
    public string              TargetCode { get; init; } = string.Empty;
    public TargetPostingStatus Status     { get; init; }
    public string?             InternalId { get; init; }
    public string?             ExternalId { get; init; }
    public string?             Error      { get; init; }

    public static TargetResult Skipped(string target, string reason) =>
        new() { TargetCode = target, Status = TargetPostingStatus.Skipped, Error = reason };
    public static TargetResult Failed(string target, string error) =>
        new() { TargetCode = target, Status = TargetPostingStatus.Failed, Error = error };
}

public sealed class PostingResult
{
    public PostingStatus      Status           { get; init; }
    public string?            Message          { get; init; }
    public Guid               SourceDocumentId { get; init; }
    public FinDocType         DocumentType     { get; init; }
    public List<TargetResult> TargetResults    { get; init; } = new();
}

/// <summary>
/// The posting gateway (implemented by SMS.Modules.Accounting). Call it <b>after</b> your own commit. It never
/// throws for a target failure (a failed target is a <see cref="TargetPostingStatus.Failed"/> result and the
/// gateway retries it), and it is idempotent by key: the same key and payload is DuplicateSkipped, the same key
/// with a changed payload is an update (D-13).
/// </summary>
public interface IFinancialPostingGateway
{
    Task<PostingResult> PostAsync(FinancialTransaction transaction, CancellationToken ct = default);
    /// <summary>Cancel / void a posted document at every target (GL: reversing journal).</summary>
    Task<PostingResult> VoidAsync(VoidRequest request, CancellationToken ct = default);
    /// <summary>A credit document (CreditNote, VendorCredit, RefundReceipt) — PostAsync with a check of the type.</summary>
    Task<PostingResult> IssueCreditAsync(FinancialTransaction credit, CancellationToken ct = default);
    /// <summary>D-26: a reversal that external systems only flag for the accountant (supplier invoice); GL reverses.</summary>
    Task<PostingResult> ReverseAsync(VoidRequest request, CancellationToken ct = default);
}

/// <summary>A destination the gateway posts to. Registered by the module that owns it.</summary>
public interface IFinancialPostingTarget
{
    /// <summary>See <see cref="PostingTargetCodes"/>.</summary>
    string TargetCode { get; }
    /// <summary>Whether this target takes postings for the ambient organization (D-10).</summary>
    Task<bool> IsActiveAsync(CancellationToken ct = default);
    Task<TargetResult> PostAsync(FinancialTransaction transaction, CancellationToken ct = default);
    Task<TargetResult> VoidAsync(VoidRequest request, CancellationToken ct = default);
    Task<TargetResult> ReverseAsync(VoidRequest request, CancellationToken ct = default);
}

/// <summary>Registered with TryAdd by callers so they work in a host without SMS.Modules.Accounting.</summary>
public sealed class NullFinancialPostingGateway : IFinancialPostingGateway
{
    private static Task<PostingResult> None(Guid id, FinDocType type) =>
        Task.FromResult(new PostingResult { Status = PostingStatus.NoTargetConfigured, SourceDocumentId = id, DocumentType = type });

    public Task<PostingResult> PostAsync(FinancialTransaction t, CancellationToken ct = default)        => None(t.SourceDocumentId, t.DocumentType);
    public Task<PostingResult> VoidAsync(VoidRequest r, CancellationToken ct = default)                => None(r.SourceDocumentId, r.DocumentType);
    public Task<PostingResult> IssueCreditAsync(FinancialTransaction t, CancellationToken ct = default) => None(t.SourceDocumentId, t.DocumentType);
    public Task<PostingResult> ReverseAsync(VoidRequest r, CancellationToken ct = default)             => None(r.SourceDocumentId, r.DocumentType);
}
