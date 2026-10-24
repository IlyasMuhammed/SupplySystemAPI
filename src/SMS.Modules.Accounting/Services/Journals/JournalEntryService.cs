using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Accounting.Services;

/// <summary>
/// A47 C6 / §9.2 — journal entries through the API (api/finance/journal-entries, docs/journal-engine/API-CONTRACT.md).
/// Uuids in and out (A-3); the organization is always the caller's. Manual entries only are created, edited and deleted
/// here; any Draft (also an A44 draft of a source document, R-CORE-1) can be posted. System writers (gateway, reversal,
/// year-end close) use <see cref="JournalEntryWriter"/> directly.
/// </summary>
public interface IJournalEntryService
{
    /// <summary>A manual entry, Draft (or Posted with PostImmediately / an auto-post journal type). GL_JOURNAL_CREATE (+ GL_JOURNAL_POST to post).</summary>
    Task<JournalEntryDetailModel> CreateAsync(AccountingCaller caller, CreateJournalEntryRequest request, CancellationToken ct = default);

    /// <summary>§9.5 — Draft → Posted, balances move. GL_JOURNAL_POST.</summary>
    Task<JournalEntryDetailModel> PostAsync(AccountingCaller caller, Guid entryUuid, CancellationToken ct = default);

    /// <summary>Create + post in one transaction (CreateAsync with PostImmediately). GL_JOURNAL_CREATE + GL_JOURNAL_POST.</summary>
    Task<JournalEntryDetailModel> CreateAndPostAsync(AccountingCaller caller, CreateJournalEntryRequest request, CancellationToken ct = default);

    /// <summary>Replaces a manual Draft's header and lines (BR-JE-008). GL_JOURNAL_EDIT.</summary>
    Task<JournalEntryDetailModel> UpdateDraftAsync(AccountingCaller caller, Guid entryUuid, UpdateJournalEntryRequest request, CancellationToken ct = default);

    /// <summary>Deletes a manual Draft (its number is not reused, TS-SQ-005). GL_JOURNAL_DELETE.</summary>
    Task DeleteDraftAsync(AccountingCaller caller, Guid entryUuid, CancellationToken ct = default);

    /// <summary>One entry with all its lines. GL_VIEW (or any journal code).</summary>
    Task<JournalEntryDetailModel> GetByIdAsync(AccountingCaller caller, Guid entryUuid, CancellationToken ct = default);

    /// <summary>Filtered, sorted, paged list. GL_VIEW (or any journal code).</summary>
    Task<PaginatedResponse<JournalEntryListItemModel>> ListAsync(AccountingCaller caller, JournalEntryQuery query, CancellationToken ct = default);

    /// <summary>
    /// A47 C7 (§10, D-14) — reverses a Posted entry with a mirror entry, auto-posted, key reversal:{uuid}; the original becomes
    /// Reversed; same-account lines are auto-reconciled. GL_JOURNAL_REVERSE. <b>Implemented by RR</b>
    /// (<see cref="IJournalReversalService"/>, contract §3); this delegates to it.
    /// </summary>
    Task<JournalReversalResultModel> ReverseAsync(AccountingCaller caller, Guid entryUuid, ReverseJournalEntryRequest request, CancellationToken ct = default);
}

internal sealed class JournalEntryService : IJournalEntryService
{
    internal const int MaxPageSize = 200;
    internal const string NoPostPermission = "You do not have permission to post journal entries.";

    private readonly AccountingDbContext _db;
    private readonly JournalEntryWriter _writer;
    private readonly JournalTypeSeeder _seeder;
    private readonly IAccountingEventPublisher _events;
    private readonly IJournalReversalService? _reversal;
    private readonly JournalReconciler? _reconciler;

    public JournalEntryService(AccountingDbContext db, JournalEntryWriter writer, JournalTypeSeeder seeder, IAccountingEventPublisher events,
        IJournalReversalService? reversal = null, JournalReconciler? reconciler = null)
    {
        _db = db;
        _writer = writer;
        _seeder = seeder;
        _events = events;
        _reversal = reversal;
        _reconciler = reconciler;
    }

