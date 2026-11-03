# Addendum 49 — QA plan

Environment: LocalDB only, never SMSGlobal. Never start SMS.API; never `dotnet ef database update`. Build to an artifacts path outside the repo (`dotnet test … --artifacts-path %TEMP%\a49x`): the running debugger locks `src\SMS.API\bin\Debug\net8.0`, and LocalDB tests need a short path (SNI.dll "filename too long"). `SMS.Integration.Tests` one class at a time only. No real email (`CapturingEmailSender`). QuickBooks: fake gateway in E2E; a real Intuit sandbox run needs the user's keys. Say plainly when it was not run.

Unit tests per module sit in `tests/SMS.Modules.Hrm.Tests` (agents add a folder each). E2E over HTTP: `tests/SMS.Integration.Tests/Hrm/`.

## Coverage map

| Area | Scenarios | Owner | Status |
|---|---|---|---|
| H1 org + employee | Tasks 010, 018, 024, 045–048, 061, 066 | CORE | unit + LocalDB green (`OrgStructureTests`, `EmployeeTests`, `ScopeAndSeedTests`, `A49MigrationTests`) |
| Migrations | Up, down, up; history lost; index missing; snapshot clean; no cross-schema FK | CORE | green for hrm, Accounting keys, Integration mappings |
| Module registry | not in any plan; off for existing orgs; idempotent start | CORE | green (`HrmModuleCatalogTests`) |
| Roles | HR Manager, HR Officer, grants, off-stays-off, System Admin excludes sensitive | CORE | green (`HrmPermissionSeedTests`) |
| Enablement | TS: 422 `PAYROLL_COST_CENTER_REQUIRED`; inline create; inline create without ORG_MANAGE 403; script path; deactivate payroll center 409 | POST, QA | open |
| H2 | Late, early, OT, half-day, absent, holiday skip, idempotent event, CSV import errors per row, leave balance = Σ ledger, insufficient, overlap, cancel restores, carry-forward cap, bulk allocation idempotent, workflow approve path | ATT, QA | open |
| H3 | Formula grammar, malicious input (`System.IO`, `;`, huge depth), SLAB, effective-dated assignment, approval, band check, verify clears on edit | PAY, QA | open |
| H4 | Stage transitions, duplicate candidate, convert to employee atomic, weighted rating, enrollment | TAL, QA | open |
| H5 | 5-employee run by hand-computed figures; mid-month joiner; mid-month leaver; tax slab; loan installment; blocked line; run uniqueness; 500 employees < 30 s (task 416) | PAY, QA | open |
| H5F | Balanced matrix; rounding to DEFAULT_ROUNDING; G-21 unverified blocks finalize; pay date fallback; both dates closed; reversal releases loans/OT/adjustments; retry never double-posts | PAY, QA | open |
| H5P | All TS-G1..G5, G10..G16; employee name appears nowhere in a payload (assert over the serialized JSON + logs); 1200 lines → 2 balanced JEs; fresh connection off + hold; existing connection off + hold; currency mismatch | POST, QA | open |
| H6 / H6E | Installment schedule sums to principal; disbursement posting; settlement totals; increment applied; letter merge, PDF through the gate; TS-G9-*, TS-G13-001 | LIFE, QA | open |
| P3 | No target: finalize → Finalized, export CSV/XLSX, pay allowed, no error. Both `NoTargetConfigured` and `ModuleNotLicensed` | QA | open |
| P2 / P2-S | **Acceptance:** tenant with profile: service invoice → receipt → payroll → expense → balanced TB and P&L; selling and stock menus hidden; startup clean | POST, QA | open |
| Isolation (418) | Org A payroll, employee, attachment via every route 404 from Org B, incl. super admin rules | QA | open |
| Security (419, 420) | No S3 cleartext in DB (scan columns), API lists or logs; masked without code; reveal logged; Department / Branch / Team / Self scope on every employee-bound route of every agent | QA | open |
| Sensitive files | Bank file logs one access per employee; payslip for another employee 404; attachment route refuses HRM_PAYSLIP | QA | open |
| Regression | Finance, Accounting, Integration, Tenancy, Auth, WorkflowEngine, Crm, Pos suites; `SMS.Integration.Tests` regression classes | QA | open |
| Frontend | FE build + unit; sf-* kit; masked fields; no-permission hides actions; statutory unverified banner | FE | open |

## G-21 assertions QA must make

Seeded tax and rates: `IsVerified = 0`, name contains UNVERIFIED, Finalize blocks with their names. Any screen or PDF that shows such a value must say it is unverified. Nothing else may flip verification but `HRM_STATUTORY_VERIFY`. Moon holidays `IsEstimated = 1`.

## Reporting

Per class: name, count, pass or fail. Findings as `QA-n` in this file with severity and the owner messaged. A skipped case states why.

## Findings

| # | Severity | Finding | Owner | State |
|---|---|---|---|---|
