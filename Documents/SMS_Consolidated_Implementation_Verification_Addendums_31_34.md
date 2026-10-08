# SMS Consolidated Implementation Verification Checklist

## Addendums 31, 32, 33 & 34

**Document:** SMS-IMPL-VERIFY-CONSOLIDATED v1.0
**Covers:** SMS-FSD-ADD-031, SMS-FSD-ADD-032, SMS-FSD-ADD-033, SMS-FSD-ADD-034
**Purpose:** Manual verification of all implementation steps across four addendums
**Date:** 2026-10-06

---

## Document Statistics

| Metric | Add-31 | Add-32 | Add-33 | Add-34 | Total |
|--------|--------|--------|--------|--------|-------|
| Changes | 10 | 5 | 5 | 6 | **26** |
| Migrations | 3 | 7 | 4 | 4 | **18** |
| Business Rules | 24 | 28 | 25 | 30 | **107** |
| Test Scenarios | 29 | 34 | 30 | 35 | **128** |
| New Permission Claims | 0 | 10 | 3 | 1 | **14** |
| Dev Days (Sequential) | 23 | 23 | 19 | 25 | **90** |
| Dev Days (Parallel) | ~18 | ~23 | ~19 | ~16 | **~76** |

---

## Cross-Addendum Dependency Map

```
Addendum 30 (Prior)
    └──► Addendum 31 (Manufacturing Flow Corrections)
              └──► Addendum 34 (Route Classification & Production-Delivery Bridge)

Addendum 32 (Sales Pre-Order Pipeline)
    └──► Addendum 33 (Fulfillment Routes)
              └──► Addendum 34 (Route Classification & Production-Delivery Bridge)
```

**Recommended Implementation Order:** 31 → 32 → 33 → 34
(Addendums 31 and 32 can run in parallel since they are independent)

---

## How to Use This Document

Each implementation step has a checkbox `[ ]`. Mark with `[x]` once verified:
- **Schema** — migration applied, columns/tables exist, constraints correct
- **Backend** — service/handler implemented, logic matches business rules
- **API** — endpoint exists, request/response matches spec
- **UI** — component renders, interactions work per wireframe
- **Test** — scenario passes (manual or automated)

---

# PART 1 — SCHEMA MIGRATIONS (18 Total)

## 1.1 Addendum 31 Migrations (M1–M3)

### M1: Product Min/Max Qty Columns
**Table:** `lookups.Products`

```sql
ALTER TABLE lookups.Products
  ADD sale_order_min_qty DECIMAL(18,4) NULL;

ALTER TABLE lookups.Products
  ADD sale_order_max_qty DECIMAL(18,4) NULL;
```

- [ ] Column `sale_order_min_qty` exists on `lookups.Products` as `DECIMAL(18,4) NULL`
- [ ] Column `sale_order_max_qty` exists on `lookups.Products` as `DECIMAL(18,4) NULL`
- [ ] Existing product rows unaffected (NULL values)
- [ ] EF Core entity `Product` updated with both properties

---

### M2: Production Order ↔ Sale Order Link
**Table:** `material.ProductionOrders`

```sql
ALTER TABLE material.ProductionOrders
  ADD sale_order_id BIGINT NULL
  CONSTRAINT FK_ProductionOrders_SaleOrders
    FOREIGN KEY REFERENCES demand.SaleOrders(sale_order_id);

ALTER TABLE material.ProductionOrders
  ADD sale_order_line_id BIGINT NULL
  CONSTRAINT FK_ProductionOrders_SaleOrderLines
    FOREIGN KEY REFERENCES demand.SaleOrderLines(sale_order_line_id);

CREATE INDEX IX_ProductionOrders_SaleOrder
  ON material.ProductionOrders(sale_order_id)
  WHERE sale_order_id IS NOT NULL;
```

- [ ] Column `sale_order_id` exists as `BIGINT NULL` with FK to `demand.SaleOrders`
- [ ] Column `sale_order_line_id` exists as `BIGINT NULL` with FK to `demand.SaleOrderLines`
- [ ] Filtered index `IX_ProductionOrders_SaleOrder` exists on `sale_order_id WHERE sale_order_id IS NOT NULL`
- [ ] EF Core entity `ProductionOrder` has navigation properties to `SaleOrder` and `SaleOrderLine`
- [ ] Existing production orders unaffected (NULL values)

---

### M3: Remove Warehouse from BOM Lines
**Table:** `material.BillOfMaterialLines`

```sql
ALTER TABLE material.BillOfMaterialLines
  DROP CONSTRAINT IF EXISTS FK_BOMLines_Warehouse;

ALTER TABLE material.BillOfMaterialLines
  DROP COLUMN IF EXISTS warehouse_id;
```

- [ ] Column `warehouse_id` no longer exists on `material.BillOfMaterialLines`
- [ ] FK constraint `FK_BOMLines_Warehouse` dropped
- [ ] EF Core entity `BillOfMaterialLine` no longer has `WarehouseId` property
- [ ] All code references to `BOMLine.warehouse_id` removed
- [ ] PMR warehouse now always sourced from `ProductionOrder.production_warehouse_id`

---

## 1.2 Addendum 32 Migrations (M1–M7)

### M1: Rejection Reasons Lookup
**Table:** `lookups.RejectionReasons`

```sql
CREATE TABLE lookups.RejectionReasons (
    rejection_reason_id   INT IDENTITY(1,1) PRIMARY KEY,
    org_id                INT NOT NULL REFERENCES auth.Organizations(org_id),
    code                  NVARCHAR(10) NOT NULL,
    name                  NVARCHAR(100) NOT NULL,
    description           NVARCHAR(500) NULL,
    is_active             BIT NOT NULL DEFAULT 1,
    is_system             BIT NOT NULL DEFAULT 0,
    created_by            INT NOT NULL,
    created_at            DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    modified_by           INT NULL,
    modified_at           DATETIME2 NULL,
    CONSTRAINT UQ_RejectionReasons_Code UNIQUE(org_id, code)
);
```

**Seed Data (10 codes):**

| Code | Name |
|------|------|
| NIP | Not in Product Range |
| OOS | Out of Stock |
| DIS | Discontinued |
| MOQ | Below Minimum Order Quantity |
| GEO | Geographic Restriction |
| REG | Regulatory Restriction |
| CAP | Capacity Constraint |
| CRD | Credit Hold |
| PRC | Price Disagreement |
| OTH | Other |

- [ ] Table `lookups.RejectionReasons` created with all columns
- [ ] Unique constraint on `(org_id, code)` exists
- [ ] All 10 seed codes inserted for each existing org
- [ ] `is_system = 1` for all seed records (prevents deletion)
- [ ] EF Core entity and `HasQueryFilter(r => r.OrgId == _orgId)` configured

---

### M2: Sale Inquiry Tables
**Tables:** `demand.SaleInquiries`, `demand.SaleInquiryLines`, `demand.SaleInquiryAttachments`

- [ ] Table `demand.SaleInquiries` created with columns:
  - [ ] `inquiry_id` BIGINT IDENTITY PK
  - [ ] `org_id` INT NOT NULL FK
  - [ ] `inquiry_number` NVARCHAR(20) NOT NULL (format: `INQ-{YYYY}-{NNNN}`)
  - [ ] `customer_id` INT NOT NULL FK to `suppliers.Suppliers`
  - [ ] `inquiry_date` DATE NOT NULL
  - [ ] `status` NVARCHAR(20) NOT NULL DEFAULT 'RECEIVED'
  - [ ] `notes` NVARCHAR(2000) NULL
  - [ ] `assigned_to` INT NULL FK to `auth.Users`
  - [ ] Standard audit columns (`created_by`, `created_at`, `modified_by`, `modified_at`)
  - [ ] Unique constraint on `(org_id, inquiry_number)`
- [ ] Table `demand.SaleInquiryLines` created with columns:
  - [ ] `inquiry_line_id` BIGINT IDENTITY PK
  - [ ] `inquiry_id` BIGINT NOT NULL FK
  - [ ] `product_variant_id` INT NOT NULL FK
  - [ ] `requested_qty` DECIMAL(18,4) NOT NULL
  - [ ] `unit_id` INT NOT NULL FK
  - [ ] `requested_delivery_date` DATE NULL
  - [ ] `evaluation_status` NVARCHAR(20) NOT NULL DEFAULT 'PENDING'
  - [ ] `evaluation_notes` NVARCHAR(1000) NULL
  - [ ] `rejection_reason_id` INT NULL FK
  - [ ] `available_qty` DECIMAL(18,4) NULL
  - [ ] `earliest_delivery_date` DATE NULL
  - [ ] `line_order` INT NOT NULL DEFAULT 0
- [ ] Table `demand.SaleInquiryAttachments` created
- [ ] Index on `(org_id, status)` exists on SaleInquiries
- [ ] Index on `(inquiry_id)` exists on SaleInquiryLines

---

### M3: Sale Quotation Tables
**Tables:** `demand.SaleQuotations`, `demand.SaleQuotationLines`, `demand.SaleQuotationAttachments`

- [ ] Table `demand.SaleQuotations` created with columns:
  - [ ] `quotation_id` BIGINT IDENTITY PK
  - [ ] `org_id` INT NOT NULL FK
  - [ ] `quotation_number` NVARCHAR(20) NOT NULL (format: `SQ-{YYYY}-{NNNN}`)
  - [ ] `customer_id` INT NOT NULL FK
  - [ ] `inquiry_id` BIGINT NULL FK (link back to inquiry)
  - [ ] `quotation_date` DATE NOT NULL
  - [ ] `valid_until` DATE NULL
  - [ ] `status` NVARCHAR(20) NOT NULL DEFAULT 'DRAFT'
  - [ ] `currency_id` INT NOT NULL FK
  - [ ] `notes` NVARCHAR(2000) NULL
  - [ ] Standard audit columns
  - [ ] Unique constraint on `(org_id, quotation_number)`
- [ ] Table `demand.SaleQuotationLines` created with columns:
  - [ ] `quotation_line_id` BIGINT IDENTITY PK
  - [ ] `quotation_id` BIGINT NOT NULL FK
  - [ ] `product_variant_id` INT NOT NULL FK
  - [ ] `qty` DECIMAL(18,4) NOT NULL
  - [ ] `unit_id` INT NOT NULL FK
  - [ ] `unit_price` DECIMAL(18,4) NOT NULL
  - [ ] `discount_percent` DECIMAL(5,2) NULL DEFAULT 0
  - [ ] `line_total` AS (`qty * unit_price * (1 - ISNULL(discount_percent,0)/100)`) PERSISTED
  - [ ] `line_type` NVARCHAR(20) NOT NULL DEFAULT 'NORMAL' (NORMAL/REJECTED/ALTERNATIVE)
  - [ ] `rejection_reason_id` INT NULL FK
  - [ ] `parent_line_id` BIGINT NULL FK (self-ref for alternatives)
  - [ ] `customer_response` NVARCHAR(20) NOT NULL DEFAULT 'PENDING' (PENDING/ACCEPTED/REJECTED/COUNTER)
  - [ ] `counter_price` DECIMAL(18,4) NULL
  - [ ] `counter_qty` DECIMAL(18,4) NULL
  - [ ] `delivery_date` DATE NULL
  - [ ] `line_order` INT NOT NULL DEFAULT 0
- [ ] Table `demand.SaleQuotationAttachments` created
- [ ] Index on `(org_id, status)` exists on SaleQuotations
- [ ] Index on `(quotation_id, line_type)` exists on SaleQuotationLines

---

### M4: Sale Order Source Linking
**Table:** `demand.SaleOrders` (ALTER)

```sql
ALTER TABLE demand.SaleOrders ADD source_quotation_id BIGINT NULL
  CONSTRAINT FK_SaleOrders_SourceQuotation
    FOREIGN KEY REFERENCES demand.SaleQuotations(quotation_id);

ALTER TABLE demand.SaleOrders ADD source_inquiry_id BIGINT NULL
  CONSTRAINT FK_SaleOrders_SourceInquiry
    FOREIGN KEY REFERENCES demand.SaleInquiries(inquiry_id);

ALTER TABLE demand.SaleOrders ADD source_type NVARCHAR(20) NOT NULL
  DEFAULT 'MANUAL';  -- MANUAL/FROM_QUOTATION/PORTAL/INTER_TENANT

ALTER TABLE demand.SaleOrders ADD customer_po_reference NVARCHAR(50) NULL;
ALTER TABLE demand.SaleOrders ADD customer_po_date DATE NULL;
ALTER TABLE demand.SaleOrders ADD customer_po_attachment_id BIGINT NULL;
```

- [ ] Column `source_quotation_id` exists as `BIGINT NULL` with FK
- [ ] Column `source_inquiry_id` exists as `BIGINT NULL` with FK
- [ ] Column `source_type` exists as `NVARCHAR(20) NOT NULL DEFAULT 'MANUAL'`
- [ ] Column `customer_po_reference` exists as `NVARCHAR(50) NULL`
- [ ] Column `customer_po_date` exists as `DATE NULL`
- [ ] Column `customer_po_attachment_id` exists as `BIGINT NULL`
- [ ] Existing sale orders backfilled with `source_type = 'MANUAL'`
- [ ] Index on `source_quotation_id` where not null
- [ ] Index on `source_inquiry_id` where not null

---

### M5: Sale Order Attachments
**Table:** `demand.SaleOrderAttachments`

- [ ] Table `demand.SaleOrderAttachments` created with standard attachment columns
- [ ] FK to `demand.SaleOrders` exists
- [ ] `org_id` discriminator and query filter configured

---

### M6: Sale Order Line Reserved Qty
**Table:** `demand.SaleOrderLines` (ALTER)

```sql
ALTER TABLE demand.SaleOrderLines
  ADD reserved_qty DECIMAL(18,4) NOT NULL DEFAULT 0;

-- Backfill existing
UPDATE demand.SaleOrderLines SET reserved_qty = 0 WHERE reserved_qty = 0;
```

