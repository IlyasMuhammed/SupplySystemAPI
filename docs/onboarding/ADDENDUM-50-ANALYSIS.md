# Addendum 50 — Tenant onboarding, default configuration & data import: analysis & decisions

Source: `Documents/SMS_FSD_Addendum_50.md` (SMS-FSD-ADD-050 v1.0) + `Documents/SMS_Task_Register_Addendum_50_Checklist.md` (rows 001–366). Facts: [A50-REALITY-NOTES.md](A50-REALITY-NOTES.md). Contract: [API-CONTRACT.md](API-CONTRACT.md). Status: [ADDENDUM-50-TASKS.md](ADDENDUM-50-TASKS.md).

User, 2026-11-09:
- "complete these tasks in addendum 50, use as many agents as you need";
- then "first complete A49";
- then "start A50 which can be done without requiring A49 completion".

Recommended defaults are adopted, per the standing rule. **The "after A49" round (D-30) was built on 2026-11-10 by HRM-50 once A49 signed off: D-90..D-95 below.**

**Read Part A before writing code.** The FSD assumes empty tables, `SMS.Core`, int `OrgId`, React and an `IsSeeded` column on every table. This repo already has much of A50 (A40 import, A42 CoA templates and the GL wizard, the A37 registry, provisioning handlers). A50 is built as an **orchestrator over each module's own data**, not as a parallel copy.

## Part A — spec vs this repo

| Spec says | Reality here | Consequence |
|---|---|---|
| React, `SMS.Core.Entities`, `core.*` schema, int OrgId | Angular 19 + PrimeNG + sf-* kit; modules `SMS.Modules.*`; Guid OrganizationId (ambient tenant); int Id + Guid Uuid | D-1 |
| Every table empty for a new org | Provisioning handlers already seed currencies (18), dunning, rejection reasons, fulfillment routes, scorecard weights, walk-in customer, CRM stages, default workflows (PR/PO/GRN/MIR); roles are global; GL initializes lazily | Wizard **adds** to what exists, idempotently |
| `core.SetupDefaults` Super Admin store | Nothing similar except A42 `CoaTemplates` (code-seeded, read-only, 3 with accounts) | New store (D-4); its COA payload uses the A42 JSON format (D-12) |
| ALTER Organizations (IndustryType, CountryCode, SetupStatus, SetupStep…) | `tenant.Organizations` owned by Tenancy (Country free text, no industry) | New `onboarding.OrganizationSetups` (D-6). Tenancy is untouched |
| `IsSeeded/SeededAt/SeededVersion` on 8 tables | Those tables live in 6 modules; some are global (payment terms, UoM values), some don't exist (`core.SequenceDefinitions`, `inventory.UnitsOfMeasure`) | One provenance table `onboarding.SeededRecords` (D-5) instead of ALTERs |
| `finance.ChartOfAccounts` seeded from JSON | A42 chart + templates + apply (full replace) + AccountingDefaults (16 role keys) | Accounting gets a **merge** apply for setup (D-12) |
| `finance.TaxCodes`, `finance.Currencies`, `finance.PaymentTerms` | `finance.tax_codes` per org (not seeded); `finance.org_currencies` per org (18 seeded); payment terms are GLOBAL `lookups.PaymentTerms` + A48 per-org `EarlyPaymentTerm` | D-13, D-14, D-15 |
| `inventory.UnitsOfMeasure` with conversion factors | UoM = LookupValue type `uom` (global + org rows); no conversions | D-16; conversions are a residual |
| `inventory.ProductCategories` | Exists per org (Inventory), not seeded | D-17 |
| `core.ApprovalWorkflowRules` | WorkflowEngine definitions per org (PO tiers hard-coded 10k/50k/200k) | D-18 |
| `core.SequenceDefinitions` (doc numbering) | Shared `IDocumentNumberGenerator` (prefix hard-coded by callers); many documents COUNT+1 | D-19 (prefix overrides for documents on the shared generator) |
| `core.Roles` per org | Roles are GLOBAL (18 seeded), grants shared; org custom roles exist via `api/roles` | D-20 |
| Warehouses + zones | Inventory Warehouses → Zones → … ; nothing seeded | D-21 |
| HRM defaults | A49 `HrmSeeder` (PK, versioned) | Built: the Hrm module's own category seeder `HrmSetupSeeder` (D-90) |
| Invite users (Step 7) | Only an org-admin invite exists; org admin adding users gets a temp-password email | D-22 |
| Link QuickBooks in wizard | QBO OAuth is interactive (`api/integrations/quickbooks/connect`) | D-23 |
| P2 Data Import (DataImportJobs, templates, mapping, validation, execute, history) | **A40 already built all of this** (14 targets, XLSX templates, 4-tier auto-map, presets, validate-first, history, background + SignalR) | ✕ duplicate tasks; the only gap is a **GL opening balance import** (D-24) |
| Document templates (HTML header/body/footer, colours, logo) | QuestPDF documents with hard-coded colours; `PoDocumentTemplate` (one per org, PO body HTML via AngleSharp); no quotation / receipt PDF | D-25 |
| Email templates | No template entity; static C# strings; A48 dunning templates use a `{Token}` engine | D-26 |
| Demo data with IsDemo flags on 7 tables | Nothing | D-27 (provenance, no ALTERs) |
| Onboarding checklist, event-driven | No global event bus (MediatR only in WorkflowEngine; module-local publishers) | D-28 (signals polled on read + persisted) |
| `[Authorize(Policy="Org.Admin")]` / `Platform.SuperAdmin` | Permission codes + `[RequirePermission]`; `PLATFORM_SUPER_ADMIN` | D-8 |
| Import file limit 50 MB | A40 10 MB default (12 MB request cap) | D-24 keeps 10 MB |

## Decisions (adopted)

### Conventions, module, flags, permissions
- **D-1 Conventions.**
  - Guid OrganizationId from the tenant context; int Id + Guid Uuid; uuids in routes.
  - ApiResponse envelope, camelCase JSON, enums as strings.
  - **Every migration guarded and idempotent:** API start migrates the shared SMSGlobal.
  - LocalDB / in-memory only; never SMSGlobal; never `dotnet ef database update`.
  - Angular + sf-* kit + --sms-* tokens, phone width.
- **D-2 New module `SMS.Modules.Onboarding`**, schema `onboarding`, own `OnboardingDbContext`, referencing only SMS.Shared.
  - Hosts: SetupDefaults, TenantSetupLog, OrganizationSetups, SeededRecords, OnboardingChecklist(+Steps), DocumentTemplates, EmailTemplates, DemoRecords.
  - It orchestrates. **Data stays in the owning modules**, reached through Shared seams (D-3).
- **D-3 Seeding seam** `ISetupCategorySeeder` (in `SMS.Shared/Onboarding/`), one per category, implemented by the module that owns the data. Shape mirrors `IImportTarget`:
  - `Category` (code);
  - `RequiresModule`;
  - `PreviewAsync(payload)` → counts;
  - `ApplyAsync(payload, ctx)` → seeds missing records by natural key (idempotent, BR-50-004) and returns the seeded keys + per-record fingerprints;
  - `GetStatusAsync(seededKeys)` → current count, modified keys, missing keys;
  - `ResetAsync(payload, mode, seededKeys)` → Merge / Overwrite / Full, with a reference check (BR-50-006/008).
  - Onboarding loops over the registered seeders in a fixed order.
- **D-4 SetupDefaults store** (Super Admin, no OrganizationId): `(CountryCode, IndustryType, DataCategory)` unique, plus `DataPayload` JSON, `DisplayName`, `Version`, `IsActive`, `Notes` and audit fields.
  - **Country codes:** ISO alpha-2 or `GLOBAL`, mapped to alpha-3 where A42 needs it.
  - **Industries:** `GLOBAL`, `Trading` (seeded); the super admin can add more.
  - **Fallback chain** per category: exact → country+GLOBAL → GLOBAL+industry → GLOBAL+GLOBAL.
  - **Version** auto-increments on any payload change.
  - **Delete** is refused while any TenantSetupLog row references the record's current or older version (task 033 → deactivate instead).
  - **Payloads** are validated per category against JSON schemas (`Packs/schemas/*.json`).
  - **Initial packs** come from embedded JSON, inserted by a startup seeder **only when missing**. A super admin's edit is never overwritten, so BR-50-015 holds.
  - **Org isolation:** orgs copy data at apply time, so later edits to a default never reach existing orgs (BR-50-015).
- **D-5 Provenance instead of IsSeeded columns.**
  - `onboarding.SeededRecords`: OrganizationId, Category, NaturalKey, EntityRef (uuid or id string), Version, Fingerprint (hash of the seeded values), SeededAt, plus a ResetCount column.
  - "Modified" = the seeder's current fingerprint ≠ the seeded one. "Custom" = the category's records with no provenance row.
  - Tasks 018–027 (ALTER 8 tables) are ✕ by this decision.
  - FE "Seeded" badge (optional) uses `GET onboarding/seeded?category=` ids.
- **D-6 Org setup state**, in `onboarding.OrganizationSetups` (PK OrganizationId):
  - columns: CountryCode, IndustryType, SetupStatus (NotStarted / InProgress / Completed / Skipped, CHECK), SetupStep, StepDataJson (per-step drafts), DefaultsSnapshotJson (the resolved payloads the admin edited), CompletedAt/By.
  - **New orgs** get `NotStarted` through an `IOrganizationProvisionedHandler`.
  - **Existing orgs** are backfilled by the migration as `Completed` with note `LEGACY`, so no current customer is redirected (Northwind, SCM-DEMO, every SMSGlobal org).
  - The Tenancy `Organization` table is not altered.
- **D-7 Wizard redirect (BR-50-001).** Only for users holding `SETUP_WIZARD_RUN`, when status = NotStarted.
  - FE route guard on `/portal/dashboard` → `/portal/pages/onboarding/setup`.
  - Others see a banner "Setup is not finished — ask your administrator".
  - **Skip** sets Skipped and applies the GLOBAL+GLOBAL defaults (BR-50-003).
- **D-8 Permissions** (new codes, prefix → module MODULE_ONBOARDING in ModuleCodeMap):
  - `SETUP_WIZARD_RUN` (wizard, default data dashboard, resets);
  - `DOC_TEMPLATE_MANAGE`;
  - `EMAIL_TEMPLATE_MANAGE`;
  - `SAMPLE_DATA_MANAGE`;
  - `ONBOARDING_VIEW` (checklist).

  Grants go to ORG_ADMIN and SYSTEM_ADMIN. Super-admin endpoints use `PLATFORM_SUPER_ADMIN`.
