# SMS Task Register — Addendum 32: Sales Pre-Order Pipeline

**Document:** SMS-TR-ADD-032 v1.0 | **FSD:** SMS-FSD-ADD-032 v1.0
**Total:** 52 Tasks | 184 Hours | 23 Dev Days | 7 Migrations

---

## Phase A — Schema & Lookups (C5)

**Priority:** P0 — Critical Path | **Estimate:** 16 Hours (2 Days) | **FSD:** §7, §8 M1

### Track A1 — Rejection Reason Lookup (C5)

- [ ] **A32-PA-01** — Create RejectionReasons Table (Migration M1) `3h`
  - Schema: `lookups` | FSD: §7.2, §8 M1 | Depends: — | Tests: T-C5-01
  - CREATE TABLE lookups.RejectionReasons: rejection_reason_id INT PK, org_id INT FK, code NVARCHAR(10), description NVARCHAR(200), is_active BIT, display_order INT. UQ on (org_id, code).

- [ ] **A32-PA-02** — RejectionReason EF Core Entity & Configuration `2h`
  - Schema: `lookups` | FSD: §7.2 | Depends: A32-PA-01 | Tests: —
  - Entity class, IEntityTypeConfiguration, HasQueryFilter(org_id), AutoMapper profile, FluentValidation (code unique per org, max 10 chars).

- [ ] **A32-PA-03** — RejectionReason Seed Data via Tenant Provisioning `2h`
  - Schema: `lookups, tenant` | FSD: §7.3, BR-C5-02 | Depends: A32-PA-01 | Tests: T-C5-02
  - Insert 10 seed codes (NIP, OOS, DIS, MOQ, GEO, REG, CAP, CRD, PRC, OTH) per org on provisioning. Idempotent — skip if exists.

- [ ] **A32-PA-04** — RejectionReason CRUD API `3h`
  - Schema: `lookups` | FSD: §9.5 | Depends: A32-PA-02 | Tests: T-C5-01–T-C5-04
  - GET /api/rejection-reasons (active only, sorted display_order). POST (admin). PUT (admin). PATCH deactivate (admin, cannot delete seed codes). Dropdown endpoint for Inquiry/Quotation forms.

- [ ] **A32-PA-05** — Seed Sales Permission Claims (Migration M7) `2h`
  - Schema: `auth` | FSD: §8 M7, §13.4 | Depends: — | Tests: —
  - INSERT 10 permission claims: sales_inquiry_view/create/edit, sales_quotation_view/create/edit/send, sales_order_create, sales_reserve_inventory, sales_release_reservation. Assign to default SALES_MANAGER, WAREHOUSE_MANAGER roles.

### Phase A Completion

- [ ] **A32-PA-06** — Phase A Unit Tests `4h`
  - Schema: `lookups, auth` | FSD: §12 | Depends: A32-PA-01–05 | Tests: T-C5-01–T-C5-04
  - RejectionReason CRUD, seed data integrity, permission claim seeding, tenant provisioning idempotency

---

## Phase B — Sale Inquiry Module (C1)

**Priority:** P0 — Critical Path | **Estimate:** 40 Hours (5 Days) | **FSD:** §3

> **⚡ Parallelizable:** Phase B and Phase C share only RejectionReasons (Phase A). They can run concurrently on separate agents.

### Track B1 — Sale Inquiry Backend

- [ ] **A32-PB-01** — Create SaleInquiries + SaleInquiryLines + Attachments Tables (Migration M2) `4h`
  - Schema: `demand` | FSD: §3.2, §3.3, §3.6, §8 M2 | Depends: A32-PA-01 | Tests: —
  - CREATE TABLE demand.SaleInquiries (13 columns + trace_id), demand.SaleInquiryLines (22 columns), demand.SaleInquiryAttachments (8 columns). All FKs, indexes, and defaults per FSD §8.2.