- [ ] Column `reserved_qty` exists as `DECIMAL(18,4) NOT NULL DEFAULT 0`
- [ ] All existing rows have `reserved_qty = 0`
- [ ] EF Core entity updated

---

### M7: Sales Permission Claims
**Table:** `auth.Permissions`

```sql
INSERT INTO auth.Permissions (permission_name, description, module)
VALUES
  ('sales_inquiry_view', 'View sale inquiries', 'demand'),
  ('sales_inquiry_create', 'Create sale inquiries', 'demand'),
  ('sales_inquiry_edit', 'Edit sale inquiries', 'demand'),
  ('sales_quotation_view', 'View sale quotations', 'demand'),
  ('sales_quotation_create', 'Create sale quotations', 'demand'),
  ('sales_quotation_edit', 'Edit sale quotations', 'demand'),
  ('sales_quotation_send', 'Send quotations to customers', 'demand'),
  ('sales_order_create', 'Create sale orders', 'demand'),
  ('sales_reserve_inventory', 'Reserve inventory against SO lines', 'demand'),
  ('sales_release_reservation', 'Release inventory reservations', 'demand');
```

- [ ] All 10 permission claims inserted
- [ ] Claims available in role assignment UI
- [ ] `[Authorize(Policy = "sales_inquiry_view")]` etc. applied to correct controllers

---

## 1.3 Addendum 33 Migrations (M1–M4)

### M1: Fulfillment Route Configuration
**Tables:** `logistics.FulfillmentRoutes`, `logistics.FulfillmentRouteSteps`

```sql
CREATE TABLE logistics.FulfillmentRoutes (
    fulfillment_route_id  INT IDENTITY(1,1) PRIMARY KEY,
    org_id                INT NOT NULL REFERENCES auth.Organizations(org_id),
    code                  NVARCHAR(30) NOT NULL,
    name                  NVARCHAR(100) NOT NULL,
    description           NVARCHAR(500) NULL,
    is_default            BIT NOT NULL DEFAULT 0,
    is_active             BIT NOT NULL DEFAULT 1,
    is_system             BIT NOT NULL DEFAULT 0,
    requires_packing      BIT NOT NULL DEFAULT 0,
    requires_shipping     BIT NOT NULL DEFAULT 1,
    -- Standard audit columns
    CONSTRAINT UQ_FulfillmentRoutes_Code UNIQUE(org_id, code)
);

CREATE TABLE logistics.FulfillmentRouteSteps (
    route_step_id         INT IDENTITY(1,1) PRIMARY KEY,
    fulfillment_route_id  INT NOT NULL REFERENCES logistics.FulfillmentRoutes,
    step_code             NVARCHAR(20) NOT NULL
      CHECK (step_code IN ('PICK','PACK','STAGE','APPROVAL','GOODS_ISSUE','SHIP')),
    step_order            INT NOT NULL,
    is_mandatory          BIT NOT NULL DEFAULT 1,
    CONSTRAINT UQ_RouteSteps_Order UNIQUE(fulfillment_route_id, step_order)
);
```

**3 Seed Routes:**

| Code | Name | Default | Steps |
|------|------|---------|-------|
| PICK_ONLY | Pick Only | No | Pick → Goods Issue |
| PICK_AND_SHIP | Pick and Ship | **Yes** | Pick → Goods Issue → Ship |
| PICK_PACK_SHIP | Pick, Pack and Ship | No | Pick → Pack → Goods Issue → Ship |

- [ ] Table `logistics.FulfillmentRoutes` created with all columns
- [ ] Table `logistics.FulfillmentRouteSteps` created with CHECK constraint on `step_code`
- [ ] Unique constraint on `(org_id, code)` exists
- [ ] Unique constraint on `(fulfillment_route_id, step_order)` exists
- [ ] Seed route `PICK_ONLY` inserted with steps: PICK(1) → GOODS_ISSUE(2)
- [ ] Seed route `PICK_AND_SHIP` inserted with steps: PICK(1) → GOODS_ISSUE(2) → SHIP(3), `is_default = 1`
- [ ] Seed route `PICK_PACK_SHIP` inserted with steps: PICK(1) → PACK(2) → GOODS_ISSUE(3) → SHIP(4)
- [ ] All seed routes have `is_system = 1`
- [ ] Only one route per org has `is_default = 1` (enforced by trigger or application logic)
- [ ] EF Core entities and query filter configured

---

### M2: Product Variant Route Assignment
**Table:** `inventory.ProductVariants` (ALTER)

```sql
ALTER TABLE inventory.ProductVariants
  ADD fulfillment_route_id INT NULL
  CONSTRAINT FK_ProductVariants_FulfillmentRoute
    FOREIGN KEY REFERENCES logistics.FulfillmentRoutes(fulfillment_route_id);
```

- [ ] Column `fulfillment_route_id` exists as `INT NULL` on `inventory.ProductVariants`
- [ ] FK constraint to `logistics.FulfillmentRoutes` exists
- [ ] EF Core entity `ProductVariant` has `FulfillmentRouteId` and navigation property

---

### M3: SO Line Route Assignment
**Table:** `demand.SaleOrderLines` (ALTER)

```sql
ALTER TABLE demand.SaleOrderLines
  ADD fulfillment_route_id INT NULL
  CONSTRAINT FK_SaleOrderLines_FulfillmentRoute
    FOREIGN KEY REFERENCES logistics.FulfillmentRoutes(fulfillment_route_id);
```

- [ ] Column `fulfillment_route_id` exists as `INT NULL` on `demand.SaleOrderLines`
- [ ] FK constraint to `logistics.FulfillmentRoutes` exists
- [ ] EF Core entity `SaleOrderLine` updated

---

### M4: Delivery Order ↔ Sale Order & Route Link
**Tables:** `logistics.delivery_orders`, `logistics.delivery_order_lines` (ALTER)

```sql
ALTER TABLE logistics.delivery_orders
  ADD sale_order_id BIGINT NULL
  CONSTRAINT FK_DeliveryOrders_SaleOrder
    FOREIGN KEY REFERENCES demand.SaleOrders(sale_order_id);

ALTER TABLE logistics.delivery_orders
  ADD fulfillment_route_id INT NULL
  CONSTRAINT FK_DeliveryOrders_FulfillmentRoute
    FOREIGN KEY REFERENCES logistics.FulfillmentRoutes(fulfillment_route_id);

ALTER TABLE logistics.delivery_order_lines
  ADD sale_order_line_id BIGINT NULL
  CONSTRAINT FK_DOLines_SaleOrderLine
    FOREIGN KEY REFERENCES demand.SaleOrderLines(sale_order_line_id);

-- Extend from_source_type CHECK to include SALE_ORDER
-- (Implementation depends on existing constraint definition)
```

- [ ] Column `sale_order_id` exists as `BIGINT NULL` on `logistics.delivery_orders` with FK
- [ ] Column `fulfillment_route_id` exists as `INT NULL` on `logistics.delivery_orders` with FK
- [ ] Column `sale_order_line_id` exists as `BIGINT NULL` on `logistics.delivery_order_lines` with FK
- [ ] `from_source_type` CHECK constraint updated to include `'SALE_ORDER'`
- [ ] Indexes created on FK columns

---

## 1.4 Addendum 34 Migrations (M1–M4)

### M1: Route Category Column & Manufacturing Seed Routes
**Table:** `logistics.FulfillmentRoutes` (ALTER)

```sql
ALTER TABLE logistics.FulfillmentRoutes
  ADD route_category NVARCHAR(20) NOT NULL DEFAULT 'STOCK'
  CONSTRAINT CK_FulfillmentRoutes_Category
    CHECK (route_category IN ('STOCK','MANUFACTURE','BUY','DROPSHIP'));

-- Update existing seed routes to STOCK
UPDATE logistics.FulfillmentRoutes
  SET route_category = 'STOCK'
  WHERE code IN ('PICK_ONLY','PICK_AND_SHIP','PICK_PACK_SHIP');

-- Insert manufacturing routes for all existing orgs (cursor-based)
DECLARE @org_id INT;
DECLARE org_cursor CURSOR FOR SELECT org_id FROM auth.Organizations;
OPEN org_cursor;
FETCH NEXT FROM org_cursor INTO @org_id;
WHILE @@FETCH_STATUS = 0
BEGIN
    -- MFG_PICK_SHIP
    INSERT INTO logistics.FulfillmentRoutes (org_id, code, name, route_category,
      is_default, is_active, is_system, requires_packing, requires_shipping)
    VALUES (@org_id, 'MFG_PICK_SHIP', 'Manufacture, Pick and Ship', 'MANUFACTURE',
      0, 1, 1, 0, 1);
    -- steps: PICK(1), GOODS_ISSUE(2), SHIP(3)

    -- MFG_PICK_PACK_SHIP
    INSERT INTO logistics.FulfillmentRoutes (org_id, code, name, route_category,
      is_default, is_active, is_system, requires_packing, requires_shipping)
    VALUES (@org_id, 'MFG_PICK_PACK_SHIP', 'Manufacture, Pick, Pack and Ship', 'MANUFACTURE',
      0, 1, 1, 1, 1);
    -- steps: PICK(1), PACK(2), GOODS_ISSUE(3), SHIP(4)

    FETCH NEXT FROM org_cursor INTO @org_id;
END;
CLOSE org_cursor; DEALLOCATE org_cursor;
```

**All Routes After Migration:**

| Code | Category | Default | Steps |
|------|----------|---------|-------|
| PICK_ONLY | STOCK | No | Pick → GI |
| PICK_AND_SHIP | STOCK | Yes | Pick → GI → Ship |
| PICK_PACK_SHIP | STOCK | No | Pick → Pack → GI → Ship |
| MFG_PICK_SHIP | MANUFACTURE | No | Pick → GI → Ship |
| MFG_PICK_PACK_SHIP | MANUFACTURE | No | Pick → Pack → GI → Ship |

- [ ] Column `route_category` exists as `NVARCHAR(20) NOT NULL DEFAULT 'STOCK'`
- [ ] CHECK constraint `CK_FulfillmentRoutes_Category` allows only `STOCK/MANUFACTURE/BUY/DROPSHIP`
- [ ] Existing routes updated to `route_category = 'STOCK'`
- [ ] Route `MFG_PICK_SHIP` inserted for all existing orgs with `route_category = 'MANUFACTURE'`
- [ ] Route `MFG_PICK_PACK_SHIP` inserted for all existing orgs with `route_category = 'MANUFACTURE'`
- [ ] Steps correctly inserted for both manufacturing routes
- [ ] New org creation logic also seeds manufacturing routes
- [ ] EF Core entity `FulfillmentRoute` has `RouteCategory` property

---

### M2: Lead Time Defaults & Variant Lead Time Columns
**Tables:** `lookups.LeadTimeDefaults` (CREATE), `inventory.ProductVariants` (ALTER)

```sql
CREATE TABLE lookups.LeadTimeDefaults (
    lead_time_default_id  INT IDENTITY(1,1) PRIMARY KEY,
    org_id                INT NOT NULL UNIQUE REFERENCES auth.Organizations(org_id),
    pick_pack_days        INT NOT NULL DEFAULT 1,
    shipping_lead_time_days INT NOT NULL DEFAULT 3,
    sales_buffer_days     INT NOT NULL DEFAULT 1,
    manufacturing_buffer_days INT NOT NULL DEFAULT 0,
    quality_inspection_days INT NOT NULL DEFAULT 0,
    internal_transfer_days INT NOT NULL DEFAULT 0,
    created_by            INT NOT NULL,
    created_at            DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    modified_by           INT NULL,
    modified_at           DATETIME2 NULL
);

ALTER TABLE inventory.ProductVariants ADD supplier_lead_time_days INT NULL;
ALTER TABLE inventory.ProductVariants ADD manufacturing_lead_time_days INT NULL;
ALTER TABLE inventory.ProductVariants ADD manufacturing_buffer_days INT NULL;
ALTER TABLE inventory.ProductVariants ADD quality_inspection_days INT NULL;
ALTER TABLE inventory.ProductVariants ADD internal_transfer_days INT NULL;
ALTER TABLE inventory.ProductVariants ADD pick_pack_days INT NULL;
ALTER TABLE inventory.ProductVariants ADD shipping_lead_time_days INT NULL;
ALTER TABLE inventory.ProductVariants ADD sales_buffer_days INT NULL;

-- Backfill from existing lead_time_days (if column existed)
UPDATE inventory.ProductVariants
  SET supplier_lead_time_days = lead_time_days
  WHERE lead_time_days IS NOT NULL;
```

- [ ] Table `lookups.LeadTimeDefaults` created with all columns
- [ ] Unique constraint on `org_id` (one row per org)
- [ ] Default values: pick_pack=1, shipping=3, sales_buffer=1, mfg_buffer=0, quality=0, transfer=0
- [ ] Seed row inserted for each existing org
- [ ] 8 new columns on `inventory.ProductVariants`:
  - [ ] `supplier_lead_time_days` INT NULL
  - [ ] `manufacturing_lead_time_days` INT NULL
  - [ ] `manufacturing_buffer_days` INT NULL
  - [ ] `quality_inspection_days` INT NULL
  - [ ] `internal_transfer_days` INT NULL
  - [ ] `pick_pack_days` INT NULL
  - [ ] `shipping_lead_time_days` INT NULL
  - [ ] `sales_buffer_days` INT NULL
- [ ] Existing `lead_time_days` values migrated to `supplier_lead_time_days`
- [ ] EF Core entities updated

---

### M3: Lead Time Columns on Demand Line Tables
**Tables:** `demand.SaleInquiryLines`, `demand.SaleQuotationLines`, `demand.SaleOrderLines` (ALTER)

