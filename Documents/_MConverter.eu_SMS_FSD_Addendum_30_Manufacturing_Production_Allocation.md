**SUPPLY MANAGEMENT SYSTEM**

Functional Specification Document

**FSD Addendum 30**

**Manufacturing, Production & Allocation Engine**

Sales Order → Production → Material Supply → Allocation →

Finished Goods → Quality → Sales Fulfillment

*Implementation Specification for Claude Code*

Version 1.1

| **Property**   | **Value**                                              |
|----------------|--------------------------------------------------------|
| Document ID    | SMS-FSD-ADD-030                                        |
| Version        | 1.1                                                    |
| Date           | 2026-09-24                                             |
| Classification | Internal - Development                                 |
| Architecture   | .NET 8 / EF Core 8 / SQL Server 2022 / React 18 + Vite |
| Prerequisite   | Addendum 29 v1.3 (SCM Commercial Enablement)           |
| Status         | Ready for Implementation                               |

**1. Document Control**

**1.1 Revision History**

| **Version** | **Date** | **Author** | **Changes** |
|----|----|----|----|
| 1.0 | 2026-09-24 | Solution Architect | Initial FSD - Complete manufacturing, production, allocation engine specification |
| 1.1 | 2026-09-24 | Solution Architect | Added: FinishedGood as BOM input (chained manufacturing), Production Ledger (debit/credit inventory tracking per PO), 11 new reports (8 ledger + 3 chained mfg), Production Ledger entity/API/UI |

**1.2 Document Audience**

| **Role**           | **Purpose**                                   |
|--------------------|-----------------------------------------------|
| Product Owner      | Business requirements validation and approval |
| Business Analyst   | Functional requirements review                |
| Solution Architect | Technical architecture validation             |
| Development Team   | Implementation reference                      |
| QA Team            | Test scenario development                     |
| Database Team      | Schema and migration review                   |

**1.3 References**

| **Document** | **Description** |
|----|----|
| SMS-FSD-ADD-025 | Multi-tenancy architecture (org_id, HasQueryFilter) |
| SMS-FSD-ADD-026 | Product variant model with dynamic attributes |
| SMS-FSD-ADD-028 | Supplier rate cards |
| SMS-FSD-ADD-029 v1.3 | SCM Commercial Enablement (Business Partners, Sales, Fulfillment, Finance) |
| TASKS.md | Logistics rebuild --- 65 tasks, delivery orders, stock reservation |

**1.4 Terminology**

| **Term** | **Definition** |
|----|----|
| BOM | Bill of Materials --- defines components/materials needed to manufacture a product |
| FGR | Finished Goods Receipt --- moves accepted production output into inventory |
| PMR | Production Material Requirement --- materials needed for a specific production order |
| FG | Finished Good --- a product manufactured and ready for sale |
| Allocation Engine | Shared service that assigns available/expected supply to competing demands |
| FEFO | First Expired First Out --- allocation strategy for perishable stock |
| Supply Requirement | A shortage record that triggers procurement, transfer, or manufacturing |
| Production Readiness | All mandatory materials are reserved/available for a production order |

**2. Executive Summary**

This FSD specifies the complete Manufacturing, Production, and Allocation Engine module for the Supply Management System (SMS). It covers the end-to-end flow from Sales Order demand through production planning, material supply, quality inspection, finished goods receipt, and final sales fulfillment.

The specification is designed to integrate with the existing SMS architecture, reusing established patterns for multi-tenancy (org_id discriminator), inventory management, procurement, logistics/delivery orders, stock reservation (IStockReservationService), and document numbering.

**2.1 Key Capabilities**

- Product Catalog Enhancement --- ProductType and SupplyMethod classification enabling manufacturing products

- Bill of Materials (BOM) --- Versioned, approved BOM definitions with change management

- Production Order Management --- Complete lifecycle from planning through completion

- Production Material Requirements --- Exploded BOM requirements with reservation and shortage tracking

- Supply Requirement Engine --- Automated shortage detection and supply source determination

- Shared Allocation Engine --- Priority-based, multi-demand, multi-supply allocation across Sales, Production, and future modules

- Quality Inspection --- Pass/fail/hold/rework decisions on production output

- Finished Goods Receipt --- Accepted production output moved into sellable inventory

- End-to-End Traceability --- Complete audit trail from Sales Order through Delivery via trace_id

- Production Ledger --- Double-entry style debit/credit tracking of material consumption and finished goods output per Production Order

- Chained Manufacturing --- FinishedGood products can be used as BOM inputs, enabling multi-stage production chains with automatic child Production Order creation

**2.2 Architectural Principles**

- Existing codebase first: analyze, reuse, extend --- never duplicate

- Product Catalog defines WHAT, BOM defines HOW, Production Order defines THE JOB

- Supply Requirements are source-agnostic: a Purchase Order is a supply source, not a production dependency

- Allocation Engine is shared across Sales, Production, Service, and future demand types

- Inventory never carries a hardcoded SalesOrderId or ProductionOrderId --- allocation records own the demand assignment

- BOM versioning protects historical production: changes create new versions, never mutate used BOMs

- Multi-tenancy is non-negotiable: every table has org_id with EF Core HasQueryFilter

**2.3 Scope Summary**

| **Area** | **In Scope** | **Out of Scope** |
|----|----|----|
| Product Catalog | ProductType, SupplyMethod enums; saleable/purchasable/stockable/manufacturable flags | Product variant attribute redesign (Addendum 26) |
| BOM | Header, lines, versioning, approval workflow, change management | Routing/work center/operation sequences |
| Production | Production Order lifecycle, material requirements, material issue, execution | Shop floor scheduling, machine integration, IoT |
| Allocation | Shared allocation engine, priority rules, competing demands | ATP (Available-to-Promise) calculations |
| Quality | Inspection, accept/reject/hold/rework, quantity tracking | Statistical process control, SPC charts |
| Finished Goods | FGR creation, inventory update, sales allocation trigger | Serial/lot manufacturing traceability |
| Delivery | Extends existing delivery_orders with PRODUCTION source type | New delivery infrastructure (already built) |

**3. Business Objective**

The SCM must support a complete flow where a customer Sales Order can result in manufacturing when finished goods are unavailable. The system must not create direct uncontrolled relationships (e.g., Sales Order → Purchase Order or Sales Order → Production Order) without going through the appropriate demand/fulfillment/supply process.

**3.1 End-to-End Business Flow**

> Sales Order
>
> ↓
>
> Fulfillment Requirement
>
> ↓
>
> Check Finished Goods Inventory
>
> ↓
>
> If available → Allocate/Reserve → Delivery
>
> ↓
>
> If unavailable → Determine Supply Method
>
> ↓
>
> Production Requirement (SupplyMethod = Manufacture)
>
> ↓
>
> Production Order (snapshots active BOM version)
>
> ↓
>
> Production Material Requirements (exploded from BOM)
>
> ↓
>
> Inventory Availability Check per material
>
> ↓
>
> Allocation/Reservation of available materials
>
> ↓
>
> Supply Requirements for shortages
>
> ↓
>
> Purchase Order / Transfer / Other Supply
>
> ↓
>
> Goods Receipt → Inventory increases
>
> ↓
>
> Allocation Engine re-evaluates outstanding demands
>
> ↓
>
> Production Material Requirements fulfilled
>
> ↓
>
> Production Order → READY
>
> ↓
>
> Material Issue (stock → production floor)
>
> ↓
>
> Production Execution
>
> ↓
>
> Quality Inspection
>
> ↓
>
> Finished Goods Receipt (accepted qty → FG inventory)
>
> ↓
>
> Allocation Engine assigns FG to Sales Order
>
> ↓
>
> Delivery → Invoice
>
> **🔴 CRITICAL:** The system must NOT create a direct Sales Order → Purchase Order or Sales Order → Production Order link without going through Fulfillment Requirement → Supply Method evaluation → Supply Requirement.

**4. Scope**

**4.1 In Scope**

- Product Catalog changes: ProductType enum, SupplyMethod enum, manufacturing capability flags

- Bill of Materials (BOM): creation, versioning, approval workflow, change management

- Production Order: complete lifecycle (Draft → Planned → Material Pending → Ready → In Progress → QI → Completed → Closed)

- Production Material Requirements: BOM explosion, shortage calculation, reservation, material issue

- Supply Requirement Engine: shortage detection, supply method determination, PO/Transfer/Manufacturing linkage

- Shared Allocation Engine: multi-demand, multi-supply, priority-based allocation with configurable rules

- Quality Inspection: pass/fail/hold/rework with quantity tracking

- Finished Goods Receipt: inventory update, allocation trigger

- Cancellation and change management for all entities

- Concurrency control for allocation and inventory operations

- Security and permissions for all new roles

- Audit trail for all state changes

- API specifications for all new endpoints

- UI screen definitions

- Reporting requirements

- End-to-end traceability via trace_id and DocumentTimelines

**4.2 Out of Scope**

- Shop floor scheduling and work center/routing management

- Machine/IoT integration

- Advanced Planning and Scheduling (APS)

- Capacity planning

- Costing/cost roll-up for manufactured products

- Serial number and batch/lot tracking for production

- Statistical Process Control (SPC)

- Available-to-Promise (ATP) calculations

- Multi-site/multi-plant manufacturing

- Subcontracting/outsourced manufacturing

**5. Existing System Assessment**

This section documents the existing SMS functionality that this FSD builds upon. All new development MUST reuse these patterns.

**5.1 Existing Functionality --- REUSE**

| **Area** | **Schema** | **Key Entities** | **Status** |
|----|----|----|----|
| Product Catalog | lookups | Products, ProductVariants, ProductCategories | EXISTS --- extend with ProductType, SupplyMethod |
| Inventory | inventory | stock_balances, stock_transactions, stock_reservations | EXISTS --- reuse IStockReservationService |
| Procurement | procurement | PurchaseRequisitions, PurchaseOrders, PurchaseOrderLines | EXISTS --- reuse for material supply |
| Goods Receipt | procurement | GoodsReceipts, GoodsReceiptLines | EXISTS --- reuse for material receipt |
| Sales Order | demand | SaleOrders, SaleOrderLines (from Addendum 29) | EXISTS --- extend with manufacturing trigger |
| Delivery Orders | logistics | delivery_orders, delivery_order_lines (15-status state machine) | EXISTS --- extend with PRODUCTION source type |
| Stock Reservation | inventory | stock_reservations (via IStockReservationService) | EXISTS --- add PRODUCTION_ORDER to ReservationSourceType |
| Business Partners | suppliers | BusinessPartners (Addendum 29 migration) | EXISTS --- reuse for customer/vendor |
| Document Numbering | logistics | document_number_sequences with RowVersion | EXISTS --- reuse for ProdOrder/BOM numbering |
| Document Timeline | various | DocumentTimelines (trace_id, JSON events) | EXISTS --- reuse for SO→PO→Production traceability |
| Approval Workflow | workflow | ApprovalWorkflows, WorkflowSteps | EXISTS --- reuse for BOM approval |
| Notifications | auth | Notifications table + Hangfire | EXISTS --- reuse for production alerts |
| Multi-tenancy | all | org_id column + EF Core HasQueryFilter | EXISTS --- mandatory on all new tables |
| Audit | auth | AuditTrail entity + EF interceptor | EXISTS --- reuse for all new entities |

**5.2 Existing Functionality --- MODIFY**

| **Entity** | **Modification** | **Reason** |
|----|----|----|
| Products / ProductVariants | Add product_type, supply_method, is_manufacturable columns | Enable manufacturing classification |
| ReservationSourceType enum | Add PRODUCTION_ORDER value | Allow stock reservation for production material requirements |
| StockMovementType enum | Add MATERIAL_ISSUE, FINISHED_GOODS_RECEIPT, PRODUCTION_SCRAP types | Track manufacturing inventory movements |
| delivery_orders.from_source_type | Add PRODUCTION value | Allow delivery orders to originate from production completion |
| SaleOrders | Add fulfillment_method, production_requirement_status columns | Track which SO lines need manufacturing |

**5.3 New Functionality**

| **Entity** | **Schema** | **Purpose** |
|----|----|----|
| BillOfMaterials | material | BOM header --- product, version, status, effective dates |
| BillOfMaterialLines | material | BOM lines --- component product, quantity, UOM, scrap % |
| ProductionOrders | material | Production order header --- planned/produced/accepted qty, BOM snapshot |
| ProductionMaterialRequirements | material | Exploded material needs per production order |
| SupplyRequirements | material | Shortage records driving procurement/transfer/manufacturing |
| AllocationRecords | inventory | Shared demand↔supply allocation assignments |
| AllocationRules | inventory | Configurable priority rules for the allocation engine |
| QualityInspections | material | Inspection results per production order |
| QualityInspectionLines | material | Per-item pass/fail/hold/rework decisions |
| FinishedGoodsReceipts | material | FGR header --- production order, accepted qty → inventory |
| FinishedGoodsReceiptLines | material | FGR line items with product, qty, warehouse |
| MaterialIssues | material | Material issue header --- production order → inventory withdrawal |
| MaterialIssueLines | material | Issued material lines with actual qty, warehouse, batch |

**5.4 Schema Allocation**

All new manufacturing entities reside in the material schema (already exists for material-related lookups). Inventory-shared entities (AllocationRecords, AllocationRules) reside in the inventory schema.

> material schema:
>
> BillOfMaterials, BillOfMaterialLines
>
> ProductionOrders, ProductionMaterialRequirements
>
> SupplyRequirements
>
> QualityInspections, QualityInspectionLines
>
> FinishedGoodsReceipts, FinishedGoodsReceiptLines
>
> MaterialIssues, MaterialIssueLines
>
> inventory schema:
>
> AllocationRecords, AllocationRules (shared across demand types)
>
> lookups schema (MODIFY):
>
> Products --- add product_type, supply_method columns

**6. Product Catalog Changes**

The existing Product entity must be extended to support manufacturing classification. This does NOT replace the existing variant-first model (Addendum 26) --- it adds classification metadata to the Product (parent) level.

**6.1 ProductType Enum**

Classifies the nature of the product:

| **Value** | **Int** | **Description** | **Can Sell** | **Can Purchase** | **Can Stock** | **Can Manufacture** | **Can be BOM Input** |
|----|----|----|----|----|----|----|----|
| StockItem | 0 | General stockable item | Yes | Yes | Yes | No | No |
| RawMaterial | 1 | Raw material for manufacturing | No | Yes | Yes | No | Yes |
| Component | 2 | Component/part used in assembly | No | Yes | Yes | No | Yes |
| SemiFinished | 3 | Intermediate manufactured product | No | No | Yes | Yes | Yes |
| FinishedGood | 4 | Final manufactured product for sale | Yes | No | Yes | Yes | Yes\* |
| Consumable | 5 | Used in production, not tracked in BOM output | No | Yes | No\* | No | Yes |
| Service | 6 | Non-stockable service item | Yes | Yes | No | No | Yes |
| Asset | 7 | Capital asset / equipment | No | Yes | No | No | No |

> ⚠ *\*Consumables may optionally be stockable depending on business configuration. \*FinishedGood products CAN be used as BOM input for chained manufacturing --- e.g., a finished sub-assembly from one production process becomes raw material for another (see Section 6.4 Chained Manufacturing example).*

**6.2 SupplyMethod Enum**

Defines HOW the product is obtained:

| **Value** | **Int** | **Description** | **Triggers** |
|----|----|----|----|
| Purchase | 0 | Procured from external vendor | Purchase Requisition / Purchase Order |
| Manufacture | 1 | Produced in-house using BOM | Production Order |
| Transfer | 2 | Transferred from another warehouse/site | Transfer Order |
| Service | 3 | Externally provided service | Service Order / PO |

**6.3 Product Entity Modifications**

**Products (MODIFY --- lookups schema)**

| **Column** | **Type** | **Nullable** | **Description** |
|----|----|----|----|
| product_type | TINYINT | NOT NULL | ProductType enum --- classifies the product nature |
| supply_method | TINYINT | NOT NULL | SupplyMethod enum --- how this product is obtained |
| is_manufacturable | BIT | NOT NULL | Can this product be a BOM output? Derived: supply_method = Manufacture |
| is_saleable | BIT | NOT NULL | Can this product be sold? Default based on product_type |
| is_purchasable | BIT | NOT NULL | Can this product be purchased? Default based on product_type |
| is_stockable | BIT | NOT NULL | Is this product tracked in inventory? Default based on product_type |
| is_bom_input | BIT | NOT NULL | Can this product appear as a BOM line component? |
| default_production_warehouse_id | BIGINT | NULL | FK to warehouses --- default warehouse for manufacturing |
| lead_time_days | INT | NULL | Default manufacturing lead time in days |

> **🔴 CRITICAL:** The manufactured product MUST already exist in Product Catalog. Manufacturing creates inventory quantity for an existing Product. Manufacturing must NOT create a new Product Master record after production.

**6.4 Product Classification Examples**

| **Product** | **ProductType** | **SupplyMethod** | **Saleable** | **Purchasable** | **Stockable** | **BOM Input** |
|----|----|----|----|----|----|----|
| Plain T-Shirt | RawMaterial | Purchase | No | Yes | Yes | Yes |
| Printing Ink | RawMaterial | Purchase | No | Yes | Yes | Yes |
| Printed T-Shirt | FinishedGood | Manufacture | Yes | No | Yes | No |
| Packaging Box | Consumable | Purchase | No | Yes | Yes | Yes |
| Washing Service | Service | Service | No | Yes | No | Yes |
| T-Shirt Blank (pre-cut) | SemiFinished | Manufacture | No | No | Yes | Yes |
| Steel Bolt (M10) | FinishedGood | Manufacture | Yes | No | Yes | Yes |
| Bolt Assembly Kit | FinishedGood | Manufacture | Yes | No | Yes | No |

**6.4.1 Chained Manufacturing --- FinishedGood as BOM Input**

A FinishedGood product from one manufacturing process can be used as a raw material (BOM input) in another manufacturing process. This is the chained manufacturing pattern:

> **🔴 CRITICAL:** A FinishedGood product can have is_bom_input = true. When used as a BOM component in another product\'s BOM, its supply_method = Manufacture means the Supply Requirement Engine will create a child Production Order (instead of a Purchase Order) when the material is short.

Example --- Two-stage manufacturing chain:

