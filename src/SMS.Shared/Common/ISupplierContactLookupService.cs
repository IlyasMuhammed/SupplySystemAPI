namespace SMS.Shared.Common;

/// <summary>
/// Shared interface for reading a supplier's contact details (phone/address) from another
/// module. Declared in SMS.Shared so SMS.Modules.Demand can read supplier contact info for the
/// PO document letterhead without referencing SMS.Modules.Suppliers directly — that reference
/// would be circular, since SMS.Modules.Suppliers already references SMS.Modules.Demand.
/// The implementation lives in SMS.Modules.Suppliers and is resolved via the DI container.
/// </summary>
public interface ISupplierContactLookupService
{
    Task<SupplierContactInfo?> GetContactInfoAsync(Guid supplierId);
}

public sealed record SupplierContactInfo(string? Phone, string? Email, string? Address);
