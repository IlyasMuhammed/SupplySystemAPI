using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Accounting.Data;
using SMS.Modules.Accounting.Domain;
using SMS.Modules.Accounting.Models;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Accounting.Services;

/// <summary>
/// A42 §7 / D-18 — fiscal years and their periods (BR-FP-001..006). A year gets one period per month of its range plus
/// period 13 (year-end adjustment, the year's last day). Period dates can be edited (custom calendars, e.g. 4-4-5) while
/// the year has no postings and no closed period: moving a period's start/end moves the neighbouring period's end/start,
/// and the monthly periods must always cover the year without gaps or overlaps.
/// </summary>
public interface IFiscalYearService
{
    Task<IReadOnlyList<FiscalYearModel>> ListAsync(AccountingCaller caller, CancellationToken ct = default);
    Task<FiscalYearModel> GetAsync(AccountingCaller caller, Guid yearUuid, CancellationToken ct = default);
    Task<IReadOnlyList<FiscalPeriodModel>> GetPeriodsAsync(AccountingCaller caller, Guid yearUuid, CancellationToken ct = default);
    Task<FiscalYearModel> CreateAsync(AccountingCaller caller, CreateFiscalYearRequest request, CancellationToken ct = default);
    Task<FiscalYearModel> UpdateAsync(AccountingCaller caller, Guid yearUuid, UpdateFiscalYearRequest request, CancellationToken ct = default);
    Task<FiscalYearModel> CloseAsync(AccountingCaller caller, Guid yearUuid, CancellationToken ct = default);
    Task<FiscalPeriodModel> UpdatePeriodAsync(AccountingCaller caller, Guid periodUuid, UpdateFiscalPeriodRequest request, CancellationToken ct = default);
}

internal sealed class FiscalYearService : IFiscalYearService
{
    internal const byte AdjustmentPeriodNumber = 13;
    internal const string AdjustmentPeriodName = "Year-End Adjustment";
    internal const string OverlapMessage = "Fiscal year dates overlap with an existing year.";
    internal const string CloseYearMessage = "All periods must be closed first.";
    internal const string ReopenClosedMessage = "A closed period cannot be reopened.";
    internal const string DatesFrozenMessage = "The dates cannot be changed: journal entries are posted in this fiscal year.";
    internal const string ClosedPeriodDatesMessage = "The dates cannot be changed: a period of this fiscal year is closed.";
    internal const string ClosedYearMessage = "A closed fiscal year cannot be changed.";
    internal const string CoverageMessage = "Periods must cover the fiscal year without gaps or overlaps.";

    private static readonly Regex CurrencyPattern = new("^[A-Za-z]{3}$", RegexOptions.Compiled);

    private readonly AccountingDbContext _db;
    private readonly IAccountingInitializer _init;
    private readonly ISetupStatusService _setup;
    private readonly IAccountingEventPublisher _events;
    private readonly FiscalBaseCurrency _baseCurrency;

    public FiscalYearService(AccountingDbContext db, IAccountingInitializer init, ISetupStatusService setup,
        IAccountingEventPublisher events, FiscalBaseCurrency baseCurrency)
    {
        _db = db;
        _init = init;
        _setup = setup;
        _events = events;
        _baseCurrency = baseCurrency;
    }

    // ── reads ──────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<FiscalYearModel>> ListAsync(AccountingCaller caller, CancellationToken ct = default)
    {
        caller.Require(caller.CanView);
        var org = caller.OrganizationId;
        var years = await _db.FiscalYears.IgnoreQueryFilters().AsNoTracking()
            .Where(y => y.OrganizationId == org)
            .Include(y => y.Periods)
            .OrderBy(y => y.StartDate)
            .ToListAsync(ct);
        var postings = await PostingDatesAsync(org, ct);
        return years.Select(y => ToModel(y, HasPostings(y, postings))).ToList();
    }

    public async Task<FiscalYearModel> GetAsync(AccountingCaller caller, Guid yearUuid, CancellationToken ct = default)
    {
        caller.Require(caller.CanView);
        var year = await LoadYearAsync(caller.OrganizationId, yearUuid, ct);
        return ToModel(year, await HasPostingsAsync(year, ct));
    }

    public async Task<IReadOnlyList<FiscalPeriodModel>> GetPeriodsAsync(AccountingCaller caller, Guid yearUuid, CancellationToken ct = default)
    {
        caller.Require(caller.CanView);
        var year = await LoadYearAsync(caller.OrganizationId, yearUuid, ct);
        return year.Periods.OrderBy(p => p.PeriodNumber).Select(ToModel).ToList();
    }

