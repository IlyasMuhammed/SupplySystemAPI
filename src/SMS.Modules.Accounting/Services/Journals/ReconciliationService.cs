using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Accounting.Services;

/// <summary>A47 C8 — reconciliation error codes (API-CONTRACT §4).</summary>
public static class ReconcileErrorCodes
{
    public const string NotPosted = "RC-001", DifferentAccounts = "RC-002", TooFewLines = "RC-003", AlreadyInGroup = "RC-004",
        LineNotFound = "RC-005", GroupNotFound = "RC-006", NothingToReconcile = "RC-007", AccountRequired = "RC-008";
}

/// <summary>A47 C8 — the exact user-facing texts (API-CONTRACT §4).</summary>
public static class ReconcileMessages
{
    public const string NotPosted          = "Only lines from posted entries can be reconciled";
    public const string DifferentAccounts  = "All lines must belong to the same account";
    public const string TooFewLines        = "Minimum 2 lines required";
    public const string LineNotFound       = "Journal line not found";
    public const string GroupNotFound      = "Reconciliation not found";
    public const string NothingToReconcile = "The lines have nothing left to reconcile";
    public const string AccountRequired    = "Choose an account";
    public const string AccountNotFound    = "Account not found.";
    public const string NoSelection        = "Select at least one line.";
    public const string DatesInvalid       = "The to date must not be before the from date.";
    public const string NoPermission       = "You do not have permission to reconcile journal lines.";

    /// <summary>RC-004 (BR-RC-008 / D-16), e.g. "Line 1 of SALE/2026/0001 is already in reconciliation REC/2026/0003: unreconcile it first".</summary>
    public static string AlreadyInGroup(int lineNumber, string entryNumber, string groupNumber) =>
        $"Line {lineNumber} of {entryNumber} is already in reconciliation {groupNumber}: unreconcile it first";
}

/// <summary>
/// A47 C8 §11.3 — line-level reconciliation through the API (api/finance/reconciliation, contract §4). Uuids in and out; the
/// organization is always the caller's. Reads: GL_RECONCILE_VIEW (or EXECUTE); writes: GL_RECONCILE_EXECUTE (D-15).
/// </summary>
public interface IReconciliationService
{
    /// <summary>Reconciles ≥ 2 posted lines of one account: Full when they net to zero, else Partial (BR-RC-001..008).</summary>
    Task<ReconcileGroupModel> ReconcileAsync(AccountingCaller caller, ReconcileRequest request, CancellationToken ct = default);

    /// <summary>Undoes a reconciliation: residuals restored, links cleared, group deleted (BR-RC-007).</summary>
    Task<UnreconcileResultModel> UnreconcileAsync(AccountingCaller caller, Guid groupUuid, CancellationToken ct = default);

    /// <summary>One reconciliation group with its lines.</summary>
    Task<ReconcileGroupModel> GetGroupAsync(AccountingCaller caller, Guid groupUuid, CancellationToken ct = default);

    /// <summary>SR-FE-1 — the reconciliation groups (Full and Partial), newest first, paged; filters optional.</summary>
    Task<PaginatedResponse<ReconcileGroupListItemModel>> ListGroupsAsync(AccountingCaller caller, ReconcileGroupQuery query, CancellationToken ct = default);

    /// <summary>The open (not fully reconciled) posted lines of an account, optionally of one partner.</summary>
    Task<IReadOnlyList<OpenItemModel>> GetOpenItemsAsync(AccountingCaller caller, OpenItemsQuery query, CancellationToken ct = default);

    /// <summary>Opposite-side open lines of the same account whose residuals offset the selection, exact matches first (TS-RC-007).</summary>
    Task<IReadOnlyList<ReconcileSuggestionModel>> SuggestMatchesAsync(AccountingCaller caller, ReconcileSuggestRequest request, CancellationToken ct = default);
}

