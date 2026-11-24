using Microsoft.EntityFrameworkCore;
using SMS.Modules.Lookups.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Lookups.Services;

// A51 P3 — implements the SMS.Shared contract so the Create Organization flow (Tenancy) can validate a country code and read its
// default timezone without referencing this project.
internal sealed class CountryLookup : ICountryLookup
{
    private readonly LookupsDbContext _db;

    public CountryLookup(LookupsDbContext db) => _db = db;

    public async Task<CountryInfo?> FindByNameAsync(string name, CancellationToken ct = default)
    {
        var key = (name ?? string.Empty).Trim().ToUpperInvariant();
        if (key.Length == 0) return null;
        var rows = await _db.Countries.AsNoTracking()
            .Where(c => c.Code != null && c.Name.ToUpper() == key)
            .OrderByDescending(c => c.IsActive)
            .Select(c => new { c.Code, c.Name, c.DefaultTimezone, c.CurrencyCode, c.IsActive })
            .Take(1).ToListAsync(ct);
        return rows.Count == 0
            ? null
            : new CountryInfo(rows[0].Code!.Trim().ToUpperInvariant(), rows[0].Name, string.IsNullOrWhiteSpace(rows[0].DefaultTimezone) ? null : rows[0].DefaultTimezone!.Trim(),
                string.IsNullOrWhiteSpace(rows[0].CurrencyCode) ? null : rows[0].CurrencyCode!.Trim(), rows[0].IsActive);
    }

    public async Task<CountryInfo?> FindByCodeAsync(string code, CancellationToken ct = default)
    {
        var key = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (key.Length == 0) return null;

        var rows = await _db.Countries.AsNoTracking()
            .Where(c => c.Code != null && c.Code.ToUpper() == key)
            .OrderByDescending(c => c.IsActive)
            .Select(c => new { c.Code, c.Name, c.DefaultTimezone, c.CurrencyCode, c.IsActive })
            .Take(1).ToListAsync(ct);

        return rows.Count == 0
            ? null
            : new CountryInfo(rows[0].Code!.Trim().ToUpperInvariant(), rows[0].Name, string.IsNullOrWhiteSpace(rows[0].DefaultTimezone) ? null : rows[0].DefaultTimezone!.Trim(),
                string.IsNullOrWhiteSpace(rows[0].CurrencyCode) ? null : rows[0].CurrencyCode!.Trim(), rows[0].IsActive);
    }
}
