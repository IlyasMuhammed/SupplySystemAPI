# SMS Task Register — Addendum 45
## Supplier Pricelist & Purchase Order Auto-Pricing

| Field           | Value                              |
| --------------- | ---------------------------------- |
| Document ID     | SMS-FSD-ADD-045                    |
| Addendum        | 45 — Supplier Pricelist & PO Auto-Pricing |
| Module          | SMS.Purchase                       |
| Schema          | `purchase`                         |
| Total Tasks     | 228                                |
| Generated       | 2026-10-17                         |

### Status Legend

| Symbol | Meaning                |
| ------ | ---------------------- |
| `[ ]`  | Pending                |
| `[~]`  | In Progress            |
| `[✓]`  | Completed              |
| `[—]`  | Skipped / Not Applicable |

### Priority Legend

| Priority | Meaning                            |
| -------- | ---------------------------------- |
| P0       | Must have — blocks release         |
| P1       | Should have — expected for release |
| P2       | Nice to have — can follow          |
| P3       | Future — tracked only              |

---

## Phase 1 — Database & Entity Foundation (Week 1–2)
**Change Items:** C1, C2 | **Migrations:** M1, M2, M3

### 1.1 Migration M1 — Create `purchase.SupplierPricelists` Table

| ID        | Task                                                                 | Priority | Status | Change |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0001  | Create EF Core migration file for `purchase.SupplierPricelists`      | P0       | `[ ]`  | C1/M1  |
| A45-0002  | Add column `Id` (int, IDENTITY, PK)                                  | P0       | `[ ]`  | C1/M1  |
| A45-0003  | Add column `OrgId` (int, NOT NULL)                                   | P0       | `[ ]`  | C1/M1  |
| A45-0004  | Add column `ProductId` (int, NOT NULL)                               | P0       | `[ ]`  | C1/M1  |
| A45-0005  | Add column `VendorId` (int, NOT NULL)                                | P0       | `[ ]`  | C1/M1  |
| A45-0006  | Add column `VendorProductCode` (nvarchar(100), NULL)                 | P0       | `[ ]`  | C1/M1  |
| A45-0007  | Add column `VendorProductName` (nvarchar(255), NULL)                 | P0       | `[ ]`  | C1/M1  |
| A45-0008  | Add column `MinQuantity` (decimal(18,4), NOT NULL, DEFAULT 1.0000)   | P0       | `[ ]`  | C1/M1  |
| A45-0009  | Add column `UnitPrice` (decimal(18,4), NOT NULL)                     | P0       | `[ ]`  | C1/M1  |
| A45-0010  | Add column `CurrencyId` (int, NOT NULL)                              | P0       | `[ ]`  | C1/M1  |
| A45-0011  | Add column `PricingDateBasis` (tinyint, NOT NULL, DEFAULT 0)         | P0       | `[ ]`  | C1/M1  |
| A45-0012  | Add column `ValidFrom` (date, NOT NULL)                              | P0       | `[ ]`  | C1/M1  |
| A45-0013  | Add column `ValidTo` (date, NOT NULL)                                | P0       | `[ ]`  | C1/M1  |
| A45-0014  | Add column `LeadTimeDays` (int, NULL)                                | P0       | `[ ]`  | C1/M1  |
| A45-0015  | Add column `Priority` (int, NOT NULL, DEFAULT 0)                     | P0       | `[ ]`  | C1/M1  |
| A45-0016  | Add column `Notes` (nvarchar(500), NULL)                             | P1       | `[ ]`  | C1/M1  |
| A45-0017  | Add column `IsActive` (bit, NOT NULL, DEFAULT 1)                     | P0       | `[ ]`  | C1/M1  |
| A45-0018  | Add column `CreatedBy` (int, NOT NULL)                               | P0       | `[ ]`  | C1/M1  |
| A45-0019  | Add column `CreatedAt` (datetime2(7), NOT NULL, DEFAULT SYSUTCDATETIME()) | P0  | `[ ]`  | C1/M1  |
| A45-0020  | Add column `ModifiedBy` (int, NOT NULL)                              | P0       | `[ ]`  | C1/M1  |
| A45-0021  | Add column `ModifiedAt` (datetime2(7), NOT NULL, DEFAULT SYSUTCDATETIME()) | P0 | `[ ]`  | C1/M1  |
| A45-0022  | Add column `RowVersion` (rowversion, NOT NULL)                       | P0       | `[ ]`  | C1/M1  |

### 1.2 Migration M1 — Foreign Keys

| ID        | Task                                                                 | Priority | Status | Change |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0023  | Add FK `FK_SupplierPricelists_Org` → `core.Organizations(Id)`       | P0       | `[ ]`  | C1/M1  |
| A45-0024  | Add FK `FK_SupplierPricelists_Product` → `inventory.Products(Id)`   | P0       | `[ ]`  | C1/M1  |
| A45-0025  | Add FK `FK_SupplierPricelists_Vendor` → `core.BusinessPartners(Id)` | P0       | `[ ]`  | C1/M1  |
| A45-0026  | Add FK `FK_SupplierPricelists_Currency` → `core.Currencies(Id)`     | P0       | `[ ]`  | C1/M1  |

### 1.3 Migration M1 — Check Constraints

