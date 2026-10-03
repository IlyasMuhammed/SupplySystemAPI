# SMS Task Register — Addendum 33: Fulfillment Routes & Sale Order Delivery Integration

**Document:** SMS-TR-ADD-033 v1.0 | **FSD:** SMS-FSD-ADD-033 v1.0
**Total:** 45 Tasks | 146 Hours | 19 Dev Days | 4 Migrations

---

## Phase A — Route Configuration (C1)

**Priority:** P0 — Critical Path | **Estimate:** 24 Hours (3 Days) | **FSD:** §3, §8 M1

### Track A1 — FulfillmentRoutes Backend

- [ ] **A33-PA-01** — Create FulfillmentRoutes + FulfillmentRouteSteps Tables (Migration M1) `4h`
  - Schema: `logistics` | FSD: §3.2, §3.3, §8.2 M1 | Depends: — | Tests: T-C1-01
  - CREATE TABLE logistics.FulfillmentRoutes: fulfillment_route_id INT PK, org_id INT FK, code NVARCHAR(30), name NVARCHAR(100), description NVARCHAR(500) NULL, is_default BIT, is_active BIT, is_system BIT, requires_packing BIT, requires_shipping BIT, display_order INT, created_at, updated_at. UQ on (org_id, code). Filtered index on (org_id, is_default) WHERE is_default = 1. CREATE TABLE logistics.FulfillmentRouteSteps: route_step_id INT PK, fulfillment_route_id INT FK, org_id INT FK, step_code NVARCHAR(20) CHECK IN ('PICK','PACK','STAGE','APPROVAL','GOODS_ISSUE','SHIP'), step_order INT, is_mandatory BIT, description NVARCHAR(200) NULL. UQ on (fulfillment_route_id, step_order), UQ on (fulfillment_route_id, step_code).

- [ ] **A33-PA-02** — FulfillmentRoute & FulfillmentRouteStep EF Core Entities & Configuration `3h`
  - Schema: `logistics` | FSD: §3.2, §3.3 | Depends: A33-PA-01 | Tests: —
  - Entity classes with navigation: FulfillmentRoute → Steps (ICollection). IEntityTypeConfiguration with HasQueryFilter(org_id). StepCode enum (PICK, PACK, STAGE, APPROVAL, GOODS_ISSUE, SHIP). AutoMapper profiles for Route and Step DTOs. FluentValidation: code max 30, unique per org; name max 100; step_code in allowed set.

- [ ] **A33-PA-03** — Seed Routes via Tenant Provisioning `2h`
  - Schema: `logistics, tenant` | FSD: §3.5, BR-C1-08 | Depends: A33-PA-01 | Tests: T-C1-07
  - Insert 3 seed routes per org on provisioning: PICK_ONLY (steps: PICK→GOODS_ISSUE), PICK_AND_SHIP (steps: PICK→GOODS_ISSUE→SHIP, is_default=1), PICK_PACK_SHIP (steps: PICK→PACK→GOODS_ISSUE→SHIP). All marked is_system=1. Compute requires_packing/requires_shipping from steps. Idempotent — skip if exists.

- [ ] **A33-PA-04** — FulfillmentRouteService — Validation & Business Rules `3h`
  - Schema: `logistics` | FSD: §3.2–§3.5, BR-C1-01–BR-C1-09 | Depends: A33-PA-02 | Tests: T-C1-03–T-C1-09
  - BR-C1-01: unique code per org. BR-C1-02: at most one is_default per org (clear previous on set). BR-C1-03: every route must have PICK + GOODS_ISSUE. BR-C1-04: step_order sequential from 1, no gaps. BR-C1-05: PICK must be first; GOODS_ISSUE must precede SHIP. BR-C1-06: system routes cannot be deleted. BR-C1-07: cannot deactivate while assigned to active variant or open SO line. BR-C1-09: compute requires_packing (PACK in steps) and requires_shipping (SHIP in steps) on save.