- [ ] **A32-PB-02** — SaleInquiry EF Core Entities & Configuration `4h`
  - Schema: `demand` | FSD: §3.2, §3.3 | Depends: A32-PB-01 | Tests: —
  - SaleInquiry, SaleInquiryLine, SaleInquiryAttachment entity classes. IEntityTypeConfiguration with HasQueryFilter(org_id). Navigation properties. AutoMapper profiles.

- [ ] **A32-PB-03** — Inquiry Number Auto-Generation Service `2h`
  - Schema: `demand` | FSD: §2.2, BR-C1-02 | Depends: A32-PB-02 | Tests: T-C1-01
  - Pattern INQ-{YYYY}-{NNNN} per org per year. Thread-safe sequence via SELECT MAX + 1 with row lock or HiLo.

- [ ] **A32-PB-04** — SaleInquiry State Machine Service `4h`
  - Schema: `demand` | FSD: §3.4, BR-C1-03, BR-C1-06, BR-C1-07 | Depends: A32-PB-02 | Tests: T-C1-09, T-C1-10, T-C1-12
  - Transition rules: RECEIVED→UNDER_REVIEW, UNDER_REVIEW→REVIEW_COMPLETE (all lines ≠ PENDING), UNDER_REVIEW/REVIEW_COMPLETE→DECLINED, REVIEW_COMPLETE→QUOTED (set by quotation creation). Read-only guard for QUOTED/DECLINED.

- [ ] **A32-PB-05** — SaleInquiryLine Evaluation Service `4h`
  - Schema: `demand, lookups` | FSD: §3.5, BR-C1-04, BR-C1-05 | Depends: A32-PB-02, A32-PA-02 | Tests: T-C1-05–T-C1-08
  - Line status transitions: PENDING→CAN_SUPPLY (requires estimated_delivery_date), PENDING→PARTIAL (requires can_supply_quantity > 0 < requested), PENDING→CANNOT_SUPPLY (requires rejection_reason_id), PENDING→UNDER_REVIEW. Alternative product linking on CANNOT_SUPPLY.

- [ ] **A32-PB-06** — Sale Inquiry CRUD API `4h`
  - Schema: `demand` | FSD: §9.1 | Depends: A32-PB-03, A32-PB-04, A32-PB-05 | Tests: T-C1-01–T-C1-04
  - Endpoints: GET list (paginated, filterable by status/customer/date), POST create, GET/{id}, PUT update header, POST/{id}/lines, PUT/{id}/lines/{lineId}, DELETE/{id}/lines/{lineId}. Permission: sales_inquiry_view, sales_inquiry_create, sales_inquiry_edit.

- [ ] **A32-PB-07** — Sale Inquiry Status & Attachment API `3h`
  - Schema: `demand` | FSD: §9.1 | Depends: A32-PB-04, A32-PB-06 | Tests: T-C1-09–T-C1-12
  - PATCH /{id}/status (state transitions). POST /{id}/attachments (file upload). DELETE /{id}/attachments/{attachId}. POST /{id}/create-quotation (requires sales_quotation_create).

### Track B2 — Sale Inquiry UI

- [ ] **A32-PB-08** — Sale Inquiry List Page `3h`
  - Schema: — (UI) | FSD: §10.1 | Depends: A32-PB-06 | Tests: —
  - Paginated table: Inquiry #, Customer, Received Date, Status (color badge), Line Count. Filters: Status, Customer, Date Range. "+ New Inquiry" button. Nav: Sales → Inquiries.

- [ ] **A32-PB-09** — Sale Inquiry Detail — Header & Tabs `3h`
  - Schema: — (UI) | FSD: §10.2 | Depends: A32-PB-06 | Tests: —
  - Header: Inquiry #, Status badge, Customer name, Customer Ref, Received Date, Deadline, Assigned To dropdown. Tabs: Details, Lines, Attachments. Action buttons: Mark Review Complete, Decline Inquiry.

