namespace SMS.Modules.Accounting.Models;

// A47 API-CONTRACT "Journal types" / "Journal entries" (docs/journal-engine/API-CONTRACT.md). Uuids only, dates yyyy-MM-dd,
// money as numbers (base currency unless named otherwise), codes as strings.

// ── journal types ─────────────────────────────────────────────────────────────────────────────────────────────────

public sealed class JournalTypeModel
{
    public Guid    Uuid                 { get; set; }
    public string  Code                 { get; set; } = string.Empty;
    public string  Name                 { get; set; } = string.Empty;
    /// <summary>Sale | Purchase | Cash | Bank | Miscellaneous | Opening | Closing.</summary>
    public string  Type                 { get; set; } = string.Empty;
    public string  SequencePrefix       { get; set; } = string.Empty;
    public AccountRefModel? DefaultDebitAccount  { get; set; }
    public AccountRefModel? DefaultCreditAccount { get; set; }
    public bool    RequiresPartner      { get; set; }
    public bool    AutoPost             { get; set; }
    public bool    IsSystem             { get; set; }
    public bool    IsActive             { get; set; }
    public int     SortOrder            { get; set; }
    /// <summary>Entries of the type exist (a custom type with entries cannot be deleted).</summary>
    public bool    IsUsed               { get; set; }
    public bool    CanDelete            { get; set; }
    public DateTime CreatedAt           { get; set; }
    public DateTime ModifiedAt          { get; set; }
}

// AccountRefModel (Models/DefaultsModels.cs, A42) is shared: uuid, accountCode, accountName, accountType, rootType (A47).

public sealed class CreateJournalTypeRequest
{
    public string?  Code                     { get; set; }
    public string?  Name                     { get; set; }
    public string?  Type                     { get; set; }
    public string?  SequencePrefix           { get; set; }
    public Guid?    DefaultDebitAccountUuid  { get; set; }
    public Guid?    DefaultCreditAccountUuid { get; set; }
    public bool     RequiresPartner          { get; set; }
    public bool     AutoPost                 { get; set; }
    public int?     SortOrder                { get; set; }
}

/// <summary>PUT — a full replacement of the editable fields. Code and type are fixed on a system type; prefix is fixed once used.</summary>
public sealed class UpdateJournalTypeRequest
{
    public string?  Code                     { get; set; }
    public string?  Name                     { get; set; }
    public string?  Type                     { get; set; }
    public string?  SequencePrefix           { get; set; }
    public Guid?    DefaultDebitAccountUuid  { get; set; }
    public Guid?    DefaultCreditAccountUuid { get; set; }
    public bool     RequiresPartner          { get; set; }
    public bool     AutoPost                 { get; set; }
    public int?     SortOrder                { get; set; }
}

// ── journal entries ───────────────────────────────────────────────────────────────────────────────────────────────

public sealed class CreateJournalEntryRequest
{
    public Guid      JournalTypeUuid  { get; set; }
    public DateOnly  EntryDate        { get; set; }
    public DateOnly? DocumentDate     { get; set; }
    public string?   Reference        { get; set; }
    public string?   Description      { get; set; }
    /// <summary>Null = the base currency of the date's fiscal year.</summary>
    public string?   CurrencyCode     { get; set; }
    /// <summary>Required (&gt; 0) for a foreign currency: 1 unit = rate × base.</summary>
    public decimal?  ExchangeRate     { get; set; }
    /// <summary>An adjustment entry (allowed in an AdjustmentOnly period). Forced by the Opening / Closing types.</summary>
    public bool      IsOpeningEntry   { get; set; }
    public bool      IsClosingEntry   { get; set; }
    /// <summary>Idempotency: the same id twice returns the first entry (key manual:{id}). Null = a new entry every time.</summary>
    public Guid?     ClientRequestId  { get; set; }
    /// <summary>Create and post at once (needs GL_JOURNAL_POST too). An auto-post journal type always posts.</summary>
    public bool      PostImmediately  { get; set; }
    public List<JournalEntryLineRequest> Lines { get; set; } = [];
}