- [ ] **A33-PA-05** — Fulfillment Route CRUD API `3h`
  - Schema: `logistics` | FSD: §9.1 | Depends: A33-PA-04 | Tests: T-C1-01–T-C1-09
  - GET /api/fulfillment-routes (active for org, with steps, sorted by display_order). POST create (admin). GET /{id} with steps. PUT update name, description, steps (admin). PATCH /{id}/deactivate (admin, validates not in use). PATCH /{id}/set-default (admin, clears previous). Permissions: fulfillment_route_view, fulfillment_route_manage.

- [ ] **A33-PA-06** — Seed Fulfillment Route Permission Claims `1h`
  - Schema: `auth` | FSD: §13.3 | Depends: — | Tests: —
  - INSERT 3 permission claims: fulfillment_route_view, fulfillment_route_manage, fulfillment_route_assign. Assign fulfillment_route_view to default WAREHOUSE_MANAGER, SALES_MANAGER roles. Assign fulfillment_route_manage to ADMIN. Assign fulfillment_route_assign to ADMIN, WAREHOUSE_MANAGER.

### Track A2 — Route Configuration UI

- [ ] **A33-PA-07** — Fulfillment Route List Page `3h`
  - Schema: — (UI) | FSD: §10.1 | Depends: A33-PA-05 | Tests: —
  - Nav: Settings → Fulfillment Routes. Paginated table: Code, Name, Steps (abbreviated chain), Default (radio dot), System lock icon. [+ New Route] button. Click row → editor. System routes show lock icon and disabled delete.

- [ ] **A33-PA-08** — Fulfillment Route Editor Form `3h`
  - Schema: — (UI) | FSD: §10.2 | Depends: A33-PA-05 | Tests: —
  - Create/Edit form: Code input (read-only on edit for system routes), Name, Description textarea. Steps panel: ordered list with drag-to-reorder, each step shows step_code + is_mandatory toggle + description. [+ Add Step] dropdown showing only unused step_codes. Preview section: computed state machine path from steps. [Set as organization default] checkbox. [Save] [Cancel] buttons. Validation: PICK required, GOODS_ISSUE required, PICK first, GOODS_ISSUE before SHIP.

### Phase A Completion

- [ ] **A33-PA-09** — Phase A Unit Tests `2h`
  - Schema: `logistics, auth` | FSD: §12 | Depends: A33-PA-01–08 | Tests: T-C1-01–T-C1-09
  - Route CRUD, code uniqueness, default toggle (only one per org), step ordering validation, PICK+GOODS_ISSUE mandatory, step_code CHECK constraint, system route delete guard, in-use deactivation guard, requires_packing/requires_shipping computation, seed data integrity, permission claim seeding.

---

## Phase B — Variant Assignment (C2)

**Priority:** P0 — Critical Path | **Estimate:** 14 Hours (2 Days) | **FSD:** §4, §8 M2

> **⚡ Parallelizable:** Phase B UI can run in parallel with Phase C backend once Migration M2 is applied.

### Track B1 — Variant Route Backend

- [ ] **A33-PB-01** — Add fulfillment_route_id to ProductVariants (Migration M2) `2h`
  - Schema: `inventory` | FSD: §4.2, §8.2 M2 | Depends: A33-PA-01 | Tests: —
  - ALTER TABLE inventory.ProductVariants ADD fulfillment_route_id INT NULL FK → logistics.FulfillmentRoutes. CREATE INDEX IX_ProductVariants_FulfillmentRoute filtered WHERE NOT NULL. No backfill — existing variants start NULL.

- [ ] **A33-PB-02** — ProductVariant Entity & DTO Updates `3h`
  - Schema: `inventory` | FSD: §4.2, §9.2, BR-C2-01 | Depends: A33-PB-01 | Tests: T-C2-01, T-C2-02
  - Add FulfillmentRouteId (int?) to ProductVariant entity. Navigation property to FulfillmentRoute. Update Create/Update DTOs to accept fulfillment_route_id. Update Response DTO to include fulfillment_route_id, fulfillment_route_code, fulfillment_route_name. Update List DTO to include route code and name. Validation: if set, must reference active route in same org (BR-C2-01).

