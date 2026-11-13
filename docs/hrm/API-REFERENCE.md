# HRM API reference (task 402)

For implementers and integrators. The tables below are generated from the running application's own route table and OpenAPI document, so they say what the controllers do. For request and response fields and error codes, read [API-CONTRACT.md](API-CONTRACT.md), and read [Differences from API-CONTRACT.md](#differences-from-api-contractmd) with it.

## OpenAPI / Swagger

`SMS.API` serves the document at `/swagger/v1/swagger.json` and the Swagger UI at `/swagger` (Swashbuckle 6.6.2).

All 262 HRM operations are in it, each with a one-line summary. Filter by controller name in the UI: they are tagged by controller, such as `PayrollRuns` and `Employees`.

**How the summaries get there.** Each action has `[EndpointSummary("...")]`. It is metadata only and changes no behaviour. XML doc comments do not reach Swagger: no project sets `GenerateDocumentationFile`, and `Program.cs` does not call `IncludeXmlComments`.

**What the document does not tell you:**

- **Response bodies.** No HRM action declares its response type, so every operation shows a bare `200`. Parameters and request bodies are shown. Use API-CONTRACT.md for responses.
- **Permissions and module gates.** Swagger shows only that a Bearer token is needed. The tables below give them.
- **Error codes.** API-CONTRACT.md lists them.

**Verification.** `tests/SMS.Integration.Tests/Hrm/HrmSwaggerDocumentTests.cs` builds the real host on a throwaway LocalDB and generates the document in-process, without starting SMS.API. It fails if an HRM action is missing from the document, has no summary, or shares a route with another (`Program.cs` resolves clashes by silently keeping the first). A new HRM action needs a summary, or this test fails.

Run it alone:

```
dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~HrmSwaggerDocumentTests
```

Set `SMS_SWAGGER_DUMP_DIR` to also write `swagger.json` and `routes.json` to a folder.

## Conventions

- Routes below are under `api/hrm`. Ids are uuids.
- Every route needs a login, `MODULE_HRM` on and the permission shown, except `setup/*` (no module gate; see that section). Holding any one code listed is enough. **and** means two checks must both pass.
- The `HRM_` prefix is left out of HRM permission codes: `EMPLOYEE_VIEW` means `HRM_EMPLOYEE_VIEW`. Codes from other modules are written in full and marked *(not HRM)*.
- A permission is not the last check. The service then applies the HR scope, and anything outside it is 404 (API-CONTRACT §1).
- Success returns `{ success, message, result }`. Coded errors start the message with the code. A few DELETEs (`user-scopes/{uuid}`, `employees/{uuid}/documents/{documentUuid}`, `holidays/{uuid}`) return the envelope with no `result`; the other DELETEs return `result: true`.
- **Files, not the envelope.** These return the raw file: `attachments/{uuid}/content`, `payroll-runs/{uuid}/bank-file`, `.../journal-export/csv`, `.../journal-export/xlsx`, `.../lines/{line}/payslip`, `payslips/{uuid}/pdf`, `GET offers/{uuid}/letter`, `letters/{uuid}/pdf`, `tax-certificates/{uuid}/pdf`.
- **Paged lists.** `result` is `{ data[], totalRecords, page, pageSize, totalPages, hasNext, hasPrevious }` (query `page` default 1, `pageSize` default 25) on exactly seven routes: `GET employees`, `GET attendance/days`, `GET leave/requests`, `GET payroll-runs`, `GET payroll-runs/{uuid}/lines`, `GET loans`, `GET expense-claims`. Every other list returns a plain array.
- **Other statuses.** `POST payroll-runs/{uuid}/calculate` returns 202 (envelope, `calculationQueued: true`) when it queues the job. `POST`/`PUT candidates` return 409 with the likely duplicates as `result`; send `force: true` to save anyway.

## Routes

### Setup and settings

Also needs: `settings` and `user-scopes/*` need `MODULE_HRM` on. `setup/*` (`HrmSetupController`) carries **no** module gate and skips the seed filter: the service checks the `MODULE_HRM` licence (403 when the plan does not include it, switch may be off) and seeds after that.

| Method | Route | Permission (any of) | What it does |
|---|---|---|---|
| GET | `/settings` | SETTINGS_MANAGE, ACCOUNT_MAPPING_VIEW, PAYROLL_VIEW | Get the organization's HR settings |
| PUT | `/settings` | SETTINGS_MANAGE | Update the organization's HR settings |
| POST | `/setup/payroll-cost-center` | SETTINGS_MANAGE (with `newCostCenter`, the service also needs ORG_MANAGE) | Enablement wizard: select or create the payroll cost center |
| GET | `/setup/status` | SETTINGS_MANAGE, `MODULES_MANAGE` *(not HRM)* | Enablement wizard: licence, switch and payroll cost center status |
| GET | `/user-scopes` | SETTINGS_MANAGE | List a user's extra HR scopes |
| POST | `/user-scopes` | SETTINGS_MANAGE | Give a user an HR scope on a branch or department |
| DELETE | `/user-scopes/{uuid}` | SETTINGS_MANAGE | Remove an HR scope |
| GET | `/user-scopes/users` | SETTINGS_MANAGE | Search active users for the HR scope picker |

### Organization

Also needs: nothing beyond `MODULE_HRM`.

