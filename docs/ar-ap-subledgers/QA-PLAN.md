# Addendum 48 — QA plan (agent QA, tasks 583–596 + 628)

Scope: end-to-end, integration, security (D-14), isolation and performance tests of the AR & AP sub-ledgers, run through the
real `SMS.API` host (`SapWebApplicationFactory`) on a throwaway LocalDB database (`SMS_SAP_E2E_<guid>`, dropped on dispose).
Never SMSGlobal: every class calls `ShouldBeOnLocalDb()` in its constructor.

Overrides: ADDENDUM-48-ANALYSIS.md Part A + D-1..D-18 win over the FSD. In particular:

- D-1 — figures come from the SCM Finance documents (sales invoices, customer payments/allocations, supplier invoices,
  supplier payments, credit/debit notes), never from `JournalEntryLines`. FSD wording "GL lines / reconciled" is tested as
  "open document balance". "JE created" (TS-MT-006, TS-PR-005/008, TS-CM-001/004, TS-DN-007) is tested as "the document
  went through the existing A44 posting" — with the internal GL on (`NewJeOrgAsync`) we read the A44 journal of the
  source document (balanced, right accounts); without it, the document and its ledger entry.
- D-5 — `CreditControlMode` is Off by default; every credit test switches it to Block (or Warn) first; one test proves Off
  leaves SO confirmation untouched even for a customer on hold and over the limit.
- D-9 — a payment run executes by creating one SupplierPayment per vendor batch (allocated to the scheduled invoices).
- D-12 — due-date basis aging by default; DPO uses supplier-invoice purchases.
- D-13 — totals in base currency (document amount × document rate).
- D-16 — seeds: STANDARD dunning procedure (4 levels, no charges, block-sales off), DEFAULT tolerance 2 % / 0 / 0 %, auto-approve off.

Routes, bodies, statuses and exact refusal messages come from `API-CONTRACT.md` (CORE). Where the contract is not yet
published the "Expected" column states the spec'd behaviour; the test asserts the contract text once it lands
(`ShouldRefuse(status, errorCode, message)` from JeKit — message contained, case-insensitive).

Dates: tests that need overdue items back-date the document's `InvoiceDate` / `DueDate` in SQL after creating it through the
API (the only way to make a 35-day-old invoice today); dunning "days since last" is moved the same way on the profile.

Run one class at a time:
`dotnet test tests\SMS.Integration.Tests --no-build --filter FullyQualifiedName~ArAp.<Class>` (results under `%TEMP%\a48qa`).

## Classes (`tests/SMS.Integration.Tests/ArAp/`)

| Class | Covers | Tasks |
|---|---|---|
| `ArApKit.cs` | shared: A48 routes/codes, org + Finance, customer with limit, credit-mode setting, issued invoices, back-dating, PO→GRN→supplier invoice, schedules, runs, permission logins | — |
| `ArCreditControlE2ETests` | TS-CR-001..010, BR-CR-001..007, Off mode, Warn mode, invoice warning | 583 |
| `ArApAgingE2ETests` | TS-AG-001..008, BR-AG-001..006, AP aging, DSO/DPO, base-currency totals, open items/balance inquiry | 597–606 (read side) |
| `ArDunningE2ETests` | TS-DN-001..011, BR-DN-001..010, seeds | 584, 476–487 |
| `ArStatementE2ETests` | TS-ST-001..007, BR-ST-001..005, per-currency sections | 244–270 (read side) |
| `ApMatchingE2ETests` | TS-MT-001..010, BR-MT-001..008, schedule created on approval | 585 |
| `ApPaymentRunE2ETests` | TS-PR-001..012, TS-EP-001..004, BR-PR-001..010, BR-EP-001..004 | 586, 587 |
| `ArApMemoE2ETests` | TS-CM-001..005 | 588–593 |
| `ArApSecurityE2ETests` | each D-14 code allows exactly its actions; no code → 403 | — |
| `ArApIsolationE2ETests` | Org A vs Org B on every A48 read and write | 596 |
| `ArApPerformanceE2ETests` | aging 50k lines < 5 s; run 200 vendors / 500 items | 594, 595 |
| regression | existing classes re-run one at a time (list at the end) | — |

## 1. Credit control — `ArCreditControlE2ETests` (task 583)

