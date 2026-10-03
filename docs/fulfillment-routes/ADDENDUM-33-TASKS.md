# Addendum 33 — Task tracker (SMS-TR-ADD-033 v1.0)

Spec: `ADDENDUM-33-FULFILLMENT-ROUTES.md` (**Part A first**). Design + decisions: `ADDENDUM-33-ANALYSIS.md`
(§3 conflicts, §4 decisions, §5 design, §7 risks). Rules: `docs/sales-preorder/AGENT-RULES.md` (apply to A33 too;
scratch base `scratchpad\a33\<role>\`, checkpoints `scratchpad\checkpoints\a33-<role>.md`).
Status: `todo` · `doing` · `done` · `n/a` (see Notes). Each agent updates its own rows.

## Decisions adopted (user, 2026-10-03: "start … ASAP" — analysis recommendations taken; all reversible)

| # | Adopted |
|---|---|
| D-1 | Auto-create deliveries on confirm (spec BR-C4-01), behind Sale Order Setting `AutoCreateDeliveriesOnConfirm`, **default ON**; only after the C-2 hold double-count fix; manual "Create deliveries" button kept for remainders/recovery |
| D-2 | Keep the repo's real delivery statuses; spec COMPLETED ≙ DELIVERED (→ CLOSED); SHIP step = IN_TRANSIT/PARTIALLY_DELIVERED/DELIVERED via consignment; every route ends at DELIVERED (PICK_ONLY via "Record collection") |
| D-3 | "Skip" = **auto-complete** skipped steps (no PACK → auto LOOSE handling unit → PACKED; no STAGE → auto-stage) so GI/booking/invoicing stay unchanged; tracker shows only route steps; auto steps logged as system actions |
| D-4 | Delivery mode comes from its route (SHIP step → SHIP else SELF_PICKUP); shipping address required if any line routes to SHIP; non-SHIP routes refused when SelfPickupEnabled is off; org-default tier picks the default shipping/non-shipping route by header mode |
| D-5 | DROP_SHIP lines exempt from the route gate |
| D-6 | Seed PICK_AND_SHIP as each org's default (non-breaking for existing drafts) |
| D-7 | APPROVAL: simple "Approve dispatch" on STAGED deliveries whose route has APPROVAL, new `DELIVERY_APPROVE`, GI refused until approved |
| D-8 | Route splitting is not "partial fulfilment" (confirm-time deliveries jointly cover all lines) |
| D-9 | Codes `FULFILLMENT_ROUTE_VIEW/MANAGE/ASSIGN`; MANAGE+ASSIGN to admin-type roles holding SHIPPING_RULE_MANAGE/STOCK_MANAGE; routes GET any-of (FULFILLMENT_ROUTE_VIEW, SALE_ORDER_VIEW, INVENTORY_VIEW, DELIVERY_VIEW); line override needs SALE_ORDER_EDIT |
| D-10 | Deliveries snapshot route steps at creation; system routes: name/description/order editable, steps locked |
| D-11 | Orgs without MODULE_LOGISTICS: no gate, no auto-create (unchanged behaviour) |
| D-12 | Hangfire sweep creates missing deliveries for CONFIRMED orders whose post-commit creation failed (idempotent) + recovery button |
| D-13 | Variant "Logistics tab" extra fields out of scope; show existing WeightKg/Dimensions next to the route |
| D-14 | Bulk assign: category incl. sub-categories (+ optional sub-category filter); only NULL variants; returns updated/skipped |
| D-15 | SO cancel: cancel open (pre-GI) deliveries, keep issued ones, list them in response + timeline |
| D-16 | Snapshot resolved route (+ source) onto the SO line at confirm |

## Agents

| Agent | Owns | Migrations |
|---|---|---|
| LOG | SMS.Shared contracts (first), API contract doc + frontend TS service, route entities/service/API, seed + provisioning + backfill, permission codes, delivery route columns | **LogisticsDbContext** |
| INV | variant route field, variant API, bulk assign | **InventoryDbContext** |
| DEM | SO line route, resolver, confirm gate (under lock), preview, route snapshot, call creator/canceller, D-12 sweep | **DemandDbContext** |
| FLOW | delivery creator (route × warehouse), canceller, C-2 hold fix, route-aware operations + auto-complete, approve, detail/list models, recovery endpoint | none (asks LOG) |
| FE-ADMIN | route list/editor, bulk assign UI, variant dialog route | — |
| FE-SO | SO line route column, source icons, preview panel, confirm gate UI, SO fulfillment tab | — |
| FE-DLV | delivery list source/SO/route filters+columns, delivery detail route badge + step tracker + step actions | — |
| QA | Phase F + per-feature host tests | — |
| REV | independent code review of every agent's diff (correctness, tenancy, permissions, concurrency, migrations) | — |

## Phase A — Route Configuration (C1)

| Id | Task | Owner | Status | Notes |
|---|---|---|---|---|
| A33-PA-01 | Routes + steps tables (M1) | LOG | done | migration `20261003081429_A33_FulfillmentRoutes` (with PD-01); LocalDB up/down/up OK; no pending model changes |
| A33-PA-02 | Entities & configuration | LOG | done | `Domain/FulfillmentRouteEntities.cs`, `Data/Maps/FulfillmentRouteMaps.cs`; unique default per class (L-1) |
| A33-PA-03 | Seed 3 routes per org (provisioning + backfill) | LOG | done | D-6 + L-1 (PICK_ONLY = SELF_PICKUP default); `FulfillmentRouteSeeder`, provisioning handler, `UseLogisticsModule` backfill |
| A33-PA-04 | Route service rules BR-C1-01..09 | LOG | done | `Services/FulfillmentRouteService.cs`; usage via `IEnumerable<IFulfillmentRouteUsage>` (L-7) |
| A33-PA-05 | Route CRUD API | LOG | done | `Controllers/FulfillmentRoutesController.cs` (API-CONTRACT §3) |
| A33-PA-06 | Permission codes | LOG | done | D-9; in `All`, seeder (Inventory Manager gets VIEW/MANAGE/ASSIGN), role-editor group Logistics |
| A33-PA-07 | Route list page | FE-ADMIN | done | `pages/logistics/fulfillment-routes/`; route `logistics/fulfillment-routes` (VIEW/MANAGE), menu Settings after Sale Order Settings (MODULE_LOGISTICS); + D-14 "Assign to category" dialog (ASSIGN) |
| A33-PA-08 | Route editor | FE-ADMIN | done | dialog; fixed-order steps (L-2), live status path mirrors server StatusPath, default per class (L-1), L-5 client check |
| A33-PA-09 | Phase A tests | LOG | done | `tests/SMS.Modules.Logistics.Tests/Routes/` (service, seeding, lookup, controller gates, LocalDB), MigrationTests, Auth `FulfillmentRoutePermissionSeedTests` |

## Phase B — Variant Assignment (C2)

| Id | Task | Owner | Status | Notes |
|---|---|---|---|---|
| A33-PB-01 | Variant route column (M2) | INV | done | `20261003081614_A33_AddFulfillmentRouteToVariants`: `ProductVariants.FulfillmentRouteUuid` + filtered index `(OrganizationId, FulfillmentRouteUuid)`, guarded SQL; up/down/up + replay on LocalDB (test); `has-pending-model-changes`: none |
| A33-PB-02 | Variant entity & DTOs | INV | done | `PUT api/variants/{uuid}/fulfillment-route` (FULFILLMENT_ROUTE_ASSIGN, MODULE_INVENTORY), `VariantFulfillmentRoutesController`; variant PATCH ignores routes; product detail variants carry route uuid/code/name; `IVariantFulfillmentRoutes` + `IFulfillmentRouteUsage` registered |
| A33-PB-03 | Bulk assign endpoint | INV | done | `POST api/fulfillment-routes/{uuid}/assign-by-category` (FULFILLMENT_ROUTE_ASSIGN, MODULE_LOGISTICS); one set-based `IS NULL` update under a per-org applock |
| A33-PB-04 | Variant dialog route dropdown | FE-ADMIN | done | `product-detail/variant-route-field/`; in the variant dialog above Weight/Dimensions (D-13), MODULE_LOGISTICS only; saved after the variant via PUT …/fulfillment-route only when changed and with ASSIGN, read-only otherwise |
| A33-PB-05 | Phase B tests | INV | done | `VariantFulfillmentRouteTests` (20, in-memory) + `VariantFulfillmentRouteBulkAssignTests` (4, LocalDB): T-C2-01..04, cross-org / super-admin 404, race, migration; Inventory suite 362/362; ratchet + anonymous green |

## Phase C — SO Route Selection & Gate (C3)

| Id | Task | Owner | Status | Notes |
|---|---|---|---|---|
| A33-PC-01 | SO line route column (M3) | DEM | done | migration `20261003081715_A33_FulfillmentRoutesOnSaleOrders` (guarded SQL): line `FulfillmentRouteUuid` (filtered index, not org-led so the tenant index stays) + `FulfillmentRouteCode` + `RouteSource` (D-16), SO `DeliveryCreationPendingSince` (REV-01, filtered index), config `AutoCreateDeliveriesOnConfirm` default 1. LocalDB up/down/up + re-run on an already-migrated DB; no pending model changes |
| A33-PC-02 | SO line entity & DTOs | DEM | done | request `fulfillmentRouteUuid`; line `effectiveRoute*`, `routeSource`, `routeBlocker`; detail `routesEnabled`, `confirmBlockers` |
| A33-PC-03 | EffectiveRouteResolver | DEM | done | `Services/EffectiveRouteResolver.cs`: line → variant → org default by header class (L-1) → NONE; DROP_SHIP exempt (D-5); D-4 address / pickup checks; one variant read + one route read + ≤1 defaults read per call; D-11 via MODULE_LOGISTICS |
| A33-PC-04 | Confirmation gate | DEM | done | inside `ConfirmHeldAsync` under the per-order lock, before reserving; 400 = blockers joined by "\n"; D-16 snapshot in the same commit; creator called after the lock commits (REV-02), best effort; `SaleOrderConfirmResultModel` |
| A33-PC-05 | Delivery preview | DEM | done | GET `{uuid}/delivery-preview` (snapshot after confirm, warehouse from holds) + POST `delivery-preview` (unsaved form); route × warehouse groups |
| A33-PC-06 | SO line API changes | DEM | done | BR-C3-01 on POST/PUT (one lookup per request); DRAFT only. Lead add-on: route-only `PUT {uuid}/lines/{lineUuid}/fulfillment-route` (SALE_ORDER_EDIT, no re-pricing, under the lock) |
| A33-PC-07 | SO lines route column UI | FE-SO | done | form Route field + detail Route column (dropdown in DRAFT w/ SALE_ORDER_EDIT — detail change uses the route-only `setLineFulfillmentRoute` PUT, never the full-draft PUT, so no re-pricing; read-only after confirm); ⓥ ✎ ⊙ ⚠ + legend; DROP_SHIP none; hidden when routesEnabled false (D-11). REV-03 fixed: the form shows, keeps and sends the shipping address on a SELF_PICKUP order when a line routes to SHIP (D-4). Helpers `sale-orders/fulfillment-route-display.ts` |
| A33-PC-08 | Delivery preview panel | FE-SO | done | `delivery-preview-panel` component: form = POST (400 ms debounce, only on line/route/mode/address changes; incomplete lines left out, renumbered), detail DRAFT = GET; collapsible |
| A33-PC-09 | Confirm button gate | FE-SO | done | disabled, not hidden; tooltip count + lines; warning banner; 400 "\n" blockers listed in the confirm dialog; confirm result names created deliveries / D-12 failure banner + retry |
| A33-PC-10 | Phase C tests | DEM | done | `SaleOrderFulfillmentRouteTests` (33: T-C3-01..09, BR-C3-01, D-4/5/11/16, D-1 on/off/failure, cancel cascade, D-12 sweep, usage, route-only update, own-org), `SaleOrderConfirmDeliveriesConcurrencyTests` (LocalDB: two confirms reserve once, creator once, outside the lock), config + C-2 tests |

## Phase D — Delivery Creation from SO (C4)

| Id | Task | Owner | Status | Notes |
|---|---|---|---|---|
| A33-PD-01 | Delivery route columns (M4) | LOG | done | `FulfillmentRouteUuid` (filtered index), `FulfillmentRouteCode`, `RouteSteps` snapshot (D-10), `ApprovedBy` (D-7); same migration as PA-01 |
| A33-PD-02 | Delivery entity & DTOs | FLOW | done | list: `saleOrderUuid`, `customerName`, `fulfillmentRouteUuid/Code/Name`, `lineSummary`; detail: + `routeSteps[]` (§8 DONE/CURRENT/PENDING, ON_HOLD by statusBeforeHold), `requiresApproval`, `approvedAt/By`, `nextStep`, `nextActions[]` (first forward action = route's next step; RECORD_COLLECTION second for collected deliveries, agreed with REV/QA). Customer name via `ISupplierNameLookupService` (optional) |
| A33-PD-03 | SaleOrderDeliveryCreator | FLOW (+DEM wiring) | done | **C-2 fix first**: `ISaleOrderDeliveryQuantities.GetHeldBySoLineAsync` (RELEASED..STAGED, PENDING_APPROVAL, ON_HOLD; never DRAFT). Creator: REV-02 applock (`SaleOrderLocks.HoldsResource`, own Logistics tx + exec strategy) → re-read SO with explicit org → route × ship-from warehouse (a line held in 2 warehouses is split) → DRAFT, mode from route (D-4), snapshot (D-10), outstanding only (idempotent), D-8 not consulted, org-stamped numbers. D-1, D-8, C-2 fix first. DEM wiring done: `SaleOrderHolds` reads `GetHeldBySoLineAsync` (C-2); confirm calls the creator after commit behind `AutoCreateDeliveriesOnConfirm`; D-12 `SaleOrderDeliverySweepJob` (every 15 min, pending ≥10 min, per-org `HangfireTenantScope`); `ISaleOrderService.MarkDeliveriesCreatedAsync` for the recovery endpoint |
| A33-PD-04 | SO cancel → delivery cancel | FLOW (+DEM wiring) | done | canceller: no lock, explicit org, existing cancel path per pre-GI delivery (holds back to the SO first), issued ones reported; single delivery cancel now own-org (R-14). D-15. DEM wiring done: canceller called inside the cancel's lock, before the release and any write; `SaleOrderCancelResultModel` + `SO_DELIVERIES_CANCELLED` timeline |
| A33-PD-05 | Delivery API filters/SO deliveries | FLOW | done | `GET deliveries?saleOrderUuid=&fulfillmentRouteUuid=`; SO deliveries carry the new fields (own-org); `POST api/sale-orders/{uuid}/create-deliveries` (DELIVERY_CREATE; snapshot lines only, pre-A33 lines skipped "use Create delivery"; calls `MarkDeliveriesCreatedAsync`); `POST …/{uuid}/approve` (DELIVERY_APPROVE) → `DeliveryApprovalModel`; `POST …/{uuid}/advance` (any of EDIT/DISPATCH/APPROVE, then the step's own code → 403) → `{previousStatus,status,action}` |
| A33-PD-06 | SO fulfillment tab | FE-SO | done | extended the A29 Deliveries tab: route, line count, status, View, expandable lines (delivery GET, once), "N of M lines delivered", "Create deliveries" (CONFIRMED/PARTIALLY_FULFILLED + DELIVERY_CREATE + routes) with skipped lines, D-15 cancel result panel, link to FE-DLV's filtered list |
| A33-PD-07 | Delivery list source filter | FE-DLV | done | source filter (real codes), Sale Order column (link + customer), route badge column, route filter, `?saleOrderUuid=&saleOrderNumber=` chip |
| A33-PD-08 | Phase D tests | FLOW | done | Logistics `Routes/`: SaleOrderHeldQuantityTests (C-2, 5), SaleOrderDeliveryCreatorTests (T-C4-01..05 + 15), SaleOrderDeliveryCancellerTests (T-C4-06/07 + 14), SaleOrderDeliveryApiTests (6) — all watched red first. Host: FulfillmentRouteLockingHostTests (REV-02 race, watched red with the lock bypassed) |

## Phase E — Route-Aware State Machine (C5)

| Id | Task | Owner | Status | Notes |
|---|---|---|---|---|
| A33-PE-01 | Route-aware state machine | FLOW | done | table unchanged; `DeliveryRouteFlow`. No PACK → pick confirm boxes into one LOOSE unit → PACKED; no STAGE → auto-staged (pick or pack); D-7 guard inside `IssueAsync` (covers pickup); pickup refused (400) on SHIP routes; consignment refused (400) without SHIP (checked before the consignment row); SPLIT backorder keeps the route. REV-04: auto-staged stays amendable (weigh/void) until approved, on a consignment, or issued. Null route = today (R-1). D-2, D-3 |
| A33-PE-02 | Advance endpoint | FLOW | done | `DeliveryRouteService.AdvanceAsync`: DRAFT→release, PACKED→stage, STAGED+approval pending→approve, STAGED/PENDING_APPROVAL→GI; 409 naming the input step; stale `expectedStatus` 409; own-org |
| A33-PE-03 | Cancellation, route-independent | FLOW | done | existing cancel (before GI, any route) + own-org filter |
| A33-PE-04 | Step tracker UI | FE-DLV | done | route steps only from `routeSteps` (+Complete, D-2), ✓/●/○, real-status caption, stopped note; no route = unchanged; stacks < 640px |
| A33-PE-05 | Delivery detail route badge & info | FE-DLV | done | route badge, SO link, customer, Shipped column; buttons from `nextActions` gated by own permission (+DELIVERY_APPROVE); STAGE/GI via advance, APPROVE via approve, input steps open existing screens/dialogs |
| A33-PE-06 | Phase E tests | FLOW | done | Logistics `Routes/RouteAwareOperationsTests` (T-C5-01..08 + D-7, guards, advance, REV-04 a–e; real Inventory ledger) red→green; host REV-04 on SQL. FULL Logistics suite 2124/2124; ratchet 3/3 |

## Phase F — Testing & Integration

| Id | Task | Owner | Status | Notes |
|---|---|---|---|---|
| A33-PF-01 | E2E full chain | QA | done | `tests/SMS.Integration.Tests/FulfillmentRoutes/FulfillmentRouteFullChainE2ETests.cs` 4/4 green (end markers reached; kit `RouteKit.cs`): routes → variant/override/default → preview → confirm → 2 auto deliveries → full custom route (PACK, STAGE, APPROVAL by hand, advance/approve) + PICK_AND_SHIP auto LOOSE/stage → FULFILLED + invoiced; nextActions forward list per status; T-C4-01; D-7 bypass on APPROVAL-without-SHIP (GI + pickup 400); D-10 snapshot, L-6, ON_HOLD/RESUME |
| A33-PF-02 | E2E Punjab mixed routes | QA | done | `FulfillmentRoutes/PunjabMixedRoutesE2ETests.cs` 1/1 green: 3 routes → 3 deliveries; PICK_ONLY via Record collection, PICK_PACK_SHIP 2 boxes + consignment, PICK_AND_SHIP auto LOOSE + consignment; list filters; nextActions per status; all invoiced |
| A33-PF-03 | E2E gate blocking | QA | done | `FulfillmentRoutes/FulfillmentRouteGateE2ETests.cs` 5/5 green (end markers reached): missing/inactive blockers, class defaults (L-1), D-4 address (REV-03) + self-pickup off, D-5 drop ship, D-11 no Logistics |
| A33-PF-04 | E2E RBAC | QA | done | `FulfillmentRoutes/FulfillmentRouteRbacE2ETests.cs` 3/3 green: route endpoints at none/each read code/MANAGE/ASSIGN + Inventory Manager seed; variant ASSIGN + ungated PATCH can't set a route; preview/override/confirm/recovery codes; DELIVERY_APPROVE, advance own-code 403s, existing delivery codes walk SO deliveries |
| A33-PF-05 | E2E multi-tenant | QA | done | `FulfillmentRoutes/FulfillmentRouteTenantIsolationE2ETests.cs` 2/2 green: per-org seeds, per-org codes, 404 both ways incl. super admin on every route/SO/delivery endpoint; no cross-org route on variant/line/delivery (SQL sweep) |
| A33-PF-06 | Regression existing delivery sources | QA | doing | `FulfillmentRoutes/FulfillmentRouteRegressionE2ETests.cs` (5 facts) + re-runs; `SapKit.DeliverAsync` adapted to D-1 |
| A33-PF-07 | Final review & documentation | REV + lead | todo | |