- **D-9 Feature.** `MODULE_ONBOARDING` is core / always-on (TenancyFeatureCatalog `IsCore`, `IsAlwaysOn`); no licence step.
- **D-10 Category order and module filter.** The apply order is:

  CURRENCY → COA (+ACCOUNT_ROLES) → TAX → FISCAL → PAYMENT_TERMS → UOM → PRODUCT_CATEGORIES → DOC_NUMBERING → APPROVAL_RULES → ROLES → WAREHOUSE.

  Each seeder declares `RequiresModule`; disabled modules are skipped and logged:
  - COA/FISCAL need FEATURE_GENERAL_LEDGER;
  - TAX needs MODULE_FINANCE;
  - WAREHOUSE needs MODULE_INVENTORY;
  - APPROVAL_RULES needs DEMAND for the PO rules.

  Step 2 (modules) shows the licensed modules. The org admin can switch on or off only what the licence allows, through the existing `api/tenant/modules` enable/disable (A37 rules and impact apply).
- **D-11 Apply.**
  - One request `POST onboarding/setup/apply`, run synchronously, target under 10 s.
  - **One category per transaction**: a failure stops at that category, reports it, and leaves the earlier categories applied.
  - Re-run = idempotent continue.
  - TenantSetupLog gets one row per category (Action Seed / Merge / Overwrite / FullReset / Skipped, RecordsSeeded, DefaultsVersion).
  - SetupStatus becomes Completed only when every selected category is OK.

### Category seeders
- **D-12 COA** (Accounting).
  - **Payload** = the A42 template JSON (nested children, default keys).
  - **Seeding:**
    - When the org's chart is empty and the A42 status allows it, use the existing replace path with an in-memory template.
    - Otherwise use a **new merge apply**: insert accounts missing by code under their parent code; never change or delete existing accounts; assign defaults only where unassigned.
    - ACCOUNT_ROLES (task 048) is part of COA: the default keys assign AccountingDefaults.
  - **Packs:** PK+Trading = the existing PK_GENERAL items (110 accounts; the FSD's "45" becomes the real count, and TS-50-002 asserts the pack's count). GLOBAL = GLOBAL_GENERAL (104).
  - AE+Trading / SA+Trading CoA are **authored by DATA** (~40 accounts each), and also delivered as A42 template items files (AE_GENERAL, SA_GENERAL headers), so the GL wizard gains them too.
  - The wizard's Step 3 edits the payload client-side (tree add / remove / rename) before apply.
- **D-13 TAX** (Finance `finance.tax_codes`): by Code; RatePercent, Usage SALES / PURCHASE / BOTH, IsDefault (one per usage).
  - Tax packs are **Unverified**: payload `"verified": false`, shown in the wizard as "check rates with your tax adviser" (as A49 G-21).
  - **Packs:**
    - PK: GST 18%, FED 16%, WHT goods 4.5%, WHT services 8%, input tax 18%, exempt 0%.
    - AE: VAT 5%, zero-rated, input VAT.
    - SA: VAT 15%, zero-rated, input VAT.
    - GLOBAL: a single "NO TAX 0%".
- **D-14 CURRENCY** (Finance `org_currencies` + Tenancy currency settings).
  - Activates the pack's currencies and deactivates none.
  - Sets the org's sale/purchase/service base to the pack base **only if no documents exist**; otherwise the base is left alone and that is reported (A35 rule).
- **D-15 PAYMENT_TERMS.**
  - Seeds A48 `EarlyPaymentTerm` per org (Code, Name, NetDays, DiscountPercent, DiscountDays): COD/0, NET15, NET30, NET60, 2/10 NET 30.
  - Also ensures matching **global** `lookups.PaymentTerms` rows exist by Name (insert if missing; never update), because partners reference that table.
  - The global rows are not recorded in provenance; reset never deletes them.
- **D-16 UOM** (Lookups org-owned `LookupValue` type `uom`): name "Kilogram (KG)" and so on, with group in the value's metadata if the entity has a free field, else in the name.
  - **Conversion factors are stored in the payload only.** There is no conversion engine (R-1).
  - Global uom values that already exist with the same code are reused, not duplicated.
- **D-17 PRODUCT_CATEGORIES** (Inventory, per org): by Code/Name. The hierarchy maps to ProductCategory → ProductSubCategory (2 levels; deeper levels are flattened, R-2).
- **D-18 APPROVAL_RULES** (WorkflowEngine).
  - PO: updates the org's seeded PO definition tiers (amount thresholds in the pack, in the org's base currency) only if the org has not edited them (fingerprint), else Merge = no-op and report.
  - Expense 2-level = HRM `EXPENSE_CLAIM`: built after A49 (D-91).
- **D-19 DOC_NUMBERING** (Logistics).
  - New `logistics.document_number_formats` (OrganizationId, DefaultPrefix, Prefix, guarded migration).
  - `IDocumentNumberGenerator` maps a caller's default prefix to the org's prefix. **Year and padding are unchanged.**
  - Covers only documents already on the shared generator (PO, DLV, SHP, PCK, HU, manufacturing prefixes, …).
  - Pack entries for COUNT+1 documents (INV, GRN, QT, PV, SO) are stored but inert (R-3).
- **D-20 ROLES** (Auth).
  - For each pack role: if a **global role with that RoleCode exists**, reuse it (no copy) and record provenance as `REUSED`.
  - Otherwise create an **org-owned custom role** (`IsGlobal=false`, OrganizationId) with the pack's permission codes, filtered by modules enabled for the org (task 071).
  - Never edit global roles. Reset deletes only org-owned roles that no user holds.
- **D-21 WAREHOUSE** (Inventory).
  - Warehouses from Step 6 input (code, name, address); for each, the selected zone types become Zones.
  - Pack zones: RECEIVING, STORAGE, DISPATCH, QUALITY_HOLD.
  - Existing warehouses are matched by Code.
- **D-22 Invite users** (Auth).
  - New `IOrgUserProvisioningService.InviteUserAsync(email, firstName?, lastName?, roleId)`: an inactive user with InviteToken (7 days) and an invite email, the same flow as the org-admin invite (`accept-invite`).
  - Existing email → reported, not changed.
- **D-23 QuickBooks in the wizard.** An optional step with a "Connect QuickBooks" button that opens the existing interactive connect flow in a new tab, plus a status read. The wizard never stores Intuit credentials.

### Import, documents, email, demo, checklist
- **D-24 P2 = A40.** Tasks 167–227 are ✕ duplicates, except:
  - **GL opening balance import:** a new `IImportTarget` `GL_OPENING_BALANCE` in Accounting (AccountCode, Debit, Credit, Notes).
    - Validates that the account exists and is a leaf, that a row is not both sides, and that the totals balance (BR-50-011).
    - Applies into the A42 opening-balance draft (the batch PUT path).
    - **Posting stays the existing "Post opening balances" action**, a separate deliberate step.
  - **Menu:** links under Settings › Organization (D-29).
  - **File limit:** stays 10 MB / 50,000 rows (A40); the FSD's 50 MB is not adopted.
- **D-25 Document templates.**
  - **Storage:** `onboarding.DocumentTemplates` per org.
    - Fields: TemplateType (SalesInvoice, Quotation, PurchaseOrder, DeliveryNote, PaymentReceipt, CustomerStatement), Name, HeaderHtml, BodyHtml, FooterHtml, LogoAttachmentUuid (attachment interface code `DOC_TEMPLATE_LOGO`), PrimaryColor, ShowBankDetails, BankDetailsText, TermsAndConditions, IsDefault (one per type), IsSeeded.
  - **Rendering stays QuestPDF.** Shared seam `IDocumentBrandingProvider.GetAsync(type)` gives logo bytes, colour, header/footer text, bank details and T&C. Every existing document renderer reads it in place of its hard-coded colours and letterhead: sales invoice, PO, packing list (delivery note), statement.
  - **BodyHtml with `{Placeholders}`** is rendered through the existing AngleSharp HTML→QuestPDF path (generalised from PO) only where the template has a non-empty BodyHtml. Otherwise the built-in layout is used with branding.
  - **New PDFs:** quotation (`GET api/sale-quotations/{uuid}/pdf`) and customer payment receipt (`GET api/customer-payments/{uuid}/pdf`).
  - **Concurrency:** all renders go through `PdfRenderGate`, which also fixes the existing gap.
  - **PO compatibility:**
    - The org's existing `PoDocumentTemplate` is copied once into the PurchaseOrder DocumentTemplate when the org has none.
    - The PO renderer prefers the DocumentTemplate.
    - The old PO template page stays and edits the same data (it redirects to the new editor for type PurchaseOrder).
  - **Country variants:** the seeded templates take localized currency symbols from the org's currency and an empty bank-details text (task 250).
- **D-26 Email templates.**
  - **Storage:** `onboarding.EmailTemplates` per org (TemplateType, Name, SubjectTemplate, BodyHtml, IsDefault, IsSeeded).
  - **Rendering:** seam `IEmailTemplateRenderer.RenderAsync(type, placeholders)`, using the dunning-style engine with `{Token}`, HTML-encoded values and unknown tokens left as typed. It falls back to the built-in text when the org has no template.
  - **Send test:** via `IEmailSender.SendWithResultAsync`.
  - **Types:** InvoiceSent, QuotationSent, POToSupplier, PaymentReminder, PaymentReceived, WelcomeEmail, PayslipSent (seeded; sent by HRM, D-93).
  - **Wiring now** (new actions):
    - "Email to customer" on the sales invoice and quotation, with the PDF attached;
    - "Email to supplier" on the PO, with the PDF attached;
    - the welcome email on accept-invite.
  - PaymentReminder stays the A48 dunning templates (the dunning engine is not replaced); the seeded email template is used by "Send reminder" only.
- **D-27 Sample data.**
  - **Provenance:** `onboarding.DemoRecords` (EntityType, EntityRef, CreatedAt). There are no IsDemo columns, so tasks 267/275 are ✕ by this decision.
  - **Creation** through module seams `ISampleDataContributor` (Suppliers: 5 customers + 3 vendors; Inventory: 20 products with prices + opening stock where Inventory is on; Demand: 2 SO + 1 PO), each using its own service code paths.
  - **Cleanup** in reverse order.
    - A demo record that real data now references (e.g. a demo customer on a real invoice) is **kept and reported**, never cascaded.
    - A new org only.
    - Refuse to load if demo data already exists.
  - **"Demo" badge:** `GET onboarding/demo/refs?entityType=`.
  - **Demo names (task 271):** the FSD's names, with "(Demo)" appended so they never collide with real records.