Setup: `NewOrgAsync` (Finance on), stocked world (vendor, customer, item at 100, 50 in stock), `CreditControlMode = Block`.
Limit = `BusinessPartner.CreditLimit` (D-4) set through the partner edit; exposure = confirmed-not-fully-invoiced SO value +
open sales-invoice balance (base currency).

| Test method | Scenario / rule | Setup | Expected |
|---|---|---|---|
| `TS_CR_001_no_limit_confirms` | TS-CR-001, D-4 null/0 = unlimited | limit null; SO 5 000 | confirm 200, CONFIRMED |
| `TS_CR_002_within_limit_confirms` | TS-CR-002 | limit 10 000; open invoice 8 000; SO 1 000 | confirm 200 |
| `TS_CR_003_over_limit_blocks` | TS-CR-003, BR-CR-001 | limit 10 000; open invoice 10 000; SO 500 | confirm refused (contract status/code/message, "exceeds"), SO stays DRAFT |
| `TS_CR_004_hold_blocks_regardless` | TS-CR-004, BR-CR-003 | limit null; hold placed; SO 1 | refused with the hold message |
| `TS_CR_005_temporary_limit_used` | TS-CR-005, BR-CR-002 | limit 1 000, temp 5 000 to today+7; SO 3 000 | confirm 200; exposure shows effective limit 5 000 |
| `TS_CR_006_expired_temporary_limit_ignored` | TS-CR-006, BR-CR-005 | temp expiry moved to yesterday in SQL | SO 3 000 refused; exposure effective = 1 000, temp cleared/ignored |
| `TS_CR_007_hold_release_flow` | TS-CR-007, task 583, BR-CR-006 | hold → SO refused → release → same SO confirms | history has Hold + Release rows (reason, user, date) |
| `TS_CR_008_concurrent_checks_no_race` | TS-CR-008, D-5 app lock | limit 1 000; two SOs of 600 confirmed in parallel | exactly one CONFIRMED, one refused |
| `TS_CR_009_exposure_sums_orders_and_invoices` | TS-CR-009, BR-CR-004, D-13 | one open confirmed SO 700 + issued invoice 1 170 (GST) − partial payment 170; one USD invoice at rate 280 | exposure.openOrders = 700, openInvoices = 1 000 + USD×280, total = sum, available = limit − total |
| `TS_CR_010_risk_tier_persists` | TS-CR-010, BR-CR-007 | new profile default Medium; PUT risk-tier High | profile reads High; history/audit shows change; without CREDIT_MANAGE → 403 |
| `Off_mode_leaves_sale_orders_untouched` | D-5 | mode Off (default); customer on hold and over limit | confirm 200 |
| `Warn_mode_confirms_with_warning` | D-5 | mode Warn; over limit | confirm 200 + warning in answer (contract field) |
| `Override_permission_confirms_blocked_order` | D-5 CREDIT_OVERRIDE | Block; over limit; user with CREDIT_OVERRIDE confirms with override | 200; without it refused |
| `Invoice_creation_warns_only` | D-5 | Block; SO confirmed earlier, then hold; raise invoice | invoice created (200) with warning |
| `Credit_check_endpoint_matches_confirm` | §7.2 #13 | POST credit/{id}/check amount | approved/blockReason/projectedExposure match the confirm decision |

## 2. Aging / inquiry — `ArApAgingE2ETests`

Setup: org PKR base + USD; three customers with issued invoices back-dated (SQL) to due dates today−0/−10/−45/−75/−100/−150;
one partially paid; one cancelled; one draft. Vendors likewise with approved supplier invoices.

