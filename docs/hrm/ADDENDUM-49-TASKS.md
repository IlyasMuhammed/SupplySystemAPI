# Addendum 49 — task tracker

Register `Documents/SMS_Task_Register_Addendum_49_Checklist.md` (001–422; its header says 546, its rows and summary say 422) + §11A.8 extras E-1..E-16. Status: `open`, `done`, `n/a`. **Done means evidence named here**, not "code written". Owners update their own rows when they finish. Phase, type and text are the register's. Interpretation of a task follows ANALYSIS Part A.


## H1

| # | Task | Type | Owner | Status | Evidence |
|---|---|---|---|---|---|
| 001 | Create `Branch` entity in `SMS.Modules.Hrm.Entities` | BE | CORE | done | Branch + BranchService; OrgStructureTests |
| 002 | Define properties: `Code` (unique per org), `Name`, `Address`, `City` | BE | CORE | done | Branch + BranchService; OrgStructureTests |
| 003 | Define `WarehouseId` (optional FK — only when branch is also a store) | BE | CORE | done | Branch + BranchService; OrgStructureTests |
| 004 | Define `IsActive` (cannot deactivate while employees use it) | BE | CORE | done | Branch + BranchService; OrgStructureTests |
| 005 | Configure entity in `HrmDbContext.OnModelCreating()` | BE | CORE | done | Branch + BranchService; OrgStructureTests |
| 006 | Configure UQ `UQ_Branch_OrgCode` on `(OrgId, Code)` | DB | CORE | done | Branch + BranchService; OrgStructureTests |
| 007 | Write migration `M_H1_001_CreateBranches` | DB | CORE | done | Branch + BranchService; OrgStructureTests |
| 008 | Create `BranchDto`, `CreateBranchRequest`, `UpdateBranchRequest` DTOs | BE | CORE | done | Branch + BranchService; OrgStructureTests |
| 009 | Implement `IBranchService` (CRUD + deactivation guard) | BE | CORE | done | Branch + BranchService; OrgStructureTests |
| 010 | Unit test: create branch, unique code enforced, deactivation blocked when in use | QA | CORE | done | OrgStructureTests (unique code, in-use guard, cross-org) |
| 011 | Create `Department` entity | BE | CORE | done | Department + DepartmentService; OrgStructureTests |
| 012 | Define: `Code` (unique per org), `Name`, `ParentId` (FK self, no cycles), `HeadEmployeeId` (FK?) | BE | CORE | done | Department + DepartmentService; OrgStructureTests |
| 013 | Define: `BranchId` (FK), `CostCenterCode` (drives posting dimension), `IsActive` | BE | CORE | done | Department + DepartmentService; OrgStructureTests |
| 014 | Configure UQ `UQ_Dept_OrgCode`, no-cycle check on ParentId | DB | CORE | done | Department + DepartmentService; OrgStructureTests |
| 015 | Write migration `M_H1_002_CreateDepartments` | DB | CORE | done | Department + DepartmentService; OrgStructureTests |
| 016 | Create DTOs and `IDepartmentService` (CRUD + tree query) | BE | CORE | done | Department + DepartmentService; OrgStructureTests |
| 017 | Implement `GetDepartmentTreeAsync(orgId)` — hierarchical display | BE | CORE | done | Department + DepartmentService; OrgStructureTests |
| 018 | Unit test: tree hierarchy, no-cycle validation, cost center code propagation | QA | CORE | done | OrgStructureTests (tree, cycle, cost center code) |
| 019 | Create `Designation` entity: `Code`, `Name`, `Level` (int) | BE | CORE | done | Designation, Grade + services |
| 020 | Create `Grade` entity: `Code`, `Name`, `MinSalary`, `MaxSalary` (Min <= Max) | BE | CORE | done | Designation, Grade + services |
| 021 | Configure UQs on `(OrgId, Code)` for both | DB | CORE | done | Designation, Grade + services |
| 022 | Write migration `M_H1_003_CreateDesignationsGrades` | DB | CORE | done | Designation, Grade + services |
| 023 | Create DTOs and `IDesignationService`, `IGradeService` (CRUD) | BE | CORE | done | Designation, Grade + services |
| 024 | Unit test: salary range validation on Grade (Min <= Max) | QA | CORE | done | OrgStructureTests (range, masking) |
| 025 | Create `Employee` entity in `hrm` schema | BE | CORE | done | Employee + EmployeeService; EmployeeTests; A49MigrationTests |
| 026 | Define: `EmployeeNumber` (auto-generated, unique per org, never reused) | BE | CORE | done | Employee + EmployeeService; EmployeeTests; A49MigrationTests |
| 027 | Define personal: `FirstName`, `LastName`, `FatherOrHusbandName`, `DateOfBirth`, `Gender`, `MaritalStatus`, `Nationality` | BE | CORE | done | Employee + EmployeeService; EmployeeTests; A49MigrationTests |
| 028 | Define identity: `Cnic` (encrypted, 13-digit, keyed hash for search), `Ntn` (encrypted) | BE | CORE | done | Employee + EmployeeService; EmployeeTests; A49MigrationTests |
| 029 | Define contact: `PersonalEmail`, `WorkEmail`, `Phone`, `Mobile` | BE | CORE | done | Employee + EmployeeService; EmployeeTests; A49MigrationTests |
| 030 | Define address: `CurrentAddress`, `PermanentAddress` (structured JSON) | BE | CORE | done | Employee + EmployeeService; EmployeeTests; A49MigrationTests |
| 031 | Define: `Photo` (FileStore key), `Status` enum (Candidate/Probation/Active/OnNotice/Suspended/Terminated/Archived) | BE | CORE | done | Employee + EmployeeService; EmployeeTests; A49MigrationTests |
| 032 | Define: `UserId` (FK to SMS user, one per employee per org), `SourceCandidateId` (UUID, link from recruitment) | BE | CORE | done | Employee + EmployeeService; EmployeeTests; A49MigrationTests |
| 033 | Define: `JoinDate`, `LeaveDate`, `DepartureReason` enum | BE | CORE | done | Employee + EmployeeService; EmployeeTests; A49MigrationTests |
| 034 | Define `EmergencyContacts` (embedded JSON: Name, Relation, Phone) | BE | CORE | done | Employee + EmployeeService; EmployeeTests; A49MigrationTests |
| 035 | Configure sensitivity levels: S0 (normal), S1 (personal), S2 (sensitive), S3 (encrypted/masked) | BE | CORE | done | Employee + EmployeeService; EmployeeTests; A49MigrationTests |
| 036 | Configure UQ `UQ_Emp_OrgNumber` on `(OrgId, EmployeeNumber)` | DB | CORE | done | Employee + EmployeeService; EmployeeTests; A49MigrationTests |
| 037 | Configure CNIC keyed hash column for uniqueness/exact search without decryption | DB | CORE | done | Employee + EmployeeService; EmployeeTests; A49MigrationTests |
| 038 | Configure indexes: `IX_Emp_OrgStatus`, `IX_Emp_Department`, `IX_Emp_UserId` | DB | CORE | done | Employee + EmployeeService; EmployeeTests; A49MigrationTests |
| 039 | Write migration `M_H1_004_CreateEmployees` | DB | CORE | done | Employee + EmployeeService; EmployeeTests; A49MigrationTests |
| 040 | Create `EmployeeDto`, `EmployeeListDto`, `CreateEmployeeRequest`, `UpdateEmployeeRequest` | BE | CORE | done | Employee + EmployeeService; EmployeeTests; A49MigrationTests |
| 041 | Implement `IEmployeeService` — CRUD with number generation | BE | CORE | done | Employee + EmployeeService; EmployeeTests; A49MigrationTests |
| 042 | Implement employee number generator: `EMP-NNNN` pattern, auto-increment, never reused | BE | CORE | done | Employee + EmployeeService; EmployeeTests; A49MigrationTests |
| 043 | Implement CNIC encryption at rest (AES-256), masked display (`*****-*******-#`) | BE | CORE | done | Employee + EmployeeService; EmployeeTests; A49MigrationTests |
| 044 | Implement status transition rules (valid state machine) | BE | CORE | done | Employee + EmployeeService; EmployeeTests; A49MigrationTests |
| 045 | Unit test: employee creation with auto number | QA | CORE | done | EmployeeTests; A49MigrationTests (concurrent numbers) |
| 046 | Unit test: CNIC encryption, hash search, masked display | QA | CORE | done | EmployeeTests; A49MigrationTests (concurrent numbers) |
| 047 | Unit test: status transition validation (Active -> Terminated OK, Archived -> Active NOT OK) | QA | CORE | done | EmployeeTests; A49MigrationTests (concurrent numbers) |
| 048 | Unit test: one UserId per employee per org | QA | CORE | done | EmployeeTests; A49MigrationTests (concurrent numbers) |
| 049 | Create `EmploymentRecord` entity (effective-dated, Odoo `hr.contract` style) | BE | CORE | done | EmploymentRecord + EmploymentRecordService; EmployeeTests; A49MigrationTests (one open, unique start) |
| 050 | Define: `EffectiveFrom`, `EffectiveTo` (no overlap per employee) | BE | CORE | done | EmploymentRecord + EmploymentRecordService; EmployeeTests; A49MigrationTests (one open, unique start) |
| 051 | Define: `EmploymentType` enum (Permanent/Contract/Probation/Intern/DailyWage) | BE | CORE | done | EmploymentRecord + EmploymentRecordService; EmployeeTests; A49MigrationTests (one open, unique start) |
| 052 | Define FKs: `DepartmentId`, `DesignationId`, `BranchId`, `GradeId`, `ManagerEmployeeId` | BE | CORE | done | EmploymentRecord + EmploymentRecordService; EmployeeTests; A49MigrationTests (one open, unique start) |
| 053 | Define: `ShiftId`/`WorkScheduleId`, `ProbationEndDate`, `ConfirmationDate`, `ContractEndDate` | BE | CORE | done | EmploymentRecord + EmploymentRecordService; EmployeeTests; A49MigrationTests (one open, unique start) |
| 054 | Define: `NoticePeriodDays`, `CostCenterCode` (overrides department), `PayrollGroup`, `ChangeReason` enum | BE | CORE | done | EmploymentRecord + EmploymentRecordService; EmployeeTests; A49MigrationTests (one open, unique start) |
| 055 | Configure no-overlap constraint (per employee) | DB | CORE | done | EmploymentRecord + EmploymentRecordService; EmployeeTests; A49MigrationTests (one open, unique start) |
| 056 | Configure UQ on `(EmployeeId, EffectiveFrom)` | DB | CORE | done | EmploymentRecord + EmploymentRecordService; EmployeeTests; A49MigrationTests (one open, unique start) |
| 057 | Write migration `M_H1_005_CreateEmploymentRecords` | DB | CORE | done | EmploymentRecord + EmploymentRecordService; EmployeeTests; A49MigrationTests (one open, unique start) |
| 058 | Create DTOs and `IEmploymentRecordService` | BE | CORE | done | EmploymentRecord + EmploymentRecordService; EmployeeTests; A49MigrationTests (one open, unique start) |
| 059 | Implement `GetCurrentRecordAsync(employeeId)` — most recent active | BE | CORE | done | EmploymentRecord + EmploymentRecordService; EmployeeTests; A49MigrationTests (one open, unique start) |
| 060 | Implement `CreateTransferAsync`, `CreatePromotionAsync` — new record, close previous | BE | CORE | done | EmploymentRecord + EmploymentRecordService; EmployeeTests; A49MigrationTests (one open, unique start) |
| 061 | Unit test: no overlap, effective date ordering, transfer/promotion flow | QA | CORE | done | EmployeeTests |
| 062 | Create `EmployeeBankAccount` entity | BE | CORE | done | EmployeeBankAccount + service; EmployeeTests; A49MigrationTests (one primary) |
| 063 | Define: `BankName`, `BranchName`, `AccountTitle`, `AccountNumber` (encrypted S3), `Iban`, `IsPrimary`, `PaymentMethod` | BE | CORE | done | EmployeeBankAccount + service; EmployeeTests; A49MigrationTests (one primary) |
| 064 | Configure: one active primary per employee | DB | CORE | done | EmployeeBankAccount + service; EmployeeTests; A49MigrationTests (one primary) |
| 065 | Write migration `M_H1_006_CreateEmployeeBankAccounts` | DB | CORE | done | EmployeeBankAccount + service; EmployeeTests; A49MigrationTests (one primary) |
| 066 | Unit test: encrypted account, one primary enforced | QA | CORE | done | EmployeeTests |
| 067 | Register `MODULE_HRM` in Module Registry (A37) | BE | CORE | done | TenancyFeatureCatalog; HrmModuleCatalogTests |
| 068 | Define HRM menu structure and route registration | BE | CORE | done | Route list in API-CONTRACT; menu itself is FE (sidebar, task 075) |
| 069 | Seed 16 HR account role keys in A42 (G-4: `M_A49_001`) | DB | CORE | done | DefaultKeys.HrDefinitions, A49_HrDefaultKeys; HrDefaultKeysTests (no M_A49_001, A-3) |
| 070 | Seed HR security roles: HR Manager, HR Officer, Finance Officer HR grants (G-22: `M_A49_005`) | DB | CORE | done | AuthDataSeeder; HrmPermissionSeedTests |
| 071 | Add IsVerified flag to TaxSlabs and StatutoryRates (G-21: `M_A49_006`) | DB | CORE | done | IsVerified on TaxSlabTables, StatutoryRates; SeedTests |
| 072 | Verify all H1 migrations execute in order and rollback cleanly | DB | CORE | done | A49MigrationTests (up, down, up, drifted) |
| 073 | Verify `hrm` schema isolation — no cross-schema FKs | DB | CORE | done | A49MigrationTests + SeedTests: no cross-schema FK |
| 074 | Document Phase H1 entity relationships | DOC | CORE | done | ANALYSIS Part B, H1 relationships; ER-DIAGRAM.md H1 |
| 075 | Create HRM module navigation sidebar | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 076 | Create `BranchList` and `BranchForm` pages | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 077 | Create `DepartmentTree` page with drag-and-drop hierarchy | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 078 | Create `DesignationList` and `GradeList` pages | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 079 | Create `EmployeeList` page with search, filter by status/department/branch | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 080 | Create `EmployeeForm` — multi-tab: Personal, Employment, Bank, Documents | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 081 | Wire all Foundation UI to API endpoints | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |

