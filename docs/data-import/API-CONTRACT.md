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
  allowUpdate; requiresOpeningBalancePermission; completedSessions: number; lastCompletedAt?; lastCompletedSessionUuid?; lastImportedRows? }
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
| `PUT api/import/sessions/{uuid}/mappings` | IMPORT_UPLOAD | `{ mappings: { sourceColumnIndex; targetFieldName: string|null }[] }` → `SessionDetail` (saves incomplete mappings; refuses only IMP-MAP-03 double mapping — IMP-MAP-01/02 are enforced by `validate`; status MAPPING; resets validation) |
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
`GET api/finance/opening-balance-date` (any of FINANCE_SETUP_MANAGE, INVOICE_VIEW, PAYMENT_VIEW, SALES_INVOICE_VIEW, CUSTOMER_LEDGER_VIEW, IMPORT_OPENING_BALANCE, CURRENCY_VIEW, CURRENCY_MANAGE, CURRENCY_RATE_VIEW, CURRENCY_RATE_MANAGE, ORG_CURRENCY_SETTINGS_MANAGE) → `{ date: 'yyyy-MM-dd'|null }`; `PUT` (FINANCE_SETUP_MANAGE) `{ date }`.
Supplier invoices (`api/finance/invoices` list + detail) carry `sourceType: 'OPENING' | 'CARRIER_INVOICE' | null`; sales invoices already carry `sourceType` (`OPENING` for imported opening balances).

## Details fixed by the backend (IMP)
- Errors: 400 BadRequest / 404 NotFound / 403 Forbidden through the usual `ApiResponse` failure body. Extra refusal texts: `"Sheet '{name}' was not found in the workbook."`, `"The file could not be read. Please check that it is a valid .xlsx or .csv file."`, `"You do not have permission to import opening balances."` (403), `"Opening balance import is not enabled for your organization."` (403), `"Validate the file before importing."`, `"There are no valid rows to import."`, `"This import is already running or finished."`, `"This import has already finished."` (cancel), `"This import does not allow updating existing records."`, `"Opening balance imports always reject duplicates (strategy ERROR)."`, `"Mapping preset '{name}' was not found."`, `"Unknown import type '{t}'."` (404), `"Unknown field '{f}'."`, `"The file has no column {i}."`.
- `POST sessions` multipart fields: `file`, `entityType`, `duplicateStrategy?`, `sheetName?`, `mappingPreset?`, `notes?`; 12 MB request cap. Row numbers are spreadsheet row numbers (header = 1; template data starts at 4).
- Every profile's `fields` start with the importer's `external_id` ("External ID", TEXT 100). `unmappedRequiredFields` holds field NAMES. A required field with a default value need not be mapped.
- `PUT mappings` is a patch: listed columns change, others keep their mapping (send all to replace). A manual change sets `isAutoMapped=false`, `confidenceScore=null`; preset-applied mappings are also manual. Any successful PUT resets validation (rows PENDING, status MAPPING). The PUT saves an incomplete mapping (required / key fields unmapped → listed in `unmappedRequiredFields`); it refuses only `"Field '{display}' is mapped twice."`, `"Column '{source}' is mapped twice."`, an unknown field/column, or a started import. `"Map required field: …"` / `"Map at least one key field: …"` come from `validate` (D-47).
- IMP-FILE-08 counts only IMPORTING sessions: UPLOADED / MAPPING / VALIDATED sessions (e.g. left by picking another sheet) never block an upload or an execute. An UPLOADED session untouched for 7 days is set CANCELLED by the recovery job with `errorMessage` `"Cancelled automatically: not mapped or validated within 7 days."` (D-47).
- `lastCompletedSessionUuid` = the newest COMPLETED / COMPLETED_WITH_ERRORS session of the entity type (same one as `lastCompletedAt` / `lastImportedRows`).
- `validate` is allowed in UPLOADED / MAPPING / VALIDATED and also FAILED / CANCELLED (re-run of an interrupted import: only rows not yet applied are re-checked; counters of applied rows are kept); `execute` needs VALIDATED and ≥ 1 valid row (FAILED / CANCELLED → `"Validate the file before importing."`). An IMPORTING session idle for 30 min without a live job, or cancelled by an IMPORT_MANAGE holder, becomes FAILED with `errorMessage` `"Import interrupted — re-validate and re-run the remaining rows."` (D-38/D-39). `errors` lists INVALID and ERROR rows; `values` are the raw mapped values by field name; `errors[].field` is empty for IMPORT_ERROR.
- `preview` values are parsed values after validation (raw before); `rows` 1–200, default 20.
- `progress.total` = rows the running import processes (valid rows). Hub event name `ImportProgress`; group per user (`org_{org}_import_user_{userId}`); the creator and the executor both receive it.
- `mapping-presets` → `{ name, lastUsedAt }` newest first; preset names are case-insensitive, ≤ 100 chars.
- Field type `CURRENCY` = 3-letter currency code (D-36). Decisions D-30..D-39 and D-47 (IMP) in the analysis doc.
- Templates: `{entity}-import-template.xlsx` (sheets Data / Instructions / Lookups hidden, defined names `List_{key}`); `data-migration-workbook.xlsx` sheets `"{n} - {profile name}"` (≤ 31 chars); `import-errors-{uuid}.xlsx` (Errors + Summary), `import-results-{uuid}.xlsx` (Results).

