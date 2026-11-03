# SMS — Functional Specification Document
## Addendum 49 (Detailed): HRM Module — Data Dictionary, Odoo/SAP Mapping, Financial Posting & Integration Scope

| Property | Value |
|---|---|
| Document ID | SMS-FSD-ADD-049-D |
| Version | 1.1 (Revision 2 added — see §11A) |
| Date | 2026-10-29 |
| Status | Draft |
| Companion to | SMS-FSD-ADD-049 (functional scope), Task Register Addendum 49 |
| Depends On | A37 Module Registry, A40 Import, A42 GL, A44 Posting Gateway + QuickBooks, A47 Journal Engine, A48 AR/AP |
| Reference models | Odoo (HR, Payroll, Time Off, Attendance, Recruitment, Expenses — model/field names as in Odoo 16/17) and SAP (HCM/SuccessFactors Employee Central concepts: PA infotypes, OM objects, T-tables, payroll results, FI posting) |

> **How to read the mapping columns.** "Odoo" gives `model.field`; "SAP" gives infotype/field (e.g. `PA0001-ORGEH`) or table. They are *conceptual* references so that consultants coming from either system can find the equivalent. SMS does not copy their schemas; where SMS deliberately differs, the Notes column says why. Names marked **(verify)** are from memory and must be checked against the customer's actual Odoo/SAP version before they are used in a migration mapping.

> **Evidence base for §7–§9.** Statements about what SMS already has are taken from the A44/A47/A37/A42 analysis documents and the code in `SMS.Shared`, `SMS.Modules.Integration`, `SMS.Modules.Accounting` and the project references of `SMS.Modules.Finance`. Items I could not confirm in code are labelled **UNVERIFIED**.

---

## Table of Contents

1. Purpose & Scope
2. Deployment Profiles (full SMS / HR + Finance + QuickBooks / HR only)
3. Tenant-Based Organization Structure
4. Concept Glossary: SMS ↔ Odoo ↔ SAP
5. Data Dictionary & Field Mapping
6. Wage-Type / Pay-Code and Process Mapping
7. Financial Posting Design (internal GL + QuickBooks via the gateway)
8. Integration Scope Matrix
9. Gap Analysis
10. Additions to the Task Register
11. Decisions Required
12. Appendices

---

## 1. Purpose & Scope

1. Give every HRM entity a field-level definition (type, rules, sensitivity) with its Odoo and SAP equivalent, so data can be migrated from either system and consultants can review the model quickly.
2. Keep the organization structure **tenant-based**, exactly as SMS works today: Organization (= tenant) → Branch → Department, no new top-level entity.
3. Define how **salary and other employee-related expenses** reach the books through the **Financial Posting Gateway** (`IFinancialPostingGateway`, A44) so they land in the **internal GL** and/or **QuickBooks Online** according to each tenant's configuration — HRM never talks to QuickBooks or the GL directly.
4. Check the **integration scope** for a client who buys **only HR + Finance + QuickBooks** (no Sales, Procurement, Inventory, Manufacturing, POS), and list the **gaps**.

Out of scope: UK/Canada/USA payroll, deferred HRM capabilities (see the base Addendum 49 §14).

---

## 2. Deployment Profiles

SMS is one deployable application; "profiles" are module-registry settings (A37), not separate builds.

| Profile | Modules enabled | Posting targets | Notes |
|---|---|---|---|
| **P1 Full SMS + HRM** | All + `MODULE_HRM` | Internal GL and/or QuickBooks | Payroll posts through the same gateway as invoices and payments. |
| **P2 HR + Finance + QuickBooks** (specific client) | `MODULE_HRM`, `MODULE_FINANCE` (always on), `FEATURE_GENERAL_LEDGER` (optional), `MODULE_INTEGRATION` | QuickBooks only, or QuickBooks + internal GL | **Needs gap fixes G-1 … G-9 (§9)** before it can work end to end. |
| **P3 HR only** | `MODULE_HRM` | None | Gateway answers `NoTargetConfigured`; payroll stops at **Finalized** and exports files. Must be a supported, non-error state (G-19). |

Evidence for P2: `SMS.Modules.Accounting` and `SMS.Modules.Integration` reference only `SMS.Shared` (they can run without Demand/Inventory). `SMS.Modules.Finance` however references Demand, Warehouse and Lookups, and `SMS.Modules.Suppliers` references Finance, Demand and Warehouse — so the binary always contains them; "standalone" means *switched off and hidden*, which has not been exercised (G-8).

---

## 3. Tenant-Based Organization Structure

### 3.1 Principle

An SMS **Organization** is the tenant boundary (`Guid OrganizationId`, EF tenant filter). In HRM it is the **employer** — one legal entity with one payroll. No new "company" or "tenant" table is introduced.

```
Organization (tenant = employer)
 ├─ Branch            (location / SAP personnel subarea / Odoo work location)
 │    └─ (optional link to a Warehouse for sites that are both)
 ├─ Department tree   (SAP org unit / Odoo hr.department; carries cost center)
 ├─ Designation       (SAP job / Odoo hr.job)
 ├─ Grade             (SAP pay scale group-level / Odoo —)
 └─ Employee ── EmploymentRecord (effective-dated: dept, designation, branch, grade, manager)
```

### 3.2 Mapping of the tenant hierarchy

| SMS | Odoo | SAP | Notes |
|---|---|---|---|
| Organization (tenant) | `res.company` | Company code (`T001-BUKRS`) + client | One SMS org = one employer/payroll. Corporate groups with several legal entities are **not** modelled (gap G-17). |
| Branch | `hr.work.location` / branch company | Personnel area (`WERKS`) / subarea (`BTRTL`) | Used for scope filtering, holidays and device grouping; not a legal entity. |
| Department (tree) | `hr.department` (`parent_id`, `manager_id`) | Org unit `O` object, relationship A002/A003 (OM) | `PA0001-ORGEH`. Department carries the **cost center code** used in payroll posting. |
| Designation | `hr.job` | Job `C` object (`STELL`) | Position-level (`S`/`PLANS`) is not modelled: SMS has no headcount-by-position. |
| Grade | — (Odoo uses structure types) | Pay scale group/level (`TRFGR/TRFST`), employee subgroup (`PERSK`) | Used for salary-band validation only. |
| Reporting line | `hr.employee.parent_id` | OM relationship A002 "reports to" (position-based) | SMS: employee → line manager. |
| Payroll group (optional) | `hr.payroll.structure.type` | Payroll area (`ABKRS`) | Lets one org run e.g. monthly staff and weekly wage payrolls. Default one group. |

### 3.3 Access rules (tenant + scope)

1. **Tenant first:** every row has `OrganizationId`; client-supplied org ids are ignored; cross-org access returns 404.
2. **Scope second:** within the tenant, a user's HR scope is `All | Branch | Department(+children) | Team | Self` (base FSD §16). Branch/department scope comes from the user's own employment record or an explicit assignment.
3. **Permission third:** sensitive data (salary, bank, CNIC, notes) needs its specific code regardless of scope.
4. **Super admin / platform support** get no automatic HR data access; exceptional access follows the deferred support-access workflow.

### 3.4 Posting dimensions follow the tenant hierarchy

Department → `CostCenter` on the posting line; Branch → `Project`/location dimension; both optional and mapped per tenant (see §7.6 and gap G-5).

---

## 4. Concept Glossary: SMS ↔ Odoo ↔ SAP

