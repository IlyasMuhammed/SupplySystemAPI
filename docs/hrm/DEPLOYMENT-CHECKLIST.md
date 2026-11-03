# HRM deployment checklist, per profile (task 411)

For implementers. Do "Every profile" first, then your profile's list. Profile contents: [POST-GUIDE.md](POST-GUIDE.md#profiles-g-8-d-14). `GET api/tenant/module-profiles` lists them; `POST api/tenancy/organizations/{id}/apply-profile { code }` applies one (super admin).

## Every profile

### Before the first employee

- [ ] **Set a real `AppSettings:AesEncryptionKey` before the first employee is entered.** Changing it later orphans all encrypted data.
  - What depends on it in HRM: CNIC, NTN, bank account number and IBAN. The keyed hashes behind the duplicate CNIC check and exact CNIC search derive from it too.
  - **Nothing warns you.** The shipped default is the placeholder `CHANGE_ME_32_BYTE_ENCRYPTION_KEY`, and the API starts and encrypts with it without complaint. Any string works, because the service hashes it into a key.
  - There is no re-encryption tool. After a change, old values cannot be decrypted, and a CNIC already on file no longer counts as a duplicate.
  - **The key is platform-wide, not HRM's.** Supplier bank details, QuickBooks credentials and courier credentials use the same key. On a deployment that already holds any of those, changing it now orphans them. Check first, and if there is such data, decide how to re-enter it before changing the key.
  - Set it outside source control, for example the environment variable `AppSettings__AesEncryptionKey`. Keep a backup: losing the key loses the data.
  - It is per deployment. All organizations on one deployment share it.

- [ ] **Migrations apply on API restart.** Nothing to run by hand, and there is no separate step in which to review them. A restart applies whatever is pending, on whichever database that API points to. A49's are:

  | Module | Migration |
  |---|---|
  | Hrm | `A49_HrmInitial` |
  | Accounting | `A49_HrDefaultKeys` |
  | Integration | `A49_CostCenterQboMappings` |
  | Finance | `A49_ServiceInvoiceIncomeAccount` |

  Take a backup, plan the restart, and read the startup log afterwards. All modules migrate at the same restart. HRM's is called from `UseHrmModule`; an exception in `Migrate()` stops startup.

### Switching on