internal sealed class ReconciliationService : IReconciliationService
{
    internal const int DefaultSuggestions = 10, MaxSuggestions = 50, MaxCandidates = 300, CombinationPool = 25, MaxCombinationSize = 4;
    /// <summary>D-RR-7 — open items are capped (oldest first); narrow by partner or dates beyond that.</summary>
    internal const int MaxOpenItems = 2000;
    internal const int MaxGroupPageSize = 200;

    private readonly AccountingDbContext _db;
    private readonly JournalReconciler _reconciler;

    public ReconciliationService(AccountingDbContext db, JournalReconciler reconciler)
    {
        _db = db;
        _reconciler = reconciler;
    }

    // ── writes ─────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<ReconcileGroupModel> ReconcileAsync(AccountingCaller caller, ReconcileRequest request, CancellationToken ct = default)
    {
        caller.Require(caller.CanReconcile, ReconcileMessages.NoPermission);
        ArgumentNullException.ThrowIfNull(request);
        var org = caller.OrganizationId;
        var uuids = (request.LineUuids ?? []).Where(u => u != Guid.Empty).Distinct().ToList();
        if (uuids.Count < 2) throw new JournalValidationException(ReconcileErrorCodes.TooFewLines, ReconcileMessages.TooFewLines);

        // The account is read first, so its lock is taken before the lines are read for the change.
        var preview = await LoadAsync(org, uuids, track: false, ct);
        Validate(preview);

        ReconcileGroup? group = null;
        await _db.InTransactionAsync(async () =>
        {
            await _reconciler.LockAccountsAsync(org, preview.Select(i => i.Line.AccountId), ct);
            var items = await LoadAsync(org, uuids, track: true, ct);
            Validate(items);
            await EnsureNotInGroupAsync(items, ct);
            group = await _reconciler.ReconcileAsync(org, items[0].Line.AccountId, items, caller.UserIdOrNull, caller.UserName, null, ct)
                    ?? throw new JournalValidationException(ReconcileErrorCodes.NothingToReconcile, ReconcileMessages.NothingToReconcile);
            await _db.SaveChangesAsync(ct);
        }, ct);
        return await GroupModelAsync(org, group!.Id, ct);
    }

    public async Task<UnreconcileResultModel> UnreconcileAsync(AccountingCaller caller, Guid groupUuid, CancellationToken ct = default)
    {
        caller.Require(caller.CanReconcile, ReconcileMessages.NoPermission);
        var org = caller.OrganizationId;
        var accountId = await _db.ReconcileGroups.IgnoreQueryFilters().AsNoTracking()
            .Where(g => g.OrganizationId == org && g.Uuid == groupUuid).Select(g => (int?)g.AccountId).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException(ReconcileMessages.GroupNotFound);

        var result = new UnreconcileResultModel();
        await _db.InTransactionAsync(async () =>
        {
            await _reconciler.LockAccountsAsync(org, [accountId], ct);
            var group = await _db.ReconcileGroups.IgnoreQueryFilters()
                .FirstOrDefaultAsync(g => g.OrganizationId == org && g.Uuid == groupUuid, ct)
                ?? throw new NotFoundException(ReconcileMessages.GroupNotFound);
            var lines = await _db.JournalEntryLines.IgnoreQueryFilters()
                .Where(l => l.OrganizationId == org && l.ReconcileGroupId == group.Id).ToListAsync(ct);
            result.GroupNumber = group.GroupNumber;
            result.LinesRestored = _reconciler.Unreconcile(group, lines);
            await _db.SaveChangesAsync(ct);
        }, ct);
        return result;
    }

    // ── reads ──────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<ReconcileGroupModel> GetGroupAsync(AccountingCaller caller, Guid groupUuid, CancellationToken ct = default)
    {
        caller.Require(caller.CanViewReconciliation);
        var id = await _db.ReconcileGroups.IgnoreQueryFilters().AsNoTracking()
            .Where(g => g.OrganizationId == caller.OrganizationId && g.Uuid == groupUuid).Select(g => (int?)g.Id).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException(ReconcileMessages.GroupNotFound);
        return await GroupModelAsync(caller.OrganizationId, id, ct);
    }