| Concept | SMS (HRM) | Odoo | SAP |
|---|---|---|---|
| Employee master | `hrm.Employees` | `hr.employee` | Pernr; infotypes 0001/0002/0006/0105/0185 |
| Employment terms (dated) | `EmploymentRecords` | `hr.contract` (+ employee) | PA0000 action, PA0001, PA0016, PA0007 |
| Pay assignment | `SalaryAssignments` | `hr.contract.wage` + structure | PA0008 Basic Pay (wage types) |
| Recurring pay item | Salary component line | Salary rule / contract allowance | PA0014 Recurring payments/deductions |
| One-off pay | `PayrollAdjustments` | `hr.payslip.input` | PA0015 Additional payments |
| Pay element | `SalaryComponent` | `hr.salary.rule` | Wage type (`T512W`) |
| Pay structure | `SalaryStructure` | `hr.payroll.structure` | Payroll schema / wage-type model |
| Payroll batch | `PayrollRun` | `hr.payslip.run` | Payroll run for a period (payroll control record) |
| Employee result | `PayrollRunLine` + components | `hr.payslip` + `hr.payslip.line` | Payroll result (cluster `RT` table, `PCL2`) |
| Payslip document | `Payslip` (PDF) | payslip report | Remuneration statement (form) |
| Posting to books | Gateway transaction | Payslip/batch "Create Draft Entry" → `account.move` | Payroll posting run (`PC00_M99_CIPE`) → FI document |
| Account determination | `AccountRole` → A42 default → account | Salary rule debit/credit account, structure journal | Wage type → symbolic account (`T52EL`) → G/L (`T030`) |
| Leave type | `LeaveType` | `hr.leave.type` | Absence/attendance type (`T554S`) |
| Leave entitlement | `LeaveLedgerEntries` (accrual) | `hr.leave.allocation` | Absence quota (PA2006) |
| Leave request | `LeaveRequest` | `hr.leave` | Absence (PA2001) |
| Attendance record | `AttendanceEvent/Day` | `hr.attendance` | Time event / PA2002 / time evaluation results |
| Overtime | `OvertimeRecord` | overtime on attendance / work entry type | PA2005 |
| Loan | `Loan` + installments | loan app (Enterprise/OCA) | PA0045 Loans |
| Expense claim | `ExpenseClaim` | `hr.expense` / `hr.expense.sheet` | Travel/expense (PR05 / Concur) |
| Applicant | `Candidate/Application` | `hr.applicant` | PB applicant data |
| Vacancy | `Vacancy` | `hr.job` (recruitment) | Vacancy (PB / OM position vacancy) |
| Review | `Review` | `hr.appraisal` (Enterprise) | Appraisal (OM appraisal catalog / SF Performance) |
| Course | `Course` | `hr.skill`/ eLearning | Training & Event Mgmt |

---

## 5. Data Dictionary & Field Mapping

**Conventions (all tables).** `Id int` PK, `Uuid Guid` (used in routes), `OrganizationId Guid` (tenant filter), `CreatedAt/CreatedBy/CreatedByName`, `UpdatedAt/UpdatedBy/UpdatedByName`, `RowVersion` where edited concurrently. Statuses are strings with CHECK constraints. Money `decimal(18,2)`, rates `decimal(9,4)`. Sensitivity: **S0** normal · **S1** personal · **S2** sensitive (permission-gated, masked) · **S3** secret (encrypted at rest, masked, audited on read).

### 5.1 Branch

| SMS field | Type / rule | Sens. | Odoo | SAP | Notes |
|---|---|---|---|---|---|
| Code | string(20), unique per org | S0 | `hr.work.location` (no code) | `BTRTL` | |
| Name | string(100) | S0 | `hr.work.location.name` | `T001P-BTEXT` | |
| Address / City | strings | S0 | `address_id` | `T001P` address | |
| WarehouseId | uuid? | S0 | — | Plant link (`WERKS`) | Only when branch is also a store/warehouse. |
| IsActive | bool | S0 | `active` | — | Cannot deactivate while employees use it. |

### 5.2 Department

| SMS field | Type / rule | Sens. | Odoo | SAP | Notes |
|---|---|---|---|---|---|
| Code | string(20), unique per org | S0 | — | Org unit short text | |
| Name | string(100) | S0 | `hr.department.name` | Org unit `O` text | |
| ParentId | FK self, no cycles | S0 | `parent_id` | OM relationship A002 | |
| HeadEmployeeId | FK Employee? | S0 | `manager_id` | Chief position holder (relationship A012) | |
| BranchId | FK? | S0 | `company_id`/location | `WERKS/BTRTL` via `PA0001` | |
| **CostCenterCode** | string(20)? | S0 | analytic account (`analytic_account_id` on contract/rule) | `PA0001-KOSTL` | Drives the posting `CostCenter` dimension. |
| IsActive | bool | S0 | `active` | Validity dates | |

### 5.3 Designation and Grade

| SMS field | Type / rule | Sens. | Odoo | SAP |
|---|---|---|---|---|
| Designation.Code / Name | string, unique code | S0 | `hr.job.name` | Job `C` / `PA0001-STELL` |
| Designation.Level | int | S0 | — | Job level (custom) |
| Grade.Code / Name | string | S0 | — | `PERSK` (employee subgroup) / pay scale group |
| Grade.MinSalary / MaxSalary | decimal; Min ≤ Max | S2 | — | Pay scale table `T510` ranges |

### 5.4 Employee (personal master)

| SMS field | Type / rule | Sens. | Odoo | SAP | Notes |
|---|---|---|---|---|---|
| EmployeeNumber | string, unique per org, generated | S0 | `hr.employee.barcode`/`registration_number` | `PERNR` | Never reused. |
| FirstName / LastName | string(60) | S1 | `name` (single field) | `PA0002-VORNA/NACHN` | SMS splits names; import splits Odoo `name` on first space (**verify** per customer convention). |
| FatherOrHusbandName | string(100)? | S1 | — | `PA0002-NACH2`/custom | Pakistan convention. |
| DateOfBirth | date | S1 | `birthday` | `PA0002-GBDAT` | |
| Gender | M/F/Other | S1 | `gender` | `PA0002-GESCH` | |
| MaritalStatus | enum | S1 | `marital` | `PA0002-FAMST` | |
| Nationality | ISO country | S1 | `country_id` | `PA0002-NATIO` | |
| **Cnic** | encrypted; 13-digit format | **S3** | `identification_id` | `PA0185` (ICTYP) `ICNUM` | Keyed hash column for uniqueness/exact search; masked `*****-*******-#`. |
| Ntn | encrypted? | S3 | — | `PA0185` (other ID type) | |
| PersonalEmail / WorkEmail | string | S1 | `private_email` / `work_email` | `PA0105` subtype 0010 | |
| Phone / Mobile | string | S1 | `phone` / `mobile_phone` | `PA0105` subtype CELL | |
| CurrentAddress / PermanentAddress | structured | S1 | `private_street…` | `PA0006` (subtype 1/2) | |
| Photo | FileStore key | S1 | `image_1920` | — | |
| Status | enum Candidate/Probation/Active/OnNotice/Suspended/Terminated/Archived | S0 | `active` + `departure_date` | `PA0000-STAT2` (employment status) | |
| UserId | int? (SMS user) | S0 | `user_id` | `PA0105` subtype 0001 (user name) | One user per employee per org. |
| SourceCandidateId | uuid? | S0 | applicant → employee link | PB → PA hiring action | |
| JoinDate / LeaveDate | date | S0 | `contract.date_start` / `departure_date` | `PA0041` date specs / `MASSN` action dates | |
| DepartureReason | enum | S1 | `departure_reason_id` | `PA0000-MASSG` | |

*Emergency contact*: SMS `EmergencyContacts(Name, Relation, Phone)` ↔ Odoo `emergency_contact`, `emergency_phone` ↔ SAP `PA0021` (Family/Related Persons, subtype emergency).

### 5.5 EmploymentRecord (effective-dated)