| Test method | Scenario / rule | Expected (hand-computed in the test) |
|---|---|---|
| `TS_AG_001_ar_default_buckets` | TS-AG-001, BR-AG-002/005, D-12 | Current / 1-30 / 31-60 / 61-90 / 91-120 / 120+ hold exactly the hand-computed residuals; total = sum of open balances |
| `TS_AG_002_ap_custom_buckets` | TS-AG-002, BR-AG-006 | boundaries 0,15,30,45,60 → buckets assigned accordingly; 2 boundaries refused (minimum 3 buckets) |
| `TS_AG_003_dso_90_days` | TS-AG-003, BR-AG-003, D-12 | DSO = AR balance / credit sales in 90 days × 90 (rounding per contract) |
| `TS_AG_004_dpo` | TS-AG-004, BR-AG-004 | DPO = AP balance / supplier-invoice purchases × days |
| `TS_AG_005_drill_down` | TS-AG-005 | bucket detail lists exactly the invoices of that bucket |
| `TS_AG_006_partial_payment_residual` | TS-AG-006 | invoice 1 000 paid 400 → residual 600 in its bucket |
| `TS_AG_007_cancelled_excluded` | TS-AG-007 (reversed → cancelled/reversed document) | not listed |
| `TS_AG_008_draft_excluded` | TS-AG-008, BR-AG-001 | draft sales invoice / unapproved supplier invoice not listed |
| `Document_date_basis` | D-12 | basis=document moves the items by invoice date |
| `Multi_currency_totals_in_base` | D-13 | USD invoice 100 @ 280 counts 28 000 PKR in totals; detail shows USD 100 |
| `Customer_balance_open_items_transactions` | C6, §7.1 #4–6 | balance = open items sum; transactions list invoice + payment |
| `Vendor_balance_open_items_transactions` | C12, §7.4 #31–33 | same for vendor |
| `Existing_aging_endpoints_still_work` | A-6 | `api/reports/sales/aging-receivables`, `api/suppliers/{id}/aging`, `api/reports/supplier-aging` 200 and agree with the new totals |

## 3. Dunning — `ArDunningE2ETests` (task 584)

Setup: procedure created by the test: grace 5, minimum 50, levels 1..4 at 7/14/30/30 days, level 4 block-sales on,
charge on level 2 (D-10 charge document); customer invoices back-dated.

| Test method | Scenario / rule | Expected |
|---|---|---|
| `Seeds_standard_procedure_and_default_tolerance` | D-16 | STANDARD, 4 levels, no charges, block-sales off; DEFAULT tolerance 2/0/0, auto-approve off |
| `TS_DN_001_overdue_12_grace_5_level_1` | TS-DN-001, BR-DN-001 | preview proposes level 1 |
| `TS_DN_002_overdue_3_skipped` | TS-DN-002 | customer not proposed |
| `TS_DN_003_level_2_20_days_not_yet` | TS-DN-003, BR-DN-003 | not proposed |
| `TS_DN_004_level_2_35_days_level_3` | TS-DN-004, BR-DN-004 | level 3 proposed (never 4) |
| `TS_DN_005_below_minimum_skipped` | TS-DN-005, BR-DN-002 | overdue 30 < 50 → skipped |
| `TS_DN_006_execute_emails_levels` | TS-DN-006, task 584, D-11 | execute → items Sent, email captured by the test sender with placeholders filled, profile level 1, last date today |
| `TS_DN_007_charge_creates_document` | TS-DN-007, BR-DN-007, D-10 | charge document for the customer (open balance + charge), A44 journal balanced Dr receivable / Cr revenue (internal GL on) |
| `TS_DN_008_block_sales_sets_hold` | TS-DN-008, BR-DN-005 | reaching level 4 → profile credit hold on |
| `TS_DN_009_paid_resets_level` | TS-DN-009, BR-DN-006 | payment clearing everything → level 0 |
| `TS_DN_010_manual_reset_admin_only` | TS-DN-010, BR-DN-009 | DUNNING_EXECUTE only → 403; DUNNING_ADMIN → level 0 |
| `TS_DN_011_second_run_same_day_rejected` | TS-DN-011, BR-DN-010 | second execute refused (contract message) |
| `Completed_run_is_immutable` | BR-DN-008 | execute again / change completed run refused |

## 4. Statements — `ArStatementE2ETests`

| Test method | Scenario / rule | Expected |
|---|---|---|
| `TS_ST_001_activity_10_transactions_running_balance` | TS-ST-001, BR-ST-001 | 10 rows, running balance hand-computed |
| `TS_ST_002_open_items_excludes_paid` | TS-ST-002, BR-ST-002 | paid invoice absent |
| `TS_ST_003_opening_balance` | TS-ST-003, BR-ST-003/004 | opening = sum before fromDate; closing = opening + debits − credits |
| `TS_ST_004_pdf` | TS-ST-004, D-11 | application/pdf, starts `%PDF` |
| `TS_ST_005_batch_min_balance` | TS-ST-005, BR-ST-005 | only customers with balance > 1 000 |
| `TS_ST_006_email_logged` | TS-ST-006 | send 200, StatementLog row, no real mail |
| `TS_ST_007_vendor_reconciliation_differences` | TS-ST-007, C13 | vendor-stated balance vs ours: differences listed (missing invoice, amount diff) |
| `Per_currency_sections` | D-13 | PKR and USD sections, each with its own balances |

