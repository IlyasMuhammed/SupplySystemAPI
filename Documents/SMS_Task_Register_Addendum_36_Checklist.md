# SMS Task Register — Addendum 36: Service Orders & Service Fulfillment

**Document:** SMS-TR-ADD-036 v1.0 | **FSD:** SMS-FSD-ADD-036 v1.0
**Total:** 58 Tasks | 256 Hours | 32 Dev Days | 10 Migrations | 12 Endpoints | 6 UI Panels
**Depends On:** SMS-FSD-ADD-030 (Manufacturing, Production & Allocation Engine), SMS-FSD-ADD-031 (BOM Management, Material Availability Tab), SMS-FSD-ADD-032 (Sale Inquiry & Quotation — SO line indicators), SMS-FSD-ADD-035 (Multi-Currency)

---

## Phase 1 — Product Catalog & Service BOM (C1, C2)

**Priority:** P0 — Critical Path | **Estimate:** 48 Hours (6 Days) | **FSD:** §2, §3, §12 M1–M2

> **⚠️ Everything downstream depends on this phase.** C1 defines the service product flags that drive all downstream behavior (BOM eligibility, invoicing policy, billing model). C2 extends BOM lines with source_type, which the Service Order BOM explosion reads.

### Track 1A — Service Product Enhancements (C1)

- [ ] **A36-P1-01** — Add Service Fields to Products (Migration M1 — DDL) `2h`
  - Schema: `lookups` | FSD: §2.2, §12.2 M1 | Depends: — | Tests: TS-01, TS-02
  - ALTER TABLE lookups.Products ADD: service_invoicing_policy TINYINT NULL, service_billing_model TINYINT NULL, estimated_duration_hours DECIMAL(8,2) NULL, has_service_bom BIT NOT NULL DEFAULT 0 (DF_Products_HasServiceBom), is_subcontractable BIT NOT NULL DEFAULT 0 (DF_Products_IsSubcontractable). All existing products unaffected — NULL for non-service, DEFAULT 0 for BIT columns.

- [ ] **A36-P1-02** — Product Entity & DTO Updates for Service Fields `2h`
  - Schema: `lookups` | FSD: §2.2 | Depends: A36-P1-01 | Tests: —
  - Add to Product entity: ServiceInvoicingPolicy (byte?), ServiceBillingModel (byte?), EstimatedDurationHours (decimal?), HasServiceBom (bool), IsSubcontractable (bool). IEntityTypeConfiguration: HasDefaultValue for BIT columns, CheckConstraints for estimated_duration_hours > 0. Add ServiceInvoicingPolicy enum: FixedPrice=0, CostPlus=1, TimeAndMaterial=2. Add ServiceBillingModel enum: Inclusive=0, PassThrough=1. Update ProductResponseDto, ProductCreateDto, ProductUpdateDto to include new fields. Map product_type classification update (§2.6): Service type now supports "Can Have Service BOM = Yes".

- [ ] **A36-P1-03** — Product Service — Service Field Validation Rules `3h`
  - Schema: `lookups` | FSD: §2.5, SVC-P-01–SVC-P-07 | Depends: A36-P1-02 | Tests: TS-01–TS-03
  - FluentValidation rules on ProductCreateDto/UpdateDto: SVC-P-01: service_invoicing_policy must be NULL when product_type ≠ Service. SVC-P-02: service_billing_model must be NULL when product_type ≠ Service. SVC-P-03: estimated_duration_hours must be NULL or > 0. SVC-P-04: has_service_bom can only be true when product_type = Service. SVC-P-05: when has_service_bom = true, at least one BOM must exist in Draft or higher status (cross-validation against material.BillOfMaterials). SVC-P-06: when service_invoicing_policy = TimeAndMaterial, product must have a unit_price (hourly rate). SVC-P-07: is_subcontractable can only be true when product_type = Service.

- [ ] **A36-P1-04** — Product API Endpoint Updates — Service Fields `1.5h`
  - Schema: `lookups` | FSD: §2.2 | Depends: A36-P1-03 | Tests: TS-01
  - Update existing POST /api/products and PUT /api/products/{id}: accept service_invoicing_policy, service_billing_model, estimated_duration_hours, has_service_bom, is_subcontractable in body. Update GET /api/products/{id}: return new fields in response DTO. Backward compatible — non-service products ignore service fields (NULL/0 defaults).

- [ ] **A36-P1-05** — UI — Product Form: Service Configuration Section `3h`
  - Schema: — (UI) | FSD: §15.4 | Depends: A36-P1-04 | Tests: —
  - Conditionally visible section when product_type = Service: Invoicing Policy dropdown (FixedPrice, CostPlus, TimeAndMaterial). Billing Model dropdown (Inclusive, PassThrough). Estimated Duration (hours) number input. Has Service BOM toggle. Is Subcontractable toggle. Validation toasts on save error. Section hidden when product_type ≠ Service. Info tooltip per field explaining behavior (e.g., "FixedPrice: Customer pays fixed price regardless of actual materials consumed").

### Track 1B — Service BOM Extension (C2)

- [ ] **A36-P1-06** — Add source_type to BOM Lines (Migration M2 — DDL) `1.5h`
  - Schema: `material` | FSD: §3.3, §12.2 M2 | Depends: — | Tests: TS-04, TS-05
  - ALTER TABLE material.BillOfMaterialLines ADD source_type TINYINT NOT NULL DEFAULT 0 (DF_BOMLines_SourceType). ALTER TABLE material.BillOfMaterialLines ADD subcontract_supplier_id BIGINT NULL FK → suppliers.Suppliers(id) (FK_BOMLines_SubcontractSupplier). All existing BOM lines default to source_type = 0 (Stock) — fully backward compatible.

- [ ] **A36-P1-07** — BOM Line Entity & DTO Updates for source_type `1.5h`
  - Schema: `material` | FSD: §3.3, §3.4 | Depends: A36-P1-06 | Tests: —
  - Add to BillOfMaterialLine entity: SourceType (BOMLineSourceType enum: Stock=0, Subcontract=1, InternalLabor=2), SubcontractSupplierId (long?). Navigation: SubcontractSupplier → suppliers.Suppliers. IEntityTypeConfiguration: HasOne for FK, HasDefaultValue for SourceType. Update BomLineResponseDto, BomLineCreateDto, BomLineUpdateDto.

- [ ] **A36-P1-08** — BOM Service — Eligibility Extension & Source Type Validation `3h`
  - Schema: `material` | FSD: §3.2, §3.6, SVC-BOM-01–SVC-BOM-06 | Depends: A36-P1-07, A36-P1-02 | Tests: TS-03–TS-05
  - Modify IBomService.Create() validation: OLD rule: BOM product_id must have supply_method = Manufacture. NEW rule: supply_method = Manufacture OR (product_type = Service AND has_service_bom = true). SVC-BOM-01: validate BOM output product eligibility. SVC-BOM-02: subcontract_supplier_id required when source_type = Subcontract (FluentValidation). SVC-BOM-03: subcontract_supplier_id must be NULL when source_type ≠ Subcontract. SVC-BOM-04: subcontracted BOM line material_product_id must reference product_type = Service. SVC-BOM-05: InternalLabor line UOM must be 'HR'. SVC-BOM-06: existing manufacturing BOM rules unchanged — guard clause ensures no relaxation.

