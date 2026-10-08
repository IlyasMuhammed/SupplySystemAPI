using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.Services;

/// <summary>
/// A35 P1-13 / P1-14 / A35-E-01 — puts an organization's currency data in place, idempotently:
/// <list type="number">
/// <item>org currencies: the 18 spec seed codes (§2.3) plus the organization's base / rate currencies and every code its
/// legacy rates use — each only when the global catalog knows it and the organization does not have it yet;</item>
/// <item>the rate currency's permanent SYSTEM 1.0 row (D-2): Tenancy's rate currency, else the organization's PKR;</item>
/// <item>the one-time conversion of the frozen legacy <c>finance.exchange_rates</c> into ranges (D-3) for every currency that
/// has no range yet, using only rows with the rate currency on one side (direct wins over reciprocal on the same day);
/// rows between two other currencies are reported in the log, never guessed;</item>
/// <item>then every <see cref="ICurrencyRatesReadyParticipant"/> (e.g. Demand's locked-document rate backfill).</item>
/// </list>
/// Runs at API start for every organization (after Finance migrates) and from Tenancy's provisioning call. On SQL Server one
/// organization is bootstrapped by one process at a time (session <c>sp_getapplock</c>), so two instances starting together
/// are safe: the second waits, then finds everything in place.
/// </summary>
internal sealed class CurrencyBootstrapper : IOrganizationProvisionedHandler
{
    internal sealed record Seed(string Code, string Name, string Symbol, int Decimals, decimal Rounding, string Position, int Order);

    /// <summary>Spec §2.3.</summary>
    internal static readonly IReadOnlyList<Seed> Seeds =
    [
        new("PKR", "Pakistani Rupee",   "₨",   2, 0.01m,  "before", 1),
        new("USD", "US Dollar",         "$",   2, 0.01m,  "before", 2),
        new("EUR", "Euro",              "€",   2, 0.01m,  "before", 3),
        new("GBP", "British Pound",     "£",   2, 0.01m,  "before", 4),
        new("SAR", "Saudi Riyal",       "﷼",   2, 0.01m,  "before", 5),
        new("AED", "UAE Dirham",        "د.إ", 2, 0.01m,  "before", 6),
        new("CNY", "Chinese Yuan",      "¥",   2, 0.01m,  "before", 7),
        new("JPY", "Japanese Yen",      "¥",   0, 1.00m,  "before", 8),
        new("BHD", "Bahraini Dinar",    "BD",  3, 0.001m, "before", 9),
        new("OMR", "Omani Rial",        "OMR", 3, 0.001m, "before", 10),
        new("CAD", "Canadian Dollar",   "C$",  2, 0.01m,  "before", 11),
        new("AUD", "Australian Dollar", "A$",  2, 0.01m,  "before", 12),
        new("INR", "Indian Rupee",      "₹",   2, 0.01m,  "before", 13),
        new("TRY", "Turkish Lira",      "₺",   2, 0.01m,  "before", 14),
        new("MYR", "Malaysian Ringgit", "RM",  2, 0.01m,  "before", 15),
        new("KWD", "Kuwaiti Dinar",     "KD",  3, 0.001m, "before", 16),
        new("QAR", "Qatari Riyal",      "QR",  2, 0.01m,  "before", 17),
        new("CHF", "Swiss Franc",       "CHF", 2, 0.01m,  "after",  18),
    ];

    private readonly FinanceDbContext                            _db;
    private readonly ILookupsService                             _lookups;
    private readonly IEnumerable<ICurrencyRatesReadyParticipant> _participants;
    private readonly IOrganizationCurrencyService?               _settings;
    private readonly ILogger                                     _log;

    public CurrencyBootstrapper(
        FinanceDbContext db, ILookupsService lookups, IEnumerable<ICurrencyRatesReadyParticipant> participants,
        IOrganizationCurrencyService? settings = null, ILogger<CurrencyBootstrapper>? logger = null)
    {
        _db           = db;
        _lookups      = lookups;
        _participants = participants;
        _settings     = settings;
        _log          = (ILogger?)logger ?? NullLogger.Instance;
    }

    internal int LockTimeoutMilliseconds { get; init; } = 120_000;

    public Task OnOrganizationProvisionedAsync(Guid organizationId, CancellationToken ct = default) =>
        EnsureAsync(organizationId, ct);

