# Addendum 35 — API contract (Multi-Currency)

**Version 1.4 — 2026-10-07 (CUR).** Owner: CUR. Changes go to CUR by message; CUR edits this file and tells the
affected agents. Decisions: `ADDENDUM-35-ANALYSIS.md` D-1..D-21. Shared C# contracts: `src/SMS.Shared/Common/CurrencyContracts.cs`
(+ `ExchangeRateMath.Convert(amount, rate, decimals)` in `IExchangeRateProvider.cs`).

## 0. Conventions (all endpoints)

- Envelope as everywhere: `{ "success": true, "message": "...", "result": <payload> }`. Errors: same envelope with
  `success: false` and the message, via `GlobalExceptionMiddleware`: `BadRequestException` → 400, `ConflictException` → 409,
  `NotFoundException` → 404. Paged lists: `result = { data: [], totalRecords, page, pageSize, totalPages, hasNext, hasPrevious }`.
- JSON camelCase. **Currency ids are the global `lookups.Currencies` Guid** (D-1) — `currencyId` everywhere means that Guid.
- Dates (`effectiveFrom`, `date`, `rateDate`, …) are date-only strings `yyyy-MM-dd`; timestamps (`rateLockedAt`, `createdAt`) are
  ISO UTC date-times. Open end of a rate = `"9999-12-31"`.
- Rates: numbers with up to **10 decimals**; "rate" of a stored row = units of the org's **rate currency** per 1 unit of the
  currency (D-2). A document's `exchangeRate` = units of its **domain base** per 1 unit of the document currency.
- Money: transaction amounts rounded at the transaction currency's `decimalPlaces`, `*Base` amounts at the base currency's,
  away from zero (D-13). Header **grand total** base = Σ line base (lines already include tax/discount); tax/discount base =
  header amount × rate; subtotal base = grand total base − tax base + discount base (REV-02).
