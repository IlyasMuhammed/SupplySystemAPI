# Addendum 35 — QA coverage matrix (T-C1..T-C8)

Owner: QA. Every FSD §13 scenario → the test that proves it. **Owner** = the owner's unit/module test (test-first);
**Host** = QA's real-host LocalDB test in `tests/SMS.Integration.Tests/MultiCurrency/` (run one class per `dotnet test`).

Host classes (abbrev.): `MultiCurrencyDocumentsE2ETests` (MCD, P3-16), `MultiCurrencyExchangeDifferenceE2ETests` (MCX, P4-04),
`MultiCurrencyAppendixAE2ETests` (MCA, P4-05), `MultiCurrencySecurityE2ETests` (MCS), `MultiCurrencySettingsE2ETests` (MCT),
`MultiCurrencyMigrationE2ETests` (MIG). Owner test files: CUR `tests/SMS.Modules.Finance.Tests/MultiCurrency/{OrgCurrencyServiceTests,
CurrencyRateServiceTests, CurrencyServiceTests, CurrencySqlServerTests}`; FIN `…/MultiCurrency/{ExchangeDifferenceMathTests,
ReceivablesCurrencyTests, PayablesCurrencyTests, ExchangeRevaluationTests}`; DEM `tests/SMS.Modules.Demand.Tests/A35*`; TEN
`tests/SMS.Modules.Tenancy.Tests/OrganizationCurrencySettingsTests`, `tests/SMS.Modules.Suppliers.Tests/PartnerCurrencyDefaultsTests`;
FE-SET `SupplyChainFrontend/src/app/shared/money/money-format.spec.ts`, `services/money.service.spec.ts`.

## C1 — Currencies
| # | Owner test | Host test |
|---|---|---|
| T-C1-01 create MXN | CUR `T_C1_01_a_new_code_is_added_to_the_catalog_and_the_org` | MCD `T_C5_10`, MCS isolation (MXN via `POST api/currencies`) |
| T-C1-02 code length | CUR `T_C1_02_03_bad_codes_are_400` | — |
| T-C1-03 lowercase | CUR `T_C1_02_03_bad_codes_are_400` | — |
| T-C1-04 duplicate → 409 | CUR `T_C1_04_05_…` | MCS `Another_orgs_…` |
| T-C1-05 same code, two orgs | CUR `T_C1_04_05_…` | MCS `Another_orgs_…` |
| T-C1-06 deactivate unused | CUR `T_C1_06_07_…` | MCT (GBP deactivated) |
| T-C1-07 deactivate base → 400 | CUR `T_C1_06_07_…` | MCS `T_C3_03_and_04_…` |
| T-C1-08..10 JPY / BHD / CHF-after display | FE-SET `money-format.spec.ts`, `money.service.spec.ts` | — (display only) |

## C2 — Exchange rates
| # | Owner test | Host test |
|---|---|---|
| T-C2-01 first rate open-ended | CUR `T_C2_01_and_09_…` | every class (`AddRateAsync`) |
| T-C2-02 new rate closes previous | CUR `T_C2_02_and_11_…` | MCD `T_C5_03_05`, `T_C6_01_to_04`; FinanceSetupE2ETests |
| T-C2-03 lookup exact date | CUR `T_C2_03_04_…` | MCD `T_C5_03_05` (`GET api/currency-rates/{id}?date=`) |
| T-C2-04 lookup within range | CUR `T_C2_03_04_…` | MCD `T_C6_01_to_04` (277.50 on day −4) |
| T-C2-05 no rate → exception | CUR `T_C2_05_no_rate_throws_the_D5_400_message` | MCD `T_C5_10`, `T_C6_01_to_04` (exact 400 text) |
| T-C2-06 overlap → 409 | CUR `T_C2_06_…` | FinanceSetupE2ETests (same day 409, PUT onto January 409) |
| T-C2-07 rate-currency rate immutable | CUR `T_C2_07_…`, `The_system_row_cannot_be_edited` | FinanceSetupE2ETests |
| T-C2-08 negative rate | CUR `T_C2_08_…` | FinanceSetupE2ETests |
| T-C2-09 inverse computed | CUR `T_C2_01_and_09_…` | — |
| T-C2-10 concurrent insert (SQL Server) | CUR `CurrencySqlServerTests.T_C2_10_…` (LocalDB) | — |
| T-C2-11 no gap | CUR `T_C2_02_and_11_…` | MCD `T_C6_01_to_04` (chain −20/−6/−2) |