| ID        | Task                                                                 | Priority | Status | Change |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0027  | Add CK `CK_SupplierPricelists_Price` — `UnitPrice > 0`             | P0       | `[ ]`  | C1/M1  |
| A45-0028  | Add CK `CK_SupplierPricelists_MinQty` — `MinQuantity >= 0`         | P0       | `[ ]`  | C1/M1  |
| A45-0029  | Add CK `CK_SupplierPricelists_Dates` — `ValidTo >= ValidFrom`      | P0       | `[ ]`  | C1/M1  |
| A45-0030  | Add CK `CK_SupplierPricelists_DateBasis` — `PricingDateBasis IN (0, 1)` | P0  | `[ ]`  | C1/M1  |

### 1.4 Migration M1 — Indexes

| ID        | Task                                                                 | Priority | Status | Change |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0031  | Create index `IX_SupplierPricelists_Lookup` (OrgId, ProductId, VendorId, IsActive, ValidFrom, ValidTo) INCLUDE (UnitPrice, MinQuantity, Priority, PricingDateBasis, CurrencyId) | P0 | `[ ]` | C1/M1 |
| A45-0032  | Create index `IX_SupplierPricelists_Vendor` (OrgId, VendorId, IsActive) INCLUDE (ProductId) | P0 | `[ ]` | C1/M1 |
| A45-0033  | Create filtered unique index `UQ_SupplierPricelists_NoDuplicate` (OrgId, ProductId, VendorId, MinQuantity, ValidFrom, ValidTo) WHERE IsActive = 1 | P0 | `[ ]` | C1/M1 |

### 1.5 Migration M2 — Alter `purchase.PurchaseOrderLines`

| ID        | Task                                                                 | Priority | Status | Change |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0034  | Create EF Core migration file for PurchaseOrderLines alterations     | P0       | `[ ]`  | C2/M2  |
| A45-0035  | Add column `PriceSource` (tinyint, NOT NULL, DEFAULT 0)              | P0       | `[ ]`  | C2/M2  |
| A45-0036  | Add column `SystemResolvedPrice` (decimal(18,4), NULL)               | P0       | `[ ]`  | C2/M2  |
| A45-0037  | Add column `SupplierPricelistId` (int, NULL)                         | P0       | `[ ]`  | C2/M2  |
| A45-0038  | Add column `PriceOverrideReason` (nvarchar(500), NULL)               | P1       | `[ ]`  | C2/M2  |
| A45-0039  | Add FK `FK_POLines_SupplierPricelist` → `purchase.SupplierPricelists(Id)` | P0  | `[ ]`  | C2/M2  |
| A45-0040  | Add CK `CK_POLines_PriceSource` — `PriceSource IN (0, 1, 2, 3)`    | P0       | `[ ]`  | C2/M2  |

### 1.6 Migration M3 — Backfill Existing PO Lines

| ID        | Task                                                                 | Priority | Status | Change |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0041  | Write DML to backfill `PriceSource = 0` for all existing PO lines   | P0       | `[ ]`  | M3     |
| A45-0042  | Verify backfill does not lock table for extended period (batch if >100K rows) | P1 | `[ ]` | M3   |

### 1.7 EF Core Entity — SupplierPricelist

| ID        | Task                                                                 | Priority | Status | Change |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0043  | Create `SupplierPricelist` entity class in SMS.Purchase.Domain       | P0       | `[ ]`  | C1     |
| A45-0044  | Add all scalar properties matching table columns                     | P0       | `[ ]`  | C1     |
| A45-0045  | Add navigation property `Organization`                               | P0       | `[ ]`  | C1     |
| A45-0046  | Add navigation property `Product`                                    | P0       | `[ ]`  | C1     |
| A45-0047  | Add navigation property `Vendor` (BusinessPartner)                   | P0       | `[ ]`  | C1     |
| A45-0048  | Add navigation property `Currency`                                   | P0       | `[ ]`  | C1     |
| A45-0049  | Create `SupplierPricelistConfiguration` (IEntityTypeConfiguration)   | P0       | `[ ]`  | C1     |
| A45-0050  | Configure table name `purchase.SupplierPricelists`                   | P0       | `[ ]`  | C1     |
| A45-0051  | Configure PK, identity, column types, defaults                       | P0       | `[ ]`  | C1     |
| A45-0052  | Configure FK relationships with delete behavior                      | P0       | `[ ]`  | C1     |
| A45-0053  | Configure RowVersion as concurrency token                            | P0       | `[ ]`  | C1     |
| A45-0054  | Configure query filter for multi-tenant `OrgId`                      | P0       | `[ ]`  | C1     |
| A45-0055  | Register configuration in `PurchaseDbContext`                        | P0       | `[ ]`  | C1     |

### 1.8 EF Core — PurchaseOrderLine Entity Updates

| ID        | Task                                                                 | Priority | Status | Change |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0056  | Add `PriceSource` property to `PurchaseOrderLine` entity             | P0       | `[ ]`  | C2     |
| A45-0057  | Add `SystemResolvedPrice` property                                   | P0       | `[ ]`  | C2     |
| A45-0058  | Add `SupplierPricelistId` property                                   | P0       | `[ ]`  | C2     |
| A45-0059  | Add `PriceOverrideReason` property                                   | P1       | `[ ]`  | C2     |
| A45-0060  | Add navigation property `SupplierPricelist`                          | P0       | `[ ]`  | C2     |
| A45-0061  | Update `PurchaseOrderLineConfiguration` with new columns and FK      | P0       | `[ ]`  | C2     |

### 1.9 Enums

