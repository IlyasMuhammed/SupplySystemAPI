# Addendum 47 — QA plan (E2E, gateway integration, security, performance)

GW ready: 2026-10-24 (C11 done — gateway journals through the engine, D-GW-1..8; GW's own run of `JeGatewayE2ETests` 8/8 green on LocalDB. Note D-4: A44 originals are now `Reversed` at write time, so "posted" filters must read `Posted` or `Reversed` — `FpKit.NetAsync` changed accordingly.)

Owner: agent QA. Tasks 415–439 and the E2E side of 440–456. Everything runs through the real `SMS.API` host
(`SapWebApplicationFactory`, the A44 factory) on a throwaway LocalDB database (`SMS_SAP_E2E_*`, dropped on dispose); only
Intuit is faked (`FakeQuickBooksCompany`). Never SMSGlobal: every class calls `ShouldBeOnLocalDb()` first.

Code: `tests/SMS.Integration.Tests/JournalEngine/` — `JeKit.cs` (shared helpers on top of `Fp`/`Gl`/`SapKit`) and one class per
area below. Routes, bodies and messages follow `docs/journal-engine/API-CONTRACT.md` (it wins over the FSD). Messages quoted
below are the contract's (`JournalMessages` in `SMS.Modules.Accounting/Services/Journals/JournalErrors.cs`) and the error code
travels as `result.errorCode`.

Run one class at a time (never the whole project):

```
dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~JournalEngine.<ClassName>
```

LocalDB needs a short artifacts path (`--artifacts-path %TEMP%\a47qa`) or SNI.dll fails with "filename too long".

Every test creates its own organization (`root.NewOrgAsync`), completes the A42 setup (US_GENERAL, fiscal year = current
calendar year, opening skipped), and so starts from an empty ledger. "Today" is the UTC date; all entries are dated inside
the current fiscal year unless the case says otherwise.

## 1. Gateway integration — `JeGatewayE2ETests` (415–420, TS-GW-001..006)

| Case | Method | Setup | Expected |
|---|---|---|---|
| TS-GW-001 / 415 | `TS_GW_001_sale_invoice_through_the_gateway_is_a_SALE_journal` | GL on; SO → delivery → invoice issued (real screens) | One journal for the invoice: journal type SALE, number `SALE/{FY start year}/0001` (`^SALE/\d{4}/\d{4,}$`), fiscal period = the period holding the invoice date, Posted, balanced, AR = grand total; posting row internal_gl Posted. A second invoice gets `/0002`. |
| TS-GW-002 / 416 | `TS_GW_002_purchase_bill_through_the_gateway_is_a_PURCH_journal` | GL on; approved supplier invoice | Journal type PURCH, `PURCH/{yyyy}/0001` (own sequence, TS-SQ-003), AP credited. |
| TS-GW-003 / 417 | `TS_GW_003_internal_GL_and_QuickBooks_outbox_both_get_it` | GL on + QBO Live (fake) | Journal created synchronously; posting row `internal_gl=Posted`, `quickbooks=Queued`; after the jobs the fake has the invoice (map Synced). |
| TS-GW-004 / 418 | `TS_GW_004_GL_off_only_the_outbox` | QBO Live, internal accounting off | No journal; quickbooks target Queued; internal_gl absent/Skipped. |
| TS-GW-005 / 419 | `TS_GW_005_closed_period_GL_fails_QuickBooks_row_still_written` | GL on + QBO Live, today's period **Closed** (and separately Locked) | Document still issued; no journal; internal_gl Failed/Pending with PERIOD_CLOSED (or the engine's "Period is closed"); QuickBooks map/outbox row written and synced by the jobs. |
| TS-GW-006 / 420 | `TS_GW_006_duplicate_idempotency_key_returns_the_existing_result` | GL on; synthetic `FinancialTransaction` posted twice in-process | Second call `DuplicateSkipped`, one journal, sequence not consumed (next journal is `/0002`, not `/0003`). |
| D-12 resolver | `Resolver_maps_every_document_kind_to_its_journal_type` | synthetic posts of Payment (cash line / bank line), BillPayment, CreditNote, VendorCredit, InventoryAdjustment, ManualJournal | SALE / PURCH / CASH / BANK / MISC (prefix `JV`) per D-12; a deactivated SALE type falls back to MISC. |
| A44 update/void (D-4, D-19) | `A44_update_and_void_mark_the_original_Reversed` | synthetic post, changed payload, then void | Original `Reversed` with `reversedByEntry` set; reversal points back (`reversesEntry`). |

