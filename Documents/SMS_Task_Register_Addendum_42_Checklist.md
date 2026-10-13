# SMS — Task Register Checklist

## FSD Addendum 42 — Chart of Accounts Configuration, Opening Balances & Financial Integration

| Property | Value |
|---|---|
| Source Document | SMS-FSD-ADD-042 v1.0 |
| Generated | 2026-10-12 |
| Total Tasks | 394 |
| Phases | 6 |
| Change Items Covered | C1–C10 |

---

## How to Use This Register

- Each task has a unique ID: `A42-XXXX`
- Status values: `[ ]` = Not Started, `[~]` = In Progress, `[✓]` = Done, `[—]` = Skipped / N/A
- Priority: **P0** = Blocker, **P1** = Must Have, **P2** = Should Have, **P3** = Nice to Have
- Every task belongs to a Change Item (C1–C10) and a Development Phase (1–6)

---

## Phase 1 — Foundation (C1, C2, C3)

### 1.1 C1 — Finance Module Activation & Schema

#### 1.1.1 Database & Schema

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0001 | Create `finance` schema in SQL Server (`CREATE SCHEMA finance`) | P0 | 1A | [ ] |
| A42-0002 | Write EF Core migration `AddFinanceSchema` (M1) | P0 | 1A | [ ] |
| A42-0003 | Add Finance module seed row in `core.ModuleCatalog` (module_code=FINANCE, schema_name=finance, display_order=20, icon=currency-dollar) | P0 | 1A | [ ] |
| A42-0004 | Set `is_available = 1` for FINANCE module in Module Catalog | P0 | 1A | [ ] |
| A42-0005 | Verify migration M1 runs idempotently (`IF NOT EXISTS` guard on schema) | P1 | 1A | [ ] |
| A42-0006 | Verify Finance module appears in Module Registry UI after migration | P1 | 1A | [ ] |
| A42-0007 | Verify `depends_on` is NULL (Finance is foundational — no module dependencies) | P1 | 1A | [ ] |
| A42-0008 | Add Finance module description: "Chart of Accounts, General Ledger, Accounts Receivable, Accounts Payable, Banking, and Financial Reporting" | P1 | 1A | [ ] |

### 1.2 C2 — CoA Template Registry

#### 1.2.1 Entity & Domain Model

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0009 | Create `CoaTemplate` domain entity class in `SMS.Domain.Entities.Finance` | P0 | 1B | [ ] |
| A42-0010 | Add property `Id` (int, PK, IDENTITY) | P0 | 1B | [ ] |
| A42-0011 | Add property `TemplateCode` (nvarchar(50), required) | P0 | 1B | [ ] |
| A42-0012 | Add property `TemplateName` (nvarchar(200), required) | P0 | 1B | [ ] |
| A42-0013 | Add property `CountryCode` (nvarchar(3), nullable) — FK to lookups.Countries | P1 | 1B | [ ] |
| A42-0014 | Add property `BusinessType` (nvarchar(100), nullable) | P1 | 1B | [ ] |
| A42-0015 | Add property `CurrencyCode` (nvarchar(3), nullable) — FK to lookups.Currencies | P1 | 1B | [ ] |
| A42-0016 | Add property `Description` (nvarchar(1000), nullable) | P2 | 1B | [ ] |
| A42-0017 | Add property `AccountCount` (int, default 0) — cached count | P1 | 1B | [ ] |
| A42-0018 | Add property `IsActive` (bit, default true) — soft-delete flag | P0 | 1B | [ ] |
| A42-0019 | Add property `Version` (nvarchar(20), default '1.0') | P2 | 1B | [ ] |
| A42-0020 | Add property `OrgId` (int, nullable) — NULL for system templates | P0 | 1B | [ ] |
| A42-0021 | Implement `BaseAuditableEntity` fields (created_at, created_by, modified_at, modified_by) | P0 | 1B | [ ] |
| A42-0022 | Implement `IMultiTenant` interface | P0 | 1B | [ ] |

#### 1.2.2 EF Core Configuration

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0023 | Create `CoaTemplateConfiguration` class implementing `IEntityTypeConfiguration<CoaTemplate>` | P0 | 1B | [ ] |
| A42-0024 | Map to table `CoaTemplates` in `finance` schema | P0 | 1B | [ ] |
| A42-0025 | Configure unique filtered index `UX_CoaTemplates_Code_Org` on (template_code, org_id) WHERE is_active = 1 | P0 | 1B | [ ] |
| A42-0026 | Configure index `IX_CoaTemplates_Country` on country_code | P1 | 1B | [ ] |
| A42-0027 | Configure index `IX_CoaTemplates_BusinessType` on business_type | P1 | 1B | [ ] |
| A42-0028 | Configure index `IX_CoaTemplates_OrgId` on org_id | P1 | 1B | [ ] |
| A42-0029 | Add CHECK constraint: template_code must match `^[A-Z0-9_]{2,50}$` | P1 | 1B | [ ] |
| A42-0030 | Add FK constraint: country_code → lookups.Countries (when non-null) | P1 | 1B | [ ] |
| A42-0031 | Add FK constraint: currency_code → lookups.Currencies (when non-null) | P1 | 1B | [ ] |
| A42-0032 | Configure HasQueryFilter for multi-tenant isolation on org_id | P0 | 1B | [ ] |

#### 1.2.3 Migration

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0033 | Write EF Core migration `CreateCoaTemplates` (M2) | P0 | 1B | [ ] |
| A42-0034 | Verify M2 creates table with all columns, indexes, and constraints | P0 | 1B | [ ] |
| A42-0035 | Verify M2 rollback (`Down()`) drops table cleanly | P1 | 1B | [ ] |

#### 1.2.4 Seed Data

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0036 | Write seed migration `SeedCoaTemplates` (M3) | P0 | 1D | [ ] |
| A42-0037 | Seed template: GLOBAL_GENERAL (Global — General Business, country=NULL, type=General) | P0 | 1D | [ ] |
| A42-0038 | Seed template: GLOBAL_SERVICES (Global — Professional Services) | P0 | 1D | [ ] |
| A42-0039 | Seed template: GLOBAL_NONPROFIT (Global — Non-Profit Organization) | P1 | 1D | [ ] |
| A42-0040 | Seed template: US_GENERAL (United States — General Business, country=USA) | P0 | 1D | [ ] |
| A42-0041 | Seed template: US_RETAIL (United States — Retail, country=USA) | P1 | 1D | [ ] |
| A42-0042 | Seed template: US_MANUFACTURING (United States — Manufacturing, country=USA) | P1 | 1D | [ ] |
| A42-0043 | Seed template: US_SERVICES (United States — Professional Services, country=USA) | P1 | 1D | [ ] |
| A42-0044 | Seed template: GB_GENERAL (United Kingdom — General Business, country=GBR) | P1 | 1D | [ ] |
| A42-0045 | Seed template: GB_RETAIL (United Kingdom — Retail, country=GBR) | P1 | 1D | [ ] |
| A42-0046 | Seed template: CA_GENERAL (Canada — General Business, country=CAN) | P1 | 1D | [ ] |
| A42-0047 | Seed template: AU_GENERAL (Australia — General Business, country=AUS) | P1 | 1D | [ ] |
| A42-0048 | Seed template: DE_GENERAL (Germany — General Business SKR04, country=DEU) | P1 | 1D | [ ] |
| A42-0049 | Seed template: AE_GENERAL (United Arab Emirates — General Business, country=ARE) | P1 | 1D | [ ] |
| A42-0050 | Seed template: SA_GENERAL (Saudi Arabia — General Business, country=SAU) | P1 | 1D | [ ] |
| A42-0051 | Seed template: IN_GENERAL (India — General Business, country=IND) | P1 | 1D | [ ] |
| A42-0052 | Seed template: PK_GENERAL (Pakistan — General Business, country=PAK) | P1 | 1D | [ ] |
| A42-0053 | Verify all 16 seed templates have org_id = NULL (system templates) | P0 | 1D | [ ] |
| A42-0054 | Verify seed is idempotent (re-runnable without duplicates) | P1 | 1D | [ ] |

### 1.3 C3 — CoA Template Items

#### 1.3.1 Entity & Domain Model

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0055 | Create `CoaTemplateItem` domain entity class in `SMS.Domain.Entities.Finance` | P0 | 1C | [ ] |
| A42-0056 | Add property `Id` (int, PK, IDENTITY) | P0 | 1C | [ ] |
| A42-0057 | Add property `TemplateId` (int, required) — FK to CoaTemplates.id | P0 | 1C | [ ] |
| A42-0058 | Add property `AccountCode` (nvarchar(20), required) | P0 | 1C | [ ] |
| A42-0059 | Add property `AccountName` (nvarchar(200), required) | P0 | 1C | [ ] |
| A42-0060 | Add property `ParentItemId` (int, nullable) — FK to self | P0 | 1C | [ ] |
| A42-0061 | Add property `Level` (tinyint, required) — CHECK 1–4 | P0 | 1C | [ ] |
| A42-0062 | Add property `RootType` (nvarchar(20), required) — CHECK enum | P0 | 1C | [ ] |
| A42-0063 | Add property `IsGroup` (bit, required) | P0 | 1C | [ ] |
| A42-0064 | Add property `AccountType` (nvarchar(50), nullable) — sub-classification for Level 4 | P1 | 1C | [ ] |
| A42-0065 | Add property `Description` (nvarchar(500), nullable) | P2 | 1C | [ ] |
| A42-0066 | Add property `SortOrder` (int, default 0) | P1 | 1C | [ ] |
| A42-0067 | Add property `IsDefaultAccount` (bit, default false) | P1 | 1C | [ ] |
| A42-0068 | Add property `DefaultAccountKey` (nvarchar(50), nullable) — non-null only when IsDefaultAccount = true | P1 | 1C | [ ] |
| A42-0069 | Add navigation property `Template` → CoaTemplate | P0 | 1C | [ ] |
| A42-0070 | Add navigation property `Parent` → self (CoaTemplateItem) | P0 | 1C | [ ] |
| A42-0071 | Add navigation property `Children` → ICollection<CoaTemplateItem> | P0 | 1C | [ ] |

