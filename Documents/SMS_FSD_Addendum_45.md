# SMS Functional Specification — Addendum 45

## Supplier Pricelist & Purchase Order Auto-Pricing

| Field               | Value                                              |
| ------------------- | -------------------------------------------------- |
| Document ID         | SMS-FSD-ADD-045                                    |
| Version             | 1.0                                                |
| Status              | Draft — Pending Approval                           |
| Author              | Muhammad Ilyas                                     |
| Date                | 2026-10-17                                         |
| Parent FSD          | SMS-FSD (Master)                                   |
| Depends On          | Addendum 41 (Product & Inventory)                  |
| Module              | SMS.Purchase                                       |
| Schema              | `purchase`                                         |

---

## 1. Purpose

When a buyer creates a Purchase Order line, the system should **automatically resolve the unit price** from a pre-configured Supplier Pricelist — matching the vendor, product, quantity, and an effective date that falls within the pricelist's validity window. If no valid price is found, the buyer is warned and can either **enter a price manually** or **redirect to the RFQ workflow**. Even when a price is auto-filled, the buyer can **override** it for exceptional cases, with full audit trail.

The pricing date basis is **configurable per pricelist record**: standard goods use the PO order date (default), while seasonal or commodity items can use the PO line's expected delivery date — following SAP's Pricing Date Category pattern (EKPO-MEPRF).

---

## 2. Change Summary

| ID  | Change                                           | Type               | Priority |
| --- | ------------------------------------------------ | ------------------ | -------- |
| C1  | New Entity: `SupplierPricelist`                  | Schema + Migration | P0       |
| C2  | PO Line Enhancement: Price Source Tracking       | Schema + Migration | P0       |
| C3  | Price Resolution Service                         | Backend Service    | P0       |
| C4  | Supplier Pricelist Management UI                 | UI Screen          | P1       |
| C5  | PO Line Auto-Pricing UI Behavior                 | UI + Backend       | P0       |
| C6  | Pricelist Overlap Validation                     | Backend Rule       | P1       |
| C7  | Quantity / Date Change Re-lookup                 | UI + Backend       | P1       |

---

## 3. Dependencies

| Dependency                       | Status         | Notes                                                                                           |
| -------------------------------- | -------------- | ----------------------------------------------------------------------------------------------- |
| `purchase.PurchaseOrders`        | Existing       | PO header entity must exist                                                                     |
| `purchase.PurchaseOrderLines`    | Existing       | PO line entity — receives new columns (C2)                                                      |
| Addendum 41: Product & Inventory | Draft          | `inventory.Products` — ProductId FK                                                             |
| `core.BusinessPartners`          | Existing       | Vendor records                                                                                  |
| `core.Currencies`                | Existing       | Currency reference                                                                              |
| Multi-Currency Conversion        | Future         | When vendor currency ≠ PO currency, exchange rate conversion needed. Until then, same-currency matching enforced |
| RFQ Workflow                     | Existing / TBD | "Create RFQ" redirect requires existing RFQ screen                                              |

---

## 4. Change Details

### C1 — New Entity: SupplierPricelist

**Table:** `purchase.SupplierPricelists`

Stores vendor-specific product prices with effective dates, quantity breaks, and configurable pricing date basis. Equivalent of Odoo's `product.supplierinfo` with SAP's pricing date category flexibility.

| Column             | Type             | Null | Default            | Description                                     |
| ------------------ | ---------------- | ---- | ------------------ | ----------------------------------------------- |
| `Id`               | int              | NO   | IDENTITY           | PK                                              |
| `OrgId`            | int              | NO   | —                  | FK → `core.Organizations`                       |
| `ProductId`        | int              | NO   | —                  | FK → `inventory.Products`                       |
| `VendorId`         | int              | NO   | —                  | FK → `core.BusinessPartners`                    |
| `VendorProductCode`| nvarchar(100)    | YES  | NULL               | Vendor's own SKU / code for this product        |
| `VendorProductName`| nvarchar(255)    | YES  | NULL               | Vendor's own name for this product              |
| `MinQuantity`      | decimal(18,4)    | NO   | 1.0000             | Price applies when order qty ≥ this value       |
| `UnitPrice`        | decimal(18,4)    | NO   | —                  | Agreed unit price                               |
| `CurrencyId`       | int              | NO   | —                  | FK → `core.Currencies`                          |
| `PricingDateBasis` | tinyint          | NO   | 0                  | 0 = Order Date, 1 = Delivery Date               |
| `ValidFrom`        | date             | NO   | —                  | Price effective from (inclusive)                 |
| `ValidTo`          | date             | NO   | —                  | Price effective until (inclusive)                |
| `LeadTimeDays`     | int              | YES  | NULL               | Expected delivery lead time in days             |
| `Priority`         | int              | NO   | 0                  | Lower value = higher priority (0 is highest)    |
| `Notes`            | nvarchar(500)    | YES  | NULL               | Internal notes                                  |
| `IsActive`         | bit              | NO   | 1                  | Soft-delete / deactivation flag                 |
| `CreatedBy`        | int              | NO   | —                  | FK → `core.Users`                               |
| `CreatedAt`        | datetime2(7)     | NO   | SYSUTCDATETIME()   | —                                               |
| `ModifiedBy`       | int              | NO   | —                  | FK → `core.Users`                               |
| `ModifiedAt`       | datetime2(7)     | NO   | SYSUTCDATETIME()   | —                                               |
| `RowVersion`       | rowversion       | NO   | —                  | Concurrency token                               |

