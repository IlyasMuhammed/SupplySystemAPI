# SMS — Task Register & Checklist

## Addendum 49: Human Resource Management (HRM) Module

| Property | Value |
|---|---|
| Addendum | 49 |
| Total Tasks | 546 |
| Phases | 10 (H1, H1P, H2, H3, H4, H5, H5F, H5P, H6, H6E) |
| Estimated Effort | 90 days (18 weeks) |
| Design Heritage | Odoo approach (DD-1 through DD-8) |
| Depends On | A37 (Module Registry), A40 (Import), A42 (CoA), A44 (Posting Gateway), A47 (GL Engine), A48 (AR/AP) |

### Task Categories

| Code | Category | Count |
|---|---|---|
| BE | Backend (Entities, Services, Logic) | 226 |
| DB | Database (Migrations, Indexes, Seeds) | 68 |
| FE | Frontend (UI Components, Pages) | 96 |
| QA | Quality Assurance (Unit, Integration, E2E) | 138 |
| DOC | Documentation | 18 |

---

## Phase H1 — Foundation: Org Structure & Employee Master (10 days)

### H1-A. Branch Entity (1 day)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 001 | Create `Branch` entity in `SMS.Modules.Hrm.Entities` | BE | 0.5h | ☐ |
| 002 | Define properties: `Code` (unique per org), `Name`, `Address`, `City` | BE | 0.5h | ☐ |
| 003 | Define `WarehouseId` (optional FK — only when branch is also a store) | BE | 0.25h | ☐ |
| 004 | Define `IsActive` (cannot deactivate while employees use it) | BE | 0.25h | ☐ |
| 005 | Configure entity in `HrmDbContext.OnModelCreating()` | BE | 0.25h | ☐ |
| 006 | Configure UQ `UQ_Branch_OrgCode` on `(OrgId, Code)` | DB | 0.25h | ☐ |
| 007 | Write migration `M_H1_001_CreateBranches` | DB | 0.25h | ☐ |
| 008 | Create `BranchDto`, `CreateBranchRequest`, `UpdateBranchRequest` DTOs | BE | 0.5h | ☐ |
| 009 | Implement `IBranchService` (CRUD + deactivation guard) | BE | 1h | ☐ |
| 010 | Unit test: create branch, unique code enforced, deactivation blocked when in use | QA | 0.5h | ☐ |

### H1-B. Department Entity (1.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 011 | Create `Department` entity | BE | 0.5h | ☐ |
| 012 | Define: `Code` (unique per org), `Name`, `ParentId` (FK self, no cycles), `HeadEmployeeId` (FK?) | BE | 0.5h | ☐ |
| 013 | Define: `BranchId` (FK), `CostCenterCode` (drives posting dimension), `IsActive` | BE | 0.5h | ☐ |
| 014 | Configure UQ `UQ_Dept_OrgCode`, no-cycle check on ParentId | DB | 0.25h | ☐ |
| 015 | Write migration `M_H1_002_CreateDepartments` | DB | 0.25h | ☐ |
| 016 | Create DTOs and `IDepartmentService` (CRUD + tree query) | BE | 1h | ☐ |
| 017 | Implement `GetDepartmentTreeAsync(orgId)` — hierarchical display | BE | 0.5h | ☐ |
| 018 | Unit test: tree hierarchy, no-cycle validation, cost center code propagation | QA | 0.5h | ☐ |

### H1-C. Designation & Grade Entities (1 day)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 019 | Create `Designation` entity: `Code`, `Name`, `Level` (int) | BE | 0.5h | ☐ |
| 020 | Create `Grade` entity: `Code`, `Name`, `MinSalary`, `MaxSalary` (Min <= Max) | BE | 0.5h | ☐ |
| 021 | Configure UQs on `(OrgId, Code)` for both | DB | 0.25h | ☐ |
| 022 | Write migration `M_H1_003_CreateDesignationsGrades` | DB | 0.25h | ☐ |
| 023 | Create DTOs and `IDesignationService`, `IGradeService` (CRUD) | BE | 0.5h | ☐ |
| 024 | Unit test: salary range validation on Grade (Min <= Max) | QA | 0.25h | ☐ |

### H1-D. Employee Master Entity (2.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 025 | Create `Employee` entity in `hrm` schema | BE | 0.5h | ☐ |
| 026 | Define: `EmployeeNumber` (auto-generated, unique per org, never reused) | BE | 0.5h | ☐ |
| 027 | Define personal: `FirstName`, `LastName`, `FatherOrHusbandName`, `DateOfBirth`, `Gender`, `MaritalStatus`, `Nationality` | BE | 0.5h | ☐ |
| 028 | Define identity: `Cnic` (encrypted, 13-digit, keyed hash for search), `Ntn` (encrypted) | BE | 1h | ☐ |
| 029 | Define contact: `PersonalEmail`, `WorkEmail`, `Phone`, `Mobile` | BE | 0.25h | ☐ |
| 030 | Define address: `CurrentAddress`, `PermanentAddress` (structured JSON) | BE | 0.5h | ☐ |
| 031 | Define: `Photo` (FileStore key), `Status` enum (Candidate/Probation/Active/OnNotice/Suspended/Terminated/Archived) | BE | 0.5h | ☐ |
| 032 | Define: `UserId` (FK to SMS user, one per employee per org), `SourceCandidateId` (UUID, link from recruitment) | BE | 0.25h | ☐ |
| 033 | Define: `JoinDate`, `LeaveDate`, `DepartureReason` enum | BE | 0.25h | ☐ |
| 034 | Define `EmergencyContacts` (embedded JSON: Name, Relation, Phone) | BE | 0.25h | ☐ |
| 035 | Configure sensitivity levels: S0 (normal), S1 (personal), S2 (sensitive), S3 (encrypted/masked) | BE | 0.5h | ☐ |
| 036 | Configure UQ `UQ_Emp_OrgNumber` on `(OrgId, EmployeeNumber)` | DB | 0.25h | ☐ |
| 037 | Configure CNIC keyed hash column for uniqueness/exact search without decryption | DB | 0.5h | ☐ |
| 038 | Configure indexes: `IX_Emp_OrgStatus`, `IX_Emp_Department`, `IX_Emp_UserId` | DB | 0.25h | ☐ |
| 039 | Write migration `M_H1_004_CreateEmployees` | DB | 0.5h | ☐ |
| 040 | Create `EmployeeDto`, `EmployeeListDto`, `CreateEmployeeRequest`, `UpdateEmployeeRequest` | BE | 1h | ☐ |
| 041 | Implement `IEmployeeService` — CRUD with number generation | BE | 1.5h | ☐ |
| 042 | Implement employee number generator: `EMP-NNNN` pattern, auto-increment, never reused | BE | 0.5h | ☐ |
| 043 | Implement CNIC encryption at rest (AES-256), masked display (`*****-*******-#`) | BE | 1h | ☐ |
| 044 | Implement status transition rules (valid state machine) | BE | 0.5h | ☐ |
| 045 | Unit test: employee creation with auto number | QA | 0.25h | ☐ |
| 046 | Unit test: CNIC encryption, hash search, masked display | QA | 0.5h | ☐ |
| 047 | Unit test: status transition validation (Active -> Terminated OK, Archived -> Active NOT OK) | QA | 0.5h | ☐ |
| 048 | Unit test: one UserId per employee per org | QA | 0.25h | ☐ |