    // ── writes ─────────────────────────────────────────────────────────────────────────────────────────────────────

    public async Task<FiscalYearModel> CreateAsync(AccountingCaller caller, CreateFiscalYearRequest request, CancellationToken ct = default)
    {
        caller.Require(caller.CanManageSetup);
        ArgumentNullException.ThrowIfNull(request);
        var org = caller.OrganizationId;
        await _init.EnsureAsync(org, ct);

        ValidateRange(request.StartDate, request.EndDate);
        var name = NameOrDefault(request.FiscalYearName, request.StartDate, request.EndDate);
        var currency = await ResolveCurrencyAsync(org, request.ReportingCurrencyCode, ct);
        await EnsureNoOverlapAsync(org, request.StartDate, request.EndDate, null, ct);
        await EnsureUniqueNameAsync(org, name, null, ct);

        var year = new FiscalYear
        {
            OrganizationId        = org,
            FiscalYearName        = name,
            StartDate             = request.StartDate,
            EndDate               = request.EndDate,
            ReportingCurrencyCode = currency,
            CreatedBy             = caller.UserIdOrNull,
            ModifiedBy            = caller.UserIdOrNull,
        };
        foreach (var p in GeneratePeriods(org, request.StartDate, request.EndDate)) year.Periods.Add(p);

        await _db.InTransactionAsync(async () =>
        {
            _db.FiscalYears.Add(year);
            await _db.SaveChangesAsync(ct);
        }, ct);

        await _events.PublishAsync(new FiscalYearCreatedEvent(org, year.Uuid, year.FiscalYearName, year.StartDate, year.EndDate,
            caller.UserIdOrNull, caller.UserName), ct);
        await _setup.RecomputeAsync(org, caller.UserIdOrNull, caller.UserName, ct);
        return ToModel(year, false);
    }

    public async Task<FiscalYearModel> UpdateAsync(AccountingCaller caller, Guid yearUuid, UpdateFiscalYearRequest request, CancellationToken ct = default)
    {
        caller.Require(caller.CanManageSetup);
        ArgumentNullException.ThrowIfNull(request);
        var org = caller.OrganizationId;
        await _init.EnsureAsync(org, ct);
        var year = await LoadYearAsync(org, yearUuid, ct, track: true);
        if (year.IsClosed) throw new ConflictException(ClosedYearMessage);

        var start = request.StartDate ?? year.StartDate;
        var end = request.EndDate ?? year.EndDate;
        var datesChanged = start != year.StartDate || end != year.EndDate;
        var hasPostings = await HasPostingsAsync(year, ct);

        var name = string.IsNullOrWhiteSpace(request.FiscalYearName) ? year.FiscalYearName : request.FiscalYearName.Trim();
        if (name.Length > 100) throw new BadRequestException("The fiscal year name can be at most 100 characters.");
        var currency = string.IsNullOrWhiteSpace(request.ReportingCurrencyCode)
            ? year.ReportingCurrencyCode
            : NormalizeCurrency(request.ReportingCurrencyCode);

        if (datesChanged)
        {
            if (hasPostings) throw new ConflictException(DatesFrozenMessage);
            if (year.Periods.Any(p => p.Status != PeriodStatuses.Open))
                throw new ConflictException("The dates can only be changed while every period of the year is open.");
            ValidateRange(start, end);
            await EnsureNoOverlapAsync(org, start, end, year.Id, ct);
        }
        if (name != year.FiscalYearName) await EnsureUniqueNameAsync(org, name, year.Id, ct);

        await _db.InTransactionAsync(async () =>
        {
            year.FiscalYearName = name;
            year.ReportingCurrencyCode = currency;
            year.ModifiedBy = caller.UserIdOrNull;
            if (datesChanged)
            {
                year.StartDate = start;
                year.EndDate = end;
                _db.FiscalPeriods.RemoveRange(year.Periods);
                await _db.SaveChangesAsync(ct); // old period numbers must be gone before the new ones are inserted
                foreach (var p in GeneratePeriods(org, start, end)) { p.FiscalYearId = year.Id; _db.FiscalPeriods.Add(p); }
            }
            await _db.SaveChangesAsync(ct);
        }, ct);

        await _setup.RecomputeAsync(org, caller.UserIdOrNull, caller.UserName, ct);
        var fresh = await LoadYearAsync(org, yearUuid, ct);
        return ToModel(fresh, hasPostings);
    }

