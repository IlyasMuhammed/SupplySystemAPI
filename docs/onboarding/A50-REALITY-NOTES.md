# A50 — reality-check notes (collected 2026-11-09, before A50 starts; A49 is finished first)

Spec: `Documents/SMS_FSD_Addendum_50.md` + `Documents/SMS_Task_Register_Addendum_50_Checklist.md`.

## Finance / Accounting

### Chart of accounts (A42)
- **Templates already exist.** They are `CoaTemplate` + `CoaTemplateItem` (Accounting module, schema `finance`).
  - **Country** is held as ISO alpha-3 (null = global). The **industry** axis is `BusinessType`: General / Services / NonProfit / Retail / Manufacturing.
  - **Account roles** are `IsDefaultAccount` + `DefaultAccountKey` on each item.
- **Embedded JSON** (`src/SMS.Modules.Accounting/Templates/`) is seeded at every start by `TemplateSeeder`, which re-seeds when a file's version changes.
  - Only GLOBAL_GENERAL (104), PK_GENERAL (110) and US_GENERAL (110) have accounts.
  - AE_GENERAL, SA_GENERAL and 11 others are headers only.
- **No super-admin CRUD/upload for templates.**
- **Applying a template** (`POST api/finance/chart-of-accounts/apply-template`) is a FULL REPLACE of the chart, not a merge. It is allowed only before a fiscal year exists and before posted journals.
- **AccountingDefaults:** 16 role keys (`GlCodes.DefaultKeys`) plus 16 HR keys when HRM is on. Rows are created lazily by `AccountingInitializer`; Accounting has no `IOrganizationProvisionedHandler`.
- **Fiscal year:** `FiscalYearService.CreateAsync` (max 12 months, monthly periods + period 13).
- **Setup wizard:** a 6-step GL wizard already exists (`api/finance/setup`, FE `pages/finance/gl/setup-wizard`).
  - States: NotStarted → AccountsCreated → FiscalYearSet → DefaultsSet → BalancesEntered → Complete.
  - Reset is allowed until a non-opening journal exists.
- **Opening balances:** `api/finance/opening-balances`. Posting writes one OPENING_BALANCE journal with a plug to 3900, dated at the A40 opening balance date, and can be posted only once.

### Tax, currency, payment terms
- **Tax codes:** `finance.tax_codes` is per org (Code unique per org, RatePercent, Usage, IsDefault). No system flag, and **no default seeding**.
  - Lookups still seeds a legacy global LookupValue "tax-code".
- **Currencies:**
  - The global catalog is `lookups.Currencies` (18).
  - The per-org list is `finance.org_currencies`, seeded with all 18 by `CurrencyBootstrapper` (an `IOrganizationProvisionedHandler`; it ignores country).
  - Base currencies are in `tenant.organization_currency_settings` (falls back to PKR). A base change is refused once documents exist.
- **Payment terms:** `lookups.PaymentTerms` is GLOBAL (Name, Days) and never seeded.
  - There is also a LookupValue "payment-terms" (global + org rows).
  - A48 `EarlyPaymentTerm` is per org and not seeded.

### Markers and org fields
- **No `IsSeeded` / `SeededVersion` anywhere in finance.** The precedent is HRM's `HrmSettings.SeedVersion`, with `IsSeeded` on leave types, holidays and expense categories.
- **No org Industry field.** `Organization.Country` is a free string.

### Opening sub-ledgers
- A48 opening AR/AP are OPENING-source documents imported by A40 targets.
- Opening stock is product-ledger OPENING_STOCK.

## Import, documents, email, events, demo, files, UI (agent a08dd2157355cc1bc)

### Import (P2 is ~already built)
- **SMS.Modules.Import (A40)** already does almost all of P2.
  - 14 IImportTarget: UNIT, PRODUCT_CATEGORY, PRODUCT, WAREHOUSE, WAREHOUSE_LOCATION, TAX_CODE, CUSTOMER, SUPPLIER, SUPPLIER_CONTACT, PRICING_RULE (= price lists), OPENING_STOCK, CUSTOMER_OPENING_BALANCE, SUPPLIER_OPENING_BALANCE, HRM_EMPLOYEE.
  - XLSX templates (Data/Instructions/Lookups sheets); XLSX+CSV upload; user column mapping with 4-tier auto-map (exact/normalized/alias/fuzzy) + saved presets.
  - Validate-before-execute, duplicate strategy, history (`GET api/import/sessions`), error/results workbooks, background jobs + SignalR progress.
  - Limits are 50,000 rows / 10 MB (upload cap 12 MB); the FSD says 50 MB.
  - FE `pages/settings/import/*`.
- **Gap: no GL opening-balance (trial balance) import target.** GL opening balances are entered on the `api/finance/opening-balances` JSON page only.

### Document PDFs
- **QuestPDF, colours hardcoded.** Existing PDFs: sales invoice, PO, packing list (delivery note), gate pass, customer/vendor statement, supplier invoice.
- **Gaps:** no quotation PDF and no customer payment receipt PDF.
- **Branding store:** only `PoDocumentTemplate` (Lookups; one per org; letterhead, logo URL, BodyHtml with `{{Token}}` for PO only, BankDetails, footer). Admin UI `pages/po-document-template`.
  - No colour, no per-doc-type templates, no org logo on Organization.
- **PdfRenderGate missing** on SalesInvoice/PO/Delivery renderers. Don't copy that gap.

