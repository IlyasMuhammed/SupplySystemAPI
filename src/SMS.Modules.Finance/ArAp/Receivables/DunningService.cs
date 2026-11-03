using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SMS.Modules.Finance.ArAp.Reporting;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;
using SMS.Shared.Pagination;

namespace SMS.Modules.Finance.ArAp.Receivables;

// ── Models (API-CONTRACT §3) ────────────────────────────────────────────────────────────────────────────────────────

public sealed class DunningLevelModel
{
    public Guid     Uuid               { get; set; }
    public int      LevelNumber        { get; set; }
    public int      DaysAfterPrevious  { get; set; }
    public string   SubjectTemplate    { get; set; } = string.Empty;
    public string   BodyTemplate       { get; set; } = string.Empty;
    public bool     IncludeInvoiceList { get; set; } = true;
    public bool     IncludeStatement   { get; set; }
    public decimal? ChargeAmount       { get; set; }
    public string   ActionType         { get; set; } = "Email";
    public string?  EscalationNote     { get; set; }
}

public sealed class DunningProcedureModel
{
    public Guid     Uuid              { get; set; }
    public string   Code              { get; set; } = string.Empty;
    public string   Name              { get; set; } = string.Empty;
    public string?  Description       { get; set; }
    public bool     IsDefault         { get; set; }
    public bool     IsActive          { get; set; } = true;
    public int      GraceDays         { get; set; }
    public decimal  MinimumAmount     { get; set; }
    public int?     BlockSalesAtLevel { get; set; }
    public List<DunningLevelModel> Levels { get; set; } = [];
}

public sealed class DunningPreviewRequest
{
    public DateOnly? AsOfDate { get; set; }
}

public sealed class DunningRunItemModel
{
    public Guid      Uuid                 { get; set; }
    public Guid      CustomerUuid         { get; set; }
    public string    CustomerName         { get; set; } = string.Empty;
    public string?   CustomerCode         { get; set; }
    public string?   ProcedureCode        { get; set; }
    public int       DunningLevel         { get; set; }
    public int       PreviousLevel        { get; set; }
    public decimal   OverdueAmount        { get; set; }
    public string?   CurrencySummary      { get; set; }
    public int       OpenItemCount        { get; set; }
    public DateOnly  OldestDueDate        { get; set; }
    public int       DaysOverdue          { get; set; }
    public string    ActionType           { get; set; } = string.Empty;
    public decimal?  ChargeAmount         { get; set; }
    public Guid?     ChargeDocumentUuid   { get; set; }
    public string?   ChargeDocumentNumber { get; set; }
    public string?   SentTo               { get; set; }
    public DateTime? SentAt               { get; set; }
    public string?   Subject              { get; set; }
    public string?   Body                 { get; set; }
    public List<Guid> InvoiceUuids        { get; set; } = [];
    public string    Status               { get; set; } = string.Empty;
    public string?   SkipReason           { get; set; }
    public string?   FailureReason        { get; set; }
}

public sealed class DunningRunModel
{
    public Guid      Uuid           { get; set; }
    public string    RunNumber      { get; set; } = string.Empty;
    public DateOnly  RunDate        { get; set; }
    public DateOnly  AsOfDate       { get; set; }
    public string    Status         { get; set; } = string.Empty;
    public string?   CurrencyCode   { get; set; }
    public int       TotalCustomers { get; set; }
    public decimal   TotalAmount    { get; set; }
    public int       ItemsProcessed { get; set; }
    public int       ItemsSent      { get; set; }
    public int       ItemsSkipped   { get; set; }
    public int       ItemsFailed    { get; set; }
    public DateTime  CreatedAt      { get; set; }
    public string?   CreatedByName  { get; set; }
    public DateTime? ExecutedAt     { get; set; }
    public string?   ExecutedByName { get; set; }
    public DateTime? CompletedAt    { get; set; }
    public DateTime? CancelledAt    { get; set; }
    public string?   CancelReason   { get; set; }
    /// <summary>Customers whose level was reset to 0 at preview because they owe nothing (BR-DN-006).</summary>
    public int       AutoResetCount { get; set; }
    public List<DunningRunItemModel> Items { get; set; } = [];
}

public sealed class DunningHistoryEvent
{
    public DateTime  At        { get; set; }
    public string    Kind      { get; set; } = string.Empty;   // Notice | Reset | Hold
    public int?      Level     { get; set; }
    public string?   RunNumber { get; set; }
    public Guid?     RunUuid   { get; set; }
    public string?   Status    { get; set; }
    public decimal?  Amount    { get; set; }
    public string?   Detail    { get; set; }
    public string?   ByName    { get; set; }
}

public sealed class DunningHistoryModel
{
    public Guid      CustomerUuid        { get; set; }
    public string    CustomerName        { get; set; } = string.Empty;
    public int       CurrentDunningLevel { get; set; }
    public DateOnly? LastDunningDate     { get; set; }
    public string?   ProcedureCode       { get; set; }
    public bool      ExcludeFromDunning  { get; set; }
    public List<DunningHistoryEvent> Events { get; set; } = [];
}

public sealed record DunningSkipBody(string? Reason);
public sealed record DunningCancelBody(string? Reason);
public sealed record DunningResetBody(string? Reason, int? Level);

/// <summary>
/// A48 AR — C5 dunning (tasks 194–243, D-10, D-11, API-CONTRACT §3).
/// <list type="bullet">
/// <item><b>Preview</b> creates the Draft run of the day (BR-DN-010: one non-cancelled run per organization per day, a filtered
/// unique index behind it): customers with invoices overdue past grace, each at the next level of its procedure
/// (<see cref="DunningRules"/>; BR-DN-001..004), with the notice rendered; customers below the minimum are listed as Skipped.
/// Customers who owe nothing are reset to level 0 first (BR-DN-006).</item>
/// <item><b>Execute</b> (Draft → Processing → Completed, never again — BR-DN-008): per item, its own commit — the charge as a
/// customer document (D-10), the notice by email through IEmailSender (or Manual for Letter/Manual levels), the profile's
/// level and date, and the automatic hold at the block-sales level (BR-DN-005). One item failing never stops the others.</item>
/// </list>
/// Amounts are the sale base (D-13); the "day" is the UTC date (D-AR-3).
/// </summary>
internal sealed class DunningService
{
    internal const string RunPrefix = "DUN";

    private readonly FinanceDbContext                _db;
    private readonly IPartnerFinancialProfileService _profiles;
    private readonly ArApDefaultsSeeder              _seeder;
    private readonly CreditControlService            _credit;
    private readonly DunningChargeWriter             _charges;
    private readonly IDunningTemplateEngine          _templates;
    private readonly ILogger<DunningService>         _log;
    private readonly IPartnerLookupService?          _partners;
    private readonly IEmailSender?                   _email;
    private readonly IStatementService?              _statements;
    private readonly IDocumentNumberGenerator?       _numbers;
    private readonly TimeProvider                    _clock;

    public DunningService(
        FinanceDbContext db, IPartnerFinancialProfileService profiles, ArApDefaultsSeeder seeder, CreditControlService credit,
        DunningChargeWriter charges, IDunningTemplateEngine templates, ILogger<DunningService> log,
        IPartnerLookupService? partners = null, IEmailSender? email = null, IStatementService? statements = null,
        IDocumentNumberGenerator? numbers = null, TimeProvider? clock = null)
    {
        _db         = db;
        _profiles   = profiles;
        _seeder     = seeder;
        _credit     = credit;
        _charges    = charges;
        _templates  = templates;
        _log        = log;
        _partners   = partners;
        _email      = email;
        _statements = statements;
        _numbers    = numbers;
        _clock      = clock ?? TimeProvider.System;
    }

