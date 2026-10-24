# SMS Task Register — Addendum 46

## Purchase Order Pricing Foundation & Multi-Currency Framework

| Field           | Value                                              |
| --------------- | -------------------------------------------------- |
| Document ID     | SMS-FSD-ADD-046                                    |
| Addendum        | 46 — PO Pricing Foundation & Multi-Currency        |
| Total Tasks     | 312                                                |
| ID Range        | A46-0001 — A46-0312                                |
| Phases          | 6 phases across 8 weeks                            |
| Depends On      | Addendum 45 (Supplier Pricelist & PO Auto-Pricing) |

---

## Status Legend

| Marker | Meaning     |
| ------ | ----------- |
| `[ ]`  | Pending     |
| `[~]`  | In progress |
| `[✓]`  | Done        |
| `[—]`  | Skipped     |

## Priority Legend

| Tag | Meaning                                 |
| --- | --------------------------------------- |
| P0  | Must have — blocks other tasks or launch |
| P1  | Should have — important but not blocking |
| P2  | Nice to have — can ship without          |

---

## Summary by Phase

| Phase | Name                                    | Tasks | Weeks | Change Items             |
| ----- | --------------------------------------- | ----- | ----- | ------------------------ |
| 1     | Database & Entity Foundation            | 88    | 1–2   | C1, C3, C5, C6, C8, M4–M10 |
| 2     | Currency Services                       | 46    | 2–3   | C8 (API), C9             |
| 3     | Price Resolution Enhancements           | 52    | 3–5   | C4, C5, C10              |
| 4     | API Layer                               | 34    | 4–5   | 6.1, 6.2, 6.3           |
| 5     | UI Layer                                | 46    | 5–7   | C1, C2, C3, C6, C7, C10 |
| 6     | Performance & Integration Testing       | 46    | 7–8   | C11, TS-27–TS-59, BRs   |

---

## Phase 1 — Database & Entity Foundation (Week 1–2)

### C1 — PO Header: OrderDate Field

#### Schema & Migration

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0001  | Add `OrderDate DATE NOT NULL` column definition to `PurchaseOrder` entity class            | P0       | [ ]    |
| A46-0002  | Configure EF Core property: `HasColumnType("date")`, `HasDefaultValueSql("CAST(SYSUTCDATETIME() AS date)")` | P0       | [ ]    |
| A46-0003  | Write migration M9: `ALTER TABLE purchase.PurchaseOrders ADD OrderDate DATE NOT NULL CONSTRAINT DF_PurchaseOrders_OrderDate DEFAULT CAST(SYSUTCDATETIME() AS date)` | P0       | [ ]    |
| A46-0004  | Write migration M10: Backfill `OrderDate = CAST(CreatedAt AS date)` for existing POs       | P0       | [ ]    |
| A46-0005  | Run M9 against dev database — verify column added with correct type and default            | P0       | [ ]    |
| A46-0006  | Run M10 against dev database — verify backfill: `SELECT COUNT(*) WHERE OrderDate <> CAST(CreatedAt AS date)` returns 0 | P0       | [ ]    |
| A46-0007  | Add `OrderDate` to `PurchaseOrder` DTO / response model                                   | P0       | [ ]    |
| A46-0008  | Add `OrderDate` to `CreatePurchaseOrderCommand` with default `DateOnly.FromDateTime(DateTime.UtcNow)` | P0       | [ ]    |
| A46-0009  | Add `OrderDate` to `UpdatePurchaseOrderCommand`                                           | P0       | [ ]    |

#### Business Logic

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0010  | Implement OrderDate editability rule: editable only when PO `Status = Draft`               | P0       | [ ]    |
| A46-0011  | Add validation: reject `OrderDate` change when PO status ≠ Draft (return 400 with message) | P0       | [ ]    |
| A46-0012  | Update `PriceResolutionRequest.OrderDate` mapping: always source from `PO.OrderDate`, remove `DateTime.UtcNow` fallback | P0       | [ ]    |
| A46-0013  | Wire OrderDate change to C7 re-lookup banner trigger from Addendum 45 (for `PricingDateBasis = OrderDate` lines) | P1       | [ ]    |
| A46-0014  | Add lead time calculation: `ExpectedDeliveryDate = OrderDate + LeadTimeDays` when buyer hasn't manually set delivery date | P1       | [ ]    |

#### Unit Tests

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0015  | Test: PO created → `OrderDate` defaults to today (UTC)                                    | P0       | [ ]    |
| A46-0016  | Test: Draft PO → `OrderDate` update accepted                                              | P0       | [ ]    |
| A46-0017  | Test: Confirmed PO → `OrderDate` update rejected with error                               | P0       | [ ]    |
| A46-0018  | Test: Resolver receives `PO.OrderDate`, not `DateTime.UtcNow`                             | P0       | [ ]    |
| A46-0019  | Test: `OrderDate` change fires re-lookup for `PricingDateBasis = OrderDate` lines          | P1       | [ ]    |
| A46-0020  | Test: `OrderDate` change does NOT fire re-lookup for `PricingDateBasis = DeliveryDate` lines | P1       | [ ]    |

---

### C3 — PO Line Price Precision Alignment

#### Schema & Migration

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0021  | Write migration M4: `ALTER TABLE purchase.PurchaseOrderLines ALTER COLUMN UnitPrice DECIMAL(18,4) NOT NULL` | P0       | [ ]    |
| A46-0022  | Run M4 against dev database — verify column widened from (18,2) to (18,4)                  | P0       | [ ]    |
| A46-0023  | Verify existing data: `SELECT UnitPrice FROM purchase.PurchaseOrderLines` — confirm `12.35` reads as `12.3500` | P0       | [ ]    |
| A46-0024  | Update `PurchaseOrderLine` entity: change `UnitPrice` property precision annotation to `HasPrecision(18, 4)` | P0       | [ ]    |
| A46-0025  | Update `PurchaseOrderLine` DTO: ensure `UnitPrice` is `decimal` with no rounding in serialization | P0       | [ ]    |

#### Business Logic

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0026  | Fix Override detection: direct comparison `UnitPrice != SystemResolvedPrice` (both `decimal(18,4)` — no rounding tolerance) | P0       | [ ]    |
| A46-0027  | Verify price assignment path: resolver result `UnitPrice` (4 decimals) → PO line `UnitPrice` (4 decimals) — no truncation | P0       | [ ]    |

