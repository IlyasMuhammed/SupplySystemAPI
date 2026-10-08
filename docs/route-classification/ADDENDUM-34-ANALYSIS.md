# Addendum 34: overlap and risk analysis (Route Classification, Lead Time, Production → Delivery)

Written 2026-10-04 by the A34 analyst. Read it together with Part A of `ADDENDUM-34-ROUTE-CLASSIFICATION-LEAD-TIME.md`,
which is the line-by-line "spec says / repo has" table with file references. Everything here was checked by reading
code; nothing was run. Paths are relative to `D:\Supply System\SupplyChain`.

## 0. Bottom line

- **C1 (route category) is small and safe.** It means one column, two seeds and a few rules on top of A33's service,
  which already has usage counting, system routes and per-org seeding with a backfill.
- **C2 can't be done literally.** The flags are product-level, the route is variant-level, and BOM and production
  creation hard-require `SupplyMethod = MANUFACTURE`. So the route is *validated against* the flags rather than
  overwriting them, and the BOM tab rule doesn't change.
- **C3/C4 are new, but have nowhere to read part of their input from.** Lead time is already stored in four places,
  BOM operations don't exist, and `lookups` doesn't migrate. Everything goes into Inventory, with BOM structure read
  through a Shared contract implemented in Material.
- **C5 is the risky change.** SO → production already exists (A30 Track C: make-to-shortage, keyed on the product's
  SupplyMethod, via the Hangfire deficit job). The spec's make-to-order flow would double-supply unless the A32
  reservation, the A29/A30 deficit job and the A33 delivery auto-create all skip MANUFACTURE lines.
