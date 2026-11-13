using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SMS.Modules.Demand.Data;
using SMS.Modules.Demand.Domain;
using SMS.Modules.Demand.Models;
using SMS.Modules.Demand.Services;
using SMS.Modules.Inventory.Data;
using SMS.Shared.Authorization;
using SMS.Shared.Common;
using SMS.Shared.Onboarding;

namespace SMS.Modules.Demand.Onboarding;

/// <summary>
/// A50 D-27 / FSD §7.1: two demo sale orders and one demo purchase order, as DRAFTs, created through
/// <see cref="ISaleOrderService.CreateAsync"/> and <see cref="IPurchaseOrderService.CreateAsync"/>. They use the demo
/// customers, vendors and products that the lower-order contributors made (from <c>createdSoFar</c>), so every rule
/// those create paths have still applies. Missing inputs skip a document with a note, never fail the load.
/// <para>A sale order needs a shipping address when it ships, and the demo customers have none. So demo orders are
/// SELF_PICKUP and are skipped (with a note) when the organization has customer pickup switched off.</para>
/// <para>Cleanup: a demo document that is still DRAFT is removed outright, with its lines. The supplier and variant
/// reference checkers also count deleted POs, so a soft delete would pin the demo vendor and products for good. A demo
/// document that someone confirmed, approved or sent is real work now and is kept.</para>
/// </summary>
internal sealed class DemandSampleDataContributor : ISampleDataContributor
{
    internal const string SaleOrderType     = "SaleOrder";
    internal const string PurchaseOrderType = "PurchaseOrder";
    internal const string CustomerType      = "Customer";
    internal const string VendorType        = "Vendor";
    internal const string VariantType       = "ProductVariant";
    internal const string NoteType          = "Note";
    internal const string DemoNote          = "Sample data: remove it from Settings › Organization › Sample Data.";
    internal const string Draft             = "DRAFT";

    /// <summary>Which demo products (by position among the default variants) and how many of each, per order (FSD §7.1).</summary>
    internal static readonly IReadOnlyList<(int Customer, (int Product, decimal Qty)[] Lines)> SaleOrders =
    [
        (0, [(0, 2m), (1, 4m), (2, 6m)]),
        (1, [(4, 1m), (5, 3m)]),
    ];

    internal static readonly (int Vendor, (int Product, decimal Qty)[] Lines) PurchaseOrder = (0, [(0, 10m), (1, 20m), (3, 15m)]);

    private readonly DemandDbContext _db;
    private readonly InventoryDbContext _inventory;
    private readonly ISaleOrderService _saleOrders;
    private readonly IPurchaseOrderService _purchaseOrders;
    private readonly ISupplierNameLookupService? _supplierNames;
    private readonly IHttpContextAccessor? _http;
    private readonly ILogger _log;

    public DemandSampleDataContributor(DemandDbContext db, InventoryDbContext inventory, ISaleOrderService saleOrders,
        IPurchaseOrderService purchaseOrders, ISupplierNameLookupService? supplierNames = null, IHttpContextAccessor? http = null,
        ILogger<DemandSampleDataContributor>? log = null)
    {
        _supplierNames = supplierNames;
        _db = db;
        _inventory = inventory;
        _saleOrders = saleOrders;
        _purchaseOrders = purchaseOrders;
        _http = http;
        _log = log ?? (ILogger)NullLogger.Instance;
    }

    public int Order => 30;
    public string? RequiresModule => ModuleCodes.Demand;

    private Guid OrgId => _db.TenantContext.OrganizationId;
    private int UserId => _http?.HttpContext?.User is { } u && u.GetUserId() is var id && id > 0 ? id : 0;

    public Task<SampleDataPlan> PreviewAsync(CancellationToken ct = default) =>
        Task.FromResult(new SampleDataPlan { Counts = { [SaleOrderType] = SaleOrders.Count, [PurchaseOrderType] = 1 } });

    internal sealed record DemoVariant(Guid Uuid, string Name, decimal PurchasePrice);

    /// <summary>The demo default variants in creation order: one per demo product, the product's own name.</summary>
    private async Task<List<DemoVariant>> DemoVariantsAsync(IReadOnlyList<DemoRecordRef> createdSoFar, CancellationToken ct)
    {
        var order = createdSoFar.Where(r => r.EntityType == VariantType).Select(r => Guid.TryParse(r.EntityRef, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty).ToList();
        if (order.Count == 0) return [];
        var org = OrgId;
        var rows = await _inventory.ProductVariants.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.OrganizationId == org && order.Contains(v.Uuid) && v.IsDefault && v.IsActive)
            .Select(v => new { v.Uuid, v.Product.Name, v.PurchasePrice }).ToListAsync(ct);
        return order.Select(u => rows.FirstOrDefault(r => r.Uuid == u)).Where(r => r is not null)
            .Select(r => new DemoVariant(r!.Uuid, r.Name, r.PurchasePrice)).ToList();
    }