| ID        | Task                                                                 | Priority | Status | Change |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0062  | Create `PriceSource` enum (Manual=0, Pricelist=1, Override=2, RFQ=3) | P0       | `[ ]`  | C2     |
| A45-0063  | Create `PricingDateBasis` enum (OrderDate=0, DeliveryDate=1)         | P0       | `[ ]`  | C1     |

### 1.10 Migration Verification

| ID        | Task                                                                 | Priority | Status | Change |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0064  | Run M1 migration against dev database — verify table created         | P0       | `[ ]`  | M1     |
| A45-0065  | Run M2 migration against dev database — verify columns added         | P0       | `[ ]`  | M2     |
| A45-0066  | Run M3 migration — verify backfill completed                         | P0       | `[ ]`  | M3     |
| A45-0067  | Verify all FKs resolve correctly with existing reference data        | P0       | `[ ]`  | M1/M2  |
| A45-0068  | Verify check constraints reject invalid data (negative price, bad dates) | P0   | `[ ]`  | M1     |
| A45-0069  | Verify filtered unique index prevents duplicate active records       | P0       | `[ ]`  | M1     |
| A45-0070  | Test migration rollback (DROP TABLE, DROP COLUMN)                    | P1       | `[ ]`  | M1/M2  |

**Phase 1 Total: 70 tasks**

---

## Phase 2 — Price Resolution Service (Week 2–3)
**Change Item:** C3

### 2.1 DTOs

| ID        | Task                                                                 | Priority | Status | Change |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0071  | Create `PriceResolutionRequest` DTO                                  | P0       | `[ ]`  | C3     |
| A45-0072  | Add properties: OrgId, ProductId, VendorId, Quantity, OrderDate, ExpectedDeliveryDate | P0 | `[ ]` | C3 |
| A45-0073  | Create `PriceResolutionResult` DTO                                   | P0       | `[ ]`  | C3     |
| A45-0074  | Add properties: Found, UnitPrice, CurrencyId, SupplierPricelistId, DateBasisUsed, EffectiveDateUsed, WarningMessage | P0 | `[ ]` | C3 |
| A45-0075  | Add FluentValidation rules for `PriceResolutionRequest` (ProductId > 0, VendorId > 0, Quantity > 0) | P0 | `[ ]` | C3 |

### 2.2 Service Interface & Implementation

| ID        | Task                                                                 | Priority | Status | Change |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0076  | Create `IPurchasePriceResolver` interface                            | P0       | `[ ]`  | C3     |
| A45-0077  | Create `PurchasePriceResolver` implementation class                  | P0       | `[ ]`  | C3     |
| A45-0078  | Register `IPurchasePriceResolver` in DI container (scoped)           | P0       | `[ ]`  | C3     |
| A45-0079  | Implement core query: filter by OrgId, ProductId, VendorId, IsActive, MinQuantity ≤ Quantity | P0 | `[ ]` | C3 |
| A45-0080  | Implement PricingDateBasis=0 (OrderDate) branch: check OrderDate BETWEEN ValidFrom AND ValidTo | P0 | `[ ]` | C3 |
| A45-0081  | Implement PricingDateBasis=1 (DeliveryDate) branch: check ExpectedDeliveryDate BETWEEN ValidFrom AND ValidTo | P0 | `[ ]` | C3 |
| A45-0082  | Implement DeliveryDate fallback: if ExpectedDeliveryDate is null, fall back to OrderDate (BR-03) | P0 | `[ ]` | C3 |
| A45-0083  | Implement ordering: Priority ASC, MinQuantity DESC (BR-01)           | P0       | `[ ]`  | C3     |
| A45-0084  | Implement TOP 1 / FirstOrDefault — return first match                | P0       | `[ ]`  | C3     |
| A45-0085  | Implement not-found path: return Found=false with formatted WarningMessage including ProductName and VendorName | P0 | `[ ]` | C3 |
| A45-0086  | Ensure query uses covering index `IX_SupplierPricelists_Lookup` — verify query plan | P1 | `[ ]` | C3 |
| A45-0087  | Ensure no locks acquired (read-only query, BR-19)                    | P0       | `[ ]`  | C3     |

### 2.3 Service Unit Tests

| ID        | Task                                                                 | Priority | Status | Change |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0088  | Unit test: valid match with OrderDate basis → returns Found=true with correct price | P0 | `[ ]` | C3 |
| A45-0089  | Unit test: valid match with DeliveryDate basis → returns Found=true using delivery date | P0 | `[ ]` | C3 |
| A45-0090  | Unit test: DeliveryDate basis but null delivery date → falls back to OrderDate | P0 | `[ ]` | C3 |
| A45-0091  | Unit test: no matching records → returns Found=false with WarningMessage | P0 | `[ ]` | C3 |
| A45-0092  | Unit test: date outside validity window → not matched                | P0       | `[ ]`  | C3     |
| A45-0093  | Unit test: qty below all MinQuantity thresholds → not matched        | P0       | `[ ]`  | C3     |
| A45-0094  | Unit test: multiple records, different Priority → lowest Priority wins | P0      | `[ ]`  | C3     |
| A45-0095  | Unit test: same Priority, different MinQuantity → highest qualifying MinQuantity wins | P0 | `[ ]` | C3 |
| A45-0096  | Unit test: quantity breaks — qty 50 gets tier-1 price, qty 200 gets tier-2 price | P0 | `[ ]` | C3 |
| A45-0097  | Unit test: three quantity tiers — correct tier selected for each boundary | P0   | `[ ]`  | C3     |
| A45-0098  | Unit test: inactive record (IsActive=0) is skipped                   | P0       | `[ ]`  | C3     |
| A45-0099  | Unit test: concurrent resolution for same product — no contention    | P1       | `[ ]`  | C3     |

