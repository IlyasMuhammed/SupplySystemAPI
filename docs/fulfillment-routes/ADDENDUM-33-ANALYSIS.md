# Addendum 33: overlap and risk analysis (Fulfillment Routes)

Written 2026-10-03 by the A33 analyst. Read this together with Part A of `ADDENDUM-33-FULFILLMENT-ROUTES.md`,
which is the line-by-line "spec says / repo has" table with file references. Everything here was checked by
reading code; nothing was run. Items marked *unverified* still need checking. Paths are relative to
`D:\Supply System\SupplyChain`.

## 1. Bottom line

- **About 40% of C4 already exists** (A29). The SALE_ORDER source type, the delivery→order and line→order-line
  links, the SHIP/SELF_PICKUP mode on the delivery, the reservation hand-over at release, crediting the SO line at
  goods issue, SO fulfilment status on DELIVERED, and the SO "Deliveries" tab all exist. What's new: the route
  entities, the variant and line route fields, the confirmation gate, **automatic** creation grouped by route,
  route-aware steps, and cancelling deliveries when an SO is cancelled.
- The spec's picture of the delivery state machine is wrong. The statuses differ (no COMPLETED, no AT_HUB;
  hub/out-for-delivery/attempted are consignment states). Transitions are hard-coded per operation. Goods issue
  ships *packed* quantities. Carrier booking needs packages. Invoicing needs DELIVERED. A literal "skip PACK /
  skip SHIP statuses" therefore breaks goods issue, booking, invoicing and SO fulfilment.
- Auto-creating DRAFT deliveries on confirm collides with A32's "held = reserved + on open deliveries" rule
  (DRAFT counts). It would also run into the partial-fulfilment setting and multi-warehouse reservations. All
  three have to be handled before C4 can switch on.

## 2. Already implemented (fully or partly)

| Spec item | Status | Where |
|---|---|---|
| §2.3 SALE_ORDER source type | **Done** | `src/SMS.Modules.Logistics/Domain/LogisticsEnums.cs:40`, `Domain/DeliverySourceTypeInfo.cs:54` |
| M4 `sale_order_id` / `sale_order_line_id` | **Done** as `SaleOrderUuid` / `SoLineUuid` (Guid, no FK) | `Domain/DeliveryEntities.cs:61, 243`; migration `20260919142124_AddDeliverySaleOrderFulfilment` |
| §6.5 SO ↔ delivery linkage, delivery links back to the SO | **Done** | `DeliveryDetailModel.SaleOrderUuid`; `delivery-detail.component.html:28-30` (link to the order) |
| §6.5 / §10.4 SO page lists its deliveries | **Partly**: "Deliveries" tab (number, status, view) + "Fulfilment" card; no route column, no per-route grouping | `GET api/sale-orders/{uuid}/deliveries` (`Controllers/SaleOrderDeliveriesController.cs:46`); `sale-order-detail.component.html:87-97, 241-285` |
| C4 create delivery from an SO | **Partly**: manual, one delivery per call, picks lines and quantities, outstanding-aware, partial-fulfilment rule, ship-from = warehouse holding the stock | `POST api/sale-orders/{uuid}/create-delivery` → `Repositories/DeliveryFromSourceRepository.cs:373-471` |
| BR-C4-04/05 source type + line back-link | **Done** for manual deliveries | same |
| Customer collects (PICK_ONLY's purpose) | **Done** via `DeliveryMode = SELF_PICKUP` and "Record collection" (ID capture, gate pass, SALES_HANDOVER movement) | `GoodsIssueRepository.RecordPickupAsync`, `DeliveriesController.cs:222-240` |
| Reservation hand-over and consumption | **Done** | `DeliveryReleaseRepository.cs:356-410` (`TransferLineAsync`), `GoodsIssueRepository.CreditSaleOrderAsync` |
| SO status from deliveries | **Done** (at DELIVERED) | `Demand/Services/SaleOrderFulfillmentService.cs`, `Logistics/Services/SaleOrderDeliveryNotifier.cs` |
| Handling units / nesting / LOOSE & DRUM types | **Done** | `Domain/PackageEntities.cs`, `LogisticsEnums.cs:413-419` |
| Per-org seeding hook + startup backfill | **Done** (A32) | `SMS.Shared/Common/IOrganizationProvisionedHandler.cs`, `Demand/IDemandModule.cs:104-120` |
| BR-C5-05 "cancel from any status" | **Different**: cancel only before GOODS_ISSUED (by design) | `DeliveryStateMachine.cs:108-115` |
| C1, C2, C3, route-aware steps, step tracker, SO-cancel → delivery-cancel, delivery preview | **Not started** | — |

## 3. Conflicts: where the spec, taken literally, breaks existing behaviour

**C-1. Auto-create on confirm vs today's manual flow.** Today a person raises each delivery and chooses lines,
quantities, mode, ship-from warehouse, addresses and dates (`CreateSaleOrderDeliveryRequest`). The spec creates
them all at confirm. Consequences:
(a) the manual "Create delivery" button still has to work for re-deliveries (after a short-close, cancel or
split), but after auto-create it finds nothing outstanding;
(b) BACK_TO_BACK and SPLIT-deficit lines get DRAFT deliveries for stock that hasn't arrived. Release then blocks,
or SPLITs into more drafts;
(c) the cross-module call runs after Demand's commit (Demand can't reference Logistics). Confirm and delivery
creation are therefore not atomic.