- [ ] **A32-PB-10** — Sale Inquiry Lines Tab — Evaluation UI `5h`
  - Schema: — (UI) | FSD: §10.2 | Depends: A32-PB-05 | Tests: T-C1-05–T-C1-08
  - Lines table: #, Product, Qty, UOM, Delivery Date, Status (color dot), Action. Inline edit drawer per line: status dropdown, can_supply_quantity (PARTIAL), estimated_delivery_date, rejection_reason dropdown (CANNOT_SUPPLY), alternative product search, notes. Expandable sub-row showing evaluation details.

- [ ] **A32-PB-11** — Sale Inquiry Attachments Tab `2h`
  - Schema: — (UI) | FSD: §10.2 | Depends: A32-PB-07 | Tests: —
  - File upload dropzone. File list: name, size, type, uploaded by, date, download/delete actions. Max file size validation.

### Phase B Completion

- [ ] **A32-PB-12** — Phase B Unit & Integration Tests `4h`
  - Schema: `demand` | FSD: §12 | Depends: A32-PB-01–11 | Tests: T-C1-01–T-C1-12
  - Inquiry creation, line evaluation, state transitions, read-only guards, customer validation, auto-numbering, create-quotation flow

---

## Phase C — Sale Quotation Module (C2)

**Priority:** P0 — Critical Path | **Estimate:** 48 Hours (6 Days) | **FSD:** §4

> **⚡ Parallelizable:** Phase C runs concurrently with Phase B. Both depend only on Phase A.

### Track C1 — Sale Quotation Backend

- [ ] **A32-PC-01** — Create SaleQuotations + SaleQuotationLines + Attachments Tables (Migration M3) `4h`
  - Schema: `demand` | FSD: §4.2, §4.3, §4.6, §8 M3 | Depends: A32-PA-01, A32-PB-01 | Tests: —
  - CREATE TABLE demand.SaleQuotations (22 columns + trace_id), demand.SaleQuotationLines (23 columns + self-ref FK), demand.SaleQuotationAttachments (8 columns). All FKs, indexes per FSD §8.3. Self-referencing FK on alternative_for_line_id.

- [ ] **A32-PC-02** — SaleQuotation EF Core Entities & Configuration `4h`
  - Schema: `demand` | FSD: §4.2, §4.3 | Depends: A32-PC-01 | Tests: —
  - SaleQuotation, SaleQuotationLine, SaleQuotationAttachment entities. HasQueryFilter(org_id). Navigation: Quotation→Lines, Line→AlternativeForLine (self-ref), Line→Alternatives (inverse). AutoMapper profiles.

- [ ] **A32-PC-03** — Quotation Number Auto-Generation Service `2h`
  - Schema: `demand` | FSD: §2.2, BR-C2-02 | Depends: A32-PC-02 | Tests: T-C2-01
  - Pattern SQ-{YYYY}-{NNNN} per org per year. Same thread-safe approach as inquiry numbering.

- [ ] **A32-PC-04** — SaleQuotation State Machine Service `4h`
  - Schema: `demand` | FSD: §4.4, BR-C2-04, BR-C2-07, BR-C2-08, BR-C2-11 | Depends: A32-PC-02 | Tests: T-C2-05, T-C2-10
  - Transitions: DRAFT→SENT (requires ≥1 NORMAL/ALT line), SENT→ACCEPTED (≥1 line ACCEPTED), SENT→REJECTED (all non-REJECTED lines customer_response=REJECTED), SENT→EXPIRED (batch job), ACCEPTED→CONVERTED (on SO creation). DRAFT editable, SENT+ read-only guard.

- [ ] **A32-PC-05** — SaleQuotationLine Service — Types & Alternatives `5h`
  - Schema: `demand, lookups` | FSD: §4.5, BR-C2-05, BR-C2-06, BR-C2-09 | Depends: A32-PC-02, A32-PA-02 | Tests: T-C2-03, T-C2-04, T-C2-08, T-C2-09
  - Line type validation: ALTERNATIVE requires alternative_for_line_id→REJECTED line in same quotation. REJECTED requires rejection_reason_id. COUNTER requires customer_counter_price > 0. Edit guard: lines modifiable only in DRAFT. Totals computation: (qty × price × (1 - discount%)) + tax.

