# Addendum 29 — SCM Commercial Enablement (knowledge base)

Source: `SMS_Implementation_Addendum_29_v1.3_SCM_Commercial.pdf` — SMS-FSD-ADD-029, v1.3, dated 2026-09-19,
"Logistics-Aligned (Delivery Orders + IStockReservationService)", status *Ready for Implementation*.
Scope: Business Partners | Sale Orders | Fulfillment | Invoicing | Customer Finance.

This file is the working reference for the tasks that implement the addendum. Part A records where the
spec's description of the *existing* system differs from what is actually in the repo (checked
2026-09-19), because Section 0 of the spec itself says to inspect the codebase first and follow its
patterns rather than the spec's sketches. Part B is the spec, section by section.

---

## Part A — Reality check: spec assumptions vs. the actual codebase

Verified by reading the code on 2026-09-19. Where these disagree, **the code wins** — the spec's intent
is honoured through the pattern that really exists.

| Spec says | Actually in the repo | Consequence |
|---|---|---|
| Frontend is **React 18** | **Angular 19** standalone components + PrimeNG 19, Karma/Jasmine specs (`SupplyChainFrontend/`) | All screens are Angular. Follow `pages/logistics/**` patterns (`data-testid`s, `LogisticsService`, `permissionGuard(P.X)`). |
| PKs are `BIGINT IDENTITY`, columns are `snake_case`, FKs like `partner_id BIGINT` | Entities have `int Id` **plus** `Guid Uuid`; every cross-module reference is a **Guid** (e.g. `Carrier.SupplierId` is `uniqueidentifier`, matched to `suppliers.Suppliers.UUID`, never `.Id`). Newer modules use snake_case tables (`logistics.*`, `demand.purchase_orders`), older ones PascalCase (`inventory.InventoryItems`, `suppliers.Suppliers`) | Model new FKs as Guids across module boundaries. Don't join on int ids across modules. Match the naming style of the schema you're adding to. |
| `procurement.PurchaseOrders` | `demand.purchase_orders` (SMS.Modules.Demand, `PurchaseOrderMaps.cs`) — there is no `procurement` schema | PO additions (`source`, `linked_so_*`, `auto_generated_*`) go on the Demand module's PO entity/migration. |
| `IStockReservationService` with `Reserve(ReservationRequest)`, `Release(id)`, `Consume(id)`, long ids, and "reserve whatever is available" | Exists in `SMS.Shared/Common/IStockReservationService.cs` (implemented in SMS.Modules.Inventory, resolved via DI). Real contract: `ReserveAsync(sourceType, sourceUuid, requests[], userId)` — **all-or-nothing**; `ReleaseBySourceAsync`, `ConsumeBySourceAsync`, `ConsumeLineAsync` (partial consume), `ReleaseAllocationAsync` (short pick), `GetBySourceAsync`, `GetAllocationsAsync`, `GetAvailableAsync(variantUuids, warehouseUuid?)`. `ReservationSourceType.SalesOrder = "SALES_ORDER"` **already declared** ("not in use yet; the ledger is shaped for it so it needs no schema change"). | Step 6 of the plan ("extend the service for SALES_ORDER") is mostly already done. **SPLIT scenario** cannot be "reserve what's there" in one call — call `GetAvailableAsync` first, then `ReserveAsync` for the coverable quantity (mirrors the delivery release-split in `DeliveryStateMachine`). Availability = best *single* warehouse, never summed across warehouses. |
| `inventory.stock_reservations` with `source_id BIGINT`; `inventory.stock_balances` with `on_hand_qty/reserved_qty/available_qty` | Counter lives on `inventory.InventoryItems.QtyOnHand / QtyReserved` (per variant + warehouse + bin/batch/serial); available = OnHand − Reserved. Reservation rows are keyed by `sourceType + sourceUuid (+ sourceLineUuid)`. Both `inventory.StockReservations` and `material.stock_reservations` tables exist in the DB — check which one the Inventory implementation writes before touching either. | Never write the counter directly; only through the service. |
| 15-status delivery state machine ending `…GOODS_ISSUED → IN_TRANSIT → AT_HUB → OUT_FOR_DELIVERY → DELIVERY_ATTEMPTED → DELIVERED → COMPLETED → CANCELLED` | `DeliveryStatus` (`Logistics/Domain/LogisticsEnums.cs`) is 15 values but different ones: DRAFT, RELEASED, PICKING, PICKED, PACKED, STAGED, PENDING_APPROVAL (conditional, shipping-rule driven), GOODS_ISSUED, IN_TRANSIT, DELIVERED, CLOSED, + off-path ON_HOLD, PARTIALLY_DELIVERED, SHORT_CLOSED, CANCELLED. Hub/out-for-delivery/attempt granularity lives on the **consignment/tracking** side, not the delivery. | Self-pickup path should be DRAFT→RELEASED→PICKING→PICKED→PACKED→(STAGED)→GOODS_ISSUED→DELIVERED→CLOSED. There is no COMPLETED. |
| `from_source_type` allowed values PO, SRO, MIV, Transfer (+ add SALE_ORDER) | `DeliverySourceType`: PO, SRO, MIV, TRANSFER, **MANUAL** — and `DeliverySourceTypeInfo` decides direction and *whether the delivery is the document that posts the stock movement* (MIV: "No — the source document does"). Pick lists are refused for INBOUND deliveries. | Adding SALE_ORDER means adding it to the enum **and** to `DeliverySourceTypeInfo` (direction OUTBOUND, `RequiresStockHold` true, posts movement = yes). |
| Delivery numbers `DO-…`, `_numberService.NextNumber('DO', orgId)` | Existing numbering: deliveries `DLV-YYYY-NNNNN`, consignments `SHP-`, pick lists `PCK-`, packages `HU-`, via `logistics.document_number_sequences` (RowVersion concurrency) | Reuse the sequence table; pick prefixes `SO-`, `SINV-`, `CPAY-` per spec. |
| No `MovementType` enum | There is **no** `MovementType` enum anywhere in `src/`. Goods issue posting is driven per source type in the logistics goods-issue path. | "Add SALES_SHIP/SALES_HANDOVER to MovementType" translates to: extend the goods-issue source-type switch, and record the movement kind wherever the existing GI records it — find that first. |
| Permissions `PACKING`, `GOODS_ISSUE`, `DELIVERY_RELEASE` exist | `PermissionCodes.cs` has PICKING, DISPATCH, DELIVERY_VIEW/CREATE/EDIT/TRACK, SHIPMENT_BOOK, POD_CAPTURE. A code comment says DELIVERY_RELEASE, DELIVERY_PACK, DELIVERY_GOODS_ISSUE, SHIPMENT_CANCEL "are added with the features they gate". | Use the codes that exist; add new ones only with the feature, following that comment. |
| `suppliers.SupplierRateCards` (Addendum 28) with `rate`, `partner_id` | No such table. Supplier price per variant is **`inventory.VariantSuppliers`** (`VariantSupplier`: `SupplierId` Guid, `VendorUnitCost`, `LeadTimeDays`, `EffectiveFrom/To`, `IsActive`, `CurrencyId`, `MinOrderValue`…). Carrier rate cards (`logistics.RateCard`) are a different thing — freight lanes, not purchase prices. | Purchase-price resolution and BEST_MATCH candidate discovery read `VariantSuppliers`. `lead_time_days` per supplier already exists there; the spec's `ProductVariants.lead_time_days` is arguably redundant. |
| `suppliers.SupplierScorecards` with a `grade` column | Supplier scorecard exists in SMS.Modules.Suppliers (`ScorecardDashboardService`, `ScorecardRecalculationJob`, `ScorecardDashboardController`); a separate **carrier** scorecard is in Logistics (`CarrierScorecardService`). Read the supplier one for its actual grade/score shape before writing the composite-score query. | |
| `workflow.DocumentTimelines`, trace_id, Hangfire append | **Confirmed.** `SMS.WorkflowEngine`: `DocumentTimeline` entity, `TimelineService` (`ITimelineService`), `TimelineAppendJob` (Hangfire), `TimelineController`, `TimelineModels.cs`. `TraceId` is already on Demand, Warehouse, Material, Finance and Logistics entities. | SO→PO linkage really is "same pattern"; copy the existing enqueue call shape. |
| `logistics.addresses` structured address table | **Confirmed** (`AddressMap.cs` → `addresses`). | Use it for SO shipping address (`shipping_address_id`), as the spec says. |
| Consignment / booking / tracking / labels / POD infrastructure | **Confirmed** and exercised end-to-end on 2026-09-18 (see the Logistics manual-test guide artifact). Known gaps: no UI to create a consignment, attach a delivery to one, or issue a public tracking link (API only). Booking refuses a consignment with no attached delivery, or a delivery with no ship-to **and** ship-from address. | Sales shipments must create the consignment, attach the DO, and carry both addresses before booking. |
| Sales-side entities (BusinessPartner, SaleOrder, SalesInvoice, CustomerLedger…) | **None exist.** Only the `SalesOrder` constant in `ReservationSourceType`. `suppliers.Suppliers` is the supplier entity; `logistics.carriers` is a separate Carrier entity that already links to a supplier via `Carrier.SupplierId` (Guid) — an existing partial answer to "carrier partner". | Section 1's rename is a genuine cross-module migration; the Carrier↔Supplier link must be reconciled with `is_carrier`, not duplicated. |
| Logistics "1531 tests" | Backend tests live in `tests/`; the logistics rebuild is tracked task-by-task in `docs/logistics-rebuild/TASKS.md` (T-01…T-19b + fixes). Frontend specs are Karma. | Follow the same task log discipline for this addendum. |