```sql
-- Applied to each of the three tables:
ALTER TABLE demand.{TableName} ADD calculated_lead_time_days INT NULL;
ALTER TABLE demand.{TableName} ADD calculated_delivery_date DATE NULL;
ALTER TABLE demand.{TableName} ADD manual_delivery_date DATE NULL;
ALTER TABLE demand.{TableName} ADD lead_time_calculated_at DATETIME2 NULL;
ALTER TABLE demand.{TableName} ADD effective_delivery_date
  AS COALESCE(manual_delivery_date, calculated_delivery_date) PERSISTED;
```

- [ ] On `demand.SaleInquiryLines` (referred to as InquiryLines):
  - [ ] `calculated_lead_time_days` INT NULL
  - [ ] `calculated_delivery_date` DATE NULL
  - [ ] `manual_delivery_date` DATE NULL
  - [ ] `lead_time_calculated_at` DATETIME2 NULL
  - [ ] `effective_delivery_date` computed column (PERSISTED)
- [ ] On `demand.SaleQuotationLines` (referred to as QuotationLines):
  - [ ] `calculated_lead_time_days` INT NULL
  - [ ] `calculated_delivery_date` DATE NULL
  - [ ] `manual_delivery_date` DATE NULL
  - [ ] `lead_time_calculated_at` DATETIME2 NULL
  - [ ] `effective_delivery_date` computed column (PERSISTED)
- [ ] On `demand.SaleOrderLines`:
  - [ ] `calculated_lead_time_days` INT NULL
  - [ ] `calculated_delivery_date` DATE NULL
  - [ ] `manual_delivery_date` DATE NULL
  - [ ] `lead_time_calculated_at` DATETIME2 NULL
  - [ ] `effective_delivery_date` computed column (PERSISTED)
- [ ] COALESCE logic: `manual_delivery_date` takes priority over `calculated_delivery_date`
- [ ] EF Core entities updated (computed column mapped as `ValueGeneratedOnAddOrUpdate`)

---

### M4: Production Order Route & Delivery Link
**Table:** `material.ProductionOrders` (ALTER)

```sql
ALTER TABLE material.ProductionOrders
  ADD fulfillment_route_id INT NULL
  CONSTRAINT FK_ProductionOrders_FulfillmentRoute
    FOREIGN KEY REFERENCES logistics.FulfillmentRoutes(fulfillment_route_id);

ALTER TABLE material.ProductionOrders
  ADD delivery_order_id BIGINT NULL
  CONSTRAINT FK_ProductionOrders_DeliveryOrder
    FOREIGN KEY REFERENCES logistics.delivery_orders(delivery_order_id);

CREATE INDEX IX_ProductionOrders_FulfillmentRoute
  ON material.ProductionOrders(fulfillment_route_id)
  WHERE fulfillment_route_id IS NOT NULL;

CREATE INDEX IX_ProductionOrders_DeliveryOrder
  ON material.ProductionOrders(delivery_order_id)
  WHERE delivery_order_id IS NOT NULL;

-- Backfill existing POs linked to MANUFACTURE routes
UPDATE po
  SET po.fulfillment_route_id = sol.fulfillment_route_id
  FROM material.ProductionOrders po
  JOIN demand.SaleOrderLines sol ON po.sale_order_line_id = sol.sale_order_line_id
  WHERE po.sale_order_line_id IS NOT NULL
    AND sol.fulfillment_route_id IS NOT NULL;
```

- [ ] Column `fulfillment_route_id` exists as `INT NULL` on `material.ProductionOrders` with FK
- [ ] Column `delivery_order_id` exists as `BIGINT NULL` on `material.ProductionOrders` with FK
- [ ] Filtered index on `fulfillment_route_id` exists
- [ ] Filtered index on `delivery_order_id` exists
- [ ] Backfill applied for existing POs linked to SO lines with routes
- [ ] EF Core entity `ProductionOrder` has `FulfillmentRouteId` and `DeliveryOrderId` properties

---

### Permission Claims — Addendum 34 (M4 continued)

```sql
INSERT INTO auth.Permissions (permission_name, description, module)
VALUES ('lead_time_defaults_manage', 'Manage organization lead time defaults', 'lookups');
```

- [ ] Permission `lead_time_defaults_manage` inserted
- [ ] Available in role assignment

---

### Permission Claims — Addendum 33

```sql
INSERT INTO auth.Permissions (permission_name, description, module)
VALUES
  ('fulfillment_route_view', 'View fulfillment routes', 'logistics'),
  ('fulfillment_route_manage', 'Manage fulfillment route configuration', 'logistics'),
  ('fulfillment_route_assign', 'Assign routes to products and orders', 'logistics');
```

- [ ] Permission `fulfillment_route_view` inserted
- [ ] Permission `fulfillment_route_manage` inserted
- [ ] Permission `fulfillment_route_assign` inserted
- [ ] Applied to correct controllers

---

## Consolidated Permission Claims (14 Total)

| # | Claim | Module | Addendum |
|---|-------|--------|----------|
| 1 | `sales_inquiry_view` | demand | 32 |
| 2 | `sales_inquiry_create` | demand | 32 |
| 3 | `sales_inquiry_edit` | demand | 32 |
| 4 | `sales_quotation_view` | demand | 32 |
| 5 | `sales_quotation_create` | demand | 32 |
| 6 | `sales_quotation_edit` | demand | 32 |
| 7 | `sales_quotation_send` | demand | 32 |
| 8 | `sales_order_create` | demand | 32 |
| 9 | `sales_reserve_inventory` | demand | 32 |
| 10 | `sales_release_reservation` | demand | 32 |
| 11 | `fulfillment_route_view` | logistics | 33 |
| 12 | `fulfillment_route_manage` | logistics | 33 |
| 13 | `fulfillment_route_assign` | logistics | 33 |
| 14 | `lead_time_defaults_manage` | lookups | 34 |

- [ ] All 14 permission claims exist in `auth.Permissions`
- [ ] Each is assignable to roles via the admin UI
- [ ] Each is enforced by `[Authorize]` on the correct API endpoints

---

# PART 2 — BACKEND SERVICES & BUSINESS LOGIC

## 2.1 Addendum 31 — Manufacturing Flow Corrections (10 Changes)

### C1: Sale Order Min/Max Quantity Limits

**Service:** `SaleOrderLineService` (or equivalent validation layer)

**Business Rules:**

| Rule | Description | Verified |
|------|-------------|----------|
| BR-C1-01 | If `sale_order_min_qty` is set on product AND ordered qty < min → block with message showing the minimum | [ ] |
| BR-C1-02 | If `sale_order_max_qty` is set on product AND ordered qty > max → block with message showing the maximum | [ ] |
| BR-C1-03 | Min/Max validation runs at SO line add/edit, not at confirmation | [ ] |
| BR-C1-04 | NULL min/max means no limit enforced | [ ] |
| BR-C1-05 | On Product save: if both set, min must be ≤ max | [ ] |
| BR-C1-06 | Min/Max displayed as helper text on SO line form | [ ] |

- [ ] Validation implemented in correct service
- [ ] Error messages include the actual min/max values
- [ ] Cross-validation on Product save prevents min > max

---

### C2: Product Tab Consolidation

**UI Changes Only — No schema change**

- [ ] Manufacturing tab removed from Product form
- [ ] Classification tab now has two sections:
  - [ ] "Product Classification" (existing fields)
  - [ ] "Manufacturing & Supply" (moved from Manufacturing tab)
- [ ] All existing Classification fields retained
- [ ] All existing Manufacturing fields retained in new location
- [ ] No data migration needed (same underlying fields)

---

### C3: Draft PO Flow from Supply Requirement Engine

**Service:** `SupplyRequirementEngine`

**Business Rules:**

| Rule | Description | Verified |
|------|-------------|----------|
| BR-C3-01 | Engine creates PO in DRAFT status (not auto-submitted) | [ ] |
| BR-C3-02 | Default supplier selection priority: product's default supplier → last supplier used for this product → NULL (no supplier, user must select) | [ ] |
| BR-C3-03 | If no default supplier found, PO created with supplier = NULL, flagged for user attention | [ ] |
| BR-C3-04 | Notification sent to users with `purchase_order_write` permission | [ ] |
| BR-C3-05 | Draft PO includes source reference (Production Order that triggered it) | [ ] |
| BR-C3-06 | Duplicate prevention: do not create Draft PO if one already exists for same product + Production Order | [ ] |
| BR-C3-07 | Quantities on Draft PO match shortage quantities from material availability check | [ ] |

- [ ] `SupplyRequirementEngine` creates POs with `Status = DRAFT`
- [ ] Supplier resolution follows the 3-tier priority
- [ ] MediatR notification event published for purchase team
- [ ] Duplicate check queries existing Draft POs before creation

---

### C4: Inline BOM on Product Form

**UI Changes Only — No schema change**

- [ ] Standalone BOM list page removed (or hidden)
- [ ] Standalone BOM edit page removed (or hidden)
- [ ] Product form shows BOM section with two-panel layout:
  - [ ] Left panel (35%): BOM list for this product
  - [ ] Right panel (65%): BOM editor (selected BOM details)
- [ ] BOM mandatory for manufacturing products (BR-C4-01: cannot save manufacturing product without at least one BOM)
- [ ] BOM section visibility: only for products classified as manufacturing (BR-C4-02)
- [ ] Workflow buttons shown by BOM status (Draft → Active → Obsolete)
- [ ] Create New BOM button in left panel
- [ ] Delete BOM only if in Draft status

---

### C5: BOM Effective Date Auto-Default

**Application-level change only — No schema change**

- [ ] `effective_from` defaults to today's date when creating new BOM (BR-C5-01)
- [ ] Default set at application level (DTO mapping or form initialization), not DB default
- [ ] User can override the default
- [ ] Existing BOMs unaffected

---

### C6: Remove Warehouse from BOM

**Service/Entity Changes:**

- [ ] `BillOfMaterialLine` entity no longer has `WarehouseId` property (BR-C6-01)
- [ ] PMR (Production Material Requirement) warehouse always sourced from `ProductionOrder.production_warehouse_id`
- [ ] All code that previously read `BOMLine.warehouse_id` updated
- [ ] BOM line form no longer shows warehouse dropdown
- [ ] Migration M3 executed (DROP COLUMN)

---

### C7: Production Order ↔ Sale Order Link

**Service:** Production Order creation logic

- [ ] When PO created from SO confirmation, `sale_order_id` and `sale_order_line_id` populated (BR-C7-01)
- [ ] Navigation from PO detail → linked SO is functional
- [ ] Navigation from SO detail → linked PO(s) is functional
- [ ] PO list can filter by "Has Sale Order" / "Standalone"

---

### C8: Material Availability Tab

**Service:** `IMaterialReadinessService`

**Business Rules:**

| Rule | Description | Verified |
|------|-------------|----------|
| BR-C8-01 | Reserve action calls `IStockReservationService.Reserve()` for available materials | [ ] |
| BR-C8-02 | Allocation Request creates a request to source material from other warehouses or via purchase | [ ] |

**UI:**

- [ ] Material Availability tab on Production Order detail page
- [ ] "Available" section lists materials with stock, shows Reserve button
- [ ] "Shortage" section lists materials without sufficient stock, shows Allocation Request button
- [ ] "Reserve All Available" batch action button
- [ ] Allocation Request dialog with pre-filled fields (product, qty needed, preferred warehouse)
- [ ] GET `/api/production-orders/{id}/material-availability` returns availability data
- [ ] POST `/api/production-orders/{id}/material-availability/reserve` executes reservation

---

### C9: Consolidated Purchase Required Dashboard

**Service:** Purchase Required aggregation query

**Business Rules:**

| Rule | Description | Verified |
|------|-------------|----------|
| BR-C9-01 | Aggregates shortages across ALL active Production Orders per product per org | [ ] |
| BR-C9-02 | Duplicate PO prevention: if Draft PO already exists for product, show "PO Pending" badge instead of "Create PO" button | [ ] |

- [ ] GET `/api/purchase-required` endpoint implemented
- [ ] Dashboard at Manufacturing → Purchase Required menu
- [ ] Groups by product, sums shortage quantities across POs
- [ ] Shows affected PO count per product
- [ ] Clicking a product row opens detail drawer showing affected POs
- [ ] "Create Draft PO" action checks for existing Draft POs (duplicate prevention)

---

### C10: GRN → Allocation → Reservation Flow

**Services:** `IAllocationEngine`, `FGRConfirmedEventHandler`

**Business Rules:**

| Rule | Description | Verified |
|------|-------------|----------|
| BR-C10-01 | GRN receipt → allocation is MANUAL (user triggers via POST `/api/allocation/run`) | [ ] |
| BR-C10-02 | FGR (Finished Goods Receipt) → allocation is AUTOMATIC (MediatR event handler) | [ ] |
| BR-C10-03 | After allocation, material status on PO updated (from SHORTAGE to AVAILABLE or PARTIAL) | [ ] |
| BR-C10-04 | Full allocation of all materials transitions PO material status to READY | [ ] |

- [ ] POST `/api/allocation/run` endpoint exists for manual allocation
- [ ] `AllocationRunCommand` MediatR handler implemented
- [ ] `FGRConfirmedEvent` event handler performs automatic allocation
- [ ] Material status updates correctly after allocation
- [ ] Partial allocation shows as PARTIAL, full as READY

---

## 2.2 Addendum 32 — Sales Pre-Order Pipeline (5 Changes)

### C1: Sale Inquiry

**Service:** `SaleInquiryService`

**State Machine:** RECEIVED → UNDER_REVIEW → REVIEW_COMPLETE → QUOTED → DECLINED

**Business Rules:**

