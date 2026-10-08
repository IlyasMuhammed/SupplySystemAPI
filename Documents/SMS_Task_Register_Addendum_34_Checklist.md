# SMS Task Register — Addendum 34: Route Classification, Lead Time & Production-to-Delivery Integration

**Document:** SMS-TR-ADD-034 v1.0 | **FSD:** SMS-FSD-ADD-034 v1.0
**Total:** 54 Tasks | 194 Hours | 25 Dev Days (16 with parallelization) | 4 Migrations

---

## Phase A — Route Category & Product Classification (C1, C2)

**Priority:** P0 — Critical Path | **Estimate:** 30 Hours (4 Days) | **FSD:** §3, §4, §9 M1

### Track A1 — Route Category Backend (C1)

- [ ] **A34-PA-01** — Add route_category Column to FulfillmentRoutes (Migration M1 — DDL) `2h`
  - Schema: `logistics` | FSD: §3.3, §9.2 M1 | Depends: ADD-033 complete | Tests: T-C1-01
  - ALTER TABLE logistics.FulfillmentRoutes ADD route_category NVARCHAR(20) NOT NULL DEFAULT 'STOCK'. CONSTRAINT CK_FulfillmentRoutes_Category CHECK (route_category IN ('STOCK', 'MANUFACTURE', 'BUY', 'DROPSHIP')). Backfill all existing rows to 'STOCK' (all Addendum 33 seed routes are stock-based).

- [ ] **A34-PA-02** — Insert Manufacturing Seed Routes per Existing Org (Migration M1 — DML) `3h`
  - Schema: `logistics` | FSD: §3.4, §3.5, §9.2 M1 | Depends: A34-PA-01 | Tests: T-C1-04
  - Cursor over DISTINCT org_id in logistics.FulfillmentRoutes. Per org INSERT: MFG_PICK_SHIP (route_category='MANUFACTURE', steps: PICK→GOODS_ISSUE→SHIP, requires_packing=0, requires_shipping=1, display_order=40, is_system=1) and MFG_PICK_PACK_SHIP (route_category='MANUFACTURE', steps: PICK→PACK→GOODS_ISSUE→SHIP, requires_packing=1, requires_shipping=1, display_order=50, is_system=1). Both is_default=0, is_active=1. Insert corresponding FulfillmentRouteSteps rows with correct step_order. Idempotent — skip if code already exists for org.

- [ ] **A34-PA-03** — FulfillmentRoute Entity & DTO Updates for route_category `2h`
  - Schema: `logistics` | FSD: §3.3, §10.1, §10.2 | Depends: A34-PA-01 | Tests: —
  - Add RouteCategory (string) property to FulfillmentRoute entity. IEntityTypeConfiguration: HasMaxLength(20), IsRequired, HasDefaultValue("STOCK"). Update FulfillmentRouteResponseDto to include route_category. Update FulfillmentRouteCreateDto and FulfillmentRouteUpdateDto to accept route_category (default "STOCK"). Update AutoMapper profiles. Add RouteCategory enum (STOCK, MANUFACTURE, BUY, DROPSHIP) for type safety in application code.

- [ ] **A34-PA-04** — FulfillmentRouteService — Category Validation & Business Rules `4h`
  - Schema: `logistics` | FSD: §3.2, BR-C1-01–BR-C1-04 | Depends: A34-PA-03 | Tests: T-C1-01–T-C1-03
  - BR-C1-01: route_category must be one of STOCK, MANUFACTURE, BUY, DROPSHIP — FluentValidation + CHECK constraint. BR-C1-02: route_category cannot be changed on a route assigned to active product variants or open SO lines — query ProductVariants and SaleOrderLines for active references. BR-C1-03: BUY and DROPSHIP are reserved — reject creation/update with these categories until future activation. BR-C1-04: manufacturing seed routes (MFG_PICK_SHIP, MFG_PICK_PACK_SHIP) are system routes — block delete (existing is_system guard covers this). Update tenant provisioning to include manufacturing seed routes on new org creation.

- [ ] **A34-PA-05** — Route CRUD API — Accept/Return route_category `2h`
  - Schema: `logistics` | FSD: §10.1, §10.2 | Depends: A34-PA-04 | Tests: T-C1-01
  - GET /api/fulfillment-routes — return route_category in list and detail DTOs. POST /api/fulfillment-routes — accept route_category in create body (default STOCK). PUT /api/fulfillment-routes/{id} — accept route_category in update body (blocked if route is in use, per BR-C1-02). Add route_category as optional filter query param on GET list endpoint. Swagger annotation updates.

### Track A2 — Route-Based Product Classification (C2)

- [ ] **A34-PA-06** — BOM Tab Visibility Rule — Route-Category Driven `3h`
  - Schema: `inventory, logistics` | FSD: §4.3, BR-C2-01, BR-C2-04 | Depends: A34-PA-03 | Tests: T-C2-01, T-C2-02
  - Replace existing BOM tab visibility condition. Before: `productType.In(FG, SF) || supplyMethod == Manufacture || isManufacturable`. After: `variant.FulfillmentRoute?.RouteCategory == "MANUFACTURE"`. Server-side: ProductVariant detail endpoint includes `show_bom_tab` computed field (JOIN to FulfillmentRoute for route_category). Client-side: BOM tab render condition reads show_bom_tab from API response. BR-C2-04: changing route from MANUFACTURE to STOCK hides tab but preserves existing BOMs in database.

- [ ] **A34-PA-07** — Auto-Computed Flags — is_manufacturable Sync from Route `2h`
  - Schema: `inventory, logistics` | FSD: §4.5, BR-C2-03 | Depends: A34-PA-06 | Tests: T-C2-01, T-C2-02
  - ProductVariantService: on fulfillment_route_id change, auto-update is_manufacturable = (route_category == 'MANUFACTURE'). Auto-update supply_method = 'Manufacture' when route_category == 'MANUFACTURE' (keep current value otherwise). Fire only on route assignment change — NOT on route category change (route category changes on in-use routes are blocked by BR-C1-02). Log sync action for audit trail.

