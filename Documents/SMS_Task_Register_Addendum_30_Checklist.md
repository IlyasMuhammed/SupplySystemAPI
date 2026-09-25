# SMS Task Register — Addendum 30: Manufacturing, Production & Allocation Engine

**Document:** SMS-TR-ADD-030 v1.0 | **FSD:** SMS-FSD-ADD-030 v1.1
**Total:** 79 Tasks | 536 Hours | 67 Dev Days | 20 Migrations

---

## Phase 1 — Foundation (Product Catalog + Allocation Engine)

**Priority:** P0 — Critical Path | **Estimate:** 72 Hours (9 Days) | **FSD:** §6–7, §14–18, §27

### Track A — Product Catalog

- [ ] **A30-P1-01** — Add ProductType, SupplyMethod & Manufacturing Flags (Migration M1) `4h`
  - Schema: `lookups` | FSD: §6, §27 M1 | Depends: — | Tests: T-PB01
  - EF Core migration: product_type, supply_method, is_manufacturable, is_saleable, is_purchasable, is_stockable, is_bom_input, default_production_warehouse_id, lead_time_days

- [ ] **A30-P1-02** — Seed Existing Products with Defaults (Migration M2) `2h`
  - Schema: `lookups` | FSD: §6.3, §27 M2 | Depends: A30-P1-01 | Tests: T-PB01
  - UPDATE Products SET product_type='StockItem', supply_method='Purchase' WHERE product_type IS NULL

- [ ] **A30-P1-03** — ProductType & SupplyMethod Enums + Entity Extension `6h`
  - Schema: `lookups` | FSD: §6.1, §6.2, §6.3 | Depends: A30-P1-02 | Tests: T-PB01, T-PB02
  - ProductType (8 values), SupplyMethod (4 values), EF HasConversion, FluentValidation, AutoMapper

- [ ] **A30-P1-04** — Chained Manufacturing Validation (is_bom_input) `3h`
  - Schema: `lookups` | FSD: §6.4.1, BR-P01–P04 | Depends: A30-P1-03 | Tests: T-PB02, T-CM01
  - Only FinishedGood/SemiFinished can have is_bom_input=true, supply_method must be Manufacture

- [ ] **A30-P1-05** — Product API Extensions `3h`
  - Schema: `lookups` | FSD: §28.1 | Depends: A30-P1-04 | Tests: T-PB01, T-PB02
  - Extend ProductsController with product_type/supply_method DTOs, filter params, /api/products/manufacturable

### Track B — Allocation Engine

- [ ] **A30-P1-06** — Create AllocationRecords Table (Migration M8) `4h`
  - Schema: `inventory` | FSD: §14, §27 M8 | Depends: — | Tests: T-AL01
  - inventory.AllocationRecords with demand_type, supply_type, priority, RowVersion, HasQueryFilter

- [ ] **A30-P1-07** — Create AllocationRules Table (Migration M9) `2h`
  - Schema: `inventory` | FSD: §14.3, §27 M9 | Depends: A30-P1-06 | Tests: T-AL01
  - inventory.AllocationRules with demand_type, priority, allocation_strategy (FIFO/FEFO/Priority)

- [ ] **A30-P1-08** — Seed Default Allocation Rules (Migration M18) `1h`
  - Schema: `inventory` | FSD: §14.3, §27 M18 | Depends: A30-P1-07 | Tests: T-AL01
  - SALES_ORDER priority=1, PRODUCTION_ORDER=2, TRANSFER_ORDER=3, MANUAL=4

- [ ] **A30-P1-09** — AllocationRecord & AllocationRule Entities + EF Config `4h`
  - Schema: `inventory` | FSD: §14, §14.3 | Depends: A30-P1-08 | Tests: T-AL01, T-AL02
  - DemandType, SupplyType, AllocationStatus enums, RowVersion concurrency token, AutoMapper, FluentValidation

