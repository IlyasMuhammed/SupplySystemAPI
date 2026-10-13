# Addendum 42 — API contract (Accounting module ⇄ Angular)

ApiResponse envelope, camelCase, UUIDs in routes, codes as strings, money as numbers (4 dp stored, 2 dp shown). Decisions: [ADDENDUM-42-ANALYSIS.md](ADDENDUM-42-ANALYSIS.md).
Every route `[RequiresFeature(FEATURE_GENERAL_LEDGER)]`. Permissions: read = `GL_VIEW`; CoA writes = `GL_ACCOUNT_MANAGE` (or GL_SETUP_MANAGE); setup writes = `GL_SETUP_MANAGE`.

## Codes
rootType `Asset Liability Equity Revenue Expense` · accountType (Appendix A) `Cash Bank Receivable OtherCurrentAsset FixedAsset AccumulatedDepreciation Payable OtherCurrentLiability LongTermLiability Equity Revenue OtherIncome CostOfGoodsSold Expense OtherExpense Tax Depreciation` · setup status `NotStarted AccountsCreated FiscalYearSet DefaultsSet BalancesEntered Complete` · period status `Open Locked Closed` · entry status `Draft Posted` · systemType `QUICKBOOKS_ONLINE QUICKBOOKS_DESKTOP XERO ERPNEXT TALLY SAGE SAP_B1 DYNAMICS_BC ZOHO_BOOKS WAVE ODOO CUSTOM` · syncDirection `None Inbound Outbound Bidirectional` · default keys per spec §8.3.
Gate error (403): `{ error: "FINANCE_SETUP_REQUIRED", message, currentStatus, redirectUrl: "/portal/pages/finance/gl/setup" }` — only on `[RequiresGlSetup]` routes (D-4).

## Templates — `api/finance/templates` (GL_VIEW)
| Route | Result |
|---|---|
| `GET ?countryCode&businessType&search` | `CoaTemplate[]` (system + own org) |
| `GET /filters` | `{ countries: { code, name }[], businessTypes: string[] }` from seeded templates |
| `GET /{uuid}` | `CoaTemplate & { accounts: AccountNode[] }` (tree) |
| `GET /{uuid}/preview` | `TemplateItem[]` flat, ordered by code path |
| `GET /{uuid}/download?format=csv|json` | file `{TEMPLATE_CODE}.csv|json` |
| `GET /blank?format=csv|json` | header-only CSV / skeleton JSON (`coa-template.csv|json`) |
```ts
CoaTemplate { uuid; templateCode; templateName; countryCode?; businessType?; currencyCode?; description?; accountCount; version; isSystem: boolean }
TemplateItem { accountCode; accountName; parentCode?; level; rootType; isGroup; accountType?; description?; isDefaultAccount; defaultAccountKey? }
AccountNode { code; name; rootType; level; isGroup; accountType?; description?; children: AccountNode[] }
```
PUT/POST/DELETE on a system template → 403 (BR-TPL-001).