| SMS field | Type / rule | Sens. | Odoo | SAP | Notes |
|---|---|---|---|---|---|
| EffectiveFrom / EffectiveTo | date; no overlap per employee | S0 | `hr.contract.date_start/date_end` | infotype validity `BEGDA/ENDDA` | |
| EmploymentType | Permanent/Contract/Probation/Intern/DailyWage | S0 | `contract_type_id` | `PA0016-CTTYP`, `PA0001-PERSG` | |
| DepartmentId | FK | S0 | `department_id` | `PA0001-ORGEH` | |
| DesignationId | FK | S0 | `job_id` | `PA0001-STELL` | |
| BranchId | FK | S0 | `work_location_id` | `PA0001-WERKS/BTRTL` | |
| GradeId | FK? | S0 | — | `PA0001-PERSK` | |
| ManagerEmployeeId | FK? no cycles | S0 | `parent_id` | OM reports-to | |
| ShiftId / WorkScheduleId | FK | S0 | `resource_calendar_id` | `PA0007-SCHKZ` | |
| ProbationEndDate | date? | S0 | `trial_date_end` | `PA0016-PRBZT` (+ `PA0041`) | |
| ConfirmationDate | date? | S0 | — | `PA0041` date type | |
| ContractEndDate | date? | S0 | `date_end` | `PA0016` / `PA0041` | Expiry reminders. |
| NoticePeriodDays | int | S0 | — | `PA0016-KGZRT` | |
| CostCenterCode | string? overrides department | S0 | analytic account | `PA0001-KOSTL` | |
| PayrollGroup | string? | S0 | structure type | `PA0001-ABKRS` | |
| ChangeReason | enum | S0 | — | `PA0000-MASSN/MASSG` | Hire, transfer, promotion, confirmation… |

### 5.6 EmployeeBankAccount

| SMS field | Type / rule | Sens. | Odoo | SAP |
|---|---|---|---|---|
| BankName / BranchName | string | S1 | `res.partner.bank.bank_id` | `PA0009-BANKS/BANKL` |
| AccountTitle | string | S1 | `acc_holder_name` | `PA0009-EMFTX` |
| AccountNumber / Iban | encrypted | **S3** | `bank_account_id.acc_number` | `PA0009-BANKN / IBAN` |
| IsPrimary | bool; one active primary | S0 | (single account) | `PA0009` subtype 0 main bank |
| PaymentMethod | Bank/Cash/Cheque | S0 | — | `PA0009-ZLSCH` |

### 5.7 Shift, Attendance, Overtime

| SMS field | Type | Odoo | SAP | Notes |
|---|---|---|---|---|
| Shift.Start/End/BreakMinutes | time/int | `resource.calendar.attendance` hour_from/hour_to | Daily work schedule (`T550A`) | |
| Shift.GraceIn/Out, HalfDayMinutes | int | attendance settings | Time evaluation parameters | |
| Shift.WeeklyOffDays | flags | calendar days not defined | Period work schedule (`T551A`) | |
| Holiday.Date/Name | date/string | `resource.calendar.leaves` | Public holiday calendar (`THOC`) | Seeded by country pack. |
| AttendanceEvent.EventTime (UTC) | datetime | `hr.attendance.check_in/out` | Time event (`PA2011`/clock) | Raw, immutable. |
| AttendanceEvent.Source / ExternalEventId | string | — | Terminal/ID | Unique (org, source, external id) → idempotent. |
| AttendanceDay.FirstIn/LastOut/WorkedMinutes | computed | `worked_hours` | `PA2002` / time results | |
| AttendanceDay.Late/Early/OvertimeMinutes | computed | overtime in attendance | Time evaluation results | |
| AttendanceDay.Status | enum | work entry type | Attendance/absence type | |
| OvertimeRecord.Minutes/Multiplier/Status | | `overtime_hours` | `PA2005` | Approved overtime feeds payroll. |

### 5.8 Leave

| SMS field | Type | Odoo | SAP | Notes |
|---|---|---|---|---|
| LeaveType.Code/Name | string | `hr.leave.type.name` | `T554S` absence type | |
| LeaveType.IsPaid | bool | `unpaid` (inverted) | Counting/payment rule | |
| LeaveType.CarryForwardCap | decimal | allocation accrual `carryover` | Quota deduction/transfer rule (`T556`) | |
| LeaveType.AttachmentAfterDays | int | `support_document` | Certificate requirement (custom) | |
| LeavePolicy.Entitlement/AccrualMethod | decimal/enum | `hr.leave.accrual.plan` | Quota generation rule (`T559L`) | Effective-dated. |
| LeaveLedgerEntry.Delta/Reason/Date | decimal/enum | `hr.leave.allocation.number_of_days` + `hr.leave` | `PA2006` quota + `PA2001` absences | **SMS balance = Σ ledger** (Odoo/SAP keep quota + consumption). |
| LeaveRequest.From/To/HalfDay | date/bool | `request_date_from/to`, `request_unit_half` | `PA2001-BEGDA/ENDDA`, `AWART` | |
| LeaveRequest.Status | Pending/Approved/Rejected/Cancelled | `state` | Workflow/approval status | |

### 5.9 Salary: components, structure, assignment

| SMS field | Type / rule | Sens. | Odoo | SAP | Notes |
|---|---|---|---|---|---|
| SalaryComponent.Code/Name | string, unique | S0 | `hr.salary.rule.code/name` | Wage type number/text | |
| Component.Type | Earning/Deduction/EmployerContribution | S0 | rule category (BASIC, ALW, DED, COMP) | Wage type processing class / cumulation | |
| Component.IsTaxable / InGross | bool | S0 | category flags | Processing class 07 / cumulation wage types `/101…` | |
| Component.CalcMethod | Fixed/Percent/Formula | S0 | `amount_select` (fix/percentage/code) | Operation / rule `PE…` | Formula = SMS safe expression grammar, no code execution. |
| **Component.PostingRole** | account role key | S0 | rule debit/credit account | Symbolic account (`T52EL`) | See §7.4. |
| SalaryStructure.Name / Lines | | S0 | `hr.payroll.structure` + `rule_ids` | Payroll schema / wage-type model | |
| SalaryAssignment.EmployeeId, StructureId | | S2 | `contract.structure_type_id` | `PA0008-TRFAR/TRFGR` | |
| SalaryAssignment.Amounts per component | decimal | **S2** | `contract.wage` + allowances | `PA0008` wage type amounts (`LGA01/BET01`) | Effective-dated; every change a new row. |
| EffectiveFrom/To | date | S0 | `date_start/end` | `BEGDA/ENDDA` | |
| ApprovalStatus | enum | S0 | contract state (draft/open) | Change approval | |
| TaxSlab / StatutoryRate | tables with Verified flag | S0 | rule parameters (`hr.rule.parameter`) | Tax tables `T5…`, `PA0207/0209` tax data | **Data, not code.** |

### 5.10 Payroll run

| SMS field | Type / rule | Sens. | Odoo | SAP | Notes |
|---|---|---|---|---|---|
| PayrollPeriod.Start/End/PayDate | date | S0 | `hr.payslip.run.date_start/end` | Payroll period (`T549Q`) | |
| PayrollRun.Status | Draft/Calculated/UnderReview/Approved/Finalized/Posted/Paid/Cancelled/Reversed | S0 | run `state` (draft/verify/close/paid) | Release/exit payroll, "posted to accounting" status | |
| PayrollRun.PayrollGroup | string | S0 | structure type | `ABKRS` | Unique non-cancelled run per period+group. |
| PayrollRunLine.EmployeeId | FK | S2 | `hr.payslip.employee_id` | `PERNR` in results | Unique (run, employee). |
| Line.PaidDays / WorkedDays | decimal | S2 | `worked_days_line_ids` | Work days in `WPBP` / `ABC` | |
| Line.Gross / TotalDeductions / Net | decimal | **S2** | payslip lines GROSS/NET | `/101`, `/559`, `/5xx` cumulation WTs | |
| LineComponent.Code/Type/Amount/Basis | snapshot | **S2** | `hr.payslip.line` (code, amount, total) | `RT` table rows | Immutable snapshot of what was computed. |
| Line.IsBlocked / BlockReason | | S0 | — | Error/“payroll error” list | Missing bank account, unverified rate… |
| Payslip.FileKey | FileStore | S2 | payslip PDF | Form output | Immutable. |
| PayrollAdjustment | one-off item | S2 | `hr.payslip.input` | `PA0015` | |
| PayrollPosting | RunId, journal refs, status | S0 | `move_id` on payslip | FI document number in posting run | Idempotent by run. |

