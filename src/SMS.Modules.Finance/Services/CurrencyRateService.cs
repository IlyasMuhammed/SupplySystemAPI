using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Finance.Services;

/// <summary>A35 C2 — api/currency-rates.</summary>
public interface ICurrencyRateService
{
    Task<IReadOnlyList<CurrencyRateModel>> ListAsync(Guid? currencyId, string? from, string? to, CancellationToken ct = default);
    Task<IReadOnlyList<CurrencyRateModel>> ActiveAsync(CancellationToken ct = default);
    Task<IReadOnlyList<CurrencyRateModel>> HistoryAsync(Guid currencyId, CancellationToken ct = default);
    Task<CurrencyRateModel> ForDateAsync(Guid currencyId, string? date, CancellationToken ct = default);
    Task<CurrencyRateInsertResult> InsertAsync(SaveCurrencyRateRequest? req, int userId, CancellationToken ct = default);
    Task<CurrencyRateModel> UpdateAsync(Guid id, SaveCurrencyRateRequest? req, int userId, CancellationToken ct = default);
}

/// <summary>
/// A35 C2 — exchange rates with date ranges (BR-C2-01..07, D-3). Reads and writes are limited to the caller's own
/// organization explicitly (a super admin included). Inserts and corrections of one currency run one at a time: on SQL
/// Server inside a SERIALIZABLE transaction holding <c>sp_getapplock</c> on (organization, currency), so two concurrent
/// inserts for the same day end as one 200 and one 409 (T-C2-10); the unique index (org, currency, from) backs it up.
/// </summary>
internal sealed class CurrencyRateService : ICurrencyRateService
{
    internal const decimal MinRate = 0.00000001m;
    internal const decimal MaxRate = 99_999_999.9999999999m;
    internal const int     MaxNotes = 200;

    private readonly FinanceDbContext              _db;
    private readonly IOrganizationCurrencyService? _settings;
    private readonly TimeProvider                  _clock;

    public CurrencyRateService(FinanceDbContext db, IOrganizationCurrencyService? settings = null, TimeProvider? clock = null)
    {
        _db       = db;
        _settings = settings;
        _clock    = clock ?? TimeProvider.System;
    }

    /// <summary>How long a save waits for another save of the same currency before giving up with 409.</summary>
    internal int LockTimeoutMilliseconds { get; init; } = 15_000;

    private Guid     Org   => _db.TenantContext.OrganizationId;
    private DateTime Now   => _clock.GetUtcNow().UtcDateTime;
    private DateOnly Today => DateOnly.FromDateTime(Now);

    private IQueryable<CurrencyRate> OwnRates(Guid org) =>
        _db.CurrencyRates.IgnoreQueryFilters().Where(r => r.OrganizationId == org);

    // ── Read ─────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<CurrencyRateModel>> ListAsync(Guid? currencyId, string? from, string? to, CancellationToken ct = default)
    {
        var org = Org;
        var q   = OwnRates(org).AsNoTracking();
        if (currencyId is Guid c) q = q.Where(r => r.CurrencyId == c);
        if (ParseDate(from, "From") is DateOnly f) q = q.Where(r => r.EffectiveTo >= f);
        if (ParseDate(to, "To") is DateOnly t)     q = q.Where(r => r.EffectiveFrom <= t);

        var rows = await q.OrderBy(r => r.CurrencyCode).ThenByDescending(r => r.EffectiveFrom).ToListAsync(ct);
        return await ToModelsAsync(org, rows, ct);
    }

    public async Task<IReadOnlyList<CurrencyRateModel>> ActiveAsync(CancellationToken ct = default)
    {
        var org  = Org;
        var open = CurrencyConventions.OpenEnd;
        var rows = await OwnRates(org).AsNoTracking().Where(r => r.EffectiveTo == open).OrderBy(r => r.CurrencyCode).ToListAsync(ct);
        return await ToModelsAsync(org, rows, ct);
    }

    public async Task<IReadOnlyList<CurrencyRateModel>> HistoryAsync(Guid currencyId, CancellationToken ct = default)
    {
        var org  = Org;
        var rows = await OwnRates(org).AsNoTracking().Where(r => r.CurrencyId == currencyId)
            .OrderByDescending(r => r.EffectiveFrom).ToListAsync(ct);
        return await ToModelsAsync(org, rows, ct);
    }