## Chart of accounts — `api/finance/chart-of-accounts`
| Route | Perm | Body → Result |
|---|---|---|
| `GET` | GL_VIEW | `AccountTreeNode[]` (5 roots) + header `summary` → result `{ roots: AccountTreeNode[]; summary: { totalAccounts; postableAccounts; lastModifiedAt? } }` |
| `GET /flat?level&rootType&isGroup&isActive&search&accountType` | GL_VIEW | `Account[]` |
| `GET /{uuid}` | GL_VIEW | `AccountDetail` |
| `GET /suggest-code?parentUuid` | GL_VIEW | `{ code }` next free code under parent |
| `POST` | GL_ACCOUNT_MANAGE | `{ parentUuid; accountCode; accountName; accountType?; description?; externalCode?; sortOrder? }` → `AccountDetail` |
| `PUT /{uuid}` | GL_ACCOUNT_MANAGE | `{ accountName; accountType?; description?; externalCode?; sortOrder? }` (code/rootType/level immutable) |
| `DELETE /{uuid}` | GL_ACCOUNT_MANAGE | soft delete; refusals per BR-COA-005/006/007 |
| `POST /apply-template` | GL_SETUP_MANAGE | `{ templateUuid }` → `ApplyResult { accountsCreated; defaultsAssigned; status }` |
| `POST /upload` | GL_SETUP_MANAGE | multipart `file` (.csv/.json ≤ 5 MB) → `ApplyResult`, or 400 `{ message, errors: UploadError[] }` (all errors at once) |
| `GET /export?format=csv|json` | GL_VIEW | file `chart-of-accounts.csv|json` |
```ts
Account { uuid; accountCode; accountName; parentUuid?; parentCode?; materializedPath; level; rootType; isGroup; accountType?; description?; externalCode?;
  isSystemAccount; isActive; hasTransactions; openingBalanceDebit; openingBalanceCredit; currentBalance; sortOrder }
AccountTreeNode extends Account { children: AccountTreeNode[] }
AccountDetail extends Account { parent?: { uuid; accountCode; accountName }; childrenCount; canDelete: boolean; deleteBlockedReason?; createdAt; modifiedAt? }
UploadError { line?: number; code?: string; rule: 'R1'..'R13'; message }
```
Exact messages: spec §5.5 R1–R13 with `{placeholders}` filled; others: `"Cannot delete an account that has child accounts."`, `"Cannot delete an account with posted transactions."`, `"Cannot delete a system account."`, `"The parent must be one level above (level {n})."`, `"A tenant can have at most 2000 accounts."`, `"Account code {code} already exists."`, `"Account type {type} is not valid for {rootType} accounts."`, `"The chart can only be replaced before the fiscal year is set and before opening balances are posted."`.

## Fiscal years & periods (GL_VIEW / GL_SETUP_MANAGE)
| Route | Body → Result |
|---|---|
| `GET api/finance/fiscal-years` | `FiscalYear[]` with periods |
| `POST api/finance/fiscal-years` | `{ fiscalYearName; startDate; endDate; reportingCurrencyCode? }` → `FiscalYear` (12 monthly + period 13) |
| `PUT api/finance/fiscal-years/{uuid}` | `{ fiscalYearName; reportingCurrencyCode; startDate?; endDate? }` (dates only while no postings) |
| `POST api/finance/fiscal-years/{uuid}/close` | all periods Closed else 409 `"All periods must be closed first."` |
| `GET api/finance/fiscal-years/{uuid}/periods` | `FiscalPeriod[]` |
| `PUT api/finance/fiscal-periods/{uuid}` | `{ status?; periodName?; startDate?; endDate? }` transitions Open→Locked, Locked→Open, Open/Locked→Closed; Closed→* refused `"A closed period cannot be reopened."` |
```ts
FiscalYear { uuid; fiscalYearName; startDate; endDate; isClosed; reportingCurrencyCode; periods: FiscalPeriod[]; hasPostings: boolean }
FiscalPeriod { uuid; periodNumber; periodName; startDate; endDate; status }
```
Overlap → 409 `"Fiscal year dates overlap with an existing year."`. Dates `yyyy-MM-dd`.

## Accounting defaults
| Route | Perm | Body → Result |
|---|---|---|
| `GET api/finance/accounting-defaults` | GL_VIEW | `{ categories: { category; items: AccountingDefault[] }[]; requiredCount; assignedRequiredCount }` |
| `PUT api/finance/accounting-defaults/{defaultKey}` | GL_SETUP_MANAGE | `{ accountUuid: string|null }` → `AccountingDefault` (DEFAULT_OPENING_BALANCE refused: locked) |
| `PUT api/finance/accounting-defaults` | GL_SETUP_MANAGE | `{ items: { defaultKey; accountUuid|null }[] }` save all → same as GET |
| `POST api/finance/accounting-defaults/validate` | GL_VIEW | `{ isValid; missingRequired: string[]; problems: { defaultKey; message }[]; warnings: { defaultKey; message }[] }` |
```ts
AccountingDefault { defaultKey; defaultLabel; category; isRequired; expectedAccountType?; helpText?; isLocked; account?: { uuid; accountCode; accountName; accountType? } }
```