**Phase 2 Total: 29 tasks**

---

## Phase 3 — API Layer (Week 3–4)
**Endpoints:** 6.1 (CRUD), 6.2 (Resolve)

### 3.1 Supplier Pricelist CRUD — Controller

| ID        | Task                                                                 | Priority | Status | Change |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0100  | Create `SupplierPricelistsController` in SMS.Purchase.Api            | P0       | `[ ]`  | C4     |
| A45-0101  | Add `[Authorize]` and `[ApiController]` attributes                   | P0       | `[ ]`  | C4     |
| A45-0102  | Apply multi-tenant OrgId filter via middleware/base controller        | P0       | `[ ]`  | C4     |

### 3.2 GET — List with Filters

| ID        | Task                                                                 | Priority | Status | Change |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0103  | Implement `GET /api/purchase/supplier-pricelists`                    | P0       | `[ ]`  | 6.1    |
| A45-0104  | Add query parameter `productId` (optional filter)                    | P0       | `[ ]`  | 6.1    |
| A45-0105  | Add query parameter `vendorId` (optional filter)                     | P0       | `[ ]`  | 6.1    |
| A45-0106  | Add query parameter `activeOnly` (default: true)                     | P0       | `[ ]`  | 6.1    |
| A45-0107  | Add pagination support (page, pageSize)                              | P1       | `[ ]`  | 6.1    |
| A45-0108  | Map entity to response DTO with joined ProductName, ProductSku, VendorName, CurrencyCode | P0 | `[ ]` | 6.1 |
| A45-0109  | Map `PricingDateBasis` enum to `PricingDateBasisLabel` string        | P0       | `[ ]`  | 6.1    |

### 3.3 GET — Single Record

| ID        | Task                                                                 | Priority | Status | Change |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0110  | Implement `GET /api/purchase/supplier-pricelists/{id}`               | P0       | `[ ]`  | 6.1    |
| A45-0111  | Return 404 if record not found or belongs to different OrgId         | P0       | `[ ]`  | 6.1    |

### 3.4 POST — Create

| ID        | Task                                                                 | Priority | Status | Change |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0112  | Implement `POST /api/purchase/supplier-pricelists`                   | P0       | `[ ]`  | 6.1    |
| A45-0113  | Validate required fields: ProductId, VendorId, UnitPrice, CurrencyId, ValidFrom, ValidTo | P0 | `[ ]` | 6.1 |
| A45-0114  | Validate UnitPrice > 0 (BR-13)                                      | P0       | `[ ]`  | 6.1    |
| A45-0115  | Validate MinQuantity ≥ 0 (BR-14)                                    | P0       | `[ ]`  | 6.1    |
| A45-0116  | Validate ValidTo ≥ ValidFrom (BR-12)                                | P0       | `[ ]`  | 6.1    |
| A45-0117  | Validate ProductId exists in `inventory.Products`                    | P0       | `[ ]`  | 6.1    |
| A45-0118  | Validate VendorId exists in `core.BusinessPartners`                  | P0       | `[ ]`  | 6.1    |
| A45-0119  | Validate CurrencyId exists in `core.Currencies`                     | P0       | `[ ]`  | 6.1    |
| A45-0120  | Run overlap detection query (C6) — return warning in response if overlap found | P1 | `[ ]` | 6.1/C6 |
| A45-0121  | Set `CreatedBy`, `CreatedAt`, `ModifiedBy`, `ModifiedAt` from current user/time | P0 | `[ ]` | 6.1 |
| A45-0122  | Return 201 Created with new record                                   | P0       | `[ ]`  | 6.1    |

### 3.5 PUT — Update

| ID        | Task                                                                 | Priority | Status | Change |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0123  | Implement `PUT /api/purchase/supplier-pricelists/{id}`               | P0       | `[ ]`  | 6.1    |
| A45-0124  | Validate RowVersion for optimistic concurrency                       | P0       | `[ ]`  | 6.1    |
| A45-0125  | Apply same field validations as POST                                 | P0       | `[ ]`  | 6.1    |
| A45-0126  | Run overlap detection on update (C6)                                 | P1       | `[ ]`  | 6.1/C6 |
| A45-0127  | Update `ModifiedBy`, `ModifiedAt`                                    | P0       | `[ ]`  | 6.1    |
| A45-0128  | Return 409 Conflict on RowVersion mismatch                          | P0       | `[ ]`  | 6.1    |

### 3.6 DELETE — Soft Delete

| ID        | Task                                                                 | Priority | Status | Change |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0129  | Implement `DELETE /api/purchase/supplier-pricelists/{id}`            | P0       | `[ ]`  | 6.1    |
| A45-0130  | Set `IsActive = false` (soft delete, BR-16)                         | P0       | `[ ]`  | 6.1    |
| A45-0131  | Return 404 if already inactive or not found                          | P0       | `[ ]`  | 6.1    |

### 3.7 Price Resolution Endpoint