**Indexes:**

| Name                                     | Columns                                                        | Type            | Include                                                        |
| ---------------------------------------- | -------------------------------------------------------------- | --------------- | -------------------------------------------------------------- |
| `IX_SupplierPricelists_Lookup`           | `OrgId, ProductId, VendorId, IsActive, ValidFrom, ValidTo`     | Non-clustered   | `UnitPrice, MinQuantity, Priority, PricingDateBasis, CurrencyId` |
| `IX_SupplierPricelists_Vendor`           | `OrgId, VendorId, IsActive`                                    | Non-clustered   | `ProductId`                                                    |
| `UQ_SupplierPricelists_NoDuplicate`      | `OrgId, ProductId, VendorId, MinQuantity, ValidFrom, ValidTo`  | Unique filtered | `WHERE IsActive = 1` — prevents exact-duplicate active records |

---

### C2 — PO Line Enhancement: Price Source Tracking

**Table:** `purchase.PurchaseOrderLines` (existing — add columns)

| Column                  | Type           | Null | Default | Description                                              |
| ----------------------- | -------------- | ---- | ------- | -------------------------------------------------------- |
| `PriceSource`           | tinyint        | NO   | 0       | 0 = Manual, 1 = Pricelist, 2 = Override, 3 = RFQ        |
| `SystemResolvedPrice`   | decimal(18,4)  | YES  | NULL    | Original price the system resolved (preserved on override) |
| `SupplierPricelistId`   | int            | YES  | NULL    | FK → `purchase.SupplierPricelists` (null if Manual/RFQ)  |
| `PriceOverrideReason`   | nvarchar(500)  | YES  | NULL    | Buyer's reason for overriding the system price           |

**Enums:**

```csharp
public enum PriceSource : byte
{
    Manual    = 0,   // Buyer typed the price, no pricelist match
    Pricelist = 1,   // Auto-resolved from SupplierPricelist
    Override  = 2,   // Was Pricelist, buyer changed it
    RFQ       = 3    // Price came from completed RFQ
}

public enum PricingDateBasis : byte
{
    OrderDate    = 0,   // Check validity against PO header order date (default)
    DeliveryDate = 1    // Check validity against PO line expected delivery date
}
```

---

### C3 — Price Resolution Service

**Interface:**

```csharp
public interface IPurchasePriceResolver
{
    Task<PriceResolutionResult> ResolveAsync(PriceResolutionRequest request);
}

public class PriceResolutionRequest
{
    public int OrgId { get; set; }
    public int ProductId { get; set; }
    public int VendorId { get; set; }
    public decimal Quantity { get; set; }
    public DateTime OrderDate { get; set; }
    public DateTime? ExpectedDeliveryDate { get; set; }
}

public class PriceResolutionResult
{
    public bool Found { get; set; }
    public decimal? UnitPrice { get; set; }
    public int? CurrencyId { get; set; }
    public int? SupplierPricelistId { get; set; }
    public PricingDateBasis? DateBasisUsed { get; set; }
    public DateTime? EffectiveDateUsed { get; set; }
    public string WarningMessage { get; set; }
}
```

**Resolution Algorithm:**

```
INPUT: OrgId, ProductId, VendorId, Quantity, OrderDate, ExpectedDeliveryDate

1. Query purchase.SupplierPricelists WHERE:
       OrgId       = @OrgId
   AND ProductId   = @ProductId
   AND VendorId    = @VendorId
   AND IsActive    = 1
   AND MinQuantity <= @Quantity
   ORDER BY Priority ASC, MinQuantity DESC

2. FOR EACH candidate (in order):

   a. Determine effective date:
      IF PricingDateBasis = DeliveryDate AND @ExpectedDeliveryDate IS NOT NULL
         effectiveDate = @ExpectedDeliveryDate
      ELSE
         effectiveDate = @OrderDate

   b. Check validity window:
      IF effectiveDate >= ValidFrom AND effectiveDate <= ValidTo
         RETURN → Found = true
                  UnitPrice = candidate.UnitPrice
                  CurrencyId = candidate.CurrencyId
                  SupplierPricelistId = candidate.Id
                  DateBasisUsed = candidate.PricingDateBasis
                  EffectiveDateUsed = effectiveDate

3. No match found:
   RETURN → Found = false
            WarningMessage = "No configured price found for
              {ProductName} from {VendorName} effective {date}.
              Enter price manually or create an RFQ."
```