### H1-E. EmploymentRecord Entity (1.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 049 | Create `EmploymentRecord` entity (effective-dated, Odoo `hr.contract` style) | BE | 0.5h | ☐ |
| 050 | Define: `EffectiveFrom`, `EffectiveTo` (no overlap per employee) | BE | 0.25h | ☐ |
| 051 | Define: `EmploymentType` enum (Permanent/Contract/Probation/Intern/DailyWage) | BE | 0.25h | ☐ |
| 052 | Define FKs: `DepartmentId`, `DesignationId`, `BranchId`, `GradeId`, `ManagerEmployeeId` | BE | 0.5h | ☐ |
| 053 | Define: `ShiftId`/`WorkScheduleId`, `ProbationEndDate`, `ConfirmationDate`, `ContractEndDate` | BE | 0.25h | ☐ |
| 054 | Define: `NoticePeriodDays`, `CostCenterCode` (overrides department), `PayrollGroup`, `ChangeReason` enum | BE | 0.5h | ☐ |
| 055 | Configure no-overlap constraint (per employee) | DB | 0.5h | ☐ |
| 056 | Configure UQ on `(EmployeeId, EffectiveFrom)` | DB | 0.25h | ☐ |
| 057 | Write migration `M_H1_005_CreateEmploymentRecords` | DB | 0.25h | ☐ |
| 058 | Create DTOs and `IEmploymentRecordService` | BE | 0.5h | ☐ |
| 059 | Implement `GetCurrentRecordAsync(employeeId)` — most recent active | BE | 0.5h | ☐ |
| 060 | Implement `CreateTransferAsync`, `CreatePromotionAsync` — new record, close previous | BE | 1h | ☐ |
| 061 | Unit test: no overlap, effective date ordering, transfer/promotion flow | QA | 0.5h | ☐ |

### H1-F. EmployeeBankAccount Entity (0.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 062 | Create `EmployeeBankAccount` entity | BE | 0.5h | ☐ |
| 063 | Define: `BankName`, `BranchName`, `AccountTitle`, `AccountNumber` (encrypted S3), `Iban`, `IsPrimary`, `PaymentMethod` | BE | 0.5h | ☐ |
| 064 | Configure: one active primary per employee | DB | 0.25h | ☐ |
| 065 | Write migration `M_H1_006_CreateEmployeeBankAccounts` | DB | 0.25h | ☐ |
| 066 | Unit test: encrypted account, one primary enforced | QA | 0.25h | ☐ |

### H1-G. HRM Module Registration & Account Role Seeding (1 day)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 067 | Register `MODULE_HRM` in Module Registry (A37) | BE | 0.25h | ☐ |
| 068 | Define HRM menu structure and route registration | BE | 0.5h | ☐ |
| 069 | Seed 16 HR account role keys in A42 (G-4: `M_A49_001`) | DB | 0.5h | ☐ |
| 070 | Seed HR security roles: HR Manager, HR Officer, Finance Officer HR grants (G-22: `M_A49_005`) | DB | 0.5h | ☐ |
| 071 | Add IsVerified flag to TaxSlabs and StatutoryRates (G-21: `M_A49_006`) | DB | 0.25h | ☐ |
| 072 | Verify all H1 migrations execute in order and rollback cleanly | DB | 0.5h | ☐ |
| 073 | Verify `hrm` schema isolation — no cross-schema FKs | DB | 0.25h | ☐ |
| 074 | Document Phase H1 entity relationships | DOC | 0.5h | ☐ |

### H1-H. Foundation UI (1 day)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 075 | Create HRM module navigation sidebar | FE | 0.5h | ☐ |
| 076 | Create `BranchList` and `BranchForm` pages | FE | 1h | ☐ |
| 077 | Create `DepartmentTree` page with drag-and-drop hierarchy | FE | 1.5h | ☐ |
| 078 | Create `DesignationList` and `GradeList` pages | FE | 1h | ☐ |
| 079 | Create `EmployeeList` page with search, filter by status/department/branch | FE | 1.5h | ☐ |
| 080 | Create `EmployeeForm` — multi-tab: Personal, Employment, Bank, Documents | FE | 2h | ☐ |
| 081 | Wire all Foundation UI to API endpoints | FE | 1h | ☐ |

**Phase H1 Subtotal: 81 tasks (43 BE, 16 DB, 8 QA, 1 DOC, 13 FE)**

---

## Phase H1P — Profile P2/P3 Readiness (4 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 082 | Define Profile P2 (HR + Finance + QBO) in module registry | BE | 0.5h | ☐ |
| 083 | Define Profile P3 (HR only) in module registry | BE | 0.25h | ☐ |
| 084 | Define Profile P-PK (Pakistan: HR + internal GL, pluggable provider) | BE | 0.25h | ☐ |
| 085 | Implement menu/route hiding for disabled modules (Sales, Procurement, Inventory, Manufacturing, POS) (G-8) | BE | 1.5h | ☐ |
| 086 | Modify Finance module: soft-reference Demand/Warehouse via interfaces (G-8) | BE | 2h | ☐ |
| 087 | Modify Finance startup: skip seeds referencing Sales/Procurement when those modules off (G-8) | BE | 1h | ☐ |
| 088 | Modify Finance GL reports: render correctly with only payroll data (G-8) | BE | 1h | ☐ |
| 089 | Hide AR/AP pages gracefully when no source module (G-8) | FE | 1h | ☐ |
| 090 | Implement `PostingResult` structured return (not throw) from gateway (G-19) | BE | 1h | ☐ |
| 091 | Implement `PostingStatus.NoTargetConfigured` — not an error for P3 (G-19) | BE | 0.5h | ☐ |
| 092 | Implement `IPayrollJournalExportService` — CSV/XLSX export for P3 tenants (G-19) | BE | 1.5h | ☐ |
| 093 | Add HrmSettings columns: `PayrollCostCenterId`, `PayrollCostCenterMode` (G-19: `M_A49_008`) | DB | 0.5h | ☐ |
| 094 | Document one org = one employer limitation (G-17) | DOC | 0.5h | ☐ |
| 095 | QA: P2 profile startup — no exceptions without Sales/Procurement | QA | 1h | ☐ |
| 096 | QA: P2 GL reports with only payroll data | QA | 0.5h | ☐ |
| 097 | QA: P2 QuickBooks settings accessible | QA | 0.5h | ☐ |
| 098 | QA: P3 Finalize with no target → status = Finalized (not error) | QA | 0.5h | ☐ |
| 099 | QA: P3 CSV/XLSX export contains correct journal data | QA | 0.5h | ☐ |

**Phase H1P Subtotal: 18 tasks (10 BE, 1 DB, 5 QA, 1 DOC, 1 FE)**

---

## Phase H2 — Leave & Attendance (12 days)

### H2-A. Shift & Work Schedule (2 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 100 | Create `Shift` entity: `Start`, `End`, `BreakMinutes`, `GraceIn`, `HalfDayMinutes`, `WeeklyOffDays` | BE | 0.5h | ☐ |
| 101 | Create `Holiday` entity: `Date`, `Name` (seeded by country pack) | BE | 0.25h | ☐ |
| 102 | Write migration `M_H2_001_CreateShiftsHolidays` | DB | 0.25h | ☐ |
| 103 | Seed Pakistan public holidays for 2026-2027 | DB | 0.5h | ☐ |
| 104 | Create `IShiftService` (CRUD + assign to employees) | BE | 0.5h | ☐ |
| 105 | Create `IHolidayService` (CRUD + by-country seeder) | BE | 0.5h | ☐ |
| 106 | Create Shift management UI pages | FE | 1h | ☐ |
| 107 | Create Holiday calendar UI page | FE | 1h | ☐ |
| 108 | Unit test: shift time calculations, holiday lookup | QA | 0.5h | ☐ |