#### 1.3.2 EF Core Configuration

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0072 | Create `CoaTemplateItemConfiguration` class | P0 | 1C | [ ] |
| A42-0073 | Map to table `CoaTemplateItems` in `finance` schema | P0 | 1C | [ ] |
| A42-0074 | Configure unique index `UX_CoaTemplateItems_Template_Code` on (template_id, account_code) | P0 | 1C | [ ] |
| A42-0075 | Configure index `IX_CoaTemplateItems_Parent` on parent_item_id | P1 | 1C | [ ] |
| A42-0076 | Configure index `IX_CoaTemplateItems_TemplateId` on template_id | P1 | 1C | [ ] |
| A42-0077 | Configure index `IX_CoaTemplateItems_Level` on (template_id, level) | P1 | 1C | [ ] |
| A42-0078 | Add CHECK constraint: `level BETWEEN 1 AND 4` | P0 | 1C | [ ] |
| A42-0079 | Add CHECK constraint: `root_type IN ('Asset', 'Liability', 'Equity', 'Revenue', 'Expense')` | P0 | 1C | [ ] |
| A42-0080 | Add CHECK constraint: `is_group = true` when level IN (1,2,3), `is_group = false` when level = 4 | P0 | 1C | [ ] |
| A42-0081 | Add CHECK constraint: `account_type` valid enum values (Cash, Bank, Receivable, Payable, Tax, CostOfGoodsSold, Revenue, Expense, FixedAsset, Depreciation, Equity, AccumulatedDepreciation, OtherCurrentAsset, OtherCurrentLiability, LongTermLiability, OtherIncome, OtherExpense, NULL) | P1 | 1C | [ ] |
| A42-0082 | Validate parent_item_id NULL when level = 1 | P0 | 1C | [ ] |
| A42-0083 | Validate parent_item_id references item with level = current_level − 1 within same template | P0 | 1C | [ ] |
| A42-0084 | Configure FK: template_id → CoaTemplates with Cascade Delete | P0 | 1C | [ ] |
| A42-0085 | Configure self-referencing FK: parent_item_id → self with Restrict Delete | P0 | 1C | [ ] |

#### 1.3.3 Migration

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0086 | Write EF Core migration `CreateCoaTemplateItems` (M4) — depends on M2 | P0 | 1C | [ ] |
| A42-0087 | Verify M4 creates table with all columns, indexes, constraints | P0 | 1C | [ ] |
| A42-0088 | Verify M4 rollback drops table cleanly | P1 | 1C | [ ] |

#### 1.3.4 Template Item Seed Data

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0089 | Author GLOBAL_GENERAL template accounts as embedded JSON resource (~65 accounts) | P0 | 1D | [ ] |
| A42-0090 | Write seed migration `SeedGlobalGeneralTemplate` (M5) — deserialize JSON, insert CoaTemplateItems | P0 | 1D | [ ] |
| A42-0091 | Verify GLOBAL_GENERAL seed has all 5 Level 1 root types (codes 1–5) | P0 | 1D | [ ] |
| A42-0092 | Verify GLOBAL_GENERAL seed has valid parent-child hierarchy (no orphans) | P0 | 1D | [ ] |
| A42-0093 | Verify GLOBAL_GENERAL seed Level 4 accounts have is_group = false, Levels 1–3 have is_group = true | P0 | 1D | [ ] |
| A42-0094 | Verify GLOBAL_GENERAL seed includes default accounts (is_default_account = true with default_account_key) | P1 | 1D | [ ] |
| A42-0095 | Author US_GENERAL template accounts as embedded JSON resource (~70 accounts) | P0 | 1D | [ ] |
| A42-0096 | Write seed migration `SeedUSGeneralTemplate` (M6) — deserialize JSON, insert CoaTemplateItems | P0 | 1D | [ ] |
| A42-0097 | Verify US_GENERAL seed has all 5 Level 1 root types | P0 | 1D | [ ] |
| A42-0098 | Verify US_GENERAL seed valid hierarchy | P0 | 1D | [ ] |
| A42-0099 | Verify US_GENERAL seed default accounts marked | P1 | 1D | [ ] |
| A42-0100 | Update `account_count` on CoaTemplates after each template seed (cached count) | P1 | 1D | [ ] |
| A42-0101 | Verify seed data is idempotent (re-runnable) | P1 | 1D | [ ] |

#### 1.3.5 CSV Export Service

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0102 | Create `ICoaTemplateExportService` interface | P0 | 1E | [ ] |
| A42-0103 | Implement `ExportToCsvAsync(int templateId)` — return CSV byte stream | P0 | 1E | [ ] |
| A42-0104 | CSV header row: account_code,account_name,parent_code,level,root_type,is_group,account_type,description | P0 | 1E | [ ] |
| A42-0105 | Export parent_code as the parent's account_code (not parent_item_id) for portability | P0 | 1E | [ ] |
| A42-0106 | Export is_group as text "true"/"false" | P1 | 1E | [ ] |
| A42-0107 | Export empty account_type for group accounts | P1 | 1E | [ ] |
| A42-0108 | Handle CSV special characters (quotes, commas, newlines in description) | P1 | 1E | [ ] |

#### 1.3.6 JSON Export Service

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0109 | Implement `ExportToJsonAsync(int templateId)` — return nested JSON tree | P0 | 1E | [ ] |
| A42-0110 | JSON root object includes: template_code, template_name, country_code, currency_code, version, accounts[] | P0 | 1E | [ ] |
| A42-0111 | Each account node: code, name, root_type, level, is_group, account_type (if L4), description, children[] | P0 | 1E | [ ] |
| A42-0112 | Build tree recursively from flat template items | P0 | 1E | [ ] |
| A42-0113 | Verify JSON output conforms to Appendix C JSON schema | P1 | 1E | [ ] |

### 1.4 Template API Endpoints

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0114 | Create `CoaTemplateController` in Finance API area | P0 | 1E | [ ] |
| A42-0115 | Implement GET `/api/finance/templates` — list templates with country/business-type filters | P0 | 1E | [ ] |
| A42-0116 | Support query params: country_code, business_type, search (name filter) | P1 | 1E | [ ] |
| A42-0117 | Include system templates (org_id=NULL) AND tenant's custom templates in results | P1 | 1E | [ ] |
| A42-0118 | Implement GET `/api/finance/templates/{id}` — template detail with account tree | P0 | 1E | [ ] |
| A42-0119 | Implement GET `/api/finance/templates/{id}/download?format=csv` — CSV file download | P0 | 1E | [ ] |
| A42-0120 | Set Content-Disposition header for CSV download (filename = template_code.csv) | P1 | 1E | [ ] |
| A42-0121 | Implement GET `/api/finance/templates/{id}/download?format=json` — JSON file download | P0 | 1E | [ ] |
| A42-0122 | Set Content-Disposition header for JSON download (filename = template_code.json) | P1 | 1E | [ ] |
| A42-0123 | Implement GET `/api/finance/templates/{id}/preview` — flat account list (no download) | P1 | 1E | [ ] |
| A42-0124 | Add [Authorize] attribute on all template endpoints | P0 | 1E | [ ] |
| A42-0125 | Add Swagger/OpenAPI annotations on all template endpoints | P2 | 1E | [ ] |
| A42-0126 | Write request/response DTOs for template list, detail, preview | P0 | 1E | [ ] |

### 1.5 Template Selection UI

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0127 | Create React component `TemplateSelectionPanel` | P0 | 1F | [ ] |
| A42-0128 | Implement Country filter dropdown (All Countries + seeded country list) | P0 | 1F | [ ] |
| A42-0129 | Implement Business Type filter dropdown (All Types + type list) | P0 | 1F | [ ] |
| A42-0130 | Display matching templates as cards showing: name, account count, currency, description | P0 | 1F | [ ] |
| A42-0131 | Add [Preview] button per template — opens flat account list in modal/side panel | P1 | 1F | [ ] |
| A42-0132 | Add [Download CSV] button per template | P0 | 1F | [ ] |
| A42-0133 | Add [Download JSON] button per template | P0 | 1F | [ ] |
| A42-0134 | Add [Apply →] button per template — calls apply-template API | P0 | 1F | [ ] |
| A42-0135 | Add "Upload your own CoA" section with drag-and-drop file zone | P0 | 1F | [ ] |
| A42-0136 | Validate uploaded file extension (CSV or JSON only) | P1 | 1F | [ ] |
| A42-0137 | Add [Download CSV Template] link (blank template with header row) | P1 | 1F | [ ] |
| A42-0138 | Add [Download JSON Template] link (skeleton JSON schema) | P1 | 1F | [ ] |
| A42-0139 | Show upload progress indicator | P2 | 1F | [ ] |
| A42-0140 | Display upload validation errors inline with line numbers | P0 | 1F | [ ] |
| A42-0141 | Add [Help] expandable panel (What is this? Why? Tips, Example) | P2 | 1F | [ ] |

---

## Phase 2 — Chart of Accounts (C4)

### 2.1 C4 — Tenant Chart of Accounts Entity

#### 2.1.1 Entity & Domain Model

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0142 | Create `ChartOfAccount` domain entity class in `SMS.Domain.Entities.Finance` | P0 | 2A | [ ] |
| A42-0143 | Add property `Id` (int, PK, IDENTITY) | P0 | 2A | [ ] |
| A42-0144 | Add property `AccountCode` (nvarchar(20), required) | P0 | 2A | [ ] |
| A42-0145 | Add property `AccountName` (nvarchar(200), required) | P0 | 2A | [ ] |
| A42-0146 | Add property `ParentAccountId` (int, nullable) — FK to self | P0 | 2A | [ ] |
| A42-0147 | Add property `MaterializedPath` (nvarchar(500), required) — auto-computed | P0 | 2A | [ ] |
| A42-0148 | Add property `Level` (tinyint, required) — CHECK 1–4 | P0 | 2A | [ ] |
| A42-0149 | Add property `RootType` (nvarchar(20), required) — enum CHECK | P0 | 2A | [ ] |
| A42-0150 | Add property `IsGroup` (bit, required) | P0 | 2A | [ ] |
| A42-0151 | Add property `AccountType` (nvarchar(50), nullable) | P1 | 2A | [ ] |
| A42-0152 | Add property `Description` (nvarchar(500), nullable) | P2 | 2A | [ ] |
| A42-0153 | Add property `ExternalCode` (nvarchar(50), nullable) — code in external system | P1 | 2A | [ ] |
| A42-0154 | Add property `IsSystemAccount` (bit, default false) | P0 | 2A | [ ] |
| A42-0155 | Add property `IsActive` (bit, default true) — soft-delete | P0 | 2A | [ ] |
| A42-0156 | Add property `HasTransactions` (bit, default false) — cached flag | P0 | 2A | [ ] |
| A42-0157 | Add property `OpeningBalanceDebit` (decimal(18,4), default 0) | P0 | 2A | [ ] |
| A42-0158 | Add property `OpeningBalanceCredit` (decimal(18,4), default 0) | P0 | 2A | [ ] |
| A42-0159 | Add property `CurrentBalance` (decimal(18,4), default 0) | P0 | 2A | [ ] |
| A42-0160 | Add property `SortOrder` (int, default 0) | P1 | 2A | [ ] |
| A42-0161 | Add property `SourceTemplateId` (int, nullable) — FK to CoaTemplates | P1 | 2A | [ ] |
| A42-0162 | Add property `OrgId` (int, required) — multi-tenant discriminator | P0 | 2A | [ ] |
| A42-0163 | Implement `BaseAuditableEntity` fields | P0 | 2A | [ ] |
| A42-0164 | Implement `IMultiTenant` interface | P0 | 2A | [ ] |
| A42-0165 | Add navigation property `Parent` → ChartOfAccount | P0 | 2A | [ ] |
| A42-0166 | Add navigation property `Children` → ICollection<ChartOfAccount> | P0 | 2A | [ ] |
| A42-0167 | Add navigation property `SourceTemplate` → CoaTemplate | P1 | 2A | [ ] |
| A42-0168 | Add navigation property `Organization` → Organization | P0 | 2A | [ ] |

