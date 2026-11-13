using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Inventory.Data;
using SMS.Modules.Inventory.Import;
using SMS.Modules.Inventory.Models;
using SMS.Modules.Inventory.Services;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Inventory.Onboarding;

/// <summary>
/// A50 D-27 / D-70 / D-71: the 20 sample products (FSD §7.1; Office Table and Office Chair with 3 variants each, so 24 variants),
/// created through <see cref="IInventoryService.CreateProductAsync"/>. They have purchase and selling prices and are available
/// for retail, POS and MIR. Opening stock is added only when the OPENING_STOCK import target's own rules accept it: the
/// opening balance date set, FEATURE_OPENING_BALANCE_IMPORT on, and an active warehouse. Each product's default variant gets
/// 10–200 units into the first active warehouse; otherwise it is skipped with a note.
/// <para>Cleanup: a product whose variants real data uses (PO / GRN / MIR lines via <see cref="IVariantReferenceChecker"/>,
/// reservations, adjustments, ledger movements other than its demo opening) is kept. Any other product has its demo opening
/// stock written off through the stock ledger and the product ledger (both append-only, so this is an OUT movement, not a
/// delete) and is then soft-deleted (the only product delete Inventory has).</para>
/// </summary>
internal sealed class InventorySampleDataContributor : ISampleDataContributor
{
    internal const string ProductType = "Product";
    internal const string VariantType = "ProductVariant";
    internal const string OpeningType = "OpeningStock";
    internal const string NoteType    = "Note";
    internal const string DemoSuffix  = " (Demo)";
    internal const string CleanupReference = "DEMO_CLEANUP";

    internal sealed record DemoProduct(string Name, string Brand, decimal Cost, decimal Price, string[]? Variants = null);

    internal static readonly IReadOnlyList<DemoProduct> Products =
    [
        new("Office Table",          "Hartwell", 180m, 299m, ["120 cm", "150 cm", "180 cm"]),
        new("Office Chair",          "Hartwell",  75m, 129m, ["Standard", "Ergonomic", "Executive"]),
        new("Desk Lamp",             "Lumina",    12m,  24m),
        new("Monitor Stand",         "QuickParts", 18m, 35m),
        new("Filing Cabinet",        "Hartwell",  95m, 165m),
        new("Bookshelf",             "Hartwell",  60m, 110m),
        new("Whiteboard",            "Lumina",    40m,  75m),
        new("Conference Table",      "Hartwell", 420m, 690m),
        new("Reception Desk",        "Hartwell", 380m, 620m),
        new("Keyboard Tray",         "QuickParts", 15m, 29m),
        new("Footrest",              "QuickParts", 10m, 22m),
        new("Cable Organizer",       "QuickParts",  4m,  9m),
        new("Desk Organizer",        "Lumina",     6m, 14m),
        new("Coat Stand",            "Hartwell",  22m,  45m),
        new("Paper Shredder",        "GlobalSupply", 55m, 99m),
        new("Office Sofa",           "Hartwell", 310m, 540m),
        new("Visitor Chair",         "Hartwell",  38m,  69m),
        new("Storage Locker",        "GlobalSupply", 140m, 235m),
        new("Printer Stand",         "QuickParts", 25m, 49m),
        new("Drawer Unit",           "Hartwell",  70m, 125m),
    ];

    /// <summary>Opening quantity of the i-th product: 10–200, fixed so a load is repeatable.</summary>
    internal static decimal OpeningQty(int i) => 10 + (i * 37 + 23) % 191;

    private readonly InventoryDbContext _db;
    private readonly IInventoryService _inventory;
    private readonly IInventoryLedgerService _ledger;
    private readonly IEnumerable<IVariantReferenceChecker> _variantCheckers;
    private readonly IEnumerable<IImportTarget> _importTargets;
    private readonly IOpeningBalanceDateProvider? _openingDate;
    private readonly IProductLedgerService? _productLedger;
    private readonly IModuleGate? _gate;
    private readonly IHttpContextAccessor? _http;
    private readonly ILogger _log;