### H2-B. Attendance (3 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 109 | Create `AttendanceEvent` entity: `EventTime` (UTC, immutable), `Source`, `ExternalEventId` (idempotent) | BE | 0.5h | ☐ |
| 110 | Create `AttendanceDay` entity (computed): `EmployeeId`, `Date`, `FirstIn`, `LastOut`, `WorkedMinutes`, `OvertimeMinutes`, `LateMinutes`, `EarlyMinutes`, `Status` enum | BE | 0.5h | ☐ |
| 111 | Write migration `M_H2_002_CreateAttendance` | DB | 0.25h | ☐ |
| 112 | Implement `IAttendanceService` | BE | 0.5h | ☐ |
| 113 | Implement `RecordEventAsync(employeeId, eventTime, source, externalId)` — idempotent | BE | 0.5h | ☐ |
| 114 | Implement `ComputeDayAsync(employeeId, date)` — calculates worked hours, OT, late from events + shift | BE | 1.5h | ☐ |
| 115 | Implement late arrival: `FirstIn - Shift.Start > Shift.GraceIn` → late minutes | BE | 0.5h | ☐ |
| 116 | Implement early departure: `LastOut < Shift.End` → early minutes | BE | 0.25h | ☐ |
| 117 | Implement overtime: `WorkedMinutes - Shift.TotalMinutes` (if positive and beyond threshold) | BE | 0.5h | ☐ |
| 118 | Implement half-day detection: `WorkedMinutes < Shift.HalfDayMinutes` | BE | 0.25h | ☐ |
| 119 | Implement absent detection: no events on a working day | BE | 0.25h | ☐ |
| 120 | Implement holiday/off-day skip in attendance computation | BE | 0.25h | ☐ |
| 121 | Implement `GetMonthlyAttendanceSummaryAsync(employeeId, month, year)` | BE | 0.5h | ☐ |
| 122 | Create attendance UI: daily view, monthly summary, employee-wise/dept-wise filters | FE | 2h | ☐ |
| 123 | Create attendance event import (CSV upload from biometric device) | BE | 1h | ☐ |
| 124 | Unit test: late calculation, overtime computation, absent detection | QA | 0.5h | ☐ |
| 125 | Unit test: idempotent event recording (duplicate external ID ignored) | QA | 0.25h | ☐ |
| 126 | Unit test: half-day detection | QA | 0.25h | ☐ |

### H2-C. Overtime Approval (1 day)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 127 | Create `OvertimeRecord` entity: `EmployeeId`, `Date`, `Minutes`, `Multiplier`, `Status` (Pending/Approved/Rejected) | BE | 0.5h | ☐ |
| 128 | Write migration `M_H2_003_CreateOvertimeRecords` | DB | 0.25h | ☐ |
| 129 | Implement `IOvertimeService` — submit, approve, reject | BE | 0.5h | ☐ |
| 130 | Implement approved OT → feeds into payroll as OT component | BE | 0.5h | ☐ |
| 131 | Create OT approval UI page | FE | 1h | ☐ |
| 132 | Unit test: OT approval flow, only approved feeds payroll | QA | 0.5h | ☐ |

### H2-D. Leave Types & Policies (2 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 133 | Create `LeaveType` entity: `Code`, `Name`, `IsPaid`, `CarryForwardCap`, `AttachmentAfterDays` | BE | 0.5h | ☐ |
| 134 | Define `LeavePolicy.Entitlement` (decimal), `AccrualMethod` (Annual/Monthly/None) | BE | 0.5h | ☐ |
| 135 | Write migration `M_H2_004_CreateLeaveTypes` | DB | 0.25h | ☐ |
| 136 | Seed standard leave types: Annual (14d), Sick (10d), Casual (10d), Maternity (90d), Paternity (30d), Unpaid | DB | 0.5h | ☐ |
| 137 | Create `ILeaveTypeService` (CRUD + policy configuration) | BE | 0.5h | ☐ |
| 138 | Create leave type configuration UI | FE | 1h | ☐ |
| 139 | Unit test: entitlement, accrual method, carry forward cap | QA | 0.5h | ☐ |

### H2-E. Leave Ledger & Requests (4 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 140 | Create `LeaveLedgerEntry` entity: `EmployeeId`, `LeaveTypeId`, `Delta` (+quota/-consumed), `Reason`, `Date`, `ReferenceId` | BE | 0.5h | ☐ |
| 141 | Create `LeaveRequest` entity: `EmployeeId`, `LeaveTypeId`, `FromDate`, `ToDate`, `HalfDay` (bool), `Status` (Pending/Approved/Rejected/Cancelled) | BE | 0.5h | ☐ |
| 142 | Write migration `M_H2_005_CreateLeaveLedgerRequests` | DB | 0.25h | ☐ |
| 143 | Implement `ILeaveService` | BE | 0.5h | ☐ |
| 144 | Implement `GetBalanceAsync(employeeId, leaveTypeId)` — sum of ledger deltas | BE | 0.5h | ☐ |
| 145 | Implement `AllocateQuotaAsync(employeeId, leaveTypeId, days)` — annual quota entry | BE | 0.5h | ☐ |
| 146 | Implement `BulkAllocateAsync(orgId, leaveTypeId, year)` — allocate for all active employees | BE | 1h | ☐ |
| 147 | Implement `RequestLeaveAsync(request)` — validate balance, create request | BE | 0.5h | ☐ |
| 148 | Implement `ApproveLeaveAsync(requestId, approvedBy)` — deduct from ledger | BE | 0.5h | ☐ |
| 149 | Implement `RejectLeaveAsync(requestId, reason, rejectedBy)` | BE | 0.25h | ☐ |
| 150 | Implement `CancelLeaveAsync(requestId)` — restore balance to ledger | BE | 0.5h | ☐ |
| 151 | Implement carry-forward: `ProcessYearEndCarryForwardAsync(orgId, fromYear, toYear)` — cap at CarryForwardCap | BE | 1h | ☐ |
| 152 | Implement leave overlap check — no overlapping approved leaves | BE | 0.5h | ☐ |
| 153 | Implement attachment requirement — if leave > AttachmentAfterDays, require FileStore attachment | BE | 0.25h | ☐ |
| 154 | WorkflowEngine integration: LEAVE_REQUEST approval | BE | 0.5h | ☐ |
| 155 | Create leave dashboard: calendar view, team leave visibility | FE | 2h | ☐ |
| 156 | Create leave request form: type, dates, half-day, attachment | FE | 1h | ☐ |
| 157 | Create leave balance summary page | FE | 1h | ☐ |
| 158 | Create leave approval queue for managers | FE | 1h | ☐ |
| 159 | Unit test: balance computation, request/approve/cancel cycle | QA | 0.5h | ☐ |
| 160 | Unit test: insufficient balance → request rejected | QA | 0.25h | ☐ |
| 161 | Unit test: overlap detection | QA | 0.25h | ☐ |
| 162 | Unit test: carry forward with cap | QA | 0.5h | ☐ |
| 163 | Unit test: bulk allocation for all active employees | QA | 0.25h | ☐ |

**Phase H2 Subtotal: 64 tasks (32 BE, 5 DB, 12 QA, 0 DOC, 8 FE + 7 seeds/imports)**

---

## Phase H3 — Salary Structure & Assignment (8 days)

### H3-A. Salary Components (2 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 164 | Create `SalaryComponent` entity: `Code`, `Name`, `Type` (Earning/Deduction/EmployerContribution) | BE | 0.5h | ☐ |
| 165 | Define: `IsTaxable`, `InGross` (bool), `CalcMethod` (Fixed/Percent/Formula), `PostingRole` (account role key) | BE | 0.5h | ☐ |
| 166 | Configure UQ `(OrgId, Code)` | DB | 0.25h | ☐ |
| 167 | Write migration `M_H3_001_CreateSalaryComponents` | DB | 0.25h | ☐ |
| 168 | Seed standard Pakistan components: BASIC, HRENT, UTIL, CONV, OT, BONUS, ARREARS, ITAX, EOBI_EE, EOBI_ER, PF_EE, PF_ER, SS_EE, SS_ER, LOAN, ADV, EXPREIMB, OTHDED, NET | DB | 1h | ☐ |
| 169 | Create `ISalaryComponentService` (CRUD) | BE | 0.5h | ☐ |
| 170 | Create salary component management UI | FE | 1h | ☐ |
| 171 | Unit test: component types, posting role resolution | QA | 0.5h | ☐ |

