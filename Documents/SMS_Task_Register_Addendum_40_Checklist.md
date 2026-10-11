# SMS Task Register — FSD Addendum 40

## Data Import & Upload Framework

| Property | Value |
|---|---|
| Document Ref | SMS-FSD-ADD-040 |
| Total Changes | 9 (C1–C9) |
| Migrations | 10 (M040.1–M040.10) |
| New Schema | 1 (`import`) |
| New Tables | 6 |
| API Endpoints | 17 |
| Permissions | 6 new |
| Business Rules | 24 |
| Test Scenarios | 30 |
| Dev Phases | 8 (26 days) |
| Depends On | SMS-FSD-ADD-001 (Core), SMS-FSD-ADD-012 (Finance), SMS-FSD-ADD-038 (POS) |
| NuGet Packages | ClosedXML 0.102+ (MIT), CsvHelper 31+ (Apache 2.0) |

---

## Phase 1 — Import Schema + Profile Tables (3 days)

### M040.1 — Create Import Schema

- [ ] Execute `CREATE SCHEMA [import]` (with `IF NOT EXISTS` guard)

### M040.2 — Create `import.ImportProfiles` Table

- [ ] Create table `import.ImportProfiles` with all 17 columns (`import_profile_id` BIGINT PK IDENTITY, `org_id` BIGINT NULL, `entity_type` TINYINT NOT NULL, `name` NVARCHAR(200) NOT NULL, `description` NVARCHAR(500) NULL, `target_schema` VARCHAR(50) NOT NULL, `target_table` VARCHAR(100) NOT NULL, `match_fields` NVARCHAR(300) NOT NULL, `duplicate_strategy` TINYINT NOT NULL DEFAULT 0, `import_order` INT NOT NULL DEFAULT 0, `max_rows_per_file` INT NOT NULL DEFAULT 50000, `max_file_size_mb` INT NOT NULL DEFAULT 10, `is_opening_balance` BIT NOT NULL DEFAULT 0, `is_system` BIT NOT NULL DEFAULT 1, `is_active` BIT NOT NULL DEFAULT 1, `created_at` DATETIME2(2) NOT NULL DEFAULT SYSUTCDATETIME(), `created_by` BIGINT NULL, `modified_at` DATETIME2(2) NOT NULL DEFAULT SYSUTCDATETIME(), `modified_by` BIGINT NULL)
- [ ] Add FK `org_id → tenant.Organizations(organization_id)`
- [ ] Add FK `created_by → auth.Users(user_id)`
- [ ] Add FK `modified_by → auth.Users(user_id)`
- [ ] Add filtered UNIQUE index `UQ_ImportProfiles_OrgEntityType (org_id, entity_type) WHERE org_id IS NOT NULL` — one custom profile per entity per org
- [ ] Add index `IX_ImportProfiles_EntityType (entity_type, is_active)` — lookup by entity type
- [ ] Add index `IX_ImportProfiles_OrgOrder (org_id, is_active, import_order)` — migration wizard sort

### M040.3 — Create `import.ImportProfileFields` Table

- [ ] Create table `import.ImportProfileFields` with all 20 columns (`import_profile_field_id` BIGINT PK IDENTITY, `import_profile_id` BIGINT NOT NULL, `field_name` VARCHAR(100) NOT NULL, `display_name` NVARCHAR(200) NOT NULL, `description` NVARCHAR(500) NULL, `field_type` TINYINT NOT NULL, `is_required` BIT NOT NULL DEFAULT 0, `is_key` BIT NOT NULL DEFAULT 0, `is_template_column` BIT NOT NULL DEFAULT 1, `max_length` INT NULL, `min_value` DECIMAL(18,4) NULL, `max_value` DECIMAL(18,4) NULL, `decimal_places` TINYINT NULL, `validation_regex` NVARCHAR(500) NULL, `default_value` NVARCHAR(500) NULL, `lookup_entity_type` TINYINT NULL, `lookup_match_field` VARCHAR(100) NULL, `lookup_dropdown` BIT NOT NULL DEFAULT 0, `sort_order` INT NOT NULL DEFAULT 0, `sample_value` NVARCHAR(200) NULL)
- [ ] Add FK `import_profile_id → import.ImportProfiles(import_profile_id) ON DELETE CASCADE`
- [ ] Add UNIQUE index `UQ_ImportProfileFields_FieldName (import_profile_id, field_name)` — one field per name per profile
- [ ] Add index `IX_ImportProfileFields_Order (import_profile_id, sort_order)` — ordered field list

### M040.8 — Seed System Import Profiles

- [ ] Seed 16 system import profiles (org_id = NULL, is_system = 1) with IDENTITY INSERT ON
- [ ] Profile 1: Customer Import — entity_type=0, target `customers.Customers`, match_fields `["code","email"]`, import_order=8
- [ ] Profile 2: Supplier Import — entity_type=1, target `procurement.Suppliers`, match_fields `["code","email"]`, import_order=9
- [ ] Profile 3: Product Import — entity_type=2, target `inventory.Products`, match_fields `["sku"]`, import_order=3
- [ ] Profile 4: Product Category Import — entity_type=3, target `inventory.ProductCategories`, match_fields `["code"]`, import_order=2
- [ ] Profile 5: Unit of Measure Import — entity_type=4, target `inventory.Units`, match_fields `["code"]`, import_order=1
- [ ] Profile 6: Warehouse Import — entity_type=5, target `inventory.Warehouses`, match_fields `["code"]`, import_order=4
- [ ] Profile 7: Warehouse Location Import — entity_type=6, target `inventory.WarehouseLocations`, match_fields `["code"]`, import_order=5
- [ ] Profile 8: Chart of Accounts Import — entity_type=7, target `finance.Accounts`, match_fields `["account_code"]`, import_order=6
- [ ] Profile 9: Tax Code Import — entity_type=8, target `finance.TaxCodes`, match_fields `["code"]`, import_order=7
- [ ] Profile 10: Contact Import — entity_type=9, target `core.Contacts`, match_fields `["email"]`, import_order=10
- [ ] Profile 11: Price List Item Import — entity_type=10, target `inventory.PriceListItems`, match_fields `["product_sku","price_list_code"]`, import_order=12
- [ ] Profile 12: Employee Import — entity_type=11, target `hr.Employees`, match_fields `["employee_code"]`, import_order=11
- [ ] Profile 13: Opening Stock Import — entity_type=12, is_opening_balance=1, match_fields `["product_sku","warehouse_code"]`, import_order=13
- [ ] Profile 14: Customer Opening Balance Import — entity_type=13, is_opening_balance=1, match_fields `["customer_code"]`, import_order=14
- [ ] Profile 15: Supplier Opening Balance Import — entity_type=14, is_opening_balance=1, match_fields `["supplier_code"]`, import_order=15
- [ ] Profile 16: GL Opening Balance Import — entity_type=15, is_opening_balance=1, match_fields `["account_code"]`, import_order=16
- [ ] IDENTITY INSERT OFF after seeding

### M040.9 — Seed Import Profile Fields