- [ ] **A34-PA-08** — BOM Mandatory Validation for MANUFACTURE Route Products on SO `2h`
  - Schema: `inventory, material` | FSD: §4.3, BR-C2-02 | Depends: A34-PA-06 | Tests: T-C2-03
  - ProductVariant validation: when route_category = 'MANUFACTURE', at least one active BOM must exist before the variant can be added to a Sale Order line. Validation message: "This product's fulfillment route requires manufacturing. At least one active Bill of Material is required." Check point: SaleOrderLine creation/update when variant is selected. Check point: SO confirmation (redundant safety check). Query: BOM WHERE variant_id = @variantId AND is_active = 1 AND (effective_to IS NULL OR effective_to >= GETUTCDATE()).

### Track A3 — Phase A UI

- [ ] **A34-PA-09** — Route Editor UI — Category Dropdown + MANUFACTURE Info Note `2h`
  - Schema: — (UI) | FSD: §3.6 | Depends: A34-PA-05 | Tests: —
  - Route Editor form (Addendum 33 §10.2): add Category dropdown at top of form. Options: STOCK (default), MANUFACTURE. BUY and DROPSHIP shown but disabled with tooltip "Reserved for future release." When category = MANUFACTURE, show info banner below dropdown: "Products with this route will trigger a Production Order at Sale Order confirmation. Delivery is created after production completes." Category read-only on system routes. Category editable only when route has no active assignments (BR-C1-02) — disable with tooltip when in use.

- [ ] **A34-PA-10** — Product Classification Tab — Updated Layout with Route Reference `3h`
  - Schema: — (UI) | FSD: §4.4 | Depends: A34-PA-07 | Tests: T-C2-04
  - Classification tab (Addendum 31 C2): add Fulfillment Route dropdown above existing fields. Route Category badge (read-only) next to route dropdown — styled pill: STOCK (green), MANUFACTURE (orange). is_manufacturable checkbox rendered read-only with "(auto-computed from route)" label. supply_method dropdown remains editable (user can override). is_bom_input, is_saleable, is_purchasable, is_stockable checkboxes unchanged. Product list page: add "Route Category" column, filterable — replaces/supplements is_manufacturable filter.

### Phase A Completion

- [ ] **A34-PA-11** — Phase A Unit Tests `4h`
  - Schema: `logistics, inventory, material` | FSD: §12 | Depends: A34-PA-01–10 | Tests: T-C1-01–T-C1-04, T-C2-01–T-C2-04
  - Route category CREATE (STOCK, MANUFACTURE), reject BUY/DROPSHIP. Category change blocked when route in use. Seed routes: 5 per org (3 STOCK + 2 MANUFACTURE) after provisioning. System route delete guard on MFG routes. BOM tab visible only for MANUFACTURE route variants. BOM tab hidden on route change to STOCK — BOMs preserved. is_manufacturable auto-sync on route assignment. BOM mandatory validation on SO line for MANUFACTURE route. Legacy filter (is_manufacturable=1) returns same set as route_category='MANUFACTURE'.

---

## Phase B — Lead Time Management (C3)

**Priority:** P1 — Parallel Track 1 | **Estimate:** 31 Hours (4 Days) | **FSD:** §5, §9 M2

> **⚡ Parallelizable:** Phase B runs in parallel with Phase D after Phase A completes. Phase B feeds Phase C (lead time schema before calculation service).

### Track B1 — Schema & Backend

- [ ] **A34-PB-01** — Create LeadTimeDefaults Table + ProductVariant Lead Time Columns (Migration M2) `3h`
  - Schema: `lookups, inventory` | FSD: §5.4, §5.3, §9.2 M2 | Depends: A34-PA-01 | Tests: —
  - CREATE TABLE lookups.LeadTimeDefaults: lead_time_default_id INT PK IDENTITY, org_id INT FK → tenants.Organizations, pick_pack_days INT NOT NULL DEFAULT 1, shipping_lead_time_days INT NOT NULL DEFAULT 3, sales_buffer_days INT NOT NULL DEFAULT 1, manufacturing_buffer_days INT NOT NULL DEFAULT 0, quality_inspection_days INT NOT NULL DEFAULT 0, internal_transfer_days INT NOT NULL DEFAULT 0, created_at DATETIME2 DEFAULT SYSUTCDATETIME(), updated_at DATETIME2 DEFAULT SYSUTCDATETIME(). UQ on (org_id). Seed one row per existing org. ALTER TABLE inventory.ProductVariants ADD: supplier_lead_time_days INT NULL, manufacturing_lead_time_days INT NULL, manufacturing_buffer_days INT NULL, quality_inspection_days INT NULL, internal_transfer_days INT NULL, pick_pack_days INT NULL, shipping_lead_time_days INT NULL, sales_buffer_days INT NULL.

- [ ] **A34-PB-02** — Migrate Existing lead_time_days to manufacturing_lead_time_days `2h`
  - Schema: `inventory, lookups` | FSD: §9.2 M2 | Depends: A34-PB-01 | Tests: —
  - UPDATE ProductVariants pv SET pv.manufacturing_lead_time_days = p.lead_time_days FROM inventory.ProductVariants pv JOIN lookups.Products p ON pv.product_id = p.product_id WHERE p.lead_time_days IS NOT NULL AND p.lead_time_days > 0 AND p.is_manufacturable = 1. Only migrate for manufacturing products — stock products' lead_time_days is not a manufacturing component. Verify count before/after for migration report.

- [ ] **A34-PB-03** — LeadTimeDefaults Entity, Configuration & Service `3h`
  - Schema: `lookups` | FSD: §5.4, BR-C3-03 | Depends: A34-PB-01 | Tests: T-C3-06
  - LeadTimeDefaults entity with IEntityTypeConfiguration: HasQueryFilter(org_id), UQ on org_id. LeadTimeDefaultsService: GetByOrgAsync(orgId), CreateOrUpdateAsync(orgId, dto). Validation: all values must be non-negative integers (BR-C3-01). Auto-create on tenant provisioning (BR-C3-03). AutoMapper profile for LeadTimeDefaultsDto. FluentValidation: each field >= 0, org_id required.