| Method | Route | Permission (any of) | What it does |
|---|---|---|---|
| GET | `/branches` | ORG_VIEW, ORG_MANAGE, EMPLOYEE_VIEW, EMPLOYEE_EDIT | List branches |
| POST | `/branches` | ORG_MANAGE | Create a branch |
| GET | `/branches/{uuid}` | ORG_VIEW, ORG_MANAGE | Get a branch |
| PUT | `/branches/{uuid}` | ORG_MANAGE | Update a branch |
| PATCH | `/branches/{uuid}/active` | ORG_MANAGE | Activate or deactivate a branch |
| GET | `/cost-centers` | ORG_VIEW, ORG_MANAGE, SETTINGS_MANAGE, ACCOUNT_MAPPING_VIEW, EMPLOYEE_EDIT | List cost centers |
| POST | `/cost-centers` | ORG_MANAGE | Create a cost center |
| PUT | `/cost-centers/{uuid}` | ORG_MANAGE | Update a cost center |
| PATCH | `/cost-centers/{uuid}/active` | ORG_MANAGE | Activate or deactivate a cost center |
| GET | `/departments` | ORG_VIEW, ORG_MANAGE, EMPLOYEE_VIEW, EMPLOYEE_EDIT | List departments |
| POST | `/departments` | ORG_MANAGE | Create a department |
| PUT | `/departments/{uuid}` | ORG_MANAGE | Update a department |
| PATCH | `/departments/{uuid}/active` | ORG_MANAGE | Activate or deactivate a department |
| GET | `/departments/tree` | ORG_VIEW, ORG_MANAGE, EMPLOYEE_VIEW | Department hierarchy as a tree |
| GET | `/designations` | ORG_VIEW, ORG_MANAGE, EMPLOYEE_VIEW, EMPLOYEE_EDIT | List designations |
| POST | `/designations` | ORG_MANAGE | Create a designation |
| PUT | `/designations/{uuid}` | ORG_MANAGE | Update a designation |
| PATCH | `/designations/{uuid}/active` | ORG_MANAGE | Activate or deactivate a designation |
| GET | `/grades` | ORG_VIEW, ORG_MANAGE, EMPLOYEE_VIEW, EMPLOYEE_EDIT | List grades (salary band masked without HRM_SALARY_VIEW) |
| POST | `/grades` | ORG_MANAGE | Create a grade |
| PUT | `/grades/{uuid}` | ORG_MANAGE | Update a grade |
| PATCH | `/grades/{uuid}/active` | ORG_MANAGE | Activate or deactivate a grade |

### Employees, employment, bank

Also needs: nothing beyond `MODULE_HRM`.

| Method | Route | Permission (any of) | What it does |
|---|---|---|---|
| GET | `/employees` | EMPLOYEE_VIEW, EMPLOYEE_EDIT | List employees in scope (identifiers masked). A `cnic` query parameter is refused with 400 `EMPLOYEE_CNIC_IN_QUERY` (REV49-12) |
| POST | `/employees/search` | EMPLOYEE_VIEW, EMPLOYEE_EDIT | Same list, filter in the JSON body; the exact `cnic` search (needs SENSITIVE_VIEW) goes here so the number is not in URL logs (REV49-12) |
| GET | `/employees/lookup?search=&take=` | EMPLOYEE_VIEW, EMPLOYEE_EDIT, ATTENDANCE_VIEW, ATTENDANCE_MANAGE, OVERTIME_APPROVE, SHIFT_MANAGE, LEAVE_VIEW, LEAVE_APPROVE, LEAVE_MANAGE, SALARY_EDIT, PAYROLL_RUN, TRAINING_MANAGE, RECRUITMENT_MANAGE, PERFORMANCE_MANAGE, LOAN_VIEW, SETTLEMENT_VIEW, INCREMENT_MANAGE, LETTER_ISSUE, TAX_CERT_VIEW, EXPENSE_VIEW | Employee picker (REV49-5): plain array of `{ uuid, employeeNumber, fullName, status, departmentName? }`, HR-scoped, ordered by number, `take` 1..200 (default 200), search on number or "first last". Archived and Candidate left out; leavers stay. No contact, identity, bank or pay field. The FE pickers use it, and fall back to `GET /employees` only on 404 |
| POST | `/employees` | EMPLOYEE_EDIT | Create an employee, optionally with the hire record |
| GET | `/employees/{uuid}` | EMPLOYEE_VIEW, EMPLOYEE_EDIT | Get an employee (identifiers masked) |
| PUT | `/employees/{uuid}` | EMPLOYEE_EDIT | Replace an employee's record |
| GET | `/employees/{uuid}/bank-accounts` | BANK_VIEW, BANK_EDIT | List an employee's bank accounts (masked) |
| POST | `/employees/{uuid}/bank-accounts` | BANK_EDIT | Add a bank account |
| PUT | `/employees/{uuid}/bank-accounts/{accountUuid}` | BANK_EDIT | Update a bank account |
| DELETE | `/employees/{uuid}/bank-accounts/{accountUuid}` | BANK_EDIT | Deactivate a bank account |
| POST | `/employees/{uuid}/bank-accounts/{accountUuid}/primary` | BANK_EDIT | Make a bank account the primary |
| GET | `/employees/{uuid}/bank-accounts/{accountUuid}/sensitive` | BANK_VIEW | Reveal a bank account number and IBAN (logged) |
| GET | `/employees/{uuid}/documents` | EMPLOYEE_EDIT, EMPLOYEE_SENSITIVE_VIEW | List an employee's documents |
| POST | `/employees/{uuid}/documents` | EMPLOYEE_EDIT | Add a document to an employee |
| DELETE | `/employees/{uuid}/documents/{documentUuid}` | EMPLOYEE_EDIT | Remove an employee's document |
| GET | `/employees/{uuid}/employment` | EMPLOYEE_VIEW, EMPLOYEE_EDIT, EMPLOYMENT_MANAGE | Employment history, newest first |
| POST | `/employees/{uuid}/employment` | EMPLOYMENT_MANAGE | Add an employment record (closes the latest) |
| PUT | `/employees/{uuid}/employment/{recordUuid}` | EMPLOYMENT_MANAGE | Correct the latest employment record |
| GET | `/employees/{uuid}/employment/current` | EMPLOYEE_VIEW, EMPLOYEE_EDIT, EMPLOYMENT_MANAGE | The current employment record |
| POST | `/employees/{uuid}/employment/promotion` | EMPLOYMENT_MANAGE | Promote an employee |
| POST | `/employees/{uuid}/employment/transfer` | EMPLOYMENT_MANAGE | Transfer an employee |
| GET | `/employees/{uuid}/sensitive` | EMPLOYEE_SENSITIVE_VIEW | Reveal CNIC and NTN (logged) |
| PATCH | `/employees/{uuid}/status` | EMPLOYEE_EDIT | Change an employee's status |
| GET | `/employees/me` | SELF_SERVICE | The caller's own employee record |