## Opening balances
| Route | Perm | Body → Result |
|---|---|---|
| `GET api/finance/opening-balances` | GL_VIEW | `{ conversionDate?; isPosted; groups: { rootType; rows: OpeningRow[] }[]; summary: OpeningSummary; subledger: SubledgerReference }` (all active leaf accounts, rows without entry show 0) |
| `PUT api/finance/opening-balances/batch` | GL_SETUP_MANAGE | `{ entries: { accountUuid; debitAmount; creditAmount; notes? }[] }` → same as GET (zero rows delete the draft) |
| `POST api/finance/opening-balances/post` | GL_SETUP_MANAGE | `{ acknowledgeDifference?: boolean; skip?: boolean }` → `{ journalEntryNumber?; summary }` |
| `GET api/finance/opening-balances/summary` | GL_VIEW | `OpeningSummary` |
```ts
OpeningRow { accountUuid; accountCode; accountName; accountType?; debitAmount; creditAmount; notes?; isPlug: boolean /*3900, read-only*/; warning?: string /*P&L*/ }
OpeningSummary { totalDebits; totalCredits; difference; isBalanced; conversionDate?; plugSide?: 'Debit'|'Credit' }
SubledgerReference { customerOpeningTotal; supplierOpeningTotal; openingStockValue; receivableAccountBalance?; payableAccountBalance?; inventoryAccountBalance?; currencyCode }
```
Refusals: `"Set the opening balance date in Finance settings first."`, `"The opening balance date must fall within the first fiscal year."`, `"Opening balances were already posted."`, `"Only Level 4 accounts accept opening balances."`, `"Cannot have both debit and credit amounts."`, `"Opening Balance Equity (3900) is calculated automatically."`, `"Debits and credits differ by {amount}. Confirm the difference to post it to Opening Balance Equity (3900)."`.
Conversion date = `GET/PUT api/finance/opening-balance-date` (A40, Finance) — the wizard uses that endpoint.

## Setup status — `api/finance/setup`
| Route | Perm | Result |
|---|---|---|
| `GET status` | GL_VIEW | `SetupStatus` |
| `POST complete` | GL_SETUP_MANAGE | `SetupStatus` or 409 listing unmet prerequisites |
| `POST reset` | GL_SETUP_MANAGE | `SetupStatus` or 409 `"Cannot reset after journal entries posted."` |
| `GET history` | GL_VIEW | `{ at; eventType; summary; userName? }[]` newest first |
```ts
SetupStatus { status; templateUuid?; templateName?; templateAppliedAt?; accountsCreatedCount; fiscalYearConfigured; defaultsConfigured; openingBalancesEntered;
  openingBalancesBalanced; setupCompletedAt?; setupCompletedByName?; conversionDate?; nextStep: 1..6; steps: { step; title; done: boolean }[] }
```

## Dashboard (gated, D-4) — `GET api/finance/gl/dashboard` (GL_VIEW)
`{ totalAssets; totalLiabilities; totalEquity; totalRevenue; totalExpenses; currencyCode; accountCount; fiscalYear?: { name; startDate; endDate } }` — sums of leaf `currentBalance` per root.

## External mappings — `api/finance/external-mappings` (GL_VIEW / GL_SETUP_MANAGE)
| Route | Body → Result |
|---|---|
| `GET ?systemType` | `ExternalMapping[]` |
| `POST` | `{ systemType; systemName; accountUuid; externalAccountCode; externalAccountName?; syncDirection?; connectionConfigJson? }` |
| `PUT /{uuid}` | `{ systemName?; externalAccountCode; externalAccountName?; syncDirection; isActive }` |
| `DELETE /{uuid}` | — |
| `POST /import` | multipart `file` CSV `sms_account_code,external_code,external_name,direction` + form `systemType`, `systemName` → `{ created; updated; errors: { line; message }[] }` |
| `GET /export?systemType` | CSV `external-mappings.csv` |
```ts
ExternalMapping { uuid; systemType; systemName; account: { uuid; accountCode; accountName }; externalAccountCode; externalAccountName?; syncDirection; isActive; lastSyncedAt?; syncStatus?; syncErrorMessage? }
```
Duplicate (systemType+account) → 409 `"This account is already mapped for {system}."`; group → `"Only Level 4 accounts can be mapped."`; inactive → `"Mapped account must be active."`; secrets → `"Connection settings must not contain credentials."`.

## Frontend routes (D-26)
`/portal/pages/finance/gl/setup` (wizard, 6 steps), `/gl` (dashboard, guard → setup), `/gl/accounts`, `/gl/fiscal-years`, `/gl/defaults`, `/gl/opening-balances`, `/gl/integrations`, `/gl/setup-status`. Menu: Finance › General Ledger.

