using SMS.Modules.Demand.Models;

namespace SMS.Modules.Demand.Services;

/// <summary>
/// A32 C5 — rejection reasons (owner: FND). Reads are for the inquiry/quotation screens' dropdowns; writes are
/// the admin screen. Every write filters on the caller's own organization.
/// </summary>
public interface IRejectionReasonService
{
    /// <summary>The organization's reasons by DisplayOrder then Code; active only unless asked (BR-C5-03).</summary>
    Task<IReadOnlyList<RejectionReasonModel>> GetListAsync(bool includeInactive);
    Task<RejectionReasonModel?> GetByIdAsync(Guid uuid);
    /// <summary>Code unique per organization (BR-C5-01) — a duplicate is a 409.</summary>
    Task<RejectionReasonModel> CreateAsync(CreateRejectionReasonRequest req, int userId);
    Task<RejectionReasonModel?> UpdateAsync(Guid uuid, UpdateRejectionReasonRequest req, int userId);
    Task<RejectionReasonModel?> SetActiveAsync(Guid uuid, bool isActive, int userId);
    /// <summary>Custom, unused reasons only: a seeded code or one any line references is a 409 (deactivate instead).</summary>
    Task<bool> DeleteAsync(Guid uuid, int userId);

    /// <summary>
    /// Inserts whichever of the ten §7.3 codes this organization lacks (idempotent; never touches existing rows).
    /// Called on organization provisioning and by the startup backfill for every existing organization.
    /// Returns how many were added.
    /// </summary>
    Task<int> EnsureSeededAsync(Guid organizationId);

    /// <summary>The startup backfill: <see cref="EnsureSeededAsync"/> for each organization, in one read. Returns how many were added.</summary>
    Task<int> EnsureSeededForAllAsync(IReadOnlyList<Guid> organizationIds);
}
