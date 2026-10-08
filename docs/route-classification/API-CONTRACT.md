# Addendum 34 — API contract (binding for INV, MFG, DEM, FE-ADMIN, FE-SALES, QA, REV)

Owner: **LOG**. Change requests go to LOG by message; LOG edits this file. **v1.4**, 2026-10-04 (change log at the end).
Decisions D-1…D-29 are in `ADDENDUM-34-TASKS.md`; design in `ADDENDUM-34-ANALYSIS.md` §4. The A33 contract
(`docs/fulfillment-routes/API-CONTRACT.md` v1.3) still holds; this file lists only what A34 adds or changes.

| What | Where |
|---|---|
| Cross-module contracts (category codes, calculator, BOM reader, readiness, production service, production delivery creator, feedback, supplier lead lookup, notification types, component/source codes) | `src/SMS.Shared/Common/RouteClassificationContracts.cs` |
| `FulfillmentRouteSummary.Category` (init-only, default STOCK) | `src/SMS.Shared/Common/FulfillmentRouteContracts.cs` |
| Permission code `LEAD_TIME_DEFAULTS_MANAGE` | `src/SMS.Shared/Authorization/PermissionCodes.cs`; frontend `P.LEAD_TIME_DEFAULTS_MANAGE` in `pages/pages.routes.ts` |

## 1. Conventions (unchanged from A33 §1)

- Every response is `ApiResponse<T>` `{ success, message, result }`. JSON is camelCase. Routes take `{uuid:guid}`; no int ids
  are exposed except the existing `productId`/`categoryId`.
- **400** business rule · **403** missing permission · **404** not in the caller's organization (super admin included)
  · **409** state conflict / in use / lost race.
- Every new action carries `[RequirePermission]` (ratchet test). No `[AllowAnonymous]`.
- **Date-only fields** (`…Date` without a time meaning: `calculatedDeliveryDate`, `manualDeliveryDate`,
  `effectiveDeliveryDate`, `earliestDeliveryDate`, `latestStartDate`, `requestedDate`, `estimatedDeliveryDate`,
  `promisedDeliveryDate`, `requiredDate`, `plannedStartDate`) travel as `"yyyy-MM-dd"` in requests and come back as
  `"yyyy-MM-ddT00:00:00"` (no `Z`). The frontend must use the shared date-only helpers (no UTC shift).
  Timestamps (`leadTimeCalculatedAt`, `calculatedAt`, `…PendingSince`, `modifiedDate`) are UTC datetimes.
- Feature gates: routes and deliveries `MODULE_LOGISTICS`; `api/lead-time/*` and variant endpoints `MODULE_INVENTORY`;
  sales endpoints `MODULE_DEMAND`; production orders `MODULE_MANUFACTURING`. **Make-to-order needs both
  MODULE_LOGISTICS (routes, A33 D-11) and MODULE_MANUFACTURING (D-9).**
- Codes used everywhere:
  - route category `STOCK | MANUFACTURE | BUY | DROPSHIP` (BUY/DROPSHIP reserved, refused);
  - lead-time component `SUPPLIER | MANUFACTURING | MFG_BUFFER | QC | TRANSFER | PICK_PACK | SHIPPING | SALES_BUFFER`;
  - lead-time source `VARIANT | ORG_DEFAULT | SUPPLIER_RATE | SUPPLIER_RECORD | PRODUCT | BOM | IN_STOCK | SYSTEM_DEFAULT`;
  - delivery-date source `CALCULATED | MANUAL | NONE`;
  - SO line `fulfillmentMode` gains `MAKE_TO_ORDER`.
- UI wording (R-15): `fulfillmentMode = MAKE_TO_ORDER` is labelled **"Make to order"** under "Sourcing"; the route
  category is a tag ("Manufacture" / "Stock"), never shown as "Sourcing".

## 2. Permissions (D-24)

| Code | Gates | Seeded to (by role code) | Role-editor group |
|---|---|---|---|
| `LEAD_TIME_DEFAULTS_MANAGE` (**new**) | `PUT api/lead-time/defaults`; also a read code (below) | System Admin, Org Admin (both via `All`), Inventory Manager, Supply Dept Admin | Inventory |

Every other endpoint reuses existing codes:

| Endpoint group | Permission |
|---|---|
| Route reads / writes (incl. `routeCategory`, `?category=`) | unchanged A33 §2: reads any of `FULFILLMENT_ROUTE_VIEW/MANAGE/ASSIGN`, `SALE_ORDER_VIEW`, `INVENTORY_VIEW`, `DELIVERY_VIEW`; writes `FULFILLMENT_ROUTE_MANAGE` |
| `GET api/lead-time/defaults`, `GET api/variants/{uuid}/lead-times` | any of `LEAD_TIME_DEFAULTS_MANAGE`, `INVENTORY_VIEW`, `STOCK_MANAGE`, `SALE_ORDER_VIEW` |
| `PUT api/variants/{uuid}/lead-times` | `STOCK_MANAGE` |
| `POST api/lead-time/calculate`, `…/calculate-manufacturing` | any of `SALE_ORDER_VIEW`, `SALE_ORDER_CREATE`, `SALE_ORDER_EDIT`, `SALE_INQUIRY_VIEW`, `SALE_INQUIRY_EDIT`, `SALE_QUOTATION_VIEW`, `SALE_QUOTATION_EDIT`, `INVENTORY_VIEW`, `STOCK_MANAGE` |
| Inquiry / quotation / SO line `…/lead-time` | `SALE_INQUIRY_EDIT` / `SALE_QUOTATION_EDIT` / `SALE_ORDER_EDIT` |
| `PUT api/sale-orders/{uuid}/lines/{lineUuid}/delivery-date` | `SALE_ORDER_EDIT` |
| `POST api/sale-orders/{uuid}/create-production-orders` | any of `SALE_ORDER_CONFIRM`, `PROD_CREATE` |
| `POST api/production-orders/{uuid}/create-delivery` | `DELIVERY_CREATE` |
| SO detail `productionOrders[]` | `SALE_ORDER_VIEW` (the detail's own code; no `PROD_VIEW` needed) |

Frontend guards and buttons (server = guard):
- Lead Time Defaults page: `permissionGuard(P.LEAD_TIME_DEFAULTS_MANAGE, P.INVENTORY_VIEW)`; Save button
  `hasPermission('LEAD_TIME_DEFAULTS_MANAGE')`, read-only otherwise.
- Lead Times tab: visible with the product detail; editable with `STOCK_MANAGE`, read-only otherwise.
- ⏱ Calculate on a line: the document's EDIT code (unsaved forms call `POST api/lead-time/calculate`).
- "Create production orders": `SALE_ORDER_CONFIRM` or `PROD_CREATE`. "Create delivery now": `DELIVERY_CREATE`.

## 3. Fulfillment routes (owner LOG) — `api/fulfillment-routes`

### Shape changes

```ts
FulfillmentRouteModel { …A33 fields…; routeCategory: 'STOCK' | 'MANUFACTURE' | 'BUY' | 'DROPSHIP' }   // existing rows: STOCK
CreateFulfillmentRouteRequest { …A33 fields…; routeCategory?: string }   // omitted / null / blank = STOCK; trimmed, upper-cased
UpdateFulfillmentRouteRequest { …A33 fields…; routeCategory?: string }   // omitted / null = unchanged
```

`GET api/fulfillment-routes?includeInactive=false&category=MANUFACTURE` — `category` optional, one code,
case-insensitive; an unknown code is a **400** ("Unknown route category 'X'. Categories are STOCK, MANUFACTURE, BUY,
DROPSHIP."). The list never hides MANUFACTURE routes; **the frontend hides MANUFACTURE routes from pickers, the
category dropdown and the variant dropdown when the org lacks MODULE_MANUFACTURING** (D-9), and the server refuses
them where it matters (assignment §4.2, confirm §6.1).

### Rules and errors (`FulfillmentRouteService`)

Checks run in this order. Messages are exact; tests should match the key words.

| Rule | When | Status | Message |
|---|---|---|---|
| BR-C1-01 unknown category | create / update | 400 | `Unknown route category 'X'. Categories are STOCK, MANUFACTURE, BUY, DROPSHIP.` |
| D-7 reserved category | create / update to BUY or DROPSHIP | 400 | `Route category 'BUY' is not yet available. Use STOCK or MANUFACTURE.` |
| D-8 system route | update that **changes** the category | 409 | `'X' is a system route: its category can't be changed. Create a custom route instead.` |
| D-8 / D-6 default route | update that changes the category | 409 | `'X' is the default route for SHIP orders: its category can't be changed. Make another route the default first, or clear the default.` (SELF_PICKUP for a route without SHIP) |
| D-8 in use | update that changes the category, any `IFulfillmentRouteUsage` count > 0 (variants, open SO lines, open production orders) | 409 | `'X' is still used by 3 active product variants and 1 open production orders: its category can't be changed. Reassign them first.` |
| D-6 set-default on MANUFACTURE | `PATCH …/set-default` | 400 | `'X' is a make-to-order (MANUFACTURE) route and can't be a default: every line without its own route would be sent to production. Assign it to the variants that are made to order instead.` |

- Sending the current category again is not a change and always passes.
- A MANUFACTURE route may have any valid steps, including none of SHIP (a make-to-order route for SELF_PICKUP
  orders, C-16 / R-17).
- `isDefault` is never true on a MANUFACTURE route.
- Frontend: disable the category dropdown on system and default routes (known from the model); for other routes,
  let the save fail and show the 409 message (usage is not on the model).

### Seeds (D-9) — `IFulfillmentRouteSeeder`, provisioning handler and startup backfill, matched by code

| Code | Name | Category | Steps | Default | Order | System |
|---|---|---|---|---|---|---|
| PICK_ONLY | Pick Only | STOCK | PICK, GOODS_ISSUE | SELF_PICKUP class, if none | 10 | yes |
| PICK_AND_SHIP | Pick & Ship | STOCK | PICK, GOODS_ISSUE, SHIP | SHIP class, if none | 20 | yes |
| PICK_PACK_SHIP | Pick, Pack & Ship | STOCK | PICK, PACK, GOODS_ISSUE, SHIP | no | 30 | yes |
| **MFG_PICK_SHIP** | Manufacture → Pick & Ship | **MANUFACTURE** | PICK, GOODS_ISSUE, SHIP | **never** | 40 | yes |
| **MFG_PICK_PACK_SHIP** | Manufacture → Pick, Pack & Ship | **MANUFACTURE** | PICK, PACK, GOODS_ISSUE, SHIP | **never** | 50 | yes |

Every org gets all five (T-C1-04), whatever its modules. An org that already owns a route with one of these codes
keeps its own route unchanged (category included) and gets no seed (R-1).

## 4. Variants, products and lead times (owner INV)

### 4.1 Variant model

`ProductVariantModel` (every variant read) gains:

```ts
fulfillmentRouteCategory?: 'STOCK' | 'MANUFACTURE';   // the variant's own route's category; null when the variant has no route
isMakeToOrder: boolean;                               // fulfillmentRouteCategory === 'MANUFACTURE' → "Make to order" tag (D-4)
// stored overrides, read-only here (edited through §4.4); null = use the default
supplierLeadTimeDays?: number;        // = ProductVariant.LeadTimeDays (D-11)
manufacturingLeadTimeDays?: number;   manufacturingBufferDays?: number;
qualityInspectionDays?: number;       internalTransferDays?: number;
pickPackDays?: number;                shippingLeadTimeDays?: number;   salesBufferDays?: number;
```

`PATCH api/variants/{uuid}` keeps ignoring route fields and also ignores these eight.

### 4.2 Route assignment (D-3, D-9)

`PUT api/variants/{uuid}/fulfillment-route` (FULFILLMENT_ROUTE_ASSIGN, unchanged shape) and
`POST api/fulfillment-routes/{uuid}/assign-by-category` gain, for a **MANUFACTURE** route:

| Rule | Status | Message |
|---|---|---|
| org lacks MODULE_MANUFACTURING | 400 | `Manufacturing is not enabled for your organization, so the make-to-order route 'X' can't be assigned.` |
| single PUT, product's SupplyMethod ≠ MANUFACTURE | 400 | `Set the product's supply method to MANUFACTURE first: only manufactured products can use the make-to-order route 'X'.` |
| bulk: products not MANUFACTURE | — | not touched; counted in `skipped` |

Product flags are never written from a route (D-3). Clearing a MANUFACTURE route (null) is always allowed.

### 4.3 Product list filter

`GET api/products?routeCategory=STOCK|MANUFACTURE` (existing ungated read): products with **at least one active
variant** whose route has that category. Combines with the other filters. Unknown code → 400 (as §3).
Variants with no route are not matched by `STOCK` (they follow the org default). The legacy `manufacturable`
filter is unchanged.

### 4.4 Lead-time defaults (D-10) — `api/lead-time/defaults`

```ts
LeadTimeDefaultsModel {
  pickPackDays: number;            // system default 1
  shippingLeadTimeDays: number;    // 3
  salesBufferDays: number;         // 1
  manufacturingBufferDays: number; // 0
  qualityInspectionDays: number;   // 0
  internalTransferDays: number;    // 0
  isSaved: boolean;                // false = the org has no row yet; the values are the system defaults
  modifiedBy?: number; modifiedDate?: string;
}
UpdateLeadTimeDefaultsRequest { pickPackDays; shippingLeadTimeDays; salesBufferDays; manufacturingBufferDays;
                                qualityInspectionDays; internalTransferDays }   // all required integers
```

| Method | Route | Permission | Result / errors |
|---|---|---|---|
| GET | `api/lead-time/defaults` | read any-of (§2) | `LeadTimeDefaultsModel` (the caller's own org; never 404) |
| PUT | `api/lead-time/defaults` | `LEAD_TIME_DEFAULTS_MANAGE` | upsert → `LeadTimeDefaultsModel` with `isSaved: true`. 400 `Pick & pack days must be between 0 and 365.` (same pattern per field, BR-C3-01). A concurrent first save is retried as an update (no 409) |

### 4.5 Variant lead times (D-26) — `api/variants/{uuid}/lead-times`

```ts
VariantLeadTimesModel {
  variantUuid: string; productId: number; sku?: string; variantName?: string;
  routeUuid?: string; routeCode?: string; routeName?: string;
  routeCategory: 'STOCK' | 'MANUFACTURE';  // the variant's route, else the org's SHIP default, else STOCK
  routeFromOrgDefault: boolean;            // the variant has no route of its own
  requiresShipping: boolean;               // the judged route has SHIP (false when no route at all)
  components: VariantLeadTimeComponentModel[];  // always all 8, in the component-code order of §1
  totalDays: number;                       // sum of resolvedDays over components with includedInTotal (no stock, no BOM recursion)
}
VariantLeadTimeComponentModel {
  code: string;                 // SUPPLIER … SALES_BUFFER
  name: string;                 // "Supplier lead time", "Manufacturing", "Manufacturing buffer", "Quality inspection",
                                // "Internal transfer", "Pick & pack", "Shipping", "Sales safety buffer"
  field: string;                // the request property: supplierLeadTimeDays, manufacturingLeadTimeDays, …
  storedDays?: number;          // the variant's override; null = not set
  resolvedDays: number;         // what the calculator would use (stock-unaware)
  source: string;               // SUPPLIER: VARIANT | SUPPLIER_RATE | SUPPLIER_RECORD | PRODUCT | SYSTEM_DEFAULT
                                // MANUFACTURING: VARIANT | PRODUCT | SYSTEM_DEFAULT (1 day)
                                // the other six: VARIANT | ORG_DEFAULT
  detail?: string;              // e.g. "ACME Ltd (preferred)"
  defaultDays: number;          // what would apply if storedDays were null (v1.2): the org default / supplier tier / product / system
  defaultSource: string;        // the source of defaultDays (same codes as source, never VARIANT)
  visible: boolean;             // §5.6: MANUFACTURING, MFG_BUFFER only for MANUFACTURE; SHIPPING only with SHIP; others always
  includedInTotal: boolean;     // v1.3: = visible, except SUPPLIER on a MANUFACTURE route (false: materials come through the BOM)
}
UpdateVariantLeadTimesRequest { supplierLeadTimeDays?; manufacturingLeadTimeDays?; manufacturingBufferDays?;
  qualityInspectionDays?; internalTransferDays?; pickPackDays?; shippingLeadTimeDays?; salesBufferDays? }
  // replaces all eight; null = use the default ("Reset to org defaults" = all null)
```

| Method | Route | Permission | Result / errors |
|---|---|---|---|
| GET | `api/variants/{uuid}/lead-times` | read any-of (§2) | `VariantLeadTimesModel`; 404 other org |
| PUT | `api/variants/{uuid}/lead-times` | `STOCK_MANAGE` | `VariantLeadTimesModel`; 404 other org; 400 `Shipping days must be between 0 and 3650.` (pattern per field) |

Badge text (FE): VARIANT "Variant override", ORG_DEFAULT "Org default", SUPPLIER_RATE / SUPPLIER_RECORD "Supplier",
PRODUCT "Product", BOM "BOM", IN_STOCK "In stock", SYSTEM_DEFAULT "System default".
"Recalculate from BOM" calls §4.6 `calculate-manufacturing` and **shows** the result; it never writes.

### 4.6 Calculator (C4) — `api/lead-time/*`

```ts
LeadTimeCalculateRequest { variantUuid: string; quantity: number; routeUuid?: string; requestedDate?: string }
LeadTimeResultModel {                    // = Shared LeadTimeResult, camelCase
  totalLeadTimeDays: number;
  earliestDeliveryDate: string;          // date-only: UTC today + total (calendar days)
  latestStartDate?: string;              // date-only: requestedDate − total; only with requestedDate
  meetsRequestedDate?: boolean;          // only with requestedDate
  routeUuid?: string; routeCode?: string; routeCategory: 'STOCK' | 'MANUFACTURE';
  components: { code: string; name: string; days: number; source: string; detail?: string }[];
  calculatedAt: string;                  // UTC
}
ManufacturingLeadTimeNodeModel {         // calculate-manufacturing (the per-level tree)
  variantUuid: string; displayName: string; quantity: number;
  levelDays: number; levelDaysSource: 'VARIANT' | 'PRODUCT' | 'SYSTEM_DEFAULT';
  bomUuid?: string; bomNumber?: string; bomVersion?: number;   // null: no active BOM (levelDays only, warning)
  totalDays: number;                     // levelDays + max(input waitDays)
  inputs: {
    variantUuid: string; displayName: string;
    requiredQty: number; freeQty: number; shortfallQty: number;   // free stock in the parent's production warehouse
    isManufactured: boolean;
    waitDays: number;                    // 0 when shortfallQty = 0
    source: string;                      // IN_STOCK | BOM (recursed) | SUPPLIER_* | PRODUCT | SYSTEM_DEFAULT
    detail?: string;
    node?: ManufacturingLeadTimeNodeModel;  // only when recursed
  }[];
  warnings: string[];                    // "Cycle: X uses itself", "Depth limit 10 reached", "No active BOM for X"
}
```

| Method | Route | Permission | Result / errors |
|---|---|---|---|
| POST | `api/lead-time/calculate` | calculate any-of (§2) | `LeadTimeResultModel`. 400 `Quantity must be greater than zero.`; 404 variant not the caller's; 400 `Route not found in your organization.` |
| POST | `api/lead-time/calculate-manufacturing` | same | body `{ variantUuid, quantity }` → `ManufacturingLeadTimeNodeModel`; same errors; 400 when the product is not MANUFACTURE |

Algorithm summary (D-11..D-14): route = request → variant → org SHIP default. MANUFACTURE: `MANUFACTURING`
(BOM-aware, source BOM) + `MFG_BUFFER`; otherwise `SUPPLIER` (0 with IN_STOCK when free stock in the best single
warehouse ≥ quantity). Then `QC`, `TRANSFER`, `PICK_PACK`, `SHIPPING` (route has SHIP), `SALES_BUFFER`. Optional
components with 0 days are omitted; `PICK_PACK` always shows, and `SHIPPING` whenever the route has SHIP. Cache
(D-27, REV-03): only the date-independent part (resolved route, total, components) is cached, 5 min, keyed by (org,
variant, **resolved** route uuid, quantity, UTC date); `earliestDeliveryDate`, `latestStartDate` and
`meetsRequestedDate` are computed on every call. Errors: 404 only for the variant; a body `routeUuid` that isn't the
org's is a 400 (as A33 BR-C3-01).

## 5. Sales lines: lead time and delivery dates (owner DEM)

### 5.1 Line model fields (D-15)

| Field | Inquiry line | Quotation line | SO line |
|---|---|---|---|
| `calculatedLeadTimeDays?: number` | new | new | new |
| `calculatedDeliveryDate?: string` (date-only) | new | new | new |
| `leadTimeCalculatedAt?: string` (UTC) | new | new | new |
| manual date | existing `estimatedDeliveryDate` | existing `promisedDeliveryDate` | **new `manualDeliveryDate?`** |
| `effectiveDeliveryDate?: string` = manual ?? calculated | new | new | new |
| `deliveryDateSource: 'MANUAL' \| 'CALCULATED' \| 'NONE'` | new | new | new |

UI indicator (D-15 / §6.7): MANUAL "✎ Manual", CALCULATED "⏱ Calculated", NONE "— Not calculated".

### 5.2 Line lead-time endpoints (D-16)

| Method | Route | Permission | Allowed when |
|---|---|---|---|
| POST | `api/sale-inquiries/{uuid}/lines/{lineUuid}/lead-time` | `SALE_INQUIRY_EDIT` | inquiry RECEIVED / UNDER_REVIEW / REVIEW_COMPLETE |
| POST | `api/sale-quotations/{uuid}/lines/{lineUuid}/lead-time` | `SALE_QUOTATION_EDIT` | quotation DRAFT |
| POST | `api/sale-orders/{uuid}/lines/{lineUuid}/lead-time` | `SALE_ORDER_EDIT` | order DRAFT |

- Body: none (`{}` accepted). Calculates with the line's variant and quantity and stores the three Calculated* fields;
  the manual date is untouched.
- Route used: SO line = its effective route (A33 resolver: override → variant → org default for the header mode);
  inquiry / quotation line = the variant's route, else the org's SHIP default.
- Requested date passed to the calculator: inquiry line `requestedDeliveryDate`; SO = header `expectedDeliveryDate`;
  quotation none.
- Result: `ApiResponse<SaleLineLeadTimeModel>`:
  ```ts
  SaleLineLeadTimeModel { line: SaleInquiryLineModel | SaleQuotationLineModel | SaleOrderLineModel; leadTime: LeadTimeResultModel }
  ```
- Errors: 404 order/line not the caller's; 400 wrong status (`Sale order SO-… is CONFIRMED: line lead times can only be calculated on a draft.`, same pattern per document); 400 when Inventory isn't enabled (`Lead-time calculation needs the Inventory module.`).

`PUT api/sale-orders/{uuid}/lines/{lineUuid}/delivery-date` (`SALE_ORDER_EDIT`):
- body `{ manualDeliveryDate: string | null }` (null clears; the effective date falls back to the calculated one);
- allowed on DRAFT, CONFIRMED, PARTIALLY_FULFILLED (400 otherwise: `Sale order SO-… is CANCELLED: its delivery dates can no longer change.`); a CANCELLED line → 400;
- runs under the order lock; no re-pricing, no line rebuild;
- result `ApiResponse<SaleOrderLineDeliveryDateResultModel>`:
  ```ts
  SaleOrderLineDeliveryDateResultModel { line: SaleOrderLineModel; productionNotRescheduled: boolean; warning?: string }
  // warning when the line already has a production order: "PROD-… was planned for the earlier date and is not rescheduled."
  ```

Round-trips and copies:
- `CreateSaleOrderLineRequest` (POST/PUT `api/sale-orders`) gains `manualDeliveryDate?`, `calculatedLeadTimeDays?`,
  `calculatedDeliveryDate?`, `leadTimeCalculatedAt?`. Draft lines are rebuilt, so **send them back on every update** (C-14).
- Inquiry → quotation copies Calculated* (and, as today, `estimatedDeliveryDate` → `promisedDeliveryDate`).
  Quotation → SO copies `promisedDeliveryDate` → `manualDeliveryDate` and Calculated*.
- Inquiry CAN_SUPPLY / PARTIAL: `calculatedDeliveryDate` satisfies the "estimated delivery date required" rule when
  no estimate was typed.
- Unsaved forms use `POST api/lead-time/calculate` (§4.6).

## 6. Sale orders: confirm split, production, cancel (owner DEM)

### 6.1 Line and order fields

`SaleOrderLineModel` gains (besides §5.1):
- `effectiveRouteCategory?: 'STOCK' | 'MANUFACTURE'` — live from the effective route while DRAFT, the confirm-time
  snapshot (`FulfillmentRouteCategory`) afterwards; null for DROP_SHIP lines and lines with no route;
- `fulfillmentMode` may be `MAKE_TO_ORDER` (D-1: nothing reserved, `deficitQty = quantity`);
- `productionShortfallQty?: number` (D-21; set when production accepted less than the line, = quantity on zero yield).

`SaleOrderModel` (detail) gains:
- `productionOrders: SaleOrderProductionOrderModel[]` (D-25): every SALES_ORDER-sourced production order of the
  order (make-to-order **and** A30 make-to-shortage), oldest first; readable with SALE_ORDER_VIEW;
- `productionCreationPending: boolean` → show the banner and the "Create production orders" button. True when
  `ProductionCreationPendingSince` is set **or** (REV-05b) the order is CONFIRMED / PARTIALLY_FULFILLED and some
  non-cancelled MAKE_TO_ORDER line has no non-cancelled make-to-order production order — so clearing the flag never
  hides a line that still has no PO.
- Frontend: links from the Production tab to the PO detail need `PROD_VIEW`; without it show the number as text.

```ts
SaleOrderProductionOrderModel {
  productionOrderUuid: string; productionNumber: string;
  soLineUuid?: string; lineNumber?: number;          // 1-based by line Id, as A33's "Line N"
  status: string;                                    // DRAFT … COMPLETED, CANCELLED
  plannedQuantity: number; acceptedQuantity: number;
  isMakeToOrder: boolean;                            // fulfillmentRouteUuid != null
  fulfillmentRouteUuid?: string; fulfillmentRouteCode?: string; fulfillmentRouteName?: string;
  deliveryOrderUuid?: string; deliveryNumber?: string;   // null = "Delivery: pending"
  created: boolean;                                  // only meaningful in confirm / create results
}
```

### 6.2 Confirm-gate blockers (D-5)

Checked for routable lines whose effective route is MANUFACTURE, after A33's route checks, through
`IManufacturingReadiness` (one batched call). **One blocker per line**, first match in this order. They appear in
`confirmBlockers[]` (DRAFT detail), in the preview's `blockers[]`, as the line's `routeBlocker`, and in the confirm's
400 (messages joined with `\n`, per-line problems in line order, as A33).

| Code | Condition | Message (N = line number, X = route code, Item = "Product — Variant") |
|---|---|---|
| `MANUFACTURING_DISABLED` | org lacks MODULE_MANUFACTURING | `Line N: 'X' is a make-to-order route, but manufacturing is not enabled for your organization. Choose a stock route for this line.` |
| `NOT_MANUFACTURED` | the product's SupplyMethod is not MANUFACTURE | `Line N: Item is not a manufactured product, so it can't use the make-to-order route 'X'. Choose a stock route, or set the product's supply method to MANUFACTURE.` |
| `BOM_MISSING` | no active, effective BOM (variant-specific, else product-general) | `Line N: Item has no active bill of materials, so it can't be made to order. Activate a BOM, or choose a stock route for this line.` |
| `PRODUCTION_WAREHOUSE_MISSING` | no `Product.DefaultProductionWarehouseId` | `Line N: Item has no default production warehouse, so it can't be made to order. Set one on the product, or choose a stock route for this line.` |

Line save stays free (drafts may hold these lines). A SELF_PICKUP order whose make-to-order line routes to SHIP hits
A33's `SHIPPING_ADDRESS_REQUIRED`; its message gains: ` For collection, create a MANUFACTURE route without the SHIP step.` (R-17).

### 6.3 Delivery preview

`GET api/sale-orders/{uuid}/delivery-preview` and `POST api/sale-orders/delivery-preview` (unchanged routes):
- `lines[]` items gain `effectiveRouteCategory?`;
- make-to-order lines are **not** in `groups[]` (so `deliveryCount` counts stock deliveries only);
- new `productionLines[]`:
  ```ts
  { lineUuid?: string; lineNumber: number; variantUuid: string; itemDescription?: string; quantity: number;
    routeUuid: string; routeCode: string; routeName: string; steps: string[];
    message: string }   // "A production order will be created; its delivery follows when production completes."
  ```
- `canConfirm` includes §6.2 blockers.

### 6.4 Confirm result (`POST api/sale-orders/{uuid}/confirm`, SALE_ORDER_CONFIRM)

`SaleOrderConfirmResultModel` gains:
```ts
productionOrders: SaleOrderProductionOrderModel[];   // created (created=true) or already existing
productionCreationFailed: boolean;                   // true → show productionMessage + "Create production orders"
productionMessage?: string;
```
`skippedLines[]` may now include make-to-order lines with reason `Line N is made to order: its delivery is created
when its production order completes.` (the A33 creator's skip, §8.2). Make-to-order lines get no reservation, no
deficit (auto-PO) job and no delivery at confirm. **D-17a (REV-01):** production creation registers **no** allocation
demand either; MFG's FGR hook registers (or extends) the line's SALES_ORDER demand just before its post-commit
allocation run, capped at the finished goods received from that PO.

### 6.5 Production recovery (D-17)

`POST api/sale-orders/{uuid}/create-production-orders` (any of `SALE_ORDER_CONFIRM`, `PROD_CREATE`):
- result `ApiResponse<SaleOrderProductionCreationResultModel { productionOrders: SaleOrderProductionOrderModel[]; productionCreationFailed: boolean; productionMessage?: string }>`;
- idempotent: lines that already have their make-to-order PO return it with `created: false`;
- 400 unless CONFIRMED / PARTIALLY_FULFILLED (`Sale order SO-… is DRAFT: production orders are created once it is confirmed.`);
  400 `Manufacturing is not enabled for your organization.`; 404 other org;
- clears `ProductionCreationPendingSince` when it returns without error.

Pending-flag semantics (D-17, REV-05):
- Confirm sets `ProductionCreationPendingSince` in its own commit when it has make-to-order lines; the creation run
  (after confirm, the sweep, or this button) clears it whenever it **returns a result**, including "order no longer
  confirmed" and a business refusal (a 400 such as a BOM deactivated after confirm, which would fail forever). Only an
  unexpected failure (database, lock timeout) keeps it, for the sweep (every 15 min, entries pending ≥ 10 min, per org).
- `SO_PRODUCTION_FAILED` (timeline + notification) goes out **once per failure**: the first failed attempt notifies;
  sweep retries of the same pending entry don't. After a business refusal the flag is cleared and the line shows
  through `productionCreationPending` (§6.1) until someone presses the button.

### 6.6 Cancel (`POST api/sale-orders/{uuid}/cancel`, SALE_ORDER_CANCEL)

`SaleOrderCancelResultModel` gains (D-22):
```ts
cancelledProductionOrders: SaleOrderProductionOrderModel[];   // were DRAFT / PLANNED / MATERIAL_PENDING / READY, nothing issued
runningProductionOrders: SaleOrderProductionOrderModel[];     // IN_PROGRESS or later, or material issued: kept, need attention
cancelledAllocationDemands: number;                           // open SALES_ORDER allocation demands cancelled
```
Both lists are also on the timeline. `productionCreationPending` is cleared.

## 7. Production orders (owner MFG) — `api/production-orders`

`ProductionOrderListItemModel` and `ProductionOrderDetailModel` gain (D-18):
```ts
isMakeToOrder: boolean;                         // fulfillmentRouteUuid != null
fulfillmentRouteUuid?: string; fulfillmentRouteCode?: string; fulfillmentRouteName?: string;
fulfillmentRouteCategory?: string;              // names via IFulfillmentRouteLookup (also for a deactivated route)
deliveryOrderUuid?: string; deliveryNumber?: string;   // the latest delivery created from this PO
deliveryCreationPending: boolean;               // DeliveryCreationPendingSince is set
saleOrderLineNumber?: number;                   // when SourceType = SALES_ORDER
shortfallQuantity?: number;                     // make-to-order only (REV-02): once COMPLETED and accepted < planned,
                                                // planned − accepted; on zero yield (QI accepted 0) = planned; else null
```
Frontend: the live delivery status call (`GET api/logistics/deliveries?productionOrderUuid=`) needs `DELIVERY_VIEW`;
without it, don't call and show `deliveryNumber` only.
The existing `sourceType/sourceUuid/sourceLineUuid/sourceReference` carry the SO link (no new SO columns).
Live delivery status: `GET api/logistics/deliveries?productionOrderUuid={uuid}` (DELIVERY_VIEW, §8.3) — Material
can't read Logistics.

`POST api/production-orders/{uuid}/create-delivery` (`DELIVERY_CREATE`, MODULE_MANUFACTURING) — "Create delivery now" (D-20):
- result `ApiResponse<ProductionDeliveryHandoffModel>`:
  ```ts
  ProductionDeliveryHandoffModel { productionOrderUuid: string; quantityCreated: number;
    deliveryUuid?: string; deliveryNumber?: string;          // created by this call
    latestDeliveryUuid?: string; latestDeliveryNumber?: string;
    skippedReason?: string }                               // why nothing / less was created
  ```
- 400 `PROD-… is not made to order for a sale order, so no delivery is created from it.` (no route, or not SALES_ORDER-sourced, BR-C6-02);
  400 `PROD-… has no accepted quantity yet.`; 404 other org;
- idempotent through the D-20 formula: a repeat returns 200 with `quantityCreated: 0` and a `skippedReason`.

## 8. Deliveries and the A33 creator (owner LOG)

### 8.1 Schema (LOG migration `A34_RouteCategoryAndProductionDeliveries`)

`logistics.fulfillment_routes.RouteCategory nvarchar(20) NOT NULL DEFAULT 'STOCK'` + CHECK
(`STOCK, MANUFACTURE, BUY, DROPSHIP`); `logistics.delivery_orders.ProductionOrderUuid uniqueidentifier NULL` +
filtered index.

### 8.2 A33 creator skip (PD-02, C-3)

`ISaleOrderDeliveryCreator` (confirm, the D-12 sweep, `POST api/sale-orders/{uuid}/create-deliveries`) skips every
line whose route category is MANUFACTURE, with reason
`Line N is made to order: its delivery is created when its production order completes.`

### 8.3 Models and filter

- `DeliveryListItemModel` and `DeliveryDetailModel` gain `productionOrderUuid?: string`.
- `DeliveryFilter` (`GET api/logistics/deliveries`, DELIVERY_VIEW) gains `productionOrderUuid?`.
- A delivery from production has `sourceType = SALE_ORDER`, `sourceUuid` = the SO (T-C6-06), the PO's route snapshot
  (`fulfillmentRouteUuid/Code`, `routeSteps`; T-C6-05), status DRAFT, and its notes say `From production order PROD-…`.

### 8.4 `IProductionDeliveryCreator` (D-29; Shared contract, implemented in Logistics)

- Own transaction; takes `SaleOrderLocks.HoldsResource(soUuid)` first, then re-reads the order and line with the org
  explicit. Order not CONFIRMED / PARTIALLY_FULFILLED, line missing or cancelled → nothing, with `skippedReason`.
- Quantity = min(`acceptedQuantity` − quantity on non-cancelled, non-deleted deliveries with this
  `ProductionOrderUuid`, line quantity − fulfilled − in flight on other non-cancelled deliveries). ≤ 0 → nothing
  (`Nothing left to deliver from PROD-…`). Ignores `PartialFulfillmentAllowed` (D-21).
- Mode and steps from the PO's route even if deactivated; a route that no longer exists → no route (legacy full path,
  A33 R-1) with the order's header delivery mode.
- Ship-from: the warehouses of the line's SALE_ORDER holds (split per warehouse, as A33), else
  `fallbackWarehouseUuid` (PO output, else production warehouse). `requestedDate` = the line's effective delivery
  date, else the header's expected date.
- Result `ProductionDeliveryResult(Delivery, QuantityCreated, SkippedReason, LatestDelivery, Deliveries)`; callers
  stamp `LatestDelivery` onto the PO even when nothing new was created.
- **Results, not exceptions (REV-05a).** Every business outcome (order cancelled / not found / not the org's, line
  missing or cancelled, nothing left) is a result with `SkippedReason`, and the caller clears
  `DeliveryCreationPendingSince` on any returned result. Only an unexpected failure (database, lock timeout) throws and
  keeps the PO pending for Material's sweep.
- `organizationId` is the **production order's own** organization, never the caller's tenant (REV-05c, R-11): a super
  admin may confirm another org's FGR. The same holds for `ISaleOrderProductionFeedback`.
- Material's handoff then calls `ISaleOrderProductionFeedback` with `DeliveryNumber` set only when `QuantityCreated > 0`,
  and `Completed = true` only when the PO is COMPLETED (REV-02).

## 9. Notifications and timeline (D-23)

| Type | Sent by | To | When |
|---|---|---|---|
| `PROD_CREATED` (existing) | Material | the creator's supervisor | each make-to-order PO created |
| `SO_DELIVERY_FROM_PRODUCTION` | Demand (feedback) | the SO creator | a call **created** a delivery from the PO (`DeliveryNumber` set); never on a replay |
| `PROD_SHORTFALL` | Material (to the PO creator, from the handoff, once, when the PO turns COMPLETED with planned > accepted) and Demand (feedback, to the SO creator) | | at COMPLETED, accepted < line quantity; Demand only when `ProductionShortfallQty` changes (REV-02) |
| `PROD_ZERO_YIELD` | Material (to the PO creator's supervisor) and Demand (to the SO creator) | | QI accepted 0 on a make-to-order PO |
| `SO_PRODUCTION_FAILED` | Demand | the confirming user | production creation after confirm failed |

Constants: `RouteClassificationNotificationTypes` in the Shared contract file. Single recipient per notification.

Timeline event codes (display-only; the frontend shows each entry's text):
- SO (Demand): `SO_PRODUCTION_CREATED`, `SO_PRODUCTION_FAILED`, `SO_PRODUCTION_CANCELLED` (cancel cascade lists),
  `SO_DELIVERY_FROM_PRODUCTION`, `SO_PRODUCTION_SHORTFALL` (also zero yield).
- PO (Material): `PROD_CREATED` (existing), `PROD_DELIVERY_CREATED`.

## 10. Shared contracts (SMS.Shared, `RouteClassificationContracts.cs`)

| Interface | Implemented by | Used by | Notes |
|---|---|---|---|
| `ILeadTimeCalculator` | Inventory | Demand (line endpoints, D-19 dates) | org explicit; cached (D-27) |
| `IBomStructureReader` | Material | Inventory calculator | batched per BOM level |
| `IManufacturingReadiness` | Material | Demand gate / preview / detail | the feature check is the caller's |
| `ISaleOrderProductionService` | Material | Demand (confirm creation, cancel, detail) | Create/Cancel called **inside** the SO lock; Plan **after** it |
| `IFulfillmentRouteUsage` (A33) | + Material: "open production orders" | Logistics route service (D-8) | non-cancelled, not COMPLETED / CLOSED POs carrying the route |
| `IProductionDeliveryCreator` | Logistics | Material (FGR hook, sweep, button) | takes the SO lock itself; call with nothing held |
| `ISaleOrderProductionFeedback` | Demand | Material handoff / QI hook | call with nothing held |
| `ISupplierLeadTimeLookup` | Suppliers | Inventory calculator | optional (D-11 tier 4) |

All are optional-safe: an unregistered implementation means the feature is off.

## Change log

| Version | Date | Change |
|---|---|---|
| 1.4 | 2026-10-04 | REV review: `ProductionOutcome` gains trailing `Completed` (shortfall only when final, feedback idempotent; `DeliveryNumber` only when created; §7 `shortfallQuantity` only once COMPLETED / zero yield) (REV-02); cache keeps only the date-independent part, keyed on the resolved route (REV-03); calculator errors: 404 variant, 400 route (REV-04); pending-flag semantics for production creation (§6.5) and delivery creation (§8.4), `productionCreationPending` also covers MTO lines with no PO, org = the PO's own (REV-05); FE notes on DELIVERY_VIEW / PROD_VIEW. REV-01 → lead's D-17a: no allocation demand at production creation; MFG registers it at FGR (§6.4). |
| 1.3 | 2026-10-04 | §4.5 component `includedInTotal` (INV request); `totalDays` sums only those (SUPPLIER is shown but not counted on a MANUFACTURE route, as wireframe 11.1). |
| 1.2 | 2026-10-04 | §4.5 `VariantLeadTimeComponentModel` gains `defaultDays` + `defaultSource` (FE-ADMIN request): the value a cleared override falls back to. |
| 1.1 | 2026-10-04 | §9 `PROD_SHORTFALL` split like `PROD_ZERO_YIELD` (MFG request): Material notifies the PO creator, Demand the SO creator. |
| 1.0 | 2026-10-04 | First version (LOG, wave 0). LOG deviations: `ProductionDeliveryResult` gains two optional trailing members (`LatestDelivery`, `Deliveries`) so a replay can still stamp the PO and a split delivery is reported in full; SO / preview lines use `effectiveRouteCategory` (matching A33's `effectiveRoute*`); skip text says "its production order". |