### H3-B. Salary Structure & Formula Engine (3 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 172 | Create `SalaryStructure` entity: `Name`, `Lines` (ordered list of component references) | BE | 0.5h | ☐ |
| 173 | Write migration `M_H3_002_CreateSalaryStructures` | DB | 0.25h | ☐ |
| 174 | Seed default structure: "Standard Monthly" with BASIC + HRENT + UTIL + CONV - ITAX - EOBI - PF = NET | DB | 0.5h | ☐ |
| 175 | Implement `ISalaryFormulaEngine` — safe expression evaluator (no code execution) | BE | 2h | ☐ |
| 176 | Support variables: `BASIC`, `GROSS`, `TOTAL_EARNINGS`, `WORKING_DAYS`, `PAID_DAYS`, `OT_HOURS` | BE | 1h | ☐ |
| 177 | Support operators: `+`, `-`, `*`, `/`, `MIN`, `MAX`, `IF`, `ROUND` | BE | 1h | ☐ |
| 178 | Example: HRENT formula = `BASIC * 0.45`, ITAX formula = `SLAB(TAXABLE_INCOME)` | BE | 0.5h | ☐ |
| 179 | Implement SLAB function — looks up TaxSlab table for progressive rates | BE | 1h | ☐ |
| 180 | Security: sandbox formula execution — no file/network/reflection access | BE | 0.5h | ☐ |
| 181 | Create formula builder/tester UI (input sample values, see result) | FE | 1.5h | ☐ |
| 182 | Unit test: formula evaluation (fixed, percent, expression) | QA | 0.5h | ☐ |
| 183 | Unit test: SLAB function with Pakistan income tax brackets | QA | 0.5h | ☐ |
| 184 | Unit test: formula sandbox — malicious input rejected | QA | 0.5h | ☐ |

### H3-C. Salary Assignment (2 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 185 | Create `SalaryAssignment` entity: `EmployeeId`, `StructureId`, effective-dated, component amounts (S2 sensitive) | BE | 0.5h | ☐ |
| 186 | Define: `ApprovalStatus` (Draft/Approved), `EffectiveFrom`, `EffectiveTo` | BE | 0.25h | ☐ |
| 187 | Write migration `M_H3_003_CreateSalaryAssignments` | DB | 0.25h | ☐ |
| 188 | Implement `ISalaryAssignmentService` — assign structure + amounts to employee | BE | 1h | ☐ |
| 189 | Implement `GetCurrentAssignmentAsync(employeeId)` — active assignment as of today | BE | 0.5h | ☐ |
| 190 | Implement approval workflow for salary changes | BE | 0.5h | ☐ |
| 191 | Create salary assignment UI: employee → structure → component amounts | FE | 1.5h | ☐ |
| 192 | Create bulk salary revision page (increment proposals) | FE | 1h | ☐ |
| 193 | Unit test: effective-dated assignment, approval flow | QA | 0.5h | ☐ |

### H3-D. Tax Slabs & Statutory Rates (1 day)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 194 | Create `TaxSlab` entity: effective-dated, progressive brackets, `IsVerified` flag | BE | 0.5h | ☐ |
| 195 | Create `StatutoryRate` entity: EOBI/PF/SS rates, effective-dated, `IsVerified` flag | BE | 0.5h | ☐ |
| 196 | Write migration `M_H3_004_CreateTaxSlabsStatutoryRates` | DB | 0.25h | ☐ |
| 197 | Seed Pakistan FY2026 income tax slabs (flagged Unverified until reviewed — G-21) | DB | 0.5h | ☐ |
| 198 | Seed EOBI/PF rates (flagged Unverified — G-21) | DB | 0.25h | ☐ |
| 199 | Create tax slab management UI with verification toggle | FE | 1h | ☐ |
| 200 | Unit test: progressive tax calculation, unverified blocks Finalize | QA | 0.5h | ☐ |

**Phase H3 Subtotal: 37 tasks (20 BE, 7 DB, 6 QA, 0 DOC, 4 FE)**

---

## Phase H4 — Recruitment & Performance (8 days)

### H4-A. Recruitment Pipeline (4 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 201 | Create `Vacancy` entity: `DesignationId`, `DepartmentId`, `Positions`, `Status` (Open/Closed/OnHold), `Description` | BE | 0.5h | ☐ |
| 202 | Create `Candidate` entity: `Name`, `Email`, `Phone`, `Resume` (FileStore), `Source` | BE | 0.5h | ☐ |
| 203 | Create `Application` entity: `CandidateId`, `VacancyId`, `Stage` (New/Screening/Interview/Offer/Hired/Rejected), `Rating` | BE | 0.5h | ☐ |
| 204 | Create `Interview` entity: `ApplicationId`, `InterviewerEmployeeId`, `ScheduledAt`, `Feedback`, `Score`, `Result` | BE | 0.5h | ☐ |
| 205 | Write migration `M_H4_001_CreateRecruitment` | DB | 0.5h | ☐ |
| 206 | Implement `IRecruitmentService` — vacancy, candidate, application CRUD | BE | 1.5h | ☐ |
| 207 | Implement stage transitions (Kanban-style, Odoo DD-7) | BE | 0.5h | ☐ |
| 208 | Implement `ConvertToEmployeeAsync(applicationId)` — create Employee + EmploymentRecord from hired candidate | BE | 1h | ☐ |
| 209 | Create vacancy list and Kanban board UI (drag applications across stages) | FE | 2h | ☐ |
| 210 | Create candidate profile page with resume viewer | FE | 1h | ☐ |
| 211 | Create interview scheduling and feedback form | FE | 1h | ☐ |
| 212 | Create offer letter generation (template-based) | BE | 1h | ☐ |
| 213 | Unit test: stage transitions, convert to employee | QA | 0.5h | ☐ |
| 214 | Unit test: duplicate candidate detection (email/phone) | QA | 0.25h | ☐ |

### H4-B. Performance Review (3 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 215 | Create `ReviewCycle` entity: `Name`, `Period` (Annual/Quarterly), `StartDate`, `EndDate`, `Status` | BE | 0.5h | ☐ |
| 216 | Create `Review` entity: `EmployeeId`, `ReviewCycleId`, `ReviewerEmployeeId`, `OverallRating`, `Status` (Draft/Submitted/Acknowledged) | BE | 0.5h | ☐ |
| 217 | Create `Goal` entity: `EmployeeId`, `ReviewCycleId`, `Title`, `Description`, `Weight`, `SelfRating`, `ManagerRating`, `Status` | BE | 0.5h | ☐ |
| 218 | Write migration `M_H4_002_CreatePerformance` | DB | 0.5h | ☐ |
| 219 | Implement `IPerformanceService` — cycle management, review, goal tracking | BE | 1.5h | ☐ |
| 220 | Implement weighted overall rating: Σ(goal.Weight × goal.ManagerRating) / Σ(goal.Weight) | BE | 0.5h | ☐ |
| 221 | Create review cycle management UI | FE | 1h | ☐ |
| 222 | Create employee review form: goals, self-rating, manager-rating, comments | FE | 1.5h | ☐ |
| 223 | Create performance dashboard: cycle completion %, rating distribution | FE | 1h | ☐ |
| 224 | Unit test: weighted rating calculation, review workflow | QA | 0.5h | ☐ |

### H4-C. Training & Courses (1 day)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 225 | Create `Course` entity: `Name`, `Description`, `Provider`, `Duration`, `Cost` | BE | 0.25h | ☐ |
| 226 | Create `CourseEnrollment` entity: `EmployeeId`, `CourseId`, `EnrolledDate`, `CompletedDate`, `Certificate` (FileStore) | BE | 0.25h | ☐ |
| 227 | Write migration `M_H4_003_CreateTraining` | DB | 0.25h | ☐ |
| 228 | Implement `ITrainingService` (CRUD + enrollment) | BE | 0.5h | ☐ |
| 229 | Create training catalog and enrollment UI | FE | 1h | ☐ |
| 230 | Unit test: enrollment, completion, certificate upload | QA | 0.25h | ☐ |

