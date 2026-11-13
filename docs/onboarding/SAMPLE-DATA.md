# A50: sample data and the onboarding checklist (task 363)

Owner: DEMO-50. Decisions: D-27 and D-28 in [ADDENDUM-50-ANALYSIS.md](ADDENDUM-50-ANALYSIS.md), plus D-70..D-74 below. Code:
- `src/SMS.Modules.Onboarding/SampleData/**` and `Checklist/**`;
- one contributor or signal file per module: Suppliers, Inventory, Demand, Integration, Auth and Import, each named `Onboarding/*Onboarding*.cs`.

## 1. Details: the exact JSON (for FE-B)

All responses use the usual `ApiResponse` envelope (`{ success, message, result }`). JSON is camelCase. The base path is `api/onboarding`.

### 1.1 Sample data (permission `SAMPLE_DATA_MANAGE`)

**`GET sample-data`** returns the plan and the current state:
```json
{
  "hasDemo": false,
  "canLoad": true,
  "reason": null,
  "loadedAt": null,
  "counts":  { "customers": 5, "vendors": 3, "products": 20, "saleOrders": 2, "purchaseOrders": 1, "openingStock": 20 },
  "current": { "customers": 0, "vendors": 0, "products": 0, "saleOrders": 0, "purchaseOrders": 0, "openingStock": 0 },
  "notes": ["Opening stock is skipped: set the opening balance date in Finance settings first."]
}
```
- `counts` is what a load would create. A contributor whose module is off contributes 0 and adds a note.
- `openingStock` is the number of products that get opening stock. It is 0 when the opening-stock rules don't allow it (D-71), and `notes` then says why.
- `current` counts the demo records this org has now, from DemoRecords.
- `canLoad` is false, with `reason` set, when:
  - `hasDemo` is true: "Sample data is already loaded. Remove it first.";
  - the organization is LEGACY (D-74): "Sample data is for new organizations only. This organization already has its own data.";
  - no contributing module is enabled: "None of the modules that provide sample data is enabled."

**`POST sample-data/load`** takes no body. It returns:
```json
{ "created": { "customers": 5, "vendors": 3, "products": 20, "saleOrders": 2, "purchaseOrders": 1, "openingStock": 20 },
  "total": 51,
  "notes": [] }
```
- If demo data already exists, the call is refused with a 409 Conflict.
- A contributor that fails stops the load. Whatever was already created stays recorded, and a cleanup removes it. The error message names the failing step.

**`DELETE sample-data`** returns:
```json
{ "deleted": { "customers": 4, "vendors": 3, "products": 20, "saleOrders": 2, "purchaseOrders": 1, "openingStock": 20 },
  "total": 50,
  "kept": [ { "entityType": "Customer", "entityRef": "5b0c…-uuid", "name": null, "reason": "Used by real data." } ],
  "notes": [ "Acme Corporation (Demo) is kept: used by sale order SO-2026-00012." ] }
```
- A kept record keeps its DemoRecords row, so it still shows the Demo badge, and a later cleanup tries it again.
- `kept[].reason` is "Used by real data.", or "Cleanup failed; try again." when a module's cleanup threw. The record's name and what uses it are in `notes`, one line per kept record (FE shows `notes` as a list). `name` is reserved and is null today.
- Kept `ProductVariant` rows are not listed in `kept`; their product is.

**`GET demo/refs?entityType=Customer`** is open to any signed-in user. It returns a `string[]` of uuids, for example `["5b0câ€¦", "â€¦"]`. Omitting `entityType` returns every type.

| entityType | entityRef is | Badge goes on |
|---|---|---|
| `Customer` | the business partner uuid | the customer list (`uuid`) |
| `Vendor` | the business partner uuid | the supplier / partner lists (`uuid`) |
| `Product` | the **product** uuid (`ProductListItemModel.uuid`) | the product list |
| `ProductVariant` | the variant uuid | variant pickers (optional) |
| `SaleOrder` | the sale order uuid | the sale order list |
| `PurchaseOrder` | the PO uuid | the PO list |
| `OpeningStock` | `{variantUuid}:{warehouseId}` (internal, no badge) | |

### 1.2 Checklist (permission `ONBOARDING_VIEW`)