> Stage 1: Steel Bolt (M10) --- FinishedGood, is_bom_input = true
>
> BOM: Steel Rod (RawMaterial, Purchase) x 1 PCS
>
> Threading Oil (Consumable, Purchase) x 0.01 L
>
> → Production Order PROD-A → produces Steel Bolt (M10) inventory
>
> Stage 2: Bolt Assembly Kit --- FinishedGood, is_bom_input = false
>
> BOM: Steel Bolt (M10) (FinishedGood, Manufacture) x 10 PCS ← FG used as BOM input
>
> Washer (RawMaterial, Purchase) x 10 PCS
>
> Nut (RawMaterial, Purchase) x 10 PCS
>
> Plastic Box (Consumable, Purchase) x 1 PCS
>
> → Production Order PROD-B → when PMR shortage for Steel Bolt:
>
> → Supply Requirement (supply_method = Manufacture)
>
> → Creates child Production Order PROD-A2 for Steel Bolt
>
> Traceability: SO → PROD-B → PMR (Steel Bolt) → SR → PROD-A2 → PMR (Steel Rod) → PO

This pattern supports unlimited depth of manufacturing chain. The Production Ledger (Section 19A) tracks material debits and production credits at every stage.

**6.5 Validation Rules**

- A product with supply_method = Manufacture cannot be created without is_manufacturable = true

- A FinishedGood product must have supply_method = Manufacture

- A RawMaterial product cannot have supply_method = Manufacture

- A Service product must have is_stockable = false

- A FinishedGood CAN have is_bom_input = true --- this enables chained manufacturing where a FG from one process is consumed in another

- When a FinishedGood with is_bom_input = true appears in a BOM, shortage triggers a Production Order (not a Purchase Order) via the Supply Requirement Engine

- Default flags are set on creation based on product_type, but can be overridden by the user within valid combinations

**6.6 Migration: Existing Product Data**

- All existing products default to product_type = StockItem, supply_method = Purchase

- All existing products default to is_saleable = true, is_purchasable = true, is_stockable = true, is_bom_input = false

- is_manufacturable defaults to false for all existing products

- These defaults are safe: existing products are all purchased stock items in the trading module

**7. BOM Management**

A Bill of Materials (BOM) defines HOW a manufactured product is made. It lists the components, quantities, and parameters needed to produce one unit of the output product.

**7.1 BOM Header Entity**

**material.BillOfMaterials (NEW)**

| **Column** | **Type** | **Nullable** | **Description** |
|----|----|----|----|
| id | BIGINT IDENTITY | NOT NULL | PK |
| org_id | BIGINT | NOT NULL | Tenant discriminator --- HasQueryFilter |
| bom_number | NVARCHAR(50) | NOT NULL | Generated via document_number_sequences (BOM-YYYYMMDD-SEQ) |
| product_id | BIGINT | NOT NULL | FK to Products --- the output product (must be Manufacture supply method) |
| product_variant_id | BIGINT | NULL | FK to ProductVariants --- optional specific variant |
| version | INT | NOT NULL | Sequential version number per product (1, 2, 3\...) |
| status | TINYINT | NOT NULL | BOMStatus enum: Draft=0, Submitted=1, Approved=2, Active=3, Obsolete=4, Rejected=5 |
| effective_from | DATE | NULL | Date from which this BOM version is valid |
| effective_to | DATE | NULL | Date until which this BOM version is valid (NULL = no expiry) |
| base_quantity | DECIMAL(18,4) | NOT NULL | Output quantity this BOM produces (default 1) |
| base_uom | NVARCHAR(20) | NOT NULL | Unit of measure for output (PCS, KG, L, etc.) |
| warehouse_id | BIGINT | NULL | FK to Warehouses --- optional site/warehouse applicability |
| notes | NVARCHAR(1000) | NULL | Free-text notes about this BOM version |
| created_by | BIGINT | NOT NULL | FK to Users --- who created this BOM |
| created_at | DATETIMEOFFSET | NOT NULL | Creation timestamp |
| submitted_by | BIGINT | NULL | FK to Users --- who submitted for approval |
| submitted_at | DATETIMEOFFSET | NULL | Submission timestamp |
| approved_by | BIGINT | NULL | FK to Users --- who approved |
| approved_at | DATETIMEOFFSET | NULL | Approval timestamp |
| rejected_by | BIGINT | NULL | FK to Users --- who rejected |
| rejected_at | DATETIMEOFFSET | NULL | Rejection timestamp |
| rejection_reason | NVARCHAR(500) | NULL | Reason for rejection |
| activated_by | BIGINT | NULL | FK to Users --- who activated |
| activated_at | DATETIMEOFFSET | NULL | Activation timestamp |
| obsoleted_by | BIGINT | NULL | FK to Users --- who obsoleted |
| obsoleted_at | DATETIMEOFFSET | NULL | Obsolescence timestamp |
| row_version | ROWVERSION | NOT NULL | Concurrency token |

**7.2 BOM Lines Entity**

**material.BillOfMaterialLines (NEW)**

| **Column** | **Type** | **Nullable** | **Description** |
|----|----|----|----|
| id | BIGINT IDENTITY | NOT NULL | PK |
| org_id | BIGINT | NOT NULL | Tenant discriminator |
| bom_id | BIGINT | NOT NULL | FK to BillOfMaterials |
| sequence | INT | NOT NULL | Line ordering (10, 20, 30\...) |
| material_product_id | BIGINT | NOT NULL | FK to Products --- the input material (must have is_bom_input = true) |
| material_variant_id | BIGINT | NULL | FK to ProductVariants --- optional specific variant |
| quantity | DECIMAL(18,6) | NOT NULL | Required quantity per base_quantity of output |
| uom | NVARCHAR(20) | NOT NULL | Unit of measure for this material |
| scrap_percentage | DECIMAL(5,2) | NOT NULL | Expected scrap/wastage % (default 0) |
| is_critical | BIT | NOT NULL | Is this material mandatory for production? Default true |
| alternate_material_id | BIGINT | NULL | FK to Products --- optional substitute material |
| notes | NVARCHAR(500) | NULL | Line-level notes |
| warehouse_id | BIGINT | NULL | FK to Warehouses --- optional specific source warehouse |

**7.3 BOM-Product Relationship**

> Product (FinishedGood / SemiFinished, SupplyMethod = Manufacture)
>
> └── BOM Version 1 (Status: Obsolete)
>
> ├── Line 1: Plain T-Shirt 1 PCS
>
> ├── Line 2: Printing Ink 0.05 L
>
> └── Line 3: Packaging Box 1 PCS
>
> └── BOM Version 2 (Status: Active)
>
> ├── Line 1: Plain T-Shirt 1 PCS
>
> ├── Line 2: Printing Ink 0.07 L ← changed
>
> └── Line 3: Packaging Box 1 PCS

**7.4 BOM Constraints**

- UQ: (org_id, product_id, product_variant_id, version) --- one version number per product per org

- UQ: Only ONE BOM version can be Active per (org_id, product_id, product_variant_id, warehouse_id) at any time

- FK: product_id must reference a product with supply_method = Manufacture

- FK: material_product_id must reference a product with is_bom_input = true

- CHECK: base_quantity \> 0

- CHECK: quantity \> 0 for each line

- CHECK: scrap_percentage \>= 0 AND scrap_percentage \< 100

- CHECK: effective_from \< effective_to when both are set

- SELF-REFERENCE CHECK: A BOM cannot list its own output product as an input material (circular dependency detection)

**8. BOM Versioning & Change Management**

BOM versioning protects historical production records. Changes to a BOM NEVER modify an existing version that has been used by a Production Order.

**8.1 Versioning Rules**

- When a BOM change is needed and the current version has been used by ANY Production Order, a new version MUST be created

- The old version transitions to Obsolete status

- The new version starts as Draft and goes through the full approval workflow

- Existing Production Orders continue using their snapshotted BOM version

- New Production Orders use the currently Active BOM version

- A Draft BOM that has NEVER been used by a Production Order CAN be edited in place

**8.2 Version Lifecycle**

> BOM V1 created → Draft
>
> BOM V1 → Submitted → Approved → Active
>
> BOM V1 used by Production Order PO-001 (snapshotted)
>
> Business needs to change ink quantity:
>
> BOM V2 created (copies lines from V1 with modifications)
>
> BOM V2 → Draft → Submitted → Approved → Active
>
> BOM V1 → Obsolete (automatic when V2 becomes Active)
>
> PO-001 continues using V1 snapshot --- unaffected
>
> New Production Orders use V2

**8.3 Change Impact Matrix**

| **Production Order Status** | **BOM Change Impact** | **Manual Migration Allowed?** |
|----|----|----|
| Draft (not released) | Can optionally refresh to new BOM version | Yes --- with BOM_ADMIN permission |
| Planned | No automatic change; can manually migrate if no materials issued | Yes --- with BOM_ADMIN + PROD_MANAGER permission |
| Material Pending | No change --- BOM version is locked | No |
| Ready | No change --- materials already reserved against V1 requirements | No |
| In Progress | No change --- production underway | No |
| Quality Inspection | No change | No |
| Completed / Closed | Historical record --- immutable | No |

**8.4 BOM Comparison**

The system must provide a BOM comparison view showing differences between two versions of the same product\'s BOM:

- Added lines (new materials in V2 not in V1)

- Removed lines (materials in V1 not in V2)

- Changed quantities (same material, different qty or scrap %)

- Changed UOM

- Changed alternate materials

**9. BOM Approval Workflow**

**9.1 BOM Status State Machine**

| **Status** | **Description** | **Transitions To** | **Trigger** |
|----|----|----|----|
| Draft (0) | BOM created or returned from rejection | Submitted | User submits for approval |
| Submitted (1) | Awaiting approval | Approved, Rejected | Approver reviews |
| Approved (2) | BOM approved, eligible for activation | Active | Authorized user activates |
| Active (3) | Current production BOM for this product | Obsolete | New version activated, or manual obsolescence |
| Obsolete (4) | No longer used for new production orders | (terminal) | New version supersedes |
| Rejected (5) | Approval denied, requires correction | Draft | Creator revises and resubmits |

**9.2 Permission Matrix**

| **Action** | **Permission Required** | **Role** |
|----|----|----|
| Create BOM | BOM_CREATE | Product Manager, BOM Creator |
| Edit Draft BOM | BOM_EDIT | BOM Creator |
| Submit BOM | BOM_SUBMIT | BOM Creator |
| Approve BOM | BOM_APPROVE | BOM Approver, Production Manager |
| Reject BOM | BOM_APPROVE | BOM Approver, Production Manager |
| Activate BOM | BOM_ACTIVATE | Production Manager, Administrator |
| Obsolete BOM | BOM_OBSOLETE | Production Manager, Administrator |
| Migrate Production Order BOM | BOM_ADMIN + PROD_MANAGER | Administrator |
| View BOM | BOM_VIEW | All manufacturing roles |
| Compare BOM versions | BOM_VIEW | All manufacturing roles |

**9.3 Business Rules**

- Rule 1: Draft BOM can be freely modified by users with BOM_EDIT permission

- Rule 2: Submitted BOM is read-only (cannot be modified while under review)

- Rule 3: Rejected BOM returns to Draft --- rejection reason is recorded and displayed

- Rule 4: Approved BOM becomes eligible for activation but is not yet used for production

- Rule 5: Only ONE Active BOM per (org_id, product_id, variant_id, warehouse_id) at any time

- Rule 6: Activating a BOM automatically obsoletes any previously Active version for the same product/variant/warehouse

- Rule 7: A BOM that has been used by a Production Order cannot be deleted --- only obsoleted

- Rule 8: An Obsolete BOM is immutable --- historical preservation

- Rule 9: The approver cannot be the same user who submitted (four-eyes principle)

- Rule 10: A product with supply_method = Manufacture cannot have a Production Order released without a valid Active BOM

**9.4 Audit Trail**

Every BOM status transition generates an audit record:

- User who performed the action

- Timestamp

- Old status → New status

- Rejection reason (if applicable)

- Which Production Orders are affected (if BOM migration)

Uses the existing AuditTrail entity and EF Core interceptor pattern.

**10. Sales Order Changes**

The Sales Order entity (created in Addendum 29) is extended to support manufacturing-triggered fulfillment. The core Sales Order lifecycle remains unchanged.

**10.1 Sales Order Line Extensions**

**demand.SaleOrderLines (MODIFY)**

| **Column** | **Type** | **Nullable** | **Description** |
|----|----|----|----|
| fulfillment_method | TINYINT | NULL | FulfillmentMethod enum: FromStock=0, Manufacture=1, Purchase=2, Transfer=3, Mixed=4 |
| allocated_quantity | DECIMAL(18,4) | NOT NULL | Quantity allocated from available inventory (default 0) |
| production_required_quantity | DECIMAL(18,4) | NOT NULL | Quantity that must be manufactured (default 0) |
| purchase_required_quantity | DECIMAL(18,4) | NOT NULL | Quantity that must be purchased (default 0) |
| fulfilled_quantity | DECIMAL(18,4) | NOT NULL | Quantity actually delivered to customer (default 0) |

**10.2 Fulfillment Requirement**

When a Sales Order is confirmed, the system creates a Fulfillment Requirement for each line. The requirement determines HOW the demand will be satisfied.

**demand.FulfillmentRequirements (NEW)**

| **Column** | **Type** | **Nullable** | **Description** |
|----|----|----|----|
| id | BIGINT IDENTITY | NOT NULL | PK |
| org_id | BIGINT | NOT NULL | Tenant discriminator |
| sale_order_id | BIGINT | NOT NULL | FK to SaleOrders |
| sale_order_line_id | BIGINT | NOT NULL | FK to SaleOrderLines |
| product_id | BIGINT | NOT NULL | FK to Products |
| product_variant_id | BIGINT | NULL | FK to ProductVariants |
| required_quantity | DECIMAL(18,4) | NOT NULL | Total quantity required |
| allocated_quantity | DECIMAL(18,4) | NOT NULL | Quantity allocated from stock |
| production_quantity | DECIMAL(18,4) | NOT NULL | Quantity assigned to production |
| purchase_quantity | DECIMAL(18,4) | NOT NULL | Quantity assigned to purchase |
| fulfilled_quantity | DECIMAL(18,4) | NOT NULL | Quantity actually delivered |
| outstanding_quantity | DECIMAL(18,4) | NOT NULL | Computed: required - fulfilled |
| required_date | DATE | NOT NULL | When the fulfillment is needed (from SO required date) |
| warehouse_id | BIGINT | NOT NULL | FK to Warehouses --- fulfillment warehouse |
| status | TINYINT | NOT NULL | FulfillmentStatus: Open=0, PartiallyAllocated=1, FullyAllocated=2, InProduction=3, PartiallyFulfilled=4, Fulfilled=5, Cancelled=6 |
| created_at | DATETIMEOFFSET | NOT NULL | Creation timestamp |
| updated_at | DATETIMEOFFSET | NOT NULL | Last update timestamp |

**10.3 Fulfillment Decision Flow**

> Sales Order Confirmed
>
> ↓
>
> For each SO line:
>
> 1\. Check product.supply_method
>
> 2\. Query inventory: available_quantity = on_hand - reserved
>
> 3\. If available \>= required → allocate from stock (fulfillment_method = FromStock)
>
> 4\. If available \> 0 but \< required → allocate available, remainder by supply method
>
> 5\. If available = 0 → entire quantity by supply method
>
> 6\. Supply method decisions:
>
> \- supply_method = Manufacture → Create Production Requirement
>
> \- supply_method = Purchase → Create Supply Requirement (→ PR → PO)
>
> \- supply_method = Transfer → Create Transfer Requirement
>
> 7\. If partial stock + partial manufacture → fulfillment_method = Mixed
>
> **🔴 CRITICAL:** The system must NOT assume every Sales Order requires manufacturing. Products with supply_method = Purchase go through procurement, not production.

**11. Production Order**

A Production Order represents an actual manufacturing job. It is created from a Fulfillment Requirement when the product\'s supply method is Manufacture.

**11.1 Production Order Entity**

**material.ProductionOrders (NEW)**

| **Column** | **Type** | **Nullable** | **Description** |
|----|----|----|----|
| id | BIGINT IDENTITY | NOT NULL | PK |
| org_id | BIGINT | NOT NULL | Tenant discriminator |
| production_number | NVARCHAR(50) | NOT NULL | Generated via document_number_sequences (PROD-YYYYMMDD-SEQ) |
| product_id | BIGINT | NOT NULL | FK to Products --- the product being manufactured |
| product_variant_id | BIGINT | NULL | FK to ProductVariants |
| bom_id | BIGINT | NOT NULL | FK to BillOfMaterials --- snapshotted at creation time |
| bom_version | INT | NOT NULL | BOM version number at time of creation (immutable) |
| planned_quantity | DECIMAL(18,4) | NOT NULL | Quantity planned for production |
| produced_quantity | DECIMAL(18,4) | NOT NULL | Actual quantity produced (default 0) |
| accepted_quantity | DECIMAL(18,4) | NOT NULL | Quantity passed quality inspection (default 0) |
| rejected_quantity | DECIMAL(18,4) | NOT NULL | Quantity failed quality inspection (default 0) |
| scrapped_quantity | DECIMAL(18,4) | NOT NULL | Quantity scrapped during production (default 0) |
| warehouse_id | BIGINT | NOT NULL | FK to Warehouses --- production warehouse |
| output_warehouse_id | BIGINT | NULL | FK to Warehouses --- FG destination (defaults to same) |
| source_type | TINYINT | NOT NULL | ProductionSourceType: Manual=0, SalesOrder=1, FulfillmentReq=2, Replenishment=3 |
| source_id | BIGINT | NULL | FK to source document (SaleOrder, FulfillmentReq, etc.) |
| source_line_id | BIGINT | NULL | FK to source line if applicable |
| priority | TINYINT | NOT NULL | ProductionPriority: Low=0, Normal=1, High=2, Urgent=3 (default Normal) |
| required_date | DATE | NOT NULL | Date by which production should be complete |
| planned_start_date | DATE | NULL | Planned start date |
| actual_start_date | DATETIMEOFFSET | NULL | When production actually started |
| actual_end_date | DATETIMEOFFSET | NULL | When production actually completed |
| status | TINYINT | NOT NULL | ProductionOrderStatus enum (see state machine below) |
| material_readiness | TINYINT | NOT NULL | MaterialReadiness: NotChecked=0, Partial=1, Ready=2, Shortage=3 |
| notes | NVARCHAR(2000) | NULL | Production notes |
| trace_id | UNIQUEIDENTIFIER | NULL | Correlation GUID for DocumentTimelines traceability |
| created_by | BIGINT | NOT NULL | FK to Users |
| created_at | DATETIMEOFFSET | NOT NULL | Creation timestamp |
| updated_at | DATETIMEOFFSET | NOT NULL | Last update timestamp |
| row_version | ROWVERSION | NOT NULL | Concurrency token |

