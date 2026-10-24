# Addendum 47 — API contract (journal engine ⇄ Angular)

Decisions: [ADDENDUM-47-ANALYSIS.md](ADDENDUM-47-ANALYSIS.md) (Part A overrides the FSD; D-1..D-21, D-CORE-n). Owner of this file: **CORE**.
Section owners: **CORE** = journal types + journal entries (create/edit/delete/post/list/detail); **RR** = reverse + reconciliation;
**RPT** = trial balance / account balance / account ledger + period close / year-end close. Implement exactly what is written here;
ask for changes in `SCHEMA-REQUESTS.md` (schema) or a "Changes" line at the bottom of this file (agreed with CORE).

## 0. Conventions (all sections)

- **Envelope**: `ApiResponse<T>` `{ success: boolean, message: string, result: T }`, camelCase JSON. Paged results are
  `ApiResponse<PaginatedResponse<T>>` with `result = { data: T[], totalRecords, page, pageSize, totalPages, hasNext, hasPrevious }`.
- **Ids**: uuids only, in routes and payloads (A-3). Journal **lines** are identified by `lineUuid` (column `JournalEntryLines.Uuid`).
- **Dates** `yyyy-MM-dd` (DateOnly); timestamps ISO UTC. **Money** numbers, stored 4 dp, base (reporting) currency unless the
  field name says otherwise (`amountCurrency`). Codes/statuses are the exact strings below (ordinal, case-sensitive in responses;
  requests accept any case for query filters).
- **Gates on every A47 route**: `[RequiresFeature(FEATURE_GENERAL_LEDGER)]` and `[RequiresGlSetup]` → 403
  `{ success:false, error:"FINANCE_SETUP_REQUIRED", message, currentStatus, redirectUrl:"/portal/pages/finance/gl/setup" }` until the
  A42 setup is Complete; then `[RequirePermission(...)]` (any of the listed codes) → 403 otherwise. Services re-check (`AccountingCaller`).
- **Errors**: 400 = a rule was broken, 409 = the state refuses the action, 404 = not found in the caller's organization (another org's
  uuid is 404, never 403). A47 controllers carry `[JournalErrorFilter]`; services throw `JournalValidationException` (400) /
  `JournalConflictException` (409) with an error code, and the body is
  `{ success:false, message: "<first error>", result: { errorCode: "JE-001", errors: [{ errorCode, message, lineNumber? }] } }`.
  Line errors are prefixed in `message` only: `"Line 2: Account is inactive"`; in `errors[]` the `message` is the bare text and
  `lineNumber` is set. Plain `BadRequestException`/`ConflictException` (no code) are allowed for simple input checks (body without `result.errorCode`).
- **Statuses**: journal entry `Draft | Posted | Reversed` (Reversed = posted, then cancelled by a reversal; **its lines still count**:
  every balance, trial balance, ledger and year-end computation reads `Status IN ('Posted','Reversed')`, never `= 'Posted'` alone —
  D-CORE-4). Period `Open | AdjustmentOnly | Locked | Closed`. Journal type kind `Sale | Purchase | Cash | Bank | Miscellaneous | Opening | Closing`.
  Reconcile type `Full | Partial`. Partner type `Customer | Vendor`.
- **Permissions** (D-15, all under FEATURE_GENERAL_LEDGER): `GL_VIEW`, `GL_JOURNAL_CREATE`, `GL_JOURNAL_EDIT`, `GL_JOURNAL_DELETE`,
  `GL_JOURNAL_POST`, `GL_JOURNAL_REVERSE`, `GL_RECONCILE_VIEW`, `GL_RECONCILE_EXECUTE`, `GL_REPORTS_VIEW`, `GL_PERIOD_CLOSE_VIEW`,
  `GL_PERIOD_CLOSE_EXECUTE`, `GL_PERIOD_CLOSE_ADMIN`; journal-type writes `GL_SETUP_MANAGE`. "JE-READ" below =
  `GL_VIEW | GL_ACCOUNT_MANAGE | GL_SETUP_MANAGE | GL_JOURNAL_CREATE | GL_JOURNAL_EDIT | GL_JOURNAL_DELETE | GL_JOURNAL_POST | GL_JOURNAL_REVERSE`.
  Grants: Org Admin + System Admin all; every role that holds GL_VIEW (by the A42 rules or explicitly) also gets
  `GL_RECONCILE_VIEW`, `GL_REPORTS_VIEW`, `GL_PERIOD_CLOSE_VIEW`. Write codes are granted by hand.

