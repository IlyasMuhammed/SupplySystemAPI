using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SMS.Modules.Pos.Data;
using SMS.Modules.Pos.Domain;
using SMS.Modules.Pos.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Pos.Services;

/// <summary>A38 API-CONTRACT §6 — POS settings of products (D-6) and customers (D-12). Every save bumps ModifiedAt (sync deltas).</summary>
public interface IPosSettingsService
{
    Task<PosProductSettingsModel> GetProductAsync(Guid productUuid, CancellationToken ct = default);
    Task<PosProductSettingsModel> PutProductAsync(Guid productUuid, PosProductSettingsModel req, int userId, CancellationToken ct = default);
    Task<PosBulkResultModel> BulkProductsAsync(PosProductSettingsBulkRequest req, int userId, CancellationToken ct = default);
    Task<PosCustomerSettingsModel> GetCustomerAsync(Guid customerUuid, CancellationToken ct = default);
    Task<PosCustomerSettingsModel> PutCustomerAsync(Guid customerUuid, PosCustomerSettingsModel req, int userId, CancellationToken ct = default);
}

internal sealed partial class PosSettingsService : IPosSettingsService
{
    internal const int MaxBulk = 500;

    private readonly PosDbContext         _db;
    private readonly ITenantContext       _tenant;
    private readonly TimeProvider         _clock;
    private readonly IPosCatalogSource?   _catalog;
    private readonly IPosCustomerSource?  _customers;
    private readonly IPosChangeListener?  _changes;

    public PosSettingsService(
        PosDbContext db, ITenantContext tenant, IPosCatalogSource? catalog = null, IPosCustomerSource? customers = null,
        IPosChangeListener? changes = null, TimeProvider? clock = null)
    {
        _db = db; _tenant = tenant; _catalog = catalog; _customers = customers; _changes = changes; _clock = clock ?? TimeProvider.System;
    }

    private Guid Org => _tenant.OrganizationId;

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex ColorPattern();

    // ── products ──

    public async Task<PosProductSettingsModel> GetProductAsync(Guid productUuid, CancellationToken ct = default)
    {
        await EnsureProductsAsync([productUuid], ct);
        var s = await _db.ProductSettings.AsNoTracking().FirstOrDefaultAsync(x => x.ProductUuid == productUuid, ct);
        return s is null ? new PosProductSettingsModel { ProductUuid = productUuid } : Model(s);
    }

    public async Task<PosProductSettingsModel> PutProductAsync(Guid productUuid, PosProductSettingsModel req, int userId, CancellationToken ct = default)
    {
        req.ProductUuid = productUuid;
        var saved = await UpsertProductsAsync([req], userId, ct);
        return Model(saved[0]);
    }

    public async Task<PosBulkResultModel> BulkProductsAsync(PosProductSettingsBulkRequest req, int userId, CancellationToken ct = default)
    {
        var items = req?.Items ?? [];
        if (items.Count == 0) throw new BadRequestException("Nothing to update.");
        if (items.Count > MaxBulk) throw new BadRequestException($"At most {MaxBulk} products per bulk update.");
        if (items.Any(i => i.ProductUuid is null || i.ProductUuid == Guid.Empty)) throw new BadRequestException("Every item needs its productUuid.");
        if (items.Select(i => i.ProductUuid).Distinct().Count() != items.Count) throw new BadRequestException("A product appears twice in the update.");
        return new PosBulkResultModel { Updated = (await UpsertProductsAsync(items, userId, ct)).Count };
    }

    private async Task<List<PosProductSettings>> UpsertProductsAsync(IReadOnlyList<PosProductSettingsModel> items, int userId, CancellationToken ct)
    {
        var ids = items.Select(i => i.ProductUuid!.Value).ToList();
        var variants = await EnsureProductsAsync(ids, ct);
        var categories = items.Any(i => i.PosCategoryId is not null) && _catalog is not null
            ? (await _catalog.GetCategoriesAsync(Org, ct)).Select(c => c.Id).ToHashSet() : null;

        foreach (var i in items)
        {
            if (i.PosName is { Length: > 100 }) throw new BadRequestException("The POS name can have at most 100 characters.");
            if (!string.IsNullOrWhiteSpace(i.Color) && !ColorPattern().IsMatch(i.Color.Trim())) throw new BadRequestException("Colour must be #RRGGBB.");
            if (i.ImageUrl is { Length: > 500 }) throw new BadRequestException("The image URL can have at most 500 characters.");
            if (i.PosCategoryId is { } cat && categories is not null && !categories.Contains(cat)) throw new BadRequestException("Unknown POS category.");
        }

        var existing = await _db.ProductSettings.Where(s => ids.Contains(s.ProductUuid)).ToDictionaryAsync(s => s.ProductUuid, ct);
        var now = _clock.GetUtcNow().UtcDateTime;
        var saved = new List<PosProductSettings>();
        foreach (var i in items)
        {
            var uuid = i.ProductUuid!.Value;
            if (!existing.TryGetValue(uuid, out var s))
                _db.ProductSettings.Add(s = new PosProductSettings { OrganizationId = Org, ProductUuid = uuid });
            s.PosName = Clean(i.PosName); s.PosCategoryId = i.PosCategoryId; s.Color = Clean(i.Color)?.ToUpperInvariant();
            s.ImageUrl = Clean(i.ImageUrl); s.IsWeighable = i.IsWeighable; s.IsFavorite = i.IsFavorite; s.SortOrder = i.SortOrder;
            s.IsPosActive = i.IsPosActive; s.ModifiedBy = userId;
            s.ModifiedAt = now; // always bumped (also with no other change) so terminals re-read the item
            _db.Entry(s).Property(x => x.ModifiedAt).IsModified = _db.Entry(s).State != EntityState.Added;
            saved.Add(s);
        }
        await _db.SaveChangesAsync(ct);

        if (_changes is not null && variants.Count > 0) await _changes.OnVariantsChangedAsync(Org, variants, ct);
        return saved;
    }

