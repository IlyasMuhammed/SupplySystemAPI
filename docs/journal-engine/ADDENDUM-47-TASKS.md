# Addendum 47 — task tracker

Register: `SMS_Task_Register_Addendum_47_Checklist.md` (tasks 001–468). Decisions: ADDENDUM-47-ANALYSIS.md (Part A/B).
Status: `[ ]` pending, `[~]` in progress, `[x]` done, `[-]` not applicable (reason given).

## Phase 1 — Foundation

| Tasks | Item | Status | Note |
|---|---|---|---|
| 001–015 | M1 JournalTypes entity, map, indexes, CHECKs | [x] | CORE. Domain/AccountingEntities.cs, Data/Maps/JournalMaps.cs; kind as string + CHECK (A-5); unique code + prefix (D-CORE-5) |
| 016–026 | M8 seed 7 system types (setup completion + existing orgs) | [x] | CORE. JournalTypeSeeder (event + on demand) + migration for Complete orgs and orgs with journals (D-CORE-1); one Opening/Closing = filtered unique indexes |
| 027–033 | M2 AdjustmentOnly period status + guard | [x] | CORE. CK replaced; JournalPeriodResolver + FiscalPeriodGuard; A42 PUT fiscal-periods accepts AdjustmentOnly |
| 034–069 | M3 JournalEntries | [x] | CORE. ALTER + backfill (type, period, Reversed pairs, flags, counts); 3 filtered unique key indexes (D-5) |
| 070–099 | M4 JournalEntryLines | [x] | CORE. ALTER + backfill; + line Uuid (D-CORE-8); OpenItems filter = Reconciled = 0 (no OR in filtered indexes) |
| 100–109 | M5 JournalEntrySequences | [x] | CORE. Type/fiscal year nullable + SequenceYear (REC numbers, outside-year drafts) |
| 110–118 | M6 ReconcileGroups | [x] | CORE (schema); + AccountId |
| 119–122 | M7 HasTransactions trigger | [-] | Dropped: set by the application (A-11, D-10); 121–122 become unit tests of the balance mover |
| 123–133 | Migration order, clean + SMSGlobal-shaped DB, Down, tenant filter | [x] | CORE. One guarded migration A47_JournalEngine, M-sections in spec order (D-CORE-3); LocalDbJournalEngineTests (clean, A44-shaped + rerun, Down/Up, no pending model changes, no cross-schema FK) |

## Phase 2 — Core engine

| Tasks | Item | Status | Note |
|---|---|---|---|
| 134–145 | Sequence service + tests TS-SQ-001..005 | [x] | CORE. UPDLOCK/HOLDLOCK batch; TS-SQ-004 on LocalDB (20 concurrent) |
| 146–156 | DTOs / validation | [x] | CORE. Models/JournalModels.cs; 155–156 [-] no FluentValidation (A-18), service validation with JE codes |
| 157–191 | CreateAsync + TS-JE-001..015 | [x] | CORE. JournalEntryWriter + JournalEntryService; JournalEngineTests |
| 192–204 | PostAsync + TS-JE-016..021 | [x] | CORE. Conditional Draft→Posted switch; GlBalanceMover (D-9) |
| 205–207 | CreateAndPostAsync | [x] | CORE |
| 208–218 | UpdateDraft / DeleteDraft | [x] | CORE. Manual drafts only (D-CORE-10) |
| 219–228 | GetById / List | [x] | CORE |
| 229–241 | Journal entry API | [x] | CORE. JournalEntriesController (+ /reverse route delegating to RR), [RequiresGlSetup], [JournalErrorFilter] |
| 242–250 | Journal type API | [x] | CORE. JournalTypesController; + PATCH activate, DELETE custom unused (D-CORE-11) |
| 251–263 | Journal entry UI | [x] | FE: `gl/journal-entries` list (`GlJournalEntriesComponent`), `/new` + `/:uuid/edit` form (`GlJournalEntryFormComponent`), `/:uuid` detail with Post / Reverse / Delete (`GlJournalEntryDetailComponent`); D-FE-3..7 |
| 264–269 | Journal type UI | [x] | FE: `gl/journal-types` (`GlJournalTypesComponent`), lock icon, deactivate confirm, activate, delete unused custom types |

## Phase 3 — Reversal & reconciliation