    public async Task<CurrencyRateModel> ForDateAsync(Guid currencyId, string? date, CancellationToken ct = default)
    {
        var org    = Org;
        var day    = ParseDate(date, "The date") ?? Today;
        var reader = new CurrencyRateReader(_db, _settings, org);
        var info   = await reader.RequireAsync(currencyId, day, ct);

        var row = info.Id == Guid.Empty ? null : await OwnRates(org).AsNoTracking().FirstOrDefaultAsync(r => r.Uuid == info.Id, ct);
        if (row is not null) return (await ToModelsAsync(org, [row], ct))[0];

        // The rate currency without a stored SYSTEM row: synthesised 1.0.
        var rc = await reader.RateCurrencyIdAsync(ct);
        return new CurrencyRateModel
        {
            CurrencyId = info.CurrencyId, CurrencyCode = info.CurrencyCode, Rate = 1m, InverseRate = 1m,
            EffectiveFrom = Fmt(info.EffectiveFrom), EffectiveTo = Fmt(info.EffectiveTo), IsCurrent = true,
            Source = info.Source, RateCurrencyId = rc, RateCurrencyCode = info.CurrencyCode
        };
    }

    // ── Write ────────────────────────────────────────────────────────────────

    public async Task<CurrencyRateInsertResult> InsertAsync(SaveCurrencyRateRequest? req, int userId, CancellationToken ct = default)
    {
        var org   = Org;
        var input = await ValidateAsync(org, req, ct);

        return await OneAtATimeAsync(org, input.Currency.CurrencyId, async () =>
        {
            var rows   = await OwnRates(org).Where(r => r.CurrencyId == input.Currency.CurrencyId).ToListAsync(ct);
            var open   = CurrencyConventions.OpenEnd;
            CurrencyRate? closed = null;

            var newTo = input.To ?? open;
            var current = rows.FirstOrDefault(r => r.EffectiveTo == open);
            if (input.To is null && current is not null && current.EffectiveFrom < input.From)
                closed = current;

            // The current row, when it starts before the new one, is closed to the day before; nothing else may overlap.
            var clash = rows.FirstOrDefault(r => r != closed && r.EffectiveFrom <= newTo && r.EffectiveTo >= input.From);
            if (clash is not null)
                throw new ConflictException(Clash(input.Currency.Code, clash));

            if (closed is not null)
            {
                closed.EffectiveTo  = input.From.AddDays(-1);
                closed.ModifiedBy   = userId;
                closed.ModifiedDate = Now;
            }

            var entity = new CurrencyRate
            {
                OrganizationId = org,
                CurrencyId     = input.Currency.CurrencyId,
                CurrencyCode   = input.Currency.Code,
                Rate           = input.Rate,
                InverseRate    = Inverse(input.Rate),
                EffectiveFrom  = input.From,
                EffectiveTo    = newTo,
                Source         = CurrencyConventions.SourceManual,
                Notes          = input.Notes,
                CreatedBy      = userId,
                CreatedDate    = Now
            };
            _db.CurrencyRates.Add(entity);
            await SaveAsync(input.Currency.Code, ct);

            var models = await ToModelsAsync(org, closed is null ? [entity] : [entity, closed], ct);
            return new CurrencyRateInsertResult { Rate = models[0], ClosedPrevious = closed is null ? null : models[1] };
        }, ct);
    }

