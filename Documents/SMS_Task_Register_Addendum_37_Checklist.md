# SMS — Task Register & Checklist

## FSD Addendum 37 — Module Registry, Enablement Layer & Foundational Shared Entities

| Property         | Value                 |
|------------------|-----------------------|
| Document ID      | SMS-FSD-ADD-037       |
| Addendum Version | 1.0                   |
| Date Created     | 2026-10-10            |
| Total Changes    | 10 (C1–C10)           |
| Total Migrations | 14 (M037.1–M037.14)   |
| Total API Endpoints | 23                 |
| Total Permissions | 12 new                |
| Total Test Scenarios | 35 (TS-037-01–35)  |
| Dev Phases       | 5 phases / 23 days    |

---

## Phase 1 — Module Catalog & Licensing Tables (4 days)

### C1 — Module Catalog & Dependencies

#### M037.1 — Create `tenant.Modules` Table + Seed (Track 1A — 1.5 days)

| # | Task | Status |
|---|------|--------|
| 1 | Create `tenant.Modules` table with all 10 columns (`module_id`, `code`, `name`, `description`, `schema_names`, `is_always_on`, `is_available`, `icon`, `sort_order`, `created_at`) | ☐ |
| 2 | Add PK on `module_id` (IDENTITY) | ☐ |
| 3 | Add UNIQUE constraint on `code` | ☐ |
| 4 | Seed 16 module rows: CORE, INVENTORY, FINANCE, CUSTOMERS, SUPPLIERS, PROCUREMENT, DEMAND, WAREHOUSE, LOGISTICS, PRODUCTION, SERVICES, POS, PROJECTS, CRM, MAINTENANCE, ECOMMERCE | ☐ |
| 5 | Verify `is_always_on = 1` for CORE, INVENTORY, FINANCE, CUSTOMERS | ☐ |
| 6 | Verify `is_available = 0` for POS, PROJECTS, CRM, MAINTENANCE, ECOMMERCE | ☐ |
| 7 | Verify `schema_names` correctly maps each module (e.g., CORE → `auth,tenant,lookups,workflow,hangfire,reports`) | ☐ |
| 8 | Verify `sort_order` 1–16 in correct sequence | ☐ |

#### M037.2 — Create `tenant.ModuleDependencies` Table + Seed (Track 1A — cont.)

| # | Task | Status |
|---|------|--------|
| 9 | Create `tenant.ModuleDependencies` table: `module_id`, `depends_on_module_id` | ☐ |
| 10 | Add composite PK `(module_id, depends_on_module_id)` | ☐ |
| 11 | Add FK `module_id → Modules.module_id` | ☐ |
| 12 | Add FK `depends_on_module_id → Modules.module_id` | ☐ |
| 13 | Add CHECK constraint `module_id <> depends_on_module_id` | ☐ |
| 14 | Seed dependency rows: PROCUREMENT → (INVENTORY, SUPPLIERS), DEMAND → PROCUREMENT, WAREHOUSE → INVENTORY, LOGISTICS → WAREHOUSE, PRODUCTION → INVENTORY, SERVICES → INVENTORY, POS → (INVENTORY, FINANCE, CUSTOMERS), PROJECTS → INVENTORY, CRM → CUSTOMERS, MAINTENANCE → INVENTORY, ECOMMERCE → (INVENTORY, CUSTOMERS) | ☐ |
| 15 | Verify no circular dependencies exist in seed data | ☐ |

### C2 — Module Features (Sub-Module Feature Flags)

#### M037.3 — Create `tenant.ModuleFeatures` Table + Seed (Track 1B — 1 day)

| # | Task | Status |
|---|------|--------|
| 16 | Create `tenant.ModuleFeatures` table with 9 columns (`feature_id`, `module_id`, `code`, `name`, `description`, `is_core_feature`, `is_available`, `requires_feature_id`, `sort_order`) | ☐ |
| 17 | Add PK on `feature_id` (IDENTITY) | ☐ |
| 18 | Add FK `module_id → Modules.module_id` | ☐ |
| 19 | Add self-referencing FK `requires_feature_id → ModuleFeatures.feature_id` | ☐ |
| 20 | Add UNIQUE constraint `(module_id, code)` | ☐ |
| 21 | Seed INVENTORY features (7): PRODUCT_CATALOG (core), STOCK_LEVELS (core), STOCK_ADJUSTMENTS (core), STOCK_TRANSFERS, STOCK_COUNTS, PRODUCT_VARIANTS (unavailable), BOM_MANAGEMENT | ☐ |
| 22 | Seed SUPPLIERS features (3): SUPPLIER_MASTER (core), SUPPLIER_CONTACTS (core), SUPPLIER_EVALUATION | ☐ |
| 23 | Seed PROCUREMENT features (5): PURCHASE_ORDERS (core), GRN_MANAGEMENT (core), RFQ_MANAGEMENT, PURCHASE_RETURNS, BLANKET_ORDERS (unavailable) | ☐ |
| 24 | Seed DEMAND features (2): DEMAND_FORECASTING (core), REORDER_SUGGESTIONS (core) | ☐ |
| 25 | Seed WAREHOUSE features (5): BIN_MANAGEMENT (core), PICK_LISTS, PUT_AWAY_RULES, CYCLE_COUNTS, BARCODE_SCANNING (unavailable) | ☐ |
| 26 | Seed LOGISTICS features (4): DELIVERY_ORDERS (core), SHIPMENT_TRACKING, CARRIER_INTEGRATION (unavailable), RETURN_ORDERS | ☐ |
| 27 | Seed PRODUCTION features (6): PRODUCTION_ORDERS (core), MATERIAL_REQUIREMENTS (core), MRP_SCHEDULING, QUALITY_INSPECTION, WORK_CENTERS (unavailable), PRODUCTION_COSTING | ☐ |
| 28 | Seed SERVICES features (4): SERVICE_ORDERS (core), SERVICE_BOM (core), FIELD_SERVICE (unavailable), SERVICE_CONTRACTS (unavailable) | ☐ |
| 29 | Seed FINANCE features (5): CHART_OF_ACCOUNTS (core), JOURNAL_ENTRIES (core), BANK_RECONCILIATION (unavailable), MULTI_CURRENCY, BUDGETING (unavailable) | ☐ |
| 30 | Seed CUSTOMERS features (3): CUSTOMER_MASTER (core), CREDIT_MANAGEMENT, LOYALTY_PROGRAM (unavailable) | ☐ |
| 31 | Verify all `is_core_feature = 1` entries are also `is_available = 1` | ☐ |