**C-2. A32 hold arithmetic counts DRAFT deliveries as held.** `SaleOrderHolds.ReadAsync` adds
`ISaleOrderDeliveryQuantities.GetInFlightBySoLineAsync` (DRAFT…STAGED, PENDING_APPROVAL, ON_HOLD,
`Logistics/Services/SaleOrderDeliveryQuantities.cs:16-26`) to the ACTIVE SALES_ORDER holds. A DRAFT delivery
holds nothing; the SO hold moves only at release. If every confirmed order immediately has DRAFT deliveries for
100% of every line:
- every line shows "held" (indicator BLUE);
- `ReservableFor` = 0, so manual Reserve and Reserve-all are dead;
- `SaleOrderHolds.Reconcile` (run by manual reserve/release and by `ReservationExpirySweepJob:112-113`) sets
  `DeficitQty` = 0, after which `SaleOrderGrnLinkService` (:90, `wanted = Min(received, DeficitQty)`) **reserves
  nothing when the back-to-back PO's goods arrive**.

The same double count already happens today in the narrow window between a manual draft and its release. A33 would
make it universal.

**C-3. Partial-fulfilment setting.** With `SaleOrderConfig.PartialFulfillmentAllowed = false`,
`EnsurePartialFulfilmentAllowedAsync` (`DeliveryFromSourceRepository.cs:478-499`) demands **one** delivery that
covers everything owed. Grouping by route produces several, so each creation call would be refused.

**C-4. Multi-warehouse reservations.** Confirm reserves each line in its best single warehouse, so different lines
can sit in different warehouses. `WarehouseHoldingAsync` (:533-553) throws when the chosen lines are held in more
than one warehouse. Grouping by route alone fails for those orders; it has to be route × ship-from warehouse.

**C-5. Statuses that don't exist.** AT_HUB doesn't exist. OUT_FOR_DELIVERY and DELIVERY_ATTEMPTED are consignment
statuses. COMPLETED doesn't exist (CLOSED does, and nothing sets it). T-C5-02's "GOODS_ISSUED → COMPLETED" for
PICK_ONLY would skip DELIVERED. That means no SO fulfilment notice (`SaleOrderDeliveryNotifier` fires on
DELIVERED) and **no invoice** (`SalesInvoiceService.cs:98-101` needs DELIVERED or CLOSED with `QtyDelivered > 0`).

**C-6. Route-skip vs the hard-coded transitions.** `DeliveryStateMachine` has no PICKED→GOODS_ISSUED or
PACKED→GOODS_ISSUED edge. GI needs STAGED or PENDING_APPROVAL and issues `QtyPacked` (refusing zero). Staging
refuses unpacked picked lines. Carrier booking refuses a consignment with no packages
(`CourierBookingRequestFactory.cs:64-67`). Skipping PACK by adding edges therefore also forces changes to GI
quantities, staging, booking, the packing list and the gate pass. The Logistics unit tests that pin the table
(`tests/SMS.Modules.Logistics.Tests`, *unverified which ones*) would fail.