- [ ] **A32-PC-06** — Sale Quotation CRUD API `4h`
  - Schema: `demand` | FSD: §9.2 | Depends: A32-PC-03, A32-PC-04, A32-PC-05 | Tests: T-C2-01, T-C2-02, T-C2-06
  - GET list (paginated, filterable). POST create (independent or from inquiry). GET/{id}. PUT update header (DRAFT only). POST/{id}/lines. PUT/{id}/lines/{lineId}. DELETE/{id}/lines/{lineId}. Permissions: sales_quotation_view, sales_quotation_create, sales_quotation_edit.

- [ ] **A32-PC-07** — Quotation Send, Customer Response & Conversion API `5h`
  - Schema: `demand` | FSD: §9.2, §5.5 | Depends: A32-PC-04, A32-PC-06 | Tests: T-C2-05, T-C2-07, T-C2-10–T-C2-13
  - POST /{id}/send (DRAFT→SENT, records sent_at). PATCH /{id}/lines/{lineId}/customer-response (per-line ACCEPTED/REJECTED/COUNTER). POST /{id}/accept (SENT→ACCEPTED). POST /{id}/reject (SENT→REJECTED). POST /{id}/convert-to-order (ACCEPTED→CONVERTED, creates SO with accepted lines). Permission: sales_quotation_send, sales_quotation_edit.

- [ ] **A32-PC-08** — Create Quotation from Inquiry Flow `3h`
  - Schema: `demand` | FSD: §9.1 (create-quotation), BR-C1-07 | Depends: A32-PB-04, A32-PC-06 | Tests: T-C1-11, T-C2-02
  - POST /api/sale-inquiries/{id}/create-quotation: creates SaleQuotation with source_inquiry_id, pre-populates lines from inquiry (CAN_SUPPLY→NORMAL, PARTIAL→NORMAL with adjusted qty, CANNOT_SUPPLY→REJECTED with reason + optional ALTERNATIVE). Sets inquiry status→QUOTED. Links source_inquiry_line_id.

- [ ] **A32-PC-09** — Quotation Expiry Hangfire Job `2h`
  - Schema: `demand, hangfire` | FSD: §4.4, BR-C2-10, §13.3 | Depends: A32-PC-04 | Tests: T-C2-11
  - QuotationExpiryJob: daily 01:00 UTC, scans SENT quotations WHERE valid_to < SYSUTCDATETIME(), sets status=EXPIRED. Idempotent. Per-org HasQueryFilter applied.

### Track C2 — Sale Quotation UI

- [ ] **A32-PC-10** — Sale Quotation List Page `3h`
  - Schema: — (UI) | FSD: §10.3 | Depends: A32-PC-06 | Tests: —
  - Paginated table: Quotation #, Customer, Valid From/To, Status (badge), Grand Total, Source Inquiry (link or "—"). Filters: Status, Customer, Date Range. "+ New Quotation" button.

- [ ] **A32-PC-11** — Sale Quotation Detail — Header & Line Table `5h`
  - Schema: — (UI) | FSD: §10.3 | Depends: A32-PC-06, A32-PC-05 | Tests: T-C2-03
  - Header: Quotation #, Status, Customer, Validity, Currency, Inquiry link. Tabs: Details, Lines, Terms, Attachments. Lines table: #, Product, Qty, Price, Total, Type badge (NORMAL/✕ REJ/◇ ALT), Customer Response. REJECTED lines greyed. ALT lines indented under parent REJECTED line. Subtotal/Tax/Total footer.

- [ ] **A32-PC-12** — Quotation Line Add/Edit Form — Alternative Flow `4h`
  - Schema: — (UI) | FSD: §10.3 | Depends: A32-PC-05 | Tests: T-C2-03, T-C2-04
  - Line form: Product search, Qty, UOM, Unit Price, Discount%, Tax%, Delivery Date. Line Type selector: NORMAL | REJECTED (shows rejection reason dropdown + notes) | ALTERNATIVE (shows "Alternative for" dropdown filtered to REJECTED lines + notes). Totals auto-computed.

