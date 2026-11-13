# Addendum 50 — SetupDefaults payload schemas (task 362)

Owner: DATA-50. Authoritative shapes of `SetupDefaults.DataPayload` per `DataCategory`. Machine-readable JSON Schemas (draft 2020-12) are in `src/SMS.Modules.Onboarding/Packs/schemas/{CATEGORY}.json`; packs in `Packs/{CC}_{INDUSTRY}/{CATEGORY}.json` are validated against them by `tests/SMS.Modules.Onboarding.Tests` (`PackValidationTests`).

Rules for every payload:
- camelCase property names, except COA, which keeps the A42 template file's snake_case (so a COA payload *is* an A42 template file).
- Every payload may carry optional `displayName` and `notes` strings (copied by CORE into SetupDefaults.DisplayName / Notes; seeders ignore them).
- The file content is the payload verbatim; there is no envelope. The super admin import/export file (CORE) wraps payloads as `{ "format": "sms-setup-defaults", "version": 1, "defaults": [ { "countryCode", "industryType", "dataCategory", "displayName", "notes", "isActive", "payload": { … } } ] }`.
- Natural keys (idempotent apply, BR-50-004) are given per category. Codes compare case-insensitively.
- Seeders read tolerantly (extra fields ignored), but a super admin save is validated strictly against the schema (D-4).
- Decisions: D-60..D-64 in [ADDENDUM-50-ANALYSIS.md](ADDENDUM-50-ANALYSIS.md).

## Packs shipped

| Pack folder | Categories |
|---|---|
| `PK_Trading` | all 13 |
| `AE_Trading` | all 13 (AE CoA, tax, currency, fiscal; the rest = the GLOBAL content) |
| `SA_Trading` | all 13 (SA CoA, tax, currency, fiscal; the rest = the GLOBAL content) |
| `GLOBAL_GLOBAL` | all 13 |
| `PK_GLOBAL`, `AE_GLOBAL`, `SA_GLOBAL` | TAX, FISCAL, CURRENCY only (country-only categories, TS-50-025). `PK_GLOBAL` also carries HRM (D-90): the HR defaults exist for Pakistan only, so there is no HRM file in any other pack |

Fallback chain (D-4): exact → country+GLOBAL → GLOBAL+industry → GLOBAL+GLOBAL. So `AE + Manufacturing` resolves TAX/FISCAL/CURRENCY from `AE_GLOBAL` and COA and everything else from `GLOBAL_GLOBAL`.

## CURRENCY (Finance, D-14)
```json
{ "base": { "code": "PKR", "name": "Pakistani Rupee", "symbol": "₨", "decimals": 2 },
  "additional": [ { "code": "USD", "name": "US Dollar", "symbol": "$" } ] }
```
- `code` ISO 4217 (`^[A-Z]{3}$`); every code in the packs exists in the Finance catalog (`CurrencyBootstrapper`).
- Natural key: code. Activates; never deactivates. `base` becomes the sale/purchase/service base only while the org has no documents; otherwise left alone and reported.

## COA (Accounting, D-12)
The A42 template JSON (`Templates/PK_GENERAL.json` format):
```json
{ "template_code": "PK_GENERAL", "template_name": "Pakistan — General Business", "country_code": "PAK", "currency_code": "PKR", "version": "1.0",
  "accounts": [ { "code": "1", "name": "Assets", "root_type": "Asset", "level": 1, "is_group": true, "children": [ … ] } ] }
```
Account: `code` `^[A-Za-z0-9]{1,20}$`, `name` ≤ 200, `root_type` Asset/Liability/Equity/Revenue/Expense, `level` 1–4, `is_group`, optional `account_type` (17 A42 types), `description` ≤ 500, `is_default_account` + `default_account_key` (one of the 16 A42 keys), `children`.

A42 rules every COA pack obeys (and the test checks): exactly the 5 roots with codes 1–5; levels 1–3 are groups, level 4 is a leaf; every code starts with its root digit; codes unique; `account_type` only on leaves and valid for the root (D-23; Tax on Asset or Liability); `3900 Opening Balance Equity` is a level-4 Equity leaf carrying `DEFAULT_OPENING_BALANCE`; each default key used at most once and only on a leaf. Natural key: account code.