| Rule | Description | Verified |
|------|-------------|----------|
| BR-C1-01 | Inquiry number auto-generated: `INQ-{YYYY}-{NNNN}` (sequential per org per year) | [ ] |
| BR-C1-02 | New inquiry starts in RECEIVED status | [ ] |
| BR-C1-03 | Transition to UNDER_REVIEW requires `assigned_to` to be set | [ ] |
| BR-C1-04 | Line evaluation status starts as PENDING | [ ] |
| BR-C1-05 | REVIEW_COMPLETE requires all lines to have non-PENDING evaluation | [ ] |
| BR-C1-06 | QUOTED transition creates a linked Quotation (auto or manual) | [ ] |
| BR-C1-07 | DECLINED requires at least one line with CANNOT_SUPPLY or rejection reason | [ ] |

**API Endpoints:**

- [ ] GET `/api/sale-inquiries` — list with filters (status, customer, date range)
- [ ] GET `/api/sale-inquiries/{id}` — detail with lines
- [ ] POST `/api/sale-inquiries` — create
- [ ] PUT `/api/sale-inquiries/{id}` — update
- [ ] PUT `/api/sale-inquiries/{id}/status` — transition status
- [ ] PUT `/api/sale-inquiries/{id}/lines/{lineId}/evaluate` — set line evaluation

---

### C2: Sale Quotation

**Service:** `SaleQuotationService`

**State Machine:** DRAFT → SENT → ACCEPTED → REJECTED → EXPIRED → CONVERTED

**Business Rules:**

| Rule | Description | Verified |
|------|-------------|----------|
| BR-C2-01 | Quotation number auto-generated: `SQ-{YYYY}-{NNNN}` (sequential per org per year) | [ ] |
| BR-C2-02 | New quotation starts in DRAFT status | [ ] |
| BR-C2-03 | SENT transition requires at least one NORMAL line with price > 0 | [ ] |
| BR-C2-04 | SENT transition records `sent_at` timestamp | [ ] |
| BR-C2-05 | Customer response per line: PENDING / ACCEPTED / REJECTED / COUNTER | [ ] |
| BR-C2-06 | COUNTER response requires `counter_price` or `counter_qty` to be set | [ ] |
| BR-C2-07 | ACCEPTED status requires at least one line with `customer_response = ACCEPTED` | [ ] |
| BR-C2-08 | ALTERNATIVE lines link to parent via `parent_line_id` | [ ] |
| BR-C2-09 | REJECTED lines require `rejection_reason_id` | [ ] |
| BR-C2-10 | EXPIRED set automatically by `QuotationExpiryJob` when `valid_until < today` | [ ] |
| BR-C2-11 | CONVERTED transition creates linked SO with only ACCEPTED lines | [ ] |

**Hangfire Job:**

- [ ] `QuotationExpiryJob` registered in Hangfire
- [ ] Runs daily at 01:00 UTC
- [ ] Updates SENT quotations where `valid_until < GETUTCDATE()` to EXPIRED
- [ ] Does not expire DRAFT quotations

**Quotation → SO Conversion (BR-C2-11):**

- [ ] Only lines where `customer_response = ACCEPTED` flow to SO
- [ ] Lines with `customer_response = COUNTER` require seller acceptance of counter-price first
- [ ] SO `source_quotation_id` populated
- [ ] SO `source_type = 'FROM_QUOTATION'`
- [ ] Quotation status transitions to CONVERTED after SO creation
- [ ] Original quotation becomes read-only after CONVERTED

**API Endpoints:**

- [ ] GET `/api/sale-quotations` — list
- [ ] GET `/api/sale-quotations/{id}` — detail
- [ ] POST `/api/sale-quotations` — create
- [ ] PUT `/api/sale-quotations/{id}` — update
- [ ] PUT `/api/sale-quotations/{id}/send` — transition to SENT
- [ ] PUT `/api/sale-quotations/{id}/lines/{lineId}/response` — record customer response
- [ ] POST `/api/sale-quotations/{id}/convert` — convert to SO

---

### C3: Sale Order Source Linking & Customer PO

**Service:** `SaleOrderService` modifications

**Business Rules:**

| Rule | Description | Verified |
|------|-------------|----------|
| BR-C3-01 | `source_type` auto-set based on creation path (MANUAL, FROM_QUOTATION, PORTAL, INTER_TENANT) | [ ] |
| BR-C3-02 | `source_quotation_id` populated when created from quotation conversion | [ ] |
| BR-C3-03 | `source_inquiry_id` populated when traceable back to inquiry | [ ] |
| BR-C3-04 | `customer_po_reference` is free-text, max 50 chars | [ ] |
| BR-C3-05 | `customer_po_attachment_id` links to `SaleOrderAttachments` record | [ ] |

- [ ] SO creation from quotation sets `source_type = 'FROM_QUOTATION'`
- [ ] SO detail page shows source document link (clickable)
- [ ] Customer PO section on SO form: reference field + date picker + file upload
- [ ] Source type displayed but not user-editable
- [ ] Breadcrumb navigation: Inquiry → Quotation → Sale Order

---

### C4: Delivery Indicators & RBAC Reservation

**Service:** Delivery indicator computation (read-time, not persisted)

**Indicator Logic (computed at read time):**

| Color | Condition |
|-------|-----------|
| GREEN | Line fully fulfilled (delivered_qty >= ordered_qty) |
| BLUE | Fully reserved (reserved_qty >= ordered_qty, not yet fulfilled) |
| YELLOW | Partially reserved (0 < reserved_qty < ordered_qty) |
| RED | Nothing reserved and nothing fulfilled (reserved_qty = 0 AND delivered_qty = 0) |
| GREY | Line cancelled |

**Business Rules:**

| Rule | Description | Verified |
|------|-------------|----------|
| BR-C4-01 | Delivery indicator computed at read time (DTO mapping), NOT persisted | [ ] |
| BR-C4-02 | Reserve action requires `sales_reserve_inventory` permission | [ ] |
| BR-C4-03 | Release action requires `sales_release_reservation` permission | [ ] |
| BR-C4-04 | Reserve calls `IStockReservationService.Reserve()` with `source = SALES_ORDER` | [ ] |
| BR-C4-05 | Release calls `IStockReservationService.Release()` | [ ] |
| BR-C4-06 | `reserved_qty` updated on `SaleOrderLines` after reserve/release | [ ] |
| BR-C4-07 | SO cancellation auto-releases all reservations on that SO's lines | [ ] |
| BR-C4-08 | Reserve qty cannot exceed (ordered_qty - delivered_qty) | [ ] |

- [ ] Indicator displayed on SO line list (colored dot/badge)
- [ ] Reserve button per line (or "Reserve All" bulk action)
- [ ] Release button per line
- [ ] Permissions enforced via `[Authorize]`
- [ ] Auto-release on SO cancellation implemented

---

### C5: Rejection Reason Lookup

**Business Rules:**

| Rule | Description | Verified |
|------|-------------|----------|
| BR-C5-01 | System codes (is_system=1) cannot be deleted | [ ] |
| BR-C5-02 | Users can add custom rejection reasons (is_system=0) | [ ] |
| BR-C5-03 | Rejection reasons shared between Inquiry and Quotation lines | [ ] |

- [ ] CRUD API for rejection reasons exists
- [ ] System codes protected from deletion
- [ ] Dropdown populated in both Inquiry line and Quotation line forms
- [ ] Lookup management page accessible from Settings/Lookups

---

## 2.3 Addendum 33 — Fulfillment Routes (5 Changes)

### C1: Fulfillment Route Configuration

**Business Rules:**

| Rule | Description | Verified |
|------|-------------|----------|
| BR-C1-01 | Step codes constrained to: PICK, PACK, STAGE, APPROVAL, GOODS_ISSUE, SHIP | [ ] |
| BR-C1-02 | Steps ordered by `step_order` (sequential, no gaps required) | [ ] |
| BR-C1-03 | Exactly one route per org can be `is_default = 1` | [ ] |
| BR-C1-04 | System routes (`is_system = 1`) cannot be deleted | [ ] |
| BR-C1-05 | System routes can be deactivated (`is_active = 0`) but not deleted | [ ] |
| BR-C1-06 | Route code unique per org | [ ] |
| BR-C1-07 | At least one step required per route | [ ] |
| BR-C1-08 | PICK step should be present in all routes (warning if missing, not enforced) | [ ] |
| BR-C1-09 | Custom routes: user can create with any combination of allowed steps | [ ] |

**API Endpoints:**

- [ ] GET `/api/fulfillment-routes` — list all routes for org
- [ ] GET `/api/fulfillment-routes/{id}` — detail with steps
- [ ] POST `/api/fulfillment-routes` — create custom route
- [ ] PUT `/api/fulfillment-routes/{id}` — update route
- [ ] DELETE `/api/fulfillment-routes/{id}` — delete (only non-system)
- [ ] PUT `/api/fulfillment-routes/{id}/set-default` — mark as org default

**UI:**

- [ ] Route configuration page at Settings → Fulfillment Routes
- [ ] Route list with columns: Code, Name, Steps (visual), Default badge, Status
- [ ] Route editor: name, code, description, step builder (drag-and-drop or ordered list)
- [ ] Step builder shows available step codes as chips to add/remove/reorder
- [ ] System routes show lock icon, edit limited to activate/deactivate

---

### C2: Product Variant Route Assignment

**Business Rules:**

| Rule | Description | Verified |
|------|-------------|----------|
| BR-C2-01 | Variant `fulfillment_route_id` is optional (NULL means "use org default") | [ ] |
| BR-C2-02 | Bulk assignment API: assign route to all variants in a product category | [ ] |

- [ ] Fulfillment Route dropdown on Product Variant form
- [ ] Shows active routes only
- [ ] NULL option labeled "Use Organization Default"
- [ ] Bulk assignment endpoint: POST `/api/product-variants/bulk-assign-route`

---

### C3: SO Line Route Selection & Confirmation Gate

**Service:** `EffectiveRouteResolver`

**4-Level Route Resolution Priority:**

1. SO line explicit override (`SaleOrderLines.fulfillment_route_id`)
2. Product variant default (`ProductVariants.fulfillment_route_id`)
3. Organization default route (`FulfillmentRoutes.is_default = 1`)
4. NULL → **blocks SO confirmation**

**Business Rules:**

| Rule | Description | Verified |
|------|-------------|----------|
| BR-C3-01 | Route resolution follows the 4-level priority | [ ] |
| BR-C3-02 | SO confirmation BLOCKED if any line resolves to NULL route | [ ] |
| BR-C3-03 | Confirmation error message identifies which lines have no route | [ ] |
| BR-C3-04 | Route dropdown on SO line form shows active routes | [ ] |
| BR-C3-05 | Delivery Preview (SaleOrderDeliveryPreviewService) computed on-the-fly, not persisted | [ ] |

- [ ] `EffectiveRouteResolver` class/service implemented
- [ ] Resolution follows exact 4-level priority
- [ ] `SaleOrderService.ConfirmAsync()` calls resolver for each line
- [ ] Confirmation fails with clear error if any line has no route
- [ ] Delivery Preview endpoint: GET `/api/sale-orders/{id}/delivery-preview`
- [ ] Preview groups lines by effective route and shows expected delivery count

---

### C4: Delivery Order Creation from SO

**Service:** `SaleOrderDeliveryCreator`

**Business Rules:**

| Rule | Description | Verified |
|------|-------------|----------|
| BR-C4-01 | SO confirmation creates Delivery Orders grouped by effective fulfillment route | [ ] |
| BR-C4-02 | One DO per route group (lines with same effective route go into one DO) | [ ] |
| BR-C4-03 | DO created in DRAFT status | [ ] |
| BR-C4-04 | DO `from_source_type = 'SALE_ORDER'` | [ ] |
| BR-C4-05 | DO `sale_order_id` and `fulfillment_route_id` populated | [ ] |
| BR-C4-06 | DO lines link back via `sale_order_line_id` | [ ] |
| BR-C4-07 | SO cancellation auto-cancels linked DOs not yet GOODS_ISSUED; post-GOODS_ISSUED DOs require manual reversal | [ ] |

- [ ] `SaleOrderDeliveryCreator` service implemented
- [ ] Grouping logic: lines with same effective route → one DO
- [ ] DO created in DRAFT status
- [ ] All FK links populated (sale_order_id, fulfillment_route_id, sale_order_line_id)
- [ ] SO cancellation handler checks DO status before auto-cancelling
- [ ] Error message when trying to cancel SO with post-GOODS_ISSUED DOs

---

### C5: Route-Aware State Machine

**Service:** `RouteAwareDeliveryStateMachine`

**Step-to-Status Mapping:**

| Step Code | Statuses Covered |
|-----------|-----------------|
| PICK | RELEASED → PICKING → PICKED |
| PACK | PACKED |
| STAGE | STAGED |
| APPROVAL | PENDING_APPROVAL |
| GOODS_ISSUE | GOODS_ISSUED |
| SHIP | IN_TRANSIT → OUT_FOR_DELIVERY → DELIVERED |

**Business Rules:**

| Rule | Description | Verified |
|------|-------------|----------|
| BR-C5-01 | State machine reads route steps and skips statuses whose step_code is absent | [ ] |
| BR-C5-02 | Skipping is transparent: transition goes directly from last status of prior step to first status of next step | [ ] |
| BR-C5-03 | Route with only PICK + GOODS_ISSUE skips PACK, STAGE, APPROVAL, and all SHIP statuses | [ ] |
| BR-C5-04 | All 15 existing statuses remain in the enum; skipping is per-delivery, not per-system | [ ] |
| BR-C5-05 | Manual status override remains available for admin users | [ ] |

- [ ] `RouteAwareDeliveryStateMachine` class implemented
- [ ] Constructor or initialization loads route steps for the delivery's route
- [ ] `GetNextStatus()` method skips statuses not in the route
- [ ] `GetPreviousStatus()` method also respects route steps
- [ ] `CanTransitionTo()` method validates against route-allowed statuses
- [ ] Tested with each of the 3 seed routes (PICK_ONLY, PICK_AND_SHIP, PICK_PACK_SHIP)
- [ ] Tested with 2 manufacturing routes (MFG_PICK_SHIP, MFG_PICK_PACK_SHIP) from Addendum 34