    public async Task<FiscalYearModel> CloseAsync(AccountingCaller caller, Guid yearUuid, CancellationToken ct = default)
    {
        caller.Require(caller.CanManageSetup);
        var org = caller.OrganizationId;
        await _init.EnsureAsync(org, ct);
        var year = await LoadYearAsync(org, yearUuid, ct, track: true);
        if (!year.IsClosed)
        {
            // BR-FP-006
            if (year.Periods.Count == 0 || year.Periods.Any(p => p.Status != PeriodStatuses.Closed))
                throw new ConflictException(CloseYearMessage);
            year.IsClosed = true;
            year.ClosedAt = DateTime.UtcNow;
            year.ClosedBy = caller.UserIdOrNull;
            year.ModifiedBy = caller.UserIdOrNull;
            await _db.SaveChangesAsync(ct);
            await _setup.RecomputeAsync(org, caller.UserIdOrNull, caller.UserName, ct);
        }
        return ToModel(year, await HasPostingsAsync(year, ct));
    }

    public async Task<FiscalPeriodModel> UpdatePeriodAsync(AccountingCaller caller, Guid periodUuid, UpdateFiscalPeriodRequest request, CancellationToken ct = default)
    {
        caller.Require(caller.CanManageSetup);
        ArgumentNullException.ThrowIfNull(request);
        var org = caller.OrganizationId;
        await _init.EnsureAsync(org, ct);

        var yearId = await _db.FiscalPeriods.IgnoreQueryFilters()
            .Where(p => p.OrganizationId == org && p.Uuid == periodUuid).Select(p => (int?)p.FiscalYearId).FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException("Fiscal period not found.");
        var year = await _db.FiscalYears.IgnoreQueryFilters().Include(y => y.Periods)
            .SingleAsync(y => y.OrganizationId == org && y.Id == yearId, ct);
        var period = year.Periods.Single(p => p.Uuid == periodUuid);
        if (year.IsClosed) throw new ConflictException(ClosedYearMessage);

        var oldStatus = period.Status;
        string? newStatus = null;
        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            newStatus = PeriodStatuses.All.FirstOrDefault(s => string.Equals(s, request.Status.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new BadRequestException("Status must be Open, Locked or Closed.");
            if (newStatus == oldStatus) newStatus = null;
        }

        // BR-FP-005 — Closed is final; Open ⇄ Locked; Open/Locked → Closed.
        if (newStatus is not null && oldStatus == PeriodStatuses.Closed) throw new ConflictException(ReopenClosedMessage);
        if (newStatus == PeriodStatuses.Closed)
        {
            // BR-FP-004 — no draft journal entries dated in the period (none in A42; A43 adds drafts).
            var hasDrafts = await _db.JournalEntries.IgnoreQueryFilters().AnyAsync(j => j.OrganizationId == org
                && j.Status == JournalStatuses.Draft && j.EntryDate >= period.StartDate && j.EntryDate <= period.EndDate, ct);
            if (hasDrafts) throw new ConflictException("The period has draft journal entries: post or delete them before closing it.");
        }

        if (request.PeriodName is not null)
        {
            var periodName = request.PeriodName.Trim();
            if (periodName.Length == 0) throw new BadRequestException("The period name is required.");
            if (periodName.Length > 100) throw new BadRequestException("The period name can be at most 100 characters.");
            period.PeriodName = periodName;
        }

        var newStart = request.StartDate ?? period.StartDate;
        var newEnd = request.EndDate ?? period.EndDate;
        if (newStart != period.StartDate || newEnd != period.EndDate)
        {
            if (await HasPostingsAsync(year, ct)) throw new ConflictException(DatesFrozenMessage);
            if (year.Periods.Any(p => p.Status == PeriodStatuses.Closed) || newStatus == PeriodStatuses.Closed)
                throw new ConflictException(ClosedPeriodDatesMessage);
            try
            {
                MovePeriodDates(year, period, newStart, newEnd);
                ValidatePeriods(year);
            }
            catch (BadRequestException)
            {
                // Leave nothing half-moved in this scope's change tracker.
                foreach (var entry in year.Periods.Select(p => _db.Entry(p)))
                {
                    entry.CurrentValues.SetValues(entry.OriginalValues);
                    entry.State = EntityState.Unchanged;
                }
                throw;
            }
        }

        if (newStatus is not null) period.Status = newStatus;
        period.ModifiedBy = caller.UserIdOrNull;
        await _db.SaveChangesAsync(ct);

        if (newStatus is not null)
            await _events.PublishAsync(new FiscalPeriodStatusChangedEvent(org, period.Uuid, period.PeriodName, oldStatus, newStatus,
                caller.UserIdOrNull, caller.UserName), ct);
        await _setup.RecomputeAsync(org, caller.UserIdOrNull, caller.UserName, ct);
        return ToModel(period);
    }

    // ── rules ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private static void ValidateRange(DateOnly start, DateOnly end)
    {
        if (start == default || end == default) throw new BadRequestException("The start and end dates are required.");
        if (end <= start) throw new BadRequestException("The end date must be after the start date.");
        if (end > start.AddMonths(12).AddDays(-1)) throw new BadRequestException("A fiscal year can be at most 12 months long.");
    }

    private static string NameOrDefault(string? name, DateOnly start, DateOnly end)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return start.Year == end.Year ? $"FY {start.Year}" : $"FY {start.Year}-{end.Year % 100:D2}";
        if (trimmed.Length > 100) throw new BadRequestException("The fiscal year name can be at most 100 characters.");
        return trimmed;
    }