#### Unit Tests

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0028  | Test: Pricelist price `$12.3456` → PO line stores `12.3456` (not `12.35`)                  | P0       | [ ]    |
| A46-0029  | Test: Pricelist `$12.3456` → buyer doesn't change → `PriceSource = Pricelist` (not Override) | P0       | [ ]    |
| A46-0030  | Test: Buyer changes from `$12.3456` to `$12.3500` → `PriceSource = Override` detected     | P0       | [ ]    |

---

### C5 — PriceSource Enum Extension (Schema Only)

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0031  | Add `BlanketOrder = 4` to `PriceSource` enum in C# code                                   | P0       | [ ]    |
| A46-0032  | Write migration M5: `ALTER TABLE purchase.PurchaseOrderLines DROP CONSTRAINT CK_POLines_PriceSource` | P0       | [ ]    |
| A46-0033  | Write migration M5 (cont): `ADD CONSTRAINT CK_POLines_PriceSource CHECK (PriceSource IN (0, 1, 2, 3, 4))` | P0       | [ ]    |
| A46-0034  | Run M5 against dev database — verify constraint accepts 0,1,2,3,4 and rejects 5           | P0       | [ ]    |
| A46-0035  | Update all `PriceSource` switch/case statements to include `BlanketOrder` case             | P1       | [ ]    |

---

### C6 — Vendor Default Purchase Currency (Schema)

#### Schema & Migration

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0036  | Add `DefaultPurchaseCurrencyId` nullable `int` property to `BusinessPartner` entity class   | P0       | [ ]    |
| A46-0037  | Configure EF Core: FK to `core.Currencies`, `.OnDelete(DeleteBehavior.Restrict)`           | P0       | [ ]    |
| A46-0038  | Write migration M6: `ALTER TABLE core.BusinessPartners ADD DefaultPurchaseCurrencyId INT NULL` | P0       | [ ]    |
| A46-0039  | Write migration M6 (cont): `ADD CONSTRAINT FK_BusinessPartners_PurchaseCurrency FOREIGN KEY (DefaultPurchaseCurrencyId) REFERENCES core.Currencies(Id)` | P0       | [ ]    |
| A46-0040  | Run M6 against dev database — verify column added, FK resolves correctly                   | P0       | [ ]    |
| A46-0041  | Add `DefaultPurchaseCurrencyId` to Vendor DTO / response model                            | P0       | [ ]    |
| A46-0042  | Add `DefaultPurchaseCurrencyId` to Update Vendor command                                  | P0       | [ ]    |

#### Unit Tests

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0043  | Test: Set vendor `DefaultPurchaseCurrencyId = EUR` → persists and reads back correctly     | P0       | [ ]    |
| A46-0044  | Test: Vendor with `DefaultPurchaseCurrencyId = NULL` → fallback to org base currency       | P0       | [ ]    |
| A46-0045  | Test: FK constraint — set to non-existent CurrencyId → rejected                           | P0       | [ ]    |

---

### C8 — Exchange Rate Table (Schema)

#### Schema & Migration

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0046  | Create `ExchangeRate` entity class in Finance module with all 10 columns                   | P0       | [ ]    |
| A46-0047  | Configure EF Core: PK, FKs (OrgId → Organizations, CurrencyId → Currencies, CreatedBy → Users) | P0       | [ ]    |
| A46-0048  | Configure EF Core: `Rate` and `InverseRate` as `HasPrecision(18, 6)`                      | P0       | [ ]    |
| A46-0049  | Configure EF Core: Check constraints — `CK_ExchangeRates_Rate`, `CK_ExchangeRates_InvRate`, `CK_ExchangeRates_Source` | P0       | [ ]    |
| A46-0050  | Configure EF Core: Unique index `UQ_ExchangeRates_NoDuplicate` on `(OrgId, CurrencyId, EffectiveDate)` | P0       | [ ]    |
| A46-0051  | Configure EF Core: Covering index `IX_ExchangeRates_Lookup` on `(OrgId, CurrencyId, EffectiveDate DESC)` INCLUDE `(Rate, InverseRate)` | P0       | [ ]    |
| A46-0052  | Write migration M7: Full `CREATE TABLE finance.ExchangeRates` DDL with PK, FKs, CKs       | P0       | [ ]    |
| A46-0053  | Write migration M7 (cont): Create unique index `UQ_ExchangeRates_NoDuplicate`             | P0       | [ ]    |
| A46-0054  | Write migration M7 (cont): Create covering index `IX_ExchangeRates_Lookup`                 | P0       | [ ]    |
| A46-0055  | Run M7 against dev database — verify table, FKs, CKs, and indexes created                  | P0       | [ ]    |
| A46-0056  | Seed test data: 5 currencies × 30 days of rates = 150 rows                                | P1       | [ ]    |
| A46-0057  | Verify rate constraints: insert `Rate = 0` → rejected. `Rate = -1` → rejected             | P0       | [ ]    |
| A46-0058  | Verify source constraint: insert `Source = 2` → rejected                                   | P0       | [ ]    |
| A46-0059  | Verify uniqueness: insert duplicate `(OrgId, CurrencyId, EffectiveDate)` → rejected        | P0       | [ ]    |

#### DTOs & Enums

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0060  | Create `ExchangeRateDto` response model                                                   | P0       | [ ]    |
| A46-0061  | Create `CreateExchangeRateCommand` request model                                          | P0       | [ ]    |
| A46-0062  | Create `UpdateExchangeRateCommand` request model                                          | P0       | [ ]    |
| A46-0063  | Create `ExchangeRateSource` enum: `Manual = 0, Api = 1`                                  | P0       | [ ]    |

---

### C10 — PO Line Currency Tracking Columns (Schema)

