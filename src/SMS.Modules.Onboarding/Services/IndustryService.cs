using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Onboarding.Data;
using SMS.Modules.Onboarding.Domain;
using SMS.Modules.Onboarding.Models;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Onboarding.Services;

/// <summary>A51 P1 — the industries master and the countries that offer each (the create-organization form reads GetByCountry).</summary>
public interface IIndustryService
{
    Task<List<IndustryModel>> ListAsync(CancellationToken ct = default);
    Task<IndustryModel> GetAsync(int id, CancellationToken ct = default);
    Task<IndustryModel> CreateAsync(SaveIndustryRequest request, int? userId = null, CancellationToken ct = default);
    Task<IndustryModel> UpdateAsync(int id, SaveIndustryRequest request, int? userId = null, CancellationToken ct = default);
    /// <summary>Active industries offered in a country (active assignment + active industry), by sort order.</summary>
    Task<List<IndustryModel>> GetByCountryAsync(string countryCode, CancellationToken ct = default);
    Task<IndustryModel> AssignToCountryAsync(int id, string countryCode, int? userId = null, CancellationToken ct = default);
    Task<IndustryModel> RemoveFromCountryAsync(int id, string countryCode, CancellationToken ct = default);
}

internal sealed class IndustryService : IIndustryService
{
    private static readonly Regex CodePattern = new("^[A-Z0-9_]{1,30}$", RegexOptions.Compiled);
    private readonly OnboardingDbContext _db;

    public IndustryService(OnboardingDbContext db) => _db = db;

    internal static string NormalizeCountry(string? code)
    {
        var c = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (c.Length is < 2 or > 5 || !c.All(char.IsLetter)) throw new BadRequestException("Country code must be 2 to 5 letters (e.g. PK).");
        return c;
    }

    private static IndustryModel ToModel(Industry i, IEnumerable<string> countries) => new()
    {
        Id = i.Id, Code = i.Code, Name = i.Name, Description = i.Description, IsActive = i.IsActive, SortOrder = i.SortOrder,
        CountryCodes = countries.OrderBy(c => c, StringComparer.Ordinal).ToList()
    };

    public async Task<List<IndustryModel>> ListAsync(CancellationToken ct = default)
    {
        var industries = await _db.Industries.AsNoTracking().ToListAsync(ct);
        var links = await _db.CountryIndustries.AsNoTracking().Where(c => c.IsActive).ToListAsync(ct);
        return industries.OrderBy(i => i.SortOrder).ThenBy(i => i.Name)
            .Select(i => ToModel(i, links.Where(l => l.IndustryCode == i.Code).Select(l => l.CountryCode))).ToList();
    }

    public async Task<IndustryModel> GetAsync(int id, CancellationToken ct = default) => await ModelAsync(await FindAsync(id, ct), ct);

    public async Task<IndustryModel> CreateAsync(SaveIndustryRequest request, int? userId = null, CancellationToken ct = default)
    {
        var code = (request.Code ?? string.Empty).Trim().ToUpperInvariant();
        if (!CodePattern.IsMatch(code)) throw new BadRequestException("Industry code must be 1 to 30 characters: A-Z, 0-9 and underscore.");
        if (await _db.Industries.AnyAsync(i => i.Code == code, ct)) throw new ConflictException($"An industry with code '{code}' already exists.");

        var entity = new Industry { Code = code, IsActive = request.IsActive ?? true, CreatedBy = userId, ModifiedBy = userId };
        Apply(entity, request);
        await EnsureUniqueNameAsync(entity, ct);
        _db.Industries.Add(entity);
        await _db.SaveChangesAsync(ct);
        if (request.CountryCodes is not null) await SyncCountriesAsync(entity.Code, request.CountryCodes, userId, ct);
        return await ModelAsync(entity, ct);
    }

    public async Task<IndustryModel> UpdateAsync(int id, SaveIndustryRequest request, int? userId = null, CancellationToken ct = default)
    {
        var entity = await FindAsync(id, ct);
        if (!string.IsNullOrWhiteSpace(request.Code) && !string.Equals(request.Code.Trim(), entity.Code, StringComparison.OrdinalIgnoreCase))
            throw new BadRequestException("An industry's code cannot be changed.");

        Apply(entity, request);
        if (request.IsActive.HasValue) entity.IsActive = request.IsActive.Value;
        entity.ModifiedBy = userId;
        await EnsureUniqueNameAsync(entity, ct);
        await _db.SaveChangesAsync(ct);
        if (request.CountryCodes is not null) await SyncCountriesAsync(entity.Code, request.CountryCodes, userId, ct);
        return await ModelAsync(entity, ct);
    }

    public async Task<List<IndustryModel>> GetByCountryAsync(string countryCode, CancellationToken ct = default)
    {
        var country = NormalizeCountry(countryCode);
        var industries = await (from l in _db.CountryIndustries.AsNoTracking()
                                join i in _db.Industries.AsNoTracking() on l.IndustryCode equals i.Code
                                where l.CountryCode == country && l.IsActive && i.IsActive
                                select i).ToListAsync(ct);
        return industries.OrderBy(i => i.SortOrder).ThenBy(i => i.Name).Select(i => ToModel(i, [country])).ToList();
    }

