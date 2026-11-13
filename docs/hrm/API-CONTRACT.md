# Addendum 49 — HRM: API contract

Owner of this file: **CORE**. Every agent codes against it. A change after the first version goes in **Changes** at the end (dated, who asked, what changed), never a silent edit above it. Read with [ADDENDUM-49-ANALYSIS.md](ADDENDUM-49-ANALYSIS.md) (Part A + decisions override the FSD).

Section owners: §1–§5, §13, §14 CORE (**built**) · §6 ATT · §7 PAY (salary) · §8 TAL · §9 PAY (payroll) · §10 POST · §11 LIFE (loans, settlement, increments, letters) · §12 LIFE (expenses, remittances).

---

## 0. Conventions

| Item | Rule |
|---|---|
| Base route | `api/hrm/...`; QuickBooks additions under `api/integrations/quickbooks/...`. Ids in routes and bodies are **uuids**; an `int` id never leaves the server. The one exception: `userId` is an int, as everywhere in SMS. |
| JSON | camelCase. Dates `yyyy-MM-dd` (`DateOnly`), times `HH:mm:ss`, timestamps ISO-8601 UTC. Enums are **strings**, spelled as in §0.1, matched case-insensitively on input. Unknown → 400 `Unknown {field} '{value}'. Allowed: {list}.` |
| Envelope | Single: `ApiResponse<T>` `{ success, message, result }`. Pages: `ApiResponse<PaginatedResponse<T>>` `{ data[], totalRecords, page, pageSize, totalPages, hasNext, hasPrevious }`, `page` default 1, `pageSize` default 25, max 200. Small lists are plain arrays. |
| Errors | `BadRequestException` 400, `NotFoundException` 404 (another org's row and a row **outside the HR scope** are 404 too), `ConflictException` 409 (state; stale `RowVersion`: `{Thing} was changed by someone else; reload and try again.`), `UnprocessableEntityException` 422, `ForbiddenException` 403. **Coded errors:** `message` starts with `CODE: ` (`BRANCH_CODE_TAKEN: …`). Code tables are per section; a screen may branch on the code and show the text after `: `. |
| Module gate | Every controller `[RequiresFeature(ModuleCodes.Hrm)]`; phase controllers **also** gate their sub-feature (`HrmFeatureCodes`): both must be on. Self-service routes gate only their own sub-feature + module. |
| Tenant | `HrmCaller.OrganizationId`. Services filter on it explicitly. Jobs take the organization as a parameter and use a tenant scope per org. |
| Who | `HrmCaller` (`UserId`, `UserName`, `Has(code)`). Columns `…By` int? + `…ByName`. A super admin never holds `HrmSensitiveCodes`. |
| Money | `decimal(18,2)`, org base currency (`currencyCode` on documents; `IOrgCurrencyLookup`). Payroll never mixes currencies (OD-12). |
| Files | Upload through `api/attachments` (`interfaceCode` from §0.1, `documentId` = the row's uuid, or a client-made uuid before the row exists), then send the attachment uuid. Read through `api/hrm/attachments` (D-25). Generated PDFs: `IHrmDocumentStore`; download only through the HRM route so scope applies. |
| Transactions | Use `HrmDbContext.InTransactionAsync` (retrying strategy, joins a caller's transaction). The work may run twice: no first-attempt assumptions. |
| Posting | **After** your own commit: build `FinancialTransaction` with `SourceModule = hrm`, call `IFinancialPostingGateway.PostAsync`. It never throws for a target failure. `NoTargetConfigured` and `ModuleNotLicensed` both mean "no posting required" (D-11). Never put a name, employee number, CNIC or per-employee amount in a transaction (G-11). |
| Email | `IEmailSender.SendWithResultAsync` where a failure matters. |
| PDF | Always `PdfRenderGate.Run`. |

### 0.1 Enum spellings (persisted; CHECK enforced; source `Domain/HrmCodes.cs`)

| Enum | Values |
|---|---|
| EmployeeStatus | `Candidate Probation Active OnNotice Suspended Terminated Archived`; moves: Candidate→Probation/Active/Archived; Probation→Active/OnNotice/Suspended/Terminated; Active→OnNotice/Suspended/Terminated; OnNotice→Active/Terminated; Suspended→Active/OnNotice/Terminated; Terminated→Archived |
| Gender | `M F Other` · MaritalStatus `Single Married Widowed Divorced Other` · DepartureReason `Resignation Retirement Termination ContractEnd Death Other` |
| EmploymentType | `Permanent Contract Probation Intern DailyWage` · ChangeReason `Hire Transfer Promotion Confirmation Demotion ContractRenewal Correction Other` |
| BankPaymentMethod | `Bank Cash Cheque` · PayrollCostCenterMode `CENTRAL BY_DEPARTMENT` · PostDatePolicy `PayDate PeriodEnd PayDateWithFallback` · DocumentType `CNIC CONTRACT CERTIFICATE RESUME PHOTO OTHER` |
| AttendanceSource | `MANUAL DEVICE IMPORT WEB` · EventType `IN OUT UNKNOWN` · DayStatus `Present Late HalfDay Absent OnLeave Holiday WeeklyOff` · ApprovalStatus `Pending Approved Rejected` |
| AccrualMethod | `Annual Monthly None` · LeaveRequestStatus `Pending Approved Rejected Cancelled` · LedgerReason `Allocation Accrual CarryForward Expiry Consumption CancellationRestore Adjustment Opening Encashment` |
| ComponentType | `Earning Deduction EmployerContribution Result` · CalcMethod `Fixed Percent Formula` · SalaryAssignmentStatus `Draft PendingApproval Approved Rejected Superseded` · StatutoryRateType `EOBI PF SS GRATUITY` |
| VacancyStatus | `Open OnHold Closed` · Stage `New Screening Interview Offer Hired Rejected` · InterviewResult `Pending Passed Failed NoShow` · OfferStatus `Draft Sent Accepted Declined Withdrawn` |
| ReviewPeriod | `Annual Quarterly` · CycleStatus `Draft Open Closed` · ReviewStatus `Draft Submitted Acknowledged` · GoalStatus `Open Achieved Missed Dropped` · EnrollmentStatus `Enrolled Completed Cancelled` |
| PayrollRunStatus | `Draft Calculated UnderReview Approved Finalized Posted Paid Cancelled Reversed` · PayrollPostingStatus `Pending Posted Failed Held NoTarget Reversed` · PostingKind `PayrollRun PayrollPayment Void` |
| LoanType | `Advance Personal Housing` · LoanStatus `Draft Approved Disbursed Closed Rejected Cancelled` · InstallmentStatus `Pending Deducted Waived` · SettlementStatus `Draft Approved Posted Paid Cancelled` · IncrementStatus `Draft Approved Applied Rejected` · LetterKind `OfferLetter ExperienceCertificate SalaryCertificate Custom` |
| ClaimStatus | `Draft Submitted Approved Paid Rejected` · PaymentMode `OwnAccount CompanyAccount` · SettlementMethod `WithPayroll SeparatePayment` · RemittanceType `IncomeTax EOBI PF SS` · PostingState `None Pending Posted Failed NoTarget` · PaidFromAccount `Bank Cash` |
| Attachment interface codes | Uploads: `HRM_EMPLOYEE`, `HRM_LEAVE_REQUEST`, `HRM_EXPENSE_CLAIM`, `HRM_CANDIDATE`, `HRM_ENROLLMENT`. Generated (no `api/attachments` rule): `HRM_PAYSLIP`, `HRM_LETTER`, `HRM_TAX_CERTIFICATE`. |

### 0.2 Permissions (`PermissionCodes.HrmCodes`, module MODULE_HRM by the `HRM_` prefix)

| Code | Gates |
|---|---|
| `HRM_SETTINGS_MANAGE` | settings, HR user scopes, payroll cost center |
| `HRM_ACCOUNT_MAPPING_VIEW` / `_EDIT` | role mapping views, expense category accounts, cost center → QBO Class map |
| `HRM_ORG_VIEW` / `HRM_ORG_MANAGE` | branches, departments, cost centers, designations, grades |
| `HRM_EMPLOYEE_VIEW` / `_EDIT` | employee list/detail (masked), create/edit/status |
| `HRM_EMPLOYEE_SENSITIVE_VIEW` | **S3** reveal CNIC/NTN, exact CNIC search |
| `HRM_EMPLOYMENT_MANAGE` | employment records, transfer, promotion |
| `HRM_BANK_VIEW` / `HRM_BANK_EDIT` | **S3** reveal; add/change accounts (both may list masked) |
| `HRM_SCOPE_ALL` / `_BRANCH` / `_DEPARTMENT` | HR scope (§1.2) |
| `HRM_SELF_SERVICE` | own profile, payslips, leave, expenses, attendance |
| `HRM_SHIFT_MANAGE` · `HRM_ATTENDANCE_VIEW` / `_MANAGE` · `HRM_OVERTIME_APPROVE` | shifts + holidays; attendance; overtime |
| `HRM_LEAVE_VIEW` / `_APPROVE` / `_MANAGE` | leave lists; approving; types, policies, allocation, carry-forward |
| `HRM_SALARY_VIEW` / `_EDIT` / `_APPROVE` | **S2** assignments, grade bands, offered salary |
| `HRM_PAYROLL_CONFIG` · `HRM_STATUTORY_VERIFY` | components, structures, tax, rates; marking statutory data reviewed |
| `HRM_PAYROLL_VIEW` / `_RUN` / `_APPROVE` / `_FINALIZE` / `_PAY` / `_REVERSE` | run lifecycle |
| `HRM_RECRUITMENT_VIEW` / `_MANAGE` · `HRM_PERFORMANCE_VIEW` / `_MANAGE` · `HRM_TRAINING_VIEW` / `_MANAGE` | H4 |
| `HRM_LOAN_VIEW` / `_MANAGE` / `_APPROVE` · `HRM_SETTLEMENT_VIEW` / `_MANAGE` · `HRM_INCREMENT_MANAGE` / `_APPROVE` · `HRM_LETTER_ISSUE` · `HRM_TAX_CERT_VIEW` | H6 |
| `HRM_PAYSLIP_EMAIL` | emailing a whole payroll run's payslips (A50 D-98; HR Manager and Org Admin; sensitive). One payslip keeps PAYROLL_VIEW in scope or SELF_SERVICE on one's own |
| `HRM_EXPENSE_VIEW` / `_APPROVE` / `_PAY` · `HRM_REMITTANCE_VIEW` / `_MANAGE` · `HRM_REPORTS_VIEW` | H6E, reports |

`HrmSensitiveCodes` (never for System Admin or a super admin): SENSITIVE_VIEW, BANK_VIEW/EDIT, SALARY_*, PAYROLL_VIEW/RUN/APPROVE/FINALIZE/PAY/REVERSE, LOAN_*, SETTLEMENT_*, INCREMENT_*, TAX_CERT_VIEW, SCOPE_ALL, PAYSLIP_EMAIL. Grants: analysis D-18. Finance Manager also holds `HRM_EXPENSE_APPROVE` since A50 D-97 (it is the level-2 approver of the seeded expense rule).

---

## 1. Access model

### 1.1 Order of checks
Tenant (org filter) → module gate → `[RequirePermission(any of)]` → HR scope in the service → field masking.

### 1.2 HR scope (§3.3)
`IHrmScopeService`: `HRM_SCOPE_ALL`, else the union of: own branch (`HRM_SCOPE_BRANCH`), own department + children (`HRM_SCOPE_DEPARTMENT`), rows in `hrm.HrUserScopes`, the reporting line under the caller (direct and indirect), the caller. No match = 404. Own branch/department read the caller's linked employee's current record.

### 1.3 Sensitivity
S0/S1 with the view code. S2: amount fields are `null` + `salaryMasked: true` unless the caller holds the code. S3: only `…Masked` fields in lists and details; the value only from `…/sensitive`, which logs to `hrm.SensitiveAccessLogs` first. An exported file that holds S3 (bank file) logs one `BANK_ACCOUNT` row per employee with purpose `Bank file`.

---

## 2. Organization structure and settings — BUILT (CORE)

### 2.1 Settings
`GET api/hrm/settings` (`HRM_SETTINGS_MANAGE`, `HRM_ACCOUNT_MAPPING_VIEW`, `HRM_PAYROLL_VIEW`) → `HrmSettings { payrollCostCenterUuid?, payrollCostCenterCode?, payrollCostCenterName?, payrollCostCenterMode, postDatePolicy, employeeNumberPrefix, countryCode, workingDaysDivisor?, defaultExpenseSettlement, defaultReceiptThreshold?, enablementCompletedAt? }`
`PUT api/hrm/settings` (`HRM_SETTINGS_MANAGE`), body = any of those writable fields, null = unchanged. Errors: 404 cost center, 400 `COST_CENTER_INACTIVE`. A mode change applies to future runs.

### 2.2 Branches `api/hrm/branches`
`BranchModel { uuid, code, name, address?, city?, warehouseUuid?, isActive }`, `BranchUpsert { code, name, address?, city?, warehouseUuid? }`

| Method, route | Permission | Result | Errors |
|---|---|---|---|
| `GET ?includeInactive=` | ORG_VIEW, ORG_MANAGE, EMPLOYEE_VIEW, EMPLOYEE_EDIT | `BranchModel[]` by name | |
| `GET {uuid}` | ORG_VIEW, ORG_MANAGE | `BranchModel` | 404 |
| `POST` / `PUT {uuid}` | ORG_MANAGE | `BranchModel` | 409 `BRANCH_CODE_TAKEN` |
| `PATCH {uuid}/active` `{ isActive }` | ORG_MANAGE | `BranchModel` | 409 `BRANCH_IN_USE` |

### 2.3 Cost centers `api/hrm/cost-centers` (FD-9)
`CostCenterModel { uuid, code, name, parentUuid?, branchUuid?, isActive, isPayrollCostCenter }`, `CostCenterUpsert { code, name, parentUuid?, branchUuid? }`. `GET ?includeInactive=` (ORG_VIEW, ORG_MANAGE, SETTINGS_MANAGE, ACCOUNT_MAPPING_VIEW, EMPLOYEE_EDIT); `POST`, `PUT {uuid}`, `PATCH {uuid}/active` (ORG_MANAGE). Errors: 409 `COST_CENTER_CODE_TAKEN`, 400 `COST_CENTER_CYCLE`, 409 `COST_CENTER_IS_PAYROLL`, 409 `COST_CENTER_IN_USE`. Changing a code carries to departments and records; posted runs keep the code they posted.

### 2.4 Departments `api/hrm/departments`
`DepartmentModel { uuid, code, name, parentUuid?, headEmployeeUuid?, headEmployeeName?, branchUuid?, branchName?, costCenterUuid?, costCenterCode?, isActive, employeeCount }`, `DepartmentTreeNode = + children[]`, `DepartmentUpsert { code, name, parentUuid?, headEmployeeUuid?, branchUuid?, costCenterUuid? }`. `GET ?includeInactive=`, `GET tree?includeInactive=`, `POST`, `PUT {uuid}`, `PATCH {uuid}/active`. Errors: 409 `DEPARTMENT_CODE_TAKEN`, 400 `DEPARTMENT_CYCLE`, 400 `COST_CENTER_INACTIVE`, 409 `DEPARTMENT_IN_USE`, 409 `DEPARTMENT_HAS_CHILDREN`.

### 2.5 Designations `api/hrm/designations`
`{ uuid, code, name, level, isActive }`; upsert `{ code, name, level }`. Same four routes as §2.3. 409 `DESIGNATION_CODE_TAKEN`, `DESIGNATION_IN_USE`.

### 2.6 Grades `api/hrm/grades`
`{ uuid, code, name, minSalary?, maxSalary?, salaryMasked, isActive }`; band null + `salaryMasked: true` without `HRM_SALARY_VIEW`; a masked editor's PUT keeps the band. 400 `GRADE_RANGE_INVALID`, 409 `GRADE_CODE_TAKEN`, `GRADE_IN_USE`.

### 2.7 HR user scopes `api/hrm/user-scopes` (SETTINGS_MANAGE)
`GET ?userId=` → `{ uuid, userId, scopeType, branchUuid?, branchName?, departmentUuid?, departmentName? }[]`. `POST { userId, branchUuid? | departmentUuid? }` (exactly one; same assignment returns the existing row). `DELETE {uuid}`.

---

## 3. Employees `api/hrm/employees` — BUILT (CORE)

`EmployeeListItem { uuid, employeeNumber, fullName, status, workEmail?, mobile?, departmentUuid?, departmentName?, designationUuid?, designationName?, branchUuid?, branchName?, managerUuid?, managerName?, joinDate?, cnicMasked? }`

`EmployeeDetail { uuid, employeeNumber, firstName, lastName, fullName, fatherOrHusbandName?, dateOfBirth?, gender?, maritalStatus?, nationality?, cnicMasked?, ntnMasked?, personalEmail?, workEmail?, phone?, mobile?, currentAddress?, permanentAddress?, emergencyContacts[], photoAttachmentUuid?, status, userId?, sourceCandidateUuid?, joinDate?, leaveDate?, departureReason?, notes? (EMPLOYEE_EDIT only), currentEmployment?, allowedStatuses[], canEdit, canRevealSensitive }`; address `{ line1?, line2?, city?, province?, postalCode?, country? }`; contact `{ name?, relation?, phone? }`

`EmployeeUpsert` = those writable fields + `cnic?`, `ntn?` (plain text; on update **null = keep, "" = clear**), `status?` (create: Candidate/Probation/Active), `employment?` (create: hire record, §4), `sourceCandidateUuid?`. **PUT replaces the whole record: send every field back**, including `userId` (null unlinks the user). Only `cnic`, `ntn`, `emergencyContacts` and `joinDate` treat null as keep, because the form cannot show the first two. `status` and employment are ignored on PUT.

| Method, route | Permission | Result | Errors |
|---|---|---|---|
| `GET ?search=&status=&departmentUuid=&branchUuid=&designationUuid=&managerUuid=&page=&pageSize=` | EMPLOYEE_VIEW, _EDIT | page of `EmployeeListItem`, scoped, by number. A `cnic` query parameter is refused (REV49-12) | 400 `EMPLOYEE_CNIC_IN_QUERY` |
| `POST search` | EMPLOYEE_VIEW, _EDIT | body = the same filter as JSON (`search, cnic, status, departmentUuid, branchUuid, designationUuid, managerUuid, page, pageSize`); same page of `EmployeeListItem`. `cnic` exact needs SENSITIVE_VIEW. The CNIC travels in the body so it is not in URL access logs (REV49-12) | 403, 400 `EMPLOYEE_CNIC_INVALID` |
| `GET me` | SELF_SERVICE | `EmployeeDetail` | 404 `HRM_NO_EMPLOYEE` |
| `GET {uuid}` | EMPLOYEE_VIEW, _EDIT | `EmployeeDetail` | 404 |
| `POST` | EMPLOYEE_EDIT | `EmployeeDetail`; number `{prefix}-NNNN` | 400 `EMPLOYEE_CNIC_INVALID`, `EMPLOYEE_STATUS_INVALID`; 409 `EMPLOYEE_CNIC_DUPLICATE`, `EMPLOYEE_USER_TAKEN`; 404 user |
| `PUT {uuid}` | EMPLOYEE_EDIT | `EmployeeDetail` | + 409 `EMPLOYEE_ARCHIVED` |
| `PATCH {uuid}/status` `{ status, effectiveDate?, departureReason? }` | EMPLOYEE_EDIT | `EmployeeDetail`. Terminated needs date + reason (sets `leaveDate`). OnNotice→Active clears them | 409 `EMPLOYEE_STATUS_TRANSITION`; 400 `EMPLOYEE_LEAVE_DATE_REQUIRED` |
| `GET {uuid}/sensitive` | SENSITIVE_VIEW | `{ cnic?, ntn? }`, one log row per value | 403 |
| `GET {uuid}/documents` | EMPLOYEE_EDIT, SENSITIVE_VIEW | `{ uuid, documentType, title, attachmentUuid, expiryDate?, createdAt }[]` | |
| `POST {uuid}/documents` `{ documentType?, title, attachmentUuid, expiryDate? }` | EMPLOYEE_EDIT | that row | |
| `DELETE {uuid}/documents/{documentUuid}` | EMPLOYEE_EDIT | | |

## 4. Employment records — BUILT (CORE)

`EmploymentRecordModel { uuid, employeeUuid, effectiveFrom, effectiveTo?, employmentType, departmentUuid, departmentName, designationUuid, designationName, branchUuid, branchName, gradeUuid?, gradeName?, managerUuid?, managerName?, shiftUuid?, shiftName?, probationEndDate?, confirmationDate?, contractEndDate?, noticePeriodDays, costCenterUuid?, costCenterCode?, payrollGroup?, changeReason, notes?, costSplits: { costCenterUuid, costCenterCode?, percent }[] }`

`EmploymentRecordCreate` = writable fields; `effectiveFrom`, department, designation, branch required.

| Method, route (under `employees/{uuid}`) | Permission | Notes |
|---|---|---|
| `GET employment`, `GET employment/current` | EMPLOYEE_VIEW, _EDIT, EMPLOYMENT_MANAGE | newest first; current may be null |
| `POST employment` | EMPLOYMENT_MANAGE | closes the latest at start − 1 |
| `POST employment/transfer` `{ effectiveFrom, departmentUuid?, branchUuid?, managerUuid?, costCenterUuid?, notes? }` | EMPLOYMENT_MANAGE | carries everything else |
| `POST employment/promotion` `{ effectiveFrom, designationUuid?, gradeUuid?, managerUuid?, notes? }` | EMPLOYMENT_MANAGE | |
| `PUT employment/{recordUuid}` | EMPLOYMENT_MANAGE | latest record only |

Errors: 409 `EMPLOYMENT_OVERLAP`, `EMPLOYMENT_NONE`, `EMPLOYMENT_NOT_LATEST`; 400 `EMPLOYMENT_FIELD_REQUIRED`, `EMPLOYMENT_FIELD_INACTIVE`, `EMPLOYMENT_MANAGER_CYCLE`, `EMPLOYMENT_TRANSFER_EMPTY`, `EMPLOYMENT_PROMOTION_EMPTY`, `COST_SPLIT_TOTAL`, `COST_CENTER_INACTIVE`.

## 5. Bank accounts — BUILT (CORE)

`{ uuid, employeeUuid, bankName, branchName?, accountTitle?, accountNumberMasked?, ibanMasked?, isPrimary, isActive, paymentMethod }`; upsert `{ bankName, branchName?, accountTitle?, accountNumber?, iban?, isPrimary?, paymentMethod? }` (null = keep, "" = clear). Under `employees/{uuid}/bank-accounts`: `GET` (BANK_VIEW, BANK_EDIT), `POST`, `PUT {a}`, `POST {a}/primary`, `DELETE {a}` = deactivate (BANK_EDIT), `GET {a}/sensitive` (BANK_VIEW, logged). 400 `BANK_ACCOUNT_NUMBER_REQUIRED`, 409 `BANK_ACCOUNT_INACTIVE`. First account is primary; a later one only when asked; a deactivated primary leaves none.

---

## 6. Leave and attendance (ATT) — feature `FEATURE_HRM_ATTENDANCE` / `FEATURE_HRM_LEAVE`

### 6.1 Shifts `api/hrm/shifts`, holidays `api/hrm/holidays`
`Shift { uuid, code, name, startTime, endTime, breakMinutes, graceInMinutes, graceOutMinutes, halfDayMinutes, overtimeThresholdMinutes, weeklyOffDays (flags Sun=1..Sat=64), isDefault, isActive }`. `GET ?includeInactive=` (ATTENDANCE_VIEW, SHIFT_MANAGE, EMPLOYEE_EDIT), `POST`, `PUT {uuid}`, `PATCH {uuid}/active` (SHIFT_MANAGE); `POST {uuid}/assign { employeeUuids[], effectiveFrom }` creates records with reason `Other`. 409 `SHIFT_CODE_TAKEN`, `SHIFT_IN_USE`.
`Holiday { uuid, date, name, countryCode, branchUuid?, isEstimated }`. `GET ?year=&branchUuid=` (any HRM_ view or self), `POST`, `PUT {uuid}`, `DELETE {uuid}` (SHIFT_MANAGE). Editing an estimated date clears `isEstimated`. 409 `HOLIDAY_DUPLICATE`.

### 6.2 Attendance `api/hrm/attendance`
`POST events { employeeUuid, eventTimeUtc, eventType?, source?, externalEventId?, note? }` (ATTENDANCE_MANAGE) → `{ uuid, duplicate }`; duplicate `(source, externalEventId)` returns the original, no error; recomputes that day.
`POST events/self { eventType }` (SELF_SERVICE, source WEB).
`POST events/import` multipart CSV `employeeNumber,eventTimeUtc,eventType,externalEventId` (ATTENDANCE_MANAGE) → `{ total, imported, duplicates, errors: { row, message }[] }`.
`GET days?employeeUuid=&departmentUuid=&branchUuid=&from=&to=&status=&page=` (ATTENDANCE_VIEW, scoped) → page of `AttendanceDay { uuid, employeeUuid, employeeName, date, shiftName?, firstInUtc?, lastOutUtc?, workedMinutes, lateMinutes, earlyMinutes, overtimeMinutes, status, isManualOverride, overrideNote? }`.
`GET days/me?from=&to=` (SELF_SERVICE). `POST days/recompute { employeeUuid?, from, to }` (ATTENDANCE_MANAGE). `PUT days/{uuid} { status, overrideNote }` (ATTENDANCE_MANAGE; sticky against recompute).
`GET summary?employeeUuid=&year=&month=` (ATTENDANCE_VIEW, or own with SELF_SERVICE) → `{ workingDays, presentDays, lateDays, halfDays, absentDays, leaveDays, paidLeaveDays, unpaidLeaveDays, holidays, weeklyOffs, workedMinutes, overtimeMinutes, approvedOvertimeMinutes }`.

### 6.3 Overtime `api/hrm/overtime`
`{ uuid, employeeUuid, employeeName, date, minutes, multiplier, status, reason?, decidedByName?, decidedAt?, decisionNote?, paid }`. `GET ?status=&from=&to=&employeeUuid=` (ATTENDANCE_VIEW, OVERTIME_APPROVE, scoped). `POST { employeeUuid, date, minutes?, multiplier?, reason? }` (ATTENDANCE_MANAGE; minutes default from the day). `POST {uuid}/approve`, `POST {uuid}/reject { note }` (OVERTIME_APPROVE). 409 `OVERTIME_ALREADY_DECIDED`, `OVERTIME_DUPLICATE`.

### 6.4 Leave `api/hrm/leave`
`LeaveType { uuid, code, name, isPaid, carryForwardCap?, attachmentAfterDays?, genderRestriction?, allowNegativeBalance, isActive, policies: { uuid, gradeUuid?, entitlement, accrualMethod, effectiveFrom, effectiveTo? }[] }`. `GET types`, (LEAVE_VIEW, LEAVE_MANAGE, SELF_SERVICE), `POST types`, `PUT types/{uuid}`, `PATCH types/{uuid}/active`, `POST types/{uuid}/policies`, `PUT policies/{uuid}` (LEAVE_MANAGE).
`GET balances?employeeUuid=&year=` (LEAVE_VIEW scoped, or own) → `{ leaveTypeUuid, leaveTypeCode, leaveTypeName, allocated, used, carried, adjusted, balance, pending }[]`. `GET balances/me?year=`. `GET ledger?employeeUuid=&leaveTypeUuid=&year=` → entries.
`POST allocate { employeeUuid, leaveTypeUuid, days, year, note? }`, `POST allocate/bulk { leaveTypeUuid, year }` → `{ allocated, skipped }`, idempotent per employee + type + year. `POST carry-forward { fromYear, toYear }` → `{ processed, carried, expired }`, idempotent (LEAVE_MANAGE).
`LeaveRequest { uuid, requestNumber, employeeUuid, employeeName, leaveTypeUuid, leaveTypeName, fromDate, toDate, halfDay, days, reason?, status, attachmentUuid?, approvalUuid?, decidedByName?, decidedAt?, decisionNote? }`. `GET requests?status=&employeeUuid=&from=&to=&page=` (LEAVE_VIEW, _APPROVE scoped), `GET requests/me`, `GET requests/{uuid}`, `GET calendar?from=&to=&departmentUuid=` (team view: names + dates only).
`POST requests { employeeUuid?, leaveTypeUuid, fromDate, toDate, halfDay, reason?, attachmentUuid? }` (SELF_SERVICE for self; LEAVE_MANAGE for another). Holidays and weekly offs do not count. Submits to workflow `LEAVE_REQUEST`.
`POST requests/{uuid}/approve`, `/reject { note }` (LEAVE_APPROVE; also via the workflow inbox), `POST requests/{uuid}/cancel` (owner or LEAVE_MANAGE; restores the ledger).
Errors: 409 `LEAVE_INSUFFICIENT_BALANCE`, `LEAVE_OVERLAP`, `LEAVE_ALREADY_DECIDED`, `HRM_NO_APPROVER`; 400 `LEAVE_ATTACHMENT_REQUIRED`, `LEAVE_GENDER_RESTRICTED`, `LEAVE_NO_WORKING_DAYS`.

---

## 7. Salary (PAY) — feature `FEATURE_HRM_PAYROLL`

`SalaryComponent { uuid, code, name, type, isTaxable, inGross, calcMethod, defaultValue?, percentOf?, formula?, postingRole?, expenseRole?, isSystem, isActive, sortOrder }`. `GET api/hrm/salary-components` (PAYROLL_CONFIG, PAYROLL_VIEW, SALARY_EDIT), `POST`, `PUT {uuid}`, `PATCH {uuid}/active` (PAYROLL_CONFIG). A system component's code and type cannot change. 409 `COMPONENT_CODE_TAKEN`, 400 `COMPONENT_FORMULA_INVALID`, `COMPONENT_ROLE_UNKNOWN`.
`SalaryStructure { uuid, code, name, description?, isDefault, isActive, lines: { uuid, componentUuid, componentCode, sequence, formulaOverride?, valueOverride? }[] }`. `GET api/hrm/salary-structures`, `GET {uuid}`, `POST`, `PUT {uuid}`, `PATCH {uuid}/active` (PAYROLL_CONFIG; view with SALARY_EDIT).
`POST api/hrm/salary-formula/test { formula, variables: { name: value } }` (PAYROLL_CONFIG) → `{ valid, result?, error? }`. Grammar: numbers, `+ - * /`, parentheses, `MIN MAX IF ROUND SLAB`, variables `BASIC GROSS TOTAL_EARNINGS TAXABLE_INCOME WORKING_DAYS PAID_DAYS OT_HOURS` + any component code seen earlier in the structure. A hand-written parser: no `eval`, no reflection, no file or network access, length and depth limited.
`SalaryAssignment { uuid, employeeUuid, employeeName, structureUuid, structureName, effectiveFrom, effectiveTo?, approvalStatus, changeReason?, approvedByName?, approvedAt?, lines: { componentUuid, componentCode, amount }[], gross }`. All S2.
`GET api/hrm/employees/{uuid}/salary`, `GET .../salary/current` (SALARY_VIEW, scoped; own: SELF_SERVICE). `POST .../salary { structureUuid, effectiveFrom, changeReason?, lines[] }` (SALARY_EDIT) Draft; `POST api/hrm/salary-assignments/{uuid}/submit` → workflow `SALARY_CHANGE`; `/approve` (SALARY_APPROVE; closes the previous at start − 1, previous → Superseded), `/reject { note }`. `POST api/hrm/salary-assignments/bulk-revision { percent?, amount?, componentCode, effectiveFrom, employeeUuids[], reason }` (SALARY_EDIT) → Drafts. 409 `SALARY_OVERLAP`, `SALARY_NOT_DRAFT`; 400 `SALARY_BELOW_GRADE_MIN`, `SALARY_ABOVE_GRADE_MAX` (warning-free hard check only when the grade has a band).
`TaxTable { uuid, code, name, countryCode, effectiveFrom, effectiveTo?, isVerified, verifiedByName?, verifiedAt?, sourceNote?, slabs: { fromAmount, toAmount?, fixedAmount, ratePercent }[] }` `GET/POST/PUT api/hrm/tax-tables`, `StatutoryRate { uuid, rateType, name, countryCode, employeePercent, employerPercent, wageCeiling?, fixedBasis?, effectiveFrom, effectiveTo?, isVerified, ..., sourceNote? }` `GET/POST/PUT api/hrm/statutory-rates` (PAYROLL_CONFIG). Editing a verified row clears verification. `POST .../{uuid}/verify { note }`, `POST .../{uuid}/unverify` (STATUTORY_VERIFY). The screen must say plainly that seeded values are unverified and need a payroll consultant.

## 8. Recruitment, performance, training (TAL)

Features `FEATURE_HRM_RECRUITMENT`, `_PERFORMANCE`, `_TRAINING`.
`Vacancy { uuid, vacancyNumber, title, designationUuid, designationName, departmentUuid, departmentName, branchUuid?, positions, status, description?, closingDate?, applicationCounts: { stage: n } }` `GET api/hrm/vacancies?status=`, `GET {uuid}`, `POST`, `PUT {uuid}`, `PATCH {uuid}/status`.
`Candidate { uuid, firstName, lastName, email?, phone?, source?, resumeAttachmentUuid?, notes?, applications[] }` `GET api/hrm/candidates?search=`, `GET {uuid}`, `POST` (409 `CANDIDATE_DUPLICATE` unless `force`, body lists matches), `PUT {uuid}`, `DELETE {uuid}`.
`Application { uuid, candidateUuid, candidateName, vacancyUuid, vacancyTitle, stage, rating?, rejectionReason?, hiredEmployeeUuid? }` `GET api/hrm/applications?vacancyUuid=&stage=`, `GET api/hrm/vacancies/{uuid}/board` → `{ stage, applications[] }[]`, `POST api/hrm/applications { candidateUuid, vacancyUuid }` (409 `APPLICATION_DUPLICATE`), `PATCH {uuid}/stage { stage, rejectionReason? }` (409 `APPLICATION_STAGE_TRANSITION`; Hired only via convert), `POST {uuid}/convert { joinDate, designationUuid?, departmentUuid?, branchUuid?, managerUuid?, employmentType? }` (RECRUITMENT_MANAGE + EMPLOYEE_EDIT) → `EmployeeDetail`, atomic; 409 `APPLICATION_ALREADY_HIRED`.
`Interview { uuid, applicationUuid, interviewerUuid?, interviewerName?, scheduledAt, feedback?, score?, result }` `GET/POST api/hrm/applications/{uuid}/interviews`, `PUT api/hrm/interviews/{uuid}`.
`Offer { uuid, applicationUuid, offeredSalary? (S2, masked), proposedJoinDate?, status, letterAttachmentUuid?, notes? }` `POST api/hrm/applications/{uuid}/offers`, `PUT api/hrm/offers/{uuid}`, `PATCH {uuid}/status`, `POST {uuid}/letter`, `GET {uuid}/letter` (PDF).
Performance (VIEW scoped; MANAGE): `ReviewCycle { uuid, name, period, startDate, endDate, status, completion: { total, submitted, acknowledged } }` `GET/POST/PUT api/hrm/review-cycles`, `POST {uuid}/open` (creates reviews for employed staff), `POST {uuid}/close`. `Review { uuid, employeeUuid, employeeName, cycleUuid, reviewerUuid?, overallRating?, status, managerComments?, employeeComments?, goals[] }` `GET api/hrm/reviews?cycleUuid=&status=`, `GET {uuid}`, `GET reviews/me`, `PUT {uuid}`, `POST {uuid}/submit` (rating = Σ(weight × managerRating) / Σ weight; 409 `REVIEW_GOALS_UNRATED`), `POST {uuid}/acknowledge` (the employee). `Goal { uuid, employeeUuid, cycleUuid, title, description?, weight, selfRating?, managerRating?, status }` `GET/POST api/hrm/goals`, `PUT/DELETE {uuid}`; self sets selfRating, manager sets managerRating. `GET api/hrm/performance/dashboard?cycleUuid=` → completion + rating distribution.
Training: `Course { uuid, code, name, description?, provider?, durationHours?, cost?, isActive }` `GET/POST/PUT api/hrm/courses`. `Enrollment { uuid, employeeUuid, employeeName, courseUuid, courseName, enrolledDate, completedDate?, status, certificateAttachmentUuid? }` `GET api/hrm/enrollments?courseUuid=&employeeUuid=`, `GET enrollments/me`, `POST`, `POST {uuid}/complete { completedDate, certificateAttachmentUuid? }`, `POST {uuid}/cancel`.

---

## 9. Payroll (PAY) — feature `FEATURE_HRM_PAYROLL`

`PayrollRun { uuid, runNumber, periodStart, periodEnd, payDate, payrollGroup, status, currencyCode, totalEmployees, blockedEmployees, totalGross, totalDeductions, totalNet, totalEmployerCost, calculatedAt?, reviewedAt?, approvedAt?, finalizedAt?, postedAt?, paidAt?, paymentReference?, paidFromAccount, postingDate?, postingNote?, costCenterMode, reversalOfRunUuid?, reversalReason?, postings[], allowedActions[] }`

`PayrollRunLine { uuid, employeeUuid, employeeNumber, employeeName, departmentName?, designationName?, costCenterCode?, workingDays, paidDays, workedDays, gross, totalDeductions, net, totalEmployerCost, isBlocked, blockReason?, paymentMethod, components: { code, name, type, amount, basis? }[] }`

| Method, route (`api/hrm/payroll-runs`) | Permission | Notes |
|---|---|---|
| `GET ?status=&from=&to=&page=` | PAYROLL_VIEW | |
| `POST { periodStart, periodEnd, payDate, payrollGroup? }` | PAYROLL_RUN | 409 `PAYROLL_RUN_EXISTS` |
| `GET {uuid}`, `GET {uuid}/lines?search=&blockedOnly=&page=` | PAYROLL_VIEW | S2 |
| `POST {uuid}/calculate` | PAYROLL_RUN | Draft/Calculated only; rewrites lines. > 200 employees → 202 and a Hangfire job with org captured; `GET {uuid}/progress` |
| `POST {uuid}/review` | PAYROLL_APPROVE | Calculated → UnderReview |
| `POST {uuid}/approve` | PAYROLL_APPROVE | → Approved; refuses blocked lines unless `{ excludeBlocked: true }` (409 `PAYROLL_HAS_BLOCKED`) |
| `POST {uuid}/reopen` | PAYROLL_APPROVE | UnderReview/Approved → Calculated |
| `GET {uuid}/posting-readiness` | PAYROLL_VIEW | §10.1 |
| `POST {uuid}/finalize` | PAYROLL_FINALIZE | checks G-21 (409 `PAYROLL_STATUTORY_UNVERIFIED` lists them), readiness (409 `POSTING_READINESS` + detail), then Finalized, then posts after commit. Result: `Posted`, or `Finalized` with `postingNote`. No target: stays Finalized, `postings[].status = NoTarget` |
| `POST {uuid}/retry-posting` | PAYROLL_FINALIZE | idempotent by key |
| `GET {uuid}/bank-file` | PAYROLL_PAY | CSV: name, bank, account, IBAN, amount, reference. Finalized/Posted/Paid. Logs one `BANK_ACCOUNT` access per employee. 409 `PAYROLL_NOT_PAYABLE` |
| `POST {uuid}/pay { paidOn, reference, paidFromAccount? }` | PAYROLL_PAY | → Paid; posts `PayrollPayment` for lines actually paid. From Finalized allowed only when there is no active target (G-19), else from Posted |
| `POST {uuid}/reverse { reason }` | PAYROLL_REVERSE | Finalized/Posted (Paid: 409 `PAYROLL_ALREADY_PAID`); gateway `VoidAsync`; loan installments, overtime and adjustments released; → Reversed |
| `POST {uuid}/cancel` | PAYROLL_RUN | before Finalized |
| `POST {uuid}/payslips` | PAYROLL_RUN | generates all; Finalized on |
| `GET {uuid}/lines/{line}/payslip` | PAYROLL_VIEW | PDF |
| `GET {uuid}/journal` | PAYROLL_VIEW | the summarized lines |
| `GET {uuid}/journal-export/csv`, `/xlsx` | PAYROLL_VIEW | G-19, always from Finalized; cols account role, account code, cost center, debit, credit, description |
| `GET {uuid}/reconciliation` | PAYROLL_VIEW, REPORTS_VIEW | run totals vs per-target totals (§7.8) |

Adjustments `api/hrm/payroll-adjustments`: `{ uuid, employeeUuid, employeeName, runUuid?, componentCode, amount, reason, appliedInRunUuid? }`, `GET ?employeeUuid=&pending=`, `POST`, `DELETE {uuid}` (PAYROLL_RUN; not once applied). Self: `GET api/hrm/payslips/me?year=` → `{ runNumber, periodStart, periodEnd, net, payslipUuid }[]`, `GET api/hrm/payslips/{uuid}/pdf` (own with SELF_SERVICE, else PAYROLL_VIEW + scope). `GET api/hrm/employees/{uuid}/payroll-history` (PAYROLL_VIEW, scoped).

Calculation order (task 247–260): employed in period (`IHrmEmployeeDirectory.GetEmployedInPeriodAsync`) → approved assignment in force at period end → paid days (`IAttendancePayInput` §13, else full month) → components in sequence → BASIC pro rata = BASIC × paidDays / workingDays (divisor from settings, else calendar days) → tax = annualized `SLAB` / 12 → EOBI, PF, SS from rates (blocks when none) → `IPayrollInputProvider`s (loans, advances, reimbursements, adjustments) → net = gross − deductions (net < 0 blocks) → snapshot. Blocks: no approved assignment, missing bank account when method is Bank, no rate. Postable run excludes blocked lines. Rounding: 2 dp per component; difference to `DEFAULT_ROUNDING` at posting.

Posting matrix: analysis §7.4 of the FSD as modified by §11A.2: expense lines per account role × cost center (shares from `GetCostCenterSharesAsync`); liabilities once, on the payroll cost center's code (BR-G5-003).

### 9.1 Emailing payslips (A50 D-30 / D-93) — `PayslipEmailController`, feature `FEATURE_HRM_PAYROLL`

| Method, route | Permission | Notes |
|---|---|---|
| `POST api/hrm/payslips/{uuid}/email` (no body) | PAYROLL_VIEW (in HR scope) or SELF_SERVICE (own payslip) | Same access as `GET payslips/{uuid}/pdf`: anyone else, or an unknown uuid, is 404. Finalized/Posted/Paid runs only: 409 `PAYROLL_NOT_FINALIZED` (also for a reversed run). 422 `PAYSLIP_NO_EMAIL` when the employee has no valid address on file. 409 `PAYSLIP_EMAIL_UNAVAILABLE` when the server has no email sender. |
| `POST api/hrm/payroll-runs/{uuid}/payslips/email { resend? }` | PAYSLIP_EMAIL (D-98; not PAYROLL_PAY), and access to all employees (403 otherwise; also checked inside the service) | Generates missing payslips, then mails each employee once; those already mailed for this run are skipped (`alreadySent`) unless `resend: true`. 409 `PAYROLL_NOT_FINALIZED` before Finalized or after Reversed. |

- **Recipient.** Always the employee's own address on file: `workEmail`, else `personalEmail`, and only if it is one plain valid address (a list, a display name or a CR/LF is ignored). There is no `to`, `cc` or `bcc` in either request; this is deliberate.
- **Content.** The organization's `PayslipSent` email template (A50; placeholders `{EmployeeName} {PayPeriod} {NetPay} {CurrencyCode}` and the company ones), else the built-in text; the payslip PDF is attached. Values are HTML-encoded by the template renderer.
- **Answers.** One: `{ sent, outcome: SENT | SEND_FAILED, sentTo? }`, `sentTo` masked (`a***@example.com`) and present only on success; a sender failure is `sent: false` with no detail. Run: `{ sent, alreadySent, noAddress, failed, noAddressEmployeeNumbers[], failedEmployeeNumbers[] }` (employee numbers only; no names, no amounts). Responses carry `Cache-Control: private, no-store`.
- **Record.** Each successful send adds a sensitive-access row (`PAYSLIP_EMAIL`, employee, user, `Payslip emailed <run number>`), like the bank file. Logs hold an outcome and the payslip/run uuid, never a name, address or amount.

## 10. Posting and profiles (POST)

### 10.1 Readiness
`GET api/hrm/payroll-runs/{uuid}/posting-readiness` → `{ isReady, noTarget, internalGlActive, quickBooksActive, internalGlReady, quickBooksReady, missingGlRoles[], missingQboAccounts: { role, accountCode? }[], missingCostCenters: { code, name }[], currencyMismatch?: { orgCurrency, qboCurrency }, unverifiedStatutory: { kind, name }[] }`. `IPayrollPostingReadinessService.CheckAsync(org, runId)`.

### 10.2 Enablement (FD-9)
`POST api/tenant/modules/MODULE_HRM/enable` refuses with 422 `PAYROLL_COST_CENTER_REQUIRED` until the org's `HrmSettings.PayrollCostCenterId` is set: new Shared seam `IModuleEnablementGuard { ModuleCode; Task CheckAsync(org) }`, implemented in Hrm, called by Tenancy from `ModuleRegistryService.EnableAsync` **and** the super-admin licence path. Because the setting's route is gated by the module, the wizard uses these two routes, gated by `MODULE_HRM` licence not switch:
`GET api/hrm/setup/status` (HRM_SETTINGS_MANAGE or MODULES_MANAGE) → `{ licensed, enabled, payrollCostCenter?, mode, costCenters[] }`.
`POST api/hrm/setup/payroll-cost-center { costCenterUuid? | newCostCenter: { code, name, parentUuid?, branchUuid? }, mode }` (HRM_SETTINGS_MANAGE; inline create also HRM_ORG_MANAGE) → settings. Then the existing enable route.

### 10.3 Profiles
`GET api/tenant/module-profiles` (MODULES_VIEW or PLATFORM_SUPER_ADMIN) → `P2, P2-S, P3, P-PK` with module lists. `POST api/tenancy/organizations/{id}/apply-profile { code }` (PLATFORM_SUPER_ADMIN and a real super admin, `[RequireSuperAdmin]`; an org admin gets 403). Data over real codes (D-14). Applying P2/P2-S/P-PK/P3 never enables HRM without §10.2.

### 10.4 Cost center → QuickBooks (G-5) `api/integrations/quickbooks/cost-center-mappings`
`{ uuid, costCenterCode, costCenterName, qboClassRef?, qboClassName?, qboDepartmentRef?, qboDepartmentName?, isActive, isMapped }`. `GET` (INTEGRATION_VIEW, HRM_ACCOUNT_MAPPING_VIEW), `GET unmapped`, `PUT {code}`, `POST sync` (auto-populate from HRM cost centers; INTEGRATION_MANAGE, HRM_ACCOUNT_MAPPING_EDIT). Integration cannot reference Hrm: new Shared seam `IHrmCostCenterSource.ListAsync(org)`.

### 10.5 Sync control (G-15)
Existing `GET/PUT api/integrations/quickbooks/sync-control…` gains the 8 HRM types. Enable refuses (409 `HRM_SYNC_NOT_READY` + detail) until accounts, cost centers and currency check out. POST: add to `SyncControlDocumentTypes.All`, labels, `NewConnection` → off + hold; hide these rows when MODULE_HRM is off.

### 10.6 JournalEntry push (G-1)
New `SyncKind.JournalEntry`; payload built by `IQuickBooksJournalEntryBuilder` from `FinancialTransaction`; routed in the QuickBooks target when `tx.DocumentType` is an HRM type or `Expense` with `SourceModule == hrm`. Role → account → QBO via `IExternalAccountMapReader`. Liability lines take the payroll cost center's Class. Reversal = opposite JE. > 900 lines splits per Class group; each batch balanced with key `:batch{N}`. Privacy safety net rejects a name in any description. Real sandbox E2E needs the user's Intuit keys: state plainly if not run.

### 10.7 GL setup status
`GET api/finance/setup/status` adds `hrRolesMissing: string[]` (HR keys unassigned while HRM is on; empty otherwise). **Does not change the status** (analysis A-4).

### 10.8 Import (FD-8, E-14)
`IImportTarget` `HRM_EMPLOYEE`, Odoo-flavoured profile (FSD Appendix A). Available when MODULE_HRM is on. Mapping docs for SAP only.

### 10.9 Service invoice (A-14, E-13) `api/sales-invoices/service`
`POST { customerUuid, invoiceDate, dueDate?, currencyCode, lines: { description, quantity, unitPrice, incomeAccountCode?, taxCode? }[] }` (`SALES_INVOICE_MANAGE`) → the normal detail, `SourceType = SERVICE_MANUAL`, no sale order, no delivery; posts via the existing A44 path. Line income account falls to `DEFAULT_REVENUE`.

---

## 11. Loans, settlement, increments, letters (LIFE) — feature `FEATURE_HRM_LOANS`

`Loan { uuid, loanNumber, employeeUuid, employeeName, type, principal, installmentCount, monthlyAmount, startMonth, status, reason?, approvedByName?, approvedAt?, disbursedOn?, paidFromAccount, paymentReference?, postingState, postingNote?, outstanding, installments: { uuid, sequence, dueMonth, amount, status, runNumber? }[] }`
`GET api/hrm/loans?status=&employeeUuid=&page=` (LOAN_VIEW, scoped), `GET {uuid}`, `GET loans/me`, `POST { employeeUuid, type, principal, installmentCount, startMonth, reason? }` (LOAN_MANAGE; last installment absorbs rounding), `PUT {uuid}` Draft, `POST {uuid}/approve`, `/reject { note }` (LOAN_APPROVE), `POST {uuid}/disburse { disbursedOn, paidFromAccount, reference? }` (LOAN_MANAGE; posts `LoanDisbursement`), `POST {uuid}/retry-posting`, `POST installments/{uuid}/waive { note }` (LOAN_APPROVE). 409 `LOAN_NOT_DRAFT`, `LOAN_NOT_APPROVED`, `LOAN_ACTIVE_EXISTS` (same type). `Advance` posts to `DEFAULT_EMPLOYEE_ADVANCE`, others to `DEFAULT_EMPLOYEE_LOAN_RECEIVABLE`.
`FinalSettlement { uuid, settlementNumber, employeeUuid, employeeName, lastWorkingDay, serviceYears, lastDrawnSalary, totalEarnings, totalDeductions, net, status, notes?, paidOn?, paidFromAccount, paymentReference?, postingState, postingNote?, lines: { code, description, type, amount, basis?, postingRole? }[] }`
`GET api/hrm/settlements?status=` (SETTLEMENT_VIEW), `GET {uuid}`, `POST compute { employeeUuid, lastWorkingDay }` (SETTLEMENT_MANAGE; Draft: remaining salary + leave encashment + gratuity − loans − advances; only one non-cancelled per employee, 409 `SETTLEMENT_EXISTS`), `PUT {uuid}` Draft line edits, `POST {uuid}/approve`, `/post`, `/pay { paidOn, paidFromAccount, reference? }`, `/cancel`, `/retry-posting`. Gratuity uses the `GRATUITY` statutory rate: unverified blocks `/post` (409 `PAYROLL_STATUTORY_UNVERIFIED`).
`IncrementProposal { uuid, employeeUuid, employeeName, currentBasic?, newBasic?, percent?, effectiveDate, reviewUuid?, status, reason?, decidedByName?, appliedAssignmentUuid? }` (S2). `GET api/hrm/increments?status=&cycleUuid=`, `POST`, `POST bulk { reviewCycleUuid, percentByRating }`, `POST {uuid}/approve`, `/reject`, `/apply` (INCREMENT_*; creates a Draft → Approved assignment, previous superseded).
`LetterTemplate { uuid, code, name, kind, subject, body, isSystem, isActive }` `GET/POST/PUT api/hrm/letter-templates` (LETTER_ISSUE). Placeholders `{EmployeeName} {EmployeeNumber} {Designation} {Department} {JoinDate} {LeaveDate} {Salary} {OrganizationName} {Date}`; unknown placeholder 400 `LETTER_PLACEHOLDER_UNKNOWN`; `{Salary}` needs SALARY_VIEW.
`IssuedLetter { uuid, letterNumber, employeeUuid, employeeName, kind, issuedAt, issuedByName? }` `GET api/hrm/letters?employeeUuid=`, `POST { employeeUuid, templateUuid }`, `GET {uuid}/pdf`, `GET letters/me`.
`TaxCertificate { uuid, employeeUuid, employeeName, taxYear, totalIncome, totalTax, generatedAt }` `POST api/hrm/tax-certificates/generate { employeeUuid?, taxYear }` (TAX_CERT_VIEW; idempotent per employee + year), `GET ?taxYear=`, `GET {uuid}/pdf`, `GET tax-certificates/me`. Says it is unverified while any rate it used is.

## 12. Expense claims and remittances (LIFE) — feature `FEATURE_HRM_EXPENSES`

`ExpenseCategory { uuid, code, name, accountCode?, accountRole?, defaultTaxCode?, requiresReceipt, receiptThreshold?, ratePerUnit?, unitName?, isActive }` `GET api/hrm/expense-categories` (EXPENSE_VIEW, SELF_SERVICE, ACCOUNT_MAPPING_VIEW), `POST`, `PUT {uuid}`, `PATCH {uuid}/active` (HRM_ACCOUNT_MAPPING_EDIT, SETTINGS_MANAGE).
`ExpenseClaim { uuid, claimNumber, employeeUuid, employeeName, claimDate, status, paymentMode, settlementMethod, currencyCode, totalAmount, totalTax, description?, approvalUuid?, submittedAt?, decidedByName?, decidedAt?, decisionNote?, paidOn?, paidInRunNumber?, paidFromAccount, paymentReference?, postingState, paymentPostingState, postingNote?, lines: { uuid, categoryUuid, categoryName, expenseDate, description?, amount, quantity?, taxCode?, taxAmount, costCenterCode?, attachmentUuid? }[] }`
`GET api/hrm/expense-claims?status=&employeeUuid=&page=` (EXPENSE_VIEW, _APPROVE, _PAY, scoped), `GET {uuid}`, `GET expense-claims/me`, `POST`, `PUT {uuid}` (own Draft; SELF_SERVICE), `POST {uuid}/submit` → workflow `EXPENSE_CLAIM` (400 `EXPENSE_RECEIPT_REQUIRED`, `EXPENSE_NO_LINES`; 409 `HRM_NO_APPROVER`), `POST {uuid}/approve`, `/reject { note }` (EXPENSE_APPROVE; approve posts `Expense`; 409 `EXPENSE_CATEGORY_UNMAPPED`), `POST {uuid}/pay { paidOn, paidFromAccount, reference? }` (EXPENSE_PAY, SeparatePayment; posts `ExpensePayment`), `POST {uuid}/retry-posting`. WithPayroll creates a `PayrollAdjustment` EXPREIMB consumed by the next run; the run marks the claim Paid. CompanyAccount: Dr expense, Cr bank/card clearing, nothing to reimburse.
Remittances: `GET api/hrm/remittances/tracker?year=&type=` (REMITTANCE_VIEW) → `{ type, periodStart, periodEnd, calculated, paid, balance }[]`, `GET api/hrm/remittances?type=&from=&to=`, `POST { remittanceType, periodStart, periodEnd, amount, paidOn, paidFromAccount, reference?, authorityName?, notes? }` (REMITTANCE_MANAGE; posts `StatutoryRemittance`, Dr payable / Cr bank-or-cash, no partner), `POST {uuid}/retry-posting`. 400 `REMITTANCE_OVERPAID` is a warning unless `force`.

---

## 13. In-process seams (Hrm project, internal; register in your partial)

Registration: create `Registration/HrmRegistration.<Agent>.cs`: `internal static partial class HrmRegistration { static partial void AddAtt(IServiceCollection s) { ... } }`. Controllers derive from `HrmControllerBase` (`Caller`, `Done(...)`, seed filter). Status handler for your workflow code: `services.AddScoped<IDocumentStatusHandler, ...>()`.

| Seam | Provided by | Use |
|---|---|---|
| `IHrmScopeService` | CORE | `GetAsync`, `Apply`, `RequireEmployeeAsync(caller, uuid)`, `CanAccessAsync(caller, id)`, `RequireOwnEmployeeAsync`, `OwnEmployeeIdAsync` |
| `IHrmSensitiveData` | CORE | `Protect`, `Hash`, `Reveal`, masks |
| `IHrmEmployeeDirectory` | CORE | `GetTermsAsync`, `GetEmployedInPeriodAsync`, `GetCostCenterSharesAsync`, `GetPrimaryBankDetailsAsync`, `GetSettingsAsync` |
| `IHrmApprovalGateway` | CORE | `SubmitAsync(caller, code, docUuid, docNumber, employeeId, fallbackPermission, conditionValue?)` |
| `IHrmDocumentStore` | CORE | `StorePdfAsync`, `ReadAsync` |
| `IHrmSeeder` | CORE | `EnsureAsync(org)`; jobs call it |
| `IEmploymentRecordFactory` | CORE | `AddAsync(caller, employee, request)` (TAL convert) |
| `HrmServiceBase` | CORE | `Stamp`, `SaveAsync` (409 on stale), `OptionalIdAsync`, `LiveRecords` |
| `HrmValidate`, `HrmErrors`, `HrmDbContext.InTransactionAsync` | CORE | |
| `IAttendancePayInput` | ATT defines, PAY consumes | `GetAsync(org, employeeId, from, to)` → `{ workingDays, paidDays, workedDays, approvedOvertimeMinutes }`; `MarkOvertimePaidAsync(org, runId, employeeId, from, to)`, `ReleaseOvertimeAsync(org, runId)` |
| `IPayrollInputProvider` (many) | PAY defines, LIFE implements | `GetInputsAsync(org, runId, employeeId, from, to)` → `{ componentCode, amount, reason }[]`; `MarkConsumedAsync(org, runId, employeeId)`; `ReleaseAsync(org, runId)` |
| `IPayrollPostingReadinessService` | POST | |
| `IPayrollPostingBuilder` | PAY | `BuildAsync(run)` → `FinancialTransaction` |
| `IHrmPostingHelper` | PAY defines, LIFE uses | wraps gateway + state: maps `PostingResult` to `HrmPostingStates` (D-11) |

Shared seams POST adds: `IModuleEnablementGuard`, `IHrmCostCenterSource`, `IQuickBooksPostingReadiness`.

Workflow: `HrmWorkflowCodes.LeaveRequest | ExpenseClaim | SalaryChange`, NAMED key `HR_APPROVER`, seeded by `IHrmSeeder`. Handler receives `APPROVED`, `REJECTED`, etc. via `UpdateStatusAsync(documentUuid, status)`; **the handler has only the tenant the engine runs under: use `IgnoreQueryFilters` + the row's own organization.**

## 14. Schema reference (58 tables, schema `hrm`; all have Id, Uuid, OrganizationId, Created*, Modified*)

H1 `HrmSettings CostCenters Branches Departments Designations Grades Employees EmploymentRecords EmploymentCostSplits EmployeeBankAccounts EmployeeDocuments HrUserScopes SensitiveAccessLogs`. H2 `Shifts Holidays AttendanceEvents AttendanceDays OvertimeRecords LeaveTypes LeavePolicies LeaveLedgerEntries LeaveRequests`. H3 `SalaryComponents SalaryStructures SalaryStructureLines SalaryAssignments SalaryAssignmentLines TaxSlabTables TaxSlabs StatutoryRates`. H4 `Vacancies Candidates JobApplications Interviews JobOffers ReviewCycles Reviews Goals Courses CourseEnrollments`. H5 `PayrollRuns PayrollRunLines PayrollRunLineComponents Payslips PayrollAdjustments PayrollPostings`. H6 `Loans LoanInstallments FinalSettlements FinalSettlementLines IncrementProposals LetterTemplates IssuedLetters TaxCertificates`. H6E `ExpenseCategories ExpenseClaims ExpenseLines StatutoryRemittances`. Plus `integration.CostCenterQboMappings`. Fields: `src/SMS.Modules.Hrm/Domain/HrmEntities.*.cs`. Immutable by `HrmDbContext`: SensitiveAccessLogs, AttendanceEvents, LeaveLedgerEntries, Payslips; PayrollRunLineComponents can be deleted on recalculation, never edited.

HR account roles: `DEFAULT_SALARY_EXPENSE OVERTIME_EXPENSE BONUS_EXPENSE EMPLOYER_CONTRIB_EXPENSE GRATUITY_EXPENSE LEAVE_ENCASHMENT_EXPENSE SALARY_PAYABLE EMPLOYEE_TAX_PAYABLE EOBI_PAYABLE PF_PAYABLE SOCIAL_SECURITY_PAYABLE OTHER_DEDUCTION_PAYABLE EXPENSE_CLAIM_PAYABLE GRATUITY_PROVISION EMPLOYEE_LOAN_RECEIVABLE EMPLOYEE_ADVANCE` (all `DEFAULT_…`; assign via existing `api/finance/accounting-defaults`).

---

## Changes

| Date | Who | What |
|---|---|---|
| 2026-10-31 | CORE | First version. |
| 2026-10-31 | POST | Seams (PAY, please read): `IPayrollPostingReadinessService` lives in `Services/Post/PostingReadiness.cs`: `CheckAsync(org, runId)` → §10.1 model (`isReady` also false while `unverifiedStatutory` is non-empty), `CheckTransactionAsync(org, tx, periodEnd)` for a tx you already built. Readiness gets the run's accounts and cost centers from your `IPayrollPostingBuilder.BuildAsync`. Optional metadata on your tx: `Metadata.Properties["Period"]`, `["Headcount"]` (`HrmPostingProperties` in Shared) make the QuickBooks PrivateNote exactly `{number}, {period}, {headcount} employees`; without them it is `{number}, {tx.Description}` and the name safety net still runs. Reverse with `gateway.VoidAsync` and set `VoidDate` (else the reversing JE takes the original's date). Export: `IPayrollJournalExportService.ToCsv(tx)` / `ToXlsx(tx, runNumber)` in `Services/Post/`, you own the routes. Shared additions: `IModuleEnablementGuard`, `IHrmCostCenterSource`, `IHrmPersonalIdentifierSource`, `IQuickBooksPostingReadiness`, `SyncKind.JournalEntry`, `JournalEntryPayload`. |
| 2026-10-31 | CORE, asked by TAL | New Shared seam `IOrganizationNameLookup.GetNameAsync(org, ct)` (Tenancy implements; null = unknown org), for `{OrganizationName}` in letters. Take it as an optional constructor parameter. TAL's `organizationName` body field and 400 `LETTER_ORGANIZATION_REQUIRED` remain the fallback only when the lookup gives nothing. |
| 2026-10-31 | CORE, asked by ATT | New Shared seam `IOrganizationTimeZoneLookup.GetTimeZoneAsync(org, ct)` (Tenancy implements): `tenant.Organizations.TimeZone` as stored, null when unset. Unvalidated free text; the caller validates and falls back. |
| 2026-10-31 | CORE, asked by LIFE | Fix: `HRM_PAYSLIP`, `HRM_LETTER` (offer letters too), `HRM_TAX_CERTIFICATE` can now be filed through `IHrmDocumentStore`. `AttachmentAccessPolicy.IsFiledOnly` lets `StoreGeneratedAsync` accept them with no access rule. `api/attachments` still refuses them: content 403, list and upload 400, delete 409. Read them only through HRM routes, after your own scope check. Diagram: [ER-DIAGRAM.md](ER-DIAGRAM.md). |
| 2026-10-31 | CORE, asked by FE (FE-1) | Uploads of `HRM_EMPLOYEE`, `HRM_LEAVE_REQUEST`, `HRM_EXPENSE_CLAIM`, `HRM_CANDIDATE`, `HRM_ENROLLMENT` now keep their bytes in the database (`AttachmentAccessPolicy.KeepsUploadsPrivate`), not under `wwwroot`. `documentId` may be a client-made uuid; no service checks it. Read with `GET api/attachments/{attachmentUuid}/content`, which checks the kind's rule, not the employee scope. `DELETE api/attachments/{uuid}` answers 409 for these files. |
| 2026-10-31 | CORE, asked by FE (FE-4) | New `GET api/hrm/user-scopes/users?search=` (`HRM_SETTINGS_MANAGE`) → `{ userId, name, email }[]`: active users of the caller's organization, at most 50. |
| 2026-10-31 | CORE, found by LIFE | `HrmCaller.UserName` is now filled on a real host through `HrmUserNames` (analysis D-24). `HrmSetupController` derives from `HrmControllerBase`, so `api/hrm/setup/*` also runs the seed filter. |
| 2026-10-31 | CORE, coordinator's decision (D-25) | **Replaces the read route in the FE-1 row above.** New, for the five HRM upload kinds: `GET api/hrm/attachments/{uuid}/content`, `GET api/hrm/attachments?interfaceCode=&rowUuid=` → `{ uuid, fileName, contentType, fileSize, uploadedDate }[]` (`rowUuid`: the employee, leave request, expense claim, candidate or enrollment), `DELETE api/hrm/attachments/{uuid}` (unused file, uploader only). Self or HR scope, else 404; no HRM code 403. `GET api/attachments/{uuid}/content` and `GET api/attachments?interface=` now answer 403 for these kinds; upload is unchanged. Errors: 400 `HRM_ATTACHMENT_INVALID` on any save whose uuid is unknown, the wrong kind, another organization's or another user's upload; 400 `HRM_ATTACHMENT_KIND_INVALID` on the list; 409 `ATTACHMENT_IN_USE` on delete. |
| 2026-10-31 | LIFE | §13 seams `IPayrollInputProvider`, `PayrollInput`, `IHrmPostingHelper`, `HrmPostingOutcome` defined in `Services/PayrollSeams.cs` because PAY had not yet (PAY: use these, do not redefine; change them there if you must, and say so here). `IPayrollInputProvider.GetInputsAsync` **reserves** what it returns for the run (an installment or claim reserved by another live run is not returned), so call it inside calculation; `MarkConsumedAsync` once per unblocked line at Finalize; `ReleaseAsync` on cancel, reverse or reopen. LIFE implements `IHrmPostingHelper` (`HrmPostingHelper`) and registers it in its partial. LIFE provides two providers: loans/advances (`LOAN`, `ADV`) and reimbursements (`EXPREIMB`). **PAY's own adjustments provider must skip `PayrollAdjustment` rows with `ExpenseClaimId != null`**; LIFE's provider owns those and marks the claim Paid. |
| 2026-10-31 | TAL | §8 additions: `GET api/hrm/applications/{uuid}`, `PATCH api/hrm/applications/{uuid}/rating { rating }` (1..5), `GET api/hrm/applications/{uuid}/offers`, `GET api/hrm/offers/{uuid}`, `GET api/hrm/review-cycles/{uuid}`, `GET api/hrm/courses/{uuid}`, `GET api/hrm/courses?includeInactive=`. Offer gains `salaryMasked`. Review gains `cycleName`, `reviewerName`. |
| 2026-10-31 | TAL | §8 bodies: `CANDIDATE_DUPLICATE` 409 has `result: { uuid, name?, matchedOn[] }[]` (name null outside scope). `POST offers/{uuid}/letter { templateUuid?, organizationName? }`, all optional. Dashboard: `{ completion, completionPercent, ratingDistribution: { rating (0..5, floor), count }[] }`. `completion.submitted` counts Submitted + Acknowledged. Course `isActive` is set through PUT. |
| 2026-10-31 | TAL | §8 errors added. 400: `APPLICATION_REJECTION_REASON_REQUIRED`, `LETTER_ORGANIZATION_REQUIRED`, `LETTER_PLACEHOLDER_UNKNOWN`, `CYCLE_DATES_INVALID`, `GOAL_RATING_INVALID`, `GOAL_WEIGHT_INVALID`, `ENROLLMENT_DATE_INVALID`. 409: `CANDIDATE_IN_USE`, `VACANCY_NOT_OPEN`, `VACANCY_FILLED`, `APPLICATION_CLOSED`, `OFFER_NOT_ACCEPTED`, `OFFER_ACTIVE_EXISTS`, `OFFER_NOT_DRAFT`, `OFFER_STATUS_TRANSITION`, `OFFER_SALARY_MISSING`, `OFFER_JOIN_DATE_MISSING`, `LETTER_TEMPLATE_MISSING`, `CYCLE_NOT_DRAFT`, `CYCLE_NOT_OPEN`, `CYCLE_CLOSED`, `REVIEW_NOT_DRAFT`, `REVIEW_NOT_SUBMITTED`, `REVIEW_LOCKED`, `COURSE_CODE_TAKEN`, `COURSE_INACTIVE`, `ENROLLMENT_DUPLICATE`, `ENROLLMENT_NOT_ACTIVE`, `ENROLLMENT_EMPLOYEE_INACTIVE`. |
| 2026-10-31 | TAL | §8 behaviour FE must know: convert only from stage Offer (and an Accepted offer if any exist); creating an offer moves the application to Offer; generating or downloading an offer letter needs `HRM_SALARY_VIEW`; list routes are plain arrays capped at 500. |
| 2026-10-31 | LIFE | §11/§12 built. Additions for FE: `POST letters` takes optional `organizationName` (used only when the lookup is empty; 400 `LETTER_ORGANIZATION_REQUIRED`). `TaxCertificate.isUnverified`. `POST increments/bulk` takes optional `effectiveDate`. `POST tax-certificates/generate` returns an **array**. `expense-categories` GET takes `includeInactive`. New codes: 409 `LOAN_INSTALLMENT_NOT_PENDING`, `SETTLEMENT_NOT_DRAFT`, `SETTLEMENT_NOT_APPROVED`, `SETTLEMENT_NOT_POSTED`, `SETTLEMENT_NOT_PAYABLE`, `SETTLEMENT_NOT_CANCELLABLE`, `SETTLEMENT_NOT_EMPLOYED`, `SETTLEMENT_NO_SALARY`, `SETTLEMENT_NO_JOIN_DATE`, `SETTLEMENT_STALE`, `SETTLEMENT_LOAN_IN_RUN`, `INCREMENT_NOT_DRAFT`, `INCREMENT_NOT_APPROVED`, `INCREMENT_NO_SALARY`, `LETTER_TEMPLATE_CODE_TAKEN`, `LETTER_NO_SALARY`, `EXPENSE_CATEGORY_CODE_TAKEN`, `EXPENSE_NOT_DRAFT`, `EXPENSE_NOT_SUBMITTED`, `EXPENSE_NOT_PAYABLE`, `EXPENSE_NOT_POSTABLE`, `REMITTANCE_DUPLICATE`; 400 `SETTLEMENT_LINE_LOCKED`, `SETTLEMENT_ROLE_INVALID`, `INCREMENT_VALUE_REQUIRED`. Expense line `amount` includes `taxAmount`. `PUT settlements/{uuid}` body `{ notes?, lines[] }` replaces the lines and must send `LOAN`/`ADV` unchanged. `/approve` on a claim answers 403 unless the caller is the workflow step's approver or an admin. |
| 2026-10-31 | ATT | §6 built. Additions for FE. Errors: 400 `ATTENDANCE_EVENT_FUTURE`, `ATTENDANCE_IMPORT_TOO_LARGE` (50,000 rows; file 10 MB), `OVERTIME_NO_MINUTES`, `LEAVE_SPANS_YEARS`, `LEAVE_TYPE_INACTIVE`; 409 `LEAVE_ALREADY_ALLOCATED`, `LEAVE_TYPE_CODE_TAKEN`, `LEAVE_TYPE_IN_USE`, `LEAVE_POLICY_OVERLAP`. `POST leave/allocate` takes optional `adjustment: true` (signed days; without it a second allocation is 409). `GET shifts/holidays` open with either the attendance or leave feature. `GET days` range defaults to the month, max 92 days, and creates missing absence rows. `presentDays` includes late days (`lateDays` is a subset). `shifts/{uuid}/assign` → `{ assigned }`, all or nothing; same start date corrects the record. `days/recompute` → `{ employees, days }`. `carry-forward`: `toYear` must be `fromYear + 1`; `carried`/`expired` are days. `calendar` lists approved leave only. `approve`/`reject` need the assigned approver as well as the permission (403 otherwise, D-ATT-5); `reject` and `overrideNote` need text. |
| 2026-10-31 | ATT | §13 `IAttendancePayInput` defined in `Services/Att/AttendancePayInput.cs` (PAY already consumes it). `AttendancePayData { employeeId, workingDays (the divisor), paidDays (same basis), workedDays, unpaidDays, scheduledWorkingDays, approvedOvertimeMinutes, weightedOvertimeMinutes, overtime[], attendanceTracked }`, `WeightedOvertimeHours`. Added `GetManyAsync`; `Mark`/`Release` return row counts. Semantics: D-ATT-3. |
| 2026-10-31 | TAL | `PUT api/hrm/interviews/{uuid}` now accepts `HRM_RECRUITMENT_VIEW` as well as `_MANAGE`. A VIEW holder must be the assigned interviewer (else 403 in scope, 404 outside): only `feedback`, `score`, `result` apply and the rest of the body is ignored. Read routes stay scoped, so that interviewer may be unable to list the interview. `PUT api/hrm/vacancies/{uuid}`: new 409 `VACANCY_POSITIONS_BELOW_HIRED`. |
| 2026-10-31 | POST | **Enablement (E2E authors, read this):** `LicenceAsync(org, ("MODULE_HRM", true))` no longer turns HRM on. The response lists it under `licensedNotEnabled` and the module stays off until the wizard + enable. Use `root.EnableHrmAsync(o)` from `tests/SMS.Integration.Tests/Hrm/HrmEnableKit.cs` (cost center coded `PAYROLL`). §10.2 otherwise as written. |
| 2026-10-31 | POST | Corrects my first row, after reading PAY's `PayrollRunSeams.cs`: I implement PAY's `IPayrollPostingReadinessService` / `PayrollPostingReadiness` as PAY defined it. Mappings only; `unverifiedStatutory` stays empty here and PAY merges it. `isReady` means mappings. QuickBooks counts only while the type sends now (D-POST-2). |
| 2026-10-31 | POST | §10.9: `incomeAccountCode` is refused (400 `SERVICE_INCOME_ACCOUNT_NOT_SUPPORTED`), not honoured: all service income posts to `DEFAULT_REVENUE` (D-POST-6). A draft comes back; issue with the normal `POST api/sales-invoices/{uuid}/issue`. Added `notes?`. `currencyCode?` null = sale base. Service invoices are not sent to QuickBooks. |
| 2026-10-31 | POST | §10.3: `GET api/tenant/module-profiles` → `{ code, name, description, modules[], optional[], off[] }[]` (MODULES_VIEW). `apply-profile` → `UpdateFeaturesResult` with `licensedNotEnabled: { featureCode, reason }[]`. Hiding uses the existing `featureCode` gating: `off` is what the menu hides. |
| 2026-10-31 | POST | §10.2: `GET setup/status` → `payrollCostCenter?` is a `CostCenterModel`, `costCenters[]` active only. Unlicensed: `{ licensed: false }` and nothing else filled. `POST setup/payroll-cost-center` unlicensed: 403; both or neither given: 400 `COST_CENTER_CHOICE`. §10.5: a 409 `HRM_SYNC_NOT_READY` carries `{ documentType, missingQboAccounts, missingCostCenters, currencyMismatch, problems }` in `result`. New codes on a blocked entry (sync dashboard): `JE_ACCOUNT_UNMAPPED`, `JE_COST_CENTER_UNMAPPED`, `JE_CURRENCY_MISMATCH`, `JE_CLASSES_OFF`, `JE_ACCOUNT_NEEDS_PARTY`; refused unstored: `JE_PERSONAL_DATA`, `JE_UNBALANCED`, `JE_CANNOT_SPLIT`, `JE_NO_COST_CENTER`. `SyncKind.JournalEntry` appears in sync lists. |
| 2026-10-31 | POST, asked by FE (SCHEMA-REQUESTS FE-2) | §10.4: `GET cost-center-mappings/reference` (INTEGRATION_VIEW, HRM_ACCOUNT_MAPPING_VIEW) → `{ connected, loaded, classes: { id, name }[], departments: { id, name }[], tracksClasses? }` from the stored reference snapshot (active rows only; never calls Intuit), so the mapping screen can offer pickers instead of typed ids. Not connected: 200 with `connected: false` and empty lists. Connected but never fetched: `loaded: false`; refresh reference data. |
| 2026-10-31 | PAY | §9 additions, all for FE. `PayrollRun` gains `correctionRunUuid?` (on the reverse answer: the new Draft), `calculationQueued` (true = 202, poll `progress`), `untrackedAttendanceEmployees` and `warnings[]`. `PayrollRunLine` gains `attendanceUntracked` (D-ATT-3: paid in full with no attendance on file; **highlight the line and show the warning on review**; it does not block). New codes: 409 `PAYROLL_STATUS`, `PAYROLL_BUSY`, `PAYROLL_CALCULATING`, `PAYROLL_NO_PAYABLE_LINES`, `PAYROLL_PERIOD_CLOSED`, `PAYROLL_STATUTORY_CHANGED`, `PAYROLL_INPUTS_CHANGED` (reopen and recalculate), `PAYROLL_BANK_MISSING`, `PAYROLL_NOT_FINALIZED`; 400 `PAYROLL_PERIOD_INVALID` (28 to 31 days only). `PAYROLL_RUN_EXISTS` also covers an overlapping period. §7 adds 409 `COMPONENT_SYSTEM_LOCKED`, `STRUCTURE_CODE_TAKEN`, `TAX_TABLE_CODE_TAKEN`, `TAX_TABLE_OVERLAP`, `STATUTORY_RATE_OVERLAP`, `SALARY_NOT_PENDING`, `SALARY_NONE`, `ADJUSTMENT_APPLIED`; 400 `TAX_SLABS_INVALID`, `SALARY_COMPONENT_MISSING`, `ADJUSTMENT_COMPONENT_INVALID`, `ADJUSTMENT_AMOUNT_INVALID`. Formula grammar also has `> < >= <= = <>` (IF needs them). `TAXABLE_INCOME` is annualized; a formula on ITAX yields annual tax and the run takes a twelfth; `OT_HOURS` has multipliers applied. |
| 2026-10-31 | PAY | Net = all earnings − deductions, so a reimbursement (`inGross = false`) is in net but not gross; §9's "gross − deductions" holds only without one. The bank file holds Bank-method lines only and needs `HRM_SCOPE_ALL` (403 otherwise). `GET {uuid}/bank-file` does not move the run to Paid (task 289 yields to this contract); `/pay` does. Seams: `IPayrollPostingReadinessService` and `IPayrollPostingBuilder` are in `Services/PayrollRunSeams.cs`; the default readiness is registered with TryAdd and POST's overrides it. ATT's `IAttendancePayInput` is used as defined, with `NullAttendancePayInput` as the TryAdd default. |
| 2026-10-31 | TAL | New `GET api/hrm/interviews/me` (`HRM_RECRUITMENT_VIEW`, under `FEATURE_HRM_RECRUITMENT`) -> `{ uuid, scheduledAt, feedback?, score?, result, applicationUuid, applicationStage, candidateName, vacancyTitle, designationName, departmentName }[]`, newest first, plain array capped at 500. Only the caller's own assigned interviews, whatever their HR scope. 404 `HRM_NO_EMPLOYEE` when the user has no employee. No contact details, resume, rating, offer or salary. Save feedback with `PUT api/hrm/interviews/{uuid}`. Do not link from it to application or vacancy routes: those stay scoped and can answer 404 for this caller. |
| 2026-10-31 | POST | §10.7: the route is `GET api/finance/setup/status` (A42's), not `api/finance/setup`. `hrRolesMissing` is on its response and on `complete`. |
| 2026-10-31 | PAY, asked by FE (FE-5) | `GET api/hrm/employees/{uuid}/payroll-history` items gain `lineUuid` and `payslipUuid?` (null until generated). Open `GET api/hrm/payslips/{payslipUuid}/pdf`; when null, `GET api/hrm/payroll-runs/{runUuid}/lines/{lineUuid}/payslip` generates it (409 `PAYROLL_NOT_FINALIZED` for a blocked line or a run before Finalized). Both apply the HR scope. |
| 2026-10-31 | TAL, answering FE-3 | **Who may do what on a review** (no API change). A request must pass the route codes, then the relationship; both are needed. Terms: *employee* = the review's employee is my linked employee (`GET employees/me`); *reviewer* = `reviewerUuid` is my employee; *HR* = holds `HRM_PERFORMANCE_MANAGE` with the employee in my scope; *manager side* = (reviewer or HR) and not employee. Nobody is on the manager side of their own review, even with MANAGE. **Read** `GET reviews/{uuid}` (VIEW, MANAGE or SELF_SERVICE): employee, reviewer, or VIEW/MANAGE with the employee in scope; else 404. `GET reviews` (VIEW, MANAGE): scoped. `GET reviews/me` (SELF_SERVICE): own. **`PUT reviews/{uuid}`** (MANAGE or SELF_SERVICE): manager side sets `managerComments` while Draft; employee sets `employeeComments` while Draft or Submitted; the other side's field is ignored; `reviewerUuid` needs MANAGE (403), must be in scope and not the employee (400). Closed cycle 409 `CYCLE_CLOSED`; Acknowledged 409 `REVIEW_LOCKED`; manager side after Draft 409 `REVIEW_LOCKED`. **`POST reviews/{uuid}/submit`** (MANAGE or SELF_SERVICE): manager side; Draft (409 `REVIEW_NOT_DRAFT`); cycle Open (409 `CYCLE_NOT_OPEN`). **`POST reviews/{uuid}/acknowledge`** (SELF_SERVICE or MANAGE): employee only; Submitted (409 `REVIEW_NOT_SUBMITTED`); allowed on a closed cycle. **`POST goals`, `DELETE goals/{uuid}`** (MANAGE or SELF_SERVICE): manager side. With no review yet, only HR. **`PUT goals/{uuid}`** (same codes): employee sets only `selfRating`; manager side sets the rest, not `selfRating`. All goal writes: cycle not Closed and review still Draft (409 `CYCLE_CLOSED`, `REVIEW_LOCKED`). **Refusals:** can read but not on the manager side = 403; cannot read = 404. **Traps:** a reviewer or employee holding only VIEW gets 403 at the route on every write, so the screen needs SELF_SERVICE or MANAGE too (D-18 gives every seeded role SELF_SERVICE; a custom role may lack it). `GET reviews` needs VIEW or MANAGE, so a reviewer with only SELF_SERVICE can open a review by uuid but **has no route that lists reviews assigned to them**. Open gap, not added here. |
| 2026-10-31 | TAL | New `GET api/hrm/reviews/assigned` (`HRM_SELF_SERVICE`, under `FEATURE_HRM_PERFORMANCE`) -> `Review[]`, same shape as `GET reviews` (with goals), newest first, plain array capped at 500. Only reviews where the caller's employee is the reviewer, whatever their HR scope, every cycle and status. 404 `HRM_NO_EMPLOYEE` when the user has no employee. This closes the gap in the FE-3 row above: use it as the reviewer's list, then the actions in that row. A reviewer HR replaced drops off immediately. Do not link from it to `employees/{uuid}`: that stays scoped and can answer 404 for this caller. |
| 2026-10-31 | POST, coordinator's decision | **Supersedes my earlier §10.9 row.** `incomeAccountCode` is honoured: an active Revenue/OtherIncome leaf code, else 400 `SERVICE_INCOME_ACCOUNT_INVALID`; returned on detail lines (`incomeAccountCode`). FE: the service invoice form can now offer it. Service invoices do go to QuickBooks: new provider default `ServiceItemRef` ("Service invoice item", reference kind `Item`, a free id: reference data has no item list); until set, the invoice is Blocked `SERVICE_ITEM_MISSING`. **§10.8 import:** starting, validating or running `HRM_EMPLOYEE` needs also HRM_EMPLOYEE_EDIT, HRM_EMPLOYMENT_MANAGE, HRM_EMPLOYEE_SENSITIVE_VIEW, HRM_BANK_EDIT, else 403 naming the missing ones. **§10.2:** setup routes seed nothing before the licence (`SkipHrmSeed`). |
| 2026-10-31 | POST, from DOC's review | **§10.8 `HRM_EMPLOYEE` import.** Rows encrypted at rest. Preview, `errors`, `errors/download`, `results/download` need HRM_EMPLOYEE_EDIT, HRM_EMPLOYMENT_MANAGE, HRM_EMPLOYEE_SENSITIVE_VIEW and HRM_BANK_EDIT (403 otherwise, IMPORT_MANAGE alone is not enough); `identification_id`, `bank_account_id` and every unmapped column read `*****`; no error carries those values. An error report re-uploaded must have them re-entered. New validate errors: `NAME_NEEDS_TWO_WORDS`; `MISSING_DEPENDENCY` on `parent_id` / `date_end` with no hire record; `INVALID_DATE` for anything but yyyy-MM-dd, dd-MMM-yyyy or an Excel date. Other import profiles unchanged. §10.3 and §10.7 text corrected in place. |
| 2026-11-09 | CORE (REV49-5), asked by REV-49B | New `GET api/hrm/employees/lookup?search=&take=` → `{ uuid, employeeNumber, fullName, status, departmentName? }[]` for employee pickers on pages gated by codes other than EMPLOYEE_VIEW/EDIT (any one of 20 HRM codes, see API-REFERENCE). HR-scoped, `take` ≤ 200, Archived and Candidate excluded, no contact, identity, bank or pay field. |
| 2026-11-10 | HRM-50 (A50 D-30) | New §9.1: `POST api/hrm/payslips/{uuid}/email` and `POST api/hrm/payroll-runs/{uuid}/payslips/email` (PayslipSent email; recipient is always the employee's own address; the run needs PAYROLL_PAY + all-employee scope). New seeder `HrmSetupSeeder` (A50 category `HRM`: leave types, salary components, tax slabs from `Packs/PK_GLOBAL/HRM.json`, kept equal to `HrmSeedData` by a test; never verifies anything) and `HrmOnboardingSignal` (checklist steps `HRM_SETUP`, `HRM_ADD_EMPLOYEE`, `HRM_VERIFY_TAX`). No table or migration change. |
| 2026-11-10 | HRM-50 (A50 D-97, D-98) | **Role matrix.** (1) `FINANCE_MANAGER` is granted `HRM_EXPENSE_APPROVE` (`AuthDataSeeder.HrmGrants`, idempotent, only a missing row is added, a grant switched off stays off; existing databases get it on the next start). (2) New permission `HRM_PAYSLIP_EMAIL` ("Email Payslips in Bulk"): in `HrmCodes` and `HrmSensitiveCodes`, granted to `HR_MANAGER` (all HrmCodes) and Org Admin (all codes); not to HR Officer, System Admin or any other role. It now gates `POST api/hrm/payroll-runs/{uuid}/payslips/email` (route and service re-check) instead of `HRM_PAYROLL_PAY`; the all-employee scope requirement stays. No migration: `auth.Permissions` rows are inserted by the seeder when missing. |
