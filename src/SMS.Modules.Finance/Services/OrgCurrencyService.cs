using Microsoft.EntityFrameworkCore;
using SMS.Modules.Finance.Data;
using SMS.Modules.Finance.Domain;
using SMS.Modules.Finance.Models;
using SMS.Modules.Lookups.Models;
using SMS.Modules.Lookups.Services;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Finance.Services;

/// <summary>A35 C1 — api/currencies.</summary>
public interface IOrgCurrencyService
{
    Task<IReadOnlyList<OrgCurrencyModel>> ListAsync(bool includeInactive, CancellationToken ct = default);
    Task<OrgCurrencyModel> GetAsync(Guid currencyId, CancellationToken ct = default);
    Task<OrgCurrencyModel> CreateAsync(SaveOrgCurrencyRequest? req, int userId, CancellationToken ct = default);
    Task<OrgCurrencyModel> UpdateAsync(Guid currencyId, SaveOrgCurrencyRequest? req, int userId, CancellationToken ct = default);
}

/// <summary>
/// A35 C1 — an organization's currencies (BR-C1-01..05, D-1). Identity is the global catalog Guid; a code the catalog lacks
/// is added to it first. Writes and by-id reads are limited to the caller's own organization explicitly.
/// </summary>
internal sealed class OrgCurrencyService : IOrgCurrencyService
{
    private readonly FinanceDbContext                    _db;
    private readonly ILookupsService                     _lookups;
    private readonly IOrganizationCurrencyService?       _settings;
    private readonly IEnumerable<ICurrencyUsageChecker>  _checkers;
    private readonly TimeProvider                        _clock;

    public OrgCurrencyService(
        FinanceDbContext db, ILookupsService lookups, IOrganizationCurrencyService? settings,
        IEnumerable<ICurrencyUsageChecker> checkers, TimeProvider? clock = null)
    {
        _db       = db;
        _lookups  = lookups;
        _settings = settings;
        _checkers = checkers;
        _clock    = clock ?? TimeProvider.System;
    }

    private Guid     Org => _db.TenantContext.OrganizationId;
    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    private IQueryable<OrgCurrency> Own(Guid org) =>
        _db.OrgCurrencies.IgnoreQueryFilters().Where(c => c.OrganizationId == org);

    // ── Read ─────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<OrgCurrencyModel>> ListAsync(bool includeInactive, CancellationToken ct = default)
    {
        var org = Org;
        var q   = Own(org).AsNoTracking();
        if (!includeInactive) q = q.Where(c => c.IsActive);
        var rows = await q.OrderBy(c => c.DisplayOrder).ThenBy(c => c.Code).ToListAsync(ct);
        var roles = await RolesAsync(org, ct);
        return rows.Select(r => ToModel(r, roles)).ToList();
    }

    public async Task<OrgCurrencyModel> GetAsync(Guid currencyId, CancellationToken ct = default)
    {
        var org = Org;
        var row = await Own(org).AsNoTracking().FirstOrDefaultAsync(c => c.CurrencyId == currencyId, ct)
                  ?? throw new NotFoundException("That currency is not configured for this organization.");
        return ToModel(row, await RolesAsync(org, ct));
    }

    // ── Write ────────────────────────────────────────────────────────────────