**11.2 Production Order Status State Machine**

| **Status** | **Description** | **Transitions To** | **Trigger** |
|----|----|----|----|
| Draft (0) | Production order created, not yet planned | Planned, Cancelled | User creates production order |
| Planned (1) | Production scheduled, BOM exploded into material requirements | MaterialPending, Cancelled | User plans/releases the order |
| MaterialPending (2) | Waiting for materials --- at least one PMR has shortage | Ready, Cancelled | Material shortage detected during planning |
| Ready (3) | ALL mandatory materials are fully reserved/available | InProgress, MaterialPending, Cancelled | All PMR shortages resolved |
| InProgress (4) | Production is underway, materials issued | QualityInspection, Completed, Cancelled | User starts production |
| QualityInspection (5) | Production complete, awaiting QI | Completed | User reports production complete |
| Completed (6) | Quality inspected, FGR processed | Closed | QI + FGR complete |
| Closed (7) | All post-production activities done, order finalized | (terminal) | Admin closes order |
| Cancelled (8) | Production order cancelled | (terminal) | User cancels (with authorization) |

**11.3 Production Readiness Calculation**

**A Production Order transitions from MaterialPending → Ready when ALL mandatory materials satisfy the readiness condition:**

> for each PMR where is_critical = true:
>
> if pmr.shortage_quantity \> 0:
>
> production_order.material_readiness = Shortage
>
> production_order.status remains MaterialPending
>
> RETURN
>
> // All critical materials have shortage_quantity = 0
>
> production_order.material_readiness = Ready
>
> production_order.status = Ready

The readiness check is triggered by:

- Allocation Engine completing an allocation to a PMR

- Goods Receipt creating new inventory that gets allocated

- Manual reservation of materials

- Supply Requirement being fulfilled

**11.4 Production Order Business Rules**

- A Production Order must reference a valid Active BOM at creation time

- The BOM version is snapshotted: bom_id and bom_version are immutable after Planned status

- A Production Order SHOULD NOT automatically become Ready simply because it exists

- Material requirements are generated by exploding the BOM during the Planned transition

- Cancelling a Production Order must release all reservations and cancel outstanding Supply Requirements

- Over-production (produced_quantity \> planned_quantity) is allowed but must be audited

- Under-production (produced_quantity \< planned_quantity) results in partial fulfillment of the source demand

**12. Production Material Requirements**

When a Production Order transitions to Planned status, the BOM is exploded into Production Material Requirements (PMRs). These represent the materials needed for this specific production job.

**12.1 PMR Entity**

**material.ProductionMaterialRequirements (NEW)**

| **Column** | **Type** | **Nullable** | **Description** |
|----|----|----|----|
| id | BIGINT IDENTITY | NOT NULL | PK |
| org_id | BIGINT | NOT NULL | Tenant discriminator |
| production_order_id | BIGINT | NOT NULL | FK to ProductionOrders |
| bom_line_id | BIGINT | NOT NULL | FK to BillOfMaterialLines --- source BOM line |
| product_id | BIGINT | NOT NULL | FK to Products --- the material needed |
| product_variant_id | BIGINT | NULL | FK to ProductVariants |
| required_quantity | DECIMAL(18,4) | NOT NULL | Gross required qty (incl. scrap allowance) |
| net_quantity | DECIMAL(18,4) | NOT NULL | Net required qty (before scrap) |
| scrap_allowance | DECIMAL(18,4) | NOT NULL | Additional qty for expected scrap |
| reserved_quantity | DECIMAL(18,4) | NOT NULL | Quantity reserved from inventory (default 0) |
| issued_quantity | DECIMAL(18,4) | NOT NULL | Quantity physically issued to production (default 0) |
| consumed_quantity | DECIMAL(18,4) | NOT NULL | Quantity actually consumed (default 0) |
| returned_quantity | DECIMAL(18,4) | NOT NULL | Quantity returned from production floor (default 0) |
| shortage_quantity | DECIMAL(18,4) | NOT NULL | Computed: required - reserved (when \> 0) |
| uom | NVARCHAR(20) | NOT NULL | Unit of measure |
| warehouse_id | BIGINT | NOT NULL | FK to Warehouses --- source warehouse for this material |
| is_critical | BIT | NOT NULL | From BOM line --- mandatory for production readiness |
| status | TINYINT | NOT NULL | PMRStatus: Pending=0, PartiallyReserved=1, FullyReserved=2, Issued=3, Consumed=4, Cancelled=5 |
| required_date | DATE | NOT NULL | When material is needed (from production order) |
| created_at | DATETIMEOFFSET | NOT NULL | Creation timestamp |
| updated_at | DATETIMEOFFSET | NOT NULL | Last update timestamp |

**12.2 BOM Explosion Calculation**

> // When Production Order transitions Draft → Planned:
>
> var bom = await GetActiveBOM(productId, variantId, warehouseId);
>
> foreach (var bomLine in bom.Lines)
>
> {
>
> var netQty = productionOrder.PlannedQuantity \* bomLine.Quantity / bom.BaseQuantity;
>
> var scrapAllowance = netQty \* (bomLine.ScrapPercentage / 100);
>
> var grossQty = netQty + scrapAllowance;
>
> var pmr = new ProductionMaterialRequirement
>
> {
>
> ProductionOrderId = productionOrder.Id,
>
> BomLineId = bomLine.Id,
>
> ProductId = bomLine.MaterialProductId,
>
> NetQuantity = netQty,
>
> ScrapAllowance = scrapAllowance,
>
> RequiredQuantity = grossQty,
>
> ShortageQuantity = grossQty, // initially all shortage
>
> IsCritical = bomLine.IsCritical,
>
> Status = PMRStatus.Pending
>
> };
>
> // Then trigger allocation engine to reserve from available inventory
>
> }

**12.3 Example: BOM Explosion**

Production Order: 80 Printed T-Shirts

BOM V2 (base_quantity = 1):

| **Material** | **BOM Qty/Unit** | **Scrap %** | **Net Qty** | **Scrap Allowance** | **Gross Required** |
|----|----|----|----|----|----|
| Plain T-Shirt | 1 PCS | 0% | 80 | 0 | 80 |
| Printing Ink | 0.07 L | 5% | 5.6 L | 0.28 L | 5.88 L |
| Packaging Box | 1 PCS | 2% | 80 | 1.6 | 81.6 ≈ 82 |

> ⚠ *Scrap allowance ensures sufficient material is reserved to account for expected wastage during production.*

**13. Supply Requirement Engine**

A Supply Requirement is created whenever a material shortage is detected --- whether for production material requirements, sales order fulfillment, or any future demand type. It drives the system to procure, transfer, or manufacture the needed material.

**13.1 Supply Requirement Entity**

**material.SupplyRequirements (NEW)**

| **Column** | **Type** | **Nullable** | **Description** |
|----|----|----|----|
| id | BIGINT IDENTITY | NOT NULL | PK |
| org_id | BIGINT | NOT NULL | Tenant discriminator |
| supply_number | NVARCHAR(50) | NOT NULL | Generated via document_number_sequences (SR-YYYYMMDD-SEQ) |
| product_id | BIGINT | NOT NULL | FK to Products --- what is needed |
| product_variant_id | BIGINT | NULL | FK to ProductVariants |
| quantity_required | DECIMAL(18,4) | NOT NULL | Total quantity needed |
| quantity_ordered | DECIMAL(18,4) | NOT NULL | Quantity covered by PO/Transfer/Production (default 0) |
| quantity_received | DECIMAL(18,4) | NOT NULL | Quantity received into inventory (default 0) |
| quantity_outstanding | DECIMAL(18,4) | NOT NULL | Computed: required - received |
| demand_source_type | TINYINT | NOT NULL | DemandSourceType: ProductionMaterialReq=0, FulfillmentReq=1, Replenishment=2, ServiceOrder=3 |
| demand_source_id | BIGINT | NOT NULL | FK to source demand record |
| supply_method | TINYINT | NOT NULL | SupplyMethod: Purchase=0, Manufacture=1, Transfer=2 |
| supply_source_type | TINYINT | NULL | SupplySourceType: PurchaseOrder=0, TransferOrder=1, ProductionOrder=2 |
| supply_source_id | BIGINT | NULL | FK to the PO/TO/ProdOrder created to fulfill this SR |
| warehouse_id | BIGINT | NOT NULL | FK to Warehouses --- where material is needed |
| required_date | DATE | NOT NULL | When the supply is needed |
| priority | TINYINT | NOT NULL | SupplyPriority: Low=0, Normal=1, High=2, Urgent=3 |
| status | TINYINT | NOT NULL | SupplyReqStatus: Open=0, Planned=1, Ordered=2, PartiallyReceived=3, Fulfilled=4, Cancelled=5 |
| trace_id | UNIQUEIDENTIFIER | NULL | Correlation GUID for traceability |
| notes | NVARCHAR(1000) | NULL | Free-text notes |
| created_at | DATETIMEOFFSET | NOT NULL | Creation timestamp |
| updated_at | DATETIMEOFFSET | NOT NULL | Last update timestamp |

**13.2 Supply Requirement Lifecycle**

> Shortage detected in PMR/Fulfillment Requirement
>
> ↓
>
> Supply Requirement created (status: Open)
>
> ↓
>
> Supply method determined from product.supply_method:
>
> Purchase → Creates Purchase Requisition / Purchase Order
>
> Manufacture → Creates Production Order (nested)
>
> Transfer → Creates Transfer Order
>
> ↓
>
> Supply Requirement status: Ordered (supply_source linked)
>
> ↓
>
> Goods Receipt / FGR / Transfer Receipt
>
> ↓
>
> quantity_received updated
>
> ↓
>
> If fully received → status: Fulfilled
>
> If partially received → status: PartiallyReceived

**13.3 Traceability Chain**

> Production Order (PO-PROD-001)
>
> → PMR (Plain Shirt = 80, shortage = 30)
>
> → Supply Requirement (SR-001, qty = 30, method = Purchase)
>
> → Purchase Order (PO-001, qty = 30)
>
> → Goods Receipt (GRN-001, qty = 10)
>
> → Goods Receipt (GRN-002, qty = 20)
>
> → Inventory increased
>
> → Allocation Engine evaluates
>
> → PMR shortage reduced
>
> → Production Readiness recalculated
>
> **🔴 CRITICAL:** A Purchase Order is a SUPPLY SOURCE, not a Production Order dependency. The Allocation Engine determines which demand receives the received inventory, not the Supply Requirement\'s original link.

**14. Shared Allocation Engine**

The Allocation Engine is a shared service that manages competing demands against available or incoming supply. It is NOT implemented inside Sales or Manufacturing --- it is a cross-cutting service in the inventory schema, callable by any demand source.

**14.1 Architecture**

> DEMAND SOURCES SUPPLY SOURCES
>
> ───────────────── ─────────────────
>
> Sales Order (Fulfillment Req) Existing Inventory (on_hand - reserved)
>
> Production Material Req Purchase Order (incoming)
>
> Service Order Production Order (expected FGR)
>
> Replenishment Transfer Order (incoming)
>
> Maintenance Goods Receipt (just received)
>
> Project Other approved supply
>
> Other future demand types
>
> DEMAND
>
> ↓
>
> Fulfillment / Material Requirement
>
> ↓
>
> ┌─────────────────┐
>
> │ ALLOCATION │
>
> │ ENGINE │
>
> └─────────────────┘
>
> ↑
>
> SUPPLY
>
> ↑
>
> Inventory / PO / Transfer / Production

**14.2 Allocation Records Entity**

**inventory.AllocationRecords (NEW)**

| **Column** | **Type** | **Nullable** | **Description** |
|----|----|----|----|
| id | BIGINT IDENTITY | NOT NULL | PK |
| org_id | BIGINT | NOT NULL | Tenant discriminator |
| product_id | BIGINT | NOT NULL | FK to Products |
| product_variant_id | BIGINT | NULL | FK to ProductVariants |
| warehouse_id | BIGINT | NOT NULL | FK to Warehouses |
| allocated_quantity | DECIMAL(18,4) | NOT NULL | Quantity allocated |
| consumed_quantity | DECIMAL(18,4) | NOT NULL | Quantity already consumed/delivered (default 0) |
| remaining_quantity | DECIMAL(18,4) | NOT NULL | Computed: allocated - consumed |
| demand_type | TINYINT | NOT NULL | AllocationDemandType: SalesOrder=0, ProductionMaterial=1, ServiceOrder=2, Replenishment=3, Transfer=4 |
| demand_id | BIGINT | NOT NULL | FK to the demand record (FulfillmentReq, PMR, etc.) |
| demand_line_id | BIGINT | NULL | FK to demand line if applicable |
| supply_type | TINYINT | NOT NULL | AllocationSupplyType: OnHand=0, PurchaseOrder=1, ProductionOrder=2, TransferOrder=3 |
| supply_id | BIGINT | NULL | FK to supply source (NULL for on-hand stock) |
| allocation_type | TINYINT | NOT NULL | AllocationType: Planned=0, Soft=1, Firm=2, Reserved=3 |
| priority | INT | NOT NULL | Calculated priority score (lower = higher priority) |
| required_date | DATE | NOT NULL | When the demand needs the material |
| status | TINYINT | NOT NULL | AllocationStatus: Active=0, Consumed=1, Released=2, Cancelled=3 |
| allocated_by | BIGINT | NOT NULL | FK to Users or SYSTEM for auto-allocation |
| allocated_at | DATETIMEOFFSET | NOT NULL | When the allocation was created |
| released_at | DATETIMEOFFSET | NULL | When the allocation was released |
| row_version | ROWVERSION | NOT NULL | Concurrency token |

> **🔴 CRITICAL:** InventoryBalance must NOT contain a hardcoded SalesOrderId or ProductionOrderId. Inventory represents physical stock. Allocation records represent demand ownership.

**14.3 Allocation Rules Entity**

**inventory.AllocationRules (NEW)**

| **Column** | **Type** | **Nullable** | **Description** |
|----|----|----|----|
| id | BIGINT IDENTITY | NOT NULL | PK |
| org_id | BIGINT | NOT NULL | Tenant discriminator |
| rule_name | NVARCHAR(100) | NOT NULL | Human-readable rule name |
| priority_order | INT | NOT NULL | Evaluation order (lower = evaluated first) |
| demand_type_filter | TINYINT | NULL | Apply only to this demand type (NULL = all) |
| sort_field | TINYINT | NOT NULL | AllocationSortField: RequiredDate=0, Priority=1, OrderDate=2, CreatedAt=3, ManualOverride=4 |
| sort_direction | TINYINT | NOT NULL | SortDirection: Ascending=0, Descending=1 |
| is_active | BIT | NOT NULL | Whether this rule is currently active |
| created_at | DATETIMEOFFSET | NOT NULL | Creation timestamp |

**14.4 Default Allocation Priority Rules**

| **Priority** | **Sort Field** | **Direction** | **Description** |
|----|----|----|----|
| 1 | Business Priority | Descending | Urgent demands first (Urgent \> High \> Normal \> Low) |
| 2 | Required Date | Ascending | Earlier need dates first |
| 3 | Customer/Order Priority | Descending | Higher-priority customers first |
| 4 | Order Date | Ascending | Older orders first (FIFO) |
| 5 | Created At | Ascending | Tie-breaker: first-come-first-served |
| 6 | Manual Override | N/A | Planner can override any calculated allocation |

**14.5 Allocation Types**

| **Type** | **Description** | **Can be Reallocated?** | **Decreases Available Qty?** |
|----|----|----|----|
| Planned | System-calculated allocation based on expected supply | Yes --- automatic | No |
| Soft | Tentative allocation that may be reassigned by the engine | Yes --- automatic or manual | Yes |
| Firm | Committed allocation that requires manual override to change | Only with ALLOCATION_ADMIN permission | Yes |
| Reserved | Physical reservation via IStockReservationService --- stock is locked | Only with ALLOCATION_ADMIN + reason | Yes (hard lock) |

**14.6 Allocation Engine Service Interface**

> public interface IAllocationEngine
>
> {
>
> // Run allocation for a specific product/warehouse across all demands
>
> Task\<AllocationResult\> AllocateAsync(
>
> long productId, long? variantId, long warehouseId,
>
> CancellationToken ct);
>
> // Run allocation for a specific demand
>
> Task\<AllocationResult\> AllocateForDemandAsync(
>
> AllocationDemandType demandType, long demandId,
>
> CancellationToken ct);
>
> // Release an allocation (e.g., demand cancelled)
>
> Task ReleaseAsync(long allocationId, string reason, CancellationToken ct);
>
> // Reallocate: release from one demand, assign to another
>
> Task\<AllocationResult\> ReallocateAsync(
>
> long fromAllocationId, AllocationDemandType toDemandType,
>
> long toDemandId, decimal quantity, string reason,
>
> CancellationToken ct);
>
> // Consume: mark allocated qty as used (material issued, goods delivered)
>
> Task ConsumeAsync(long allocationId, decimal quantity, CancellationToken ct);
>
> // Query available quantity for a product/warehouse
>
> Task\<AvailabilityResult\> GetAvailabilityAsync(
>
> long productId, long? variantId, long warehouseId,
>
> CancellationToken ct);
>
> }

**14.7 Competing Demands Example**

**This example demonstrates the Allocation Engine\'s priority-based behavior:**

