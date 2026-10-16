using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Integration.QuickBooks;

namespace SMS.Modules.Accounting.Posting;

/// <summary>
/// D-49b (QA-2) — keeps the posting log's quickbooks rows current without Integration calling back into Accounting.
/// The QuickBooks target answers Queued/Held when it stores a record; the outbox finishes later. Before the log is read
/// (list page, detail, by-source, summary) and on every retry-job run, the organization's quickbooks rows still Queued or
/// Held are looked up with <see cref="IQuickBooksGateway.GetStatusAsync"/> — one call per kind, at most
/// <see cref="MaxRows"/> rows — and the answer is persisted:
/// Synced → Posted (ExternalId = QuickBooks id, ExternalDocNumber); Voided → Posted ("Voided in QuickBooks." on a post);
/// Failed / Blocked → Failed and NeedsResolution → DeadLetter, both <c>MirroredFromTarget</c> (not retried by the Accounting job;
/// a manual retry re-dispatches); a mirrored row the target is working on again → Queued; anything else stays as it is.
/// Mirrored rows are refreshed too, so a later Synced flips them to Posted. A gateway error is logged and changes nothing.
/// </summary>
internal sealed class QuickBooksStatusRefresher
{
    internal const int MaxRows = 500;
    internal const string VoidedNote = "Voided in QuickBooks.";

    private static readonly Dictionary<string, SyncKind> ByPayloadType = new(StringComparer.Ordinal)
    {
        [nameof(SalesInvoicePayload)]  = SyncKind.SalesInvoice,
        [nameof(BillPayload)]          = SyncKind.Bill,
        [nameof(PaymentPayload)]       = SyncKind.Payment,
        [nameof(BillPaymentPayload)]   = SyncKind.BillPayment,
        [nameof(SalesReceiptPayload)]  = SyncKind.SalesReceipt,
        [nameof(CreditMemoPayload)]    = SyncKind.CreditMemo,
        [nameof(RefundReceiptPayload)] = SyncKind.RefundReceipt,
        [nameof(VendorCreditPayload)]  = SyncKind.VendorCredit,
    };

    private static readonly Dictionary<string, SyncKind> ByDocumentType = new(StringComparer.Ordinal)
    {
        [nameof(FinDocType.SalesInvoice)]  = SyncKind.SalesInvoice,
        [nameof(FinDocType.PurchaseBill)]  = SyncKind.Bill,
        [nameof(FinDocType.Payment)]       = SyncKind.Payment,
        [nameof(FinDocType.BillPayment)]   = SyncKind.BillPayment,
        [nameof(FinDocType.SalesReceipt)]  = SyncKind.SalesReceipt,
        [nameof(FinDocType.CreditNote)]    = SyncKind.CreditMemo,
        [nameof(FinDocType.RefundReceipt)] = SyncKind.RefundReceipt,
        [nameof(FinDocType.VendorCredit)]  = SyncKind.VendorCredit,
    };

    private readonly AccountingDbContext _db;
    private readonly FinancialPostingGateway _gateway;
    private readonly IQuickBooksGateway? _quickBooks;
    private readonly ILogger _log;

    public QuickBooksStatusRefresher(AccountingDbContext db, FinancialPostingGateway gateway, IQuickBooksGateway? quickBooks = null,
        ILogger<QuickBooksStatusRefresher>? log = null)
    {
        _db = db;
        _gateway = gateway;
        _quickBooks = quickBooks;
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    /// <summary>Refreshes the organization's open quickbooks rows (of <paramref name="postingIds"/> only, when given).
    /// Returns how many rows changed.</summary>
    public async Task<int> RefreshAsync(Guid org, IReadOnlyCollection<int>? postingIds = null, CancellationToken ct = default)
    {
        if (_quickBooks is null || _quickBooks is NullQuickBooksGateway) return 0;
        if (postingIds is { Count: 0 }) return 0;

        var query = _db.FinancialPostingTargets.IgnoreQueryFilters().Include(t => t.Posting)
            .Where(t => t.OrganizationId == org && t.TargetCode == PostingTargetCodes.QuickBooks
                        && (t.Status == PostingTargetStatuses.Queued || t.Status == PostingTargetStatuses.Held
                            || (t.MirroredFromTarget && (t.Status == PostingTargetStatuses.Failed || t.Status == PostingTargetStatuses.DeadLetter))));
        if (postingIds is not null) query = query.Where(t => postingIds.Contains(t.PostingId));
        var rows = await query.OrderByDescending(t => t.ModifiedAt).Take(MaxRows).ToListAsync(ct);
        if (rows.Count == 0) return 0;

        // A void's kind and record id are its original's.
        var originalKeys = rows.Where(r => r.Posting.Kind != PostingKinds.Post)
            .Select(r => $"{r.Posting.SourceModule}:{r.Posting.DocumentType}:{r.Posting.SourceDocumentUuid}").Distinct().ToList();
        var originals = originalKeys.Count == 0 ? new Dictionary<string, FinancialPosting>()
            : await _db.FinancialPostings.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.OrganizationId == org && originalKeys.Contains(p.IdempotencyKey))
                .ToDictionaryAsync(p => p.IdempotencyKey, ct);

        var keyed = new List<(FinancialPostingTargetState Row, SyncKind Kind, string ExternalId)>();
        foreach (var row in rows)
        {
            var source = row.Posting.Kind == PostingKinds.Post ? row.Posting
                : originals.GetValueOrDefault($"{row.Posting.SourceModule}:{row.Posting.DocumentType}:{row.Posting.SourceDocumentUuid}") ?? row.Posting;
            if (KindOf(source) is not { } kind) continue;
            keyed.Add((row, kind, ExternalIdOf(source)));
        }

        var changed = 0;
        foreach (var group in keyed.GroupBy(k => k.Kind))
        {
            IReadOnlyList<SyncStatus> statuses;
            try
            {
                statuses = await _quickBooks.GetStatusAsync(group.Key, group.Select(g => g.ExternalId).Distinct().ToList(), ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "QuickBooks status lookup for {Kind} failed; the posting log keeps its last answer.", group.Key);
                continue;
            }
            var byId = statuses.GroupBy(s => s.ExternalId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var (row, _, externalId) in group)
                if (byId.TryGetValue(externalId, out var status) && Apply(row, status)) changed++;
        }
        if (changed > 0) await _db.SaveChangesAsync(CancellationToken.None);
        return changed;
    }