    // ── writes ─────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<JournalEntryDetailModel> CreateAsync(AccountingCaller caller, CreateJournalEntryRequest request, CancellationToken ct = default)
    {
        caller.Require(caller.CanCreateJournal);
        ArgumentNullException.ThrowIfNull(request);
        var org = caller.OrganizationId;
        await _seeder.EnsureAsync(org, ct);

        var type = await _db.JournalTypes.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(t => t.OrganizationId == org && t.Uuid == request.JournalTypeUuid, ct)
            ?? throw new JournalValidationException(JournalErrorCodes.JournalTypeInvalid, JournalMessages.JournalTypeNotFound);
        var post = request.PostImmediately || type.AutoPost;
        if (post) caller.Require(caller.CanPostJournal, NoPostPermission);

        var key = JournalKeys.Manual(request.ClientRequestId ?? Guid.NewGuid());
        var draft = new JournalDraft
        {
            OrganizationId = org,
            JournalTypeId  = type.Id,
            EntryDate      = request.EntryDate,
            DocumentDate   = request.DocumentDate,
            Reference      = request.Reference,
            Description    = request.Description,
            EntryType      = JournalEntryTypes.ManualJournal,
            IdempotencyKey = key,
            CurrencyCode   = request.CurrencyCode,
            ExchangeRate   = request.ExchangeRate,
            IsOpeningEntry = request.IsOpeningEntry,
            IsClosingEntry = request.IsClosingEntry,
            IsManual       = true,
            UserId         = caller.UserIdOrNull,
            UserName       = caller.UserName,
            Lines          = await LinesAsync(org, request.Lines, ct),
        };

        JournalWriteResult? result = null;
        try
        {
            await _db.InTransactionAsync(async () =>
            {
                result = await _writer.CreateAsync(draft, post, ct);
                if (result.Created) await _db.SaveChangesAsync(ct);
            }, ct);
        }
        catch (DbUpdateException) when (request.ClientRequestId is not null)
        {
            // BR-JE-007 — the same request raced itself: the unique key let one through; answer with it.
            _db.ChangeTracker.Clear();
            var winner = await _writer.FindByKeyAsync(org, key, ct) ?? throw new ConflictException("The journal entry could not be saved. Please try again.");
            return await DetailAsync(caller, winner.Id, ct);
        }

        var entry = result!.Entry;
        if (result.Created && entry.Status == JournalStatuses.Posted) await PublishPostedAsync(caller, entry, type.Code, ct);
        return await DetailAsync(caller, entry.Id, ct);
    }

    public Task<JournalEntryDetailModel> CreateAndPostAsync(AccountingCaller caller, CreateJournalEntryRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.PostImmediately = true;
        return CreateAsync(caller, request, ct);
    }

    public async Task<JournalEntryDetailModel> PostAsync(AccountingCaller caller, Guid entryUuid, CancellationToken ct = default)
    {
        caller.Require(caller.CanPostJournal, NoPostPermission);
        var entry = await LoadTrackedAsync(caller.OrganizationId, entryUuid, ct);
        await _db.InTransactionAsync(async () =>
        {
            await _writer.PostAsync(entry, caller.UserIdOrNull, caller.UserName, ct);
            await CompleteReversalAsync(caller, entry, ct);
            await _db.SaveChangesAsync(ct);
        }, ct);
        var code = await _db.JournalTypes.IgnoreQueryFilters().AsNoTracking().Where(t => t.Id == entry.JournalTypeId).Select(t => t.Code).FirstAsync(ct);
        await PublishPostedAsync(caller, entry, code, ct);
        return await DetailAsync(caller, entry.Id, ct);
    }

    public async Task<JournalEntryDetailModel> UpdateDraftAsync(AccountingCaller caller, Guid entryUuid, UpdateJournalEntryRequest request,
        CancellationToken ct = default)
    {
        caller.Require(caller.CanEditJournal);
        ArgumentNullException.ThrowIfNull(request);
        var org = caller.OrganizationId;
        var entry = await LoadTrackedAsync(org, entryUuid, ct);
        if (entry.Status != JournalStatuses.Draft)
            throw new JournalConflictException(JournalErrorCodes.NotDraft,
                entry.Status == JournalStatuses.Reversed ? JournalMessages.AlreadyReversed : JournalMessages.PostedImmutable);
        if (!IsManual(entry)) throw new JournalConflictException(JournalErrorCodes.NotDraft, JournalMessages.SourceDraft);

        var draft = new JournalDraft
        {
            EntryDate      = request.EntryDate,
            DocumentDate   = request.DocumentDate,
            Reference      = request.Reference,
            Description    = request.Description,
            EntryType      = entry.EntryType,
            IdempotencyKey = entry.IdempotencyKey!,
            CurrencyCode   = request.CurrencyCode,
            ExchangeRate   = request.ExchangeRate,
            IsOpeningEntry = request.IsOpeningEntry,
            IsClosingEntry = request.IsClosingEntry,
            IsManual       = true,
            UserId         = caller.UserIdOrNull,
            UserName       = caller.UserName,
            Lines          = await LinesAsync(org, request.Lines, ct),
        };
        await _db.InTransactionAsync(async () =>
        {
            await _writer.UpdateDraftAsync(entry, draft, ct);
            await _db.SaveChangesAsync(ct);
        }, ct);
        return await DetailAsync(caller, entry.Id, ct);
    }