> Scenario: Three competing demands for Plain T-Shirt at WH-01
>
> Production A: Requires 5 PCS, Required Date: 25 Sep, Priority: Normal
>
> Sales Order B: Requires 10 PCS, Required Date: 23 Sep, Priority: High
>
> Production C: Requires 5 PCS, Required Date: 24 Sep, Priority: Normal
>
> Current Stock: 3 PCS available
>
> Expected PO: 2 PCS arriving \~26 Sep
>
> Total Demand: 20 PCS
>
> Total Supply: 5 PCS (3 on-hand + 2 incoming)
>
> Allocation Engine evaluates using default priority rules:
>
> 1\. Sales Order B wins (Higher Priority + Earlier Required Date)
>
> → Allocated: 3 (on-hand) + 2 (planned from PO) = 5 PCS
>
> → Remaining demand: 5 PCS (Supply Requirement created)
>
> 2\. Production C: 0 allocated (shortage = 5, SR created)
>
> 3\. Production A: 0 allocated (shortage = 5, SR created)
>
> Productions A and C remain MATERIAL PENDING.
>
> NOTE: PO-001 (created by Production A\'s SR) is NOT automatically owned
>
> by Production A. The Allocation Engine evaluates ALL demands when the
>
> PO is received.

**14.8 Concurrency Control**

Two users/processes attempting to allocate the same stock must not cause double-allocation:

- AllocationRecords uses RowVersion for optimistic concurrency

- The allocation engine operates within a SERIALIZABLE transaction for the specific (product_id, warehouse_id) combination

- Available quantity is calculated inside the transaction: available = on_hand - SUM(active firm/reserved allocations)

- If RowVersion conflict occurs, the engine retries (up to 3 times) with exponential backoff

- Integration with IStockReservationService for firm/reserved allocations uses the existing row-level lock pattern

**15. Goods Receipt Integration**

When a Purchase Order is received via the existing GRN process, the Allocation Engine is triggered to re-evaluate outstanding demands.

**15.1 Post-GRN Allocation Flow**

> Purchase Order received → Goods Receipt created
>
> ↓
>
> Inventory stock_balances.on_hand_quantity increased
>
> ↓
>
> MediatR domain event: GoodsReceiptCompletedEvent
>
> ↓
>
> AllocationEngineHandler receives event
>
> ↓
>
> IAllocationEngine.AllocateAsync(productId, variantId, warehouseId)
>
> ↓
>
> Engine evaluates ALL outstanding demands for this product/warehouse:
>
> \- Open FulfillmentRequirements (Sales Orders)
>
> \- Pending ProductionMaterialRequirements
>
> \- Other registered demands
>
> ↓
>
> Allocations created/updated based on priority rules
>
> ↓
>
> For each affected PMR:
>
> \- Shortage recalculated
>
> \- Production Order readiness recalculated
>
> \- If all materials ready → Production Order → READY

**15.2 Partial Receipt Example**

| **Event** | **Required** | **Received** | **Remaining** | **SR Status** | **PMR Status** |
|----|----|----|----|----|----|
| SR created | 30 | 0 | 30 | Ordered | Pending |
| GRN-001 (10 units) | 30 | 10 | 20 | PartiallyReceived | PartiallyReserved |
| GRN-002 (20 units) | 30 | 30 | 0 | Fulfilled | FullyReserved |
| Production Readiness | \- | \- | \- | \- | Ready (if all PMRs fulfilled) |

**16. Material Issue**

Material Issue physically moves materials from inventory to the production floor. It uses the Production Material Requirements to determine what to issue.

**16.1 Material Issue Entity**

**material.MaterialIssues (NEW)**

| **Column** | **Type** | **Nullable** | **Description** |
|----|----|----|----|
| id | BIGINT IDENTITY | NOT NULL | PK |
| org_id | BIGINT | NOT NULL | Tenant discriminator |
| issue_number | NVARCHAR(50) | NOT NULL | Generated via document_number_sequences (MI-YYYYMMDD-SEQ) |
| production_order_id | BIGINT | NOT NULL | FK to ProductionOrders |
| warehouse_id | BIGINT | NOT NULL | FK to Warehouses --- source warehouse |
| status | TINYINT | NOT NULL | MaterialIssueStatus: Draft=0, Confirmed=1, Reversed=2 |
| issued_by | BIGINT | NOT NULL | FK to Users |
| issued_at | DATETIMEOFFSET | NOT NULL | Issue timestamp |
| notes | NVARCHAR(500) | NULL | Issue notes |
| created_at | DATETIMEOFFSET | NOT NULL | Creation timestamp |

**16.2 Material Issue Lines Entity**

**material.MaterialIssueLines (NEW)**

| **Column** | **Type** | **Nullable** | **Description** |
|----|----|----|----|
| id | BIGINT IDENTITY | NOT NULL | PK |
| org_id | BIGINT | NOT NULL | Tenant discriminator |
| material_issue_id | BIGINT | NOT NULL | FK to MaterialIssues |
| pmr_id | BIGINT | NOT NULL | FK to ProductionMaterialRequirements |
| product_id | BIGINT | NOT NULL | FK to Products |
| product_variant_id | BIGINT | NULL | FK to ProductVariants |
| issued_quantity | DECIMAL(18,4) | NOT NULL | Quantity issued |
| uom | NVARCHAR(20) | NOT NULL | Unit of measure |
| batch_number | NVARCHAR(50) | NULL | Batch/lot reference if applicable |
| bin_location | NVARCHAR(50) | NULL | Source bin/location |

**16.3 Material Issue Business Rules**

- Material can only be issued when Production Order status = Ready or InProgress

- Issued quantity cannot exceed reserved quantity for that PMR

- Material issue creates inventory stock_transactions with movement_type = MATERIAL_ISSUE

- stock_balances.on_hand_quantity decreases by issued quantity

- Allocations are consumed (AllocationRecord.consumed_quantity increases)

- PMR.issued_quantity increases, PMR status → Issued

- Partial issue is allowed --- issue what is available, remaining stays reserved

**16.4 Material Issue Types**

| **Type** | **Description** | **Inventory Impact** |
|----|----|----|
| Full Issue | All PMR materials issued at once | on_hand decreases for all materials |
| Partial Issue | Some materials issued, others remain | on_hand decreases for issued materials only |
| Return Material | Unused material returned from production floor | on_hand increases, PMR.returned_quantity increases |
| Excess Material | Additional material issued beyond PMR requirement | on_hand decreases, requires PROD_MANAGER approval |
| Scrap Write-off | Material scrapped during production | Creates PRODUCTION_SCRAP movement type |

**17. Production Execution**

Production execution tracks the actual manufacturing process from start to completion.

**17.1 Starting Production**

- Production Order must be in Ready status with material_readiness = Ready

- User with PROD_START permission triggers the transition to InProgress

- actual_start_date is set to current timestamp

- Materials should be issued (via Material Issue) before or during production

**17.2 Reporting Production Output**

- As production proceeds, produced_quantity is updated

- Partial production reporting is supported: report quantities incrementally

- Over-production (produced \> planned) is allowed with audit trail

- Under-production (produced \< planned) triggers a review: remaining demand may need additional production order

**17.3 Production Completion**

- When all production is done, user transitions to QualityInspection status

- actual_end_date is set

- Production cannot skip Quality Inspection (even if it is just a formality --- the step must be recorded)

**18. Quality Inspection**

**18.1 Quality Inspection Entity**

**material.QualityInspections (NEW)**

| **Column** | **Type** | **Nullable** | **Description** |
|----|----|----|----|
| id | BIGINT IDENTITY | NOT NULL | PK |
| org_id | BIGINT | NOT NULL | Tenant discriminator |
| inspection_number | NVARCHAR(50) | NOT NULL | Generated via document_number_sequences (QI-YYYYMMDD-SEQ) |
| production_order_id | BIGINT | NOT NULL | FK to ProductionOrders |
| inspected_quantity | DECIMAL(18,4) | NOT NULL | Total quantity inspected |
| accepted_quantity | DECIMAL(18,4) | NOT NULL | Quantity passed inspection |
| rejected_quantity | DECIMAL(18,4) | NOT NULL | Quantity failed inspection |
| hold_quantity | DECIMAL(18,4) | NOT NULL | Quantity held for further review |
| rework_quantity | DECIMAL(18,4) | NOT NULL | Quantity sent for rework |
| overall_result | TINYINT | NOT NULL | InspectionResult: Passed=0, PartiallyPassed=1, Rejected=2, Hold=3 |
| inspected_by | BIGINT | NOT NULL | FK to Users --- quality inspector |
| inspected_at | DATETIMEOFFSET | NOT NULL | Inspection timestamp |
| notes | NVARCHAR(2000) | NULL | Inspection notes and observations |
| created_at | DATETIMEOFFSET | NOT NULL | Creation timestamp |

**18.2 Quality Inspection Lines**

**material.QualityInspectionLines (NEW)**

| **Column** | **Type** | **Nullable** | **Description** |
|----|----|----|----|
| id | BIGINT IDENTITY | NOT NULL | PK |
| org_id | BIGINT | NOT NULL | Tenant discriminator |
| inspection_id | BIGINT | NOT NULL | FK to QualityInspections |
| check_name | NVARCHAR(200) | NOT NULL | Name of the quality check (e.g., Visual Inspection, Dimensional Check) |
| result | TINYINT | NOT NULL | QILineResult: Pass=0, Fail=1, Hold=2, Rework=3 |
| quantity_checked | DECIMAL(18,4) | NOT NULL | Quantity subjected to this check |
| quantity_passed | DECIMAL(18,4) | NOT NULL | Quantity that passed this check |
| quantity_failed | DECIMAL(18,4) | NOT NULL | Quantity that failed this check |
| defect_code | NVARCHAR(50) | NULL | Defect classification code |
| notes | NVARCHAR(500) | NULL | Check-specific notes |

**18.3 Quality Decision Impact**

| **Decision** | **Inventory Impact** | **Production Order Update** | **Next Step** |
|----|----|----|----|
| Passed | Eligible for FGR | accepted_quantity += qty | Create Finished Goods Receipt |
| Rejected | Scrap or disposal | rejected_quantity += qty | PRODUCTION_SCRAP stock movement |
| Hold | Quarantine --- not available for allocation | No qty update until resolved | Re-inspect or escalate |
| Rework | Returned to production floor | rework triggers new production cycle | Material may need re-issue |

**18.4 Quality Rules**

- Inspected quantity must equal production order\'s produced_quantity

- accepted + rejected + hold + rework must equal inspected

- Rejected quantity must NOT become saleable Finished Goods

- Hold quantity is quarantined --- excluded from available inventory until resolved

- Rework creates a linked production cycle (same Production Order, new Material Issue if needed)

- Quality Inspector cannot be the Production Operator (separation of duties)

**19. Finished Goods Receipt**

Finished Goods Receipt (FGR) is a separate inventory transaction that moves accepted production output into finished-goods inventory.

**19.1 FGR Entity**

**material.FinishedGoodsReceipts (NEW)**

| **Column** | **Type** | **Nullable** | **Description** |
|----|----|----|----|
| id | BIGINT IDENTITY | NOT NULL | PK |
| org_id | BIGINT | NOT NULL | Tenant discriminator |
| fgr_number | NVARCHAR(50) | NOT NULL | Generated via document_number_sequences (FGR-YYYYMMDD-SEQ) |
| production_order_id | BIGINT | NOT NULL | FK to ProductionOrders |
| quality_inspection_id | BIGINT | NOT NULL | FK to QualityInspections |
| warehouse_id | BIGINT | NOT NULL | FK to Warehouses --- destination for finished goods |
| total_quantity | DECIMAL(18,4) | NOT NULL | Total accepted quantity received |
| status | TINYINT | NOT NULL | FGRStatus: Draft=0, Confirmed=1, Reversed=2 |
| received_by | BIGINT | NOT NULL | FK to Users |
| received_at | DATETIMEOFFSET | NOT NULL | Receipt timestamp |
| notes | NVARCHAR(500) | NULL | Receipt notes |
| created_at | DATETIMEOFFSET | NOT NULL | Creation timestamp |

**19.2 FGR Lines**

**material.FinishedGoodsReceiptLines (NEW)**

| **Column** | **Type** | **Nullable** | **Description** |
|----|----|----|----|
| id | BIGINT IDENTITY | NOT NULL | PK |
| org_id | BIGINT | NOT NULL | Tenant discriminator |
| fgr_id | BIGINT | NOT NULL | FK to FinishedGoodsReceipts |
| product_id | BIGINT | NOT NULL | FK to Products --- the finished good |
| product_variant_id | BIGINT | NULL | FK to ProductVariants |
| quantity | DECIMAL(18,4) | NOT NULL | Quantity received into inventory |
| uom | NVARCHAR(20) | NOT NULL | Unit of measure |
| bin_location | NVARCHAR(50) | NULL | Destination bin/location |

**19.3 FGR Processing**

> FGR Confirmed:
>
> 1\. stock_balances.on_hand_quantity += fgr.total_quantity
>
> 2\. stock_transactions created with movement_type = FINISHED_GOODS_RECEIPT
>
> 3\. Production Order: accepted_quantity += fgr.total_quantity
>
> 4\. Production Order status → Completed
>
> 5\. MediatR event: FinishedGoodsReceivedEvent
>
> 6\. Allocation Engine triggered for the finished product
>
> 7\. Outstanding FulfillmentRequirements (Sales Orders) evaluated
>
> 8\. Allocations created based on priority rules
>
> 9\. Sales Order fulfillment status updated
>
> **🔴 CRITICAL:** FGR does NOT directly hardcode allocation to any Sales Order. After FGR increases inventory, the Allocation Engine evaluates ALL outstanding demands for that product and allocates based on configurable priority rules.

**19A. Production Ledger**

The Production Ledger provides a complete double-entry style record of inventory movements against each Production Order. It shows exactly which materials were debited (consumed) and which finished goods were credited (produced), giving full transparency into the cost and material flow of manufacturing.

> **🔴 CRITICAL:** The Production Ledger is auto-populated from existing stock_transactions. It is a materialized VIEW / query-based report, NOT a separate transactional table. All underlying data comes from MaterialIssues (debit side) and FinishedGoodsReceipts (credit side), ensuring single source of truth.

**19A.1 Production Ledger Entity**

**material.ProductionLedgerEntries (NEW --- materialized via view or query)**

| **Column** | **Type** | **Nullable** | **Description** |
|----|----|----|----|
| id | BIGINT IDENTITY | NOT NULL | PK |
| org_id | BIGINT | NOT NULL | Tenant discriminator |
| production_order_id | BIGINT | NOT NULL | FK to ProductionOrders --- the production this entry belongs to |
| production_order_number | NVARCHAR(50) | NOT NULL | Denormalized PROD number for display |
| entry_type | TINYINT | NOT NULL | LedgerEntryType: Debit=0 (material consumed), Credit=1 (finished good produced) |
| product_id | BIGINT | NOT NULL | FK to Products --- the material consumed or FG produced |
| product_variant_id | BIGINT | NULL | FK to ProductVariants |
| product_name | NVARCHAR(200) | NOT NULL | Denormalized product name for display |
| product_type | TINYINT | NOT NULL | ProductType of the item (RawMaterial, Component, FinishedGood, etc.) |
| quantity | DECIMAL(18,4) | NOT NULL | Quantity consumed (debit) or produced (credit) |
| uom | NVARCHAR(20) | NOT NULL | Unit of measure |
| warehouse_id | BIGINT | NOT NULL | FK to Warehouses --- source (debit) or destination (credit) |
| warehouse_name | NVARCHAR(100) | NOT NULL | Denormalized warehouse name |
| source_document_type | NVARCHAR(20) | NOT NULL | MATERIAL_ISSUE or FINISHED_GOODS_RECEIPT |
| source_document_id | BIGINT | NOT NULL | FK to MaterialIssues or FinishedGoodsReceipts |
| source_document_number | NVARCHAR(50) | NOT NULL | MI or FGR document number |
| stock_transaction_id | BIGINT | NOT NULL | FK to stock_transactions --- the underlying inventory movement |
| movement_type | NVARCHAR(30) | NOT NULL | MATERIAL_ISSUE, FINISHED_GOODS_RECEIPT, PRODUCTION_SCRAP, MATERIAL_RETURN |
| transaction_date | DATETIMEOFFSET | NOT NULL | When the inventory movement occurred |
| notes | NVARCHAR(500) | NULL | Optional notes |
| created_at | DATETIMEOFFSET | NOT NULL | Entry creation timestamp |

**19A.2 Ledger Entry Types**

| **Entry Type** | **Int** | **Description** | **Inventory Impact** | **Source Document** |
|----|----|----|----|----|
| Debit | 0 | Material consumed in production | stock_balance.on_hand DECREASES | MaterialIssue (MI) |
| Credit | 1 | Finished good produced from production | stock_balance.on_hand INCREASES | FinishedGoodsReceipt (FGR) |
| Debit (Scrap) | 0 | Material scrapped during production | stock_balance.on_hand DECREASES | QualityInspection rejection |
| Credit (Return) | 1 | Unused material returned to inventory | stock_balance.on_hand INCREASES | MaterialIssue reversal |

**19A.3 How Ledger Entries Are Created**

- Material Issue Confirmed → Debit entries created for each MaterialIssueLine (one per material consumed)

- Finished Goods Receipt Confirmed → Credit entry created for the finished product received into inventory

- Quality Inspection Rejection → Debit entry for scrapped quantity (movement_type = PRODUCTION_SCRAP)

- Material Return → Credit entry for returned unused material (movement_type = MATERIAL_RETURN)

- All entries link back to the underlying stock_transaction for full audit trail

**19A.4 Production Ledger Example**

**Production Order PROD-001: Printed T-Shirt x 80, BOM V1**

| **Entry** | **Type** | **Product** | **Product Type** | **Qty** | **UOM** | **Warehouse** | **Source Doc** | **Movement Type** |
|----|----|----|----|----|----|----|----|----|
| 1 | DEBIT | Plain T-Shirt | RawMaterial | 80 | PCS | WH-01 | MI-001 | MATERIAL_ISSUE |
| 2 | DEBIT | Printing Ink | RawMaterial | 4.2 | L | WH-01 | MI-001 | MATERIAL_ISSUE |
| 3 | DEBIT | Packaging Box | Consumable | 82 | PCS | WH-01 | MI-001 | MATERIAL_ISSUE |
| 4 | DEBIT | Printed T-Shirt (rejected) | FinishedGood | 4 | PCS | WH-01 | QI-001 | PRODUCTION_SCRAP |
| 5 | CREDIT | Printed T-Shirt | FinishedGood | 76 | PCS | WH-01 | FGR-001 | FINISHED_GOODS_RECEIPT |

Net result: 3 raw materials consumed → 76 finished goods produced (4 rejected as scrap).

**19A.5 Chained Manufacturing Ledger Example**

**Two-stage chain: Steel Bolt (FG) → Bolt Assembly Kit (FG)**

Stage 1 --- PROD-A: Steel Bolt (M10) x 100:

| **Entry** | **Type** | **Product** | **Product Type** | **Qty** | **UOM** | **Source Doc** |
|----|----|----|----|----|----|----|
| 1 | DEBIT | Steel Rod | RawMaterial | 100 | PCS | MI-010 |
| 2 | DEBIT | Threading Oil | Consumable | 1.0 | L | MI-010 |
| 3 | CREDIT | Steel Bolt (M10) | FinishedGood | 98 | PCS | FGR-010 |

Stage 2 --- PROD-B: Bolt Assembly Kit x 9:

| **Entry** | **Type** | **Product** | **Product Type** | **Qty** | **UOM** | **Source Doc** |
|----|----|----|----|----|----|----|
| 4 | DEBIT | Steel Bolt (M10) | FinishedGood→BOM Input | 90 | PCS | MI-020 |
| 5 | DEBIT | Washer | RawMaterial | 90 | PCS | MI-020 |
| 6 | DEBIT | Nut | RawMaterial | 90 | PCS | MI-020 |
| 7 | DEBIT | Plastic Box | Consumable | 9 | PCS | MI-020 |
| 8 | CREDIT | Bolt Assembly Kit | FinishedGood | 9 | PCS | FGR-020 |

> ⚠ *Entry \#4 shows a FinishedGood (Steel Bolt) being consumed as raw material in Stage 2. The Production Ledger makes this cross-production traceability visible.*

**19A.6 Production Ledger Business Rules**

- Every Production Order MUST have at least one Debit entry (materials consumed) and one Credit entry (finished goods received) before it can transition to Completed status

- Ledger entries are IMMUTABLE once created --- reversals create offsetting entries (e.g., material return creates a Credit to offset the original Debit)

- The sum of all Debit quantities by product should match the total material issued quantities on the Production Order\'s PMRs

- The sum of all Credit quantities should match the Production Order\'s accepted_quantity (from QI)

- Production Ledger entries inherit the trace_id from the Production Order for end-to-end traceability

- For chained manufacturing, each Production Order has its own independent ledger --- cross-referencing is done via product_id and Supply Requirement traceability

**19A.7 Production Ledger API**

| **Method** | **Endpoint** | **Purpose** | **Permission** |
|----|----|----|----|
| GET | /api/production-orders/{id}/ledger | Get production ledger for a specific production order | PROD_VIEW |
| GET | /api/production-ledger | List/filter ledger entries across production orders | PROD_VIEW |
| GET | /api/production-ledger/summary | Aggregated ledger summary by product/period/warehouse | REPORTS_VIEW |
| GET | /api/production-orders/{id}/cost-summary | Material cost vs production output summary for a PO | PROD_VIEW |

**19A.8 Production Ledger UI**

- Production Order Detail → \'Ledger\' tab: Shows debit/credit entries in chronological order with running totals

- Color coding: Debit entries in RED (material consumed), Credit entries in GREEN (finished goods produced)

- Summary card at top: Total Materials Consumed (count + qty), Total Produced (qty), Scrap (qty), Yield %

- Filter by entry_type, product, movement_type, date range

- Export to Excel/PDF for audit purposes

- Cross-Production Ledger view: aggregate ledger across all production orders, filterable by product, date range, warehouse

> ┌───────────────────────────────────────────────────────────────────────┐
>
> │ Production Ledger --- PROD-20260924-001 (Printed T-Shirt x 80) │
>
> │ │
>
> │ ┌─ Summary ────────────────────────────────────────────────────────┐ │
>
> │ │ Materials Consumed: 3 items │ Finished Goods: 76 PCS │ │
>
> │ │ Scrap: 4 PCS │ Yield: 95.0% │ │
>
> │ └──────────────────────────────────────────────────────────────────┘ │
>
> │ │
>
> │ Date │ Type │ Product │ Qty │ UOM │ Source │
>
> │ ────────────┼────────┼──────────────────┼────────┼─────┼────────── │
>
> │ 24 Sep 08:30│ DEBIT │ Plain T-Shirt │ 80 │ PCS │ MI-001 │
>
> │ 24 Sep 08:30│ DEBIT │ Printing Ink │ 4.2 │ L │ MI-001 │
>
> │ 24 Sep 08:30│ DEBIT │ Packaging Box │ 82 │ PCS │ MI-001 │
>
> │ 24 Sep 16:00│ DEBIT │ Printed T-Shirt │ 4 │ PCS │ QI-001 │
>
> │ │ (scrap)│ (rejected) │ │ │ │
>
> │ 24 Sep 16:30│ CREDIT │ Printed T-Shirt │ 76 │ PCS │ FGR-001 │
>
> │ ────────────┼────────┼──────────────────┼────────┼─────┼────────── │
>
> │ TOTAL │ DEBIT │ 3 materials │ --- │ │ │
>
> │ │ CREDIT │ 1 finished good │ 76 │ PCS │ │
>
> └───────────────────────────────────────────────────────────────────────┘

**20. Sales Fulfillment After Production**

Once the Allocation Engine assigns finished goods to a Sales Order, the delivery process begins using the existing logistics.delivery_orders infrastructure.

**20.1 Post-Allocation Flow**

> FGR complete → Allocation Engine runs
>
> ↓
>
> Allocation assigned to Fulfillment Requirement (Sales Order)
>
> ↓
>
> Fulfillment Requirement status updated (FullyAllocated / PartiallyAllocated)
>
> ↓
>
> Delivery Order created with from_source_type = SALE_ORDER
>
> ↓
>
> Existing 15-status delivery state machine:
>
> DRAFT → RELEASED → PICKING → PICKED → PACKED → STAGED →
>
> PENDING_APPROVAL → GOODS_ISSUED → IN_TRANSIT → DELIVERED
>
> ↓
>
> Goods Issue: movement_type = SALES_DISPATCH
>
> ↓
>
> Sales Invoice generated

**20.2 Partial Fulfillment Example**

| **Event** | **SO Qty** | **Allocated** | **Produced** | **FGR** | **Delivered** | **Outstanding** |
|----|----|----|----|----|----|----|
| SO Created | 100 | 20 (stock) | \- | \- | 0 | 80 |
| Production Complete | 100 | 20 | 80 | \- | 0 | 80 |
| QI: 76 accepted, 4 rejected | 100 | 20 | 80 | \- | 0 | 80 |
| FGR: 76 units | 100 | 96 (20+76) | \- | 76 | 0 | 4 |
| Delivery 1: 96 units | 100 | 96 | \- | \- | 96 | 4 |
| Remaining: 4 units | 100 | \- | \- | \- | 96 | 4 |

The remaining 4 units generate a new Fulfillment Requirement that can be satisfied by:

- Additional Production Order

- Purchase (if product allows dual supply method)

- Transfer from another warehouse

- Backorder (wait for next production run)

- Partial cancellation (customer agreement)

**21. Status Models Summary**

**21.1 All Status Enums**

| **Entity** | **Status Values** | **Schema** |
|----|----|----|
| BillOfMaterials | Draft, Submitted, Approved, Active, Obsolete, Rejected | material |
| ProductionOrder | Draft, Planned, MaterialPending, Ready, InProgress, QualityInspection, Completed, Closed, Cancelled | material |
| PMR | Pending, PartiallyReserved, FullyReserved, Issued, Consumed, Cancelled | material |
| SupplyRequirement | Open, Planned, Ordered, PartiallyReceived, Fulfilled, Cancelled | material |
| FulfillmentRequirement | Open, PartiallyAllocated, FullyAllocated, InProduction, PartiallyFulfilled, Fulfilled, Cancelled | demand |
| QualityInspection | Passed, PartiallyPassed, Rejected, Hold | material |
| MaterialIssue | Draft, Confirmed, Reversed | material |
| FGR | Draft, Confirmed, Reversed | material |
| AllocationRecord | Active, Consumed, Released, Cancelled | inventory |

**22. Business Rules**

**22.1 Product Rules**

- BR-P01: A product with supply_method = Manufacture MUST have a valid Active BOM before any Production Order can be released

- BR-P02: A product cannot be both a BOM output (is_manufacturable) and a StockItem type simultaneously

- BR-P03: Changing a product\'s supply_method after it has been used in Production Orders requires PRODUCT_ADMIN permission

- BR-P04: A product becoming inactive must trigger review of all open Production Orders and Supply Requirements

**22.2 BOM Rules**

- BR-B01: Only one Active BOM per (product, variant, warehouse) combination

- BR-B02: A BOM used by any Production Order becomes immutable --- changes require a new version

- BR-B03: Circular BOM references are prohibited (Product A cannot be both input and output)

- BR-B04: BOM approval requires four-eyes principle (approver != submitter)

- BR-B05: BOM effective dates must not overlap for the same product/variant/warehouse

**22.3 Production Rules**

- BR-PR01: Production Order must reference an Active BOM at creation

- BR-PR02: BOM version is snapshotted and immutable after Planned status

- BR-PR03: Production cannot start until material_readiness = Ready

- BR-PR04: Over-production is allowed but audited

- BR-PR05: Under-production triggers remaining quantity review

- BR-PR06: Cancelled Production Order must release ALL reservations and cancel Supply Requirements

- BR-PR07: Production Order readiness is recalculated whenever a PMR\'s shortage changes

**22.4 Allocation Rules**

- BR-A01: Available quantity = on_hand - SUM(firm/reserved allocations)

- BR-A02: Allocation must never exceed available quantity

- BR-A03: Planned allocations can be reassigned by the engine automatically

- BR-A04: Firm allocations require ALLOCATION_ADMIN permission to reallocate

- BR-A05: Reserved (physical lock) allocations require ALLOCATION_ADMIN + documented reason

- BR-A06: A Purchase Order is a supply source, NOT automatic ownership by any demand

- BR-A07: Concurrent allocation is prevented via SERIALIZABLE transaction + RowVersion

**22.5 Supply Rules**

- BR-S01: Supply Requirements are created automatically when shortage is detected

- BR-S02: Supply method is determined from the product\'s supply_method configuration

- BR-S03: Supply Requirements maintain full traceability to their demand source

- BR-S04: Cancelling a demand cancels linked Supply Requirements (if not yet ordered)

- BR-S05: Partially received Supply Requirements stay open until fully fulfilled

- BR-S06: When a FinishedGood (is_bom_input=true) has a shortage, the Supply Requirement engine creates a child Production Order (supply_method=Manufacture) instead of a Purchase Order

- BR-S07: Chained manufacturing chains can be unlimited depth --- each level creates its own Production Order with independent BOM explosion

**22.6 Production Ledger Rules**

- BR-L01: Every Material Issue confirmation auto-creates DEBIT entries in the Production Ledger

- BR-L02: Every FGR confirmation auto-creates a CREDIT entry in the Production Ledger

- BR-L03: Quality Inspection rejection auto-creates a DEBIT (scrap) entry in the Production Ledger

- BR-L04: Material returns auto-create a CREDIT (return) entry in the Production Ledger

- BR-L05: Production Ledger entries are IMMUTABLE --- reversals are new offsetting entries, never deletes or updates

- BR-L06: Production Ledger entries inherit trace_id from the Production Order

- BR-L07: A Production Order cannot be Closed until the ledger balances: total debits and credits are recorded

**23. Validation Rules**

| **Entity** | **Rule ID** | **Validation** | **Error Message** |
|----|----|----|----|
| BOM | V-B01 | Product must have supply_method = Manufacture | Product {name} is not configured for manufacturing |
| BOM | V-B02 | All BOM line materials must have is_bom_input = true | Material {name} is not configured as a BOM input |
| BOM | V-B03 | base_quantity must be \> 0 | Base quantity must be greater than zero |
| BOM | V-B04 | No circular reference (output product != any input material) | Circular BOM reference detected |
| BOM | V-B05 | Version must be unique per product/variant/warehouse | BOM version {v} already exists for this product |
| ProdOrder | V-PO01 | Active BOM must exist for the product | No active BOM found for product {name} |
| ProdOrder | V-PO02 | planned_quantity \> 0 | Planned quantity must be greater than zero |
| ProdOrder | V-PO03 | required_date must be in the future | Required date must be a future date |
| ProdOrder | V-PO04 | Warehouse must be active | Warehouse {name} is not active |
| MaterialIssue | V-MI01 | Production Order must be Ready or InProgress | Cannot issue materials: production order not ready |
| MaterialIssue | V-MI02 | Issue qty cannot exceed reserved qty | Issue quantity ({qty}) exceeds reserved ({reserved}) |
| QI | V-QI01 | accepted + rejected + hold + rework = inspected | Inspection quantities do not sum to total |
| FGR | V-FGR01 | FGR quantity must equal QI accepted quantity | FGR quantity must match accepted inspection quantity |
| Allocation | V-AL01 | Allocated qty cannot exceed available qty | Insufficient available quantity for allocation |

**24. Exception Handling**

**24.1 Exception Scenarios**

| **\#** | **Scenario** | **System Behavior** |
|----|----|----|
| 1 | No BOM exists for manufactured product | Block Production Order creation; show error with product name |
| 2 | BOM exists but not Active (only Draft/Submitted) | Block Production Order release; prompt to approve/activate BOM |
| 3 | BOM expired (effective_to \< today) | Block new Production Orders; alert Production Manager |
| 4 | Multiple Active BOMs (data integrity issue) | Use most recently activated; log warning; alert admin |
| 5 | BOM changed after Production Order creation | Existing PO keeps old version; new POs get new version |
| 6 | Raw material completely unavailable | PMR shortage = full qty; Supply Requirement created; PO stays MaterialPending |
| 7 | Partial raw material availability | Allocate available; create Supply Requirement for shortage |
| 8 | PO partially received | Update Supply Requirement partially; PMR remains PartiallyReserved |
| 9 | Purchase Order cancelled | Release planned allocations; recalculate PMR shortages; re-trigger Supply Requirement |
| 10 | Goods Receipt reversed | Decrease inventory; release/reduce affected allocations; recalculate readiness |
| 11 | Inventory reserved by another demand | Allocation Engine respects priority rules; lower-priority demand gets shortage |
| 12 | Competing Sales Orders for same FG | Allocation Engine evaluates by priority/required_date/order_date |
| 13 | Sales Order cancelled after production starts | Flag for review; reallocate produced goods to other demands if any |
| 14 | Production Order cancelled after material issue | Return issued materials to inventory; release reservations |
| 15 | Production produces less than planned | Partial FGR; remaining demand gets new Fulfillment Requirement |
| 16 | Production produces more than planned | Excess goes to FG inventory; Allocation Engine distributes |
| 17 | Quality rejects entire quantity | No FGR; demand remains unfulfilled; new Production Order may be needed |
| 18 | Rework decision | Material returned; new production cycle; additional material may be needed |
| 19 | Allocation failure (concurrent) | Retry with exponential backoff (3 attempts); alert if exhausted |
| 20 | Duplicate allocation request | Idempotency check: if allocation already exists for same demand+supply, return existing |
| 21 | Sales Order quantity increased | Additional Fulfillment Requirement created; triggers supply method evaluation |
| 22 | Sales Order quantity decreased | Release excess allocations; cancel excess Supply Requirements; reduce Production Order if not started |
| 23 | Warehouse changed on SO | Release current warehouse allocations; re-evaluate at new warehouse |
| 24 | Required date changed | Recalculate allocation priority; may shift allocation from other demands |
| 25 | Product becomes inactive | Block new orders; flag open Production Orders for review |
| 26 | BOM material becomes inactive | Block new BOM activation; existing Production Orders unaffected |
| 27 | UOM mismatch between BOM and inventory | Block with validation error; require UOM alignment |
| 28 | Partial delivery from allocated FG | Consume partial allocation; remaining stays allocated |
| 29 | Backorder situation | Fulfillment Requirement stays open with outstanding_quantity \> 0 |

**25. Security & Permissions**

**25.1 New Permission Claims**

| **Permission** | **Description** | **Roles** |
|----|----|----|
| BOM_VIEW | View BOMs and BOM versions | All manufacturing roles |
| BOM_CREATE | Create new BOMs | Product Manager, BOM Creator |
| BOM_EDIT | Edit Draft BOMs | BOM Creator |
| BOM_SUBMIT | Submit BOM for approval | BOM Creator |
| BOM_APPROVE | Approve/reject submitted BOMs | BOM Approver, Production Manager |
| BOM_ACTIVATE | Activate approved BOMs | Production Manager, Admin |
| BOM_OBSOLETE | Obsolete active BOMs | Production Manager, Admin |
| BOM_ADMIN | Full BOM administration including migration | Administrator |
| PROD_VIEW | View production orders | All manufacturing roles |
| PROD_CREATE | Create production orders | Production Planner |
| PROD_PLAN | Plan/release production orders | Production Planner, Production Manager |
| PROD_START | Start production execution | Production Operator, Production Manager |
| PROD_REPORT | Report production output | Production Operator |
| PROD_CANCEL | Cancel production orders | Production Manager |
| PROD_CLOSE | Close completed production orders | Production Manager, Admin |
| PROD_MANAGER | Full production management | Production Manager |
| MI_CREATE | Create material issues | Warehouse User, Production Operator |
| MI_CONFIRM | Confirm material issues | Warehouse User, Inventory Manager |
| MI_REVERSE | Reverse material issues | Inventory Manager, Admin |
| QI_CREATE | Create quality inspections | Quality Inspector |
| QI_APPROVE | Approve quality decisions | Quality Inspector |
| FGR_CREATE | Create finished goods receipts | Warehouse User |
| FGR_CONFIRM | Confirm FGR | Warehouse User, Inventory Manager |
| ALLOCATION_VIEW | View allocation records | All roles |
| ALLOCATION_RUN | Manually trigger allocation engine | Inventory Manager, Production Planner |
| ALLOCATION_ADMIN | Reallocate firm/reserved allocations | Inventory Manager, Admin |
| SUPPLY_VIEW | View supply requirements | Procurement, Production |
| SUPPLY_CREATE | Create manual supply requirements | Production Planner, Procurement User |
| SUPPLY_CANCEL | Cancel supply requirements | Production Manager, Procurement Manager |
| PROD_LEDGER_VIEW | View production ledger entries and reports | Production Manager, Inventory Manager, Finance |
| REPORTS_VIEW | View manufacturing reports (ledger summary, cost analysis, chained mfg) | Production Manager, Finance, Admin |

> ⚠ *Map these permissions to the existing ASP.NET Core Identity claims-based authorization pattern. Do NOT create new role entities --- add claims to existing roles.*

**26. Audit Requirements**

All significant state changes generate audit records using the existing AuditTrail entity and EF Core interceptor pattern.

**26.1 Audited Events**

| **Entity** | **Audited Events** |
|----|----|
| Product | product_type changed, supply_method changed, is_manufacturable changed |
| BOM | Created, Submitted, Approved, Rejected, Activated, Obsoleted, Line added/modified/removed |
| Production Order | Created, Planned, MaterialPending, Ready, Started, Completed, Closed, Cancelled, Quantity changed |
| PMR | Created, Reservation updated, Shortage changed, Issued, Consumed, Cancelled |
| Supply Requirement | Created, Ordered, Partially Received, Fulfilled, Cancelled |
| Allocation | Created, Consumed, Released, Cancelled, Reallocated (old_demand → new_demand) |
| Material Issue | Created, Confirmed, Reversed |
| Quality Inspection | Created, Decision recorded (pass/fail/hold/rework) |
| FGR | Created, Confirmed, Reversed |
| Fulfillment Requirement | Created, Status changed, Quantity changed |
| Production Ledger | Entry created (auto from MI/FGR/QI), reversal entry created |

**26.2 Audit Record Fields**

- User who performed the action (from JWT claims)

- Timestamp (DATETIMEOFFSET)

- Entity type and ID

- Action type (Created, Updated, StatusChanged, Deleted)

- Old value (JSON)

- New value (JSON)

- Reason (required for cancellations, reallocations, reversals)

- org_id (tenant context)

**27. Database Changes Summary**

**27.1 New Tables**

| **Table** | **Schema** | **Purpose** | **Key Relationships** |
|----|----|----|----|
| BillOfMaterials | material | BOM header | → Products, → Users |
| BillOfMaterialLines | material | BOM components | → BillOfMaterials, → Products |
| ProductionOrders | material | Production jobs | → Products, → BillOfMaterials, → Warehouses |
| ProductionMaterialRequirements | material | Material needs per PO | → ProductionOrders, → BOMLines, → Products |
| SupplyRequirements | material | Shortage records | → Products, → various demand sources |
| QualityInspections | material | QI header | → ProductionOrders |
| QualityInspectionLines | material | QI detail checks | → QualityInspections |
| FinishedGoodsReceipts | material | FGR header | → ProductionOrders, → QualityInspections |
| FinishedGoodsReceiptLines | material | FGR line items | → FinishedGoodsReceipts, → Products |
| MaterialIssues | material | Material issue header | → ProductionOrders |
| MaterialIssueLines | material | Material issue items | → MaterialIssues, → PMRs |
| AllocationRecords | inventory | Demand↔supply assignments | → Products, → various demands/supplies |
| AllocationRules | inventory | Configurable priority rules | Standalone |
| FulfillmentRequirements | demand | SO fulfillment tracking | → SaleOrders, → Products |
| ProductionLedgerEntries | material | Production debit/credit inventory log | → ProductionOrders, → Products, → stock_transactions |

**27.2 Modified Tables**

| **Table** | **Schema** | **Modification** |
|----|----|----|
| Products | lookups | Add product_type, supply_method, is_manufacturable, is_bom_input, lead_time_days columns |
| SaleOrderLines | demand | Add fulfillment_method, allocated_quantity, production_required_quantity columns |
| stock_balances | inventory | No structural change --- reserved_qty already exists from G4 decision |
| stock_transactions | inventory | Add MATERIAL_ISSUE, FINISHED_GOODS_RECEIPT, PRODUCTION_SCRAP to movement_type enum |
| delivery_orders | logistics | Add PRODUCTION to from_source_type enum |
| ReservationSourceType | inventory | Add PRODUCTION_ORDER value to existing enum |

**27.3 Key Indexes**

| **Table** | **Index** | **Columns** | **Purpose** |
|----|----|----|----|
| BillOfMaterials | IX_BOM_Product_Active | org_id, product_id, variant_id, status | Fast Active BOM lookup |
| BillOfMaterials | UQ_BOM_Version | org_id, product_id, variant_id, version | Version uniqueness |
| ProductionOrders | IX_ProdOrder_Status | org_id, status, required_date | Status-based queries |
| ProductionOrders | IX_ProdOrder_Source | org_id, source_type, source_id | Source traceability |
| PMR | IX_PMR_ProdOrder | production_order_id, status | Material readiness check |
| PMR | IX_PMR_Shortage | org_id, product_id, warehouse_id, shortage_quantity | Shortage queries |
| SupplyRequirements | IX_SR_Product_Status | org_id, product_id, status | Open supply queries |
| AllocationRecords | IX_Alloc_Product_WH | org_id, product_id, warehouse_id, status | Available qty calculation |
| AllocationRecords | IX_Alloc_Demand | demand_type, demand_id, status | Demand allocation lookup |
| FulfillmentRequirements | IX_FR_SaleOrder | sale_order_id, status | SO fulfillment status |
| ProductionLedgerEntries | IX_ProdLedger_PO | production_order_id, entry_type, transaction_date | Ledger by production order |
| ProductionLedgerEntries | IX_ProdLedger_Product | org_id, product_id, entry_type, transaction_date | Material consumption analysis |

**28. API Specifications**

All APIs follow existing SMS conventions: RESTful, JSON, JWT auth with org_id claim, FluentValidation, AutoMapper DTOs.

**28.1 Product APIs (MODIFY)**

| **Method** | **Endpoint** | **Purpose** | **Permission** |
|----|----|----|----|
| PATCH | /api/products/{id}/manufacturing-config | Set product_type, supply_method, manufacturing flags | PRODUCT_EDIT |

**28.2 BOM APIs (NEW)**

| **Method** | **Endpoint** | **Purpose** | **Permission** |
|----|----|----|----|
| POST | /api/boms | Create new BOM | BOM_CREATE |
| GET | /api/boms/{id} | Get BOM with lines | BOM_VIEW |
| PUT | /api/boms/{id} | Update Draft BOM | BOM_EDIT |
| DELETE | /api/boms/{id} | Delete unused Draft BOM | BOM_EDIT |
| POST | /api/boms/{id}/submit | Submit for approval | BOM_SUBMIT |
| POST | /api/boms/{id}/approve | Approve BOM | BOM_APPROVE |
| POST | /api/boms/{id}/reject | Reject BOM (with reason) | BOM_APPROVE |
| POST | /api/boms/{id}/activate | Activate approved BOM | BOM_ACTIVATE |
| POST | /api/boms/{id}/obsolete | Obsolete active BOM | BOM_OBSOLETE |
| GET | /api/products/{id}/boms | List all BOM versions for a product | BOM_VIEW |
| GET | /api/boms/{id1}/compare/{id2} | Compare two BOM versions | BOM_VIEW |
| POST | /api/boms/{id}/new-version | Create new version from existing | BOM_CREATE |

**28.3 Production Order APIs (NEW)**

| **Method** | **Endpoint** | **Purpose** | **Permission** |
|----|----|----|----|
| POST | /api/production-orders | Create production order | PROD_CREATE |
| GET | /api/production-orders/{id} | Get production order details | PROD_VIEW |
| GET | /api/production-orders | List production orders (filterable) | PROD_VIEW |
| POST | /api/production-orders/{id}/plan | Plan/release --- explode BOM into PMRs | PROD_PLAN |
| POST | /api/production-orders/{id}/start | Start production | PROD_START |
| POST | /api/production-orders/{id}/report-output | Report production quantities | PROD_REPORT |
| POST | /api/production-orders/{id}/complete | Mark production complete → QI | PROD_REPORT |
| POST | /api/production-orders/{id}/cancel | Cancel production order | PROD_CANCEL |
| POST | /api/production-orders/{id}/close | Close completed order | PROD_CLOSE |
| GET | /api/production-orders/{id}/materials | Get PMR list with shortage details | PROD_VIEW |
| GET | /api/production-orders/{id}/readiness | Get material readiness summary | PROD_VIEW |

**28.4 Material Issue APIs (NEW)**

| **Method** | **Endpoint** | **Purpose** | **Permission** |
|----|----|----|----|
| POST | /api/production-orders/{id}/material-issues | Create material issue | MI_CREATE |
| POST | /api/material-issues/{id}/confirm | Confirm material issue | MI_CONFIRM |
| POST | /api/material-issues/{id}/reverse | Reverse material issue | MI_REVERSE |
| POST | /api/production-orders/{id}/material-return | Return unused material | MI_CREATE |

**28.5 Quality Inspection APIs (NEW)**

| **Method** | **Endpoint** | **Purpose** | **Permission** |
|----|----|----|----|
| POST | /api/production-orders/{id}/quality-inspections | Create quality inspection | QI_CREATE |
| GET | /api/quality-inspections/{id} | Get inspection details | QI_CREATE |
| POST | /api/quality-inspections/{id}/decide | Record inspection decision | QI_APPROVE |

**28.6 FGR APIs (NEW)**

| **Method** | **Endpoint** | **Purpose** | **Permission** |
|----|----|----|----|
| POST | /api/production-orders/{id}/fgr | Create finished goods receipt | FGR_CREATE |
| POST | /api/fgr/{id}/confirm | Confirm FGR → inventory update | FGR_CONFIRM |

**28.7 Supply Requirement APIs (NEW)**

| **Method** | **Endpoint** | **Purpose** | **Permission** |
|----|----|----|----|
| GET | /api/supply-requirements | List supply requirements (filterable) | SUPPLY_VIEW |
| GET | /api/supply-requirements/{id} | Get supply requirement details | SUPPLY_VIEW |
| POST | /api/supply-requirements | Create manual supply requirement | SUPPLY_CREATE |
| POST | /api/supply-requirements/{id}/cancel | Cancel supply requirement | SUPPLY_CANCEL |

**28.8 Allocation APIs (NEW)**

| **Method** | **Endpoint** | **Purpose** | **Permission** |
|----|----|----|----|
| POST | /api/allocations/run | Manually trigger allocation for a product/warehouse | ALLOCATION_RUN |
| GET | /api/allocations | List allocation records (filterable) | ALLOCATION_VIEW |
| GET | /api/allocations/{id} | Get allocation details | ALLOCATION_VIEW |
| POST | /api/allocations/{id}/release | Release an allocation | ALLOCATION_ADMIN |
| POST | /api/allocations/{id}/reallocate | Reallocate to different demand | ALLOCATION_ADMIN |
| GET | /api/products/{id}/availability | Get availability summary | ALLOCATION_VIEW |

**28.9 Production Ledger APIs (NEW)**

| **Method** | **Endpoint** | **Purpose** | **Permission** |
|----|----|----|----|
| GET | /api/production-orders/{id}/ledger | Get ledger entries for a specific production order | PROD_VIEW |
| GET | /api/production-ledger | List/filter ledger entries across all production orders | PROD_VIEW |
| GET | /api/production-ledger/summary | Aggregated ledger summary (by product, period, warehouse) | REPORTS_VIEW |
| GET | /api/production-orders/{id}/cost-summary | Material cost vs production output summary | PROD_VIEW |
| GET | /api/production-ledger/chained | Cross-production entries where FG is consumed as BOM input | REPORTS_VIEW |

**29. UI Requirements**

**29.1 Product Screens (MODIFY)**

- Product Create/Edit: Add ProductType dropdown, SupplyMethod dropdown, manufacturing checkboxes

- Product Detail: Show manufacturing configuration section

**29.2 BOM Screens (NEW)**

- BOM List: Filterable by product, status, version. Show active version highlighted

- BOM Create: Product selector, base quantity, effective dates, add lines with material search

- BOM Edit: Editable only in Draft status. Add/remove/modify lines

- BOM Detail: Header info, lines table, status badge, approval history timeline

- BOM Approval: Submission form (Submit button on Draft), Approve/Reject buttons for approvers

- BOM Version History: Timeline of all versions for a product with comparison links

- BOM Comparison: Side-by-side diff of two versions (added/removed/changed lines highlighted)

**29.3 Production Order Screens (NEW)**

- Production Order List: Filterable by status, product, date range, priority. Status badges

- Production Order Create: Product selector (filtered to manufacturable), auto-load Active BOM, quantity, dates

- Production Order Detail: Header, material requirements grid, material status indicator, timeline

- Material Requirements: Table showing each PMR with required/reserved/issued/shortage quantities

- Material Shortages: Dashboard showing all products with shortage_quantity \> 0 across all production orders

- Material Issue Form: Select PMR lines, enter issue quantities, warehouse/bin selector

- Production Execution: Report output quantities, mark complete

- Quality Inspection Form: Accept/reject/hold/rework per check, quantity inputs, notes

- FGR Form: Confirm accepted quantity into inventory, warehouse/bin selector

**29.4 Production Ledger Screens (NEW)**

- Production Order Detail → \'Ledger\' tab: Chronological debit/credit table with color coding (red=debit, green=credit)

- Ledger Summary Card: Total Materials Consumed (count + qty), Total FG Produced, Scrap Qty, Yield %

- Cross-Production Ledger: Aggregate view across all POs, filterable by product, date, warehouse, entry_type

- Material Consumption Analysis: Report showing total raw material consumed per product over a period

- Finished Goods Output Analysis: Report showing total FG produced per product over a period

- Production Cost Summary: Material cost breakdown per Production Order (qty × unit cost, if costing available)

- Chained Manufacturing View: Visual chain showing which FGs feed into other production orders as inputs

- Export: All ledger views support Export to Excel (.xlsx) and PDF

**29.5 Allocation Screens (NEW)**

- Allocation Dashboard: Demand vs Supply overview per product/warehouse

- Allocation Detail: Show which demand owns which supply, with priorities

- Reallocation Form: Transfer allocation from one demand to another (admin only)

- Availability View: Per-product: on_hand, reserved, available, incoming, demand breakdown

**29.5 Production Order Screen --- Material Status Display**

> ┌─────────────────────────────────────────────────────────┐
>
> │ Production Order: PROD-20260924-001 │
>
> │ Product: Printed T-Shirt │
>
> │ Status: MATERIAL PENDING │
>
> │ │
>
> │ Material Status: ⚠ MATERIAL PENDING │
>
> │ │
>
> │ Missing Materials: │
>
> │ • Plain T-Shirt: shortage 2 PCS (reserved 78 of 80) │
>
> │ • Printing Ink: OK (reserved 5.88 L of 5.88 L) │
>
> │ • Packaging Box: OK (reserved 82 of 82) │
>
> │ │
>
> │ Supply Status: │
>
> │ • SR-001: PO-1001 for 2 PCS, expected 26 Sep │
>
> └─────────────────────────────────────────────────────────┘

**30. Notifications**

Notifications use the existing Notifications table + Hangfire background job pattern.

| **Event** | **Notification To** | **Channel** | **Message** |
|----|----|----|----|
| BOM Submitted | BOM Approver(s) | In-app + Email | BOM {number} for {product} submitted for approval |
| BOM Approved | BOM Creator | In-app | BOM {number} approved by {approver} |
| BOM Rejected | BOM Creator | In-app + Email | BOM {number} rejected: {reason} |
| Production Order Ready | Production Manager | In-app | PROD-{number} is ready --- all materials available |
| Material Shortage | Production Planner | In-app + Email | PROD-{number}: shortage of {material} ({qty} {uom}) |
| Supply Requirement Created | Procurement User | In-app | New supply requirement: {product} x {qty} |
| Quality Inspection Complete | Production Manager | In-app | QI for PROD-{number}: {accepted} accepted, {rejected} rejected |
| FGR Confirmed | Inventory Manager | In-app | FGR-{number}: {qty} {product} received into {warehouse} |
| Allocation Changed | Demand Owner | In-app | Allocation updated for {demand}: {new_qty} allocated |
| Production Ledger Complete | Production Manager, Finance | In-app | PROD-{number} ledger finalized: {debit_count} materials consumed, {credit_qty} {product} produced (Yield: {yield}%) |

**31. Reporting**

**31.1 Standard Reports**

| **Report** | **Description** | **Key Data** |
|----|----|----|
| Sales Order Fulfillment Status | Current fulfillment state of all open SOs | SO#, Product, Required, Allocated, Produced, Delivered, Outstanding |
| Production Demand | All open production requirements | Product, Total Demand, In Production, Completed, Remaining |
| Production Pending Materials | Production orders waiting for materials | PROD#, Product, Status, Missing Materials, Expected Supply Date |
| Material Shortages | Cross-production-order material shortages | Material, Total Shortage, Supply Requirements, Expected Dates |
| Supply Requirements | Open supply requirements with status | SR#, Product, Qty, Method, Status, Source PO/TO, ETA |
| Open Purchase Dependencies | POs linked to production supply requirements | PO#, Vendor, Product, Qty, Expected Date, Linked Production |
| Allocation Report | Current allocation state per product | Product, WH, On-Hand, Reserved, Available, Demand Breakdown |
| Unallocated Inventory | Stock available for allocation | Product, WH, Available Qty, Oldest Demand Waiting |
| Reserved Inventory | Stock reserved by demand type | Product, WH, Reserved Qty, By Sales/Production/Other |
| Production Order Status | Status distribution of all production orders | Status, Count, Total Planned Qty, Avg Days in Status |
| BOM Versions | Active BOM versions per product | Product, Active Version, Previous Versions, Last Change Date |
| BOM Approval History | Approval audit trail | BOM#, Version, Action, User, Date, Reason |
| Production Yield | Planned vs produced vs accepted | PROD#, Planned, Produced, Accepted, Rejected, Yield % |
| Finished Goods Produced | FGR summary by product/period | Product, Period, Qty Produced, Qty Accepted, Qty Rejected |
| Sales Backorders | SO lines with outstanding qty | SO#, Customer, Product, Outstanding Qty, Days Overdue |
| Demand vs Supply | Aggregate demand/supply balance | Product, Total Demand, Total Supply, Gap, Supply Coverage % |
| Supply Requirement Aging | SRs by age and status | Age Bucket, Open Count, Total Qty, Avg Days Open |

**31.2 Production Ledger Reports**

These reports are driven by the Production Ledger (Section 19A) and provide complete visibility into material consumption and production output.

| **Report** | **Description** | **Key Data** |
|----|----|----|
| Production Ledger Detail | Complete debit/credit log for a single Production Order | PROD#, Entry#, Type (Debit/Credit), Product, ProductType, Qty, UOM, Warehouse, Source Doc, Date |
| Production Ledger Summary | Aggregated material consumption and output per Production Order | PROD#, FG Product, Planned Qty, Materials Consumed (item count), Qty Produced, Qty Scrapped, Yield % |
| Material Consumption Report | All raw materials consumed across Production Orders in a period | Material, ProductType, Total Consumed, \# of POs, Avg per PO, Warehouse, Period |
| Finished Goods Output Report | All finished goods produced across Production Orders in a period | FG Product, Total Produced, Total Accepted, Total Rejected, Total Scrapped, Yield %, Period |
| Production Scrap Report | All scrap/rejection entries from QI across Production Orders | PROD#, Product, Scrapped Qty, Scrap %, Rejection Reason, QI#, Date |
| Material Return Report | Materials returned to inventory from production floor | PROD#, Material, Returned Qty, Original Issued Qty, Return %, Date |
| Production Cost Analysis | Material cost breakdown per Production Order (debits × unit cost) | PROD#, FG Product, Material, Qty Consumed, Unit Cost, Total Cost, % of Production Cost |
| Chained Manufacturing Report | Cross-production tracking where FG from one PO is consumed as input in another | Parent PROD#, FG Used as Input, Qty, Child PROD# (consumer), Child FG Output, Full Chain |

> ⚠ *The Production Cost Analysis report requires product unit cost data from the existing Product/Variant cost fields. If costing is not yet implemented, this report shows quantities only with a \'cost data pending\' indicator.*

**31.3 Chained Manufacturing Reports**

These reports support the scenario where a FinishedGood from one production process is consumed as raw material in another:

| **Report** | **Description** | **Key Data** |
|----|----|----|
| FinishedGood as Raw Material | Products classified as FinishedGood with is_bom_input = true | Product, SupplyMethod, \# BOMs as Output, \# BOMs as Input, Current Stock, Reserved, Available |
| Manufacturing Chain Trace | End-to-end chain from raw materials through intermediate FGs to final FG | Final FG, Chain Depth, Intermediate Products (per level), Raw Materials (leaf level), Total POs in Chain |
| Cross-Production Dependency | Production Orders that depend on output from other Production Orders | Consumer PROD#, FG Required, Supplier PROD#, FG Supplied, Qty Needed, Qty Available, Gap |

**32. Concurrency Management**

**32.1 Concurrency Strategy**

| **Entity** | **Strategy** | **Implementation** |
|----|----|----|
| BillOfMaterials | Optimistic (RowVersion) | EF Core \[Timestamp\] attribute; 409 on conflict |
| ProductionOrders | Optimistic (RowVersion) | EF Core \[Timestamp\] attribute; 409 on conflict |
| AllocationRecords | Optimistic (RowVersion) + Serializable TX | Serializable isolation for allocation engine operations |
| stock_balances | Existing pattern (RowVersion) | Already implemented in current system |
| stock_reservations | Existing pattern (row-level lock) | IStockReservationService pattern |
| document_number_sequences | Existing pattern (RowVersion) | Already implemented in logistics |

**32.2 Critical Concurrency Scenarios**

- Two allocation requests for same product/warehouse: SERIALIZABLE transaction prevents double-allocation

- Concurrent Production Order planning for same BOM: Each gets independent PMR set; allocation handles contention

- Concurrent Goods Receipt and Allocation: GRN commits first (inventory increase), then allocation event fires

- Concurrent FGR and SO delivery: FGR increases FG inventory, then allocation assigns to highest-priority demand

**33. Transaction Management**

**33.1 Transaction Boundaries**

| **Operation** | **Scope** | **Includes** | **Event Type** |
|----|----|----|----|
| BOM Approval | Single TX | BOM status + audit trail | Synchronous |
| Production Order Planning | Single TX | PO status + all PMR creation | Synchronous + async allocation |
| Material Issue Confirm | Single TX | MI status + stock_transaction + stock_balance update + PMR update | Synchronous |
| Goods Receipt | Single TX (existing) | GRN + inventory update | Sync + async AllocationEngineHandler |
| Quality Inspection | Single TX | QI record + PO quantity updates | Synchronous |
| FGR Confirm | Single TX | FGR status + stock_transaction + stock_balance + PO update | Sync + async AllocationEngineHandler |
| Allocation Engine Run | Serializable TX (per product/wh) | AllocationRecord create/update | Synchronous + async readiness check |
| Production Readiness Check | Single TX | PO status update based on PMR shortage scan | Via Hangfire (non-blocking) |

**33.2 Synchronous vs Asynchronous**

- Synchronous (within same transaction): Status updates, inventory movements, audit trail

- Asynchronous (MediatR domain event → Hangfire): Allocation Engine runs, Production Readiness recalculation, Notification dispatch, DocumentTimeline writes

- Rationale: Allocation can be expensive for many demands. Running it asynchronously prevents blocking the GRN/FGR transaction while still ensuring eventual consistency.

**34. End-to-End Worked Example**

**This section walks through the complete flow with exact state changes and quantity calculations.**

**34.1 Setup**

Product: Printed T-Shirt (FinishedGood, SupplyMethod = Manufacture)

BOM V1 (Active, base_quantity = 1):

| **Material**  | **Qty/Unit** | **Scrap %** | **Product Type** |
|---------------|--------------|-------------|------------------|
| Plain T-Shirt | 1 PCS        | 0%          | RawMaterial      |
| Printing Ink  | 0.05 L       | 5%          | RawMaterial      |
| Packaging Box | 1 PCS        | 2%          | Consumable       |

**34.2 Step 1: Sales Order Created**

> Sales Order SO-1001:
>
> Customer: ABC Corp
>
> Line 1: Printed T-Shirt x 100, Required Date: 30 Sep
>
> Status: Draft → Confirmed

**34.3 Step 2: Fulfillment Requirement Generated**

> FulfillmentRequirement FR-001:
>
> Product: Printed T-Shirt
>
> Required: 100
>
> Source: SO-1001, Line 1
>
> Inventory Check:
>
> on_hand: 20 Printed T-Shirts
>
> reserved: 0
>
> available: 20
>
> Decision:
>
> Allocate 20 from stock → FR-001.allocated_quantity = 20
>
> Remaining: 80 → supply_method = Manufacture
>
> → Create Production Requirement for 80

**34.4 Step 3: Production Order Created**

> ProductionOrder PROD-001:
>
> Product: Printed T-Shirt
>
> Planned: 80
>
> BOM: V1 (snapshotted)
>
> Source: SO-1001 (via FR-001)
>
> Status: Draft → Planned

**34.5 Step 4: BOM Exploded into PMRs**

| **PMR** | **Material** | **Net Qty** | **Scrap** | **Gross Required** | **Available** | **Reserved** | **Shortage** |
|----|----|----|----|----|----|----|----|
| PMR-001 | Plain T-Shirt | 80 PCS | 0 | 80 PCS | 78 PCS | 78 PCS | 2 PCS |
| PMR-002 | Printing Ink | 4 L | 0.2 L | 4.2 L | 4.2 L | 4.2 L | 0 |
| PMR-003 | Packaging Box | 80 PCS | 1.6 | 82 PCS | 82 PCS | 82 PCS | 0 |

> PROD-001 Status: Planned → MaterialPending
>
> Reason: PMR-001 (Plain T-Shirt) shortage = 2

**34.6 Step 5: Supply Requirement Created**

> SupplyRequirement SR-001:
>
> Product: Plain T-Shirt
>
> Quantity Required: 2
>
> Demand Source: PMR-001
>
> Supply Method: Purchase
>
> Status: Open → Ordered
>
> → Purchase Order PO-001 created:
>
> Vendor: Textile Supplier Co
>
> Line 1: Plain T-Shirt x 2
>
> Expected: 26 Sep

**34.7 Step 6: Goods Receipt**

> GRN-001 (PO-001 received):
>
> Plain T-Shirt x 2
>
> Inventory update:
>
> Plain T-Shirt on_hand: 0 → 2 (78 were already reserved)
>
> Allocation Engine triggered (async):
>
> Evaluates outstanding demands for Plain T-Shirt:
>
> PMR-001: shortage = 2
>
> (No other competing demands)
>
> → Allocates 2 to PMR-001
>
> PMR-001 updated:
>
> reserved: 78 → 80
>
> shortage: 2 → 0
>
> Status: PartiallyReserved → FullyReserved
>
> SR-001 Status: Ordered → Fulfilled
>
> Production Readiness Check:
>
> PMR-001: shortage = 0 ✓
>
> PMR-002: shortage = 0 ✓
>
> PMR-003: shortage = 0 ✓
>
> → PROD-001 Status: MaterialPending → Ready ✓

**34.8 Step 7: Material Issue**

> MaterialIssue MI-001 (PROD-001):
>
> Line 1: Plain T-Shirt x 80 → issued from WH-01
>
> Line 2: Printing Ink x 4.2 L → issued from WH-01
>
> Line 3: Packaging Box x 82 → issued from WH-01
>
> Inventory impact:
>
> Plain T-Shirt on_hand: -= 80 (movement_type: MATERIAL_ISSUE)
>
> Printing Ink on_hand: -= 4.2 (movement_type: MATERIAL_ISSUE)
>
> Packaging Box on_hand: -= 82 (movement_type: MATERIAL_ISSUE)
>
> PMR statuses: all → Issued
>
> PROD-001 Status: Ready → InProgress

**34.9 Step 8: Production Execution**

> PROD-001 production complete:
>
> produced_quantity = 80
>
> Status: InProgress → QualityInspection

**34.10 Step 9: Quality Inspection**

> QualityInspection QI-001 (PROD-001):
>
> Inspected: 80
>
> Accepted: 76
>
> Rejected: 4 (defect: print misalignment)
>
> Result: PartiallyPassed
>
> PROD-001 updated:
>
> accepted_quantity = 76
>
> rejected_quantity = 4
>
> Rejected 4 units → PRODUCTION_SCRAP stock movement

**34.11 Step 10: Finished Goods Receipt**

> FGR-001 (PROD-001):
>
> Product: Printed T-Shirt
>
> Quantity: 76
>
> Warehouse: WH-01
>
> Inventory update:
>
> Printed T-Shirt on_hand: += 76 (movement_type: FINISHED_GOODS_RECEIPT)
>
> PROD-001 Status: QualityInspection → Completed
>
> Allocation Engine triggered (async):
>
> Evaluates outstanding demands for Printed T-Shirt:
>
> FR-001: outstanding = 80 (already has 20 allocated)
>
> → Allocates 76 to FR-001
>
> FR-001 updated:
>
> allocated_quantity: 20 → 96
>
> outstanding_quantity: 80 → 4
>
> Status: InProduction → PartiallyFulfilled

**34.12 Step 11: Delivery**

> Delivery Order DO-001 (from_source_type: SALE_ORDER):
>
> SO: SO-1001
>
> Product: Printed T-Shirt x 96
>
> Status: DRAFT → RELEASED → PICKING → PACKED → GOODS_ISSUED → DELIVERED
>
> Goods Issue: movement_type = SALES_DISPATCH
>
> Printed T-Shirt on_hand: -= 96
>
> SO-1001 updated:
>
> Line 1: fulfilled_quantity = 96 of 100
>
> Status: Partially Fulfilled

**34.13 Step 12: Remaining Demand**

> FR-001 outstanding: 4 units
>
> Options:
>
> a\) New Production Order for 4 units
>
> b\) Wait for next production run
>
> c\) Customer agrees to partial delivery → cancel remaining
>
> d\) Purchase if product allows dual supply
>
> // Decision depends on business rules / customer agreement