    private DateOnly Today => DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

    // ══ Procedures (tasks 195–197) ═════════════════════════════════════════════════════════════════════════════════

    public async Task<List<DunningProcedureModel>> ListProceduresAsync(ArApCaller caller, CancellationToken ct = default)
    {
        var org = Org(caller);
        await EnsureDefaultsAsync(org, ct);
        var rows = await Procedures(org).AsNoTracking().Include(p => p.Levels)
            .OrderByDescending(p => p.IsDefault).ThenBy(p => p.Code).ToListAsync(ct);
        return rows.Select(ToModel).ToList();
    }

    public async Task<DunningProcedureModel> GetProcedureAsync(Guid uuid, ArApCaller caller, CancellationToken ct = default)
    {
        var org = Org(caller);
        var p = await Procedures(org).AsNoTracking().Include(x => x.Levels).FirstOrDefaultAsync(x => x.Uuid == uuid, ct)
            ?? throw new NotFoundException("Dunning procedure", uuid);
        return ToModel(p);
    }

    public async Task<DunningProcedureModel> CreateProcedureAsync(DunningProcedureModel request, ArApCaller caller, CancellationToken ct = default)
    {
        var org = Org(caller);
        await EnsureDefaultsAsync(org, ct);
        var code = ValidateProcedure(request);
        if (await Procedures(org).AnyAsync(p => p.Code == code, ct))
            throw new ConflictException($"A dunning procedure with code {code} already exists.");

        var now = _clock.GetUtcNow().UtcDateTime;
        var p = new DunningProcedure
        {
            OrganizationId = org, Code = code, CreatedAt = now, CreatedBy = caller.UserIdOrNull, CreatedByName = caller.UserName
        };
        Fill(p, request, org);
        if (p.IsDefault) await ClearOtherDefaultsAsync(org, null, ct);
        _db.Set<DunningProcedure>().Add(p);
        await SaveAsync(ct, $"A dunning procedure with code {code} already exists.");
        return await GetProcedureAsync(p.Uuid, caller, ct);
    }

    public async Task<DunningProcedureModel> UpdateProcedureAsync(Guid uuid, DunningProcedureModel request, ArApCaller caller, CancellationToken ct = default)
    {
        var org = Org(caller);
        var p = await Procedures(org).Include(x => x.Levels).FirstOrDefaultAsync(x => x.Uuid == uuid, ct)
            ?? throw new NotFoundException("Dunning procedure", uuid);
        var code = ValidateProcedure(request);
        if (code != p.Code && await Procedures(org).AnyAsync(x => x.Code == code && x.Id != p.Id, ct))
            throw new ConflictException($"A dunning procedure with code {code} already exists.");
        if (p.IsDefault && !request.IsDefault)
            throw new ConflictException("The default dunning procedure cannot stop being the default; make another procedure the default first.");
        if (p.IsDefault && !request.IsActive)
            throw new ConflictException("The default dunning procedure cannot be deactivated; make another procedure the default first.");

        p.Code = code;
        _db.Set<DunningLevel>().RemoveRange(p.Levels);
        p.Levels.Clear();
        Fill(p, request, org);
        p.ModifiedAt     = _clock.GetUtcNow().UtcDateTime;
        p.ModifiedBy     = caller.UserIdOrNull;
        p.ModifiedByName = caller.UserName;
        if (p.IsDefault) await ClearOtherDefaultsAsync(org, p.Id, ct);
        await SaveAsync(ct, $"A dunning procedure with code {code} already exists.");
        return await GetProcedureAsync(uuid, caller, ct);
    }

    // ══ Runs (tasks 198–219) ═══════════════════════════════════════════════════════════════════════════════════════

    public async Task<DunningRunModel> PreviewAsync(DunningPreviewRequest request, ArApCaller caller, CancellationToken ct = default)
    {
        var org   = Org(caller);
        var today = Today;
        var asOf  = request?.AsOfDate ?? today;
        if (asOf > today) throw new BadRequestException("The as-of date cannot be in the future.");

        await RefuseSecondRunAsync(org, today, ct);
        await EnsureDefaultsAsync(org, ct);

        var procedures = await Procedures(org).AsNoTracking().Include(p => p.Levels).Where(p => p.IsActive).ToListAsync(ct);
        var fallback = procedures.FirstOrDefault(p => p.IsDefault)
            ?? throw new ConflictException("The organization has no active default dunning procedure.");

        var open = await OpenItemsAsync(org, null, ct);
        var resets = await AutoResetAsync(org, open.Keys.ToHashSet(), caller, ct);

        var customers = open.Keys.ToList();
        var profiles = await _db.Set<PartnerFinancialProfile>().IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.OrganizationId == org && p.PartnerRole == ArApEnums.PartnerRole.Customer && customers.Contains(p.PartnerUuid))
            .ToDictionaryAsync(p => p.PartnerUuid, ct);
        var partners = _partners is null || customers.Count == 0
            ? new Dictionary<Guid, PartnerInfo>()
            : new Dictionary<Guid, PartnerInfo>(await _partners.GetManyAsync(org, customers, null, ct));
        var baseCode = await _credit.BaseCodeAsync(org, ct);

        var now = _clock.GetUtcNow().UtcDateTime;
        var run = new DunningRun
        {
            OrganizationId = org,
            RunNumber      = await RunNumberAsync(org, now, ct),
            RunDate        = today,
            AsOfDate       = asOf,
            Status         = ArApEnums.DunningRunStatus.Draft,
            CurrencyCode   = baseCode,
            CreatedAt      = now,
            CreatedBy      = caller.UserIdOrNull,
            CreatedByName  = caller.UserName
        };

        foreach (var (customer, rows) in open.OrderBy(o => o.Value[0].InvoiceNumber))
        {
            var items = rows.Select(r => r.Item).ToList();
            var profile = profiles.GetValueOrDefault(customer);
            var procedure = profile?.DunningProcedureId is { } pid ? procedures.FirstOrDefault(p => p.Id == pid) ?? fallback : fallback;
            var eval = DunningRules.Evaluate(Rule(procedure), profile?.CurrentDunningLevel ?? 0,
                profile?.LastDunningDate?.ToDateTime(TimeOnly.MinValue), items, asOf.ToDateTime(TimeOnly.MinValue));
            if (eval.OverdueItems.Count == 0) continue;

            var partner = partners.GetValueOrDefault(customer);
            string? skip = null;
            if (profile?.ExcludeFromDunning == true) skip = "The customer is excluded from dunning.";
            else if (!eval.Eligible)
            {
                // Only the minimum-amount refusal is listed (TS-DN-005); a customer simply not due for the next level is not.
                if (eval.SkipReason is null || !eval.SkipReason.Contains("minimum", StringComparison.Ordinal)) continue;
                skip = eval.SkipReason;
            }

            var level = procedure.Levels.FirstOrDefault(l => l.LevelNumber == (skip is null ? eval.ProposedLevel : eval.CurrentLevel + 1));
            var item = new DunningRunItem
            {
                OrganizationId     = org,
                PartnerUuid        = customer,
                PartnerName        = CreditControlService.Truncate(partner?.Name ?? rows[0].CustomerName ?? "(unknown customer)", 200),
                PartnerCode        = partner?.Code,
                DunningProcedureId = procedure.Id,
                DunningLevel       = skip is null ? eval.ProposedLevel : eval.CurrentLevel,
                PreviousLevel      = eval.CurrentLevel,
                OverdueAmount      = eval.OverdueBase,
                CurrencySummary    = CurrencySummary(eval.OverdueItems),
                OpenItemCount      = eval.OverdueItems.Count,
                OldestDueDate      = DateOnly.FromDateTime(eval.OldestDueDate!.Value),
                DaysOverdue        = eval.DaysOverdue,
                ActionType         = level?.ActionType ?? ArApEnums.DunningActionType.Email,
                ChargeAmount       = skip is null && level?.ChargeAmount is > 0m ? level.ChargeAmount : null,
                SentTo             = partner?.Email,
                InvoiceUuids       = JsonSerializer.Serialize(eval.OverdueItems.Select(i => i.InvoiceUuid)),
                Status             = skip is null ? ArApEnums.DunningRunItemStatus.Pending : ArApEnums.DunningRunItemStatus.Skipped,
                SkipReason         = skip
            };
            if (skip is null && level is not null) Render(item, level, eval.OverdueItems, baseCode);
            run.Items.Add(item);
        }