- [ ] **A30-P1-10** — IAllocationEngine Interface & Core Implementation `12h`
  - Schema: `inventory` | FSD: §14.1, §14.2, §32 | Depends: A30-P1-09 | Tests: T-AL01–04, T-AL07
  - 6 methods: AllocateAsync, AllocateForDemandAsync, ReleaseAsync, ReallocateAsync, ConsumeAsync, GetAvailabilityAsync
  - SERIALIZABLE TX + RowVersion concurrency, priority-based allocation

- [ ] **A30-P1-11** — Allocation Engine — GRN Integration Handler `4h`
  - Schema: `inventory, procurement` | FSD: §15, §18 | Depends: A30-P1-10 | Tests: T-AL05, T-AL08
  - MediatR: GRNApprovedEvent → auto-allocate to highest-priority unmet demand

### Track C — Infrastructure (Independent)

- [ ] **A30-P1-12** — Add PRODUCTION_ORDER to ReservationSourceType (Migration M10) `1h`
  - Schema: `inventory` | FSD: §27 M10 | Depends: — | Tests: —

- [ ] **A30-P1-13** — Add Manufacturing Movement Types (Migration M11) `1h`
  - Schema: `inventory` | FSD: §27 M11 | Depends: — | Tests: —
  - MATERIAL_ISSUE, FINISHED_GOODS_RECEIPT, PRODUCTION_SCRAP, MATERIAL_RETURN

- [ ] **A30-P1-14** — Add Document Number Sequences (Migration M20) `2h`
  - Schema: `logistics` | FSD: §27 M20 | Depends: — | Tests: —
  - BOM-, PROD-, SR-, MI-, QI-, FGR- prefixes

- [ ] **A30-P1-15** — Seed Manufacturing Permission Claims (Migration M19) `2h`
  - Schema: `auth` | FSD: §27 M19 | Depends: — | Tests: —
  - 17 permission claims mapped to IT_ADMIN and WAREHOUSE_STAFF roles

### Phase 1 Completion

- [ ] **A30-P1-16** — Allocation Engine Unit & Integration Tests `8h`
  - Schema: `inventory` | FSD: §14, §36.4 | Depends: A30-P1-11 | Tests: T-AL01–08

- [ ] **A30-P1-17** — Allocation Engine API Endpoints `4h`
  - Schema: `inventory` | FSD: §28.8 | Depends: A30-P1-10 | Tests: T-AL01
  - AllocationController: GET list, GET by id, POST manual, DELETE release, GET availability, GET by demand

- [ ] **A30-P1-18** — Product Catalog & Allocation Frontend `6h`
  - Schema: — | FSD: §29.1, §29.4 | Depends: A30-P1-05, A30-P1-17 | Tests: —
  - Product form extensions + Allocation dashboard

---

## Phase 2 — BOM Management

**Priority:** P0 — Critical Path | **Estimate:** 104 Hours (13 Days) | **FSD:** §7–9, §27 M3–M4

- [ ] **A30-P2-01** — Create BillOfMaterials Table (Migration M3) `3h`
  - Schema: `material` | FSD: §7.1, §27 M3 | Depends: A30-P1-01 | Tests: T-PB03
  - material.BillOfMaterials with 6-status workflow (Draft→Submitted→Approved→Active→Obsolete, Rejected)

- [ ] **A30-P2-02** — Create BillOfMaterialLines Table (Migration M4) `2h`
  - Schema: `material` | FSD: §7.2, §27 M4 | Depends: A30-P2-01 | Tests: T-PB03
  - material.BillOfMaterialLines with component_product_id, quantity, wastage_percent, scrap_percent

- [ ] **A30-P2-03** — BOM Entities & EF Configuration `5h`
  - Schema: `material` | FSD: §7.1, §7.2, §8 | Depends: A30-P2-02 | Tests: T-PB03, T-PB04
  - BOMStatus enum, entities, AutoMapper profiles, FluentValidation

- [ ] **A30-P2-04** — BOM CRUD Service `8h`
  - Schema: `material` | FSD: §8, §28.2 | Depends: A30-P2-03 | Tests: T-PB03–05
  - IBOMService: Create, GetById, GetList, Update, Delete, AddLine, UpdateLine, RemoveLine, RecalculateTotalCost

