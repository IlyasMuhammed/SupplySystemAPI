using SMS.Modules.Material.Models;

namespace SMS.Modules.Material.Services;

/// <summary>Moving materials between stock and the production floor for one order (A30 §16).</summary>
public interface IProductionMaterialIssueService
{
    /// <summary>A DRAFT issue, or — when <see cref="CreateProductionIssueRequest.Confirm"/> — one posted straight away.</summary>
    Task<Guid> CreateAsync(Guid productionOrderUuid, CreateProductionIssueRequest req, int userId, CancellationToken ct = default);
    /// <summary>Posts a DRAFT issue's stock movements.</summary>
    Task ConfirmAsync(Guid uuid, int userId, CancellationToken ct = default);
    Task ReverseAsync(Guid uuid, string reason, int userId, CancellationToken ct = default);
    Task<ProductionIssueModel?> GetAsync(Guid uuid);
}