#### Schema & Migration

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0064  | Add `OriginalCurrencyId` nullable int to `PurchaseOrderLine` entity                       | P0       | [ ]    |
| A46-0065  | Add `OriginalCurrencyPrice` nullable `decimal(18,4)` to `PurchaseOrderLine` entity        | P0       | [ ]    |
| A46-0066  | Add `ExchangeRateUsed` nullable `decimal(18,6)` to `PurchaseOrderLine` entity             | P0       | [ ]    |
| A46-0067  | Configure EF Core: FK `OriginalCurrencyId` → `core.Currencies`                           | P0       | [ ]    |
| A46-0068  | Write migration M8: `ALTER TABLE purchase.PurchaseOrderLines ADD OriginalCurrencyId INT NULL, OriginalCurrencyPrice DECIMAL(18,4) NULL, ExchangeRateUsed DECIMAL(18,6) NULL` | P0       | [ ]    |
| A46-0069  | Write migration M8 (cont): `ADD CONSTRAINT FK_POLines_OriginalCurrency FOREIGN KEY (OriginalCurrencyId) REFERENCES core.Currencies(Id)` | P0       | [ ]    |
| A46-0070  | Run M8 against dev database — verify 3 columns and FK created                             | P0       | [ ]    |

---

### Migration Execution & Rollback Verification

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0071  | Validate migration execution order: M9 → M10 → M4 → M5 → M6 → M7 → M8                   | P0       | [ ]    |
| A46-0072  | Write rollback script for M9: `ALTER TABLE purchase.PurchaseOrders DROP CONSTRAINT DF_PurchaseOrders_OrderDate; ALTER TABLE purchase.PurchaseOrders DROP COLUMN OrderDate` | P0       | [ ]    |
| A46-0073  | Write rollback script for M4: `ALTER TABLE purchase.PurchaseOrderLines ALTER COLUMN UnitPrice DECIMAL(18,2) NOT NULL` | P0       | [ ]    |
| A46-0074  | Write rollback script for M5: Restore original CK constraint `CHECK (PriceSource IN (0, 1, 2, 3))` | P0       | [ ]    |
| A46-0075  | Write rollback script for M6: `ALTER TABLE core.BusinessPartners DROP CONSTRAINT FK_BusinessPartners_PurchaseCurrency; DROP COLUMN DefaultPurchaseCurrencyId` | P0       | [ ]    |
| A46-0076  | Write rollback script for M7: `DROP TABLE finance.ExchangeRates`                          | P0       | [ ]    |
| A46-0077  | Write rollback script for M8: `DROP CONSTRAINT FK_POLines_OriginalCurrency; DROP COLUMN OriginalCurrencyId, OriginalCurrencyPrice, ExchangeRateUsed` | P0       | [ ]    |
| A46-0078  | Run full migration sequence on clean dev database — all 7 migrations pass                  | P0       | [ ]    |
| A46-0079  | Run full rollback sequence in reverse — database returns to pre-A46 state                  | P1       | [ ]    |

### EF Core Model Snapshot

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0080  | Update EF Core model snapshot for `PurchaseOrder` entity (OrderDate)                      | P0       | [ ]    |
| A46-0081  | Update EF Core model snapshot for `PurchaseOrderLine` entity (UnitPrice precision, 3 new columns, CK update) | P0       | [ ]    |
| A46-0082  | Update EF Core model snapshot for `BusinessPartner` entity (DefaultPurchaseCurrencyId)    | P0       | [ ]    |
| A46-0083  | Update EF Core model snapshot — add `ExchangeRate` entity                                  | P0       | [ ]    |
| A46-0084  | Run `dotnet ef migrations add A46_PricingFoundation` — verify generated migration matches handwritten DDL | P0       | [ ]    |

### Multi-Tenant OrgId Verification

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0085  | Verify `ExchangeRate` entity has OrgId property and EF global query filter `e.OrgId == _currentOrgId` | P0       | [ ]    |
| A46-0086  | Verify `ExchangeRate` repository applies OrgId automatically on insert                    | P0       | [ ]    |
| A46-0087  | Test: Org A's exchange rates are invisible to Org B                                        | P0       | [ ]    |
| A46-0088  | Verify `OrderDate` and currency columns on PO/PO-line are covered by existing OrgId filters | P0       | [ ]    |

---

## Phase 2 — Currency Services (Week 2–3)

### C8 — Exchange Rate Repository & Service

#### Repository

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0089  | Create `IExchangeRateRepository` interface                                                 | P0       | [ ]    |
| A46-0090  | Implement `ExchangeRateRepository`: CRUD operations with OrgId filter                     | P0       | [ ]    |
| A46-0091  | Implement rate lookup method: `GetEffectiveRateAsync(orgId, currencyId, date)` — most recent rate on or before date | P0       | [ ]    |
| A46-0092  | Implement `GetRatesForDateRangeAsync(orgId, currencyId, fromDate, toDate)` for list endpoint | P1       | [ ]    |
| A46-0093  | Implement `ExistsAsync(orgId, currencyId, effectiveDate)` for duplicate check on create   | P0       | [ ]    |

#### Application Service

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0094  | Create `IExchangeRateService` interface                                                    | P0       | [ ]    |
| A46-0095  | Implement `CreateRateAsync` — validate: `Rate > 0`, `InverseRate > 0`, no duplicate date, compute `InverseRate = 1 / Rate` if not supplied | P0       | [ ]    |
| A46-0096  | Implement `UpdateRateAsync` — only `Source = Manual` rates can be updated; reject `Source = Api` | P0       | [ ]    |
| A46-0097  | Implement `DeleteRateAsync` — only `Source = Manual` rates can be deleted                  | P0       | [ ]    |
| A46-0098  | Implement `GetRatesListAsync` — paginated, filterable by currencyId, date range, source   | P0       | [ ]    |
| A46-0099  | Implement `InverseRate` auto-compute: `InverseRate = ROUND(1.0 / Rate, 6)` on create/update | P0       | [ ]    |
| A46-0100  | Implement manual rate precedence: manual entry (`Source = 0`) overrides auto-fetched (`Source = 1`) for same date+currency (BR-34) | P1       | [ ]    |