- **D-28 Onboarding checklist.**
  - `onboarding.OnboardingChecklists` (per org; Dismissed, DismissedAt) + `OnboardingChecklistSteps` (StepCode, Title, Link, RequiresModule, SortOrder, CompletedAt, CompletedBy, AutoCompleted).
  - **Steps:** COMPLETE_SETUP, ADD_CUSTOMER, ADD_PRODUCT, ADD_SUPPLIER, CREATE_SO, CREATE_PO, CONNECT_QB, INVITE_TEAM, IMPORT_DATA, filtered by modules.
  - **Auto-completion (BR-50-014)** is evaluated by **signals on read and on a 10-minute job**: Shared seam `IOnboardingSignal` per module answers "has any X (non-demo)".
    - A step, once true, is persisted as completed and never unticked.
    - An after-commit customer / vendor / product hook via a new `IEntitySyncTarget` ("onboarding") completes those three instantly.
    - There is no global event bus, so this is a deviation from "event handlers".
- **D-29 Menu.** New group **Settings › Organization**:
  - Setup Wizard;
  - Default Data;
  - Data Import (link to the A40 page);
  - Document Templates;
  - Email Templates;
  - Sample Data.

  Super admin: System Administration › **Setup Defaults**. Dashboard: checklist widget + "Setup incomplete" banner.

### Deferral, docs, tests
- **D-30 After A49** (deferred until A49 signed off; BUILT 2026-11-10, see D-90..D-95):
  - task 058 HRM defaults (leave types, salary components, tax slabs → the A49 HrmSeeder becomes a category seeder);
  - the Expense approval rules;
  - HRM roles in ROLES packs;
  - PayslipSent wiring;
  - HRM onboarding steps.
- **D-31 Docs.** `docs/onboarding/`:
  - this analysis;
  - API-CONTRACT;
  - TASKS;
  - PAYLOAD-SCHEMAS.md (task 362);
  - ADMIN-GUIDE.md (tasks 358/365);
  - SUPER-ADMIN-GUIDE.md (tasks 360/364);
  - TEMPLATE-PLACEHOLDERS.md (task 361);
  - SAMPLE-DATA.md (task 363).

  Swagger already documents the endpoints (task 357 = verify).
- **D-32 Testing.**
  - Unit tests per module.
  - LocalDB HTTP E2E in `tests/SMS.Integration.Tests/Onboarding/`, one class at a time.
  - TS-50-001..025 + perf / security (tasks 325–356).
  - "Malicious file scan" (task 356) = the existing UploadRules type/size/extension checks, tested; no AV engine (R-4).

