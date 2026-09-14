namespace SMS.Shared.Common;

// RC-003 (FSD Addendum 28) — shared interface so SMS.Modules.Inventory's Rate Card comparison grid
// can show supplier names without a project reference to SMS.Modules.Suppliers. The implementation
// lives in SMS.Modules.Suppliers and is resolved via the DI container.
public interface ISupplierNameLookupService
{
    Task<IReadOnlyDictionary<Guid, string>> GetNamesAsync(IReadOnlyList<Guid> supplierIds);
}