    public async Task<IndustryModel> AssignToCountryAsync(int id, string countryCode, int? userId = null, CancellationToken ct = default)
    {
        var entity = await FindAsync(id, ct);
        await AssignAsync(entity.Code, NormalizeCountry(countryCode), userId, ct);
        await _db.SaveChangesAsync(ct);
        return await ModelAsync(entity, ct);
    }

    public async Task<IndustryModel> RemoveFromCountryAsync(int id, string countryCode, CancellationToken ct = default)
    {
        var entity = await FindAsync(id, ct);
        var country = NormalizeCountry(countryCode);
        var link = await _db.CountryIndustries.FirstOrDefaultAsync(c => c.CountryCode == country && c.IndustryCode == entity.Code, ct);
        if (link is not null)
        {
            _db.CountryIndustries.Remove(link);
            await _db.SaveChangesAsync(ct);
        }
        return await ModelAsync(entity, ct);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private async Task<Industry> FindAsync(int id, CancellationToken ct) =>
        await _db.Industries.FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw new NotFoundException("Industry not found.");

    private async Task<IndustryModel> ModelAsync(Industry i, CancellationToken ct) =>
        ToModel(i, await _db.CountryIndustries.AsNoTracking().Where(c => c.IndustryCode == i.Code && c.IsActive).Select(c => c.CountryCode).ToListAsync(ct));

    private static void Apply(Industry entity, SaveIndustryRequest r)
    {
        var name = (r.Name ?? string.Empty).Trim();
        if (name.Length == 0) throw new BadRequestException("Industry name cannot be empty.");
        if (name.Length > 100) throw new BadRequestException("Industry name is too long (100 characters).");
        var description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim();
        if (description is { Length: > 500 }) throw new BadRequestException("Description is too long (500 characters).");
        entity.Name = name;
        entity.Description = description;
        entity.SortOrder = r.SortOrder ?? entity.SortOrder;
    }

    private async Task EnsureUniqueNameAsync(Industry entity, CancellationToken ct)
    {
        if (await _db.Industries.AnyAsync(i => i.Id != entity.Id && i.Name.ToLower() == entity.Name.ToLower(), ct))
            throw new ConflictException($"An industry named '{entity.Name}' already exists.");
    }

    /// <summary>Adds the link, or re-activates an inactive one. Does not save.</summary>
    private async Task AssignAsync(string industryCode, string country, int? userId, CancellationToken ct)
    {
        var link = await _db.CountryIndustries.FirstOrDefaultAsync(c => c.CountryCode == country && c.IndustryCode == industryCode, ct);
        if (link is null)
            _db.CountryIndustries.Add(new CountryIndustry { CountryCode = country, IndustryCode = industryCode, IsActive = true, CreatedBy = userId });
        else
            link.IsActive = true;
    }

    private async Task SyncCountriesAsync(string industryCode, IEnumerable<string> wanted, int? userId, CancellationToken ct)
    {
        var target = wanted.Select(NormalizeCountry).ToHashSet();
        var current = await _db.CountryIndustries.Where(c => c.IndustryCode == industryCode).ToListAsync(ct);
        foreach (var link in current.Where(c => !target.Contains(c.CountryCode))) _db.CountryIndustries.Remove(link);
        foreach (var country in target) await AssignAsync(industryCode, country, userId, ct);
        await _db.SaveChangesAsync(ct);
    }
}

/// <summary>A51 P1 — the launch industries and which countries offer them. Idempotent by code: inserts what is missing and never
/// changes an existing row (a super admin's edits and removals of an assignment that already exists are kept; a deleted seed
/// assignment is re-added at the next start, so deactivate it instead).</summary>
internal sealed class IndustrySeeder
{
    internal static readonly (string Code, string Name, string Description, int Sort)[] Industries =
    [
        ("TRADING",       "Trading",       "Buying and selling goods: wholesale, distribution, import and export.", 1),
        ("MANUFACTURING", "Manufacturing", "Producing goods from raw materials or components.", 2),
        ("SERVICES",      "Services",      "Professional and field services billed by time or job.", 3),
        ("RETAIL",        "Retail",        "Selling to consumers through stores or counters.", 4),
    ];

    internal static readonly (string Country, string Industry)[] CountryIndustries =
    [
        ("PK", "TRADING"), ("PK", "MANUFACTURING"), ("PK", "SERVICES"), ("PK", "RETAIL"),
        ("AE", "TRADING"), ("AE", "SERVICES"),
        ("SA", "TRADING"), ("SA", "SERVICES"),
    ];

    private readonly OnboardingDbContext _db;

    public IndustrySeeder(OnboardingDbContext db) => _db = db;

    /// <summary>Returns the number of rows inserted.</summary>
    public async Task<int> SeedAsync(CancellationToken ct = default)
    {
        var added = 0;
        var codes = (await _db.Industries.Select(i => i.Code).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, name, description, sort) in Industries.Where(i => !codes.Contains(i.Code)))
        {
            _db.Industries.Add(new Industry { Code = code, Name = name, Description = description, SortOrder = sort });
            added++;
        }

        var links = (await _db.CountryIndustries.Select(c => c.CountryCode + "|" + c.IndustryCode).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (country, industry) in CountryIndustries.Where(c => !links.Contains(c.Country + "|" + c.Industry)))
        {
            _db.CountryIndustries.Add(new CountryIndustry { CountryCode = country, IndustryCode = industry });
            added++;
        }

        if (added > 0) await _db.SaveChangesAsync(ct);
        return added;
    }
}