- [ ] Seed Customer Import fields (15 fields: external_id, code, name, email, phone, address_line1, address_line2, city, state_province, postal_code, country_code, tax_number, credit_limit, payment_terms_days, is_active)
- [ ] Seed Supplier Import fields (12 fields: external_id, code, name, email, phone, address_line1, city, country_code, tax_number, payment_terms_days, currency_code, is_active)
- [ ] Seed Product Import fields (14 fields: external_id, sku, name, description, category_code, unit_code, sale_price, cost_price, barcode, weight, min_stock_level, max_stock_level, is_service, is_active)
- [ ] Seed Product Category Import fields (6 fields: external_id, code, name, parent_code, description, is_active)
- [ ] Seed Unit of Measure Import fields (6 fields: external_id, code, name, category, ratio, is_active)
- [ ] Seed Warehouse Import fields (7 fields: external_id, code, name, address, city, country_code, is_active)
- [ ] Seed Warehouse Location Import fields (fields matching WarehouseLocations entity + external_id)
- [ ] Seed Chart of Accounts Import fields (8 fields: external_id, account_code, name, account_type, parent_code, currency_code, is_reconcilable, is_active)
- [ ] Seed Tax Code Import fields (fields matching TaxCodes entity + external_id)
- [ ] Seed Contact Import fields (fields matching Contacts entity + external_id)
- [ ] Seed Price List Item Import fields (fields matching PriceListItems entity + external_id)
- [ ] Seed Employee Import fields (fields matching Employees entity + external_id)
- [ ] Seed Opening Stock Import fields (7 fields: product_sku, warehouse_code, location_code, quantity, unit_cost, batch_number, expiry_date)
- [ ] Seed Customer Opening Balance Import fields (6 fields: customer_code, invoice_reference, invoice_date, due_date, amount, currency_code)
- [ ] Seed Supplier Opening Balance Import fields (6 fields: supplier_code, bill_reference, bill_date, due_date, amount, currency_code)
- [ ] Seed GL Opening Balance Import fields (4 fields: account_code, debit_amount, credit_amount, description)
- [ ] Set correct `field_type` for each field (0=Text, 1=Integer, 2=Decimal, 3=Date, 4=Boolean, 5=Lookup, 6=Email, 7=Phone, 8=Currency)
- [ ] Set `is_required = true` on mandatory fields per entity (e.g., sku, name, quantity)
- [ ] Set `is_key = true` on natural key fields per entity (e.g., sku, code, account_code)
- [ ] Set `lookup_entity_type` and `lookup_match_field` on all Lookup fields (e.g., category_code → ProductCategory → code)
- [ ] Set `lookup_dropdown = true` on lookup fields that should have Excel data validation dropdowns
- [ ] Set `sample_value` for all fields (realistic sample data)

### C1 — ImportProfileService

- [ ] Create `ImportEntityType` enum with 16 values (Customer=0 through GLOpeningBalance=15)
- [ ] Create `DuplicateStrategy` enum with 4 values (Skip=0, Update=1, CreateNew=2, Error=3)
- [ ] Create `ImportProfile` EF Core entity class with all 17 columns
- [ ] Create `ImportProfileField` EF Core entity class with all 20 columns
- [ ] Configure EF entity mappings in `ImportDbContext` — schema `import`, table names, FK relationships
- [ ] Create `IImportProfileService` interface
- [ ] Implement `GetProfilesAsync(long orgId)` — returns system profiles + org-specific custom profiles, sorted by `import_order`
- [ ] Implement `GetProfileWithFieldsAsync(long orgId, long profileId)` — profile + ordered fields list
- [ ] Implement `GetProfileByEntityTypeAsync(long orgId, ImportEntityType entityType)` — org-specific first, fall back to system
- [ ] Register service in DI container

### Phase 1 — API Endpoints

- [ ] `GET /api/import/profiles` — list import profiles for the org (auth: `import.profile.view`). Return system + custom profiles sorted by import_order
- [ ] `GET /api/import/profiles/{id}` — get profile with field definitions (auth: `import.profile.view`). Include all ImportProfileFields sorted by sort_order

### Phase 1 — Permissions

- [ ] Register permission `import.profile.view` — "View Import Profiles" — default: all roles
- [ ] Register permission `import.profile.manage` — "Manage Import Profiles" — default: Admin only

### Phase 1 — Unit Tests

- [ ] Test: GetProfilesAsync returns system profiles when no custom profiles exist
- [ ] Test: GetProfilesAsync returns org custom profile instead of system when both exist for same entity_type
- [ ] Test: GetProfileWithFieldsAsync returns fields in sort_order
- [ ] Test: 16 system profiles seeded correctly with correct entity_type, match_fields, and import_order
- [ ] Test: Profile fields seeded with correct field_type, is_required, is_key for each entity

---

## Phase 2 — Session + Mapping + Rows (4 days)

### M040.4 — Create `import.ImportSessions` Table

- [ ] Create table `import.ImportSessions` with all 24 columns (`import_session_id` BIGINT PK IDENTITY, `org_id` BIGINT NOT NULL, `import_profile_id` BIGINT NOT NULL, `file_name` NVARCHAR(500) NOT NULL, `file_size_bytes` BIGINT NOT NULL, `file_type` TINYINT NOT NULL, `sheet_name` NVARCHAR(200) NULL, `duplicate_strategy` TINYINT NOT NULL DEFAULT 0, `total_rows` INT NOT NULL DEFAULT 0, `valid_rows` INT NOT NULL DEFAULT 0, `invalid_rows` INT NOT NULL DEFAULT 0, `imported_rows` INT NOT NULL DEFAULT 0, `updated_rows` INT NOT NULL DEFAULT 0, `skipped_rows` INT NOT NULL DEFAULT 0, `error_rows` INT NOT NULL DEFAULT 0, `status` TINYINT NOT NULL DEFAULT 0, `validation_started_at` DATETIME2(2) NULL, `validation_completed_at` DATETIME2(2) NULL, `import_started_at` DATETIME2(2) NULL, `import_completed_at` DATETIME2(2) NULL, `error_message` NVARCHAR(MAX) NULL, `hangfire_job_id` VARCHAR(200) NULL, `saved_mapping_name` NVARCHAR(200) NULL, `notes` NVARCHAR(1000) NULL, `created_at` DATETIME2(2) NOT NULL DEFAULT SYSUTCDATETIME(), `created_by` BIGINT NOT NULL)
- [ ] Add FK `org_id → tenant.Organizations(organization_id)`
- [ ] Add FK `import_profile_id → import.ImportProfiles(import_profile_id)`
- [ ] Add FK `created_by → auth.Users(user_id)`
- [ ] Add index `IX_ImportSessions_OrgStatus (org_id, status, created_at DESC)` — session listing
- [ ] Add index `IX_ImportSessions_OrgProfile (org_id, import_profile_id, created_at DESC)` — sessions by entity type
- [ ] Add filtered index `IX_ImportSessions_Hangfire (hangfire_job_id) WHERE hangfire_job_id IS NOT NULL` — job lookup

### M040.5 — Create `import.ImportColumnMappings` Table

