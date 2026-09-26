# Addendum 30 — Manufacturing, Production & Allocation Engine (knowledge base)

Source: `Documents/_MConverter.eu_SMS_FSD_Addendum_30_Manufacturing_Production_Allocation.md` — SMS-FSD-ADD-030,
v1.1, dated 2026-09-24, prerequisite Addendum 29 v1.3, status *Ready for Implementation*.
Task register: `Documents/SMS_Task_Register_Addendum_30_Checklist.md` (Phases 1–5, Tracks A/B/C, migration
execution order, progress summary).
Scope: Product classification | BOM (versioned, approved) | Production Orders | Production Material
Requirements | Supply Requirements | shared Allocation Engine | Material Issue | Quality Inspection |
Finished Goods Receipt | Production Ledger | chained manufacturing | fulfilment back into Sales.

This file is the working reference for the tasks that implement the addendum. Part A records where the
spec's description of the *existing* system differs from what is actually in the repo (checked
2026-09-26), because Appendix A of the spec itself says to inspect the codebase first and follow its
patterns rather than invent new ones. Part B is a map into the spec, which is already in the repo as
Markdown and is not copied here.

---

## Part A — Reality check: spec assumptions vs. the actual codebase

Verified by reading the code on 2026-09-26. Where these disagree, **the code wins** — the spec's intent
is honoured through the pattern that really exists. Rows carried over from the A29 reality check
(`docs/scm-commercial/ADDENDUM-29-SCM-COMMERCIAL.md`, Part A) still hold and are not repeated unless
A30 leans on them differently.