### 5.11 Loans, settlement, increments, letters

| SMS | Key fields | Odoo | SAP |
|---|---|---|---|
| Loan | Type, Principal, InstallmentCount, StartMonth, Status | loan (OCA/Enterprise) | `PA0045` Loans (loan type, approved amount, repayment) |
| LoanInstallment | DueMonth, Amount, Status, RunId | loan line | Loan repayment schedule / `PA0045` payment lines |
| FinalSettlement(+Lines) | LastWorkingDay, components, Net, Status | final pay via structure “Final Settlement” (custom) | Off-cycle payroll after leaving action (`PA0000-MASSN` leaving), `PA0267` |
| IncrementProposal | New salary/%, EffectiveDate, ReviewId | `hr.contract` new wage + appraisal | `PA0008` new record via action "Pay change" |
| IssuedLetter | Template, Code, Issuer | report templates (documents) | Forms / HR Forms (`HRFORMS`) |
| TaxCertificate | EmployeeId, TaxYear, totals | — | Annual tax statement (country form) |

### 5.12 Expense claims (employee-related "other expenses")

| SMS field | Type / rule | Odoo | SAP | Notes |
|---|---|---|---|---|
| ExpenseClaim.EmployeeId, ClaimDate, Status | Draft/Submitted/Approved/Paid/Rejected | `hr.expense.sheet` (`state`) | Trip/expense report header | Workflow EXPENSE_CLAIM. |
| ExpenseClaim.PaymentMode | OwnAccount (reimburse) / CompanyAccount | `payment_mode` | Payment type | CompanyAccount = already paid by company card: no reimbursement. |
| ExpenseLine.Category / Description / Date | | `hr.expense.product_id/name/date` | Expense type | Category carries the **expense account role**. |
| ExpenseLine.Amount, TaxCode, TaxAmount | decimal | `total_amount`, `tax_ids` | Expense amount, tax code | |
| ExpenseLine.CostCenterCode | string | analytic distribution | Cost object | Defaults from employee's department. |
| Attachment (receipt) | FileStore | `attachment_ids` | Receipt | Required above a threshold. |
| SettlementMethod | WithPayroll / SeparatePayment | add to payslip input | Pay via payroll (`PA0015`) or AP | Default WithPayroll. |

### 5.13 Recruitment, performance, training (summary mapping)

| SMS | Odoo | SAP |
|---|---|---|
| Requisition → Vacancy | `hr.job` (no_of_recruitment) | Vacancy on position / PB vacancy |
| Candidate / Application (+Stage) | `hr.applicant` (`stage_id`) | PB applicant (`PB0000/PB4000`), application status |
| Interview / Feedback | calendar event + `hr.applicant` notes | Applicant activity |
| Offer | `hr.contract` draft / offer template | Hiring action |
| Convert to employee | "Create Employee" | Applicant → employee hiring (`PA40` from PB) |
| ReviewCycle / Review / Goal | `hr.appraisal` / goals | Appraisal catalog / SF goals |
| Course / Session / Enrollment / Certificate | `slide.channel` / skills | Business event (`D` objects) / qualifications |

---

## 6. Wage-Type / Pay-Code and Process Mapping

### 6.1 Standard component set (Pakistan, seeded as data — names only, values are not asserted)

| SMS code | Name | Type | Taxable | Posting role | Odoo rule cat. | SAP wage-type class |
|---|---|---|---|---|---|---|
| BASIC | Basic salary | Earning | Yes | `SALARY_EXPENSE` | BASIC | Basic pay (1000-series) |
| HRENT | House rent | Earning | per rule | `SALARY_EXPENSE` | ALW | Allowance |
| UTIL | Utilities | Earning | per rule | `SALARY_EXPENSE` | ALW | Allowance |
| CONV | Conveyance/allowances | Earning | per rule | `SALARY_EXPENSE` | ALW | Allowance |
| OT | Overtime | Earning | Yes | `OVERTIME_EXPENSE` | ALW | Overtime (PA2005 result) |
| BONUS | Bonus | Earning | Yes | `BONUS_EXPENSE` | ALW | Additional payment |
| ARREARS | Arrears | Earning | Yes | `SALARY_EXPENSE` | ALW | Retro-accounting difference |
| ITAX | Income tax withheld | Deduction | — | `EMPLOYEE_TAX_PAYABLE` | DED | Tax cumulation `/4xx` |
| EOBI_EE / EOBI_ER | EOBI employee / employer | Deduction / Employer | — | `EOBI_PAYABLE` (+ `EMPLOYER_CONTRIB_EXPENSE`) | DED / COMP | Social-insurance wage types |
| PF_EE / PF_ER | Provident fund | Deduction / Employer | — | `PF_PAYABLE` (+ expense) | DED / COMP | Pension/savings plan |
| SS_EE / SS_ER | Social security (provincial) | Deduction / Employer | — | `SOCIAL_SECURITY_PAYABLE` (+ expense) | DED / COMP | Social-insurance |
| LOAN | Loan recovery | Deduction | — | `EMPLOYEE_LOAN_RECEIVABLE` | DED | `PA0045` repayment WT |
| ADV | Advance recovery | Deduction | — | `EMPLOYEE_ADVANCE` | DED | `PA0014/PA0045` |
| EXPREIMB | Expense reimbursement | Earning (non-taxable) | No | `EXPENSE_CLAIM_PAYABLE` | ALW | Reimbursement WT |
| OTHDED | Other deductions | Deduction | — | `OTHER_DEDUCTION_PAYABLE` | DED | Deduction |
| NET | Net pay | (result) | — | `SALARY_PAYABLE` | NET | `/559` net transfer |

Rates, slabs and ceilings are **not** defined here (effective-dated data, "Unverified" until reviewed — base FSD C24 / OD-4).

### 6.2 Process mapping

| Process | SMS | Odoo | SAP |
|---|---|---|---|
| Hire | Convert offer / create employee + employment + assignment | Applicant → employee + contract | PA40 hiring action |
| Transfer / promotion | New employment record (reason) | New contract/employee fields | PA40 org reassignment |
| Monthly payroll | Create run → calculate → review → approve → finalize → post → pay | Batch → compute sheet → confirm → account entry → pay | Simulate → release → exit → payroll posting → bank transfer (DME) |
| Payroll correction | Reversal run + new run | Refund payslip / new | Retroactive accounting / off-cycle |
| Final pay | Final settlement | Final payslip structure | Off-cycle run after leaving |
| Salary payment | Bank file → mark Paid → payment posting | Bank statement reconciliation | DME/payment run |

---

## 7. Financial Posting Design

### 7.1 Rule

HRM **never** writes journals and never calls QuickBooks. It builds a provider-neutral `FinancialTransaction` and calls `IFinancialPostingGateway.PostAsync` **after its own commit** (A44 D-9/D-25). The gateway routes to the targets the tenant has switched on (internal GL = `FEATURE_GENERAL_LEDGER` + `InternalAccountingEnabled` + GL setup Complete; QuickBooks = `MODULE_INTEGRATION` + a connected connection). A failure at one target never fails the payroll and never blocks the other target.

### 7.2 Transactions HRM publishes

New `FinDocType` values are needed (the enum has `ManualJournal` and `Expense` but no payroll types — **gap G-2**). New `PostingSourceModules.Hrm = "hrm"` (currently Finance/Pos/Suppliers/Inventory only).