- [ ] **A30-P2-05** — BOM Approval Workflow Service `8h`
  - Schema: `material` | FSD: §9, BR-B01–B05 | Depends: A30-P2-04 | Tests: T-PB06, T-PB07
  - IBOMApprovalService: Submit, Approve (4-eyes principle), Reject, Activate (deactivates others), Obsolete

- [ ] **A30-P2-06** — BOM Versioning Service `6h`
  - Schema: `material` | FSD: §9.3 | Depends: A30-P2-04 | Tests: T-PB08
  - IBOMVersionService: CreateNewVersion (clone as Draft v+1), CompareVersions, GetVersionHistory

- [ ] **A30-P2-07** — BOM Circular Reference Detection `4h`
  - Schema: `material` | FSD: §9.4, BR-B04 | Depends: A30-P2-04 | Tests: T-PB09, T-CM02
  - DFS circular reference check, max depth 10, chained manufacturing support

- [ ] **A30-P2-08** — BOM Cost Roll-Up Service `4h`
  - Schema: `material` | FSD: §7.3 | Depends: A30-P2-07 | Tests: T-PB05
  - IBOMCostService: recursive cost calculation including chained manufactured components

- [ ] **A30-P2-09** — BOM API Endpoints `6h`
  - Schema: `material` | FSD: §28.2 | Depends: A30-P2-05, A30-P2-06, A30-P2-07, A30-P2-08 | Tests: T-PB03, T-PB04, T-PB06
  - BOMController: full CRUD, line management, workflow actions, versioning, comparison

- [ ] **A30-P2-10** — BOM UI — List & Create/Edit `8h`
  - Schema: — | FSD: §29.2 | Depends: A30-P2-09 | Tests: —
  - BOM list with status filters, create/edit form, line editor, inline cost display

- [ ] **A30-P2-11** — BOM UI — Approval, Versioning & Comparison `6h`
  - Schema: — | FSD: §29.2 | Depends: A30-P2-09 | Tests: —
  - Workflow buttons, approval history, version comparison, BOM tree visualization

- [ ] **A30-P2-12** — BOM Unit & Integration Tests `8h`
  - Schema: `material` | FSD: §36.1 | Depends: A30-P2-09 | Tests: T-PB03–09

---

## Phase 3 — Production Core

**Priority:** P0 — Critical Path | **Estimate:** 152 Hours (19 Days) | **FSD:** §11–13, §16, §27 M5–M7, M14

### Migrations

- [ ] **A30-P3-01** — Create ProductionOrders Table (Migration M5) `4h`
  - Schema: `material` | FSD: §11, §27 M5 | Depends: A30-P1-01, A30-P2-01 | Tests: T-PR01
  - 9-status state machine, parent_production_order_id for chained mfg, trace_id, RowVersion

- [ ] **A30-P3-02** — Create ProductionMaterialRequirements Table (Migration M6) `3h`
  - Schema: `material` | FSD: §12, §27 M6 | Depends: A30-P3-01 | Tests: T-PR03
  - PMR with required/issued/returned/wastage/shortage quantities

- [ ] **A30-P3-03** — Create SupplyRequirements Table (Migration M7) `3h`
  - Schema: `material` | FSD: §13, §27 M7 | Depends: A30-P3-02 | Tests: T-SR01
  - SR with supply_method (Purchase/Manufacture/Transfer), linked_po_id, linked_production_order_id

- [ ] **A30-P3-04** — Create MaterialIssues Table (Migration M14) `3h`
  - Schema: `material` | FSD: §16, §27 M14 | Depends: A30-P3-02 | Tests: T-PR05
  - MI header + lines, 5 issue types (Standard/Additional/Return/Scrap/Substitution)

### Entities & Services

- [ ] **A30-P3-05** — ProductionOrder Entity & EF Configuration `6h`
  - Schema: `material` | FSD: §11 | Depends: A30-P3-01–04 | Tests: T-PR01, T-PR02
  - ProductionOrderStatus enum (9 values), all relationships, RowVersion concurrency

