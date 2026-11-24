namespace SMS.Shared.Common;

/// <summary>A row of the countries master (lookups.Countries) as another module needs it.</summary>
public sealed record CountryInfo(string Code, string Name, string? DefaultTimezone, string? CurrencyCode, bool IsActive);

/// <summary>
/// Resolves a country code against the Lookups countries master without a project reference to SMS.Modules.Lookups (the same
/// arrangement as <see cref="ICurrencyCodeLookup"/>). First used by the super admin's Create Organization flow (A51 P3).
/// </summary>
public interface ICountryLookup
{
    /// <summary>The country with this code (case-insensitive), active or not; null when there is none.</summary>
    Task<CountryInfo?> FindByCodeAsync(string code, CancellationToken ct = default);

    /// <summary>A51 P7 — the country with this NAME (case-insensitive; the organization's free-text Country holds the name). Default: none.</summary>
    Task<CountryInfo?> FindByNameAsync(string name, CancellationToken ct = default) => Task.FromResult<CountryInfo?>(null);
}
