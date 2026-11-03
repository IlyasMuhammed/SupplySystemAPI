# Payroll posting matrix and salary formulas (tasks 403, 404)

Decisions: `D-PAY-*` in [ADDENDUM-49-ANALYSIS.md](ADDENDUM-49-ANALYSIS.md). Routes: [API-CONTRACT.md](API-CONTRACT.md) §7 and §9.

## Posting matrix

One line per account role and cost center, from unblocked lines only. Nothing per employee is sent (G-11). "Payroll CC" is the payroll cost center snapshotted when the run was calculated.

| Event | Side | Role | Amount | Cost center |
|---|---|---|---|---|
| PayrollRun | Dr | `DEFAULT_SALARY_EXPENSE`, `DEFAULT_OVERTIME_EXPENSE`, `DEFAULT_BONUS_EXPENSE` | earnings by component role | the employee's share |
| | Dr | `DEFAULT_EMPLOYER_CONTRIB_EXPENSE` | employer contributions | the employee's share |
| | Dr | `DEFAULT_EXPENSE_CLAIM_PAYABLE` | reimbursements paid with payroll | Payroll CC |
| | Cr | `DEFAULT_EMPLOYEE_TAX_PAYABLE` | income tax | Payroll CC |
| | Cr | `DEFAULT_EOBI_PAYABLE`, `DEFAULT_PF_PAYABLE`, `DEFAULT_SOCIAL_SECURITY_PAYABLE` | employee + employer parts | Payroll CC |
| | Cr | `DEFAULT_EMPLOYEE_LOAN_RECEIVABLE`, `DEFAULT_EMPLOYEE_ADVANCE` | recoveries | Payroll CC |
| | Cr | `DEFAULT_OTHER_DEDUCTION_PAYABLE` | other deductions | Payroll CC |
| | Cr | `DEFAULT_SALARY_PAYABLE` | net pay | Payroll CC |
| PayrollPayment | Dr | `DEFAULT_SALARY_PAYABLE` | net of unblocked lines | Payroll CC |
| | Cr | `DEFAULT_BANK` or `DEFAULT_CASH` | same | Payroll CC |
| Reversal | | gateway `VoidAsync`, key `hrm:PayrollRun:{uuid}:void` | opposite journal | |

Roles come from each component's `PostingRole` and `ExpenseRole`, so a custom component posts where it is configured. A role ending `_EXPENSE` follows the employee's cost center; any other role posts on the Payroll CC. A negative total moves to the other side. Each component is rounded once to 2 dp, so debits equal credits exactly; `DEFAULT_ROUNDING` is only a guard, and a difference of 1.00 or more refuses to build.

Posting date (G-10), when the internal GL is active: `PayDate` and `PeriodEnd` fail if that period is closed; `PayDateWithFallback` falls back to period end with a note. With no active GL the period is not checked.

Outcome: at least one target Posted moves the run to Posted. `NoTargetConfigured` and `ModuleNotLicensed` keep it Finalized with posting status `NoTarget`. A failed, held or queued target keeps the posting row retryable under the same key.

## Formula syntax

Numbers (no exponent), `+ - * /`, brackets, comparisons `> < >= <= = <>` (1 or 0).

| Function | Meaning |
|---|---|
| `MIN(a, ...)`, `MAX(a, ...)` | 1 to 16 arguments |
| `IF(c, a, b)` | `a` when `c` is not 0; only the branch taken runs |
| `ROUND(x)`, `ROUND(x, d)` | away from zero, `d` 0 to 6 |
| `SLAB(x)` | annual tax on annual income `x`, from the table in force at period end |

| Variable | Meaning |
|---|---|
| `BASIC` | earned basic, after pro rata |
| `GROSS`, `TOTAL_EARNINGS` | running totals |
| `TAXABLE_INCOME` | taxable earnings so far × 12 |
| `WORKING_DAYS`, `PAID_DAYS` | the line's days |
| `OT_HOURS` | approved overtime hours, multipliers applied |
| a component code | its amount, if earlier in the structure |

Examples: `BASIC * 0.45`, `5000 * PAID_DAYS / WORKING_DAYS`, `IF(GROSS > 100000, 500, 0)`. A formula on `ITAX` yields annual tax; the run takes a twelfth.

Evaluation order: earnings in sequence, then earnings from providers and adjustments, then tax, statutory and other deductions in sequence, then deductions from providers. So a deduction sees every earning, and an earning cannot name a deduction.

Limits: 1000 characters, depth 32, 400 nodes. A formula that fails blocks that employee's line with the reason; the run continues. It is a hand-written parser into a small tree: nothing can name a type, a file or a network resource. Test one with `POST api/hrm/salary-formula/test`.