#### 2.1.2 EF Core Configuration

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0169 | Create `ChartOfAccountConfiguration` class | P0 | 2A | [ ] |
| A42-0170 | Map to table `ChartOfAccounts` in `finance` schema | P0 | 2A | [ ] |
| A42-0171 | Configure unique filtered index `UX_ChartOfAccounts_Code_Org` on (account_code, org_id) WHERE is_active = 1 | P0 | 2A | [ ] |
| A42-0172 | Configure index `IX_ChartOfAccounts_Parent` on parent_account_id | P1 | 2A | [ ] |
| A42-0173 | Configure index `IX_ChartOfAccounts_OrgId` on org_id | P0 | 2A | [ ] |
| A42-0174 | Configure index `IX_ChartOfAccounts_RootType` on (org_id, root_type) | P1 | 2A | [ ] |
| A42-0175 | Configure index `IX_ChartOfAccounts_Level` on (org_id, level) | P1 | 2A | [ ] |
| A42-0176 | Configure index `IX_ChartOfAccounts_Path` on (org_id, materialized_path) | P0 | 2A | [ ] |
| A42-0177 | Configure index `IX_ChartOfAccounts_AccountType` on (org_id, account_type) filtered WHERE account_type IS NOT NULL | P1 | 2A | [ ] |
| A42-0178 | Configure index `IX_ChartOfAccounts_Active_Leaf` on (org_id, is_active, is_group) filtered WHERE is_active = 1 AND is_group = 0 | P1 | 2A | [ ] |
| A42-0179 | Add CHECK constraint: `level BETWEEN 1 AND 4` | P0 | 2A | [ ] |
| A42-0180 | Add CHECK constraint: `root_type IN ('Asset', 'Liability', 'Equity', 'Revenue', 'Expense')` | P0 | 2A | [ ] |
| A42-0181 | Add CHECK constraint: `(level IN (1,2,3) AND is_group = 1) OR (level = 4 AND is_group = 0)` | P0 | 2A | [ ] |
| A42-0182 | Add CHECK constraint: opening_balance_debit and opening_balance_credit cannot both be > 0 | P0 | 2A | [ ] |
| A42-0183 | Configure decimal precision: OpeningBalanceDebit, OpeningBalanceCredit, CurrentBalance → (18,4) | P0 | 2A | [ ] |
| A42-0184 | Configure self-referencing FK: parent_account_id → self with DeleteBehavior.Restrict | P0 | 2A | [ ] |
| A42-0185 | Configure FK: source_template_id → CoaTemplates with DeleteBehavior.SetNull | P1 | 2A | [ ] |
| A42-0186 | Configure HasQueryFilter for multi-tenant isolation on org_id | P0 | 2A | [ ] |

#### 2.1.3 Migration

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0187 | Write EF Core migration `CreateChartOfAccounts` (M7) — depends on M2 | P0 | 2A | [ ] |
| A42-0188 | Verify M7 creates table with all columns, indexes, constraints | P0 | 2A | [ ] |
| A42-0189 | Verify M7 rollback drops table cleanly | P1 | 2A | [ ] |

### 2.2 Apply Template Service

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0190 | Create `ICoaSetupService` interface | P0 | 2B | [ ] |
| A42-0191 | Implement `ApplyTemplateAsync(int templateId, int orgId)` | P0 | 2B | [ ] |
| A42-0192 | Validate: setup status must be NotStarted or AccountsCreated (BR-TPL-004) | P0 | 2B | [ ] |
| A42-0193 | If tenant already has accounts, delete all existing accounts before applying (BR-TPL-005) | P0 | 2B | [ ] |
| A42-0194 | Copy template items → ChartOfAccounts rows with tenant's org_id | P0 | 2B | [ ] |
| A42-0195 | Auto-create 5 Level 1 root accounts regardless of template content (BR-COA-008) | P0 | 2B | [ ] |
| A42-0196 | Compute `materialized_path` for each account during copy | P0 | 2B | [ ] |
| A42-0197 | Set `parent_account_id` FK references correctly (map template parent IDs → new ChartOfAccount IDs) | P0 | 2B | [ ] |
| A42-0198 | Set `source_template_id` on all copied accounts | P1 | 2B | [ ] |
| A42-0199 | Auto-create system account: 3900 Opening Balance Equity if not in template | P0 | 2B | [ ] |
| A42-0200 | Set `is_system_account = true` on account 3900 | P0 | 2B | [ ] |
| A42-0201 | Auto-populate `AccountingDefaults` from template items where is_default_account = true | P1 | 2B | [ ] |
| A42-0202 | Update CoaSetupStatuses: status → AccountsCreated, template_id, template_applied_at, accounts_created_count | P0 | 2B | [ ] |
| A42-0203 | Raise `CoaTemplateAppliedEvent` domain event | P1 | 2B | [ ] |
| A42-0204 | Wrap entire operation in a database transaction (all-or-nothing) | P0 | 2B | [ ] |

### 2.3 Upload Service (CSV/JSON)

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0205 | Create `ICoaUploadService` interface | P0 | 2C | [ ] |
| A42-0206 | Implement `UploadCsvAsync(Stream csvStream, int orgId)` | P0 | 2C | [ ] |
| A42-0207 | Implement `UploadJsonAsync(Stream jsonStream, int orgId)` | P0 | 2C | [ ] |
| A42-0208 | CSV parser: read header row, validate column names and order | P0 | 2C | [ ] |
| A42-0209 | CSV parser: parse each data row into intermediate account DTO | P0 | 2C | [ ] |
| A42-0210 | JSON parser: deserialize nested tree structure into flat account list | P0 | 2C | [ ] |
| A42-0211 | Validation Rule R1: file must be valid CSV or JSON | P0 | 2C | [ ] |
| A42-0212 | Validation Rule R2: all 5 root types must be present at Level 1 | P0 | 2C | [ ] |
| A42-0213 | Validation Rule R3: account codes must be unique within file | P0 | 2C | [ ] |
| A42-0214 | Validation Rule R4: level must be 1, 2, 3, or 4 | P0 | 2C | [ ] |
| A42-0215 | Validation Rule R5: parent code must exist and be one level above | P0 | 2C | [ ] |
| A42-0216 | Validation Rule R6: Level 1 codes must be 1–5 matching root types | P0 | 2C | [ ] |
| A42-0217 | Validation Rule R7: Level 4 must have is_group = false | P0 | 2C | [ ] |
| A42-0218 | Validation Rule R8: Levels 1–3 must have is_group = true | P0 | 2C | [ ] |
| A42-0219 | Validation Rule R9: root_type must match root ancestor's type | P0 | 2C | [ ] |
| A42-0220 | Validation Rule R10: maximum 2000 accounts per upload | P1 | 2C | [ ] |
| A42-0221 | Validation Rule R11: account code must be alphanumeric | P1 | 2C | [ ] |
| A42-0222 | Validation Rule R12: account_name max 200 characters | P1 | 2C | [ ] |
| A42-0223 | Validation Rule R13: block upload if tenant has posted journal entries | P0 | 2C | [ ] |
| A42-0224 | Return all validation errors at once (not fail-on-first) with line numbers | P0 | 2C | [ ] |
| A42-0225 | On valid upload: delete existing accounts, insert new accounts with materialized_path | P0 | 2C | [ ] |
| A42-0226 | Auto-create 5 Level 1 root accounts if upload omits any | P0 | 2C | [ ] |
| A42-0227 | Auto-create system account 3900 if not in uploaded file | P0 | 2C | [ ] |
| A42-0228 | Update CoaSetupStatuses: status → AccountsCreated, accounts_created_count | P0 | 2C | [ ] |
| A42-0229 | Raise `CoaUploadedEvent` domain event (OrgId, FileName, AccountCount, Format) | P1 | 2C | [ ] |
| A42-0230 | Wrap entire upload operation in a database transaction | P0 | 2C | [ ] |

