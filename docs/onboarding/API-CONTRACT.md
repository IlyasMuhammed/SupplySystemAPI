# Addendum 50 — contract

Decisions are in [ADDENDUM-50-ANALYSIS.md](ADDENDUM-50-ANALYSIS.md). Seams are already written in `src/SMS.Shared/Onboarding/OnboardingContracts.cs`: `ISetupCategorySeeder`, `ISampleDataContributor`, `IOnboardingSignal`, `IOnboardingProgress`, `IDocumentBrandingProvider` and `IEmailTemplateRenderer`, plus Null defaults.

An owner may refine any part of this contract. Record the change as a D-line and write the exact JSON under **Details**, so FE can code against it.

## 1. Onboarding module (CORE): `src/SMS.Modules.Onboarding`, schema `onboarding`

CORE creates **all** entities and the **only** Onboarding migration(s). Other agents add services and controllers inside the module, in their own folders, and ask CORE for any column.

| Entity | Key fields |
|---|---|
| `SetupDefault` (no org) | Id, Uuid, CountryCode(10), IndustryType(50), DataCategory(40), DisplayName, DataPayload nvarchar(max), Version int, IsActive, Notes, Created/Modified At/By, RowVersion; UQ (CountryCode, IndustryType, DataCategory) |
| `TenantSetupLog` | Id, OrganizationId, CountryCode, IndustryType, DataCategory, DefaultsVersion, RecordsSeeded, Action (Seed/Merge/Overwrite/FullReset/Skipped/Failed), Message, AppliedAt, AppliedBy |
| `OrganizationSetup` (PK OrganizationId) | CountryCode, IndustryType, SetupStatus (NotStarted/InProgress/Completed/Skipped, CHECK), SetupStep int, StepDataJson, DefaultsSnapshotJson, CompletedAt, CompletedBy, Note, RowVersion |
| `SeededRecord` | Id, OrganizationId, Category, NaturalKey, EntityRef, Version, Fingerprint, Reused, SeededAt; UQ (OrganizationId, Category, NaturalKey) |
| `OnboardingChecklist` (PK OrganizationId) + `OnboardingChecklistStep` | Dismissed, DismissedAt/By; step: StepCode, Title, Link, RequiresModule, SortOrder, CompletedAt, CompletedBy, AutoCompleted; UQ (OrganizationId, StepCode) |
| `DocumentTemplate` | Id, Uuid, OrganizationId, TemplateType (CHECK, the 6 types), Name, HeaderHtml, BodyHtml, FooterHtml, LogoAttachmentUuid, PrimaryColor(7), ShowBankDetails, BankDetailsText, TermsAndConditions, IsDefault, IsSeeded, IsActive, audit; filtered UQ one default per (org, type) |
| `EmailTemplate` | Id, Uuid, OrganizationId, TemplateType, Name, SubjectTemplate, BodyHtml, IsDefault, IsSeeded, IsActive, audit; filtered UQ one default per (org, type) |
| `DemoRecord` | Id, OrganizationId, EntityType(50), EntityRef(100), Contributor, CreatedAt; UQ (OrganizationId, EntityType, EntityRef) |

Every org-scoped entity implements `ITenantScopedEntity`. The migration backfills `OrganizationSetups` for every existing org as Completed, with note LEGACY (D-6).

## 2. Seeders: who implements `ISetupCategorySeeder`

| Category | Owner agent | Module and location |
|---|---|---|
| CURRENCY | SEED-FIN | Finance `Onboarding/CurrencySetupSeeder.cs` (+ Tenancy currency settings through the existing Shared seam or service) |
| COA (+ account roles) | SEED-FIN | Accounting `Onboarding/CoaSetupSeeder.cs` + new merge apply in ChartOfAccountsService |
| TAX | SEED-FIN | Finance `Onboarding/TaxSetupSeeder.cs` |
| FISCAL | SEED-FIN | Accounting `Onboarding/FiscalSetupSeeder.cs` |
| PAYMENT_TERMS | SEED-FIN | Finance (EarlyPaymentTerm) + global lookups.PaymentTerms insert-if-missing |
| UOM | SEED-OPS | Lookups `Onboarding/UomSetupSeeder.cs` |
| PRODUCT_CATEGORIES | SEED-OPS | Inventory `Onboarding/CategorySetupSeeder.cs` |
| WAREHOUSE | SEED-OPS | Inventory `Onboarding/WarehouseSetupSeeder.cs` |
| DOC_NUMBERING | SEED-OPS | Logistics `Onboarding/NumberingSetupSeeder.cs` + `document_number_formats` table + generator mapping |
| APPROVAL_RULES | SEED-OPS | WorkflowEngine `Onboarding/ApprovalSetupSeeder.cs` |
| ROLES | SEED-OPS | Auth `Onboarding/RoleSetupSeeder.cs` + `InviteUserAsync` (D-22) |
| HRM | HRM-50 | Hrm `Onboarding/HrmSetupSeeder.cs` (D-90; MODULE_HRM; PK_GLOBAL pack only) |
| DOC_TEMPLATES / EMAIL_TEMPLATES | TPL | Onboarding `Templates/*` seeders (own tables) |