- **C6 has no event to extend.** The hook goes into `FinishedGoodsReceiptService` after its post-commit allocation
  run and calls Logistics through a Shared contract (Material can't reference Logistics). The spec's M4 backfill would
  create duplicate deliveries, so there is none.
- **Pre-existing bug found.** SO cancel never cancels production orders or SALES_ORDER allocation demands, so finished
  goods made for a cancelled order are later re-parented onto it. The C5 cancel cascade fixes this.

## 1. Already implemented (fully or partly)

| Spec item | Status | Where |
|---|---|---|
| §2 SO ↔ production order link | **Done**, generic: `SourceType` = SALES_ORDER, `SourceUuid` = SO, `SourceLineUuid` = line, `SourceReference` = SO number; indexed by (Org, SourceType, SourceUuid) | `src/SMS.Modules.Material/Domain/ProductionEntities.cs:138-141`, `Data/Maps/ProductionMaps.cs:35`; list filter `SourceUuid` (`Models/ProductionModels.cs:56`) |
| C5 "SO confirm → production order" | **Done differently**: make-to-shortage for the *deficit* of products with SupplyMethod MANUFACTURE, through the Hangfire deficit job (gated by `AutoPoEnabled`) | `Demand/Services/AutoPoCreationJob.cs:63-96`, `SaleOrderManufacturingService.cs:34-65`, `Material/Services/ProductionOrderService.cs:261-324` |
| BOM explosion, PMRs, Supply Requirement Engine, chained POs, draft-PO consolidation | **Done** (A30/A31), and stays unchanged as the spec says | `ProductionOrderService.cs:148-216`, `SupplyRequirementEngine.cs` |
| BR-C10-02 "automatic allocation" at FGR, and FG reaching the SO line | **Done**: FGR → `AllocateAsync` after the commit → `SaleOrderFulfillmentListener` moves the hold onto the SO line (no expiry) | `FinishedGoodsReceiptService.cs:177-180`, `Demand/Services/SaleOrderFulfillmentListener.cs:41-86` |
| PO detail ↔ SO links in the UI | **Done**: PO detail "From sale order …"; SO detail "Production" tab | `production-order-detail.component.html:29-33`; `sale-order-detail.component.html:558-594` |
| C6 escape hatch (BR-C6-09): override a line to a stock route | **Done** (A33 line override, route-only PUT, D-16 snapshot) | `Demand/Services/SaleOrderService.Routes.cs:179-211` |
| SO status with deliveries arriving later | **Done**: the status is derived from each line's `FulfilledQty`, so lines delivered after production work as they are | `Demand/Services/SaleOrderFulfillmentService.cs:51-60` |
| BR-C4-02 cycle detection and depth guard | **Done** for BOM save, cost roll-up and chained supply (depth 10) | `BomRepository.cs:548-576`, `BomService.cs:41-63`, `SupplyRequirementEngine.cs:376-390` |
| C3 lead time data | **Partly, scattered**: `Product.LeadTimeDays`; `ProductVariant.LeadTimeDays` (never written); `VariantSupplier.LeadTimeDays` and `IsPreferred`; `BusinessPartner.LeadTimeDays`; inquiry `RequestedDeliveryDate` / `EstimatedDeliveryDate` / `ProcurementLeadDays`; quotation `PromisedDeliveryDate` | Part A rows 20, 22 |
| Per-org settings singleton | **Done** as a pattern (`sale_order_config`: read with defaults, created on first write, audited) | `Demand/Services/SaleOrderConfigService.cs:143-152` |
| Route seeding, usage counting, per-class defaults | **Done** (A33) | `Logistics/Services/FulfillmentRouteService.cs` |
| "Manufacturable products" filter | **Done** at product level: `GET api/products/manufacturable` | `Inventory/Controllers/ProductsController.cs:194` |
| Route category, lead-time components, defaults, calculator, line lead times, confirm split, production → delivery | **Not started** | — |

## 2. Conflicts: where the spec, taken literally, breaks existing behaviour

| # | Severity | Conflict |
|---|---|---|
| C-1 | **High** | **Two production paths.** The spec says SO → production "is not implemented". In fact A30 Track C raises a production order for the deficit of every SupplyMethod MANUFACTURE line. If the spec's make-to-order path is added next to it, a MANUFACTURE line gets the deficit job's order (`SaleOrderService.cs:365-366` → `AutoPoCreationJob.cs:87-96`) *and* the confirm-time order. Because `EnsureForSourceAsync` is idempotent per (source, line), they may merge into one order, but the deficit path first allocates free stock to the line (`SaleOrderManufacturingService.cs:55`), which contradicts make-to-order. |
| C-2 | **High** | **A32 reservation at confirm.** `CheckAndReserveAsync` reserves free stock for every line (`AvailabilityCheckService.cs:86-112`). For a make-to-order line, that stock plus a full-quantity production order is double supply. |
| C-3 | **High** | **A33 delivery auto-create.** The creator makes DRAFT deliveries for the full outstanding quantity of every routed line (`SaleOrderDeliveryCreator.cs:157`). The D-12 sweep and the "Create deliveries" button use the same creator. A MANUFACTURE line would get a delivery at confirm, and a second one from C6. |
| C-4 | **High** | **Spec M4 backfill.** Copying the SO line's route onto existing SALES_ORDER-sourced POs would make every A30 make-to-shortage PO create a delivery at FGR, on top of the A33 delivery its line already has. |
| C-5 | Medium | **Flags are product-level; production requires SupplyMethod.** `IsManufacturable` is derived from `SupplyMethod` (`InventoryRepository.cs:705`). A FINISHED_GOOD must be MANUFACTURE, and only semi-finished or finished goods may be (`ProductClassificationRules.cs:24-28`). BOM and PO creation refuse anything else (`BomRepository.cs:455`, `ProductionOrderRepository.cs:443-445`). So "auto-sync from the route" is either a no-op or an invalid write, and it can't express a product whose variants have different routes. T-C2-02 ("is_manufacturable = FALSE after switching a variant to PICK_AND_SHIP") cannot hold for a finished good. |
| C-6 | Medium | **`lookups.LeadTimeDefaults` would never exist on SMSGlobal.** Lookups calls `EnsureCreatedAsync` only (`LookupsDataSeeder.cs:13`); every read would fail with "invalid object name". |
| C-7 | Medium | **Production statuses.** The spec's list lacks QUALITY_INSPECTION, CLOSED and CANCELLED. T-C5-04 ("status = DRAFT") and T-C5-05 ("PMRs created") contradict each other here, because PMRs exist only after planning (status PLANNED). The spec cancels only below IN_PROGRESS, while the repo's `CanCancel` includes IN_PROGRESS and issue is allowed at READY. |
| C-8 | Medium | **No FGR event.** FGR can be partial; COMPLETED comes only after every accepted unit is received; a QI with 0 accepted never reaches FGR (`FinishedGoodsReceiptService.cs:74-76, 157-161`). The spec's trigger and its zero-yield case need re-anchoring. |
| C-9 | Medium | **A33 C-2 hold rule vs finished goods.** A DRAFT delivery holds nothing. What protects the goods for the order is the SO line's SALES_ORDER hold, which the allocation engine gives only to a *registered* SALES_ORDER demand, by priority and date. Without a registered demand the goods stay free until release, and any other demand can take them. |
| C-10 | Medium (pre-existing) | **SO cancel leaves production running.** `CancelHeldAsync` (`SaleOrderService.cs:483-540`) doesn't cancel the order's production orders or its SALES_ORDER allocation demands. Finished goods allocated later are moved onto the cancelled order by the listener, which doesn't check status (`SaleOrderFulfillmentListener.cs:53-73`), and those holds never expire. |
| C-11 | Medium | **Default per class.** Both MFG seeds have SHIP. Making either the SHIP default (A33 L-1) would turn every unrouted line of every SHIP order into production, which fails for products that aren't manufactured. |
| C-12 | Low | **Partial yield vs A33 D-8.** With `PartialFulfillmentAllowed = false`, a delivery for less than the line is refused (`DeliveryFromSourceRepository.cs:478-499`), but a yield shortfall can't be "completed" by waiting. |
| C-13 | Low | **PERSISTED computed column.** There is no precedent in `src`; the EF InMemory provider (all unit tests) never computes it; and it needs guarded DDL on the drifted DB. |
| C-14 | Low | **SO lines are rebuilt wholesale with new uuids** (`SaleOrderService.cs:201-216`). Lead-time fields are lost on every save unless they round-trip. |
| C-15 | Low | **Spec API shape.** There are no `api/product-variants`, `api/inquiry-lines` or `api/sale-order-lines` endpoints, and no "authenticated"/"admin" gates. The variant PATCH is ungated (`ProductsController.cs:349-350`). |
| C-16 | Low | **SELF_PICKUP orders.** Both MFG seeds have SHIP, so a SELF_PICKUP order with a make-to-order line hits A33's SHIPPING_ADDRESS_REQUIRED blocker. A custom MANUFACTURE route without SHIP solves it. |
| C-17 | Low | **Yield shortfall leaves the SO PARTIALLY_FULFILLED.** SO lines can't be short-closed (no such action exists). Invoicing still works per delivery. Re-making the shortfall is deferred by the spec (Appendix A). |

## 3. Decisions (D-1 … D-29)

Standing instruction: when the register arrives, these recommended defaults are adopted without asking. Each one is
non-breaking for existing data. No existing variant or line has a MANUFACTURE route, so nothing changes for an org
until it assigns one.

| # | Question | Options | **Recommended default** and reason |
|---|---|---|---|
| D-1 | What does a MANUFACTURE-route line do at confirm? | (a) Make-to-order as specified: reserve nothing, no deficit job, one production order per line for the **full** quantity. (b) Make-to-shortage: reserve free stock and produce only the deficit. | **(a)**, for lines whose effective route is MANUFACTURE. The line gets `FulfillmentMode = MAKE_TO_ORDER` (a new code), `DeficitQty = Quantity`, status OPEN, and nothing is reserved. No deficit job is enqueued for it, and no delivery is created at confirm. This is the spec's model; its escape hatch (override the line to a stock route) already exists; and option (b) stays available through D-2. |
| D-2 | What happens to the existing A30 make-to-shortage path? | (a) Re-key it on route category. (b) Keep it keyed on `Product.SupplyMethod` for non-MANUFACTURE lines. | **(b), unchanged.** Every existing manufactured product resolves to a STOCK route today (org default PICK_AND_SHIP), so re-keying would break their production overnight. A STOCK-route line of a MANUFACTURE product keeps "reserve what's free, produce the shortage". `AutoPoCreationJob` also skips MAKE_TO_ORDER lines defensively, in case a job is replayed. |
| D-3 | Should a route "replace" `is_manufacturable` / `supply_method` (auto-sync, BR-C2-03)? | (a) Write the product flags from the variant's route. (b) Don't write them; validate instead. | **(b).** A MANUFACTURE route can only be assigned (single or bulk) to variants of products whose `SupplyMethod = MANUFACTURE`; otherwise 400 "Set the product's supply method to MANUFACTURE first". `IsManufacturable` stays derived from SupplyMethod. Reason: the flags are product-level and type-constrained (C-5), production and BOM creation require them, and a write would contradict sibling variants. Under (b) a variant with a MANUFACTURE route always implies `isManufacturable`, so T-C2-01 holds. T-C2-02 is adapted: flags unchanged, BOMs kept. |
| D-4 | BOM tab visibility (BR-C2-01/04) | (a) Only when the variant's route is MANUFACTURE. (b) Unchanged: `product.isManufacturable`. | **(b).** Because of D-3, (a) ⊂ (b). And (a) would hide the BOMs of make-to-stock products that are produced by standalone POs (spec §8.5). A "Make-to-order" tag on variants with a MANUFACTURE route gives the visual cue. |
| D-5 | Where is "MANUFACTURE needs an active BOM" (BR-C2-02) enforced, and what else must hold? | Line save / route assignment / confirm gate | **Confirm gate**, extending A33's blocker list. It shows live in the preview and on the detail, and blocks confirm. New codes: `MANUFACTURING_DISABLED` (org lacks MODULE_MANUFACTURING), `NOT_MANUFACTURED` (the product's SupplyMethod changed since the route was assigned), `BOM_MISSING` (no active, effective BOM: variant-specific, else product-general), `PRODUCTION_WAREHOUSE_MISSING` (no `Product.DefaultProductionWarehouseId`). These checks run batched through `IManufacturingReadiness`. Line save stays free, so drafts can be entered while the BOM is still being approved. |
| D-6 | Can a MANUFACTURE route be an org default? | yes / no | **No.** set-default on a MANUFACTURE route → 400; changing a default route's category to MANUFACTURE → 409. The seeds are non-default. Avoids C-11. |
| D-7 | BUY / DROPSHIP | (a) Accept them now. (b) Allow them in the schema, refuse them in the service. | **(b)**: the CHECK constraint includes them; create and update return 400 "not yet available" (T-C1-02, BR-C1-03). A29's drop ship stays the line *sourcing* mode `DROP_SHIP`, still exempt from routes (A33 D-5). A future DROPSHIP category must be reconciled with it. |
| D-8 | When can a route's category change? | in use / system / default | **Refused (409) when** the route is a system route, a default route, or in use: active variants, open SO lines, or open production orders (a new Material `IFulfillmentRouteUsage`). Otherwise allowed. This is BR-C1-02 plus A33 D-10's spirit. |
| D-9 | Seeding the MFG routes | (a) All orgs. (b) Only orgs with MODULE_MANUFACTURING. | **(a)**, through the existing seeder, provisioning handler and startup backfill, matched by code (T-C1-04: 5 seeds). When the org lacks MODULE_MANUFACTURING, MANUFACTURE routes are hidden from pickers, refused on assignment and blocked at confirm (D-5). That way enabling the module later needs no re-seed. |
| D-10 | Where `LeadTimeDefaults` lives | Lookups (spec) / Demand `sale_order_config` / Inventory / Material | **Inventory: `inventory.LeadTimeDefaults`**, one row per org with a unique `(OrganizationId)` index. A missing row reads as the system defaults 1/3/1/0/0/0; PUT upserts. No backfill and no provisioning write, which is the `SaleOrderConfig` precedent and behaves the same as BR-C3-03. Inventory owns the variant overrides these defaults back, migrates at startup, and the calculator lives there. Lookups is ruled out (C-6). |
| D-11 | Supplier lead time: column and resolution | add `SupplierLeadTimeDays` / reuse the existing one | **Reuse `ProductVariant.LeadTimeDays`** as the variant's supplier override (the API calls it `supplierLeadTimeDays`; the app has never written it, so nothing changes meaning). Chain: variant override → the preferred active `VariantSupplier` row (`IsPreferred`, effective today) → the active row of `DefaultSupplierId` → that supplier's `BusinessPartner.LeadTimeDays` (a new optional `ISupplierLeadTimeLookup`, implemented in Suppliers) → `Product.LeadTimeDays` → 0. Each component reports its tier as its `source`. |
| D-12 | Manufacturing time without BOM operations | (a) Add BOM operations/routing now. (b) Days per BOM level, entered by hand. | **(b)**. Operations, work centres and routing are a module of their own and aren't in the spec's migration list. Days for one level = `variant.ManufacturingLeadTimeDays ?? Product.LeadTimeDays (MANUFACTURE products) ?? 1`. Unlike the spec, the total is **always** BOM-aware: level days + max(component waits), even when the manual value is set; otherwise a manual value would hide component shortages. No data copy from `Product.LeadTimeDays` (it is used as the fallback instead), so nothing is written to SMSGlobal. "Recalculate from BOM" shows the computed total; it doesn't overwrite the manual value. |
| D-13 | Recursion and component stock | route / SupplyMethod; which warehouse | **Recurse when the component's product has SupplyMethod MANUFACTURE** (the Supply Requirement Engine's own rule, which the spec keeps unchanged); otherwise use the component's supplier lead (D-11). Component stock = free stock in the **parent's production warehouse** (A31 C6: every PMR draws from the PO's warehouse); with no production warehouse, use the best single warehouse. Depth 10 + visited set; a cycle gives 0 with a warning component (T-C4-05). Batched per BOM level: one BOM read, one stock read and one supplier read per level. |
| D-14 | Stock awareness at the top level (STOCK routes) | (a) Always add supplier lead (T-C4-01 as written). (b) Only when free stock is short. | **(b)**: supplier lead counts only when the variant's free stock in its best single warehouse (`GetAvailableAsync`, the rule confirm uses) is below the quantity. Otherwise the component shows 0 with source "In stock". This is consistent with BR-C4-04 and with what confirm will actually do. T-C4-01 is tested with no stock. |
| D-15 | Line date fields and the effective date | persisted computed column / C# | **No computed column** (C-13). Add `CalculatedLeadTimeDays`, `CalculatedDeliveryDate` (date) and `LeadTimeCalculatedAt` to inquiry, quotation and SO lines. The manual date is the inquiry's existing `EstimatedDeliveryDate`, the quotation's existing `PromisedDeliveryDate`, and a new `ManualDeliveryDate` on SO lines. `effectiveDeliveryDate = manual ?? calculated` is computed in the models. Conversions copy the dates: inquiry → quotation already copies Estimated → Promised and now also copies the Calculated* fields; quotation → SO copies Promised → `ManualDeliveryDate` plus Calculated*. An inquiry's CAN_SUPPLY/PARTIAL accepts the calculated date when no estimate was typed. |
| D-16 | Endpoints for line lead times; when SO dates can be edited | | Per line: `POST …/lines/{lineUuid}/lead-time` (calculates and stores) for inquiry (open), quotation (DRAFT) and SO (DRAFT); `PUT api/sale-orders/{uuid}/lines/{lineUuid}/delivery-date` (manual date or null) for DRAFT, CONFIRMED and PARTIALLY_FULFILLED lines, under the order lock, with no re-pricing. A later change does **not** reschedule existing production orders (shown as a warning). The full SO POST/PUT round-trips the four fields (C-14). Unsaved forms use `POST api/lead-time/calculate`. |
| D-17 | When and how production orders are created (C5) | inside confirm's lock / after commit synchronously / Hangfire per line | **After confirm commits**, like A33's deliveries. Step 1: under the order lock (`SaleOrderLocks.HoldsResource`, in its own Demand transaction), re-read the order (CONFIRMED / PARTIALLY_FULFILLED only), register one SALES_ORDER allocation demand per line **without** running allocation, and create one **DRAFT** PO per line (idempotent per SO line). Step 2: after the lock, **plan** each DRAFT PO (BR-C5-04). Planning is slow and cascades (chained POs, purchase drafts), so it must not run inside the lock. Visibility and recovery follow A33 D-12: `SaleOrder.ProductionCreationPendingSince`, a Hangfire sweep (every 15 min, entries pending ≥ 10 min, per org), a "Create production orders" button, and `productionOrders[]` plus a failure banner in the confirm result. |
| D-18 | How the PO records its SO and route | spec columns / reuse Source* | Reuse `SourceType/SourceUuid/SourceLineUuid/SourceReference` (no SO columns). Add `FulfillmentRouteUuid`, set **only** by the D-17 make-to-order path, which marks the PO as make-to-order and is C6's trigger. Also add `DeliveryOrderUuid` + `DeliveryNumber` (the latest delivery created from it) and `DeliveryCreationPendingSince`. **No backfill** (C-4). |
| D-19 | Planned dates (BR-C5-06) | | `post` = pick/pack + shipping (if the route has SHIP) + sales buffer + QC + transfer (resolved values). `RequiredDate = max(today, effective date − post)`. `PlannedStartDate = max(today, RequiredDate − (level days + mfg buffer))`. With no effective date: `RequiredDate = today + the line's calculated manufacturing total`, `PlannedStartDate = tomorrow`, capped at RequiredDate. The demand's required date = `RequiredDate`. All dates are date-only. |
| D-20 | When the delivery from production is created (C6) | (a) Per confirmed FGR. (b) When the PO turns COMPLETED. | **(b), automatically** (spec §8.1): qty = min(`AcceptedQuantity` − quantity already on non-cancelled deliveries from this PO, the line's outstanding = Qty − Fulfilled − in flight). Plus a **"Create delivery now"** button on the PO detail (DELIVERY_CREATE) for shipping accepted goods early; the same formula makes the button and the automatic call safe to combine. A PO with no route, or not sourced from a SALES_ORDER, never creates a delivery (BR-C6-02). |
| D-21 | Yield shortfall, zero yield, and the partial-fulfilment setting | | **Deliveries made from production ignore `PartialFulfillmentAllowed`**, like A33 D-8: the setting governs choosing to ship early, not yield loss. When accepted < line quantity: set `SaleOrderLine.ProductionShortfallQty`, add a timeline entry, and notify (BR-C6-05). No automatic re-make (Appendix A). **Zero yield** is raised when a QI with `AcceptedQuantity = 0` is recorded on a make-to-order PO (FGR can't happen then): shortfall = the line quantity, plus a notification. The PO stays in QUALITY_INSPECTION, which is unchanged A30 behaviour. |
| D-22 | Cancel cascade (T-C5-07) | which POs; where it runs | Runs inside the cancel's lock, before the hold release, like the A33 canceller. It covers **every** SALES_ORDER-sourced PO of the order (make-to-order and A30 make-to-shortage alike; this fixes C-10). A PO in DRAFT / PLANNED / MATERIAL_PENDING / READY with nothing issued is cancelled through `ProductionOrderService.CancelAsync`. One that is IN_PROGRESS or later, or has issued material, keeps running and is listed in the response and the timeline. Then every open SALES_ORDER allocation demand of the order is cancelled. A PO that keeps running finishes into stock: its delivery creator call finds the order cancelled and creates nothing. |
| D-23 | Notifications | | Single recipients and string types, following A30's pattern. PO created: the existing `PROD_CREATED` (to the supervisor). New: `SO_DELIVERY_FROM_PRODUCTION` (to the SO creator), `PROD_SHORTFALL` (to the SO creator and the PO creator), `PROD_ZERO_YIELD` (to the PO creator's supervisor and the SO creator), and `SO_PRODUCTION_FAILED` (to the confirming user). The "warehouse team" gets no broadcast (no such mechanism exists); the DRAFT delivery appears in their delivery list. |
| D-24 | Permissions | | New: **`LEAD_TIME_DEFAULTS_MANAGE`** (PUT defaults), seeded to System Admin and Org Admin (through `All`), Inventory Manager and Supply Dept Admin; role-editor group "Inventory". Variant lead-time PUT: **`STOCK_MANAGE`** (the code product master data such as pricing rules already uses). Reads (defaults, variant lead times) accept any of `LEAD_TIME_DEFAULTS_MANAGE`, `INVENTORY_VIEW`, `STOCK_MANAGE`, `SALE_ORDER_VIEW`. Calculate accepts any of `SALE_ORDER_VIEW/CREATE/EDIT`, `SALE_INQUIRY_VIEW/EDIT`, `SALE_QUOTATION_VIEW/EDIT`, `INVENTORY_VIEW`, `STOCK_MANAGE`. Line lead-time endpoints need the document's EDIT code. Production recovery: any of `SALE_ORDER_CONFIRM`, `PROD_CREATE`. "Create delivery now": `DELIVERY_CREATE`. Route category uses the existing `FULFILLMENT_ROUTE_MANAGE`. Every new action is gated (ratchet test). |
| D-25 | The SO "Fulfillment" view | new aggregate endpoint / extend the detail | **Extend the detail.** `SaleOrderModel.productionOrders[]` (number, status, line, planned and accepted quantities, route, delivery number) comes from the Shared reader and is readable with SALE_ORDER_VIEW. The Production tab gets route and "Delivery: pending / DLV-…" columns; the Deliveries tab notes "N lines awaiting production". Each line gets its route category and shortfall. No `GET …/fulfillment`. |
| D-26 | Where the "Lead Times" UI goes (no variant page exists) | variant dialog / product-detail tab | **A "Lead Times" tab on product detail**: pick a variant, then edit its 8 components with "Variant override / Org default / Supplier / BOM" source badges. Components are hidden per BR-C3-04/05, judged by the variant's route (or the org's SHIP default when the variant has none). Editable with STOCK_MANAGE, read-only otherwise. |
| D-27 | Calculator cache (BR-C4-06) | | `IMemoryCache` (already registered), 5 min, key = (org, variant, route, exact quantity, UTC date). **The org is in the key** to keep tenants apart. "Quantity bucket" = the exact quantity, since stock-aware results depend on it. T-C4-08 counts calls to the BOM reader. |
| D-28 | Manual reserve (A32) on a MAKE_TO_ORDER line | allow / refuse | **Allow** (A32 unchanged); it is the post-confirm make-to-stock escape. The UI warns "PROD-x still makes the full quantity". The production order is not reduced automatically. |
| D-29 | Logistics API for C6 | new member on `ISaleOrderDeliveryCreator` / new interface | **A new `IProductionDeliveryCreator`**: test fakes implement the A33 interface, so adding a member would break them. It is implemented in Logistics and shares the A33 creator's internals: it takes `SaleOrderLocks.HoldsResource` in its own transaction, re-reads the order (CONFIRMED / PARTIALLY_FULFILLED, else it skips with a reason), applies the D-20 quantity formula, and is idempotent through `delivery_orders.ProductionOrderUuid`. Delivery mode and steps snapshot come from the PO's route, even if the route was deactivated since (A33 D-10). Ship-from = the warehouse of the line's SALES_ORDER hold (split as in A33), else the PO's output/production warehouse. `RequestedDate` = the line's effective date, else the header's. DRAFT (BR-C6-07). |

## 4. Proposed design per change (repo terms)

### 4.1 Shared contracts (written first by LOG, file `src/SMS.Shared/Common/RouteClassificationContracts.cs`)

Every method takes the org explicitly and treats other orgs' rows as absent. All are optional-safe: a missing
implementation means the feature is off.

```csharp
public static class FulfillmentRouteCategory { Stock="STOCK"; Manufacture="MANUFACTURE"; Buy="BUY"; DropShip="DROPSHIP"; All; Active=[Stock,Manufacture]; }
// FulfillmentRouteContracts.cs: FulfillmentRouteSummary gains  public string Category { get; init; } = "STOCK";  (init-only: positional callers unaffected)

// Inventory implements — C4
interface ILeadTimeCalculator { Task<LeadTimeResult> CalculateAsync(Guid org, LeadTimeRequest req, CancellationToken ct = default); }
record LeadTimeRequest(Guid VariantUuid, decimal Quantity, Guid? RouteUuid = null, DateTime? RequestedDate = null);
record LeadTimeComponentResult(string Code, string Name, int Days, string Source, string? Detail = null);
//   Code: SUPPLIER|MANUFACTURING|MFG_BUFFER|QC|TRANSFER|PICK_PACK|SHIPPING|SALES_BUFFER; Source: VARIANT|ORG_DEFAULT|SUPPLIER_RATE|SUPPLIER_RECORD|PRODUCT|BOM|IN_STOCK|SYSTEM_DEFAULT
record LeadTimeResult(int TotalLeadTimeDays, DateTime EarliestDeliveryDate, DateTime? LatestStartDate, bool? MeetsRequestedDate,
                      Guid? RouteUuid, string? RouteCode, string RouteCategory, IReadOnlyList<LeadTimeComponentResult> Components, DateTime CalculatedAt);

// Material implements — C4 / C5 gate
interface IBomStructureReader { Task<IReadOnlyDictionary<Guid, BomStructure>> GetActiveBomsAsync(Guid org, IReadOnlyCollection<Guid> variantUuids, CancellationToken ct = default); }
record BomInput(Guid MaterialVariantUuid, Guid MaterialProductUuid, decimal Quantity, decimal ScrapPercentage);
record BomStructure(Guid BomUuid, string BomNumber, int Version, decimal BaseQuantity, IReadOnlyList<BomInput> Inputs);
interface IManufacturingReadiness { Task<IReadOnlyDictionary<Guid, ManufacturingReadinessInfo>> CheckAsync(Guid org, IReadOnlyCollection<Guid> variantUuids, CancellationToken ct = default); }
record ManufacturingReadinessInfo(Guid VariantUuid, string DisplayName, bool IsManufactured, bool HasActiveBom, bool HasProductionWarehouse);

// Material implements — C5 (make-to-order); IProductionDemandService (A30 make-to-shortage) stays untouched
interface ISaleOrderProductionService
{
    Task<IReadOnlyList<SaleOrderProductionRef>> CreateDraftsAsync(Guid org, SaleOrderProductionRequest req, int userId, CancellationToken ct = default); // idempotent per SO line
    Task PlanDraftsAsync(Guid org, IReadOnlyCollection<Guid> productionOrderUuids, int userId, CancellationToken ct = default);  // skips non-DRAFT
    Task<SaleOrderProductionCancellation> CancelForSaleOrderAsync(Guid org, Guid saleOrderUuid, string reason, int userId, CancellationToken ct = default);
    Task<IReadOnlyList<SaleOrderProductionRef>> GetForSaleOrderAsync(Guid org, Guid saleOrderUuid, CancellationToken ct = default);
}
record SaleOrderProductionLine(Guid SoLineUuid, Guid VariantUuid, decimal Quantity, Guid FulfillmentRouteUuid, DateTime RequiredDate, DateTime? PlannedStartDate);
record SaleOrderProductionRequest(Guid SaleOrderUuid, string SoNumber, Guid TraceId, int Priority, IReadOnlyList<SaleOrderProductionLine> Lines);
record SaleOrderProductionRef(Guid ProductionOrderUuid, string ProductionNumber, Guid? SoLineUuid, string Status, decimal PlannedQuantity,
                              decimal AcceptedQuantity, Guid? FulfillmentRouteUuid, Guid? DeliveryOrderUuid, string? DeliveryNumber, bool Created);
record SaleOrderProductionCancellation(IReadOnlyList<SaleOrderProductionRef> Cancelled, IReadOnlyList<SaleOrderProductionRef> KeptRunning);

// Logistics implements — C6
interface IProductionDeliveryCreator { Task<ProductionDeliveryResult> CreateForProductionOrderAsync(Guid org, ProductionDeliveryRequest req, int userId, CancellationToken ct = default); }
record ProductionDeliveryRequest(Guid ProductionOrderUuid, string ProductionNumber, Guid SaleOrderUuid, Guid SoLineUuid, Guid RouteUuid, decimal AcceptedQuantity, Guid FallbackWarehouseUuid);
record ProductionDeliveryResult(CreatedSaleOrderDelivery? Delivery, decimal QuantityCreated, string? SkippedReason);

// Demand implements — C6 feedback (shortfall / zero yield / delivery created → SO line, timeline, notifications)
interface ISaleOrderProductionFeedback { Task RecordOutcomeAsync(Guid org, ProductionOutcome outcome, int userId, CancellationToken ct = default); }
record ProductionOutcome(Guid SaleOrderUuid, Guid SoLineUuid, Guid ProductionOrderUuid, string ProductionNumber,
                         decimal PlannedQuantity, decimal AcceptedQuantity, string? DeliveryNumber, bool ZeroYield);

// Suppliers implements — D-11 tier 4 (optional)
interface ISupplierLeadTimeLookup { Task<IReadOnlyDictionary<Guid, int>> GetAsync(Guid org, IReadOnlyCollection<Guid> supplierUuids, CancellationToken ct = default); }
```

Module reference rules, all respected: Demand → Inventory only; Material → Inventory, Demand; Logistics → Demand,
Material, Warehouse. Material → Logistics, Demand → Material and Inventory → anything all go through the interfaces above.

### 4.2 C1 — Route category (LOG)

- Entity `FulfillmentRoute.RouteCategory` (string, max 20, default STOCK) with a CHECK in the migration.
  `FulfillmentRouteModel.routeCategory`; `CreateFulfillmentRouteRequest.routeCategory?` (default STOCK);
  `UpdateFulfillmentRouteRequest.routeCategory?` (null = unchanged); list filter `?category=`.
- `FulfillmentRouteService`:
  - BR-C1-01: unknown category → 400.
  - D-7: BUY/DROPSHIP → 400.
  - D-8: a category change on a system, default or in-use route → 409, using the existing `UsageOfAsync`.
  - D-6: set-default on MANUFACTURE → 400.
  - `ToSummary` and `ToModel` carry the category.
- Seeder (D-9): `SeedRoute` gains `Category`; add `MFG_PICK_SHIP` ("Manufacture → Pick & Ship", PICK, GOODS_ISSUE,
  SHIP, order 40) and `MFG_PICK_PACK_SHIP` ("Manufacture → Pick, Pack & Ship", PICK, PACK, GOODS_ISSUE, SHIP, order 50).
  Both non-default; the seeder stamps their category.
- **Creator skip (C-3).** `SaleOrderDeliveryCreator`: a line whose route category is MANUFACTURE is skipped with the
  reason "Line N is made to order: its delivery is created when production order completes." This covers confirm, the
  D-12 sweep and `create-deliveries`.
- Delivery: column `ProductionOrderUuid`. `DeliveryFilter.productionOrderUuid`; list and detail expose
  `productionOrderUuid`.
- Permissions (D-24): `PermissionCodes.cs` (+ `All`), `AuthDataSeeder.cs` (definition and grants, by code),
  `AuthRepository.cs` grouping, frontend `P` constant.

### 4.3 C2 — Route-based classification (INV)

- `VariantFulfillmentRouteService.SetRouteAsync` (D-3, D-9): a MANUFACTURE route needs
  `Product.SupplyMethod == MANUFACTURE` (400) and the org's MODULE_MANUFACTURING (`ITenantSnapshotProvider`, 400).
- `AssignByCategoryAsync` with a MANUFACTURE route narrows its scope to MANUFACTURE products; the rest count as
  `skipped`.
- Product list: `GET api/products?routeCategory=MANUFACTURE` (T-C2-04). Route uuids come from
  `IFulfillmentRouteLookup.ListActiveAsync` filtered by category, then the filter is
  `Variants.Any(v => uuids.Contains(v.FulfillmentRouteUuid))`. The legacy `manufacturable` filter stays.
- Variant model: `fulfillmentRouteCategory`, plus the lead-time overrides (read-only).

### 4.4 C3 — Lead times (INV)

- `ProductVariant` gains 7 nullable ints: `ManufacturingLeadTimeDays`, `ManufacturingBufferDays`,
  `QualityInspectionDays`, `InternalTransferDays`, `PickPackDays`, `ShippingLeadTimeDays`, `SalesBufferDays`. The
  existing `LeadTimeDays` becomes the supplier override (D-11).
- New `LeadTimeDefaults` (`ITenantScopedEntity`): `Id`, `UUID`, `OrganizationId` (unique), `PickPackDays = 1`,
  `ShippingLeadTimeDays = 3`, `SalesBufferDays = 1`, `ManufacturingBufferDays = 0`, `QualityInspectionDays = 0`,
  `InternalTransferDays = 0`, `CreatedBy/Date`, `ModifiedBy/Date`, `RowVersion`.
- `LeadTimeDefaultsService`:
  - GET reads the own org explicitly; a missing row → defaults with `isSaved: false`.
  - PUT upserts. A unique-index race → re-read and update. Validation: BR-C3-01 (≥ 0), at most 365.
  - `ModifiedBy`/`ModifiedDate` are stamped. The spec asks for no audit table, so none is added.
- `VariantLeadTimeService`: GET returns, per component, the stored value, the resolved value, its `source`, and
  `visible`. Visibility comes from the variant's route, or the org's SHIP default when the variant has none
  (BR-C3-04/05). PUT accepts 8 nullable values (null = use the default), each 0–3650, gated by STOCK_MANAGE.
- `LeadTimeResolver` (pure, unit-tested): variant → org default → 0 (BR-C3-02).

### 4.5 C4 — Calculator and line lead times (INV + MFG reader + DEM endpoints)

- `LeadTimeCalculator : ILeadTimeCalculator` (Inventory):
  1. Load the variant, its product, the route (request route, else the variant's, else the org's SHIP default), and
     the defaults.
  2. MANUFACTURE: `MANUFACTURING` = BOM-aware total (D-12/D-13), then `MFG_BUFFER`.
     STOCK: `SUPPLIER` (D-11, stock-aware D-14).
  3. Then `QC`, `TRANSFER`, `PICK_PACK`, `SHIPPING` (only when the route has SHIP) and `SALES_BUFFER`.
  4. Zero-day optional components are omitted, as in the spec; PICK_PACK and SHIPPING always show.
  5. `EarliestDeliveryDate = UTC today + total` (calendar days; there is no working calendar). With `RequestedDate`:
     `LatestStartDate = requested − total` and `MeetsRequestedDate`.
- BOM-aware total. For each level:
  `levelDays + max over short inputs of (manufactured ? recurse(shortfall) : supplier lead)`, where
  `required = qty × input.Quantity ÷ BaseQuantity × (1 + scrap%)` and
  `shortfall = required − free stock in the parent's production warehouse`.
  - Breadth-first per level: one `IBomStructureReader` call, one stock query and one supplier query per level.
  - Depth 10 + visited set; a cycle gives 0 and a "cycle" detail component.
- Cache: D-27.
- Endpoints (Inventory, `[RequiresFeature("MODULE_INVENTORY")]`):
  - `POST api/lead-time/calculate` and `POST api/lead-time/calculate-manufacturing` (the latter returns the per-level
    tree);
  - `GET/PUT api/lead-time/defaults`;
  - `GET/PUT api/variants/{uuid}/lead-times`.
- Demand line endpoints (D-16). Each resolves the line's effective route (A33 resolver; inquiry and quotation use the
  variant route, else the SHIP default), calls `ILeadTimeCalculator` with the line's quantity, and stores the
  Calculated* fields. The response is the line plus `components[]`. Line models gain `calculatedLeadTimeDays`,
  `calculatedDeliveryDate`, `manualDeliveryDate` (SO only), `effectiveDeliveryDate`, `deliveryDateSource`
  (`CALCULATED | MANUAL | NONE`) and `leadTimeCalculatedAt`.

