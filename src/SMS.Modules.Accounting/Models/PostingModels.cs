namespace SMS.Modules.Accounting.Models;

// A44 API-CONTRACT "Details › CORE" — api/finance/posting/*.

public sealed class PostingConfigModel
{
    public bool     InternalAccountingEnabled { get; init; }
    public bool     AutoPostToGL              { get; init; }
    public string?  DefaultCurrencyCode       { get; init; }
    public int      FiscalYearStartMonth      { get; init; }
    public bool     GlFeatureEnabled          { get; init; }
    public string   SetupStatus               { get; init; } = string.Empty;
    /// <summary>Feature on and setup Complete.</summary>
    public bool     GlReady                   { get; init; }
    /// <summary>GlReady and InternalAccountingEnabled — the GL target takes postings.</summary>
    public bool     GlActive                  { get; init; }
    /// <summary>null | FEATURE_OFF | SETUP_INCOMPLETE.</summary>
    public string?  NotReadyReason            { get; init; }
    /// <summary>Null while the settings were never saved (defaults).</summary>
    public DateTime? ModifiedAt               { get; init; }
}

/// <summary>Omitted fields keep their value.</summary>
public sealed class UpdatePostingConfigRequest
{
    public bool? InternalAccountingEnabled { get; init; }
    public bool? AutoPostToGL              { get; init; }
    public int?  FiscalYearStartMonth      { get; init; }
}

public sealed class PostingLogQuery
{
    public string?   Status       { get; init; }
    public string?   TargetCode   { get; init; }
    public string?   DocType      { get; init; }
    public string?   SourceModule { get; init; }
    public string?   Kind         { get; init; }
    public DateOnly? From         { get; init; }
    public DateOnly? To           { get; init; }
    public string?   Search       { get; init; }
    public int       Page         { get; init; } = 1;
    public int       PageSize     { get; init; } = 25;
}

public sealed class PostingTargetModel
{
    public string    TargetCode    { get; init; } = string.Empty;
    public string    Status        { get; init; } = string.Empty;
    public int       Attempts      { get; init; }
    public DateTime? NextAttemptAt { get; init; }
    public DateTime? LastAttemptAt { get; init; }
    public string?   LastError     { get; init; }
    public string?   InternalId    { get; init; }
    public string?   ExternalId    { get; init; }
    /// <summary>D-49b — QuickBooks DocNumber once synced.</summary>
    public string?   ExternalDocNumber { get; init; }
    /// <summary>D-49b — Failed/DeadLetter copied from QuickBooks' own state: not retried by the Accounting job.</summary>
    public bool      MirroredFromTarget { get; init; }
    public DateTime? PostedAt      { get; init; }
    /// <summary>D-49a (by-source only, else null): the caller may retry/skip this target now — it is open (Pending/Failed/DeadLetter)
    /// and the caller holds the target's manage code (internal_gl: GL_SETUP_MANAGE; external: INTEGRATION_MANAGE).</summary>
    public bool?     CanAct        { get; init; }
}

public class PostingLogItemModel
{
    public Guid      Uuid                 { get; init; }
    public string    IdempotencyKey       { get; init; } = string.Empty;
    public string    Kind                 { get; init; } = string.Empty;
    public string    DocumentType         { get; init; } = string.Empty;
    public string    SourceModule         { get; init; } = string.Empty;
    public Guid      SourceDocumentUuid   { get; init; }
    public string    SourceDocumentNumber { get; init; } = string.Empty;
    public DateOnly  TransactionDate      { get; init; }
    public Guid?     PartnerUuid          { get; init; }
    public string?   PartnerName          { get; init; }
    public string?   CurrencyCode         { get; init; }
    public decimal   TotalAmount          { get; init; }
    public int       Version              { get; init; }
    public string    OverallStatus        { get; init; } = string.Empty;
    public DateTime  CreatedAt            { get; init; }
    public DateTime  ModifiedAt           { get; init; }
    public List<PostingTargetModel> Targets { get; init; } = [];
}

public sealed class PostingLineModel
{
    public int      LineNumber  { get; init; }
    public string?  AccountRole { get; init; }
    public string?  AccountCode { get; init; }
    public decimal  Debit       { get; init; }
    public decimal  Credit      { get; init; }
    public string?  Label       { get; init; }
    public string?  TaxCode     { get; init; }
    public decimal? TaxAmount   { get; init; }
}

public sealed class PostingJournalModel
{
    public Guid     Uuid                { get; init; }
    public string   EntryNumber         { get; init; } = string.Empty;
    public string   EntryType           { get; init; } = string.Empty;
    public DateOnly EntryDate           { get; init; }
    public string   Status              { get; init; } = string.Empty;
    public decimal  TotalDebit          { get; init; }
    public decimal  TotalCredit         { get; init; }
    public string?  CurrencyCode        { get; init; }
    public string?  ReversesEntryNumber { get; init; }
    /// <summary>D-36 — same as ReversesEntryNumber (FE's name).</summary>
    public string?  ReversesJournalNumber   { get; init; }
    /// <summary>D-36 — the journal that reversed this one, when any.</summary>
    public string?  ReversedByJournalNumber { get; init; }
    /// <summary>D-36 — the lines, base currency (detail only).</summary>
    public List<PostingJournalLineModel> Lines { get; init; } = [];
    public bool     IsReversed          { get; init; }
}

public sealed class PostingLogDetailModel : PostingLogItemModel
{
    public string?  Description         { get; init; }
    public decimal? ExchangeRate        { get; init; }
    public string?  PartnerType         { get; init; }
    public string?  DocumentPayloadType { get; init; }
    public List<PostingLineModel>    Lines    { get; init; } = [];
    public List<PostingJournalModel> Journals { get; init; } = [];
}

public sealed class PostingTargetActionRequest
{
    public string? TargetCode { get; init; }
    /// <summary>Skip only.</summary>
    public string? Reason     { get; init; }
}

public sealed class PostingTargetSummaryModel
{
    public string TargetCode { get; init; } = string.Empty;
    public int    Pending    { get; init; }
    public int    Posted     { get; init; }
    public int    Queued     { get; init; }
    public int    Held       { get; init; }
    public int    Failed     { get; init; }
    public int    DeadLetter { get; init; }
    public int    Skipped    { get; init; }
    public int    Total      { get; init; }
}

public sealed class PostingSummaryModel
{
    public List<PostingTargetSummaryModel> Targets { get; init; } = [];
    public int       Postings       { get; init; }
    public int       NeedsAttention { get; init; }
    public DateTime? LastPostingAt  { get; init; }
}

public sealed class PostingJournalLineModel
{
    public int     LineNumber  { get; init; }
    public string  AccountCode { get; init; } = string.Empty;
    public string  AccountName { get; init; } = string.Empty;
    /// <summary>The line memo.</summary>
    public string? Description { get; init; }
    public decimal Debit       { get; init; }
    public decimal Credit      { get; init; }
}