    private static List<Guid> Ids(IReadOnlyList<DemoRecordRef> refs, string type) =>
        refs.Where(r => r.EntityType == type).Select(r => Guid.TryParse(r.EntityRef, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty).ToList();

    public async Task<IReadOnlyList<DemoRecordRef>> CreateAsync(IReadOnlyList<DemoRecordRef> createdSoFar, CancellationToken ct = default)
    {
        var refs = new List<DemoRecordRef>();
        var customers = Ids(createdSoFar, CustomerType);
        var vendors = Ids(createdSoFar, VendorType);
        var variants = await DemoVariantsAsync(createdSoFar, ct);

        if (variants.Count == 0)
            return [new(NoteType, "Demo orders are skipped: there are no demo products (the Inventory module is off, or its sample products failed).")];

        refs.AddRange(await CreateSaleOrdersAsync(customers, variants));
        refs.AddRange(await CreatePurchaseOrderAsync(vendors, variants));
        return refs;
    }

    private async Task<List<DemoRecordRef>> CreateSaleOrdersAsync(List<Guid> customers, List<DemoVariant> variants)
    {
        if (customers.Count == 0) return [new(NoteType, "Demo sale orders are skipped: there are no demo customers.")];

        SaleOrderDefaultsModel defaults;
        try { defaults = await _saleOrders.GetDefaultsAsync(); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [new(NoteType, $"Demo sale orders are skipped: {ex.Message}")];
        }
        if (!defaults.SelfPickupEnabled)
            return [new(NoteType, "Demo sale orders are skipped: customer pickup is switched off, and a shipped order needs a customer address the demo customers do not have.")];

        var refs = new List<DemoRecordRef>();
        for (var i = 0; i < SaleOrders.Count; i++)
        {
            var (customerIndex, lines) = SaleOrders[i];
            var customer = customers[customerIndex % customers.Count];
            try
            {
                var uuid = await _saleOrders.CreateAsync(new CreateSaleOrderRequest
                {
                    PartnerId = customer, DeliveryMode = EnumCode<DeliveryMode>.Of(DeliveryMode.SelfPickup), Notes = DemoNote,
                    ExpectedDeliveryDate = DateTime.UtcNow.Date.AddDays(7 + 7 * i),
                    Lines = lines.Select(l => new CreateSaleOrderLineRequest { VariantUuid = variants[l.Product % variants.Count].Uuid, Quantity = l.Qty })
                        .GroupBy(l => l.VariantUuid).Select(g => new CreateSaleOrderLineRequest { VariantUuid = g.Key, Quantity = g.Sum(x => x.Quantity) }).ToList()
                }, UserId);
                refs.Add(new(SaleOrderType, uuid.ToString()));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Sample data: demo sale order {Index} was not created.", i + 1);
                refs.Add(new(NoteType, $"Demo sale order {i + 1} was not created: {ex.Message}"));
            }
        }
        return refs;
    }

    private async Task<List<DemoRecordRef>> CreatePurchaseOrderAsync(List<Guid> vendors, List<DemoVariant> variants)
    {
        if (vendors.Count == 0) return [new(NoteType, "The demo purchase order is skipped: there are no demo vendors.")];
        var vendor = vendors[PurchaseOrder.Vendor % vendors.Count];
        try
        {
            var vendorName = await VendorNameAsync(vendor);
            var lines = PurchaseOrder.Lines.Select(l => variants[l.Product % variants.Count]).Zip(PurchaseOrder.Lines, (v, l) => (v, l.Qty))
                .GroupBy(x => x.v.Uuid).Select(g => (g.First().v, Qty: g.Sum(x => x.Qty)))
                .Select(x => new CreatePoLineRequest
                {
                    VariantUuid = x.v.Uuid, ItemDescription = x.v.Name, Quantity = x.Qty,
                    // An explicit price: 0 would ask for a pricelist price, and the demo vendor has no pricelist.
                    UnitPrice = x.v.PurchasePrice > 0 ? x.v.PurchasePrice : 1m, RequiredDate = DateTime.UtcNow.Date.AddDays(10)
                }).ToList();
            var uuid = await _purchaseOrders.CreateAsync(new CreatePoRequest
            {
                SupplierId = vendor, SupplierName = vendorName, Title = "Demo purchase order", Notes = DemoNote,
                DeliveryDate = DateTime.UtcNow.Date.AddDays(10), Lines = lines
            }, UserId);
            return [new(PurchaseOrderType, uuid.ToString())];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Sample data: the demo purchase order was not created.");
            return [new(NoteType, $"The demo purchase order was not created: {ex.Message}")];
        }
    }

    /// <summary>The PO keeps the supplier's name as typed; Demand has no partner table, so it asks the vendors' own module.</summary>
    private async Task<string> VendorNameAsync(Guid vendor)
    {
        var name = _supplierNames is null ? null : (await _supplierNames.GetNamesAsync([vendor])).GetValueOrDefault(vendor);
        return string.IsNullOrWhiteSpace(name) ? "Demo vendor" : name;
    }

    public async Task<IReadOnlyList<DemoRecordRef>> DeleteAsync(IReadOnlyList<DemoRecordRef> refs, CancellationToken ct = default)
    {
        var kept = new List<DemoRecordRef>();
        var org = OrgId;
        foreach (var r in refs)
        {
            if (!Guid.TryParse(r.EntityRef, out var uuid)) continue;
            try
            {
                if (r.EntityType == SaleOrderType)
                {
                    var so = await _db.SaleOrders.IgnoreQueryFilters().Include(o => o.Lines)
                        .FirstOrDefaultAsync(o => o.UUID == uuid && o.OrganizationId == org, ct);
                    if (so is null) continue;
                    if (so.Status != Draft)
                    {
                        kept.Add(r);
                        kept.Add(new(NoteType, $"Sale order {so.SoNumber} is kept: it is {so.Status.ToLowerInvariant()}, not a draft any more."));
                        continue;
                    }
                    _db.SaleOrderIntimations.RemoveRange(await _db.SaleOrderIntimations.IgnoreQueryFilters().Where(x => x.SaleOrderId == so.Id).ToListAsync(ct));
                    _db.SaleOrderLines.RemoveRange(so.Lines);
                    _db.SaleOrders.Remove(so);
                    await _db.SaveChangesAsync(ct);
                }
                else if (r.EntityType == PurchaseOrderType)
                {
                    var po = await _db.PurchaseOrders.IgnoreQueryFilters().FirstOrDefaultAsync(p => p.UUID == uuid && p.OrganizationId == org, ct);
                    if (po is null) continue;
                    if (po.Status != Draft)
                    {
                        kept.Add(r);
                        kept.Add(new(NoteType, $"Purchase order {po.PoNumber} is kept: it is {po.Status.ToLowerInvariant()}, not a draft any more."));
                        continue;
                    }
                    _db.PurchaseOrderLines.RemoveRange(await _db.PurchaseOrderLines.IgnoreQueryFilters().Where(l => l.PurchaseOrderId == po.Id).ToListAsync(ct));
                    _db.PurchaseOrderPrLinks.RemoveRange(await _db.PurchaseOrderPrLinks.IgnoreQueryFilters().Where(l => l.PurchaseOrderId == po.Id).ToListAsync(ct));
                    _db.PurchaseOrders.Remove(po);
                    await _db.SaveChangesAsync(ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Sample data cleanup: {Type} {Uuid} could not be removed.", r.EntityType, uuid);
                _db.ChangeTracker.Clear();
                kept.Add(r);
                kept.Add(new(NoteType, $"A demo {(r.EntityType == SaleOrderType ? "sale" : "purchase")} order is kept: {ex.Message}"));
            }
        }
        return kept;
    }
}

/// <summary>A50 D-28: CREATE_SO (a sale order) and CREATE_PO (a purchase order), not deleted and not demo records.</summary>
internal sealed class DemandOnboardingSignal : IOnboardingSignal
{
    private readonly DemandDbContext _db;
    public DemandOnboardingSignal(DemandDbContext db) => _db = db;

    public IReadOnlyCollection<string> StepCodes { get; } = [OnboardingStepCodes.CreateSo, OnboardingStepCodes.CreatePo];

    public async Task<IReadOnlySet<string>> CompletedAsync(IReadOnlySet<Guid> demoEntityIds, CancellationToken ct = default)
    {
        var org = _db.TenantContext.OrganizationId;
        var demo = demoEntityIds.ToList();
        var done = new HashSet<string>();
        if (await _db.SaleOrders.IgnoreQueryFilters().AsNoTracking().AnyAsync(o => o.OrganizationId == org && !o.IsDeleted && !demo.Contains(o.UUID), ct))
            done.Add(OnboardingStepCodes.CreateSo);
        if (await _db.PurchaseOrders.IgnoreQueryFilters().AsNoTracking().AnyAsync(p => p.OrganizationId == org && !p.IsDelete && !demo.Contains(p.UUID), ct))
            done.Add(OnboardingStepCodes.CreatePo);
        return done;
    }
}
