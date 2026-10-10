# Addendum 37 — API contract (backend ⇄ frontend)

`ApiResponse<T> { success, message, result }`, camelCase, UUIDs in routes, codes are strings. Decisions: [ADDENDUM-37-ANALYSIS.md](ADDENDUM-37-ANALYSIS.md).
Errors: 400 rule refusal (`message` user-facing), 403 permission / module, 404, 409 stale `rowVersion`.

## 1. Module registry — Tenancy (REG)

### 1.1 Org-admin endpoints `api/tenant/modules`
| # | Route | Permission | Body | Result |
|---|---|---|---|---|
| 1 | `GET /api/tenant/modules` | MODULES_VIEW | — | `ModuleCard[]` (sort: always-on, active, available, coming soon; then displayOrder) |
| 2 | `GET /api/tenant/modules/enabled` | any signed-in user | — | `EnabledModules` |
| 3 | `POST /api/tenant/modules/{code}/enable` | MODULES_MANAGE | `{ rowVersion? }` | `ModuleCard` |
| 4 | `POST /api/tenant/modules/{code}/disable` | MODULES_MANAGE | `{ graceDays?: number /*0–365, default 30*/, notes?, rowVersion? }` | `ModuleCard` |
| 5 | `PUT /api/tenant/modules/{code}/features/{featureCode}` | MODULES_MANAGE | `{ enabled: boolean, rowVersion? }` | `ModuleCard` |
| 6 | `GET /api/tenant/modules/{code}/history` | MODULES_VIEW | — | `ModuleHistoryEntry[]` newest first (module + its features) |
| 7 | `GET /api/tenant/modules/{code}/impact` | MODULES_VIEW | — | `ModuleImpact` |

```ts
EnabledModules { modules: string[]; features: string[];            // codes currently usable (grace ⇒ not listed)
  grace: { code: string; graceEndsAt: string }[] }                 // modules disabled but in grace
ModuleCard { code; name; description?; icon?; isAlwaysOn; isAvailable; isLicensed; isEnabled;
  status: 'ALWAYS_ON'|'ACTIVE'|'GRACE'|'DISABLED'|'NOT_LICENSED'|'COMING_SOON';
  graceEndsAt?; disabledAt?; disabledByName?; enabledAt?;
  dependsOn: { code; name; isEnabled }[]; dependents: { code; name; isEnabled }[];
  features: ModuleFeature[]; featureCount; enabledFeatureCount; rowVersion: string /*base64*/ }
ModuleFeature { code; name; description?; isCore; isAvailable; isLicensed; isEnabled; requiresCode?; requiredBy: string[];
  autoManaged: boolean /* BOM_MANAGEMENT switched by the system (MOD-08) */ }
ModuleHistoryEntry { performedAt; action; featureCode?; performedByName /* 'System' for jobs */; graceDays?; notes? }
ModuleImpact { code; name; dependents: { code; name }[] /* enabled dependents: disable is refused while any */;
  inProgress: { label; count }[]; willBlock: string[]; notAffected: string[]; defaultGraceDays: 30 }
```
400 messages (exact): `"{Name} is always on."` · `"{Name} is not yet available."` · `"{Name} is not included in your plan — contact your administrator."` · `"Enable {Dep names} first."` · `"Disable {Dependent names} first."` · `"Core features cannot be switched off."` · `"Switch on {Module name} first."` · `"Grace period must be between 0 and 365 days."`.
Actions: action codes `ENABLED DISABLED FEATURE_ENABLED FEATURE_DISABLED LICENSED UNLICENSED GRACE_EXPIRED AUTO_ENABLED AUTO_DISABLED`.

### 1.2 Super admin (existing `api/organizations/{id}/features`)
Unchanged routes. Toggling there sets the **licence** (`isLicensed` and `isEnabled` together), writes history (LICENSED/UNLICENSED). The GET rows gain `isLicensed`, `status`, `graceEndsAt`, `parentModuleCode`, `isAlwaysOn`, `isAvailable`. New `GET api/organizations/{id}/features/history` (same entry shape + `featureCode`).