- [ ] **A33-PB-03** — Bulk Assignment Endpoint `3h`
  - Schema: `inventory, logistics` | FSD: §4.4, §9.1, BR-C2-02 | Depends: A33-PB-02 | Tests: T-C2-03
  - POST /api/fulfillment-routes/{routeId}/assign-by-category. Body: { product_category_id }. Sets fulfillment_route_id on all variants under that category WHERE fulfillment_route_id IS NULL. Returns count of updated variants. Does not overwrite existing assignments (BR-C2-02). Permission: fulfillment_route_assign.

### Track B2 — Variant Route UI

- [ ] **A33-PB-04** — Product Variant Form — Route Dropdown `3h`
  - Schema: — (UI) | FSD: §10.2 (variant form wireframe) | Depends: A33-PB-02 | Tests: T-C2-01
  - Logistics tab on product variant form: Fulfillment Route dropdown (routes for org, sorted by display_order). Below dropdown: step preview chain (e.g., "Steps: Pick → Goods Issue → Ship"). Info tooltip: "This route determines the warehouse operations when this variant is sold. Can be overridden per Sale Order line." Clear/unset option available.

### Phase B Completion

- [ ] **A33-PB-05** — Phase B Unit Tests `3h`
  - Schema: `inventory, logistics` | FSD: §12 | Depends: A33-PB-01–04 | Tests: T-C2-01–T-C2-04
  - Route assignment to variant, cross-org FK validation (route must be same org), bulk assignment by category (skip existing, count updated), clear route to NULL, list DTO includes route code/name.

---

## Phase C — SO Route Selection & Confirmation Gate (C3)

**Priority:** P0 — Critical Path | **Estimate:** 31 Hours (4 Days) | **FSD:** §5, §8 M3

> **⚠️ Sequenced:** Phase C depends on Phase A (route entities) and Phase B (variant route FK for resolution).

### Track C1 — Route Selection Backend

- [ ] **A33-PC-01** — Add fulfillment_route_id to SaleOrderLines (Migration M3) `2h`
  - Schema: `demand` | FSD: §5.2, §8.2 M3 | Depends: A33-PA-01 | Tests: —
  - ALTER TABLE demand.SaleOrderLines ADD fulfillment_route_id INT NULL FK → logistics.FulfillmentRoutes. CREATE INDEX IX_SaleOrderLines_FulfillmentRoute filtered WHERE NOT NULL. No backfill — existing SO lines pre-date route requirement.

- [ ] **A33-PC-02** — SaleOrderLine Entity & DTO Updates `2h`
  - Schema: `demand` | FSD: §5.2, §9.3, BR-C3-01 | Depends: A33-PC-01 | Tests: T-C3-01
  - Add FulfillmentRouteId (int?) to SaleOrderLine entity. Navigation property. Update Create/Update DTOs to accept optional fulfillment_route_id. Update Response DTO: effective_route_id, effective_route_code, route_source (VARIANT/LINE_OVERRIDE/ORG_DEFAULT/NONE). Validation: if set, must reference active route in same org (BR-C3-01). Route locked after SO confirmation (BR-C3-04).

- [ ] **A33-PC-03** — EffectiveRouteResolver Service `4h`
  - Schema: `demand, inventory, logistics` | FSD: §5.3, BR-C3-03 | Depends: A33-PB-02, A33-PC-02 | Tests: T-C3-01–T-C3-04
  - ResolveEffectiveRouteId(SaleOrderLine): Priority 1 — SO line override (FulfillmentRouteId). Priority 2 — variant default (ProductVariant.FulfillmentRouteId). Priority 3 — org default route (FulfillmentRoute WHERE is_default = 1). Priority 4 — NULL (blocks confirmation). Returns tuple: (routeId, source) where source is LINE_OVERRIDE/VARIANT/ORG_DEFAULT/NONE. Batch method: ResolveAllLines(SaleOrder) for UI and preview.