    public async Task<CurrencyRateModel> UpdateAsync(Guid id, SaveCurrencyRateRequest? req, int userId, CancellationToken ct = default)
    {
        var org      = Org;
        var existing = await OwnRates(org).AsNoTracking().FirstOrDefaultAsync(r => r.Uuid == id, ct)
                       ?? throw new NotFoundException("That exchange rate does not exist in this organization.");
        if (existing.Source == CurrencyConventions.SourceSystem)
            throw new BadRequestException("Base currency rate cannot be modified: the rate currency is always 1.");

        if (req is not null) req.CurrencyId = existing.CurrencyId;
        var input = await ValidateAsync(org, req, ct);

        return await OneAtATimeAsync(org, existing.CurrencyId, async () =>
        {
            var rows   = await OwnRates(org).Where(r => r.CurrencyId == existing.CurrencyId).ToListAsync(ct);
            var entity = rows.Single(r => r.Uuid == id);
            var newTo  = input.To ?? CurrencyConventions.OpenEnd;

            var clash = rows.FirstOrDefault(r => r.Uuid != id && r.EffectiveFrom <= newTo && r.EffectiveTo >= input.From);
            if (clash is not null)
                throw new ConflictException(Clash(input.Currency.Code, clash));

            entity.Rate          = input.Rate;
            entity.InverseRate   = Inverse(input.Rate);
            entity.EffectiveFrom = input.From;
            entity.EffectiveTo   = newTo;
            entity.Notes         = input.Notes;
            entity.Source        = CurrencyConventions.SourceManual;
            entity.ModifiedBy    = userId;
            entity.ModifiedDate  = Now;
            await SaveAsync(input.Currency.Code, ct);

            return (await ToModelsAsync(org, [entity], ct))[0];
        }, ct);
    }

    // ── Rules ────────────────────────────────────────────────────────────────

    private sealed record ValidRate(OrgCurrency Currency, decimal Rate, DateOnly From, DateOnly? To, string? Notes);

    private async Task<ValidRate> ValidateAsync(Guid org, SaveCurrencyRateRequest? req, CancellationToken ct)
    {
        if (req is null) throw new BadRequestException("Send the exchange rate to save.");
        if (req.CurrencyId is not Guid currencyId || currencyId == Guid.Empty)
            throw new BadRequestException("Choose the currency the rate is for (currencyId).");

        var reader   = new CurrencyRateReader(_db, _settings, org);
        var currency = await reader.CurrencyAsync(currencyId, ct);
        var rc       = await reader.RateCurrencyIdAsync(ct);
        if (rc == currencyId)
            throw new BadRequestException(
                $"Base currency rate cannot be modified: {currency?.Code ?? "the rate currency"} is the organization's rate currency and is always 1.");
        if (currency is null || !currency.IsActive)
            throw new BadRequestException($"{currency?.Code ?? "The selected currency"} is not an active currency of this organization.");
        if (rc is null)
            throw new BadRequestException("This organization has no rate currency yet. Set its base currencies under Settings → Currency Configuration.");

        if (req.Rate <= 0m)
            throw new BadRequestException("Rate must be positive.");
        if (decimal.Round(req.Rate, CurrencyConventions.RateDecimals) != req.Rate)
            throw new BadRequestException(
                $"A rate can have at most {CurrencyConventions.RateDecimals} decimals; {req.Rate.ToString(CultureInfo.InvariantCulture)} has more.");
        if (req.Rate > MaxRate)
            throw new BadRequestException("The rate is too large; it can be at most 99,999,999.9999999999.");
        if (req.Rate < MinRate)
            throw new BadRequestException("The rate is too small; it must be at least 0.00000001 so its inverse can be stored.");

        var from = ParseDate(req.EffectiveFrom, "Effective from")
                   ?? throw new BadRequestException("Say from which date the rate applies (effectiveFrom, yyyy-MM-dd).");
        var to   = ParseDate(req.EffectiveTo, "Effective to");
        if (to is DateOnly t && t < from)
            throw new BadRequestException("Effective to must be on or after effective from.");

        var notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim();
        if (notes is not null && notes.Length > MaxNotes)
            throw new BadRequestException($"Notes can be at most {MaxNotes} characters.");

        return new ValidRate(currency, req.Rate, from, to, notes);
    }

    internal static decimal Inverse(decimal rate) => CurrencyConventions.RoundRate(1m / rate);

    private static string Clash(string code, CurrencyRate r) =>
        $"Rate already exists for this date ({code} {r.Rate.ToString(CultureInfo.InvariantCulture)} from {Fmt(r.EffectiveFrom)} to {Fmt(r.EffectiveTo)}).";