**Phase H4 Subtotal: 30 tasks (14 BE, 3 DB, 4 QA, 0 DOC, 9 FE)**

---

## Phase H5 — Payroll Core (12 days)

### H5-A. Payroll Run Entity & Lifecycle (2 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 231 | Create `PayrollRun` entity: `PeriodStart`, `PeriodEnd`, `PayDate`, `PayrollGroup`, `Status` | BE | 0.5h | ☐ |
| 232 | Define status enum: Draft/Calculated/UnderReview/Approved/Finalized/Posted/Paid/Cancelled/Reversed | BE | 0.5h | ☐ |
| 233 | Define: `TotalEmployees`, `TotalGross`, `TotalDeductions`, `TotalNet`, `TotalEmployerCost` | BE | 0.25h | ☐ |
| 234 | Define workflow: `CalculatedBy/At`, `ReviewedBy/At`, `ApprovedBy/At`, `FinalizedBy/At`, `PostedAt`, `PaidAt` | BE | 0.25h | ☐ |
| 235 | Configure UQ: unique non-cancelled run per period + payroll group | DB | 0.25h | ☐ |
| 236 | Write migration `M_H5_001_CreatePayrollRuns` | DB | 0.25h | ☐ |
| 237 | Implement `IPayrollRunService` — create, status transitions, status validation | BE | 1.5h | ☐ |
| 238 | Implement status transition rules: Draft→Calculated→UnderReview→Approved→Finalized→Posted→Paid | BE | 0.5h | ☐ |
| 239 | Unit test: status transitions, invalid transition rejected | QA | 0.5h | ☐ |

### H5-B. Payroll Run Lines & Calculation Engine (4 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 240 | Create `PayrollRunLine` entity: `RunId`, `EmployeeId` (unique per run+employee) | BE | 0.5h | ☐ |
| 241 | Define: `PaidDays`, `WorkedDays`, `Gross`, `TotalDeductions`, `Net`, `TotalEmployerCost` | BE | 0.25h | ☐ |
| 242 | Define: `IsBlocked`, `BlockReason` (missing bank account, unverified rate, etc.) | BE | 0.25h | ☐ |
| 243 | Create `PayrollRunLineComponent` entity: `Code`, `Type`, `Amount`, `Basis` (immutable snapshot of what was computed) | BE | 0.5h | ☐ |
| 244 | Write migration `M_H5_002_CreatePayrollRunLines` | DB | 0.25h | ☐ |
| 245 | Implement `IPayrollCalculationEngine` | BE | 1h | ☐ |
| 246 | Implement `CalculateRunAsync(orgId, runId)` — core calculation loop | BE | 3h | ☐ |
| 247 | Step 1: Load all active employees for the payroll group/period | BE | 0.5h | ☐ |
| 248 | Step 2: For each employee — load current SalaryAssignment | BE | 0.25h | ☐ |
| 249 | Step 3: Compute paid days from attendance (worked + paid leave + holiday) | BE | 1h | ☐ |
| 250 | Step 4: For each component in structure (ordered): evaluate formula using ISalaryFormulaEngine | BE | 1h | ☐ |
| 251 | Step 5: Compute BASIC pro-rata: `BASIC * PaidDays / WorkingDays` | BE | 0.5h | ☐ |
| 252 | Step 6: Compute tax (ITAX) using TaxSlab progressive rates | BE | 1h | ☐ |
| 253 | Step 7: Compute EOBI employee/employer portions from StatutoryRate | BE | 0.5h | ☐ |
| 254 | Step 8: Compute PF employee/employer portions | BE | 0.5h | ☐ |
| 255 | Step 9: Deduct loan installment (if any active loan for this employee) | BE | 0.5h | ☐ |
| 256 | Step 10: Deduct advance recovery (if any outstanding advance) | BE | 0.25h | ☐ |
| 257 | Step 11: Compute NET = Gross - Deductions | BE | 0.25h | ☐ |
| 258 | Step 12: Create PayrollRunLine + components (immutable snapshot) | BE | 0.5h | ☐ |
| 259 | Step 13: Check for blocked employees (missing bank, unverified slab) — set IsBlocked | BE | 0.5h | ☐ |
| 260 | Step 14: Update run totals | BE | 0.25h | ☐ |
| 261 | Unit test: full calculation for 5-employee sample payroll | QA | 1h | ☐ |
| 262 | Unit test: pro-rata for mid-month joiner | QA | 0.5h | ☐ |
| 263 | Unit test: tax slab progressive calculation | QA | 0.5h | ☐ |
| 264 | Unit test: loan installment deduction | QA | 0.5h | ☐ |
| 265 | Unit test: blocked employee (no bank account) | QA | 0.25h | ☐ |

### H5-C. Payslip Generation (2 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 266 | Create `Payslip` entity: `RunLineId`, `FileKey` (FileStore), immutable | BE | 0.25h | ☐ |
| 267 | Implement `IPayslipService` — generate PDF payslip from RunLine | BE | 1h | ☐ |
| 268 | Create payslip PDF template: company header, employee details, earnings table, deductions table, net pay, YTD totals | BE | 1.5h | ☐ |
| 269 | Implement bulk payslip generation: `GenerateAllPayslipsAsync(runId)` | BE | 0.5h | ☐ |
| 270 | Implement employee self-service payslip download | BE | 0.5h | ☐ |
| 271 | Create payslip viewer UI (PDF preview + download) | FE | 1h | ☐ |
| 272 | Unit test: payslip PDF generated with correct figures | QA | 0.5h | ☐ |

### H5-D. Payroll Adjustment (1 day)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 273 | Create `PayrollAdjustment` entity: `EmployeeId`, `RunId`, `ComponentCode`, `Amount`, `Reason` (one-off item, like Odoo hr.payslip.input) | BE | 0.5h | ☐ |
| 274 | Write migration `M_H5_003_CreatePayrollAdjustments` | DB | 0.25h | ☐ |
| 275 | Implement adjustments in calculation engine — added after regular components | BE | 0.5h | ☐ |
| 276 | Create adjustment entry UI | FE | 0.5h | ☐ |
| 277 | Unit test: adjustment added to payslip correctly | QA | 0.25h | ☐ |

### H5-E. Payroll Run UI (3 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 278 | Create `PayrollRunList` page with period/status filters | FE | 1h | ☐ |
| 279 | Create `PayrollRunWizard` — Step 1: Select period, payroll group, pay date | FE | 1h | ☐ |
| 280 | Step 2: Calculate → progress bar → show summary (total employees, gross, deductions, net) | FE | 1.5h | ☐ |
| 281 | Step 3: Review → employee-by-employee breakdown, blocked items highlighted | FE | 2h | ☐ |
| 282 | Step 4: Approve → confirm and lock | FE | 0.5h | ☐ |
| 283 | Step 5: Finalize → posting readiness check → post to GL/QBO | FE | 1h | ☐ |
| 284 | Step 6: Pay → bank file generation | FE | 0.5h | ☐ |
| 285 | Create employee payroll history page | FE | 1h | ☐ |
| 286 | Responsive layout for payroll wizard | FE | 0.5h | ☐ |

**Phase H5 Subtotal: 56 tasks (30 BE, 4 DB, 7 QA, 0 DOC, 15 FE)**

---