### Error code catalog (task 462)
| Code | Meaning | Texts (exact) |
|---|---|---|
| JE-001 | not balanced | `Entry is not balanced: debits {d}, credits {c}` (+ ` ({CUR})` when checked in a named currency; amounts `#,##0.00`) |
| JE-002 | lines count | `Minimum 2 lines required` · `An entry can have at most 500 lines` |
| JE-003 | both sides | `Line cannot have both debit and credit` |
| JE-004 | no / negative amount | `Line must have a debit or a credit amount` · `Amounts cannot be negative` |
| JE-005 | no period | `No fiscal period covers this date` · `The entry date must stay in the fiscal year of the entry number` |
| JE-006 | closed | `Period is closed` · `Fiscal year is closed` |
| JE-007 | locked | `Period is locked` |
| JE-008 | adjustment only | `Period accepts only adjustment entries` |
| JE-009 | account | `Account not found` · `Account is not postable` |
| JE-010 | account inactive | `Account is inactive` |
| JE-011 | partner | `Sales entries require a partner` · `Purchase entries require a partner` · `{Kind} entries require a partner` · `Partner type must be Customer or Vendor` · `Partner not found` |
| JE-012 | currency | `Exchange rate required for foreign currency` · `Exchange rate must be greater than zero` · `Currency must be a 3-letter currency code` |
| JE-013 | journal type | `Journal type not found` · `Journal type is inactive` · `An entry cannot be both an opening and a closing entry` |
| JE-014 | state (409) | `Entry is already posted` · `Entry has been reversed` · `Posted entries cannot be modified` · `Posted entries cannot be deleted` · `This draft was created from a source document: post it, or change the source document instead` |
| JE-015 | reversal (RR) | see §3 |
| RC-001..RC-008 | reconciliation (RR) | see §4 |
| PC-001..PC-010 | period / year-end close (RPT) | see §6 |

Period rule (D-11, used by create, post, reverse, year-end close): the date must be in a period of a fiscal year that is not closed;
Open accepts all entries, AdjustmentOnly only `isOpeningEntry`/`isClosingEntry` entries, Locked/Closed none. On the year's last day
(period 12 and 13 overlap) a regular entry prefers 1–12, a closing entry period 13.

---

## 1. Journal types — `api/finance/journal-types` (CORE)

| Method & route | Permission | Body → result |
|---|---|---|
| `GET ?activeOnly=false` | JE-READ | `JournalType[]` ordered by sortOrder, code |
| `GET /{uuid}` | JE-READ | `JournalType` |
| `POST` | GL_SETUP_MANAGE | `CreateJournalType` → `JournalType` (custom, `isSystem:false`) |
| `PUT /{uuid}` | GL_SETUP_MANAGE | `UpdateJournalType` (full replacement of editable fields) → `JournalType` |
| `PATCH /{uuid}/deactivate` | GL_SETUP_MANAGE | — → `JournalType` (`isActive:false`) |
| `PATCH /{uuid}/activate` | GL_SETUP_MANAGE | — → `JournalType` (`isActive:true`) |
| `DELETE /{uuid}` | GL_SETUP_MANAGE | — → `{ deleted: true }` (custom and unused only) |

```ts
JournalType { uuid; code; name; type: 'Sale'|'Purchase'|'Cash'|'Bank'|'Miscellaneous'|'Opening'|'Closing'; sequencePrefix;
  defaultDebitAccount?: AccountRef; defaultCreditAccount?: AccountRef; requiresPartner; autoPost; isSystem; isActive; sortOrder;
  isUsed: boolean /* entries exist */; canDelete: boolean; createdAt; modifiedAt }
AccountRef { uuid; accountCode; accountName; rootType? }
CreateJournalType { code; name; type; sequencePrefix?; defaultDebitAccountUuid?; defaultCreditAccountUuid?; requiresPartner?; autoPost?; sortOrder? }
UpdateJournalType { code; name; type; sequencePrefix; defaultDebitAccountUuid?; defaultCreditAccountUuid?; requiresPartner; autoPost; sortOrder? }
```
Seed (§4.3): SALE/Sales Journal/Sale/SALE/partner, PURCH/Purchase Journal/Purchase/PURCH/partner, CASH/Cash Journal/Cash/CASH,
BANK/Bank Journal/Bank/BANK, MISC/Miscellaneous Journal/Miscellaneous/JV, OPEN/Opening Journal/Opening/OPEN/auto-post,
CLOSE/Closing Journal/Closing/CLOSE/auto-post — all `isSystem`. Seeded on setup completion, by the migration, and on demand.

Rules / texts: code and prefix are upper-cased, `^[A-Z0-9]{1,10}$` → 400 `"The code must be 1–10 letters or digits."` /
`"The sequence prefix must be 1–10 letters or digits."`; prefix defaults to the code. Name required, ≤ 100 → 400 `"The name is required."` /
`"The name can be at most 100 characters."`. Type not in the list → 400 `"Type must be Sale, Purchase, Cash, Bank, Miscellaneous, Opening or Closing."`.
409 `"Journal type code {code} already exists."` · 409 `"Sequence prefix {prefix} is already used by journal type {code}."` ·
409 `"An organization can have only one Opening journal type."` (and `Closing`, task 250) · 400 `"The default debit account must be an active Level 4 account."`
(and `credit`). System types: code and type fixed → 400 `"The code and type of a system journal type cannot be changed."`; the
Opening/Closing system types keep `autoPost:true` → 400 `"The Opening and Closing journal types always post automatically."`.
Prefix fixed once the type has numbered entries → 409 `"The sequence prefix cannot be changed once entries are numbered with it."`;
custom type's kind fixed once used → 409 `"The type cannot be changed once the journal type has entries."`.
DELETE: system → 409 `"System journal types cannot be deleted, only deactivated."` (task 249); used → 409
`"A journal type with entries cannot be deleted; deactivate it instead."`. An inactive type cannot get new entries (JE-013); system
writers fall back to MISC (D-12).

