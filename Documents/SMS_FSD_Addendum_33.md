# SUPPLY MANAGEMENT SYSTEM — Functional Specification Document

## FSD Addendum 33 — Fulfillment Routes & Sale Order Delivery Integration

| Property | Value |
|---|---|
| Document ID | SMS-FSD-ADD-033 |
| Version | 1.0 |
| Date | 2026-10-03 |
| Status | Draft |
| Author | System Architect |
| Classification | Internal — Development |
| Depends On | SMS-FSD-ADD-032 (Sales Pre-Order Pipeline), Logistics Module (delivery_orders, pick_lists, shipment_packages) |
| Architecture | .NET 8 / EF Core 8 / SQL Server 2022 / React 18 |
| Change Items | 5 changes (C1–C5) |
| Impact | 4 migrations, 2 modified entities, 3 new tables, 5 new/modified UI panels, 1 Hangfire job |

---

## Table of Contents

1. [Purpose & Scope](#1-purpose--scope)
2. [Existing Delivery Module Confirmation](#2-existing-delivery-module-confirmation)
3. [C1 — Fulfillment Route Configuration](#3-c1--fulfillment-route-configuration)
4. [C2 — Product Variant Route Assignment](#4-c2--product-variant-route-assignment)
5. [C3 — Sale Order Line Route Selection & Confirmation Gate](#5-c3--sale-order-line-route-selection--confirmation-gate)
6. [C4 — Delivery Order Creation from Sale Order](#6-c4--delivery-order-creation-from-sale-order)
7. [C5 — Route-Aware State Machine Transitions](#7-c5--route-aware-state-machine-transitions)
8. [Database Migrations](#8-database-migrations)
9. [API Changes](#9-api-changes)
10. [UI Wireframes & Specifications](#10-ui-wireframes--specifications)
11. [Business Rules](#11-business-rules)
12. [Test Scenarios](#12-test-scenarios)
13. [Development Phases](#13-development-phases)

---

## 1. Purpose & Scope

**This addendum introduces Fulfillment Routes — a configurable mechanism that determines the warehouse operations required to fulfill each product on a Sale Order.** Fulfillment Routes are inspired by Odoo's route system and define whether a product requires picking only (customer collects), pick-and-ship, or the full pick-pack-ship cycle.

### 1.1 Design Principles

- **Route-Driven Fulfillment** — The fulfillment path (which warehouse steps are executed) is determined by the route, not hard-coded. Routes are defined per organization and assigned at the product variant level.
- **Override at Sale Order** — Each SO line can override the variant's default route, giving salespeople flexibility when the customer's delivery needs differ from the norm.
- **Confirmation Gate** — A Sale Order cannot be confirmed unless every line has a fulfillment route, either inherited from the product variant or explicitly set on the SO line. This ensures the warehouse always knows what to do.
- **Reuse Existing Infrastructure** — Delivery orders, pick lists, shipment packages, and the 15-status state machine already exist. This addendum extends them, not replaces them.

### 1.2 Changes Summary

| Change ID | Title | Category |
|---|---|---|
| C1 | Fulfillment Route Configuration | New entity — FulfillmentRoutes & FulfillmentRouteSteps |
| C2 | Product Variant Route Assignment | Modify entity — add fulfillment_route_id to ProductVariants |
| C3 | Sale Order Line Route Selection & Confirmation Gate | Modify entity — add fulfillment_route_id to SaleOrderLines + validation |
| C4 | Delivery Order Creation from Sale Order | New service — SO confirmation triggers delivery order(s) grouped by route |
| C5 | Route-Aware State Machine Transitions | Modify service — delivery orders skip steps not in their route |

---

## 2. Existing Delivery Module Confirmation

**The following logistics module entities and services are already implemented and are the foundation for this addendum. No changes are needed to these structures — only extensions.**

### 2.1 Existing Schema (logistics)

| Entity | Purpose | Key Fields |
|---|---|---|
| delivery_orders | Header for outbound/inbound delivery | delivery_order_id, org_id, from_source_type (PO/SRO/MIV/Transfer), status (15-state machine), warehouse_id |
| delivery_order_lines | Line items linking to source documents | delivery_order_line_id, delivery_order_id, variant_id, quantity, fulfilled_qty |
| pick_lists | Generated picking instructions | pick_list_id, delivery_order_id, FEFO allocation |
| pick_list_lines | Per-line pick instructions with bin locations | pick_list_line_id, zone, bin, lot_id, quantity |
| shipment_packages | Handling units (cartons, pallets) | package_id, hu_barcode, parent_package_id (nesting) |
| package_contents | Links packages to delivery lines | package_content_id, package_id, delivery_order_line_id, quantity |
| consignments | Carrier booking unit | consignment_id, carrier_account_id, 16-status state machine |
| consignment_stops | Pickup/delivery waypoints | stop_id, consignment_id, stop_type, address_id |

### 2.2 Existing Delivery State Machine (15 Statuses)

```
DRAFT → RELEASED → PICKING → PICKED → PACKED → STAGED
  → PENDING_APPROVAL → GOODS_ISSUED → IN_TRANSIT → AT_HUB
  → OUT_FOR_DELIVERY → DELIVERY_ATTEMPTED → DELIVERED
  → COMPLETED → CANCELLED
```

### 2.3 Existing from_source_type Values

| Source Type | Description | Currently Implemented |
|---|---|---|
| PO | Purchase Order receipt | ✅ Yes |
| SRO | Stock Return Order | ✅ Yes |
| MIV | Manual Issue Voucher | ✅ Yes |
| Transfer | Inter-warehouse transfer | ✅ Yes |
| **SALE_ORDER** | **Sale Order outbound delivery** | **❌ To be added in this addendum** |

### 2.4 Existing IStockReservationService

```csharp
// Already implemented in SMS.Shared — no changes needed
public interface IStockReservationService
{
    Task<ReservationResult> Reserve(ReserveRequest request);
    Task Release(long reservationId);
    Task Consume(long reservationId);
}

// ReservationSourceType enum already includes:
// MIR, DELIVERY, SALES_ORDER
```

### 2.5 Existing Permissions (Logistics)

| Permission | Description |
|---|---|
| DELIVERY_VIEW | View delivery orders |
| DELIVERY_EDIT | Create/edit delivery orders |
| DELIVERY_RELEASE | Release delivery for picking |
| DELIVERY_CANCEL | Cancel a delivery order |
| PICKING | Access pick list |
| PICK_CONFIRM | Confirm pick quantities |
| PACKING | Access packing station |
| PACK_CONFIRM | Confirm packed items |
| DISPATCH | Dispatch for shipment |
| GOODS_ISSUE | Post goods issue (accounting event) |

> **📝 NOTE:** All existing logistics entities, state machines, services, and permissions are confirmed as-is. This addendum adds to them without modifying existing behavior.

---

## 3. C1 — Fulfillment Route Configuration

### 3.1 Overview

A Fulfillment Route is an organization-defined template that specifies which warehouse operations are required to fulfill a product. Each route has an ordered list of steps drawn from the existing delivery state machine.

Three seed routes cover the most common patterns:

| Route | Steps | Use Case |
|---|---|---|
| PICK_ONLY | Pick | Customer collects from warehouse; no packing or shipping |
| PICK_AND_SHIP | Pick → Goods Issue → Ship | Standard items that don't need packing |
| PICK_PACK_SHIP | Pick → Pack → Goods Issue → Ship | Items requiring packing (boxing, palletizing) before shipment |

Organizations can create custom routes beyond these three seeds.

### 3.2 Schema — FulfillmentRoutes

**New Table:** `logistics.FulfillmentRoutes`

| Column | Type | Nullable | Default | Description |
|---|---|---|---|---|
| fulfillment_route_id | INT IDENTITY | NOT NULL | PK | Primary key |
| org_id | INT | NOT NULL | FK → tenants.Organizations | Tenant discriminator |
| code | NVARCHAR(30) | NOT NULL | | Short code (e.g., PICK_ONLY, PICK_AND_SHIP) |
| name | NVARCHAR(100) | NOT NULL | | Display name |
| description | NVARCHAR(500) | NULL | | Explanation of when to use this route |
| is_default | BIT | NOT NULL | 0 | Whether this is the org's default route (at most one per org) |
| is_active | BIT | NOT NULL | 1 | Soft delete |
| is_system | BIT | NOT NULL | 0 | TRUE for seed routes — cannot be deleted |
| requires_packing | BIT | NOT NULL | 0 | Computed convenience flag — TRUE if route includes PACK step |
| requires_shipping | BIT | NOT NULL | 0 | Computed convenience flag — TRUE if route includes SHIP step |
| display_order | INT | NOT NULL | 0 | Sort order in dropdowns |
| created_at | DATETIME2 | NOT NULL | SYSUTCDATETIME() | |
| updated_at | DATETIME2 | NOT NULL | SYSUTCDATETIME() | |

**Indexes:**

| Index | Columns | Type |
|---|---|---|
| UQ_FulfillmentRoutes_OrgCode | org_id, code | Unique |
| IX_FulfillmentRoutes_OrgDefault | org_id, is_default | Filtered (WHERE is_default = 1) |

### 3.3 Schema — FulfillmentRouteSteps

**New Table:** `logistics.FulfillmentRouteSteps`

| Column | Type | Nullable | Default | Description |
|---|---|---|---|---|
| route_step_id | INT IDENTITY | NOT NULL | PK | Primary key |
| fulfillment_route_id | INT | NOT NULL | FK → FulfillmentRoutes | Parent route |
| org_id | INT | NOT NULL | FK → tenants.Organizations | Tenant discriminator |
| step_code | NVARCHAR(20) | NOT NULL | | One of: PICK, PACK, STAGE, APPROVAL, GOODS_ISSUE, SHIP |
| step_order | INT | NOT NULL | | Execution order (1, 2, 3…) |
| is_mandatory | BIT | NOT NULL | 1 | Whether this step can be skipped at runtime |
| description | NVARCHAR(200) | NULL | | Step description or instructions |

**Indexes:**

| Index | Columns | Type |
|---|---|---|
| UQ_FulfillmentRouteSteps_RouteOrder | fulfillment_route_id, step_order | Unique |
| UQ_FulfillmentRouteSteps_RouteStep | fulfillment_route_id, step_code | Unique |

### 3.4 Step Code Definitions

Each step_code maps to a segment of the existing 15-status delivery state machine:

| step_code | Delivery Statuses Involved | Description |
|---|---|---|
| PICK | RELEASED → PICKING → PICKED | Generate pick list, warehouse worker picks items |
| PACK | PICKED → PACKED | Pack picked items into handling units (shipment_packages) |
| STAGE | PACKED → STAGED | Move packed items to staging/loading area |
| APPROVAL | STAGED → PENDING_APPROVAL | Manager approval before dispatch (optional) |
| GOODS_ISSUE | (previous) → GOODS_ISSUED | Post goods issue — stock decremented, COGS posted |
| SHIP | GOODS_ISSUED → IN_TRANSIT → … → DELIVERED | Create consignment, carrier booking, tracking |

### 3.5 Seed Route Definitions

#### PICK_ONLY — Customer Collects

| step_order | step_code | is_mandatory |
|---|---|---|
| 1 | PICK | YES |
| 2 | GOODS_ISSUE | YES |

State machine path: `DRAFT → RELEASED → PICKING → PICKED → GOODS_ISSUED → COMPLETED`

Skipped statuses: PACKED, STAGED, PENDING_APPROVAL, IN_TRANSIT through DELIVERED (customer takes physical possession at warehouse).

#### PICK_AND_SHIP — Standard Shipment (No Packing)

| step_order | step_code | is_mandatory |
|---|---|---|
| 1 | PICK | YES |
| 2 | GOODS_ISSUE | YES |
| 3 | SHIP | YES |

State machine path: `DRAFT → RELEASED → PICKING → PICKED → GOODS_ISSUED → IN_TRANSIT → … → DELIVERED → COMPLETED`

Skipped statuses: PACKED, STAGED, PENDING_APPROVAL.

#### PICK_PACK_SHIP — Full Cycle

| step_order | step_code | is_mandatory |
|---|---|---|
| 1 | PICK | YES |
| 2 | PACK | YES |
| 3 | GOODS_ISSUE | YES |
| 4 | SHIP | YES |

State machine path: `DRAFT → RELEASED → PICKING → PICKED → PACKED → GOODS_ISSUED → IN_TRANSIT → … → DELIVERED → COMPLETED`

Skipped statuses: STAGED, PENDING_APPROVAL.

> **📝 NOTE:** Organizations can create custom routes with additional steps. For example, a route with STAGE and APPROVAL for high-value items: PICK → PACK → STAGE → APPROVAL → GOODS_ISSUE → SHIP (uses all 15 statuses).

---

## 4. C2 — Product Variant Route Assignment

### 4.1 Overview

Each product variant can have a default fulfillment route. This tells the system "when this product is sold, here's how it should be fulfilled." The route assignment lives on the product variant because different variants of the same product may require different handling (e.g., a 1kg pack ships directly, but a 500kg bulk variant requires packing onto pallets).

### 4.2 Schema Change — ProductVariants

**Modify:** `inventory.ProductVariants`

#### ProductVariants — New Column (ADD)

| Column | Type | Nullable | Default | Description |
|---|---|---|---|---|
| fulfillment_route_id | INT | NULL | FK → logistics.FulfillmentRoutes | Default fulfillment route for this variant |

**New Index:**

| Index | Columns | Type |
|---|---|---|
| IX_ProductVariants_FulfillmentRoute | fulfillment_route_id | Non-unique, filtered (WHERE NOT NULL) |

### 4.3 UI — Product Variant Form

The fulfillment route is set on the product variant form in the existing "Logistics" or "Inventory" tab:

```
┌──────────────────────────────────────────────────────────┐
│  Product Variant: Steel Rod 10mm - 6M Length              │
│  ──────────────────────────────────────────────────────  │
│  [General] [Pricing] [Inventory] [Logistics] [History]    │
│                                       ▲ active            │
│  ──────────────────────────────────────────────────────  │
│                                                           │
│  Fulfillment Route                                        │
│  ┌──────────────────────────────┐                        │
│  │ Pick & Ship (PICK_AND_SHIP)  ▼│                        │
│  └──────────────────────────────┘                        │
│  Steps: Pick → Goods Issue → Ship                        │
│                                                           │
│  ⓘ This route determines the warehouse operations        │
│    when this variant is sold. Can be overridden per       │
│    Sale Order line.                                       │
│                                                           │
│  Weight & Dimensions                                      │
│  ┌──────────┐ ┌──────────┐ ┌──────┐ ┌──────┐ ┌──────┐  │
│  │ Net Wt   │ │ Gross Wt │ │ L    │ │ W    │ │ H    │  │
│  │ 12.5 kg  │ │ 13.0 kg  │ │ 6 m  │ │ 10mm │ │ 10mm │  │
│  └──────────┘ └──────────┘ └──────┘ └──────┘ └──────┘  │
│                                                           │
│  Storage                                                  │
│  ┌──────────────────┐ ┌────────────────┐                 │
│  │ Default Zone     │ │ Storage Class  │                 │
│  │ Zone A — Racks   │ │ Standard       │                 │
│  └──────────────────┘ └────────────────┘                 │
└──────────────────────────────────────────────────────────┘
```

### 4.4 Bulk Assignment

For organizations setting up routes across many variants, a bulk assignment endpoint allows setting the route by product category:

```
POST /api/fulfillment-routes/{routeId}/assign-by-category
Body: { "product_category_id": 15 }
```

This sets fulfillment_route_id on all variants under that category that currently have NULL (does not overwrite existing assignments).

---

## 5. C3 — Sale Order Line Route Selection & Confirmation Gate

### 5.1 Overview

Two changes to Sale Order lines:

1. **Route Column** — Each SO line shows the effective fulfillment route (inherited from variant or overridden) and allows the salesperson to change it.
2. **Confirmation Gate** — The SO cannot transition from DRAFT to CONFIRMED unless every line has a resolved fulfillment route.

### 5.2 Schema Change — SaleOrderLines

**Modify:** `demand.SaleOrderLines`

#### SaleOrderLines — New Column (ADD)

| Column | Type | Nullable | Default | Description |
|---|---|---|---|---|
| fulfillment_route_id | INT | NULL | FK → logistics.FulfillmentRoutes | Route override for this line. NULL = inherit from variant. |

**New Index:**

| Index | Columns | Type |
|---|---|---|
| IX_SaleOrderLines_FulfillmentRoute | fulfillment_route_id | Non-unique, filtered (WHERE NOT NULL) |

### 5.3 Effective Route Resolution

The effective route for a SO line is resolved in this priority order:

```csharp
// Pseudocode — EffectiveRouteResolver
public int? ResolveEffectiveRouteId(SaleOrderLine line)
{
    // Priority 1: Explicit override on the SO line
    if (line.FulfillmentRouteId.HasValue)
        return line.FulfillmentRouteId;

    // Priority 2: Default route on the product variant
    var variant = _variantRepo.GetById(line.VariantId);
    if (variant.FulfillmentRouteId.HasValue)
        return variant.FulfillmentRouteId;

    // Priority 3: Organization default route
    var orgDefault = _routeRepo.GetOrgDefault(line.OrgId);
    if (orgDefault != null)
        return orgDefault.FulfillmentRouteId;

    // No route found — this line will block confirmation
    return null;
}
```

**Resolution priority:**

| Priority | Source | Description |
|---|---|---|
| 1 (highest) | SO Line override | Salesperson explicitly chose a route for this line |
| 2 | Product Variant default | Route configured on the variant master data |
| 3 | Organization default route | The route with is_default = 1 for this org |
| 4 | NULL — blocks confirmation | No route resolved anywhere |

### 5.4 Confirmation Gate Validation

When a user attempts to confirm a Sale Order (DRAFT → CONFIRMED), the system validates:

```csharp
// Pseudocode — SaleOrderConfirmationValidator
public ValidationResult ValidateForConfirmation(SaleOrder order)
{
    var errors = new List<ValidationError>();
    var missingRouteLines = new List<int>();

    foreach (var line in order.Lines)
    {
        var effectiveRouteId = _routeResolver.ResolveEffectiveRouteId(line);

        if (effectiveRouteId == null)
        {
            missingRouteLines.Add(line.LineNumber);
        }
        else
        {
            // Validate the route is still active
            var route = _routeRepo.GetById(effectiveRouteId.Value);
            if (route == null || !route.IsActive)
            {
                errors.Add(new ValidationError(
                    $"Line {line.LineNumber}: fulfillment route '{route?.Code}' is inactive or deleted"));
            }
        }
    }

    if (missingRouteLines.Any())
    {
        errors.Add(new ValidationError(
            $"Cannot confirm: lines {string.Join(", ", missingRouteLines)} " +
            $"have no fulfillment route. Assign a route on each line or " +
            $"set a default route on the product variant."));
    }

    return new ValidationResult(errors);
}
```

**Confirmation is blocked when:**

- Any SO line has no effective route (not on line, not on variant, no org default)
- Any SO line's effective route is inactive or deleted

**Confirmation proceeds when:**

- Every SO line resolves to an active fulfillment route (from any of the three priority levels)

### 5.5 UI — Sale Order Lines with Route Column

```
┌──────────────────────────────────────────────────────────────────┐
│  Sale Order: SO-1085                    Status: DRAFT             │
│  ──────────────────────────────────────────────────────────────  │
│  [Header] [Lines] [Fulfillment] [Attachments] [History]          │
│              ▲ active                                             │
│  ──────────────────────────────────────────────────────────────  │
│                                                                   │
│  ┌──────────────────────────────────────────────────────────────┐│
│  │    Product          Qty   Price    Route              Action  ││
│  │─────────────────────────────────────────────────────────────││
│  │ 1  Steel Pipes      500   $450    [Pick Only       ▼] ⓥ     ││
│  │    └ Source: variant default                                  ││
│  │ 2  Copper Wire       600   $820    [Pick Pack Ship  ▼] ✎     ││
│  │    └ Source: overridden on SO line                            ││
│  │ 3  Synth Hyd Oil    100   $2800   [Pick & Ship     ▼] ⓥ     ││
│  │    └ Source: variant default                                  ││
│  │ 4  Custom Gasket     50   $150    [— Select Route —  ▼] ⚠    ││
│  │    └ ⚠ No route defined — order cannot be confirmed          ││
│  └──────────────────────────────────────────────────────────────┘│
│                                                                   │
│  Legend: ⓥ = inherited from variant  ✎ = overridden on line      │
│          ⚠ = missing route (blocking)                             │
│                                                                   │
│  [Confirm Order]  ← DISABLED: 1 line missing fulfillment route   │
│                                                                   │
│  Delivery Preview:                                                │
│  ┌────────────────────────────────────────────────────────────┐  │
│  │ On confirmation, 3 delivery orders will be created:        │  │
│  │  • DO-1: Steel Pipes (Pick Only — customer collects)       │  │
│  │  • DO-2: Copper Wire (Pick → Pack → Goods Issue → Ship)   │  │
│  │  • DO-3: Synth Hyd Oil (Pick → Goods Issue → Ship)        │  │
│  │  • Line 4: ⚠ unroutable — assign route first              │  │
│  └────────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────┘
```

> **⚠️ CRITICAL:** The "Confirm Order" button is DISABLED and shows a tooltip explaining which lines are missing routes. It is not hidden — the salesperson needs to see it and understand what's blocking them.

### 5.6 Delivery Preview

Before confirmation, the UI shows a "Delivery Preview" panel that groups SO lines by their effective route and shows how many delivery orders will be created. This preview is computed on the fly (not persisted) and updates as the salesperson changes routes on lines.

---

## 6. C4 — Delivery Order Creation from Sale Order

### 6.1 Overview

When a Sale Order is confirmed, the system automatically creates Delivery Orders grouped by fulfillment route. Lines with the same route go into the same Delivery Order. This extends the existing delivery_orders entity with `from_source_type = 'SALE_ORDER'`.

### 6.2 Schema Change — DeliveryOrders

**Modify:** `logistics.delivery_orders`

#### delivery_orders — Extend from_source_type ENUM

The existing `from_source_type` column (NVARCHAR) accepts a new value:

| Value | Description | Direction |
|---|---|---|
| PO | Purchase Order receipt (existing) | Inbound |
| SRO | Stock Return Order (existing) | Inbound |
| MIV | Manual Issue Voucher (existing) | Outbound |
| Transfer | Inter-warehouse transfer (existing) | Both |
| **SALE_ORDER** | **Sale Order outbound delivery (new)** | **Outbound** |

#### delivery_orders — New Columns (ADD)

| Column | Type | Nullable | Default | Description |
|---|---|---|---|---|
| sale_order_id | BIGINT | NULL | FK → demand.SaleOrders | Originating Sale Order (NULL for non-SO deliveries) |
| fulfillment_route_id | INT | NULL | FK → logistics.FulfillmentRoutes | Route governing this delivery's steps |

**New Indexes:**

| Index | Columns | Type |
|---|---|---|
| IX_DeliveryOrders_SaleOrder | sale_order_id | Non-unique, filtered (WHERE sale_order_id IS NOT NULL) |
| IX_DeliveryOrders_Route | fulfillment_route_id | Non-unique, filtered (WHERE NOT NULL) |

#### delivery_order_lines — New Column (ADD)

| Column | Type | Nullable | Default | Description |
|---|---|---|---|---|
| sale_order_line_id | BIGINT | NULL | FK → demand.SaleOrderLines | Originating SO line (NULL for non-SO deliveries) |

### 6.3 Delivery Order Creation Logic

```csharp
// Pseudocode — SaleOrderDeliveryCreator
public async Task<List<DeliveryOrder>> CreateDeliveriesFromSaleOrder(SaleOrder order)
{
    // Group SO lines by their effective fulfillment route
    var linesByRoute = order.Lines
        .GroupBy(l => _routeResolver.ResolveEffectiveRouteId(l)!.Value)
        .ToList();

    var deliveries = new List<DeliveryOrder>();

    foreach (var group in linesByRoute)
    {
        var route = await _routeRepo.GetByIdAsync(group.Key);

        var delivery = new DeliveryOrder
        {
            OrgId = order.OrgId,
            FromSourceType = "SALE_ORDER",
            SaleOrderId = order.SaleOrderId,
            FulfillmentRouteId = route.FulfillmentRouteId,
            WarehouseId = order.WarehouseId,
            Status = "DRAFT",
            ExpectedDate = group.Min(l => l.RequestedDeliveryDate),
            PartnerId = order.PartnerId,
            DeliveryAddressId = order.DeliveryAddressId
        };

        foreach (var soLine in group)
        {
            delivery.Lines.Add(new DeliveryOrderLine
            {
                VariantId = soLine.VariantId,
                Quantity = soLine.Quantity,
                SaleOrderLineId = soLine.SaleOrderLineId,
                OrgId = order.OrgId
            });
        }

        deliveries.Add(delivery);
    }

    await _deliveryRepo.CreateBatchAsync(deliveries);

    // Update SO with delivery reference
    order.HasDeliveries = true;

    return deliveries;
}
```

**Grouping Rules:**

| Scenario | Lines | Delivery Orders Created |
|---|---|---|
| All lines same route | 3 lines, all PICK_AND_SHIP | 1 delivery order with 3 lines |
| Mixed routes | 1× PICK_ONLY, 1× PICK_PACK_SHIP, 1× PICK_AND_SHIP | 3 delivery orders, 1 line each |
| Two lines same route, one different | 2× PICK_AND_SHIP, 1× PICK_PACK_SHIP | 2 delivery orders (2 lines + 1 line) |

### 6.4 Punjab Group Example

```
Sale Order SO-1085 confirmed
├─ Line 1: Steel Pipes 500 @ $450     Route: PICK_ONLY
├─ Line 2: Copper Wire 600 @ $820     Route: PICK_PACK_SHIP
└─ Line 3: Synth Hyd Oil 100 @ $2800  Route: PICK_AND_SHIP

System creates 3 Delivery Orders:

DO-20261003-001 (Route: PICK_ONLY)
├── from_source_type = SALE_ORDER
├── sale_order_id = SO-1085
├── fulfillment_route_id = PICK_ONLY
├── Line: Steel Pipes × 500
└── Steps: Pick → Goods Issue → Complete
    (customer sends truck to collect)

DO-20261003-002 (Route: PICK_PACK_SHIP)
├── from_source_type = SALE_ORDER
├── sale_order_id = SO-1085
├── fulfillment_route_id = PICK_PACK_SHIP
├── Line: Copper Wire × 600
└── Steps: Pick → Pack (spool into boxes) → Goods Issue → Ship → Deliver
    (carrier booked, tracking generated)

DO-20261003-003 (Route: PICK_AND_SHIP)
├── from_source_type = SALE_ORDER
├── sale_order_id = SO-1085
├── fulfillment_route_id = PICK_AND_SHIP
├── Line: Synth Hyd Oil × 100
└── Steps: Pick (drums from yard) → Goods Issue → Ship → Deliver
    (no packing needed — drums ship as-is)
```

### 6.5 Sale Order ↔ Delivery Order Linkage

```
demand.SaleOrders ──── 1:N ────→ logistics.delivery_orders
demand.SaleOrderLines ── 1:1 ──→ logistics.delivery_order_lines
```

The SO detail page's "Fulfillment" tab shows all delivery orders with their statuses. Each delivery order links back to its SO.

---

## 7. C5 — Route-Aware State Machine Transitions

### 7.1 Overview

The existing 15-status delivery state machine remains unchanged. What changes is which statuses a delivery order *passes through* based on its fulfillment route. Statuses not in the route are skipped — the state machine jumps over them.

### 7.2 Step-to-Status Mapping

Each step_code in the route maps to the delivery statuses it covers. When a step is not in the route, its statuses are skipped:

| step_code | Statuses Covered | If Skipped |
|---|---|---|
| PICK | RELEASED, PICKING, PICKED | ❌ Never skipped — always required |
| PACK | PACKED | Jump from PICKED directly to next step |
| STAGE | STAGED | Jump from PACKED (or PICKED) to next step |
| APPROVAL | PENDING_APPROVAL | Jump from STAGED (or previous) to next step |
| GOODS_ISSUE | GOODS_ISSUED | ❌ Never skipped — always required |
| SHIP | IN_TRANSIT, AT_HUB, OUT_FOR_DELIVERY, DELIVERY_ATTEMPTED, DELIVERED | Jump from GOODS_ISSUED directly to COMPLETED |

### 7.3 Transition Logic

```csharp
// Pseudocode — RouteAwareDeliveryStateMachine
public string GetNextStatus(DeliveryOrder delivery, string currentStatus)
{
    var route = _routeRepo.GetWithSteps(delivery.FulfillmentRouteId);
    var routeStepCodes = route.Steps
        .OrderBy(s => s.StepOrder)
        .Select(s => s.StepCode)
        .ToHashSet();

    // Standard transition map (full 15-status chain)
    var fullChain = new[]
    {
        "DRAFT", "RELEASED", "PICKING", "PICKED",
        "PACKED", "STAGED", "PENDING_APPROVAL",
        "GOODS_ISSUED", "IN_TRANSIT", "AT_HUB",
        "OUT_FOR_DELIVERY", "DELIVERY_ATTEMPTED",
        "DELIVERED", "COMPLETED"
    };

    // Map statuses to their step_code
    var statusToStep = new Dictionary<string, string>
    {
        ["RELEASED"] = "PICK", ["PICKING"] = "PICK", ["PICKED"] = "PICK",
        ["PACKED"] = "PACK",
        ["STAGED"] = "STAGE",
        ["PENDING_APPROVAL"] = "APPROVAL",
        ["GOODS_ISSUED"] = "GOODS_ISSUE",
        ["IN_TRANSIT"] = "SHIP", ["AT_HUB"] = "SHIP",
        ["OUT_FOR_DELIVERY"] = "SHIP", ["DELIVERY_ATTEMPTED"] = "SHIP",
        ["DELIVERED"] = "SHIP"
    };

    // Find current position in full chain
    var currentIndex = Array.IndexOf(fullChain, currentStatus);

    // Walk forward, skipping statuses whose step is not in the route
    for (int i = currentIndex + 1; i < fullChain.Length; i++)
    {
        var candidateStatus = fullChain[i];

        // DRAFT and COMPLETED are always valid
        if (candidateStatus == "COMPLETED")
            return candidateStatus;

        // Check if this status's step is in the route
        if (statusToStep.TryGetValue(candidateStatus, out var stepCode))
        {
            if (routeStepCodes.Contains(stepCode))
                return candidateStatus;
            // else: skip this status, continue to next
        }
    }

    return currentStatus; // should not reach here
}
```

### 7.4 Route Path Examples

**PICK_ONLY (steps: PICK, GOODS_ISSUE):**

```
DRAFT → RELEASED → PICKING → PICKED → GOODS_ISSUED → COMPLETED
                                        ↑ skips PACKED, STAGED, APPROVAL
                                                        ↑ skips SHIP statuses
```

**PICK_AND_SHIP (steps: PICK, GOODS_ISSUE, SHIP):**

```
DRAFT → RELEASED → PICKING → PICKED → GOODS_ISSUED → IN_TRANSIT → … → DELIVERED → COMPLETED
                                ↑ skips PACKED, STAGED, APPROVAL
```

**PICK_PACK_SHIP (steps: PICK, PACK, GOODS_ISSUE, SHIP):**

```
DRAFT → RELEASED → PICKING → PICKED → PACKED → GOODS_ISSUED → IN_TRANSIT → … → DELIVERED → COMPLETED
                                         ↑ skips STAGED, APPROVAL
```

**Full route with all steps (custom):**

```
DRAFT → RELEASED → PICKING → PICKED → PACKED → STAGED → PENDING_APPROVAL → GOODS_ISSUED → IN_TRANSIT → … → DELIVERED → COMPLETED
```

### 7.5 UI — Delivery Order Step Tracker

The delivery order detail page shows a visual step tracker that only includes the steps in the delivery's route. Skipped steps do not appear:

```
PICK_AND_SHIP route:

  ● Pick ──────→ ○ Goods Issue ──────→ ○ Ship ──────→ ○ Complete
  (current)

PICK_PACK_SHIP route:

  ✓ Pick ──────→ ● Pack ──────→ ○ Goods Issue ──────→ ○ Ship ──────→ ○ Complete
  (done)         (current)
```

### 7.6 Cancellation

Cancellation (→ CANCELLED) is available from any status regardless of route, following existing behavior. If the delivery has a linked Sale Order, the SO's delivery status is recalculated.

---

## 8. Database Migrations

**Four migrations are required for this addendum. They are additive and non-breaking.**

### 8.1 Migration Order

| # | Migration Name | Schema | Type | Description |
|---|---|---|---|---|
| M1 | CreateFulfillmentRoutes | logistics | CREATE TABLE | FulfillmentRoutes + FulfillmentRouteSteps + seed data |
| M2 | AddFulfillmentRouteToProductVariants | inventory | ALTER TABLE | Add fulfillment_route_id FK to ProductVariants |
| M3 | AddFulfillmentRouteToSaleOrderLines | demand | ALTER TABLE | Add fulfillment_route_id FK to SaleOrderLines |
| M4 | ExtendDeliveryOrdersForSaleOrder | logistics | ALTER TABLE | Add sale_order_id, fulfillment_route_id to delivery_orders; add sale_order_line_id to delivery_order_lines |

### 8.2 Migration Details

#### M1 — CreateFulfillmentRoutes

```sql
CREATE TABLE logistics.FulfillmentRoutes (
    fulfillment_route_id INT IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_FulfillmentRoutes PRIMARY KEY,
    org_id INT NOT NULL
        CONSTRAINT FK_FulfillmentRoutes_Org FOREIGN KEY REFERENCES tenants.Organizations(org_id),
    code NVARCHAR(30) NOT NULL,
    name NVARCHAR(100) NOT NULL,
    description NVARCHAR(500) NULL,
    is_default BIT NOT NULL CONSTRAINT DF_FulfillmentRoutes_IsDefault DEFAULT 0,
    is_active BIT NOT NULL CONSTRAINT DF_FulfillmentRoutes_IsActive DEFAULT 1,
    is_system BIT NOT NULL CONSTRAINT DF_FulfillmentRoutes_IsSystem DEFAULT 0,
    requires_packing BIT NOT NULL CONSTRAINT DF_FulfillmentRoutes_ReqPack DEFAULT 0,
    requires_shipping BIT NOT NULL CONSTRAINT DF_FulfillmentRoutes_ReqShip DEFAULT 0,
    display_order INT NOT NULL CONSTRAINT DF_FulfillmentRoutes_DisplayOrder DEFAULT 0,
    created_at DATETIME2 NOT NULL CONSTRAINT DF_FulfillmentRoutes_Created DEFAULT SYSUTCDATETIME(),
    updated_at DATETIME2 NOT NULL CONSTRAINT DF_FulfillmentRoutes_Updated DEFAULT SYSUTCDATETIME(),

    CONSTRAINT UQ_FulfillmentRoutes_OrgCode UNIQUE (org_id, code)
);

CREATE INDEX IX_FulfillmentRoutes_OrgDefault ON logistics.FulfillmentRoutes(org_id, is_default)
    WHERE is_default = 1;

CREATE TABLE logistics.FulfillmentRouteSteps (
    route_step_id INT IDENTITY(1,1) NOT NULL
        CONSTRAINT PK_FulfillmentRouteSteps PRIMARY KEY,
    fulfillment_route_id INT NOT NULL
        CONSTRAINT FK_RouteSteps_Route FOREIGN KEY REFERENCES logistics.FulfillmentRoutes(fulfillment_route_id),
    org_id INT NOT NULL
        CONSTRAINT FK_RouteSteps_Org FOREIGN KEY REFERENCES tenants.Organizations(org_id),
    step_code NVARCHAR(20) NOT NULL,
    step_order INT NOT NULL,
    is_mandatory BIT NOT NULL CONSTRAINT DF_RouteSteps_IsMandatory DEFAULT 1,
    description NVARCHAR(200) NULL,

    CONSTRAINT UQ_RouteSteps_RouteOrder UNIQUE (fulfillment_route_id, step_order),
    CONSTRAINT UQ_RouteSteps_RouteStep UNIQUE (fulfillment_route_id, step_code),
    CONSTRAINT CK_RouteSteps_StepCode CHECK (step_code IN ('PICK','PACK','STAGE','APPROVAL','GOODS_ISSUE','SHIP'))
);

-- Seed data is inserted per org via tenant provisioning stored procedure
-- See §3.5 for seed route definitions
-- The first org's seed routes serve as the template:
--
-- Route 1: PICK_ONLY (is_default=0, is_system=1, requires_packing=0, requires_shipping=0)
--   Steps: (1, PICK), (2, GOODS_ISSUE)
--
-- Route 2: PICK_AND_SHIP (is_default=1, is_system=1, requires_packing=0, requires_shipping=1)
--   Steps: (1, PICK), (2, GOODS_ISSUE), (3, SHIP)
--
-- Route 3: PICK_PACK_SHIP (is_default=0, is_system=1, requires_packing=1, requires_shipping=1)
--   Steps: (1, PICK), (2, PACK), (3, GOODS_ISSUE), (4, SHIP)
```

#### M2 — AddFulfillmentRouteToProductVariants

```sql
ALTER TABLE inventory.ProductVariants
ADD fulfillment_route_id INT NULL
    CONSTRAINT FK_ProductVariants_FulfillmentRoute
    FOREIGN KEY REFERENCES logistics.FulfillmentRoutes(fulfillment_route_id);

CREATE INDEX IX_ProductVariants_FulfillmentRoute
    ON inventory.ProductVariants(fulfillment_route_id)
    WHERE fulfillment_route_id IS NOT NULL;

-- No backfill needed — existing variants start with NULL (no default route)
-- Organizations should assign routes via bulk assignment or one-by-one
```

#### M3 — AddFulfillmentRouteToSaleOrderLines

```sql
ALTER TABLE demand.SaleOrderLines
ADD fulfillment_route_id INT NULL
    CONSTRAINT FK_SaleOrderLines_FulfillmentRoute
    FOREIGN KEY REFERENCES logistics.FulfillmentRoutes(fulfillment_route_id);

CREATE INDEX IX_SaleOrderLines_FulfillmentRoute
    ON demand.SaleOrderLines(fulfillment_route_id)
    WHERE fulfillment_route_id IS NOT NULL;

-- No backfill needed — existing SO lines pre-date route requirement
-- The confirmation gate (§5.4) only applies to NEW confirmations going forward
```

#### M4 — ExtendDeliveryOrdersForSaleOrder

```sql
-- delivery_orders: add sale_order_id and fulfillment_route_id
ALTER TABLE logistics.delivery_orders
ADD sale_order_id BIGINT NULL
    CONSTRAINT FK_DeliveryOrders_SaleOrder
    FOREIGN KEY REFERENCES demand.SaleOrders(sale_order_id);

ALTER TABLE logistics.delivery_orders
ADD fulfillment_route_id INT NULL
    CONSTRAINT FK_DeliveryOrders_FulfillmentRoute
    FOREIGN KEY REFERENCES logistics.FulfillmentRoutes(fulfillment_route_id);

CREATE INDEX IX_DeliveryOrders_SaleOrder
    ON logistics.delivery_orders(sale_order_id)
    WHERE sale_order_id IS NOT NULL;

CREATE INDEX IX_DeliveryOrders_Route
    ON logistics.delivery_orders(fulfillment_route_id)
    WHERE fulfillment_route_id IS NOT NULL;

-- delivery_order_lines: add sale_order_line_id
ALTER TABLE logistics.delivery_order_lines
ADD sale_order_line_id BIGINT NULL
    CONSTRAINT FK_DOLines_SaleOrderLine
    FOREIGN KEY REFERENCES demand.SaleOrderLines(sale_order_line_id);

CREATE INDEX IX_DOLines_SaleOrderLine
    ON logistics.delivery_order_lines(sale_order_line_id)
    WHERE sale_order_line_id IS NOT NULL;
```

### 8.3 Rollback

| Migration | Rollback Steps |
|---|---|
| M1 | DROP TABLE logistics.FulfillmentRouteSteps, logistics.FulfillmentRoutes (after clearing FKs from M2, M3, M4) |
| M2 | DROP COLUMN fulfillment_route_id from ProductVariants (after clearing FK and index) |
| M3 | DROP COLUMN fulfillment_route_id from SaleOrderLines (after clearing FK and index) |
| M4 | DROP COLUMN sale_order_line_id from delivery_order_lines; DROP COLUMN sale_order_id, fulfillment_route_id from delivery_orders (after clearing FKs and indexes) |

---

## 9. API Changes

### 9.1 New Endpoints — Fulfillment Routes

| Endpoint | Method | Description | Permission |
|---|---|---|---|
| /api/fulfillment-routes | GET | List active routes for org (with steps) | authenticated |
| /api/fulfillment-routes | POST | Create a new route with steps | admin |
| /api/fulfillment-routes/{id} | GET | Get route with steps | authenticated |
| /api/fulfillment-routes/{id} | PUT | Update route name, description, steps | admin |
| /api/fulfillment-routes/{id}/deactivate | PATCH | Soft-delete a route (must not be in use) | admin |
| /api/fulfillment-routes/{id}/set-default | PATCH | Set as org default route | admin |
| /api/fulfillment-routes/{id}/assign-by-category | POST | Bulk assign to variants in a product category | admin |

### 9.2 Modified Endpoints — Product Variants

| Endpoint | Change | Change ID |
|---|---|---|
| GET /api/product-variants/{id} | Return fulfillment_route_id and route summary in response | C2 |
| PUT /api/product-variants/{id} | Accept fulfillment_route_id in update DTO | C2 |
| GET /api/product-variants (list) | Include fulfillment_route_code and fulfillment_route_name in list DTO | C2 |

### 9.3 Modified Endpoints — Sale Orders

| Endpoint | Change | Change ID |
|---|---|---|
| POST /api/sale-orders/{id}/lines | Accept optional fulfillment_route_id per line | C3 |
| PUT /api/sale-orders/{id}/lines/{lineId} | Accept optional fulfillment_route_id update | C3 |
| GET /api/sale-orders/{id}/lines | Return effective_route_id, effective_route_code, route_source (VARIANT/LINE_OVERRIDE/ORG_DEFAULT/NONE) per line | C3 |
| POST /api/sale-orders/{id}/confirm | Validate all lines have routes; on success create delivery orders grouped by route | C3, C4 |
| GET /api/sale-orders/{id}/delivery-preview | Return delivery grouping preview (route groups, line counts) without creating anything | C3 |

### 9.4 Modified Endpoints — Delivery Orders

| Endpoint | Change | Change ID |
|---|---|---|
| GET /api/delivery-orders/{id} | Return fulfillment_route_id, fulfillment_route_code, sale_order_id, allowed_transitions (route-aware) | C4, C5 |
| POST /api/delivery-orders/{id}/advance | Use route-aware state machine to determine next status | C5 |
| GET /api/delivery-orders (list) | Support filter by from_source_type=SALE_ORDER, sale_order_id | C4 |

---

## 10. UI Wireframes & Specifications

### 10.1 Fulfillment Route Management

```
┌───────────────────────────────────────────────────────────────┐
│  Settings → Fulfillment Routes                  [+ New Route]  │
│  ─────────────────────────────────────────────────────────── │
│                                                               │
│  ┌─────────────────────────────────────────────────────────┐ │
│  │ Code            Name              Steps     Default  🔒 │ │
│  │──────────────────────────────────────────────────────── │ │
│  │ PICK_ONLY       Pick Only         Pick→GI      ○    🔒  │ │
│  │ PICK_AND_SHIP   Pick & Ship       Pick→GI→Ship ●    🔒  │ │
│  │ PICK_PACK_SHIP  Pick Pack Ship    Pick→Pk→GI→S ○    🔒  │ │
│  │ HIGH_VALUE      High Value Full   Pk→Pk→St→Ap  ○        │ │
│  │                                   →GI→Ship              │ │
│  └─────────────────────────────────────────────────────────┘ │
│                                                               │
│  🔒 = System route (cannot be deleted)                       │
│  ● = Organization default route                              │
└───────────────────────────────────────────────────────────────┘
```

### 10.2 Fulfillment Route Editor

```
┌──────────────────────────────────────────────────────────────┐
│  Fulfillment Route: PICK_PACK_SHIP                           │
│  ──────────────────────────────────────────────────────────  │
│                                                               │
│  ┌─────────────┐ ┌──────────────────────────────────┐        │
│  │ Code         │ │ Name                             │        │
│  │ PICK_PACK_SH │ │ Pick, Pack & Ship                │        │
│  └─────────────┘ └──────────────────────────────────┘        │
│                                                               │
│  ┌──────────────────────────────────────────────────────┐    │
│  │ Description                                          │    │
│  │ Full cycle — items picked, packed into boxes/pallets, │    │
│  │ then shipped via carrier to customer                  │    │
│  └──────────────────────────────────────────────────────┘    │
│                                                               │
│  Steps (drag to reorder):                                    │
│  ┌──────────────────────────────────────────────────────┐    │
│  │  ① PICK         ☑ Mandatory    Generate pick list    │    │
│  │  ② PACK         ☑ Mandatory    Pack into HUs         │    │
│  │  ③ GOODS_ISSUE  ☑ Mandatory    Post goods issue      │    │
│  │  ④ SHIP         ☑ Mandatory    Book carrier, ship    │    │
│  │                                                       │    │
│  │  [+ Add Step]                                         │    │
│  │  Available: STAGE, APPROVAL                           │    │
│  └──────────────────────────────────────────────────────┘    │
│                                                               │
│  Preview: DRAFT → RELEASED → PICKING → PICKED → PACKED      │
│           → GOODS_ISSUED → IN_TRANSIT → … → DELIVERED        │
│           → COMPLETED                                         │
│                                                               │
│  [☐ Set as organization default]                              │
│                                                               │
│  [Save]  [Cancel]                                             │
└──────────────────────────────────────────────────────────────┘
```

### 10.3 Delivery Order — Route-Aware Step Tracker

```
┌──────────────────────────────────────────────────────────────┐
│  Delivery: DO-20261003-002           Route: Pick Pack Ship    │
│  Sale Order: SO-1085                 Customer: Punjab Group   │
│  ──────────────────────────────────────────────────────────  │
│                                                               │
│  ┌──────────────────────────────────────────────────────────┐│
│  │  ✓ Pick ───→ ● Pack ───→ ○ Goods Issue ───→ ○ Ship     ││
│  │  (done)      (current)                                   ││
│  └──────────────────────────────────────────────────────────┘│
│                                                               │
│  ──────────────────────────────────────────────────────────  │
│  Lines                                                        │
│  ┌──────────────────────────────────────────────────────────┐│
│  │    Product          Qty    Picked  Packed   Shipped      ││
│  │─────────────────────────────────────────────────────────││
│  │ 1  Copper Wire       600   600     0        0            ││
│  └──────────────────────────────────────────────────────────┘│
│                                                               │
│  [Start Packing]                                              │
└──────────────────────────────────────────────────────────────┘
```

### 10.4 Sale Order Fulfillment Tab

```
┌──────────────────────────────────────────────────────────────┐
│  Sale Order: SO-1085              Status: CONFIRMED           │
│  ──────────────────────────────────────────────────────────  │
│  [Header] [Lines] [Fulfillment] [Attachments] [History]      │
│                       ▲ active                                │
│  ──────────────────────────────────────────────────────────  │
│                                                               │
│  Delivery Orders (3)                                          │
│  ┌──────────────────────────────────────────────────────────┐│
│  │ Delivery          Route          Lines  Status    Action  ││
│  │─────────────────────────────────────────────────────────││
│  │ DO-20261003-001   Pick Only      1      ● Picked  [View] ││
│  │   └ Steel Pipes × 500 — customer collecting today        ││
│  │ DO-20261003-002   Pick Pack Ship 1      ● Packing [View] ││
│  │   └ Copper Wire × 600 — packing in progress              ││
│  │ DO-20261003-003   Pick & Ship    1      ● Picking [View] ││
│  │   └ Synth Hyd Oil × 100 — pick list generated            ││
│  └──────────────────────────────────────────────────────────┘│
│                                                               │
│  Overall Fulfillment: 0 of 3 lines delivered                 │
│  ████░░░░░░░░░░░░░░░░ 0%                                    │
└──────────────────────────────────────────────────────────────┘
```

---

## 11. Business Rules

**Consolidated business rules for all 5 changes.**

| Rule ID | Change | Rule Description | Enforcement Point |
|---|---|---|---|
| BR-C1-01 | C1 | Route code must be unique per org | FulfillmentRouteService |
| BR-C1-02 | C1 | At most one route per org can have is_default = 1 | FulfillmentRouteService |
| BR-C1-03 | C1 | Every route must have at least PICK and GOODS_ISSUE steps | FulfillmentRouteService |
| BR-C1-04 | C1 | Step order must be sequential starting from 1, no gaps | FulfillmentRouteService |
| BR-C1-05 | C1 | PICK must always be the first step; GOODS_ISSUE must come before SHIP | FulfillmentRouteService |
| BR-C1-06 | C1 | System routes (is_system = 1) cannot be deleted, only deactivated | FulfillmentRouteService |
| BR-C1-07 | C1 | A route cannot be deactivated while assigned to any active product variant or open SO line | FulfillmentRouteService |
| BR-C1-08 | C1 | Seed routes are inserted per org on tenant provisioning | TenantProvisioningService |
| BR-C1-09 | C1 | requires_packing and requires_shipping are computed from steps on save (PACK step → requires_packing = 1, SHIP step → requires_shipping = 1) | FulfillmentRouteService |
| BR-C2-01 | C2 | Product variant's fulfillment_route_id must reference an active route in the same org | ProductVariantService |
| BR-C2-02 | C2 | Bulk assignment by category only sets NULL variants, does not overwrite existing assignments | FulfillmentRouteService |
| BR-C3-01 | C3 | SO line fulfillment_route_id (when set) must reference an active route in the same org | SaleOrderLineService |
| BR-C3-02 | C3 | SO confirmation (DRAFT → CONFIRMED) requires every line to resolve an effective route | SaleOrderService |
| BR-C3-03 | C3 | Effective route resolution order: SO line override → variant default → org default → NULL (blocks) | EffectiveRouteResolver |
| BR-C3-04 | C3 | SO line route can be changed while SO is in DRAFT; locked after confirmation | SaleOrderLineService |
| BR-C3-05 | C3 | Delivery preview is computed on the fly and not persisted — changes as lines are edited | SaleOrderDeliveryPreviewService |
| BR-C4-01 | C4 | SO confirmation creates delivery orders automatically, grouped by effective route | SaleOrderDeliveryCreator |
| BR-C4-02 | C4 | Lines with the same effective route go into the same delivery order | SaleOrderDeliveryCreator |
| BR-C4-03 | C4 | Delivery orders are created in DRAFT status — require explicit release for picking | SaleOrderDeliveryCreator |
| BR-C4-04 | C4 | delivery_orders.from_source_type = 'SALE_ORDER' for all SO-originated deliveries | SaleOrderDeliveryCreator |
| BR-C4-05 | C4 | Each delivery_order_line links back to its sale_order_line_id for traceability | SaleOrderDeliveryCreator |
| BR-C4-06 | C4 | Cancelling a SO cancels all linked delivery orders that are not yet GOODS_ISSUED | SaleOrderService |
| BR-C4-07 | C4 | A delivery order past GOODS_ISSUED cannot be auto-cancelled by SO cancellation — requires manual reversal | SaleOrderService |
| BR-C5-01 | C5 | Delivery state machine skips statuses whose step_code is not in the delivery's fulfillment route | RouteAwareDeliveryStateMachine |
| BR-C5-02 | C5 | PICK and GOODS_ISSUE steps are always required — routes without them are rejected at creation | FulfillmentRouteService |
| BR-C5-03 | C5 | The "Advance" action on a delivery determines the next valid status based on the route, not the full 15-status chain | DeliveryOrderService |
| BR-C5-04 | C5 | Delivery order detail UI shows only the steps in the delivery's route (step tracker) | Delivery Detail UI |
| BR-C5-05 | C5 | Cancellation (→ CANCELLED) is always available regardless of route | DeliveryOrderService |

---

## 12. Test Scenarios

| Test ID | Change | Scenario | Expected Result |
|---|---|---|---|
| T-C1-01 | C1 | Create fulfillment route with PICK and GOODS_ISSUE steps | Route created, requires_packing = 0, requires_shipping = 0 |
| T-C1-02 | C1 | Create route with PICK, PACK, GOODS_ISSUE, SHIP | Route created, requires_packing = 1, requires_shipping = 1 |
| T-C1-03 | C1 | Create route without PICK step | Validation error: PICK step is required |
| T-C1-04 | C1 | Create route without GOODS_ISSUE step | Validation error: GOODS_ISSUE step is required |
| T-C1-05 | C1 | Create route with SHIP before GOODS_ISSUE | Validation error: GOODS_ISSUE must precede SHIP |
| T-C1-06 | C1 | Create route with duplicate code in same org | Validation error: code must be unique per org |
| T-C1-07 | C1 | Set route as org default when another default exists | Previous default cleared, new one set |
| T-C1-08 | C1 | Deactivate route assigned to active variant | Validation error: route is in use |
| T-C1-09 | C1 | Delete system route | Validation error: system routes cannot be deleted |
| T-C2-01 | C2 | Assign fulfillment route to product variant | fulfillment_route_id saved on variant |
| T-C2-02 | C2 | Assign route from different org | Validation error: route must belong to same org |
| T-C2-03 | C2 | Bulk assign route by category (3 variants, 1 already has route) | 2 variants updated, 1 skipped (already assigned) |
| T-C2-04 | C2 | Clear route from variant (set to NULL) | fulfillment_route_id = NULL |
| T-C3-01 | C3 | Add SO line for variant with route — no line override | Effective route = variant's route, source = VARIANT |
| T-C3-02 | C3 | Override route on SO line | Effective route = line override, source = LINE_OVERRIDE |
| T-C3-03 | C3 | SO line variant has no route, org has default | Effective route = org default, source = ORG_DEFAULT |
| T-C3-04 | C3 | SO line variant has no route, no org default, no line override | Effective route = NULL, source = NONE |
| T-C3-05 | C3 | Confirm SO with all lines having effective routes | SO confirmed, delivery orders created |
| T-C3-06 | C3 | Confirm SO with 1 line missing route | Validation error: line {N} has no fulfillment route |
| T-C3-07 | C3 | Confirm SO with line pointing to inactive route | Validation error: route is inactive |
| T-C3-08 | C3 | Change line route after SO confirmed | Validation error: SO is confirmed, route locked |
| T-C3-09 | C3 | Delivery preview shows correct grouping | Preview returns 3 groups for 3 different routes |
| T-C4-01 | C4 | Confirm SO with 3 lines, all same route | 1 delivery order created with 3 lines |
| T-C4-02 | C4 | Confirm SO with 3 lines, 3 different routes | 3 delivery orders created, 1 line each |
| T-C4-03 | C4 | Confirm SO with 3 lines, 2 same route + 1 different | 2 delivery orders (2 lines + 1 line) |
| T-C4-04 | C4 | Delivery order has from_source_type = SALE_ORDER | Verified on created delivery |
| T-C4-05 | C4 | Delivery order lines link back to SO lines | sale_order_line_id populated on each DO line |
| T-C4-06 | C4 | Cancel SO — deliveries in DRAFT/RELEASED | All linked deliveries cancelled |
| T-C4-07 | C4 | Cancel SO — one delivery already GOODS_ISSUED | DRAFT/RELEASED deliveries cancelled; GOODS_ISSUED delivery remains (manual reversal needed) |
| T-C5-01 | C5 | Advance PICK_ONLY delivery from PICKED | Next status = GOODS_ISSUED (skips PACKED, STAGED, APPROVAL) |
| T-C5-02 | C5 | Advance PICK_ONLY delivery from GOODS_ISSUED | Next status = COMPLETED (skips all SHIP statuses) |
| T-C5-03 | C5 | Advance PICK_AND_SHIP delivery from PICKED | Next status = GOODS_ISSUED (skips PACKED, STAGED, APPROVAL) |
| T-C5-04 | C5 | Advance PICK_AND_SHIP delivery from GOODS_ISSUED | Next status = IN_TRANSIT |
| T-C5-05 | C5 | Advance PICK_PACK_SHIP delivery from PICKED | Next status = PACKED |
| T-C5-06 | C5 | Advance PICK_PACK_SHIP delivery from PACKED | Next status = GOODS_ISSUED (skips STAGED, APPROVAL) |
| T-C5-07 | C5 | Full route (all steps) — advance from PACKED | Next status = STAGED |
| T-C5-08 | C5 | Cancel delivery from any status (any route) | Status = CANCELLED |
| T-C5-09 | C5 | Step tracker UI for PICK_ONLY | Shows: Pick → Goods Issue → Complete (3 steps only) |
| T-C5-10 | C5 | Step tracker UI for PICK_PACK_SHIP | Shows: Pick → Pack → Goods Issue → Ship → Complete (4 steps) |

---

## 13. Development Phases

### 13.1 Phase Breakdown

| Phase | Changes | Estimated Days | Dependencies |
|---|---|---|---|
| Phase A — Route Configuration | C1 (FulfillmentRoutes entity, steps, API, seed data, UI) | 3 days | None |
| Phase B — Variant Assignment | C2 (ProductVariants FK, variant form UI, bulk assign) | 2 days | Phase A |
| Phase C — SO Route Selection & Gate | C3 (SaleOrderLines FK, route resolver, confirmation validator, SO line UI, delivery preview) | 4 days | Phase A, B |
| Phase D — Delivery Creation from SO | C4 (delivery grouping logic, delivery_orders extension, SO-to-DO creation, fulfillment tab UI) | 4 days | Phase C |
| Phase E — Route-Aware State Machine | C5 (state machine modifications, step tracker UI, advance logic) | 3 days | Phase D |
| Phase F — Testing & Integration | All changes — integration tests, E2E scenarios | 3 days | Phase A–E |
| | **TOTAL** | **19 days** | |

### 13.2 Parallelization Opportunities

- Phase B (Variant Assignment) can start immediately after Phase A's migration is done — the UI work can run in parallel with Phase C's backend
- Phase C backend (route resolver, validator) and Phase C frontend (SO line UI) can run in parallel
- Phase E (state machine) is independent of Phase D's UI — backend can start once D's migration is applied

### 13.3 New Permission Claims Summary

| Claim Code | Module | Description |
|---|---|---|
| fulfillment_route_view | Logistics | View fulfillment routes |
| fulfillment_route_manage | Logistics | Create, edit, deactivate fulfillment routes |
| fulfillment_route_assign | Logistics | Assign routes to product variants (including bulk) |

---

## Appendix A — Entity Relationship Summary

```
logistics.FulfillmentRoutes
    ↑ FK (fulfillment_route_id)        ↑ FK
    │                                   │
inventory.ProductVariants          demand.SaleOrderLines
    (default route per variant)    (override route per SO line)
                                        │
                                        ↓
                               demand.SaleOrders
                                        │ on confirmation
                                        ↓
                               logistics.delivery_orders
                               (sale_order_id, fulfillment_route_id,
                                from_source_type = 'SALE_ORDER')
                                        │
                                        ↓
                               logistics.delivery_order_lines
                               (sale_order_line_id → back to SO line)
                                        │
                               Route-aware state machine
                               (skips steps not in route)

logistics.FulfillmentRouteSteps
    ↑ FK (fulfillment_route_id → FulfillmentRoutes)
    Defines: PICK, PACK, STAGE, APPROVAL, GOODS_ISSUE, SHIP
```

---

## Appendix B — Deferred to Future Addendums

| Topic | Description |
|---|---|
| Partial Delivery & Backorders | When only part of a DO line is picked/shipped — auto-create backorder delivery |
| Delivery Notes & Print | Delivery note PDF generation, packing slip print |
| Carrier Integration for SO Deliveries | Extending ICourierProvider for SO-originated consignments (existing infra, needs wiring) |
| Invoice Generation from Delivery | Creating invoices when delivery reaches GOODS_ISSUED or DELIVERED |
| Returns & Reverse Logistics | Customer returns, credit notes, return delivery orders |
| Warehouse Zone Routing | Route steps that specify which warehouse zone/area the product moves through |
| Wave Picking | Grouping multiple delivery pick lists into a single warehouse wave |

---

*End of FSD Addendum 33 — Fulfillment Routes & Sale Order Delivery Integration*