**C-7. APPROVAL step.** PENDING_APPROVAL is reachable only through the workflow engine
(`DeliveryStatusHandler`). No DELIVERY workflow definition is seeded and no code or UI submits a delivery, so a
route with APPROVAL would stall at STAGED with no way to reach approval.

**C-8. DeliveryMode / RequiresShipment / FulfillmentMode vs routes.**
- `SaleOrder.DeliveryMode` (SHIP / SELF_PICKUP) is the header-level "does it ship". `RequiresShipment` is just
  `mode == SHIP` (`SaleOrderService.cs:127, 180`).
- A route's SHIP step says the same thing per line. A SHIP order can contain a PICK_ONLY line, and a SELF_PICKUP
  order can contain a PICK_PACK_SHIP line, which has no shipping address. `ShippingAddressOfAsync` would then throw.
- The delivery's `DeliveryMode` decides the GI movement type (SALES_SHIP vs SALES_HANDOVER,
  `GoodsIssueRepository.SalesMovementOf`) and whether "Record collection" is allowed.
- `SaleOrderLine.FulfillmentMode` (IN_STOCK / BACK_TO_BACK / DROP_SHIP / SPLIT) is **sourcing**, not warehouse
  steps, so the two are orthogonal. DROP_SHIP lines never get a delivery. The names "fulfillment mode" and
  "fulfillment route" will confuse users, so the UI should say "Sourcing" and "Route".

**C-9. Spec API shape.** There are no per-line SO endpoints (DRAFT lines are replaced wholesale by
`PUT api/sale-orders/{uuid}`) and no `api/product-variants` or `api/delivery-orders`. The variant PATCH is ungated
(grandfathered), so putting the route on that PATCH would let anyone with the Inventory feature assign routes,
bypassing the new ASSIGN permission.

**C-10. SO cancel.** BR-C4-06 is new. Today cancelling an order leaves its deliveries alive: they can still be
picked and issued, and released deliveries keep their DELIVERY holds. Also, `CancelHeldAsync` runs under the SO
lock in Demand, while the deliveries live in Logistics on another connection.

**C-11. Spec says "no changes to existing structures".** That's not possible: `delivery_orders` needs the route
columns, and the operations need to read the route.

## 4. Decisions needed from the user

