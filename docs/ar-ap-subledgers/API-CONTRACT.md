# Addendum 48 — AR & AP Sub-Ledgers: API contract

Owner of this file: **CORE**. Every other agent codes against it; a change after the first version goes in the
**Changes** section at the end (dated, who asked, what changed) — never a silent edit above it.
Read with `ADDENDUM-48-ANALYSIS.md` (Part A + D-1..D-18 override the FSD; D-CORE-n in Part B).

Section owners: §1 CORE · §2 AR (credit control) · §3 AR (dunning) · §4 AR (credit memos) · §5 RPT (AR/AP inquiry, aging,
statements, vendor reconciliation) · §6 AP (matching) · §7 AP (schedules, early-payment terms) · §8 AP (payment runs) ·
§9 AP (debit memos) · §10 CORE (in-process interfaces and seams) · §11 CORE (schema reference).

---

## 0. Conventions (all sections)

| Item | Rule |
|---|---|
| Base route | `api/finance/...` (A-3). Ids in routes and bodies are **uuids**; an `int` id never leaves the server. |
| JSON | camelCase. Dates `yyyy-MM-dd` (DateOnly), timestamps ISO-8601 UTC. Enums are **strings**, spelled exactly as in §0.1. Unknown enum text → 400 `"Unknown {field} '{value}'. Allowed: {list}."`. |
| Envelope | Single result: `ApiResponse<T>` `{ success, message, result }`. Lists: `ApiResponse<PaginatedResponse<T>>` `{ data[], totalRecords, page, pageSize, totalPages, hasNext, hasPrevious }`; `page` default 1, `pageSize` default 25, max 200 (larger → 200). Small fixed lists (procedures, tolerance rules, terms) are plain arrays. |
| Errors | `BadRequestException` → **400** (validation; message = the exact text in this contract), `NotFoundException` → **404** (`"{Thing} with key '{uuid}' was not found."` — another organization's row is 404 too), `ConflictException` → **409** (state conflicts), `ForbiddenException` → **403**. Never 500 for a user error. |
| Money | Amounts in a document's own currency are rounded to **2 dp** (the Finance documents are decimal(18,2)) and carry `currencyCode`. Totals across documents (exposure, aging totals, KPIs, run totals) are in the organization's **sale base** (AR) / **purchase base** (AP) currency, converted at each document's locked rate (`BaseGrandTotal` / `BaseTotalAmount`, or amount × `ExchangeRate`); they carry `baseCurrencyCode` (D-13). A document with no rate is converted at the current rate via `ICurrencyService` if it can be, else counted 1:1 and flagged `rateMissing: true` in the detail row. |
| Source of truth | Finance documents and ledgers only (D-1). **No A48 code reads `JournalEntryLines` / `AccountingDbContext`.** |
| Feature gate | Every A48 **write** action carries `[RequiresFeature("MODULE_FINANCE")]` (like the A40 opening-balance PUT). Reads carry no feature gate. |
| Tenant | The caller's organization (`ITenantContext.OrganizationId`). Services filter on it **explicitly** (`.Where(x => x.OrganizationId == org)`) — the tenant filter is bypassed for super admins (memory: tenant filter). Jobs pass the organization explicitly. |
| Who | `User.GetUserId()` (int) + a name snapshot from `IOrgUserDirectory` (`ArApCaller`, §10.4). Columns `...By` (int?) + `...ByName` (nvarchar(200)). |
| Concurrency | Rows with a `rowVersion` (profiles, schedules, runs) answer **409** `"{Thing} was changed by someone else; reload and try again."` on `DbUpdateConcurrencyException`. |

### 0.1 Enum spellings (persisted exactly like this; CHECK constraints enforce them)

| Enum | Values |
|---|---|
| PartnerRole | `Customer`, `Vendor` |
| CreditRiskTier | `Low`, `Medium` (default), `High` |
| PaymentPriority | `High`, `Normal` (default), `Low` |
| CreditControlMode | `Off` (default), `Warn`, `Block` |
| CreditHoldAction | `Hold`, `Release`, `TempIncrease`, `TempExpired`, `RiskTier`, `Override`, `DunningHold`, `DunningReset` |
| CreditHoldRequestStatus | `Pending`, `Completed` (default), `Rejected` |
| StatementType | `Activity`, `OpenItems` |
| StatementEmailStatus | `NotSent` (default), `Sent`, `Failed` |
| DunningActionType | `Email` (default), `Letter`, `Manual` |
| DunningRunStatus | `Draft`, `Processing`, `Completed`, `Cancelled` |
| DunningRunItemStatus | `Pending`, `Sent`, `Skipped`, `Failed`, `Manual` (Letter/Manual action recorded, nothing sent) |
| MatchType | `ThreeWay`, `TwoWay` |
| LineMatchResult (invoice header) | `Matched`, `PartialMatch`, `Mismatch` |
| MatchLineStatus | `Pending`, `Matched`, `QuantityMismatch`, `PriceMismatch`, `Unmatched` (no PO line behind it), `Overridden` |
| PaymentScheduleStatus | `Open`, `Blocked`, `Scheduled`, `PartiallyPaid`, `Paid`, `Cancelled` |
| PaymentRunStatus | `Draft`, `Proposed`, `Reviewing`, `Approved`, `Executing`, `Completed`, `Cancelled` |
| PaymentRunItemStatus | `Proposed`, `Approved`, `Removed`, `Paid`, `Failed` |
| PaymentRunBatchStatus | `Pending`, `Paid`, `Failed` |
| SupplierNoteType | `DebitNote`, `CreditNote` |
| DebitNoteOrigin | `SRO` (default — raised from a supplier return), `MEMO` (A48 standalone debit memo) |
| AgingBasis | `DueDate` (default), `DocumentDate` |
| Payment methods | AP: the SupplierPayment codes `BANK_TRANSFER`, `ONLINE_WIRE`, `CHEQUE`, `CASH`. AR: the `CustomerPaymentMethods.All` codes `CASH`, `CHEQUE`, `BANK_TRANSFER`, `CARD`, `ONLINE`, `MOBILE`. |

### 0.2 Permissions (D-14; codes in `PermissionCodes`, module Finance via the `ModuleCodeMap` prefixes)

| Code | Gates |
|---|---|
| `AR_VIEW` | AR inquiry, aging, DSO, summary, statements (view/PDF), credit exposure/history, dunning lists, customer profiles (read) |
| `AR_STATEMENT_SEND` | emailing statements, batch statements |
| `AR_CREDIT_MEMO` | creating customer credit memos and applying them |
| `CREDIT_CHECK` | the explicit credit-check endpoint |
| `CREDIT_MANAGE` | hold / release / temporary limit / risk tier |
| `CREDIT_OVERRIDE` | confirming a sale order the credit check blocks (with a reason) |
| `DUNNING_EXECUTE` | preview / execute / cancel dunning runs, skip items |
| `DUNNING_ADMIN` | manual dunning-level reset |
| `AP_VIEW` | AP inquiry, aging, DPO, summary, vendor statements, schedules, match lists, payment runs (read), vendor profiles (read) |
| `AP_EXECUTE` | block/unblock schedules, change schedule priority/method/terms, vendor reconciliation |
| `AP_DEBIT_MEMO` | creating AP debit memos and applying them |
| `MATCH_EXECUTE` | running the per-line match |
| `MATCH_OVERRIDE` | overriding a match variance (reason required) |
| `MATCH_APPROVE` | approving a matched / overridden invoice (the existing approval path) |
| `PAYMENT_RUN_PROPOSE` | creating a proposal |
| `PAYMENT_RUN_EDIT` | removing / restoring items, changing an item's method, sending to review |
| `PAYMENT_RUN_APPROVE` | approving a run |
| `PAYMENT_RUN_EXECUTE` | executing a run, exporting the payment file |
| `PAYMENT_RUN_ADMIN` | cancelling a run |
| `FINANCE_AR_AP_ADMIN` | A48 configuration: dunning procedures, tolerance rules, early-payment terms, partner profiles (write), the A48 settings (credit control mode) |

Grants (CORE seeds, D-CORE-7): Organization Admin + System Admin get all (via `PermissionCodes.All`). Finance Manager: all
A48 codes. Finance Officer: `AR_VIEW, AP_VIEW, AR_STATEMENT_SEND, CREDIT_CHECK, AP_EXECUTE, MATCH_EXECUTE,
PAYMENT_RUN_PROPOSE, PAYMENT_RUN_EDIT, DUNNING_EXECUTE`. Auditor: `AR_VIEW, AP_VIEW`. Any role (built-in or custom) allowed
`SALES_INVOICE_VIEW` or `CUSTOMER_LEDGER_VIEW` gets `AR_VIEW`; allowed `INVOICE_VIEW` or `PAYMENT_VIEW` gets `AP_VIEW`
(only missing grants are added; a grant switched off stays off). Proposer ≠ approver is not enforced (R-2).

A route's `[RequirePermission(...)]` lists the codes in the "Permission" column (any of them). A read that lists several
codes accepts any.

---

## 1. Partner financial profiles and A48 settings — owner CORE

### 1.1 `GET api/finance/partner-profiles/{partnerUuid}?role=Customer|Vendor`

Get-or-create (D-4): the first read creates the row with defaults (tier `Medium`, priority `Normal`, level 0, no hold).
Permission: `AR_VIEW, CREDIT_CHECK, CREDIT_MANAGE, AP_VIEW, AP_EXECUTE, FINANCE_AR_AP_ADMIN` on the route; the service
then requires, for `role=Customer`, one of `AR_VIEW, CREDIT_CHECK, CREDIT_MANAGE, FINANCE_AR_AP_ADMIN`, for `role=Vendor`
one of `AP_VIEW, AP_EXECUTE, FINANCE_AR_AP_ADMIN` (else 403).

- 400 `"role is required: Customer or Vendor."` (missing) / the unknown-enum text.
- 404 `"Business partner with key '{uuid}' was not found."` — absent, deleted or another organization's.
- 400 `"{name} is not a customer."` / `"{name} is not a vendor."` — the partner's `IsCustomer` / `IsVendor` flag is off.

`PartnerFinancialProfileModel`:
```json
{
  "uuid": "…", "partnerUuid": "…", "role": "Customer",
  "partnerCode": "C-0001", "partnerName": "Acme", "partnerEmail": "ar@acme.test", "partnerIsActive": true,
  "creditLimit": 50000.00,            // BusinessPartner.CreditLimit (read-only here; null/0 = no limit, D-CORE-3)
  "temporaryCreditLimit": 70000.00, "temporaryCreditExpiry": "2026-11-30", "temporaryLimitActive": true,
  "effectiveCreditLimit": 70000.00,   // null = no limit (BR-CR-002, D-CORE-3)
  "creditHold": false, "creditHoldReason": null, "creditHoldAt": null, "creditHoldByName": null,
  "riskTier": "Medium",
  "dunningProcedureUuid": null, "dunningProcedureCode": null,   // null = the organization's default procedure
  "currentDunningLevel": 0, "lastDunningDate": null, "blockSalesAtDunning": false, "excludeFromDunning": false,
  "defaultPaymentMethod": null,                                 // AR method code
  "paymentPriority": "Normal", "preferredPaymentMethod": null,  // AP method code
  "withholdingTaxApplicable": false,
  "earlyPaymentTermUuid": null, "earlyPaymentTermCode": null,
  "paymentTermsDays": 30,             // BusinessPartner.PaymentTermsDays (read-only; the spec's default_payment_term_days)
  "lastPaymentDate": null, "averageDaysToPay": null, "totalOverdueAmount": null, "statisticsUpdatedAt": null,
  "notes": null,
  "createdAt": "…", "modifiedAt": null, "modifiedByName": null,
  "rowVersion": "AAAAAAAAB9E="
}
```

### 1.2 `PUT api/finance/partner-profiles/{partnerUuid}?role=Customer|Vendor` — `FINANCE_AR_AP_ADMIN`

Body (every field optional; absent = unchanged; `rowVersion` optional — when sent and stale → 409):
`riskTier, dunningProcedureUuid (Guid.Empty or "clear": true → default), blockSalesAtDunning, excludeFromDunning,
defaultPaymentMethod, paymentPriority, preferredPaymentMethod, withholdingTaxApplicable, earlyPaymentTermUuid,
clearDunningProcedure, clearEarlyPaymentTerm, notes, rowVersion`.

Not editable here (they have audited AR endpoints, §2/§3): credit hold, temporary limit, dunning level/date, statistics.
A risk-tier change writes a `CreditHoldRequests` row (`RiskTier`, reason `"Changed in partner profile"`).

- 400 `"Dunning procedure with key '{uuid}' was not found."` / `"Dunning procedure {code} is inactive."`
- 400 `"Early payment term with key '{uuid}' was not found."` / `"Early payment term {code} is inactive."`
- 400 `"Unknown payment method '{x}'. Allowed: …"` (AR list for `defaultPaymentMethod`, AP list for `preferredPaymentMethod`).
- 400 `"Notes cannot be longer than 1000 characters."`
- Customer-only fields on a Vendor profile (and vice versa) are accepted and stored (one table) but ignored by the other side.

Returns the model of §1.1.

### 1.3 `GET api/finance/partner-profiles?role=&onHold=&riskTier=&minDunningLevel=&search=&page=&pageSize=`

Existing profile rows only (a partner never opened has none). Permission as §1.1 (role filter required: same 400).
`search` matches partner name/code (resolved through the partner lookup). Sorted by partner name. Items = §1.1 model.

### 1.4 `GET api/finance/ar-ap/settings` / `PUT api/finance/ar-ap/settings`

GET: any A48 code. PUT: `FINANCE_AR_AP_ADMIN`. Body/response: `{ "creditControlMode": "Off" }`.
Stored in `finance.org_settings.CreditControlMode` (row created when missing). 400 for an unknown mode.

---

## 2. Credit control — owner AR (tasks 084–126; D-5)

| Method | Route | Permission | Notes |
|---|---|---|---|
| GET | `api/finance/credit/{customerUuid}/exposure` | AR_VIEW, CREDIT_CHECK, CREDIT_MANAGE | `CreditExposureModel` |
| POST | `api/finance/credit/{customerUuid}/check` | CREDIT_CHECK | body `{ amount, currencyCode? }` → `CreditCheckResultModel` (§10.2 shape) |
| POST | `api/finance/credit/{customerUuid}/hold` | CREDIT_MANAGE | `{ reason }` |
| POST | `api/finance/credit/{customerUuid}/release` | CREDIT_MANAGE | `{ reason }` |
| POST | `api/finance/credit/{customerUuid}/temporary-limit` | CREDIT_MANAGE | `{ amount, expiryDate, reason }`; `amount: null` clears it |
| PUT | `api/finance/credit/{customerUuid}/risk-tier` | CREDIT_MANAGE | `{ riskTier, reason }` |
| GET | `api/finance/credit/{customerUuid}/history?page=` | AR_VIEW, CREDIT_MANAGE | CreditHoldRequests rows, newest first |
| GET | `api/finance/credit/exposures?overLimit=&onHold=&riskTier=&search=&page=` | AR_VIEW, CREDIT_MANAGE | credit-control list (customers with a profile or an open balance) |

`CreditExposureModel`: `customerUuid, customerName, customerCode, baseCurrencyCode, creditLimit, temporaryCreditLimit,
temporaryCreditExpiry, effectiveCreditLimit (null = no limit), openOrdersAmount, openInvoicesAmount, totalExposure,
availableCredit (null when no limit), isOverLimit, isOnHold, riskTier, creditControlMode`.

Exposure (D-5): confirmed-not-fully-invoiced sale orders (Demand `SaleOrders`, read directly — Finance references Demand;
status CONFIRMED / partially delivered / partially invoiced; value = order base total − base value already invoiced) +
open sales-invoice balance (ISSUED / PARTIALLY_PAID / OVERDUE `BalanceDue` in base) − unapplied customer credit (open credit
notes, unallocated receipts) — never below the invoices part. Base currency (D-13). Computed live, never cached.

Texts: 400 `"A reason is required."` · 409 `"{name} is already on credit hold."` · 409 `"{name} is not on credit hold."` ·
400 `"The temporary limit must be greater than zero."` · 400 `"The expiry date must be after today."` ·
400 `"{name} has no permanent credit limit; set one on the customer before granting a temporary one."` ·
400 `"The temporary limit must be above the permanent limit ({limit})."`.

**Sale-order confirm integration (AR edits Demand):** `POST api/sale-orders/{uuid}/confirm` gains an optional body
`{ "creditOverrideReason": "…" }`. With mode `Block` and a failing check: 409
`"Credit check failed: {blockReason} Confirming needs the CREDIT_OVERRIDE permission and an override reason."`; with the
permission + reason the order confirms and an `Override` row is recorded (`ICreditCheck.RecordOverrideAsync`). With mode
`Warn` the confirm result gains `creditWarning` (text) and never blocks. The confirm result gains `creditCheck`
(`CreditCheckResultModel`, null when mode `Off`). Sales-invoice creation: warning only (`creditWarning` on the response).
Hold blocks whenever the mode is not `Off`; the limit blocks only in `Block` (D-CORE-4). Block reason texts:
`"{name} is on credit hold: {reason}."` · `"Exceeds the credit limit of {limit} {ccy} by {excess} {ccy} (exposure {exposure}, this order {amount})."`

---

## 3. Dunning — owner AR (tasks 194–243; D-10, D-11)

| Method | Route | Permission |
|---|---|---|
| GET | `api/finance/dunning/procedures` | AR_VIEW, DUNNING_EXECUTE, DUNNING_ADMIN, FINANCE_AR_AP_ADMIN |
| GET | `api/finance/dunning/procedures/{uuid}` | same |
| POST | `api/finance/dunning/procedures` | FINANCE_AR_AP_ADMIN |
| PUT | `api/finance/dunning/procedures/{uuid}` | FINANCE_AR_AP_ADMIN |
| POST | `api/finance/dunning/runs/preview` | DUNNING_EXECUTE — body `{ asOfDate }`; creates the Draft run with its items |
| POST | `api/finance/dunning/runs/{uuid}/items/{itemUuid}/skip` | DUNNING_EXECUTE — `{ reason }` (Draft only) |
| POST | `api/finance/dunning/runs/{uuid}/execute` | DUNNING_EXECUTE |
| POST | `api/finance/dunning/runs/{uuid}/cancel` | DUNNING_EXECUTE (Draft only) |
| GET | `api/finance/dunning/runs?page=` | AR_VIEW, DUNNING_EXECUTE, DUNNING_ADMIN |
| GET | `api/finance/dunning/runs/{uuid}` | same |
| GET | `api/finance/dunning/customers/{customerUuid}/history` | AR_VIEW, DUNNING_EXECUTE, DUNNING_ADMIN |
| POST | `api/finance/dunning/customers/{customerUuid}/reset` | DUNNING_ADMIN — `{ reason, level? (default 0) }` |

Procedure model: `uuid, code, name, description, isDefault, isActive, graceDays, minimumAmount, blockSalesAtLevel (null =
never), levels[]: { uuid, levelNumber, daysAfterPrevious, subjectTemplate, bodyTemplate, includeInvoiceList,
includeStatement, chargeAmount (base currency; null = none), actionType, escalationNote }`. Levels are replaced as a set
on PUT (1..n contiguous, never skipping — 400 `"Dunning levels must be numbered 1 to {n} without gaps."`).
Placeholders (D-11): `{CustomerName} {CustomerCode} {TotalOverdue} {OldestDueDate} {DunningLevel} {InvoiceList} {Amount} {DueDate}`.

Run rules: one non-cancelled run per organization per `runDate` (unique index; 409
`"A dunning run already exists for {date} ({runNumber})."`); completed runs immutable (409
`"Dunning run {runNumber} is {status}; it can no longer change."`); level progression never skips (BR-DN-004);
the charge is a customer document — a SalesInvoice with `SourceType = "DUNNING_CHARGE"` (AR adds the constant; the column
is nvarchar(20), no CHECK) — posted through the existing receivables path; item stores `chargeDocumentUuid/Number`.
Reset to 0 when the customer's open balance is 0 (AR, at preview time; written as a `DunningReset` audit row).

---

## 4. AR credit memos — owner AR (tasks 607–612 credit side; D-6)

Customer credit notes already exist: a `SalesInvoice` with `Status = "CREDIT_NOTE"` (A38), `OriginalInvoiceUuid`,
`CreditedAmount` on both sides. A48 adds `finance.customer_credit_applications` (§11) to apply a note to **any** open
invoice of the same customer and currency.

| Method | Route | Permission |
|---|---|---|
| POST | `api/finance/ar/credit-memo` | AR_CREDIT_MEMO — `{ customerUuid, originalInvoiceUuid?, date, currencyCode?, reason, lines: [{ description, amount, taxCodeUuid? }] }` → the credit note (an issued CREDIT_NOTE SalesInvoice; ledger + A44 posting as A38's) |
| POST | `api/finance/ar/credit-memo/{creditNoteUuid}/apply` | AR_CREDIT_MEMO — `{ applications: [{ invoiceUuid, amount }] }` |
| GET | `api/finance/ar/credit-memo/{creditNoteUuid}/applications` | AR_VIEW, AR_CREDIT_MEMO |
| GET | `api/finance/ar/customers/{customerUuid}/open-credits` | AR_VIEW, AR_CREDIT_MEMO |

Apply rules: an application row per invoice; both documents' `CreditedAmount` increase, the invoice's `BalanceDue` and
status follow; customer ledger entry as A38's credit-note application. Texts: 400 `"{noteNumber} has only {x} left to
apply."` · 400 `"Invoice {n} has only {x} open."` · 400 `"Invoice {n} belongs to another customer."` · 400
`"{noteNumber} is in {c1}; invoice {n} is in {c2}."` · 409 `"{noteNumber} is not an issued credit note."`.
Remaining credit of a note = |GrandTotal| − CreditedAmount − Σ refunds (AR confirms the sign convention of A38 notes).

---

## 5. Inquiry, aging, statements, vendor reconciliation — owner RPT (D-1, D-12, D-13)

| Method | Route | Permission |
|---|---|---|
| GET | `api/finance/ar/aging?asOfDate=&basis=DueDate\|DocumentDate&buckets=30,60,90,120&customerUuid=&includeDetails=&sortBy=Partner\|TotalOutstanding\|OldestBucket&sortDirection=Asc\|Desc&page=&pageSize=` | AR_VIEW, REPORT_VIEW |
| GET | `api/finance/ar/aging/export?format=pdf\|xlsx&…same filters` | AR_VIEW + REPORT_EXPORT |
| GET | `api/finance/ar/dso?asOfDate=&periodDays=365` | AR_VIEW |
| GET | `api/finance/ar/summary?asOfDate=` | AR_VIEW |
| GET | `api/finance/ar/customers/{uuid}/balance?asOfDate=` | AR_VIEW |
| GET | `api/finance/ar/customers/{uuid}/open-items?asOfDate=&page=` | AR_VIEW |
| GET | `api/finance/ar/customers/{uuid}/transactions?from=&to=&page=` | AR_VIEW |
| GET | `api/finance/ar/customers/{uuid}/statement?from=&to=&type=Activity\|OpenItems&currencyCode=&format=json\|pdf` | AR_VIEW |
| POST | `api/finance/ar/customers/{uuid}/statement/email` | AR_STATEMENT_SEND — `{ from, to, type, emailTo? }` |
| POST | `api/finance/ar/statements/batch` | AR_STATEMENT_SEND — `{ from, to, type, minimumBalance?, sendEmail }` |
| GET | `api/finance/statements/log?role=&partnerUuid=&page=` | AR_VIEW (Customer) / AP_VIEW (Vendor) |
| GET | `api/finance/ap/aging` (same parameters, `vendorUuid`), `/ap/aging/export` | AP_VIEW, REPORT_VIEW |
| GET | `api/finance/ap/dpo`, `api/finance/ap/summary` | AP_VIEW |
| GET | `api/finance/ap/vendors/{uuid}/balance`, `/open-items`, `/transactions`, `/statement` | AP_VIEW |
| POST | `api/finance/ap/vendors/{uuid}/reconciliation` | AP_EXECUTE — `{ asOfDate, vendorClaimedBalance, vendorItems?: [{ reference, date, amount }] }` |

Aging: buckets = boundaries in days (default `30,60,90,120` → `Current, 1-30, 31-60, 61-90, 91-120, 120+`); fewer than 2
boundaries (3 buckets) → 400 `"At least 3 buckets are needed (2 boundaries)."`; boundaries must ascend → 400
`"Bucket boundaries must be positive and ascending."`. Due-date basis: Current = not yet due (D-12). Totals base currency;
detail rows carry document currency + amount + base amount. Existing endpoints (`api/reports/aging-receivables`,
`api/suppliers/{id}/aging`, `api/reports/supplier-aging`) keep working unchanged.
Statements: per currency section (D-13); PDF through `PdfRenderGate` (D-11), generated on request, a `statement_log` row
per generation (and per email: `emailStatus`, `emailedTo`, `emailedAt`).
DSO = AR balance / credit sales in period × days; DPO = AP balance / supplier-invoice purchases in period × days (D-12).

---

## 6. 3-way matching — owner AP (tasks 291–332; D-7, D-CORE-5)

The supplier `Invoice` **is** the match header (D-CORE-5); its new columns (§11.3) hold the A48 match result; per-line
rows go to `finance.invoice_match_lines`. The existing whole-invoice match (`MatchStatus` Pending/Matched/Variance,
5% on Subtotal) keeps running on create/update; A48's per-line match refines it.

| Method | Route | Permission |
|---|---|---|
| POST | `api/finance/matching/invoices/{invoiceUuid}/match` | MATCH_EXECUTE — `{ matchType?: ThreeWay\|TwoWay }` (default ThreeWay when the invoice names a GRN, else TwoWay) |
| GET | `api/finance/matching/invoices/{invoiceUuid}` | AP_VIEW, MATCH_EXECUTE, MATCH_OVERRIDE, MATCH_APPROVE |
| POST | `api/finance/matching/invoices/{invoiceUuid}/override` | MATCH_OVERRIDE — `{ reason }` |
| POST | `api/finance/matching/invoices/{invoiceUuid}/approve` | MATCH_APPROVE — the existing approval path (ledger, A44 posting) |
| GET | `api/finance/matching/pending?vendorUuid=&result=&page=` | AP_VIEW, MATCH_* |
| GET | `api/finance/matching/po/{poUuid}` | AP_VIEW, MATCH_* |
| GET | `api/finance/matching/tolerances` | AP_VIEW, MATCH_*, FINANCE_AR_AP_ADMIN |
| POST/PUT | `api/finance/matching/tolerances`, `…/{uuid}` | FINANCE_AR_AP_ADMIN |

Match detail model: `invoiceUuid, invoiceNumber, supplierInvoiceNo, vendorUuid, vendorName, poUuid, poNumber, grnUuid,
grnNumber, matchStatus (existing), matchType, lineMatchResult, withinTolerance, variancePercent, varianceAmount,
toleranceRule { uuid, name }, autoApproved, matchedAt, matchedByName, overrideReason, overriddenAt, overriddenByName,
lines[]: { lineNo, itemDescription, poLineUuid, grnLineUuid, poQuantity, poUnitPrice, poLineTotal, grnQuantity,
invoiceQuantity, invoiceUnitPrice, invoiceLineTotal, quantityVariance, priceVariance, priceVariancePercent, totalVariance,
lineStatus }`.

Tolerance rule model: `uuid, name, vendorUuid (null = organization rule), vendorName, priceTolerancePercent,
priceToleranceAmount, quantityTolerancePercent, totalTolerancePercent, totalToleranceAmount, autoApproveWithin,
isDefault, isActive`. Rule choice: active vendor rule, else the active default. Texts: 409 `"A tolerance rule named
'{name}' already exists."` · 409 `"{vendor} already has an active tolerance rule ({name})."` · 400 `"Tolerances cannot be
negative."` · 400 `"A percentage cannot exceed 100."` · 409 `"The default tolerance rule cannot be deactivated; make
another rule the default first."`.
Match texts: 400 `"An override needs a reason."` · 409 `"Only an invoice with a match variance can be overridden."` ·
409 `"Invoice {n} has a match variance; override it before approving."` (approve of a PartialMatch/Mismatch not
overridden) · 409 `"Invoice {n} is {matchStatus}; only a pending, matched or variance invoice can be matched."`.
Duplicate vendor invoice number (BR-MT-007) — **service check, no unique index** (D-CORE-6), on invoice create/update:
409 `"Supplier invoice number {no} is already recorded for {supplier} (invoice {n})."` (rejected/reversed/deleted invoices
do not count).

---

## 7. Payment schedules and early-payment terms — owner AP (tasks 351–382; D-8)

| Method | Route | Permission |
|---|---|---|
| GET | `api/finance/payment-schedules?status=&vendorUuid=&dueFrom=&dueTo=&page=` | AP_VIEW, AP_EXECUTE, PAYMENT_RUN_* |
| GET | `api/finance/payment-schedules/upcoming?from=&to=` | same |
| GET | `api/finance/payment-schedules/discounts?asOfDate=&lookAheadDays=7` | same |
| GET | `api/finance/payment-schedules/cash-flow?from=&weeks=8` | same |
| GET | `api/finance/payment-schedules/{uuid}/discount?paymentDate=` | same — `DiscountCalculation` |
| POST | `api/finance/payment-schedules/{uuid}/block` | AP_EXECUTE — `{ reason }` |
| POST | `api/finance/payment-schedules/{uuid}/unblock` | AP_EXECUTE |
| PUT | `api/finance/payment-schedules/{uuid}` | AP_EXECUTE — `{ paymentPriority?, paymentMethod?, rowVersion? }` |
| POST | `api/finance/payment-schedules/{uuid}/early-payment-term` | AP_EXECUTE — `{ earlyPaymentTermUuid }` (null clears) |
| GET | `api/finance/early-payment-terms` | AP_VIEW, FINANCE_AR_AP_ADMIN |
| POST/PUT | `api/finance/early-payment-terms`, `…/{uuid}` | FINANCE_AR_AP_ADMIN |

Schedules: one row per approved supplier invoice (unique `InvoiceId`); the migration backfills approved open invoices
(D-8); AP keeps them in step (D-CORE-8: a `PaymentScheduleSync` run on every schedule read/proposal upserts rows for
approved invoices without one, refreshes `AmountRemaining` from `InvoiceSettlement.AvailableAsync`, and sets
`PartiallyPaid` / `Paid` / `Cancelled` (reversed, rejected, deleted invoice)). Held by a non-final run: `ReservedByRunId`
(claim with the row's `RowVersion` or a conditional `ExecuteUpdate … WHERE ReservedByRunId IS NULL`).
Texts: 409 `"The payment of invoice {n} is already blocked."` · 409 `"... is not blocked."` · 409 `"The payment of invoice
{n} is in payment run {runNumber}; remove it from the run first."` · 409 `"Invoice {n} is {status}; its schedule can no longer
change."`.
Early-payment terms model: `uuid, code, name, discountPercent, discountDays, netDays, isActive`; 400 `"Discount days must be
fewer than net days."` (also the DB CHECK) · 400 `"The discount percent must be greater than 0 and less than 100."` · 409
`"An early payment term with code {code} already exists."`. Discount (BR-EP-001): eligible when payment date ≤ invoice date +
discount days; amount = remaining × percent / 100, 2 dp.

---

## 8. Payment runs — owner AP (tasks 415–475; D-9, D-15)

| Method | Route | Permission |
|---|---|---|
| POST | `api/finance/payment-runs/propose` | PAYMENT_RUN_PROPOSE — `{ paymentDate, dueDateCutoff, paymentMethod?, vendorUuid?, currencyCode?, bankAccount?, includeDiscountItems (true), minPriority?: High\|Normal\|Low }` |
| GET | `api/finance/payment-runs?status=&page=` | AP_VIEW, PAYMENT_RUN_* |
| GET | `api/finance/payment-runs/{uuid}` | same — header + vendor groups (batches) + items |
| POST | `api/finance/payment-runs/{uuid}/items/{itemUuid}/remove` | PAYMENT_RUN_EDIT — `{ reason }` (also `DELETE …/items/{itemUuid}` with the same body) |
| POST | `api/finance/payment-runs/{uuid}/items/{itemUuid}/restore` | PAYMENT_RUN_EDIT |
| PUT | `api/finance/payment-runs/{uuid}/items/{itemUuid}` | PAYMENT_RUN_EDIT — `{ paymentMethod }` |
| POST | `api/finance/payment-runs/{uuid}/review` | PAYMENT_RUN_EDIT — Proposed → Reviewing |
| POST | `api/finance/payment-runs/{uuid}/approve` | PAYMENT_RUN_APPROVE — from Proposed or Reviewing (`/review` optional); 409 when discounts are taken and the GL has no DEFAULT_DISCOUNT_RECEIVED (D-AP-12) |
| POST | `api/finance/payment-runs/{uuid}/execute` | PAYMENT_RUN_EXECUTE — **200** with the run `Completed` when ≤ 20 batches are pending (executed in the request); **202 Accepted** with the run `Executing` otherwise (background job — poll `GET payment-runs/{uuid}` until `Completed`) (D-AP-13) |
| POST | `api/finance/payment-runs/{uuid}/resume` | PAYMENT_RUN_EXECUTE — a run left `Executing` carries on with its Pending batches (Paid / Failed skipped); same 200 / 202 rule; 409 unless Executing |
| GET | `api/finance/payment-runs/{uuid}/export?format=csv` | PAYMENT_RUN_EXECUTE — CSV file (D-15), generated on request |
| POST | `api/finance/payment-runs/{uuid}/cancel` | PAYMENT_RUN_ADMIN — `{ reason }`; also an `Executing` run with no Paid batch (D-AP-13) |

Execution (D-9, D-AP-8, D-AP-13): per batch (vendor × currency × method) one `SupplierPayment` through the supplier-payment
repository (create → approve → post, in the batch's own transaction; the A44 posting is published after its commit) allocated to the batch's invoices — ledger, exchange differences, A44 BillPayment posting as
today; batch-level transaction; a failed batch → `Failed` (+ `errorMessage`), items `Failed`, schedules unlocked; others
continue; run `Completed` with `failedBatchCount`. Run numbers `PR-…` from `IDocumentNumberGenerator`. Bank details for
the CSV from `IPartnerLookupService.GetBankDetailsAsync` at export time — never stored (only `bankDetailsMasked` on the
batch). Early discount taken (AP-1): the payment line carries `DiscountAmount`; `AllocatedAmount` stays the gross amount
settled on the invoice (PaidAmount += it, so the invoice ends fully paid) and the payment's `TotalAmount` is the cash =
Σ(AllocatedAmount − DiscountAmount). Old lines are 0 → unchanged behaviour. The discount's GL/QuickBooks effect is AP's to
add to the BillPayment posting (R-CORE-3).
Texts: 409 `"Payment run {runNumber} is {status}; it can no longer change."` · 409 `"Only an approved payment run can be
executed."` · 409 `"Only a draft, proposed, reviewing or approved payment run can be cancelled."` · 400 `"Nothing is due
by {cutoff}."` (proposal found no item) · 400 `"A removal needs a reason."`.

---

## 9. AP debit memos — owner AP (tasks 607–612 debit side; D-6)

| Method | Route | Permission |
|---|---|---|
| POST | `api/finance/ap/debit-memo` | AP_DEBIT_MEMO — `{ vendorUuid, originalInvoiceUuid?, date, reason (a DebitReason code), reasonDetail?, amount, notes? }` → a `DebitNote` with `Origin = "MEMO"`, `SroUuid = Guid.Empty`, `SroNumber = ""` |
| POST | `api/finance/ap/debit-memo/{debitNoteUuid}/apply` | AP_DEBIT_MEMO — `{ applications: [{ invoiceUuid, amount }] }` |
| GET | `api/finance/ap/debit-memo/{debitNoteUuid}/applications` | AP_VIEW, AP_DEBIT_MEMO |
| GET | `api/finance/ap/debit-memo/{debitNoteUuid}` | AP_VIEW, AP_DEBIT_MEMO — `ApDebitMemoModel` (§8.1; memo or SRO note, `remainingAmount`, `applications[]`) (FE-8) |
| GET | `api/finance/ap/vendors/{vendorUuid}/debit-memos?openOnly=&page=&pageSize=` | AP_VIEW, AP_DEBIT_MEMO — paged `ApDebitMemoModel`, newest first; `openOnly=true` = carried forward, live status, amount left (FE-8) |

Apply: one `supplier_note_applications` row per invoice; the invoice is reduced the way existing notes reduce it
(`SupplierNotes.DeductAsync`: `TotalAmount −= amount`, payment status re-derived) under the existing lock order; the
ledger / A44 effect stays on the note's own posting (D-6). Texts mirror §4.

---

## 10. In-process interfaces and seams — owner CORE

### 10.1 `IPartnerLookupService` (SMS.Shared.Common; implemented by Suppliers, scoped, per-request cache) — D-3

```csharp
public static class PartnerRoles { public const string Customer = "Customer", Vendor = "Vendor"; }
public sealed record PartnerInfo(Guid Uuid, string Code, string Name, bool IsCustomer, bool IsVendor, bool IsActive,
    string? Email, string? Phone, string? Address, string? ContactName, string? ContactEmail,
    decimal? CreditLimit, int PaymentTermsDays, string? CustomerType);
public sealed record PartnerBankInfo(Guid Uuid, string? BankName, string? AccountNumber, string? Iban, string? Swift);
public interface IPartnerLookupService
{
    // role: null = any, "Customer" / "Vendor" = only partners carrying that flag; anything else → ArgumentException.
    Task<PartnerInfo?> GetAsync(Guid organizationId, Guid partnerUuid, string? role = null, CancellationToken ct = default);
    // One query per call for the uuids not cached yet (chunks of 1000). Missing / other-org / deleted → absent from the dictionary.
    Task<IReadOnlyDictionary<Guid, PartnerInfo>> GetManyAsync(Guid organizationId, IEnumerable<Guid> partnerUuids,
        string? role = null, CancellationToken ct = default);
    // Partners whose name or code contains the text (max `limit`), for list filters.
    Task<IReadOnlyList<PartnerInfo>> SearchAsync(Guid organizationId, string search, string? role = null, int limit = 200,
        CancellationToken ct = default);
    // Decrypted bank details — for the payment file only; never persist or log them.
    Task<PartnerBankInfo?> GetBankDetailsAsync(Guid organizationId, Guid partnerUuid, CancellationToken ct = default);
}
```
`Email` = partner email, else the primary contact's. `Address` = AddressLine1, AddressLine2, City, ProvinceState,
PostalCode, Country joined with ", " (blanks skipped). Resolve it with `GetService` where a host may lack Suppliers.

### 10.2 `ICreditCheck` (SMS.Shared.Common; implemented by Finance — AR) — D-5

```csharp
public static class CreditControlModes { public const string Off = "Off", Warn = "Warn", Block = "Block"; }
public sealed record CreditCheckRequest(Guid OrganizationId, Guid CustomerUuid, decimal Amount, string? CurrencyCode,
    decimal? AmountBase, string DocumentType /* "SALE_ORDER" | "SALES_INVOICE" | "MANUAL" */, Guid? DocumentUuid = null,
    string? DocumentNumber = null);
public sealed record CreditCheckResult(string Mode, bool Approved, string? BlockReason, string? Warning, bool OnHold,
    decimal? EffectiveLimit, decimal OpenOrdersAmount, decimal OpenInvoicesAmount, decimal Exposure,
    decimal RequestedAmount, decimal ProjectedExposure, decimal? AvailableCredit, string? BaseCurrencyCode);
public interface ICreditCheck
{
    Task<CreditCheckResult> CheckAsync(CreditCheckRequest request, CancellationToken ct = default);
    Task RecordOverrideAsync(CreditCheckRequest request, CreditCheckResult result, string reason, int? userId,
        string? userName, CancellationToken ct = default);
    // Per-customer serialization of check + confirm (sp_getapplock, session owner, own connection). Dispose releases.
    Task<IAsyncDisposable> LockCustomerAsync(Guid organizationId, Guid customerUuid, CancellationToken ct = default);
}
```
Mode `Off` → `Approved = true`, nothing computed (amounts 0) — the fast path every existing org takes. Demand resolves it
with `GetService<ICreditCheck>()` (absent → no check). The SO's `AmountBase` = its `GrandTotalBase` when locked.

### 10.3 Finance-internal seams (namespace `SMS.Modules.Finance.ArAp`, `internal`, same assembly)

| Seam | Use |
|---|---|
| `FinanceDbContext` DbSets (§11) | everyone; CORE alone edits the context, entities and maps |
| `IPartnerFinancialProfileService` (internal; file `ArAp/Core/PartnerFinancialProfileService.cs`) | `GetOrCreateAsync(org, partnerUuid, role, ct)` → the **tracked** entity (created with defaults when missing; a concurrent create is absorbed; the partner is **not** looked up — a document's partner may since be deleted); `GetCreditControlModeAsync(org, ct)` (Off when no row). AR writes hold / temp-limit / dunning fields on that tracked entity and saves in its own transaction. Plus the public `IPartnerProfileEndpoints` part (`GetModelAsync`, `UpdateAsync`, `ListAsync`, `GetSettingsAsync`, `UpdateSettingsAsync`) the CORE controllers use. |
| `CreditLimits` (same file) | `Effective(permanent, temporary, expiry, today)` (null = no limit, D-CORE-3) and `TemporaryActive(...)` — use them so every screen agrees |
| `ArApCaller` (**public**, `ArAp/Core/ArApCaller.cs`) | `await ArApCaller.FromAsync(User, ITenantContext, IOrgUserDirectory?, ct)` → `OrganizationId, UserId, UserIdOrNull, UserName, Has(code), HasAny(...), Require(bool, msg), RequireAny(...)`; `ArApCaller.System(org)` for jobs. Controllers must be public with public constructors, so a controller-facing service interface may only use public types (pattern: public `I…Endpoints` + internal interface deriving from it). |
| `ArApDefaultsSeeder` | `EnsureAsync(org, ct)` — STANDARD dunning procedure + DEFAULT tolerance rule if no default exists (idempotent, app lock per org); also an `IOrganizationProvisionedHandler`. AR/AP call it before relying on a default. Seed texts in `ArApSeedData`. |
| `ArApRegistration` | `static partial class ArApRegistration` (namespace `SMS.Modules.Finance.ArAp`) with `static partial void AddAr(IServiceCollection services)`, `AddRpt(...)`, `AddAp(...)`. Each agent implements **its own** partial in its own file `ArAp/ArApRegistration.{Ar,Rpt,Ap}.cs`; CORE's `AddArApSubledgers()` (called from `IFinanceModule`) calls all three. Nobody else edits `IFinanceModule.cs`. |
| `ArApEnums` (namespace `SMS.Modules.Finance.ArAp`, file `ArAp/Core/ArApEnums.cs`) | the §0.1 constants (`ArApEnums.ScheduleStatus.Open`, …) + `All` lists, `PaymentPriority.Rank`, `ApPaymentMethods`, `Canonical(value, all)`, `UnknownText(field, value, all)` — use them, not literals |
| Existing | `InvoiceSettlement.AvailableAsync` (what is still owed on a supplier invoice), `SupplierNotes` (CreditNoteRepository: lock order + deduct), `DocumentNumbers.SerializeAsync`, `ISupplierPaymentService` (create/approve/post), `ICustomerLedgerService`, `SalesInvoiceService` internals (A38 credit-note booking), `ICurrencyService` / `IOrgCurrencyLookup` (base currency, conversion), `PdfRenderGate` (Reports — RPT requests a shared copy if Finance cannot reach it), `IDocumentNumberGenerator`, `IOrgUserDirectory`. |

Edits to existing code (each file has one owner): Demand `SaleOrderService` confirm + `SaleOrdersController` — **AR**;
Finance `SalesInvoiceService` (credit warning) — **AR**; Finance `FinanceRepository` invoice create/update/approve (duplicate
check, approval gate) — **AP**; `SupplierPaymentRepository` — **AP** only if unavoidable. Anything else: ask in
`SCHEMA-REQUESTS.md` first.

---

## 11. Schema reference — owner CORE (migration `A48_ArApSubledgers`, guarded/idempotent, D-17)

All tables in schema `finance`, `Id int IDENTITY` PK + `Uuid uniqueidentifier` unique, `OrganizationId uniqueidentifier`
(tenant filter + index). Money decimal(18,2) (D-CORE-2), percents decimal(9,4), quantities/unit prices decimal(18,4).
Statuses nvarchar + CHECK `CK_{table}_{Column}`.

| Table | Key columns |
|---|---|
Entities: `src/SMS.Modules.Finance/Domain/ArApEntities.cs` (C# names = column names); maps `Data/Maps/ArApMaps.cs`;
DbSets on `FinanceDbContext` named after the entity in plural (`PartnerFinancialProfiles`, `CreditHoldRequests`,
`StatementLogs`, `DunningProcedures`, `DunningLevels`, `DunningRuns`, `DunningRunItems`, `MatchToleranceRules`,
`InvoiceMatchLines`, `EarlyPaymentTerms`, `PaymentSchedules`, `PaymentRuns`, `PaymentRunBatches`, `PaymentRunItems`,
`CustomerCreditApplications`, `SupplierNoteApplications`). Dates `date` (DateOnly) where a business date, `datetime2` UTC for
timestamps. FKs between A48 tables are Restrict / NoAction unless noted.

| Table (entity) | Columns (beyond Id, Uuid, OrganizationId) |
|---|---|
| `partner_financial_profiles` (PartnerFinancialProfile) | PartnerUuid, PartnerRole; CreditHold, CreditHoldReason(500), CreditHoldAt, CreditHoldBy, CreditHoldByName; TemporaryCreditLimit (CHECK > 0), TemporaryCreditExpiry (date); RiskTier (default Medium); DunningProcedureId → dunning_procedures, CurrentDunningLevel (≥ 0), LastDunningDate (date), BlockSalesAtDunning, ExcludeFromDunning; DefaultPaymentMethod(30); PaymentPriority (default Normal), PreferredPaymentMethod(30), WithholdingTaxApplicable, EarlyPaymentTermId → early_payment_terms; LastPaymentDate (date), AverageDaysToPay, TotalOverdueAmount, StatisticsUpdatedAt; Notes(1000); CreatedAt/By/ByName, ModifiedAt/By/ByName; **RowVersion**. Unique (OrganizationId, PartnerUuid, PartnerRole); IX (OrganizationId, PartnerRole) INCLUDE (CreditHold, RiskTier); filtered IX on hold. |
| `credit_hold_requests` (CreditHoldRequest) | ProfileId → profiles, PartnerUuid, PartnerName, Action, Reason(500, required), PreviousValue(200), NewValue(200), Amount, ExpiryDate (date), SourceDocumentType(30) / Uuid / Number(50), RequestedBy/ByName/At, ApprovedBy/ByName/At, Status (default Completed). IX (OrganizationId, PartnerUuid, RequestedAt). |
| `statement_log` (StatementLog) | PartnerUuid, PartnerName, PartnerRole, StatementType, FromDate, ToDate (CHECK To ≥ From), CurrencyCode (null = all), OpeningBalance, ClosingBalance, LineCount, Format (PDF/JSON, default PDF), EmailStatus (default NotSent), EmailedTo(500), EmailedAt, EmailError(500), BatchUuid, GeneratedAt/By/ByName. |
| `dunning_procedures` (DunningProcedure) | Code(20, unique per org), Name(100), Description(500), IsDefault (filtered unique: one per org), IsActive, GraceDays (≥ 0), MinimumAmount (≥ 0, base), BlockSalesAtLevel (null = never, ≥ 1), audit. `Levels` navigation. |
| `dunning_levels` (DunningLevel) | DunningProcedureId (cascade), LevelNumber (≥ 1; unique per procedure), DaysAfterPrevious (≥ 0), SubjectTemplate(200), BodyTemplate (max), IncludeInvoiceList (default 1), IncludeStatement, ChargeAmount (null/≥ 0, base), ActionType (default Email), EscalationNote(500). |
| `dunning_runs` (DunningRun) | RunNumber(30, unique per org), RunDate (date; filtered unique (OrganizationId, RunDate) WHERE Status ≠ Cancelled), AsOfDate, Status (default Draft), CurrencyCode, TotalCustomers, TotalAmount, ItemsProcessed/Sent/Skipped/Failed, CreatedAt/By/ByName, ExecutedAt/By/ByName, CompletedAt, CancelledAt/By/ByName, CancelReason(500). `Items` (cascade). |
| `dunning_run_items` (DunningRunItem) | DunningRunId, PartnerUuid, PartnerName(200), PartnerCode(50), DunningProcedureId (no FK), DunningLevel, PreviousLevel, OverdueAmount (base), CurrencySummary(500), OpenItemCount, OldestDueDate, DaysOverdue, ActionType, ChargeAmount, ChargeDocumentUuid, ChargeDocumentNumber(30), SentTo(500), SentAt, Subject(200), Body (max), InvoiceUuids (JSON, max), Status (default Pending), SkipReason(200), FailureReason(1000). |
| `match_tolerance_rules` (MatchToleranceRule) | Name(100, unique per org), VendorUuid (null = org rule; filtered unique per vendor where active), VendorName, PriceTolerancePercent, PriceToleranceAmount, QuantityTolerancePercent, TotalTolerancePercent, TotalToleranceAmount (all ≥ 0, percents ≤ 100), AutoApproveWithin, IsDefault (filtered unique), IsActive, audit. |
| `invoices` (+Invoice columns) | ToleranceRuleId → match_tolerance_rules, MatchType (null/ThreeWay/TwoWay), LineMatchResult (null/Matched/PartialMatch/Mismatch), MatchVariancePercent, WithinTolerance (bit null), AutoApproved (default 0), MatchedAt, MatchedBy, MatchedByName, MatchOverrideReason(500), MatchOverriddenAt, MatchOverriddenBy, MatchOverriddenByName; `MatchLines` navigation; IX_invoices_VendorInvoiceNo (OrganizationId, SupplierId, SupplierInvoiceNo), **not unique** (D-CORE-6). |
| `invoice_match_lines` (InvoiceMatchLine) | InvoiceId → invoices (cascade), InvoiceLineUuid, PoLineUuid, GrnLineUuid, LineNo, ItemDescription(300), PoQuantity, PoUnitPrice, PoLineTotal, GrnQuantity, InvoiceQuantity, InvoiceUnitPrice, InvoiceLineTotal, QuantityVariance, PriceVariance, PriceVariancePercent, TotalVariance, LineStatus (default Pending), Note(300), CreatedAt. Re-matching = delete the invoice's rows and insert new ones. |
| `early_payment_terms` (EarlyPaymentTerm) | Code(20, unique per org), Name(100), DiscountPercent (> 0, < 100), DiscountDays (≥ 0, < NetDays), NetDays, IsActive, audit. |
| `payment_schedules` (PaymentSchedule) | InvoiceId → invoices (**unique**), InvoiceUuid, InvoiceNumber(20), SupplierInvoiceNo(50), VendorUuid, VendorName, CurrencyCode, InvoiceDate, DueDate, OriginalAmount, AmountRemaining, PaymentPriority (default Normal), PaymentMethod(30), PaymentBlock, BlockReason(200), BlockedBy/ByName/At, EarlyPaymentTermId →, DiscountDate, DiscountPercent, DiscountAmount, ReservedByRunId → payment_runs, PaidInRunId → payment_runs, Status (default Open), CreatedAt, ModifiedAt, **RowVersion**. |
| `payment_runs` (PaymentRun) | RunNumber(30, unique per org), RunDate, PaymentDate, DueDateCutoff, Status (default Draft), SelectionCriteria (JSON), PaymentMethod, BankAccount(100), IncludeDiscount (default 1), BaseCurrencyCode, TotalVendors, TotalItems, TotalAmount, TotalDiscount, NetPaymentAmount, FailedBatchCount, CreatedAt, Proposed/Approved/Executed/Cancelled By + ByName + At, CompletedAt, CancelReason(500), PaymentFileName(200), ExportedAt, **RowVersion**. `Batches`, `Items` (cascade). |
| `payment_run_batches` (PaymentRunBatch) | PaymentRunId, VendorUuid, VendorName, CurrencyCode, PaymentMethod (unique (run, vendor, currency, method)), BankDetailsMasked(100), ItemCount, TotalAmount, TotalDiscount, NetAmount, PaymentReference(100), SupplierPaymentUuid, SupplierPaymentNumber(20), Status (default Pending), FailureReason(1000), ExecutedAt. |
| `payment_run_items` (PaymentRunItem) | PaymentRunId, BatchId → batches (NoAction), PaymentScheduleId → schedules (unique per run), InvoiceUuid, InvoiceNumber, VendorUuid, VendorName, CurrencyCode, DueDate, OriginalAmount, DiscountAmount, NetPaymentAmount, PaymentMethod, Status (default Proposed), RemovedReason(200), RemovedBy/ByName/At, PaidAt. |
| `customer_credit_applications` (CustomerCreditApplication) | CreditNoteId → sales_invoices, CreditNoteUuid, CreditNoteNumber(30), InvoiceId → sales_invoices (CHECK ≠ CreditNoteId), InvoiceUuid, InvoiceNumber(30), PartnerUuid, Amount (> 0), Notes(300), AppliedAt/By/ByName, ReversedAt/By, ReversalReason(500). |
| `supplier_note_applications` (SupplierNoteApplication) | NoteType (DebitNote/CreditNote), NoteUuid, NoteNumber(20), InvoiceId → invoices, InvoiceUuid, InvoiceNumber(20), VendorUuid, Amount (> 0), Notes(300), AppliedAt/By/ByName, ReversedAt/By, ReversalReason(500). |
| `supplier_payment_lines` (+SupplierPaymentLine.DiscountAmount) | decimal(18,2) NOT NULL default 0 (AP-1, §8). |
| `debit_notes` (+DebitNote.Origin) | nvarchar(10) NOT NULL default `SRO`, CHECK SRO/MEMO. |
| `org_settings` (+FinanceOrgSettings.CreditControlMode) | nvarchar(10) NOT NULL default `Off`, CHECK Off/Warn/Block. |

Seeds (D-16) per existing organization (from `tenant.Organizations`, when that table exists) and on provisioning / first
use (`ArApDefaultsSeeder`): `STANDARD` dunning procedure (default, 4 levels at 0 / 14 / 14 / 14 days, Email, no charges,
block-sales level 4 — effective only for profiles with `BlockSalesAtDunning`), `DEFAULT` tolerance rule (default, price 2%,
amount 0, quantity 0%, total 0 / 0, auto-approve off). Backfill (D-8): a `payment_schedules` row for every approved,
undeleted, not reversed supplier invoice still owing (TotalAmount − PaidAmount − legacy payments > 0).

---

## Changes

| Date | Asked by | Change |
|---|---|---|
| 2026-10-25 | CORE | First version. |
| 2026-10-25 | AP, AR (SCHEMA-REQUESTS) | Folded in: payment-line `DiscountAmount` (AP-1, §8); schedule `ReservedByRunId` + `SupplierInvoiceNo` + RowVersion (AP-2); batch `CurrencyCode`, `SupplierPaymentUuid/Number`, `FailureReason`, run `FailedBatchCount` (AP-3); vendor tolerance rules (AP-4); per-line match on the invoice header — **no** InvoiceMatchHeaders table (AP-5 answered, D-CORE-5); one `supplier_note_applications` table for debit and credit notes (AP-6); bank details through the partner lookup (AP-8); credit note applications with denormalised uuids/numbers (AR-1); `org_settings.CreditControlMode` (AR-2); credit actions `DunningHold`, `DunningReset`, `PreviousValue`/`NewValue`, `PartnerName` (AR-3); dunning run `AsOfDate`, item `PartnerCode`, `CurrencySummary`, `DaysOverdue`, `Subject`, `Body`, `InvoiceUuids`, `FailureReason`, status `Manual` (AR-4); lookup fields (AR-5). §10.3: `GetOrCreateAsync(org, uuid, role, ct)` does not look the partner up; `ArApCaller` public; `ArApEnums` in `SMS.Modules.Finance.ArAp`; `CreditLimits` helper. §11 rewritten with the final columns. |
| 2026-10-25 | CORE | Finance test project gets `global using SMS.Modules.Finance.ArAp;` (`tests/SMS.Modules.Finance.Tests/ArApCore/ArApGlobalUsings.cs`) — `ArApEnums` is **not** in `SMS.Modules.Finance.Domain`; src files outside `SMS.Modules.Finance.ArAp.*` need `using SMS.Modules.Finance.ArAp;`. A model snapshot test (`A48MigrationTests.The_model_snapshot_matches…`) fails when a map changes without a migration — ask CORE. |
| 2026-10-25 | AR | §2: `creditCheck` / `creditWarning` on the confirm result, `SalesInvoiceCreated.creditWarning`; block-reason texts carry the base code when known (`… of 1,000.00 PKR by …`). §3: preview items also list customers below the minimum / excluded as `Skipped`; run model adds `autoResetCount`; charge document number `SDC-yyyyMMdd-NNNN`, `SourceType DUNNING_CHARGE`, booked as an INVOICE ledger debit. §4 (additive): request `applyToOriginal` (default true) and line `invoiceLineUuid`; `GET api/finance/ar/credit-memo/{creditNoteUuid}`; memo `SourceType AR_CREDIT_MEMO`, number `SCN-…`. **Every A48 application writes a `customer_credit_applications` row, also to the memo's own original invoice; no ledger entry on apply. RPT as-of credit on invoice i = Σ live rows targeting i + Σ over notes with OriginalInvoiceUuid = i of (note.CreditedAmount − Σ that note's rows)** (D-AR-12). A38 notes have a positive GrandTotal. |
| 2026-10-25 | AR (QA-1) | `ArApDefaultsSeeder.EnsureAsync` fixed for the retrying execution strategy (D-CORE-14); same signature, safe to call on every first access and concurrently. |
| 2026-10-25 | RPT | §5 as built (D-RPT-1..9): aging response `AgingReport` { reportType, asOfDate, basis, bucketDays (the caller's boundaries), bucketLabels, baseCurrencyCode, lines[] { partnerId, partnerName, partnerCode, bucketAmounts[], totalOutstanding, totalOverdue, unappliedCredits, netBalance, openItemCount, oldestOpenDate, daysBeyondTerms, oldestBucket, details[]? }, totals, byCurrency[], dsoOrDpo, page/pageSize/totalRecords/totalPages }; a leading 0 in `buckets` is accepted; extra query params `currencyCode`, sortBy also `TotalOverdue`. **Added** `GET api/finance/ar/aging/details` / `ap/aging/details` (`bucketIndex` 0-based, `customerUuid` / `vendorUuid`, paged `AgingDetailLine` with `rateMissing`) — AR_VIEW/AP_VIEW, REPORT_VIEW. dso/dpo/summary take `periodDays` (default 365). open-items / transactions return `PaginatedResponse` (open items signed, credits negative; transactions per-currency `runningBalance`); statement query also `includeAging`, `buckets`; email body field `emailTo` (+ optional `subject`, `message`, `currencyCode`); batch body also `partnerIds`, `includeAging`; reconciliation body also `currencyCode`, `tolerance` (default 0.01). Export `format` default pdf. |
| 2026-10-25 | RPT (RPT-1, RPT-2) | Two indexes on existing tables in the A48 migration: `IX_customer_ledger_OrganizationId_ReferenceId` (OrganizationId, ReferenceId) INCLUDE (EntryType, ReferenceType, EntryDate) and `IX_invoices_ApAging` (OrganizationId, MatchStatus, SupplierId) INCLUDE (TotalAmount, PaidAmount, DueDate, InvoiceDate, ApprovedAt, ReversedAt, Currency, ExchangeRate, IsDelete). **Superseded by D-CORE-16:** the INCLUDE as built is (TotalAmount, PaidAmount, DueDate, InvoiceDate, ApprovedAt, Currency, IsDelete) — ReversedAt and ExchangeRate left out (REV note). |
| 2026-10-25 | AR (FE-3) | §2: a credit-blocked sale-order confirm stays a 409 with the same message; its body `result` is now `{ creditCheck: CreditCheckResult (§10.2), exceptionMessage }` (Demand `CreditCheckBlockedException`, a ConflictException, caught in `SaleOrdersController.Confirm`). |
| 2026-10-25 | RPT (FE-1/4/5) | §5 response models, exact (camelCase; dates `yyyy-MM-dd`, timestamps ISO UTC; partner fields are **flat** — `partnerId` (= the BusinessPartner uuid), `partnerName`, `partnerCode`; no nested partner object). **AgingReport** `{ reportType: "Receivable"\|"Payable", asOfDate, basis: "DueDate"\|"DocumentDate", bucketDays: int[] (caller's boundaries), bucketLabels: string[] (bucketDays.length + 2), baseCurrencyCode, lines: AgingPartnerLine[] (this page), totals: AgingTotals, byCurrency: [{ currencyCode, bucketAmounts[], total, totalBase, openItemCount }], dsoOrDpo, totalOutstanding, page, pageSize, totalRecords (partners), totalPages, generatedAt }`. **AgingPartnerLine** `{ partnerId, partnerName, partnerCode, bucketAmounts[] (base), totalOutstanding, totalOverdue, unappliedCredits, netBalance, openItemCount, oldestOpenDate, daysBeyondTerms, oldestBucket (-1 none), details: AgingDetailLine[] \| null }` — no `onHold` (credit hold is AR's: `GET credit/exposures?onHold=`). **AgingTotals** `{ bucketTotals[], grandTotal, totalOverdue, unappliedCredits, netBalance, totalPartners, totalOpenItems }` — **FE-4: totals and byCurrency are over every partner and document matching the filters, independent of page/pageSize; only `lines` is paged.** **AgingDetailLine** (aging/details → `PaginatedResponse<AgingDetailLine>`) `{ documentType, documentUuid, documentNumber, reference, partnerId, partnerName, documentDate, dueDate, currencyCode, originalAmount, openAmount, openAmountBase, exchangeRate, daysOverdue, ageDays, bucketIndex, bucketLabel, status, rateMissing }`. **DaysOutstandingResult** `{ measure: "DSO"\|"DPO", asOfDate, periodDays, baseCurrencyCode, balance, periodAmount, days }`. **PartnerBalanceResult** `{ partnerId, partnerName, partnerRole, asOfDate, baseCurrencyCode, totalOpen, totalCredits, netBalance, totalOverdue, openItemCount, oldestOpenDate, daysOldest, byCurrency: [{ currencyCode, totalOpen, totalCredits, netBalance, netBalanceBase }] }`. **OpenItemDto** (`PaginatedResponse`) `{ documentType, documentUuid, documentNumber, reference, documentDate, dueDate, currencyCode, originalAmount, openAmount (signed, credits negative), openAmountBase, exchangeRate, daysOutstanding, daysOverdue, isOverdue, isCredit, status, rateMissing }`. **PartnerTransactionDto** (`PaginatedResponse`) `{ sequenceNo, entryDate (timestamp), entryType, referenceType, referenceId, referenceNumber, narration, currencyCode, debit, credit, runningBalance }`. **ArSummaryKpis** `{ asOfDate, baseCurrencyCode, periodDays, totalOutstanding, totalOverdue, totalCurrent, unappliedCredits, netReceivable, totalCustomersWithBalance, totalCustomersOverdue, dso, daysBeyondTerms, collectionRate (%), invoicedInPeriod, collectedInPeriod, agingBuckets[], agingLabels[] }`. **ApSummaryKpis** `{ asOfDate, baseCurrencyCode, periodDays, totalOutstanding, totalOverdue, totalCurrent, totalDueThisWeek, totalDueThisMonth, unappliedCredits, netPayable, totalVendorsWithBalance, totalVendorsOverdue, dpo, discountOpportunityAmount, agingBuckets[], agingLabels[] }` — no `customersOnHold` (AR's exposures list), `pendingMatches` (AP's `matching/pending` totalRecords) or trends (call twice with two `asOfDate`s). **StatementResult** `{ statementId (statement_log uuid), partnerType, partnerId, partnerName, partnerCode, partnerEmail, partnerAddress, companyName, fromDate, toDate, type, baseCurrencyCode, sections: [{ currencyCode, openingBalance, totalDebits, totalCredits, closingBalance, lines: [{ date, documentType, entryNumber, reference, description, dueDate, debit, credit, runningBalance, status }], aging: decimal[] \| null }], openingBalanceBase, closingBalanceBase, lineCount, agingLabels[], generatedAt }`. **StatementEmailResult** `{ statementId, emailedTo, emailedAt, closingBalanceBase }`. **BatchStatementResult** `{ considered, generated, emailed, skipped, noEmail, failed, items: [{ partnerId, partnerName, statementId, closingBalanceBase, emailed, emailedTo, outcome: GENERATED\|EMAILED\|NO_EMAIL\|FAILED, error }] }`. **StatementLogModel** `{ uuid, partnerUuid, partnerName, partnerRole, statementType, fromDate, toDate, currencyCode (null = several), openingBalance, closingBalance, lineCount, format, emailStatus, emailedTo, emailedAt, emailError, batchUuid, generatedAt, generatedByName }`. **VendorReconciliationResult** `{ vendorId, vendorName, asOfDate, currencyCode, smsBalance, vendorClaimedBalance, difference, matchedCount, differences: [{ type, documentType, entryNumber, reference, date, smsAmount, vendorAmount, difference }], isReconciled }`. **FE-5:** `statement/email` already takes optional `message` (HTML-encoded into the body) and `subject`. |

| 2026-10-25 | AP (coordinator, round 2) | **§8 execution is asynchronous for big runs (D-AP-13).** `POST payment-runs/{uuid}/execute` claims Approved → Executing once (second call 409 `"Only an approved payment run can be executed."`), then: ≤ 20 Pending batches → executed in the request, **200** with the run `Completed`; more → a Hangfire job (`PaymentRunExecutionJob`, organization passed explicitly into `HangfireTenantScope`) executes it and the call answers **202 Accepted** with the run detail in `Executing` — the UI polls `GET payment-runs/{uuid}` until `run.status` is `Completed` (batches turn `Paid` / `Failed` as they go). New **`POST payment-runs/{uuid}/resume`** (PAYMENT_RUN_EXECUTE, MODULE_FINANCE): a run stuck in `Executing` (crash, stopped job) carries on — batches already `Paid` / `Failed` are skipped, `Pending` ones executed; same 200 / 202 rule; 409 `"Only an executing payment run can be resumed."`. Execution is idempotent per batch (each batch is claimed under its row lock, after the run's row lock), so a resume racing a running job never pays a batch twice. **Cancel** (PAYMENT_RUN_ADMIN) now also accepts an `Executing` run with no `Paid` batch: its Pending batches become `Failed` ("Payment run cancelled before this batch was paid."), schedules released; with a paid batch → 409 `"Payment run {n} has paid batches; resume it instead of cancelling it."` |
| 2026-10-25 | AP (coordinator, round 2) | **Discount account gate (D-AP-12).** Run detail gains `warnings: string[]`. When a run takes early-payment discounts and an **active** posting target needs `DEFAULT_DISCOUNT_RECEIVED` but cannot resolve it (the internal GL with the default unassigned; QuickBooks posts the payload and never needs it), the proposal/detail carries the warning and `approve` (and `execute`) answer 409 `"This run takes early-payment discounts, but the general ledger has no account for DEFAULT_DISCOUNT_RECEIVED (Discount Received). Assign it in Accounting → Defaults, or remove the discounted items (or propose without discounts), before approving."` New Shared seam `IPostingRoleReadiness.MissingRolesAsync(roles)` (SMS.Shared.Finance.Posting; Accounting implements it for the internal GL, `NullPostingRoleReadiness` elsewhere). |
| 2026-10-25 | AP (FE-2, FE-6, FE-7) | **FE-2:** `approve` is accepted straight from `Proposed` — `/review` (Proposed → Reviewing) is optional. **FE-6:** new `POST matching/invoices/{invoiceUuid}/reject` `{ reason }` (MATCH_APPROVE, MODULE_FINANCE) — the existing supplier-invoice rejection (`MatchStatus = Rejected`, reason ≤ 300 in Notes; 400 `"Say why the invoice is being rejected."`; 409 when approved/reversed/rejected or payments exist, the existing texts); returns the match detail. **FE-7:** `GET matching/pending?search=` — contains, case-insensitive, over our invoice number, the supplier's invoice number, the PO number and the supplier name. |
| 2026-10-25 | AP (FE-1) | §6–§8 response models published below (**§8.1**). |
| 2026-10-25 | AP (QA-2, QA-4) | §8 table itself now states execute/resume 200 vs 202, resume, cancel of a stuck run, approve from Proposed. Supplier-invoice numbers (INV-YYYY-NNNNN, unchanged format) are taken under a per-organization numbering app lock in the insert's transaction, first free number — concurrent creates no longer 500 (D-AP-15). |
| 2026-10-25 | AP (FE-8) | §9: `GET ap/debit-memo/{uuid}` (detail with remaining amount + applications, 404 when absent / another organization's) and `GET ap/vendors/{vendorUuid}/debit-memos?openOnly=` (paged, newest first), AP_VIEW or AP_DEBIT_MEMO — the AP mirror of AR's credit-memo reads. |
| 2026-10-25 | AP (REV round) | §8: `propose` 400 `"The payment date cannot be before today."`; execution pays on the run's date or today when it has passed, re-prices items to what is still owed (settled ones become `Removed`), drops expired discounts; a batch's payment is created then posted in two steps (`supplierPaymentUuid` appears on a Pending batch between them); BillPayments are (re)published for every Paid batch before `Completed`. Export `Reference` = the batch's cheque number / SPAY number, else `{run}-{seq}` by place in the whole run. §6: `matchType: TwoWay` on a PO with receipts → 400 `"Purchase order {po} has goods received; its invoices are matched three-way."`; approval (all paths) re-checks the match → 409 `"Invoice {n} no longer matches its purchase order (…) …"`. Supplier payments: a line discount needs the invoice's term (400 texts: no term / "…was available until {date}." / "…can be at most {cap} …") and the discount account (409). §9: apply → 400 `"{note} is in {c1}; invoice {n} is in {c2}."` (D-AP-16..22). |
| 2026-10-25 | AP (FE-10) | §9 / §8.1: `ApDebitMemoModel` gains `currencyCode` (additive) — the note's currency per D-AP-21: its original invoice's, else the purchase base (null when the host cannot say). On create, apply, detail and the vendor list. |

### §8.1 AP response models (FE-1) — camelCase, enums as strings, dates `yyyy-MM-dd`

- **MatchDetailModel** (`GET/POST matching/invoices/{uuid}[...]`): `invoiceUuid, invoiceNumber, supplierInvoiceNo, vendorUuid, vendorName, poUuid, poNumber, grnUuid, grnNumber, matchStatus` (Pending/Matched/Variance/Approved/Rejected/Reversed), `matchType` (ThreeWay/TwoWay/null), `lineMatchResult` (Matched/PartialMatch/Mismatch/null = never matched), `withinTolerance, variancePercent, varianceAmount, toleranceRule { uuid, name } | null, autoApproved, matchedAt, matchedByName, overrideReason, overriddenAt, overriddenByName, currencyCode, subtotal, totalAmount, autoApproveMessage` (set when an auto-approval was refused), `lines[]: { lineNo, itemDescription, poLineUuid, grnLineUuid, poQuantity, poUnitPrice, poLineTotal, grnQuantity, invoiceQuantity, invoiceUnitPrice, invoiceLineTotal, quantityVariance, priceVariance, priceVariancePercent, totalVariance, lineStatus }`.
- **MatchSummaryModel** (`pending` page items, `po/{poUuid}` array): `invoiceUuid, invoiceNumber, supplierInvoiceNo, vendorUuid, vendorName, poUuid, poNumber, matchStatus, matchType, lineMatchResult, withinTolerance, variancePercent, varianceAmount, overridden, currencyCode, totalAmount, invoiceDate, matchedAt`. `pending?result=` Matched / PartialMatch / Mismatch / Overridden / NotMatched.
- **ToleranceRuleModel**: as §6.
- **PaymentScheduleModel** (list page items, `upcoming` / `discounts` arrays, every schedule command): `uuid, invoiceUuid, invoiceNumber, supplierInvoiceNo, vendorUuid, vendorName, currencyCode, invoiceDate, dueDate, daysUntilDue, originalAmount, amountRemaining, paymentPriority, paymentMethod, paymentBlock, blockReason, blockedByName, blockedAt, earlyPaymentTermUuid, earlyPaymentTermCode, discountDate, discountPercent, discountAmount, reservedByRunUuid, reservedByRunNumber, paidInRunUuid, paidInRunNumber, status, rowVersion`.
- **DiscountCalculation** (`{uuid}/discount`): `originalAmount, discountPercent, discountAmount, netPaymentAmount, discountDeadline, isEligible, daysUntilDeadline`.
- **CashFlowForecastModel** (`cash-flow`): `from, weeks, baseCurrencyCode, overdueAmount, overdueCount, overdueBlockedAmount, weekRows[]: { week (1-based), weekStart (Monday), weekEnd (Sunday), itemCount, dueAmount, blockedAmount, discountAvailable, netIfDiscountsTaken }, laterAmount, totalDue, totalBlocked, totalDiscountAvailable` (base currency; blocked amounts are never in `dueAmount`). FE note: the week array is `weekRows` and the amount `dueAmount`.
- **EarlyPaymentTermModel**: `uuid, code, name, discountPercent, discountDays, netDays, isActive`.
- **PaymentRunSummaryModel** (list items; `run` inside the detail): `uuid, runNumber, runDate, paymentDate, dueDateCutoff, status, paymentMethod, baseCurrencyCode, totalVendors, totalItems, totalAmount, totalDiscount, netPaymentAmount, failedBatchCount, proposedByName, proposedAt, approvedByName, approvedAt, completedAt` (totals in base; a Completed run's totals are what was paid).
- **PaymentRunDetailModel** (detail + every run command): `run` (summary above), `bankAccount, includeDiscount, selectionCriteria` (JSON text), `executedByName, executedAt, cancelledByName, cancelledAt, cancelReason, paymentFileName, exportedAt, rowVersion, warnings[]`, `vendorGroups[]` (= batches): `{ batchUuid, vendorUuid, vendorName, currencyCode, paymentMethod, bankDetailsMasked, itemCount, totalAmount, totalDiscount, netAmount, supplierPaymentUuid, supplierPaymentNumber, status (Pending/Paid/Failed), errorMessage, items[] }`, `removedItems[]`, `blockedItems[]: { scheduleUuid, invoiceUuid, invoiceNumber, vendorUuid, vendorName, currencyCode, dueDate, amountRemaining, blockReason }`. Item: `{ itemUuid, scheduleUuid, invoiceUuid, invoiceNumber, currencyCode, dueDate, originalAmount, discountAmount, netPaymentAmount, daysUntilDue, hasDiscount, discountDate, paymentMethod, status (Proposed/Approved/Removed/Paid/Failed), removedReason }`.
- **ApDebitMemoModel** (§9 create/apply): `uuid, debitNoteNumber, origin (SRO/MEMO), vendorUuid, vendorName, amount, debitReason, debitReasonDetail, originalInvoiceUuid, originalInvoiceNumber, status, applicationStatus (APPLIED_TO_INVOICE / CARRIED_FORWARD / APPLIED), remainingAmount, issuedAt, applications[]`; application row (also `GET …/applications`): `uuid, noteUuid, noteNumber, invoiceUuid, invoiceNumber, amount, appliedAt, appliedByName`.| 2026-10-25 | AP (A35 test) | `IX_invoices_ApAging` no longer includes ExchangeRate / ReversedAt (D-CORE-16); RPT's AP aging read looks them up. |
| 2026-10-25 | RPT (FE round 2) | §5: `GET api/finance/ar/customers/{uuid}/open-items` also admits `AR_CREDIT_MEMO`, and `GET api/finance/ap/vendors/{uuid}/open-items` also admits `AP_DEBIT_MEMO` (any of), so the credit-/debit-memo pages can pick invoices. Read-only; no other route changed. FE-9 (trend figures) left as a residual. |
| 2026-10-25 | REV (REV-1) | §10 seam added: `IEmailSender.SendWithResultAsync(string to, string subject, string htmlBody, IReadOnlyList<EmailAttachment>? attachments = null, CancellationToken ct = default)` → `EmailSendResult(bool Success, string? Error)` (SMS.Shared.Common), never throws; statements (RPT) and dunning (AR) must use it to decide Sent / Failed and store `Error`. `SendAsync` unchanged. Moq mocks need a Setup (or CallBase) for it. |
