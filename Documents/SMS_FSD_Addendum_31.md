# SUPPLY MANAGEMENT SYSTEM — Functional Specification Document

## FSD Addendum 31 — Manufacturing Flow Corrections & UX Consolidation

| Property | Value |
|---|---|
| Document ID | SMS-FSD-ADD-031 |
| Version | 1.0 |
| Date | 2026-09-27 |
| Status | Draft |
| Author | System Architect |
| Classification | Internal — Development |
| Supersedes | Portions of SMS-FSD-ADD-030 v1.1 (see Change Matrix §2) |
| Architecture | .NET 8 / EF Core 8 / SQL Server 2022 / React 18 |
| Change Items | 10 corrections & flow updates |
| Impact | 3 migrations, 6 modified entities, 4 new/modified UI panels, 2 workflow changes |

---

## Table of Contents

1. [Purpose & Scope](#1-purpose--scope)
2. [Change Matrix — Addendum 30 Impact](#2-change-matrix--addendum-30-impact)
3. [C1 — Sale Order Quantity Limits (Min/Max per Product)](#3-c1--sale-order-quantity-limits-minmax-per-product)
4. [C2 — Product Tab Consolidation (Manufacturing → Classification)](#4-c2--product-tab-consolidation-manufacturing--classification)
5. [C3 — Supply Requirement Engine: Draft Purchase Order Flow](#5-c3--supply-requirement-engine-draft-purchase-order-flow)
6. [C4 — Inline BOM Management on Product Form](#6-c4--inline-bom-management-on-product-form)
7. [C5 — BOM Effective Date Auto-Default](#7-c5--bom-effective-date-auto-default)
8. [C6 — Remove Warehouse from BOM](#8-c6--remove-warehouse-from-bom)
9. [C7 — Production Order ↔ Sale Order Reference](#9-c7--production-order--sale-order-reference)
10. [C8 — Material Availability Tab on Production Order](#10-c8--material-availability-tab-on-production-order)
11. [C9 — Consolidated Purchase Required View](#11-c9--consolidated-purchase-required-view)
12. [C10 — GRN → Allocation → Reservation → Production Readiness Flow](#12-c10--grn--allocation--reservation--production-readiness-flow)
13. [Database Migrations](#13-database-migrations)
14. [API Changes](#14-api-changes)
15. [UI Wireframes & Specifications](#15-ui-wireframes--specifications)
16. [Business Rules](#16-business-rules)
17. [Test Scenarios](#17-test-scenarios)
18. [Development Phases](#18-development-phases)

---

## 1. Purpose & Scope

**This addendum corrects and refines 10 specific flows introduced in FSD Addendum 30 (Manufacturing, Production & Allocation Engine). It does not add new modules — it modifies existing behavior based on business review feedback to improve usability, consolidate UX, and align the manufacturing loop with operational workflow requirements.**

### 1.1 Design Principles

- **Consolidation over Proliferation** — BOM management is embedded in the Product form rather than a separate module. Shortage views are consolidated across all Production Orders into a single Purchase Required dashboard.
- **Draft-First Workflow** — Auto-generated Purchase Orders are created in Draft state, not auto-submitted. This gives procurement teams review and edit capability before supplier engagement.
- **Conditional Enforcement** — Sale Order quantity limits (Min/Max) only apply when explicitly configured on a product. NULL or 0 values mean no restriction.
- **Traceability** — Production Orders carry a direct reference to their originating Sale Order when demand-driven, enabling end-to-end trace from customer order through production to fulfillment.
- **Operational Consolidation** — Material shortages across all Production Orders are aggregated per product into a single Purchase Required view, preventing duplicate purchase orders for the same material.

### 1.2 Scope Summary

| Change ID | Title | Type | Addendum 30 Ref |
|---|---|---|---|
| C1 | Sale Order Quantity Limits (Min/Max) | New Feature | §6 Product Catalog |
| C2 | Product Tab Consolidation | UX Change | §29.1 Product UI |
| C3 | Draft Purchase Order Flow | Workflow Change | §13 Supply Requirement Engine |
| C4 | Inline BOM on Product Form | UX Restructure | §7–9 BOM Management, §29.2 BOM UI |
| C5 | BOM Effective Date Auto-Default | Default Change | §7.1 BillOfMaterials |
| C6 | Remove Warehouse from BOM | Schema Change | §7.2 BillOfMaterialLines |
| C7 | Production Order ↔ Sale Order Link | Schema Extension | §11 ProductionOrders |
| C8 | Material Availability Tab | New UI Panel | §12 PMR, §14 Allocation |
| C9 | Consolidated Purchase Required View | New Feature | §13 Supply Requirements |
| C10 | GRN → Allocation → Reservation Flow | Workflow Refinement | §15, §18, §20 |

---

## 2. Change Matrix — Addendum 30 Impact

**This section maps each change to the specific Addendum 30 sections it supersedes, modifies, or extends.**

| Change | ADD-030 Section | Action | Impact Description |
|---|---|---|---|
| C1 | §6 Products | EXTEND | Add sale_order_min_qty, sale_order_max_qty to Products table. Add validation in Sale Order line creation. |
| C2 | §29.1 Product UI | MODIFY | Merge Manufacturing tab content into Classification tab. Remove separate Manufacturing tab. |
| C3 | §13 Supply Req Engine | MODIFY | Replace auto-submit + auto-send-to-supplier with Draft creation + notification. Add default supplier pre-selection with edit capability. |
| C4 | §7–9 BOM, §29.2 BOM UI | RESTRUCTURE | Remove standalone BOM module UI. BOM is now inline on Product form for manufacturing products. Mandatory when product_type requires BOM. Two-panel layout: BOM list + BOM editor. |
| C5 | §7.1 BillOfMaterials | MODIFY | effective_from defaults to GETDATE() instead of NULL. Applies to new BOM creation only. |
| C6 | §7.2 BOM Lines | REMOVE | Remove warehouse_id from BillOfMaterialLines. Warehouse is determined at Production Order level, not BOM level. |
| C7 | §11 ProductionOrders | EXTEND | Add sale_order_id, sale_order_line_id columns. Populated when PO originates from Sale Order demand. NULL for directly created POs. |
| C8 | §12, §14 PMR + Alloc | NEW PANEL | New 'Material Availability' tab on Production Order detail. Two sections: Available (with Reserve button) and Shortage (with Allocation Request button). |
| C9 | §13 Supply Requirements | RESTRUCTURE | Replace per-Production Order shortage view with consolidated Purchase Required dashboard. Aggregate shortages across all POs per product. Single-line per product with summed qty. |
| C10 | §15, §18, §20 | REFINE | Clarify GRN → allocation run → inventory reservation → PO material status update flow. Authorized user triggers allocation. Items move from Shortage to Available on PO. |

---

## 3. C1 — Sale Order Quantity Limits (Min/Max per Product)

### 3.1 Overview

Add two new parameters to the Product Stock Settings: Sale Order Min Qty and Sale Order Max Qty. These enforce minimum and maximum order quantities when a Sale Order line is created or updated for this product variant.

> **⚠️ CRITICAL:** When both sale_order_min_qty and sale_order_max_qty are NULL or 0, NO quantity validation is applied. The limit is conditional — it only fires when explicitly configured.

### 3.2 Schema Change

**Modify:** `lookups.Products`

#### Products — New Columns (ADD)

| Column | Type | Nullable | Description |
|---|---|---|---|
| sale_order_min_qty | DECIMAL(18,4) | NULL | Minimum allowed quantity per Sale Order line. NULL or 0 = no minimum. |
| sale_order_max_qty | DECIMAL(18,4) | NULL | Maximum allowed quantity per Sale Order line. NULL or 0 = no maximum. |

### 3.3 Validation Logic

**Applied at Sale Order line creation and update (SaleOrderLineService.Create/Update):**

```csharp
// Pseudocode — Sale Order Line Quantity Validation
var product = await _productRepo.GetByIdAsync(lineDto.ProductId);

decimal minQty = product.SaleOrderMinQty ?? 0;
decimal maxQty = product.SaleOrderMaxQty ?? 0;

if (minQty > 0 && lineDto.Quantity < minQty)
    throw new BusinessException(
        $"Quantity {lineDto.Quantity} is below minimum order quantity of {minQty} for {product.Name}.");

if (maxQty > 0 && lineDto.Quantity > maxQty)
    throw new BusinessException(
        $"Quantity {lineDto.Quantity} exceeds maximum order quantity of {maxQty} for {product.Name}.");

// Cross-validation: if both set, min must be <= max
// This is validated on Product save, not on SO line save
```

### 3.4 Product-Level Cross-Validation

**On Product create/update, if both values are provided:**

- sale_order_min_qty must be <= sale_order_max_qty (when both are > 0)
- Both values must be >= 0 (negative quantities not allowed)
- Values are per-line, not per-order (each SO line is validated independently)

### 3.5 Business Rules

| Rule ID | Rule | Enforcement |
|---|---|---|
| BR-C1-01 | Min/Max validation fires ONLY when value is non-null AND > 0 | SaleOrderLineService |
| BR-C1-02 | When min is set but max is not → only minimum enforced | SaleOrderLineService |
| BR-C1-03 | When max is set but min is not → only maximum enforced | SaleOrderLineService |
| BR-C1-04 | Validation applies to each SO line independently, not aggregated across lines | SaleOrderLineService |
| BR-C1-05 | On Product save: min must be <= max when both are > 0 | ProductService |
| BR-C1-06 | Changing min/max on a product does NOT retroactively validate existing Sale Orders | N/A |

### 3.6 UI Placement

**Location:** Product form → Stock Parameters section (existing section)

- Add 'Sale Order Min Qty' numeric input — placed below existing stock parameters
- Add 'Sale Order Max Qty' numeric input — placed below Min Qty
- Both fields accept decimal values (4 decimal places)
- Placeholder text: 'Leave empty for no limit'
- When max < min on save: inline validation error 'Maximum must be greater than or equal to minimum'

### 3.7 Sale Order UI Impact

**When a user enters a quantity on a Sale Order line and tabs out or saves:**

- If qty < min: inline error below qty field — 'Minimum order quantity for [Product Name] is [min]'
- If qty > max: inline error below qty field — 'Maximum order quantity for [Product Name] is [max]'
- Error clears when qty is corrected
- Save button is disabled while validation errors exist

---

## 4. C2 — Product Tab Consolidation (Manufacturing → Classification)

### 4.1 Overview

The Manufacturing tab defined in Addendum 30 (§29.1) is removed as a separate tab. All manufacturing-related fields are merged into the existing Classification tab on the Product form. This reduces tab proliferation and groups all product categorization and type information in one place.

### 4.2 Fields Moved to Classification Tab

**The following fields from the Manufacturing tab are relocated to the Classification tab, appearing after the existing classification fields:**

| Field | Type | Position in Classification Tab | Visibility Condition |
|---|---|---|---|
| Product Type | Dropdown (8 values) | After existing category/sub-category fields | Always visible |
| Supply Method | Dropdown (4 values) | Below Product Type | Always visible |
| Is Manufacturable | Toggle | Below Supply Method | Always visible |
| Is BOM Input | Toggle | Below Is Manufacturable | Visible when Product Type = FinishedGood or SemiFinished |
| Is Saleable | Toggle | Below Is BOM Input | Always visible |
| Is Purchasable | Toggle | Below Is Saleable | Always visible |
| Is Stockable | Toggle | Below Is Purchasable | Always visible |
| Default Production Warehouse | Dropdown | Below Is Stockable | Visible when Is Manufacturable = true |
| Lead Time Days | Numeric | Below Default Production Warehouse | Visible when Is Manufacturable = true |

### 4.3 Classification Tab Layout

**The Classification tab is now organized into two visual sections:**

- **Section 1 — Product Classification (existing):** Category, Sub-Category, Brand, Unit of Measure, HSN/Tax code, etc.
- **Section 2 — Product Type & Manufacturing (new, below Section 1):** Product Type, Supply Method, manufacturing toggles, production warehouse, lead time. This section has a subtle section header 'Manufacturing & Supply' with a horizontal divider above it.

> **📝 NOTE:** The separate Manufacturing tab route/component is removed entirely. Any existing navigation links to it should redirect to the Classification tab.

---

## 5. C3 — Supply Requirement Engine: Draft Purchase Order Flow

### 5.1 Overview

Addendum 30 §13 specified that the Supply Requirement Engine auto-creates and auto-submits Purchase Orders to suppliers when a component has supply_method=Purchase. This is changed: the engine now creates Purchase Orders in Draft status and sends a notification to the procurement team. The user reviews, optionally changes the supplier and quantity, then manually submits.

> **⚠️ CRITICAL:** The auto-submit and auto-send-to-supplier behavior from Addendum 30 §13 is fully replaced by this Draft-first workflow. No Purchase Order is sent to a supplier without human review.

### 5.2 Revised Flow

**When ISupplyRequirementEngine.ProcessShortages() identifies a component with supply_method=Purchase:**

```
// BEFORE (Addendum 30 — SUPERSEDED):
// 1. Create PO → 2. Auto-submit → 3. Auto-send to supplier

// AFTER (Addendum 31 — CURRENT):
// 1. Create PO in Draft status
// 2. Auto-select default supplier from product.default_supplier_id
// 3. Set quantity from SR.required_qty (subject to user update)
// 4. Link SR ↔ PO (sr.linked_po_id = po.purchase_order_id)
// 5. Send notification + email to users with 'purchase_order_write' permission
// 6. User reviews → changes supplier/qty if needed → manually submits
```

### 5.3 Auto-Created Purchase Order Details

| PO Field | Value | Editable by User |
|---|---|---|
| status | Draft | No (transitions via workflow) |
| supplier_id | product.default_supplier_id (from Product master) | Yes — user can change before submit |
| quantity | supply_requirement.required_qty | Yes — user can adjust before submit |
| required_by_date | production_order.planned_start_date (from parent PO) | Yes |
| source_type | 'PRODUCTION' (new marker) | No |
| source_reference_id | supply_requirement.supply_requirement_id | No |
| notes | 'Auto-generated from Production Order [PO#] for [Product Name]' | Yes |
| created_by | SYSTEM (service account) | No |

### 5.4 Default Supplier Selection

**The system pre-selects the supplier using this priority:**

1. **product.default_supplier_id** — if a default supplier is configured on the product
2. **Last supplier used** — query the most recent approved PO for this product to find the last supplier
3. **NULL** — if no supplier history exists, leave supplier_id NULL. User must select before submitting.

> **📝 NOTE:** When supplier_id is NULL on the auto-created Draft PO, the notification message includes 'Supplier selection required' to alert the reviewer.

### 5.5 Notification

**On Draft PO creation from Supply Requirement Engine:**

- Notification type: `PurchaseOrderDraftCreated`
- Recipients: All users in the same org_id with permission claim 'purchase_order_write'
- Channel: In-app notification (auth.Notifications) + Email via Hangfire
- Email subject: `[SMS] Purchase Order [PO#] created for review — [Product Name]`
- Email body includes: PO number, product name, required quantity, required-by date, supplier (or 'Not selected'), link to PO edit page, originating Production Order number

### 5.6 Business Rules

| Rule ID | Rule | Enforcement |
|---|---|---|
| BR-C3-01 | Auto-created POs are ALWAYS in Draft status — never auto-submitted | SupplyRequirementEngine |
| BR-C3-02 | Default supplier is pre-selected but always editable before submit | PO Edit UI |
| BR-C3-03 | Quantity is pre-filled from SR but always editable before submit | PO Edit UI |
| BR-C3-04 | PO cannot be submitted without a supplier_id | PurchaseOrderService.Submit() |
| BR-C3-05 | The SR ↔ PO link is established at creation and immutable | SupplyRequirementEngine |
| BR-C3-06 | If user changes quantity on PO, the SR.required_qty is NOT updated (SR reflects original demand) | PurchaseOrderService |
| BR-C3-07 | Multiple SRs for the same product may link to the same Draft PO (consolidated purchasing) | SupplyRequirementEngine |

---

## 6. C4 — Inline BOM Management on Product Form

### 6.1 Overview

Instead of a separate Bill of Materials module with its own navigation and pages (Addendum 30 §29.2), BOM management is integrated directly into the Product form. When a product has product_type that requires manufacturing (FinishedGood, SemiFinished, or any product with supply_method=Manufacture), a BOM section appears on the product form. BOM data entry is mandatory for such products.

> **⚠️ CRITICAL:** The standalone BOM list page and BOM create/edit pages from Addendum 30 §29.2 are REMOVED. All BOM operations happen inline on the Product form. The BOM API endpoints (§28.2) remain unchanged — only the UI entry point changes.

### 6.2 BOM Section Visibility

**The BOM section appears on the Product form when:**

- product_type IN (FinishedGood, SemiFinished) — always shows BOM section
- OR supply_method = Manufacture — always shows BOM section
- OR is_manufacturable = true — always shows BOM section

**When visible, the BOM section is mandatory:**

- The product cannot be saved/activated without at least one BOM in Draft or higher status
- Validation message: 'At least one Bill of Material is required for a manufacturing product'

### 6.3 Two-Panel Layout

**The BOM section on the Product form uses a two-panel layout:**

#### Panel 1 — BOM List (Left Panel, 35% width)

- Lists all BOMs for this product (same data as the removed standalone BOM list)
- Each row shows: BOM Number, Version, Status (badge), Effective From, Effective To, Is Default
- Status badge colors: Draft (grey), Submitted (blue), Approved (amber), Active (green), Obsolete (grey strikethrough), Rejected (red)
- Click a BOM row → loads it in Panel 2 for viewing/editing
- '+ New BOM' button at top of list → opens blank form in Panel 2
- Active BOM is visually highlighted (green left border)

#### Panel 2 — BOM Editor/Viewer (Right Panel, 65% width)

**When a BOM is selected or new BOM is being created:**

- Header: BOM Number (auto-generated on create), Version, Status badge
- Fields: Base Quantity, Base UOM, Yield Percentage, Notes
- Effective From date (auto-defaults to today — see C5)
- Effective To date (optional)
- BOM Lines table: Component Product (searchable dropdown), Quantity, UOM, Wastage %, Scrap %, Is Critical (toggle), Notes
- Add Line button at bottom of lines table
- Inline line editing — click a line to edit in-place
- Delete line: trash icon on each row (with confirmation)

**Workflow action buttons (bottom of Panel 2, based on current status):**

- **Draft:** [Save] [Submit for Approval] [Delete]
- **Submitted:** [Approve] [Reject] (4-eyes principle enforced)
- **Approved:** [Activate] (deactivates other Active BOMs for this product)
- **Active:** [Create New Version] [Obsolete]
- **Rejected:** [Edit & Resubmit] (creates new version in Draft)
- **Obsolete:** [Create New Version]

### 6.4 Empty State

**When a manufacturing product has no BOMs yet:**

- Panel 1 shows empty state: 'No Bill of Materials defined'
- Panel 2 shows: 'Add a Bill of Materials to define the components required to manufacture this product.' with a prominent '+ Create First BOM' button
- The Product save button shows a warning badge: 'BOM required'

### 6.5 BOM Version Comparison

**In Panel 1, when multiple versions exist for the same product:**

- A 'Compare Versions' link appears below the list
- Opens a modal with side-by-side version comparison (same as Addendum 30 §29.2)
- Highlights: added lines (green), removed lines (red), changed quantities (amber)

### 6.6 Navigation Change

| Navigation Item | Before (ADD-030) | After (ADD-031) |
|---|---|---|
| Main nav: BOM Management | Exists as top-level menu | REMOVED |
| BOM List page | /bom-management | REMOVED — inline on Product form |
| BOM Create page | /bom-management/create | REMOVED — inline on Product form |
| BOM Edit page | /bom-management/:id/edit | REMOVED — inline on Product form |
| BOM API endpoints | Unchanged | Unchanged — /api/boms/* still active |
| Product form | Separate Manufacturing tab | Classification tab + inline BOM section |

---

## 7. C5 — BOM Effective Date Auto-Default

### 7.1 Change

When creating a new BOM (via the inline BOM editor in the Product form), the effective_from field is automatically set to today's date. The user can change it but it is no longer NULL by default.

### 7.2 Implementation

- Frontend: On BOM create form mount, set `effective_from = new Date().toISOString().split('T')[0]`
- Backend: In BOMService.Create(), if effective_from is null → set to `DateTimeOffset.UtcNow.Date`
- Existing BOMs with NULL effective_from are NOT retroactively updated
- effective_to remains optional (NULL by default)

### 7.3 Schema Impact

*No schema change required. The column already allows NULL. The default is applied at the application level, not the database level. This preserves backward compatibility with existing BOMs that have NULL effective_from.*

---

## 8. C6 — Remove Warehouse from BOM

### 8.1 Rationale

The warehouse_id on BillOfMaterialLines (Addendum 30 §7.2) is removed. Warehouse assignment happens at the Production Order level — the production_warehouse_id on the Production Order determines where materials are sourced from. Having warehouse at the BOM line level creates confusion because the same BOM may be used in different warehouses depending on the Production Order.

### 8.2 Schema Change

**Modify:** `material.BillOfMaterialLines`

- **REMOVE:** warehouse_id BIGINT NULL FK — drop column and foreign key constraint
- The corresponding Warehouse dropdown in the BOM line editor UI is removed

### 8.3 Impact on PMR/BOM Explosion

**In Addendum 30 §12.2 (BOM Explosion Service), the PMR warehouse_id was set from the BOM line warehouse or the Production Order warehouse. With this change:**

- PMR.warehouse_id is ALWAYS set from `ProductionOrder.production_warehouse_id`
- This simplifies the BOM explosion logic — no conditional warehouse selection

### 8.4 Migration

**Migration drops the column. Any existing data in warehouse_id on BOM lines is discarded. This is safe because:**

- BOM lines are template data, not transactional — no business data is lost
- The warehouse selection moves to Production Order, which already has production_warehouse_id

---

## 9. C7 — Production Order ↔ Sale Order Reference

### 9.1 Overview

Add a direct reference from Production Orders to the originating Sale Order. When a Production Order is created from a Sale Order demand (via FulfillmentRequirementService), it carries the sale_order_id and sale_order_line_id. When a Production Order is created directly (manual creation), these fields remain NULL.

### 9.2 Schema Change

**Modify:** `material.ProductionOrders`

#### ProductionOrders — New Columns (ADD)

| Column | Type | Nullable | Description |
|---|---|---|---|
| sale_order_id | BIGINT | NULL | FK → demand.SaleOrders. The originating Sale Order, if this PO was created from SO demand. NULL for directly created POs. |
| sale_order_line_id | BIGINT | NULL | FK → demand.SaleOrderLines. The specific SO line that drove this production demand. NULL for directly created POs. |

*Both columns are nullable — they are populated ONLY when the Production Order originates from a Sale Order demand chain (SO → Fulfillment Requirement → Production Order).*

### 9.3 Population Logic

```csharp
// In FulfillmentRequirementService.CreateFromSaleOrder():
// When creating a Production Order for a SO line with fulfillment_method = Manufacture:

var productionOrder = new ProductionOrder {
    // ... existing fields ...
    SaleOrderId = fulfillmentReq.SaleOrderId,
    SaleOrderLineId = fulfillmentReq.SaleOrderLineId,
    SourceDemandType = "SALES_ORDER",
    SourceDemandId = fulfillmentReq.SaleOrderId,
    // ...
};

// For direct creation (ProductionOrderController.Create):
// sale_order_id and sale_order_line_id remain NULL
```

### 9.4 UI Display

**On the Production Order detail page:**

- If sale_order_id is NOT NULL: Show a 'Sale Order' link field displaying the SO number as a clickable link that opens the Sale Order detail page. Also show the SO line number.
- If sale_order_id IS NULL: Show 'Sale Order: — (Directly Created)' as a greyed-out text.
- The Sale Order field is read-only — it is set by the system, not editable by users.

### 9.5 Reverse Navigation

**On the Sale Order detail page:**

- If any Production Orders reference this Sale Order: Show a 'Production Orders' section listing all linked POs with their number, product, status, and planned qty.
- This enables end-to-end traceability: Sale Order → Production Order → Material Issue → QI → FGR → Delivery.

---

## 10. C8 — Material Availability Tab on Production Order

### 10.1 Overview

Add a new 'Material Availability' tab on the Production Order detail page. This tab replaces the previous inline PMR shortage display with a structured two-section layout: Material Available and Material Shortage. Each section has actionable buttons for reservation and allocation requests.

### 10.2 Tab Structure

**Production Order Detail → Tab: 'Material Availability'**

#### Section 1 — Material Available

**Lists all materials (PMRs) that have sufficient stock in the warehouse:**

| Column | Description |
|---|---|
| Product | Component product name + variant |
| Required Qty | pmr.gross_required_qty |
| Available Qty | Available stock from AllocationEngine.GetAvailabilityAsync() |
| Warehouse | Warehouse name (from PO production_warehouse_id) |
| Status | Badge: Available (green) |
| Action | 'Reserve' button — reserves this material for this Production Order |

**Reserve Button Behavior:**

- Calls `IStockReservationService.Reserve(variantId, warehouseId, qty, sourceType=PRODUCTION_ORDER, sourceId=productionOrderId)`
- On success: row status changes to 'Reserved' (blue badge), Reserve button becomes 'Reserved' (disabled)
- On failure (insufficient stock due to concurrent allocation): shows error toast 'Stock no longer available — another allocation consumed this stock'
- Bulk action: 'Reserve All Available' button at section header — reserves all items in one batch

#### Section 2 — Material Shortage

**Lists all materials (PMRs) with shortage_qty > 0:**

| Column | Description |
|---|---|
| Product | Component product name + variant |
| Required Qty | pmr.gross_required_qty |
| Issued Qty | pmr.issued_qty (what has been issued so far) |
| Shortage Qty | pmr.shortage_qty |
| Status | Badge: Shortage (red), Partial (amber) |
| Action | 'Allocation Request' button — opens Register Demand dialog |

### 10.3 Allocation Request Dialog

**When user clicks 'Allocation Request' on a shortage line:**

A modal dialog titled **'Register Demand — Allocation Request'** opens with:

| Field | Pre-filled Value | Editable |
|---|---|---|
| Product | Component product name (from PMR) | No |
| Variant | Component variant (from PMR) | No |
| Quantity | pmr.shortage_qty | Yes (can reduce, cannot exceed shortage) |
| Demand Type | PRODUCTION_ORDER | No |
| Demand ID | production_order_id | No |
| Priority | Production Order priority (1–10) | Yes |
| Warehouse | PO production_warehouse_id | No |
| Required By | PO planned_start_date | Yes |
| Notes | Empty | Yes |

**On Submit:**

- Calls `IAllocationEngine.AllocateAsync(variantId, warehouseId, PRODUCTION_ORDER, productionOrderId, qty, priority)`
- If stock becomes available (e.g., from GRN): allocation is fulfilled, item moves from Shortage to Available
- If no stock available: allocation record is created as pending demand — will be fulfilled when stock arrives
- The button label changes to 'Allocation Requested' (disabled) with a pending badge

---

## 11. C9 — Consolidated Purchase Required View

### 11.1 Overview

Instead of viewing material shortages per-Production Order (which leads to duplicate purchase orders for the same material), this change introduces a Consolidated Purchase Required dashboard. It aggregates all shortages across all active Production Orders per product into a single line item.

> **⚠️ CRITICAL:** This replaces the per-Production Order 'Create PO from Shortage' button from Addendum 30. The consolidated view prevents procurement from creating separate POs for the same product that is short across multiple Production Orders.

### 11.2 Data Aggregation Logic

```sql
-- Consolidated Purchase Required Query
SELECT
    pmr.component_product_id,
    p.product_name,
    p.sku,
    SUM(pmr.shortage_qty) AS total_shortage_qty,
    COUNT(DISTINCT pmr.production_order_id) AS affected_po_count,
    MIN(po.planned_start_date) AS earliest_required_date,
    p.default_supplier_id,
    s.supplier_name AS default_supplier_name
FROM material.ProductionMaterialRequirements pmr
JOIN material.ProductionOrders po ON pmr.production_order_id = po.production_order_id
JOIN lookups.Products p ON pmr.component_product_id = p.product_id
LEFT JOIN suppliers.Suppliers s ON p.default_supplier_id = s.supplier_id
WHERE pmr.shortage_qty > 0
    AND pmr.is_deleted = 0
    AND po.status IN ('Planned','MaterialPending','Ready','InProgress')
    AND po.org_id = @orgId
GROUP BY pmr.component_product_id, p.product_name, p.sku,
         p.default_supplier_id, s.supplier_name
```

### 11.3 Purchase Required Dashboard

**Navigation:** Manufacturing → Purchase Required (new top-level menu item)

| Column | Description |
|---|---|
| Product | Product name + SKU |
| Total Shortage | Sum of shortage_qty across all Production Orders for this product |
| Affected POs | Count of Production Orders with shortage for this product (clickable — shows PO list) |
| Earliest Required By | Earliest planned_start_date among affected POs |
| Default Supplier | Product's default supplier name (or 'Not Set') |
| Current Stock | Current on_hand_qty for this product across all warehouses |
| Pending POs | Existing Draft/Submitted POs for this product (to prevent duplicates) |
| Action | 'Create Purchase Order' button |

### 11.4 'Create Purchase Order' Flow

**When user clicks 'Create Purchase Order' on a consolidated shortage line:**

1. Purchase Order creation screen opens (existing PO create form from procurement module)
2. Pre-filled fields:
   - Supplier: product.default_supplier_id (editable — user can change)
   - Product/variant: from the shortage line
   - Quantity: total_shortage_qty (editable — user can adjust up or down)
   - Required By: earliest_required_date from aggregation
   - Notes: 'Purchase for production shortage — [X] Production Orders affected'
   - Source Type: 'PRODUCTION' (read-only)
3. PO is created in Draft status (consistent with C3)
4. User reviews and submits when ready

### 11.5 Duplicate Prevention

**Before showing the 'Create Purchase Order' button, the system checks:**

- If a Draft or Submitted PO already exists for this product with source_type='PRODUCTION': show 'PO [number] already in Draft' link instead of the create button
- If the existing Draft PO quantity < total_shortage_qty: show 'PO [number] in Draft (qty: X, shortage: Y)' with a 'Update Quantity' button that opens the existing PO for editing
- This prevents procurement from accidentally creating duplicate POs for the same shortage

### 11.6 Affected POs Detail Drawer

**Clicking the 'Affected POs' count opens a side drawer showing:**

- List of Production Orders with shortage for this product
- Each row: PO Number (link), Status, Planned Qty, Shortage Qty, Planned Start Date
- Helps procurement understand the demand distribution before creating a PO

### 11.7 API Endpoint

**New endpoint:**

- `GET /api/purchase-required` — returns aggregated shortage data per product
- Params: `?supplierId`, `?minShortageQty`, `?sortBy=shortage|urgency|product`
- Response includes: product details, total_shortage_qty, affected_po_count, earliest_required_date, default_supplier, current_stock, existing_draft_po (if any)

---

## 12. C10 — GRN → Allocation → Reservation → Production Readiness Flow

### 12.1 Overview

This change clarifies and formalizes the end-to-end flow from Goods Receipt Note (GRN) through to Production Order material readiness. Addendum 30 described pieces of this flow across §15, §18, and §20 — this consolidates and sequences the steps explicitly, with a manual allocation trigger by an authorized user.

### 12.2 Complete Flow Sequence

**Step-by-step sequence when a Purchase Order's goods are received:**

```
STEP 1: GRN Received & Approved
  → Inventory: stock_transactions created (GOODS_RECEIPT)
  → Inventory: stock_balance.on_hand_qty increased
  → Supply Requirement: IF sr.linked_po_id = this PO
      → Update sr.fulfilled_qty += grn_line.received_qty
      → Update sr.status (Open → PartiallyFulfilled → Fulfilled)

STEP 2: Authorized User Runs Allocation (MANUAL TRIGGER)
  → User navigates to Allocation dashboard or Material Availability tab
  → Clicks 'Run Allocation' button
  → IAllocationEngine processes all pending demands for the received products
  → Priority-based allocation: highest-priority demand gets stock first
  → Creates AllocationRecords (status=Active) for fulfilled demands

STEP 3: Inventory Reservation
  → For each fulfilled AllocationRecord where demand_type = PRODUCTION_ORDER:
      → IStockReservationService.Reserve(variantId, warehouseId, allocatedQty,
          sourceType=PRODUCTION_ORDER, sourceId=productionOrderId)
      → stock_balance.reserved_qty increased
      → stock_balance.available_qty decreased

STEP 4: Production Order Material Status Update
  → For each affected Production Order:
      → IMaterialReadinessService.Recalculate(productionOrderId)
      → PMR items with fulfilled allocation:
          → pmr.shortage_qty decreased (or zeroed)
          → pmr.status → FullyIssued or PartiallyIssued
      → On Production Order 'Material Availability' tab:
          → Item moves from 'Shortage' section to 'Available' section
      → IF all PMRs have zero shortage:
          → PO status: MaterialPending → Ready
          → Notification: ProductionOrderReady sent
      → ELSE: PO remains MaterialPending (partial fulfillment)

STEP 5: Production Proceeds
  → PO in Ready status can be started (IProductionExecutionService.Start)
  → Material Issue can proceed for available materials
```

### 12.3 Manual Allocation Trigger

**The allocation step (Step 2) is a manual action by an authorized user — it is NOT automatic on GRN approval. This gives the allocation team control over when and how to distribute received stock.**

- Required permission: `allocation_write`
- Location 1: Allocation Dashboard → 'Run Allocation' button (processes all pending demands)
- Location 2: Production Order → Material Availability tab → 'Check Availability' button (re-checks stock for this PO only)
- Location 3: After GRN approval, a banner on the GRN detail page shows: 'Stock received. Run allocation to distribute to pending demands.' with a 'Run Allocation' button.

### 12.4 Automatic vs. Manual Allocation

| Scenario | Trigger | Scope |
|---|---|---|
| GRN Approved | Manual — user clicks 'Run Allocation' | All pending demands for received products |
| FGR Confirmed | Automatic — via FGRConfirmedEvent MediatR handler | Pending SO demands for the produced FG product |
| Manual Allocation Request (C8) | Creates pending demand | Fulfilled when stock becomes available + allocation runs |
| Allocation Dashboard refresh | Manual — user clicks 'Run Allocation' | All pending demands across all products |

> **📝 NOTE:** The only automatic allocation trigger is FGR Confirmation (finished goods to sales order demand). All other allocation runs require manual user action. This prevents GRN receipts from automatically consuming stock that may be needed for higher-priority demands not yet entered into the system.

### 12.5 Material Availability Tab State Transitions

**How items move between sections on the Production Order 'Material Availability' tab:**

| Event | Shortage Section | Available Section |
|---|---|---|
| BOM Explosion (Plan) | All PMRs with shortage appear here | PMRs with existing stock appear here |
| Allocation Request submitted | Item shows 'Allocation Requested' badge | No change |
| GRN received + Allocation run | Item removed (shortage resolved) | Item appears with 'Available' status |
| Partial GRN + Allocation run | Shortage qty reduced, item remains | Partial qty appears in Available |
| Material Reserved | No change | Status changes to 'Reserved' (blue) |
| Material Issued | No change | Status changes to 'Issued' (green), qty updated |

---

## 13. Database Migrations

**Three migrations are required for this addendum. They are additive and non-breaking.**

### 13.1 Migration Order

| # | Migration Name | Schema | Type | Description |
|---|---|---|---|---|
| M1 | AddSaleOrderQtyLimitsToProducts | lookups | ALTER TABLE | Add sale_order_min_qty DECIMAL(18,4) NULL, sale_order_max_qty DECIMAL(18,4) NULL to lookups.Products |
| M2 | AddSaleOrderRefToProductionOrders | material | ALTER TABLE | Add sale_order_id BIGINT NULL FK, sale_order_line_id BIGINT NULL FK to material.ProductionOrders |
| M3 | RemoveWarehouseFromBOMLines | material | ALTER TABLE | Drop warehouse_id column and FK from material.BillOfMaterialLines |

### 13.2 Migration Details

#### M1 — AddSaleOrderQtyLimitsToProducts

```sql
ALTER TABLE lookups.Products
ADD sale_order_min_qty DECIMAL(18,4) NULL;

ALTER TABLE lookups.Products
ADD sale_order_max_qty DECIMAL(18,4) NULL;

-- No data migration needed — NULL means no limit (backward compatible)
```

#### M2 — AddSaleOrderRefToProductionOrders

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

#### M3 — RemoveWarehouseFromBOMLines

```sql
ALTER TABLE material.BillOfMaterialLines
DROP CONSTRAINT IF EXISTS FK_BOMLines_Warehouse;

ALTER TABLE material.BillOfMaterialLines
DROP COLUMN IF EXISTS warehouse_id;

-- Safe: BOM lines are template data. Warehouse is
-- determined at Production Order level.
```

### 13.3 Rollback

- **M1:** DROP COLUMN sale_order_min_qty, sale_order_max_qty from Products
- **M2:** DROP COLUMN sale_order_id, sale_order_line_id from ProductionOrders (after clearing FK)
- **M3:** ADD COLUMN warehouse_id BIGINT NULL to BillOfMaterialLines + FK (data is lost but BOM lines are templates, not transactions)

---

## 14. API Changes

### 14.1 Modified Endpoints

| Endpoint | Change | Change ID |
|---|---|---|
| GET/POST/PUT /api/products | Add sale_order_min_qty, sale_order_max_qty to DTOs | C1 |
| POST/PUT /api/sale-orders/lines | Add min/max qty validation in request pipeline | C1 |
| POST /api/boms | Default effective_from to today if null | C5 |
| POST /api/boms/lines | Remove warehouse_id from CreateBOMLineDto | C6 |
| GET /api/boms/lines | Remove warehouse_id from BOMLineDto response | C6 |
| POST /api/production-orders | Add sale_order_id, sale_order_line_id to response DTO | C7 |
| GET /api/production-orders/{id} | Include sale_order_id, sale_order_line_id + SO number in response | C7 |

### 14.2 New Endpoints

| Endpoint | Method | Description | Change ID |
|---|---|---|---|
| /api/purchase-required | GET | Consolidated purchase required list — aggregated shortage per product | C9 |
| /api/purchase-required/{productId}/affected-orders | GET | List of Production Orders with shortage for a specific product | C9 |
| /api/production-orders/{id}/material-availability | GET | Material availability data: available + shortage sections | C8 |
| /api/production-orders/{id}/material-availability/reserve | POST | Reserve available material for this Production Order | C8 |
| /api/production-orders/{id}/material-availability/reserve-all | POST | Batch reserve all available materials | C8 |
| /api/allocation/run | POST | Manual allocation trigger — processes all pending demands | C10 |
| /api/allocation/run?productionOrderId={id} | POST | Scoped allocation for a specific Production Order | C10 |

### 14.3 Removed/Changed Behavior

| Previous Behavior | New Behavior | Change ID |
|---|---|---|
| SupplyRequirementEngine auto-submits PO | Creates PO in Draft + sends notification | C3 |
| BOM lines include warehouse_id | warehouse_id removed from BOM line DTOs | C6 |
| Per-PO shortage → Create PO button | Consolidated Purchase Required dashboard | C9 |
| GRN → Auto allocation | GRN → Manual allocation trigger | C10 |

---

## 15. UI Wireframes & Specifications

### 15.1 Product Form — Classification Tab (C2)

```
┌─────────────────────────────────────────────────────────┐
│  Product: [Product Name]                    [Save] [×]  │
│  ─────────────────────────────────────────────────────── │
│  [General] [Classification] [Stock] [Pricing] [BOM]     │
│                  ▲ active                         ▲ new │
│  ─────────────────────────────────────────────────────── │
│                                                         │
│  Product Classification                                 │
│  ┌─────────────────────┐ ┌─────────────────────┐       │
│  │ Category         ▼  │ │ Sub-Category      ▼  │       │
│  └─────────────────────┘ └─────────────────────┘       │
│  ┌─────────────────────┐ ┌─────────────────────┐       │
│  │ Brand            ▼  │ │ UOM               ▼  │       │
│  └─────────────────────┘ └─────────────────────┘       │
│                                                         │
│  ─── Manufacturing & Supply ─────────────────────────── │
│                                                         │
│  ┌─────────────────────┐ ┌─────────────────────┐       │
│  │ Product Type     ▼  │ │ Supply Method     ▼  │       │
│  └─────────────────────┘ └─────────────────────┘       │
│  □ Is Manufacturable   □ Is BOM Input                   │
│  □ Is Saleable         □ Is Purchasable   □ Is Stockable│
│  ┌─────────────────────┐ ┌─────────────┐               │
│  │ Default Prod WH  ▼  │ │ Lead Time   │               │
│  └─────────────────────┘ └─────────────┘               │
└─────────────────────────────────────────────────────────┘
```

### 15.2 Product Form — Stock Parameters (C1)

```
┌─────────────────────────────────────────────────────────┐
│  [General] [Classification] [Stock] [Pricing] [BOM]     │
│                              ▲ active                    │
│  ─────────────────────────────────────────────────────── │
│                                                         │
│  Stock Parameters                                       │
│  ┌────────────────┐ ┌────────────────┐                  │
│  │ Reorder Level  │ │ Reorder Qty    │                  │
│  └────────────────┘ └────────────────┘                  │
│  ┌────────────────┐ ┌────────────────┐                  │
│  │ Safety Stock   │ │ Max Stock Lvl  │                  │
│  └────────────────┘ └────────────────┘                  │
│                                                         │
│  Sale Order Limits                                      │
│  ┌────────────────────────────┐                         │
│  │ Sale Order Min Qty         │ Leave empty = no limit  │
│  └────────────────────────────┘                         │
│  ┌────────────────────────────┐                         │
│  │ Sale Order Max Qty         │ Leave empty = no limit  │
│  └────────────────────────────┘                         │
└─────────────────────────────────────────────────────────┘
```

### 15.3 Product Form — Inline BOM (C4)

```
┌─────────────────────────────────────────────────────────────┐
│  [General] [Classification] [Stock] [Pricing] [BOM]         │
│                                                ▲ active     │
│  ───────────────────────────────────────────────────────── │
│                                                             │
│  ┌── BOM List ──────────┐ ┌── BOM Editor ────────────────┐ │
│  │                      │ │                               │ │
│  │  [+ New BOM]         │ │  BOM-00042  v1  [Active ●]   │ │
│  │                      │ │                               │ │
│  │  ▌BOM-00042 v1 [ACT] │ │  Base Qty: [1.0000]          │ │
│  │   BOM-00041 v2 [OBS] │ │  UOM:      [Each ▼]          │ │
│  │   BOM-00041 v1 [OBS] │ │  Yield %:  [98.00]           │ │
│  │                      │ │  Effective: [2026-09-27] to   │ │
│  │                      │ │  [          ]                  │ │
│  │                      │ │                               │ │
│  │                      │ │  Components                   │ │
│  │                      │ │  ┌──────────────────────────┐│ │
│  │                      │ │  │ Product   Qty  W%  S% C ││ │
│  │                      │ │  │─────────────────────────││ │
│  │                      │ │  │ Steel Bar  2   1%  2% ● ││ │
│  │                      │ │  │ Bolt M6   12   0%  1%   ││ │
│  │                      │ │  │ Paint     0.5  5%  0%   ││ │
│  │                      │ │  │         [+ Add Line]     ││ │
│  │                      │ │  └──────────────────────────┘│ │
│  │                      │ │                               │ │
│  │  [Compare Versions]  │ │  [Create New Version] [Obs.] │ │
│  └──────────────────────┘ └───────────────────────────────┘ │
└─────────────────────────────────────────────────────────────┘
```

### 15.4 Production Order — Material Availability Tab (C8)

```
┌──────────────────────────────────────────────────────────────┐
│  Production Order: PROD-00085        Status: MaterialPending │
│  Sale Order: SO-1042 (line 3)        ← C7 link              │
│  ──────────────────────────────────────────────────────────  │
│  [Overview] [Material Availability] [Issues] [Timeline]      │
│                    ▲ active                                   │
│  ──────────────────────────────────────────────────────────  │
│                                                              │
│  Material Available                [Reserve All Available]   │
│  ┌────────────────────────────────────────────────────────┐  │
│  │ Product       Reqd  Avail  Warehouse   Status  Action │  │
│  │─────────────────────────────────────────────────────── │  │
│  │ Steel Bar     200   200    Main WH    ● Avail [Rsrv]  │  │
│  │ Bolt M6      1200  1200    Main WH    ● Avail [Rsrv]  │  │
│  └────────────────────────────────────────────────────────┘  │
│                                                              │
│  Material Shortage                                           │
│  ┌────────────────────────────────────────────────────────┐  │
│  │ Product       Reqd  Issued  Short  Status  Action     │  │
│  │─────────────────────────────────────────────────────── │  │
│  │ Paint         50    0       50    ● Short  [Alloc Req] │  │
│  │ Rubber Seal   100   40      60    ● Partial[Alloc Req] │  │
│  └────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────┘
```

### 15.5 Consolidated Purchase Required Dashboard (C9)

```
┌──────────────────────────────────────────────────────────────┐
│  Manufacturing → Purchase Required                           │
│  ──────────────────────────────────────────────────────────  │
│  Filter: [Supplier ▼] [Min Shortage ▼] [Sort: Urgency ▼]   │
│  ──────────────────────────────────────────────────────────  │
│                                                              │
│  ┌────────────────────────────────────────────────────────┐  │
│  │ Product    Shortage  POs  Earliest   Supplier  Action  │  │
│  │──────────────────────────────────────────────────────  │  │
│  │ Paint      350       5    Sep 29     ColorCo  [Put PO] │  │
│  │ Rubber     180       3    Oct 02     RubberLtd[Put PO] │  │
│  │ Epoxy      90        2    Oct 05     ChemCorp [Put PO] │  │
│  │ Steel Rod  50        1    Oct 10     SteelInc  PO-0892 │  │
│  │                                        ↑ already Draft │  │
│  └────────────────────────────────────────────────────────┘  │
│                                                              │
│  Clicking [Put PO] opens PO create form pre-filled.         │
│  Clicking PO-0892 opens the existing Draft PO for review.   │
└──────────────────────────────────────────────────────────────┘
```

---

## 16. Business Rules

**Consolidated business rules for all 10 changes.**

| Rule ID | Change | Rule Description | Enforcement Point |
|---|---|---|---|
| BR-C1-01 | C1 | Sale Order Min/Max validation fires ONLY when value is non-null AND > 0 | SaleOrderLineService |
| BR-C1-02 | C1 | When min is set but max is not → only minimum enforced | SaleOrderLineService |
| BR-C1-03 | C1 | When max is set but min is not → only maximum enforced | SaleOrderLineService |
| BR-C1-04 | C1 | Validation applies to each SO line independently | SaleOrderLineService |
| BR-C1-05 | C1 | On Product save: min must be <= max when both > 0 | ProductService |
| BR-C1-06 | C1 | Changing min/max does NOT retroactively validate existing SOs | N/A |
| BR-C3-01 | C3 | Auto-created POs are ALWAYS Draft — never auto-submitted | SupplyRequirementEngine |
| BR-C3-02 | C3 | Default supplier is pre-selected but editable before submit | PO Edit UI |
| BR-C3-03 | C3 | Quantity is pre-filled from SR but editable before submit | PO Edit UI |
| BR-C3-04 | C3 | PO cannot be submitted without supplier_id | PurchaseOrderService |
| BR-C3-05 | C3 | SR ↔ PO link is established at creation and immutable | SupplyRequirementEngine |
| BR-C3-06 | C3 | Changing PO qty does NOT update SR.required_qty | PurchaseOrderService |
| BR-C3-07 | C3 | Multiple SRs for same product may link to same Draft PO | SupplyRequirementEngine |
| BR-C4-01 | C4 | Manufacturing products MUST have at least one BOM | ProductService |
| BR-C4-02 | C4 | BOM section visible when product_type IN (FG, SF) OR supply_method=Manufacture | Product UI |
| BR-C6-01 | C6 | PMR.warehouse_id ALWAYS comes from ProductionOrder.production_warehouse_id | BOMExplosionService |
| BR-C7-01 | C7 | sale_order_id populated only for demand-driven POs, NULL for direct creation | FulfillmentReqService |
| BR-C8-01 | C8 | Reserve button calls IStockReservationService with PRODUCTION_ORDER source | Material Availability UI |
| BR-C8-02 | C8 | Allocation Request creates pending demand in AllocationEngine | Allocation Request dialog |
| BR-C9-01 | C9 | Purchase Required aggregates shortages across ALL active POs per product | PurchaseRequiredService |
| BR-C9-02 | C9 | Duplicate PO prevention: shows existing Draft PO instead of create button | Purchase Required UI |
| BR-C10-01 | C10 | GRN → Allocation is MANUAL (authorized user triggers) | Allocation Dashboard |
| BR-C10-02 | C10 | FGR → Allocation is AUTOMATIC (MediatR event handler) | FGRConfirmedEventHandler |
| BR-C10-03 | C10 | After allocation: items move from Shortage to Available on PO | MaterialReadinessService |
| BR-C10-04 | C10 | PO transitions MaterialPending → Ready only when ALL PMR shortages = 0 | MaterialReadinessService |

---

## 17. Test Scenarios

| Test ID | Change | Scenario | Expected Result |
|---|---|---|---|
| T-C1-01 | C1 | Create SO line with qty below product min | Validation error: qty below minimum |
| T-C1-02 | C1 | Create SO line with qty above product max | Validation error: qty above maximum |
| T-C1-03 | C1 | Create SO line with qty within min-max range | SO line created successfully |
| T-C1-04 | C1 | Create SO line when min/max are both NULL | No validation — any qty accepted |
| T-C1-05 | C1 | Create SO line when min/max are both 0 | No validation — any qty accepted |
| T-C1-06 | C1 | Save product with min > max (both > 0) | Validation error on product save |
| T-C1-07 | C1 | Create SO line when only min is set (max NULL) | Only min validated |
| T-C1-08 | C1 | Create SO line when only max is set (min NULL) | Only max validated |
| T-C3-01 | C3 | SR Engine creates PO for Purchase component | PO created in Draft, notification sent |
| T-C3-02 | C3 | Auto-created PO has default supplier | supplier_id = product.default_supplier_id |
| T-C3-03 | C3 | User changes supplier on Draft PO | Supplier updated, PO remains Draft |
| T-C3-04 | C3 | User changes quantity on Draft PO | Quantity updated, SR.required_qty unchanged |
| T-C3-05 | C3 | Submit PO without supplier | Validation error: supplier required |
| T-C4-01 | C4 | Save FinishedGood product without BOM | Validation error: BOM required |
| T-C4-02 | C4 | BOM section visible for Manufacture product | BOM panels shown on product form |
| T-C4-03 | C4 | BOM CRUD via inline editor | All BOM operations work inline |
| T-C5-01 | C5 | Create new BOM | effective_from defaults to today |
| T-C6-01 | C6 | Create BOM line | No warehouse field present |
| T-C6-02 | C6 | BOM explosion | PMR warehouse = PO production_warehouse_id |
| T-C7-01 | C7 | PO created from SO demand | sale_order_id populated, link shown |
| T-C7-02 | C7 | PO created directly | sale_order_id NULL, shows 'Directly Created' |
| T-C8-01 | C8 | Reserve available material on PO | Stock reserved, status = Reserved |
| T-C8-02 | C8 | Allocation Request on shortage | Pending demand created in AllocationEngine |
| T-C8-03 | C8 | Reserve All Available button | All available items reserved in batch |
| T-C9-01 | C9 | View Purchase Required dashboard | Aggregated per product, summed shortage |
| T-C9-02 | C9 | Create PO from Purchase Required | PO form opens with pre-filled data |
| T-C9-03 | C9 | Draft PO already exists for product | 'PO in Draft' link shown instead of create |
| T-C10-01 | C10 | GRN approved → manual allocation | Stock available, user triggers allocation |
| T-C10-02 | C10 | Allocation run after GRN | Shortage items move to Available on PO |
| T-C10-03 | C10 | All shortages resolved after allocation | PO status → Ready |
| T-C10-04 | C10 | Partial fulfillment | Some items move, PO stays MaterialPending |
| T-C10-05 | C10 | FGR → automatic allocation to SO | FG stock allocated to Sales Order demand |

---

## 18. Development Phases

### 18.1 Phase Breakdown

| Phase | Changes | Estimated Days | Dependencies |
|---|---|---|---|
| Phase A — Schema & Foundation | C1, C5, C6, C7 (migrations + entity updates) | 3 days | Addendum 30 Phase 1 complete |
| Phase B — Product UI Consolidation | C2, C4 (tab merge + inline BOM) | 5 days | Phase A |
| Phase C — Workflow Changes | C3 (Draft PO flow + notifications) | 3 days | Addendum 30 Phase 3 complete |
| Phase D — Material Availability & Allocation | C8, C10 (availability tab + GRN flow) | 5 days | Phase C |
| Phase E — Consolidated Purchase Required | C9 (dashboard + aggregation) | 4 days | Phase D |
| Phase F — Testing & Integration | All changes — integration tests | 3 days | Phase A–E |
| | **TOTAL** | **23 days** | |

### 18.2 Parallelization Opportunities

- Phase A + Phase C can run in parallel (schema changes are independent of workflow changes)
- Phase B can start after Phase A completes
- Phase D depends on Phase C (Draft PO flow must be in place)
- Phase E depends on Phase D (allocation flow feeds Purchase Required)
- With parallelization: critical path = A → B + (C → D → E) → F = **~18 dev days**

### 18.3 Risk Considerations

| Risk | Impact | Mitigation |
|---|---|---|
| Inline BOM editor complexity (C4) | Medium — two-panel layout with real-time validation | Reuse existing BOM API, only change UI entry point |
| Consolidated Purchase Required data performance (C9) | Low — aggregate query across PMRs | Indexed views or materialized summary table if needed |
| Manual vs automatic allocation confusion (C10) | Medium — users may expect GRN to auto-allocate | Clear UI messaging + 'Run Allocation' banner on GRN page |
| Removing warehouse from BOM (C6) breaks existing data | Low — BOM lines are templates | Migration safely drops column; no transactional data affected |

---

*SMS-FSD-ADD-031 v1.0 | Manufacturing Flow Corrections & UX Consolidation | Confidential — Internal Use Only*