## C3 — Org currency config
| # | Owner test | Host test |
|---|---|---|
| T-C3-01 all three same | TEN `T_C3_01_…` | MCA `Scenario_B` (never-configured org reads all PKR) |
| T-C3-02 purchase base USD | TEN `T_C3_02_…` | MCD `T_C6_05`, MCA `Scenario_A_and_C` |
| T-C3-03 change base with confirmed SO → 409 | TEN `T_C3_03_…` | MCS `T_C3_03_and_04_…` |
| T-C3-04 change base, no SOs | TEN `T_C3_04_…` | MCS `T_C3_03_and_04_…` |
| (REV-07) rate currency read-only | TEN `The_rate_currency_is_read_only_…` | MCT `The_rate_currency_is_read_only_…` |

## C4 — Business-partner defaults
| # | Owner test | Host test |
|---|---|---|
| T-C4-01 customer AED → inquiry AED | DEM `A35SaleInquiryCurrencyTests`, TEN `PartnerCurrencyDefaultsTests` | MCA `Scenario_A_and_C` |
| T-C4-02 no default → sale base | DEM `A35SaleInquiryCurrencyTests`, TEN | MCA `Scenario_B` (SO without currency → PKR) |
| T-C4-03 override on document | DEM `T_C4_03_an_explicit_currency_overrides_…` | — |
| T-C4-04 supplier USD → PO USD | DEM `T_C4_04_…`, TEN `PartnerCurrencyDefaultsTests` | MCD `PO_in_a_foreign_currency_…`, MCA `Scenario_A_and_C` |
| BP API (alias, null = unchanged, clear flags, BR-C4-01) | TEN `PartnerCurrencyDefaultsTests` | MCT `Partner_default_currencies_…` |

## C5 — Dual-amount storage (P3-16)
| # | Owner test | Host test |
|---|---|---|
| T-C5-01 SO AED → 457,800 | DEM `T_C5_01_…` | MCD `T_C5_01_and_04`, MCA |
| T-C5-02 SO in base → rate 1, no lookup | DEM `T_C5_02_…` | MCD `T_C5_02` (base EUR with **no EUR rate at all**), MCA `Scenario_B` |
| T-C5-03 SQ locked at SENT | DEM `T_C5_03_…` | MCD `T_C5_03_05`, MCA |
| T-C5-04 SO locked at CONFIRMED | DEM `T_C5_01_…` (lock date) | MCD `T_C5_01_and_04`, `T_C5_03_05` |
| T-C5-05 immutable after lock | DEM `T_C5_05_…` | MCD `T_C5_03_05` (PUT correction of the covering row) |
| T-C5-06 SQ → SO, re-lock at confirm | DEM `T_C5_06_…` | MCD `T_C5_06` (SO 317.00 vs SQ 316.48) |
| T-C5-07 JPY rounding | DEM `T_C5_07_…`, CUR `T_C5_07_JPY_and_BHD_…` | MCD `T_C5_07_and_08` |
| T-C5-08 BHD rounding | CUR `T_C5_07_JPY_and_BHD_…` | MCD `T_C5_07_and_08` — **spec arithmetic: 498.750 × 737.47 = 367,813.1625 → 367,813.16** |
| T-C5-09 grand total base = Σ lines (v1.2) | DEM `T_C5_09_…` | MCD `T_C5_09` |
| T-C5-10 no rate at lock → 400 | DEM `T_C5_10_…`, `Sending_without_a_rate_…`, `Approving_a_foreign_currency_PO_without_a_rate_…` | MCD `T_C5_10` (SO confirm + SQ send), `PO_in_a_foreign_currency_…` (PO approve); SAP PurchaseFlow (supplier-invoice approve) |

