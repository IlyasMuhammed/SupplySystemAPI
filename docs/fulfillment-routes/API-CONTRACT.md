# Addendum 33 — API contract (binding for INV, DEM, FLOW, FE-ADMIN, FE-SO, FE-DLV, QA, REV)

Owner: LOG. Change requests go to LOG by message, and LOG edits this file. Last update: 2026-10-03 (v1.1: `customerName` on deliveries; REV-01 sweep selection, §5; REV-02 per-order lock, §9. v1.2: per-line route PUT, §5. v1.3: FLOW as built — approve result, nextActions order, REV-04, R-14 own-organization on every delivery operation, §6).
Decisions D-1…D-16 are in `ADDENDUM-33-TASKS.md`; LOG's own decisions (L-1…) are in §0.

Code that already encodes this contract (read it rather than re-deriving it):

| What | Where |
|---|---|
| Cross-module contracts (route lookup, variant routes, usage, delivery creator and canceller, step and source codes) | `src/SMS.Shared/Common/FulfillmentRouteContracts.cs` |
| Permission codes | `src/SMS.Shared/Authorization/PermissionCodes.cs` (`FULFILLMENT_ROUTE_VIEW/MANAGE/ASSIGN`, `DELIVERY_APPROVE`); frontend `P.` in `pages/pages.routes.ts` |
| Route entities, step enum, D-2 status map | `src/SMS.Modules.Logistics/Domain/FulfillmentRouteEntities.cs` (they join the EF model with LOG's migration, "SCHEMA READY") |
| Route DTOs | `src/SMS.Modules.Logistics/Models/FulfillmentRouteModels.cs` |
| Route service and seeder interfaces | `src/SMS.Modules.Logistics/Services/IFulfillmentRouteService.cs` |
| Frontend models and calls | `SupplyChainFrontend/src/app/services/fulfillment-routes.service.ts` (routes, step codes, labels, D-2 helpers); additions in `sale-order.service.ts`, `logistics.service.ts`, `inventory.service.ts`, `sale-order-config.service.ts` |

## 0. LOG decisions (on top of D-1…D-16)

| # | Decision | Why |
|---|---|---|
| L-1 | **One default per class**: at most one default route **with** SHIP (used for SHIP orders) and one **without** SHIP (used for SELF_PICKUP orders) per organization. The DB enforces it with a unique filtered index on `(OrganizationId, RequiresShipping) WHERE IsDefault = 1`. Seeds: PICK_AND_SHIP is the SHIP default (D-6), and PICK_ONLY is the SELF_PICKUP default. | D-4 says the org-default tier picks by header mode. With only one default (PICK_AND_SHIP), every existing SELF_PICKUP draft would resolve to a shipping route, need a shipping address, and stop confirming. That would break D-6's "non-breaking" promise. |
| L-2 | Steps must be a **subsequence of the canonical order** PICK, PACK, STAGE, APPROVAL, GOODS_ISSUE, SHIP. PICK and GOODS_ISSUE are required and always mandatory. This one rule covers BR-C1-05 (PICK first, GOODS_ISSUE before SHIP). | The delivery state machine only runs in this order (D-3 keeps the table unchanged). The editor's "drag to reorder" therefore only reorders within that constraint, so use toggles in fixed order. |
| L-3 | `IsMandatory` is stored. For steps other than PICK/GOODS_ISSUE it is **informational** in this release: a step in the route is always performed or auto-completed, never skipped at runtime. | Nothing in the spec defines runtime skipping, and D-3 replaces skipping with auto-completion. |
| L-4 | Route **code is immutable** after creation, for all routes. | Deliveries snapshot the code. |
| L-5 | A **default route can't be deactivated or deleted** (409: "make another route the default first, or clear the default"). Changing a default route's steps so that it gains or loses SHIP (which changes its class) is also a 409. | Keeps "defaults are always active" true, so the resolver never lands on an inactive default. |
| L-6 | **Delete** (custom routes only) is allowed only when nothing refers to the route: no variant, no open SO line, **and no delivery** (the snapshot uuid). Otherwise 409 "deactivate it instead". | BR-C1-06 / T-C1-09. History keeps its route. |
| L-7 | `IFulfillmentRouteUsage` has **one implementation per module that stores a route**: INV (active variants) and DEM (lines of DRAFT / CONFIRMED / PARTIALLY_FULFILLED orders, override or snapshot). Logistics injects `IEnumerable<IFulfillmentRouteUsage>`. | Logistics doesn't need DEM's new column at compile time, and no ordering between agents is needed. |
| L-8 | Delivery gains `FulfillmentRouteUuid`, `FulfillmentRouteCode`, `RouteSteps` (snapshot "PICK,PACK,GOODS_ISSUE,SHIP", D-10) **and `ApprovedBy`** (int?, D-7: the approve action knows the user; `ApprovedAt` already exists). Null route = legacy full path (R-1). | One Logistics migration for everything FLOW needs. |

## 1. Conventions (same as A29/A32)

- Every response is `ApiResponse<T>` `{ success, message, result }`. Routes take `{uuid:guid}`, and the API never exposes int ids.
- Status codes:
  - **400**: business rule (`BadRequestException`);
  - **403**: missing permission;
  - **404**: not in the caller's organization (another org's route, variant, order or delivery is a 404, **super admin included**);
  - **409**: state conflict, duplicate, "in use", or a lost race (`ConflictException`, `DbUpdateConcurrencyException`).
