# SAP alignment: tax codes, exchange rates, reverse-don't-edit

Started 2026-10-01 at the user's request ("build the SAP-style improvements that fit the system").
Based on two read-only reality checks of the code (tax flow; currency + reversals). The user chose:
exchange-rate table, tax codes per line, and "everything else that fits".

## Decisions

| # | Decision | Why |
|---|---|---|
| S-1 | **Tax codes and exchange rates live in Finance** (`finance.tax_codes`, `finance.exchange_rates`), read by other modules through SMS.Shared contracts `ITaxCodeLookup` and `IExchangeRateProvider` | Finance migrates itself at startup; Lookups does not. Demand cannot reference Finance (cycle) |
| S-2 | **Per organization**, tenant-scoped | Each organization's accountant owns its codes and rates |
| S-3 | **Documents keep the code AND the rate as a snapshot** (`TaxPercent` stays the arithmetic source of truth) | A later rate change never changes an existing document; old rows (no code) keep working |
| S-4 | Exchange rates keyed by **ISO code strings**, latest rate on or before the date, else the reciprocal of the opposite pair, same currency = 1, **no triangulation** | Finance documents already store codes; predictable for accountants |
| S-5 | Rates are **snapshotted when a document is final** (sales invoice at issue, supplier invoice at approval): `ExchangeRate`, `BaseCurrencyCode`, base amount. A missing rate never blocks the document (null snapshot) | Base-currency reporting later; QuickBooks multicurrency now; no regression for orgs without rates |
| S-6 | **Reports stay per currency** in this phase (they already refuse to add currencies together) | Converting reports is a separate, larger change |
| S-7 | **Reverse, don't edit**: an issued sales invoice can be **cancelled** (unpaid only) — opposite customer-ledger and product-ledger entries, status CANCELLED, QuickBooks void. An approved supplier invoice can be **reversed** (unpaid only) — opposite supplier/master ledger entry, status Reversed. Approved invoices can no longer have tax or match status edited, nor be re-approved | Fixes double-debit on re-approval and silent un-approval; matches how accountants work |
| S-8 | **Three-way match compares the net Subtotal**, not the tax-inclusive total, with PO and GRN values (both tax-exclusive) | Any tax above 5% made every invoice a "Variance" |
| S-9 | **Line-level discount accounts: not built** | SMS has no general-ledger chart of accounts, and QuickBooks accepts one discount line per invoice |
| S-10 | **QuickBooks multicurrency**: foreign-currency documents/parties are sent when QuickBooks multicurrency is on, the currency is active there, and SMS has a rate for the document date (sent as QBO `ExchangeRate`). Otherwise the old home-currency rule applies | Removes D-1's blanket refusal where QuickBooks can hold the data |
| S-11 | **QuickBooks tax mapping by code first**, then by rate (old lines, other systems). New payload field `TaxCode` is omitted from stored JSON when null | Fixes D-4/D-10 at the root without re-sending already-synced invoices |

## Schema (done by the lead, migrations verified)

- Finance `20261001173747_SAP_TaxCodesExchangeRatesReversals`: tables `tax_codes`, `exchange_rates`; `sales_invoices` + `ExchangeRate, BaseCurrencyCode, BaseGrandTotal, CancelledAt, CancelledBy, CancellationReason`; `sales_invoice_lines` + `TaxCodeUuid, TaxCode`; `invoices` + `TaxCodeUuid, TaxCode, TaxPercent, ExchangeRate, BaseCurrencyCode, BaseTotalAmount, ReversedAt, ReversedBy, ReversalReason`. Additive only (nullable columns, new tables).
- Demand `20261001173751_SAP_SaleOrderLineTaxCode`: `sale_order_lines` + `TaxCodeUuid, TaxCode`. Up/down/up verified on LocalDB.
- Integration `20261001173755_SAP_TaxCodeMappingByCode`: `TaxCodeMappings.SourceTaxCode` + filtered unique indexes; guarded index drop. Up/down/up verified on LocalDB.

All three reach the shared database on the next API start.

## API contract (Finance)