## H1P

| # | Task | Type | Owner | Status | Evidence |
|---|---|---|---|---|---|
| 082 | Define Profile P2 (HR + Finance + QBO) in module registry | BE | POST | done | ModuleProfiles; HrmEnablementGuardTests |
| 083 | Define Profile P3 (HR only) in module registry | BE | POST | done | ModuleProfiles; HrmEnablementGuardTests; HrmPostE2ETests G19_a_P3 |
| 084 | Define Profile P-PK (Pakistan: HR + internal GL, pluggable provider) | BE | POST | done | ModuleProfiles; HrmPostE2ETests G8 (P-PK, GL only); provider seam: POST-GUIDE design note |
| 085 | Implement menu/route hiding for disabled modules (Sales, Procurement, Inventory, Manufacturing, POS) (G-8) | BE | POST | done | Existing menu/route featureCode gating over api/tenant/modules/enabled; profile Off list; HrmPostE2ETests G8/E16 show off modules absent |
| 086 | Modify Finance module: soft-reference Demand/Warehouse via interfaces (G-8) | BE | POST | done | Closed per D-13: references kept. Finance routes gated only by MODULE_FINANCE (scan); HrmPostE2ETests G8 |
| 087 | Modify Finance startup: skip seeds referencing Sales/Procurement when those modules off (G-8) | BE | POST | done | Nothing to change: UseFinanceModule seeds only currencies per org, nothing sales/purchase; host starts with modules off in HrmPostE2ETests |
| 088 | Modify Finance GL reports: render correctly with only payroll data (G-8) | BE | POST | done | HrmPostE2ETests G8: payroll-only org, trial balance balanced. P&L/BS: QA 096 |
| 089 | Hide AR/AP pages gracefully when no source module (G-8) | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 090 | Implement `PostingResult` structured return (not throw) from gateway (G-19) | BE | POST | done | Already in FinancialPostingContracts (A-8); no code |
| 091 | Implement `PostingStatus.NoTargetConfigured` — not an error for P3 (G-19) | BE | POST | done | Already in FinancialPostingContracts (A-8); no code |
| 092 | Implement `IPayrollJournalExportService` — CSV/XLSX export for P3 tenants (G-19) | BE | POST | done | PayrollJournalExportService; PostTests G19_002/003 (PAY owns routes) |
| 093 | Add HrmSettings columns: `PayrollCostCenterId`, `PayrollCostCenterMode` (G-19: `M_A49_008`) | DB | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 094 | Document one org = one employer limitation (G-17) | DOC | POST | done | POST-GUIDE; analysis G-17 |
| 095 | QA: P2 profile startup — no exceptions without Sales/Procurement | QA | QA | open |  |
| 096 | QA: P2 GL reports with only payroll data | QA | QA | open |  |
| 097 | QA: P2 QuickBooks settings accessible | QA | QA | open |  |
| 098 | QA: P3 Finalize with no target → status = Finalized (not error) | QA | QA | open |  |
| 099 | QA: P3 CSV/XLSX export contains correct journal data | QA | QA | open |  |

