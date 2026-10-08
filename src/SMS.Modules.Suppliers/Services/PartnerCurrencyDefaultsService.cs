using Microsoft.EntityFrameworkCore;
using SMS.Modules.Suppliers.Data;
using SMS.Modules.Suppliers.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Suppliers.Services;

/// <summary>
/// A35 C4 / D-9 / D-14 — a partner's default currencies for new documents (BR-C4-02/03). Explicit organization: the
/// query ignores the EF tenant filter and filters on the organization it is given, so a super admin or a Hangfire job
/// never reads another organization's partner (absent → the domain base).
/// </summary>
internal sealed class PartnerCurrencyDefaultsService : IPartnerCurrencyDefaults
{
    private readonly SuppliersDbContext _db;
    private readonly IOrganizationCurrencyService? _orgCurrency;

    /// <param name="orgCurrency">Tenancy's base currencies. Optional: without it an unresolved base is <see cref="Guid.Empty"/>.</param>
    public PartnerCurrencyDefaultsService(SuppliersDbContext db, IOrganizationCurrencyService? orgCurrency = null)
    {
        _db = db;
        _orgCurrency = orgCurrency;
    }

    public async Task<PartnerCurrencyDefaults?> GetAsync(Guid organizationId, Guid partnerId, CancellationToken ct = default) =>
        await _db.BusinessPartners.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.OrganizationId == organizationId && p.UUID == partnerId && !p.IsDelete)
            .Select(p => new PartnerCurrencyDefaults(p.UUID, p.DefaultSaleCurrency, p.PreferredCurrency))
            .FirstOrDefaultAsync(ct);

    public async Task<Guid> ResolveDefaultCurrencyAsync(Guid organizationId, Guid partnerId, TransactionDomain domain, CancellationToken ct = default)
    {
        var defaults = await GetAsync(organizationId, partnerId, ct);
        var partnerDefault = domain switch
        {
            TransactionDomain.Sale     => defaults?.DefaultSaleCurrencyId,
            TransactionDomain.Purchase => defaults?.DefaultPurchaseCurrencyId,
            _                          => null
        };
        if (partnerDefault is { } id) return id;

        return _orgCurrency is null
            ? Guid.Empty
            : await _orgCurrency.GetBaseCurrencyIdAsync(organizationId, domain, ct) ?? Guid.Empty;
    }
}

/// <summary>
/// A35 BR-C4-01 — a partner default must be an active currency of the caller's organization; a value together with its
/// clear flag is refused. Only a value that differs from the stored one is checked, so an edit form that sends back a
/// since-deactivated default does not block the rest of the edit. Also fills the response codes.
/// </summary>
internal sealed class PartnerCurrencyRules
{
    private readonly IOrgCurrencyLookup? _orgCurrencies;
    private readonly ITenantContext? _tenant;
    private readonly ICurrencyCodeLookup? _codes;

    /// <param name="orgCurrencies">Finance's org currencies. Optional: without Finance nothing can be checked.</param>
    public PartnerCurrencyRules(IOrgCurrencyLookup? orgCurrencies = null, ITenantContext? tenant = null, ICurrencyCodeLookup? codes = null)
    {
        _orgCurrencies = orgCurrencies;
        _tenant = tenant;
        _codes = codes;
    }

    /// <summary>The purchase default a request asks for: defaultPurchaseCurrencyId wins over the preferredCurrency alias.</summary>
    public static Guid? Purchase(Guid? defaultPurchaseCurrencyId, Guid? preferredCurrency) => defaultPurchaseCurrencyId ?? preferredCurrency;

    public async Task ValidateAsync(
        Guid? sale, bool clearSale, Guid? purchase, bool clearPurchase,
        Guid? currentSale = null, Guid? currentPurchase = null, CancellationToken ct = default)
    {
        if (clearSale && sale is not null)
            throw new BadRequestException("Either choose a default sale currency or clear it, not both.");
        if (clearPurchase && purchase is not null)
            throw new BadRequestException("Either choose a default purchase currency or clear it, not both.");

        if (sale is { } s && s != currentSale) await EnsureActiveAsync(s, ct);
        if (purchase is { } p && p != currentPurchase) await EnsureActiveAsync(p, ct);
    }

    private async Task EnsureActiveAsync(Guid currencyId, CancellationToken ct)
    {
        if (currencyId == Guid.Empty)
            throw new BadRequestException("Choose a currency from this organization's currency list.");
        if (_orgCurrencies is null || _tenant is null) return;

        var info = await _orgCurrencies.GetAsync(_tenant.OrganizationId, currencyId, ct);
        if (info is { IsActive: true }) return;

        var code = info?.Code ?? await CodeAsync(currencyId, ct);
        throw new BadRequestException(
            $"{(string.IsNullOrWhiteSpace(code) ? "The selected currency" : code.Trim().ToUpperInvariant())} is not an active currency of this organization.");
    }

    public async Task<string?> CodeAsync(Guid? currencyId, CancellationToken ct = default) =>
        currencyId is { } id && id != Guid.Empty && _codes is not null ? await _codes.GetCodeAsync(id, ct) : null;

    public async Task FillCodesAsync(BusinessPartnerModel? m, CancellationToken ct = default)
    {
        if (m is null) return;
        m.DefaultSaleCurrencyCode = await CodeAsync(m.DefaultSaleCurrencyId, ct);
        m.DefaultPurchaseCurrencyCode = await CodeAsync(m.DefaultPurchaseCurrencyId, ct);
    }

    public async Task FillCodesAsync(SupplierDetailModel? d, CancellationToken ct = default)
    {
        if (d is null) return;
        d.DefaultSaleCurrencyCode = await CodeAsync(d.DefaultSaleCurrencyId, ct);
        d.DefaultPurchaseCurrencyCode = await CodeAsync(d.DefaultPurchaseCurrencyId, ct);
    }
}
