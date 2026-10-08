using Microsoft.EntityFrameworkCore;
using SMS.Modules.Tenancy.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Tenancy.Services;

// Implements the SMS.Shared.Common cross-module interface so other modules resolve an organization's base currencies
// without a project reference to Tenancy — mirrors OrganizationStatusService.
//
// A35 D-7: reads tenant.organization_currency_settings. Every query filters on the organization it is given (Tenancy's
// tables carry no EF tenant filter; another organization's row is never read).
internal sealed class OrganizationCurrencyService : IOrganizationCurrencyService
{
    private readonly TenancyDbContext _db;
    private readonly IOrgCurrencyLookup? _orgCurrencies;

    /// <param name="orgCurrencies">
    /// Finance's per-organization currency list, used only for the PKR fallback. Optional: a host without Finance
    /// still answers from the settings row and Organization.BaseCurrency.
    /// </param>
    public OrganizationCurrencyService(TenancyDbContext db, IOrgCurrencyLookup? orgCurrencies = null)
    {
        _db = db;
        _orgCurrencies = orgCurrencies;
    }

    public async Task<Guid?> GetBaseCurrencyIdAsync(Guid organizationId)
    {
        var stored = await _db.OrganizationCurrencySettings.AsNoTracking()
            .Where(s => s.OrganizationId == organizationId)
            .Select(s => (Guid?)s.SaleBaseCurrencyId)
            .FirstOrDefaultAsync();
        if (stored is not null) return stored;

        // No PKR fallback on the legacy read: null keeps meaning "not configured" (QuickBooks preflight, sale pricing).
        return await _db.Organizations.AsNoTracking()
            .Where(o => o.Id == organizationId)
            .Select(o => o.BaseCurrency)
            .FirstOrDefaultAsync();
    }

    public async Task<Guid?> GetBaseCurrencyIdAsync(Guid organizationId, TransactionDomain domain, CancellationToken ct = default)
    {
        var id = (await GetSettingsAsync(organizationId, ct)).BaseFor(domain);
        return id == Guid.Empty ? null : id;
    }

    public async Task<OrgCurrencySettingsSnapshot> GetSettingsAsync(Guid organizationId, CancellationToken ct = default)
    {
        var row = await _db.OrganizationCurrencySettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.OrganizationId == organizationId, ct);
        if (row is not null)
            return new OrgCurrencySettingsSnapshot(organizationId, row.SaleBaseCurrencyId, row.PurchaseBaseCurrencyId,
                row.ServiceBaseCurrencyId, row.RateCurrencyId, row.ExchangeGainAccountCode, row.ExchangeLossAccountCode,
                row.UnrealizedGainAccountCode, row.UnrealizedLossAccountCode, IsStored: true);

        var fallback = await FallbackCurrencyAsync(organizationId, ct) ?? Guid.Empty;
        return new OrgCurrencySettingsSnapshot(organizationId, fallback, fallback, fallback, fallback,
            null, null, null, null, IsStored: false);
    }

    /// <summary>D-7: Organization.BaseCurrency, else the organization's PKR currency; null when neither exists.</summary>
    internal async Task<Guid?> FallbackCurrencyAsync(Guid organizationId, CancellationToken ct = default)
    {
        var baseCurrency = await _db.Organizations.AsNoTracking()
            .Where(o => o.Id == organizationId)
            .Select(o => o.BaseCurrency)
            .FirstOrDefaultAsync(ct);
        if (baseCurrency is not null) return baseCurrency;
        if (_orgCurrencies is null) return null;

        var pkr = await _orgCurrencies.GetByCodeAsync(organizationId, CurrencyConventions.FallbackCurrencyCode, ct);
        return pkr?.CurrencyId;
    }
}
