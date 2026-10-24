using Microsoft.EntityFrameworkCore;
using SMS.Modules.Pos.Data;
using SMS.Modules.Pos.Domain;
using SMS.Modules.Pos.Models;
using SMS.Shared.Common;
using SMS.Shared.Exceptions;

namespace SMS.Modules.Pos.Services;

/// <summary>
/// A38 API-CONTRACT §7 — what the PWA caches: config, catalog and customer deltas (two sources merged, see
/// <see cref="Window"/>), the store warehouse's stock snapshot and, for a bound device only, the store staff's PIN hashes.
/// </summary>
public interface IPosSyncService
{
    Task<PosSyncConfigModel> ConfigAsync(Guid terminalUuid, CancellationToken ct = default);
    Task<PosCatalogPageModel> CatalogAsync(Guid terminalUuid, DateTime? since, int limit, CancellationToken ct = default);
    Task<PosStockSnapshotModel> StockAsync(Guid terminalUuid, CancellationToken ct = default);
    Task<PosCustomersPageModel> CustomersAsync(Guid terminalUuid, DateTime? since, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<PosSyncUserModel>> UsersAsync(Guid terminalUuid, string? deviceHeader, CancellationToken ct = default);
}

internal sealed class PosSyncService : IPosSyncService
{
    internal const int DefaultLimit = 500, MaxLimit = 1000;
    internal const string DeviceHeader = "X-Pos-Device";

    private readonly PosDbContext            _db;
    private readonly ITenantContext          _tenant;
    private readonly PosReadModel            _read;
    private readonly IPosCatalogSource?      _catalog;
    private readonly IPosStockPoster?        _stock;
    private readonly IPosCustomerSource?     _customers;
    private readonly ICustomerBalanceLookup? _balances;
    private readonly IPosPinService?         _pins;
    private readonly IModuleGate?            _gate;

    public PosSyncService(
        PosDbContext db, ITenantContext tenant, PosReadModel read, IPosCatalogSource? catalog = null, IPosStockPoster? stock = null,
        IPosCustomerSource? customers = null, ICustomerBalanceLookup? balances = null, IPosPinService? pins = null, IModuleGate? gate = null)
    {
        _db = db; _tenant = tenant; _read = read; _catalog = catalog; _stock = stock; _customers = customers; _balances = balances;
        _pins = pins; _gate = gate;
    }

    private Guid Org => _tenant.OrganizationId;

    private async Task<PosTerminal> TerminalAsync(Guid uuid, CancellationToken ct) =>
        await _db.Terminals.Include(t => t.Store).FirstOrDefaultAsync(t => t.Uuid == uuid, ct) ?? throw new NotFoundException("Terminal", uuid);

    // ── config ──

    public async Task<PosSyncConfigModel> ConfigAsync(Guid terminalUuid, CancellationToken ct = default)
    {
        var terminal = await TerminalAsync(terminalUuid, ct);
        var tax = await _read.StoreTaxAsync(terminal.Store, ct);
        var credit = await _gate.IsOnAsync(Org, PosFeatureCodes.CreditSales, ct);
        var creditNotes = await _gate.IsOnAsync(Org, PosFeatureCodes.CreditNotes, ct);
        return new PosSyncConfigModel
        {
            ServerTime = _read.UtcNow, Terminal = await _read.TerminalAsync(terminal, ct), Store = await _read.StoreAsync(terminal.Store, ct),
            TaxCode = new PosSyncTaxCodeModel { Uuid = tax.Uuid, Code = tax.Code, Percent = tax.Percent },
            PaymentMethods = PosTotalsCalculator.Methods
                .Where(m => (credit || m != PosPaymentMethod.OnAccount) && (creditNotes || m != PosPaymentMethod.StoreCredit)).ToList(),
            Features = new PosSyncFeaturesModel
            {
                Returns = await _gate.IsOnAsync(Org, PosFeatureCodes.Returns, ct), CreditSales = credit,
                Barcode = await _gate.IsOnAsync(Org, PosFeatureCodes.Barcode, ct), MultiStore = await _gate.IsOnAsync(Org, PosFeatureCodes.MultiStore, ct),
                CreditNotes = creditNotes, CrossStoreReturns = await _gate.IsOnAsync(Org, PosFeatureCodes.CrossStoreReturns, ct)
            },
            Currency = new PosSyncCurrencyModel { Code = await _read.CurrencyCodeAsync(ct), Decimals = 2 }
        };
    }

    // ── catalog ──