## Details fixed by the backend (BE1)
Templates, chart of accounts, setup status and the gate (decisions D-28..D-39, D-45..D-49, D-60, D-61 in the analysis):
- **Permissions.** Reads accept `GL_VIEW`, `GL_ACCOUNT_MANAGE` or `GL_SETUP_MANAGE`. CoA POST/PUT/DELETE accept `GL_ACCOUNT_MANAGE` or `GL_SETUP_MANAGE`. Apply, upload, start-blank, template DELETE, setup complete and setup reset need `GL_SETUP_MANAGE`.
- **Start a blank chart (FSD §9.3 "Manual", D-60).** `POST api/finance/chart-of-accounts/start-blank` (no body, `GL_SETUP_MANAGE`).
  - Replaces any existing chart with only the system accounts: the 5 roots (1 Assets, 2 Liabilities, 3 Equity, 4 Revenue, 5 Expenses) plus 39 / 390 / 3900.
  - Returns `ApplyResult` `{ accountsCreated: 8, defaultsAssigned: 1 (DEFAULT_OPENING_BALANCE → 3900), status: "AccountsCreated" }`.
  - Same replace rules and 409s as apply/upload.
  - Users then add levels 2–4 with `POST chart-of-accounts`. The defaults step still needs real leaves.
  - History: "Blank chart of accounts started: 8 system accounts created." (a `CoaUploadedEvent` with format `BLANK`).
- **Delete refusal on every row (D-61).** Every `Account` — tree nodes, `/flat`, `/subtree`, `/ancestors` and detail — carries `canDelete` and `deleteBlockedReason`.
  - The rules and their order are the DELETE rules listed under "CoA writes" below.
  - They are computed in one batch (4 lookups for the whole list, no per-row queries). The tree, the flat list and detail always agree. All under `[RequiresFeature(FEATURE_GENERAL_LEDGER)]`.
- **Templates.**
  - `GET ?countryCode=GLOBAL` (or `none`) lists the country-agnostic templates. `search` matches name, code or description.
  - Order: system templates first, then file order.
  - `GET /filters` returns countries as `{ code, name }` (alpha-3 + English name) sorted by name. Business types: `General Services NonProfit Retail Manufacturing`.
  - `DELETE /{uuid}` deactivates a custom template. On a system template it returns 403 `"System templates are read-only."`.
  - A template with `accountCount = 0` cannot be applied: 409 `"Accounts for this template are coming. Choose another template or upload a file."`.
- **Files.**
  - CSV is UTF-8 with BOM and CRLF line ends, with RFC 4180 quoting.
  - JSON is the §5.4 tree. Leaves have no `children`.
  - `is_default_account` and `default_account_key` are written when set. This applies to template downloads and to the chart export, and an upload honours them.
  - Responses are file results with `Content-Disposition`: `{TEMPLATE_CODE}.csv|json`, `coa-template.csv|json`, `chart-of-accounts.csv|json`.
  - A bad `format` gives 400 `"Format must be csv or json."`.
- **Apply / upload result.** `{ accountsCreated, defaultsAssigned, status }`.
  - `accountsCreated` includes 39/390/3900 when they had to be added.
  - `status` is the recomputed setup status (normally `AccountsCreated`).
  - When replacing is not allowed, the response is 409 `"The chart can only be replaced before the fiscal year is set and before opening balances are posted."`.
  - Apply after non-opening journals gives 409 with the R13 sentence.
- **Upload 400.**
  - Body: `{ success: false, message, errors: UploadError[], result: null }`.
  - `UploadError { line?, code?, path?, rule, message }`. `line` is for CSV (the header is line 1). `path` is for JSON, e.g. `accounts[0].children[2]`.
  - Every problem is listed, sorted by line/path.
  - `message` is the single error's text, or `"The file has {n} problems. Nothing was imported."`.
  - A file over 5 MB, or no file, gives a 400 with an empty `errors` list and the message `"The file must be 5 MB or smaller."` or `"Choose a CSV or JSON file."`.