- [ ] **A36-P1-09** — UI — Product Form: Service BOM Section `3h`
  - Schema: — (UI) | FSD: §15.3 | Depends: A36-P1-08, A36-P1-05 | Tests: —
  - Conditionally visible when product_type = Service AND has_service_bom = true. Reuses Addendum 31 §6.3 two-panel BOM layout: Panel 1 (BOM List, 35%), Panel 2 (BOM Editor, 65%). BOM Lines grid extended with 'Source Type' column: dropdown (Stock, Subcontract, Internal Labor). When Source Type = Subcontract → 'Supplier' searchable dropdown appears (suppliers.Suppliers). When Source Type = InternalLabor → UOM auto-set to 'HR', quantity label changes to 'Hours'. Validation toasts per SVC-BOM-02/03/04/05.

### Track 1C — Phase 1 Tests

- [ ] **A36-P1-10** — Phase 1 Unit Tests `4h`
  - Schema: `lookups, material` | FSD: §17.1 | Depends: A36-P1-01–09 | Tests: TS-01–TS-05
  - TS-01: Create product type=Service, set invoicing_policy=FixedPrice → saved. TS-02: Set has_service_bom=true on non-Service product → validation error SVC-P-04. TS-03: Set has_service_bom=true without any BOM → validation error SVC-P-05. TS-04: Create BOM line source_type=Subcontract, no supplier → validation error SVC-BOM-02. TS-05: Create BOM line source_type=Subcontract, valid supplier → saved with subcontract_supplier_id. Additional: estimated_duration_hours < 0 rejected. TimeAndMaterial without unit_price rejected. BOM eligibility: manufacturing product still requires supply_method=Manufacture.

---

## Phase 2 — Service Order Core (C3, C4, C5)

**Priority:** P1 — Sequenced after Phase 1 | **Estimate:** 80 Hours (10 Days) | **FSD:** §4, §5, §6, §12 M3–M4

> **⚡ Sequenced:** Depends on Phase 1 (service product flags and BOM source_type must exist for Service Order creation and BOM explosion).

### Track 2A — Service Orders Entity (C3)

- [ ] **A36-P2-01** — Create material.ServiceOrders Table (Migration M3 — DDL) `3h`
  - Schema: `material` | FSD: §4.2, §4.3, §4.4, §12.2 M3 | Depends: A36-P1-01 | Tests: TS-06–TS-09
  - CREATE TABLE material.ServiceOrders: 30+ columns as per §4.2 — id BIGINT IDENTITY PK, org_id, service_number NVARCHAR(50), service_product_id FK → lookups.Products, service_product_variant_id FK → lookups.ProductVariants, customer_id FK → suppliers.BusinessPartners, bom_id FK → material.BillOfMaterials (NULL), bom_version INT (NULL), quantity DECIMAL(18,4), warehouse_id FK → warehouse.Warehouses, assigned_user_id FK → auth.Users (NULL), assigned_team_id (NULL), estimated_hours, actual_hours, scheduled_date, scheduled_time, actual_start_date, actual_end_date, source_type TINYINT DEFAULT 0, source_id, source_line_id, invoicing_policy TINYINT DEFAULT 0, billing_model TINYINT DEFAULT 0, priority TINYINT DEFAULT 1, status TINYINT DEFAULT 0, material_readiness TINYINT DEFAULT 0, completion_notes, customer_signature BIT DEFAULT 0, notes, trace_id, is_deleted BIT DEFAULT 0, created_by, created_at, updated_at, row_version ROWVERSION. 6 indexes: UQ_ServiceOrders_OrgNumber, IX_ServiceOrders_Customer, IX_ServiceOrders_Status, IX_ServiceOrders_AssignedUser (filtered), IX_ServiceOrders_SourceSO (filtered), IX_ServiceOrders_Product. 3 CHECK constraints: CK_ServiceOrders_Quantity (>0), CK_ServiceOrders_EstHours, CK_ServiceOrders_ActHours.

- [ ] **A36-P2-02** — ServiceOrder EF Core Entity & Configuration `2h`
  - Schema: `material` | FSD: §4.2 | Depends: A36-P2-01 | Tests: —
  - Add ServiceOrder entity class with all 30+ properties. Add enums: ServiceOrderStatus (Draft=0..Cancelled=8), ServiceSourceType (Manual=0, SalesOrder=1, FulfillmentReq=2), ServicePriority (Low=0..Urgent=3), MaterialReadiness (NotChecked=0..NotApplicable=4). IEntityTypeConfiguration<ServiceOrder>: ToTable("ServiceOrders", "material"), HasQueryFilter(o => o.OrgId == _tenantId && !o.IsDeleted), HasIndex for all 6 indexes, HasCheckConstraint for 3 checks. Navigations: ServiceProduct, Customer, Bom, Warehouse, AssignedUser, CreatedByUser, ICollection<ServiceMaterialRequirement> Materials. DbContext: DbSet<ServiceOrder> ServiceOrders. Configure ROWVERSION as concurrency token.

- [ ] **A36-P2-03** — ServiceOrderService — CRUD Operations `4h`
  - Schema: `material` | FSD: §4.2, §16.1 SVC-01–SVC-03, SVC-12, SVC-13 | Depends: A36-P2-02 | Tests: TS-06, TS-09
  - IServiceOrderService methods: CreateAsync(orgId, dto), GetByIdAsync(orgId, id), GetListAsync(orgId, filter), UpdateAsync(orgId, id, dto), DeleteAsync(orgId, id). SVC-01: validate service_product_id references product_type = Service. SVC-12: only notes editable after Completed — guard on status. SVC-13: optimistic concurrency via row_version (EF Core concurrency token). Service number generation via document_number_sequences (SVC-YYYYMMDD-SEQ). Auto-populate invoicing_policy, billing_model from product defaults. Auto-populate estimated_hours from product.estimated_duration_hours × quantity. If product.has_service_bom → lookup active BOM, set bom_id + bom_version (snapshot).

### Track 2B — Service Material Requirements Entity (C5)

- [ ] **A36-P2-04** — Create material.ServiceMaterialRequirements Table (Migration M4 — DDL) `2h`
  - Schema: `material` | FSD: §6.2, §6.3, §12.2 M4 | Depends: A36-P2-01 | Tests: TS-07, TS-08
  - CREATE TABLE material.ServiceMaterialRequirements: id BIGINT IDENTITY PK, org_id, service_order_id FK → material.ServiceOrders, bom_line_id FK → material.BillOfMaterialLines (NULL), product_id FK → lookups.Products, product_variant_id FK (NULL), source_type TINYINT DEFAULT 0, required_quantity, net_quantity, scrap_allowance DEFAULT 0, reserved_quantity DEFAULT 0, issued_quantity DEFAULT 0, consumed_quantity DEFAULT 0, returned_quantity DEFAULT 0, shortage_quantity, uom NVARCHAR(20), warehouse_id FK → warehouse.Warehouses, is_critical BIT DEFAULT 1, is_adhoc BIT DEFAULT 0, status TINYINT DEFAULT 0, required_date DATE, added_by FK → auth.Users (NULL), notes, is_deleted BIT DEFAULT 0, created_at, updated_at. 3 indexes: IX_SMR_ServiceOrder, IX_SMR_Shortage (filtered WHERE shortage_quantity > 0), IX_SMR_Product.