- [ ] **A34-PB-04** — LeadTimeDefaults CRUD API Endpoints `2h`
  - Schema: `lookups` | FSD: §10.1 | Depends: A34-PB-03 | Tests: —
  - GET /api/lead-time/defaults — returns org-level defaults for authenticated user's org. PUT /api/lead-time/defaults — update org defaults (admin permission: lead_time_defaults_manage). Seed permission claim: lead_time_defaults_manage assigned to ADMIN role. Response DTO includes all 6 component defaults with current values. Swagger annotations.

- [ ] **A34-PB-05** — ProductVariant Entity & DTO Updates for Lead Time Columns `3h`
  - Schema: `inventory` | FSD: §5.3, §10.2 | Depends: A34-PB-01 | Tests: T-C3-01, T-C3-04, T-C3-05
  - Add 8 nullable INT properties to ProductVariant entity: SupplierLeadTimeDays, ManufacturingLeadTimeDays, ManufacturingBufferDays, QualityInspectionDays, InternalTransferDays, PickPackDays, ShippingLeadTimeDays, SalesBufferDays. Update ProductVariantResponseDto to include all 8 fields + resolved totals (using LeadTimeResolver). Update ProductVariantUpdateDto to accept all 8 fields. FluentValidation: each field must be >= 0 when provided (BR-C3-01).

- [ ] **A34-PB-06** — LeadTimeResolver Service — Component Priority Resolution `4h`
  - Schema: `inventory, lookups, logistics` | FSD: §5.5, §5.6, BR-C3-02, BR-C3-04, BR-C3-05 | Depends: A34-PB-03, A34-PB-05 | Tests: T-C3-01–T-C3-05
  - ResolveComponent(ProductVariant, string component, LeadTimeDefaults orgDefaults): Priority 1 — variant-level override (if not NULL). Priority 2 — org default. Priority 3 — 0 (fallback). ResolveAllComponents(ProductVariant): returns list of LeadTimeComponentDto with name, days, source ("Variant override" / "Org default"). Component visibility: manufacturing components hidden when route_category ≠ 'MANUFACTURE' (BR-C3-04). Shipping component hidden when route has no SHIP step (BR-C3-05). Returns only applicable components for the variant's route.

### Track B2 — UI

- [ ] **A34-PB-07** — Product Variant — Lead Times Tab `6h`
  - Schema: — (UI) | FSD: §11.1 | Depends: A34-PB-06 | Tests: T-C3-01–T-C3-05
  - New "Lead Times" tab on Product Variant detail (between Stock and BOM tabs). Header: Route name + category badge. Table: Component name | Days (editable input) | Source label. Source labels: "⏱ From BOM calc", "Variant override", "Org default". Manufacturing components (mfg lead time, mfg buffer) shown only for MANUFACTURE routes. Shipping component shown only when route has SHIP step. Footer row: TOTAL LEAD TIME (sum). [Recalculate from BOM] button — calls lead time calculator (Phase C) to refresh manufacturing_lead_time_days. [Reset to Org Defaults] button — sets all variant fields to NULL. Save persists non-NULL overrides to ProductVariant.

- [ ] **A34-PB-08** — Lead Time Defaults Settings Page `3h`
  - Schema: — (UI) | FSD: §11.5 | Depends: A34-PB-04 | Tests: —
  - Nav: Settings → Lead Time Defaults. Form: 6 numeric inputs (pick_pack_days, shipping_lead_time_days, sales_buffer_days, manufacturing_buffer_days, quality_inspection_days, internal_transfer_days) with current values. Info note: "These defaults apply to all products that don't have variant-level overrides." Additional note: "Supplier lead time and manufacturing lead time are always set at the product/variant level." [Save] button. Permission gated: lead_time_defaults_manage.

### Phase B Completion

- [ ] **A34-PB-09** — Phase B Unit Tests `4h`
  - Schema: `lookups, inventory, logistics` | FSD: §12 | Depends: A34-PB-01–08 | Tests: T-C3-01–T-C3-06
  - LeadTimeDefaults CRUD (create, read, update per org). One row per org constraint. Non-negative validation on all fields. LeadTimeResolver priority: variant override > org default > 0. Component visibility by route_category. Component visibility by SHIP step presence. Variant lead time column persistence. NULL means org default. Data migration: existing lead_time_days migrated for manufacturing products only. Lead Times tab rendering per route type.

---

## Phase C — Lead Time Calculation Service (C4)

**Priority:** P1 — Parallel Track 1 (after Phase B) | **Estimate:** 38 Hours (5 Days) | **FSD:** §6, §9 M3

> **⚠️ Sequenced:** Phase C depends on Phase B (lead time schema required before calculation service).

### Track C1 — Calculation Backend

- [ ] **A34-PC-01** — Add Lead Time Columns to InquiryLines, QuotationLines, SaleOrderLines (Migration M3) `3h`
  - Schema: `demand` | FSD: §6.5, §9.2 M3 | Depends: — | Tests: —
  - Per table (demand.InquiryLines, demand.QuotationLines, demand.SaleOrderLines): ADD calculated_lead_time_days INT NULL, calculated_delivery_date DATE NULL, manual_delivery_date DATE NULL, lead_time_calculated_at DATETIME2 NULL. ADD computed persisted column: effective_delivery_date AS COALESCE(manual_delivery_date, calculated_delivery_date) PERSISTED. Three identical ALTER TABLE blocks — one per table.

- [ ] **A34-PC-02** — ILeadTimeCalculator Service — Main Calculation Algorithm `5h`
  - Schema: `inventory, logistics, lookups` | FSD: §6.2, §6.3 | Depends: A34-PB-06 | Tests: T-C4-01
  - Interface: ILeadTimeCalculator with CalculateAsync(LeadTimeRequest) → LeadTimeResult. LeadTimeRequest: VariantId, Quantity, RequestedDate (optional), OrgId. LeadTimeResult: TotalLeadTimeDays, EarliestDeliveryDate, List<LeadTimeComponent>. Algorithm: load variant with route → resolve org defaults → sequentially: (1) manufacturing lead time if MANUFACTURE route, (2) supplier lead time if non-MANUFACTURE, (3) QC time, (4) internal transfer, (5) pick & pack, (6) shipping if SHIP step in route, (7) sales buffer. Sum all components. EarliestDeliveryDate = UtcNow + TotalLeadTimeDays.