    public async Task DeleteDraftAsync(AccountingCaller caller, Guid entryUuid, CancellationToken ct = default)
    {
        caller.Require(caller.CanDeleteJournal);
        var entry = await LoadTrackedAsync(caller.OrganizationId, entryUuid, ct);
        if (entry.Status != JournalStatuses.Draft)
            throw new JournalConflictException(JournalErrorCodes.NotDraft, JournalMessages.PostedNotDeletable);
        if (!IsManual(entry)) throw new JournalConflictException(JournalErrorCodes.NotDraft, JournalMessages.SourceDraft);
        await _db.InTransactionAsync(async () =>
        {
            await _writer.DeleteDraftAsync(entry, ct);
            await _db.SaveChangesAsync(ct);
        }, ct);
    }

    public Task<JournalReversalResultModel> ReverseAsync(AccountingCaller caller, Guid entryUuid, ReverseJournalEntryRequest request, CancellationToken ct = default) =>
        _reversal?.ReverseAsync(caller, entryUuid, request, ct)
        ?? throw new NotImplementedException("A47 C7 — the reversal is implemented by RR (docs/journal-engine/API-CONTRACT.md §3).");

    // ── reads ──────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<JournalEntryDetailModel> GetByIdAsync(AccountingCaller caller, Guid entryUuid, CancellationToken ct = default)
    {
        caller.Require(caller.CanReadJournals);
        var id = await _db.JournalEntries.IgnoreQueryFilters().AsNoTracking()
            .Where(j => j.OrganizationId == caller.OrganizationId && j.Uuid == entryUuid).Select(j => (int?)j.Id).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException(JournalMessages.EntryNotFound + ".");
        return await DetailAsync(caller, id, ct);
    }