---

## 2.4 Addendum 34 — Route Classification & Production-Delivery Bridge (6 Changes)

### C1: Route Category

**Business Rules:**

| Rule | Description | Verified |
|------|-------------|----------|
| BR-C1-01 | `route_category` must be one of: STOCK, MANUFACTURE, BUY, DROPSHIP | [ ] |
| BR-C1-02 | Existing PICK_ONLY, PICK_AND_SHIP, PICK_PACK_SHIP routes set to STOCK | [ ] |
| BR-C1-03 | New MFG_PICK_SHIP and MFG_PICK_PACK_SHIP routes set to MANUFACTURE | [ ] |
| BR-C1-04 | Route category displayed on route configuration UI and is user-selectable for custom routes | [ ] |

- [ ] Category dropdown on route create/edit form
- [ ] Category badge visible in route list
- [ ] Seed route categories correctly set
- [ ] New org creation seeds both STOCK and MANUFACTURE routes

---

### C2: Route-Based Product Classification

**Business Rules:**

| Rule | Description | Verified |
|------|-------------|----------|
| BR-C2-01 | BOM tab visible when product variant's route has `route_category = 'MANUFACTURE'` | [ ] |
| BR-C2-02 | `is_manufacturable` flag auto-synced from route category (TRUE if MANUFACTURE, FALSE otherwise) | [ ] |
| BR-C2-03 | Legacy boolean filters (`is_manufacturable`) still work for backward compatibility | [ ] |
| BR-C2-04 | Changing variant route from MANUFACTURE to STOCK hides BOM tab (but does not delete existing BOMs) | [ ] |

- [ ] BOM tab visibility logic updated from boolean check to route category check
- [ ] `is_manufacturable` set automatically when route assigned/changed
- [ ] Existing API filters using `is_manufacturable` continue to work
- [ ] BOMs preserved when route changes away from MANUFACTURE (hidden but not deleted)

---

### C3: Lead Time Management

**Service:** Lead time resolution logic

**8 Lead Time Components:**

| Component | Column | Route Types | Default |
|-----------|--------|-------------|---------|
| Supplier Lead Time | `supplier_lead_time_days` | BUY, STOCK (purchased items) | From variant |
| Manufacturing Lead Time | `manufacturing_lead_time_days` | MANUFACTURE | From variant |
| Manufacturing Buffer | `manufacturing_buffer_days` | MANUFACTURE | 0 |
| Quality Inspection | `quality_inspection_days` | All | 0 |
| Internal Transfer | `internal_transfer_days` | All (multi-warehouse) | 0 |
| Pick & Pack | `pick_pack_days` | All | 1 |
| Shipping Lead Time | `shipping_lead_time_days` | All (with shipping step) | 3 |
| Sales Buffer | `sales_buffer_days` | All | 1 |

**Business Rules:**

| Rule | Description | Verified |
|------|-------------|----------|
| BR-C3-01 | Variant-level lead times override org defaults (NULL means "use org default") | [ ] |
| BR-C3-02 | Org defaults managed via `lookups.LeadTimeDefaults` (one row per org) | [ ] |
| BR-C3-03 | Lead time component visibility on variant form driven by route type | [ ] |
| BR-C3-04 | MANUFACTURE route shows: manufacturing_lead_time_days, manufacturing_buffer_days, quality_inspection_days, pick_pack_days, shipping, sales_buffer | [ ] |
| BR-C3-05 | STOCK route shows: supplier_lead_time_days, quality_inspection_days, pick_pack_days, shipping, sales_buffer | [ ] |

- [ ] Lead Time Defaults management page at Settings → Lead Time Defaults
- [ ] Requires `lead_time_defaults_manage` permission
- [ ] Variant form shows lead time fields appropriate to route type
- [ ] Resolution: variant override → org default → hardcoded fallback
- [ ] Org defaults seeded for existing orgs

---

### C4: Lead Time Calculation (BOM-Aware)

**Service:** `ILeadTimeCalculator`

**Algorithm:**

```
CalculateLeadTime(variant_id, quantity):
  1. Check stock: if variant has sufficient stock → lead = 0, return
  2. Get route category for variant
  3. If MANUFACTURE:
     a. Get BOM for variant
     b. For each sub-component:
        - Recursively calculate lead time (parallel → take MAX)
     c. Get this-level operations:
        - Sum: manufacturing_lead_time + mfg_buffer + quality + internal_transfer
     d. Convert BOM operation times: 480 min = 1 working day
     e. Total = MAX(sub-component leads) + this-level sum + pick_pack + shipping + sales_buffer
  4. If STOCK/BUY:
     - Total = supplier_lead_time + quality + internal_transfer + pick_pack + shipping + sales_buffer
  5. Cache result for 5 minutes per (variant_id, quantity_bucket)
```

**Business Rules:**

| Rule | Description | Verified |
|------|-------------|----------|
| BR-C4-01 | Recursive BOM traversal: sub-components run in parallel (take MAX of their lead times) | [ ] |
| BR-C4-02 | This-level operations are sequential (SUM) | [ ] |
| BR-C4-03 | Stock-aware: if component has sufficient stock, its lead time = 0 | [ ] |
| BR-C4-04 | Cycle detection via `visited` HashSet — cycle returns 0 and logs warning | [ ] |
| BR-C4-05 | 5-minute cache per `(variant_id, quantity_bucket)` using `IMemoryCache` | [ ] |
| BR-C4-06 | BOM operation times converted at 480 minutes per working day | [ ] |

- [ ] `ILeadTimeCalculator` interface defined
- [ ] `LeadTimeCalculator` implementation with recursive BOM traversal
- [ ] Cycle detection prevents infinite recursion
- [ ] Cache layer with 5-minute TTL
- [ ] Stock check integrated (calls inventory service)
- [ ] Calculation triggered when:
  - [ ] Inquiry line added/changed → `calculated_lead_time_days` and `calculated_delivery_date` updated
  - [ ] Quotation line added/changed → same columns updated
  - [ ] SO line added/changed → same columns updated
- [ ] `calculated_delivery_date = today + calculated_lead_time_days`
- [ ] `effective_delivery_date` computed column works correctly (manual overrides calculated)

---

### C5: SO Confirmation Split by Route Category

**Service:** `SaleOrderService.ConfirmAsync()` (modified)

**Split Logic:**

```
ConfirmAsync(saleOrderId):
  1. Resolve effective route for each line (EffectiveRouteResolver)
  2. Validate: all lines must have a route (else block — from Addendum 33)
  3. Group lines by route_category:
     - STOCK lines → SaleOrderDeliveryCreator (existing from Addendum 33)
     - MANUFACTURE lines → SaleOrderProductionCreator (NEW)
  4. Mixed orders: process both groups
```

**Business Rules:**

| Rule | Description | Verified |
|------|-------------|----------|
| BR-C5-01 | STOCK lines → Delivery Order creation (existing flow) | [ ] |
| BR-C5-02 | MANUFACTURE lines → Production Order creation (new flow) | [ ] |
| BR-C5-03 | Mixed orders process both — some lines create DOs, others create POs | [ ] |
| BR-C5-04 | `SaleOrderProductionCreator` creates one PO per manufacturing line | [ ] |
| BR-C5-05 | PO includes: sale_order_id, sale_order_line_id, fulfillment_route_id | [ ] |
| BR-C5-06 | BOM explosion triggered on PO creation | [ ] |

**SaleOrderProductionCreator Details:**

- [ ] Service class `SaleOrderProductionCreator` implemented
- [ ] Creates `ProductionOrder` with:
  - [ ] `sale_order_id` = SO id
  - [ ] `sale_order_line_id` = SO line id
  - [ ] `fulfillment_route_id` = line's effective route id
  - [ ] `planned_qty` = SO line quantity
  - [ ] `product_variant_id` = SO line product
- [ ] BOM explosion: creates PMR lines from active BOM for the product
- [ ] Backward scheduling: `planned_start_date = effective_delivery_date - calculated_lead_time_days`
- [ ] PO created in DRAFT (or PLANNED) status
- [ ] If no active BOM found for a MANUFACTURE product → error, confirmation fails for that line

---

### C6: Production → Delivery Bridge

**Service:** `FGRConfirmedEventHandler` (extended), `ProductionDeliveryCreator` (new)

**Flow:**

```
Production Order Complete (FGR Confirmed)
  ↓
FGRConfirmedEventHandler
  ├── Existing: Automatic allocation of finished goods
  └── NEW: If PO has sale_order_id AND fulfillment_route_id
      ↓
      ProductionDeliveryCreator
      ├── Creates Delivery Order from completed PO
      ├── delivery_qty = PO.actual_qty (reflects actual yield)
      ├── DO.fulfillment_route_id = PO.fulfillment_route_id
      ├── DO.sale_order_id = PO.sale_order_id
      ├── PO.delivery_order_id = new DO id
      └── Handles shortfall (actual < planned)
```

**Business Rules:**

| Rule | Description | Verified |
|------|-------------|----------|
| BR-C6-01 | FGR confirmed triggers automatic allocation (existing behavior preserved) | [ ] |
| BR-C6-02 | If PO has `sale_order_id` AND `fulfillment_route_id`, auto-create Delivery Order | [ ] |
| BR-C6-03 | `delivery_qty = PO.actual_qty` (actual yield, not planned qty) | [ ] |
| BR-C6-04 | Shortfall: if `actual_qty < planned_qty`, create delivery for actual qty + send notification | [ ] |
| BR-C6-05 | Zero production: if `actual_qty = 0`, no delivery created + error notification sent | [ ] |
| BR-C6-06 | `PO.delivery_order_id` populated for traceability | [ ] |
| BR-C6-07 | DO `from_source_type = 'PRODUCTION_ORDER'` or extended source type | [ ] |
| BR-C6-08 | MTS escape hatch: salesperson overrides manufacturing route to stock route on SO line → delivery created immediately from stock (bypasses production) | [ ] |
| BR-C6-09 | Partial production notification includes: PO number, planned vs actual qty, shortfall amount | [ ] |

- [ ] `FGRConfirmedEventHandler` extended with delivery creation logic
- [ ] `ProductionDeliveryCreator` service implemented
- [ ] Delivery quantity based on actual production yield
- [ ] Shortfall handling: delivery for actual + notification
- [ ] Zero production handling: no delivery + error notification
- [ ] PO ↔ DO bidirectional link established
- [ ] MTS escape hatch: route override on SO line skips production entirely
- [ ] Notification service called for shortfall/zero scenarios

---

# PART 3 — API ENDPOINTS CONSOLIDATED

## 3.1 New Endpoints

| # | Method | Endpoint | Addendum | Permission Required |
|---|--------|----------|----------|-------------------|
| 1 | GET | `/api/purchase-required` | 31 | `purchase_order_view` |
| 2 | GET | `/api/production-orders/{id}/material-availability` | 31 | (production order view) |
| 3 | POST | `/api/production-orders/{id}/material-availability/reserve` | 31 | (production order edit) |
| 4 | POST | `/api/allocation/run` | 31 | (inventory manage) |
| 5 | GET | `/api/sale-inquiries` | 32 | `sales_inquiry_view` |
| 6 | GET | `/api/sale-inquiries/{id}` | 32 | `sales_inquiry_view` |
| 7 | POST | `/api/sale-inquiries` | 32 | `sales_inquiry_create` |
| 8 | PUT | `/api/sale-inquiries/{id}` | 32 | `sales_inquiry_edit` |
| 9 | PUT | `/api/sale-inquiries/{id}/status` | 32 | `sales_inquiry_edit` |
| 10 | PUT | `/api/sale-inquiries/{id}/lines/{lineId}/evaluate` | 32 | `sales_inquiry_edit` |
| 11 | GET | `/api/sale-quotations` | 32 | `sales_quotation_view` |
| 12 | GET | `/api/sale-quotations/{id}` | 32 | `sales_quotation_view` |
| 13 | POST | `/api/sale-quotations` | 32 | `sales_quotation_create` |
| 14 | PUT | `/api/sale-quotations/{id}` | 32 | `sales_quotation_edit` |
| 15 | PUT | `/api/sale-quotations/{id}/send` | 32 | `sales_quotation_send` |
| 16 | PUT | `/api/sale-quotations/{id}/lines/{lineId}/response` | 32 | `sales_quotation_edit` |
| 17 | POST | `/api/sale-quotations/{id}/convert` | 32 | `sales_order_create` |
| 18 | GET | `/api/fulfillment-routes` | 33 | `fulfillment_route_view` |
| 19 | GET | `/api/fulfillment-routes/{id}` | 33 | `fulfillment_route_view` |
| 20 | POST | `/api/fulfillment-routes` | 33 | `fulfillment_route_manage` |
| 21 | PUT | `/api/fulfillment-routes/{id}` | 33 | `fulfillment_route_manage` |
| 22 | DELETE | `/api/fulfillment-routes/{id}` | 33 | `fulfillment_route_manage` |
| 23 | PUT | `/api/fulfillment-routes/{id}/set-default` | 33 | `fulfillment_route_manage` |
| 24 | POST | `/api/product-variants/bulk-assign-route` | 33 | `fulfillment_route_assign` |
| 25 | GET | `/api/sale-orders/{id}/delivery-preview` | 33 | `sales_order_view` |
| 26 | GET/PUT | `/api/lead-time-defaults` | 34 | `lead_time_defaults_manage` |

- [ ] All 26+ new endpoints exist and return correct responses
- [ ] Each endpoint has appropriate `[Authorize]` attribute
- [ ] Multi-tenancy filter (org_id) applied in all queries
- [ ] Pagination implemented on list endpoints
- [ ] Proper error responses (400, 404, 403)

## 3.2 Modified Endpoints