## H2

| # | Task | Type | Owner | Status | Evidence |
|---|---|---|---|---|---|
| 100 | Create `Shift` entity: `Start`, `End`, `BreakMinutes`, `GraceIn`, `HalfDayMinutes`, `WeeklyOffDays` | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 101 | Create `Holiday` entity: `Date`, `Name` (seeded by country pack) | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 102 | Write migration `M_H2_001_CreateShiftsHolidays` | DB | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 103 | Seed Pakistan public holidays for 2026-2027 | DB | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 104 | Create `IShiftService` (CRUD + assign to employees) | BE | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 105 | Create `IHolidayService` (CRUD + by-country seeder) | BE | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 106 | Create Shift management UI pages | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 107 | Create Holiday calendar UI page | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 108 | Unit test: shift time calculations, holiday lookup | QA | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 109 | Create `AttendanceEvent` entity: `EventTime` (UTC, immutable), `Source`, `ExternalEventId` (idempotent) | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 110 | Create `AttendanceDay` entity (computed): `EmployeeId`, `Date`, `FirstIn`, `LastOut`, `WorkedMinutes`, `OvertimeMinutes`, `LateMinutes`, `EarlyMinutes`, `Status` enum | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 111 | Write migration `M_H2_002_CreateAttendance` | DB | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 112 | Implement `IAttendanceService` | BE | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 113 | Implement `RecordEventAsync(employeeId, eventTime, source, externalId)` — idempotent | BE | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 114 | Implement `ComputeDayAsync(employeeId, date)` — calculates worked hours, OT, late from events + shift | BE | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 115 | Implement late arrival: `FirstIn - Shift.Start > Shift.GraceIn` → late minutes | BE | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 116 | Implement early departure: `LastOut < Shift.End` → early minutes | BE | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 117 | Implement overtime: `WorkedMinutes - Shift.TotalMinutes` (if positive and beyond threshold) | BE | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 118 | Implement half-day detection: `WorkedMinutes < Shift.HalfDayMinutes` | BE | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 119 | Implement absent detection: no events on a working day | BE | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 120 | Implement holiday/off-day skip in attendance computation | BE | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 121 | Implement `GetMonthlyAttendanceSummaryAsync(employeeId, month, year)` | BE | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 122 | Create attendance UI: daily view, monthly summary, employee-wise/dept-wise filters | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 123 | Create attendance event import (CSV upload from biometric device) | BE | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 124 | Unit test: late calculation, overtime computation, absent detection | QA | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 125 | Unit test: idempotent event recording (duplicate external ID ignored) | QA | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 126 | Unit test: half-day detection | QA | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 127 | Create `OvertimeRecord` entity: `EmployeeId`, `Date`, `Minutes`, `Multiplier`, `Status` (Pending/Approved/Rejected) | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 128 | Write migration `M_H2_003_CreateOvertimeRecords` | DB | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 129 | Implement `IOvertimeService` — submit, approve, reject | BE | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 130 | Implement approved OT → feeds into payroll as OT component | BE | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 131 | Create OT approval UI page | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 132 | Unit test: OT approval flow, only approved feeds payroll | QA | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 133 | Create `LeaveType` entity: `Code`, `Name`, `IsPaid`, `CarryForwardCap`, `AttachmentAfterDays` | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 134 | Define `LeavePolicy.Entitlement` (decimal), `AccrualMethod` (Annual/Monthly/None) | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 135 | Write migration `M_H2_004_CreateLeaveTypes` | DB | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 136 | Seed standard leave types: Annual (14d), Sick (10d), Casual (10d), Maternity (90d), Paternity (30d), Unpaid | DB | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 137 | Create `ILeaveTypeService` (CRUD + policy configuration) | BE | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 138 | Create leave type configuration UI | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 139 | Unit test: entitlement, accrual method, carry forward cap | QA | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 140 | Create `LeaveLedgerEntry` entity: `EmployeeId`, `LeaveTypeId`, `Delta` (+quota/-consumed), `Reason`, `Date`, `ReferenceId` | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 141 | Create `LeaveRequest` entity: `EmployeeId`, `LeaveTypeId`, `FromDate`, `ToDate`, `HalfDay` (bool), `Status` (Pending/Approved/Rejected/Cancelled) | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 142 | Write migration `M_H2_005_CreateLeaveLedgerRequests` | DB | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 143 | Implement `ILeaveService` | BE | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 144 | Implement `GetBalanceAsync(employeeId, leaveTypeId)` — sum of ledger deltas | BE | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 145 | Implement `AllocateQuotaAsync(employeeId, leaveTypeId, days)` — annual quota entry | BE | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 146 | Implement `BulkAllocateAsync(orgId, leaveTypeId, year)` — allocate for all active employees | BE | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 147 | Implement `RequestLeaveAsync(request)` — validate balance, create request | BE | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 148 | Implement `ApproveLeaveAsync(requestId, approvedBy)` — deduct from ledger | BE | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 149 | Implement `RejectLeaveAsync(requestId, reason, rejectedBy)` | BE | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 150 | Implement `CancelLeaveAsync(requestId)` — restore balance to ledger | BE | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 151 | Implement carry-forward: `ProcessYearEndCarryForwardAsync(orgId, fromYear, toYear)` — cap at CarryForwardCap | BE | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 152 | Implement leave overlap check — no overlapping approved leaves | BE | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 153 | Implement attachment requirement — if leave > AttachmentAfterDays, require FileStore attachment | BE | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 154 | WorkflowEngine integration: LEAVE_REQUEST approval | BE | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 155 | Create leave dashboard: calendar view, team leave visibility | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 156 | Create leave request form: type, dates, half-day, attachment | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 157 | Create leave balance summary page | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 158 | Create leave approval queue for managers | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 159 | Unit test: balance computation, request/approve/cancel cycle | QA | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 160 | Unit test: insufficient balance → request rejected | QA | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 161 | Unit test: overlap detection | QA | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 162 | Unit test: carry forward with cap | QA | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| 163 | Unit test: bulk allocation for all active employees | QA | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |

## H3