    public async Task<PaginatedResponse<JournalEntryListItemModel>> ListAsync(AccountingCaller caller, JournalEntryQuery query, CancellationToken ct = default)
    {
        caller.Require(caller.CanReadJournals);
        query ??= new JournalEntryQuery();
        var org = caller.OrganizationId;
        var rows = _db.JournalEntries.IgnoreQueryFilters().AsNoTracking().Where(j => j.OrganizationId == org);

        if (query.JournalTypeUuid is { } typeUuid) rows = rows.Where(j => j.JournalType.Uuid == typeUuid);
        if (query.FiscalYearUuid is { } yearUuid) rows = rows.Where(j => j.FiscalYear != null && j.FiscalYear.Uuid == yearUuid);
        if (query.FiscalPeriodUuid is { } periodUuid) rows = rows.Where(j => j.FiscalPeriod != null && j.FiscalPeriod.Uuid == periodUuid);
        if (!string.IsNullOrWhiteSpace(query.Status))
        {
            var status = JournalStatuses.All.FirstOrDefault(s => string.Equals(s, query.Status.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new BadRequestException("Status must be Draft, Posted or Reversed.");
            rows = rows.Where(j => j.Status == status);
        }
        if (query.FromDate is { } from) rows = rows.Where(j => j.EntryDate >= from);
        if (query.ToDate is { } to) rows = rows.Where(j => j.EntryDate <= to);
        if (query.FromDate is { } f && query.ToDate is { } t && t < f) throw new BadRequestException("The to date must not be before the from date.");
        if (query.AccountUuid is { } accountUuid) rows = rows.Where(j => j.Lines.Any(l => l.Account.Uuid == accountUuid));
        if (query.PartnerUuid is { } partnerUuid) rows = rows.Where(j => j.Lines.Any(l => l.PartnerUuid == partnerUuid));
        if (!string.IsNullOrWhiteSpace(query.SourceModule))
        {
            var module = query.SourceModule.Trim().ToLowerInvariant();
            rows = module switch
            {
                "manual"  => rows.Where(j => j.IdempotencyKey != null && j.IdempotencyKey.StartsWith(JournalKeys.ManualPrefix)),
                "opening" => rows.Where(j => j.EntryType == JournalEntryTypes.OpeningBalance),
                _         => rows.Where(j => j.SourceModule == module),
            };
        }
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var s = query.Search.Trim();
            rows = rows.Where(j => j.EntryNumber.Contains(s) || (j.Reference != null && j.Reference.Contains(s)) || (j.Description != null && j.Description.Contains(s)));
        }

        var asc = string.Equals(query.SortDirection, "asc", StringComparison.OrdinalIgnoreCase);
        rows = (query.SortBy?.Trim().ToLowerInvariant()) switch
        {
            "entrynumber" => asc ? rows.OrderBy(j => j.EntryNumber) : rows.OrderByDescending(j => j.EntryNumber),
            "totaldebit"  => asc ? rows.OrderBy(j => j.TotalDebit).ThenBy(j => j.Id) : rows.OrderByDescending(j => j.TotalDebit).ThenByDescending(j => j.Id),
            "createdat"   => asc ? rows.OrderBy(j => j.CreatedAt).ThenBy(j => j.Id) : rows.OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.Id),
            "postedat"    => asc ? rows.OrderBy(j => j.PostedAt).ThenBy(j => j.Id) : rows.OrderByDescending(j => j.PostedAt).ThenByDescending(j => j.Id),
            null or "" or "entrydate" => asc ? rows.OrderBy(j => j.EntryDate).ThenBy(j => j.Id) : rows.OrderByDescending(j => j.EntryDate).ThenByDescending(j => j.Id),
            _ => throw new BadRequestException("Sort by entryDate, entryNumber, totalDebit, createdAt or postedAt."),
        };

        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize <= 0 ? 25 : query.PageSize, 1, MaxPageSize);
        var total = await rows.CountAsync(ct);
        var data = await rows.Skip((page - 1) * size).Take(size)
            .Select(j => new JournalEntryListItemModel
            {
                Uuid           = j.Uuid,
                EntryNumber    = j.EntryNumber,
                JournalType    = new JournalTypeRefModel { Uuid = j.JournalType.Uuid, Code = j.JournalType.Code, Name = j.JournalType.Name, Type = j.JournalType.Type },
                EntryDate      = j.EntryDate,
                Reference      = j.Reference,
                Description    = j.Description,
                EntryType      = j.EntryType,
                SourceModule   = j.SourceModule,
                Status         = j.Status,
                TotalDebit     = j.TotalDebit,
                TotalCredit    = j.TotalCredit,
                CurrencyCode   = j.CurrencyCode,
                LineCount      = j.LineCount,
                IsManual       = j.IdempotencyKey != null && j.IdempotencyKey.StartsWith(JournalKeys.ManualPrefix),
                IsOpeningEntry = j.IsOpeningEntry,
                IsClosingEntry = j.IsClosingEntry,
                IsReversal     = j.ReversesEntryId != null,
                CreatedAt      = j.CreatedAt,
                PostedAt       = j.PostedAt,
            })
            .ToListAsync(ct);
        return new PaginatedResponse<JournalEntryListItemModel>
        {
            Data = data, TotalRecords = total, Page = page, PageSize = size, TotalPages = (int)Math.Ceiling(total / (double)size),
        };
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A47 REV-10 — a Draft that reverses another entry (an A44 update reversal written while AutoPostToGL was off) only
    /// cancels its original once it is posted: then the original becomes Reversed with ReversedByEntryId, and its lines are
    /// auto-reconciled with the mirror (BR-RV-008, <see cref="JournalReconciler.ReconcileReversalAsync"/>) when the mirror
    /// matches the original line by line. Same transaction as the post; not saved.
    /// </summary>
    private async Task CompleteReversalAsync(AccountingCaller caller, JournalEntry posted, CancellationToken ct)
    {
        if (posted.ReversesEntryId is not { } originalId) return;
        var org = posted.OrganizationId;
        var original = await _db.JournalEntries.IgnoreQueryFilters().Include(j => j.Lines)
            .FirstOrDefaultAsync(j => j.OrganizationId == org && j.Id == originalId, ct);
        if (original is not { Status: JournalStatuses.Posted, ReversedByEntryId: null }) return;
        original.Status = JournalStatuses.Reversed;
        original.ReversedByEntryId = posted.Id;
        original.ModifiedBy = caller.UserIdOrNull;
        original.ModifiedByName = caller.UserName;

        if (_reconciler is null) return;
        var lines = original.Lines.OrderBy(l => l.LineNumber).ToList();
        var mirror = posted.Lines.OrderBy(l => l.LineNumber).ToList();
        var matches = lines.Count == mirror.Count
            && mirror.Select((m, k) => m.LineNumber == k + 1 && m.AccountId == lines[k].AccountId
                                       && m.DebitAmount == lines[k].CreditAmount && m.CreditAmount == lines[k].DebitAmount).All(x => x);
        if (!matches) return; // not a line-by-line mirror: the lines stay open for a manual reconciliation
        await _reconciler.LockAccountsAsync(org, lines.Select(l => l.AccountId).Distinct().OrderBy(id => id), ct);
        await _reconciler.ReconcileReversalAsync(org, original, lines, posted, posted.EntryDate, caller.UserIdOrNull, caller.UserName, ct);
    }