Other standing facts worth knowing before starting:
- Multi-tenancy is `OrganizationId` (Guid) on entities with `ITenantScopedEntity` + query filters — the spec's `org_id INT` is the same idea, different type/name.
- Backend: .NET 8 modular monolith, `src/SMS.API/SMS.API.csproj`; modules `SMS.Modules.{Auth,Lookups,Suppliers,Inventory,Demand,Warehouse,Logistics,Finance,Reports,Material}`, `SMS.WorkflowEngine`, `SMS.Shared`. Each module owns its DbContext + migrations.
- Dev DB is Azure SQL `supplymanagment.database.windows.net` / `SMSGlobal` (shared — direct writes need the user's OK).

---

## Part B — The specification

### Section 0 — Implementation instructions (verbatim intent)
Before writing code: inspect the codebase; follow existing repository/service/controller patterns; use each
module's EF Core DbContext + `dotnet ef migrations add`; respect tenancy (query filter on every new
table; Hangfire jobs receive the org id as an explicit parameter — no HttpContext in background); keep
schema isolation (auth, lookups, suppliers, inventory, demand, procurement*, warehouse, logistics,
finance, reports, workflow, hangfire, material, tenant — *see Part A: PO lives in `demand`); AutoMapper
for DTOs, FluentValidation, MediatR domain events, QuestPDF for PDFs, Hangfire for background work.
**Reuse** `logistics.delivery_orders` with `from_source_type = SALE_ORDER` (no separate Fulfillments
table); **reuse** `IStockReservationService` with `SALES_ORDER` (no separate reservation table); reuse
pick lists, packages, consignments, goods issue, `logistics.addresses`, `document_number_sequences`,
existing permissions, supplier scorecard, variant-supplier prices, and the trace_id/DocumentTimeline
pattern (same propagation, same JSON events array, same Hangfire non-blocking write).