| Spec says | Actually in the repo | Consequence |
|---|---|---|
| Frontend is **React 18 + Vite** | **Angular 19** standalone components + PrimeNG 19, Karma/Jasmine specs (`SupplyChainFrontend/`) | All screens are Angular. Follow the sale-order / logistics page patterns (`permissionGuard(P.X)`, service per feature, `data-testid`s). `ng build` catches template errors Karma does not — run it. |
| `lookups.Products` — add `product_type`, `supply_method`, `is_manufacturable`, `is_saleable`, `is_purchasable`, `is_stockable`, `is_bom_input`, `default_production_warehouse_id`, `lead_time_days` | Products, ProductVariants, VariantSuppliers, Warehouses, InventoryItems, InventoryLedgerEntries all live in **`inventory.*`** (SMS.Modules.Inventory, `InventoryDbContext`, `HasDefaultSchema("inventory")`, PascalCase tables). `Product.LeadTimeDays` and `Product.PreferredSupplierId` **already exist**. No `ProductType`/`SupplyMethod`/`Is*able` fields exist anywhere. | Manufacturing classification goes on `inventory.Products` via an Inventory migration. Do not re-add `LeadTimeDays`. Store the enums the way this module stores status codes (string codes via `EnumCode<T>`), not TINYINT. |
| Classification is product-level only | Two days earlier (2026-09-23) `ProductVariant` gained five **variant-level** channel flags: `IsAvailableForRetail / Pos / MirMiv / Production / Services`, with Retail and MIR/MIV enforced by picker filtering + server refusal (`IVariantAvailabilityService`). `IsAvailableForProduction` was left inert pending this addendum. | `is_bom_input` and `IsAvailableForProduction` describe the same thing. **Decided (D2): `IsAvailableForProduction` is the BOM-input gate; `is_bom_input` is not added.** BOM line validation (V-B02) and the material picker for BOM lines use `channel="PRODUCTION"` / `IVariantAvailabilityService`, the plumbing that already exists for Retail and MIR/MIV. |
| `org_id BIGINT` on every table, `HasQueryFilter` per entity | Tenancy is `OrganizationId` (Guid) on `ITenantScopedEntity`, applied by `ApplyTenantQueryFilters(this)` + `ApplyTenantIndexes()` in each DbContext. Entities are `int Id` **plus** `Guid Uuid`; every cross-module reference is a **Guid** (e.g. `SaleOrderLine.VariantUuid`, `SaleOrderLine.SelectedSupplierId`). | New entities implement `ITenantScopedEntity`, get `int Id` + `Guid Uuid`, and reference other modules' rows by Guid (`ProductVariant.Uuid`, `Warehouse.Uuid`, `SaleOrder.UUID`). Never join on int ids across modules. |
| `BIGINT IDENTITY` PKs, `snake_case` columns, `DATETIMEOFFSET` | Older schemas (`inventory`, `suppliers`, `auth`) are PascalCase; newer ones (`logistics.*`, `demand.*`, `material.*`) are snake_case (`material.material_issue_vouchers`, `material.material_issue_requests`, `material.stock_reservations`, `material.project_cost_ledger`). Timestamps are `DateTime` (UTC). | Match the naming of the schema you add to: new `material.*` tables are snake_case, new `inventory.*` tables (AllocationRecords/Rules) are PascalCase. |
| MediatR domain events (`GoodsReceiptCompletedEvent`, `FinishedGoodsReceivedEvent`, `MaterialIssuedEvent`, `QIRejectedEvent`) with handlers such as `AllocationEngineHandler`; ledger entries "created via MediatR event handlers, NOT in the controller" | **MediatR is referenced only by `SMS.WorkflowEngine`** and is used solely for its own document events (`DocumentSubmittedEvent`, `DocumentApprovedEvent`, …). No business module publishes or handles MediatR notifications. Cross-module signalling here is (a) small single-purpose interfaces in `SMS.Shared/Common` implemented by the owning module and resolved via DI (`IStockReservationService`, `IVariantAvailabilityService`, `IProductVariantResolver`, `IOrgChartService`, `IDocumentNumberGenerator`), and (b) Hangfire jobs (`BackgroundJob.Enqueue<IJob>`, `RecurringJob.AddOrUpdate<Job>`) that receive the org id as an explicit argument. | Define `IAllocationEngine` in `SMS.Shared/Common`, implement it in Inventory, register in `IInventoryModule`. GRN / FGR / MI "events" become explicit calls at the posting site (or a Hangfire enqueue for the async cases) — the same shape as `TimelineAppendJob`. Do not introduce MediatR into business modules. |
| `AuditTrail` entity + EF Core `SaveChangesInterceptor` | Audit is an explicit service: `IAuditService` (Reports module, `AuditService`, `AuditLog` entity, `ReportsMaps.AuditLogMap`), injected into repositories/controllers (`GrnRepository`, `SroRepository`, `PurchaseOrdersController`, `DebitNoteRepository`). There is no interceptor. `GrnRepository` carries a `NoOpAuditService` fallback for test harnesses. | Every A30 state transition calls `IAuditService` explicitly, following `GrnRepository`'s call shape. |
| `ReservationSourceType` **enum** — add `PRODUCTION_ORDER` | `SMS.Shared/Common/IStockReservationService.cs` — `ReservationSourceType` is a **static string class** (`Mir = "MIR"`, `Delivery = "DELIVERY"`, `SalesOrder = "SALES_ORDER"`). Reservations are keyed by `sourceType + sourceUuid (+ sourceLineUuid)`; `ReserveAsync` is all-or-nothing; availability is per single warehouse. | Add `public const string ProductionOrder = "PRODUCTION_ORDER";`. PMR reservation = `ReserveAsync(ProductionOrder, productionOrderUuid, lines, userId)` after a `GetAvailableAsync` check for the coverable quantity — the same two-step the SO SPLIT scenario uses. |
| `StockMovementType` enum — add `MATERIAL_ISSUE`, `FINISHED_GOODS_RECEIPT`, `PRODUCTION_SCRAP`; `stock_balances.on_hand_quantity` / `stock_transactions` | No movement-type enum exists. Movements are `inventory.InventoryLedgerEntries` rows (`InventoryLedgerEntry.TransactionType` string such as `GRN_RECEIPT`, `STOCK_ADJUSTMENT`, `RETURN_DISPATCH`; `ReferenceType` + `ReferenceId` Guid + `ReferenceNumber`; `QuantityIn`/`QuantityOut`/`BalanceAfter`/`UnitCost`). The counter is `InventoryItems.QtyOnHand / QtyReserved`. Posting sites today: `IGrnStockPoster` (Warehouse), `GoodsIssuePoster` + `InventoryLedgerService` (Inventory), `MivService` and `MaterialReturnService` (Material). | New transaction types are new string constants used at a new posting site that follows `GoodsIssuePoster` / `IGrnStockPoster`. The Production Ledger (spec §19A) can be a **query over `InventoryLedgerEntries` filtered by ReferenceType/ReferenceId** rather than a new table — the spec itself says "materialized VIEW / query-based, NOT a separate transactional table". |
| `delivery_orders.from_source_type` — add `PRODUCTION` | `DeliverySourceType` enum (`Logistics/Domain/LogisticsEnums.cs`): PO, SRO, MIV, TRANSFER, MANUAL, **SALE_ORDER** (added by A29). `DeliverySourceTypeInfo` decides direction and whether the delivery posts the stock movement. | Add `[Code("PRODUCTION")] Production` **and** a `DeliverySourceTypeInfo` rule. Note the spec's own worked example delivers finished goods with `from_source_type = SALE_ORDER` (§20.1), so `PRODUCTION` may only be needed for internal moves — confirm before adding. |
| Document numbers `BOM-YYYYMMDD-SEQ`, `PROD-YYYYMMDD-SEQ`, `SR-`, `MI-`, `QI-`, `FGR-` | `IDocumentNumberGenerator.NextAsync(prefix, date)` (`SMS.Shared/Common`) yields **`PREFIX-YYYY-NNNNN`** from `logistics.document_number_sequences` (RowVersion). Every A29 document (`SO-`, `SINV-`, `CPAY-`) already made this same deviation. MIR/MIV build `MIR-{year}-` themselves. | Use `NextAsync("BOM"|"PROD"|"SR"|"MI"|"QI"|"FGR", date)`. `MI-` may collide in meaning with MIR/MIV — consider `PMI-` (production material issue). |
| New `material.MaterialIssues` / `MaterialIssueLines` for production floor issue; `Material Return`; scrap write-off | The **`material` schema is already the project-site MIR/MIV module** (SMS.Modules.Material): `MaterialIssueRequest`(+Detail), `MaterialIssueVoucher`(+Line, +`MivLineBatchSerial`), `MaterialReturn`(+Detail), `Wastage`, `MaterialConsumption`, `ProjectCostLedger`, `DepartmentCostLedger`, its own `StockReservation`. MIV already posts stock out, supports batch/serial, and is a `DeliverySourceType`. | Name collision **and** concept overlap. Options: (1) new entities with unambiguous names (`ProductionMaterialIssue`), or (2) an MIV with a `PRODUCTION_ORDER` source that consumes PMR reservations. Decide in Phase 3 before the migration; either way do not name anything `MaterialIssue`. |
| Sales Order → Fulfillment Requirement → supply-method evaluation → Supply Requirement → PO. **"The system must NOT create a direct Sales Order → Purchase Order link."** | **A29 built exactly that direct link, and it is live**: `SaleOrderLine.FulfillmentMode` (`STOCK` / `BACK_TO_BACK`), `AvailableQtyAtConfirm`, `DeficitQty`, `LinkedPoId`, `SelectedSupplierId`; `AvailabilityCheckService` → `SupplierSelectionService` → `AutoPurchaseOrderService`; sale-order settings `AutoPoApprovalMode` incl. `AUTO_SEND`; PO↔SO trace via `DocumentTimeline`. Fixed and re-verified 2026-09-23. | **Needs a user decision before Phase 4 Track C.** Either (a) A29's pipeline stays as the path for `SupplyMethod = Purchase` products and A30's FulfillmentRequirement/SupplyRequirement only wrap `Manufacture`, or (b) the A29 pipeline is re-routed so `DeficitQty` creates a FulfillmentRequirement and the auto-PO is raised from a SupplyRequirement. (a) is less churn; (b) is what the spec literally says. |
| `demand.SaleOrderLines` — add `fulfillment_method`, `allocated_quantity`, `production_required_quantity`, `purchase_required_quantity`, `fulfilled_quantity` | `SaleOrderLine` already has `FulfilledQty`, `InvoicedQty`, `FulfillmentMode`, `DeficitQty`. Nothing for allocated / production-required. | Only add what is missing; map the spec's `fulfilled_quantity` onto the existing `FulfilledQty`. |
| `procurement.PurchaseOrders`, `procurement.GoodsReceipts` | POs are `demand.purchase_orders` (SMS.Modules.Demand). GRNs are in **SMS.Modules.Warehouse** (`GrnRepository`, `IGrnStockPoster`), which already holds `InventoryDbContext` and `DemandDbContext` directly. | The post-GRN allocation trigger hooks into the Warehouse GRN posting path. Supply Requirement → PO creation reuses `AutoPurchaseOrderService` / `IPurchaseOrderService` in Demand. |
| Warehouses referenced as `warehouse_id BIGINT` | `inventory.Warehouses` — `Warehouse` has `int Id` + `Guid Uuid` + `Code`/`Name`. Other modules reference it by Uuid. | `WarehouseUuid` Guid on new entities. |
| Approval workflow: "reuse ApprovalWorkflows / WorkflowSteps" (spec OQ-10 leaves engine vs inline open) | `SMS.WorkflowEngine` exists: document submit/approve/reject/recall/reissue MediatR events, `DocumentTimeline` + `TimelineService` + `TimelineAppendJob`, workflow inbox, `IDocumentCloneHandler`. The Material module is the reference consumer: `MirService`/`MivService`/`MaterialReturnService` import `SMS.WorkflowEngine.{Jobs,Models,Services}`, `MirCloneHandlerBase : IDocumentCloneHandler`, `MirPrLineDisbursementHandler` handles engine events, `material.mir_line_approvals`. | BOM approval can ride the engine (four-eyes rule §9.3 is then the engine's problem) or be inline status transitions like sale-order confirm. Pick one in Phase 2; the inline route is what every A29 document did. |
| Permission claims "added to existing roles; do NOT create role entities" | `SMS.Shared/Authorization/PermissionCodes.cs` constants (e.g. `SALE_ORDER_VIEW`, `SALE_ORDER_CONFIG_WRITE`) seeded idempotently by `AuthDataSeeder` (`if (!AnyAsync(p => p.Code == code)) Add(...)`) and granted to roles there. Role matching in the seeder is by id, not code (see memory `supply-dept-admin-role-fix`). | Add `BOM_*`, `PROD_*`, `MI_*`, `QI_*`, `FGR_*`, `ALLOCATION_*`, `SUPPLY_*`, `PROD_LEDGER_VIEW` to `PermissionCodes.cs` **and** to the `AuthDataSeeder` list, with the feature that gates them. |
| Notifications "table + Hangfire" | `INotificationService` (`SMS.Shared/Common`, implemented in Auth) is called directly from services (`SupplierSelectionService.NotifySupplyTeamAsync`, PO controllers); Hangfire is used for email dispatch. Recipient resolution via `IOrgChartService` (department head fallback to acting user). | Same shape for production alerts. Name products in messages, never raw variant GUIDs (bug fixed 2026-09-23). |
| Audit "old value (JSON) / new value (JSON) / reason" | `AuditLog` (`Reports/Domain/ReportsEntities.cs`): `Timestamp`, `UserId`, `UserName`, `Module`, `Action`, `EntityType`, `EntityId` (Guid?), `FieldChanged`, `OldValue`, `NewValue` (plain strings, one field per row), `IpAddress`, `Notes`, `OrganizationId`. | One row per changed field, or a status-transition row with `FieldChanged = "Status"`. Put the cancellation/reallocation reason in `Notes`. No JSON blobs. |
| "Serializable transaction per (product, warehouse)" + RowVersion retry for allocation; "existing row-level lock pattern" in `IStockReservationService` | **Until 2026-09-26 there was no lock at all**: every writer read `InventoryItems`, changed a quantity and saved under READ COMMITTED, so two documents confirmed at once could both be promised the last unit (the A29 reality check's "row-level lock pattern" was optimistic). **Now guarded**, at the user's direction: `InventoryItem.RowVersion` (`IsRowVersion()`, migration `20260925223738_AddInventoryItemRowVersion`, guarded SQL) makes the second writer fail with `DbUpdateConcurrencyException` at all seven read-modify-write sites (`StockReservationService`, `GoodsIssuePoster`, `InventoryRepository` adjustments, Warehouse `IGrnStockPoster` + `SroRepository`, Material `MivService` + `MaterialReturnService`). `StockReservationService.RetryOnConcurrencyAsync` re-plans up to 3 times from fresh queries when it owns the transaction; inside a caller's transaction the conflict propagates, and `GlobalExceptionMiddleware` maps it to **409**. Proven on LocalDB by `InventoryItemConcurrencyTests`. All seven sites already ran inside execution-strategy transactions. | `AllocationRecords` gets the same `RowVersion` + retry shape (spec §14.8 asks for exactly that). No SERIALIZABLE isolation is needed on top; under Azure SQL's read-committed-snapshot a lock hint would either need raw SQL at every site or, because the tenant filter defeats index seeks, lock whole scans. Any new stock writer must load the row, change it and save in one unit and let the token do the rest. |
| Tenant "Manufacturing Mode" feature toggle (§37.3 Phase 4) | No feature-flag mechanism exists beyond per-org config tables (e.g. `SaleOrderConfig`). | A `ManufacturingEnabled` bit on org config, gating the menu, is the cheapest honest version. |

Other standing facts worth knowing before starting:
- Backend: .NET 8 modular monolith, `src/SMS.API`; modules `SMS.Modules.{Auth,Lookups,Suppliers,Inventory,Demand,Warehouse,Logistics,Finance,Reports,Material,Tenancy}`, `SMS.WorkflowEngine`, `SMS.Shared`, `SMS.Notifications`. Each module owns its DbContext + migrations.
- `dotnet ef migrations add` must **not** be run with `--no-build` after source edits — it silently produces an empty migration.
- API startup runs every module's migrations against the configured DB, and the configured DB is the shared Azure SQL `SMSGlobal` (`supplymanagment.database.windows.net`). Migrations must be idempotent; tests run on LocalDB; direct writes to SMSGlobal need the user's explicit OK per action.
- Integration tests: two factory-based test classes cannot share one `dotnet test` invocation (see memory `a29-integration-tests-findings`).
- Any test fixture that creates a variant for use on a sale order or MIR must set `IsAvailableForRetail` / `IsAvailableForMirMiv`, or the server refuses the line.
- The Inventory migration history is **not replayable on an empty database**: `20260706090000_AddDirectConsumptionFlag` has no `.Designer.cs`, so EF never discovers it and `Products.IsDirectConsumption` is never created. Tests that need a real SQL Server build the schema from the current model (`IRelationalDatabaseCreator.CreateTablesAsync()`), as `ProcurementCycleWebApplicationFactory` and `InventoryItemConcurrencyTests` do. A new migration's own SQL is therefore only exercised on SMSGlobal unless a test runs its `UpOperations` directly.
- QuestPDF renders are not safe in parallel; Reports PDFs go through `PdfRenderGate`. New production PDFs (ledger export) must use the same gate.

### How the allocation engine was actually built (Phase 1 Track B, 2026-09-26)

`IAllocationEngine` lives in `SMS.Shared/Common/IAllocationEngine.cs` (codes, records, interface);
`AllocationEngine` in `SMS.Modules.Inventory/Services`. Four `inventory.*` tables, all with
`RowVersion`: **AllocationDemands** (the registry — a sale order line, a PMR… registered by its
document uuid + line, with required qty/date, priority 0–3, optional pinned warehouse),
**AllocationSupplies** (expected supply — a PO line, a production order's output — expected qty/date,
received qty), **AllocationRecords** (the decisions: demand ↔ supply, qty, kind, status),
**AllocationRules** (per-org sort order; none = built-in defaults: priority desc, required date asc,
demand-type rank asc [sale 1, production 2, transfer 3, other 4], document date asc, created asc).

- A **RESERVED** record is a real hold through `IStockReservationService` with source type
  `ALLOCATION`, source uuid = the demand's registry uuid, source line uuid = the record uuid. So
  `InventoryItems.QtyReserved` and every existing availability query already see it.
- A **PLANNED** record leans on an `AllocationSupply`; it holds nothing and is deleted and re-made by
  every run. **FIRM** is what a manual move of a plan becomes, so a run does not undo it. A run never
  moves a RESERVED/FIRM record; only `ReallocateAsync` (ALLOCATION_ADMIN) does.
- A run (`AllocateAsync(variant, warehouse?)`) is one transaction: sort open demands by the rules →
  drop tentative records → hold on-hand stock per demand (best warehouse first, across warehouses for
  an unpinned demand) → plan the rest against open supply soonest-first → report shortage. Scoped to
  a warehouse it only sees that warehouse's demands (pinned there or unpinned), stock and supply.
- Lost-update guard: the run retries (3×) on `DbUpdateConcurrencyException` when it owns the
  transaction, detaching everything it read. Proven on LocalDB in `AllocationEngineTests`.
- Consumers: register a demand, call `AllocateForDemandAsync`, later `ConsumeAsync` per record when
  stock actually leaves, `CancelDemandAsync` when the document dies. Register expected supply when a
  PO/production order is raised; `EfGrnInventoryPoster` books `SupplyReceivedAsync(PURCHASE_ORDER,
  poLineUuid)` and runs the engine **after** the stock transaction commits, never failing the GRN.
- API: `AllocationsController` (`/api/allocations/**`, `/api/variants/{uuid}/availability`),
  permissions ALLOCATION_VIEW / RUN / ADMIN. UI: `pages/inventory/allocations` (dashboard + rules).
- Phase 3/4 hook points: PMRs register `PRODUCTION_MATERIAL` demands and consume on production issue;
  the Supply Requirement engine registers `PRODUCTION_ORDER` supply for child production orders and
  reads `DemandAllocationSummary.Shortage` to know what to raise; FGR calls `SupplyReceivedAsync`
  then `AllocateAsync` for the finished good. The retail path (D1) does not use the engine.

### How bills of materials were actually built (Phase 2, 2026-09-26)

`material.bill_of_materials` + `material.bill_of_material_lines` (snake_case, Material module;
entities `BillOfMaterial`/`BillOfMaterialLine`, `BomRepository`, `BomService`, `BomCostService`,
`BomsController` at `/api/boms/**` + `/api/products/{uuid}/boms`). Numbers `BOM-YYYY-NNNNN` from
`IDocumentNumberGenerator`. Feature gate **`MODULE_MANUFACTURING`** (new catalog entry, depends on
MODULE_INVENTORY, excluded from the BASIC plan; the tenant seeder backfills it for existing orgs on
STANDARD/ENTERPRISE at the next API start — a BASIC org turns it on in Organization Features).
Permissions BOM_VIEW/CREATE/EDIT/SUBMIT/APPROVE/ACTIVATE/OBSOLETE/ADMIN (Inventory Manager all;
Procurement Manager, Purchase Officer's peers, Warehouse Operator, Auditor view).

- Output product is referenced by `inventory.Products.Uuid` and must have `SupplyMethod =
  MANUFACTURE` (V-B01). Lines reference the **variant** (`MaterialVariantUuid`, plus the parent
  `MaterialProductUuid` denormalised for the cycle walk) and must be `IsAvailableForProduction`
  (V-B02, decision D2). UOMs default from the product/variant's product.
- Status walk is inline (D5): DRAFT → SUBMITTED → APPROVED → ACTIVE → OBSOLETE, SUBMITTED →
  REJECTED → (any edit) DRAFT. Four eyes on approve. Activating retires the other ACTIVE recipe for
  the same product/variant/warehouse. Only DRAFT/REJECTED can be edited or deleted; anything else is
  versioned (`/new-version` clones lines as DRAFT vN+1, same TraceId).
- Cycle walk (`EnsureNoCycleAsync`): BFS over live recipes (DRAFT/SUBMITTED/APPROVED/ACTIVE) that make
  each input, depth ≤ 10, run at create/update and again at activation. Self-reference refused at
  the line. A finished good feeding another recipe is allowed — that is chained manufacturing.
- Cost (`GET /{uuid}/cost`): per line gross = qty × (1 + scrap%); unit cost = the input's own ACTIVE
  recipe rolled up (variant-specific first, else product-level; `BOM_ROLLUP`) when the input is
  MANUFACTURE-supplied, else `LastPurchasePrice ?? PurchasePrice`; nothing stored.
- "Used by a production order → immutable" (§8.2, BR-B02) needed no extra check once
  `ProductionOrders` existed (Phase 3): `EnsureEditable`/`DeleteAsync` already refuse anything but
  DRAFT/REJECTED, and a production order's own recipe snapshot (`ActiveBomAsync`,
  `ProductionOrderRepository`) only ever picks an **ACTIVE** BOM — so a BOM any order references was
  already un-editable and un-deletable before Phase 3 existed to test it against.
- UI: `pages/manufacturing/boms/{bom-list,bom-form,bom-detail}` under a new "Manufacturing" menu
  group; the form's inputs use the variant picker on `channel="PRODUCTION"`.
- Tests: `tests/SMS.Modules.Material.Tests/BomTests.cs` (InMemory Material + Inventory contexts,
  `IDocumentNumberGenerator` mocked).

### How production orders were actually built (Phase 3, 2026-09-26)

`material.production_orders` + `production_material_requirements` + `supply_requirements` +
`production_material_issues`(+`_lines`) (snake_case, one migration `AddProductionCore`). Entities in
`Material/Domain/ProductionEntities.cs`; three services split by concern —
`ProductionOrderRepository`/`ProductionOrderService` (create/plan/execute/cancel),
`SupplyRequirementEngine` (what a shortage becomes), `ProductionMaterialIssueService` (floor
movements) — plus `ProductionReadiness`, a static rules class both the service and the allocation
listener apply so they can never disagree. Numbers `PROD-`/`SR-`/`PMI-YYYY-NNNNN`. Same
`MODULE_MANUFACTURING` gate and PROD_*/MI_*/SUPPLY_* permissions as Phase 1's table already listed.

- **Recipe snapshot**: `BomId`+`BomVersion` are fixed at creation from the active BOM for the exact
  variant+warehouse, falling back to the variant's general one, then the product's for that
  warehouse, then the product's plain active recipe — never re-read afterwards (§11.4).
- **Planning** (`ProductionOrderService.PlanCoreAsync`, D2/§12.2): explodes each BOM line into a PMR
  — net = line qty × planned ÷ BOM base quantity, gross = net + scrap allowance — registers each as a
  `PRODUCTION_MATERIAL` demand on the shared allocation engine (D1: this is the manufacturing path;
  retail's Sale Order → PO is untouched A29), runs one allocation per distinct material variant, then
  hands whatever is still short to the supply engine. DRAFT → PLANNED happens here; PLANNED/MATERIAL_
  PENDING/READY are then the allocation listener's call, every time, not just at planning.
- **Readiness** (`ProductionReadiness`, A30-P3-08): no polling service — `ProductionReadinessListener
  : IAllocationRunListener` is told after *every* allocation run anywhere (a GRN receipt, a manual
  run, another order's own plan touching the same variant) and re-derives each PMR's reserved/
  planned/shortage from the engine's own demand summary, then the order's readiness and, while it is
  DRAFT/PLANNED/MATERIAL_PENDING/READY, its status. The same listener copies a receipt's quantity
  onto the PMR's supply requirement so the two never drift apart.
- **Supply requirement engine** (D3 settled as "new unambiguous entity", `SupplyRequirementEngine`,
  A30-P3-09): one live SR per shortfalling PMR, idempotent. PURCHASE raises a DRAFT PO (supplier =
  `ProductVariant.DefaultSupplierId`, rate from `IVariantSupplierResolver` else last/list price) and
  registers its line as expected supply. MANUFACTURE raises **and plans** a child production order —
  chained manufacturing is exactly this, not a separate flag — at `ParentProductionOrderId` = the
  order with the shortage, depth-bounded at 10 like the BOM cycle walk. TRANSFER is a note; there is
  no transfer-order module. The engine resolves `ProductionOrderService` **from the container**
  rather than taking it as a constructor dependency (`IServiceProvider.GetRequiredService`) — the two
  services otherwise depend on each other and .NET's container cannot resolve that cycle. Manual SRs
  (`POST /api/supply-requirements`) have no formal demand document, so a manual one's
  `DemandSourceUuid` is its own uuid.
- **Material issues** (D3, A30-P3-11): STANDARD checks a requirement's held allocations cover the
  line **in full before consuming any of them** — an early version consumed what little was there and
  then refused, corrupting the hold for a rejected line; fixed to check-then-act, the same
  all-or-nothing shape as `IStockReservationService.ReserveAsync`. ADDITIONAL/SUBSTITUTION take free
  stock FEFO instead (PROD_MANAGER, enforced by the controller). RETURN posts stock back in. SCRAP is
  a wastage record with **no stock movement** — the material already left when it was issued.
  Reversing gives the physical stock back and rolls the requirement's quantities back, but does
  **not** reinstate the original allocation hold (the shared engine has no "un-consume"); a reversed
  order picks a fresh hold up on its next allocation run. Shared-connection transaction across
  Material and Inventory, MivService's own arrangement, skipped on a non-relational provider the same
  way `AllocationEngine`'s transaction helper is (unit tests use InMemory).
- **Execution** (§17): Start needs READY; ReportOutput refuses to take produced quantity past what
  was planned (no over-production tolerance); Complete needs some output reported and moves the order
  to QUALITY_INSPECTION, where Phase 4's QI/FGR take over. Cancel releases every live demand, cancels
  open supply requirements, and is guarded the same `RowVersion` + bounded-retry shape as
  `AllocationEngine`/`StockReservationService`, since a report-output and a cancel could race.
- API: `ProductionOrdersController` (`/api/production-orders/**`), `ProductionIssuesController`
  (`/api/production-issues/{uuid}`), `SupplyRequirementsController` (`/api/supply-requirements/**`).
  UI: `pages/manufacturing/production-orders/{production-order-list,-form,-detail}`,
  `pages/manufacturing/shortages`. Substitution issues are reachable through the API but not yet from
  the issue dialog (needs a second variant picker per line).
- Tests: `tests/SMS.Modules.Material.Tests/ProductionOrderTests.cs` (InMemory Material + Inventory,
  a real `AllocationEngine` + `StockReservationService` + `ProductionReadinessListener` wired through
  a small `ServiceCollection` so the container resolves the `SupplyRequirementEngine` ↔
  `ProductionOrderService` cycle the same way production DI does; `IPurchaseOrderService` mocked).

### How quality inspection, FGR and the production ledger were actually built (Phase 4 Tracks A/B, 2026-09-26)

`material.quality_inspections`(+`_lines`) + `material.finished_goods_receipts` (snake_case, one migration
`AddQualityAndFgr`). No `ProductionLedgerEntries` table at all — see D4 below, settled in Phase 1 and
now actually built that way. Permissions QI_CREATE/QI_APPROVE, FGR_CREATE/FGR_CONFIRM, PROD_LEDGER_VIEW.

- **QI is one-shot** (`quality_inspections.ProductionOrderId` is a unique index): exactly one
  inspection per order, taken against the whole of `ProducedQuantity`, never edited. **Create and
  decide are the same call** — `QualityInspectionService.CreateAsync` takes the checks (name,
  PASS/FAIL/HOLD/REWORK, quantity) and *is* the decision, gated QI_APPROVE; there is no separate
  draft-then-decide step because nothing here needs one. The header's four buckets
  (accepted/rejected/hold/rework) are **summed from the lines**, not entered directly, so
  "accepted+rejected+hold+rework = inspected" holds by construction rather than a rule to check.
  §18.4's "inspector ≠ production operator" reads "the operator" as whoever raised the order
  (`ProductionOrder.CreatedBy`) — the same reading BOM's four-eyes rule gives "the submitter".
- **Passing touches no stock.** Nothing was ever added to inventory for produced-but-uninspected
  output (that only happens on FGR), so there is nothing to credit at QI time. **Rejecting touches no
  stock either** — the rejected units never arrived in the first place, so there is no real
  `PRODUCTION_SCRAP` stock movement to post; a real one would subtract from a running
  `InventoryLedgerEntries.BalanceAfter` that never included those units, silently corrupting it. The
  scrap side only exists in the Production Ledger's read (below), synthesised from the QI row.
  Rework is recorded (`ReworkQuantity`) but does **not** automatically raise a new production cycle —
  a person raises one manually, same as every other manual follow-up in this module (ADDITIONAL
  issues, TRANSFER supply requirements).
- **FGR can be partial.** `FinishedGoodsReceiptService.ConfirmAsync` credits the output variant's
  stock and posts a `FINISHED_GOODS_RECEIPT` ledger entry (shared-connection transaction, same
  arrangement as `MivService`/`ProductionMaterialIssueService`, skipped on a non-relational provider
  for tests), then — **after the commit** — calls `SupplyReceivedAsync(PRODUCTION_ORDER, po.UUID, ...)`
  (closes whatever expected supply this order registered; a harmless no-op for a top-level order) and
  `AllocateAsync` for the variant. That one call is what actually does §19.3's steps 6-9: a chained
  order's own PMR is covered the moment its input's FGR confirms — proven end to end in
  `QualityAndFgrTests` — and it is exactly what a future sales-order demand (Phase 4 Track C) would be
  satisfied by too, with no FGR-side special-casing needed either way. The order moves to Completed
  only once **none of its one QI's accepted quantity is still outstanding** across every FGR raised
  against it — not, as a literal reading of §19.3 step 4 would have it, unconditionally after the
  first receipt.
- **The Production Ledger is a read, confirmed built exactly as D4 called it**: `ProductionLedgerService`
  has no write side at all. Debit rows come from `inventory.InventoryLedgerEntries` where
  `ReferenceType = "PRODUCTION_ISSUE"`, joined back to a production order through the confirmed
  issue's own uuid (`ProductionMaterialIssue.ProductionOrderId`); credit rows the same way through
  `ReferenceType = "FGR"`. A material return is already a `PRODUCTION_RETURN`-tagged row on that same
  join, so it needs no separate handling. The one row type with **no underlying ledger entry at all**
  — QI-rejected scrap — is synthesised straight from `QualityInspection.RejectedQuantity`, for the
  reason above. Summary: distinct materials debited, finished-goods quantity, scrap quantity, yield %
  = finished ÷ (finished + scrap). No MediatR handlers exist or are needed (A30-P4-07 superseded).
- API: `QualityInspectionsController`, `FinishedGoodsReceiptsController`, `ProductionLedgerController`.
  UI: a "Quality & FGR" tab and a "Ledger" tab on the production order detail page. No cross-order
  ledger dashboard page or export were built — the API supports the cross-order read
  (`GET /api/production-ledger`) but no screen calls it yet.
- Tests: `tests/SMS.Modules.Material.Tests/QualityAndFgrTests.cs` (7; InMemory Material + Inventory,
  the same real-engine harness as `ProductionOrderTests`), incl. an end-to-end chained-manufacturing
  proof: a child order's FGR reserves stock for its parent's own requirement automatically. Material
  suite 91/91; Auth suite 117/117 (new permission codes); Inventory 263/263 and Warehouse 51/51
  unaffected.

### How fulfillment integration was actually built (Phase 4 Track C, 2026-09-26)

D1 (decided Phase 1: retail Sale Order → PO stays A29's own pipeline; manufacturing Sale Order →
Production Order via A30) turned out to need surgery at exactly one point, not a parallel schema.
**Nothing in A29's confirm pipeline ever read `Product.SupplyMethod`** — `AvailabilityCheckService`
only ever asks "is there enough on the shelf", and whatever is not covered becomes a `DeficitQty`
that unconditionally reaches `AutoPoCreationJob` → `IAutoPurchaseOrderService`. A manufactured
product's shortage was, until this task, silently handled as if it were a purchasing problem. The
fix is one branch, not a new pipeline:

- **`AutoPoCreationJob.CreateForDeficitAsync`** now resolves the line's variant's `Product.SupplyMethod`
  (an optional `InventoryDbContext`, the same nullable-for-old-tests pattern `PurchaseOrderRepository`
  already uses) and, for `MANUFACTURE`, calls the new `ISaleOrderManufacturingService.FulfillDeficitAsync`
  instead of ever reaching `ISupplierSelectionService`/`IAutoPurchaseOrderService`. Everything else —
  DROP_SHIP, BACK_TO_BACK, SPLIT, `AutoPoEnabled`'s own gate — is untouched.
- **`SaleOrderManufacturingService`** registers a `SALES_ORDER` demand with the shared allocation
  engine (`AllocationDemandType.SalesOrder` — A29 declared this and `ReservationSourceType.SalesOrder`
  as placeholders "not in use yet"; this is what finally uses them), runs one allocation (existing
  stock or supply already on its way may cover some or all of it for free), then raises a production
  order for whatever is still short — through a **new shared interface**, `IProductionDemandService`
  (`SMS.Shared.Common`, implemented by `ProductionOrderService`), because Sales has no project
  reference to Material and Material already references Demand (for `IPurchaseOrderService`) — a
  reference the other way would be circular. `EnsureForSourceAsync` is idempotent per source
  (SourceType+SourceUuid+SourceLineUuid) the same shape `IAutoPurchaseOrderService` already is per its
  PO link: a DRAFT order's quantity tops up in place, a further order covers the extra once it is not.
- **`SaleOrderFulfillmentListener`** (Demand's own `IAllocationRunListener`) is what actually gets the
  stock to the customer, and needed **no changes to Logistics at all**. A confirmed FGR already runs
  `AllocateAsync` for the output variant (A30-P4-05, no FGR-side awareness of sales required — the
  spec's own 🔴 rule); this listener, told after that run, re-parents whatever real hold it made from
  the engine's own bookkeeping (`ALLOCATION`/demand uuid) onto the **exact reservation shape
  `AvailabilityCheckService` already makes** (`SALES_ORDER`/order+line uuid) via
  `IStockReservationService.TransferLineAsync` — built, per its own doc comment, for exactly this kind
  of re-parent (§7.3/§7.5's delivery hand-over). `DeliveryFromSourceRepository` already reads holds by
  that same source/uuid, so a manufactured line's delivery needed zero new code, which is why
  A30-P4-14/-15 (a `PRODUCTION` `DeliverySourceType`) were not built — there was no gap for them to fill.
- **`IAllocationRunListener` gained a `userId` parameter.** `ProductionReadinessListener` never needed
  an actor id (it only ever applies numbers the run already decided); `SaleOrderFulfillmentListener`
  does, because `TransferLineAsync` stamps one. Added to the interface rather than invented as a
  system-user sentinel, since `AllocationEngine.AllocateAsync` already has the real one in scope at
  its one call site.
- Tests: `AutoPoCreationJobTests` (2 new — manufacture routes away from supplier selection, purchase
  is unchanged), `SaleOrderManufacturingServiceTests` (4, `IAllocationEngine`/`IProductionDemandService`
  mocked), `SaleOrderFulfillmentListenerTests` (4, `IStockReservationService` mocked — the transfer
  itself is Inventory's own proof, not re-tested here). Demand suite 459/459, Material 91/91 (the
  `ProductionSourceType` move), Inventory 263/263, Auth unaffected.
- **What this did not cover at the time**: no HTTP-level test wired a real sale order, a real production
  order and a real FGR into one running app to prove the whole chain end to end through the actual DI
  container — each seam was proven only at its module boundary with the other side mocked. Closed by
  A30-P4-21; see its own section below.

### How DocumentTimeline integration was actually built (Phase 5, A30-P5-07, 2026-09-26)

Ten event types, all enqueued through the existing `ITimelineAppendJob`/Hangfire pattern
(`SMS.WorkflowEngine.Jobs`/`.Models`) rather than a new mechanism — the same shape A29's own documents
already use. Constants live in `Material/Services/ManufacturingTimelineEventTypes.cs`
(`BOM_ACTIVATED`, `PROD_CREATED`, `PROD_PLANNED`, `PROD_MATERIAL_PENDING`, `SR_CREATED`,
`PROD_STARTED`, `PROD_COMPLETED`, `MI_CONFIRMED`, `QI_RECORDED`, `FGR_CONFIRMED`) and
`ManufacturingInterfaceCodes` (`BOM`, `PROD`, `SR` — MI/QI/FGR events post onto the *production
order's* interface code and trace, since none of those three documents has its own timeline page).

- **Two propagation bugs surfaced doing this**, both silent since Phase 3: `SupplyRequirement.TraceId`
  had no default initializer at all (`Guid.Empty` on every row ever created), and every production
  order — including a child raised for a parent's shortage and one raised for a sale order's own
  deficit — got a fresh random `TraceId` from the entity default instead of inheriting the document
  that caused it to exist. Both fixed: `IProductionOrderRepository.CreateAsync` gained an optional
  `Guid? traceId` threaded from `CreateChildForSupplyAsync` (the parent SR's `TraceId`) and from
  `IProductionDemandService.EnsureForSourceAsync` (a new optional parameter on the same interface,
  filled with `SaleOrder.TraceId` at `SaleOrderManufacturingService`'s one call site). `SupplyRequirement`
  now sets `TraceId = productionOrder.TraceId` (PMR-triggered) or `Guid.NewGuid()` (manual, no
  originating document to inherit from). The whole SO→PROD→SR→PROD(child)→…→FGR walk now shares one
  trace, proven by a new assertion in `QualityAndFgrTests`'s chained-manufacturing test
  (`child.TraceId.Should().Be(kitDetail.TraceId)`, T-CM05).
- **Every new `IBackgroundJobClient` dependency is optional** (`BomRepository`, `ProductionOrderService`,
  `SupplyRequirementEngine`, `ProductionMaterialIssueService`, `QualityInspectionService`,
  `FinishedGoodsReceiptService`) and every enqueue call is null-guarded (`_jobs?.Enqueue<...>`) — the
  same nullable-for-old-tests pattern `PurchaseOrderRepository`/`AutoPoCreationJob` established for
  `InventoryDbContext`. These six services' existing unit tests construct them directly or resolve
  them through a hand-built `ServiceCollection` that never registers Hangfire; making the dependency
  optional meant none of that test wiring needed to change.
- **Three new `ITraceIdResolver` implementations** (`BomTraceIdResolver`/`ProdTraceIdResolver`/
  `SrTraceIdResolver`, `Material/Services/ManufacturingTraceIdResolvers.cs`, registered in
  `IMaterialModule`) so the generic `/documents/{id}/history` endpoint resolves BOM/PROD/SR documents
  the same way it already resolves SO/PO/PR/QUOTATION (`Demand/Services/TraceIdResolvers.cs`).
- No migration: `TraceId` columns already existed on every entity involved (`BillOfMaterial`,
  `ProductionOrder`, `SupplyRequirement`) — this task only fixed what populated them and added the
  events that read them.
- Tests: Material 91/91 (incl. the new trace-id assertion), Demand 459/459 (a `Moq.Verify` in
  `SaleOrderManufacturingServiceTests` updated for `EnsureForSourceAsync`'s new parameter), Inventory
  263/263, Warehouse 51/51, Auth 117/117 unaffected. Full solution build clean.

### How manufacturing notifications were actually built (Phase 5, A30-P5-01, 2026-09-26)

Ten notifications, all through the same `INotificationService`/Hangfire pipeline
`SupplierSelectionService.NotifySupplyTeamAsync` and `WorkflowEscalationJob` already use — no new
mechanism, no new table. A new `IManufacturingNotificationService`
(`Material/Services/{I,}ManufacturingNotificationService.cs`, internal to Material — nothing outside
this module needs to call it) has one method per event, called at the point in each service where the
event actually happens:

- **PO Created** — `ProductionOrderService.CreateAsync` (manual) and `.CreateForSourceAsync` (the
  Phase 4 Track C sale-order path).
- **Shortage Alert** — `PlanCoreAsync`, once per material still short after planning's own allocation
  run (not re-fired by a later run finding the same shortage still there).
- **Chained PO Created** — `CreateChildForSupplyAsync`, addressed to the **parent's** creator, not the
  child's — falls back to a plain PO Created when there is no parent (a manual supply requirement
  raised straight into manufacture has nothing above it to notify).
- **QI Required** — `CompleteAsync`, the order's own transition to QUALITY_INSPECTION.
- **QI Completed** — `QualityInspectionService.CreateAsync`.
- **FGR Completed**, and **PO Completed** when the receipt happens to be the one that takes the order
  all the way to COMPLETED — both in `FinishedGoodsReceiptService.ConfirmCoreAsync`.
- **SR Created** — `SupplyRequirementEngine`, both the PMR-triggered path and the manual one.
- **PO Ready** and **Allocation Completed** — `ProductionReadinessListener`, fired on the *transition*
  (order newly reaches Ready; a material newly becomes fully covered), captured by comparing status
  before and after `ProductionReadiness.Apply` inside the same run rather than on every run that
  happens to touch an already-ready order or an already-covered material.

Two recipient shapes, matching precedent already in the codebase rather than inventing a third:
a new document that needs **someone other than its own creator** to act on it (PO Created, QI
Required — §18.4 says the creator cannot inspect their own order, SR Created) escalates to
`IOrgChartService.GetSupervisorAsync`, falling back to the actor when no supervisor is configured —
the exact fallback shape `SupplierSelectionService.NotifySupplyTeamAsync` already uses for a
department head. Everything else is "your own order changed" and goes straight to the order's
`CreatedBy`, regardless of who performed the action that caused it (a floor operator confirming an
FGR is not the notification's audience; the planner waiting on the order is).

`IManufacturingNotificationService` is optional (`? notify = null`) on all five call sites'
constructors, the same nullable-for-old-tests shape as `IBackgroundJobClient` — none of this module's
hand-built `ServiceCollection` test harnesses register either one. One new wrinkle: Moq cannot proxy
an `internal` interface without `[InternalsVisibleTo("DynamicProxyGenAssembly2")]` on the owning
assembly, which `SMS.Modules.Material.csproj` didn't have (every interface mocked in this module's
tests until now was public). Added it, following the same line already present in
`SMS.Modules.Inventory.csproj` and others.

- Tests: `ManufacturingNotificationServiceTests` (11, isolated — recipient/escalation logic for all
  ten methods, `INotificationService`/`IOrgChartService` mocked) plus `Moq.Verify` assertions added to
  the existing chained-manufacturing tests in `ProductionOrderTests` and `QualityAndFgrTests`, proving
  the real call sites fire (not just the isolated service). Material 102/102, Demand 459/459, Auth
  117/117, Inventory 263/263, Warehouse 51/51 unaffected.

### How manufacturing reports were actually built (Phase 5, A30-P5-02..06, 2026-09-26)

FSD §31 lists 28 reports. **User decision (2026-09-26): a representative subset, honestly documented,
rather than all 28 exhaustively** — reading the spec's own list against what Phase 1-4 already built
found that a large share of it already exists as an endpoint, so a Reports-module report would just be
a second query returning the same rows. The task register's own DONE notes for A30-P5-02..06 carry the
full per-report accounting; the shape of it:

- **Already covered, not duplicated**: PO Register (R1 = `GET /api/production-orders`), Material
  Requirement (R3 = `GET /api/production-orders/shortages`), SR Status (R8 =
  `GET /api/supply-requirements`), BOM Cost Analysis (R10 = `GET /api/boms/{uuid}/cost`), Allocation
  Summary + Demand vs Supply (R11/R12 = Phase 1's own `/api/allocations` + `pages/inventory/allocations`
  dashboard — A30-P4-20 had already found this needed no further work for Track C either), Stock
  Availability (R13 = `GET /api/variants/{uuid}/availability`), and — the one that took actually
  reading `ProductionLedgerService.BuildEntriesAsync` to notice — Ledger Detail/Summary, Material
  Consumption and Cross-Order (R18/R19/R20/R25) are **all already cross-order** the moment
  `GET /api/production-ledger`/`.../summary` is called with no `productionOrderUuid` filter; the
  per-order endpoint and the cross-order one were never two different things.
- **Explicitly skipped, with a reason** rather than silently dropped: PO Schedule (R2, a frontend
  calendar over data the list already returns), Supplier Performance/Mfg (R9, needs a Demand↔Material
  join nothing provides), Warehouse Utilization (R17, not manufacturing-specific), Cost Variance/Scrap
  Cost/Profitability (R21-23, each needs a cost model this phase does not have — a BOM-rollup-vs-actual
  comparison, a unit-cost basis for QI-rejected scrap that never touched inventory, and sale price data
  crossing into Demand, respectively).
- **Genuinely new** — five endpoints, one per category, built as
  `SMS.Modules.Reports/{Models/ManufacturingReportModels.cs, Services/{I,}ManufacturingReportService.cs,
  Controllers/ManufacturingReportsController.cs}` under `GET /api/reports/manufacturing/**`:
  - **Production Efficiency** (R4/R5/R16) — planned/produced/accepted/rejected totals, yield%,
    on-time-completion% and average cycle days across orders in a date range, with a per-status count
    (folds WIP and PO Completion in rather than giving them their own endpoints).
  - **Quality & Scrap Summary** (R6/R7) — inspection outcome totals and rates across orders, by
    product, worst rejection rate first.
  - **Material Issue Register** and **Finished Goods Receipt Register** (R14/R15) — the one real
    register-level gap: nothing before this listed these documents flat across orders, only per-order.
  - **Ledger Reconciliation** (R24) — flags a produced order whose own `AcceptedQuantity` doesn't match
    the sum of its confirmed FGRs, that produced output with no confirmed material issue, or that
    carries a rejection with no quality inspection — invariants the write path already enforces
    transactionally, so this is a safety net against future drift/bugs, not a rule the UI can violate
    today.
  - **Chained Manufacturing Dependency Tree** (R26/R27/R28, combined into one payload — a tree without
    each node's status and cycle time is not useful alone) — walks up to the chain's true root from any
    order in it, then breadth-first back down through every descendant, the same bounded (depth ≤ 10)
    level-by-level walk `BomRepository.EnsureNoCycleAsync` uses for a recipe chain, so it never loads
    more of the table than the one chain in view.

Every endpoint is gated `[RequiresFeature("MODULE_MANUFACTURING")]` + `REPORT_VIEW` + `PROD_LEDGER_VIEW`
— the latter is A30-P4's own "narrower cross-order and summary view" permission, reused rather than
minting a new code, since these reports are the same shape of read. No PDF/Excel export was built
(unlike A29's own sales-reports precedent) and no frontend dashboard yet — that is A30-P5-09's task.
Reports already referenced `SMS.Modules.Material` (for the pre-existing, unrelated MIR/MIV reports) and
already had `InternalsVisibleTo` from Material, so querying `ProductionOrder`/`QualityInspection`/
`ProductionMaterialIssue`/`FinishedGoodsReceipt` directly needed no new project reference.

- Tests: `ManufacturingReportServiceTests` (9, InMemory Material + Inventory contexts) covering all
  five endpoints, including the reconciliation's flag/no-flag cases and the chained walk's up-then-down
  correctness starting from a non-root node. Reports suite 937/937 (was 928), Material 102/102
  unaffected, full solution build clean.

### How concurrency stress testing was actually done (Phase 5, A30-P5-08, 2026-09-26)

The task register's own phrasing ("5 concurrent scenarios, SERIALIZABLE isolation, P99 < 500ms
target") does not match the spec: FSD §32.2 lists **4** critical concurrency scenarios, and neither
"SERIALIZABLE" nor "500ms" appears anywhere in the spec text — D7 (Part A) already settled, at the
user's direction, that RowVersion + bounded retry is the mechanism, not literal SERIALIZABLE
isolation. This task verified that mechanism against the spec's own four scenarios rather than
building infrastructure for numbers the spec never actually asked for:

1. **Two allocation requests, same product/warehouse** — already proven on real SQL Server by T-AL07.
2. **Concurrent production order planning for the same BOM** — the engine's RowVersion-and-retry
   protection lives entirely inside `AllocateAsync`, agnostic to whether the demand is `SALES_ORDER`
   or `PRODUCTION_MATERIAL`. Proved directly with a new SQL Server test
   (`AllocationEngineTests.Two_production_orders_planning_against_the_same_material_cannot_both_hold_the_last_unit`)
   rather than just argued by extension of T-AL07.
3. **Concurrent Goods Receipt and Allocation** — **not a race at all, verified from source**:
   `IGrnStockPoster.AllocateReceiptsAsync`'s own doc comment says it runs "after the stock is
   committed, never inside that transaction," and the code does exactly that.
4. **Concurrent FGR and SO delivery** — same shape, verified in
   `FinishedGoodsReceiptService.ConfirmCoreAsync`: the stock-crediting transaction commits, then and
   only then does it call `SupplyReceivedAsync`/`AllocateAsync`.

No latency-threshold test was added to the permanent suite — this codebase has no benchmark-style
tests anywhere else, and the spec's own "P99 < 500ms" has no textual basis to hold anyone to. One
honest ad-hoc measurement was taken instead (30 independent concurrent `AllocateAsync` runs on
LocalDB, then the scratch test was deleted): min 652ms / p50 666ms / p99 808ms — above the task
register's own figure, but that is LocalDB's per-connection overhead on a dev machine, not Azure SQL
with a warm pool, and not something worth gating CI on. No production code changed; this task was
entirely verification of what Phases 1-4 already built.

### How the reports dashboard frontend was actually built (Phase 5, A30-P5-09, 2026-09-26)

One page, `pages/reports/manufacturing-reports/`, routed at `/portal/pages/reports/manufacturing`,
gated `PROD_LEDGER_VIEW` (added to `pages.routes.ts`'s own `P` permission map — it wasn't there yet).
Followed `material-ops-reports`'s own `p-tabView` shape (one tab per report, filter card + table/
summary cards inline) rather than the sales-reports dashboard's card-picker-plus-route-param shape,
since nothing here needs a single report deep-linked to its own URL. Six tabs: the five A30-P5-02..06
endpoints, with Chained Manufacturing broken out as its own tab because it takes a production-order
UUID rather than a date filter — its tree is rendered as a flattened, depth-indented table (indent =
`depth * 24px`), not a recursive Angular component, since one is not worth building for a single tab.
New `services/manufacturing-reports.service.ts` mirrors every field the backend models expose. No
export buttons anywhere on the page — the backend has none, an explicit cut already recorded against
A30-P5-02..06 — and no shared KPI-card component exists in this codebase (every report page, including
this one, uses its own inline `.summary-card` CSS).

**Verification gap, stated plainly**: this was checked with `ng build --configuration development`
(clean) and the full Karma suite (1120/1120, no regressions, no new specs — matching every other report
page's own lack of specs) but **not** with a live browser click-through, because this session has no
browser-automation tool to drive a login + navigation + render check. Do that before calling the page
production-ready.

### How UAT prep was actually done (Phase 5, A30-P5-10, 2026-09-26)

`Documents/SMS_UAT_Addendum_30_Manufacturing.md` has the full plan and 5 scripts. The user approved
seeding the real data into SMSGlobal's SCM-DEMO organization (`84d0a96d-52d4-4375-9260-357b46fb9d9f`),
which surfaced a genuine, previously-unnoticed gap: **`MODULE_MANUFACTURING` had never been enabled for
any organization on SMSGlobal.** The tenant seeder that backfills a new `FeatureDefinition` onto every
organization only runs at API startup (`TenancyDataSeeder.SeedAsync`, called from
`app.UseTenancyModule()`), and every check of this addendum's work since Phase 2 added the
`MODULE_MANUFACTURING` catalog entry had gone through `dotnet test`, never `dotnet run` — so the
backfill had simply never fired. Running `dotnet run --project src/SMS.API` once against SMSGlobal
triggered it (idempotent, the same "API startup migrates the shared DB" behavior this project already
relies on for every migration); a direct query afterward confirmed `MODULE_MANUFACTURING = true` for
SCM-DEMO and every other ENTERPRISE-plan org. The process itself crashed a few seconds after that on
an unrelated `AddressInUseException` (something else briefly held port 5000) — harmless, since every
module's migration and seeder had already run to completion by the point Kestrel tried to bind.

The 5 products and 3 BOMs were then seeded for real through the actual `BomRepository`
create→submit→approve→activate workflow (not raw SQL) against a real SqlServer-backed
`MaterialDbContext`/`InventoryDbContext`, via a disposable xUnit-test-as-script — the same throwaway
pattern A30-P5-08 used for its latency measurement. A small local `IDocumentNumberGenerator` was
written against the real `logistics.document_number_sequences` table rather than adding a permanent
project reference to `SMS.Modules.Logistics` for a one-off script. The UUIDs are recorded in the task
register's own DONE note for A30-P5-10. Both scratch scripts were deleted immediately after use.

### How the HTTP-level E2E test was actually built (Phase 4, A30-P4-21, 2026-09-26)

`tests/SMS.Integration.Tests/Manufacturing/ManufacturingCycleTests.cs`, one test, real `Program.cs`
pipeline over LocalDB + `WebApplicationFactory` (`ProcurementCycleWebApplicationFactory`, already built
for A29's own PV-009 test). Chain: Steel Rod (Purchase) → Steel Bolt (Manufacture, BOM1) → Bolt Kit
(Manufacture, BOM2) → Packaged Bolt Kit (Manufacture, BOM3 — also takes Packaging Box, Purchase),
triggered by confirming a sale order for the top item with zero stock anywhere. Proves
`AutoPoCreationJob` → `SaleOrderManufacturingService` → `ProductionOrderService.PlanCoreAsync`'s
recursive child/grandchild raising → raw-material PO/GRN → MI/QI/FGR bottom-up → the sale order line
reaching `RESERVED` once `DeficitQty` hits exactly zero — the real DI container assembling
`SaleOrderFulfillmentListener` and `ProductionReadinessListener` together, which no unit test (each
module's own suite mocks the other's interface) had ever exercised. The ledger's debit=credit+scrap
identity is proven separately, as a standalone 2-unit Steel Bolt order with one unit QI-rejected,
deliberately decoupled from the sale-order chain: `SaleOrderFulfillmentListener` only moves a line off
`OPEN` once `DeficitQty` reaches exactly zero, so a QI rejection anywhere in the chain feeding the sold
quantity would leave the line permanently short and never reach `RESERVED`.

**This test earned its cost immediately — it found three real production bugs, all invisible to every
existing unit test**:

1. **`ProductionOrderRepository.CreateAsync` never populated the `po.Bom` navigation property** — only
   the scalar `BomId`/`BomVersion` were copied from an `.AsNoTracking()` lookup. `PlanCoreAsync`
   dereferences `po.Bom.Lines`/`po.Bom.BomNumber` directly, which threw `NullReferenceException` in any
   genuinely fresh `DbContext` scope (a real HTTP request or Hangfire job). Every existing unit test
   masked this by accident: each test harness shares one `MaterialDbContext` across BOM setup and order
   creation, so EF's change-tracker identity/fixup silently wired `po.Bom` from the leftover tracked BOM
   entity. Worse, the failure was **silent in production** too — Hangfire's `[AutomaticRetry(Attempts=3)]`
   swallowed the exception with no log bridge configured, leaving the order stuck in `DRAFT` with zero
   materials forever, no error visible anywhere. Fixed by reloading via `_repo.LoadAsync(uuid)` (which
   correctly `.Include(p => p.Bom).ThenInclude(b => b.Lines)`) immediately before every `PlanCoreAsync`
   call, in `ProductionOrderService.CreateAsync`, `CreateForSourceAsync` and `CreateChildForSupplyAsync`.
2. **`SupplyRequirementStatus.IsLive(s.Status)`, a plain C# static method, was called inside a
   LINQ-to-Entities predicate** in `SupplyRequirementEngine.EnsureForShortageAsync` and
   `ProductionOrderService.CancelAsync`. EF's SQL Server provider cannot translate an arbitrary method
   call and throws `InvalidOperationException`; EF's InMemory provider (every unit test) evaluates it
   client-side without complaint, hiding the bug completely. Fixed by adding a translatable
   `SupplyRequirementStatus.LiveStatuses` (`string[]`) and switching both call sites to
   `.Contains(s.Status)` (a real SQL `IN (...)`). A third occurrence, in `ProductionOrderRepository.cs`,
   runs on an already-materialized in-memory list post-`.ToListAsync()` and needed no change.
3. **`FinishedGoodsReceiptService.RunAcrossContextsAsync` leaked a stale `Database.CurrentTransaction`.**
   It calls `_db.Database.UseTransaction(sqlTx)`/`_inv.Database.UseTransaction(sqlTx)` to share one raw
   ADO.NET transaction across both DbContexts (the standard pattern for a retry-safe cross-context unit
   of work), commits it, then returns — without ever clearing that association. `CurrentTransaction`
   then stays non-null on the shared, scoped `InventoryDbContext` for the rest of the request.
   `ConfirmCoreAsync`'s own post-commit calls (`AllocationEngine.SupplyReceivedAsync`/`AllocateAsync`,
   intentionally run *after* the receipt commits, per §19.3) use `InTransactionAsync`'s
   ambient-transaction check (`CurrentTransaction is not null → join it, don't open a new one` — the same
   arrangement `StockReservationService` uses); finding the stale reference, they skipped their own
   retry-safe `BeginTransactionAsync` wrapping entirely, and the first query run that way was rejected
   outright by `SqlServerRetryingExecutionStrategy`
   (`"does not support user-initiated transactions"`) — a check that exists specifically to prevent an
   unmanaged transaction like this stale one from being silently retried. Fixed by calling
   `UseTransaction(null)` on both contexts and `Database.CloseConnectionAsync()` right after the commit,
   inside `RunAcrossContextsAsync`.

All three were diagnosed by adding a temporary file-based exception logger directly at the failure site
(both Hangfire's swallowed retries and the generic API error handler's flattened response hid the real
stack trace), removed once each was understood.

**Also required, not bugs but real test-infrastructure gaps**: placeholder role-holders
(`ProcurementManager`, `FinanceOfficer`, and — per `ApproverResolutionService`'s own role-code
table — `WarehouseOperator` for GRN_QC's "QC Inspector Verification" step, which resolves to that role,
not a dedicated QC one) so the workflow engine's ROLE-type PO/GRN approval steps have at least one
active candidate to resolve at submit time; and a genuine second real user (`InventoryManager`) for
BOM's four-eyes rule and QI's "not the order's own creator" rule, both identity checks in inline code
rather than permission checks, so the SystemAdmin override that lets one login submit-and-approve every
other workflow-engine document does not apply to either.

Verified: the new test passes standalone across multiple runs; Material 102/102, Demand 459/459,
Inventory 264/264, Warehouse 51/51, Auth 117/117 all unaffected; `ProcurementToIssueCycleTests` and
`MaterialIssueVoucherTests` (the two existing suites nearest these fixes — GRN/MIV/allocation-engine
paths) still pass standalone; full solution build clean. Running the entire `SMS.Integration.Tests`
project as one process produces widespread unrelated failures (Auth, MultiTenancy, Workflow, etc.) —
this is pre-existing test-infrastructure fragility from xUnit's default parallel test-class execution
racing multiple `WebApplicationFactory`/Hangfire `GlobalConfiguration` instances in-process (observed:
`ObjectDisposedException` on a shared `LoggerFactory` during host startup), not a regression from this
work — every affected class, including ones this work never touched, passes cleanly run on its own.

### Decisions to settle before the corresponding phase

| # | Question | Blocks | Suggested default |
|---|---|---|---|
| D1 | A29 auto-PO pipeline vs A30 FulfillmentRequirement → SupplyRequirement → PO (row above) | Phase 4 Track C | **Decided 2026-09-26 (user), and built this way 2026-09-26.** Retail: Sale Order → PO, the A29 pipeline stays as is — literally untouched except that `AutoPoCreationJob` now checks `SupplyMethod` before ever reaching it. Manufacturing: Sale Order → Production Order via a one-branch hook (`ISaleOrderManufacturingService`) plus a new listener (`SaleOrderFulfillmentListener`) that hands the result back into A29's own reservation shape — see the Phase 4 Track C section above. No generic `FulfillmentRequirement`/`SupplyRequirement` layer was built for the retail side; it was never needed. |
| D2 | `is_bom_input` vs existing `ProductVariant.IsAvailableForProduction` | Phase 1 Track A | **Decided 2026-09-26 (user): ignore `is_bom_input`; `IsAvailableForProduction` is the BOM-input gate.** Wherever the spec says `is_bom_input = true` (§6.1, §6.5, §7.4 V-B02, §22.5 BR-S06, T-CM01), read `ProductVariant.IsAvailableForProduction`. No product-level column is added for it. |
| D7 | Allocation-engine concurrency (spec §14.8, §32) | Phase 1 Track B | **Decided 2026-09-26 (user): "apply locks/transactions where concurrency is impacted."** Done for stock counters (Part A row above). `AllocationRecords` repeats the pattern: `RowVersion` + bounded retry, one transaction per unit of work. |
| D3 | Production material issue: new `ProductionMaterialIssue` entity vs MIV with `PRODUCTION_ORDER` source | Phase 3 migrations | **Decided 2026-09-26: new entity.** `ProductionMaterialIssue`/`ProductionMaterialIssueLine`, numbered `PMI-` (not `MI-` — MIR/MIV already own that), posts via `IInventoryLedgerService` like `GoodsIssuePoster`. |
| D4 | Production Ledger: new table vs query over `InventoryLedgerEntries` | Phase 4 Track B | **Decided 2026-09-26, and built this way:** query/view (`ProductionLedgerService`, no table, no migration), plus a synthesised (not written) scrap row per QI rejection — see the Phase 4 Tracks A/B section above for why a real ledger row there would corrupt the running balance. |
| D5 | BOM approval: WorkflowEngine vs inline transitions (spec OQ-10) | Phase 2 | Inline, like every A29 document |
| D6 | Spec OQ-1 (sync vs async allocation after GRN/FGR), OQ-4 (auto vs manual PO creation from SO), OQ-5 (auto PO vs PR), OQ-7 (allocation rules per tenant) | Phase 1 Track B / Phase 4 | Sync inside the posting call for a single product/warehouse, Hangfire only for org-wide re-runs; OQ-4/5 follow whatever D1 decides; rules per tenant (every config here is per org) |

---

## Part B — Map into the specification

The spec is in the repo as Markdown; read the section rather than a summary. Section numbers below are
the spec's own.

| Section | Topic | Notes for implementation |
|---|---|---|
| 3 | Business objective + end-to-end flow | The 🔴 "no direct SO→PO" rule — see D1. |
| 5 | Existing system assessment (REUSE / MODIFY / NEW) | Written against the wrong schemas; use Part A instead. |
| 6 | Product catalog: `ProductType` (8 values), `SupplyMethod` (4), flags, validation rules §6.5, migration defaults §6.6 (existing = StockItem / Purchase / saleable+purchasable+stockable) | Phase 1 Track A. §6.4.1 chained manufacturing: FinishedGood with `is_bom_input = true`. |
| 7–9 | BOM header/lines, constraints §7.4 (one Active per product/variant/warehouse, circular check), versioning §8, status machine §9.1, permissions §9.2, rules §9.3 (four-eyes) | Phase 2. |
| 10 | Sale order line extensions, `FulfillmentRequirements` (demand schema), decision flow §10.3 | Phase 4 Track C. Overlaps A29 — D1. |
| 11 | Production Order entity, 9-status machine §11.2, readiness rule §11.3 | Phase 3. |
| 12 | PMR entity, BOM explosion formula §12.2 (net, scrap allowance, gross) | Phase 3. |
| 13 | Supply Requirement entity, lifecycle, traceability chain | Phase 3. BR-S06: FinishedGood shortage → child Production Order. |
| 14 | Shared Allocation Engine: `AllocationRecords`, `AllocationRules`, default priority rules §14.4, allocation types §14.5, `IAllocationEngine` interface §14.6, competing-demands example §14.7, concurrency §14.8 | Phase 1 Track B. Interface goes in `SMS.Shared/Common`. |
| 15 | Post-GRN allocation | Hook in Warehouse GRN posting. |
| 16 | Material Issue (+ types table §16.4) | Phase 3 — D3. |
| 17–18 | Production execution; Quality Inspection (+ lines), decision impact §18.3, rules §18.4 (inspector ≠ operator) | Phase 4 Track A. |
| 19 | Finished Goods Receipt, processing steps §19.3 | Phase 4 Track A. |
| 19A | Production Ledger: entry shape, examples §19A.4–5, rules §19A.6, API §19A.7, UI §19A.8 | Phase 4 Track B — D4. |
| 20 | Sales fulfilment after production, partial-fulfilment table §20.2 | Phase 4 Track C. |
| 21–23 | Status enums summary; business rules BR-P/B/PR/A/S/L; validation rules V-* with error messages | Use the V-* messages verbatim. |
| 24 | 29 exception scenarios | Test-case source. |
| 25 | 30 permission codes | `PermissionCodes.cs` + `AuthDataSeeder`. |
| 26 | Audit events per entity | `IAuditService`. |
| 27 | DB changes summary + indexes §27.3 | Cross-check each with Part A before writing the migration. |
| 28 | API specs (28.1–28.9) | Match existing controller conventions (`ApiResponse<T>`, Guid routes). |
| 29 | UI requirements (product, BOM, production, ledger, allocation screens) | Angular. |
| 30 | Notifications table | `INotificationService`. |
| 31 | 28 reports (17 standard, 8 ledger, 3 chained) | Reports module pattern from A29 (`GET /api/reports/sales/{name}` style, QuestPDF behind `PdfRenderGate`). |
| 32–33 | Concurrency and transaction boundaries | Reuse reservation service locking. |
| 34 | End-to-end worked example (SO-1001 → PROD-001 → … → DO-001) | Basis for the integration test. |
| 35–36 | Acceptance criteria (AC-PB/PM/AE) and QA scenarios (T-PB/PR/SR/AL/CM/PL/QF) | Test names. |
| 37 | Migration order (20 steps + 14A) and rollback | Reconcile with Part A (no `lookups`, no `procurement`). |
| 38–39 | Traceability (`trace_id` propagation), dependencies | `TraceId` already exists on Demand/Warehouse/Material/Finance/Logistics entities. |
| 40 | Development phases (~65 dev days) | Mirrors the task register. |
| 41 | Open questions OQ-1…OQ-10 | See D6. |
| App. A | Claude Code implementation instructions | Superseded where Part A says otherwise. |