**SQL Query (core lookup):**

```sql
SELECT TOP 1
    sp.Id,
    sp.UnitPrice,
    sp.CurrencyId,
    sp.PricingDateBasis,
    sp.MinQuantity
FROM purchase.SupplierPricelists sp
WHERE sp.OrgId = @OrgId
  AND sp.ProductId = @ProductId
  AND sp.VendorId = @VendorId
  AND sp.IsActive = 1
  AND sp.MinQuantity <= @Quantity
  AND (
      (sp.PricingDateBasis = 0 AND @OrderDate BETWEEN sp.ValidFrom AND sp.ValidTo)
      OR
      (sp.PricingDateBasis = 1 AND COALESCE(@ExpectedDeliveryDate, @OrderDate)
                                    BETWEEN sp.ValidFrom AND sp.ValidTo)
  )
ORDER BY sp.Priority ASC, sp.MinQuantity DESC;
```

---

### C4 — Supplier Pricelist Management UI

**Access Points:**

| Entry Point                        | Behavior                                      |
| ---------------------------------- | --------------------------------------------- |
| Product Detail → **Purchase Tab**  | Grid filtered by ProductId; Vendor is editable |
| Vendor Detail → **Products Tab**   | Grid filtered by VendorId; Product is editable |
| Purchase → **Supplier Pricelists** | Full list view with all filters               |

**Grid Columns:**

| Column               | Editable | Notes                                         |
| -------------------- | -------- | --------------------------------------------- |
| Vendor / Product     | Yes      | Context-dependent (hidden when pre-filtered)  |
| Vendor Product Code  | Yes      | Optional                                      |
| Min Quantity         | Yes      | Default: 1                                    |
| Unit Price           | Yes      | Required, > 0                                 |
| Currency             | Yes      | Dropdown, default: org base currency          |
| Pricing Date Basis   | Yes      | Dropdown: "Order Date" / "Delivery Date"      |
| Valid From           | Yes      | Required, date picker                         |
| Valid To             | Yes      | Required, ≥ Valid From                        |
| Lead Time (days)     | Yes      | Optional                                      |
| Priority             | Yes      | Default: 0 — lower = higher priority          |
| Active               | Yes      | Toggle switch                                 |

**Wireframe — Product Purchase Tab:**

```
┌─────────────────────────────────────────────────────────────────────┐
│  Product: Widget-X (SKU: WDG-001)                                  │
│  ┌──────────┬──────────┬───────────┬──────────┬──────────┐         │
│  │ General  │ Pricing  │ Inventory │ Purchase │ History  │         │
│  └──────────┴──────────┴───────────┴────┬─────┴──────────┘         │
│                                         ▼                          │
│  Vendor Prices                                    [+ Add Price]    │
│  ┌────────────┬────────┬────────┬──────────┬──────────┬─────────┐  │
│  │ Vendor     │ Min Qty│ Price  │ Currency │ Valid    │ Date    │  │
│  │            │        │        │          │ Period   │ Basis   │  │
│  ├────────────┼────────┼────────┼──────────┼──────────┼─────────┤  │
│  │ Acme Corp  │      1 │ $15.00 │ USD      │ Jan–Dec  │ Order   │  │
│  │            │        │        │          │ 2026     │ Date    │  │
│  ├────────────┼────────┼────────┼──────────┼──────────┼─────────┤  │
│  │ Acme Corp  │    100 │ $12.50 │ USD      │ Jan–Dec  │ Order   │  │
│  │            │        │        │          │ 2026     │ Date    │  │
│  ├────────────┼────────┼────────┼──────────┼──────────┼─────────┤  │
│  │ Globex Inc │      1 │ $14.00 │ USD      │ Mar–Aug  │ Delivery│  │
│  │            │        │        │          │ 2026     │ Date    │  │
│  └────────────┴────────┴────────┴──────────┴──────────┴─────────┘  │
│                                                                     │
│  ⓘ Prices are resolved by priority. When multiple records match,   │
│    the lowest priority number wins.                                 │
└─────────────────────────────────────────────────────────────────────┘
```

---

### C5 — PO Line Auto-Pricing Behavior

