using System.Text.Json.Serialization;
using SMS.Shared.Finance.Posting;

namespace SMS.Shared.Integration.QuickBooks;

// A49 G-1 — the stored form of a journal entry for an accounting system (docs/hrm/API-CONTRACT.md §10.6). Provider-neutral like the
// other payloads: account roles and cost center codes, resolved to QuickBooks ids only when it is sent, so a held entry waits
// for mappings instead of freezing old ones. Summarized by construction: no line carries a person (G-11).

public static class JournalEntryPostingTypes
{
    public const string Debit = "Debit", Credit = "Credit";
}

public sealed class JournalEntryPayloadLine
{
    /// <summary><see cref="JournalEntryPostingTypes"/>.</summary>
    public string  PostingType    { get; set; } = JournalEntryPostingTypes.Debit;
    public decimal Amount         { get; set; }
    /// <summary>An A42 default key; used when <see cref="AccountCode"/> is null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? AccountRole { get; set; }
    /// <summary>An explicit leaf account code; wins over the role.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? AccountCode { get; set; }
    public string  CostCenterCode { get; set; } = string.Empty;
    /// <summary>The whole line description at QuickBooks (G-11: never a person).</summary>
    public string  CostCenterName { get; set; } = string.Empty;
}

public sealed class JournalEntryPayload : IAccountingDocument
{
    /// <summary><see cref="JournalEntryKeys.Root(Guid, FinDocType)"/>, then ":void" for a reversal, then ":batch{N}" for a split entry.</summary>
    public string   ExternalId   { get; set; } = string.Empty;
    /// <summary>"hrm:{DocType}:{uuid}[:void][:batch{N}]" (BR-G1-003).</summary>
    public string   IdempotencyKey { get; set; } = string.Empty;
    public string   DocNumber    { get; set; } = string.Empty;
    public DateTime TxnDate      { get; set; }
    public string   CurrencyCode { get; set; } = string.Empty;
    /// <summary>The FinDocType name; it is also the sync-control type governing the entry.</summary>
    public string   SourceType   { get; set; } = string.Empty;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public decimal? ExchangeRate { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string?  PrivateNote  { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int?     BatchNumber  { get; set; }
    public bool     IsReversal   { get; set; }
    public List<JournalEntryPayloadLine> Lines { get; set; } = new();
}

/// <summary>Optional <see cref="FinancialDocumentMetadata.Properties"/> keys of an HRM posting: PrivateNote = "{number}, {period}, {headcount} employees".</summary>
public static class HrmPostingProperties
{
    public const string Period = "Period", Headcount = "Headcount";
}

/// <summary>
/// A49 E-13: the ItemExternalId of a non-stock service line. Not a record of any source: the gateway sends it as the connection's
/// default service item (provider default ServiceItemRef), and refuses the invoice with a clear message until one is chosen.
/// </summary>
public static class ServiceInvoiceLines
{
    public const string ItemExternalId = "SERVICE-ITEM";
}

/// <summary>
/// QA49C-1: the record key of a journal entry at the accounting system. A payment carries the uuid of the document it pays
/// (run, claim, settlement), so keyed on the uuid alone it overwrote that document's entry. The payment types get their own key
/// "{uuid}:{DocType}" and DocNumber "{number}-P"; every other type keeps the bare uuid, so the maps already stored stay valid.
/// The builder, the poster's reversal, the gateway status and the Accounting status refresher all derive it here.
/// </summary>
public static class JournalEntryKeys
{
    public const string PaymentDocNumberSuffix = "-P";

    /// <summary>A type that only ever pays a document posted under the same uuid.</summary>
    public static bool IsPaymentOfAnother(FinDocType type) => type is FinDocType.PayrollPayment or FinDocType.ExpensePayment;

    public static string Root(Guid sourceDocumentId, FinDocType type) =>
        IsPaymentOfAnother(type) ? $"{sourceDocumentId}:{type}" : sourceDocumentId.ToString();

    /// <summary>The FinDocType name as stored on a posting; an unknown name keys on the bare uuid.</summary>
    public static string Root(Guid sourceDocumentId, string? documentType) =>
        Enum.TryParse<FinDocType>(documentType, ignoreCase: true, out var type) ? Root(sourceDocumentId, type) : sourceDocumentId.ToString();
}

/// <summary>The FinDocTypes that go to an accounting system as a journal entry (BR-G1-001, G-2).</summary>
public static class JournalEntryRouting
{
    public static bool IsJournalEntry(FinDocType type, string? sourceModule) => type switch
    {
        FinDocType.PayrollRun or FinDocType.PayrollPayment or FinDocType.LoanDisbursement or FinDocType.ExpensePayment
            or FinDocType.StatutoryRemittance or FinDocType.FinalSettlement or FinDocType.PayrollProvision => true,
        FinDocType.Expense => string.Equals(sourceModule, PostingSourceModules.Hrm, StringComparison.OrdinalIgnoreCase),
        _ => false
    };
}
