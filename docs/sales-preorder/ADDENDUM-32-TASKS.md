# Addendum 32 — Task tracker (SMS-TR-ADD-032 v1.0)

Spec: `ADDENDUM-32-SALES-PREORDER-PIPELINE.md` (read its **Part A** first). Task ids are the register's.
Status: `todo` · `doing` · `done` · `n/a` (replaced by an existing mechanism — see Notes). Each agent updates
its own rows (owner column) when a task is done and tested.

## Agents (max 7, no sub-agents)

| Agent | Role | Owns |
|---|---|---|
| FND | Foundation → then Sale Order backend | Phase A; all Demand entities/maps/the migration; API contract + frontend TS service/models; permissions + seeder; rejection reasons; attachment interface codes; then Phase D/E backend |
| INQ | Sale Inquiry backend | PB-03..07, PB-12 |
| QUO | Sale Quotation backend | PC-03..09, PC-15 |
| FE-INQ | Sale Inquiry UI | PB-08..11 |
| FE-QUO | Sale Quotation UI | PC-10..14 |
| FE-SO | Sale Order UI (extensions + reservation) | PD-06, PD-07, PE-07..09 |
| QA | End-to-end, RBAC, multi-tenant, regression | Phase F |

## Repo-pattern decisions (apply everywhere — from Part A)

- Entities: `int Id` + `Guid UUID`, `Guid OrganizationId` (`ITenantScopedEntity`), PascalCase; cross-module refs are scalar Guids (PartnerId, VariantUuid, CurrencyId); users are `int`.
- **One** owner of the Demand model and migrations (FND). Nobody else adds migrations or changes maps.
- Permissions: UPPER_SNAKE in `SMS.Shared/Authorization/PermissionCodes.cs`; every new action `[RequirePermission(...)]` (any-of); server = frontend guard.
- Attachments: generic `api/attachments` (WorkflowEngine) with new interface codes — no per-document attachment tables.
- Rejection reasons: Demand-owned table (Demand migrates at startup; Lookups does not).
- Lines carry the SAP tax-code snapshot (`TaxCodeUuid`, `TaxCode`, `TaxPercent`); currency as `CurrencyId` like the SO.
- Writes filter to the caller's own organization explicitly (super admins bypass the tenant filter).
- Existing SO reservation paths (confirm/AvailabilityCheckService, GRN link, allocation transfer, ReservationExpirySweepJob) must stay consistent with the new reserved quantity.

## Phase A — Schema & Lookups (C5)