### Attachments

Also needs: nothing beyond `MODULE_HRM`.

| Method | Route | Permission (any of) | What it does |
|---|---|---|---|
| GET | `/attachments` | SELF_SERVICE, EMPLOYEE_EDIT, EMPLOYEE_SENSITIVE_VIEW, LEAVE_VIEW, LEAVE_APPROVE, LEAVE_MANAGE, EXPENSE_VIEW, EXPENSE_APPROVE, EXPENSE_PAY, TRAINING_VIEW, TRAINING_MANAGE, RECRUITMENT_VIEW, RECRUITMENT_MANAGE | List the attachments of an HRM row |
| DELETE | `/attachments/{uuid}` | SELF_SERVICE, EMPLOYEE_EDIT, LEAVE_MANAGE, TRAINING_MANAGE, RECRUITMENT_MANAGE | Delete an unused upload (uploader only) |
| GET | `/attachments/{uuid}/content` | SELF_SERVICE, EMPLOYEE_EDIT, EMPLOYEE_SENSITIVE_VIEW, LEAVE_VIEW, LEAVE_APPROVE, LEAVE_MANAGE, EXPENSE_VIEW, EXPENSE_APPROVE, EXPENSE_PAY, TRAINING_VIEW, TRAINING_MANAGE, RECRUITMENT_VIEW, RECRUITMENT_MANAGE | Download an HRM attachment |

### Shifts and holidays

Also needs: `FEATURE_HRM_ATTENDANCE or FEATURE_HRM_LEAVE`.

| Method | Route | Permission (any of) | What it does |
|---|---|---|---|
| GET | `/holidays` | SELF_SERVICE, ATTENDANCE_VIEW, LEAVE_VIEW, LEAVE_APPROVE, LEAVE_MANAGE, SHIFT_MANAGE, EMPLOYEE_VIEW, ORG_VIEW, PAYROLL_VIEW | List holidays for a year and branch |
| POST | `/holidays` | SHIFT_MANAGE | Create a holiday |
| PUT | `/holidays/{uuid}` | SHIFT_MANAGE | Update a holiday (clears the estimated flag when the date changes) |
| DELETE | `/holidays/{uuid}` | SHIFT_MANAGE | Delete a holiday |
| GET | `/shifts` | ATTENDANCE_VIEW, SHIFT_MANAGE, EMPLOYEE_EDIT | List shifts |
| POST | `/shifts` | SHIFT_MANAGE | Create a shift |
| PUT | `/shifts/{uuid}` | SHIFT_MANAGE | Update a shift |
| PATCH | `/shifts/{uuid}/active` | SHIFT_MANAGE | Activate or deactivate a shift |
| POST | `/shifts/{uuid}/assign` | SHIFT_MANAGE | Assign a shift to employees from a date |

### Attendance and overtime

Also needs: `FEATURE_HRM_ATTENDANCE`.

| Method | Route | Permission (any of) | What it does |
|---|---|---|---|
| GET | `/attendance/days` | ATTENDANCE_VIEW, ATTENDANCE_MANAGE | List attendance days in scope |
| PUT | `/attendance/days/{uuid}` | ATTENDANCE_MANAGE | Override an attendance day's status |
| GET | `/attendance/days/me` | SELF_SERVICE | List the caller's own attendance days |
| POST | `/attendance/days/recompute` | ATTENDANCE_MANAGE | Recompute attendance days for a date range |
| POST | `/attendance/events` | ATTENDANCE_MANAGE | Record an attendance event for an employee |
| POST | `/attendance/events/import` | ATTENDANCE_MANAGE | Import attendance events from a CSV file (multipart field `file`, max 10 MB; 400 when missing, empty or larger) |
| POST | `/attendance/events/self` | SELF_SERVICE | Record the caller's own attendance event |
| GET | `/attendance/summary` | ATTENDANCE_VIEW, ATTENDANCE_MANAGE, SELF_SERVICE | Monthly attendance summary for an employee |
| GET | `/overtime` | ATTENDANCE_VIEW, ATTENDANCE_MANAGE, OVERTIME_APPROVE | List overtime records |
| POST | `/overtime` | ATTENDANCE_MANAGE | Submit an overtime record |
| POST | `/overtime/{uuid}/approve` | OVERTIME_APPROVE | Approve overtime |
| POST | `/overtime/{uuid}/reject` | OVERTIME_APPROVE | Reject overtime |

### Leave

Also needs: `FEATURE_HRM_LEAVE`.

