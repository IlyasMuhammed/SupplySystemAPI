using Microsoft.EntityFrameworkCore;
using SMS.Modules.Inventory.Data;
using SMS.Shared.Common;

namespace SMS.Modules.Inventory.Services.LeadTimes;

/// <summary>One variant as lead-time resolution sees it.</summary>
internal sealed record VariantLeadTimeFacts(
    int            VariantId,
    Guid           VariantUuid,
    string         Sku,
    string         VariantName,
    int            ProductId,
    Guid           ProductUuid,
    string         ProductName,
    string         SupplyMethod,
    int?           DefaultProductionWarehouseId,
    Guid?          FulfillmentRouteUuid,
    LeadTimeInputs Inputs)
{
    public bool   IsManufactured => Inputs.ProductIsManufactured;
    public string DisplayName    => string.IsNullOrWhiteSpace(VariantName) ? ProductName : $"{ProductName} — {VariantName}";
}

/// <summary>
/// Loads what <see cref="LeadTimeResolver"/> needs, batched: one read of the variants (with their products), one of
/// their active supplier rates, at most one <see cref="ISupplierLeadTimeLookup"/> call and one name lookup — whatever
/// the number of variants (D-13: one supplier read per BOM level). Always for the organization given, with the EF tenant
/// filter set aside: a super admin or a Hangfire caller has none, and another organization's variant must read as absent.
/// </summary>
internal sealed class LeadTimeInputsLoader
{
    private readonly InventoryDbContext _db;
    private readonly ISupplierLeadTimeLookup? _supplierLeadTimes;
    private readonly ISupplierNameLookupService? _supplierNames;

    /// <param name="supplierLeadTimes">D-11 tier 4, implemented in Suppliers; optional (absent = the tier is skipped).</param>
    /// <param name="supplierNames">Names for the source detail ("ACME Ltd (preferred)"); optional.</param>
    public LeadTimeInputsLoader(
        InventoryDbContext db, ISupplierLeadTimeLookup? supplierLeadTimes = null, ISupplierNameLookupService? supplierNames = null)
    {
        _db                = db;
        _supplierLeadTimes = supplierLeadTimes;
        _supplierNames     = supplierNames;
    }

    /// <summary>The organization's row, else the system defaults (D-10).</summary>
    public async Task<LeadTimeDefaultValues> ReadDefaultsAsync(Guid organizationId, CancellationToken ct = default)
    {
        var row = await _db.LeadTimeDefaults.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(d => d.OrganizationId == organizationId, ct);
        return row is null ? LeadTimeDefaultValues.System : LeadTimeDefaultValues.From(row);
    }

    /// <summary>The organization's variants among <paramref name="variantUuids"/>; unknown and other organizations' are absent.</summary>
    public async Task<IReadOnlyDictionary<Guid, VariantLeadTimeFacts>> LoadAsync(
        Guid organizationId, IReadOnlyCollection<Guid> variantUuids, LeadTimeDefaultValues defaults, CancellationToken ct = default)
    {
        var wanted = variantUuids.Where(u => u != Guid.Empty).Distinct().ToList();
        if (wanted.Count == 0) return new Dictionary<Guid, VariantLeadTimeFacts>();

        var variants = await _db.ProductVariants.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.OrganizationId == organizationId && wanted.Contains(v.Uuid))
            .Select(v => new
            {
                v.Id, v.Uuid, v.Sku, v.VariantName, v.DefaultSupplierId, v.FulfillmentRouteUuid,
                Overrides = new VariantLeadTimeOverrides(
                    v.LeadTimeDays, v.ManufacturingLeadTimeDays, v.ManufacturingBufferDays, v.QualityInspectionDays,
                    v.InternalTransferDays, v.PickPackDays, v.ShippingLeadTimeDays, v.SalesBufferDays),
                v.ProductId, ProductUuid = v.Product.Uuid, ProductName = v.Product.Name, v.Product.SupplyMethod,
                ProductLeadTimeDays = v.Product.LeadTimeDays, v.Product.DefaultProductionWarehouseId
            })
            .ToListAsync(ct);
        if (variants.Count == 0) return new Dictionary<Guid, VariantLeadTimeFacts>();