| HRM event | FinDocType (new unless noted) | Idempotency key | Internal GL journal type (A47 D-12) | QuickBooks object (target of G-1) |
|---|---|---|---|---|
| Payroll run posted | `PayrollRun` | `hrm:PayrollRun:{runUuid}` | MISC (propose a PAYR type, optional) | `JournalEntry` |
| Salaries paid | `PayrollPayment` | `hrm:PayrollPayment:{runUuid}` | BANK/CASH | `JournalEntry` (Dr Salaries Payable, Cr Bank) |
| Payroll reversal | void of `PayrollRun` | `…:void` | reversing journal (A47 D-14) | reversing `JournalEntry` (not delete) — G-12 |
| Loan disbursement | `LoanDisbursement` | `hrm:LoanDisbursement:{loanUuid}` | BANK/CASH | `JournalEntry` |
| Expense claim approved | `Expense` (exists) | `hrm:Expense:{claimUuid}` | PURCH | `JournalEntry` v1; `Purchase`/`Bill` later |
| Expense claim paid separately | `Payment`-like `ExpensePayment` | `hrm:ExpensePayment:{claimUuid}` | BANK/CASH | `JournalEntry` |
| Statutory remittance (tax/EOBI/PF paid to authority) | `StatutoryRemittance` | `hrm:StatutoryRemittance:{id}` | BANK/CASH | `JournalEntry` — G-13 |
| Final settlement | `FinalSettlement` | `hrm:FinalSettlement:{uuid}` | MISC | `JournalEntry` |
| Employer provisions (gratuity/leave encashment accrual) — optional | `PayrollProvision` | `hrm:PayrollProvision:{period}` | MISC | `JournalEntry` |

Why JournalEntry for QuickBooks: QBO's native payroll objects are not writable through the accounting API, and employees are not SMS `BusinessPartner`s, so documents such as Bill/Invoice do not fit. A summarized JE is the standard integration pattern.

### 7.3 Account roles (A42 defaults) — additions

A42 has 16 default keys (`DEFAULT_RECEIVABLE … DEFAULT_ROUNDING`). HRM needs more (**gap G-4**). Proposed keys, seeded unmapped and added to the GL "setup complete" check **only for orgs with HRM payroll enabled**:

`DEFAULT_SALARY_EXPENSE`, `DEFAULT_OVERTIME_EXPENSE`, `DEFAULT_BONUS_EXPENSE`, `DEFAULT_EMPLOYER_CONTRIB_EXPENSE`, `DEFAULT_SALARY_PAYABLE`, `DEFAULT_EMPLOYEE_TAX_PAYABLE`, `DEFAULT_EOBI_PAYABLE`, `DEFAULT_PF_PAYABLE`, `DEFAULT_SOCIAL_SECURITY_PAYABLE`, `DEFAULT_OTHER_DEDUCTION_PAYABLE`, `DEFAULT_EMPLOYEE_LOAN_RECEIVABLE`, `DEFAULT_EMPLOYEE_ADVANCE`, `DEFAULT_EXPENSE_CLAIM_PAYABLE`, `DEFAULT_GRATUITY_EXPENSE`, `DEFAULT_GRATUITY_PROVISION`, `DEFAULT_LEAVE_ENCASHMENT_EXPENSE`. (`DEFAULT_BANK`, `DEFAULT_CASH` reused.)

Expense categories carry their own account (explicit `AccountCode` on the line wins over role, as in A44 D-5).

### 7.4 Posting matrix — payroll run (Posted)

Summarized by department/cost center (one line per account × cost center). **Per-employee amounts are never sent** to the gateway payload for external targets (privacy, G-11); the run keeps the detail.

| Dr / Cr | Account role | Amount | Dimension |
|---|---|---|---|
| Dr | `SALARY_EXPENSE` (+ OVERTIME / BONUS roles) | Σ earnings (gross) | cost center = department |
| Dr | `EMPLOYER_CONTRIB_EXPENSE` | Σ employer contributions | cost center |
| Cr | `EMPLOYEE_TAX_PAYABLE` | Σ income tax withheld | — |
| Cr | `EOBI_PAYABLE`, `PF_PAYABLE`, `SOCIAL_SECURITY_PAYABLE` | employee + employer parts | — |
| Cr | `EMPLOYEE_LOAN_RECEIVABLE` / `EMPLOYEE_ADVANCE` | Σ recoveries (reduces asset) | — |
| Cr | `OTHER_DEDUCTION_PAYABLE` | Σ other deductions | — |
| Cr | `SALARY_PAYABLE` | Σ net pay | — |

Check: Σ Dr = Σ Cr to the cent per run (BR-PO-001). Rounding difference → `DEFAULT_ROUNDING`.

**Payment (Paid):** Dr `SALARY_PAYABLE` / Cr `DEFAULT_BANK` (or `DEFAULT_CASH`) for the amount actually paid; employees not paid (blocked, returned) stay in payable.
**Loan disbursement:** Dr `EMPLOYEE_LOAN_RECEIVABLE` / Cr `DEFAULT_BANK`.
**Expense claim approved:** Dr expense account(s) (+ input tax per tax code if recoverable) / Cr `EXPENSE_CLAIM_PAYABLE`; paid with payroll → the payable is cleared inside the payroll payment, otherwise `ExpensePayment` Dr payable / Cr bank. CompanyAccount claims: Dr expense / Cr bank-or-card clearing, no payable.
**Remittance:** Dr tax/EOBI/PF payable / Cr bank.
**Final settlement:** same shape as a payroll run line set plus leave-encashment/gratuity roles.

### 7.5 QuickBooks mapping (JournalEntry)

| Gateway field | QBO `JournalEntry` | Notes |
|---|---|---|
| TransactionDate | `TxnDate` | Must fall in an open QBO period if a close date is set. |
| SourceDocumentNumber | `DocNumber` | e.g. `PAY-2026-10`. |
| Description | `PrivateNote` | Run number, period, headcount. |
| Currency / rate | `CurrencyRef` / `ExchangeRate` | Only if the QBO company is multi-currency; payroll is org base currency (OD-12). |
| Line Debit / Credit | `Line[].DetailType = JournalEntryLineDetail`, `PostingType = Debit/Credit`, `Amount` | |
| Line account | `AccountRef` = QBO account id from **A42 `ExternalSystemMappings`** | **Mapping is mandatory for JE** (G-3). |
| Line CostCenter | `ClassRef` / `DepartmentRef` | Needs a per-cost-center map (G-5). |
| Line Label | `Description` | |
| Idempotency | `IdempotencyKey` → `integration.EntityMaps` (doc ↔ QBO id) + fingerprint | Reuse the existing outbox, command ledger, dead-letter and retry policy (A44 D-11). |
| Void / reverse | post a **reversing JE** | Avoids deleting accounting history in QBO (G-12). |
| Limits | QBO JE line cap (**UNVERIFIED**, historically 1 000 lines) | Summarized posting keeps well below it; split by department group if needed (G-16). |

Sync control: add document types `PayrollRun`, `PayrollPayment`, `LoanDisbursement`, `Expense`, `StatutoryRemittance` to `integration.SyncControlConfigs` (A44 D-14); **default OFF + hold** for new connections so no payroll reaches QuickBooks until an accountant enables it.

### 7.6 Mapping tables the posting needs (all per tenant)

| Mapping | Exists today? | Needed for HRM |
|---|---|---|
| CoA account → QBO account (`finance.ExternalSystemMappings`, A42; import + auto-map by AcctNum/name exists, D-16) | Yes, optional for documents | **Mandatory** for every payroll account; coverage check (G-3) |
| Account role → CoA account (A42 defaults) | 16 keys | + HR roles (G-4) |
| Department / cost center → QBO Class / Location | Only a single default `ClassRef`/`DepartmentRef` (`ProviderDefaults`) | Per-cost-center table (G-5) |
| Employee → QBO Employee/Vendor | No (`PartnerType` = Customer/Vendor only; entity sync kinds Customer/Vendor/Item) | Not required for v1 (JE lines carry no entity); optional later (G-6) |
| Expense category → expense account | No | New table `hrm.ExpenseCategories.AccountCode` |

### 7.7 Failure, retry and period rules

