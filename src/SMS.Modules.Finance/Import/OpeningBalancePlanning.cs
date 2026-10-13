using SMS.Shared.Common;

using static SMS.Modules.Finance.Import.FinanceImport;

namespace SMS.Modules.Finance.Import;

/// <summary>
/// A40 (TFIN, D-5) — the checks the customer and supplier opening-balance targets share: partner by code, reference,
/// document date (≤ the opening balance date), due date, amount, currency (blank = the organization's base of the side) and
/// the exchange rate locked at the document date (A35 D-12). Read-only: it plans, the target writes.
/// </summary>
internal sealed class OpeningBalancePlanner
{
    internal const int MaxReferenceLength = 50;

    private readonly IOpeningBalanceDateProvider _openingDate;
    private readonly ICurrencyService            _currency;
    private readonly IOrgCurrencyLookup          _orgCurrencies;
    private readonly IImportPartnerLookup?       _partners;

    public OpeningBalancePlanner(
        IOpeningBalanceDateProvider openingDate, ICurrencyService currency, IOrgCurrencyLookup orgCurrencies, IImportPartnerLookup? partners)
    {
        _openingDate   = openingDate;
        _currency      = currency;
        _orgCurrencies = orgCurrencies;
        _partners      = partners;
    }

    /// <summary>One row's fields, by the target's own names.</summary>
    internal sealed record Fields(string Partner, string Reference, string Date, string DueDate, string Amount, string Currency);

    /// <summary>What a clean row will write. Null members only when the row has errors.</summary>
    internal sealed record Plan(
        int RowNumber, List<ImportFieldError> Errors, ImportPartner? Partner, string? Reference, DateOnly Date, DateOnly? DueDate,
        decimal Amount, DocumentRateLock? Rate, Guid Key)
    {
        public Guid? Existing { get; set; }
    }