- [ ] **A34-PC-03** — BOM-Aware Manufacturing Lead Time Calculation (Recursive) `6h`
  - Schema: `inventory, material, logistics` | FSD: §6.4, BR-C4-02–BR-C4-04 | Depends: A34-PC-02 | Tests: T-C4-02–T-C4-05
  - CalculateManufacturingLeadTime(variantId, quantity, visited HashSet): (1) Cycle detection — if variantId already in visited set, log warning, return 0 (BR-C4-02). (2) Load active BOM for variant. (3) This-level production time: if BOM has operations, sum (setup_time + cycle_time × ceil(qty/batch_size)) for all operations, convert minutes to days (480 min/day). If no operations, use variant.ManufacturingLeadTimeDays ?? 1 fallback. (4) Sub-component lead times (parallel): for each BOM line, compute required_qty = quantity_per × quantity. Check available stock via IStockService. If shortfall > 0: if component route_category = 'MANUFACTURE' → recurse; else use supplier lead time. Take MAX across all components (BR-C4-03). (5) Return maxComponentLead + thisLevelDays (BR-C4-03). Stock-aware: if stock covers demand, component lead = 0 (BR-C4-04).

- [ ] **A34-PC-04** — Lead Time Calculation Caching `2h`
  - Schema: — (infrastructure) | FSD: BR-C4-06 | Depends: A34-PC-03 | Tests: T-C4-08
  - IMemoryCache integration: cache key = (variant_id, quantity_bucket). Quantity bucketing: round up to nearest 10% band to prevent cache pollution. TTL = 5 minutes (BR-C4-06). Cache invalidation: on BOM change, on variant lead time field update, on org defaults update. Inject IMemoryCache into LeadTimeCalculator. Wrap CalculateAsync with cache check/store.

- [ ] **A34-PC-05** — Lead Time Calculate API Endpoints `3h`
  - Schema: `demand, inventory` | FSD: §10.1 | Depends: A34-PC-02 | Tests: T-C4-01, T-C4-02
  - POST /api/lead-time/calculate — body: { variant_id, quantity, requested_date? }. Returns: { total_lead_time_days, earliest_delivery_date, components: [{ name, days, source }] }. POST /api/lead-time/calculate-manufacturing — body: { variant_id, quantity }. Returns manufacturing-only lead time with BOM breakdown (for testing/debugging). Both endpoints: authenticated, permission = any authenticated user. Error responses: 404 (variant not found), 400 (invalid quantity ≤ 0).

- [ ] **A34-PC-06** — InquiryLine, QuotationLine, SaleOrderLine Entity & DTO Updates `3h`
  - Schema: `demand` | FSD: §6.5, §10.2, BR-C4-05 | Depends: A34-PC-01 | Tests: T-C4-06, T-C4-07
  - Add 4 properties to each line entity: CalculatedLeadTimeDays (int?), CalculatedDeliveryDate (DateOnly?), ManualDeliveryDate (DateOnly?), LeadTimeCalculatedAt (DateTimeOffset?). Computed property: EffectiveDeliveryDate (read-only, mapped from DB computed column). Update response DTOs: include all 5 fields. Update update DTOs: accept manual_delivery_date for override. When calculate API is called for a line, persist result to calculated_* fields and set lead_time_calculated_at = UtcNow. BR-C4-05: manual_delivery_date takes precedence via COALESCE computed column.

### Track C2 — Calculation UI

- [ ] **A34-PC-07** — Calculate Button on SO / Quotation / Inquiry Line Items `4h`
  - Schema: — (UI) | FSD: §6.6, §11.2, BR-C4-01 | Depends: A34-PC-05 | Tests: —
  - Per line item row: [⏱] button appears next to Delivery date column when variant is selected and quantity > 0. On click: call POST /api/lead-time/calculate with { variant_id, quantity }. On success: populate calculated_delivery_date on the line, show popover (A34-PC-08). On error: show inline error message. Button disabled during calculation (loading spinner). BR-C4-01: calculation is on-demand (button click), NOT automatic on product selection. Same button/behavior on InquiryLines, QuotationLines, SaleOrderLines.

- [ ] **A34-PC-08** — Lead Time Breakdown Popover `4h`
  - Schema: — (UI) | FSD: §6.6, §11.2 | Depends: A34-PC-07 | Tests: —
  - Popover expands below the line item on ⏱ click. Table: Component name | Days | Source. Footer: "Total: N days → Earliest delivery: {date}". Source labels match Lead Times tab: "From BOM calculation", "Manual", "Org Default", "Supplier Record". [Apply] button — sets calculated_delivery_date on the line. [Override Date] button — opens date picker for manual_delivery_date. Popover dismisses on click outside or on Apply/Override. Popover data comes from the calculate API response.

- [ ] **A34-PC-09** — Manual Override Indicator + Date Override Logic `3h`
  - Schema: — (UI) | FSD: §6.7, BR-C4-05 | Depends: A34-PC-06 | Tests: T-C4-06, T-C4-07
  - Delivery date column shows indicator: "⏱ Calculated" (muted text) when calculated and no manual override. "✎ Manual" (amber badge) when manual_delivery_date is set, with tooltip showing original calculated date. "— Not calculated" (grey) when no calculation has been run. Date cell is editable — direct edit sets manual_delivery_date. Clear manual override: click "×" next to manual indicator → sets manual_delivery_date = NULL → reverts to calculated_delivery_date. effective_delivery_date displayed as the active date regardless of source.

### Phase C Completion

