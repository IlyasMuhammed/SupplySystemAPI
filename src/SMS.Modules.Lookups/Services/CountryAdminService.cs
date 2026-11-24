using Microsoft.EntityFrameworkCore;
using SMS.Modules.Lookups.Data;
using SMS.Modules.Lookups.Domain;
using SMS.Modules.Lookups.Models;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Lookups.Services;

/// <summary>A51 P1 — super-admin management of the country master (extends the existing Countries lookup).</summary>
public interface ICountryAdminService
{
    Task<List<CountryAdminModel>> ListAsync(CancellationToken ct = default);
    Task<CountryAdminModel> GetAsync(Guid id, CancellationToken ct = default);
    Task<CountryAdminModel> CreateAsync(SaveCountryRequest request, CancellationToken ct = default);
    Task<CountryAdminModel> UpdateAsync(Guid id, SaveCountryRequest request, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<CountryAdminModel> SetActiveAsync(Guid id, bool active, CancellationToken ct = default);
}

internal sealed class CountryAdminService : ICountryAdminService
{
    private const string DefaultDateFormat = "DD/MM/YYYY";
    private readonly LookupsDbContext _db;

    public CountryAdminService(LookupsDbContext db) => _db = db;

    internal static CountryAdminModel ToModel(Country c) => new()
    {
        Id = c.Id, Code = c.Code ?? string.Empty, Name = c.Name, CurrencyCode = c.CurrencyCode, DefaultTimezone = c.DefaultTimezone,
        FiscalYearStartMonth = c.FiscalYearStartMonth, DateFormat = c.DateFormat, PhoneCode = c.PhoneCode, IsActive = c.IsActive, SortOrder = c.SortOrder
    };

    public async Task<List<CountryAdminModel>> ListAsync(CancellationToken ct = default) =>
        (await _db.Countries.AsNoTracking().ToListAsync(ct))
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name).Select(ToModel).ToList();

    public async Task<CountryAdminModel> GetAsync(Guid id, CancellationToken ct = default) => ToModel(await FindAsync(id, ct));

    public async Task<CountryAdminModel> CreateAsync(SaveCountryRequest request, CancellationToken ct = default)
    {
        var code = NormalizeCode(request.Code);
        if (await _db.Countries.AnyAsync(c => c.Code != null && c.Code.ToUpper() == code, ct))
            throw new ConflictException($"A country with code '{code}' already exists.");

        var entity = new Country { Id = Guid.NewGuid(), Code = code, IsActive = request.IsActive ?? true };
        await ApplyAsync(entity, request, ct);
        _db.Countries.Add(entity);
        await _db.SaveChangesAsync(ct);
        return ToModel(entity);
    }

    public async Task<CountryAdminModel> UpdateAsync(Guid id, SaveCountryRequest request, CancellationToken ct = default)
    {
        var entity = await _db.Countries.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Country not found.");
        if (!string.IsNullOrWhiteSpace(request.Code)
            && !string.Equals(request.Code.Trim(), entity.Code?.Trim(), StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(entity.Code))
            throw new BadRequestException("A country's code cannot be changed.");
        if (string.IsNullOrWhiteSpace(entity.Code)) entity.Code = NormalizeCode(request.Code);

        await ApplyAsync(entity, request, ct, id);
        if (request.IsActive.HasValue) entity.IsActive = request.IsActive.Value;
        await _db.SaveChangesAsync(ct);
        return ToModel(entity);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await FindAsync(id, ct);
        if (await _db.Cities.AnyAsync(c => c.CountryId == id, ct))
            throw new ConflictException("This country has cities and cannot be deleted. Deactivate it instead.");
        _db.Countries.Remove(entity);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<CountryAdminModel> SetActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var entity = await FindAsync(id, ct);
        entity.IsActive = active;
        await _db.SaveChangesAsync(ct);
        return ToModel(entity);
    }

    private async Task<Country> FindAsync(Guid id, CancellationToken ct) =>
        await _db.Countries.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw new NotFoundException("Country not found.");

    private static string NormalizeCode(string? code)
    {
        var c = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (c.Length is < 2 or > 5 || !c.All(char.IsLetter)) throw new BadRequestException("Country code must be 2 to 5 letters (ISO alpha-2, e.g. PK).");
        return c;
    }

    private async Task ApplyAsync(Country entity, SaveCountryRequest r, CancellationToken ct, Guid? selfId = null)
    {
        var name = (r.Name ?? string.Empty).Trim();
        if (name.Length == 0) throw new BadRequestException("Country name cannot be empty.");
        if (name.Length > 200) throw new BadRequestException("Country name is too long (200 characters).");
        if (await _db.Countries.AnyAsync(c => c.Id != entity.Id && c.Name.ToLower() == name.ToLower(), ct))
            throw new ConflictException($"A country named '{name}' already exists.");

        var currency = string.IsNullOrWhiteSpace(r.CurrencyCode) ? null : r.CurrencyCode.Trim().ToUpperInvariant();
        if (currency is not null && (currency.Length is < 3 or > 5 || !currency.All(char.IsLetter)))
            throw new BadRequestException("Currency code must be 3 to 5 letters (e.g. PKR).");

        var tz = string.IsNullOrWhiteSpace(r.DefaultTimezone) ? null : r.DefaultTimezone.Trim();
        if (tz is { Length: > 50 }) throw new BadRequestException("Timezone is too long (50 characters).");

        var fiscal = r.FiscalYearStartMonth ?? 1;
        if (fiscal is < 1 or > 12) throw new BadRequestException("Fiscal year start month must be between 1 and 12.");

        var dateFormat = string.IsNullOrWhiteSpace(r.DateFormat) ? DefaultDateFormat : r.DateFormat.Trim();
        if (dateFormat.Length > 20) throw new BadRequestException("Date format is too long (20 characters).");

        var phone = string.IsNullOrWhiteSpace(r.PhoneCode) ? null : r.PhoneCode.Trim();
        if (phone is not null && (phone.Length > 5 || !phone.TrimStart('+').All(char.IsDigit)))
            throw new BadRequestException("Phone code must be digits with an optional leading + (up to 5 characters).");

        entity.Name = name;
        entity.CurrencyCode = currency;
        entity.DefaultTimezone = tz;
        entity.FiscalYearStartMonth = fiscal;
        entity.DateFormat = dateFormat;
        entity.PhoneCode = phone;
        entity.SortOrder = r.SortOrder ?? entity.SortOrder;
    }
}
