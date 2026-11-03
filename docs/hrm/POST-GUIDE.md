# Addendum 49 — Posting, QuickBooks and deployment profiles

Tasks 094, 406, 407, 408, 409, 411, E-12. Decisions are in [ADDENDUM-49-ANALYSIS.md](ADDENDUM-49-ANALYSIS.md) (D-POST-n); routes in [API-CONTRACT.md](API-CONTRACT.md) §10.

**Verification status.** Everything below that talks to QuickBooks was tested against the repo's fakes (`ScriptedAccountingProvider`, `FakeQuickBooksCompany`). **It has never run against a real QuickBooks company or sandbox:** this environment has no Intuit keys. The rules marked *unverified* below come from memory of Intuit's behaviour. Run the sandbox pass in "Before go-live" before relying on the JournalEntry path.

## One organization, one employer (G-17)

One SMS organization is one employer and one payroll legal entity. A group of companies is several organizations; there is no consolidated payroll or journal across them.

## Switching HRM on (FD-9)

1. A super admin licenses `MODULE_HRM` and the sub-features wanted (`PUT api/system/organizations/{id}/features`, or apply a profile). **The licence does not switch HRM on until a payroll cost center exists:** the response lists it under `licensedNotEnabled` with `PAYROLL_COST_CENTER_REQUIRED`.
2. The org admin runs the wizard: `GET api/hrm/setup/status`, then `POST api/hrm/setup/payroll-cost-center` (select one, or create one inline; creating also needs `HRM_ORG_MANAGE`), with the mode `CENTRAL` or `BY_DEPARTMENT`.
3. `POST api/tenant/modules/MODULE_HRM/enable`. Without step 2 it answers 422 `PAYROLL_COST_CENTER_REQUIRED`.

A super admin who licenses an organization that already has its cost center gets the module on directly, as for any other module.

## QuickBooks setup for HR

Every HRM document goes to QuickBooks as a JournalEntry. Every type starts **off + held**: documents are stored and wait, and nothing is sent until the accountant turns the type on.

1. **Accounts.** Under Finance → Accounting defaults give each HR role an account (`DEFAULT_SALARY_EXPENSE`, `DEFAULT_SALARY_PAYABLE`, …). Under QuickBooks Integration → Accounts map those accounts to QuickBooks accounts. *Unverified:* QuickBooks wants a customer or vendor on a journal line that hits Accounts Payable or Accounts Receivable, and HR entries carry none, so mapping to one of those is refused (`JE_ACCOUNT_NEEDS_PARTY`). Use a liability or other current asset account.
2. **Classes.** QuickBooks Integration → Cost centers (`api/integrations/quickbooks/cost-center-mappings`). The list fills itself from HR's cost centers. Map each to a Class, and optionally a Location. Turn on class tracking in QuickBooks first, and refresh its reference data.
3. **Currency.** The organization's base currency must equal QuickBooks' home currency. A mismatch blocks the entry (`JE_CURRENCY_MISMATCH`); preflight also reports it on connect.
4. **Turn the types on** under Sync control (`PUT api/integrations/quickbooks/sync-control/{type}`): `PayrollRun`, `PayrollPayment`, `LoanDisbursement`, `Expense`, `ExpensePayment`, `StatutoryRemittance`, `FinalSettlement`, `PayrollProvision`. Refused with 409 `HRM_SYNC_NOT_READY` and the detail until accounts, classes and currency check out. Turning one on sends what it held, that type only.

Behaviour worth knowing:

| Situation | What happens |
|---|---|
| Type off + held, mappings missing | Finalize is **not** blocked (D-POST-2). The entry waits, and is validated when released. |
| Type on, a mapping missing | Finalize is blocked by readiness (`POSTING_READINESS`). If a mapping is removed later, the entry goes Blocked and is sent once it is fixed. |
| A name or employee number in a description | Refused, nothing stored (`JE_PERSONAL_DATA`). The message does not say whose. |
| More than 900 lines | Several entries `{number}-B1`, `-B2`, each balanced. Liabilities are shared across them in proportion; every account's total is unchanged. |
| Payroll reversed | A new opposite entry `{number}-R` (per batch). The original stays. An entry never sent is closed and nothing is sent. |
| A mapping changed between posting and reversal | The reversal uses today's mapping, so it does not cancel the original line for line (R-POST-2). Reverse first, or correct by hand in QuickBooks. |

## Profiles (G-8, D-14)

