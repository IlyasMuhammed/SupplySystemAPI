namespace SMS.Shared.Common;

/// <summary>Who a tax code may be used by. Mirrors Finance's TaxCode.Usage values.</summary>
public static class TaxCodeUsage
{
    public const string Sales    = "SALES";
    public const string Purchase = "PURCHASE";
    public const string Both     = "BOTH";

    /// <summary>True when a code of usage <paramref name="codeUsage"/> may be used on a <paramref name="side"/> (Sales or Purchase) document.</summary>
    public static bool Allows(string codeUsage, string side) =>
        string.Equals(codeUsage, Both, StringComparison.OrdinalIgnoreCase)
        || string.Equals(codeUsage, side, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A tax code as other modules see it.</summary>
public sealed record TaxCodeInfo(
    Guid    Uuid,
    string  Code,
    string  Name,
    decimal RatePercent,
    string  Usage,
    bool    IsDefault,
    bool    IsActive);

/// <summary>
/// The current organization's tax codes (SAP's FTXP in spirit), owned by SMS.Modules.Finance and read
/// through SMS.Shared — Demand picks a code on a sale-order line without referencing Finance.
/// Documents keep the code AND its rate as a snapshot; this lookup is only consulted when a line is
/// built. Register/resolve optional-safe, like <see cref="IExchangeRateProvider"/>.
/// </summary>
public interface ITaxCodeLookup
{
    /// <summary>The code, active or not (an old document may reference a deactivated one). Null if unknown or another organization's.</summary>
    Task<TaxCodeInfo?> GetAsync(Guid uuid, CancellationToken ct = default);

    /// <summary>Active codes usable on <paramref name="side"/> (TaxCodeUsage.Sales / Purchase), default first, then by code.</summary>
    Task<IReadOnlyList<TaxCodeInfo>> ListActiveAsync(string side, CancellationToken ct = default);

    /// <summary>The active default code for <paramref name="side"/>, if one is set.</summary>
    Task<TaxCodeInfo?> GetDefaultAsync(string side, CancellationToken ct = default);
}