- [ ] **A34-PC-10** — Phase C Unit & Integration Tests `5h`
  - Schema: `demand, inventory, material, logistics, lookups` | FSD: §12 | Depends: A34-PC-01–09 | Tests: T-C4-01–T-C4-08
  - Stock variant lead time calculation (supplier + pick/pack + shipping + buffer). Manufacturing variant with BOM (3 components all in stock → component lead = 0). Manufacturing variant with BOM (components out of stock → supplier lead included). Nested BOM (sub-assembly requires production → recursive calculation). Circular BOM detection (returns 0, logs warning). Manual delivery date override (effective = manual). Clear override (effective reverts to calculated). Cache hit within 5 minutes. Cache miss after BOM change. Line-item column persistence across Inquiry/Quotation/SO.

---

## Phase D — SO Confirmation Split (C5)

**Priority:** P1 — Parallel Track 2 | **Estimate:** 40 Hours (5 Days) | **FSD:** §7

> **⚡ Parallelizable:** Phase D runs in parallel with Phase B+C after Phase A completes. Phase D feeds Phase E (SO confirmation split before FGR delivery bridge).

### Track D1 — Confirmation Split Backend

- [ ] **A34-PD-01** — SaleOrderProductionCreator Service `7h`
  - Schema: `demand, material, inventory, logistics` | FSD: §7.3, BR-C5-01–BR-C5-04 | Depends: A34-PA-04 | Tests: T-C5-02, T-C5-04, T-C5-05
  - CreateProductionFromSaleOrder(SaleOrder, IEnumerable<SaleOrderLine> manufacturingLines): per line: resolve effective route via EffectiveRouteResolver (Addendum 33). Create ProductionOrder: OrgId, ProductId, VariantId from line, PlannedQty = line.Quantity, Status = "DRAFT" (BR-C5-03), ProductionWarehouseId from variant.DefaultProductionWarehouseId, SaleOrderId = order.SaleOrderId, SaleOrderLineId = line.SaleOrderLineId, FulfillmentRouteId = effective route id, SourceDemandType = "SALES_ORDER", SourceDemandId = order.SaleOrderId. PlannedStartDate = backward-schedule from effective_delivery_date (subtract post-production lead time). PlannedEndDate = PlannedStartDate + manufacturing_lead_time_days. Batch insert all POs. Trigger BOM explosion per PO (BR-C5-04). Notify production planning.

- [ ] **A34-PD-02** — Modified SaleOrderDeliveryCreator — Exclude Manufacturing Lines `3h`
  - Schema: `demand, logistics` | FSD: §7.4, BR-C5-01, BR-C5-02 | Depends: A34-PA-04, ADD-033 PD-03 | Tests: T-C5-01, T-C5-03
  - Modify existing SaleOrderDeliveryCreator.CreateDeliveriesFromSaleOrder(): filter lines — resolve each line's effective route, load route's route_category. Exclude lines where route_category == 'MANUFACTURE' (BR-C5-02). If no stock lines remain after filtering, return empty list (all lines are manufacturing). Remaining stock lines grouped by route → create Delivery Orders per group (existing Addendum 33 logic unchanged). No change to delivery order creation logic itself — only the input set changes.

- [ ] **A34-PD-03** — Modified SaleOrderService.ConfirmAsync — Orchestration with Category Split `6h`
  - Schema: `demand, logistics, material` | FSD: §7.5, BR-C5-01, BR-C5-05 | Depends: A34-PD-01, A34-PD-02 | Tests: T-C5-01–T-C5-03, T-C5-05
  - ConfirmAsync(saleOrderId): (1) Load order with lines. (2) Validate routes (Addendum 33 C3 validator — unchanged). (3) Split lines by route_category: resolve each line's effective route, group by route.RouteCategory. stockLines = where category != 'MANUFACTURE'. mfgLines = where category == 'MANUFACTURE'. (4) If stockLines.Any() → call SaleOrderDeliveryCreator.CreateDeliveriesFromSaleOrder(order, stockLines). (5) If mfgLines.Any() → call SaleOrderProductionCreator.CreateProductionFromSaleOrder(order, mfgLines). (6) Set order.Status = "CONFIRMED", order.ConfirmedAt = UtcNow. (7) Publish SaleOrderConfirmedEvent. All within single transaction. BR-C5-05: mixed orders process both types at confirmation.

- [ ] **A34-PD-04** — Backward-Scheduling for Production Planned Dates `3h`
  - Schema: `demand, inventory` | FSD: §7.3 (CalculatePlannedStart) | Depends: A34-PD-01 | Tests: T-C5-04
  - CalculatePlannedStart(SaleOrderLine): if line.EffectiveDeliveryDate has value → subtract post-production days (pick_pack + shipping + sales_buffer) and manufacturing days to get planned start. Post-production days resolved via LeadTimeResolver (variant → org default → 0). Manufacturing days from variant.ManufacturingLeadTimeDays ?? 5 (fallback). If no delivery date → default: start tomorrow. CalculatePlannedEnd: PlannedStartDate + manufacturing lead time days. Both dates stored on Production Order.

- [ ] **A34-PD-05** — Production Order Created Notification + BOM Explosion Trigger `3h`
  - Schema: `material, demand` | FSD: §7.3, BR-C5-04 | Depends: A34-PD-01 | Tests: T-C5-05
  - After PO batch insert: iterate each PO → call existing BOMExplosionService.ExplodeAsync(poId) (BR-C5-04). Creates PMR lines from BOM. On successful explosion: trigger existing Supply Requirement Engine (Addendum 31 C3) for material availability. Send notification: NotificationType.ProductionOrderCreatedFromSO with payload { SaleOrderNumber, Count of POs created, list of product names }. Target: production planning role users.

- [ ] **A34-PD-06** — SO Cancellation — Handle Manufacturing Lines `4h`
  - Schema: `demand, material, logistics` | FSD: T-C5-07 | Depends: A34-PD-03 | Tests: T-C5-07
  - On SO cancellation (status → CANCELLED): (1) Cancel linked Delivery Orders pre-GOODS_ISSUED (existing Addendum 33 logic). (2) NEW: cancel linked Production Orders. Query material.ProductionOrders WHERE sale_order_id = @soId. If PO.Status < IN_PROGRESS (DRAFT, PLANNED, MATERIAL_PENDING, READY) → set status = CANCELLED. If PO.Status = IN_PROGRESS → DO NOT auto-cancel. Flag warning: "Production Order {number} is already in progress and cannot be auto-cancelled." Return list of cancelled POs and skipped POs (in-progress) for user feedback.