| Id | Task | Owner | Status | Notes |
|---|---|---|---|---|
| A32-PA-01 | RejectionReasons table (M1) | FND | done | `demand.rejection_reasons` in migration `20261003061626_A32_SalesPreOrderPipeline` (the ONE A32 Demand migration: all tables + SO columns; up/down/up on LocalDB, no pending model changes) |
| A32-PA-02 | RejectionReason entity & configuration | FND | done | `Domain/SalesPreOrderEntities.cs`, `Data/Maps/SalesPreOrderMaps.cs`; + `IsSystem` flag; unique (OrganizationId, Code) |
| A32-PA-03 | Seed 10 reasons per org (provisioning + idempotent backfill) | FND | done | New shared hooks `IOrganizationProvisionedHandler` (Tenancy calls them after an org is committed; failure logged, not fatal) + `IOrganizationDirectory` (Tenancy); Demand's `RejectionReasonProvisioningHandler`; startup backfill in `UseDemandModule` after Migrate (all orgs, adds only missing codes, never touches renamed/deactivated) |
| A32-PA-04 | RejectionReason CRUD API | FND | done | `api/rejection-reasons`: GET (any SALE_INQUIRY_VIEW/EDIT, SALE_QUOTATION_VIEW/EDIT, SALE_REJECTION_REASON_MANAGE), POST/PUT/PATCH deactivate+activate/DELETE (MANAGE). Code upper-cased, `^[A-Z0-9_]{1,10}$`, unique per org → 409; seeded or in-use → delete 409 |
| A32-PA-05 | Sales permission codes (M7) | FND | done | SALE_INQUIRY_VIEW/CREATE/EDIT, SALE_QUOTATION_VIEW/CREATE/EDIT/SEND, SALE_ORDER_RESERVE, SALE_ORDER_RELEASE_RESERVATION, SALE_REJECTION_REASON_MANAGE. System/Org Admin via All; Inventory Manager (= spec WAREHOUSE_MANAGER) + SALE_ORDER_VIEW/RESERVE/RELEASE_RESERVATION. Frontend `P.` added. Role-editor groups added |
| A32-PA-07 | Rejection reasons admin UI (lead's decision) | FE-INQ | done | `pages/sales/rejection-reasons`; route `sales/rejection-reasons` (SALE_REJECTION_REASON_MANAGE), menu Administration → Settings → Rejection Reasons (next to Sale Order Settings). List code/description/order/active (+ "Standard" for seeded), create (code upper-cased, `^[A-Z0-9_]{1,10}$`), edit description/order, deactivate/reactivate, delete only non-system (409 message shown) |
| A32-PA-06 | Phase A tests | FND | done | Demand `RejectionReasonServiceTests` (29, watched fail on stubs), Tenancy `OrganizationProvisioningHookTests` (3, watched fail), Auth `SalesPreOrderPermissionSeedTests` (written after the seeder edit — not test-first), WorkflowEngine `AttachmentAccessPolicyTests` +4 codes (8 watched fail) |

## Phase B — Sale Inquiry (C1)

| Id | Task | Owner | Status | Notes |
|---|---|---|---|---|
| A32-PB-01 | Inquiry tables (M2) | FND | done | `sale_inquiries`, `sale_inquiry_lines` (same migration); no attachments table — interface code SALE_INQUIRY |
| A32-PB-02 | Inquiry entities & configuration | FND | done | Status is a concurrency token; + DeclineReason |
| A32-PB-03 | INQ-{YYYY}-{NNNN} numbering | INQ | done | IDocumentNumberGenerator has no width → 5 digits (INQ-2026-00001), accepted by lead; year = received date's; drawn after validation; unique (OrganizationId, InquiryNumber) |
| A32-PB-04 | Inquiry state machine | INQ | done | REVIEW_COMPLETE needs ≥ 1 line, all CAN_SUPPLY/PARTIAL/CANNOT_SUPPLY; an edit leaving an undecided line (or none) reopens to UNDER_REVIEW; QUOTED only via MarkQuotedAsync |
| A32-PB-05 | Line evaluation service | INQ | done | in SaleInquiryService (no separate line service); fields not applying to the status are cleared |
| A32-PB-06 | Inquiry CRUD API | INQ | done | `SaleInquiriesController` api/sale-inquiries; own-org everywhere incl. the list |
| A32-PB-07 | Status & attachment API, create-quotation endpoint | INQ | done | attachments = generic api/attachments (SALE_INQUIRY), no endpoint here; create-quotation → QUO's CreateFromInquiryAsync |
| A32-PB-08 | Inquiry list page | FE-INQ | done | `pages/sales/sale-inquiries/sale-inquiry-list`; route `sales/inquiries` (SALE_INQUIRY_VIEW), menu Sales → Inquiries; filters status/customer/received range/search; badges in §10.1 colours; "New Inquiry" (SALE_INQUIRY_CREATE) → `sales/inquiries/new` (header-only form, guard SALE_INQUIRY_CREATE, opens the detail on Lines) |
| A32-PB-09 | Inquiry detail — header & tabs | FE-INQ | done | `sale-inquiry-detail`; route `sales/inquiries/:uuid` (VIEW). Start Review / Mark Review Complete / Decline driven by `allowedNextStatuses` + SALE_INQUIRY_EDIT, disabled with the reason; read-only when `!isEditable`; Details tab edits the header (assignee list = self + current, all users only with USER_MANAGE — api/users needs it); Create Quotation (REVIEW_COMPLETE + SALE_QUOTATION_CREATE) dialog → `sales/quotations/:uuid`, blocks supplied lines with no variant (contract 4.1) |
| A32-PB-10 | Lines tab — evaluation UI | FE-INQ | done | `sale-inquiry-lines`: table + status dots (§10.2 colours), expandable sub-row, evaluation drawer (fields per status as INQ clears them; client rules BR-C1-04/05), active reasons from the API (a deactivated one already on the line stays, marked), alternative via catalogue picker (RETAIL), add line (catalogue or free text), delete; every write reloads the inquiry |
| A32-PB-11 | Attachments tab | FE-INQ | done | `<app-attachment-list interfaceCode="SALE_INQUIRY" [documentId]="uuid">`, `[readOnly]` when QUOTED/DECLINED |
| A32-PB-12 | Phase B tests | INQ | done | Demand.Tests `SaleInquiryServiceTests` (T-C1-01..12, own org incl. super admin, numbering, state machine; T-C1-11 through the real quotation service) + `SaleInquiriesControllerTests` (routes/gates); Integration `SalesPreOrder/SaleInquiryHttpTests` (journey, 403 gates, super-admin 404) on LocalDB |

## Phase C — Sale Quotation (C2)

| Id | Task | Owner | Status | Notes |
|---|---|---|---|---|
| A32-PC-01 | Quotation tables (M3) | FND | done | `sale_quotations`, `sale_quotation_lines` (self-ref AlternativeForLineId); no attachments table — SALE_QUOTATION |
| A32-PC-02 | Quotation entities & configuration | FND | done | Status concurrency token; line VariantUuid nullable (REJECTED free-text lines) |
| A32-PC-03 | SQ-{YYYY}-{NNNN} numbering | QUO | done | 5 digits (SQ-2026-00001) via IDocumentNumberGenerator, drawn only after every rule passed; unique (OrganizationId, QuotationNumber) |
| A32-PC-04 | Quotation state machine | QUO | done | `SaleQuotationService`; only DRAFT editable; header/line edits refused otherwise (400) |
| A32-PC-05 | Line types & alternatives service | QUO | done | ALTERNATIVE → REJECTED line of same quotation (by uuid, or by line number incl. same create request); REJECTED: active own-org reason, no price; tax-code snapshot; header = offered (non-REJECTED) lines; retail availability + min/max checked like the SO; currency change refused while priced lines exist |
| A32-PC-06 | Quotation CRUD API | QUO | done | `SaleQuotationsController` api/sale-quotations, contract permissions |
| A32-PC-07 | Send, customer response, convert API | QUO | done | reject reason appended to InternalNotes (no column); convert reverts the tracked status if the SO call throws |
| A32-PC-08 | Create quotation from inquiry | QUO | done | §4.1 mapping; QUOTED → 409, other non-REVIEW_COMPLETE → 400; generated lines get the org's default SALES tax code if any |
| A32-PC-09 | QuotationExpiryJob (Hangfire) | QUO | done | `sale-quotation-expiry`, cron `7 1 * * *` UTC, per org with HangfireTenantScope, per-row save |
| A32-PC-10 | Quotation list page | FE-QUO | done | `sales/quotations` (SALE_QUOTATION_VIEW); filters status/customer/valid-to range/search; menu Sales › Quotations; "+ New Quotation" → header form `sales/quotations/new` (CREATE), draft header edit `:uuid/edit` (EDIT) |
| A32-PC-11 | Quotation detail — header & lines | FE-QUO | done | `sales/quotations/:uuid`; tabs Details/Lines/Terms/Attachments; alternatives lettered under their REJECTED line (3a, 3b) |
| A32-PC-12 | Line add/edit — alternative flow | FE-QUO | done | dialog editor; tax code/discount/total as the SO form (shared `saleOrderLineTotal`/`saleOrderGrandTotal`, `shared/tax-code-label.ts`); blank price = server prices it, same-currency rule price suggested |
| A32-PC-13 | Customer response panel | FE-QUO | done | responses saved one after another; Mark Accepted/Rejected save pending answers first; COUNTER→ACCEPTED offers acceptCounterPrice |
| A32-PC-14 | Attachments & send action | FE-QUO | done | SALE_QUOTATION attachments; send confirm + Sent badge; convert dialog (PO ref/date, delivery mode/address) → opens the SO, link to FE-SO's full form; Copy (CREATE) |
| A32-PC-15 | Phase C tests | QUO | done | Demand.Tests `SaleQuotationServiceTests`, `QuotationExpiryJobTests` (+ controller permission reflection), T-C2-01..13, own-org 404 incl. super admin; LocalDB `SalesPreOrder/SaleQuotationConcurrencyE2ETests`: numbering race, double convert 200/409, double create-quotation 200/409, 403 gates |

## Phase D — Sale Order Extensions (C3)

| Id | Task | Owner | Status | Notes |
|---|---|---|---|---|
| A32-PD-01 | SO source & customer-PO columns (M4) | FND | done | SourceType (default MANUAL), SourceQuotationId (FK, unique filtered — one order per quotation), SourceInquiryId (FK), CustomerPoReference (+ org index), CustomerPoDate, CustomerPoAttachmentUuid |
| A32-PD-02 | SaleOrderAttachments table (M5) | FND | n/a | generic attachments: interface codes CUSTOMER_PO and SALE_ORDER (documentId = order uuid) in AttachmentAccessPolicy |
| A32-PD-03 | SO entity & DTO extension | FND | done | POST accepts `sourceType` (only MANUAL; FROM_QUOTATION/PORTAL/INTER_TENANT → 400), `customerPoReference` (trimmed, ≤ 50), `customerPoDate`; PUT (DRAFT) too. GET returns sourceType + sourceQuotation/sourceInquiry links + customerPo*; list filter `sourceType`, search also matches customer PO. Duplicate PO = warning in the create message + `GET customer-po-check` (BR-C3-05) |
| A32-PD-04 | Quotation → SO conversion | FND/QUO | done | `ISaleOrderService.CreateFromQuotationAsync`: partner/currency/SourceInquiryId from the quotation, quoted prices (no re-pricing), tax codes re-checked, normal SO rules; one SaveChanges commits order + QUO's CONVERTED. Second conversion → 409 (existing-order check + unique index + quotation Status concurrency token). QUO's LocalDB E2E race test passes (1×200, 1×409) |
| A32-PD-05 | SO attachment API | FND | done | Generic attachments (CUSTOMER_PO / SALE_ORDER codes) + `PUT api/sale-orders/{uuid}/customer-po` (SALE_ORDER_EDIT; any status but CANCELLED/CLOSED; the file must be a CUSTOMER_PO upload of that order, checked via IAttachmentService) |
| A32-PD-06 | SO header — source & customer PO | FE-SO | done | Detail: source badge by the status; "Source & customer PO" card. The quotation/inquiry numbers link to `sales/quotations/:uuid` / `sales/inquiries/:uuid` only with *_VIEW (plain text otherwise). Card shows PO ref, PO date (date-only helper), the linked file, and a `CUSTOMER_PO` attachment-list for upload/view. Edit dialog (SALE_ORDER_EDIT, not CANCELLED/CLOSED) → PUT customer-po: ref ≤ 50, date, and the PO file chosen from the order's CUSTOMER_PO files; the only file is preselected. Non-blocking duplicate banner from `customer-po-check` (also in the dialog on blur). Page now follows `paramMap`, since the banner links to another order on the same route. Not added: an Attachments tab (the existing spec pins six tabs). Specs: `sale-order-detail.a32.spec.ts` |
| A32-PD-07 | SO create — source selection | FE-SO | done | New-order form: "Create directly" / "From an accepted quotation" (the latter disabled without SALE_QUOTATION_VIEW). Quotation mode: autocomplete over `status=ACCEPTED` → reads the quotation → customer/currency/accepted lines (COUNTER excluded) shown read-only → `convertQuotationToOrder` with delivery mode/address/expected date/notes/customer PO → opens the new SO. Direct mode = today's form + optional customer PO ref/date (dup warning on blur), also sent back on a draft edit. Entry points: list "From quotation" button (`?source=quotation`) and `orders/new?quotation=<uuid>` (for FE-QUO). Specs: `sale-order-form.a32.spec.ts` + list spec |
| A32-PD-08 | Phase D tests | FND | done | `SaleOrderSourceAndCustomerPoTests` (27; watched fail on stubs) |

## Phase E — Delivery Indicators & Reservation (C4)

| Id | Task | Owner | Status | Notes |
|---|---|---|---|---|
| A32-PE-01 | ReservedQty on SO lines (M6) | FND | n/a | **Decision: computed, not stored.** ReservedQty = sum of the line's ACTIVE SALES_ORDER reservations, read at GET time. Those holds are changed outside Demand (Logistics transfers them to/from deliveries, Inventory consumes, the expiry sweep releases), so a stored column would drift; no backfill needed |
| A32-PE-02 | ReservedQty + computed DeliveryIndicator | FND | done | `SaleOrderHolds`: ReservedQty = ACTIVE SALES_ORDER holds per line; held for the indicator also counts the order's not-yet-issued delivery lines (new shared `ISaleOrderDeliveryQuantities`, implemented in Logistics with the same in-flight rule as delivery creation); `reservableQty` = qty − fulfilled − reserved − in flight. Indicator per §6.3 (cancelled order/line → GREY) |
| A32-PE-03 | Reserve & release service | FND | done | `SaleOrderReservationService`: CONFIRMED/PARTIALLY_FULFILLED orders, OPEN/RESERVED/PARTIALLY_FULFILLED non-drop-ship lines; partial allowed (allowPartial) else NEEDS_CONFIRMATION; TTL from config; line Status/DeficitQty reconciled from the ledger (DeficitQty = unreserved balance, so the GRN link never double-holds). Per-order `sp_getapplock` serializes reserve/release/cancel |
| A32-PE-04 | Auto-release on SO cancel | FND | done | Cancel runs under the order lock: releases every SALES_ORDER hold FIRST, then saves CANCELLED (OPEN/RESERVED lines → CANCELLED). Cross-DB atomicity is impossible (Inventory's ledger commits separately); release-first means a failed save leaves an open order with nothing held, never a cancelled order with stock locked |
| A32-PE-05 | Reservation permissions | FND | done | SALE_ORDER_RESERVE, SALE_ORDER_RELEASE_RESERVATION (see PA-05) |
| A32-PE-06 | Reserve / release / reserve-all API | FND | done | `POST api/sale-orders/{uuid}/lines/{lineUuid}/reserve` · `/release` · `POST {uuid}/reserve-all`; 200 + outcome, 400 rule, 404 other org |
| A32-PE-07 | Delivery indicator column | FE-SO | done | "Ind" column: coloured dot per `deliveryIndicator` with a tooltip (meaning + reserved/delivered figures). Updated at once from each reserve/release/reserve-all result, then the order is re-read (fulfilment shows on the next read; `—` when absent) |
| A32-PE-08 | Reserved qty column & buttons | FE-SO | done | "Reserved" column (`reservedQty`). Reserve shows on CONFIRMED/PARTIALLY_FULFILLED orders for OPEN/RESERVED/PARTIALLY_FULFILLED non-drop-ship lines with `reservableQty` > 0; Release shows when `reservedQty` > 0; "Reserve all open lines" (allowPartial true). Without SALE_ORDER_RESERVE / SALE_ORDER_RELEASE_RESERVATION each is **disabled, not hidden**, with the contract's tooltip; the two codes are independent; a note under the table names the permissions |
| A32-PE-09 | Partial reservation dialog | FE-SO | done | NEEDS_CONFIRMATION → dialog "Available: X, Required: Y. Reserve X units?" with item + SKU and warehouse; Confirm → reserve `{quantity: availableQty, allowPartial: true, warehouseUuid}` (same warehouse); Cancel/close holds nothing. NONE_AVAILABLE → warning toast with the server's message |
| A32-PE-10 | TTL & expiry integration | FND | done | Manual holds carry `now + ReservationTtlHours`; `ReservationExpirySweepJob` now reconciles the line from what the ledger still holds (a line can have several holds) instead of resetting it to OPEN/full deficit |
| A32-PE-11 | Phase E tests | FND | done | `SaleOrderReservationServiceTests` (46, real Inventory StockReservationService in-memory; watched fail on stubs) + `SaleOrderReservationConcurrencyTests` (2, LocalDB `sp_getapplock`; watched fail with the lock bypassed: 20 held / hold left on a cancelled order) |

## Phase F — Testing & Integration

| Id | Task | Owner | Status | Notes |
|---|---|---|---|---|
| A32-PF-01 | E2E full chain | QA | todo | |
| A32-PF-02 | E2E direct SO + customer PO + reservation | QA | todo | |
| A32-PF-03 | E2E quotation rejection & expiry | QA | todo | |
| A32-PF-04 | E2E RBAC matrix | QA | todo | |
| A32-PF-05 | E2E multi-tenant isolation | QA | todo | |
| A32-PF-06 | Regression — existing SO & reservation flows | QA | todo | |
| A32-PF-07 | Final review & documentation | lead | todo | |
