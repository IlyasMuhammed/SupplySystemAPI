# Addendum 35 — Multi-Currency: reality check, decisions, design

Spec: `Documents/SMS_FSD_Addendum_35.md` (SMS-FSD-ADD-035 v1.1). Register: `Documents/SMS_Task_Register_Addendum_35_Checklist.md`
(62 tasks). Tracker / status: `ADDENDUM-35-TASKS.md` (this folder). Contract: `API-CONTRACT.md` (written by CUR, wave 0).

The spec was written as a green-field design. **Most of it already exists in another shape** (SAP alignment, 2026-10-01..03,
`docs/finance/SAP-ALIGNMENT-PLAN.md`). This file records what is really there and how each spec item is built on top of it.
Where the register says X and we build Y, the tracker's "Built as" table says so.

## 1. Reality check (verified in code 2026-10-07)

| Spec | What exists |
|---|---|
| C1 `lookups.Currencies` per org, INT id, formatting columns | `lookups.Currencies` is **global**, `Guid Id`, only `Name, Code, Symbol` (`SMS.Modules.Lookups/Domain/LookupEntities.cs`). Referenced by Guid from `SaleOrder.CurrencyId`, `SaleQuotation.CurrencyId`, `Organization.BaseCurrency`, `Supplier.PreferredCurrency`, Inventory price rules/rate cards. CRUD `api/lookups/currencies` (reads open, writes `SYSTEM_CONFIGURE`) + frontend Master Data → Currencies. **Lookups does NOT migrate at startup** (no `Database.Migrate` in `ILookupsModule`); Finance, Tenancy, Demand, Suppliers do. |
| C2 `lookups.CurrencyRates` date-ranged per currency vs base | `finance.exchange_rates` (SAP S-1/S-4): **currency pairs** (`FromCurrencyCode`, `ToCurrencyCode`, ISO strings), one `EffectiveDate`, lookup = latest on/before date, else reciprocal of the opposite pair, **no triangulation**, soft-delete. Read cross-module via `SMS.Shared.Common.IExchangeRateProvider` (code strings) by SO pricing, quotation pricing, sales-invoice issue snapshot, supplier-invoice approval snapshot, **QuickBooks** (`SMS.Modules.Integration`). Frontend Settings → Finance Setup → Exchange Rates. API `api/finance/exchange-rates` (`FINANCE_SETUP_MANAGE` writes). |
| C3 triple base currency | One `Organization.BaseCurrency` (Guid?, Tenancy). `IOrganizationCurrencyService.GetBaseCurrencyIdAsync(org)` (Shared, impl in Tenancy). `OrganizationSettings` 1:1 exists. No service/project-billing module exists. |
| C4 BP default currencies | `Supplier.PreferredCurrency` (Guid?) on `suppliers.BusinessPartners` ≈ default purchase. No default sale currency. |
| C5 SaleInquiry | No currency, no prices. |
| C5 SaleQuotation `currency_code` | **Wrong in spec:** already `CurrencyId` Guid (A32). Lines have `UnitPrice, DiscountPercent, TaxPercent, TaxAmount, LineTotal`. Status `SENT` exists (`SentAt`). |
| C5 SaleOrder | `CurrencyId` Guid, `Subtotal, TaxAmount, DiscountAmount, GrandTotal`; lines `UnitPrice, LineTotal`. Confirm in `SaleOrderService.ConfirmHeldAsync` (A33/A34 hazard file). Price-rule currency already converted into the order currency at line pricing via `IExchangeRateProvider`. |
| C5 PurchaseOrder | **No currency at all** (amounts implicitly PKR / org base). Header `TotalAmount` only — **no subtotal, no tax**; lines `UnitPrice, LineTotal, LineDiscountPct`. Statuses `DRAFT → APPROVED → SENT → PARTIALLY_RECEIVED → RECEIVED → PARTIALLY_INVOICED → CLOSED / CANCELLED` (workflow may add PENDING_APPROVAL). Schema is `demand`, not `procurement`. |
| C5 Sales invoice | `CurrencyCode` string + `ExchangeRate?`, `BaseCurrencyCode?`, `BaseGrandTotal?` snapshotted **at ISSUE** (spec says "POSTED" — it is `ISSUED`). Missing rate → null snapshot, never blocks (S-5). |
| C5 Supplier invoice (`finance.invoices`) | `Currency` string (default "PKR") + `ExchangeRate?`, `BaseCurrencyCode?`, `BaseTotalAmount?` snapshotted at **approval**. Not in the spec's column lists but needed by C7 payables. |
| C5 "finance.Payments" | Ambiguous. Three tables: legacy `Payments` (single invoice, read-only history now), **`SupplierPayments`** (+ `SupplierPaymentLines` allocations, **no currency column**), **`CustomerPayments`** (`CurrencyCode`) + `PaymentAllocation`. |
| C7 journal entries / GL accounts | **No general ledger in SMS.** Customer ledger and supplier ledger exist (balances in transaction currency). |
| Permissions `currencies.read` … | Codebase uses UPPER_SNAKE constants in `SMS.Shared/Authorization/PermissionCodes.cs`; every new action must be gated (ratchet test). |
| "React 18" | Frontend is **Angular 19 + PrimeNG** (`SupplyChainFrontend/`). |
| Org ids `INT`, `tenants.Organizations(org_id)` | Organizations are `Guid Id`; every tenant row is `Guid OrganizationId` + `ITenantScopedEntity`. |

