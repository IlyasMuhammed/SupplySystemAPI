# Addendum 49 — entity relationships (task 410)

All 58 tables of schema `hrm`, grouped by phase, with `integration.CostCenterQboMappings` at the end. Taken from the maps in `src/SMS.Modules.Hrm/Data/Maps`. Every table also has `Id`, `Uuid`, `OrganizationId` and the audit columns. Foreign keys stay inside `hrm`. Values that point to another module are plain columns with no foreign key: `UserId`, `WarehouseUuid`, `*AttachmentUuid`, `ApprovalUuid`, `AccountCode`, `PostingRole`, `ExpenseRole`.

A dashed line is optional from the child's side (nullable FK). A solid line is required.

## H1 — organization and employees

```mermaid
erDiagram
    HrmSettings }o..o| CostCenters : "payroll cost center"
    CostCenters }o..o| CostCenters : parent
    CostCenters }o..o| Branches : branch
    Departments }o..o| Departments : parent
    Departments }o..o| Employees : head
    Departments }o..o| Branches : branch
    Departments }o..o| CostCenters : "cost center"
    Employees ||--o{ EmploymentRecords : has
    Employees ||--o{ EmployeeBankAccounts : has
    Employees ||--o{ EmployeeDocuments : has
    EmploymentRecords }o--|| Departments : department
    EmploymentRecords }o--|| Designations : designation
    EmploymentRecords }o--|| Branches : branch
    EmploymentRecords }o..o| Grades : grade
    EmploymentRecords }o..o| Employees : manager
    EmploymentRecords }o..o| Shifts : shift
    EmploymentRecords }o..o| CostCenters : override
    EmploymentRecords ||--o{ EmploymentCostSplits : splits
    EmploymentCostSplits }o--|| CostCenters : "cost center"
    HrUserScopes }o..o| Branches : branch
    HrUserScopes }o..o| Departments : department
```

`SensitiveAccessLogs` has `EmployeeId` as a plain column with no FK and is append-only.

## H2 — leave and attendance

```mermaid
erDiagram
    Holidays }o..o| Branches : branch
    Employees ||--o{ AttendanceEvents : clocks
    Employees ||--o{ AttendanceDays : computed
    AttendanceDays }o..o| Shifts : shift
    Employees ||--o{ OvertimeRecords : has
    OvertimeRecords }o..o| PayrollRuns : "paid in"
    LeaveTypes ||--o{ LeavePolicies : policies
    LeavePolicies }o..o| Grades : grade
    Employees ||--o{ LeaveLedgerEntries : balance
    LeaveLedgerEntries }o--|| LeaveTypes : type
    Employees ||--o{ LeaveRequests : requests
    LeaveRequests }o--|| LeaveTypes : type
```

## H3 — salary

```mermaid
erDiagram
    SalaryStructures ||--o{ SalaryStructureLines : lines
    SalaryStructureLines }o--|| SalaryComponents : component
    Employees ||--o{ SalaryAssignments : has
    SalaryAssignments }o--|| SalaryStructures : structure
    SalaryAssignments ||--o{ SalaryAssignmentLines : lines
    SalaryAssignmentLines }o--|| SalaryComponents : component
    TaxSlabTables ||--o{ TaxSlabs : slabs
```

`StatutoryRates` has no foreign keys. `SalaryAssignments.IncrementProposalId` is a plain column.

## H4 — recruitment, performance, training

```mermaid
erDiagram
    Vacancies }o--|| Designations : designation
    Vacancies }o--|| Departments : department
    Vacancies }o..o| Branches : branch
    JobApplications }o--|| Candidates : candidate
    JobApplications }o--|| Vacancies : vacancy
    JobApplications }o..o| Employees : "hired as"
    JobApplications ||--o{ Interviews : interviews
    Interviews }o..o| Employees : interviewer
    JobApplications ||--o{ JobOffers : offers
    Reviews }o--|| Employees : employee
    Reviews }o--|| ReviewCycles : cycle
    Reviews }o..o| Employees : reviewer
    Goals }o--|| Employees : employee
    Goals }o--|| ReviewCycles : cycle
    CourseEnrollments }o--|| Employees : employee
    CourseEnrollments }o--|| Courses : course
```

## H5 — payroll

```mermaid
erDiagram
    PayrollRuns }o..o| PayrollRuns : "corrects (ReversalOfRunId)"
    PayrollRuns ||--o{ PayrollRunLines : lines
    PayrollRunLines }o--|| Employees : employee
    PayrollRunLines ||--o{ PayrollRunLineComponents : snapshot
    PayrollRunLines ||--o| Payslips : payslip
    PayrollRuns ||--o{ PayrollPostings : postings
    PayrollAdjustments }o--|| Employees : employee
    PayrollAdjustments }o..o| PayrollRuns : "for run"
    PayrollAdjustments }o..o| PayrollRuns : "applied in"
    PayrollAdjustments }o..o| ExpenseClaims : reimburses
```

## H6 and H6E — loans, settlement, letters, expenses

```mermaid
erDiagram
    Loans }o--|| Employees : employee
    Loans ||--o{ LoanInstallments : schedule
    LoanInstallments }o..o| PayrollRuns : "deducted in"
    FinalSettlements }o--|| Employees : employee
    FinalSettlements ||--o{ FinalSettlementLines : lines
    IncrementProposals }o--|| Employees : employee
    IncrementProposals }o..o| Reviews : review
    IncrementProposals }o..o| SalaryAssignments : "applied as"
    IssuedLetters }o--|| Employees : employee
    IssuedLetters }o..o| LetterTemplates : template
    TaxCertificates }o--|| Employees : employee
    ExpenseClaims }o--|| Employees : employee
    ExpenseClaims }o..o| PayrollRuns : "paid in"
    ExpenseClaims ||--o{ ExpenseLines : lines
    ExpenseLines }o--|| ExpenseCategories : category
```

`StatutoryRemittances` has no foreign keys.

## Outside schema `hrm`

`integration.CostCenterQboMappings` has `ConnectionId` and `CostCenterCode` (unique together) and no FK to `hrm`. It matches `hrm.CostCenters.Code` by value.