| Method | Route | Permission (any of) | What it does |
|---|---|---|---|
| POST | `/leave/allocate` | LEAVE_MANAGE | Allocate or adjust leave for one employee |
| POST | `/leave/allocate/bulk` | LEAVE_MANAGE | Allocate a leave type's entitlement to all eligible employees |
| GET | `/leave/balances` | LEAVE_VIEW, LEAVE_APPROVE, LEAVE_MANAGE, SELF_SERVICE | Leave balances of an employee |
| GET | `/leave/balances/me` | SELF_SERVICE | The caller's own leave balances |
| GET | `/leave/calendar` | LEAVE_VIEW, LEAVE_APPROVE, LEAVE_MANAGE, SELF_SERVICE | Team calendar of approved leave |
| POST | `/leave/carry-forward` | LEAVE_MANAGE | Carry leave balances into the next year |
| GET | `/leave/ledger` | LEAVE_VIEW, LEAVE_APPROVE, LEAVE_MANAGE, SELF_SERVICE | Leave ledger entries of an employee |
| PUT | `/leave/policies/{uuid}` | LEAVE_MANAGE | Update an entitlement policy |
| GET | `/leave/requests` | LEAVE_VIEW, LEAVE_APPROVE, LEAVE_MANAGE | List leave requests in scope |
| POST | `/leave/requests` | SELF_SERVICE, LEAVE_MANAGE | Submit a leave request to its approval workflow |
| GET | `/leave/requests/{uuid}` | LEAVE_VIEW, LEAVE_APPROVE, LEAVE_MANAGE, SELF_SERVICE | Get a leave request |
| POST | `/leave/requests/{uuid}/approve` | LEAVE_APPROVE | Approve a leave request |
| POST | `/leave/requests/{uuid}/cancel` | SELF_SERVICE, LEAVE_MANAGE | Cancel a leave request and restore the ledger |
| POST | `/leave/requests/{uuid}/reject` | LEAVE_APPROVE | Reject a leave request |
| GET | `/leave/requests/me` | SELF_SERVICE | List the caller's own leave requests |
| GET | `/leave/types` | LEAVE_VIEW, LEAVE_MANAGE, SELF_SERVICE | List leave types with their policies |
| POST | `/leave/types` | LEAVE_MANAGE | Create a leave type |
| PUT | `/leave/types/{uuid}` | LEAVE_MANAGE | Update a leave type |
| PATCH | `/leave/types/{uuid}/active` | LEAVE_MANAGE | Activate or deactivate a leave type |
| POST | `/leave/types/{uuid}/policies` | LEAVE_MANAGE | Add an entitlement policy to a leave type |

### Salary

Also needs: `FEATURE_HRM_PAYROLL`.

| Method | Route | Permission (any of) | What it does |
|---|---|---|---|
| GET | `/employees/{uuid}/salary` | SALARY_VIEW, SELF_SERVICE | An employee's salary assignments |
| POST | `/employees/{uuid}/salary` | SALARY_EDIT | Create a draft salary assignment |
| GET | `/employees/{uuid}/salary/current` | SALARY_VIEW, SELF_SERVICE | An employee's salary in force |
| POST | `/salary-assignments/{uuid}/approve` | SALARY_APPROVE | Approve a salary assignment (supersedes the previous one) |
| POST | `/salary-assignments/{uuid}/reject` | SALARY_APPROVE | Reject a salary assignment |
| POST | `/salary-assignments/{uuid}/submit` | SALARY_EDIT | Submit a salary assignment to its approval workflow |
| POST | `/salary-assignments/bulk-revision` | SALARY_EDIT | Create draft salary revisions for several employees |
| GET | `/salary-components` | PAYROLL_CONFIG, PAYROLL_VIEW, SALARY_EDIT | List salary components |
| POST | `/salary-components` | PAYROLL_CONFIG | Create a salary component |
| PUT | `/salary-components/{uuid}` | PAYROLL_CONFIG | Update a salary component |
| PATCH | `/salary-components/{uuid}/active` | PAYROLL_CONFIG | Activate or deactivate a salary component |
| POST | `/salary-formula/test` | PAYROLL_CONFIG | Test a salary formula with sample variables |
| GET | `/salary-structures` | PAYROLL_CONFIG, SALARY_EDIT | List salary structures |
| POST | `/salary-structures` | PAYROLL_CONFIG | Create a salary structure |
| GET | `/salary-structures/{uuid}` | PAYROLL_CONFIG, SALARY_EDIT | Get a salary structure |
| PUT | `/salary-structures/{uuid}` | PAYROLL_CONFIG | Update a salary structure |
| PATCH | `/salary-structures/{uuid}/active` | PAYROLL_CONFIG | Activate or deactivate a salary structure |

### Tax tables and statutory rates

Also needs: `FEATURE_HRM_PAYROLL`.

| Method | Route | Permission (any of) | What it does |
|---|---|---|---|
| GET | `/statutory-rates` | PAYROLL_CONFIG, STATUTORY_VERIFY | List statutory rates |
| POST | `/statutory-rates` | PAYROLL_CONFIG | Create a statutory rate |
| GET | `/statutory-rates/{uuid}` | PAYROLL_CONFIG, STATUTORY_VERIFY | Get a statutory rate |
| PUT | `/statutory-rates/{uuid}` | PAYROLL_CONFIG | Update a statutory rate (clears verification) |
| POST | `/statutory-rates/{uuid}/unverify` | STATUTORY_VERIFY | Withdraw a statutory rate's review |
| POST | `/statutory-rates/{uuid}/verify` | STATUTORY_VERIFY | Mark a statutory rate as reviewed |
| GET | `/tax-tables` | PAYROLL_CONFIG, STATUTORY_VERIFY | List tax tables |
| POST | `/tax-tables` | PAYROLL_CONFIG | Create a tax table |
| GET | `/tax-tables/{uuid}` | PAYROLL_CONFIG, STATUTORY_VERIFY | Get a tax table |
| PUT | `/tax-tables/{uuid}` | PAYROLL_CONFIG | Update a tax table (clears verification) |
| POST | `/tax-tables/{uuid}/unverify` | STATUTORY_VERIFY | Withdraw a tax table's review |
| POST | `/tax-tables/{uuid}/verify` | STATUTORY_VERIFY | Mark a tax table as reviewed |

### Payroll

Also needs: `FEATURE_HRM_PAYROLL`.

