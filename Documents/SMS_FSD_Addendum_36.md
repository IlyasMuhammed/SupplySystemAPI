# SUPPLY MANAGEMENT SYSTEM — Functional Specification Document

## FSD Addendum 36 — Service Orders & Service Fulfillment

| Property | Value |
|---|---|
| Document ID | SMS-FSD-ADD-036 |
| Version | 1.0 |
| Date | 2026-10-10 |
| Status | Draft |
| Author | System Architect |
| Classification | Internal — Development |
| Depends On | SMS-FSD-ADD-030 (Manufacturing, Production & Allocation Engine — ProductionOrders, PMR, MaterialIssues, Production Ledger, Allocation Engine, Supply Requirements), SMS-FSD-ADD-031 (BOM Management Inline, Production Order ↔ Sale Order Link, Material Availability Tab), SMS-FSD-ADD-032 (Sale Inquiry & Quotation — SaleOrders with mixed line types), SMS-FSD-ADD-035 (Multi-Currency — dual-amount storage on transactions) |
| Architecture | .NET 8 / EF Core 8 / SQL Server 2022 / React 18 / Hangfire |
| Change Items | 10 changes (C1–C10) |
| Impact | 10 migrations, 5 new tables, 3 modified tables, 2 extended entities, 1 new service interface, 8 new API endpoints, 6 new UI panels, 1 state machine |

---

## Table of Contents