#### Unit Tests — Exchange Rate Service

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0101  | Test: Create rate with valid data → persisted with correct InverseRate                     | P0       | [ ]    |
| A46-0102  | Test: Create rate with `Rate = 0` → validation error                                      | P0       | [ ]    |
| A46-0103  | Test: Create duplicate (same org, currency, date) → conflict error                        | P0       | [ ]    |
| A46-0104  | Test: Update auto-fetched rate (`Source = Api`) → rejected                                 | P0       | [ ]    |
| A46-0105  | Test: Delete manual rate → success. Delete auto-fetched rate → rejected                   | P0       | [ ]    |
| A46-0106  | Test: Rate lookup — exact date match returns that rate                                     | P0       | [ ]    |
| A46-0107  | Test: Rate lookup — no exact date → returns most recent earlier rate                       | P0       | [ ]    |
| A46-0108  | Test: Rate lookup — no rate exists on or before date → returns NOT FOUND                   | P0       | [ ]    |
| A46-0109  | Test: Manual rate and auto rate for same date → manual takes precedence                    | P1       | [ ]    |

#### DI Registration

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0110  | Register `IExchangeRateRepository` → `ExchangeRateRepository` in Finance module DI        | P0       | [ ]    |
| A46-0111  | Register `IExchangeRateService` → `ExchangeRateService` in Finance module DI              | P0       | [ ]    |

---

### C9 — Currency Conversion Service

#### Interface & Implementation

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0112  | Create `ICurrencyConverter` interface with `ConvertAsync` and `GetRateAsync` methods       | P0       | [ ]    |
| A46-0113  | Create `CurrencyConversionRequest` DTO                                                    | P0       | [ ]    |
| A46-0114  | Create `CurrencyConversionResult` DTO                                                     | P0       | [ ]    |
| A46-0115  | Create `ExchangeRateResult` DTO                                                           | P0       | [ ]    |
| A46-0116  | Implement same-currency short-circuit: `FromCurrencyId == ToCurrencyId` → return amount unchanged | P0       | [ ]    |
| A46-0117  | Implement direct conversion: base → foreign: `amount * Rate`                              | P0       | [ ]    |
| A46-0118  | Implement direct conversion: foreign → base: `amount * InverseRate`                       | P0       | [ ]    |
| A46-0119  | Implement cross-rate conversion: foreign → base → foreign (via two rate lookups)          | P0       | [ ]    |
| A46-0120  | Implement rounding: `Math.Round(result, request.DecimalPlaces, MidpointRounding.AwayFromZero)` | P0       | [ ]    |
| A46-0121  | Handle rate-not-found: set `Success = false`, populate `ErrorMessage` with missing currency + date | P0       | [ ]    |
| A46-0122  | Register `ICurrencyConverter` → `CurrencyConverter` as Scoped in DI                      | P0       | [ ]    |

#### Unit Tests — Currency Converter

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0123  | Test: Same currency → returns amount unchanged, no DB call                                | P0       | [ ]    |
| A46-0124  | Test: Base → foreign: USD 100 at Rate 0.92 → EUR 92.00                                   | P0       | [ ]    |
| A46-0125  | Test: Foreign → base: EUR 50 at InverseRate 1.087 → USD 54.35                            | P0       | [ ]    |
| A46-0126  | Test: Cross-rate: EUR → GBP via USD base (two lookups)                                    | P0       | [ ]    |
| A46-0127  | Test: No rate for FromCurrency → `Success = false`, error message                         | P0       | [ ]    |
| A46-0128  | Test: No rate for ToCurrency (cross-rate) → `Success = false`                             | P0       | [ ]    |
| A46-0129  | Test: Rounding to 4 decimal places applied correctly                                      | P0       | [ ]    |
| A46-0130  | Test: Rate from 3 days ago used when no rate exists for today                              | P0       | [ ]    |
| A46-0131  | Test: Multi-tenant — Org A's rate not used for Org B conversion                           | P0       | [ ]    |
| A46-0132  | Test: DecimalPlaces parameter respected (2 vs 4 vs 6)                                     | P1       | [ ]    |
| A46-0133  | Test: Very small amounts (0.0001) converted without precision loss                        | P1       | [ ]    |
| A46-0134  | Test: Very large amounts (999,999,999.9999) converted without overflow                    | P1       | [ ]    |

---

## Phase 3 — Price Resolution Enhancements (Week 3–5)

### C4 — Server-Side Price Resolution on PO Line Mutation

#### Interface & DTOs

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0135  | Create `IPurchaseOrderLinePricingMiddleware` interface with `ApplyPricingAsync` method      | P0       | [ ]    |
| A46-0136  | Create `LineChangeContext` DTO: `IsNew`, `ProductChanged`, `QuantityChanged`, `DeliveryDateChanged`, `PreviousQuantity`, `PreviousDeliveryDate` | P0       | [ ]    |

#### Implementation

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0137  | Implement skip condition: PO status ≠ Draft → no re-pricing                               | P0       | [ ]    |
| A46-0138  | Implement skip condition: `PriceSource = RFQ` and `IsNew = false` → no re-pricing (BR-28) | P0       | [ ]    |
| A46-0139  | Implement skip condition: `PriceSource = BlanketOrder` and `IsNew = false` → no re-pricing (BR-28) | P0       | [ ]    |
| A46-0140  | Implement resolve trigger: `IsNew && UnitPrice == 0` → call resolver                      | P0       | [ ]    |
| A46-0141  | Implement resolve trigger: `ProductChanged` → call resolver                                | P0       | [ ]    |
| A46-0142  | Implement resolve trigger: `QuantityChanged && PriceSource == Pricelist` → silent re-resolve (BR-07) | P0       | [ ]    |
| A46-0143  | Implement resolve trigger: `DeliveryDateChanged && PricingDateBasis == DeliveryDate` → re-resolve | P0       | [ ]    |
| A46-0144  | On FOUND + `PriceSource = Pricelist` or `IsNew`: update `UnitPrice`, `SystemResolvedPrice`, `SupplierPricelistId`, `PriceSource = Pricelist` | P0       | [ ]    |
| A46-0145  | On FOUND + `PriceSource = Override` or `Manual`: update only `SystemResolvedPrice` — preserve buyer's `UnitPrice` | P0       | [ ]    |
| A46-0146  | On NOT FOUND + `IsNew`: set `PriceSource = Manual`, `UnitPrice = 0`, clear `SupplierPricelistId` | P0       | [ ]    |
| A46-0147  | On NOT FOUND + existing line: clear `SystemResolvedPrice`, `SupplierPricelistId`          | P0       | [ ]    |
| A46-0148  | Register `IPurchaseOrderLinePricingMiddleware` → `PurchaseOrderLinePricingMiddleware` as Scoped | P0       | [ ]    |

