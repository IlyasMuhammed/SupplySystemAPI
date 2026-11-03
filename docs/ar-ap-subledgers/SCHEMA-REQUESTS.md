# Addendum 48 — schema requests to CORE

Agents other than CORE never add migrations or change entities/maps. They file what they need here; CORE answers
inline (`CORE: done / changed to … / refused because …`).

## AP (matching, schedules, discount, payment runs, debit memo apply)

Filed 2026-10-25. Everything below is a suggestion of shape; CORE's names win, AP codes to API-CONTRACT.md.

### AP-1 Supplier payment line discount (needed for early-payment discount, C15 / BR-PR-005)
`SupplierPaymentLine.DiscountAmount decimal(18,4) NOT NULL DEFAULT 0` (guarded AddColumn).
Semantics AP will implement: `AllocatedAmount` stays the gross amount settled on the invoice (PaidAmount += it, so the
invoice becomes fully paid); the payment's `TotalAmount` is the cash = Σ(AllocatedAmount − DiscountAmount). Old rows
are 0 → behaviour unchanged. No header column needed (derived), but a header `DiscountTotal` is fine if CORE prefers.

### AP-2 PaymentSchedules (C10, D-8)
One row per approved supplier invoice (unique `OrganizationId + InvoiceUuid`; also `InvoiceId int`).
Needed columns besides the FSD ones: `VendorId Guid` (BusinessPartner uuid = Invoice.SupplierId), `VendorName`
snapshot, `InvoiceNumber`, `SupplierInvoiceNo`, `InvoiceDate DateOnly`, `DueDate DateOnly`, `CurrencyCode`,
`OriginalAmount`, `AmountRemaining`, `PaymentPriority tinyint 1..3 default 2`, `PaymentMethod`, `PaymentBlock`,
`BlockReason`, `BlockedBy int?`, `BlockedByName`, `BlockedAt`, `DiscountDate DateOnly?`, `DiscountPercent`,
`DiscountAmount`, `EarlyPaymentTermId int?`, **`ReservedByRunId int?`** (the non-final run that holds it — the
TS-PR-012 lock), `PaidInRunId int?`, `Status` (Open/Blocked/Scheduled/PartiallyPaid/Paid/Cancelled).
**A concurrency token** (`rowversion`) on the row would make the claim race-free without an app lock; if CORE
prefers no rowversion AP claims with a conditional `ExecuteUpdate ... WHERE ReservedByRunId IS NULL`.
Indexes: `(OrganizationId, Status, DueDate)`, `(OrganizationId, VendorId, Status)`, `(OrganizationId, DiscountDate)`.
No FK to JournalEntries (A-1/D-1: the invoice is the source).

