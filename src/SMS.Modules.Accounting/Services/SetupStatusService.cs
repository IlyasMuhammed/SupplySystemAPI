using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Accounting.Services;

/// <summary>D-16 complete refused: what is still missing (409; the controller returns the list too).</summary>
public sealed class SetupIncompleteException : ConflictException
{
    public IReadOnlyList<string> Unmet { get; }

    public SetupIncompleteException(IReadOnlyList<string> unmet)
        : base("The general ledger setup cannot be completed yet: " + string.Join(" ", unmet)) => Unmet = unmet;
}

/// <summary>
/// D-16 / D-17 — the organization's GL setup status (CoaSetupStatuses). <see cref="RecomputeAsync"/> is the hook every
/// setup action calls after committing: it refreshes the derived flags and moves the stored status to the furthest
/// consecutive satisfied step (never past a missing one). Complete is terminal until a reset (D-30).
/// </summary>
public interface ISetupStatusService
{
    /// <summary>GET status — initializes the organization (D-15) and recomputes first.</summary>
    Task<SetupStatusModel> GetAsync(AccountingCaller caller, CancellationToken ct = default);

    /// <summary>POST complete — BR-MG-003; throws <see cref="SetupIncompleteException"/> listing what is missing.</summary>
    Task<SetupStatusModel> CompleteAsync(AccountingCaller caller, CancellationToken ct = default);

    /// <summary>POST reset — D-17; 409 "Cannot reset after journal entries posted." when a non-opening journal exists.</summary>
    Task<SetupStatusModel> ResetAsync(AccountingCaller caller, CancellationToken ct = default);

    /// <summary>GET history — newest first.</summary>
    Task<IReadOnlyList<SetupHistoryItemModel>> HistoryAsync(AccountingCaller caller, int limit = 100, CancellationToken ct = default);

    /// <summary>
    /// Refreshes AccountsCreatedCount / FiscalYearConfigured / DefaultsConfigured from the data (the opening balance flags
    /// are set by the opening balance service) and the stored status; publishes CoaSetupStatusChangedEvent when it moved.
    /// Call after SaveChanges of every setup action. Returns the new status.
    /// </summary>
    Task<string> RecomputeAsync(Guid organizationId, int? userId = null, string? userName = null, CancellationToken ct = default);

    /// <summary>The stored status (NotStarted when the organization has no row yet). Used by the [RequiresGlSetup] gate.</summary>
    Task<string> GetStatusCodeAsync(Guid organizationId, CancellationToken ct = default);

    /// <summary>True when any journal entry other than the opening balance exists (D-10: blocks reset; none in A42).</summary>
    Task<bool> HasPostedJournalsAsync(Guid organizationId, CancellationToken ct = default);
}

internal sealed class SetupStatusService : ISetupStatusService
{
    internal const string ResetRefusal = "Cannot reset after journal entries posted.";

    internal static readonly (int Step, string Title)[] StepTitles =
    [
        (1, "Choose Template"), (2, "Review & Customize Accounts"), (3, "Set Up Fiscal Year"),
        (4, "Configure Default Accounts"), (5, "Enter Opening Balances"), (6, "Review & Complete"),
    ];

    private readonly AccountingDbContext _db;
    private readonly IAccountingInitializer _init;
    private readonly IAccountingEventPublisher _events;

    public SetupStatusService(AccountingDbContext db, IAccountingInitializer init, IAccountingEventPublisher events)
    {
        _db = db;
        _init = init;
        _events = events;
    }

    private Task<CoaSetupStatus> LoadAsync(Guid org, CancellationToken ct) =>
        _db.CoaSetupStatuses.IgnoreQueryFilters().SingleAsync(s => s.OrganizationId == org, ct);

    public async Task<SetupStatusModel> GetAsync(AccountingCaller caller, CancellationToken ct = default)
    {
        caller.Require(caller.CanView);
        await RecomputeAsync(caller.OrganizationId, caller.UserIdOrNull, caller.UserName, ct);
        return await ToModelAsync(await LoadAsync(caller.OrganizationId, ct), ct);
    }