**`GET checklist`** evaluates the signals (any that tick are saved) and returns the status. **`POST checklist`** does exactly the same; it is a "refresh now" that FE can call after an action. Both return:
```json
{
  "dismissed": false,
  "dismissedAt": null,
  "completedCount": 3,
  "totalCount": 9,
  "isComplete": false,
  "steps": [
    { "stepCode": "COMPLETE_SETUP", "title": "Complete the setup wizard", "description": "Country, chart of accounts, taxes and defaults.",
      "link": "/portal/pages/onboarding/setup", "sortOrder": 10, "requiresModule": null,
      "completed": true, "completedAt": "2026-11-09T10:12:00Z", "autoCompleted": true }
  ]
}
```
- `link` is a full Angular route. FE navigates to it as is.
- `steps` are sorted by `sortOrder` and include only steps whose module is enabled. A step whose module is switched off later is hidden, but its saved tick is kept.

**`POST checklist/steps/{code}/complete`** ticks a step by hand (`autoCompleted: false`). It returns the status above. An unknown code is a 404; an already-ticked step is a no-op.

**`POST checklist/dismiss`** sets `dismissed: true` and returns the status. Dismissing is permanent; there is no undismiss endpoint (FSD Â§4.4).

FE shows the widget only when `!dismissed && !isComplete`. An organization that was live before A50 (setup note LEGACY) gets its checklist already dismissed (D-72), so the widget stays hidden for it.

### 1.3 Steps

| sortOrder | stepCode | Title | Link | Requires module | Auto-ticks when (non-demo records only) | Signal owner |
|---|---|---|---|---|---|---|
| 10 | COMPLETE_SETUP | Complete the setup wizard | /portal/pages/onboarding/setup | none | OrganizationSetups.SetupStatus is Completed or Skipped | Onboarding |
| 20 | ADD_CUSTOMER | Add your first customer | /portal/pages/customers | MODULE_CUSTOMERS | a customer that is not the walk-in customer | Suppliers |
| 30 | ADD_PRODUCT | Add your first product | /portal/pages/inventory/products/new | MODULE_INVENTORY | an active product | Inventory |
| 40 | ADD_SUPPLIER | Add your first supplier | /portal/pages/suppliers/supplier-create | MODULE_SUPPLIERS | a vendor | Suppliers |
| 50 | CREATE_SO | Create your first sale order | /portal/pages/sales/orders | MODULE_DEMAND | a sale order, not deleted | Demand |
| 60 | CREATE_PO | Create your first purchase order | /portal/pages/demand/purchase-orders/create | MODULE_DEMAND | a purchase order, not deleted | Demand |
| 70 | CONNECT_QB | Connect QuickBooks (optional) | /portal/pages/integrations/quickbooks | MODULE_INTEGRATION | a QuickBooks connection that finished OAuth (Connected, NeedsSetup or Live) | Integration |
| 80 | INVITE_TEAM | Invite your team members | /portal/pages/users | none | 2 or more internal users, not deleted (invited counts; supplier-portal accounts do not) | Auth |
| 90 | IMPORT_DATA | Import your existing data | /portal/pages/settings/import | MODULE_DATA_IMPORT | an import session COMPLETED or COMPLETED_WITH_ERRORS that created or updated 1+ rows | Import |

Module codes are `SMS.Shared.Common.ModuleCodes`. A step is shown when its module is enabled (`IModuleGate`); a host without the gate shows every step.

Ticks come from three places: the signals on every checklist read, the 10-minute job `onboarding-checklist-sweep` (orgs whose checklist is not dismissed and has an open step), and, for customers / vendors / products, the "onboarding" entity sync target right after the save (only while MODULE_INTEGRATION is licensed, since the gateway calls targets only then). A tick is never undone (BR-50-014).

## 2. What a load creates

Contributors run in `Order`. Each one creates its records through its own module's normal create path, so every rule of that path applies. A record that fails is skipped and reported in `notes`; the load goes on.