- [ ] **A33-PC-04** — SaleOrderConfirmationValidator — Route Gate `4h`
  - Schema: `demand, logistics` | FSD: §5.4, BR-C3-02 | Depends: A33-PC-03 | Tests: T-C3-05–T-C3-07
  - ValidateForConfirmation(SaleOrder): iterate all lines, resolve effective route per line. Collect errors: lines with NULL route (list line numbers), lines with inactive/deleted route. Return ValidationResult with clear messages: "Cannot confirm: lines {N, M} have no fulfillment route. Assign a route on each line or set a default route on the product variant." Block DRAFT → CONFIRMED transition on any error. Wire into existing SO confirmation flow.

- [ ] **A33-PC-05** — Delivery Preview Service `3h`
  - Schema: `demand, logistics` | FSD: §5.6, BR-C3-05 | Depends: A33-PC-03 | Tests: T-C3-09
  - SaleOrderDeliveryPreviewService.GetPreview(SaleOrder): group lines by effective route, return route groups with line counts and route step descriptions. Computed on the fly — not persisted (BR-C3-05). Flag unroutable lines. Used by UI preview panel and by GET /api/sale-orders/{id}/delivery-preview endpoint.

- [ ] **A33-PC-06** — Modified SO Line API Endpoints `3h`
  - Schema: `demand` | FSD: §9.3 | Depends: A33-PC-02, A33-PC-03, A33-PC-04, A33-PC-05 | Tests: T-C3-05, T-C3-08
  - POST /api/sale-orders/{id}/lines — accept optional fulfillment_route_id per line. PUT /api/sale-orders/{id}/lines/{lineId} — accept optional fulfillment_route_id update (DRAFT only, BR-C3-04). GET /api/sale-orders/{id}/lines — return effective_route_id, effective_route_code, route_source per line. POST /api/sale-orders/{id}/confirm — call validator; on success trigger delivery creation (Phase D). GET /api/sale-orders/{id}/delivery-preview — return preview grouping.

### Track C2 — SO Route UI

- [ ] **A33-PC-07** — SO Lines Table — Route Column & Source Indicator `4h`
  - Schema: — (UI) | FSD: §10.3 (SO lines wireframe) | Depends: A33-PC-06 | Tests: T-C3-01, T-C3-02
  - New "Route" column in SO Lines table: dropdown with org routes (editable in DRAFT, read-only after confirmation). Source indicator below each line: "Source: variant default" (ⓥ icon), "Source: overridden on SO line" (✎ icon), "Source: org default" (⊙ icon). Warning row for lines with no route: "⚠ No route defined — order cannot be confirmed" with ⚠ icon in Action column. Legend row: ⓥ = inherited from variant, ✎ = overridden on line, ⚠ = missing route (blocking).

- [ ] **A33-PC-08** — Delivery Preview Panel `3h`
  - Schema: — (UI) | FSD: §5.6 | Depends: A33-PC-05 | Tests: T-C3-09
  - Preview section below SO lines table (DRAFT status only): "On confirmation, N delivery orders will be created:" followed by grouped list — route name, line count, step chain. Unroutable lines shown as "⚠ unroutable — assign route first". Real-time update as salesperson changes routes on lines. Collapsed by default, expandable.

- [ ] **A33-PC-09** — Confirm Button — Disabled State & Route Warnings `2h`
  - Schema: — (UI) | FSD: §5.5 (critical note) | Depends: A33-PC-04 | Tests: T-C3-06
  - [Confirm Order] button DISABLED (not hidden) when any line lacks effective route. Tooltip on disabled button: "N line(s) missing fulfillment route". Warning banner at top of Lines tab when missing routes exist. Button enabled when all lines resolve. On click → call confirm API → show success/error.

### Phase C Completion

- [ ] **A33-PC-10** — Phase C Unit & Integration Tests `4h`
  - Schema: `demand, inventory, logistics` | FSD: §12 | Depends: A33-PC-01–09 | Tests: T-C3-01–T-C3-09
  - Effective route resolution (all 4 priorities), confirmation gate blocking (missing route, inactive route), confirmation success, route locked after confirm, delivery preview grouping (same route = 1 DO, mixed = N DOs), route override on SO line, route source reporting.