### 1.3 Catalog additions (seeded, D-1/D-2)
Modules: `MODULE_CUSTOMERS` (always on), coming-soon `MODULE_DEMAND_PLANNING`, `MODULE_POS`, `MODULE_PROJECTS`, `MODULE_CRM`, `MODULE_MAINTENANCE`, `MODULE_ECOMMERCE`; `MODULE_PROCUREMENT` → not available.
Sub-features (`FEATURE_*`, parent in brackets). **Enforced** ones (✔) gate real endpoints/menus; the rest are catalog only (R-2):
INVENTORY: BOM_MANAGEMENT ✔, STOCK_TRANSFERS, STOCK_COUNTS, PRODUCT_VARIANTS (n/a) · SUPPLIERS: SUPPLIER_EVALUATION (= existing SCREEN_SUPPLIER_SCORECARD, re-parented, no new code) · DEMAND: RFQ_MANAGEMENT ✔, BLANKET_ORDERS (n/a) · WAREHOUSE: PURCHASE_RETURNS ✔ (SROs) · LOGISTICS: PICK_LISTS ✔, SHIPMENT_TRACKING ✔ (consignments, tracking links, carrier tracking), CARRIER_INTEGRATION (n/a) · MANUFACTURING: QUALITY_INSPECTION ✔, MRP_SCHEDULING, PRODUCTION_COSTING, WORK_CENTERS (n/a) · SERVICES: FIELD_SERVICE (n/a), SERVICE_CONTRACTS (n/a) · FINANCE: MULTI_CURRENCY, BANK_RECONCILIATION (n/a), BUDGETING = existing SCREEN_BUDGET_MONITOR · CUSTOMERS: CREDIT_MANAGEMENT ✔ (credit-limit field), LOYALTY_PROGRAM (n/a).
Existing entries get parents: SCREEN_SUPPLIER_SCORECARD→SUPPLIERS, SCREEN_PO_DOCUMENT_TEMPLATE→DEMAND, SCREEN_WORKFLOW_CONFIG→WORKFLOW_ENGINE, SCREEN_BUDGET_MONITOR→FINANCE, SCREEN_AUDIT_LOG/USER/ROLE→MASTER_DATA, FEATURE_EMAIL_NOTIFICATIONS/FEATURE_WHATSAPP→NOTIFICATIONS, FEATURE_MASTER_LEDGERS→INVENTORY.
New sub-features default to licensed+enabled in a plan exactly when their parent module is in that plan; BOM_MANAGEMENT per MOD-08.

### 1.4 Module 403
A disabled module/feature returns HTTP 403 with
`{ success: false, message: "The {Name} module is not enabled for your organization.", errorCode: "MODULE_NOT_LICENSED", result: { module: code, graceEndsAt: string|null } }`.
Grace rules (D-6): GET passes; a write passes during grace only on a route with a route parameter (acts on an existing record); creates are refused. After grace GET still passes when the org once had the module.

## 2. Products — Inventory (OPS-A)
`GET /api/products/{id}` adds `isServiceable` (read-only), `serviceCategory` (`GENERAL|INSTALLATION|REPAIR|MAINTENANCE|CONSULTING|null`), `requiresSiteVisit`, and — **key present only when the module is enabled**:
`productionSettings { supplyMethod; defaultProductionWarehouseId?; defaultProductionWarehouseName?; activeBomUuid?; activeBomNumber?; manufacturingLeadTimeDays? }` (MODULE_MANUFACTURING)
`serviceSettings { invoicingPolicy?; billingModel?; estimatedDurationHours?; hasServiceBom; isSubcontractable; serviceCategory?; requiresSiteVisit; activeServiceBomUuid? }` (MODULE_SERVICES).
`PATCH /api/products/{id}` accepts `serviceCategory`, `requiresSiteVisit` (service products only; 400 "Service category is only applicable to service products").