| ID        | Task                                                                 | Priority | Status | Change |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0132  | Implement `POST /api/purchase/resolve-price`                         | P0       | `[ ]`  | 6.2    |
| A45-0133  | Validate request: ProductId > 0, VendorId > 0, Quantity > 0, OrderDate not null | P0 | `[ ]` | 6.2 |
| A45-0134  | Call `IPurchasePriceResolver.ResolveAsync()` and return result       | P0       | `[ ]`  | 6.2    |
| A45-0135  | Include CurrencyCode in response (join from Currencies)              | P0       | `[ ]`  | 6.2    |
| A45-0136  | Return 200 OK for both found and not-found (not-found is not an error) | P0     | `[ ]`  | 6.2    |

### 3.8 API Integration Tests

| ID        | Task                                                                 | Priority | Status | Change |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0137  | Integration test: POST create pricelist → 201 with all fields        | P0       | `[ ]`  | 6.1    |
| A45-0138  | Integration test: POST with UnitPrice=0 → 400 validation error      | P0       | `[ ]`  | 6.1    |
| A45-0139  | Integration test: POST with ValidTo < ValidFrom → 400               | P0       | `[ ]`  | 6.1    |
| A45-0140  | Integration test: GET list filtered by productId                     | P0       | `[ ]`  | 6.1    |
| A45-0141  | Integration test: GET list filtered by vendorId                      | P0       | `[ ]`  | 6.1    |
| A45-0142  | Integration test: GET list activeOnly=false includes inactive        | P1       | `[ ]`  | 6.1    |
| A45-0143  | Integration test: PUT with stale RowVersion → 409                    | P0       | `[ ]`  | 6.1    |
| A45-0144  | Integration test: DELETE → IsActive=false, record still queryable    | P0       | `[ ]`  | 6.1    |
| A45-0145  | Integration test: resolve-price with valid match → found=true        | P0       | `[ ]`  | 6.2    |
| A45-0146  | Integration test: resolve-price with no match → found=false + warning | P0      | `[ ]`  | 6.2    |
| A45-0147  | Integration test: resolve-price multi-tenant isolation — Org A cannot resolve Org B prices | P0 | `[ ]` | 6.2 |
| A45-0148  | Integration test: POST create with overlapping dates → 201 with overlap warning | P1 | `[ ]` | 6.1/C6 |

**Phase 3 Total: 49 tasks**

---

## Phase 4 — Supplier Pricelist Management UI (Week 4–5)
**Change Items:** C4, C6

### 4.1 Product Detail — Purchase Tab

| ID        | Task                                                                 | Priority | Status | Change |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0149  | Add "Purchase" tab to Product Detail screen                          | P0       | `[ ]`  | C4     |
| A45-0150  | Create Vendor Prices data grid component                             | P0       | `[ ]`  | C4     |
| A45-0151  | Load pricelist records filtered by ProductId on tab mount            | P0       | `[ ]`  | C4     |
| A45-0152  | Display columns: Vendor, Min Qty, Price, Currency, Valid Period, Date Basis, Priority, Active | P0 | `[ ]` | C4 |
| A45-0153  | "[+ Add Price]" button — opens inline add row or modal               | P0       | `[ ]`  | C4     |
| A45-0154  | Inline edit support for all editable columns                         | P1       | `[ ]`  | C4     |
| A45-0155  | Vendor column: searchable dropdown (BusinessPartners where IsVendor) | P0       | `[ ]`  | C4     |
| A45-0156  | Currency column: dropdown populated from `core.Currencies`, default org base currency | P0 | `[ ]` | C4 |
| A45-0157  | Pricing Date Basis column: dropdown — "Order Date" / "Delivery Date" | P0       | `[ ]`  | C4     |
| A45-0158  | Valid From / Valid To: date pickers with ValidTo ≥ ValidFrom validation | P0     | `[ ]`  | C4     |
| A45-0159  | Active column: toggle switch                                         | P0       | `[ ]`  | C4     |
| A45-0160  | Sort grid by Priority ASC, then ValidFrom DESC by default            | P1       | `[ ]`  | C4     |
| A45-0161  | Info text: "Prices resolved by priority. Lowest number wins."        | P1       | `[ ]`  | C4     |

### 4.2 Vendor Detail — Products Tab

| ID        | Task                                                                 | Priority | Status | Change |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0162  | Add Vendor Prices grid to Vendor Detail → Products Tab               | P1       | `[ ]`  | C4     |
| A45-0163  | Load pricelist records filtered by VendorId                          | P1       | `[ ]`  | C4     |
| A45-0164  | Product column: searchable dropdown (replaces Vendor column from Product view) | P1 | `[ ]` | C4 |
| A45-0165  | Same grid behavior as Product Purchase Tab (columns, edit, validation) | P1     | `[ ]`  | C4     |

### 4.3 Standalone List View

| ID        | Task                                                                 | Priority | Status | Change |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0166  | Create standalone route: Purchase → Supplier Pricelists              | P1       | `[ ]`  | C4     |
| A45-0167  | Add filter bar: Product search, Vendor search, Active/All toggle     | P1       | `[ ]`  | C4     |
| A45-0168  | Display all columns including Product and Vendor                     | P1       | `[ ]`  | C4     |
| A45-0169  | Pagination support                                                   | P1       | `[ ]`  | C4     |
| A45-0170  | Click row → navigate to edit or expand inline                        | P1       | `[ ]`  | C4     |

### 4.4 Validation & Overlap Warning (C6)