    public InventorySampleDataContributor(InventoryDbContext db, IInventoryService inventory, IInventoryLedgerService ledger,
        IEnumerable<IVariantReferenceChecker> variantCheckers, IEnumerable<IImportTarget> importTargets,
        IOpeningBalanceDateProvider? openingDate = null, IProductLedgerService? productLedger = null, IModuleGate? gate = null,
        IHttpContextAccessor? http = null, ILogger<InventorySampleDataContributor>? log = null)
    {
        _db = db;
        _inventory = inventory;
        _ledger = ledger;
        _variantCheckers = variantCheckers;
        _importTargets = importTargets;
        _openingDate = openingDate;
        _productLedger = productLedger;
        _gate = gate;
        _http = http;
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    public int Order => 20;
    public string? RequiresModule => ModuleCodes.Inventory;

    private Guid OrgId => _db.TenantContext.OrganizationId;
    private int UserId => _http?.HttpContext?.User is { } u && u.GetUserId() is var id && id > 0 ? id : 0;

    public async Task<SampleDataPlan> PreviewAsync(CancellationToken ct = default)
    {
        var (warehouse, _) = await OpeningWarehouseAsync(ct);
        return new SampleDataPlan { Counts = { [ProductType] = Products.Count, [OpeningType] = warehouse is null ? 0 : Products.Count } };
    }

    /// <summary>The warehouse demo opening stock goes to, or null with the reason (D-71).</summary>
    private async Task<(string? WarehouseCode, string? Reason)> OpeningWarehouseAsync(CancellationToken ct)
    {
        if (_importTargets.OfType<OpeningStockImportTarget>().FirstOrDefault() is null)
            return (null, "Opening stock is skipped: the opening stock import is not available in this host.");
        if (!await _gate.IsOnAsync(OrgId, ModuleCodes.OpeningBalanceImport, ct))
            return (null, "Opening stock is skipped: opening balance import is not enabled for this organization.");
        if (_openingDate is null || await _openingDate.GetAsync(OrgId, ct) is null)
            return (null, "Opening stock is skipped: " + OpeningStockImportTarget.OpeningDateNotSetMessage);
        var code = await _db.Warehouses.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.OrganizationId == OrgId && w.IsActive).OrderBy(w => w.Code).Select(w => w.Code).FirstOrDefaultAsync(ct);
        return code is null ? (null, "Opening stock is skipped: there is no active warehouse. Create one first.") : (code, null);
    }

    public async Task<IReadOnlyList<DemoRecordRef>> CreateAsync(IReadOnlyList<DemoRecordRef> createdSoFar, CancellationToken ct = default)
    {
        var refs = new List<DemoRecordRef>();
        var openingRows = new List<(int Index, string Sku, decimal Cost, Guid VariantUuid)>();

        for (var i = 0; i < Products.Count; i++)
        {
            var p = Products[i];
            try
            {
                var variants = (p.Variants ?? [p.Name]).Select((name, n) => new CreateProductVariantRequest
                {
                    VariantName = name, IsDefault = n == 0, SortOrder = n,
                    PurchasePrice = p.Cost + n * Math.Round(p.Cost * 0.2m, 2), SellingPrice = p.Price + n * Math.Round(p.Price * 0.2m, 2),
                    IsAvailableForRetail = true, IsAvailableForPos = true, IsAvailableForMirMiv = true
                }).ToList();

                var (id, _) = await _inventory.CreateProductAsync(new CreateProductRequest
                {
                    Name = p.Name + DemoSuffix, Brand = p.Brand, Description = "Sample product (demo data).", Variants = variants
                }, UserId);

                var created = await _db.Products.IgnoreQueryFilters().AsNoTracking().Where(x => x.Id == id)
                    .Select(x => new { x.Uuid, Variants = x.Variants.Select(v => new { v.Uuid, v.Sku, v.IsDefault, v.PurchasePrice }).ToList() })
                    .FirstAsync(ct);
                refs.Add(new(ProductType, created.Uuid.ToString()));
                foreach (var v in created.Variants) refs.Add(new(VariantType, v.Uuid.ToString()));
                var def = created.Variants.First(v => v.IsDefault);
                openingRows.Add((i, def.Sku, def.PurchasePrice, def.Uuid));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Sample data: product {Name} was not created.", p.Name);
                refs.Add(new(NoteType, $"Product {p.Name}{DemoSuffix} was not created: {ex.Message}"));
            }
        }

        if (openingRows.Count > 0) refs.AddRange(await AddOpeningStockAsync(openingRows, ct));
        return refs;
    }

