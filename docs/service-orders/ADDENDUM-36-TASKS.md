# Addendum 36 — task register (mapped) & status

Owners: **CAT** catalog backend · **SVC** service-order backend · **DEM** sale-order integration · **FE1** product/BOM UI · **FE2** service-order UI · **QA** end-to-end · **COORD** coordinator.
Status: ☐ open · ◐ in progress · ☑ done · ✕ dropped (reason). Mapping per [ADDENDUM-36-ANALYSIS.md](ADDENDUM-36-ANALYSIS.md).

| Task | Owner | Mapped to | Status |
|---|---|---|---|
| A36-P1-01 M1 product service fields | CAT | Inventory migration on `Product` (D-2) — `A36_ProductServiceFields` (guarded, additive) | ☑ |
| A36-P1-02 entity/DTO | CAT | string codes, not byte enums — `ServiceInvoicingPolicy` / `ServiceBillingModel` in SMS.Shared.Common | ☑ |
| A36-P1-03 SVC-P rules | CAT | SVC-P-05 → warning only (D-3); SVC-P-06 → default variant SellingPrice — `ServiceProductRules` | ☑ |
| A36-P1-04 product API | CAT | existing product endpoints (POST, PATCH; explicit null clears) + `hasActiveServiceBom` via Material's `IBomStructureReader` | ☑ |
| A36-P1-05 UI service config section | FE1 | product create/detail — shared `service-settings-fields` (create/edit page + detail edit dialog), detail view + "no active service BOM" note; specs green | ☑ |
| A36-P1-06 M2 BOM line source type | CAT | Material migration, `SubcontractSupplierUuid` Guid, no FK — `A36_BomLineSourceType` (existing lines STOCK) | ☑ |
| A36-P1-07 BOM line entity/DTO | CAT | `sourceType`, `subcontractSupplierUuid`, read `subcontractSupplierName` | ☑ |
| A36-P1-08 BOM eligibility + SVC-BOM rules | CAT | `CatalogProductReader` + `BomRepository`; `/api/boms` open for MODULE_MANUFACTURING **or** MODULE_SERVICES | ☑ |
| A36-P1-09 UI service BOM section | FE1 | product's Bill of Materials tab (A31 inline BOM) — tab also for SERVICE + hasServiceBom; Source / vendor picker / Hours on service BOM lines; specs green | ☑ |
| A36-P1-10 phase 1 tests | CAT | unit tests — ServiceProductTests, ServiceBomTests, ServiceOrderPermissionSeedTests, ServiceOrdersFeatureTests | ☑ |
| A36-P2-01 M3 ServiceOrders | SVC | Material migration (D-5) — `A36_ServiceOrders` (5 new tables, additive) | ☑ |
| A36-P2-02 entity | SVC | `Domain/ServiceOrderEntities.cs` + `ServiceOrderMaps` (RowVersion, tenant filter, FSD §4.3 indexes) | ☑ |
| A36-P2-03 CRUD service | SVC | `IDocumentNumberGenerator` "SVC" (M9 ✕) — `ServiceOrderService` | ☑ |
| A36-P2-04 M4 SMRs | SVC | in `A36_ServiceOrders` | ☑ |
| A36-P2-05 SMR entity | SVC | `ServiceMaterialRequirement` | ☑ |
| A36-P2-06 BOM explosion | SVC | allocation demands SERVICE_ORDER, SR for subcontract (D-6) — `ISupplyRequirementEngine.EnsureForServiceAsync` | ☑ |
| A36-P2-07 state machine | SVC | string statuses, direct calls (D-17) | ☑ |
| A36-P2-08 readiness | SVC | `IAllocationRunListener` + SR listener — `ServiceReadiness` / `ServiceReadinessListener` | ☑ |
| A36-P2-09 API 1–4 | SVC | `api/service-orders` — `ServiceOrdersController` | ☑ |
| A36-P2-10 API 5–8 | SVC | + `/close` (D-13) | ☑ |
| A36-P2-11 API 9–11 | SVC | + `/materials/allocate` | ☑ |
| A36-P2-12 UI list | FE2 | | ☑ |
| A36-P2-13 UI detail (details, materials) | FE2 | SMS Flow object page | ☑ |
| A36-P2-14 phase 2 tests | SVC | `ServiceOrderTests` (28, service level) | ☑ |
| A36-P3-01 M5 MaterialIssues | — | ✕ replaced by new ServiceMaterialIssues (D-7) | ✕ |
| A36-P3-02 M6 MaterialIssueLines | — | ✕ replaced (D-7) | ✕ |
| A36-P3-03 MI entity polymorphic | SVC | new `ServiceMaterialIssue(Line)` entities + migration | ☑ |
| A36-P3-04 MI service for services | SVC | `IInventoryLedgerService` SERVICE_ISSUE, `ConsumeAsync` (shared `MaterialStockMovements`) | ☑ |
| A36-P3-05 ad-hoc materials | SVC | | ☑ |
| A36-P3-06 completion | SVC | | ☑ |
| A36-P3-07 material return | SVC | SERVICE_RETURN | ☑ |
| A36-P3-08 cancellation | SVC | auto-return issued (D-16) | ☑ |
| A36-P3-09 UI completion | FE2 | | ☑ |
| A36-P3-10 UI ad-hoc panel | FE2 | | ☑ |
| A36-P3-11 phase 3 tests | SVC | XOR-constraint tests ✕ (no polymorphic table) | ☑ |
| A36-P4-01 M7 ledger | SVC | in `A36_ServiceOrders` | ☑ |
| A36-P4-02 ledger entity | SVC | `ServiceLedgerEntry` (immutable — DbContext refuses update/delete) | ☑ |
| A36-P4-03 ledger service | SVC | written in completion's transaction | ☑ |
| A36-P4-04 ledger API | SVC | | ☑ |
| A36-P4-05 UI ledger | FE2 | | ☑ |
| A36-P4-06 UI timeline | FE2 | existing timeline panel | ☑ |
| A36-P4-07 phase 4 tests | SVC | | ☑ |
| A36-P5-01 M8 FulfillmentRequirements | — | ✕ table does not exist (D-10) | ✕ |
| A36-P5-02 M9 SVC sequence | — | ✕ `IDocumentNumberGenerator` | ✕ |
| A36-P5-03 M10 backfill | — | ✕ no polymorphic table | ✕ |
| A36-P5-04 FulfillmentMethod.Service | — | ✕ → route-exempt service lines (D-10) | ✕ |
| A36-P5-05 fulfilment routing | DEM | confirm → `IServiceOrderDemandService`. Line `FulfillmentMode = SERVICE` (set from product type at build + re-checked at confirm, no migration); route-exempt, no reserve/allocation/auto-PO/delivery (Logistics creator + manual create filter it too); Ensure under the SO lock after confirm when `MODULE_SERVICES` on; failure retried idempotently on the next detail load (lines with no service order at all) | ☑ |
| A36-P5-06 mixed lines + indicators | DEM | `isService`, `serviceOrders[]`, fulfilment counting; cancel cascade + `SO_SERVICE_ORDERS_*` timeline; tests `A36ServiceLineTests` (11) | ☑ |
| A36-P5-07 UI SO service indicators | FE2 | sale order detail | ☑ |
| A36-P5-08 SO update on completion | DEM+SVC | `ISaleOrderServiceFulfillmentListener` — DEM side ☑ (`SaleOrderServiceFulfillmentListener`, registered; resolves `IServiceOrderDemandService` lazily to avoid a DI cycle); SVC side ☑ (called after commit on COMPLETED/CLOSED/CANCELLED, failures logged; not on the SO-cancel cascade) | ☑ |
| A36-P5-09 UI dashboard | FE2 | | ☑ |
| A36-P5-10 E2E tests | QA | LocalDB HTTP tests — `tests/SMS.Integration.Tests/ServiceOrders/ServiceOrdersE2ETests` (9 green). Found + fixed: `/materials/allocate` refused IN_PROGRESS, so stock reserved for a job an outside allocation run resumed (WAITING → IN_PROGRESS) could never be issued; now allowed IN_PROGRESS (ServiceOrderService, detail page RESERVABLE, contract note 7, ServiceOrderTests regression) | ☑ |
| A36-X-01 permissions | CAT | D-12 + feature `MODULE_SERVICES` (D-11) | ☑ |
| A36-X-02 DI | each owner | CAT/SVC/DEM registrations; verified by QA's HTTP host + full regression (FE 2457, Material 215, Demand 945, Inventory 471, Auth 300, Tenancy 110, Logistics 2202, Reports 962, 19 integration classes) | ☑ |
| A36-X-03 allocation handler | SVC | `IAllocationRunListener` (no handler registry exists) | ☑ |
| Shared contracts | COORD | `IServiceOrderDemandService`, `ISaleOrderServiceFulfillmentListener` | ☑ |