| # | Task | Type | Owner | Status | Evidence |
|---|---|---|---|---|---|
| 164 | Create `SalaryComponent` entity: `Code`, `Name`, `Type` (Earning/Deduction/EmployerContribution) | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 165 | Define: `IsTaxable`, `InGross` (bool), `CalcMethod` (Fixed/Percent/Formula), `PostingRole` (account role key) | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 166 | Configure UQ `(OrgId, Code)` | DB | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 167 | Write migration `M_H3_001_CreateSalaryComponents` | DB | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 168 | Seed standard Pakistan components: BASIC, HRENT, UTIL, CONV, OT, BONUS, ARREARS, ITAX, EOBI_EE, EOBI_ER, PF_EE, PF_ER, SS_EE, SS_ER, LOAN, ADV, EXPREIMB, OTHDED, NET | DB | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 169 | Create `ISalaryComponentService` (CRUD) | BE | PAY | done | SalaryConfigServices; SalaryConfigTests |
| 170 | Create salary component management UI | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 171 | Unit test: component types, posting role resolution | QA | PAY | done | SalaryConfigServices; SalaryConfigTests |
| 172 | Create `SalaryStructure` entity: `Name`, `Lines` (ordered list of component references) | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 173 | Write migration `M_H3_002_CreateSalaryStructures` | DB | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 174 | Seed default structure: "Standard Monthly" with BASIC + HRENT + UTIL + CONV - ITAX - EOBI - PF = NET | DB | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 175 | Implement `ISalaryFormulaEngine` — safe expression evaluator (no code execution) | BE | PAY | done | SalaryFormulaEngine; SalaryFormulaEngineTests |
| 176 | Support variables: `BASIC`, `GROSS`, `TOTAL_EARNINGS`, `WORKING_DAYS`, `PAID_DAYS`, `OT_HOURS` | BE | PAY | done | SalaryFormulaEngine; SalaryFormulaEngineTests |
| 177 | Support operators: `+`, `-`, `*`, `/`, `MIN`, `MAX`, `IF`, `ROUND` | BE | PAY | done | SalaryFormulaEngine; SalaryFormulaEngineTests |
| 178 | Example: HRENT formula = `BASIC * 0.45`, ITAX formula = `SLAB(TAXABLE_INCOME)` | BE | PAY | done | SalaryFormulaEngine; SalaryFormulaEngineTests |
| 179 | Implement SLAB function — looks up TaxSlab table for progressive rates | BE | PAY | done | SalaryFormulaEngine; SalaryFormulaEngineTests |
| 180 | Security: sandbox formula execution — no file/network/reflection access | BE | PAY | done | SalaryFormulaEngine; SalaryFormulaEngineTests |
| 181 | Create formula builder/tester UI (input sample values, see result) | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 182 | Unit test: formula evaluation (fixed, percent, expression) | QA | PAY | done | SalaryFormulaEngine; SalaryFormulaEngineTests |
| 183 | Unit test: SLAB function with Pakistan income tax brackets | QA | PAY | done | SalaryFormulaEngine; SalaryFormulaEngineTests |
| 184 | Unit test: formula sandbox — malicious input rejected | QA | PAY | done | SalaryFormulaEngine; SalaryFormulaEngineTests |
| 185 | Create `SalaryAssignment` entity: `EmployeeId`, `StructureId`, effective-dated, component amounts (S2 sensitive) | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 186 | Define: `ApprovalStatus` (Draft/Approved), `EffectiveFrom`, `EffectiveTo` | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 187 | Write migration `M_H3_003_CreateSalaryAssignments` | DB | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 188 | Implement `ISalaryAssignmentService` — assign structure + amounts to employee | BE | PAY | done | SalaryAssignmentService, StatutoryService; SalaryConfigTests, PayrollLifecycleTests |
| 189 | Implement `GetCurrentAssignmentAsync(employeeId)` — active assignment as of today | BE | PAY | done | SalaryAssignmentService, StatutoryService; SalaryConfigTests, PayrollLifecycleTests |
| 190 | Implement approval workflow for salary changes | BE | PAY | done | SalaryAssignmentService, StatutoryService; SalaryConfigTests, PayrollLifecycleTests |
| 191 | Create salary assignment UI: employee → structure → component amounts | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 192 | Create bulk salary revision page (increment proposals) | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 193 | Unit test: effective-dated assignment, approval flow | QA | PAY | done | SalaryAssignmentService, StatutoryService; SalaryConfigTests, PayrollLifecycleTests |
| 194 | Create `TaxSlab` entity: effective-dated, progressive brackets, `IsVerified` flag | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 195 | Create `StatutoryRate` entity: EOBI/PF/SS rates, effective-dated, `IsVerified` flag | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 196 | Write migration `M_H3_004_CreateTaxSlabsStatutoryRates` | DB | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 197 | Seed Pakistan FY2026 income tax slabs (flagged Unverified until reviewed — G-21) | DB | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 198 | Seed EOBI/PF rates (flagged Unverified — G-21) | DB | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 199 | Create tax slab management UI with verification toggle | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 200 | Unit test: progressive tax calculation, unverified blocks Finalize | QA | PAY | done | SalaryAssignmentService, StatutoryService; SalaryConfigTests, PayrollLifecycleTests |

## H4

| # | Task | Type | Owner | Status | Evidence |
|---|---|---|---|---|---|
| 201 | Create `Vacancy` entity: `DesignationId`, `DepartmentId`, `Positions`, `Status` (Open/Closed/OnHold), `Description` | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 202 | Create `Candidate` entity: `Name`, `Email`, `Phone`, `Resume` (FileStore), `Source` | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 203 | Create `Application` entity: `CandidateId`, `VacancyId`, `Stage` (New/Screening/Interview/Offer/Hired/Rejected), `Rating` | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 204 | Create `Interview` entity: `ApplicationId`, `InterviewerEmployeeId`, `ScheduledAt`, `Feedback`, `Score`, `Result` | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 205 | Write migration `M_H4_001_CreateRecruitment` | DB | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 206 | Implement `IRecruitmentService` — vacancy, candidate, application CRUD | BE | TAL | done | RecruitmentTests (vacancy numbers, tenant, scope, duplicates, delete, applications) |
| 207 | Implement stage transitions (Kanban-style, Odoo DD-7) | BE | TAL | done | RecruitmentTests.Stages_move_freely... |
| 208 | Implement `ConvertToEmployeeAsync(applicationId)` — create Employee + EmploymentRecord from hired candidate | BE | TAL | done | RecruitmentTests.Convert_*; TalSqlServerTests rollback on LocalDB |
| 209 | Create vacancy list and Kanban board UI (drag applications across stages) | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 210 | Create candidate profile page with resume viewer | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 211 | Create interview scheduling and feedback form | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 212 | Create offer letter generation (template-based) | BE | TAL | done | RecruitmentTests letter tests (PDF via PdfRenderGate; FakeDocumentStore, real attachment store not exercised) |
| 213 | Unit test: stage transitions, convert to employee | QA | TAL | done | RecruitmentTests stage + Convert_*; TalSqlServerTests |
| 214 | Unit test: duplicate candidate detection (email/phone) | QA | TAL | done | RecruitmentTests.Duplicate_candidates_* (4 cases), Force_*, outside-scope |
| 215 | Create `ReviewCycle` entity: `Name`, `Period` (Annual/Quarterly), `StartDate`, `EndDate`, `Status` | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 216 | Create `Review` entity: `EmployeeId`, `ReviewCycleId`, `ReviewerEmployeeId`, `OverallRating`, `Status` (Draft/Submitted/Acknowledged) | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 217 | Create `Goal` entity: `EmployeeId`, `ReviewCycleId`, `Title`, `Description`, `Weight`, `SelfRating`, `ManagerRating`, `Status` | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 218 | Write migration `M_H4_002_CreatePerformance` | DB | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 219 | Implement `IPerformanceService` — cycle management, review, goal tracking | BE | TAL | done | PerformanceTests (cycles, workflow, visibility, scope, tenant, dashboard) |
| 220 | Implement weighted overall rating: Σ(goal.Weight × goal.ManagerRating) / Σ(goal.Weight) | BE | TAL | done | PerformanceTests.The_overall_rating_* |
| 221 | Create review cycle management UI | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 222 | Create employee review form: goals, self-rating, manager-rating, comments | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 223 | Create performance dashboard: cycle completion %, rating distribution | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 224 | Unit test: weighted rating calculation, review workflow | QA | TAL | done | PerformanceTests.The_whole_workflow_*, The_overall_rating_* |
| 225 | Create `Course` entity: `Name`, `Description`, `Provider`, `Duration`, `Cost` | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 226 | Create `CourseEnrollment` entity: `EmployeeId`, `CourseId`, `EnrolledDate`, `CompletedDate`, `Certificate` (FileStore) | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 227 | Write migration `M_H4_003_CreateTraining` | DB | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 228 | Implement `ITrainingService` (CRUD + enrollment) | BE | TAL | done | TrainingTests |
| 229 | Create training catalog and enrollment UI | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 230 | Unit test: enrollment, completion, certificate upload | QA | TAL | done | TrainingTests.Enrollment_completes_with_a_certificate_* (stores the uuid; upload itself is api/attachments, not exercised) |

## H5