| Method | Route | Permission | Body / result |
|---|---|---|---|
| GET | `/api/finance/tax-codes?side=SALES\|PURCHASE&includeInactive=` | signed in | `TaxCodeModel[]` |
| POST / PUT | `/api/finance/tax-codes` / `/{uuid}` | FINANCE_SETUP_MANAGE | `SaveTaxCodeRequest` → `TaxCodeModel` |
| POST | `/api/finance/tax-codes/from-rates-in-use` | FINANCE_SETUP_MANAGE | → `{ created, skippedRates }` |
| GET | `/api/finance/exchange-rates?from=&to=` | signed in | `ExchangeRateModel[]` |
| POST / PUT / DELETE | `/api/finance/exchange-rates` / `/{uuid}` | FINANCE_SETUP_MANAGE | `SaveExchangeRateRequest` → `ExchangeRateModel` |
| GET | `/api/finance/exchange-rates/quote?from=&to=&date=` | signed in | `ExchangeRateQuoteModel \| null` |
| POST | `/api/sales-invoices/{uuid}/cancel` | SALES_INVOICE_MANAGE | `{ reason }` → invoice detail |
| POST | `/api/finance/invoices/{uuid}/reverse` | INVOICE_PROCESS | `{ reason }` → invoice detail |

Frontend contract: `SupplyChainFrontend/src/app/services/finance-setup.service.ts` (written by the lead).

## Work packages

| WP | Scope |
|---|---|
| **A — Masters & setup** | TaxCode / ExchangeRate services, controllers, `ITaxCodeLookup` + `IExchangeRateProvider` implementations; "codes from rates in use"; Tax Codes and Exchange Rates settings pages + routes + menu; fix: organization edit wiping BaseCurrency + base-currency picker on the organization edit; currency delete/rename guard in Lookups |
| **B — Sales** | Sale-order line tax code (pick, default, snapshot rate, validator); price-rule currency converted into the order currency; sales-invoice line code snapshot; exchange-rate snapshot at issue; **cancel an issued invoice** (reversal entries, SO InvoicedQty, QuickBooks void); refuse cancelling a sale order that has live invoices; tax code on the invoice PDF; frontend SO form/detail, sales-invoice detail (code, base amount, Cancel) |
| **C — Purchase** | Supplier-invoice purchase tax code (TaxAmount computed from Subtotal); three-way match on Subtotal (S-8); exchange-rate snapshot at approval; guards (no tax/match-status edit or re-approval once Approved, no reject of an Approved one); **reverse an approved invoice**; frontend invoice create (tax code, real currency list) and detail (code, Reverse) |
| **D — QuickBooks** | Payload factories send `TaxCode`; mapping by code then rate (validator, builder, settings service + mappings tab); multicurrency with `ExchangeRate` (S-10) incl. provider mapper and preflight wording |

## Execution status — DONE (2026-10-02)

- Builders A, B, C, D: done. Reviewers A'–D', end-to-end tester, security auditor, frontend consistency
  checker and regression watcher: done — every finding fixed or listed under "Open" below.
- Bugs found and fixed after the builders: setup 10, sales 12, purchase 16 backend + 11 frontend,
  QuickBooks 5, end-to-end 3 + 1 gap, security 2 + Q1/Q2/F3, frontend consistency 7.
- Hardening added in review: supplier-invoice approve/reject/reverse/patch are atomic (row lock + one
  transaction across Finance and Demand); every `InvoicesController` action needs INVOICE_VIEW or
  INVOICE_PROCESS; super-admin requests can no longer touch another organization's invoices, API clients
  or QuickBooks pushes; notification e-mails HTML-encode their text; a tax code mapped in QuickBooks
  cannot be renamed (`ITaxCodeReferenceChecker`).
- End-to-end (real host, LocalDB): `tests/SMS.Integration.Tests/SapAlignment/` — FinanceSetup, SalesFlow,
  PurchaseFlow, QuickBooksTaxAndCurrency, CrossOrganizationReversal. Run one class per `dotnet test`.
