namespace SMS.Modules.Inventory.Models;

// A37 §6 — GET /api/sync/catalog. Inactive rows are sent too (isActive=false), so a client can retire them.

public class CatalogSyncResponse
{
    /// <summary>When the read started (UTC).</summary>
    public DateTime ServerTime { get; set; }
    /// <summary>At least one collection was cut at the limit; ask again with <see cref="NextSince"/>.</summary>
    public bool HasMore { get; set; }
    /// <summary>
    /// What to send as <c>since</c> next: the earliest last-modifiedAt of the collections that were cut, else
    /// <see cref="ServerTime"/>. Rows already received may come again (upsert by id).
    /// </summary>
    public DateTime NextSince { get; set; }
    public List<SyncProductModel>   Products   { get; set; } = [];
    public List<SyncVariantModel>   Variants   { get; set; } = [];
    public List<SyncCategoryModel>  Categories { get; set; } = [];
    public List<SyncLookupModel>    TaxCodes   { get; set; } = [];
    public List<SyncLookupModel>    Uoms       { get; set; } = [];
    public List<SyncWarehouseModel> Warehouses { get; set; } = [];
}

public class SyncProductModel
{
    public int Id { get; set; }
    public Guid Uuid { get; set; }
    public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ShortName { get; set; }
    public int? CategoryId { get; set; }
    public int? SubCategoryId { get; set; }
    public string? Brand { get; set; }
    public string? UomCode { get; set; }
    public string ProductType { get; set; } = string.Empty;
    public bool IsSaleable { get; set; }
    public bool IsStockable { get; set; }
    public bool IsBatchTracked { get; set; }
    public bool IsSerialTracked { get; set; }
    public string? ImageUrl { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime ModifiedAt { get; set; }
}

public class SyncVariantModel
{
    public int Id { get; set; }
    public Guid Uuid { get; set; }
    public int ProductId { get; set; }
    public string Sku { get; set; } = string.Empty;
    public string VariantName { get; set; } = string.Empty;
    public string? Barcode { get; set; }
    public decimal? SellingPrice { get; set; }
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; }
    public bool IsAvailableForRetail { get; set; }
    public bool IsAvailableForPos { get; set; }
    public int? SortOrder { get; set; }
    public DateTime ModifiedAt { get; set; }
}

public class SyncCategoryModel
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime ModifiedAt { get; set; }
}

public class SyncLookupModel
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public bool IsActive { get; set; }
    public int SortOrder { get; set; }
    public DateTime ModifiedAt { get; set; }
}

public class SyncWarehouseModel
{
    public int Id { get; set; }
    public Guid Uuid { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTime ModifiedAt { get; set; }
}
