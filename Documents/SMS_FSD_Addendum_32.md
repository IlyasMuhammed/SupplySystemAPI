# SUPPLY MANAGEMENT SYSTEM — Functional Specification Document

## FSD Addendum 32 — Sales Pre-Order Pipeline (Inquiry → Quotation → Sale Order)

| Property | Value |
|---|---|
| Document ID | SMS-FSD-ADD-032 |
| Version | 1.0 |
| Date | 2026-10-03 |
| Status | Draft |
| Author | System Architect |
| Classification | Internal — Development |
| Supersedes | None — new entities extending demand schema |
| Architecture | .NET 8 / EF Core 8 / SQL Server 2022 / React 18 |
| Change Items | 5 new features & extensions |
| Impact | 7 migrations, 6 new entities, 2 modified entities, 1 new lookup, 5 new UI panels, 3 state machines |
| Deferred to Addendum 33 | Fulfillment orchestration, delivery notes, shipment tracking, invoice generation |

---

## Table of Contents

1. [Purpose & Scope](#1-purpose--scope)
2. [Pre-Order Pipeline Overview](#2-pre-order-pipeline-overview)
3. [C1 — Sale Inquiry](#3-c1--sale-inquiry)
4. [C2 — Sale Quotation](#4-c2--sale-quotation)
5. [C3 — Sale Order Extensions (Source Linking & Customer PO Capture)](#5-c3--sale-order-extensions-source-linking--customer-po-capture)
6. [C4 — Sale Order Line Delivery Indicators & Inventory Reservation](#6-c4--sale-order-line-delivery-indicators--inventory-reservation)
7. [C5 — Rejection Reason Lookup](#7-c5--rejection-reason-lookup)
8. [Database Migrations](#8-database-migrations)
9. [API Changes](#9-api-changes)
10. [UI Wireframes & Specifications](#10-ui-wireframes--specifications)
11. [Business Rules](#11-business-rules)
12. [Test Scenarios](#12-test-scenarios)
13. [Development Phases](#13-development-phases)

---

## 1. Purpose & Scope

**This addendum introduces the Sales Pre-Order Pipeline — a three-document chain (Sale Inquiry → Sale Quotation → Sale Order) that covers the seller-side sales process from initial customer inquiry through to a confirmed order with inventory reservation. Both Sale Inquiry and Sale Quotation are OPTIONAL steps; a Sale Order may be created directly without either predecessor.**

This addendum does NOT cover fulfillment, delivery notes, shipment tracking, or invoicing — those are deferred to Addendum 33.

### 1.1 Design Principles

- **Optional Chain, Maximum Traceability** — Inquiry and Quotation are optional steps, but when used the full chain is linked (Inquiry → Quotation → Sale Order) with forward and backward FK references for end-to-end traceability.
- **Line-Level Granularity** — Every evaluation, rejection, alternative, and customer response is tracked per line item, not per document. This enables fine-grained analytics on why products are rejected and which alternatives customers prefer.
- **Analytics-Ready Rejection Tracking** — Inquiry line rejections are captured with a formal RejectionReason lookup. This feeds future dashboards answering: "How many inquiries did we decline per reason, per product, per quarter?"
- **Separation of Concerns** — Sale Quotation (seller-initiated pricing proposal) is entirely separate from the existing RFQ implementation (buyer-initiated request for pricing). They are different document types with different schemas and workflows.
- **RBAC-Controlled Reservation** — Inventory reservation on Sale Order lines requires explicit permission. The reserve action is disabled in the UI unless the logged-in user holds the `sales_reserve_inventory` claim. Releasing reserved stock follows the same permission model.
- **Multi-Tenant by Default** — Every new entity carries `org_id` with EF Core `HasQueryFilter`. No exception.

### 1.2 Scope Summary

| Change ID | Title | Type | Impact |
|---|---|---|---|
| C1 | Sale Inquiry | New Entity + UI | 2 new tables (SaleInquiries, SaleInquiryLines), 1 state machine, 1 new UI panel |
| C2 | Sale Quotation | New Entity + UI | 2 new tables (SaleQuotations, SaleQuotationLines), 1 state machine, 1 new UI panel |
| C3 | Sale Order Extensions | Schema Extension | Add source linking + customer PO capture columns to existing SaleOrders |
| C4 | SO Line Delivery Indicators & Reservation | Feature Extension | Line-level status indicators (green/yellow/red) + RBAC reservation column on existing SaleOrderLines |
| C5 | Rejection Reason Lookup | New Lookup | 1 new table (RejectionReasons) with seed data for analytics |

### 1.3 Relationship to Existing Entities

| Existing Entity | Schema | Addendum 32 Interaction |
|---|---|---|
| demand.SaleOrders | demand | EXTENDED — add source_quotation_id, source_inquiry_id, source_type, customer_po_reference, customer_po_date, customer_po_attachment_id |
| demand.SaleOrderLines | demand | EXTENDED — add reserved_qty column; delivery indicator is computed, not stored |
| demand.SaleOrderConfig | demand | UNCHANGED — reservation_ttl_hours already exists and applies |
| suppliers.BusinessPartners | suppliers | REFERENCED — SaleInquiry/Quotation link to customer via partner_id (partner_type includes CUSTOMER) |
| inventory.StockReservations | inventory | USED — IStockReservationService.Reserve/Release with source_type=SALES_ORDER |
| lookups.Products | lookups | REFERENCED — Inquiry/Quotation lines reference product variants |

---

## 2. Pre-Order Pipeline Overview

### 2.1 Three-Document Chain

```
                    ┌──────────────┐
                    │ Sale Inquiry │  OPTIONAL
                    │  (INQ-xxxx)  │  Line-level evaluation
                    └──────┬───────┘  & rejection tracking
                           │ 0..1
                           ▼
                    ┌──────────────────┐
                    │  Sale Quotation  │  OPTIONAL
                    │   (SQ-xxxx)      │  Pricing, alternatives,
                    └──────┬───────────┘  customer response
                           │ 0..1
                           ▼
                    ┌──────────────────┐
                    │   Sale Order     │  REQUIRED
                    │   (SO-xxxx)      │  Existing entity, extended
                    └──────────────────┘  Source linking + reservation

  ──── Direct Creation Paths ────────────────────────────

  Path 1: Inquiry → Quotation → Sale Order  (full chain)
  Path 2: Quotation → Sale Order            (skip inquiry)
  Path 3: Sale Order                         (direct entry)
```

### 2.2 Document Numbering

| Document | Pattern | Example | Auto-Generated |
|---|---|---|---|
| Sale Inquiry | INQ-{YYYY}-{NNNN} | INQ-2026-0001 | Yes — per org, per year, sequential |
| Sale Quotation | SQ-{YYYY}-{NNNN} | SQ-2026-0001 | Yes — per org, per year, sequential |
| Sale Order | SO-{NNNN} | SO-0001 | Yes — existing pattern unchanged |

---

## 3. C1 — Sale Inquiry

### 3.1 Overview

A Sale Inquiry captures an inbound customer request — received by email, phone, portal, or any other channel — and allows the sales team to evaluate each requested line item individually. The inquiry is the only place where line-level evaluation (CAN_SUPPLY, CANNOT_SUPPLY, etc.) and rejection reasons are captured, providing analytics data for future product and capacity decisions.

> **📝 NOTE:** Sale Inquiry is an OPTIONAL step. Organizations that do not need inquiry tracking can skip directly to Sale Quotation or Sale Order creation.

### 3.2 Schema — SaleInquiries

**New Table:** `demand.SaleInquiries`

| Column | Type | Nullable | Default | Description |
|---|---|---|---|---|
| sale_inquiry_id | BIGINT IDENTITY | NOT NULL | PK | Primary key |
| org_id | INT | NOT NULL | FK → tenants | Tenant discriminator — EF Core HasQueryFilter |
| inquiry_number | NVARCHAR(20) | NOT NULL | Auto | Pattern: INQ-{YYYY}-{NNNN}, unique per org |
| customer_id | BIGINT | NOT NULL | FK → BusinessPartners | Customer who sent the inquiry |
| customer_reference | NVARCHAR(50) | NULL | | Customer's own reference number (their RFQ number, email subject, etc.) |
| customer_reference_date | DATE | NULL | | Date on the customer's reference document |
| status | NVARCHAR(20) | NOT NULL | 'RECEIVED' | State machine: RECEIVED → UNDER_REVIEW → REVIEW_COMPLETE → QUOTED → DECLINED |
| received_date | DATE | NOT NULL | GETDATE() | Date inquiry was received |
| response_deadline | DATE | NULL | | Customer-requested response date |
| assigned_to_user_id | BIGINT | NULL | FK → auth.Users | Sales rep assigned to evaluate |
| notes | NVARCHAR(2000) | NULL | | Internal notes |
| created_by | BIGINT | NOT NULL | FK → auth.Users | User who entered the inquiry |
| created_at | DATETIME2 | NOT NULL | SYSUTCDATETIME() | |
| updated_at | DATETIME2 | NOT NULL | SYSUTCDATETIME() | |
| trace_id | UNIQUEIDENTIFIER | NOT NULL | NEWID() | Correlation ID |

**Indexes:**

| Index | Columns | Type |
|---|---|---|
| UQ_SaleInquiries_OrgNumber | org_id, inquiry_number | Unique |
| IX_SaleInquiries_Customer | customer_id | Non-unique |
| IX_SaleInquiries_Status | org_id, status | Non-unique, filtered |
| IX_SaleInquiries_AssignedTo | assigned_to_user_id | Non-unique, filtered (WHERE NOT NULL) |

### 3.3 Schema — SaleInquiryLines

**New Table:** `demand.SaleInquiryLines`

| Column | Type | Nullable | Default | Description |
|---|---|---|---|---|
| inquiry_line_id | BIGINT IDENTITY | NOT NULL | PK | Primary key |
| sale_inquiry_id | BIGINT | NOT NULL | FK → SaleInquiries | Parent inquiry |
| org_id | INT | NOT NULL | FK → tenants | Tenant discriminator |
| line_number | INT | NOT NULL | Auto | Sequential within inquiry |
| product_id | BIGINT | NULL | FK → Products | Requested product (NULL when customer describes a non-catalog item) |
| variant_id | BIGINT | NULL | FK → ProductVariants | Specific variant if identified |
| product_description | NVARCHAR(500) | NOT NULL | | Free text — customer's description (always captured, even when product_id is set) |
| requested_quantity | DECIMAL(18,4) | NOT NULL | | Quantity the customer is requesting |
| requested_uom_id | INT | NULL | FK → UnitOfMeasures | Customer's requested unit of measure |
| requested_delivery_date | DATE | NULL | | Customer's desired delivery date |
| line_status | NVARCHAR(20) | NOT NULL | 'PENDING' | Evaluation status: PENDING, CAN_SUPPLY, PARTIAL, CANNOT_SUPPLY, UNDER_REVIEW |
| can_supply_quantity | DECIMAL(18,4) | NULL | | Quantity the org can supply (when PARTIAL) |
| estimated_delivery_date | DATE | NULL | | Org's estimated delivery date for this line |
| rejection_reason_id | INT | NULL | FK → RejectionReasons | Why the line was rejected (when CANNOT_SUPPLY) |
| rejection_notes | NVARCHAR(500) | NULL | | Additional rejection explanation |
| alternative_product_id | BIGINT | NULL | FK → Products | Suggested alternative product (when original rejected) |
| alternative_variant_id | BIGINT | NULL | FK → ProductVariants | Alternative variant if applicable |
| alternative_notes | NVARCHAR(500) | NULL | | Explanation of alternative |
| requires_procurement | BIT | NOT NULL | 0 | True if supply requires purchasing from vendor |
| procurement_lead_days | INT | NULL | | Estimated procurement lead time |
| reviewed_by_user_id | BIGINT | NULL | FK → auth.Users | User who evaluated this line |
| reviewed_at | DATETIME2 | NULL | | When the line was evaluated |
| notes | NVARCHAR(1000) | NULL | | Internal line-level notes |
| created_at | DATETIME2 | NOT NULL | SYSUTCDATETIME() | |
| updated_at | DATETIME2 | NOT NULL | SYSUTCDATETIME() | |

**Indexes:**

| Index | Columns | Type |
|---|---|---|
| IX_SaleInquiryLines_InquiryId | sale_inquiry_id | Clustered-adjacent |
| UQ_SaleInquiryLines_InqLine | sale_inquiry_id, line_number | Unique |
| IX_SaleInquiryLines_Product | product_id | Non-unique, filtered (WHERE NOT NULL) |
| IX_SaleInquiryLines_Rejection | rejection_reason_id | Non-unique, filtered (WHERE NOT NULL) |

### 3.4 Sale Inquiry State Machine

```
  ┌──────────┐
  │ RECEIVED │ ← Initial state on creation
  └────┬─────┘
       │ assign_to_user / begin_review
       ▼
  ┌──────────────┐
  │ UNDER_REVIEW │ ← Sales team evaluating lines
  └────┬────┬────┘
       │    │
       │    └──────────────────────┐
       │  all lines evaluated     │  org declines entire inquiry
       ▼                          ▼
  ┌─────────────────┐     ┌──────────┐
  │ REVIEW_COMPLETE │     │ DECLINED │ (terminal)
  └───────┬─────────┘     └──────────┘
          │ create quotation
          ▼
     ┌─────────┐
     │ QUOTED  │ (terminal — inquiry's work is done)
     └─────────┘
```

**Transition Rules:**

| From | To | Trigger | Condition |
|---|---|---|---|
| RECEIVED | UNDER_REVIEW | User begins evaluation | At least one line exists |
| UNDER_REVIEW | REVIEW_COMPLETE | User marks review complete | All lines have a status ≠ PENDING |
| UNDER_REVIEW | DECLINED | User declines entire inquiry | — |
| REVIEW_COMPLETE | QUOTED | System — when quotation is created from this inquiry | source_inquiry_id set on quotation |
| REVIEW_COMPLETE | DECLINED | User declines after review | — |

### 3.5 Line-Level Evaluation Statuses

| Status | Meaning | Required Fields |
|---|---|---|
| PENDING | Not yet evaluated | — |
| CAN_SUPPLY | Organization can deliver the requested qty by the date | estimated_delivery_date |
| PARTIAL | Organization can supply a partial quantity | can_supply_quantity, estimated_delivery_date |
| CANNOT_SUPPLY | Organization cannot supply this item | rejection_reason_id |
| UNDER_REVIEW | Evaluation in progress — awaiting internal check (e.g., procurement lead time) | — |

### 3.6 Inquiry Attachments

**New Table:** `demand.SaleInquiryAttachments`

| Column | Type | Nullable | Description |
|---|---|---|---|
| attachment_id | BIGINT IDENTITY | NOT NULL | PK |
| sale_inquiry_id | BIGINT | NOT NULL | FK → SaleInquiries |
| org_id | INT | NOT NULL | Tenant discriminator |
| file_name | NVARCHAR(255) | NOT NULL | Original file name |
| file_path | NVARCHAR(500) | NOT NULL | Storage path (Azure Blob / local) |
| file_size_bytes | BIGINT | NOT NULL | |
| content_type | NVARCHAR(100) | NOT NULL | MIME type |
| uploaded_by | BIGINT | NOT NULL | FK → auth.Users |
| uploaded_at | DATETIME2 | NOT NULL | SYSUTCDATETIME() |

> **Use Case:** Customer sends RFQ as PDF via email. Sales rep creates an inquiry, enters the line items, and attaches the original PDF for reference.

---

## 4. C2 — Sale Quotation

### 4.1 Overview

A Sale Quotation is the organization's formal pricing proposal to a customer. It may originate from a reviewed Sale Inquiry or be created independently. The quotation supports:

- **Line-level rejection with alternatives** — a line can be marked as rejected with a reason, and one or more alternative products offered in its place
- **Per-line customer response tracking** — each line tracks whether the customer accepted, rejected, or countered the offer
- **Validity period** — quotation carries valid_from/valid_to dates
- **Conversion to Sale Order** — accepted lines flow into a Sale Order

> **📝 NOTE:** Sale Quotation is SEPARATE from the existing RFQ (Request for Quotation) implementation in the procurement module. RFQ is buyer-side (sent to suppliers); Sale Quotation is seller-side (sent to customers).

### 4.2 Schema — SaleQuotations

**New Table:** `demand.SaleQuotations`

| Column | Type | Nullable | Default | Description |
|---|---|---|---|---|
| sale_quotation_id | BIGINT IDENTITY | NOT NULL | PK | Primary key |
| org_id | INT | NOT NULL | FK → tenants | Tenant discriminator |
| quotation_number | NVARCHAR(20) | NOT NULL | Auto | Pattern: SQ-{YYYY}-{NNNN}, unique per org |
| customer_id | BIGINT | NOT NULL | FK → BusinessPartners | Customer receiving the quotation |
| customer_reference | NVARCHAR(50) | NULL | | Customer's reference (their RFQ number, inquiry ref, etc.) |
| customer_reference_date | DATE | NULL | | Date on customer's reference document |
| source_inquiry_id | BIGINT | NULL | FK → SaleInquiries | Originating inquiry (NULL if created independently) |
| currency_code | NVARCHAR(3) | NOT NULL | FK → Currencies | Quotation currency |
| valid_from | DATE | NOT NULL | GETDATE() | Quotation validity start |
| valid_to | DATE | NOT NULL | | Quotation validity end |
| status | NVARCHAR(20) | NOT NULL | 'DRAFT' | State machine: DRAFT → SENT → ACCEPTED → REJECTED → EXPIRED → CONVERTED |
| payment_terms | NVARCHAR(200) | NULL | | Payment terms text |
| delivery_terms | NVARCHAR(200) | NULL | | Delivery/shipping terms text |
| subtotal | DECIMAL(18,4) | NOT NULL | 0 | Sum of line totals (excl. tax) |
| tax_amount | DECIMAL(18,4) | NOT NULL | 0 | Total tax |
| discount_amount | DECIMAL(18,4) | NOT NULL | 0 | Total discount |
| grand_total | DECIMAL(18,4) | NOT NULL | 0 | Final total |
| notes | NVARCHAR(2000) | NULL | | Terms, conditions, or notes printed on quotation |
| internal_notes | NVARCHAR(2000) | NULL | | Internal notes (not sent to customer) |
| sent_at | DATETIME2 | NULL | | When quotation was sent to customer |
| sent_by_user_id | BIGINT | NULL | FK → auth.Users | Who sent it |
| created_by | BIGINT | NOT NULL | FK → auth.Users | |
| created_at | DATETIME2 | NOT NULL | SYSUTCDATETIME() | |
| updated_at | DATETIME2 | NOT NULL | SYSUTCDATETIME() | |
| trace_id | UNIQUEIDENTIFIER | NOT NULL | NEWID() | |

**Indexes:**

| Index | Columns | Type |
|---|---|---|
| UQ_SaleQuotations_OrgNumber | org_id, quotation_number | Unique |
| IX_SaleQuotations_Customer | customer_id | Non-unique |
| IX_SaleQuotations_Status | org_id, status | Non-unique |
| IX_SaleQuotations_SourceInquiry | source_inquiry_id | Non-unique, filtered (WHERE NOT NULL) |
| IX_SaleQuotations_ValidTo | org_id, valid_to | Non-unique (for expiry batch job) |

### 4.3 Schema — SaleQuotationLines

**New Table:** `demand.SaleQuotationLines`

| Column | Type | Nullable | Default | Description |
|---|---|---|---|---|
| quotation_line_id | BIGINT IDENTITY | NOT NULL | PK | Primary key |
| sale_quotation_id | BIGINT | NOT NULL | FK → SaleQuotations | Parent quotation |
| org_id | INT | NOT NULL | FK → tenants | Tenant discriminator |
| line_number | INT | NOT NULL | Auto | Sequential within quotation |
| source_inquiry_line_id | BIGINT | NULL | FK → SaleInquiryLines | Originating inquiry line (NULL if not from inquiry) |
| variant_id | BIGINT | NOT NULL | FK → ProductVariants | Product variant being quoted |
| product_description | NVARCHAR(500) | NOT NULL | | Product description (snapshot at quotation time) |
| quantity | DECIMAL(18,4) | NOT NULL | | Offered quantity |
| uom_id | INT | NOT NULL | FK → UnitOfMeasures | Unit of measure |
| unit_price | DECIMAL(18,4) | NOT NULL | | Price per unit |
| discount_percent | DECIMAL(5,2) | NOT NULL | 0 | Line discount % |
| tax_percent | DECIMAL(5,2) | NOT NULL | 0 | Tax % |
| tax_amount | DECIMAL(18,4) | NOT NULL | 0 | Computed tax amount |
| line_total | DECIMAL(18,4) | NOT NULL | 0 | (qty × unit_price × (1 - discount%)) + tax |
| promised_delivery_date | DATE | NULL | | Promised delivery for this line |
| line_type | NVARCHAR(15) | NOT NULL | 'NORMAL' | NORMAL, ALTERNATIVE, or REJECTED |
| rejection_reason_id | INT | NULL | FK → RejectionReasons | Why this line is rejected (when line_type = REJECTED) |
| rejection_notes | NVARCHAR(500) | NULL | | Rejection explanation |
| alternative_for_line_id | BIGINT | NULL | FK → self (quotation_line_id) | Points to the REJECTED line this is an alternative for |
| alternative_notes | NVARCHAR(500) | NULL | | Explanation of the alternative |
| customer_response | NVARCHAR(15) | NOT NULL | 'PENDING' | PENDING, ACCEPTED, REJECTED, COUNTER |
| customer_response_date | DATE | NULL | | When customer responded to this line |
| customer_response_notes | NVARCHAR(500) | NULL | | Customer's notes or feedback |
| customer_counter_price | DECIMAL(18,4) | NULL | | Customer's counter-offer price (when response = COUNTER) |
| notes | NVARCHAR(1000) | NULL | | Internal line notes |
| created_at | DATETIME2 | NOT NULL | SYSUTCDATETIME() | |
| updated_at | DATETIME2 | NOT NULL | SYSUTCDATETIME() | |

**Indexes:**

| Index | Columns | Type |
|---|---|---|
| IX_SaleQuotationLines_QuotationId | sale_quotation_id | Clustered-adjacent |
| UQ_SaleQuotationLines_QuotLine | sale_quotation_id, line_number | Unique |
| IX_SaleQuotationLines_Variant | variant_id | Non-unique |
| IX_SaleQuotationLines_AltFor | alternative_for_line_id | Non-unique, filtered (WHERE NOT NULL) |
| IX_SaleQuotationLines_CustResponse | sale_quotation_id, customer_response | Non-unique |

### 4.4 Sale Quotation State Machine

```
  ┌───────┐
  │ DRAFT │ ← Initial state — quotation being prepared
  └───┬───┘
      │ send_to_customer
      ▼
  ┌────────┐
  │  SENT  │ ← Awaiting customer response
  └──┬──┬──┘
     │  │
     │  └───────────────────────────────────────┐
     │  customer responds                       │  no response by valid_to
     ▼                                          ▼
  ┌──────────┐    ┌──────────┐          ┌─────────┐
  │ ACCEPTED │    │ REJECTED │          │ EXPIRED │
  └────┬─────┘    └──────────┘          └─────────┘
       │ create sale order                (terminal)
       ▼
  ┌───────────┐
  │ CONVERTED │ (terminal — sale order created)
  └───────────┘
```

**Transition Rules:**

| From | To | Trigger | Condition |
|---|---|---|---|
| DRAFT | SENT | User sends quotation | At least one NORMAL or ALTERNATIVE line exists |
| SENT | ACCEPTED | User records customer acceptance | At least one line has customer_response = ACCEPTED |
| SENT | REJECTED | User records customer rejection | All lines have customer_response = REJECTED |
| SENT | EXPIRED | System batch job (Hangfire) | SYSUTCDATETIME() > valid_to AND status = SENT |
| ACCEPTED | CONVERTED | System — when Sale Order is created from this quotation | source_quotation_id set on Sale Order |
| DRAFT | DRAFT | User edits quotation | Lines can be added/modified/removed while draft |

> **⚠️ CRITICAL:** A quotation in SENT status cannot be edited. To modify a sent quotation, the user must create a new quotation (optionally copying lines from the original). The original can be manually set to REJECTED or allowed to expire.

### 4.5 Line Type & Alternative Flow

**Three line types exist on a quotation:**

| line_type | Description | alternative_for_line_id |
|---|---|---|
| NORMAL | Standard quoted item | NULL |
| REJECTED | Item the org cannot or will not supply | NULL |
| ALTERNATIVE | Offered replacement for a REJECTED line | Points to the rejected line's quotation_line_id |

**Example Flow:**

```
Line 1: Product A — 100 units @ $50      line_type=NORMAL
Line 2: Product B — 200 units            line_type=REJECTED  (reason: OOS)
Line 3: Product B2 — 200 units @ $48     line_type=ALTERNATIVE  alternative_for_line_id=Line 2
Line 4: Product B3 — 150 units @ $45     line_type=ALTERNATIVE  alternative_for_line_id=Line 2
Line 5: Product C — 50 units @ $120      line_type=NORMAL
```

When the customer responds:
- Line 1: customer_response = ACCEPTED
- Line 2: (skipped — already REJECTED by seller)
- Line 3: customer_response = ACCEPTED (customer chose this alternative)
- Line 4: customer_response = REJECTED (customer did not want this one)
- Line 5: customer_response = COUNTER, customer_counter_price = $110

**Conversion to Sale Order:** Only lines with `customer_response = ACCEPTED` flow into the Sale Order. COUNTER lines require the seller to accept the counter-price (by updating the line's unit_price and setting customer_response = ACCEPTED) before conversion.

### 4.6 Quotation Attachments

**New Table:** `demand.SaleQuotationAttachments`

| Column | Type | Nullable | Description |
|---|---|---|---|
| attachment_id | BIGINT IDENTITY | NOT NULL | PK |
| sale_quotation_id | BIGINT | NOT NULL | FK → SaleQuotations |
| org_id | INT | NOT NULL | Tenant discriminator |
| file_name | NVARCHAR(255) | NOT NULL | Original file name |
| file_path | NVARCHAR(500) | NOT NULL | Storage path |
| file_size_bytes | BIGINT | NOT NULL | |
| content_type | NVARCHAR(100) | NOT NULL | MIME type |
| uploaded_by | BIGINT | NOT NULL | FK → auth.Users |
| uploaded_at | DATETIME2 | NOT NULL | SYSUTCDATETIME() |

---

## 5. C3 — Sale Order Extensions (Source Linking & Customer PO Capture)

### 5.1 Overview

The existing `demand.SaleOrders` table is extended with columns that:

1. **Link back to the source** — which quotation and/or inquiry this Sale Order originated from
2. **Capture the customer's Purchase Order** — the customer's PO number, date, and attached PO document
3. **Record how the order was created** — manual entry, from quotation, portal, or inter-tenant

### 5.2 Schema Change

**Modify:** `demand.SaleOrders`

#### SaleOrders — New Columns (ADD)

| Column | Type | Nullable | Default | Description |
|---|---|---|---|---|
| source_quotation_id | BIGINT | NULL | FK → SaleQuotations | Originating quotation (NULL for direct orders) |
| source_inquiry_id | BIGINT | NULL | FK → SaleInquiries | Originating inquiry (NULL when no inquiry) |
| source_type | NVARCHAR(20) | NOT NULL | 'MANUAL' | MANUAL, FROM_QUOTATION, PORTAL, INTER_TENANT |
| customer_po_reference | NVARCHAR(50) | NULL | | Customer's Purchase Order number |
| customer_po_date | DATE | NULL | | Date on customer's PO |
| customer_po_attachment_id | BIGINT | NULL | FK → demand.SaleOrderAttachments | Attached customer PO document |

**New Indexes:**

| Index | Columns | Type |
|---|---|---|
| IX_SaleOrders_SourceQuotation | source_quotation_id | Non-unique, filtered (WHERE NOT NULL) |
| IX_SaleOrders_SourceInquiry | source_inquiry_id | Non-unique, filtered (WHERE NOT NULL) |
| IX_SaleOrders_CustomerPO | org_id, customer_po_reference | Non-unique, filtered (WHERE NOT NULL) |

### 5.3 Sale Order Attachments

**New Table:** `demand.SaleOrderAttachments`

| Column | Type | Nullable | Description |
|---|---|---|---|
| attachment_id | BIGINT IDENTITY | NOT NULL | PK |
| sale_order_id | BIGINT | NOT NULL | FK → SaleOrders |
| org_id | INT | NOT NULL | Tenant discriminator |
| file_name | NVARCHAR(255) | NOT NULL | Original file name |
| file_path | NVARCHAR(500) | NOT NULL | Storage path |
| file_size_bytes | BIGINT | NOT NULL | |
| content_type | NVARCHAR(100) | NOT NULL | MIME type |
| attachment_type | NVARCHAR(20) | NOT NULL | 'CUSTOMER_PO' or 'GENERAL' |
| uploaded_by | BIGINT | NOT NULL | FK → auth.Users |
| uploaded_at | DATETIME2 | NOT NULL | SYSUTCDATETIME() |

### 5.4 Source Type Enum

| Value | Description | Typical Fields Populated |
|---|---|---|
| MANUAL | Entered directly by sales rep | customer_po_reference (optional) |
| FROM_QUOTATION | Converted from an accepted Sale Quotation | source_quotation_id, source_inquiry_id (if chain), customer_po_reference |
| PORTAL | Customer created via self-service portal (future) | customer_po_reference |
| INTER_TENANT | Created from another tenant's Purchase Order (future) | customer_po_reference |

### 5.5 Conversion Logic — Quotation to Sale Order

When creating a Sale Order from a Sale Quotation:

```csharp
// Pseudocode — Quotation → Sale Order conversion
var quotation = await _quotationRepo.GetWithLinesAsync(quotationId);

// Only lines the customer accepted are converted
var acceptedLines = quotation.Lines
    .Where(l => l.CustomerResponse == "ACCEPTED")
    .ToList();

if (!acceptedLines.Any())
    throw new BusinessRuleException("No accepted lines to convert");

var saleOrder = new SaleOrder
{
    OrgId = quotation.OrgId,
    PartnerId = quotation.CustomerId,
    SourceQuotationId = quotation.SaleQuotationId,
    SourceInquiryId = quotation.SourceInquiryId,  // chain through
    SourceType = "FROM_QUOTATION",
    CurrencyCode = quotation.CurrencyCode,
    OrderDate = DateOnly.FromDateTime(DateTime.UtcNow),
    Status = "DRAFT",
    CustomerPoReference = dto.CustomerPoReference,  // captured at conversion
    CustomerPoDate = dto.CustomerPoDate
};

foreach (var ql in acceptedLines)
{
    saleOrder.Lines.Add(new SaleOrderLine
    {
        VariantId = ql.VariantId,
        Quantity = ql.Quantity,
        UnitPrice = ql.UnitPrice,
        DiscountPercent = ql.DiscountPercent,
        TaxPercent = ql.TaxPercent,
        Status = "OPEN"
    });
}

await _saleOrderRepo.CreateAsync(saleOrder);

// Transition quotation → CONVERTED
quotation.Status = "CONVERTED";
await _quotationRepo.UpdateAsync(quotation);
```

---

## 6. C4 — Sale Order Line Delivery Indicators & Inventory Reservation

### 6.1 Overview

Two enhancements to Sale Order lines:

1. **Delivery Progress Indicators** — a computed green/yellow/red visual indicator per line showing fulfillment status (similar to Odoo's delivery status tooltips)
2. **Inventory Reservation Column** — a per-line `reserved_qty` and an RBAC-controlled "Reserve" button that calls `IStockReservationService`

### 6.2 Schema Change — SaleOrderLines

**Modify:** `demand.SaleOrderLines`

#### SaleOrderLines — New Column (ADD)

| Column | Type | Nullable | Default | Description |
|---|---|---|---|---|
| reserved_qty | DECIMAL(18,4) | NOT NULL | 0 | Currently reserved quantity from inventory |

> **📝 NOTE:** The delivery indicator is NOT stored — it is computed at read time from `fulfilled_qty`, `reserved_qty`, and `quantity`.

### 6.3 Delivery Indicator Logic (Computed)

The delivery indicator is a computed property in the DTO, never persisted:

```csharp
// Pseudocode — SaleOrderLineDto.DeliveryIndicator
public string DeliveryIndicator => Status switch
{
    "CANCELLED" => "GREY",
    "FULFILLED" or "INVOICED" => "GREEN",
    _ => ComputeIndicator()
};

private string ComputeIndicator()
{
    if (FulfilledQty >= Quantity) return "GREEN";      // fully delivered
    if (FulfilledQty > 0) return "YELLOW";             // partially delivered
    if (ReservedQty >= Quantity) return "BLUE";         // fully reserved, not yet shipped
    if (ReservedQty > 0) return "YELLOW";              // partially reserved
    return "RED";                                       // nothing reserved or delivered
}
```

| Indicator | Color | Meaning |
|---|---|---|
| GREEN | 🟢 | Fully fulfilled — entire quantity delivered |
| BLUE | 🔵 | Fully reserved — inventory held, not yet shipped |
| YELLOW | 🟡 | Partially fulfilled or partially reserved |
| RED | 🔴 | Nothing reserved or fulfilled — action needed |
| GREY | ⚪ | Cancelled line |

### 6.4 Inventory Reservation Flow

**Reserve inventory for a Sale Order line:**

```
User clicks [Reserve] on SO line
  → System checks: user has 'sales_reserve_inventory' permission?
    → NO: button is disabled (greyed out), tooltip: "Insufficient permissions"
    → YES:
      → Check available_qty for variant in warehouse
        → available_qty >= remaining unreserved qty?
          → YES: call IStockReservationService.Reserve(
                    variantId, warehouseId, reserveQty,
                    sourceType = SALES_ORDER,
                    sourceId = saleOrderId)
                 Update SaleOrderLine.reserved_qty += reserveQty
                 Indicator may change: RED→BLUE or YELLOW→BLUE
          → NO: show dialog "Available: {X}, Required: {Y}. Reserve partial?"
                → User confirms partial → reserve available
                → User cancels → no action
```

**Release reserved inventory:**

```
User clicks [Release] on SO line (visible when reserved_qty > 0)
  → System checks: user has 'sales_reserve_inventory' permission?
    → YES:
      → IStockReservationService.Release(reservationId)
      → SaleOrderLine.reserved_qty -= releasedQty
      → stock_balance.reserved_qty decreased
      → stock_balance.available_qty increased
      → Indicator changes: BLUE→RED or YELLOW→RED
```

**Automatic release on Sale Order cancellation:**

```
When SaleOrder.Status → CANCELLED:
  → For each line with reserved_qty > 0:
    → IStockReservationService.Release(reservationId)
    → reserved_qty set to 0
    → Inventory returned to available pool
```

### 6.5 Reservation Permissions

**New Permission Claims:**

| Claim | Description | Default Roles |
|---|---|---|
| sales_reserve_inventory | Reserve inventory against a Sale Order line | SALES_MANAGER, WAREHOUSE_MANAGER |
| sales_release_reservation | Release existing reservation on a Sale Order line | SALES_MANAGER, WAREHOUSE_MANAGER |

**UI Behavior:**

| User Has Permission | Reserve Button | Release Button |
|---|---|---|
| Neither | Disabled (greyed) | Disabled (greyed) |
| sales_reserve_inventory only | Enabled | Disabled |
| sales_release_reservation only | Disabled | Enabled |
| Both | Enabled | Enabled |

> **⚠️ CRITICAL:** The Reserve/Release buttons are DISABLED in the UI when the user lacks the required permission. They are not hidden — hiding them would confuse users who expect to see them. The disabled state includes a tooltip explaining why: "You do not have permission to reserve inventory. Contact your administrator."

### 6.6 Reservation TTL

The existing `demand.SaleOrderConfig.reservation_ttl_hours` applies. When a reservation exceeds the TTL without the Sale Order advancing to fulfillment, the Hangfire `ReservationExpiryJob` releases it:

```csharp
// Existing behavior — no changes needed
// ReservationExpiryJob runs on schedule, checks:
//   reservation.created_at + config.reservation_ttl_hours < SYSUTCDATETIME()
//   AND reservation.status == ACTIVE
//   → release reservation, update SO line reserved_qty
```

---

## 7. C5 — Rejection Reason Lookup

### 7.1 Overview

A shared lookup table for rejection reasons used by both Sale Inquiry lines and Sale Quotation lines. The rejection reasons enable analytics: "Why are we declining customer requests?"

### 7.2 Schema — RejectionReasons

**New Table:** `lookups.RejectionReasons`

| Column | Type | Nullable | Default | Description |
|---|---|---|---|---|
| rejection_reason_id | INT IDENTITY | NOT NULL | PK | Primary key |
| org_id | INT | NOT NULL | FK → tenants | Tenant discriminator |
| code | NVARCHAR(10) | NOT NULL | | Short code (e.g., OOS, MOQ, DIS) |
| description | NVARCHAR(200) | NOT NULL | | Human-readable description |
| is_active | BIT | NOT NULL | 1 | Soft delete |
| display_order | INT | NOT NULL | 0 | Sort order in dropdowns |
| created_at | DATETIME2 | NOT NULL | SYSUTCDATETIME() | |
| updated_at | DATETIME2 | NOT NULL | SYSUTCDATETIME() | |

**Indexes:**

| Index | Columns | Type |
|---|---|---|
| UQ_RejectionReasons_OrgCode | org_id, code | Unique |

### 7.3 Seed Data

| Code | Description |
|---|---|
| NIP | Not in product catalog |
| OOS | Out of stock — no replenishment planned |
| DIS | Product discontinued |
| MOQ | Below minimum order quantity |
| GEO | Cannot deliver to requested region |
| REG | Regulatory restriction on this product |
| CAP | Insufficient production capacity |
| CRD | Customer credit hold |
| PRC | Cannot meet requested price |
| OTH | Other (see notes) |

> **📝 NOTE:** Seed data is inserted per org on tenant provisioning. Organizations can add, rename, or deactivate reasons but cannot delete seed codes.

---

## 8. Database Migrations

**Seven migrations are required for this addendum. They are additive and non-breaking.**

### 8.1 Migration Order

| # | Migration Name | Schema | Type | Description |
|---|---|---|---|---|
| M1 | CreateRejectionReasons | lookups | CREATE TABLE | New lookup table for rejection reasons + seed data |
| M2 | CreateSaleInquiries | demand | CREATE TABLE | SaleInquiries + SaleInquiryLines + SaleInquiryAttachments |
| M3 | CreateSaleQuotations | demand | CREATE TABLE | SaleQuotations + SaleQuotationLines + SaleQuotationAttachments |
| M4 | ExtendSaleOrdersSourceLinking | demand | ALTER TABLE | Add source columns + customer PO to SaleOrders |
| M5 | CreateSaleOrderAttachments | demand | CREATE TABLE | SaleOrderAttachments table |
| M6 | AddReservedQtyToSaleOrderLines | demand | ALTER TABLE | Add reserved_qty to SaleOrderLines |
| M7 | SeedSalesPermissions | auth | INSERT | Add sales_reserve_inventory and sales_release_reservation claims |

### 8.2 Migration Details

#### M1 — CreateRejectionReasons

```sql
CREATE TABLE lookups.RejectionReasons (
    rejection_reason_id INT IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_RejectionReasons PRIMARY KEY,
    org_id INT NOT NULL
        CONSTRAINT FK_RejectionReasons_Org FOREIGN KEY REFERENCES tenants.Organizations(org_id),
    code NVARCHAR(10) NOT NULL,
    description NVARCHAR(200) NOT NULL,
    is_active BIT NOT NULL CONSTRAINT DF_RejectionReasons_IsActive DEFAULT 1,
    display_order INT NOT NULL CONSTRAINT DF_RejectionReasons_DisplayOrder DEFAULT 0,
    created_at DATETIME2 NOT NULL CONSTRAINT DF_RejectionReasons_Created DEFAULT SYSUTCDATETIME(),
    updated_at DATETIME2 NOT NULL CONSTRAINT DF_RejectionReasons_Updated DEFAULT SYSUTCDATETIME(),

    CONSTRAINT UQ_RejectionReasons_OrgCode UNIQUE (org_id, code)
);

-- Seed data is inserted per org via tenant provisioning stored procedure
-- See §7.3 for seed values
```

#### M2 — CreateSaleInquiries

```sql
CREATE TABLE demand.SaleInquiries (
    sale_inquiry_id BIGINT IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_SaleInquiries PRIMARY KEY,
    org_id INT NOT NULL
        CONSTRAINT FK_SaleInquiries_Org FOREIGN KEY REFERENCES tenants.Organizations(org_id),
    inquiry_number NVARCHAR(20) NOT NULL,
    customer_id BIGINT NOT NULL
        CONSTRAINT FK_SaleInquiries_Customer FOREIGN KEY REFERENCES suppliers.BusinessPartners(partner_id),
    customer_reference NVARCHAR(50) NULL,
    customer_reference_date DATE NULL,
    status NVARCHAR(20) NOT NULL CONSTRAINT DF_SaleInquiries_Status DEFAULT 'RECEIVED',
    received_date DATE NOT NULL CONSTRAINT DF_SaleInquiries_ReceivedDate DEFAULT GETDATE(),
    response_deadline DATE NULL,
    assigned_to_user_id BIGINT NULL,
    notes NVARCHAR(2000) NULL,
    created_by BIGINT NOT NULL,
    created_at DATETIME2 NOT NULL CONSTRAINT DF_SaleInquiries_Created DEFAULT SYSUTCDATETIME(),
    updated_at DATETIME2 NOT NULL CONSTRAINT DF_SaleInquiries_Updated DEFAULT SYSUTCDATETIME(),
    trace_id UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_SaleInquiries_TraceId DEFAULT NEWID(),

    CONSTRAINT UQ_SaleInquiries_OrgNumber UNIQUE (org_id, inquiry_number)
);

CREATE INDEX IX_SaleInquiries_Customer ON demand.SaleInquiries(customer_id);
CREATE INDEX IX_SaleInquiries_Status ON demand.SaleInquiries(org_id, status);
CREATE INDEX IX_SaleInquiries_AssignedTo ON demand.SaleInquiries(assigned_to_user_id)
    WHERE assigned_to_user_id IS NOT NULL;

CREATE TABLE demand.SaleInquiryLines (
    inquiry_line_id BIGINT IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_SaleInquiryLines PRIMARY KEY,
    sale_inquiry_id BIGINT NOT NULL
        CONSTRAINT FK_SaleInquiryLines_Inquiry FOREIGN KEY REFERENCES demand.SaleInquiries(sale_inquiry_id),
    org_id INT NOT NULL
        CONSTRAINT FK_SaleInquiryLines_Org FOREIGN KEY REFERENCES tenants.Organizations(org_id),
    line_number INT NOT NULL,
    product_id BIGINT NULL,
    variant_id BIGINT NULL,
    product_description NVARCHAR(500) NOT NULL,
    requested_quantity DECIMAL(18,4) NOT NULL,
    requested_uom_id INT NULL,
    requested_delivery_date DATE NULL,
    line_status NVARCHAR(20) NOT NULL CONSTRAINT DF_SaleInquiryLines_Status DEFAULT 'PENDING',
    can_supply_quantity DECIMAL(18,4) NULL,
    estimated_delivery_date DATE NULL,
    rejection_reason_id INT NULL
        CONSTRAINT FK_SaleInquiryLines_RejReason FOREIGN KEY REFERENCES lookups.RejectionReasons(rejection_reason_id),
    rejection_notes NVARCHAR(500) NULL,
    alternative_product_id BIGINT NULL,
    alternative_variant_id BIGINT NULL,
    alternative_notes NVARCHAR(500) NULL,
    requires_procurement BIT NOT NULL CONSTRAINT DF_SaleInquiryLines_ReqProc DEFAULT 0,
    procurement_lead_days INT NULL,
    reviewed_by_user_id BIGINT NULL,
    reviewed_at DATETIME2 NULL,
    notes NVARCHAR(1000) NULL,
    created_at DATETIME2 NOT NULL CONSTRAINT DF_SaleInquiryLines_Created DEFAULT SYSUTCDATETIME(),
    updated_at DATETIME2 NOT NULL CONSTRAINT DF_SaleInquiryLines_Updated DEFAULT SYSUTCDATETIME(),

    CONSTRAINT UQ_SaleInquiryLines_InqLine UNIQUE (sale_inquiry_id, line_number)
);

CREATE INDEX IX_SaleInquiryLines_Product ON demand.SaleInquiryLines(product_id)
    WHERE product_id IS NOT NULL;
CREATE INDEX IX_SaleInquiryLines_Rejection ON demand.SaleInquiryLines(rejection_reason_id)
    WHERE rejection_reason_id IS NOT NULL;

CREATE TABLE demand.SaleInquiryAttachments (
    attachment_id BIGINT IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_SaleInquiryAttachments PRIMARY KEY,
    sale_inquiry_id BIGINT NOT NULL
        CONSTRAINT FK_SaleInqAttach_Inquiry FOREIGN KEY REFERENCES demand.SaleInquiries(sale_inquiry_id),
    org_id INT NOT NULL,
    file_name NVARCHAR(255) NOT NULL,
    file_path NVARCHAR(500) NOT NULL,
    file_size_bytes BIGINT NOT NULL,
    content_type NVARCHAR(100) NOT NULL,
    uploaded_by BIGINT NOT NULL,
    uploaded_at DATETIME2 NOT NULL CONSTRAINT DF_SaleInqAttach_Uploaded DEFAULT SYSUTCDATETIME()
);
```

#### M3 — CreateSaleQuotations

```sql
CREATE TABLE demand.SaleQuotations (
    sale_quotation_id BIGINT IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_SaleQuotations PRIMARY KEY,
    org_id INT NOT NULL
        CONSTRAINT FK_SaleQuotations_Org FOREIGN KEY REFERENCES tenants.Organizations(org_id),
    quotation_number NVARCHAR(20) NOT NULL,
    customer_id BIGINT NOT NULL
        CONSTRAINT FK_SaleQuotations_Customer FOREIGN KEY REFERENCES suppliers.BusinessPartners(partner_id),
    customer_reference NVARCHAR(50) NULL,
    customer_reference_date DATE NULL,
    source_inquiry_id BIGINT NULL
        CONSTRAINT FK_SaleQuotations_Inquiry FOREIGN KEY REFERENCES demand.SaleInquiries(sale_inquiry_id),
    currency_code NVARCHAR(3) NOT NULL,
    valid_from DATE NOT NULL CONSTRAINT DF_SaleQuotations_ValidFrom DEFAULT GETDATE(),
    valid_to DATE NOT NULL,
    status NVARCHAR(20) NOT NULL CONSTRAINT DF_SaleQuotations_Status DEFAULT 'DRAFT',
    payment_terms NVARCHAR(200) NULL,
    delivery_terms NVARCHAR(200) NULL,
    subtotal DECIMAL(18,4) NOT NULL CONSTRAINT DF_SaleQuotations_Subtotal DEFAULT 0,
    tax_amount DECIMAL(18,4) NOT NULL CONSTRAINT DF_SaleQuotations_Tax DEFAULT 0,
    discount_amount DECIMAL(18,4) NOT NULL CONSTRAINT DF_SaleQuotations_Discount DEFAULT 0,
    grand_total DECIMAL(18,4) NOT NULL CONSTRAINT DF_SaleQuotations_Total DEFAULT 0,
    notes NVARCHAR(2000) NULL,
    internal_notes NVARCHAR(2000) NULL,
    sent_at DATETIME2 NULL,
    sent_by_user_id BIGINT NULL,
    created_by BIGINT NOT NULL,
    created_at DATETIME2 NOT NULL CONSTRAINT DF_SaleQuotations_Created DEFAULT SYSUTCDATETIME(),
    updated_at DATETIME2 NOT NULL CONSTRAINT DF_SaleQuotations_Updated DEFAULT SYSUTCDATETIME(),
    trace_id UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_SaleQuotations_TraceId DEFAULT NEWID(),

    CONSTRAINT UQ_SaleQuotations_OrgNumber UNIQUE (org_id, quotation_number)
);

CREATE INDEX IX_SaleQuotations_Customer ON demand.SaleQuotations(customer_id);
CREATE INDEX IX_SaleQuotations_Status ON demand.SaleQuotations(org_id, status);
CREATE INDEX IX_SaleQuotations_SourceInquiry ON demand.SaleQuotations(source_inquiry_id)
    WHERE source_inquiry_id IS NOT NULL;
CREATE INDEX IX_SaleQuotations_ValidTo ON demand.SaleQuotations(org_id, valid_to);

CREATE TABLE demand.SaleQuotationLines (
    quotation_line_id BIGINT IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_SaleQuotationLines PRIMARY KEY,
    sale_quotation_id BIGINT NOT NULL
        CONSTRAINT FK_SaleQuotLines_Quotation FOREIGN KEY REFERENCES demand.SaleQuotations(sale_quotation_id),
    org_id INT NOT NULL
        CONSTRAINT FK_SaleQuotLines_Org FOREIGN KEY REFERENCES tenants.Organizations(org_id),
    line_number INT NOT NULL,
    source_inquiry_line_id BIGINT NULL
        CONSTRAINT FK_SaleQuotLines_InqLine FOREIGN KEY REFERENCES demand.SaleInquiryLines(inquiry_line_id),
    variant_id BIGINT NOT NULL,
    product_description NVARCHAR(500) NOT NULL,
    quantity DECIMAL(18,4) NOT NULL,
    uom_id INT NOT NULL,
    unit_price DECIMAL(18,4) NOT NULL,
    discount_percent DECIMAL(5,2) NOT NULL CONSTRAINT DF_SaleQuotLines_Discount DEFAULT 0,
    tax_percent DECIMAL(5,2) NOT NULL CONSTRAINT DF_SaleQuotLines_TaxPct DEFAULT 0,
    tax_amount DECIMAL(18,4) NOT NULL CONSTRAINT DF_SaleQuotLines_TaxAmt DEFAULT 0,
    line_total DECIMAL(18,4) NOT NULL CONSTRAINT DF_SaleQuotLines_Total DEFAULT 0,
    promised_delivery_date DATE NULL,
    line_type NVARCHAR(15) NOT NULL CONSTRAINT DF_SaleQuotLines_LineType DEFAULT 'NORMAL',
    rejection_reason_id INT NULL
        CONSTRAINT FK_SaleQuotLines_RejReason FOREIGN KEY REFERENCES lookups.RejectionReasons(rejection_reason_id),
    rejection_notes NVARCHAR(500) NULL,
    alternative_for_line_id BIGINT NULL
        CONSTRAINT FK_SaleQuotLines_AltFor FOREIGN KEY REFERENCES demand.SaleQuotationLines(quotation_line_id),
    alternative_notes NVARCHAR(500) NULL,
    customer_response NVARCHAR(15) NOT NULL CONSTRAINT DF_SaleQuotLines_CustResp DEFAULT 'PENDING',
    customer_response_date DATE NULL,
    customer_response_notes NVARCHAR(500) NULL,
    customer_counter_price DECIMAL(18,4) NULL,
    notes NVARCHAR(1000) NULL,
    created_at DATETIME2 NOT NULL CONSTRAINT DF_SaleQuotLines_Created DEFAULT SYSUTCDATETIME(),
    updated_at DATETIME2 NOT NULL CONSTRAINT DF_SaleQuotLines_Updated DEFAULT SYSUTCDATETIME(),

    CONSTRAINT UQ_SaleQuotationLines_QuotLine UNIQUE (sale_quotation_id, line_number)
);

CREATE INDEX IX_SaleQuotLines_Variant ON demand.SaleQuotationLines(variant_id);
CREATE INDEX IX_SaleQuotLines_AltFor ON demand.SaleQuotationLines(alternative_for_line_id)
    WHERE alternative_for_line_id IS NOT NULL;
CREATE INDEX IX_SaleQuotLines_CustResponse ON demand.SaleQuotationLines(sale_quotation_id, customer_response);

CREATE TABLE demand.SaleQuotationAttachments (
    attachment_id BIGINT IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_SaleQuotationAttachments PRIMARY KEY,
    sale_quotation_id BIGINT NOT NULL
        CONSTRAINT FK_SaleQuotAttach_Quotation FOREIGN KEY REFERENCES demand.SaleQuotations(sale_quotation_id),
    org_id INT NOT NULL,
    file_name NVARCHAR(255) NOT NULL,
    file_path NVARCHAR(500) NOT NULL,
    file_size_bytes BIGINT NOT NULL,
    content_type NVARCHAR(100) NOT NULL,
    uploaded_by BIGINT NOT NULL,
    uploaded_at DATETIME2 NOT NULL CONSTRAINT DF_SaleQuotAttach_Uploaded DEFAULT SYSUTCDATETIME()
);
```

#### M4 — ExtendSaleOrdersSourceLinking

```sql
ALTER TABLE demand.SaleOrders
ADD source_quotation_id BIGINT NULL
    CONSTRAINT FK_SaleOrders_SourceQuotation
    FOREIGN KEY REFERENCES demand.SaleQuotations(sale_quotation_id);

ALTER TABLE demand.SaleOrders
ADD source_inquiry_id BIGINT NULL
    CONSTRAINT FK_SaleOrders_SourceInquiry
    FOREIGN KEY REFERENCES demand.SaleInquiries(sale_inquiry_id);

ALTER TABLE demand.SaleOrders
ADD source_type NVARCHAR(20) NOT NULL
    CONSTRAINT DF_SaleOrders_SourceType DEFAULT 'MANUAL';

ALTER TABLE demand.SaleOrders
ADD customer_po_reference NVARCHAR(50) NULL;

ALTER TABLE demand.SaleOrders
ADD customer_po_date DATE NULL;

ALTER TABLE demand.SaleOrders
ADD customer_po_attachment_id BIGINT NULL;

-- Indexes
CREATE INDEX IX_SaleOrders_SourceQuotation ON demand.SaleOrders(source_quotation_id)
    WHERE source_quotation_id IS NOT NULL;

CREATE INDEX IX_SaleOrders_SourceInquiry ON demand.SaleOrders(source_inquiry_id)
    WHERE source_inquiry_id IS NOT NULL;

CREATE INDEX IX_SaleOrders_CustomerPO ON demand.SaleOrders(org_id, customer_po_reference)
    WHERE customer_po_reference IS NOT NULL;

-- Backfill: all existing Sale Orders get source_type = 'MANUAL'
-- (handled by DEFAULT constraint — no data migration needed)
```

#### M5 — CreateSaleOrderAttachments

```sql
CREATE TABLE demand.SaleOrderAttachments (
    attachment_id BIGINT IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_SaleOrderAttachments PRIMARY KEY,
    sale_order_id BIGINT NOT NULL
        CONSTRAINT FK_SOAttach_SaleOrder FOREIGN KEY REFERENCES demand.SaleOrders(sale_order_id),
    org_id INT NOT NULL,
    file_name NVARCHAR(255) NOT NULL,
    file_path NVARCHAR(500) NOT NULL,
    file_size_bytes BIGINT NOT NULL,
    content_type NVARCHAR(100) NOT NULL,
    attachment_type NVARCHAR(20) NOT NULL CONSTRAINT DF_SOAttach_Type DEFAULT 'GENERAL',
    uploaded_by BIGINT NOT NULL,
    uploaded_at DATETIME2 NOT NULL CONSTRAINT DF_SOAttach_Uploaded DEFAULT SYSUTCDATETIME()
);

CREATE INDEX IX_SOAttach_SaleOrder ON demand.SaleOrderAttachments(sale_order_id);
```

#### M6 — AddReservedQtyToSaleOrderLines

```sql
ALTER TABLE demand.SaleOrderLines
ADD reserved_qty DECIMAL(18,4) NOT NULL
    CONSTRAINT DF_SaleOrderLines_ReservedQty DEFAULT 0;

-- Backfill: existing lines with status='RESERVED' should have
-- reserved_qty populated from StockReservations
UPDATE sol
SET sol.reserved_qty = ISNULL(sr.total_reserved, 0)
FROM demand.SaleOrderLines sol
OUTER APPLY (
    SELECT SUM(r.quantity) AS total_reserved
    FROM inventory.StockReservations r
    WHERE r.source_type = 'SALES_ORDER'
      AND r.source_id = sol.sale_order_id
      AND r.variant_id = sol.variant_id
      AND r.status = 'ACTIVE'
) sr
WHERE sol.status = 'RESERVED';
```

#### M7 — SeedSalesPermissions

```sql
-- Insert new permission claims for sales reservation
INSERT INTO auth.Permissions (permission_code, permission_name, module, description)
VALUES
    ('sales_reserve_inventory', 'Reserve Inventory for Sale Order', 'Sales',
     'Allows reserving inventory against Sale Order lines'),
    ('sales_release_reservation', 'Release Sale Order Reservation', 'Sales',
     'Allows releasing reserved inventory from Sale Order lines');

-- Assign to default roles (org-specific role assignment via tenant provisioning)
-- SALES_MANAGER and WAREHOUSE_MANAGER roles get both permissions
```

### 8.3 Rollback

| Migration | Rollback Steps |
|---|---|
| M1 | DROP TABLE lookups.RejectionReasons (after clearing FKs from M2, M3) |
| M2 | DROP TABLE demand.SaleInquiryAttachments, demand.SaleInquiryLines, demand.SaleInquiries |
| M3 | DROP TABLE demand.SaleQuotationAttachments, demand.SaleQuotationLines, demand.SaleQuotations |
| M4 | DROP COLUMN source_quotation_id, source_inquiry_id, source_type, customer_po_reference, customer_po_date, customer_po_attachment_id from SaleOrders (after clearing FKs and indexes) |
| M5 | DROP TABLE demand.SaleOrderAttachments |
| M6 | DROP COLUMN reserved_qty from SaleOrderLines |
| M7 | DELETE inserted permission claims |

---

## 9. API Changes

### 9.1 New Endpoints — Sale Inquiry

| Endpoint | Method | Description | Permission |
|---|---|---|---|
| /api/sale-inquiries | GET | List inquiries (paginated, filterable by status, customer, date range) | sales_inquiry_view |
| /api/sale-inquiries | POST | Create a new inquiry | sales_inquiry_create |
| /api/sale-inquiries/{id} | GET | Get inquiry with lines | sales_inquiry_view |
| /api/sale-inquiries/{id} | PUT | Update inquiry header | sales_inquiry_edit |
| /api/sale-inquiries/{id}/lines | POST | Add a line to inquiry | sales_inquiry_edit |
| /api/sale-inquiries/{id}/lines/{lineId} | PUT | Update a line (including evaluation) | sales_inquiry_edit |
| /api/sale-inquiries/{id}/lines/{lineId} | DELETE | Remove a line | sales_inquiry_edit |
| /api/sale-inquiries/{id}/status | PATCH | Transition inquiry status | sales_inquiry_edit |
| /api/sale-inquiries/{id}/attachments | POST | Upload attachment | sales_inquiry_edit |
| /api/sale-inquiries/{id}/attachments/{attachId} | DELETE | Remove attachment | sales_inquiry_edit |
| /api/sale-inquiries/{id}/create-quotation | POST | Create quotation from reviewed inquiry | sales_quotation_create |

### 9.2 New Endpoints — Sale Quotation

| Endpoint | Method | Description | Permission |
|---|---|---|---|
| /api/sale-quotations | GET | List quotations (paginated, filterable) | sales_quotation_view |
| /api/sale-quotations | POST | Create a new quotation | sales_quotation_create |
| /api/sale-quotations/{id} | GET | Get quotation with lines | sales_quotation_view |
| /api/sale-quotations/{id} | PUT | Update quotation header (DRAFT only) | sales_quotation_edit |
| /api/sale-quotations/{id}/lines | POST | Add a line (DRAFT only) | sales_quotation_edit |
| /api/sale-quotations/{id}/lines/{lineId} | PUT | Update a line (DRAFT only) | sales_quotation_edit |
| /api/sale-quotations/{id}/lines/{lineId} | DELETE | Remove a line (DRAFT only) | sales_quotation_edit |
| /api/sale-quotations/{id}/send | POST | Send quotation to customer (DRAFT → SENT) | sales_quotation_send |
| /api/sale-quotations/{id}/lines/{lineId}/customer-response | PATCH | Record customer response on a line | sales_quotation_edit |
| /api/sale-quotations/{id}/accept | POST | Mark quotation as accepted | sales_quotation_edit |
| /api/sale-quotations/{id}/reject | POST | Mark quotation as rejected | sales_quotation_edit |
| /api/sale-quotations/{id}/convert-to-order | POST | Create Sale Order from accepted quotation | sales_order_create |
| /api/sale-quotations/{id}/attachments | POST | Upload attachment | sales_quotation_edit |

### 9.3 Modified Endpoints — Sale Order

| Endpoint | Change | Change ID |
|---|---|---|
| POST /api/sale-orders | Accept source_quotation_id, source_inquiry_id, source_type, customer_po_reference, customer_po_date in request DTO | C3 |
| GET /api/sale-orders/{id} | Return source_quotation_id, source_inquiry_id, source_type, customer_po_reference, customer_po_date, delivery indicators per line, reserved_qty per line | C3, C4 |
| GET /api/sale-orders/{id}/lines | Include reserved_qty and computed delivery_indicator per line | C4 |

### 9.4 New Endpoints — Reservation

| Endpoint | Method | Description | Permission |
|---|---|---|---|
| /api/sale-orders/{id}/lines/{lineId}/reserve | POST | Reserve inventory for a SO line | sales_reserve_inventory |
| /api/sale-orders/{id}/lines/{lineId}/release | POST | Release reservation on a SO line | sales_release_reservation |
| /api/sale-orders/{id}/reserve-all | POST | Batch reserve all open lines | sales_reserve_inventory |

### 9.5 New Endpoints — Rejection Reasons

| Endpoint | Method | Description | Permission |
|---|---|---|---|
| /api/rejection-reasons | GET | List active rejection reasons | authenticated |
| /api/rejection-reasons | POST | Create a new rejection reason | admin |
| /api/rejection-reasons/{id} | PUT | Update a rejection reason | admin |
| /api/rejection-reasons/{id}/deactivate | PATCH | Soft-delete a rejection reason | admin |

---

## 10. UI Wireframes & Specifications

### 10.1 Sale Inquiry List

```
┌───────────────────────────────────────────────────────────────┐
│  Sales → Inquiries                           [+ New Inquiry]  │
│  ─────────────────────────────────────────────────────────── │
│  Filter: [Status ▼] [Customer ▼] [Date From] [Date To]       │
│  ─────────────────────────────────────────────────────────── │
│                                                               │
│  ┌─────────────────────────────────────────────────────────┐ │
│  │ Inquiry #      Customer       Received  Status    Lines │ │
│  │──────────────────────────────────────────────────────── │ │
│  │ INQ-2026-0042  GlobalTech Co  Oct 01    ● Review    5   │ │
│  │ INQ-2026-0041  Meridian Ltd   Sep 28    ● Complete  3   │ │
│  │ INQ-2026-0040  NorthStar Inc  Sep 25    ● Quoted    8   │ │
│  │ INQ-2026-0039  Atlas Corp     Sep 22    ● Declined  2   │ │
│  └─────────────────────────────────────────────────────────┘ │
│                                                               │
│  Status legend: ● RECEIVED (blue) ● UNDER_REVIEW (orange)   │
│  ● REVIEW_COMPLETE (green) ● QUOTED (purple) ● DECLINED (red)│
└───────────────────────────────────────────────────────────────┘
```

### 10.2 Sale Inquiry Detail — Line Evaluation

```
┌──────────────────────────────────────────────────────────────────┐
│  Inquiry: INQ-2026-0042          Status: UNDER_REVIEW            │
│  Customer: GlobalTech Co         Ref: GT-RFQ-2026-118            │
│  Received: Oct 01, 2026          Deadline: Oct 10, 2026          │
│  Assigned To: [John Smith ▼]                                     │
│  ──────────────────────────────────────────────────────────────  │
│  [Details] [Lines] [Attachments]                                 │
│                ▲ active                                           │
│  ──────────────────────────────────────────────────────────────  │
│                                                                   │
│  ┌──────────────────────────────────────────────────────────────┐│
│  │ #  Product          Qty    UOM  Delivery  Status    Action   ││
│  │─────────────────────────────────────────────────────────────││
│  │ 1  Steel Rod 10mm   500    KG   Oct 15   ● Can     [Edit]   ││
│  │ 2  Copper Wire 2mm  1000   M    Oct 20   ● Partial [Edit]   ││
│  │    └ Can supply: 600M by Oct 25                              ││
│  │ 3  Titanium Sheet   200    KG   Oct 12   ● Cannot  [Edit]   ││
│  │    └ Reason: DIS — Discontinued                              ││
│  │    └ Alternative: Stainless Steel Sheet 316L                 ││
│  │ 4  Rubber Gasket    5000   EA   Oct 18   ● Review  [Edit]   ││
│  │    └ Checking supplier lead time...                          ││
│  │ 5  Bolt M8x40       10000  EA   Oct 10   ● Can     [Edit]   ││
│  │                                           [+ Add Line]       ││
│  └──────────────────────────────────────────────────────────────┘│
│                                                                   │
│  Status: ● CAN_SUPPLY (green) ● PARTIAL (yellow)                │
│          ● CANNOT_SUPPLY (red) ● UNDER_REVIEW (orange)           │
│          ● PENDING (grey)                                         │
│                                                                   │
│  [Mark Review Complete]                         [Decline Inquiry] │
└──────────────────────────────────────────────────────────────────┘
```

### 10.3 Sale Quotation Detail — Lines with Alternatives

```
┌──────────────────────────────────────────────────────────────────┐
│  Quotation: SQ-2026-0015         Status: DRAFT                   │
│  Customer: GlobalTech Co         Valid: Oct 03 — Oct 31, 2026    │
│  From Inquiry: INQ-2026-0042     Currency: USD                    │
│  ──────────────────────────────────────────────────────────────  │
│  [Details] [Lines] [Terms] [Attachments]                         │
│                ▲ active                                           │
│  ──────────────────────────────────────────────────────────────  │
│                                                                   │
│  ┌──────────────────────────────────────────────────────────────┐│
│  │ #  Product          Qty   Price   Total    Type   Response   ││
│  │─────────────────────────────────────────────────────────────││
│  │ 1  Steel Rod 10mm   500   $2.50   $1,250   NORMAL  Pending  ││
│  │ 2  Copper Wire 2mm  600   $1.80   $1,080   NORMAL  Pending  ││
│  │    └ (adjusted from 1000 — partial supply)                   ││
│  │ 3  Titanium Sheet   —     —       —        ✕ REJ   —        ││
│  │    └ Reason: Discontinued                                    ││
│  │ 3a SS Sheet 316L    200   $8.50   $1,700   ◇ ALT   Pending  ││
│  │    └ Alternative for line 3                                  ││
│  │ 3b SS Sheet 304     200   $6.20   $1,240   ◇ ALT   Pending  ││
│  │    └ Alternative for line 3 (lower grade option)             ││
│  │ 4  Bolt M8x40       10000 $0.12   $1,200   NORMAL  Pending  ││
│  │                                             [+ Add Line]     ││
│  └──────────────────────────────────────────────────────────────┘│
│                                                                   │
│  Subtotal: $6,470.00   Tax: $0.00   Grand Total: $6,470.00      │
│                                                                   │
│  [Send to Customer]                                               │
└──────────────────────────────────────────────────────────────────┘
```

### 10.4 Sale Quotation — Customer Response Recording

```
┌──────────────────────────────────────────────────────────────────┐
│  Quotation: SQ-2026-0015         Status: SENT                    │
│  ──────────────────────────────────────────────────────────────  │
│  Record Customer Response                                         │
│  ──────────────────────────────────────────────────────────────  │
│                                                                   │
│  ┌──────────────────────────────────────────────────────────────┐│
│  │ #  Product          Qty   Price   Response                   ││
│  │─────────────────────────────────────────────────────────────││
│  │ 1  Steel Rod 10mm   500   $2.50   [Accepted ▼]              ││
│  │ 2  Copper Wire 2mm  600   $1.80   [Accepted ▼]              ││
│  │ 3a SS Sheet 316L    200   $8.50   [Accepted ▼]              ││
│  │ 3b SS Sheet 304     200   $6.20   [Rejected ▼]              ││
│  │ 4  Bolt M8x40       10000 $0.12   [Counter  ▼]              ││
│  │    └ Counter price: [$0.10    ]                              ││
│  │    └ Notes: [Bulk discount expected for 10K+ qty]            ││
│  └──────────────────────────────────────────────────────────────┘│
│                                                                   │
│  [Save Responses]   [Mark Quotation Accepted]                     │
└──────────────────────────────────────────────────────────────────┘
```

### 10.5 Sale Order — Extended Header with Source & Customer PO

```
┌──────────────────────────────────────────────────────────────────┐
│  Sale Order: SO-1085                 Status: CONFIRMED            │
│  ──────────────────────────────────────────────────────────────  │
│  Customer: GlobalTech Co                                          │
│  Source: From Quotation SQ-2026-0015   ← clickable link          │
│  Inquiry: INQ-2026-0042               ← clickable link           │
│                                                                   │
│  Customer PO                                                      │
│  ┌────────────────────────┐ ┌────────────────┐                   │
│  │ PO Reference           │ │ PO Date        │                   │
│  │ GT-PO-2026-4521        │ │ Oct 05, 2026   │                   │
│  └────────────────────────┘ └────────────────┘                   │
│  📎 GlobalTech_PO_4521.pdf  [View] [Replace]                     │
│  ──────────────────────────────────────────────────────────────  │
│  [Header] [Lines] [Fulfillment] [Attachments] [History]          │
└──────────────────────────────────────────────────────────────────┘
```

### 10.6 Sale Order Lines — Delivery Indicators & Reservation

```
┌──────────────────────────────────────────────────────────────────┐
│  [Header] [Lines] [Fulfillment] [Attachments] [History]          │
│              ▲ active                                             │
│  ──────────────────────────────────────────────────────────────  │
│                                                                   │
│  ┌──────────────────────────────────────────────────────────────┐│
│  │    Product          Qty    Rsvd   Dlvrd  Status  Ind  Action ││
│  │─────────────────────────────────────────────────────────────││
│  │ 1  Steel Rod 10mm   500   500    0      RSVD    🔵  [Rlse]  ││
│  │ 2  Copper Wire 2mm  600   0      300    PARTIAL 🟡  [Rsrv]  ││
│  │ 3  SS Sheet 316L    200   0      200    FULFL   🟢  —       ││
│  │ 4  Bolt M8x40       10000 0      0      OPEN    🔴  [Rsrv]  ││
│  └──────────────────────────────────────────────────────────────┘│
│                                                                   │
│  Column Key:                                                      │
│  Qty = ordered quantity                                           │
│  Rsvd = reserved_qty (inventory held)                             │
│  Dlvrd = fulfilled_qty (shipped/delivered)                        │
│  Ind = delivery indicator (computed)                              │
│                                                                   │
│  [Reserve All Open Lines]                                         │
│                                                                   │
│  ⓘ Reserve/Release buttons require 'sales_reserve_inventory'     │
│    permission. Contact your administrator if disabled.            │
└──────────────────────────────────────────────────────────────────┘
```

---

## 11. Business Rules

**Consolidated business rules for all 5 changes.**

| Rule ID | Change | Rule Description | Enforcement Point |
|---|---|---|---|
| BR-C1-01 | C1 | Inquiry can only be created for a BusinessPartner with partner_type including CUSTOMER | SaleInquiryService |
| BR-C1-02 | C1 | Inquiry number auto-generated: INQ-{YYYY}-{NNNN} per org per year | SaleInquiryService |
| BR-C1-03 | C1 | UNDER_REVIEW → REVIEW_COMPLETE requires ALL lines to have status ≠ PENDING | SaleInquiryService |
| BR-C1-04 | C1 | Line status CANNOT_SUPPLY requires rejection_reason_id to be set | SaleInquiryLineService |
| BR-C1-05 | C1 | Line status PARTIAL requires can_supply_quantity > 0 and < requested_quantity | SaleInquiryLineService |
| BR-C1-06 | C1 | Inquiry in QUOTED or DECLINED status is read-only — no line modifications | SaleInquiryService |
| BR-C1-07 | C1 | Creating a quotation from an inquiry sets inquiry status to QUOTED | SaleQuotationService |
| BR-C2-01 | C2 | Quotation can only be created for a BusinessPartner with partner_type including CUSTOMER | SaleQuotationService |
| BR-C2-02 | C2 | Quotation number auto-generated: SQ-{YYYY}-{NNNN} per org per year | SaleQuotationService |
| BR-C2-03 | C2 | valid_to must be ≥ valid_from | SaleQuotationService |
| BR-C2-04 | C2 | DRAFT → SENT requires at least one line with line_type NORMAL or ALTERNATIVE | SaleQuotationService |
| BR-C2-05 | C2 | Lines can only be added/modified/removed while quotation is DRAFT | SaleQuotationLineService |
| BR-C2-06 | C2 | line_type = ALTERNATIVE requires alternative_for_line_id pointing to a REJECTED line in the same quotation | SaleQuotationLineService |
| BR-C2-07 | C2 | SENT → ACCEPTED requires at least one line with customer_response = ACCEPTED | SaleQuotationService |
| BR-C2-08 | C2 | SENT → REJECTED: all non-REJECTED lines must have customer_response = REJECTED | SaleQuotationService |
| BR-C2-09 | C2 | customer_response = COUNTER requires customer_counter_price > 0 | SaleQuotationLineService |
| BR-C2-10 | C2 | Quotation expiry job runs daily — marks SENT quotations past valid_to as EXPIRED | QuotationExpiryJob (Hangfire) |
| BR-C2-11 | C2 | ACCEPTED → CONVERTED only via convert-to-order endpoint — sets source_quotation_id on SO | SaleQuotationService |
| BR-C3-01 | C3 | source_type defaults to MANUAL when no quotation_id is provided | SaleOrderService |
| BR-C3-02 | C3 | source_type = FROM_QUOTATION requires source_quotation_id to be non-null | SaleOrderService |
| BR-C3-03 | C3 | When converting from quotation, source_inquiry_id is chained from quotation.source_inquiry_id | SaleOrderService |
| BR-C3-04 | C3 | customer_po_reference is free text (max 50 chars), not validated against external systems | SaleOrderService |
| BR-C3-05 | C3 | Duplicate customer_po_reference per org is allowed (warning, not error) | SaleOrderService |
| BR-C4-01 | C4 | Reserve button is DISABLED unless user has sales_reserve_inventory claim | SO Line UI |
| BR-C4-02 | C4 | Release button is DISABLED unless user has sales_release_reservation claim | SO Line UI |
| BR-C4-03 | C4 | Reserve calls IStockReservationService.Reserve with source_type=SALES_ORDER | SaleOrderReservationService |
| BR-C4-04 | C4 | Partial reservation is allowed when available_qty < requested reserve qty | SaleOrderReservationService |
| BR-C4-05 | C4 | Cancelling a Sale Order releases ALL active reservations on its lines | SaleOrderService |
| BR-C4-06 | C4 | reservation_ttl_hours from SaleOrderConfig applies — expired reservations auto-released | ReservationExpiryJob |
| BR-C4-07 | C4 | Delivery indicator is COMPUTED at read time — never persisted | SaleOrderLineDto |
| BR-C4-08 | C4 | Reserve/Release buttons are visible but disabled (not hidden) for unauthorized users | SO Line UI |
| BR-C5-01 | C5 | Rejection reason codes are unique per org | RejectionReasonService |
| BR-C5-02 | C5 | Seed rejection reasons are inserted on tenant provisioning — cannot be deleted, only deactivated | TenantProvisioningService |
| BR-C5-03 | C5 | Deactivated rejection reasons are excluded from dropdowns but retained in historical data | RejectionReason query |

---

## 12. Test Scenarios

| Test ID | Change | Scenario | Expected Result |
|---|---|---|---|
| T-C1-01 | C1 | Create sale inquiry with valid customer | Inquiry created, status = RECEIVED, number auto-generated |
| T-C1-02 | C1 | Create inquiry for non-CUSTOMER partner | Validation error: partner must include CUSTOMER type |
| T-C1-03 | C1 | Add line to inquiry with product from catalog | Line created with product_id set, status = PENDING |
| T-C1-04 | C1 | Add line with free-text description (no product_id) | Line created with product_id NULL, description populated |
| T-C1-05 | C1 | Evaluate line as CAN_SUPPLY | Line status = CAN_SUPPLY, estimated_delivery_date required |
| T-C1-06 | C1 | Evaluate line as PARTIAL (qty 600 of 1000) | can_supply_quantity = 600, estimated_delivery_date required |
| T-C1-07 | C1 | Evaluate line as CANNOT_SUPPLY without rejection_reason | Validation error: rejection_reason_id required |
| T-C1-08 | C1 | Evaluate line as CANNOT_SUPPLY with rejection reason + alternative | Line saved with rejection_reason_id, alternative_product_id |
| T-C1-09 | C1 | Try to mark REVIEW_COMPLETE with PENDING lines | Validation error: all lines must be evaluated |
| T-C1-10 | C1 | Mark REVIEW_COMPLETE when all lines evaluated | Status = REVIEW_COMPLETE |
| T-C1-11 | C1 | Create quotation from reviewed inquiry | Quotation created, inquiry status = QUOTED |
| T-C1-12 | C1 | Modify line on QUOTED inquiry | Validation error: inquiry is read-only |
| T-C2-01 | C2 | Create quotation independently (no inquiry) | Quotation created, source_inquiry_id = NULL |
| T-C2-02 | C2 | Create quotation from inquiry with evaluated lines | Lines pre-populated from inquiry, source_inquiry_line_id linked |
| T-C2-03 | C2 | Add REJECTED line with alternative | REJECTED line + ALTERNATIVE line with alternative_for_line_id |
| T-C2-04 | C2 | Add ALTERNATIVE without pointing to REJECTED line | Validation error: alternative_for_line_id required |
| T-C2-05 | C2 | Send quotation (DRAFT → SENT) | Status = SENT, sent_at populated |
| T-C2-06 | C2 | Try to edit line on SENT quotation | Validation error: quotation is not editable |
| T-C2-07 | C2 | Record customer ACCEPTED on a line | customer_response = ACCEPTED, date recorded |
| T-C2-08 | C2 | Record customer COUNTER without counter price | Validation error: counter_price required |
| T-C2-09 | C2 | Record customer COUNTER with price | customer_response = COUNTER, counter_price saved |
| T-C2-10 | C2 | Mark quotation accepted (at least one ACCEPTED line) | Status = ACCEPTED |
| T-C2-11 | C2 | Quotation expires past valid_to | Hangfire job sets status = EXPIRED |
| T-C2-12 | C2 | Convert accepted quotation to Sale Order | SO created with source_quotation_id, only ACCEPTED lines copied, quotation status = CONVERTED |
| T-C2-13 | C2 | Convert quotation with COUNTER lines (unresolved) | Only ACCEPTED lines in SO; COUNTER lines excluded |
| T-C3-01 | C3 | Create SO directly (no quotation) | source_type = MANUAL, source_quotation_id = NULL |
| T-C3-02 | C3 | Create SO with customer_po_reference | customer_po_reference stored, visible on header |
| T-C3-03 | C3 | Attach customer PO document | Attachment created, customer_po_attachment_id linked |
| T-C3-04 | C3 | Duplicate customer_po_reference per org | Warning shown, not blocked |
| T-C3-05 | C3 | SO from quotation chains inquiry ID | source_inquiry_id = quotation.source_inquiry_id |
| T-C4-01 | C4 | View SO lines — no reservations or fulfillment | All lines show 🔴 RED indicator |
| T-C4-02 | C4 | Reserve full quantity on line (authorized user) | reserved_qty updated, indicator → 🔵 BLUE |
| T-C4-03 | C4 | Reserve partial quantity (available < ordered) | Partial reserve dialog, reserved_qty = available, indicator → 🟡 YELLOW |
| T-C4-04 | C4 | Try to reserve (unauthorized user) | Button disabled, tooltip shown |
| T-C4-05 | C4 | Release reservation (authorized user) | reserved_qty = 0, inventory returned, indicator → 🔴 RED |
| T-C4-06 | C4 | Cancel SO with active reservations | All reservations released, lines reserved_qty = 0 |
| T-C4-07 | C4 | Reservation TTL expires | Hangfire releases, reserved_qty decremented |
| T-C4-08 | C4 | Line partially fulfilled (300 of 600) | indicator = 🟡 YELLOW |
| T-C4-09 | C4 | Line fully fulfilled | indicator = 🟢 GREEN |
| T-C4-10 | C4 | Reserve All Open Lines (batch) | All eligible lines reserved, indicators updated |
| T-C5-01 | C5 | List rejection reasons | Returns active reasons for org, sorted by display_order |
| T-C5-02 | C5 | Create custom rejection reason | New reason created with unique code |
| T-C5-03 | C5 | Deactivate seed rejection reason | Reason deactivated, excluded from dropdowns, retained in historical data |
| T-C5-04 | C5 | Create reason with duplicate code | Validation error: code must be unique per org |

---

## 13. Development Phases

### 13.1 Phase Breakdown

| Phase | Changes | Estimated Days | Dependencies |
|---|---|---|---|
| Phase A — Schema & Lookups | C5 (RejectionReasons), M1 migrations | 2 days | None |
| Phase B — Sale Inquiry Module | C1 (entities, services, API, UI) | 5 days | Phase A |
| Phase C — Sale Quotation Module | C2 (entities, services, API, UI, Hangfire job) | 6 days | Phase A |
| Phase D — Sale Order Extensions | C3 (source linking, customer PO, conversion logic) | 3 days | Phase C |
| Phase E — Delivery Indicators & Reservation | C4 (indicators, reserve/release, RBAC, auto-release on cancel) | 4 days | Phase D |
| Phase F — Testing & Integration | All changes — integration tests, E2E scenarios | 3 days | Phase A–E |
| | **TOTAL** | **23 days** | |

### 13.2 Parallelization Opportunities

- Phase B (Sale Inquiry) and Phase C (Sale Quotation) can run in **parallel** after Phase A completes — they share only the RejectionReasons lookup, not each other's entities
- Phase D depends on Phase C completion (conversion logic requires quotation entities)
- Phase E depends on Phase D (reservation UI is on the Sale Order lines panel that Phase D extends)

### 13.3 Hangfire Jobs

| Job | Schedule | Description |
|---|---|---|
| QuotationExpiryJob | Daily at 01:00 UTC | Scans SENT quotations past valid_to → marks EXPIRED |
| ReservationExpiryJob | Existing — no change | Releases reservations exceeding reservation_ttl_hours |

### 13.4 New Permission Claims Summary

| Claim Code | Module | Description |
|---|---|---|
| sales_inquiry_view | Sales | View sale inquiries |
| sales_inquiry_create | Sales | Create sale inquiries |
| sales_inquiry_edit | Sales | Edit inquiry lines, evaluate, change status |
| sales_quotation_view | Sales | View sale quotations |
| sales_quotation_create | Sales | Create sale quotations |
| sales_quotation_edit | Sales | Edit quotation lines, record customer responses |
| sales_quotation_send | Sales | Send quotation to customer |
| sales_order_create | Sales | Create sale orders (extends existing) |
| sales_reserve_inventory | Sales | Reserve inventory for SO lines |
| sales_release_reservation | Sales | Release SO line reservations |

---

## Appendix A — Entity Relationship Summary

```
lookups.RejectionReasons
    ↑ FK                    ↑ FK
demand.SaleInquiryLines     demand.SaleQuotationLines
    ↑ FK                        ↑ FK  ↑ self-ref (alternative_for)
demand.SaleInquiries        demand.SaleQuotations
    ↑ FK (source_inquiry_id)    ↑ FK (source_quotation_id)
    └───────────────────────────┘
                    ↓
            demand.SaleOrders  (extended)
                    ↓
            demand.SaleOrderLines (extended: reserved_qty)
                    ↓
            inventory.StockReservations (existing — SALES_ORDER source_type)
```

---

## Appendix B — Deferred to Addendum 33

The following capabilities are explicitly NOT covered in this addendum and will be addressed in Addendum 33 — Fulfillment & Delivery:

| Topic | Description |
|---|---|
| Fulfillment Orchestration | Picking lists, pack slips, fulfillment workflow |
| Delivery Notes | Delivery note generation, partial delivery tracking |
| Shipment Tracking | Carrier integration, tracking numbers, delivery proof |
| Invoice Generation | Invoice creation from fulfilled Sale Orders |
| Customer Portal | Self-service portal for order placement (source_type=PORTAL) |
| Inter-Tenant Orders | Cross-tenant purchase → sale order flow (source_type=INTER_TENANT) |

---

*End of FSD Addendum 32 — Sales Pre-Order Pipeline*