## 3. BOMs — Material (OPS-A)
Reads (`GET /api/boms…`) need MODULE_INVENTORY only; every write (create/update/lines/submit/approve/activate/obsolete) needs FEATURE_BOM_MANAGEMENT. DTOs gain `bomUsage` (`UNIVERSAL|PRODUCTION_PREFERRED|SERVICE_PREFERRED`, default UNIVERSAL, editable in any non-terminal status — advisory). List accepts `?preferFor=PRODUCTION|SERVICE` (matching usage first, nothing hidden). `PUT /api/boms/{uuid}/usage { bomUsage }` (BOM_EDIT + BOM_MANAGEMENT) changes it in any status but OBSOLETE; create/update accept `bomUsage` too (D-31). List/detail add `modifiedAt`.

## 4. Routes — Logistics (OPS-B)
`GET /api/fulfillment-routes` items add `isAvailable: boolean`, `unavailableReason?: string` ("Manufacturing is switched off").
`GET /api/products/{id}/routes` (FULFILLMENT_ROUTE_VIEW) → `{ variantUuid; variantName; sku; routeUuid?; routeName?; category?; isAvailable; effectiveRouteUuid?; effectiveRouteName?; warning? }[]`.
Sale order line DTOs add `routeWarning?: string` when the configured route is unavailable and a fallback (or nothing) applies.

## 5. Customers — Suppliers module (CUS)
| Route | Permission | Notes |
|---|---|---|
| `GET /api/customers?search&type&status=ACTIVE\|INACTIVE&page=1&pageSize=25&sortField&sortOrder` | CUSTOMER_VIEW | `PaginatedResponse<CustomerListItem>`; sort fields code,name,customerType,creditLimit,balance,status |
| `GET /api/customers/{uuid}` | CUSTOMER_VIEW | `CustomerDetail` |
| `POST /api/customers` | CUSTOMER_CREATE | `CustomerUpsert` → `uuid`; code auto `C-00001` |
| `PUT /api/customers/{uuid}` | CUSTOMER_EDIT | `CustomerUpsert` (+ `rowVersion` if the entity has one) |
| `PATCH /api/customers/{uuid}/status` | CUSTOMER_DEACTIVATE | `{ isActive }` |
| `GET /api/customers/search?q=&limit=10` | CUSTOMER_VIEW | `CustomerListItem[]`, active only; exact code/phone/mobile match first, then starts-with, then contains |
| `GET /api/customers/{uuid}/balance` | CUSTOMER_VIEW | `{ balance; currencyCode; overdue }` (base currency) |
```ts
CustomerListItem { uuid; code; name; customerType: 'WALK_IN'|'INDIVIDUAL'|'COMPANY'|'EMPLOYEE'; phone?; mobile?; email?;
  creditLimit: number; balance: number; currencyCode; isActive; isSystem }
CustomerDetail extends CustomerListItem { taxId?; addressLine1?; addressLine2?; city?; provinceState?; postalCode?; country?;
  paymentTermsDays: number; defaultSaleCurrencyId?; notes?; isVendor: boolean; createdAt; modifiedAt }
CustomerUpsert { name; customerType; phone?; mobile?; email?; taxId?; creditLimit?; paymentTermsDays?; defaultSaleCurrencyId?;
  addressLine1?; addressLine2?; city?; provinceState?; postalCode?; country?; notes? }
```
400: `"The walk-in customer cannot be deactivated."` · `"The walk-in customer cannot be changed to another type."` · `"Walk-in customers cannot have a credit limit."` · `"Name is required."`. Credit limit editable only with FEATURE_CREDIT_MANAGEMENT (otherwise ignored on write).

## 6. Sync (OPS-A catalog, CUS customers)
`GET /api/sync/catalog?since=<ISO-8601 UTC>&limit=500` (INVENTORY_VIEW) → `{ serverTime; hasMore; products[]; variants[]; categories[]; taxCodes[]; uoms[]; warehouses[] }` each item has `modifiedAt`, only rows with `modifiedAt > since` (no `since` = everything), `limit` per collection. Also `nextSince` (send as the next `since`; = `serverTime` unless `hasMore`) — D-33.
`GET /api/sync/customers?since=&limit=500` (CUSTOMER_VIEW) → `{ serverTime; hasMore; customers: CustomerListItem & { modifiedAt }[] }`.

