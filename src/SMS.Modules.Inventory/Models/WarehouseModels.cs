namespace SMS.Modules.Inventory.Models;

// ── Warehouse ─────────────────────────────────────────────────────────────────

public class WarehouseModel
{
    public int Id { get; set; }
    public Guid Uuid { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? Country { get; set; }
    public string? ContactName { get; set; }
    public string? ContactPhone { get; set; }
    public string? GoogleMapsUrl { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedDate { get; set; }
}

public class CreateWarehouseRequest
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? Country { get; set; }
    public string? ContactName { get; set; }
    public string? ContactPhone { get; set; }
    public string? GoogleMapsUrl { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
}

public class PatchWarehouseRequest
{
    public string? Name { get; set; }
    public string? Address { get; set; }
    public string? City { get; set; }
    public string? Country { get; set; }
    public string? ContactName { get; set; }
    public string? ContactPhone { get; set; }
    public string? GoogleMapsUrl { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public bool? IsActive { get; set; }
}

public class CreateZoneRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
    public string? Description { get; set; }
}

public class CreateBinRequest
{
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public class RackModel
{
    public int Id { get; set; }
    public int ZoneId { get; set; }
    public string RackCode { get; set; } = string.Empty;
    public string? RackName { get; set; }
    public bool IsActive { get; set; }
}

public class CreateRackRequest
{
    public string RackCode { get; set; } = string.Empty;
    public string? RackName { get; set; }
}

public class ShelfModel
{
    public int Id { get; set; }
    public int RackId { get; set; }
    public string ShelfCode { get; set; } = string.Empty;
    public string? ShelfLevel { get; set; }
    public bool IsActive { get; set; }
}

public class CreateShelfRequest
{
    public string ShelfCode { get; set; } = string.Empty;
    public string? ShelfLevel { get; set; }
}

// ── Stock level ───────────────────────────────────────────────────────────────

public class StockLevelModel
{
    public int InventoryItemId { get; set; }
    public int VariantId { get; set; }
    public Guid VariantUuid { get; set; }
    public string VariantSku { get; set; } = string.Empty;
    public string VariantName { get; set; } = string.Empty;
    public int ProductId { get; set; }
    public Guid ProductUuid { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string? CategoryName { get; set; }
    public string? UomCode { get; set; }
    public int WarehouseId { get; set; }
    public string WarehouseName { get; set; } = string.Empty;
    public int? BinId { get; set; }
    public string? BinCode { get; set; }
    public string? ZoneName { get; set; }
    public string? RackCode { get; set; }
    public string? ShelfCode { get; set; }
    public string? LocationPath { get; set; }
    public decimal QtyOnHand { get; set; }
    public decimal QtyReserved { get; set; }
    public decimal QtyAvailable { get; set; }  // computed: QtyOnHand - QtyReserved
    public decimal QtyOnOrder { get; set; }
    public decimal? ReorderPoint { get; set; }
    public bool IsBelowReorder { get; set; }
    public DateTime LastUpdated { get; set; }
}

public class StockLevelFilter
{
    public int? CategoryId { get; set; }
    public bool BelowReorderOnly { get; set; } = false;
    public bool IncludeZeroStock { get; set; } = false;
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public class ProductStockModel
{
    public int WarehouseId { get; set; }
    public Guid WarehouseUuid { get; set; }
    public string WarehouseCode { get; set; } = string.Empty;
    public string WarehouseName { get; set; } = string.Empty;
    public int? BinId { get; set; }
    public string? BinCode { get; set; }
    public decimal QtyOnHand { get; set; }
    public decimal QtyReserved { get; set; }
    public decimal QtyAvailable { get; set; }
    public decimal QtyOnOrder { get; set; }
    public DateTime LastUpdated { get; set; }
}

// One row per warehouse for a single variant — bins within the same warehouse are summed
// together, unlike ProductStockModel/GetProductStockAsync which is per-bin, per-variant detail
// (intentionally, for the product-detail stock table). Used wherever a picker needs "how much of
// THIS variant is available in THIS warehouse" as one clean figure — e.g. MIR line creation.
public class VariantWarehouseStockModel
{
    public int WarehouseId { get; set; }
    public Guid WarehouseUuid { get; set; }
    public string WarehouseCode { get; set; } = string.Empty;
    public string WarehouseName { get; set; } = string.Empty;
    public decimal QtyOnHand { get; set; }
    public decimal QtyReserved { get; set; }
    public decimal QtyAvailable { get; set; }
}

public class ReorderAlertModel
{
    public int VariantId { get; set; }
    public Guid VariantUuid { get; set; }
    public string VariantSku { get; set; } = string.Empty;
    public string VariantName { get; set; } = string.Empty;
    public int ProductId { get; set; }
    public Guid ProductUuid { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string? CategoryName { get; set; }
    public int WarehouseId { get; set; }
    public Guid WarehouseUuid { get; set; }
    public string WarehouseName { get; set; } = string.Empty;
    public decimal QtyOnHand { get; set; }
    public decimal QtyAvailable { get; set; }
    public decimal ReorderPoint { get; set; }
    public decimal? ReorderQty { get; set; }
}

public class MoveBinRequest
{
    public int BinId { get; set; }
}

// ── Warehouse structure tree ───────────────────────────────────────────────────

public class BinNodeModel
{
    public int Id { get; set; }
    public int ZoneId { get; set; }
    public int? RackId { get; set; }
    public int? ShelfId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; }
}

public class ShelfNodeModel
{
    public int Id { get; set; }
    public int RackId { get; set; }
    public string ShelfCode { get; set; } = string.Empty;
    public string? ShelfLevel { get; set; }
    public bool IsActive { get; set; }
    public List<BinNodeModel> Bins { get; set; } = new();
}

public class RackNodeModel
{
    public int Id { get; set; }
    public int ZoneId { get; set; }
    public string RackCode { get; set; } = string.Empty;
    public string? RackName { get; set; }
    public bool IsActive { get; set; }
    public List<ShelfNodeModel> Shelves { get; set; } = new();
    public List<BinNodeModel> DirectBins { get; set; } = new();
}

public class ZoneNodeModel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public List<RackNodeModel> Racks { get; set; } = new();
    public List<BinNodeModel> DirectBins { get; set; } = new();
}

public class WarehouseStructureModel
{
    public int WarehouseId { get; set; }
    public List<ZoneNodeModel> Zones { get; set; } = new();
}

public class StructureConflictResult
{
    public int ChildCount { get; set; }
    public string ChildType { get; set; } = string.Empty;
}

public record StructureDeactivateResult(bool Found, bool Succeeded, int ChildCount = 0, string ChildType = "")
{
    public static StructureDeactivateResult Ok()                          => new(true,  true);
    public static StructureDeactivateResult Missing()                     => new(false, false);
    public static StructureDeactivateResult BlockedBy(int n, string type) => new(true,  false, n, type);
}

// ── Structure update requests ─────────────────────────────────────────────────

public class UpdateZoneRequest
{
    public string? Name { get; set; }
    public string? Code { get; set; }
    public string? Description { get; set; }
}

public class UpdateRackRequest
{
    public string? RackCode { get; set; }
    public string? RackName { get; set; }
}

public class UpdateShelfRequest
{
    public string? ShelfCode { get; set; }
    public string? ShelfLevel { get; set; }
}

public class UpdateBinRequest
{
    public string? Code { get; set; }
    public string? Description { get; set; }
}