    internal static string Fmt(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal static DateOnly? ParseDate(string? value, string what)
    {
        var s = value?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        if (s.Length >= 10 && DateOnly.TryParseExact(s[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;
        throw new BadRequestException($"{what} must be a date as yyyy-MM-dd; '{s}' is not.");
    }

    private async Task SaveAsync(string code, CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The unique index (org, currency, effectiveFrom) refused a second row for the same start day.
            throw new ConflictException($"Rate already exists for this date ({code}). Another save got there first; reload and try again.");
        }
    }

    private async Task<T> OneAtATimeAsync<T>(Guid org, Guid currencyId, Func<Task<T>> work, CancellationToken ct)
    {
        if (!_db.Database.IsRelational()) return await work();

        if (_db.Database.CurrentTransaction is not null)
        {
            await FinanceAppLock.AcquireAsync(_db, LockResource(org, currencyId), "Transaction", LockTimeoutMilliseconds, ct);
            return await work();
        }

        var strategy = _db.Database.CreateExecutionStrategy();
        var attempt  = 0;
        return await strategy.ExecuteAsync(async () =>
        {
            if (attempt++ > 0) _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await FinanceAppLock.AcquireAsync(_db, LockResource(org, currencyId), "Transaction", LockTimeoutMilliseconds, ct);
            var result = await work();
            await tx.CommitAsync(ct);
            return result;
        });
    }

    internal static string LockResource(Guid org, Guid currencyId) => $"finance.currency_rates/{org:N}/{currencyId:N}";

    private async Task<List<CurrencyRateModel>> ToModelsAsync(Guid org, IReadOnlyList<CurrencyRate> rows, CancellationToken ct)
    {
        var reader = new CurrencyRateReader(_db, _settings, org);
        var rc     = await reader.RateCurrencyIdAsync(ct);
        var rcCode = rc is Guid g ? await reader.CodeAsync(g, ct) : null;
        var ids    = rows.Select(r => r.CurrencyId).Distinct().ToList();
        var names  = await _db.OrgCurrencies.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.OrganizationId == org && ids.Contains(c.CurrencyId))
            .ToDictionaryAsync(c => c.CurrencyId, c => c.Name, ct);

        return rows.Select(r => new CurrencyRateModel
        {
            Id = r.Uuid, CurrencyId = r.CurrencyId, CurrencyCode = r.CurrencyCode,
            CurrencyName = names.GetValueOrDefault(r.CurrencyId),
            Rate = r.Rate, InverseRate = r.InverseRate,
            EffectiveFrom = Fmt(r.EffectiveFrom), EffectiveTo = Fmt(r.EffectiveTo),
            IsCurrent = r.EffectiveTo == CurrencyConventions.OpenEnd,
            Source = r.Source, Notes = r.Notes, RateCurrencyId = rc, RateCurrencyCode = rcCode,
            CreatedAt = r.CreatedDate, CreatedBy = r.CreatedBy, ModifiedAt = r.ModifiedDate, ModifiedBy = r.ModifiedBy
        }).ToList();
    }
}

/// <summary>sp_getapplock for Finance work (A35). Returns only when granted; otherwise 409.</summary>
internal static class FinanceAppLock
{
    public static async Task AcquireAsync(FinanceDbContext db, string resource, string owner, int timeoutMs, CancellationToken ct)
    {
        var outcome = new SqlParameter("@outcome", SqlDbType.Int) { Direction = ParameterDirection.Output };
        await db.Database.ExecuteSqlRawAsync(
            "DECLARE @result int; "
          + $"EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = '{owner}', @LockTimeout = @timeout; "
          + "SET @outcome = @result;",
            [
                new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = resource },
                new SqlParameter("@timeout", SqlDbType.Int) { Value = timeoutMs },
                outcome
            ],
            ct);

        if (outcome.Value is not int granted || granted < 0)
            throw new ConflictException("Someone else is saving the same data right now, so nothing was saved. Try again in a moment.");
    }

    public static Task ReleaseSessionAsync(FinanceDbContext db, string resource, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(
            "IF APPLOCK_MODE('public', @resource, 'Session') <> 'NoLock' EXEC sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';",
            [new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = resource }],
            ct);
}