---

## Phase D — Delivery Creation from Sale Order (C4)

**Priority:** P0 — Critical Path | **Estimate:** 30 Hours (4 Days) | **FSD:** §6, §8 M4

> **⚠️ Sequenced:** Phase D depends on Phase C (confirmation gate triggers delivery creation).

### Track D1 — Delivery Creation Backend

- [ ] **A33-PD-01** — Extend delivery_orders & delivery_order_lines (Migration M4) `3h`
  - Schema: `logistics` | FSD: §6.2, §8.2 M4 | Depends: — | Tests: —
  - ALTER TABLE logistics.delivery_orders ADD sale_order_id BIGINT NULL FK → demand.SaleOrders, fulfillment_route_id INT NULL FK → logistics.FulfillmentRoutes. Filtered indexes on both. ALTER TABLE logistics.delivery_order_lines ADD sale_order_line_id BIGINT NULL FK → demand.SaleOrderLines. Filtered index. from_source_type column already NVARCHAR — new value 'SALE_ORDER' requires no DDL, only application-level validation update.

- [ ] **A33-PD-02** — DeliveryOrder Entity & DTO Updates `3h`
  - Schema: `logistics` | FSD: §6.2, §9.4 | Depends: A33-PD-01 | Tests: T-C4-04
  - Add SaleOrderId (long?), FulfillmentRouteId (int?) to DeliveryOrder entity. Navigation properties. Add SaleOrderLineId (long?) to DeliveryOrderLine entity. Update Response DTOs: include fulfillment_route_id, fulfillment_route_code, sale_order_id. Update from_source_type validation to accept 'SALE_ORDER'. Update list DTO to support filter by from_source_type and sale_order_id.

- [ ] **A33-PD-03** — SaleOrderDeliveryCreator Service `6h`
  - Schema: `demand, logistics` | FSD: §6.3, §6.4, BR-C4-01–BR-C4-05 | Depends: A33-PC-03, A33-PD-02 | Tests: T-C4-01–T-C4-03
  - CreateDeliveriesFromSaleOrder(SaleOrder): group SO lines by effective fulfillment route (BR-C4-02). Per group: create DeliveryOrder with from_source_type='SALE_ORDER', sale_order_id, fulfillment_route_id, warehouse_id, status=DRAFT (BR-C4-03), expected_date from earliest line. Create DeliveryOrderLine per SO line with sale_order_line_id (BR-C4-05). Batch insert. Set SO.HasDeliveries = true. Wire into SO confirmation flow (called after validation passes).

- [ ] **A33-PD-04** — SO Cancellation → Delivery Cancellation Logic `3h`
  - Schema: `demand, logistics` | FSD: BR-C4-06, BR-C4-07 | Depends: A33-PD-03 | Tests: T-C4-06, T-C4-07
  - On SO status → CANCELLED: iterate linked delivery orders. Cancel deliveries NOT yet GOODS_ISSUED (DRAFT, RELEASED, PICKING, PICKED, PACKED, STAGED, PENDING_APPROVAL) — BR-C4-06. Deliveries past GOODS_ISSUED (GOODS_ISSUED, IN_TRANSIT, etc.) cannot be auto-cancelled — require manual reversal (BR-C4-07). Return list of cancelled vs skipped deliveries for user feedback.

- [ ] **A33-PD-05** — Modified Delivery Order API — Source & SO Filters `3h`
  - Schema: `logistics` | FSD: §9.4 | Depends: A33-PD-02 | Tests: T-C4-04, T-C4-05
  - GET /api/delivery-orders — add query params: from_source_type (filter by SALE_ORDER, PO, etc.), sale_order_id (filter by originating SO). GET /api/delivery-orders/{id} — return fulfillment_route_id, fulfillment_route_code, sale_order_id, allowed_transitions (route-aware, Phase E wires this). GET /api/sale-orders/{id}/deliveries — convenience endpoint listing DOs for a specific SO.