**Trigger:** Fires when any of these change on a **draft** PO line: Product, Vendor (header), Quantity, Expected Delivery Date.

**Flow Diagram:**

```
Buyer adds/changes product on PO line
         │
         ▼
System calls IPurchasePriceResolver.ResolveAsync()
         │
    ┌────┴─────────────┐
    ▼                  ▼
  FOUND              NOT FOUND
    │                  │
    ▼                  ▼
Auto-fill            ⚠ Warning banner on the PO line:
UnitPrice            ┌─────────────────────────────────────┐
PriceSource = 1      │ ⚠ No configured price found for     │
    │                │   Widget-X from Acme Corp effective  │
    │                │   2026-10-17.                        │
    │                │                                      │
    │                │   [Enter Manually]   [Create RFQ]    │
    │                └─────────────────────────────────────┘
    │                       │                  │
    │                       ▼                  ▼
    │                  Price field         Navigate to RFQ
    │                  gets focus          screen pre-filled
    │                  PriceSource = 0     with Vendor + Product
    │                  (Manual)            + Quantity
    │
    ▼
Price field remains EDITABLE (not read-only)
    │
    ├── Buyer does NOT change → PriceSource stays = 1 (Pricelist)
    │
    └── Buyer changes price  → PriceSource = 2 (Override)
                                SystemResolvedPrice = original price
                                Show optional "Reason" text field:
                                ┌──────────────────────────────┐
                                │ Override Reason (optional):   │
                                │ [Vendor offered spot discount]│
                                └──────────────────────────────┘
```

**Price Source Indicator:**

A small badge or icon next to the price field indicates the source:

```
  Unit Price: [$12.50]  🏷️ Pricelist    ← auto-resolved
  Unit Price: [$11.00]  ✏️ Override     ← buyer changed system price
  Unit Price: [$14.50]  ⌨️ Manual       ← no system price, buyer entered
  Unit Price: [$13.00]  📋 RFQ          ← from completed RFQ
```

---

### C6 — Pricelist Overlap Validation

**Trigger:** On save of a SupplierPricelist record.

**Check:** Does another **active** record exist for the same OrgId + ProductId + VendorId + same MinQuantity with an overlapping date range?

```sql
-- Overlap detection query
SELECT Id, ValidFrom, ValidTo, Priority
FROM purchase.SupplierPricelists
WHERE OrgId = @OrgId
  AND ProductId = @ProductId
  AND VendorId = @VendorId
  AND MinQuantity = @MinQuantity
  AND IsActive = 1
  AND Id <> @CurrentId
  AND ValidFrom <= @NewValidTo
  AND ValidTo   >= @NewValidFrom;
```

**Behavior:** Warning toast, NOT a hard block.

> *"Another price record (Priority {X}, Valid {From}–{To}) overlaps with this date range. The record with the lowest priority number will be selected during price resolution."*

---

### C7 — Quantity / Date Change Re-lookup

**Quantity Change:**

| Current PriceSource | Behavior on Qty Change                                                                |
| ------------------- | ------------------------------------------------------------------------------------- |
| Pricelist (1)       | Automatic re-lookup. Price updates silently if a different tier matches.               |
| Override (2)        | Toast: *"System price for qty {X} is {Y}. Keep your price of {Z}?"* — [Keep] [Update] |
| Manual (0)          | Toast: *"A configured price of {Y} is available for qty {X}."* — [Keep] [Apply]       |
| RFQ (3)             | No re-lookup. RFQ price is contractual.                                               |

**Delivery Date Change:**

Re-lookup fires only when the matched pricelist record's `PricingDateBasis = DeliveryDate`. Same toast behavior as quantity change based on current PriceSource.

**Order Date Change (PO Header):**

When the PO header order date changes, re-validate all lines where the resolved pricelist record had `PricingDateBasis = OrderDate`. Show summary banner:

```
┌────────────────────────────────────────────────────────────────┐
│ ⚠ Order date changed to 2026-11-15. 3 line(s) may have       │
│   different prices for the new date.                           │
│                                                                │
│   [Re-lookup All]              [Keep Current Prices]           │
└────────────────────────────────────────────────────────────────┘
```

---

## 5. Database Migrations

| ID  | Description                                                                                   | Type | Rollback                      |
| --- | --------------------------------------------------------------------------------------------- | ---- | ----------------------------- |
| M1  | Create table `purchase.SupplierPricelists` with all columns, FKs, and indexes (C1)            | DDL  | DROP TABLE                    |
| M2  | Add `PriceSource`, `SystemResolvedPrice`, `SupplierPricelistId`, `PriceOverrideReason` to `purchase.PurchaseOrderLines` | DDL  | DROP COLUMN ×4                |
| M3  | Backfill `PriceSource = 0` (Manual) for all existing PO lines                                 | DML  | No-op (0 is default)          |

