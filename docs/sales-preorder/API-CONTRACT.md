# Addendum 32 — API contract (binding for INQ, QUO, FE-INQ, FE-QUO, FE-SO, QA)

Owner: FND. Change requests to FND by message; FND edits this file. Last update: 2026-10-03 (v1).

Code that already encodes this contract (read it, don't re-derive):

| What | Where |
|---|---|
| Entities (not in the EF model until FND's migration lands — "SCHEMA READY") | `src/SMS.Modules.Demand/Domain/SalesPreOrderEntities.cs` |
| Status/type codes (`EnumCode<T>`) | `src/SMS.Modules.Demand/Domain/SalesPreOrderEnums.cs` |
| Request/response DTOs | `src/SMS.Modules.Demand/Models/SaleInquiryModels.cs`, `SaleQuotationModels.cs`, `RejectionReasonModels.cs`, `SaleOrderReservationModels.cs`, additions in `SaleOrderModels.cs` |
| Service interfaces | `Services/ISaleInquiryService.cs` (INQ), `ISaleQuotationService.cs` (QUO), `IRejectionReasonService.cs` (FND), `ISaleOrderReservationService.cs` (FND), additions to `ISaleOrderService.cs` (FND) |
| Customer check (BR-C1-01/BR-C2-01) | `SMS.Shared/Common/IPartnerRoleLookup.cs` → `GetAsync(partnerUuid)` returns `IsCustomer`/`IsActive`/`Name`, own org only; implemented in Suppliers (registered) |
| Permission codes | `SMS.Shared/Authorization/PermissionCodes.cs`; seeded in `SMS.Modules.Auth/Data/AuthDataSeeder.cs`; frontend `P.` in `pages/pages.routes.ts` |
| Frontend models + calls | `SupplyChainFrontend/src/app/services/sales-preorder.service.ts` (inquiries, quotations, rejection reasons, codes); `services/sale-order.service.ts` (SO source/customer PO/reserve/release) |

## 1. Conventions (same as `SaleOrdersController`)

- Every response is `ApiResponse<T>` `{ success, message, result }`; lists are `ApiResponse<PaginatedResponse<T>>`
  `{ data, totalRecords, page, pageSize, totalPages }` with `page` (1-based) and `pageSize` (default 20, clamped 1–100).
- Create returns `200 ApiResponse<Guid>` (the new uuid). Update/delete/transition return `200 ApiResponse` unless stated.
- Routes take `{uuid:guid}` / `{lineUuid:guid}`; the API never exposes int ids. Users are `int`.
- Status codes: **400** business rule (`BadRequestException`), **403** missing permission (`[RequirePermission]`),
  **404** not in the caller's organization (service returns false/null, or `NotFoundException`), **409** state conflict /
  duplicate / lost race (`ConflictException`, `DbUpdateConcurrencyException` — the global middleware maps both).
- Tenancy: every write and every by-uuid read filters explicitly on `ITenantContext.OrganizationId` (super admins bypass
  the EF filter) — another organization's record is a 404, super admin included.
- Controllers: `[ApiController]`, `[RequiresFeature("MODULE_DEMAND")]`, `[RequirePermission(...)]` on **every** action
  (`UngatedEndpointsRatchetTests`); no `[AllowAnonymous]`. User id = `User.GetUserId()`.
- Date-only fields (received/deadline/validity/delivery/PO dates) travel as `"yyyy-MM-dd"` (midnight, no offset); the
  server stores them in SQL `date` columns. Frontend: shared date-only helpers, never `toISOString()` on a picked date.
- Money: line amounts and header totals `decimal(18,2)`; quantities and unit prices `decimal(18,4)`; percentages
  `decimal(5,2)` (0–100, ≤ 2 decimals). Line total = `qty × price × (1 − disc%) × (1 + tax%)`, rounded 2 away from zero —
  the SaleOrderLine formula; header = sum of the same parts (`SaleOrderService.ApplyTotals`).
- Tax on lines: `taxCodeUuid` (Finance code via `ITaxCodeLookup`, must be active and usable on SALES) wins and its rate is
  snapshotted into `taxPercent` + `taxCode`; without one, `taxPercent` as entered. Same rules as `SaleOrderService.ResolveTaxAsync`.
- Numbering: `IDocumentNumberGenerator.NextAsync("INQ" | "SQ", date)` → `INQ-2026-00001`, `SQ-2026-00001` (the repo's
  5-digit format, the same deviation SO numbers make from the spec's `NNNN`); unique index `(OrganizationId, Number)`.
- Concurrency: `SaleInquiry.Status` and `SaleQuotation.Status` are EF concurrency tokens — a transition that lost a race
  fails its save with 409. Keep every status change a tracked load → mutate → `SaveChangesAsync`.

## 2. Permissions and role seeding

| Code | Gates | Seeded to |
|---|---|---|
| `SALE_INQUIRY_VIEW` | GET inquiries; inquiry list/detail routes | System Admin, Org Admin |
| `SALE_INQUIRY_CREATE` | POST inquiry | System Admin, Org Admin |
| `SALE_INQUIRY_EDIT` | PUT header, lines (add/edit/evaluate/delete), PATCH status | System Admin, Org Admin |
| `SALE_QUOTATION_VIEW` | GET quotations; quotation list/detail routes | System Admin, Org Admin |
| `SALE_QUOTATION_CREATE` | POST quotation, POST inquiry `create-quotation`, POST `copy` | System Admin, Org Admin |
| `SALE_QUOTATION_EDIT` | PUT header, lines, customer-response, accept, reject | System Admin, Org Admin |
| `SALE_QUOTATION_SEND` | POST `send` | System Admin, Org Admin |
| `SALE_ORDER_CREATE` (existing) | POST `convert-to-order` (spec: sales_order_create) | System Admin, Org Admin |
| `SALE_ORDER_RESERVE` | POST line `reserve`, POST `reserve-all` | System Admin, Org Admin, **Inventory Manager** |
| `SALE_ORDER_RELEASE_RESERVATION` | POST line `release` | System Admin, Org Admin, **Inventory Manager** |
| `SALE_REJECTION_REASON_MANAGE` | POST/PUT/PATCH/DELETE rejection reasons | System Admin, Org Admin |

- The spec's SALES_MANAGER / WAREHOUSE_MANAGER roles don't exist. Built-in roles: System Admin and Org Admin get every new
  code through `PermissionCodes.All`; **Inventory Manager** stands in for WAREHOUSE_MANAGER (it owns stock allocation) and
  also gets `SALE_ORDER_VIEW` so it can open an order to reach the buttons. Supply Dept Admin stays exactly config
  read/write (an A29 decision its tests pin). No other role gets a sales code (none has `SALE_ORDER_VIEW` today). Seeding is idempotent,
  roles matched by code (the existing seeder).
- **Server = frontend guard.** Each GET needs its `*_VIEW` code only; route guards for list/detail pages use the same
  `*_VIEW` code; "new" pages use `*_CREATE`. Buttons: `authService.hasPermission(<code in the table>)`. Reserve/Release
  buttons are **disabled with a tooltip, not hidden**, without the code (BR-C4-08):
  "You do not have permission to reserve inventory. Contact your administrator." / "…to release reserved inventory…".
- Rejection-reason **reads** (`GET api/rejection-reasons`) accept any of: `SALE_INQUIRY_VIEW`, `SALE_INQUIRY_EDIT`,
  `SALE_QUOTATION_VIEW`, `SALE_QUOTATION_EDIT`, `SALE_REJECTION_REASON_MANAGE` (the spec's "authenticated" — the ratchet
  test forbids an ungated action).

## 3. Attachments (no per-document tables — generic `api/attachments`)

| interfaceCode | documentId | View | Upload | Remove any | Remove own |
|---|---|---|---|---|---|
| `SALE_INQUIRY` | inquiry uuid | SALE_INQUIRY_VIEW/_CREATE/_EDIT | SALE_INQUIRY_CREATE/_EDIT | SALE_INQUIRY_EDIT | SALE_INQUIRY_CREATE |
| `SALE_QUOTATION` | quotation uuid | SALE_QUOTATION_VIEW/_CREATE/_EDIT/_SEND | SALE_QUOTATION_CREATE/_EDIT | SALE_QUOTATION_EDIT | SALE_QUOTATION_CREATE |
| `CUSTOMER_PO` | sale order uuid | SALE_ORDER_VIEW/_CREATE/_EDIT/_CONFIRM | SALE_ORDER_CREATE/_EDIT | SALE_ORDER_EDIT | SALE_ORDER_CREATE |
| `SALE_ORDER` (general SO files) | sale order uuid | same as CUSTOMER_PO | same | same | same |

Frontend: `<app-attachment-list interfaceCode="SALE_INQUIRY" [documentId]="uuid">` (codes exported as
`SALES_ATTACHMENT_CODES`). The spec's `POST /{id}/attachments` endpoints are **n/a**. The rules land in
`AttachmentAccessPolicy` with FND's "SCHEMA READY"; until then the panel answers 400.

## 4. Sale inquiries — `api/sale-inquiries` (INQ)

| Method & route | Permission | Body → result | Notes |
|---|---|---|---|
| GET `/` | SALE_INQUIRY_VIEW | query `SaleInquiryListFilter` (status, partnerId, receivedFrom, receivedTo, assignedToUserId, search, page, pageSize) → `PaginatedResponse<SaleInquiryListItemModel>` | newest received first |
| POST `/` | SALE_INQUIRY_CREATE | `CreateSaleInquiryRequest` → `Guid` | RECEIVED; partner must be an active customer (400 otherwise, BR-C1-01) |
| GET `/{uuid}` | SALE_INQUIRY_VIEW | → `SaleInquiryModel` (lines, `allowedNextStatuses`, `isEditable`, `quotations[]`) | 404 other org |
| PUT `/{uuid}` | SALE_INQUIRY_EDIT | `UpdateSaleInquiryRequest` | header only; 400 when QUOTED/DECLINED |
| POST `/{uuid}/lines` | SALE_INQUIRY_EDIT | `SaleInquiryLineRequest` → `Guid` (line) | `lineNumber` = max+1; description required; qty > 0 |
| PUT `/{uuid}/lines/{lineUuid}` | SALE_INQUIRY_EDIT | `UpdateSaleInquiryLineRequest` | request fields + evaluation; rules below |
| DELETE `/{uuid}/lines/{lineUuid}` | SALE_INQUIRY_EDIT | — | 400 when QUOTED/DECLINED |
| PATCH `/{uuid}/status` | SALE_INQUIRY_EDIT | `ChangeSaleInquiryStatusRequest {status, reason?}` → `SaleInquiryModel` | user transitions only |
| POST `/{uuid}/create-quotation` | SALE_QUOTATION_CREATE | `CreateSaleQuotationFromInquiryRequest` → `Guid` (quotation) | calls `ISaleQuotationService.CreateFromInquiryAsync` |

**State machine** (`SaleInquiryStatus`): `RECEIVED → UNDER_REVIEW` (≥ 1 line) · `UNDER_REVIEW → REVIEW_COMPLETE`
(≥ 1 line and **every** line evaluated — CAN_SUPPLY, PARTIAL or CANNOT_SUPPLY; no PENDING and no UNDER_REVIEW line;
BR-C1-03, the diagram's "all lines evaluated") · `UNDER_REVIEW | REVIEW_COMPLETE → DECLINED` (reason required) ·
`REVIEW_COMPLETE → QUOTED` (system only, via create-quotation / `MarkQuotedAsync`). QUOTED and DECLINED are terminal and
read-only (BR-C1-06). Header/lines are editable in RECEIVED, UNDER_REVIEW and REVIEW_COMPLETE. An edit in REVIEW_COMPLETE
that leaves a PENDING or UNDER_REVIEW line, or deletes the last line (adding a line, setting a line back to
PENDING/UNDER_REVIEW), is **not refused**: it moves the inquiry back to UNDER_REVIEW automatically.
`allowedNextStatuses` lists only the transitions possible right now, conditions included. `pendingLineCount` (list) counts
PENDING + UNDER_REVIEW lines — what stands between UNDER_REVIEW and REVIEW_COMPLETE.

**Line evaluation** (`SaleInquiryLineStatus`): PENDING · CAN_SUPPLY (needs estimatedDeliveryDate) · PARTIAL (needs
0 < canSupplyQuantity < requestedQuantity and estimatedDeliveryDate, BR-C1-05) · CANNOT_SUPPLY (needs an **active**
rejectionReasonUuid of the caller's org, BR-C1-04; alternative product/variant optional) · UNDER_REVIEW. Leaving PENDING
stamps reviewedByUserId/reviewedAt.

### 4.1 create-quotation line mapping (QUO implements, INQ's endpoint calls)
CAN_SUPPLY → NORMAL, qty = requestedQuantity · PARTIAL → NORMAL, qty = canSupplyQuantity · CANNOT_SUPPLY → REJECTED
(reason + notes copied, variant if any) **plus**, when it names an alternativeVariantUuid, an ALTERNATIVE line for it
(qty = requestedQuantity, alternativeNotes copied, alternativeFor = that REJECTED line). Every line keeps `SourceInquiryLineId`; `promisedDeliveryDate` = estimatedDeliveryDate;
NORMAL/ALTERNATIVE prices come from the sale-price waterfall (`IPricingService`, customer, validFrom). A CAN_SUPPLY/PARTIAL
line with no variant is a 400 naming the line ("identify the catalog item first"). (No PENDING or UNDER_REVIEW line
can exist here: create-quotation requires REVIEW_COMPLETE, which requires every line evaluated.)

## 5. Sale quotations — `api/sale-quotations` (QUO)

| Method & route | Permission | Body → result | Notes |
|---|---|---|---|
| GET `/` | SALE_QUOTATION_VIEW | `SaleQuotationListFilter` → `PaginatedResponse<SaleQuotationListItemModel>` | |
| POST `/` | SALE_QUOTATION_CREATE | `CreateSaleQuotationRequest` → `Guid` | DRAFT; active customer (BR-C2-01); validTo ≥ validFrom (BR-C2-03) |
| GET `/{uuid}` | SALE_QUOTATION_VIEW | → `SaleQuotationModel` (`isEditable`, `allowedActions`, `sourceInquiry`, `saleOrder`) | |
| PUT `/{uuid}` | SALE_QUOTATION_EDIT | `UpdateSaleQuotationRequest` | DRAFT only (400) |
| POST `/{uuid}/lines` | SALE_QUOTATION_EDIT | `SaleQuotationLineRequest` → `Guid` | DRAFT only |
| PUT `/{uuid}/lines/{lineUuid}` | SALE_QUOTATION_EDIT | `SaleQuotationLineRequest` | DRAFT only |
| DELETE `/{uuid}/lines/{lineUuid}` | SALE_QUOTATION_EDIT | — | DRAFT only; a REJECTED line with alternatives → 400 |
| POST `/{uuid}/send` | SALE_QUOTATION_SEND | — | DRAFT → SENT (BR-C2-04); stamps sentAt/sentBy |
| PATCH `/{uuid}/lines/{lineUuid}/customer-response` | SALE_QUOTATION_EDIT | `RecordCustomerResponseRequest` | SENT only; not on REJECTED lines; COUNTER needs counterPrice > 0 (BR-C2-09); `acceptCounterPrice` takes the counter as the price |
| POST `/{uuid}/accept` | SALE_QUOTATION_EDIT | — | SENT → ACCEPTED (≥ 1 ACCEPTED line, BR-C2-07) |
| POST `/{uuid}/reject` | SALE_QUOTATION_EDIT | `RejectSaleQuotationRequest {reason?}` | SENT → REJECTED (every non-REJECTED line REJECTED, BR-C2-08) |
| POST `/{uuid}/convert-to-order` | SALE_ORDER_CREATE | `ConvertSaleQuotationToOrderRequest` → `Guid` (sale order) | ACCEPTED → CONVERTED; only ACCEPTED lines (COUNTER excluded, T-C2-13) |
| POST `/{uuid}/copy` | SALE_QUOTATION_CREATE | — → `Guid` | optional; new DRAFT copy (§4.4) |

**State machine** (`SaleQuotationStatus`): `DRAFT → SENT → ACCEPTED → CONVERTED`; `SENT → REJECTED`; `SENT → EXPIRED`
(QuotationExpiryJob, daily 01:00 UTC, per org with `HangfireTenantScope`: `today > validTo`). REJECTED/EXPIRED/CONVERTED
are terminal. Only DRAFT is editable. `allowedActions` is computed from **state only** (SEND in DRAFT with ≥ 1
NORMAL/ALTERNATIVE line; RECORD_RESPONSE/ACCEPT/REJECT in SENT; CONVERT in ACCEPTED; COPY always) — the frontend ANDs it
with `hasPermission`.

**Line types** (`SaleQuotationLineType`): NORMAL · REJECTED (reason required, no price; customerResponse stays PENDING)
· ALTERNATIVE (alternativeFor → a REJECTED line of the same quotation, BR-C2-06). `variantUuid` required on
NORMAL/ALTERNATIVE, optional on REJECTED (deviation: the spec's NOT NULL would make a free-text inquiry line impossible to
reject on a quotation).

**Conversion (PD-04, QUO ↔ FND):** QUO loads the quotation tracked (own org), checks ACCEPTED, sets `Status = CONVERTED`
(+Modified*), builds `CreateSaleOrderFromQuotationCommand` from the ACCEPTED lines (variant, qty, **quoted unitPrice**,
discount, taxPercent, taxCodeUuid) and the request's order fields, and calls `ISaleOrderService.CreateFromQuotationAsync`.
That method derives partner, currency and `SourceInquiryId` from the quotation, applies the normal SO rules (numbering,
delivery mode + address, retail availability, min/max qty, tax-code re-check, totals), sets `SourceType = FROM_QUOTATION`,
and commits order + quotation in **one** `SaveChangesAsync`. `SaleOrders.SourceQuotationId` is uniquely indexed (filtered
NOT NULL) and `SaleQuotation.Status` is a concurrency token: a second conversion is a 409.

## 6. Rejection reasons — `api/rejection-reasons` (FND)

| Method & route | Permission | Body → result |
|---|---|---|
| GET `/?includeInactive=false` | any of SALE_INQUIRY_VIEW, SALE_INQUIRY_EDIT, SALE_QUOTATION_VIEW, SALE_QUOTATION_EDIT, SALE_REJECTION_REASON_MANAGE | → `RejectionReasonModel[]` by displayOrder, code |
| POST `/` | SALE_REJECTION_REASON_MANAGE | `CreateRejectionReasonRequest` → `RejectionReasonModel` (409 duplicate code) |
| PUT `/{uuid}` | SALE_REJECTION_REASON_MANAGE | `UpdateRejectionReasonRequest` (description, displayOrder) → `RejectionReasonModel` |
| PATCH `/{uuid}/deactivate` · `/{uuid}/activate` | SALE_REJECTION_REASON_MANAGE | → `RejectionReasonModel` |
| DELETE `/{uuid}` | SALE_REJECTION_REASON_MANAGE | custom + unused only; seeded (`isSystem`) or referenced → 409 |

Seed per org (provisioning + idempotent startup backfill): NIP, OOS, DIS, MOQ, GEO, REG, CAP, CRD, PRC, OTH (§7.3),
displayOrder 10…100, `isSystem = true`. Deactivated reasons stay on historical lines; a line may only be **set** to an
active reason. Admin UI: not assigned in the tracker — lead to decide (suggest FE-INQ, route `sales/rejection-reasons`).

## 7. Sale order extensions — `api/sale-orders` (FND)

| Method & route | Permission | Body → result | Notes |
|---|---|---|---|
| POST `/` (existing) | SALE_ORDER_CREATE | `CreateSaleOrderRequest` + `sourceType?`, `customerPoReference?`, `customerPoDate?` → `Guid` | `sourceType` only MANUAL (default); FROM_QUOTATION → 400 "use convert-to-order"; PORTAL/INTER_TENANT → 400 (A33). Duplicate customer PO → still 200, `message` carries the warning (BR-C3-05) |
| PUT `/{uuid}` (existing) | SALE_ORDER_EDIT | + `customerPoReference?`, `customerPoDate?` | DRAFT only, as today |
| PUT `/{uuid}/customer-po` | SALE_ORDER_EDIT | `UpdateSaleOrderCustomerPoRequest` | any status but CANCELLED/CLOSED; attachment must be a CUSTOMER_PO file of this order (400 otherwise) |
| GET `/customer-po-check?reference=&excludeUuid=` | any of SALE_ORDER_VIEW, _CREATE, _EDIT | → `CustomerPoDuplicateModel[]` | case-insensitive, trimmed; for the warning |
| GET `/{uuid}` (existing) | SALE_ORDER_VIEW | `SaleOrderModel` + `sourceType`, `sourceQuotation`, `sourceInquiry`, `customerPo*`; lines + `reservedQty`, `reservableQty`, `deliveryIndicator` | |
| GET `/` (existing) | SALE_ORDER_VIEW | + filter `sourceType`; `search` also matches customer PO | |
| POST `/{uuid}/lines/{lineUuid}/reserve` | SALE_ORDER_RESERVE | `ReserveSaleOrderLineRequest {quantity?, allowPartial=false, warehouseUuid?}` → `SaleOrderLineReservationModel` | see 7.1 |
| POST `/{uuid}/lines/{lineUuid}/release` | SALE_ORDER_RELEASE_RESERVATION | `ReleaseSaleOrderLineRequest {quantity?, reason?}` → `SaleOrderLineReservationModel` | 400 when nothing held |
| POST `/{uuid}/reserve-all` | SALE_ORDER_RESERVE | `ReserveAllSaleOrderLinesRequest {allowPartial=true}` → `SaleOrderReserveAllModel` | every reservable line |
| POST `/{uuid}/cancel` (existing) | SALE_ORDER_CANCEL | | now releases every SALES_ORDER hold of the order (BR-C4-05) — already did; kept |

`SourceType` (`SaleOrderSourceType`): MANUAL · FROM_QUOTATION · PORTAL · INTER_TENANT. Existing orders read MANUAL.
`sourceQuotation`/`sourceInquiry` are `{uuid, number, status}` links.

### 7.1 Reservation semantics
- Rows: `IStockReservationService`, source type `SALES_ORDER`, source = order uuid, source line = line uuid, expiry
  `now + SaleOrderConfig.ReservationTtlHours` (so `ReservationExpirySweepJob` releases them, BR-C4-06).
- **`reservedQty` is computed, not stored**: the sum of the line's ACTIVE `SALES_ORDER` holds, read at GET time. Reasons:
  those holds are changed outside Demand (Logistics moves them to a delivery and back; Inventory consumes; the sweep
  releases), so a stored column would drift. The spec's `reserved_qty` column (M6) is **n/a**.
- `reservableQty = max(0, quantity − fulfilledQty − reservedQty − held by the order's open deliveries)`.
- Reservable: order CONFIRMED or PARTIALLY_FULFILLED; line OPEN / RESERVED / PARTIALLY_FULFILLED and not DROP_SHIP.
  Else 400. DRAFT orders reserve by confirming.
- Reserve outcomes (`SaleOrderReservationOutcome`), all `200`: RESERVED (all held) · PARTIAL (allowPartial, part held) ·
  NEEDS_CONFIRMATION (not enough free, allowPartial false → nothing held; show "Available {availableQty}, Required
  {requestedQty}. Reserve partial?" and retry with `allowPartial: true`) · NONE_AVAILABLE (nothing free). Release → RELEASED.
  reserve-all also uses SKIPPED (+ `message`).
- After a change the line's `status` follows the existing paths: RESERVED when the order holds anything for it, OPEN when
  not (unless partially fulfilled); `deficitQty` is kept as `quantity − fulfilled − held` (the GRN link reserves up to it).
  The expiry sweep reconciles the same way (a line can now carry several holds, each with its own expiry).
- Reserve, release, reserve-all and cancel on one order are serialized by a per-order `sp_getapplock`.
- Cancel releases every SALES_ORDER hold first, then saves the order CANCELLED and its OPEN/RESERVED lines CANCELLED
  (lines that shipped keep their fulfilment status). Timeline: `SO_STOCK_RESERVED` on reserve, `SO_STOCK_RELEASED` on release.
- **Delivery indicator** (`deliveryIndicator`, §6.3, never stored): CANCELLED → GREY; FULFILLED/INVOICED → GREEN;
  else fulfilled ≥ qty → GREEN; fulfilled > 0 → YELLOW; held (order + its deliveries) ≥ qty → BLUE; held > 0 → YELLOW; else RED.

## 8. Schema (FND's single Demand migration — names for queries and reviews)

Tables (schema `demand`, snake_case table names like the existing `sale_orders`, PascalCase columns):
`sale_inquiries`, `sale_inquiry_lines`, `sale_quotations`, `sale_quotation_lines`, `rejection_reasons` — columns exactly
as the entity properties in `SalesPreOrderEntities.cs`. Key indexes: unique `(OrganizationId, InquiryNumber)`, unique
`(OrganizationId, QuotationNumber)`, unique `(SaleInquiryId, LineNumber)`, unique `(SaleQuotationId, LineNumber)`,
unique `(OrganizationId, Code)` on reasons; `(OrganizationId, Status)`, `PartnerId`, `(OrganizationId, ValidTo)`,
filtered `SourceInquiryId`, `AlternativeForLineId`, `RejectionReasonId`, `AssignedToUserId`. FKs inside Demand are
`Restrict`/`NoAction` except lines → header (`Cascade`).

`sale_orders` gains: `SourceType nvarchar(20) NOT NULL DEFAULT 'MANUAL'`, `SourceQuotationId int NULL` (FK, **unique**
filtered), `SourceInquiryId int NULL` (FK, filtered index), `CustomerPoReference nvarchar(50) NULL` (filtered index with
OrganizationId), `CustomerPoDate date NULL`, `CustomerPoAttachmentUuid uniqueidentifier NULL`. `sale_order_lines`: no change.

## 9. Deviations from the spec (all agreed in Part A or decided here)

1. UUIDs/ints/PascalCase instead of BIGINT/snake_case; `PartnerId` (not customer_id) as on SaleOrder; `CurrencyId` Guid
   (not currency_code); UoM as a code string (the catalog has no UoM table ids).
2. No attachment tables (M2/M3/M5 parts, PD-02): generic attachments, codes in §3.
3. RejectionReasons is Demand-owned (`demand.rejection_reasons`), not `lookups`; `IsSystem` flag added.
4. Permission codes UPPER_SNAKE (§2), seeded by `AuthDataSeeder`, not an auth migration (M7).
5. `reserved_qty` computed, not stored (§7.1).
6. One quotation → at most one sale order (unique `SourceQuotationId`).
7. POST /api/sale-orders does not accept source ids; a FROM_QUOTATION order is only made by convert-to-order.
8. Customer PO attachment is linked after the order exists (`PUT …/customer-po`), since its documentId is the order uuid.
9. Numbers are `PREFIX-YYYY-NNNNN` (5 digits) from `IDocumentNumberGenerator`.
10. `SaleQuotationLine.VariantUuid` nullable (REJECTED lines without a catalog item).
11. `SaleInquiry.DeclineReason` added (decline reason is required).