### 2.4 CoA CRUD API Endpoints

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0231 | Create `ChartOfAccountsController` in Finance API area | P0 | 2D | [ ] |
| A42-0232 | Implement GET `/api/finance/chart-of-accounts` — hierarchical tree response | P0 | 2D | [ ] |
| A42-0233 | Build tree from flat data using parent_account_id references | P0 | 2D | [ ] |
| A42-0234 | Implement GET `/api/finance/chart-of-accounts/flat` — flat list with filters | P0 | 2D | [ ] |
| A42-0235 | Support query params: level, root_type, is_group, is_active, search (name/code) | P0 | 2D | [ ] |
| A42-0236 | Implement GET `/api/finance/chart-of-accounts/{id}` — single account detail | P0 | 2D | [ ] |
| A42-0237 | Include parent info, children count, current balance in detail response | P1 | 2D | [ ] |
| A42-0238 | Implement POST `/api/finance/chart-of-accounts` — create new account | P0 | 2D | [ ] |
| A42-0239 | Validate: parent must be exactly one level above (BR-COA-009) | P0 | 2D | [ ] |
| A42-0240 | Validate: root_type inherited from parent (BR-COA-003, BR-COA-010) | P0 | 2D | [ ] |
| A42-0241 | Validate: max 2000 accounts per tenant (BR-COA-012) | P1 | 2D | [ ] |
| A42-0242 | Auto-compute materialized_path on create (BR-COA-011) | P0 | 2D | [ ] |
| A42-0243 | Auto-set is_group based on level (true for L1–L3, false for L4) | P0 | 2D | [ ] |
| A42-0244 | Raise `ChartOfAccountCreatedEvent` | P1 | 2D | [ ] |
| A42-0245 | Implement PUT `/api/finance/chart-of-accounts/{id}` — update account | P0 | 2D | [ ] |
| A42-0246 | Allow update: name, description, external_code, sort_order, account_type | P0 | 2D | [ ] |
| A42-0247 | Block update: account_code, root_type (immutable after creation) | P0 | 2D | [ ] |
| A42-0248 | Block update: system accounts — cannot change root_type or code (BR-COA-007) | P0 | 2D | [ ] |
| A42-0249 | Raise `ChartOfAccountUpdatedEvent` with changed fields | P1 | 2D | [ ] |
| A42-0250 | Implement DELETE `/api/finance/chart-of-accounts/{id}` — soft-delete | P0 | 2D | [ ] |
| A42-0251 | Validate: cannot delete if account has children (BR-COA-005) | P0 | 2D | [ ] |
| A42-0252 | Validate: cannot delete if has_transactions = true (BR-COA-006) | P0 | 2D | [ ] |
| A42-0253 | Validate: cannot delete system accounts (BR-COA-007) | P0 | 2D | [ ] |
| A42-0254 | Soft-delete: set is_active = false | P0 | 2D | [ ] |
| A42-0255 | Raise `ChartOfAccountDeletedEvent` | P1 | 2D | [ ] |
| A42-0256 | Implement POST `/api/finance/chart-of-accounts/apply-template` — apply template | P0 | 2D | [ ] |
| A42-0257 | Require Finance.Admin authorization role | P0 | 2D | [ ] |
| A42-0258 | Implement POST `/api/finance/chart-of-accounts/upload` — multipart file upload | P0 | 2D | [ ] |
| A42-0259 | Accept Content-Type: multipart/form-data | P0 | 2D | [ ] |
| A42-0260 | Implement GET `/api/finance/chart-of-accounts/export?format=csv` — export tenant CoA | P0 | 2D | [ ] |
| A42-0261 | Implement GET `/api/finance/chart-of-accounts/export?format=json` — export tenant CoA | P0 | 2D | [ ] |
| A42-0262 | Write request/response DTOs for all CoA endpoints | P0 | 2D | [ ] |
| A42-0263 | Add Swagger/OpenAPI annotations | P2 | 2D | [ ] |

### 2.5 CoA Tree View UI

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0264 | Create React component `ChartOfAccountsTreeView` | P0 | 2E | [ ] |
| A42-0265 | Render collapsible tree with expand/collapse per group node | P0 | 2E | [ ] |
| A42-0266 | Display per row: account code, account name, current balance (for Level 4), actions menu [⋯] | P0 | 2E | [ ] |
| A42-0267 | Actions menu: Edit, Delete (conditional on BR-COA-005/006/007) | P0 | 2E | [ ] |
| A42-0268 | Add [+ Add Account] button — opens Add Account dialog | P0 | 2E | [ ] |
| A42-0269 | Add [Export ▼] dropdown: CSV, JSON | P0 | 2E | [ ] |
| A42-0270 | Add search bar — filter accounts by code or name | P0 | 2E | [ ] |
| A42-0271 | Add level filter dropdown: All Levels, Level 1, Level 2, Level 3, Level 4 | P1 | 2E | [ ] |
| A42-0272 | Show footer summary: total accounts, postable count, last modified date | P1 | 2E | [ ] |

#### 2.5.1 Add/Edit Account Dialog

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0273 | Create React component `AccountFormDialog` (modal) | P0 | 2E | [ ] |
| A42-0274 | Field: Parent Account dropdown (shows group accounts only) | P0 | 2E | [ ] |
| A42-0275 | Field: Level — auto-computed from parent (read-only) | P0 | 2E | [ ] |
| A42-0276 | Field: Root Type — inherited from parent (read-only) | P0 | 2E | [ ] |
| A42-0277 | Field: Account Code — auto-suggest next available under parent | P1 | 2E | [ ] |
| A42-0278 | Field: Account Name (required, max 200 chars) | P0 | 2E | [ ] |
| A42-0279 | Field: Account Type dropdown — filtered by root_type (only valid types) | P1 | 2E | [ ] |
| A42-0280 | Field: Description (optional, max 500 chars, textarea) | P2 | 2E | [ ] |
| A42-0281 | Field: External Code (optional, max 50 chars) | P2 | 2E | [ ] |
| A42-0282 | Field: Sort Order (default 0) | P2 | 2E | [ ] |
| A42-0283 | Client-side validation matching API business rules | P0 | 2E | [ ] |
| A42-0284 | On save: call POST/PUT API, refresh tree, close dialog | P0 | 2E | [ ] |

#### 2.5.2 Materialized Path Service

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0285 | Create `IMaterializedPathService` (or method within CoA service) | P0 | 2B | [ ] |
| A42-0286 | Implement `ComputePath(ChartOfAccount account)` — builds "/1/11/111/1111" from ancestors | P0 | 2B | [ ] |
| A42-0287 | Implement `UpdateSubtreePaths(int accountId)` — recompute paths for all descendants when parent changes | P0 | 2B | [ ] |
| A42-0288 | Subtree query using `LIKE '/1/11/%'` for materialized_path lookups | P0 | 2B | [ ] |
| A42-0289 | Recursive CTE for ancestry traversal query | P1 | 2B | [ ] |

---

## Phase 3 — Fiscal Periods & Defaults (C5, C6)

### 3.1 C5 — Fiscal Years

#### 3.1.1 Entity & Domain Model

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0290 | Create `FiscalYear` domain entity class | P0 | 3A | [ ] |
| A42-0291 | Add property `Id` (int, PK, IDENTITY) | P0 | 3A | [ ] |
| A42-0292 | Add property `FiscalYearName` (nvarchar(100), required) | P0 | 3A | [ ] |
| A42-0293 | Add property `StartDate` (date, required) | P0 | 3A | [ ] |
| A42-0294 | Add property `EndDate` (date, required) | P0 | 3A | [ ] |
| A42-0295 | Add property `IsClosed` (bit, default false) | P0 | 3A | [ ] |
| A42-0296 | Add property `ReportingCurrencyCode` (nvarchar(3), required) — FK to Currencies | P0 | 3A | [ ] |
| A42-0297 | Add property `OrgId` (int, required) | P0 | 3A | [ ] |
| A42-0298 | Implement audit fields + IMultiTenant | P0 | 3A | [ ] |
| A42-0299 | Add navigation property `Periods` → ICollection<FiscalPeriod> | P0 | 3A | [ ] |

#### 3.1.2 Fiscal Year EF Core Configuration & Migration

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0300 | Create `FiscalYearConfiguration` class | P0 | 3A | [ ] |
| A42-0301 | Map to table `FiscalYears` in `finance` schema | P0 | 3A | [ ] |
| A42-0302 | Configure unique index `UX_FiscalYears_Org_Name` on (org_id, fiscal_year_name) | P0 | 3A | [ ] |
| A42-0303 | Configure index `IX_FiscalYears_Org_Dates` on (org_id, start_date, end_date) | P1 | 3A | [ ] |
| A42-0304 | Add CHECK constraint: end_date > start_date | P0 | 3A | [ ] |
| A42-0305 | Validate: no overlapping fiscal year date ranges per org (application logic) | P0 | 3A | [ ] |
| A42-0306 | Configure FK: reporting_currency_code → lookups.Currencies | P0 | 3A | [ ] |
| A42-0307 | Configure HasQueryFilter on org_id | P0 | 3A | [ ] |

### 3.2 C5 — Fiscal Periods

#### 3.2.1 Entity & Domain Model

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0308 | Create `FiscalPeriod` domain entity class | P0 | 3A | [ ] |
| A42-0309 | Add properties: Id, FiscalYearId, PeriodName, PeriodNumber, StartDate, EndDate, Status, OrgId | P0 | 3A | [ ] |
| A42-0310 | Add CHECK constraint: `period_number BETWEEN 1 AND 13` | P0 | 3A | [ ] |
| A42-0311 | Add CHECK constraint: `status IN ('Open', 'Closed', 'Locked')` | P0 | 3A | [ ] |
| A42-0312 | Validate: period dates within fiscal year range | P0 | 3A | [ ] |
| A42-0313 | Validate: no overlapping period dates within fiscal year | P0 | 3A | [ ] |

#### 3.2.2 Fiscal Periods EF Core Configuration & Migration

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0314 | Create `FiscalPeriodConfiguration` class | P0 | 3A | [ ] |
| A42-0315 | Map to table `FiscalPeriods` in `finance` schema | P0 | 3A | [ ] |
| A42-0316 | Configure unique index `UX_FiscalPeriods_Year_Number` on (fiscal_year_id, period_number, org_id) | P0 | 3A | [ ] |
| A42-0317 | Configure index `IX_FiscalPeriods_Org_Status` on (org_id, status) | P1 | 3A | [ ] |
| A42-0318 | Configure index `IX_FiscalPeriods_Org_Dates` on (org_id, start_date, end_date) | P1 | 3A | [ ] |
| A42-0319 | Configure FK: fiscal_year_id → FiscalYears with Cascade Delete | P0 | 3A | [ ] |
| A42-0320 | Write combined migration `CreateFiscalYearsAndPeriods` (M8) | P0 | 3A | [ ] |
| A42-0321 | Verify M8 creates both tables with all indexes and constraints | P0 | 3A | [ ] |

#### 3.2.3 Fiscal Year Auto-Period Generation Service

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0322 | Create `IFiscalYearService` interface | P0 | 3B | [ ] |
| A42-0323 | Implement `CreateFiscalYearAsync()` — create year + auto-generate 12 monthly periods | P0 | 3B | [ ] |
| A42-0324 | Auto-generate Period 13 (Year-End Adjustment) — same start and end as last day of fiscal year | P1 | 3B | [ ] |
| A42-0325 | Set all auto-generated periods to Status = 'Open' | P0 | 3B | [ ] |
| A42-0326 | Validate: no overlapping fiscal year for the tenant (BR-FP-001) | P0 | 3B | [ ] |
| A42-0327 | Support custom period boundaries (4-4-5 calendar, custom splits) | P2 | 3B | [ ] |
| A42-0328 | Update CoaSetupStatuses: fiscal_year_configured = true, status → FiscalYearSet | P0 | 3B | [ ] |
| A42-0329 | Raise `FiscalYearCreatedEvent` | P1 | 3B | [ ] |