- [ ] **A36-P2-05** — ServiceMaterialRequirement EF Core Entity & Configuration `1.5h`
  - Schema: `material` | FSD: §6.2 | Depends: A36-P2-04 | Tests: —
  - Entity class with all properties. Add SMRStatus enum: Pending=0, PartiallyReserved=1, FullyReserved=2, Issued=3, Consumed=4, Returned=5, Cancelled=6. IEntityTypeConfiguration: ToTable("ServiceMaterialRequirements", "material"), HasQueryFilter, all FKs and indexes. Navigations: ServiceOrder, BomLine, Product, ProductVariant, Warehouse, AddedByUser. DbContext: DbSet<ServiceMaterialRequirement>.

- [ ] **A36-P2-06** — BOM Explosion Service for Service Orders `4h`
  - Schema: `material` | FSD: §6.4, §6.5 | Depends: A36-P2-05, A36-P1-08 | Tests: TS-07, TS-08
  - IServiceBomExplosionService.ExplodeAsync(serviceOrderId): triggered on Draft → Planned when bom_id IS NOT NULL. For each BOM line, create SMR per source_type: **Stock (0):** netQty = SO.Quantity × bomLine.Quantity / bom.BaseQuantity; scrapAllowance = netQty × scrapPercentage/100; grossQty = net + scrap. SMR with shortage_quantity = grossQty. Trigger Allocation Engine AllocateForDemandAsync(ServiceOrder, smr.Id). **Subcontract (1):** Create SupplyRequirement with DemandSourceType.ServiceOrder=3, SupplyMethod.Purchase. Also create SMR with source_type=Subcontract for tracking. **InternalLabor (2):** Create SMR for cost tracking only. shortage_quantity=0, is_critical=false. No stock reservation, no PO. Register ServiceOrderDemandHandler in DI for AllocationDemandType.ServiceOrder.

### Track 2C — State Machine (C4)

- [ ] **A36-P2-07** — Service Order State Machine Implementation `4h`
  - Schema: `material` | FSD: §5.1, §5.4, ST-01–ST-10 | Depends: A36-P2-03, A36-P2-06 | Tests: TS-07–TS-13
  - IServiceOrderStateMachine with TransitionAsync(serviceOrderId, targetStatus). 10 transition rules as per §5.4: ST-01 Draft→Planned: requires assigned_user_id OR assigned_team_id, warehouse_id set, valid service product. Triggers BOM explosion if bom_id IS NOT NULL. ST-02 Planned→MaterialPending: auto — at least one critical SMR has shortage > 0. ST-03 Planned→Ready: auto — all critical SMRs shortage=0 OR no SMRs exist. ST-04 MaterialPending→Ready: auto — all critical SMR shortages resolved. ST-05 Ready→InProgress: set actual_start_date. ST-06 InProgress→Waiting: auto — ad-hoc SMR added with shortage. ST-07 Waiting→InProgress: auto — ad-hoc shortage resolved. ST-08 InProgress→Completed: requires completion form submitted. ST-09 Completed→Closed: invoice generated or manual close. ST-10 Any non-terminal→Cancelled: releases reservations, cancels SRs. Guard clauses per transition. MediatR events: ServiceOrderPlannedEvent, ServiceOrderStartedEvent, ServiceCompletedEvent, ServiceOrderCancelledEvent.

- [ ] **A36-P2-08** — Material Readiness Calculation `2h`
  - Schema: `material` | FSD: §5.5 | Depends: A36-P2-07 | Tests: TS-07–TS-09
  - RecalculateMaterialReadiness(serviceOrderId): triggered by Allocation Engine allocation, GRN creating inventory, manual reservation, SR fulfillment. Logic (parallel to Production Order §11.3): No SMRs → MaterialReadiness.NotApplicable, status → Ready (if Planned). Any critical SMR shortage > 0 → MaterialReadiness.Shortage (stays MaterialPending or Waiting). All critical shortage = 0 → MaterialReadiness.Ready (MaterialPending→Ready or Waiting→InProgress). Subscribe to AllocationCompletedEvent, GoodsReceivedEvent for automatic recalculation.

### Track 2D — Service Order API

