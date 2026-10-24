# Addendum 45 — task tracker

Agents append their completed A45 ids and residuals below (do not overwrite others' sections).

## FE-A — Supplier pricelist management UI (Phase 4, C4/C6)

Done (specs: 33 new Karma specs green; app.menu / product-detail / supplier-detail specs still green, 59; `tsc -p tsconfig.app.json` clean):

A45-0149, 0150, 0151, 0152, 0153, 0155, 0156, 0157, 0158, 0159, 0160, 0161, 0162, 0163, 0164, 0165, 0166, 0167, 0168, 0169, 0171, 0172, 0173, 0174, 0175 (server message passed through), 0176, 0177, 0178, 0179.

Partial / by design:
- A45-0154 inline edit: edit is a dialog (all fields), not in-cell editing (spec allowed "inline row or modal").
- A45-0170 click row: row has Edit button (dialog) rather than navigation.
- A45-0175 overlap toast shows the server's `overlapWarning` text verbatim; it contains the conflicting priority/dates only if the backend writes them into that string.

Files: `services/supplier-pricelist.{service,models}.ts` (+spec), `shared/supplier-pricelist/pricelist-{grid,dialog}.component.ts` (+specs), `pages/demand/supplier-pricelists/supplier-pricelist-list.component.ts`, route `demand/supplier-pricelists`, menu entry Procurement > Supplier Pricelists, Product detail "Purchase" tab, Supplier detail "Products & prices" section.

Residuals / notes for BE and QA:
- R-1 Envelope: the real `ApiResponse` serialises `result` (not `data` as Part B says); the service reads `result ?? data`. Paged list is read as `{data|items, totalRecords|total|totalCount}` or a bare array — BE to confirm the paged shape.
- R-2 Permissions are literal: view = PO_VIEW/PO_CREATE/PO_EDIT/PO_APPROVE, write (add/edit/deactivate buttons) = PO_CREATE/PO_EDIT (D-8 placeholder). Change if BE picks different codes (pages.routes.ts, app.menu.ts, three `canViewPricelist/canEditPricelist` getters).
- R-3 The edit dialog sends `isActive` in the PUT/POST body (active toggle re-activates a row); BE must accept it as a writable field.
- R-4 Currency defaults to the org PURCHASE base currency; the supplier's default currency is not applied (supplier detail holds a lookup label, not a currency id).
- R-5 Variant choice appears only on the Product tab (variants of that product); standalone/supplier contexts create product-level rows (variantUuid null). Product/vendor pickers load up to 500 active records (same as PO create).
- R-6 409 on update shows the server message or a "changed by someone else" text and reloads the grid; a 409 for exact-duplicate on create shows the server message.
- R-7 Supplier detail anchor "Products" is listed for all users; the section itself is hidden without view permission.
- R-8 Not exercised live (backend in parallel): no Playwright pass; QA to walk TS-21..TS-24.

## FE-B — PO line auto-pricing & re-lookup (Phase 5, C5/C7)

Done (Karma: 27 specs green across pages/demand incl. new po-create.a45 x16, po-edit.a45 x2; existing a35 PO specs unchanged and green):

A45-0180, 0181, 0182, 0183, 0184, 0185, 0186, 0187, 0188, 0189, 0190, 0191, 0192, 0193, 0194, 0195, 0196, 0197, 0198, 0199, 0200, 0201, 0202, 0203, 0204, 0205, 0206, 0207, 0208, 0209, 0210 (logic + banner, see R-1), 0211, 0212, 0213. (5.7 / 0214-0217 are backend.)

Files: `services/purchase-pricing.service.ts` (resolvePrice + PriceSource enum/labels), `pages/demand/purchase-orders/po-line-pricing.ts` (shared controller used by po-create and po-edit), po-create / po-edit ts+html+scss, po-detail ts+html (source badge + override reason), `services/demand.service.ts` (CreatePoLineRequest / PoLineModel gain priceSource, systemResolvedPrice, supplierPricelistUuid, priceOverrideReason, pricingDateBasis?).

Residuals / notes:
- R-1 (D-11 gap) The PO form has no order-date control and PO has no order-date column, so orderDate sent = today. The order-date banner (Re-lookup All / Keep Current Prices) and `PoLinePricing.setOrderDate()` are implemented and unit-tested but nothing in the UI calls it yet; wire it if an order-date field is ever added.
- R-2 Delivery-date re-lookup (BR-09) needs the matched record's dateBasisUsed. On create it is kept from the resolve response; on edit it is only known if PoLineModel returns `pricingDateBasis` ('OrderDate'|'DeliveryDate'). BE: please add it to the PO line read model, otherwise hydrated lines skip date-triggered re-lookup (qty re-lookup still works).
- R-3 Create RFQ navigates to /portal/pages/demand/quotations/create with query params supplierId, productId, variantUuid, qty, productName; quotation-create does not read query params yet, so prefill is not applied (navigation works).
- R-4 PR/quotation import fills lines silently (no resolve); lines are priced when the supplier is set/changed (Manual lines with a typed price get the Keep/Apply toast, zero-price Manual lines are filled silently). Quotation-imported lines stay Manual client-side (RFQ source is server-assigned per D-10).
- R-5 Toast actions: Keep/Update/Apply clear the whole price-relookup toast stack; other pending offers disappear (equivalent to Keep).
- R-6 Supplier or currency change re-resolves every line with a variant (Pricelist/Override lines are replaced; Manual get the offer toast). Edit page suspends this while hydrating so opening a PO never re-prices it.
- R-7 Not exercised live (backend in parallel); QA to walk TS-01..TS-14 in the browser. Wire envelope read as `result ?? data`.


## BE — entity, migration, resolver, API, PO save validation (Phases 1-3, 5.7, backend of 6)

Done (Demand.Tests 999 passed / 1 skipped, incl. 38 new A45 tests; SMS.Integration.Tests SupplierPricelistE2ETests 2/2, plus UngatedEndpointsRatchet 3, VendorBackwardCompatibility 8, PurchaseFlowE2E 2, MultiCurrencyDocumentsE2E 10 green; LocalDB only):

- Phase 1: A45-0001..0063 as D-1..D-5 adapted (table `demand.SupplierPricelists`, UUID refs instead of int FKs, so A45-0003/0004/0005/0010/0023-0026/0037/0039/0045-0048/0060 are replaced by UUID columns without FK; `OrgId` is `OrganizationId` Guid). A45-0027..0033 (4 CKs, 3 indexes + 2 extra: Variant filtered, UUID unique), 0034..0042 (M2 columns; PO line `SupplierPricelistUuid` no FK; CK_POLines_PriceSource; M3 guarded UPDATE touches 0 rows), 0043..0044, 0049..0055, 0056..0059, 0061..0063. Migration `20261017*_A45_SupplierPricelists` is hand-guarded and idempotent. Verification 0064..0070 done by A45SupplierPricelistMigrationTests on LocalDB (up/down/up, re-run after history loss, existing lines read as Manual, CKs, filtered unique index, rowversion, model-vs-DB no pending changes).
- Phase 2: A45-0071..0098 (DTOs, `IPurchasePriceResolver`/`PurchasePriceResolver` scoped in DI, ordering, basis, fallback, warning, currency rule); 0086 index chosen to cover the seek (plan not captured), 0087/0099 read-only, concurrency smoke only (single-threaded in-memory).
- Phase 3: A45-0100..0136 (controller, filters, paging, create/update/delete, overlap, resolve-price), 0137..0148 via SupplierPricelistE2ETests (HTTP) plus service tests.
- Phase 5.7: A45-0214..0217. Phase 6 backend-checkable: 0218..0227, 0228, 0229(server side), 0238..0243 (TS-01..13 resolver/PO side, TS-21..26), 0244..0246, 0250, 0251, 0253..0256, 0258. UI-only ones (0230..0237 toasts/banners) belong to FE/QA.

Residuals:
- R-1 Order date on save: a PO has no order-date column (D-11), so the server verifies a Pricelist/Override claim with the order date = today on create and the PO's CreatedDate on edit. Mismatches with the UI's date are absorbed by trusting a claimed pricelist row whose price equals the line price.
- R-2 PO line `UnitPrice` is decimal(18,2) (pre-existing) while pricelist prices are decimal(18,4): prices are compared at 2 decimals, a 4-decimal pricelist price is stored rounded on the line.
- R-3 Server never re-prices on quantity change (UI does); `AddOrIncreaseProductionLineAsync` bumping an existing line's quantity leaves its price source untouched; Split moves the quantity to a Manual line; auto/deficit POs are Manual.
- R-4 PR-to-PO conversion paths set `RFQ` for requisition lines with `QuotationStatus = AWARDED`; otherwise Manual (no auto pricelist pricing on conversion).
- R-5 Supplier-name search in the list resolves names via ISupplierNameLookupService over the organization's distinct pricelist suppliers (fine at pricelist scale).
- R-6 Query plan for the resolver (A45-0086/0257) not captured; indexes are leading on OrganizationId.
- R-7 In-memory tests cannot generate rowversions; conflict path is asserted explicitly there and for real on SQL Server in the E2E and migration tests.
- R-8 Warehouse/Material/Finance test projects not re-run (additive optional constructor parameter and new nullable columns only).

## FINAL STATUS (2026-10-18) — verified live on the user's API (Azure SMSGlobal, A45+A46 migrations applied)
Verified in a real browser + API as haroonarshad230@gmail.com:
- Pricelist page loads; Add dialog (product/vendor pickers load real data, date picker, 4dp price) saves ("Saved" toast); row shows 15.1234 PKR, 01-28 Oct 2026.
- Overlap warning returned by API with priority + dates (TS-21); qty-tier row (min 100 @ 12.5) accepted.
- PO create: auto-price from pricelist (15.1234, badge Pricelist) -> qty 150 silently re-prices to the 12.50 tier -> typing 11 flips badge to Override and shows reason field -> saved as PO-2026-00016; detail shows PKR 11.00, Override badge, reason, total 1,650.00.
- Not-found: warning banner "No configured price found for ... Enter price manually or create an RFQ" with [Enter Manually] [Create RFQ]; Create RFQ opens RFQ create prefilled (title, qty, product, suggested supplier, 'Referenced from PO...').
- A46 order-date picker defaults to today and the calendar selects dates.
Fixes this round: FE-B R-5 (act per re-lookup toast, redrawn stack) + specs updated (po-create.a45, po-edit.a45); Karma pages/demand + shared/supplier-pricelist + pages/suppliers = 109/109.
Test data: pricelist rows created were soft-deleted (0 active remain); draft PO-2026-00016 "A45-TEST PO" remains (no delete endpoint) — cancel/ignore.
Known, not A45 defects: PrimeNG p-calendar logs a harmless resize-listener TypeError app-wide; typed (not picked) dates in p-calendar are discarded on blur; supplier picker `pageSize=500` is capped at 100 by /api/suppliers; new suppliers are PENDING and absent from vendor pickers until ACTIVE.
Not live-walked: TS-14..20 order-date banner/delivery-date basis toasts, Product Purchase tab and Supplier Products tab UIs (covered by specs + API tests), tenant isolation (integration tests only).

## Live UI verification, second pass (2026-10-18, user's API on Azure, Playwright MCP, test rows then deactivated)
Verified in the browser: Product Purchase tab (3 rows, Add price, hint, edit 12.00->12.50 saved, deactivate confirm + "now inactive" toast); Supplier detail Products tab lists the prices; PO create re-lookup prompts: Override qty change -> "System price for qty 120 is 10.00. Keep your price of 11.00?" [Update] -> 10.00 Pricelist, [Keep] keeps 15.00/Override; Manual line + vendor switch back -> "A configured price of 13,800.00 is available for qty 500" [Apply] -> Pricelist; per-toast actions leave no stale toasts; currency info icon tooltip "Original: USD 50 @ 276 (2026-10-01)" on converted line (13,800.00); delivery-date basis: required date 05/11 priced by the delivery-basis row (9.00), moving to 25/10 silently re-priced to 12.50; PUT update with rowVersion + overlap warning; non-draft PO: PATCH orderDate -> 400 "only be changed while … draft", edit URL redirects received PO to detail; legacy PO line shows "Manual" badge and backfilled orderDate (2026-10-07 = CreatedDate).
Still not live-walked: PR->PO conversion pricing summary (would convert a real approved PR), approval of a PO. Observations: PO detail header shows "created <date>" but no separate Order Date label; switching vendor on a priced line keeps the previous vendor's price as Manual with the "No configured price" banner (A45 R-6).