- Every new action carries `[RequirePermission(...)]` (`UngatedEndpointsRatchetTests`). No `[AllowAnonymous]`.
- Feature gates:
  - `api/fulfillment-routes` and every delivery endpoint are `[RequiresFeature("MODULE_LOGISTICS")]`;
  - SO endpoints stay `MODULE_DEMAND`;
  - variant endpoints stay `MODULE_INVENTORY`.
- **D-11:** when the org lacks MODULE_LOGISTICS there is no gate, no auto-create and no route column. DEM exposes this as `routesEnabled: false`.
- Step codes: `PICK | PACK | STAGE | APPROVAL | GOODS_ISSUE | SHIP`.
- Route source codes: `LINE_OVERRIDE | VARIANT | ORG_DEFAULT | NONE`.
- UI wording (R-17): the SO line's `fulfillmentMode` is labelled **"Sourcing"**, and the route is labelled **"Route"**.

## 2. Permissions (D-9)

| Code | Gates | Seeded to |
|---|---|---|
| `FULFILLMENT_ROUTE_VIEW` | routes settings page (route guard); routes GET (any-of, below) | System Admin, Org Admin (via `All`), **Inventory Manager** |
| `FULFILLMENT_ROUTE_MANAGE` | POST / PUT / PATCH / DELETE `api/fulfillment-routes…` | System Admin, Org Admin, **Inventory Manager** |
| `FULFILLMENT_ROUTE_ASSIGN` | `PUT api/variants/{uuid}/fulfillment-route`, `POST api/fulfillment-routes/{uuid}/assign-by-category` | System Admin, Org Admin, **Inventory Manager** |
| `DELIVERY_APPROVE` | `POST api/logistics/deliveries/{uuid}/approve` (D-7) | System Admin, Org Admin |

- Routes **reads** (`GET api/fulfillment-routes`, `GET …/{uuid}`) accept any of these: `FULFILLMENT_ROUTE_VIEW`, `FULFILLMENT_ROUTE_MANAGE`, `FULFILLMENT_ROUTE_ASSIGN`, `SALE_ORDER_VIEW`, `INVENTORY_VIEW`, `DELIVERY_VIEW`.
- The SO line override needs only the existing `SALE_ORDER_CREATE` / `SALE_ORDER_EDIT`.
- Inventory Manager is the built-in role holding `STOCK_MANAGE` (D-9: "admin-type roles holding SHIPPING_RULE_MANAGE/STOCK_MANAGE"; `SHIPPING_RULE_MANAGE` is held only by the two admins).
- Seeding is idempotent and matches roles by code.
- Role editor grouping: `FULFILLMENT_ROUTE_*` and `DELIVERY_APPROVE` sit under **Logistics**.
- Frontend:
  - route guard for Logistics → Fulfillment Routes: `permissionGuard(P.FULFILLMENT_ROUTE_VIEW, P.FULFILLMENT_ROUTE_MANAGE)`;
  - editor buttons: `hasPermission('FULFILLMENT_ROUTE_MANAGE')`;
  - variant route dropdown: editable with `FULFILLMENT_ROUTE_ASSIGN`, read-only otherwise;
  - Approve button: `DELIVERY_APPROVE`.

## 3. Fulfillment routes (owner LOG) — `api/fulfillment-routes`

### Shapes

```ts
FulfillmentRouteModel {
  uuid; code; name; description?;
  isDefault;            // default of its class: SHIP orders if requiresShipping, else SELF_PICKUP orders (L-1)
  isActive; isSystem; requiresPacking; requiresShipping; displayOrder;
  steps: { stepCode; label; stepOrder; isMandatory; description? }[];   // in step order
  stepsText;            // "Pick → Pack → Goods Issue → Ship"
  statusPath: string[]; // DRAFT … DELIVERED — the editor's preview line (§8)
  createdDate; modifiedDate?;
}
FulfillmentRouteStepRequest { stepCode; stepOrder; isMandatory = true; description? (≤200) }
CreateFulfillmentRouteRequest { code (≤30); name (≤100); description? (≤500); displayOrder? (default last+10); steps[] }
UpdateFulfillmentRouteRequest { name; description?; displayOrder; steps?: FulfillmentRouteStepRequest[] | null }  // null = unchanged
```