| Method | Route | Permission (any of) | What it does |
|---|---|---|---|
| GET | `/employees/{uuid}/payroll-history` | PAYROLL_VIEW | An employee's payroll history |
| GET | `/payroll-adjustments` | PAYROLL_RUN, PAYROLL_VIEW | List payroll adjustments |
| POST | `/payroll-adjustments` | PAYROLL_RUN | Create a one-off payroll adjustment |
| DELETE | `/payroll-adjustments/{uuid}` | PAYROLL_RUN | Delete an adjustment not yet applied |
| GET | `/payroll-runs` | PAYROLL_VIEW | List payroll runs |
| POST | `/payroll-runs` | PAYROLL_RUN | Create a draft payroll run |
| GET | `/payroll-runs/{uuid}` | PAYROLL_VIEW | Get a payroll run |
| POST | `/payroll-runs/{uuid}/approve` | PAYROLL_APPROVE | Approve a run under review |
| GET | `/payroll-runs/{uuid}/bank-file` | PAYROLL_PAY | Download the bank payment CSV (logged per employee) |
| POST | `/payroll-runs/{uuid}/calculate` | PAYROLL_RUN | Calculate a run (202 and a background job above 200 employees) |
| POST | `/payroll-runs/{uuid}/cancel` | PAYROLL_RUN | Cancel a run before finalizing |
| POST | `/payroll-runs/{uuid}/finalize` | PAYROLL_FINALIZE | Finalize a run and post it |
| GET | `/payroll-runs/{uuid}/journal` | PAYROLL_VIEW | The run's summarized journal lines |
| GET | `/payroll-runs/{uuid}/journal-export/csv` | PAYROLL_VIEW | Export the run's journal as CSV |
| GET | `/payroll-runs/{uuid}/journal-export/xlsx` | PAYROLL_VIEW | Export the run's journal as Excel |
| GET | `/payroll-runs/{uuid}/lines` | PAYROLL_VIEW | A payroll run's employee lines |
| GET | `/payroll-runs/{uuid}/lines/{line}/payslip` | PAYROLL_VIEW | Download one line's payslip as PDF |
| POST | `/payroll-runs/{uuid}/pay` | PAYROLL_PAY | Mark a run paid and post the payment |
| POST | `/payroll-runs/{uuid}/payslips` | PAYROLL_RUN | Generate all payslips of a run |
| GET | `/payroll-runs/{uuid}/posting-readiness` | PAYROLL_VIEW | Check whether a run can post to its targets |
| GET | `/payroll-runs/{uuid}/progress` | PAYROLL_VIEW, PAYROLL_RUN | Progress of a background calculation |
| GET | `/payroll-runs/{uuid}/reconciliation` | PAYROLL_VIEW, REPORTS_VIEW | Run totals against what each posting target received |
| POST | `/payroll-runs/{uuid}/reopen` | PAYROLL_APPROVE | Reopen a run for recalculation |
| POST | `/payroll-runs/{uuid}/retry-posting` | PAYROLL_FINALIZE | Retry a failed payroll posting |
| POST | `/payroll-runs/{uuid}/reverse` | PAYROLL_REVERSE | Reverse a finalized or posted run |
| POST | `/payroll-runs/{uuid}/review` | PAYROLL_APPROVE | Move a calculated run to review |
| GET | `/payslips/{uuid}/pdf` | SELF_SERVICE, PAYROLL_VIEW | Download a payslip as PDF |
| GET | `/payslips/me` | SELF_SERVICE | The caller's own payslips |

### Recruitment

Also needs: `FEATURE_HRM_RECRUITMENT`.

| Method | Route | Permission (any of) | What it does |
|---|---|---|---|
| GET | `/applications` | RECRUITMENT_VIEW, RECRUITMENT_MANAGE | List job applications |
| POST | `/applications` | RECRUITMENT_MANAGE | Apply a candidate to a vacancy |
| GET | `/applications/{uuid}` | RECRUITMENT_VIEW, RECRUITMENT_MANAGE | Get a job application |
| POST | `/applications/{uuid}/convert` | RECRUITMENT_MANAGE **and** EMPLOYEE_EDIT | Hire: convert an application into an employee |
| GET | `/applications/{uuid}/interviews` | RECRUITMENT_VIEW, RECRUITMENT_MANAGE | List an application's interviews |
| POST | `/applications/{uuid}/interviews` | RECRUITMENT_MANAGE | Schedule an interview |
| GET | `/applications/{uuid}/offers` | RECRUITMENT_VIEW, RECRUITMENT_MANAGE | List an application's offers |
| POST | `/applications/{uuid}/offers` | RECRUITMENT_MANAGE | Create an offer |
| PATCH | `/applications/{uuid}/rating` | RECRUITMENT_MANAGE | Rate an application from 1 to 5 |
| PATCH | `/applications/{uuid}/stage` | RECRUITMENT_MANAGE | Move an application to another stage |
| GET | `/candidates` | RECRUITMENT_VIEW, RECRUITMENT_MANAGE | List or search candidates |
| POST | `/candidates` | RECRUITMENT_MANAGE | Create a candidate (409 on a likely duplicate unless forced) |
| GET | `/candidates/{uuid}` | RECRUITMENT_VIEW, RECRUITMENT_MANAGE | Get a candidate |
| PUT | `/candidates/{uuid}` | RECRUITMENT_MANAGE | Update a candidate |
| DELETE | `/candidates/{uuid}` | RECRUITMENT_MANAGE | Delete a candidate |
| PUT | `/interviews/{uuid}` | RECRUITMENT_MANAGE, RECRUITMENT_VIEW | Update an interview or save its feedback |
| GET | `/interviews/me` | RECRUITMENT_VIEW | The caller's own assigned interviews |
| GET | `/offers/{uuid}` | RECRUITMENT_VIEW, RECRUITMENT_MANAGE | Get an offer |
| PUT | `/offers/{uuid}` | RECRUITMENT_MANAGE | Update an offer |
| GET | `/offers/{uuid}/letter` | RECRUITMENT_VIEW, RECRUITMENT_MANAGE | Download an offer letter as PDF |
| POST | `/offers/{uuid}/letter` | RECRUITMENT_MANAGE | Generate an offer letter |
| PATCH | `/offers/{uuid}/status` | RECRUITMENT_MANAGE | Change an offer's status |
| GET | `/vacancies` | RECRUITMENT_VIEW, RECRUITMENT_MANAGE | List vacancies |
| POST | `/vacancies` | RECRUITMENT_MANAGE | Create a vacancy |
| GET | `/vacancies/{uuid}` | RECRUITMENT_VIEW, RECRUITMENT_MANAGE | Get a vacancy |
| PUT | `/vacancies/{uuid}` | RECRUITMENT_MANAGE | Update a vacancy |
| GET | `/vacancies/{uuid}/board` | RECRUITMENT_VIEW, RECRUITMENT_MANAGE | A vacancy's applications grouped by stage |
| PATCH | `/vacancies/{uuid}/status` | RECRUITMENT_MANAGE | Change a vacancy's status |