| # | Question | Options | Recommendation |
|---|---|---|---|
| D-1 | Create deliveries automatically on confirm (spec) or keep manual? | (a) Auto, every non-drop-ship line, at confirm. (b) Auto only for quantity reserved at confirm; the rest gets a delivery when stock is reserved later (GRN link / manual reserve). (c) Manual, but the button creates one delivery per route group. | **(a)** as the spec says, behind a Sale Order Setting `AutoCreateDeliveriesOnConfirm` (default ON), and only after C-2 is fixed. Keep the manual button for remainders and recovery. |
| D-2 | Status vocabulary | (a) Add the spec's statuses (COMPLETED, AT_HUB…). (b) Keep the repo's 15 and map them. | **(b)**: COMPLETED = DELIVERED (→ CLOSED). The SHIP step = IN_TRANSIT / PARTIALLY_DELIVERED / DELIVERED from the consignment. **Every route ends at DELIVERED**; PICK_ONLY gets there by "Record collection". |
| D-3 | How to "skip" steps | (a) True skip: new edges PICKED→GI and PACKED→GI, GI issues QtyPicked when unpacked, booking without packages. (b) **Auto-complete the skipped steps**: when the route has no PACK, confirming the pick auto-boxes each picked line into a LOOSE handling unit (→ PACKED); when it has no STAGE, auto-stage (→ STAGED). The table, GI, booking, packing list and invoicing stay unchanged. The tracker shows only the route's steps. | **(b)**: much smaller blast radius, and consignment booking keeps working for PICK_AND_SHIP. The audit trail shows the auto steps as system actions. |
| D-4 | SO DeliveryMode vs line routes | (a) The route decides each delivery's mode (route has SHIP → SHIP, else SELF_PICKUP); the header mode becomes the default for resolution and decides whether an address is required. (b) The header mode restricts which routes lines may use (SELF_PICKUP order → only routes without SHIP; SHIP order → any route). | **(a) plus a check from (b)**: a delivery's mode comes from its route. A shipping address is required if any line resolves to a route with SHIP. Routes without SHIP are refused when `SelfPickupEnabled` is off. The org-default tier picks the default shipping route for SHIP orders and the default non-shipping route for SELF_PICKUP orders. |
| D-5 | DROP_SHIP lines and the confirmation gate | (a) Exempt (no warehouse steps). (b) Require a route anyway. | **(a)**: no route needed and none shown. |
| D-6 | Seed an org default route? | (a) Seed PICK_AND_SHIP as default (spec): the gate effectively never blocks. (b) Seed with no default: existing DRAFT orders block until routes are assigned. | **(a)**: non-breaking on SMSGlobal; admins can clear the default to make the gate bite. |
| D-7 | APPROVAL step | (a) Leave it out of the allowed steps for now. (b) A simple "Approve dispatch" action on STAGED deliveries whose route has APPROVAL, gated by a new `DELIVERY_APPROVE`, setting `ApprovedAt`; GI refused until approved. (c) Wire up the workflow engine (seed a DELIVERY definition + submit action). | **(b)** if they want it now, otherwise (a). (c) is a bigger piece. |
| D-8 | Partial-fulfilment OFF + several route groups | (a) Route splitting doesn't count as partial: confirm-time deliveries jointly cover every line in full. (b) Force a single delivery when partial is off (ignore route grouping; the "strongest" route wins). | **(a)**: the setting is about not shipping part of a line or order early, and route splitting ships everything. |
| D-9 | Permissions and roles | Which roles get `FULFILLMENT_ROUTE_VIEW / MANAGE / ASSIGN`? Does a line route override need ASSIGN or just `SALE_ORDER_EDIT`? | MANAGE + ASSIGN → admin-type roles that hold `SHIPPING_RULE_MANAGE` / `STOCK_MANAGE`. The routes list GET is any-of (`FULFILLMENT_ROUTE_VIEW`, `SALE_ORDER_VIEW`, `INVENTORY_VIEW`, `DELIVERY_VIEW`). The line override needs `SALE_ORDER_EDIT` only. |
| D-10 | Editing routes that are in use; system routes | (a) Deliveries snapshot the route's steps at creation, so edits affect new deliveries only. (b) Live. And: are system routes' steps editable? | **(a)** snapshot. System routes: name, description and display order editable, steps locked. |
| D-11 | Orgs without MODULE_LOGISTICS | Gate on or off? | When Logistics isn't enabled for the org: no gate, no auto-create (no change from today). *Needs confirmation that this combination exists.* |
| D-12 | The spec's unnamed "1 Hangfire job" | (a) None. (b) A sweep that creates missing deliveries for CONFIRMED orders whose post-commit creation failed. | **(b)**, cheap and idempotent: it reuses the creator, which only creates for outstanding quantity. Plus a visible "Create deliveries" recovery button. |
| D-13 | Variant "Logistics" tab fields (gross weight, L/W/H, zone, storage class) | In scope or not? | **Out of scope**: no schema changes listed for them. Show the existing `WeightKg` / `Dimensions` next to the route. |
| D-14 | Bulk assign scope | Category only, or sub-category too? Overwrite? | Category (all its sub-categories) plus an optional sub-category filter. Only NULL variants are set (spec). Return counts updated / skipped. |
| D-15 | SO cancel when a delivery is already goods-issued | (a) Cancel the open deliveries and still cancel the SO (spec BR-C4-07: issued ones stay). (b) Refuse to cancel the SO. | **(a)**, as the spec says, listing the issued deliveries in the response and timeline. Note that existing code refuses SO cancel when live invoices exist, which stays. |
| D-16 | Route on a line after confirm | Snapshot the resolved route onto the line at confirm, or re-resolve for later manual deliveries? | Snapshot (`FulfillmentRouteUuid` + `RouteSource` written at confirm), so remainders follow the same route. |

## 5. Proposed technical design (repo terms)

### 5.1 Entities and migrations (one owner per DbContext)

