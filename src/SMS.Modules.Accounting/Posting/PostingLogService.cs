using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Modules.Accounting.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Exceptions;
using SMS.Shared.Finance.Posting;
using SMS.Shared.Pagination;

namespace SMS.Modules.Accounting.Posting;

/// <summary>
/// A44 D-12 — the cross-target posting log (<c>finance.FinancialPostings</c> + per-target rows): list, detail with the
/// lines and the GL journals of the document, manual retry (attempts reset, dispatched now) and skip (dismiss), and the
/// summary counts. Readable with a GL or an integration permission (D-41); retry/skip per target: internal_gl needs
/// GL_SETUP_MANAGE, external targets INTEGRATION_MANAGE (REV-18). Always the caller's organization.
/// </summary>
public interface IPostingLogService
{
    Task<PaginatedResponse<PostingLogItemModel>> ListAsync(AccountingCaller caller, PostingLogQuery query, CancellationToken ct = default);
    Task<PostingLogDetailModel> GetAsync(AccountingCaller caller, Guid uuid, CancellationToken ct = default);
    /// <summary>D-33 — the postings of one source document (its post and its void), with per-target canAct. Empty when none.</summary>
    Task<IReadOnlyList<PostingLogItemModel>> BySourceAsync(AccountingCaller caller, Guid sourceUuid, CancellationToken ct = default);
    Task<PostingTargetActionResult> RetryAsync(AccountingCaller caller, Guid uuid, string? targetCode, CancellationToken ct = default);
    Task<PostingTargetActionResult> SkipAsync(AccountingCaller caller, Guid uuid, string? targetCode, string? reason, CancellationToken ct = default);
    Task<PostingSummaryModel> SummaryAsync(AccountingCaller caller, CancellationToken ct = default);
}

internal sealed class PostingLogService : IPostingLogService
{
    internal const string NotFoundMessage = "Posting not found.";
    internal const string NothingToRetry = "Nothing to retry: no target of this posting is failed.";
    internal const string NothingToSkip = "Nothing to skip: no target of this posting is failed or pending.";

    private readonly AccountingDbContext _db;
    private readonly FinancialPostingGateway _gateway;
    private readonly QuickBooksStatusRefresher _refresher;

    public PostingLogService(AccountingDbContext db, FinancialPostingGateway gateway, QuickBooksStatusRefresher refresher)
    {
        _db = db;
        _gateway = gateway;
        _refresher = refresher;
    }

    private static bool CanRead(AccountingCaller c) =>
        c.CanView || c.Has(PermissionCodes.INTEGRATION_VIEW) || c.Has(PermissionCodes.INTEGRATION_MANAGE);

    internal const string NoTouchableTarget = "You may not retry or skip any target of this posting.";

    private static bool CanWrite(AccountingCaller c) => c.CanManageSetup || c.Has(PermissionCodes.INTEGRATION_MANAGE);

    /// <summary>D-41 / REV-18 — segregation of duties: the internal GL is GL_SETUP_MANAGE's, every external target is
    /// INTEGRATION_MANAGE's. Neither manage code touches the other side's rows.</summary>
    internal static bool CanTouch(AccountingCaller c, string targetCode) =>
        targetCode == PostingTargetCodes.InternalGl ? c.CanManageSetup : c.Has(PermissionCodes.INTEGRATION_MANAGE);

    /// <summary>The open rows the caller may act on, and the codes of those left out for lack of permission (403 when
    /// some exist but none may be touched).</summary>
    private static (List<FinancialPostingTargetState> Allowed, List<string> Excluded) Touchable(
        AccountingCaller caller, List<FinancialPostingTargetState> open)
    {
        var allowed = open.Where(s => CanTouch(caller, s.TargetCode)).ToList();
        var excluded = open.Where(s => !CanTouch(caller, s.TargetCode)).Select(s => s.TargetCode).ToList();
        if (open.Count > 0 && allowed.Count == 0) throw new ForbiddenException(NoTouchableTarget);
        return (allowed, excluded);
    }

    public async Task<PaginatedResponse<PostingLogItemModel>> ListAsync(AccountingCaller caller, PostingLogQuery query, CancellationToken ct = default)
    {
        caller.Require(CanRead(caller));
        query ??= new PostingLogQuery();
        var org = caller.OrganizationId;
        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, 200);