### Track D2 — SO Fulfillment UI

- [ ] **A33-PD-06** — Sale Order Fulfillment Tab `5h`
  - Schema: — (UI) | FSD: §10.4 (SO fulfillment tab wireframe) | Depends: A33-PD-05 | Tests: T-C4-05
  - New "Fulfillment" tab on SO Detail page (visible after confirmation). Table: Delivery #, Route name, Line count, Status badge, [View] action. Expandable sub-row per DO showing line details (product × qty) and current step context. Overall fulfillment progress bar: "N of M lines delivered". Progress percentage computed from fulfilled lines.

- [ ] **A33-PD-07** — Delivery Order List — Source Type Filter `3h`
  - Schema: — (UI) | FSD: §9.4 | Depends: A33-PD-05 | Tests: —
  - Delivery Orders list page: add "Source Type" filter dropdown (All, PO, SRO, MIV, Transfer, Sale Order). Sale Order column: clickable SO reference (link to SO detail) or "—" for non-SO deliveries. Route column: route code badge.

### Phase D Completion

- [ ] **A33-PD-08** — Phase D Unit & Integration Tests `4h`
  - Schema: `demand, logistics` | FSD: §12 | Depends: A33-PD-01–07 | Tests: T-C4-01–T-C4-07
  - Delivery creation grouping (all same route → 1 DO, mixed → N DOs), from_source_type = SALE_ORDER, SO line linkage, SO cancellation cascade (pre-GI cancelled, post-GI skipped), source filter API, fulfillment tab rendering.

---

## Phase E — Route-Aware State Machine (C5)

**Priority:** P0 — Critical Path | **Estimate:** 23 Hours (3 Days) | **FSD:** §7

> **⚠️ Sequenced:** Phase E depends on Phase D (delivery orders must have fulfillment_route_id).

### Track E1 — State Machine Backend

- [ ] **A33-PE-01** — RouteAwareDeliveryStateMachine Service `6h`
  - Schema: `logistics` | FSD: §7.2, §7.3, BR-C5-01–BR-C5-03 | Depends: A33-PD-02 | Tests: T-C5-01–T-C5-07
  - GetNextStatus(DeliveryOrder, currentStatus): load route steps → build step_code set → walk the full 15-status chain from current position → skip statuses whose step_code is not in the route → return first status whose step is present (BR-C5-01). Status-to-step mapping: RELEASED/PICKING/PICKED→PICK, PACKED→PACK, STAGED→STAGE, PENDING_APPROVAL→APPROVAL, GOODS_ISSUED→GOODS_ISSUE, IN_TRANSIT through DELIVERED→SHIP. DRAFT and COMPLETED always valid. GetAllowedTransitions(DeliveryOrder): return ordered list of remaining statuses in route path (for UI step tracker). Backward compatibility: if fulfillment_route_id is NULL (non-SO deliveries), use full chain (existing behavior).

- [ ] **A33-PE-02** — Modified Advance API Endpoint `3h`
  - Schema: `logistics` | FSD: §9.4, BR-C5-03 | Depends: A33-PE-01 | Tests: T-C5-01–T-C5-06
  - POST /api/delivery-orders/{id}/advance — use RouteAwareDeliveryStateMachine.GetNextStatus instead of linear chain. Return new status + remaining route steps. Validation: if delivery has a route, only route-valid transitions allowed. If delivery has no route (legacy), full chain as before.

- [ ] **A33-PE-03** — Cancellation Logic — Route Independence `2h`
  - Schema: `logistics` | FSD: §7.6, BR-C5-05 | Depends: A33-PE-01 | Tests: T-C5-08
  - Cancellation (→ CANCELLED) available from any status regardless of route (BR-C5-05). No change to existing cancellation logic — verify and add test coverage. If delivery has linked SO, trigger SO delivery status recalculation (update fulfillment progress).

### Track E2 — Step Tracker UI