- Source commit is independent of posting (A44 D-9). Posting rows are recorded in `finance.FinancialPostings` (per-target status) and retried by the existing jobs; HRM shows the run as **Finalized — posting pending/failed** with the reason and a **Retry** button (same pattern as A44 D-33).
- Closed GL period or unmapped role → internal GL target fails with a clear code; QuickBooks proceeds independently. Payroll run cannot move to **Posted** unless at least one *active* target reports Posted (or none is active, P3).
- Reposting with a changed payload (e.g. a run reversed and re-run) is a new run with a new key, never an edit.
- Posting-role readiness check before **Finalize** (same seam as A48 `IPostingRoleReadiness`): missing HR roles block with the list of missing keys.

### 7.8 Reconciliation expectations

Internal GL and QuickBooks must show the same net salary expense and payables per period. A reconciliation view (payroll run totals vs posted journal totals per target) is part of the HR reports; the SMS-wide reconciliation engine is still deferred (A44 D-28), so this is a simple per-run comparison, not a bank reconciliation.

---

## 8. Integration Scope Matrix

✔ available today · ◐ exists but needs work for HRM · ✖ missing · — not applicable.

| Capability | P1 Full | P2 HR+Finance+QBO | P3 HR only | Status / evidence |
|---|---|---|---|---|
| Tenant/org model, auth, roles | ✔ | ✔ | ✔ | Core |
| Module registry gating for `MODULE_HRM` | ◐ | ◐ | ◐ | A37 supports new modules; HRM entries to add |
| Employee ↔ SMS user directory (`IOrgUserDirectory`) | ✔ | ✔ | ✔ | exists |
| Workflow engine approvals | ✔ | ✔ | ✔ | `SMS.WorkflowEngine` |
| FileStore, Notifications, Hangfire + `HangfireTenantScope` | ✔ | ✔ | ✔ | exist |
| PDF via `PdfRenderGate` | ✔ | ✔ | ✔ | exists |
| A40 import (Employee profile) | ◐ | ◐ | ◐ | profile exists, target must change |
| **Gateway: HRM source/doc types** | ✖ | ✖ | — | enum lacks them (G-2) |
| **Internal GL posting** (A42/A44/A47) | ✔ engine · ◐ HR roles | ◐ | — | 16 default keys only (G-4) |
| **QuickBooks connection / OAuth / outbox / dead-letter / sync control** | ✔ | ✔ | — | A44 live-tested |
| **QuickBooks JournalEntry push** | ✖ | ✖ | — | `journal_entry_push` deferred (A44 D-29); no JE code in Integration (G-1) |
| QuickBooks account mapping (import, auto-map) | ✔ | ◐ | — | exists but not mandatory today (G-3) |
| Cost center → Class/Location mapping | ◐ single default | ✖ | — | G-5 |
| Expense documents (`Expense` posting + QBO builder) | ✖ | ✖ | — | enum value only; no source, no builder (A44 D-28) (G-9) |
| Finance module without Sales/Procurement/Inventory | ✔ | ◐ | — | Finance project references Demand/Warehouse/Lookups; never run with them off (G-8) |
| Business partners (customers/vendors) | ✔ (Suppliers module) | ◐ | — | Suppliers references Finance/Demand/Warehouse; needed only if the client also keeps vendors in SMS |
| AR/AP sub-ledgers (A48) | ✔ | ◐ | — | source documents come from Sales/Procurement; a P2 client without them has nothing to age |
| Bank accounts / bank reconciliation | ◐ | ✖ | — | no bank-statement import or reconciliation engine (A44 D-28) |
| HR-only operation (no target) | — | — | ◐ | gateway returns `NoTargetConfigured`; HRM must treat it as normal (G-19) |

---

## 9. Gap Analysis

Severity: **B** = blocks the P2 client · **H** high (must fix before production) · **M** medium · **L** low. "Verified" = confirmed in code/docs read for this addendum.

| ID | Gap | Sev | Evidence | Remedy | Phase |
|---|---|---|---|---|---|
| **G-1** | **No QuickBooks JournalEntry push.** Payroll/expense cannot reach QuickBooks. | **B** | A44 D-28/D-29 defer `journal_entry_push`; `SMS.Modules.Integration` contains no JournalEntry code (verified) | Add `JournalEntry` builder + sync kind to the QuickBooks target; reuse outbox/fingerprint/dead-letter; sandbox test on Intuit | H5P (new) |
| **G-2** | Gateway has no HRM document types or source module | **B** | `FinDocType` (20 values) and `PostingSourceModules` read in `FinancialPostingContracts.cs` (verified) | Add `PayrollRun, PayrollPayment, LoanDisbursement, ExpensePayment, StatutoryRemittance, FinalSettlement, PayrollProvision`; `Hrm` source; A47 resolver mapping (D-12) | H5P |
| **G-3** | QuickBooks account mapping is optional for document push but **mandatory** for JE | **B** | A44 D-16 "GL account mappings are not required for document push" (verified) | Payroll coverage check: every HR role must resolve to a CoA account **with** a QBO mapping before Finalize; surface in QuickBooks › Coverage | H5P |
| **G-4** | A42 has 16 default account keys; none for payroll | **H** | `GlCodes.cs` (verified) | Add the HR keys (§7.3), seeded unmapped; make "GL setup complete" aware of HR when payroll enabled | H1/H5P |
| **G-5** | No cost-center / department → QBO Class/Location mapping (only a single default) | H | A44 D-15 `ClassRef`/`DepartmentRef` single values (verified) | New `integration` mapping table + UI tab; fall back to the default when unmapped | H5P |
| **G-6** | Employees are not partners; QBO JE lines cannot reference employees | M | `PartnerType` = Customer\|Vendor (verified) | v1: no entity on JE lines (name in memo, no per-employee data). Later: Employee kind in entity sync | Later |
| **G-7** | Salary payment has no bank-file → posted-payment path in Finance | H | Payments engine is customer/supplier documents | HRM-owned `PayrollPayment` via gateway (§7.2), bank CSV generator (base FSD C29) | H5 |
| **G-8** | "HR + Finance only" profile never exercised: Finance references Demand/Warehouse/Lookups; menus, dependency graph and always-on Finance assume sales/purchase data | **H** | csproj references (verified); A37 D-3 dependencies (verified); behaviour with Demand/Inventory/Suppliers off **UNVERIFIED** | Define profile P2 in registry; QA with those modules disabled: Finance menus, reports, GL, QuickBooks pages, startup migrations; hide AR/AP pages with no data | H1 |
| **G-9** | No expense capability for non-payroll employee expenses; `Expense` doc type has no source or QBO builder | **H** | A44 D-28 "No expense module" (verified) | Build HRM Expense Claims (§5.12) + post as `Expense` JE (v1); `Purchase`/`Bill` mapping later | H6E (new) |
| **G-10** | Closed fiscal period blocks internal GL posting | M | A42 `IFiscalPeriodGuard` | Payroll "post date" policy: run date vs pay date; clear failure message; retry after reopening | H5P |
| **G-11** | Privacy: per-employee salaries must not leave SMS | H | design | Summarize by cost center; keep detail in HRM; no employee names in QBO notes | H5P |
| **G-12** | QBO JE void/delete vs history | M | A44 D-26 void semantics are for documents | Reverse with an opposite JE; never delete | H5P |
| **G-13** | Statutory remittances (tax/EOBI/PF to authorities) have no payment path | M | no such doc type | `StatutoryRemittance` transaction + HR screen; vendor-less | H6E |
| **G-14** | Currency: QBO home currency must equal org base currency for payroll | M | OD-12 | Check on connect/first payroll post; refuse otherwise | H5P |
| **G-15** | Existing QuickBooks connections: new doc types must start OFF/held | M | A44 D-14 seeding rules (verified) | Migration seeds `SyncControlConfigs` rows disabled + hold | H5P |
| **G-16** | JE line count / size limits and rate limiting | M | QBO limits **UNVERIFIED** | Summarize; split by department group; use existing throttle/backoff | H5P |
| **G-17** | One org = one employer; no multi-legal-entity groups (SAP company codes, Odoo multi-company) | M | tenant model | Document as limitation; groups use separate orgs; revisit only on demand | Decision |
| **G-18** | Bank reconciliation absent, so "Paid" is a manual confirmation | M | A44 D-28 | Manual mark-paid with bank reference; reconciliation deferred | Later |
| **G-19** | HR-only/no-target state not defined; payroll "Posted" status can never be reached | H | gateway returns `NoTargetConfigured` (verified) | Run lifecycle: Posted optional; Paid allowed from Finalized when no active target | H5 |
| **G-20** | **Business risk:** QuickBooks Online availability in Pakistan | **H** | **UNVERIFIED** — to my knowledge QBO has no Pakistan edition | Confirm the client's QBO edition/country before committing; otherwise internal GL + export, or a different accounting target | Pre-sales |
| **G-21** | Pakistan tax / EOBI / PF / gratuity values not provided | B for payroll | base FSD OD-4 | Qualified review; Unverified flag blocks finalize | H5 |
| **G-22** | Role seeding: Finance Officer/Auditor mappings for HR codes | L | A48 D-CORE-7 pattern | Add grants in the idempotent seeder | H1 |