**34.14 Complete Traceability Chain**

> SO-1001 (Sales Order, 100 PCS)
>
> → FR-001 (Fulfillment Requirement)
>
> → Allocation: 20 from stock (on_hand)
>
> → PROD-001 (Production Order, 80 PCS, BOM V1)
>
> → PMR-001 (Plain T-Shirt x 80)
>
> → Allocation: 78 from stock
>
> → SR-001 (Supply Requirement, 2 PCS)
>
> → PO-001 (Purchase Order, 2 PCS)
>
> → GRN-001 (Goods Receipt, 2 PCS)
>
> → Allocation: 2 → PMR-001
>
> → PMR-002 (Printing Ink x 4.2 L) → Allocated from stock
>
> → PMR-003 (Packaging Box x 82) → Allocated from stock
>
> → MI-001 (Material Issue --- all materials)
>
> → QI-001 (Quality: 76 accepted, 4 rejected)
>
> → FGR-001 (76 PCS → inventory)
>
> → Allocation: 76 → FR-001
>
> → DO-001 (Delivery Order, 96 PCS)
>
> → Invoice

**35. Acceptance Criteria**

**35.1 Product & BOM**

| **AC ID** | **Given** | **When** | **Then** |
|----|----|----|----|
| AC-PB01 | Product with supply_method=Manufacture | User creates BOM | BOM created successfully with product linked |
| AC-PB02 | BOM in Draft status | User submits for approval | BOM transitions to Submitted; approver notified |
| AC-PB03 | BOM in Submitted status | Approver approves | BOM transitions to Approved; creator notified |
| AC-PB04 | BOM in Approved status | Admin activates | BOM transitions to Active; any previous Active version → Obsolete |
| AC-PB05 | Active BOM used by Production Order | User tries to edit BOM | System blocks edit; requires new version |
| AC-PB06 | BOM V1 Active, V2 created | V2 activated | V1 → Obsolete; new POs use V2; existing POs keep V1 |