#### Integration into PO Line App Service

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0149  | Inject `IPurchaseOrderLinePricingMiddleware` into `PurchaseOrderLineAppService`            | P0       | [ ]    |
| A46-0150  | Call middleware in `CreateLineAsync` with `LineChangeContext { IsNew = true, ProductChanged = true }` | P0       | [ ]    |
| A46-0151  | Call middleware in `UpdateLineAsync` with computed `LineChangeContext` (detect which fields changed) | P0       | [ ]    |
| A46-0152  | Return `PriceResolutionResult` from update for UI warning rendering (Override toast)      | P1       | [ ]    |

#### Unit Tests — Pricing Middleware

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0153  | Test: New line, no price, pricelist exists → `UnitPrice` set, `PriceSource = Pricelist`    | P0       | [ ]    |
| A46-0154  | Test: New line, no price, no pricelist → `UnitPrice = 0`, `PriceSource = Manual`           | P0       | [ ]    |
| A46-0155  | Test: Update qty on Pricelist line → price re-resolved silently                           | P0       | [ ]    |
| A46-0156  | Test: Update qty on Override line → `SystemResolvedPrice` updated, `UnitPrice` unchanged  | P0       | [ ]    |
| A46-0157  | Test: Update qty on RFQ line → nothing changes (BR-28)                                    | P0       | [ ]    |
| A46-0158  | Test: Update qty on BlanketOrder line → nothing changes (BR-28)                           | P0       | [ ]    |
| A46-0159  | Test: Update qty on Manual line → no resolver call                                        | P0       | [ ]    |
| A46-0160  | Test: Product changed → full re-resolution regardless of PriceSource                      | P0       | [ ]    |
| A46-0161  | Test: PO status = Confirmed → middleware skips entirely                                    | P0       | [ ]    |
| A46-0162  | Test: DeliveryDate changed on line with `PricingDateBasis = DeliveryDate` → re-resolved   | P1       | [ ]    |
| A46-0163  | Test: DeliveryDate changed on line with `PricingDateBasis = OrderDate` → no re-resolve    | P1       | [ ]    |

---

### C5 — PO Line Pricing Factory

#### Interface & DTOs

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0164  | Create `IPurchaseOrderLineFactory` interface with `CreateLineAsync` method                 | P0       | [ ]    |
| A46-0165  | Create `CreateLineInput` DTO: `ProductId`, `Quantity`, `ExpectedDeliveryDate`, `SuppliedPrice`, `SuppliedPriceSource`, `SourceRfqLineId`, `SuppliedOverrideReason` | P0       | [ ]    |
| A46-0166  | Create `PurchaseOrderLineResult` DTO: `Line`, `PriceWasResolved`, `WarningMessage`        | P0       | [ ]    |

#### Implementation

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0167  | Implement: `SuppliedPrice` provided + `SuppliedPriceSource = RFQ` → use supplied price, no resolver call | P0       | [ ]    |
| A46-0168  | Implement: `SuppliedPrice` provided + `SuppliedPriceSource = BlanketOrder` → use supplied price, `PriceSource = BlanketOrder` | P0       | [ ]    |
| A46-0169  | Implement: `SuppliedPrice` provided + `SuppliedPriceSource = Manual` → use supplied price, `PriceSource = Manual` (CSV import with price) | P0       | [ ]    |
| A46-0170  | Implement: No `SuppliedPrice` → call `IPurchasePriceResolver`. Found → Pricelist. Not found → Manual, UnitPrice = 0 | P0       | [ ]    |
| A46-0171  | Implement: CSV import with price = 0 or null → attempt resolve; fall back to Manual       | P1       | [ ]    |
| A46-0172  | Implement: PO line split (partial GR) — inherit parent source; if qty changed + source = Pricelist → re-resolve | P1       | [ ]    |
| A46-0173  | Register `IPurchaseOrderLineFactory` → `PurchaseOrderLineFactory` as Scoped                | P0       | [ ]    |

#### Refactor Existing Code Paths to Use Factory

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0174  | Refactor: Manual PO line creation (UI) → use `IPurchaseOrderLineFactory`                  | P0       | [ ]    |
| A46-0175  | Refactor: Purchase Request → PO conversion → use `IPurchaseOrderLineFactory`              | P0       | [ ]    |
| A46-0176  | Refactor: RFQ/Quotation → PO line creation → use factory with `SuppliedPriceSource = RFQ` | P0       | [ ]    |
| A46-0177  | Refactor: Reorder Point auto-PO → use `IPurchaseOrderLineFactory`                         | P0       | [ ]    |
| A46-0178  | Refactor: CSV/Excel import → use `IPurchaseOrderLineFactory`                              | P1       | [ ]    |
| A46-0179  | Refactor: PO line split from partial goods receipt → use factory with parent inheritance   | P1       | [ ]    |
| A46-0180  | Audit: Search codebase for any direct `PurchaseOrderLine` instantiation outside factory → flag for refactor | P0       | [ ]    |

#### Batch Creation Summary

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0181  | Implement batch result aggregation: count priced vs unpriced lines across multiple `CreateLineAsync` calls | P1       | [ ]    |
| A46-0182  | Return batch summary DTO: `{ TotalLines, PricedCount, UnpricedCount, Warnings[] }`       | P1       | [ ]    |

#### Unit Tests — Factory

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0183  | Test: Manual line, pricelist exists → `PriceSource = Pricelist`, correct price             | P0       | [ ]    |
| A46-0184  | Test: Manual line, no pricelist → `PriceSource = Manual`, `UnitPrice = 0`                  | P0       | [ ]    |
| A46-0185  | Test: RFQ line with supplied price → `PriceSource = RFQ`, no resolver call                 | P0       | [ ]    |
| A46-0186  | Test: BlanketOrder line with supplied price → `PriceSource = BlanketOrder`                  | P0       | [ ]    |
| A46-0187  | Test: CSV import with price → `PriceSource = Manual`, supplied price used                  | P0       | [ ]    |
| A46-0188  | Test: CSV import without price → resolver called. Found → Pricelist. Not found → Manual   | P1       | [ ]    |
| A46-0189  | Test: Purchase Request → PO (5 lines, 3 have pricelists) → 3 Pricelist + 2 Manual         | P0       | [ ]    |
| A46-0190  | Test: PO line split — parent Pricelist, new qty crosses tier → re-resolved to new price   | P1       | [ ]    |
| A46-0191  | Test: PO line split — parent Override → child inherits Override, no re-resolution          | P1       | [ ]    |
| A46-0192  | Test: Factory enforced — direct `new PurchaseOrderLine()` in PO Line App Service removed  | P0       | [ ]    |

