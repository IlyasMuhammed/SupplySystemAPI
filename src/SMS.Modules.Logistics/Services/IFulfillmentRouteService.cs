using SMS.Modules.Logistics.Models;

namespace SMS.Modules.Logistics.Services;

/// <summary>
/// A33 C1 — the organization's fulfillment routes (BR-C1-01..09). Every read and write filters explicitly on the
/// caller's own organization (the EF tenant filter is off for super admins): another organization's route is a 404.
/// Business-rule failures throw <c>BadRequestException</c> (400); state conflicts, duplicates and "in use" throw
/// <c>ConflictException</c> (409). Null / false = not found in the caller's organization (404).
/// </summary>
public interface IFulfillmentRouteService
{
    /// <summary>By display order, then code. Active only unless <paramref name="includeInactive"/>.</summary>
    Task<IReadOnlyList<FulfillmentRouteModel>> GetListAsync(bool includeInactive);

    Task<FulfillmentRouteModel?> GetByUuidAsync(Guid uuid);

    Task<FulfillmentRouteModel> CreateAsync(CreateFulfillmentRouteRequest req, int userId);

    Task<FulfillmentRouteModel?> UpdateAsync(Guid uuid, UpdateFulfillmentRouteRequest req, int userId);

    /// <summary>
    /// Deactivating is refused (409) while the route is a default or in use (BR-C1-07: active variants, open sale
    /// order lines — asked through every registered <c>IFulfillmentRouteUsage</c>). Activating always succeeds.
    /// </summary>
    Task<FulfillmentRouteModel?> SetActiveAsync(Guid uuid, bool isActive, int userId);

    /// <summary>
    /// Makes the route the default of its class (D-4), clearing the previous one in the same transaction (T-C1-07).
    /// Serialized per organization. An inactive route cannot become a default (400).
    /// </summary>
    Task<FulfillmentRouteModel?> SetDefaultAsync(Guid uuid, int userId);

    /// <summary>Leaves the route's class with no default, so lines with no other route block confirmation (D-6).</summary>
    Task<FulfillmentRouteModel?> ClearDefaultAsync(Guid uuid, int userId);

    /// <summary>
    /// Hard-deletes a custom route nothing refers to. A system route (BR-C1-06), a default, one in use, or one a
    /// delivery already carries is a 409 — deactivate it instead.
    /// </summary>
    Task<bool> DeleteAsync(Guid uuid, int userId);
}

/// <summary>
/// A33 PA-03 — seeds PICK_ONLY, PICK_AND_SHIP and PICK_PACK_SHIP for an organization (D-6: PICK_AND_SHIP is the
/// default for SHIP orders, PICK_ONLY the default for SELF_PICKUP orders). Matched by code; a seed route that exists
/// — renamed, edited or deactivated — is never touched. Stamps the organization it is given (R-13).
/// </summary>
public interface IFulfillmentRouteSeeder
{
    /// <summary>Returns how many routes were added.</summary>
    Task<int> EnsureSeededAsync(Guid organizationId, CancellationToken ct = default);

    /// <summary>The startup backfill: every organization, in one read. Returns how many routes were added.</summary>
    Task<int> EnsureSeededForAllAsync(IReadOnlyList<Guid> organizationIds, CancellationToken ct = default);
}