## 5. 3-way matching — `ApMatchingE2ETests` (task 585)

Setup: vendor, item, PO 10 @ 300 → sent → GRN; supplier invoice with lines (PoLineUuid/GrnLineUuid), tolerance rule per test.

| Test method | Scenario / rule | Expected |
|---|---|---|
| `TS_MT_001_all_lines_within_tolerance` | TS-MT-001, BR-MT-001 | Matched, each line Matched |
| `TS_MT_002_price_3pct_tol_2pct` | TS-MT-002, BR-MT-003 | PriceMismatch line, header Mismatch |
| `TS_MT_003_price_1_5pct_tol_2pct` | TS-MT-003 | Matched |
| `TS_MT_004_qty_over_grn` | TS-MT-004, BR-MT-004 | QuantityMismatch |
| `TS_MT_005_override_with_reason` | TS-MT-005, BR-MT-006 | no reason → 400; MATCH_OVERRIDE + reason → Overridden; without permission 403 |
| `TS_MT_006_approve_posts_and_schedules` | TS-MT-006, task 585, D-7/D-8 | approve → existing approval (ledger INVOICE_APPROVED, A44 journal), PaymentSchedule row with invoice due date |
| `TS_MT_007_duplicate_vendor_invoice_number` | TS-MT-007, BR-MT-007 | refused (contract message) |
| `TS_MT_008_auto_approve_within_tolerance` | TS-MT-008, BR-MT-005 | rule auto-approve on → invoice approved after match, schedule created |
| `TS_MT_009_two_way_no_grn` | TS-MT-009, BR-MT-002 | PO ↔ invoice only, Matched |
| `TS_MT_010_partial_delivery` | TS-MT-010 | PO 10, GRN 6, invoice 6 → Matched |
| `Mismatch_cannot_be_approved` | BR-MT-008 | approve of Mismatch (not overridden) refused |
| `Per_vendor_tolerance_wins` | D-7 | vendor rule 5 % beats org 2 % |

## 6. Payment runs + early discount — `ApPaymentRunE2ETests` (tasks 586, 587)

Setup: approved supplier invoices (direct, `ApprovedBillAsync`) for N vendors with due dates; schedules backfilled/created.

| Test method | Scenario / rule | Expected |
|---|---|---|
| `TS_PR_001_proposal_5_vendors_15_items` | TS-PR-001, BR-PR-001/003 | 5 batches, 15 items, totals = hand sum; items past cutoff excluded |
| `TS_PR_002_blocked_excluded` | TS-PR-002, BR-PR-002 | blocked schedule not in run (listed as blocked) |
| `TS_PR_003_remove_item_recalculates` | TS-PR-003 | DELETE item → totals − amount |
| `TS_PR_004_approve` | TS-PR-004, BR-PR-006 | Approved; execute before approve refused |
| `TS_PR_005_execute_one_payment_per_vendor` | TS-PR-005, task 586, D-9, BR-PR-007 | one SupplierPayment per vendor, lines = scheduled invoices, supplier ledger entries, A44 posting |
| `TS_PR_006_schedules_paid` | TS-PR-006 | schedules Paid; invoices PAID |
| `TS_PR_007_invoices_settled` | TS-PR-007, BR-PR-008 | invoice outstanding 0; vendor open items empty |
| `TS_PR_008_discount_line` | TS-PR-008, task 587, BR-PR-004/005 | discount applied → payment net, discount amount recorded; GL 3 lines (internal GL on) |
| `TS_PR_009_csv_export` | TS-PR-009, D-15 | text/csv, header + one row per vendor, bank details |
| `TS_PR_010_cancel_approved` | TS-PR-010, BR-PR-010 | Cancelled, no supplier payments, schedules released |
| `TS_PR_011_execute_twice_rejected` | TS-PR-011, BR-PR-009 | second execute refused; no extra payments |
| `TS_PR_012_concurrent_runs_no_double_payment` | TS-PR-012, D-9 lock | two proposals in parallel → each schedule in at most one run; execute both → each invoice paid once |
| `Completed_run_cannot_be_cancelled` | BR-PR-009/010 | refused |
| `TS_EP_001_within_window` | TS-EP-001, BR-EP-001/002 | net = original − original × pct/100 |
| `TS_EP_002_after_window` | TS-EP-002 | full amount |
| `TS_EP_003_three_line_journal` | TS-EP-003, BR-EP-003 | Dr AP full, Cr bank net, Cr discount account |
| `TS_EP_004_discount_opportunities` | TS-EP-004 | list holds the invoice with deadline and amount |
| `Term_discount_days_must_be_below_net` | BR-EP-004 | term 30/30 refused |