| Order | Contributor | Requires | Creates |
|---|---|---|---|
| 10 | Suppliers `SuppliersSampleDataContributor` | none | 5 customers (company, 30-day terms) and 3 vendors (`DEMO-V01..03`, approved so a PO can be raised). Names are the FSD §7.1 names plus " (Demo)". |
| 20 | Inventory `InventorySampleDataContributor` | MODULE_INVENTORY | 20 products, 24 variants (Office Table and Office Chair have 3 each), with purchase and selling prices, available for retail, POS and MIR. Opening stock as in D-71. |
| 30 | Demand `DemandSampleDataContributor` | MODULE_DEMAND | 2 DRAFT sale orders (SELF_PICKUP, 3 + 2 lines) to the first two demo customers, and 1 DRAFT purchase order (3 lines at the variants' purchase price) to the first demo vendor. |

The Demand contributor reads the customers, vendors and variants the earlier contributors made (`createdSoFar`). Without demo products it creates nothing and says so. Without demo customers it skips the sale orders, and without demo vendors the PO.

## 3. Cleanup

Contributors run in reverse order (orders, then products, then partners).

- **Demand.** A demo SO or PO still in DRAFT is deleted outright, lines included. A soft delete is not enough: the supplier and variant reference checkers also count deleted POs, so the demo vendor and products could never be removed. A demo document someone confirmed, approved or sent is now real work, so it is kept, with a note.
- **Inventory.** A product is kept if any of its variants is on a PO, GRN or MIR line, has a reservation or a stock adjustment, or has any ledger movement other than its demo opening and its write-off. Otherwise, its demo stock is written off (D-71) and the product is soft-deleted through `IInventoryService.SoftDeleteProductAsync`, the only product delete Inventory has.
- **Suppliers.** A customer is kept if a sale order, quotation, inquiry, sales invoice or customer payment uses it. Otherwise it goes through `IBusinessPartnerService.DeleteAsync`. A vendor the supplier reference checks refuse (a PO, RFQ response, invoice, payment or GRN) is kept, with the refusal as its note.

## 4. Decisions D-70..D-74

- **D-70. Provenance types.** Every demo record is a `onboarding.DemoRecords` row: `EntityType` (the table in §1.1), `EntityRef`, `Contributor` (the class's full name). Contributors write the literals themselves, because they cannot reference the Onboarding module. `ProductVariant` rows are kept for the variant badge and for cleanup, but are not counted.
- **D-71. Opening stock only under the opening-stock rules.**
  - Demo opening stock goes through the OPENING_STOCK import target: its own `ValidateAsync`, then `ApplyAsync`. So it needs everything a real opening import needs: FEATURE_OPENING_BALANCE_IMPORT on, the opening balance date set, and an active warehouse.
  - Each product's default variant gets 10–200 units (a fixed formula, so a load is repeatable), at its purchase price, into the first active warehouse by code.
  - Any refusal skips opening stock as a whole, with the refusal as the note.
  - Cleanup cannot delete ledger rows (append-only). It writes the stock off as an OUT `ADJUSTMENT` with reference `DEMO_CLEANUP` in the stock ledger, the master ledger and the product ledger, in one transaction, then soft-deletes the product.
- **D-72. Checklist seeding.**
  - A new organization gets its checklist when it is provisioned; an existing one gets it on first read.
  - Catalog wording and links are refreshed on every seed; ticks are the organization's.
  - REV50-4: a checklist created for a LEGACY organization starts dismissed. Its steps still exist and still tick. A checklist that already exists is never dismissed by a later seed.
- **D-73. Demo records never tick a step.**
  - Signals get the demo ids and exclude them.
  - During a load, `SampleDataScope.IsLoading` (an AsyncLocal) is set. While it is set, the entity sync target and `IOnboardingProgress` do nothing, because a demo record is saved before its DemoRecords row exists.
  - A contributor reports a skipped record as a `Note` ref, which is shown and never stored.
- **D-74. New organizations only.** Loading sample data is refused (409) for a LEGACY organization: demo records would mix with its own data.

Unit tests:
- `SuppliersOnboardingTests`, `InventoryOnboardingTests`, `DemandOnboardingTests`: load counts, a failure is skipped with a note, cleanup keeps referenced records, signals exclude demo records.
- `ChecklistLegacyTests` (D-72): legacy orgs get a dismissed checklist.
- `ControllerActivationTests`: every onboarding controller has a public constructor.