- [ ] Create table `import.ImportColumnMappings` with all 8 columns (`import_column_mapping_id` BIGINT PK IDENTITY, `import_session_id` BIGINT NOT NULL, `source_column_index` INT NOT NULL, `source_column_name` NVARCHAR(200) NOT NULL, `target_field_name` VARCHAR(100) NULL, `target_field_id` BIGINT NULL, `is_auto_mapped` BIT NOT NULL DEFAULT 0, `confidence_score` DECIMAL(5,2) NULL)
- [ ] Add FK `import_session_id → import.ImportSessions(import_session_id) ON DELETE CASCADE`
- [ ] Add FK `target_field_id → import.ImportProfileFields(import_profile_field_id)`
- [ ] Add UNIQUE index `UQ_ImportColumnMappings_SourceCol (import_session_id, source_column_index)` — one mapping per source column
- [ ] Add index `IX_ImportColumnMappings_TargetField (import_session_id, target_field_name)` — lookup by target

### M040.6 — Create `import.ImportRows` Table

- [ ] Create table `import.ImportRows` with all 7 columns (`import_row_id` BIGINT PK IDENTITY, `import_session_id` BIGINT NOT NULL, `row_number` INT NOT NULL, `status` TINYINT NOT NULL DEFAULT 0, `raw_data` NVARCHAR(MAX) NOT NULL, `created_entity_id` BIGINT NULL, `error_details` NVARCHAR(MAX) NULL)
- [ ] Add FK `import_session_id → import.ImportSessions(import_session_id) ON DELETE CASCADE`
- [ ] Add index `IX_ImportRows_Status (import_session_id, status)` — filter by status
- [ ] Add index `IX_ImportRows_RowNumber (import_session_id, row_number)` — ordered row access

### C3 — ImportSessionService

- [ ] Create `ImportSessionStatus` enum with 9 values (Uploaded=0, Mapping=1, Validating=2, Validated=3, Importing=4, Completed=5, CompletedWithErrors=6, Failed=7, Cancelled=8)
- [ ] Create `ImportRowStatus` enum with 7 values (Pending=0, Valid=1, Invalid=2, Imported=3, Skipped=4, Updated=5, Error=6)
- [ ] Create `ImportFileType` enum with 2 values (XLSX=0, CSV=1)
- [ ] Create `ImportSession` EF Core entity class with all 24 columns
- [ ] Create `ImportColumnMapping` EF Core entity class with all 8 columns
- [ ] Create `ImportRow` EF Core entity class with all 7 columns
- [ ] Configure EF entity mappings — FKs, CASCADE deletes, JSON columns
- [ ] Create `IImportSessionService` interface
- [ ] Implement `CreateSessionAsync(long orgId, long userId, IFormFile file, long profileId, DuplicateStrategy strategy, string? sheetName)`:
  - [ ] Validate file extension (.xlsx or .csv only) — rule IMP-FILE-01
  - [ ] Validate file size against profile's `max_file_size_mb` — rule IMP-FILE-02
  - [ ] Parse file headers (ClosedXML for xlsx, CsvHelper for csv)
  - [ ] Count data rows and validate against `max_rows_per_file` — rule IMP-FILE-03
  - [ ] Validate header row exists — rule IMP-FILE-04
  - [ ] Validate at least 1 data row — rule IMP-FILE-05
  - [ ] Create ImportSession record with status=Uploaded
  - [ ] Parse all data rows and store as ImportRow records (raw_data as JSON objects)
  - [ ] Trigger auto-mapping of source columns
  - [ ] Return session with auto-mapping proposals
- [ ] Implement `GetSessionAsync(long orgId, long sessionId)` — session with counts and status
- [ ] Implement `ListSessionsAsync(long orgId, int page, int pageSize, ImportEntityType? entityType)` — paginated, sorted by created_at DESC
- [ ] Implement `GetPreviewAsync(long sessionId, int rowCount = 20)` — first N rows with mapped field values
- [ ] Implement `CancelSessionAsync(long sessionId)` — set status=Cancelled, cancel Hangfire job if running
- [ ] Implement `GetErrorsAsync(long sessionId, int page, int pageSize)` — paginated invalid/error rows
- [ ] Enforce rule IMP-FILE-07: reject mapping/strategy changes when status ≥ Importing
- [ ] Enforce rule IMP-FILE-08: block concurrent imports of same entity type per org

### C6 — ColumnMappingService (Auto-Mapping)

- [ ] Create `IColumnMappingService` interface with methods: `AutoMapColumnsAsync`, `ApplyMappingsAsync`, `SaveMappingPresetAsync`, `LoadMappingPresetAsync`
- [ ] Implement 4-tier matching algorithm in `AutoMapColumnsAsync`:
  - [ ] Tier 1: Exact match — source header matches `field_name` or `display_name` (case-insensitive), confidence = 1.00
  - [ ] Tier 2: Normalized match — remove spaces/underscores/hyphens and lowercase, confidence = 0.95
  - [ ] Tier 3: Common alias match — predefined alias dictionary per field (e.g., "Item Code" → `sku`, "Material No" → `sku`, "Part Number" → `sku`), confidence = 0.90
  - [ ] Tier 4: Fuzzy match — Levenshtein distance ≤ 2 on normalized names, confidence = 0.70–0.89
- [ ] Do not auto-map if confidence < 0.70
- [ ] Auto-map external_id for columns named "external_id", "ext_id", "legacy_id", "source_id" — rule IMP-MAP-04
- [ ] Implement `ApplyMappingsAsync(long sessionId, List<ColumnMappingUpdate> mappings)`:
  - [ ] Enforce rule IMP-MAP-01: all required fields must be mapped
  - [ ] Enforce rule IMP-MAP-02: at least one key field mapped
  - [ ] Enforce rule IMP-MAP-03: one-to-one mapping (no duplicate source→target or target←source)
  - [ ] Update session status to Mapping
- [ ] Implement `SaveMappingPresetAsync(long sessionId, string presetName)` — store preset name on session
- [ ] Implement `LoadMappingPresetAsync(long sessionId, string presetName)` — find most recent session with that preset name, copy its column mappings
- [ ] Register service in DI container

### Phase 2 — API Endpoints

- [ ] `POST /api/import/sessions` — upload file + create session (auth: `import.session.upload`). Multipart form: file + profile_id + duplicate_strategy + sheet_name + mapping_preset. Return 201 with session ID, auto-mappings, unmapped columns
- [ ] `GET /api/import/sessions` — list sessions for org (auth: `import.session.upload`). Paginated, sorted by created_at DESC. Filter by entity_type optional
- [ ] `GET /api/import/sessions/{id}` — get session details with counts and status (auth: `import.session.upload`)
- [ ] `GET /api/import/sessions/{id}/preview` — get first 20 rows mapped to SMS field names (auth: `import.session.upload`)
- [ ] `GET /api/import/sessions/{id}/mappings` — get current column mappings with confidence scores (auth: `import.session.upload`)
- [ ] `PUT /api/import/sessions/{id}/mappings` — update column mappings after user adjustments (auth: `import.session.upload`). Validate rules IMP-MAP-01 through IMP-MAP-03
- [ ] `POST /api/import/sessions/{id}/mappings/save` — save current mapping as named preset (auth: `import.session.upload`)

### Phase 2 — Permissions