- [ ] **A30-P3-06** — ProductionOrder CRUD Service `8h`
  - Schema: `material` | FSD: §11, §28.3 | Depends: A30-P3-05 | Tests: T-PR01, T-PR02
  - IProductionOrderService: Create, GetById, GetList, Update, Cancel, state machine validation

- [ ] **A30-P3-07** — BOM Explosion Service (Plan/Release) `8h`
  - Schema: `material, inventory` | FSD: §12, §12.2, BR-PR03 | Depends: A30-P3-06, A30-P2-04 | Tests: T-PR03, T-PR04, T-CM03
  - Draft→Planned, create PMR per BOM line, gross_required_qty with scrap allowance, chained mfg flagging

- [ ] **A30-P3-08** — Material Readiness Calculation Service `8h`
  - Schema: `material, inventory` | FSD: §11.3, BR-PR04 | Depends: A30-P3-07, A30-P1-10 | Tests: T-PR04, T-PR06
  - IMaterialReadinessService: checks PMRs, queries allocation availability, auto-transitions PO status

- [ ] **A30-P3-09** — Supply Requirement Engine `12h`
  - Schema: `material, procurement` | FSD: §13, BR-S01–S07 | Depends: A30-P3-08 | Tests: T-SR01–06, T-CM03
  - ISupplyRequirementEngine: Purchase→auto-create PO, Manufacture→auto-create child PO, Transfer→SR

- [ ] **A30-P3-10** — PMR & SupplyRequirement Entities + EF Config `4h`
  - Schema: `material` | FSD: §12, §13 | Depends: A30-P3-05 | Tests: T-PR03, T-SR01

- [ ] **A30-P3-11** — Material Issue Service `8h`
  - Schema: `material, inventory` | FSD: §16, BR-PR05 | Depends: A30-P3-10, A30-P1-13 | Tests: T-PR05, T-PR07
  - IMaterialIssueService: Create, Confirm (inventory deduction), Return. 5 issue types

- [ ] **A30-P3-12** — Production Execution Service `4h`
  - Schema: `material` | FSD: §17, BR-PR06–07 | Depends: A30-P3-11 | Tests: T-PR08–10
  - IProductionExecutionService: Start, ReportOutput, Complete. Over-production validation

### APIs & UI

- [ ] **A30-P3-13** — Production Order API Endpoints `5h`
  - Schema: `material` | FSD: §28.3 | Depends: A30-P3-12 | Tests: T-PR01, T-PR02
  - ProductionOrderController: CRUD, plan, start, report-output, complete, cancel, readiness, shortages

- [ ] **A30-P3-14** — Material Issue API Endpoints `3h`
  - Schema: `material` | FSD: §28.4 | Depends: A30-P3-11 | Tests: T-PR05

- [ ] **A30-P3-15** — Supply Requirement API Endpoints `3h`
  - Schema: `material` | FSD: §28.7 | Depends: A30-P3-09 | Tests: T-SR01

- [ ] **A30-P3-16** — Production Order UI — List, Create & Detail `8h`
  - Schema: — | FSD: §29.3 | Depends: A30-P3-13 | Tests: —
  - List with filters, create form, detail tabs (Overview, PMRs, SRs, MIs, Timeline)

- [ ] **A30-P3-17** — Production Order UI — Execution & Shortage Dashboard `6h`
  - Schema: — | FSD: §29.3 | Depends: A30-P3-13, A30-P3-14 | Tests: —
  - Execution panel, material readiness gauge, shortage dashboard, MI dialog

- [ ] **A30-P3-18** — Production Core Unit & Integration Tests `12h`
  - Schema: `material, inventory` | FSD: §36.2, §36.3 | Depends: A30-P3-13–15 | Tests: T-PR01–10, T-SR01–06

---

## Phase 4 — Quality, FGR, Production Ledger & Integration

**Priority:** P1 — High | **Estimate:** 144 Hours (18 Days) | **FSD:** §10, §18–19A, §20, §27 M12–M17

### Track A — Quality + FGR

- [ ] **A30-P4-01** — Create QualityInspections Table (Migration M12) `3h`
  - Schema: `material` | FSD: §18, §27 M12 | Depends: A30-P3-01 | Tests: T-QF01
  - QI header + lines with 4 decisions (Passed/Rejected/Hold/Rework)