## 7. Memos — `ArApMemoE2ETests` (tasks 588–593)

| Test method | Scenario | Expected |
|---|---|---|
| `TS_CM_001_ar_credit_memo_posts` | TS-CM-001, 589 | credit memo document; A44 journal Dr revenue / Cr receivable (GL on) |
| `TS_CM_002_apply_full` | TS-CM-002, 588, 590 | invoice residual 0, memo fully applied, customer ledger credit |
| `TS_CM_003_apply_partial` | TS-CM-003, 591 | residual reduced by the applied amount only; over-apply refused |
| `TS_CM_004_ap_debit_memo` | TS-CM-004, 592 | debit memo document; journal Dr payable / Cr expense |
| `TS_CM_005_apply_debit_memo` | TS-CM-005, 593 | supplier invoice outstanding reduced; supplier ledger |

## 8. Security — `ArApSecurityE2ETests` (D-14)

One user per code (role with only that code + module view codes needed to read the target). For each code: its own
actions are not 403, every other A48 write is 403. Matrix:

| Code | Allowed (sample call) |
|---|---|
| AR_VIEW | AR aging, summary, DSO, customer balance/open items/transactions/statement |
| AR_STATEMENT_SEND | statement email, batch |
| AR_CREDIT_MEMO | create / apply credit memo |
| CREDIT_CHECK | credit check |
| CREDIT_MANAGE | hold, release, temporary limit, risk tier |
| CREDIT_OVERRIDE | confirm a blocked SO with override |
| DUNNING_EXECUTE | preview, execute |
| DUNNING_ADMIN | reset level |
| AP_VIEW | AP aging, summary, DPO, vendor reads, schedules, discounts, cash flow |
| AP_EXECUTE | block/unblock schedule, vendor reconciliation |
| AP_DEBIT_MEMO | create / apply debit memo |
| MATCH_EXECUTE | match |
| MATCH_OVERRIDE | override |
| MATCH_APPROVE | approve match |
| PAYMENT_RUN_PROPOSE / _EDIT / _APPROVE / _EXECUTE / _ADMIN | propose / remove item / approve / execute+export / cancel |
| FINANCE_AR_AP_ADMIN | procedures, tolerances, early-payment terms, profile PUT |

Plus: anonymous → 401; Organization Admin has all; a user with none → 403 on every A48 route.

## 9. Isolation — `ArApIsolationE2ETests` (task 596)

Org A and Org B (`NewOrgAsync` twice). A's customer/vendor/invoices/runs/procedures. B's admin: every A48 GET by A's ids →
404 (or empty), lists contain none of A's rows, aging/summary totals are B's only, writes on A's ids (hold, check, apply,
approve run, execute) → 404 and A unchanged; B's dunning run doesn't touch A's customers; B's payment run never proposes A's schedules.

## 10. Performance — `ArApPerformanceE2ETests` (tasks 594, 595)

- 594: 50 000 open sales invoices cloned in SQL from one issued API invoice (columns from sys.columns, due dates spread 0–200
  days back), AR aging timed after a warm-up: **< 5 s** (contract target), totals checked = 50 001 × amount.
- 595: 200 vendors × 2–3 approved invoices = 500 schedules (vendors via API, invoices cloned in SQL + schedules via the
  backfill/API); propose timed, execute timed; 200 batches, 500 items, 200 supplier payments, every invoice paid once.
Timings to test output and `%TEMP%\a48qa\perf.txt`.

## 11. Regression (one class at a time)