- [ ] **A33-PE-04** — Delivery Order Step Tracker — Route-Aware `5h`
  - Schema: — (UI) | FSD: §10.3 (step tracker wireframe), BR-C5-04 | Depends: A33-PE-01 | Tests: T-C5-09, T-C5-10
  - Delivery detail page: horizontal step tracker showing ONLY steps in the delivery's route (BR-C5-04). Each step: ✓ (done), ● (current), ○ (pending). Labels from step_code display names. Connected by arrow lines. PICK_ONLY: Pick → Goods Issue → Complete. PICK_AND_SHIP: Pick → Goods Issue → Ship → Complete. PICK_PACK_SHIP: Pick → Pack → Goods Issue → Ship → Complete. Full route: all steps shown. Responsive: stack vertically on mobile.

- [ ] **A33-PE-05** — Delivery Detail Page — Route Badge & Info `3h`
  - Schema: — (UI) | FSD: §10.3 | Depends: A33-PD-02 | Tests: —
  - Delivery detail header: "Route: {route_name}" badge. "Sale Order: {SO reference}" link (if from_source_type = SALE_ORDER). "Customer: {partner_name}" below. Lines table: add Picked/Packed/Shipped quantity columns for route-aware progress. Action button label matches current step: [Start Packing], [Post Goods Issue], [Dispatch for Shipment] etc.

### Phase E Completion

- [ ] **A33-PE-06** — Phase E Unit Tests `4h`
  - Schema: `logistics` | FSD: §12 | Depends: A33-PE-01–05 | Tests: T-C5-01–T-C5-10
  - Route-aware next status for all 3 seed routes + full route. Skip logic verification (PICK_ONLY skips PACK/STAGE/APPROVAL/SHIP statuses). PICK_AND_SHIP skips PACK/STAGE/APPROVAL. PICK_PACK_SHIP skips STAGE/APPROVAL. Cancellation from any status. Step tracker rendering per route. Backward compatibility: NULL route → full chain.

---

## Phase F — Testing & Integration (All Changes)

**Priority:** P0 — Critical Path | **Estimate:** 24 Hours (3 Days) | **FSD:** §12

> **⚠️ Sequenced:** Phase F depends on all previous phases.

### Track F1 — End-to-End Integration Tests

- [ ] **A33-PF-01** — E2E: Full Chain — SO Confirmation → Delivery Creation → Fulfillment `5h`
  - Schema: `demand, logistics, inventory` | FSD: §12 | Depends: All | Tests: T-C3-05, T-C4-01, T-C5-01
  - Set up routes → assign to variants → create SO → set routes on lines → confirm SO → verify delivery orders created with correct routes → advance each DO through its route path → verify each skips correct statuses → complete deliveries → verify SO fulfillment progress = 100%.

- [ ] **A33-PF-02** — E2E: Mixed Routes on Single SO (Punjab Group Scenario) `4h`
  - Schema: `demand, logistics` | FSD: §6.4 | Depends: All | Tests: T-C4-01–T-C4-03
  - Create SO with 3 lines: Steel Pipes (PICK_ONLY), Copper Wire (PICK_PACK_SHIP), Synth Hyd Oil (PICK_AND_SHIP). Confirm → verify 3 DOs created, 1 line each. Advance DO-1: DRAFT→RELEASED→PICKING→PICKED→GOODS_ISSUED→COMPLETED (skip PACK/STAGE/APPROVAL/SHIP). Advance DO-2: full pick→pack→goods issue→ship→deliver→complete. Advance DO-3: pick→goods issue→ship→deliver→complete (skip PACK/STAGE/APPROVAL). Verify all SO line linkages maintained.

- [ ] **A33-PF-03** — E2E: Confirmation Gate Blocking Scenarios `3h`
  - Schema: `demand, logistics, inventory` | FSD: §5.4, §12 | Depends: A33-PC-10 | Tests: T-C3-06, T-C3-07
  - SO with 1 line missing route (no variant default, no org default, no line override) → confirm blocked with message. SO with 1 line pointing to inactive route → confirm blocked. Fix: assign route on line → confirm succeeds. Fix: set org default → confirm succeeds for all lines without explicit route. Verify Confirm button disabled state and tooltip in UI.