### Section 1 — Business Partner entity (Suppliers → BusinessPartners)

**1.1 Migration: rename, not copy.** Rename `suppliers.Suppliers` → `suppliers.BusinessPartners`; add
partner_type + customer columns with defaults; set all existing rows `partner_type='VENDOR'`; rename
FK columns `supplier_id → partner_id` across referencing tables (sp_rename); rename entity
`Supplier → BusinessPartner`; update repos/services/controllers; add backward-compatible aliases.
Zero data loss.

**1.2 Partner types** (stored as `partner_type NVARCHAR(20)` **derived from four flags**):

| partner_type | vendor | customer | carrier | service provider |
|---|---|---|---|---|
| VENDOR | Y | – | – | – |
| CUSTOMER | – | Y | – | – |
| CARRIER | – | – | Y | – |
| SERVICE_PROVIDER | – | – | – | Y |
| BOTH | Y | Y | – | – |
| VENDOR_CARRIER | Y | – | Y | – |
| VENDOR_SERVICE | Y | – | – | Y |
| FULL_PARTNER | Y | Y | Y | Y |

Flags: `is_vendor`, `is_customer`, `is_carrier`, `is_service_provider` (BIT NOT NULL, default 0).
Type is auto-computed from the flags on save; flags allow `WHERE is_carrier = 1`.

**1.3 `suppliers.BusinessPartners` columns:** partner_id PK (was supplier_id) · org_id · partner_code
(auto, was supplier_code) · partner_type · the four flags · company_name (req) · trade_name ·
contact_person · email · phone · address_line1/2 · city · state_province · country · postal_code · tax_id ·
credit_limit DECIMAL(18,2) · payment_terms_days INT (default 30) · currency_code CHAR(3) · bank_name ·
bank_account · iban · swift_code · vehicle_types NVARCHAR(500) (carrier) · service_categories
NVARCHAR(500) (service provider) · is_active · rating NVARCHAR(2) (A/B/C/D/F) · notes · created_by/at ·
updated_by/at · is_deleted.

**1.4 Carrier partners** (`is_carrier=1`): appear in the shipment carrier dropdown; can be assigned to
deliveries; `vehicle_types` list; rate cards via existing rate-card structure; performance via the
existing scorecard engine with delivery-focused dimensions. *(Part A: reconcile with `logistics.carriers` +
`Carrier.SupplierId`.)*

**1.5 Service providers** (`is_service_provider=1`): maintenance contracts, consulting POs, outsourced
operations; `service_categories`.

**1.6 API:** `GET /api/partners` (filters: type, is_vendor, is_customer, is_carrier, is_service_provider,
active, search) · `GET/PUT/DELETE /api/partners/{id}` · `POST /api/partners` ·
`GET /api/partners/{id}/ledger` (payable or receivable by type) · aliases `GET /api/suppliers` →
`?is_vendor=true`, `/api/customers` → `is_customer`, `/api/carriers` → `is_carrier`,
`/api/service-providers` → `is_service_provider`.

**1.7 Backward compatibility:** PO `supplier_id` FKs renamed to `partner_id` but point at the same rows;
`SupplierLedger` → `PartnerLedger` (entries intact); `/api/suppliers` keeps working; supplier scorecard
unchanged (filters `is_vendor=1`); nothing deleted/moved/duplicated.

### Section 2 — Product pricing & default supplier

**2.1** New on `inventory.ProductVariants`: `default_supplier_id` (FK partner, is_vendor) and
`lead_time_days INT`. Used by back-to-back auto-PO in DEFAULT_SUPPLIER mode; if unset, fall back to
the admin-configured mode. *(Part A: `VariantSuppliers` already carries per-supplier lead time.)*

**2.2 `inventory.PricingRules`:** pricing_rule_id · org_id · variant_id · partner_id (NULL = all customers)
· price_type SELLING|COST|PROMOTIONAL|CONTRACT · min_qty (default 1) · max_qty (NULL = unlimited) ·
unit_price DECIMAL(18,4) · currency_code · effective_from · effective_to (NULL = open) · is_active ·
created_by/at.