- [ ] Register permission `import.session.upload` — "Upload Import Files" — default: all roles
- [ ] Register permission `import.session.manage` — "Manage Import Sessions" — default: Manager, Admin

### Phase 2 — Unit Tests

- [ ] Test: CreateSessionAsync rejects .xls files with correct error message
- [ ] Test: CreateSessionAsync rejects files > max_file_size_mb
- [ ] Test: CreateSessionAsync rejects files > max_rows_per_file
- [ ] Test: CreateSessionAsync rejects empty files (0 data rows)
- [ ] Test: CreateSessionAsync rejects files with no header row
- [ ] Test: Auto-mapping exact match returns confidence 1.00
- [ ] Test: Auto-mapping normalized match ("Product Name" → product_name) returns confidence 0.95
- [ ] Test: Auto-mapping alias match ("Item Code" → sku) returns confidence 0.90
- [ ] Test: Auto-mapping fuzzy match ("Prodct Name" → product_name) returns confidence 0.70–0.89
- [ ] Test: Auto-mapping does not map below 0.70 confidence
- [ ] Test: ApplyMappingsAsync rejects when required fields unmapped
- [ ] Test: ApplyMappingsAsync rejects duplicate target field mappings
- [ ] Test: SaveMappingPresetAsync stores preset name on session
- [ ] Test: LoadMappingPresetAsync copies mappings from most recent session with that preset
- [ ] Test: Session status transition Uploaded → Mapping works correctly
- [ ] Test: Concurrent import block (IMP-FILE-08) — reject second session for same entity type while one is Importing

---

## Phase 3 — External References + Template Engine (3 days)

### M040.7 — Create `import.ExternalReferences` Table

- [ ] Create table `import.ExternalReferences` with all 8 columns (`external_reference_id` BIGINT PK IDENTITY, `org_id` BIGINT NOT NULL, `entity_type` TINYINT NOT NULL, `entity_id` BIGINT NOT NULL, `external_id` NVARCHAR(200) NOT NULL, `source_system` NVARCHAR(100) NULL, `import_session_id` BIGINT NULL, `created_at` DATETIME2(2) NOT NULL DEFAULT SYSUTCDATETIME())
- [ ] Add FK `org_id → tenant.Organizations(organization_id)`
- [ ] Add FK `import_session_id → import.ImportSessions(import_session_id)`
- [ ] Add UNIQUE index `UQ_ExternalReferences_ExternalId (org_id, entity_type, external_id)` — one external ID per entity type per org
- [ ] Add UNIQUE index `UQ_ExternalReferences_EntityId (org_id, entity_type, entity_id)` — one reference per SMS record per entity type
- [ ] Add index `IX_ExternalReferences_Session (import_session_id)` — all references from one import

### C4 — ExternalReferenceService

- [ ] Create `ExternalReference` EF Core entity class with all 8 columns
- [ ] Configure EF entity mapping — schema, table, FKs, unique indexes
- [ ] Create `IExternalReferenceService` interface
- [ ] Implement `ResolveExternalIdAsync(long orgId, ImportEntityType entityType, string externalId)` — returns nullable entity_id. Query by (org_id, entity_type, external_id)
- [ ] Implement `RegisterExternalReferenceAsync(long orgId, ImportEntityType entityType, long entityId, string externalId, string? sourceSystem, long? sessionId)` — create ExternalReference record
- [ ] Implement `BulkResolveAsync(long orgId, ImportEntityType entityType, List<string> externalIds)` — batch resolve for performance during import
- [ ] Register service in DI container

### C5 — ImportTemplateService (ClosedXML)

- [ ] Install NuGet package: `ClosedXML 0.102+` (MIT license)
- [ ] Install NuGet package: `CsvHelper 31+` (Apache 2.0)
- [ ] Create `IImportTemplateService` interface with methods: `GenerateTemplateAsync`, `GenerateMigrationWorkbookAsync`
- [ ] Implement `GenerateTemplateAsync(long orgId, long profileId)`:
  - [ ] Load profile and fields from DB
  - [ ] Create Excel workbook with ClosedXML
  - [ ] **Sheet 1: "Data"** — create as active sheet:
    - [ ] Row 1: column headers from `display_name` — bold, frozen row
    - [ ] Row 2: help text from field `description` — grey italic font
    - [ ] Row 3: sample data from `sample_value` — light blue background
    - [ ] Rows 4+: empty for user data
    - [ ] Required fields: red background on header cell
    - [ ] external_id column (always column A): add cell note explaining upsert behavior
  - [ ] Apply data validation per field type:
    - [ ] Lookup fields with `lookup_dropdown = true`: populate dropdown from current org data (e.g., all active category codes)
    - [ ] Boolean fields: dropdown with "Yes" / "No"
    - [ ] Date fields: Excel date format validation
    - [ ] Decimal fields: number validation with min_value/max_value and decimal_places
  - [ ] **Sheet 2: "Instructions"** — create with:
    - [ ] Title row: "Import Instructions — {profile.Name}"
    - [ ] Instruction lines (fill required fields, delete sample row, max rows info)
    - [ ] Field reference table: field name, required?, type, description
  - [ ] **Sheet 3: "Lookups"** (hidden) — populate reference data for dropdown validation lists:
    - [ ] Query org's current data for each lookup field (active categories, units, warehouses, etc.)
    - [ ] Store in named ranges for data validation references
  - [ ] Return `byte[]` of Excel file
- [ ] Implement `GenerateMigrationWorkbookAsync(long orgId)`:
  - [ ] Load all 16 system profiles in import_order
  - [ ] Create single Excel workbook with 16 sheets named by sequence + entity name:
    - [ ] "1 - Units", "2 - Categories", "3 - Products", "4 - Warehouses", "5 - Locations", "6 - Accounts", "7 - Tax Codes", "8 - Customers", "9 - Suppliers", "10 - Contacts", "11 - Employees", "12 - Price List Items", "13 - Opening Stock", "14 - Customer Balances", "15 - Supplier Balances", "16 - GL Balances"
  - [ ] Each sheet uses same template structure as individual template (headers, help row, sample row, validation)
  - [ ] Share lookup data across sheets where applicable
  - [ ] Return `byte[]` of Excel file
- [ ] Register service in DI container

### Phase 3 — API Endpoints

- [ ] `GET /api/import/profiles/{id}/template` — download Excel template for profile (auth: `import.session.upload`). Return file download with content-type `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet`
- [ ] `GET /api/import/migration-workbook` — download full migration workbook (auth: `import.session.upload`). Return single Excel file with all 16 sheets

### Phase 3 — Unit Tests

- [ ] Test: ResolveExternalIdAsync returns entity_id when reference exists
- [ ] Test: ResolveExternalIdAsync returns null when reference does not exist
- [ ] Test: RegisterExternalReferenceAsync creates record with correct fields
- [ ] Test: RegisterExternalReferenceAsync fails on duplicate (org_id, entity_type, external_id)
- [ ] Test: GenerateTemplateAsync produces valid Excel with 3 sheets
- [ ] Test: Template Sheet 1 has correct headers matching profile fields in sort_order
- [ ] Test: Template required fields have red header background
- [ ] Test: Template lookup fields have dropdown validation populated from org data
- [ ] Test: Template boolean fields have Yes/No dropdown
- [ ] Test: GenerateMigrationWorkbookAsync produces Excel with 16 sheets in correct order
- [ ] Test: Migration workbook sheet names match dependency sequence