### Performance

Also needs: `FEATURE_HRM_PERFORMANCE`.

| Method | Route | Permission (any of) | What it does |
|---|---|---|---|
| GET | `/goals` | PERFORMANCE_VIEW, PERFORMANCE_MANAGE, SELF_SERVICE | List goals |
| POST | `/goals` | PERFORMANCE_MANAGE, SELF_SERVICE | Create a goal |
| PUT | `/goals/{uuid}` | PERFORMANCE_MANAGE, SELF_SERVICE | Update a goal or its rating |
| DELETE | `/goals/{uuid}` | PERFORMANCE_MANAGE, SELF_SERVICE | Delete a goal |
| GET | `/performance/dashboard` | PERFORMANCE_VIEW, PERFORMANCE_MANAGE | Completion and rating distribution of a cycle |
| GET | `/review-cycles` | PERFORMANCE_VIEW, PERFORMANCE_MANAGE | List review cycles |
| POST | `/review-cycles` | PERFORMANCE_MANAGE | Create a review cycle |
| GET | `/review-cycles/{uuid}` | PERFORMANCE_VIEW, PERFORMANCE_MANAGE | Get a review cycle |
| PUT | `/review-cycles/{uuid}` | PERFORMANCE_MANAGE | Update a review cycle |
| POST | `/review-cycles/{uuid}/close` | PERFORMANCE_MANAGE | Close a review cycle |
| POST | `/review-cycles/{uuid}/open` | PERFORMANCE_MANAGE | Open a cycle and create its reviews |
| GET | `/reviews` | PERFORMANCE_VIEW, PERFORMANCE_MANAGE | List reviews in scope |
| GET | `/reviews/{uuid}` | PERFORMANCE_VIEW, PERFORMANCE_MANAGE, SELF_SERVICE | Get a review |
| PUT | `/reviews/{uuid}` | PERFORMANCE_MANAGE, SELF_SERVICE | Update a review's comments or reviewer |
| POST | `/reviews/{uuid}/acknowledge` | SELF_SERVICE, PERFORMANCE_MANAGE | The employee acknowledges a review |
| POST | `/reviews/{uuid}/submit` | PERFORMANCE_MANAGE, SELF_SERVICE | Submit a review (computes the rating) |
| GET | `/reviews/assigned` | SELF_SERVICE | Reviews where the caller is the reviewer |
| GET | `/reviews/me` | SELF_SERVICE | The caller's own reviews |

### Training

Also needs: `FEATURE_HRM_TRAINING`.

| Method | Route | Permission (any of) | What it does |
|---|---|---|---|
| GET | `/courses` | TRAINING_VIEW, TRAINING_MANAGE, SELF_SERVICE | List courses |
| POST | `/courses` | TRAINING_MANAGE | Create a course |
| GET | `/courses/{uuid}` | TRAINING_VIEW, TRAINING_MANAGE, SELF_SERVICE | Get a course |
| PUT | `/courses/{uuid}` | TRAINING_MANAGE | Update a course |
| GET | `/enrollments` | TRAINING_VIEW, TRAINING_MANAGE | List course enrollments |
| POST | `/enrollments` | TRAINING_MANAGE | Enroll an employee in a course |
| POST | `/enrollments/{uuid}/cancel` | TRAINING_MANAGE | Cancel an enrollment |
| POST | `/enrollments/{uuid}/complete` | TRAINING_MANAGE | Mark an enrollment complete |
| GET | `/enrollments/me` | SELF_SERVICE | The caller's own enrollments |

### Loans, settlements, increments, letters, tax certificates

Also needs: `FEATURE_HRM_LOANS`.

| Method | Route | Permission (any of) | What it does |
|---|---|---|---|
| GET | `/increments` | INCREMENT_MANAGE, INCREMENT_APPROVE | List increment proposals |
| POST | `/increments` | INCREMENT_MANAGE | Create an increment proposal |
| POST | `/increments/{uuid}/apply` | INCREMENT_APPROVE | Apply an approved increment as a salary assignment |
| POST | `/increments/{uuid}/approve` | INCREMENT_APPROVE | Approve an increment |
| POST | `/increments/{uuid}/reject` | INCREMENT_APPROVE | Reject an increment |
| POST | `/increments/bulk` | INCREMENT_MANAGE | Create increment proposals from a review cycle's ratings |
| GET | `/letter-templates` | LETTER_ISSUE | List letter templates |
| POST | `/letter-templates` | LETTER_ISSUE | Create a letter template |
| PUT | `/letter-templates/{uuid}` | LETTER_ISSUE | Update a letter template |
| GET | `/letters` | LETTER_ISSUE | List issued letters |
| POST | `/letters` | LETTER_ISSUE | Issue a letter from a template |
| GET | `/letters/{uuid}/pdf` | LETTER_ISSUE, SELF_SERVICE | Download an issued letter as PDF |
| GET | `/letters/me` | SELF_SERVICE | The caller's own letters |
| GET | `/loans` | LOAN_VIEW | List loans in scope |
| POST | `/loans` | LOAN_MANAGE | Create a draft loan or advance |
| GET | `/loans/{uuid}` | LOAN_VIEW | Get a loan with its installments |
| PUT | `/loans/{uuid}` | LOAN_MANAGE | Update a draft loan |
| POST | `/loans/{uuid}/approve` | LOAN_APPROVE | Approve a loan |
| POST | `/loans/{uuid}/disburse` | LOAN_MANAGE | Disburse a loan and post it |
| POST | `/loans/{uuid}/reject` | LOAN_APPROVE | Reject a loan |
| POST | `/loans/{uuid}/retry-posting` | LOAN_MANAGE | Retry a failed loan posting |
| POST | `/loans/installments/{uuid}/waive` | LOAN_APPROVE | Waive a loan installment |
| GET | `/loans/me` | SELF_SERVICE | The caller's own loans |
| GET | `/settlements` | SETTLEMENT_VIEW | List final settlements |
| GET | `/settlements/{uuid}` | SETTLEMENT_VIEW | Get a final settlement |
| PUT | `/settlements/{uuid}` | SETTLEMENT_MANAGE | Edit a draft settlement's notes and lines |
| POST | `/settlements/{uuid}/approve` | SETTLEMENT_MANAGE | Approve a settlement |
| POST | `/settlements/{uuid}/cancel` | SETTLEMENT_MANAGE | Cancel a settlement |
| POST | `/settlements/{uuid}/pay` | SETTLEMENT_MANAGE | Pay a posted settlement |
| POST | `/settlements/{uuid}/post` | SETTLEMENT_MANAGE | Post an approved settlement |
| POST | `/settlements/{uuid}/retry-posting` | SETTLEMENT_MANAGE | Retry a failed settlement posting |
| POST | `/settlements/compute` | SETTLEMENT_MANAGE | Compute a draft final settlement |
| GET | `/tax-certificates` | TAX_CERT_VIEW | List tax certificates |
| GET | `/tax-certificates/{uuid}/pdf` | TAX_CERT_VIEW, SELF_SERVICE | Download a tax certificate as PDF |
| POST | `/tax-certificates/generate` | TAX_CERT_VIEW | Generate tax certificates for a tax year |
| GET | `/tax-certificates/me` | SELF_SERVICE | The caller's own tax certificates |