**35.2 Production & Materials**

| **AC ID** | **Given** | **When** | **Then** |
|----|----|----|----|
| AC-PM01 | Sales Order for 100 FG, 20 in stock | SO confirmed | 20 allocated from stock; 80 triggers Production Requirement |
| AC-PM02 | Production Order created | PO planned (BOM exploded) | PMRs created with correct quantities including scrap allowance |
| AC-PM03 | PMR with shortage | Supply Requirement created | SR links to PMR; supply method from product config |
| AC-PM04 | All PMR shortages = 0 | Readiness check runs | Production Order → Ready |
| AC-PM05 | Production Order Ready | Materials issued | Inventory decreases; PMR.issued_qty updated; PO → InProgress |
| AC-PM06 | Production complete | QI performed: 76 accepted, 4 rejected | PO.accepted=76, rejected=4; only 76 eligible for FGR |
| AC-PM07 | FGR confirmed with 76 units | Allocation Engine runs | 76 allocated to outstanding SO fulfillment requirement |

**35.3 Allocation Engine**

| **AC ID** | **Given** | **When** | **Then** |
|----|----|----|----|
| AC-AE01 | Two demands for same product, different priorities | Allocation runs | Higher priority demand gets stock first |
| AC-AE02 | PO-001 created for Production A\'s shortage | PO received, Sales B has higher priority | Allocation assigns to Sales B, not Production A |
| AC-AE03 | Firm allocation exists for Demand A | Engine tries to reallocate | Firm allocation NOT reassigned without ALLOCATION_ADMIN |
| AC-AE04 | Two concurrent allocation requests | Both execute | Only one succeeds; other retries with RowVersion conflict |
| AC-AE05 | Goods Receipt increases inventory | Allocation event fires | All outstanding demands re-evaluated by priority |