- [ ] **A32-PC-13** — Customer Response Recording Panel `4h`
  - Schema: — (UI) | FSD: §10.4 | Depends: A32-PC-07 | Tests: T-C2-07–T-C2-09
  - SENT quotation: per-line response dropdown (ACCEPTED/REJECTED/COUNTER). COUNTER shows counter_price input + notes. Bulk action: "Mark Quotation Accepted" (enabled when ≥1 ACCEPTED). "Mark Quotation Rejected" (enabled when all non-REJECTED = REJECTED). Save responses button.

- [ ] **A32-PC-14** — Quotation Attachments & Send Action `2h`
  - Schema: — (UI) | FSD: §10.3 | Depends: A32-PC-07 | Tests: T-C2-05
  - Attachments tab: upload/download/delete. "Send to Customer" button (DRAFT only): confirmation dialog, triggers DRAFT→SENT. Sent badge with timestamp.

### Phase C Completion

- [ ] **A32-PC-15** — Phase C Unit & Integration Tests `4h`
  - Schema: `demand` | FSD: §12 | Depends: A32-PC-01–14 | Tests: T-C2-01–T-C2-13
  - Quotation CRUD, line type validation, alternative linking, customer response, state transitions, expiry job, inquiry→quotation flow, conversion logic

---

## Phase D — Sale Order Extensions (C3)

**Priority:** P0 — Critical Path | **Estimate:** 24 Hours (3 Days) | **FSD:** §5

> **⚠️ Sequenced:** Phase D depends on Phase C (conversion logic requires quotation entities).

### Track D1 — Source Linking & Customer PO Backend

- [ ] **A32-PD-01** — Extend SaleOrders Table — Source & Customer PO Columns (Migration M4) `3h`
  - Schema: `demand` | FSD: §5.2, §8 M4 | Depends: A32-PC-01 | Tests: —
  - ALTER TABLE demand.SaleOrders ADD: source_quotation_id BIGINT NULL FK, source_inquiry_id BIGINT NULL FK, source_type NVARCHAR(20) DEFAULT 'MANUAL', customer_po_reference NVARCHAR(50) NULL, customer_po_date DATE NULL, customer_po_attachment_id BIGINT NULL. Filtered indexes. Existing rows backfilled with source_type='MANUAL' via DEFAULT.

- [ ] **A32-PD-02** — Create SaleOrderAttachments Table (Migration M5) `2h`
  - Schema: `demand` | FSD: §5.3, §8 M5 | Depends: — | Tests: T-C3-03
  - CREATE TABLE demand.SaleOrderAttachments: attachment_id PK, sale_order_id FK, org_id, file_name, file_path, file_size_bytes, content_type, attachment_type (CUSTOMER_PO/GENERAL), uploaded_by, uploaded_at.

- [ ] **A32-PD-03** — SaleOrder Entity Extension & DTO Updates `3h`
  - Schema: `demand` | FSD: §5.2, §5.4, §9.3 | Depends: A32-PD-01, A32-PD-02 | Tests: T-C3-01, T-C3-02
  - Add SourceQuotationId, SourceInquiryId, SourceType, CustomerPoReference, CustomerPoDate, CustomerPoAttachmentId to SaleOrder entity. Update Create/Update/Response DTOs. source_type enum validation. Duplicate customer_po_reference warning (not error, BR-C3-05).

- [ ] **A32-PD-04** — Quotation → Sale Order Conversion Service `5h`
  - Schema: `demand` | FSD: §5.5, BR-C3-02, BR-C3-03 | Depends: A32-PD-03, A32-PC-07 | Tests: T-C2-12, T-C2-13, T-C3-05
  - ConvertQuotationToSaleOrder: filter ACCEPTED lines, create SO with source_quotation_id + chained source_inquiry_id, copy line data (variant, qty, price, discount, tax). Set quotation→CONVERTED. Accept customer_po_reference at conversion time. COUNTER lines excluded until resolved.