| Method | Endpoint | Modification | Addendum |
|--------|----------|-------------|----------|
| POST/PUT | `/api/sale-order-lines` | Min/Max qty validation | 31 |
| GET | `/api/products/{id}` | Returns BOM inline, min/max fields | 31 |
| POST | `/api/sale-orders/{id}/confirm` | Route resolution + split by category + confirmation gate | 33, 34 |
| GET | `/api/sale-orders/{id}` | Returns source linking, delivery indicators, route info | 32, 33 |
| GET | `/api/product-variants/{id}` | Returns fulfillment_route_id, lead time columns | 33, 34 |

- [ ] All modified endpoints reflect the new behavior
- [ ] Backward compatibility maintained for existing consumers

---

# PART 4 — UI COMPONENTS & WIREFRAMES

## 4.1 Addendum 31 UI (5 Wireframes)

| # | Component | Description | Verified |
|---|-----------|-------------|----------|
| W1 | Product Form — Classification Tab | Two sections: Product Classification + Manufacturing & Supply | [ ] |
| W2 | Product Form — BOM Panel | Two-panel layout: 35% BOM list + 65% BOM editor | [ ] |
| W3 | Production Order — Material Availability Tab | Available section + Shortage section + Reserve/Request buttons | [ ] |
| W4 | Purchase Required Dashboard | Aggregated shortage list with affected POs drawer | [ ] |
| W5 | Allocation Run Dialog | Manual allocation trigger with warehouse/product filters | [ ] |

---

## 4.2 Addendum 32 UI (6 Wireframes)

| # | Component | Description | Verified |
|---|-----------|-------------|----------|
| W1 | Sale Inquiry List | Grid with status filter, customer filter, date range | [ ] |
| W2 | Sale Inquiry Form | Header + line grid with evaluation columns | [ ] |
| W3 | Sale Quotation Form | Header + line grid with line_type, alternatives, customer response | [ ] |
| W4 | Sale Order — Source & Customer PO Section | Source document link + customer PO fields | [ ] |
| W5 | Sale Order — Delivery Indicators | Colored dots on line grid + Reserve/Release buttons | [ ] |
| W6 | Rejection Reasons Management | Settings page CRUD for rejection reasons | [ ] |

---

## 4.3 Addendum 33 UI (4 Wireframes)

| # | Component | Description | Verified |
|---|-----------|-------------|----------|
| W1 | Fulfillment Route Configuration | Route list + route editor with step builder | [ ] |
| W2 | Product Variant — Route Assignment | Route dropdown on variant form | [ ] |
| W3 | Sale Order Line — Route Selector | Route dropdown per SO line + effective route display | [ ] |
| W4 | Delivery Preview Panel | Grouped preview showing expected DOs by route before confirmation | [ ] |

---

## 4.4 Addendum 34 UI (5 Wireframes)

| # | Component | Description | Verified |
|---|-----------|-------------|----------|
| W1 | Route Configuration — Category Column | Category dropdown added to route create/edit | [ ] |
| W2 | Product Variant — Lead Time Section | Conditional lead time fields by route type | [ ] |
| W3 | Lead Time Defaults Settings Page | Org-level default management form | [ ] |
| W4 | SO Confirmation — Split Summary | Preview showing which lines create DOs vs POs | [ ] |
| W5 | Production Order — Delivery Link | DO link on PO detail, shortfall notification area | [ ] |

---

# PART 5 — BUSINESS RULES MASTER INDEX (107 Total)

## 5.1 Addendum 31 Business Rules (24)

| Rule ID | Change | Summary | Verified |
|---------|--------|---------|----------|
| BR-C1-01 | C1 | Min qty validation on SO line add/edit | [ ] |
| BR-C1-02 | C1 | Max qty validation on SO line add/edit | [ ] |
| BR-C1-03 | C1 | Validation at line level, not confirmation | [ ] |
| BR-C1-04 | C1 | NULL means no limit | [ ] |
| BR-C1-05 | C1 | Cross-validation: min ≤ max on Product save | [ ] |
| BR-C1-06 | C1 | Min/Max displayed as helper text on SO line form | [ ] |
| BR-C3-01 | C3 | Engine creates PO in DRAFT (not auto-submitted) | [ ] |
| BR-C3-02 | C3 | Supplier priority: default → last used → NULL | [ ] |
| BR-C3-03 | C3 | No supplier: PO created with NULL, flagged | [ ] |
| BR-C3-04 | C3 | Notification to purchase_order_write users | [ ] |
| BR-C3-05 | C3 | Draft PO includes Production Order reference | [ ] |
| BR-C3-06 | C3 | Duplicate prevention for same product + PO | [ ] |
| BR-C3-07 | C3 | Quantities match shortage amounts | [ ] |
| BR-C4-01 | C4 | BOM mandatory for manufacturing products | [ ] |
| BR-C4-02 | C4 | BOM section visible only for manufacturing products | [ ] |
| BR-C6-01 | C6 | PMR warehouse ALWAYS from ProductionOrder.production_warehouse_id | [ ] |
| BR-C7-01 | C7 | sale_order_id and sale_order_line_id populated on PO creation | [ ] |
| BR-C8-01 | C8 | Reserve calls IStockReservationService.Reserve() | [ ] |
| BR-C8-02 | C8 | Allocation Request creates sourcing request | [ ] |
| BR-C9-01 | C9 | Aggregates shortages across all active POs per product | [ ] |
| BR-C9-02 | C9 | Duplicate PO prevention with "PO Pending" badge | [ ] |
| BR-C10-01 | C10 | GRN → allocation is MANUAL | [ ] |
| BR-C10-02 | C10 | FGR → allocation is AUTOMATIC | [ ] |
| BR-C10-03 | C10 | Material status update after allocation (SHORTAGE → AVAILABLE/PARTIAL) | [ ] |
| BR-C10-04 | C10 | Full allocation → READY status | [ ] |

## 5.2 Addendum 32 Business Rules (28)

| Rule ID | Change | Summary | Verified |
|---------|--------|---------|----------|
| BR-C1-01 | C1 | Inquiry number: INQ-{YYYY}-{NNNN} | [ ] |
| BR-C1-02 | C1 | New inquiry starts RECEIVED | [ ] |
| BR-C1-03 | C1 | UNDER_REVIEW requires assigned_to | [ ] |
| BR-C1-04 | C1 | Line evaluation starts PENDING | [ ] |
| BR-C1-05 | C1 | REVIEW_COMPLETE requires all lines non-PENDING | [ ] |
| BR-C1-06 | C1 | QUOTED creates linked Quotation | [ ] |
| BR-C1-07 | C1 | DECLINED requires CANNOT_SUPPLY or rejection reason | [ ] |
| BR-C2-01 | C2 | Quotation number: SQ-{YYYY}-{NNNN} | [ ] |
| BR-C2-02 | C2 | New quotation starts DRAFT | [ ] |
| BR-C2-03 | C2 | SENT requires ≥1 NORMAL line with price > 0 | [ ] |
| BR-C2-04 | C2 | SENT records sent_at timestamp | [ ] |
| BR-C2-05 | C2 | Customer response: PENDING/ACCEPTED/REJECTED/COUNTER | [ ] |
| BR-C2-06 | C2 | COUNTER requires counter_price or counter_qty | [ ] |
| BR-C2-07 | C2 | ACCEPTED status requires ≥1 ACCEPTED line | [ ] |
| BR-C2-08 | C2 | ALTERNATIVE lines via parent_line_id | [ ] |
| BR-C2-09 | C2 | REJECTED lines require rejection_reason_id | [ ] |
| BR-C2-10 | C2 | QuotationExpiryJob: SENT → EXPIRED when valid_until < today | [ ] |
| BR-C2-11 | C2 | CONVERTED creates SO with ACCEPTED lines only | [ ] |
| BR-C3-01 | C3 | source_type auto-set by creation path | [ ] |
| BR-C3-02 | C3 | source_quotation_id populated on conversion | [ ] |
| BR-C3-03 | C3 | source_inquiry_id populated when traceable | [ ] |
| BR-C3-04 | C3 | customer_po_reference max 50 chars | [ ] |
| BR-C3-05 | C3 | customer_po_attachment_id links to attachment | [ ] |
| BR-C4-01 | C4 | Delivery indicator computed at read time | [ ] |
| BR-C4-02 | C4 | Reserve requires sales_reserve_inventory | [ ] |
| BR-C4-03 | C4 | Release requires sales_release_reservation | [ ] |
| BR-C4-04 | C4 | Reserve calls IStockReservationService with SALES_ORDER source | [ ] |
| BR-C4-05 | C4 | Release calls IStockReservationService.Release() | [ ] |
| BR-C4-06 | C4 | reserved_qty updated on SaleOrderLines | [ ] |
| BR-C4-07 | C4 | SO cancel auto-releases reservations | [ ] |
| BR-C4-08 | C4 | Reserve qty ≤ (ordered - delivered) | [ ] |

## 5.3 Addendum 33 Business Rules (25)

| Rule ID | Change | Summary | Verified |
|---------|--------|---------|----------|
| BR-C1-01 | C1 | Step codes: PICK/PACK/STAGE/APPROVAL/GOODS_ISSUE/SHIP | [ ] |
| BR-C1-02 | C1 | Steps ordered by step_order | [ ] |
| BR-C1-03 | C1 | One default route per org | [ ] |
| BR-C1-04 | C1 | System routes cannot be deleted | [ ] |
| BR-C1-05 | C1 | System routes can be deactivated | [ ] |
| BR-C1-06 | C1 | Route code unique per org | [ ] |
| BR-C1-07 | C1 | At least one step per route | [ ] |
| BR-C1-08 | C1 | PICK step recommended (warning if absent) | [ ] |
| BR-C1-09 | C1 | Custom routes with any step combination | [ ] |
| BR-C2-01 | C2 | Variant route_id NULL = use org default | [ ] |
| BR-C2-02 | C2 | Bulk route assignment by category | [ ] |
| BR-C3-01 | C3 | 4-level route resolution priority | [ ] |
| BR-C3-02 | C3 | NULL route blocks confirmation | [ ] |
| BR-C3-03 | C3 | Error identifies lines with no route | [ ] |
| BR-C3-04 | C3 | Route dropdown shows active routes | [ ] |
| BR-C3-05 | C3 | Delivery Preview computed on-the-fly | [ ] |
| BR-C4-01 | C4 | SO confirmation creates DOs grouped by route | [ ] |
| BR-C4-02 | C4 | One DO per route group | [ ] |
| BR-C4-03 | C4 | DO created in DRAFT status | [ ] |
| BR-C4-04 | C4 | DO from_source_type = SALE_ORDER | [ ] |
| BR-C4-05 | C4 | DO sale_order_id and fulfillment_route_id populated | [ ] |
| BR-C4-06 | C4 | DO lines link via sale_order_line_id | [ ] |
| BR-C4-07 | C4 | Cancel SO → cancel DOs not yet GOODS_ISSUED | [ ] |
| BR-C5-01 | C5 | State machine skips statuses not in route | [ ] |
| BR-C5-02 | C5 | Skipping is transparent (direct transition) | [ ] |
| BR-C5-03 | C5 | PICK_ONLY: skips PACK, STAGE, APPROVAL, SHIP statuses | [ ] |
| BR-C5-04 | C5 | All 15 statuses remain in enum | [ ] |
| BR-C5-05 | C5 | Admin manual override still available | [ ] |

## 5.4 Addendum 34 Business Rules (30)

| Rule ID | Change | Summary | Verified |
|---------|--------|---------|----------|
| BR-C1-01 | C1 | route_category: STOCK/MANUFACTURE/BUY/DROPSHIP | [ ] |
| BR-C1-02 | C1 | Existing routes → STOCK | [ ] |
| BR-C1-03 | C1 | New manufacturing routes → MANUFACTURE | [ ] |
| BR-C1-04 | C1 | Category displayed and user-selectable for custom routes | [ ] |
| BR-C2-01 | C2 | BOM visible when route_category = MANUFACTURE | [ ] |
| BR-C2-02 | C2 | is_manufacturable auto-synced from route | [ ] |
| BR-C2-03 | C2 | Legacy boolean filters preserved | [ ] |
| BR-C2-04 | C2 | Route change hides BOM tab, preserves data | [ ] |
| BR-C3-01 | C3 | Variant overrides org defaults (NULL = use org) | [ ] |
| BR-C3-02 | C3 | One LeadTimeDefaults row per org | [ ] |
| BR-C3-03 | C3 | Field visibility by route type | [ ] |
| BR-C3-04 | C3 | MANUFACTURE shows: mfg, mfg_buffer, quality, pick_pack, shipping, sales_buffer | [ ] |
| BR-C3-05 | C3 | STOCK shows: supplier, quality, pick_pack, shipping, sales_buffer | [ ] |
| BR-C4-01 | C4 | BOM traversal: sub-components parallel (MAX) | [ ] |
| BR-C4-02 | C4 | This-level operations sequential (SUM) | [ ] |
| BR-C4-03 | C4 | Stock-aware: stock available → lead = 0 | [ ] |
| BR-C4-04 | C4 | Cycle detection → return 0 + log warning | [ ] |
| BR-C4-05 | C4 | 5-min cache per (variant_id, qty_bucket) | [ ] |
| BR-C4-06 | C4 | BOM operations: 480 min = 1 working day | [ ] |
| BR-C5-01 | C5 | STOCK lines → SaleOrderDeliveryCreator | [ ] |
| BR-C5-02 | C5 | MANUFACTURE lines → SaleOrderProductionCreator | [ ] |
| BR-C5-03 | C5 | Mixed orders process both groups | [ ] |
| BR-C5-04 | C5 | One PO per manufacturing line | [ ] |
| BR-C5-05 | C5 | PO includes: sale_order_id, sale_order_line_id, fulfillment_route_id | [ ] |
| BR-C5-06 | C5 | BOM explosion triggered on PO creation | [ ] |
| BR-C6-01 | C6 | FGR triggers automatic allocation (existing) | [ ] |
| BR-C6-02 | C6 | FGR auto-creates DO if PO has SO link + route | [ ] |
| BR-C6-03 | C6 | delivery_qty = actual_qty (actual yield) | [ ] |
| BR-C6-04 | C6 | Shortfall: create delivery for actual + notify | [ ] |
| BR-C6-05 | C6 | Zero production: no delivery + error notification | [ ] |
| BR-C6-06 | C6 | PO.delivery_order_id populated | [ ] |
| BR-C6-07 | C6 | DO from_source_type extended for production source | [ ] |
| BR-C6-08 | C6 | MTS escape hatch: stock route override skips production | [ ] |
| BR-C6-09 | C6 | Shortfall notification: PO number, planned vs actual, shortfall | [ ] |

