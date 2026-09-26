using SMS.Modules.Material.Domain;

namespace SMS.Modules.Material.Services;

/// <summary>A30-P5-01 — the ten manufacturing-lifecycle notifications (FSD §30), one method each.</summary>
internal interface IManufacturingNotificationService
{
    Task ProductionOrderCreatedAsync(ProductionOrder po, string productName, int actingUserId);
    Task ProductionOrderReadyAsync(ProductionOrder po);
    Task ProductionOrderCompletedAsync(ProductionOrder po);
    Task ShortageAlertAsync(ProductionOrder po, string materialName, decimal quantity, string uom);
    Task QualityInspectionRequiredAsync(ProductionOrder po);
    Task QualityInspectionCompletedAsync(ProductionOrder po, QualityInspection qi);
    Task FinishedGoodsReceiptConfirmedAsync(ProductionOrder po, FinishedGoodsReceipt fgr, string productName, string warehouseName);
    Task SupplyRequirementCreatedAsync(SupplyRequirement sr, string productName);
    Task AllocationCompletedAsync(ProductionOrder po, string materialName, decimal quantity, string uom);
    Task ChainedProductionOrderCreatedAsync(ProductionOrder parent, ProductionOrder child, string materialName);
}