    private static string NormalizeCurrency(string code)
    {
        var trimmed = code.Trim();
        if (!CurrencyPattern.IsMatch(trimmed)) throw new BadRequestException("The reporting currency must be a 3-letter currency code.");
        return trimmed.ToUpperInvariant();
    }

    private async Task<string> ResolveCurrencyAsync(Guid org, string? requested, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return NormalizeCurrency(requested);
        return await _baseCurrency.GetCodeAsync(org, ct)
            ?? throw new BadRequestException("Enter the reporting currency: the organization's base currency could not be found.");
    }

    private async Task EnsureNoOverlapAsync(Guid org, DateOnly start, DateOnly end, int? exceptId, CancellationToken ct)
    {
        // BR-FP-001
        var overlaps = await _db.FiscalYears.IgnoreQueryFilters().AnyAsync(y => y.OrganizationId == org
            && (exceptId == null || y.Id != exceptId) && y.StartDate <= end && start <= y.EndDate, ct);
        if (overlaps) throw new ConflictException(OverlapMessage);
    }

    private async Task EnsureUniqueNameAsync(Guid org, string name, int? exceptId, CancellationToken ct)
    {
        var taken = await _db.FiscalYears.IgnoreQueryFilters().AnyAsync(y => y.OrganizationId == org
            && (exceptId == null || y.Id != exceptId) && y.FiscalYearName == name, ct);
        if (taken) throw new ConflictException($"A fiscal year named \"{name}\" already exists.");
    }

    /// <summary>§7.4 — one period per month of the range (the last one ends on the year's end) + period 13 on the last day.</summary>
    internal static List<FiscalPeriod> GeneratePeriods(Guid org, DateOnly start, DateOnly end)
    {
        var periods = new List<FiscalPeriod>();
        for (var i = 0; ; i++)
        {
            var s = start.AddMonths(i);
            if (s > end || i >= 12) break;
            var e = start.AddMonths(i + 1).AddDays(-1);
            if (e > end) e = end;
            periods.Add(new FiscalPeriod
            {
                OrganizationId = org, PeriodNumber = (byte)(i + 1), StartDate = s, EndDate = e, Status = PeriodStatuses.Open,
                PeriodName = s.ToString("MMMM yyyy", CultureInfo.InvariantCulture),
            });
        }
        periods.Add(new FiscalPeriod
        {
            OrganizationId = org, PeriodNumber = AdjustmentPeriodNumber, StartDate = end, EndDate = end, Status = PeriodStatuses.Open,
            PeriodName = AdjustmentPeriodName,
        });
        return periods;
    }

    /// <summary>Moves a period's dates; a monthly period's neighbours follow so the year stays covered.</summary>
    private static void MovePeriodDates(FiscalYear year, FiscalPeriod period, DateOnly newStart, DateOnly newEnd)
    {
        if (period.PeriodNumber == AdjustmentPeriodNumber)
        {
            period.StartDate = newStart;
            period.EndDate = newEnd;
            return;
        }
        var monthly = year.Periods.Where(p => p.PeriodNumber != AdjustmentPeriodNumber).OrderBy(p => p.PeriodNumber).ToList();
        var index = monthly.IndexOf(period);
        if (newStart != period.StartDate)
        {
            if (index == 0)
                throw new BadRequestException($"The first period must start on the fiscal year's start date ({year.StartDate:yyyy-MM-dd}).");
            monthly[index - 1].EndDate = newStart.AddDays(-1);
            period.StartDate = newStart;
        }
        if (newEnd != period.EndDate)
        {
            if (index == monthly.Count - 1)
                throw new BadRequestException($"The last period must end on the fiscal year's end date ({year.EndDate:yyyy-MM-dd}).");
            monthly[index + 1].StartDate = newEnd.AddDays(1);
            period.EndDate = newEnd;
        }
    }