### C3 — Organization Module Licensing & Grace Period

#### M037.4 — Create `tenant.OrgModules` + Seed Existing Tenants (Track 1C — 1.5 days)

| # | Task | Status |
|---|------|--------|
| 32 | Create `tenant.OrgModules` table with 11 columns (`org_module_id`, `org_id`, `module_id`, `is_enabled`, `license_tier`, `enabled_at`, `enabled_by`, `disabled_at`, `disabled_by`, `grace_period_ends_at`, `trial_expires_at`) | ☐ |
| 33 | Add PK on `org_module_id` (BIGINT IDENTITY) | ☐ |
| 34 | Add FK `org_id → Organizations.org_id` | ☐ |
| 35 | Add FK `module_id → Modules.module_id` | ☐ |
| 36 | Add FK `enabled_by → Users.user_id` | ☐ |
| 37 | Add FK `disabled_by → Users.user_id` | ☐ |
| 38 | Add UNIQUE constraint `(org_id, module_id)` | ☐ |
| 39 | Add CHECK constraint `grace_period_ends_at IS NULL OR disabled_at IS NOT NULL` | ☐ |
| 40 | Seed: every existing tenant gets all `is_available = 1` modules enabled with `license_tier = 0` | ☐ |
| 41 | Verify seed uses `TOP 1 admin user` per org for `enabled_by` | ☐ |

#### M037.5 — Create `tenant.OrgModuleFeatures` + Seed (Track 1C — cont.)

| # | Task | Status |
|---|------|--------|
| 42 | Create `tenant.OrgModuleFeatures` table with 6 columns (`org_module_feature_id`, `org_id`, `feature_id`, `is_enabled`, `enabled_at`, `enabled_by`) | ☐ |
| 43 | Add PK on `org_module_feature_id` (BIGINT IDENTITY) | ☐ |
| 44 | Add FK `org_id → Organizations.org_id` | ☐ |
| 45 | Add FK `feature_id → ModuleFeatures.feature_id` | ☐ |
| 46 | Add FK `enabled_by → Users.user_id` | ☐ |
| 47 | Add UNIQUE constraint `(org_id, feature_id)` | ☐ |
| 48 | Seed: all `is_available = 1` features for each tenant's enabled modules | ☐ |

#### M037.6 — Create `tenant.OrgModuleHistory` (Track 1C — cont.)

| # | Task | Status |
|---|------|--------|
| 49 | Create `tenant.OrgModuleHistory` table with 10 columns (`history_id`, `org_id`, `module_id`, `feature_id`, `action`, `performed_by`, `performed_at`, `previous_tier`, `new_tier`, `notes`) | ☐ |
| 50 | Add PK on `history_id` (BIGINT IDENTITY) | ☐ |
| 51 | Add FKs: `org_id → Organizations`, `module_id → Modules`, `feature_id → ModuleFeatures`, `performed_by → Users` | ☐ |
| 52 | Add index `IX (org_id, module_id, performed_at DESC)` | ☐ |
| 53 | Verify action values: 0=Enabled, 1=Disabled, 2=FeatureEnabled, 3=FeatureDisabled, 4=TierChanged, 5=GracePeriodExpired | ☐ |

---

## Phase 2 — Module Access Infrastructure (5 days)

### C4 — Module Access Infrastructure (Middleware, Attributes, Service)

#### IModuleService Implementation (Track 2A — 1.5 days)

| # | Task | Status |
|---|------|--------|
| 54 | Create `IModuleService` interface with 9 methods: `IsModuleEnabled(code)`, `IsModuleEnabled(orgId, code)`, `IsFeatureEnabled(code)`, `IsInGracePeriod(code)`, `GetEnabledModulesAsync()`, `GetEnabledFeaturesAsync()`, `EnableModuleAsync(code, tier)`, `DisableModuleAsync(code, graceDays)`, `SetFeatureEnabledAsync(code, enabled)` | ☐ |
| 55 | Implement `ModuleService` class | ☐ |
| 56 | Implement per-request caching via `IHttpContextAccessor` (one DB hit per request) | ☐ |
| 57 | Implement `IMemoryCache` with 5-minute sliding expiration for cross-request caching | ☐ |
| 58 | Implement cache invalidation on enable/disable actions | ☐ |
| 59 | Register `ModuleService` as **Scoped** in DI container | ☐ |
| 60 | Implement `EnableModuleAsync` — validate dependencies before enabling | ☐ |
| 61 | Implement `DisableModuleAsync` — validate active dependents, set grace period, log to history | ☐ |
| 62 | Implement `SetFeatureEnabledAsync` — validate parent module enabled, core feature check | ☐ |
| 63 | Implement `IsInGracePeriod` — check `disabled_at IS NOT NULL AND grace_period_ends_at > SYSUTCDATETIME()` | ☐ |
| 64 | Create `ModuleActionResult` return type (success/failure + messages) | ☐ |

#### Attributes + Middleware (Track 2B — 1.5 days)