| ID        | Task                                                                 | Priority | Status | Change |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0171  | Frontend validation: UnitPrice > 0 — show error on blur              | P0       | `[ ]`  | C4     |
| A45-0172  | Frontend validation: MinQuantity ≥ 0 — show error on blur            | P0       | `[ ]`  | C4     |
| A45-0173  | Frontend validation: ValidTo ≥ ValidFrom — show error on date change | P0       | `[ ]`  | C4     |
| A45-0174  | On save: display overlap warning toast if API response includes overlap flag (BR-11) | P1 | `[ ]` | C6 |
| A45-0175  | Overlap toast message: show conflicting record's Priority and date range | P1    | `[ ]`  | C6     |
| A45-0176  | Overlap is warning only — save proceeds regardless                   | P1       | `[ ]`  | C6     |

### 4.5 Delete / Deactivate

| ID        | Task                                                                 | Priority | Status | Change |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0177  | Add "Deactivate" action button per row (or context menu)             | P0       | `[ ]`  | C4     |
| A45-0178  | Confirmation dialog: "Deactivate this price record?"                 | P0       | `[ ]`  | C4     |
| A45-0179  | Call DELETE endpoint → update grid (remove row or show inactive state) | P0      | `[ ]`  | C4     |

**Phase 4 Total: 31 tasks**

---

## Phase 5 — PO Line Auto-Pricing & Re-lookup (Week 5–6)
**Change Items:** C5, C7

### 5.1 Auto-Pricing Trigger

| ID        | Task                                                                 | Priority | Status | Change |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0180  | Hook PO line product change → call resolve-price API                 | P0       | `[ ]`  | C5     |
| A45-0181  | Hook PO line quantity change → conditional re-lookup (see 5.4)       | P0       | `[ ]`  | C5/C7  |
| A45-0182  | Hook PO line delivery date change → conditional re-lookup (see 5.5) | P0       | `[ ]`  | C5/C7  |
| A45-0183  | Pass OrderDate from PO header + ExpectedDeliveryDate from PO line to resolver | P0 | `[ ]` | C5 |
| A45-0184  | On found: auto-fill UnitPrice, set PriceSource=Pricelist, store SystemResolvedPrice and SupplierPricelistId | P0 | `[ ]` | C5 |

### 5.2 Warning Banner — No Price Found

| ID        | Task                                                                 | Priority | Status | Change |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0185  | Create warning banner component for PO line                          | P0       | `[ ]`  | C5     |
| A45-0186  | Display formatted warning message from resolver response             | P0       | `[ ]`  | C5     |
| A45-0187  | "[Enter Manually]" button: dismiss banner, focus price field, set PriceSource=Manual | P0 | `[ ]` | C5 |
| A45-0188  | "[Create RFQ]" button: navigate to RFQ screen pre-filled with Vendor + Product + Qty | P1 | `[ ]` | C5 |
| A45-0189  | RFQ redirect: pass vendor, product, and quantity as query parameters or route state | P1 | `[ ]` | C5 |
| A45-0190  | Banner auto-dismisses when price field receives a valid value        | P0       | `[ ]`  | C5     |

### 5.3 Price Source Badge & Override Flow

| ID        | Task                                                                 | Priority | Status | Change |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0191  | Create PriceSource badge component (Pricelist / Override / Manual / RFQ) | P0    | `[ ]`  | C5     |
| A45-0192  | Display badge next to price field on PO line                         | P0       | `[ ]`  | C5     |
| A45-0193  | Price field remains editable when PriceSource=Pricelist              | P0       | `[ ]`  | C5     |
| A45-0194  | Detect price change on Pricelist-sourced line → change PriceSource to Override (BR-05) | P0 | `[ ]` | C5 |
| A45-0195  | On Override: preserve SystemResolvedPrice (do not clear) (BR-06)     | P0       | `[ ]`  | C5     |
| A45-0196  | On Override: show optional "Override Reason" text field              | P1       | `[ ]`  | C5     |
| A45-0197  | Save PriceOverrideReason to PO line on PO save                      | P1       | `[ ]`  | C5     |

### 5.4 Quantity Change Re-lookup (C7)

| ID        | Task                                                                 | Priority | Status | Change |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0198  | On qty change + PriceSource=Pricelist → silent re-lookup, auto-update price (BR-07) | P0 | `[ ]` | C7 |
| A45-0199  | On qty change + PriceSource=Override → show toast: "System price for qty {X} is {Y}. Keep your price of {Z}?" with [Keep] / [Update] (BR-08) | P1 | `[ ]` | C7 |
| A45-0200  | On qty change + PriceSource=Manual → show toast: "A configured price of {Y} is available for qty {X}." with [Keep] / [Apply] (BR-08) | P1 | `[ ]` | C7 |
| A45-0201  | On qty change + PriceSource=RFQ → no re-lookup (BR-17)              | P0       | `[ ]`  | C7     |
| A45-0202  | [Update] / [Apply] action: update price, change PriceSource to Pricelist | P1    | `[ ]`  | C7     |
| A45-0203  | [Keep] action: dismiss toast, retain current price and PriceSource   | P1       | `[ ]`  | C7     |

### 5.5 Delivery Date Change Re-lookup (C7)

| ID        | Task                                                                 | Priority | Status | Change |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0204  | On delivery date change: check if current PricelistId record has PricingDateBasis=DeliveryDate | P1 | `[ ]` | C7 |
| A45-0205  | If DeliveryDate basis: trigger re-lookup with new delivery date (BR-09) | P1     | `[ ]`  | C7     |
| A45-0206  | If OrderDate basis: no re-lookup on delivery date change             | P1       | `[ ]`  | C7     |
| A45-0207  | Apply same toast logic as quantity change based on current PriceSource | P1      | `[ ]`  | C7     |