### Endpoints

| Method | Route | Permission | Result |
|---|---|---|---|
| GET | `api/fulfillment-routes?includeInactive=false` | any of the six read codes (§2) | `FulfillmentRouteModel[]`, by displayOrder then code |
| GET | `api/fulfillment-routes/{uuid}` | same | `FulfillmentRouteModel` (inactive too); 404 if not in the caller's org |
| POST | `api/fulfillment-routes` | `FULFILLMENT_ROUTE_MANAGE` | `FulfillmentRouteModel`. `isSystem=false`, `isDefault=false` (call set-default afterwards) |
| PUT | `api/fulfillment-routes/{uuid}` | `FULFILLMENT_ROUTE_MANAGE` | `FulfillmentRouteModel` |
| PATCH | `api/fulfillment-routes/{uuid}/deactivate` | `FULFILLMENT_ROUTE_MANAGE` | `FulfillmentRouteModel` |
| PATCH | `api/fulfillment-routes/{uuid}/activate` | `FULFILLMENT_ROUTE_MANAGE` | `FulfillmentRouteModel` |
| PATCH | `api/fulfillment-routes/{uuid}/set-default` | `FULFILLMENT_ROUTE_MANAGE` | `FulfillmentRouteModel`; the previous default **of the same class** is cleared in the same transaction (T-C1-07) |
| PATCH | `api/fulfillment-routes/{uuid}/clear-default` | `FULFILLMENT_ROUTE_MANAGE` | `FulfillmentRouteModel` (the class is then left with no default: lines with no other route block, D-6) |
| DELETE | `api/fulfillment-routes/{uuid}` | `FULFILLMENT_ROUTE_MANAGE` | `ApiResponse` (custom route that nothing refers to, L-6) |

### Rules and errors (service: `FulfillmentRouteService`)

| Rule | Status | Message (exact text may vary; tests match key words) |
|---|---|---|
| BR-C1-01 code unique per org (checked, plus a unique index for races) | 409 | "Fulfillment route code 'X' already exists in this organization." |
| Code format: ≤30, `^[A-Z0-9_]+$` after upper-casing; name required, ≤100; description ≤500; step description ≤200 | 400 | |
| At least one step; no unknown codes; no duplicate codes | 400 | "Unknown step 'X'…" / "Step 'X' appears more than once." |
| BR-C1-04 `stepOrder` 1..n with no gaps or duplicates | 400 | "Step order must run 1, 2, 3 … with no gaps." |
| BR-C1-03 / BR-C5-02 PICK and GOODS_ISSUE present | 400 | "A route must include the PICK step." / "…the GOODS_ISSUE step." |
| BR-C1-05 + L-2 canonical order (PICK first, GOODS_ISSUE before SHIP, …) | 400 | "PICK must be the first step." / "GOODS_ISSUE must come before SHIP." / "Steps must follow the order PICK, PACK, STAGE, APPROVAL, GOODS_ISSUE, SHIP." |
| PICK / GOODS_ISSUE `isMandatory=false` | silently forced to true | |
| BR-C1-09 `requiresPacking` = has PACK; `requiresShipping` = has SHIP (computed on every save; never accepted from the client) | — | |
| D-10 system route steps locked (sending different steps) | 400 | "The steps of a system route can't be changed." |
| L-5 default route changing class via steps | 409 | "…is the default route for SHIP orders; …make another route the default first." |
| BR-C1-07 deactivate while in use (asks every `IFulfillmentRouteUsage`) | 409 | "'X' is still used by 3 active product variants and 2 open sale order lines. Reassign them first." |
| L-5 deactivate / delete a default | 409 | |
| set-default on an inactive route | 400 | |
| BR-C1-06 delete a system route | 409 | "System routes can't be deleted. Deactivate it instead." |
| L-6 delete a route in use or carried by any delivery | 409 | |
| Concurrent edit (RowVersion) | 409 | |

Concurrency: set-default runs in a transaction under `sp_getapplock('fulfillment-route-default:{orgId}')`, and the unique filtered index is the backstop. Deactivation versus a concurrent assignment isn't atomic across modules; DEM's gate re-checks at confirm (R-8).

### Seeds (D-6, L-1). `IFulfillmentRouteSeeder`: provisioning handler plus a startup backfill, matched by code, never touching an existing one

| Code | Name | Steps | Default | DisplayOrder |
|---|---|---|---|---|
| PICK_ONLY | Pick Only | PICK, GOODS_ISSUE | yes, for SELF_PICKUP orders | 10 |
| PICK_AND_SHIP | Pick & Ship | PICK, GOODS_ISSUE, SHIP | yes, for SHIP orders | 20 |
| PICK_PACK_SHIP | Pick, Pack & Ship | PICK, PACK, GOODS_ISSUE, SHIP | no | 30 |