**Summary for P2:** the client cannot be live until **G-1, G-2, G-3** (QuickBooks journal path), **G-4, G-8, G-9** and **G-19** are closed, and G-20/G-21 are answered. Everything else is hardening.

---

## 10. Additions to the Task Register

New sections to insert into Task Register 49 (not yet added to that file):

| Section | Phase | Tasks |
|---|---|---|
| **H5P — Payroll posting & QuickBooks journal path** | after H5F | Add `FinDocType`/source values; HR account-role keys + seeder + readiness check; payroll posting builder; QuickBooks `JournalEntry` builder, sync kind and sync-control rows (default off/hold); cost-center → Class/Location map + UI tab; mandatory-account coverage check; reversing-JE logic; period policy; currency check; sandbox E2E (internal GL ≡ QuickBooks totals); dead-letter/retry UI on the run page |
| **H6E — Expense claims & remittances** | after H6 | Entities (§5.12), categories → accounts, workflow EXPENSE_CLAIM, payroll-input settlement, `Expense` / `ExpensePayment` / `StatutoryRemittance` postings, screens, tests |
| **H1P — Profile P2/P3 readiness** | within H1 | Registry profile definitions, menu/route hiding, startup with Demand/Inventory/Suppliers off, no-target lifecycle, QA of Finance pages in P2 |
| **DOC** | | Mapping workbook (Odoo/SAP) as a CSV/XLSX; client migration guides |

Estimated added tasks: ≈ 90–110 (BE ≈ 35, DB ≈ 10, FE ≈ 12, QA ≈ 40, DOC ≈ 5).

---

## 11. Decisions Required

| ID | Decision | Recommended default |
|---|---|---|
| FD-1 | Is the P2 client's QuickBooks edition available for their country? | Confirm before any build (G-20). |
| FD-2 | Payroll posting granularity | One summarized JE per run by cost center (no per-employee lines). |
| FD-3 | QuickBooks JE only, or also native Bill for expenses? | JE only in v1. |
| FD-4 | Internal GL in P2: on in parallel with QuickBooks, or QuickBooks only? | Client choice; both supported through the gateway. |
| FD-5 | Expense claims in scope for this client? | Yes (user asked for "other expense"); settle with payroll by default. |
| FD-6 | Employer entity per org only (G-17) | Accept. |
| FD-7 | Accounting period for payroll posting | Run's pay date; fall back to period end if pay date is in a closed period (with warning). |
| FD-9 | Payroll cost center | Mandatory at module enablement; select or create inline; Central or By-department mode (see §11A.2). |
| FD-10 | Pakistan accounting target | Internal GL; pluggable provider later (see §11A.6). |
| FD-8 | Migration source: Odoo and/or SAP? | Provide mapping CSVs for both; ship Odoo CSV import first (flat files), SAP via flat-file extract. |

---

## 11A. Revision 2 — Client Directions (supersedes conflicting text above)

Source: client review of the gap list. Where this section conflicts with §7–§11, **this section wins**.

### 11A.1 Directions received and how they are met