        // D-11 tiers 2 and 3 only matter where the variant has no supplier override of its own.
        var needRates = variants.Where(v => v.Overrides.Supplier is null).Select(v => v.Id).ToList();
        var today = DateTime.UtcNow.Date;
        List<RateRow> rates = needRates.Count == 0
            ? []
            : await _db.VariantSuppliers.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.OrganizationId == organizationId && needRates.Contains(s.VariantId) && s.IsActive
                            && s.EffectiveFrom <= today && (s.EffectiveTo == null || s.EffectiveTo >= today))
                .Select(s => new RateRow(s.Id, s.VariantId, s.SupplierId, s.IsPreferred, s.LeadTimeDays, s.EffectiveFrom))
                .ToListAsync(ct);
        // Newest effective rate first: a supplier quoting again replaces its older rate.
        var ratesByVariant = rates
            .OrderByDescending(r => r.EffectiveFrom).ThenByDescending(r => r.Id)
            .GroupBy(r => r.VariantId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Per variant: the preferred rate with days, the default supplier's rate with days, and — when neither has
        // days — the supplier whose own record is tier 4 (the default supplier, else the preferred one).
        var picks = variants.ToDictionary(v => v.Uuid, v =>
        {
            if (v.Overrides.Supplier is not null) return new SupplierPick(null, null, null);
            var rows = ratesByVariant.GetValueOrDefault(v.Id) ?? [];
            var preferred = rows.FirstOrDefault(r => r.IsPreferred && r.LeadTimeDays is not null);
            var byDefault = v.DefaultSupplierId is { } ds
                ? rows.FirstOrDefault(r => r.SupplierId == ds && r.LeadTimeDays is not null)
                : null;
            var record = preferred is null && byDefault is null
                ? v.DefaultSupplierId ?? rows.FirstOrDefault(r => r.IsPreferred)?.SupplierId
                : null;
            return new SupplierPick(preferred, byDefault, record);
        });

        var recordSuppliers = picks.Values.Where(p => p.RecordSupplier is not null).Select(p => p.RecordSupplier!.Value).Distinct().ToList();
        IReadOnlyDictionary<Guid, int> recordDays = recordSuppliers.Count == 0 || _supplierLeadTimes is null
            ? new Dictionary<Guid, int>()
            : await _supplierLeadTimes.GetAsync(organizationId, recordSuppliers, ct);

        var named = picks.Values
            .SelectMany(p => new[] { p.Preferred?.SupplierId, p.Default?.SupplierId, p.RecordSupplier })
            .Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
        IReadOnlyDictionary<Guid, string> names = named.Count == 0 || _supplierNames is null
            ? new Dictionary<Guid, string>()
            : await _supplierNames.GetNamesAsync(named);

        string? NameOf(Guid? id) => id is { } k && names.TryGetValue(k, out var n) ? n : null;

        return variants.ToDictionary(v => v.Uuid, v =>
        {
            var (preferred, byDefault, record) = picks[v.Uuid];
            var supplier = new SupplierLeadFacts(
                preferred?.LeadTimeDays, NameOf(preferred?.SupplierId),
                byDefault?.LeadTimeDays, NameOf(byDefault?.SupplierId),
                record is { } r && recordDays.TryGetValue(r, out var days) ? days : null, NameOf(record));
            var manufactured = v.SupplyMethod == SMS.Shared.Common.SupplyMethod.Manufacture;

            return new VariantLeadTimeFacts(
                v.Id, v.Uuid, v.Sku, v.VariantName, v.ProductId, v.ProductUuid, v.ProductName, v.SupplyMethod,
                v.DefaultProductionWarehouseId, v.FulfillmentRouteUuid,
                new LeadTimeInputs(v.Overrides, defaults, supplier, v.ProductLeadTimeDays, manufactured));
        });
    }

    private sealed record RateRow(int Id, int VariantId, Guid SupplierId, bool IsPreferred, int? LeadTimeDays, DateTime EffectiveFrom);

    private sealed record SupplierPick(RateRow? Preferred, RateRow? Default, Guid? RecordSupplier);
}
