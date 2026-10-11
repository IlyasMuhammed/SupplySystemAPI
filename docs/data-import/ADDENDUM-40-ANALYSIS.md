# Addendum 40 — Data Import & Upload Framework: reality check & proposed tasks

Spec: SMS-FSD-ADD-040 v1.0 (C1–C9) · Register: 353 tasks · Status: **analysis only — nothing built**.

## Part A — the 16 import targets vs. this system

| # | Spec entity (target) | Exists here as | Verdict |
|---|---|---|---|
| 0 | Customer (`customers.Customers`) | `suppliers.BusinessPartners` IsCustomer (A37 facade `api/customers`, codes C-00001) | ✔ build |
| 1 | Supplier (`procurement.Suppliers`) | BusinessPartners IsVendor (onboarding status PENDING→APPROVED) | ✔ build (imported vendors land PENDING unless "approved on import" option — discuss) |
| 2 | Product (`inventory.Products`) | Products + mandatory default **variant** (SKU, barcode, selling/purchase price on the variant) | ✔ build (one default variant per row; variants import later) |
| 3 | Product category (parent hierarchy) | ProductCategories + ProductSubCategories (2 fixed levels, no parent tree) | ✔ build mapped: parent_code ⇒ sub-category |
| 4 | Unit of measure (`inventory.Units`) | Lookups `LookupValues` type `uom`; Product.UomCode free string; no ratio/conversion | ✔ build (code + name; ratio/category ✕) |
| 5 | Warehouse | `inventory.Warehouses` | ✔ build |
| 6 | Warehouse location | Inventory `Zone` + `Bin` | ✔ build mapped: zone_code + bin_code per warehouse |
| 7 | Chart of accounts | **No GL / accounts** (QuickBooks holds the GL) | ✕ drop |
| 8 | Tax code | Finance `TaxCode` (code, rate, usage) — no linked accounts | ✔ build (account columns ✕) |
| 9 | Contact | `SupplierContact` only (customers have one inline contact) | ◐ supplier contacts only |
| 10 | Price list item | **No price lists**; `PricingRule` (variant, partner, price type, qty band, dates) | ◐ build as pricing-rule import |
| 11 | Employee | **No HR module**; users are auth accounts | ✕ drop (user import would be a separate, security-sensitive feature) |
| 12 | Opening stock | Stock adjustment path + **Finance product ledger IN** (needed so later sales can cost COGS) | ✔ build (no journal — R) |
| 13 | Customer opening balance | Customer ledger (`OPENING_BAL` entry type exists, unused) + an "opening invoice" so it shows in receivables/aging and can be paid | ✔ build |
| 14 | Supplier opening balance | Supplier ledger + payables invoice equivalent | ✔ build |
| 15 | GL opening balance | No GL | ✕ drop |

Other spec assumptions vs. reality:
- React UI → Angular + SMS Flow kit; `/settings/import` → `/portal/pages/settings/import`.
- `system.Modules / FeatureFlags` → the A37 registry: `MODULE_DATA_IMPORT` (core, always on) + `FEATURE_OPENING_BALANCE_IMPORT`.
- Dotted permissions → UPPER_SNAKE: `IMPORT_PROFILE_VIEW`, `IMPORT_PROFILE_MANAGE`, `IMPORT_UPLOAD`, `IMPORT_EXECUTE`, `IMPORT_MANAGE`, `IMPORT_OPENING_BALANCE`.
- bigint ids / FKs to auth.Users / org bigint → int Id + Guid UUID, Guid OrganizationId, cross-module Guid refs.
- ClosedXML already referenced (0.104.2); CsvHelper is new. Uploaded files: keep out of `wwwroot` (the earlier upload-restarts-API issue) — parse in memory, store rows only.
- Journal entries for opening balances → none; ledgers + documents instead (as A38/A39).
- Each target lives in a different module, so the importer cannot write their tables directly: one shared `IImportTarget` interface per entity, implemented **inside the owning module** (validation + create/update through that module's own rules), and a new module `SMS.Modules.Import` owning profiles, sessions, rows, mappings, external references, templates, validation, execution, Hangfire job and hub.

## Part B — proposed tasks (what would be built)

1. **Import module foundation (Import)** — schema `import`: profiles + fields (seeded in code for the 12 kept entity types, reconciled at startup like the A37 catalog), sessions, column mappings, rows (JSON), external references; profile list/detail APIs.
2. **Upload & parsing (Import)** — `.xlsx` (ClosedXML) and `.csv` (CsvHelper) parsing, file rules IMP-FILE-01..08 (type, 10 MB, 50 000 rows, header, empty, sheet choice, immutability, one active import per entity per org).
3. **Column mapping (Import)** — 4-tier auto-mapping (exact / normalized / alias incl. SAP-style names / fuzzy Levenshtein ≥0.70), external_id aliases, rules IMP-MAP-01..04, saved presets.
4. **Templates (Import + owners)** — per-profile .xlsx with Data / Instructions / hidden Lookups sheets, required headers red, dropdowns from live org data (categories, units, warehouses, tax codes, partners), migration workbook with the kept sheets in dependency order.
5. **Validation engine (Import + owners)** — generic checks (required, type, length, range, precision, regex, email, phone, boolean/date/decimal parsing, trimming, in-file duplicates) + lookup cache + per-entity checks and DB-duplicate detection asked from the owning module; error list + error-report .xlsx.
6. **Execution (Import + owners)** — batches of 100 in a transaction per batch, duplicate strategies (Skip / Update / CreateNew / Error), external-reference upsert, audit user, ≤500 rows synchronous else Hangfire `import` queue job, SignalR `/hubs/import` progress, cancel.
7. **Entity import targets (owning modules)** — Suppliers module: customers, suppliers, supplier contacts; Inventory: products (+default variant), categories/sub-categories, warehouses, zones/bins, pricing rules, opening stock (+ product ledger via Finance seam); Lookups: units; Finance: tax codes, customer and supplier opening balances.
8. **Opening balance rules** — IMP-OB-01/03/04/05/06 (permission, one opening per product+warehouse, quantity ≥ 0, opening date = org setting "opening balance date" (new), no updates); GL equation rule ✕ (no GL).
9. **Permissions, roles, module switch** — 6 permissions; role grants mapped to existing roles (data entry ≈ Requester/Purchase Officer: view+upload; Finance Officer/Manager: + execute + opening balances; Org Admin: all).
10. **Frontend (Angular)** — Import dashboard (entity cards, opening-balances section, recent imports), 4-step wizard (upload, mapping with confidence marks, preview + validate + error report, results with live progress), migration wizard checklist, session detail, menu "Data Import" under Settings.
11. **QA** — LocalDB HTTP end-to-end over TS-040-01..30 (minus GL/accounts/employee ones) + regression.

## Decisions needed before building
1. **Dropped entity types**: chart of accounts, GL opening balance, employees — confirm they are out (no GL / HR here).
2. **Imported suppliers**: land as APPROVED (ready to use) or PENDING (go through supplier onboarding approval)?
3. **Opening balances**: customer/supplier opening balances become real open documents (payable/receivable, appear in aging, can be paid/allocated) — confirm; and the "opening balance date" setting per org.
4. **Price list items** → pricing rules (selling price per variant, optional customer, quantity band, dates) — confirm.

## Residuals (expected)
- R-1 No journal entries for opening stock/balances (no GL); QuickBooks receives opening documents only if they are normal invoices/bills.
- R-2 Product import creates one default variant; multi-variant import is a later step.
- R-3 Units have no conversion ratios in this system.