## 2. Journal entries — `api/finance/journal-entries` (CORE; `/reverse` RR)

| Method & route | Permission | Body → result |
|---|---|---|
| `GET` + `JournalEntryQuery` | JE-READ | `PaginatedResponse<JournalEntryListItem>` |
| `GET /{uuid}` | JE-READ | `JournalEntryDetail` |
| `POST` | GL_JOURNAL_CREATE (+ GL_JOURNAL_POST when it posts) | `CreateJournalEntry` → `JournalEntryDetail` (Draft, or Posted) |
| `PUT /{uuid}` | GL_JOURNAL_EDIT | `UpdateJournalEntry` → `JournalEntryDetail` (manual Draft only) |
| `DELETE /{uuid}` | GL_JOURNAL_DELETE | — → `{ deleted: true }` (manual Draft only) |
| `POST /{uuid}/post` | GL_JOURNAL_POST | — → `JournalEntryDetail` (Posted) — any Draft, also an A44 source-document draft |
| `POST /{uuid}/reverse` | GL_JOURNAL_REVERSE | `ReverseJournalEntry` → `JournalEntryDetail` **of the new reversal entry** (RR, §3) |

```ts
JournalEntryQuery (query string) { journalTypeUuid?; fiscalYearUuid?; fiscalPeriodUuid?; status?: 'Draft'|'Posted'|'Reversed'; fromDate?; toDate?;
  accountUuid? /* has a line on it */; partnerUuid?; sourceModule?: 'finance'|'pos'|'suppliers'|'inventory'|'manual'|'opening';
  search? /* entryNumber, reference, description contain */; page=1; pageSize=25 (max 200);
  sortBy?: 'entryDate'(default)|'entryNumber'|'totalDebit'|'createdAt'|'postedAt'; sortDirection?: 'desc'(default)|'asc' }
JournalEntryListItem { uuid; entryNumber; journalType: JournalTypeRef; entryDate; reference?; description?; entryType; sourceModule?; status;
  totalDebit; totalCredit; currencyCode?; lineCount; isManual; isOpeningEntry; isClosingEntry; isReversal; createdAt; postedAt? }
JournalTypeRef { uuid; code; name; type }
JournalEntryDetail { uuid; entryNumber; journalType: JournalTypeRef; entryDate; documentDate?; fiscalYear?: { uuid; fiscalYearName };
  fiscalPeriod?: { uuid; periodName; periodNumber; status }; reference?; description?;
  entryType /* SALES_INVOICE … (FinDocType upper snake) | MANUAL_JOURNAL | OPENING_BALANCE | YEAR_END_CLOSE */;
  sourceModule?; sourceDocumentUuid?; idempotencyKey?; currencyCode?; exchangeRate?; status; isManual; isOpeningEntry; isClosingEntry;
  totalDebit; totalCredit; lineCount; reversesEntry?: EntryRef; reversedByEntry?: EntryRef; reversalReason?;
  createdAt; createdByName?; postedAt?; postedByName?; modifiedAt; modifiedByName?;
  canEdit; canDelete; canPost; canReverse /* state + the caller's permissions */; lines: JournalEntryLine[] }
EntryRef { uuid; entryNumber }
JournalEntryLine { lineUuid; lineNumber; account: AccountRef; description?; debit; credit /* base */; currencyCode?; amountCurrency? /* signed, debit + */;
  exchangeRate?; partnerUuid?; partnerType?; partnerName?; costCenter?; project?; taxCode?; taxAmount?;
  reconciled; amountResidual /* signed base, debit + */; reconcileGroupUuid?; reconcileGroupNumber?; clearingEntry?: EntryRef; clearingDate? }
CreateJournalEntry { journalTypeUuid; entryDate; documentDate?; reference? (≤200); description? (≤500); currencyCode? /* null = base */;
  exchangeRate? /* required > 0 when foreign: 1 unit = rate × base */; isOpeningEntry?; isClosingEntry?;
  clientRequestId?: uuid /* idempotent retry: same id → same entry (key manual:{id}) */; postImmediately?: boolean; lines: JournalEntryLineInput[] }
UpdateJournalEntry { entryDate; documentDate?; reference?; description?; currencyCode?; exchangeRate?; isOpeningEntry?; isClosingEntry?; lines: JournalEntryLineInput[] }
JournalEntryLineInput { accountUuid; debit; credit /* in the line's currency */; description?; partnerUuid?; partnerType?: 'Customer'|'Vendor';
  currencyCode? /* null = the entry's */; exchangeRate? /* null = the entry's */; costCenter?; project?; taxCode?; taxAmount? }
```
Behaviour:
- Number `{prefix}/{year}/{seq:D4}` (e.g. `SALE/2026/0001`, `JV/2026/0003`), assigned at creation (Draft too), per (type, fiscal year),
  gap-free except deleted drafts (no backfill); year = fiscal year's start year (`yyyy-MM` for a second fiscal year starting in the same
  calendar year; the calendar year outside every fiscal year). Never changes (BR-JE-010). **Older journals keep `JE-YYYY-NNNNN`** (R-4);
  A42 opening and A44 gateway journals keep JE- numbers until GW moves the gateway onto the engine (C11).