| # | Task | Status |
|---|------|--------|
| 65 | Create `[RequiresModule]` attribute with properties: `ModuleCode`, `AllowReadWhenDisabled` (default false), `AllowDuringGracePeriod` (default true) | ☐ |
| 66 | Create `[RequiresFeature]` attribute with property: `FeatureCode` | ☐ |
| 67 | Implement `ModuleAccessMiddleware` in ASP.NET Core pipeline | ☐ |
| 68 | Position middleware: after Authentication → after Tenant Resolution → **ModuleAccessMiddleware** → Controller | ☐ |
| 69 | Middleware logic: read `[RequiresModule]` from endpoint metadata | ☐ |
| 70 | Middleware logic: no attribute → pass through (CORE endpoints) | ☐ |
| 71 | Middleware logic: module enabled → pass through | ☐ |
| 72 | Middleware logic: module disabled + `AllowReadWhenDisabled = true` + GET → pass through | ☐ |
| 73 | Middleware logic: module disabled + in grace + `AllowDuringGracePeriod = true` + PUT/PATCH → pass through | ☐ |
| 74 | Middleware logic: otherwise → 403 `MODULE_NOT_LICENSED` JSON response | ☐ |
| 75 | Include `graceEndsAt` in 403 response body when in grace period | ☐ |
| 76 | Implement `[RequiresFeature]` check in middleware (requires parent module enabled) | ☐ |

#### Controller Tagging (Track 2C — 1 day)

| # | Task | Status |
|---|------|--------|
| 77 | Tag INVENTORY controllers: ProductsController, StockLevelsController, StockMovementsController, StockAdjustmentsController, BomController (after C6) | ☐ |
| 78 | Tag SUPPLIERS controllers: SuppliersController, SupplierContactsController | ☐ |
| 79 | Tag PROCUREMENT controllers: PurchaseOrdersController, PurchaseOrderLinesController, GrnController | ☐ |
| 80 | Tag DEMAND controllers: DemandForecastsController, ReorderSuggestionsController | ☐ |
| 81 | Tag WAREHOUSE controllers: WarehousesController, BinsController, PickListsController, PutAwayController | ☐ |
| 82 | Tag LOGISTICS controllers: DeliveryOrdersController, ShipmentsController, CarriersController | ☐ |
| 83 | Tag PRODUCTION controllers: ProductionOrdersController, MaterialRequirementsController, MaterialIssuesController (action-level), FGReceiptController, ProductionLedgerController | ☐ |
| 84 | Tag SERVICES controllers: ServiceOrdersController, ServiceMaterialRequirementsController, ServiceLedgerController | ☐ |
| 85 | Tag FINANCE controllers: JournalEntriesController, ChartOfAccountsController, LedgerController | ☐ |
| 86 | Tag CUSTOMERS controller: CustomersController (new in C8) | ☐ |
| 87 | Handle MaterialIssuesController special case — dual-module logic at action level: `production_order_id` → check PRODUCTION, `service_order_id` → check SERVICES | ☐ |
| 88 | Verify CORE controllers have NO `[RequiresModule]` tag (always-on, skipped by middleware) | ☐ |

#### Background Job Retrofit (Track 2D — 1 day)

| # | Task | Status |
|---|------|--------|
| 89 | Create `ModuleAwareJob` abstract base class with `RequiredModuleCode` property and `ProcessAllTenants()` method | ☐ |
| 90 | `ProcessAllTenants()` iterates all active tenants, skips those without module enabled | ☐ |
| 91 | Retrofit MRP Calculation Job → `RequiredModuleCode = "PRODUCTION"` | ☐ |
| 92 | Retrofit Demand Forecast Refresh Job → `RequiredModuleCode = "DEMAND"` | ☐ |
| 93 | Retrofit Reorder Point Check Job → `RequiredModuleCode = "DEMAND"` | ☐ |
| 94 | Retrofit Delivery Order Status Sync Job → `RequiredModuleCode = "LOGISTICS"` | ☐ |
| 95 | Verify Allocation Engine Batch remains always-run (INVENTORY — always on) | ☐ |
| 96 | Verify Hangfire Dashboard remains always-run (CORE — always on) | ☐ |
| 97 | Create Grace Period Expiry daily job — checks expired grace periods, sets `grace_period_ends_at = NULL`, logs `GracePeriodExpired` to history | ☐ |

---

## Phase 3 — Product Extensions & BOM Migration (5 days)

### C5 — Product Capability Flags & Extension Table Pattern

#### M037.7 — Add Capability Flags + `modified_at` to `inventory.Products` (Track 3A — 1 day)

| # | Task | Status |
|---|------|--------|
| 98 | Add column `is_purchasable BIT NOT NULL DEFAULT 1` | ☐ |
| 99 | Add column `is_sellable BIT NOT NULL DEFAULT 0` | ☐ |
| 100 | Add column `is_manufacturable BIT NOT NULL DEFAULT 0` | ☐ |
| 101 | Add column `is_serviceable BIT NOT NULL DEFAULT 0` | ☐ |
| 102 | Add column `is_stockable BIT NOT NULL DEFAULT 1` | ☐ |
| 103 | Add column `modified_at DATETIME2(2) NOT NULL DEFAULT SYSUTCDATETIME()` | ☐ |
| 104 | Create UPDATE trigger `trg_Products_ModifiedAt` for auto-setting `modified_at` | ☐ |
| 105 | Backfill existing products: set capability flags based on current product_type usage | ☐ |

#### M037.8 — Create Extension Tables (Track 3B — 1 day)

| # | Task | Status |
|---|------|--------|
| 106 | Create `material.ProductProductionSettings` table: `product_id` (PK, FK), `default_bom_id`, `production_type`, `scrap_percentage`, `manufacturing_lead_time_days`, `default_work_center_id` | ☐ |
| 107 | Add FK `product_id → inventory.Products.product_id` | ☐ |
| 108 | Add FK `default_bom_id → inventory.BillOfMaterials.bom_id` (NULL) | ☐ |
| 109 | Verify `production_type`: 0=MakeToStock, 1=MakeToOrder, 2=Assemble | ☐ |
| 110 | Create `service.ProductServiceSettings` table: `product_id` (PK, FK), `default_service_bom_id`, `service_category`, `estimated_duration_hours`, `requires_site_visit`, `is_subcontractable` | ☐ |
| 111 | Add FK `product_id → inventory.Products.product_id` | ☐ |
| 112 | Add FK `default_service_bom_id → inventory.BillOfMaterials.bom_id` (NULL) | ☐ |
| 113 | Verify `service_category`: 0=General, 1=Installation, 2=Repair, 3=Maintenance, 4=Consulting | ☐ |
| 114 | If existing production-specific columns on `inventory.Products`, migrate data into `material.ProductProductionSettings` | ☐ |