## 2. The core design problem the spec leaves open — and its resolution

The spec stores **one rate per currency** ("units of base per 1 foreign", base = rate 1.0 forever) but defines **three**
bases per org. With sale base PKR and purchase base USD, "the" base for a USD rate is ambiguous. Appendix A resolves it
implicitly: all rates are against **one** reference currency (PKR, the one seeded with rate 1.0), and the purchase base is
reached through it ("convert cost to PKR using USD→PKR rate").

**Decision D-2:** each org has one **rate currency** (`RateCurrencyId` on the org currency settings): the currency whose rate
is permanently 1.0. Every rate is "units of the rate currency per 1 unit of X". Any conversion X → Y on date d is
`amount × rate(X,d) / rate(Y,d)`, with rate(rate currency) = 1. `ToBase(domain)` = conversion to that domain's base.
For the common single-base org (all three = PKR = rate currency) this is exactly the spec's arithmetic.

## 3. Decisions (adopted per the standing instruction: recommended defaults, recorded, reversible)

| # | Decision |
|---|---|
| D-1 | **Currency identity stays the global `lookups.Currencies` Guid.** Per-org configuration (decimal places, rounding, symbol position, active, display order, optional name/symbol override) is a new **`finance.org_currencies`** table: `(OrganizationId, CurrencyId Guid, Code snapshot)`, unique `(OrganizationId, CurrencyId)` and `(OrganizationId, Code)`. Reason: every existing document references the Guid; Lookups doesn't self-migrate. The 18 seed codes missing from the global table are inserted by an idempotent startup seeder in the Lookups module (rows only, no schema change). Global-currency CRUD (`api/lookups/currencies`) stays as is (reference data). |
| D-2 | **One rate currency per org** (§2). Seeded = `Organization.BaseCurrency` ?? PKR. Its rate row: 1.0, 2000-01-01 → 9999-12-31, `SYSTEM`, immutable (BR-C2-04). Changing the rate currency is out of scope (refused once any non-system rate exists). |
| D-3 | **New table `finance.currency_rates`** (date ranges, `EffectiveFrom`/`EffectiveTo` date, `Rate decimal(18,10)`, `InverseRate`, `Source MANUAL\|SYSTEM`, `Notes`, unique `(OrganizationId, CurrencyId, EffectiveFrom)`, CHECKs per spec, filtered index on `EffectiveTo = '9999-12-31'`). Insert/close-out under **SERIALIZABLE** + `sp_getapplock` per (org, currency). Legacy `finance.exchange_rates` is **kept, not dropped**, frozen read-only: a one-time idempotent data step converts its rows into ranges where one side is the rate currency (others reported in the migration log, not guessed). |
| D-4 | **`IExchangeRateProvider` keeps its signature** and is re-implemented over `currency_rates` with D-2 triangulation, so QuickBooks, SO/quotation pricing and invoice snapshots keep working unchanged. New Guid-based Shared contract **`ICurrencyService`** (spec §7.1 with `Guid` ids, no `orgId` parameter — tenant from context, plus explicit-org overloads for Hangfire) and `enum TransactionDomain { Sale, Purchase, Service }`. Implemented in Finance. |
| D-5 | **Missing rate blocks** (spec BR-C2-05, T-C5-10) **only when the document's currency ≠ its domain base**: SO confirm, PO approve, quotation send, sales-invoice issue, supplier-invoice approval, payment post → 400 "No exchange rate for {CODE} on {date}. Add one under Settings → Exchange Rates." Supersedes SAP S-5. Same-currency documents never look up a rate (BR-C5-04), so single-currency orgs are unaffected. |
| D-6 | **Triangulation allowed** (spec BR-C6-01). Supersedes SAP S-4. |
| D-7 | **Org currency settings live in Tenancy**: new `tenancy.organization_currency_settings` (1:1, `SaleBaseCurrencyId`, `PurchaseBaseCurrencyId`, `ServiceBaseCurrencyId`, `RateCurrencyId`, 4 GL account codes `nvarchar(20)` free text). `Organization.BaseCurrency` is kept and **kept equal to the sale base** (legacy readers: Integration `BaseCurrencyResolver`, SO). `IOrganizationCurrencyService` gains `GetBaseCurrencyIdAsync(org, TransactionDomain)` and `GetSettingsAsync`; the old overload = Sale. Missing row reads as all three = `Organization.BaseCurrency` ?? PKR (no hard dependency on the backfill). |
| D-8 | **Base immutability (BR-C3-03)** through a Shared `ICurrencyUsageChecker` (multi-registration): Demand reports confirmed SOs (status ≠ DRAFT/CANCELLED) / sent quotations for Sale and approved+ POs for Purchase; Finance reports issued sales invoices (Sale) and approved supplier invoices (Purchase). Changing a domain's base with usage → 409. Rate currency likewise. Same checker answers BR-C1-04 (currency in use). |
| D-9 | **BP defaults:** `Supplier.PreferredCurrency` **is** the default purchase currency (column reused; API exposes it as `defaultPurchaseCurrencyId`, old name kept as alias). New `DefaultSaleCurrency Guid?`. Resolution via Shared `IPartnerCurrencyDefaults` (Suppliers impl): partner default → org domain base. Must reference an **active org currency** (BR-C4-01). |
| D-10 | **Document columns** (all nullable where they are filled at lock time; Guid currency ids; `decimal(18,10)` rate; base amounts `decimal(18,4)`): SaleInquiry `CurrencyId`. SaleQuotation + `ExchangeRate`, `BaseCurrencyId`, `RateLockedAt`; lines `UnitPriceBase, DiscountAmountBase, TaxAmountBase, LineTotalBase`. SaleOrder + `ExchangeRate, BaseCurrencyId, RateLockedAt, SubtotalBase, TaxAmountBase, DiscountAmountBase, GrandTotalBase`; lines `UnitPriceBase, LineTotalBase`. **PurchaseOrder + `CurrencyId`** (backfill = org purchase base), `ExchangeRate, BaseCurrencyId, RateLockedAt, TotalAmountBase`; lines `UnitPriceBase, LineTotalBase` (**no subtotal/tax columns — POs have no tax**). SalesInvoice + `CurrencyId, BaseCurrencyId, ExchangeRateLockedAt` (spec `amount_currency` = existing `GrandTotal`, `amount_base` = existing `BaseGrandTotal` — no duplicate columns). Supplier invoice + `CurrencyId, BaseCurrencyId, ExchangeRateLockedAt`. CustomerPayment + `CurrencyId, ExchangeRate, BaseCurrencyId, AmountBase, ExchangeDifference`; `PaymentAllocation` + `ExchangeDifference`. SupplierPayment + `CurrencyCode, CurrencyId, ExchangeRate, BaseCurrencyId, AmountBase, ExchangeDifference`; `SupplierPaymentLines` + `ExchangeDifference`. Legacy `Payments`: untouched. |
| D-11 | **ExchangeRate null = not locked yet** (draft) — spec's "NOT NULL DEFAULT 1.0" would claim a rate nobody locked. Backfill: documents whose currency = their domain base → rate 1, base = amounts. Locked legacy documents in a foreign currency → rate from `currency_rates` at their lock date if one exists, else left null and listed by the migration's data step (no guessing). |
| D-12 | **Lock points:** quotation → SENT (sent date), SO → CONFIRMED (confirm date, **re-locked, not inherited from the quotation** — spec §6.3.5), PO → APPROVED, sales invoice → ISSUED, supplier invoice → APPROVED, payment → POSTED (payment date). After lock the rate never changes (BR-C5-06). |
| D-13 | **Rounding:** transaction amounts at the transaction currency's `DecimalPlaces`, base amounts at the base currency's, `MidpointRounding.AwayFromZero`. `ExchangeRateMath.Convert(amount, rate, decimals)` overload; the 2dp default stays for existing callers. Header base = sum of line base (BR, T-C5-09); tax/discount base = header amount × rate. |
| D-14 | **Propagation:** customer default → inquiry → quotation (from inquiry) → SO (from quotation) → sales invoice (from SO/delivery); supplier default → PO → supplier invoice (from GRN/PO) → supplier payment. Each step inherits, overridable until locked. A payment is in one currency; allocating it to an invoice in another currency → 400. |
| D-15 | **C7 without a GL:** a new `finance.exchange_differences` register (`Kind REALIZED\|UNREALIZED`, `Side RECEIVABLE\|PAYABLE`, source doc + allocation refs, transaction currency + amount, booked rate, settlement/revaluation rate, base currency, `DifferenceBase` (+gain/−loss), `AccountCode` from org settings, `PostedAt`/`RevaluationDate`). That row **is** the spec's "journal entry" (Dr/Cr recorded as account codes). Customer/supplier ledgers stay in transaction currency and are **not** touched by FX. Realized: computed per allocation at payment post (BR-C7-01..03, partial = allocated portion only). Unrealized: `ExchangeRevaluationJob` (Hangfire, monthly last day 23:00 org-local fallback UTC, plus manual "Run revaluation") revalues open foreign receivables/payables at the rate on the revaluation date vs booked rate; one row per open document per revaluation date, idempotent (re-run replaces that date's rows). |
| D-16 | **Permissions (UPPER_SNAKE):** `CURRENCY_VIEW` (every role — forms need it), `CURRENCY_MANAGE`, `CURRENCY_RATE_VIEW` (every role), `CURRENCY_RATE_MANAGE`, `ORG_CURRENCY_SETTINGS_MANAGE`, `EXCHANGE_REVALUATION_RUN`. Granted to System Admin, Org Admin; `CURRENCY_RATE_MANAGE` and `EXCHANGE_REVALUATION_RUN` also to roles holding `FINANCE_SETUP_MANAGE`. Seeded by code (role matched by code, not id — see the Supply Dept Admin incident). |
| D-17 | **Endpoints** as the spec (§10.1) with Guid ids: `api/currencies` (the **org** list, D-1), `api/currency-rates…`, `api/organization/currency-settings`, `api/currency/convert`, plus `POST api/finance/exchange-revaluation/run` and `GET api/finance/exchange-differences`. Legacy `api/finance/exchange-rates`: **reads kept** (served from the new table), **writes removed** once the new Exchange Rates page ships (FE-SET says when). |
| D-18 | **Service base:** stored, validated, exposed, unused (no service documents exist). Recorded residual. |
| D-19 | **QuickBooks** keeps comparing its home currency with the **sale** base; purchase documents in a purchase base ≠ QBO home currency are refused by preflight with a clear message. Recorded residual (no QBO change beyond that). |
| D-20 | **Supplier quotations (RFQ)** and procurement requisitions get no currency (not in spec). Recorded residual. |
| D-21 | **Display:** a shared frontend `MoneyService` / `money` pipe formatting with the org currency's symbol, decimals and symbol position (T-C1-08..10). Dual columns + "Show in X / Show in base" toggle on SO and PO detail/forms (spec §11.5). |