| # | Task | Type | Owner | Status | Evidence |
|---|---|---|---|---|---|
| 231 | Create `PayrollRun` entity: `PeriodStart`, `PeriodEnd`, `PayDate`, `PayrollGroup`, `Status` | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 232 | Define status enum: Draft/Calculated/UnderReview/Approved/Finalized/Posted/Paid/Cancelled/Reversed | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 233 | Define: `TotalEmployees`, `TotalGross`, `TotalDeductions`, `TotalNet`, `TotalEmployerCost` | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 234 | Define workflow: `CalculatedBy/At`, `ReviewedBy/At`, `ApprovedBy/At`, `FinalizedBy/At`, `PostedAt`, `PaidAt` | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 235 | Configure UQ: unique non-cancelled run per period + payroll group | DB | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 236 | Write migration `M_H5_001_CreatePayrollRuns` | DB | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 237 | Implement `IPayrollRunService` — create, status transitions, status validation | BE | PAY | done | PayrollRunService; PayrollLifecycleTests, PayrollConcurrencySqlTests |
| 238 | Implement status transition rules: Draft→Calculated→UnderReview→Approved→Finalized→Posted→Paid | BE | PAY | done | PayrollRunService; PayrollLifecycleTests, PayrollConcurrencySqlTests |
| 239 | Unit test: status transitions, invalid transition rejected | QA | PAY | done | PayrollRunService; PayrollLifecycleTests, PayrollConcurrencySqlTests |
| 240 | Create `PayrollRunLine` entity: `RunId`, `EmployeeId` (unique per run+employee) | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 241 | Define: `PaidDays`, `WorkedDays`, `Gross`, `TotalDeductions`, `Net`, `TotalEmployerCost` | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 242 | Define: `IsBlocked`, `BlockReason` (missing bank account, unverified rate, etc.) | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 243 | Create `PayrollRunLineComponent` entity: `Code`, `Type`, `Amount`, `Basis` (immutable snapshot of what was computed) | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 244 | Write migration `M_H5_002_CreatePayrollRunLines` | DB | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 245 | Implement `IPayrollCalculationEngine` | BE | PAY | done | PayrollCalculationEngine; PayrollCalculationTests |
| 246 | Implement `CalculateRunAsync(orgId, runId)` — core calculation loop | BE | PAY | done | PayrollCalculationEngine; PayrollCalculationTests |
| 247 | Step 1: Load all active employees for the payroll group/period | BE | PAY | done | PayrollCalculationEngine; PayrollCalculationTests |
| 248 | Step 2: For each employee — load current SalaryAssignment | BE | PAY | done | PayrollCalculationEngine; PayrollCalculationTests |
| 249 | Step 3: Compute paid days from attendance (worked + paid leave + holiday) | BE | PAY | done | PayrollCalculationEngine; PayrollCalculationTests |
| 250 | Step 4: For each component in structure (ordered): evaluate formula using ISalaryFormulaEngine | BE | PAY | done | PayrollCalculationEngine; PayrollCalculationTests |
| 251 | Step 5: Compute BASIC pro-rata: `BASIC * PaidDays / WorkingDays` | BE | PAY | done | PayrollCalculationEngine; PayrollCalculationTests |
| 252 | Step 6: Compute tax (ITAX) using TaxSlab progressive rates | BE | PAY | done | PayrollCalculationEngine; PayrollCalculationTests |
| 253 | Step 7: Compute EOBI employee/employer portions from StatutoryRate | BE | PAY | done | PayrollCalculationEngine; PayrollCalculationTests |
| 254 | Step 8: Compute PF employee/employer portions | BE | PAY | done | PayrollCalculationEngine; PayrollCalculationTests |
| 255 | Step 9: Deduct loan installment (if any active loan for this employee) | BE | PAY | done | PayrollCalculationEngine; PayrollCalculationTests |
| 256 | Step 10: Deduct advance recovery (if any outstanding advance) | BE | PAY | done | PayrollCalculationEngine; PayrollCalculationTests |
| 257 | Step 11: Compute NET = Gross - Deductions | BE | PAY | done | PayrollCalculationEngine; PayrollCalculationTests |
| 258 | Step 12: Create PayrollRunLine + components (immutable snapshot) | BE | PAY | done | PayrollCalculationEngine; PayrollCalculationTests |
| 259 | Step 13: Check for blocked employees (missing bank, unverified slab) — set IsBlocked | BE | PAY | done | PayrollCalculationEngine; PayrollCalculationTests |
| 260 | Step 14: Update run totals | BE | PAY | done | PayrollCalculationEngine; PayrollCalculationTests |
| 261 | Unit test: full calculation for 5-employee sample payroll | QA | PAY | done | PayrollCalculationTests; real loan provider in PayrollConcurrencySqlTests |
| 262 | Unit test: pro-rata for mid-month joiner | QA | PAY | done | PayrollCalculationTests; real loan provider in PayrollConcurrencySqlTests |
| 263 | Unit test: tax slab progressive calculation | QA | PAY | done | PayrollCalculationTests; real loan provider in PayrollConcurrencySqlTests |
| 264 | Unit test: loan installment deduction | QA | PAY | done | PayrollCalculationTests; real loan provider in PayrollConcurrencySqlTests |
| 265 | Unit test: blocked employee (no bank account) | QA | PAY | done | PayrollCalculationTests; real loan provider in PayrollConcurrencySqlTests |
| 266 | Create `Payslip` entity: `RunLineId`, `FileKey` (FileStore), immutable | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 267 | Implement `IPayslipService` — generate PDF payslip from RunLine | BE | PAY | done | PayslipService via PdfRenderGate; PayrollLifecycleTests |
| 268 | Create payslip PDF template: company header, employee details, earnings table, deductions table, net pay, YTD totals | BE | PAY | done | PayslipService via PdfRenderGate; PayrollLifecycleTests |
| 269 | Implement bulk payslip generation: `GenerateAllPayslipsAsync(runId)` | BE | PAY | done | PayslipService via PdfRenderGate; PayrollLifecycleTests |
| 270 | Implement employee self-service payslip download | BE | PAY | done | PayslipService via PdfRenderGate; PayrollLifecycleTests |
| 271 | Create payslip viewer UI (PDF preview + download) | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 272 | Unit test: payslip PDF generated with correct figures | QA | PAY | done | PayslipService via PdfRenderGate; PayrollLifecycleTests |
| 273 | Create `PayrollAdjustment` entity: `EmployeeId`, `RunId`, `ComponentCode`, `Amount`, `Reason` (one-off item, like Odoo hr.payslip.input) | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 274 | Write migration `M_H5_003_CreatePayrollAdjustments` | DB | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 275 | Implement adjustments in calculation engine — added after regular components | BE | PAY | done | PayrollAdjustmentService; PayrollCalculationTests |
| 276 | Create adjustment entry UI | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 277 | Unit test: adjustment added to payslip correctly | QA | PAY | done | PayrollAdjustmentService; PayrollCalculationTests |
| 278 | Create `PayrollRunList` page with period/status filters | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 279 | Create `PayrollRunWizard` — Step 1: Select period, payroll group, pay date | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 280 | Step 2: Calculate → progress bar → show summary (total employees, gross, deductions, net) | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 281 | Step 3: Review → employee-by-employee breakdown, blocked items highlighted | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 282 | Step 4: Approve → confirm and lock | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 283 | Step 5: Finalize → posting readiness check → post to GL/QBO | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 284 | Step 6: Pay → bank file generation | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 285 | Create employee payroll history page | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 286 | Responsive layout for payroll wizard | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |

## H5F