#### 3.2.4 Fiscal Period Status Transitions

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0330 | Implement period Lock: status Open → Locked (reversible soft-close, BR-FP-003) | P0 | 3B | [ ] |
| A42-0331 | Implement period Close: status Open/Locked → Closed (final, BR-FP-005) | P0 | 3B | [ ] |
| A42-0332 | Implement period Reopen: status Locked → Open (BR-FP-005) | P0 | 3B | [ ] |
| A42-0333 | Block reopen of Closed period (BR-FP-005) | P0 | 3B | [ ] |
| A42-0334 | Block period close if unposted draft entries exist (BR-FP-004) | P1 | 3B | [ ] |
| A42-0335 | Implement fiscal year close: all periods must be Closed first (BR-FP-006) | P0 | 3B | [ ] |
| A42-0336 | Raise `FiscalPeriodStatusChangedEvent` on each transition | P1 | 3B | [ ] |

### 3.3 C6 — Accounting Defaults

#### 3.3.1 Entity & Domain Model

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0337 | Create `AccountingDefault` domain entity class | P0 | 3C | [ ] |
| A42-0338 | Add properties: Id, DefaultKey, DefaultLabel, AccountId (nullable FK), IsRequired, Category, HelpText, OrgId | P0 | 3C | [ ] |
| A42-0339 | Implement audit fields + IMultiTenant | P0 | 3C | [ ] |

#### 3.3.2 EF Core Configuration & Migration

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0340 | Create `AccountingDefaultConfiguration` class | P0 | 3C | [ ] |
| A42-0341 | Map to table `AccountingDefaults` in `finance` schema | P0 | 3C | [ ] |
| A42-0342 | Configure unique index `UX_AccountingDefaults_Key_Org` on (default_key, org_id) | P0 | 3C | [ ] |
| A42-0343 | Configure index `IX_AccountingDefaults_AccountId` on account_id | P1 | 3C | [ ] |
| A42-0344 | Configure FK: account_id → ChartOfAccounts with SetNull on delete | P0 | 3C | [ ] |
| A42-0345 | Validate: account_id must be Level 4 leaf within same org (application logic) | P0 | 3C | [ ] |
| A42-0346 | Validate: account_id must be active (is_active = true) | P0 | 3C | [ ] |
| A42-0347 | Write migration `CreateAccountingDefaults` (M9) — depends on M7 | P0 | 3C | [ ] |

#### 3.3.3 Default Account Keys — Seed per Tenant

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0348 | Seed 16 default key rows per tenant when Finance module activated (account_id = NULL) | P0 | 3C | [ ] |
| A42-0349 | Seed: DEFAULT_RECEIVABLE (Category=Receivables, Required=Yes, Expected=Receivable) | P0 | 3C | [ ] |
| A42-0350 | Seed: DEFAULT_PAYABLE (Category=Payables, Required=Yes, Expected=Payable) | P0 | 3C | [ ] |
| A42-0351 | Seed: DEFAULT_CASH (Category=Banking, Required=Yes, Expected=Cash) | P0 | 3C | [ ] |
| A42-0352 | Seed: DEFAULT_BANK (Category=Banking, Required=Yes, Expected=Bank) | P0 | 3C | [ ] |
| A42-0353 | Seed: DEFAULT_REVENUE (Category=Revenue, Required=Yes, Expected=Revenue) | P0 | 3C | [ ] |
| A42-0354 | Seed: DEFAULT_COGS (Category=CostOfSales, Required=Yes, Expected=CostOfGoodsSold) | P0 | 3C | [ ] |
| A42-0355 | Seed: DEFAULT_TAX_RECEIVABLE (Category=Tax, Required=No, Expected=Tax) | P1 | 3C | [ ] |
| A42-0356 | Seed: DEFAULT_TAX_PAYABLE (Category=Tax, Required=No, Expected=Tax) | P1 | 3C | [ ] |
| A42-0357 | Seed: DEFAULT_INVENTORY (Category=Inventory, Required=No, Expected=OtherCurrentAsset) | P1 | 3C | [ ] |
| A42-0358 | Seed: DEFAULT_PURCHASE (Category=CostOfSales, Required=No, Expected=Expense) | P1 | 3C | [ ] |
| A42-0359 | Seed: DEFAULT_DISCOUNT_GIVEN (Category=Revenue, Required=No, Expected=Revenue) | P2 | 3C | [ ] |
| A42-0360 | Seed: DEFAULT_DISCOUNT_RECEIVED (Category=CostOfSales, Required=No, Expected=OtherIncome) | P2 | 3C | [ ] |
| A42-0361 | Seed: DEFAULT_RETAINED_EARNINGS (Category=Equity, Required=Yes, Expected=Equity) | P0 | 3C | [ ] |
| A42-0362 | Seed: DEFAULT_OPENING_BALANCE (Category=Equity, Required=Yes, auto-assigned to 3900) | P0 | 3C | [ ] |
| A42-0363 | Seed: DEFAULT_EXCHANGE_GAIN_LOSS (Category=Revenue, Required=No, Expected=OtherIncome) | P2 | 3C | [ ] |
| A42-0364 | Seed: DEFAULT_ROUNDING (Category=Revenue, Required=No, Expected=OtherExpense) | P2 | 3C | [ ] |

#### 3.3.4 Defaults API Endpoints

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0365 | Create `AccountingDefaultsController` | P0 | 3D | [ ] |
| A42-0366 | Implement GET `/api/finance/accounting-defaults` — list all with assigned accounts | P0 | 3D | [ ] |
| A42-0367 | Group response by category for UI rendering | P1 | 3D | [ ] |
| A42-0368 | Implement PUT `/api/finance/accounting-defaults/{default_key}` — assign account | P0 | 3D | [ ] |
| A42-0369 | Validate assigned account is Level 4, active, same org | P0 | 3D | [ ] |
| A42-0370 | Raise `AccountingDefaultAssignedEvent` | P1 | 3D | [ ] |
| A42-0371 | Implement POST `/api/finance/accounting-defaults/validate` — check all required assigned | P0 | 3D | [ ] |
| A42-0372 | Update CoaSetupStatuses: defaults_configured = true, status → DefaultsSet when all required assigned | P0 | 3D | [ ] |
| A42-0373 | Require Finance.Admin authorization | P0 | 3D | [ ] |

#### 3.3.5 Fiscal Year & Period API Endpoints

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0374 | Create `FiscalYearsController` | P0 | 3D | [ ] |
| A42-0375 | Implement GET `/api/finance/fiscal-years` — list fiscal years | P0 | 3D | [ ] |
| A42-0376 | Implement POST `/api/finance/fiscal-years` — create with auto-period generation | P0 | 3D | [ ] |
| A42-0377 | Implement PUT `/api/finance/fiscal-years/{id}` — update name/currency (block date change if periods have postings) | P0 | 3D | [ ] |
| A42-0378 | Implement GET `/api/finance/fiscal-years/{id}/periods` — list periods | P0 | 3D | [ ] |
| A42-0379 | Implement PUT `/api/finance/fiscal-periods/{id}` — update status (Open/Locked/Closed), name, dates | P0 | 3D | [ ] |
| A42-0380 | Require Finance.Admin authorization on create/update | P0 | 3D | [ ] |

### 3.4 Fiscal Year & Defaults UI Panels

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0381 | Create React component `FiscalYearPanel` | P0 | 3E | [ ] |
| A42-0382 | Display fiscal year card: name, date range, currency, status (Active/Closed) | P0 | 3E | [ ] |
| A42-0383 | Display period table: number, name, start, end, status per row | P0 | 3E | [ ] |
| A42-0384 | Add [+ New Year] button — fiscal year creation form (name, start, end, currency) | P0 | 3E | [ ] |
| A42-0385 | Add [Edit Year] button | P1 | 3E | [ ] |
| A42-0386 | Add [Close Year] button (with period-all-closed validation) | P1 | 3E | [ ] |
| A42-0387 | Create React component `AccountingDefaultsPanel` | P0 | 3E | [ ] |
| A42-0388 | Group defaults by category (Receivables, Payables, Banking, Tax, Revenue, CostOfSales, Equity) | P0 | 3E | [ ] |
| A42-0389 | Each row: label, account selector dropdown (Level 4 only), Required/Optional badge | P0 | 3E | [ ] |
| A42-0390 | Lock DEFAULT_OPENING_BALANCE row (auto-assigned to 3900, non-editable) | P0 | 3E | [ ] |
| A42-0391 | Add [Validate All] button — call validate endpoint, show results | P0 | 3E | [ ] |
| A42-0392 | Add [Save Defaults] button | P0 | 3E | [ ] |
| A42-0393 | Show summary: "X of Y required defaults assigned" with checkmark icons | P1 | 3E | [ ] |

---

## Phase 4 — Opening Balances & Setup Gate (C7, C8)

### 4.1 C8 — Opening Balances

#### 4.1.1 Entity & Domain Model

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0394 | Create `OpeningBalanceEntry` domain entity class | P0 | 4A | [ ] |
| A42-0395 | Add properties: Id, AccountId (FK), DebitAmount, CreditAmount, Notes, ConversionDate, EntryStatus, PostedAt, PostedBy, OrgId | P0 | 4A | [ ] |
| A42-0396 | Implement audit fields + IMultiTenant | P0 | 4A | [ ] |
| A42-0397 | Add CHECK constraint: `entry_status IN ('Draft', 'Posted')` | P0 | 4A | [ ] |
| A42-0398 | Validate: debit and credit cannot both be > 0 (BR-OB-003) | P0 | 4A | [ ] |
| A42-0399 | Validate: both amounts >= 0 | P0 | 4A | [ ] |

#### 4.1.2 EF Core Configuration & Migration

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0400 | Create `OpeningBalanceEntryConfiguration` class | P0 | 4A | [ ] |
| A42-0401 | Map to table `OpeningBalanceEntries` in `finance` schema | P0 | 4A | [ ] |
| A42-0402 | Configure unique index `UX_OpeningBalanceEntries_Account_Org` on (account_id, org_id) | P0 | 4A | [ ] |
| A42-0403 | Configure index `IX_OpeningBalanceEntries_OrgId` on org_id | P1 | 4A | [ ] |
| A42-0404 | Configure index `IX_OpeningBalanceEntries_Status` on (org_id, entry_status) | P1 | 4A | [ ] |
| A42-0405 | Configure FK: account_id → ChartOfAccounts (must be Level 4, same org) | P0 | 4A | [ ] |
| A42-0406 | Configure decimal precision: DebitAmount, CreditAmount → (18,4) | P0 | 4A | [ ] |
| A42-0407 | Write migration `CreateOpeningBalanceEntries` (M11) — depends on M7 | P0 | 4A | [ ] |
| A42-0408 | Verify M11 creates table with all indexes and constraints | P0 | 4A | [ ] |

