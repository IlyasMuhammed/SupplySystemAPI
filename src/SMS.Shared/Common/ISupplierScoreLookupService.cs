namespace SMS.Shared.Common;

// RC-003 (FSD Addendum 28) — shared interface so SMS.Modules.Inventory's Rate Card comparison grid
// can show each supplier's latest Scorecard grade (FSD Addendum 23) without a project reference to
// SMS.Modules.Suppliers. The implementation lives in SMS.Modules.Suppliers and is resolved via the
// DI container — same isolation mechanism as ISupplierContactLookupService, batch shape mirrors
// IPurchaseOrderPriceLookupService.
public interface ISupplierScoreLookupService
{
    /// <summary>
    /// For each supplier, the Grade (A–F) of their most recent SupplierScoreSnapshot. A supplier
    /// that has never been scored is simply absent from the result (not present as null) — the
    /// caller renders '-' for any id with no entry, never an error.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> GetLatestGradesAsync(IReadOnlyList<Guid> supplierIds);
}
