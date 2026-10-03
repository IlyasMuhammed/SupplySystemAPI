using System.Globalization;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

using static SMS.Modules.Finance.Services.FinanceSetupFormat;

namespace SMS.Modules.Finance.Services;

/// <summary>
/// Exchange rates (SAP alignment S-1/S-2/S-4). Keyed by ISO code strings checked against the Lookups
/// currency catalog and stored upper-case; one live rate per pair per day (the filtered unique index
/// enforces it too); soft-deleted so a rate a document recorded can always be explained.
/// </summary>
internal sealed class ExchangeRateService : IExchangeRateService
{
    internal const int     MaxNotesLength = 300;
    internal const int     RateDecimals   = 8;
    /// <summary>decimal(18,8) holds ten digits before the point.</summary>
    internal const decimal MaxRate        = 9_999_999_999.99999999m;
    internal const string  ManualSource   = "MANUAL";

    private readonly FinanceDbContext      _db;
    private readonly ILookupsService       _lookups;
    private readonly IExchangeRateProvider _provider;
    private readonly TimeProvider          _clock;

    public ExchangeRateService(
        FinanceDbContext db, ILookupsService lookups, IExchangeRateProvider provider, TimeProvider? clock = null)
    {
        _db       = db;
        _lookups  = lookups;
        _provider = provider;
        _clock    = clock ?? TimeProvider.System;
    }

    private Guid     Org => _db.TenantContext.OrganizationId;
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    // ── Read ─────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ExchangeRateModel>> ListAsync(string? from, string? to, CancellationToken ct = default)
    {
        var org        = Org;
        var fromFilter = ExchangeRateProvider.Normalize(from);
        var toFilter   = ExchangeRateProvider.Normalize(to);

        var query = _db.ExchangeRates.AsNoTracking().Where(r => r.OrganizationId == org && !r.IsDelete);
        if (fromFilter is not null) query = query.Where(r => r.FromCurrencyCode == fromFilter);
        if (toFilter is not null)   query = query.Where(r => r.ToCurrencyCode == toFilter);

        var rows = await query
            .OrderByDescending(r => r.EffectiveDate)
            .ThenBy(r => r.FromCurrencyCode)
            .ThenBy(r => r.ToCurrencyCode)
            .ToListAsync(ct);

        return rows.Select(ToModel).ToList();
    }

    public async Task<ExchangeRateQuoteModel?> QuoteAsync(string? from, string? to, string? date, CancellationToken ct = default)
    {
        var fromCode = ExchangeRateProvider.Normalize(from)
                       ?? throw new BadRequestException("Say which currency to convert from (from=USD).");
        var toCode   = ExchangeRateProvider.Normalize(to)
                       ?? throw new BadRequestException("Say which currency to convert to (to=PKR).");
        var asOf     = ParseDate(date, "The date") ?? Now.Date;

        var quote = await _provider.GetRateAsync(fromCode, toCode, asOf, ct);
        return quote is null
            ? null
            : new ExchangeRateQuoteModel
            {
                FromCurrencyCode = quote.FromCurrencyCode,
                ToCurrencyCode   = quote.ToCurrencyCode,
                Rate             = quote.Rate,
                EffectiveDate    = Date(quote.EffectiveDate),
                Inverted         = quote.Inverted
            };
    }

    // ── Write ────────────────────────────────────────────────────────────────

    public async Task<ExchangeRateModel> CreateAsync(SaveExchangeRateRequest req, int userId, CancellationToken ct = default)
    {
        var input = Validate(req);
        var org   = Org;

        await EnsureDayFreeAsync(org, input, except: null, ct);

        var entity = new ExchangeRate
        {
            Uuid             = Guid.NewGuid(),
            OrganizationId   = org,
            FromCurrencyCode = input.From,
            ToCurrencyCode   = input.To,
            Rate             = input.Rate,
            EffectiveDate    = input.Day,
            Source           = ManualSource,
            Notes            = input.Notes,
            CreatedBy        = userId,
            CreatedDate      = Now
        };

        _db.ExchangeRates.Add(entity);
        await SaveAsync(org, entity, ct);
        return ToModel(entity);
    }

    public async Task<ExchangeRateModel> UpdateAsync(Guid uuid, SaveExchangeRateRequest req, int userId, CancellationToken ct = default)
    {
        var input  = Validate(req);
        var org    = Org;
        var entity = await FindLiveAsync(org, uuid, ct);

        await EnsureDayFreeAsync(org, input, except: entity.Uuid, ct);

        entity.FromCurrencyCode = input.From;
        entity.ToCurrencyCode   = input.To;
        entity.Rate             = input.Rate;
        entity.EffectiveDate    = input.Day;
        entity.Notes            = input.Notes;
        // A person typed this value, whatever wrote the row first.
        entity.Source           = ManualSource;
        entity.ModifiedBy       = userId;
        entity.ModifiedDate     = Now;

        await SaveAsync(org, entity, ct);
        return ToModel(entity);
    }