    /// <param name="customers">True: the partner must be a customer (sale base); false: a vendor (purchase base).</param>
    internal async Task<(DateOnly? OpeningDate, List<Plan> Plans)> PlanAsync(
        Guid org, IReadOnlyList<(int RowNumber, IReadOnlyDictionary<string, string?> Values)> rows, Fields f, bool customers,
        string keyKind, CancellationToken ct)
    {
        var opening = await _openingDate.GetAsync(org, ct);
        var domain  = customers ? TransactionDomain.Sale : TransactionDomain.Purchase;
        var who     = customers ? "customer" : "supplier";

        var codes = rows.Select(r => Value(r.Values, f.Partner)?.ToUpperInvariant()).OfType<string>().Distinct().ToList();
        IReadOnlyDictionary<string, ImportPartner> partners = codes.Count == 0 || _partners is null
            ? new Dictionary<string, ImportPartner>()
            : await _partners.FindByCodesAsync(org, codes, ct);

        Guid? baseCurrency = null;
        var currencies = new Dictionary<string, OrgCurrencyInfo?>(StringComparer.OrdinalIgnoreCase);
        var locks      = new Dictionary<(Guid, DateOnly), (DocumentRateLock? Lock, string? Error)>();
        var seenKeys   = new Dictionary<Guid, int>();
        var plans      = new List<Plan>(rows.Count);

        foreach (var (rowNumber, values) in rows)
        {
            var errors = new List<ImportFieldError>();

            if (opening is null)
                errors.Add(new(f.Date, OpeningDateMissing, OpeningDateNotSet, Value(values, f.Date)));

            // ── Partner ──
            var rawCode = Value(values, f.Partner);
            ImportPartner? partner = null;
            if (rawCode is null)
                errors.Add(new(f.Partner, "REQUIRED", $"The {who} code is required.", null));
            else if (_partners is null)
                errors.Add(new(f.Partner, "LOOKUP_NOT_FOUND", $"{Cap(who)}s cannot be looked up in this host.", rawCode));
            else if (!partners.TryGetValue(rawCode.ToUpperInvariant(), out partner))
                errors.Add(new(f.Partner, "LOOKUP_NOT_FOUND", $"There is no {who} with code {rawCode}.", rawCode));
            else if (customers ? !partner.IsCustomer : !partner.IsVendor)
            {
                errors.Add(new(f.Partner, customers ? NotACustomer : NotAVendor, $"{partner.Code} ({partner.Name}) is not a {who}.", rawCode));
                partner = null;
            }
            else if (!partner.IsActive)
            {
                errors.Add(new(f.Partner, InactivePartner, $"{Cap(who)} {partner.Code} ({partner.Name}) is inactive.", rawCode));
                partner = null;
            }

            // ── Reference ──
            var reference = Value(values, f.Reference);
            if (reference is { Length: > MaxReferenceLength })
                errors.Add(new(f.Reference, "MAX_LENGTH", $"A reference can be at most {MaxReferenceLength} characters.", reference));

            // ── Dates ──
            var rawDate = Value(values, f.Date);
            var date    = default(DateOnly);
            var dateOk  = false;
            if (rawDate is null)
                errors.Add(new(f.Date, "REQUIRED", "The date is required.", null));
            else if (!TryDate(rawDate, out date) || date < new DateOnly(2000, 1, 1))
                errors.Add(new(f.Date, "INVALID_DATE", $"'{rawDate}' is not a date (yyyy-MM-dd, 2000 or later).", rawDate));
            else if (opening is { } o && date > o)
                errors.Add(new(f.Date, AfterOpeningDate, $"The date {Iso(date)} is after the opening balance date {Iso(o)}.", rawDate));
            else
                dateOk = true;

            DateOnly? due = null;
            var rawDue = Value(values, f.DueDate);
            if (rawDue is not null)
            {
                if (!TryDate(rawDue, out var d))
                    errors.Add(new(f.DueDate, "INVALID_DATE", $"'{rawDue}' is not a date (yyyy-MM-dd).", rawDue));
                else if (dateOk && d < date)
                    errors.Add(new(f.DueDate, "INVALID_DATE", $"The due date {Iso(d)} is before the date {Iso(date)}.", rawDue));
                else
                    due = d;
            }

            // ── Amount ──
            var rawAmount = Value(values, f.Amount);
            decimal amount = 0m;
            if (rawAmount is null)
                errors.Add(new(f.Amount, "REQUIRED", "The amount is required.", null));
            else if (!TryDecimal(rawAmount, out amount))
                errors.Add(new(f.Amount, "INVALID_TYPE", $"'{rawAmount}' is not a number.", rawAmount));
            else if (amount <= 0m)
                errors.Add(new(f.Amount, "OUT_OF_RANGE", "The amount must be greater than zero (what is owed).", rawAmount));
            else if (decimal.Round(amount, 2) != amount)
                errors.Add(new(f.Amount, "INVALID_PRECISION", "The amount can have at most two decimals.", rawAmount));

            // ── Currency and the rate locked at the document date ──
            var rawCurrency = Value(values, f.Currency);
            Guid? currencyId = null;
            if (rawCurrency is null)
            {
                baseCurrency ??= await _currency.GetBaseCurrencyIdAsync(org, domain, ct);
                currencyId = baseCurrency;
            }
            else
            {
                if (!currencies.TryGetValue(rawCurrency, out var info))
                    currencies[rawCurrency] = info = await _orgCurrencies.GetByCodeAsync(org, rawCurrency, ct);
                if (info is null || !info.IsActive)
                    errors.Add(new(f.Currency, "LOOKUP_NOT_FOUND", $"{rawCurrency.ToUpperInvariant()} is not an active currency of this organization.", rawCurrency));
                else
                {
                    currencyId = info.CurrencyId;
                    if (rawAmount is not null && TryDecimal(rawAmount, out var a) && CurrencyConventions.RoundAmount(a, info.DecimalPlaces) != a)
                        errors.Add(new(f.Amount, "INVALID_PRECISION", $"{info.Code} amounts have {info.DecimalPlaces} decimals.", rawAmount));
                }
            }

            DocumentRateLock? rate = null;
            if (currencyId is { } cid && dateOk)
            {
                if (!locks.TryGetValue((cid, date), out var lk))
                {
                    try { lk = (await _currency.LockRateAsync(org, cid, date, domain, ct), null); }
                    catch (CurrencyRateNotFoundException ex) { lk = (null, ex.Message); }
                    locks[(cid, date)] = lk;
                }
                if (lk.Error is not null) errors.Add(new(f.Currency, NoExchangeRate, lk.Error, rawCurrency));
                rate = lk.Lock;
            }

            // ── One opening document per partner and reference (blank = one per partner) ──
            var key = partner is null ? Guid.Empty : OpeningKey(keyKind, org, partner.Uuid, reference);
            if (partner is not null)
            {
                if (seenKeys.TryGetValue(key, out var first))
                    errors.Add(new(f.Reference, "DUPLICATE_IN_FILE",
                        reference is null
                            ? $"Row {first} is already the opening balance of {partner.Code} without a reference; give each one a reference."
                            : $"Row {first} is already {partner.Code}'s opening balance {reference}.", reference));
                else
                    seenKeys[key] = rowNumber;
            }

            plans.Add(new Plan(rowNumber, errors, partner, reference, date, due, amount, rate, key));
        }

        return (opening, plans);
    }

    private static string Cap(string s) => char.ToUpperInvariant(s[0]) + s[1..];
}