Counts: PK_Trading 110 (= PK_GENERAL), GLOBAL_GLOBAL 104 (= GLOBAL_GENERAL), AE_Trading 52 (= AE_GENERAL), SA_Trading 53 (= SA_GENERAL; adds Zakat and GOSI payables), see D-64. All 16 default keys assigned in each.

## TAX (Finance `finance.tax_codes`, D-13)
```json
{ "verified": false,
  "verificationNote": "Rates are indicative (FBR, 2026). Check with your tax adviser before use.",
  "taxCodes": [ { "code": "GST18", "name": "Sales Tax 18%", "rate": 18.0, "usage": "SALES", "type": "Output", "isDefault": true } ] }
```
- `code` ≤ 20, `name` ≤ 100, `rate` 0–100 (percent, 2 decimals), `usage` SALES / PURCHASE / BOTH (the Finance column), `type` informational (Output / Input / Withholding / Excise / Exempt / ZeroRated / None), `isDefault` at most one per usage.
- **`verified: false` — every shipped tax pack is UNVERIFIED. The wizard shows "check rates with your tax adviser". A super admin sets `verified: true` only after a tax adviser has confirmed the rates.**
- Natural key: code.

Shipped rates (all **unverified — check with tax adviser**):

| Pack | Code | Name | Rate | Usage | Default |
|---|---|---|---|---|---|
| PK | GST18 | Sales Tax 18% | 18 | SALES | yes |
| PK | FED16 | Federal Excise Duty 16% | 16 | SALES | |
| PK | WHT_G | WHT on Goods 4.5% | 4.5 | PURCHASE | |
| PK | WHT_S | WHT on Services 8% | 8 | PURCHASE | |
| PK | INPUT18 | Input Tax 18% | 18 | PURCHASE | yes |
| PK | GST0 | Exempt 0% | 0 | BOTH | |
| AE | VAT5 | VAT 5% | 5 | SALES | yes |
| AE | VAT0 | Zero-Rated 0% | 0 | BOTH | |
| AE | INVAT5 | Input VAT 5% | 5 | PURCHASE | yes |
| SA | VAT15 | VAT 15% | 15 | SALES | yes |
| SA | VAT0 | Zero-Rated 0% | 0 | BOTH | |
| SA | INVAT15 | Input VAT 15% | 15 | PURCHASE | yes |
| GLOBAL | NOTAX | No Tax 0% | 0 | BOTH | yes |

## FISCAL (Accounting, A42 fiscal years)
```json
{ "startMonth": 7, "periodType": "Monthly", "dateFormat": "DD/MM/YYYY", "yearNamePattern": "FY {YYYY}-{YY}" }
```
- `startMonth` 1–12; `periodType` only `Monthly` (12 periods, as A42 creates); `dateFormat` DD/MM/YYYY | MM/DD/YYYY | YYYY-MM-DD (display hint); `yearNamePattern` optional hint.
- The seeder creates the fiscal year containing today. Natural key: the year's start date.
- PK Jul start; AE, SA, GLOBAL Jan start.

## PAYMENT_TERMS (Finance A48 EarlyPaymentTerm + global lookups, D-15)
```json
{ "terms": [ { "code": "NET30", "name": "Net 30", "netDays": 30, "discountPercent": 0, "discountDays": 0, "isDefault": true } ] }
```
- `code` ≤ 20, `name` ≤ 100, `netDays` 0–365, `discountPercent` 0–100, `discountDays` 0–365, `isDefault` optional.
- Names equal the existing global `lookups` payment-terms values (`Net 15`, `Net 30`, `Net 60`, `Cash on Delivery`, `2/10 Net 30`) so the insert-if-missing by name reuses them.
- Natural key: code. 5 terms in every pack: COD, NET15, NET30 (default), NET60, 2_10_NET30.