- Base amounts = line amount × rate rounded to 2 dp; in the base currency amounts are kept as given (4 dp). Balance must hold within
  0.01 in the transaction currency (when all lines share a foreign currency) **and** in base (JE-001). Lines' `amountResidual` starts at debit − credit.
- Flags: the Opening type forces `isOpeningEntry`, the Closing type `isClosingEntry`; both → JE-013. Auto-post types post at creation (needs GL_JOURNAL_POST → 403 `"You do not have permission to post journal entries."`).
- `requiresPartner` applies to manual entries only (D-13); a partner needs both `partnerUuid` and `partnerType`; it is looked up
  (BusinessPartner of the caller's org) and its name is stored.
- Edit/delete: only manual entries (`isManual`: key `manual:…`) in Draft; journal type, number and key cannot change; the date must stay in the
  number's fiscal year. Post re-checks period and accounts (TS-JE-019). A concurrent second post → 409 `Entry is already posted`.
- Not found → 404 `"Journal entry not found."`. List filter status invalid → 400 `"Status must be Draft, Posted or Reversed."`;
  bad sortBy → 400 `"Sort by entryDate, entryNumber, totalDebit, createdAt or postedAt."`; toDate < fromDate → 400 `"The to date must not be before the from date."`.
- History (GlSetupHistory, A-19): `JournalEntryPostedEvent` on every API post.

## 3. Reverse — `POST api/finance/journal-entries/{uuid}/reverse` (RR)

```ts
ReverseJournalEntry { reason: string /* required, ≤ 200 */; reversalDate?: 'yyyy-MM-dd' /* null = the original's entryDate */ }
→ JournalEntryDetail of the reversal (its reversesEntry = the original); the original's detail then shows status 'Reversed' + reversedByEntry.
```
Wiring: CORE's `IJournalEntryService.ReverseAsync` delegates to `IJournalReversalService` (RR implements and registers it; until then 501-ish
`NotImplementedException`). RR builds the mirror through `JournalEntryWriter.CreateAsync(draft, post:true)` with
`ReversesEntryId = original.Id`, `ReversalReason`, `IdempotencyKey = reversal:{original uuid}` (unique; a second call returns the
existing reversal, 200), same journal type, `EntryType`/`SourceModule`/`SourceDocumentUuid`/currency/rate copied, flags copied
(`isOpeningEntry`/`isClosingEntry`), lines mirrored (debit ↔ credit, same account/partner/description/dimensions, `AmountsInBase = true`,
`AmountCurrency = −original`), description `"Reversal of {entryNumber}: {reason}"`, `AllowInactiveAccounts = true`; then sets
`original.Status = 'Reversed'` and `original.ReversedByEntry = reversal` in the same SaveChanges/transaction; balances move through the
writer (net zero). BR-RV-008: for each account, original + reversal lines are put into one **Full** reconcile group (number from
`IJournalEntrySequenceService.NextReconcileGroupNumberAsync`), residuals 0, `reconciled = true`, clearing entry = the reversal, clearing date =
reversal date — skipped for an account whose original line is already in a group. Publishes `JournalEntryReversedEvent`.
Refusals (JE-015 unless noted): 409 `"Only posted entries can be reversed"` (Draft) · 409 `"Entry has already been reversed"` ·
409 `"A reversal entry cannot be reversed"` · 409 `"The opening balance entry cannot be reversed"` (EntryType OPENING_BALANCE) ·
400 `"A reversal reason is required"` · 400 `"The reversal reason can be at most 200 characters"` · period refusal of the reversal date
with codes JE-005..JE-008 and texts `"Reversal period is closed"` / `"Reversal period is locked"` / `"Reversal period accepts only adjustment entries"` /
`"No fiscal period covers the reversal date"` / `"Fiscal year is closed"`. A gateway (source-document) journal may be reversed by hand
(D-14): allowed with GL_JOURNAL_REVERSE, the source document is not changed; the detail's `sourceModule` tells the UI to warn.

## 4. Reconciliation — `api/finance/reconciliation` (RR)