#### 4.1.3 Opening Balance API Endpoints

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0409 | Create `OpeningBalancesController` | P0 | 4B | [ ] |
| A42-0410 | Implement GET `/api/finance/opening-balances` — list all entries grouped by root type | P0 | 4B | [ ] |
| A42-0411 | Implement PUT `/api/finance/opening-balances/batch` — batch update entries | P0 | 4B | [ ] |
| A42-0412 | Accept array of { account_id, debit_amount, credit_amount, notes } | P0 | 4B | [ ] |
| A42-0413 | Validate: each account_id is Level 4 leaf (BR-OB-001) | P0 | 4B | [ ] |
| A42-0414 | Validate: no account has both debit > 0 and credit > 0 (BR-OB-003) | P0 | 4B | [ ] |
| A42-0415 | Create or update OpeningBalanceEntry per account (upsert pattern) | P0 | 4B | [ ] |
| A42-0416 | Block update of Posted entries (BR-OB-004) | P0 | 4B | [ ] |

#### 4.1.4 Opening Balance Posting Logic

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0417 | Implement POST `/api/finance/opening-balances/post` — finalize all drafts | P0 | 4B | [ ] |
| A42-0418 | Calculate total debits vs total credits across all entries | P0 | 4B | [ ] |
| A42-0419 | If debits ≠ credits, compute difference and auto-post to Opening Balance Equity (3900) (BR-OB-005) | P0 | 4B | [ ] |
| A42-0420 | Validate conversion_date within first fiscal year range (BR-OB-006) | P0 | 4B | [ ] |
| A42-0421 | Create journal entry with type = OPENING_BALANCE, date = conversion_date | P0 | 4B | [ ] |
| A42-0422 | Create journal entry lines for each entry with non-zero amounts | P0 | 4B | [ ] |
| A42-0423 | Set entry_status = 'Posted', posted_at, posted_by on all entries | P0 | 4B | [ ] |
| A42-0424 | Update ChartOfAccounts.opening_balance_debit and opening_balance_credit | P0 | 4B | [ ] |
| A42-0425 | Update ChartOfAccounts.current_balance (debit − credit for debit-normal, credit − debit for credit-normal) | P0 | 4B | [ ] |
| A42-0426 | Set ChartOfAccounts.has_transactions = true on affected accounts | P0 | 4B | [ ] |
| A42-0427 | Update CoaSetupStatuses: opening_balances_entered = true, opening_balances_balanced | P0 | 4B | [ ] |
| A42-0428 | Raise `OpeningBalancePostedEvent` (TotalDebit, TotalCredit, Difference, ConversionDate) | P1 | 4B | [ ] |
| A42-0429 | Wrap entire posting in a database transaction | P0 | 4B | [ ] |

#### 4.1.5 Opening Balance Summary Endpoint

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0430 | Implement GET `/api/finance/opening-balances/summary` | P0 | 4B | [ ] |
| A42-0431 | Return: total_debits, total_credits, difference, is_balanced, conversion_date | P0 | 4B | [ ] |

### 4.2 C7 — CoA Setup Status & Module Gate

#### 4.2.1 Entity & Domain Model

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0432 | Create `CoaSetupStatus` domain entity class | P0 | 4C | [ ] |
| A42-0433 | Add properties: Id, Status, TemplateAppliedAt, TemplateId, AccountsCreatedCount, FiscalYearConfigured, DefaultsConfigured, OpeningBalancesEntered, OpeningBalancesBalanced, SetupCompletedAt, SetupCompletedBy, ConversionDate, OrgId | P0 | 4C | [ ] |
| A42-0434 | Add CHECK constraint: Status IN ('NotStarted', 'AccountsCreated', 'FiscalYearSet', 'DefaultsSet', 'BalancesEntered', 'Complete') | P0 | 4C | [ ] |

#### 4.2.2 EF Core Configuration & Migration

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0435 | Create `CoaSetupStatusConfiguration` class | P0 | 4C | [ ] |
| A42-0436 | Map to table `CoaSetupStatuses` in `finance` schema | P0 | 4C | [ ] |
| A42-0437 | Configure unique index `UX_CoaSetupStatuses_OrgId` on org_id (one row per tenant) | P0 | 4C | [ ] |
| A42-0438 | Configure FK: template_id → CoaTemplates (nullable) | P1 | 4C | [ ] |
| A42-0439 | Write migration `CreateCoaSetupStatuses` (M10) — depends on M2 | P0 | 4C | [ ] |
| A42-0440 | Create CoaSetupStatuses row when Finance module is activated per tenant (status = NotStarted) | P0 | 4C | [ ] |

#### 4.2.3 State Machine Implementation

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0441 | Implement state transition validation: NotStarted → AccountsCreated | P0 | 4C | [ ] |
| A42-0442 | Implement state transition: AccountsCreated → FiscalYearSet | P0 | 4C | [ ] |
| A42-0443 | Implement state transition: FiscalYearSet → DefaultsSet | P0 | 4C | [ ] |
| A42-0444 | Implement state transition: DefaultsSet → BalancesEntered | P0 | 4C | [ ] |
| A42-0445 | Implement state transition: BalancesEntered → Complete | P0 | 4C | [ ] |
| A42-0446 | Allow revisiting steps before Complete (e.g., re-apply template resets to AccountsCreated) | P1 | 4C | [ ] |
| A42-0447 | Once Complete: lock conversion date and 5 Level 1 root accounts | P0 | 4C | [ ] |
| A42-0448 | Complete transition requires opening_balances_balanced = true OR tenant acknowledges imbalance | P0 | 4C | [ ] |
| A42-0449 | Raise `CoaSetupStatusChangedEvent` on each transition | P1 | 4C | [ ] |
| A42-0450 | Raise `CoaSetupCompletedEvent` on Complete (CompletedAt, CompletedByUserId, AccountCount) | P1 | 4C | [ ] |

#### 4.2.4 Setup Status API Endpoints

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0451 | Create `SetupStatusController` (exempt from gate middleware) | P0 | 4C | [ ] |
| A42-0452 | Implement GET `/api/finance/setup/status` — current status | P0 | 4C | [ ] |
| A42-0453 | Implement POST `/api/finance/setup/complete` — validate all prerequisites, finalize (BR-MG-003) | P0 | 4C | [ ] |
| A42-0454 | Validate: accounts_created_count > 0 | P0 | 4C | [ ] |
| A42-0455 | Validate: fiscal_year_configured = true | P0 | 4C | [ ] |
| A42-0456 | Validate: defaults_configured = true | P0 | 4C | [ ] |
| A42-0457 | Validate: opening_balances_entered = true | P0 | 4C | [ ] |
| A42-0458 | Implement POST `/api/finance/setup/reset` — reset to NotStarted (BR-MG-004) | P1 | 4C | [ ] |
| A42-0459 | Block reset if journal entries beyond opening balance exist | P0 | 4C | [ ] |

#### 4.2.5 Finance Setup Gate Middleware

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0460 | Create `FinanceSetupGateMiddleware` class | P0 | 4D | [ ] |
| A42-0461 | Intercept all requests to `/api/finance/*` except setup endpoints (BR-MG-001) | P0 | 4D | [ ] |
| A42-0462 | Define setup endpoint exemption list (IsSetupEndpoint method) | P0 | 4D | [ ] |
| A42-0463 | Query CoaSetupStatuses for current tenant | P0 | 4D | [ ] |
| A42-0464 | If status ≠ Complete: return 403 with error body {error: "FINANCE_SETUP_REQUIRED", current_status, redirect_url} | P0 | 4D | [ ] |
| A42-0465 | Register middleware in ASP.NET Core pipeline | P0 | 4D | [ ] |
| A42-0466 | Create React route guard: redirect to `/finance/setup` when status ≠ Complete (BR-MG-002) | P0 | 4D | [ ] |
| A42-0467 | Show banner on redirected page explaining which step is needed | P1 | 4D | [ ] |

### 4.3 Opening Balance Entry UI

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0468 | Create React component `OpeningBalanceEntryPanel` | P0 | 4E | [ ] |
| A42-0469 | Conversion Date picker (date input, validated against fiscal year) | P0 | 4E | [ ] |
| A42-0470 | Flat table: Code, Account Name, Debit, Credit — grouped by root type | P0 | 4E | [ ] |
| A42-0471 | Show only Level 4 (leaf) accounts in the table | P0 | 4E | [ ] |
| A42-0472 | Collapsible root type sections (Assets, Liabilities, Equity, Revenue, Expenses) | P1 | 4E | [ ] |
| A42-0473 | Debit/Credit input fields with numeric formatting (decimal 2 or 4 places) | P0 | 4E | [ ] |
| A42-0474 | Client-side validation: cannot enter both debit and credit > 0 per row | P0 | 4E | [ ] |
| A42-0475 | Running footer totals: Total Debits, Total Credits, Difference | P0 | 4E | [ ] |
| A42-0476 | Auto-compute "→ To Opening Balance Equity" line when difference exists | P0 | 4E | [ ] |
| A42-0477 | Warning banner when debits ≠ credits: "Difference of {amount} will be posted to OBE (3900)" | P0 | 4E | [ ] |
| A42-0478 | Checkbox: "I confirm this difference is intentional" | P0 | 4E | [ ] |
| A42-0479 | Checkbox: "Skip opening balances (new company with zero balances)" | P1 | 4E | [ ] |
| A42-0480 | [Save Draft] button — save entries as Draft status | P0 | 4E | [ ] |
| A42-0481 | [Post & Continue] button — finalize and post (calls POST /opening-balances/post) | P0 | 4E | [ ] |
| A42-0482 | Disable editing after posting (read-only mode for Posted entries) | P0 | 4E | [ ] |
| A42-0483 | Warning for Revenue/Expense accounts: "P&L accounts typically have zero opening balance in a new fiscal year" (BR-OB-007) | P2 | 4E | [ ] |
| A42-0484 | [Help] expandable panel | P2 | 4E | [ ] |

---

## Phase 5 — Wizard, Guide & Integration Hooks (C9, C10)