- Final regression by the lead (2026-10-02, everything rebuilt; SMS.API host compiles):
  Finance 1655, Demand 539, Integration 972, Reports 942, Suppliers 216, Inventory 335, Warehouse 54,
  Logistics 1988, Material 115, Auth 117, Lookups 26, Procurement 2, WorkflowEngine 186 — all green;
  Tenancy 57/58 (the one failure, `UpdateFeatures_EnablingMir…`, predates this work). Host tests:
  QuickBooks journey + API key and all 5 SapAlignment classes green (14 tests). `ng build` OK, Karma 1530/1530.
- Startup safety: `has-pending-model-changes` clean for Finance, Demand, Integration, Tenancy, Lookups.

### Round 2 (2026-10-02/03) — DONE

User decision: **a supplier invoice is payable only once Approved** (SAP way). Fixed and tested:
- **A** — approvals/reversals of different invoices on one PO no longer lose `QtyInvoiced` (PO row lock after
  the invoice lock, `InvoiceRowLocks`).
- **B** — supplier payments (create/approve/post/cancel/bounce) and credit/debit notes (create/apply) take the
  invoice locks (ascending Id) → payment → note; no double posting, no lost PaidAmount, no deadlocks.
- **C** — three-way match per invoice at PO prices (`MatchedPoValue` = expected value); partial invoices match.
- **D** — the one-off `Two_reversals_at_once` failure was test-thread starvation, not a product bug.
- **E** — payables, aging, outstanding lists and Reports aging list Approved invoices only; both payment flows
  refuse unapproved invoices; old payments stay cancellable. Supplier-payment, legacy-payment, ledger and
  note controllers check permissions.
- Security work done alongside (critical items only, by user decision): AuthController/api/users privilege
  escalation and account takeover, password-reset brute force; anonymous Warehouses/GRN endpoints; error
  bodies leaking server paths/SQL; platform (all-organization) endpoints need real super-admin membership;
  attachments policy, path traversal and file-type allow-list; test hosts can no longer send real mail.
- Final regression by the lead (2026-10-03, everything rebuilt; SMS.API compiles): Finance 2120, Logistics
  1988, Integration 972, Reports 944, Demand 539 (+1 old skip), WorkflowEngine 389 (+1 skip), Inventory 335,
  Auth 232, Suppliers 218 (+3 skips), Material 115, Warehouse 54, Lookups 26, Procurement 2 — all green;
  Tenancy 57/58 (the old `UpdateFeatures_EnablingMir…` failure). Host tests, one class per run: 17 classes,
  85 passing (QuickBooks, SapAlignment incl. SupplierInvoiceHardening + SupplierNoteApplication, Security/*).
  `ng build` OK, Karma 1613/1613. `has-pending-model-changes` clean for 10 modules. Skips = documented open holes.
- Paused as a separate project (findings + plans): `docs/security/permission-sweep.md` (162 ungated actions),
  `docs/security/public-portals.md` (RFQ/SRO portal rows filed under SCM-DEMO — proven),
  `docs/security/attachments.md`.

### Open (deliberately not done here)

- Who may create supplier credit/debit notes: INVOICE_PROCESS or GOODS_RECEIVE or WAREHOUSE_TRANSFER today,
  so a storekeeper can reduce what is owed to a supplier — user decision.
- Supplier aging and outstanding payables ignore legacy (single-invoice) payments (pre-existing).
- A super admin can still read other organizations' payment lists (tenant filter only), as with invoices.
- Every new SMS tax code needs its own QuickBooks code mapping; until mapped, invoices using it are Blocked
  TAX_UNMAPPED (by design, S-11).
- Any USER_MANAGE holder can build a near-full custom role inside their organization (no "only grant what you
  hold" rule on api/roles, which would stop Org Admins assigning Supply Dept Admin) — user decision.

## Out of scope (recorded so they are not lost)

Converting reports to base currency; per-currency running balances on the customer/supplier ledgers;
partial sales credit notes (separate CREDIT_NOTE documents); voiding/deleting a reversed bill in
QuickBooks (it is flagged for the accountant); an automatic rate feed.