Each module registers its seeder as `AddScoped<ISetupCategorySeeder, X>()`; Onboarding enumerates them. The **payload JSON shapes** per category are FSD §5. DATA writes the authoritative shapes into `docs/onboarding/PAYLOAD-SCHEMAS.md` and `src/SMS.Modules.Onboarding/Packs/schemas/*.json` **first**. Seeders read those shapes. Where a seeder needs an extra field, coordinate with DATA.

## 3. Packs (DATA)

- **Files:** `src/SMS.Modules.Onboarding/Packs/{CC}_{INDUSTRY}/{CATEGORY}.json`, embedded. Packs: PK_Trading, AE_Trading, SA_Trading, GLOBAL_GLOBAL, plus country-level `PK_GLOBAL` / `AE_GLOBAL` / `SA_GLOBAL` where a category is country-only (TAX, FISCAL, CURRENCY).
- **COA format:** the COA payload uses the A42 items format. AE and SA COA are also written as `src/SMS.Modules.Accounting/Templates/AE_GENERAL.json` / `SA_GENERAL.json` items files; set `itemsFile` in templates.json. Coordinate with SEED-FIN, who owns Accounting.
- **Startup seeder (CORE):** inserts a pack row only when (country, industry, category) is missing (D-4).

## 4. Endpoints

### Org admin (SETUP_WIZARD_RUN unless noted)