`SapAlignment.SalesFlowE2ETests`, `SapAlignment.PurchaseFlowE2ETests`, `SapAlignment.SupplierInvoiceHardeningE2ETests`,
`SapAlignment.SupplierNoteApplicationE2ETests`, `SapAlignment.FinanceSetupE2ETests`, `MultiCurrency.MultiCurrencyDocumentsE2ETests`,
`FinancialPosting.*` (9 classes), `JournalEngine.*` (5 classes), `SalesPreOrder.*` (SO confirm path), `FulfillmentRoutes.*` (confirm gate).

## 12. Business-rule coverage (task 628)

| Rule | Test |
|---|---|
| BR-CR-001 | TS_CR_003 |
| BR-CR-002 | TS_CR_005 |
| BR-CR-003 | TS_CR_004 |
| BR-CR-004 | TS_CR_009 |
| BR-CR-005 | TS_CR_006 |
| BR-CR-006 | TS_CR_007 |
| BR-CR-007 | TS_CR_010 |
| BR-AG-001 | TS_AG_007, TS_AG_008 |
| BR-AG-002 | TS_AG_001 |
| BR-AG-003 | TS_AG_003 |
| BR-AG-004 | TS_AG_004 |
| BR-AG-005 | TS_AG_001 |
| BR-AG-006 | TS_AG_002 |
| BR-DN-001 | TS_DN_001/002 |
| BR-DN-002 | TS_DN_005 |
| BR-DN-003 | TS_DN_003 |
| BR-DN-004 | TS_DN_004 |
| BR-DN-005 | TS_DN_008 |
| BR-DN-006 | TS_DN_009 |
| BR-DN-007 | TS_DN_007 (D-10: charge document via A44, not a raw JE) |
| BR-DN-008 | Completed_run_is_immutable |
| BR-DN-009 | TS_DN_010 |
| BR-DN-010 | TS_DN_011 |
| BR-ST-001 | TS_ST_001 |
| BR-ST-002 | TS_ST_002 |
| BR-ST-003 | TS_ST_003 |
| BR-ST-004 | TS_ST_003 |
| BR-ST-005 | TS_ST_005 |
| BR-MT-001 | TS_MT_001, TS_MT_010 |
| BR-MT-002 | TS_MT_009 |
| BR-MT-003 | TS_MT_002/003 |
| BR-MT-004 | TS_MT_004 |
| BR-MT-005 | TS_MT_008 |
| BR-MT-006 | TS_MT_005 |
| BR-MT-007 | TS_MT_007 |
| BR-MT-008 | Mismatch_cannot_be_approved |
| BR-PR-001 | TS_PR_001 |
| BR-PR-002 | TS_PR_002 |
| BR-PR-003 | TS_PR_001, TS_PR_005 |
| BR-PR-004 | TS_PR_008 |
| BR-PR-005 | TS_PR_008, TS_EP_003 |
| BR-PR-006 | TS_PR_004 |
| BR-PR-007 | TS_PR_005 (D-9: SupplierPayment per vendor → A44) |
| BR-PR-008 | TS_PR_007 |
| BR-PR-009 | TS_PR_011, Completed_run_cannot_be_cancelled |
| BR-PR-010 | TS_PR_010 |
| BR-EP-001 | TS_EP_001/002 |
| BR-EP-002 | TS_EP_001 |
| BR-EP-003 | TS_EP_003 |
| BR-EP-004 | Term_discount_days_must_be_below_net |

47/47 rules mapped.

## Results

Run 2026-10-25 on LocalDB (`SMS_SAP_E2E_*`, dropped), each class alone, build output `%TEMP%\a48qa\bin`.

| Class | Result | Notes |
|---|---|---|
| ArCreditControlE2ETests | 18/18 | incl. no-body confirm (no 415), tasks 124–126, Off/Warn/Block, override, concurrency (3 rounds) |
| ArApAgingE2ETests | 5/5 | hand totals, custom buckets (+ FSD `0,…` spelling), DSO 108.0 / DPO, USD@280 base totals |
| ArDunningE2ETests | 10/11 | QA-3 (reset on profile read) open; seeds green after the QA-1 fix |
| ArStatementE2ETests | 7/7 | PDF, email capture, batch minimum, vendor reconciliation, per-currency sections |
| ApMatchingE2ETests | 12/12 | after QA-1 fix |
| ApPaymentRunE2ETests | 10/10 | 5 vendors / 15 items, discount 3-line journal, concurrent proposals, CSV |
| ArApMemoE2ETests | 6/6 | AR credit memo Dr revenue / Cr receivable; AP debit memo Dr payable; partial / split application |
| ArApSecurityE2ETests | 2/2 | 43 actions × 20 codes = 860 calls, 0 wrong; no-code 403, admin passes, anonymous 401 |
| ArApIsolationE2ETests | 1/1 | 0 leaks over 37 reads/writes/lists |
| ArApPerformanceE2ETests | 2/2 | aging 50 001 invoices: 2.0 s (one customer + details 1.6 s, summary 1.4 s); run 200 vendors/500 items: propose 1.3 s, execute 9.1 s (background, D-AP-13) |