### 5.6 Order Date (Header) Change Re-validation (C7)

| ID        | Task                                                                 | Priority | Status | Change |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0208  | On PO header order date change: identify all lines with PricingDateBasis=OrderDate | P1 | `[ ]` | C7 |
| A45-0209  | Count affected lines                                                 | P1       | `[ ]`  | C7     |
| A45-0210  | Show summary banner: "Order date changed. {N} line(s) may have different prices." | P1 | `[ ]` | C7 |
| A45-0211  | "[Re-lookup All]" button: re-resolve all affected lines (BR-10)      | P1       | `[ ]`  | C7     |
| A45-0212  | "[Keep Current Prices]" button: dismiss banner, no changes           | P1       | `[ ]`  | C7     |
| A45-0213  | Re-lookup respects per-line PriceSource (skip Override/Manual unless user chose Re-lookup All) | P1 | `[ ]` | C7 |

### 5.7 PO Line Save — Backend Handling

| ID        | Task                                                                 | Priority | Status | Change |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0214  | Update PO Line create/update endpoint to accept PriceSource, SystemResolvedPrice, SupplierPricelistId, PriceOverrideReason | P0 | `[ ]` | C5 |
| A45-0215  | Validate PriceSource enum value on save                              | P0       | `[ ]`  | C5     |
| A45-0216  | Validate SupplierPricelistId FK exists when PriceSource=Pricelist or Override | P0 | `[ ]` | C5 |
| A45-0217  | Allow SupplierPricelistId=null when PriceSource=Manual or RFQ       | P0       | `[ ]`  | C5     |

**Phase 5 Total: 38 tasks**

---

## Phase 6 — Integration Testing & Test Scenarios (Week 6–7)
**Test Scenarios:** TS-01 through TS-26

### 6.1 Price Resolution — Core (TS-01 to TS-06)

| ID        | Task                                                                 | Priority | Status | Test   |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0218  | TS-01: Add PO line with valid pricelist (OrderDate basis, date in range, qty ≥ min) → price auto-filled, PriceSource=Pricelist, badge shown | P0 | `[ ]` | TS-01 |
| A45-0219  | TS-02: Add PO line with no pricelist for product+vendor → warning banner, price empty, [Enter Manually] + [Create RFQ] shown | P0 | `[ ]` | TS-02 |
| A45-0220  | TS-03: Pricelist exists but PO order date outside validity → warning banner | P0 | `[ ]` | TS-03 |
| A45-0221  | TS-04: Pricelist exists but qty < MinQuantity on all records → warning banner | P0 | `[ ]` | TS-04 |
| A45-0222  | TS-05: Pricelist with DeliveryDate basis, delivery date in range → resolved using delivery date | P0 | `[ ]` | TS-05 |
| A45-0223  | TS-06: DeliveryDate basis but no delivery date on PO line → falls back to order date | P1 | `[ ]` | TS-06 |

### 6.2 Priority & Quantity Breaks (TS-07 to TS-09)

| ID        | Task                                                                 | Priority | Status | Test   |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0224  | TS-07: Multiple records, different Priority → lowest Priority wins   | P0       | `[ ]`  | TS-07  |
| A45-0225  | TS-08: Qty breaks MinQty=1 ($15), MinQty=100 ($12.50) → qty 50 gets $15, qty 200 gets $12.50 | P0 | `[ ]` | TS-08 |
| A45-0226  | TS-09: Three tiers — qty 500 → $10, qty 150 → $12.50               | P0       | `[ ]`  | TS-09  |

### 6.3 Override & Manual Entry (TS-10 to TS-13)

| ID        | Task                                                                 | Priority | Status | Test   |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0227  | TS-10: Auto-filled price → buyer edits → PriceSource=Override, SystemResolvedPrice preserved | P0 | `[ ]` | TS-10 |
| A45-0228  | TS-11: Override with reason text → PriceOverrideReason saved         | P1       | `[ ]`  | TS-11  |
| A45-0229  | TS-12: No price found → [Enter Manually] → price saved, PriceSource=Manual, warning dismissed | P0 | `[ ]` | TS-12 |
| A45-0230  | TS-13: No price found → [Create RFQ] → RFQ screen opens pre-filled  | P1       | `[ ]`  | TS-13  |

### 6.4 Re-lookup on Changes (TS-14 to TS-20)

| ID        | Task                                                                 | Priority | Status | Test   |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0231  | TS-14: Change qty on Pricelist line → new tier → price auto-updated silently | P0 | `[ ]` | TS-14 |
| A45-0232  | TS-15: Change qty on Override line → toast with [Keep]/[Update]      | P1       | `[ ]`  | TS-15  |
| A45-0233  | TS-16: Change qty on Manual line → configured price available → toast with [Keep]/[Apply] | P1 | `[ ]` | TS-16 |
| A45-0234  | TS-17: Change delivery date on DeliveryDate-basis line → re-lookup   | P1       | `[ ]`  | TS-17  |
| A45-0235  | TS-18: Change delivery date on OrderDate-basis line → no re-lookup   | P1       | `[ ]`  | TS-18  |
| A45-0236  | TS-19: Change PO header order date → 3 OrderDate-basis lines → summary banner with [Re-lookup]/[Keep] | P1 | `[ ]` | TS-19 |
| A45-0237  | TS-20: Change qty on RFQ line → no re-lookup                        | P1       | `[ ]`  | TS-20  |