### C6 — BOM Schema Migration (material → inventory)

#### M037.9 — BOM Schema Transfer + `bom_usage` (Track 3C — 1.5 days)

| # | Task | Status |
|---|------|--------|
| 115 | Execute `ALTER SCHEMA inventory TRANSFER material.BillOfMaterials` | ☐ |
| 116 | Execute `ALTER SCHEMA inventory TRANSFER material.BomLines` | ☐ |
| 117 | Verify all FK constraints followed the transfer (SQL Server auto-follows) | ☐ |
| 118 | Verify all indexes followed the transfer | ☐ |
| 119 | Add column `bom_usage TINYINT NOT NULL DEFAULT 0` on `inventory.BillOfMaterials` — 0=Universal, 1=ProductionPreferred, 2=ServicePreferred, 3=PosKit(future) | ☐ |
| 120 | Add column `modified_at DATETIME2(2) NOT NULL DEFAULT SYSUTCDATETIME()` on `inventory.BillOfMaterials` | ☐ |
| 121 | Add column `modified_at DATETIME2(2) NOT NULL DEFAULT SYSUTCDATETIME()` on `inventory.BomLines` | ☐ |
| 122 | Create trigger `trg_BillOfMaterials_ModifiedAt` | ☐ |
| 123 | Create trigger `trg_BomLines_ModifiedAt` | ☐ |
| 124 | Update any stored procedures, views, or computed columns referencing `material.BillOfMaterials` → `inventory.BillOfMaterials` | ☐ |

#### EF Core + API Reconfiguration (Track 3D — 1.5 days)

| # | Task | Status |
|---|------|--------|
| 125 | Remove `BillOfMaterials` and `BomLines` DbSet from `MaterialDbContext` | ☐ |
| 126 | Add `BillOfMaterials` and `BomLines` DbSet to `InventoryDbContext` with `inventory` schema mapping | ☐ |
| 127 | Relocate `IBomService` DI registration from Material module to Inventory module | ☐ |
| 128 | Verify `ProductionBomExplosionService` still injects `IBomService` correctly | ☐ |
| 129 | Verify `ServiceBomExplosionService` still injects `IBomService` correctly | ☐ |
| 130 | Move `BomController` attribute from `[RequiresModule("PRODUCTION")]` to `[RequiresModule("INVENTORY")]` | ☐ |
| 131 | Change BOM API route: `/api/production/bom` → `/api/bom` | ☐ |
| 132 | Add 301 redirect from old `/api/production/bom/*` routes to `/api/bom/*` (one release cycle) | ☐ |
| 133 | Update Product form BOM tab visibility: controlled by `BOM_MANAGEMENT` feature flag, not PRODUCTION module | ☐ |
| 134 | Implement EF Core conditional Include for extension data: `Include(p => p.ProductionSettings)` when PRODUCTION enabled, `Include(p => p.ServiceSettings)` when SERVICES enabled | ☐ |
| 135 | Product API: conditionally include/exclude extension keys in response based on module enablement | ☐ |

---

## Phase 4 — Routes + Customer Master (4 days)

### C7 — Fulfillment Routes & Product Routes

#### M037.10 — Create Route Tables + Seed (Track 4A — 1.5 days)

| # | Task | Status |
|---|------|--------|
| 136 | Create `inventory.Routes` table: `route_id`, `org_id`, `code`, `name`, `description`, `requires_module`, `requires_feature`, `sequence`, `is_active`, `created_at`, `modified_at` | ☐ |
| 137 | Add PK on `route_id` (INT IDENTITY) | ☐ |
| 138 | Add FK `org_id → Organizations.org_id` | ☐ |
| 139 | Add UNIQUE constraint `(org_id, code)` | ☐ |
| 140 | Create `inventory.ProductRoutes` table: `product_id`, `route_id`, `is_preferred`, `org_id` | ☐ |
| 141 | Add composite PK `(product_id, route_id)` | ☐ |
| 142 | Add FK `product_id → Products.product_id` | ☐ |
| 143 | Add FK `route_id → Routes.route_id` | ☐ |
| 144 | Add FK `org_id → Organizations.org_id` | ☐ |
| 145 | Seed 7 default routes per org: BUY (→PROCUREMENT), MANUFACTURE (→PRODUCTION), SERVICE (→SERVICES), MTO (→PRODUCTION), PPS (→WAREHOUSE), DIRECT_SHIP (→PROCUREMENT), INTERNAL (→NULL) | ☐ |
| 146 | Implement `GetAvailableRoutesAsync(productId)` — dynamically filters by module enablement | ☐ |
| 147 | Implement preferred route fallback logic: if preferred route's module disabled, next available by sequence becomes effective | ☐ |
| 148 | Implement "no available routes" warning when all routes unavailable | ☐ |
| 149 | Create Routes API: `GET /api/routes` — list available routes for tenant | ☐ |
| 150 | Create Routes API: `GET /api/products/{id}/routes` — filtered by module enablement | ☐ |
| 151 | Create Routes API: `PUT /api/products/{id}/routes` — set product routes | ☐ |

### C8 — Customer Master (New Schema)

#### M037.11 — Customer Schema + Table + Seed (Track 4B — 1.5 days)