Mail: the shared factory now records every `IEmailSender` send (`SapWebApplicationFactory.Mail`); A48 classes set `Mail.Accept = true`, all others keep the disarmed sender.
Findings QA-1..QA-4: ADDENDUM-48-ANALYSIS.md, section "QA — findings".

Regression (2026-10-25, one class at a time): SapAlignment x6, MultiCurrency.MultiCurrencyDocuments, FinancialPosting x9, JournalEngine x5 — 21 classes, 105/105 green.

## Final run (2026-10-25, after the REV fixes; fresh build, one class at a time)

| Class | Result |
|---|---|
| ArCreditControlE2ETests | 18/18 |
| ArApAgingE2ETests | 5/5 |
| ArDunningE2ETests | 11/11 (QA-3 fixed) |
| ArStatementE2ETests | 7/7 |
| ApMatchingE2ETests | 12/12 |
| ApPaymentRunE2ETests | 10/10 |
| ArApMemoE2ETests | 6/6 |
| ArApSecurityE2ETests | 2/2 (860-call matrix, 0 wrong) |
| ArApIsolationE2ETests | 1/1 (0 leaks) |
| ArApPerformanceE2ETests | 2/2 — aging 50 001 invoices 3.1 s, one customer + details 2.6 s, summary 2.4 s; run 200 vendors / 500 items: propose 3.4 s, execute 28.0 s (202 + background) |
| ArApRound2E2ETests | 9/9 — 202 + polling, resume of a stuck run, cancel of an Executing run, discount-account 409, matching reject + search, debit-memo reads, memo-only open items, 10 parallel supplier invoices → distinct numbers |
| ArApRevFixE2ETests | 7/9 — REV-1/2, REV-4 (past date; late run pays today without the expired discount), REV-6, REV-8, REV-9 (timeline entries in the run's org only), REV-7 (resume re-publishes; posting rows / GL journals / QBO maps / outbox / QBO writes unchanged) green; **QA-5** (REV-3 at the till), **QA-6** (REV-21 settled-meanwhile) open |
| Regression (21 classes) | 105/105 |
| SMS.Modules.Finance.Tests | 2658/2658 (LocalDB classes included, no timeouts) |

## Final confirmation (2026-10-26, after D-AR-14 / D-AP-23; fresh build, one class at a time, no other agent running) — ALL GREEN

| Suite | Result |
|---|---|
| ArCreditControlE2ETests | 18/18 |
| ArApAgingE2ETests | 5/5 |
| ArDunningE2ETests | 11/11 |
| ArStatementE2ETests | 7/7 |
| ApMatchingE2ETests | 12/12 |
| ApPaymentRunE2ETests | 10/10 |
| ArApMemoE2ETests | 6/6 |
| ArApSecurityE2ETests | 2/2 |
| ArApIsolationE2ETests | 1/1 |
| ArApPerformanceE2ETests | 2/2 — aging 50 001 invoices 1.07 s (one customer + details 1.06 s, summary 1.03 s); run 200 vendors / 500 items: propose 1.26 s, execute 10.5 s (202 + background) |
| ArApRound2E2ETests | 9/9 |
| ArApRevFixE2ETests | 9/9 (QA-5, QA-6 fixed) |
| Regression (SapAlignment ×6, MultiCurrency documents, FinancialPosting ×9, JournalEngine ×5) | 105/105 |
| Pos.PosE2ETests + Pos.PosReturnsE2ETests | 16/16 |
| SMS.Modules.Finance.Tests | 2660/2660 |
| SMS.Modules.Pos.Tests | 119/119 |

Findings QA-1..QA-6: all fixed and re-tested (ADDENDUM-48-ANALYSIS.md, QA section). Tracker row 583–596 closed.
