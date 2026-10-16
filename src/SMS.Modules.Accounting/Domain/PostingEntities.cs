using SMS.Shared.Common;

namespace SMS.Modules.Accounting.Domain;

// A44 (docs/financial-posting/ADDENDUM-44-ANALYSIS.md D-8, D-9, D-12, D-13) — the posting log of the financial posting
// gateway and the per-organization finance posting settings. Schema finance, same conventions as the A42 tables.

/// <summary>
/// D-9 / D-12 — one posting the gateway received: one row per (organization, idempotency key). A re-post of the same key
/// with a changed payload updates the row (D-13: <see cref="Fingerprint"/>, <see cref="Version"/>); a void / reversal is
/// a row of its own (key "…:void"). The transaction and its provider-neutral document are stored so that a retry can
/// rebuild exactly what the caller sent (the document is [JsonIgnore] on the DTO and is serialized separately).
/// </summary>
internal class FinancialPosting : ITenantScopedEntity, IHasModifiedAt
{
    public int       Id                   { get; set; }
    public Guid      Uuid                 { get; set; } = Guid.NewGuid();
    public Guid      OrganizationId       { get; set; }
    /// <summary>"{SourceModule}:{DocumentType}:{SourceDocumentUuid}" (+ ":void"), unique per organization.</summary>
    public string    IdempotencyKey       { get; set; } = string.Empty;
    /// <summary><see cref="PostingKinds"/>.</summary>
    public string    Kind                 { get; set; } = PostingKinds.Post;
    /// <summary>FinDocType name, e.g. SalesInvoice.</summary>
    public string    DocumentType         { get; set; } = string.Empty;
    public string    SourceModule         { get; set; } = string.Empty;
    public Guid      SourceDocumentUuid   { get; set; }
    public string    SourceDocumentNumber { get; set; } = string.Empty;
    public DateOnly  TransactionDate      { get; set; }
    public Guid?     PartnerUuid          { get; set; }
    public string?   PartnerType          { get; set; }
    public string?   PartnerName          { get; set; }
    public string?   CurrencyCode         { get; set; }
    /// <summary>Σ debit of the lines, document currency (for the log).</summary>
    public decimal   TotalAmount          { get; set; }
    /// <summary>SHA-256 (hex) over the transaction JSON and the document JSON (D-13).</summary>
    public string    Fingerprint          { get; set; } = string.Empty;
    /// <summary>1 on the first post, +1 on every changed-payload update.</summary>
    public int       Version              { get; set; } = 1;
    /// <summary>The FinancialTransaction (Post) or VoidRequest (Void/Reverse), camelCase JSON.</summary>
    public string    TransactionJson      { get; set; } = string.Empty;
    /// <summary>Metadata.Document serialized on its own; null when the caller sent none.</summary>
    public string?   DocumentJson         { get; set; }
    /// <summary>The Shared payload type's name (allowlisted by Posting/PostingSerializer.cs).</summary>
    public string?   DocumentPayloadType  { get; set; }
    public DateTime  CreatedAt            { get; set; } = DateTime.UtcNow;
    public DateTime  ModifiedAt           { get; set; } = DateTime.UtcNow;

    public ICollection<FinancialPostingTargetState> Targets { get; set; } = new List<FinancialPostingTargetState>();
}

/// <summary>D-9 — where one posting stands at one target (internal_gl, quickbooks). Failed rows are retried by the job.</summary>
internal class FinancialPostingTargetState : ITenantScopedEntity, IHasModifiedAt
{
    public int       Id                { get; set; }
    public Guid      OrganizationId    { get; set; }
    public int       PostingId         { get; set; }
    public string    TargetCode        { get; set; } = string.Empty;
    /// <summary><see cref="PostingTargetStatuses"/>.</summary>
    public string    Status            { get; set; } = PostingTargetStatuses.Pending;
    /// <summary>Failed attempts since the last success / manual retry.</summary>
    public int       Attempts          { get; set; }
    public DateTime? NextAttemptAt     { get; set; }
    public DateTime? LastAttemptAt     { get; set; }
    public string?   LastError         { get; set; }
    /// <summary>The target's own id (GL: journal entry number; QuickBooks: its map id).</summary>
    public string?   InternalId        { get; set; }
    /// <summary>The external system's id, when known.</summary>
    public string?   ExternalId        { get; set; }
    /// <summary>D-49b — the external system's document number, when known (QuickBooks DocNumber).</summary>
    public string?   ExternalDocNumber { get; set; }
    /// <summary>D-49b (REV, option B) — Failed / DeadLetter copied from the target's own state (QuickBooks outbox), not an
    /// Accounting dispatch failure: the retry job leaves it alone (the target retries itself); a manual retry re-dispatches
    /// and clears it, as does the target reporting success.</summary>
    public bool      MirroredFromTarget { get; set; }
    /// <summary>The posting fingerprint this target last accepted (Posted/Queued/Held) — makes a repeated call a no-op.</summary>
    public string?   PostedFingerprint { get; set; }
    public DateTime? PostedAt          { get; set; }
    public DateTime  CreatedAt         { get; set; } = DateTime.UtcNow;
    public DateTime  ModifiedAt        { get; set; } = DateTime.UtcNow;

    public FinancialPosting Posting { get; set; } = null!;
}

/// <summary>D-8 — the organization's finance posting settings (one row, created on first access).</summary>
internal class TenantFinanceConfig : ITenantScopedEntity, IHasModifiedAt
{
    public int       Id                        { get; set; }
    public Guid      OrganizationId            { get; set; }
    /// <summary>The internal GL takes postings (also needs FEATURE_GENERAL_LEDGER and setup Complete).</summary>
    public bool      InternalAccountingEnabled { get; set; }
    /// <summary>True: journals are Posted. False: journals are created as Draft for review.</summary>
    public bool      AutoPostToGL              { get; set; } = true;
    /// <summary>Read-only mirror; null = the organization's sale base currency.</summary>
    public string?   DefaultCurrencyCode       { get; set; }
    /// <summary>1–12, informational (A42 fiscal years rule).</summary>
    public int       FiscalYearStartMonth      { get; set; } = 1;
    public DateTime  CreatedAt                 { get; set; } = DateTime.UtcNow;
    public DateTime  ModifiedAt                { get; set; } = DateTime.UtcNow;
    public int?      ModifiedBy                { get; set; }
}

public static class PostingKinds
{
    public const string Post = "Post", Void = "Void", Reverse = "Reverse";
    public static readonly IReadOnlyList<string> All = [Post, Void, Reverse];
}

/// <summary>D-9 — a target's state for one posting (superset of the Shared TargetPostingStatus: + Pending, DeadLetter).</summary>
public static class PostingTargetStatuses
{
    public const string Pending = "Pending", Posted = "Posted", Queued = "Queued", Held = "Held", Failed = "Failed",
        DeadLetter = "DeadLetter", Skipped = "Skipped";

    public static readonly IReadOnlyList<string> All = [Pending, Posted, Queued, Held, Failed, DeadLetter, Skipped];

    /// <summary>The overall status of a posting = the worst of its targets, in this order.</summary>
    public static readonly IReadOnlyList<string> Severity = [DeadLetter, Failed, Pending, Held, Queued, Posted, Skipped];

    public static string Worst(IEnumerable<string> statuses)
    {
        var set = statuses.ToHashSet(StringComparer.Ordinal);
        return Severity.FirstOrDefault(set.Contains) ?? Skipped;
    }

    /// <summary>Statuses a manual retry / skip acts on.</summary>
    public static bool IsOpen(string status) => status is Failed or DeadLetter or Pending;
}