    private bool Apply(FinancialPostingTargetState row, SyncStatus status)
    {
        var isPost = row.Posting.Kind == PostingKinds.Post;
        switch (status.State)
        {
            case SyncState.Synced when isPost:
                SetPosted(row, status, note: null);
                return true;
            case SyncState.Voided:
                SetPosted(row, status, isPost ? VoidedNote : null);
                return true;
            case SyncState.Failed or SyncState.Blocked:
                return Mirror(row, PostingTargetStatuses.Failed, status);
            case SyncState.NeedsResolution:
                return Mirror(row, PostingTargetStatuses.DeadLetter, status);
            case SyncState.Pending or SyncState.InProgress or SyncState.WaitingOnDependency when row.MirroredFromTarget:
                // The target is working on it again (its own retry, or a manual push there): back to Queued.
                row.Status = PostingTargetStatuses.Queued;
                row.MirroredFromTarget = false;
                row.LastError = null;
                row.NextAttemptAt = null;
                return true;
            default:
                return false; // still on its way; a void's record still Synced
        }
    }

    /// <summary>
    /// REV option B — the target's own failure, copied: Failed (Blocked/Failed) or DeadLetter (NeedsResolution), with
    /// <c>MirroredFromTarget</c> and no NextAttemptAt, so the Accounting retry job never re-sends it and never counts it
    /// towards its own dead letter (Integration's outbox owns those retries, D-11). A manual retry still re-dispatches it.
    /// </summary>
    private static bool Mirror(FinancialPostingTargetState row, string target, SyncStatus status)
    {
        var error = string.IsNullOrWhiteSpace(status.LastError) ? $"{status.State}." : status.LastError;
        if (!string.IsNullOrWhiteSpace(status.LastErrorCode)) error = $"{status.LastErrorCode}: {error}";
        var text = FinancialPostingGateway.Truncate($"QuickBooks {status.State}: {error}", 1000);
        if (row.MirroredFromTarget && row.Status == target && row.LastError == text) return false;
        row.Status = target;
        row.MirroredFromTarget = true;
        row.NextAttemptAt = null;
        row.LastError = text;
        row.LastAttemptAt = status.LastSyncedAt ?? row.LastAttemptAt;
        return true;
    }
    private void SetPosted(FinancialPostingTargetState row, SyncStatus status, string? note)
    {
        _gateway.Apply(row, new TargetResult
        {
            TargetCode = row.TargetCode, Status = TargetPostingStatus.Posted, ExternalId = status.RemoteId, Error = note,
        }, row.Posting.Fingerprint);
        if (!string.IsNullOrWhiteSpace(status.RemoteDocNumber))
            row.ExternalDocNumber = FinancialPostingGateway.Truncate(status.RemoteDocNumber, 100);
        if (status.LastSyncedAt is { } at) row.PostedAt = at;
    }

    private static SyncKind? KindOf(FinancialPosting p) =>
        p.DocumentPayloadType is { } t && ByPayloadType.TryGetValue(t, out var k) ? k
        : ByDocumentType.TryGetValue(p.DocumentType, out var d) ? d
        : null;

    /// <summary>The document's own ExternalId (the record key at the target), else the source document uuid.</summary>
    private static string ExternalIdOf(FinancialPosting p)
    {
        if (!string.IsNullOrEmpty(p.DocumentJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(p.DocumentJson);
                if (doc.RootElement.TryGetProperty("externalId", out var id) && id.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(id.GetString()))
                    return id.GetString()!;
            }
            catch (JsonException)
            {
                // fall back to the uuid
            }
        }
        return p.SourceDocumentUuid.ToString();
    }
}