---

# PART 6 — TEST SCENARIOS MASTER INDEX (128 Total)

## 6.1 Addendum 31 Test Scenarios (29)

| # | Scenario | Expected Result | Verified |
|---|----------|----------------|----------|
| 1 | Add SO line with qty < product min_qty | Blocked with validation message | [ ] |
| 2 | Add SO line with qty > product max_qty | Blocked with validation message | [ ] |
| 3 | Add SO line with qty within range | Accepted | [ ] |
| 4 | Add SO line with NULL min/max on product | Accepted (no limit) | [ ] |
| 5 | Save product with min > max | Blocked with cross-validation error | [ ] |
| 6 | View Product form — Classification tab | Shows both sections (Classification + Manufacturing) | [ ] |
| 7 | Supply Requirement Engine triggered | Draft PO created (not submitted) | [ ] |
| 8 | Product has default supplier | Draft PO uses default supplier | [ ] |
| 9 | Product has no default but has past POs | Draft PO uses last supplier | [ ] |
| 10 | Product has no supplier history | Draft PO created with NULL supplier | [ ] |
| 11 | Draft PO already exists for product+PO | No duplicate created | [ ] |
| 12 | Notification sent to purchase team | Users with purchase_order_write receive notification | [ ] |
| 13 | View BOM on Product form | Two-panel layout, BOM list + editor | [ ] |
| 14 | Manufacturing product without BOM | Save blocked, BOM required | [ ] |
| 15 | Create new BOM | effective_from defaults to today | [ ] |
| 16 | BOM line form | No warehouse dropdown present | [ ] |
| 17 | PMR creation | Warehouse from PO.production_warehouse_id | [ ] |
| 18 | PO created from SO confirmation | sale_order_id and sale_order_line_id set | [ ] |
| 19 | Material Availability — all available | "Available" section populated, Reserve button shown | [ ] |
| 20 | Material Availability — shortage | "Shortage" section populated, Allocation button shown | [ ] |
| 21 | Reserve All Available | All available materials reserved via IStockReservationService | [ ] |
| 22 | Purchase Required dashboard | Aggregated shortages grouped by product | [ ] |
| 23 | Create PO from dashboard (no duplicate exists) | Draft PO created | [ ] |
| 24 | Create PO from dashboard (duplicate exists) | "PO Pending" badge shown, creation blocked | [ ] |
| 25 | GRN received | Manual allocation available via /api/allocation/run | [ ] |
| 26 | Run manual allocation | Materials allocated, PO material status updated | [ ] |
| 27 | FGR confirmed | Automatic allocation triggered by event handler | [ ] |
| 28 | Full allocation | PO material status → READY | [ ] |
| 29 | Partial allocation | PO material status → PARTIAL | [ ] |

## 6.2 Addendum 32 Test Scenarios (34)

| # | Scenario | Expected Result | Verified |
|---|----------|----------------|----------|
| 1 | Create Sale Inquiry | Inquiry created with status RECEIVED, number INQ-xxxx | [ ] |
| 2 | Transition to UNDER_REVIEW without assigned_to | Blocked | [ ] |
| 3 | Transition to UNDER_REVIEW with assigned_to | Succeeds | [ ] |
| 4 | Evaluate line as CAN_SUPPLY | Line evaluation updated | [ ] |
| 5 | Evaluate line as CANNOT_SUPPLY | Rejection reason required | [ ] |
| 6 | Transition to REVIEW_COMPLETE with PENDING lines | Blocked | [ ] |
| 7 | Transition to REVIEW_COMPLETE, all evaluated | Succeeds | [ ] |
| 8 | Transition to QUOTED | Linked quotation created | [ ] |
| 9 | Create Sale Quotation | Quotation created with status DRAFT, number SQ-xxxx | [ ] |
| 10 | Add ALTERNATIVE line | parent_line_id set, line_type = ALTERNATIVE | [ ] |
| 11 | Transition to SENT without valid lines | Blocked (needs ≥1 NORMAL with price) | [ ] |
| 12 | Transition to SENT with valid lines | Succeeds, sent_at recorded | [ ] |
| 13 | Set customer response COUNTER without counter values | Blocked | [ ] |
| 14 | Set customer response COUNTER with counter_price | Succeeds | [ ] |
| 15 | Set customer response ACCEPTED | Line marked accepted | [ ] |
| 16 | Transition to ACCEPTED without any accepted lines | Blocked | [ ] |
| 17 | Transition to ACCEPTED with accepted lines | Succeeds | [ ] |
| 18 | QuotationExpiryJob — SENT quotation past valid_until | Status → EXPIRED | [ ] |
| 19 | QuotationExpiryJob — DRAFT quotation past valid_until | Not affected (stays DRAFT) | [ ] |
| 20 | Convert quotation to SO | SO created with ACCEPTED lines only, source_type = FROM_QUOTATION | [ ] |
| 21 | Convert quotation — COUNTER lines without seller acceptance | Not included in SO | [ ] |
| 22 | Quotation after conversion | Status = CONVERTED, read-only | [ ] |
| 23 | Create SO manually | source_type = MANUAL, no source links | [ ] |
| 24 | View SO with source quotation | Source document link clickable | [ ] |
| 25 | Set customer PO reference | Saved to customer_po_reference field | [ ] |
| 26 | Delivery indicator — fully fulfilled line | GREEN dot | [ ] |
| 27 | Delivery indicator — fully reserved, not fulfilled | BLUE dot | [ ] |
| 28 | Delivery indicator — partially reserved | YELLOW dot | [ ] |
| 29 | Delivery indicator — nothing reserved or fulfilled | RED dot | [ ] |
| 30 | Delivery indicator — cancelled line | GREY dot | [ ] |
| 31 | Reserve inventory on SO line | reserved_qty updated, indicator changes | [ ] |
| 32 | Release reservation | reserved_qty decremented, indicator changes | [ ] |
| 33 | Cancel SO with reservations | All reservations auto-released | [ ] |
| 34 | Reserve without permission | 403 Forbidden | [ ] |

## 6.3 Addendum 33 Test Scenarios (30)

| # | Scenario | Expected Result | Verified |
|---|----------|----------------|----------|
| 1 | View seed routes | 3 routes displayed (PICK_ONLY, PICK_AND_SHIP, PICK_PACK_SHIP) | [ ] |
| 2 | Create custom route | Route created with specified steps | [ ] |
| 3 | Delete system route | Blocked (is_system protection) | [ ] |
| 4 | Deactivate system route | Succeeds (is_active = 0) | [ ] |
| 5 | Set default route | Previous default unset, new default set | [ ] |
| 6 | Create route without steps | Blocked (at least 1 step required) | [ ] |
| 7 | Create route with duplicate code | Blocked (unique constraint) | [ ] |
| 8 | Assign route to product variant | fulfillment_route_id set | [ ] |
| 9 | Bulk assign route to category | All variants in category updated | [ ] |
| 10 | Variant with NULL route | Resolves to org default | [ ] |
| 11 | SO line with explicit route | Uses line-level route (priority 1) | [ ] |
| 12 | SO line without route, variant has route | Uses variant route (priority 2) | [ ] |
| 13 | SO line without route, variant without route | Uses org default (priority 3) | [ ] |
| 14 | SO line resolves to NULL (no default) | Confirmation blocked with error | [ ] |
| 15 | Confirmation error message | Lists specific lines with no route | [ ] |
| 16 | Delivery Preview | Shows grouped DOs by route (computed, not saved) | [ ] |
| 17 | Confirm SO — all lines same route | 1 Delivery Order created | [ ] |
| 18 | Confirm SO — lines with 2 different routes | 2 Delivery Orders created | [ ] |
| 19 | Confirm SO — lines with 3 different routes | 3 Delivery Orders created | [ ] |
| 20 | DO created | Status = DRAFT, from_source_type = SALE_ORDER | [ ] |
| 21 | DO links | sale_order_id, fulfillment_route_id populated | [ ] |
| 22 | DO lines | sale_order_line_id populated | [ ] |
| 23 | Cancel SO — DOs in DRAFT | DOs auto-cancelled | [ ] |
| 24 | Cancel SO — DO in PICKING | DO auto-cancelled | [ ] |
| 25 | Cancel SO — DO is GOODS_ISSUED | Error: manual reversal required | [ ] |
| 26 | PICK_ONLY route state machine | RELEASED → PICKING → PICKED → GOODS_ISSUED → COMPLETED | [ ] |
| 27 | PICK_AND_SHIP route state machine | RELEASED → PICKING → PICKED → GOODS_ISSUED → IN_TRANSIT → ... → DELIVERED | [ ] |
| 28 | PICK_PACK_SHIP route state machine | RELEASED → PICKING → PICKED → PACKED → GOODS_ISSUED → IN_TRANSIT → ... → DELIVERED | [ ] |
| 29 | Status skip — no PACK step | Transition from PICKED directly to GOODS_ISSUED | [ ] |
| 30 | Admin manual status override | Override works regardless of route | [ ] |

## 6.4 Addendum 34 Test Scenarios (35)

| # | Scenario | Expected Result | Verified |
|---|----------|----------------|----------|
| 1 | View routes — category column | STOCK/MANUFACTURE labels displayed | [ ] |
| 2 | Create custom route with MANUFACTURE category | Route created with correct category | [ ] |
| 3 | Seed routes category values | PICK_ONLY/PICK_AND_SHIP/PICK_PACK_SHIP = STOCK; MFG_* = MANUFACTURE | [ ] |
| 4 | New org creation | Both STOCK and MANUFACTURE routes seeded | [ ] |
| 5 | Variant with MANUFACTURE route | BOM tab visible | [ ] |
| 6 | Variant with STOCK route | BOM tab hidden | [ ] |
| 7 | Change variant route from MANUFACTURE to STOCK | BOM tab hidden, existing BOMs preserved | [ ] |
| 8 | is_manufacturable sync | TRUE when route = MANUFACTURE, FALSE otherwise | [ ] |
| 9 | Legacy filter is_manufacturable=true | Returns variants with MANUFACTURE route | [ ] |
| 10 | Lead Time Defaults — view | Shows org defaults with current values | [ ] |
| 11 | Lead Time Defaults — update | Values saved, requires lead_time_defaults_manage | [ ] |
| 12 | Variant lead time — MANUFACTURE route | Shows mfg fields, hides supplier field | [ ] |
| 13 | Variant lead time — STOCK route | Shows supplier field, hides mfg fields | [ ] |
| 14 | Lead time resolution — variant override set | Uses variant value | [ ] |
| 15 | Lead time resolution — variant NULL | Falls back to org default | [ ] |
| 16 | Calculate lead time — simple product (no BOM) | Sum of applicable components | [ ] |
| 17 | Calculate lead time — product with BOM | Recursive: MAX(sub-components) + SUM(this-level) | [ ] |
| 18 | Calculate lead time — sub-component has stock | Sub-component lead = 0 | [ ] |
| 19 | Calculate lead time — BOM cycle | Returns 0, logs warning | [ ] |
| 20 | Calculate lead time — caching | Second call within 5 min returns cached result | [ ] |
| 21 | Inquiry line — lead time calculated | calculated_lead_time_days and calculated_delivery_date populated | [ ] |
| 22 | Quotation line — lead time calculated | Same columns populated | [ ] |
| 23 | SO line — lead time calculated | Same columns populated | [ ] |
| 24 | effective_delivery_date — no manual override | = calculated_delivery_date | [ ] |
| 25 | effective_delivery_date — manual override set | = manual_delivery_date | [ ] |
| 26 | Confirm SO — all STOCK lines | Only DOs created (existing flow) | [ ] |
| 27 | Confirm SO — all MANUFACTURE lines | Only POs created (new flow) | [ ] |
| 28 | Confirm SO — mixed STOCK + MANUFACTURE | Both DOs and POs created | [ ] |
| 29 | PO from SO confirmation | sale_order_id, sale_order_line_id, fulfillment_route_id populated | [ ] |
| 30 | PO BOM explosion | PMR lines created from active BOM | [ ] |
| 31 | PO backward scheduling | planned_start_date = effective_delivery_date - lead_time_days | [ ] |
| 32 | FGR confirmed — PO with SO link | Delivery Order auto-created | [ ] |
| 33 | FGR — actual_qty < planned_qty | DO created for actual_qty + shortfall notification | [ ] |
| 34 | FGR — actual_qty = 0 | No DO created + error notification | [ ] |
| 35 | MTS escape hatch — route override to STOCK | DO created immediately from stock, no PO | [ ] |

---

# PART 7 — HANGFIRE JOBS

| Job | Schedule | Module | Addendum | Verified |
|-----|----------|--------|----------|----------|
| `QuotationExpiryJob` | Daily at 01:00 UTC | demand | 32 | [ ] |
| `ReservationExpiryJob` | (Existing — verify still functional) | demand | Pre-existing | [ ] |