### 6.5 Pricelist Management (TS-21 to TS-26)

| ID        | Task                                                                 | Priority | Status | Test   |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0238  | TS-21: Save pricelist with overlapping dates → warning toast, save allowed | P1  | `[ ]`  | TS-21  |
| A45-0239  | TS-22: Save pricelist with ValidTo < ValidFrom → validation error, blocked | P0  | `[ ]`  | TS-22  |
| A45-0240  | TS-23: Save pricelist with UnitPrice=0 or negative → validation error, blocked | P0 | `[ ]` | TS-23 |
| A45-0241  | TS-24: Deactivate record → add PO line → deactivated record skipped  | P0       | `[ ]`  | TS-24  |
| A45-0242  | TS-25: View PO line referencing soft-deleted pricelist → FK valid, historical source visible | P2 | `[ ]` | TS-25 |
| A45-0243  | TS-26: Two buyers create POs simultaneously → both resolve independently, no contention | P1 | `[ ]` | TS-26 |

### 6.6 Business Rule Verification

| ID        | Task                                                                 | Priority | Status | Rule   |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0244  | Verify BR-01: Priority ASC, MinQuantity DESC ordering in resolver    | P0       | `[ ]`  | BR-01  |
| A45-0245  | Verify BR-02: PricingDateBasis correctly switches between OrderDate and DeliveryDate | P0 | `[ ]` | BR-02 |
| A45-0246  | Verify BR-03: DeliveryDate fallback to OrderDate when null           | P0       | `[ ]`  | BR-03  |
| A45-0247  | Verify BR-04: Warning displayed, PO creation not blocked             | P0       | `[ ]`  | BR-04  |
| A45-0248  | Verify BR-05: Buyer can always edit auto-filled price → Override     | P0       | `[ ]`  | BR-05  |
| A45-0249  | Verify BR-06: SystemResolvedPrice preserved after override           | P0       | `[ ]`  | BR-06  |
| A45-0250  | Verify BR-15: Only IsActive=1 records participate in resolution      | P0       | `[ ]`  | BR-15  |
| A45-0251  | Verify BR-16: Soft-delete retains record for FK reference            | P0       | `[ ]`  | BR-16  |
| A45-0252  | Verify BR-17: RFQ-sourced prices not re-looked-up                    | P0       | `[ ]`  | BR-17  |
| A45-0253  | Verify BR-18: Pre-migration PO lines have PriceSource=Manual         | P0       | `[ ]`  | BR-18  |
| A45-0254  | Verify BR-19: Read-only resolution, no locks, concurrent safe        | P1       | `[ ]`  | BR-19  |

### 6.7 Cross-Cutting Verification

| ID        | Task                                                                 | Priority | Status | Notes  |
| --------- | -------------------------------------------------------------------- | -------- | ------ | ------ |
| A45-0255  | Multi-tenant isolation: Org A pricelists invisible to Org B          | P0       | `[ ]`  | Security |
| A45-0256  | Audit trail: PriceSource + SystemResolvedPrice visible in PO line detail view | P0 | `[ ]` | Audit |
| A45-0257  | Covering index used by resolver query — verify via query plan        | P1       | `[ ]`  | Perf   |
| A45-0258  | Regression: existing PO CRUD unaffected by new columns (default values) | P0     | `[ ]`  | Compat |

**Phase 6 Total: 41 tasks**

---

## Summary

| Phase | Description                              | Tasks | Weeks |
| ----- | ---------------------------------------- | ----- | ----- |
| 1     | Database & Entity Foundation (C1, C2, M1–M3) | 70    | 1–2   |
| 2     | Price Resolution Service (C3)            | 29    | 2–3   |
| 3     | API Layer (6.1, 6.2)                     | 49    | 3–4   |
| 4     | Supplier Pricelist Management UI (C4, C6) | 31    | 4–5   |
| 5     | PO Line Auto-Pricing & Re-lookup (C5, C7) | 38   | 5–6   |
| 6     | Integration Testing (TS-01 to TS-26, BRs) | 41   | 6–7   |
| **Total** |                                      | **258** | **7 weeks** |

### Priority Distribution

| Priority | Count | Percentage |
| -------- | ----- | ---------- |
| P0       | 155   | 60.1%      |
| P1       | 97    | 37.6%      |
| P2       | 6     | 2.3%       |
| **Total** | **258** | **100%** |

### Change Item Coverage

| Change Item | Description                        | Tasks | Phase(s) |
| ----------- | ---------------------------------- | ----- | -------- |
| C1          | SupplierPricelist Entity           | 55    | 1        |
| C2          | PO Line Price Source Tracking      | 18    | 1        |
| C3          | Price Resolution Service           | 29    | 2        |
| C4          | Supplier Pricelist Management UI   | 31    | 4        |
| C5          | PO Line Auto-Pricing UI           | 27    | 5        |
| C6          | Pricelist Overlap Validation       | 5     | 3, 4     |
| C7          | Quantity / Date Change Re-lookup   | 20    | 5        |
| M1–M3       | Migrations                         | 12    | 1        |
| 6.1–6.2     | API Endpoints                      | 42    | 3        |
| BR-01–BR-19 | Business Rules Verification        | 11    | 6        |
| TS-01–TS-26 | Test Scenarios                     | 26    | 6        |

---

*End of Task Register — Addendum 45*