**Logistics, migration `A33_FulfillmentRoutes`** (owner: A33-LOG)
- `FulfillmentRoute` → `logistics.fulfillment_routes`: `Id`, `UUID`, `OrganizationId`, `Code` (30, upper),
  `Name` (100), `Description` (500), `IsDefault`, `IsActive`, `IsSystem`, `RequiresPacking`, `RequiresShipping`,
  `DisplayOrder`, `CreatedBy/Date`, `ModifiedBy/Date`, `RowVersion`.
  - Unique `(OrganizationId, Code)`.
  - **Unique** filtered `(OrganizationId)` WHERE `IsDefault = 1`, which enforces BR-C1-02 in the DB. The spec
    has a non-unique index here.
  - If D-4 needs both a "default shipping" and a "default non-shipping" route, use
    `(OrganizationId, RequiresShipping) WHERE IsDefault = 1` instead.
- `FulfillmentRouteStep` → `logistics.fulfillment_route_steps`: `Id`, `OrganizationId`, `FulfillmentRouteId`
  (same-context FK), `StepCode` (enum `FulfillmentStepCode` with `[Code]`: PICK, PACK, STAGE, APPROVAL,
  GOODS_ISSUE, SHIP), `StepOrder`, `IsMandatory`, `Description` (200). Unique `(RouteId, StepOrder)` and
  `(RouteId, StepCode)`.
- `delivery_orders`: add `FulfillmentRouteUuid Guid?` (filtered index), `FulfillmentRouteCode string?` and
  `RouteSteps string?` (a snapshot such as `PICK,PACK,GOODS_ISSUE,SHIP`). Null = legacy full path (today's
  behaviour).
- No `sale_order_id` / `sale_order_line_id`: they exist.

**Inventory, migration `A33_AddFulfillmentRouteToVariants`** (owner: A33-INV): `ProductVariant.FulfillmentRouteUuid
Guid?` plus a filtered index.

**Demand, migration `A33_AddFulfillmentRouteToSaleOrderLines`** (owner: A33-DEM): `SaleOrderLine.FulfillmentRouteUuid
Guid?` plus a filtered index, and (D-16) `RouteSource string?` (LINE_OVERRIDE / VARIANT / ORG_DEFAULT, written at
confirm).

All three are additive and idempotent (every API start migrates SMSGlobal). Verify up/down/up on LocalDB and run
`has-pending-model-changes`. **No seed data in migrations.**

### 5.2 SMS.Shared contracts (written first, by A33-LOG; this breaks the Demand↔Logistics cycle)

- `IFulfillmentRouteLookup` (implemented in Logistics): `GetAsync(orgId, uuids)` → summaries {Uuid, Code, Name,
  IsActive, RequiresShipping, RequiresPacking, Steps[]}; `GetOrgDefaultsAsync(orgId)`.
  Used by Inventory (validating a variant assignment) and Demand (resolver, gate, preview).
- `IVariantFulfillmentRoutes` (implemented in Inventory): `GetRouteUuidsAsync(variantUuids)`.
  Used by Demand and Logistics. A new interface rather than a new member on the positional `VariantDescription`
  record, which would break every caller.
- `ISaleOrderDeliveryCreator` (implemented in Logistics):
  `CreateForConfirmedOrderAsync(orgId, saleOrderUuid, lineRoutes, userId)` → created deliveries. Idempotent: it
  only creates for outstanding quantity.
- `ISaleOrderDeliveryCanceller` (implemented in Logistics): `CancelOpenAsync(orgId, saleOrderUuid, reason,
  userId)` → {cancelled[], alreadyIssued[]}.
- `IFulfillmentRouteUsage` (implemented in Inventory, the way `IVariantReferenceChecker` is): "is this route on
  any active variant". Logistics reads SO lines directly through `DemandDbContext`.

### 5.3 Services and behaviour

- **Logistics `FulfillmentRouteService`**: CRUD; BR-C1-01..09 (PICK first, GOODS_ISSUE required, GOODS_ISSUE
  before SHIP, contiguous order, derived flags); set-default (transaction + unique index); deactivate (refused while
  in use: active variants via the contract, lines of DRAFT/CONFIRMED/PARTIALLY_FULFILLED orders via Demand);
  system routes not deletable. Explicit `OwnRoutes()` filter on every lookup and write.