- [ ] Apply the profile. **HRM stays off until a payroll cost center exists**: the response lists it under `licensedNotEnabled`.
- [ ] Sub-features as wanted. Profiles license all eight: Attendance, Leave, Payroll, Recruitment, Performance, Training, Loans, Expenses. Loans also gates settlements, increments, letters and tax certificates.
- [ ] Run the wizard: `GET api/hrm/setup/status`, `POST api/hrm/setup/payroll-cost-center`, then `POST api/tenant/modules/MODULE_HRM/enable`. Details in [POST-GUIDE.md](POST-GUIDE.md#switching-hrm-on-fd-9).
- [ ] Give HR staff a role that holds the sensitive codes. A super admin never has them, and System Admin is not seeded with them, so neither can see salaries, run payroll or reveal a CNIC.

### Payroll data: needs a payroll consultant

**Every statutory value seeded is unverified: from memory, not from a source.** No one on the build side has confirmed them.

- [ ] **Verify tax tables and rates with a qualified payroll consultant.** Correct them, then have someone with `HRM_STATUTORY_VERIFY` mark each reviewed (`POST api/hrm/tax-tables/{uuid}/verify`, `POST api/hrm/statutory-rates/{uuid}/verify`). Finalize answers 409 `PAYROLL_STATUTORY_UNVERIFIED` until the ones the run used are marked. Editing a verified row clears its mark.

  Seeded, all per organization and unverified:

  | Row | Values | In force |
  |---|---|---|
  | Tax table `PK-SALARIED-2026` | Six slabs of 2025-26 | **2025-07-01 to 2026-06-30** |
  | EOBI | Employee 1%, employer 5%, basis 37,000 | From 2025-07-01, no end date |
  | Provident fund | 8.33% and 8.33% | From 2025-07-01, no end date |
  | Provincial social security | Employer 6% | From 2025-07-01, no end date |
  | Gratuity | Employer 100% (one month's wage per year of service) | From 2025-07-01, no end date. Blocks posting a settlement while unverified. |

- [ ] **The seeded tax table expired on 2026-06-30. Enter a current one before the first payroll.** A run reads the table in force at its period end, and every period ending after that date finds none. Each employee line with `ITAX`, which the standard structure includes, is then blocked with "No tax table is in force for this period." Verifying the seeded table does not help, since it stays expired. Create the new table with `POST api/hrm/tax-tables` (`HRM_PAYROLL_CONFIG`), with dates that do not overlap (409 `TAX_TABLE_OVERLAP`), and repeat each tax year.
- [ ] Check the rates against their own effective dates. They have no end date, so an outdated rate keeps applying without any error.
- [ ] Confirm moon-dated holidays. Those seeded with `isEstimated` are estimates; correct them when the date is notified.
- [ ] Check the seeded leave types against your contracts and law ([LEAVE-POLICY-GUIDE.md](LEAVE-POLICY-GUIDE.md)).
- [ ] Set up components, a structure and approved salary assignments ([PAYROLL-POSTING-AND-FORMULAS.md](PAYROLL-POSTING-AND-FORMULAS.md)).

### Moving people in

- [ ] If you are importing: [MIGRATION-MAPPING-GUIDE.md](MIGRATION-MAPPING-GUIDE.md), especially one-word names, date formats and plain-text retention in the import tables.

## P2: HR + Finance + QuickBooks

Finance and Integration on. General Ledger is left as it is.

- [ ] Do the QuickBooks sandbox pass in [POST-GUIDE.md](POST-GUIDE.md#before-go-live-the-quickbooks-sandbox-pass-nobody-has-done-yet). **This path has only run against test fakes, never against real QuickBooks.**
- [ ] Assign HR account roles under Accounting defaults and map those accounts to QuickBooks accounts.
- [ ] Turn on class tracking in QuickBooks, refresh reference data, then map each cost center to a Class.
- [ ] Organization base currency equals QuickBooks home currency.
- [ ] Turn on each document type under Sync control. All start off and held. Nothing is sent until then.
- [ ] If General Ledger is on too: complete its setup, because both targets post.

## P2-S: HR + Finance, service income

Finance, General Ledger, Customers and Suppliers on. Integration is left as it is.

- [ ] Complete General Ledger setup and enable internal accounting.
- [ ] `GET api/finance/setup/status` shows `hrRolesMissing` empty. It lists missing HR roles and does not move the status, so a "Complete" GL can still lack them.
- [ ] Customers and suppliers imported or entered.
- [ ] Read the service-invoice notes in [POST-GUIDE.md](POST-GUIDE.md#profiles-g-8-d-14) before quoting behaviour to a client. A per-line income account takes effect in the GL only.
- [ ] If Integration is on: every P2 item above, and choose the **Service invoice item** under QuickBooks Integration → Defaults. Until then service invoices stay Blocked with `SERVICE_ITEM_MISSING`.

## P3: HR only

No Finance, no General Ledger, no Integration.

- [ ] Nothing to set up for posting. A finalized run stays Finalized with posting status `NoTarget`.
- [ ] Agree with the external accountant how they take the journal: `GET api/hrm/payroll-runs/{uuid}/journal-export/csv` or `/xlsx`.
- [ ] Note the payment step: `POST api/hrm/payroll-runs/{uuid}/pay` is allowed from Finalized only here, where there is no active target (G-19).

Expenses and settlements still need their approvals here, and record no accounting.

## P-PK: Pakistan, internal GL

Finance and General Ledger on. No Integration, so no QuickBooks.

- [ ] Complete General Ledger setup and enable internal accounting.
- [ ] `GET api/finance/setup/status` shows `hrRolesMissing` empty.
- [ ] Statutory review is especially material here: this profile's books rest on it.
- [ ] Choosing a second accounting system is open (FD-10): only a design note exists, and no adapter is built ([POST-GUIDE.md](POST-GUIDE.md#provider-seam-for-p-pk-e-12-design-note-nothing-built)).

## After the first run, any profile

- [ ] `GET api/hrm/payroll-runs/{uuid}/reconciliation`: run totals against what each target received.
- [ ] Reveal one CNIC and one bank account, and confirm that decrypts after an API restart.