### Expenses and remittances

Also needs: `FEATURE_HRM_EXPENSES`.

| Method | Route | Permission (any of) | What it does |
|---|---|---|---|
| GET | `/expense-categories` | EXPENSE_VIEW, SELF_SERVICE, ACCOUNT_MAPPING_VIEW | List expense categories |
| POST | `/expense-categories` | ACCOUNT_MAPPING_EDIT, SETTINGS_MANAGE | Create an expense category |
| PUT | `/expense-categories/{uuid}` | ACCOUNT_MAPPING_EDIT, SETTINGS_MANAGE | Update an expense category |
| PATCH | `/expense-categories/{uuid}/active` | ACCOUNT_MAPPING_EDIT, SETTINGS_MANAGE | Activate or deactivate an expense category |
| GET | `/expense-claims` | EXPENSE_VIEW, EXPENSE_APPROVE, EXPENSE_PAY | List expense claims in scope |
| POST | `/expense-claims` | SELF_SERVICE | Create a draft expense claim |
| GET | `/expense-claims/{uuid}` | EXPENSE_VIEW, EXPENSE_APPROVE, EXPENSE_PAY, SELF_SERVICE | Get an expense claim |
| PUT | `/expense-claims/{uuid}` | SELF_SERVICE | Update a draft expense claim |
| POST | `/expense-claims/{uuid}/approve` | EXPENSE_APPROVE | Approve a claim and post the expense |
| POST | `/expense-claims/{uuid}/pay` | EXPENSE_PAY | Pay a separately settled claim and post the payment |
| POST | `/expense-claims/{uuid}/reject` | EXPENSE_APPROVE | Reject a claim |
| POST | `/expense-claims/{uuid}/retry-posting` | EXPENSE_PAY, EXPENSE_APPROVE | Retry a failed claim posting |
| POST | `/expense-claims/{uuid}/submit` | SELF_SERVICE | Submit a claim to its approval workflow |
| GET | `/expense-claims/me` | SELF_SERVICE | The caller's own expense claims |
| GET | `/remittances` | REMITTANCE_VIEW | List statutory remittances |
| POST | `/remittances` | REMITTANCE_MANAGE | Record a statutory remittance and post it |
| POST | `/remittances/{uuid}/retry-posting` | REMITTANCE_MANAGE | Retry a failed remittance posting |
| GET | `/remittances/tracker` | REMITTANCE_VIEW | Statutory amounts calculated against paid, per period |

### Outside `api/hrm`

Codes in this table are written in full.

| Method | Route | Permission (any of) | Gate | What it does |
|---|---|---|---|---|
| GET | `api/tenant/module-profiles` | MODULES_VIEW, PLATFORM_SUPER_ADMIN | | List deployment profiles and their modules |
| POST | `api/tenancy/organizations/{id}/apply-profile` | PLATFORM_SUPER_ADMIN | `[RequireSuperAdmin]` (an org admin gets 403) | Apply a deployment profile |
| POST | `api/tenant/modules/{code}/enable` | MODULES_MANAGE | | With `MODULE_HRM`: 422 `PAYROLL_COST_CENTER_REQUIRED` until the wizard is done |
| GET | `api/integrations/quickbooks/cost-center-mappings` | INTEGRATION_VIEW, HRM_ACCOUNT_MAPPING_VIEW | `MODULE_INTEGRATION` | List cost center to Class mappings |
| GET | `.../cost-center-mappings/unmapped` | INTEGRATION_VIEW, HRM_ACCOUNT_MAPPING_VIEW | `MODULE_INTEGRATION` | Cost centers with no Class mapped |
| GET | `.../cost-center-mappings/reference` | INTEGRATION_VIEW, HRM_ACCOUNT_MAPPING_VIEW | `MODULE_INTEGRATION` | Stored Classes and Departments for pickers |
| PUT | `.../cost-center-mappings/{code}` | INTEGRATION_MANAGE, HRM_ACCOUNT_MAPPING_EDIT | `MODULE_INTEGRATION` | Map a cost center to a Class or Department (`{code}` is the cost center code, not a uuid) |
| POST | `.../cost-center-mappings/sync` | INTEGRATION_MANAGE, HRM_ACCOUNT_MAPPING_EDIT | `MODULE_INTEGRATION` | Fill the list from HR's cost centers |
| GET | `api/integrations/quickbooks/sync-control` | INTEGRATION_VIEW | `MODULE_INTEGRATION` | Document types, including the HRM ones while HRM is on |
| PUT | `api/integrations/quickbooks/sync-control/{documentType}` | INTEGRATION_MANAGE | `MODULE_INTEGRATION` | Turn a type on or off |
| POST | `api/integrations/quickbooks/sync-control/{documentType}/release` | INTEGRATION_MANAGE | `MODULE_INTEGRATION` | Release what a type held |
| GET | `api/finance/setup/status` | GL_VIEW, GL_ACCOUNT_MANAGE, GL_SETUP_MANAGE | `FEATURE_GENERAL_LEDGER` | GL setup status, with `hrRolesMissing` |
| POST | `api/sales-invoices/service` | SALES_INVOICE_MANAGE | `MODULE_FINANCE` | Create a draft service invoice |
| | `api/import/...` | IMPORT_* | `MODULE_DATA_IMPORT` | Employee import, profile `HRM_EMPLOYEE`: see [MIGRATION-MAPPING-GUIDE.md](MIGRATION-MAPPING-GUIDE.md) |

