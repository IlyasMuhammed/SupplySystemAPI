using Microsoft.EntityFrameworkCore;
using SMS.Modules.Lookups.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Lookups.Services;

// Implements the SMS.Shared contract so other modules (SMS.Modules.Logistics' structured
// addresses, first) can resolve a city without referencing this project.
internal sealed class CityLookupService : ICityLookupService
{
    private readonly LookupsDbContext _db;

    public CityLookupService(LookupsDbContext db) => _db = db;

    public async Task<CityLookupResult?> FindAsync(Guid cityId)
    {
        if (cityId == Guid.Empty) return null;

        return await _db.Cities
            .Where(c => c.Id == cityId && c.IsActive)
            .Join(_db.Countries,
                  city    => city.CountryId,
                  country => country.Id,
                  (city, country) => new CityLookupResult(
                      city.Id, city.Name, country.Id, country.Name, country.Code))
            .FirstOrDefaultAsync();
    }
}