    public async Task<PaginatedResponse<ReconcileGroupListItemModel>> ListGroupsAsync(AccountingCaller caller, ReconcileGroupQuery query,
        CancellationToken ct = default)
    {
        caller.Require(caller.CanViewReconciliation);
        query ??= new ReconcileGroupQuery();
        var org = caller.OrganizationId;
        var fromDate = query.FromDate ?? query.From;
        var toDate = query.ToDate ?? query.To;
        if (fromDate is { } f0 && toDate is { } t0 && t0 < f0) throw new BadRequestException(ReconcileMessages.DatesInvalid);

        var rows = _db.ReconcileGroups.IgnoreQueryFilters().AsNoTracking().Where(g => g.OrganizationId == org);
        if (query.AccountUuid is { } au && au != Guid.Empty)
        {
            var accountId = await AccountIdAsync(org, au, ct);
            rows = rows.Where(g => g.AccountId == accountId);
        }
        if (query.PartnerUuid is { } partner)
            rows = rows.Where(g => _db.JournalEntryLines.IgnoreQueryFilters().Any(l => l.ReconcileGroupId == g.Id && l.PartnerUuid == partner));
        if (fromDate is { } from)
        {
            var start = from.ToDateTime(TimeOnly.MinValue);
            rows = rows.Where(g => g.ReconciledAt >= start);
        }
        if (toDate is { } to)
        {
            var end = to.AddDays(1).ToDateTime(TimeOnly.MinValue);
            rows = rows.Where(g => g.ReconciledAt < end);
        }

        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize <= 0 ? 25 : query.PageSize, 1, MaxGroupPageSize);
        var total = await rows.CountAsync(ct);
        var data = await rows.OrderByDescending(g => g.ReconciledAt).ThenByDescending(g => g.Id)
            .Skip((page - 1) * size).Take(size)
            .Select(g => new ReconcileGroupListItemModel
            {
                Uuid          = g.Uuid,
                GroupNumber   = g.GroupNumber,
                ReconcileType = g.ReconcileType,
                Account       = new AccountRefModel { Uuid = g.Account.Uuid, AccountCode = g.Account.AccountCode, AccountName = g.Account.AccountName, RootType = g.Account.RootType },
                Amount        = g.Amount,
                CurrencyCode  = g.CurrencyCode,
                ReconciledAt  = g.ReconciledAt,
                CreatedByName = g.CreatedByName,
                LineCount     = _db.JournalEntryLines.IgnoreQueryFilters().Count(l => l.ReconcileGroupId == g.Id),
            })
            .ToListAsync(ct);
        return new PaginatedResponse<ReconcileGroupListItemModel>
        {
            Data = data, TotalRecords = total, Page = page, PageSize = size, TotalPages = (int)Math.Ceiling(total / (double)size),
        };
    }

    public async Task<IReadOnlyList<OpenItemModel>> GetOpenItemsAsync(AccountingCaller caller, OpenItemsQuery query, CancellationToken ct = default)
    {
        caller.Require(caller.CanViewReconciliation);
        query ??= new OpenItemsQuery();
        var org = caller.OrganizationId;
        var accountId = await AccountIdAsync(org, query.AccountUuid, ct);
        if (query.FromDate is { } f && query.ToDate is { } t && t < f) throw new BadRequestException(ReconcileMessages.DatesInvalid);

        var rows = OpenLines(org, accountId);
        if (!string.IsNullOrWhiteSpace(query.PartnerType))
        {
            var type = JournalPartnerTypes.All.FirstOrDefault(p => string.Equals(p, query.PartnerType.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new BadRequestException(JournalMessages.PartnerTypeInvalid + ".");
            rows = rows.Where(l => l.PartnerType == type);
        }
        if (query.PartnerUuid is { } partner) rows = rows.Where(l => l.PartnerUuid == partner);
        if (query.FromDate is { } from) rows = rows.Where(l => l.JournalEntry.EntryDate >= from);
        if (query.ToDate is { } to) rows = rows.Where(l => l.JournalEntry.EntryDate <= to);

        return await rows.OrderBy(l => l.JournalEntry.EntryDate).ThenBy(l => l.JournalEntry.EntryNumber).ThenBy(l => l.LineNumber)
            .Take(MaxOpenItems).Select(ToOpenItem).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ReconcileSuggestionModel>> SuggestMatchesAsync(AccountingCaller caller, ReconcileSuggestRequest request,
        CancellationToken ct = default)
    {
        caller.Require(caller.CanViewReconciliation);
        ArgumentNullException.ThrowIfNull(request);
        var org = caller.OrganizationId;
        var accountId = await AccountIdAsync(org, request.AccountUuid, ct);
        var uuids = (request.LineUuids ?? []).Where(u => u != Guid.Empty).Distinct().ToList();
        if (uuids.Count == 0) throw new BadRequestException(ReconcileMessages.NoSelection);

        var selected = await LoadAsync(org, uuids, track: false, ct);
        Validate(selected);
        if (selected[0].Line.AccountId != accountId)
            throw new JournalConflictException(ReconcileErrorCodes.DifferentAccounts, ReconcileMessages.DifferentAccounts);
        await EnsureNotInGroupAsync(selected, ct);

        var target = selected.Sum(i => i.Line.AmountResidual);
        if (target == 0) return [];
        var max = Math.Clamp(request.MaxResults ?? DefaultSuggestions, 1, MaxSuggestions);

        var selectedIds = selected.Select(i => i.Line.Id).ToList();
        var partners = selected.Select(i => i.Line.PartnerUuid).Distinct().ToList();
        Guid? partner = partners.Count == 1 ? partners[0] : null;
        var pool = OpenLines(org, accountId).Where(l => l.ReconcileGroupId == null && !selectedIds.Contains(l.Id));
        pool = target > 0 ? pool.Where(l => l.AmountResidual < 0) : pool.Where(l => l.AmountResidual > 0);
        var candidates = await pool
            .OrderBy(l => partner != null && l.PartnerUuid == partner ? 0 : 1)
            .ThenBy(l => l.JournalEntry.EntryDate).ThenBy(l => l.JournalEntryId).ThenBy(l => l.LineNumber)
            .Take(MaxCandidates)
            .Select(l => new Candidate(l.Uuid, l.AmountResidual))
            .ToListAsync(ct);
        return Suggest(target, candidates, max);
    }

    // ── suggestions (pure) ─────────────────────────────────────────────────────────────────────────────────────────

    internal sealed record Candidate(Guid LineUuid, decimal Residual);

    /// <summary>
    /// D-RR-8 — up to <paramref name="max"/>, exact ones first: single lines that offset the selection, then sets of 2–4 lines that do
    /// (searched among the first 25 candidates), then the closest non-exact ones (single lines and the oldest-first run that covers
    /// it) by |residualAfter|. Candidates come ordered: the selection's partner first, then oldest.
    /// </summary>
    internal static List<ReconcileSuggestionModel> Suggest(decimal target, IReadOnlyList<Candidate> candidates, int max)
    {
        var need = Math.Abs(target);
        var tol = JournalLimits.BalanceTolerance;
        var exact = new List<List<Candidate>>();

        foreach (var c in candidates.Where(c => Math.Abs(Math.Abs(c.Residual) - need) < tol))
        {
            if (exact.Count >= max) break;
            exact.Add([c]);
        }

        var pool = candidates.Take(CombinationPool).ToList();
        var budget = 50_000;
        var stack = new List<int>();
        void Search(int start, decimal sum)
        {
            if (exact.Count >= max || budget-- <= 0) return;
            if (stack.Count >= 2 && Math.Abs(sum - need) < tol)
            {
                exact.Add(stack.Select(i => pool[i]).ToList());
                return;
            }
            if (stack.Count == MaxCombinationSize) return;
            for (var i = start; i < pool.Count && exact.Count < max; i++)
            {
                var s = sum + Math.Abs(pool[i].Residual);
                if (s > need + tol) continue;
                stack.Add(i);
                Search(i + 1, s);
                stack.RemoveAt(stack.Count - 1);
            }
        }
        if (exact.Count < max) Search(0, 0m);

        var result = exact.Select(set => Suggestion(target, set)).ToList();
        if (result.Count >= max) return result.Take(max).ToList();

        // Nearest non-exact: every single line, and the oldest-first run that covers the selection.
        var near = new List<List<Candidate>>();
        near.AddRange(candidates.Select(c => new List<Candidate> { c }));
        var run = new List<Candidate>();
        var covered = 0m;
        foreach (var c in candidates)
        {
            run.Add(c);
            covered += Math.Abs(c.Residual);
            if (covered >= need) break;
        }
        if (run.Count > 1) near.Add(run);
        var seen = result.Select(r => string.Join(",", r.LineUuids.OrderBy(u => u))).ToHashSet();
        foreach (var s in near.Select(set => Suggestion(target, set)).Where(s => !s.Exact)
                     .OrderBy(s => Math.Abs(s.ResidualAfter)).ThenBy(s => s.LineUuids.Count))
        {
            if (result.Count >= max) break;
            if (seen.Add(string.Join(",", s.LineUuids.OrderBy(u => u)))) result.Add(s);
        }
        return result;
    }

    private static ReconcileSuggestionModel Suggestion(decimal target, IReadOnlyList<Candidate> lines)
    {
        var total = lines.Sum(l => l.Residual);
        var after = target + total;
        var exact = Math.Abs(after) < JournalLimits.BalanceTolerance;
        return new ReconcileSuggestionModel { LineUuids = lines.Select(l => l.LineUuid).ToList(), Total = total, ResidualAfter = exact ? 0m : after, Exact = exact };
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The lines with these uuids and their entries (request order); an unknown one is 404 RC-005.</summary>
    private async Task<List<JournalReconciler.Item>> LoadAsync(Guid org, IReadOnlyList<Guid> uuids, bool track, CancellationToken ct)
    {
        var q = _db.JournalEntryLines.IgnoreQueryFilters().Include(l => l.JournalEntry).Where(l => l.OrganizationId == org && uuids.Contains(l.Uuid));
        var lines = await (track ? q : q.AsNoTracking()).ToListAsync(ct);
        var byUuid = lines.ToDictionary(l => l.Uuid);
        var items = new List<JournalReconciler.Item>();
        foreach (var u in uuids)
        {
            if (!byUuid.TryGetValue(u, out var line)) throw new NotFoundException(ReconcileMessages.LineNotFound);
            items.Add(new JournalReconciler.Item(line, line.JournalEntry));
        }
        return items;
    }

    /// <summary>BR-RC-001 (RC-001) and BR-RC-002 (RC-002) — 409.</summary>
    private static void Validate(IReadOnlyList<JournalReconciler.Item> items)
    {
        if (items.Any(i => !JournalStatuses.IsPostedFamily(i.Entry.Status)))
            throw new JournalConflictException(ReconcileErrorCodes.NotPosted, ReconcileMessages.NotPosted);
        if (items.Select(i => i.Line.AccountId).Distinct().Count() > 1)
            throw new JournalConflictException(ReconcileErrorCodes.DifferentAccounts, ReconcileMessages.DifferentAccounts);
    }

    /// <summary>BR-RC-008 / D-16 (RC-004) — a line in a reconciliation (full or partial) is refused until that one is undone.</summary>
    private async Task EnsureNotInGroupAsync(IReadOnlyList<JournalReconciler.Item> items, CancellationToken ct)
    {
        var taken = items.FirstOrDefault(i => i.Line.ReconcileGroupId is not null || i.Line.Reconciled);
        if (taken.Line is null) return;
        var number = taken.Line.ReconcileGroupId is { } gid
            ? await _db.ReconcileGroups.IgnoreQueryFilters().AsNoTracking().Where(g => g.Id == gid).Select(g => g.GroupNumber).FirstOrDefaultAsync(ct)
            : null;
        throw new JournalConflictException(ReconcileErrorCodes.AlreadyInGroup,
            ReconcileMessages.AlreadyInGroup(taken.Line.LineNumber, taken.Entry.EntryNumber, number ?? "?"));
    }

    /// <summary>Open = not fully reconciled, a residual left, on a Posted or Reversed entry (BR-RC-001, D-CORE-4).</summary>
    private IQueryable<JournalEntryLine> OpenLines(Guid org, int accountId) =>
        _db.JournalEntryLines.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.OrganizationId == org && l.AccountId == accountId && !l.Reconciled && l.AmountResidual != 0
                        && (l.JournalEntry.Status == JournalStatuses.Posted || l.JournalEntry.Status == JournalStatuses.Reversed));

    /// <summary>RC-008 400 without an account; 404 for an account of another organization.</summary>
    private async Task<int> AccountIdAsync(Guid org, Guid? uuid, CancellationToken ct)
    {
        if (uuid is not { } u || u == Guid.Empty) throw new JournalValidationException(ReconcileErrorCodes.AccountRequired, ReconcileMessages.AccountRequired);
        return await _db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking().Where(a => a.OrganizationId == org && a.Uuid == u)
                   .Select(a => (int?)a.Id).FirstOrDefaultAsync(ct)
               ?? throw new NotFoundException(ReconcileMessages.AccountNotFound);
    }

    internal static readonly Expression<Func<JournalEntryLine, OpenItemModel>> ToOpenItem = l => new OpenItemModel
    {
        LineUuid             = l.Uuid,
        Entry                = new JournalEntryRefModel { Uuid = l.JournalEntry.Uuid, EntryNumber = l.JournalEntry.EntryNumber },
        EntryDate            = l.JournalEntry.EntryDate,
        JournalTypeCode      = l.JournalEntry.JournalType.Code,
        Reference            = l.JournalEntry.Reference,
        Description          = l.Memo ?? l.JournalEntry.Description,
        LineNumber           = l.LineNumber,
        Account              = new AccountRefModel { Uuid = l.Account.Uuid, AccountCode = l.Account.AccountCode, AccountName = l.Account.AccountName, RootType = l.Account.RootType },
        PartnerUuid          = l.PartnerUuid,
        PartnerType          = l.PartnerType,
        PartnerName          = l.PartnerName,
        Debit                = l.DebitAmount,
        Credit               = l.CreditAmount,
        AmountResidual       = l.AmountResidual,
        ReconcileGroupUuid   = l.ReconcileGroup != null ? l.ReconcileGroup.Uuid : null,
        ReconcileGroupNumber = l.ReconcileGroup != null ? l.ReconcileGroup.GroupNumber : null,
    };

    internal async Task<ReconcileGroupModel> GroupModelAsync(Guid org, int groupId, CancellationToken ct)
    {
        var g = await _db.ReconcileGroups.IgnoreQueryFilters().AsNoTracking().Include(x => x.Account)
            .FirstOrDefaultAsync(x => x.OrganizationId == org && x.Id == groupId, ct)
            ?? throw new NotFoundException(ReconcileMessages.GroupNotFound);
        var lines = await _db.JournalEntryLines.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.OrganizationId == org && l.ReconcileGroupId == groupId)
            .OrderBy(l => l.JournalEntry.EntryDate).ThenBy(l => l.JournalEntry.EntryNumber).ThenBy(l => l.LineNumber)
            .Select(ToOpenItem).ToListAsync(ct);
        return new ReconcileGroupModel
        {
            Uuid          = g.Uuid,
            GroupNumber   = g.GroupNumber,
            ReconcileType = g.ReconcileType,
            Account       = new AccountRefModel { Uuid = g.Account.Uuid, AccountCode = g.Account.AccountCode, AccountName = g.Account.AccountName, RootType = g.Account.RootType },
            Amount        = g.Amount,
            CurrencyCode  = g.CurrencyCode,
            ReconciledAt  = g.ReconciledAt,
            CreatedByName = g.CreatedByName,
            Residual      = lines.Sum(l => l.AmountResidual),
            Lines         = lines,
        };
    }
}