| # | Task | Status |
|---|------|--------|
| 152 | Create `customers` schema: `CREATE SCHEMA customers` | ☐ |
| 153 | Create `customers.Customers` table with 25 columns (see §9.3.1 for full list) | ☐ |
| 154 | Add PK on `customer_id` (BIGINT IDENTITY) | ☐ |
| 155 | Add FK `org_id → Organizations.org_id` | ☐ |
| 156 | Add FK `created_by → Users.user_id` | ☐ |
| 157 | Add FK `modified_by → Users.user_id` (NULL) | ☐ |
| 158 | Add UNIQUE constraint `(org_id, code)` | ☐ |
| 159 | Add `ROWVERSION` for optimistic concurrency | ☐ |
| 160 | Add index `IX_Customers_OrgName (org_id, name)` | ☐ |
| 161 | Add filtered index `IX_Customers_OrgEmail (org_id, email) WHERE email IS NOT NULL` | ☐ |
| 162 | Add filtered index `IX_Customers_OrgPhone (org_id, phone) WHERE phone IS NOT NULL` | ☐ |
| 163 | Add index `IX_Customers_OrgType (org_id, customer_type)` | ☐ |
| 164 | Add index `IX_Customers_OrgModified (org_id, modified_at)` — delta sync | ☐ |
| 165 | Create trigger `trg_Customers_ModifiedAt` for auto-setting `modified_at` | ☐ |
| 166 | Add EF Core `HasQueryFilter(c => c.OrgId == _currentTenant.OrgId)` | ☐ |
| 167 | Seed Walk-In Customer per org: `code = 'C-WALKIN'`, `customer_type = 0`, `is_active = 1` | ☐ |
| 168 | Verify `customer_type` enum: 0=WalkIn, 1=Individual, 2=Company, 3=Employee | ☐ |
| 169 | Implement Customer auto-code generation: `C-{5-digit sequence per org}` | ☐ |

#### Customer API (Track 4B — cont.)

| # | Task | Status |
|---|------|--------|
| 170 | Create `CustomersController` with `[RequiresModule("CUSTOMERS")]` | ☐ |
| 171 | `GET /api/customers` — list customers (paginated, filterable by type, searchable by name/phone/email) | ☐ |
| 172 | `GET /api/customers/{id}` — get customer detail | ☐ |
| 173 | `POST /api/customers` — create customer (auto-generate code) | ☐ |
| 174 | `PUT /api/customers/{id}` — update customer | ☐ |
| 175 | `PATCH /api/customers/{id}/status` — activate/deactivate | ☐ |
| 176 | `GET /api/customers/search?q={term}` — quick search for POS/SO lookups | ☐ |
| 177 | `GET /api/customers/{id}/balance` — current outstanding balance | ☐ |
| 178 | Validate: Walk-In customer (C-WALKIN) cannot be deleted or deactivated (CUST-01) | ☐ |
| 179 | Validate: WalkIn type customers cannot have `credit_limit > 0` (CUST-04) | ☐ |

#### Customer UI (Track 4C — 1 day)

| # | Task | Status |
|---|------|--------|
| 180 | Create Customer List page at `/customers` | ☐ |
| 181 | List columns: Code (100px), Name (250px), Type (100px), Phone (130px), Email (200px), Credit Limit (120px), Balance (120px), Status (80px) | ☐ |
| 182 | Sortable columns: Code, Name, Type, Credit Limit, Balance, Status | ☐ |
| 183 | Filterable: Code (search), Name (search), Type (dropdown), Phone (search), Email (search), Status (toggle) | ☐ |
| 184 | Actions: "+ New Customer" button, Export | ☐ |
| 185 | Create Customer Detail page at `/customers/{id}` | ☐ |
| 186 | Detail tabs: Details (name, type, contact, address, tax ID, payment terms, notes), Financial (credit limit, balance), Orders (future), Timeline | ☐ |

---

## Phase 5 — Cross-Module Awareness & Frontend (5 days)

### C9 — Cross-Module Awareness (Workflow, Permissions, Sync Timestamps)

#### M037.12 — Workflow ApprovalRules `module_code` + Backfill (Track 5A — 1 day)

| # | Task | Status |
|---|------|--------|
| 187 | Add column `module_code VARCHAR(30) NULL` to `workflow.ApprovalRules` | ☐ |
| 188 | Backfill: `PRODUCTION` for entity_type IN ('ProductionOrder', 'MaterialIssue') | ☐ |
| 189 | Backfill: `PROCUREMENT` for entity_type IN ('PurchaseOrder', 'GRN') | ☐ |
| 190 | Backfill: `SERVICES` for entity_type IN ('ServiceOrder') | ☐ |
| 191 | Backfill: `LOGISTICS` for entity_type IN ('DeliveryOrder', 'Shipment') | ☐ |
| 192 | Backfill: `WAREHOUSE` for entity_type IN ('StockTransfer', 'StockAdjustment') | ☐ |
| 193 | Backfill: `INVENTORY` for entity_type IN ('StockCount') | ☐ |
| 194 | Verify: rules with `module_code = NULL` apply regardless of module status | ☐ |
| 195 | Update workflow engine: skip rules where associated module is disabled (dormant behavior) | ☐ |

#### M037.13 — Permissions `module_code` + Backfill (Track 5A — cont.)

| # | Task | Status |
|---|------|--------|
| 196 | Add column `module_code VARCHAR(30) NULL` to `auth.Permissions` | ☐ |
| 197 | Backfill: `PRODUCTION` for code LIKE 'production.%' OR 'material.%' | ☐ |
| 198 | Backfill: `PROCUREMENT` for code LIKE 'procurement.%' OR 'purchase.%' | ☐ |
| 199 | Backfill: `SERVICES` for code LIKE 'service.%' | ☐ |
| 200 | Backfill: `WAREHOUSE` for code LIKE 'warehouse.%' | ☐ |
| 201 | Backfill: `LOGISTICS` for code LIKE 'logistics.%' OR 'delivery.%' | ☐ |
| 202 | Backfill: `FINANCE` for code LIKE 'finance.%' OR 'journal.%' | ☐ |
| 203 | Backfill: `INVENTORY` for code LIKE 'inventory.%' OR 'product.%' OR 'stock.%' | ☐ |
| 204 | Role Management UI: group permissions by module | ☐ |
| 205 | Role Management UI: permissions for disabled modules shown greyed with tooltip "Enable the {Module} module to use this permission" | ☐ |
| 206 | Permissions for disabled modules can still be pre-assigned but not enforced | ☐ |

#### M037.14 — Sync Timestamps on Shared Lookup Tables (Track 5B — 0.5 days)