    public async Task<PosCatalogPageModel> CatalogAsync(Guid terminalUuid, DateTime? since, int limit, CancellationToken ct = default)
    {
        var terminal = await TerminalAsync(terminalUuid, ct);
        var catalog = _catalog ?? throw new BadRequestException("The product catalog is not available on this server.");
        limit = Limit(limit);
        var serverTime = _read.UtcNow;

        var changed = await catalog.GetChangedAsync(Org, since, limit, ct);
        var settingsQuery = _db.ProductSettings.AsNoTracking().Where(s => since == null || s.ModifiedAt > since);
        var settingsPage = await settingsQuery.OrderBy(s => s.ModifiedAt).ThenBy(s => s.Id).Take(limit).ToListAsync(ct);
        var cut = Window(changed.Count >= limit ? changed.Max(v => v.ModifiedAt) : null,
                         settingsPage.Count >= limit ? settingsPage[^1].ModifiedAt : null);
        var variants = cut is null ? changed.ToList() : changed.Where(v => v.ModifiedAt <= cut).ToList();
        var settingsChanged = cut is null ? settingsPage : await settingsQuery.Where(s => s.ModifiedAt <= cut).ToListAsync(ct);

        // Products whose POS settings changed: all their variants.
        var extraProducts = settingsChanged.Select(s => s.ProductUuid).Except(variants.Select(v => v.ProductUuid)).ToList();
        if (extraProducts.Count > 0)
        {
            var known = variants.Select(v => v.VariantUuid).ToHashSet();
            variants.AddRange((await catalog.GetByProductUuidsAsync(Org, extraProducts, ct)).Where(v => known.Add(v.VariantUuid)));
        }
        var productIds = variants.Select(v => v.ProductUuid).Distinct().ToList();
        var settings = await _db.ProductSettings.AsNoTracking().Where(s => productIds.Contains(s.ProductUuid)).ToDictionaryAsync(s => s.ProductUuid, ct);
        var categories = (await catalog.GetCategoriesAsync(Org, ct)).ToList();
        var categoryNames = categories.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First().Name);

        var items = variants.Select(v =>
        {
            var s = settings.GetValueOrDefault(v.ProductUuid);
            var categoryId = s?.PosCategoryId ?? v.CategoryId;
            return new PosCatalogItemModel
            {
                VariantUuid = v.VariantUuid, ProductUuid = v.ProductUuid, Sku = v.Sku, Name = v.ProductName, PosName = s?.PosName,
                VariantName = v.VariantName, Barcode = v.Barcode, CategoryId = categoryId,
                CategoryName = categoryId is { } c ? categoryNames.GetValueOrDefault(c) ?? (c == v.CategoryId ? v.CategoryName : null) : null,
                UomCode = v.UomCode, Price = v.Price, ImageUrl = s?.ImageUrl ?? v.ImageUrl, Color = s?.Color,
                IsWeighable = s?.IsWeighable ?? false, IsFavorite = s?.IsFavorite ?? false, SortOrder = s?.SortOrder ?? 0,
                IsActive = PosOrderService.IsUsable(v, s), IsStockable = v.IsStockable, ModifiedAt = Later(v.ModifiedAt, s?.ModifiedAt)
            };
        }).OrderBy(i => i.ModifiedAt).ThenBy(i => i.Sku).ToList();

        terminal.LastSyncAt = serverTime;
        try { await _db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { _db.ChangeTracker.Clear(); } // a back-office edit won; the next pull stamps again

        return new PosCatalogPageModel
        {
            ServerTime = serverTime, HasMore = cut is not null, Items = items,
            NextSince = cut ?? (items.Count == 0 ? since : Later(items.Max(i => i.ModifiedAt), since)),
            Categories = categories.Select((c, i) => new PosCategoryModel { Id = c.Id, Name = c.Name, SortOrder = i }).ToList()
        };
    }

    // ── stock ──

    public async Task<PosStockSnapshotModel> StockAsync(Guid terminalUuid, CancellationToken ct = default)
    {
        var terminal = await TerminalAsync(terminalUuid, ct);
        var stock = _stock ?? throw new BadRequestException("Stock levels are not available on this server.");
        var levels = await stock.GetLevelsAsync(Org, terminal.Store.WarehouseUuid, null, ct);
        return new PosStockSnapshotModel
        {
            ServerTime = _read.UtcNow,
            Items = levels.Select(l => new PosStockItemModel { VariantUuid = l.VariantUuid, Available = l.Available }).ToList()
        };
    }

    // ── customers ──