    /// <summary>404 for a product the catalog does not know (when the catalog is available); answers its variants.</summary>
    private async Task<IReadOnlyList<Guid>> EnsureProductsAsync(IReadOnlyCollection<Guid> productUuids, CancellationToken ct)
    {
        if (_catalog is null) return [];
        var variants = await _catalog.GetByProductUuidsAsync(Org, productUuids, ct);
        if (productUuids.FirstOrDefault(p => variants.All(v => v.ProductUuid != p)) is var missing && missing != Guid.Empty)
            throw new NotFoundException("Product", missing);
        return variants.Select(v => v.VariantUuid).ToList();
    }

    private static PosProductSettingsModel Model(PosProductSettings s) => new()
    {
        ProductUuid = s.ProductUuid, PosName = s.PosName, PosCategoryId = s.PosCategoryId, Color = s.Color, ImageUrl = s.ImageUrl,
        IsWeighable = s.IsWeighable, IsFavorite = s.IsFavorite, SortOrder = s.SortOrder, IsPosActive = s.IsPosActive, ModifiedAt = s.ModifiedAt
    };

    // ── customers ──

    public async Task<PosCustomerSettingsModel> GetCustomerAsync(Guid customerUuid, CancellationToken ct = default)
    {
        await CustomerAsync(customerUuid, ct);
        var s = await _db.CustomerSettings.AsNoTracking().FirstOrDefaultAsync(x => x.CustomerUuid == customerUuid, ct);
        return s is null ? new PosCustomerSettingsModel() : Model(s);
    }

    public async Task<PosCustomerSettingsModel> PutCustomerAsync(Guid customerUuid, PosCustomerSettingsModel req, int userId, CancellationToken ct = default)
    {
        var customer = await CustomerAsync(customerUuid, ct);
        if (req.AllowPosCredit && customer is not null && PosOrderService.IsWalkIn(customer))
            throw new BadRequestException("Walk-In customers cannot use credit.");
        if (req.OfflineCreditLimitOverride < 0 || req.MaxSingleCreditOrder < 0) throw new BadRequestException("Credit amounts cannot be negative.");
        if (req.Notes is { Length: > 1000 }) throw new BadRequestException("Notes can have at most 1000 characters.");

        var s = await _db.CustomerSettings.FirstOrDefaultAsync(x => x.CustomerUuid == customerUuid, ct);
        if (s is null) _db.CustomerSettings.Add(s = new PosCustomerSettings { OrganizationId = Org, CustomerUuid = customerUuid });
        s.AllowPosCredit = req.AllowPosCredit;
        s.OfflineCreditLimitOverride = req.OfflineCreditLimitOverride is { } o ? PosTotalsCalculator.Round(o) : null;
        s.MaxSingleCreditOrder = req.MaxSingleCreditOrder is { } m ? PosTotalsCalculator.Round(m) : null;
        s.RequiresIdForCredit = req.RequiresIdForCredit;
        s.Notes = Clean(req.Notes);
        s.ModifiedBy = userId;
        s.ModifiedAt = _clock.GetUtcNow().UtcDateTime;
        _db.Entry(s).Property(x => x.ModifiedAt).IsModified = _db.Entry(s).State != EntityState.Added;
        await _db.SaveChangesAsync(ct);
        return Model(s);
    }

    private async Task<PosCustomer?> CustomerAsync(Guid customerUuid, CancellationToken ct)
    {
        if (_customers is null) return null;
        return await _customers.GetAsync(Org, customerUuid, ct) ?? throw new NotFoundException("Customer", customerUuid);
    }

    private static PosCustomerSettingsModel Model(PosCustomerSettings s) => new()
    {
        AllowPosCredit = s.AllowPosCredit, OfflineCreditLimitOverride = s.OfflineCreditLimitOverride, MaxSingleCreditOrder = s.MaxSingleCreditOrder,
        RequiresIdForCredit = s.RequiresIdForCredit, Notes = s.Notes, ModifiedAt = s.ModifiedAt
    };

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