**M1 — DDL:**

```sql
CREATE TABLE purchase.SupplierPricelists
(
    Id                INT              IDENTITY(1,1) NOT NULL,
    OrgId             INT              NOT NULL,
    ProductId         INT              NOT NULL,
    VendorId          INT              NOT NULL,
    VendorProductCode NVARCHAR(100)    NULL,
    VendorProductName NVARCHAR(255)    NULL,
    MinQuantity       DECIMAL(18,4)    NOT NULL DEFAULT 1.0000,
    UnitPrice         DECIMAL(18,4)    NOT NULL,
    CurrencyId        INT              NOT NULL,
    PricingDateBasis  TINYINT          NOT NULL DEFAULT 0,
    ValidFrom         DATE             NOT NULL,
    ValidTo           DATE             NOT NULL,
    LeadTimeDays      INT              NULL,
    [Priority]        INT              NOT NULL DEFAULT 0,
    Notes             NVARCHAR(500)    NULL,
    IsActive          BIT              NOT NULL DEFAULT 1,
    CreatedBy         INT              NOT NULL,
    CreatedAt         DATETIME2(7)     NOT NULL DEFAULT SYSUTCDATETIME(),
    ModifiedBy        INT              NOT NULL,
    ModifiedAt        DATETIME2(7)     NOT NULL DEFAULT SYSUTCDATETIME(),
    RowVersion        ROWVERSION       NOT NULL,

    CONSTRAINT PK_SupplierPricelists PRIMARY KEY CLUSTERED (Id),
    CONSTRAINT FK_SupplierPricelists_Org       FOREIGN KEY (OrgId)       REFERENCES core.Organizations(Id),
    CONSTRAINT FK_SupplierPricelists_Product   FOREIGN KEY (ProductId)   REFERENCES inventory.Products(Id),
    CONSTRAINT FK_SupplierPricelists_Vendor    FOREIGN KEY (VendorId)    REFERENCES core.BusinessPartners(Id),
    CONSTRAINT FK_SupplierPricelists_Currency  FOREIGN KEY (CurrencyId)  REFERENCES core.Currencies(Id),
    CONSTRAINT CK_SupplierPricelists_Price     CHECK (UnitPrice > 0),
    CONSTRAINT CK_SupplierPricelists_MinQty    CHECK (MinQuantity >= 0),
    CONSTRAINT CK_SupplierPricelists_Dates     CHECK (ValidTo >= ValidFrom),
    CONSTRAINT CK_SupplierPricelists_DateBasis CHECK (PricingDateBasis IN (0, 1))
);

-- Price resolution lookup (covering index)
CREATE NONCLUSTERED INDEX IX_SupplierPricelists_Lookup
    ON purchase.SupplierPricelists (OrgId, ProductId, VendorId, IsActive, ValidFrom, ValidTo)
    INCLUDE (UnitPrice, MinQuantity, [Priority], PricingDateBasis, CurrencyId);

-- Vendor-centric listing
CREATE NONCLUSTERED INDEX IX_SupplierPricelists_Vendor
    ON purchase.SupplierPricelists (OrgId, VendorId, IsActive)
    INCLUDE (ProductId);

-- Prevent exact duplicate active records
CREATE UNIQUE NONCLUSTERED INDEX UQ_SupplierPricelists_NoDuplicate
    ON purchase.SupplierPricelists (OrgId, ProductId, VendorId, MinQuantity, ValidFrom, ValidTo)
    WHERE IsActive = 1;
```

**M2 — DDL:**

```sql
ALTER TABLE purchase.PurchaseOrderLines
    ADD PriceSource          TINYINT        NOT NULL DEFAULT 0,
        SystemResolvedPrice  DECIMAL(18,4)  NULL,
        SupplierPricelistId  INT            NULL,
        PriceOverrideReason  NVARCHAR(500)  NULL;

ALTER TABLE purchase.PurchaseOrderLines
    ADD CONSTRAINT FK_POLines_SupplierPricelist
        FOREIGN KEY (SupplierPricelistId) REFERENCES purchase.SupplierPricelists(Id);

ALTER TABLE purchase.PurchaseOrderLines
    ADD CONSTRAINT CK_POLines_PriceSource CHECK (PriceSource IN (0, 1, 2, 3));
```

**M3 — DML:**

```sql
-- Backfill existing PO lines as Manual (already default, but explicit for clarity)
UPDATE purchase.PurchaseOrderLines
SET PriceSource = 0
WHERE PriceSource = 0;  -- No-op, ensures column is populated
```