- [ ] **A30-P4-02** — Create FinishedGoodsReceipts Table (Migration M13) `3h`
  - Schema: `material` | FSD: §19, §27 M13 | Depends: A30-P3-01, A30-P4-01 | Tests: T-QF06

- [ ] **A30-P4-04** — QualityInspection Entity, Service & Workflow `8h`
  - Schema: `material` | FSD: §18, BR-PR06 | Depends: A30-P4-01, A30-P3-05 | Tests: T-QF01–05
  - IQualityInspectionService: Create, AddLine, Complete. Emit QICompletedEvent (MediatR)

- [ ] **A30-P4-05** — Finished Goods Receipt Service `10h`
  - Schema: `material, inventory` | FSD: §19, §19.3 | Depends: A30-P4-02, A30-P4-04, A30-P1-10, A30-P1-13 | Tests: T-QF06–08
  - IFGRService: 9-step Confirm (stock credit, ledger entry, allocation trigger, PO completion)

- [ ] **A30-P4-16** — QI API Endpoints `3h`
  - Schema: `material` | FSD: §28.5 | Depends: A30-P4-04 | Tests: T-QF01

- [ ] **A30-P4-17** — FGR API Endpoints `2h`
  - Schema: `material` | FSD: §28.6 | Depends: A30-P4-05 | Tests: T-QF06

- [ ] **A30-P4-18** — QI & FGR UI `6h`
  - Schema: — | FSD: §29.3 | Depends: A30-P4-16, A30-P4-17 | Tests: —

### Track B — Production Ledger

- [ ] **A30-P4-03** — Create ProductionLedgerEntries Table (Migration M14A) `3h`
  - Schema: `material` | FSD: §19A, §27 M14A | Depends: A30-P3-01, A30-P3-04, A30-P4-02 | Tests: T-PL01
  - Immutable debit/credit ledger (no UPDATE/DELETE)

- [ ] **A30-P4-06** — Production Ledger Entity & Service `6h`
  - Schema: `material` | FSD: §19A, BR-L01–L07 | Depends: A30-P4-03 | Tests: T-PL01–05
  - IProductionLedgerService: AppendEntry, GetLedger, GetSummary. Called ONLY by MediatR handlers

- [ ] **A30-P4-07** — Production Ledger MediatR Event Handlers `6h`
  - Schema: `material` | FSD: §19A.3, §19A.4 | Depends: A30-P4-06, A30-P3-11, A30-P4-05, A30-P4-04 | Tests: T-PL01–04, T-PL06
  - 4 handlers: MaterialIssuedEvent→Debit, FGRConfirmedEvent→Credit, QIRejectedEvent→Scrap Debit, MaterialReturnedEvent→Credit

- [ ] **A30-P4-08** — Production Ledger API Endpoints `4h`
  - Schema: `material` | FSD: §19A.5, §28.9 | Depends: A30-P4-07 | Tests: T-PL07, T-PL08
  - Ledger entries, summary, by-product, export, cross-order view

- [ ] **A30-P4-19** — Production Ledger UI `6h`
  - Schema: — | FSD: §29.5 | Depends: A30-P4-08 | Tests: —
  - Debit/credit table (color coded), summary panel, export, cross-PO view

### Track C — Fulfillment + Integration

- [ ] **A30-P4-09** — Create FulfillmentRequirements Table (Migration M15) `2h`
  - Schema: `demand` | FSD: §10.2, §27 M15 | Depends: — | Tests: —

- [ ] **A30-P4-10** — Extend SaleOrderLines for Fulfillment (Migration M16) `2h`
  - Schema: `demand` | FSD: §10.1, §27 M16 | Depends: A30-P4-09 | Tests: —
  - Add fulfillment_method, production_order_id, allocated_qty, manufacturing_status, expected_completion_date

- [ ] **A30-P4-11** — FulfillmentRequirement Service `8h`
  - Schema: `demand, material, inventory` | FSD: §10, §20 | Depends: A30-P4-09, A30-P4-10, A30-P3-06, A30-P1-10 | Tests: T-CM04
  - IFulfillmentRequirementService: determine method (InStock/Manufacture/Purchase/Split), auto-create PO or allocate