### 5.1 C9 — CoA Setup Wizard

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0485 | Create React component `CoaSetupWizard` (multi-step container) | P0 | 5A | [ ] |
| A42-0486 | Implement 6-step flow: Template → Accounts → Fiscal Year → Defaults → Opening Balances → Complete | P0 | 5A | [ ] |
| A42-0487 | Progress bar/stepper showing current step and completed steps | P0 | 5A | [ ] |
| A42-0488 | Step 1: integrate TemplateSelectionPanel component | P0 | 5A | [ ] |
| A42-0489 | Step 2: integrate ChartOfAccountsTreeView component (review/customize) | P0 | 5A | [ ] |
| A42-0490 | Step 3: integrate FiscalYearPanel component | P0 | 5A | [ ] |
| A42-0491 | Step 4: integrate AccountingDefaultsPanel component | P0 | 5A | [ ] |
| A42-0492 | Step 5: integrate OpeningBalanceEntryPanel component | P0 | 5A | [ ] |
| A42-0493 | Step 6: Review & Complete summary panel | P0 | 5A | [ ] |
| A42-0494 | Step 6: show summary stats (account count, fiscal year, defaults assigned, balance totals) | P0 | 5A | [ ] |
| A42-0495 | Step 6: [Finalize Setup] button — calls POST /setup/complete | P0 | 5A | [ ] |
| A42-0496 | Navigate between steps with [Back] / [Continue] buttons | P0 | 5A | [ ] |
| A42-0497 | Disable [Continue] until current step's prerequisites are met | P0 | 5A | [ ] |
| A42-0498 | Allow revisiting earlier steps (non-linear navigation within completed steps) | P1 | 5A | [ ] |
| A42-0499 | Auto-redirect to current step based on CoaSetupStatuses.status | P0 | 5A | [ ] |
| A42-0500 | Route: `/finance/setup` → wizard | P0 | 5A | [ ] |

### 5.2 Contextual Help System

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0501 | Create React component `ContextualHelpPanel` (expandable side/bottom panel) | P1 | 5B | [ ] |
| A42-0502 | Help content for Step 1 (Template): What is a CoA? Why templates? Tips for choosing | P1 | 5B | [ ] |
| A42-0503 | Help content for Step 2 (Accounts): Group vs. leaf accounts, 4-level rules, adding/editing | P1 | 5B | [ ] |
| A42-0504 | Help content for Step 3 (Fiscal Year): What is a fiscal year? Period boundaries | P1 | 5B | [ ] |
| A42-0505 | Help content for Step 4 (Defaults): Why defaults? What each default controls | P1 | 5B | [ ] |
| A42-0506 | Help content for Step 5 (Opening Balances): What they are, conversion date, balancing | P1 | 5B | [ ] |
| A42-0507 | Help content for Step 6 (Review): What happens when you finalize | P1 | 5B | [ ] |
| A42-0508 | Each help section: "What is this?", "Why does it matter?", "Tips", "Example" | P1 | 5B | [ ] |
| A42-0509 | [? Help] toggle button on each step | P1 | 5B | [ ] |

### 5.3 C10 — External Financial System Integration Hooks

#### 5.3.1 Entity & Domain Model

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0510 | Create `ExternalSystemMapping` domain entity class | P0 | 5C | [ ] |
| A42-0511 | Add properties: Id, SystemType, SystemName, AccountId (FK), ExternalAccountCode, ExternalAccountName, SyncDirection, IsActive, LastSyncedAt, SyncStatus, SyncErrorMessage, ConnectionConfigJson, OrgId | P0 | 5C | [ ] |
| A42-0512 | Implement audit fields + IMultiTenant | P0 | 5C | [ ] |
| A42-0513 | Add CHECK constraint: system_type IN recognized values (12 types) | P0 | 5C | [ ] |
| A42-0514 | Add CHECK constraint: sync_direction IN ('None', 'Inbound', 'Outbound', 'Bidirectional') | P0 | 5C | [ ] |

#### 5.3.2 EF Core Configuration & Migration

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0515 | Create `ExternalSystemMappingConfiguration` class | P0 | 5C | [ ] |
| A42-0516 | Map to table `ExternalSystemMappings` in `finance` schema | P0 | 5C | [ ] |
| A42-0517 | Configure unique index `UX_ExtSysMappings_Sys_Account_Org` on (system_type, account_id, org_id) | P0 | 5C | [ ] |
| A42-0518 | Configure index `IX_ExtSysMappings_AccountId` on account_id | P1 | 5C | [ ] |
| A42-0519 | Configure index `IX_ExtSysMappings_OrgId` on org_id | P1 | 5C | [ ] |
| A42-0520 | Configure filtered index `IX_ExtSysMappings_SyncDirection` on (org_id, system_type, sync_direction) WHERE is_active = 1 | P1 | 5C | [ ] |
| A42-0521 | Configure FK: account_id → ChartOfAccounts (Level 4, active, same org) | P0 | 5C | [ ] |
| A42-0522 | Write migration `CreateExternalSystemMappings` (M12) — depends on M7 | P0 | 5C | [ ] |
| A42-0523 | Verify M12 creates table with all indexes and constraints | P0 | 5C | [ ] |

#### 5.3.3 External System Types (Validation Constants)

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0524 | Define constants/enum: QUICKBOOKS_ONLINE | P0 | 5C | [ ] |
| A42-0525 | Define constants/enum: QUICKBOOKS_DESKTOP | P1 | 5C | [ ] |
| A42-0526 | Define constants/enum: XERO | P0 | 5C | [ ] |
| A42-0527 | Define constants/enum: ERPNEXT | P1 | 5C | [ ] |
| A42-0528 | Define constants/enum: TALLY | P1 | 5C | [ ] |
| A42-0529 | Define constants/enum: SAGE | P1 | 5C | [ ] |
| A42-0530 | Define constants/enum: SAP_B1 | P1 | 5C | [ ] |
| A42-0531 | Define constants/enum: DYNAMICS_BC | P1 | 5C | [ ] |
| A42-0532 | Define constants/enum: ZOHO_BOOKS | P1 | 5C | [ ] |
| A42-0533 | Define constants/enum: WAVE | P2 | 5C | [ ] |
| A42-0534 | Define constants/enum: ODOO | P1 | 5C | [ ] |
| A42-0535 | Define constants/enum: CUSTOM | P0 | 5C | [ ] |

#### 5.3.4 External Mapping API Endpoints

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0536 | Create `ExternalMappingsController` | P0 | 5D | [ ] |
| A42-0537 | Implement GET `/api/finance/external-mappings` — list with system_type filter | P0 | 5D | [ ] |
| A42-0538 | Implement POST `/api/finance/external-mappings` — create mapping | P0 | 5D | [ ] |
| A42-0539 | Validate: account_id is Level 4, active (BR-INT-002, BR-INT-003) | P0 | 5D | [ ] |
| A42-0540 | Validate: unique per (system_type, account_id, org_id) — no duplicates (BR-INT-001) | P0 | 5D | [ ] |
| A42-0541 | Implement PUT `/api/finance/external-mappings/{id}` — update (external code, direction, active) | P0 | 5D | [ ] |
| A42-0542 | Implement DELETE `/api/finance/external-mappings/{id}` — delete mapping | P0 | 5D | [ ] |
| A42-0543 | Implement POST `/api/finance/external-mappings/import` — CSV import | P1 | 5D | [ ] |
| A42-0544 | CSV import format: sms_account_code, external_code, external_name, direction | P1 | 5D | [ ] |
| A42-0545 | Raise `ExternalMappingCreatedEvent` | P1 | 5D | [ ] |
| A42-0546 | Require Finance.Admin authorization | P0 | 5D | [ ] |
| A42-0547 | Validate connection_config_json never contains plaintext credentials (BR-INT-005) | P1 | 5D | [ ] |

#### 5.3.5 External Mappings UI Panel

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0548 | Create React component `ExternalMappingsPanel` | P0 | 5E | [ ] |
| A42-0549 | System type dropdown filter (QuickBooks Online, Xero, etc.) | P0 | 5E | [ ] |
| A42-0550 | Connection name display | P1 | 5E | [ ] |
| A42-0551 | Mapping table: SMS Code, SMS Account, Ext Code, Ext Name, Direction dropdown | P0 | 5E | [ ] |
| A42-0552 | Direction dropdown per row: None, Inbound, Outbound, Bidirectional | P0 | 5E | [ ] |
| A42-0553 | [+ Add Mapping] button — opens add mapping dialog | P0 | 5E | [ ] |
| A42-0554 | [Import from CSV] button with file upload | P1 | 5E | [ ] |
| A42-0555 | [Export Mappings] button — CSV download | P2 | 5E | [ ] |
| A42-0556 | Show sync status per mapping: last synced, status (Success/Error/Pending/Never) | P2 | 5E | [ ] |
| A42-0557 | Show "Connectors configured in a future addendum" note | P1 | 5E | [ ] |

---

## Phase 6 — Testing & Polish

### 6.1 Unit Tests — Validation Engine

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0558 | Test T-UPL-01: upload valid CSV with 50 accounts → CoA created, status → AccountsCreated | P0 | 6A | [ ] |
| A42-0559 | Test T-UPL-02: upload CSV missing Level 1 Equity → validation error | P0 | 6A | [ ] |
| A42-0560 | Test T-UPL-03: upload CSV with duplicate code → validation error | P0 | 6A | [ ] |
| A42-0561 | Test T-UPL-04: upload CSV with Level 5 → validation error | P0 | 6A | [ ] |
| A42-0562 | Test T-UPL-05: upload CSV with orphan parent → validation error | P0 | 6A | [ ] |
| A42-0563 | Test T-UPL-06: upload CSV with 2001 accounts → validation error | P1 | 6A | [ ] |
| A42-0564 | Test T-UPL-07: upload valid JSON tree → CoA created with correct hierarchy | P0 | 6A | [ ] |
| A42-0565 | Test T-UPL-08: upload malformed JSON → validation error | P0 | 6A | [ ] |
| A42-0566 | Test T-UPL-09: upload CSV L4 is_group=true → validation error | P0 | 6A | [ ] |
| A42-0567 | Test T-UPL-10: upload after journal entries → blocked | P0 | 6A | [ ] |
| A42-0568 | Test: multiple validation errors returned simultaneously | P0 | 6A | [ ] |
| A42-0569 | Test T-COA-01: apply US_GENERAL template → correct hierarchy and materialized_path | P0 | 6A | [ ] |
| A42-0570 | Test T-COA-02: add Level 4 under Level 3 → correct path computation | P0 | 6A | [ ] |
| A42-0571 | Test T-COA-03: add Level 3 under Level 1 → validation error | P0 | 6A | [ ] |
| A42-0572 | Test T-COA-04: add account with wrong root_type → validation error | P0 | 6A | [ ] |
| A42-0573 | Test T-COA-05: delete Level 4 with no transactions → soft-deleted | P0 | 6A | [ ] |
| A42-0574 | Test T-COA-06: delete Level 4 with transactions → blocked | P0 | 6A | [ ] |
| A42-0575 | Test T-COA-07: delete Level 3 with children → blocked | P0 | 6A | [ ] |
| A42-0576 | Test T-COA-08: delete system account 3900 → blocked | P0 | 6A | [ ] |
| A42-0577 | Test T-COA-09: export CoA as CSV → valid CSV | P1 | 6A | [ ] |
| A42-0578 | Test T-COA-10: subtree query via materialized_path LIKE → correct results | P0 | 6A | [ ] |