**2.3 Selling price resolution** (first match wins): 1 CONTRACT for partner+variant+date+qty →
2 PROMOTIONAL for variant+date+qty → 3 partner-specific SELLING → 4 default SELLING (partner NULL) →
5 `Variant.selling_price`.

**2.4 Purchase price resolution** (for back-to-back PO): 1 latest active supplier rate for
supplier+variant (effective today, newest `effective_from`) → 2 last PO line price for supplier+variant →
3 `Variant.purchase_price`. *(Part A: read `inventory.VariantSuppliers`, not `SupplierRateCards`.)*

### Section 3 — Sale order administration & configuration

Owned by the **Supply Department Admin** (Deputy Director / Director of Supply), *not* IT/Super Admin.

**3.1 Role** `SUPPLY_DEPT_ADMIN` ("Supply Department Administrator"), org-scoped, under the Supply Chain
Management permission group. IT Admin / Super Admin may VIEW config, may CHANGE it only if they also
hold this role.

**3.2 `demand.SaleOrderConfig`** (one row per org): auto_po_enabled (default 1) ·
supplier_selection_mode DEFAULT_SUPPLIER|BEST_MATCH|MANUAL · auto_po_approval_mode
REQUIRE_WORKFLOW|AUTO_SEND|DRAFT_ONLY · drop_ship_enabled (default 0) · self_pickup_enabled
(default 1) · default_fulfillment_mode IN_STOCK|BACK_TO_BACK|DROP_SHIP · reservation_ttl_hours
(default 72) · partial_fulfillment_allowed (default 1) · email_intimation_enabled (default 1) ·
intimation_department_id · intimation_cc_emails NVARCHAR(500) · shipment_required_default (default 1)
· updated_by (must be SUPPLY_DEPT_ADMIN) · updated_at. Every change audit-trailed.

**3.3 Supplier selection modes** (when auto_po_enabled and stock is short):
- **DEFAULT_SUPPLIER** — variant's `default_supplier_id`; none set → MANUAL for that line.
- **BEST_MATCH (recommended)** — candidates = active vendors supplying the variant; scorecard grade
  A=5 B=4 C=3 D=2 else 1; latest rate for the qty; `composite = score*0.6 + (1/price_rank)*0.4`
  (price_rank = RANK by rate ascending); highest wins; tie → shortest lead time.
- **MANUAL** — no auto-PO; create a notification/task for the supply team.

**3.4 Auto-PO approval modes:** REQUIRE_WORKFLOW → PO `PENDING_APPROVAL`, normal workflow, team may edit
before approval · AUTO_SEND → PO `APPROVED/OPEN`, sent immediately, no human review · DRAFT_ONLY → PO
`DRAFT`, no workflow, team reviews/edits then submits or sends. In DRAFT_ONLY and REQUIRE_WORKFLOW the
PO is **not** sent until manual action; the team may change qty (e.g. order 100 when SO needs 40),
supplier, price, add items (combine several SO deficits), or split across suppliers.

**3.5 API:** `GET /api/sale-order-config` (SUPPLY_DEPT_ADMIN | IT_ADMIN read-only) · `PUT` (SUPPLY_DEPT_ADMIN
only) · `GET /api/sale-order-config/audit`.

### Section 4 — Sale orders (3-scenario availability check)

**4.1 `demand.SaleOrders`:** sale_order_id · org_id · so_number (`SO-YYYYMMDD-NNNN`) · partner_id
(is_customer) · order_date · expected_delivery_date · currency_code · subtotal · tax_amount ·
discount_amount · grand_total (= subtotal + tax − discount) · status
DRAFT|CONFIRMED|PARTIALLY_FULFILLED|FULFILLED|INVOICED|CLOSED|CANCELLED · requires_shipment BIT
(from config default, overridable) · delivery_mode SHIP|SELF_PICKUP · shipping_address_id (FK
`logistics.addresses`, NULL if self-pickup) · intimation_department_id · notes · trace_id (GUID, generated
at creation) · created_by/at · updated_by/at · is_deleted.

**4.2 `demand.SaleOrderLines`:** so_line_id · sale_order_id · variant_id · quantity · unit_price (resolved
selling price) · discount_percent · tax_percent · line_total = qty·price·(1−disc%)·(1+tax%) ·
fulfilled_qty · invoiced_qty · fulfillment_mode IN_STOCK|BACK_TO_BACK|DROP_SHIP|SPLIT ·
available_qty_at_confirm · deficit_qty · linked_po_id · selected_supplier_id · margin · margin_percent ·
status OPEN|RESERVED|PARTIALLY_FULFILLED|FULFILLED|INVOICED|CANCELLED · notes.

**4.3 Availability check on CONFIRM** (mode may be set by the user or auto-determined):
- **IN_STOCK** — available ≥ ordered → reserve now (reservation linked to so_line), line RESERVED.
  Email: "Product fully available in stock. Reservation created for [qty] units."
- **SPLIT** — reserve what is available; back-to-back PO for the deficit (per config);
  `available_qty_at_confirm`, `deficit_qty = ordered − available`. Email: "[X] reserved… [Y] require
  procurement. PO-xxxx created in DRAFT." Two deliveries will follow (stock now, GRN later).
  *(Part A: all-or-nothing service → preview with `GetAvailableAsync`, then reserve that quantity.)*