- **Extra rule sentences** (all use the R codes in D-45):
  - `"Account {code} must start with {digit}, the code of its root type ({rootType})."`
  - `"Account type {type} is not valid for {rootType} accounts."`
  - `"Account {code} is a group and cannot have an account type."`
  - `"Account 3900 is reserved for Opening Balance Equity and must be a Level 4 Equity account."`
  - `"Description exceeds 500 characters at code {code}."`
  - `"Account code '{code}' is longer than 20 characters."`
- **Chart reads.**
  - `GET chart-of-accounts` returns `result: { roots, summary }` with active accounts only. Add `?includeInactive=true` for all accounts.
  - Children are ordered by `sortOrder`, then code.
  - `GET /flat` with no `isActive` returns active and inactive accounts, ordered by path. `search` matches a code prefix or part of the name.
  - Extra routes: `GET /{uuid}/subtree` (active descendants) and `GET /{uuid}/ancestors` (root first).
  - `suggest-code` returns the parent code plus the first free digit 1–9, then 10–99.
- **CoA writes.**
  - POST errors:
    - 404 `"Parent account not found."`
    - 400 `"Accounts can only be added under a group account (levels 1–3)."`
    - 400 `"Account code must start with {digit}, the code of {rootType} accounts."`
    - 409 `"Account code {code} already exists."`
    - 409 `"A tenant can have at most 2000 accounts."`
  - PUT errors:
    - 409 `"Opening Balance Equity (3900) cannot be renamed."`
    - 409 `"The account type of Opening Balance Equity (3900) cannot be changed."`
    - 409 `"Root accounts are locked once the setup is complete."`
    - 409 `"This account was deleted and cannot be changed."`
  - DELETE refusals are 409. The same text is returned as `AccountDetail.deleteBlockedReason`, checked in this order:
    1. system account
    2. children
    3. transactions
    4. `"This account is assigned as {label}. Assign another account first."`
    5. `"This account has an opening balance. Remove it first."`
    6. `"This account is mapped to {systemName}. Remove the mapping first."`
- **Setup.**
  - `GET setup/status` initializes the organization and recomputes the status first.
  - `POST setup/complete` returns 409 `{ success: false, message, result: { unmet: string[] } }`. If setup is already Complete, it returns 200.
  - `GET setup/history?limit=` (1–500, default 100) returns `{ at, eventType, summary, userName? }`. `eventType` is the event class name, e.g. `CoaTemplateAppliedEvent`.
- **Gate 403.** Body: `{ success: false, error: "FINANCE_SETUP_REQUIRED", message, currentStatus, redirectUrl: "/portal/pages/finance/gl/setup", result: null }`.

## Details fixed by the backend (BE2)
Fiscal years, defaults, opening balances, mappings and the dashboard (decisions D-50..D-59 in the analysis):
- **Permissions.** Reads accept `GL_VIEW`, `GL_ACCOUNT_MANAGE` or `GL_SETUP_MANAGE`. Every write here needs `GL_SETUP_MANAGE`. `POST accounting-defaults/validate` is a read.
- **Fiscal years.**
  - Extra route: `GET api/finance/fiscal-years/{uuid}` returns one `FiscalYear`.
  - `FiscalYear` adds `closedAt?`. `FiscalPeriod` adds `isAdjustment` (true for period 13). Periods are ordered by number.
  - On POST, `fiscalYearName` may be blank: it becomes `FY 2026` or `FY 2026-27`.
  - `reportingCurrencyCode` omitted = the org's sale base currency. If that cannot be resolved: 400 `"Enter the reporting currency: the organization's base currency could not be found."`. Not 3 letters → 400.
  - A year is at most 12 months: 400 `"A fiscal year can be at most 12 months long."`. `endDate` must be after `startDate`.
  - A duplicate name gives 409 `"A fiscal year named \"{name}\" already exists."`.
  - PUT: blank name or currency = keep.
  - PUT with new dates:
    - With postings: 409 `"The dates cannot be changed: journal entries are posted in this fiscal year."`.
    - With a non-Open period: 409 `"The dates can only be changed while every period of the year is open."`.
    - Otherwise the periods are regenerated, with **new uuids**.
  - A closed year gives 409 `"A closed fiscal year cannot be changed."` on PUT and on period PUT.
  - `POST /close` on a closed year returns 200 with no change.