## Phase H5F — Payroll Finalize & Payment (5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 287 | Implement `IPayrollPaymentService` — bank file generation | BE | 1h | ☐ |
| 288 | Generate CSV bank file: employee name, bank, account, IBAN, amount, reference | BE | 1h | ☐ |
| 289 | Mark run status → Paid when bank file generated | BE | 0.25h | ☐ |
| 290 | Build `FinancialTransaction` from PayrollRun — posting matrix (summarized by account × cost center) | BE | 2h | ☐ |
| 291 | Posting matrix: Dr SALARY_EXPENSE (+ OT/BONUS), Cr TAX_PAYABLE, Cr EOBI_PAYABLE, Cr PF_PAYABLE, Cr SALARY_PAYABLE, Cr OTHER_DED | BE | 1h | ☐ |
| 292 | Employer costs: Dr EMPLOYER_CONTRIB_EXPENSE, Cr EOBI_PAYABLE (employer), Cr PF_PAYABLE (employer) | BE | 0.5h | ☐ |
| 293 | Rounding: Σ Dr = Σ Cr to cent, difference → DEFAULT_ROUNDING | BE | 0.25h | ☐ |
| 294 | Call `IFinancialPostingGateway.PostAsync()` with FinDocType.PayrollRun | BE | 0.5h | ☐ |
| 295 | Build payment FinancialTransaction: Dr SALARY_PAYABLE, Cr DEFAULT_BANK | BE | 0.5h | ☐ |
| 296 | Call gateway with FinDocType.PayrollPayment | BE | 0.25h | ☐ |
| 297 | Handle `PostingStatus.NoTargetConfigured` — stay at Finalized (G-19) | BE | 0.25h | ☐ |
| 298 | Implement payroll reversal: void original run + create reversing FinancialTransaction (G-12) | BE | 1.5h | ☐ |
| 299 | Implement `IPayrollPostingReadinessService.CheckReadinessAsync()` (G-3) | BE | 1h | ☐ |
| 300 | Implement post-date policy: PayDateWithFallback (G-10) | BE | 0.5h | ☐ |
| 301 | Unit test: posting matrix balanced | QA | 0.5h | ☐ |
| 302 | Unit test: payment JE: Dr Payable / Cr Bank | QA | 0.5h | ☐ |
| 303 | Unit test: reversal creates opposite JE | QA | 0.5h | ☐ |
| 304 | Unit test: readiness check — unmapped role blocks Finalize | QA | 0.5h | ☐ |
| 305 | Unit test: no-target → Finalized (not error) | QA | 0.25h | ☐ |
| 306 | Unit test: pay date fallback to period end when period closed | QA | 0.25h | ☐ |
| 307 | Integration test: full payroll cycle — create → calculate → approve → finalize → post → pay | QA | 1.5h | ☐ |

**Phase H5F Subtotal: 21 tasks (12 BE, 0 DB, 7 QA, 0 DOC, 0 FE + 2 integration)**

---

## Phase H5P — Payroll Posting & QuickBooks (12 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 308 | Add 7 HRM FinDocType enum values to `FinancialPostingContracts` (G-2) | BE | 0.5h | ☐ |
| 309 | Add `PostingSourceModules.Hrm = "hrm"` (G-2) | BE | 0.25h | ☐ |
| 310 | Add gateway routing cases for all 7 HRM doc types (G-2) | BE | 0.5h | ☐ |
| 311 | Implement `IQuickBooksJournalEntryBuilder` interface (G-1) | BE | 0.5h | ☐ |
| 312 | Implement `QboJournalEntryPayload` and `QboJournalEntryLine` records (G-1) | BE | 0.5h | ☐ |
| 313 | Implement builder: AccountRole → CoA → QBO AccountRef resolution (G-1) | BE | 1.5h | ☐ |
| 314 | Implement builder: CostCenter → QBO ClassRef resolution via CostCenterQboMappings (G-1, G-5) | BE | 1h | ☐ |
| 315 | Implement builder: balanced entry validation (Σ Dr = Σ Cr) (G-1) | BE | 0.5h | ☐ |
| 316 | Implement builder: privacy — description = cost center name only, never employee (G-11) | BE | 0.5h | ☐ |
| 317 | Implement builder: PrivateNote = run# + period + headcount only (G-11) | BE | 0.25h | ☐ |
| 318 | Implement employee name safety-net check in builder (G-11) | BE | 0.5h | ☐ |
| 319 | Implement builder: IdempotencyKey = `hrm:{DocType}:{uuid}[:batch{N}]` (G-1) | BE | 0.25h | ☐ |
| 320 | Integrate builder into QuickBooksTarget: if HRM type → JE builder, else existing (G-1) | BE | 0.5h | ☐ |
| 321 | Register `IQuickBooksJournalEntryBuilder` in DI (G-1) | BE | 0.25h | ☐ |
| 322 | Implement `BuildReversalAsync` for payroll corrections (G-12) | BE | 1h | ☐ |
| 323 | Create `CostCenterQboMappings` entity and migration (G-5: `M_A49_002`) | DB | 0.5h | ☐ |
| 324 | Implement `ICostCenterQboMappingService` — List, Update, GetUnmapped, Resolve (G-5) | BE | 1h | ☐ |
| 325 | Implement auto-populate from HRM departments (G-5) | BE | 0.5h | ☐ |
| 326 | Extend GL setup-complete check to include HR keys when MODULE_HRM enabled (G-4) | BE | 0.5h | ☐ |
| 327 | Implement QBO currency validation on connect and first Finalize (G-14) | BE | 0.5h | ☐ |
| 328 | Seed SyncControlConfigs for 7 HRM doc types — disabled + held (G-15: `M_A49_004`) | DB | 0.5h | ☐ |
| 329 | Implement JE line splitting by department group when > 900 lines (G-16) | BE | 1h | ☐ |
| 330 | Implement split batch balancing validation (G-16) | BE | 0.5h | ☐ |
| 331 | Create Cost Center Mapping UI page in Integration Settings (G-5) | FE | 1.5h | ☐ |
| 332 | Create Sync Control toggle UI for HRM doc types (G-15) | FE | 1h | ☐ |
| 333 | Create Posting Readiness dashboard in payroll wizard (G-3) | FE | 1h | ☐ |
| 334 | Unit test G-1: payroll → JE in QBO sandbox (TS-G1-001) | QA | 1h | ☐ |
| 335 | Unit test G-1: 5 depts → 5 ClassRefs (TS-G1-002) | QA | 0.5h | ☐ |
| 336 | Unit test G-1: duplicate idempotency key → reuse (TS-G1-003) | QA | 0.5h | ☐ |
| 337 | Unit test G-2: PayrollRun routes to JournalEntry (TS-G2-001) | QA | 0.5h | ☐ |
| 338 | Unit test G-3: unmapped role → Finalize blocked (TS-G3-001) | QA | 0.5h | ☐ |
| 339 | Unit test G-3: all mapped → Post succeeds (TS-G3-003) | QA | 0.5h | ☐ |
| 340 | Unit test G-5: unmapped cost center → blocked (TS-G5-003) | QA | 0.5h | ☐ |
| 341 | Unit test G-11: no employee names in QBO payload (TS-G11-001) | QA | 0.5h | ☐ |
| 342 | Unit test G-12: reversal → opposite JE (TS-G12-001) | QA | 0.5h | ☐ |
| 343 | Unit test G-14: currency mismatch → blocked (TS-G14-001) | QA | 0.5h | ☐ |
| 344 | Unit test G-15: fresh connect → types disabled (TS-G15-001) | QA | 0.5h | ☐ |
| 345 | Unit test G-16: 1200 lines → splits into 2 JEs (TS-G16-001) | QA | 0.5h | ☐ |

**Phase H5P Subtotal: 38 tasks (20 BE, 2 DB, 12 QA, 0 DOC, 4 FE)**

---

## Phase H6 — Loans, Settlements, Letters (8 days)