### Amendments
- **2026-10-07 (REV-07):** the rate currency is **read-only** in A35. Its source of truth is Finance's SYSTEM rate row. A `PUT api/organization/currency-settings` that changes `rateCurrencyId` returns 409.
- **2026-10-07 (REV-08):** the new Suppliers migrate-only startup hook is defensive. If `suppliers.BusinessPartners` exists but the history lacks `20260518044609_InitialCreate`, it logs a critical error and skips `Migrate()` instead of crashing startup. The user also confirms the history before deploy (tracker deploy checklist).
- **2026-10-07 (REV-06):** a profile base change on an org with no settings row first materialises the row from the current snapshot, then changes only the sale base.

- **2026-10-08 (FIN, spec correction):** for **payables** the sign is booked − settled: a fall in the currency you owe is a **gain**. Spec T-C8-07 (open AP USD 10,000 at 278.05, now 277.50 → "loss PKR 5,500") applies the receivables formula to a payable. That case is built and tested as a **gain** of PKR 5,500. Receivables keep settled − booked (T-C8-01/06 unchanged).

### Accepted residuals (REV, 2026-10-07)
- Unrealized rows are **cumulative against the booked rate**, one row per revaluation date (D-15). A report must take the latest date per document and never sum across months. Realized differences ignore earlier unrealized rows.
- A backdated revaluation run uses **today's** open balance.
- Applying an advance supplier payment to invoices later books **no** exchange difference.
- A legacy `IExchangeRateProvider` pair where neither side is the rate currency now returns null (D-3; logged).
- There's a race between a base-currency change and a domain's first locked document. Each document stores its own `BaseCurrencyId`, and downstream code uses the stored base.
- Partner by-id reads rely on the EF tenant filter. This is pre-existing super-admin behaviour, outside A35.
- QuickBooks still pushes `PreferredCurrency`, which now means the default *purchase* currency, as the customer currency. Pre-existing, out of scope.
- Locked legacy documents in a foreign currency with no rate at their lock date stay null. They are retried on every start and logged; nothing is guessed.
- The startup bootstrap costs about 12–15 round trips per org on every API start, run sequentially before `app.Run`. On Azure that's roughly 0.3–0.7 s per org, i.e. about 10–30 s at 50 orgs. A second instance waits per org. If the org count grows, add a per-org "nothing pending" marker or move the loop into a hosted service after `app.Run`; provisioning stays inline.
- The customer payment form, supplier payment create and supplier invoice create have no currency picker. They still send a currency code, which the server accepts and defaults per D-14. Payment detail shows the locked rate without a lock date, because the contract has none for payments.
- **3-decimal currencies (BHD, OMR, KWD):** transaction amounts on sale and purchase documents are stored at 2 decimals, because the existing columns are `decimal(18,2)`. Rounding uses the currency's decimals capped at 2. Spec T-C5-08 (BHD at 3dp) is therefore only partly met; base amounts are fine. Fixing it means widening the amount columns on SMSGlobal, a separate change.
- If a rate disappears between the PO approval pre-check and the workflow's status update, the PO ends APPROVED with no rate. This is logged and fixed by the startup backfill.
- **New-org provisioning (fixed 2026-10-08):** `TenancyRepository.CreateOrganizationWithAdminAsync` left its committed transaction attached to `TenancyDbContext`, so the A35 provisioning handlers failed silently.
  - Fixed by CUR (`UseTransaction(null)`). The lead moved it into a `finally` so it also runs on failure.
  - Any org created before the fix is repaired by the startup backfills.
  - Residual: the Auth and Workflow contexts that joined the same transaction stay attached for the rest of that request. Nothing reads them afterwards today; a future handler that does would fail the same way.
