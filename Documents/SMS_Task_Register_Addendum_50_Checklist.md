# SMS — Task Register & Checklist

## Addendum 50: Tenant Onboarding, Default Configuration & Data Import

| Property | Value |
|---|---|
| Addendum | 50 |
| Total Tasks | 312 |
| Phases | 4 (P1, P2, P3, P4) |
| Estimated Effort | 40 days (8 weeks) |
| Depends On | A37 (Module Registry), A42 (CoA), A44 (Posting Gateway), A47 (GL Engine) |

### Task Categories

| Code | Category | Count |
|---|---|---|
| BE | Backend (Entities, Services, Logic) | 132 |
| DB | Database (Migrations, Indexes, Seeds) | 48 |
| FE | Frontend (UI Components, Pages) | 82 |
| QA | Quality Assurance (Unit, Integration, E2E) | 40 |
| DOC | Documentation | 10 |

---

## Phase P1 — Foundation: Setup Wizard & Industry Templates (15 days)

### P1-A. SetupDefaults Entity & Repository (2 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 001 | Create `SetupDefault` entity in `SMS.Core.Entities` | BE | 0.5h | ☐ |
| 002 | Define properties: `CountryCode`, `IndustryType`, `DataCategory`, `DataPayload` (NVARCHAR MAX JSON) | BE | 0.5h | ☐ |
| 003 | Define: `DisplayName`, `Version` (auto-increment on update), `IsActive`, `Notes` | BE | 0.25h | ☐ |
| 004 | Define audit fields: `CreatedAt`, `CreatedBy`, `ModifiedAt`, `ModifiedBy` | BE | 0.25h | ☐ |
| 005 | Configure UQ constraint `UQ_SD_Key` on `(CountryCode, IndustryType, DataCategory)` | DB | 0.25h | ☐ |
| 006 | Write migration `M50_001_CreateSetupDefaults` | DB | 0.25h | ☐ |
| 007 | Create `SetupDefaultDto`, `CreateSetupDefaultRequest`, `UpdateSetupDefaultRequest` DTOs | BE | 0.5h | ☐ |
| 008 | Implement `ISetupDefaultsRepository` — CRUD | BE | 0.5h | ☐ |
| 009 | Implement fallback lookup: exact → country+GLOBAL → GLOBAL+industry → GLOBAL+GLOBAL | BE | 1h | ☐ |
| 010 | Unit test: fallback chain resolves correctly at each priority level | QA | 0.5h | ☐ |
| 011 | Unit test: UQ rejects duplicate country+industry+category | QA | 0.25h | ☐ |

### P1-B. TenantSetupLog & Organization Extensions (1 day)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 012 | Create `TenantSetupLog` entity | BE | 0.25h | ☐ |
| 013 | Define: `OrgId`, `CountryCode`, `IndustryType`, `DataCategory`, `DefaultsVersion`, `RecordsSeeded`, `Action`, `AppliedAt`, `AppliedBy` | BE | 0.25h | ☐ |
| 014 | Write migration `M50_002_CreateTenantSetupLog` | DB | 0.25h | ☐ |
| 015 | Write migration `M50_004_AlterOrganizations` — add `IndustryType`, `CountryCode`, `SetupStatus`, `SetupStep`, `SetupCompletedAt`, `SetupCompletedBy` | DB | 0.5h | ☐ |
| 016 | Add CHECK constraint `CK_Org_SetupStatus` — `IN ('NotStarted','InProgress','Completed','Skipped')` | DB | 0.25h | ☐ |
| 017 | Create `SetupState` DTO for wizard progress tracking | BE | 0.25h | ☐ |

### P1-C. IsSeeded Flags on Existing Tables (1 day)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 018 | Write migration `M50_005_AddIsSeededFlags` | DB | 0.5h | ☐ |
| 019 | Add `IsSeeded BIT DEFAULT 0`, `SeededAt DATETIME2`, `SeededVersion INT` to `finance.ChartOfAccounts` | DB | 0.25h | ☐ |
| 020 | Add same columns to `finance.TaxCodes` | DB | 0.25h | ☐ |
| 021 | Add same columns to `finance.PaymentTerms` | DB | 0.25h | ☐ |
| 022 | Add same columns to `inventory.UnitsOfMeasure` | DB | 0.25h | ☐ |
| 023 | Add same columns to `inventory.ProductCategories` | DB | 0.25h | ☐ |
| 024 | Add same columns to `core.ApprovalWorkflowRules` | DB | 0.25h | ☐ |
| 025 | Add same columns to `core.SequenceDefinitions` | DB | 0.25h | ☐ |
| 026 | Add same columns to `core.Roles` | DB | 0.25h | ☐ |
| 027 | Verify all migrations run in order and rollback cleanly | DB | 0.5h | ☐ |

### P1-D. ISetupDefaultsService — Super Admin (1.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 028 | Define `ISetupDefaultsService` interface | BE | 0.25h | ☐ |
| 029 | Implement `ListAsync` with country/industry/category filters | BE | 0.5h | ☐ |
| 030 | Implement `GetAsync(id)` | BE | 0.25h | ☐ |
| 031 | Implement `CreateAsync` — validate JSON payload schema per category | BE | 1h | ☐ |
| 032 | Implement `UpdateAsync` — auto-increment Version on change | BE | 0.5h | ☐ |
| 033 | Implement `DeleteAsync` — prevent if any tenant has applied this version | BE | 0.5h | ☐ |
| 034 | Implement `ImportFromJsonAsync` — bulk import country+industry pack | BE | 1h | ☐ |
| 035 | Implement `ExportAllAsync` — full export as JSON | BE | 0.5h | ☐ |
| 036 | Implement `ResolvePayloadAsync` — lookup with fallback chain | BE | 0.5h | ☐ |
| 037 | Register in DI | BE | 0.25h | ☐ |
| 038 | Unit test: create, update (version increments), delete guard | QA | 0.5h | ☐ |
| 039 | Unit test: bulk import validates and seeds correctly | QA | 0.5h | ☐ |