    /// <summary>Startup backfill: every organization; one failing never stops the others.</summary>
    public async Task EnsureForAllAsync(IEnumerable<Guid> organizationIds, CancellationToken ct = default)
    {
        foreach (var org in organizationIds)
        {
            try
            {
                await EnsureAsync(org, ct);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "A35 currency bootstrap failed for organization {Org}; the next start retries.", org);
            }
            _db.ChangeTracker.Clear();
        }
    }

    public async Task EnsureAsync(Guid org, CancellationToken ct = default)
    {
        var relational = _db.Database.IsRelational();
        var resource   = $"finance.currency_bootstrap/{org:N}";
        if (relational)
        {
            await _db.Database.OpenConnectionAsync(ct);
            await FinanceAppLock.AcquireAsync(_db, resource, "Session", LockTimeoutMilliseconds, ct);
        }

        try
        {
            await SeedCurrenciesAsync(org, ct);
            var rateCurrency = await EnsureRateCurrencyRowAsync(org, ct);
            if (rateCurrency is not null)
                await ConvertLegacyRatesAsync(org, rateCurrency, ct);

            foreach (var participant in _participants)
            {
                try
                {
                    await participant.OnCurrencyRatesReadyAsync(org, ct);
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "A35 currency participant {Participant} failed for organization {Org}; the next start retries.",
                        participant.GetType().Name, org);
                }
            }
        }
        finally
        {
            if (relational)
            {
                try { await FinanceAppLock.ReleaseSessionAsync(_db, resource, CancellationToken.None); }
                finally { await _db.Database.CloseConnectionAsync(); }
            }
        }
    }

    // ── 1. Org currencies ────────────────────────────────────────────────────

    private async Task SeedCurrenciesAsync(Guid org, CancellationToken ct)
    {
        var catalog = _lookups.GetCurrencies()
            .Where(c => !string.IsNullOrWhiteSpace(c.Code) && c.Code!.Trim().Length == 3)
            .GroupBy(c => c.Code!.Trim().ToUpperInvariant())
            .ToDictionary(g => g.Key, g => g.First());
        var byId = catalog.Values.ToDictionary(c => c.Id, c => c.Code!.Trim().ToUpperInvariant());

        var existing = await _db.OrgCurrencies.IgnoreQueryFilters().Where(c => c.OrganizationId == org)
            .Select(c => new { c.Code, c.CurrencyId, c.DisplayOrder }).ToListAsync(ct);
        var haveCodes = existing.Select(e => e.Code).ToHashSet();
        var haveIds   = existing.Select(e => e.CurrencyId).ToHashSet();
        var order     = existing.Count == 0 ? Seeds.Count : existing.Max(e => e.DisplayOrder);

        var wanted = Seeds.Select(s => s.Code).ToList();
        if (_settings is not null)
        {
            var s = await _settings.GetSettingsAsync(org, ct);
            foreach (var id in new[] { s.SaleBaseCurrencyId, s.PurchaseBaseCurrencyId, s.ServiceBaseCurrencyId, s.RateCurrencyId })
                if (byId.TryGetValue(id, out var code)) wanted.Add(code);
        }
        var legacy = await _db.ExchangeRates.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.OrganizationId == org && !r.IsDelete)
            .Select(r => new { r.FromCurrencyCode, r.ToCurrencyCode }).ToListAsync(ct);
        wanted.AddRange(legacy.SelectMany(l => new[] { l.FromCurrencyCode.Trim().ToUpperInvariant(), l.ToCurrencyCode.Trim().ToUpperInvariant() }));

        var added = 0;
        foreach (var code in wanted.Distinct())
        {
            if (haveCodes.Contains(code) || !catalog.TryGetValue(code, out var cat) || haveIds.Contains(cat.Id)) continue;
            if (!code.All(ch => ch is >= 'A' and <= 'Z')) continue;

            var seed   = Seeds.FirstOrDefault(s => s.Code == code);
            var symbol = (seed?.Symbol ?? cat.Symbol?.Trim() ?? code);
            if (symbol.Length > 5) symbol = symbol[..5];
            var name   = string.IsNullOrWhiteSpace(cat.Name) ? seed?.Name ?? code : cat.Name.Trim();
            if (name.Length > 60) name = name[..60];

            _db.OrgCurrencies.Add(new OrgCurrency
            {
                OrganizationId = org,
                CurrencyId     = cat.Id,
                Code           = code,
                Name           = name,
                Symbol         = symbol,
                DecimalPlaces  = seed?.Decimals ?? CurrencyConventions.DefaultDecimalPlaces,
                Rounding       = seed?.Rounding ?? 0.01m,
                SymbolPosition = seed?.Position ?? CurrencyConventions.SymbolBefore,
                IsActive       = true,
                DisplayOrder   = seed is not null && existing.Count == 0 ? seed.Order : ++order,
                CreatedDate    = DateTime.UtcNow
            });
            haveCodes.Add(code);
            haveIds.Add(cat.Id);
            added++;
        }

        if (added > 0)
        {
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("A35: {Count} currencies added to organization {Org}.", added, org);
        }

        var missing = Seeds.Select(s => s.Code).Where(c => !catalog.ContainsKey(c)).ToList();
        if (missing.Count > 0)
            _log.LogWarning("A35: the global currency catalog lacks {Codes}; organization {Org} did not get them.", string.Join(", ", missing), org);
    }

    // ── 2. Rate currency row ─────────────────────────────────────────────────

    private async Task<OrgCurrency?> EnsureRateCurrencyRowAsync(Guid org, CancellationToken ct)
    {
        var currencies = _db.OrgCurrencies.IgnoreQueryFilters().AsNoTracking().Where(c => c.OrganizationId == org);

        var system = await _db.CurrencyRates.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.OrganizationId == org && r.Source == CurrencyConventions.SourceSystem)
            .OrderBy(r => r.Id).FirstOrDefaultAsync(ct);
        if (system is not null)
            return await currencies.FirstOrDefaultAsync(c => c.CurrencyId == system.CurrencyId, ct);

        OrgCurrency? rc = null;
        if (_settings is not null)
        {
            var id = (await _settings.GetSettingsAsync(org, ct)).RateCurrencyId;
            if (id != Guid.Empty) rc = await currencies.FirstOrDefaultAsync(c => c.CurrencyId == id, ct);
        }
        rc ??= await currencies.FirstOrDefaultAsync(c => c.Code == CurrencyConventions.FallbackCurrencyCode, ct);
        if (rc is null)
        {
            _log.LogWarning("A35: organization {Org} has no resolvable rate currency (no settings, no PKR); no rates seeded.", org);
            return null;
        }

        _db.CurrencyRates.Add(new CurrencyRate
        {
            OrganizationId = org,
            CurrencyId     = rc.CurrencyId,
            CurrencyCode   = rc.Code,
            Rate           = 1m,
            InverseRate    = 1m,
            EffectiveFrom  = CurrencyConventions.SystemStart,
            EffectiveTo    = CurrencyConventions.OpenEnd,
            Source         = CurrencyConventions.SourceSystem,
            Notes          = "Rate currency (always 1)",
            CreatedDate    = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(ct);
        return rc;
    }

    // ── 3. Legacy rates → ranges ─────────────────────────────────────────────

    private async Task ConvertLegacyRatesAsync(Guid org, OrgCurrency rateCurrency, CancellationToken ct)
    {
        var legacy = await _db.ExchangeRates.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.OrganizationId == org && !r.IsDelete).ToListAsync(ct);
        if (legacy.Count == 0) return;

        var r = rateCurrency.Code;
        var usable  = new Dictionary<string, Dictionary<DateOnly, (decimal Rate, bool Direct, Guid Uuid)>>();
        var skipped = 0;

        foreach (var row in legacy)
        {
            var from = row.FromCurrencyCode.Trim().ToUpperInvariant();
            var to   = row.ToCurrencyCode.Trim().ToUpperInvariant();
            string x; decimal rate; bool direct;
            if (to == r && from != r)      { x = from; rate = row.Rate; direct = true; }
            else if (from == r && to != r) { x = to;   rate = row.Rate <= 0m ? 0m : 1m / row.Rate; direct = false; }
            else { skipped++; continue; }

            rate = CurrencyConventions.RoundRate(rate);
            if (rate < CurrencyRateService.MinRate || rate > CurrencyRateService.MaxRate) { skipped++; continue; }

            var day = DateOnly.FromDateTime(row.EffectiveDate);
            if (!usable.TryGetValue(x, out var days)) usable[x] = days = new();
            if (!days.TryGetValue(day, out var have) || (direct && !have.Direct))
                days[day] = (rate, direct, row.Uuid);
        }

        if (skipped > 0)
            _log.LogWarning(
                "A35: {Count} legacy exchange rates of organization {Org} do not involve its rate currency {Rc} (or are out of range) and were not converted; enter them under Settings → Exchange Rates.",
                skipped, org, r);

        var currencies = await _db.OrgCurrencies.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.OrganizationId == org).ToDictionaryAsync(c => c.Code, ct);
        var converted = 0;

        foreach (var (code, days) in usable)
        {
            if (!currencies.TryGetValue(code, out var currency))
            {
                _log.LogWarning("A35: legacy rates for {Code} of organization {Org} skipped — not a currency of the organization.", code, org);
                continue;
            }
            var currencyId = currency.CurrencyId;
            if (await _db.CurrencyRates.IgnoreQueryFilters().AnyAsync(x => x.OrganizationId == org && x.CurrencyId == currencyId, ct))
                continue;   // already converted (or entered by hand): never touched again

            var ordered = days.OrderBy(d => d.Key).ToList();
            for (var i = 0; i < ordered.Count; i++)
            {
                var (day, (rate, _, uuid)) = (ordered[i].Key, ordered[i].Value);
                _db.CurrencyRates.Add(new CurrencyRate
                {
                    OrganizationId = org,
                    CurrencyId     = currencyId,
                    CurrencyCode   = code,
                    Rate           = rate,
                    InverseRate    = CurrencyRateService.Inverse(rate),
                    EffectiveFrom  = day,
                    EffectiveTo    = i + 1 < ordered.Count ? ordered[i + 1].Key.AddDays(-1) : CurrencyConventions.OpenEnd,
                    Source         = CurrencyConventions.SourceManual,
                    Notes          = $"Converted from legacy exchange rate {uuid.ToString().ToUpperInvariant()[..8]}",
                    CreatedDate    = DateTime.UtcNow
                });
                converted++;
            }
            await _db.SaveChangesAsync(ct);
        }

        if (converted > 0)
            _log.LogInformation("A35: {Count} legacy exchange rates of organization {Org} converted into ranges against {Rc}.",
                converted, org, r);
    }

    internal static string Day(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
