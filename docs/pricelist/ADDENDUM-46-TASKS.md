# Addendum 46 - tasks tracker

## BE-2 — Finance / Shared (ICurrencyConverter, rate rules)

Reality: A35 already owns the exchange-rate model (`finance.currency_rates`: per-currency date RANGES, rate = units of the org rate/base currency per 1 X, Source MANUAL / API_* / SYSTEM, unique (org,currency,from)). The legacy pair table is frozen. `IExchangeRateProvider` already triangulates through the rate currency (the analysis note "no triangulation" is out of date); it was left unchanged. No new table, no migration (E-3).

| Ids | Status | Evidence |
|---|---|---|
| A46-0046..0063, 0071(M7), 0076, 0083, 0085..0087 (ExchangeRates table/entity/DTOs/enum/tenant filter) | satisfied-by-A35 | `Finance.CurrencyRate` + `CurrencyRatesController` (api/currency-rates), OrganizationId filter, `Source` strings instead of the int enum (MANUAL / API_SBP / API_ECB / API_OPENEXCHANGE / API_FOREX / SYSTEM); A35 `CurrencyRateServiceTests`; new `Another_organizations_rates_are_never_used` |
| A46-0089..0093 (repository + lookups) | satisfied-by-A35 | `CurrencyRateReader.FindAsync` (latest row covering the date), `CurrencyRateService.ListAsync/ForDateAsync`; duplicate check = ConflictException (A35 T-C2-06) |
| A46-0094, 0095, 0098, 0099, 0101..0103, 0106..0108, 0110, 0111 | satisfied-by-A35 | `ICurrencyRateService` (Insert/Update/List/Active/History/ForDate); Rate>0 + range + 10dp validation; InverseRate = RoundRate(1/rate) at 10dp (A35 chose 10dp, not the spec's 6); DI in IFinanceModule |
| A46-0096, 0104 (FEED/Api rows not updatable) | DONE (added) | `CurrencyRateService.UpdateAsync` refuses `API_*` rows with 400 "Auto-fetched exchange rates are read-only..."; tests `Feed_rows_cannot_be_updated`, `Manual_rows_can_still_be_updated` |
| A46-0097, 0105 (FEED rows not deletable) | satisfied-by-A35 | no delete operation exists on `ICurrencyRateService` / controller (rates are closed by range, never deleted); test `There_is_no_rate_delete_so_feed_rows_cannot_be_deleted` |
| A46-0100, 0109 (BR-34 manual over feed, same date) | DONE (added) | `CurrencyRateService.InsertAsync`: a MANUAL insert whose start date equals an `API_*` row's start takes that row over in place (becomes MANUAL, new rate); tests `BR34_*`, `A_manual_manual_clash_on_the_same_date_is_still_409`, `A_later_manual_rate_closes_a_feed_row_as_usual` |
| A46-0112..0115 (ICurrencyConverter + DTOs) | DONE | `src/SMS.Shared/Common/ICurrencyConverter.cs`: `ICurrencyConverter`, `CurrencyConvertRequest`, `CurrencyConvertResult`, `CurrencyRateLookup` (named to avoid clashing with A35 `CurrencyConversionResult`; Guid ids + ISO-code convenience instead of int ids) |
| A46-0116..0121 (same-currency, direct, inverse, cross, rounding, no-rate) | DONE | `src/SMS.Modules.Finance/Services/CurrencyConverter.cs` (rate(from)/rate(to) via the rate currency; `CurrencyConventions.RoundAmount`, default 4dp; failure = `Success=false` + message naming currency and date) |
| A46-0122 (DI) | DONE | `IFinanceModule`: `AddScoped<ICurrencyConverter>`; consumers resolve with GetService |
| A46-0123..0134 (tests) | DONE | `tests/SMS.Modules.Finance.Tests/MultiCurrency/CurrencyConverterTests.cs` (28 tests incl. theory cases) |
| TS-52..56 / C8 convert endpoint | DONE | `POST api/finance/convert-currency` (`FinanceConvertCurrencyController`): 200 + `result.success:false` on no rate, 400 on malformed input; A35 `POST api/currency/convert` unchanged |
| A46-0072..0079 (rollback scripts incl. M7 `DROP TABLE finance.ExchangeRates`), 0084 | N/A for Finance | no migration in Finance (E-3); M7 rollback must NOT be run (no such table) |

Residuals (BE-2):
- Rates are date RANGES (A35). A closed range (EffectiveTo set) leaves later dates without a rate until another row is entered, so "latest earlier rate" holds only for the open-ended/current row or a covering range. Provider and converter share this behaviour.
- Conversion rate is rounded to 10dp before multiplying (consistent with A35 locked rates); amount x rate is rounded once at the requested decimals.
- `RateDate` of a cross/foreign rate = the later start date of the two legs (rate-currency leg starts 2000-01-01).
- Same-currency with a currency id/code that is not an organization currency is a failed result, not a pass-through.
- Endpoint wraps the result in the standard `ApiResponse` envelope (`result.success`, `result.errorMessage`); the envelope's own `success` is true.
- There is no feed job writing `API_*` rows in this codebase; the BR-34/read-only rules are in place for when one exists.

## FE — Phase 5 UI (C1, C2, C3, C5, C6/C7, C10) — DONE

Files (all under `SupplyChainFrontend/src/app`):
- `services/purchase-pricing.service.ts` — `PriceSource.BlanketOrder=4` ("Blanket"), `poCurrencyId` on the resolve request (`currencyId` still sent), converted/mismatch fields on `ResolvePriceResult`, helpers `conversionTooltip`, `currencyMismatchText`, `price2/price4`, `samePrice` (4dp compare, replaces the A45 exact compare), `PricingSummary`.
- `services/demand.service.ts` — `orderDate` on create/patch/detail, PO line `originalCurrency*`/`exchangeRate*`/`currencyMismatchWarning`, convert endpoints typed with optional `pricingSummary`.
- `pages/demand/purchase-orders/po-line-pricing.ts` — an `orderDate` form control drives `setOrderDate()` (banner + Re-lookup All / Keep; silent while hydrating); resolve uses the PO order date; per-line `conversionInfo`; no-rate result => Manual, price 0, banner `No exchange rate available for {from} → {to} on {date}. Enter price manually.`; Create RFQ passes `sourcePO` (PO number; edit page only).
- `po-create`, `po-edit` (ts/html/scss) — Order Date picker (default today local, sent `yyyy-MM-dd`; edit disables it unless DRAFT and then does not send it), converted-price info icon + tooltip `Original: {code} {price} @ {rate} ({date})`.
- `po-detail` (ts/html/scss) — Order date row, unit price 2dp with 4dp tooltip, info icon, no-rate warning, "Blanket" source, PR→PO pricing-summary banner + `View Unpriced Lines` (yellow rows for Manual-source lines; also `?unpriced=1`).
- `pr-detail` (ts/html/scss) — single conversion forwards `pricingSummary` as router state to the PO page; split conversion shows the banner on the PR page and View Unpriced Lines opens the first PO highlighted.
- `pages/demand/quotations/quotation-create/` — `quotation-prefill.ts` (pure parser), component/html: supplier hint field, line-1 product/qty prefill, `Referenced from {sourcePO}`.
- `shared/supplier-pricelist/pricelist-dialog.component.ts` — a new price defaults its currency from the vendor's `defaultPurchaseCurrencyId` (fallback `preferredCurrency`, then org purchase base), on fixed vendor and on vendor pick; always editable; a hand-picked currency is never overwritten; edit never re-defaults.

Specs added: `po-create.a46.spec.ts`, `po-edit.a46.spec.ts`, `po-detail.a46.spec.ts`, `pr-detail.a46.spec.ts`, `quotation-create.a46.spec.ts` (incl. parser), `services/purchase-pricing.a46.spec.ts`, plus 8 cases in `pricelist-dialog.component.spec.ts`. Regression: pages/demand + pages/suppliers + shared = 256/256 green; `tsc --noEmit` clean for the app and spec configs.

Decisions / residuals (FE):
- **C6 verified, not rebuilt**: the supplier form already exposes the default purchase currency (A35 `partner-currency-tab`, supplier-detail/create `defaultPurchaseCurrencyId`).
- **RFQ vendor**: an RFQ has no vendor on create (suppliers are invited when it is sent) and the create API has no supplier field, so `vendorId`/`supplierId` pre-selects a "Suggested supplier" dropdown, shown only when the id is valid, **not persisted**. If BE adds a field, wire it in `quotation-create.component.ts onSubmit`. `sourcePO` is display-only (no FK, per spec). Product prefill uses `productId` (product uuid) + optional `variantUuid`; the title defaults to `RFQ - {product}` when empty.
- **PR→PO summary**: read as a top-level `pricingSummary` on the convert response (`result` stays the PO id/ids). Single conversion hands it over via `history.state` (lost on reload; the banner is then simply absent). "Unpriced" = line `priceSource` Manual (0/null).
- **No-rate on detail**: shown only if the line read carries `currencyMismatchWarning` (+ optional `pricingWarning`); the contract does not say BE persists it, so on a saved PO it normally appears only on create/edit (transient resolve state).
- **PO line read names assumed**: `originalCurrencyId`, `originalCurrencyCode?`, `originalCurrencyPrice`, `exchangeRateUsed`, `exchangeRateDate?` (code falls back to the org currency lookup by id; date omitted from the tooltip if absent).
- **C3**: inputs already allow 4dp (min 2 / max 4); the detail grid is the only read-only price grid. **PO print/export 2dp is server-side (PDF)** — the FE has no export of its own. Pricelist grid unchanged (2–4dp).
- The money pipe is given `decimals: 2/4` explicitly, so the 2dp rule holds for 3-dp currencies too, on unit prices only.
- Not done: no browser walk-through (specs only); dev server/API untouched.


## PERF — C11 resolver query plan (A46-0290..0302) — DONE (measure-only; patch handed to BE-1)

Doc: `docs/pricelist/ADDENDUM-46-QUERY-PLAN.md` (+ raw plans in `docs/pricelist/query-plans/`). Tests: `tests/SMS.Integration.Tests/Pricelist/ResolverPlanKit.cs`, `ResolverPlanTests.cs` (CI class + `[Trait("Category","Perf")]` full class; skip when LocalDB is unavailable).

| Task | Status | Evidence |
|---|---|---|
| A46-0290 seed 24k | DONE | `ResolverPlanKit.SeedAsync`; 26,800 rows (24,000 org A + org B copy + variant rows, mixed date basis, inactive rows) |
| A46-0291 STATISTICS IO/TIME | DONE | real EF SQL captured by interceptor and replayed; `query-plans/1-real-ef-query.txt` |
| A46-0292 actual plan XML | DONE | `query-plans/*.sqlplan` |
| A46-0293 Index Seek on IX_SupplierPricelists_Lookup | DONE - PASS | seek, no scan (real query and spec query) |
| A46-0294 0 key lookups | DONE - FAIL today / PASS with patch | real query selects the full entity -> 1 Key Lookup operator; projected + `UUID` INCLUDE -> 0 |
| A46-0295 reads <= 5 | DONE - FAIL today (21-27) / PASS with patch (4) | |
| A46-0296 < 1 ms warm | DONE - PASS server-side (0 ms); end-to-end resolver p50 0.84 ms, p95 1.2 ms | |
| A46-0297 Option A/B if OR scans | N/A | no scan; real SQL has no OR on PricingDateBasis (date filtering is in memory); spec SQL seeks in 3-4 reads. Option C (project + widen existing index) recommended instead |
| A46-0298 / 0299 filtered indexes OrderDateBasis / DeliveryDateBasis | NOT NEEDED | spec rule: only if plan scans; a filtered covering index was also tried and ignored by the optimizer |
| A46-0300 load test 50 concurrent | DONE | `ResolverPerfTests.E_*`, `ResolverPlanTests.FiftyConcurrentResolutions_*` |
| A46-0301 within 5 s, no deadlock | DONE - PASS | 34-418 ms total |
| A46-0302 no blocking (DMV) | DONE - PASS | `sys.dm_exec_requests` sampled every 5 ms: 0 blocked |

Open for BE-1 (not applied by PERF): project the resolver candidate query + upgrade `IX_SupplierPricelists_Lookup` INCLUDE with `UUID` via the guarded DROP_EXISTING SQL in the doc (section 4). Acceptance test already green on the patched shape: `ResolverPerfTests.G_ProposedProjectedResolverQuery_MeetsBr37`.


## BE-1 (Demand) - status 2026-10-17

Done and verified (LocalDB only). Register ids: A46-0001..0035 (C1/C3/C5 schema, M4/M5/M8/M9/M10), 0064..0084 (+ 0071..0079 rollback: `ADDENDUM-46-ROLLBACK.sql`, tested by `A46PricingMigrationTests`), 0135..0209 (C4 middleware, C5 factory + refactor, C10 resolver), 0231..0237 (resolve-price), TS-27..31, 35, 36, 38..47, 50..52, 56.
- A46-0013/0019/0020 (order-date re-lookup banner): UI-side (BR-22); server does not auto-re-price on an order-date change - the buyer chooses Re-lookup/Keep.
- A46-0152 (return the resolution from an update): DONE. `PATCH /api/purchase-orders/{uuid}` keeps its `success`/`message` envelope and now also carries a top-level `pricing` (`PoPricingApiResponse`): `{ lines: [{ lineUuid, lineNo, priceSource (0..4), priceSourceName, unitPrice, systemResolvedPrice, resolvedChanged, warning?, currencyMismatchWarning, offer?: { kind: KEEP_OR_UPDATE (Override) | KEEP_OR_APPLY (Manual), systemPrice, yourPrice, qty } }], pricedCount, unpricedCount, warnings[] }`. Built by `PoPricingResultBuilder` from the per-line middleware outcomes (`PurchaseOrderLineResult.Outcome`); `resolvedChanged` = the line's system price differs from the one stored before the edit (a Pricelist line re-priced silently; an Override/Manual line with a moved system price also gets the offer). `lineUuid` is the line's new UUID (an edit rebuilds lines). A header-only edit (no `lines`) returns the stored lines with no change flags. `UpdateAsync` is unchanged; the new `UpdateWithPricingAsync` (repo + service) carries the result. FE: optional `pricing` on `DemandService.patchPo`'s type; po-edit shows one warn toast per `pricing.warnings` entry (offers are not rendered yet). Tests: 8 xUnit (`A46PricingFoundationTests` A46_0152_*, `PurchaseOrderAuditTests`) + 1 Karma (`po-edit.a46.spec.ts`).
- A46-0172/0179/0190/0191 (partial-GR split): no partial-GR line split exists in Demand. The only split (sale-order PO -> another supplier) goes through the factory (Origin Split): new supplier's pricelist, else typed price, else the parent's price as Manual; the remaining quantity goes through the middleware.
- A46-0177 (reorder-point auto-PO), A46-0178 (CSV/Excel import): N/A - no such code path exists in Demand/Material (grep `ReorderPoint|reorder` in src/SMS.Modules.Demand, `import` in Demand: none; Material's SupplyRequirementEngine calls AddOrIncreaseProductionLineAsync, which goes through the factory). The factory supports Origin Import/AutoGenerated with SuppliedPrice for when they land.
- A46-0176 (RFQ -> PO): an awarded RFQ reaches POs only as the requisition line's `QuotationStatus = AWARDED`; the factory takes the awarded price as RFQ (Origin Rfq, no resolver call).
- A46-0180/0192: architectural test `A46_0180_0192_BR_29_...` fails the build of any `new PurchaseOrderLine` in src outside Services/PurchaseOrderLineFactory.cs (test projects excluded).
- C11: resolver projects a narrow `Candidate` record; migration widens IX_SupplierPricelists_Lookup with INCLUDE [UUID] (DROP_EXISTING, guarded). ResolverPlanTests asserts 0 key lookups; PendingModelChangesWarning-ignore removed from ResolverPlanKit.

## FINAL STATUS (2026-10-18)
Verified live (user's API, Azure SMSGlobal, A45+A46 migrations applied; browser + API, test rows A46-TEST then deactivated):
- TS-27 new PO orderDate = today; TS-28 draft orderDate PATCH accepted; TS-30 PO edit order-date change shows "Order date changed. N line(s) may have different prices… [Re-lookup All] [Keep Current Prices]"; TS-31 PO dated 2026-09-15 resolves no price (validity starts Oct 1).
- TS-35/36 12.3456 stored exactly, source Pricelist (not Override); TS-38/39 server prices an unpriced line and re-prices on qty change (tier 10 @150); TS-40 Override keeps buyer's 9, systemResolvedPrice refreshed (12.3456 @ qty 5).
- TS-50/56 USD 50 -> PKR 13,800 @276 (rate date 2026-10-01); PO line stores originalCurrency USD / originalCurrencyPrice 50 / exchangeRateUsed 276; TS-52 same currency no conversion; TS-51 EUR with no rate -> found, unitPrice null, currencyMismatchWarning + message; /finance/convert-currency same/no-rate cases.
- TS-32..34 RFQ prefill (params consumed, 'Referenced from', suggested supplier); PO order-date picker defaults today.
Covered by automated tests only: TS-29 (non-draft orderDate rejected), TS-44 (PR->PO pricingSummary), TS-45 (RFQ price source), TS-53/55, TS-57..59 (query plan: seek, 0 key lookups, 4 reads; 50 concurrent OK on scratch LocalDB).
A46-0152 done (PATCH PO returns `pricing`); takes effect after the API is rebuilt/restarted. Demand tests 1060 pass; FE pages/demand 70/70.
Not applicable: A46-0177/0178 (no reorder-point or CSV import path), A46-0172/0179/0190/0191 (no partial-GR split), exchange-rate table/entity/enum tasks (satisfied by A35).
Residuals: GRN UnitCost and invoice UnitPrice/LineTotal still 2dp (outside A46 spec); no exchange-rate feed job; server does not re-price on order-date change (UI banner only); FE does not render PATCH `offer` objects yet (warnings only); currencyMismatchWarning true-case not unit-tested on PATCH.
Test data left: draft POs PO-2026-00016 ("A45-TEST PO") and one "A46-TEST PO" (no delete endpoint).

## Live UI verification, second pass (2026-10-18, user's API on Azure, Playwright MCP, test rows then deactivated)
Verified in the browser: Product Purchase tab (3 rows, Add price, hint, edit 12.00->12.50 saved, deactivate confirm + "now inactive" toast); Supplier detail Products tab lists the prices; PO create re-lookup prompts: Override qty change -> "System price for qty 120 is 10.00. Keep your price of 11.00?" [Update] -> 10.00 Pricelist, [Keep] keeps 15.00/Override; Manual line + vendor switch back -> "A configured price of 13,800.00 is available for qty 500" [Apply] -> Pricelist; per-toast actions leave no stale toasts; currency info icon tooltip "Original: USD 50 @ 276 (2026-10-01)" on converted line (13,800.00); delivery-date basis: required date 05/11 priced by the delivery-basis row (9.00), moving to 25/10 silently re-priced to 12.50; PUT update with rowVersion + overlap warning; non-draft PO: PATCH orderDate -> 400 "only be changed while … draft", edit URL redirects received PO to detail; legacy PO line shows "Manual" badge and backfilled orderDate (2026-10-07 = CreatedDate).
Still not live-walked: PR->PO conversion pricing summary (would convert a real approved PR), approval of a PO. Observations: PO detail header shows "created <date>" but no separate Order Date label; switching vendor on a priced line keeps the previous vendor's price as Manual with the "No configured price" banner (A45 R-6).
