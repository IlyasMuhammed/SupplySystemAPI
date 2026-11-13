# Setting up your organization — admin guide (A50, tasks 358 / 365)

This guide is for organization administrators: whoever holds **Run setup wizard** (`SETUP_WIZARD_RUN`, granted to Organization Admin and System Admin).

Decisions are in [ADDENDUM-50-ANALYSIS.md](ADDENDUM-50-ANALYSIS.md). Payload shapes are in [PAYLOAD-SCHEMAS.md](PAYLOAD-SCHEMAS.md).

## 1. First login

- **If your organization is new**, your first visit to the dashboard opens the **setup wizard**.
- **Other users** of your organization see a *"Setup incomplete — ask your administrator"* banner on the dashboard until the setup is done.
- **Organizations that existed before A50** are marked *Completed* and are never redirected.

You can always reopen the wizard from **Settings › Organization › Setup Wizard**.

## 2. The wizard, step by step

Every step has three things:
- **Skip setup** (with a confirmation). It loads the general (GLOBAL) defaults and marks the setup *Skipped*.
- **Reset to default** (steps 1 and 3–7). It undoes your changes on that step and takes the defaults again.
- **Automatic saving.** Moving between steps saves your progress. If you leave and come back, the wizard resumes where you were, with your edits.

| Step | What you do | Notes |
|---|---|---|
| 1. Company and industry | Pick your **country** and **industry** | The company name is read-only. The choice selects the starting defaults; the pills show where each category came from (e.g. `PK/Trading`, or `GLOBAL/GLOBAL` as a fallback). |
| 2. Modules | Switch on the modules you use | Only modules in your plan can be switched; this needs **Manage modules** (`MODULES_MANAGE`). A category whose module is off is not created. |
| 3. Chart of accounts | Rename, add or remove accounts; drag accounts between groups (or use **Move to…**) | Badges such as `DEFAULT_CASH` mark accounts that play a default role; those cannot be removed. Levels 1–3 are groups, level 4 accounts are postable. Accounts already in your books are never changed. Untick *Create this chart of accounts* to leave it out. |
| 4. Tax codes | Edit codes, rates, where they apply and the default per usage | **The shipped rates are unverified: check them with your tax adviser** (the yellow banner). |
| 5. Currency and fiscal year | Base currency, additional currencies, the fiscal start month, the period type | The base currency is applied only while no documents exist. The fiscal year containing today is created (monthly periods). |
| 6. Warehouses | Add warehouses (code, name, address) and tick their zones | Defaults to the pack's main warehouse. A warehouse with an existing code is kept. |
| 7. Users and roles | Invite people (email, name, role); optionally connect QuickBooks | Roles shown are the ones your modules support. Each invitee gets a 7-day link to set a password; an existing account is left alone. **Connect QuickBooks** opens the QuickBooks page in a new tab; come back and the status refreshes. |
| 8. Review and launch | Check the counts per category, untick any you don't want, then **Launch my system** | See below. |

### Launching

- Categories are created one at a time, in a fixed order:
  1. currencies;
  2. chart of accounts;
  3. tax;
  4. fiscal year;
  5. payment terms;
  6. units;
  7. product categories;
  8. numbering;
  9. approvals;
  10. roles;
  11. warehouses;
  12. document and email templates.
- **When every category succeeds** you see *"Your system is ready!"*, with quick-start links.
- **When a category fails**, the wizard shows where it stopped and why. The categories before it are already created. Fix the problem and press **Launch again**: it carries on and never creates duplicates.
- **Running the wizard again later** only adds what is missing. Nothing you created or changed is overwritten.

## 3. Default Data (Settings › Organization › Default Data)

There is one row per category, showing:
- **Seeded** — how many records the setup created;
- **Current** — how many records the category holds now;
- **Modified** — how many seeded records you changed, or how many are missing;
- **Last applied** — the date, with the action;
- **Version** — the version applied. A *"vN available"* pill appears when a newer default exists.

Each row also has two buttons:
- **View** opens the screen that maintains that category: Chart of Accounts, Tax Codes, Currencies, AR & AP settings (payment terms), Lookup Values (units), Product Categories, Workflows (approval rules), Roles, Warehouses, and Document / Email Templates. Document numbering has no screen.
- **Reset** opens a dialog with three modes:
  - **Merge** adds back missing default records. It keeps your own records and your changes.
  - **Overwrite** puts default records back to their default values. It keeps records you added.
  - **Full reset** deletes the category and recreates it. It is **refused** while any record is used on a document, and the dialog then says *"X records are referenced by documents"*.

  When a newer version exists, you can tick *Use the newest defaults*.

**Run setup wizard again** opens the wizard.

## 4. Quick-start checklist (task 365)

- [ ] Finish (or skip) the setup wizard.
- [ ] Check the tax codes with your tax adviser (**Settings › Finance Setup › Tax Codes**).
- [ ] Review the chart of accounts and the default accounts (**Finance › General Ledger**).
- [ ] Add your first customer, supplier and product, or import them (**Settings › Organization › Data Import**).
- [ ] Enter opening balances: stock, AR/AP and the GL opening balance import.
- [ ] Create your first sale order and purchase order.
- [ ] Invite your team and check their roles (**Administration › Users & Access**).
- [ ] Optional: connect QuickBooks (**Settings › Integrations › QuickBooks Integration**).
- [ ] Brand your documents and emails (**Settings › Organization › Document Templates / Email Templates**).

### Human resources (when the HR module is on)

- **HR defaults** (Pakistan only): leave types, salary components and the tax slab table appear on the Review step and in Default Data as "HR defaults". They are seeded as **unverified**: setup never marks tax or statutory data as checked. Payroll cannot be finalized until someone with the HR "verify statutory data" permission has confirmed the tables and rates against the current notification, and the seeded table may already have expired, so enter the current one first.
- **Expense claims** get a two-level approval: the employee's line manager (else the department head) first, then the Finance Manager for larger claims (PKR 10,000.01 and above in the Pakistan pack). The Finance Manager can approve level 2 from the HR expense screen or the Approvals inbox. The rule applies whenever HR is on, with or without the Demand module.
- **HR Manager** and **HR Officer** are the existing built-in roles; setup reuses them. Assign them to your HR staff on the Users page.
- **Emailing payslips** (API only for now, there is no button in the HR screens yet): `POST api/hrm/payroll-runs/{run}/payslips/email` sends each employee their own payslip PDF to the address on their employee record (work email, else personal); it needs the permission **Email Payslips in Bulk** (`HRM_PAYSLIP_EMAIL`, held by the HR Manager and Org Admin; paying payroll does not include it). `POST api/hrm/payslips/{payslip}/email` sends one (HR in scope, or the employee for their own). Edit the wording under Settings › Email templates › Payslip.
- The checklist gains three steps while HR is on: set up HR (payroll cost center), add your first employee, and have your tax slabs checked.

## 5. For the platform super admin

Master defaults are managed under **System Administration › Setup Defaults**:
- the list has filters for country, industry, category, version and status;
- there are category editors for the chart of accounts (tree), tax (table, with a *verified* tick), roles (role × permission matrix), approval rules (levels) and units (groups), and a JSON editor for every category;
- you can import a pack file (check first, then confirm) and export everything;
- each default can be published or unpublished.

A change reaches only organizations set up afterwards. Details: SUPER-ADMIN-GUIDE.md.