---

## Phase 4 — Validation Engine (3 days)

### C7 — ImportValidationService

- [ ] Create `IImportValidationService` interface with method `ValidateSessionAsync(long sessionId)`
- [ ] Create `ValidationResult` class with TotalRows, ValidRows, InvalidRows, Errors list
- [ ] Create `ValidationError` class with RowNumber, FieldName, ErrorCode, Message, SourceValue
- [ ] Create `ILookupResolverService` interface with methods: `ResolveAsync`, `BuildLookupCacheAsync`
- [ ] Implement `LookupCache` class:
  - [ ] In-memory dictionary per lookup entity type
  - [ ] `TryResolve(field, value, out entityId)` method
  - [ ] Case-insensitive matching — rule IMP-VAL-03
- [ ] Implement `BuildLookupCacheAsync(long orgId, long sessionId)`:
  - [ ] Query all required lookup entities from DB (categories, units, warehouses, accounts, etc.)
  - [ ] Build cache keyed by (entity_type, match_field_value_lowercase)
  - [ ] Return populated LookupCache
- [ ] Implement `ValidateSessionAsync(long sessionId)`:
  - [ ] Load session, profile fields, column mappings
  - [ ] Build lookup cache via `BuildLookupCacheAsync`
  - [ ] Load all ImportRows for session, ordered by row_number
  - [ ] Initialize key tracker (HashSet) for in-file duplicate detection
  - [ ] For each row, deserialize raw_data JSON and validate each mapped field:
    - [ ] **REQUIRED check** — `is_required = true` and value is blank/null → error code `REQUIRED`
    - [ ] **Type validation** — parse value as declared type → error code `INVALID_TYPE`
    - [ ] **Max length** — text value > max_length → error code `MAX_LENGTH`
    - [ ] **Min/Max value** — numeric outside bounds → error code `OUT_OF_RANGE`
    - [ ] **Decimal precision** — more decimals than allowed → error code `INVALID_PRECISION`
    - [ ] **Regex pattern** — doesn't match validation_regex → error code `PATTERN_MISMATCH`
    - [ ] **Email format** — invalid email → error code `INVALID_EMAIL`
    - [ ] **Phone format** — invalid phone → error code `INVALID_PHONE`
    - [ ] **Lookup resolution** — value not in cache → error code `LOOKUP_NOT_FOUND`
    - [ ] **In-file duplicate** — key value already seen in file → error code `DUPLICATE_IN_FILE`
    - [ ] **DB duplicate check** — record exists and strategy=Error → error code `DUPLICATE_IN_DB`
    - [ ] **Date range** — date outside reasonable range → error code `INVALID_DATE`
  - [ ] Set row.Status = Valid or Invalid based on error count
  - [ ] Serialize errors to row.ErrorDetails as JSON array
  - [ ] Update session valid_rows, invalid_rows counts
  - [ ] Set session status = Validated, validation_completed_at = UtcNow
  - [ ] SaveChangesAsync
  - [ ] Return ValidationResult
- [ ] Implement value parsing helpers:
  - [ ] Boolean parsing: accept "true"/"false", "yes"/"no", "1"/"0", "y"/"n" (case-insensitive) — rule IMP-VAL-04
  - [ ] Date parsing: accept ISO 8601, common formats (MM/DD/YYYY, DD-Mon-YYYY), Excel date serial numbers — rule IMP-VAL-05
  - [ ] Decimal parsing: accept both `.` and `,` as decimal separator, strip thousand separators — rule IMP-VAL-06
- [ ] Apply whitespace trimming to all values before validation — rule IMP-VAL-07
- [ ] Support re-validation (status reset to Mapping, re-run) — rule IMP-VAL-08
- [ ] Register services in DI container

### Phase 4 — API Endpoints

- [ ] `POST /api/import/sessions/{id}/validate` — run validation pass (auth: `import.session.execute`). Return ValidationResult with valid/invalid counts and error summary

### Phase 4 — Permissions

- [ ] Register permission `import.session.execute` — "Execute Import" — default: Accountant, Manager, Admin

### Phase 4 — Unit Tests

- [ ] Test: Required field validation — blank value → REQUIRED error
- [ ] Test: Type validation — "abc" in decimal field → INVALID_TYPE error
- [ ] Test: Max length — text exceeding max_length → MAX_LENGTH error
- [ ] Test: Out of range — negative price with min_value=0 → OUT_OF_RANGE error
- [ ] Test: Decimal precision — 3 decimals when only 2 allowed → INVALID_PRECISION error
- [ ] Test: Email validation — invalid format → INVALID_EMAIL error
- [ ] Test: Lookup not found — nonexistent category code → LOOKUP_NOT_FOUND error
- [ ] Test: In-file duplicate — same SKU on rows 3 and 45 → DUPLICATE_IN_FILE on row 45
- [ ] Test: DB duplicate with strategy=Error → DUPLICATE_IN_DB error
- [ ] Test: DB duplicate with strategy=Skip → row passes validation (skipped at execution time)
- [ ] Test: Boolean parsing accepts all variants (true/false, yes/no, 1/0, y/n)
- [ ] Test: Date parsing accepts ISO 8601, MM/DD/YYYY, DD-Mon-YYYY
- [ ] Test: Decimal parsing handles both `.` and `,` separators
- [ ] Test: Whitespace trimming applied before validation
- [ ] Test: Lookup resolution is case-insensitive ("electronics" matches "ELECTRONICS")
- [ ] Test: All valid rows → session status = Validated with correct counts
- [ ] Test: Mixed valid/invalid → correct counts for each

---

## Phase 5 — Import Execution Pipeline (4 days)

### C8 — Entity Import Handlers

- [ ] Create `IEntityImportHandler` interface with `EntityType` property, `CreateAsync`, and `UpdateAsync` methods
- [ ] Create `IEntityImportHandlerFactory` interface with `GetHandler(ImportEntityType)` method
- [ ] Implement `EntityImportHandlerFactory` — resolve handler from DI by entity type
- [ ] Implement `CustomerImportHandler` — map row data to Customer entity, handle code/email matching, set created_by
- [ ] Implement `SupplierImportHandler` — map row data to Supplier entity, handle code/email matching
- [ ] Implement `ProductImportHandler` — map row data to Product entity, resolve category_code → category_id via LookupCache, resolve unit_code → unit_id
- [ ] Implement `ProductCategoryImportHandler` — handle self-referencing parent_code hierarchy (parent must exist or be earlier in file)
- [ ] Implement `UnitImportHandler` — map row data to Unit entity
- [ ] Implement `WarehouseImportHandler` — map row data to Warehouse entity
- [ ] Implement `WarehouseLocationImportHandler` — map row data to WarehouseLocation, resolve warehouse_code → warehouse_id
- [ ] Implement `AccountImportHandler` — handle self-referencing parent_code hierarchy, map account_type string to enum
- [ ] Implement `TaxCodeImportHandler` — resolve account references
- [ ] Implement `ContactImportHandler` — resolve customer or supplier reference
- [ ] Implement `PriceListItemImportHandler` — resolve product_sku → product_id, price_list_code → price_list_id
- [ ] Implement `EmployeeImportHandler` — map row data to Employee entity
- [ ] Register all 12 standard handlers in DI container as `IEntityImportHandler`