### H6-A. Employee Loans (3 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 346 | Create `Loan` entity: `EmployeeId`, `Type` (Advance/Personal/Housing), `Principal`, `InstallmentCount`, `MonthlyAmount`, `StartMonth`, `Status` | BE | 0.5h | ☐ |
| 347 | Create `LoanInstallment` entity: `LoanId`, `DueMonth`, `Amount`, `Status` (Pending/Deducted/Waived), `RunId` | BE | 0.5h | ☐ |
| 348 | Write migration `M_H6_001_CreateLoans` | DB | 0.25h | ☐ |
| 349 | Implement `ILoanService` — create, approve, disburse, track | BE | 1h | ☐ |
| 350 | Implement loan disbursement posting: Dr EMPLOYEE_LOAN_RECEIVABLE / Cr BANK via gateway (FinDocType.LoanDisbursement) | BE | 1h | ☐ |
| 351 | Implement monthly installment deduction in payroll calculation engine (link to H5-B Step 9) | BE | 0.5h | ☐ |
| 352 | Implement loan recovery posting: each payroll deduction reduces the receivable | BE | 0.5h | ☐ |
| 353 | Implement outstanding balance: Principal - Σ(deducted installments) | BE | 0.25h | ☐ |
| 354 | Create loan management UI: application, approval, disbursement, repayment schedule | FE | 2h | ☐ |
| 355 | Unit test: loan creation, installment schedule generation | QA | 0.5h | ☐ |
| 356 | Unit test: disbursement posting balanced | QA | 0.25h | ☐ |
| 357 | Unit test: payroll deduction reduces balance | QA | 0.25h | ☐ |

### H6-B. Final Settlement (2 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 358 | Create `FinalSettlement` entity: `EmployeeId`, `LastWorkingDay`, `Components` (JSON), `Net`, `Status` | BE | 0.5h | ☐ |
| 359 | Write migration `M_H6_002_CreateFinalSettlements` | DB | 0.25h | ☐ |
| 360 | Implement `IFinalSettlementService` — compute: remaining salary + leave encashment + gratuity - loan balance - advances | BE | 2h | ☐ |
| 361 | Implement gratuity calculation based on service years and last drawn salary | BE | 0.5h | ☐ |
| 362 | Implement leave encashment: unused leave days × daily rate | BE | 0.5h | ☐ |
| 363 | Implement settlement posting via gateway (FinDocType.FinalSettlement) | BE | 0.5h | ☐ |
| 364 | Create settlement form UI | FE | 1h | ☐ |
| 365 | Unit test: settlement computation, posting | QA | 0.5h | ☐ |

### H6-C. Increment Proposals (1 day)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 366 | Create `IncrementProposal` entity: `EmployeeId`, `NewSalary` or `Percent`, `EffectiveDate`, `ReviewId`, `Status` (Draft/Approved/Applied) | BE | 0.5h | ☐ |
| 367 | Write migration `M_H6_003_CreateIncrementProposals` | DB | 0.25h | ☐ |
| 368 | Implement `IIncrementService` — propose, approve, apply (creates new SalaryAssignment) | BE | 1h | ☐ |
| 369 | Create bulk increment proposal UI (linked from performance reviews) | FE | 1h | ☐ |
| 370 | Unit test: increment applied → new salary assignment created | QA | 0.25h | ☐ |

### H6-D. Letters & Tax Certificates (2 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 371 | Create `IssuedLetter` entity: `EmployeeId`, `Template` (OfferLetter/ExperienceCertificate/SalaryCertificate/Custom), `Code`, `FileKey`, `IssuedBy` | BE | 0.5h | ☐ |
| 372 | Create `TaxCertificate` entity: `EmployeeId`, `TaxYear`, `TotalIncome`, `TotalTax`, `FileKey` | BE | 0.25h | ☐ |
| 373 | Write migration `M_H6_004_CreateLettersCertificates` | DB | 0.25h | ☐ |
| 374 | Implement `ILetterService` — generate from template, merge employee data, store PDF | BE | 1.5h | ☐ |
| 375 | Create letter templates: offer, experience, salary certificate with placeholders `{EmployeeName}`, `{Designation}`, `{JoinDate}`, etc. | BE | 1h | ☐ |
| 376 | Implement `ITaxCertificateService` — generate annual statement from payroll data | BE | 1h | ☐ |
| 377 | Create letter issuance UI | FE | 1h | ☐ |
| 378 | Create tax certificate viewer | FE | 0.5h | ☐ |
| 379 | Unit test: template merge, PDF generation | QA | 0.5h | ☐ |

**Phase H6 Subtotal: 34 tasks (17 BE, 4 DB, 5 QA, 0 DOC, 8 FE)**

---

## Phase H6E — Expense Claims & Statutory Remittances (5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 380 | Create `ExpenseCategory` entity and migration (G-9: `M_A49_003`) | BE | 0.5h | ☐ |
| 381 | Seed default categories: Travel, Meals, Office, Telecom, Lodging, Other (G-9: `M_A49_007`) | DB | 0.5h | ☐ |
| 382 | Create `ExpenseClaim` entity: `EmployeeId`, `ClaimDate`, `Status` (Draft/Submitted/Approved/Paid/Rejected), `PaymentMode` (OwnAccount/CompanyAccount), `SettlementMethod` (WithPayroll/SeparatePayment) | BE | 0.5h | ☐ |
| 383 | Create `ExpenseLine` entity: `CategoryId`, `Description`, `Date`, `Amount`, `TaxCode`, `TaxAmount`, `CostCenterCode`, `Attachment` (FileStore) | BE | 0.5h | ☐ |
| 384 | Write migration `M_H6E_001_CreateExpenseClaims` | DB | 0.25h | ☐ |
| 385 | Implement `IExpenseClaimService` — submit, approve, reject, pay | BE | 1h | ☐ |
| 386 | Implement WorkflowEngine integration: EXPENSE_CLAIM approval | BE | 0.5h | ☐ |
| 387 | Implement receipt requirement: enforce above category threshold | BE | 0.25h | ☐ |
| 388 | Implement expense approval → build FinancialTransaction: Dr Expense (per category → CoA) / Cr EXPENSE_CLAIM_PAYABLE | BE | 1h | ☐ |
| 389 | Post via gateway as FinDocType.Expense → GL: PURCH type, QBO: JournalEntry (G-9) | BE | 0.5h | ☐ |
| 390 | Implement separate payment: FinDocType.ExpensePayment → Dr CLAIM_PAYABLE / Cr BANK (G-9) | BE | 0.5h | ☐ |
| 391 | Implement WithPayroll settlement: add to payslip line as EXPREIMB component | BE | 0.5h | ☐ |
| 392 | Implement statutory remittance recording: `IStatutoryRemittanceService` (G-13) | BE | 1h | ☐ |
| 393 | Build FinancialTransaction for remittance: Dr TAX/EOBI/PF_PAYABLE / Cr BANK (G-13) | BE | 0.5h | ☐ |
| 394 | Post via gateway as FinDocType.StatutoryRemittance (G-13) | BE | 0.25h | ☐ |
| 395 | Create expense claim submission UI: lines, receipts, category selection | FE | 1.5h | ☐ |
| 396 | Create expense approval queue UI | FE | 1h | ☐ |
| 397 | Create statutory remittance tracker UI: period, type, calculated vs paid vs balance | FE | 1h | ☐ |
| 398 | Unit test G-9: expense approved → JE created (TS-G9-001) | QA | 0.5h | ☐ |
| 399 | Unit test G-9: expense paid separately → Dr Payable / Cr Bank (TS-G9-002) | QA | 0.5h | ☐ |
| 400 | Unit test G-9: expense with payroll → EXPREIMB on payslip (TS-G9-003) | QA | 0.5h | ☐ |
| 401 | Unit test G-13: statutory remittance → Dr Payable / Cr Bank (TS-G13-001) | QA | 0.5h | ☐ |

**Phase H6E Subtotal: 22 tasks (12 BE, 2 DB, 4 QA, 0 DOC, 4 FE)**

---