### Track D2 — Confirmation Split UI

- [ ] **A34-PD-07** — SO Fulfillment Tab — Production Orders Section `5h`
  - Schema: — (UI) | FSD: §7.7, §11.3 | Depends: A34-PD-03 | Tests: T-C5-06
  - Fulfillment tab (Addendum 33 §10.4): add "Production Orders" section below existing "Delivery Orders" section. Table: Production Order # | Product name | Status badge | Delivery column. Status badges: Draft (grey), Planned (blue), MaterialPending (amber), Ready (green), InProgress (indigo), Completed (green checkmark). Delivery column: "Pending" (grey, when PO not yet complete), DO number + link (when delivery auto-created after FGR). Sub-row: "Route after production: {route_name}" and "Delivery will be created when production completes." Overall fulfillment progress: "N of M lines delivered" — manufacturing lines count as "delivered" only after their delivery reaches COMPLETED. Timeline section: chronological events (SO confirmed, DOs created, POs created, expected completion dates).

- [ ] **A34-PD-08** — Confirmation Preview — Stock vs Manufacturing Split Display `4h`
  - Schema: — (UI) | FSD: §7.6, §11.3 | Depends: A34-PD-03 | Tests: —
  - Delivery Preview panel (Addendum 33 §10.3): extend to show manufacturing line handling. Before confirmation (DRAFT): "On confirmation:" section split into two groups: "Delivery Orders (N lines):" with route grouping for stock lines. "Production Orders (M lines):" with product names and expected production time for manufacturing lines. Color coding: stock lines = green group, manufacturing lines = orange group. Info note for manufacturing lines: "Production will be triggered. Delivery is created after production completes." [Confirm Order] button tooltip updated when mixed: "Will create N delivery orders and M production orders."

### Phase D Completion

- [ ] **A34-PD-09** — Phase D Unit & Integration Tests `5h`
  - Schema: `demand, material, logistics, inventory` | FSD: §12 | Depends: A34-PD-01–08 | Tests: T-C5-01–T-C5-07
  - SO with all stock lines → only deliveries created (existing behavior). SO with all manufacturing lines → only production orders created, no deliveries. Mixed SO (2 stock + 1 mfg) → 2 delivery groups + 1 production order. PO fields: sale_order_id, sale_order_line_id, fulfillment_route_id correctly set. BOM explosion triggered per PO. Backward-scheduling: planned_start_date computed from delivery date minus lead times. SO cancellation: pre-InProgress POs cancelled, InProgress POs skipped with warning. Notification sent on PO creation. Fulfillment tab renders both sections.

---

## Phase E — Production → Delivery Bridge (C6)

**Priority:** P1 — Parallel Track 2 (after Phase D) | **Estimate:** 31 Hours (4 Days) | **FSD:** §8, §9 M4

> **⚠️ Sequenced:** Phase E depends on Phase D (SO-linked Production Orders must exist before FGR delivery bridge).

### Track E1 — Bridge Backend

- [ ] **A34-PE-01** — Add fulfillment_route_id and delivery_order_id to ProductionOrders (Migration M4) `3h`
  - Schema: `material` | FSD: §8.7, §9.2 M4 | Depends: — | Tests: —
  - ALTER TABLE material.ProductionOrders ADD fulfillment_route_id INT NULL FK → logistics.FulfillmentRoutes. ALTER TABLE material.ProductionOrders ADD delivery_order_id BIGINT NULL FK → logistics.delivery_orders. CREATE INDEX IX_ProductionOrders_FulfillmentRoute filtered WHERE NOT NULL. CREATE INDEX IX_ProductionOrders_DeliveryOrder filtered WHERE NOT NULL. Backfill: UPDATE existing SO-linked POs: SET fulfillment_route_id = COALESCE(sol.fulfillment_route_id, pv.fulfillment_route_id) FROM ProductionOrders po JOIN SaleOrderLines sol JOIN ProductVariants pv WHERE po.sale_order_id IS NOT NULL AND po.fulfillment_route_id IS NULL.

- [ ] **A34-PE-02** — ProductionOrder Entity & DTO Updates `2h`
  - Schema: `material` | FSD: §8.7, §10.2 | Depends: A34-PE-01 | Tests: —
  - Add FulfillmentRouteId (int?) and DeliveryOrderId (long?) to ProductionOrder entity. Navigation properties: FulfillmentRoute, DeliveryOrder. Update ProductionOrderResponseDto: include fulfillment_route_id, fulfillment_route_code, fulfillment_route_name, delivery_order_id, delivery_order_number. Update GET /api/production-orders/{id} to return new fields. AutoMapper profile updates.

- [ ] **A34-PE-03** — Extended FGRConfirmedEventHandler — Delivery Trigger Condition `4h`
  - Schema: `material, logistics, demand` | FSD: §8.2, BR-C6-01, BR-C6-02 | Depends: A34-PE-02 | Tests: T-C6-01, T-C6-04
  - Modify existing FGRConfirmedEventHandler.Handle(FGRConfirmedEvent): (1) Existing: await _allocationEngine.AllocateForFGR(po) — unchanged. (2) NEW: if (po.SaleOrderId.HasValue && po.FulfillmentRouteId.HasValue) → await _productionDeliveryCreator.CreateDeliveryFromProduction(po) (BR-C6-01). (3) ELSE: standalone production — FG goes to inventory, no delivery (BR-C6-02). Wrap delivery creation in try/catch: on failure, log error, send NotificationType.DeliveryCreationFailed notification — do not fail the FGR confirmation itself. Idempotency: check if po.DeliveryOrderId is already set → skip (prevents duplicate on event replay).

