using SMS.Modules.Material.Domain;
using SMS.Modules.Material.Models;
using SMS.Shared.Pagination;

namespace SMS.Modules.Material.Repositories;

/// <summary>
/// Production orders, their requirements, supply requirements and floor issues — the persistence
/// and the read models (A30 §11–§13, §16). Behaviour (planning, allocation, issuing) lives in the
/// services; this is what they load, save and read back.
/// </summary>
internal interface IProductionOrderRepository
{
    // ── Writes ────────────────────────────────────────────────────────────────
    /// <summary>
    /// A DRAFT order with its recipe snapshotted. Nothing is exploded yet. <paramref name="traceId"/>
    /// carries a chain's trace forward (a child order inheriting its parent's, or one raised for a
    /// sale order's own deficit inheriting the sale order's) — omitted, the order starts its own
    /// (A30-P5-07, trace_id propagation through SO→PO→SR→...→FGR).
    /// </summary>
    Task<ProductionOrder> CreateAsync(CreateProductionOrderRequest req, int userId, int? parentProductionOrderId = null, Guid? traceId = null);
    Task UpdateAsync(Guid uuid, UpdateProductionOrderRequest req, int userId);
    /// <summary>The tracked order with its requirements and recipe lines, for a service to change.</summary>
    Task<ProductionOrder> LoadAsync(Guid uuid);
    Task<ProductionOrder> LoadAsync(int id);
    Task SaveAsync();

    // ── Reads ─────────────────────────────────────────────────────────────────
    Task<PaginatedResponse<ProductionOrderListItemModel>> GetListAsync(ProductionOrderListFilter filter);
    Task<ProductionOrderDetailModel?> GetByUuidAsync(Guid uuid);
    Task<IReadOnlyList<ProductionMaterialModel>> GetMaterialsAsync(Guid uuid);
    Task<ProductionReadinessModel?> GetReadinessAsync(Guid uuid);
    Task<IReadOnlyList<MaterialShortageModel>> GetShortagesAsync(Guid? warehouseUuid);
    Task<PaginatedResponse<SupplyRequirementModel>> GetSupplyRequirementsAsync(SupplyRequirementListFilter filter);
    Task<SupplyRequirementModel?> GetSupplyRequirementAsync(Guid uuid);
    Task<IReadOnlyList<SupplyRequirementModel>> GetSupplyRequirementsForOrderAsync(Guid productionOrderUuid);
    Task<ProductionIssueModel?> GetIssueAsync(Guid uuid);
    Task<IReadOnlyList<ProductionIssueModel>> GetIssuesForOrderAsync(Guid productionOrderUuid);
}