- **BACK_TO_BACK** — nothing available → PO for the full qty (status per config); GRN auto-reserves
  for the line. Email: "No stock available. PO-xxxx created. Awaiting procurement."
- **DROP_SHIP** — only if `drop_ship_enabled`; PO carries the customer's shipping address; no warehouse
  stock impact. Email: "Drop Ship order. Supplier will ship directly to customer at [address]."

**4.4 Reservation** — via the existing `IStockReservationService`, source type `SALES_ORDER`, source =
the SO line, expiry = now + `reservation_ttl_hours`. Multi-row FEFO allocation across batches; the
counter never goes negative.

**4.5 API:** `GET /api/sale-orders` (status, customer, date range) · `GET /api/sale-orders/{id}` ·
`POST /api/sale-orders` (DRAFT) · `PUT /api/sale-orders/{id}` (DRAFT only) ·
`POST /api/sale-orders/{id}/confirm` (availability check + email) · `POST …/cancel` (releases
reservations) · `GET …/timeline` · `GET …/availability` (preview without confirming).

### Section 5 — Email intimation

**5.1 Events** (all via Hangfire `BackgroundJob.Enqueue`, non-blocking):

| Event | Recipients | Content |
|---|---|---|
| SO Confirmed | intimation dept + CC | SO details, per-line availability, mode, auto-POs |
| Back-to-back PO created | intimation dept | PO, linked SO, supplier, price, status |
| Drop ship detected | intimation dept | supplier, customer delivery address |
| Stock reserved | SO creator | reservation + expiry |
| PO approved (manual) | SO creator + dept | sent to vendor, expected delivery |
| GRN received (SO-linked) | SO creator | received, auto-reserved, ready to fulfil |
| Reservation expiring | SO creator | expires in 24 h |

**5.2 SO-confirmation template:** subject `[SMS] Sale Order {SO-Number} Confirmed - Availability Summary`;
header (SO, customer, order/expected dates); line table Product | Ordered | Available | Deficit | Mode |
Action taken (e.g. "60 reserved from stock. PO-2026-0045 created (DRAFT) for 40 units from TechSupply
Co."); footer action items ("PO-2026-0045 requires approval before sending"); self-pickup note
("Customer will collect. No shipment required. Quantities held at [warehouse].").

**5.3 `demand.SaleOrderIntimations`:** intimation_id · org_id · sale_order_id · event_type
SO_CONFIRMED|PO_CREATED|DROP_SHIP|RESERVED|PO_APPROVED|GRN_RECEIVED|EXPIRING · recipients · subject ·
body_html · sent_at · status SENT|FAILED|QUEUED · error_message · hangfire_job_id. Failed emails retried
3× by Hangfire.

### Section 6 — Back-to-back PO

**6.1 Flow:** deficit → supplier selection (per mode) → price resolution (rate → last PO → variant
purchase price) → create PO with status per approval mode → email intimation.

**6.2 Edits before approval** (DRAFT_ONLY / REQUIRE_WORKFLOW): qty, supplier, price, add items, split.
Original auto-generated values are kept for audit.

**6.3 New PO columns:** source MANUAL|BACK_TO_BACK|DROP_SHIP|MIR (default MANUAL) · linked_so_line_id ·
linked_so_id · customer_shipping_address_id (FK `logistics.addresses`, drop ship) · auto_generated_qty ·
auto_generated_price · auto_selected_supplier_id.

**6.3.1 trace_id propagation:** `newPO.trace_id = saleOrder.trace_id`; set source, linked_so_id,
linked_so_line_id; enqueue `PO_CREATED_FROM_SO` timeline event (source SALE_ORDER → target
PURCHASE_ORDER, details "{qty} units of {sku} → {supplier}"). One trace_id then spans
Quotation→PO→GRN→Invoice and SO→PO→GRN→Reservation→Fulfilment→Sales Invoice.

**6.4 GRN auto-reservation:** on GRN against a PO with `linked_so_line_id`: reserve the received qty for
the SO line, line OPEN→RESERVED, email the SO creator; partial GRN → partial reservation, line stays OPEN
until fully received.

### Section 7 — Fulfilment via existing delivery orders

**7.1 Additions to `logistics.delivery_orders`:** `from_source_type` gains SALE_ORDER · sale_order_id ·
delivery_mode SHIP|SELF_PICKUP · pickup_person_name · pickup_person_id_type (CNIC/License/Passport) ·
pickup_person_id_number · pickup_authorization. On `delivery_order_lines`: `so_line_id`.

**7.2 State machine:** reuse as-is. Shipment path DRAFT→RELEASED→PICKING→PICKED→PACKED→GOODS_ISSUED→
IN_TRANSIT→DELIVERED. Self-pickup path skips IN_TRANSIT: …→GOODS_ISSUED→DELIVERED (GI posts
SALES_HANDOVER; DELIVERED when the customer collects). *(Part A for the real status list.)*

**7.3 Create from SO** — same from-source pattern as PO/SRO/MIV/Transfer: source SALE_ORDER, sale_order_id,
warehouse, delivery_mode from the SO, ship-to = SO `shipping_address_id`, status DRAFT, **trace_id
propagated from the SO**.

**7.4 Reused as-is:** pick lists (FEFO/FIFO, zone+bin walk order), pack station (HU barcodes, nesting,
void-not-delete), goods issue (source type decides movement), carrier booking via consignments (Hangfire),
webhook+poll tracking, labels, proof of delivery (bytes stored, not URLs).

**7.5 Goods issue for SALE_ORDER:** movement = SELF_PICKUP ? SALES_HANDOVER : SALES_SHIP; consume the
reservation via the service; `soLine.fulfilled_qty += issuedQty`. Idempotent (existing pattern).

**7.6 Partial fulfilment:** one SO → many DOs (e.g. 60 from stock shipped, 30 from GRN shipped, 10 from
GRN self-pickup). SO CONFIRMED→PARTIALLY_FULFILLED when the first DO reaches DELIVERED/CLOSED;
→FULFILLED when every line's fulfilled_qty = ordered. Each DO independent (mode, carrier, date).

**7.7** SO shipping address is `shipping_address_id` → `logistics.addresses` (no flat string).

**7.8 API:** `POST /api/sale-orders/{id}/create-delivery` (lines + qtys → existing from-source create) ·
`GET /api/sale-orders/{id}/deliveries` · `POST /api/delivery-orders/{id}/pickup` (record pickup person,
ID, authorization → DELIVERED). Everything else uses the existing delivery endpoints.

**7.9 Permissions:** DELIVERY_VIEW, DELIVERY_EDIT, DELIVERY_RELEASE*, PICKING, PACKING*, DISPATCH /
GOODS_ISSUE*, SHIPMENT_BOOK, POD_CAPTURE (*not yet defined — see Part A).

### Section 8 — Self-pickup

**8.1 Config:** `self_pickup_enabled=1` allows SELF_PICKUP per SO; `=0` forces SHIP;
`shipment_required_default=0` makes new SOs default to SELF_PICKUP (overridable).

**8.2 Process:** SO created SELF_PICKUP (no shipping address) → confirm (reserve) → DO DRAFT
(SALE_ORDER, SELF_PICKUP) → RELEASED → PICKING (pick list, FEFO) → PICKED (short-pick reasons) → PACKED
(optional) → GOODS_ISSUED (SALES_HANDOVER, reservation consumed, on-hand down) → email "ready for
collection at [warehouse]" → DELIVERED on collection (record pickup person/ID/authorization; SO line
fulfilled_qty updated). No IN_TRANSIT, no carrier booking, **no consignment**.

