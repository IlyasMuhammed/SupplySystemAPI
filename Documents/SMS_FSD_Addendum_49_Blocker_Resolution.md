# SMS — Functional Specification Document

## Addendum 49 — Supplement: Blocker & Gap Resolution Specifications

| Property | Value |
|---|---|
| Document ID | SMS-FSD-ADD-049-S |
| Version | 1.1 |
| Date | 2026-10-31 |
| Status | Draft |
| Companion to | SMS-FSD-ADD-049-D (HRM Detailed), SMS-FSD-ADD-049 (HRM Base) |
| Purpose | Detailed implementation specs for all gaps (G-1 through G-22) identified in A49 §9 |
| Design Heritage | **Odoo approach chosen** — all HRM areas follow Odoo's model (see §0) |

> **Reading order:** Read A49-D first (the main detailed addendum). This supplement provides the
> entity DDL, service interfaces, migrations, business rules, and test scenarios needed to
> **clear every blocker and high-severity gap** so HRM can go live.

> **Design decision (2026-10-31):** Where A49-D presents both Odoo and SAP approaches,
> **the Odoo approach is chosen** for all HRM areas. SAP mappings remain in A49-D for
> migration reference only — they are not the design target.

---

## Table of Contents

0. [Design Decision: Odoo Approach](#0-design-decision-odoo-approach)
1. [Resolution Overview](#1-resolution-overview)
2. [G-1: QuickBooks JournalEntry Builder](#2-g-1-quickbooks-journalentry-builder)
3. [G-2: Gateway HRM Document Types](#3-g-2-gateway-hrm-document-types)
4. [G-3: Mandatory Account Mapping at Finalize](#4-g-3-mandatory-account-mapping-at-finalize)
5. [G-4: HR Default Account Role Keys](#5-g-4-hr-default-account-role-keys)
6. [G-5: Cost Center → QBO Class/Location Mapping](#6-g-5-cost-center--qbo-classlocation-mapping)
7. [G-8: Finance Standalone Profile (P2 Readiness)](#7-g-8-finance-standalone-profile-p2-readiness)
8. [G-9: Expense Claim Posting & QBO Builder](#8-g-9-expense-claim-posting--qbo-builder)
9. [G-10: Fiscal Period vs Pay-Date Policy](#9-g-10-fiscal-period-vs-pay-date-policy)
10. [G-11: Privacy — No Per-Employee Data in QBO](#10-g-11-privacy--no-per-employee-data-in-qbo)
11. [G-12: QBO Void/Reversal Semantics](#11-g-12-qbo-voidreversal-semantics)
12. [G-13: Statutory Remittance Payment Path](#12-g-13-statutory-remittance-payment-path)
13. [G-14: QBO Currency Validation](#13-g-14-qbo-currency-validation)
14. [G-15: SyncControlConfigs Seeding](#14-g-15-synccontrolconfigs-seeding)
15. [G-16: QBO JE Line Limits & Splitting](#15-g-16-qbo-je-line-limits--splitting)
16. [G-17: One Org = One Employer](#16-g-17-one-org--one-employer)
17. [G-18: Bank Reconciliation (Deferred)](#17-g-18-bank-reconciliation-deferred)
18. [G-19: No-Target Lifecycle State](#18-g-19-no-target-lifecycle-state)
19. [G-20/G-21: Pakistan QuickBooks & Statutory](#19-g-20g-21-pakistan-quickbooks--statutory)
20. [G-22: Role Seeding for HR](#20-g-22-role-seeding-for-hr)
21. [Database Migrations](#21-database-migrations)
22. [API Endpoints](#22-api-endpoints)
23. [Business Rules](#23-business-rules)
24. [Test Scenarios](#24-test-scenarios)
25. [Phase Mapping & Effort](#25-phase-mapping--effort)

---

## 0. Design Decision: Odoo Approach

**Decision date:** 2026-10-31
**Decision:** All HRM areas follow the Odoo model. SAP field/infotype mappings in A49-D are retained for data migration reference only — they do not drive SMS design.

### 0.1 Decision Matrix

| # | HRM Area | Odoo Model (CHOSEN) | SAP Model (NOT chosen) | SMS Implementation |
|---|---|---|---|---|
| DD-1 | Employee & Contract | `hr.employee` + `hr.contract` — single master, dated contracts | 30+ infotypes (PA0001-PA0021) with validity periods | `Employees` + `EmploymentRecords` (effective-dated, one active). Single place to look, contract-style |
| DD-2 | Salary Structure | `hr.payroll.structure` + salary rules with Python expressions | Wage-type model with processing classes and schemas | `SalaryStructure` + `SalaryComponents`. Calc method: Fixed/Percent/Formula. Safe expression grammar, no code execution |
| DD-3 | Leave Management | `hr.leave` + `hr.leave.allocation` with accrual plans | PA2006 Absence Quotas + T554S absence types + quota deduction rules | `LeaveType` + `LeaveLedgerEntries` (accrual-style). Balance = quota - consumed. Carry-forward = new ledger entry |
| DD-4 | Attendance | `hr.attendance` — check_in / check_out timestamps | Time events PA2011 + time evaluation schemas | `AttendanceEvent` (raw clock) → `AttendanceDay` (computed: hours, OT, late). OT approved → payroll |
| DD-5 | Payroll Processing | Create → Compute → Confirm → Draft Entry → Post | Simulate → Release → Exit → Bank transfer → FI posting | Create → Calculate → Review → Approve → Finalize → Post → Pay. Correction = reverse + new run |
| DD-6 | Expense Claims | `hr.expense` + `hr.expense.sheet` with manager approval | Travel Management / Concur with multi-level approval | `ExpenseClaim` + `ExpenseLines` + receipts. WorkflowEngine approval. Settle via payroll or separate payment |
| DD-7 | Recruitment | `hr.applicant` with Kanban stages | PB applicant data with PA40 hiring actions | `Vacancy` → `Candidate/Application` (stages) → Interview → Offer → Convert to Employee. Kanban pipeline |
| DD-8 | Performance | `hr.appraisal` + goals | Appraisal catalog / SuccessFactors | `ReviewCycle` → `Review` → `Goal`. Annual/quarterly. Manager rates, employee acknowledges |

### 0.2 What This Means for Developers

1. **Follow Odoo's model names and flow** — when in doubt about how something should work, look at Odoo 16/17 HR modules, not SAP
2. **SAP columns in A49-D data dictionary** — keep for reference, useful when a client migrates FROM SAP. Do not build SMS to match SAP infotype structure
3. **Salary computation** — use expression-based rules (like Odoo `hr.salary.rule`), not SAP wage-type processing classes
4. **Payroll run lifecycle** — Odoo's linear flow (compute → confirm → post), not SAP's simulate/release/retro pattern
5. **Leave balance** — ledger-style running balance (like Odoo allocations), not SAP quota-deduction rules
6. **Recruitment** — stage-based pipeline (Kanban), not SAP's action-based PA40 hiring

### 0.3 What This Does NOT Change

| Unchanged Area | Reason |
|---|---|
| Financial posting via `IFinancialPostingGateway` (A44) | SMS's own architecture — not Odoo or SAP |
| GL journals via A47 engine | SMS's own architecture |
| QuickBooks sync via JournalEntry builder | SMS's own integration pattern |
| Multi-tenancy (`OrgId` discriminator) | SMS's own architecture |
| Modular monolith, schema-per-module | SMS's own architecture |
| Privacy rule — no per-employee data in QBO | SMS's own policy |
| EF Core Code-First migrations | SMS's own tech stack |

### 0.4 Odoo Reference Versions

| Module | Odoo Reference | Key Models |
|---|---|---|
| Employee | `hr` (Odoo 16/17) | `hr.employee`, `hr.department`, `hr.job` |
| Contracts | `hr_contract` | `hr.contract` |
| Payroll | `hr_payroll` (Community/Enterprise) | `hr.payslip`, `hr.payroll.structure`, `hr.salary.rule` |
| Leave | `hr_holidays` | `hr.leave`, `hr.leave.type`, `hr.leave.allocation` |
| Attendance | `hr_attendance` | `hr.attendance` |
| Expense | `hr_expense` | `hr.expense`, `hr.expense.sheet` |
| Recruitment | `hr_recruitment` | `hr.applicant`, `hr.job` (vacancy) |
| Appraisal | `hr_appraisal` (Enterprise) | `hr.appraisal`, `hr.appraisal.goal` |

---

## 1. Resolution Overview

### Severity & Phase Assignment

| Gap | Severity | Phase | Blocks |
|---|---|---|---|
| G-1 | 🔴 Blocker | H5P | P2 deployment |
| G-2 | 🔴 Blocker | H5P | All payroll posting |
| G-3 | 🔴 Blocker | H5P | Payroll finalize (QBO tenants) |
| G-4 | 🟠 High | H1 + H5P | Payroll posting |
| G-5 | 🟠 High | H5P | QBO posting accuracy |
| G-8 | 🟠 High | H1P | P2 profile |
| G-9 | 🟠 High | H6E | Expense claims |
| G-10 | 🟡 Medium | H5P | Period-end payroll |
| G-11 | 🟡 Medium | H5P | Data privacy |
| G-12 | 🟡 Medium | H5P | Payroll correction |
| G-13 | 🟡 Medium | H6E | Statutory payments |
| G-14 | 🟡 Medium | H5P | Multi-currency |
| G-15 | 🟡 Medium | H5P | New QBO connections |
| G-16 | 🟡 Medium | H5P | Large payrolls |
| G-17 | 🟡 Medium | H1P | Documentation/decision |
| G-18 | 🟡 Medium | Later | Deferred |
| G-19 | 🟠 High | H1P | P3 profile (HR-only) |
| G-20 | 🟠 High | Pre-sales | Decision |
| G-21 | 🔴 for PK | H5 | Pakistan payroll |
| G-22 | 🟢 Low | H1 | Role setup |

### Dependency Graph

```
G-4 (account role keys)
  └──→ G-3 (mandatory mapping check)
         └──→ G-1 (JournalEntry builder)
                └──→ G-5 (cost center mapping)
                       └──→ G-15 (sync control seeding)
                              └──→ G-16 (line splitting)

G-2 (FinDocType enum) ──→ G-1 (builder uses doc types)

G-19 (no-target state) ──→ G-8 (P2/P3 profile readiness)

G-9 (expense posting) ──→ G-2 + G-1

G-13 (statutory remittance) ──→ G-2 + G-1

G-10 (fiscal period) ──→ independent (A42 guard)

G-11 (privacy) ──→ G-1 (enforced in builder)

G-12 (void/reversal) ──→ G-1 (reversal JE in builder)

G-14 (currency) ──→ G-1 (validation in builder)
```

---

## 2. G-1: QuickBooks JournalEntry Builder

**Severity:** 🔴 Blocker | **Phase:** H5P | **Affected module:** `SMS.Modules.Integration`

### 2.1 Problem

`SMS.Modules.Integration` currently supports Bill and Invoice push to QuickBooks. It has no `JournalEntry` builder. Payroll, loan disbursements, expense claims, and statutory remittances all need to post as summarized JournalEntry objects because QuickBooks' native payroll objects are not writable via the API.

### 2.2 Solution: IQuickBooksJournalEntryBuilder

```csharp
namespace SMS.Modules.Integration.QuickBooks;

/// <summary>
/// Builds a QuickBooks JournalEntry from a gateway FinancialTransaction.
/// Reuses the existing outbox, fingerprint, dead-letter, and retry 
/// infrastructure from A44.
/// </summary>
public interface IQuickBooksJournalEntryBuilder
{
    /// <summary>
    /// Build a QBO JournalEntry from a FinancialTransaction payload.
    /// Returns the serialized QBO object ready for the outbox.
    /// </summary>
    Task<QboJournalEntryPayload> BuildAsync(
        Guid orgId,
        FinancialTransaction transaction,
        ExternalSystemMappings mappings);

    /// <summary>
    /// Build a reversing JournalEntry for a previously posted transaction.
    /// Used for payroll correction (void original + post new).
    /// </summary>
    Task<QboJournalEntryPayload> BuildReversalAsync(
        Guid orgId,
        FinancialTransaction originalTransaction,
        FinancialTransaction reversalTransaction,
        ExternalSystemMappings mappings);
}
```

### 2.3 QboJournalEntryPayload Record

```csharp
public record QboJournalEntryPayload
{
    public string TxnDate { get; init; }           // YYYY-MM-DD
    public string DocNumber { get; init; }          // e.g. "PAY-2026-10"
    public string PrivateNote { get; init; }        // Run number, period, headcount
    public string CurrencyRef { get; init; }        // ISO code
    public decimal? ExchangeRate { get; init; }     // Only if multi-currency
    public List<QboJournalEntryLine> Lines { get; init; }
}

public record QboJournalEntryLine
{
    public string DetailType { get; init; }         // Always "JournalEntryLineDetail"
    public string PostingType { get; init; }        // "Debit" or "Credit"
    public decimal Amount { get; init; }
    public string AccountRef { get; init; }         // QBO account id from mapping
    public string ClassRef { get; init; }           // QBO Class from cost center map
    public string DepartmentRef { get; init; }      // QBO Location (optional)
    public string Description { get; init; }        // Cost center name, NEVER employee
}
```

### 2.4 Builder Logic (Pseudocode)

```
BuildAsync(orgId, transaction, mappings):
  1. Validate: every line's AccountRole resolves to a CoA account
  2. Validate: every CoA account has a QBO mapping (G-3)
  3. For each line in transaction.Lines:
     a. Resolve AccountRole → CoA AccountId → QBO AccountRef
     b. Resolve CostCenter → QBO ClassRef (G-5 mapping table)
     c. Resolve CostCenter → QBO DepartmentRef (optional, G-5)
     d. Set Description = cost center name (NEVER employee name, G-11)
     e. Set PostingType = "Debit" if line.Debit > 0, else "Credit"
     f. Set Amount = abs(line.Debit or line.Credit)
  4. Validate: Σ Debits == Σ Credits (balanced)
  5. Check line count (G-16):
     IF Lines.Count > MAX_QBO_JE_LINES (1000):
       Split by PayrollGroup/DepartmentGroup into multiple JEs
       Return multiple payloads
  6. Set TxnDate = transaction.TransactionDate
  7. Set DocNumber = transaction.SourceDocumentNumber (e.g. "PAY-2026-10")
  8. Set PrivateNote = "{RunNumber}, {Period}, {Headcount} employees"
     NEVER include employee names or per-employee amounts (G-11)
  9. Compute IdempotencyKey = "hrm:{DocType}:{SourceDocUuid}"
     → maps to integration.EntityMaps for retry (A44 D-11)
  10. Return QboJournalEntryPayload
```

### 2.5 Integration with Existing Outbox

The builder plugs into the **existing** A44 outbox pipeline. No new queue or retry infrastructure:

```
Existing A44 pipeline (no changes needed):
  FinancialPostingGateway.PostAsync()
    → FinancialPostings table (per-target status)
    → Outbox message (Hangfire job)
      → QuickBooksTarget.PushAsync()
        → NEW: if DocType is HRM type → use IQuickBooksJournalEntryBuilder
        → EXISTING: if DocType is Invoice/Bill → use existing builders
      → Dead-letter on failure
      → Retry with backoff
```

### 2.6 Registration

```csharp
// In DI registration (SMS.Modules.Integration startup)
services.AddScoped<IQuickBooksJournalEntryBuilder, 
                    QuickBooksJournalEntryBuilder>();

// In QuickBooksTarget routing
if (IsHrmDocType(transaction.DocumentType))
    return await _journalEntryBuilder.BuildAsync(orgId, transaction, mappings);
else
    return await _existingBillInvoiceBuilder.BuildAsync(...); // unchanged
```

---

## 3. G-2: Gateway HRM Document Types

**Severity:** 🔴 Blocker | **Phase:** H5P | **Affected module:** `SMS.Shared` (FinancialPostingContracts)

### 3.1 FinDocType Enum Extension

```csharp
// File: SMS.Shared/FinancialPostingContracts.cs
// ADD to existing enum — do NOT renumber existing values

public enum FinDocType
{
    // === Existing (A44) — unchanged ===
    // SalesInvoice = 1,
    // PurchaseInvoice = 2,
    // ... etc ...
    ManualJournal = 20,   // existing
    Expense = 21,         // existing (A44 D-28 reserved)
    
    // === A49 HRM additions ===
    PayrollRun = 50,           // Monthly payroll posting (summarized)
    PayrollPayment = 51,       // Salary bank transfer (Dr Payable / Cr Bank)
    LoanDisbursement = 52,     // Employee loan payout (Dr Receivable / Cr Bank)
    ExpensePayment = 53,       // Expense claim reimbursement
    StatutoryRemittance = 54,  // Tax/EOBI/PF payment to authority
    FinalSettlement = 55,      // End-of-service settlement
    PayrollProvision = 56      // Gratuity/leave encashment accrual
}
```

### 3.2 PostingSourceModules Extension

```csharp
// File: SMS.Shared/FinancialPostingContracts.cs

public static class PostingSourceModules
{
    // Existing
    public const string Finance = "finance";
    public const string Pos = "pos";
    public const string Suppliers = "suppliers";
    public const string Inventory = "inventory";
    
    // A49 addition
    public const string Hrm = "hrm";
}
```

### 3.3 Gateway Routing Extension

```csharp
// File: SMS.Modules.Finance/Services/FinancialPostingGateway.cs
// In the target-routing switch — ADD cases for HRM doc types

private JournalType MapToJournalType(FinDocType docType)
{
    return docType switch
    {
        // Existing mappings unchanged...
        
        // A49 HRM mappings:
        FinDocType.PayrollRun         => JournalType.MISC,  // or PAYR if added
        FinDocType.PayrollPayment     => JournalType.BANK,
        FinDocType.LoanDisbursement   => JournalType.BANK,
        FinDocType.ExpensePayment     => JournalType.BANK,
        FinDocType.StatutoryRemittance=> JournalType.BANK,
        FinDocType.FinalSettlement    => JournalType.MISC,
        FinDocType.PayrollProvision   => JournalType.MISC,
        
        _ => throw new ArgumentOutOfRangeException(nameof(docType))
    };
}

private string MapToQboTarget(FinDocType docType)
{
    return docType switch
    {
        // Existing: Bill, Invoice, etc.
        
        // ALL HRM types → JournalEntry (never Bill/Invoice)
        FinDocType.PayrollRun or
        FinDocType.PayrollPayment or
        FinDocType.LoanDisbursement or
        FinDocType.ExpensePayment or
        FinDocType.StatutoryRemittance or
        FinDocType.FinalSettlement or
        FinDocType.PayrollProvision
            => "JournalEntry",
        
        _ => throw new ArgumentOutOfRangeException(nameof(docType))
    };
}
```

### 3.4 HRM FinDocType Helper

```csharp
public static class FinDocTypeExtensions
{
    private static readonly HashSet<FinDocType> _hrmTypes = new()
    {
        FinDocType.PayrollRun,
        FinDocType.PayrollPayment,
        FinDocType.LoanDisbursement,
        FinDocType.ExpensePayment,
        FinDocType.StatutoryRemittance,
        FinDocType.FinalSettlement,
        FinDocType.PayrollProvision
    };

    public static bool IsHrmType(this FinDocType docType) 
        => _hrmTypes.Contains(docType);
    
    public static bool RequiresJournalEntryTarget(this FinDocType docType)
        => docType.IsHrmType();  // All HRM types go as JE
}
```

---

## 4. G-3: Mandatory Account Mapping at Finalize

**Severity:** 🔴 Blocker | **Phase:** H5P | **Affected module:** HRM Payroll Service

### 4.1 Problem

For Bill/Invoice push, QBO account mapping is optional (A44 D-16). For JournalEntry push, **every line must have a mapped QBO account** or the JE is rejected by Intuit's API.

### 4.2 Solution: IPayrollPostingReadinessService

```csharp
namespace SMS.Modules.Hrm.Services;

public interface IPayrollPostingReadinessService
{
    /// <summary>
    /// Check that all account roles used in a payroll run can resolve
    /// to GL accounts AND (if QBO is an active target) to QBO accounts.
    /// Called at Finalize step — blocks if any are unmapped.
    /// Same seam as A48 IPostingRoleReadiness.
    /// </summary>
    Task<PostingReadinessResult> CheckReadinessAsync(
        Guid orgId, Guid payrollRunId);
}

public record PostingReadinessResult
{
    public bool IsReady { get; init; }
    public bool InternalGLReady { get; init; }
    public bool QuickBooksReady { get; init; }
    public List<string> MissingGLMappings { get; init; }        // Role keys
    public List<string> MissingQBOMappings { get; init; }       // CoA account names
    public List<string> MissingCostCenterMappings { get; init; } // Cost center codes
}
```

### 4.3 Readiness Check Logic

```
CheckReadinessAsync(orgId, payrollRunId):
  1. Load all unique AccountRoles from payroll run lines
     (SALARY_EXPENSE, EMPLOYEE_TAX_PAYABLE, EOBI_PAYABLE, etc.)
  
  2. For each AccountRole:
     a. Resolve → A42 default account key → CoA AccountId
     b. IF unresolved → add to MissingGLMappings
  
  3. Determine active targets:
     internal_gl = FEATURE_GENERAL_LEDGER enabled + InternalAccountingEnabled
     quickbooks  = MODULE_INTEGRATION enabled + QuickBooks connected
  
  4. IF internal_gl active:
     a. All AccountRoles must resolve to CoA accounts
     b. InternalGLReady = (MissingGLMappings.Count == 0)
  
  5. IF quickbooks active:
     a. For each resolved CoA account:
        Check ExternalSystemMappings has a QBO AccountRef
        IF missing → add to MissingQBOMappings
     b. For each unique CostCenter in payroll run:
        Check CostCenterQboMappings has an entry (G-5)
        IF missing → add to MissingCostCenterMappings
     c. QuickBooksReady = (MissingQBOMappings.Count == 0 
                           AND MissingCostCenterMappings.Count == 0)
  
  6. IF no target active (G-19):
     IsReady = true  (Finalize is allowed; "Posted" is skipped)
  ELSE:
     IsReady = InternalGLReady AND QuickBooksReady
  
  7. Return PostingReadinessResult
```

### 4.4 Integration with Payroll Finalize

```csharp
// In PayrollRunService.FinalizeAsync():

public async Task<FinalizeResult> FinalizeAsync(
    Guid orgId, Guid runId, string finalizedBy)
{
    // Step 1: Validate run is in Approved status
    var run = await _repo.GetAsync(orgId, runId);
    if (run.Status != PayrollRunStatus.Approved)
        throw new InvalidOperationException("Run must be Approved to finalize");
    
    // Step 2: G-3 — Posting readiness check
    var readiness = await _readinessService.CheckReadinessAsync(orgId, runId);
    if (!readiness.IsReady)
    {
        return new FinalizeResult
        {
            Success = false,
            BlockedBy = "POSTING_READINESS",
            MissingGLMappings = readiness.MissingGLMappings,
            MissingQBOMappings = readiness.MissingQBOMappings,
            MissingCostCenterMappings = readiness.MissingCostCenterMappings,
            Message = "Cannot finalize: the following mappings are missing"
        };
    }
    
    // Step 3: Mark as Finalized
    run.Status = PayrollRunStatus.Finalized;
    run.FinalizedAt = DateTime.UtcNow;
    run.FinalizedBy = finalizedBy;
    
    // Step 4: Post to targets (if any active)
    if (readiness.InternalGLReady || readiness.QuickBooksReady)
    {
        var transaction = _mapper.BuildFinancialTransaction(run);
        var postResult = await _gateway.PostAsync(transaction);
        
        if (postResult.Status == PostingStatus.Success)
            run.Status = PayrollRunStatus.Posted;
        else if (postResult.Status == PostingStatus.NoTargetConfigured)
            // G-19: no target — stay at Finalized, this is OK
            run.PostingNote = "No accounting target configured";
        else
            run.PostingNote = $"Posting pending/failed: {postResult.Message}";
    }
    
    await _repo.UpdateAsync(run);
    return new FinalizeResult { Success = true, RunStatus = run.Status };
}
```

---

## 5. G-4: HR Default Account Role Keys

**Severity:** 🟠 High | **Phase:** H1 (seeding) + H5P (GL-setup-complete awareness)

### 5.1 New Account Role Keys

```sql
-- Migration: M_A49_001_SeedHRAccountRoleKeys
-- Adds 16 new default account role keys to A42's AccountRoleDefaults
-- All seeded UNMAPPED — tenant's accountant maps them during HR setup

INSERT INTO finance.AccountRoleDefaults 
    (OrgId, RoleKey, DisplayName, AccountId, IsRequired, ModuleSource)
SELECT 
    o.Id,
    v.RoleKey,
    v.DisplayName,
    NULL,          -- unmapped initially
    0,             -- not required until MODULE_HRM enabled
    'hrm'
FROM core.Organizations o
CROSS APPLY (VALUES
    ('DEFAULT_SALARY_EXPENSE',              'Salary Expense'),
    ('DEFAULT_OVERTIME_EXPENSE',            'Overtime Expense'),
    ('DEFAULT_BONUS_EXPENSE',               'Bonus Expense'),
    ('DEFAULT_EMPLOYER_CONTRIB_EXPENSE',    'Employer Contribution Expense'),
    ('DEFAULT_SALARY_PAYABLE',              'Salaries Payable'),
    ('DEFAULT_EMPLOYEE_TAX_PAYABLE',        'Employee Tax Payable'),
    ('DEFAULT_EOBI_PAYABLE',                'EOBI Payable'),
    ('DEFAULT_PF_PAYABLE',                  'Provident Fund Payable'),
    ('DEFAULT_SOCIAL_SECURITY_PAYABLE',     'Social Security Payable'),
    ('DEFAULT_OTHER_DEDUCTION_PAYABLE',     'Other Deductions Payable'),
    ('DEFAULT_EMPLOYEE_LOAN_RECEIVABLE',    'Employee Loan Receivable'),
    ('DEFAULT_EMPLOYEE_ADVANCE',            'Employee Advance'),
    ('DEFAULT_EXPENSE_CLAIM_PAYABLE',       'Expense Claim Payable'),
    ('DEFAULT_GRATUITY_EXPENSE',            'Gratuity Expense'),
    ('DEFAULT_GRATUITY_PROVISION',          'Gratuity Provision'),
    ('DEFAULT_LEAVE_ENCASHMENT_EXPENSE',    'Leave Encashment Expense')
) AS v(RoleKey, DisplayName)
WHERE NOT EXISTS (
    SELECT 1 FROM finance.AccountRoleDefaults d
    WHERE d.OrgId = o.Id AND d.RoleKey = v.RoleKey
);
```

### 5.2 GL Setup Complete — HR Awareness

```csharp
// Extend A42's existing GL setup-complete check
// When MODULE_HRM is enabled for a tenant, the HR role keys become required

public async Task<GlSetupStatus> CheckGlSetupCompleteAsync(Guid orgId)
{
    var status = await _existingCheck.CheckAsync(orgId); // A42 existing
    
    // A49 extension: if HRM enabled, check HR keys too
    if (await _moduleRegistry.IsEnabledAsync(orgId, "MODULE_HRM"))
    {
        var hrKeys = await _roleRepo.GetByModuleAsync(orgId, "hrm");
        var unmapped = hrKeys.Where(k => k.AccountId == null).ToList();
        
        if (unmapped.Any())
        {
            status.IsComplete = false;
            status.MissingMappings.AddRange(
                unmapped.Select(k => k.RoleKey));
            status.Message = $"GL setup incomplete: {unmapped.Count} " +
                             $"HR account roles unmapped";
        }
    }
    
    return status;
}
```

---

## 6. G-5: Cost Center → QBO Class/Location Mapping

**Severity:** 🟠 High | **Phase:** H5P | **Affected module:** `SMS.Modules.Integration`

### 6.1 Problem

A44 has only a single default `ClassRef`/`DepartmentRef` via `ProviderDefaults`. HRM payroll posts are summarized by cost center, and each cost center needs its own QBO Class (and optionally Location) mapping for accurate departmental P&L in QuickBooks.

### 6.2 New Entity: CostCenterQboMapping

```sql
-- Migration: M_A49_002_CreateCostCenterQboMappings

CREATE TABLE integration.CostCenterQboMappings
(
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    OrgId               UNIQUEIDENTIFIER NOT NULL,
    CostCenterCode      NVARCHAR(20) NOT NULL,
    CostCenterName      NVARCHAR(100) NOT NULL,
    
    -- QBO mapping
    QboClassRef         NVARCHAR(50) NULL,       -- QBO Class id
    QboClassName        NVARCHAR(100) NULL,      -- Display name (cached)
    QboDepartmentRef    NVARCHAR(50) NULL,       -- QBO Location/Department id
    QboDepartmentName   NVARCHAR(100) NULL,      -- Display name (cached)
    
    -- Status
    IsActive            BIT NOT NULL DEFAULT 1,
    IsMapped            AS CAST(CASE WHEN QboClassRef IS NOT NULL 
                               THEN 1 ELSE 0 END AS BIT),
    
    -- Audit
    CreatedAt           DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CreatedBy           NVARCHAR(100) NOT NULL,
    ModifiedAt          DATETIME2 NULL,
    ModifiedBy          NVARCHAR(100) NULL,
    
    CONSTRAINT UQ_CCQM_OrgCostCenter 
        UNIQUE (OrgId, CostCenterCode),
    
    INDEX IX_CCQM_Unmapped (OrgId) 
        WHERE QboClassRef IS NULL
);
```

### 6.3 Service Interface

```csharp
namespace SMS.Modules.Integration.QuickBooks;

public interface ICostCenterQboMappingService
{
    /// <summary>
    /// List all cost center mappings for an org.
    /// Auto-populated from HRM departments on first access.
    /// </summary>
    Task<List<CostCenterQboMappingDto>> ListAsync(Guid orgId);
    
    /// <summary>
    /// Map a cost center to a QBO Class and optionally a Location.
    /// </summary>
    Task UpdateMappingAsync(
        Guid orgId, string costCenterCode,
        string qboClassRef, string qboClassName,
        string qboDepartmentRef, string qboDepartmentName,
        string updatedBy);
    
    /// <summary>
    /// Get unmapped cost centers — used by G-3 readiness check.
    /// </summary>
    Task<List<string>> GetUnmappedAsync(Guid orgId);
    
    /// <summary>
    /// Resolve a cost center code to QBO refs for JE line building.
    /// Returns null ClassRef if unmapped.
    /// </summary>
    Task<CostCenterQboRefs> ResolveAsync(
        Guid orgId, string costCenterCode);
}

public record CostCenterQboRefs(
    string QboClassRef,
    string QboClassName,
    string QboDepartmentRef,
    string QboDepartmentName);
```

### 6.4 Auto-Sync from HRM Departments

When HRM module is enabled, the cost center list is auto-populated from the Department table:

```
On module enablement / on first mapping page load:
  1. Load all Departments WHERE CostCenterCode IS NOT NULL
  2. For each, UPSERT into CostCenterQboMappings:
     CostCenterCode = Department.CostCenterCode
     CostCenterName = Department.Name
     QboClassRef = NULL  (unmapped — accountant fills in)
  3. Surface in Integration Settings → "Cost Center Mapping" tab
```

### 6.5 Posting Rule

```
When building QBO JournalEntry line:
  IF CostCenter is present on the line:
    refs = ResolveAsync(orgId, costCenterCode)
    IF refs.QboClassRef != null:
      line.ClassRef = refs.QboClassRef
      line.DepartmentRef = refs.QboDepartmentRef  // may be null
    ELSE:
      // Unmapped cost center — this was caught at G-3 readiness
      // Should not reach here if Finalize check passed
      throw PostingException("Unmapped cost center: " + costCenterCode)
  ELSE:
    // No cost center on line (liability lines — G-11A.2 rules)
    // Use Payroll cost center from HrmSettings
    line.ClassRef = payrollCostCenterClassRef
```

---

## 7. G-8: Finance Standalone Profile (P2 Readiness)

**Severity:** 🟠 High | **Phase:** H1P | **Affected module:** Finance, Module Registry

### 7.1 Problem

The Finance module project references `Demand`, `Warehouse`, `Lookups`, and `SMS.Modules.Suppliers`. Menus, dependency graph, and always-on Finance pages assume sales/purchase data exists. Profile P2 (HR + Finance + QuickBooks) runs without Sales, Procurement, Inventory, or Manufacturing.

### 7.2 Solution: Profile P2 Definition

```csharp
// Module registry profile for P2
public static class DeploymentProfiles
{
    public static readonly ModuleProfile P2_HR_Finance_QBO = new()
    {
        Code = "P2",
        Name = "HR + Finance + QuickBooks",
        EnabledModules = new[]
        {
            "MODULE_HRM",
            "MODULE_FINANCE",        // always on for P2
            "MODULE_INTEGRATION",    // optional — for QuickBooks
        },
        OptionalFeatures = new[]
        {
            "FEATURE_GENERAL_LEDGER",  // optional internal GL
        },
        DisabledModules = new[]
        {
            "MODULE_SALES",
            "MODULE_PROCUREMENT",
            "MODULE_INVENTORY",
            "MODULE_MANUFACTURING",
            "MODULE_POS",
            "MODULE_LOGISTICS",
        }
    };
}
```

### 7.3 Required Changes to Finance Module

| File/Area | Change | Detail |
|---|---|---|
| Finance menus | Conditional rendering | Hide AR pages when no Sales module (no invoices); hide AP pages when no Procurement (no PO/GRN). Show GL, reports, QuickBooks settings |
| Dependency graph | Soft references | Finance `.csproj` must compile without Demand/Warehouse. Use interface-based resolution, not direct project references |
| Startup migrations | Guard checks | Migrations that seed data referencing Sales/Procurement entities must skip when those modules are off |
| GL reports | Graceful empty | Trial Balance, P&L, Balance Sheet must render correctly with only payroll data (no sales/purchase journals) |
| QuickBooks pages | Always visible | Account mapping, sync status, dead-letter — these are needed for P2 |

### 7.4 QA Checklist for P2

```
[ ] Finance module starts with only MODULE_HRM + MODULE_FINANCE enabled
[ ] GL reports show only payroll and expense data
[ ] AR Aging page hidden or shows "No sales module" message
[ ] AP Aging page hidden or shows "No procurement module" message  
[ ] QuickBooks settings page loads and allows account mapping
[ ] Payroll run → Finalize → Post → JE appears in GL
[ ] Expense claim → Approve → Post → JE appears in GL
[ ] Bank statement import (future) available if GL enabled
[ ] No startup exception from missing Demand/Warehouse/Lookups references
[ ] Menu system shows only: Dashboard, HR, Finance (GL + Reports + Settings)
```

---

## 8. G-9: Expense Claim Posting & QBO Builder

**Severity:** 🟠 High | **Phase:** H6E | **Affected module:** HRM + Gateway

### 8.1 Posting Flow

```
Employee submits ExpenseClaim
  → Manager approves (WorkflowEngine EXPENSE_CLAIM)
  → Status = Approved
  → Build FinancialTransaction:
      DocType = FinDocType.Expense  (existing enum value, A44 D-28)
      SourceModule = "hrm"
      Lines:
        Dr Expense account (per category → A42 ExpenseCategory.AccountCode)
           Dimension: cost_center = employee's department
        Cr EXPENSE_CLAIM_PAYABLE
           (if SettlementMethod = WithPayroll → cleared in payroll run)
           (if SettlementMethod = SeparatePayment → separate ExpensePayment)
  → IFinancialPostingGateway.PostAsync()
      → Internal GL: PURCH journal type
      → QuickBooks: JournalEntry (via G-1 builder)
```

### 8.2 ExpenseCategory → Account Mapping

```sql
-- Migration: M_A49_003_CreateExpenseCategories

CREATE TABLE hrm.ExpenseCategories
(
    Id                  INT IDENTITY(1,1) PRIMARY KEY,
    OrgId               UNIQUEIDENTIFIER NOT NULL,
    Code                NVARCHAR(20) NOT NULL,
    Name                NVARCHAR(100) NOT NULL,
    AccountCode         NVARCHAR(20) NOT NULL,    -- → CoA account
    AccountId           INT NULL,                  -- FK to finance.ChartOfAccounts
    DefaultTaxCode      NVARCHAR(10) NULL,
    RequiresReceipt     BIT NOT NULL DEFAULT 0,
    ReceiptThreshold    DECIMAL(18,2) NULL,        -- Required above this amount
    IsActive            BIT NOT NULL DEFAULT 1,
    
    CONSTRAINT UQ_EC_OrgCode UNIQUE (OrgId, Code)
);

-- Seed standard categories
INSERT INTO hrm.ExpenseCategories (OrgId, Code, Name, AccountCode, RequiresReceipt)
SELECT o.Id, v.Code, v.Name, v.AccountCode, v.RequiresReceipt
FROM core.Organizations o
CROSS APPLY (VALUES
    ('TRAVEL',   'Travel & Transport',    '6310', 1),
    ('MEALS',    'Meals & Entertainment', '6320', 1),
    ('OFFICE',   'Office Supplies',       '6330', 0),
    ('TELECOM',  'Telecom & Internet',    '6340', 0),
    ('LODGING',  'Lodging',               '6350', 1),
    ('OTHER',    'Other Expenses',        '6390', 1)
) AS v(Code, Name, AccountCode, RequiresReceipt)
WHERE NOT EXISTS (
    SELECT 1 FROM hrm.ExpenseCategories e
    WHERE e.OrgId = o.Id AND e.Code = v.Code
);
```

### 8.3 Separate Payment Posting (ExpensePayment)

```
When expense is paid separately (not through payroll):
  DocType = FinDocType.ExpensePayment
  Lines:
    Dr EXPENSE_CLAIM_PAYABLE (clear the payable)
    Cr DEFAULT_BANK or DEFAULT_CASH (depending on payment method)
  
  → Gateway posts to GL as BANK/CASH journal
  → Gateway posts to QBO as JournalEntry
```

---

## 9. G-10: Fiscal Period vs Pay-Date Policy

**Severity:** 🟡 Medium | **Phase:** H5P

### 9.1 Problem

Payroll run covers October but pay-date is November 1st. If November's fiscal period is closed (or not yet open), posting fails.

### 9.2 Solution: Post-Date Policy

```csharp
public enum PayrollPostDatePolicy
{
    /// <summary>Use the run's PayDate for the JE date.</summary>
    PayDate,
    
    /// <summary>Use the last day of the payroll period.</summary>
    PeriodEnd,
    
    /// <summary>Use PayDate, but fall back to PeriodEnd if PayDate's 
    /// period is closed.</summary>
    PayDateWithFallback   // DEFAULT
}
```

### 9.3 Logic

```
DeterminePostingDate(run, policy):
  IF policy == PayDate:
    date = run.PayDate
    IF FiscalPeriod(date).Status == Closed:
      FAIL with "Period {period} is closed. Reopen it or change policy."
  
  ELIF policy == PeriodEnd:
    date = run.PeriodEnd  // e.g. Oct 31
    IF FiscalPeriod(date).Status == Closed:
      FAIL with "Period {period} is closed."
  
  ELIF policy == PayDateWithFallback:
    date = run.PayDate
    IF FiscalPeriod(date).Status == Closed:
      date = run.PeriodEnd  // fallback
      IF FiscalPeriod(date).Status == Closed:
        FAIL with "Both pay date and period end are in closed periods."
      ELSE:
        WARN "Pay date {PayDate} is in a closed period. " +
             "Posting to {PeriodEnd} instead."
  
  RETURN date
```

---

## 10. G-11: Privacy — No Per-Employee Data in QBO

**Severity:** 🟡 Medium | **Phase:** H5P

### 10.1 Rule

> Per-employee amounts are **never sent** to the gateway payload for external targets. The run keeps the detail.

### 10.2 Enforcement Points

| Point | Rule |
|---|---|
| **FinancialTransaction.Lines** | Lines are summarized: one per account × cost center. No employee-level lines |
| **FinancialTransaction.Description** | Pattern: `"Payroll October 2026 — 142 employees"` — never `"John Smith salary"` |
| **QBO PrivateNote** | `"{RunNumber}, {Period}, {Headcount} employees"` |
| **QBO Line Description** | Cost center name only: `"Operations"`, `"Sales Department"` — never employee names |
| **QBO DocNumber** | `"PAY-2026-10"` — run reference, not employee reference |

### 10.3 Validation in Builder

```csharp
// In QuickBooksJournalEntryBuilder.BuildAsync():

// Validate: no line description contains employee identifiers
foreach (var line in payload.Lines)
{
    if (ContainsPersonalInfo(line.Description))
        throw new PrivacyViolationException(
            "JE line description must not contain employee names or IDs");
}

// Validate: PrivateNote has no employee data
if (ContainsPersonalInfo(payload.PrivateNote))
    throw new PrivacyViolationException(
        "JE note must not contain employee names or IDs");

private bool ContainsPersonalInfo(string text)
{
    // Check against employee names loaded for this run
    // This is a safety net — the summarization step should already
    // have removed all per-employee data
    return _employeeNames.Any(name => 
        text.Contains(name, StringComparison.OrdinalIgnoreCase));
}
```

---

## 11. G-12: QBO Void/Reversal Semantics

**Severity:** 🟡 Medium | **Phase:** H5P

### 11.1 Rule

QuickBooks does not allow deleting accounting history. A payroll correction = **reverse the original JE with an opposite JE**, then post the new corrected JE.

### 11.2 Flow

```
Payroll Correction:
  1. Reverse original run: create new PayrollRun with Status = Reversed
  2. Build reversing FinancialTransaction:
     - Same lines as original but Dr ↔ Cr swapped
     - IdempotencyKey = "hrm:void:{originalRunUuid}"
     - DocType = void of PayrollRun
  3. Post to gateway → creates reversing JE in GL (A47 D-14)
  4. Post to QBO → creates reversing JournalEntry (not a delete)
  5. Build corrected FinancialTransaction from the new run
  6. Post corrected run normally
  
Result in QBO:
  JE-001: Original payroll (Oct)     $100,000
  JE-002: Reversal of JE-001         ($100,000)
  JE-003: Corrected payroll (Oct)    $101,500
```

### 11.3 Gateway Behavior

```csharp
// A47 already supports reversing journals (D-14).
// For QBO, the builder creates an opposite JE, never a "delete":

if (transaction.IsReversal)
{
    // Build a normal JE with swapped Dr/Cr
    var reversal = await _builder.BuildReversalAsync(
        orgId, originalTransaction, transaction, mappings);
    
    // Post as a new JE — QBO sees it as a separate entry
    await _outbox.EnqueueAsync(reversal);
    
    // The original JE stays in QBO history — never deleted
}
```

---

## 12. G-13: Statutory Remittance Payment Path

**Severity:** 🟡 Medium | **Phase:** H6E

### 12.1 Flow

```
Statutory remittance (tax, EOBI, PF to government authority):
  DocType = FinDocType.StatutoryRemittance
  Lines:
    Dr EMPLOYEE_TAX_PAYABLE (or EOBI_PAYABLE, PF_PAYABLE)
    Cr DEFAULT_BANK
  
  → Gateway posts to GL as BANK journal
  → Gateway posts to QBO as JournalEntry
  
  Note: Vendor-less in v1 — no vendor/partner on the JE.
  Authority details stored in HRM only.
```

### 12.2 HRM Screen

```
Statutory Remittances screen:
  - Filter: period, type (Income Tax / EOBI / PF / Social Security)
  - Show: calculated amount from payroll run, paid amount, balance
  - Action: "Record Payment" → date, bank account, reference number
  - On record → build FinancialTransaction → PostAsync()
```

---

## 13. G-14: QBO Currency Validation

**Severity:** 🟡 Medium | **Phase:** H5P

### 13.1 Rule

QuickBooks Online home currency must match the org's base currency for payroll. If mismatch, payroll JE will fail.

### 13.2 Enforcement

```csharp
// Check on first connect AND on first payroll finalize:

public async Task ValidateCurrencyAsync(Guid orgId)
{
    var orgCurrency = await _orgService.GetBaseCurrencyAsync(orgId);
    var qboCurrency = await _qboClient.GetHomeCurrencyAsync(orgId);
    
    if (orgCurrency != qboCurrency)
        throw new CurrencyMismatchException(
            $"SMS org currency ({orgCurrency}) does not match " +
            $"QuickBooks home currency ({qboCurrency}). " +
            $"Payroll cannot post until they match.");
}
```

---

## 14. G-15: SyncControlConfigs Seeding

**Severity:** 🟡 Medium | **Phase:** H5P

### 14.1 Migration

```sql
-- Migration: M_A49_004_SeedHRMSyncControlConfigs
-- All HRM doc types start DISABLED + HELD
-- Accountant must enable after completing account mapping

INSERT INTO integration.SyncControlConfigs 
    (OrgId, DocumentType, IsEnabled, IsHeld, HeldReason, CreatedAt)
SELECT 
    o.Id,
    v.DocType,
    0,          -- disabled by default
    1,          -- held
    'HRM payroll sync disabled by default. Enable after completing ' +
    'GL account mapping and QuickBooks Class mapping.',
    SYSUTCDATETIME()
FROM core.Organizations o
CROSS APPLY (VALUES
    ('PayrollRun'),
    ('PayrollPayment'),
    ('LoanDisbursement'),
    ('ExpensePayment'),
    ('StatutoryRemittance'),
    ('FinalSettlement'),
    ('PayrollProvision')
) AS v(DocType)
WHERE NOT EXISTS (
    SELECT 1 FROM integration.SyncControlConfigs s
    WHERE s.OrgId = o.Id AND s.DocumentType = v.DocType
);
```

### 14.2 Enablement Flow

```
When accountant enables HRM sync for a tenant:
  1. Verify GL setup complete (G-4 check passes)
  2. Verify all cost centers mapped (G-5)
  3. Verify currency match (G-14)
  4. SET IsEnabled = 1, IsHeld = 0 for each HRM doc type
  5. Log enablement event
```

---

## 15. G-16: QBO JE Line Limits & Splitting

**Severity:** 🟡 Medium | **Phase:** H5P

### 15.1 Problem

QBO has a line cap on JournalEntry (historically ~1,000 lines). A large payroll with many departments could exceed this.

### 15.2 Solution: Split by Department Group

```csharp
public async Task<List<QboJournalEntryPayload>> BuildWithSplitAsync(
    Guid orgId, FinancialTransaction transaction,
    ExternalSystemMappings mappings, int maxLinesPerJe = 900)
{
    var payload = await BuildAsync(orgId, transaction, mappings);
    
    if (payload.Lines.Count <= maxLinesPerJe)
        return new List<QboJournalEntryPayload> { payload };
    
    // Split by PayrollGroup (department group)
    var groups = payload.Lines
        .GroupBy(l => l.ClassRef ?? "UNCLASSIFIED")
        .ToList();
    
    var payloads = new List<QboJournalEntryPayload>();
    var currentBatch = new List<QboJournalEntryLine>();
    int batchNumber = 1;
    
    foreach (var group in groups)
    {
        if (currentBatch.Count + group.Count() > maxLinesPerJe 
            && currentBatch.Any())
        {
            payloads.Add(CreateBatchPayload(
                payload, currentBatch, batchNumber++));
            currentBatch = new List<QboJournalEntryLine>();
        }
        currentBatch.AddRange(group);
    }
    
    if (currentBatch.Any())
        payloads.Add(CreateBatchPayload(
            payload, currentBatch, batchNumber));
    
    // Each batch must be independently balanced
    foreach (var batch in payloads)
        ValidateBalanced(batch);
    
    return payloads;
}
```

### 15.3 Throttle/Backoff

```
Existing A44 outbox handles rate limiting.
Multiple JEs from a split are enqueued as separate outbox messages.
Each has its own idempotency key: "hrm:PayrollRun:{uuid}:batch{N}"
```

---

## 16. G-17: One Org = One Employer

**Severity:** 🟡 Medium | **Phase:** H1P (documentation)

### 16.1 Decision

SMS models one Organization as one employer entity. Corporate groups with multiple legal entities must use separate SMS orgs. This is documented as a **limitation**, revisitable on demand.

### 16.2 Impact

- Payroll is always per-org — no cross-org payroll consolidation
- Each org has its own CoA, bank accounts, QuickBooks connection
- Consolidation reports (if needed later) would be a separate addendum

No code change — document this in the HRM admin guide and tenant setup wizard.

---

## 17. G-18: Bank Reconciliation (Deferred)

**Severity:** 🟡 Medium | **Phase:** Later (separate addendum)

### 17.1 Current State

No bank-statement import or reconciliation engine exists (A44 D-28 deferred it). Salary payments are posted to GL but marked "Paid" based on the payroll run, not bank confirmation.

### 17.2 Interim Solution

```
For v1:
  - Salary payment generates a bank-file (CSV) for upload to the bank
  - HRM marks the payment as "Paid" when the file is generated
  - Manual confirmation: accountant marks bank transactions as reconciled
    in the GL (not automated)
  - The bank reconciliation engine (when built) will retroactively
    match these payment JEs against bank statement lines
```

### 17.3 Future Addendum Reference

Bank reconciliation implementation (as analyzed in the previous session) would become a separate addendum building on A47's GL engine. HRM's bank payment JEs will automatically appear as candidates for matching.

---

## 18. G-19: No-Target Lifecycle State

**Severity:** 🟠 High | **Phase:** H1P | **Affected module:** Gateway + HRM

### 18.1 Problem

Profile P3 (HR only) has no accounting targets — no GL, no QuickBooks. The gateway returns `NoTargetConfigured`. Currently this would be an error state.

### 18.2 Solution: Gateway Returns Structured Result

```csharp
// Modify IFinancialPostingGateway to return a result object
// instead of throwing when no target is configured

public record PostingResult
{
    public PostingStatus Status { get; init; }
    public string Message { get; init; }
    public Dictionary<string, TargetResult> TargetResults { get; init; }
}

public enum PostingStatus
{
    Success,              // At least one target posted
    PartialSuccess,       // Some targets posted, some failed
    NoTargetConfigured,   // No active target — not an error for P3
    AllTargetsFailed,     // Targets exist but all failed
    ValidationFailed      // Payload validation failed
}

// Gateway change (minimal):
public async Task<PostingResult> PostAsync(FinancialTransaction transaction)
{
    var targets = await GetActiveTargetsAsync(transaction.OrgId);
    
    if (!targets.Any())
    {
        // G-19: this is OK for P3 profile — return, don't throw
        return new PostingResult
        {
            Status = PostingStatus.NoTargetConfigured,
            Message = "No accounting target configured. " +
                      "Transaction finalized but not posted."
        };
    }
    
    // ... existing posting logic unchanged ...
}
```

### 18.3 Payroll Run Status Lifecycle

```
         ┌─────────────────────────────────────────────┐
         │              PayrollRun.Status               │
         └─────────────────────────────────────────────┘

  Draft → Calculated → UnderReview → Approved → Finalized → Posted → Paid
                                                    │          │       │
                                                    │          │       └─ Bank file
                                                    │          │          generated
                                                    │          │
                                                    │          └─ At least one
                                                    │             target accepted
                                                    │
                                                    └─ G-19: "Finalized" is the
                                                       terminal state when no
                                                       target is active.
                                                       
                                                       Export CSV/XLSX available.
                                                       Run is complete from
                                                       HRM perspective.
```

### 18.4 Export for External Accountant

```csharp
// P3 tenants can export a journal CSV for their external accountant
public interface IPayrollJournalExportService
{
    /// <summary>
    /// Export the payroll run as a journal CSV.
    /// Columns: Account Role, Account Code, Debit, Credit, 
    ///          Cost Center, Description
    /// Available at Finalized status, regardless of posting target.
    /// </summary>
    Task<byte[]> ExportJournalCsvAsync(Guid orgId, Guid runId);
    
    /// <summary>
    /// Export as XLSX with formatting.
    /// </summary>
    Task<byte[]> ExportJournalXlsxAsync(Guid orgId, Guid runId);
}
```

---

## 19. G-20/G-21: Pakistan QuickBooks & Statutory

**Severity:** 🟠 High (G-20) / 🔴 for PK (G-21) | **Phase:** Pre-sales / H5

### 19.1 G-20: QuickBooks Availability in Pakistan

**Decision required before build:**
- Confirm client's QBO edition/country is available
- If no Pakistan QBO → internal GL is the accounting target
- This decision unlocks Profile P-PK (§11A.6)

**Action:** Pre-sales team confirms with client. If QBO is not available, the P-PK profile uses internal GL + pluggable provider adapter (future).

### 19.2 G-21: Pakistan Tax/EOBI/PF/Gratuity Values

**Status:** Data review required. Tax slabs, EOBI rates, PF rules are effective-dated and country-specific.

**Action:**
1. Review base FSD OD-4 statutory tables
2. Qualified payroll consultant reviews the effective-dated data
3. Flag `Unverified` blocks Finalize until reviewed (safety net)
4. Migration seeds the structure; client's accountant enters/confirms the rates

```sql
-- TaxSlabs, StatutoryRates tables already in base FSD
-- G-21 adds a verification flag:

ALTER TABLE hrm.TaxSlabs ADD IsVerified BIT NOT NULL DEFAULT 0;
ALTER TABLE hrm.StatutoryRates ADD IsVerified BIT NOT NULL DEFAULT 0;

-- Business rule: Payroll Finalize blocked if any referenced 
-- slab/rate has IsVerified = 0
```

---

## 20. G-22: Role Seeding for HR

**Severity:** 🟢 Low | **Phase:** H1

### 20.1 Migration

```sql
-- Migration: M_A49_005_SeedHRSecurityRoles
-- Uses A48 CORE-7 idempotent seeder pattern

-- Finance Officer role gets HR account mapping grants
INSERT INTO core.RolePermissions (RoleId, PermissionCode, GrantedAt)
SELECT r.Id, v.Permission, SYSUTCDATETIME()
FROM core.Roles r
CROSS APPLY (VALUES
    ('HRM_SETTINGS_MANAGE'),
    ('HRM_ACCOUNT_MAPPING_VIEW'),
    ('HRM_ACCOUNT_MAPPING_EDIT'),
    ('FINANCE_QBO_MAPPING_EDIT')
) AS v(Permission)
WHERE r.Code = 'FINANCE_OFFICER'
AND NOT EXISTS (
    SELECT 1 FROM core.RolePermissions rp
    WHERE rp.RoleId = r.Id AND rp.PermissionCode = v.Permission
);

-- HR Manager role
INSERT INTO core.RolePermissions (RoleId, PermissionCode, GrantedAt)
SELECT r.Id, v.Permission, SYSUTCDATETIME()
FROM core.Roles r
CROSS APPLY (VALUES
    ('HRM_EMPLOYEE_VIEW'),
    ('HRM_EMPLOYEE_EDIT'),
    ('HRM_PAYROLL_VIEW'),
    ('HRM_PAYROLL_RUN'),
    ('HRM_PAYROLL_APPROVE'),
    ('HRM_LEAVE_MANAGE'),
    ('HRM_ATTENDANCE_MANAGE'),
    ('HRM_EXPENSE_APPROVE'),
    ('HRM_REPORTS_VIEW')
) AS v(Permission)
WHERE r.Code = 'HR_MANAGER'
AND NOT EXISTS (
    SELECT 1 FROM core.RolePermissions rp
    WHERE rp.RoleId = r.Id AND rp.PermissionCode = v.Permission
);

-- Auditor role gets read-only HR access
INSERT INTO core.RolePermissions (RoleId, PermissionCode, GrantedAt)
SELECT r.Id, v.Permission, SYSUTCDATETIME()
FROM core.Roles r
CROSS APPLY (VALUES
    ('HRM_PAYROLL_VIEW'),
    ('HRM_REPORTS_VIEW'),
    ('HRM_ACCOUNT_MAPPING_VIEW')
) AS v(Permission)
WHERE r.Code = 'AUDITOR'
AND NOT EXISTS (
    SELECT 1 FROM core.RolePermissions rp
    WHERE rp.RoleId = r.Id AND rp.PermissionCode = v.Permission
);
```

---

## 21. Database Migrations

### Migration Register

| # | Migration Name | Gap | Phase | Depends On |
|---|---|---|---|---|
| M_A49_001 | SeedHRAccountRoleKeys | G-4 | H1 | A42 AccountRoleDefaults table |
| M_A49_002 | CreateCostCenterQboMappings | G-5 | H5P | integration schema |
| M_A49_003 | CreateExpenseCategories | G-9 | H6E | hrm schema |
| M_A49_004 | SeedHRMSyncControlConfigs | G-15 | H5P | integration.SyncControlConfigs |
| M_A49_005 | SeedHRSecurityRoles | G-22 | H1 | core.Roles, core.RolePermissions |
| M_A49_006 | AddVerifiedFlagToStatutoryTables | G-21 | H5 | hrm.TaxSlabs, hrm.StatutoryRates |
| M_A49_007 | SeedDefaultExpenseCategories | G-9 | H6E | M_A49_003 |
| M_A49_008 | AddHrmSettingsColumns | G-19 | H1P | hrm.HrmSettings (if exists) |

### Execution Order

```
H1 Phase:
  M_A49_001 → M_A49_005 → M_A49_006

H1P Phase:
  M_A49_008

H5P Phase:
  M_A49_002 → M_A49_004

H6E Phase:
  M_A49_003 → M_A49_007
```

---

## 22. API Endpoints

### G-5: Cost Center Mapping Endpoints

| # | Method | Route | Service | Auth |
|---|---|---|---|---|
| 1 | GET | `/api/v1/integration/qbo/cost-center-mappings` | ICostCenterQboMappingService.ListAsync | Finance.QBO.View |
| 2 | PUT | `/api/v1/integration/qbo/cost-center-mappings/{code}` | ICostCenterQboMappingService.UpdateMappingAsync | Finance.QBO.Edit |
| 3 | GET | `/api/v1/integration/qbo/cost-center-mappings/unmapped` | ICostCenterQboMappingService.GetUnmappedAsync | Finance.QBO.View |
| 4 | POST | `/api/v1/integration/qbo/cost-center-mappings/sync` | Auto-populate from departments | Finance.QBO.Edit |

### G-3: Posting Readiness Endpoint

| # | Method | Route | Service | Auth |
|---|---|---|---|---|
| 5 | GET | `/api/v1/hrm/payroll-runs/{id}/posting-readiness` | IPayrollPostingReadinessService.CheckReadinessAsync | HRM.Payroll.View |

### G-4: GL Setup Status Endpoint (Extension)

| # | Method | Route | Service | Auth |
|---|---|---|---|---|
| 6 | GET | `/api/v1/finance/gl/setup-status` | (existing, extended) | Finance.GL.View |

### G-9: Expense Category Endpoints

| # | Method | Route | Service | Auth |
|---|---|---|---|---|
| 7 | GET | `/api/v1/hrm/expense-categories` | List categories | HRM.Expense.View |
| 8 | POST | `/api/v1/hrm/expense-categories` | Create category | HRM.Settings.Manage |
| 9 | PUT | `/api/v1/hrm/expense-categories/{id}` | Update category | HRM.Settings.Manage |

### G-15: Sync Control Endpoints (Extension)

| # | Method | Route | Service | Auth |
|---|---|---|---|---|
| 10 | GET | `/api/v1/integration/sync-control` | (existing, now includes HRM types) | Finance.QBO.View |
| 11 | PUT | `/api/v1/integration/sync-control/{docType}` | Enable/disable/hold | Finance.QBO.Admin |

### G-19: Journal Export Endpoints

| # | Method | Route | Service | Auth |
|---|---|---|---|---|
| 12 | GET | `/api/v1/hrm/payroll-runs/{id}/journal-export/csv` | IPayrollJournalExportService.ExportJournalCsvAsync | HRM.Payroll.View |
| 13 | GET | `/api/v1/hrm/payroll-runs/{id}/journal-export/xlsx` | IPayrollJournalExportService.ExportJournalXlsxAsync | HRM.Payroll.View |

**Total: 13 new/extended endpoints for gap resolution**

---

## 23. Business Rules

### Blocker Rules (Must enforce before go-live)

| ID | Rule | Gap | Phase |
|---|---|---|---|
| BR-G1-001 | All HRM transactions post to QBO as JournalEntry, never Bill/Invoice | G-1 | H5P |
| BR-G1-002 | JE builder must produce a balanced entry (Σ Dr = Σ Cr) | G-1 | H5P |
| BR-G1-003 | Idempotency key format: `hrm:{DocType}:{sourceUuid}[:batch{N}]` | G-1 | H5P |
| BR-G2-001 | All 7 HRM FinDocType values route to JournalEntry target on QBO | G-2 | H5P |
| BR-G2-002 | HRM source module = "hrm" on all gateway transactions | G-2 | H5P |
| BR-G3-001 | Payroll Finalize is blocked if any account role is unmapped to CoA | G-3 | H5P |
| BR-G3-002 | Payroll Finalize is blocked if any CoA account lacks QBO mapping (when QBO active) | G-3 | H5P |
| BR-G3-003 | Payroll Finalize is blocked if any cost center lacks QBO Class mapping (when QBO active) | G-3, G-5 | H5P |
| BR-G19-001 | Gateway returns NoTargetConfigured (not error) when no target active | G-19 | H1P |
| BR-G19-002 | Payroll run status = Finalized is a valid terminal state when no target | G-19 | H1P |

### High-Severity Rules

| ID | Rule | Gap | Phase |
|---|---|---|---|
| BR-G4-001 | HR account role keys are seeded unmapped; tenant accountant maps them | G-4 | H1 |
| BR-G4-002 | GL setup-complete check includes HR keys when MODULE_HRM enabled | G-4 | H5P |
| BR-G5-001 | Cost center mapping table auto-populates from HRM departments | G-5 | H5P |
| BR-G5-002 | Unmapped cost center blocks Finalize (QBO active) | G-5 | H5P |
| BR-G5-003 | Liability lines (salary payable, tax) post to Payroll cost center, not split | G-5 | H5P |
| BR-G8-001 | P2 profile: Finance runs without Sales/Procurement/Inventory modules | G-8 | H1P |
| BR-G8-002 | P2 profile: AR/AP pages hidden when no source module | G-8 | H1P |
| BR-G9-001 | Expense claim approved → PURCH JE via gateway | G-9 | H6E |
| BR-G9-002 | Expense category determines the debit account | G-9 | H6E |
| BR-G9-003 | ExpensePayment (separate) = Dr Claim Payable / Cr Bank | G-9 | H6E |

### Medium-Severity Rules

| ID | Rule | Gap | Phase |
|---|---|---|---|
| BR-G10-001 | PayDateWithFallback is the default posting date policy | G-10 | H5P |
| BR-G10-002 | Closed period blocks posting; re-open or use fallback | G-10 | H5P |
| BR-G11-001 | No employee name, ID, or salary in QBO payload | G-11 | H5P |
| BR-G11-002 | QBO JE description = cost center name only | G-11 | H5P |
| BR-G11-003 | QBO PrivateNote = run number + period + headcount only | G-11 | H5P |
| BR-G12-001 | Payroll reversal = opposite JE in QBO, never delete | G-12 | H5P |
| BR-G12-002 | Original JE remains in QBO history | G-12 | H5P |
| BR-G13-001 | StatutoryRemittance = Dr payable / Cr bank, vendor-less | G-13 | H6E |
| BR-G14-001 | QBO home currency must match org base currency | G-14 | H5P |
| BR-G14-002 | Currency mismatch blocks payroll posting to QBO | G-14 | H5P |
| BR-G15-001 | HRM sync types seeded disabled + held by default | G-15 | H5P |
| BR-G15-002 | Accountant explicitly enables after mapping complete | G-15 | H5P |
| BR-G16-001 | JE exceeding 900 lines splits by department group | G-16 | H5P |
| BR-G16-002 | Each split batch is independently balanced | G-16 | H5P |
| BR-G21-001 | Payroll Finalize blocked if referenced tax slab IsVerified = 0 | G-21 | H5 |

---

## 24. Test Scenarios

### Blocker Tests (G-1, G-2, G-3, G-19)

| ID | Scenario | Expected | Gap |
|---|---|---|---|
| TS-G1-001 | Payroll run finalized → JournalEntry appears in QBO sandbox | JE posted, balanced, no employee data | G-1 |
| TS-G1-002 | Payroll with 5 departments → 5 cost center ClassRefs on JE lines | Each line has correct ClassRef | G-1, G-5 |
| TS-G1-003 | Duplicate post (same idempotency key) → reuse existing | No duplicate JE in QBO | G-1 |
| TS-G1-004 | QBO target offline → dead-letter → retry succeeds | JE eventually posted | G-1 |
| TS-G2-001 | PayrollRun FinDocType routes to JournalEntry target | QBO receives JournalEntry, not Bill | G-2 |
| TS-G2-002 | ExpensePayment FinDocType routes to JournalEntry | QBO receives JE with expense lines | G-2 |
| TS-G2-003 | Gateway source_module = "hrm" on all payroll postings | FinancialPostings.SourceModule = "hrm" | G-2 |
| TS-G3-001 | Finalize with unmapped SALARY_EXPENSE → blocked | Error lists missing account role | G-3 |
| TS-G3-002 | Finalize with GL mapped but no QBO mapping → blocked (QBO active) | Error lists unmapped CoA accounts | G-3 |
| TS-G3-003 | Finalize with all mapped → proceeds to Post | Status = Posted | G-3 |
| TS-G3-004 | Finalize with all mapped, QBO not active → proceeds to Post (GL only) | GL JE created, no QBO | G-3 |
| TS-G19-001 | P3 profile (no target), Finalize → status = Finalized | No error, no PostingPending | G-19 |
| TS-G19-002 | P3 Finalized run → CSV export contains correct journal | Downloadable CSV | G-19 |
| TS-G19-003 | P3 Finalized run → XLSX export with formatting | Downloadable XLSX | G-19 |
| TS-G19-004 | P3 later enables GL → re-finalize posts pending runs | Status transitions to Posted | G-19 |

### High-Severity Tests (G-4, G-5, G-8, G-9)

| ID | Scenario | Expected | Gap |
|---|---|---|---|
| TS-G4-001 | Enable MODULE_HRM → 16 account keys seeded unmapped | Keys visible in GL setup | G-4 |
| TS-G4-002 | GL setup-complete with unmapped HR keys → incomplete | Returns list of unmapped keys | G-4 |
| TS-G4-003 | Map all HR keys → GL setup-complete = true | Payroll Finalize unblocked | G-4 |
| TS-G5-001 | Cost center mapping page loads departments | All departments with cost centers shown | G-5 |
| TS-G5-002 | Map cost center "OPS" → QBO Class "Operations" | Saved, payroll line uses ClassRef | G-5 |
| TS-G5-003 | Unmapped cost center → Finalize blocked (QBO active) | Error lists unmapped cost centers | G-5 |
| TS-G8-001 | P2 profile startup — no Sales/Procurement modules | No startup exceptions | G-8 |
| TS-G8-002 | P2 GL reports — only payroll data | TB/P&L/BS render correctly | G-8 |
| TS-G8-003 | P2 AR page hidden or graceful empty | No crash, appropriate message | G-8 |
| TS-G8-004 | P2 QuickBooks settings accessible | Account mapping page loads | G-8 |
| TS-G9-001 | Expense claim approved → JE: Dr Expense / Cr Claim Payable | GL entry created | G-9 |
| TS-G9-002 | Expense paid separately → Dr Claim Payable / Cr Bank | GL entry + QBO JE | G-9 |
| TS-G9-003 | Expense with payroll → payslip line → cleared in run | No separate payment | G-9 |

### Medium-Severity Tests

| ID | Scenario | Expected | Gap |
|---|---|---|---|
| TS-G10-001 | Pay date in closed period, policy = Fallback → posts to period end | JE date = period end | G-10 |
| TS-G10-002 | Both dates in closed periods → blocked with message | Clear error message | G-10 |
| TS-G11-001 | QBO JE PrivateNote → no employee names | Only run number + headcount | G-11 |
| TS-G11-002 | QBO JE line descriptions → cost center names only | No personal identifiers | G-11 |
| TS-G12-001 | Payroll correction → reversing JE in QBO + new JE | Two JEs: reversal + corrected | G-12 |
| TS-G12-002 | Original QBO JE still exists after reversal | Not deleted | G-12 |
| TS-G13-001 | Statutory remittance → Dr Tax Payable / Cr Bank | GL + QBO JE | G-13 |
| TS-G14-001 | Currency mismatch SMS=PKR, QBO=USD → blocked | Error at connect/finalize | G-14 |
| TS-G15-001 | Fresh QBO connection → HRM types disabled+held | SyncControlConfigs seeded | G-15 |
| TS-G15-002 | Accountant enables after mapping → sync active | Payroll posts to QBO | G-15 |
| TS-G16-001 | Payroll with 1200 lines → splits into 2 JEs | Two balanced JEs in QBO | G-16 |
| TS-G21-001 | Unverified tax slab → Finalize blocked | Error lists unverified items | G-21 |

**Total: 39 test scenarios for gap resolution**

---

## 25. Phase Mapping & Effort

### Tasks by Phase

| Phase | Gap Coverage | New Tasks | Effort |
|---|---|---|---|
| **H1** (Foundation) | G-4, G-22 | Account role seeding, role permissions, verification flags | ~8 tasks, 2 days |
| **H1P** (Profile Readiness) | G-8, G-17, G-19 | P2/P3 profile, Finance standalone, no-target lifecycle, export service | ~15 tasks, 4 days |
| **H5P** (Payroll Posting & QBO) | G-1, G-2, G-3, G-5, G-10, G-11, G-12, G-14, G-15, G-16 | JE builder, FinDocType extension, readiness check, cost center mapping, privacy, reversal, splitting | ~55 tasks, 12 days |
| **H6E** (Expense Claims & Remittances) | G-9, G-13 | Expense categories, posting, separate payment, statutory remittances | ~18 tasks, 5 days |
| **Pre-sales** | G-20 | QuickBooks edition confirmation | 1 decision |
| **H5** (Payroll Core) | G-21 | Tax slab verification flag, review process | ~5 tasks, 1 day |

### Summary

| Metric | Count |
|---|---|
| Total gap resolution tasks | ~101 |
| New entities/tables | 2 (CostCenterQboMappings, ExpenseCategories) |
| New services/interfaces | 5 (JE builder, readiness, cost center mapping, export, expense categories) |
| Modified existing services | 3 (gateway routing, GL setup check, QBO target) |
| New API endpoints | 13 |
| Business rules | 27 |
| Test scenarios | 39 |
| Database migrations | 8 |
| Estimated effort | **24 days** (on top of base A49 HRM estimate) |

### Revised A49 Total Estimate

```
Base HRM (from A49 task register):          ~420 tasks
Gap resolution supplement (this document):  ~101 tasks
Client directions (§11A.8):                 ~25 tasks
─────────────────────────────────────────────
Revised total:                              ~546 tasks
Revised effort:                             ~90 days (18 weeks)
```

---

## Cross-Reference: Gap → Entity/Service → Migration → Test

| Gap | Entity/Service | Migration | Business Rule | Test |
|---|---|---|---|---|
| G-1 | IQuickBooksJournalEntryBuilder | — | BR-G1-001..003 | TS-G1-001..004 |
| G-2 | FinDocType enum, PostingSourceModules | — (code change) | BR-G2-001..002 | TS-G2-001..003 |
| G-3 | IPayrollPostingReadinessService | — | BR-G3-001..003 | TS-G3-001..004 |
| G-4 | AccountRoleDefaults (rows) | M_A49_001 | BR-G4-001..002 | TS-G4-001..003 |
| G-5 | CostCenterQboMappings | M_A49_002 | BR-G5-001..003 | TS-G5-001..003 |
| G-8 | ModuleProfile P2 | — | BR-G8-001..002 | TS-G8-001..004 |
| G-9 | ExpenseCategories | M_A49_003, M_A49_007 | BR-G9-001..003 | TS-G9-001..003 |
| G-10 | PayrollPostDatePolicy | — | BR-G10-001..002 | TS-G10-001..002 |
| G-11 | (enforced in G-1 builder) | — | BR-G11-001..003 | TS-G11-001..002 |
| G-12 | (reversal in G-1 builder) | — | BR-G12-001..002 | TS-G12-001..002 |
| G-13 | (uses G-1 + G-2 path) | — | BR-G13-001 | TS-G13-001 |
| G-14 | (validation in G-1 builder) | — | BR-G14-001..002 | TS-G14-001 |
| G-15 | SyncControlConfigs (rows) | M_A49_004 | BR-G15-001..002 | TS-G15-001..002 |
| G-16 | (splitting in G-1 builder) | — | BR-G16-001..002 | TS-G16-001 |
| G-17 | Documentation only | — | — | — |
| G-18 | Deferred | — | — | — |
| G-19 | PostingResult, PayrollJournalExportService | M_A49_008 | BR-G19-001..002 | TS-G19-001..004 |
| G-20 | Decision | — | — | — |
| G-21 | IsVerified flag | M_A49_006 | BR-G21-001 | TS-G21-001 |
| G-22 | RolePermissions (rows) | M_A49_005 | — | — |

---

**End of Supplement — Addendum 49 Blocker & Gap Resolution Specifications**