- Domain values (query/body): `"SALE" | "PURCHASE" | "SERVICE"` (case-insensitive in input; responses upper-case).
- Every action is gated; no anonymous access. Every write and by-id read is filtered to the caller's own organization
  (another org's id → 404, super admin included).
- **Missing rate (D-5):** 400 with exactly `No exchange rate for {CODE} on {yyyy-MM-dd}. Add one under Settings → Exchange Rates.`
  (`CurrencyRateNotFoundException`). Raised only when a rate is really needed (document currency ≠ its domain base, or a
  conversion between different currencies).
- **Not an org currency:** 400 `{CODE} is not an active currency of this organization.` (inactive or not configured).

### Permissions (D-16)

| Code | Who |
|---|---|
| `CURRENCY_VIEW` | every role (forms need the picker) |
| `CURRENCY_MANAGE` | System Admin, Org Admin |
| `CURRENCY_RATE_VIEW` | every role |
| `CURRENCY_RATE_MANAGE` | System Admin, Org Admin, roles holding `FINANCE_SETUP_MANAGE` |
| `ORG_CURRENCY_SETTINGS_MANAGE` | System Admin, Org Admin |
| `EXCHANGE_REVALUATION_RUN` | System Admin, Org Admin, roles holding `FINANCE_SETUP_MANAGE` |

## 1. Organization currencies — `api/currencies` (CUR, Finance; spec #1–3)

The org's currencies (`finance.org_currencies`), not the global catalog (`api/lookups/currencies` is unchanged reference data).

`OrgCurrencyModel`
```json
{
  "id": "guid (org_currencies row Uuid)",
  "currencyId": "guid (global lookups.Currencies Id)",
  "code": "AED", "name": "UAE Dirham", "symbol": "د.إ",
  "decimalPlaces": 2, "rounding": 0.01, "symbolPosition": "before",
  "isActive": true, "displayOrder": 6,
  "baseFor": ["SALE"],            // domains whose base this currency is (empty = none)
  "isRateCurrency": false,        // the D-2 rate currency (rate 1.0 forever)
  "createdAt": "…", "updatedAt": "…"
}
```

| Method | Route | Permission | Notes |
|---|---|---|---|
| GET | `api/currencies?includeInactive=false` | `CURRENCY_VIEW` | ordered by `displayOrder`, `code` |
| GET | `api/currencies/{currencyId}` | `CURRENCY_VIEW` | 404 when not configured for the org |
| POST | `api/currencies` | `CURRENCY_MANAGE` | body `SaveOrgCurrencyRequest`; 200 with the model |
| PUT | `api/currencies/{currencyId}` | `CURRENCY_MANAGE` | body `SaveOrgCurrencyRequest` (`code` ignored — immutable) |

`SaveOrgCurrencyRequest`: `{ code, name?, symbol?, decimalPlaces?, rounding?, symbolPosition?, isActive?, displayOrder? }`.
POST: a code already in the global catalog is linked (missing name/symbol taken from it); a new code is added to the global
catalog first (then `name` and `symbol` are required). Defaults: decimals 2, rounding 10^-decimals, `before`, active, order = last+1.
No DELETE (BR-C1-04 — deactivate instead).

Errors: 400 `Currency code must be exactly 3 characters` · 400 `Currency code must be uppercase` · 400 `Currency code must be
letters A–Z` · 409 `Currency {CODE} already exists` · 400 `Decimal places must be between 0 and 3` · 400 `Rounding must be
greater than 0` · 400 `Symbol position must be 'before' or 'after'` · 400 `Name is required (max 60)` / `Symbol is required
(max 5)` · deactivate a base: 400 `Cannot deactivate — used as {sale|purchase|service} base currency` · deactivate the rate
currency: 400 `Cannot deactivate — it is the organization's rate currency` · change `decimalPlaces` of a currency used by
locked documents: 409 `Cannot change decimal places — {usage}` (usage text from `ICurrencyUsageChecker`).

## 2. Exchange rates — `api/currency-rates` (CUR, Finance; spec #4–9)

`CurrencyRateModel`
```json
{
  "id": "guid", "currencyId": "guid", "currencyCode": "USD", "currencyName": "US Dollar",
  "rate": 278.05, "inverseRate": 0.0035964755,
  "effectiveFrom": "2026-10-07", "effectiveTo": "9999-12-31", "isCurrent": true,
  "source": "MANUAL",             // MANUAL | SYSTEM (the rate currency's 1.0 row)
  "notes": "SBP closing rate",
  "rateCurrencyId": "guid", "rateCurrencyCode": "PKR",
  "createdAt": "…", "createdBy": 12, "modifiedAt": null, "modifiedBy": null
}
```

| Method | Route | Permission | Notes |
|---|---|---|---|
| GET | `api/currency-rates?currencyId=&from=&to=` | `CURRENCY_RATE_VIEW` | rows whose range overlaps [from, to] (either optional); `currencyCode` asc, `effectiveFrom` desc |
| GET | `api/currency-rates/active` | `CURRENCY_RATE_VIEW` | `effectiveTo = 9999-12-31`, rate currency's SYSTEM row included |
| GET | `api/currency-rates/{currencyId}?date=yyyy-MM-dd` | `CURRENCY_RATE_VIEW` | the row covering `date` (default today); 400 missing-rate message when none |
| GET | `api/currency-rates/history/{currencyId}` | `CURRENCY_RATE_VIEW` | every row, `effectiveFrom` desc |
| POST | `api/currency-rates` | `CURRENCY_RATE_MANAGE` | insert (below) |
| PUT | `api/currency-rates/{id}` | `CURRENCY_RATE_MANAGE` | historical correction (below) |

POST body `{ currencyId, rate, effectiveFrom, effectiveTo?: null, notes? }` → `{ rate: CurrencyRateModel, closedPrevious: CurrencyRateModel | null }`.
- `effectiveTo` null (open-ended, the normal case): if the current row starts **before** `effectiveFrom` it is closed to
  `effectiveFrom − 1 day` (BR-C2-03, `closedPrevious`), and the new row runs to 9999-12-31. If `effectiveFrom` falls on or
  before the current row's start, or inside a closed row → 409.
- `effectiveTo` set (gap fill): must not overlap any row → else 409.
- Runs SERIALIZABLE under `sp_getapplock` per (org, currency): two concurrent inserts for the same day → one 200, one 409.

PUT body `{ rate, effectiveFrom, effectiveTo (null = open-ended), notes }` → `CurrencyRateModel`. Must not overlap any other row
of the currency (409). Locked documents are unaffected (they hold a snapshot).

Errors: 400 `Rate must be positive` · 400 `A rate can have at most 10 decimals` · 400 `The rate is too large` (max 99,999,999.9999999999)
· 400 `Base currency rate cannot be modified` (the rate currency, or any SYSTEM row) · 400 not-an-org-currency · 400
`Effective to must be on or after effective from` · 409 `Rate already exists for this date ({CODE} {rate} from {from} to {to})` ·
404 unknown rate id / currency.

## 3. Conversion — `POST api/currency/convert` (CUR, Finance; spec #12)

Permission: `CURRENCY_VIEW` or `CURRENCY_RATE_VIEW`.
Body `{ amount, fromCurrencyId, toCurrencyId?: null, date?: today, domain?: "SALE" }` — `toCurrencyId` null = the org's base for
`domain`.
```json
{
  "originalAmount": 5000.00,
  "fromCurrency": { "id": "guid", "code": "AED", "symbol": "د.إ", "decimalPlaces": 2 },
  "convertedAmount": 381500.00,
  "toCurrency":   { "id": "guid", "code": "PKR", "symbol": "₨", "decimalPlaces": 2 },
  "rateUsed": 76.3,                 // units of To per 1 From (cross rate, 10dp); 1 when same currency
  "rateDate": "2026-10-07",
  "effectiveFrom": "2026-10-07",    // the range over which this cross rate holds (intersection of both rows)
  "effectiveTo": "9999-12-31",
  "domain": "SALE"
}
```
Same currency → no lookup, rate 1, range 2000-01-01..9999-12-31. Missing rate → 400 missing-rate message.

## 4. Organization currency settings — `api/organization/currency-settings` (TEN, Tenancy; spec #10–11)

`OrgCurrencySettingsModel`
```json
{
  "saleBaseCurrencyId": "guid",     "saleBaseCurrencyCode": "PKR",
  "purchaseBaseCurrencyId": "guid", "purchaseBaseCurrencyCode": "USD",
  "serviceBaseCurrencyId": "guid",  "serviceBaseCurrencyCode": "PKR",
  "rateCurrencyId": "guid",         "rateCurrencyCode": "PKR",
  "exchangeGainAccountCode": "7110", "exchangeLossAccountCode": "7120",
  "unrealizedGainAccountCode": "7130", "unrealizedLossAccountCode": "7140",
  "isStored": true,
  "locks": {
    "sale":         { "locked": true,  "reason": "12 confirmed sale orders" },
    "purchase":     { "locked": false, "reason": null },
    "service":      { "locked": false, "reason": null },
    "rateCurrency": { "locked": true,  "reason": "34 exchange rates" }
  }
}
```

| Method | Route | Permission |
|---|---|---|
| GET | `api/organization/currency-settings` | `CURRENCY_VIEW` or `ORG_CURRENCY_SETTINGS_MANAGE` |
| PUT | `api/organization/currency-settings` | `ORG_CURRENCY_SETTINGS_MANAGE` |

PUT body: `{ saleBaseCurrencyId, purchaseBaseCurrencyId, serviceBaseCurrencyId, rateCurrencyId?: null (**read-only**: null or the stored value; anything else → 409 `The rate currency can't be changed.` — REV-07; the SYSTEM rate row is the source of truth),
exchangeGainAccountCode?, exchangeLossAccountCode?, unrealizedGainAccountCode?, unrealizedLossAccountCode? }` → the model.
Saving the sale base also sets `Organization.BaseCurrency` (D-7). Missing row on GET reads as all = `Organization.BaseCurrency` ?? PKR
(`isStored: false`); PUT creates it.
Errors: 400 not-an-org-currency (BR-C3-02) · 409 `Cannot change the {sale|purchase|service} base currency — {usage}.` (D-8, from
every `ICurrencyUsageChecker.DescribeDomainBaseUsageAsync`) · 409 `The rate currency can't be changed.` · REV-06: with no row yet, a profile BaseCurrency change creates the row (only the sale base moves); clearing the profile base → 409 when any base or the rate currency is in use · 400 `Account
codes can be at most 20 characters`.

## 5. Business partner currency defaults (TEN, Suppliers; D-9)

Existing business-partner / supplier GET, create and update DTOs gain:
- `defaultSaleCurrencyId: guid | null` (new column `DefaultSaleCurrency`), `defaultSaleCurrencyCode` (response only);
- `defaultPurchaseCurrencyId: guid | null` = the existing `PreferredCurrency` (`preferredCurrency` stays as an alias in both
  directions; if both are sent, `defaultPurchaseCurrencyId` wins), `defaultPurchaseCurrencyCode` (response only).
- On PUT/PATCH a null id means **unchanged** (the existing `PreferredCurrency` convention). To clear a default send
  `clearDefaultSaleCurrency: true` / `clearDefaultPurchaseCurrency: true`; a value together with its clear flag → 400.
Error: 400 not-an-org-currency (BR-C4-01). Resolution for new documents: `IPartnerCurrencyDefaults.ResolveDefaultCurrencyAsync`.

## 6. Demand documents (DEM; D-10, D-12, D-14)

All new fields are additive; old fields unchanged. Input `currencyId` optional on create (default per D-14), changeable until the
document locks (after lock → 400 `The currency cannot be changed after the exchange rate is locked.`). Base fields are **null
until the lock** (D-11). Lock failures → 400 missing-rate message.

| Document | Header (response) | Lines (response) | Lock |
|---|---|---|---|
| Sale inquiry | `currencyId`, `currencyCode` (input `currencyId`; default customer `defaultSaleCurrencyId` → sale base) | — | none |
| Sale quotation | existing `currencyId` + `currencyCode`, `exchangeRate`, `baseCurrencyId`, `baseCurrencyCode`, `rateLockedAt` (default from inquiry → customer → sale base) | + `unitPriceBase`, `discountAmountBase`, `taxAmountBase`, `lineTotalBase` | → SENT, rate of the sent date |
| Sale order | existing `currencyId` + `currencyCode`, `exchangeRate`, `baseCurrencyId`, `baseCurrencyCode`, `rateLockedAt`, `subtotalBase`, `taxAmountBase`, `discountAmountBase`, `grandTotalBase` (default from quotation → customer → sale base) | + `unitPriceBase`, `lineTotalBase` | → CONFIRMED, rate of the confirm date (re-locked, not inherited) |
| Purchase order | **new** `currencyId` (input), `currencyCode`, `exchangeRate`, `baseCurrencyId`, `baseCurrencyCode`, `rateLockedAt`, `totalAmountBase` (default supplier `defaultPurchaseCurrencyId` → purchase base) | + `unitPriceBase`, `lineTotalBase` | → APPROVED, rate of the approval date |

Same currency as the domain base → `exchangeRate: 1`, base fields = amounts, no lookup (BR-C5-04). POs have no tax/subtotal.
List endpoints (SO, PO registers) add `currencyCode` and the header base total (`grandTotalBase` / `totalAmountBase`).

## 7. Finance documents (FIN; D-10, D-12, D-14, D-15)

| Document | New response fields | Lock / rule |
|---|---|---|
| Sales invoice | `currencyId`, `baseCurrencyId`, `exchangeRateLockedAt` (existing `currencyCode`, `exchangeRate`, `baseCurrencyCode`, `baseGrandTotal` kept; `grandTotal` = amount in currency) | → ISSUED, sale base, rate of the issue date; currency inherited from the SO |
| Supplier invoice (`finance.invoices`) | `currencyId`, `baseCurrencyId`, `exchangeRateLockedAt` (existing `currency`, `exchangeRate`, `baseCurrencyCode`, `baseTotalAmount` kept) | → APPROVED, **purchase** base; currency inherited from the PO |
| Customer payment | `currencyId`, `exchangeRate`, `baseCurrencyId`, `baseCurrencyCode`, `amountBase`, `exchangeDifference` (Σ allocations) (existing `currencyCode` kept); allocations + `exchangeDifference` | → POSTED, sale base, rate of the payment date |
| Supplier payment | `currencyCode`, `currencyId`, `exchangeRate`, `baseCurrencyId`, `baseCurrencyCode`, `amountBase`, `exchangeDifference`; lines + `exchangeDifference` | → POSTED, purchase base, rate of the payment date |

Input: customer/supplier payment create accepts `currencyId` (or the existing `currencyCode`); default = the first allocated
invoice's currency → partner default → domain base. Allocating a payment to an invoice in another currency → 400
`Payment is in {X}; invoice {NO} is in {Y}. Allocate it to invoices in the same currency.` (D-14).
Realized difference per allocation (BR-C7-01..03, D-15): `round(allocated × paymentRate, baseDp) − round(allocated × invoiceRate, baseDp)`;
+ = gain, − = loss; none when the invoice currency = base (BR-C7-05). **Payable sign** (FIN, recorded deviation from the
spec's T-C8-07 text): owing a currency that fell is a GAIN, i.e. sign from the organization's point of view.

Detail additions (FIN, E-06, v1.4):
- Sales invoice detail: `payments[]` rows + `exchangeDifference` (number|null, sale base, the allocation's); header
  `realizedExchangeDifference` (number|null = net of REALIZED register rows for the invoice; bounces net out).
- Supplier invoice detail (`api/finance/invoices/{uuid}`): header `currencyId`, `baseCurrencyId`, `exchangeRateLockedAt`,
  `realizedExchangeDifference`; legacy `payments[]` rows + `exchangeDifference` (always null — no FX on legacy single-invoice
  payments); new `supplierPayments[]`: `{ paymentUuid, paymentNumber, paymentDate, paymentMethod, status, currencyCode,
  allocatedAmount, exchangeDifference }` (multi-invoice supplier-payment lines against the invoice).
- Customer payment detail: `allocations[]` + `exchangeDifference`. Supplier payment detail: `lines[]` + `exchangeDifference`;
  supplier payment list adds `currencyCode`, `amountBase`.
- Create inputs: customer payment `currencyId` (or the existing `currencyCode`, now optional); supplier payment
  `currencyId` / `currencyCode` (optional); supplier invoice create `currencyId` (optional; else the PO's currency, else `currency`).
- Revaluation result `totals[].loss` is a negative number.

### 7.1 `GET api/finance/exchange-differences`

Permission (any of): `INVOICE_VIEW`, `SALES_INVOICE_VIEW`, `CUSTOMER_PAYMENT_VIEW`, `PAYMENT_VIEW`, `EXCHANGE_REVALUATION_RUN`.
Query: `kind=REALIZED|UNREALIZED`, `side=RECEIVABLE|PAYABLE`, `currencyId`, `from`, `to` (on `postedAt` date / `revaluationDate`),
`documentType`, `documentId`, `paymentType`, `paymentId`, `page=1`, `pageSize=50` (max 200). Paged `ExchangeDifferenceModel`:
```json
{
  "id": "guid", "kind": "REALIZED", "side": "RECEIVABLE",
  "documentType": "SALES_INVOICE", "documentId": 41, "documentUuid": "guid", "documentNo": "SINV-2026-00041",
  "paymentType": "CUSTOMER_PAYMENT", "paymentId": 7, "paymentUuid": "guid", "paymentNo": "RCPT-2026-00007",
  "allocationId": 9,
  "partnerId": "guid",
  "currencyId": "guid", "currencyCode": "AED", "amountCurrency": 10550.00,
  "bookedRate": 76.30, "settlementRate": 76.45,
  "baseCurrencyId": "guid", "baseCurrencyCode": "PKR",
  "bookedAmountBase": 804965.00, "settledAmountBase": 806547.50,
  "differenceBase": 1582.50,                       // + gain, − loss
  "accountCode": "7110",                            // gain/loss (or unrealized gain/loss) code from org settings, may be null
  "postedAt": "2026-10-14T09:12:00Z", "revaluationDate": null,
  "createdBy": 12
}
```
`documentType`: `SALES_INVOICE | SUPPLIER_INVOICE`; `paymentType`: `CUSTOMER_PAYMENT | SUPPLIER_PAYMENT | null` (unrealized).

### 7.2 `POST api/finance/exchange-revaluation/run`

Permission `EXCHANGE_REVALUATION_RUN`. Body `{ revaluationDate?: today }`. Revalues the caller's org's open foreign-currency
receivables (issued sales invoices with balance) and payables (approved supplier invoices with balance) at the rate on
`revaluationDate` vs the booked rate; one UNREALIZED row per open document per date; re-running the same date **replaces** that
date's rows (idempotent). A document whose currency has no rate on that date is skipped (listed), the run continues. Same job runs
monthly (last day 23:00 UTC) for every org via Hangfire.
```json
{
  "revaluationDate": "2026-10-31",
  "receivablesRevalued": 3, "payablesRevalued": 2, "rowsWritten": 5, "rowsReplaced": 0,
  "totals": [ { "baseCurrencyCode": "PKR", "gain": 2600.00, "loss": -5500.00, "net": -2900.00 } ],
  "skipped": [ { "documentType": "SALES_INVOICE", "documentNo": "SINV-2026-00050", "reason": "No exchange rate for MXN on 2026-10-31. …" } ]
}
```

## 8. Legacy `api/finance/exchange-rates` (CUR, D-17, A35-E-02)

`GET api/finance/exchange-rates?from=&to=` and `GET …/quote?from=&to=&date=` keep their shapes but are **served from
`finance.currency_rates`**: each row appears as `fromCurrencyCode = X`, `toCurrencyCode = rate currency`, `rate`,
`effectiveDate = effectiveFrom`, `uuid = row id`. `quote` uses the re-implemented `IExchangeRateProvider` (triangulated, D-6;
`inverted: false`, `effectiveDate` = the later of the two rows' starts). POST/PUT/DELETE are **removed** (FE-SET replaced the page, 2026-10-07): 404/405. `finance.exchange_rates` is frozen.

## 9. Shared C# contracts (summary — the code is authoritative)

`TransactionDomain`, `CurrencyConventions` (OpenEnd, SystemStart, RateDecimals = 10, RoundAmount, RoundRate),
`CurrencyRateNotFoundException : BadRequestException`, `CurrencyRateInfo`, `CurrencyConversionResult`, `DocumentRateLock`
(`Rate`, `BaseCurrencyId`, `SameCurrency`, decimals, `ToBase(amount)`), `ICurrencyService` (Finance), `OrgCurrencyInfo` +
`IOrgCurrencyLookup` (Finance), `OrgCurrencySettingsSnapshot` (returned by TEN's `IOrganizationCurrencyService.GetSettingsAsync`),
`ICurrencyUsageChecker` (Demand + Finance; multi-registration), `PartnerCurrencyDefaults` + `IPartnerCurrencyDefaults` (Suppliers),
`ICurrencyRatesReadyParticipant` (multi-registration; Finance's `CurrencyBootstrapper` calls each per organization after seeding
currencies, the rate-currency row and the legacy conversion — synchronously in `UseFinanceModule` and on provisioning, under a
per-org applock; Demand's locked-document backfill uses it).

`IOrganizationCurrencyService` (TEN's file, Tenancy impl): `Task<Guid?> GetBaseCurrencyIdAsync(org)` (= Sale; null only when
nothing resolves), `Task<Guid?> GetBaseCurrencyIdAsync(org, TransactionDomain, ct)`, `Task<OrgCurrencySettingsSnapshot>
GetSettingsAsync(org, ct)` — never null; missing row → all = `Organization.BaseCurrency` ?? the org's PKR (via
`IOrgCurrencyLookup.GetByCodeAsync(org, "PKR")`), ids `Guid.Empty` only when none of those resolves (`isStored: false`). The new
members are default interface methods. `ICurrencyService.GetBaseCurrencyIdAsync` delegates to it.

Lock recipe for a document owner (DEM / FIN):
```csharp
var lk = await currency.LockRateAsync(doc.OrganizationId, doc.CurrencyId, lockDate, TransactionDomain.Sale, ct); // throws 400 when missing
doc.ExchangeRate = lk.Rate; doc.BaseCurrencyId = lk.BaseCurrencyId; doc.RateLockedAt = now;
foreach (var l in doc.Lines) { l.UnitPriceBase = lk.ToBase(l.UnitPrice); l.LineTotalBase = lk.ToBase(l.LineTotal); }
// SO/SQ LineTotal already includes discount and tax, so Σ line base is the GRAND total (REV-02):
doc.GrandTotalBase     = doc.Lines.Sum(l => l.LineTotalBase);
doc.TaxAmountBase      = lk.ToBase(doc.TaxAmount);
doc.DiscountAmountBase = lk.ToBase(doc.DiscountAmount);
doc.SubtotalBase       = doc.GrandTotalBase - doc.TaxAmountBase + doc.DiscountAmountBase;
// PO: TotalAmountBase = Σ LineTotalBase.
```

## Change log
- 1.0 (2026-10-07) — initial.
- 1.1 (2026-10-07) — TEN: §5 null = unchanged + clear flags; §9 `IOrganizationCurrencyService` members.
- 1.4 (2026-10-07) — FIN §7 detail additions (E-06), payable sign, create inputs.
- 1.3 (2026-10-07) — §4 rateCurrencyId read-only (REV-07) + REV-06 note; §8 legacy writes removed; §9 ICurrencyRatesReadyParticipant.
- 1.2 (2026-10-07) — REV-02: GrandTotalBase = Σ LineTotalBase; SubtotalBase = GrandTotalBase − TaxAmountBase + DiscountAmountBase.