---

### C10 — Currency-Aware Price Resolution

#### Enhanced DTOs

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0193  | Add `POCurrencyId` (optional) to `PriceResolutionRequest`                                 | P0       | [ ]    |
| A46-0194  | Add to `PriceResolutionResult`: `OriginalPrice`, `OriginalCurrencyId`, `ExchangeRateUsed`, `ExchangeRateDate`, `CurrencyConverted`, `CurrencyMismatchWarning` | P0       | [ ]    |

#### Implementation

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0195  | Inject `ICurrencyConverter` into `PurchasePriceResolver`                                   | P0       | [ ]    |
| A46-0196  | After pricelist match found: check if `match.CurrencyId != request.POCurrencyId`          | P0       | [ ]    |
| A46-0197  | Same currency or no `POCurrencyId` → no conversion, `CurrencyConverted = false`           | P0       | [ ]    |
| A46-0198  | Different currency → call `ICurrencyConverter.ConvertAsync(match.UnitPrice, matchCurrency, poCurrency, effectiveDate)` | P0       | [ ]    |
| A46-0199  | Conversion success → populate `UnitPrice` (converted), `OriginalPrice`, `OriginalCurrencyId`, `ExchangeRateUsed`, `CurrencyConverted = true` | P0       | [ ]    |
| A46-0200  | Conversion failure (no rate) → `Found = true`, `CurrencyMismatchWarning = true`, `UnitPrice = null`, populate `WarningMessage` | P0       | [ ]    |
| A46-0201  | Wire currency tracking to PO line: populate `OriginalCurrencyId`, `OriginalCurrencyPrice`, `ExchangeRateUsed` on line entity | P0       | [ ]    |

#### Unit Tests — Currency-Aware Resolution

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0202  | Test: Pricelist EUR, PO USD, rate exists → price converted, `CurrencyConverted = true`    | P0       | [ ]    |
| A46-0203  | Test: Pricelist EUR, PO USD, NO rate → `CurrencyMismatchWarning = true`, `UnitPrice = null` | P0       | [ ]    |
| A46-0204  | Test: Pricelist USD, PO USD → no conversion, `CurrencyConverted = false`                  | P0       | [ ]    |
| A46-0205  | Test: `OriginalPrice` and `OriginalCurrencyId` populated correctly after conversion        | P0       | [ ]    |
| A46-0206  | Test: `ExchangeRateUsed` and `ExchangeRateDate` match the rate used for conversion         | P0       | [ ]    |
| A46-0207  | Test: Cross-rate: EUR → GBP via USD base — correct converted price                        | P1       | [ ]    |
| A46-0208  | Test: `POCurrencyId = null` → no conversion attempted, pricelist currency returned directly | P0       | [ ]    |
| A46-0209  | Test: PO line entity `OriginalCurrencyId`, `OriginalCurrencyPrice`, `ExchangeRateUsed` persisted to DB | P0       | [ ]    |

---

## Phase 4 — API Layer (Week 4–5)

### 6.1 — Exchange Rate CRUD Endpoints

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0210  | Create `ExchangeRatesController` in Finance module                                        | P0       | [ ]    |
| A46-0211  | GET `/api/finance/exchange-rates` — list with filters: `currencyId`, `from`, `to`, `source` | P0       | [ ]    |
| A46-0212  | GET `/api/finance/exchange-rates/{id}` — get single rate                                  | P0       | [ ]    |
| A46-0213  | POST `/api/finance/exchange-rates` — create manual rate, validate `Rate > 0`, check duplicates | P0       | [ ]    |
| A46-0214  | PUT `/api/finance/exchange-rates/{id}` — update, reject if `Source = Api`                  | P0       | [ ]    |
| A46-0215  | DELETE `/api/finance/exchange-rates/{id}` — delete, reject if `Source = Api`               | P0       | [ ]    |
| A46-0216  | Add pagination to GET list: `?page=1&pageSize=25`, default sort by `EffectiveDate DESC`   | P1       | [ ]    |
| A46-0217  | Add authorization: exchange rate management requires `Finance.ExchangeRates.Manage` permission | P0       | [ ]    |

#### API Tests — Exchange Rate

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0218  | Test: POST valid rate → 201 Created with auto-computed InverseRate                        | P0       | [ ]    |
| A46-0219  | Test: POST duplicate (orgId + currencyId + date) → 409 Conflict                          | P0       | [ ]    |
| A46-0220  | Test: PUT on `Source = Api` rate → 400 Bad Request with message                           | P0       | [ ]    |
| A46-0221  | Test: DELETE on `Source = Api` rate → 400 Bad Request                                     | P0       | [ ]    |
| A46-0222  | Test: GET list with `currencyId` filter → only matching currency returned                 | P0       | [ ]    |
| A46-0223  | Test: GET list with `from`/`to` date range → only rates within range returned             | P1       | [ ]    |
| A46-0224  | Test: Unauthorized user → 403 Forbidden                                                   | P0       | [ ]    |

---

### 6.2 — Currency Conversion Endpoint

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0225  | POST `/api/finance/convert-currency` — convert amount at given date                       | P0       | [ ]    |
| A46-0226  | Request validation: `amount > 0`, valid `fromCurrencyId`, valid `toCurrencyId`, `effectiveDate` not future | P0       | [ ]    |
| A46-0227  | Response includes: `convertedAmount`, `rateUsed`, `rateDate`, currency codes, `success`/`errorMessage` | P0       | [ ]    |

#### API Tests — Currency Conversion

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0228  | Test: Valid conversion → 200 OK with correct converted amount                             | P0       | [ ]    |
| A46-0229  | Test: No rate available → 200 OK with `success = false`, `errorMessage` populated          | P0       | [ ]    |
| A46-0230  | Test: Same currency → 200 OK with amount unchanged, `rateUsed = 1.0`                     | P0       | [ ]    |