## 2. End-to-end cycles — `JeCycleE2ETests` (421–426)

| Case | Method | Expected |
|---|---|---|
| 422 | `E2E_422_manual_entry_create_post_trial_balance` | POST journal-entries (Draft, MISC `JV/yyyy/0001`, totals 1000) → post → trial balance for the year lists both accounts with 1000 Dr / 1000 Cr, totals equal, `isBalanced`. Draft not in TB before posting (TS-TB-006). |
| 421 + 425 | `E2E_421_425_full_cycle_lock_year_end_close_P_and_L_zeroed` | Revenue 5000 and expense 3000 posted; lock periods 1–12 (period close API); preview shows the revenue and expense accounts and net income 2000 (TS-PC-009); execute → closing JE type CLOSE, `IsClosingEntry`, revenue Dr 5000, expense Cr 3000, retained earnings Cr 2000 (TS-PC-006); fiscal year closed; all periods Closed; TB with `includeClosingEntries=true` shows every revenue/expense account at net 0; TB default excludes it (TS-TB-003); a second execute returns the same entry (TS-PC-008); a new entry in the year → "Fiscal year is closed" (TS-PC-010). |
| TS-PC-011 | `Year_end_close_with_a_net_loss_debits_retained_earnings` | Revenue 1000, expense 1500 → retained earnings Dr 500. |
| TS-PC-007 | `Year_end_close_refused_while_a_period_is_open` | "All periods must be Locked or Closed". |
| 424 | `E2E_424_reverse_restores_balances_and_auto_reconciles` | Post, reverse with reason "Incorrect amount" → original Reversed, mirror Posted, `ReversalReason` stored, description "Reversal of {number}: Incorrect amount" (TS-RV-006), CoA `currentBalance` back to before, both lines on each account reconciled with residual 0 (TS-RV-005, BR-RV-008); second reverse → "Entry has already been reversed" (TS-RV-002); reverse a Draft → "Only posted entries can be reversed" (TS-RV-003); reversal date in a Closed period → "Reversal period is closed" (TS-RV-004). |
| 423 | `E2E_423_invoice_and_payment_reconcile` | Real sales invoice (gateway) + customer payment → open items on AR show both lines; suggest for the invoice line returns the payment line; reconcile → Full, residual 0; unreconcile → residual restored, group gone (TS-RC-004). Partial payment → Partial, invoice residual = rest (TS-RC-002). |
| 426 | `E2E_426_multi_currency_entry_local_amounts_and_trial_balance` | USD entry, rate 3.67, 1000 Dr / 1000 Cr → lines `debit = 3670.00` base, `amountCurrency = 1000`, `currencyCode USD`, `exchangeRate 3.67` (TS-JE-015); TB in base shows 3670; no rate → "Exchange rate required for foreign currency" (TS-JE-014). |
| REV-1 | `REV_1_next_year_opens_with_P_and_L_zero_after_the_year_end_close` | FY Y: revenue 5000, expense 3000, lock 1–12, year-end close; FY Y+1 created, revenue 100 posted → FY Y+1 TB (by `fiscalYearUuid` and by dates, default flags): revenue/expense opening 0, retained earnings opening −2000, AR +5000 / cash −3000 carried, revenue balance −100, opening totals equal; revenue ledger Y+1 opening 0, RE ledger opening −2000; FY Y TB still shows revenue −5000 (BR-TB-004). |

