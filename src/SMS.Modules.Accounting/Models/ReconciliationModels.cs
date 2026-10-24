namespace SMS.Modules.Accounting.Models;

// A47 C8 — API-CONTRACT §4 "Reconciliation" (api/finance/reconciliation, owned by RR) and the §3 reverse result. Lines are named by
// their lineUuid. Amounts are base currency; residuals signed (debit +).

/// <summary>GET reconciliation/open-items (query string). accountUuid is required (RC-008).</summary>
public sealed class OpenItemsQuery
{
    public Guid?     AccountUuid { get; set; }
    public Guid?     PartnerUuid { get; set; }
    /// <summary>Customer | Vendor.</summary>
    public string?   PartnerType { get; set; }
    public DateOnly? FromDate    { get; set; }
    public DateOnly? ToDate      { get; set; }
}

/// <summary>GET reconciliation/groups (query string, SR-FE-1) — every filter optional; newest first.</summary>
public sealed class ReconcileGroupQuery
{
    public Guid?     AccountUuid { get; set; }
    /// <summary>Groups with a line of this partner.</summary>
    public Guid?     PartnerUuid { get; set; }
    /// <summary>On the reconciliation date (reconciledAt, UTC). <c>from</c> / <c>to</c> are accepted as aliases.</summary>
    public DateOnly? FromDate    { get; set; }
    public DateOnly? ToDate      { get; set; }
    public DateOnly? From        { get; set; }
    public DateOnly? To          { get; set; }
    public int       Page        { get; set; } = 1;
    /// <summary>Default 25, max 200.</summary>
    public int       PageSize    { get; set; } = 25;
}

/// <summary>One row of GET reconciliation/groups.</summary>
public sealed class ReconcileGroupListItemModel
{
    public Guid      Uuid          { get; set; }
    public string    GroupNumber   { get; set; } = string.Empty;
    /// <summary>Full | Partial.</summary>
    public string    ReconcileType { get; set; } = string.Empty;
    public AccountRefModel Account { get; set; } = new();
    public decimal   Amount        { get; set; }
    public string?   CurrencyCode  { get; set; }
    public DateTime  ReconciledAt  { get; set; }
    public string?   CreatedByName { get; set; }
    public int       LineCount     { get; set; }
}

/// <summary>POST reconciliation/reconcile — ≥ 2 lines of one account, posted, none already in a reconciliation.</summary>
public sealed class ReconcileRequest
{
    public List<Guid> LineUuids { get; set; } = [];
}

/// <summary>POST reconciliation/suggest — the selected line(s) of the account to find counter-lines for (TS-RC-007).</summary>
public sealed class ReconcileSuggestRequest
{
    public Guid?      AccountUuid { get; set; }
    public List<Guid> LineUuids   { get; set; } = [];
    /// <summary>Default 10, max 50.</summary>
    public int?       MaxResults  { get; set; }
}

/// <summary>An open journal line (open items; also a group's lines, reconciled ones included).</summary>
public sealed class OpenItemModel
{
    public Guid      LineUuid         { get; set; }
    public JournalEntryRefModel Entry { get; set; } = new();
    public DateOnly  EntryDate        { get; set; }
    public string    JournalTypeCode  { get; set; } = string.Empty;
    public string?   Reference        { get; set; }
    /// <summary>The line's description, else the entry's.</summary>
    public string?   Description      { get; set; }
    public int       LineNumber       { get; set; }
    public AccountRefModel Account    { get; set; } = new();
    public Guid?     PartnerUuid      { get; set; }
    public string?   PartnerType      { get; set; }
    public string?   PartnerName      { get; set; }
    public decimal   Debit            { get; set; }
    public decimal   Credit           { get; set; }
    /// <summary>Signed, base currency (debit +).</summary>
    public decimal   AmountResidual   { get; set; }
    /// <summary>Set when the line is in a reconciliation (for an open item: a partial one, which must be undone before the line is reconciled again).</summary>
    public Guid?     ReconcileGroupUuid   { get; set; }
    public string?   ReconcileGroupNumber { get; set; }
}

/// <summary>A reconciliation group (the reconcile answer, GET reconciliation/{groupUuid}).</summary>
public sealed class ReconcileGroupModel
{
    public Guid      Uuid          { get; set; }
    /// <summary>REC/{year}/{seq:D4}.</summary>
    public string    GroupNumber   { get; set; } = string.Empty;
    /// <summary>Full | Partial.</summary>
    public string    ReconcileType { get; set; } = string.Empty;
    public AccountRefModel Account { get; set; } = new();
    /// <summary>The amount matched (base, ≥ 0).</summary>
    public decimal   Amount        { get; set; }
    public string?   CurrencyCode  { get; set; }
    public DateTime  ReconciledAt  { get; set; }
    public string?   CreatedByName { get; set; }
    /// <summary>Σ residual left on the group's lines, signed (0 for Full).</summary>
    public decimal   Residual      { get; set; }
    public List<OpenItemModel> Lines { get; set; } = [];
}

/// <summary>One suggested set of opposite-side lines to add to the selection.</summary>
public sealed class ReconcileSuggestionModel
{
    public List<Guid> LineUuids     { get; set; } = [];
    /// <summary>Σ the suggested lines' residuals, signed.</summary>
    public decimal    Total         { get; set; }
    /// <summary>Σ selected + suggested (0 when exact).</summary>
    public decimal    ResidualAfter { get; set; }
    /// <summary>|residualAfter| &lt; 0.01 — reconciling would be Full.</summary>
    public bool       Exact         { get; set; }
}

/// <summary>POST reconciliation/{groupUuid}/unreconcile.</summary>
public sealed class UnreconcileResultModel
{
    public string GroupNumber   { get; set; } = string.Empty;
    public int    LinesRestored { get; set; }
}

/// <summary>
/// POST journal-entries/{uuid}/reverse (contract §3 + Changes): the new reversal entry's detail, the original, the auto-reconciliation
/// groups and, for a journal posted from a source document, the D-14 warning.
/// </summary>
public sealed class JournalReversalResultModel
{
    /// <summary>The new (posted) mirror entry; its reversesEntry is the original.</summary>
    public JournalEntryDetailModel Reversal { get; set; } = new();
    /// <summary>The reversed entry (now status Reversed).</summary>
    public JournalEntryRefModel    Original { get; set; } = new();
    /// <summary>The BR-RV-008 groups, one per account whose original lines were still unreconciled.</summary>
    public List<string> ReconcileGroupNumbers { get; set; } = [];
    /// <summary>D-14 — set when the original came from a source document, which is not changed.</summary>
    public string? Warning { get; set; }
}