- [ ] `QuotationExpiryJob` registered in Hangfire configuration
- [ ] Job runs at 01:00 UTC daily
- [ ] Job processes only SENT quotations (not DRAFT)
- [ ] Job updates status to EXPIRED where `valid_until < GETUTCDATE()`
- [ ] Job logs execution details
- [ ] `ReservationExpiryJob` still runs correctly after schema changes

---

# PART 8 — STATE MACHINES SUMMARY

## 8.1 Sale Inquiry State Machine (Addendum 32)

```
RECEIVED → UNDER_REVIEW → REVIEW_COMPLETE → QUOTED
                                           → DECLINED
```

- [ ] All transitions implemented
- [ ] UNDER_REVIEW requires assigned_to
- [ ] REVIEW_COMPLETE requires all lines evaluated (non-PENDING)
- [ ] QUOTED creates linked quotation
- [ ] DECLINED requires rejection justification
- [ ] Invalid transitions rejected with appropriate error

## 8.2 Sale Quotation State Machine (Addendum 32)

```
DRAFT → SENT → ACCEPTED → CONVERTED
             → REJECTED
             → EXPIRED (automatic via job)
```

- [ ] All transitions implemented
- [ ] SENT requires valid lines
- [ ] ACCEPTED requires accepted customer responses
- [ ] EXPIRED set by Hangfire job
- [ ] CONVERTED creates linked SO
- [ ] CONVERTED makes quotation read-only

## 8.3 Delivery Order State Machine — Route-Aware (Addendum 33)

**Full 15-status chain:**
```
DRAFT → RELEASED → PICKING → PICKED → PACKED → STAGED →
  PENDING_APPROVAL → GOODS_ISSUED → IN_TRANSIT → AT_HUB →
  OUT_FOR_DELIVERY → DELIVERED → COMPLETED
  (+ CANCELLED, RETURNED)
```

**Step-to-status mapping:**

| Step | Statuses |
|------|----------|
| PICK | RELEASED, PICKING, PICKED |
| PACK | PACKED |
| STAGE | STAGED |
| APPROVAL | PENDING_APPROVAL |
| GOODS_ISSUE | GOODS_ISSUED |
| SHIP | IN_TRANSIT, AT_HUB, OUT_FOR_DELIVERY, DELIVERED |

**Route examples:**

| Route | Active Statuses |
|-------|----------------|
| PICK_ONLY | DRAFT → RELEASED → PICKING → PICKED → GOODS_ISSUED → COMPLETED |
| PICK_AND_SHIP | DRAFT → RELEASED → PICKING → PICKED → GOODS_ISSUED → IN_TRANSIT → ... → DELIVERED → COMPLETED |
| PICK_PACK_SHIP | DRAFT → RELEASED → PICKING → PICKED → PACKED → GOODS_ISSUED → IN_TRANSIT → ... → DELIVERED → COMPLETED |

- [ ] `RouteAwareDeliveryStateMachine` handles all route permutations
- [ ] Skipped statuses never appear in UI or API for that delivery
- [ ] `GetNextStatus()` and `GetPreviousStatus()` respect route steps

---

# PART 9 — KEY SERVICES REGISTRY

| Service/Interface | Module | Addendum | Purpose |
|-------------------|--------|----------|---------|
| `SaleOrderLineService` (validation) | demand | 31 | Min/Max qty validation |
| `SupplyRequirementEngine` | material | 31 | Draft PO creation from shortages |
| `IMaterialReadinessService` | material | 31 | Material availability queries |
| `IAllocationEngine` | material | 31 | Manual & automatic allocation |
| `SaleInquiryService` | demand | 32 | Inquiry CRUD + state transitions |
| `SaleQuotationService` | demand | 32 | Quotation CRUD + state + conversion |
| `QuotationExpiryJob` | demand | 32 | Hangfire quotation expiry |
| `IStockReservationService` | inventory | 32 | Reserve/Release (existing, extended) |
| `FulfillmentRouteService` | logistics | 33 | Route CRUD |
| `EffectiveRouteResolver` | logistics | 33 | 4-level route resolution |
| `SaleOrderDeliveryCreator` | logistics | 33 | DO creation from SO (STOCK lines) |
| `SaleOrderDeliveryPreviewService` | logistics | 33 | Preview (computed, not persisted) |
| `RouteAwareDeliveryStateMachine` | logistics | 33 | Route-aware status transitions |
| `ILeadTimeCalculator` | material | 34 | Recursive BOM lead time calculation |
| `SaleOrderProductionCreator` | material | 34 | PO creation from SO (MANUFACTURE lines) |
| `ProductionDeliveryCreator` | logistics | 34 | DO creation from completed PO |
| `FGRConfirmedEventHandler` | material | 31, 34 | Extended: allocation + delivery bridge |
| `SaleOrderService.ConfirmAsync` | demand | 33, 34 | Modified: route gate + category split |

---

# PART 10 — DEVELOPMENT PHASES & TIMELINE

## 10.1 Addendum 31 Phases (23 days sequential, ~18 parallel)

| Phase | Tasks | Days | Dependencies |
|-------|-------|------|-------------|
| A — Schema | M1-M3 migrations | 3 | None |
| B — Product UI | Tab consolidation, BOM inline, min/max fields | 5 | A |
| C — Workflow | Draft PO flow, effective date, warehouse removal | 3 | A |
| D — Material Availability | Tab UI, reserve action, allocation request | 5 | B, C |
| E — Purchase Required | Dashboard, aggregation, duplicate prevention | 4 | D |
| F — Testing | All 29 test scenarios | 3 | E |

## 10.2 Addendum 32 Phases (23 days)

| Phase | Tasks | Days | Dependencies |
|-------|-------|------|-------------|
| A — Schema | M1-M7 migrations | 2 | None |
| B — Inquiry | Service, UI, state machine | 5 | A |
| C — Quotation | Service, UI, state machine, Hangfire job | 6 | A (parallel with B) |
| D — SO Extensions | Source linking, customer PO, attachments | 3 | B, C |
| E — Indicators | Delivery indicators, reservation RBAC | 4 | D |
| F — Testing | All 34 test scenarios | 3 | E |

## 10.3 Addendum 33 Phases (19 days)

| Phase | Tasks | Days | Dependencies |
|-------|-------|------|-------------|
| A — Route Config | Tables, seed data, CRUD UI | 3 | None |
| B — Variant Assignment | Column, dropdown, bulk assign | 2 | A |
| C — SO Route | Resolver, confirmation gate, preview | 4 | A, B |
| D — Delivery Creation | SaleOrderDeliveryCreator, DO linking | 4 | C |
| E — State Machine | RouteAwareDeliveryStateMachine | 3 | A, D |
| F — Testing | All 30 test scenarios | 3 | E |

## 10.4 Addendum 34 Phases (25 days sequential, ~16 parallel)

| Phase | Tasks | Days | Dependencies |
|-------|-------|------|-------------|
| A — Route Category | Column, manufacturing seeds, cursor migration | 4 | Add-33 complete |
| B — Lead Time | Defaults table, variant columns, settings UI | 4 | A (parallel with D) |
| C — Calculation | ILeadTimeCalculator, BOM traversal, caching | 5 | B |
| D — SO Split | ConfirmAsync modification, ProductionCreator | 5 | A (parallel with B) |
| E — Bridge | FGRConfirmedEventHandler, ProductionDeliveryCreator | 4 | C, D |
| F — Testing | All 35 test scenarios | 3 | E |

## 10.5 Consolidated Timeline

**Sequential (worst case): 90 dev days**
**With parallelization: ~76 dev days**

**Parallel execution opportunities:**
- Addendum 31 and 32 can start simultaneously (independent)
- Within Addendum 32: Inquiry (Phase B) and Quotation (Phase C) can run in parallel
- Within Addendum 34: Lead Time (Phase B) and SO Split (Phase D) can run in parallel
- Within Addendum 34: Calculation (Phase C) and Bridge (Phase E) overlap potential

**Critical path:** Addendum 33 → Addendum 34 (strict dependency on route infrastructure)

---

# PART 11 — CROSS-ADDENDUM INTEGRATION POINTS

These are the points where implementations from different addendums interact. Verify these connections specifically.

## 11.1 Production Order → Sale Order Link (Add-31 M2 + Add-34 C5)

- [ ] Add-31 creates the FK columns on ProductionOrders
- [ ] Add-34 C5 `SaleOrderProductionCreator` populates these columns
- [ ] Both addendums reference the same columns: `sale_order_id`, `sale_order_line_id`

## 11.2 Fulfillment Route → Route Category (Add-33 M1 + Add-34 M1)

- [ ] Add-33 creates the `FulfillmentRoutes` table
- [ ] Add-34 adds `route_category` to the same table
- [ ] Seed routes from Add-33 get `route_category = 'STOCK'` in Add-34
- [ ] Add-34 adds manufacturing seed routes to the same table

## 11.3 SO Confirmation Flow (Add-33 C4 + Add-34 C5)

- [ ] Add-33 introduces `SaleOrderDeliveryCreator` for all SO lines
- [ ] Add-34 modifies `ConfirmAsync` to split: STOCK → `SaleOrderDeliveryCreator`, MANUFACTURE → `SaleOrderProductionCreator`
- [ ] The delivery creator from Add-33 is reused unchanged for STOCK lines

## 11.4 FGRConfirmedEventHandler (Add-31 C10 + Add-34 C6)

- [ ] Add-31 establishes automatic allocation on FGR
- [ ] Add-34 extends the same handler to also create Delivery Orders
- [ ] Both behaviors must co-exist: allocation first, then delivery creation
- [ ] Handler checks for `sale_order_id` AND `fulfillment_route_id` before creating DO

## 11.5 IStockReservationService (Add-32 C4 + Add-33)

- [ ] Add-32 adds SALES_ORDER as a reservation source type
- [ ] Add-33 introduces route-based delivery (may consume reservations)
- [ ] SO cancellation auto-releases reservations (Add-32) AND auto-cancels DOs (Add-33)

## 11.6 Sale Order Lines Accumulated Columns

Final state of `demand.SaleOrderLines` after all addendums:

| Column | Type | Addendum | Purpose |
|--------|------|----------|---------|
| `reserved_qty` | DECIMAL(18,4) NOT NULL DEFAULT 0 | 32 | Reservation tracking |
| `fulfillment_route_id` | INT NULL FK | 33 | Route assignment |
| `calculated_lead_time_days` | INT NULL | 34 | Calculated lead time |
| `calculated_delivery_date` | DATE NULL | 34 | Calculated delivery |
| `manual_delivery_date` | DATE NULL | 34 | Manual override |
| `lead_time_calculated_at` | DATETIME2 NULL | 34 | Calculation timestamp |
| `effective_delivery_date` | COMPUTED PERSISTED | 34 | COALESCE(manual, calculated) |

- [ ] All columns present on `demand.SaleOrderLines`
- [ ] No naming conflicts
- [ ] EF Core entity has all properties mapped

## 11.7 Production Order Accumulated Columns

Final state of `material.ProductionOrders` after all addendums:

| Column | Type | Addendum | Purpose |
|--------|------|----------|---------|
| `sale_order_id` | BIGINT NULL FK | 31 | SO traceability |
| `sale_order_line_id` | BIGINT NULL FK | 31 | SO line traceability |
| `fulfillment_route_id` | INT NULL FK | 34 | Route for delivery creation |
| `delivery_order_id` | BIGINT NULL FK | 34 | Created DO traceability |

- [ ] All columns present on `material.ProductionOrders`
- [ ] All FK constraints valid
- [ ] All filtered indexes created

## 11.8 Delivery Order Source Types

After all addendums, `from_source_type` on `logistics.delivery_orders` must support:

| Source Type | Origin | Addendum |
|-------------|--------|----------|
| (existing values) | Pre-existing flows | Pre-31 |
| `SALE_ORDER` | SO confirmation (STOCK lines) | 33 |
| Production source | FGR bridge (MANUFACTURE lines) | 34 |

- [ ] CHECK constraint updated to include all source types
- [ ] Each creation path sets the correct source type

---

# PART 12 — FINAL VERIFICATION CHECKLIST

## Schema Verification

- [ ] All 18 migrations applied successfully in sequence
- [ ] No orphaned FK constraints
- [ ] All indexes created
- [ ] All seed data inserted for all existing orgs
- [ ] New org creation seeds all lookup data (rejection reasons, routes, lead time defaults)

## Backend Verification

- [ ] All 18 services/handlers listed in Part 9 implemented
- [ ] All 107 business rules verified (Parts 5.1–5.4)
- [ ] All state machines function correctly (Part 8)
- [ ] All Hangfire jobs registered and scheduled (Part 7)
- [ ] Multi-tenancy (org_id filter) applied everywhere
- [ ] Audit columns (created_by, modified_by, timestamps) populated

## API Verification

- [ ] All 26+ new endpoints functional (Part 3.1)
- [ ] All modified endpoints updated (Part 3.2)
- [ ] Authorization attributes applied
- [ ] Pagination on list endpoints
- [ ] Proper error responses

## UI Verification

- [ ] All 20 UI wireframes/components implemented (Part 4)
- [ ] Responsive design
- [ ] Permission-based visibility (buttons hidden when no permission)
- [ ] Navigation between linked entities (Inquiry → Quotation → SO → DO/PO)

## Integration Verification

- [ ] All 8 cross-addendum integration points verified (Part 11)
- [ ] End-to-end flow: Inquiry → Quotation → SO Confirmation → Production/Delivery
- [ ] End-to-end flow: Production Complete → FGR → Allocation → Delivery Creation

## Testing Verification

- [ ] All 128 test scenarios pass (Part 6)
- [ ] Addendum 31: 29 scenarios ✓
- [ ] Addendum 32: 34 scenarios ✓
- [ ] Addendum 33: 30 scenarios ✓
- [ ] Addendum 34: 35 scenarios ✓

---

**Document End**
*SMS Consolidated Implementation Verification — Addendums 31, 32, 33 & 34*
*Generated: 2026-10-06*
