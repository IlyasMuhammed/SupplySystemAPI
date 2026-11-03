# Addendum 48 — task tracker

Register: `Documents/SMS_Task_Register_Addendum_48_Checklist.md` (001–629). Decisions: ADDENDUM-48-ANALYSIS.md.
Status: `[ ]` pending, `[~]` in progress, `[x]` done, `[-]` not applicable (reason). Owner in brackets.

| Tasks | Item | Owner | Status | Note |
|---|---|---|---|---|
| 001–030 | PartnerFinancialProfiles | CORE | [x] | D-4, D-CORE-3/9; entity/map/service/`api/finance/partner-profiles` + `ar-ap/settings`; 078 rollback = Down tested; tests A48PartnerProfileTests, A48MigrationTests |
| 031–042 | Partner lookup | CORE | [x] | D-3, D-CORE-10; `IPartnerLookupService` (SMS.Shared) in Suppliers; PartnerLookupServiceTests |
| 043–058 | Tolerance rules, early-payment terms (schema) | CORE | [x] | schema + CHECK/UQ proven on LocalDB; DTOs (054) are AP's |
| 059–069 | CreditHoldRequests (schema) | CORE | [x] | AR-3 actions; DTO (066) is AR's |
| 070–083 | StatementLog, Phase-1 migration checks | CORE | [x] | one guarded migration (D-CORE-1); no cross-schema FK asserted |
| 084–126 | Credit control + SO/invoice integration | AR | [x] | D-5, D-AR-1..6; `ArAp/Receivables/CreditControlService.cs` (ICreditCheck), `CreditExposureReader.cs`, `CreditRules.cs`; Demand confirm (`SaleOrderService`, `SaleOrdersController`) + `SalesInvoiceService` warning; 121 = 409 ConflictException (no new exception type); tests A48CreditRulesTests, A48CreditControlServiceTests, A48CreditLockSqlServerTests (LocalDB), Demand A48SaleOrderCreditCheckTests; 124–126 HTTP-level = QA |
| 127–159 | Aging AR/AP, DSO/DPO, indexes, perf | RPT | [x] | D-RPT-1/4/5; FSD GL indexes 155–157 → [-] (no GL reads, D-1), document indexes optional (SCHEMA-REQUESTS RPT-1/2); perf: AR 10k 0.3–0.7 s, 50k 1.3–1.9 s, AP 10k 0.2–0.3 s (LocalDB) |
| 160–193 | Dunning schema | CORE | [x] | AR-4; one live run per day by filtered unique index |
| 194–243 | Dunning service, templates, charge | AR | [x] | D-AR-7..10; `DunningService.cs`, `DunningRules.cs`, `DunningTemplateEngine.cs`, charge `ArDocuments.cs` (DunningChargeWriter, a DUNNING_CHARGE sales invoice, not a JE — 240/241 per D-10); tests A48DunningRulesTests, A48DunningServiceTests |
| 244–270 | Statements + PDF | RPT | [x] | D-RPT-7/8; PDF via PdfRenderGate, statement_log, email, batch |
| 271–290 | Match schema | CORE | [x] | D-CORE-5 (invoice = header, invoice_match_lines), D-CORE-6 |
| 291–332 | Matching service, override, approve | AP | [x] | D-7, D-AP-1..5; journal-entry steps (307/327/328) = existing approval path (D-7) |
| 333–350 | PaymentSchedules schema | CORE | [x] | D-CORE-8; backfill in the migration |
| 351–382 | Schedules, early discount, cash flow | AP | [x] | D-8, D-AP-6 |
| 383–414 | Payment run schema | CORE | [x] | AP-3; payment-line DiscountAmount (AP-1) |
| 415–475 | Payment run proposal/review/execute/export | AP | [x] | D-9, D-15, D-AP-7..9, D-AP-11; JE/reconcile steps = SupplierPayment + A44 (A-9); PaymentRunCompletedEvent (463) not raised — no consumer (residual) |
| 476–487 | Seeds | CORE | [x] | D-16, D-CORE-12 (migration + ArApDefaultsSeeder) |
| 488–503 | AR API | AR + RPT | [~] | AR part done: #10/#11 credit-memo routes in `ArControllers.cs` (`ArCreditMemoController`, + applications, open-credits, GET memo); permissions per contract §0.2; validation in services (A-3, no FluentValidation); #1–#9 = RPT; RPT part done: #1–#9 (ArReportsController) + 515–518 AP read side (ApReportsController: aging, DPO, summary, vendor balance/open items/transactions/statement, reconciliation) |
| 504–514 | Credit + dunning API | AR | [x] | `CreditControlController`, `DunningController` (contract §2/§3); permission codes from CORE (506/513); service validation; A48ArControllerTests |
| 515–538 | AP, matching, payment run, schedule, terms, profile API | AP (+CORE profile) | [x] | AP: §6–§9 controllers (ArApPayablesControllers.cs); #28–35 RPT, #60–61 CORE |
| 539–582 | UI | FE | [x] | D-18, D-FE-1..17; 15 pages (incl. credit / debit memo screens, round 2) + profile section on customer/supplier detail + SO-confirm credit block / credit warnings; round 2 aligned to the built models + FE-1..7 answers (2026-10-25); round 3: FE-8 wired + frontend review fixes + REV-24 (D-FE-18); REV round (D-FE-19); FE suite 3581 green, prod build green; open: FE-9 (optional summary trends), FE-10 (debit-memo currencyCode in the model) |
| 583–596 | Integration / perf / isolation tests | QA | [x] | tests/SMS.Integration.Tests/ArAp/ — 13 classes, 92/92 green (2026-10-26, fresh build, one class at a time); QA-1..6 fixed + re-tested; regression 21 classes 105/105 + Pos 16/16; Finance unit 2660/2660; Pos unit 119/119; task 628 rule map 47/47 (QA-PLAN §12) |
| 597–606 | AR/AP inquiry services | RPT | [x] | D-RPT-2/3/6; computed from Finance documents, not GL lines |
| 607–612 | Credit / debit memo apply | AR (credit), AP (debit) | [x] | AR 607–609 done (D-AR-11/12, `ArCreditMemoService.cs`, CREDIT_NOTE document + application rows, not a JE); A48CreditMemoTests (TS-CM-001..003); AP 610–612 done (D-AP-10, `ApDebitMemoService.cs`, TS-CM-004/005) |
| 613–617 | Vendor reconciliation | RPT | [x] | D-RPT-9 |
| 618–629 | Docs | all | [ ] | |