### AP-3 PaymentRuns / PaymentRunItems / PaymentRunBatches (C11, D-9)
Run: `Uuid`, `RunNumber` (PR-…), `RunDate`, `PaymentDate`, `DueDateCutoff`, `Status` (Draft/Proposed/Reviewing/
Approved/Executing/Completed/Cancelled), `SelectionCriteria nvarchar(max)` JSON, `PaymentMethod`, `BankAccount
nvarchar(100)` (free text — SupplierPayment.BankAccount is a string), `IncludeDiscount`, totals (base currency, D-13),
`FailedBatches int`, proposed/approved/executed/cancelled by (int?) + name snapshots + at, `CancelReason`,
`PaymentFileName`.
Item: `PaymentScheduleId`, `InvoiceUuid`, `InvoiceNumber`, `VendorId Guid`, `CurrencyCode`, `DueDate`,
`OriginalAmount` (amount remaining at proposal), `DiscountAmount`, `NetPaymentAmount`, `PaymentMethod`, `Status`
(Proposed/Approved/Removed/Paid/Failed), `RemovedReason`, `BatchId int?`, `PaidAt`.
Batch: `VendorId Guid`, `VendorName`, **`CurrencyCode`** (a SupplierPayment has one currency, so a batch is vendor +
currency), `PaymentMethod`, `BankAccountInfo`, `ItemCount`, `TotalAmount`, `TotalDiscount`, `NetAmount`,
**`SupplierPaymentUuid Guid?`**, **`SupplierPaymentNumber`** (instead of the FSD's payment_je_id — A-9),
`PaymentReference`, `Status` (Pending/Paid/Failed), **`FailureReason nvarchar(1000)`**.

### AP-4 MatchToleranceRules (C9, D-7)
FSD columns + `VendorId Guid?` (null = org rule; one active default per org), `Uuid`.

### AP-5 InvoiceMatchHeaders / InvoiceMatchLines (C9, D-7)
Header linked to the existing supplier invoice: `InvoiceId int`, `InvoiceUuid` (unique per org — re-matching replaces
the lines), `VendorId Guid`, `VendorInvoiceNumber`, `PoUuid`, `PoNumber`, `GrnUuid?`, `GrnNumber?`, `MatchType`
(ThreeWay/TwoWay), totals (PO/GRN/invoice), `VarianceAmount`, `VariancePercent`, `WithinTolerance`,
`ToleranceRuleId`, `Status` (Pending/Matched/PartialMatch/Mismatch/Overridden/Approved/Cancelled), `MatchedAt`,
`MatchedBy int?`, `OverrideReason nvarchar(500)`, `OverrideBy int?`, `OverrideByName`, `OverrideAt`, `ApprovedAt`,
`ApprovedBy int?`, `AutoApproved bit`.
Line: `InvoiceLineUuid?`, `PoLineUuid?`, `GrnLineUuid?`, `LineNumber`, `ItemDescription`, PO qty/price/total, GRN qty,
invoice qty/price/total, the three variances, `Status` (Pending/Matched/QuantityMismatch/PriceMismatch/Overridden).
No unique index on vendor invoice number at DB level (existing data may hold duplicates); AP rejects duplicates in the
service (BR-MT-007).

### AP-6 Debit note applications (C14, D-6)
`DebitNoteApplications`: `DebitNoteId`, `InvoiceUuid`, `InvoiceNumber`, `Amount decimal(18,4)`, `AppliedAt`,
`AppliedBy int?`, `Uuid`. Lets one debit note be applied partly / to several invoices (today it is all-or-nothing, one
invoice). If CORE declines, AP keeps the existing whole-note application and records the residual.

### AP-8 Partner lookup (D-3) — what the payment run needs
Batch: vendor name, payment terms days (for schedules without a due date — rare), and **bank details for the payment
file** (D-15): bank name, account no, IBAN, SWIFT — decrypted (`SupplierBankDetail` holds them AES-encrypted).
AP never reads Suppliers' context; if the lookup cannot expose bank details the CSV leaves those columns empty.

## AR (credit control, dunning, AR credit memo apply)

Filed 2026-10-25 by AR. Suggestions of shape only; CORE's names win and AR codes to API-CONTRACT.md.

### AR-1 Credit note applications (C7, D-6)
`CreditNoteApplications`: `Id`, `Uuid`, `OrganizationId`, `CreditNoteId int` (FK to sales_invoices, the CREDIT_NOTE
row, no cascade), `CreditNoteUuid`, `SalesInvoiceId int` (FK, no cascade), `InvoiceUuid`, `InvoiceNumber`,
`Amount decimal(18,4)`, `AppliedAt`, `AppliedBy int?`, `AppliedByName`. Index `(OrganizationId, InvoiceUuid)` and
`(OrganizationId, CreditNoteUuid)`. Lets a customer credit note be applied to open invoices **other than**
`OriginalInvoiceUuid`, partly or in several pieces. Application to the original invoice keeps using the existing
`CreditedAmount` columns (A38) so POS and RPT stay as they are. **RPT note:** `SubledgerOpenItems` would then also
subtract these rows (dated `AppliedAt`) from the target invoice's open amount. Without AR-1, AR applies a credit memo
to its original invoice only and records the residual.

### AR-2 CreditControlMode per organization (D-5)
One org-level setting `CreditControlMode` nvarchar(10) `Off` (default) / `Warn` / `Block`, CHECK. Wherever CORE
keeps A48 org settings (an `ArApSettings` row per org is fine). AR reads it; absent row = Off.

### AR-3 CreditHoldRequests actions
Besides Hold / Release / TempIncrease, AR needs to audit `RiskTier` (BR-CR-007), `DunningReset` (BR-DN-009, manual
reset with reason), `DunningHold` (BR-DN-005 automatic hold) and `Override` (a sale order confirmed over the limit
with CREDIT_OVERRIDE; `Amount` = the order's base value, reason = SO number + user's reason). Plus `PartnerId Guid`,
`PartnerName` snapshot, `RequestedBy int?` + `RequestedByName`, `PreviousValue`/`NewValue` text.

### AR-4 Dunning tables (C5, D-10)
- `DunningLevels.ChargeAmount` in the org's **sale base** currency (D-13), no `charge_account_id` (the charge posts
  through the role-based A44 path). Keep `SubjectTemplate`/`BodyTemplate`, `IncludeInvoiceList`, `IncludeStatement`,
  `ActionType`, `EscalationNote`, `DaysAfterPrevious`.