- **Periods.**
  - `status` is case-insensitive. Anything other than Open, Locked or Closed → 400.
  - Closing is refused while a Draft journal entry is dated in the period: 409.
  - Date edits:
    - Changing a period's `startDate` moves the previous period's end. Changing its `endDate` moves the next period's start.
    - The first period's start and the last monthly period's end are fixed to the year (400).
    - Period 13 can move anywhere inside the year.
    - Refusals (400): `"Period {name} would end before it starts."`, `"Period dates must fall within the fiscal year."`, `"Periods must cover the fiscal year without gaps or overlaps."`.
    - Dates cannot be edited when the year has postings or when any of its periods is Closed (409).
  - A period PUT returns the updated `FiscalPeriod`. The neighbour may have moved too, so reload the year's periods.
- **Accounting defaults.**
  - `AccountingDefault` adds two fields:
    - `warning?`: the account type is not the expected one. Information only.
    - `problem?`: the assigned account is now inactive, a group, or missing. The row then counts as unassigned.
  - Categories are in the order `Receivables Payables Banking Revenue CostOfSales Inventory Tax Equity`. Items within a category are in catalog order.
  - `DEFAULT_OPENING_BALANCE` is set to 3900 automatically, on GET and on any save, whenever the chart has an active leaf 3900.
    - `PUT /{DEFAULT_OPENING_BALANCE}` → 400 `"The Opening Balance Equity default is assigned automatically to account 3900 and cannot be changed."`.
    - Save-all ignores it when it is sent unchanged, null, or as 3900.
  - Default keys are case-insensitive. An unknown key on the single PUT → 404.
  - Single PUT refusals are 400:
    - `"Account not found."`
    - `"Only Level 4 accounts can be assigned as defaults."`
    - `"The default account must be active."`
    - `"A required default cannot be cleared after the setup is complete."`
  - Save-all is all-or-nothing. On failure it returns 400 `{ success: false, message, result: { problems: { defaultKey, message }[] } }`, and `message` is the single problem or `"{n} accounting defaults are invalid."`.
  - `validate`: `missingRequired` lists required keys that are unassigned or have a `problem`. `isValid` ignores warnings.
- **Opening balances.**
  - The GET result adds `postedAt?`, `isSkipped` and `summary.entryCount`.
  - Before posting:
    - Rows show the draft amounts; 3900 shows 0.
    - `summary.difference` is debits − credits.
    - `plugSide` is `Credit` when debits are greater, otherwise `Debit`, or null when balanced.
  - After posting, rows show each account's posted opening balance, so 3900 carries the plug. `conversionDate` is then the copy taken at posting.
  - Rows cover active leaf accounts, plus inactive ones that have an entry or a posted balance.
  - `subledger` is `null` when Finance's totals are unavailable. Otherwise it also has `receivableAccountCode?`, `payableAccountCode?` and `inventoryAccountCode?`. Each `*AccountBalance` is the draft (or posted) entry on that default account in its normal direction, or null when the default is unassigned.
  - Batch PUT:
    - It is all-or-nothing. On failure it returns 400 `{ success: false, message, result: { errors: { accountUuid, accountCode?, message }[] } }`.
    - Extra sentences: `"Account not found."`, `"Account {code} is inactive."`, `"Opening balances cannot be negative."`, `"Account {code} is listed more than once."`.
    - Amounts are stored at 4 dp.
    - After posting: 409 `"Opening balances were already posted."`.
  - Post:
    - Refusals: 400 `"Set up a fiscal year first."`, 400 `"Enter at least one opening balance, or skip this step."`, 409 `"Opening Balance Equity (3900) is missing from the chart of accounts."` (only when a plug is needed). A second post gives 409.
    - Draft entries that became invalid give 400 with `result.errors`.
    - The response `summary` is the pre-plug summary. The journal number is `JE-{year of posting}-NNNNN`.
  - Skip (`{ skip: true }`):
    - Needs no date and no fiscal year.
    - It is refused with 409 `"Delete the draft opening balances before skipping, or post them instead."` while drafts exist.
    - The response is `{ journalEntryNumber: null, summary }` (zeros).
  - Once posted, Finance's `PUT opening-balance-date` is locked to the posted date (409 from Finance, `"…locked by General ledger opening balances posted as at {date}."`).