- [ ] **A32-PD-05** — Sale Order Attachment API `2h`
  - Schema: `demand` | FSD: §9.3 | Depends: A32-PD-02 | Tests: T-C3-03
  - POST /api/sale-orders/{id}/attachments (upload, set type CUSTOMER_PO or GENERAL). GET /api/sale-orders/{id}/attachments. DELETE attachment. Link customer_po_attachment_id on SO header.

### Track D2 — Sale Order Extended UI

- [ ] **A32-PD-06** — Sale Order Header — Source & Customer PO Section `4h`
  - Schema: — (UI) | FSD: §10.5 | Depends: A32-PD-03 | Tests: T-C3-01, T-C3-02
  - Source section: Source type badge (MANUAL/FROM_QUOTATION/PORTAL/INTER_TENANT), clickable quotation link, clickable inquiry link. Customer PO section: PO Reference input, PO Date picker, PO attachment upload/view. Duplicate PO reference warning banner (non-blocking).

- [ ] **A32-PD-07** — Sale Order Create — Source Selection Flow `3h`
  - Schema: — (UI) | FSD: §5.4 | Depends: A32-PD-04 | Tests: T-C3-01
  - Create SO dialog: "Create from Quotation" (searchable quotation picker, filtered to ACCEPTED) or "Create Directly" (standard form). From Quotation: lines pre-populated, read-only product/qty, editable customer PO fields. Direct: standard line-by-line entry.

### Phase D Completion

- [ ] **A32-PD-08** — Phase D Unit & Integration Tests `3h`
  - Schema: `demand` | FSD: §12 | Depends: A32-PD-01–07 | Tests: T-C3-01–T-C3-05
  - Source linking, conversion logic, customer PO capture, attachment upload, duplicate PO warning, inquiry chain-through

---

## Phase E — Delivery Indicators & Inventory Reservation (C4)

**Priority:** P1 — High | **Estimate:** 32 Hours (4 Days) | **FSD:** §6

> **⚠️ Sequenced:** Phase E depends on Phase D (reservation UI lives on the SO line panel that Phase D extends).

### Track E1 — Reservation Backend

- [ ] **A32-PE-01** — Add reserved_qty to SaleOrderLines (Migration M6) `2h`
  - Schema: `demand` | FSD: §6.2, §8 M6 | Depends: — | Tests: —
  - ALTER TABLE demand.SaleOrderLines ADD reserved_qty DECIMAL(18,4) NOT NULL DEFAULT 0. Backfill from StockReservations for existing RESERVED lines.

- [ ] **A32-PE-02** — SaleOrderLine Entity & DTO — reserved_qty + DeliveryIndicator `3h`
  - Schema: `demand` | FSD: §6.2, §6.3, BR-C4-07 | Depends: A32-PE-01 | Tests: T-C4-01, T-C4-08, T-C4-09
  - Add ReservedQty to entity. DeliveryIndicator computed property on DTO: GREEN (fulfilled ≥ qty), BLUE (reserved ≥ qty), YELLOW (partial fulfillment or partial reservation), RED (nothing), GREY (cancelled). Never persisted.

- [ ] **A32-PE-03** — SaleOrderReservationService — Reserve & Release `5h`
  - Schema: `demand, inventory` | FSD: §6.4, BR-C4-03, BR-C4-04 | Depends: A32-PE-02 | Tests: T-C4-02, T-C4-03, T-C4-05
  - Reserve: check available_qty, call IStockReservationService.Reserve(variantId, warehouseId, qty, SALES_ORDER, soId), update SOLine.reserved_qty. Partial reservation dialog support. Release: call IStockReservationService.Release(reservationId), decrement SOLine.reserved_qty, return stock to available.

- [ ] **A32-PE-04** — Auto-Release on Sale Order Cancellation `3h`
  - Schema: `demand, inventory` | FSD: §6.4, BR-C4-05 | Depends: A32-PE-03 | Tests: T-C4-06
  - On SO status→CANCELLED: iterate lines with reserved_qty > 0, call Release for each, set reserved_qty=0. Transactional — all-or-nothing.