### P1-E. ITenantSetupService — Core Wizard Logic (3 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 040 | Define `ITenantSetupService` interface | BE | 0.25h | ☐ |
| 041 | Implement `GetAvailableCountriesAsync` — distinct countries from SetupDefaults | BE | 0.25h | ☐ |
| 042 | Implement `GetAvailableIndustriesAsync(countryCode)` — distinct industries for country | BE | 0.25h | ☐ |
| 043 | Implement `PreviewDefaultsAsync` — count records per category without seeding | BE | 1h | ☐ |
| 044 | Implement `LoadDefaultsAsync` — private method, loads all JSON payloads with fallback chain | BE | 1h | ☐ |
| 045 | Implement `ApplyDefaultsAsync` — master orchestrator | BE | 1h | ☐ |
| 046 | Implement `SeedCurrenciesAsync` — parse CURRENCY payload, insert into `finance.Currencies` | BE | 0.5h | ☐ |
| 047 | Implement `SeedChartOfAccountsAsync` — parse COA payload, insert tree with parent resolution, set account roles | BE | 1.5h | ☐ |
| 048 | Implement `SeedAccountRoleMappingsAsync` — map role tags from COA to `finance.AccountRoleDefaults` | BE | 0.5h | ☐ |
| 049 | Implement `SeedTaxCodesAsync` — parse TAX payload, insert into `finance.TaxCodes` | BE | 0.5h | ☐ |
| 050 | Implement `SeedFiscalYearAsync` — create fiscal year + monthly/quarterly periods | BE | 0.5h | ☐ |
| 051 | Implement `SeedPaymentTermsAsync` | BE | 0.25h | ☐ |
| 052 | Implement `SeedUnitsOfMeasureAsync` — parse groups, insert units with conversion factors | BE | 0.5h | ☐ |
| 053 | Implement `SeedProductCategoriesAsync` — parse hierarchy, insert with parent resolution | BE | 0.5h | ☐ |
| 054 | Implement `SeedDocumentNumberingAsync` — create sequence definitions per document type | BE | 0.5h | ☐ |
| 055 | Implement `SeedApprovalRulesAsync` — create rules per document type with amount levels | BE | 0.5h | ☐ |
| 056 | Implement `SeedRolesAsync` — filter by enabled modules, create roles + permissions | BE | 1h | ☐ |
| 057 | Implement `SeedWarehousesAsync` — create warehouses from admin input + zones from defaults | BE | 0.5h | ☐ |
| 058 | Implement `SeedHrmDefaultsAsync` — leave types, salary components, tax slabs (only when HRM enabled) | BE | 0.5h | ☐ |
| 059 | Implement `InviteUsersAsync` — create user invitations from wizard Step 7 input | BE | 0.5h | ☐ |
| 060 | Implement `LinkQuickBooksAsync` — connect QuickBooks if selected in wizard | BE | 0.25h | ☐ |
| 061 | Implement `UpdateOrgSetupStatusAsync` — mark org as Completed/Skipped | BE | 0.25h | ☐ |
| 062 | Implement `LogSetupAsync` — write TenantSetupLog for every category seeded | BE | 0.25h | ☐ |
| 063 | Implement idempotency guard — check if record already exists before inserting (by code/key per org) | BE | 0.5h | ☐ |
| 064 | Mark all seeded records: `IsSeeded = 1`, `SeededAt`, `SeededVersion` | BE | 0.25h | ☐ |
| 065 | Implement `GetSetupStateAsync` — return current wizard step and progress | BE | 0.25h | ☐ |
| 066 | Implement `SaveStepProgressAsync` — save step data as JSON per step | BE | 0.25h | ☐ |
| 067 | Register in DI | BE | 0.25h | ☐ |
| 068 | Unit test: full apply for PK+Trading seeds all categories correctly | QA | 1h | ☐ |
| 069 | Unit test: apply twice → no duplicates (idempotent) | QA | 0.5h | ☐ |
| 070 | Unit test: CoA tree parent resolution correct | QA | 0.5h | ☐ |
| 071 | Unit test: roles filtered by enabled modules (HRM roles skipped when HRM off) | QA | 0.25h | ☐ |
| 072 | Unit test: skip wizard → GLOBAL defaults applied | QA | 0.25h | ☐ |

### P1-F. Reset Category Logic (1 day)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 073 | Implement `ResetCategoryAsync(orgId, category, mode, resetBy)` | BE | 1h | ☐ |
| 074 | Implement Merge mode — find missing defaults, insert only those, keep custom + modified | BE | 1h | ☐ |
| 075 | Implement Overwrite mode — delete seeded records (IsSeeded=1), re-seed, keep custom (IsSeeded=0) | BE | 1h | ☐ |
| 076 | Implement Full Reset mode — validate no seeded records are referenced, delete all, re-seed | BE | 1h | ☐ |
| 077 | Implement reference check — query documents that use a record before allowing full reset | BE | 0.5h | ☐ |
| 078 | Implement `GetCategoryStatusesAsync` — return seeded/current counts and modification status per category | BE | 0.5h | ☐ |
| 079 | Unit test: merge adds missing, keeps custom | QA | 0.5h | ☐ |
| 080 | Unit test: overwrite replaces seeded, keeps custom | QA | 0.5h | ☐ |
| 081 | Unit test: full reset blocked when records in use | QA | 0.5h | ☐ |