### 6.2 Unit Tests — Template Operations

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0579 | Test T-TPL-01: list templates filtered by country USA → returns 4 US templates | P0 | 6A | [ ] |
| A42-0580 | Test T-TPL-02: download US_GENERAL as CSV → valid CSV with 5 root types | P0 | 6A | [ ] |
| A42-0581 | Test T-TPL-03: download US_GENERAL as JSON → valid nested tree | P0 | 6A | [ ] |
| A42-0582 | Test T-TPL-04: modify system template → 403 Forbidden | P0 | 6A | [ ] |
| A42-0583 | Test T-TPL-05: preview template with 70 accounts → correct flat list | P1 | 6A | [ ] |

### 6.3 Integration Tests — Full Setup Flow

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0584 | Test: complete setup flow: activate → apply template → fiscal year → defaults → opening balances → complete | P0 | 6B | [ ] |
| A42-0585 | Test T-FP-01: create fiscal year → 12 monthly + 1 adjustment period | P0 | 6B | [ ] |
| A42-0586 | Test T-FP-02: overlapping fiscal year → validation error | P0 | 6B | [ ] |
| A42-0587 | Test T-FP-03: lock period → status Locked, postings blocked | P0 | 6B | [ ] |
| A42-0588 | Test T-FP-04: close period → status Closed, reopen blocked | P0 | 6B | [ ] |
| A42-0589 | Test T-FP-05: close year with open periods → error | P0 | 6B | [ ] |
| A42-0590 | Test T-OB-01: balanced opening balances → balanced flag true | P0 | 6B | [ ] |
| A42-0591 | Test T-OB-02: unbalanced → difference posted to 3900 | P0 | 6B | [ ] |
| A42-0592 | Test T-OB-03: post → status Posted, CoA balances updated, has_transactions = true | P0 | 6B | [ ] |
| A42-0593 | Test T-OB-04: modify posted balance → error | P0 | 6B | [ ] |
| A42-0594 | Test T-OB-05: skip opening balances → proceed with zeros | P1 | 6B | [ ] |
| A42-0595 | Test T-OB-06: opening balance on group account → error | P0 | 6B | [ ] |
| A42-0596 | Test T-OB-07: both debit and credit > 0 → error | P0 | 6B | [ ] |
| A42-0597 | Test T-SS-01: new tenant activates Finance → status NotStarted | P0 | 6B | [ ] |
| A42-0598 | Test T-SS-02: apply template → AccountsCreated | P0 | 6B | [ ] |
| A42-0599 | Test T-SS-03: create fiscal year → FiscalYearSet | P0 | 6B | [ ] |
| A42-0600 | Test T-SS-04: assign all defaults → DefaultsSet | P0 | 6B | [ ] |
| A42-0601 | Test T-SS-05: post opening balances → BalancesEntered | P0 | 6B | [ ] |
| A42-0602 | Test T-SS-06: complete → Complete, modules unlocked | P0 | 6B | [ ] |
| A42-0603 | Test T-SS-07: access /api/finance/invoices before Complete → 403 | P0 | 6B | [ ] |
| A42-0604 | Test T-SS-08: access /api/finance/setup/status before Complete → 200 OK | P0 | 6B | [ ] |
| A42-0605 | Test T-SS-09: reset after only opening balances → NotStarted | P1 | 6B | [ ] |
| A42-0606 | Test T-SS-10: reset after journal entries → blocked | P0 | 6B | [ ] |

### 6.4 Integration Tests — External Mappings

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0607 | Test T-EXT-01: create QB Online mapping → success | P0 | 6B | [ ] |
| A42-0608 | Test T-EXT-02: duplicate mapping → unique constraint violation | P0 | 6B | [ ] |
| A42-0609 | Test T-EXT-03: import 20 mappings from CSV → 20 rows created | P1 | 6B | [ ] |
| A42-0610 | Test T-EXT-04: map group account → validation error | P0 | 6B | [ ] |
| A42-0611 | Test T-EXT-05: map inactive account → validation error | P0 | 6B | [ ] |

### 6.5 Multi-Tenant Isolation Tests

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0612 | Test T-COA-11: Tenant A accounts not visible to Tenant B (HasQueryFilter) | P0 | 6C | [ ] |
| A42-0613 | Test: Tenant A's fiscal years not visible to Tenant B | P0 | 6C | [ ] |
| A42-0614 | Test: Tenant A's accounting defaults not visible to Tenant B | P0 | 6C | [ ] |
| A42-0615 | Test: Tenant A's opening balances not visible to Tenant B | P0 | 6C | [ ] |
| A42-0616 | Test: Tenant A's setup status not visible to Tenant B | P0 | 6C | [ ] |
| A42-0617 | Test: Tenant A's external mappings not visible to Tenant B | P0 | 6C | [ ] |
| A42-0618 | Test: system templates (org_id = NULL) visible to all tenants | P0 | 6C | [ ] |
| A42-0619 | Test: tenant-created custom template visible only to that tenant | P0 | 6C | [ ] |

### 6.6 UI Polish & Verification

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0620 | Verify template selection panel responsive at mobile width | P1 | 6D | [ ] |
| A42-0621 | Verify account tree view responsive (horizontal scroll for wide trees) | P1 | 6D | [ ] |
| A42-0622 | Verify opening balance table responsive (horizontal scroll for debit/credit columns) | P1 | 6D | [ ] |
| A42-0623 | Verify wizard step navigation responsive | P1 | 6D | [ ] |
| A42-0624 | Verify all finance UI panels in dark mode | P1 | 6D | [ ] |
| A42-0625 | Verify all form validation messages display correctly | P1 | 6D | [ ] |
| A42-0626 | Verify all loading states (spinners, skeletons) during API calls | P2 | 6D | [ ] |
| A42-0627 | Verify error handling for failed API calls (toast/inline errors) | P1 | 6D | [ ] |
| A42-0628 | Verify numeric formatting consistency (decimal places, thousand separators) | P1 | 6D | [ ] |

---

## Phase 6+ — Domain Events (Cross-Cutting)

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0629 | Define `CoaTemplateAppliedEvent` domain event class | P1 | 2B | [ ] |
| A42-0630 | Define `CoaUploadedEvent` domain event class | P1 | 2C | [ ] |
| A42-0631 | Define `ChartOfAccountCreatedEvent` domain event class | P1 | 2D | [ ] |
| A42-0632 | Define `ChartOfAccountUpdatedEvent` domain event class | P1 | 2D | [ ] |
| A42-0633 | Define `ChartOfAccountDeletedEvent` domain event class | P1 | 2D | [ ] |
| A42-0634 | Define `FiscalYearCreatedEvent` domain event class | P1 | 3B | [ ] |
| A42-0635 | Define `FiscalPeriodStatusChangedEvent` domain event class | P1 | 3B | [ ] |
| A42-0636 | Define `AccountingDefaultAssignedEvent` domain event class | P1 | 3D | [ ] |
| A42-0637 | Define `OpeningBalancePostedEvent` domain event class | P1 | 4B | [ ] |
| A42-0638 | Define `CoaSetupStatusChangedEvent` domain event class | P1 | 4C | [ ] |
| A42-0639 | Define `CoaSetupCompletedEvent` domain event class | P1 | 4C | [ ] |
| A42-0640 | Define `ExternalMappingCreatedEvent` domain event class | P1 | 5D | [ ] |

---

## Finance Module Dashboard UI (Post-Setup)

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A42-0641 | Create React component `FinanceDashboard` (post-setup landing page) | P1 | 5A | [ ] |
| A42-0642 | Summary tiles: Total Assets, Total Liabilities, Total Equity | P1 | 5A | [ ] |
| A42-0643 | Navigation links: Chart of Accounts, Fiscal Periods, Defaults, Opening Balances, Integrations, Setup Status | P1 | 5A | [ ] |

---

## Summary

| Category | Count |
|---|---|
| Database & Schema (C1) | 8 |
| CoA Templates Entity & Config (C2) | 27 |
| CoA Template Items Entity & Config (C3) | 31 |
| Template Seed Data | 19 |
| CSV/JSON Export Services | 12 |
| Template API Endpoints | 13 |
| Template Selection UI | 15 |
| ChartOfAccounts Entity & Config (C4) | 48 |
| Apply Template Service | 15 |
| Upload Service (CSV/JSON) | 26 |
| CoA CRUD API Endpoints | 33 |
| CoA Tree View UI + Dialog | 22 |
| Materialized Path Service | 5 |
| Fiscal Years Entity & Config (C5) | 19 |
| Fiscal Periods Entity & Config | 14 |
| Auto-Period Generation & Transitions | 15 |
| Accounting Defaults Entity & Config (C6) | 30 |
| Defaults & Fiscal API Endpoints | 16 |
| Fiscal Year & Defaults UI | 13 |
| Opening Balances Entity & Config (C8) | 15 |
| Opening Balance API & Posting Logic | 23 |
| Setup Status Entity & State Machine (C7) | 29 |
| Finance Gate Middleware | 8 |
| Opening Balance Entry UI | 17 |
| Setup Wizard (C9) | 16 |
| Contextual Help System | 9 |
| External System Mappings Entity (C10) | 14 |
| External System Types Constants | 12 |
| External Mapping API Endpoints | 12 |
| External Mappings UI | 10 |
| Unit Tests — Validation & Templates | 26 |
| Integration Tests — Full Flow | 28 |
| Multi-Tenant Isolation Tests | 8 |
| UI Polish & Verification | 9 |
| Domain Events | 12 |
| Finance Dashboard | 3 |
| **TOTAL** | **643** |

---

*End of Task Register — FSD Addendum 42*