**8.3 Documentation:** GI movement is the audit trail; a gate pass / packing list is generated with QuestPDF
(reuse the packing-list PDF) containing DO number, items, quantities, pickup person, authorization,
timestamp; stored as an attachment on the DO; used by warehouse security.

### Section 9 — Sales invoicing & customer payments

**9.1 `finance.SalesInvoices`:** invoice_id · org_id · invoice_number (`SINV-YYYYMMDD-NNNN`) · sale_order_id
· partner_id · invoice_date · due_date · subtotal · tax_amount · discount_amount · grand_total ·
amount_paid · balance_due · status DRAFT|ISSUED|PARTIALLY_PAID|PAID|OVERDUE|CANCELLED|CREDIT_NOTE ·
currency_code · trace_id (copied from the SO) · notes · created_by/at · is_deleted.

**9.2 `finance.SalesInvoiceLines`:** invoice_line_id · invoice_id · so_line_id · variant_id · description ·
quantity · unit_price · discount_percent · tax_percent · line_total.

**9.3 `finance.CustomerPayments`:** payment_id · org_id · partner_id · payment_number
(`CPAY-YYYYMMDD-NNNN`) · payment_date · amount · payment_method CASH|CHEQUE|BANK_TRANSFER|CARD|ONLINE ·
cheque_number · bank_reference · currency_code · notes · status RECEIVED|BOUNCED|REVERSED · created_by/at.

**9.4 `finance.PaymentAllocations`:** allocation_id · payment_id · invoice_id · allocated_amount ·
allocated_at · allocated_by. One payment across many invoices; auto-allocate FIFO (oldest unpaid first)
with manual override.

**9.5 Rules:** invoice only against delivered / picked-up lines; invoiced qty ≤ delivered qty; multiple
invoices per SO; ISSUED → CustomerLedger DEBIT in the same transaction; payment → CustomerLedger CREDIT
in the same transaction; PARTIALLY_PAID when amount_paid > 0, PAID when balance_due = 0; OVERDUE set by a
Hangfire job (due_date < today and balance_due > 0).

### Section 10 — Customer ledger (receivables)

Mirror of the supplier ledger: append-only, running balance, written in the same transaction as the
business action. **`finance.CustomerLedger`:** entry_id · org_id · partner_id · entry_date · entry_type
INVOICE|PAYMENT|CREDIT_NOTE|DEBIT_NOTE|ADVANCE|REFUND|OPENING_BAL · reference_type · reference_id ·
reference_number · debit_amount (increases receivable) · credit_amount (decreases) · running_balance ·
currency_code · narration · created_by/at.

Direction: invoice issued → debit grand_total · payment → credit amount · credit note → credit · debit
note → debit · customer advance → credit · refund → debit · opening balance → debit.
`running_balance = previous + debit − credit`.