    private async Task<List<DemoRecordRef>> AddOpeningStockAsync(List<(int Index, string Sku, decimal Cost, Guid VariantUuid)> rows, CancellationToken ct)
    {
        var (warehouse, reason) = await OpeningWarehouseAsync(ct);
        if (warehouse is null) return [new(NoteType, reason!)];

        var target = _importTargets.OfType<OpeningStockImportTarget>().First();
        var values = rows.Select((r, n) => (Row: n + 1, Values: (IReadOnlyDictionary<string, string?>)new Dictionary<string, string?>
        {
            ["product_sku"] = r.Sku, ["warehouse_code"] = warehouse,
            ["quantity"] = OpeningQty(r.Index).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["unit_cost"] = r.Cost.ToString(System.Globalization.CultureInfo.InvariantCulture),
        })).ToList();

        try
        {
            // The target's own validation first (IMP-OB rules); any refusal skips opening stock as a whole, with its reason.
            var checks = await target.ValidateAsync(OrgId, values.Select(v => new ImportRowInput(v.Row, v.Values, null)).ToList(), ct);
            if (checks.SelectMany(c => c.Errors).FirstOrDefault() is { } error)
                return [new(NoteType, $"Opening stock is skipped: {error.Message}")];

            await target.ApplyAsync(OrgId, UserId, values.Select(v => new ImportApplyRow(v.Row, v.Values, "CREATE", null)).ToList(), ct);
            var warehouseId = await _db.Warehouses.IgnoreQueryFilters().Where(w => w.OrganizationId == OrgId && w.Code == warehouse)
                .Select(w => w.Id).FirstAsync(ct);
            return rows.Select(r => new DemoRecordRef(OpeningType, $"{r.VariantUuid}:{warehouseId}")).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Sample data: opening stock was not added.");
            return [new(NoteType, $"Opening stock is skipped: {ex.Message}")];
        }
    }