**36. QA Test Scenarios**

**36.1 Product & BOM Tests**

| **Test ID** | **Scenario** | **Expected Result** |
|----|----|----|
| T-PB01 | Create manufactured product with all classification flags | Product created with ProductType, SupplyMethod, manufacturing flags |
| T-PB02 | Create BOM for non-manufacturable product | Validation error: product not configured for manufacturing |
| T-PB03 | Create BOM with circular reference (output = input) | Validation error: circular BOM reference detected |
| T-PB04 | Submit BOM → Approve → Activate lifecycle | BOM transitions through all statuses correctly |
| T-PB05 | Reject BOM with reason | BOM returns to Draft; rejection reason stored and displayed |
| T-PB06 | Create BOM V2 from V1 | V2 created with V1 lines copied; V2 in Draft |
| T-PB07 | Activate V2 while V1 is Active | V1 → Obsolete; V2 → Active |
| T-PB08 | Edit BOM after it\'s used by Production Order | System blocks edit; prompts to create new version |
| T-PB09 | Compare BOM V1 and V2 | Diff shows added/removed/changed lines correctly |

**36.2 Production Tests**

| **Test ID** | **Scenario** | **Expected Result** |
|----|----|----|
| T-PR01 | Create Production Order --- all materials available | PO → Planned → Ready (all PMRs FullyReserved) |
| T-PR02 | Create Production Order --- partial material availability | PO → MaterialPending; SRs created for shortages |
| T-PR03 | Create Production Order --- zero material availability | PO → MaterialPending; SRs for all materials |
| T-PR04 | Multiple material shortages for one PO | Multiple SRs created; PO stays MaterialPending |
| T-PR05 | Partial material supply via GRN | PMR.reserved updated; PO stays MaterialPending if any shortage remains |
| T-PR06 | Full material supply completes | All PMR shortages = 0; PO → Ready |
| T-PR07 | Material issue --- full issue | Inventory decreases; all PMR → Issued; PO → InProgress |
| T-PR08 | Material issue --- partial issue | Partial inventory decrease; some PMR → Issued |
| T-PR09 | Full production → QI → FGR → Completed | Complete lifecycle executes correctly |
| T-PR10 | Production cancellation after material issue | Materials returned to inventory; reservations released |

**36.3 Supply Tests**

| **Test ID** | **Scenario** | **Expected Result** |
|----|----|----|
| T-SR01 | Supply Requirement created from PMR shortage | SR linked to PMR; supply method from product config |
| T-SR02 | SR generates Purchase Order | PO created; SR.supply_source_id linked to PO |
| T-SR03 | Partial GRN against SR-linked PO | SR.quantity_received updated; status PartiallyReceived |
| T-SR04 | Full GRN completes SR | SR → Fulfilled; PMR shortage recalculated |
| T-SR05 | PO cancelled while SR is Ordered | SR → Cancelled; PMR shortage re-evaluated; new SR may be needed |
| T-SR06 | GRN reversed after SR fulfilled | Inventory decreases; allocation released; PMR shortage increases |

