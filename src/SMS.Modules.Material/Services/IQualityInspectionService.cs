using SMS.Modules.Material.Models;

namespace SMS.Modules.Material.Services;

/// <summary>One inspection of a production order's output (A30 §18) — one-shot, against the whole produced quantity.</summary>
public interface IQualityInspectionService
{
    Task<Guid> CreateAsync(Guid productionOrderUuid, CreateQualityInspectionRequest req, int userId, CancellationToken ct = default);
    Task<QualityInspectionModel?> GetAsync(Guid uuid);
    Task<QualityInspectionModel?> GetForOrderAsync(Guid productionOrderUuid);
}