    public async Task<OrgCurrencyModel> CreateAsync(SaveOrgCurrencyRequest? req, int userId, CancellationToken ct = default)
    {
        if (req is null) throw new BadRequestException("Send the currency to add.");
        var org  = Org;
        var code = ValidateCode(req.Code);

        if (await Own(org).AnyAsync(c => c.Code == code, ct))
            throw new ConflictException($"Currency {code} already exists");

        var catalog = _lookups.GetCurrencies()
            .FirstOrDefault(c => string.Equals(c.Code?.Trim(), code, StringComparison.OrdinalIgnoreCase));

        var name   = Clean(req.Name)   ?? catalog?.Name?.Trim();
        var symbol = Clean(req.Symbol) ?? Clean(catalog?.Symbol);
        ValidateNameSymbol(name, symbol);

        Guid currencyId;
        if (catalog is not null)
        {
            currencyId = catalog.Id;
            if (await Own(org).AnyAsync(c => c.CurrencyId == currencyId, ct))
                throw new ConflictException($"Currency {code} already exists");
        }
        else
        {
            currencyId = _lookups.CreateCurrency(new CreateCurrencyRequest { Code = code, Name = name!, Symbol = symbol });
        }

        var decimals = req.DecimalPlaces ?? CurrencyConventions.DefaultDecimalPlaces;
        ValidateFormat(decimals, req.Rounding, req.SymbolPosition);
        var maxOrder = await Own(org).Select(c => (int?)c.DisplayOrder).MaxAsync(ct) ?? 0;

        var entity = new OrgCurrency
        {
            OrganizationId = org,
            CurrencyId     = currencyId,
            Code           = code,
            Name           = name!,
            Symbol         = symbol!,
            DecimalPlaces  = decimals,
            Rounding       = req.Rounding ?? DefaultRounding(decimals),
            SymbolPosition = Position(req.SymbolPosition),
            IsActive       = req.IsActive ?? true,
            DisplayOrder   = req.DisplayOrder ?? maxOrder + 1,
            CreatedBy      = userId,
            CreatedDate    = Now
        };
        _db.OrgCurrencies.Add(entity);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw new ConflictException($"Currency {code} already exists");
        }
        return ToModel(entity, await RolesAsync(org, ct));
    }

    public async Task<OrgCurrencyModel> UpdateAsync(Guid currencyId, SaveOrgCurrencyRequest? req, int userId, CancellationToken ct = default)
    {
        if (req is null) throw new BadRequestException("Send the currency's new settings.");
        var org    = Org;
        var entity = await Own(org).FirstOrDefaultAsync(c => c.CurrencyId == currencyId, ct)
                     ?? throw new NotFoundException("That currency is not configured for this organization.");

        var name   = Clean(req.Name) ?? entity.Name;
        var symbol = Clean(req.Symbol) ?? entity.Symbol;
        ValidateNameSymbol(name, symbol);
        var decimals = req.DecimalPlaces ?? entity.DecimalPlaces;
        ValidateFormat(decimals, req.Rounding, req.SymbolPosition);

        var roles = await RolesAsync(org, ct);
        if (req.IsActive == false && entity.IsActive)
        {
            var bases = roles.BaseFor(currencyId);
            if (bases.Count > 0)
                throw new BadRequestException($"Cannot deactivate — used as {bases[0].ToLowerInvariant()} base currency");
            if (roles.RateCurrency == currencyId)
                throw new BadRequestException("Cannot deactivate — it is the organization's rate currency");
        }

        if (decimals != entity.DecimalPlaces)
        {
            foreach (var checker in _checkers)
            {
                var usage = await checker.DescribeCurrencyUsageAsync(org, currencyId, ct);
                if (usage is not null)
                    throw new ConflictException($"Cannot change decimal places — {usage}");
            }
        }

        entity.Name           = name;
        entity.Symbol         = symbol;
        if (req.Rounding is null && decimals != entity.DecimalPlaces) entity.Rounding = DefaultRounding(decimals);
        entity.DecimalPlaces  = decimals;
        if (req.Rounding is decimal r) entity.Rounding = r;
        if (req.SymbolPosition is not null) entity.SymbolPosition = Position(req.SymbolPosition);
        if (req.IsActive is bool a) entity.IsActive = a;
        if (req.DisplayOrder is int o) entity.DisplayOrder = o;
        entity.ModifiedBy   = userId;
        entity.ModifiedDate = Now;
        await _db.SaveChangesAsync(ct);

        return ToModel(entity, roles);
    }

    // ── Rules ────────────────────────────────────────────────────────────────

    internal static string ValidateCode(string? raw)
    {
        var code = raw?.Trim() ?? string.Empty;
        if (code.Length != 3) throw new BadRequestException("Currency code must be exactly 3 characters");
        if (code.Any(char.IsLower)) throw new BadRequestException("Currency code must be uppercase");
        if (!code.All(ch => ch is >= 'A' and <= 'Z')) throw new BadRequestException("Currency code must be letters A–Z");
        return code;
    }

    private static void ValidateNameSymbol(string? name, string? symbol)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 60) throw new BadRequestException("Name is required (max 60)");
        if (string.IsNullOrWhiteSpace(symbol) || symbol.Length > 5) throw new BadRequestException("Symbol is required (max 5)");
    }

    private static void ValidateFormat(int decimals, decimal? rounding, string? position)
    {
        if (decimals is < 0 or > 3) throw new BadRequestException("Decimal places must be between 0 and 3");
        if (rounding is decimal r && r <= 0m) throw new BadRequestException("Rounding must be greater than 0");
        if (position is not null && Position(position) is var p && p != CurrencyConventions.SymbolBefore && p != CurrencyConventions.SymbolAfter)
            throw new BadRequestException("Symbol position must be 'before' or 'after'");
    }

    private static string Position(string? p) => (p?.Trim().ToLowerInvariant()) switch
    {
        null or "" => CurrencyConventions.SymbolBefore,
        var v      => v
    };

    internal static decimal DefaultRounding(int decimals) => decimals switch { 0 => 1m, 1 => 0.1m, 3 => 0.001m, _ => 0.01m };

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private sealed record Roles(OrgCurrencySettingsSnapshot? Settings, Guid? RateCurrency)
    {
        public List<string> BaseFor(Guid currencyId)
        {
            var list = new List<string>();
            if (Settings is null) return list;
            if (Settings.SaleBaseCurrencyId == currencyId)     list.Add("SALE");
            if (Settings.PurchaseBaseCurrencyId == currencyId) list.Add("PURCHASE");
            if (Settings.ServiceBaseCurrencyId == currencyId)  list.Add("SERVICE");
            return list;
        }
    }

    private async Task<Roles> RolesAsync(Guid org, CancellationToken ct)
    {
        var settings = _settings is null ? null : await _settings.GetSettingsAsync(org, ct);
        var rc       = await new CurrencyRateReader(_db, _settings, org).RateCurrencyIdAsync(ct);
        return new Roles(settings, rc);
    }

    private static OrgCurrencyModel ToModel(OrgCurrency c, Roles roles) => new()
    {
        Id = c.Uuid, CurrencyId = c.CurrencyId, Code = c.Code, Name = c.Name, Symbol = c.Symbol,
        DecimalPlaces = c.DecimalPlaces, Rounding = c.Rounding, SymbolPosition = c.SymbolPosition,
        IsActive = c.IsActive, DisplayOrder = c.DisplayOrder,
        BaseFor = roles.BaseFor(c.CurrencyId), IsRateCurrency = roles.RateCurrency == c.CurrencyId,
        CreatedAt = c.CreatedDate, UpdatedAt = c.ModifiedDate
    };
}

