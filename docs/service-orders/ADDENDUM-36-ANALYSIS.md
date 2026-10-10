# Addendum 36 — Service Orders: reality check & decisions

Spec: SMS-FSD-ADD-036 v1.0 · Register: SMS-TR-ADD-036 (58 tasks) · Contract: [API-CONTRACT.md](API-CONTRACT.md) · Tasks: [ADDENDUM-36-TASKS.md](ADDENDUM-36-TASKS.md)

## Part A — spec vs. code

The FSD is written against a generic model (BIGINT ids, snake_case, numeric enums, React, MediatR, one shared
MaterialIssues table). The real system differs; every task is mapped onto what exists.

| Spec assumes | Reality | Consequence |
|---|---|---|
| BIGINT ids, numeric enums, `org_id` | `int Id` + `Guid UUID`, `Guid OrganizationId`, **string codes** (`"IN_PROGRESS"`) | All new entities follow the real convention (D-1) |
| FKs across schemas (Products, Users, Partners) | Cross-module references are **Guid scalars, no FK** (validated in services) | No cross-module FKs (D-1) |
| `lookups.Products` | `inventory.Products` (Inventory module); stock unit is the **variant** | Service fields on Inventory `Product` (D-2); orders always name a variant (D-5) |
| `ProductType.Service = 6` | `ProductType.Service = "SERVICE"` exists, CanBeBomInput, not stockable | Reused |
| `AllocationDemandType.ServiceOrder = 3`, DemandSourceType 3 | `"SERVICE_ORDER"` exists in both | Reused; no engine change. Readiness via `IAllocationRunListener` (production's pattern) — there is no "demand handler" registry (X-03 ⇒ listener) |
| `material.MaterialIssues` polymorphic (M5/M6/M10) | Only `ProductionMaterialIssue(Line)` — production-specific | **New `ServiceMaterialIssue(Line)`**; M5, M6, M10 dropped (D-7) |
| `demand.FulfillmentRequirements`, `FulfillmentMethod` enum (C9, M8) | Do not exist — fulfilment = A33/A34 routes + auto deliveries on confirm | Service lines are **route-exempt** (like DROP_SHIP: no route, no blocker, no delivery) and raise service orders through a shared interface (D-10); M8 dropped |
| `document_number_sequences` (M9) | `IDocumentNumberGenerator.NextAsync(prefix)` | `SVC-2026-00001`; M9 dropped |
| MediatR events | Not used for domain flow | Direct calls inside the same transaction (D-17) |
| React | Angular 19 + SMS Flow page kit (`sf-*`) | Screens per `src/app/shared/flow/README.md` (D-15) |
| Roles SERVICE_MANAGER / TECHNICIAN / SALES | Do not exist | Mapped to existing built-in roles (D-12) |

Migrations kept: M1 (Inventory), M2/M3/M4/M7 (Material) + new ServiceMaterialIssues. Dropped: M5, M6, M8, M9, M10.

## Part B — decisions (adopted defaults)

- **D-1 Conventions.** int Id + Guid UUID, OrganizationId (tenant filter), string status/type codes, Guid cross-module refs without FKs, RowVersion on ServiceOrders. Migrations per module DbContext, additive only (startup migrates the shared DB — never destructive).
- **D-2 Product service fields** (Inventory `Product`): `ServiceInvoicingPolicy` string? (`FIXED_PRICE`|`COST_PLUS`|`TIME_AND_MATERIAL`), `ServiceBillingModel` string? (`INCLUSIVE`|`PASS_THROUGH`), `EstimatedDurationHours` decimal?, `HasServiceBom` bool, `IsSubcontractable` bool. SVC-P-01/02/03/04/07 enforced. **SVC-P-06** "unit price = hourly rate" ⇒ the default variant's `SellingPrice` > 0.
- **D-3 SVC-P-05 is not enforced at product save.** It is circular with SVC-BOM-01 (a BOM can only be created once the flag is on). Instead: a service with `HasServiceBom` but no ACTIVE BOM plans as ad-hoc, and the product page warns "no active service BOM".
- **D-4 BOM lines**: `SourceType` string (`STOCK` default | `SUBCONTRACT` | `INTERNAL_LABOR`), `SubcontractSupplierUuid` Guid? (business partner, must be a vendor). Eligibility: `SupplyMethod == MANUFACTURE` **or** (`ProductType == SERVICE` and `HasServiceBom`). SVC-BOM-02/03/04 as written; SVC-BOM-05: an INTERNAL_LABOR line's material product UOM must be `HR`. Manufacturing BOM rules unchanged (SVC-BOM-06). Service BOMs use the same draft → submit → approve → activate lifecycle.
- **D-5 ServiceOrder** (Material, `material.ServiceOrders`): statuses `DRAFT, PLANNED, MATERIAL_PENDING, WAITING, READY, IN_PROGRESS, COMPLETED, CLOSED, CANCELLED`; readiness `NOT_CHECKED, PARTIAL, READY, SHORTAGE, NOT_APPLICABLE`; source `MANUAL | SALES_ORDER`; priority 0–3 (AllocationPriority); `ServiceProductUuid` + `ServiceVariantUuid` (default variant when not given); `CustomerUuid` (partner, must be a customer); `WarehouseUuid`; `AssignedUserId` int?; `AssignedRoleId` int? (team = role); `ScheduledDate` date?, `ScheduledTime` time?; invoicing policy / billing model copied from the product (defaults FIXED_PRICE / INCLUSIVE); `EstimatedHours` = product hours × qty; BOM snapshot (`BomId`, `BomVersion`) taken at **plan**, immutable after. Number `SVC-yyyy-#####`.
- **D-6 SMRs** (`material.ServiceMaterialRequirements`), statuses `PENDING, PARTIALLY_RESERVED, FULLY_RESERVED, ISSUED, CONSUMED, RETURNED, CANCELLED`. STOCK SMRs register an allocation demand (`SERVICE_ORDER`, DemandUuid = order UUID, DemandLineUuid = SMR UUID) and run allocation; an `IAllocationRunListener` updates reserved/shortage and recalculates readiness (ST-02/03/04/07). SUBCONTRACT SMRs raise a `SupplyRequirement` (`DemandSourceType SERVICE_ORDER`, `SupplyMethod PURCHASE`, supplier = BOM line supplier) and count as covered once that requirement is `ORDERED` or later (labour does not arrive into stock). INTERNAL_LABOR SMRs: cost tracking only, never critical, no stock, no PO.
- **D-7 Issues**: new `material.ServiceMaterialIssues` + `ServiceMaterialIssueLines` (types `ISSUE`, `RETURN`), posted through `IInventoryLedgerService` with new transaction types `SERVICE_ISSUE` / `SERVICE_RETURN`, allocation consumed via `IAllocationEngine.ConsumeAsync` — the production issue's own mechanics, not a shared polymorphic table. **Start** (READY → IN_PROGRESS) issues everything reserved (TS-10). Ad-hoc materials are issued immediately when available (SVC-MI rules).
- **D-8 Completion** (SVC-COMP-01..06): consumed ≤ issued; return = issued − consumed posted as one RETURN issue; actual hours required for TIME_AND_MATERIAL; ledger written in the same transaction; then the sale order is told (D-10).
- **D-9 Ledger** `material.ServiceLedgerEntries`: debit-only, returns as **negative** quantities, immutable (no update/delete API), trace id from the order, names denormalized.
- **D-10 Sale orders** (no FulfillmentRequirements): a SO line whose product is `SERVICE` is route-exempt and is not allocated/reserved as SO demand. After confirm (same moment and retry path as make-to-order production) Demand calls `IServiceOrderDemandService.EnsureForSaleOrderLineAsync` (Shared, implemented in Material) — idempotent per (SO, line). Material calls back `ISaleOrderServiceFulfillmentListener` when a service order completes/closes/cancels; a service line counts as fulfilled when its service order(s) for the line's quantity are COMPLETED/CLOSED, and SO status is recomputed with deliveries. SO cancel cancels service orders not yet IN_PROGRESS (running ones are kept and reported, like production). SO detail gets `serviceOrders[]` and line `isService`.
- **D-11 Feature switch** `MODULE_SERVICES` ("Service Orders"), depends on `MODULE_INVENTORY`; in Standard and Enterprise, not Basic. Endpoints `[RequiresFeature]`; without it, SO confirm creates no service orders (service lines are still exempt from delivery).
- **D-12 Permissions** `SERVICE_ORDER_VIEW`, `SERVICE_ORDER_CREATE`, `SERVICE_ORDER_EDIT` (update, plan, start, ad-hoc materials, close), `SERVICE_ORDER_COMPLETE`, `SERVICE_ORDER_CANCEL`. System Admin / Org Admin: all (automatic). Inventory Manager: all five. Warehouse Operator (technician): VIEW, EDIT, COMPLETE. Procurement Manager, Purchase Officer, Finance Officer/Manager, Auditor: VIEW.
- **D-13 Invoicing** stays out of scope (FSD §11.5). **CLOSED** is a manual action (`POST /close`, EDIT permission) from COMPLETED. Residual R-1: sales invoices are raised from deliveries, so service lines cannot be invoiced yet.
- **D-14 Timeline**: existing document timeline with the order's trace id (carried from the SO when sourced from one); interface code **`SERVICE_ORDER`**, documentId = the order's UUID; events SERVICE_ORDER_CREATED / PLANNED / STARTED / MATERIAL_ISSUED / WAITING / COMPLETED / CLOSED / CANCELLED. (Frontend timeline knows `SERVICE_ORDER`.)
- **D-15 Frontend**: Angular, SMS Flow kit. Menu group "Services" (feature `MODULE_SERVICES`): Service Orders, New Service Order, Dashboard. Routes `/portal/pages/services/service-orders` (list), `/new`, `/dashboard`, `/:uuid`. Detail = object page with stages, sections Details · Materials · Completion · Ledger, Timeline in the side panel.
- **D-16 Cancellation (TS-30)**: cancel releases every allocation demand, cancels open supply requirements, and **returns** everything issued (posted as a RETURN issue) — rather than refusing until someone returns by hand. Allowed from any non-terminal state (ST-10).
- **D-17 No event bus**: "events" in the spec are direct calls in the same unit of work.
- **D-18 Ad-hoc remove (TS-32)**: hard delete only while nothing was issued; its allocation demand is cancelled.

- **D-19 (CAT)** `api/boms` is reachable with **either** MODULE_MANUFACTURING or MODULE_SERVICES (`RequiresFeature` now takes alternatives). Product edits are the existing `PATCH /api/products/{id}` (there is no PUT): omitted = unchanged, `null` clears codes/duration, `false` clears flags.
- **D-20 (CAT)** Service BOM inputs: a variant available for services or production. A STOCK line may not use a SERVICE material; manufacturing BOM lines must stay STOCK. INTERNAL_LABOR needs material UOM and line UOM `HR`.
- **D-21 (CAT)** Material reads product facts through `Services/CatalogProductReader` (`FindAsync` tenant-filtered, `GetAsync` with explicit org for jobs); active BOMs through `IBomStructureReader.GetActiveBomsAsync`. `MODULE_SERVICES` is backfilled at startup from each org's plan template (Standard/Enterprise **on**, Basic off) — same as MODULE_MANUFACTURING was.

## Residuals (accepted, reported at the end)
- R-1 Service lines cannot be invoiced until an invoicing addendum adds service-sourced invoice lines (D-13).
- R-2 Subcontract labour "covered" at PO placement, not receipt (D-6).
- R-3 SVC-P-05 relaxed to a warning (D-3).
- R-4 SVC-P-06 (T&M needs a selling price) is checked on product create/patch, not when the default variant's price is later lowered.
- R-6 (DEM) A failed service-order creation after confirm is retried on the next detail load of the order (no background sweep — a sweep would need a pending column).
- R-7 (DEM) Draft lines saved before A36 are classified as SERVICE only when next edited or confirmed.
- R-8 (QA) Stronger than R-2: planning auto-creates a *draft* PO for the subcontract line and its supply requirement is ORDERED at once (same as production), so a critical subcontract line is "covered" before anyone approves the PO — the order can be READY at plan.
- R-9 (SVC) SO-sourced service orders stay DRAFT until someone assigns a technician/team (SVC-03 enforced at plan); the A36 migration has no existence guards; readiness is not recomputed when a PO is placed from Purchase Required; held stock for a running job is issued on the next action or "Reserve all available".
- R-5 The startup feature backfill ignores dependencies: a Standard org with Inventory switched off would get Services on.
