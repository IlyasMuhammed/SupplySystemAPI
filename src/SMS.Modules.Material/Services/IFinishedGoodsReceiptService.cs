using SMS.Modules.Material.Models;

namespace SMS.Modules.Material.Services;

/// <summary>Accepted production output moving into finished-goods stock (A30 §19).</summary>
public interface IFinishedGoodsReceiptService
{
    /// <summary>A DRAFT receipt, or — when <see cref="CreateFinishedGoodsReceiptRequest.Confirm"/> — one posted straight away.</summary>
    Task<Guid> CreateAsync(Guid productionOrderUuid, CreateFinishedGoodsReceiptRequest req, int userId, CancellationToken ct = default);
    Task ConfirmAsync(Guid uuid, int userId, CancellationToken ct = default);
    Task<FinishedGoodsReceiptModel?> GetAsync(Guid uuid);
    Task<IReadOnlyList<FinishedGoodsReceiptModel>> GetForOrderAsync(Guid productionOrderUuid);
}