- [ ] **A36-P2-09** — Service Order CRUD API Endpoints (Endpoints #1–#4) `3h`
  - Schema: `material` | FSD: §13.1 endpoints 1–4, §13.4 | Depends: A36-P2-03 | Tests: TS-06
  - POST /api/service-orders — Create Service Order. Auth: service_order_create. Body: { serviceProductId, serviceProductVariantId, customerId, quantity, warehouseId, assignedUserId, scheduledDate, scheduledTime, priority, notes }. Response: ServiceOrderResponseDto with all fields. GET /api/service-orders — List (paginated). Auth: service_order_read. Query: ?status, ?customerId, ?assignedUserId, ?fromDate, ?toDate, ?priority. Default sort: scheduled_date ASC, priority DESC. GET /api/service-orders/{id} — Detail with SMRs and ledger entries eager-loaded. Auth: service_order_read. PUT /api/service-orders/{id} — Update (Draft/Planned only for most fields). Auth: service_order_write. Swagger annotations for all 4.

- [ ] **A36-P2-10** — Service Order State Transition Endpoints (Endpoints #5–#8) `3h`
  - Schema: `material` | FSD: §13.1 endpoints 5–8 | Depends: A36-P2-07 | Tests: TS-07–TS-16
  - POST /api/service-orders/{id}/plan — Draft→Planned. Auth: service_order_write. Triggers BOM explosion + material readiness check. Returns updated ServiceOrderResponseDto with SMR list. POST /api/service-orders/{id}/start — Ready→InProgress. Auth: service_order_write. Sets actual_start_date. POST /api/service-orders/{id}/complete — InProgress→Completed. Auth: service_order_complete. Body: ServiceCompletionRequest { consumedMaterials[], actualHours, completionNotes, customerSignature }. Triggers consumption confirmation + ledger. POST /api/service-orders/{id}/cancel — Any non-terminal→Cancelled. Auth: service_order_cancel. Releases reservations, cancels SRs.

- [ ] **A36-P2-11** — Service Material Endpoints (Endpoints #9–#11) `2h`
  - Schema: `material` | FSD: §13.2 endpoints 9–11 | Depends: A36-P2-06 | Tests: TS-11, TS-12, TS-32
  - GET /api/service-orders/{id}/materials — List SMRs for a Service Order. Auth: service_order_read. Response includes BOM-driven and ad-hoc SMRs, with is_adhoc flag. POST /api/service-orders/{id}/materials — Add ad-hoc material. Auth: service_order_write. Body: { productId, productVariantId, quantity, uom, notes }. Only allowed when status = InProgress (SVC-06). DELETE /api/service-orders/{id}/materials/{smrId} — Remove ad-hoc SMR. Auth: service_order_write. Only if not yet issued (hard delete).

### Track 2E — Service Order UI

- [ ] **A36-P2-12** — UI — Service Order List Page `3h`
  - Schema: — (UI) | FSD: §15.1 | Depends: A36-P2-09 | Tests: —
  - Route: /service-orders. Page title: 'Service Orders'. Toolbar: '+ New Service Order' button (service_order_create). Filters: Status multi-select, Customer searchable dropdown, Assigned To dropdown, Date Range (scheduled_date), Priority. Table columns: Service # (link), Customer, Service Product, Scheduled Date, Assigned To, Status (badge), Priority (badge), Material Readiness (icon). Status badges: Draft(grey), Planned(blue), MaterialPending(amber), Waiting(orange), Ready(green), InProgress(teal), Completed(dark green), Closed(grey), Cancelled(red). Server-side pagination 25/page. Default sort: scheduled_date ASC, priority DESC.

- [ ] **A36-P2-13** — UI — Service Order Detail Page (Tabs 1–2) `4h`
  - Schema: — (UI) | FSD: §15.2 (Tab 1 & 2) | Depends: A36-P2-09, A36-P2-11 | Tests: —
  - Route: /service-orders/{id}. Header: Service Number (large), Status badge, Priority badge, Customer name (link to BP), Service Product name, Assigned To, Scheduled Date/Time, Estimated Hours, Source SO link (if source_type=SalesOrder). **Tab 1 — Details:** Editable fields when Draft/Planned (customer, warehouse, assigned user/team, scheduled date/time, estimated hours, priority, notes). Read-only when InProgress+. BOM reference (number, version, link). **Tab 2 — Material Availability:** Mirrors Addendum 31 §C8 layout. Section 1 (Available): SMRs with stock, Reserve/Reserve All buttons. Section 2 (Shortage): SMRs with shortage, Allocation Request button → Register Demand dialog. Section 3 (Ad-hoc, visible InProgress/Waiting): '+ Add Material' button → product search + qty + UOM, ad-hoc SMR list with 'Ad-hoc' tag.

### Track 2F — Phase 2 Tests

- [ ] **A36-P2-14** — Phase 2 Unit & Integration Tests `4h`
  - Schema: `material` | FSD: §17.2 | Depends: A36-P2-01–13 | Tests: TS-06–TS-13, TS-29
  - TS-06: Create SO from Sale Order confirmation (service product line) → SO with source_type=SalesOrder. TS-07: Plan SO with BOM, all stock → SMRs created, all reserved, status=Ready. TS-08: Plan SO with BOM, partial shortage → SMRs created, partial reservation, status=MaterialPending, SR created. TS-09: Plan SO without BOM → no SMRs, material_readiness=NotApplicable, status=Ready. TS-10: Ready→InProgress → actual_start_date set. TS-11: Ad-hoc material, stock available → SMR(is_adhoc=true), immediately issued. TS-12: Ad-hoc material, stock unavailable → SMR created, SR created, status=Waiting. TS-13: Waiting→InProgress on material arrival (GRN → Allocation → SMR shortage resolved). TS-29: Quantity > 1 BOM explosion (3× oil changes → materials scaled ×3). Concurrency: row_version conflict on simultaneous update.

---

## Phase 3 — Material Issue & Service Completion (C6, C7)

**Priority:** P1 — Sequenced after Phase 2 | **Estimate:** 64 Hours (8 Days) | **FSD:** §7, §8, §12 M5–M6

> **⚡ Sequenced:** Depends on Phase 2 (ServiceOrders and SMR tables must exist for FK references in MaterialIssues extension).

### Track 3A — Material Issues Extension (C6)

- [ ] **A36-P3-01** — Extend MaterialIssues for Service Orders (Migration M5 — DDL) `2h`
  - Schema: `material` | FSD: §7.2, §12.2 M5 | Depends: A36-P2-01 | Tests: TS-10, TS-14, TS-15
  - Step 1: ALTER TABLE material.MaterialIssues ADD service_order_id BIGINT NULL FK → material.ServiceOrders(id). Step 2: ALTER TABLE material.MaterialIssues ADD issue_source_type TINYINT NOT NULL DEFAULT 0 (DF_MI_SourceType). Step 3: ALTER COLUMN production_order_id BIGINT NULL (relax from NOT NULL — existing data preserved). Step 4: ADD CONSTRAINT CK_MaterialIssues_SourceXOR CHECK ((issue_source_type=0 AND production_order_id IS NOT NULL AND service_order_id IS NULL) OR (issue_source_type=1 AND service_order_id IS NOT NULL AND production_order_id IS NULL)). **⚠️ CRITICAL:** Must drop existing NOT NULL constraint on production_order_id before ALTER COLUMN. All existing MI records retain production_order_id values (no data loss).

- [ ] **A36-P3-02** — Extend MaterialIssueLines for Service Orders (Migration M6 — DDL) `1.5h`
  - Schema: `material` | FSD: §7.3, §12.2 M6 | Depends: A36-P3-01 | Tests: TS-14, TS-15
  - ALTER TABLE material.MaterialIssueLines ADD smr_id BIGINT NULL FK → material.ServiceMaterialRequirements(id) (FK_MILines_SMR). ALTER COLUMN pmr_id BIGINT NULL (relax from NOT NULL). ADD CONSTRAINT CK_MILines_ReqXOR CHECK ((pmr_id IS NOT NULL AND smr_id IS NULL) OR (smr_id IS NOT NULL AND pmr_id IS NULL)).

- [ ] **A36-P3-03** — MaterialIssue Entity Updates for Polymorphic Source `2h`
  - Schema: `material` | FSD: §7.2, §7.3 | Depends: A36-P3-02 | Tests: —
  - MaterialIssue entity: change ProductionOrderId to nullable (long?). Add ServiceOrderId (long?), IssueSourceType (IssueSourceType enum: ProductionOrder=0, ServiceOrder=1). Navigation: ServiceOrder → material.ServiceOrders. Update IEntityTypeConfiguration: HasOne for ServiceOrder FK, CK_MaterialIssues_SourceXOR as HasCheckConstraint. MaterialIssueLine entity: change PmrId to nullable (long?). Add SmrId (long?). Navigation: ServiceMaterialRequirement. CK_MILines_ReqXOR as HasCheckConstraint. Update all MaterialIssue DTOs to include service_order_id, issue_source_type, smr_id.

- [ ] **A36-P3-04** — Material Issue Service Extension for Service Orders `3h`
  - Schema: `material` | FSD: §7.4, SVC-MI-01–SVC-MI-07 | Depends: A36-P3-03 | Tests: TS-10, TS-11, TS-33
  - Extend IMaterialIssueService to support Service Orders: CreateForServiceOrderAsync(serviceOrderId, smrId, quantity, warehouseId). SVC-MI-01: validate SO status = Ready, InProgress, or Waiting. SVC-MI-02: issued quantity ≤ reserved quantity per SMR. SVC-MI-03: stock_transactions with movement_type = MATERIAL_ISSUE. SVC-MI-04: stock_balances.on_hand_quantity decreases. SVC-MI-05: AllocationRecord.consumed_quantity increases. SVC-MI-06: SMR.issued_quantity increases, SMR status → Issued. SVC-MI-07: partial issue allowed. Set issue_source_type = ServiceOrder, service_order_id populated, production_order_id = NULL. Lines: smr_id populated, pmr_id = NULL.

- [ ] **A36-P3-05** — Ad-hoc Material Addition Service `3h`
  - Schema: `material` | FSD: §6.5 | Depends: A36-P3-04, A36-P2-06 | Tests: TS-11, TS-12
  - AddAdhocMaterial(serviceOrderId, productId, variantId, quantity, uom, notes): validate SO status = InProgress (SVC-06). Create SMR with is_adhoc=true, bom_line_id=NULL, source_type=Stock, is_critical=true, added_by=currentUser. Check availability via Allocation Engine: (a) Full stock → allocate + issue immediately (technician waiting). (b) Partial stock → allocate + issue available, create SupplyRequirement for remainder, SO→Waiting. (c) No stock → create SupplyRequirement, SO→Waiting. Also handle remove ad-hoc SMR (hard delete only if not yet issued).

### Track 3B — Service Completion (C7)

- [ ] **A36-P3-06** — Service Completion Service `4h`
  - Schema: `material` | FSD: §8.1, §8.2, §8.3, §8.4, SVC-COMP-01–SVC-COMP-06 | Depends: A36-P3-04 | Tests: TS-14, TS-15, TS-28
  - IServiceCompletionService.CompleteAsync(serviceOrderId, ServiceCompletionRequest, ct). Validations: SVC-COMP-01: status must be InProgress. SVC-COMP-02: consumed_quantity ≤ issued_quantity per SMR. SVC-COMP-03: consumed_quantity ≥ 0. SVC-COMP-04: actual_hours > 0 when invoicing_policy = TimeAndMaterial. SVC-COMP-05: return_quantity = issued - consumed (auto-calculated). SVC-COMP-06: all issued SMRs must have consumed_quantity confirmed. Execution: (1) Set SMR.consumed_quantity per confirmation line. (2) For each SMR where issued > consumed → create MATERIAL_RETURN stock_transaction, stock_balances.on_hand_quantity increases, SMR.returned_quantity set, SMR status → Returned or Consumed. (3) Set actual_hours, actual_end_date, completion_notes, customer_signature. (4) Create Service Ledger entries (deferred to Phase 4 service — calls IServiceLedgerService). (5) SO status → Completed. (6) If source_type = SalesOrder → update SaleOrderLine fulfillment_status. (7) Publish ServiceCompletedEvent via MediatR.

- [ ] **A36-P3-07** — Material Return Integration `2h`
  - Schema: `material` | FSD: §7.5 | Depends: A36-P3-06 | Tests: TS-15
  - Process material returns during service completion: for each SMR where issued_quantity > consumed_quantity, returnQty = issued - consumed. Create StockTransaction with movement_type = MATERIAL_RETURN, SourceDocumentType = 'SERVICE_ORDER'. stock_balances.on_hand_quantity += returnQty. SMR.returned_quantity = returnQty. SMR.status updated (Consumed if consumed + returned = issued, or partial). Release allocation records for returned quantity.

- [ ] **A36-P3-08** — Cancellation Service — Reservation Release `2h`
  - Schema: `material` | FSD: §5.4 ST-10, §16.1 SVC-08 | Depends: A36-P3-04 | Tests: TS-16, TS-30
  - CancelServiceOrder(serviceOrderId): validate status is non-terminal. SVC-08: release all stock reservations via IAllocationEngine.ReleaseAsync(). Cancel outstanding SupplyRequirements. If materials already issued → require material return first (TS-30: cancellation after issue requires return). SO status → Cancelled. MediatR: ServiceOrderCancelledEvent.

### Track 3C — Service Completion UI

- [ ] **A36-P3-09** — UI — Service Order Detail: Completion Tab (Tab 3) `3h`
  - Schema: — (UI) | FSD: §15.2 Tab 3, §8.2 | Depends: A36-P2-13, A36-P3-06 | Tests: —
  - Visible when status = InProgress or later. Consumption Confirmation table: columns Product, Issued Qty (read-only), Consumed Qty (editable number input), Return Qty (auto-calculated, read-only: issued - consumed). Actual Hours number input. Completion Notes textarea. Customer Sign-off checkbox. 'Complete Service' button → calls POST /service-orders/{id}/complete. Validation: consumed ≤ issued per row, actual_hours > 0 for T&M. Confirmation dialog before submit: "This will finalize consumption and return unused materials to warehouse."

- [ ] **A36-P3-10** — UI — Ad-hoc Material Addition Panel `2h`
  - Schema: — (UI) | FSD: §15.2 Tab 2 Section 3 | Depends: A36-P2-13, A36-P3-05 | Tests: —
  - Integrated into Material Availability Tab (Section 3), visible when InProgress/Waiting. '+ Add Material' button → product search dropdown (catalog search), quantity input, UOM dropdown. Submit calls POST /service-orders/{id}/materials. On success: SMR added to list with 'Ad-hoc' badge. If stock unavailable → toast: "Material not fully available. Service Order set to Waiting." Remove button (trash icon) on ad-hoc SMRs not yet issued → calls DELETE endpoint.

### Track 3D — Phase 3 Tests

- [ ] **A36-P3-11** — Phase 3 Integration Tests `4h`
  - Schema: `material` | FSD: §17.2 TS-10–TS-16, §17.6 TS-28, TS-30, TS-33 | Depends: A36-P3-01–10 | Tests: TS-10, TS-14–TS-16, TS-28, TS-30, TS-33
  - TS-10: Start service (Ready→InProgress) → actual_start_date set, MI created for reserved materials. TS-14: Complete service, all consumed → consumed qty confirmed, no returns, ledger created, status=Completed. TS-15: Complete service, partial consumption → consumed < issued, MATERIAL_RETURN created, stock_balances increased. TS-16: Cancel SO in MaterialPending → reservations released, SRs cancelled, status=Cancelled. TS-28: Labor-only service (no BOM, no materials) → completes with empty ledger, actual_hours recorded. TS-30: Cancel after materials issued → must return materials first. TS-33: Multiple MIs for same SO → partial + additional + ad-hoc issues all linked via SMRs. XOR constraint tests: MI with both production_order_id and service_order_id → rejected. MI line with both pmr_id and smr_id → rejected.

---

## Phase 4 — Service Ledger (C8)

**Priority:** P1 — Sequenced after Phase 3 | **Estimate:** 24 Hours (3 Days) | **FSD:** §9, §12 M7

> **⚡ Sequenced:** Depends on Phase 3 (Service Completion triggers ledger entry creation).

### Track 4A — Service Ledger Entity & Service

- [ ] **A36-P4-01** — Create material.ServiceLedgerEntries Table (Migration M7 — DDL) `1.5h`
  - Schema: `material` | FSD: §9.2, §9.3, §12.2 M7 | Depends: A36-P2-01 | Tests: TS-24–TS-27
  - CREATE TABLE material.ServiceLedgerEntries: id BIGINT IDENTITY PK, org_id, service_order_id FK → material.ServiceOrders, service_order_number NVARCHAR(50), entry_type TINYINT DEFAULT 0, product_id FK → lookups.Products, product_variant_id FK (NULL), product_name NVARCHAR(200), product_type TINYINT, quantity DECIMAL(18,4), uom NVARCHAR(20), warehouse_id FK → warehouse.Warehouses, warehouse_name NVARCHAR(100), source_document_type NVARCHAR(20), source_document_id BIGINT, source_document_number NVARCHAR(50), stock_transaction_id BIGINT, movement_type NVARCHAR(30), transaction_date DATETIMEOFFSET, notes NVARCHAR(500) NULL, created_at DATETIMEOFFSET DEFAULT SYSUTCDATETIME(). 2 indexes: IX_ServiceLedger_SO, IX_ServiceLedger_Product.

- [ ] **A36-P4-02** — ServiceLedgerEntry EF Core Entity & Configuration `1h`
  - Schema: `material` | FSD: §9.2 | Depends: A36-P4-01 | Tests: —
  - Entity class with all properties. LedgerEntryType enum: Debit=0 (service ledger is debit-only). IEntityTypeConfiguration: ToTable("ServiceLedgerEntries", "material"), HasQueryFilter, all FK navigations (ServiceOrder, Product). DbContext: DbSet<ServiceLedgerEntry>.

- [ ] **A36-P4-03** — Service Ledger Service — Entry Creation `3h`
  - Schema: `material` | FSD: §9.4, §9.6, SVC-LED-01–SVC-LED-05 | Depends: A36-P4-02, A36-P3-06 | Tests: TS-24, TS-25
  - IServiceLedgerService.CreateEntriesAsync(serviceOrderId, completionResult): called from ServiceCompletedEvent handler. SVC-LED-01: every completed SO with material consumption gets DEBIT entries (labor-only = zero entries, valid). SVC-LED-02: entries are IMMUTABLE — no update/delete. SVC-LED-03: sum of DEBIT quantities by product matches SMR consumed_quantity totals. SVC-LED-04: return entries = negative DEBIT (e.g., −0.5L Engine Oil returned). SVC-LED-05: entries inherit trace_id from Service Order. Denormalize: service_order_number, product_name, warehouse_name for display performance. Source document = MaterialIssue that issued the materials.

- [ ] **A36-P4-04** — Service Ledger API Endpoint (Endpoint #12) `1h`
  - Schema: `material` | FSD: §13.3 endpoint 12 | Depends: A36-P4-03 | Tests: TS-26, TS-27
  - GET /api/service-orders/{id}/ledger — List ledger entries for a Service Order. Auth: service_order_read. Response: List<ServiceLedgerEntryDto> ordered by transaction_date. Include computed net consumption per product (sum DEBIT, accounting for returns).

### Track 4B — Service Ledger UI

- [ ] **A36-P4-05** — UI — Service Order Detail: Ledger Tab (Tab 4) `2h`
  - Schema: — (UI) | FSD: §15.2 Tab 4 | Depends: A36-P4-04, A36-P2-13 | Tests: —
  - Mirrors Production Ledger tab (Addendum 30 §19A.8). Chronological entry list: columns Date, Entry Type, Product, Qty, UOM, Warehouse, Source Document, Movement Type. Color coding: DEBIT (consumption) in RED, RETURN (negative DEBIT) in GREEN. Running total per product at bottom. Summary row: total unique products consumed, total consumption value (if cost data available). Empty state for labor-only services: "No material consumption for this service."

- [ ] **A36-P4-06** — UI — Service Order Detail: Timeline Tab (Tab 5) `1.5h`
  - Schema: — (UI) | FSD: §15.2 Tab 5 | Depends: A36-P2-13 | Tests: —
  - DocumentTimeline entries for this Service Order, filtered by trace_id. Shows all status transitions (Draft→Planned→Ready→InProgress→Completed→Closed), material issues, material returns, service completion event. Chronological order. Each entry: timestamp, event type, user, details.

### Track 4C — Phase 4 Tests

- [ ] **A36-P4-07** — Phase 4 Unit Tests `2h`
  - Schema: `material` | FSD: §17.5 | Depends: A36-P4-01–06 | Tests: TS-24–TS-27
  - TS-24: Complete service with 3 materials → 3 DEBIT entries, one per material. TS-25: Complete with partial return → DEBIT for consumed + negative DEBIT for returned qty. TS-26: Query ledger by service order → all entries chronologically. TS-27: Query ledger by product across all SOs → consumption analysis. Additional: ledger immutability — no update/delete operations exposed. Trace_id linkage verified. Denormalized fields match source entities.

---

## Phase 5 — Fulfillment Integration & Mixed SO Lines (C9, C10)

**Priority:** P1 — Final Phase | **Estimate:** 40 Hours (5 Days) | **FSD:** §10, §11, §12 M8–M10

> **⚡ Sequenced:** Depends on Phase 2 (ServiceOrders table for FK), Phase 3 (Material Issue integration), Phase 4 (Ledger). This phase connects the Service Order lifecycle to Sale Orders and completes the end-to-end flow.

### Track 5A — Fulfillment Extension (C9)

- [ ] **A36-P5-01** — Extend FulfillmentRequirements (Migration M8 — DDL) `1h`
  - Schema: `demand` | FSD: §10.4, §12.2 M8 | Depends: A36-P2-01 | Tests: TS-17
  - ALTER TABLE demand.FulfillmentRequirements ADD service_required_quantity DECIMAL(18,4) NOT NULL DEFAULT 0 (DF_FR_ServiceQty). ALTER TABLE demand.FulfillmentRequirements ADD service_order_id BIGINT NULL FK → material.ServiceOrders(id) (FK_FR_ServiceOrder).

- [ ] **A36-P5-02** — Seed SVC Document Number Sequence (Migration M9 — INSERT) `0.5h`
  - Schema: `lookups` | FSD: §12.2 M9 | Depends: — | Tests: —
  - INSERT INTO lookups.document_number_sequences per org: prefix='SVC', current_sequence=0, date_format='yyyyMMdd', padding_length=3, separator='-'. Idempotent with WHERE NOT EXISTS.

- [ ] **A36-P5-03** — Backfill MaterialIssue issue_source_type (Migration M10 — UPDATE) `0.5h`
  - Schema: `material` | FSD: §12.2 M10 | Depends: A36-P3-01 | Tests: —
  - UPDATE material.MaterialIssues SET issue_source_type = 0 WHERE issue_source_type = 0 (explicit backfill — all existing MIs are for Production Orders). Verification query: SELECT COUNT(*) FROM material.MaterialIssues WHERE production_order_id IS NULL AND service_order_id IS NULL → must return 0.

- [ ] **A36-P5-04** — FulfillmentMethod.Service Enum & FulfillmentRequirements Entity Update `1.5h`
  - Schema: `demand` | FSD: §10.2, §10.4 | Depends: A36-P5-01 | Tests: —
  - Add FulfillmentMethod.Service = 5 to existing enum (after Mixed=4). Update FulfillmentRequirement entity: ServiceRequiredQuantity (decimal), ServiceOrderId (long?). Navigation: ServiceOrder → material.ServiceOrders. Update IEntityTypeConfiguration with new FK. Update FulfillmentRequirementDto.

- [ ] **A36-P5-05** — Fulfillment Decision Routing for Service Products `3h`
  - Schema: `demand, material` | FSD: §10.3, §11.2 | Depends: A36-P5-04, A36-P2-03 | Tests: TS-17
  - Extend FulfillmentRequirementService.CreateFromSaleOrder(): for each SO line where product.ProductType == Service: set fulfillmentReq.FulfillmentMethod = Service. Set service_required_quantity = line.Quantity. Create ServiceOrder: copy service_product_id, customer_id, quantity from SO line. Set source_type=SalesOrder, source_id=SO.id, source_line_id=line.id. Copy invoicing_policy, billing_model from product defaults. Calculate estimated_hours = product.estimated_duration_hours × quantity. Set scheduled_date from line.requested_delivery_date. If product.has_service_bom → lookup and attach active BOM (bom_id, bom_version). Set trace_id from fulfillment requirement for traceability.

### Track 5B — Mixed Sale Order Lines (C10)

- [ ] **A36-P5-06** — Sale Order Mixed Lines — Routing & Indicators `2.5h`
  - Schema: `demand` | FSD: §11.1, §11.2, §11.3, §11.4 | Depends: A36-P5-05 | Tests: TS-17–TS-20
  - SO confirmation routes each line by product_type: StockItem/FinishedGood/etc → existing Delivery Order flow. Service → Service Order flow (via A36-P5-05). Both coexist on same SO — independent fulfillment paths. SO line completion indicators (extend Addendum 32 §C4): Service Pending (grey), Service In Progress (blue), Service Waiting (amber), Service Completed (green), Service Cancelled (red). SO overall completion: requires ALL lines fulfilled (deliveries confirmed + services completed). Partial fulfillment: SO line-by-line indicator mix (e.g., Line 1 green delivered, Line 2 blue in progress).

- [ ] **A36-P5-07** — UI — Sale Order: Service Line Indicators `2h`
  - Schema: — (UI) | FSD: §15.5 | Depends: A36-P5-06 | Tests: —
  - On SO detail page, for lines with service products: show service-specific status indicator (color-coded per §11.4) instead of delivery indicators. 'View Service Order' link next to indicator → navigates to /service-orders/{id}. Service completion percentage: (completed / total service orders) × 100. Mixed SO: show both delivery and service indicators, each line labeled by its fulfillment type.

- [ ] **A36-P5-08** — SO Update on Service Completion `1.5h`
  - Schema: `demand, material` | FSD: §8.2 step 6, §16.1 SVC-11 | Depends: A36-P3-06 | Tests: TS-18, TS-19
  - Handle ServiceCompletedEvent: find source SaleOrderLine via source_line_id. Update SO line fulfillment_status to reflect service completion. Check if ALL SO lines now fulfilled (deliveries + services) → update SO overall status. Invoice generation note: when all lines fulfilled, invoice becomes available (actual invoice creation deferred to invoicing addendum — §11.5 documents the data flow only).

### Track 5C — Dashboard & Integration Tests

- [ ] **A36-P5-09** — UI — Service Orders Dashboard `3h`
  - Schema: — (UI) | FSD: §15.6 | Depends: A36-P2-09 | Tests: —
  - Route: /service-orders/dashboard. 4 sections: **Today's Services:** SO with scheduled_date = today, grouped by status (tiles). **Waiting for Materials:** SOs in MaterialPending/Waiting, sorted by priority, with material detail expandable. **My Assigned Services:** SOs assigned to current user, sorted by scheduled_date, status badge. **Completion Rate:** (completed this week / total scheduled this week) × 100, displayed as progress bar with percentage.

- [ ] **A36-P5-10** — End-to-End Integration Tests `4h`
  - Schema: `demand, material` | FSD: §17.2, §17.3, §17.4, §17.6 | Depends: A36-P5-01–09 | Tests: TS-17–TS-23, TS-31, TS-32
  - **TS-17:** Confirm SO with StockItem line + Service line → Delivery Order + Service Order created. **TS-18:** Delivery confirmed, Service InProgress → SO shows green + blue. **TS-19:** Both completed → SO fully fulfilled, invoice available. **TS-20:** PassThrough billing → invoice includes material lines. **TS-21:** Plan SO with BOM containing Subcontract line → stock SMRs + SR + PO to vendor. **TS-22:** Subcontract PO received → SR fulfilled, SMR shortage resolved. **TS-23:** Service completed with subcontracted labor → ledger shows material debits, vendor bill exists. **TS-31:** Concurrent allocation: two SOs compete for same material → Allocation Engine priority rules apply. **TS-32:** Ad-hoc material added then removed before issue → hard delete, no stock impact.

---

## Cross-Cutting & Permissions

### Permission Claims Setup

- [ ] **A36-X-01** — Seed Permission Claims & Role Assignments `1.5h`
  - Schema: `auth` | FSD: §14 | Depends: — | Tests: —
  - Seed 5 new permission claims: service_order_create (ADMIN, SERVICE_MANAGER, SALES), service_order_read (all roles), service_order_write (ADMIN, SERVICE_MANAGER, TECHNICIAN), service_order_complete (ADMIN, SERVICE_MANAGER, TECHNICIAN), service_order_cancel (ADMIN, SERVICE_MANAGER). Add to role-permission seed data. Update tenant provisioning to include new claims for new orgs.

### Services Registry

- [ ] **A36-X-02** — DI Registration for All New Services `1.5h`
  - Schema: — (infrastructure) | FSD: §4, §6, §7, §8, §9, §10, Appendix B | Depends: All service implementations | Tests: —
  - Register in Program.cs / ServiceCollectionExtensions: IServiceOrderService → ServiceOrderService (Scoped). IServiceOrderStateMachine → ServiceOrderStateMachine (Scoped). IServiceBomExplosionService → ServiceBomExplosionService (Scoped). IServiceCompletionService → ServiceCompletionService (Scoped). IServiceLedgerService → ServiceLedgerService (Scoped). ServiceOrderDemandHandler as handler for AllocationDemandType.ServiceOrder (Appendix B — only DI registration needed, no engine changes). MediatR handlers: ServiceCompletedEvent → ServiceLedgerHandler, SaleOrderFulfillmentHandler. ServiceOrderCancelledEvent → ReservationReleaseHandler.

### Allocation Engine Integration

- [ ] **A36-X-03** — Register ServiceOrderDemandHandler in Allocation Engine `1h`
  - Schema: `material` | FSD: Appendix B | Depends: A36-P2-06 | Tests: TS-31
  - Implement IAllocationDemandHandler for AllocationDemandType.ServiceOrder = 3. Register in DI container. Handler delegates to existing Allocation Engine methods — no new allocation logic required. Verify: AllocateForDemandAsync, ConsumeAsync, ReleaseAsync all work with ServiceOrder demand type. This is the ONLY code change to the Allocation Engine — the engine itself (Addendum 30 §14) remains untouched.

---

## Summary by Migration

| Migration | Phase | Task(s) | Schema | Type |
|---|---|---|---|---|
| M1 — AddServiceFieldsToProducts | 1 | A36-P1-01 | lookups | ALTER TABLE |
| M2 — AddSourceTypeToBOMLines | 1 | A36-P1-06 | material | ALTER TABLE |
| M3 — CreateServiceOrders | 2 | A36-P2-01 | material | CREATE TABLE |
| M4 — CreateServiceMaterialRequirements | 2 | A36-P2-04 | material | CREATE TABLE |
| M5 — ExtendMaterialIssuesForServices | 3 | A36-P3-01 | material | ALTER TABLE |
| M6 — ExtendMaterialIssueLinesForServices | 3 | A36-P3-02 | material | ALTER TABLE |
| M7 — CreateServiceLedgerEntries | 4 | A36-P4-01 | material | CREATE TABLE |
| M8 — ExtendFulfillmentRequirements | 5 | A36-P5-01 | demand | ALTER TABLE |
| M9 — SeedServiceDocNumberSequence | 5 | A36-P5-02 | lookups | INSERT |
| M10 — BackfillMaterialIssueSourceType | 5 | A36-P5-03 | material | UPDATE |

## Summary by API Endpoint

| # | Endpoint | Phase | Task |
|---|---|---|---|
| 1 | POST /api/service-orders | 2 | A36-P2-09 |
| 2 | GET /api/service-orders | 2 | A36-P2-09 |
| 3 | GET /api/service-orders/{id} | 2 | A36-P2-09 |
| 4 | PUT /api/service-orders/{id} | 2 | A36-P2-09 |
| 5 | POST /api/service-orders/{id}/plan | 2 | A36-P2-10 |
| 6 | POST /api/service-orders/{id}/start | 2 | A36-P2-10 |
| 7 | POST /api/service-orders/{id}/complete | 2 | A36-P2-10 |
| 8 | POST /api/service-orders/{id}/cancel | 2 | A36-P2-10 |
| 9 | GET /api/service-orders/{id}/materials | 2 | A36-P2-11 |
| 10 | POST /api/service-orders/{id}/materials | 2 | A36-P2-11 |
| 11 | DELETE /api/service-orders/{id}/materials/{smrId} | 2 | A36-P2-11 |
| 12 | GET /api/service-orders/{id}/ledger | 4 | A36-P4-04 |

## Summary by Business Rule

| Rule ID | Change | Task |
|---|---|---|
| SVC-P-01–07 | C1 Service Product | A36-P1-03 |
| SVC-BOM-01–06 | C2 Service BOM | A36-P1-08 |
| SVC-01–13 | C3/C4 Service Order | A36-P2-03, A36-P2-07 |
| ST-01–10 | C4 State Transitions | A36-P2-07 |
| SVC-MI-01–07 | C6 Material Issue | A36-P3-04 |
| SVC-COMP-01–06 | C7 Service Completion | A36-P3-06 |
| SVC-LED-01–05 | C8 Service Ledger | A36-P4-03 |
| SVC-SR-01–03 | Supply Requirement | A36-P2-06 |
| SVC-BILL-01–05 | Billing Rules | A36-P3-06 (data flow), invoicing addendum (actual generation) |

## Summary by Test Scenario

| Test ID | Phase | Task |
|---|---|---|
| TS-01–05 | 1 | A36-P1-10 |
| TS-06–13, TS-29 | 2 | A36-P2-14 |
| TS-10, TS-14–16, TS-28, TS-30, TS-33 | 3 | A36-P3-11 |
| TS-24–27 | 4 | A36-P4-07 |
| TS-17–23, TS-31–32 | 5 | A36-P5-10 |

## Dependency Graph

```
Phase 1 (6 days) ─────────────────────────────────────────────────────────►
  A36-P1-01 (M1: Service Fields on Products)
    └── A36-P1-02 (Entity + Enums)
         └── A36-P1-03 (Validation SVC-P-01–07)
              └── A36-P1-04 (API Update)
                   └── A36-P1-05 (UI: Service Config Section)
  A36-P1-06 (M2: BOM Line source_type)
    └── A36-P1-07 (Entity + Enum)
         └── A36-P1-08 (BOM Eligibility + Validation)
              └── A36-P1-09 (UI: Service BOM Section)
  A36-P1-10 (Phase 1 Tests)

Phase 2 (10 days) ─── depends on Phase 1 ────────────────────────────────►
  A36-P2-01 (M3: ServiceOrders)
    └── A36-P2-02 (Entity + Enums + Config)
         └── A36-P2-03 (ServiceOrderService CRUD)
              └── A36-P2-09 (API #1–4)
                   └── A36-P2-12 (UI: List Page)
  A36-P2-04 (M4: SMRs)
    └── A36-P2-05 (Entity + Config)
         └── A36-P2-06 (BOM Explosion Service)
              └── A36-P2-07 (State Machine)
                   └── A36-P2-08 (Material Readiness)
                        └── A36-P2-10 (API #5–8)
              └── A36-P2-11 (API #9–11)
  A36-P2-13 (UI: Detail Page Tabs 1–2)
  A36-P2-14 (Phase 2 Tests)

Phase 3 (8 days) ─── depends on Phase 2 ─────────────────────────────────►
  A36-P3-01 (M5: MaterialIssues Extension)
    └── A36-P3-02 (M6: MaterialIssueLines Extension)
         └── A36-P3-03 (Entity Updates)
              └── A36-P3-04 (MI Service Extension)
                   └── A36-P3-05 (Ad-hoc Material Service)
                   └── A36-P3-08 (Cancellation Service)
  A36-P3-06 (Service Completion Service)
    └── A36-P3-07 (Material Return Integration)
  A36-P3-09 (UI: Completion Tab)
  A36-P3-10 (UI: Ad-hoc Panel)
  A36-P3-11 (Phase 3 Tests)

Phase 4 (3 days) ─── depends on Phase 3 ─────────────────────────────────►
  A36-P4-01 (M7: ServiceLedgerEntries)
    └── A36-P4-02 (Entity)
         └── A36-P4-03 (Ledger Service)
              └── A36-P4-04 (API #12)
  A36-P4-05 (UI: Ledger Tab)
  A36-P4-06 (UI: Timeline Tab)
  A36-P4-07 (Phase 4 Tests)

Phase 5 (5 days) ─── depends on Phase 2, 3, 4 ──────────────────────────►
  A36-P5-01 (M8: FulfillmentRequirements Extension)
    └── A36-P5-04 (Entity + Enum)
         └── A36-P5-05 (Fulfillment Routing)
              └── A36-P5-06 (Mixed Lines + Indicators)
                   └── A36-P5-07 (UI: SO Service Indicators)
                   └── A36-P5-08 (SO Update on Completion)
  A36-P5-02 (M9: SVC Sequence Seed)
  A36-P5-03 (M10: Backfill issue_source_type)
  A36-P5-09 (UI: Dashboard)
  A36-P5-10 (E2E Integration Tests)

Cross-Cutting ─── independent ────────────────────────────────────────────►
  A36-X-01 (Permission Claims)
  A36-X-02 (DI Registration)
  A36-X-03 (Allocation Engine Handler)
```

---

*End of Task Register — Addendum 36*
