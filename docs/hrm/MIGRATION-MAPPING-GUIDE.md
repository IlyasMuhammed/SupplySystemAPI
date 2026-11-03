# Moving employees from Odoo or SAP (tasks 408, 409)

For HR administrators and implementers. The field-by-field workbook is [HRM-MIGRATION-MAPPING.csv](HRM-MIGRATION-MAPPING.csv).

## What is built, plainly

- **One importer exists: the Odoo-flavoured employee CSV**, import profile `HRM_EMPLOYEE`, code `src/SMS.Modules.Hrm/Services/Post/HrmEmployeeImportTarget.cs`. It creates new employees, and optionally their hire record and primary bank account.
- **SAP has no importer and no connection.** It is a flat-file mapping: you extract each infotype yourself and shape it into the same columns, then use the same importer.
- **Nothing connects to a live Odoo or SAP system.** Everything is a file you upload.
- **Only employees are imported.** Salaries, leave balances, loans, addresses and emergency contacts are not. See [What the import leaves out](#what-the-import-leaves-out).

**About the Odoo and SAP names.** Every SMS column and rule below was read from the code. The Odoo and SAP names come from the FSD (Addendum 49 §5 and Appendices A and B), which was written from memory. **None has been checked against a real Odoo installation or SAP system.** Check them against your own version first. The workbook marks each as `unverified`.

## Before you start

1. HRM is switched on for the organization. The profile is not listed otherwise, and rows are refused with `MODULE_OFF`.
2. `AppSettings:AesEncryptionKey` is set for real ([deployment checklist](DEPLOYMENT-CHECKLIST.md)). CNICs and bank accounts are encrypted with it as they arrive.
3. Branches, departments and designations exist and are active. The import looks them up and does not create them.
4. The person importing holds the import codes and the codes for what the rows write:

   | Step | Codes |
   |---|---|
   | Upload, map, preview | `IMPORT_UPLOAD` |
   | Validate, run | `IMPORT_EXECUTE` |
   | Start, validate and run this profile | also `HRM_EMPLOYEE_EDIT`, `HRM_EMPLOYMENT_MANAGE`, `HRM_EMPLOYEE_SENSITIVE_VIEW`, `HRM_BANK_EDIT` |

   A super admin never holds the sensitive codes, and the System Admin role is not seeded with them (analysis A-23, D-16), so give this work to an HR role.

## Running the import

Screen: Settings → Data Import (`/portal/pages/settings/import`). Routes: `api/import/...`, feature `MODULE_DATA_IMPORT`, which is always on.

1. Download the template if you want one (`GET api/import/profiles/HRM_EMPLOYEE/template`). A plain CSV works as well.
2. Upload a `.csv` or `.xlsx` (at most 10 MB and 50,000 rows by default). CSV may use comma, semicolon or tab.
3. Check the column mapping. Exact names and the aliases below map automatically; correct anything else by hand. `name` must be mapped, and so must `identification_id` or `external_id`.
4. Validate. Fix the file and upload again until it is clean.
5. Run. Rows go in batches of 100, and **a batch that fails is rolled back whole**. More than 500 valid rows run in the background.
6. Review the people created: names especially, and every employee's status.

Coverage: one end-to-end test runs a one-row file through upload, validate and run (`HrmHireToRetireE2ETests.Employee_import_creates_people_with_names_and_encrypted_identity`). Large files have not been tried on this profile.

## Columns the importer accepts

| Column | Also accepted as | Goes to | Rule (from the code) |
|---|---|---|---|
| `external_id` | `ext_id`, `legacy_id`, `source_id` | Import's own reference table | Optional, 100 characters. Your old system's id. Not stored on the employee. |
| `name` | `employee_name`, `full_name` | First and last name | **Required, and needs two words.** Split on the first space. See the warning below. |
| `identification_id` | `cnic`, `national_id`, `ICNUM` | CNIC, encrypted | Optional. 13 digits; dashes and spaces ignored. Refused if already on file or repeated in the file. |
| `birthday` | `date_of_birth`, `dob` | Date of birth | A date, not in the future. |
| `gender` | | Gender | `male`, `m`, `female`, `f`, `other`, any case. |
| `marital` | `marital_status` | Marital status | `single`, `married`, `divorced`, `other`; `widower`, `widow`, `widowed` become Widowed; `cohabitant` becomes Other. |
| `work_email` | | Work email | Valid email, 150 characters. |
| `private_email` | `personal_email` | Personal email | Valid email, 150 characters. |
| `mobile_phone` | `mobile` | Mobile | 7 to 15 digits; `+`, spaces and `- ( ) . /` allowed. |
| `department_id` | `department` | Hire record | Code or name of an active department. |
| `job_id` | `job_title`, `designation` | Hire record | Code or name of an active designation. |
| `work_location_id` | `branch`, `work_location` | Hire record | Code or name of an active branch. |
| `date_start` | `contract_date_start`, `join_date` | Join date and hire record start | A date. |
| `parent_id` | `manager` | Manager on the hire record | **The SMS employee number** of someone already on file. |
| `date_end` | `contract_date_end` | Contract end on the hire record | A date. |
| `bank_name` | | Primary bank account | Needed when `bank_account_id` is given. |
| `bank_account_id` | `bank_account` | Primary bank account number, encrypted | 50 characters. |

**The hire record is all or nothing.** `department_id`, `job_id`, `work_location_id` and `date_start` must all be given, or all be blank (`MISSING_DEPENDENCY`). Without them there is no hire record and no join date, and a `parent_id` or `date_end` on such a row is an error (`MISSING_DEPENDENCY`), not silently dropped.

**What every imported person gets.** Status `Active`, whatever the source says, so leave former employees out of the file. An SMS-generated number, `{prefix}-NNNN`. The hire record takes the system's defaults for what the file cannot set: employment type, grade, shift, cost center.

## Traps

**One-word names are refused.** Last name is required, so a `name` with no space fails validation (`NAME_NEEDS_TWO_WORDS`) on its own row. Fix them in the source.

**Only unambiguous dates.** This profile takes `yyyy-MM-dd`, `dd-MMM-yyyy` and Excel dates. `05/01/2024` is refused (`INVALID_DATE`), because it can be read two ways. Export dates as `yyyy-MM-dd`.

**Managers need their SMS number, which does not exist until they are imported.** `parent_id` looks only at employees already on file, not at earlier rows of the same file, and the file cannot be re-imported to add it later. So import top-down in separate files, taking each tier's new numbers for the next, or leave `parent_id` blank and set managers on the employment screen afterwards. The second is simpler for most migrations.

**Create only, never update.** A CNIC already on file is an error (`DUPLICATE_IN_DB`), the same for a repeat run. A row whose `external_id` was imported earlier and has no CNIC is skipped under the default strategy. Choosing `UPDATE` is refused for this profile. Correct people on their own screen.

**A person with no CNIC cannot be matched.** With `identification_id` blank and no `external_id`, each run creates that person again.

**A name that matches several rows.** Lookups use code or name, and a code wins. If two departments share a name, one is picked without warning. Use codes.

**Protect the file, and be aware of what the import keeps.** See the next section.

## Sensitive data during import

CNIC and bank numbers are encrypted in the import's own tables too (`import.rows`, until the purge job deletes them after 90 days), using the same key as the employee record. They are masked as `*****`, with every unmapped column of the file, in the preview, the error list and both downloads, and they never appear in an error. Those reads need the same HRM codes as running the import: `IMPORT_MANAGE` alone gets 403.

So an error report cannot be corrected and re-uploaded as it stands: fix the source file. Other personal fields (name, date of birth, email, phone) stay readable to those who pass the check, and are stored as plain text in the import tables. Store and delete the source file as you would a payroll file, and treat downloaded reports the same way.

## Odoo

Export `hr.employee` to CSV and rename or reshape the columns to the table above. The importer takes SMS's column names, which follow Odoo's. It has no knowledge of Odoo itself. All of this is `unverified` against a real Odoo version.

| Odoo export | Expect to change | Why |
|---|---|---|
| `name` | Check one-word names | Must have two words. |
| `identification_id` | Nothing | Dashes are ignored. |
| `birthday` | Set to `yyyy-MM-dd` | See dates above. |
| `gender`, `marital` | Probably nothing | Matching ignores case. A translated label would be refused. |
| `department_id`, `job_id`, `work_location_id` | Likely to need replacing with SMS codes | A relational export gives a display name, and a department's may be a full path such as `Sales / Retail`. That would not match. |
| `parent_id` | **Replace with the SMS number, or clear it** | Odoo gives the manager's name; SMS wants its own employee number. |
| `date_start`, `date_end` | Bring in from the contract | They live on `hr.contract`, not `hr.employee` (FSD §5.5), so a plain employee export will not have them. |
| `bank_account_id` | Must be the account number | A relational field; check what your export gives. |
| `bank_name` | **Add this column** | Not an Odoo employee field; SMS requires it with an account. |
| `wage` | Remove, or leave unmapped | Not imported. |
| `id` or `barcode` | Map to `external_id` if you want a trail | |

FSD Appendix A differs from the built importer in these ways, and the importer is what counts: `contract.date_start` is `date_start`; `wage` to a salary assignment is not built; `job_id` matches code or name, not name only; `bank_name` is an addition.

## SAP flat file

**Mapping only: no SAP code exists in SMS.** Extract one flat file per infotype, join them per person (`PERNR`), keep the record valid today, and produce one CSV in the columns above. All SAP names are `unverified`.

| SAP field | CSV column | Notes |
|---|---|---|
| PA0001-PERNR | `external_id` | SMS numbers employees itself; Appendix B's PERNR to EmployeeNumber is not possible. |
| PA0002-VORNA + NACHN | `name` | Join with one space. A multi-word first name would split wrongly, since the split is at the first space. Correct afterwards. |
| PA0002-GBDAT | `birthday` | SAP dates are often `YYYYMMDD` or `DD.MM.YYYY`, neither accepted. Convert to `yyyy-MM-dd`. |
| PA0002-GESCH | `gender` | SAP stores codes (often 1 and 2). Convert to `male` / `female`. |
| PA0002-FAMST | `marital` | Codes; convert to the words above. |
| PA0185-ICNUM, where ICTYP is the national ID | `identification_id` | `ICNUM` is accepted as a header. |
| PA0105 email and cell subtypes | `work_email`, `mobile_phone` | From FSD §5.4; subtype numbers vary. |
| PA0001-ORGEH | `department_id` | Create the department first, using this or its short text as the code. |
| PA0001-STELL | `job_id` | Likewise. |
| PA0001-WERKS + BTRTL | `work_location_id` | Create branches first. A combined code is your choice; it must match. |
| PA0041 or PA0000 hire date | `date_start` | Pick the date you treat as joining. |
| PA0016 contract end | `date_end` | |
| PA0009-BANKL | `bank_name` | BANKL is a bank key, so translate it to a name. |
| PA0009-BANKN | `bank_account_id` | IBAN is not an import column; add it on the screen. |

Leave out anyone already separated (PA0000-STAT2), since all rows arrive as Active.

Not available through the import:

| Infotype | Enter it through |
|---|---|
| PA0001-KOSTL | Cost center on the employment record |
| PA0001-PERSK | Grade on the employment record |
| PA0002-NATIO | Nationality on the employee |
| PA0006 | Addresses on the employee |
| PA0021 | Emergency contacts on the employee |
| PA0008 | Salary assignments, with a structure and approval |
| PA2006 | Leave allocation, `POST api/hrm/leave/allocate` |
| PA0045 | Loans, `POST api/hrm/loans` |

Appendix B lists PA2006 as an "Opening Leave Balance import" and PA0045 as opening loans. **Neither import exists.**

## What the import leaves out

Father's or husband's name, nationality, NTN, phone, addresses, emergency contacts, photo, user link, grade, shift, cost center, employment type, salary, leave balances, loans, IBAN, bank branch and account title. Enter these on the employee, employment and bank screens. Salary needs a structure and approval (`POST api/hrm/employees/{uuid}/salary`). The decision is D-POST-5 in [ADDENDUM-49-ANALYSIS.md](ADDENDUM-49-ANALYSIS.md).

## After the import

- Count employees against the source, by department.
- Spot-check names, dates of birth and join dates. Dates are where a bad format hides.
- Reveal a sample CNIC and bank account, to prove they decrypt.
- Set managers, grades, shifts and cost centers, then salaries, then opening leave.