| # | Task | Status |
|---|------|--------|
| 207 | Add `modified_at DATETIME2(2) NOT NULL DEFAULT SYSUTCDATETIME()` to `lookups.TaxRates` | ☐ |
| 208 | Add `modified_at DATETIME2(2) NOT NULL DEFAULT SYSUTCDATETIME()` to `lookups.UnitOfMeasures` | ☐ |
| 209 | Add `modified_at DATETIME2(2) NOT NULL DEFAULT SYSUTCDATETIME()` to `lookups.Categories` | ☐ |
| 210 | Add `modified_at DATETIME2(2) NOT NULL DEFAULT SYSUTCDATETIME()` to `inventory.Warehouses` | ☐ |
| 211 | Create trigger `trg_TaxRates_ModifiedAt` on `lookups.TaxRates` | ☐ |
| 212 | Create trigger `trg_UnitOfMeasures_ModifiedAt` on `lookups.UnitOfMeasures` | ☐ |
| 213 | Create trigger `trg_Categories_ModifiedAt` on `lookups.Categories` | ☐ |
| 214 | Create trigger `trg_Warehouses_ModifiedAt` on `inventory.Warehouses` | ☐ |

#### Sync API Foundation (Track 5B — cont.)

| # | Task | Status |
|---|------|--------|
| 215 | `GET /api/sync/catalog?since={iso8601}` — delta sync: products, categories, taxes, UoMs changed since timestamp | ☐ |
| 216 | `GET /api/sync/customers?since={iso8601}` — delta sync: customers changed since timestamp | ☐ |
| 217 | Verify only records with `modified_at > since` are returned | ☐ |

### C10 — Frontend Module Context & Module Administration UI

#### React Module Context (Track 5C — 1 day)

| # | Task | Status |
|---|------|--------|
| 218 | Create `ModuleProvider` React Context with `enabledModules[]`, `enabledFeatures[]`, `isModuleEnabled()`, `isFeatureEnabled()`, `isLoading`, `refresh()` | ☐ |
| 219 | `ModuleProvider` fetches `/api/tenant/modules/enabled` on app mount | ☐ |
| 220 | Implement cache in React state, refresh on module enable/disable or page reload | ☐ |
| 221 | Create `ModuleGuard` component with props: `module?`, `feature?`, `children`, `fallback?` | ☐ |
| 222 | `ModuleGuard` renders children when module/feature is enabled, `fallback` (default: null) when disabled | ☐ |
| 223 | Create `useModule()` hook: `{ isEnabled, isFeatureEnabled }` | ☐ |

#### Sidebar + Product Form Conditional Tabs (Track 5D — 1 day)

| # | Task | Status |
|---|------|--------|
| 224 | Wrap all sidebar nav items with `<ModuleGuard module="...">` per module | ☐ |
| 225 | Sidebar: BOM nav item wrapped with `<ModuleGuard feature="BOM_MANAGEMENT">` | ☐ |
| 226 | Sidebar: sub-features wrapped with `<ModuleGuard feature="...">` (e.g., RFQ → RFQ_MANAGEMENT, Pick Lists → PICK_LISTS, MRP Scheduler → MRP_SCHEDULING) | ☐ |
| 227 | Sidebar: Dashboard, Products, Customers, Inventory, Finance, Reports, Settings always visible | ☐ |
| 228 | Sidebar: Settings → Modules nav item (Admin only) added | ☐ |
| 229 | Product form tabs: Basic (always), Inventory (always), Procurement (PROCUREMENT), BOM (BOM_MANAGEMENT), Production (PRODUCTION + `is_manufacturable`), Service (SERVICES + `is_serviceable`), POS (future — POS + `is_sellable`), Finance (always) | ☐ |

#### Module Administration UI (Track 5E — 1.5 days)

| # | Task | Status |
|---|------|--------|
| 230 | Create Module Admin page at `/settings/modules` (Admin only) | ☐ |
| 231 | Section 1 — Core Modules: cards for CORE, INVENTORY, FINANCE, CUSTOMERS with "Always Active" badge, no toggle | ☐ |
| 232 | Section 2 — Active Modules: enabled toggleable module cards with toggle switch | ☐ |
| 233 | Section 3 — Available Modules: disabled but available modules as greyed cards with "Enable" button | ☐ |
| 234 | Section 4 — Coming Soon: `is_available = 0` modules as preview cards with "Coming Soon" badge | ☐ |
| 235 | Each card shows: icon, name, description, status badge (Active/Grace Period/Disabled/Coming Soon), dependencies line, feature count, toggle switch, "Manage Features" chevron | ☐ |
| 236 | Grace Period badge: amber with countdown | ☐ |
| 237 | "Manage Features" expand: feature list with name, toggle (core features locked-on), description, availability, dependencies | ☐ |
| 238 | Disable Module Confirmation Dialog: lists what will be blocked, what will NOT be affected (data, ledger, shared BOM), in-progress record count, grace period input (default 30 days) | ☐ |
| 239 | Dependency validation blocking dialog: "Cannot Disable {Module}" — lists active dependents that must be disabled first | ☐ |

#### Module Management API (Track 5E — cont.)

| # | Task | Status |
|---|------|--------|
| 240 | `GET /api/tenant/modules` — list all modules with dependency + feature info (Admin) | ☐ |
| 241 | `GET /api/tenant/modules/enabled` — list enabled module + feature codes for current tenant (Any) | ☐ |
| 242 | `POST /api/tenant/modules/{code}/enable` — enable module, validate dependencies (Admin) | ☐ |
| 243 | `POST /api/tenant/modules/{code}/disable` — disable module, validate dependents, set grace period (Admin) | ☐ |
| 244 | `PUT /api/tenant/modules/{code}/features/{featureCode}` — toggle feature (Admin) | ☐ |
| 245 | `GET /api/tenant/modules/{code}/history` — enable/disable history (Admin) | ☐ |
| 246 | `GET /api/tenant/modules/{code}/impact` — pre-disable impact check: in-progress records, dependent modules (Admin) | ☐ |