- [ ] **A30-P4-12** — Post-FGR Allocation Flow `6h`
  - Schema: `inventory, demand` | FSD: §19.3, §20 | Depends: A30-P4-05, A30-P4-11 | Tests: T-QF07, T-QF08, T-AL08
  - On FGRConfirmedEvent: allocate FG to highest-priority Sales Order demand

- [ ] **A30-P4-13** — GRN → Allocation Engine → PMR Readiness Integration `6h`
  - Schema: `procurement, material, inventory` | FSD: §15, §18 | Depends: A30-P3-08, A30-P1-11 | Tests: T-SR05, T-AL05
  - GRN approval → update SR fulfilled_qty → recalculate PMR readiness → auto-transition PO

- [ ] **A30-P4-14** — Add PRODUCTION to DeliverySourceType (Migration M17) `1h`
  - Schema: `logistics` | FSD: §27 M17 | Depends: — | Tests: —

- [ ] **A30-P4-15** — Delivery Integration for Production Output `4h`
  - Schema: `logistics, material` | FSD: §20 | Depends: A30-P4-14, A30-P4-05 | Tests: —
  - Extend DeliveryOrderService for PRODUCTION source type

- [ ] **A30-P4-20** — Allocation Engine UI Extensions `4h`
  - Schema: — | FSD: §29.4 | Depends: A30-P1-17 | Tests: —
  - Allocation dashboard, manual allocation, availability checker, demand queue

### Phase 4 Completion

- [ ] **A30-P4-21** — Phase 4 Integration Tests `12h`
  - Schema: `all` | FSD: §36 | Depends: A30-P4-07, A30-P4-12, A30-P4-13, A30-P4-15 | Tests: T-CM04–06, T-PL05–06, T-QF07–08
  - E2E: SO→FR→PO→BOM→PMR→SR→MI→QI→FGR→Allocation→Fulfilled
  - Chained: 3-level chain (Raw→Semi→FG→Assembly)
  - Ledger verification: debit = credit + scrap

---

## Phase 5 — Polish & Hardening

**Priority:** P2–P3 | **Estimate:** 64 Hours (8 Days) | **FSD:** §30–31, §38, §32, §40

- [ ] **A30-P5-01** — Manufacturing Notifications (Hangfire) `6h`
  - Schema: `auth, hangfire` | FSD: §30 | Depends: A30-P3-12, A30-P4-07 | Tests: —
  - 10 notifications: PO Created/Ready/Completed, Shortage Alert, QI Required/Completed, FGR Completed, SR Created, Allocation Completed, Chained PO Created

- [ ] **A30-P5-02** — Reports R1–R5: Production Order Reports `5h`
  - Schema: `reports` | FSD: §31 R1–R5 | Depends: A30-P3-13 | Tests: —
  - PO Register, Schedule, Material Requirement, Efficiency, WIP

- [ ] **A30-P5-03** — Reports R6–R10: Quality & Supply Reports `5h`
  - Schema: `reports` | FSD: §31 R6–R10 | Depends: A30-P4-16, A30-P3-15 | Tests: —
  - QI Summary, Scrap Analysis, SR Status, Supplier Performance (Mfg), BOM Cost Analysis

- [ ] **A30-P5-04** — Reports R11–R17: Allocation & Inventory Reports `7h`
  - Schema: `reports` | FSD: §31 R11–R17 | Depends: A30-P3-14, A30-P4-17, A30-P1-17 | Tests: —
  - Allocation Summary, Demand vs Supply, Stock Availability, MI Register, FGR Register, PO Completion, Warehouse Utilization

- [ ] **A30-P5-05** — Reports R18–R25: Production Ledger Reports `6h`
  - Schema: `reports` | FSD: §31 R18–R25 | Depends: A30-P4-08 | Tests: T-PL08
  - Ledger Detail/Summary, Material Consumption, Cost Variance, Scrap Cost, Profitability, Reconciliation, Cross-Order