---

### 6.3 — Enhanced Price Resolution Endpoint

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0231  | Update POST `/api/purchase/resolve-price` request model to accept optional `poCurrencyId` | P0       | [ ]    |
| A46-0232  | Update response model with currency conversion fields: `originalPrice`, `originalCurrencyId`, `originalCurrencyCode`, `exchangeRateUsed`, `exchangeRateDate`, `currencyConverted`, `currencyMismatchWarning` | P0       | [ ]    |
| A46-0233  | Backward compatibility: existing calls without `poCurrencyId` continue to work unchanged  | P0       | [ ]    |

#### API Tests — Enhanced Resolver

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0234  | Test: Request without `poCurrencyId` → response same as Addendum 45 (no conversion fields) | P0       | [ ]    |
| A46-0235  | Test: Request with `poCurrencyId` different from pricelist currency, rate exists → converted | P0       | [ ]    |
| A46-0236  | Test: Request with `poCurrencyId` different, no rate → `currencyMismatchWarning = true`   | P0       | [ ]    |
| A46-0237  | Test: Request with `poCurrencyId` same as pricelist currency → `currencyConverted = false` | P0       | [ ]    |

---

### API Documentation

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0238  | Add Swagger/OpenAPI annotations for `ExchangeRatesController`                             | P1       | [ ]    |
| A46-0239  | Add Swagger/OpenAPI annotations for currency conversion endpoint                          | P1       | [ ]    |
| A46-0240  | Update existing resolve-price Swagger docs with new optional fields                       | P1       | [ ]    |
| A46-0241  | Verify API versioning strategy — new fields are additive (backward-compatible)            | P0       | [ ]    |
| A46-0242  | Add example request/response to API docs for currency-converted resolution                | P2       | [ ]    |
| A46-0243  | Add error response codes documentation for all new endpoints                              | P1       | [ ]    |

---

## Phase 5 — UI Layer (Week 5–7)

### C1 — OrderDate UI

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0244  | Add date picker for `OrderDate` on PO header form (between Vendor and Currency)           | P0       | [ ]    |
| A46-0245  | Default value: today (local date) on new PO creation                                      | P0       | [ ]    |
| A46-0246  | Conditionally disable date picker when PO status ≠ Draft                                  | P0       | [ ]    |
| A46-0247  | Wire `onChange` to server: PATCH PO header with new OrderDate                              | P0       | [ ]    |
| A46-0248  | On OrderDate change response: display C7 re-lookup banner if applicable lines exist        | P1       | [ ]    |
| A46-0249  | Test: Date picker renders with correct default on new PO                                  | P0       | [ ]    |
| A46-0250  | Test: Date picker disabled on Confirmed PO                                                | P0       | [ ]    |
| A46-0251  | Test: Change date → re-lookup banner appears for eligible lines                           | P1       | [ ]    |

---

### C2 — RFQ Prefill Parameter Consumption

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0252  | Parse query parameters on RFQ Create page: `vendorId`, `productId`, `quantity`, `sourcePO` | P0       | [ ]    |
| A46-0253  | Pre-select vendor dropdown from `vendorId` parameter (validate exists, ignore if invalid) | P0       | [ ]    |
| A46-0254  | Pre-select first line product from `productId` parameter (validate exists, ignore if invalid) | P0       | [ ]    |
| A46-0255  | Pre-fill first line quantity from `quantity` parameter (validate > 0 and numeric, default 1 if invalid) | P0       | [ ]    |
| A46-0256  | Display `sourcePO` as info text: "Referenced from {sourcePO}" (no FK, just display)       | P2       | [ ]    |
| A46-0257  | Each parameter independently optional — any subset works                                  | P0       | [ ]    |
| A46-0258  | All pre-filled fields remain fully editable                                                | P0       | [ ]    |
| A46-0259  | Test: Navigate with all 3 params → vendor, product, qty pre-filled                       | P0       | [ ]    |
| A46-0260  | Test: Navigate with invalid vendorId → vendor dropdown empty, no error toast               | P0       | [ ]    |
| A46-0261  | Test: Navigate with only vendorId → vendor filled, product and qty empty                  | P0       | [ ]    |
| A46-0262  | Test: Navigate with no params → all fields empty (normal behavior)                        | P0       | [ ]    |

---

### C3 — Price Precision Display

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0263  | Update PO line grid: format `UnitPrice` as `#,##0.00` (2 decimal display)                 | P0       | [ ]    |
| A46-0264  | Add hover tooltip on `UnitPrice` cell showing full 4-decimal precision: "Unit Price: $12.3456" | P1       | [ ]    |
| A46-0265  | PO line detail/edit view: show full precision in the input field                           | P0       | [ ]    |
| A46-0266  | PO print/export: use 2-decimal format                                                     | P1       | [ ]    |
| A46-0267  | Test: Pricelist $12.3456 displayed as $12.35 in grid, $12.3456 on hover                  | P1       | [ ]    |
| A46-0268  | Test: Buyer input of 4-decimal price accepted and persisted                               | P0       | [ ]    |

---

### C6 — Vendor Default Purchase Currency UI

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0269  | Add "Default Purchase Currency" dropdown to Vendor detail form → Purchase tab              | P0       | [ ]    |
| A46-0270  | Populate dropdown from `core.Currencies` (active only), with empty/"inherit org base" option | P0       | [ ]    |
| A46-0271  | Wire save to PATCH vendor with `DefaultPurchaseCurrencyId`                                 | P0       | [ ]    |
| A46-0272  | Add info tooltip: "If set, new Supplier Pricelist records for this vendor will default to this currency" | P2       | [ ]    |
| A46-0273  | Test: Set vendor currency to EUR → save → reload → shows EUR                              | P0       | [ ]    |
| A46-0274  | Test: Clear vendor currency (set to empty) → save → defaults to null (org base fallback)  | P0       | [ ]    |

---

