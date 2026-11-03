using System.Globalization;
using SMS.Shared.Common;

namespace SMS.Modules.Finance.ArAp.Receivables;

/// <summary>What a customer's credit standing is made of, all in the org's sale base currency (D-13).</summary>
/// <param name="PermanentLimit">BusinessPartner.CreditLimit (D-4). Null or 0 = no limit (D-CORE-3 / D-AR-1).</param>
/// <param name="OpenInvoices">Open sales-invoice balance less unapplied customer credit, never below 0 (API-CONTRACT §2).</param>
internal sealed record CreditStanding(
    decimal? PermanentLimit,
    decimal? TemporaryLimit,
    DateOnly? TemporaryExpiry,
    bool OnHold,
    string? HoldReason,
    decimal OpenOrders,
    decimal OpenInvoices);

/// <summary>The decision of one credit check (pure — no I/O; the service feeds it).</summary>
internal sealed record CreditDecision(
    bool Approved,
    string? BlockReason,
    string? Warning,
    decimal? EffectiveLimit,
    bool TemporaryActive,
    decimal CurrentExposure,
    decimal RequestedAmount,
    decimal ProjectedExposure,
    decimal? AvailableCredit,
    bool OverLimit);

/// <summary>
/// A48 C2 business rules BR-CR-001..005 as pure functions, so they are tested without a database (TS-CR-001..006).
/// Mode semantics (D-5, D-CORE-4): <c>Off</c> approves without looking; a hold blocks in <c>Warn</c> and <c>Block</c>;
/// the limit blocks only in <c>Block</c> and is a warning in <c>Warn</c>.
/// </summary>
internal static class CreditRules
{
    /// <summary>D-AR-1: the permanent limit counts only when it is above zero; null / 0 = unlimited (TS-CR-001).</summary>
    public static bool HasPermanentLimit(decimal? limit) => limit is > 0m;

    /// <summary>BR-CR-005: a temporary limit counts up to and including its expiry day.</summary>
    public static bool TemporaryActive(decimal? temporary, DateOnly? expiry, DateOnly today) =>
        CreditLimits.TemporaryActive(temporary, expiry, today);

    /// <summary>
    /// BR-CR-002: MAX(credit limit, temporary limit) while the temporary one is live, else the credit limit.
    /// Null = no limit at all (unlimited). A temporary limit needs a permanent one (contract §2), so a customer without a
    /// permanent limit stays unlimited whatever the profile says.
    /// </summary>
    public static decimal? EffectiveLimit(decimal? permanent, decimal? temporary, DateOnly? expiry, DateOnly today)
    {
        if (!HasPermanentLimit(permanent)) return null;
        return TemporaryActive(temporary, expiry, today) ? Math.Max(permanent!.Value, temporary!.Value) : permanent!.Value;
    }

    /// <summary>BR-CR-004: exposure = open (confirmed, not yet invoiced) orders + open invoice balances.</summary>
    public static decimal Exposure(CreditStanding s) => s.OpenOrders + s.OpenInvoices;

    public static CreditDecision Decide(
        string mode, CreditStanding s, decimal requested, DateOnly today, string customerName, string? currencyCode)
    {
        if (requested < 0m) requested = 0m;
        var ccy = string.IsNullOrWhiteSpace(currencyCode) ? "" : currencyCode.Trim();

        if (mode == CreditControlModes.Off)
            return new CreditDecision(true, null, null, null, false, 0m, requested, 0m, null, false);

        var limit     = EffectiveLimit(s.PermanentLimit, s.TemporaryLimit, s.TemporaryExpiry, today);
        var tempLive  = limit is not null && TemporaryActive(s.TemporaryLimit, s.TemporaryExpiry, today);
        var current   = Exposure(s);
        var projected = current + requested;
        decimal? available = limit is { } l ? l - current : null;
        var over      = limit is { } lim && projected > lim;

        if (s.OnHold)
        {
            var reason = string.IsNullOrWhiteSpace(s.HoldReason) ? "no reason recorded" : s.HoldReason.Trim().TrimEnd('.');
            return new CreditDecision(false, $"{customerName} is on credit hold: {reason}.", null,
                limit, tempLive, current, requested, projected, available, over);
        }

        if (!over)
            return new CreditDecision(true, null, null, limit, tempLive, current, requested, projected, available, false);

        var text = $"Exceeds the credit limit of {Money(limit!.Value)}{Ccy(ccy)} by {Money(projected - limit.Value)}{Ccy(ccy)} " +
                   $"(exposure {Money(current)}, this order {Money(requested)}).";

        return mode == CreditControlModes.Block
            ? new CreditDecision(false, text, null, limit, tempLive, current, requested, projected, available, true)
            : new CreditDecision(true, null, text, limit, tempLive, current, requested, projected, available, true);
    }

    internal static string Money(decimal v) => v.ToString("#,##0.00", CultureInfo.InvariantCulture);

    private static string Ccy(string ccy) => ccy.Length == 0 ? "" : " " + ccy;
}