- **External mappings.**
  - `ExternalMapping` adds `connectionConfigJson?`. The list is ordered by system type, then account code. `systemType` in the query is case-insensitive.
  - POST:
    - `systemName` defaults to the display name (e.g. `QuickBooks Online`). `syncDirection` defaults to `None`; both are case-insensitive.
    - Unknown type or direction → 400.
    - An account of another org gives 404 `"Account not found."`.
    - Invalid JSON gives 400 `"Connection settings must be valid JSON."`.
    - Credential keys (any depth, substring, case-insensitive) give 400 `"Connection settings must not contain credentials."`.
  - PUT:
    - `externalAccountCode` is required.
    - `syncDirection` empty = `None`.
    - `systemName` blank = keep.
    - Optional `connectionConfigJson` (null = keep, `""` = clear).
    - Re-activating an inactive mapping re-checks the account.
  - DELETE removes the row.
  - Import:
    - The header is case-insensitive. Only `sms_account_code` and `external_code` are required; extra columns are ignored, so an export can be re-imported.
    - The response is always 200 `{ created, updated, errors: { line, message }[] }`. `line` is the file line, and the header is line 1.
    - Good lines are saved even when other lines fail.
    - Missing file or file > 5 MB → 400. Unknown `systemType` or missing columns → 400.
  - Export:
    - Columns: `sms_account_code,external_code,external_name,direction,system_type,system_name,is_active`.
    - UTF-8 BOM, CRLF.
    - Values starting with `= + - @` are prefixed with `'`.
- **Dashboard.**
  - Totals are the sum of every leaf account's `currentBalance` per root type, each in its normal direction.
  - `accountCount` is the number of active accounts at all levels.
  - `fiscalYear` is the year containing today, else the latest one that has started, else the earliest. `currencyCode` is that year's reporting currency, or null.
- **Shared seams.**
  - `IFiscalPeriodGuard.IsPostingAllowedAsync(org, date)` is true when an Open period of a non-closed year contains the date.
  - `IOpeningDateLockSource` (Accounting) returns the conversion date once the opening balances are posted. If they were skipped without a date, it returns null.

