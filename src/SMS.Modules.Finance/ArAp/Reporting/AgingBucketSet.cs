using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Finance.ArAp.Reporting;

/// <summary>
/// A48 RPT (D-12, BR-AG-005/006, contract §5) — the aging buckets. The caller gives positive, ascending boundaries in days
/// (<c>30,60,90,120</c> by default); "Current" (age ≤ 0) is always the first bucket and the last is open-ended, so the
/// default is Current, 1-30, 31-60, 61-90, 91-120, 120+. At least 2 boundaries, at most 12. A leading 0 (the FSD's
/// <c>[0,15,30,45,60]</c> spelling) is accepted and means the implicit Current boundary.
/// <para>Due-date basis (default): age = days past the due date, so Current = not yet due. Document-date basis: age =
/// days since the document date.</para>
/// </summary>
internal sealed class AgingBucketSet
{
    /// <summary>Internal form: the implicit 0 (Current) first.</summary>
    public static readonly int[] DefaultBoundaries = [0, 30, 60, 90, 120];
    public const int MaxBoundaries = 12;

    public const string DueDateBasis      = "DueDate";
    public const string DocumentDateBasis = "DocumentDate";

    internal const string TooFewMessage   = "At least 3 buckets are needed (2 boundaries).";
    internal const string AscendingMessage = "Bucket boundaries must be positive and ascending.";

    private AgingBucketSet(int[] boundaries)
    {
        Boundaries = boundaries;
        Labels     = BuildLabels(boundaries);
    }

    /// <summary>Internal form: 0 (Current) first, then the caller's boundaries.</summary>
    public int[]    Boundaries { get; }
    public string[] Labels     { get; }
    public int      Count      => Labels.Length;

    /// <summary>"0,15,30,45,60" (or null / blank = default). 400 on anything else.</summary>
    public static AgingBucketSet Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new AgingBucketSet(DefaultBoundaries);

        var parts = text.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var values = new List<int>(parts.Length);
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var v)) throw new BadRequestException(AscendingMessage);
            values.Add(v);
        }
        return From(values);
    }

    /// <summary>The caller's boundaries (without or with a leading 0); null / empty = default.</summary>
    public static AgingBucketSet From(IReadOnlyList<int>? boundaries)
    {
        if (boundaries is null || boundaries.Count == 0) return new AgingBucketSet(DefaultBoundaries);

        var given = boundaries[0] == 0 ? boundaries.Skip(1).ToList() : [.. boundaries];
        if (given.Count < 2) throw new BadRequestException(TooFewMessage);
        if (given.Count > MaxBoundaries)
            throw new BadRequestException($"At most {MaxBoundaries} bucket boundaries can be given.");
        if (given[0] <= 0) throw new BadRequestException(AscendingMessage);
        for (var i = 1; i < given.Count; i++)
            if (given[i] <= given[i - 1]) throw new BadRequestException(AscendingMessage);

        return new AgingBucketSet([0, .. given]);
    }

    /// <summary>The boundaries as the caller spells them (without the implicit 0).</summary>
    public int[] PublicBoundaries => Boundaries[1..];

    public static string NormalizeBasis(string? basis)
    {
        if (string.IsNullOrWhiteSpace(basis)) return DueDateBasis;
        var b = basis.Trim().Replace("-", "").Replace("_", "").Replace(" ", "").ToUpperInvariant();
        return b switch
        {
            "DUEDATE" or "DUE"                    => DueDateBasis,
            "DOCUMENTDATE" or "DOCUMENT" or "DOC" => DocumentDateBasis,
            _ => throw new BadRequestException($"Unknown basis '{basis}'. Allowed: DueDate, DocumentDate.")
        };
    }

    /// <summary>The age of an item in days on the basis (negative = not due yet on the due-date basis).</summary>
    public static int AgeOf(DateTime documentDate, DateTime dueDate, DateTime asOf, string basis) =>
        (asOf.Date - (basis == DocumentDateBasis ? documentDate.Date : dueDate.Date)).Days;

    /// <summary>The bucket index for an age: the first boundary it does not exceed, else the open-ended last bucket.</summary>
    public int IndexOf(int age)
    {
        for (var i = 0; i < Boundaries.Length; i++)
            if (age <= Boundaries[i]) return i;
        return Boundaries.Length;
    }

    private static string[] BuildLabels(int[] b)
    {
        var labels = new string[b.Length + 1];
        labels[0] = b[0] == 0 ? "Current" : $"0-{b[0]}";
        for (var i = 1; i < b.Length; i++) labels[i] = $"{b[i - 1] + 1}-{b[i]}";
        labels[b.Length] = $"{b[^1]}+";
        return labels;
    }
}

/// <summary>
/// A48 RPT (D-13) — the base currency each sub-ledger totals in: the sale base for receivables, the purchase base for
/// payables (A35), by its code. Falls back to the documents' own base code, then to "PKR" (the platform default), so a
/// report never fails for want of a currency setting.
/// </summary>
internal sealed class SubledgerBaseCurrency
{
    private readonly FinanceDbContext     _db;
    private readonly ICurrencyService?    _currency;
    private readonly IOrgCurrencyLookup?  _orgCurrencies;
    private readonly Dictionary<SubledgerSide, string> _cache = [];