### P1-G. Seed Pakistan Trading Template Data (1.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 082 | Write migration `M50_010_SeedPakistanTradingDefaults` | DB | 0.5h | ☐ |
| 083 | Prepare COA JSON: 45 accounts, full trading CoA with account roles tagged | DB | 1h | ☐ |
| 084 | Prepare TAX JSON: GST 18%, FED 16%, WHT on Goods 4.5%, WHT on Services 8%, Input Tax 18%, Exempt | DB | 0.5h | ☐ |
| 085 | Prepare CURRENCY JSON: PKR (base) + USD, AED, SAR, GBP, EUR | DB | 0.25h | ☐ |
| 086 | Prepare FISCAL JSON: Jul start, monthly periods | DB | 0.25h | ☐ |
| 087 | Prepare PAYMENT_TERMS JSON: COD, Net 15/30/60, 2/10 Net 30 | DB | 0.25h | ☐ |
| 088 | Prepare UOM JSON: 16 units in 4 groups (Weight, Count, Volume, Length) | DB | 0.25h | ☐ |
| 089 | Prepare PRODUCT_CATEGORIES JSON: 8 trading categories | DB | 0.25h | ☐ |
| 090 | Prepare APPROVAL_RULES JSON: PO 3-level, Expense 2-level | DB | 0.25h | ☐ |
| 091 | Prepare DOC_NUMBERING JSON: SO, PO, INV, QT, GRN, PV patterns | DB | 0.25h | ☐ |
| 092 | Prepare ROLES JSON: 8 roles with permissions per module | DB | 0.5h | ☐ |
| 093 | Prepare WAREHOUSE JSON: Receiving, Storage, Dispatch, Quality Hold | DB | 0.25h | ☐ |
| 094 | Verify all PK+Trading payloads parse correctly | QA | 0.5h | ☐ |

### P1-H. Seed Additional Template Data (1 day)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 095 | Write migration `M50_011_SeedUAETradingDefaults` | DB | 0.25h | ☐ |
| 096 | Prepare AE TAX JSON: VAT 5%, Zero-Rated, Input VAT | DB | 0.25h | ☐ |
| 097 | Prepare AE CURRENCY JSON: AED (base) + USD, GBP, EUR, INR | DB | 0.25h | ☐ |
| 098 | Prepare AE FISCAL JSON: Jan start, monthly | DB | 0.25h | ☐ |
| 099 | Prepare AE COA JSON: 38 accounts for UAE trading | DB | 0.5h | ☐ |
| 100 | Write migration `M50_012_SeedSaudiTradingDefaults` | DB | 0.25h | ☐ |
| 101 | Prepare SA TAX JSON: VAT 15%, Zero-Rated, Input VAT | DB | 0.25h | ☐ |
| 102 | Prepare SA CURRENCY JSON: SAR (base) + USD, AED, GBP, EUR | DB | 0.25h | ☐ |
| 103 | Prepare SA FISCAL JSON: Jan start, monthly | DB | 0.25h | ☐ |
| 104 | Write migration `M50_013_SeedGlobalDefaults` | DB | 0.25h | ☐ |
| 105 | Prepare GLOBAL COA JSON: 28 accounts — minimal skeleton | DB | 0.5h | ☐ |
| 106 | Prepare GLOBAL PAYMENT_TERMS, UOM, ROLES JSON | DB | 0.5h | ☐ |
| 107 | Verify all template packs load via fallback chain | QA | 0.5h | ☐ |

### P1-I. Setup Wizard API Endpoints (1 day)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 108 | Create `SetupController` | BE | 0.25h | ☐ |
| 109 | Endpoint 1: `GET /api/v1/setup/countries` | BE | 0.25h | ☐ |
| 110 | Endpoint 2: `GET /api/v1/setup/industries?country={code}` | BE | 0.25h | ☐ |
| 111 | Endpoint 3: `POST /api/v1/setup/preview` | BE | 0.25h | ☐ |
| 112 | Endpoint 4: `POST /api/v1/setup/apply` | BE | 0.5h | ☐ |
| 113 | Endpoint 5: `GET /api/v1/setup/state` | BE | 0.25h | ☐ |
| 114 | Endpoint 6: `POST /api/v1/setup/state/{step}` | BE | 0.25h | ☐ |
| 115 | Endpoint 7: `GET /api/v1/setup/categories` | BE | 0.25h | ☐ |
| 116 | Endpoint 8: `POST /api/v1/setup/categories/{category}/reset` | BE | 0.5h | ☐ |
| 117 | Add `[Authorize(Policy = "Org.Admin")]` on all setup endpoints | BE | 0.25h | ☐ |
| 118 | Request validation: FluentValidation for all POST bodies | BE | 0.5h | ☐ |

### P1-J. Super Admin API Endpoints (0.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 119 | Create `AdminSetupDefaultsController` | BE | 0.25h | ☐ |
| 120 | Endpoints 34-40: CRUD + import + export for SetupDefaults | BE | 1h | ☐ |
| 121 | Add `[Authorize(Policy = "Platform.SuperAdmin")]` — no Org Admin access | BE | 0.25h | ☐ |
| 122 | Request validation on all POST/PUT bodies | BE | 0.25h | ☐ |

### P1-K. Setup Wizard UI (3 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 123 | Create `SetupWizard` React component — multi-step container with progress bar | FE | 1h | ☐ |
| 124 | Step 1: Company & Industry — company name input, country dropdown, industry dropdown | FE | 1h | ☐ |
| 125 | Country dropdown populated from `GET /setup/countries` | FE | 0.25h | ☐ |
| 126 | Industry dropdown populated from `GET /setup/industries?country=PK` on country change | FE | 0.25h | ☐ |
| 127 | Step 2: Modules — toggle switches for each module with one-line descriptions | FE | 1h | ☐ |
| 128 | Step 3: Chart of Accounts — tree view of pre-filled accounts, inline edit name, add/remove | FE | 2h | ☐ |
| 129 | Account tree drag-and-drop to reorganize hierarchy | FE | 1h | ☐ |
| 130 | Account role tags shown as subtle badges (DEFAULT_BANK, DEFAULT_RECEIVABLE) | FE | 0.5h | ☐ |
| 131 | Step 4: Tax Codes — editable table, inline edit rates, add/remove rows | FE | 1h | ☐ |
| 132 | Step 5: Fiscal Year & Currency — base currency dropdown, additional currencies checkboxes, fiscal start month, period type | FE | 1h | ☐ |
| 133 | Step 6: Warehouses — add warehouse name/address, zone checkboxes from defaults | FE | 1h | ☐ |
| 134 | Add Warehouse button — dynamic form for multiple warehouses | FE | 0.5h | ☐ |
| 135 | Step 7: Users & Roles — invite form (email + role dropdown), add/remove rows | FE | 1h | ☐ |
| 136 | Role dropdown populated from seeded roles (filtered by enabled modules) | FE | 0.25h | ☐ |
| 137 | QuickBooks connection — optional connect button or skip | FE | 0.5h | ☐ |
| 138 | Review & Confirm screen — summary of all selections with record counts | FE | 1h | ☐ |
| 139 | "Launch My System" button → calls `POST /setup/apply` → progress indicator | FE | 0.5h | ☐ |
| 140 | Success screen — "Your system is ready!" with quick-start links | FE | 0.5h | ☐ |
| 141 | "Skip Setup" button on every step → applies GLOBAL defaults | FE | 0.25h | ☐ |
| 142 | Save progress on every step forward/backward → `POST /setup/state/{step}` | FE | 0.5h | ☐ |
| 143 | Resume wizard on return → `GET /setup/state` → jump to last step | FE | 0.5h | ☐ |
| 144 | Auto-redirect to wizard on first login when SetupStatus = 'NotStarted' | FE | 0.5h | ☐ |
| 145 | "Reset to Default" button per step → reloads from preview | FE | 0.25h | ☐ |
| 146 | Responsive layout — wizard works on tablet | FE | 0.5h | ☐ |