Data in `ModuleProfiles` over the real module codes. `GET api/tenant/module-profiles` lists them; `POST api/tenancy/organizations/{id}/apply-profile { code }` (super admin) applies one as ordinary licence changes.

| Profile | Licensed and on | Left as they are | Off (menus and routes hidden) |
|---|---|---|---|
| **P2** HR + Finance + QuickBooks | HRM, Finance, Integration | General Ledger | Demand, Inventory, Warehouse, Logistics, Material Issue, Manufacturing, Services, POS, Suppliers, Customers, CRM |
| **P2-S** service income | HRM, Finance, General Ledger, Customers, Suppliers | Integration | Demand, Inventory, Warehouse, Logistics, Material Issue, Manufacturing, Services, POS, CRM |
| **P3** HR only | HRM | | Everything above, plus Finance, Integration, General Ledger, Suppliers, Customers |
| **P-PK** Pakistan | HRM, Finance, General Ledger | | As P2, plus Integration |

HRM in any profile is subject to the wizard. Statutory data stays unverified until reviewed (G-21), whatever the profile.

G-8 is met at runtime (D-13): the Finance project keeps its references, all modules ship in one binary, and "off" means the module gate. Finance's routes are gated only by `MODULE_FINANCE` and its startup seeds nothing about sales or purchasing; Accounting references neither Demand nor Warehouse.

**Service invoices (P2-S).** `POST api/sales-invoices/service` creates a draft; issue it with the normal route. In the GL each line posts to its `incomeAccountCode` (an active Revenue or OtherIncome leaf account), else `DEFAULT_REVENUE`. In QuickBooks it is an ordinary invoice whose lines are one item: choose **Service invoice item** (`ServiceItemRef`) under QuickBooks Integration → Defaults. Until then the invoice is Blocked with `SERVICE_ITEM_MISSING` and a message saying so, and goes once it is chosen.

Known limits: QuickBooks books income to that item's own income account, so **a per-line income account takes effect in the GL only**; with several income accounts the two books differ by account, not in total. SMS cannot check the item id (QuickBooks' reference data here holds no items): a wrong id fails at QuickBooks and the invoice shows why. Tested on the fake only.

## Provider seam for P-PK (E-12): design note, nothing built

Reviewed for a second accounting system beside the internal GL. **What already holds:** the gateway (`IFinancialPostingGateway`) is provider-neutral, posts to every registered `IFinancialPostingTarget`, and HRM knows nothing beyond it; `FinancialTransaction` carries roles, cost center codes and summarized lines; `ExternalSystemMappings` is keyed by a `systemType`; `IAccountingProvider` and its registry already resolve by provider key.

**What a second adapter would have to do:** register its own `IFinancialPostingTarget`, take its account map with its own `systemType`, and reuse the role lines. **What is QuickBooks-shaped today and would need work:** `integration.CostCenterQboMappings` and `IQuickBooksPostingReadiness` are named for and keyed to a QuickBooks connection, and readiness composes exactly two targets. The outbox and `EntityMaps` are reusable, but `QuickBooksJournalEntryBuilder`'s 900-line limit, DocNumber rules and privacy net are written for QBO and should move behind the adapter. Choosing the system is the client's (FD-10); its availability and API are unverified.

## Import mapping (FD-8)

[MIGRATION-MAPPING-GUIDE.md](MIGRATION-MAPPING-GUIDE.md) and the workbook [HRM-MIGRATION-MAPPING.csv](HRM-MIGRATION-MAPPING.csv). Only the Odoo-flavoured CSV import is built; SAP is a flat-file mapping into the same columns.

## Deployment checklist

[DEPLOYMENT-CHECKLIST.md](DEPLOYMENT-CHECKLIST.md), per profile.

## Before go-live: the QuickBooks sandbox pass nobody has done yet

With Intuit sandbox keys and a company that tracks classes:

1. A run with two cost centers: the JournalEntry appears, balanced, with the right classes, no names (TS-G1-001, 002).
2. Repeat the posting: no second entry (TS-G1-003).
3. Reverse: an opposite entry, the original intact (TS-G12).
4. Map a payable role to an Accounts Payable account and confirm QuickBooks really refuses a partyless line, so the refusal here is warranted.
5. A company with classes off: confirm what QuickBooks does with a `ClassRef`. Also send one service invoice with a real service item.
6. An entry with 901+ lines, if a test can reach it. The 900 limit is the spec's safety margin on a "historically ~1000" cap, not a verified number.
7. `TxnDate` and a home-currency `CurrencyRef` with multicurrency off.