- **Logistics `FulfillmentRouteSeeder`**: `EnsureSeededAsync(orgId)` matches by code and never touches an edited
  or deactivated route. It runs from `FulfillmentRouteProvisioningHandler : IOrganizationProvisionedHandler` and
  from the backfill in `UseLogisticsModule` after `Migrate()` (copy `IDemandModule.cs:104-120`).
- **Demand `EffectiveRouteResolver`**: line override → variant → org default (mode-aware, D-4) → none. DROP_SHIP
  lines are exempt (D-5).
- **Demand `ConfirmAsync`**:
  - wrap it in `SaleOrderHolds.OneChangeAtATimeAsync`;
  - run the gate before `CheckAndReserveAsync` (inactive route and missing route each get their own message,
    naming lines as "Line N" in `Id` order);
  - snapshot the route on each line;
  - commit;
  - then call `ISaleOrderDeliveryCreator`, best effort and logged, and append a timeline event. Recovery is the
    D-12 sweep and the button.
- **Demand delivery preview**: `GET api/sale-orders/{uuid}/delivery-preview` (SALE_ORDER_VIEW) → groups by route
  (and by expected warehouse where it can be known), plus unroutable lines. Not persisted.
- **Logistics `SaleOrderDeliveryCreator`**: groups by route × ship-from warehouse. It reuses
  `CreateFromSaleOrderAsync` with a new internal option: a route plus "confirm batch" mode, which skips the
  single-delivery partial-fulfilment check per D-8. The delivery's mode comes from the route (D-4). It stamps the
  route snapshot and runs under a Logistics-side applock on the SO uuid so two calls can't both create.
- **Fix C-2 (A33-FLOW with A33-DEM)**: split "in flight" into two numbers. *Planned* (DRAFT) still counts for "what
  may still be put on a delivery" (`OutstandingBySoLineAsync`). *Held* is RELEASED and later, and that is what
  `SaleOrderHolds` uses. Change `ISaleOrderDeliveryQuantities` (add a method rather than changing the existing one)
  and re-run the A32 hold tests.
- **Route-aware operations (A33-FLOW, D-3 option b)**:
  - pick completion (`PickListRepository.CompleteAsync`) auto-packs into LOOSE units when the route lacks PACK, and
    auto-stages when it lacks STAGE;
  - `PackageRepository`'s move to PACKED auto-stages when the route lacks STAGE;
  - GI refuses when the route has APPROVAL and `ApprovedAt` is null (D-7);
  - consignment creation is refused for routes without SHIP;
  - "Record collection" is refused for routes with SHIP;
  - a null route keeps today's behaviour exactly.
- **Detail model**: `FulfillmentRouteUuid/Code/Name`, `RouteSteps[]` with done/current/pending, and
  `NextActions[]`. The list filter gains `SaleOrderUuid` and `FulfillmentRouteUuid`.
- **SO cancel**: `CancelAsync` calls `ISaleOrderDeliveryCanceller` before releasing the SO holds. The canceller
  cancels DRAFT through PENDING_APPROVAL and ON_HOLD deliveries through the existing cancel path, which gives the
  delivery's hold back (`ReturnHoldAsync`, `DeliveryStatusRepository.cs:117`). That path looks the delivery up with
  only the tenant filter, so add an own-org filter. Issued deliveries are reported (D-15).

### 5.4 Endpoints

| Endpoint | Owner | Permission |
|---|---|---|
| `GET api/fulfillment-routes`, `GET api/fulfillment-routes/{uuid}` | Logistics | any of FULFILLMENT_ROUTE_VIEW, SALE_ORDER_VIEW, INVENTORY_VIEW, DELIVERY_VIEW |
| `POST api/fulfillment-routes`, `PUT …/{uuid}`, `POST …/{uuid}/deactivate`, `…/activate`, `…/set-default` | Logistics | FULFILLMENT_ROUTE_MANAGE |
| `PUT api/variants/{uuid}/fulfillment-route` (body: route uuid or null) | Inventory | FULFILLMENT_ROUTE_ASSIGN |
| `POST api/fulfillment-routes/{uuid}/assign-by-category` (body: `categoryId` int, `subCategoryId?`) | Inventory | FULFILLMENT_ROUTE_ASSIGN |
| `PUT api/sale-orders/{uuid}` / `POST api/sale-orders`: line `fulfillmentRouteUuid` | Demand | SALE_ORDER_EDIT / CREATE (existing) |
| `GET api/sale-orders/{uuid}`: per line `effectiveRouteUuid/Code/Name`, `routeSource` (LINE_OVERRIDE / VARIANT / ORG_DEFAULT / NONE), plus a `confirmBlockers[]` summary | Demand | SALE_ORDER_VIEW (existing) |
| `GET api/sale-orders/{uuid}/delivery-preview` | Demand | SALE_ORDER_VIEW |
| `POST api/sale-orders/{uuid}/create-deliveries` (recovery: one per route group) | Logistics | DELIVERY_CREATE |
| `GET api/logistics/deliveries/{uuid}` / list: route fields, steps, next actions, new filters | Logistics | DELIVERY_VIEW (existing) |
| (D-7b) `POST api/logistics/deliveries/{uuid}/approve` | Logistics | DELIVERY_APPROVE (new) |