    public async Task<string> GetStatusCodeAsync(Guid organizationId, CancellationToken ct = default) =>
        await _db.CoaSetupStatuses.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.OrganizationId == organizationId).Select(s => s.Status).FirstOrDefaultAsync(ct)
        ?? SetupStatuses.NotStarted;

    public Task<bool> HasPostedJournalsAsync(Guid organizationId, CancellationToken ct = default) =>
        _db.JournalEntries.IgnoreQueryFilters()
            .AnyAsync(j => j.OrganizationId == organizationId && j.EntryType != JournalEntryTypes.OpeningBalance, ct);

    public async Task<string> RecomputeAsync(Guid organizationId, int? userId = null, string? userName = null, CancellationToken ct = default)
    {
        await _init.EnsureAsync(organizationId, ct);
        var status = await LoadAsync(organizationId, ct);
        var old = status.Status;

        status.AccountsCreatedCount = await _db.ChartOfAccounts.IgnoreQueryFilters()
            .CountAsync(a => a.OrganizationId == organizationId && a.IsActive, ct);
        status.FiscalYearConfigured = await _db.FiscalYears.IgnoreQueryFilters()
            .AnyAsync(y => y.OrganizationId == organizationId, ct);
        status.DefaultsConfigured = await RequiredDefaultsAssignedAsync(organizationId, ct);

        if (old != SetupStatuses.Complete) status.Status = Derive(status);

        if (_db.ChangeTracker.HasChanges()) await _db.SaveChangesAsync(ct);
        if (status.Status != old)
            await _events.PublishAsync(new CoaSetupStatusChangedEvent(organizationId, old, status.Status, null, userId, userName), ct);
        return status.Status;
    }

    /// <summary>D-24 — every required default points at an active leaf account of the organization.</summary>
    private Task<bool> RequiredDefaultsAssignedAsync(Guid org, CancellationToken ct) =>
        _db.AccountingDefaults.IgnoreQueryFilters()
            .Where(d => d.OrganizationId == org && d.IsRequired)
            .AllAsync(d => d.AccountId != null && _db.ChartOfAccounts.IgnoreQueryFilters()
                .Any(a => a.Id == d.AccountId && a.OrganizationId == org && a.IsActive && !a.IsGroup), ct);

    /// <summary>D-16 — the furthest consecutive satisfied step (Complete only through <see cref="CompleteAsync"/>).</summary>
    internal static string Derive(CoaSetupStatus s)
    {
        if (s.AccountsCreatedCount <= 0) return SetupStatuses.NotStarted;
        if (!s.FiscalYearConfigured) return SetupStatuses.AccountsCreated;
        if (!s.DefaultsConfigured) return SetupStatuses.FiscalYearSet;
        if (!s.OpeningBalancesEntered) return SetupStatuses.DefaultsSet;
        return SetupStatuses.BalancesEntered;
    }

    /// <summary>BR-MG-003 (+ §9.3: balanced or the difference acknowledged when posting).</summary>
    internal static List<string> Unmet(CoaSetupStatus s)
    {
        var unmet = new List<string>();
        if (s.AccountsCreatedCount <= 0) unmet.Add("Create the chart of accounts.");
        if (!s.FiscalYearConfigured) unmet.Add("Set up a fiscal year.");
        if (!s.DefaultsConfigured) unmet.Add("Assign every required accounting default.");
        if (!s.OpeningBalancesEntered) unmet.Add("Post or skip the opening balances.");
        else if (!s.OpeningBalancesBalanced) unmet.Add("The opening balances must balance.");
        return unmet;
    }

    public async Task<SetupStatusModel> CompleteAsync(AccountingCaller caller, CancellationToken ct = default)
    {
        caller.Require(caller.CanManageSetup);
        var org = caller.OrganizationId;
        await RecomputeAsync(org, caller.UserIdOrNull, caller.UserName, ct);
        var status = await LoadAsync(org, ct);
        if (status.Status == SetupStatuses.Complete) return await ToModelAsync(status, ct);

        var unmet = Unmet(status);
        if (unmet.Count > 0) throw new SetupIncompleteException(unmet);

        var old = status.Status;
        status.Status = SetupStatuses.Complete;
        status.SetupCompletedAt = DateTime.UtcNow;
        status.SetupCompletedBy = caller.UserIdOrNull;
        status.SetupCompletedByName = caller.UserName;
        await _db.SaveChangesAsync(ct);

        await _events.PublishAsync(new CoaSetupStatusChangedEvent(org, old, SetupStatuses.Complete, null, caller.UserIdOrNull, caller.UserName), ct);
        await _events.PublishAsync(new CoaSetupCompletedEvent(org, status.SetupCompletedAt.Value, caller.UserIdOrNull,
            status.AccountsCreatedCount, caller.UserIdOrNull, caller.UserName), ct);
        return await ToModelAsync(status, ct);
    }

    public async Task<SetupStatusModel> ResetAsync(AccountingCaller caller, CancellationToken ct = default)
    {
        caller.Require(caller.CanManageSetup);
        var org = caller.OrganizationId;
        await _init.EnsureAsync(org, ct);
        if (await HasPostedJournalsAsync(org, ct)) throw new ConflictException(ResetRefusal);

        var status = await LoadAsync(org, ct);
        var old = status.Status;

        await _db.InTransactionAsync(async () =>
        {
            await DeleteAsync(_db.ExternalSystemMappings.IgnoreQueryFilters().Where(m => m.OrganizationId == org), ct);
            await DeleteAsync(_db.OpeningBalanceEntries.IgnoreQueryFilters().Where(e => e.OrganizationId == org), ct);
            await DeleteAsync(_db.JournalEntryLines.IgnoreQueryFilters().Where(l => l.OrganizationId == org), ct);
            await DeleteAsync(_db.JournalEntries.IgnoreQueryFilters().Where(j => j.OrganizationId == org), ct);
            // A47 — the journal engine's rows reference the fiscal years and accounts deleted below (system types are re-seeded on demand).
            await DeleteAsync(_db.ReconcileGroups.IgnoreQueryFilters().Where(g => g.OrganizationId == org), ct);
            await DeleteAsync(_db.JournalEntrySequences.IgnoreQueryFilters().Where(s => s.OrganizationId == org), ct);
            await DeleteAsync(_db.JournalTypes.IgnoreQueryFilters().Where(t => t.OrganizationId == org), ct);
            await ClearDefaultsAsync(org, ct);
            await DeleteAsync(_db.FiscalPeriods.IgnoreQueryFilters().Where(p => p.OrganizationId == org), ct);
            await DeleteAsync(_db.FiscalYears.IgnoreQueryFilters().Where(y => y.OrganizationId == org), ct);
            await DeleteAsync(_db.ChartOfAccounts.IgnoreQueryFilters().Where(a => a.OrganizationId == org), ct);

            status.Status = SetupStatuses.NotStarted;
            status.TemplateId = null;
            status.TemplateAppliedAt = null;
            status.AccountsCreatedCount = 0;
            status.FiscalYearConfigured = false;
            status.DefaultsConfigured = false;
            status.OpeningBalancesEntered = false;
            status.OpeningBalancesBalanced = false;
            status.OpeningBalancesPostedAt = null;
            status.SetupCompletedAt = null;
            status.SetupCompletedBy = null;
            status.SetupCompletedByName = null;
            status.ConversionDate = null;
            await _db.SaveChangesAsync(ct);
        }, ct);

        await _events.PublishAsync(new CoaSetupStatusChangedEvent(org, old, SetupStatuses.NotStarted, "Setup reset",
            caller.UserIdOrNull, caller.UserName), ct);
        return await ToModelAsync(status, ct);
    }

    /// <summary>A set-based delete on SQL Server (one statement — the self-referencing chart deletes in one go); the
    /// in-memory test provider loads and removes.</summary>
    private async Task DeleteAsync<T>(IQueryable<T> rows, CancellationToken ct) where T : class
    {
        if (_db.Database.IsRelational())
        {
            await rows.ExecuteDeleteAsync(ct);
            // Rows of these sets the context still tracks are gone now.
            foreach (var entry in _db.ChangeTracker.Entries<T>().ToList()) entry.State = EntityState.Detached;
            return;
        }
        _db.RemoveRange(await rows.ToListAsync(ct));
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>D-17 — the default rows stay; their accounts are cleared.</summary>
    private async Task ClearDefaultsAsync(Guid org, CancellationToken ct)
    {
        var assigned = _db.AccountingDefaults.IgnoreQueryFilters().Where(d => d.OrganizationId == org && d.AccountId != null);
        if (_db.Database.IsRelational())
        {
            var now = DateTime.UtcNow;
            await assigned.ExecuteUpdateAsync(s => s.SetProperty(d => d.AccountId, (int?)null).SetProperty(d => d.ModifiedAt, now), ct);
            foreach (var entry in _db.ChangeTracker.Entries<AccountingDefault>().ToList()) entry.State = EntityState.Detached;
            return;
        }
        foreach (var d in await assigned.ToListAsync(ct)) d.AccountId = null;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<SetupHistoryItemModel>> HistoryAsync(AccountingCaller caller, int limit = 100, CancellationToken ct = default)
    {
        caller.Require(caller.CanView);
        limit = Math.Clamp(limit, 1, 500);
        return await _db.GlSetupHistory.IgnoreQueryFilters().AsNoTracking()
            .Where(h => h.OrganizationId == caller.OrganizationId)
            .OrderByDescending(h => h.At).ThenByDescending(h => h.Id)
            .Take(limit)
            .Select(h => new SetupHistoryItemModel { At = h.At, EventType = h.EventType, Summary = h.Summary, UserName = h.UserName })
            .ToListAsync(ct);
    }

    private async Task<SetupStatusModel> ToModelAsync(CoaSetupStatus s, CancellationToken ct)
    {
        var template = s.TemplateId is { } templateId
            ? await _db.CoaTemplates.IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.Id == templateId).Select(t => new { t.Uuid, t.TemplateName }).FirstOrDefaultAsync(ct)
            : null;

        var done = new Dictionary<int, bool>
        {
            [1] = SetupStatuses.Rank(s.Status) >= SetupStatuses.Rank(SetupStatuses.AccountsCreated),
            [2] = s.AccountsCreatedCount > 0,
            [3] = s.FiscalYearConfigured,
            [4] = s.DefaultsConfigured,
            [5] = s.OpeningBalancesEntered,
            [6] = s.Status == SetupStatuses.Complete,
        };

        return new SetupStatusModel
        {
            Status                  = s.Status,
            TemplateUuid            = template?.Uuid,
            TemplateName            = template?.TemplateName,
            TemplateAppliedAt       = s.TemplateAppliedAt,
            AccountsCreatedCount    = s.AccountsCreatedCount,
            FiscalYearConfigured    = s.FiscalYearConfigured,
            DefaultsConfigured      = s.DefaultsConfigured,
            OpeningBalancesEntered  = s.OpeningBalancesEntered,
            OpeningBalancesBalanced = s.OpeningBalancesBalanced,
            SetupCompletedAt        = s.SetupCompletedAt,
            SetupCompletedByName    = s.SetupCompletedByName,
            ConversionDate          = s.ConversionDate,
            NextStep                = StepTitles.Select(x => x.Step).FirstOrDefault(step => !done[step]) is var next and > 0 ? next : 6,
            Steps                   = StepTitles.Select(x => new SetupStepModel { Step = x.Step, Title = x.Title, Done = done[x.Step] }).ToList(),
        };
    }
}
