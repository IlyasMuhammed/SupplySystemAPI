using SMS.Shared.Common;

namespace SMS.Modules.Finance.Domain;

// A29-P7-03 §10 — the receivables ledger: what each customer owes us, entry by entry. A mirror of
// SupplierLedgerEntry (the payables ledger) in every way that matters to correctness — append-only,
// a running balance derived from the previous entry at post time and never stored anywhere else, and
// a per-partner SequenceNo whose unique index doubles as the concurrency guard for the writer — with
// the §10 vocabulary (entry_type, running_balance) in place of the supplier one.
//
// Nothing here posts entries yet: the invoice, payment and credit-note flows that write them come
// with their own tasks, in the same transaction as the business action (§9.5).

/// <summary>§10's closed vocabulary of ledger entry kinds, persisted as-is.</summary>
internal static class CustomerLedgerEntryTypes
{
    public const string Invoice     = "INVOICE";
    public const string Payment     = "PAYMENT";
    public const string CreditNote  = "CREDIT_NOTE";
    public const string DebitNote   = "DEBIT_NOTE";
    public const string Advance     = "ADVANCE";
    public const string Refund      = "REFUND";
    public const string OpeningBal  = "OPENING_BAL";

    public static readonly IReadOnlyList<string> All =
        [Invoice, Payment, CreditNote, DebitNote, Advance, Refund, OpeningBal];
}

/// <summary>
/// One line of a customer's account. <see cref="DebitAmount"/> increases what they owe,
/// <see cref="CreditAmount"/> decreases it, and <see cref="RunningBalance"/> is the balance after
/// this entry (<c>previous + debit − credit</c>). A negative balance is real: the customer has paid
/// more than they were billed.
/// <para>
/// <b>Append-only by construction:</b> no modified or deleted columns exist, so a correction is a
/// new entry that offsets the old one — the audit trail is the ledger itself.
/// </para>
/// </summary>
internal class CustomerLedgerEntry : ITenantScopedEntity
{
    public int  Id             { get; set; }
    public Guid UUID           { get; set; }
    public Guid OrganizationId { get; set; }

    /// <summary>The customer. Bare UUID onto suppliers.BusinessPartners — no FK across modules.</summary>
    public Guid PartnerId { get; set; }

    /// <summary>
    /// Position in this customer's ledger, 1, 2, 3…. Unique per (organization, partner): two writers
    /// racing for the next entry both compute the same number, one loses on the unique index and
    /// retries against the fresh last row — which is what keeps the running balance from forking.
    /// </summary>
    public int SequenceNo { get; set; }

    public DateTime EntryDate { get; set; }

    /// <summary>See <see cref="CustomerLedgerEntryTypes"/>.</summary>
    public string EntryType { get; set; } = string.Empty;

    /// <summary>The document behind the entry — e.g. SalesInvoice, CustomerPayment. Bare reference, no FK.</summary>
    public string ReferenceType   { get; set; } = string.Empty;
    public Guid   ReferenceId     { get; set; }
    public string ReferenceNumber { get; set; } = string.Empty;

    public decimal DebitAmount    { get; set; }
    public decimal CreditAmount   { get; set; }
    public decimal RunningBalance { get; set; }

    public string  CurrencyCode { get; set; } = "PKR";
    public string? Narration    { get; set; }

    public int      CreatedBy   { get; set; }
    public DateTime CreatedDate { get; set; }
}
