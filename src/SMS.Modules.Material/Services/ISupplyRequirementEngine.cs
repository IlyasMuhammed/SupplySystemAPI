using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Models;

namespace SMS.Modules.Material.Services;

/// <summary>
/// What happens to a shortage a production order cannot cover from stock or supply already on its
/// way (A30 §13, BR-S01–S07): raise the paperwork that will bring the rest in.
/// </summary>
internal interface ISupplyRequirementEngine
{
    /// <summary>
    /// Raises or refreshes the one live supply requirement for a requirement's shortage, then acts
    /// on it — a purchase order, a child production order, or a note for a transfer nothing here
    /// automates. Idempotent per requirement: an already-ordered requirement's supply requirement is
    /// left alone, and a shortage that has closed cancels an unordered one. Returns null when there
    /// is, and remains, nothing to raise.
    /// </summary>
    Task<SupplyRequirement?> EnsureForShortageAsync(
        ProductionMaterialRequirement pmr, ProductionOrder productionOrder, int userId, int depth, CancellationToken ct = default);

    /// <summary>A person raising a supply requirement directly, with nothing behind it but their own say-so.</summary>
    Task<Guid> CreateManualAsync(CreateSupplyRequirementRequest req, int userId, CancellationToken ct = default);

    Task CancelAsync(Guid uuid, string reason, int userId, CancellationToken ct = default);
}