| Method & route | Permission | Body → result |
|---|---|---|
| `GET /open-items?accountUuid&partnerUuid&partnerType&fromDate&toDate` | GL_RECONCILE_VIEW, GL_RECONCILE_EXECUTE | `OpenItem[]` (accountUuid required) |
| `POST /suggest` | GL_RECONCILE_VIEW, GL_RECONCILE_EXECUTE | `{ accountUuid; lineUuids: uuid[]; maxResults?: number (default 10, max 50) }` → `ReconcileSuggestion[]` |
| `POST /reconcile` | GL_RECONCILE_EXECUTE | `{ lineUuids: uuid[] }` → `ReconcileGroup` |
| `GET /{groupUuid}` | GL_RECONCILE_VIEW, GL_RECONCILE_EXECUTE | `ReconcileGroup` |
| `POST /{groupUuid}/unreconcile` | GL_RECONCILE_EXECUTE | — → `{ groupNumber; linesRestored: number }` |

```ts
OpenItem { lineUuid; entry: EntryRef; entryDate; journalTypeCode; reference?; description?; lineNumber; account: AccountRef;
  partnerUuid?; partnerType?; partnerName?; debit; credit; amountResidual /* signed */; reconcileGroupUuid?; reconcileGroupNumber? /* partial */ }
ReconcileSuggestion { lineUuids: uuid[] /* the opposite-side lines to add */; total /* Σ their residuals, signed */;
  residualAfter /* Σ selected + suggested */; exact: boolean /* |residualAfter| < 0.01 */ }
ReconcileGroup { uuid; groupNumber /* REC/{year}/{seq:D4} */; reconcileType: 'Full'|'Partial'; account: AccountRef; amount /* matched, ≥ 0 */;
  currencyCode?; reconciledAt; createdByName?; residual /* signed residual left */; lines: OpenItem[] /* incl. reconciled */ }
```
Open items = lines of Posted/Reversed entries on the account with `reconciled = false` and non-zero residual (partially reconciled lines
included, with their group); ordered entryDate, entryNumber, lineNumber. Suggest (TS-RC-007): opposite-side open lines (credits for a
debit selection) whose residuals sum nearest to −Σselected, exact matches first.
Reconcile (D-16): ≥ 2 lines, same organization and account, parents Posted/Reversed, none already in a group. Σ residual |< 0.01| →
**Full**: every line residual 0, `reconciled = true`, clearing entry = the latest entry (by date, then id) of the group, clearing date =
its entry date. Otherwise **Partial**: matched = min(Σ debit residuals, −Σ credit residuals); the smaller side's lines are cleared fully
(residual 0, reconciled, clearing set) and the larger side's lines are reduced oldest first (entryDate, then line id) — so TS-RC-002 leaves the
invoice at 400 and the payment at 0; every line keeps `reconcileGroupId`. Unreconcile (BR-RC-007): every line of the group back to
`amountResidual = debit − credit`, `reconciled = false`, group/clearing cleared, group deleted.
Texts (409 unless noted): RC-001 `"Only lines from posted entries can be reconciled"` · RC-002 `"All lines must belong to the same account"` ·
RC-003 400 `"Minimum 2 lines required"` · RC-004 `"Line {n} of {entryNumber} is already in reconciliation {groupNumber}: unreconcile it first"` ·
RC-005 404 `"Journal line not found"` · RC-006 404 `"Reconciliation not found"` · RC-007 400 `"The lines have nothing left to reconcile"` ·
RC-008 400 `"Choose an account"`.

## 5. Trial balance & ledgers — `api/finance/trial-balance` (RPT)

| Method & route | Permission | Result |
|---|---|---|
| `GET ?fromDate&toDate&fiscalYearUuid&includeOpeningBalances=true&includeClosingEntries=false&includeZeroBalances=false&costCenter&project` | GL_REPORTS_VIEW | `TrialBalanceReport` |
| `GET /export?…same…` | GL_REPORTS_VIEW | file `trial-balance-{from}-{to}.csv` (text/csv, UTF-8 BOM) |
| `GET /accounts/{accountUuid}/balance?asOfDate` | GL_REPORTS_VIEW | `AccountBalance` |
| `GET /accounts/{accountUuid}/ledger?fromDate&toDate&page=1&pageSize=50` | GL_REPORTS_VIEW | `AccountLedger` |

```ts
TrialBalanceReport { fromDate; toDate; fiscalYear?: { uuid; fiscalYearName }; generatedAt; currencyCode?; includeOpeningBalances; includeClosingEntries;
  lines: TrialBalanceLine[]; totals: { openingDebit; openingCredit; periodDebit; periodCredit; closingDebit; closingCredit };
  totalDebit /* = totals.closingDebit */; totalCredit; isBalanced /* |Δ| < 0.01 */ }
TrialBalanceLine { account: AccountRef; rootType; accountType?; level; openingDebit; openingCredit; periodDebit; periodCredit;
  closingDebit; closingCredit; balance /* closingDebit − closingCredit, + = debit */ }
AccountBalance { account: AccountRef; asOfDate; debit; credit; balance /* debit − credit */ }
AccountLedger { account: AccountRef; fromDate; toDate; openingBalance; closingBalance; totalDebit; totalCredit;
  page: PaginatedResponse<LedgerLine> }
LedgerLine { lineUuid; entry: EntryRef; entryDate; journalTypeCode; status; reference?; description?; lineNumber; partnerName?;
  debit; credit; runningBalance /* openingBalance + Σ to this line, debit − credit */; reconciled }
```
Rules (D-17): base amounts of lines whose entry is **Posted or Reversed** (BR-TB-001; drafts never, TS-TB-006); leaf accounts of the
organization (active, plus inactive ones with activity); opening = Σ before `fromDate` (0 when includeOpeningBalances=false, and then
closing = period only); period = fromDate..toDate; closing entries (`isClosingEntry`) excluded unless includeClosingEntries (BR-TB-004);
zero-activity accounts hidden unless includeZeroBalances (BR-TB-005); ordered by accountCode. `fiscalYearUuid` instead of dates = the year's
dates. Signs are debit − credit here (the chart's `currentBalance` is in the account's normal direction — do not mix). Ledger ordered
entryDate, entryNumber, lineNumber. Texts (400): `"Enter a from and a to date, or a fiscal year."` · `"The to date must not be before the from date."` ·
404 `"Account not found."` · 400 `"Choose a Level 4 account."`. Performance target 10k+ entries < 2 s (tasks 427–431): aggregate in SQL
(GroupBy on lines joined to entries), never load lines into memory.