        var rows = _db.FinancialPostings.IgnoreQueryFilters().AsNoTracking().Where(p => p.OrganizationId == org);
        if (!string.IsNullOrWhiteSpace(query.DocType)) rows = rows.Where(p => p.DocumentType == query.DocType.Trim());
        if (!string.IsNullOrWhiteSpace(query.SourceModule)) rows = rows.Where(p => p.SourceModule == query.SourceModule.Trim());
        if (!string.IsNullOrWhiteSpace(query.Kind)) rows = rows.Where(p => p.Kind == query.Kind.Trim());
        if (query.From is { } from) rows = rows.Where(p => p.TransactionDate >= from);
        if (query.To is { } to) rows = rows.Where(p => p.TransactionDate <= to);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var s = query.Search.Trim();
            rows = rows.Where(p => p.SourceDocumentNumber.Contains(s) || (p.PartnerName != null && p.PartnerName.Contains(s)) || p.IdempotencyKey.Contains(s));
        }
        var status = string.IsNullOrWhiteSpace(query.Status) ? null : query.Status.Trim();
        var target = string.IsNullOrWhiteSpace(query.TargetCode) ? null : query.TargetCode.Trim();
        if (status is not null || target is not null)
            rows = rows.Where(p => p.Targets.Any(t => (status == null || t.Status == status) && (target == null || t.TargetCode == target)));

        var total = await rows.CountAsync(ct);
        var pageIds = await rows.OrderByDescending(p => p.ModifiedAt).ThenByDescending(p => p.Id)
            .Skip((page - 1) * size).Take(size).Select(p => p.Id).ToListAsync(ct);
        // D-49b — this page's open quickbooks rows are brought up to date first (one status call per kind).
        await _refresher.RefreshAsync(org, pageIds, ct);
        var items = await _db.FinancialPostings.IgnoreQueryFilters().AsNoTracking().Include(p => p.Targets)
            .Where(p => pageIds.Contains(p.Id))
            .OrderByDescending(p => p.ModifiedAt).ThenByDescending(p => p.Id)
            .ToListAsync(ct);
        return new PaginatedResponse<PostingLogItemModel>
        {
            Data = items.Select(Item).ToList(),
            TotalRecords = total,
            Page = page,
            PageSize = size,
            TotalPages = (int)Math.Ceiling(total / (double)size),
        };
    }

    public async Task<PostingLogDetailModel> GetAsync(AccountingCaller caller, Guid uuid, CancellationToken ct = default)
    {
        caller.Require(CanRead(caller));
        var id = await _db.FinancialPostings.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.OrganizationId == caller.OrganizationId && p.Uuid == uuid).Select(p => (int?)p.Id).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException(NotFoundMessage);
        await _refresher.RefreshAsync(caller.OrganizationId, [id], ct);
        var posting = await _db.FinancialPostings.IgnoreQueryFilters().AsNoTracking().Include(p => p.Targets).SingleAsync(p => p.Id == id, ct);
        return await DetailAsync(posting, ct);
    }

    public async Task<IReadOnlyList<PostingLogItemModel>> BySourceAsync(AccountingCaller caller, Guid sourceUuid, CancellationToken ct = default)
    {
        caller.Require(CanRead(caller));
        var ids = await _db.FinancialPostings.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.OrganizationId == caller.OrganizationId && p.SourceDocumentUuid == sourceUuid).Select(p => p.Id).ToListAsync(ct);
        await _refresher.RefreshAsync(caller.OrganizationId, ids, ct);
        var postings = await _db.FinancialPostings.IgnoreQueryFilters().AsNoTracking().Include(p => p.Targets)
            .Where(p => ids.Contains(p.Id))
            .ToListAsync(ct);
        return postings
            .OrderBy(p => p.Kind == PostingKinds.Post ? 0 : 1).ThenBy(p => p.CreatedAt).ThenBy(p => p.Id)
            .Select(p =>
            {
                var item = Item(p);
                return new PostingLogItemModel
                {
                    Uuid = item.Uuid, IdempotencyKey = item.IdempotencyKey, Kind = item.Kind, DocumentType = item.DocumentType,
                    SourceModule = item.SourceModule, SourceDocumentUuid = item.SourceDocumentUuid, SourceDocumentNumber = item.SourceDocumentNumber,
                    TransactionDate = item.TransactionDate, PartnerUuid = item.PartnerUuid, PartnerName = item.PartnerName,
                    CurrencyCode = item.CurrencyCode, TotalAmount = item.TotalAmount, Version = item.Version, OverallStatus = item.OverallStatus,
                    CreatedAt = item.CreatedAt, ModifiedAt = item.ModifiedAt,
                    Targets = item.Targets.Select(t => new PostingTargetModel
                    {
                        TargetCode = t.TargetCode, Status = t.Status, Attempts = t.Attempts, NextAttemptAt = t.NextAttemptAt,
                        LastAttemptAt = t.LastAttemptAt, LastError = t.LastError, InternalId = t.InternalId, ExternalId = t.ExternalId,
                        PostedAt = t.PostedAt, ExternalDocNumber = t.ExternalDocNumber, MirroredFromTarget = t.MirroredFromTarget,
                        CanAct = PostingTargetStatuses.IsOpen(t.Status) && CanTouch(caller, t.TargetCode),
                    }).ToList(),
                };
            })
            .ToList();
    }
    public async Task<PostingTargetActionResult> RetryAsync(AccountingCaller caller, Guid uuid, string? targetCode, CancellationToken ct = default)
    {
        caller.Require(CanWrite(caller));
        var posting = await LoadTrackedAsync(caller.OrganizationId, uuid, ct);
        var open = Open(posting, targetCode);
        if (open.Count == 0) throw new ConflictException(NothingToRetry);
        var (states, excluded) = Touchable(caller, open);
        foreach (var (postingId, code) in states.Select(s => (s.PostingId, s.TargetCode)).ToList())
            await _gateway.RetryAsync(postingId, code, resetAttempts: true);
        return new PostingTargetActionResult(await GetAsync(caller, uuid, ct), states.Select(s => s.TargetCode).ToList(), excluded);
    }

    public async Task<PostingTargetActionResult> SkipAsync(AccountingCaller caller, Guid uuid, string? targetCode, string? reason, CancellationToken ct = default)
    {
        caller.Require(CanWrite(caller));
        var posting = await LoadTrackedAsync(caller.OrganizationId, uuid, ct);
        var open = Open(posting, targetCode);
        if (open.Count == 0) throw new ConflictException(NothingToSkip);
        var (states, excluded) = Touchable(caller, open);
        var who = string.IsNullOrWhiteSpace(caller.UserName) ? $"user {caller.UserId}" : caller.UserName;
        var text = string.IsNullOrWhiteSpace(reason) ? $"Skipped by {who}." : $"Skipped by {who}: {reason.Trim()}";
        foreach (var state in states)
        {
            state.Status = PostingTargetStatuses.Skipped;
            state.NextAttemptAt = null;
            state.LastError = FinancialPostingGateway.Truncate(text, 1000);
        }
        await _db.SaveChangesAsync(ct);
        return new PostingTargetActionResult(await GetAsync(caller, uuid, ct), states.Select(s => s.TargetCode).ToList(), excluded);
    }

    public async Task<PostingSummaryModel> SummaryAsync(AccountingCaller caller, CancellationToken ct = default)
    {
        caller.Require(CanRead(caller));
        var org = caller.OrganizationId;
        await _refresher.RefreshAsync(org, null, ct); // D-49b — counts after the open quickbooks rows are current
        var counts = await _db.FinancialPostingTargets.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.OrganizationId == org)
            .GroupBy(t => new { t.TargetCode, t.Status })
            .Select(g => new { g.Key.TargetCode, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);
        var postings = _db.FinancialPostings.IgnoreQueryFilters().AsNoTracking().Where(p => p.OrganizationId == org);
        int Of(string code, string status) => counts.Where(c => c.TargetCode == code && c.Status == status).Sum(c => c.Count);
        return new PostingSummaryModel
        {
            Targets = counts.Select(c => c.TargetCode).Distinct().OrderBy(c => c, StringComparer.Ordinal).Select(code => new PostingTargetSummaryModel
            {
                TargetCode = code,
                Pending    = Of(code, PostingTargetStatuses.Pending),
                Posted     = Of(code, PostingTargetStatuses.Posted),
                Queued     = Of(code, PostingTargetStatuses.Queued),
                Held       = Of(code, PostingTargetStatuses.Held),
                Failed     = Of(code, PostingTargetStatuses.Failed),
                DeadLetter = Of(code, PostingTargetStatuses.DeadLetter),
                Skipped    = Of(code, PostingTargetStatuses.Skipped),
                Total      = counts.Where(c => c.TargetCode == code).Sum(c => c.Count),
            }).ToList(),
            Postings = await postings.CountAsync(ct),
            NeedsAttention = await postings.CountAsync(p => p.Targets.Any(t =>
                t.Status == PostingTargetStatuses.Failed || t.Status == PostingTargetStatuses.DeadLetter), ct),
            LastPostingAt = await postings.MaxAsync(p => (DateTime?)p.ModifiedAt, ct),
        };
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<FinancialPosting> LoadTrackedAsync(Guid org, Guid uuid, CancellationToken ct) =>
        await _db.FinancialPostings.IgnoreQueryFilters().Include(p => p.Targets)
            .FirstOrDefaultAsync(p => p.OrganizationId == org && p.Uuid == uuid, ct)
        ?? throw new NotFoundException(NotFoundMessage);

    private static List<FinancialPostingTargetState> Open(FinancialPosting posting, string? targetCode) =>
        posting.Targets.Where(t => PostingTargetStatuses.IsOpen(t.Status)
                                   && (string.IsNullOrWhiteSpace(targetCode) || t.TargetCode == targetCode.Trim()))
            .OrderBy(t => t.TargetCode, StringComparer.Ordinal).ToList();

    private static List<PostingTargetModel> Targets(FinancialPosting p) =>
        p.Targets.OrderBy(t => t.TargetCode, StringComparer.Ordinal).Select(t => new PostingTargetModel
        {
            TargetCode = t.TargetCode, Status = t.Status, Attempts = t.Attempts, NextAttemptAt = t.NextAttemptAt,
            LastAttemptAt = t.LastAttemptAt, LastError = t.LastError, InternalId = t.InternalId, ExternalId = t.ExternalId, PostedAt = t.PostedAt,
            ExternalDocNumber = t.ExternalDocNumber, MirroredFromTarget = t.MirroredFromTarget,
        }).ToList();

    private static PostingLogItemModel Item(FinancialPosting p)
    {
        var targets = Targets(p);
        return new PostingLogItemModel
        {
            Uuid = p.Uuid, IdempotencyKey = p.IdempotencyKey, Kind = p.Kind, DocumentType = p.DocumentType, SourceModule = p.SourceModule,
            SourceDocumentUuid = p.SourceDocumentUuid, SourceDocumentNumber = p.SourceDocumentNumber, TransactionDate = p.TransactionDate,
            PartnerUuid = p.PartnerUuid, PartnerName = p.PartnerName, CurrencyCode = p.CurrencyCode, TotalAmount = p.TotalAmount,
            Version = p.Version, OverallStatus = PostingTargetStatuses.Worst(targets.Select(t => t.Status)),
            CreatedAt = p.CreatedAt, ModifiedAt = p.ModifiedAt, Targets = targets,
        };
    }

    private async Task<PostingLogDetailModel> DetailAsync(FinancialPosting p, CancellationToken ct)
    {
        var shell = Item(p);
        string? description = null;
        decimal? rate = null;
        var lines = new List<PostingLineModel>();
        if (p.Kind == PostingKinds.Post)
        {
            try
            {
                var tx = PostingSerializer.DeserializeTransaction(p.TransactionJson, null);
                description = tx.Description;
                rate = tx.ExchangeRate;
                lines = tx.Lines.OrderBy(l => l.LineNumber).Select(l => new PostingLineModel
                {
                    LineNumber = l.LineNumber, AccountRole = l.AccountRole, AccountCode = l.AccountCode, Debit = l.Debit, Credit = l.Credit,
                    Label = l.Label, TaxCode = l.TaxCode, TaxAmount = l.TaxAmount,
                }).ToList();
            }
            catch (Exception)
            {
                // A row written by an older contract: shown without its lines.
            }
        }
        else
        {
            try { description = PostingSerializer.DeserializeVoid(p.TransactionJson).Reason; } catch (Exception) { /* as above */ }
        }

        // Every journal of the document: its posting key, the void key and the update reversals under the posting key.
        var baseKey = p.Kind == PostingKinds.Post ? p.IdempotencyKey : $"{p.SourceModule}:{p.DocumentType}:{p.SourceDocumentUuid}";
        var voidKey = baseKey + ":void";
        var journals = await _db.JournalEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(j => j.OrganizationId == p.OrganizationId && (j.IdempotencyKey == baseKey || j.IdempotencyKey == voidKey))
            .OrderBy(j => j.Id)
            .Select(j => new { j.Id, j.Uuid, j.EntryNumber, j.EntryType, j.EntryDate, j.Status, j.TotalDebit, j.TotalCredit, j.CurrencyCode, j.ReversesEntryId })
            .ToListAsync(ct);
        var numbers = journals.ToDictionary(j => j.Id, j => j.EntryNumber);
        var reversed = journals.Where(j => j.ReversesEntryId != null).Select(j => j.ReversesEntryId!.Value).ToHashSet();
        var reversedBy = journals.Where(j => j.ReversesEntryId != null).GroupBy(j => j.ReversesEntryId!.Value)
            .ToDictionary(g => g.Key, g => g.First().EntryNumber);
        // D-36 — the journals' lines with their accounts (base currency), one query.
        var journalIds = journals.Select(j => j.Id).ToList();
        var journalLines = (await _db.JournalEntryLines.IgnoreQueryFilters().AsNoTracking()
                .Where(l => journalIds.Contains(l.JournalEntryId))
                .Join(_db.ChartOfAccounts.IgnoreQueryFilters(), l => l.AccountId, a => a.Id,
                    (l, a) => new { l.JournalEntryId, l.LineNumber, a.AccountCode, a.AccountName, l.Memo, l.DebitAmount, l.CreditAmount })
                .ToListAsync(ct))
            .GroupBy(l => l.JournalEntryId)
            .ToDictionary(g => g.Key, g => g.OrderBy(l => l.LineNumber).Select(l => new PostingJournalLineModel
            {
                LineNumber = l.LineNumber, AccountCode = l.AccountCode, AccountName = l.AccountName, Description = l.Memo,
                Debit = l.DebitAmount, Credit = l.CreditAmount,
            }).ToList());

        return new PostingLogDetailModel
        {
            Uuid = shell.Uuid, IdempotencyKey = shell.IdempotencyKey, Kind = shell.Kind, DocumentType = shell.DocumentType,
            SourceModule = shell.SourceModule, SourceDocumentUuid = shell.SourceDocumentUuid, SourceDocumentNumber = shell.SourceDocumentNumber,
            TransactionDate = shell.TransactionDate, PartnerUuid = shell.PartnerUuid, PartnerName = shell.PartnerName,
            CurrencyCode = shell.CurrencyCode, TotalAmount = shell.TotalAmount, Version = shell.Version, OverallStatus = shell.OverallStatus,
            CreatedAt = shell.CreatedAt, ModifiedAt = shell.ModifiedAt, Targets = shell.Targets,
            PartnerType = p.PartnerType, DocumentPayloadType = p.DocumentPayloadType,
            Description = description, ExchangeRate = rate, Lines = lines,
            Journals = journals.Select(j => new PostingJournalModel
            {
                Uuid = j.Uuid, EntryNumber = j.EntryNumber, EntryType = j.EntryType, EntryDate = j.EntryDate, Status = j.Status,
                TotalDebit = j.TotalDebit, TotalCredit = j.TotalCredit, CurrencyCode = j.CurrencyCode,
                ReversesEntryNumber = j.ReversesEntryId is { } r ? numbers.GetValueOrDefault(r) : null,
                IsReversed = reversed.Contains(j.Id),
                ReversesJournalNumber = j.ReversesEntryId is { } rv ? numbers.GetValueOrDefault(rv) : null,
                ReversedByJournalNumber = reversedBy.GetValueOrDefault(j.Id),
                Lines = journalLines.GetValueOrDefault(j.Id) ?? [],
            }).ToList(),
        };
    }
}

/// <summary>Retry/skip outcome: the posting after the action, the targets acted on, and the open targets left out because the
/// caller may not touch them (REV-18).</summary>
public sealed record PostingTargetActionResult(PostingLogDetailModel Posting, IReadOnlyList<string> Acted, IReadOnlyList<string> NotPermitted)
{
    public string Message(string verb) => NotPermitted.Count == 0
        ? $"{verb}."
        : $"{verb}: {string.Join(", ", Acted)}. Not {verb.ToLowerInvariant()} (no permission): {string.Join(", ", NotPermitted)}.";
}