- [ ] **A30-P5-06** — Reports R26–R28: Chained Manufacturing Reports `3h`
  - Schema: `reports` | FSD: §31 R26–R28 | Depends: A30-P3-09, A30-P4-21 | Tests: —
  - Dependency Tree, Multi-Stage Status, Cycle Time

- [ ] **A30-P5-07** — DocumentTimeline Integration `6h`
  - Schema: `workflow` | FSD: §38 | Depends: A30-P3-12, A30-P4-07 | Tests: T-CM05
  - 10 event types, trace_id propagation through SO→FR→PO→SR→MI→QI→FGR

- [ ] **A30-P5-08** — Concurrency Stress Testing `6h`
  - Schema: `inventory, material` | FSD: §32 | Depends: A30-P4-21 | Tests: T-AL07
  - 5 concurrent scenarios, SERIALIZABLE isolation, P99 < 500ms target

- [ ] **A30-P5-09** — Reports Dashboard Frontend `4h`
  - Schema: — | FSD: §29.3 | Depends: A30-P5-06 | Tests: —
  - Manufacturing Reports dashboard organized by category, filter controls, export, summary cards

- [ ] **A30-P5-10** — UAT Preparation & Test Data Setup `5h`
  - Schema: `all` | FSD: §40 | Depends: A30-P5-08 | Tests: All
  - 5 products, 3 multi-level BOMs, 5 UAT scripts, test procedure documentation

---

## Migration Execution Order

Execute in sequence. Each is independently revertible with `dotnet ef database update <previous>`.

| # | Migration Name | Phase | Schema | Depends |
|---|---------------|-------|--------|---------|
| - [ ] M1 | AddProductTypeAndSupplyMethod | P1 | lookups | None |
| - [ ] M2 | UpdateExistingProductDefaults | P1 | lookups | M1 |
| - [ ] M3 | CreateBillOfMaterials | P2 | material | M1 |
| - [ ] M4 | CreateBillOfMaterialLines | P2 | material | M3 |
| - [ ] M5 | CreateProductionOrders | P3 | material | M1, M3 |
| - [ ] M6 | CreateProductionMaterialRequirements | P3 | material | M5 |
| - [ ] M7 | CreateSupplyRequirements | P3 | material | M6 |
| - [ ] M8 | CreateAllocationRecords | P1 | inventory | None |
| - [ ] M9 | CreateAllocationRules | P1 | inventory | M8 |
| - [ ] M10 | AddProductionOrderToReservationSourceType | P1 | inventory | None |
| - [ ] M11 | AddManufacturingMovementTypes | P1 | inventory | None |
| - [ ] M12 | CreateQualityInspections | P4 | material | M5 |
| - [ ] M13 | CreateFinishedGoodsReceipts | P4 | material | M5, M12 |
| - [ ] M14 | CreateMaterialIssues | P3 | material | M5, M6 |
| - [ ] M14A | CreateProductionLedgerEntries | P4 | material | M5, M14, M13 |
| - [ ] M15 | CreateFulfillmentRequirements | P4 | demand | None |
| - [ ] M16 | ExtendSaleOrderLinesForFulfillment | P4 | demand | M15 |
| - [ ] M17 | AddProductionToDeliverySourceType | P4 | logistics | None |
| - [ ] M18 | SeedAllocationRuleDefaults | P1 | inventory | M9 |
| - [ ] M19 | SeedManufacturingPermissions | P1 | auth | None |
| - [ ] M20 | AddDocumentNumberSequences | P1 | logistics | None |

---

## Progress Summary

| Phase | Tasks | Hours | Status |
|-------|-------|-------|--------|
| Phase 1 — Foundation | 0/18 | 0/72h | Not Started |
| Phase 2 — BOM Management | 0/12 | 0/104h | Not Started |
| Phase 3 — Production Core | 0/18 | 0/152h | Not Started |
| Phase 4 — Quality/FGR/Ledger/Integration | 0/21 | 0/144h | Not Started |
| Phase 5 — Polish & Hardening | 0/10 | 0/64h | Not Started |
| **TOTAL** | **0/79** | **0/536h** | **Not Started** |