- [ ] **A32-PE-05** — Reservation Permission Enforcement `2h`
  - Schema: `demand, auth` | FSD: §6.5, BR-C4-01, BR-C4-02 | Depends: A32-PA-05, A32-PE-03 | Tests: T-C4-04
  - API endpoints: POST /reserve requires sales_reserve_inventory claim. POST /release requires sales_release_reservation claim. POST /reserve-all requires sales_reserve_inventory. Return 403 with message if missing.

- [ ] **A32-PE-06** — Reserve & Release API Endpoints `3h`
  - Schema: `demand` | FSD: §9.4 | Depends: A32-PE-03, A32-PE-05 | Tests: T-C4-02, T-C4-05, T-C4-10
  - POST /api/sale-orders/{id}/lines/{lineId}/reserve (single line). POST /api/sale-orders/{id}/lines/{lineId}/release (single line). POST /api/sale-orders/{id}/reserve-all (batch — all OPEN lines with available stock).

### Track E2 — Delivery Indicators & Reservation UI

- [ ] **A32-PE-07** — SO Lines Table — Delivery Indicator Column `3h`
  - Schema: — (UI) | FSD: §10.6, §6.3 | Depends: A32-PE-02 | Tests: T-C4-01, T-C4-08, T-C4-09
  - New column "Ind" in SO Lines table: colored circle (🟢🔵🟡🔴⚪) with tooltip explaining status. Color mapping from DeliveryIndicator DTO property. Real-time update after reserve/release/fulfillment.

- [ ] **A32-PE-08** — SO Lines Table — Reserved Qty Column & Reserve/Release Buttons `4h`
  - Schema: — (UI) | FSD: §10.6, BR-C4-08 | Depends: A32-PE-06 | Tests: T-C4-02–T-C4-05
  - New "Rsvd" column showing reserved_qty. [Reserve] button per line (OPEN/partially reserved). [Release] button per line (reserved_qty > 0). Buttons DISABLED (not hidden) when user lacks permission — tooltip: "You do not have permission to reserve inventory. Contact your administrator." [Reserve All Open Lines] bulk action at table footer.

- [ ] **A32-PE-09** — Partial Reservation Dialog `2h`
  - Schema: — (UI) | FSD: §6.4 | Depends: A32-PE-08 | Tests: T-C4-03
  - Modal: "Available: {X}, Required: {Y}. Reserve {X} units?" Confirm reserves partial. Cancel aborts. Shows variant name, warehouse, current stock.

- [ ] **A32-PE-10** — Reservation TTL & Expiry Integration `2h`
  - Schema: `demand` | FSD: §6.6, BR-C4-06 | Depends: A32-PE-03 | Tests: T-C4-07
  - Verify existing ReservationExpiryJob handles SALES_ORDER source_type. On expiry: decrement SOLine.reserved_qty, update indicator. No new job needed — extend existing if source_type filter is missing.

### Phase E Completion

- [ ] **A32-PE-11** — Phase E Unit & Integration Tests `4h`
  - Schema: `demand, inventory` | FSD: §12 | Depends: A32-PE-01–10 | Tests: T-C4-01–T-C4-10
  - Reserve/release flows, permission enforcement, partial reservation, auto-release on cancel, TTL expiry, indicator computation, bulk reserve

---

## Phase F — Testing & Integration (All Changes)

**Priority:** P0 — Critical Path | **Estimate:** 24 Hours (3 Days) | **FSD:** §12

> **⚠️ Sequenced:** Phase F depends on all previous phases.

### Track F1 — End-to-End Integration Tests

- [ ] **A32-PF-01** — E2E: Full Chain — Inquiry → Quotation → Sale Order `5h`
  - Schema: `demand` | FSD: §2.1, §12 | Depends: A32-PB-12, A32-PC-15, A32-PD-08 | Tests: T-C1-11, T-C2-02, T-C2-12
  - Create inquiry → evaluate lines → create quotation from inquiry → add alternatives → send → record customer acceptance → convert to Sale Order. Verify all FKs chained: SO.source_quotation_id, SO.source_inquiry_id, quotation.source_inquiry_id.

