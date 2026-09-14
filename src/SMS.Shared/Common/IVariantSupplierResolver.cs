namespace SMS.Shared.Common;

// RC-001 (FSD Addendum 28) — shared interface so other modules (e.g. Demand for PO creation,
// Material for MIR costing) can resolve "what's this supplier's active rate for this variant
// today" without a project reference to SMS.Modules.Inventory. The implementation lives in
// SMS.Modules.Inventory and is resolved via the DI container — same pattern as
// ISupplierContactLookupService.
public interface IVariantSupplierResolver
{
    Task<ActiveRateInfo?> GetActiveRateAsync(Guid variantUuid, Guid supplierId, DateOnly asOfDate);
}

public sealed record ActiveRateInfo(
    Guid VariantSupplierUuid,
    decimal VendorUnitCost,
    Guid CurrencyId,
    int? LeadTimeDays,
    decimal? MinOrderValue,
    string? DiscountTiers,
    DateTime EffectiveFrom,
    DateTime? EffectiveTo);