        Totals(run);
        _db.Set<DunningRun>().Add(run);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            await RefuseSecondRunAsync(org, today, ct);
            // REV-15 — any other unique clash (the run number) is a 409 too, never a raw 500.
            throw new ConflictException("Another dunning run was being created at the same moment; try again.");
        }

        var model = await GetRunAsync(run.Uuid, caller, ct);
        model.AutoResetCount = resets;
        return model;
    }

    /// <summary>REV-10 — a run left Processing this long (a request that died mid-run) can be resumed or cancelled.</summary>
    internal static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(15);

    public async Task<DunningRunModel> SkipItemAsync(Guid runUuid, Guid itemUuid, string? reason, ArApCaller caller, CancellationToken ct = default)
    {
        var why = CreditControlService.RequireReason(reason);
        var org = Org(caller);
        var run = await LoadRunAsync(org, runUuid, ct);
        RequireDraft(run);
        var item = run.Items.FirstOrDefault(i => i.Uuid == itemUuid) ?? throw new NotFoundException("Dunning run item", itemUuid);
        if (item.Status != ArApEnums.DunningRunItemStatus.Pending)
            throw new ConflictException($"The item for {item.PartnerName} is {item.Status}; only a pending item can be skipped.");

        var skip = CreditControlService.Truncate($"{why} ({caller.UserName ?? "user " + caller.UserId})", 200);
        if (_db.Database.IsRelational())
        {
            // REV-12 — conditional: only a pending item of a run that is still Draft (an execute may have claimed it since).
            var runId = run.Id;
            var changed = await _db.Set<DunningRunItem>().IgnoreQueryFilters()
                .Where(i => i.Id == item.Id && i.Status == ArApEnums.DunningRunItemStatus.Pending
                         && _db.Set<DunningRun>().IgnoreQueryFilters().Any(r => r.Id == runId && r.Status == ArApEnums.DunningRunStatus.Draft))
                .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, ArApEnums.DunningRunItemStatus.Skipped)
                                          .SetProperty(i => i.SkipReason, skip), ct);
            if (changed == 0) throw new ConflictException($"Dunning run {run.RunNumber} changed meanwhile; reload it.");
            _db.ChangeTracker.Clear();
            await RecountAsync(org, runUuid, onlyIfStatus: ArApEnums.DunningRunStatus.Draft, ct);
        }
        else
        {
            item.Status     = ArApEnums.DunningRunItemStatus.Skipped;
            item.SkipReason = skip;
            Totals(run);
            await SaveAsync(ct);
        }
        return await GetRunAsync(runUuid, caller, ct);
    }

    /// <summary>A Draft run, or (REV-10) one stuck in Processing for <see cref="StuckAfter"/>: its pending items are skipped.</summary>
    public async Task<DunningRunModel> CancelRunAsync(Guid runUuid, string? reason, ArApCaller caller, CancellationToken ct = default)
    {
        var org = Org(caller);
        var run = await LoadRunAsync(org, runUuid, ct);
        var now = _clock.GetUtcNow().UtcDateTime;
        var stuck = IsStuck(run, now);
        if (run.Status != ArApEnums.DunningRunStatus.Draft && !stuck) RequireDraft(run);

        var why = string.IsNullOrWhiteSpace(reason) ? null : CreditControlService.Truncate(reason.Trim(), 500);
        if (_db.Database.IsRelational())
        {
            // REV-12 — conditional on the status (and, for a stuck run, on the claim it was stuck with).
            var status = run.Status;
            var executedAt = run.ExecutedAt;
            var changed = await _db.Set<DunningRun>().IgnoreQueryFilters()
                .Where(r => r.Id == run.Id && r.Status == status && r.ExecutedAt == executedAt)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ArApEnums.DunningRunStatus.Cancelled)
                                          .SetProperty(r => r.CancelledAt, now)
                                          .SetProperty(r => r.CancelledBy, caller.UserIdOrNull)
                                          .SetProperty(r => r.CancelledByName, caller.UserName)
                                          .SetProperty(r => r.CancelReason, why), ct);
            if (changed == 0) throw new ConflictException($"Dunning run {run.RunNumber} changed meanwhile; reload it.");
            _db.ChangeTracker.Clear();
            run = await LoadRunAsync(org, runUuid, ct);
        }
        else
        {
            run.Status = ArApEnums.DunningRunStatus.Cancelled;
            run.CancelledAt = now; run.CancelledBy = caller.UserIdOrNull; run.CancelledByName = caller.UserName; run.CancelReason = why;
        }

        foreach (var i in run.Items.Where(i => i.Status == ArApEnums.DunningRunItemStatus.Pending))
        {
            i.Status     = ArApEnums.DunningRunItemStatus.Skipped;
            i.SkipReason = "The run was cancelled.";
        }
        Totals(run);
        await SaveAsync(ct);
        return await GetRunAsync(runUuid, caller, ct);
    }

    public async Task<DunningRunModel> ExecuteAsync(Guid runUuid, ArApCaller caller, CancellationToken ct = default)
    {
        var org = Org(caller);
        var run = await LoadRunAsync(org, runUuid, ct);
        var now = _clock.GetUtcNow().UtcDateTime;
        // REV-10 — a run stuck in Processing (its request died) is resumed: only its pending items are left to do.
        var resume = IsStuck(run, now);
        if (!resume) RequireDraft(run);

        // Draft → Processing (or the stale claim → a new one), committed first: a second execute reads Processing and is refused.
        if (_db.Database.IsRelational())
        {
            var status = run.Status;
            var executedAt = run.ExecutedAt;
            var claimed = await _db.Set<DunningRun>().IgnoreQueryFilters()
                .Where(r => r.Id == run.Id && r.Status == status && r.ExecutedAt == executedAt)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, ArApEnums.DunningRunStatus.Processing)
                                          .SetProperty(r => r.ExecutedAt, now)
                                          .SetProperty(r => r.ExecutedBy, caller.UserIdOrNull)
                                          .SetProperty(r => r.ExecutedByName, caller.UserName), ct);
            if (claimed == 0) throw new ConflictException($"Dunning run {run.RunNumber} is already being executed.");
            _db.ChangeTracker.Clear();
        }
        else
        {
            run.Status = ArApEnums.DunningRunStatus.Processing;
            run.ExecutedAt = now; run.ExecutedBy = caller.UserIdOrNull; run.ExecutedByName = caller.UserName;
            await _db.SaveChangesAsync(ct);
        }

        // REV-10 — once claimed, the run is finished whatever happens to the request: no cancellation from here on.
        var work = CancellationToken.None;
        run = await LoadRunAsync(org, runUuid, work);
        var procedures = await Procedures(org).AsNoTracking().Include(p => p.Levels).ToListAsync(work);
        var baseCode = run.CurrencyCode ?? await _credit.BaseCodeAsync(org, work);
        var pending = run.Items.Where(i => i.Status == ArApEnums.DunningRunItemStatus.Pending).Select(i => i.Uuid).ToList();

        foreach (var itemUuid in pending)
        {
            try
            {
                await ExecuteItemAsync(org, run.Id, itemUuid, procedures, baseCode, caller, work);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Dunning run item {Item} failed.", itemUuid);
                await MarkFailedAsync(org, itemUuid, ex.Message, work);
            }
        }

        _db.ChangeTracker.Clear();
        run = await LoadRunAsync(org, runUuid, work);
        Totals(run);
        run.Status      = ArApEnums.DunningRunStatus.Completed;
        run.CompletedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(work);
        return await GetRunAsync(runUuid, caller, work);
    }

    private static bool IsStuck(DunningRun run, DateTime now) =>
        run.Status == ArApEnums.DunningRunStatus.Processing && run.ExecutedAt is { } at && now - at >= StuckAfter;

    private async Task MarkFailedAsync(Guid org, Guid itemUuid, string reason, CancellationToken ct)
    {
        _db.ChangeTracker.Clear();
        var item = await _db.Set<DunningRunItem>().IgnoreQueryFilters().FirstAsync(i => i.OrganizationId == org && i.Uuid == itemUuid, ct);
        if (item.Status != ArApEnums.DunningRunItemStatus.Pending) return;
        item.Status        = ArApEnums.DunningRunItemStatus.Failed;
        item.FailureReason = CreditControlService.Truncate(reason, 1000);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// One item (tasks 207–216). REV-1 / REV-2 / REV-13 order: deliverability first (no address / no sender → Failed); the
    /// notice through the result-returning send (a failure → Failed with the sender's reason: no charge, no level, no hold);
    /// then the charge (idempotent on the item) and level, date and hold — retried on a profile conflict, and never turning a
    /// delivered notice into Failed.
    /// </summary>
    private async Task ExecuteItemAsync(
        Guid org, int runId, Guid itemUuid, List<DunningProcedure> procedures, string? baseCode, ArApCaller caller, CancellationToken ct)
    {
        _db.ChangeTracker.Clear();
        var item = await _db.Set<DunningRunItem>().IgnoreQueryFilters().Include(i => i.DunningRun)
            .FirstAsync(i => i.OrganizationId == org && i.Uuid == itemUuid && i.DunningRunId == runId, ct);
        var run = item.DunningRun;
        var procedure = procedures.FirstOrDefault(p => p.Id == item.DunningProcedureId) ?? procedures.First(p => p.IsDefault);
        var level = procedure.Levels.FirstOrDefault(l => l.LevelNumber == item.DunningLevel)
            ?? throw new InvalidOperationException($"Procedure {procedure.Code} has no level {item.DunningLevel} any more.");

        var profile = await _profiles.GetOrCreateAsync(org, item.PartnerUuid, ArApEnums.PartnerRole.Customer, ct);
        if (profile.CurrentDunningLevel != item.PreviousLevel)
        {
            item.Status     = ArApEnums.DunningRunItemStatus.Skipped;
            item.SkipReason = $"The customer's level changed since the preview ({item.PreviousLevel} → {profile.CurrentDunningLevel}).";
            await _db.SaveChangesAsync(ct);
            return;
        }

        // The overdue invoices as they stand now (a payment since the preview counts), the preview's own if all are still open.
        var invoiceUuids = JsonSerializer.Deserialize<List<Guid>>(item.InvoiceUuids ?? "[]") ?? [];
        var open = (await OpenItemsAsync(org, item.PartnerUuid, ct)).GetValueOrDefault(item.PartnerUuid) ?? [];
        var items = open.Where(o => invoiceUuids.Contains(o.InvoiceUuid)).Select(o => o.Item).ToList();
        if (items.Count == 0)
        {
            item.Status     = ArApEnums.DunningRunItemStatus.Skipped;
            item.SkipReason = "Everything dunned was paid or credited since the preview.";
            await _db.SaveChangesAsync(ct);
            return;
        }

        // REV-2 — can the notice go at all? Checked before anything is charged.
        var email = level.ActionType == ArApEnums.DunningActionType.Email;
        string? to = null;
        if (email)
        {
            to = item.SentTo;
            if (_partners is not null && await _partners.GetAsync(org, item.PartnerUuid, null, ct) is { } partner)
                to = partner.Email ?? partner.ContactEmail ?? to;
            if (string.IsNullOrWhiteSpace(to)) throw new InvalidOperationException($"{item.PartnerName} has no email address.");
            if (_email is null) throw new InvalidOperationException("No email sender is configured.");
            to = to.Trim();
        }

        Render(item, level, items, baseCode);

        var now = _clock.GetUtcNow().UtcDateTime;
        if (email)
        {
            var attachments = new List<EmailAttachment>();
            if (level.IncludeStatement && await StatementPdfAsync(item.PartnerUuid, run.AsOfDate, caller, ct) is { } pdf)
                attachments.Add(pdf);
            var error = await SendNoticeAsync(to!, item.Subject ?? $"Payment reminder (level {item.DunningLevel})", item.Body ?? string.Empty, attachments, ct);
            if (error is not null)
            {
                // REV-1 / REV-2 — not delivered: Failed with the sender's reason; no charge, no level, no hold.
                item.Status        = ArApEnums.DunningRunItemStatus.Failed;
                item.FailureReason = CreditControlService.Truncate($"The notice could not be sent: {error}", 1000);
                await _db.SaveChangesAsync(ct);
                return;
            }
            item.SentTo = CreditControlService.Truncate(to!, 200);
            item.SentAt = now;
            item.Status = ArApEnums.DunningRunItemStatus.Sent;
        }
        else
        {
            // Letter / Manual: the notice is rendered and recorded for someone to act on; nothing is sent.
            item.SentAt = now;
            item.Status = ArApEnums.DunningRunItemStatus.Manual;
        }

        // D-10 / REV-2 — the charge only for a delivered (or recorded) notice: its own customer document, idempotent on the
        // item (and reused from an earlier undelivered item of this customer and level whose run died after charging).
        if (item.ChargeAmount is > 0m && item.ChargeDocumentUuid is null)
        {
            try
            {
                var reused = await UnusedChargeAsync(org, item, ct);
                var (uuid, number) = reused ?? await _charges.RaiseAsync(org, item.Uuid, item.PartnerUuid, item.PartnerName,
                    item.ChargeAmount.Value, item.DunningLevel, run.RunNumber, baseCode, caller.UserIdOrNull, ct);
                item.ChargeDocumentUuid   = uuid;
                item.ChargeDocumentNumber = number;
            }
            catch (Exception ex)
            {
                // The notice went: the item stays delivered and the level moves; the charge is reported for a person to raise.
                _log.LogError(ex, "Dunning item {Item} was delivered but its charge could not be raised.", item.Uuid);
                item.FailureReason = CreditControlService.Truncate($"Delivered, but the charge could not be raised: {ex.Message}", 1000);
                _db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList().ForEach(e => e.State = EntityState.Detached);
            }
        }

        await CommitOutcomeAsync(org, item, profile, procedure, run, caller, now, ct);
    }

    /// <summary>
    /// REV-13 — the item's outcome with the profile's level / date / hold. A profile conflict (someone saved it meanwhile)
    /// is retried on a fresh read; if it still cannot be saved, the item keeps its delivered status with the reason.
    /// </summary>
    private async Task CommitOutcomeAsync(Guid org, DunningRunItem item, PartnerFinancialProfile profile, DunningProcedure procedure,
        DunningRun run, ArApCaller caller, DateTime now, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            ApplyOutcome(profile, item, procedure, run, caller, now);
            try
            {
                await _db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateConcurrencyException ex) when (attempt < 3)
            {
                _log.LogInformation(ex, "Customer {Customer}'s profile changed while dunning; retrying the level update.", item.PartnerUuid);
            }
            catch (DbUpdateException ex)
            {
                _log.LogError(ex, "Dunning item {Item} was delivered but the customer's level could not be updated.", item.Uuid);
                var (status, sentTo, sentAt, subject, body, chargeUuid, chargeNumber) =
                    (item.Status, item.SentTo, item.SentAt, item.Subject, item.Body, item.ChargeDocumentUuid, item.ChargeDocumentNumber);
                _db.ChangeTracker.Clear();
                var fresh = await _db.Set<DunningRunItem>().IgnoreQueryFilters().FirstAsync(i => i.Id == item.Id, ct);
                (fresh.Status, fresh.SentTo, fresh.SentAt, fresh.Subject, fresh.Body, fresh.ChargeDocumentUuid, fresh.ChargeDocumentNumber) =
                    (status, sentTo, sentAt, subject, body, chargeUuid, chargeNumber);
                fresh.FailureReason = CreditControlService.Truncate($"Delivered, but the customer's dunning level could not be updated: {ex.Message}", 1000);
                await _db.SaveChangesAsync(ct);
                return;
            }

            // Re-read the profile (and keep the item's in-memory outcome) for the next attempt.
            var keep = (item.Status, item.SentTo, item.SentAt, item.Subject, item.Body, item.ChargeDocumentUuid, item.ChargeDocumentNumber);
            _db.ChangeTracker.Clear();
            item = await _db.Set<DunningRunItem>().IgnoreQueryFilters().FirstAsync(i => i.Id == item.Id, ct);
            (item.Status, item.SentTo, item.SentAt, item.Subject, item.Body, item.ChargeDocumentUuid, item.ChargeDocumentNumber) = keep;
            profile = await _profiles.GetOrCreateAsync(org, item.PartnerUuid, ArApEnums.PartnerRole.Customer, ct);
        }
    }

    private void ApplyOutcome(PartnerFinancialProfile profile, DunningRunItem item, DunningProcedure procedure, DunningRun run,
        ArApCaller caller, DateTime now)
    {
        profile.CurrentDunningLevel = Math.Max(profile.CurrentDunningLevel, item.DunningLevel);
        profile.LastDunningDate     = run.RunDate;
        profile.ModifiedAt          = now;
        profile.ModifiedBy          = caller.UserIdOrNull;
        profile.ModifiedByName      = caller.UserName;

        // BR-DN-005 — the block-sales level puts the customer on hold, only when the profile opts in.
        if (procedure.BlockSalesAtLevel is { } block && item.DunningLevel >= block && profile.BlockSalesAtDunning && !profile.CreditHold)
        {
            var reason = $"Dunning Level {item.DunningLevel}";
            _credit.ApplyHold(profile, reason, caller.UserIdOrNull, caller.UserName);
            _credit.Audit(profile, item.PartnerName, ArApEnums.CreditHoldAction.DunningHold, $"{reason} ({run.RunNumber})",
                "Active", "On hold", userId: caller.UserIdOrNull, userName: caller.UserName,
                docType: "DUNNING_RUN", docUuid: run.Uuid, docNumber: run.RunNumber);
        }
    }

    /// <summary>
    /// REV-2 — a charge already raised for this customer and level by an earlier item whose notice never went (Failed), and
    /// still live (not cancelled): it is this item's charge too, so a customer is charged once per level per cycle.
    /// </summary>
    private async Task<(Guid, string)?> UnusedChargeAsync(Guid org, DunningRunItem item, CancellationToken ct)
    {
        // Earlier items of this customer and level whose notice never went (Failed, Skipped, or Pending in a run that died).
        var earlier = await _db.Set<DunningRunItem>().IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.OrganizationId == org && i.PartnerUuid == item.PartnerUuid && i.DunningLevel == item.DunningLevel && i.Id != item.Id
                     && (i.Status == ArApEnums.DunningRunItemStatus.Failed || i.Status == ArApEnums.DunningRunItemStatus.Skipped
                         || i.Status == ArApEnums.DunningRunItemStatus.Pending))
            .Select(i => i.Uuid)
            .ToListAsync(ct);
        if (earlier.Count == 0) return null;

        // Their charges, found by the charge's own idempotency key (SourceUuid = the item) — so even a charge whose link was
        // never written back to its item is found.
        var charges = await _db.SalesInvoices.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.OrganizationId == org && s.SourceType == ArDocumentSources.DunningCharge && s.SourceUuid != null
                     && earlier.Contains(s.SourceUuid.Value) && !s.IsDelete && s.Status != SalesInvoiceStatuses.Cancelled)
            .OrderByDescending(s => s.Id)
            .Select(s => new { s.UUID, s.InvoiceNumber })
            .ToListAsync(ct);
        foreach (var c in charges)
        {
            var delivered = await _db.Set<DunningRunItem>().IgnoreQueryFilters().AsNoTracking().AnyAsync(i =>
                i.OrganizationId == org && i.ChargeDocumentUuid == c.UUID
                && (i.Status == ArApEnums.DunningRunItemStatus.Sent || i.Status == ArApEnums.DunningRunItemStatus.Manual), ct);
            if (!delivered) return (c.UUID, c.InvoiceNumber);
        }
        return null;
    }

    /// <summary>
    /// REV-1 — the notice through the result-returning send: null when it was sent, else the sender's reason (SMTP /
    /// SendGrid failure, missing key, invalid address, …). A sender that still throws, or answers nothing, is a failure too.
    /// </summary>
    private async Task<string?> SendNoticeAsync(string to, string subject, string body, List<EmailAttachment> attachments, CancellationToken ct)
    {
        try
        {
            var result = await _email!.SendWithResultAsync(to, subject, body, attachments.Count == 0 ? null : attachments, ct);
            if (result is { Success: true }) return null;
            var why = result?.Error ?? "the email sender gave no result";
            _log.LogWarning("The dunning notice to {To} could not be sent: {Error}", to, why);
            return why;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "The dunning notice to {To} could not be sent.", to);
            return ex.Message;
        }
    }

    /// <summary>Recomputes a run's totals from its items, conditionally on its status (REV-12).</summary>
    private async Task RecountAsync(Guid org, Guid runUuid, string onlyIfStatus, CancellationToken ct)
    {
        var run = await LoadRunAsync(org, runUuid, ct);
        if (run.Status != onlyIfStatus) return;
        Totals(run);
        await _db.Set<DunningRun>().IgnoreQueryFilters()
            .Where(r => r.Id == run.Id && r.Status == onlyIfStatus)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.TotalCustomers, run.TotalCustomers)
                                      .SetProperty(r => r.TotalAmount, run.TotalAmount)
                                      .SetProperty(r => r.ItemsProcessed, run.ItemsProcessed)
                                      .SetProperty(r => r.ItemsSent, run.ItemsSent)
                                      .SetProperty(r => r.ItemsSkipped, run.ItemsSkipped)
                                      .SetProperty(r => r.ItemsFailed, run.ItemsFailed), ct);
        _db.ChangeTracker.Clear();
    }

    public async Task<DunningRunModel> GetRunAsync(Guid runUuid, ArApCaller caller, CancellationToken ct = default)
    {
        var org = Org(caller);
        var run = await _db.Set<DunningRun>().IgnoreQueryFilters().AsNoTracking().Include(r => r.Items)
            .FirstOrDefaultAsync(r => r.OrganizationId == org && r.Uuid == runUuid, ct)
            ?? throw new NotFoundException("Dunning run", runUuid);
        var codes = await Procedures(org).AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Code, ct);
        var model = ToModel(run);
        model.Items = run.Items.OrderBy(i => i.PartnerName, StringComparer.OrdinalIgnoreCase)
            .Select(i => ToModel(i, i.DunningProcedureId is { } id ? codes.GetValueOrDefault(id) : null)).ToList();
        return model;
    }

    public async Task<PaginatedResponse<DunningRunModel>> ListRunsAsync(int page, int pageSize, ArApCaller caller, CancellationToken ct = default)
    {
        var org = Org(caller);
        var runs = await _db.Set<DunningRun>().IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.OrganizationId == org)
            .OrderByDescending(r => r.RunDate).ThenByDescending(r => r.Id)
            .ToListAsync(ct);
        return CreditControlService.Page(runs.Select(ToModel).ToList(), page, pageSize);
    }

    // ══ Per customer (tasks 220–222) ═══════════════════════════════════════════════════════════════════════════════

    public async Task<DunningHistoryModel> CustomerHistoryAsync(Guid customerUuid, ArApCaller caller, CancellationToken ct = default)
    {
        var org = Org(caller);
        var name = await CustomerNameAsync(org, customerUuid, ct);
        var profile = await _db.Set<PartnerFinancialProfile>().IgnoreQueryFilters().AsNoTracking().Include(p => p.DunningProcedure)
            .FirstOrDefaultAsync(p => p.OrganizationId == org && p.PartnerUuid == customerUuid && p.PartnerRole == ArApEnums.PartnerRole.Customer, ct);

        var notices = await _db.Set<DunningRunItem>().IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.OrganizationId == org && i.PartnerUuid == customerUuid && i.DunningRun.Status != ArApEnums.DunningRunStatus.Cancelled)
            .Select(i => new
            {
                i.DunningRun.RunNumber, i.DunningRun.Uuid, i.DunningRun.RunDate, i.DunningRun.CreatedAt, i.SentAt, i.DunningLevel,
                i.Status, i.OverdueAmount, i.SkipReason, i.FailureReason, i.ChargeDocumentNumber, i.DunningRun.ExecutedByName
            })
            .ToListAsync(ct);
        var audits = await _db.Set<CreditHoldRequest>().IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.OrganizationId == org && r.PartnerUuid == customerUuid
                     && (r.Action == ArApEnums.CreditHoldAction.DunningReset || r.Action == ArApEnums.CreditHoldAction.DunningHold))
            .ToListAsync(ct);

        var events = notices.Select(n => new DunningHistoryEvent
            {
                At = n.SentAt ?? n.CreatedAt, Kind = "Notice", Level = n.DunningLevel, RunNumber = n.RunNumber, RunUuid = n.Uuid,
                Status = n.Status, Amount = n.OverdueAmount,
                Detail = n.FailureReason ?? n.SkipReason ?? (n.ChargeDocumentNumber is null ? null : $"Charge {n.ChargeDocumentNumber}"),
                ByName = n.ExecutedByName
            })
            .Concat(audits.Select(a => new DunningHistoryEvent
            {
                At = a.RequestedAt, Kind = a.Action == ArApEnums.CreditHoldAction.DunningReset ? "Reset" : "Hold",
                Level = int.TryParse(a.NewValue, out var lv) ? lv : null, Detail = a.Reason, ByName = a.RequestedByName,
                RunNumber = a.SourceDocumentNumber, RunUuid = a.SourceDocumentUuid
            }))
            .OrderByDescending(e => e.At).ToList();

        var stored = profile?.CurrentDunningLevel ?? 0;
        var owing = stored > 0 && (await EffectiveDunningLevel.OwingAsync(_db, org, [customerUuid], ct)).Contains(customerUuid);

        return new DunningHistoryModel
        {
            CustomerUuid        = customerUuid,
            CustomerName        = name,
            CurrentDunningLevel = EffectiveDunningLevel.Of(stored, owing),   // D-AR-13
            LastDunningDate     = profile?.LastDunningDate,
            ProcedureCode       = profile?.DunningProcedure?.Code,
            ExcludeFromDunning  = profile?.ExcludeFromDunning ?? false,
            Events              = events
        };
    }

    /// <summary>BR-DN-009 — manual reset (DUNNING_ADMIN): to 0 by default, or down to a lower level; audited.</summary>
    public async Task<DunningHistoryModel> ResetAsync(Guid customerUuid, string? reason, int? level, ArApCaller caller, CancellationToken ct = default)
    {
        var why = CreditControlService.RequireReason(reason);
        var org = Org(caller);
        var name = await CustomerNameAsync(org, customerUuid, ct);
        var target = level ?? 0;
        var profile = await _profiles.GetOrCreateAsync(org, customerUuid, ArApEnums.PartnerRole.Customer, ct);
        if (target < 0 || target > profile.CurrentDunningLevel)
            throw new BadRequestException($"The level can only be reset down: {name} is at level {profile.CurrentDunningLevel}.");

        if (target != profile.CurrentDunningLevel)
        {
            var previous = profile.CurrentDunningLevel;
            profile.CurrentDunningLevel = target;
            if (target == 0) profile.LastDunningDate = null;
            profile.ModifiedAt = _clock.GetUtcNow().UtcDateTime;
            profile.ModifiedBy = caller.UserIdOrNull;
            profile.ModifiedByName = caller.UserName;
            _credit.Audit(profile, name, ArApEnums.CreditHoldAction.DunningReset, why,
                previous.ToString(CultureInfo.InvariantCulture), target.ToString(CultureInfo.InvariantCulture),
                userId: caller.UserIdOrNull, userName: caller.UserName);
            await SaveAsync(ct);
        }
        return await CustomerHistoryAsync(customerUuid, caller, ct);
    }

    /// <summary>
    /// BR-DN-006 / task 222 — every profile above level 0 whose customer has nothing open is reset to 0 (audited, "Balance
    /// cleared"). Returns how many. <paramref name="owing"/> = the customers with open invoices.
    /// </summary>
    internal async Task<int> AutoResetAsync(Guid org, HashSet<Guid> owing, ArApCaller caller, CancellationToken ct)
    {
        var leveled = await _db.Set<PartnerFinancialProfile>().IgnoreQueryFilters()
            .Where(p => p.OrganizationId == org && p.PartnerRole == ArApEnums.PartnerRole.Customer && p.CurrentDunningLevel > 0)
            .ToListAsync(ct);
        var cleared = leveled.Where(p => !owing.Contains(p.PartnerUuid)).ToList();
        if (cleared.Count == 0) return 0;

        // A credit-only customer (negative balance) also owes nothing.
        foreach (var p in cleared)
        {
            var previous = p.CurrentDunningLevel;
            p.CurrentDunningLevel = 0;
            p.LastDunningDate     = null;
            p.ModifiedAt          = _clock.GetUtcNow().UtcDateTime;
            _credit.Audit(p, null, ArApEnums.CreditHoldAction.DunningReset, "Balance cleared",
                previous.ToString(CultureInfo.InvariantCulture), "0", userId: caller.UserIdOrNull, userName: caller.UserName);
        }
        await SaveAsync(ct);
        return cleared.Count;
    }

    // ══ Helpers ════════════════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>Open sales invoices (ISSUED / PARTIALLY_PAID / OVERDUE, BalanceDue &gt; 0) per customer, base at the locked rate.</summary>
    private async Task<Dictionary<Guid, List<OpenInvoiceRow>>> OpenItemsAsync(Guid org, Guid? customer, CancellationToken ct)
    {
        var query = _db.SalesInvoices.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.OrganizationId == org && !i.IsDelete && CreditExposureReader.OpenInvoiceStatuses.Contains(i.Status) && i.BalanceDue > 0m);
        if (customer is { } c) query = query.Where(i => i.PartnerId == c);
        var rows = await query.Select(i => new
        {
            i.UUID, i.InvoiceNumber, i.InvoiceDate, i.DueDate, i.CurrencyCode, i.BalanceDue, i.ExchangeRate, i.PartnerId, i.PartnerName
        }).ToListAsync(ct);

        return rows.GroupBy(r => r.PartnerId).ToDictionary(g => g.Key, g => g
            .Select(r => new OpenInvoiceRow(r.UUID, new DunningOpenItem(r.UUID, r.InvoiceNumber, r.InvoiceDate, r.DueDate, r.CurrencyCode,
                r.BalanceDue, Math.Round(r.BalanceDue * (r.ExchangeRate ?? 1m), 2, MidpointRounding.AwayFromZero)), r.PartnerName))
            .OrderBy(r => r.Item.DueDate).ToList());
    }

    private sealed record OpenInvoiceRow(Guid InvoiceUuid, DunningOpenItem Item, string? CustomerName)
    {
        public string InvoiceNumber => Item.InvoiceNumber;
    }

    private void Render(DunningRunItem item, DunningLevel level, IReadOnlyList<DunningOpenItem> items, string? baseCode)
    {
        var model = new DunningTemplateModel(item.PartnerName, item.PartnerCode, item.DunningLevel, items.Sum(i => i.OpenBase),
            baseCode ?? string.Empty, items.Count == 0 ? null : items.Min(i => i.DueDate),
            level.IncludeInvoiceList ? items : [], item.ChargeAmount);
        item.Subject = CreditControlService.Truncate(_templates.RenderSubject(level.SubjectTemplate, model), 200);
        item.Body    = _templates.RenderBody(level.BodyTemplate, model);
    }

    private async Task<EmailAttachment?> StatementPdfAsync(Guid customer, DateOnly asOf, ArApCaller caller, CancellationToken ct)
    {
        if (_statements is null) return null;
        try
        {
            var output = await _statements.GenerateCustomerStatementAsync(customer,
                new StatementRequest { Type = ArApEnums.StatementType.OpenItems, To = asOf, Format = "pdf" },
                new StatementActor(caller.UserIdOrNull, caller.UserName), ct);
            return output.Pdf is { Length: > 0 } pdf ? new EmailAttachment(output.FileName, pdf, "application/pdf") : null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "The open-items statement for dunning customer {Customer} could not be generated; the notice goes without it.", customer);
            return null;
        }
    }

    /// <summary>D-16 defaults on demand (CORE's seeder, retry-safe since D-CORE-14); a failure is the caller's error, not hidden.</summary>
    private Task EnsureDefaultsAsync(Guid org, CancellationToken ct) => _seeder.EnsureAsync(org, ct);

    private static DunningProcedureRule Rule(DunningProcedure p) =>
        new(p.GraceDays, p.MinimumAmount, p.Levels.Select(l => new DunningLevelRule(l.LevelNumber, l.DaysAfterPrevious)).ToList());

    private static string CurrencySummary(IEnumerable<DunningOpenItem> items) =>
        CreditControlService.Truncate(string.Join("; ", items.GroupBy(i => i.CurrencyCode).OrderBy(g => g.Key)
            .Select(g => $"{g.Key} {g.Sum(i => i.Open).ToString("#,##0.00", CultureInfo.InvariantCulture)}")), 500);

    private static void Totals(DunningRun run)
    {
        var live = run.Items.Where(i => i.Status != ArApEnums.DunningRunItemStatus.Skipped).ToList();
        run.TotalCustomers = live.Count;
        run.TotalAmount    = live.Sum(i => i.OverdueAmount);
        run.ItemsProcessed = run.Items.Count(i => i.Status != ArApEnums.DunningRunItemStatus.Pending);
        run.ItemsSent      = run.Items.Count(i => i.Status is ArApEnums.DunningRunItemStatus.Sent or ArApEnums.DunningRunItemStatus.Manual);
        run.ItemsSkipped   = run.Items.Count(i => i.Status == ArApEnums.DunningRunItemStatus.Skipped);
        run.ItemsFailed    = run.Items.Count(i => i.Status == ArApEnums.DunningRunItemStatus.Failed);
    }

    private async Task RefuseSecondRunAsync(Guid org, DateOnly day, CancellationToken ct)
    {
        var existing = await _db.Set<DunningRun>().IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.OrganizationId == org && r.RunDate == day && r.Status != ArApEnums.DunningRunStatus.Cancelled)
            .Select(r => r.RunNumber).FirstOrDefaultAsync(ct);
        if (existing is not null)
            throw new ConflictException($"A dunning run already exists for {day:yyyy-MM-dd} ({existing}).");
    }

    private async Task<string> RunNumberAsync(Guid org, DateTime now, CancellationToken ct)
    {
        if (_numbers is not null)
        {
            try { return await _numbers.NextAsync(RunPrefix, now, org, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _log.LogWarning(ex, "The document-number generator failed; the dunning run is numbered by date.");
            }
        }
        // REV-15 — the fallback is unique too: a cancelled run of the same day keeps its number.
        var prefix = $"{RunPrefix}-{now:yyyyMMdd}";
        var used = await _db.Set<DunningRun>().IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.OrganizationId == org && r.RunNumber.StartsWith(prefix)).Select(r => r.RunNumber).ToListAsync(ct);
        if (!used.Contains(prefix)) return prefix;
        var n = 2;
        while (used.Contains($"{prefix}-{n}")) n++;
        return $"{prefix}-{n}";
    }

    private async Task<DunningRun> LoadRunAsync(Guid org, Guid runUuid, CancellationToken ct) =>
        await _db.Set<DunningRun>().IgnoreQueryFilters().Include(r => r.Items)
            .FirstOrDefaultAsync(r => r.OrganizationId == org && r.Uuid == runUuid, ct)
        ?? throw new NotFoundException("Dunning run", runUuid);

    private static void RequireDraft(DunningRun run)
    {
        if (run.Status != ArApEnums.DunningRunStatus.Draft)
            throw new ConflictException($"Dunning run {run.RunNumber} is {run.Status}; it can no longer change.");
    }

    private IQueryable<DunningProcedure> Procedures(Guid org) =>
        _db.Set<DunningProcedure>().IgnoreQueryFilters().Where(p => p.OrganizationId == org);

    private async Task ClearOtherDefaultsAsync(Guid org, int? keepId, CancellationToken ct)
    {
        var others = await Procedures(org).Where(p => p.IsDefault && (keepId == null || p.Id != keepId)).ToListAsync(ct);
        foreach (var o in others) o.IsDefault = false;
        // Two default rows never exist at once (filtered unique index): the old default is cleared in its own save first.
        if (others.Count > 0 && _db.Database.IsRelational())
        {
            var ids = others.Select(o => o.Id).ToList();
            await Procedures(org).Where(p => ids.Contains(p.Id)).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsDefault, false), ct);
        }
    }

    private static string ValidateProcedure(DunningProcedureModel r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var code = r.Code?.Trim().ToUpperInvariant() ?? string.Empty;
        if (code.Length == 0) throw new BadRequestException("The code is required.");
        if (code.Length > 20) throw new BadRequestException("The code cannot be longer than 20 characters.");
        if (string.IsNullOrWhiteSpace(r.Name)) throw new BadRequestException("The name is required.");
        if (r.Name.Trim().Length > 100) throw new BadRequestException("The name cannot be longer than 100 characters.");
        if (r.Description is { Length: > 500 }) throw new BadRequestException("The description cannot be longer than 500 characters.");
        if (r.GraceDays < 0) throw new BadRequestException("Grace days cannot be negative.");
        if (r.MinimumAmount < 0m) throw new BadRequestException("The minimum amount cannot be negative.");
        if (r.IsDefault && !r.IsActive) throw new BadRequestException("The default dunning procedure must be active.");

        var levels = r.Levels ?? [];
        if (levels.Count == 0) throw new BadRequestException("A dunning procedure needs at least one level.");
        var numbers = levels.Select(l => l.LevelNumber).OrderBy(n => n).ToList();
        if (!numbers.SequenceEqual(Enumerable.Range(1, levels.Count)))
            throw new BadRequestException($"Dunning levels must be numbered 1 to {levels.Count} without gaps.");
        if (r.BlockSalesAtLevel is { } b && (b < 1 || b > levels.Count))
            throw new BadRequestException($"The block-sales level must be between 1 and {levels.Count}.");

        foreach (var l in levels)
        {
            if (l.DaysAfterPrevious < 0) throw new BadRequestException($"Level {l.LevelNumber}: days after previous cannot be negative.");
            if (string.IsNullOrWhiteSpace(l.SubjectTemplate)) throw new BadRequestException($"Level {l.LevelNumber}: the subject is required.");
            if (l.SubjectTemplate.Length > 200) throw new BadRequestException($"Level {l.LevelNumber}: the subject cannot be longer than 200 characters.");
            if (string.IsNullOrWhiteSpace(l.BodyTemplate)) throw new BadRequestException($"Level {l.LevelNumber}: the body is required.");
            if (l.ChargeAmount is < 0m) throw new BadRequestException($"Level {l.LevelNumber}: the charge cannot be negative.");
            if (l.ChargeAmount is { } c && decimal.Round(c, 2) != c) throw new BadRequestException($"Level {l.LevelNumber}: the charge has more than two decimals.");
            if (ArApEnums.Canonical(l.ActionType ?? ArApEnums.DunningActionType.Email, ArApEnums.DunningActionType.All) is null)
                throw new BadRequestException(ArApEnums.UnknownText("actionType", l.ActionType, ArApEnums.DunningActionType.All));
            if (l.EscalationNote is { Length: > 500 }) throw new BadRequestException($"Level {l.LevelNumber}: the escalation note cannot be longer than 500 characters.");
        }
        return code;
    }

    private static void Fill(DunningProcedure p, DunningProcedureModel r, Guid org)
    {
        p.Name              = r.Name.Trim();
        p.Description       = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim();
        p.IsDefault         = r.IsDefault;
        p.IsActive          = r.IsActive;
        p.GraceDays         = r.GraceDays;
        p.MinimumAmount     = Math.Round(r.MinimumAmount, 2, MidpointRounding.AwayFromZero);
        p.BlockSalesAtLevel = r.BlockSalesAtLevel;
        foreach (var l in r.Levels.OrderBy(l => l.LevelNumber))
            p.Levels.Add(new DunningLevel
            {
                OrganizationId     = org,
                LevelNumber        = l.LevelNumber,
                DaysAfterPrevious  = l.DaysAfterPrevious,
                SubjectTemplate    = l.SubjectTemplate.Trim(),
                BodyTemplate       = l.BodyTemplate,
                IncludeInvoiceList = l.IncludeInvoiceList,
                IncludeStatement   = l.IncludeStatement,
                ChargeAmount       = l.ChargeAmount is > 0m ? l.ChargeAmount : null,
                ActionType         = ArApEnums.Canonical(l.ActionType ?? ArApEnums.DunningActionType.Email, ArApEnums.DunningActionType.All)!,
                EscalationNote     = string.IsNullOrWhiteSpace(l.EscalationNote) ? null : l.EscalationNote.Trim()
            });
    }

    private static DunningProcedureModel ToModel(DunningProcedure p) => new()
    {
        Uuid = p.Uuid, Code = p.Code, Name = p.Name, Description = p.Description, IsDefault = p.IsDefault, IsActive = p.IsActive,
        GraceDays = p.GraceDays, MinimumAmount = p.MinimumAmount, BlockSalesAtLevel = p.BlockSalesAtLevel,
        Levels = p.Levels.OrderBy(l => l.LevelNumber).Select(l => new DunningLevelModel
        {
            Uuid = l.Uuid, LevelNumber = l.LevelNumber, DaysAfterPrevious = l.DaysAfterPrevious, SubjectTemplate = l.SubjectTemplate,
            BodyTemplate = l.BodyTemplate, IncludeInvoiceList = l.IncludeInvoiceList, IncludeStatement = l.IncludeStatement,
            ChargeAmount = l.ChargeAmount, ActionType = l.ActionType, EscalationNote = l.EscalationNote
        }).ToList()
    };

    private static DunningRunModel ToModel(DunningRun r) => new()
    {
        Uuid = r.Uuid, RunNumber = r.RunNumber, RunDate = r.RunDate, AsOfDate = r.AsOfDate, Status = r.Status, CurrencyCode = r.CurrencyCode,
        TotalCustomers = r.TotalCustomers, TotalAmount = r.TotalAmount, ItemsProcessed = r.ItemsProcessed, ItemsSent = r.ItemsSent,
        ItemsSkipped = r.ItemsSkipped, ItemsFailed = r.ItemsFailed, CreatedAt = r.CreatedAt, CreatedByName = r.CreatedByName,
        ExecutedAt = r.ExecutedAt, ExecutedByName = r.ExecutedByName, CompletedAt = r.CompletedAt, CancelledAt = r.CancelledAt,
        CancelReason = r.CancelReason
    };

    private static DunningRunItemModel ToModel(DunningRunItem i, string? procedureCode) => new()
    {
        Uuid = i.Uuid, CustomerUuid = i.PartnerUuid, CustomerName = i.PartnerName, CustomerCode = i.PartnerCode, ProcedureCode = procedureCode,
        DunningLevel = i.DunningLevel, PreviousLevel = i.PreviousLevel, OverdueAmount = i.OverdueAmount, CurrencySummary = i.CurrencySummary,
        OpenItemCount = i.OpenItemCount, OldestDueDate = i.OldestDueDate, DaysOverdue = i.DaysOverdue, ActionType = i.ActionType,
        ChargeAmount = i.ChargeAmount, ChargeDocumentUuid = i.ChargeDocumentUuid, ChargeDocumentNumber = i.ChargeDocumentNumber,
        SentTo = i.SentTo, SentAt = i.SentAt, Subject = i.Subject, Body = i.Body,
        InvoiceUuids = JsonSerializer.Deserialize<List<Guid>>(i.InvoiceUuids ?? "[]") ?? [],
        Status = i.Status, SkipReason = i.SkipReason, FailureReason = i.FailureReason
    };

    private async Task<string> CustomerNameAsync(Guid org, Guid customer, CancellationToken ct)
    {
        if (_partners is not null)
        {
            var p = await _partners.GetAsync(org, customer, null, ct) ?? throw new NotFoundException("Business partner", customer);
            if (!p.IsCustomer) throw new BadRequestException($"{p.Name} is not a customer.");
            return p.Name;
        }
        return await _db.SalesInvoices.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.OrganizationId == org && i.PartnerId == customer).Select(i => i.PartnerName).FirstOrDefaultAsync(ct)
            ?? "(unknown customer)";
    }

    private async Task SaveAsync(CancellationToken ct, string? duplicateMessage = null)
    {
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The record was changed by someone else; reload and try again.");
        }
        catch (DbUpdateException) when (duplicateMessage is not null)
        {
            throw new ConflictException(duplicateMessage);
        }
    }

    private static Guid Org(ArApCaller caller) =>
        caller.OrganizationId == Guid.Empty
            ? throw new BadRequestException("Pick an organization first: dunning is one organization's books.")
            : caller.OrganizationId;
}