1. [Purpose & Scope](#1-purpose--scope)
2. [C1 — Product Catalog: Service Product Enhancements](#2-c1--product-catalog-service-product-enhancements)
3. [C2 — Service BOM (Optional Bill of Materials for Services)](#3-c2--service-bom-optional-bill-of-materials-for-services)
4. [C3 — Service Orders Entity](#4-c3--service-orders-entity)
5. [C4 — Service Order State Machine](#5-c4--service-order-state-machine)
6. [C5 — Service Material Requirements (SMRs)](#6-c5--service-material-requirements-smrs)
7. [C6 — Material Issue & Return Integration for Service Orders](#7-c6--material-issue--return-integration-for-service-orders)
8. [C7 — Service Completion & Consumption Confirmation](#8-c7--service-completion--consumption-confirmation)
9. [C8 — Service Ledger](#9-c8--service-ledger)
10. [C9 — Fulfillment Requirement Extension for Services](#10-c9--fulfillment-requirement-extension-for-services)
11. [C10 — Sale Order Mixed Lines (Product + Service)](#11-c10--sale-order-mixed-lines-product--service)
12. [Database Migrations](#12-database-migrations)
13. [API Endpoints](#13-api-endpoints)
14. [Permission Claims](#14-permission-claims)
15. [UI Wireframes & Specifications](#15-ui-wireframes--specifications)
16. [Business Rules](#16-business-rules)
17. [Test Scenarios](#17-test-scenarios)
18. [Development Phases](#18-development-phases)
19. [Future Enhancements](#19-future-enhancements)

---

## 1. Purpose & Scope

**This addendum introduces the complete Service Order lifecycle to SMS, enabling organizations to sell, deliver, and invoice services — with or without inventory consumption — using the same architectural patterns as the existing Production Order (Addendum 30). The Service Order mirrors the Production Order flow: BOM explosion → Material Requirements → Allocation → Material Issue → Execution → Consumption Confirmation → Ledger → Invoice.**

### 1.1 Design Principles

- **Services are Products, Not a Separate Module** — Following Odoo's approach, services are a product type (`product_type = Service`) within the existing Product Catalog. Service products flow through the same Sale Order, same invoicing pipeline, and same product master. The differentiation is behavioral, driven by the product's type and flags.
- **Service Order Mirrors Production Order** — The Service Order (`material.ServiceOrders`) is architecturally parallel to `material.ProductionOrders`. It follows the same fulfillment flow: BOM explosion (when BOM exists) → Material Requirements → Allocation Engine → Material Issue → Execution → Completion. The key difference: no Finished Goods output.
- **Optional BOM** — Unlike Production Orders (which require a BOM), Service Orders support two modes: **BOM-driven** (materials pre-defined, auto-exploded) and **Ad-hoc** (assigned person adds materials during service delivery). Both use the same Service Material Requirements (SMR) entity.
- **Reuse Existing Infrastructure** — The Shared Allocation Engine (Addendum 30 §14) already lists `ServiceOrder = 3` as a demand source type. Material Issues, Supply Requirements, and the Allocation Engine are reused as-is with an extended `source_type`. No new allocation logic is required.
- **Three Service Models, One Flow** — Standalone services, product+service bundles, and subcontracted services all pass through the same Service Order lifecycle. The variation is in BOM composition (stock components vs. subcontracted service components) and invoicing (inclusive vs. pass-through billing).
- **Consumption-Only Ledger** — The Service Ledger records only DEBIT entries (materials consumed). Unlike the Production Ledger, there is no CREDIT entry because services produce no inventory output.

### 1.2 Changes Summary

| Change ID | Title | Category |
|---|---|---|
| C1 | Product Catalog: Service Product Enhancements | Extend entity — service-specific flags and invoicing policy |
| C2 | Service BOM (Optional Bill of Materials for Services) | Extend BOM — allow BOM on service products |
| C3 | Service Orders Entity | New table — `material.ServiceOrders` |
| C4 | Service Order State Machine | New state machine — 9 states parallel to Production Order |
| C5 | Service Material Requirements (SMRs) | New table — `material.ServiceMaterialRequirements` |
| C6 | Material Issue & Return Integration | Extend entities — `MaterialIssues` supports Service Orders |
| C7 | Service Completion & Consumption Confirmation | New feature — confirm consumed, return unused |
| C8 | Service Ledger | New view/table — debit-only consumption ledger |
| C9 | Fulfillment Requirement Extension for Services | Extend entity — service fulfillment method |
| C10 | Sale Order Mixed Lines (Product + Service) | Extend behavior — mixed storable + service lines on same SO |

### 1.3 Service Models Supported

| # | Model | Description | BOM? | Stock Consumed? | Subcontract? | Example |
|---|---|---|---|---|---|---|
| 1 | Standalone Service | Service sold and performed independently | Optional | Yes (from BOM or ad-hoc) | No | Oil change, house cleaning, car tuning |
| 2 | Product + Service Bundle | Storable product sold with service on same SO | Optional | Yes (product delivery + service materials) | No | AC unit + installation |
| 3 | Subcontracted Service | Service sold to customer, labor from external resource company | Yes (with service-type BOM line) | Yes (materials from your stock) | Yes (labor from vendor) | Installation by third-party labor |

### 1.4 Parallel Architecture: Production Order vs. Service Order

| Concept | Production Order (ADD-030) | Service Order (ADD-036) |
|---|---|---|
| Entity | `material.ProductionOrders` | `material.ServiceOrders` |
| Material Requirements | `material.ProductionMaterialRequirements` | `material.ServiceMaterialRequirements` |
| BOM | Required (Active BOM mandatory) | Optional (BOM or ad-hoc materials) |
| Material Issue | `material.MaterialIssues` (reused) | `material.MaterialIssues` (reused, extended source_type) |
| Quality Inspection | Required step before completion | Not applicable — no manufactured output |
| Finished Goods Receipt | Required — inventory increases | Not applicable — no inventory output |
| Ledger | Debit (consumed) + Credit (produced) | Debit only (consumed) |
| Output | Finished product added to inventory | Service delivered — no inventory output |
| SO Line Type | `product_type IN (FinishedGood, StockItem)` | `product_type = Service` |
| Fulfillment Source | `FulfillmentMethod = Manufacture` | `FulfillmentMethod = Service` (new enum value) |

### 1.5 Odoo Reference Mapping

| Odoo Concept | SMS Equivalent | Notes |
|---|---|---|
| Service product type | `product_type = Service` (existing, value 6) | Already defined in ADD-030 §6.1 |
| Sale Order with mixed lines | SO line with `product_type = Service` triggers Service Order instead of Delivery | C10 handles this |
| Field Service module | `material.ServiceOrders` | Simplified — no FSM app, integrated into core |
| Service BOM / Kit BOM | `material.BillOfMaterials` on service product | C2 — existing BOM table, extended eligibility |
| Timesheet on service | `estimated_hours` / `actual_hours` on ServiceOrder | Time tracking without full HR timesheet module |
| Subcontracting | BOM line with `source_type = SUBCONTRACT` | C2 — new column on BOM lines |

---

## 2. C1 — Product Catalog: Service Product Enhancements

### 2.1 Overview

The existing Product Catalog (Addendum 30 §6) already defines `ProductType.Service = 6` with `is_stockable = false`. This change adds service-specific metadata fields to control invoicing behavior, time estimation, and billing model.

### 2.2 Schema Changes — Products

**Modify:** `lookups.Products`

#### Products — New Columns (ADD)

| Column | Type | Nullable | Default | Description |
|---|---|---|---|---|
| service_invoicing_policy | TINYINT | NULL | NULL | ServiceInvoicingPolicy: FixedPrice=0, CostPlus=1, TimeAndMaterial=2. NULL for non-service products. |
| service_billing_model | TINYINT | NULL | NULL | ServiceBillingModel: Inclusive=0 (materials included in service price), PassThrough=1 (consumed materials added as separate invoice lines). NULL for non-service products. |
| estimated_duration_hours | DECIMAL(8,2) | NULL | NULL | Default estimated duration in hours for this service. Used to pre-fill Service Order estimated_hours. NULL for non-service products. |
| has_service_bom | BIT | NOT NULL | 0 | Whether this service product has a BOM defining standard materials consumed during service delivery. When true, BOM section is visible on Product form. |
| is_subcontractable | BIT | NOT NULL | 0 | Whether this service can be subcontracted to an external labor/resource company. |

### 2.3 ServiceInvoicingPolicy Enum

| Value | Int | Description | Behavior |
|---|---|---|---|
| FixedPrice | 0 | Customer pays a fixed price regardless of actual materials consumed | Materials are an internal cost. Invoice shows only the service line. |
| CostPlus | 1 | Customer pays service labor fee + actual materials consumed | Each consumed material appears as a separate line on the invoice. |
| TimeAndMaterial | 2 | Customer pays for actual hours worked + actual materials consumed | Service fee calculated from actual_hours × hourly rate, plus material lines. |

### 2.4 ServiceBillingModel Enum

| Value | Int | Description | Invoice Impact |
|---|---|---|---|
| Inclusive | 0 | Consumed materials are included in the service price | Invoice: 1 line — service fee only. Materials are internal cost. |
| PassThrough | 1 | Consumed materials are billed separately to customer | Invoice: service fee line + N material lines with markup. |

### 2.5 Validation Rules

| Rule | Condition | Error Message |
|---|---|---|
| SVC-P-01 | `service_invoicing_policy` must be NULL when `product_type ≠ Service` | 'Invoicing policy is only applicable to service products' |
| SVC-P-02 | `service_billing_model` must be NULL when `product_type ≠ Service` | 'Billing model is only applicable to service products' |
| SVC-P-03 | `estimated_duration_hours` must be NULL or > 0 | 'Estimated duration must be a positive number' |
| SVC-P-04 | `has_service_bom` can only be true when `product_type = Service` | 'Service BOM is only applicable to service products' |
| SVC-P-05 | When `has_service_bom = true`, at least one BOM must exist in Draft or higher status | 'At least one Bill of Materials is required when service BOM is enabled' |
| SVC-P-06 | When `service_invoicing_policy = TimeAndMaterial`, the product must have a `unit_price` (hourly rate) | 'Hourly rate (unit price) is required for Time & Material services' |
| SVC-P-07 | `is_subcontractable` can only be true when `product_type = Service` | 'Subcontract flag is only applicable to service products' |

### 2.6 Product Type Classification Update

**Extended classification table (adds to Addendum 30 §6.1):**

| Product Type | Can Sell | Can Purchase | Can Stock | Can Manufacture | Can be BOM Input | Can Have Service BOM | Notes |
|---|---|---|---|---|---|---|---|
| StockItem (0) | Yes | Yes | Yes | No | No | No | Unchanged |
| RawMaterial (1) | No | Yes | Yes | No | Yes | No | Unchanged |
| Component (2) | No | Yes | Yes | No | Yes | No | Unchanged |
| SemiFinished (3) | No | No | Yes | Yes | Yes | No | Unchanged |
| FinishedGood (4) | Yes | No | Yes | Yes | Yes* | No | Unchanged |
| Consumable (5) | No | Yes | No* | No | Yes | No | Unchanged |
| **Service (6)** | **Yes** | **Yes** | **No** | **No** | **Yes** | **Yes** | **EXTENDED — now supports optional BOM and subcontracting** |
| Asset (7) | No | Yes | No | No | No | No | Unchanged |

> **⚠️ KEY CHANGE:** Service products (`product_type = 6`) can now optionally have a BOM attached (`has_service_bom = true`). The BOM defines standard materials consumed during service delivery. This reuses the existing `material.BillOfMaterials` and `material.BillOfMaterialLines` tables — no new BOM tables required.

---

## 3. C2 — Service BOM (Optional Bill of Materials for Services)

### 3.1 Overview

Extend the existing BOM framework to support service products. A Service BOM defines the standard materials consumed when performing a service. Unlike manufacturing BOMs (which are mandatory for production), Service BOMs are **optional** — services can operate without a BOM (ad-hoc material consumption).

### 3.2 BOM Eligibility Extension

**Current rule (Addendum 30 §7.4):**
- BOM product_id must reference a product with `supply_method = Manufacture`

**New rule (replaces):**
- BOM product_id must reference a product with `supply_method = Manufacture` OR (`product_type = Service` AND `has_service_bom = true`)

This is an application-level validation change in `IBomService.Create()` — no schema modification to BillOfMaterials needed.

### 3.3 BOM Line Source Type — New Column

**Modify:** `material.BillOfMaterialLines`

#### BillOfMaterialLines — New Column (ADD)

| Column | Type | Nullable | Default | Description |
|---|---|---|---|---|
| source_type | TINYINT | NOT NULL | 0 | BOMLineSourceType: Stock=0, Subcontract=1, InternalLabor=2. Determines how the material is sourced when BOM is exploded. |
| subcontract_supplier_id | BIGINT | NULL | NULL | FK → suppliers.Suppliers. Default supplier for subcontracted lines. Required when source_type = Subcontract. NULL otherwise. |

### 3.4 BOMLineSourceType Enum

| Value | Int | Description | Behavior on BOM Explosion |
|---|---|---|---|
| Stock | 0 | Material sourced from warehouse inventory | Creates SMR → Allocation Engine → Material Issue (standard flow) |
| Subcontract | 1 | Service/labor sourced from external vendor | Creates Supply Requirement → Purchase Order to subcontract_supplier_id |
| InternalLabor | 2 | Internal labor hours (cost tracking only) | Creates SMR for cost allocation — no stock movement, no PO. Hours recorded on Service Order. |

### 3.5 Service BOM Examples

#### Example A — Oil Change Service BOM

```
Service Product: "Oil Change Service" (product_type = Service, has_service_bom = true)
BOM (base_quantity = 1):

  Line 1: Oil Filter          qty: 1 PCS     source_type: Stock       scrap: 0%    is_critical: true
  Line 2: Engine Oil 5W-30    qty: 4 L       source_type: Stock       scrap: 5%    is_critical: true
  Line 3: Drain Plug Washer   qty: 1 PCS     source_type: Stock       scrap: 0%    is_critical: false
```

#### Example B — AC Installation Service BOM (with Subcontract)

```
Service Product: "AC Installation Service" (product_type = Service, has_service_bom = true, is_subcontractable = true)
BOM (base_quantity = 1):

  Line 1: Mounting Bracket    qty: 1 PCS     source_type: Stock           scrap: 0%    is_critical: true
  Line 2: Copper Piping       qty: 3 M       source_type: Stock           scrap: 10%   is_critical: true
  Line 3: Refrigerant R410A   qty: 2 KG      source_type: Stock           scrap: 5%    is_critical: true
  Line 4: Installation Labor  qty: 1 SVC     source_type: Subcontract     scrap: 0%    is_critical: true
                                              subcontract_supplier_id: → "ABC Labor Services"
```

#### Example C — House Cleaning Service (No BOM)

```
Service Product: "House Cleaning Service" (product_type = Service, has_service_bom = false)
BOM: None — materials added ad-hoc by assigned technician during service.
```

### 3.6 Validation Rules

| Rule | Condition | Error Message |
|---|---|---|
| SVC-BOM-01 | BOM for service product: `product_type` of BOM output product must be `Service` AND `has_service_bom = true` | 'This product does not have service BOM enabled' |
| SVC-BOM-02 | `subcontract_supplier_id` required when `source_type = Subcontract` | 'Subcontract supplier is required for subcontracted BOM lines' |
| SVC-BOM-03 | `subcontract_supplier_id` must be NULL when `source_type ≠ Subcontract` | 'Supplier reference is only valid for subcontracted lines' |
| SVC-BOM-04 | Subcontracted BOM line: `material_product_id` must reference a product with `product_type = Service` | 'Subcontracted line material must be a service-type product' |
| SVC-BOM-05 | InternalLabor BOM line: quantity represents hours, UOM must be 'HR' | 'Internal labor lines must use hours (HR) as unit of measure' |
| SVC-BOM-06 | Existing rule preserved: BOM for manufacturing product still requires `supply_method = Manufacture` — this change does NOT relax manufacturing BOM rules | — |

---

## 4. C3 — Service Orders Entity

### 4.1 Overview

The Service Order is the operational document that tracks service delivery from assignment through completion. It is architecturally parallel to `material.ProductionOrders` — same pattern for material requirements, allocation, issue, and ledger — but without the Quality Inspection and Finished Goods Receipt steps.

### 4.2 Schema — ServiceOrders

**New Table:** `material.ServiceOrders`

| Column | Type | Nullable | Default | Description |
|---|---|---|---|---|
| id | BIGINT IDENTITY | NOT NULL | PK | Primary key |
| org_id | BIGINT | NOT NULL | | Tenant discriminator (EF Core HasQueryFilter) |
| service_number | NVARCHAR(50) | NOT NULL | | Generated via document_number_sequences (SVC-YYYYMMDD-SEQ) |
| service_product_id | BIGINT | NOT NULL | | FK → lookups.Products — the service product being delivered (must be product_type = Service) |
| service_product_variant_id | BIGINT | NULL | | FK → lookups.ProductVariants — optional specific variant |
| customer_id | BIGINT | NOT NULL | | FK → suppliers.BusinessPartners — the customer receiving the service |
| bom_id | BIGINT | NULL | | FK → material.BillOfMaterials — snapshotted at planning time. NULL when service has no BOM (ad-hoc). |
| bom_version | INT | NULL | | BOM version number at time of planning (immutable after PLANNED). NULL when no BOM. |
| quantity | DECIMAL(18,4) | NOT NULL | 1 | Service quantity (e.g., 1 oil change, 2 AC installations). BOM explodes per this quantity. |
| warehouse_id | BIGINT | NOT NULL | | FK → warehouse.Warehouses — warehouse from which materials are sourced |
| assigned_user_id | BIGINT | NULL | | FK → auth.Users — technician/staff assigned to perform the service |
| assigned_team_id | BIGINT | NULL | | FK → auth.Roles — team/group responsible (optional, for team-based assignment) |
| estimated_hours | DECIMAL(8,2) | NULL | | Estimated service duration in hours. Pre-filled from product.estimated_duration_hours × quantity. |
| actual_hours | DECIMAL(8,2) | NULL | | Actual hours spent performing the service. Recorded at completion. |
| scheduled_date | DATE | NULL | | Scheduled date for service delivery |
| scheduled_time | TIME | NULL | | Scheduled start time (optional) |
| actual_start_date | DATETIMEOFFSET | NULL | | When service actually started |
| actual_end_date | DATETIMEOFFSET | NULL | | When service actually completed |
| source_type | TINYINT | NOT NULL | 0 | ServiceSourceType: Manual=0, SalesOrder=1, FulfillmentReq=2 |
| source_id | BIGINT | NULL | | FK → source document (SaleOrder, FulfillmentReq). NULL for manually created. |
| source_line_id | BIGINT | NULL | | FK → source line (SaleOrderLine). NULL for manually created. |
| invoicing_policy | TINYINT | NOT NULL | 0 | Copied from product.service_invoicing_policy. FixedPrice=0, CostPlus=1, TimeAndMaterial=2. |
| billing_model | TINYINT | NOT NULL | 0 | Copied from product.service_billing_model. Inclusive=0, PassThrough=1. |
| priority | TINYINT | NOT NULL | 1 | ServicePriority: Low=0, Normal=1, High=2, Urgent=3 |
| status | TINYINT | NOT NULL | 0 | ServiceOrderStatus enum (see C4 state machine) |
| material_readiness | TINYINT | NOT NULL | 0 | MaterialReadiness: NotChecked=0, Partial=1, Ready=2, Shortage=3, NotApplicable=4 |
| completion_notes | NVARCHAR(2000) | NULL | | Technician's notes on service performed |
| customer_signature | BIT | NOT NULL | 0 | Whether customer signed off on service completion |
| notes | NVARCHAR(2000) | NULL | | General notes |
| trace_id | UNIQUEIDENTIFIER | NULL | | Correlation GUID for DocumentTimelines traceability |
| is_deleted | BIT | NOT NULL | 0 | Soft delete flag |
| created_by | BIGINT | NOT NULL | | FK → auth.Users |
| created_at | DATETIMEOFFSET | NOT NULL | SYSUTCDATETIME() | |
| updated_at | DATETIMEOFFSET | NOT NULL | SYSUTCDATETIME() | |
| row_version | ROWVERSION | NOT NULL | | Concurrency token |

### 4.3 Indexes

| Index Name | Columns | Type |
|---|---|---|
| UQ_ServiceOrders_OrgNumber | org_id, service_number | Unique |
| IX_ServiceOrders_Customer | org_id, customer_id | Non-unique |
| IX_ServiceOrders_Status | org_id, status, scheduled_date | Non-unique |
| IX_ServiceOrders_AssignedUser | assigned_user_id | Non-unique, filtered (WHERE assigned_user_id IS NOT NULL) |
| IX_ServiceOrders_SourceSO | source_id | Non-unique, filtered (WHERE source_type = 1) |
| IX_ServiceOrders_Product | org_id, service_product_id | Non-unique |

### 4.4 Constraints

| Constraint | Condition |
|---|---|
| FK_ServiceOrders_Product | service_product_id → lookups.Products(id) |
| FK_ServiceOrders_Customer | customer_id → suppliers.BusinessPartners(id) |
| FK_ServiceOrders_BOM | bom_id → material.BillOfMaterials(id) |
| FK_ServiceOrders_Warehouse | warehouse_id → warehouse.Warehouses(id) |
| FK_ServiceOrders_AssignedUser | assigned_user_id → auth.Users(id) |
| FK_ServiceOrders_CreatedBy | created_by → auth.Users(id) |
| CK_ServiceOrders_Quantity | quantity > 0 |
| CK_ServiceOrders_EstimatedHours | estimated_hours IS NULL OR estimated_hours > 0 |
| CK_ServiceOrders_ActualHours | actual_hours IS NULL OR actual_hours >= 0 |

---

## 5. C4 — Service Order State Machine

### 5.1 State Definitions

| Status | Int | Description | Transitions To | Trigger |
|---|---|---|---|---|
| Draft | 0 | Service order created, not yet planned | Planned, Cancelled | User creates service order |
| Planned | 1 | Service scheduled. If BOM exists → BOM exploded into SMRs. If no BOM → empty SMR list. | MaterialPending, Ready, Cancelled | User plans/releases the order |
| MaterialPending | 2 | Waiting for materials — at least one SMR has shortage | Ready, Waiting, Cancelled | Material shortage detected during planning |
| Waiting | 3 | Service paused — ad-hoc material requested during service is not available | InProgress, MaterialPending, Cancelled | Technician requests unavailable material during InProgress |
| Ready | 4 | All required materials available/reserved OR no materials needed | InProgress, MaterialPending, Cancelled | All SMR shortages resolved, or service has no material requirements |
| InProgress | 5 | Service is being performed, materials issued | Waiting, Completed, Cancelled | User/technician starts service |
| Completed | 6 | Service finished — consumed items confirmed, unused returned, ledger entries created | Closed | Technician submits service completion |
| Closed | 7 | Invoice generated, all post-service activities done | (terminal) | Admin/system closes order |
| Cancelled | 8 | Service order cancelled | (terminal) | User cancels (with authorization) |

### 5.2 State Machine Diagram

```
  ┌───────┐
  │ DRAFT │ ← Initial state on creation
  └───┬───┘
      │ plan / release
      ▼
  ┌─────────┐
  │ PLANNED │ → BOM exploded (if exists), SMRs created
  └────┬────┘
       │ check material availability
       ├──────────────────────────────────────┐
       ▼                                      ▼
  ┌──────────────────┐              ┌───────┐
  │ MATERIAL_PENDING │◄────────────►│ READY │
  └────────┬─────────┘  materials   └───┬───┘
           │             resolved       │ start service
           │                            ▼
           │                      ┌──────────────┐
           │                      │ IN_PROGRESS  │
           │                      └──┬─────┬─────┘
           │                         │     │
           │    material unavailable │     │ service done
           │                         ▼     ▼
           │                   ┌─────────┐  ┌───────────┐
           │                   │ WAITING │  │ COMPLETED │
           │                   └────┬────┘  └─────┬─────┘
           │                        │              │ close
           │    material arrives    │              ▼
           │    ┌───────────────────┘         ┌────────┐
           │    │                             │ CLOSED │
           │    ▼                             └────────┘
           │ IN_PROGRESS (resumes)
           │
  ┌───────────┐
  │ CANCELLED │ ← from Draft, Planned, MaterialPending, Ready, InProgress, Waiting
  └───────────┘
```

### 5.3 Key Difference from Production Order State Machine

| Production Order | Service Order | Reason |
|---|---|---|
| InProgress → QualityInspection | InProgress → Completed | No manufactured output to inspect |
| QualityInspection → Completed | — (skipped) | No QI step for services |
| Completed requires FGR | Completed requires consumption confirmation | Services consume but don't produce |
| No Waiting state | **Waiting** state exists | Ad-hoc material requests during service can pause execution |
| MaterialPending ↔ Ready only | MaterialPending ↔ Ready + InProgress ↔ Waiting | Two-phase material availability: pre-service (Planned) and mid-service (InProgress) |

### 5.4 State Transition Rules

| # | From | To | Trigger | Conditions |
|---|---|---|---|---|
| ST-01 | Draft | Planned | User plans service order | assigned_user_id OR assigned_team_id is set; warehouse_id is set; service_product_id references a valid service product |
| ST-02 | Planned | MaterialPending | System — BOM explosion found shortages | At least one SMR has shortage_quantity > 0 AND is_critical = true |
| ST-03 | Planned | Ready | System — no shortages or no BOM | All critical SMRs have shortage = 0, OR no SMRs exist (no BOM, ad-hoc service) |
| ST-04 | MaterialPending | Ready | System — all critical materials available | All SMRs where is_critical = true have shortage_quantity = 0 |
| ST-05 | Ready | InProgress | User starts service | actual_start_date set to current timestamp |
| ST-06 | InProgress | Waiting | System — ad-hoc material added that has shortage | Technician adds material (via ad-hoc SMR) and stock is unavailable. Supply Requirement created. |
| ST-07 | Waiting | InProgress | System — shortage resolved | The ad-hoc material that caused Waiting now has shortage = 0 |
| ST-08 | InProgress | Completed | User completes service | Service completion form submitted: consumed items confirmed, unused items returned |
| ST-09 | Completed | Closed | System/Admin | Invoice generated (or manually closed for non-invoiced services) |
| ST-10 | Any non-terminal | Cancelled | User cancels | User with `service_order_cancel` permission. Releases all reservations, cancels Supply Requirements. |

### 5.5 Material Readiness Calculation

Parallel to Production Order readiness (Addendum 30 §11.3):

```csharp
// Triggered by: Allocation Engine completing allocation to an SMR,
//               GRN creating new inventory, Manual reservation, SR fulfillment

public async Task RecalculateMaterialReadiness(long serviceOrderId)
{
    var smrs = await _db.ServiceMaterialRequirements
        .Where(s => s.ServiceOrderId == serviceOrderId && !s.IsDeleted)
        .ToListAsync();

    if (!smrs.Any())
    {
        // No material requirements — service is Ready (ad-hoc mode)
        serviceOrder.MaterialReadiness = MaterialReadiness.NotApplicable;
        if (serviceOrder.Status == ServiceOrderStatus.Planned)
            serviceOrder.Status = ServiceOrderStatus.Ready;
        return;
    }

    foreach (var smr in smrs.Where(s => s.IsCritical))
    {
        if (smr.ShortageQuantity > 0)
        {
            serviceOrder.MaterialReadiness = MaterialReadiness.Shortage;
            // Status stays MaterialPending or transitions to Waiting
            return;
        }
    }

    // All critical materials have shortage = 0
    serviceOrder.MaterialReadiness = MaterialReadiness.Ready;
    if (serviceOrder.Status == ServiceOrderStatus.MaterialPending)
        serviceOrder.Status = ServiceOrderStatus.Ready;
    else if (serviceOrder.Status == ServiceOrderStatus.Waiting)
        serviceOrder.Status = ServiceOrderStatus.InProgress;
}
```

---

## 6. C5 — Service Material Requirements (SMRs)

### 6.1 Overview

Service Material Requirements (SMRs) are the service equivalent of Production Material Requirements (PMRs). They track what materials are needed for a service job, their availability, and consumption status. SMRs are created in two ways:

1. **BOM Explosion** — When a Service Order with a BOM transitions to Planned, the BOM is exploded into SMRs (identical calculation to PMR BOM explosion in Addendum 30 §12.2).
2. **Ad-hoc Addition** — When the assigned technician adds materials during service execution (InProgress status). These create new SMR records on the fly.

### 6.2 Schema — ServiceMaterialRequirements

**New Table:** `material.ServiceMaterialRequirements`

| Column | Type | Nullable | Default | Description |
|---|---|---|---|---|
| id | BIGINT IDENTITY | NOT NULL | PK | Primary key |
| org_id | BIGINT | NOT NULL | | Tenant discriminator |
| service_order_id | BIGINT | NOT NULL | | FK → material.ServiceOrders |
| bom_line_id | BIGINT | NULL | | FK → material.BillOfMaterialLines — source BOM line. NULL for ad-hoc materials. |
| product_id | BIGINT | NOT NULL | | FK → lookups.Products — the material needed |
| product_variant_id | BIGINT | NULL | | FK → lookups.ProductVariants |
| source_type | TINYINT | NOT NULL | 0 | BOMLineSourceType: Stock=0, Subcontract=1, InternalLabor=2. For ad-hoc, always Stock. |
| required_quantity | DECIMAL(18,4) | NOT NULL | | Gross required qty (incl. scrap allowance). For ad-hoc: technician-entered qty. |
| net_quantity | DECIMAL(18,4) | NOT NULL | | Net required qty (before scrap). For ad-hoc: same as required_quantity. |
| scrap_allowance | DECIMAL(18,4) | NOT NULL | 0 | Additional qty for expected scrap. For ad-hoc: 0. |
| reserved_quantity | DECIMAL(18,4) | NOT NULL | 0 | Quantity reserved from inventory |
| issued_quantity | DECIMAL(18,4) | NOT NULL | 0 | Quantity physically issued to technician |
| consumed_quantity | DECIMAL(18,4) | NOT NULL | 0 | Quantity actually consumed during service |
| returned_quantity | DECIMAL(18,4) | NOT NULL | 0 | Quantity returned to warehouse (issued but not consumed) |
| shortage_quantity | DECIMAL(18,4) | NOT NULL | | Computed: required_quantity − reserved_quantity (when > 0, else 0) |
| uom | NVARCHAR(20) | NOT NULL | | Unit of measure |
| warehouse_id | BIGINT | NOT NULL | | FK → warehouse.Warehouses — source warehouse |
| is_critical | BIT | NOT NULL | 1 | Whether this material is mandatory for service readiness. From BOM line, or true for ad-hoc by default. |
| is_adhoc | BIT | NOT NULL | 0 | Whether this SMR was added ad-hoc during service execution (not from BOM explosion) |
| status | TINYINT | NOT NULL | 0 | SMRStatus: Pending=0, PartiallyReserved=1, FullyReserved=2, Issued=3, Consumed=4, Returned=5, Cancelled=6 |
| required_date | DATE | NOT NULL | | When material is needed (from service_order.scheduled_date or current date for ad-hoc) |
| added_by | BIGINT | NULL | | FK → auth.Users — who added this SMR (populated for ad-hoc additions) |
| notes | NVARCHAR(500) | NULL | | Notes (technician can add reason for ad-hoc material) |
| is_deleted | BIT | NOT NULL | 0 | Soft delete |
| created_at | DATETIMEOFFSET | NOT NULL | SYSUTCDATETIME() | |
| updated_at | DATETIMEOFFSET | NOT NULL | SYSUTCDATETIME() | |

### 6.3 Indexes

| Index Name | Columns | Type |
|---|---|---|
| IX_SMR_ServiceOrder | service_order_id, status | Non-unique |
| IX_SMR_Shortage | org_id, product_id, warehouse_id, shortage_quantity | Non-unique, filtered (WHERE shortage_quantity > 0) |
| IX_SMR_Product | product_id, product_variant_id | Non-unique |

### 6.4 BOM Explosion for Services

Same calculation as Production PMR explosion (Addendum 30 §12.2), with one extension for `source_type`:

```csharp
// When Service Order transitions Draft → Planned AND bom_id IS NOT NULL:

var bom = await GetActiveBOM(serviceProductId, variantId);

foreach (var bomLine in bom.Lines)
{
    if (bomLine.SourceType == BOMLineSourceType.Subcontract)
    {
        // Subcontracted line → Create Supply Requirement directly (PO to vendor)
        var sr = new SupplyRequirement
        {
            ProductId = bomLine.MaterialProductId,
            QuantityRequired = serviceOrder.Quantity * bomLine.Quantity / bom.BaseQuantity,
            DemandSourceType = DemandSourceType.ServiceOrder,    // = 3
            DemandSourceId = serviceOrder.Id,
            SupplyMethod = SupplyMethod.Purchase,
            WarehouseId = serviceOrder.WarehouseId,
            RequiredDate = serviceOrder.ScheduledDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
            Priority = serviceOrder.Priority
        };
        // Also create SMR for tracking, but with source_type = Subcontract
        var smr = new ServiceMaterialRequirement
        {
            ServiceOrderId = serviceOrder.Id,
            BomLineId = bomLine.Id,
            ProductId = bomLine.MaterialProductId,
            SourceType = BOMLineSourceType.Subcontract,
            RequiredQuantity = sr.QuantityRequired,
            NetQuantity = sr.QuantityRequired,
            ScrapAllowance = 0,
            ShortageQuantity = sr.QuantityRequired,
            IsCritical = bomLine.IsCritical,
            Status = SMRStatus.Pending
        };
    }
    else if (bomLine.SourceType == BOMLineSourceType.InternalLabor)
    {
        // Internal labor → SMR for cost tracking only, no stock reservation
        var smr = new ServiceMaterialRequirement
        {
            ServiceOrderId = serviceOrder.Id,
            BomLineId = bomLine.Id,
            ProductId = bomLine.MaterialProductId,
            SourceType = BOMLineSourceType.InternalLabor,
            RequiredQuantity = serviceOrder.Quantity * bomLine.Quantity / bom.BaseQuantity,
            NetQuantity = serviceOrder.Quantity * bomLine.Quantity / bom.BaseQuantity,
            ShortageQuantity = 0,  // No stock shortage — labor is not stockable
            IsCritical = false,     // Internal labor does not block material readiness
            Status = SMRStatus.Pending
        };
    }
    else // Stock
    {
        // Standard stock consumption — same as PMR creation
        var netQty = serviceOrder.Quantity * bomLine.Quantity / bom.BaseQuantity;
        var scrapAllowance = netQty * (bomLine.ScrapPercentage / 100);
        var grossQty = netQty + scrapAllowance;

        var smr = new ServiceMaterialRequirement
        {
            ServiceOrderId = serviceOrder.Id,
            BomLineId = bomLine.Id,
            ProductId = bomLine.MaterialProductId,
            SourceType = BOMLineSourceType.Stock,
            NetQuantity = netQty,
            ScrapAllowance = scrapAllowance,
            RequiredQuantity = grossQty,
            ShortageQuantity = grossQty,  // Initially all shortage
            IsCritical = bomLine.IsCritical,
            Status = SMRStatus.Pending
        };
        // Then trigger allocation engine to reserve from available inventory
    }
}
```

### 6.5 Ad-hoc Material Addition

When the assigned technician needs a material not listed in the BOM (or when no BOM exists):

```csharp
// Service Order must be in InProgress status
// Technician selects product from catalog → system creates ad-hoc SMR

public async Task<ServiceMaterialRequirement> AddAdhocMaterial(
    long serviceOrderId, long productId, long? variantId,
    decimal quantity, string uom, string notes)
{
    var serviceOrder = await GetServiceOrder(serviceOrderId);
    if (serviceOrder.Status != ServiceOrderStatus.InProgress)
        throw new BusinessException("Materials can only be added during service execution");

    var smr = new ServiceMaterialRequirement
    {
        ServiceOrderId = serviceOrderId,
        BomLineId = null,  // No BOM line — ad-hoc
        ProductId = productId,
        ProductVariantId = variantId,
        SourceType = BOMLineSourceType.Stock,
        RequiredQuantity = quantity,
        NetQuantity = quantity,
        ScrapAllowance = 0,
        ShortageQuantity = quantity,  // Initially all shortage
        IsCritical = true,  // Ad-hoc materials are critical by default (technician needs them)
        IsAdhoc = true,
        AddedBy = _currentUser.Id,
        Status = SMRStatus.Pending,
        Notes = notes
    };

    // Trigger allocation engine for this SMR
    var availability = await _allocationEngine.GetAvailabilityAsync(
        productId, variantId, serviceOrder.WarehouseId);

    if (availability.AvailableQuantity >= quantity)
    {
        // Stock available → reserve immediately → Material Issue
        await _allocationEngine.AllocateForDemandAsync(
            AllocationDemandType.ServiceOrder, smr.Id);
        // Issue material immediately (technician is waiting)
        await _materialIssueService.CreateAndConfirmIssue(
            serviceOrderId, smr, quantity, serviceOrder.WarehouseId);
    }
    else if (availability.AvailableQuantity > 0)
    {
        // Partial stock → reserve what's available, create SR for remainder
        await _allocationEngine.AllocateForDemandAsync(
            AllocationDemandType.ServiceOrder, smr.Id);
        // Issue available portion
        await _materialIssueService.CreateAndConfirmIssue(
            serviceOrderId, smr, availability.AvailableQuantity,
            serviceOrder.WarehouseId);
        // Create Supply Requirement for shortage
        await _supplyReqService.CreateFromShortage(smr);
        // Service Order → Waiting (material not fully available)
        serviceOrder.Status = ServiceOrderStatus.Waiting;
    }
    else
    {
        // No stock → create Supply Requirement → PO
        await _supplyReqService.CreateFromShortage(smr);
        // Service Order → Waiting
        serviceOrder.Status = ServiceOrderStatus.Waiting;
    }

    return smr;
}
```

### 6.6 SMR Status Flow

```
Pending → PartiallyReserved → FullyReserved → Issued → Consumed
                                                     ↘ Returned (partial)
                                                     
Ad-hoc during InProgress:
  Pending → Issued (immediate when stock available)
  Pending → Pending (Waiting state when stock unavailable) → Issued (when SR fulfilled)
```

---

## 7. C6 — Material Issue & Return Integration for Service Orders

### 7.1 Overview

The existing `material.MaterialIssues` and `material.MaterialIssueLines` entities (Addendum 30 §16) are **reused** for Service Orders. The MaterialIssues table receives a new column to support polymorphic source references (Production Order or Service Order).

### 7.2 Schema Changes — MaterialIssues

**Modify:** `material.MaterialIssues`

#### MaterialIssues — New/Modified Columns

| Column | Type | Nullable | Default | Description |
|---|---|---|---|---|
| production_order_id | BIGINT | **NULL** (changed) | | FK → material.ProductionOrders. **Changed from NOT NULL to NULL** — now optional because issue may be for a Service Order. |
| service_order_id | BIGINT | NULL | | FK → material.ServiceOrders. Populated when issue is for a Service Order. |
| issue_source_type | TINYINT | NOT NULL | 0 | IssueSourceType: ProductionOrder=0, ServiceOrder=1. Determines which FK is populated. |

> **⚠️ CRITICAL MIGRATION NOTE:** The existing `production_order_id NOT NULL` constraint must be relaxed to NULL. All existing records retain their production_order_id value (no data loss). The new `issue_source_type` column is populated as `0` (ProductionOrder) for all existing records via the migration.

#### Check Constraint

```sql
-- Exactly one source must be populated
ALTER TABLE material.MaterialIssues
ADD CONSTRAINT CK_MaterialIssues_SourceXOR
CHECK (
    (issue_source_type = 0 AND production_order_id IS NOT NULL AND service_order_id IS NULL)
    OR
    (issue_source_type = 1 AND service_order_id IS NOT NULL AND production_order_id IS NULL)
);
```

### 7.3 Schema Changes — MaterialIssueLines

**Modify:** `material.MaterialIssueLines`

#### MaterialIssueLines — New/Modified Columns

| Column | Type | Nullable | Default | Description |
|---|---|---|---|---|
| pmr_id | BIGINT | **NULL** (changed) | | FK → material.ProductionMaterialRequirements. **Changed from NOT NULL to NULL.** |
| smr_id | BIGINT | NULL | | FK → material.ServiceMaterialRequirements. Populated when line is for a Service Order. |

#### Check Constraint

```sql
ALTER TABLE material.MaterialIssueLines
ADD CONSTRAINT CK_MaterialIssueLines_ReqXOR
CHECK (
    (pmr_id IS NOT NULL AND smr_id IS NULL)
    OR
    (smr_id IS NOT NULL AND pmr_id IS NULL)
);
```

### 7.4 Material Issue Business Rules for Services

The following rules extend Addendum 30 §16.3 for Service Orders:

| Rule | Description |
|---|---|
| SVC-MI-01 | Material can only be issued when Service Order status = Ready, InProgress, or Waiting |
| SVC-MI-02 | Issued quantity cannot exceed reserved quantity for that SMR (same as PMR rule) |
| SVC-MI-03 | Material issue creates `stock_transactions` with `movement_type = MATERIAL_ISSUE` (same as production) |
| SVC-MI-04 | `stock_balances.on_hand_quantity` decreases by issued quantity (same as production) |
| SVC-MI-05 | `AllocationRecord.consumed_quantity` increases (same as production) |
| SVC-MI-06 | `SMR.issued_quantity` increases, SMR status → Issued (same pattern as PMR) |
| SVC-MI-07 | Partial issue is allowed — issue what is available, remaining stays reserved |

### 7.5 Material Return for Services

When service is completed, unused materials are returned to the warehouse. This uses the existing `stock_transactions` with `movement_type = MATERIAL_RETURN` (already defined in Addendum 30 §19A.3):

```csharp
// During service completion, for each SMR where issued_quantity > consumed_quantity:
var returnQty = smr.IssuedQuantity - smr.ConsumedQuantity;
if (returnQty > 0)
{
    // Create stock transaction: MATERIAL_RETURN
    var stockTx = new StockTransaction
    {
        ProductId = smr.ProductId,
        WarehouseId = serviceOrder.WarehouseId,
        Quantity = returnQty,
        MovementType = MovementType.MATERIAL_RETURN,
        SourceDocumentType = "SERVICE_ORDER",
        SourceDocumentId = serviceOrder.Id,
        TransactionDate = DateTimeOffset.UtcNow
    };
    // stock_balances.on_hand_quantity increases by returnQty
    // SMR.returned_quantity = returnQty
    // SMR.status → Returned (if partial) or Consumed (if consumed + returned = issued)
}
```

---

## 8. C7 — Service Completion & Consumption Confirmation

### 8.1 Overview

Service completion is the final step before invoicing. The assigned technician (or authorized user) submits a completion form that:
1. Confirms which materials were actually consumed (vs. what was issued)
2. Returns unused materials to the warehouse
3. Records actual hours worked
4. Triggers Service Ledger entries
5. Updates the Sale Order line fulfillment status

### 8.2 Completion Workflow

```
Technician clicks "Complete Service" on Service Order (status = InProgress)
  ↓
System displays Consumption Confirmation Form:
  ┌─────────────────────────────────────────────────────────┐
  │ Service Completion: SVC-20261010-001                     │
  │                                                          │
  │ ┌── Material Consumption ─────────────────────────────┐ │
  │ │ Material          │ Issued │ Consumed │ Return │     │ │
  │ │ Oil Filter        │ 1      │ 1        │ 0      │     │ │
  │ │ Engine Oil 5W-30  │ 4 L    │ 3.5 L    │ 0.5 L  │     │ │
  │ │ Drain Plug Washer │ 1      │ 1        │ 0      │     │ │
  │ └─────────────────────────────────────────────────────┘ │
  │                                                          │
  │ ┌── Service Details ──────────────────────────────────┐ │
  │ │ Actual Hours:     [  3.5  ]                         │ │
  │ │ Completion Notes: [________________________]        │ │
  │ │ Customer Sign-off: ☑                                │ │
  │ └─────────────────────────────────────────────────────┘ │
  │                                                          │
  │              [ Cancel ]  [ Complete Service ]            │
  └─────────────────────────────────────────────────────────┘
  ↓
On Submit:
  1. For each SMR: set consumed_quantity to confirmed value
  2. For each SMR where issued > consumed: create MATERIAL_RETURN stock_transaction
  3. Set actual_hours, actual_end_date, completion_notes, customer_signature
  4. Create Service Ledger entries (DEBIT for each consumed material)
  5. Service Order status → Completed
  6. If source_type = SalesOrder: update SaleOrderLine fulfillment_status
  7. MediatR event: ServiceCompletedEvent
```

### 8.3 Completion Validation Rules

| Rule | Condition | Error Message |
|---|---|---|
| SVC-COMP-01 | Service Order status must be InProgress | 'Service can only be completed from In Progress status' |
| SVC-COMP-02 | consumed_quantity must be ≤ issued_quantity for each SMR | 'Consumed quantity cannot exceed issued quantity' |
| SVC-COMP-03 | consumed_quantity must be ≥ 0 for each SMR | 'Consumed quantity cannot be negative' |
| SVC-COMP-04 | actual_hours must be > 0 when invoicing_policy = TimeAndMaterial | 'Actual hours required for Time & Material billing' |
| SVC-COMP-05 | return_quantity = issued_quantity − consumed_quantity (auto-calculated, not editable) | — |
| SVC-COMP-06 | All issued SMRs must have consumed_quantity confirmed (no NULL consumed_quantity allowed at completion) | 'All issued materials must have consumed quantity confirmed' |

### 8.4 Service Completion Service

```csharp
public interface IServiceCompletionService
{
    /// <summary>
    /// Completes a service order: confirms consumption, returns unused materials,
    /// creates ledger entries, updates SO fulfillment.
    /// </summary>
    Task<ServiceCompletionResult> CompleteAsync(
        long serviceOrderId,
        ServiceCompletionRequest request,
        CancellationToken ct);
}

public class ServiceCompletionRequest
{
    public List<MaterialConsumptionLine> ConsumedMaterials { get; set; }
    public decimal? ActualHours { get; set; }
    public string CompletionNotes { get; set; }
    public bool CustomerSignature { get; set; }
}

public class MaterialConsumptionLine
{
    public long SmrId { get; set; }
    public decimal ConsumedQuantity { get; set; }
}
```

---

## 9. C8 — Service Ledger

### 9.1 Overview

The Service Ledger provides a record of materials consumed during service delivery. It is architecturally parallel to the Production Ledger (Addendum 30 §19A) but contains **DEBIT entries only** — materials consumed. There are no CREDIT entries because services produce no inventory output.

### 9.2 Schema — ServiceLedgerEntries

**New Table:** `material.ServiceLedgerEntries`

| Column | Type | Nullable | Default | Description |
|---|---|---|---|---|
| id | BIGINT IDENTITY | NOT NULL | PK | Primary key |
| org_id | BIGINT | NOT NULL | | Tenant discriminator |
| service_order_id | BIGINT | NOT NULL | | FK → material.ServiceOrders |
| service_order_number | NVARCHAR(50) | NOT NULL | | Denormalized SVC number for display |
| entry_type | TINYINT | NOT NULL | 0 | LedgerEntryType: Debit=0 (material consumed). Service ledger is debit-only. |
| product_id | BIGINT | NOT NULL | | FK → lookups.Products — the material consumed |
| product_variant_id | BIGINT | NULL | | FK → lookups.ProductVariants |
| product_name | NVARCHAR(200) | NOT NULL | | Denormalized product name for display |
| product_type | TINYINT | NOT NULL | | ProductType of the consumed item |
| quantity | DECIMAL(18,4) | NOT NULL | | Quantity consumed |
| uom | NVARCHAR(20) | NOT NULL | | Unit of measure |
| warehouse_id | BIGINT | NOT NULL | | FK → warehouse.Warehouses — source warehouse |
| warehouse_name | NVARCHAR(100) | NOT NULL | | Denormalized warehouse name |
| source_document_type | NVARCHAR(20) | NOT NULL | | 'MATERIAL_ISSUE' or 'MATERIAL_RETURN' |
| source_document_id | BIGINT | NOT NULL | | FK → MaterialIssues (for consumption) |
| source_document_number | NVARCHAR(50) | NOT NULL | | MI document number |
| stock_transaction_id | BIGINT | NOT NULL | | FK → stock_transactions — the underlying inventory movement |
| movement_type | NVARCHAR(30) | NOT NULL | | MATERIAL_ISSUE, MATERIAL_RETURN, SERVICE_CONSUMPTION |
| transaction_date | DATETIMEOFFSET | NOT NULL | | When the inventory movement occurred |
| notes | NVARCHAR(500) | NULL | | Optional notes |
| created_at | DATETIMEOFFSET | NOT NULL | SYSUTCDATETIME() | |

### 9.3 Indexes

| Index Name | Columns | Type |
|---|---|---|
| IX_ServiceLedger_SO | service_order_id, entry_type, transaction_date | Non-unique |
| IX_ServiceLedger_Product | org_id, product_id, entry_type, transaction_date | Non-unique |

### 9.4 How Ledger Entries Are Created

| Event | Entry Type | Trigger |
|---|---|---|
| Service Completion — consumed materials confirmed | DEBIT | `ServiceCompletedEvent` — one entry per SMR with consumed_quantity > 0 |
| Material Return — unused materials returned to warehouse | DEBIT (negative) | `ServiceCompletedEvent` — offsetting entry for returned quantity, movement_type = MATERIAL_RETURN |

### 9.5 Service Ledger Example

**Service Order SVC-001: Oil Change Service × 1**

| # | Entry Type | Product | Qty | UOM | Warehouse | Source Doc | Movement Type |
|---|---|---|---|---|---|---|---|
| 1 | DEBIT | Oil Filter | 1 | PCS | Main WH | MI-20261010-001 | MATERIAL_ISSUE |
| 2 | DEBIT | Engine Oil 5W-30 | 3.5 | L | Main WH | MI-20261010-001 | MATERIAL_ISSUE |
| 3 | DEBIT | Drain Plug Washer | 1 | PCS | Main WH | MI-20261010-001 | MATERIAL_ISSUE |
| 4 | DEBIT (−) | Engine Oil 5W-30 | −0.5 | L | Main WH | MI-20261010-001 | MATERIAL_RETURN |

**Net result:** Oil Filter (1), Engine Oil (3.0L net), Drain Plug Washer (1) consumed. 0.5L Engine Oil returned to warehouse.

### 9.6 Service Ledger Business Rules

| Rule | Description |
|---|---|
| SVC-LED-01 | Every completed Service Order MUST have at least one DEBIT entry OR zero material consumption (labor-only service) |
| SVC-LED-02 | Ledger entries are IMMUTABLE once created — reversals create offsetting entries |
| SVC-LED-03 | The sum of DEBIT quantities by product should match SMR consumed_quantity totals |
| SVC-LED-04 | Return entries create negative DEBIT entries (not credits — no credit side for services) |
| SVC-LED-05 | Entries inherit trace_id from Service Order for end-to-end traceability |

---

## 10. C9 — Fulfillment Requirement Extension for Services

### 10.1 Overview

The existing `demand.FulfillmentRequirements` entity (Addendum 30 §10.2) is extended to support service fulfillment. When a Sale Order is confirmed and contains service-type lines, the fulfillment engine creates Service Orders instead of Delivery Orders.

### 10.2 FulfillmentMethod Enum Extension

**Current values (Addendum 30 §10.3):**

| Value | Int | Triggers |
|---|---|---|
| FromStock | 0 | Delivery Order from available inventory |
| Manufacture | 1 | Production Order |
| Purchase | 2 | Supply Requirement → PO |
| Transfer | 3 | Transfer Order |
| Mixed | 4 | Combination of above |

**New value:**

| Value | Int | Triggers |
|---|---|---|
| **Service** | **5** | **Service Order** |

### 10.3 Fulfillment Decision Flow Extension

Extend Addendum 30 §10.3 with service handling:

```csharp
// In FulfillmentRequirementService.CreateFromSaleOrder():
// For each SO line:

if (product.ProductType == ProductType.Service)
{
    // Service product → create Service Order
    fulfillmentReq.FulfillmentMethod = FulfillmentMethod.Service;
    fulfillmentReq.ServiceRequiredQuantity = line.Quantity;

    var serviceOrder = new ServiceOrder
    {
        ServiceProductId = line.ProductId,
        ServiceProductVariantId = line.ProductVariantId,
        CustomerId = saleOrder.CustomerId,
        Quantity = line.Quantity,
        WarehouseId = saleOrder.WarehouseId ?? defaultWarehouseId,
        SourceType = ServiceSourceType.SalesOrder,
        SourceId = saleOrder.Id,
        SourceLineId = line.Id,
        InvoicingPolicy = product.ServiceInvoicingPolicy ?? ServiceInvoicingPolicy.FixedPrice,
        BillingModel = product.ServiceBillingModel ?? ServiceBillingModel.Inclusive,
        EstimatedHours = product.EstimatedDurationHours * line.Quantity,
        ScheduledDate = line.RequestedDeliveryDate,
        Priority = ServicePriority.Normal,
        Status = ServiceOrderStatus.Draft,
        TraceId = fulfillmentReq.TraceId
    };

    // If product has BOM → attach it
    if (product.HasServiceBom)
    {
        var activeBom = await _bomService.GetActiveBOM(product.Id, line.ProductVariantId);
        if (activeBom != null)
        {
            serviceOrder.BomId = activeBom.Id;
            serviceOrder.BomVersion = activeBom.Version;
        }
    }
}
```

### 10.4 Schema Changes — FulfillmentRequirements

**Modify:** `demand.FulfillmentRequirements`

#### FulfillmentRequirements — New Column (ADD)

| Column | Type | Nullable | Default | Description |
|---|---|---|---|---|
| service_required_quantity | DECIMAL(18,4) | NOT NULL | 0 | Quantity that must be fulfilled via service delivery (default 0) |
| service_order_id | BIGINT | NULL | | FK → material.ServiceOrders — the Service Order created for this fulfillment, if applicable. |

---

## 11. C10 — Sale Order Mixed Lines (Product + Service)

### 11.1 Overview

Sale Orders already support line items referencing different products. This change defines the **behavioral difference** when a SO line references a service product vs. a storable product: service lines create Service Orders, storable lines create Delivery Orders, and both can coexist on the same Sale Order.

### 11.2 SO Line Fulfillment Routing

When a Sale Order is confirmed, the `FulfillmentRequirementService` routes each line based on its product type:

| SO Line Product Type | Fulfillment Path | Created Document |
|---|---|---|
| StockItem, RawMaterial, Component, FinishedGood | FromStock / Manufacture / Purchase / Transfer | Delivery Order / Production Order / PO |
| **Service** | **Service** | **Service Order** |
| Consumable (stockable) | FromStock / Purchase | Delivery Order / PO |
| Asset | Purchase | PO |

### 11.3 Mixed Line Example

```
Sale Order: SO-20261010-001
  Customer: Acme Corp
  
  Line 1: AC Unit Model X-2000       (StockItem)    qty: 1    $800.00
           → FulfillmentMethod: FromStock → Delivery Order
           
  Line 2: AC Installation Service     (Service)      qty: 1    $200.00
           → FulfillmentMethod: Service → Service Order SVC-20261010-001
           → Service BOM explodes: Mounting Bracket, Copper Piping, Refrigerant

  Line 3: Extended Warranty 1-Year    (Service)      qty: 1    $50.00
           → FulfillmentMethod: Service → Service Order SVC-20261010-002
           → No BOM — immediate completion possible (no materials)
```

### 11.4 SO Line Completion Indicators

**Extend the delivery indicator system (Addendum 32 §C4) for service lines:**

| Indicator | Color | Condition |
|---|---|---|
| Service Pending | Grey | Service Order created but not yet started |
| Service In Progress | Blue | Service Order status = InProgress |
| Service Waiting | Amber | Service Order status = Waiting (material shortage) |
| Service Completed | Green | Service Order status = Completed or Closed |
| Service Cancelled | Red | Service Order status = Cancelled |

### 11.5 Invoice Generation for Mixed Orders

When all lines on a Sale Order are fulfilled (deliveries confirmed + services completed), the invoice includes all lines:

```
Invoice for SO-20261010-001:
  Line 1: AC Unit Model X-2000           $800.00    (from Delivery confirmation)
  Line 2: AC Installation Service         $200.00    (from Service completion)
  
  IF billing_model = PassThrough for Line 2:
    Line 2a: Mounting Bracket              $25.00    (consumed material, marked up)
    Line 2b: Copper Piping 3m              $40.00    (consumed material, marked up)
    Line 2c: Refrigerant R410A 2kg         $60.00    (consumed material, marked up)
  
  Line 3: Extended Warranty 1-Year         $50.00    (from Service completion)
  ────────────────────────────────────────────────
  Total                                  $1,175.00
```

> **📝 NOTE:** The actual invoice generation logic (how material pass-through lines are created, markup calculation, tax handling) is deferred to the Invoicing addendum. This FSD defines the data flow and the Service Order → Invoice trigger.

---

## 12. Database Migrations

**Ten migrations are required for this addendum. They are additive except for M5 (MaterialIssues FK relaxation) which modifies an existing constraint.**

### 12.1 Migration Order

| # | Migration Name | Schema | Type | Description |
|---|---|---|---|---|
| M1 | AddServiceFieldsToProducts | lookups | ALTER TABLE | Add service_invoicing_policy, service_billing_model, estimated_duration_hours, has_service_bom, is_subcontractable to Products |
| M2 | AddSourceTypeToBOMLines | material | ALTER TABLE | Add source_type, subcontract_supplier_id to BillOfMaterialLines |
| M3 | CreateServiceOrders | material | CREATE TABLE | New ServiceOrders table |
| M4 | CreateServiceMaterialRequirements | material | CREATE TABLE | New ServiceMaterialRequirements table |
| M5 | ExtendMaterialIssuesForServices | material | ALTER TABLE | Relax production_order_id to NULL, add service_order_id, issue_source_type, add XOR check constraint |
| M6 | ExtendMaterialIssueLinesForServices | material | ALTER TABLE | Relax pmr_id to NULL, add smr_id, add XOR check constraint |
| M7 | CreateServiceLedgerEntries | material | CREATE TABLE | New ServiceLedgerEntries table |
| M8 | ExtendFulfillmentRequirements | demand | ALTER TABLE | Add service_required_quantity, service_order_id to FulfillmentRequirements |
| M9 | SeedServiceDocNumberSequence | material | INSERT | Seed document_number_sequences for SVC prefix |
| M10 | BackfillMaterialIssueSourceType | material | UPDATE | Set issue_source_type = 0 for all existing MaterialIssues records |

### 12.2 Migration Details

#### M1 — AddServiceFieldsToProducts

```sql
ALTER TABLE lookups.Products
ADD service_invoicing_policy TINYINT NULL;

ALTER TABLE lookups.Products
ADD service_billing_model TINYINT NULL;

ALTER TABLE lookups.Products
ADD estimated_duration_hours DECIMAL(8,2) NULL;

ALTER TABLE lookups.Products
ADD has_service_bom BIT NOT NULL CONSTRAINT DF_Products_HasServiceBom DEFAULT 0;

ALTER TABLE lookups.Products
ADD is_subcontractable BIT NOT NULL CONSTRAINT DF_Products_IsSubcontractable DEFAULT 0;
```

#### M2 — AddSourceTypeToBOMLines

```sql
ALTER TABLE material.BillOfMaterialLines
ADD source_type TINYINT NOT NULL CONSTRAINT DF_BOMLines_SourceType DEFAULT 0;

ALTER TABLE material.BillOfMaterialLines
ADD subcontract_supplier_id BIGINT NULL
    CONSTRAINT FK_BOMLines_SubcontractSupplier
    FOREIGN KEY REFERENCES suppliers.Suppliers(id);

-- All existing BOM lines default to source_type = 0 (Stock) — backward compatible
```

#### M3 — CreateServiceOrders

```sql
CREATE TABLE material.ServiceOrders
(
    id                          BIGINT IDENTITY(1,1) NOT NULL,
    org_id                      BIGINT NOT NULL,
    service_number              NVARCHAR(50) NOT NULL,
    service_product_id          BIGINT NOT NULL,
    service_product_variant_id  BIGINT NULL,
    customer_id                 BIGINT NOT NULL,
    bom_id                      BIGINT NULL,
    bom_version                 INT NULL,
    quantity                    DECIMAL(18,4) NOT NULL,
    warehouse_id                BIGINT NOT NULL,
    assigned_user_id            BIGINT NULL,
    assigned_team_id            BIGINT NULL,
    estimated_hours             DECIMAL(8,2) NULL,
    actual_hours                DECIMAL(8,2) NULL,
    scheduled_date              DATE NULL,
    scheduled_time              TIME NULL,
    actual_start_date           DATETIMEOFFSET NULL,
    actual_end_date             DATETIMEOFFSET NULL,
    source_type                 TINYINT NOT NULL DEFAULT 0,
    source_id                   BIGINT NULL,
    source_line_id              BIGINT NULL,
    invoicing_policy            TINYINT NOT NULL DEFAULT 0,
    billing_model               TINYINT NOT NULL DEFAULT 0,
    priority                    TINYINT NOT NULL DEFAULT 1,
    status                      TINYINT NOT NULL DEFAULT 0,
    material_readiness          TINYINT NOT NULL DEFAULT 0,
    completion_notes            NVARCHAR(2000) NULL,
    customer_signature          BIT NOT NULL DEFAULT 0,
    notes                       NVARCHAR(2000) NULL,
    trace_id                    UNIQUEIDENTIFIER NULL,
    is_deleted                  BIT NOT NULL DEFAULT 0,
    created_by                  BIGINT NOT NULL,
    created_at                  DATETIMEOFFSET NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at                  DATETIMEOFFSET NOT NULL DEFAULT SYSUTCDATETIME(),
    row_version                 ROWVERSION NOT NULL,

    CONSTRAINT PK_ServiceOrders PRIMARY KEY (id),
    CONSTRAINT FK_ServiceOrders_Org FOREIGN KEY (org_id) REFERENCES tenant.Organizations(id),
    CONSTRAINT FK_ServiceOrders_Product FOREIGN KEY (service_product_id) REFERENCES lookups.Products(id),
    CONSTRAINT FK_ServiceOrders_Variant FOREIGN KEY (service_product_variant_id) REFERENCES lookups.ProductVariants(id),
    CONSTRAINT FK_ServiceOrders_Customer FOREIGN KEY (customer_id) REFERENCES suppliers.BusinessPartners(id),
    CONSTRAINT FK_ServiceOrders_BOM FOREIGN KEY (bom_id) REFERENCES material.BillOfMaterials(id),
    CONSTRAINT FK_ServiceOrders_Warehouse FOREIGN KEY (warehouse_id) REFERENCES warehouse.Warehouses(id),
    CONSTRAINT FK_ServiceOrders_AssignedUser FOREIGN KEY (assigned_user_id) REFERENCES auth.Users(id),
    CONSTRAINT FK_ServiceOrders_CreatedBy FOREIGN KEY (created_by) REFERENCES auth.Users(id),
    CONSTRAINT CK_ServiceOrders_Quantity CHECK (quantity > 0),
    CONSTRAINT CK_ServiceOrders_EstHours CHECK (estimated_hours IS NULL OR estimated_hours > 0),
    CONSTRAINT CK_ServiceOrders_ActHours CHECK (actual_hours IS NULL OR actual_hours >= 0)
);

CREATE UNIQUE INDEX UQ_ServiceOrders_OrgNumber ON material.ServiceOrders(org_id, service_number);
CREATE INDEX IX_ServiceOrders_Customer ON material.ServiceOrders(org_id, customer_id);
CREATE INDEX IX_ServiceOrders_Status ON material.ServiceOrders(org_id, status, scheduled_date);
CREATE INDEX IX_ServiceOrders_AssignedUser ON material.ServiceOrders(assigned_user_id) WHERE assigned_user_id IS NOT NULL;
CREATE INDEX IX_ServiceOrders_SourceSO ON material.ServiceOrders(source_id) WHERE source_type = 1;
CREATE INDEX IX_ServiceOrders_Product ON material.ServiceOrders(org_id, service_product_id);
```

#### M4 — CreateServiceMaterialRequirements

```sql
CREATE TABLE material.ServiceMaterialRequirements
(
    id                    BIGINT IDENTITY(1,1) NOT NULL,
    org_id                BIGINT NOT NULL,
    service_order_id      BIGINT NOT NULL,
    bom_line_id           BIGINT NULL,
    product_id            BIGINT NOT NULL,
    product_variant_id    BIGINT NULL,
    source_type           TINYINT NOT NULL DEFAULT 0,
    required_quantity     DECIMAL(18,4) NOT NULL,
    net_quantity          DECIMAL(18,4) NOT NULL,
    scrap_allowance       DECIMAL(18,4) NOT NULL DEFAULT 0,
    reserved_quantity     DECIMAL(18,4) NOT NULL DEFAULT 0,
    issued_quantity       DECIMAL(18,4) NOT NULL DEFAULT 0,
    consumed_quantity     DECIMAL(18,4) NOT NULL DEFAULT 0,
    returned_quantity     DECIMAL(18,4) NOT NULL DEFAULT 0,
    shortage_quantity     DECIMAL(18,4) NOT NULL,
    uom                   NVARCHAR(20) NOT NULL,
    warehouse_id          BIGINT NOT NULL,
    is_critical           BIT NOT NULL DEFAULT 1,
    is_adhoc              BIT NOT NULL DEFAULT 0,
    status                TINYINT NOT NULL DEFAULT 0,
    required_date         DATE NOT NULL,
    added_by              BIGINT NULL,
    notes                 NVARCHAR(500) NULL,
    is_deleted            BIT NOT NULL DEFAULT 0,
    created_at            DATETIMEOFFSET NOT NULL DEFAULT SYSUTCDATETIME(),
    updated_at            DATETIMEOFFSET NOT NULL DEFAULT SYSUTCDATETIME(),

    CONSTRAINT PK_ServiceMaterialReqs PRIMARY KEY (id),
    CONSTRAINT FK_SMR_Org FOREIGN KEY (org_id) REFERENCES tenant.Organizations(id),
    CONSTRAINT FK_SMR_ServiceOrder FOREIGN KEY (service_order_id) REFERENCES material.ServiceOrders(id),
    CONSTRAINT FK_SMR_BOMLine FOREIGN KEY (bom_line_id) REFERENCES material.BillOfMaterialLines(id),
    CONSTRAINT FK_SMR_Product FOREIGN KEY (product_id) REFERENCES lookups.Products(id),
    CONSTRAINT FK_SMR_Variant FOREIGN KEY (product_variant_id) REFERENCES lookups.ProductVariants(id),
    CONSTRAINT FK_SMR_Warehouse FOREIGN KEY (warehouse_id) REFERENCES warehouse.Warehouses(id),
    CONSTRAINT FK_SMR_AddedBy FOREIGN KEY (added_by) REFERENCES auth.Users(id)
);

CREATE INDEX IX_SMR_ServiceOrder ON material.ServiceMaterialRequirements(service_order_id, status);
CREATE INDEX IX_SMR_Shortage ON material.ServiceMaterialRequirements(org_id, product_id, warehouse_id, shortage_quantity) WHERE shortage_quantity > 0;
CREATE INDEX IX_SMR_Product ON material.ServiceMaterialRequirements(product_id, product_variant_id);
```

#### M5 — ExtendMaterialIssuesForServices

```sql
-- Step 1: Add new columns
ALTER TABLE material.MaterialIssues
ADD service_order_id BIGINT NULL
    CONSTRAINT FK_MaterialIssues_ServiceOrder
    FOREIGN KEY REFERENCES material.ServiceOrders(id);

ALTER TABLE material.MaterialIssues
ADD issue_source_type TINYINT NOT NULL CONSTRAINT DF_MI_SourceType DEFAULT 0;

-- Step 2: Relax production_order_id from NOT NULL to NULL
-- Must drop existing constraint first, then recreate as nullable
ALTER TABLE material.MaterialIssues
ALTER COLUMN production_order_id BIGINT NULL;

-- Step 3: Add XOR check constraint
ALTER TABLE material.MaterialIssues
ADD CONSTRAINT CK_MaterialIssues_SourceXOR
CHECK (
    (issue_source_type = 0 AND production_order_id IS NOT NULL AND service_order_id IS NULL)
    OR
    (issue_source_type = 1 AND service_order_id IS NOT NULL AND production_order_id IS NULL)
);
```

#### M6 — ExtendMaterialIssueLinesForServices

```sql
ALTER TABLE material.MaterialIssueLines
ADD smr_id BIGINT NULL
    CONSTRAINT FK_MILines_SMR
    FOREIGN KEY REFERENCES material.ServiceMaterialRequirements(id);

ALTER TABLE material.MaterialIssueLines
ALTER COLUMN pmr_id BIGINT NULL;

ALTER TABLE material.MaterialIssueLines
ADD CONSTRAINT CK_MILines_ReqXOR
CHECK (
    (pmr_id IS NOT NULL AND smr_id IS NULL)
    OR
    (smr_id IS NOT NULL AND pmr_id IS NULL)
);
```

#### M7 — CreateServiceLedgerEntries

```sql
CREATE TABLE material.ServiceLedgerEntries
(
    id                        BIGINT IDENTITY(1,1) NOT NULL,
    org_id                    BIGINT NOT NULL,
    service_order_id          BIGINT NOT NULL,
    service_order_number      NVARCHAR(50) NOT NULL,
    entry_type                TINYINT NOT NULL DEFAULT 0,
    product_id                BIGINT NOT NULL,
    product_variant_id        BIGINT NULL,
    product_name              NVARCHAR(200) NOT NULL,
    product_type              TINYINT NOT NULL,
    quantity                  DECIMAL(18,4) NOT NULL,
    uom                       NVARCHAR(20) NOT NULL,
    warehouse_id              BIGINT NOT NULL,
    warehouse_name            NVARCHAR(100) NOT NULL,
    source_document_type      NVARCHAR(20) NOT NULL,
    source_document_id        BIGINT NOT NULL,
    source_document_number    NVARCHAR(50) NOT NULL,
    stock_transaction_id      BIGINT NOT NULL,
    movement_type             NVARCHAR(30) NOT NULL,
    transaction_date          DATETIMEOFFSET NOT NULL,
    notes                     NVARCHAR(500) NULL,
    created_at                DATETIMEOFFSET NOT NULL DEFAULT SYSUTCDATETIME(),

    CONSTRAINT PK_ServiceLedgerEntries PRIMARY KEY (id),
    CONSTRAINT FK_SLE_Org FOREIGN KEY (org_id) REFERENCES tenant.Organizations(id),
    CONSTRAINT FK_SLE_ServiceOrder FOREIGN KEY (service_order_id) REFERENCES material.ServiceOrders(id),
    CONSTRAINT FK_SLE_Product FOREIGN KEY (product_id) REFERENCES lookups.Products(id)
);

CREATE INDEX IX_ServiceLedger_SO ON material.ServiceLedgerEntries(service_order_id, entry_type, transaction_date);
CREATE INDEX IX_ServiceLedger_Product ON material.ServiceLedgerEntries(org_id, product_id, entry_type, transaction_date);
```

#### M8 — ExtendFulfillmentRequirements

```sql
ALTER TABLE demand.FulfillmentRequirements
ADD service_required_quantity DECIMAL(18,4) NOT NULL CONSTRAINT DF_FR_ServiceQty DEFAULT 0;

ALTER TABLE demand.FulfillmentRequirements
ADD service_order_id BIGINT NULL
    CONSTRAINT FK_FR_ServiceOrder
    FOREIGN KEY REFERENCES material.ServiceOrders(id);
```

#### M9 — SeedServiceDocNumberSequence

```sql
INSERT INTO lookups.document_number_sequences (org_id, prefix, current_sequence, date_format, padding_length, separator)
SELECT o.id, 'SVC', 0, 'yyyyMMdd', 3, '-'
FROM tenant.Organizations o
WHERE NOT EXISTS (
    SELECT 1 FROM lookups.document_number_sequences d
    WHERE d.org_id = o.id AND d.prefix = 'SVC'
);
```

#### M10 — BackfillMaterialIssueSourceType

```sql
-- All existing MaterialIssues are for Production Orders
UPDATE material.MaterialIssues
SET issue_source_type = 0
WHERE issue_source_type = 0;  -- Already defaulted, but explicit for clarity

-- Verify: no orphans
-- SELECT COUNT(*) FROM material.MaterialIssues WHERE production_order_id IS NULL AND service_order_id IS NULL;
-- Should return 0
```

### 12.3 Rollback Notes

| Migration | Rollback |
|---|---|
| M1 | DROP COLUMN service_invoicing_policy, service_billing_model, estimated_duration_hours, has_service_bom, is_subcontractable from Products |
| M2 | DROP COLUMN source_type, subcontract_supplier_id from BillOfMaterialLines (after dropping FK) |
| M3 | DROP TABLE material.ServiceOrders (after dropping dependent FKs) |
| M4 | DROP TABLE material.ServiceMaterialRequirements |
| M5 | DROP CONSTRAINT CK_MaterialIssues_SourceXOR, DROP COLUMN service_order_id, issue_source_type, ALTER production_order_id back to NOT NULL |
| M6 | DROP CONSTRAINT CK_MILines_ReqXOR, DROP COLUMN smr_id, ALTER pmr_id back to NOT NULL |
| M7 | DROP TABLE material.ServiceLedgerEntries |
| M8 | DROP COLUMN service_required_quantity, service_order_id from FulfillmentRequirements |
| M9 | DELETE FROM lookups.document_number_sequences WHERE prefix = 'SVC' |
| M10 | No rollback needed (column dropped in M5 rollback) |

---

## 13. API Endpoints

### 13.1 Service Order Endpoints

| # | Method | Route | Description | Permission |
|---|---|---|---|---|
| 1 | POST | /api/service-orders | Create a new Service Order | service_order_create |
| 2 | GET | /api/service-orders | List Service Orders (paginated, filterable by status, customer, assigned user, date range) | service_order_read |
| 3 | GET | /api/service-orders/{id} | Get Service Order details (includes SMRs, ledger entries) | service_order_read |
| 4 | PUT | /api/service-orders/{id} | Update Service Order (Draft/Planned status only for most fields) | service_order_write |
| 5 | POST | /api/service-orders/{id}/plan | Transition Draft → Planned (triggers BOM explosion if BOM exists) | service_order_write |
| 6 | POST | /api/service-orders/{id}/start | Transition Ready → InProgress | service_order_write |
| 7 | POST | /api/service-orders/{id}/complete | Submit Service Completion (consumption confirmation + completion details) | service_order_complete |
| 8 | POST | /api/service-orders/{id}/cancel | Cancel Service Order | service_order_cancel |

### 13.2 Service Material Endpoints

| # | Method | Route | Description | Permission |
|---|---|---|---|---|
| 9 | GET | /api/service-orders/{id}/materials | List SMRs for a Service Order | service_order_read |
| 10 | POST | /api/service-orders/{id}/materials | Add ad-hoc material to Service Order (InProgress status only) | service_order_write |
| 11 | DELETE | /api/service-orders/{id}/materials/{smrId} | Remove ad-hoc material (only if not yet issued) | service_order_write |

### 13.3 Service Ledger Endpoint

| # | Method | Route | Description | Permission |
|---|---|---|---|---|
| 12 | GET | /api/service-orders/{id}/ledger | Get Service Ledger entries for a Service Order | service_order_read |

### 13.4 Request/Response Schemas

#### POST /api/service-orders — Request

```json
{
    "serviceProductId": 42,
    "serviceProductVariantId": null,
    "customerId": 15,
    "quantity": 1,
    "warehouseId": 3,
    "assignedUserId": 8,
    "scheduledDate": "2026-10-15",
    "scheduledTime": "09:00:00",
    "priority": 1,
    "notes": "Customer requested morning appointment"
}
```

#### POST /api/service-orders/{id}/complete — Request

```json
{
    "consumedMaterials": [
        { "smrId": 101, "consumedQuantity": 1.0 },
        { "smrId": 102, "consumedQuantity": 3.5 },
        { "smrId": 103, "consumedQuantity": 1.0 }
    ],
    "actualHours": 1.5,
    "completionNotes": "Service completed. Filter replaced, oil changed. 0.5L oil returned.",
    "customerSignature": true
}
```

---

## 14. Permission Claims

| # | Claim | Description |
|---|---|---|
| 1 | service_order_create | Create new Service Orders |
| 2 | service_order_read | View Service Orders, SMRs, and Ledger |
| 3 | service_order_write | Edit Service Orders, plan, start, add ad-hoc materials |
| 4 | service_order_complete | Complete Service Orders (submit consumption confirmation) |
| 5 | service_order_cancel | Cancel Service Orders (releases reservations, cancels SRs) |

---

## 15. UI Wireframes & Specifications

### 15.1 Service Order List Page

**Route:** `/service-orders`

| Element | Specification |
|---|---|
| Page Title | 'Service Orders' |
| Toolbar | '+ New Service Order' button (service_order_create permission) |
| Filters | Status (multi-select), Customer (searchable dropdown), Assigned To (dropdown), Date Range (scheduled_date), Priority |
| Table Columns | Service # (link), Customer, Service Product, Scheduled Date, Assigned To, Status (badge), Priority (badge), Material Readiness (icon) |
| Status Badges | Draft (grey), Planned (blue), MaterialPending (amber), Waiting (orange), Ready (green), InProgress (teal), Completed (dark green), Closed (grey), Cancelled (red) |
| Sorting | Default: scheduled_date ASC, then priority DESC |
| Pagination | Server-side, 25 per page |

### 15.2 Service Order Detail Page

**Route:** `/service-orders/{id}`

**Header Section:**
- Service Number (large), Status badge, Priority badge
- Customer name (link to BP), Service Product name
- Assigned To (user name or team)
- Scheduled Date/Time, Estimated Hours
- Source reference (SO link if from Sale Order)

**Tabs:**

#### Tab 1 — Details
- Editable fields (when Draft/Planned): customer, warehouse, assigned user/team, scheduled date/time, estimated hours, priority, notes
- Read-only when InProgress or beyond
- BOM reference (if attached): BOM number, version, link

#### Tab 2 — Material Availability
**Mirrors the Production Order Material Availability tab (Addendum 31 §C8):**

**Section 1 — Material Available:**
- Lists SMRs with available stock. Columns: Product, Required Qty, Available Qty, Warehouse, Status badge.
- 'Reserve' button per row, 'Reserve All Available' bulk action.

**Section 2 — Material Shortage:**
- Lists SMRs with shortage. Columns: Product, Required Qty, Issued Qty, Shortage Qty, Status badge.
- 'Allocation Request' button → opens Register Demand dialog (same as Addendum 31 §C8).

**Section 3 — Ad-hoc Materials (visible when InProgress or Waiting):**
- '+ Add Material' button → product search dropdown, quantity input, UOM.
- Lists ad-hoc SMRs with 'Ad-hoc' tag.

#### Tab 3 — Service Completion
**Visible when status = InProgress or later:**
- Consumption confirmation table: Product, Issued Qty, Consumed Qty (editable input), Return Qty (auto-calculated)
- Actual Hours input
- Completion Notes textarea
- Customer Sign-off checkbox
- 'Complete Service' button (triggers POST /complete)

#### Tab 4 — Ledger
**Mirrors Production Ledger tab (Addendum 30 §19A.8):**
- Chronological list of ledger entries
- Columns: Date, Entry Type, Product, Qty, UOM, Warehouse, Source Document, Movement Type
- Color coding: DEBIT entries in RED, RETURN entries in GREEN
- Running total per product

#### Tab 5 — Timeline
- DocumentTimeline entries for this Service Order
- Shows all status transitions, material issues, returns, completion

### 15.3 Product Form — Service BOM Section

**Extend the Product form (Addendum 31 §C4):**

**When `product_type = Service` AND `has_service_bom = true`:**
- BOM section appears with the same two-panel layout as manufacturing BOM (Addendum 31 §6.3)
- Panel 1: BOM List (left, 35%)
- Panel 2: BOM Editor (right, 65%)
- BOM Lines include new 'Source Type' column: dropdown (Stock, Subcontract, Internal Labor)
- When Source Type = Subcontract: 'Supplier' dropdown appears (searchable, from suppliers.Suppliers)

### 15.4 Product Form — Service Configuration Section

**When `product_type = Service`:**

| Field | Type | Position | Visibility |
|---|---|---|---|
| Invoicing Policy | Dropdown (FixedPrice, CostPlus, TimeAndMaterial) | Service section | Always when Service |
| Billing Model | Dropdown (Inclusive, PassThrough) | Below Invoicing Policy | Always when Service |
| Estimated Duration (hours) | Number input | Below Billing Model | Always when Service |
| Has Service BOM | Toggle | Below Estimated Duration | Always when Service |
| Is Subcontractable | Toggle | Below Has Service BOM | Always when Service |

### 15.5 Sale Order — Service Line Indicators

**On Sale Order detail page, for lines with service products:**
- Fulfillment indicator shows service-specific states (§11.4) instead of delivery states
- 'View Service Order' link next to indicator → navigates to Service Order detail page
- Service completion percentage: (completed_service_orders / total_service_orders) × 100

### 15.6 Consolidated Service Orders Dashboard

**Route:** `/service-orders/dashboard`

| Section | Content |
|---|---|
| Today's Services | Service Orders with scheduled_date = today, grouped by status |
| Waiting for Materials | Service Orders in MaterialPending or Waiting status, sorted by priority |
| My Assigned Services | Service Orders assigned to current user, sorted by scheduled_date |
| Completion Rate | Services completed this week / total scheduled this week (%) |

---

## 16. Business Rules

### 16.1 Service Order Rules

| Rule ID | Rule | Description |
|---|---|---|
| SVC-01 | Service product type validation | service_product_id must reference a product with product_type = Service |
| SVC-02 | BOM snapshot immutability | bom_id and bom_version are immutable after Planned status |
| SVC-03 | Assignment required for planning | assigned_user_id OR assigned_team_id must be set before Draft → Planned |
| SVC-04 | Material readiness check | Same logic as Production Order (Addendum 30 §11.3) — all critical SMRs must have shortage = 0 for Ready |
| SVC-05 | No BOM = immediate Ready | If service has no BOM (has_service_bom = false or bom_id IS NULL), status goes directly from Planned → Ready |
| SVC-06 | Ad-hoc materials only during InProgress | New SMRs can only be added when status = InProgress |
| SVC-07 | Waiting → InProgress auto-transition | When all shortage SMRs (that caused Waiting) are resolved, status automatically returns to InProgress |
| SVC-08 | Cancellation releases reservations | Cancelling a Service Order releases all stock reservations and cancels outstanding Supply Requirements |
| SVC-09 | Completion requires consumption confirmation | All issued SMRs must have consumed_quantity confirmed at completion |
| SVC-10 | Material return on completion | For each SMR where issued > consumed, a MATERIAL_RETURN stock transaction is auto-created |
| SVC-11 | Source SO update | When Service Order completes, the originating SO line's fulfillment_status is updated |
| SVC-12 | Service Order cannot be edited after Completed | Only notes can be modified in Completed/Closed status |
| SVC-13 | Concurrency control | row_version on ServiceOrders for optimistic concurrency (same pattern as ProductionOrders) |

### 16.2 Supply Requirement Integration Rules

| Rule ID | Rule | Description |
|---|---|---|
| SVC-SR-01 | Demand source type | Supply Requirements created from Service Order SMR shortages use `demand_source_type = ServiceOrder (3)` — already defined in Addendum 30 §13.1 |
| SVC-SR-02 | Subcontract BOM lines | BOM lines with source_type = Subcontract create Supply Requirements with supply_method = Purchase directly, bypassing the stock check |
| SVC-SR-03 | Allocation Engine integration | The Allocation Engine processes Service Order demands using the same priority rules as Production Orders. `AllocationDemandType.ServiceOrder` is a valid demand type. |

### 16.3 Billing Rules

| Rule ID | Rule | Description |
|---|---|---|
| SVC-BILL-01 | FixedPrice + Inclusive | Invoice contains only the service line at the SO line price. Materials are internal cost. |
| SVC-BILL-02 | FixedPrice + PassThrough | Invoice contains the service line at SO price + consumed material lines at cost (or marked-up cost). |
| SVC-BILL-03 | CostPlus | Invoice = service labor fee + consumed material lines. Labor fee = SO line price. |
| SVC-BILL-04 | TimeAndMaterial | Invoice = (actual_hours × hourly_rate) + consumed material lines. Hourly rate from product unit_price. |
| SVC-BILL-05 | Pass-through material pricing | Consumed materials on pass-through invoices use the product's sale price or a configured markup % over cost (markup configuration deferred to invoicing addendum). |

---

## 17. Test Scenarios

### 17.1 Service Product Configuration

| # | Scenario | Expected Result |
|---|---|---|
| TS-01 | Create product with type = Service, set invoicing_policy = FixedPrice | Product saved; service fields populated |
| TS-02 | Set has_service_bom = true on a non-Service product | Validation error: 'Service BOM is only applicable to service products' |
| TS-03 | Set has_service_bom = true without any BOM | Validation error: 'At least one Bill of Materials is required when service BOM is enabled' |
| TS-04 | Create BOM for service product with source_type = Subcontract, no supplier | Validation error: 'Subcontract supplier is required' |
| TS-05 | Create BOM line with source_type = Subcontract and valid supplier | BOM line saved with subcontract_supplier_id |

### 17.2 Service Order Lifecycle

| # | Scenario | Expected Result |
|---|---|---|
| TS-06 | Create Service Order from Sale Order confirmation (service product line) | Service Order created with source_type = SalesOrder, source_id = SO.id |
| TS-07 | Plan Service Order WITH BOM — all materials in stock | BOM exploded into SMRs, all reserved, status → Ready |
| TS-08 | Plan Service Order WITH BOM — partial material shortage | BOM exploded, partial reservation, status → MaterialPending, Supply Requirement created |
| TS-09 | Plan Service Order WITHOUT BOM | No SMRs created, material_readiness = NotApplicable, status → Ready |
| TS-10 | Start service (Ready → InProgress) | actual_start_date set, Material Issue created for reserved materials |
| TS-11 | Add ad-hoc material during InProgress — stock available | SMR created (is_adhoc = true), immediately reserved and issued |
| TS-12 | Add ad-hoc material during InProgress — stock unavailable | SMR created, SR created, status → Waiting |
| TS-13 | Waiting → InProgress transition when material arrives | GRN → Allocation Engine → SMR shortage resolved → status returns to InProgress |
| TS-14 | Complete service — all materials consumed | Consumed quantities confirmed, no returns, ledger entries created, status → Completed |
| TS-15 | Complete service — partial consumption with returns | Consumed < issued, MATERIAL_RETURN stock_transaction created, stock_balances increased, ledger entries include return |
| TS-16 | Cancel Service Order in MaterialPending | Reservations released, Supply Requirements cancelled, status → Cancelled |

### 17.3 Mixed Sale Order (Product + Service)

| # | Scenario | Expected Result |
|---|---|---|
| TS-17 | Confirm SO with Line 1 (StockItem) + Line 2 (Service) | Line 1 → Delivery Order, Line 2 → Service Order. Both fulfillment requirements created. |
| TS-18 | Delivery confirmed, Service still InProgress | SO shows Line 1 = green (delivered), Line 2 = blue (in progress) |
| TS-19 | Both delivery and service completed | SO fully fulfilled. Invoice can be generated with both lines. |
| TS-20 | Service has PassThrough billing — invoice includes material lines | Invoice: product line + service line + consumed material lines |

### 17.4 Subcontracted Service

| # | Scenario | Expected Result |
|---|---|---|
| TS-21 | Plan Service Order with BOM containing Subcontract line | Stock BOM lines → SMRs with stock reservation. Subcontract line → SMR + Supply Requirement with supply_method = Purchase + PO to subcontract supplier. |
| TS-22 | Subcontract PO received (vendor invoice) | Supply Requirement → Fulfilled. SMR shortage resolved. |
| TS-23 | Service completed with subcontracted labor | Service ledger shows material consumption debits. Vendor bill exists for labor. |

### 17.5 Service Ledger

| # | Scenario | Expected Result |
|---|---|---|
| TS-24 | Complete service with 3 materials consumed | 3 DEBIT ledger entries created, one per material |
| TS-25 | Complete service with 1 material partially returned | DEBIT entry for consumed qty + negative DEBIT for returned qty |
| TS-26 | Query ledger by service order | Returns all entries chronologically |
| TS-27 | Query ledger by product across all service orders | Returns consumption analysis by product |

### 17.6 Edge Cases

| # | Scenario | Expected Result |
|---|---|---|
| TS-28 | Service Order with no materials (labor-only, no BOM) | Completes with empty ledger. actual_hours recorded. Invoice = service fee only. |
| TS-29 | Service Order for quantity > 1 (e.g., 3 oil changes) | BOM explodes at qty × 3. Materials scaled accordingly. |
| TS-30 | Service Order cancelled after materials issued | Reservations released. Issued materials must be returned via Material Return before cancellation. |
| TS-31 | Concurrent allocation: two Service Orders compete for same material | Allocation Engine uses priority rules (same as Production — Addendum 30 §14.4) |
| TS-32 | Ad-hoc material added, then removed before issue | SMR deleted (hard delete — never issued). No stock impact. |
| TS-33 | Multiple Material Issues for same Service Order | Allowed — partial issue, additional issue, ad-hoc issue. All linked via SMRs. |

---

## 18. Development Phases

### 18.1 Phase Summary

| Phase | Changes | Description | Estimated Days |
|---|---|---|---|
| 1 | C1, C2 | Product enhancements + Service BOM | 6 days |
| 2 | C3, C4, C5 | Service Orders + State Machine + SMRs | 10 days |
| 3 | C6, C7 | Material Issue integration + Service Completion | 8 days |
| 4 | C8 | Service Ledger | 3 days |
| 5 | C9, C10 | Fulfillment integration + Mixed SO lines | 5 days |
| **Total** | | | **32 days** |

### 18.2 Phase Details

#### Phase 1 — Product Catalog & Service BOM (6 days)

| Track | Tasks | Days |
|---|---|---|
| 1A | M1 migration + Product entity update + service field validations + API update | 2 |
| 1B | M2 migration + BOM line source_type + subcontract supplier FK + BOM eligibility extension | 2 |
| 1C | Product form UI (service config section + service BOM section) + tests | 2 |

#### Phase 2 — Service Order Core (10 days)

| Track | Tasks | Days |
|---|---|---|
| 2A | M3 migration + ServiceOrders entity + EF Core config + repository | 2 |
| 2B | M4 migration + ServiceMaterialRequirements entity + BOM explosion service for services | 2 |
| 2C | State machine implementation + material readiness calculation + transition validations | 2 |
| 2D | Service Order CRUD API (endpoints 1–8) + DTO mapping | 2 |
| 2E | Service Order List + Detail UI (tabs: Details, Material Availability) + tests | 2 |

#### Phase 3 — Material Issue & Completion (8 days)

| Track | Tasks | Days |
|---|---|---|
| 3A | M5, M6 migrations + MaterialIssues/Lines extension + XOR constraints | 2 |
| 3B | Material issue service extension for Service Orders + ad-hoc material addition flow | 2 |
| 3C | Service Completion service (consumption confirmation + material return + SO update) | 2 |
| 3D | Completion UI (Tab 3) + ad-hoc material UI + tests | 2 |

#### Phase 4 — Service Ledger (3 days)

| Track | Tasks | Days |
|---|---|---|
| 4A | M7 migration + ServiceLedgerEntries entity + ledger entry creation service | 1.5 |
| 4B | Ledger UI (Tab 4) + ledger API endpoint + tests | 1.5 |

#### Phase 5 — Fulfillment Integration (5 days)

| Track | Tasks | Days |
|---|---|---|
| 5A | M8, M9, M10 migrations + FulfillmentMethod.Service enum + fulfillment routing logic | 2 |
| 5B | Mixed SO line handling + service line indicators on SO detail page | 1.5 |
| 5C | Service Orders dashboard + integration tests (end-to-end SO → Service → Invoice) | 1.5 |

### 18.3 Dependencies

```
Phase 1 (Product + BOM) ──► Phase 2 (Service Orders + SMRs)
                                       │
                                       ▼
                             Phase 3 (Material Issue + Completion)
                                       │
                                       ▼
                             Phase 4 (Service Ledger)
                                       │
                                       ▼
                             Phase 5 (Fulfillment Integration)
```

---

## 19. Future Enhancements

The following capabilities are **not included** in this addendum and are deferred to future addendums:

| # | Feature | Description | Rationale for Deferral |
|---|---|---|---|
| 1 | Service Contracts / AMC | Annual Maintenance Contracts with scheduled recurring service orders | Requires subscription/recurring billing infrastructure |
| 2 | Field Service Management | GPS tracking, route optimization, mobile app for field technicians | Requires mobile app and location services |
| 3 | Timesheet Integration | Detailed timesheet entries per technician per service order | Requires HR/timesheet module |
| 4 | Service Level Agreements (SLA) | Response time targets, escalation rules, SLA compliance tracking | Requires notification/escalation engine |
| 5 | Warranty Service | Service orders triggered by warranty claims, warranty period tracking | Requires warranty management module |
| 6 | Service Pricing Rules | Dynamic pricing based on customer tier, service complexity, distance | Requires pricing engine |
| 7 | Customer Portal | Self-service portal for customers to request services and track progress | Requires customer-facing module |
| 8 | Service Analytics | Service profitability analysis, technician utilization, material consumption trends | Requires reporting module extension |
| 9 | Material Markup Configuration | Configurable markup % for pass-through billing of consumed materials | Deferred to invoicing addendum |
| 10 | Multi-Currency on Service Orders | Service orders in customer currency with dual-amount storage | Integration with Addendum 35 — will follow same pattern as SO/PO currency support |

---

## Appendix A — Entity Relationship Summary

```
lookups.Products (MODIFIED)
  ├── service_invoicing_policy (NEW)
  ├── service_billing_model (NEW)
  ├── estimated_duration_hours (NEW)
  ├── has_service_bom (NEW)
  └── is_subcontractable (NEW)

material.BillOfMaterialLines (MODIFIED)
  ├── source_type (NEW)
  └── subcontract_supplier_id (NEW)

material.ServiceOrders (NEW)
  ├── → lookups.Products (service_product_id)
  ├── → suppliers.BusinessPartners (customer_id)
  ├── → material.BillOfMaterials (bom_id)
  ├── → warehouse.Warehouses (warehouse_id)
  ├── → auth.Users (assigned_user_id)
  └── ← material.ServiceMaterialRequirements (1:N)

material.ServiceMaterialRequirements (NEW)
  ├── → material.ServiceOrders (service_order_id)
  ├── → material.BillOfMaterialLines (bom_line_id, nullable)
  ├── → lookups.Products (product_id)
  └── → warehouse.Warehouses (warehouse_id)

material.MaterialIssues (MODIFIED)
  ├── production_order_id (RELAXED to NULL)
  ├── service_order_id (NEW)
  └── issue_source_type (NEW)

material.MaterialIssueLines (MODIFIED)
  ├── pmr_id (RELAXED to NULL)
  └── smr_id (NEW)

material.ServiceLedgerEntries (NEW)
  ├── → material.ServiceOrders (service_order_id)
  └── → lookups.Products (product_id)

demand.FulfillmentRequirements (MODIFIED)
  ├── service_required_quantity (NEW)
  └── service_order_id (NEW)
```

## Appendix B — Allocation Engine Integration Points

The existing Allocation Engine (Addendum 30 §14) requires **no code changes** for Service Order support. The following integration points are already designed:

| Integration Point | Status | Reference |
|---|---|---|
| `AllocationDemandType.ServiceOrder = 3` | Already defined | ADD-030 §14.5 |
| `DemandSourceType.ServiceOrder = 3` on SupplyRequirements | Already defined | ADD-030 §13.1 |
| `IAllocationEngine.AllocateForDemandAsync()` | Works with any demand type | ADD-030 §14.6 |
| `IAllocationEngine.ConsumeAsync()` | Works with any allocation | ADD-030 §14.6 |
| `IAllocationEngine.ReleaseAsync()` | Works for cancellation | ADD-030 §14.6 |
| Service Order listed as demand source in architecture | Already documented | ADD-030 §14.1 diagram |

**The only registration needed:** Register `ServiceOrderDemandHandler` in DI as a handler for `AllocationDemandType.ServiceOrder`.

---

*End of FSD Addendum 36*
