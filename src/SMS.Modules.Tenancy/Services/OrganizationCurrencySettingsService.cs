using Microsoft.EntityFrameworkCore;
using SMS.Modules.Tenancy.Data;
using SMS.Modules.Tenancy.Data.Maps;
using SMS.Modules.Tenancy.Domain;
using SMS.Modules.Tenancy.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Tenancy.Services;

/// <summary>A35 P2-03 — the organization currency settings screen (API-CONTRACT.md §4). Always the organization given.</summary>
public interface IOrganizationCurrencySettingsService
{
    Task<OrgCurrencySettingsModel> GetAsync(Guid organizationId, CancellationToken ct = default);
    Task<OrgCurrencySettingsModel> UpdateAsync(Guid organizationId, UpdateOrgCurrencySettingsRequest req, int userId, CancellationToken ct = default);

    /// <summary>
    /// The organization profile edit changes Organization.BaseCurrency: refuse (409) when the sale base is in use, else move
    /// the sale base ONLY (D-7 mirror). With no row yet, one is first created from the current snapshot so the purchase /
    /// service bases and the rate currency keep their values instead of following Organization.BaseCurrency (REV-06).
    /// Changes are tracked, saved by the caller's SaveChanges.
    /// </summary>
    Task ApplyProfileBaseCurrencyAsync(Guid organizationId, Guid newBaseCurrencyId, CancellationToken ct = default);

    /// <summary>
    /// The profile edit clears Organization.BaseCurrency: 400 once a settings row is stored; 409 when any domain base (or
    /// the rate currency) is in use, since every domain would silently move to the PKR fallback (REV-06).
    /// </summary>
    Task EnsureProfileBaseCurrencyCanBeClearedAsync(Guid organizationId, CancellationToken ct = default);
}

/// <summary>
/// A35 P2-03 (D-7, D-8): BR-C3-02 every base (and a changed rate currency) is an active currency of this organization;
/// BR-C3-03 a domain base whose locked documents exist cannot change (409, usage text from every
/// <see cref="ICurrencyUsageChecker"/>); the rate currency is read-only in A35 (any change → 409, REV-07/D-2). Only values
/// that change are checked,
/// so editing the GL codes never trips over a base that is in use. Saving the sale base also sets
/// Organization.BaseCurrency.
/// </summary>
internal sealed class OrganizationCurrencySettingsService : IOrganizationCurrencySettingsService
{
    private readonly TenancyDbContext _db;
    private readonly IReadOnlyList<ICurrencyUsageChecker> _usage;
    private readonly IOrgCurrencyLookup? _orgCurrencies;
    private readonly ICurrencyCodeLookup? _codes;
    private readonly OrganizationCurrencyService _reader;

    /// <param name="orgCurrencies">Finance's org currency list (BR-C3-02). Optional: without Finance nothing can be checked.</param>
    /// <param name="codes">Lookups' code for each id, for the response and messages. Optional.</param>
    public OrganizationCurrencySettingsService(
        TenancyDbContext db, IEnumerable<ICurrencyUsageChecker> usage,
        IOrgCurrencyLookup? orgCurrencies = null, ICurrencyCodeLookup? codes = null)
    {
        _db = db;
        _usage = usage.ToList();
        _orgCurrencies = orgCurrencies;
        _codes = codes;
        _reader = new OrganizationCurrencyService(db, orgCurrencies);
    }