## Agent decision ranges
- CORE D-40..49
- SEED-FIN D-50..54
- SEED-OPS D-55..59
- DATA D-60..64
- TPL D-65..69
- DEMO D-70..74
- FE-A D-75..79
- FE-B D-80..84
- QA D-85..89
- HRM-50 (the "after A49" round) D-90..D-99 (D-96..D-98 are the user's three follow-up decisions)

Append one line each under "Decisions by agents".

## Decisions by agents
- **D-60 (DATA) Payload shapes.** [PAYLOAD-SCHEMAS.md](PAYLOAD-SCHEMAS.md) and `Packs/schemas/{CATEGORY}.json` (a subset of JSON Schema draft 2020-12) are authoritative.
  - Payloads are camelCase, except COA, which is the A42 template file verbatim (snake_case).
  - Every payload may carry `displayName` and `notes`.
  - TAX adds `usage` (SALES / PURCHASE / BOTH), `verified:false` and `verificationNote`; the FSD's `type` is kept as information only.
  - PAYMENT_TERMS has `code, name, netDays, discountPercent, discountDays`.
  - ROLES uses `code` (the FSD and SEED-OPS field name, the same thing as "roleCode"), with real `PermissionCodes` and `ModuleCodes`.
- **D-61 (DATA) Numbering keeps the existing default prefixes:** SO, PO, SQ (quotation), SINV (sales invoice), GRN, and PAY (supplier payment, the FSD's "PV").
  - `sharedGenerator:false` marks SINV, GRN and PAY: they are stored but inert (R-3).
  - SO and SQ are live on the shared generator.
- **D-62 (DATA) UoM and payment-term names match the existing global lookups,** so the seeders reuse those rows rather than duplicate them.
  - UoM uses `L` / `M`, not the FSD's LTR / MTR.
  - Payment terms are "Net 15", "Net 30", "Net 60", "Cash on Delivery" and "2/10 Net 30".
  - The FSD's unit list is one short of its 16; MM (millimetre) is the 16th.
- **D-63 (DATA) Approval tiers use real roles with no `AUTO` tier,** because the engine has none.
  - Level 1 always applies. Level n>1 is skipped while the value < minAmount.
  - PK (PKR): PROCUREMENT_MANAGER 0 → FINANCE_MANAGER 50,000 → GENERAL_MANAGER 500,000 (the FSD "Director").
  - GLOBAL / AE / SA use the existing seeded tiers: 0 / 10k / 50k / 200k.
  - ExpenseClaim was listed with `afterA49:true` and inert; D-91 makes it live and the packs no longer carry the flag.
  - All 8 pack roles are existing global role codes, so they are reused. INVENTORY_MANAGER is the FSD's "Warehouse Manager".
- **D-64 (DATA) The AE / SA charts have 52 / 53 accounts, not ~38–40.**
  - The A42 4-level structure needs 23 groups (5 roots, 7 L2, 11 L3, including 39/390 for 3900), plus 16 leaves for the 16 default keys.
  - The rest are trading essentials: fixed assets and depreciation, the EOSB provision, corporate tax (AE) or Zakat + GOSI (SA), and freight/customs.
  - The same JSON is both the pack COA and `Accounting/Templates/AE_GENERAL.json` / `SA_GENERAL.json` (version 1.0). SEED-FIN wires `itemsFile`.
  - Every tax pack is `verified:false` (check with a tax adviser).
  - The country-only packs `PK_GLOBAL` / `AE_GLOBAL` / `SA_GLOBAL` hold TAX, FISCAL and CURRENCY.
  - The GLOBAL_GLOBAL CURRENCY pack is a USD base with no additional currencies.
- **D-55 (SEED-OPS) UOM and ROLES seeders.**
  - **UOM** (`Lookups/Onboarding/UomSetupSeeder.cs`): natural key = code; units are "{name} ({code})" (the A40 UNIT rule); a global unit with the code is reused (provenance Reused); the group name goes in `LookupValue.Notes`. Reset never hard-deletes (products keep the code string, which no ILookupReferenceChecker sees): Overwrite/Full **deactivate** seeded units, and a re-apply reactivates rather than duplicates.
  - **ROLES** (`Auth/Onboarding/RoleSetupSeeder.cs`): natural key = role code, EntityRef = RoleID (string). Global code → reused, never edited. Otherwise an org role (IsGlobal=false) is created with the pack permissions that exist, minus SYSTEM_CONFIGURE / PLATFORM_SUPER_ADMIN, minus codes whose ModuleCodeMap module is off (task 071), plus the every-role currency codes. A role whose `requiredModules` has a disabled module is not created. Existing org role with the code = custom, left alone. Full/Overwrite delete only seeded org roles no non-deleted user holds; CheckReferences lists the held ones. RoleID = max(roles, grants, users)+1 (no inherited orphan grants). AuthDataSeeder is not touched by SEED-OPS.
- **D-56 (SEED-OPS) PRODUCT_CATEGORIES and WAREHOUSE seeders** (`Inventory/Onboarding/`).
  - Categories: keys `CODE` and `PARENT/CODE`; a category matches by code, then by name; deeper levels flatten under the top category (R-2). Full/Overwrite delete seeded rows unless products use them, they have a non-seeded sub-category or attribute links — all of which CheckReferences reports (REV50-6).
  - Warehouse: wizard input `{warehouses:[{code,name,address?,city?,country?,zones?}]}`, else the pack's `defaultWarehouse` with the zone types marked `default` (all when none is). Keys `WH-CODE` / `WH-CODE/ZONE`; a zone's Code = zone type code. An existing warehouse (by code) not seeded here is not changed; only explicitly listed zones are added to it. **Reset never deletes a warehouse** (other modules keep warehouse uuids with no visible reference): it is deactivated; a zone is deleted only when no rack, bin or stock row uses it, else deactivated.
- **D-57 (SEED-OPS) DOC_NUMBERING** (`Logistics/Onboarding/NumberingSetupSeeder.cs` + `logistics.document_number_formats`, guarded migration `A50_DocumentNumberFormats`).
  - Row per (org, DocumentType): DefaultPrefix, Prefix, Pattern, IsApplied. `DocumentNumberGenerator.NextAsync` maps a caller's default prefix to the org's (`IsApplied` rows only; per-scope cache); year and 5-digit padding unchanged; the mapped prefix has its own counter, so switching (or resetting back) never reissues a number.
  - Live document types are Logistics' own list (`SharedGeneratorPrefixes`: PO, SO, SQ, INQ, DLV, SHP, PCK, HU, LD, OP, DUN, PR, SVC, SMI, BOM, PROD, SR, PMI, QI, FGR), not the pack flag. Everything else is stored IsApplied=false and reported inert (R-3). SO and SQ are live (D-19's guess was wrong).
  - Refused (validation error, apply = "Not applied"): a prefix equal to another live document's default prefix, used by two live documents, or reserved (JE, REC — REV50-5). `startNumber≠1` / `resetYearly=false` are reported and ignored. No reference block.
- **D-58 (SEED-OPS) APPROVAL_RULES** (`WorkflowEngine/Onboarding/ApprovalSetupSeeder.cs`).
  - The org's active unconditional `PO` definition gets one ROLE step per level; level 1 has no skip, level n>1 `ConditionValue < minAmount`. "Unedited" = step fingerprint equals the org-creation default (`DefaultPoSteps`, kept in step with WorkflowDefinitionSeeder by a unit test) or the one this seeder last wrote. Merge applies only when unedited; Overwrite also when this seeder wrote it; Full always.
  - An approverRole the engine cannot resolve (ApproverResolutionService role map) refuses the rule — such a step would block every PO. Steps are replaced in place (approvals keep their own step rows): no reference block. ExpenseClaim / `afterA49` skipped with a warning (D-30).
- **D-59 (SEED-OPS) Invites and the welcome hook.**
  - `IOrgUserProvisioningService.InviteUserAsync(OrgUserInviteRequest)` → `OrgUserInviteResult(Email, Outcome INVITED|EXISTS|INVALID, UserId?, Message?)`; never throws for data. Inactive user, 7-day token, the org-admin invite e-mail (Hangfire). EXISTS = any non-deleted account with the e-mail (login is global; UserId only when it is in the same org). INVALID = bad address, unknown/inactive role, another org's custom role, or a role granting PLATFORM_SUPER_ADMIN.
  - Welcome hook: `SMS.Shared.Common.IUserInviteAcceptedHandler.OnInviteAcceptedAsync(InviteAcceptedContext)`, called by `AuthService.AcceptInviteAsync` after save, for every registered handler; a throwing handler is logged and never fails the accept. Context carries OrganizationId, UserId, Email, names, RoleName, LoginUrl (BaseUrl + `/auth/login`); OrganizationName is null (TPL resolves it). TPL implements the WelcomeEmail handler.
  - Shared helpers for all seeders: `SMS.Shared/Onboarding/SetupSeederKit.cs` (SetupJson, SetupFingerprint, SetupUnitOfWork).
- **D-40 (CORE) Module layout.** `SMS.Modules.Onboarding`: Domain/, Data/ (internal `OnboardingDbContext`), Services/Defaults (store, resolver, packs, validator), Services/Setup (wizard, provisioning, inviter), Controllers/. `ISetupDefaultsService` / `ITenantSetupService` are public (MVC needs public controller ctors). `AddOnboardingModule` registers CORE, then `AddOnboardingTemplates` (TPL), `AddOnboardingSampleData` / `AddOnboardingChecklist` (DEMO), then TryAdd Null fallbacks. `UseOnboardingModule` runs after HRM: migrate → pack seeder → missing setup rows → `OnboardingChecklistJob.Schedule()`.
- **D-41 (CORE) Store columns beyond §1.** SetupDefaults.`IsSystemPack` (inserted by the pack seeder); TenantSetupLogs.`SetupDefaultId` + `ResolvedCountryCode/IndustryType` (the row and level actually applied). The delete guard (task 033) = any log row with that SetupDefaultId (all versions) → 409 "unpublish instead". A system pack row is never deletable either (the seeder would re-insert it — REV50-2); unpublish = IsActive false, and inactive rows never resolve.
- **D-42 (CORE) Packs and validation.** A pack file is the payload verbatim (`Packs/{CC}_{INDUSTRY}/{CATEGORY}.json`, folder split at the first `_`); optional top-level `displayName` / `notes` fill the row. Schemas `Packs/schemas/{CATEGORY}.json` are checked by a built-in JSON Schema subset (`JsonSchemaLite`: type, properties, required, additionalProperties, items, enum, const, min/max/exclusive, length, pattern, min/maxItems, local `$ref`) — the repo has no schema library — plus the seeder's `ValidatePayload`. The startup seeder inserts only missing keys, never updates; an invalid pack is logged and skipped (retried next start); a UQ race between two instances is caught and logged.
- **D-43 (CORE) Super admin writes.** Version +1 only when the canonical JSON (sorted keys, normalized numbers) changes; optional `rowVersion` (base64) gives 409 on a stale save; the key is immutable. Import = pack file or bare array, `mode=upsert|insertOnly`, `dryRun`; all or nothing (one invalid item → 400, nothing saved). Export = a JSON file in the same format. Extra endpoints: `meta`, `resolve`, `validate`.
- **D-44 (CORE) Apply orchestration.** Categories run in `SetupPayloadValidator.KnownCategories` order (ApplyOrder + any extra registered seeder). Per category: Skipped (skip list, logged) / NoSeeder / ModuleDisabled (logged Skipped) / NoDefaults / Invalid (override fails validation, logged Failed, stops) / Failed (seeder threw, logged, stops; later = NotRun) / Applied (logged Seed). Only Failed/Invalid/NotRun hold back Completed. The seeder commits its own transaction; Onboarding then writes provenance + log (residual R-11). Invites run only when every category is ok. A Completed org is never downgraded by a re-run.
- **D-45 (CORE) Provenance upsert.** Seed/Merge add new keys and keep an existing row's fingerprint (an admin edit stays "modified"), unless the EntityRef changed (record re-created). Overwrite/Full replace fingerprint/EntityRef/Version and ResetCount +1. RemovedKeys rows are deleted.
- **D-46 (CORE) Skip.** GLOBAL+GLOBAL through the same engine; status Skipped even when a category fails (the admin is not sent back to the wizard; the dashboard shows what failed). Refused (400) once Completed. COMPLETE_SETUP is ticked through IOnboardingProgress on Completed or Skipped.
- **D-47 (CORE) Reset source.** Default `source=applied`: the payload in the org's `DefaultsSnapshotJson` (`{CAT: {setupDefaultId, version, overridden, payload}}`), falling back to the current resolution; `source=latest` uses today's default (the dashboard's `updateAvailable`). This keeps BR-50-015 for resets. Overwrite/Full need `confirm`; Full runs CheckReferences first → 409 with `{referencedCount, referencedKeys}`.
- **D-48 (CORE) Setup rows and status.** Legacy orgs: migration backfill (Completed / LEGACY) — it runs after Tenancy, so the factory default org and SCM-DEMO are Completed on a fresh LocalDB too. New orgs: the provisioning handler (NotStarted); a missed one is filled at startup (`EnsureMissingAsync`) or on the first wizard call. `GET setup/status` is for any signed-in user (banner / guard), `result:null` without an organization.
- **D-49 (CORE) Seed context and invites.** `SetupSeedContext` gains `PayloadIsOverride` + `DefaultPayload` (REV50-8): set on preview, apply and reset (reset reads the snapshot's `overridden`; `source=latest` = false) so a seeder writes GLOBAL rows only from the Super Admin default. Step 7 calls `IOrgUserProvisioningService.InviteUserAsync` (SEED-OPS, D-59) with the org name from `IOrganizationNameLookup`; `roleRef` = digits (RoleID) or a ROLES natural key resolved through SeededRecords; unresolved → FAILED, others as Auth reports.
- **D-75 (FE-A) Wizard shape.** `/portal/pages/onboarding/setup` (SETUP_WIZARD_RUN) has 8 screens: the FSD's 7 steps plus Review & launch. The defaults are one `POST setup/preview` for the chosen country / industry.
  - Only categories the admin edited go as `payloadOverrides`: Step 3 COA, Step 4 TAX, Step 5 CURRENCY + FISCAL. Step 6 goes as `wizardInputs.WAREHOUSE`, Step 7 as `invites` (`roleRef` = pack role code), and the Review untick boxes as `skipCategories`.
  - Every step move saves `state/{step}`: 1 country/industry, 3/4 `{payload}` when edited, 5 `{CURRENCY, FISCAL}`, 6 `{warehouses}`, 7 `{invites}`, 8 `{skip}`. Resume = `GET state` → `setupStep` plus those drafts laid over a fresh preview. A Completed / Skipped org reopens at Step 1 with a "re-run only adds what is missing" note.
  - "Reset to default" shows on steps 1 and 3–7. Step 1 just reloads the preview; steps 2 (live module switches) and 8 have nothing to reset.
  - Changing the country or industry drops the earlier edits. The edited payload belonged to the old pack.
- **D-76 (FE-A) CoA drag-and-drop.** It uses PrimeNG `p-tree` drag-drop (`validateDrop`, component-level `TreeDragDropService`).
  - Every drop is accepted, then the whole chart is checked against the A42 rules: levels 1–3 groups, level 4 leaves, root digit, unique codes, one role per key. A breaking drop is undone with a message.
  - HTML5 drag-and-drop does not work on touch screens, so every node also has a "Move to…" picker (same-level groups of the same root). That is the phone / tablet and keyboard path.
  - Accounts carrying a `default_account_key`, and roots, cannot be removed. The code is editable only on accounts added in the wizard.
- **D-77 (FE-A) First login and banner** (task 144 / 318).
  - `setupWizardRedirectGuard` on `/portal/dashboard` redirects only when the user holds SETUP_WIZARD_RUN, is not the super admin, and `GET setup/status` says NotStarted. Any error lets the dashboard open.
  - The status is read once per session and is updated by state / apply / skip.
  - `<app-setup-incomplete-banner>` (dashboard) shows for NotStarted / InProgress. It gives "Start / Continue setup" when `canRunWizard`, else "ask your administrator".
- **D-78 (FE-A) Step 2 and Step 7.**
  - Modules are `api/tenant/modules` cards. COMING_SOON is hidden. A module switches only if licensed, available, not always-on and the user has MODULES_MANAGE; the A37 enable/disable rules and errors apply. Each switch refreshes ModuleService and the preview.
  - Invite roles are the ROLES pack roles whose `requiredModules` are all enabled.
  - QuickBooks shows only with MODULE_INTEGRATION and INTEGRATION_VIEW/MANAGE. "Connect" opens `/portal/pages/integrations/quickbooks` in a new tab; the status is `GET integrations/quickbooks/connection`, refreshed on window focus.
- **D-79 (FE-A) Super admin Setup Defaults** (`/portal/pages/system/setup-defaults`, under `system/` for the platform-scope guard; PLATFORM_SUPER_ADMIN).
  - The list filters client-side: country, industry, category, version, published.
  - Duplicate = New with `?from=`, unpublished.
  - Structured editors exist for COA (the wizard's tree), TAX (table plus a *verified* tick), ROLES, APPROVAL_RULES and UOM. Every category also has "Edit as JSON", and JSON is the only editor for the other eight.
  - The ROLES matrix rows come from the existing role catalog (`api/roles` → first role's `permissionGroups`), plus codes found only in the payload. There is no permission-catalog endpoint.
  - Import = upload → client parse → "Check" (`dryRun=true`) → "Import". Confirm is enabled only after a clean check.
  - Export = the blob download.
  - Menu: Settings › Organization is one shared definition, `pages/onboarding/onboarding.nav.ts`. FE-A owns entries 1–2 and FE-B owns 3–6; `app.menu.a50.spec.ts` asserts it.
- **D-80 (FE-B) Routes and menu.** FE-B's pages live in `pages/onboarding/fe-b.routes.ts` (`FE_B_ONBOARDING_ROUTES`), which FE-A's `onboarding.routes.ts` spreads, so the two agents never edit the same file.
  - Routes: `/portal/pages/onboarding/document-templates[/:uuid]` (DOC_TEMPLATE_MANAGE), `email-templates[/:uuid]` (EMAIL_TEMPLATE_MANAGE), `sample-data` (SAMPLE_DATA_MANAGE).
  - Data Import stays the A40 page `/portal/pages/settings/import`, linked from the shared menu (D-24; tasks 217–227 ✕).
  - There is no `/new` route. "New template" is a dialog (type + name) that POSTs and then opens the editor. Email: new = server clone of the type's default (`cloneFromUuid`), because without a clone the server needs a subject and a body. Clone = server `cloneFromUuid`.
- **D-81 (FE-B) Template editors.**
  - Header / body / footer are contenteditable fields like the A29 PO template page. Each field remembers its caret, so a placeholder picked in the side panel lands in the field last focused. For email, the subject input keeps `selectionStart`.
  - An empty field (only `<br>` / `&nbsp;`) is saved as null, which keeps the built-in layout (D-25).
  - Colour: picker + hex text, validated as `^#[0-9A-Fa-f]{6}$`, sent upper-case.
  - Logo: drag-and-drop or file picker, images ≤ 2 MB, uploaded as `DOC_TEMPLATE_LOGO` with documentId = the template uuid; the attachment uuid goes into `logoAttachmentUuid` on Save. An `/api/` logo URL is fetched as a blob.
  - Document preview POSTs the unsaved form and shows the PDF in an iframe dialog. Email preview renders the **saved** template (the server preview takes placeholders, not a draft); the dialog says so when there are edits.
  - Set as default is refused while there are unsaved edits. Reset asks first, then opens the seeded row the server returns.
- **D-82 (FE-B) Document actions.**
  - Email to customer (sales invoice: SALES_INVOICE_MANAGE, not DRAFT / CANCELLED; quotation: SALE_QUOTATION_SEND) and Email to supplier (PO: any of PO_CREATE / PO_EDIT / PO_APPROVE, not DRAFT / PENDING_APPROVAL / REJECTED / CANCELLED) share `shared/components/document-email-dialog`.
  - To is **never prefilled**; the hint says it defaults to the party's email (D-TPL). To / Cc are validated as `,` / `;` lists.
  - A 200 with nobody reached counts as a failure, and the dialog stays open. A partial send gives a warn toast listing who did not get it.
  - Download PDF: quotation (SALE_QUOTATION_VIEW) and customer payment receipt (CUSTOMER_PAYMENT_VIEW), as blobs. A failed call reads the server message out of the blob.
- **D-83 (FE-B) Sample data and Demo badge.**
  - The page shows `counts` (what a load creates) and `current` (demo records now), in a fixed order, plus `notes` and `reason`. Load and Delete each go through a dialog listing the records.
  - The delete result lists `kept` records (name, type, reason) and stays on the page until the next action.
  - `<app-demo-badge entityType [entityRef]>` sits on the customer, supplier (Vendor), product (product uuid), sale order and PO lists. `DemoRefsStore` makes one `demo/refs` call per type, reuses it for 60 s and is invalidated after a load or delete. Any error, or a host without HttpClient, means no badge.
- **D-84 (FE-B) Checklist widget** (REV50-4).
  - `<app-onboarding-checklist-widget>` sits directly under FE-A's banner on the dashboard. It calls `GET checklist` only with ONBOARDING_VIEW.
  - It renders **nothing** while loading, on any error (403, 404, 5xx, no toast), when dismissed, or when complete, so a LEGACY org never sees a flash.
  - It shows "N of M done" from `completedCount` / `totalCount`, steps by `sortOrder`, and "Do this now" (`link` as given) on open steps. It also offers a manual "Mark done" (`steps/{code}/complete`) and "I'll handle this myself" (dismiss, permanent).

- **D-50 (SEED-FIN) CURRENCY** (`Finance/Onboarding/CurrencySetupSeeder.cs`). The pack currencies (base + additional) become active `finance.org_currencies` rows. A missing code is added from the global catalog; a code the catalog lacks reaches `lookups.Currencies` only when it is in the Super Admin default, never from an override (REV50-8). Nothing is ever deactivated or removed, so CheckReferences = 0.
  - **Base:** Tenancy's settings had no write seam, so a new one was added: `SMS.Shared/Onboarding/OrganizationBaseCurrencySeam.cs` (`IOrganizationBaseCurrencySetter`) with its adapter `Tenancy/Onboarding/OrganizationBaseCurrencySetter.cs`, which sits over `IOrganizationCurrencySettingsService.UpdateAsync`. It sets all three bases or none. A domain locked by documents refuses the change, and the reason becomes a warning. The rate currency never changes (A35 REV-07).
  - **REV50-7:** re-activating a currency and setting the base happen only on the first apply (no provenance yet) or in Overwrite. Merge reports what it left alone. Full runs as Overwrite.
- **D-51 (SEED-FIN) COA + account roles** (`Accounting/Onboarding/CoaSetupSeeder.cs`, `ChartOfAccountsService.Setup.cs`).
  - **Payload:** the A42 template file, validated by CoaFile + CoaValidator.
  - **Empty chart, A42 replace allowed:** `ReplaceChartAsync` with the rows as an in-memory template.
  - **Otherwise a merge:** a row whose code is not an active account is inserted under the active group with its parent code. That group must be the right level and root, else the row and its descendants are skipped with a warning. Existing accounts are never changed. The 2000-account limit stops the merge with a warning. 39/390/3900 are added only when the org has no 3900. Roles are assigned only where the default is unassigned.
  - **Fingerprint:** code + name + account type + parent code.
  - **"Referenced"** = HasTransactions, any journal line, a non-zero opening balance, or an external mapping.
  - **Reset:**
    - Overwrite puts unreferenced seeded accounts back to the pack's name, type and description in place (never moved). It deletes seeded accounts that left the pack when A42's delete rules allow, then merges.
    - Full is the A42 replace of the whole chart. CheckReferences covers every active account and also lists A42's replace refusal (fiscal year set, opening balances posted, posted journals), so Full is refused with 409 up front (REV50-6).
- **D-52 (SEED-FIN) TAX and FISCAL.**
  - **TAX** (`Finance/Onboarding/TaxSetupSeeder.cs`).
    - Code pattern: TaxCodeService's `^[A-Z0-9_-]+$`, upper-cased. A '.' allowed by the schema is refused.
    - Insert-only by Code. A pack default is set only when no active code of an overlapping usage is the default.
    - "Referenced" = the uuid on sale-order lines, quotation lines, sales-invoice lines or supplier invoices, or any `ITaxCodeReferenceChecker`. That checker now covers POS store defaults and POS orders (new `Pos/Services/PosTaxCodeReferenceChecker.cs`, REV50-1; the TaxCodeService rename guard benefits too) as well as QuickBooks mappings.
    - Overwrite reverts seeded codes; a seeded code that left the pack is deleted, or deactivated if referenced.
    - Full deletes every code and reseeds. CheckReferences covers every code of the org (REV50-6), and the in-seeder backstop throws 409.
  - **FISCAL** (`Accounting/Onboarding/FiscalSetupSeeder.cs`).
    - Creates the year containing today through `IFiscalYearService.CreateAsync`, only when the org has no year; otherwise it skips with a warning.
    - "Quarterly" is accepted and gets monthly periods plus a warning, since A42 is monthly-only.
    - `yearNamePattern`: {YYYY} = start year, {YY} = end year's last two digits, {YYYY2} = end year.
    - Natural key = start date.
    - Merge and Overwrite never change a year. Full deletes and re-creates the seeded year. It is refused when any year was not created by the wizard, or when the seeded year is referenced (a journal in or linked to it, a non-open period, posted opening balances); CheckReferences lists exactly those.
- **D-53 (SEED-FIN) PAYMENT_TERMS** (`Finance/Onboarding/PaymentTermsSetupSeeder.cs`).
  - Only terms with a discount become A48 `EarlyPaymentTerm` rows, keyed by Code. The table's CHECKs (0 < DiscountPercent < 100, DiscountDays < NetDays) make COD / NET30 impossible there.
  - Every name is ensured in the global `lookups.PaymentTerms` (insert if missing, via `ILookupsService`), but only names in the Super Admin default when the payload is an override (REV50-8).
  - `isDefault` has no column and is ignored.
  - "Referenced" = vendor finance profiles and payment schedules. CheckReferences covers every early term of the org (REV50-6). Full refusal throws 409.
- **D-54 (SEED-FIN) GL_OPENING_BALANCE import** (`Accounting/Onboarding/GlOpeningBalanceImportTarget.cs`).
  - **Profile:** IsOpeningBalance (IMPORT_OPENING_BALANCE, FEATURE_OPENING_BALANCE_IMPORT, opening date set), RequiresModule FEATURE_GENERAL_LEDGER, RequiredPermissions GL_SETUP_MANAGE, ImportOrder 14. Fields: `account_code, debit, credit, notes`.
  - **Validation** runs over the whole file. Row codes: LOOKUP_NOT_FOUND / INACTIVE / NOT_LEAF / PLUG_ACCOUNT (3900) / OUT_OF_RANGE / BOTH_SIDES / ZERO_AMOUNT / ALREADY_POSTED / DUPLICATE_IN_FILE (every row of an account listed twice) / DRAFT_EXISTS. If the rows that pass do not balance, every one of them gets UNBALANCED ("Debits do not equal credits…"), so nothing is imported (BR-50-011, TS-50-016). Because the balance is computed over error-free rows only, and the duplicate and draft rows are errors (REV50-9), the imported set is always exactly the balanced set, whatever the A40 batch split.
  - **Drafts:** an account that already has a draft is refused with DRAFT_EXISTS (IMP-OB-06; clear it on the GL page).
  - **Apply:** `IOpeningBalanceService.SaveBatchAsync`, per A40 batch. Posting stays the separate A42 action, so TS-50-017's "journal posted" happens on Post, not on import.
  - **Also in Accounting (REV50-3):** EntitySyncGateway ignores an observing target's answer (Success + NoTargetConfigured + 0 queued) whenever another target answered.
- **D-65 (TPL) Seeded document templates carry branding only.** The packs set `bodyHtml` null.
  - A template with no body brands the built-in layout (colour, logo, header and footer HTML, bank details, terms), so status banners, payments, aging and cartons are kept.
  - A non-empty body goes to Onboarding's `TemplatedDocumentRenderer`, generalised from the PO AngleSharp path, with structural blocks `{LineItems}` `{TaxSummary}` `{Totals}` `{BankDetails}` `{TermsAndConditions}` `{SignatureBlock}`.
  - **No template = the pre-A50 PDF, byte for byte**, proven by goldens in `tests/*/A50/Golden`: invoice ×2, statement, PO.
  - The renderers touched are sales invoice, PO, packing list (DeliveryNote; the gate pass keeps the house look) and customer statement (vendor statements keep the house look). New PDFs: quotation and payment receipt.
  - Every render is wrapped in `PdfRenderGate.Run(document.GeneratePdf)` after all awaits; this also closes the old gap on invoice, PO, packing list and gate pass.
- **D-66 (TPL) Header / footer HTML in the built-in layouts** are read through `SMS.Shared.Onboarding.HtmlLite`, a small subset (TEMPLATE-PLACEHOLDERS.md §2.2).
  - The header replaces only the letterhead's company block. Title, number, date and party are still printed by the layout, so DATA dropped them from the pack headers.
  - The footer replaces the old footer-text line.
- **D-67 (TPL) New Shared seams** (`SMS.Shared/Onboarding/DocumentRenderingContracts.cs`, `HtmlLite.cs`, `DocumentEmail.cs`), each with a Null default:
  - `ITemplatedDocumentRenderer` + `TemplatedDocument`;
  - `ILegacyLetterheadSource` (Lookups, over PoDocumentTemplate);
  - `IDocumentAssetReader` (WorkflowEngine; reads a `DOC_TEMPLATE_LOGO` attachment in-process, never outside wwwroot);
  - the one `{Token}` engine `TemplateText`, plus `AmountInWords`.
  - The company block of every templated document = the saved letterhead, else the organization's name.
  - The callers (Finance, Demand, Logistics) `TryAdd` Null providers; Onboarding's registrations win.
  - `TemplateSupport` resolves `ISetupDefaultsService` lazily (QA50-1: DI cycle through the seeders).
- **D-68 (TPL) Document emails** (`POST api/sales-invoices|sale-quotations|purchase-orders/{uuid}/email`):
  - **Recipients:** the request's `to`, else the party's email (SupplierContacts). `cc` gets one send each, because IEmailSender has no CC; at most 20 recipients.
  - **Delivery:** the PDF is attached and sent through `SendWithResultAsync`. Failures are reported in `failed[]`, never thrown.
  - **Permissions:** invoice = SALES_INVOICE_MANAGE (draft / cancelled refused); quotation = SALE_QUOTATION_SEND (does not mark it SENT); PO = PO_CREATE | PO_EDIT | PO_APPROVE.
  - **Text:** with no template, the built-in text with the same wording as Onboarding's.
  - **Welcome email:** sent through SEED-OPS's `IUserInviteAcceptedHandler`, reading the invited org explicitly (accept-invite is anonymous).
  - **Previews:** the document preview renders a sample document of the type through the templated renderer (a template with no body shows the built-in blocks in order).
- **D-69 (TPL) Reset and the PO copy.**
  - "Reset to default" restores the seeded row of the type from the org's applied DOC_TEMPLATES / EMAIL_TEMPLATES snapshot, else today's resolution, else a built-in default.
    - It makes that row the default; custom templates are kept and the logo is kept.
    - Set-default clears the others then sets this one, two saves in one serializable transaction (filtered unique index).
    - The transaction runs inside `Database.CreateExecutionStrategy().ExecuteAsync(...)`: the SQL Server retrying strategy rejects a bare user transaction (found by the Templates E2E; every create/set-default of a default template returned HTTP 500 until then).
  - **One-time copy of the PoDocumentTemplate (REV50-12):** made when the admin first lists document templates, or by the DOC_TEMPLATES seeder.
    - The copy carries **no body, header, footer, colour or logo**, so the PO keeps printing its pre-A50 letter byte for byte (test).
    - The admin brands it in the new editor, and pastes a body to switch the PO to the templated renderer (`ConvertLegacyBody` maps `{{X}}` to `{X}`).

- **D-90 (HRM-50) HRM category (task 058).** Category `HRM` is the 14th and last of `SetupCategories.ApplyOrder`; `RequiresModule` = MODULE_HRM (without it the wizard reports `ModuleDisabled`, which holds nothing back).
  - **Owner:** the Hrm module, `Onboarding/HrmSetupSeeder.cs` (an `ISetupCategorySeeder`, registered in `AddHrmServices`). The A49 `HrmSeeder` stays as it is (first-use seed, insert-only by code) and `HrmSeedData` stays the single source of its values; a test (`The_pk_pack_says_what_HrmSeedData_seeds`) keeps `Packs/PK_GLOBAL/HRM.json` equal to it, which is how the pack "reuses" the A49 seed without a second copy to maintain.
  - **Payload** (PAYLOAD-SCHEMAS.md "HRM"): `{ verified:false, leaveTypes[], salaryComponents[], taxSlabTables[] }`. Natural keys `LEAVE/CODE`, `COMPONENT/CODE`, `TAX/CODE`; EntityRef = the row's Uuid. Statutory rates (EOBI, PF, SS, gratuity) are not in the payload: HRM seeds them itself on first use, also unverified.
  - **Pack:** `PK_GLOBAL/HRM.json` only (country-level, like TAX). AE / SA / GLOBAL have none: no tax slabs or leave entitlements are invented for a country nobody has researched, so an AE organization sees `NoDefaults` for HRM and completes anyway.
  - **Nothing verifies anything (A49 G-21, REV-49).** `verified` must be `false` in the payload (a `true` is a validation error, not a quiet downgrade); a tax table is always inserted `IsVerified = 0` and no code path writes IsVerified, VerifiedBy or VerifiedAt. A table a person verified is never rewritten (Overwrite warns and keeps it, including a reviewer's corrected rate) and never deleted (Full keeps it and reports it). The seeded PK table is the same expired 2025-26 table HRM seeds (valid to 2026-06-30, QA49-1): payroll calculation stays blocked until someone enters and verifies the current one. No rate was written for this round.
  - **Adoption:** a row with the pack's code whose values already equal the pack's (what HRM's first-use seed leaves behind) is recorded as seeded and not rewritten; a differing row is the organization's own and is left alone. So the wizard and HRM's own seed never duplicate each other, in either order.
  - **Reset:** Overwrite restores seeded rows and removes seeded rows the pack dropped; Full removes every seeded row and re-creates the pack's. Kept and reported (BR-50-008): a leave type with requests or ledger entries; a component used by a salary assignment, by a structure other than the default `STD_MONTHLY` (or by it once it has assignments) or by a payroll line; a verified tax table. The default structure's line for a removed component goes with it.
  - **Transactions:** `SetupUnitOfWork` (the SQL Server retrying execution strategy), as every seeder.
- **D-91 (HRM-50) Expense approval rule (APPROVAL_RULES).** `ApprovalSetupSeeder` now handles `ExpenseClaim` / interface `EXPENSE_CLAIM` as it handles PO (D-18, D-58), merge-safe the same way: applied only while the definition is unedited (fingerprint equals the one HRM seeds, `DefaultExpenseSteps`, kept in step by a test, or the one this seeder wrote).
  - **Level 1 is the HRM named approver**, not a role: the step is `NAMED / HR_APPROVER`, the user HRM names at submit time (line manager, else department head, else a holder of the approving permission). The pack's `approverRole` on level 1 (`HR_MANAGER`) is informational, because the engine's role map cannot resolve an HR role. Levels above 1 are ROLE steps and must resolve (`FINANCE_MANAGER`), skipped while `ConditionValue < minAmount` (HRM passes the claim total).
  - **PK/AE/SA/GLOBAL packs:** `interfaceCode: EXPENSE_CLAIM`, level 2 from 10,000.01 (so a claim of exactly 10,000.00 stays on level 1, as the pack's `maxAmount: 10000` says), `afterA49` removed. The flag is still accepted by the schema and ignored. The amounts are in the organization's currency like the PO tiers; they are defaults, not advice.
  - **Only while MODULE_HRM is on** (`IModuleGate`); otherwise a warning and nothing is created. A missing definition is created through `IWorkflowSeedingService.SeedHrmWorkflowsAsync` (HRM creates the same three on first use), so the wizard does not depend on the order in which HRM was opened.
  - Module gating is per rule since D-96 (R-16 closed).
- **D-92 (HRM-50) HR roles in ROLES.** `HR_MANAGER` and `HR_OFFICER` are added to the four ROLES packs (PK_Trading, AE_Trading, SA_Trading, GLOBAL_GLOBAL) with `requiredModules: [MODULE_HRM]`. Roles are global in this repo (D-20) and both exist as global roles (AuthDataSeeder, A49 G-22), so the wizard **reuses** them (provenance `Reused`), makes no copy and edits no global role; the pack permission lists apply only where an organization must get its own custom role because no global role has the code. The lists equal the global grants (`HrManagerCodes` = every HRM_ code, `HrOfficerCodes` = day-to-day HR, no salary, payroll, bank or unmasked-identity codes); a test pins the officer's exclusions and that the manager list equals the `HRM_*` constants.
- **D-93 (HRM-50) PayslipSent email.** `POST api/hrm/payslips/{uuid}/email` and `POST api/hrm/payroll-runs/{uuid}/payslips/email` (Hrm `PayslipEmailService`, `PayslipEmailController`; HRM API-CONTRACT §9.1). Subject and body come from the organization's PayslipSent template through `IEmailTemplateRenderer` (A50), else the built-in text; the payslip PDF is attached; it goes out through `IEmailSender.SendWithResultAsync`.
  - **Recipient:** only the employee's own address on file (work email, else personal; a value that is not one plain valid address is ignored). Neither request has a `to`, `cc` or `bcc`. No address: 422 `PAYSLIP_NO_EMAIL`; nothing is sent.
  - **Access:** one payslip = the PDF download's rule (HR with payroll view in scope, or self service on one's own; anyone else 404), only for a finalized/posted/paid run, never a reversed one. **The whole run needs `HRM_PAYSLIP_EMAIL` (D-98; first built on `HRM_PAYROLL_PAY`, replaced on the user's decision) and the all-employee scope**, checked on the route and again in the service; a second click skips employees already sent unless `{ "resend": true }`.
  - **Nothing identifying in logs:** the service logs an outcome and the payslip's uuid, and counts for a run; no name, no address, no amount, no payslip text. The sender's own error (which can name the address) is not logged or returned; the answer carries `SEND_FAILED` and a masked address only on success. The answer masks the address (`a***@example.com`).
  - **Record:** each successful send writes a `SensitiveAccessLog` row (`PAYSLIP_EMAIL`, employee, who, `Payslip emailed <run number>`), the same record the bank file uses. No migration.
  - **Tests** use a capturing sender and, in the host, the factory's `Mail` capture; `Accept` is switched on only inside the test. Nothing real is sent.
- **D-94 (HRM-50) HRM checklist steps.** Three steps in `ChecklistCatalog`, shown only while MODULE_HRM is on (D-72): `HRM_SETUP` (payroll cost center chosen), `HRM_ADD_EMPLOYEE` (a non-deleted employee exists), `HRM_VERIFY_TAX` (a person has verified a tax table). Signal: `Hrm/Onboarding/HrmOnboardingSignal` (read-only; the third step is ticked by someone else's verification, never by this code). Existing organizations get the steps through the idempotent checklist sync (the catalog is the source of the wording).
- **D-95 (HRM-50) Wizard and screens.** The frontend category list gains `HRM` ("HR defaults", link to the salary components); `payloadCount` counts leave types + components + tax tables; the approval-rules editor no longer shows an "After A49 - inert" pill. The wizard still lists categories by the API (`preview` returns all 14), so no new wizard step was needed: HRM is reviewed and applied on the Review step with the other optional categories. The super-admin editor gets an empty HRM payload (`verified:false` and three empty lists).
- **D-96 (HRM-50, user decision) Per-rule module gating of APPROVAL_RULES.** `ApprovalSetupSeeder.RequiresModule` is now null; the seeder checks the module per rule through `IModuleGate`: the PO tiers need MODULE_DEMAND, the ExpenseClaim rule needs only MODULE_HRM. A rule whose module is off is skipped with a warning and nothing is created for it; the other rule still applies. Merge-safe behaviour (applied only while unedited) is unchanged. Side effect: with both modules off the category reports Applied with 0 created and two warnings instead of `ModuleDisabled`. Closes R-16. Tests: three new in `ApprovalSetupSeederTests` (HRM on + Demand off gets the rule; Demand on + HRM off gets the PO tiers only; both off, and an edited expense workflow stays untouched).
- **D-97 (HRM-50, user decision) Finance Manager may approve expense claims.** `AuthDataSeeder.HrmGrants` gives `FINANCE_MANAGER` `HRM_EXPENSE_APPROVE`. The existing idempotent HRM grant seeder runs at every start, adds only a missing row and never re-enables a grant an administrator switched off, so existing databases get it on the next API start with no migration and nothing removed. The role is global, so every organization is covered. No other role changed. Closes R-17. Tests in `HrmPermissionSeedTests` (grant present; second and third start add no duplicate; off stays off; FINANCE_OFFICER and AUDITOR do not gain it).
- **D-98 (HRM-50, user decision) `HRM_PAYSLIP_EMAIL`.** New permission ("Email Payslips in Bulk"), added to `PermissionCodes`, `HrmCodes`, `HrmSensitiveCodes` (so never the System Admin) and the permission catalog seed (inserted when missing, with `MODULE_HRM` through the `HRM_` prefix). Granted to HR Manager (its grants are all `HrmCodes`) and Org Admin; not to HR Officer. It replaces `HRM_PAYROLL_PAY` on `POST api/hrm/payroll-runs/{uuid}/payslips/email`, on the route attribute and in the service re-check; the all-employee scope requirement stays and the single-payslip route is unchanged. The four ROLES packs' HR_MANAGER list carries it. Closes the bulk-permission question raised in the first HRM-50 report. Tests: Hrm (`A50PayslipEmailTests`: PAYROLL_PAY holder refused, PAYSLIP_EMAIL holder succeeds, single route unchanged), Auth (`HrmPermissionSeedTests`), Onboarding pack test, and the LocalDB E2E (a PAYROLL_PAY + view + all-scope user gets 403 on the bulk route).
## Residuals
- **R-TPL-1** The document preview of a template without a body approximates the built-in layout (same header, footer, colour and blocks) rather than calling each module's own renderer, which Onboarding cannot reference.
- **R-TPL-2** No golden for the packing list: the Logistics harness numbers deliveries by the current year. A packing list with no template takes the same code path with the house palette (tests green); a DeliveryNote golden would need a fixed numbering seam.
- **R-TPL-3** PaymentReceived and PaymentReminder email templates are seeded only. Recording a payment sends nothing, and dunning keeps its A48 templates. PayslipSent comes after A49 (D-30).
- **R-TPL-4** The header/footer HTML of the built-in layouts is a subset (D-66). Images and tables beyond one row are ignored in them.
- **R-13 (SEED-FIN)** TAX references do not see document lines in modules without a uuid column or checker (e.g. service orders keep only a percent).
- **R-14 (SEED-FIN)** A non-PK org keeps the PKR rate currency (A35: read-only), so its rates are still entered against PKR.
- **R-15 (SEED-FIN)** PAYMENT_TERMS: no per-org row for terms without a discount; partners pick the global list. COA merge reverts in place and never moves an account to another parent.
- **R-1** UoM conversions are stored, not applied.
- **R-2** Category hierarchy deeper than 2 levels is flattened.
- **R-3** Numbering prefixes only for documents on the shared generator.
- **R-4** No antivirus scan of uploads.
- **R-5** HRM pieces after A49 (D-30): built, see D-90..D-95 and R-16..R-19.
- **R-6 (DATA)** The statutory tax rates in every pack (PK / AE / SA) are unverified (`verified:false`). A tax adviser must confirm them before a super admin sets `verified:true`.
- **R-8 (SEED-OPS)** A reset never deletes a seeded unit of measure or warehouse (only deactivates): product unit codes and other modules' warehouse uuids are invisible to the seeders.
- **R-9 (SEED-OPS)** Approval rules: no auto-approve tier (the engine has none; level 1 always applies) and only the PO definition is handled; expense rules after A49.
- **R-10 (SEED-OPS)** Numbering: startNumber / non-yearly reset are ignored; an A47 journal type's own sequence prefix is not checked against org prefixes.
- **R-16 (HRM-50) CLOSED by D-96.** Was: an HRM org without Demand got no expense rule.
- **R-17 (HRM-50) CLOSED by D-97.** Was: the Finance Manager could not use the HRM approve route for level 2.
- **R-18 (HRM-50)** The PK tax table the pack seeds is the 2025-26 table, expired on 2026-06-30 (QA49-1). Seeding it is not a claim that it is current; payroll still needs a current, verified table entered by the organization. Statutory rates in the pack: none (HRM seeds them, unverified).
- **R-19 (HRM-50)** PayslipSent has no delivery tracking beyond the sensitive-access record and the sender's own result; there is no retry queue, and no bounce handling. A failed send is reported and can be repeated.- **R-20 (HRM-50)** There is no screen for emailing payslips: the two endpoints (D-93) are API only. No frontend button was added in this round (the payroll wizard and the employee payslip list are A49 screens). Frontend: the small edits (category list, payload count, approval-rules pill, empty HRM payload) ran against `pages/onboarding/**` (98/98) and `pages/system/setup-defaults/**` (33/33) specs; the full frontend suite and `ng build` were not run.
- **R-7 (DATA)** A pack edited after its first deploy does not reach an existing DB, because the startup seeder inserts only missing rows. Use the super admin import instead.
- **R-11 (CORE)** A seeder commits before Onboarding writes provenance; if that second save fails, the records exist with no provenance and read as "custom" until a re-apply (accepted by REV-50).
- **R-12 (CORE)** Pack schemas are checked by a JSON Schema subset (D-42); keywords outside it (oneOf, format…) are ignored, not enforced.

## QA (QA-50) — test plan, results, findings

All E2E classes live in `tests/SMS.Integration.Tests/Onboarding/` on the real `Program.cs` host over a throwaway LocalDB database (`SapWebApplicationFactory`, never SMSGlobal), driven through HTTP as real users (org admin from `POST api/system/organizations`, logins with exact permission sets). Bodies are anonymous objects and answers are read as JSON, so the classes do not depend on builder DTO types. Run each class alone: `dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~Onboarding.<Class>`.

| Class | Covers |
|---|---|
| `OnboardingKit` | shared helpers: new org, wizard calls, provenance / SQL reads, super-admin calls, PDF text/bytes, timing |
| `OnboardingWizardE2ETests` | TS-50-001..004, 011..013, 025; BR-50-001..005, 013 |
| `OnboardingResetE2ETests` | TS-50-005..010; BR-50-006..008 |
| `OnboardingSuperAdminE2ETests` | TS-50-024; BR-50-015; rows 353, 354 |
| `OnboardingImportE2ETests` | TS-50-016, 017; BR-50-011, 012; rows 351, 355, 356 |
| `OnboardingSampleDataE2ETests` | TS-50-018, 019; BR-50-009, 010; row 352 |
| `OnboardingTemplatesE2ETests` | TS-50-020, 021 |
| `OnboardingChecklistE2ETests` | TS-50-022, 023; BR-50-014 |
| `OnboardingPerformanceE2ETests` | row 350 (apply), plus measured times for 351/352 |

### Mapping — test scenarios (rows 325–349)

| TS / row | Expectation as changed by decisions | Coverage | Owner of the code under test |
|---|---|---|---|
| TS-50-001 / 325 | A new org (`POST api/system/organizations`) has `setup/state` = **NotStarted** (D-6 provisioned handler); every pre-existing org (factory default org, seeded SCM-DEMO) reads **Completed** with note LEGACY. The redirect itself is an FE guard (D-7): E2E asserts the state the guard reads; the redirect is FE-A's unit test. | E2E `Wizard` | CORE / FE-A |
| TS-50-002 / 326 | PK+Trading preview: COA count = **the pack's real count** (PK_GENERAL items, 110, not the FSD's 45), TAX = 6 (D-13), each category reports `version` and `resolvedFrom`. The test reads the expected counts from the preview payload itself and pins COA=110 / TAX=6. | E2E `Wizard` | CORE / DATA |
| TS-50-003 / 327 | Apply seeds every category **into the owning module's own tables** (D-2/D-3): `finance.tax_codes`, chart of accounts, `finance.org_currencies`, `EarlyPaymentTerm`, `lookups` uom values, `inventory.ProductCategories`, `logistics.document_number_formats`, WorkflowEngine PO definition, roles, `inventory.Warehouses`+Zones, document/email templates; one TenantSetupLog row per category; status Completed. Asserted via the owners' read APIs where they exist, SQL otherwise. | E2E `Wizard` | CORE + all seeders |
| TS-50-004 / 328 | Second apply: every table count unchanged, `SeededRecords` count unchanged (BR-50-004). | E2E `Wizard` | CORE + seeders |
| TS-50-005 / 329 | Edit a seeded account's name → dashboard `modified` lists it (D-5 fingerprint, **not** an IsModified column). | E2E `Reset` | SEED-FIN |
| TS-50-006 / 330 | A custom tax code (`POST api/finance/tax-codes`) → dashboard TAX `currentCount` > `seededCount`; it has no provenance row. | E2E `Reset` | SEED-FIN / CORE |
| TS-50-007 / 331 | Merge: a deleted/deactivated seeded tax code comes back; the custom code stays. | E2E `Reset` | SEED-FIN |
| TS-50-008 / 332 | Overwrite: an edited seeded tax code returns to the pack's rate; the custom code stays. | E2E `Reset` | SEED-FIN |
| TS-50-009 / 333 | Full on an unused category (UOM / product categories): seeded rows removed and re-seeded, provenance ResetCount up. | E2E `Reset` | SEED-OPS |
| TS-50-010 / 334 | Full on a used category (a seeded product category on a product; a seeded tax code on a document): refused, message names the referenced count; nothing deleted. | E2E `Reset` | SEED-OPS / SEED-FIN |
| TS-50-011 / 335 | Org A edits a seeded tax code and adds one → org B (same pack) still has the pack rate, no extra code; A's dashboard ≠ B's. | E2E `Wizard` | all |
| TS-50-012 / 336 | `setup/skip` → Skipped and the **GLOBAL+GLOBAL** packs applied (TAX = one "NO TAX 0%", COA = GLOBAL_GENERAL). | E2E `Wizard` | CORE |
| TS-50-013 / 337 | `POST setup/state/3 {data}` → `GET setup/state` returns step 3 and the draft, InProgress; a fresh login sees the same. | E2E `Wizard` | CORE |
| TS-50-014 / 338 | 500 customers from Excel = **A40 (D-24)**; already covered by `DataImport/DataImportE2ETests` (CUSTOMER target) — re-run in regression, not duplicated. | existing A40 E2E | (A40) |
| TS-50-015 / 339 | Warnings → import valid rows only = A40 validate-first (`COMPLETED_WITH_ERRORS`), covered by `DataImportE2ETests`. | existing A40 E2E | (A40) |
| TS-50-016 / 340 | `GL_OPENING_BALANCE` import, debits ≠ credits → validation rejects the file (BR-50-011), nothing written to the opening-balance draft. Also: unknown account, group (non-leaf) account, a row with both sides. | E2E `Import` | SEED-FIN |
| TS-50-017 / 341 | Balanced → the A42 opening-balance **draft** holds the lines; **no journal is posted** by the import (D-24). Posting through the existing `opening-balances/post` then creates one OPENING_BALANCE journal. | E2E `Import` | SEED-FIN |
| TS-50-018 / 342 | Load → 5 customers + 3 vendors + 20 products (+2 SO, 1 PO where on), every one recorded in `onboarding.DemoRecords` (**D-27 provenance, not IsDemo**); names end "(Demo)"; second load refused. | E2E `SampleData` | DEMO |
| TS-50-019 / 343 | Cleanup → demo rows gone in reverse order; a real customer stays; a demo customer used by a real document is **kept and reported**. | E2E `SampleData` | DEMO |
| TS-50-020 / 344 | Edit the SalesInvoice template (colour, footer text, logo) → invoice PDF bytes differ from the no-template render and contain the footer text; an org with no template renders as before (same text as the built-in). Same check on quotation PDF (new). | E2E `Templates` | TPL |
| TS-50-021 / 345 | Edit an email template → `send-test` reaches the fake sender (`Mail` capture) with the rendered subject/body, tokens substituted and HTML-encoded. | E2E `Templates` | TPL |
| TS-50-022 / 346 | Create the first customer → `ADD_CUSTOMER` completed (instant hook or signal on read), never unticked; demo customers do not tick it. | E2E `Checklist` | DEMO |
| TS-50-023 / 347 | Dismiss → `dismissed = true` on the next read. | E2E `Checklist` | DEMO |
| TS-50-024 / 348 | Super admin edits the PK TAX default (`PUT api/system/setup-defaults/{uuid}`) → version +1; an org that applied before keeps its rates; a new org gets the new rate and the new version in TenantSetupLog (BR-50-013/015). | E2E `SuperAdmin` | CORE |
| TS-50-025 / 349 | AE + Manufacturing → TAX resolvedFrom (AE, GLOBAL); COA resolvedFrom (GLOBAL, GLOBAL) unless an AE pack exists at country level — the test asserts the fallback chain order of D-4 against whatever packs DATA ships. | E2E `Wizard` | CORE / DATA |

### Mapping — business rules

| BR | Coverage |
|---|---|
| BR-50-001 | TS-50-001 (state); FE guard = FE-A unit |
| BR-50-002 | TS-50-013 |
| BR-50-003 | TS-50-012 |
| BR-50-004 | TS-50-004 |
| BR-50-005 | TS-50-011 + row 354 |
| BR-50-006 | TS-50-010 (Full refused) + Overwrite on a referenced record keeps it |
| BR-50-007 | TS-50-007 |
| BR-50-008 | TS-50-010 |
| BR-50-009 | TS-50-018 via DemoRecords (D-27) |
| BR-50-010 | TS-50-019 |
| BR-50-011 | TS-50-016 |
| BR-50-012 | TS-50-016 (validation before any write) + A40 suite |
| BR-50-013 | TS-50-024 (TenantSetupLog DefaultsVersion, SeededRecords Version) |
| BR-50-014 | TS-50-022 |
| BR-50-015 | TS-50-024 |

### Mapping — rows 350–356

| Row | Test | Pass bar |
|---|---|---|
| 350 | `Performance.Apply_full_pk_trading_pack_under_10s` | `POST setup/apply` wall clock < 10 s on LocalDB |
| 351 | `Import.Five_thousand_products_under_60s` | A40 PRODUCT upload+validate+execute (background path + wait) < 60 s |
| 352 | `SampleData.Load_under_15s` (also in Performance) | `POST sample-data/load` < 15 s |
| 353 | `SuperAdmin.Org_admin_gets_403_on_every_super_admin_route` | 403 on all `api/system/setup-defaults` routes |
| 354 | `SuperAdmin.Another_orgs_setup_data_is_invisible` | B's state/categories/seeded/templates/demo refs never show A's rows; uuids of A's templates 404 for B |
| 355 | `Import.File_limit_is_10_mb` | the A40 10 MB limit (D-24, **not 50 MB**) refuses an 11 MB file |
| 356 | `Import.Upload_type_checks` | D-32/R-4: UploadRules type/extension/size checks — import (.exe/.xls/renamed binary refused) and the template logo upload (`DOC_TEMPLATE_LOGO`: non-image refused). No AV engine. |

### Measured times
(filled in at the finish run)

### Findings (QA50-n)
(none yet)

### Regression
(filled in at the finish run)

## Review (REV-50)
Independent review in rounds: early (2026-11-09), mid, final. Status: Open · Fixed (verified) · Accepted (residual).

| # | Owner | Sev | Finding | Status |
|---|---|---|---|---|
| REV50-1 | SEED-FIN | Medium | TaxSetupSeeder.ReferencedAsync omits POS orders (`PosOrders.TaxCodeUuid`) and the POS default tax code. Full reset can delete codes still in use (BR-50-008). | Fixed (verified: PosTaxCodeReferenceChecker registered as ITaxCodeReferenceChecker, IPosModule.cs:40; TaxSetupSeeder asks every checker). Residual R-13: service orders keep only a percent. |
| REV50-2 | CORE | Low | DELETE of an unapplied system pack row is undone at the next start (pack seeder re-inserts it). | Fixed (verified: SetupDefaultsService.cs:194 refuses IsSystemPack delete with 409; the pack seeder catches the UQ race) |
| REV50-3 | DEMO (+SEED-FIN gateway) | Medium | The "onboarding" IEntitySyncTarget makes EntitySyncGateway combine 2 results for QBO orgs. DuplicateSkipped can become NoTargetConfigured (results[0]), and error messages gain a target prefix. | Fixed (verified: EntitySyncGateway.cs:103 drops Success+NoTargetConfigured+0-queued results before combining; tests in PostingGlAndLogTests) |
| REV50-4 | DEMO + FE-B | Medium | LEGACY (live) orgs get a checklist created on first read, so the widget appears for long-live customers. Create it Dismissed for LEGACY orgs; the widget hides when dismissed or 403. | Fixed (verified: OnboardingChecklistService.cs:96-99 creates LEGACY checklists Dismissed; FE-B widget gated on ONBOARDING_VIEW and hidden when dismissed or on error) |
| REV50-5 | SEED-OPS | Low | Numbering clash check does not reserve JE (journals, same generator) or other unlisted generator prefixes, so two kinds can share one counter. | Fixed (verified: ReservedPrefixes JE/REC, NumberingSetupSeeder.cs:59,123). Residual: an A47 per-journal-type sequence prefix is not checked. |
| REV50-6 | SEED-FIN | Medium | TAX/PAYMENT_TERMS Full refused inside the seeder (custom or reused record referenced) returns warnings only. The orchestrator reports Applied/FullReset (expected 409). CheckReferencesAsync must cover all that Full deletes. | Fixed per SEED-FIN (CheckReferencesAsync covers everything Full deletes in TAX/PAYMENT_TERMS/FISCAL/COA; the backstop throws ConflictException, e.g. PaymentTermsSetupSeeder.cs:238). To be confirmed by QA TS-50-010 in the final round. |
| REV50-7 | SEED-FIN | Medium | CURRENCY re-apply / Merge re-activates currencies the admin deactivated and re-sets the base currency, which changes custom data. | Fixed (verified: CurrencySetupSeeder firstOrOverwrite gate) |
| REV50-8 | SEED-FIN + CORE | Medium | Org-admin payloadOverrides reach global inserts (lookups.Currencies via CreateCurrency, lookups.PaymentTerms), so one tenant's text shows to all. Restrict global inserts to the resolved default payload. | Fixed (verified: PayloadIsOverride/DefaultPayload in SetupSeedContext; CURRENCY GlobalAllowed; PaymentTermsSetupSeeder.cs:115; reset path TenantSetupService.cs:449) |
| REV50-9 | SEED-FIN | Medium | GL_OPENING_BALANCE: (a) a duplicate account inside the file passes validation, and if the duplicates fall in different 100-row batches, batch 1 saves and batch 2 throws, leaving the draft unbalanced; (b) a row whose account already has a draft only sets Existing, so it is counted in the balance but skipped by A40. Posting then plugs the difference to 3900 (defeats BR-50-011). | Fixed (verified: DUPLICATE_IN_FILE and DRAFT_EXISTS errors, GlOpeningBalanceImportTarget.cs:121,143; Accounting 386/386 run by REV) |
| REV50-10 | TPL | High (build) | SMS.Modules.Demand SalesDocumentServices.cs(225,30) CS0172: Demand, Inventory and Logistics do not compile, so their suites cannot run. TPL is still working on it. | Fixed (verified: everything builds; REV ran Inventory 566/566 and Logistics 2237/2237) |
| REV50-11 | TPL | Low | Finance test ReceivablesControllerTests.The_controllers_and_their_actions_are_actually_found expects 15 actions and finds 17 (new Email + DownloadReceipt). Same issue in Demand: SaleQuotationsControllerPermissionTests expects 14 actions and finds 16 (REV50-11b). | Finance part fixed per TPL (count 17 + permission InlineData). Demand part (11b) open. |
| REV50-12 | TPL | Medium | Opening the Document Templates list (ListAsync → EnsureLegacyPoCopyAsync) copies the legacy PO BodyHtml into a PurchaseOrder template. From then on PO PDFs use TemplatedDocumentRenderer instead of the pre-A50 letter layout, so existing PO template users' PDFs change without an edit. | Fixed (verified: TemplateSupport.FromLegacy copies with BodyHtml/FooterHtml null, TemplateSupport.cs:196-197; byte-for-byte golden test A50SalesDocumentTests; D-69) |