---

## New Permissions (12)

| # | Permission Code | Module | Verify |
|---|----------------|--------|--------|
| 247 | `tenant.modules.view` | CORE | ☐ |
| 248 | `tenant.modules.manage` | CORE | ☐ |
| 249 | `customers.view` | CUSTOMERS | ☐ |
| 250 | `customers.create` | CUSTOMERS | ☐ |
| 251 | `customers.update` | CUSTOMERS | ☐ |
| 252 | `customers.delete` | CUSTOMERS | ☐ |
| 253 | `inventory.bom.view` | INVENTORY | ☐ |
| 254 | `inventory.bom.create` | INVENTORY | ☐ |
| 255 | `inventory.bom.update` | INVENTORY | ☐ |
| 256 | `inventory.bom.delete` | INVENTORY | ☐ |
| 257 | `inventory.routes.view` | INVENTORY | ☐ |
| 258 | `inventory.routes.manage` | INVENTORY | ☐ |

---

## Business Rules Verification

### Module Lifecycle Rules (MOD-01 through MOD-11)

| # | Rule ID | Rule | Verify |
|---|---------|------|--------|
| 259 | MOD-01 | Always-on modules (CORE, INVENTORY, FINANCE, CUSTOMERS) reject disable requests | ☐ |
| 260 | MOD-02 | Dependency validation on enable — all dependencies must be enabled first | ☐ |
| 261 | MOD-03 | Dependent validation on disable — cannot disable if active dependents exist | ☐ |
| 262 | MOD-04 | Grace period set on disable: `grace_period_ends_at = disabled_at + graceDays` | ☐ |
| 263 | MOD-05 | Grace period expiry: daily Hangfire job checks expired, sets NULL, logs GracePeriodExpired | ☐ |
| 264 | MOD-06 | Feature requires parent module — disabling module auto-disables all features | ☐ |
| 265 | MOD-07 | Core features (`is_core_feature = 1`) cannot be individually disabled | ☐ |
| 266 | MOD-08 | BOM_MANAGEMENT auto-enable when PRODUCTION or SERVICES enabled; auto-disable when both off (preserves manual admin enable) | ☐ |
| 267 | MOD-09 | Every module action logged in OrgModuleHistory | ☐ |
| 268 | MOD-10 | Re-enable restores full access, no data migration required | ☐ |
| 269 | MOD-11 | Unavailable modules (`is_available = 0`) cannot be enabled | ☐ |

### Product Capability Rules (PRD-CAP-01 through PRD-CAP-04)

| # | Rule ID | Rule | Verify |
|---|---------|------|--------|
| 270 | PRD-CAP-01 | Capability flags are product attributes, independent of module toggle | ☐ |
| 271 | PRD-CAP-02 | Product tab visibility = module enabled AND flag = true | ☐ |
| 272 | PRD-CAP-03 | Extension data conditionally loaded — key absent when module disabled | ☐ |
| 273 | PRD-CAP-04 | Extension data preserved on disable — re-enable restores in API | ☐ |

### Route Rules (RTE-01 through RTE-03)

| # | Rule ID | Rule | Verify |
|---|---------|------|--------|
| 274 | RTE-01 | Available routes filtered by module enablement at query time | ☐ |
| 275 | RTE-02 | Preferred route fallback to next by sequence when module disabled | ☐ |
| 276 | RTE-03 | No-available-routes warning when all routes unavailable | ☐ |

### BOM Sharing Rules (BOM-SHR-01 through BOM-SHR-05)

| # | Rule ID | Rule | Verify |
|---|---------|------|--------|
| 277 | BOM-SHR-01 | BOM is module-agnostic in `inventory` schema | ☐ |
| 278 | BOM-SHR-02 | Creating/editing BOMs requires BOM_MANAGEMENT feature enabled | ☐ |
| 279 | BOM-SHR-03 | Reading BOM data always available through INVENTORY module | ☐ |
| 280 | BOM-SHR-04 | `bom_usage` is advisory filter/sort, not access control | ☐ |
| 281 | BOM-SHR-05 | BOM explosion module-specific: Production → material schema, Services → service schema | ☐ |

### Customer Rules (CUST-01 through CUST-04)

| # | Rule ID | Rule | Verify |
|---|---------|------|--------|
| 282 | CUST-01 | Walk-In Customer (C-WALKIN) cannot be deleted or deactivated | ☐ |
| 283 | CUST-02 | Customer code auto-generated: `C-{5-digit sequence per org}` | ☐ |
| 284 | CUST-03 | Credit limit enforcement (future): `current_balance + new_order <= credit_limit` | ☐ |
| 285 | CUST-04 | WalkIn (type 0) customers cannot have `credit_limit > 0` | ☐ |

---

## Test Scenarios (TS-037-01 through TS-037-35)

### Module Enable/Disable

| # | Test ID | Scenario | Pass |
|---|---------|----------|------|
| 286 | TS-037-01 | Enable PRODUCTION when INVENTORY is enabled → success, BOM_MANAGEMENT auto-enabled, history logged | ☐ |
| 287 | TS-037-02 | Enable PROCUREMENT when SUPPLIERS is disabled → fail "Enable Suppliers first" | ☐ |
| 288 | TS-037-03 | Disable INVENTORY when PRODUCTION active → fail "Disable Production first" | ☐ |
| 289 | TS-037-04 | Disable PRODUCTION with 3 in-progress POs → success, grace +30 days, history logged | ☐ |
| 290 | TS-037-05 | Create new Production Order during grace → 403 MODULE_NOT_LICENSED | ☐ |
| 291 | TS-037-06 | Complete existing Production Order during grace → success (state transition) | ☐ |
| 292 | TS-037-07 | Access Production Order after grace expired → GET 200, POST/PUT 403 | ☐ |
| 293 | TS-037-08 | Re-enable PRODUCTION → full access, in-progress resume, grace cleared | ☐ |
| 294 | TS-037-28 | Enable unavailable module (`is_available = 0`) → fail "Module not yet available" | ☐ |
| 295 | TS-037-30 | Concurrent enable/disable from two admins → row_version prevents conflict | ☐ |