| # | Task | Type | Owner | Status | Evidence |
|---|---|---|---|---|---|
| 287 | Implement `IPayrollPaymentService` — bank file generation | BE | PAY | done | GetBankFileAsync; PayrollLifecycleTests |
| 288 | Generate CSV bank file: employee name, bank, account, IBAN, amount, reference | BE | PAY | done | GetBankFileAsync; PayrollLifecycleTests |
| 289 | Mark run status → Paid when bank file generated | BE | PAY | done | Contract wins: /pay marks Paid, the download does not; PayrollLifecycleTests |
| 290 | Build `FinancialTransaction` from PayrollRun — posting matrix (summarized by account × cost center) | BE | PAY | done | PayrollPostingBuilder, PayrollRunService; PayrollLifecycleTests (fake gateway) |
| 291 | Posting matrix: Dr SALARY_EXPENSE (+ OT/BONUS), Cr TAX_PAYABLE, Cr EOBI_PAYABLE, Cr PF_PAYABLE, Cr SALARY_PAYABLE, Cr OTHER_DED | BE | PAY | done | PayrollPostingBuilder, PayrollRunService; PayrollLifecycleTests (fake gateway) |
| 292 | Employer costs: Dr EMPLOYER_CONTRIB_EXPENSE, Cr EOBI_PAYABLE (employer), Cr PF_PAYABLE (employer) | BE | PAY | done | PayrollPostingBuilder, PayrollRunService; PayrollLifecycleTests (fake gateway) |
| 293 | Rounding: Σ Dr = Σ Cr to cent, difference → DEFAULT_ROUNDING | BE | PAY | done | PayrollPostingBuilder, PayrollRunService; PayrollLifecycleTests (fake gateway) |
| 294 | Call `IFinancialPostingGateway.PostAsync()` with FinDocType.PayrollRun | BE | PAY | done | PayrollPostingBuilder, PayrollRunService; PayrollLifecycleTests (fake gateway) |
| 295 | Build payment FinancialTransaction: Dr SALARY_PAYABLE, Cr DEFAULT_BANK | BE | PAY | done | PayrollPostingBuilder, PayrollRunService; PayrollLifecycleTests (fake gateway) |
| 296 | Call gateway with FinDocType.PayrollPayment | BE | PAY | done | PayrollPostingBuilder, PayrollRunService; PayrollLifecycleTests (fake gateway) |
| 297 | Handle `PostingStatus.NoTargetConfigured` — stay at Finalized (G-19) | BE | PAY | done | PayrollPostingBuilder, PayrollRunService; PayrollLifecycleTests (fake gateway) |
| 298 | Implement payroll reversal: void original run + create reversing FinancialTransaction (G-12) | BE | PAY | done | PayrollPostingBuilder, PayrollRunService; PayrollLifecycleTests (fake gateway) |
| 299 | Implement `IPayrollPostingReadinessService.CheckReadinessAsync()` (G-3) | BE | PAY | done | Service by POST, default and finalize check by PAY; PayrollLifecycleTests |
| 300 | Implement post-date policy: PayDateWithFallback (G-10) | BE | PAY | done | PayrollLifecycleTests |
| 301 | Unit test: posting matrix balanced | QA | PAY | done | PayrollLifecycleTests |
| 302 | Unit test: payment JE: Dr Payable / Cr Bank | QA | PAY | done | PayrollLifecycleTests |
| 303 | Unit test: reversal creates opposite JE | QA | PAY | done | PayrollLifecycleTests |
| 304 | Unit test: readiness check — unmapped role blocks Finalize | QA | PAY | done | PayrollLifecycleTests |
| 305 | Unit test: no-target → Finalized (not error) | QA | PAY | done | PayrollLifecycleTests |
| 306 | Unit test: pay date fallback to period end when period closed | QA | PAY | done | PayrollLifecycleTests |
| 307 | Integration test: full payroll cycle — create → calculate → approve → finalize → post → pay | QA | PAY | open |  |

## H5P

| # | Task | Type | Owner | Status | Evidence |
|---|---|---|---|---|---|
| 308 | Add 7 HRM FinDocType enum values to `FinancialPostingContracts` (G-2) | BE | CORE | done | FinancialPostingContracts; GatewayJournalEngineTests |
| 309 | Add `PostingSourceModules.Hrm = "hrm"` (G-2) | BE | CORE | done | FinancialPostingContracts; GatewayJournalEngineTests |
| 310 | Add gateway routing cases for all 7 HRM doc types (G-2) | BE | POST | done | GL half CORE. Target routes all 8 types; A49JournalEntryTests G2_*; FAKE QuickBooks only, never sandbox (no Intuit keys) |
| 311 | Implement `IQuickBooksJournalEntryBuilder` interface (G-1) | BE | POST | done | A49JournalEntryTests; FAKE QuickBooks only, never sandbox (no Intuit keys) |
| 312 | Implement `QboJournalEntryPayload` and `QboJournalEntryLine` records (G-1) | BE | POST | done | JournalEntryPayload + RemoteJournalEntry (D-POST-3); mapper test |
| 313 | Implement builder: AccountRole → CoA → QBO AccountRef resolution (G-1) | BE | POST | done | JournalEntryRefSource; G1_002, G3_*; FAKE QuickBooks only, never sandbox (no Intuit keys) |
| 314 | Implement builder: CostCenter → QBO ClassRef resolution via CostCenterQboMappings (G-1, G-5) | BE | POST | done | G1_002, G5_003; FAKE QuickBooks only, never sandbox (no Intuit keys) |
| 315 | Implement builder: balanced entry validation (Σ Dr = Σ Cr) (G-1) | BE | POST | done | Builder + validator; G16_001; unbalanced refused |
| 316 | Implement builder: privacy — description = cost center name only, never employee (G-11) | BE | POST | done | G1_001 (line label dropped); FAKE QuickBooks only, never sandbox (no Intuit keys) |
| 317 | Implement builder: PrivateNote = run# + period + headcount only (G-11) | BE | POST | done | G1_001; FAKE QuickBooks only, never sandbox (no Intuit keys) |
| 318 | Implement employee name safety-net check in builder (G-11) | BE | POST | done | G11_001, name on cost center, whole-word theory |
| 319 | Implement builder: IdempotencyKey = `hrm:{DocType}:{uuid}[:batch{N}]` (G-1) | BE | POST | done | payload IdempotencyKey; keys in G16_001, G12 |
| 320 | Integrate builder into QuickBooksTarget: if HRM type → JE builder, else existing (G-1) | BE | POST | done | QuickBooksPostingTarget; G2_*; existing document E2E green; FAKE QuickBooks only, never sandbox (no Intuit keys) |
| 321 | Register `IQuickBooksJournalEntryBuilder` in DI (G-1) | BE | POST | done | GatewayRegistration; HrmPostE2ETests on real host |
| 322 | Implement `BuildReversalAsync` for payroll corrections (G-12) | BE | POST | done | BuildReversal; G12_001; HrmPostE2ETests; FAKE QuickBooks only, never sandbox (no Intuit keys) |
| 323 | Create `CostCenterQboMappings` entity and migration (G-5: `M_A49_002`) | DB | CORE | done | A49_CostCenterQboMappings; A49CostCenterMigrationTests |
| 324 | Implement `ICostCenterQboMappingService` — List, Update, GetUnmapped, Resolve (G-5) | BE | POST | done | CostCenterQboMappingService; G5_ tests; HrmPostE2ETests |
| 325 | Implement auto-populate from HRM departments (G-5) | BE | POST | done | From HRM cost centers (A-13) via IHrmCostCenterSource; G5 populate test |
| 326 | Extend GL setup-complete check to include HR keys when MODULE_HRM enabled (G-4) | BE | POST | done | Deliberate deviation A-4: status never moves; hrRolesMissing; HrRolesMissingTests; HrmPostE2ETests G8 |
| 327 | Implement QBO currency validation on connect and first Finalize (G-14) | BE | POST | done | Validator JE_CURRENCY_MISMATCH + readiness; existing preflight on connect; G14_001; FAKE QuickBooks only, never sandbox (no Intuit keys) |
| 328 | Seed SyncControlConfigs for 7 HRM doc types — disabled + held (G-15: `M_A49_004`) | DB | CORE | done | Rows for existing connections in A49_CostCenterQboMappings; A49CostCenterMigrationTests. New connections: POST (A-10) |
| 329 | Implement JE line splitting by department group when > 900 lines (G-16) | BE | POST | done | Split; G16_001 |
| 330 | Implement split batch balancing validation (G-16) | BE | POST | done | G16_001 each batch balanced, totals exact |
| 331 | Create Cost Center Mapping UI page in Integration Settings (G-5) | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 332 | Create Sync Control toggle UI for HRM doc types (G-15) | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 333 | Create Posting Readiness dashboard in payroll wizard (G-3) | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 334 | Unit test G-1: payroll → JE in QBO sandbox (TS-G1-001) | QA | POST | done | TS-G1-001 FAKE QuickBooks only, never sandbox (no Intuit keys). G1_001 + HrmPostE2ETests |
| 335 | Unit test G-1: 5 depts → 5 ClassRefs (TS-G1-002) | QA | POST | done | TS-G1-002 FAKE QuickBooks only, never sandbox (no Intuit keys). G1_002 |
| 336 | Unit test G-1: duplicate idempotency key → reuse (TS-G1-003) | QA | POST | done | TS-G1-003 FAKE QuickBooks only, never sandbox (no Intuit keys). G1_003, G1_004 |
| 337 | Unit test G-2: PayrollRun routes to JournalEntry (TS-G2-001) | QA | POST | done | G2_* ; FAKE QuickBooks only, never sandbox (no Intuit keys) |
| 338 | Unit test G-3: unmapped role → Finalize blocked (TS-G3-001) | QA | POST | done | PostTests G3_001; G3_an_unmapped_role |
| 339 | Unit test G-3: all mapped → Post succeeds (TS-G3-003) | QA | POST | done | PostTests G3_003 (readiness). Run to Posted: PAY/QA; FAKE QuickBooks only, never sandbox (no Intuit keys) |
| 340 | Unit test G-5: unmapped cost center → blocked (TS-G5-003) | QA | POST | done | G5_003; PostTests G3_002; FAKE QuickBooks only, never sandbox (no Intuit keys) |
| 341 | Unit test G-11: no employee names in QBO payload (TS-G11-001) | QA | POST | done | G11_*; FAKE QuickBooks only, never sandbox (no Intuit keys) |
| 342 | Unit test G-12: reversal → opposite JE (TS-G12-001) | QA | POST | done | G12_001; FAKE QuickBooks only, never sandbox (no Intuit keys) |
| 343 | Unit test G-14: currency mismatch → blocked (TS-G14-001) | QA | POST | done | G14_001, readiness; FAKE QuickBooks only, never sandbox (no Intuit keys) |
| 344 | Unit test G-15: fresh connect → types disabled (TS-G15-001) | QA | POST | done | G15_001, no-row theory, HrmPostE2ETests |
| 345 | Unit test G-16: 1200 lines → splits into 2 JEs (TS-G16-001) | QA | POST | done | G16_001; FAKE QuickBooks only, never sandbox (no Intuit keys) |