## 3. Business rules over HTTP — `JeRulesE2ETests` (440–456, E2E side)

One test per rule group; each refusal asserts the HTTP status (400 validation, 409 state), `result.errorCode` and the exact message.

| Rule | Method | Expected message / code |
|---|---|---|
| BR-JE-001 (440) TS-JE-002 | `BR_JE_001_to_003_line_shape_rules` | "Entry is not balanced" JE-001 |
| BR-JE-002 (441) TS-JE-012 | same | "Minimum 2 lines required" JE-002 |
| BR-JE-003 (442) TS-JE-003 | same | "Line cannot have both debit and credit" JE-003 |
| BR-JE-004/005 (443–444) TS-JE-004..007, 013 | `BR_JE_004_005_period_rules` | Closed → "Period is closed" JE-006; Locked → "Period is locked" JE-007; AdjustmentOnly + regular → "Period accepts only adjustment entries" JE-008; AdjustmentOnly + closing entry → created; date outside every year → "No fiscal period covers this date" JE-005 |
| BR-JE-006 (445) TS-JE-009/010 | `BR_JE_006_account_must_be_a_leaf_and_active` | "Account is not postable" JE-009; "Account is inactive" JE-010 |
| BR-JE-007 (446) TS-JE-008 | `BR_JE_007_same_client_request_id_returns_the_existing_entry` | same `clientRequestId` → same uuid and number, one row |
| BR-JE-008/009/010 (447–449) TS-JE-016..021 | `BR_JE_008_to_010_posting_and_immutability` | post → Posted, postedAt, CoA balances moved, hasTransactions; post again → "Entry is already posted" (409); PUT/DELETE of a posted entry → "Posted entries cannot be modified" / "…deleted"; number unchanged after PUT of a draft; draft period locked before post → "Period is locked" (TS-JE-019); post a Reversed entry → "Entry has been reversed" (TS-JE-018) |
| BR-JE-011 (450) TS-JE-011 | `BR_JE_011_sale_type_requires_a_partner` | "Sales entries require a partner" JE-011 (manual only, D-13) |
| BR-JE-012/013 (451–452) | in `E2E_426_…` | see 426 |
| TS-SQ-001..005 | `Sequences_are_per_type_and_never_reused` | SALE/…/0001, 0002, PURCH/…/0001; delete a draft then create → next number (no gap backfill) |
| BR-RV-* (453) | in `E2E_424_…` | see 424 |
| BR-RC-* (454) TS-RC-001..007 | `BR_RC_reconciliation_rules` | different accounts → "All lines must belong to the same account"; draft line → "Only lines from posted entries can be reconciled"; one line → refused; 3 invoices vs 1 payment → Full over 4 lines (TS-RC-003); a line already in a group → refused (BR-RC-008) |
| BR-PC-* (455) TS-PC-001..005 | `BR_PC_period_close_rules` | lock with 2 drafts → "Period has 2 unposted draft entries"; lock → Locked; reopen → Open; final-close Locked → Closed; reopen Closed → "Closed periods cannot be reopened" |
| BR-TB-* (456) TS-TB-001..006 | `BR_TB_trial_balance_rules` | only posted lines; totals equal; opening column = lines before from date; zero-activity accounts hidden; account balance as of a date; ledger with running balance |

## 4. Security — `JeSecurityE2ETests` (432–439)

Users are created in the test organization with exactly one role holding the listed codes (`LoginWithPermissionsAsync`).
"Forbidden" = HTTP 403. Each code is checked against every journal-engine endpoint (allowed → not 403/401; refused → 403).

