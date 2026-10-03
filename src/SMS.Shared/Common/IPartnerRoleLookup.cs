namespace SMS.Shared.Common;

/// <summary>What one business partner is, for a module that must check it without referencing Suppliers.</summary>
public sealed record PartnerRoleInfo(Guid Uuid, string Name, bool IsCustomer, bool IsVendor, bool IsActive);

/// <summary>
/// A32 — BR-C1-01/BR-C2-01: a sale inquiry or quotation may only be raised for a partner flagged as a customer.
/// Implemented in SMS.Modules.Suppliers (which owns BusinessPartners) and resolved through DI, like
/// <see cref="ISupplierNameLookupService"/>. Scoped to the caller's own organization: another organization's
/// partner is absent (null), super admin included.
/// </summary>
public interface IPartnerRoleLookup
{
    /// <summary>Null when no partner with this UUID exists in the caller's organization.</summary>
    Task<PartnerRoleInfo?> GetAsync(Guid partnerUuid, CancellationToken ct = default);
}