- [ ] **A32-PF-02** — E2E: Direct Sale Order with Customer PO & Reservation `4h`
  - Schema: `demand, inventory` | FSD: §12 | Depends: A32-PD-08, A32-PE-11 | Tests: T-C3-01, T-C4-02, T-C4-06
  - Create SO directly (source_type=MANUAL), attach customer PO PDF, reserve inventory per line, verify indicators, cancel SO → verify auto-release.

- [ ] **A32-PF-03** — E2E: Quotation Rejection & Expiry Paths `3h`
  - Schema: `demand` | FSD: §12 | Depends: A32-PC-15 | Tests: T-C2-10, T-C2-11
  - Customer rejects all lines → quotation REJECTED. Quotation past valid_to → Hangfire sets EXPIRED. Verify no SO created from rejected/expired quotation.

- [ ] **A32-PF-04** — E2E: RBAC — Permission Matrix Verification `3h`
  - Schema: `auth, demand, inventory` | FSD: §6.5, §13.4 | Depends: A32-PE-11 | Tests: T-C4-04
  - Test every endpoint with: no permission (403), view-only (200 GET, 403 POST), full permission (200). Verify button disabled states match permission claims. Test reserve/release with/without claims.

- [ ] **A32-PF-05** — E2E: Multi-Tenant Isolation Verification `3h`
  - Schema: all | FSD: §1.1 | Depends: all | Tests: —
  - Create inquiries/quotations/orders in Tenant A and Tenant B. Verify HasQueryFilter: Tenant A cannot see/access Tenant B data. Verify rejection reason codes are per-org. Verify numbering sequences are per-org.

- [ ] **A32-PF-06** — Regression Tests — Existing SO & Reservation Flows `3h`
  - Schema: `demand, inventory` | FSD: — | Depends: A32-PE-11, A32-PD-08 | Tests: —
  - Existing Sale Order creation (no source linking) still works. Existing IStockReservationService consumers (PRODUCTION_ORDER, MIR, DELIVERY) unaffected. Existing SaleOrderConfig.reservation_ttl_hours applies to new SALES_ORDER reservations.

### Phase F Completion

- [ ] **A32-PF-07** — Final Code Review & Documentation `3h`
  - Schema: all | FSD: all | Depends: A32-PF-01–06 | Tests: —
  - Code review: all new services, entities, migrations. Swagger/OpenAPI annotations on all new endpoints. README updates for sales pre-order pipeline.

---

## Parallelization Map

```
Phase A (2 days) ─────────────────┐
                                  ├──→ Phase B (5 days) ──┐
                                  │                       ├──→ Phase D (3 days) ──→ Phase E (4 days) ──┐
                                  ├──→ Phase C (6 days) ──┘                                           │
                                  │                                                                    │
                                  └────────────────────────────────────────────────────────────────────→ Phase F (3 days)

Parallel agents:
  Agent 1: Phase A → Phase B → Phase D (backend) → Phase E (backend)
  Agent 2: Phase A → Phase C → Phase D (UI) → Phase E (UI)
  Agent 3: Phase F (after B+C+D+E complete)

Calendar time with 2 agents: 14 days (vs 23 sequential)
```

---

## Task Summary

| Phase | Tasks | Hours | Days | Parallel Group |
|---|---|---|---|---|
| A — Schema & Lookups | 6 | 16 | 2 | Foundation |
| B — Sale Inquiry | 12 | 42 | 5 | Agent 1 |
| C — Sale Quotation | 15 | 50 | 6 | Agent 2 |
| D — Sale Order Extensions | 8 | 25 | 3 | Converge |
| E — Delivery Indicators & Reservation | 11 | 32 | 4 | Converge |
| F — Testing & Integration | 7 | 24 | 3 | Final |
| **TOTAL** | **59** | **189** | **23** | |

---

*End of Task Register — Addendum 32*
