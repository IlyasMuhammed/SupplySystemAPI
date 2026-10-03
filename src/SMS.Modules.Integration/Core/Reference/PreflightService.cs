using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SMS.Modules.Integration.Core.Connections;
using SMS.Modules.Integration.Core.Providers;
using SMS.Modules.Integration.Core.Settings;
using SMS.Modules.Integration.Core.Sync;
using SMS.Modules.Integration.Data;
using SMS.Modules.Integration.Domain;
using SMS.Modules.Integration.Models;
using SMS.Shared.Common;

namespace SMS.Modules.Integration.Core.Reference;

/// <summary>The checks that must pass before anything is sent to a real company (plan QBI-12).</summary>
public interface IPreflightService
{
    Task<PreflightResultModel> RunAsync(CancellationToken ct = default);
}

/// <summary>Values of <see cref="PreflightCheckModel.Code"/>.</summary>
internal static class PreflightCodes
{
    public const string Connection        = "CONNECTION";
    public const string ReferenceData     = "REFERENCE_DATA";
    public const string HomeCurrency      = "HOME_CURRENCY";
    public const string MultiCurrency     = "MULTICURRENCY";
    public const string Country           = "COUNTRY";
    public const string CustomTxnNumbers  = "CUSTOM_TXN_NUMBERS";
    public const string Discounts         = "DISCOUNTS";
    public const string TaxCodes          = "TAX_CODES";
    public const string AccountsMapped    = "ACCOUNTS_MAPPED";
    public const string MatchingConfirmed = "MATCHING_CONFIRMED";
}

/// <summary>
/// Everything here reads what is stored — the connection, the reference snapshots, the settings and
/// the payloads the gateway holds — and nothing calls QuickBooks, so the screen can re-run it freely.
/// A <c>Fail</c> blocks Live; a <c>Warn</c> is something a person should look at, and does not.
/// </summary>
internal sealed class PreflightService : IPreflightService
{
    private static readonly string[] UnitedStates = ["US", "USA", "UNITED STATES", "UNITED STATES OF AMERICA"];

    private readonly IntegrationDbContext  _db;
    private readonly IConnectionAccessor   _accessor;
    private readonly ReferenceDataService  _reference;
    private readonly IBaseCurrencyResolver _baseCurrency;
    private readonly IServiceProvider      _services;

    public PreflightService(
        IntegrationDbContext db, IConnectionAccessor accessor, ReferenceDataService reference, IBaseCurrencyResolver baseCurrency,
        IServiceProvider services)
    {
        _db           = db;
        _accessor     = accessor;
        _reference    = reference;
        _baseCurrency = baseCurrency;
        _services     = services;
    }

    public async Task<PreflightResultModel> RunAsync(CancellationToken ct = default)
    {
        var checks     = new List<PreflightCheckModel>();
        var connection = await _accessor.GetCurrentAsync(ct);

        if (connection is null || !connection.IsUsable())
        {
            checks.Add(Fail(PreflightCodes.Connection, "QuickBooks connection",
                ConnectionPersistence.DescribeUnusable(connection?.Status ?? ConnectionStatus.NotConnected)));

            if (connection is null) return Result(checks, matchingConfirmed: false);
        }
        else
        {
            checks.Add(Pass(PreflightCodes.Connection, "QuickBooks connection",
                $"Connected to {connection.CompanyName ?? "the QuickBooks company"} ({connection.Environment})."));
        }

        // Read-only: preflight never creates the settings row.
        var settings = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.ConnectionId == connection.Id, ct)
                    ?? new IntegrationSettings { ConnectionId = connection.Id };

        var (data, fetchedAt) = await _reference.LoadAsync(connection.Id, ct);
        var facts             = await StoredPayloadScanner.ScanAsync(_db, connection.Id, ct);
        var mappings          = await _db.TaxCodeMappings.AsNoTracking()
            .Where(m => m.ConnectionId == connection.Id)
            .Select(m => new { m.SourceTaxCode, m.TaxPercent })
            .ToListAsync(ct);
        var mappedCodes = mappings.Select(m => SyncPayloads.NormalizeTaxCode(m.SourceTaxCode))
                                  .Where(c => c is not null).Select(c => c!).ToHashSet(StringComparer.Ordinal);
        var mappedRates = mappings.Where(m => SyncPayloads.NormalizeTaxCode(m.SourceTaxCode) is null).Select(m => m.TaxPercent).ToList();