    public async Task<IReadOnlyList<DemoRecordRef>> DeleteAsync(IReadOnlyList<DemoRecordRef> refs, CancellationToken ct = default)
    {
        var kept = new List<DemoRecordRef>();
        var products = refs.Where(r => r.EntityType == ProductType).ToList();

        foreach (var r in products)
        {
            if (!Guid.TryParse(r.EntityRef, out var uuid)) continue;
            var product = await _db.Products.IgnoreQueryFilters().AsNoTracking()
                .Where(p => p.Uuid == uuid && p.OrganizationId == OrgId)
                .Select(p => new { p.Id, p.Name, p.IsActive, Variants = p.Variants.Select(v => new { v.Id, v.Uuid }).ToList() })
                .FirstOrDefaultAsync(ct);
            if (product is null) continue;

            var variantRefs = product.Variants.Select(v => new DemoRecordRef(VariantType, v.Uuid.ToString())).ToList();
            var openingRefs = refs.Where(o => o.EntityType == OpeningType && product.Variants.Any(v => o.EntityRef.StartsWith(v.Uuid + ":", StringComparison.OrdinalIgnoreCase))).ToList();

            if (await RealUseAsync(product.Variants.Select(v => (v.Id, v.Uuid)).ToList(), ct) is { } use)
            {
                kept.Add(r);
                kept.AddRange(variantRefs);
                kept.AddRange(openingRefs);
                kept.Add(new(NoteType, $"{product.Name} is kept: {use}."));
                continue;
            }

            try
            {
                await WriteOffDemoStockAsync(product.Variants.Select(v => (v.Id, v.Uuid)).ToList(), ct);
                if (product.IsActive) await _inventory.SoftDeleteProductAsync(product.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Sample data cleanup: product {Name} could not be removed.", product.Name);
                kept.Add(r);
                kept.AddRange(variantRefs);
                kept.AddRange(openingRefs);
                kept.Add(new(NoteType, $"{product.Name} is kept: {ex.Message}"));
            }
        }
        return kept;
    }

    /// <summary>What real data uses one of the variants for, or null. The demo opening (and its write-off) does not count.</summary>
    private async Task<string?> RealUseAsync(IReadOnlyList<(int Id, Guid Uuid)> variants, CancellationToken ct)
    {
        var ids = variants.Select(v => v.Id).ToList();
        var uuids = variants.Select(v => v.Uuid).ToList();
        foreach (var (_, uuid) in variants)
            foreach (var checker in _variantCheckers)
                if (await checker.IsVariantReferencedAsync(uuid)) return "a purchase order, goods receipt or material request uses it";

        var org = OrgId;
        if (await _db.StockReservations.IgnoreQueryFilters().AnyAsync(s => s.OrganizationId == org && uuids.Contains(s.VariantUuid), ct))
            return "stock of it is reserved";
        if (await _db.StockAdjustments.IgnoreQueryFilters().AnyAsync(a => a.OrganizationId == org && ids.Contains(a.VariantId), ct))
            return "a stock adjustment uses it";
        if (await _db.InventoryLedgerEntries.IgnoreQueryFilters().AnyAsync(e => e.OrganizationId == org && ids.Contains(e.VariantId)
                && e.TransactionType != OpeningStockImportTarget.TransactionType && e.ReferenceType != CleanupReference, ct))
            return "it has stock movements";
        return null;
    }

    /// <summary>D-71: the demo opening stock goes OUT again (stock ledger, master and product ledgers), one transaction.</summary>
    private async Task WriteOffDemoStockAsync(IReadOnlyList<(int Id, Guid Uuid)> variants, CancellationToken ct)
    {
        var ids = variants.Select(v => v.Id).ToList();
        var org = OrgId;
        if (!await _db.InventoryItems.IgnoreQueryFilters().AnyAsync(i => i.OrganizationId == org && ids.Contains(i.VariantId) && i.QtyOnHand > 0m, ct))
            return;

        await ImportUnitOfWork.RunAsync<int>(_db, async tx =>
        {
            var items = await _db.InventoryItems.IgnoreQueryFilters()
                .Where(i => i.OrganizationId == org && ids.Contains(i.VariantId) && i.QtyOnHand > 0m).ToListAsync(ct);
            foreach (var item in items)
            {
                var qty = item.QtyOnHand;
                var referenceId = Guid.NewGuid();
                var warehouseName = await _db.Warehouses.IgnoreQueryFilters().Where(w => w.Id == item.WarehouseId).Select(w => w.Name).FirstOrDefaultAsync(ct);
                await _ledger.CreateEntryAsync(new LedgerEntryCommand
                {
                    VariantId = item.VariantId, WarehouseId = item.WarehouseId, TransactionType = "ADJUSTMENT",
                    ReferenceType = CleanupReference, ReferenceId = referenceId, ReferenceNumber = CleanupReference,
                    QuantityOut = qty, UnitCost = item.UnitCost ?? 0m, CreatedBy = UserId,
                    Notes = "Sample data removed: its demo opening stock is written off.",
                    SourceType = "WAREHOUSE", SourceName = warehouseName, DestinationType = "ADJUSTMENT", DestinationName = "Sample data cleanup"
                }, tx);
                foreach (var entry in _db.ChangeTracker.Entries<Domain.InventoryLedgerEntry>())
                    if (entry.State == EntityState.Added && entry.Entity.OrganizationId == Guid.Empty) entry.Entity.OrganizationId = org;
                item.QtyOnHand = 0m;
                item.LastUpdated = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);

                var variantUuid = variants.First(v => v.Id == item.VariantId).Uuid;
                if (_productLedger is not null)
                    await _productLedger.AppendEntryAsync(new ProductLedgerPosting(
                        variantUuid, ProductLedgerEntryTypes.Adjustment, ProductLedgerDirections.Out, qty, null,
                        CleanupReference, referenceId, CleanupReference, UserId, Narration: "Sample data cleanup"),
                        tx?.GetDbTransaction());
            }
            return items.Count;
        }, ct);
    }
}

/// <summary>A50 D-28: ADD_PRODUCT, meaning an active product that is not a demo record.</summary>
internal sealed class InventoryOnboardingSignal : IOnboardingSignal
{
    private readonly InventoryDbContext _db;
    public InventoryOnboardingSignal(InventoryDbContext db) => _db = db;

    public IReadOnlyCollection<string> StepCodes { get; } = [OnboardingStepCodes.AddProduct];

    public async Task<IReadOnlySet<string>> CompletedAsync(IReadOnlySet<Guid> demoEntityIds, CancellationToken ct = default)
    {
        var org = _db.TenantContext.OrganizationId;
        var demo = demoEntityIds.ToList();
        var any = await _db.Products.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(p => p.OrganizationId == org && p.IsActive && !demo.Contains(p.Uuid), ct);
        return any ? new HashSet<string> { OnboardingStepCodes.AddProduct } : new HashSet<string>();
    }
}