## Frontend routes
`/portal/pages/settings/import` (dashboard), `/portal/pages/settings/import/:entityType` (4-step wizard), `/portal/pages/settings/import/migration`, `/portal/pages/settings/import/sessions/:uuid`; Settings menu "Data Import"; Finance settings gets the opening balance date field.

## Details fixed by the frontend (FE)
- **Guards / menu**: dashboard + migration `IMPORT_PROFILE_VIEW`; wizard `IMPORT_UPLOAD`; session detail `IMPORT_UPLOAD` or `IMPORT_MANAGE`. Menu: Settings › System › "Data Import" (`IMPORT_PROFILE_VIEW` + `MODULE_DATA_IMPORT`). Opening-balance cards/wizard need `FEATURE_OPENING_BALANCE_IMPORT` **and** `IMPORT_OPENING_BALANCE` (else hidden / refused before upload).
- **Opening balance date** lives on Settings › Finance Setup › **Currency configuration** (`/portal/pages/finance-setup/currency-configuration`, card `#opening-balance-date`); opening cards link there while the GET returns `date: null` (wizard Upload disabled meanwhile). Anyone who opens that page sees it; Save / Clear only with `FINANCE_SETUP_MANAGE`. PUT `{ date: 'yyyy-MM-dd' }` (local day, never via UTC) or `{ date: null }` to clear; any refusal (409: cleared / moved before existing opening documents) is shown verbatim and the field returns to the saved date. Holders of only `CURRENCY_*` / `ORG_CURRENCY_SETTINGS_MANAGE` get 403 on the GET → the card says "could not be loaded".
- **Upload**: multipart fields as above (notes trimmed, omitted when blank). Client pre-checks IMP-FILE-01/02 with the same sentences (`profile.maxFileSizeMb`); everything else is the server's `message` verbatim. Strategies offered: SKIP / UPDATE / ERROR (CREATE_NEW never offered); UPDATE hidden when `allowUpdate=false`; opening balances offer ERROR only. Default = `profile.duplicateStrategy` when offered, else SKIP.
- **Sheet picker**: shown when `sheetNames.length > 1`; keeping the sheet the server read (`sheetName`, else `sheetNames[0]`) goes on; another sheet is a **new POST /sessions with `sheetName`** — the first session stays UPLOADED (it must not count as "already running" for IMP-FILE-08).
- **Mapping**: PUT after every change with the **whole** list (FE waits while a field is chosen twice). The FE expects the PUT to accept incomplete mappings and answer `unmappedRequiredFields`; a refusal is shown and the choice kept (Next retries). Required fields unmapped in the server's answer but absent from `unmappedRequiredFields` (defaults) do not block Next. Fields render generically from the profile (`displayName`, `*` required, `(key)`), so new columns such as PRODUCT `product_type` / `supply_method` need no FE change. Marks from `isAutoMapped` + `confidenceScore` (≥0.995 ✓, other score ~ n %, manual —, no target ✗).
- **Validate**: `errorCountByCode` sorted by count + the first 50 of `errors`; preview `rows=20` re-read after validation for `issues`. Import = `IMPORT_EXECUTE` && VALIDATED && validRows > 0 ("Import N valid rows"). Error report offered when `invalidRows > 0`.
- **Results**: `isBackground` and not terminal → hub `ImportProgress` filtered by `sessionUuid` (PascalCase tolerated); `GET /progress` once at start, every 3 s while the hub is not connected (e.g. connection aborted) and every 15 s even when it is; on a terminal status the session is re-read. Cards: Imported `importedRows`, Updated `updatedRows`, Skipped `skippedRows`, Errors `errorRows + invalidRows`; duration `importCompletedAt − importStartedAt`.
- **Resume**: `/import/:entityType?session=<uuid>` reopens a session on its step (UPLOADED/MAPPING → Map, VALIDATING/VALIDATED → Validate, else Results); `&revalidate=1` reopens a FAILED / CANCELLED (interrupted, D-38/D-39) session on Validate. The session page offers "Continue import" (`IMPORT_UPLOAD`, not-yet-imported states), "Re-validate remaining rows" (`IMPORT_EXECUTE`, FAILED / CANCELLED) and "Cancel import" while not terminal (`IMPORT_EXECUTE` or `IMPORT_MANAGE`).
- **Downloads**: file name from `Content-Disposition` — **the API's CORS policy must `WithExposedHeaders("Content-Disposition")`** (nothing is exposed today); otherwise the FE falls back to the server's own names (`{entity}-import-template.xlsx` with the lower-cased entity type, `data-migration-workbook.xlsx`, `import-errors-{uuid}.xlsx`, `import-results-{uuid}.xlsx`). A failed download's JSON message is read out of the blob.
- **Migration "View"**: `GET sessions?entityType=X&page=1&pageSize=20` → newest COMPLETED / COMPLETED_WITH_ERRORS (a `lastCompletedSessionUuid` on `ImportProfile` would save the call). **IMP: `lastCompletedSessionUuid` is now on `ImportProfile` (list and detail).**
- **Labels**: shared `formatCode` maps `OPENING_BAL` / `OPENING` → "Opening balance" and `OPENING_STOCK` → "Opening stock" (customer ledger, report ledgers); the master product ledger and stock ledger filters list "Opening stock". Sales invoices with `sourceType: 'OPENING'` get an "Opening balance" chip on list and detail. Supplier invoices get the same chip **when `sourceType` is sent — Finance's `InvoiceListItemModel` / `InvoiceDetailModel` do not expose `SourceType` yet (TFIN to add).**