## H6

| # | Task | Type | Owner | Status | Evidence |
|---|---|---|---|---|---|
| 346 | Create `Loan` entity: `EmployeeId`, `Type` (Advance/Personal/Housing), `Principal`, `InstallmentCount`, `MonthlyAmount`, `StartMonth`, `Status` | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 347 | Create `LoanInstallment` entity: `LoanId`, `DueMonth`, `Amount`, `Status` (Pending/Deducted/Waived), `RunId` | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 348 | Write migration `M_H6_001_CreateLoans` | DB | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 349 | Implement `ILoanService` — create, approve, disburse, track | BE | LIFE | done | LifeLoanTests, HrmLifeE2ETests (host) |
| 350 | Implement loan disbursement posting: Dr EMPLOYEE_LOAN_RECEIVABLE / Cr BANK via gateway (FinDocType.LoanDisbursement) | BE | LIFE | done | LifeLoanTests Disbursement_needs_approval...; host disburse via real gateway |
| 351 | Implement monthly installment deduction in payroll calculation engine (link to H5-B Step 9) | BE | LIFE | done | LoanPayrollInputProvider; LifeLoanTests + LifeConcurrencyTests race. Not yet run from PAY engine |
| 352 | Implement loan recovery posting: each payroll deduction reduces the receivable | BE | LIFE | done | Run posting credits LOAN/ADV role: seed role test only; PAY/REV to confirm on a real run |
| 353 | Implement outstanding balance: Principal - Σ(deducted installments) | BE | LIFE | done | LifeLoanTests A_run_deduction...; waived also reduces (D-LIFE-3) |
| 354 | Create loan management UI: application, approval, disbursement, repayment schedule | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 355 | Unit test: loan creation, installment schedule generation | QA | LIFE | done | LifeLoanTests Schedule_sums... |
| 356 | Unit test: disbursement posting balanced | QA | LIFE | done | LifeLoanTests Disbursement_needs_approval... |
| 357 | Unit test: payroll deduction reduces balance | QA | LIFE | done | LifeLoanTests A_run_deduction... |
| 358 | Create `FinalSettlement` entity: `EmployeeId`, `LastWorkingDay`, `Components` (JSON), `Net`, `Status` | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 359 | Write migration `M_H6_002_CreateFinalSettlements` | DB | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 360 | Implement `IFinalSettlementService` — compute: remaining salary + leave encashment + gratuity - loan balance - advances | BE | LIFE | done | LifeSettlementTests Compute_adds... |
| 361 | Implement gratuity calculation based on service years and last drawn salary | BE | LIFE | done | LifeSettlementTests Gratuity_applies...; values unverified G-21, D-LIFE-5 |
| 362 | Implement leave encashment: unused leave days × daily rate | BE | LIFE | done | LifeSettlementTests Compute_adds...; D-LIFE-6 |
| 363 | Implement settlement posting via gateway (FinDocType.FinalSettlement) | BE | LIFE | done | LifeSettlementTests Post_is_balanced... |
| 364 | Create settlement form UI | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 365 | Unit test: settlement computation, posting | QA | LIFE | done | LifeSettlementTests (13 kinds of case) |
| 366 | Create `IncrementProposal` entity: `EmployeeId`, `NewSalary` or `Percent`, `EffectiveDate`, `ReviewId`, `Status` (Draft/Approved/Applied) | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 367 | Write migration `M_H6_003_CreateIncrementProposals` | DB | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 368 | Implement `IIncrementService` — propose, approve, apply (creates new SalaryAssignment) | BE | LIFE | done | LifeSettlementTests Increment_applied... |
| 369 | Create bulk increment proposal UI (linked from performance reviews) | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 370 | Unit test: increment applied → new salary assignment created | QA | LIFE | done | LifeSettlementTests Increment_applied... |
| 371 | Create `IssuedLetter` entity: `EmployeeId`, `Template` (OfferLetter/ExperienceCertificate/SalaryCertificate/Custom), `Code`, `FileKey`, `IssuedBy` | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 372 | Create `TaxCertificate` entity: `EmployeeId`, `TaxYear`, `TotalIncome`, `TotalTax`, `FileKey` | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 373 | Write migration `M_H6_004_CreateLettersCertificates` | DB | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 374 | Implement `ILetterService` — generate from template, merge employee data, store PDF | BE | LIFE | done | LifeLetterTests; host issue + PDF read-back with real store and org lookup |
| 375 | Create letter templates: offer, experience, salary certificate with placeholders `{EmployeeName}`, `{Designation}`, `{JoinDate}`, etc. | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 376 | Implement `ITaxCertificateService` — generate annual statement from payroll data | BE | LIFE | done | LifeLetterTests A_tax_certificate...; not run on host |
| 377 | Create letter issuance UI | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 378 | Create tax certificate viewer | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 379 | Unit test: template merge, PDF generation | QA | LIFE | done | LifeLetterTests Merge..., Issuing... |

## H6E

| # | Task | Type | Owner | Status | Evidence |
|---|---|---|---|---|---|
| 380 | Create `ExpenseCategory` entity and migration (G-9: `M_A49_003`) | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 381 | Seed default categories: Travel, Meals, Office, Telecom, Lodging, Other (G-9: `M_A49_007`) | DB | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 382 | Create `ExpenseClaim` entity: `EmployeeId`, `ClaimDate`, `Status` (Draft/Submitted/Approved/Paid/Rejected), `PaymentMode` (OwnAccount/CompanyAccount), `SettlementMethod` (WithPayroll/SeparatePayment) | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 383 | Create `ExpenseLine` entity: `CategoryId`, `Description`, `Date`, `Amount`, `TaxCode`, `TaxAmount`, `CostCenterCode`, `Attachment` (FileStore) | BE | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 384 | Write migration `M_H6E_001_CreateExpenseClaims` | DB | CORE | done | Entity/map/seed in A49_HrmInitial; model test (58 tables); seeds: SeedTests |
| 385 | Implement `IExpenseClaimService` — submit, approve, reject, pay | BE | LIFE | done | LifeExpenseTests; host submit/approve/pay |
| 386 | Implement WorkflowEngine integration: EXPENSE_CLAIM approval | BE | LIFE | done | Host: real engine, bystander 403, handler callback |
| 387 | Implement receipt requirement: enforce above category threshold | BE | LIFE | done | LifeExpenseTests Receipts_are_enforced... |
| 388 | Implement expense approval → build FinancialTransaction: Dr Expense (per category → CoA) / Cr EXPENSE_CLAIM_PAYABLE | BE | LIFE | done | LifeExpenseTests TS_G9_001... |
| 389 | Post via gateway as FinDocType.Expense → GL: PURCH type, QBO: JournalEntry (G-9) | BE | LIFE | done | Host: approve posts through real gateway (Posted or NoTarget accepted); GL PURCH and QBO JE routing not checked by LIFE |
| 390 | Implement separate payment: FinDocType.ExpensePayment → Dr CLAIM_PAYABLE / Cr BANK (G-9) | BE | LIFE | done | LifeExpenseTests TS_G9_002; host pay; LifeConcurrencyTests |
| 391 | Implement WithPayroll settlement: add to payslip line as EXPREIMB component | BE | LIFE | done | LifeExpenseTests TS_G9_003 via provider; not yet on a real payslip |
| 392 | Implement statutory remittance recording: `IStatutoryRemittanceService` (G-13) | BE | LIFE | done | LifeExpenseTests TS_G13_001 |
| 393 | Build FinancialTransaction for remittance: Dr TAX/EOBI/PF_PAYABLE / Cr BANK (G-13) | BE | LIFE | done | LifeExpenseTests TS_G13_001 |
| 394 | Post via gateway as FinDocType.StatutoryRemittance (G-13) | BE | LIFE | done | LifeExpenseTests TS_G13_001; not run on host |
| 395 | Create expense claim submission UI: lines, receipts, category selection | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 396 | Create expense approval queue UI | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 397 | Create statutory remittance tracker UI: period, type, calculated vs paid vs balance | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| 398 | Unit test G-9: expense approved → JE created (TS-G9-001) | QA | LIFE | done | LifeExpenseTests TS_G9_001 |
| 399 | Unit test G-9: expense paid separately → Dr Payable / Cr Bank (TS-G9-002) | QA | LIFE | done | LifeExpenseTests TS_G9_002 |
| 400 | Unit test G-9: expense with payroll → EXPREIMB on payslip (TS-G9-003) | QA | LIFE | done | LifeExpenseTests TS_G9_003 (provider level) |
| 401 | Unit test G-13: statutory remittance → Dr Payable / Cr Bank (TS-G13-001) | QA | LIFE | done | LifeExpenseTests TS_G13_001 |

