namespace SMS.Modules.Finance.Models;

// A29-P7-06 §10 — the receivables ledger as callers see it. The entity stays internal to Finance;
// these are what an endpoint or a report can serialize.

/// <summary>The document behind a ledger entry — what an auditor follows from the entry back to its source.</summary>
/// <param name="Type">e.g. <c>SalesInvoice</c>, <c>CustomerPayment</c>. At most 30 characters.</param>
/// <param name="Id">The document's UUID. Empty for an entry with no document, such as an opening balance.</param>
/// <param name="Number">The document's number as people read it, e.g. <c>SINV-20260920-0001</c>. At most 30 characters.</param>
public sealed record CustomerLedgerReference(string Type, Guid Id, string Number);

public class CustomerLedgerEntryModel
{
    public Guid     Uuid            { get; set; }
    public Guid     PartnerId       { get; set; }
    /// <summary>Position in this customer's ledger, 1, 2, 3…; the order the entries were posted in.</summary>
    public int      SequenceNo      { get; set; }
    /// <summary>The business date — for a back-dated receipt, earlier than entries posted before it.</summary>
    public DateTime EntryDate       { get; set; }
    public string   EntryType       { get; set; } = string.Empty;
    public string   ReferenceType   { get; set; } = string.Empty;
    public Guid     ReferenceId     { get; set; }
    public string   ReferenceNumber { get; set; } = string.Empty;
    public decimal  DebitAmount     { get; set; }
    public decimal  CreditAmount    { get; set; }
    /// <summary>What the customer owed after this entry. Negative means they are in credit.</summary>
    public decimal  RunningBalance  { get; set; }
    public string   CurrencyCode    { get; set; } = string.Empty;
    public string?  Narration       { get; set; }
    public int      CreatedBy       { get; set; }
    public DateTime CreatedDate     { get; set; }
}

/// <summary>
/// Which of a customer's entries to page through. The range is in whole days and inclusive at both
/// ends: <c>DateTo</c> of the 20th includes an entry at 15:00 on the 20th, and any time of day on
/// either bound is ignored. Leave a bound out for an open-ended range.
/// </summary>
public class CustomerLedgerFilter
{
    public DateTime? DateFrom { get; set; }
    public DateTime? DateTo   { get; set; }
    public int        Page     { get; set; } = 1;
    /// <summary>Clamped to 1–100.</summary>
    public int        PageSize { get; set; } = 20;
}