**Permissions**: add the codes to `PermissionCodes.cs` (+ its all-codes list), `AuthDataSeeder.cs` (definitions and
role grants, matched by code; see memory "Supply Dept Admin role fix"), `AuthRepository.cs:862` (module
"Logistics"), the frontend permission constants and the route guards. Every new action is gated (ratchet test); no
`[AllowAnonymous]`.

### 5.5 Frontend

- Logistics → **Fulfillment Routes** list and editor: steps with ordering, mandatory flag, path preview, set-default,
  deactivate. Plus a bulk-assign dialog (category picker).
- Product detail → variant dialog: Route dropdown with its steps summary (FULFILLMENT_ROUTE_ASSIGN; read-only
  otherwise). Variants table: a route tag.
- `sale-order-form`:
  - a Route column per line (inherited ⓥ / overridden ✎ / missing ⚠), with the source text;
  - "Confirm order" disabled with a tooltip naming the blocking lines (the confirm button is on `sale-order-detail`,
    so the detail page needs the blocker summary too);
  - a Delivery Preview panel, also inside the existing confirm dialog.
- `sale-order-detail` Deliveries tab: route column, per-route grouping, overall progress bar. Keep the tab name
  "Deliveries", or rename it "Fulfillment" (cosmetic; ask).
- `delivery-detail`:
  - a step tracker that shows only the route's steps;
  - Stage, Goods-issue and (D-7) Approve actions on the detail page for routes without PACK (today only the pack
    station has them);
  - hide "Pack station" when the route has no PACK.

## 6. Proposed agent split (7 max; one migration owner per DbContext)

| Agent | Scope | Owns migrations of | Depends on |
|---|---|---|---|
| **A33-LOG** (Logistics foundation) | SMS.Shared contracts (day 1); route entities, maps, migration (incl. delivery route columns); seeder + provisioning handler + startup backfill; `FulfillmentRoutesController` / service; `IFulfillmentRouteLookup`; permission codes + Auth seeder | **LogisticsDbContext** | — |
| **A33-INV** (Inventory) | Variant column + migration; `IVariantFulfillmentRoutes`, `IFulfillmentRouteUsage`; variant DTO fields; assign endpoint; bulk assign by category | **InventoryDbContext** | contracts |
| **A33-DEM** (Demand) | Line column + `RouteSource` + migration; resolver; confirm gate; confirm under lock; preview endpoint; line models; call creator and canceller; D-12 sweep job (Demand side, `HangfireTenantScope`) | **DemandDbContext** | contracts |
| **A33-FLOW** (Logistics fulfilment) | `SaleOrderDeliveryCreator` (route × warehouse, D-8), `SaleOrderDeliveryCanceller`, C-2 fix (`ISaleOrderDeliveryQuantities`), route-aware pick / pack / stage / GI / collection / consignment guards, detail and list models, recovery endpoint, D-7 approve | none (asks A33-LOG for any column) | A33-LOG migration |
| **A33-FE-ADMIN** | Routes settings page + editor, bulk assign UI, variant dialog route, menu / routes / permissions | — | LOG, INV APIs |
| **A33-FE-OPS** | SO form route column + confirm blocker + delivery preview; SO detail deliveries-by-route; delivery-detail step tracker + actions | — | DEM, FLOW APIs |
| **A33-QA** | LocalDB integration tests per class (T-C1..T-C5, gate, auto-create grouping, C-2 regression, cancel cascade, tenant isolation, ratchet); E2E at the end | — | all |

