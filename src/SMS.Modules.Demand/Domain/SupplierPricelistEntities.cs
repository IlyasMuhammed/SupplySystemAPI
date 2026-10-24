using SMS.Shared.Common;

namespace SMS.Modules.Demand.Domain;

/// <summary>A45 C2 — where a purchase order line's unit price came from. Stored as tinyint on the line.</summary>
public enum PriceSource : byte
{
    /// <summary>Typed by the buyer, no pricelist match (also every line created before A45).</summary>
    Manual = 0,
    /// <summary>Resolved from a supplier pricelist row and not changed.</summary>
    Pricelist = 1,
    /// <summary>Was resolved from a pricelist row, the buyer changed it.</summary>
    Override = 2,
    /// <summary>Came from an awarded RFQ (contractual — never re-looked-up).</summary>
    RFQ = 3,
    /// <summary>
    /// A46 E-7 — a price locked by a blanket order / purchase agreement (never re-looked-up). There is no blanket-order entity yet:
    /// only trusted internal callers (the line factory with an explicit source) can store it; an API client claiming 4 gets Manual.
    /// </summary>
    BlanketOrder = 4
}

/// <summary>A45 C1 — which date a pricelist row's validity window is checked against.</summary>
public enum PricingDateBasis : byte
{
    /// <summary>The purchase order's order date (default).</summary>
    OrderDate = 0,
    /// <summary>The line's expected delivery date; falls back to the order date when the line has none (BR-03).</summary>
    DeliveryDate = 1
}

/// <summary>
/// A45 C1 / D-1..D-9 — one agreed price for a supplier: product (optionally one variant), quantity break, validity window,
/// currency and priority. Lives in Demand beside the purchase orders it prices (the spec's SMS.Purchase / "purchase" schema do
/// not exist). Cross-module references are UUIDs without a foreign key, like every other Demand entity.
/// </summary>
internal class SupplierPricelist : ITenantScopedEntity
{
    public int      Id             { get; set; }
    public Guid     UUID           { get; set; } = Guid.NewGuid();
    public Guid     OrganizationId { get; set; }
    /// <summary>inventory.Products.Uuid — always set, so a product's Purchase tab can list every row for it.</summary>
    public Guid     ProductUuid    { get; set; }
    /// <summary>inventory.ProductVariants.Uuid — null means the row applies to every variant of the product (D-3).</summary>
    public Guid?    VariantUuid    { get; set; }
    /// <summary>suppliers.BusinessPartners.UUID of the vendor (D-4).</summary>
    public Guid     SupplierUuid   { get; set; }
    public string?  VendorProductCode { get; set; }
    public string?  VendorProductName { get; set; }
    /// <summary>The price applies when the ordered quantity is at least this (0 = any quantity).</summary>
    public decimal  MinQuantity    { get; set; } = 1m;
    public decimal  UnitPrice      { get; set; }
    /// <summary>Lookups currency UUID (D-5). Resolved only for a purchase order in the same currency.</summary>
    public Guid     CurrencyId     { get; set; }
    public PricingDateBasis PricingDateBasis { get; set; } = PricingDateBasis.OrderDate;
    /// <summary>Inclusive; compared on the date part (D-6).</summary>
    public DateTime ValidFrom      { get; set; }
    public DateTime ValidTo        { get; set; }
    public int?     LeadTimeDays   { get; set; }
    /// <summary>Lower wins (0 is the highest priority).</summary>
    public int      Priority       { get; set; }
    public string?  Notes          { get; set; }
    /// <summary>False = soft-deleted (BR-16): kept for the purchase order lines that reference it, never resolved.</summary>
    public bool     IsActive       { get; set; } = true;
    public int      CreatedBy      { get; set; }
    public DateTime CreatedDate    { get; set; } = DateTime.UtcNow;
    public int?     ModifiedBy     { get; set; }
    public DateTime? ModifiedDate  { get; set; }
    /// <summary>SQL Server rowversion — the optimistic-concurrency token (D-7).</summary>
    public byte[]   RowVersion     { get; set; } = [];
}
