using SMS.Shared.Common;

namespace SMS.Modules.Accounting.Domain;

// A42 (docs/general-ledger/ADDENDUM-42-ANALYSIS.md D-1/D-2): int Id + Guid Uuid, Guid OrganizationId (tenant filter),
// PascalCase columns in schema finance with the spec's table names, statuses/types as strings (GlCodes.cs), users as int
// ids, money decimal(18,4), dates as DateOnly (SQL date). References inside the module are real FKs (Restrict, except a
// template's items, a fiscal year's periods and a journal entry's lines); country/currency are codes (D-7).

/// <summary>§4.2 — a chart of accounts template. System templates (IsSystem, OrganizationId null) are visible to every
/// organization and read-only (BR-TPL-001); a custom template (D-5) only to its own organization.</summary>
internal class CoaTemplate : IGloballyExemptTenantScopedEntity, IHasModifiedAt
{
    public int      Id             { get; set; }
    public Guid     Uuid           { get; set; } = Guid.NewGuid();
    /// <summary>^[A-Z0-9_]{2,50}$, e.g. US_GENERAL.</summary>
    public string   TemplateCode   { get; set; } = string.Empty;
    public string   TemplateName   { get; set; } = string.Empty;
    /// <summary>ISO 3166-1 alpha-3; null = country-agnostic.</summary>
    public string?  CountryCode    { get; set; }
    public string?  BusinessType   { get; set; }
    /// <summary>ISO 4217; null = the organization's base currency.</summary>
    public string?  CurrencyCode   { get; set; }
    public string?  Description    { get; set; }
    /// <summary>Cached count of the template's items (refreshed by the seeder).</summary>
    public int      AccountCount   { get; set; }
    public bool     IsActive       { get; set; } = true;
    /// <summary>D-6 — the seeder replaces the items only when the JSON version differs.</summary>
    public string   Version        { get; set; } = "1.0";
    public int      SortOrder      { get; set; }
    /// <summary>Null for a system template.</summary>
    public Guid?    OrganizationId { get; set; }
    /// <summary>Column <c>IsSystem</c>: a system template (the tenant filter shows it to everyone).</summary>
    public bool     IsGlobal       { get; set; }
    public DateTime CreatedAt      { get; set; } = DateTime.UtcNow;
    public int?     CreatedBy      { get; set; }
    public DateTime ModifiedAt     { get; set; } = DateTime.UtcNow;
    public int?     ModifiedBy     { get; set; }

    public bool IsSystem => IsGlobal;

    public ICollection<CoaTemplateItem> Items { get; set; } = new List<CoaTemplateItem>();
}

/// <summary>§5.2 — one account of a template's 4-level tree. Visible through its template.</summary>
internal class CoaTemplateItem
{
    public int     Id                { get; set; }
    public int     TemplateId        { get; set; }
    public string  AccountCode       { get; set; } = string.Empty;
    public string  AccountName       { get; set; } = string.Empty;
    public int?    ParentItemId      { get; set; }
    /// <summary>1–4.</summary>
    public byte    Level             { get; set; }
    public string  RootType          { get; set; } = string.Empty;
    /// <summary>True for levels 1–3, false for level 4.</summary>
    public bool    IsGroup           { get; set; }
    /// <summary>Level 4 only (Appendix A).</summary>
    public string? AccountType       { get; set; }
    public string? Description       { get; set; }
    public int     SortOrder         { get; set; }
    /// <summary>Applied to AccountingDefaults when the template is applied.</summary>
    public bool    IsDefaultAccount  { get; set; }
    public string? DefaultAccountKey { get; set; }

    public CoaTemplate      Template { get; set; } = null!;
    public CoaTemplateItem? Parent   { get; set; }
}