    public SubledgerBaseCurrency(FinanceDbContext db, ICurrencyService? currency = null, IOrgCurrencyLookup? orgCurrencies = null)
    {
        _db            = db;
        _currency      = currency;
        _orgCurrencies = orgCurrencies;
    }

    public async Task<string> CodeAsync(SubledgerSide side, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(side, out var cached)) return cached;

        var org = _db.TenantContext.OrganizationId;
        string? code = null;
        try
        {
            if (_currency is not null && _orgCurrencies is not null)
            {
                var id = await _currency.GetBaseCurrencyIdAsync(org,
                    side == SubledgerSide.Receivable ? TransactionDomain.Sale : TransactionDomain.Purchase, ct);
                code = (await _orgCurrencies.GetAsync(org, id, ct))?.Code;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            code = null; // a missing currency setting is not a reason to fail a report
        }

        code ??= side == SubledgerSide.Receivable
            ? await _db.SalesInvoices.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.OrganizationId == org && i.BaseCurrencyCode != null)
                .OrderByDescending(i => i.Id).Select(i => i.BaseCurrencyCode).FirstOrDefaultAsync(ct)
            : await _db.Invoices.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.OrganizationId == org && i.BaseCurrencyCode != null)
                .OrderByDescending(i => i.Id).Select(i => i.BaseCurrencyCode).FirstOrDefaultAsync(ct);

        code = string.IsNullOrWhiteSpace(code) ? "PKR" : code.Trim();
        _cache[side] = code;
        return code;
    }

    /// <summary>
    /// Contract §0: a foreign document with no locked rate is converted at the rate on file for <paramref name="asOf"/>
    /// through <see cref="ICurrencyService"/> when it can be; else it stays 1:1 and is flagged <c>RateMissing</c>.
    /// One lookup per currency.
    /// </summary>
    public async Task<List<OpenItemRow>> ResolveRatesAsync(
        List<OpenItemRow> rows, SubledgerSide side, DateTime asOf, CancellationToken ct = default)
    {
        var baseCode = await CodeAsync(side, ct);
        var needing  = rows.Where(r => r.LockedRate is null && !SameCode(r.CurrencyCode, baseCode))
                           .Select(r => r.CurrencyCode).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (needing.Count == 0) return rows;

        var rates = new Dictionary<string, decimal?>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in needing) rates[code] = await CurrentRateAsync(code, baseCode, asOf, ct);

        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.LockedRate is not null || SameCode(r.CurrencyCode, baseCode)) continue;
            rows[i] = rates[r.CurrencyCode] is { } rate ? r with { ResolvedRate = rate } : r with { RateMissing = true };
        }
        return rows;
    }

    /// <summary>
    /// REV-18 — the same rule for a total: <paramref name="lockedBase"/> (documents with a locked rate, already in base) plus
    /// each currency's sum of unlocked documents converted at the current rate for <paramref name="asOf"/> (1 for the base
    /// itself; 1:1 when no rate is on file).
    /// </summary>
    public async Task<decimal> TotalInBaseAsync(
        SubledgerSide side, DateTime asOf, decimal lockedBase, IEnumerable<(string Code, decimal Amount)> unlocked, CancellationToken ct = default)
    {
        var baseCode = await CodeAsync(side, ct);
        var total    = lockedBase;
        foreach (var (code, amount) in unlocked)
        {
            if (amount == 0m) continue;
            var rate = SameCode(code, baseCode) ? 1m : await CurrentRateAsync(code, baseCode, asOf, ct) ?? 1m;
            total += amount * rate;
        }
        return Math.Round(total, 2, MidpointRounding.AwayFromZero);
    }

    private readonly Dictionary<(string, string, DateTime), decimal?> _rates = [];

    private async Task<decimal?> CurrentRateAsync(string from, string to, DateTime asOf, CancellationToken ct)
    {
        var key = (from.Trim().ToUpperInvariant(), to.Trim().ToUpperInvariant(), asOf.Date);
        if (_rates.TryGetValue(key, out var known)) return known;
        return _rates[key] = await LookupRateAsync(from, to, asOf, ct);
    }

    private async Task<decimal?> LookupRateAsync(string from, string to, DateTime asOf, CancellationToken ct)
    {
        if (_currency is null || _orgCurrencies is null) return null;
        var org = _db.TenantContext.OrganizationId;
        try
        {
            var fromInfo = await _orgCurrencies.GetByCodeAsync(org, from, ct);
            var toInfo   = await _orgCurrencies.GetByCodeAsync(org, to, ct);
            if (fromInfo is null || toInfo is null) return null;

            const decimal probe = 1_000_000m; // converted amounts are rounded at the target's decimals
            var converted = await _currency.ConvertAsync(org, probe, fromInfo.CurrencyId, toInfo.CurrencyId,
                DateOnly.FromDateTime(asOf), ct);
            return converted.ConvertedAmount / probe;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static bool SameCode(string a, string b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
}