    public async Task<OrgCurrencySettingsModel> GetAsync(Guid organizationId, CancellationToken ct = default)
    {
        await EnsureOrganizationAsync(organizationId, ct);
        var s = await _reader.GetSettingsAsync(organizationId, ct);

        var model = new OrgCurrencySettingsModel
        {
            SaleBaseCurrencyId = s.SaleBaseCurrencyId,
            SaleBaseCurrencyCode = await CodeAsync(organizationId, s.SaleBaseCurrencyId, ct),
            PurchaseBaseCurrencyId = s.PurchaseBaseCurrencyId,
            PurchaseBaseCurrencyCode = await CodeAsync(organizationId, s.PurchaseBaseCurrencyId, ct),
            ServiceBaseCurrencyId = s.ServiceBaseCurrencyId,
            ServiceBaseCurrencyCode = await CodeAsync(organizationId, s.ServiceBaseCurrencyId, ct),
            RateCurrencyId = s.RateCurrencyId,
            RateCurrencyCode = await CodeAsync(organizationId, s.RateCurrencyId, ct),
            ExchangeGainAccountCode = s.ExchangeGainAccountCode,
            ExchangeLossAccountCode = s.ExchangeLossAccountCode,
            UnrealizedGainAccountCode = s.UnrealizedGainAccountCode,
            UnrealizedLossAccountCode = s.UnrealizedLossAccountCode,
            IsStored = s.IsStored
        };
        model.Locks.Sale = Lock(await DomainUsageAsync(organizationId, TransactionDomain.Sale, ct));
        model.Locks.Purchase = Lock(await DomainUsageAsync(organizationId, TransactionDomain.Purchase, ct));
        model.Locks.Service = Lock(await DomainUsageAsync(organizationId, TransactionDomain.Service, ct));
        model.Locks.RateCurrency = Lock(await RateUsageAsync(organizationId, ct));
        return model;
    }

    public async Task<OrgCurrencySettingsModel> UpdateAsync(
        Guid organizationId, UpdateOrgCurrencySettingsRequest req, int userId, CancellationToken ct = default)
    {
        var org = await EnsureOrganizationAsync(organizationId, ct);
        var codes = new[]
        {
            Clean(req.ExchangeGainAccountCode), Clean(req.ExchangeLossAccountCode),
            Clean(req.UnrealizedGainAccountCode), Clean(req.UnrealizedLossAccountCode)
        };
        if (codes.Any(c => c is not null && c.Length > OrganizationCurrencySettingsMap.AccountCodeLength))
            throw new BadRequestException("Account codes can be at most 20 characters");

        var current = await _reader.GetSettingsAsync(organizationId, ct);
        var rateCurrency = req.RateCurrencyId ?? current.RateCurrencyId;

        var wanted = new (TransactionDomain Domain, Guid Id)[]
        {
            (TransactionDomain.Sale, req.SaleBaseCurrencyId),
            (TransactionDomain.Purchase, req.PurchaseBaseCurrencyId),
            (TransactionDomain.Service, req.ServiceBaseCurrencyId)
        };

        // BR-C3-02 first (a 400 is about the input), then BR-C3-03 (a 409 is about the data).
        foreach (var (domain, id) in wanted)
            if (id != current.BaseFor(domain))
                await EnsureActiveOrgCurrencyAsync(organizationId, id, ct);
        // REV-07 / D-2 — the rate currency is fixed in A35 (Finance's SYSTEM 1.0 row was seeded for it); read-only here.
        if (rateCurrency != current.RateCurrencyId)
            throw new ConflictException("The rate currency can't be changed.");

        foreach (var (domain, id) in wanted)
        {
            if (id == current.BaseFor(domain)) continue;
            var usage = await DomainUsageAsync(organizationId, domain, ct);
            if (usage is not null)
                throw new ConflictException(
                    $"Cannot change the {CurrencyConventions.DomainCode(domain).ToLowerInvariant()} base currency — {usage}.");
        }
        var now = DateTime.UtcNow;
        var row = await _db.OrganizationCurrencySettings.FirstOrDefaultAsync(s => s.OrganizationId == organizationId, ct);
        if (row is null)
        {
            row = new OrganizationCurrencySettings { OrganizationId = organizationId, CreatedAt = now };
            _db.OrganizationCurrencySettings.Add(row);
        }

        row.SaleBaseCurrencyId = req.SaleBaseCurrencyId;
        row.PurchaseBaseCurrencyId = req.PurchaseBaseCurrencyId;
        row.ServiceBaseCurrencyId = req.ServiceBaseCurrencyId;
        row.RateCurrencyId = rateCurrency;
        row.ExchangeGainAccountCode = codes[0];
        row.ExchangeLossAccountCode = codes[1];
        row.UnrealizedGainAccountCode = codes[2];
        row.UnrealizedLossAccountCode = codes[3];
        row.UpdatedAt = now;
        row.ModifiedBy = userId;

        // D-7 mirror for the legacy readers.
        if (org.BaseCurrency != req.SaleBaseCurrencyId)
        {
            org.BaseCurrency = req.SaleBaseCurrencyId;
            org.ModifiedBy = userId;
            org.ModifiedDate = now;
        }

        await _db.SaveChangesAsync(ct);
        return await GetAsync(organizationId, ct);
    }