---

## 6. API Endpoints

### 6.1 Supplier Pricelist CRUD

| Method | Endpoint                                       | Description                                           |
| ------ | ---------------------------------------------- | ----------------------------------------------------- |
| GET    | `/api/purchase/supplier-pricelists`            | List with filters: `?productId=&vendorId=&activeOnly=` |
| GET    | `/api/purchase/supplier-pricelists/{id}`       | Get single record                                     |
| POST   | `/api/purchase/supplier-pricelists`            | Create new pricelist record                           |
| PUT    | `/api/purchase/supplier-pricelists/{id}`       | Update record (concurrency via RowVersion)            |
| DELETE | `/api/purchase/supplier-pricelists/{id}`       | Soft-delete: sets `IsActive = false`                  |

**POST/PUT Request Body:**

```json
{
  "productId": 42,
  "vendorId": 15,
  "vendorProductCode": "ACM-WDG-001",
  "vendorProductName": "Standard Widget",
  "minQuantity": 100,
  "unitPrice": 12.50,
  "currencyId": 1,
  "pricingDateBasis": 0,
  "validFrom": "2026-01-01",
  "validTo": "2026-12-31",
  "leadTimeDays": 14,
  "priority": 0,
  "notes": "Annual contract price"
}
```

**GET Response Item:**

```json
{
  "id": 87,
  "orgId": 1,
  "productId": 42,
  "productName": "Widget-X",
  "productSku": "WDG-001",
  "vendorId": 15,
  "vendorName": "Acme Corp",
  "vendorProductCode": "ACM-WDG-001",
  "vendorProductName": "Standard Widget",
  "minQuantity": 100.0000,
  "unitPrice": 12.5000,
  "currencyId": 1,
  "currencyCode": "USD",
  "pricingDateBasis": 0,
  "pricingDateBasisLabel": "Order Date",
  "validFrom": "2026-01-01",
  "validTo": "2026-12-31",
  "leadTimeDays": 14,
  "priority": 0,
  "notes": "Annual contract price",
  "isActive": true,
  "createdAt": "2026-01-15T10:30:00Z",
  "modifiedAt": "2026-01-15T10:30:00Z",
  "rowVersion": "AAAAAAAAB9M="
}
```

### 6.2 Price Resolution

| Method | Endpoint                          | Description                                   |
| ------ | --------------------------------- | --------------------------------------------- |
| POST   | `/api/purchase/resolve-price`     | Resolve price for product + vendor + qty + dates |

**Request:**

```json
{
  "productId": 42,
  "vendorId": 15,
  "quantity": 200,
  "orderDate": "2026-10-17",
  "expectedDeliveryDate": "2026-11-15"
}
```

**Response (found):**

```json
{
  "found": true,
  "unitPrice": 12.5000,
  "currencyId": 1,
  "currencyCode": "USD",
  "supplierPricelistId": 87,
  "dateBasisUsed": "OrderDate",
  "effectiveDateUsed": "2026-10-17",
  "warningMessage": null
}
```

**Response (not found):**

```json
{
  "found": false,
  "unitPrice": null,
  "currencyId": null,
  "currencyCode": null,
  "supplierPricelistId": null,
  "dateBasisUsed": null,
  "effectiveDateUsed": "2026-10-17",
  "warningMessage": "No configured price found for Widget-X from Acme Corp effective 2026-10-17. Enter price manually or create an RFQ."
}
```

---

## 7. Business Rules