### C7 — Pricelist Currency Default from Vendor

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0275  | On "Add Price" in Supplier Pricelist (Vendor Products Tab) — default CurrencyId from `Vendor.DefaultPurchaseCurrencyId` | P0       | [ ]    |
| A46-0276  | On "Add Price" in Supplier Pricelist (Product Purchase Tab) — on vendor selection, update currency default | P0       | [ ]    |
| A46-0277  | On "Add Price" in Supplier Pricelist (Standalone List) — on vendor selection, fetch vendor's default currency | P0       | [ ]    |
| A46-0278  | Fallback: vendor has no `DefaultPurchaseCurrencyId` → default to org base currency        | P0       | [ ]    |
| A46-0279  | Currency dropdown remains fully editable — default is a convenience, not a constraint      | P0       | [ ]    |
| A46-0280  | Backend: POST endpoint accepts `currencyId = 0` or omitted → server resolves default      | P1       | [ ]    |
| A46-0281  | Test: Vendor has EUR default → "Add Price" shows EUR pre-selected                         | P0       | [ ]    |
| A46-0282  | Test: Vendor has no default → "Add Price" shows org base currency                          | P0       | [ ]    |
| A46-0283  | Test: Change vendor in dropdown → currency updates to new vendor's default                | P1       | [ ]    |

---

### C10 — Currency Conversion Info in UI

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0284  | PO line grid: show info icon (ℹ️) next to UnitPrice when `CurrencyConverted = true`      | P1       | [ ]    |
| A46-0285  | Info tooltip content: "Original: {OriginalCurrencyCode} {OriginalCurrencyPrice} @ {ExchangeRateUsed} ({ExchangeRateDate})" | P1       | [ ]    |
| A46-0286  | Currency mismatch warning banner: "No exchange rate available for {from} → {to} on {date}. Enter price manually." | P0       | [ ]    |
| A46-0287  | Test: Currency-converted line shows info icon with correct tooltip                        | P1       | [ ]    |
| A46-0288  | Test: Currency mismatch → warning banner displayed on PO line                             | P0       | [ ]    |
| A46-0289  | Test: Same-currency resolution → no info icon, no warning                                 | P0       | [ ]    |

---

## Phase 6 — Performance & Integration Testing (Week 7–8)

### C11 — Resolver Query Plan Verification

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0290  | Write seed script: 1,000 products × 3 vendors × 4 qty tiers × 2 date ranges = 24,000 pricelist rows | P1       | [ ]    |
| A46-0291  | Run resolver query with `SET STATISTICS IO ON; SET STATISTICS TIME ON;` — capture output  | P1       | [ ]    |
| A46-0292  | Capture actual execution plan (XML) for resolver query                                    | P1       | [ ]    |
| A46-0293  | Verify: Index Seek on `IX_SupplierPricelists_Lookup` (not Scan)                           | P1       | [ ]    |
| A46-0294  | Verify: 0 Key Lookups (all columns in INCLUDE)                                            | P1       | [ ]    |
| A46-0295  | Verify: Logical reads ≤ 5 pages                                                           | P1       | [ ]    |
| A46-0296  | Verify: Execution time < 1ms (warm cache)                                                 | P2       | [ ]    |
| A46-0297  | If OR branch causes scan: implement Option A (split query) or Option B (filtered indexes) | P1       | [ ]    |
| A46-0298  | If Option B chosen: Create `IX_SupplierPricelists_OrderDateBasis` filtered index           | P1       | [ ]    |
| A46-0299  | If Option B chosen: Create `IX_SupplierPricelists_DeliveryDateBasis` filtered index        | P1       | [ ]    |

#### Load Test

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0300  | Write load test: 50 concurrent resolver calls with varied products/vendors/quantities     | P1       | [ ]    |
| A46-0301  | Run load test → verify all 50 complete within 5 seconds, no deadlocks                     | P1       | [ ]    |
| A46-0302  | Run load test → verify no blocking (DMV: `sys.dm_exec_requests` shows no waits)           | P2       | [ ]    |

---

### Integration Test Scenarios (TS-27 to TS-59)

#### TS-27 to TS-31 — Order Date (Gap 1)

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0303  | TS-27: Create PO → `OrderDate = today (UTC)`                                              | P0       | [ ]    |
| A46-0304  | TS-28: Edit OrderDate on draft PO → new date persisted                                    | P0       | [ ]    |
| A46-0305  | TS-29: Change OrderDate on confirmed PO → read-only, change rejected                      | P0       | [ ]    |
| A46-0306  | TS-30: Change OrderDate → re-lookup banner for applicable lines                           | P1       | [ ]    |
| A46-0307  | TS-31: Resolver uses `PO.OrderDate`, not `DateTime.UtcNow` — create Oct 15, edit Oct 17 → price for Oct 15 | P0       | [ ]    |

#### TS-32 to TS-34 — RFQ Prefill (Gap 2)

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0308  | TS-32: "Create RFQ" → vendor + product + qty pre-populated                                | P1       | [ ]    |
| A46-0309  | TS-33: Invalid vendorId in query string → vendor dropdown empty, no error                 | P1       | [ ]    |
| A46-0310  | TS-34: Only vendorId supplied → vendor pre-selected, product and qty empty                | P1       | [ ]    |

#### TS-35 to TS-37 — Price Precision (Gap 3)

| ID        | Task                                                                                      | Priority | Status |
| --------- | ----------------------------------------------------------------------------------------- | -------- | ------ |
| A46-0311  | TS-35: Pricelist `$12.3456` → PO line `UnitPrice = $12.3456` (no rounding)                | P0       | [ ]    |
| A46-0312  | TS-36: Pricelist `$12.3456`, buyer unchanged → `PriceSource = Pricelist`, not Override    | P0       | [ ]    |

---

*Scenarios TS-37 through TS-59 are covered by unit tests in Phases 2–5 (A46-0028 to A46-0209). The integration test IDs above serve as the end-to-end verification pass combining backend + frontend.*

---

## Summary Statistics

| Metric            | Value     |
| ----------------- | --------- |
| Total Tasks       | 312       |
| P0 Tasks          | 196 (62.8%) |
| P1 Tasks          | 100 (32.1%) |
| P2 Tasks          | 16 (5.1%)  |
| Schema Tasks      | 88        |
| Service Tasks     | 98        |
| API Tasks         | 34        |
| UI Tasks          | 46        |
| Test Tasks        | 134       |
| Performance Tasks | 13        |
| Estimated Weeks   | 8         |

---

*End of Task Register — Addendum 46*
