# Addendum 35 — task tracker (Multi-Currency)

> **2026-10-08: COMPLETE.** All 7 agents have reported and REV has signed off. QA is green: 6 A35 host classes, 28/28, plus 42 A29–A34 regression classes. Coverage is in `QA-COVERAGE.md`. QA's last low finding is fixed: an unknown currency now reads "The selected currency is not an active currency…" instead of a raw GUID, in the Demand, Finance, Suppliers and Tenancy services. Before deploying, follow the deploy checklist below.

Source: `Documents/SMS_Task_Register_Addendum_35_Checklist.md` (SMS-TR-ADD-035, 62 tasks), received 2026-10-07 with the
instruction "open multiple agents whatever required". Spec: `Documents/SMS_FSD_Addendum_35.md`. **This file is the source of
truth for status; each owner updates only their own rows** (Status: TODO / WIP / DONE + one-line evidence).

Read first: `ADDENDUM-35-ANALYSIS.md` (reality check §1, D-2 design §2, decisions §3, migrations §4, split §5), then the spec
and register, then `API-CONTRACT.md` (CUR, wave 0).

## Rules (`docs/sales-preorder/AGENT-RULES.md` applies, with these A35 overrides)
- Scratch base: `C:\Users\ILYAS~1.QAD\AppData\Local\Temp\claude\d--Supply-System-SupplyChain-SupplyChainFrontend\e0158e3b-20b9-4fbd-ba5a-6e4b24969eb5\scratchpad\a35\<role>\`.
  Checkpoints: `…\scratchpad\checkpoints\a35-<role>.md` (one line per finished task).
- **Never build SMS.API or into `src\SMS.API\bin`** (the user's API may be running). Build test projects with `-o <scratch>`.
  **Never touch SMSGlobal** (Azure). LocalDB / in-memory only. Never `dotnet ef database update`.
- **Migration owners** (analysis §4). Need a column in someone else's context → message the owner. Finance has two owners
  in sequence: CUR first; FIN only after CUR messages "FINANCE MIGRATION IN". Migrations additive + idempotent (guarded SQL
  for data steps / index ops — every API start migrates the shared, drifted DB); verify up/down/up on LocalDB and
  `dotnet ef migrations has-pending-model-changes`.
- **Sole editors of hazard files:**
  - `src/SMS.Shared/Common/CurrencyContracts.cs` (new Shared contracts) and `API-CONTRACT.md`: CUR. Changes → message CUR.
  - `IExchangeRateProvider.cs`, `ExchangeRateMath`: CUR.
  - `SaleOrderService*.cs`, `SaleQuotationService*.cs`, `PurchaseOrder*Service*.cs`: DEM.
  - `SalesInvoiceService*.cs`, `CustomerPayment*Service*.cs`, `SupplierPayment*Service*.cs`, supplier-invoice approval code: FIN.
  - `IOrganizationCurrencyService.cs` + Tenancy impl: TEN.
  - `PermissionCodes.cs` + `AuthDataSeeder.cs` role grants: CUR (others message CUR).
  - Frontend `pages.routes.ts`, `app.menu.ts`: FE-SET. Shared money pipe/service: FE-SET (FE-DOC consumes).
- **Test-first:** watch each new test fail before implementing; report which. Targeted tests while iterating, one full module
  suite at the end. `tests/SMS.Integration.Tests`: one class per `dotnet test`.
- **Every new action gated** (`UngatedEndpointsRatchetTests`), no `[AllowAnonymous]`. Tenancy: explicit own-org filter on every
  write and by-id read (super admin included).
- **No sub-agents.** Blocked by a permission → tell the lead ("main"). Be token-efficient; short messages.
- "checkpoint and stop" → write the checkpoint and stop.

## Waves
- **Wave 0 (CUR, first ~hour):** `CurrencyContracts.cs` (ICurrencyService Guid-based, TransactionDomain, CurrencyRateNotFoundException
  → 400 mapping, ICurrencyUsageChecker, IPartnerCurrencyDefaults, IOrgCurrencyLookup (decimals/symbol), DTOs), the new permission
  codes, `API-CONTRACT.md` → message the lead **"CONTRACT READY"**.
- From the start, in parallel on their own modules: TEN, DEM (schema + entities; lock logic after contract), FIN (read-only
  study + tests until CONTRACT READY and FINANCE MIGRATION IN), FE-SET (scaffold pages against the contract draft).
- On CONTRACT READY the lead spawns FE-DOC, QA, REV and sends everyone the roster.

## Decisions
Adopted 2026-10-07 = `ADDENDUM-35-ANALYSIS.md` §3, D-1..D-21 (recommended defaults, per the user's standing instruction).
Amendments are added there with date + reason.

## Where the register is built differently

| Register says | Built as |
|---|---|
| P1-01/02 `lookups.Currencies` per org, INT id | D-1: global Guid identity kept; per-org config in `finance.org_currencies`; missing seed codes added to the global table by a Lookups startup seeder (rows only) |
| P1-06/07 `lookups.CurrencyRates`, INT ids | D-3: `finance.currency_rates`, Guid ids; legacy `finance.exchange_rates` frozen + converted |
| §3.5 base rate = 1.0 per org | D-2: one **rate currency** per org (rate 1.0); bases per domain reached through it |
| P2-01 `tenant.OrganizationCurrencySettings` INT | D-7: `tenancy.organization_currency_settings`, Guid, + `RateCurrencyId`; `Organization.BaseCurrency` kept = sale base |
| P2-06 BP `default_sale/purchase_currency_id` INT | D-9: `DefaultSaleCurrency` Guid new; `PreferredCurrency` reused as default purchase |
| P3-01 drop SQ `currency_code` | Nothing to drop — SQ already has `CurrencyId` |
| P3-01/02 `exchange_rate NOT NULL DEFAULT 1.0` | D-11: nullable = not locked yet |
| P3-02 PO `subtotal_base, tax_amount_base` | D-10: PO has no tax/subtotal → `TotalAmountBase`; PO gains `CurrencyId` (it had none) |
| P3-03 SI `amount_currency/amount_base` | D-10: existing `GrandTotal` / `BaseGrandTotal` |
| P3-03 `finance.Payments` | D-10: CustomerPayments + SupplierPayments (+ allocations); legacy Payments untouched |
| P3-09 lock "at ConfirmAsync"; invoice "POSTED" | D-12: SO CONFIRMED, PO APPROVED, SQ SENT, SI ISSUED, supplier invoice APPROVED, payment POSTED |
| P4-02/03 journal entries, `status = POSTED` | D-15: `finance.exchange_differences` register rows (no GL exists) |
| X-01 `currencies.read` … | D-16: UPPER_SNAKE codes |
| SAP S-4 (no triangulation), S-5 (missing rate never blocks) | Superseded by D-6, D-5 |

## Task status

| Task | Title | Owner | Status | Evidence |
|---|---|---|---|---|
| A35-P1-01 | Currencies table (M1) → `finance.org_currencies` | CUR | DONE | migration `20261007182414_A35_CurrencyCore` (guarded SQL, CHECKs/indexes per spec); LocalDB up/down/up/drift test `A35CurrencyCoreMigrationTests`; EF pending clean |
| A35-P1-02 | Currency entity & config | CUR | DONE | `Domain/CurrencyEntities.cs` (OrgCurrency), `Data/Maps/CurrencyMaps.cs` |
| A35-P1-03 | CurrencyService CRUD + BR-C1-01..05 | CUR | DONE | `OrgCurrencyService` + `IOrgCurrencyLookup`; `OrgCurrencyServiceTests` (T-C1-01..07, decimals locked by usage) |
| A35-P1-04 | Currencies API #1–3 | CUR | DONE | `CurrenciesController` api/currencies GET/GET{id}/POST/PUT, CURRENCY_VIEW/MANAGE, no feature gate; ratchet + anonymous classes green |
| A35-P1-05 | UI Currencies list | FE-SET | DONE | `pages/finance-setup/org-currencies` at Settings → Finance Setup → Currencies (CURRENCY_MANAGE\|FINANCE_SETUP_MANAGE\|INVOICE_VIEW); 8 Karma specs; Master Data entry relabelled "Currency Catalog" |
| A35-P1-06 | CurrencyRates table (M2) → `finance.currency_rates` | CUR | DONE | same migration (+ `exchange_differences` D-15 for FIN) |
| A35-P1-07 | CurrencyRate entity & config | CUR | DONE | `CurrencyRate` (DateOnly ranges, decimal(18,10)) |
| A35-P1-08 | Rate insertion SERIALIZABLE + BR-C2-* | CUR | DONE | `CurrencyRateService` (SERIALIZABLE + sp_getapplock per org/currency, close-out, gap fill, 409 on overlap); `CurrencyRateServiceTests` |
| A35-P1-09 | GetRateAsync / GetActiveRatesAsync | CUR | DONE | `CurrencyRateReader` + `CurrencyService`; T-C2-03..05 in `CurrencyServiceTests` |
| A35-P1-10 | Currency Rates API #4–9 | CUR | DONE | `CurrencyRatesController` list/active/{id}?date/history/POST/PUT, CURRENCY_RATE_VIEW/MANAGE |
| A35-P1-11 | UI Exchange Rates panel | FE-SET | DONE | `finance-setup/exchange-rates` rewritten over api/currency-rates (currency + month filters, ● Current, rate on a date); 8 Karma specs + access/sap-dates specs updated |
| A35-P1-12 | UI Add/Edit Rate dialog | FE-SET | DONE | same page: inverse, current/fixed end, "previous rate will be closed" notice from history, client overlap check (`currency-setup.shared.ts`, 15 specs) |
| A35-P1-13 | Seed (M8): currencies + rate-currency rate (CUR); org settings + document backfills (TEN, DEM, FIN in their migrations) | CUR / TEN / DEM / FIN | WIP | TEN part DONE: settings row backfilled in the Tenancy migration for orgs with a BaseCurrency (orgs without one keep no row → read as PKR). CUR part DONE: Lookups catalog seeder (18 codes) + `CurrencyBootstrapper` (org currencies, rate-currency SYSTEM row) |
| A35-P1-14 | Provisioning: seed on new org (`IOrganizationProvisionedHandler`) | TEN (settings) + CUR (currencies, rate) | WIP | TEN part DONE: `OrganizationCurrencySettingsProvisioningHandler` + startup backfill (order-independent of CUR's handler). CUR part DONE: `CurrencyBootstrapper` registered as `IOrganizationProvisionedHandler` |
| A35-P1-15 | Phase 1 unit tests T-C1-*, T-C2-* (T-C2-10 on LocalDB) | CUR | DONE | Finance suite 2163/2163 incl. `CurrencySqlServerTests` (T-C2-10 ×5 rounds, 2-instance bootstrap); T-C1-08..10 are FE-SET (pipe) |
| A35-P2-01 | Org currency settings table (M3) | TEN | DONE | Tenancy `20261007181803_A35_OrganizationCurrencySettings` (`tenant.organization_currency_settings`, guarded SQL); LocalDB up/re-run/down/up OK; has-pending none; `OrganizationCurrencySettingsMigrationTests` 5/5 (watched 4 fail) |
| A35-P2-02 | Entity & config | TEN | DONE | `OrganizationCurrencySettings` (PK OrganizationId, cascade FK), map, DbSet |
| A35-P2-03 | Settings service + immutability (via ICurrencyUsageChecker) + GetBaseCurrencyIdAsync(domain) | TEN | DONE | `OrganizationCurrencySettingsService` (BR-C3-02 400, BR-C3-03 409 per changed domain, rate currency 409, mirror to Organization.BaseCurrency, profile edit synced/409); `IOrganizationCurrencyService` + domain/settings members (default impls); legacy overload keeps null (no PKR); `OrganizationCurrencySettingsTests` 28/28 |
| A35-P2-04 | Settings API #10–11 | TEN | DONE | `OrganizationCurrencySettingsController` GET (`CURRENCY_VIEW`/`ORG_CURRENCY_SETTINGS_MANAGE`), PUT (`ORG_CURRENCY_SETTINGS_MANAGE`), tenant org only |
| A35-P2-05 | UI Currency Configuration page | FE-SET | DONE | `finance-setup/currency-configuration` (ORG_CURRENCY_SETTINGS_MANAGE\|FINANCE_SETUP_MANAGE\|INVOICE_VIEW): 3 bases, locks → disabled + reason tooltip, rate currency read-only, 4 GL codes; 8 Karma specs |
| A35-P2-06 | BP currency columns (M4) | TEN | DONE | Suppliers `20261007182045_A35_PartnerDefaultSaleCurrency` (guarded); LocalDB up/re-run/down/up OK; has-pending none; `PartnerCurrencyDefaultsMigrationTests` 2/2 (watched fail); host now runs `MigrateSuppliersSchema()` (Program.cs, migrate only) |
| A35-P2-07 | BP entity & DTOs | TEN | DONE | `DefaultSaleCurrency`; supplier + partner DTOs: `defaultSaleCurrencyId/Code`, `defaultPurchaseCurrencyId/Code` (= PreferredCurrency, alias kept, wins), `clearDefault*` flags, null = unchanged |
| A35-P2-08 | BP validation + IPartnerCurrencyDefaults | TEN | DONE | `PartnerCurrencyRules` (BR-C4-01, changed values only), `PartnerCurrencyDefaultsService` (explicit org); `PartnerCurrencyDefaultsTests` 13/13; Suppliers suite 238 pass / 3 skipped |
| A35-P2-09 | UI BP Currency tab | FE-SET | DONE | `suppliers/partner-currency-tab` in Partner detail (all types): default sale/purchase (blank = "Use org default: {code}", sends clear flags), Save gated SUPPLIER_EDIT\|SUPPLIER_MANAGE; 7 Karma specs |
| A35-P2-10 | Phase 2 unit tests T-C3-*, T-C4-* | TEN (T-C3, T-C4-01/02 resolver) + DEM (T-C4-03/04 on documents) | WIP | TEN part DONE: T-C3-01..04 in `OrganizationCurrencySettingsTests`, T-C4-01/02/04 resolver in `PartnerCurrencyDefaultsTests`. DEM part DONE: T-C4-03 (SO override) in `A35SaleOrderCurrencyTests`, T-C4-04 (PO) in `A35PurchaseOrderCurrencyTests`, T-C4-01/02 on the inquiry in `A35SaleInquiryCurrencyTests` |
| A35-P3-01 | Sale document columns (M5) | DEM | DONE | Demand migration `20261007181838_A35_MultiCurrencyOnDemandDocuments` (guarded, idempotent, all nullable); `A35DemandMigrationTests` up/down/up + drift re-run + D-11 backfill on LocalDB; `has-pending-model-changes` clean |
| A35-P3-02 | PO columns (M6) + PO CurrencyId | DEM | DONE | same migration: PO `CurrencyId` (backfill = purchase base) + rate/base/`TotalAmountBase`, lines base; foreign locked docs filled by `DemandCurrencyBackfill` (ICurrencyRatesReadyParticipant, REV-01) |
| A35-P3-03 | Finance document columns (M7) | FIN | DONE | Migration `20261007183205_A35_FinanceDocumentCurrency` (guarded ADDs, rates widened to (18,10), base totals (18,4); same-currency backfill in SQL; foreign-with-rate backfill = `FinanceDocumentRatesBackfill` ICurrencyRatesReadyParticipant). LocalDB down/up/down/up/re-run test green; has-pending clean |
| A35-P3-04 | Inquiry/quotation/SO entities & DTOs | DEM | DONE | entities + models per API-CONTRACT §6 (rate/base/lockedAt/*Base, currency codes) |
| A35-P3-05 | PO entity & DTOs | DEM | DONE | `CreatePoRequest`/`PatchPoRequest.currencyId`; detail/list/line base fields + codes |
| A35-P3-06 | SI + payment entities & DTOs | FIN | DONE | Entities/maps/DTOs per D-10 + E-06 detail fields (contract v1.4 §7); SI locks at ISSUE (sale base, issue date), customer payment at record, supplier payment at POST; D-14 currency defaults + mismatch 400. ReceivablesCurrencyTests 14, PayablesCurrencyTests 13 |
| A35-P3-07 | ICurrencyService full (To/FromBase, Convert, LockRate, domain bases) | CUR | DONE | `CurrencyService` (D-2 cross via rate currency, 10dp, D-13 rounding, tenant + explicit-org overloads); T-C6-01..05, T-C5-07/08 math |
| A35-P3-08 | Convert API #12 | CUR | DONE | POST api/currency/convert (CURRENCY_VIEW or CURRENCY_RATE_VIEW), effective range = intersection |
| A35-P3-09 | SO rate lock at CONFIRMED | DEM | DONE | `ConfirmHeldAsync` locks before reserving (missing rate → 400, nothing held), re-locked not inherited; `A35SaleOrderCurrencyTests` 15 (T-C5-01/02/04/05/06/07/09/10) |
| A35-P3-10 | PO rate lock at APPROVED | DEM | DONE | pre-check in `PurchaseOrderService.ApproveAsync` (400 before workflow), lock in `PoStatusHandler`; AUTO_SEND deficit PO locked at creation or created DRAFT; `A35PurchaseOrderCurrencyTests` 9 |
| A35-P3-11 | SQ rate lock at SENT | DEM | DONE | `SendAsync` locks (400 + stays DRAFT without rate); accepted counter price re-based at locked rate; copy drops rate; `A35SaleQuotationCurrencyTests` 7 |
| A35-P3-12 | Propagation customer→inquiry→SQ→SO; supplier→PO | DEM | DONE | `IPartnerCurrencyDefaults` → inquiry → SQ → SO; supplier → PO (all PO creation paths, split, clone); update without currency keeps it; `A35SaleInquiryCurrencyTests` 4 |
| A35-P3-13 | UI SO form/detail dual amounts | FE-DOC | DONE | `shared/doc-currency/*` (panel: currency, rate 4dp "locked d MMM yyyy", base; toggle; picker); SO detail: Total (AED)/(PKR) per line, header totals both (server base values, v1.2), Show-in toggle, D-5 "No exchange rate" dialog block + toast; SO form: picker = org-active currencies, customer `defaultSaleCurrencyId` default. Form edits drafts only, so base columns live on the detail. Karma `sale-order-detail.a35` 6, `sale-order-form.a35` 4, all SO specs green |
| A35-P3-14 | UI PO form/detail dual amounts | FE-DOC | DONE | PO detail: panel (locked at approval, purchase base), line + order total both currencies, toggle, D-5 toast on approve; PO create/edit: currency picker (purchase base → supplier `defaultPurchaseCurrencyId`/`preferredCurrency`), sent as `currencyId`; totals via `money` pipe. Karma `po-detail.a35` 4, `po-create.a35` 2, `po-edit.a35` 2 |
| A35-P3-15 | Existing SO/SQ/inquiry/PO APIs gain currency fields | DEM | DONE | request/response models only (no new actions); SO + PO lists carry `currencyCode` + base total |
| A35-P3-16 | Phase 3 integration tests T-C5-*, T-C6-* | QA | DONE | MultiCurrencyDocumentsE2ETests 10/10 (T-C5-01..10, T-C6-01..05, PO lock) + MultiCurrencySettingsE2ETests 4/4 + MultiCurrencySecurityE2ETests 3/3, LocalDB host 2026-10-08 |
| A35-P4-01 | ExchangeDifferenceService realized | FIN | DONE | `ExchangeDifferenceMath` (8 tests, T-C8-01..07 math) + `ExchangeDifferenceWriter` (register rows, reversal on bounce, post-twice guard). **Built as:** payable sign = accounting (owing a currency that fell = GAIN; spec T-C8-07 text says loss) |
| A35-P4-02 | ExchangeRevaluationJob unrealized | FIN | DONE | `ExchangeRevaluationService` (each doc's own base, idempotent per org+date under applock, missing rate → skipped) + monthly `ExchangeRevaluationJob` (0 23 L * *, HangfireTenantScope per org) + GET api/finance/exchange-differences, POST api/finance/exchange-revaluation/run. ExchangeRevaluationTests 8 |
| A35-P4-03 | Payment posting integration (customer + supplier) | FIN | DONE | Customer record/allocate + supplier post book realized per allocation/line; bounce reverses; ledgers untouched by FX |
| A35-P4-04 | Phase 4 unit tests T-C8-* | FIN (unit) + QA (host cross-check) | WIP | FIN unit part DONE (T-C8-01..08 in MultiCurrency/*; Finance suite 2164 green before E-06 tests, MultiCurrency 100 green). QA host cross-check DONE: MultiCurrencyExchangeDifferenceE2ETests 7/7 (T-C8-07 asserted as gain per 2026-10-08 amendment) |
| A35-P4-05 | E2E scenarios A/B/C (Appendix A) | QA | DONE | MultiCurrencyAppendixAE2ETests 2/2 (A+C, B no-setup org); MultiCurrencyMigrationE2ETests 2/2; matrix docs/multi-currency/QA-COVERAGE.md; A29–A34 host regression 42 classes green |
| A35-X-01 | Permission codes + role grants | CUR | DONE | 6 codes; admins all; FINANCE_SETUP_MANAGE holders + Finance Manager rate-manage/revaluation; every role views (startup, missing rows only) + new roles at creation (REV-09); Auth 272/272 |
| A35-X-02 | DI registration | each owner for their services | TODO | |

Extra (not in the register, required by the decisions):
| A35-E-01 | Re-implement `IExchangeRateProvider` over `currency_rates` (D-4); legacy table data step (D-3) | CUR | DONE | provider triangulates, same signature; legacy conversion in C# `CurrencyBootstrapper` (startup + provisioning, applock, logs unconverted pairs) — not SQL |
| A35-E-02 | Legacy `api/finance/exchange-rates`: reads from new table, writes removed when FE-SET ships the new page (D-17) | CUR + FE-SET | DONE | GET list/quote from currency_rates; POST/PUT/DELETE removed; HTTP + service tests rewritten; QA asked to move `FinanceSetupE2ETests` to api/currency-rates |
| A35-E-03 | Supplier invoice currency Guid + base by purchase domain + lock at approval (D-10, D-12) | FIN | DONE | CurrencyId from req/PO (D-14)/code; lock at approval date vs PURCHASE base, 400 when foreign & no rate; PayablesCurrencyTests |
| A35-E-04 | QuickBooks preflight: purchase base ≠ QBO home currency → clear refusal (D-19) | FIN | DONE | Preflight PURCHASE_BASE_CURRENCY (Warn, row only when purchase base ≠ sale base) + bill error PURCHASE_BASE_NOT_HOME (revalidatable). 6 new tests; Integration module 978/978 |
| A35-E-05 | Shared `MoneyService` / `money` pipe (D-21) | FE-SET | DONE | `shared/money/money-format.ts`, `services/money.service.ts`, `shared/money/money.pipe.ts`; T-C1-08..10 + REV-03/04 (org-keyed cache, reload on tenant) Karma-tested |
| A35-E-06 | Invoice + payment screens show currency, locked rate, base amount, FX difference; Exchange Differences list + "Run revaluation" | FE-DOC | DONE | `pages/finance/exchange-differences` (filters kind/side/date-only range, links to invoice/payment, signed base diff, "Run revaluation" dialog gated `EXCHANGE_REVALUATION_RUN` with totals + skipped) + `exchange-difference.service.ts` (route/menu by FE-SET); sales invoice, supplier invoice (`supplierPayments[]`), customer + supplier payment details: currency panel (rate locked at issue/approval/posting), amount in base, per-payment/allocation/line `exchangeDifference` + total (v1.4 §7), D-5 "No exchange rate" toast on issue/approve/post; inquiry form currency (customer default), quotation form customer default + org-active currencies only (also the inquiry's create-quotation dialog; doc's own currency kept), quotation detail locked rate + line base column; SO/PO/supplier-payment lists show currency + base total. Karma a35 specs: exchange-differences 5, SI 4, supplier invoice 4, CP 3, SP 4, SQ detail 4, SQ form 2, inquiry form 1; full suite 2346/2346; `ng build` OK |

## Deploy checklist (before the first API start on SMSGlobal; the lead tells the user)
1. **Suppliers migrate-only startup hook** (new in A35; `Program.cs`, after `UseTenancyModule`). `dbo.__EFMigrationsHistory` on SMSGlobal must already hold the 8 earlier Suppliers ids, `20260518044609_InitialCreate` … `20260919072828_BusinessPartnerEntityRename`. Otherwise `Migrate()` re-runs InitialCreate and startup fails.
   - Evidence so far: on 2026-09-21, `dotnet ef migrations list` for every context showed nothing pending. To re-confirm, run the read-only `dotnet ef migrations list --project src\SMS.Modules.Suppliers --context SuppliersDbContext --no-build`.
2. The A35 migrations reach SMSGlobal on the next API start, in Program.cs order. The data that needs rates comes from Finance's `CurrencyBootstrapper` at startup: org currencies, the rate-currency row, the legacy rate conversion and the foreign-document rate backfill.

## Roster (filled by the lead)
See memory `a35-agent-roster` for agent ids; the lead keeps it current.