/// <summary>PUT — the draft's new header and lines (the journal type, number and key never change).</summary>
public sealed class UpdateJournalEntryRequest
{
    public DateOnly  EntryDate        { get; set; }
    public DateOnly? DocumentDate     { get; set; }
    public string?   Reference        { get; set; }
    public string?   Description      { get; set; }
    public string?   CurrencyCode     { get; set; }
    public decimal?  ExchangeRate     { get; set; }
    public bool      IsOpeningEntry   { get; set; }
    public bool      IsClosingEntry   { get; set; }
    public List<JournalEntryLineRequest> Lines { get; set; } = [];
}

public sealed class JournalEntryLineRequest
{
    public Guid      AccountUuid  { get; set; }
    /// <summary>In the line's currency (the entry's unless <see cref="CurrencyCode"/>); the base amount is × rate, 2 dp.</summary>
    public decimal   Debit        { get; set; }
    public decimal   Credit       { get; set; }
    public string?   Description  { get; set; }
    public Guid?     PartnerUuid  { get; set; }
    /// <summary>Customer | Vendor (with <see cref="PartnerUuid"/>).</summary>
    public string?   PartnerType  { get; set; }
    public string?   CurrencyCode { get; set; }
    public decimal?  ExchangeRate { get; set; }
    public string?   CostCenter   { get; set; }
    public string?   Project      { get; set; }
    public string?   TaxCode      { get; set; }
    public decimal?  TaxAmount    { get; set; }
}

/// <summary>A47 C7 — owned by RR (POST journal-entries/{uuid}/reverse).</summary>
public sealed class ReverseJournalEntryRequest
{
    /// <summary>Required, ≤ 200 characters.</summary>
    public string?   Reason       { get; set; }
    /// <summary>Null = the original's entry date.</summary>
    public DateOnly? ReversalDate { get; set; }
}

public sealed class JournalEntryQuery
{
    public Guid?     JournalTypeUuid  { get; set; }
    public Guid?     FiscalYearUuid   { get; set; }
    public Guid?     FiscalPeriodUuid { get; set; }
    /// <summary>Draft | Posted | Reversed.</summary>
    public string?   Status           { get; set; }
    public DateOnly? FromDate         { get; set; }
    public DateOnly? ToDate           { get; set; }
    /// <summary>Entries with a line on this account.</summary>
    public Guid?     AccountUuid      { get; set; }
    /// <summary>Entries with a line on this partner.</summary>
    public Guid?     PartnerUuid      { get; set; }
    /// <summary>finance | pos | suppliers | inventory | manual (manual = entered by hand) | opening (A42 opening balances).</summary>
    public string?   SourceModule     { get; set; }
    /// <summary>Entry number, reference or description contains it.</summary>
    public string?   Search           { get; set; }
    public int       Page             { get; set; } = 1;
    public int       PageSize         { get; set; } = 25;
    /// <summary>entryDate (default) | entryNumber | totalDebit | createdAt | postedAt.</summary>
    public string?   SortBy           { get; set; }
    /// <summary>desc (default) | asc.</summary>
    public string?   SortDirection    { get; set; }
}