| # | Direction | Resolution | Gap / Decision |
|---|---|---|---|
| 1 | Check the QuickBooks code and enable payroll posting in SMS | Code check done: `SMS.Modules.Integration` has no JournalEntry code. Build a QuickBooks `JournalEntry` push (summarized journal; **not** QuickBooks' own Payroll product) as part of H5P. Starts OFF/held on existing connections. | G-1 |
| 2 | HR source module is defined in the addendum; middleware must be enabled for HR | Add source module `Hrm` and the HR document types to the gateway, with A47 resolver mappings. | G-2 |
| 3 | QuickBooks mapping must be done | Payroll Finalize is blocked until every HR account role resolves to a CoA account **with** a QuickBooks mapping, and every used cost center has a Class mapping (when QuickBooks is an active target). | G-3, G-5 |
| 4 | Payroll account roles should be added | Add the HR role keys of §7.3, seeded unmapped; "GL setup complete" becomes HR-aware when payroll is enabled. | G-4 |
| 5 | Check how Odoo and SAP handle expenses | See §11A.4. | G-9, FD-5 |
| 6 | Client may need only payroll, personnel and other operating expenses; income from **services**, not sales | New **Profile P2-S** (§11A.5): HR + Finance, no Sales/Procurement/Inventory; income via manual service invoices in Finance AR (A48). | G-8, FD-9 |
| 7 | Pakistan operations may use the SMS financial module, not QuickBooks; or another system may be integrated | New **Profile P-PK** (§11A.6): internal GL is the book of record; pluggable accounting provider. | G-20, FD-10 |

### 11A.2 Payroll cost center — tenant setting (FD-9, replaces FD-2 granularity where it conflicts)

**Mandatory at module enablement.** When an administrator enables the HRM module for a tenant, the enablement wizard cannot complete until a **Payroll cost center** is chosen. The step offers:

1. **Select an existing cost center** (e.g. *Head Office*), or
2. **Create a new cost center inline** (e.g. *Salaries*) — name, code, optional parent and branch — and attach it in the same step. The new cost center is a normal organization cost center afterwards (visible and editable under the organization structure).

Validation: the cost center must be active and belong to the same organization. It cannot be deactivated while it is the payroll cost center. If the tenant already has no cost centers, the wizard goes straight to "create new".

**Posting mode** (also set in the wizard; changeable later, applies only to **future** runs — posted runs are never re-classified):

| Mode | Behaviour |
|---|---|
| **Central** (default) | All payroll expense lines post to the Payroll cost center. |
| **By department** | Each employee's cost center comes from their department; optional per-employee override on the employment record and optional percentage split (Odoo analytic distribution / SAP PA0027 equivalent). Summarized per cost center. |

Rules in both modes:
- Employer-side costs (EOBI, PF contribution, gratuity provision) follow the cost center of the salary they relate to.
- Liability lines (salary payable, tax payable, EOBI payable, PF payable) post once to the Payroll cost center and are not split.
- QuickBooks: each cost center used maps to a QuickBooks Class (and optionally Location). An unmapped cost center **blocks Finalize** with a message naming it; it never posts unclassified. Mapping table: `cost center → QBO Class/Location` (tab under QuickBooks mappings).
- Data model: `HrmSettings.PayrollCostCenterId` (required, FK), `PayrollCostCenterMode` (`CENTRAL`|`BY_DEPARTMENT`). Module enable API rejects without it (HTTP 422 `PAYROLL_COST_CENTER_REQUIRED`). Existing tenants being enabled by script/migration are blocked the same way.
- Permissions: setting the cost center needs `HRM_SETTINGS_MANAGE`; creating one inline additionally needs the existing organization-structure manage permission.

### 11A.3 No-target state (explained; G-19 resolution)

The gateway posts a payroll journal to *targets*: the internal GL, QuickBooks, or both. A tenant using HR alone has neither, so a run could never reach "Posted". Resolution: payroll run status gains **Finalized — no posting required** when no target is active. Such tenants get a journal **export** (CSV/XLSX: account role, cost center, debit, credit) for their external accountant. Under P2/P2-S/P-PK at least one target is always active, so the normal Posted state applies.

### 11A.4 Expense claims — Odoo and SAP comparison (from memory; verify)

| Aspect | Odoo (Expenses) | SAP (Travel Management / Concur) | SMS design |
|---|---|---|---|
| Categories | Expense products with expense account + tax | Expense types mapped to GL accounts | `ExpenseCategory` → account role / CoA account |
| Entry | Employee submits expense report with receipts | Trip / expense report with receipts | `ExpenseClaim` + lines + attachments (FileStore) |
| Approval | Manager, then accounting | Approval workflow | WorkflowEngine `EXPENSE_CLAIM` |
| Posting | Dr expense / Cr employee payable (or Cr company card if company-paid) | Transfer to FI: Dr expense / Cr employee as vendor | Gateway `Expense`: Dr expense by category and cost center / Cr Employee Payable |
| Reimbursement | Payment to employee, or added to payslip | Payment run (F110) | Pay separately (`ExpensePayment`) or settle through the next payroll run |
| Extras | Per-diem, mileage, re-invoice to customer | Advances, per-diem, mileage | v1: categories + receipts + per-diem/mileage as rate-based categories; advances later |

### 11A.5 Profile P2-S — HR + Finance, service income (G-8 refined)

- Modules: HRM, Finance (GL, AR/AP sub-ledgers), Integration/QuickBooks (optional), Accounting. Sales, Procurement, Inventory, Warehouse, Manufacturing, POS **off**.
- Income: manual **service invoices** raised in Finance AR (A48) against customers, with income accounts; no sales order is needed. Customer receipts through the existing payments engine.
- Operating expenses: HRM expense claims, plus (if needed) supplier bills through Finance AP.
- **Required deliverable:** a tested profile — Finance and QuickBooks pages, startup migrations, menus, dependency graph and reports must work with the above modules disabled (previously only a gap, G-8). Acceptance: a tenant created in P2-S can raise a service invoice, receive payment, run payroll, post an expense claim and see a balanced trial balance and P&L, with all selling/stock menus hidden.
- Open check: whether AR service invoicing exists without a Sales document source (UNVERIFIED; confirm against A48 code before H1P).

### 11A.6 Profile P-PK — Pakistan operations (G-20, G-21 refined; FD-10)

- QuickBooks is **not** assumed for Pakistan. The internal GL is the book of record; the payroll journal posts through the gateway to the internal target.
- The gateway is provider-neutral, so a different accounting system can be added later as a second provider adapter (candidate system to be chosen by the client — not named here, availability and API unverified). The adapter would reuse the outbox, idempotency key and mapping tables; the HR side does not change.
- Pakistan statutory rules remain effective-dated data flagged Unverified and still block Finalize until reviewed (G-21).
- QuickBooks and Pakistan paths are separate deliverables; neither blocks the other.

### 11A.7 Updated priorities for the P2 / P2-S client

Blockers: G-1, G-2, G-3, G-19 (resolved by §11A.3). High, before production: G-4, G-5 (now with the §11A.2 block rule), G-8 (now the P2-S acceptance test), G-9. Unchanged business risks: G-20, G-21.

### 11A.8 Additional tasks (≈ 20–25, on top of §10)

- H1P: cost-center step in module-enable wizard (select / create inline), `HrmSettings` columns + guarded migration, enable-API validation, mode switch, tests for the 422 path and for inline creation.
- H5P: Central / By-department splitter, liability-line rule, cost center → Class mapping tab and Finalize block, no-target export and status.
- H1P/QA: P2-S acceptance scenario (service invoice → receipt → payroll → expense → TB/P&L).
- H5P: provider-adapter seam review for P-PK (design note only; no second adapter built).

Revised estimate of added tasks: ≈ 110–135.

---

## 12. Appendices

### Appendix A — Odoo-flavoured import column map (Employee)

| Odoo export column | SMS field | Transform |
|---|---|---|
| `name` | FirstName, LastName | split on first space; review |
| `identification_id` | Cnic | strip dashes; validate 13 digits |
| `birthday` | DateOfBirth | ISO date |
| `gender` | Gender | male→M, female→F, other→Other |
| `marital` | MaritalStatus | single/married/cohabitant/widower/divorced |
| `work_email`, `private_email` | WorkEmail, PersonalEmail | |
| `mobile_phone` | Mobile | |
| `department_id` | DepartmentId | match by code or name |
| `job_id` | DesignationId | match by name |
| `parent_id` | ManagerEmployeeId | by employee number |
| `work_location_id` | BranchId | |
| `contract.date_start`, `date_end`, `wage` | JoinDate, ContractEndDate, SalaryAssignment | wage → BASIC component (needs structure choice) |
| `bank_account_id` | EmployeeBankAccount | |

### Appendix B — SAP-flavoured import column map (flat extract per infotype)

| Infotype/field | SMS field |
|---|---|
| PA0001-PERNR | EmployeeNumber |
| PA0002-VORNA / NACHN / GBDAT / GESCH / FAMST / NATIO | FirstName / LastName / DateOfBirth / Gender / MaritalStatus / Nationality |
| PA0185-ICNUM (ICTYP = national ID) | Cnic |
| PA0001-ORGEH / STELL / WERKS+BTRTL / KOSTL / PERSK | DepartmentId / DesignationId / BranchId / CostCenterCode / GradeId |
| PA0008 wage types (LGA01/BET01) | SalaryAssignment component amounts |
| PA0009-BANKL / BANKN / IBAN | EmployeeBankAccount |
| PA0006 | Addresses |
| PA0021 (emergency) | EmergencyContacts |
| PA2006 (quotas) | Opening Leave Balance import |
| PA0045 | Loans (opening) |

### Appendix C — Posting payload example (payroll run)

```json
{
  "documentType": "PayrollRun",
  "sourceModule": "hrm",
  "sourceDocumentId": "<run uuid>",
  "sourceDocumentNumber": "PAY-2026-10",
  "transactionDate": "2026-10-31",
  "currencyCode": "PKR",
  "description": "Payroll October 2026 — 142 employees",
  "lines": [
    {"lineNumber":1,"accountRole":"DEFAULT_SALARY_EXPENSE","debit":4250000.00,"costCenter":"OPS"},
    {"lineNumber":2,"accountRole":"DEFAULT_SALARY_EXPENSE","debit":1800000.00,"costCenter":"SALES"},
    {"lineNumber":3,"accountRole":"DEFAULT_EMPLOYER_CONTRIB_EXPENSE","debit":120000.00,"costCenter":"OPS"},
    {"lineNumber":4,"accountRole":"DEFAULT_EMPLOYEE_TAX_PAYABLE","credit":410000.00},
    {"lineNumber":5,"accountRole":"DEFAULT_EOBI_PAYABLE","credit":65000.00},
    {"lineNumber":6,"accountRole":"DEFAULT_SALARY_PAYABLE","credit":5695000.00}
  ]
}
```
*(Illustrative figures only — not tax or statutory values.)*

### Appendix D — Acceptance criteria for the posting design

1. A finalized run posts exactly one journal per active target; retry never duplicates (key `hrm:PayrollRun:{uuid}`).
2. Σ Dr = Σ Cr; internal GL and QuickBooks totals per account agree for the same run.
3. A missing HR account role or QuickBooks account mapping blocks Finalize with the full list of missing items.
4. Disabling QuickBooks or the GL does not stop payroll; the run shows which targets are pending.
5. No per-employee salary or personal identifier appears in any QuickBooks payload, log or `PrivateNote`.
6. Reversal produces an opposite journal at every target; the original is never deleted.
7. Profile P3 (no target) finalizes and pays without errors.