## 6. Period close & year-end close (RPT)

| Method & route | Permission | Body → result |
|---|---|---|
| `GET api/finance/period-close/{fiscalYearUuid}/status` | GL_PERIOD_CLOSE_VIEW, _EXECUTE, _ADMIN | `PeriodCloseOverview` |
| `POST api/finance/period-close/{periodUuid}/close` | GL_PERIOD_CLOSE_EXECUTE, _ADMIN | `{ targetStatus: 'Locked'|'AdjustmentOnly' }` → `CloseResult` |
| `POST api/finance/period-close/{periodUuid}/reopen` | GL_PERIOD_CLOSE_EXECUTE, _ADMIN | — → `CloseResult` (→ Open) |
| `POST api/finance/period-close/{periodUuid}/final-close` | GL_PERIOD_CLOSE_ADMIN | — → `CloseResult` (Locked → Closed) |
| `GET api/finance/year-end-close/{fiscalYearUuid}/preview?retainedEarningsAccountUuid` | GL_PERIOD_CLOSE_VIEW, _EXECUTE, _ADMIN | `YearEndPreview` |
| `POST api/finance/year-end-close/{fiscalYearUuid}/execute` | GL_PERIOD_CLOSE_ADMIN | `{ retainedEarningsAccountUuid? }` → `YearEndCloseResult` |

```ts
PeriodCloseOverview { fiscalYear: { uuid; fiscalYearName; startDate; endDate; isClosed; closedAt? }; periods: PeriodCloseStatus[];
  closingEntry?: EntryRef; canCloseYear: boolean; yearCloseBlockers: string[] /* the PC texts execute would answer */ }
PeriodCloseStatus { uuid; periodNumber; periodName; startDate; endDate; status; postedEntryCount; draftEntryCount; hasUnpostedDrafts;
  allowedActions: ('Lock'|'AdjustmentOnly'|'Reopen'|'FinalClose')[] /* by state and the caller's permissions */ }
CloseResult { period: PeriodCloseStatus; oldStatus; newStatus }
YearEndPreview { fiscalYear: { uuid; fiscalYearName; startDate; endDate }; retainedEarningsAccount?: AccountRef; entryDate; periodNumber /* 13 or 12 */;
  totalRevenue; totalExpenses; netIncome /* revenue − expenses */; lines: YearEndPreviewLine[]; canExecute; blockers: string[] }
YearEndPreviewLine { account: AccountRef; rootType: 'Revenue'|'Expense'|'Equity'; balance /* natural: revenue credit − debit, expense debit − credit */;
  closingDebit; closingCredit }
YearEndCloseResult { fiscalYear: { uuid; fiscalYearName; isClosed; closedAt }; closingEntry?: EntryRef; totalRevenueClosing; totalExpenseClosing;
  netIncomeTransferred; alreadyClosed: boolean }
```
Transitions (D-11): Open → AdjustmentOnly | Locked; AdjustmentOnly → Open (reopen) | Locked; Locked → Open (reopen) | Closed (final, admin);
Closed → nothing. Lock/AdjustmentOnly with drafts dated in the period → refused (BR-PC-001); Reopen only from Locked/AdjustmentOnly; a closed
fiscal year refuses everything. Every change writes `FiscalPeriodStatusChangedEvent`. The A42 route `PUT api/finance/fiscal-periods/{uuid}`
keeps working and also accepts `AdjustmentOnly` (its own A42 transitions); the period-close routes are the A47 way.
Year-end (D-18): periods 1–12 all Locked or Closed, period 13 (if any) Open or AdjustmentOnly, year not closed, no Draft entries in the
year, retained earnings = the request's account or DEFAULT_RETAINED_EARNINGS, an active leaf Equity account. Closing JE through
`JournalEntryWriter.CreateAsync(post:true)`: journal type CLOSE (`JournalTypeResolver.ByCodeAsync(org, "CLOSE")`), `EntryType = YEAR_END_CLOSE`,
`IsClosingEntry`, date = year end, key `yearclose:{fiscal year uuid}` (unique; a second run returns the existing result, `alreadyClosed:true`, TS-PC-008),
`AmountsInBase = true`, `SkipPeriodCheck = true` (period 12 is Locked when there is no period 13), lines: each Revenue account with a balance debited,
each Expense account credited, one Retained Earnings line (credit profit, debit loss, none at 0; no entry at all when every balance is 0 —
`closingEntry` null). Balances = Posted/Reversed lines of the year, closing entries excluded. Then `FiscalYear.IsClosed/ClosedAt/ClosedBy`, every period
Closed, `FiscalYearClosedEvent`. Texts: PC-001 409 `"Period has {n} unposted draft entries"` · PC-002 409 `"Closed periods cannot be reopened"` ·
PC-003 409 `"Only a Locked period can be closed finally"` · PC-004 409 `"Cannot change a {old} period to {new}"` · PC-005 409
`"All periods must be Locked or Closed"` · PC-006 409 `"Period 13 must be Open or AdjustmentOnly"` · PC-007 409 `"The fiscal year has {n} unposted draft entries"` ·
PC-008 400 `"Retained earnings account must be an active Level 4 Equity account"` · PC-009 400 `"Choose a retained earnings account, or set the DEFAULT_RETAINED_EARNINGS default"` ·
PC-010 409 `"Fiscal year is closed"` · 404 `"Fiscal year not found."` / `"Fiscal period not found."` · 400 `"Target status must be Locked or AdjustmentOnly."`.