public sealed class JournalTypeRefModel
{
    public Guid   Uuid { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
}

public sealed class JournalEntryRefModel
{
    public Guid   Uuid        { get; set; }
    public string EntryNumber { get; set; } = string.Empty;
}

public sealed class FiscalPeriodRefModel
{
    public Guid   Uuid         { get; set; }
    public string PeriodName   { get; set; } = string.Empty;
    public int    PeriodNumber { get; set; }
    public string Status       { get; set; } = string.Empty;
}

public sealed class FiscalYearRefModel
{
    public Guid   Uuid           { get; set; }
    public string FiscalYearName { get; set; } = string.Empty;
}

public sealed class JournalEntryListItemModel
{
    public Guid      Uuid           { get; set; }
    public string    EntryNumber    { get; set; } = string.Empty;
    public JournalTypeRefModel JournalType { get; set; } = new();
    public DateOnly  EntryDate      { get; set; }
    public string?   Reference      { get; set; }
    public string?   Description    { get; set; }
    public string    EntryType      { get; set; } = string.Empty;
    public string?   SourceModule   { get; set; }
    public string    Status         { get; set; } = string.Empty;
    public decimal   TotalDebit     { get; set; }
    public decimal   TotalCredit    { get; set; }
    public string?   CurrencyCode   { get; set; }
    public int       LineCount      { get; set; }
    public bool      IsManual       { get; set; }
    public bool      IsOpeningEntry { get; set; }
    public bool      IsClosingEntry { get; set; }
    public bool      IsReversal     { get; set; }
    public DateTime  CreatedAt      { get; set; }
    public DateTime? PostedAt       { get; set; }
}

public sealed class JournalEntryDetailModel
{
    public Guid      Uuid               { get; set; }
    public string    EntryNumber        { get; set; } = string.Empty;
    public JournalTypeRefModel JournalType { get; set; } = new();
    public DateOnly  EntryDate          { get; set; }
    public DateOnly? DocumentDate       { get; set; }
    public FiscalYearRefModel?   FiscalYear   { get; set; }
    public FiscalPeriodRefModel? FiscalPeriod { get; set; }
    public string?   Reference          { get; set; }
    public string?   Description        { get; set; }
    /// <summary>The source document kind: FinDocType upper snake (SALES_INVOICE …), MANUAL_JOURNAL, OPENING_BALANCE, YEAR_END_CLOSE.</summary>
    public string    EntryType          { get; set; } = string.Empty;
    public string?   SourceModule       { get; set; }
    public Guid?     SourceDocumentUuid { get; set; }
    public string?   IdempotencyKey     { get; set; }
    public string?   CurrencyCode       { get; set; }
    public decimal?  ExchangeRate       { get; set; }
    public string    Status             { get; set; } = string.Empty;
    public bool      IsManual           { get; set; }
    public bool      IsOpeningEntry     { get; set; }
    public bool      IsClosingEntry     { get; set; }
    public decimal   TotalDebit         { get; set; }
    public decimal   TotalCredit        { get; set; }
    public int       LineCount          { get; set; }
    /// <summary>Set on a reversal: the entry it reverses.</summary>
    public JournalEntryRefModel? ReversesEntry   { get; set; }
    /// <summary>Set on a Reversed entry: its reversal.</summary>
    public JournalEntryRefModel? ReversedByEntry { get; set; }
    public string?   ReversalReason     { get; set; }
    public DateTime  CreatedAt          { get; set; }
    public string?   CreatedByName      { get; set; }
    public DateTime? PostedAt           { get; set; }
    public string?   PostedByName       { get; set; }
    public DateTime  ModifiedAt         { get; set; }
    public string?   ModifiedByName     { get; set; }
    /// <summary>What the caller may do now (permissions + state).</summary>
    public bool      CanEdit            { get; set; }
    public bool      CanDelete          { get; set; }
    public bool      CanPost            { get; set; }
    public bool      CanReverse         { get; set; }
    public List<JournalEntryLineModel> Lines { get; set; } = [];
}

public sealed class JournalEntryLineModel
{
    public Guid      LineUuid         { get; set; }
    public int       LineNumber       { get; set; }
    public AccountRefModel Account    { get; set; } = new();
    public string?   Description      { get; set; }
    /// <summary>Base currency.</summary>
    public decimal   Debit            { get; set; }
    public decimal   Credit           { get; set; }
    public string?   CurrencyCode     { get; set; }
    /// <summary>Signed, in <see cref="CurrencyCode"/> (debit +).</summary>
    public decimal?  AmountCurrency   { get; set; }
    public decimal?  ExchangeRate     { get; set; }
    public Guid?     PartnerUuid      { get; set; }
    public string?   PartnerType      { get; set; }
    public string?   PartnerName      { get; set; }
    public string?   CostCenter       { get; set; }
    public string?   Project          { get; set; }
    public string?   TaxCode          { get; set; }
    public decimal?  TaxAmount        { get; set; }
    public bool      Reconciled       { get; set; }
    public decimal   AmountResidual   { get; set; }
    public Guid?     ReconcileGroupUuid   { get; set; }
    public string?   ReconcileGroupNumber { get; set; }
    public JournalEntryRefModel? ClearingEntry { get; set; }
    public DateOnly? ClearingDate     { get; set; }
}