/// <summary>A35 D-13 — <see cref="IOrgCurrencyLookup"/> for other modules. Explicit organization.</summary>
internal sealed class OrgCurrencyLookup : IOrgCurrencyLookup
{
    private readonly FinanceDbContext _db;
    public OrgCurrencyLookup(FinanceDbContext db) => _db = db;

    private IQueryable<OrgCurrency> Own(Guid org) =>
        _db.OrgCurrencies.IgnoreQueryFilters().AsNoTracking().Where(c => c.OrganizationId == org);

    public async Task<OrgCurrencyInfo?> GetAsync(Guid organizationId, Guid currencyId, CancellationToken ct = default) =>
        ToInfo(await Own(organizationId).FirstOrDefaultAsync(c => c.CurrencyId == currencyId, ct));

    public async Task<OrgCurrencyInfo?> GetByCodeAsync(Guid organizationId, string code, CancellationToken ct = default)
    {
        var upper = code?.Trim().ToUpperInvariant() ?? string.Empty;
        return ToInfo(await Own(organizationId).FirstOrDefaultAsync(c => c.Code == upper, ct));
    }

    public async Task<IReadOnlyList<OrgCurrencyInfo>> ListAsync(Guid organizationId, bool activeOnly, CancellationToken ct = default)
    {
        var q = Own(organizationId);
        if (activeOnly) q = q.Where(c => c.IsActive);
        var rows = await q.OrderBy(c => c.DisplayOrder).ThenBy(c => c.Code).ToListAsync(ct);
        return rows.Select(r => ToInfo(r)!).ToList();
    }

    public async Task<int> GetDecimalPlacesAsync(Guid organizationId, Guid currencyId, CancellationToken ct = default) =>
        await Own(organizationId).Where(c => c.CurrencyId == currencyId).Select(c => (int?)c.DecimalPlaces).FirstOrDefaultAsync(ct)
        ?? CurrencyConventions.DefaultDecimalPlaces;

    private static OrgCurrencyInfo? ToInfo(OrgCurrency? c) => c is null
        ? null
        : new OrgCurrencyInfo(c.CurrencyId, c.Code, c.Name, c.Symbol, c.DecimalPlaces, c.Rounding, c.SymbolPosition, c.IsActive, c.DisplayOrder);
}