| Task | Method | Check |
|---|---|---|
| 432 | `GL_VIEW_reads_but_cannot_create_post_or_reverse_432` | GL_VIEW: GET list/detail/types OK, detail `canPost/canReverse/canEdit` false; POST create, POST post, POST reverse, PUT, DELETE → 403 |
| 433–434 | `GL_JOURNAL_CREATE_cannot_post_or_reverse_433_and_POST_cannot_create_or_reverse_434` | CREATE: create OK, post/reverse 403, `postImmediately` and an auto-post type 403 "You do not have permission to post journal entries."; POST: post OK, create/reverse 403; REVERSE/EDIT/DELETE each only their action; `createdByName`/`postedByName` set |
| 435–436 | `Period_close_permissions_435_436` | VIEW: status + preview only; EXECUTE: lock/reopen, not final-close/execute; ADMIN: final-close + execute pass the gate |
| 437 | `GL_SETUP_MANAGE_required_for_journal_type_crud_437` | create/update/deactivate/delete 403 for GL_VIEW and GL_JOURNAL_CREATE, OK for GL_SETUP_MANAGE; system type delete/code change and duplicate code / second Opening type refused with the contract texts |
| — | `Each_D15_code_allows_exactly_its_actions` | matrix: each of the 12 D-15 codes + GL_SETUP_MANAGE alone × 22 actions, per contract JE-READ / §1–§6; an unrelated code refused everywhere |
| 438–439 | `Another_organization_sees_nothing_438_439` | org B: A's entry/type/period/year/account/lines by uuid → 404 (never 403); A's type/account in B's create → JE-013 / JE-009; B's list/TB/types hold only B's rows; sequences per org |
| — | `No_licence_or_no_setup_refuses_the_engine` | setup not Complete → 403 `FINANCE_SETUP_REQUIRED`; FEATURE_GENERAL_LEDGER off → A37 D-6 read-only: reads 200, writes 403 `MODULE_NOT_LICENSED` |
| — | `Org_admin_role_holds_every_D15_code` | the codes are seeded; the org admin reverses, reconciles, locks, final-closes, reads the TB |

Contract C-1: reverse answers `{ reversal, original, reconcileGroupNumbers, warning? }`. Contract C-4: the reconciliation 404s
(RC-005, RC-006) carry no `result.errorCode`. Results, timings and findings: the "QA" section of ADDENDUM-47-ANALYSIS.md.

## 5. Performance — `JePerformanceE2ETests` (427–431)

Bulk data is inserted with SQL into the throwaway database (posted, balanced journals spread over the year's periods), then
the API is timed. Timings are written to the test output and to the QA section of the analysis doc; a test fails only when a
call is grossly slow (> 30 s) or wrong.

| Task | Method | Data | Measured |
|---|---|---|---|
| 427 | `Perf_427_trial_balance_10k_entries` | 10 000 entries × 2 lines | GET trial-balance for the year |
| 428 | `Perf_428_account_ledger_5k_lines` | 5 000 lines on one account | GET accounts/{uuid}/ledger |
| 429 | `Perf_429_ten_parallel_creates_get_unique_numbers` | 10 parallel POST journal-entries (TS-SQ-004) | all succeed, 10 distinct consecutive numbers |
| 430 | `Perf_430_list_with_filters_50k_entries` | 50 000 entries | GET journal-entries with status + type + date range + account + search |
| 431 | `Perf_431_year_end_close_100_pl_accounts` | 120 revenue/expense leaves with activity | preview + execute year-end close |

## 6. Regression

The A44 / A42 classes, one at a time: `FinancialPosting.*` (GlPostingE2ETests, PostingGatewayE2ETests, PostingApiE2ETests,
FinancialJourneyE2ETests, CoverageAndMappingE2ETests, SyncControlE2ETests, SyncLogAndBySourceE2ETests, EntitySyncE2ETests,
QuickBooksDocumentsE2ETests) and `GeneralLedger.*` (GlSetupE2ETests, GlChartE2ETests, GlAccessE2ETests). D-19: only the
journal number format may change.

## 7. Not testable here

- UI pages (FE) — not covered by these HTTP tests.
- Staging smoke (task 468) — user step.