    public async Task<PosCustomersPageModel> CustomersAsync(Guid terminalUuid, DateTime? since, int limit, CancellationToken ct = default)
    {
        await TerminalAsync(terminalUuid, ct);
        var source = _customers ?? throw new BadRequestException("Customers are not available on this server.");
        limit = Limit(limit);
        var serverTime = _read.UtcNow;

        var changed = await source.GetChangedAsync(Org, since, limit, ct);
        var settingsQuery = _db.CustomerSettings.AsNoTracking().Where(s => since == null || s.ModifiedAt > since);
        var settingsPage = await settingsQuery.OrderBy(s => s.ModifiedAt).ThenBy(s => s.Id).Take(limit).ToListAsync(ct);
        var cut = Window(changed.Count >= limit ? changed.Max(c => c.ModifiedAt) : null,
                         settingsPage.Count >= limit ? settingsPage[^1].ModifiedAt : null);
        var customers = cut is null ? changed.ToList() : changed.Where(c => c.ModifiedAt <= cut).ToList();
        var settingsChanged = cut is null ? settingsPage : await settingsQuery.Where(s => s.ModifiedAt <= cut).ToListAsync(ct);

        var extra = settingsChanged.Select(s => s.CustomerUuid).Except(customers.Select(c => c.Uuid)).ToList();
        if (extra.Count > 0) customers.AddRange(await source.GetManyAsync(Org, extra, ct));
        var ids = customers.Select(c => c.Uuid).Distinct().ToList();
        var settings = await _db.CustomerSettings.AsNoTracking().Where(s => ids.Contains(s.CustomerUuid)).ToDictionaryAsync(s => s.CustomerUuid, ct);
        var balances = _balances is null || ids.Count == 0 ? new Dictionary<Guid, CustomerBalanceInfo>() : await _balances.GetAsync(Org, ids, ct);

        var page = customers.GroupBy(c => c.Uuid).Select(g => g.First()).Select(c =>
        {
            var s = settings.GetValueOrDefault(c.Uuid);
            var walkIn = PosOrderService.IsWalkIn(c);
            return new PosSyncCustomerModel
            {
                Uuid = c.Uuid, Code = c.Code, Name = c.Name, CustomerType = c.CustomerType, Phone = c.Phone, Mobile = c.Mobile, Email = c.Email,
                CreditLimit = c.CreditLimit, Balance = balances.GetValueOrDefault(c.Uuid)?.Balance ?? 0m, IsActive = c.IsActive, IsSystem = c.IsSystem,
                AllowPosCredit = !walkIn && s?.AllowPosCredit == true, OfflineCreditLimitOverride = s?.OfflineCreditLimitOverride,
                MaxSingleCreditOrder = s?.MaxSingleCreditOrder, RequiresIdForCredit = s?.RequiresIdForCredit ?? false,
                ModifiedAt = Later(c.ModifiedAt, s?.ModifiedAt)
            };
        }).OrderBy(c => c.ModifiedAt).ThenBy(c => c.Code).ToList();

        return new PosCustomersPageModel
        {
            ServerTime = serverTime, HasMore = cut is not null, Customers = page,
            NextSince = cut ?? (page.Count == 0 ? since : Later(page.Max(c => c.ModifiedAt), since))
        };
    }

    // ── users ──

    public async Task<IReadOnlyList<PosSyncUserModel>> UsersAsync(Guid terminalUuid, string? deviceHeader, CancellationToken ct = default)
    {
        var terminal = await TerminalAsync(terminalUuid, ct);
        // D-4 — PIN hashes only to the device the terminal is bound to.
        if (terminal.DeviceUuid is not { } device || !Guid.TryParse(deviceHeader?.Trim(), out var sent) || sent != device)
            throw new ForbiddenException("This device is not bound to the terminal.");
        var pins = _pins ?? throw new BadRequestException("Cashier PINs are not available on this server.");

        var staff = await _db.StoreStaff.AsNoTracking().Where(s => s.StoreId == terminal.StoreId && s.IsActive).ToListAsync(ct);
        if (staff.Count == 0) return [];
        var ids = staff.Select(s => s.UserId).ToList();
        var users = (await pins.GetUsersAsync(Org, ids, ct)).Where(u => u.IsActive && u.HasPin).ToDictionary(u => u.UserId);
        var hashes = await pins.GetPinHashesAsync(Org, users.Keys.ToList(), ct);
        return hashes.Where(h => users.ContainsKey(h.UserId))
            .Select(h => new PosSyncUserModel
            {
                UserId = h.UserId, Name = users[h.UserId].Name, PosRole = staff.First(s => s.UserId == h.UserId).PosRole, PinHash = h.PinHash,
                Permissions = users[h.UserId].Permissions.ToList()
            })
            .Where(u => u.Permissions.Contains(Models.PosPermissions.OrderCreate, StringComparer.OrdinalIgnoreCase))
            .OrderBy(u => u.Name).ToList();
    }

    // ── helpers ──

    private static int Limit(int limit) => limit <= 0 ? DefaultLimit : Math.Min(limit, MaxLimit);

    /// <summary>
    /// Two delta sources (the master data and POS's settings table) paged together: when either filled its page, the
    /// page ends at the earlier of their last timestamps (<c>cut</c>) and holds everything of both up to and including it
    /// — ties included — so <c>nextSince = cut</c> with "strictly after" never skips an equal timestamp. Null = both
    /// sources were read to the end.
    /// </summary>
    internal static DateTime? Window(DateTime? lastOfFullA, DateTime? lastOfFullB) =>
        lastOfFullA is null ? lastOfFullB : lastOfFullB is null ? lastOfFullA : lastOfFullA < lastOfFullB ? lastOfFullA : lastOfFullB;

    private static DateTime Later(DateTime a, DateTime? b) => b is { } x && x > a ? x : a;
}