## Phase DOC — Documentation & Final QA (6 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 402 | Document all HRM API endpoints in OpenAPI/Swagger | DOC | 2h | ☐ |
| 403 | Document payroll posting matrix with account role mapping | DOC | 1h | ☐ |
| 404 | Document salary formula engine syntax and built-in functions | DOC | 1h | ☐ |
| 405 | Document leave policy configuration guide | DOC | 0.5h | ☐ |
| 406 | Document QuickBooks integration setup for HR (cost center mapping, sync enablement) | DOC | 1h | ☐ |
| 407 | Document P2/P3/P-PK profile differences | DOC | 0.5h | ☐ |
| 408 | Create Odoo CSV import mapping guide (Appendix A of A49) | DOC | 1h | ☐ |
| 409 | Create SAP flat-file import mapping guide (Appendix B of A49) | DOC | 1h | ☐ |
| 410 | Document entity relationship diagram for all A49 entities | DOC | 0.5h | ☐ |
| 411 | Document deployment checklist per profile | DOC | 0.5h | ☐ |
| 412 | Integration test: full hire-to-retire cycle (recruit → hire → attendance → payroll → settlement) | QA | 2h | ☐ |
| 413 | Integration test: P2-S acceptance (service invoice → receipt → payroll → expense → TB/P&L) | QA | 1.5h | ☐ |
| 414 | Integration test: payroll → GL JE → QBO JournalEntry end-to-end | QA | 1h | ☐ |
| 415 | Integration test: expense claim → approval → GL → QBO | QA | 1h | ☐ |
| 416 | Performance test: payroll calculation for 500 employees < 30 seconds | QA | 1h | ☐ |
| 417 | Performance test: QBO JE split for 1200-line payroll | QA | 0.5h | ☐ |
| 418 | Multi-tenant isolation test: Org A payroll not visible to Org B | QA | 1h | ☐ |
| 419 | Security test: S2/S3 fields encrypted at rest, masked in API responses | QA | 1h | ☐ |
| 420 | Security test: HR scope enforcement (Department/Branch/Self) | QA | 1h | ☐ |
| 421 | Verify all 22 gaps (G-1 through G-22) resolved | QA | 1h | ☐ |
| 422 | Final checklist walkthrough | DOC | 0.5h | ☐ |

**Phase DOC Subtotal: 21 tasks (0 BE, 0 DB, 10 QA, 11 DOC, 0 FE)**

---

## Summary

| Phase | Description | Tasks | Days |
|---|---|---|---|
| H1 | Foundation: Org Structure & Employee Master | 81 | 10 |
| H1P | Profile P2/P3 Readiness (G-8, G-17, G-19) | 18 | 4 |
| H2 | Leave & Attendance | 64 | 12 |
| H3 | Salary Structure & Assignment | 37 | 8 |
| H4 | Recruitment & Performance | 30 | 8 |
| H5 | Payroll Core | 56 | 12 |
| H5F | Payroll Finalize & Payment | 21 | 5 |
| H5P | Payroll Posting & QuickBooks (G-1,2,3,5,10-16) | 38 | 12 |
| H6 | Loans, Settlements, Letters | 34 | 8 |
| H6E | Expense Claims & Remittances (G-9, G-13) | 22 | 5 |
| DOC | Documentation & Final QA | 21 | 6 |
| **Total** | | **422** | **90** |

### Task Counts by Category

| Category | Count | % |
|---|---|---|
| BE — Backend | 210 | 49.8% |
| DB — Database | 43 | 10.2% |
| FE — Frontend | 66 | 15.6% |
| QA — Quality Assurance | 90 | 21.3% |
| DOC — Documentation | 13 | 3.1% |
| **Total** | **422** | **100%** |

---

## Cross-Reference: A49 Entities → Phase → Odoo Equivalent

| SMS Entity | Phase | Odoo Model | Schema |
|---|---|---|---|
| Branch | H1 | hr.work.location | hrm |
| Department | H1 | hr.department | hrm |
| Designation | H1 | hr.job | hrm |
| Grade | H1 | — (pay scale group) | hrm |
| Employee | H1 | hr.employee | hrm |
| EmploymentRecord | H1 | hr.contract | hrm |
| EmployeeBankAccount | H1 | res.partner.bank | hrm |
| Shift | H2 | resource.calendar | hrm |
| Holiday | H2 | resource.calendar.leaves | hrm |
| AttendanceEvent | H2 | hr.attendance | hrm |
| AttendanceDay | H2 | (computed) | hrm |
| OvertimeRecord | H2 | (overtime on attendance) | hrm |
| LeaveType | H2 | hr.leave.type | hrm |
| LeaveLedgerEntry | H2 | hr.leave.allocation + hr.leave | hrm |
| LeaveRequest | H2 | hr.leave | hrm |
| SalaryComponent | H3 | hr.salary.rule | hrm |
| SalaryStructure | H3 | hr.payroll.structure | hrm |
| SalaryAssignment | H3 | hr.contract.wage | hrm |
| TaxSlab | H3 | hr.rule.parameter | hrm |
| StatutoryRate | H3 | hr.rule.parameter | hrm |
| Vacancy | H4 | hr.job (recruitment) | hrm |
| Candidate / Application | H4 | hr.applicant | hrm |
| Interview | H4 | (calendar event + notes) | hrm |
| ReviewCycle / Review / Goal | H4 | hr.appraisal | hrm |
| Course / CourseEnrollment | H4 | hr.skill / eLearning | hrm |
| PayrollRun | H5 | hr.payslip.run | hrm |
| PayrollRunLine | H5 | hr.payslip | hrm |
| PayrollRunLineComponent | H5 | hr.payslip.line | hrm |
| PayrollAdjustment | H5 | hr.payslip.input | hrm |
| Payslip (PDF) | H5 | (payslip report) | hrm |
| Loan / LoanInstallment | H6 | loan (OCA/Enterprise) | hrm |
| FinalSettlement | H6 | (custom) | hrm |
| IncrementProposal | H6 | hr.contract (new record) | hrm |
| IssuedLetter / TaxCertificate | H6 | (report templates) | hrm |
| ExpenseCategory | H6E | hr.expense (product) | hrm |
| ExpenseClaim / ExpenseLine | H6E | hr.expense / hr.expense.sheet | hrm |
| CostCenterQboMappings | H5P | — | integration |

### Gap Resolution Cross-Reference

| Gap | Task #s | Phase |
|---|---|---|
| G-1 (QBO JE builder) | 311-322, 334-337 | H5P |
| G-2 (FinDocType enum) | 308-310, 337 | H5P |
| G-3 (Account mapping mandatory) | 299, 304, 333, 338-339 | H5F/H5P |
| G-4 (HR account role keys) | 069, 326 | H1/H5P |
| G-5 (Cost center → QBO Class) | 323-325, 331, 340 | H5P |
| G-8 (Finance standalone P2) | 085-089, 095-097 | H1P |
| G-9 (Expense claim posting) | 380-401 | H6E |
| G-10 (Fiscal period policy) | 300, 306 | H5F |
| G-11 (Privacy in QBO) | 316-318, 341 | H5P |
| G-12 (QBO void/reversal) | 298, 322, 342 | H5F/H5P |
| G-13 (Statutory remittance) | 392-394, 401 | H6E |
| G-14 (QBO currency) | 327, 343 | H5P |
| G-15 (SyncControl seeding) | 328, 332, 344 | H5P |
| G-16 (JE line splitting) | 329-330, 345 | H5P |
| G-17 (One org = one employer) | 094 | H1P |
| G-18 (Bank reconciliation) | — (deferred) | — |
| G-19 (No-target lifecycle) | 090-093, 098-099, 297, 305 | H1P/H5F |
| G-20 (QBO Pakistan availability) | Pre-sales decision | — |
| G-21 (Pakistan statutory values) | 071, 197-198, 200 | H1/H3 |
| G-22 (Role seeding) | 070 | H1 |

---

**End of Task Register — Addendum 49**