- If SMSGlobal's Suppliers history is incomplete, the Suppliers migration is skipped with a Critical log. Every partner query would then fail on the missing `DefaultSaleCurrency`, so the pre-deploy check is mandatory.

## 4. Migrations (one owner per DbContext; all additive + idempotent; verified up/down/up on LocalDB + `has-pending-model-changes`)

| Context | Owner | Content |
|---|---|---|
| Finance (1st) | CUR | `org_currencies`, `currency_rates`, `exchange_differences`; legacy `exchange_rates` → ranges data step |
| Finance (2nd, after CUR's is in) | FIN | document columns of D-10 for sales invoices, supplier invoices, customer/supplier payments + allocations; backfill |
| Tenancy | TEN | `organization_currency_settings` + backfill from `Organization.BaseCurrency` |
| Suppliers | TEN | `DefaultSaleCurrency` on BusinessPartners |
| Demand | DEM | inquiry/quotation/SO/PO columns of D-10; PO `CurrencyId` backfill; base backfill |
| Lookups | — | **none** (rows only, via startup seeder owned by CUR) |

**Two agents never add a migration to the same context at the same time.** FIN starts its Finance migration only after CUR
reports "FINANCE MIGRATION IN".

## 5. Agent split

| Role | Owns | Register tasks |
|---|---|---|
| CUR | Finance currency core: org currencies, rates, ICurrencyService, IExchangeRateProvider re-impl, Lookups seeder, permissions, currencies/rates/convert APIs, **Shared contracts + API-CONTRACT (wave 0)** | P1-01..04, P1-06..10, P1-13 (currency + rate part), P1-15, P3-07, P3-08, X-01, X-02 (its services) |
| TEN | Tenancy settings + provisioning + Suppliers BP defaults | P1-13 (settings part), P1-14, P2-01..04, P2-06..08, P2-10 |
| DEM | Demand: inquiry, quotation, SO, PO currency + lock + propagation + usage checker | P3-01, P3-02, P3-04, P3-05, P3-09..12, P3-15 |
| FIN | Finance documents: invoices, payments, realized/unrealized FX, revaluation job, Finance usage checker, QuickBooks preflight | P3-03, P3-06, P4-01..03 |
| FE-SET | Angular: Currencies, Exchange Rates panel + dialog, Currency Configuration, BP Currency tab, MoneyService/pipe, menu/routes (sole editor of `pages.routes.ts` / `app.menu.ts`) | P1-05, P1-11, P1-12, P2-05, P2-09 |
| FE-DOC | Angular: inquiry/quotation/SO/PO/invoice/payment currency + dual amounts + toggle + FX difference display | P3-13, P3-14 |
| QA | Host/integration + E2E (LocalDB), coverage of every T-* scenario, ratchet/anonymous checks, migration checks | P3-16, P4-04 (cross-check), P4-05 |
| REV | Review of every owner's diff (correctness, tenancy, rounding, concurrency, migration idempotency) | — |

## 6. Test plan
Every T-C1..T-C8 scenario is covered by the owner's unit tests (test-first) and re-proven end-to-end by QA on LocalDB host
tests. Concurrency T-C2-10 needs a real SQL Server (LocalDB) test, not in-memory. QA also re-runs the A29–A34 host classes
touching SO confirm, quotation send, PO approve, invoice issue/approval and payments.