## UOM (Lookups `LookupValue` type `uom`, D-16)
```json
{ "groups": [ { "name": "Weight", "units": [ { "code": "KG", "name": "Kilogram", "isBase": true }, { "code": "G", "name": "Gram", "conversionFactor": 0.001 } ] } ] }
```
- `code` ≤ 20 `[A-Za-z0-9_.-]`, `name` ≤ 150, one `isBase` per group, `conversionFactor` > 0 = base units per unit (stored only, R-1).
- Natural key: code. Codes follow the existing global uom values (`L`, `M`, not `LTR`/`MTR`) so they are reused (D-62).
- 16 units: Weight KG G LB TON; Count PCS DOZ BOX CTN PLT; Volume L ML GAL; Length M CM MM FT.

## PRODUCT_CATEGORIES (Inventory, D-17)
```json
{ "categories": [ { "code": "RAW", "name": "Raw Materials", "description": "…", "children": [ { "code": "RAW-MET", "name": "Metals" } ] } ] }
```
- `code` ≤ 20, `name` ≤ 200, `description` ≤ 500, `children` = ProductSubCategory; deeper levels flattened (R-2). (`subCategories` accepted as alias by the seeder.) Natural key: code.
- 8 categories: RAW, FG, PKG, CONS, SRV, SPARE, TRD, OTH (no children in the shipped packs).

## DOC_NUMBERING (Logistics `document_number_formats`, D-19)
```json
{ "sequences": [ { "documentType": "PurchaseOrder", "defaultPrefix": "PO", "prefix": "PO", "pattern": "PO-{YYYY}-{NNNNN}", "startNumber": 1, "resetYearly": true, "sharedGenerator": true } ] }
```
- `prefix` `^[A-Za-z0-9]{1,10}$` is what the seeder stores; `defaultPrefix` = what the code passes today; `pattern` informational; year and 5-digit padding never change.
- `sharedGenerator: false` marks a document numbered outside the shared generator; stored, inert (R-3).
- Natural key: documentType. Shipped (existing default prefixes, D-61): SaleOrder SO (live), PurchaseOrder PO (live), Quotation SQ (live), SalesInvoice SINV (inert, `SINV-yyyyMMdd-n`), GoodsReceipt GRN (inert, COUNT+1), PaymentVoucher PAY (inert, supplier payment COUNT+1).