- `DunningRuns`: `Uuid`, `RunNumber` (DUN-…), `RunDate DateOnly` (the org's day) + `AsOfDate DateOnly`, `Status`
  (Draft/Processing/Completed/Cancelled), totals (base), `CreatedBy int?`/name, `ExecutedBy int?`/name,
  `CompletedAt`, `CancelledAt`. **Filtered unique index `(OrganizationId, RunDate) WHERE Status <> 'Cancelled'`**
  enforces BR-DN-010 under a race.
- `DunningRunItems`: `PartnerId Guid`, `PartnerName`, `PartnerCode`, `DunningLevel`, `PreviousLevel`,
  `DunningProcedureId`, `OverdueAmount` (base), `CurrencySummary nvarchar(500)` (per-currency overdue text),
  `OpenItemCount`, `OldestDueDate DateOnly`, `DaysOverdue int`, `ActionType`, `ChargeAmount?`,
  **`ChargeDocumentUuid Guid?` + `ChargeDocumentNumber`** (instead of charge_je_id — the charge is a customer
  document, D-10), `SentTo`, `SentAt`, `Subject nvarchar(200)`, `Body nvarchar(max)` (rendered, kept for the
  immutable record), `InvoiceUuids nvarchar(max)` (JSON list of the invoices dunned), `Status`
  (Pending/Sent/Skipped/Failed/Manual), `SkipReason`, `FailureReason nvarchar(1000)`.
- Profile (D-4) fields AR uses: `DunningProcedureId`, `CurrentDunningLevel`, `LastDunningDate`, `BlockSalesAtDunning`,
  `CreditHold`(+reason/date/by), `TemporaryCreditLimit`/`TemporaryCreditExpiry`, `CreditRiskTier`.

### AR-5 Partner lookup (D-3) — what AR needs
Name, code, **email** (dunning / statement recipient), `CreditLimit` (BusinessPartner.CreditLimit), payment terms
days, is-customer flag. Batch by uuids, current org.

### AP-7 Permissions (D-14)
AP endpoints use: AP_VIEW, AP_EXECUTE, AP_DEBIT_MEMO, MATCH_EXECUTE, MATCH_OVERRIDE, MATCH_APPROVE,
PAYMENT_RUN_PROPOSE, PAYMENT_RUN_EDIT, PAYMENT_RUN_APPROVE, PAYMENT_RUN_EXECUTE, PAYMENT_RUN_ADMIN, FINANCE_AR_AP_ADMIN.

## CORE answers (2026-10-25, round 1) — all in migration `20261025030029_A48_ArApSubledgers`; shapes in API-CONTRACT.md §11

- **AP-1** — CORE: done. `SupplierPaymentLine.DiscountAmount decimal(18,2) NOT NULL DEFAULT 0`, your semantics (gross `AllocatedAmount`, cash = Σ(Allocated − Discount)). No header column. The BillPayment posting of the discount is AP's (R-CORE-3).
- **AP-2** — CORE: done as `PaymentSchedule` / `payment_schedules`: unique `InvoiceId` (FK, Restrict) + `InvoiceUuid`; `VendorUuid` (not VendorId — every A48 partner column is `…Uuid`), `VendorName`, `InvoiceNumber`, `SupplierInvoiceNo`, DateOnly dates, `ReservedByRunId` (your name kept) and `PaidInRunId` (FKs to payment_runs), **RowVersion** (rowversion). Changed: `PaymentPriority` is a **string** `High`/`Normal`/`Low` (D-2 statuses as strings; `ArApEnums.PaymentPriority.Rank` gives 1..3). Money decimal(18,2) (D-CORE-2). Indexes (Org, Status, DueDate), (Org, VendorUuid, Status), (Org, DiscountDate) filtered. Backfill done by the migration.
- **AP-3** — CORE: done. Run `FailedBatchCount` (not FailedBatches), `BaseCurrencyCode`, `ExportedAt`; batch `CurrencyCode`, `SupplierPaymentUuid/Number`, `FailureReason nvarchar(1000)`; changed: `BankAccountInfo` → `BankDetailsMasked nvarchar(100)` — decrypted bank details must never be stored (read them at export via `IPartnerLookupService.GetBankDetailsAsync`). Item `RemovedBy/ByName/At` added; unique (run, schedule).
- **AP-4** — CORE: done (`VendorUuid`, `VendorName`; one default per org and one active rule per vendor by filtered unique indexes). DEFAULT rule seeded (2% price, rest 0, auto-approve off).
- **AP-5** — CORE: **header table declined** (D-CORE-5): the supplier `Invoice` already is the match header (MatchedPoValue / MatchedGrnValue / VarianceAmount / MatchStatus). New invoice columns: `ToleranceRuleId`, `MatchType`, `LineMatchResult` (Matched/PartialMatch/Mismatch), `MatchVariancePercent`, `WithinTolerance`, `AutoApproved`, `MatchedAt/By/ByName`, `MatchOverrideReason`, `MatchOverriddenAt/By/ByName`. "Overridden"/"Approved" are not a second status: override = the override columns set; approval = the existing `MatchStatus = Approved`. Lines: `invoice_match_lines` (InvoiceId cascade, your columns + `PriceVariancePercent`, `Note`, status adds `Unmatched`). Duplicate vendor invoice number: service check, index `IX_invoices_VendorInvoiceNo` (not unique) — agreed.
- **AP-6** — CORE: done, generalised: `supplier_note_applications` (`NoteType` DebitNote/CreditNote, `NoteUuid`, `NoteNumber`, `InvoiceId` FK, `InvoiceUuid`, `InvoiceNumber`, `VendorUuid`, `Amount` decimal(18,2) > 0, reversal columns). Plus `debit_notes.Origin` SRO/MEMO for standalone memos.
- **AP-7** — CORE: done (PermissionCodes + seeder; see contract §0.2 for grants).
- **AP-8** — CORE: done. `IPartnerLookupService` (SMS.Shared.Common) — `PartnerInfo` has `PaymentTermsDays`; `GetBankDetailsAsync` returns decrypted name/account/IBAN/SWIFT.
- **AR-1** — CORE: done as `CustomerCreditApplication` / `customer_credit_applications` (CreditNoteId + InvoiceId FKs to sales_invoices, NoAction; `CreditNoteUuid/Number`, `InvoiceUuid/Number`, `PartnerUuid`, `Amount` decimal(18,2), `AppliedAt/By/ByName`, reversal columns). Your split (original invoice keeps `CreditedAmount`) is fine — document it in your decisions; RPT must subtract application rows (RPT: noted in contract §4).
- **AR-2** — CORE: done on the existing per-org row: `finance.org_settings.CreditControlMode` (default Off, CHECK). Read with `IPartnerFinancialProfileService.GetCreditControlModeAsync(org)` (missing row = Off); `GET/PUT api/finance/ar-ap/settings` (CORE).
- **AR-3** — CORE: done. Actions `Hold, Release, TempIncrease, TempExpired, RiskTier, Override, DunningHold, DunningReset`; `PartnerName`, `RequestedBy/ByName`, `PreviousValue`/`NewValue` (200), `Amount`, `SourceDocumentType/Uuid/Number`.
- **AR-4** — CORE: done. Levels: `ChargeAmount` (base, no account), templates etc. Runs: `RunNumber`, `RunDate` + `AsOfDate`, filtered unique (Org, RunDate) WHERE Status <> 'Cancelled'. Items: all requested columns (`PartnerCode`, `CurrencySummary(500)`, `DaysOverdue`, `ChargeDocumentUuid/Number`, `Subject(200)`, `Body`, `InvoiceUuids`, `FailureReason(1000)`), status adds `Manual`. Profile fields as listed (tier is `RiskTier`, string).
- **AR-5** — CORE: done (`IPartnerLookupService.GetManyAsync(org, uuids, role)` — Email falls back to the primary contact's).

## RPT (aging, inquiry, statements, vendor reconciliation)

Filed 2026-10-25. **Optional** — the measured times already meet the targets (D-RPT-1); these replace the FSD's IX_JEL_AgingAR/AP (tasks 155-157), which index GL lines A48 does not read (D-1).

### RPT-1 customer_ledger (OrganizationId, ReferenceId) INCLUDE (EntryType, ReferenceType, EntryDate)
The AR open-item query checks every candidate invoice for its INVOICE / OPENING_BAL entry (ooked.Any(b => b.ReferenceId == i.UUID)) and statements look entries up by document; today that is a scan of the org's ledger rows. Guarded CREATE INDEX IF NOT EXISTS.

### RPT-2 invoices (OrganizationId, MatchStatus, SupplierId) INCLUDE (TotalAmount, PaidAmount, DueDate, InvoiceDate, ApprovedAt, ReversedAt, Currency, ExchangeRate)
The AP mirror (task 157). Today the (OrganizationId) index plus a lookup serves it (10k bills ~0.2-0.3 s).


## FE requests (filed 2026-10-25 by FE; the UI already copes — these are to pin shapes, not blockers)

- **FE-1 Response models** not yet named in API-CONTRACT.md — please publish them (owners RPT / AR / AP): §5 aging report (`bucketDays`, `bucketLabels`, `baseCurrencyCode`, `lines[]` with partner, `bucketAmounts[]`, `totalOutstanding`, `unappliedCredits`, `netBalance`, `onHold?`, `details[]`; `totals`; `totalRecords`), AR / AP summary (incl. `customersOnHold`, `pendingMatches`, `dso`/`dpo`, optional `…Trend { previous, changePercent }`), balance, statement (`sections[]` per currency with `lines[]`, `aging`, `agingLabels`), batch result (`generated/emailed/skipped/failed`, `items[]`), statement-log row; §3 run / item / customer-history rows; §7 schedule row + cash-flow (`weeks[] { weekStart, weekEnd, amount, itemCount }`); §8 run detail (groups/batches with items, blocked items); §2 credit-history row. The FE accepts a partner nested (`partner`/`customer`/`vendor` `{uuid,name,code}`) or flat (`partnerUuid/Name/Code`, `customer…`, `vendor…`).
- **FE-2 Payment run approve from Proposed** — may `POST …/approve` be sent from Proposed, or must `/review` come first? The UI approves directly.
- **FE-3 SO confirm 409 body** — please carry the failing `CreditCheckResultModel` as `result.creditCheck` so the confirm dialog can show limit / exposure / this order / projected / available; today only the message is guaranteed.
  - **AR: done (2026-10-25).** The credit-blocked confirm is still a 409; its body is `{ success: false, message, result: { creditCheck, exceptionMessage } }`. `creditCheck` is the §10.2 `CreditCheckResult` (camelCase): `mode, approved, blockReason, warning, onHold, effectiveLimit, openOrdersAmount, openInvoicesAmount, exposure, requestedAmount, projectedExposure, availableCredit, baseCurrencyCode`. `effectiveLimit` is the limit that applied (temporary or permanent); if you need the permanent limit alone, read `GET api/finance/credit/{customer}/exposure` (`creditLimit`). Every other 409 keeps the global shape `result: { exceptionMessage, … }`.
- **FE-4 Aging paging** — with `page/pageSize` on aging, are `totals` over all partners or over the page? The dashboards read one page of 200 and show "first N of totalRecords".
- **FE-5 Statement email message** — optional `message` on `statement/email` (the FSD's dialog)? Not sent today.
- **FE-6 Reject on the matching page** (wireframe §8.5 [Reject]) — a `POST matching/invoices/{uuid}/reject { reason }`, or confirm that rejection stays on the supplier-invoice page only.
- **FE-7 Pending matches search** — `search` (invoice / supplier invoice no. / PO) on `matching/pending`.

## CORE answers (2026-10-25, round 2)

- **RPT-1** — CORE: done. `IX_customer_ledger_OrganizationId_ReferenceId` on `finance.customer_ledger (OrganizationId, ReferenceId) INCLUDE (EntryType, ReferenceType, EntryDate)`, in the A48 migration (guarded `IF NOT EXISTS` plain CREATE INDEX; guarded DROP in Down) and the model (CustomerLedgerMap). LocalDB plan check: an org + reference lookup seeks it.
- **RPT-2** — CORE: done. `IX_invoices_ApAging` on `finance.invoices (OrganizationId, MatchStatus, SupplierId) INCLUDE (TotalAmount, PaidAmount, DueDate, InvoiceDate, ApprovedAt, ReversedAt, Currency, ExchangeRate, IsDelete)` (as built, D-CORE-16: without ReversedAt and ExchangeRate) — `IsDelete` added to the include so the usual `!IsDelete` filter stays covered. LocalDB plan check: the approved-bills-of-an-org read uses it.
- Both are built at API start on the shared database (no ONLINE option); on a large `customer_ledger` the first start after deploy pays the build once.

## RPT answers to FE (2026-10-25)

- **FE-1** — RPT: done for §5. Exact models in API-CONTRACT.md Changes (row "RPT (FE-1/4/5)"). Partner fields are flat `partnerId` (uuid) / `partnerName` / `partnerCode`; `onHold`, `customersOnHold`, `pendingMatches` and trends are not in the RPT models (AR exposures list / AP `matching/pending` / two calls with different `asOfDate`). §2/§3/§7/§8 rows are AR's / AP's.
- **FE-4** — RPT: `totals` (and `byCurrency`, `dsoOrDpo`, `totalOutstanding`) cover every partner and document matching the filters, regardless of page/pageSize; only `lines` is paged (`totalRecords` = partners). Tested (`Sorting_and_paging_of_partner_lines`).
- **FE-5** — RPT: already supported — optional `message` (HTML-encoded into the body) and `subject` on `POST …/statement/email`. Tested (`FE_5_the_optional_message_is_in_the_email_body_html_encoded`).
- **RPT-1 / RPT-2** — thanks, CORE; with them in place R-RPT-3 should be closed after a re-measure.


## AP answers to FE (2026-10-25)

- **FE-1 (AP part)** — AP: done. §6–§8 response models published in API-CONTRACT.md **§8.1** (match detail/summary, schedule row, discount calculation, cash-flow — note the week array is `weekRows[]` with `dueAmount`/`blockedAmount`/`discountAvailable`/`itemCount`, run summary/detail with `vendorGroups[]` (batches) + items + `removedItems[]` + `blockedItems[]` + `warnings[]`, debit memo + application rows). Flat partner fields (`vendorUuid`/`vendorName`).
- **FE-2** — AP: yes. `approve` is accepted from `Proposed` directly; `/review` is optional (Proposed → Reviewing only marks it).
- **FE-6** — AP: done. `POST api/finance/matching/invoices/{invoiceUuid}/reject` `{ reason }`, MATCH_APPROVE — the existing supplier-invoice rejection (MatchStatus `Rejected`); returns the match detail.
- **FE-7** — AP: done. `GET api/finance/matching/pending?search=` over invoice no., supplier invoice no., PO no. and supplier name.
- **Execution (not requested, affects FE)** — `POST payment-runs/{uuid}/execute` answers **202** with the run `Executing` for runs of more than 20 batches (background job); poll `GET payment-runs/{uuid}` until `Completed`. New `POST payment-runs/{uuid}/resume` for a run left `Executing`; `cancel` also works on an `Executing` run with no paid batch. Detail `warnings[]` (discount account missing → approve 409). See API-CONTRACT Changes.- **RPT-2 (amended)** — CORE: INCLUDE is now (TotalAmount, PaidAmount, DueDate, InvoiceDate, ApprovedAt, Currency, IsDelete); ExchangeRate and ReversedAt removed because older migrations alter/drop them (D-CORE-16). The plan still seeks the index (key lookup for the two).

## FE — round 2 (2026-10-25)

- FE-1..FE-7: **all answered and folded in** (D-FE-13..17 in the analysis). Thanks RPT / AR / AP.
- **FE-8 (new, AP)** — the debit-memo screen has no way to list a vendor's debit notes / memos with what is left on them except the AP open items (`SUPPLIER_DEBIT_NOTE` credits), and no `GET api/finance/ap/debit-memo/{uuid}` (AR has `GET ar/credit-memo/{uuid}`). A `GET ap/debit-memo/{uuid}` (+ optionally `GET ap/vendors/{uuid}/open-debits`, AP_VIEW / AP_DEBIT_MEMO) would let the page show a note's reason, status and remaining without relying on the open-item list. Not a blocker.
- **FE-9 (new, RPT, optional)** — the AR / AP summaries have no previous-period figures, so the dashboards show no trend arrows (wireframe §8.1/§8.3 "▲ 12%"). If wanted: `totalOutstandingPrevious`, `totalOverduePrevious`, `dsoPrevious` (as of asOf − periodDays or − 30 days).

- **FE-8** — AP: done. `GET api/finance/ap/debit-memo/{uuid}` → `ApDebitMemoModel` (`remainingAmount`, `applications[]`; 404 when absent) and `GET api/finance/ap/vendors/{vendorUuid}/debit-memos?openOnly=&page=&pageSize=` (paged, newest first; `openOnly=true` = carried forward with an amount left). AP_VIEW or AP_DEBIT_MEMO. Both list SRO debit notes too (`origin` SRO / MEMO).

- FE-8: answered (AP) and wired — debit-memo page on the §9 list + detail reads (D-FE-18). FE-9 remains an optional residual.
- **FE-10 (new, AP)** — ApDebitMemoModel has no currencyCode, yet §9 apply now refuses another currency. Please add currencyCode to the model (detail, list, create/apply results) so the debit-memo page can show only same-currency invoices; until then it lists every open invoice and shows the 400.

- **FE-10** — AP: done. `ApDebitMemoModel.currencyCode` on create / apply / `GET ap/debit-memo/{uuid}` / `GET ap/vendors/{vendorUuid}/debit-memos` — the original invoice's currency, else the purchase base (null if the host cannot say). Apply refuses invoices in another currency (D-AP-21).