| Tasks | Item | Status | Note |
|---|---|---|---|
| 270–286 | ReverseAsync + API + TS-RV-001..006 | [x] | RR: `JournalReversalService` (IJournalReversalService, wired into CORE's ReverseAsync + `POST journal-entries/{uuid}/reverse`, GL_JOURNAL_REVERSE); D-14, D-RR-1..4; tests `ReversalReconciliationTests` + `LocalDbReversalReconciliationTests` |
| 287–308 | Reconciliation service + TS-RC-001..007 | [x] | RR: `JournalReconciler` + `ReconciliationService` (IReconciliationService); D-16, D-RR-5..8; partial = oldest first (contract) |
| 309–314 | Reconciliation API | [x] | RR: `ReconciliationController` api/finance/reconciliation (open-items, suggest, reconcile, {uuid}, {uuid}/unreconcile), GL_RECONCILE_VIEW / _EXECUTE, `[RequiresGlSetup]` |
| 315–321 | Reconciliation UI | [x] | FE: `gl/reconciliation` (`GlReconciliationComponent`); paged groups list from C-8 with Unreconcile (D-FE-8) |

## Phase 4 — Reports & period close

| Tasks | Item | Status | Note |
|---|---|---|---|
| 322–341 | Trial balance service + TS-TB-001..006 | [x] | RPT: `Services/Reports/TrialBalanceService.cs`; SQL GROUP BY; tests `TrialBalanceTests` (15) — D-17, D-RPT-1..3 |
| 342–346 | Trial balance API | [x] | RPT: `TrialBalanceController` (trial-balance, /export CSV, accounts/{uuid}/balance+ledger on both route forms, D-RPT-9); GL_REPORTS_VIEW |
| 347–355 | Trial balance UI | [x] | FE: `gl/trial-balance` (`GlTrialBalanceComponent`) + ledger drawer (`GlAccountLedgerComponent`); D-FE-9 |
| 356–384 | Period close service + TS-PC-001..011 | [x] | RPT: `Services/PeriodClose/PeriodCloseService.cs`; closing JE through `JournalEntryWriter`; tests `PeriodCloseTests` (19) — D-RPT-4..8 (D-RPT-5 = contract change C-5, needs CORE) |
| 385–392 | Period close API | [x] | RPT: `PeriodCloseController` (period-close status/close/reopen/final-close, year-end-close preview/execute); VIEW/EXECUTE/ADMIN |
| 393–401 | Period close UI | [x] | FE: `gl/period-close` (`GlPeriodCloseComponent`), preview + execute + summary dialogs; D-FE-10 |

## Phase 5 — Gateway integration & testing

| Tasks | Item | Status | Note |
|---|---|---|---|
| 402–414 | Resolver + InternalGlPostingTarget delegates to the engine | [x] | GW: `Posting/InternalGlPostingTarget.cs` → `JournalEntryWriter` (post, update, void), D-12 types, per-type numbers, partner on AR/AP lines, reversal marks Reversed + auto-reconciles; error mapping (411); data fix migration `A47_GatewayReversalReconcile`. 402 [-] no interface (CORE's concrete `JournalTypeResolver`), 412/413 CORE. 414: gateway contract unchanged. D-GW-1..8; tests `GatewayJournalEngineTests` (45), `LocalDbGatewayJournalTests` (3) |
| 415–420 | Integration TS-GW-001..006 | [x] | QA: `JournalEngine/JeGatewayE2ETests` 8/8 on LocalDB after C11 (QA-PLAN.md §1): TS-GW-001..006 + D-12 resolver + A44 update/void → Reversed |
| 421–426 | E2E | [x] | QA: `JournalEngine/JeCycleE2ETests` 8/8 green on LocalDB (QA-PLAN.md §2), incl. the REV-1 test (FY+1 opens with P&L 0) |
| 427–431 | Performance | [x] | QA: `JournalEngine/JePerformanceE2ETests` 5/5 (QA-PLAN.md §5): TB 10k entries 625 ms, ledger 5k lines 245/439 ms, 10 parallel creates 301 ms, list 50k filtered 1.2 s, year-end 120 accounts 115 ms + 994 ms. RPT 427/428 done in `LocalDbReportTests`: 12,150 entries / 24,300 lines — year TB 59–168 ms, Oct TB 103–263 ms, CSV ≤ 248 ms, 6,000-line ledger p1 ≤ 521 ms, last page 340 ms (was 46 s with SUM over TOP n, fixed), balance ≈ 110 ms |
| 432–439 | Security | [x] | QA: `JournalEngine/JeSecurityE2ETests` 8/8 (QA-PLAN.md §4) — D-15 matrix (13 codes × 22 actions), org isolation (404), licence read-only, setup gate; QA-1 fixed by CORE |
| 440–456 | Business-rule tests | [x] | QA (E2E side): `JournalEngine/JeRulesE2ETests` 10/10 (QA-PLAN.md §3); unit side = CORE/RR/RPT |

## Phase 6 — Documentation & cleanup

| Tasks | Item | Status | Note |
|---|---|---|---|
| 457–468 | Swagger, XML docs, error catalog, A44 note, permission seed, reviews, smoke | [~] | 457–461 [x] (GW, D-GW-8: `[ProducesResponseType]` + summaries on the five A47 controllers, interface XML docs); 462 error catalog [x] (API-CONTRACT §0); 463 [x] (A44 analysis "A47 changes"); 464 permission seed [x] (CORE, D-15: PermissionCodes, AuthDataSeeder, GlPermissionSeedTests); 465–467 [x] (QA code review: AccountingDbContext only, no cross-schema SQL, every IgnoreQueryFilters query org-filtered or child-of-org-scoped — analysis QA section; REV security pass agrees). 468 smoke on staging = user step |