## Details fixed by the frontend (FE)
Where the contract left room, the Angular side (`services/accounting.service.ts`, `pages/finance/gl/**`) does this — the backend may rely on it:
- **CoA tree shape.** `GET chart-of-accounts` is read as `result: { roots, summary }`; a bare `AccountTreeNode[]` is also accepted (summary then counted client-side, `lastModifiedAt` null).
- **Start blank.** Step 1 offers "Start with a blank chart (add accounts yourself)" → confirm → `POST chart-of-accounts/start-blank` → step 2. Apply / start-blank refusals (e.g. the 409 once a fiscal year exists or opening balances are posted) are shown inline in the server's words; upload refusals in the upload box.
- **Upload 400.** The error list is read from `errors` at the top level or from `result.errors`; each line shows `Line {line}` (CSV) or `{path}` (JSON) + `code` + message + `(rule)`, sorted by line. Client pre-checks: extension `.csv|.json`, size ≤ 5 MB (server words still shown when it refuses).
- **Complete 409.** Unmet prerequisites are read from `unmet`, `errors` or `result.unmet` (strings or `{ message }`); the `message` is shown above them.
- **Gate 403.** Recognised by `error` **or** `errorCode` = `FINANCE_SETUP_REQUIRED`; the interceptor navigates to `redirectUrl` (only if it starts with `/`, else the wizard) with `?gate=1`. The dashboard route guard does the same from `GET setup/status` (`?gate=1&next={nextStep}`); a failed status read lets the page open.
- **Wizard gating.** Step N's Continue is enabled by `steps[N].done` (fallback: 1–2 `accountsCreatedCount > 0`, 3 `fiscalYearConfigured`, 4 `defaultsConfigured`, 5 `openingBalancesEntered`). A step can be opened only up to the first not-done one; `?step=` is clamped to it; Complete opens step 6. Apply/upload are disabled (with the server's sentence) unless status is `NotStarted|AccountsCreated` and opening balances are not entered.
- **Delete reason in the tree.** The ⋯ menu uses the node's `canDelete` / `deleteBlockedReason` when the tree sends them; otherwise it computes the reason (system → children → `hasTransactions`, the contract's exact sentences). The server's refusal is shown verbatim if it still differs.
- **Account dialog.** New level = parent level + 1; Level 4 = postable (type list = Appendix A for the root: Asset `Cash Bank Receivable OtherCurrentAsset FixedAsset AccumulatedDepreciation Tax`, Liability `Payable OtherCurrentLiability LongTermLiability Tax`, Equity `Equity`, Revenue `Revenue OtherIncome`, Expense `CostOfGoodsSold Expense OtherExpense Depreciation`); Levels 2–3 are groups and send `accountType: null`. Client limits: code `[A-Za-z0-9]{1,20}` starting with the root digit and unique (case-insensitive), name ≤ 200, description ≤ 500, external code ≤ 50, sortOrder integer ≥ 0. 3900's name is read-only. PUT never sends code/parent.
- **Level filter** on the tree means "show levels 1..N" (not "only level N").
- **Fiscal years.** New year defaults: start = day after the latest year's end, end = start + 12 months − 1 day, currency = org sale base currency (`api/organization/currency-settings`). Client checks: name ≤ 100 (FSD §7.2; may be blank on create — the server names it), end > start, at most 12 months (end ≤ start + 12 months − 1 day). PUT omits `startDate/endDate` when `hasPostings`. Period actions: Open → Lock/Close, Locked → Reopen/Close, Closed → none; each PUT sends `{ status }` only, and the years are reloaded after every period PUT (D-51: a neighbour may move). `closedAt` is shown on the card.
- **Defaults.** "X of Y required defaults assigned" is computed from the current (unsaved) selections, the locked row included. Save sends every non-locked key (`DEFAULT_OPENING_BALANCE` never sent) via the bulk PUT, then calls validate. A per-row `problem` from GET is shown until that row is changed, and such a row counts as unassigned in "X of Y"; the row `warning` is shown (or the client-computed type mismatch). A save-all 400 `result.problems` is shown on each row by `defaultKey`.
- **Opening balances.** Inputs are 2 dp, ≥ 0; typing one side zeroes the other. Totals/plug are recomputed client-side from the typed values (plug = |Dr − Cr|, credit when Dr > Cr). Batch PUT sends non-plug rows that have an amount **or had one when loaded** (zeros delete the draft). Post: if dirty, the draft is saved first; body `{ acknowledgeDifference: true }` only when there is a difference and the box is ticked; Skip with no saved drafts sends `{ skip: true }` only; with N saved drafts (D-56) it confirms "This will clear N saved draft balances and post zero opening balances.", PUTs the batch with every draft row zeroed (deleting them), then posts `{ skip: true }`. A batch/post 400 `result.errors` marks each row by `accountUuid` (cleared when the row is edited). `summary.entryCount` is shown as "N draft entries saved"; `postedAt` in the posted / skipped note. Read-only when `isPosted`; `isSkipped` shows its own note. The conversion date is the A40 component (`GET/PUT api/finance/opening-balance-date`, edit needs FINANCE_SETUP_MANAGE or GL_SETUP_MANAGE), and its 409 sentence is shown verbatim.
- **Mappings.** Default direction for a new mapping `Outbound`. Duplicate (systemType + account) is checked client-side with the server's sentence. The connection name field is reused for new mappings and imports; it is pre-filled from the first mapping of the system. Sync status label: empty/unknown → Never; `success|synced` → Success; `error|failed` → Error; `pending` → Pending. A 400 import with `errors` is shown as a result (0 created / 0 updated).
- **Reset** needs the word `RESET` typed. **Menu** first entry: "Set up general ledger" (→ wizard) while status ≠ Complete, else "Dashboard"; the status is fetched once when the group is visible (unknown → "Dashboard").
- **Routes/menu gates.** Every GL route and the menu group: any of `GL_VIEW` / `GL_ACCOUNT_MANAGE` / `GL_SETUP_MANAGE` + `FEATURE_GENERAL_LEDGER` (the guard loads the tenant when needed; Super Admin passes). Write buttons: CoA = `GL_ACCOUNT_MANAGE` or `GL_SETUP_MANAGE`; everything else = `GL_SETUP_MANAGE`.
- **Downloads** use the `Content-Disposition` file name (fallbacks: `{TEMPLATE_CODE}.{fmt}`, `coa-template.{fmt}`, `chart-of-accounts.{fmt}`, `external-mappings.csv`); a JSON error inside a blob is read and shown.