| ID    | Rule                                                                                                                                                               | Enforcement            |
| ----- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------ | ---------------------- |
| BR-01 | Price resolution selects the first matching record ordered by `Priority ASC`, then `MinQuantity DESC` (highest qualifying tier at best priority)                    | Backend: Resolver      |
| BR-02 | Effective date is per-record: `PricingDateBasis = 0` → PO header Order Date; `PricingDateBasis = 1` → PO line Expected Delivery Date                              | Backend: Resolver      |
| BR-03 | If `PricingDateBasis = DeliveryDate` and no delivery date is set on the PO line, fall back to Order Date                                                           | Backend: Resolver      |
| BR-04 | When no matching price is found, the system displays a warning banner on the PO line. PO creation is **not blocked**                                               | Frontend + Backend     |
| BR-05 | The buyer can always edit the auto-filled price. Editing changes `PriceSource` from `Pricelist(1)` to `Override(2)`                                                | Frontend               |
| BR-06 | `SystemResolvedPrice` retains the original system-resolved value even after override — it is never cleared on the same line                                        | Backend                |
| BR-07 | Quantity change on a `Pricelist`-sourced line triggers automatic re-lookup and silent price update                                                                  | Frontend + Backend     |
| BR-08 | Quantity change on an `Override` or `Manual` line shows a confirmation toast; buyer chooses to keep or update                                                       | Frontend               |
| BR-09 | Delivery date change triggers re-lookup only when the matched record's `PricingDateBasis = DeliveryDate`                                                           | Frontend + Backend     |
| BR-10 | Order date (header) change triggers re-validation of all lines resolved with `PricingDateBasis = OrderDate`; summary banner with [Re-lookup All] / [Keep] options  | Frontend               |
| BR-11 | Overlapping validity date ranges for same OrgId + ProductId + VendorId + same MinQuantity produce a **save-time warning toast**, not a hard block                  | Frontend + Backend     |
| BR-12 | `ValidTo` must be ≥ `ValidFrom`                                                                                                                                    | Frontend + DB: CK      |
| BR-13 | `UnitPrice` must be > 0                                                                                                                                            | Frontend + DB: CK      |
| BR-14 | `MinQuantity` must be ≥ 0 (zero means "any quantity")                                                                                                              | Frontend + DB: CK      |
| BR-15 | Only active (`IsActive = 1`) pricelist records participate in price resolution                                                                                      | Backend: query filter  |
| BR-16 | Soft-delete: DELETE endpoint sets `IsActive = false`. Record remains for historical FK reference from PO lines                                                      | Backend                |
| BR-17 | `PriceSource = RFQ (3)` is set when price originates from a completed RFQ. RFQ-sourced prices are **not re-looked-up** on qty/date changes                         | Backend                |
| BR-18 | Existing PO lines (pre-migration) receive `PriceSource = Manual (0)` with null `SystemResolvedPrice`                                                               | Migration M3           |
| BR-19 | Price resolution is a **read-only query** — no locks, no contention. Multiple buyers can resolve prices concurrently for the same product without blocking          | Backend: design        |

---

## 8. Test Scenarios

### 8.1 Price Resolution — Core

| ID    | Scenario                                                                          | Expected Result                                                           | Priority |
| ----- | --------------------------------------------------------------------------------- | ------------------------------------------------------------------------- | -------- |
| TS-01 | Add PO line: valid pricelist exists (OrderDate basis, date in range, qty ≥ min)   | Price auto-filled, `PriceSource = Pricelist`, pricelist badge shown       | P0       |
| TS-02 | Add PO line: no pricelist record for product + vendor                              | Warning banner, price empty, [Enter Manually] and [Create RFQ] shown     | P0       |
| TS-03 | Add PO line: pricelist exists but PO order date is outside validity window         | Warning banner, same as TS-02                                             | P0       |
| TS-04 | Add PO line: pricelist exists but order qty < MinQuantity on all records           | Warning banner, same as TS-02                                             | P0       |
| TS-05 | Add PO line: pricelist with `PricingDateBasis = DeliveryDate`, delivery date in range | Price resolved using delivery date                                       | P0       |
| TS-06 | Add PO line: `PricingDateBasis = DeliveryDate` but no delivery date on PO line    | Falls back to order date for resolution                                   | P1       |

### 8.2 Priority & Quantity Breaks

| ID    | Scenario                                                                          | Expected Result                                                           | Priority |
| ----- | --------------------------------------------------------------------------------- | ------------------------------------------------------------------------- | -------- |
| TS-07 | Multiple records with different Priority values, all matching                     | Lowest Priority number selected                                           | P0       |
| TS-08 | Same vendor, same validity, qty breaks: MinQty=1 ($15), MinQty=100 ($12.50)       | Order qty 50 → $15.00; Order qty 200 → $12.50                            | P0       |
| TS-09 | Same vendor, same validity, MinQty=1 ($15), MinQty=100 ($12.50), MinQty=500 ($10) | Order qty 500 → $10.00; Order qty 150 → $12.50                           | P0       |

### 8.3 Override & Manual Entry

| ID    | Scenario                                                                          | Expected Result                                                           | Priority |
| ----- | --------------------------------------------------------------------------------- | ------------------------------------------------------------------------- | -------- |
| TS-10 | Auto-filled price → buyer edits it                                                | `PriceSource` → Override, `SystemResolvedPrice` preserved                 | P0       |
| TS-11 | Override with reason text                                                          | `PriceOverrideReason` saved                                               | P1       |
| TS-12 | No price found → buyer clicks [Enter Manually] → types price                      | `PriceSource = Manual`, price saved, warning dismissed                    | P0       |
| TS-13 | No price found → buyer clicks [Create RFQ]                                        | Navigate to RFQ screen pre-filled with vendor + product + qty             | P1       |

### 8.4 Re-lookup on Changes

