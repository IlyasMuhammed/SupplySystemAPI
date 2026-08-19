namespace SMS.Modules.Inventory.Models;

public class LedgerEntryCommand
{
    public int VariantId { get; set; }
    public int WarehouseId { get; set; }
    public string TransactionType { get; set; } = string.Empty;
    public string ReferenceType { get; set; } = string.Empty;
    public Guid ReferenceId { get; set; }
    public string ReferenceNumber { get; set; } = string.Empty;
    public decimal? QuantityIn { get; set; }
    public decimal? QuantityOut { get; set; }
    public decimal UnitCost { get; set; }
    public string? Notes { get; set; }
    public int CreatedBy { get; set; }

    // FSD Addendum 24 (ML-003) — when both are set, CreateEntryAsync also mirrors this movement
    // into the organization-wide master product ledger (SMS.Modules.Finance, via a Shared-defined
    // interface — Inventory has no direct reference to Finance). Left blank, no master entry is
    // written — safe default for callers/tests not yet updated.
    // SUPPLIER | WAREHOUSE | PROJECT | DEPARTMENT | ADJUSTMENT | ...
    public string? SourceType { get; set; }
    public string? SourceName { get; set; }
    public string? DestinationType { get; set; }
    public string? DestinationName { get; set; }
}

public class InventoryLedgerEntryDto
{
    public Guid LedgerId { get; set; }
    public int VariantId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public string VariantSku { get; set; } = string.Empty;
    public int WarehouseId { get; set; }
    public string WarehouseName { get; set; } = string.Empty;
    public DateTime TransactionDate { get; set; }
    public string TransactionType { get; set; } = string.Empty;
    public string ReferenceType { get; set; } = string.Empty;
    public Guid ReferenceId { get; set; }
    public string ReferenceNumber { get; set; } = string.Empty;
    public decimal? QuantityIn { get; set; }
    public decimal? QuantityOut { get; set; }
    public decimal BalanceAfter { get; set; }
    public decimal UnitCost { get; set; }
    public decimal TransactionValue { get; set; }
    public string? Notes { get; set; }
    public int CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class LedgerFilterDto
{
    public int? VariantId { get; set; }
    public int? WarehouseId { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public string? TransactionType { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

public class LedgerPagedResult
{
    public List<InventoryLedgerEntryDto> Entries { get; set; } = new();
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int TotalPages { get; set; }
    public decimal CurrentBalance { get; set; }
    public decimal TotalValueIn { get; set; }
    public decimal TotalValueOut { get; set; }
}