- [ ] **A34-PE-04** — ProductionDeliveryCreator Service `6h`
  - Schema: `material, logistics, demand` | FSD: §8.3, BR-C6-03–BR-C6-08 | Depends: A34-PE-03 | Tests: T-C6-01, T-C6-02, T-C6-05, T-C6-06
  - CreateDeliveryFromProduction(ProductionOrder po): load SaleOrder, SaleOrderLine, FulfillmentRoute with steps. deliveryQty = po.ActualQty (BR-C6-04 — reflects actual yield, not planned). Create DeliveryOrder: OrgId, FromSourceType = "SALE_ORDER", SaleOrderId, FulfillmentRouteId from PO (BR-C6-03), WarehouseId = po.ProductionWarehouseId, Status = "DRAFT" (BR-C6-07), ExpectedDate from SO line effective_delivery_date, PartnerId and DeliveryAddressId from SaleOrder. Create DeliveryOrderLine: VariantId, Quantity = deliveryQty, SaleOrderLineId. Insert delivery. Update po.DeliveryOrderId = delivery.DeliveryOrderId (BR-C6-08). Call SaleOrderService.RecalculateFulfillmentStatusAsync(po.SaleOrderId). Send notification: DeliveryCreatedFromProduction with { DeliveryNumber, PONumber, SONumber, RouteName }.

- [ ] **A34-PE-05** — Partial Production & Shortfall Handling `4h`
  - Schema: `material, demand, logistics` | FSD: §8.6, BR-C6-04–BR-C6-06 | Depends: A34-PE-04 | Tests: T-C6-02, T-C6-03
  - If po.ActualQty < soLine.Quantity (BR-C6-05): create delivery for actual_qty. Set soLine.PartialFulfillmentNotes = "Production Order {number} produced {actual} of {planned}. Shortfall: {diff}". Send NotificationType.ProductionShortfall with { PONumber, Planned, Actual, SONumber }. If po.ActualQty == 0 (BR-C6-06): do NOT create delivery. Send NotificationType.ProductionZeroYield as error-level notification to production manager. Log error: "Production Order {id} completed with zero yield — no delivery created." Handle in ProductionDeliveryCreator: guard clause at top — if ActualQty <= 0, skip delivery creation, send notification, return null.

### Track E2 — Bridge UI

- [ ] **A34-PE-06** — Production Order Detail — Auto-Created Delivery Section `5h`
  - Schema: — (UI) | FSD: §11.4 | Depends: A34-PE-02 | Tests: T-C6-07
  - Production Order detail page: add "Auto-Created Delivery" section below production summary. Shows: delivery order number (clickable link to DO detail), route name, delivery qty, delivery status badge, creation timestamp. If PO completed but no delivery (standalone): section hidden. If PO completed with shortfall: show note "Shortfall of N units (planned X, produced Y)". Header area: "Sale Order: {SO number}" clickable link (if SO-linked). "Fulfillment Route: {route name}" label. Production summary row: Planned qty | Actual qty | Yield %.

- [ ] **A34-PE-07** — SO Fulfillment Tab — Production Completion → Delivery Update `3h`
  - Schema: — (UI) | FSD: §11.3 | Depends: A34-PD-07 | Tests: T-C6-08
  - SO Fulfillment tab: when production order status changes to Completed, the Production Orders section auto-updates. Delivery column changes from "Pending" to delivery order number + status badge. Auto-refresh: poll or SignalR push when PO status updates to Completed. Timeline section: add events for "PROD-{N} completed — delivery DO-{M} created". Fulfillment progress bar: recalculate — manufacturing lines move from "in production" to "delivery in progress" upon delivery creation.

### Phase E Completion

- [ ] **A34-PE-08** — Phase E Unit Tests `4h`
  - Schema: `material, logistics, demand` | FSD: §12 | Depends: A34-PE-01–07 | Tests: T-C6-01–T-C6-10
  - FGR on SO-linked PO (100 planned, 100 actual) → delivery created for 100 with correct route. FGR on SO-linked PO (100 planned, 95 actual) → delivery for 95, shortfall notification, SO line annotated. FGR on SO-linked PO (100 planned, 0 actual) → no delivery, error notification. FGR on standalone PO → no delivery, FG to inventory. Auto-created delivery has correct route steps (PICK→PACK→GOODS_ISSUE→SHIP for MFG_PICK_PACK_SHIP). Delivery from_source_type = 'SALE_ORDER'. PO.delivery_order_id populated after creation. SO fulfillment tab shows completed PO with delivery link. MTS escape hatch: route override to STOCK → delivery at SO confirmation. Idempotency: duplicate FGR event does not create duplicate delivery.

---

## Phase F — Testing & Integration (All Changes)

**Priority:** P0 — Final Gate | **Estimate:** 24 Hours (3 Days) | **FSD:** §13

> **⚠️ Sequenced:** Phase F depends on all previous phases (A–E).

### Track F1 — End-to-End Integration Tests

- [ ] **A34-PF-01** — E2E: Mixed SO — Stock + Manufacturing Lines Through Full Lifecycle `5h`
  - Schema: `demand, logistics, material, inventory` | FSD: §13 | Depends: All | Tests: T-C5-03, T-C6-10
  - Create SO with 3 lines: Line 1 — Steel Pipes (PICK_AND_SHIP, STOCK), Line 2 — Custom Gear Assembly (MFG_PICK_PACK_SHIP, MANUFACTURE), Line 3 — Copper Fittings (PICK_PACK_SHIP, STOCK). Confirm SO → verify: DO-001 for Steel Pipes (PICK_AND_SHIP), DO-002 for Copper Fittings (PICK_PACK_SHIP), PROD-085 for Custom Gear Assembly. Advance DO-001: DRAFT→RELEASED→PICKING→PICKED→GOODS_ISSUED→IN_TRANSIT→DELIVERED→COMPLETED. Advance DO-002: DRAFT→RELEASED→PICKING→PICKED→PACKED→GOODS_ISSUED→IN_TRANSIT→DELIVERED→COMPLETED. Complete PROD-085 → FGR → verify DO-003 auto-created with MFG_PICK_PACK_SHIP steps. Advance DO-003 through all steps. Verify SO fulfillment = 100%.