**36.4 Allocation Tests**

| **Test ID** | **Scenario** | **Expected Result** |
|----|----|----|
| T-AL01 | Single demand, sufficient stock | Full allocation created; demand fully allocated |
| T-AL02 | Multiple demands, insufficient stock | Highest priority demand allocated first; others get remainder/shortage |
| T-AL03 | Priority-based allocation (Required Date) | Earlier required_date gets stock first |
| T-AL04 | Reallocation of firm allocation | Only succeeds with ALLOCATION_ADMIN permission |
| T-AL05 | Partial allocation --- some stock available | Available qty allocated; remaining tracked as shortage |
| T-AL06 | Reservation release on demand cancellation | Allocation → Released; stock becomes available for other demands |
| T-AL07 | Concurrent allocation attempts | One succeeds; other gets DbUpdateConcurrencyException and retries |
| T-AL08 | FGR triggers allocation for competing Sales Orders | Higher priority SO gets FG stock first |

**36.5 Chained Manufacturing Tests**

| **Test ID** | **Scenario** | **Expected Result** |
|----|----|----|
| T-CM01 | Create FinishedGood product with is_bom_input = true | Product created; can be used as BOM output AND as BOM line in another product\'s BOM |
| T-CM02 | Add FinishedGood (is_bom_input=true) as BOM line in another product\'s BOM | BOM line accepted; parent BOM valid |
| T-CM03 | PMR shortage for a FinishedGood BOM input (supply_method=Manufacture) | Supply Requirement created with supply_method=Manufacture; triggers child Production Order |
| T-CM04 | Child Production Order completes FGR → parent PO shortage resolves | FGR → Allocation Engine → PMR shortage on parent PO reduced; parent PO → Ready when all shortages cleared |
| T-CM05 | Three-level chain: Raw → SemiFinished → FinishedGood → Final Assembly | All three production orders created in correct dependency order; traceability chain intact |
| T-CM06 | Cancel parent PO while child PO is in production | Warning shown; user confirms; child PO status review triggered |

**36.6 Production Ledger Tests**

| **Test ID** | **Scenario** | **Expected Result** |
|----|----|----|
| T-PL01 | Material Issue confirmed for production order | Debit ledger entries created for each issued material with correct qty, UOM, warehouse |
| T-PL02 | FGR confirmed for production order | Credit ledger entry created for finished goods with correct qty, UOM, warehouse |
| T-PL03 | Quality Inspection rejects units | Debit (scrap) ledger entry created for rejected qty with movement_type=PRODUCTION_SCRAP |
| T-PL04 | Material return from production floor | Credit (return) ledger entry created to offset original debit |
| T-PL05 | Production Ledger summary matches PMR issued + FGR accepted | Sum of debits = total material issued; sum of credits = accepted qty |
| T-PL06 | Chained manufacturing ledger shows FG as debit in child PO | Steel Bolt appears as Credit in PROD-A ledger and as Debit in PROD-B ledger |
| T-PL07 | Production Ledger API returns entries in chronological order | GET /api/production-orders/{id}/ledger returns ordered entries with all fields |
| T-PL08 | Production Ledger export to Excel/PDF | Export generates correct document with all debit/credit entries and summary |

**36.7 Quality & FGR Tests**

| **Test ID** | **Scenario** | **Expected Result** |
|----|----|----|
| T-QF01 | Full acceptance (80/80 accepted) | All qty eligible for FGR |
| T-QF02 | Partial rejection (76/80 accepted, 4 rejected) | Only 76 eligible for FGR; 4 scrapped |
| T-QF03 | Full rejection | No FGR; demand remains unfulfilled |
| T-QF04 | Hold decision | Qty quarantined; not available until resolved |
| T-QF05 | Rework decision | Material returned; new production cycle triggered |
| T-QF06 | FGR confirmed → inventory updated | stock_balance.on_hand increases; stock_transaction created |
| T-QF07 | FGR allocation to Sales Order | Allocation Engine assigns FG to highest-priority demand |
| T-QF08 | Multiple Sales Orders competing for FGR | Priority rules determine allocation; lower priority gets shortage |

**37. Migration Strategy**

**37.1 Migration Order**

| **\#** | **Migration Name** | **Schema** | **Description** | **Depends On** |
|----|----|----|----|----|
| 1 | AddProductTypeAndSupplyMethod | lookups | Add product_type, supply_method, manufacturing flags to Products | None |
| 2 | UpdateExistingProductDefaults | lookups | Set all existing products to StockItem/Purchase defaults | \#1 |
| 3 | CreateBillOfMaterials | material | BOM header table | \#1 |
| 4 | CreateBillOfMaterialLines | material | BOM lines table | \#3 |
| 5 | CreateProductionOrders | material | Production order table | \#1, \#3 |
| 6 | CreateProductionMaterialRequirements | material | PMR table | \#5 |
| 7 | CreateSupplyRequirements | material | Supply requirement table | \#6 |
| 8 | CreateAllocationRecords | inventory | Allocation records table | None |
| 9 | CreateAllocationRules | inventory | Allocation rules with defaults | \#8 |
| 10 | AddProductionOrderToReservationSourceType | inventory | Add PRODUCTION_ORDER to ReservationSourceType enum | None |
| 11 | AddManufacturingMovementTypes | inventory | Add MATERIAL_ISSUE, FGR, PRODUCTION_SCRAP movement types | None |
| 12 | CreateQualityInspections | material | QI header + lines tables | \#5 |
| 13 | CreateFinishedGoodsReceipts | material | FGR header + lines tables | \#5, \#12 |
| 14 | CreateMaterialIssues | material | Material issue header + lines tables | \#5, \#6 |
| 14A | CreateProductionLedgerEntries | material | Production ledger debit/credit entries table | \#5, \#14, \#13 |
| 15 | CreateFulfillmentRequirements | demand | Fulfillment requirement table | None |
| 16 | ExtendSaleOrderLinesForFulfillment | demand | Add fulfillment columns to SaleOrderLines | \#15 |
| 17 | AddProductionToDeliverySourceType | logistics | Add PRODUCTION to delivery from_source_type enum | None |
| 18 | SeedAllocationRuleDefaults | inventory | Insert default allocation priority rules | \#9 |
| 19 | SeedManufacturingPermissions | auth | Add all new permission claims | None |
| 20 | AddDocumentNumberSequences | logistics | Add PROD, BOM, SR, MI, QI, FGR sequences | None |

**37.2 Rollback Strategy**

- Each migration is independently reversible via dotnet ef migrations remove

- Data migrations (#2, \#18, \#19) use idempotent INSERT WHERE NOT EXISTS

- No existing data is modified destructively --- only new columns with defaults added

- AllocationRecords is independent of existing stock_reservations --- both can coexist

**37.3 Zero-Downtime Deployment**

- Phase 1: Run schema migrations (new tables + columns with defaults) --- no app change needed

- Phase 2: Deploy new API endpoints and services --- existing functionality unaffected

- Phase 3: Deploy UI changes --- feature-flagged behind Manufacturing Mode toggle

- Phase 4: Enable Manufacturing Mode for tenants that need it

**38. Traceability**

The system provides complete end-to-end traceability using trace_id (correlation GUID) and the existing DocumentTimelines pattern:

> trace_id propagation:
>
> Sales Order (SO-1001)
>
> trace_id: {guid}
>
> → Fulfillment Requirement (FR-001)
>
> trace_id: same {guid}
>
> → Production Order (PROD-001)
>
> trace_id: same {guid}
>
> → Supply Requirement (SR-001)
>
> trace_id: same {guid}
>
> → Purchase Order (PO-001)
>
> trace_id: same {guid}
>
> → Goods Receipt (GRN-001)
>
> trace_id: same {guid}
>
> → Material Issue (MI-001)
>
> trace_id: same {guid}
>
> → Quality Inspection (QI-001)
>
> trace_id: same {guid}
>
> → FGR (FGR-001)
>
> trace_id: same {guid}
>
> → Delivery Order (DO-001)
>
> trace_id: same {guid}
>
> → Invoice
>
> trace_id: same {guid}

DocumentTimelines writes are NON-BLOCKING via Hangfire BackgroundJob.Enqueue (existing pattern).

**39. Implementation Dependencies**

| **This Module** | **Depends On** | **From Addendum** | **Dependency Type** |
|----|----|----|----|
| Product Manufacturing Config | Product Catalog (variant model) | Addendum 26 | MODIFY existing |
| BOM Management | Product Catalog | Addendum 26 | FK reference |
| Production Orders | BOM Management | This FSD | Requires Active BOM |
| Production Material Req | Production Orders + Inventory | This FSD + Existing | FK + stock check |
| Supply Requirements | PMR + Procurement | This FSD + Existing | Creates POs |
| Allocation Engine | Inventory + Stock Reservations | Existing (G4) | Extends existing patterns |
| Sales → Manufacturing | Sales Orders (Addendum 29) | Addendum 29 v1.3 | Fulfillment trigger |
| Delivery from Production | Delivery Orders (TASKS.md) | Existing logistics | from_source_type extension |
| Material Issue | Production + Inventory | This FSD + Existing | Stock movement |
| Quality Inspection | Production | This FSD | Post-production step |
| FGR | Quality + Inventory | This FSD + Existing | Inventory increase |
| Notifications | Hangfire + auth.Notifications | Existing | REUSE pattern |
| Audit Trail | auth.AuditTrail | Existing | REUSE pattern |
| Document Numbering | logistics.document_number_sequences | Existing | REUSE pattern |
| Multi-tenancy | org_id + HasQueryFilter | Addendum 25 | NON-NEGOTIABLE |

**40. Recommended Development Phases**

**Phase 1: Foundation (Weeks 1-2)**

| **Task** | **Description** | **Est. Days** |
|----|----|----|
| Product Catalog Extension | Add ProductType, SupplyMethod enums and columns to Products | 2 |
| Migration + Defaults | Migration script, seed existing products with defaults | 1 |
| Document Number Sequences | Add BOM, PROD, SR, MI, QI, FGR sequence types | 0.5 |
| Permission Claims | Add all new permission claims to auth system | 0.5 |
| Allocation Engine Foundation | AllocationRecords, AllocationRules tables + IAllocationEngine interface | 3 |
| Allocation Engine Tests | Unit + integration tests for allocation logic | 2 |

**Phase 1 Total: \~9 days**

**Phase 2: BOM Management (Weeks 3-4)**

| **Task** | **Description** | **Est. Days** |
|----|----|----|
| BOM Entities + Migrations | BillOfMaterials, BillOfMaterialLines tables | 1 |
| BOM CRUD APIs | Create, Read, Update, Delete endpoints | 2 |
| BOM Approval Workflow | Submit, Approve, Reject, Activate, Obsolete APIs | 2 |
| BOM Versioning | Version management, new-version-from-existing, comparison | 2 |
| BOM Validation | Circular reference detection, constraint enforcement | 1 |
| BOM UI | BOM list, create, edit, detail, approval, comparison screens | 3 |
| BOM Tests | Unit + integration tests for all BOM operations | 2 |

**Phase 2 Total: \~13 days**

**Phase 3: Production Core (Weeks 5-7)**

| **Task** | **Description** | **Est. Days** |
|----|----|----|
| Production Order Entities | ProductionOrders, PMR tables + migrations | 1 |
| Production Order CRUD | Create, list, detail APIs | 2 |
| BOM Explosion | Plan/release: explode BOM into PMRs with scrap calculation | 2 |
| Material Readiness | Readiness calculation, status transitions | 2 |
| Supply Requirement Engine | SR entity, auto-creation from PMR shortage, PO linkage | 3 |
| Material Issue | MI entity, CRUD, inventory impact, PMR updates | 2 |
| Production Execution | Start, report output, complete APIs | 1 |
| Production UI | PO list, detail, material requirements, shortage dashboard | 3 |
| Production Tests | Full test suite for production lifecycle | 3 |

**Phase 3 Total: \~19 days**

**Phase 4: Quality, FGR & Integration (Weeks 8-9)**

| **Task** | **Description** | **Est. Days** |
|----|----|----|
| Quality Inspection | QI entities, APIs, decision workflow | 2 |
| Finished Goods Receipt | FGR entities, APIs, inventory update | 2 |
| Post-FGR Allocation | FGR → Allocation Engine → Sales Order fulfillment | 2 |
| GRN Integration | GRN → Allocation Engine → PMR readiness recalculation | 2 |
| Fulfillment Requirements | FR entity, SO → FR → Manufacturing trigger | 2 |
| Delivery Integration | PRODUCTION source type for delivery orders | 1 |
| QI/FGR UI | Quality inspection form, FGR form, production completion flow | 2 |
| Production Ledger | Ledger entity, auto-entry creation from MI/FGR/QI, APIs, UI tab | 2 |
| Integration Tests | End-to-end workflow tests (SO → Delivery) + ledger verification | 3 |

**Phase 4 Total: \~18 days**

**Phase 5: Polish & Hardening (Week 10)**

| **Task** | **Description** | **Est. Days** |
|----|----|----|
| Notifications | All event-driven notifications via Hangfire | 1 |
| Reporting | All 28 reports (17 standard + 8 ledger + 3 chained mfg) | 3 |
| DocumentTimeline Integration | trace_id propagation across all entities | 1 |
| Concurrency Testing | Stress test allocation engine with concurrent requests | 1 |
| UAT Preparation | Test data setup, user acceptance test scripts | 1 |

**Phase 5 Total: \~8 days**

**Grand Total: \~65 dev days**

Recommended team: 2-3 developers working in parallel on different phases

**41. Open Questions / Decisions Required**

| **\#** | **Question** | **Options** | **Impact** | **Decision By** |
|----|----|----|----|----|
| OQ-1 | Should the Allocation Engine run synchronously or asynchronously after GRN/FGR? | A: Sync (immediate consistency) / B: Async via Hangfire (better performance) | Sync = simpler but slower GRN; Async = eventual consistency | Solution Architect |
| OQ-2 | Should Production Orders allow over-production without limit? | A: Unlimited / B: Configurable % limit (e.g., max 110%) | Unlimited = flexible but risky; Limited = controlled excess | Product Owner |
| OQ-3 | Should BOM support multi-level (sub-assemblies with their own BOMs)? | A: Single-level only / B: Multi-level with recursive explosion | Multi-level adds complexity; may be needed for semi-finished goods | Solution Architect + PO |
| OQ-4 | Should the system auto-create Production Orders from Sales Order confirmation, or require manual trigger? | A: Automatic / B: Manual / C: Configurable per product | Auto = faster but less control; Manual = more oversight | Product Owner |
| OQ-5 | Should Supply Requirements auto-create Purchase Orders, or just Purchase Requisitions? | A: Auto PO / B: Auto PR → Manual PO approval | Auto PO = faster procurement; PR = more oversight | Product Owner |
| OQ-6 | Should the rework workflow create a new Production Order or extend the existing one? | A: New PO / B: Extend existing with rework cycle | New PO = cleaner audit; Extend = simpler tracking | Solution Architect |
| OQ-7 | Are allocation rules configurable per tenant (org), or global? | A: Per-tenant / B: Global defaults with tenant override | Per-tenant = more flexible; Global = simpler setup | Product Owner |
| OQ-8 | Should partial delivery from allocated FG be allowed without explicit approval? | A: Yes, always / B: Requires approval above threshold | Always = flexible; Threshold = controlled partial shipments | Product Owner |
| OQ-9 | Should manufacturing mode be tenant-configurable (some tenants trading-only, others manufacturing)? | A: Tenant-level toggle / B: Always available | Toggle = cleaner UI for non-mfg tenants | Product Owner |
| OQ-10 | Should BOM approval use the existing workflow engine, or a simpler inline approval? | A: Existing workflow engine / B: Inline status transitions | Workflow engine = consistent but heavier; Inline = simpler | Solution Architect |

**Appendix A --- Claude Code Implementation Instructions**

**This document is structured as an implementation specification for Claude Code. Before writing any code, the agent MUST:**

- Inspect the existing codebase to understand current entity structure, naming conventions, and patterns

- Follow existing repository/service/controller patterns exactly --- do NOT invent new architectural patterns

- Use the existing EF Core DbContext and migration pipeline (dotnet ef migrations add)

- Respect the existing multi-tenancy pattern: org_id discriminator with HasQueryFilter on EVERY table

- Follow existing schema isolation: auth, lookups, suppliers, inventory, demand, procurement, warehouse, logistics, finance, reports, workflow, hangfire, material, tenant

- Use AutoMapper for DTOs, FluentValidation for input validation, MediatR for domain events

- Use QuestPDF for any new PDF generation

- All background jobs via Hangfire (hangfire schema) --- pass org_id as explicit parameter

- REUSE existing IStockReservationService (SMS.Shared) --- add PRODUCTION_ORDER to ReservationSourceType

- REUSE existing logistics.delivery_orders with from_source_type = PRODUCTION for FG delivery

- REUSE existing logistics.document_number_sequences with RowVersion for all new number generation

- REUSE existing trace_id / DocumentTimelines pattern for traceability --- NON-BLOCKING via Hangfire

- REUSE existing AuditTrail entity and EF Core interceptor for all audit requirements

- REUSE existing Notifications table + Hangfire for all notification dispatch

- DO NOT create duplicate inventory/reservation structures --- use AllocationRecords alongside existing stock_reservations

- DO NOT put hardcoded SalesOrderId or ProductionOrderId on InventoryBalance --- use AllocationRecords

- DO NOT create a BOM-specific or Production-specific allocation engine --- use the shared IAllocationEngine

- Production Ledger entries are created via MediatR event handlers (MaterialIssuedEvent → debit entries, FGRConfirmedEvent → credit entry, QIRejectedEvent → scrap entry) --- NOT manually in the API controller

- A FinishedGood with is_bom_input = true is valid --- the Supply Requirement Engine must check product.supply_method to decide whether to create a PO (Purchase) or child Production Order (Manufacture)

> **🔴 CRITICAL:** MULTI-TENANCY: Every new table MUST include org_id column with EF Core HasQueryFilter. All queries filter by org_id from JWT claim. Hangfire jobs pass org_id as explicit parameter (no HttpContext in background). This is NON-NEGOTIABLE across all schemas.