Rules as in `docs/sales-preorder/AGENT-RULES.md`: never build SMS.API; never touch SMSGlobal; test-first; one
integration-test class per run. A32's E2E tester is active in `tests/SMS.Integration.Tests` right now, so A33-DEM
and A33-FLOW must not start until A32 has settled `ConfirmAsync` and `SaleOrderHolds`.

## 7. Risk list

**Data on SMSGlobal** (counts unknown; the shared DB must not be queried)

- R-1. Existing `delivery_orders` get a null route. Every route-aware code path and the UI must treat null as
  "legacy full path". No backfill.
- R-2. Existing SO lines get a null route. Confirmed and fulfilled orders are unaffected: the gate runs only on
  DRAFT → CONFIRMED and auto-create only at confirm. **Existing DRAFT orders** confirm without a hitch under D-6(a);
  under D-6(b) every draft blocks until routes are assigned.
- R-3. Startup backfill writes three routes (plus steps) for **every** organization on SMSGlobal at the first API
  start. It must be idempotent by code and never touch edited routes. The same applies to the provisioning handler.
- R-4. Every API start migrates SMSGlobal, which has drifted. The three migrations must be additive and idempotent
  (guarded SQL where needed), and must have no pending model changes (EF 9 refuses `Migrate()` otherwise). The
  order is Demand → Inventory → Logistics; nothing cross-schema, so order doesn't matter.
- R-5. Turning on auto-create changes what confirming does for every org at once. Ship it behind the
  `AutoCreateDeliveriesOnConfirm` setting (D-1).

**Concurrency**

- R-6. `ConfirmAsync` isn't under the per-order lock and `sale_orders` has no RowVersion. Two simultaneous confirms
  can both pass the DRAFT check, which today means double reservations and with A33 also double deliveries. Fix:
  confirm under `OneChangeAtATimeAsync`, plus a creator-side applock and outstanding-quantity check.
- R-7. Confirm (Demand) and delivery creation (Logistics) are separate commits. A failure leaves a CONFIRMED order
  without deliveries, which needs the D-12 recovery. The same split applies to SO-cancel → delivery-cancel: cancel
  the deliveries first, then the order; both are idempotent.
- R-8. Set-default races are covered by the unique filtered index. Deactivate vs concurrent assignment isn't atomic
  across modules, so re-validate at confirm (an inactive route blocks).
- R-9. A route edited while its deliveries are in flight is handled by the step snapshot (D-10).
- R-10. **C-2 hold double count**: the most likely silent failure. Without the fix, back-to-back GRNs stop
  reserving for orders confirmed after A33.

**Multi-tenant isolation**

- R-11. The new entities are `ITenantScopedEntity`, but the EF filter is off for super admins. Every by-id lookup
  and write filters on the caller's own org explicitly (`OwnRoutes()`); another org's route is a 404.
- R-12. Cross-module route references are unenforced Guids. Validate that the route belongs to the **same org** as
  the variant or line (T-C2-02), in the service, via `IFulfillmentRouteLookup` with an explicit `orgId`.
- R-13. Seeder, provisioning and backfill stamp the org they're given, never the caller's (a super admin creates
  orgs). Any Hangfire path (D-12 sweep) iterates orgs under `HangfireTenantScope`.
- R-14. Existing delivery operations (stage, issue, pickup, get-by-SO) rely only on the tenant filter, so a super
  admin can act on another org's delivery. This is pre-existing; new A33 code must not copy it. Fixing it in passing
  is recommended but out of spec.
- R-15. Bulk assign touches many variants in one call. It filters by org explicitly, and updates only NULL routes in
  a single set-based update.

**Other**

- R-16. Tests that pin today's behaviour (Logistics state-machine tests, A29 sale-order delivery tests, A32 hold
  tests) will need deliberate updates for the C-2 fix and the auto steps. D-3(b) keeps the transition table
  untouched to minimise this.
- R-17. "Fulfillment mode" vs "fulfillment route" naming confuses users (C-8). Label them "Sourcing" and "Route".