---

## 7. Engine seams for other agents (C# — internal to SMS.Modules.Accounting)

- `JournalEntryWriter` (Services/Journals): `CreateAsync(JournalDraft, bool post)` → `JournalWriteResult(Entry, Created)` (tracked, **not saved**, call inside
  `AccountingDbContext.InTransactionAsync` and SaveChanges yourself; unique keys `manual:`/`reversal:`/`yearclose:` return the existing entry);
  `PostAsync(entry, userId, userName)`; `UpdateDraftAsync`; `DeleteDraftAsync`; `GetEntityAsync(org, id)`; `FindByKeyAsync(org, key)`;
  `StampLegacyAsync(journal)` (fills A47 columns of a journal an A42/A44 writer built). `JournalDraft` flags: `AmountsInBase`, `IsManual`,
  `AllowInactiveAccounts`, `SkipPeriodCheck` (system writers only).
- `GlBalanceMover.MoveAsync / MoveLinesAsync(lines, moveBalance, sign)` — the only balance writer (D-9).
- `IJournalEntrySequenceService.NextEntryNumberAsync / NextReconcileGroupNumberAsync` (inside the transaction).
- `JournalPeriodResolver.ResolveAsync(org, date, isOpening, isClosing)` → `PeriodResolution(FiscalYearId, FiscalPeriodId, ErrorCode, Message)`.
- `JournalTypeResolver.ResolveAsync(org, entryType, accountIds)` (D-12) / `ByCodeAsync(org, code)`; `JournalTypeSeeder.EnsureAsync(org)`.
- `IJournalReversalService` (RR implements): `Task<JournalEntryDetailModel> ReverseAsync(AccountingCaller, Guid entryUuid, ReverseJournalEntryRequest, CancellationToken)`.
- Errors: `JournalValidationException(code, message, lineNumber?)` / `(IReadOnlyList<JournalError>)`, `JournalConflictException(code, message)`;
  put `[JournalErrorFilter]` on your controller; derive from `AccountingControllerBase` and add `[RequiresGlSetup]`.
- Caller checks: `AccountingCaller.CanReverseJournal`, `CanViewReconciliation`, `CanReconcile`, `CanViewReports`, `CanViewPeriodClose`,
  `CanExecutePeriodClose`, `CanAdministerPeriodClose`. Events: `JournalEntryReversedEvent`, `FiscalYearClosedEvent` (already in the history writer).