### Section 11 — Product ledger

Per-variant financial history, both cost and revenue sides (per-product P&L).

**11.1 `finance.ProductLedger`:** entry_id · org_id · variant_id · product_id (denormalised) · entry_date ·
entry_type PURCHASE|SALE|RETURN_IN|RETURN_OUT|ADJUSTMENT|WRITE_OFF · reference_type · reference_id ·
reference_number · partner_id · quantity · unit_cost · total_cost · direction IN|OUT · running_qty ·
running_value (weighted-average) · narration · created_by/at.

**11.2 Written on:** GRN approved (PURCHASE, IN, cost = PO line price) · sales invoice issued (SALE, OUT,
cost = WAC, revenue = selling price) · customer return (RETURN_IN) · supplier return (RETURN_OUT) · stock
adjustment (IN/OUT) · write-off (OUT).

**11.3 WAC:** on IN: running_qty += qty; running_value += qty·unit_cost; WAC = value/qty. On OUT:
COGS/unit = current WAC; running_qty −= qty; running_value −= qty·COGS/unit. Same transaction as the
business action.

**11.4 API:** `GET /api/product-ledger/{variantId}` (paginated) · `…/summary` (purchased, sold, stock
value, WAC) · `GET /api/reports/product-ledger` · `GET /api/reports/product-profitability`.

### Section 12 — Inventory impact (sales side)

**12.1 Movement kinds:** SALES_SHIP (OUT, GI with SHIP) · SALES_HANDOVER (OUT, GI with SELF_PICKUP) ·
SALES_RETURN (IN, customer return receipt) · DROP_SHIP_VIRTUAL (N/A, virtual receipt+dispatch, no
warehouse stock). SALES_RESERVE / SALES_UNRESERVE are **not** movements — reservation is the service's
Reserve/Release/Consume on the reserved counter. *(Part A: no MovementType enum exists.)*

**12.2** Reserved counter is shared across MIR + DELIVERY + SALES_ORDER; FEFO across batches; never
negative.

### Section 13 — PO–SO linkage via trace_id / DocumentTimeline

**13.1–13.2** Existing pattern: originating doc (PR, Quotation, SO) mints a GUID trace_id; downstream
docs copy it; `workflow.DocumentTimelines` (timeline_id · org_id · trace_id · events JSON array ·
created_at · updated_at) holds one array per trace_id; writes are non-blocking via Hangfire.

**13.3 New event types:** SO_CREATED · SO_CONFIRMED · PO_CREATED_FROM_SO (SALE_ORDER→PURCHASE_ORDER) ·
GRN_FOR_SO_PO (PURCHASE_ORDER→GRN) · SO_STOCK_RESERVED (SALE_ORDER→STOCK_RESERVATION) · SO_FULFILLED
(→FULFILLMENT) · SO_INVOICED (→SALES_INVOICE). Event shape: `{event_type, source_type, source_id,
source_number, target_type?, target_id?, target_number?, timestamp, actor, details}`.