### Email
- **IEmailSender** (SMTP/SendGrid). Business emails are static C# strings.
- **No email template entity, admin UI or send-test.**
- **Closest editable pattern:** A48 dunning templates (`DunningTemplateEngine`, `{Token}`, HTML-encoded).
- **Missing business emails:** invoice-to-customer, quotation-to-customer, PO-to-supplier email (send = WhatsApp), welcome.

### Events
- **No global bus.**
  - MediatR exists only in WorkflowEngine (document approval events).
  - Module-local publishers: ICrmEventPublisher, IAccountingEventPublisher.
  - IEntitySyncGateway after-commit pushes for customer/vendor/product: a new module can register an IEntitySyncTarget to hear them.
  - IOrganizationProvisionedHandler fires on org create.
- **No SaleOrderCreated / PurchaseOrderCreated / UserCreated / QuickBooksConnected events.** Use hooks or state polling: QBO connection status, IUserQueryService, counts.

### Demo data
- **No IsDemo, no sample loader.** Only the seeded SCM-DEMO org.

### Files
- **Logo upload pattern:** the main app stores files on local disk via `api/attachments/upload` + `AttachmentAccessPolicy` interface codes (e.g. PO_TEMPLATE_LOGO). Use that for logos.
- SMS.FileStore (Azure Blob) is a separate service, not referenced by SMS.API.

### Frontend
- **Dashboard:** `pages/dashboard/dashboard.ts`, route `/portal/dashboard`.
- **Menu:** `layout/component/app.menu.ts`. Settings has groups Sales & Fulfilment, Finance Setup, Documents (PO templates), System (… Data Import), Integrations.
  - **No "Settings > Organization" group.** Organizations is super-admin only.

## Tenancy, roles, workflow, numbering, lookups, warehouses, HRM (agent a2658d768a140cbef)

### Organization creation
- **Endpoint:** `POST api/system/organizations`, in one transaction. It creates the org, plan features (or chosen modules + deps), the org admin (ORG_ADMIN, invite email + token) and the default workflows.
- **After commit, these `IOrganizationProvisionedHandler`s run:**
  - currency settings;
  - CurrencyBootstrapper (18 currencies, ignores country);
  - ArApDefaultsSeeder;
  - 10 rejection reasons;
  - 5 fulfillment routes;
  - scorecard weights;
  - walk-in customer;
  - CRM sources/stages.
- **No handler** in Accounting (lazy), HRM, Inventory, Lookups or Auth roles.
- **Organization:** `tenant.Organizations`, Guid Id, Country is a free string. **No Industry, no SetupStatus.**

### Modules
- **Registry:** TenancyFeatureCatalog / OrganizationFeatures (IsLicensed vs IsEnabled).
- **Checking a module:** `IModuleGate.IsEnabledAsync`, `[RequiresFeature]`, `ITenantSnapshotProvider`.
- **Deployment profiles already exist** (`ModuleProfileService`: P2, P2-S, P3, P-PK; `api/tenant/module-profiles`, `apply-profile`). The FE does not use them.
- **Org-admin API:** `api/tenant/modules`.

### Roles and users
- **Roles are GLOBAL only** (seeded IsGlobal=true). Org custom roles exist only via `api/roles` at runtime.
  - RolePermission grants are shared by all orgs.
  - **No per-org role seeding.**
  - 18 role codes in AuthDataSeeder.
- **User invite:** only the org-admin invite exists. Org admins adding users create active users with a temporary password emailed.

### Workflows
- **Seeded per org at creation:** PR, PO (4 tiers at 10k/50k/200k, hard-coded, not currency-aware), GRN_QC, GRN, MIR_PROJECT, MIR_GENERAL.
- **HRM workflows** are seeded on first use. **No SO/invoice workflows.**

### Document numbering
- **Shared generator:** `IDocumentNumberGenerator` (Logistics, `logistics.document_number_sequences`, PREFIX-YYYY-NNNNN, prefix hard-coded by callers).
- **Many documents still COUNT+1** (GRN, PR, quotation, invoices, payments…).
- **No configurable per-org prefixes**, except GL journal types, POS and HRM employee number.

### Lookups
- Countries, Currencies, PaymentTerms, Cities and DeliveryTerms are **global**. LookupValues are global or org-owned.
- **UoM** = LookupValue type `uom` (10 global values). No UoM entity and no conversions.
- **Product categories** are per org in Inventory; seeded only for the demo org.

### Warehouses
- Inventory Warehouses → Zones → Racks → Shelves → Bins. **No defaults seeded.** Import targets exist.

### Org settings
- `tenant.OrganizationSettings` holds only AckLinkExpiryDays. Currency settings, Finance and HRM have separate settings.

### Existing wizards
- **No tenant-wide wizard or checklist.**
- Module wizards exist: GL setup (A42), HRM setup (`hrm/setup`), and import/migration (`settings/import/import-migration`).

### Super admin UI
- `organizations` (create with module picker), `organizations/:id/features`, `system/quickbooks-app`.
- Menu group "System Administration" (superAdminOnly).

### HRM defaults (A49)
- **`HrmSeeder`:** per org, insert-only by key, `SeedVersion` 2. Seeds leave types, shift, PK holidays, 19 salary components, PK tax slab and statutory rates (UNVERIFIED), expense categories, letters, structure and workflows.
- **PK only.** Seeded lazily / at startup for HRM orgs, never at org creation.
- **2026-11-10 (HRM-50):** HRM is now also a wizard category (`HrmSetupSeeder`, D-90). The expense approval rule, HR roles, PayslipSent email and checklist steps are built (D-91..D-94).