- [ ] **A34-PF-02** — E2E: Full Manufacturing Chain — SO → PO → Materials → FGR → Delivery → Ship `5h`
  - Schema: all | FSD: §8.4 | Depends: All | Tests: T-C6-10
  - Create product with MFG_PICK_PACK_SHIP route and 3-component BOM. Create SO with 1 manufacturing line for qty 100. Confirm → PROD-001 created with correct fields. BOM explosion → 3 PMR lines. GRNs for raw materials → allocation to PROD-001. Production: DRAFT→PLANNED→MATERIAL_PENDING→READY→IN_PROGRESS→COMPLETED (FGR with actual_qty=95). FGRConfirmedEvent → delivery DO auto-created for 95 units. Shortfall = 5 → notification sent, SO line annotated. DO advanced: PICK→PACK→GOODS_ISSUE→SHIP→DELIVER→COMPLETE. Full traceability: SO ↔ PO ↔ DO chain verified at every step.

- [ ] **A34-PF-03** — E2E: MTS Escape Hatch — Route Override from Manufacturing to Stock `3h`
  - Schema: `demand, logistics, inventory` | FSD: §8.5, BR-C6-09 | Depends: All | Tests: T-C6-09
  - Product has MFG_PICK_PACK_SHIP route (MANUFACTURE). Prior production run deposited 200 units to inventory. New SO: salesperson overrides route on SO line to PICK_PACK_SHIP (STOCK). Confirm SO → delivery created immediately from existing stock (no production order). Verify: no Production Order created. Delivery qty from stock reservation. Alternative test: salesperson keeps manufacturing route → production order created (default behavior).

- [ ] **A34-PF-04** — E2E: Lead Time Calculation Across Pipeline Stages `3h`
  - Schema: `demand, inventory, material` | FSD: §6 | Depends: Phase C | Tests: T-C4-01–T-C4-04
  - Create manufacturing product with BOM (2 components — 1 in stock, 1 out of stock). Calculate lead time on Inquiry line → verify: mfg time + 0 (in-stock component) + supplier lead (out-of-stock component) + QC + pick/pack + shipping + buffer. Copy to Quotation → verify same calculation available. Convert to SO → verify same calculation. Override delivery date manually → verify effective_delivery_date = manual date. Clear override → verify revert to calculated date.

- [ ] **A34-PF-05** — E2E: Multi-Tenant Isolation `3h`
  - Schema: all | FSD: — | Depends: All | Tests: —
  - Create routes in Tenant A and Tenant B — each gets 5 seed routes independently. Verify HasQueryFilter: Tenant A cannot see/use Tenant B's manufacturing routes. Tenant A's LeadTimeDefaults isolated from Tenant B. SO in Tenant A cannot reference Tenant B route. Production Order in Tenant A cannot use Tenant B route. Lead time calculation uses correct org's defaults.

- [ ] **A34-PF-06** — Regression: Existing Delivery, Production & BOM Flows Unaffected `3h`
  - Schema: `logistics, material` | FSD: §2 | Depends: All | Tests: —
  - Existing delivery sources (PO, SRO, MIV, Transfer) — fulfillment_route_id = NULL → full state machine chain (backward compatible). Existing standalone Production Orders (no SO link) → no delivery auto-creation on FGR. Existing BOM explosion, PMR, material issue, QC, FGR flows unchanged. Existing 1531 logistics tests pass. Existing manufacturing tests pass. No regression in Supply Requirement Engine.

- [ ] **A34-PF-07** — Final Code Review & Documentation `2h`
  - Schema: all | FSD: all | Depends: A34-PF-01–06 | Tests: —
  - Code review: all new services (LeadTimeCalculator, SaleOrderProductionCreator, ProductionDeliveryCreator, LeadTimeResolver). Code review: all modified services (FGRConfirmedEventHandler, SaleOrderDeliveryCreator, SaleOrderService.ConfirmAsync). Swagger/OpenAPI annotations on all new/modified endpoints. Verify all 30 business rules have test coverage. Verify entity relationship diagram accuracy with new FKs.

---

## Parallelization Map

```
Phase A (4 days) ──────────────────┐
                                   ├──→ Phase B (4 days) ──→ Phase C (5 days) ──→ ┐
                                   │                                               ├──→ Phase F (3 days)
                                   └──→ Phase D (5 days) ──→ Phase E (4 days) ──→ ┘

Parallel agents:
  Agent 1 (Track 1): Phase A → Phase B → Phase C
  Agent 2 (Track 2): Phase A → Phase D → Phase E
  Agent 3: Phase F (after Tracks 1 & 2 complete)

Critical path: A(4) + max(B+C=9, D+E=9) + F(3) = 16 dev days
Calendar time with 2 parallel agents: ~16 working days
```

---

## Dependency Chain

```
M1 (route_category + mfg seed routes)
├── M2 (LeadTimeDefaults + variant columns) ──→ LeadTimeResolver ──→ LeadTimeCalculator
│                                                                        ↓
│                                              InquiryLines/QuotationLines/SaleOrderLines (M3)
│
├── SaleOrderProductionCreator ──→ Modified ConfirmAsync ──→ FGRConfirmedEventHandler (M4)
│                                                              ↓
│                                                      ProductionDeliveryCreator
│
└── BOM visibility rule ──→ BOM mandatory validation
```

---

## Task Summary

| Phase | Changes | Tasks | Hours | Days | Parallel Group |
|---|---|---|---|---|---|
| A — Route Category & Classification (C1, C2) | C1, C2 | 11 | 30 | 4 | Foundation |
| B — Lead Time Management (C3) | C3 | 9 | 31 | 4 | Track 1 |
| C — Lead Time Calculation Service (C4) | C4 | 10 | 38 | 5 | Track 1 |
| D — SO Confirmation Split (C5) | C5 | 9 | 40 | 5 | Track 2 |
| E — Production → Delivery Bridge (C6) | C6 | 8 | 31 | 4 | Track 2 |
| F — Testing & Integration | All | 7 | 24 | 3 | Final |
| **TOTAL** | **C1–C6** | **54** | **194** | **25** | |

---

*End of Task Register — Addendum 34*