### C8 — ImportExecutionService

- [ ] Create `IImportExecutionService` interface with methods: `ExecuteAsync`, `GetProgressAsync`, `CancelAsync`
- [ ] Create `ImportExecutionResult` class with SessionId, IsBackground, HangfireJobId, counts
- [ ] Create `ImportProgress` class with SessionId, Processed, Total, Status
- [ ] Implement `ExecuteAsync(long sessionId)`:
  - [ ] Load session, verify status = Validated
  - [ ] Check valid_rows count against background threshold (default 500)
  - [ ] If valid_rows > threshold: queue Hangfire job, set hangfire_job_id, return IsBackground=true — rule IMP-EXC-06
  - [ ] If valid_rows ≤ threshold: process synchronously, call ProcessBatchAsync — rule IMP-EXC-06
  - [ ] Set session status = Importing, import_started_at = UtcNow
  - [ ] After sync completion: set status = Completed or CompletedWithErrors, import_completed_at = UtcNow
- [ ] Implement `ProcessBatchAsync(long sessionId, int batchSize = 100)`:
  - [ ] Load session, get handler from factory
  - [ ] Build lookup cache
  - [ ] Load all Valid rows ordered by row_number
  - [ ] Process in chunks of batchSize — rule IMP-EXC-02:
    - [ ] Begin transaction per batch
    - [ ] For each row, deserialize raw_data
    - [ ] Check external_id first via ExternalReferenceService.ResolveExternalIdAsync
    - [ ] If not found by external_id, check match fields via handler
    - [ ] If existing record found, apply duplicate strategy:
      - [ ] Skip → row.Status = Skipped, increment skipped_rows — rule IMP-EXC-04
      - [ ] Update → handler.UpdateAsync, row.Status = Updated, increment updated_rows — rule IMP-EXC-04
      - [ ] CreateNew → handler.CreateAsync, row.Status = Imported — rule IMP-EXC-04
    - [ ] If no existing record, handler.CreateAsync:
      - [ ] Set row.Status = Imported, row.CreatedEntityId = new entity ID
      - [ ] Register ExternalReference if external_id provided — rule IMP-EXC-03
      - [ ] Increment imported_rows
    - [ ] Set created_by/modified_by on imported records — rule IMP-EXC-05
    - [ ] SaveChangesAsync and commit transaction
    - [ ] Send SignalR progress update `ImportProgress` — rule IMP-EXC-08
    - [ ] On batch exception: rollback transaction, set batch rows to Error status with error_details
- [ ] Implement `GetProgressAsync(long sessionId)` — return current counts from session
- [ ] Implement `CancelAsync(long sessionId)`:
  - [ ] If background: cancel Hangfire job
  - [ ] Set session status = Cancelled — rule IMP-EXC-07
  - [ ] Already-committed batches preserved
- [ ] Only process Valid rows (skip Invalid) — rule IMP-EXC-01
- [ ] Register service in DI container

### C8 — Hangfire Background Job

- [ ] Create `ImportExecutionJob` class
- [ ] Add `[Queue("import")]` attribute
- [ ] Add `[AutomaticRetry(Attempts = 0)]` — no auto-retry on import failures
- [ ] Implement `ExecuteAsync(long sessionId, CancellationToken cancellationToken)` — call ProcessBatchAsync
- [ ] After processing: set session status = Completed or CompletedWithErrors, set import_completed_at

### C8 — SignalR Hub

- [ ] Create `ImportHub : Hub` for real-time progress updates
- [ ] Register hub endpoint at `/hubs/import`
- [ ] Inject `IHubContext<ImportHub>` into ImportExecutionService
- [ ] Send `ImportProgress` event: `{sessionId, processed, total}` after each batch

### Phase 5 — API Endpoints

- [ ] `POST /api/import/sessions/{id}/execute` — start import execution (auth: `import.session.execute`). Return ImportExecutionResult with IsBackground flag and HangfireJobId
- [ ] `POST /api/import/sessions/{id}/cancel` — cancel in-progress import (auth: `import.session.execute`). Return success/failure
- [ ] `GET /api/import/sessions/{id}/errors` — get error details paginated (auth: `import.session.upload`). Return rows with status=Invalid or Error including error_details JSON
- [ ] `GET /api/import/sessions/{id}/errors/download` — download error report as Excel (auth: `import.session.upload`). Return Excel file with original data + error columns, failed rows highlighted red
- [ ] `GET /api/import/sessions/{id}/progress` — get real-time progress (auth: `import.session.upload`). Return {processed, total, status}

### Phase 5 — Error Report Excel Generation

- [ ] Create `IImportErrorReportService` interface with `GenerateErrorReportAsync(long sessionId)`
- [ ] Implement error report generation using ClosedXML:
  - [ ] Include all original columns from uploaded file
  - [ ] Add "Status" column — Valid/Invalid/Error
  - [ ] Add "Error Details" column — concatenated error messages
  - [ ] Highlight failed rows in red background
  - [ ] Group errors by error code in a summary sheet
  - [ ] Return `byte[]` of Excel file

### Phase 5 — Unit Tests

- [ ] Test: ExecuteAsync processes synchronously when valid_rows ≤ 500
- [ ] Test: ExecuteAsync queues Hangfire job when valid_rows > 500
- [ ] Test: ProcessBatchAsync creates new records correctly (CreateAsync called, row.Status = Imported)
- [ ] Test: ProcessBatchAsync handles Skip duplicate strategy (row.Status = Skipped)
- [ ] Test: ProcessBatchAsync handles Update duplicate strategy (UpdateAsync called, row.Status = Updated)
- [ ] Test: External reference registered on successful import with external_id
- [ ] Test: Re-import with existing external_id resolves to existing entity (upsert)
- [ ] Test: Batch transaction rollback on exception — all batch rows marked Error
- [ ] Test: CancelAsync stops future batches, preserves committed batches
- [ ] Test: SignalR progress sent after each batch
- [ ] Test: ProductImportHandler resolves category_code and unit_code via LookupCache
- [ ] Test: ProductCategoryImportHandler handles parent_code hierarchy correctly
- [ ] Test: Error report Excel generated with correct structure and highlighted rows

---

## Phase 6 — Opening Balance Handlers (3 days)

### C9 — OpeningStockImportHandler

- [ ] Implement `OpeningStockImportHandler : IEntityImportHandler`
- [ ] EntityType = ImportEntityType.OpeningStock
- [ ] `CreateAsync`:
  - [ ] Resolve product_sku → product_id via LookupCache
  - [ ] Resolve warehouse_code → warehouse_id via LookupCache
  - [ ] Optionally resolve location_code → location_id
  - [ ] Parse quantity and unit_cost
  - [ ] Create StockMovement (type=OpeningBalance, direction=In, quantity, unit_cost, total_cost)
  - [ ] Update or create StockLevel (QuantityOnHand += quantity, TotalCost += quantity × unit_cost)
  - [ ] Generate journal entry via `_journalService.CreateOpeningStockEntryAsync`: DR Inventory Asset, CR Opening Balance Equity
  - [ ] Handle optional batch_number and expiry_date
  - [ ] Return StockMovement ID