## 7. Permissions & roles — Auth (REG)
New codes: `MODULES_VIEW`, `MODULES_MANAGE`, `CUSTOMER_VIEW`, `CUSTOMER_CREATE`, `CUSTOMER_EDIT`, `CUSTOMER_DEACTIVATE`.
The permission list used by role management gains `moduleCode?: string` and `moduleEnabled: boolean` (current org).

## 8. Workflow (REG)
Workflow definition list items gain `moduleCode?` and `moduleEnabled` (derived from InterfaceCode, D-14).

## Details fixed by the frontend (FE1)
- **Super-admin routes** live under `api/system/organizations/{id}/features` (the real controller route), so the history call is `GET api/system/organizations/{id}/features/history` — §1.2's `api/organizations/…` is read as that.
- **Role permissions (§7):** `moduleCode`/`moduleEnabled` are read on each permission item inside `RoleDetail.permissionGroups[].permissions[]` (`GET api/roles/{id}`). The UI regroups by `moduleCode` in server order and names the group from the catalog name map (`MODULE_DEMAND` → "Demand & Procurement" …); items without `moduleCode` stay in their server group. `moduleEnabled` absent = enabled.
- **Workflow (§8):** `moduleEnabled` absent = enabled; only `false` greys a definition row and its interface summary tile (`GET …/interfaces` summaries also carry `moduleCode`/`moduleEnabled`).
- **Module 403 (§1.4):** the toast uses `result.module` for the name (catalog name map, falling back to the "The {Name} module …" text of `message`) and `result.graceEndsAt` when present. The 403 is passed on unchanged, so pages still show their own error too.
- **Sections on Settings › Modules:** by `status` — `ALWAYS_ON` → Core; `ACTIVE` → Active; `GRACE`, `DISABLED`, `NOT_LICENSED` → Available (grace = disabled, so it is not in "Active"); `COMING_SOON` → Coming soon. No switch for `ALWAYS_ON`, `NOT_LICENSED`, `COMING_SOON`.
- **Row versions:** every enable/disable/feature call sends the card's `rowVersion` (the module's row, also for a feature PUT). After any success the page re-reads `GET /api/tenant/modules` (no in-place patch from the returned card) and calls `ModuleService.refresh()`.
- **Enable with dependencies off / feature of a module that is off:** not pre-blocked by the client (only shown as "Requires: X (off)" / "Switch on X first."); the server's 400 text is shown verbatim.
- **Disable:** `/impact` is always called first; `dependents.length > 0` → "Cannot disable" dialog (no disable call). Grace input 0–365 is also checked client-side (same sentence as the server). `notes` is trimmed and omitted when empty. `defaultGraceDays` missing → 30.
- **Menu:** feature gates use `GET /api/tenant/current` `enabledFeatureCodes`, which REG confirmed means "usable now" (grace excluded, new `FEATURE_*` codes included), so RFQ / Picking / Tracking / Supplier Returns follow their sub-features. `ModuleService.refresh()` reloads it after every module action and on a module 403. Logistics › Tracking (Exception Queue, Proof of Delivery, Carrier Scorecard) is gated on `FEATURE_SHIPMENT_TRACKING` as a group although only the consignment and tracking-link controllers enforce it server-side.

