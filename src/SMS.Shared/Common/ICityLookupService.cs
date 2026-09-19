namespace SMS.Shared.Common;

/// <summary>
/// A city resolved from the shared Lookups catalog, with its country flattened in.
/// </summary>
/// <param name="CountryCode">
/// The country's <c>Code</c> exactly as it is stored. It is entered through the Lookups admin
/// screen with only a uniqueness check, so it is <b>not</b> guaranteed to be an ISO-3166 alpha-2
/// region code. Treat it as a hint, never as something load-bearing.
/// </param>
public sealed record CityLookupResult(
    Guid    CityId,
    string  CityName,
    Guid    CountryId,
    string  CountryName,
    string? CountryCode);

/// <summary>
/// Shared interface so modules can resolve a city id from the Lookups catalog without a project
/// reference to SMS.Modules.Lookups. The implementation lives there and is resolved through DI —
/// the same arrangement as <see cref="ISupplierNameLookupService"/>.
/// </summary>
public interface ICityLookupService
{
    /// <summary>
    /// Resolves a city by id, or returns null when it does not exist or is inactive.
    /// Cities are global reference data, not tenant-scoped.
    /// </summary>
    Task<CityLookupResult?> FindAsync(Guid cityId);
}