    /// <summary>BR-FP-002 — the monthly periods (in number order) cover the year exactly; period 13 lies inside the year.</summary>
    internal static void ValidatePeriods(FiscalYear year)
    {
        var monthly = year.Periods.Where(p => p.PeriodNumber != AdjustmentPeriodNumber).OrderBy(p => p.PeriodNumber).ToList();
        foreach (var p in year.Periods)
        {
            if (p.EndDate < p.StartDate)
                throw new BadRequestException($"Period {p.PeriodName} would end before it starts.");
            if (p.StartDate < year.StartDate || p.EndDate > year.EndDate)
                throw new BadRequestException("Period dates must fall within the fiscal year.");
        }
        if (monthly.Count == 0 || monthly[0].StartDate != year.StartDate || monthly[^1].EndDate != year.EndDate)
            throw new BadRequestException(CoverageMessage);
        for (var i = 1; i < monthly.Count; i++)
            if (monthly[i].StartDate != monthly[i - 1].EndDate.AddDays(1))
                throw new BadRequestException(CoverageMessage);
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<FiscalYear> LoadYearAsync(Guid org, Guid uuid, CancellationToken ct, bool track = false)
    {
        var query = _db.FiscalYears.IgnoreQueryFilters().Include(y => y.Periods).Where(y => y.OrganizationId == org && y.Uuid == uuid);
        if (!track) query = query.AsNoTracking();
        return await query.SingleOrDefaultAsync(ct) ?? throw new NotFoundException("Fiscal year not found.");
    }

    /// <summary>D-50 — a year "has postings" when a posted journal entry is dated inside it (the opening balance entry counts).</summary>
    private Task<bool> HasPostingsAsync(FiscalYear year, CancellationToken ct) =>
        _db.JournalEntries.IgnoreQueryFilters().AnyAsync(j => j.OrganizationId == year.OrganizationId
            && j.Status == JournalStatuses.Posted && j.EntryDate >= year.StartDate && j.EntryDate <= year.EndDate, ct);

    private async Task<List<DateOnly>> PostingDatesAsync(Guid org, CancellationToken ct) =>
        await _db.JournalEntries.IgnoreQueryFilters()
            .Where(j => j.OrganizationId == org && j.Status == JournalStatuses.Posted)
            .Select(j => j.EntryDate).Distinct().ToListAsync(ct);

    private static bool HasPostings(FiscalYear y, List<DateOnly> dates) => dates.Any(d => d >= y.StartDate && d <= y.EndDate);

    internal static FiscalYearModel ToModel(FiscalYear y, bool hasPostings) => new()
    {
        Uuid                  = y.Uuid,
        FiscalYearName        = y.FiscalYearName,
        StartDate             = y.StartDate,
        EndDate               = y.EndDate,
        IsClosed              = y.IsClosed,
        ClosedAt              = y.ClosedAt,
        ReportingCurrencyCode = y.ReportingCurrencyCode,
        HasPostings           = hasPostings,
        Periods               = y.Periods.OrderBy(p => p.PeriodNumber).Select(ToModel).ToList(),
    };

    internal static FiscalPeriodModel ToModel(FiscalPeriod p) => new()
    {
        Uuid         = p.Uuid,
        PeriodNumber = p.PeriodNumber,
        PeriodName   = p.PeriodName,
        StartDate    = p.StartDate,
        EndDate      = p.EndDate,
        Status       = p.Status,
        IsAdjustment = p.PeriodNumber == AdjustmentPeriodNumber,
    };
}

/// <summary>D-18 — Shared seam for A43 posting: the date lies in an Open period of an open fiscal year.</summary>
internal sealed class FiscalPeriodGuard : SMS.Shared.Common.IFiscalPeriodGuard
{
    private readonly AccountingDbContext _db;

    public FiscalPeriodGuard(AccountingDbContext db) => _db = db;

    public Task<bool> IsPostingAllowedAsync(Guid organizationId, DateOnly date, CancellationToken ct = default) =>
        _db.FiscalPeriods.IgnoreQueryFilters().AsNoTracking().AnyAsync(p => p.OrganizationId == organizationId
            && p.Status == PeriodStatuses.Open && p.StartDate <= date && date <= p.EndDate
            && !p.FiscalYear.IsClosed, ct);
}