- [ ] `UpdateAsync` → throw NotSupportedException ("Opening balances cannot be updated via import. Reverse and re-import.") — rule IMP-OB-06
- [ ] Validate quantity ≥ 0 — rule IMP-OB-04
- [ ] Handle single opening per product+warehouse — rule IMP-OB-03

### C9 — CustomerBalanceImportHandler

- [ ] Implement `CustomerBalanceImportHandler : IEntityImportHandler`
- [ ] EntityType = ImportEntityType.CustomerOpeningBalance
- [ ] `CreateAsync`:
  - [ ] Resolve customer_code → customer_id via LookupCache
  - [ ] Parse amount, invoice_date, due_date
  - [ ] Create opening balance journal entry: DR Accounts Receivable (customer sub-ledger), CR Opening Balance Equity
  - [ ] Set journal entry date per fiscal year start — rule IMP-OB-05
  - [ ] Handle optional invoice_reference and currency_code
  - [ ] Return journal entry ID
- [ ] `UpdateAsync` → throw NotSupportedException — rule IMP-OB-06

### C9 — SupplierBalanceImportHandler

- [ ] Implement `SupplierBalanceImportHandler : IEntityImportHandler`
- [ ] EntityType = ImportEntityType.SupplierOpeningBalance
- [ ] `CreateAsync`:
  - [ ] Resolve supplier_code → supplier_id via LookupCache
  - [ ] Parse amount, bill_date, due_date
  - [ ] Create opening balance journal entry: DR Opening Balance Equity, CR Accounts Payable (supplier sub-ledger)
  - [ ] Set journal entry date per fiscal year start — rule IMP-OB-05
  - [ ] Handle optional bill_reference and currency_code
  - [ ] Return journal entry ID
- [ ] `UpdateAsync` → throw NotSupportedException — rule IMP-OB-06

### C9 — GLBalanceImportHandler

- [ ] Implement `GLBalanceImportHandler : IEntityImportHandler`
- [ ] EntityType = ImportEntityType.GLOpeningBalance
- [ ] Override standard per-row processing to batch all rows into a single journal entry:
  - [ ] Resolve all account_code → account_id via LookupCache
  - [ ] Sum total debits and total credits
  - [ ] Validate total debits = total credits — rule IMP-OB-02
  - [ ] If unbalanced, throw BusinessException with debit, credit, and difference amounts
  - [ ] Create single opening balance journal entry with all rows as journal lines
  - [ ] Set journal entry date = organization's fiscal year start date — rule IMP-OB-05
  - [ ] Return journal entry ID
- [ ] `UpdateAsync` → throw NotSupportedException — rule IMP-OB-06

### C9 — Opening Balance Registration

- [ ] Register all 4 opening balance handlers in DI container
- [ ] Enforce `import.opening.balance` permission for entity types 12–15 — rule IMP-OB-01

### Phase 6 — Permissions

- [ ] Register permission `import.opening.balance` — "Import Opening Balances" — default: Accountant, Admin

### Phase 6 — Unit Tests

- [ ] Test: OpeningStockImportHandler creates StockMovement with type=OpeningBalance, direction=In
- [ ] Test: OpeningStockImportHandler updates StockLevel (quantity and cost incremented)
- [ ] Test: OpeningStockImportHandler creates journal entry DR Inventory Asset, CR Opening Balance Equity
- [ ] Test: OpeningStockImportHandler rejects negative quantity (rule IMP-OB-04)
- [ ] Test: OpeningStockImportHandler throws on UpdateAsync (rule IMP-OB-06)
- [ ] Test: CustomerBalanceImportHandler creates journal entry DR AR, CR Opening Balance Equity
- [ ] Test: SupplierBalanceImportHandler creates journal entry DR Opening Balance Equity, CR AP
- [ ] Test: GLBalanceImportHandler validates total debits = total credits
- [ ] Test: GLBalanceImportHandler rejects unbalanced file with correct error message showing difference
- [ ] Test: GLBalanceImportHandler creates single journal entry with all rows as lines
- [ ] Test: All opening balance handlers use fiscal year start date for journal entries
- [ ] Test: Opening balance import requires `import.opening.balance` permission

---

## Phase 7 — Import UI + Migration Wizard (4 days)

### Import Dashboard (`/settings/import`)

- [ ] Create `ImportDashboard` React page component
- [ ] Display entity type cards in a grid: Customers, Suppliers, Products, Categories, Units, Warehouses, Accounts, Tax Codes, Employees
- [ ] Display Opening Balances section with cards: Opening Stock, Customer Balances, Supplier Balances, GL Balances
- [ ] Each card links to upload wizard for that entity type
- [ ] Display "Download Migration Workbook" button — calls `GET /api/import/migration-workbook`
- [ ] Display "Recent Imports" list — calls `GET /api/import/sessions` — shows entity type, date, row count, status badge (Completed / Errors / In Progress)
- [ ] Click on recent import row navigates to session detail/results
- [ ] Check `import.profile.view` permission for page access

### Upload Wizard — Step 1: Upload File (`/settings/import/{entityType}/upload`)

- [ ] Create `ImportUploadStep` React component
- [ ] Display drag-and-drop file upload zone (accepts .xlsx, .csv)
- [ ] Display file constraints: max size, max rows
- [ ] Display "Download Template" button — calls `GET /api/import/profiles/{id}/template`
- [ ] Display duplicate strategy radio buttons: Skip (default), Update, Error
- [ ] Display "Load saved mapping" dropdown — populated from previous sessions with saved_mapping_name
- [ ] On file drop/select: call `POST /api/import/sessions` with multipart form data
- [ ] Handle upload errors: file type, size, row count, missing headers — show user-friendly messages
- [ ] Check `import.session.upload` permission
- [ ] Navigate to Step 2 on successful upload

### Upload Wizard — Step 2: Column Mapping

- [ ] Create `ImportMappingStep` React component
- [ ] Display file info bar: file name, sheet name, total rows
- [ ] Display source-to-target mapping table:
  - [ ] Source column name (from uploaded file)
  - [ ] Arrow (→)
  - [ ] Target field dropdown (SMS fields from profile + "— Ignore —" option)
  - [ ] Match indicator: ✓ exact, ~ fuzzy, — manual, ✗ unmapped
- [ ] Pre-populate with auto-mapped columns from upload response
- [ ] Highlight required fields that are not yet mapped
- [ ] Disable "Next: Preview" button if any required field is unmapped (rule IMP-MAP-01)
- [ ] Display "Save mapping as..." input + button — calls `POST /api/import/sessions/{id}/mappings/save`
- [ ] On user adjustment: call `PUT /api/import/sessions/{id}/mappings`
- [ ] Navigate to Step 3 on confirmation

### Upload Wizard — Step 3: Preview & Validate