### P1-L. Default Data Dashboard UI (1 day)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 147 | Create `DefaultDataDashboard` page — Settings → Organization → Default Data | FE | 1h | ☐ |
| 148 | Show table: Category, Seeded count, Current count, Modified flag, Last Applied date | FE | 0.5h | ☐ |
| 149 | [View] button → navigates to existing editing screen for that category | FE | 0.5h | ☐ |
| 150 | [Reset] button → opens reset dialog | FE | 0.25h | ☐ |
| 151 | Reset dialog: Merge / Overwrite / Full Reset radio options with descriptions | FE | 0.5h | ☐ |
| 152 | Full Reset shows warning if records in use → "X records are referenced by documents" | FE | 0.5h | ☐ |
| 153 | "Run Setup Wizard Again" button → navigates to wizard | FE | 0.25h | ☐ |
| 154 | Show template info: Country, Industry, Applied date at top | FE | 0.25h | ☐ |

### P1-M. Super Admin UI (1 day)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 155 | Create `SetupDefaultsManagement` page — Platform Admin → Configuration → Setup Defaults | FE | 1h | ☐ |
| 156 | List view: country, industry, category, records count, version, status — with filters | FE | 1h | ☐ |
| 157 | Add/Edit form: country dropdown, industry dropdown, category dropdown | FE | 0.5h | ☐ |
| 158 | Category-specific data editor: COA → tree editor | FE | 1h | ☐ |
| 159 | Category-specific data editor: TAX → simple table editor | FE | 0.5h | ☐ |
| 160 | Category-specific data editor: ROLES → role + permission matrix | FE | 1h | ☐ |
| 161 | Category-specific data editor: APPROVAL_RULES → rules with amount levels | FE | 0.5h | ☐ |
| 162 | Category-specific data editor: UOM → grouped units editor | FE | 0.5h | ☐ |
| 163 | Generic JSON editor fallback for other categories | FE | 0.5h | ☐ |
| 164 | Import from JSON button → file upload → preview → confirm | FE | 0.5h | ☐ |
| 165 | Export All button → downloads JSON file | FE | 0.25h | ☐ |
| 166 | Publish / Unpublish toggle (IsActive) | FE | 0.25h | ☐ |

**Phase P1 Subtotal: 166 tasks (79 BE, 31 DB, 46 FE, 10 QA)**

---

## Phase P2 — Data Import Wizard (8 days)

### P2-A. DataImportJobs Entity (0.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 167 | Create `DataImportJob` entity | BE | 0.5h | ☐ |
| 168 | Define: `Uuid`, `OrgId`, `ImportType`, `FileName`, `FileKey`, `Status`, row counts, `ColumnMapping` (JSON), `ErrorLog` (JSON) | BE | 0.5h | ☐ |
| 169 | Configure CHECK constraints on Status and ImportType | DB | 0.25h | ☐ |
| 170 | Configure index `IX_DIJ_Org` on `(OrgId, CreatedAt DESC)` | DB | 0.25h | ☐ |
| 171 | Write migration `M50_006_CreateDataImportJobs` | DB | 0.25h | ☐ |
| 172 | Create `DataImportJobDto`, `ColumnMapping`, `ValidationResult`, `ValidationIssue` DTOs | BE | 0.5h | ☐ |

### P2-B. Import Template Generation (1 day)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 173 | Implement `IDataImportService` interface | BE | 0.25h | ☐ |
| 174 | Implement `GetImportTemplateAsync("Customers")` — generate Excel with headers + sample row + instructions sheet | BE | 1h | ☐ |
| 175 | Implement `GetImportTemplateAsync("Vendors")` — vendor-specific template | BE | 0.5h | ☐ |
| 176 | Implement `GetImportTemplateAsync("Products")` — product template with UoM/Category dropdowns | BE | 0.5h | ☐ |
| 177 | Implement `GetImportTemplateAsync("OpeningBalances")` — account code, name, debit, credit | BE | 0.5h | ☐ |
| 178 | Implement `GetImportTemplateAsync("PriceLists")` — product code, vendor/customer, price, dates | BE | 0.5h | ☐ |
| 179 | Include instructions sheet in every template: required columns, format rules, sample data | BE | 0.5h | ☐ |

### P2-C. Upload & Column Mapping (1.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 180 | Implement `UploadAndValidateAsync` — save file to FileStore, create DataImportJob | BE | 0.5h | ☐ |
| 181 | Parse uploaded Excel/CSV — detect columns, first N rows for preview | BE | 1h | ☐ |
| 182 | Implement auto-mapping: match source columns to target fields by name similarity | BE | 1h | ☐ |
| 183 | Fuzzy match: "Company Name" → BusinessPartner.Name, "Ph" → Contact.Phone | BE | 0.5h | ☐ |
| 184 | Implement `GetColumnMappingsAsync(jobId)` — return current mapping with sample values | BE | 0.25h | ☐ |
| 185 | Implement `UpdateColumnMappingsAsync(jobId, mappings)` — admin adjusts mapping | BE | 0.25h | ☐ |
| 186 | Validate: all required target fields must be mapped | BE | 0.25h | ☐ |