## C6 — Currency service (P3-16)
| # | Owner test | Host test |
|---|---|---|
| T-C6-01 direct | CUR `T_C6_01_…` | MCD `T_C6_01_to_04` |
| T-C6-02 cross via PKR | CUR `T_C6_02_…` | MCD `T_C6_01_to_04` — **spec says 241.08; D-13 gives 241.09** (1000 × 76.30 / 316.48 = 241.0896) |
| T-C6-03 same currency | CUR `T_C6_03_…` | MCD `T_C6_01_to_04` |
| T-C6-04 historical | CUR `T_C6_04_…` | MCD `T_C6_01_to_04` |
| T-C6-05 base per domain | CUR `T_C6_05_…`, TEN `A_stored_row_answers_per_domain_…` | MCD `T_C6_05` (API + `ICurrencyService` + `IOrganizationCurrencyService` from the host's DI) |

## C7 — Exchange differences (P4-04 host cross-check)
| # | Owner test | Host test |
|---|---|---|
| T-C8-01 realized gain 1,582.50 | FIN `ExchangeDifferenceMathTests`, `ReceivablesCurrencyTests` | MCX `T_C8_01`, MCA (+900) |
| T-C8-02 realized loss −2,900 | FIN same | MCX `T_C8_02` |
| T-C8-03 same rate → 0 | FIN same | MCX `T_C8_03_and_04` (payment difference 0; no non-zero register row) |
| T-C8-04 same currency → none | FIN `ReceivablesCurrencyTests` | MCX `T_C8_03_and_04`, MCA `Scenario_B` |
| T-C8-05 partial +800 | FIN same | MCX `T_C8_05` (balance AED 6,000 untouched) |
| T-C8-06 unrealized AR +2,600 | FIN `ExchangeRevaluationTests` | MCX `T_C8_06_to_08` (7130, idempotent re-run) |
| T-C8-07 unrealized AP | FIN `ExchangeRevaluationTests`, `PayablesCurrencyTests` | MCX `T_C8_06_to_08` — **amended 2026-10-08: payable sign = booked − settled → GAIN +5,500 (7130), not the spec's loss** |
| T-C8-08 same-currency skipped | FIN `ExchangeRevaluationTests` | MCX `T_C8_06_to_08`, MCA `Scenario_B` |
| (extra) supplier payment realized, payable sign | FIN `PayablesCurrencyTests` | MCX `Supplier_payment_in_USD_…` (−1,950, 7120) |
| (extra) cross-currency allocation → 400 (D-14) | FIN | MCX `Allocating_a_payment_…` |

## Appendix A (P4-05)
| Scenario | Host test |
|---|---|
| A — sale base PKR / purchase base USD, AED customer: inquiry → SQ (send) → SO (confirm) → SI (issue) → payment at 76.45 → +900 gain row | MCA `Scenario_A_and_C_…` |
| B — single-currency org created through the API, **no currency setup at all**: PO, SO, SI, customer payment, supplier invoice + payment at rate 1, no differences, revaluation writes nothing | MCA `Scenario_B_…` |
| C — PO in USD, purchase base USD → rate 1, base = amounts | MCA `Scenario_A_and_C_…` |

## Cross-cutting
| Check | Test |
|---|---|
| Every new action gated; viewer vs manager; anonymous 401 | MCS `Every_new_action_is_gated_…`; `Security/UngatedEndpointsRatchetTests`, `Security/AnonymousEndpointsTests` |
| Cross-org isolation (404 incl. super admin; lists; revaluation scope; rate/convert on a foreign org's currency) | MCS `Another_orgs_…`; CUR/TEN owner `Another_organizations_…` tests; `CrossOrganizationReversalE2ETests` (new org gets its own currencies) |
| New org via the API → provisioning (org currencies, SYSTEM row, settings fallback) | MCT `An_org_without_BaseCurrency_…`, MCA `Scenario_B` |
| Migrations from empty in host order + re-run | MIG `Fresh_database_…` (Finance via model + Down: its pre-A35 chain does not replay from empty — pre-existing) |
| Pre-A35 DB with real data upgraded by the real startup; reboot with forgotten history | MIG `Pre_A35_database_…` (legacy rate conversion incl. reciprocal + skipped pair + soft-deleted row; D-11 backfill PKR → 1, AED → 76.30 after conversion (REV-01); PO currency; invoice ids; Suppliers column; no duplicates on reboot) |

## Status log
- 2026-10-07 — matrix drafted; host classes written against API-CONTRACT v1.2.
- 2026-10-08 — owners landed; MCD 10/10, MCX 7/7, MCS 3/3, MCT 4/4, MIG 2/2; MCA 1/2 until CUR's provisioning fix (then re-run in the regression batch).
- 2026-10-08 (final) — all six A35 host classes green: MCD 10/10, MCX 7/7, MCA 2/2, MCS 3/3, MCT 4/4, MIG 2/2. A29–A34 host regression (42 classes, incl. ratchet/anonymous, CrossOrganizationReversal 4/4) green after A35-intended test updates.