## Differences from API-CONTRACT.md

Every route the contract names exists, every controller route is listed above, and query parameters match on every list route the contract gives them for. The differences are below. Contract §section owners should fold these in; nothing here changes behaviour, and each row was read from the attribute on the action.

**The contract's body says one thing, the code another:**

| Contract says | The code | Owner |
|---|---|---|
| Changes (D-24): `HrmSetupController` "also runs the seed filter" | It carries `[SkipHrmSeed]`: nothing is seeded before the licence is checked, and the service seeds afterwards. | CORE |

**The code accepts more codes than the contract lists.** A caller the contract would refuse gets in, though HR scope still applies:

| Route | Contract | Also accepted | Owner |
|---|---|---|---|
| `GET attendance/days`, `GET attendance/summary`, `GET overtime` | ATTENDANCE_VIEW (overtime: + OVERTIME_APPROVE) | ATTENDANCE_MANAGE | ATT |
| `GET leave/balances`, `GET leave/ledger` | LEAVE_VIEW, or own | LEAVE_APPROVE, LEAVE_MANAGE | ATT |
| `GET leave/requests` | LEAVE_VIEW, LEAVE_APPROVE | LEAVE_MANAGE | ATT |
| `GET tax-tables`, `GET statutory-rates` | PAYROLL_CONFIG | STATUTORY_VERIFY | PAY |
| `GET payroll-adjustments` | PAYROLL_RUN | PAYROLL_VIEW | PAY |
| `GET courses`, `GET courses/{uuid}` | none stated | TRAINING_VIEW, TRAINING_MANAGE, SELF_SERVICE | TAL |

**The code narrows what the contract implies:**

| Route | Contract | The code | Owner |
|---|---|---|---|
| `GET holidays` | "any HRM_ view or self" | Nine named codes: SELF_SERVICE, ATTENDANCE_VIEW, LEAVE_VIEW, LEAVE_APPROVE, LEAVE_MANAGE, SHIFT_MANAGE, EMPLOYEE_VIEW, ORG_VIEW, PAYROLL_VIEW. A holder of only, say, RECRUITMENT_VIEW is refused. | ATT |
| `GET departments/tree` | Lists "same" departments routes; no separate code | ORG_VIEW, ORG_MANAGE, EMPLOYEE_VIEW. `GET departments` also takes EMPLOYEE_EDIT, so an editor without EMPLOYEE_VIEW can list departments but not load the tree. | CORE |

**In the code, not in the contract:**

| Route or parameter | Owner |
|---|---|
| `GET tax-tables/{uuid}`, `GET statutory-rates/{uuid}` | PAY |
| `GET leave/types?includeInactive=` | ATT |
| `GET goals?cycleUuid=&employeeUuid=` | TAL |
| `POST loans/{uuid}/retry-posting` needs LOAN_MANAGE; `POST settlements/{uuid}/retry-posting` needs SETTLEMENT_MANAGE; `POST expense-claims/{uuid}/retry-posting` needs EXPENSE_PAY or EXPENSE_APPROVE; `POST remittances/{uuid}/retry-posting` needs REMITTANCE_MANAGE (contract names the routes, not their codes) | LIFE |

Not compared: response bodies and individual error codes, because the document does not describe them. Each field-level statement in the contract rests on its owner's tests, not on this check.

**Note, not an HRM change.** `Program.cs` serves Swagger and `/swagger-error` in every environment without a login, so the whole route list, HRM included, is readable by anyone who can reach the API. A49 did not introduce this. It is worth a decision before production.

## Corrections (REV-49, 2026-11-09)

Checked against the controllers, not the route dump. All 262 HRM actions (verb, template, parameter names, codes, gates) and the outside routes matched the tables. These were fixed:

- Setup and settings, gate line: implied `MODULE_HRM` covers `setup/*`; now says `setup/*` has no module gate (licence checked in the service, 403 when unlicensed) and `settings`/`user-scopes` do.
- `GET setup/status`: `MODULES_MANAGE` read as `HRM_MODULES_MANAGE` under the prefix rule; now marked as not an HRM code, and the convention says so.
- `POST setup/payroll-cost-center`: the extra ORG_MANAGE check for `newCostCenter` (service) was missing; added.
- Conventions, response envelope: did not say which routes return raw files; listed (9 downloads).
- Conventions, paged lists: did not say which lists are paged; now names the 7 that return `PaginatedResponse`, and says the rest are plain arrays.
- Conventions, other statuses: 202 from calculate and 409-with-matches from candidates were only in summaries; stated, with `force`. DELETEs with no `result` noted.
- `POST attendance/events/import`: multipart field name, 10 MB limit and the uncoded 400s added.
- `PUT cost-center-mappings/{code}`: `{code}` is the cost center code, not a uuid; said so.
- Outside `api/hrm`, apply-profile gate: said "Super admin"; now names `[RequireSuperAdmin]` and the 403.
- Differences, §10.3 module-profiles row: stale, the contract already lists MODULES_VIEW or PLATFORM_SUPER_ADMIN; removed.
- Differences, §10.7 `api/finance/setup` row: stale, the contract body already says `setup/status`; removed.