        checks.Add(ReferenceDataCheck(data, fetchedAt));
        checks.Add(await HomeCurrencyCheckAsync(connection, data, facts, ct));
        checks.Add(await MultiCurrencyCheckAsync(connection, data, facts, ct));
        checks.Add(CountryCheck(connection, data));
        checks.Add(CustomTxnNumbersCheck(data));
        checks.Add(DiscountsCheck(data));
        checks.Add(TaxCodesCheck(data, facts, mappedCodes, mappedRates));
        checks.Add(AccountsCheck(settings, data));
        checks.Add(settings.MatchingConfirmedAt is { } confirmedAt
            ? Pass(PreflightCodes.MatchingConfirmed, "Existing records matched",
                $"Matching was confirmed on {confirmedAt:dd MMM yyyy}.")
            : Warn(PreflightCodes.MatchingConfirmed, "Existing records matched",
                "Matching of customers, vendors and items already in QuickBooks has not been confirmed. Live stays off until it is."));

        return Result(checks, settings.MatchingConfirmedAt is not null);
    }

    private static PreflightResultModel Result(List<PreflightCheckModel> checks, bool matchingConfirmed)
    {
        var passed = checks.All(c => c.Status != "Fail");
        return new PreflightResultModel { Passed = passed, CanGoLive = passed && matchingConfirmed, Checks = checks };
    }

    private static PreflightCheckModel ReferenceDataCheck(RemoteReferenceData? data, DateTime? fetchedAt) =>
        data is null
            ? Fail(PreflightCodes.ReferenceData, "QuickBooks reference data",
                "Accounts, tax codes and terms have never been loaded from QuickBooks. Refresh the reference data.")
            : Pass(PreflightCodes.ReferenceData, "QuickBooks reference data",
                $"{data.Accounts.Count} accounts, {data.TaxCodes.Count} tax codes and {data.Terms.Count} terms, loaded {fetchedAt:dd MMM yyyy HH:mm} UTC.");

    private async Task<PreflightCheckModel> HomeCurrencyCheckAsync(
        IntegrationConnection connection, RemoteReferenceData? data, StoredPayloadFacts facts, CancellationToken ct)
    {
        const string title = "Home currency";

        var home = (connection.HomeCurrencyCode ?? data?.Preferences?.HomeCurrencyCode)?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(home))
            return Warn(PreflightCodes.HomeCurrency, title,
                "QuickBooks' home currency is not known yet. Refresh the reference data.");

        // D-1 / S-10: whatever the base-currency answer, the stored payloads say what callers actually send.
        // Foreign-currency records are refused while QuickBooks' multicurrency is off; with it on they go in
        // their own currency (the multicurrency check says what that needs).
        var foreign = facts.Currencies
            .Where(c => !string.Equals(c.Key.Trim(), home, StringComparison.OrdinalIgnoreCase))
            .Select(c => $"{c.Key.Trim().ToUpperInvariant()} ({c.Value})")
            .ToList();
        var multiCurrency = connection.MultiCurrencyEnabled ?? data?.Preferences?.MultiCurrencyEnabled;
        var foreignNote = foreign.Count == 0
            ? string.Empty
            : multiCurrency == true
                ? $" Stored records in other currencies are sent in their own currency (see Multicurrency): {string.Join(", ", foreign)}."
                : $" Stored records in other currencies will be refused while QuickBooks' multicurrency is off: {string.Join(", ", foreign)}.";

        var baseCurrency = await _baseCurrency.ResolveAsync(connection.OrganizationId, ct);

        if (baseCurrency.Code is { } code)
        {
            var scmBase = code.Trim().ToUpperInvariant();
            return string.Equals(scmBase, home, StringComparison.OrdinalIgnoreCase)
                ? Pass(PreflightCodes.HomeCurrency, title,
                    $"QuickBooks' home currency and SCM's base currency are both {home}.{foreignNote}")
                : Fail(PreflightCodes.HomeCurrency, title,
                    $"QuickBooks' home currency is {home} but SCM's base currency is {scmBase}. Every {scmBase} record would be a "
                  + $"foreign-currency record in QuickBooks, and SMS's base-currency amounts would not match QuickBooks' books. "
                  + $"Connect the QuickBooks company kept in {scmBase}, or correct SCM's base currency.");
        }

        return Warn(PreflightCodes.HomeCurrency, title,
            (baseCurrency.Configured
                ? $"SCM's base currency could not be checked automatically. Confirm it is {home}, QuickBooks' home currency."
                : $"No base currency is set for this organization in SCM. Set it, and confirm it is {home}, QuickBooks' home currency.")
            + foreignNote);
    }

    /// <summary>
    /// Plan S-10. Off: only home-currency records go. On: foreign-currency records go in their own currency
    /// when QuickBooks has it active, and foreign documents at SMS's exchange rate for their date — so the
    /// check warns about stored records in currencies QuickBooks lacks, and about document currencies with
    /// no SMS rate on file at all.
    /// </summary>
    private async Task<PreflightCheckModel> MultiCurrencyCheckAsync(
        IntegrationConnection connection, RemoteReferenceData? data, StoredPayloadFacts facts, CancellationToken ct)
    {
        const string title = "Multicurrency";
        var enabled = connection.MultiCurrencyEnabled ?? data?.Preferences?.MultiCurrencyEnabled;
        var home    = CurrencyRules.Normalize(connection.HomeCurrencyCode ?? data?.Preferences?.HomeCurrencyCode);

        if (enabled is null)
            return Warn(PreflightCodes.MultiCurrency, title, "Not known yet. Refresh the reference data.");

        if (enabled == false)
            return Pass(PreflightCodes.MultiCurrency, title,
                $"Multicurrency is off in QuickBooks, so only {home ?? "home-currency"} customers, vendors, invoices and bills are "
              + "sent; records in any other currency are refused. Turning multicurrency on in QuickBooks (it cannot be turned off "
              + "again) lets them be sent at SMS's exchange rates.");

        const string explained =
            "Multicurrency is on in QuickBooks. A customer or vendor in another currency is sent in that currency when it is "
          + "active in QuickBooks; an invoice or bill in another currency is sent with the exchange rate SMS fixed on it when "
          + "it was issued or approved, or, if it has none, SMS's exchange rate for its date (Settings → Exchange Rates). "
          + "QuickBooks keeps each customer and vendor in one currency, so their documents must be in it too.";

        if (home is null) return Pass(PreflightCodes.MultiCurrency, title, explained);

        var active = (data?.Currencies ?? [])
            .Select(c => CurrencyRules.Normalize(c.Code)).Where(c => c is not null).Select(c => c!)
            .ToHashSet(StringComparer.Ordinal);

        var notActive = facts.Currencies
            .Select(c => (Code: CurrencyRules.Normalize(c.Key)!, Count: c.Value))
            .Where(c => c.Code != home && !active.Contains(c.Code))
            .OrderBy(c => c.Code, StringComparer.Ordinal)
            .Select(c => $"{c.Code} ({c.Count})")
            .ToList();

        // One question per currency: is any rate on file at all, up to today? (Each document still needs
        // one for its own date; validation checks that when it is sent.)
        var rates   = _services.GetService<IExchangeRateProvider>();
        var noRates = new List<string>();
        foreach (var currency in facts.DocumentCurrencies.Keys
                     .Select(c => CurrencyRules.Normalize(c)!).Distinct(StringComparer.Ordinal)
                     .Where(c => c != home && active.Contains(c))
                     .OrderBy(c => c, StringComparer.Ordinal))
        {
            var quote = rates is null ? null : await rates.GetRateAsync(currency, home, DateTime.UtcNow.Date, ct);
            if (quote is not { Rate: > 0 }) noRates.Add($"{currency} → {home}");
        }

        if (notActive.Count == 0 && noRates.Count == 0)
            return Pass(PreflightCodes.MultiCurrency, title, explained);

        var problems = string.Empty;
        if (notActive.Count > 0)
            problems += $" Stored records are in currencies QuickBooks does not have active: {string.Join(", ", notActive)}. "
                      + "They are refused until the currency is added in QuickBooks and the reference data refreshed.";
        if (noRates.Count > 0)
            problems += $" SMS has no exchange rate on file for {string.Join(", ", noRates)}, so invoices and bills in "
                      + (noRates.Count > 1 ? "those currencies" : "that currency")
                      + " without a rate of their own are refused until one is added under Settings → Exchange Rates "
                      + "(they are then sent by themselves).";

        return Warn(PreflightCodes.MultiCurrency, title, explained + problems);
    }

    private static PreflightCheckModel CountryCheck(IntegrationConnection connection, RemoteReferenceData? data)
    {
        const string title = "Company country";
        var country = (connection.Country ?? data?.CompanyInfo?.Country)?.Trim();

        if (string.IsNullOrEmpty(country))
            return Warn(PreflightCodes.Country, title, "Not known yet. Refresh the reference data.");

        return UnitedStates.Contains(country.ToUpperInvariant())
            ? Fail(PreflightCodes.Country, title,
                "US companies use QuickBooks' automated sales tax, which this integration does not support.")
            : Pass(PreflightCodes.Country, title, $"{country}.");
    }

    private static PreflightCheckModel CustomTxnNumbersCheck(RemoteReferenceData? data)
    {
        const string title = "Custom transaction numbers";

        return data?.Preferences?.CustomTxnNumbersEnabled switch
        {
            null  => Warn(PreflightCodes.CustomTxnNumbers, title, "Not known yet. Refresh the reference data."),
            true  => Pass(PreflightCodes.CustomTxnNumbers, title, "On: SCM's invoice numbers are used as QuickBooks document numbers."),
            false => Fail(PreflightCodes.CustomTxnNumbers, title,
                "Off in QuickBooks, so SCM's invoice numbers could not be sent. Turn on Account and Settings → Sales → Custom transaction numbers, then refresh.")
        };
    }

    /// <summary>
    /// Plan D-5 sends invoice discounts as one discount line, which QuickBooks refuses while "Allow
    /// discount" is off. A warning, not a failure: an organization that never discounts is unaffected.
    /// </summary>
    private static PreflightCheckModel DiscountsCheck(RemoteReferenceData? data)
    {
        const string title = "Invoice discounts";

        return data?.Preferences?.DiscountsEnabled switch
        {
            null  => Warn(PreflightCodes.Discounts, title, "Not known yet. Refresh the reference data."),
            true  => Pass(PreflightCodes.Discounts, title, "On: invoice discounts are sent as a discount line."),
            false => Warn(PreflightCodes.Discounts, title,
                "Off in QuickBooks, so invoices with a discount will be refused. Turn on Account and Settings → Sales → Discount, then refresh.")
        };
    }

    /// <summary>
    /// Plan S-11: SMS tax codes seen on stored lines need a code mapping; bare rates seen on lines without a
    /// code need a percent mapping. Either kind missing is a warning — the documents using it are refused.
    /// </summary>
    private static PreflightCheckModel TaxCodesCheck(
        RemoteReferenceData? data, StoredPayloadFacts facts, IReadOnlySet<string> mappedCodes, IReadOnlyCollection<decimal> mappedRates)
    {
        const string title = "Tax codes";

        if (data is null)
            return Warn(PreflightCodes.TaxCodes, title, "Not known yet. Refresh the reference data.");

        var active = data.TaxCodes.Count(t => t.Active);
        if (active == 0)
            return Warn(PreflightCodes.TaxCodes, title,
                "QuickBooks has no active tax codes, so no SMS tax code or tax rate can be mapped. Taxed invoices and bills will be refused.");

        var mapped        = mappedRates.Select(StoredPayloadScanner.NormalizeRate).ToHashSet();
        var unmappedRates = facts.TaxRates.Keys.Where(r => !mapped.Contains(r)).OrderBy(r => r).ToList();
        var unmappedCodes = facts.TaxCodes.Keys.Where(c => !mappedCodes.Contains(c)).OrderBy(c => c, StringComparer.Ordinal).ToList();

        if (unmappedRates.Count == 0 && unmappedCodes.Count == 0)
            return Pass(PreflightCodes.TaxCodes, title,
                $"{active} active tax codes; every SMS tax code and tax rate seen so far is mapped.");

        var message = string.Empty;
        if (unmappedCodes.Count > 0)
            message += $"{unmappedCodes.Count} SMS tax code(s) seen in stored invoices or bills have no mapping: "
                     + $"{string.Join(", ", unmappedCodes)}. ";
        if (unmappedRates.Count > 0)
            message += $"{unmappedRates.Count} tax rate(s) seen on stored lines without a tax code have no mapping: "
                     + $"{string.Join(", ", unmappedRates.Select(FormatRate))}. ";

        return Warn(PreflightCodes.TaxCodes, title,
            message + "Documents using them will be refused until they are mapped.");
    }

    private static PreflightCheckModel AccountsCheck(IntegrationSettings settings, RemoteReferenceData? data)
    {
        const string title = "Default accounts";

        if (string.IsNullOrWhiteSpace(settings.DefaultIncomeAccountId) || string.IsNullOrWhiteSpace(settings.DefaultExpenseAccountId))
            return Fail(PreflightCodes.AccountsMapped, title,
                "Choose a default income account and a default expense account. Items cannot be created in QuickBooks without them.");

        if (data is not null)
        {
            string? Missing(string label, string? id) =>
                id is null || data.Accounts.Any(a => a.Id == id && a.Active) ? null : $"the {label} account (id {id})";

            var missing = new[]
            {
                Missing("default income",  settings.DefaultIncomeAccountId),
                Missing("default expense", settings.DefaultExpenseAccountId),
                Missing("freight expense", settings.FreightExpenseAccountId),
                Missing("discount",        settings.DiscountAccountId)
            }.Where(m => m is not null).ToList();

            if (missing.Count > 0)
                return Fail(PreflightCodes.AccountsMapped, title,
                    $"{string.Join(", ", missing)} is no longer an active account in QuickBooks. Choose again.");
        }

        return Pass(PreflightCodes.AccountsMapped, title, "Default income and expense accounts are set.");
    }

    internal static string FormatRate(decimal rate) => rate.ToString("0.####", CultureInfo.InvariantCulture) + "%";

    private static PreflightCheckModel Pass(string code, string title, string message) => Check(code, title, "Pass", message);
    private static PreflightCheckModel Warn(string code, string title, string message) => Check(code, title, "Warn", message);
    private static PreflightCheckModel Fail(string code, string title, string message) => Check(code, title, "Fail", message);

    private static PreflightCheckModel Check(string code, string title, string status, string message) =>
        new() { Code = code, Title = title, Status = status, Message = message };
}