| Method + route | Owner |
|---|---|
| `GET api/onboarding/setup/countries`, `GET …/industries?country=` | CORE |
| `POST …/setup/preview { countryCode, industryType, categories? }` → per category `{ category, version, resolvedFrom: {country, industry}, payload, preview }` | CORE |
| `POST …/setup/apply { countryCode, industryType, payloadOverrides: {category: payload}, wizardInputs: {category: json}, skipCategories: [], invites: [{email, firstName, lastName, roleRef}] }` → per category result | CORE |
| `POST …/setup/skip` | CORE |
| `GET …/setup/state`, `POST …/setup/state/{step} { data }` | CORE |
| `GET …/setup/categories` (dashboard: seeded / current / modified / last applied / version) | CORE |
| `POST …/setup/categories/{category}/reset { mode, confirm }` | CORE |
| `GET api/onboarding/seeded?category=` | CORE |
| `GET/POST api/onboarding/checklist`, `POST …/checklist/steps/{code}/complete`, `POST …/checklist/dismiss` (ONBOARDING_VIEW) | DEMO |
| `GET api/onboarding/sample-data` (plan + has-demo), `POST …/load`, `DELETE …/sample-data`, `GET …/demo/refs?entityType=` (SAMPLE_DATA_MANAGE; refs: any signed-in user) | DEMO |
| `GET/PUT/POST api/onboarding/document-templates[/{uuid}]`, `POST …/{uuid}/set-default`, `POST …/reset/{type}`, `POST …/{uuid}/preview` (PDF), `GET …/placeholders/{type}` (DOC_TEMPLATE_MANAGE) | TPL |
| `GET/PUT/POST api/onboarding/email-templates[/{uuid}]`, `POST …/{uuid}/send-test { toEmail }`, `GET …/placeholders/{type}` (EMAIL_TEMPLATE_MANAGE) | TPL |
| `GET api/sale-quotations/{uuid}/pdf`, `GET api/customer-payments/{uuid}/pdf`, `POST api/sales-invoices/{uuid}/email`, `POST api/sale-quotations/{uuid}/email`, `POST api/purchase-orders/{uuid}/email` `{ to?, cc?, message? }` (existing view/send permissions of each document) | TPL |
| `POST api/hrm/payslips/{uuid}/email` (HRM_PAYROLL_VIEW in scope, or HRM_SELF_SERVICE on one's own), `POST api/hrm/payroll-runs/{uuid}/payslips/email { resend? }` (HRM_PAYSLIP_EMAIL + all-employee scope, D-98) — the PayslipSent email (D-93; docs/hrm/API-CONTRACT.md §9.1) | HRM-50 |
| Import target `GL_OPENING_BALANCE` (A40 routes) | SEED-FIN |

### Super admin (PLATFORM_SUPER_ADMIN): `api/system/setup-defaults`

| Method + route | Owner |
|---|---|
| `GET ?country&industry&category`, `GET {uuid}`, `POST`, `PUT {uuid}`, `DELETE {uuid}` (409 if applied → deactivate), `POST import` (pack JSON), `GET export`, `POST {uuid}/publish`, `POST {uuid}/unpublish` | CORE |

## 5. File ownership

| Agent | Owns |
|---|---|
| CORE | `src/SMS.Modules.Onboarding/**` except `Templates/**`, `SampleData/**`, `Checklist/**` and `Packs/**`; `src/SMS.API/Program.cs` (the only agent allowed to edit it); TenancyFeatureCatalog entry for MODULE_ONBOARDING; Auth permission codes + grants for D-8 (coordinate with SEED-OPS, who owns the rest of Auth) |
| SEED-FIN | `SMS.Modules.Accounting/**` (Onboarding/, merge apply, GL opening import target, AE/SA template items) and `SMS.Modules.Finance/**` (Onboarding/). A49's REV-49 may also fix bugs in Accounting/Finance; avoid HR files (`*Hr*`, Payroll) |
| SEED-OPS | Onboarding/ folders + needed changes in `SMS.Modules.Lookups`, `SMS.Modules.Inventory`, `SMS.Modules.Logistics` (numbering), `SMS.WorkflowEngine`, `SMS.Modules.Auth` (roles seeder, InviteUserAsync, welcome hook point) |
| DATA | `src/SMS.Modules.Onboarding/Packs/**`, `docs/onboarding/PAYLOAD-SCHEMAS.md`; the AE/SA items files in Accounting Templates (tell SEED-FIN) |
| TPL | `src/SMS.Modules.Onboarding/Templates/**`; branding/HTML-render changes in existing renderers (Finance SalesInvoiceDocumentService, StatementPdfRenderer, Demand PoDocumentService + SaleQuotation PDF, Logistics DeliveryDocumentService, Finance customer payment receipt PDF); email actions in Finance/Demand controllers; Lookups PoDocumentTemplate copy |
| DEMO | `src/SMS.Modules.Onboarding/SampleData/**` + `Checklist/**`; `ISampleDataContributor` / `IOnboardingSignal` implementations in Suppliers, Inventory, Demand, Integration (QB signal), Auth (users signal) — **one new file each**, plus a DI line in each module |
| FE-A | Angular: setup wizard, default data dashboard, super admin Setup Defaults, first-login guard, banner |
| FE-B | Angular: Settings › Organization menu group, document/email template editors, sample data manager, checklist widget, demo badge, email/PDF buttons on invoice/quotation/PO/payment |
| QA | `tests/SMS.Integration.Tests/Onboarding/**` |

Unit tests live in each module's test project, written by the owner of that code.

## Details (appended by owners)

### D-CORE — wizard, default data, super admin (CORE-50)

Every response uses the ApiResponse envelope `{ success, message, result }`: camelCase JSON, enums as strings. Errors:
- 400 = validation (`message` lists the problems);
- 403 = permission;
- 404 = unknown uuid / category;
- 409 = conflict (delete guard, a Full reset blocked by references, a stale rowVersion).

`payload` / `dataPayload` is always a **JSON object**, never a string.

**Categories** are `SetupCategories.ApplyOrder`: CURRENCY, COA, TAX, FISCAL, PAYMENT_TERMS, UOM, PRODUCT_CATEGORIES, DOC_NUMBERING, APPROVAL_RULES, ROLES, WAREHOUSE, DOC_TEMPLATES, EMAIL_TEMPLATES. A category with no registered seeder appears with `hasSeeder:false` and is never applied.

#### Org admin — `api/onboarding/setup/*` (SETUP_WIZARD_RUN unless noted)

`GET api/onboarding/setup/status`: **any signed-in user**. This is the banner / first-login guard (D-7). `result` is null when the caller has no organization (a super admin outside one).
```json
{ "setupStatus": "NotStarted", "setupStep": 0, "countryCode": null, "industryType": null, "completedAt": null, "canRunWizard": true }
```
`canRunWizard` = the caller holds SETUP_WIZARD_RUN.

`GET api/onboarding/setup/countries`: the countries that have at least one active default, plus GLOBAL, which is always present and listed last.
```json
[ { "code": "PK", "name": "Pakistan", "defaultsCount": 16 }, { "code": "GLOBAL", "name": "Other / Global", "defaultsCount": 13 } ]
```

`GET api/onboarding/setup/industries?country=PK`: the industries with an active default for that country or for GLOBAL. GLOBAL is always present.
```json
[ { "code": "Trading", "name": "Trading" }, { "code": "GLOBAL", "name": "General (any industry)" } ]
```

`POST api/onboarding/setup/preview`
```json
{ "countryCode": "PK", "industryType": "Trading", "categories": ["COA","TAX"], "payloadOverrides": { "TAX": { } } }
```
`categories` null = all. `payloadOverrides` is optional: it previews the admin's edited payload.

The response is an array in apply order:
```json
[ {
  "category": "TAX", "displayName": "PK GLOBAL — TAX", "status": "Ready",
  "setupDefaultUuid": "…", "version": 1,
  "resolvedFrom": { "country": "PK", "industry": "GLOBAL" },
  "payload": { "verified": false, "taxCodes": [ … ] },
  "verified": false,
  "preview": { "category": "TAX", "inPayload": 6, "alreadyPresent": 0, "warnings": [] },
  "requiresModule": "MODULE_FINANCE", "moduleEnabled": true, "hasSeeder": true,
  "errors": []
} ]
```
- `status`: `Ready` | `ModuleDisabled` | `NoDefaults` | `NoSeeder` | `Invalid`. `Invalid` means the override failed validation, and the problems are listed in `errors`.
- `resolvedFrom` / `payload` / `version` / `setupDefaultUuid` are null when the status is `NoDefaults`.
- `verified` is the payload's top-level `verified` boolean, or null when the payload has none. FE shows "check rates with your tax adviser" when it is false.

`POST api/onboarding/setup/apply`
```json
{
  "countryCode": "PK", "industryType": "Trading",
  "payloadOverrides": { "COA": { … edited tree … } },
  "wizardInputs":     { "WAREHOUSE": { "warehouses": [ { "code": "WH01", "name": "Main", "address": "…", "zones": ["RECEIVING","STORAGE"] } ] } },
  "skipCategories":   ["ROLES"],
  "invites":          [ { "email": "a@b.com", "firstName": "Ali", "lastName": null, "roleRef": "SALES_MANAGER" } ]
}
```
- The wizardInputs shapes are the seeders' own; see PAYLOAD-SCHEMAS.md.
- `roleRef` is a RoleID (digits) or a ROLES pack role code that the apply seeded or reused.
- Response:
```json
{
  "setupStatus": "Completed", "completed": true,
  "results": [ { "category": "CURRENCY", "status": "Applied", "action": "Seed", "ok": true, "created": 6, "skipped": 0, "removed": 0,
                 "version": 1, "warnings": [], "message": null } ],
  "invites": [ { "email": "a@b.com", "outcome": "INVITED", "userId": 42, "message": null } ]
}
```
- result `status` values:
  - `Applied`;
  - `Skipped` (in skipCategories);
  - `ModuleDisabled`;
  - `NoDefaults`;
  - `NoSeeder`;
  - `Invalid` (the override failed validation, nothing applied);
  - `Failed`, with `message` set. **Processing stops at the first Failed or Invalid**; later categories come back `NotRun`.
- `ok` = Applied | Skipped | ModuleDisabled | NoDefaults | NoSeeder.
- `action` is the TenantSetupLog action written: Seed | Skipped | Failed, or null.
- `setupStatus` becomes Completed only when every row is ok. Otherwise it stays InProgress. A re-run is idempotent and continues.
- Invite `outcome` values: `INVITED` | `EXISTS` | `INVALID` | `FAILED`. `FAILED` means an unexpected error or an unresolved roleRef. Invites run only after the categories succeed.

`POST api/onboarding/setup/skip` (no body): status → Skipped, applying GLOBAL+GLOBAL for every category (BR-50-003). The response has the apply shape, with `setupStatus: "Skipped"`. Refused with 400 when the setup is already Completed.

`GET api/onboarding/setup/state`
```json
{ "setupStatus": "InProgress", "setupStep": 3, "countryCode": "PK", "industryType": "Trading",
  "stepData": { "1": { … }, "3": { … } }, "completedAt": null, "completedBy": null, "note": null, "lastAppliedAt": null }
```
`POST api/onboarding/setup/state/{step}` (step 1..20). Body:
```json
{ "data": { … any JSON … }, "countryCode": "PK", "industryType": "Trading" }
```
- `countryCode` / `industryType` are optional; when present they are stored on the setup.
- It stores `stepData[step] = data` and sets `setupStep = step`. NotStarted moves to InProgress.
- It returns the state.

`GET api/onboarding/setup/categories`: the Default Data dashboard.
```json
{ "countryCode": "PK", "industryType": "Trading", "setupStatus": "Completed", "completedAt": "…",
  "categories": [ {
    "category": "TAX", "displayName": "PK GLOBAL — TAX", "requiresModule": "MODULE_FINANCE", "moduleEnabled": true, "hasSeeder": true,
    "seededCount": 6, "currentCount": 7, "modifiedCount": 1, "missingCount": 0, "modifiedKeys": ["GST18"], "missingKeys": [],
    "isModified": true, "lastAppliedAt": "…", "lastAction": "Seed", "appliedVersion": 1, "latestVersion": 2, "updateAvailable": true
  } ] }
```
- `appliedVersion` = the version of the last log row.
- `latestVersion` = the version the org's country / industry resolves to now. Null when nothing resolves.

`POST api/onboarding/setup/categories/{category}/reset`
```json
{ "mode": "Merge", "confirm": true, "source": "applied" }
```
- `mode`: Merge | Overwrite | Full.
- `confirm` must be true for Overwrite and Full.
- `source`: `applied` (default) = the payload the org applied, taken from its snapshot, falling back to the current resolution. `latest` = what resolves today.
- Returns one result row, as in apply, with `action` Merge | Overwrite | FullReset.
- A Full reset blocked by references (BR-50-008) → **409**: `{ "success": false, "message": "…", "result": { "referencedCount": 2, "referencedKeys": ["GST18","INPUT18"] } }`.

`GET api/onboarding/seeded?category=TAX`: provenance (D-5), for the optional "Seeded" badge. `category` is required.
```json
[ { "naturalKey": "GST18", "entityRef": "…uuid or id…", "version": 1, "reused": false, "seededAt": "…", "resetCount": 0 } ]
```

#### Super admin — `api/system/setup-defaults` (PLATFORM_SUPER_ADMIN + `is_super_admin`)

Row, as returned by list. The list omits `dataPayload`:
```json
{ "uuid": "…", "countryCode": "PK", "industryType": "Trading", "dataCategory": "COA", "displayName": "PK Trading — COA",
  "version": 3, "isActive": true, "notes": null, "isSystemPack": true, "recordCount": 110, "appliedCount": 4,
  "createdAt": "…", "createdBy": 1, "modifiedAt": "…", "modifiedBy": 1, "rowVersion": "AAAAAAAAB9E=" }
```
- `recordCount` = the items in the payload's top-level arrays, counting nested `children` too.
- `appliedCount` = the distinct orgs whose TenantSetupLog references this row.
- `GET {uuid}` returns the row plus `"dataPayload": { … }`.

Routes:
- `GET api/system/setup-defaults?country=&industry=&category=&active=`: the list, ordered country → industry → apply order.
- `GET api/system/setup-defaults/meta`:
  ```json
  { "categories": [ { "code": "COA", "hasSeeder": true, "hasSchema": true, "requiresModule": "FEATURE_GENERAL_LEDGER" } ], "countries": ["AE","GLOBAL","PK","SA"], "industries": ["GLOBAL","Trading"] }
  ```
- `GET api/system/setup-defaults/resolve?country=PK&industry=Trading&category=TAX`: the row that the fallback chain picks, plus `"resolvedFrom": { "country", "industry" }`. 404 when nothing resolves.
- `POST api/system/setup-defaults`:
  ```json
  { "countryCode": "AE", "industryType": "Trading", "dataCategory": "TAX", "displayName": "…", "notes": null, "isActive": true, "dataPayload": { … } }
  ```
  - Returns the row with its payload, at version 1.
  - A duplicate key → 409.
  - Validation errors → 400: a bad country (`GLOBAL` or 2 letters), an industry outside `^[A-Za-z0-9_]{1,50}$` or literally GLOBAL, an unknown category, or a payload failing its schema or the seeder's ValidatePayload.
- `PUT api/system/setup-defaults/{uuid}`:
  ```json
  { "displayName": "…", "notes": null, "isActive": true, "dataPayload": { … }, "rowVersion": "AAAAAAAAB9E=" }
  ```
  - The key is immutable.
  - `Version` +1 only when the payload changes. Comparison is on canonical JSON: whitespace and order are ignored.
  - `rowVersion` is optional; a stale one → 409.
- `DELETE api/system/setup-defaults/{uuid}` → 409 while any TenantSetupLog row references it ("deactivate it instead"), and always for a built-in pack row (`isSystemPack:true`; the startup seeder would restore it — REV50-2).
- `POST api/system/setup-defaults/{uuid}/publish` / `…/unpublish` → the row, with IsActive true / false. Inactive rows never resolve.
- `POST api/system/setup-defaults/validate` with `{ "dataCategory": "TAX", "dataPayload": { … } }` → `{ "valid": false, "errors": ["$.taxCodes[0].rate: …"] }`.
- `POST api/system/setup-defaults/import?dryRun=false&mode=upsert`:
  - The body is the pack file below (JSON). `mode`: `upsert` (default; an existing key is updated, and its version is bumped if the payload differs) | `insertOnly` (an existing key is left alone).
  - **All-or-nothing:** any invalid item means nothing is saved and the response is 400 with the same result body.
  ```json
  { "dryRun": false, "created": 2, "updated": 1, "unchanged": 10, "errors": [ { "index": 3, "key": "AE/Trading/TAX", "errors": ["…"] } ],
    "items": [ { "key": "AE/Trading/TAX", "action": "Created" } ] }
  ```
  Item `action` values: Created | Updated | Unchanged | Invalid.
- `GET api/system/setup-defaults/export?country=&industry=&category=` → a `application/json` file download (`setup-defaults-YYYYMMDD.json`) in the pack file format:
  ```json
  { "format": "sms-setup-defaults", "version": 1, "exportedAt": "…",
    "defaults": [ { "countryCode": "PK", "industryType": "Trading", "dataCategory": "COA", "displayName": "…", "notes": null, "isActive": true, "version": 3, "payload": { … } } ] }
  ```
  On import, `version` in an item is ignored. Import also accepts a bare array of items.

### D-DEMO: sample data and checklist (DEMO-50)

The exact JSON is in [SAMPLE-DATA.md section 1](SAMPLE-DATA.md#1-details-the-exact-json-for-fe-b).
- entityType values: `Customer`, `Vendor`, `Product` (the product uuid), `ProductVariant`, `SaleOrder`, `PurchaseOrder`, `OpeningStock`.
- `GET checklist` and `POST checklist` both evaluate the signals and return the status.
- Each step's `link` is a full `/portal/pages/...` route.


### D-TPL — document templates, email templates, document PDFs and emails (TPL-50)

Envelope, casing and error codes are as in D-CORE. Every `api/onboarding/document-templates*` route needs **DOC_TEMPLATE_MANAGE**, and every `api/onboarding/email-templates*` route needs **EMAIL_TEMPLATE_MANAGE**. Types are the `DocumentTemplateTypes` / `EmailTemplateTypes` strings. An unknown type gets a 400.

#### Document templates: `api/onboarding/document-templates`

Model (`DocumentTemplateModel`), returned by list, get, create, update, set-default and reset:
```json
{ "uuid": "…", "templateType": "SalesInvoice", "name": "Standard Sales Invoice",
  "headerHtml": "<p><strong>{CompanyName}</strong></p>", "bodyHtml": null, "footerHtml": "<p>Thank you for your business</p>",
  "logoAttachmentUuid": null, "logoUrl": null, "primaryColor": "#4A42CC",
  "showBankDetails": true, "bankDetailsText": "HBL …\nIBAN …", "termsAndConditions": "Goods once sold …",
  "isDefault": true, "isSeeded": true, "isActive": true, "createdAt": "…", "modifiedAt": "…" }
```
- `logoUrl` is the logo attachment's FileUrl (for an `<img>`), or null.
- `bodyHtml` null or empty means the built-in layout, with this template's branding applied. Non-empty means the generic HTML body with placeholders (D-25).

Routes:
- `GET ?type=SalesInvoice&includeInactive=false`: the list, ordered type → default first → name. `type` is optional.
  - On the first list call, an org with a saved pre-A50 PoDocumentTemplate and no PurchaseOrder template gets a default "Purchase Order (from PO template)" copy. It has no body, header, footer or colour, so its PO PDFs do not change until the admin edits it (D-69, REV50-12).
- `GET {uuid}`: one template.
- `POST`: create a custom template. It is never the default unless `isDefault: true` is sent. Returns the model.
  ```json
  { "templateType": "SalesInvoice", "name": "Copy of Standard Sales Invoice", "cloneFromUuid": null,
    "headerHtml": null, "bodyHtml": null, "footerHtml": null, "logoAttachmentUuid": null, "primaryColor": "#4A42CC",
    "showBankDetails": true, "bankDetailsText": null, "termsAndConditions": null, "isDefault": false }
  ```
  - `cloneFromUuid` set: the clone's fields are copied first, then any non-null field in the body overrides them. The type must match.
  - Not set: blank, from the body's fields.
- `PUT {uuid}`: all fields are written as sent (null clears). Returns the model.
  ```json
  { "name": "…", "headerHtml": "…", "bodyHtml": "…", "footerHtml": "…", "logoAttachmentUuid": "…", "primaryColor": "#1F6FEB",
    "showBankDetails": true, "bankDetailsText": "…", "termsAndConditions": "…", "isActive": true }
  ```
  - Validation (400): name required (max 200); `primaryColor` must match `^#[0-9A-Fa-f]{6}$` or be null; `bankDetailsText` max 2000; `termsAndConditions` max 4000; each HTML field max 100,000.
  - A logo uuid must be an attachment with interface code `DOC_TEMPLATE_LOGO`.
  - The default template cannot be deactivated (400).
- `POST {uuid}/set-default`: makes this the type's only default, and the others of the type lose it, in one transaction. Inactive templates are refused (400). Returns the model.
- `POST reset/{type}`: restores the seeded default of the type from the org's DOC_TEMPLATES pack (the org's country/industry chain, falling back to GLOBAL+GLOBAL).
  - The seeded row, the one with `isSeeded` true, gets the pack's values back and becomes the default. Custom templates are kept but are no longer the default.
  - If no seeded row exists, one is created. Returns the model.
- `POST {uuid}/preview`: renders a sample document of the template's type with sample data and returns `application/pdf` (a file, not an envelope).
  - Body optional. If you send the PUT body, its unsaved values are previewed; nothing is saved.
  - `logoAttachmentUuid` in the body is honoured.
- `GET placeholders/{type}`:
  ```json
  [ { "token": "{InvoiceNumber}", "label": "Invoice number", "group": "Document", "description": "…", "structural": false } ]
  ```
  `structural: true` marks a block token that is placed on a line of its own: `{LineItems}`, `{TaxSummary}`, `{Totals}`, `{BankDetails}`, `{TermsAndConditions}`, `{SignatureBlock}`. The full list is in TEMPLATE-PLACEHOLDERS.md.

**Logo:** upload through the existing `POST api/attachments/upload` with `interfaceCode = DOC_TEMPLATE_LOGO` (pictures only; DOC_TEMPLATE_MANAGE for view, upload and delete) and `documentId = <template uuid>`. Then PUT the attachment's uuid in `logoAttachmentUuid`.

#### Email templates: `api/onboarding/email-templates`

Model (`EmailTemplateModel`):
```json
{ "uuid": "…", "templateType": "InvoiceSent", "name": "Invoice to customer", "subjectTemplate": "Invoice {InvoiceNumber} from {CompanyName}",
  "bodyHtml": "<p>Dear {CustomerName},</p>…", "isDefault": true, "isSeeded": true, "isActive": true, "createdAt": "…", "modifiedAt": "…" }
```
Routes:
- `GET ?type=&includeInactive=false`: the list.
- `GET {uuid}`: one template.
- `POST { templateType, name, subjectTemplate?, bodyHtml?, cloneFromUuid?, isDefault? }`: returns the model. Without a clone, subject and body are required.
- `PUT {uuid} { name, subjectTemplate, bodyHtml, isActive }`: returns the model. Subject max 300, with no line breaks. Body max 100,000.
- `POST {uuid}/set-default`: **exists**, with the same rules as for documents.
- `POST reset/{type}`: **exists**, with the same rules as for documents (from the org's EMAIL_TEMPLATES pack).
- `POST {uuid}/preview { placeholders?: { "CustomerName": "…" } }` → `{ "subject": "…", "bodyHtml": "…" }`. Sample values fill any token that is not given.
- `POST {uuid}/send-test { "toEmail": "me@x.com" }`: renders with sample values and sends via `IEmailSender.SendWithResultAsync`. The subject is prefixed `[TEST] `.
  - Response `{ "sent": true, "toEmail": "me@x.com", "subject": "[TEST] …", "error": null }`.
  - A failed send is still 200, with `sent:false` and `error` set. An invalid address gets a 400.
- `GET placeholders/{type}`: the same shape as for documents (`structural` is always false).

#### Document PDFs and emails

| Route | Permission (any of) |
|---|---|
| `GET api/sale-quotations/{uuid}/pdf` | SALE_QUOTATION_VIEW |
| `GET api/customer-payments/{uuid}/pdf` (receipt) | CUSTOMER_PAYMENT_VIEW |
| `POST api/sales-invoices/{uuid}/email` | SALES_INVOICE_MANAGE (a DRAFT or CANCELLED invoice → 400) |
| `POST api/sale-quotations/{uuid}/email` | SALE_QUOTATION_SEND |
| `POST api/purchase-orders/{uuid}/email` | PO_CREATE, PO_EDIT, PO_APPROVE |

Both PDFs return `application/pdf` files named `{number}.pdf`.

Email request (all fields optional):
```json
{ "to": "buyer@acme.com; ap@acme.com", "cc": "me@us.com", "message": "Please find attached …" }
```
- `to` / `cc` are separated by `,` or `;`. With `to` empty, the server uses the customer's or supplier's email (SupplierContacts lookup). If none is found → 400 "No recipient: enter an email address".
- `message` fills `{Message}`. It is HTML-encoded, with its line breaks kept.
- The subject and body come from the org's default template of the type (InvoiceSent / QuotationSent / POToSupplier). Without one, built-in text is used.
- The PDF is attached. IEmailSender has no CC, so each cc address receives its own copy.

Response:
```json
{ "sent": true, "sentTo": ["buyer@acme.com"], "cc": ["me@us.com"], "subject": "Invoice SINV-… from Sunrise", "failed": [ { "email": "x@y", "error": "…" } ] }
```
`sent` = every recipient succeeded. A send failure is still 200, with `failed` listing the addresses and reasons.

FE: leave `to` empty to use the server default. The document models do not always carry the party email, so do not prefill it; show a hint "defaults to the customer's email".

### D-SEED-OPS — UOM, PRODUCT_CATEGORIES, WAREHOUSE, DOC_NUMBERING, APPROVAL_RULES, ROLES, invites (SEED-OPS)

Payloads are as in PAYLOAD-SCHEMAS.md (D-60), read case-insensitively; extra fields are ignored. Details in D-55..D-59.

**WAREHOUSE wizard input** (`wizardInputs.WAREHOUSE`, Step 6). With no input, the pack's `defaultWarehouse` and default zone types are used.
```json
{ "warehouses": [ { "code": "WH-LHR", "name": "Lahore Warehouse", "address": "…", "city": "Lahore", "country": "PK",
                    "zones": ["RECEIVING", "STORAGE"] } ] }
```
- `zones` omitted means the pack's zone types with `default:true`.
- `zones: []` means no zones.
- Unknown zone codes are ignored, with a warning.

**Provenance (SeededRecords) per category**

| Category | NaturalKey | EntityRef | Reused |
|---|---|---|---|
| UOM | `KG` | LookupValue id (guid) | true for a global unit |
| PRODUCT_CATEGORIES | `RAW` or `RAW/STEEL` | `C:{id}` or `S:{id}` | false |
| WAREHOUSE | `WH-MAIN` or `WH-MAIN/RECEIVING` | warehouse uuid or `Z:{zoneId}` | false |
| DOC_NUMBERING | documentType (`PurchaseOrder`) | format id | false |
| APPROVAL_RULES | `PurchaseOrder` | PO definition uuid | false |
| ROLES | role code (`SALES_REP`) | RoleID (int as string) | true for a global role |

The wizard resolves an invite's `roleRef` this way: a number is a RoleID; anything else is the ROLES NaturalKey, whose EntityRef is the RoleID.

**Invites** (Shared `IOrgUserProvisioningService`):
```csharp
Task<OrgUserInviteResult> InviteUserAsync(OrgUserInviteRequest request, CancellationToken ct = default);
record OrgUserInviteRequest(Guid OrganizationId, string Email, string? FirstName, string? LastName, int RoleId, string OrganizationName, int InvitedBy);
record OrgUserInviteResult(string Email, string Outcome /* INVITED | EXISTS | INVALID */, int? UserId, string? Message);
```

**Welcome hook** (Shared `IUserInviteAcceptedHandler`, implemented by TPL): `OnInviteAcceptedAsync(InviteAcceptedContext(OrganizationId, UserId, Email, FirstName, LastName, RoleName, OrganizationName = null, LoginUrl))`. Auth calls it after accept-invite has committed. A failure in the hook never fails the accept.

**Warnings the FE should show as they are** (`preview.warnings` / apply `warnings`):
- inert numbering entries (R-3);
- refused prefixes;
- an edited PO workflow left alone;
- a role not created because its module is off;
- units or categories kept because they are in use.

### D-SEED-FIN: CURRENCY, COA, TAX, FISCAL, PAYMENT_TERMS, GL_OPENING_BALANCE (SEED-FIN)

The payloads follow PAYLOAD-SCHEMAS.md (D-60), with no extra field needed. The details are in D-50..D-54.

**Payload notes:**
- TAX `code` is upper-cased and must match `^[A-Z0-9_-]{1,20}$`. That is the TaxCodeService rule, which is stricter than the schema: a '.' is refused.
- FISCAL also accepts `periodType: "Quarterly"`, which gets monthly periods and a warning.
- PAYMENT_TERMS also accepts `days` in place of `netDays`.

**Provenance per category:**

| Category | NaturalKey | EntityRef | Reused |
|---|---|---|---|
| CURRENCY | `PKR` | lookups currency id (guid) | true when the org already had it |
| COA | account code `1112` | account uuid | true for an existing account with the code |
| TAX | `GST18` | tax code uuid | true for an existing code |
| FISCAL | start date `2026-07-01` | fiscal year uuid | true for an existing year with that start |
| PAYMENT_TERMS | `2_10_30` (discount terms only) | early payment term uuid | true for an existing code |

**Reset and references:** `CheckReferencesAsync` lists everything a Full reset would have to delete, not only seeded rows (REV50-6). For COA, the first key can be A42's refusal text (for example "The chart can only be replaced before the fiscal year is set…"). A Full call that gets past the orchestrator anyway throws 409. For CURRENCY, Full runs as Overwrite.

**GL_OPENING_BALANCE** is an A40 profile; it uses the normal `api/import/*` routes.
- Fields: `account_code` (LOOKUP list `accounts`), `debit`, `credit`, `notes`.
- Target error codes: `LOOKUP_NOT_FOUND`, `INACTIVE`, `NOT_LEAF`, `PLUG_ACCOUNT`, `OUT_OF_RANGE`, `BOTH_SIDES`, `ZERO_AMOUNT`, `ALREADY_POSTED`, `DUPLICATE_IN_FILE`, `DRAFT_EXISTS`, `UNBALANCED`. UNBALANCED is put on every error-free row when those rows do not balance.
- The rows land in the GL opening balance draft. Posting is `POST api/finance/opening-balances/post`.

### D-HRM — the "after A49" round (HRM-50, 2026-11-10)

- **Category `HRM`** (D-90): `GET/POST api/onboarding/setup/*` and the super-admin `api/system/setup-defaults` treat it like any category: it is the 14th of `SetupCategories.ApplyOrder`, `meta` lists `requiresModule: MODULE_HRM`, `preview` answers `ModuleDisabled` while HRM is off and `NoDefaults` outside Pakistan, `apply` / `reset` return the usual per-category result. Payload: PAYLOAD-SCHEMAS.md "HRM". `validate` and the super-admin save refuse `verified: true`.
  - Apply result: `created` counts rows created or restored; rows adopted from HRM's own first-use seed count as `skipped`. Warnings always carry the UNVERIFIED notice when a tax table is in the payload; a reset of a verified table adds `TAX/<code>: left as it is / kept — a person has verified this table`.
- **APPROVAL_RULES** (D-91, D-96): the category has no module of its own any more (`requiresModule: null` in `meta`); the PO rule needs MODULE_DEMAND and the ExpenseClaim rule only MODULE_HRM. A rule whose module is off is skipped with a warning (`PurchaseOrder: skipped — the Demand module is not enabled…`). The finance manager's `HRM_EXPENSE_APPROVE` grant is D-97. the `ExpenseClaim` rule is applied (no longer inert); the result's provenance holds a second natural key `ExpenseClaim` next to `PurchaseOrder`. Warning texts: `ExpenseClaim: skipped — the HRM module is not enabled for this organization.`, `…the approval workflow was edited…`.
- **ROLES** (D-92): `HR_MANAGER`, `HR_OFFICER` in the four ROLES packs; provenance `Reused` for both.
- **Checklist** (D-94): `GET api/onboarding/checklist` lists `HRM_SETUP`, `HRM_ADD_EMPLOYEE`, `HRM_VERIFY_TAX` only while MODULE_HRM is on.
- **Payslip email** (D-93). Request/response and errors are in docs/hrm/API-CONTRACT.md §9.1. Template type `PayslipSent`, placeholders `{EmployeeName} {PayPeriod} {NetPay} {CurrencyCode}` + company placeholders.