**13.4 Worked example** (100 ordered, 60 in stock, SPLIT): SO_CREATED → SO_CONFIRMED ("60 reserved, 40
deficit → PO created") + SO_STOCK_RESERVED (60, WH-01) + PO_CREATED_FROM_SO (40 → TechSupply, DRAFT) →
PO_APPROVED → GRN_FOR_SO_PO (40 received, auto-reserved) + SO_STOCK_RESERVED (40 from GRN) →
SO_FULFILLED… → SO_INVOICED — all under one trace_id.

**13.5 API:** `GET /api/sale-orders/{id}/timeline` · `GET /api/purchase-orders/{id}/timeline` (shared
trace_id, so SO events appear too) · `GET /api/timeline/{traceId}`.

**13.6 Rules:** trace_id minted at SO creation (`Guid.NewGuid()`); copied to auto-POs and to sales
invoices; timeline writes always via Hangfire, never in the main transaction; events appended
(JSON_MODIFY or read-modify-write), never overwritten; a PO that already has a Quotation trace_id and is
later linked to an SO joins by cross-reference; timeline UI is chronological with document links.

**13.7 Tenancy:** org filter on DocumentTimelines; unique index on (org_id, trace_id); org id from the
JWT only; Hangfire jobs get org id as a parameter.

### Section 14 — Procurement impact

PO additions as §6.3. **Margin:** `margin = SO_line.unit_price − PO_line.unit_price`;
`margin_percent = margin / SO_line.unit_price × 100`, stored on the SO line (informational; Margin
Analysis report). PO timeline shows originating SO, selection method used, GRN events, full chain
SO→PO→GRN→Reservation→Fulfilment→Invoice.

### Section 15 — Sales reports (`GET /api/reports/sales/{name}`, PDF via QuestPDF + Excel)

R1 Sales Order Register (date, status, customer, delivery_mode) · R2 Customer Ledger (customer, date) ·
R3 Aging Receivables (0–30/31–60/61–90/90+; as-of, customer) · R4 Sales by Product · R5 Sales by
Customer (revenue, order count, AOV) · R6 Fulfilment Status (warehouse, status, mode) · R7 Margin
Analysis · R8 Sales vs Purchase (revenue vs COGS by period) · R9 Product Ledger Report · R10 Product
Profitability (revenue − COGS, ranked).

### Section 16 — Implementation order (30 dev days)

| # | Component | Depends on | Days | Schema |
|---|---|---|---|---|
| 1 | BusinessPartner migration (rename + types) | – | 2 | suppliers |
| 2 | Partner API + aliases + carrier/service types | 1 | 2 | suppliers |
| 3 | Default supplier on variants + PricingRules | 1 | 2 | inventory |
| 4 | SUPPLY_DEPT_ADMIN role + SaleOrderConfig | 1 | 1 | auth + demand |
| 5 | SaleOrder + lines + availability check | 1–4 | 3 | demand |
| 6 | IStockReservationService for SALES_ORDER | 5 | 1 | inventory (shared) — *largely exists* |
| 7 | Email intimation service + templates | 5 | 2 | demand |
| 8 | Back-to-back PO (auto-create, selection, draft/approval) + trace_id | 5–6 | 3 | demand(PO) + workflow |
| 9 | PO–SO timeline linkage + timeline API | 8 | 2 | workflow |
| 10 | Drop-ship PO flow | 9 | 1 | demand(PO) |
| 11 | delivery_orders for SALE_ORDER + self-pickup + goods issue | 6 | 2 | logistics |
| 12 | Sales invoice + CustomerLedger | 11 | 2 | finance |
| 13 | Customer payment + allocation | 12 | 2 | finance |
| 14 | Product ledger | 11–12 | 2 | finance |
| 15 | Sales reports (10) | 5–14 | 3 | reports |

**16.1 Migration names, in order:** RenameSupplierToBusinessPartner · AddPartnerTypeFlagsAndCarrierFields
· AddDefaultSupplierToVariants · CreatePricingRules · CreateSupplyDeptAdminRole · CreateSaleOrderConfig ·
CreateSaleOrderTables · AddSalesOrderToReservationSourceType · CreateSaleOrderIntimations ·
AddPOSourceAndLinkedFields · AddSOTimelineEvents · ExtendDeliveryOrdersForSaleOrderSource ·
CreateSalesInvoiceTables · CreateCustomerLedger · CreateCustomerPaymentTables · CreateProductLedger.

### Section 17 — Acceptance criteria

| TC | Scenario | Expected |
|---|---|---|
| 01 | `GET /api/suppliers` returns only is_vendor=1; create PO with an existing vendor | procurement unchanged |
| 02 | Create partner is_carrier=1; appears in carrier dropdown; assign to a delivery | usable in fulfilment |
| 03 | Create service provider; service_categories saved; service PO | works in procurement |
| 04 | 50 available, SO for 30, confirm | reserved; email "fully available" |
| 05 | 60 available, SO for 100, confirm | 60 reserved, PO for 40 in DRAFT, email shows both |
| 06 | BEST_MATCH with several vendors (scorecards + rates) | composite score picks the optimal vendor |
| 07 | DRAFT_ONLY; auto-PO in DRAFT; edit qty/price; manual submit | not sent until manual action |
| 08 | SELF_PICKUP SO → confirm → DO → pick, pack → record pickup person → DELIVERED; gate pass generated | no shipment; stock out via SALES_HANDOVER |
| 09 | SO 100: 40 shipped, 30 shipped, 30 self-pickup | SO = FULFILLED, mixed modes |
| 10 | GRN 100 @ $10; sell 30 @ $15 | PURCHASE + SALE entries; WAC $10; COGS $300; revenue $450 |
| 11 | SPLIT line (50/50) + drop-ship line | one email with reservation, PO and drop-ship notices |
| 12 | Customer in Org A invisible to Org B; cannot be referenced in an SO | complete isolation |
| 13 | Confirm SO with deficit; auto-PO | `PO.trace_id == SO.trace_id`; PO_CREATED_FROM_SO in timeline |
| 14 | SPLIT → PO approved → GRN → fulfil 100 → deliver → invoice | ≥7 events, chronological, all docs linked |
| 15 | Confirm SO triggering auto-PO | response < 2 s; timeline event within 5 s |

**17.1 NFRs:** org filter on every new table; list APIs paginated (max 100/page); explicit transactions;
customer + product ledger writes in the same transaction as the action; timeline and email via Hangfire;
config changes need SUPPLY_DEPT_ADMIN.

**17.2 Indexes:** BusinessPartners (org, partner_type, is_active) and (org, four flags) · SaleOrders (org,
status, order_date), (org, partner) · SaleOrderLines (variant, status) · StockReservations (org, variant,
status) · Fulfilments/DOs (sale_order_id, status) · SalesInvoices (org, partner, status), (org, due_date,
balance_due) · CustomerLedger (org, partner, entry_date) · ProductLedger (org, variant, entry_date), (org,
product, entry_date) · PricingRules (org, variant, is_active, effective_from) · SaleOrderIntimations
(sale_order_id, event_type) · DocumentTimelines (org, trace_id) UNIQUE · PurchaseOrders (org, trace_id),
(linked_so_id, linked_so_line_id).

**17.3 Rollback:** each migration independently revertible; partner rename reversible; new tables droppable
without touching procurement/logistics data; PO and delivery_orders additions are nullable; removing
SALES_ORDER from the source types requires clearing sales reservations first; SO timeline events are
additive JSON.
