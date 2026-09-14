namespace SMS.Shared.Common;

/// <summary>
/// Implemented once per module that owns a document referencing a supplier (Demand for POs/RFQ
/// responses, Finance for invoices/payments, Warehouse for GRNs/return orders), consumed by
/// SMS.Modules.Suppliers's supplier-delete flow purely through DI (IEnumerable&lt;ISupplierReferenceChecker&gt;)
/// — mirrors the IVariantReferenceChecker pattern (PV-007), same reasoning: avoids a direct
/// project reference in either direction.
/// </summary>
public interface ISupplierReferenceChecker
{
    /// <summary>True if this module has ever recorded a document against this supplier — deleting
    /// it would orphan real transaction history, so the delete must be refused.</summary>
    Task<bool> IsSupplierReferencedAsync(Guid supplierId);
}