## Changes
- C-1 (CORE, after RR's first files): `POST /journal-entries/{uuid}/reverse` returns `JournalReversalResult { reversal: JournalEntryDetail;
  original: EntryRef; reconcileGroupNumbers: string[]; warning?: string /* set for a source-document journal */ }` (RR's
  `JournalReversalResultModel`), not the bare detail. `IJournalReversalService` is RR's interface (Services/Journals/JournalReversalService.cs);
  `IJournalEntryService.ReverseAsync` returns that model and delegates to it.
- C-2 (CORE): `AccountRef` is the shared A42 `AccountRefModel` `{ uuid; accountCode; accountName; accountType?; rootType? }` (rootType added).
- C-3 (RR, §3): also refused 409 JE-015 `"The year-end closing entry cannot be reversed"` (EntryType YEAR_END_CLOSE; analysis D-RR-3).
- C-4 (RR, §4): the 404s RC-005 / RC-006 (and `"Account not found."`) carry no `result.errorCode` (the JournalErrorFilter codes 400/409 only).
  Open items are capped at 2000, oldest first (D-RR-7). Simple input checks without a code: `"Select at least one line."` (suggest with no
  lineUuids), `"The to date must not be before the from date."`, `"Partner type must be Customer or Vendor."`.
- C-5 (RPT, §6 — **needs CORE's agreement**): the year-end P&L balances include `isClosingEntry` entries (manual year-end adjustments in
  period 13 / an AdjustmentOnly period); only entries keyed `yearclose:` are excluded. With "closing entries excluded" such an adjustment
  would stay on the P&L account after the close and retained earnings would be wrong (analysis D-RPT-5). The trial balance keeps BR-TB-004.
- C-6 (RPT, §5): balance/ledger answer on both `api/finance/trial-balance/accounts/{accountUuid}/balance|ledger` and
  `api/finance/accounts/{accountUuid}/balance|ledger` (FSD §16.4). Both refuse a group account 400 `"Choose a Level 4 account."`; the ledger
  without dates → 400 `"Enter a from and a to date, or a fiscal year."`. Order entryDate, entryNumber, (entry id), lineNumber; pageSize 1–500.
  Balance and ledger include closing entries; the ledger takes an extra `includeClosingEntries` (default true) for the TB drill-down.
- C-7 (RPT, §5/§6 additive fields): TrialBalanceQuery `fiscalPeriodUuid?` (its dates when no dates/year); TrialBalanceLine `isActive`
  (inactive leaves with activity are listed); LedgerLine `isClosingEntry`; PeriodCloseOverview `defaultRetainedEarningsAccount?`;
  YearEndPreview `existingClosingEntry?` (year already closed: its lines, blockers = [PC-010]); YearEndCloseResult `closingEntries[]`
  (several entries only past 499 P&L accounts — keys `yearclose:{uuid}:2` …) and `retainedEarningsAccount?`. PC-001/PC-007 also apply to
  AdjustmentOnly. Reopen of an Open period and any same-status move → PC-004. `execute` on a year closed by the A42 close → `alreadyClosed:true`, `closingEntry` null.
- C-8 (RR, §4, SR-FE-1): `GET api/finance/reconciliation/groups?accountUuid&partnerUuid&fromDate&toDate&page=1&pageSize=25` (GL_RECONCILE_VIEW,
  GL_RECONCILE_EXECUTE) → `PaginatedResponse<ReconcileGroupListItem>`, newest first (reconciledAt, then id), pageSize max 200, every filter optional;
  `from`/`to` accepted as aliases of `fromDate`/`toDate`, which filter on the reconciliation date (reconciledAt, UTC). `partnerUuid` = groups with a
  line of that partner. `ReconcileGroupListItem { uuid; groupNumber; reconcileType: 'Full'|'Partial'; account: AccountRef; amount; currencyCode?;
  reconciledAt; createdByName?; lineCount }`. Texts: 400 `"The to date must not be before the from date."`; another org's account → 404 `"Account not found."`.
  Includes the reversal auto-groups (BR-RV-008).
- C-9 (CORE review): **C-3 accepted** (a closing entry is undone by reopening the year, not reversed). **C-4 accepted**: 404s stay plain
  (no `errorCode`) on every A47 route — §0's coded body is for 400/409 only. **C-5 accepted**: the year-end P&L includes `isClosingEntry`
  adjustments and excludes only `yearclose:` entries; this replaces §6 "closing entries excluded" (the trial balance keeps BR-TB-004).
  **C-6, C-7, C-8 accepted.**
- C-10 (CORE, SR-FE-2): the A42 read GETs `api/finance/fiscal-years`, `fiscal-years/{uuid}`, `fiscal-years/{uuid}/periods`,
  `chart-of-accounts` (tree), `chart-of-accounts/flat` and `accounting-defaults` also accept every A47 code (GL_JOURNAL_*, GL_RECONCILE_*,
  GL_REPORTS_VIEW, GL_PERIOD_CLOSE_*) — reads only; no write changed (`AccountingCaller.CanReadGlLookups`).
- C-11 (CORE, SR-RPT-1 declined): the A42 `PUT api/finance/fiscal-periods/{uuid}` keeps its own A42 transitions (Open → Closed directly,
  Open ⇄ Locked, AdjustmentOnly accepted); the D-11 state machine is enforced on the A47 period-close routes only (§6). Its unknown-status
  text is now `"Status must be Open, Locked or Closed (or AdjustmentOnly)."`.
- C-12 (CORE, REV fixes): **§1** `PATCH /journal-types/{uuid}/deactivate` on the system MISC, OPEN or CLOSE type → 409 `"The {code} journal type cannot be deactivated: system entries (fallback, opening and year-end entries) are written to it."` (SALE/PURCH/CASH/BANK may be deactivated; their system journals fall back to MISC). **§2** a manual entry's base totals must be equal exactly (JE-001; 4-dp amounts no longer pass with a < 0.01 difference); `canReverse` is false for OPENING_BALANCE and YEAR_END_CLOSE entries. **§0 period rule** a regular entry never goes to period 13: on the year's last day with period 12 Locked/Closed it is refused even when period 13 is Open (adjustment entries only there).
