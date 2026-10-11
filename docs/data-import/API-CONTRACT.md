# Addendum 40 — API contract (Import module ⇄ Angular)

ApiResponse, camelCase, UUIDs in routes, codes as strings. All routes `[RequiresFeature(MODULE_DATA_IMPORT)]`; opening-balance entity types additionally FEATURE_OPENING_BALANCE_IMPORT + IMPORT_OPENING_BALANCE. Decisions: [ADDENDUM-40-ANALYSIS.md](ADDENDUM-40-ANALYSIS.md).

## Codes
Session `status`: `UPLOADED MAPPING VALIDATING VALIDATED IMPORTING COMPLETED COMPLETED_WITH_ERRORS FAILED CANCELLED`. Row `status`: `PENDING VALID INVALID IMPORTED SKIPPED UPDATED ERROR`. `duplicateStrategy`: `SKIP UPDATE CREATE_NEW ERROR`. `fileType`: `XLSX CSV`. Error codes: `REQUIRED INVALID_TYPE MAX_LENGTH OUT_OF_RANGE INVALID_PRECISION PATTERN_MISMATCH INVALID_EMAIL INVALID_PHONE LOOKUP_NOT_FOUND DUPLICATE_IN_FILE DUPLICATE_IN_DB MISSING_DEPENDENCY INVALID_DATE IMPORT_ERROR` (+ target-specific). Match marks: confidence 1.00 exact, 0.95 normalized, 0.90 alias, 0.70–0.89 fuzzy, null manual.
Exact refusals: `"Only .xlsx and .csv files are accepted. Please save the file as .xlsx (Excel 2007+)."` · `"File exceeds the maximum size of {n} MB."` · `"File exceeds the maximum of {n} rows."` · `"No column headers detected in the first row."` · `"The file has no data rows."` · `"Another {entity} import is already running."` · `"Map required field: {display}."` · `"Map at least one key field: {displays}."` · `"Column '{source}' is mapped twice."` / `"Field '{display}' is mapped twice."` · `"Mappings cannot change once the import has started."` · `"Set the opening balance date in Finance settings first."`.

## Profiles
| Route | Permission | Result |
|---|---|---|
| `GET api/import/profiles` | IMPORT_PROFILE_VIEW | `ImportProfile[]` (importOrder; includes `completedSessions` and last completed info for the migration wizard) |
| `GET api/import/profiles/{entityType}` | IMPORT_PROFILE_VIEW | `ImportProfile & { fields: ImportField[] }` |
| `PUT api/import/profiles/{entityType}/settings` | IMPORT_PROFILE_MANAGE | `{ duplicateStrategy, maxRowsPerFile, maxFileSizeMb }` → org override |
| `GET api/import/profiles/{entityType}/template` | IMPORT_UPLOAD | .xlsx (Data / Instructions / hidden Lookups) |
| `GET api/import/migration-workbook` | IMPORT_UPLOAD | .xlsx, one sheet per entity in importOrder ("1 - Units" …) |
```ts
ImportProfile { entityType; name; description?; importOrder; isOpeningBalance; matchFields: string[]; duplicateStrategy; maxRowsPerFile; maxFileSizeMb;
  allowUpdate; requiresOpeningBalancePermission; completedSessions: number; lastCompletedAt?; lastImportedRows? }
ImportField { fieldName; displayName; description?; fieldType; isRequired; isKey; sortOrder; sampleValue?; maxLength?; minValue?; maxValue?; decimalPlaces?; lookupList? }
```

## Sessions
| Route | Permission | Body → Result |
|---|---|---|
| `POST api/import/sessions` | IMPORT_UPLOAD | multipart: `file`, `entityType`, `duplicateStrategy?`, `sheetName?`, `mappingPreset?`, `notes?` → 201 `SessionDetail` (auto mappings, unmappedColumns, unmappedRequiredFields, sheetNames) |
| `GET api/import/sessions?entityType&status&page&pageSize` | IMPORT_UPLOAD (own) / IMPORT_MANAGE (all) | `Paginated<SessionListItem>` newest first |
| `GET api/import/sessions/{uuid}` | same | `SessionDetail` |
| `GET api/import/sessions/{uuid}/preview?rows=20` | same | `{ columns: { fieldName; displayName }[]; rows: { rowNumber; values: Record<string,string|null>; issues?: Record<string,string> }[] }` |
| `GET api/import/sessions/{uuid}/mappings` | same | `ColumnMapping[]` |
| `PUT api/import/sessions/{uuid}/mappings` | IMPORT_UPLOAD | `{ mappings: { sourceColumnIndex; targetFieldName: string|null }[] }` → `SessionDetail` (IMP-MAP-01..03; status MAPPING; resets validation) |
| `GET api/import/mapping-presets?entityType` | IMPORT_UPLOAD | `{ name; lastUsedAt }[]` |
| `POST api/import/sessions/{uuid}/mappings/save` | IMPORT_UPLOAD | `{ presetName }` |
| `POST api/import/sessions/{uuid}/validate` | IMPORT_EXECUTE | `ValidationResult` |
| `POST api/import/sessions/{uuid}/execute` | IMPORT_EXECUTE (+ opening rule) | `{ isBackground; jobId?; session: SessionDetail }` |
| `POST api/import/sessions/{uuid}/cancel` | IMPORT_EXECUTE or IMPORT_MANAGE | `SessionDetail` |
| `GET api/import/sessions/{uuid}/progress` | IMPORT_UPLOAD | `{ processed; total; status }` |
| `GET api/import/sessions/{uuid}/errors?page&pageSize` | IMPORT_UPLOAD | `Paginated<{ rowNumber; status; values; errors: ImportFieldError[] }>` |
| `GET api/import/sessions/{uuid}/errors/download` | IMPORT_UPLOAD | .xlsx: original columns + Status + Error Details, failed rows red, summary sheet by code |
| `GET api/import/sessions/{uuid}/results/download` | IMPORT_UPLOAD | .xlsx: every row with its outcome + entity reference |
```ts
SessionListItem { uuid; entityType; entityName; fileName; status; totalRows; validRows; invalidRows; importedRows; updatedRows; skippedRows; errorRows; createdAt; createdByName }
SessionDetail extends SessionListItem { fileType; fileSizeBytes; sheetName?; sheetNames: string[]; duplicateStrategy; notes?; savedMappingName?;
  validationCompletedAt?; importStartedAt?; importCompletedAt?; errorMessage?; mappings: ColumnMapping[]; unmappedColumns: string[]; unmappedRequiredFields: string[] }
ColumnMapping { sourceColumnIndex; sourceColumnName; targetFieldName?: string|null; targetDisplayName?; isAutoMapped; confidenceScore?: number|null }
ValidationResult { session: SessionDetail; errors: { rowNumber; fieldName; errorCode; message; sourceValue? }[] /* first 200 */; errorCountByCode: Record<string, number> }
```
Hub `/hubs/import` (user group): `ImportProgress { sessionUuid, processed, total, status }`. Background threshold 500 valid rows; batch 100; Hangfire queue `import`, no automatic retry.

## Finance (opening date)
`GET api/finance/opening-balance-date` (FINANCE view perms or IMPORT_OPENING_BALANCE) → `{ date: 'yyyy-MM-dd'|null }`; `PUT` (FINANCE_SETUP manage) `{ date }`.

## Frontend routes
`/portal/pages/settings/import` (dashboard), `/portal/pages/settings/import/:entityType` (4-step wizard), `/portal/pages/settings/import/migration`, `/portal/pages/settings/import/sessions/:uuid`; Settings menu "Data Import"; Finance settings gets the opening balance date field.