/// <summary>§6.2 — the organization's chart of accounts (4 levels; only level 4 posts).</summary>
internal class ChartOfAccount : ITenantScopedEntity, IHasModifiedAt
{
    public int      Id                   { get; set; }
    public Guid     Uuid                 { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId       { get; set; }
    /// <summary>[A-Za-z0-9]{1,20}, unique per organization among active accounts (D-8).</summary>
    public string   AccountCode          { get; set; } = string.Empty;
    public string   AccountName          { get; set; } = string.Empty;
    public int?     ParentAccountId      { get; set; }
    /// <summary>"/1/11/111/1111" — computed by the service, never set by a user (BR-COA-011).</summary>
    public string   MaterializedPath     { get; set; } = string.Empty;
    public byte     Level                { get; set; }
    /// <summary>Inherited from the level-1 ancestor, immutable (BR-COA-003).</summary>
    public string   RootType             { get; set; } = string.Empty;
    public bool     IsGroup              { get; set; }
    public string?  AccountType          { get; set; }
    public string?  Description          { get; set; }
    /// <summary>The account's code in an external financial system (§12.5).</summary>
    public string?  ExternalCode         { get; set; }
    /// <summary>D-9 — the five roots and 3900.</summary>
    public bool     IsSystemAccount      { get; set; }
    public bool     IsActive             { get; set; } = true;
    /// <summary>Set when the first journal line posts to the account; blocks deletion.</summary>
    public bool     HasTransactions      { get; set; }
    public decimal  OpeningBalanceDebit  { get; set; }
    public decimal  OpeningBalanceCredit { get; set; }
    /// <summary>Opening + posted entries, in the account's normal direction.</summary>
    public decimal  CurrentBalance       { get; set; }
    public int      SortOrder            { get; set; }
    public int?     SourceTemplateId     { get; set; }
    public DateTime CreatedAt            { get; set; } = DateTime.UtcNow;
    public int?     CreatedBy            { get; set; }
    public DateTime ModifiedAt           { get; set; } = DateTime.UtcNow;
    public int?     ModifiedBy           { get; set; }

    public ChartOfAccount?  Parent         { get; set; }
    public ICollection<ChartOfAccount> Children { get; set; } = new List<ChartOfAccount>();
    public CoaTemplate?     SourceTemplate { get; set; }
}

/// <summary>§7.2 — a fiscal year (12 monthly periods + period 13 for year-end adjustments).</summary>
internal class FiscalYear : ITenantScopedEntity, IHasModifiedAt
{
    public int      Id                    { get; set; }
    public Guid     Uuid                  { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId        { get; set; }
    public string   FiscalYearName        { get; set; } = string.Empty;
    public DateOnly StartDate             { get; set; }
    public DateOnly EndDate               { get; set; }
    public bool     IsClosed              { get; set; }
    public DateTime? ClosedAt             { get; set; }
    public int?     ClosedBy              { get; set; }
    /// <summary>D-13 — defaults to the organization's base currency.</summary>
    public string   ReportingCurrencyCode { get; set; } = string.Empty;
    public DateTime CreatedAt             { get; set; } = DateTime.UtcNow;
    public int?     CreatedBy             { get; set; }
    public DateTime ModifiedAt            { get; set; } = DateTime.UtcNow;
    public int?     ModifiedBy            { get; set; }

    public ICollection<FiscalPeriod> Periods { get; set; } = new List<FiscalPeriod>();
}

/// <summary>§7.3 — a period of a fiscal year (1–13). Only Open periods accept postings (D-18).</summary>
internal class FiscalPeriod : ITenantScopedEntity, IHasModifiedAt
{
    public int      Id             { get; set; }
    public Guid     Uuid           { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId { get; set; }
    public int      FiscalYearId   { get; set; }
    public string   PeriodName     { get; set; } = string.Empty;
    public byte     PeriodNumber   { get; set; }
    public DateOnly StartDate      { get; set; }
    public DateOnly EndDate        { get; set; }
    public string   Status         { get; set; } = PeriodStatuses.Open;
    public DateTime CreatedAt      { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt     { get; set; } = DateTime.UtcNow;
    public int?     ModifiedBy     { get; set; }

    public FiscalYear FiscalYear { get; set; } = null!;
}

/// <summary>§8.2 — one of the 16 accounting defaults (rows created per organization on first access, D-15).</summary>
internal class AccountingDefault : ITenantScopedEntity, IHasModifiedAt
{
    public int      Id                  { get; set; }
    public Guid     Uuid                { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId      { get; set; }
    public string   DefaultKey          { get; set; } = string.Empty;
    public string   DefaultLabel        { get; set; } = string.Empty;
    /// <summary>Null = not assigned. Must be a leaf, active account of the same organization (D-24).</summary>
    public int?     AccountId           { get; set; }
    public bool     IsRequired          { get; set; }
    public string   Category            { get; set; } = string.Empty;
    /// <summary>A mismatch is a warning only (D-24).</summary>
    public string?  ExpectedAccountType { get; set; }
    public string?  HelpText            { get; set; }
    public int      SortOrder           { get; set; }
    public DateTime CreatedAt           { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt          { get; set; } = DateTime.UtcNow;
    public int?     ModifiedBy          { get; set; }

    public ChartOfAccount? Account { get; set; }
}

/// <summary>§9.2 — one row per organization: where the GL setup stands (D-16).</summary>
internal class CoaSetupStatus : ITenantScopedEntity, IHasModifiedAt
{
    public int       Id                      { get; set; }
    public Guid      OrganizationId          { get; set; }
    public string    Status                  { get; set; } = SetupStatuses.NotStarted;
    public DateTime? TemplateAppliedAt       { get; set; }
    /// <summary>Null when the chart was uploaded or built by hand.</summary>
    public int?      TemplateId              { get; set; }
    public int       AccountsCreatedCount    { get; set; }
    public bool      FiscalYearConfigured    { get; set; }
    public bool      DefaultsConfigured      { get; set; }
    /// <summary>Set by posting (or skipping) the opening balances.</summary>
    public bool      OpeningBalancesEntered  { get; set; }
    public bool      OpeningBalancesBalanced { get; set; }
    /// <summary>When the opening balances were posted or skipped; from then on the conversion date is locked (D-11).</summary>
    public DateTime? OpeningBalancesPostedAt { get; set; }
    public DateTime? SetupCompletedAt        { get; set; }
    public int?      SetupCompletedBy        { get; set; }
    public string?   SetupCompletedByName    { get; set; }
    /// <summary>D-11 — copy of the A40 opening balance date taken when the opening balances are posted.</summary>
    public DateOnly? ConversionDate          { get; set; }
    public DateTime  CreatedAt               { get; set; } = DateTime.UtcNow;
    public DateTime  ModifiedAt              { get; set; } = DateTime.UtcNow;

    public CoaTemplate? Template { get; set; }
}

/// <summary>§10.2 — an opening balance of a leaf account (one per account). Draft until posted, then immutable.</summary>
internal class OpeningBalanceEntry : ITenantScopedEntity, IHasModifiedAt
{
    public int       Id             { get; set; }
    public Guid      Uuid           { get; set; } = Guid.NewGuid();
    public Guid      OrganizationId { get; set; }
    public int       AccountId      { get; set; }
    public decimal   DebitAmount    { get; set; }
    public decimal   CreditAmount   { get; set; }
    public string?   Notes          { get; set; }
    /// <summary>D-28 — null while Draft (the A40 opening balance date may not be set yet); stamped when posted.</summary>
    public DateOnly? ConversionDate { get; set; }
    public string    EntryStatus    { get; set; } = OpeningEntryStatuses.Draft;
    public DateTime? PostedAt       { get; set; }
    public int?      PostedBy       { get; set; }
    public int?      JournalEntryId { get; set; }
    public DateTime  CreatedAt      { get; set; } = DateTime.UtcNow;
    public int?      CreatedBy      { get; set; }
    public DateTime  ModifiedAt     { get; set; } = DateTime.UtcNow;
    public int?      ModifiedBy     { get; set; }

    public ChartOfAccount Account      { get; set; } = null!;
    public JournalEntry?  JournalEntry { get; set; }
}

/// <summary>D-10 — a journal entry. A42 writes the OPENING_BALANCE entry, A44 one per posted document, A47 the manual,
/// reversal and year-end entries (docs/journal-engine/ADDENDUM-47-ANALYSIS.md D-2).</summary>
internal class JournalEntry : ITenantScopedEntity, IHasModifiedAt
{
    public int       Id             { get; set; }
    public Guid      Uuid           { get; set; } = Guid.NewGuid();
    public Guid      OrganizationId { get; set; }
    /// <summary>JE-YYYY-NNNNN, unique per organization.</summary>
    public string    EntryNumber    { get; set; } = string.Empty;
    public string    EntryType      { get; set; } = JournalEntryTypes.OpeningBalance;
    public DateOnly  EntryDate      { get; set; }
    public string?   Reference      { get; set; }
    public string?   Description    { get; set; }
    public string    Status         { get; set; } = JournalStatuses.Posted;
    public decimal   TotalDebit     { get; set; }
    public decimal   TotalCredit    { get; set; }
    public string?   CurrencyCode   { get; set; }
    public DateTime? PostedAt       { get; set; }
    public int?      PostedBy       { get; set; }
    public DateTime  CreatedAt      { get; set; } = DateTime.UtcNow;
    public int?      CreatedBy      { get; set; }
    public DateTime  ModifiedAt     { get; set; } = DateTime.UtcNow;
    /// <summary>A44 §3 — the module that posted it (finance, pos, suppliers, inventory); null for A42 opening balances.</summary>
    public string?   SourceModule       { get; set; }
    /// <summary>A44 — the source document's uuid.</summary>
    public Guid?     SourceDocumentUuid { get; set; }
    /// <summary>A44 D-13 — the posting key ("…:void" on a void reversal). Several versions of one document share it.</summary>
    public string?   IdempotencyKey     { get; set; }
    /// <summary>A44 (D-43) — set on a reversing journal: the journal it reverses (the spec's reversal_of_entry_id).</summary>
    public int?      ReversesEntryId    { get; set; }

    // ── A47 C3 (D-2) ──────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>The journal type (SALE, PURCH … or a custom one); backfilled for the A42/A44 journals (D-6).</summary>
    public int       JournalTypeId      { get; set; }
    /// <summary>The source document's own date when it differs from <see cref="EntryDate"/>.</summary>
    public DateOnly? DocumentDate       { get; set; }
    /// <summary>The fiscal year / period of <see cref="EntryDate"/>; null only for an A44 draft dated outside every year.</summary>
    public int?      FiscalYearId       { get; set; }
    public int?      FiscalPeriodId     { get; set; }
    /// <summary>1 entry-currency unit = rate × base currency; null = the base currency (rate 1).</summary>
    public decimal?  ExchangeRate       { get; set; }
    /// <summary>D-4 — set on a reversed journal: its reversal (Status Reversed).</summary>
    public int?      ReversedByEntryId  { get; set; }
    public string?   ReversalReason     { get; set; }
    public bool      IsOpeningEntry     { get; set; }
    public bool      IsClosingEntry     { get; set; }
    /// <summary>Denormalized number of lines (display).</summary>
    public int       LineCount          { get; set; }
    /// <summary>D-CORE-6 — who did it, by name (the user ids are kept as well).</summary>
    public string?   CreatedByName      { get; set; }
    public string?   PostedByName       { get; set; }
    public int?      ModifiedBy         { get; set; }
    public string?   ModifiedByName     { get; set; }

    public JournalType   JournalType     { get; set; } = null!;
    public FiscalYear?   FiscalYear      { get; set; }
    public FiscalPeriod? FiscalPeriod    { get; set; }
    public JournalEntry? ReversesEntry   { get; set; }
    public JournalEntry? ReversedByEntry { get; set; }

    public ICollection<JournalEntryLine> Lines { get; set; } = new List<JournalEntryLine>();
}

/// <summary>
/// D-10 — a debit or credit line of a journal entry, on a leaf account. A47 C4 (D-3): <see cref="DebitAmount"/> /
/// <see cref="CreditAmount"/> are the <b>base</b> (reporting-currency) amounts (A-9, the spec's local_debit/local_credit);
/// the transaction-currency amount is <see cref="AmountCurrency"/> (signed, debit positive).
/// </summary>
internal class JournalEntryLine : ITenantScopedEntity
{
    public int     Id             { get; set; }
    public Guid    OrganizationId { get; set; }
    public int     JournalEntryId { get; set; }
    public int     LineNumber     { get; set; }
    public int     AccountId      { get; set; }
    public decimal DebitAmount    { get; set; }
    public decimal CreditAmount   { get; set; }
    /// <summary>The line's narration (the spec's description).</summary>
    public string? Memo           { get; set; }

    // ── A47 C4 (D-3) ──────────────────────────────────────────────────────────────────────────────────────────────
    /// <summary>D-CORE-8 — the line's public id (reconciliation and ledgers address lines by uuid, A-3).</summary>
    public Guid      Uuid            { get; set; } = Guid.NewGuid();
    /// <summary>A-12 — a customer/vendor (BusinessPartner uuid); no FK outside the module.</summary>
    public Guid?     PartnerUuid     { get; set; }
    /// <summary>Customer | Vendor | null.</summary>
    public string?   PartnerType     { get; set; }
    /// <summary>D-CORE-6 — the partner's name when the line was written (display only).</summary>
    public string?   PartnerName     { get; set; }
    /// <summary>The transaction currency of <see cref="AmountCurrency"/>.</summary>
    public string?   CurrencyCode    { get; set; }
    /// <summary>Signed amount in <see cref="CurrencyCode"/> (debit +, credit −).</summary>
    public decimal?  AmountCurrency  { get; set; }
    /// <summary>The rate used for this line (null = the header's).</summary>
    public decimal?  ExchangeRate    { get; set; }
    public string?   CostCenter      { get; set; }
    public string?   Project         { get; set; }
    public string?   TaxCode         { get; set; }
    public decimal?  TaxAmount       { get; set; }
    /// <summary>C8 — fully reconciled (AmountResidual = 0).</summary>
    public bool      Reconciled      { get; set; }
    /// <summary>C8 — the line's (one) active reconciliation group (BR-RC-008), full or partial.</summary>
    public int?      ReconcileGroupId { get; set; }
    /// <summary>C8 — the entry that cleared the line, and when.</summary>
    public int?      ClearingEntryId { get; set; }
    public DateOnly? ClearingDate    { get; set; }
    /// <summary>C8 — the unreconciled base amount, signed (debit +); starts at Debit − Credit.</summary>
    public decimal   AmountResidual  { get; set; }
    public DateTime  CreatedAt       { get; set; } = DateTime.UtcNow;

    public JournalEntry    JournalEntry   { get; set; } = null!;
    public ChartOfAccount  Account        { get; set; } = null!;
    public ReconcileGroup? ReconcileGroup { get; set; }
    public JournalEntry?   ClearingEntry  { get; set; }
}

/// <summary>
/// A47 §4.2 — a journal type of the organization: classifies its entries and owns their number sequence
/// ({SequencePrefix}/{year}/{seq:D4}). The 7 system types (<see cref="SystemJournalTypes"/>) cannot be deleted, only
/// deactivated; custom types can be added (one Opening and one Closing type per organization).
/// </summary>
internal class JournalType : ITenantScopedEntity, IHasModifiedAt
{
    public int      Id                     { get; set; }
    public Guid     Uuid                   { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId         { get; set; }
    /// <summary>^[A-Z0-9]{1,10}$, unique per organization.</summary>
    public string   Code                   { get; set; } = string.Empty;
    public string   Name                   { get; set; } = string.Empty;
    /// <summary><see cref="JournalTypeKinds"/>.</summary>
    public string   Type                   { get; set; } = JournalTypeKinds.Miscellaneous;
    /// <summary>^[A-Z0-9]{1,10}$, unique per organization (two types sharing a prefix would share entry numbers).</summary>
    public string   SequencePrefix         { get; set; } = string.Empty;
    /// <summary>Suggested accounts for the entry form (leaf, active, same organization).</summary>
    public int?     DefaultDebitAccountId  { get; set; }
    public int?     DefaultCreditAccountId { get; set; }
    /// <summary>D-13 — every line of a <b>manual</b> entry of this type needs a partner.</summary>
    public bool     RequiresPartner        { get; set; }
    /// <summary>Entries of this type are posted when created (no Draft).</summary>
    public bool     AutoPost               { get; set; }
    public bool     IsSystem               { get; set; }
    public bool     IsActive               { get; set; } = true;
    public int      SortOrder              { get; set; }
    public DateTime CreatedAt              { get; set; } = DateTime.UtcNow;
    public int?     CreatedBy              { get; set; }
    public DateTime ModifiedAt             { get; set; } = DateTime.UtcNow;
    public int?     ModifiedBy             { get; set; }

    public ChartOfAccount? DefaultDebitAccount  { get; set; }
    public ChartOfAccount? DefaultCreditAccount { get; set; }
}

/// <summary>
/// A47 §8.2 / D-7 — the last number used per (organization, journal type, fiscal year). The row is taken with UPDLOCK,
/// HOLDLOCK in the posting transaction, so numbers are unique and gap-free except for deleted drafts. A47 D-16 — rows with
/// no journal type number the reconciliation groups (REC/{year}/{seq}); rows with no fiscal year number journals dated
/// outside every fiscal year (calendar year, D-7).
/// </summary>
internal class JournalEntrySequence : ITenantScopedEntity, IHasModifiedAt
{
    public int      Id              { get; set; }
    public Guid     OrganizationId  { get; set; }
    public int?     JournalTypeId   { get; set; }
    public int?     FiscalYearId    { get; set; }
    /// <summary>The year shown in the number (the fiscal year's start year, or the calendar year).</summary>
    public int      SequenceYear    { get; set; }
    public int      CurrentSequence { get; set; }
    public DateTime CreatedAt       { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAt      { get; set; } = DateTime.UtcNow;

    public JournalType? JournalType { get; set; }
    public FiscalYear?  FiscalYear  { get; set; }
}

/// <summary>
/// A47 §11.2 / D-16 — lines of one account reconciled together: Full (they net to zero, every line cleared) or Partial
/// (a residual remains). Deleted by an unreconcile (BR-RC-007).
/// </summary>
internal class ReconcileGroup : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     Uuid           { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId { get; set; }
    /// <summary>REC/{year}/{seq:D4}, unique per organization.</summary>
    public string   GroupNumber    { get; set; } = string.Empty;
    /// <summary><see cref="ReconcileTypes"/>.</summary>
    public string   ReconcileType  { get; set; } = ReconcileTypes.Full;
    /// <summary>The account every line of the group is on (BR-RC-002).</summary>
    public int      AccountId      { get; set; }
    /// <summary>The amount matched (base currency, positive).</summary>
    public decimal  Amount         { get; set; }
    public string?  CurrencyCode   { get; set; }
    public DateTime ReconciledAt   { get; set; } = DateTime.UtcNow;
    public int?     CreatedBy      { get; set; }
    public string?  CreatedByName  { get; set; }

    public ChartOfAccount Account { get; set; } = null!;
    public ICollection<JournalEntryLine> Lines { get; set; } = new List<JournalEntryLine>();
}

/// <summary>§12.2 — an account mapped to its counterpart in an external financial system (D-21). No connectors in A42.</summary>
internal class ExternalSystemMapping : ITenantScopedEntity, IHasModifiedAt
{
    public int       Id                   { get; set; }
    public Guid      Uuid                 { get; set; } = Guid.NewGuid();
    public Guid      OrganizationId       { get; set; }
    public string    SystemType           { get; set; } = string.Empty;
    public string    SystemName           { get; set; } = string.Empty;
    public int       AccountId            { get; set; }
    public string    ExternalAccountCode  { get; set; } = string.Empty;
    public string?   ExternalAccountName  { get; set; }
    public string    SyncDirection        { get; set; } = SyncDirections.None;
    public bool      IsActive             { get; set; } = true;
    public DateTime? LastSyncedAt         { get; set; }
    public string?   SyncStatus           { get; set; }
    public string?   SyncErrorMessage     { get; set; }
    /// <summary>Connection metadata only — never credentials (BR-INT-005).</summary>
    public string?   ConnectionConfigJson { get; set; }
    public DateTime  CreatedAt            { get; set; } = DateTime.UtcNow;
    public int?      CreatedBy            { get; set; }
    public DateTime  ModifiedAt           { get; set; } = DateTime.UtcNow;
    public int?      ModifiedBy           { get; set; }

    public ChartOfAccount Account { get; set; } = null!;
}

/// <summary>D-20 — every accounting event, written once (immutable) and shown on the Setup Status page.</summary>
internal class GlSetupHistory : ITenantScopedEntity
{
    public long     Id             { get; set; }
    public Guid     OrganizationId { get; set; }
    /// <summary>The event's name, e.g. CoaTemplateAppliedEvent.</summary>
    public string   EventType      { get; set; } = string.Empty;
    public string   Summary        { get; set; } = string.Empty;
    /// <summary>The event serialized (camelCase JSON).</summary>
    public string?  PayloadJson    { get; set; }
    public int?     UserId         { get; set; }
    public string?  UserName       { get; set; }
    public DateTime At             { get; set; } = DateTime.UtcNow;
}