### BOM Sharing

| # | Test ID | Scenario | Pass |
|---|---------|----------|------|
| 296 | TS-037-09 | Disable PRODUCTION, BOM still accessible → `/api/bom` returns 200, BOM tab visible if SERVICES active | ☐ |
| 297 | TS-037-10 | Disable both PRODUCTION and SERVICES → BOM_MANAGEMENT auto-disabled, BOM tab hidden, data preserved | ☐ |
| 298 | TS-037-11 | Enable SERVICES only (no PRODUCTION), create Service Order with BOM → success | ☐ |
| 299 | TS-037-31 | BOM `bom_usage = ProductionPreferred` listed in Service Order picker → appears (advisory, not restrictive) | ☐ |

### Feature Flags

| # | Test ID | Scenario | Pass |
|---|---------|----------|------|
| 300 | TS-037-12 | Toggle MRP_SCHEDULING off within PRODUCTION → MRP job skips tenant, POs still work, MRP menu hidden | ☐ |

### Product Form

| # | Test ID | Scenario | Pass |
|---|---------|----------|------|
| 301 | TS-037-13 | All modules enabled → all tabs visible: Basic, Inventory, Procurement, BOM, Production, Service, Finance | ☐ |
| 302 | TS-037-14 | Only INVENTORY + FINANCE → tabs: Basic, Inventory, Finance | ☐ |
| 303 | TS-037-15 | `is_manufacturable=true` + PRODUCTION disabled → Production tab hidden, flag preserved, re-enable shows tab | ☐ |

### Product API

| # | Test ID | Scenario | Pass |
|---|---------|----------|------|
| 304 | TS-037-16 | GET product with PRODUCTION enabled → response includes `productionSettings` | ☐ |
| 305 | TS-037-17 | GET product with PRODUCTION disabled → no `productionSettings` key | ☐ |

### Routes

| # | Test ID | Scenario | Pass |
|---|---------|----------|------|
| 306 | TS-037-18 | BUY + MANUFACTURE configured, PRODUCTION disabled → only BUY returned | ☐ |
| 307 | TS-037-19 | Preferred = MANUFACTURE, PRODUCTION disabled → fallback to BUY by sequence | ☐ |

### Customer

| # | Test ID | Scenario | Pass |
|---|---------|----------|------|
| 308 | TS-037-20 | Create WalkIn customer with `credit_limit > 0` → validation error | ☐ |
| 309 | TS-037-21 | Delete system Walk-In customer → fail (protected) | ☐ |
| 310 | TS-037-22 | Customer search by phone → returns matching, ordered by relevance | ☐ |

### Sync

| # | Test ID | Scenario | Pass |
|---|---------|----------|------|
| 311 | TS-037-23 | Delta sync `/api/sync/catalog?since=` → only `modified_at > since` returned | ☐ |

### UI / Navigation

| # | Test ID | Scenario | Pass |
|---|---------|----------|------|
| 312 | TS-037-24 | PRODUCTION + SERVICES disabled → their sidebar groups hidden, rest visible | ☐ |
| 313 | TS-037-25 | Module admin: disable shows impact dialog with correct in-progress count | ☐ |

### Cross-Module

| # | Test ID | Scenario | Pass |
|---|---------|----------|------|
| 314 | TS-037-26 | Workflow rule `module_code = PRODUCTION`, module disabled → rule dormant | ☐ |
| 315 | TS-037-27 | Permission `module_code = PRODUCTION`, module disabled → greyed in role mgmt, not enforced | ☐ |
| 316 | TS-037-29 | Material Issue for Service Order when PRODUCTION disabled → success (checks SERVICES) | ☐ |

### Background Jobs

| # | Test ID | Scenario | Pass |
|---|---------|----------|------|
| 317 | TS-037-32 | MRP job runs, tenant has PRODUCTION disabled → job skips tenant, no error | ☐ |
| 318 | TS-037-33 | Grace period daily job expires → `grace_period_ends_at = NULL`, history logged | ☐ |

### End-to-End Flows

| # | Test ID | Scenario | Pass |
|---|---------|----------|------|
| 319 | TS-037-34 | Enable fresh tenant with Trading Edition (INVENTORY + PROCUREMENT + SUPPLIERS + FINANCE + CUSTOMERS) → all enabled, sidebar correct, product form correct | ☐ |
| 320 | TS-037-35 | Upgrade Trading tenant to Manufacturing (add PRODUCTION + WAREHOUSE) → both enabled, BOM_MANAGEMENT auto-enabled, sidebar expands, product form adds tabs | ☐ |

---

## Summary

| Category | Count |
|----------|-------|
| Total checklist items | 320 |
| New tables | 8 (Modules, ModuleDependencies, ModuleFeatures, OrgModules, OrgModuleFeatures, OrgModuleHistory, ProductProductionSettings, ProductServiceSettings, Routes, ProductRoutes, Customers) |
| Modified tables | 6 (Products, BillOfMaterials, BomLines, ApprovalRules, Permissions, + 4 lookup tables) |
| Schema transfers | 2 (BillOfMaterials, BomLines from material → inventory) |
| New schema | 1 (customers) |
| SQL triggers | 8 (Products, BillOfMaterials, BomLines, Customers, TaxRates, UnitOfMeasures, Categories, Warehouses) |
| Migrations | 14 (M037.1–M037.14) |
| API endpoints | 23 (7 module mgmt + 7 customer + 5 BOM relocated + 3 routes + 2 sync) |
| New permissions | 12 |
| Business rules | 27 (MOD ×11 + PRD-CAP ×4 + RTE ×3 + BOM-SHR ×5 + CUST ×4) |
| Test scenarios | 35 |
| React components | 3 (ModuleProvider, ModuleGuard, useModule hook) |
| UI pages | 3 (Module Admin, Customer List, Customer Detail) |
| Development days | 23 (across 5 phases) |

---

*End of Task Register — Addendum 37*
