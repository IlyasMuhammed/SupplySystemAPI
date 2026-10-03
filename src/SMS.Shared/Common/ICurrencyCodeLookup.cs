namespace SMS.Shared.Common;

/// <summary>
/// Resolves a Lookups currency id to its code without a project reference to SMS.Modules.Lookups —
/// the same arrangement as <see cref="ICityLookupService"/>. The implementation lives in Lookups.
/// First used by the QuickBooks gateway's preflight, which compares the organization's base currency
/// (an id, from <see cref="IOrganizationCurrencyService"/>) with QuickBooks' home currency (a code).
/// </summary>
public interface ICurrencyCodeLookup
{
    /// <summary>
    /// The currency's <c>Code</c> exactly as stored (entered through the Lookups admin screen, so an
    /// ISO-4217 code by convention, not by constraint), or null when it does not exist or has no code.
    /// Currencies are global reference data, not tenant-scoped.
    /// </summary>
    Task<string?> GetCodeAsync(Guid currencyId, CancellationToken ct = default);
}
