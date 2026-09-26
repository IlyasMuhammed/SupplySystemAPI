using SMS.Modules.Material.Models;
using SMS.Shared.Pagination;

namespace SMS.Modules.Material.Services;

/// <summary>
/// Production orders end to end (A30 §11, §12, §17, §28.3): create, explode the recipe into what
/// it needs, hold and chase materials, run the floor, and the supply-requirement reads/writes that
/// sit alongside a production order's own screen.
/// </summary>
public interface IProductionOrderService
{
    Task<Guid> CreateAsync(CreateProductionOrderRequest req, int userId, CancellationToken ct = default);
    Task UpdateAsync(Guid uuid, UpdateProductionOrderRequest req, int userId, CancellationToken ct = default);
    Task<PaginatedResponse<ProductionOrderListItemModel>> GetListAsync(ProductionOrderListFilter filter);
    Task<ProductionOrderDetailModel?> GetByUuidAsync(Guid uuid);
    Task<IReadOnlyList<ProductionMaterialModel>> GetMaterialsAsync(Guid uuid);

    /// <summary>Explodes the snapshotted recipe into requirements, registers and allocates demand, raises supply for what is short (BR-PR03).</summary>
    Task<ProductionReadinessModel> PlanAsync(Guid uuid, int userId, CancellationToken ct = default);
    Task<ProductionReadinessModel?> GetReadinessAsync(Guid uuid);
    Task<IReadOnlyList<MaterialShortageModel>> GetShortagesAsync(Guid? warehouseUuid);

    Task StartAsync(Guid uuid, int userId, CancellationToken ct = default);
    Task ReportOutputAsync(Guid uuid, ReportOutputRequest req, int userId, CancellationToken ct = default);
    Task CompleteAsync(Guid uuid, int userId, CancellationToken ct = default);
    Task CancelAsync(Guid uuid, CancelProductionOrderRequest req, int userId, CancellationToken ct = default);

    // Supply requirements — reachable from a production order's own screen and their own list.
    Task<PaginatedResponse<SupplyRequirementModel>> GetSupplyRequirementsAsync(SupplyRequirementListFilter filter);
    Task<SupplyRequirementModel?> GetSupplyRequirementAsync(Guid uuid);
    Task<IReadOnlyList<SupplyRequirementModel>> GetSupplyRequirementsForOrderAsync(Guid productionOrderUuid);
    Task<Guid> CreateSupplyRequirementAsync(CreateSupplyRequirementRequest req, int userId, CancellationToken ct = default);
    Task CancelSupplyRequirementAsync(Guid uuid, CancelSupplyRequirementRequest req, int userId, CancellationToken ct = default);

    Task<IReadOnlyList<ProductionIssueModel>> GetIssuesForOrderAsync(Guid productionOrderUuid);
}