A seed is made default only if its class has no default yet. All three get `isSystem=true`.

## 4. Variant route (owner INV)

- `ProductVariantModel` (every variant read: product detail, `api/products/{id}/variants`, …) gains:
  - `fulfillmentRouteUuid?`;
  - `fulfillmentRouteCode?`;
  - `fulfillmentRouteName?`.
- `PATCH api/variants/{uuid}` **ignores** route fields (it's ungated, C-9).
- `PUT api/variants/{uuid}/fulfillment-route`:
  - permission `FULFILLMENT_ROUTE_ASSIGN`; body `{ fulfillmentRouteUuid: string | null }` (null clears it, T-C2-04);
  - returns `ApiResponse<VariantFulfillmentRouteModel { variantUuid, fulfillmentRouteUuid?, fulfillmentRouteCode?, fulfillmentRouteName? }>`;
  - 404: variant not in the caller's org;
  - 400: route unknown or another org's (T-C2-02, "route must belong to your organization");
  - 400: route inactive (BR-C2-01).
  - Validated through `IFulfillmentRouteLookup.GetAsync(orgId, [uuid])`.
- `POST api/fulfillment-routes/{uuid}/assign-by-category`:
  - permission `FULFILLMENT_ROUTE_ASSIGN`, mounted by Inventory;
  - body `{ categoryId: number, subCategoryId?: number }`;
  - sets the route on every **active variant with a NULL route** of active products in the category (all its sub-categories, or only `subCategoryId`) (BR-C2-02, D-14, R-15);
  - returns `ApiResponse<{ updated: number, skipped: number, total: number }>` (skipped = already had a route);
  - 404: route not in the org;
  - 400: inactive route, or a category or sub-category that is unknown or doesn't belong to the category.
- `IVariantFulfillmentRoutes` and `IFulfillmentRouteUsage` ("active product variants") are implemented in Inventory.

## 5. Sale orders (owner DEM)

### Line and order fields

- `SaleOrderLineRequest` (`POST api/sale-orders`, `PUT api/sale-orders/{uuid}`, DRAFT only) gains `fulfillmentRouteUuid?: string | null`, the line override; null means inherit.
  - 400 if the route is unknown, another org's, or inactive (BR-C3-01).
  - The orders are rebuilt wholesale, so send it back on every update.
- **`PUT api/sale-orders/{uuid}/lines/{lineUuid}/fulfillment-route`** (v1.2, lead's request: changing a route must not re-price a draft):
  - permission `SALE_ORDER_EDIT`; body `{ fulfillmentRouteUuid: string | null }` (null = inherit);
  - DRAFT only (400 otherwise); 404 for another org's order or line; 400 for an unknown, other-org or inactive route (BR-C3-01);
  - no re-pricing and no line rebuild; runs under the per-order lock;
  - returns `ApiResponse<SaleOrderLineModel>` with the recomputed `effectiveRoute*`, `routeSource` and `routeBlocker`;
  - frontend: one method in `SaleOrderService` (FE-SO owns which name stays). Use it for a route change on a saved draft; the full PUT still accepts `fulfillmentRouteUuid` per line.
- `SaleOrderLineModel` gains these fields, computed live while DRAFT and taken from the D-16 snapshot afterwards:
  - `fulfillmentRouteUuid?`: the override while DRAFT; the confirmed route afterwards;
  - `effectiveRouteUuid?`, `effectiveRouteCode?`, `effectiveRouteName?`, `effectiveRouteSteps: string[]`;
  - `routeSource: 'LINE_OVERRIDE' | 'VARIANT' | 'ORG_DEFAULT' | 'NONE'`;
  - `routeBlocker?: ConfirmBlockerCode`.
- `SaleOrderModel` (detail) gains:
  - `routesEnabled: boolean` (D-11);
  - `confirmBlockers: ConfirmBlockerModel[]` (empty unless DRAFT).
- `ConfirmBlockerModel { lineUuid?, lineNumber?, code, message }`, where `code` is one of:
  - `ROUTE_MISSING`;
  - `ROUTE_INACTIVE`;
  - `ROUTE_UNKNOWN` (deleted or another org's);
  - `SHIPPING_ADDRESS_REQUIRED` (D-4: some line routes to SHIP and the order has no shipping address);
  - `SELF_PICKUP_DISABLED` (D-4: a route without SHIP while `SelfPickupEnabled` is off).
- `lineNumber` is the 1-based position by line `Id` ("Line N").
- **Exempt (D-5):** DROP_SHIP lines need no route, and none is shown for them (`routeSource: 'NONE'`, no blocker).
- Resolution (BR-C3-03, D-4) has four tiers, in order:
  1. line override;
  2. the variant's route (`IVariantFulfillmentRoutes`);
  3. `IFulfillmentRouteLookup.GetOrgDefaultsAsync(org).For(order.deliveryMode)`;
  4. NONE.

### Confirm gate: `POST api/sale-orders/{uuid}/confirm` (SALE_ORDER_CONFIRM, unchanged route)

- It runs under `SaleOrderHolds.OneChangeAtATimeAsync` (R-6). The gate runs **before** `CheckAndReserveAsync`.
- On any blocker → **400**. The message is the blocker messages joined with `\n`:
  1. the per-line route problems first, in line order: `"Line 3: fulfillment route 'X' is inactive."`;
  2. then all missing lines together: `"Cannot confirm: lines 2, 4 have no fulfillment route. Assign a route on each line or set a default route on the product variant."` (T-C3-06/07).
- On success, the route and source are snapshotted onto each line (D-16), the confirm commits, and then (D-1, if `autoCreateDeliveriesOnConfirm` and Logistics is enabled) DEM calls `ISaleOrderDeliveryCreator`.
  - The call is made **after** `OneChangeAtATimeAsync` has committed, **outside** the lock (REV-02, §9).
  - It is best effort: it is logged and gets a timeline event, and the D-12 sweep and §6 recovery cover failures.
- **D-12 sweep selection (REV-01, v1.1).** DEM adds `SaleOrder.DeliveryCreationPendingSince datetime2 NULL`.
  - It is set (UTC now) in the confirm's own commit whenever auto-create applies.
  - It is cleared (null) as soon as a creator call **returns without throwing** for the order, whoever made the call: confirm, the sweep, or the recovery button. It is cleared even if that call created nothing.
  - Cancel clears it too.
  - The sweep (Hangfire, `HangfireTenantScope`, iterating orgs) picks only orders that meet all of these:
    1. `DeliveryCreationPendingSince` is not null and older than **10 minutes** (so it doesn't race the confirm's own call);
    2. the status is CONFIRMED / PARTIALLY_FULFILLED;
    3. the org still has `autoCreateDeliveriesOnConfirm` and MODULE_LOGISTICS.
  - It passes the lines' D-16 snapshots.
  - A delivery that a user cancelled or short-closed is therefore **never re-created by the sweep**. Only the explicit "Create deliveries" button creates for outstanding quantity again.
- Response: `ApiResponse<SaleOrderConfirmResultModel>`. This is additive, so the old FE still works.

```ts
SaleOrderConfirmResultModel {
  status: string;                          // CONFIRMED
  deliveries: CreatedSaleOrderDeliveryModel[];  // { deliveryUuid, deliveryNumber, routeUuid, routeCode, deliveryMode, shipFromWarehouseUuid?, lineCount }
  skippedLines: { soLineUuid, reason }[];
  deliveryCreationFailed: boolean;         // true → show deliveryMessage + the "Create deliveries" recovery button
  deliveryMessage?: string;
}
```

- After confirm, the line route is locked: a PUT on a non-DRAFT order is already refused (T-C3-08, 400).

### Delivery preview (BR-C3-05, not persisted)

| Method | Route | Permission | Body |
|---|---|---|---|
| GET | `api/sale-orders/{uuid}/delivery-preview` | `SALE_ORDER_VIEW` | for a saved order |
| POST | `api/sale-orders/delivery-preview` | any of `SALE_ORDER_CREATE`, `SALE_ORDER_EDIT`, `SALE_ORDER_VIEW` | `{ saleOrderUuid?, deliveryMode, shippingAddressId?, lines: { variantUuid, quantity, fulfillmentRouteUuid? }[] }` for the unsaved form; it also drives the form's Route column |

```ts
SaleOrderDeliveryPreviewModel {
  routesEnabled: boolean;
  canConfirm: boolean;                     // no blockers
  deliveryCount: number;                   // = groups.length
  lines: { lineUuid?, lineNumber, variantUuid, itemDescription?, quantity, fulfillmentRouteUuid?,
           effectiveRouteUuid?, effectiveRouteCode?, effectiveRouteName?, effectiveRouteSteps: string[],
           routeSource, routeBlocker? }[];
  groups: { routeUuid, routeCode, routeName, steps: string[], stepsText, requiresShipping,
            deliveryMode /* SHIP | SELF_PICKUP, from the route (D-4) */,
            warehouseUuid?, warehouseName? /* only once reservations exist */,
            lineNumbers: number[] }[];
  blockers: ConfirmBlockerModel[];
}
```

### Other order changes

- Cancel, `POST api/sale-orders/{uuid}/cancel` (SALE_ORDER_CANCEL, unchanged route):
  - DEM calls `ISaleOrderDeliveryCanceller.CancelOpenAsync` **before** releasing the order's holds (R-7, D-15);
  - the response becomes `ApiResponse<{ cancelledDeliveries: SaleOrderDeliveryRef[], issuedDeliveries: SaleOrderDeliveryRef[] }>`, where `SaleOrderDeliveryRef` is `{ deliveryUuid, deliveryNumber, status }`;
  - issued deliveries are also listed on the timeline.
- Sale order settings (`api/sale-order-config`):
  - `autoCreateDeliveriesOnConfirm: boolean` (D-1, default **true**);
  - in the PUT it is `bool?`, where **null keeps the current value**, so an older client can't switch it off by omission.
- DEM implements `IFulfillmentRouteUsage`, counted as "open sale order lines": lines of DRAFT / CONFIRMED / PARTIALLY_FULFILLED orders whose stored `FulfillmentRouteUuid` is the route.

## 6. Deliveries (owner FLOW; columns by LOG)

Schema (LOG's migration): `delivery_orders` gains these columns:
- `FulfillmentRouteUuid uniqueidentifier NULL`, with a filtered index;
- `FulfillmentRouteCode nvarchar(30) NULL`;
- `RouteSteps nvarchar(100) NULL` (snapshot, D-10; read with `FulfillmentStepCode.Parse`);
- `ApprovedBy int NULL`.

Null route = the legacy full path, today's behaviour exactly (R-1).

### Models

- `DeliveryListItemModel` gains:
  - `saleOrderUuid?`;
  - `fulfillmentRouteUuid?`, `fulfillmentRouteCode?`, `fulfillmentRouteName?` (live name from the route table);
  - `lineSummary?` (e.g. "Steel Pipes × 500, +2 more");
  - `customerName?`: the sale order's partner name, null for other sources (FE-DLV request, v1.1).
- `DeliveryDetailModel` gains:
  - `customerName?` (as on the list item);
  - `fulfillmentRouteUuid?`, `fulfillmentRouteCode?`, `fulfillmentRouteName?`;
  - `routeSteps: RouteStepProgressModel[]`. This is empty when there is no route; then the UI shows today's status tag and no tracker;
  - `requiresApproval: boolean` (the route has APPROVAL);
  - `approvedAt?`, `approvedBy?`;
  - `nextStep?: string` (the CURRENT step code);
  - `nextActions: DeliveryNextAction[]`.
  - Keep `allowedNextStatuses` as it is.
- `RouteStepProgressModel { stepCode: StepCode | 'COMPLETE', label, state: 'DONE' | 'CURRENT' | 'PENDING' }`:
  - only the route's steps, then a final `COMPLETE` ("Complete"), as in T-C5-09/10;
  - skipped steps never appear (BR-C5-04).
- `DeliveryNextAction` takes one of these values:
  - `RELEASE`, `GENERATE_PICK_LIST`, `CONFIRM_PICK`, `PACK`, `STAGE`, `APPROVE`, `GOODS_ISSUE`;
  - `CREATE_CONSIGNMENT`, `RECORD_COLLECTION`;
  - `HOLD`, `RESUME`, `CANCEL`, `SHORT_CLOSE`.
  It only lists what the route, the status and the data allow. Buttons are still gated by the operation's own permission.
  **Order (v1.3, as FLOW built it, agreed with REV and QA):**
  1. The first forward action is the route's next step.
  2. `RECORD_COLLECTION` comes second, for a collected delivery (a route without SHIP, or a null route with SELF_PICKUP) at PACKED / STAGED / PENDING_APPROVAL when no D-7 approval is pending.
  3. Then `HOLD`, `RESUME`, `SHORT_CLOSE` and `CANCEL`, from the state machine.
- **REV-04 (v1.3):** an auto-staged delivery (a route without STAGE) keeps its packages amendable (PATCH / void) until it is approved, on a live consignment, or issued. Voiding a carton steps it back to PICKED.
- `DeliveryFilter` (`GET api/logistics/deliveries`, DELIVERY_VIEW) gains `saleOrderUuid?` and `fulfillmentRouteUuid?`. `sourceType=SALE_ORDER` already works.
- `GET api/sale-orders/{uuid}/deliveries` (DELIVERY_VIEW, existing): items carry the new list fields, oldest first.

### Endpoints

| Method | Route | Permission | Behaviour |
|---|---|---|---|
| POST | `api/sale-orders/{uuid}/create-deliveries` | `DELIVERY_CREATE` | Recovery (D-12 button): calls `ISaleOrderDeliveryCreator` with the lines' confirmed snapshots → `ApiResponse<SaleOrderDeliveryCreationResult>` `{ created: CreatedSaleOrderDeliveryModel[], skipped: { soLineUuid, reason }[] }`. Lines with no snapshot (orders confirmed before A33) are skipped with a reason; use the existing `create-delivery`. 400 unless CONFIRMED / PARTIALLY_FULFILLED; 404 other org. Idempotent. |
| POST | `api/logistics/deliveries/{uuid}/approve` | `DELIVERY_APPROVE` | D-7. Sets `ApprovedAt/By` on a **STAGED** delivery whose route has APPROVAL → `ApiResponse<DeliveryApprovalModel { deliveryUuid, approvedAt, approvedBy?, alreadyApproved }>`. 400 if not STAGED or the route has no APPROVAL; 200 with `alreadyApproved: true` if already approved. Goods issue on such a delivery is refused (400) until approved. |
| POST | `api/logistics/deliveries/{uuid}/advance` | any of `DELIVERY_EDIT`, `DISPATCH`, `DELIVERY_APPROVE`, and then **the operation's own code** is checked (403) | PE-02. Body `{ expectedStatus?: string }` (409 if stale). Performs the one next step that needs no input: DRAFT → release (DELIVERY_EDIT); PACKED → stage (DISPATCH); STAGED + APPROVAL not approved → approve (DELIVERY_APPROVE); STAGED / PENDING_APPROVAL → goods issue (DISPATCH). If the next step needs input, it returns **409** naming it: confirm the pick, pack at the pack station, create a consignment, record collection. Returns `{ previousStatus, status, action }`. |
| POST | `api/logistics/deliveries/{uuid}/pickup` (existing, DISPATCH) | — | **Record collection**. Refused (400) when the delivery's route has SHIP. |
| POST | consignment creation (existing) | — | Refused (400) for a delivery whose route lacks SHIP. |
| POST | `api/logistics/deliveries/{uuid}/cancel` (existing, DELIVERY_EDIT) | — | Unchanged and route-independent (BR-C5-05 as the repo has it: before GOODS_ISSUED). Add an own-org filter. |

**R-14 (v1.3, FLOW):** every delivery operation now reads and writes only the caller's own organization's records, so another organization's delivery, pick list, package, consignment or source document is a **404, super admin included**, and nothing changes. This covers:
- the delivery list (also `GET api/sale-orders/{uuid}/deliveries`), detail, PATCH and DELETE;
- availability and release;
- the pick list: generate, get by delivery or uuid, list, assign, cancel and confirm;
- packages: pack, list, get, PATCH and void;
- stage, approve, advance, goods issue, and pickup (record collection);
- consignment create, attach, get and manual booking, and its carrier;
- hold, resume, cancel and short close;
- creating a delivery from a PO, SRO, MIV or sale order (`create-delivery` / `from-source`).

Consignment creation now checks every listed delivery (own and existing → else 404; route has SHIP → else 400) **before** the consignment row exists, so a refusal leaves no empty consignment. Background paths that work across organizations take the organization explicitly instead: the A33 creator and canceller, and the carrier tracking and webhook services, which don't use these operations.

## 7. Route-aware operations (owner FLOW; D-3 auto-complete)

| Operation | Route without… | Does |
|---|---|---|
| Confirm pick (`PickListRepository.CompleteAsync`) | PACK | Boxes each picked line into a LOOSE handling unit → PACKED (a system action on the timeline/audit) |
| Pick or pack reaching PACKED | STAGE | Auto-stage → STAGED |
| Goods issue | (route has APPROVAL) | Refused until `ApprovedAt` is set |
| Create consignment | SHIP | Refused |
| Record collection | (route has SHIP) | Refused |
| Any | null route | Today's behaviour, unchanged |

## 8. D-2 / D-3 status mapping

Steps → the real delivery statuses they cover:

| Step | Statuses | When the step is not in the route |
|---|---|---|
| PICK | RELEASED, PICKING, PICKED | never absent |
| PACK | PACKED | auto: confirming the pick boxes into LOOSE units → PACKED |
| STAGE | STAGED | auto: staged as soon as PACKED |
| APPROVAL | (action on STAGED, sets `ApprovedAt`); PENDING_APPROVAL if the workflow engine is used | nothing to do; GI is allowed straight from STAGED |
| GOODS_ISSUE | GOODS_ISSUED | never absent |
| SHIP | IN_TRANSIT, PARTIALLY_DELIVERED, DELIVERED (from the consignment) | "Record collection" takes GOODS_ISSUED → DELIVERED (SELF_PICKUP) |
| spec COMPLETED | **DELIVERED** (then CLOSED) | every route ends at DELIVERED, so SO fulfilment and invoicing work (C-5) |

When each tracker step counts as **DONE**. For ON_HOLD, use `statusBeforeHold`.

| Step | DONE once the status is at or past |
|---|---|
| PICK | PICKED |
| PACK | PACKED |
| STAGE | STAGED |
| APPROVAL | `ApprovedAt` set, or GOODS_ISSUED |
| GOODS_ISSUE | GOODS_ISSUED |
| SHIP | DELIVERED (CURRENT while IN_TRANSIT / PARTIALLY_DELIVERED) |
| COMPLETE | DELIVERED / CLOSED |

- CURRENT is the first step that isn't DONE; the rest are PENDING.
- CANCELLED / SHORT_CLOSED: the tracker stays where it was, and the status tag says why.

Seeded routes, as each sees the delivery (tracker shows only the listed steps):

| Status | PICK_ONLY (Pick → Goods Issue → Complete) | PICK_AND_SHIP (Pick → Goods Issue → Ship → Complete) | PICK_PACK_SHIP (Pick → Pack → Goods Issue → Ship → Complete) |
|---|---|---|---|
| DRAFT / RELEASED / PICKING | Pick ● | Pick ● | Pick ● |
| PICKED | (passes straight to STAGED by auto-pack + auto-stage) | (same) | Pack ● |
| PACKED | (auto-stage) | (auto-stage) | (auto-stage) → Goods Issue ● |
| STAGED | Goods Issue ● | Goods Issue ● | Goods Issue ● |
| GOODS_ISSUED | Complete ● (Record collection) | Ship ● (create consignment) | Ship ● |
| IN_TRANSIT / PARTIALLY_DELIVERED | — | Ship ● | Ship ● |
| DELIVERED / CLOSED | Complete ✓ | Complete ✓ | Complete ✓ |

Spec test mapping:
- T-C5-01/03: PICKED → next GOODS_ISSUED. This means the next **route** step is GOODS_ISSUE; the delivery passes PACKED/STAGED automatically.
- T-C5-02: GOODS_ISSUED → COMPLETED = DELIVERED via Record collection.
- T-C5-04: GOODS_ISSUED → IN_TRANSIT.
- T-C5-06: PACKED → GOODS_ISSUE, with auto-stage.
- T-C5-07: PACKED → STAGED.

## 9. Shared contracts (SMS.Shared, `FulfillmentRouteContracts.cs`)

| Interface | Implemented by | Used by |
|---|---|---|
| `IFulfillmentRouteLookup` `GetAsync(orgId, uuids)`, `GetOrgDefaultsAsync(orgId)` → `FulfillmentRouteDefaults{Shipping, NonShipping}.For(mode)`, `ListActiveAsync(orgId)` | Logistics (LOG) | INV, DEM |
| `IVariantFulfillmentRoutes` `GetRouteUuidsAsync(orgId, variantUuids)` | Inventory (INV) | DEM |
| `IFulfillmentRouteUsage` `CountUsageAsync(orgId, routeUuid)` → `{ description, count }` | INV, DEM (one each, L-7) | LOG |
| `ISaleOrderDeliveryCreator` `CreateForConfirmedOrderAsync(orgId, soUuid, lineRoutes, userId)` → `{ created[], skipped[] }`; idempotent, per-order lock, route × warehouse, D-8 | Logistics (FLOW) | DEM (confirm, D-12 sweep), FLOW (recovery endpoint) |
| `ISaleOrderDeliveryCanceller` `CancelOpenAsync(orgId, soUuid, reason, userId)` → `{ cancelled[], alreadyIssued[] }`; idempotent | Logistics (FLOW) | DEM (cancel) |
| `ISaleOrderDeliveryQuantities` (C-2 fix: **add** a "held on released deliveries" method, don't change the existing one) | Logistics (FLOW) | DEM (`SaleOrderHolds`) |

Every method takes the organization explicitly and filters on it (R-12, R-13); another org's records read as absent. All are optional-safe: a consumer resolves them with `GetService` / optional constructor parameters, and treats a missing one as "routes off" (D-11).

**Per-order locking (REV-02, v1.1).** There is one lock: `SaleOrderLocks.HoldsResource(soUuid)` in SMS.Shared, which is `demand.sale_orders/{uuid:N}/holds`, the same string `SaleOrderHolds.LockResource` uses. DEM should switch to calling it. It is `sp_getapplock`, Exclusive, owner Transaction, with `SaleOrderLocks.TimeoutMilliseconds` = 15 s.
- **Creator (FLOW).**
  - Opens its own transaction on the Logistics connection and takes the lock **first**.
  - Then re-reads the order status and lines, and outstanding quantities, under the lock. A read through `DemandDbContext` after the lock is granted sees the committed state.
  - Creates nothing if the order is no longer CONFIRMED / PARTIALLY_FULFILLED.
  - Commits, which releases the lock.
  - Skip the lock on non-relational providers (unit tests), as `TaxCodeService.OneSaveAtATimeAsync` does.
- **Canceller (FLOW)** must **not** take the lock. DEM's cancel already holds it on Demand's connection, so taking it from Logistics would wait 15 s and fail.
- **DEM** calls the creator only **outside** the lock (after confirm commits; from the sweep). It calls the canceller **inside** its cancel's lock.
- QA host test: confirm and cancel the same order concurrently, then expect no non-cancelled delivery on a CANCELLED order.