## APPROVAL_RULES (WorkflowEngine, D-18)
```json
{ "rules": [ { "documentType": "PurchaseOrder", "interfaceCode": "PO", "levels": [
    { "level": 1, "minAmount": 0, "maxAmount": null, "approverRole": "PROCUREMENT_MANAGER", "label": "Procurement manager", "slaHours": 48 },
    { "level": 2, "minAmount": 50000, "maxAmount": null, "approverRole": "FINANCE_MANAGER", "label": "Finance manager", "slaHours": 72 } ] },
  { "documentType": "ExpenseClaim", "interfaceCode": "EXPENSE_CLAIM", "levels": [ … ] } ] }
```
- `level` 1..n = workflow step order. Level 1 always applies (minAmount 0). Level n > 1 = a step skipped while value < `minAmount` (the engine's `ConditionValue < x`). `maxAmount` is informational (null = no upper bound).
- Amounts are in the org's base currency. `approverRole` = a role code. `AUTO` is not used (the engine has no auto-approve tier, D-63).
- ExpenseClaim (D-91): the seeder applies it to the `EXPENSE_CLAIM` definition while MODULE_HRM is on. Level 1 becomes the HRM **named approver** step (its `approverRole`, e.g. `HR_MANAGER`, is informational: the engine cannot resolve an HR role); levels above 1 are role steps that must resolve (`FINANCE_MANAGER`) and are skipped while the claim total < `minAmount`. PK/AE/SA/GLOBAL: level 1 up to 10,000, level 2 from 10,000.01. `afterA49` is still accepted by the schema and ignored.
- Natural key: documentType + level.
- PK: PROCUREMENT_MANAGER ≥0, FINANCE_MANAGER ≥50,000, GENERAL_MANAGER ≥500,000 (PKR). GLOBAL / AE / SA: the existing seeded PO tiers, PROCUREMENT_MANAGER ≥0, FINANCE_MANAGER ≥10,000, GENERAL_MANAGER ≥50,000, BOARD_MEMBER ≥200,000.

## ROLES (Auth, D-20)
```json
{ "roles": [ { "code": "SALES_MANAGER", "name": "Sales Manager", "description": "…", "permissions": ["SALE_ORDER_VIEW", "…"], "requiredModules": ["MODULE_DEMAND", "MODULE_CRM"] } ] }
```
- `code` = role code (FSD `code`; the task text's `roleCode` is the same field). When it equals a global role code (all 8 shipped do), the global role is reused with provenance `REUSED` and the permission list is not applied.
- `permissions` = real `PermissionCodes` (auth.Permissions) or `["ALL"]`; the test checks every code exists. They apply only when an org-owned custom role is created (a code with no global role).
- `requiredModules` = `ModuleCodes` (`MODULE_*` / `FEATURE_*`); empty = always.
- Natural key: code. 8 roles: ORG_ADMIN, SALES_MANAGER, SALES_REP, PURCHASE_OFFICER, PROCUREMENT_MANAGER, WAREHOUSE_OPERATOR, INVENTORY_MANAGER (the FSD "Warehouse Manager"), FINANCE_OFFICER. HR_MANAGER and HR_OFFICER (D-92) are global roles too, need `MODULE_HRM`, and are reused; the packs now carry 10 roles.

## HRM (Hrm `HrmSetupSeeder`, D-90)
```json
{ "verified": false,
  "leaveTypes": [ { "code": "ANNUAL", "name": "Annual Leave", "entitlementDays": 14, "isPaid": true, "carryForwardCap": 14, "attachmentAfterDays": null,
                    "genderRestriction": null, "accrualMethod": "Annual", "effectiveFrom": "2026-01-01" } ],
  "salaryComponents": [ { "code": "ITAX", "name": "Income tax withheld", "type": "Deduction", "isTaxable": false, "inGross": false, "calcMethod": "Formula",
                          "postingRole": "DEFAULT_EMPLOYEE_TAX_PAYABLE", "expenseRole": null, "formula": "SLAB(TAXABLE_INCOME)", "sortOrder": 90 } ],
  "taxSlabTables": [ { "code": "PK-SALARIED-2026", "name": "…", "countryCode": "PK", "effectiveFrom": "2025-07-01", "effectiveTo": "2026-06-30", "sourceNote": "UNVERIFIED …",
                       "slabs": [ { "from": 0, "to": 600000, "fixedAmount": 0, "ratePercent": 0 }, { "from": 4100000, "to": null, "fixedAmount": 616000, "ratePercent": 35 } ] } ] }
```
- **`verified` must be `false`** (schema `const` and a seeder check). The seeder never verifies anything (A49 G-21): a table is inserted `IsVerified = 0`, and a table a person verified is never rewritten or deleted. Payroll Finalize stays blocked until a person with HRM_STATUTORY_VERIFY has verified the tables and rates in HR.
- Natural keys: `LEAVE/CODE`, `COMPONENT/CODE`, `TAX/CODE`. Lengths follow the hrm tables (leave and component codes 20, tax table code 30, names 100).
- `calcMethod` is Fixed or Formula (a `Percent` component needs a base component, so it is not seedable here; a Formula needs a formula). Slabs: the first starts at 0, each starts where the previous ends, only the last may have no `to`, rate 0..100.
- Statutory rates (EOBI, provident fund, social security, gratuity) are **not** in this payload; HRM seeds them itself on first use, unverified.
- Pack: `PK_GLOBAL/HRM.json` — 6 leave types (ANNUAL 14 days, SICK 10, CASUAL 10, MATERNITY 90, PATERNITY 30, UNPAID), the 19 PK salary components (BASIC … NET) and the PK 2025-26 salaried slab table (UNVERIFIED, and already past its `effectiveTo`: payroll needs a current table the organization enters). A test keeps it equal to `HrmSeedData`.
- A row with the pack's code whose values already equal the pack's (HRM's own first-use seed) is adopted as seeded; a different one is the organization's own and is left alone.

## WAREHOUSE (Inventory zones, D-21)
```json
{ "zoneTypes": [ { "code": "RECEIVING", "name": "Receiving", "description": "…", "default": true } ],
  "defaultWarehouse": { "code": "WH-MAIN", "name": "Main Warehouse" } }
```
- Wizard input (`ctx.WizardInput`, not in the payload): `{ "warehouses": [ { "code", "name", "address?", "city?", "country?", "zones?": ["RECEIVING", …] } ] }`. With no input, `defaultWarehouse` is seeded with the zone types marked `default` (all when none marked).
- Natural keys: warehouse code; zone = warehouse code + zone type code.
- 4 zone types: RECEIVING, STORAGE, DISPATCH, QUALITY_HOLD (all default).

## DOC_TEMPLATES (Onboarding `DocumentTemplates`, D-25)
```json
{ "templates": [ { "templateType": "SalesInvoice", "name": "Standard Sales Invoice", "headerHtml": "…", "bodyHtml": null, "footerHtml": "…",
    "primaryColor": "#1F4E79", "showBankDetails": true, "bankDetailsText": null, "termsAndConditions": "…", "isDefault": true } ] }
```
- `templateType` = one of the 6 `DocumentTemplateTypes`; `primaryColor` `#RRGGBB`; HTML fields free (sanitised by TPL on save).
- Shipped packs set `bodyHtml: null` (the built-in QuestPDF layout is used with branding) and put header / footer / T&C in place; `bankDetailsText` null = none (task 250).
- With `bodyHtml: null`, `headerHtml` replaces only the letterhead block (one `<div>` per line); the built-in layout prints the title, number, date and party block itself, so the packs' headers do not repeat them (TPL-50).
- Placeholders: `{Token}` names owned by TPL in [TEMPLATE-PLACEHOLDERS.md](TEMPLATE-PLACEHOLDERS.md). The packs use only `{CompanyName}`, `{CompanyAddress}`, `{CompanyPhone}`, `{CompanyEmail}`, `{CompanyTaxId}` (TPL also offers `{DocumentTitle}` and footer-only `{PageNumber}`).
- Natural key: templateType (the seeded default of that type).

## EMAIL_TEMPLATES (Onboarding `EmailTemplates`, D-26)
```json
{ "templates": [ { "templateType": "InvoiceSent", "name": "Invoice sent", "subjectTemplate": "Invoice {InvoiceNumber} from {CompanyName}", "bodyHtml": "<p>Dear {CustomerName},</p>…", "isDefault": true } ] }
```
- `templateType` = one of the 7 `EmailTemplateTypes`; PayslipSent is seeded here and sent by HRM (D-93); the `afterA49` flag is gone from the packs (still accepted, ignored).
- `{Token}` placeholders, HTML-encoded on render; unknown tokens are left as typed. Names per TPL's TEMPLATE-PLACEHOLDERS.md (confirmed by TPL):
  - common: `{CompanyName}`, `{CompanyEmail}`, `{CompanyPhone}` (also `{CompanyAddress}`, `{CompanyTaxId}`, `{DocumentUrl}`);
  - InvoiceSent: `{CustomerName}`, `{InvoiceNumber}`, `{InvoiceDate}`, `{DueDate}`, `{TotalAmount}`, `{CurrencyCode}`, `{Message}` (also `{AmountDue}`, `{CustomerEmail}`, `{AmountInWords}`);
  - QuotationSent: `{CustomerName}`, `{QuotationNumber}`, `{QuotationDate}`, `{ValidUntil}`, `{TotalAmount}`, `{CurrencyCode}`, `{Message}`;
  - POToSupplier: `{SupplierName}`, `{PoNumber}`, `{PoDate}`, `{DeliveryDate}`, `{TotalAmount}`, `{CurrencyCode}`, `{Message}`;
  - PaymentReminder: `{CustomerName}`, `{InvoiceNumber}`, `{DueDate}`, `{AmountDue}`, `{CurrencyCode}`, `{DaysOverdue}` (A48 dunning keeps its own templates);
  - PaymentReceived: `{CustomerName}`, `{PaymentNumber}`, `{PaymentDate}`, `{AmountPaid}`, `{CurrencyCode}`, `{InvoiceNumber}`;
  - WelcomeEmail: `{UserName}`, `{FirstName}`, `{OrganizationName}`, `{LoginUrl}`, `{AcceptInviteUrl}`;
  - PayslipSent (sent by HRM, D-93): `{EmployeeName}`, `{PayPeriod}` (e.g. October 2026), `{NetPay}` (N2, e.g. 108,600.00), `{CurrencyCode}`, plus the company placeholders.
- Natural key: templateType.