- [ ] **A33-PF-04** — E2E: RBAC Permission Verification `3h`
  - Schema: `auth, logistics` | FSD: §13.3 | Depends: All | Tests: —
  - Test every endpoint with: no permission (403), view-only (200 GET, 403 POST/PUT), manage (200 all). Verify: fulfillment_route_view (list/get), fulfillment_route_manage (create/edit/deactivate/set-default), fulfillment_route_assign (bulk assign, variant route set). Verify existing DELIVERY_EDIT, PICKING, PACKING, DISPATCH permissions still work for SO-originated deliveries.

- [ ] **A33-PF-05** — E2E: Multi-Tenant Isolation `3h`
  - Schema: all | FSD: — | Depends: All | Tests: —
  - Create routes in Tenant A and Tenant B. Verify HasQueryFilter: Tenant A cannot see/assign Tenant B routes. Tenant A variant cannot reference Tenant B route. SO in Tenant A cannot use Tenant B route. Delivery orders scoped by org_id. Seed routes exist independently per tenant.

- [ ] **A33-PF-06** — Regression: Existing Delivery Sources (PO/SRO/MIV/Transfer) `3h`
  - Schema: `logistics` | FSD: §2 | Depends: A33-PE-06 | Tests: —
  - Existing delivery order creation for PO, SRO, MIV, Transfer sources unaffected. These have fulfillment_route_id = NULL → state machine uses full 15-status chain (backward compatible). No new validation blocks existing flows. Existing pick, pack, ship operations work unchanged. Verify 1531 existing logistics tests pass.

- [ ] **A33-PF-07** — Final Code Review & Documentation `3h`
  - Schema: all | FSD: all | Depends: A33-PF-01–06 | Tests: —
  - Code review: all new services, entities, migrations. Swagger/OpenAPI annotations on all new/modified endpoints. README updates for fulfillment routes. Verify entity relationship diagram accuracy. Verify all 30 business rules have test coverage.

---

## Parallelization Map

```
Phase A (3 days) ─────────────────┐
                                  ├──→ Phase B (2 days) ──┐
                                  │                       ├──→ Phase C (4 days) ──→ Phase D (4 days) ──→ Phase E (3 days) ──┐
                                  │                       │                                                                 │
                                  └───────────────────────┘                                                                 │
                                                                                                                            │
                                                                                                                            └──→ Phase F (3 days)

Parallel agents:
  Agent 1: Phase A (backend) → Phase B (backend) → Phase C (backend) → Phase D (backend) → Phase E (backend)
  Agent 2: Phase A (UI) → Phase B (UI) → Phase C (UI) → Phase D (UI) → Phase E (UI)
  Agent 3: Phase F (after A–E complete)

Calendar time with 2 agents: 14 days (vs 19 sequential)
```

---

## Dependency Chain

```
M1 (FulfillmentRoutes)
├── M2 (ProductVariants FK) ──→ EffectiveRouteResolver ──→ ConfirmationValidator ──→ DeliveryCreator
├── M3 (SaleOrderLines FK) ──→ EffectiveRouteResolver
└── M4 (delivery_orders FK) ──→ DeliveryCreator ──→ RouteAwareStateMachine
```

---

## Task Summary

| Phase | Tasks | Hours | Days | Parallel Group |
|---|---|---|---|---|
| A — Route Configuration (C1) | 9 | 24 | 3 | Foundation |
| B — Variant Assignment (C2) | 5 | 14 | 2 | Foundation |
| C — SO Route Selection & Gate (C3) | 10 | 31 | 4 | Core Logic |
| D — Delivery Creation from SO (C4) | 8 | 30 | 4 | Core Logic |
| E — Route-Aware State Machine (C5) | 6 | 23 | 3 | Core Logic |
| F — Testing & Integration | 7 | 24 | 3 | Final |
| **TOTAL** | **45** | **146** | **19** | |

---

*End of Task Register — Addendum 33*