    public async Task DeleteAsync(Guid uuid, int userId, CancellationToken ct = default)
    {
        var entity = await FindLiveAsync(Org, uuid, ct);

        entity.IsDelete     = true;
        entity.ModifiedBy   = userId;
        entity.ModifiedDate = Now;

        await _db.SaveChangesAsync(ct);
    }

    // ── Rules ────────────────────────────────────────────────────────────────

    private sealed record ValidRate(string From, string To, decimal Rate, DateTime Day, string? Notes);

    private ValidRate Validate(SaveExchangeRateRequest? req)
    {
        if (req is null) throw new BadRequestException("Send the exchange rate to save.");

        var from = ResolveCurrency(req.FromCurrencyCode, "convert from");
        var to   = ResolveCurrency(req.ToCurrencyCode, "convert to");
        if (from == to)
            throw new BadRequestException($"A rate converts between two different currencies; both sides are {from}.");

        var rate = req.Rate;
        if (rate <= 0m)
            throw new BadRequestException("The rate must be greater than 0.");
        if (rate > MaxRate)
            throw new BadRequestException("The rate is too large; it can have at most ten digits before the decimal point.");
        if (decimal.Round(rate, RateDecimals) != rate)
            throw new BadRequestException(
                $"A rate can have at most {RateDecimals} decimals; {rate.ToString(CultureInfo.InvariantCulture)} has more.");

        var day = ParseDate(req.EffectiveDate, "The effective date")
                  ?? throw new BadRequestException("Say from which date the rate applies (effectiveDate, yyyy-MM-dd).");

        var notes = Clean(req.Notes);
        if (notes is not null && notes.Length > MaxNotesLength)
            throw new BadRequestException($"Notes can be at most {MaxNotesLength} characters.");

        return new ValidRate(from, to, rate, day, notes);
    }

    /// <summary>The code as the Lookups catalog knows it (any case in, upper-case out), or a 400.</summary>
    private string ResolveCurrency(string? code, string role)
    {
        var wanted = code?.Trim();
        if (string.IsNullOrEmpty(wanted))
            throw new BadRequestException($"Choose the currency to {role}.");

        var known = _lookups.GetCurrencies()
            .Select(c => c.Code?.Trim())
            .FirstOrDefault(c => !string.IsNullOrEmpty(c) && string.Equals(c, wanted, StringComparison.OrdinalIgnoreCase));

        return known?.ToUpperInvariant()
               ?? throw new BadRequestException($"'{wanted}' is not a currency in the Lookups catalog.");
    }

    private async Task EnsureDayFreeAsync(Guid org, ValidRate input, Guid? except, CancellationToken ct)
    {
        var (from, to, day) = (input.From, input.To, input.Day);

        var clash = await _db.ExchangeRates.AsNoTracking()
            .Where(r => r.OrganizationId == org
                     && !r.IsDelete
                     && r.FromCurrencyCode == from
                     && r.ToCurrencyCode == to
                     && r.EffectiveDate == day
                     && (except == null || r.Uuid != except))
            .Select(r => (decimal?)r.Rate)
            .FirstOrDefaultAsync(ct);

        if (clash is not null)
            throw new ConflictException(DayTakenMessage(from, to, day, clash.Value));
    }

    private static string DayTakenMessage(string from, string to, DateTime day, decimal? existing) =>
        $"There is already a {from} → {to} rate for {Date(day)}"
      + (existing is null ? string.Empty : $" ({Rate(existing.Value)})")
      + ". Change that one, or delete it first.";

    private async Task<ExchangeRate> FindLiveAsync(Guid org, Guid uuid, CancellationToken ct) =>
        await _db.ExchangeRates.FirstOrDefaultAsync(r => r.OrganizationId == org && r.Uuid == uuid && !r.IsDelete, ct)
        ?? throw new NotFoundException("That exchange rate does not exist in this organization, or was deleted.");

    private async Task SaveAsync(Guid org, ExchangeRate entity, CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The filtered unique index (one live rate per pair per day) refused it: another request saved a
            // rate for the same pair and day between this one's check and its save.
            var (from, to, day, uuid) = (entity.FromCurrencyCode, entity.ToCurrencyCode, entity.EffectiveDate, entity.Uuid);
            var taken = await _db.ExchangeRates.AsNoTracking()
                .AnyAsync(r => r.OrganizationId == org && !r.IsDelete && r.FromCurrencyCode == from
                            && r.ToCurrencyCode == to && r.EffectiveDate == day && r.Uuid != uuid, ct);
            if (taken) throw new ConflictException(DayTakenMessage(from, to, day, existing: null));
            throw;
        }
    }
}