## Details fixed by the frontend (FE2)
- **Customers list (§5):** status filter defaults to `ACTIVE` (chips Active / Inactive / All; All = no `status`); `type` and `search` (400 ms debounce) omitted when empty. Only the six contract sort fields are sent, as `sortOrder=asc|desc`; any other column is unsorted. Page sizes 25/50/100.
- **Customer export:** client-side CSV of the current filter, built by walking `GET /api/customers` pages of 200 until `totalPages` (no server export endpoint). Columns Code, Name, Type, Phone, Mobile, Email, Credit limit, Balance, Currency, Status; UTF-8 with BOM.
- **Customer upsert:** the dialog always sends the full form (PUT replaces every field): trimmed text, empty → `null`, `paymentTermsDays` 0 when blank, `rowVersion` echoed when the detail had one. `creditLimit`: typed value with FEATURE_CREDIT_MANAGEMENT; without it a create omits it and an edit sends the stored value back; the walk-in always sends 0. `WALK_IN` cannot be chosen for any other customer; the walk-in's type is locked. 400 and other `message`s are shown verbatim; 409 → "Someone else changed this customer…". "Name is required." is also checked client-side before the call.
- **Customer currency:** `currencyCode` null → the amount is shown with no code (list, detail, balance KPI).
- **Customer detail:** `GET …/balance` supplies balance + overdue (falls back to the detail's `balance` if it fails). Orders tab = `GET /api/sale-orders?partnerId={customer uuid}&pageSize=10` (needs SALE_ORDER_VIEW; otherwise a note). Ledger link → `/portal/pages/finance/customer-ledger/{uuid}` (CUSTOMER_LEDGER_VIEW). No timeline (partners have none). Deactivate confirms first; activate does not; the walk-in's Deactivate is disabled. "Over credit" is shown only with credit management on and a limit > 0.
- **Customers menu/route:** `/portal/pages/customers` and `/portal/pages/customers/:uuid`, guard CUSTOMER_VIEW. Menu item sits first in the Sales group (`MODULE_CUSTOMERS` + CUSTOMER_VIEW) — so it inherits the group's `MODULE_DEMAND` gate (R-1); a standalone group would change the top-level menu.
- **Product page (§2):** a section shows when its key is non-null **and** the flag holds: production ⇒ `productionSettings` + `isManufacturable` (warehouse, active BOM number, manufacturing lead time); service ⇒ `serviceSettings` + `isServiceable` (`isServiceable` absent ⇒ productType SERVICE). Flags (Manufactured here / Serviceable) always show, with a "Manufacturing off" / "Services off" pill when the key is absent. Flat fields win over the nested ones for `serviceCategory`/`requiresSiteVisit` display. The edit dialog's service fields (now incl. category + site visit) follow the product type as before (not the module), so dormant data can still be kept.
- **POST /api/products:** the create page also sends `serviceCategory` and `requiresSiteVisit` (same shared service-settings payload as PATCH; null/false for non-service types). If the backend ignores them on create, they are lost until an edit.
- **BOM tab:** with FEATURE_BOM_MANAGEMENT as before (manufacturable, or service with service BOM). Without it the page calls `GET /api/boms?productUuid=&pageSize=1` and shows the tab read-only only when `totalRecords > 0` (no New/Create, no workflow buttons, usage locked, note shown). Feature checks on the product page use `TenantService.hasFeature` (tenant/current, same as its other module checks); customer pages use `ModuleService.isFeatureEnabled`.
- **BOM usage (§3):** `preferFor` = SERVICE for a service BOM product, else PRODUCTION, on the product page's BOM list. Draft/rejected/new: `bomUsage` goes with create/update. Any other status except OBSOLETE: the dropdown calls `PUT /api/boms/{uuid}/usage` at once (reverts on error). List shows a small tag for PRODUCTION_/SERVICE_PREFERRED only. There are **no BOM pickers** on the production-order / service-order forms (the recipe is snapshotted from the active BOM server-side), so nothing else passes `preferFor`.
- **Routes (§4):** `isAvailable` absent = available. Pickers (variant route field, SO form line, SO detail line) list available routes as before, then unavailable ones **disabled** as "Name — {unavailableReason}"; a line/variant that already has an unavailable route keeps it selectable-as-current with that label and (variant field) a warning that the org default stock route applies. Settings › Fulfillment routes lists unavailable routes dimmed with an "Unavailable · reason" pill. `GET /api/products/{uuid}/routes` is called with the product **uuid**, from a lazy "Routes" tab (MODULE_LOGISTICS + FULFILLMENT_ROUTE_VIEW/MANAGE/ASSIGN); a 403 hides the tab. `routeWarning` shows on SO detail lines and on SO form lines (live preview line first, else the saved line's).