| ID    | Scenario                                                                          | Expected Result                                                           | Priority |
| ----- | --------------------------------------------------------------------------------- | ------------------------------------------------------------------------- | -------- |
| TS-14 | Change qty on Pricelist-sourced line → new qty hits different tier                 | Price auto-updated silently to new tier                                    | P0       |
| TS-15 | Change qty on Override-sourced line → different tier available                     | Toast: "System price is $X. Keep $Y?" — [Keep] / [Update]                 | P1       |
| TS-16 | Change qty on Manual-sourced line → configured price now available                 | Toast: "Configured price of $X available." — [Keep] / [Apply]             | P1       |
| TS-17 | Change delivery date on line with DeliveryDate-basis record                        | Price re-lookup triggered                                                  | P1       |
| TS-18 | Change delivery date on line with OrderDate-basis record                           | No re-lookup (delivery date is irrelevant for this record)                 | P1       |
| TS-19 | Change PO header order date → 3 lines with OrderDate-basis prices                 | Summary banner: "3 lines may have different prices" — [Re-lookup] / [Keep] | P1       |
| TS-20 | Change qty on RFQ-sourced line                                                     | No re-lookup; RFQ price is contractual                                     | P1       |

### 8.5 Pricelist Management

| ID    | Scenario                                                                          | Expected Result                                                           | Priority |
| ----- | --------------------------------------------------------------------------------- | ------------------------------------------------------------------------- | -------- |
| TS-21 | Save pricelist with overlapping dates for same vendor + product + MinQty           | Warning toast displayed, save allowed                                      | P1       |
| TS-22 | Save pricelist with `ValidTo < ValidFrom`                                          | Validation error, save blocked                                             | P0       |
| TS-23 | Save pricelist with `UnitPrice = 0` or negative                                    | Validation error, save blocked                                             | P0       |
| TS-24 | Deactivate pricelist record → add PO line for same product + vendor                | Deactivated record skipped, warning shown if no other match                | P0       |
| TS-25 | View PO line that references a soft-deleted pricelist via `SupplierPricelistId` FK | FK still valid, historical price source visible                            | P2       |
| TS-26 | Two buyers create POs simultaneously for same product + vendor                     | Both resolve independently, no contention (BR-19)                          | P1       |

---

## 9. Feature Coverage Matrix

| Feature                    | Entity | Migration | API  | Service              | UI  | Rules                        | Tests                     |
| -------------------------- | ------ | --------- | ---- | -------------------- | --- | ---------------------------- | ------------------------- |
| Supplier Pricelist CRUD    | C1     | M1        | 6.1  | —                    | C4  | BR-12, 13, 14, 15, 16       | TS-21, 22, 23, 24, 25    |
| PO Line Price Source       | C2     | M2, M3    | —    | —                    | C5  | BR-05, 06, 18               | TS-10, 11, 12             |
| Price Resolution           | C3     | —         | 6.2  | IPurchasePriceResolver| C5  | BR-01, 02, 03, 04, 19       | TS-01–09                  |
| Qty/Date Re-lookup         | C7     | —         | 6.2  | IPurchasePriceResolver| C5  | BR-07, 08, 09, 10, 17       | TS-14–20                  |
| Overlap Validation         | C6     | —         | —    | —                    | C4  | BR-11                        | TS-21                     |
| RFQ Redirect               | C5     | —         | —    | —                    | C5  | BR-17                        | TS-13                     |

---

## 10. Open Items / Future Considerations

| #  | Item                         | Description                                                                                                    | Target                           |
| -- | ---------------------------- | -------------------------------------------------------------------------------------------------------------- | -------------------------------- |
| 1  | Multi-Currency Conversion    | When vendor pricelist currency ≠ PO currency, exchange rate conversion at effective date                        | Multi-Currency Addendum          |
| 2  | Purchase Agreements          | Blanket Order entity overriding pricelist prices for contracted periods/quantities (highest resolution priority) | Future Purchase Addendum         |
| 3  | Import / Bulk Upload         | CSV/Excel import for supplier pricelists (bulk vendor price updates)                                            | Future enhancement               |
| 4  | Price History Report         | Historical prices per vendor + product for trend analysis and negotiation support                               | Future enhancement               |
| 5  | Auto-Update from PO          | Option to update pricelist from confirmed PO price (like Odoo's `purchase_order_supplierinfo_update` module)    | Future enhancement               |
| 6  | Three-Way Match Integration  | At Vendor Bill, compare bill price vs. PO price (from pricelist or override) with configurable variance %       | AR/AP Addendum                   |
| 7  | Addendum 44 Integration      | `PriceSource` and `SystemResolvedPrice` available in Financial Posting Gateway for COGS audit trail             | When both addendums are approved |

---

*End of Addendum 45*