### 4.6 C5 — Confirm split (DEM orchestrates; MFG and LOG supply)

Confirm orchestration (`SaleOrderService.ConfirmWithResultAsync`):

1. `OwnOrders` check (unchanged).
2. **Under the order lock** (`ConfirmHeldAsync`, unchanged lock):
   1. DRAFT check and self-pickup check (unchanged).
   2. A33 route resolution; each resolved route now carries `Category`.
   3. **MFG gate (D-5).** For routable lines with category MANUFACTURE: check tenant features, then call
      `IManufacturingReadiness.CheckAsync` once. Blockers are appended to the A33 list; any blocker → 400 (all of
      them, one per line).
   4. `CheckAndReserveAsync(uuid, userId, makeToOrder: set of line uuids)`. Make-to-order lines get
      `AvailableQtyAtConfirm` (for information), `DeficitQty = Quantity`, `FulfillmentMode = MAKE_TO_ORDER` and status
      OPEN; nothing is reserved for them (C-2). Other lines are handled as today.
   5. Snapshot route + source (D-16 A33), **plus** `FulfillmentRouteCategory` on each line.
   6. `createDeliveries` = A33 condition **and** at least one routable STOCK line. `createProduction` = at least one
      make-to-order line and `ISaleOrderProductionService` registered; set `ProductionCreationPendingSince`.
   7. Status CONFIRMED; one `SaveChanges` (which commits the lock's transaction).
3. After the lock:
   1. Timeline and email as today.
   2. Deficit jobs **only for lines that aren't MAKE_TO_ORDER** (C-1).
   3. A33 deliveries (the creator skips make-to-order lines anyway).
4. **Production** (`SaleOrderProductionCreation.RunAsync`, never throws, D-17):
   1. Under the order lock, in its own Demand transaction: re-read the order (CONFIRMED or PARTIALLY_FULFILLED, else
      clear and stop).
   2. Per make-to-order line: `ILeadTimeCalculator` (cached) gives the D-19 dates; `IAllocationEngine.RegisterDemandAsync`
      (SALES_ORDER, SO, line, `RequiredDate`) **without** `AllocateAsync`; then
      `ISaleOrderProductionService.CreateDraftsAsync`. Commit.
   3. After the lock: `PlanDraftsAsync` (BR-C5-04: BOM explosion, PMRs, SR engine, the A30 notifications).
   4. Clear the pending flag, and append `SO_PRODUCTION_CREATED` (or `_FAILED`) to the timeline.
   5. The result gets `productionOrders[]`, `productionCreationFailed` and `productionMessage`.
5. Recovery:
   - `SaleOrderProductionSweepJob`: a new class and recurring id, every 15 min, entries pending ≥ 10 min, per org
     under `HangfireTenantScope`, user 0. A33's sweep is left untouched.
   - `POST api/sale-orders/{uuid}/create-production-orders`: any of SALE_ORDER_CONFIRM, PROD_CREATE.

Material: `SaleOrderProductionService.CreateDraftsAsync`:
- For each line, if a non-cancelled PO exists with (SALES_ORDER, SO, line) **and a route**, return it.
- Otherwise call `ProductionOrderRepository.CreateAsync(… SourceType SALES_ORDER, Source*, RequiredDate,
  PlannedStartDate, priority)`, set `FulfillmentRouteUuid`, append `PROD_CREATED` to the timeline, and send the
  existing notification.
- The trace id is the SO's (A30-P5-07).
- Extract the active-BOM rule from `ProductionOrderRepository.cs:480-495` into a shared internal `ActiveBomResolver`.
  `IBomStructureReader` and `IManufacturingReadiness` use it too, so the confirm gate and PO creation can never
  disagree.

Delivery preview: make-to-order lines leave the delivery groups and are listed under `productionLines[]` ("A
production order will be created; its delivery follows when production completes"). Lines carry `routeCategory`.

**SO cancel (D-22)**, inside `CancelHeldAsync`, after the A33 delivery canceller and before the hold release:
1. `ISaleOrderProductionService.CancelForSaleOrderAsync`.
2. `IAllocationEngine.GetDemandsAsync(SALES_ORDER, soUuid)` → `CancelDemandAsync` for each.
3. The response gains `cancelledProductionOrders[]` and `runningProductionOrders[]`, plus a timeline entry.

Also clear `ProductionCreationPendingSince`.

### 4.7 C6 — Production completion → delivery (MFG hook + LOG creator + DEM feedback)

FGR hook in `FinishedGoodsReceiptService.ConfirmCoreAsync`:
1. Inside the cross-context work, if the PO is make-to-order (`FulfillmentRouteUuid != null`, `SourceType`
   SALES_ORDER, `SourceLineUuid` set) **and** it turns COMPLETED here, set `DeliveryCreationPendingSince` in the same
   commit.
2. After `SupplyReceivedAsync` + `AllocateAsync` (unchanged; the SO line now holds the goods if allocation gave them),
   run `ProductionDeliveryHandoff.RunAsync`, which never throws. It calls `IProductionDeliveryCreator` with
   `AcceptedQuantity` and fallback warehouse = output ?? production. On return it stamps `DeliveryOrderUuid/Number`,
   clears the flag, calls `ISaleOrderProductionFeedback.RecordOutcomeAsync` (shortfall if accepted < line quantity),
   and appends `PROD_DELIVERY_CREATED` to the PO's timeline.
3. A failure leaves the flag set for `ProductionDeliverySweepJob` (Material, every 15 min, ≥ 10 min, per org).
4. **No transaction or lock is held at this point.** `RunAcrossContextsAsync` has already cleared both contexts'
   transactions (`FinishedGoodsReceiptService.cs:257-259`), so the creator can take the SO lock on its own connection.
   This is the A30 P4-21 bug-3 lesson: never call another module from inside that block.

Other parts of C6:
- **QI hook (D-21):** after a QI with `AcceptedQuantity == 0` is recorded on a make-to-order PO, send feedback with
  `ZeroYield` and the `PROD_ZERO_YIELD` notification.
- **"Create delivery now":** `POST api/production-orders/{uuid}/create-delivery` (DELIVERY_CREATE; in
  `ProductionOrdersController`, MODULE_MANUFACTURING; 400 unless the PO is make-to-order with `AcceptedQuantity > 0`).
  It runs the same handoff.
- **Logistics `ProductionDeliveryCreator`:** see D-29. The quantity already "from this PO" = the sum of `QtyOrdered` on
  non-cancelled, non-deleted deliveries with that `ProductionOrderUuid`. All reads are under the SO lock, with the org
  explicit.
- **Demand `ISaleOrderProductionFeedback`:** sets `ProductionShortfallQty`; appends `SO_DELIVERY_FROM_PRODUCTION` /
  `SO_PRODUCTION_SHORTFALL` to the timeline; sends the notifications (D-23).
- **PO models:** `fulfillmentRouteUuid/Code/Name/Category` (route names via `IFulfillmentRouteLookup`),
  `deliveryOrderUuid`, `deliveryNumber`, `saleOrderLineNumber`, `shortfallQuantity` (derived). The frontend reads live
  delivery status from `GET api/logistics/deliveries?productionOrderUuid=` (DELIVERY_VIEW); Material can't reference
  Logistics.

### 4.8 Endpoints

| Endpoint | Owner | Permission |
|---|---|---|
| `GET/POST/PUT api/fulfillment-routes…`: `routeCategory`, `?category=` | Logistics | existing (A33 §2) |
| `GET api/lead-time/defaults` / `PUT …` | Inventory | any of LEAD_TIME_DEFAULTS_MANAGE, INVENTORY_VIEW, STOCK_MANAGE, SALE_ORDER_VIEW / **LEAD_TIME_DEFAULTS_MANAGE** |
| `POST api/lead-time/calculate`, `POST api/lead-time/calculate-manufacturing` | Inventory | any of SALE_ORDER_VIEW/CREATE/EDIT, SALE_INQUIRY_VIEW/EDIT, SALE_QUOTATION_VIEW/EDIT, INVENTORY_VIEW, STOCK_MANAGE |
| `GET api/variants/{uuid}/lead-times` / `PUT …` | Inventory | read any-of as for defaults / STOCK_MANAGE |
| `GET api/products?routeCategory=` | Inventory | existing (ungated legacy read) |
| `POST api/sale-inquiries/{uuid}/lines/{lineUuid}/lead-time` | Demand | SALE_INQUIRY_EDIT |
| `POST api/sale-quotations/{uuid}/lines/{lineUuid}/lead-time` | Demand | SALE_QUOTATION_EDIT |
| `POST api/sale-orders/{uuid}/lines/{lineUuid}/lead-time`; `PUT …/delivery-date` | Demand | SALE_ORDER_EDIT |
| `POST api/sale-orders/{uuid}/confirm` (result + `productionOrders[]`), `…/cancel` (result + production lists) | Demand | existing |
| `GET api/sale-orders/{uuid}` (+ `productionOrders[]`, line fields), `…/delivery-preview` (+ `productionLines[]`) | Demand | existing |
| `POST api/sale-orders/{uuid}/create-production-orders` | Demand | any of SALE_ORDER_CONFIRM, PROD_CREATE |
| `POST api/production-orders/{uuid}/create-delivery` | Material | DELIVERY_CREATE |
| `GET api/production-orders…` (+ route and delivery fields) | Material | existing PROD_VIEW |
| `GET api/logistics/deliveries?productionOrderUuid=` | Logistics | existing DELIVERY_VIEW |

### 4.9 Frontend

| Area | Change | Files |
|---|---|---|
| Routes (FE-ADMIN) | Category dropdown (STOCK, MANUFACTURE; BUY/DROPSHIP disabled) and the §3.6 note; category column and filter; MANUFACTURE routes hidden when the org lacks MODULE_MANUFACTURING; set-default hidden for MANUFACTURE | `pages/logistics/fulfillment-routes/*`, `services/fulfillment-routes.service.ts` (+ spec) |
| Variant route (FE-ADMIN) | Dropdown offers MANUFACTURE routes only for MANUFACTURE products; "Make to order" tag in the variants table | `product-detail/variant-route-field/*`, `product-detail.component.*` |
| Lead Times tab (FE-ADMIN, D-26) | Per-variant 8-component editor with source badges, visibility rules, total, "Recalculate from BOM" (view only), "Reset to org defaults" | `product-detail/lead-times-tab/` (new), `services/lead-time.service.ts` (new) |
| Settings (FE-ADMIN) | "Lead Time Defaults" page; route guard `permissionGuard(P.LEAD_TIME_DEFAULTS_MANAGE, P.INVENTORY_VIEW)`; menu entry after "Sale Order Settings" | `pages/lead-time-defaults/` (new), `pages/pages.routes.ts`, `layout/component/app.menu.ts` |
| Product list (FE-ADMIN) | Route-category filter | `inventory/products/product-list/*`, `services/inventory.service.ts` |
| ⏱ popover (FE-SALES) | Shared component: Calculate, breakdown, Apply / Override date, indicator (⏱ Calculated / ✎ Manual / — Not calculated), date-only helpers | `pages/sales/lead-time-popover/` (new), used by `sale-inquiries/sale-inquiry-lines`, `quotations/sale-quotation-line-editor`, `sale-orders/sale-order-form`, `sale-order-detail` |
| SO (FE-SALES) | Blocker texts for the 4 new codes; preview "production" group; confirm result listing POs + failure banner + "Create production orders"; Production tab route/delivery columns; Deliveries tab "awaiting production"; line shortfall badge; cancel result listing POs; MAKE_TO_ORDER "Sourcing" label; D-28 warning | `sale-orders/sale-order-detail/*`, `sale-order-form/*`, `delivery-preview-panel/*`, `services/sale-order.service.ts`, `services/sales-preorder.service.ts` |
| Production order (FE-SALES) | SO and line link (exists) + route; "Auto-created delivery" card (number, live status, quantity, shortfall note, View); "Create delivery now" (DELIVERY_CREATE) | `manufacturing/production-orders/production-order-detail/*`, `production-order-list/*`, `services/production-order.service.ts`, `services/logistics.service.ts` |

## 5. Migrations (one owner per DbContext; additive, guarded SQL, no seed rows, no backfill)

Every API start runs `Migrate()` against the drifted SMSGlobal, except Lookups, which this addendum doesn't touch.
Each migration needs: guarded `IF COL_LENGTH(...) IS NULL` / `sys.indexes` / `sys.check_constraints`; one statement per
`Sql()` call when an index uses a column added earlier in the same migration (A33 precedent); up/down/up on LocalDB
plus a re-run on an already-migrated DB; and `dotnet ef migrations has-pending-model-changes` clean. **Never**
`database update` against SMSGlobal. Cross-context references are scalar Guids.

| DbContext → owner | Migration | Adds |
|---|---|---|
| **LogisticsDbContext → LOG** | `A34_RouteCategoryAndProductionDeliveries` | `fulfillment_routes.RouteCategory nvarchar(20) NOT NULL CONSTRAINT DF_fulfillment_routes_RouteCategory DEFAULT 'STOCK'` (existing rows → STOCK) + `CK_fulfillment_routes_RouteCategory CHECK (RouteCategory IN ('STOCK','MANUFACTURE','BUY','DROPSHIP'))`; `delivery_orders.ProductionOrderUuid uniqueidentifier NULL` + filtered index. Seeds: the C# seeder, its provisioning handler (new orgs) and the existing `UseLogisticsModule` backfill (existing orgs) add the 2 MFG routes, matched by code |
| **InventoryDbContext → INV** | `A34_LeadTimes` | `ProductVariants`: 7 × `int NULL` (§4.4). New `inventory.LeadTimeDefaults` (`CREATE TABLE` guarded by `OBJECT_ID IS NULL`), unique `(OrganizationId)`, unique `UUID`, `RowVersion`, defaults 1/3/1/0/0/0. No rows inserted (D-10). Inventory migrations don't replay on an empty DB (memory A30), so test the `UpOperations` directly, as A33 INV did |
| **DemandDbContext → DEM** | `A34_LeadTimeAndProductionOnSales` | `sale_inquiry_lines`, `sale_quotation_lines`, `sale_order_lines`: `CalculatedLeadTimeDays int NULL`, `CalculatedDeliveryDate date NULL`, `LeadTimeCalculatedAt datetime2 NULL`. `sale_order_lines`: `ManualDeliveryDate date NULL`, `FulfillmentRouteCategory nvarchar(20) NULL`, `ProductionShortfallQty decimal(18,4) NULL`. `sale_orders`: `ProductionCreationPendingSince datetime2 NULL` + filtered index (REV-01 shape). `MAKE_TO_ORDER` fits `FulfillmentMode nvarchar(20)` with no schema change |
| **MaterialDbContext → MFG** | `A34_ProductionOrderRouteAndDelivery` | `production_orders`: `FulfillmentRouteUuid uniqueidentifier NULL` (+ filtered index), `DeliveryOrderUuid uniqueidentifier NULL`, `DeliveryNumber nvarchar(50) NULL`, `DeliveryCreationPendingSince datetime2 NULL` (+ filtered index). **No backfill** (C-4) |
| AuthDbContext | none | Permission definitions and grants are seeded in C# (`AuthDataSeeder.cs`) at startup, matched by code |

Migrations run Demand → Inventory → Material → Logistics. Nothing crosses schemas, so the order doesn't matter.

## 6. Agent split (8 agents; one migration owner per DbContext)

| Agent | Scope / file ownership | Migrations of | Wave / depends on |
|---|---|---|---|
| **LOG** | **W0:** `src/SMS.Shared/Common/RouteClassificationContracts.cs`, the Category property in `FulfillmentRouteContracts.cs`, `docs/route-classification/API-CONTRACT.md` (owner; changes by message), the `LEAD_TIME_DEFAULTS_MANAGE` code (`PermissionCodes.cs`, `AuthDataSeeder.cs`, `AuthRepository.cs`, frontend `P`). **W1:** route entity/maps/service/model/controller, seeder, creator skip, delivery column + filter. **W2:** `ProductionDeliveryCreator` (`src/SMS.Modules.Logistics/Services/`), registration in `ILogisticsModule.cs` | **LogisticsDbContext** | W0 first (½ day); W1 ∥; W2 ∥ with MFG/DEM |
| **INV** | Variant columns, `LeadTimeDefaults` entity/map/service/controller, `VariantLeadTimeService`, resolver, `LeadTimeCalculator` (+ cache), `VariantFulfillmentRouteService` rules (D-3, D-9), product-list filter, `IInventoryModule.cs` registrations; Suppliers' `ISupplierLeadTimeLookup` (one small file in `src/SMS.Modules.Suppliers/Services/`) | **InventoryDbContext** | W1 schema/defaults/variant API; W2 calculator, against MFG's reader (fake until it lands) |
| **MFG** | `ProductionOrder` columns/maps/models, `ActiveBomResolver` extraction, `BomStructureReader`, `ManufacturingReadiness`, `SaleOrderProductionService`, Material `IFulfillmentRouteUsage`, FGR and QI hooks, `ProductionDeliveryHandoff`, `ProductionDeliverySweepJob`, the `create-delivery` endpoint, `IMaterialModule.cs` | **MaterialDbContext** | W1 schema + readers + service; W2 hooks (LOG creator via fake until it lands) |
| **DEM** | Line and SO columns/maps/models; resolver blockers; `AvailabilityCheckService` MTO branch; confirm orchestration; `SaleOrderProductionCreation` + sweep + recovery endpoint; `AutoPoCreationJob` skip; cancel cascade; preview `productionLines`; line lead-time endpoints + request round-trip + conversion copies; `ISaleOrderProductionFeedback`; SO detail `productionOrders[]`; `IDemandModule.cs` | **DemandDbContext** | W1 schema + line endpoints + gate; W2 orchestration + cancel (needs MFG W1, LOG W1) |
| **FE-ADMIN** | Route editor/list, variant route field, Lead Times tab, Lead Time Defaults page, product-list filter, `pages.routes.ts` + `app.menu.ts` (**sole editor**; FE-SALES asks by message), `services/lead-time.service.ts`, `fulfillment-routes.service.ts`, `inventory.service.ts` | — | W1 from API-CONTRACT; wires up once LOG/INV APIs land |
| **FE-SALES** | ⏱ popover + the inquiry/quotation/SO line integrations, SO form/detail/preview/confirm/cancel changes, production-order detail/list, `sale-order.service.ts`, `sales-preorder.service.ts`, `production-order.service.ts`, `logistics.service.ts` | — | W2 (needs the DEM, MFG and LOG W2 APIs; start against the contract) |
| **QA** | `tests/SMS.Integration.Tests/RouteClassification/*` (new folder + `RouteClassificationKit.cs`), migration tests, end-to-end; the re-run list in §7 | — | W3 (per-feature host tests can start as each W2 item lands) |
| **REV** | Independent review of every agent's diff: correctness, tenancy, permissions, locks and transactions, migrations, A30/A32/A33 regressions. Reports to lead and owner | — | continuous |

Rules: as in `docs/sales-preorder/AGENT-RULES.md`, plus:
- scratch base `scratchpad\a34\<role>\`; checkpoints `scratchpad\checkpoints\a34-<role>.md`;
- never build SMS.API or into `src\SMS.API\bin`; never touch SMSGlobal;
- test-first; `tests/SMS.Integration.Tests` one class per run.

Parallelism:
- The four backend agents can work in parallel in W1, because each owns its own context and the contracts land in W0.
- In W2, LOG's production creator, MFG's hooks, DEM's orchestration and INV's calculator run in parallel against the
  W0 contracts, using fakes.
- QA's end-to-end tests need everything.
- Sequencing hazards: `SaleOrderService.cs` and `AvailabilityCheckService.cs` (DEM only) and
  `FinishedGoodsReceiptService.cs` (MFG only). Nobody else edits them.

## 7. Test plan

Unit tests run on the in-memory provider unless marked. Host = LocalDB `WebApplicationFactory` classes in
`tests/SMS.Integration.Tests`, one class per `dotnet test` run, factory `SapAlignment/SapWebApplicationFactory.cs`
(jobs run through its fresh-scope helper).

| Test | Unit (owner, file) | Host class |
|---|---|---|
| T-C1-01 create MANUFACTURE route | LOG `tests/SMS.Modules.Logistics.Tests/Routes/RouteCategoryServiceTests` | `RouteClassification/RouteCategoryE2ETests` |
| T-C1-02 BUY refused | LOG same | — |
| T-C1-03 category change while in use (variants / SO lines / POs), system, default | LOG same (fake usages) | `RouteCategoryE2ETests` (real variant usage) |
| T-C1-04 new org → 5 seeds (3 STOCK + 2 MFG); backfill idempotent | LOG `RouteSeedingTests` (LocalDB) | `RouteClassificationTenantIsolationE2ETests` |
| T-C2-01/02 assign / unassign MFG route (D-3 validation, flags unchanged, BOMs kept) | INV `VariantRouteCategoryTests` | `RouteCategoryE2ETests` |
| T-C2-03 MFG variant without active BOM → BOM_MISSING (and the 3 other blockers) | DEM `SaleOrderManufactureGateTests` (fake readiness); MFG `ManufacturingReadinessTests` | `ManufactureConfirmE2ETests` |
| T-C2-04 filter by category | INV `ProductListRouteCategoryFilterTests` | — |
| T-C3-01..05 tab values, visibility, sources | INV `VariantLeadTimeServiceTests`, `LeadTimeResolverTests`; Karma `lead-times-tab.component.spec` | — |
| T-C3-06 defaults update (+ RBAC, missing-row defaults, upsert race) | INV `LeadTimeDefaultsServiceTests` (+ LocalDB race) | `LeadTimeE2ETests` |
| T-C4-01..05 calculator (stock, BOM in stock, short + supplier, nested, cycle) | INV `LeadTimeCalculatorTests` (fake `IBomStructureReader`, seeded stock) | `LeadTimeE2ETests` (real Material reader, 3-level chain) |
| T-C4-06/07 manual override set / cleared | DEM `SaleLineLeadTimeTests` | `LeadTimeE2ETests` |
| T-C4-08 cache (one reader call per key; org in the key) | INV `LeadTimeCalculatorCacheTests` | — |
| T-C5-01 all stock → deliveries only (A33 unchanged) | DEM `SaleOrderManufactureConfirmTests` | `ManufactureConfirmE2ETests` |
| T-C5-02/04/05 all make-to-order → POs (route, Source*, DRAFT on create → PLANNED with PMRs), no reservation, no deficit job, no delivery | DEM same (mocks); MFG `SaleOrderProductionServiceTests` | `ManufactureConfirmE2ETests` |
| T-C5-03 mixed order | DEM same | `ManufactureConfirmE2ETests` |
| T-C5-06 fulfillment view (detail `productionOrders[]`) | DEM model test; Karma `sale-order-detail.spec` | — |
| T-C5-07 cancel cascade (DRAFT..READY cancelled; IN_PROGRESS / issued kept; allocation demands cancelled; A30 make-to-shortage POs too) | MFG `CancelForSaleOrderTests`; DEM `SaleOrderCancelProductionTests` | `RouteClassificationRegressionE2ETests` |
| T-C6-01 completed → delivery for the accepted quantity, route steps | LOG `ProductionDeliveryCreatorTests` (LocalDB lock); MFG `FgrDeliveryHandoffTests` (fake creator) | `ProductionToDeliveryE2ETests` |
| T-C6-02 shortfall (delivery 95, line flagged, notifications) | MFG + DEM `ProductionFeedbackTests` | `ProductionToDeliveryE2ETests` |
| T-C6-03 zero yield (at QI) | MFG `QiZeroYieldTests` | `ProductionToDeliveryE2ETests` |
| T-C6-04 standalone / A30 make-to-shortage PO → no delivery | MFG same | `RouteClassificationRegressionE2ETests` |
| T-C6-05/06/07 steps snapshot, source SALE_ORDER, `DeliveryOrderUuid` stamped | LOG / MFG same | `ProductionToDeliveryE2ETests` |
| T-C6-08 SO view after completion | DEM model | `ProductionToDeliveryE2ETests` |
| T-C6-09 escape hatch (override to STOCK → delivery at confirm, no PO) | DEM `SaleOrderManufactureConfirmTests` | `ManufactureConfirmE2ETests` |
| T-C6-10 end to end: SO → PO → materials (GRN + manual allocation run, A31 C10) → production → QI → FGR → delivery → pick → ship → DELIVERED → invoiced | — | `RouteClassificationFullChainE2ETests` |
| Recovery: production sweep / button exactly once; delivery sweep / "Create delivery now" partial then completion (D-20 formula); replayed handoff creates nothing | DEM / MFG / LOG unit | `RouteClassificationLockingHostTests` (races: two confirm-recoveries; cancel vs production creation; cancel vs FGR handoff) |
| RBAC: new code, every new action gated, read any-of lists, frontend guard parity | controller-attribute unit tests per module | `RouteClassificationRbacE2ETests` + ratchet + `AnonymousEndpointsTests` |
| Tenancy: other org's route / variant / PO / SO → 404, super admin included; cache isolation; sweeps per org | per module | `RouteClassificationTenantIsolationE2ETests` |
| Migrations: up/down/up, re-run on a migrated DB, no pending model changes (×4) | each owner's `MigrationTests` (LocalDB) | — |

**Must stay green (re-run one class per run):** the 7 A33 classes (`FulfillmentRoutes/*`), the A32 classes
(`SalesPreOrder/*`), `Manufacturing/ManufacturingCycleTests` (A30 make-to-shortage on a STOCK route must be
untouched), `ProcurementCycle/ProcurementToIssueCycleTests`, `SaleOrderSettings/*`, `SapAlignment/SalesFlowE2ETests`,
`Security/*`. Then every module's unit suite and Karma, plus `ng build`. `MultiTenancyIsolationTests` has 11 failures
that predate this work; don't count them.

## 8. Risks

**Data on SMSGlobal** (counts unknown; the shared DB must not be queried)
- R-1. The startup backfill adds 2 routes, with their steps, to **every** organization on the first start. It is
  idempotent by code and never touches existing routes. An org that already owns a custom `MFG_*` code keeps its own
  route and gets no seed.
- R-2. All existing routes become STOCK through the column default. No existing variant, line or PO changes behaviour.
  Make-to-order starts only once someone assigns a MANUFACTURE route.
- R-3. No PO backfill (C-4). Existing A30 make-to-shortage orders keep their A33 deliveries and never trigger C6.
- R-4. Four migrations, additive and guarded. Inventory's chain doesn't replay from empty (test `UpOperations`
  directly). Lookups isn't used.
- R-5. `ProductVariant.LeadTimeDays` gains a meaning (supplier override). The app never writes it, but a value
  hand-loaded on SMSGlobal would start to count. Low.

**Concurrency and consistency**
- R-6. Confirm (Demand), production creation (Demand lock + Material writes) and planning (Material, outside the lock)
  are separate commits. A failure leaves a CONFIRMED order pending; the sweep and the button recover it, and
  `CreateDraftsAsync` is idempotent per line under the lock.
- R-7. FGR (Material commit) and the delivery (Logistics commit) are split. The pending flag, the sweep and the D-20
  formula make a replay create 0. The creator takes the SO lock; FGR holds no transaction or lock when it calls (A30
  P4-21 bug-3 lesson).
- R-8. Cancel vs production creation vs FGR handoff: all three take `SaleOrderLocks.HoldsResource`, so they serialize.
  A PO left running after cancel finishes into stock, and its handoff sees the order cancelled.
- R-9. **The allocation engine isn't strict pegging.** At FGR, the goods go to the highest-priority / earliest open
  demand for that variant. An earlier-dated competing SO line for the same variant can take them; the make-to-order
  delivery is still created, and release then reserves free stock or backorders. Document it; register the
  make-to-order demand with the order's priority.
- R-10. The calculator is called inside the creation lock (D-19 dates). It is cached and batched; a deep BOM is
  bounded by depth 10. If it becomes slow, compute the dates before taking the lock.

**Tenant isolation**
- R-11. New contracts take the org explicitly. The calculator cache key includes the org. Sweeps iterate orgs under
  `HangfireTenantScope`. The FGR handoff stamps the PO's org, not the caller's (a super admin may confirm another
  org's FGR).
- R-12. Route ↔ variant ↔ line ↔ PO references are unenforced Guids. Validate the same org at every write, through
  `IFulfillmentRouteLookup` with the org explicit.

**Behaviour and UX**
- R-13. A30 tests that seed products may now reach new code paths. `ManufacturingCycleTests` uses the org default
  (STOCK), so it should be unaffected; re-run it.
- R-14. A yield shortfall leaves the SO PARTIALLY_FULFILLED (no line short-close exists, C-17). This is a known gap,
  deferred by the spec.
- R-15. Wording: "Sourcing" now has MAKE_TO_ORDER, next to "Route category" MANUFACTURE. Label the first "Make to
  order", and show the category as a tag, never as "Sourcing".
- R-16. MODULE_MANUFACTURING without MODULE_LOGISTICS: routes are off (A33 D-11), so there is no make-to-order and the
  A30 make-to-shortage stays as it is. *Unverified whether such orgs exist.*
- R-17. A SELF_PICKUP order with a make-to-order line is blocked (C-16) until a MANUFACTURE route without SHIP exists.
  The confirm-gate message says how to create one.