- [ ] Create `ImportPreviewStep` React component
- [ ] Display preview table with first 10–20 rows mapped to SMS field names — calls `GET /api/import/sessions/{id}/preview`
- [ ] Highlight cells with potential issues (lookup values shown with warning icon if not matching)
- [ ] Display "Run Validation" button — calls `POST /api/import/sessions/{id}/validate`
- [ ] Show loading spinner during validation
- [ ] Display validation results panel:
  - [ ] Summary: ✓ Valid count, ⚠ Invalid count, Total
  - [ ] Error list: row number, field name, error code, message
  - [ ] Error count grouped by error code
- [ ] Display "Download Error Report" button — calls `GET /api/import/sessions/{id}/errors/download`
- [ ] Display "Import N valid rows" button (enabled only when valid_rows > 0) — check `import.session.execute` permission
- [ ] Display "Cancel" button
- [ ] Navigate to Step 4 on execute

### Upload Wizard — Step 4: Results

- [ ] Create `ImportResultsStep` React component
- [ ] For synchronous imports: show results immediately
- [ ] For background imports: connect to SignalR hub `/hubs/import`, display progress bar
  - [ ] Listen for `ImportProgress` event: update progress bar with processed/total
  - [ ] On completion: display final results
- [ ] Display result summary cards: Imported count, Updated count, Skipped count
- [ ] Display import duration
- [ ] Display file and profile info
- [ ] Display "Download Full Results Report" button
- [ ] Display navigation buttons: "Import More {EntityType}", "Back to Import Dashboard"

### Migration Wizard (`/settings/import/migration`)

- [ ] Create `MigrationWizard` React page component
- [ ] Display guided checklist of all 16 entity types in dependency order
- [ ] For each entity type show:
  - [ ] Step number and entity name
  - [ ] Status indicator: ✓ completed (with row count), ◉ current, ○ not started
  - [ ] [Import] button linking to upload wizard for that entity type
  - [ ] [View] link for completed imports (links to session results)
- [ ] Separate "Opening Balances" section for entity types 12–15
- [ ] Display "Download Migration Workbook" button at bottom
- [ ] Track completion status by checking for Completed import sessions per entity type

### Phase 7 — Routing

- [ ] Add route `/settings/import` → ImportDashboard
- [ ] Add route `/settings/import/:entityType/upload` → ImportUploadWizard (4-step)
- [ ] Add route `/settings/import/migration` → MigrationWizard
- [ ] Add route `/settings/import/sessions/:id` → Session detail/results view
- [ ] Add navigation link in Settings sidebar: "Data Import"

---

## Phase 8 — Testing + Error Report + Feature Flag (2 days)

### M040.10 — Module Registration & Feature Flag

- [ ] Register import module in `system.Modules`: module_code='IMPORT', name='Data Import', schema_name='import', is_core=1, is_enabled_by_default=1, sort_order=170
- [ ] Register feature flag in `system.FeatureFlags`: module_code='IMPORT', flag_code='DATA_IMPORT', name='Data Import', is_enabled_by_default=1

### End-to-End Test Scenarios

- [ ] TS-040-01: Upload valid .xlsx with template headers → session created, auto-mapped confidence 1.0, status=Uploaded
- [ ] TS-040-02: Upload valid .csv file → session created, CSV parsed correctly
- [ ] TS-040-03: Upload file >10 MB → rejection with size error
- [ ] TS-040-04: Upload file >50,000 rows → rejection with row count error
- [ ] TS-040-05: Upload .xls (old format) → rejection with format upgrade message
- [ ] TS-040-06: Upload file with no header row → rejection with header error
- [ ] TS-040-07: Upload SAP-style headers (MATNR, MAKTX) → alias auto-mapping, confidence 0.90
- [ ] TS-040-08: Upload with unmapped required fields → Validate button disabled
- [ ] TS-040-09: Save column mapping preset "SAP Export" → preset available on next import
- [ ] TS-040-10: Load saved mapping preset → columns auto-mapped per preset
- [ ] TS-040-11: Validate file with all valid rows → all Valid, Import button enabled
- [ ] TS-040-12: Validate with missing required field (blank SKU) → REQUIRED error on correct row
- [ ] TS-040-13: Validate with invalid type (text in decimal) → INVALID_TYPE error
- [ ] TS-040-14: Validate with lookup not found (category "XYZ") → LOOKUP_NOT_FOUND with suggestion
- [ ] TS-040-15: Validate with duplicate key in file → DUPLICATE_IN_FILE on second occurrence
- [ ] TS-040-16: Validate with duplicate in DB (strategy=Error) → DUPLICATE_IN_DB error
- [ ] TS-040-17: Validate with duplicate in DB (strategy=Skip) → Valid at validation, Skipped at execution
- [ ] TS-040-18: Download error report → Excel with original data + error columns, red highlights
- [ ] TS-040-19: Import 50 products (sync) → all created, status=Completed
- [ ] TS-040-20: Import 1,000 products (background) → Hangfire job, SignalR progress, status=Completed
- [ ] TS-040-21: Import with strategy=Update → existing product updated, modified_by set
- [ ] TS-040-22: Import with external_id for new record → ExternalReference registered
- [ ] TS-040-23: Re-import with same external_id (Update) → existing record updated, no duplicate
- [ ] TS-040-24: Cancel running background import → future batches stopped, committed preserved
- [ ] TS-040-25: Opening stock import (100 products × 3 warehouses) → StockMovements + StockLevels + journal entries
- [ ] TS-040-26: GL opening balance (balanced) → single journal entry with all lines
- [ ] TS-040-27: GL opening balance (unbalanced) → validation error with debit/credit/difference amounts
- [ ] TS-040-28: Download Product Import template → Excel with headers, help row, sample, dropdowns
- [ ] TS-040-29: Download migration workbook → 16 sheets in dependency order
- [ ] TS-040-30: Import categories with parent hierarchy → parents created first, children reference parents

### Edge Case Testing

- [ ] Test: Empty string values treated as NULL during import
- [ ] Test: Unicode characters in product names and descriptions handled correctly
- [ ] Test: Very long text values truncated with appropriate error if exceeding max_length
- [ ] Test: CSV with quoted fields containing commas parsed correctly
- [ ] Test: Excel file with multiple sheets — correct sheet selection
- [ ] Test: Import session cleanup — verify rows older than 90 days eligible for archival
- [ ] Test: Concurrent import block — second import for same entity type rejected while first is Importing
- [ ] Test: Permission enforcement — data entry staff can upload but not execute
- [ ] Test: Permission enforcement — opening balance import requires `import.opening.balance`

---

## Summary Checklist

| Phase | Focus | Days | Task Count |
|---|---|---|---|
| Phase 1 | Import Schema + Profile Tables | 3 | 42 |
| Phase 2 | Session + Mapping + Rows | 4 | 58 |
| Phase 3 | External References + Template Engine | 3 | 42 |
| Phase 4 | Validation Engine | 3 | 42 |
| Phase 5 | Import Execution Pipeline | 4 | 52 |
| Phase 6 | Opening Balance Handlers | 3 | 32 |
| Phase 7 | Import UI + Migration Wizard | 4 | 43 |
| Phase 8 | Testing + Feature Flag | 2 | 42 |
| **Total** | | **26 days** | **353 tasks** |

---

*Task register generated from SMS-FSD-ADD-040 v1.0*