    internal static bool IsManual(JournalEntry e) => e.IdempotencyKey is { } k && k.StartsWith(JournalKeys.ManualPrefix, StringComparison.Ordinal);

    private async Task<JournalEntry> LoadTrackedAsync(Guid org, Guid uuid, CancellationToken ct) =>
        await _db.JournalEntries.IgnoreQueryFilters().Include(j => j.Lines)
            .FirstOrDefaultAsync(j => j.OrganizationId == org && j.Uuid == uuid, ct)
        ?? throw new NotFoundException(JournalMessages.EntryNotFound + ".");

    /// <summary>Request lines → engine lines (account uuids of this organization → ids; an unknown uuid is refused per line).</summary>
    private async Task<List<JournalDraftLine>> LinesAsync(Guid org, List<JournalEntryLineRequest>? lines, CancellationToken ct)
    {
        lines ??= [];
        var uuids = lines.Select(l => l.AccountUuid).Distinct().ToList();
        var ids = await _db.ChartOfAccounts.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.OrganizationId == org && uuids.Contains(a.Uuid)).ToDictionaryAsync(a => a.Uuid, a => a.Id, ct);
        var missing = lines.Select((l, i) => (l, i)).Where(x => !ids.ContainsKey(x.l.AccountUuid))
            .Select(x => new JournalError(JournalErrorCodes.AccountNotPostable, JournalMessages.AccountNotFound, x.i + 1)).ToList();
        if (missing.Count > 0) throw new JournalValidationException(missing);
        return lines.Select(l => new JournalDraftLine
        {
            AccountId    = ids[l.AccountUuid],
            Debit        = l.Debit,
            Credit       = l.Credit,
            Description  = l.Description,
            PartnerUuid  = l.PartnerUuid,
            PartnerType  = l.PartnerType,
            CurrencyCode = l.CurrencyCode,
            ExchangeRate = l.ExchangeRate,
            CostCenter   = l.CostCenter,
            Project      = l.Project,
            TaxCode      = l.TaxCode,
            TaxAmount    = l.TaxAmount,
        }).ToList();
    }

    private Task PublishPostedAsync(AccountingCaller caller, JournalEntry entry, string typeCode, CancellationToken ct) =>
        _events.PublishAsync(new JournalEntryPostedEvent(caller.OrganizationId, entry.Uuid, entry.EntryNumber, typeCode, entry.TotalDebit,
            entry.EntryDate, caller.UserIdOrNull, caller.UserName), ct);

    /// <summary>The detail of one entry of the caller's organization (with what the caller may do with it).</summary>
    internal async Task<JournalEntryDetailModel> DetailAsync(AccountingCaller caller, int id, CancellationToken ct)
    {
        var j = await _db.JournalEntries.IgnoreQueryFilters().AsNoTracking().AsSplitQuery()
            .Include(x => x.JournalType).Include(x => x.FiscalYear).Include(x => x.FiscalPeriod)
            .Include(x => x.ReversesEntry).Include(x => x.ReversedByEntry)
            .Include(x => x.Lines).ThenInclude(l => l.Account)
            .Include(x => x.Lines).ThenInclude(l => l.ReconcileGroup)
            .Include(x => x.Lines).ThenInclude(l => l.ClearingEntry)
            .FirstOrDefaultAsync(x => x.OrganizationId == caller.OrganizationId && x.Id == id, ct)
            ?? throw new NotFoundException(JournalMessages.EntryNotFound + ".");
        return ToDetail(j, caller);
    }

    internal static JournalEntryDetailModel ToDetail(JournalEntry j, AccountingCaller caller)
    {
        var manual = IsManual(j);
        var draft = j.Status == JournalStatuses.Draft;
        return new JournalEntryDetailModel
        {
            Uuid               = j.Uuid,
            EntryNumber        = j.EntryNumber,
            JournalType        = new JournalTypeRefModel { Uuid = j.JournalType.Uuid, Code = j.JournalType.Code, Name = j.JournalType.Name, Type = j.JournalType.Type },
            EntryDate          = j.EntryDate,
            DocumentDate       = j.DocumentDate,
            FiscalYear         = j.FiscalYear is null ? null : new FiscalYearRefModel { Uuid = j.FiscalYear.Uuid, FiscalYearName = j.FiscalYear.FiscalYearName },
            FiscalPeriod       = j.FiscalPeriod is null ? null : new FiscalPeriodRefModel
                { Uuid = j.FiscalPeriod.Uuid, PeriodName = j.FiscalPeriod.PeriodName, PeriodNumber = j.FiscalPeriod.PeriodNumber, Status = j.FiscalPeriod.Status },
            Reference          = j.Reference,
            Description        = j.Description,
            EntryType          = j.EntryType,
            SourceModule       = j.SourceModule,
            SourceDocumentUuid = j.SourceDocumentUuid,
            IdempotencyKey     = j.IdempotencyKey,
            CurrencyCode       = j.CurrencyCode,
            ExchangeRate       = j.ExchangeRate,
            Status             = j.Status,
            IsManual           = manual,
            IsOpeningEntry     = j.IsOpeningEntry,
            IsClosingEntry     = j.IsClosingEntry,
            TotalDebit         = j.TotalDebit,
            TotalCredit        = j.TotalCredit,
            LineCount          = j.LineCount == 0 ? j.Lines.Count : j.LineCount,
            ReversesEntry      = j.ReversesEntry is null ? null : new JournalEntryRefModel { Uuid = j.ReversesEntry.Uuid, EntryNumber = j.ReversesEntry.EntryNumber },
            ReversedByEntry    = j.ReversedByEntry is null ? null : new JournalEntryRefModel { Uuid = j.ReversedByEntry.Uuid, EntryNumber = j.ReversedByEntry.EntryNumber },
            ReversalReason     = j.ReversalReason,
            CreatedAt          = j.CreatedAt,
            CreatedByName      = j.CreatedByName,
            PostedAt           = j.PostedAt,
            PostedByName       = j.PostedByName,
            ModifiedAt         = j.ModifiedAt,
            ModifiedByName     = j.ModifiedByName,
            CanEdit            = draft && manual && caller.CanEditJournal,
            CanDelete          = draft && manual && caller.CanDeleteJournal,
            CanPost            = draft && caller.CanPostJournal,
            CanReverse         = j.Status == JournalStatuses.Posted && j.ReversesEntryId is null && caller.CanReverseJournal
                                 && j.EntryType is not (JournalEntryTypes.OpeningBalance or JournalEntryTypes.YearEndClose), // REV-7 — what the reversal refuses
            Lines = j.Lines.OrderBy(l => l.LineNumber).Select(l => new JournalEntryLineModel
            {
                LineUuid             = l.Uuid,
                LineNumber           = l.LineNumber,
                Account              = new AccountRefModel { Uuid = l.Account.Uuid, AccountCode = l.Account.AccountCode, AccountName = l.Account.AccountName, RootType = l.Account.RootType },
                Description          = l.Memo,
                Debit                = l.DebitAmount,
                Credit               = l.CreditAmount,
                CurrencyCode         = l.CurrencyCode,
                AmountCurrency       = l.AmountCurrency,
                ExchangeRate         = l.ExchangeRate,
                PartnerUuid          = l.PartnerUuid,
                PartnerType          = l.PartnerType,
                PartnerName          = l.PartnerName,
                CostCenter           = l.CostCenter,
                Project              = l.Project,
                TaxCode              = l.TaxCode,
                TaxAmount            = l.TaxAmount,
                Reconciled           = l.Reconciled,
                AmountResidual       = l.AmountResidual,
                ReconcileGroupUuid   = l.ReconcileGroup?.Uuid,
                ReconcileGroupNumber = l.ReconcileGroup?.GroupNumber,
                ClearingEntry        = l.ClearingEntry is null ? null : new JournalEntryRefModel { Uuid = l.ClearingEntry.Uuid, EntryNumber = l.ClearingEntry.EntryNumber },
                ClearingDate         = l.ClearingDate,
            }).ToList(),
        };
    }
}