    public async Task ApplyProfileBaseCurrencyAsync(Guid organizationId, Guid newBaseCurrencyId, CancellationToken ct = default)
    {
        var current = await _reader.GetSettingsAsync(organizationId, ct);
        if (current.SaleBaseCurrencyId == newBaseCurrencyId) return;

        if (await DomainUsageAsync(organizationId, TransactionDomain.Sale, ct) is { } usage)
            throw new ConflictException($"Cannot change the sale base currency — {usage}.");

        var now = DateTime.UtcNow;
        var row = await _db.OrganizationCurrencySettings.FirstOrDefaultAsync(s => s.OrganizationId == organizationId, ct);
        if (row is null)
        {
            // REV-06 — without a row every domain reads from Organization.BaseCurrency, so changing it would silently move
            // the purchase / service bases (and the rate currency) too. Pin them to their current values first.
            Guid Keep(Guid id) => id == Guid.Empty ? newBaseCurrencyId : id;
            row = new OrganizationCurrencySettings
            {
                OrganizationId = organizationId,
                PurchaseBaseCurrencyId = Keep(current.PurchaseBaseCurrencyId),
                ServiceBaseCurrencyId = Keep(current.ServiceBaseCurrencyId),
                RateCurrencyId = Keep(current.RateCurrencyId),
                CreatedAt = now
            };
            _db.OrganizationCurrencySettings.Add(row);
        }
        row.SaleBaseCurrencyId = newBaseCurrencyId;
        row.UpdatedAt = now;
    }