### P2-D. Validation Engine (1.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 187 | Implement `RevalidateAsync(jobId)` — core validation loop | BE | 1h | ☐ |
| 188 | Validate per row: required fields present, types correct | BE | 0.5h | ☐ |
| 189 | Validate: email format check | BE | 0.25h | ☐ |
| 190 | Validate: phone format check | BE | 0.25h | ☐ |
| 191 | Validate: reference fields exist (PaymentTermCode, CategoryCode, UoMCode must match org's data) | BE | 0.5h | ☐ |
| 192 | Validate: uniqueness check (product code not duplicate within file or existing data) | BE | 0.5h | ☐ |
| 193 | Validate (OpeningBalances): total debits = total credits | BE | 0.5h | ☐ |
| 194 | Validate (OpeningBalances): account codes exist in org's CoA | BE | 0.25h | ☐ |
| 195 | Classify each row: Valid, Warning (missing optional field), Error (missing required / invalid) | BE | 0.25h | ☐ |
| 196 | Store validation results in `DataImportJob.ErrorLog` as JSON | BE | 0.25h | ☐ |
| 197 | Unit test: valid file passes, invalid blocked | QA | 0.5h | ☐ |
| 198 | Unit test: opening balances must balance | QA | 0.25h | ☐ |

### P2-E. Import Execution (1.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 199 | Implement `ExecuteImportAsync(jobId, importedBy)` — core import loop | BE | 1h | ☐ |
| 200 | Import Customers: create BusinessPartner (type=Customer) + Contact + Address per valid row | BE | 1h | ☐ |
| 201 | Import Vendors: create BusinessPartner (type=Vendor) + Contact + Address | BE | 0.5h | ☐ |
| 202 | Import Products: create Product with category/UoM FK resolution, set prices, stock levels | BE | 1h | ☐ |
| 203 | Import Opening Balances: create single JournalEntry with type OPENING, post via gateway | BE | 1h | ☐ |
| 204 | Import Price Lists: create/update PriceList entries with product FK resolution | BE | 0.5h | ☐ |
| 205 | Track progress: update ImportedRows count as each row succeeds | BE | 0.25h | ☐ |
| 206 | Handle partial failure: skip error rows, continue with valid, log skipped | BE | 0.5h | ☐ |
| 207 | Update job status: Importing → Completed (or Failed if all rows fail) | BE | 0.25h | ☐ |
| 208 | Implement `GetImportHistoryAsync(orgId)` — list past import jobs | BE | 0.25h | ☐ |
| 209 | Register `IDataImportService` in DI | BE | 0.25h | ☐ |
| 210 | Unit test: import 100 customers from Excel | QA | 0.5h | ☐ |
| 211 | Unit test: import products with category/UoM resolution | QA | 0.5h | ☐ |
| 212 | Unit test: opening balance creates balanced JE | QA | 0.5h | ☐ |

### P2-F. Data Import API Endpoints (0.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 213 | Create `DataImportController` | BE | 0.25h | ☐ |
| 214 | Endpoints 11-17: template download, upload, mappings, validate, execute, history | BE | 1h | ☐ |
| 215 | Add `[Authorize(Policy = "Org.Admin")]` on all import endpoints | BE | 0.25h | ☐ |
| 216 | Request validation, file size limit (50MB) | BE | 0.25h | ☐ |

### P2-G. Data Import Wizard UI (1.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 217 | Create `DataImportWizard` React component — multi-step | FE | 0.5h | ☐ |
| 218 | Step 1: Import type selection — cards for Customers, Vendors, Products, Opening Balances, Price Lists | FE | 0.5h | ☐ |
| 219 | Step 2: Download template button + instructions | FE | 0.5h | ☐ |
| 220 | Step 3: File upload — drag-and-drop zone + file picker | FE | 0.5h | ☐ |
| 221 | Step 4: Column mapping — source → target table, auto-mapped with manual override dropdowns | FE | 1.5h | ☐ |
| 222 | Sample values preview per mapping row | FE | 0.5h | ☐ |
| 223 | Step 5: Validation results — valid/warning/error counts, expandable error list per row | FE | 1h | ☐ |
| 224 | "Fix and Re-upload" vs "Import valid rows only" options | FE | 0.25h | ☐ |
| 225 | Step 6: Import progress bar → completion summary | FE | 0.5h | ☐ |
| 226 | Create `ImportHistory` page — table of past jobs with status, counts, dates | FE | 0.5h | ☐ |
| 227 | Wire all import UI to API endpoints | FE | 0.5h | ☐ |

**Phase P2 Subtotal: 61 tasks (39 BE, 3 DB, 11 FE, 4 QA + 4 validation tests)**

---

## Phase P3 — Templates, Demo Data & Onboarding (10 days)

### P3-A. Document Templates Entity & Service (2 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 228 | Create `DocumentTemplate` entity | BE | 0.5h | ☐ |
| 229 | Define: `TemplateType`, `Name`, `HeaderHtml`, `BodyHtml`, `FooterHtml`, `LogoFileKey`, `PrimaryColor` | BE | 0.5h | ☐ |
| 230 | Define: `ShowBankDetails`, `BankDetailsText`, `TermsAndConditions`, `IsDefault`, `IsSeeded` | BE | 0.25h | ☐ |
| 231 | Configure CHECK constraint on TemplateType | DB | 0.25h | ☐ |
| 232 | Write migration `M50_007_CreateDocumentTemplates` | DB | 0.25h | ☐ |
| 233 | Implement `IDocumentTemplateService` interface | BE | 0.25h | ☐ |
| 234 | Implement `ListAsync(orgId)` — list all templates | BE | 0.25h | ☐ |
| 235 | Implement `GetAsync(orgId, templateId)` | BE | 0.25h | ☐ |
| 236 | Implement `UpdateAsync` — save customizations (logo, colors, footer) | BE | 0.5h | ☐ |
| 237 | Implement `SetDefaultAsync(orgId, templateId)` — unset other default of same type | BE | 0.25h | ☐ |
| 238 | Implement `CreateCustomAsync` — clone from existing or blank | BE | 0.5h | ☐ |
| 239 | Implement `ResetToDefaultAsync(orgId, templateType)` — restore seeded template | BE | 0.5h | ☐ |
| 240 | Implement `RenderPdfAsync(orgId, templateType, documentData)` — merge placeholders + generate PDF | BE | 2h | ☐ |
| 241 | Implement placeholder engine: `{CompanyName}`, `{CustomerName}`, `{InvoiceNumber}`, `{LineItems}`, `{TaxSummary}`, `{TotalAmount}`, `{AmountInWords}`, `{BankDetails}` | BE | 1h | ☐ |
| 242 | Register in DI | BE | 0.25h | ☐ |

### P3-B. Seed Default Document Templates (1 day)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 243 | Write migration `M50_014_SeedDocumentTemplates` | DB | 0.25h | ☐ |
| 244 | Create SalesInvoice HTML template — professional layout with company header, line items table, tax summary, totals, payment terms, bank details | DB | 1h | ☐ |
| 245 | Create Quotation HTML template | DB | 0.5h | ☐ |
| 246 | Create PurchaseOrder HTML template | DB | 0.5h | ☐ |
| 247 | Create DeliveryNote HTML template | DB | 0.5h | ☐ |
| 248 | Create PaymentReceipt HTML template | DB | 0.5h | ☐ |
| 249 | Create CustomerStatement HTML template | DB | 0.5h | ☐ |
| 250 | Seed templates for each country variant (PK, AE, SA, Global) with localized bank details, currency symbols | DB | 1h | ☐ |

### P3-C. Email Templates Entity & Service (1 day)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 251 | Create `EmailTemplate` entity | BE | 0.5h | ☐ |
| 252 | Define: `TemplateType`, `Name`, `SubjectTemplate`, `BodyHtml`, `IsDefault`, `IsSeeded` | BE | 0.25h | ☐ |
| 253 | Write migration `M50_008_CreateEmailTemplates` | DB | 0.25h | ☐ |
| 254 | Implement `IEmailTemplateService` interface | BE | 0.25h | ☐ |
| 255 | Implement CRUD + `RenderAsync(orgId, templateType, placeholders)` | BE | 0.5h | ☐ |
| 256 | Implement `SendTestAsync(orgId, templateId, toEmail)` — send preview email | BE | 0.5h | ☐ |
| 257 | Implement placeholder substitution: `{CompanyName}`, `{CustomerName}`, `{InvoiceNumber}`, `{Amount}`, `{DueDate}`, `{PaymentLink}` | BE | 0.5h | ☐ |
| 258 | Register in DI | BE | 0.25h | ☐ |

### P3-D. Seed Default Email Templates (0.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 259 | Write migration `M50_015_SeedEmailTemplates` | DB | 0.25h | ☐ |
| 260 | Create InvoiceSent template: subject + body | DB | 0.25h | ☐ |
| 261 | Create QuotationSent template | DB | 0.25h | ☐ |
| 262 | Create POToSupplier template | DB | 0.25h | ☐ |
| 263 | Create PaymentReminder template | DB | 0.25h | ☐ |
| 264 | Create PaymentReceived template | DB | 0.25h | ☐ |
| 265 | Create WelcomeEmail template | DB | 0.25h | ☐ |
| 266 | Create PayslipSent template (HRM) | DB | 0.25h | ☐ |

### P3-E. Sample/Demo Data (1.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 267 | Write migration `M50_009_AddIsDemoFlags` — add `IsDemo BIT DEFAULT 0` to BusinessPartners, Products, SaleOrders, SaleOrderLines, PurchaseOrders, PurchaseOrderLines, StockTransactions | DB | 0.5h | ☐ |
| 268 | Implement `ISampleDataService` interface | BE | 0.25h | ☐ |
| 269 | Implement `PreviewAsync(orgId)` — return what will be created (counts) | BE | 0.25h | ☐ |
| 270 | Implement `LoadSampleDataAsync(orgId)` — create demo records | BE | 1h | ☐ |
| 271 | Create 5 demo customers: Acme Corporation, BlueStar Enterprises, Lone Star Offices LLC, Cedar Trading Co, Highland Industries | BE | 0.5h | ☐ |
| 272 | Create 3 demo vendors: GlobalSupply Inc, Hartwell Furniture, QuickParts Co | BE | 0.25h | ☐ |
| 273 | Create 20 demo products with prices and opening stock | BE | 0.5h | ☐ |
| 274 | Create 2 demo sale orders and 1 demo purchase order with lines | BE | 0.5h | ☐ |
| 275 | Tag all demo records: `IsDemo = 1` | BE | 0.25h | ☐ |
| 276 | Implement `HasDemoDataAsync(orgId)` — check if any IsDemo = 1 exists | BE | 0.25h | ☐ |
| 277 | Implement `CleanupDemoDataAsync(orgId)` — delete in reverse dependency order | BE | 0.5h | ☐ |
| 278 | Register in DI | BE | 0.25h | ☐ |
| 279 | Unit test: load demo data → correct counts, all tagged IsDemo | QA | 0.5h | ☐ |
| 280 | Unit test: cleanup removes demo, keeps real data | QA | 0.5h | ☐ |

### P3-F. Onboarding Checklist (1 day)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 281 | Create `OnboardingChecklist` entity | BE | 0.25h | ☐ |
| 282 | Write migration `M50_003_CreateOnboardingChecklist` | DB | 0.25h | ☐ |
| 283 | Implement `IOnboardingService` interface | BE | 0.25h | ☐ |
| 284 | Implement `SeedChecklistAsync(orgId, enabledModules)` — create steps filtered by modules | BE | 0.5h | ☐ |
| 285 | Implement `GetStatusAsync(orgId)` — return completed/total/steps | BE | 0.25h | ☐ |
| 286 | Implement `CompleteStepAsync(orgId, stepCode)` — mark step done | BE | 0.25h | ☐ |
| 287 | Implement `DismissAsync(orgId)` — hide checklist permanently | BE | 0.25h | ☐ |
| 288 | Create event handlers for auto-completion: | BE | 1h | ☐ |
| 289 | → On CustomerCreated event → complete `ADD_CUSTOMER` | BE | 0.25h | ☐ |
| 290 | → On ProductCreated event → complete `ADD_PRODUCT` | BE | 0.25h | ☐ |
| 291 | → On VendorCreated event → complete `ADD_SUPPLIER` | BE | 0.25h | ☐ |
| 292 | → On SaleOrderCreated event → complete `CREATE_SO` | BE | 0.25h | ☐ |
| 293 | → On PurchaseOrderCreated event → complete `CREATE_PO` | BE | 0.25h | ☐ |
| 294 | → On QuickBooksConnected event → complete `CONNECT_QB` | BE | 0.25h | ☐ |
| 295 | → On second user created → complete `INVITE_TEAM` | BE | 0.25h | ☐ |
| 296 | Register in DI | BE | 0.25h | ☐ |

### P3-G. Template Editor UI (1.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 297 | Create `DocumentTemplateList` page — table of templates with type, status, actions | FE | 0.5h | ☐ |
| 298 | Create `DocumentTemplateEditor` page — edit header/body/footer HTML | FE | 1.5h | ☐ |
| 299 | Logo upload — drag-and-drop, preview | FE | 0.5h | ☐ |
| 300 | Color picker for primary color | FE | 0.25h | ☐ |
| 301 | Bank details text editor | FE | 0.25h | ☐ |
| 302 | Terms and conditions text editor | FE | 0.25h | ☐ |
| 303 | Preview button — render with sample data | FE | 0.5h | ☐ |
| 304 | "Set as Default" and "Reset to Default" actions | FE | 0.25h | ☐ |
| 305 | Create `EmailTemplateList` page | FE | 0.5h | ☐ |
| 306 | Create `EmailTemplateEditor` page — subject + body HTML with placeholder guide | FE | 1h | ☐ |
| 307 | "Send Test Email" button with email input | FE | 0.25h | ☐ |
| 308 | Placeholder reference panel — shows available `{placeholders}` with descriptions | FE | 0.5h | ☐ |

### P3-H. Demo Data & Onboarding UI (1 day)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 309 | Create `SampleDataManager` page — preview counts, Load button, Delete button | FE | 0.5h | ☐ |
| 310 | Load confirmation dialog with record list | FE | 0.25h | ☐ |
| 311 | Delete confirmation dialog with warning | FE | 0.25h | ☐ |
| 312 | "Demo" badge component — shown on demo records in list views | FE | 0.25h | ☐ |
| 313 | Create `OnboardingChecklistWidget` dashboard component | FE | 1h | ☐ |
| 314 | Progress bar: "3 of 8 done" | FE | 0.25h | ☐ |
| 315 | Clickable steps with "Do this now" links to relevant pages | FE | 0.25h | ☐ |
| 316 | Dismiss button — "I'll handle this myself" | FE | 0.25h | ☐ |
| 317 | Add widget to dashboard — show only when not dismissed and not all complete | FE | 0.25h | ☐ |
| 318 | Add "Setup incomplete" banner on dashboard when SetupStatus = 'NotStarted' | FE | 0.25h | ☐ |
| 319 | Add "Default Data" and "Data Import" menu items to Settings → Organization | FE | 0.25h | ☐ |

### P3-I. API Endpoints for P3 Features (0.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 320 | Create `OnboardingController` — endpoints 9-10 | BE | 0.25h | ☐ |
| 321 | Create `SampleDataController` — endpoints 18-21 | BE | 0.5h | ☐ |
| 322 | Create `DocumentTemplateController` — endpoints 22-28 | BE | 0.5h | ☐ |
| 323 | Create `EmailTemplateController` — endpoints 29-33 | BE | 0.5h | ☐ |
| 324 | Authorization policies on all endpoints | BE | 0.25h | ☐ |

**Phase P3 Subtotal: 97 tasks (42 BE, 14 DB, 23 FE, 2 QA)**

---

## Phase P4 — Testing & Documentation (7 days)

### P4-A. Integration Tests (3 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 325 | TS-50-001: New org → first login → wizard launches | QA | 0.5h | ☐ |
| 326 | TS-50-002: PK + Trading → preview shows correct counts | QA | 0.5h | ☐ |
| 327 | TS-50-003: Complete wizard → all categories seeded in per-org tables | QA | 1h | ☐ |
| 328 | TS-50-004: Run wizard twice → no duplicates | QA | 0.5h | ☐ |
| 329 | TS-50-005: Edit seeded account → CategoryStatus shows Modified | QA | 0.25h | ☐ |
| 330 | TS-50-006: Add custom tax code → Current > Seeded | QA | 0.25h | ☐ |
| 331 | TS-50-007: Reset Merge → missing restored, custom kept | QA | 0.5h | ☐ |
| 332 | TS-50-008: Reset Overwrite → seeded replaced, custom kept | QA | 0.5h | ☐ |
| 333 | TS-50-009: Reset Full on unused → all deleted + re-seeded | QA | 0.5h | ☐ |
| 334 | TS-50-010: Reset Full on used → blocked with message | QA | 0.5h | ☐ |
| 335 | TS-50-011: Org A edits → Org B unaffected (multi-tenant isolation) | QA | 1h | ☐ |
| 336 | TS-50-012: Skip wizard → GLOBAL defaults applied | QA | 0.5h | ☐ |
| 337 | TS-50-013: Leave wizard at Step 3, return → resumes at Step 3 | QA | 0.5h | ☐ |
| 338 | TS-50-014: Import 500 customers from Excel → all created | QA | 1h | ☐ |
| 339 | TS-50-015: Import with warnings → admin imports valid only | QA | 0.5h | ☐ |
| 340 | TS-50-016: Import unbalanced opening balances → rejected | QA | 0.5h | ☐ |
| 341 | TS-50-017: Import balanced opening balances → JE created | QA | 0.5h | ☐ |
| 342 | TS-50-018: Load demo data → correct counts, all IsDemo = 1 | QA | 0.5h | ☐ |
| 343 | TS-50-019: Delete demo data → demo gone, custom untouched | QA | 0.5h | ☐ |
| 344 | TS-50-020: Edit document template → PDF renders with changes | QA | 0.5h | ☐ |
| 345 | TS-50-021: Edit email template → test email sent correctly | QA | 0.5h | ☐ |
| 346 | TS-50-022: Create customer → onboarding step auto-completes | QA | 0.5h | ☐ |
| 347 | TS-50-023: Dismiss checklist → widget hidden | QA | 0.25h | ☐ |
| 348 | TS-50-024: Super Admin updates PK TAX → existing orgs unaffected | QA | 0.5h | ☐ |
| 349 | TS-50-025: Fallback chain: AE + Manufacturing → correct fallback | QA | 0.5h | ☐ |

### P4-B. Performance & Security Tests (1.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 350 | Performance: wizard apply < 10 seconds for full PK+Trading template | QA | 0.5h | ☐ |
| 351 | Performance: import 5,000 products < 60 seconds | QA | 0.5h | ☐ |
| 352 | Performance: demo data load < 15 seconds | QA | 0.25h | ☐ |
| 353 | Security: Org Admin cannot access Super Admin endpoints | QA | 0.5h | ☐ |
| 354 | Security: Org Admin cannot see another org's setup data | QA | 0.5h | ☐ |
| 355 | Security: import file size limit enforced (50MB) | QA | 0.25h | ☐ |
| 356 | Security: uploaded import files scanned for malicious content | QA | 0.5h | ☐ |

### P4-C. Documentation (2.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 357 | Document all 40 API endpoints in OpenAPI/Swagger | DOC | 1.5h | ☐ |
| 358 | Document setup wizard user guide for Org Admins | DOC | 1h | ☐ |
| 359 | Document data import guide with template instructions | DOC | 1h | ☐ |
| 360 | Document Super Admin template management guide | DOC | 0.5h | ☐ |
| 361 | Document placeholder reference for document/email templates | DOC | 0.5h | ☐ |
| 362 | Document JSON payload schemas per DataCategory | DOC | 1h | ☐ |
| 363 | Document sample data specification | DOC | 0.25h | ☐ |
| 364 | Document template pack creation process for new countries | DOC | 0.5h | ☐ |
| 365 | Create quick-start checklist for Org Admins | DOC | 0.5h | ☐ |
| 366 | Final review of all documentation | DOC | 0.5h | ☐ |

**Phase P4 Subtotal: 42 tasks (0 BE, 0 DB, 0 FE, 32 QA, 10 DOC)**

---

## Summary

| Phase | Description | Tasks | Days |
|---|---|---|---|
| P1 | Foundation: Setup Wizard & Industry Templates | 166 | 15 |
| P2 | Data Import Wizard | 61 | 8 |
| P3 | Templates, Demo Data & Onboarding | 97 | 10 |
| P4 | Testing & Documentation | 42 | 7 |
| **Total** | | **366** | **40** |

### Task Counts by Category

| Category | Count | % |
|---|---|---|
| BE — Backend | 160 | 43.7% |
| DB — Database | 48 | 13.1% |
| FE — Frontend | 80 | 21.9% |
| QA — Quality Assurance | 46 | 12.6% |
| DOC — Documentation | 10 | 2.7% |
| Seeds (within DB) | 22 | 6.0% |
| **Total** | **366** | **100%** |

---

## Cross-Reference: Entity → Phase → Migration

| Entity | Phase | Migration | Schema |
|---|---|---|---|
| SetupDefaults | P1 | M50_001 | core |
| TenantSetupLog | P1 | M50_002 | core |
| OnboardingChecklist | P3 | M50_003 | core |
| Organizations (ALTER) | P1 | M50_004 | core |
| IsSeeded flags (ALTER 8 tables) | P1 | M50_005 | finance, inventory, core |
| DataImportJobs | P2 | M50_006 | core |
| DocumentTemplates | P3 | M50_007 | core |
| EmailTemplates | P3 | M50_008 | core |
| IsDemo flags (ALTER 7 tables) | P3 | M50_009 | sales, inventory, procurement |
| Pakistan Trading defaults | P1 | M50_010 | core.SetupDefaults (rows) |
| UAE Trading defaults | P1 | M50_011 | core.SetupDefaults (rows) |
| Saudi Trading defaults | P1 | M50_012 | core.SetupDefaults (rows) |
| Global defaults | P1 | M50_013 | core.SetupDefaults (rows) |
| Default document templates | P3 | M50_014 | core.DocumentTemplates (rows) |
| Default email templates | P3 | M50_015 | core.EmailTemplates (rows) |

---

## Cross-Reference: Business Rule → Task #

| Rule | Task #s |
|---|---|
| BR-50-001: Wizard on first login | 144 |
| BR-50-002: Wizard resumable | 142, 143 |
| BR-50-003: Wizard skippable | 141 |
| BR-50-004: Idempotent seeding | 063, 069 |
| BR-50-005: Org-scoped data | All seeding tasks (046-064) |
| BR-50-006: Deletion guard | 077 |
| BR-50-007: Merge preserves custom | 074, 079 |
| BR-50-008: Full reset blocked if in use | 076, 081 |
| BR-50-009: Demo data tagged | 275, 279 |
| BR-50-010: Demo cleanup one-click | 277, 280 |
| BR-50-011: Opening balance must balance | 193, 198 |
| BR-50-012: Import validates first | 187-196 |
| BR-50-013: Template version tracking | 032, 064 |
| BR-50-014: Auto-complete checklist | 288-295, 346 |
| BR-50-015: Super Admin changes don't affect existing | 033, 348 |

---

**End of Task Register — Addendum 50**
