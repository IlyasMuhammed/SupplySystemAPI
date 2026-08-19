namespace SMS.Shared.Common;

/// <summary>
/// PV-007 — implemented once per module that owns a document referencing a variant (Demand for
/// PO lines, Warehouse for GRN lines, Material for MIR lines), consumed by
/// SMS.Modules.Inventory's variant-delete flow without a direct project reference — mirrors the
/// IMasterProductLedgerService pattern: the interface lives in Shared so a module can depend on
/// it purely through DI, injecting IEnumerable&lt;IVariantReferenceChecker&gt;.
/// </summary>
public interface IVariantReferenceChecker
{
    /// <summary>True if this module has ever referenced the variant (any PO/GRN/MIR line, etc.),
    /// meaning it must be soft-deleted rather than hard-deleted.</summary>
    Task<bool> IsVariantReferencedAsync(Guid variantUuid);
}