## DOC

| # | Task | Type | Owner | Status | Evidence |
|---|---|---|---|---|---|
| 402 | Document all HRM API endpoints in OpenAPI/Swagger | DOC | DOC | done | docs/hrm/API-REFERENCE.md; 262 operations with [EndpointSummary]; HrmSwaggerDocumentTests generates the document in-process on LocalDB (every action present, summarized, no route clash). No response schemas: contract remains the source for bodies |
| 403 | Document payroll posting matrix with account role mapping | DOC | PAY | done | docs/hrm/PAYROLL-POSTING-AND-FORMULAS.md |
| 404 | Document salary formula engine syntax and built-in functions | DOC | PAY | done | docs/hrm/PAYROLL-POSTING-AND-FORMULAS.md |
| 405 | Document leave policy configuration guide | DOC | ATT | done | docs/hrm/LEAVE-POLICY-GUIDE.md |
| 406 | Document QuickBooks integration setup for HR (cost center mapping, sync enablement) | DOC | POST | done | POST-GUIDE |
| 407 | Document P2/P3/P-PK profile differences | DOC | POST | done | POST-GUIDE |
| 408 | Create Odoo CSV import mapping guide (Appendix A of A49) | DOC | DOC | done | docs/hrm/MIGRATION-MAPPING-GUIDE.md + HRM-MIGRATION-MAPPING.csv (§10 workbook), from HrmEmployeeImportTarget. Odoo names unverified |
| 409 | Create SAP flat-file import mapping guide (Appendix B of A49) | DOC | DOC | done | docs/hrm/MIGRATION-MAPPING-GUIDE.md + HRM-MIGRATION-MAPPING.csv. Mapping only, no SAP code. SAP names unverified |
| 410 | Document entity relationship diagram for all A49 entities | DOC | CORE | done | docs/hrm/ER-DIAGRAM.md (all phases, from the maps) |
| 411 | Document deployment checklist per profile | DOC | DOC | done | docs/hrm/DEPLOYMENT-CHECKLIST.md, read against ModuleProfileService, AesEncryptionService, HrmSeedData, the A49 migrations |
| 412 | Integration test: full hire-to-retire cycle (recruit → hire → attendance → payroll → settlement) | QA | QA | open |  |
| 413 | Integration test: P2-S acceptance (service invoice → receipt → payroll → expense → TB/P&L) | QA | QA | open |  |
| 414 | Integration test: payroll → GL JE → QBO JournalEntry end-to-end | QA | QA | open |  |
| 415 | Integration test: expense claim → approval → GL → QBO | QA | QA | open |  |
| 416 | Performance test: payroll calculation for 500 employees < 30 seconds | QA | QA | open |  |
| 417 | Performance test: QBO JE split for 1200-line payroll | QA | QA | open |  |
| 418 | Multi-tenant isolation test: Org A payroll not visible to Org B | QA | QA | open |  |
| 419 | Security test: S2/S3 fields encrypted at rest, masked in API responses | QA | QA | open |  |
| 420 | Security test: HR scope enforcement (Department/Branch/Self) | QA | QA | open |  |
| 421 | Verify all 22 gaps (G-1 through G-22) resolved | QA | QA | open |  |
| 422 | Final checklist walkthrough | DOC | REV | open |  |

## Extras (§11A.8 and Part A)

| # | Phase | Task | Type | Owner | Status | Evidence |
|---|---|---|---|---|---|---|
| E-1 | H1P | Enablement wizard API: GET setup/status, POST setup/payroll-cost-center (select or create inline), mode | BE | POST | done | HrmSetupService; PostTests E1/E6; HrmPostE2ETests E6 |
| E-2 | H1P | IModuleEnablementGuard in Shared; Hrm implements; Tenancy enable and licence paths call it; 422 PAYROLL_COST_CENTER_REQUIRED | BE | POST | done | Both paths: PostTests, HrmEnablementGuardTests, HrmPostE2ETests E6 (licence path licenses, switch off: D-POST-1) |
| E-3 | H1P | Cost center step in the module-enable wizard (select / create inline) | FE | FE | done | Mocked-HTTP specs in pages/hrm (hrm-records, hrm-screens, employee, payroll-wizard, hrm.routes, hrm-integration); not run against a live API |
| E-4 | H1P | HrmSettings columns + guarded migration (done in A49_HrmInitial) | DB | CORE | done | A49_HrmInitial; OrgStructureTests |
| E-5 | H1P | Mode switch applies to future runs (run snapshots mode) | BE | PAY | done | Run snapshots mode; PayrollCalculationTests |
| E-6 | H1P | Tests: 422 path, inline creation | QA | POST | done | PostTests E6; HrmEnablementGuardTests; E2E |
| E-7 | H5P | Central / By-department splitter (GetCostCenterSharesAsync done; run allocation PAY) | BE | PAY | done | Allocate; PayrollLifecycleTests split test |
| E-8 | H5P | Liability lines post once to the payroll cost center | BE | PAY | done | NET row keeps the center; PayrollLifecycleTests |
| E-9 | H5P | Cost center to Class mapping tab data + Finalize block | BE | POST | done | Map routes + readiness + HRM_SYNC_NOT_READY; G5, G15_002; reference route for FE-2 |
| E-10 | H5P | No-target export and status | BE | PAY | done | ExportJournalAsync; PayrollLifecycleTests no-target theory |
| E-11 | H1P | P2-S acceptance: service invoice, receipt, payroll, expense, TB and P&L | QA | QA | open |  |
| E-12 | H5P | Provider-adapter seam review for P-PK (design note only; no second adapter) | DOC | POST | done | POST-GUIDE design note |
| E-13 | H1P | Manual service invoice in Finance AR (A-14): POST api/sales-invoices/service, SourceType SERVICE_MANUAL | BE | POST | done | ServiceInvoiceTests; HrmPostE2ETests E13; migration verified on LocalDB only. QuickBooks via default service item, FAKE only; per-line income account reaches the GL only (D-POST-6) |
| E-14 | H1P | Odoo CSV employee import target + profile (A-15, FD-8) | BE | POST | done | HrmEmployeeImportTarget; PostTests E14; ImportRequiredPermissionTests. Create only, no wage (D-POST-5). Import pipeline E2E not run for this target |
| E-15 | H2 | Workflow status handlers: LEAVE_REQUEST (ATT), SALARY_CHANGE (PAY), EXPENSE_CLAIM (LIFE) | BE | ATT | done | AttCalculator/AttAttendance/AttPayInput/AttLeave/AttLocalDb tests green |
| E-16 | H1P | Profile definitions P2, P2-S, P3, P-PK (data) and apply-profile | BE | POST | done | HrmEnablementGuardTests; HrmPostE2ETests E16 |