    public async Task EnsureProfileBaseCurrencyCanBeClearedAsync(Guid organizationId, CancellationToken ct = default)
    {
        if (await _db.OrganizationCurrencySettings.AnyAsync(s => s.OrganizationId == organizationId, ct))
            throw new BadRequestException(
                "This organization's currency settings are saved; its base currency is the sale base currency under Settings → Currency Configuration and cannot be cleared.");

        foreach (var domain in new[] { TransactionDomain.Sale, TransactionDomain.Purchase, TransactionDomain.Service })
            if (await DomainUsageAsync(organizationId, domain, ct) is { } usage)
                throw new ConflictException(
                    $"Cannot clear the base currency — the {CurrencyConventions.DomainCode(domain).ToLowerInvariant()} base is in use ({usage}).");
        if (await RateUsageAsync(organizationId, ct) is { } rateUsage)
            throw new ConflictException($"Cannot clear the base currency — the rate currency is in use ({rateUsage}).");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<Organization> EnsureOrganizationAsync(Guid organizationId, CancellationToken ct) =>
        await _db.Organizations.FirstOrDefaultAsync(o => o.Id == organizationId, ct)
        ?? throw new NotFoundException("Organization", organizationId);

    private async Task EnsureActiveOrgCurrencyAsync(Guid organizationId, Guid currencyId, CancellationToken ct)
    {
        if (currencyId == Guid.Empty)
            throw new BadRequestException("Choose a currency from this organization's currency list.");
        if (_orgCurrencies is null) return;

        var info = await _orgCurrencies.GetAsync(organizationId, currencyId, ct);
        if (info is { IsActive: true }) return;

        var code = info?.Code ?? (_codes is null ? null : await _codes.GetCodeAsync(currencyId, ct));
        throw new BadRequestException(
            $"{(string.IsNullOrWhiteSpace(code) ? "The selected currency" : code.Trim().ToUpperInvariant())} is not an active currency of this organization.");
    }

    private async Task<string?> DomainUsageAsync(Guid organizationId, TransactionDomain domain, CancellationToken ct)
    {
        foreach (var checker in _usage)
            if (await checker.DescribeDomainBaseUsageAsync(organizationId, domain, ct) is { } usage)
                return usage;
        return null;
    }

    private async Task<string?> RateUsageAsync(Guid organizationId, CancellationToken ct)
    {
        foreach (var checker in _usage)
            if (await checker.DescribeRateCurrencyUsageAsync(organizationId, ct) is { } usage)
                return usage;
        return null;
    }

    private async Task<string?> CodeAsync(Guid organizationId, Guid currencyId, CancellationToken ct)
    {
        if (currencyId == Guid.Empty) return null;
        if (_orgCurrencies is not null && await _orgCurrencies.GetAsync(organizationId, currencyId, ct) is { } info)
            return info.Code;
        return _codes is null ? null : await _codes.GetCodeAsync(currencyId, ct);
    }

    private static OrgCurrencySettingsLock Lock(string? usage) => new() { Locked = usage is not null, Reason = usage };

    private static string? Clean(string? code) => string.IsNullOrWhiteSpace(code) ? null : code.Trim();
}

/// <summary>
/// A35 P1-14 (settings part) — a new organization gets its settings row (all four currencies = its base currency). An
/// organization without a base currency gets none (D-7: a row's sale base must equal Organization.BaseCurrency; the
/// missing row already reads as PKR). Idempotent; also run for every organization at startup, which repairs a missed call.
/// Independent of the order of CUR's handler (which reads the rate currency through GetSettingsAsync).
/// </summary>
internal sealed class OrganizationCurrencySettingsProvisioningHandler : IOrganizationProvisionedHandler
{
    private readonly TenancyDbContext _db;

    public OrganizationCurrencySettingsProvisioningHandler(TenancyDbContext db, IOrgCurrencyLookup? orgCurrencies = null) => _db = db;

    public async Task OnOrganizationProvisionedAsync(Guid organizationId, CancellationToken ct = default)
    {
        var baseCurrency = await _db.Organizations.AsNoTracking()
            .Where(o => o.Id == organizationId)
            .Select(o => o.BaseCurrency)
            .FirstOrDefaultAsync(ct);
        if (baseCurrency is not { } id) return;
        if (await _db.OrganizationCurrencySettings.AnyAsync(s => s.OrganizationId == organizationId, ct)) return;

        var now = DateTime.UtcNow;
        _db.OrganizationCurrencySettings.Add(new OrganizationCurrencySettings
        {
            OrganizationId = organizationId,
            SaleBaseCurrencyId = id, PurchaseBaseCurrencyId = id, ServiceBaseCurrencyId = id, RateCurrencyId = id,
            CreatedAt = now, UpdatedAt = now
        });
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost a race with another writer of the same organization's row (PK): the row exists, which is the goal.
            _db.ChangeTracker.Clear();
        }
    }

    /// <summary>Startup: every organization with a base currency and no row.</summary>
    public async Task BackfillAsync(CancellationToken ct = default)
    {
        var missing = await _db.Organizations.AsNoTracking()
            .Where(o => o.BaseCurrency != null && !_db.OrganizationCurrencySettings.Any(s => s.OrganizationId == o.Id))
            .Select(o => o.Id)
            .ToListAsync(ct);
        foreach (var id in missing)
            await OnOrganizationProvisionedAsync(id, ct);
    }
}
