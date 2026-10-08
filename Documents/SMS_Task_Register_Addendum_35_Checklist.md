# SMS Task Register — Addendum 35: Multi-Currency Support

**Document:** SMS-TR-ADD-035 v1.0 | **FSD:** SMS-FSD-ADD-035 v1.1
**Total:** 62 Tasks | 216 Hours | 27 Dev Days | 8 Migrations | 12 New Endpoints | 5 UI Panels
**Depends On:** SMS-FSD-ADD-032 (Sale Inquiry & Quotation — currency_code on SaleQuotations)

---

## Phase 1 — Foundation: Currencies & Exchange Rates (C1, C2)

**Priority:** P0 — Critical Path | **Estimate:** 64 Hours (8 Days) | **FSD:** §2, §3, §9 M1–M2, M8

> **⚠️ Everything downstream depends on this phase.** C1 (Currencies) is the FK target for every other change. C2 (CurrencyRates) supplies the rate lookup used by all conversions and rate locking.

### Track 1A — Currencies Table & CRUD (C1)

- [ ] **A35-P1-01** — Create lookups.Currencies Table (Migration M1 — DDL) `2h`
  - Schema: `lookups` | FSD: §2.2, §9.2 M1 | Depends: — | Tests: T-C1-01–T-C1-10
  - CREATE TABLE lookups.Currencies: currency_id INT IDENTITY PK, org_id INT FK → tenants.Organizations, code NVARCHAR(3) NOT NULL, name NVARCHAR(60) NOT NULL, symbol NVARCHAR(5) NOT NULL, decimal_places INT NOT NULL DEFAULT 2, rounding DECIMAL(18,6) NOT NULL DEFAULT 0.01, symbol_position NVARCHAR(6) NOT NULL DEFAULT 'before', is_active BIT NOT NULL DEFAULT 1, display_order INT NOT NULL DEFAULT 0, created_at DATETIME2, updated_at DATETIME2. UNIQUE: UQ_Currencies_OrgCode (org_id, code). INDEX: IX_Currencies_Active (org_id, is_active, display_order). CHECK constraints: CK_Currencies_Code (LEN=3, UPPER), CK_Currencies_DecimalPlaces (0–3), CK_Currencies_Rounding (>0), CK_Currencies_SymbolPosition ('before'/'after').

- [ ] **A35-P1-02** — Currency EF Core Entity & Configuration `2h`
  - Schema: `lookups` | FSD: §2.2 | Depends: A35-P1-01 | Tests: —
  - Add Currency entity class: CurrencyId, OrgId, Code, Name, Symbol, DecimalPlaces, Rounding, SymbolPosition, IsActive, DisplayOrder, CreatedAt, UpdatedAt. IEntityTypeConfiguration<Currency>: ToTable("Currencies", "lookups"), HasQueryFilter(c => c.OrgId == _tenantId), HasIndex for UQ_Currencies_OrgCode (IsUnique), HasCheckConstraint for all 4 CHECK constraints. Navigation: ICollection<CurrencyRate> Rates. DbContext: DbSet<Currency> Currencies.

- [ ] **A35-P1-03** — CurrencyService — CRUD Operations `3h`
  - Schema: `lookups` | FSD: §2.2, BR-C1-01–BR-C1-05 | Depends: A35-P1-02 | Tests: T-C1-01–T-C1-07
  - ICurrencyService methods: GetAllAsync(orgId, activeOnly), GetByIdAsync(orgId, currencyId), CreateAsync(orgId, dto), UpdateAsync(orgId, currencyId, dto), DeactivateAsync(orgId, currencyId). BR-C1-01: FluentValidation — code exactly 3 uppercase ASCII chars. BR-C1-02: reject duplicate code per org (409 Conflict). BR-C1-03: deactivation blocked if currency is any of the 3 base currencies (query OrganizationCurrencySettings). BR-C1-04: deactivation blocked if currency referenced by confirmed SO/PO/SQ/Invoice — soft-delete only (set is_active = 0). BR-C1-05: decimal_places drives rounding precision — expose via DTO for UI formatters.

- [ ] **A35-P1-04** — Currencies CRUD API Endpoints (Endpoints #1–#3) `2h`
  - Schema: `lookups` | FSD: §10.1 endpoints 1–3, §10.2 | Depends: A35-P1-03 | Tests: T-C1-01
  - GET /api/currencies — list active currencies for org. Auth claim: currencies.read. Query params: ?active_only=true (default). Response: List<CurrencyDto> with id, code, name, symbol, decimal_places, rounding, symbol_position, is_active, display_order. POST /api/currencies — add new currency. Auth claim: currencies.manage. Body: { code, name, symbol, decimal_places, rounding, symbol_position }. PUT /api/currencies/{id} — update currency (name, symbol, is_active, display_order). Auth claim: currencies.manage. Swagger annotations for all 3.

- [ ] **A35-P1-05** — UI — Currencies List Panel (Settings → Currencies → Manage Currencies) `3h`
  - Schema: — (UI) | FSD: §11.2 | Depends: A35-P1-04 | Tests: —
  - Data grid: columns Order, Code, Name, Symbol, Dec., Active (toggle), Actions (Edit, Lock icon). Lock icon (🔒) shown for currencies used as any base currency — cannot deactivate (tooltip: "Used as base currency"). [+ Add Currency] button opens Add Currency dialog. Edit dialog: fields for name, symbol, decimal_places, rounding, symbol_position, display_order, is_active toggle. Sorting by display_order. Active toggle calls PUT with is_active flag. Error toast on deactivation rejection (base currency / in-use).

### Track 1B — Exchange Rates (C2)

- [ ] **A35-P1-06** — Create lookups.CurrencyRates Table (Migration M2 — DDL) `2h`
  - Schema: `lookups` | FSD: §3.2, §9.2 M2 | Depends: A35-P1-01 | Tests: T-C2-01–T-C2-11
  - CREATE TABLE lookups.CurrencyRates: currency_rate_id INT IDENTITY PK, org_id INT FK → tenants.Organizations, currency_id INT FK → lookups.Currencies, rate DECIMAL(18,10) NOT NULL, inverse_rate DECIMAL(18,10) NOT NULL, effective_from DATE NOT NULL, effective_to DATE NOT NULL, rate_source NVARCHAR(30) NOT NULL DEFAULT 'MANUAL', notes NVARCHAR(200) NULL, created_at DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(), created_by_user_id INT NULL FK → auth.Users. UNIQUE: UQ_CurrencyRates_Range (org_id, currency_id, effective_from). INDEXES: IX_CurrencyRates_Lookup (org_id, currency_id, effective_from, effective_to), IX_CurrencyRates_Active filtered WHERE effective_to = '9999-12-31'. CHECK constraints: CK_CurrencyRates_DateRange (effective_to >= effective_from), CK_CurrencyRates_RatePositive (rate > 0), CK_CurrencyRates_InversePositive (inverse_rate > 0), CK_CurrencyRates_Source (rate_source IN ('MANUAL', 'SYSTEM')).

- [ ] **A35-P1-07** — CurrencyRate EF Core Entity & Configuration `2h`
  - Schema: `lookups` | FSD: §3.2 | Depends: A35-P1-06 | Tests: —
  - Add CurrencyRate entity: CurrencyRateId, OrgId, CurrencyId, Rate, InverseRate, EffectiveFrom (DateOnly), EffectiveTo (DateOnly), RateSource, Notes, CreatedAt, CreatedByUserId. IEntityTypeConfiguration<CurrencyRate>: ToTable("CurrencyRates", "lookups"), HasQueryFilter(r => r.OrgId == _tenantId), navigation to Currency entity. DbContext: DbSet<CurrencyRate> CurrencyRates.

- [ ] **A35-P1-08** — CurrencyRateService — Rate Insertion with SERIALIZABLE TX `4h`
  - Schema: `lookups` | FSD: §3.3, §3.6, §3.7, BR-C2-01–BR-C2-07 | Depends: A35-P1-07 | Tests: T-C2-01–T-C2-11
  - InsertRateAsync(orgId, currencyId, rate, effectiveFrom, source, userId?, notes?): (1) Open SERIALIZABLE transaction. (2) Find current active rate WHERE effective_to = '9999-12-31' AND currency_id = @currencyId. (3) Close it: SET effective_to = effectiveFrom - 1 day. (4) Insert new rate with effective_to = '9999-12-31'. (5) Commit TX. BR-C2-02: overlap prevention — query for any existing rate WHERE effective_from <= newTo AND effective_to >= newFrom; reject if rows found beyond the current active. BR-C2-03: gap prevention — previous row's effective_to = new effective_from - 1. BR-C2-04: reject modification of base currency rate (rate always 1.0). BR-C2-06: rate > 0, inverse_rate > 0 (CHECK constraints). BR-C2-07: inverse_rate = 1 / rate, computed before insert. Also: UpdateHistoricalRateAsync for admin edits to past rates (validate no overlaps). Concurrent insert protection via SERIALIZABLE isolation (T-C2-10).

- [ ] **A35-P1-09** — CurrencyService — GetRateAsync (Rate Lookup) `1.5h`
  - Schema: `lookups` | FSD: §3.4, §7.2, BR-C2-05 | Depends: A35-P1-07 | Tests: T-C2-03–T-C2-05
  - GetRateAsync(orgId, currencyId, date): query WHERE orgId AND currencyId AND @date BETWEEN effective_from AND effective_to → SingleOrDefaultAsync. If null → throw CurrencyRateNotFoundException(orgId, currencyId, date). Must return exactly 0 or 1 rows (BR-C2-05). GetActiveRatesAsync(orgId): query WHERE effective_to = '9999-12-31' → list of all current active rates for display.

- [ ] **A35-P1-10** — Currency Rates API Endpoints (Endpoints #4–#9) `3h`
  - Schema: `lookups` | FSD: §10.1 endpoints 4–9 | Depends: A35-P1-08, A35-P1-09 | Tests: —
  - GET /api/currency-rates — list rates with date filters (?currency_id, ?from, ?to, ?active_only). Auth: currency-rates.read. GET /api/currency-rates/active — all current rates (effective_to = 9999-12-31). Auth: currency-rates.read. GET /api/currency-rates/{currencyId}?date={date} — specific rate on specific date. Auth: currency-rates.read. POST /api/currency-rates — insert new rate (closes previous active). Auth: currency-rates.manage. Body: { currency_id, rate, effective_from, notes }. PUT /api/currency-rates/{id} — edit historical rate (admin). Auth: currency-rates.manage. GET /api/currency-rates/history/{currencyId} — full rate history for a currency. Auth: currency-rates.read. Swagger annotations for all 6.

- [ ] **A35-P1-11** — UI — Exchange Rates Panel (Settings → Exchange Rates) `4h`
  - Schema: — (UI) | FSD: §11.3 | Depends: A35-P1-10 | Tests: —
  - Data grid: columns Code, Name, Rate (PKR), From, To, Source, Edit. Currency filter dropdown [All ▼]. Period filter [Oct 2026 ▼]. Active rates marked with "● Current" badge in To column. Rate values formatted with 4 decimal places. Source column shows "Manual" for all (MANUAL source). Edit button (✎) opens edit dialog. [+ Add Rate] button opens Add Rate dialog. Sort by currency code, then effective_from DESC.

- [ ] **A35-P1-12** — UI — Add/Edit Rate Dialog `3h`
  - Schema: — (UI) | FSD: §11.4 | Depends: A35-P1-11 | Tests: —
  - Add Rate dialog fields: Currency dropdown (active currencies), Rate input (numeric, 10 decimal precision), Inverse display (computed: 1/rate, read-only), Effective From (date picker), Effective To (radio: "Current (open-ended)" default | "Fixed: [date]"), Source (static text: "Manual"), Notes (text input, optional). Info banner: "The previous active rate ({rate} from {date}) will be closed to {date}." — dynamically fetched. Edit mode: same fields, Currency read-only. Validation: rate > 0, effective_from required. On save, call POST or PUT endpoint.

### Track 1C — Seed Data & Tests

- [ ] **A35-P1-13** — Migration M8 — Seed Currencies & Initial Rates for Existing Tenants `2h`
  - Schema: `lookups, tenant` | FSD: §9.2 M8 | Depends: A35-P1-01, A35-P1-06 | Tests: —
  - CROSS APPLY INSERT: 18 currencies per existing org (PKR, USD, EUR, GBP, SAR, AED, CNY, JPY, BHD, OMR, CAD, AUD, INR, TRY, MYR, KWD, QAR, CHF) with correct decimal_places, rounding, symbol_position, display_order. Idempotent — WHERE NOT EXISTS. Seed base currency rate: PKR rate = 1.0, effective_from = 2000-01-01, effective_to = 9999-12-31, rate_source = 'SYSTEM'. Initialize OrganizationCurrencySettings: one row per org, all three base currencies = PKR (or org's registration currency). Backfill existing SaleOrders: currency_id = PKR, exchange_rate = 1.0, *_base = *_currency amounts. Backfill existing PurchaseOrders: same pattern.

- [ ] **A35-P1-14** — Update Tenant Provisioning — Auto-Seed Currencies on New Org `1.5h`
  - Schema: `lookups, tenant` | FSD: §2.3, §4.3 | Depends: A35-P1-02 | Tests: —
  - Update tenant creation service: after creating Organization, seed 18 currencies, create base currency rate (1.0 for registration currency), create OrganizationCurrencySettings row (all 3 base = registration currency). Transactional — all or nothing within tenant creation TX.

- [ ] **A35-P1-15** — Phase 1 Unit Tests `4h`
  - Schema: `lookups` | FSD: §13.1, §13.2 | Depends: A35-P1-01–14 | Tests: T-C1-01–T-C1-10, T-C2-01–T-C2-11
  - T-C1-01: Create valid currency (MXN). T-C1-02: Reject code length ≠ 3. T-C1-03: Reject lowercase code. T-C1-04: Reject duplicate code for same org (409). T-C1-05: Same code allowed across orgs. T-C1-06: Deactivate unused currency. T-C1-07: Reject deactivation of base currency. T-C1-08: JPY formatting (0 decimals). T-C1-09: BHD formatting (3 decimals). T-C1-10: CHF symbol_position 'after'. T-C2-01: Insert first rate. T-C2-02: Insert new rate closes previous. T-C2-03: Rate lookup exact date. T-C2-04: Rate lookup within range. T-C2-05: Rate lookup — no rate → CurrencyRateNotFoundException. T-C2-06: Reject overlapping rate. T-C2-07: Reject base currency rate change. T-C2-08: Reject negative rate. T-C2-09: Inverse rate computed. T-C2-10: Concurrent insert — SERIALIZABLE. T-C2-11: Gap prevention.

---

## Phase 2 — Configuration: Org Settings & Business Partner Defaults (C3, C4)

**Priority:** P1 — Sequenced after Phase 1 | **Estimate:** 40 Hours (5 Days) | **FSD:** §4, §5, §9 M3–M4

> **⚡ Sequenced:** Depends on Phase 1 (Currencies table must exist for FK references).

### Track 2A — Organization Currency Settings (C3)

- [ ] **A35-P2-01** — Create tenant.OrganizationCurrencySettings Table (Migration M3 — DDL) `1.5h`
  - Schema: `tenant` | FSD: §4.2, §9.2 M3 | Depends: A35-P1-01 | Tests: T-C3-01–T-C3-04
  - CREATE TABLE tenant.OrganizationCurrencySettings: org_currency_setting_id INT IDENTITY PK, org_id INT FK → tenants.Organizations, sale_base_currency_id INT NOT NULL FK → lookups.Currencies, purchase_base_currency_id INT NOT NULL FK → lookups.Currencies, service_base_currency_id INT NOT NULL FK → lookups.Currencies, exchange_gain_account_code NVARCHAR(20) NULL, exchange_loss_account_code NVARCHAR(20) NULL, unrealized_gain_account_code NVARCHAR(20) NULL, unrealized_loss_account_code NVARCHAR(20) NULL, created_at DATETIME2, updated_at DATETIME2. UNIQUE: UQ_OrgCurrencySettings_Org (org_id) — one row per org.

- [ ] **A35-P2-02** — OrganizationCurrencySettings EF Core Entity & Configuration `1.5h`
  - Schema: `tenant` | FSD: §4.2 | Depends: A35-P2-01 | Tests: —
  - Entity: OrgCurrencySettingId, OrgId, SaleBaseCurrencyId, PurchaseBaseCurrencyId, ServiceBaseCurrencyId, ExchangeGainAccountCode, ExchangeLossAccountCode, UnrealizedGainAccountCode, UnrealizedLossAccountCode, CreatedAt, UpdatedAt. Navigations: SaleBaseCurrency, PurchaseBaseCurrency, ServiceBaseCurrency (Currency entities). IEntityTypeConfiguration: 1:1 with Organization (HasQueryFilter for org_id). DbContext: DbSet<OrganizationCurrencySettings>.

- [ ] **A35-P2-03** — OrgCurrencySettingsService — CRUD & Immutability Enforcement `3h`
  - Schema: `tenant` | FSD: §4.3, §4.4, BR-C3-01–BR-C3-03 | Depends: A35-P2-02 | Tests: T-C3-01–T-C3-04
  - GetAsync(orgId): return current settings with currency navigation loaded. UpdateAsync(orgId, dto): update base currencies and GL accounts. BR-C3-01: exactly one row per org (enforce via UNIQUE constraint + create-on-provision). BR-C3-02: all three base currencies must reference active currencies belonging to same org — FluentValidation. BR-C3-03: CRITICAL — base currency immutability. Before allowing change to sale_base_currency_id: query for ANY confirmed SaleOrder/SalesInvoice with current base. If exists → 400 "Cannot change — confirmed transactions exist in this base currency." Same logic for purchase_base (check confirmed POs) and service_base. GetBaseCurrencyIdAsync(orgId, TransactionDomain domain): returns the appropriate base currency ID per domain enum (Sale → sale_base, Purchase → purchase_base, Service → service_base).

- [ ] **A35-P2-04** — Org Currency Settings API Endpoints (Endpoints #10–#11) `2h`
  - Schema: `tenant` | FSD: §10.1 endpoints 10–11 | Depends: A35-P2-03 | Tests: —
  - GET /api/organization/currency-settings — return org's currency config with resolved currency DTOs. Auth: org-settings.read. Response: { sale_base: {id, code, name}, purchase_base: {...}, service_base: {...}, exchange_gain_account_code, exchange_loss_account_code, unrealized_gain_account_code, unrealized_loss_account_code }. PUT /api/organization/currency-settings — update config. Auth: org-settings.manage (new claim → org-currency-settings.manage). Body: { sale_base_currency_id, purchase_base_currency_id, service_base_currency_id, exchange_gain_account_code, exchange_loss_account_code, unrealized_gain_account_code, unrealized_loss_account_code }. 400 on immutability violation. Swagger annotations.

- [ ] **A35-P2-05** — UI — Currency Configuration Settings Page `4h`
  - Schema: — (UI) | FSD: §11.1 | Depends: A35-P2-04, A35-P1-04 | Tests: —
  - Route: Settings → Currency Configuration. Panel "Base Currencies": 3 dropdowns (Sale Base, Purchase Base, Service Base) populated from active currencies. Dropdown disabled + tooltip when confirmed transactions exist (immutability rule). Warning banner when any base differs from others: "⚠ {Domain} base set to {code} — all {domain} amounts will be converted to {code} for accounting." Panel "Exchange Difference Accounts": 4 text inputs for GL account codes (Realized Gain, Realized Loss, Unrealized Gain, Unrealized Loss). [Save Settings] button. Error toasts on validation failure. Success toast on save.

### Track 2B — Business Partner Currency Defaults (C4)

- [ ] **A35-P2-06** — Add Currency Default Columns to BusinessPartners (Migration M4 — DDL) `1h`
  - Schema: `suppliers` | FSD: §5.2, §9.2 M4 | Depends: A35-P1-01 | Tests: T-C4-01–T-C4-04
  - ALTER TABLE suppliers.BusinessPartners ADD default_sale_currency_id INT NULL FK → lookups.Currencies. ALTER TABLE suppliers.BusinessPartners ADD default_purchase_currency_id INT NULL FK → lookups.Currencies. Both nullable — NULL means "use org's base currency for that domain."

- [ ] **A35-P2-07** — BusinessPartner Entity & DTO Updates for Currency Defaults `1.5h`
  - Schema: `suppliers` | FSD: §5.2 | Depends: A35-P2-06 | Tests: —
  - Add DefaultSaleCurrencyId (int?) and DefaultPurchaseCurrencyId (int?) to BusinessPartner entity. Navigation properties: DefaultSaleCurrency, DefaultPurchaseCurrency (Currency entities). Update IEntityTypeConfiguration: HasOne for each FK. Update BusinessPartnerResponseDto: include default_sale_currency (CurrencyDto), default_purchase_currency (CurrencyDto). Update BusinessPartnerCreateDto/UpdateDto: accept default_sale_currency_id, default_purchase_currency_id.

- [ ] **A35-P2-08** — BusinessPartnerService — Currency Default Validation `1.5h`
  - Schema: `suppliers` | FSD: §5.2, BR-C4-01–BR-C4-03 | Depends: A35-P2-07 | Tests: T-C4-01–T-C4-04
  - BR-C4-01: default currencies must reference active currencies in the same org — FluentValidation + query. BR-C4-02: resolution logic for sale documents — if customer has default_sale_currency_id → use it, else → org's sale_base_currency_id. BR-C4-03: resolution logic for purchase documents — if supplier has default_purchase_currency_id → use it, else → org's purchase_base_currency_id. Expose resolution methods: GetDefaultSaleCurrencyAsync(orgId, businessPartnerId), GetDefaultPurchaseCurrencyAsync(orgId, businessPartnerId).

- [ ] **A35-P2-09** — UI — Business Partner Currency Tab `2h`
  - Schema: — (UI) | FSD: §11.6 | Depends: A35-P2-08, A35-P1-04 | Tests: —
  - New "Currency" tab on Business Partner detail form (between existing tabs). Default Sale Currency dropdown: active currencies + blank ("Use org default: {code}"). Default Purchase Currency dropdown: same. Info note below Sale dropdown: "New Sale Inquiries, Quotations, and Orders for this customer will default to {selected}. Can be overridden on each document." Info note below Purchase: "(Used when this partner is also a supplier)". Save persists to BusinessPartner entity. Tab shown for all BP types (customer, supplier, both).

### Track 2C — Phase 2 Tests

- [ ] **A35-P2-10** — Phase 2 Unit Tests `2h`
  - Schema: `tenant, suppliers` | FSD: §13.3, §13.4 | Depends: A35-P2-01–09 | Tests: T-C3-01–T-C3-04, T-C4-01–T-C4-04
  - T-C3-01: Set all 3 bases to same currency (single-currency behavior). T-C3-02: Set purchase base to USD (POs convert to USD). T-C3-03: Change base with existing confirmed SO → 400. T-C3-04: Change base with no transactions → allowed. T-C4-01: Customer with AED default → inquiry defaults to AED. T-C4-02: Customer with no default → inquiry uses org's sale base (PKR). T-C4-03: Override currency on SO (EUR for AED-default customer). T-C4-04: Supplier with USD default → PO defaults to USD.

---

## Phase 3 — Transaction Currency: Dual-Amount Storage & Rate Locking (C5, C6)

**Priority:** P1 — Sequenced after Phase 2 | **Estimate:** 80 Hours (10 Days) | **FSD:** §6, §7, §9 M5–M7

> **⚠️ Sequenced:** Depends on Phase 1 (Currencies/Rates tables) and Phase 2 (Org Settings for base currency resolution). This is the largest phase — touches SO, PO, SQ, SI, and Payment entities.

### Track 3A — Schema Migrations (C5)

- [ ] **A35-P3-01** — Add Currency Columns to Sale Documents (Migration M5 — DDL) `4h`
  - Schema: `demand` | FSD: §6.3, §9.2 M5 | Depends: A35-P1-01 | Tests: —
  - **SaleInquiries:** ADD currency_id INT NULL FK → lookups.Currencies. **SaleQuotations:** ADD currency_id INT NULL FK, ADD exchange_rate DECIMAL(18,10) NOT NULL DEFAULT 1.0, ADD base_currency_id INT NULL FK. Data migration: UPDATE sq SET currency_id = c.currency_id FROM SaleQuotations sq JOIN Currencies c ON c.code = sq.currency_code AND c.org_id = sq.org_id (runs after M8 seeds currencies). Comment: DROP COLUMN currency_code deferred to manual post-verification step. **SaleQuotationLines:** ADD unit_price_base DECIMAL(18,4) NOT NULL DEFAULT 0, ADD discount_amount_base DECIMAL(18,4) NOT NULL DEFAULT 0, ADD tax_amount_base DECIMAL(18,4) NOT NULL DEFAULT 0, ADD line_total_base DECIMAL(18,4) NOT NULL DEFAULT 0. **SaleOrders:** ADD currency_id INT NULL FK, ADD exchange_rate DECIMAL(18,10) NOT NULL DEFAULT 1.0, ADD base_currency_id INT NULL FK, ADD subtotal_base DECIMAL(18,4) NOT NULL DEFAULT 0, ADD tax_amount_base DECIMAL(18,4) NOT NULL DEFAULT 0, ADD grand_total_base DECIMAL(18,4) NOT NULL DEFAULT 0. **SaleOrderLines:** ADD unit_price_base DECIMAL(18,4) NOT NULL DEFAULT 0, ADD line_total_base DECIMAL(18,4) NOT NULL DEFAULT 0.

- [ ] **A35-P3-02** — Add Currency Columns to Purchase Documents (Migration M6 — DDL) `2h`
  - Schema: `procurement` | FSD: §6.4, §9.2 M6 | Depends: A35-P1-01 | Tests: —
  - **PurchaseOrders:** ADD currency_id INT NULL FK → lookups.Currencies, ADD exchange_rate DECIMAL(18,10) NOT NULL DEFAULT 1.0, ADD base_currency_id INT NULL FK, ADD subtotal_base DECIMAL(18,4) NOT NULL DEFAULT 0, ADD tax_amount_base DECIMAL(18,4) NOT NULL DEFAULT 0, ADD grand_total_base DECIMAL(18,4) NOT NULL DEFAULT 0. **PurchaseOrderLines:** ADD unit_price_base DECIMAL(18,4) NOT NULL DEFAULT 0, ADD line_total_base DECIMAL(18,4) NOT NULL DEFAULT 0.

- [ ] **A35-P3-03** — Add Currency Columns to Finance Documents (Migration M7 — DDL) `2h`
  - Schema: `finance` | FSD: §6.5, §9.2 M7 | Depends: A35-P1-01 | Tests: —
  - **SalesInvoices:** ADD currency_id INT NULL FK, ADD exchange_rate DECIMAL(18,10) NOT NULL DEFAULT 1.0, ADD base_currency_id INT NULL FK, ADD amount_currency DECIMAL(18,4) NOT NULL DEFAULT 0, ADD amount_base DECIMAL(18,4) NOT NULL DEFAULT 0, ADD exchange_rate_locked_at DATETIME2 NULL. **Payments:** ADD currency_id INT NULL FK, ADD exchange_rate DECIMAL(18,10) NOT NULL DEFAULT 1.0, ADD base_currency_id INT NULL FK, ADD amount_currency DECIMAL(18,4) NOT NULL DEFAULT 0, ADD amount_base DECIMAL(18,4) NOT NULL DEFAULT 0, ADD exchange_difference DECIMAL(18,4) NOT NULL DEFAULT 0.

### Track 3B — Entity Updates for Dual-Amount (C5)

- [ ] **A35-P3-04** — SaleInquiry, SaleQuotation, SaleOrder Entity & DTO Updates `3h`
  - Schema: `demand` | FSD: §6.3, BR-C5-01 | Depends: A35-P3-01 | Tests: —
  - **SaleInquiry entity:** add CurrencyId (int), navigation Currency. Update SaleInquiryResponseDto, CreateDto. **SaleQuotation entity:** add CurrencyId (int), ExchangeRate (decimal), BaseCurrencyId (int), navigations Currency, BaseCurrency. Remove CurrencyCode string property. Update all DTOs. **SaleQuotationLine entity:** add UnitPriceBase, DiscountAmountBase, TaxAmountBase, LineTotalBase (all decimal). Update line DTOs. **SaleOrder entity:** add CurrencyId, ExchangeRate, BaseCurrencyId, SubtotalBase, TaxAmountBase, GrandTotalBase. **SaleOrderLine entity:** add UnitPriceBase, LineTotalBase. Update all DTOs to include *_base amounts in responses. BR-C5-01: validators require currency_id, exchange_rate, base_currency_id on all headers.

- [ ] **A35-P3-05** — PurchaseOrder Entity & DTO Updates `1.5h`
  - Schema: `procurement` | FSD: §6.4, BR-C5-01 | Depends: A35-P3-02 | Tests: —
  - **PurchaseOrder entity:** add CurrencyId, ExchangeRate, BaseCurrencyId, SubtotalBase, TaxAmountBase, GrandTotalBase. Navigations: Currency, BaseCurrency. **PurchaseOrderLine entity:** add UnitPriceBase, LineTotalBase. Update all response/create/update DTOs. Validators: require currency_id, exchange_rate, base_currency_id.

- [ ] **A35-P3-06** — SalesInvoice & Payment Entity & DTO Updates `1.5h`
  - Schema: `finance` | FSD: §6.5, BR-C5-01 | Depends: A35-P3-03 | Tests: —
  - **SalesInvoice entity:** add CurrencyId, ExchangeRate, BaseCurrencyId, AmountCurrency, AmountBase, ExchangeRateLockedAt. Navigations. **Payment entity:** add CurrencyId, ExchangeRate, BaseCurrencyId, AmountCurrency, AmountBase, ExchangeDifference. Update DTOs. Validators.

### Track 3C — Currency Service Full Implementation (C6)

- [ ] **A35-P3-07** — ICurrencyService — Full Interface Implementation `4h`
  - Schema: `lookups, tenant` | FSD: §7.1, §7.2, §7.3, §7.5, BR-C6-01, BR-C6-02 | Depends: A35-P1-09, A35-P2-03 | Tests: T-C6-01–T-C6-05
  - Implement remaining ICurrencyService methods: **ToBaseCurrencyAsync(orgId, amount, sourceCurrencyId, date, domain):** resolve base currency via GetBaseCurrencyIdAsync → get rate → multiply. **FromBaseCurrencyAsync(orgId, amount, targetCurrencyId, date, domain):** resolve base → get rate → divide. **ConvertAsync(orgId, amount, fromCurrencyId, toCurrencyId, date):** cross-currency via base: A → Base → B (BR-C6-01). **LockRateAsync(orgId, currencyId, date):** return rate value only — caller persists on document (BR-C6-02). **GetBaseCurrencyIdAsync(orgId, domain):** resolve from OrganizationCurrencySettings per TransactionDomain enum. **Same-currency optimization (§7.5):** if currencyId == baseCurrencyId → return 1.0, skip DB lookup.

- [ ] **A35-P3-08** — Conversion API Endpoint (Endpoint #12) `1h`
  - Schema: `lookups` | FSD: §10.1 endpoint 12, §10.4 | Depends: A35-P3-07 | Tests: —
  - POST /api/currency/convert. Auth: currencies.read. Body: { amount, from_currency_id, to_currency_id, date, domain }. Response: { original_amount, from_currency: {id, code, symbol}, converted_amount, to_currency: {id, code, symbol}, rate_used, rate_date, effective_from, effective_to }. Error: 404 if rate not found for date.

### Track 3D — Rate Locking Integration (C5 + C6)

- [ ] **A35-P3-09** — SaleOrderService — Rate Locking at ConfirmAsync `3h`
  - Schema: `demand` | FSD: §6.3.5, §7.4, BR-C5-02, BR-C5-03, BR-C5-05, BR-C5-06 | Depends: A35-P3-04, A35-P3-07 | Tests: T-C5-04, T-C5-05
  - In SaleOrderService.ConfirmAsync: (1) Resolve base currency via GetBaseCurrencyIdAsync(orgId, Sale). (2) If SO.CurrencyId == baseCurrencyId → set ExchangeRate = 1.0, copy all amounts as-is (§7.5). (3) Else → call LockRateAsync(orgId, SO.CurrencyId, today) → set SO.ExchangeRate. (4) Recalculate all lines: line.UnitPriceBase = Round(line.UnitPrice × rate, baseCurrencyDecimalPlaces), line.LineTotalBase = Round(line.LineTotal × rate, baseCurrencyDecimalPlaces). (5) Recalculate header: SubtotalBase = SUM(line.LineTotalBase), TaxAmountBase = Round(TaxAmount × rate), GrandTotalBase = SubtotalBase + TaxAmountBase. BR-C5-06: after confirmation, ExchangeRate is immutable — no recalculation path. Throw CurrencyRateNotFoundException if no rate defined (T-C5-10).

- [ ] **A35-P3-10** — PurchaseOrderService — Rate Locking at ConfirmAsync `2h`
  - Schema: `procurement` | FSD: §6.4, BR-C5-02, BR-C5-03 | Depends: A35-P3-05, A35-P3-07 | Tests: —
  - Same pattern as SO but uses TransactionDomain.Purchase → purchase_base_currency_id. In PurchaseOrderService.ConfirmAsync: lock rate, calculate all *_base amounts on lines and header. Same-currency optimization for purchase base.

- [ ] **A35-P3-11** — SaleQuotationService — Rate Locking at SendAsync `1.5h`
  - Schema: `demand` | FSD: §6.3.5, BR-C5-05 | Depends: A35-P3-04, A35-P3-07 | Tests: T-C5-03
  - In SaleQuotationService when status → SENT: lock rate on sent_at date. Calculate *_base columns on quotation lines: UnitPriceBase, DiscountAmountBase, TaxAmountBase, LineTotalBase.

- [ ] **A35-P3-12** — Currency Propagation: Customer → Inquiry → Quotation → SO `3h`
  - Schema: `demand, suppliers` | FSD: §5.2, §6.6, BR-C5-07 | Depends: A35-P2-08, A35-P3-04 | Tests: T-C4-01–T-C4-03, T-C5-06
  - **SaleInquiryService.CreateAsync:** resolve currency from customer's default_sale_currency_id → org sale_base fallback. Set inquiry.CurrencyId. **SaleQuotationService.CreateFromInquiryAsync:** inherit currency from inquiry; allow override. Set base_currency_id from org settings. **SaleOrderService.CreateFromQuotationAsync:** inherit currency and exchange_rate from quotation (or re-lock at confirmation date per §6.3.5). Each step allows user override before confirmation. PO currency propagation: supplier default_purchase_currency_id → org purchase_base fallback.

### Track 3E — UI Integration (C5)

- [ ] **A35-P3-13** — UI — Sale Order Form with Dual-Amount Display `4h`
  - Schema: — (UI) | FSD: §11.5 | Depends: A35-P3-09, A35-P1-04 | Tests: —
  - SO header: Currency dropdown (active currencies, defaulted from customer), Exchange Rate display (read-only after confirmation, showing "locked Oct 7"), Base Currency label. Line item grid: add Total ({Currency}) and Total ({Base}) columns side by side. Footer: Subtotal, Tax, Grand Total — each in both currencies. [Toggle: Show in {Currency} / Show in {Base}] button. Rate display format: 4 decimal places. Currency symbol formatting using decimal_places and symbol_position from Currencies table. "No rate found" error dialog when confirming without rate.

- [ ] **A35-P3-14** — UI — Purchase Order Form with Dual-Amount Display `3h`
  - Schema: — (UI) | FSD: §11.5 (adapted) | Depends: A35-P3-10, A35-P1-04 | Tests: —
  - Same pattern as SO form. PO header: Currency dropdown (defaulted from supplier), Exchange Rate, Base Currency (purchase base). Line items with dual columns. Toggle view. Uses purchase_base_currency_id from org settings.

- [ ] **A35-P3-15** — Modified Endpoints — Add Currency Fields to Existing SO/PO APIs `2h`
  - Schema: `demand, procurement` | FSD: §10.3 | Depends: A35-P3-04, A35-P3-05 | Tests: —
  - POST /api/sale-inquiries: accept currency_id in body; default from customer. POST /api/sale-quotations: accept currency_id instead of currency_code; add exchange_rate to response. POST /api/sale-orders: accept currency_id; response includes *_base amounts. GET /api/sale-orders/{id}: response includes currency info, exchange_rate, *_base on all lines. POST /api/purchase-orders: accept currency_id; response includes *_base. GET /api/purchase-orders/{id}: response includes currency info, exchange_rate, *_base on all lines. Backward compatibility: existing clients that don't send currency_id get org base currency default.

### Track 3F — Phase 3 Tests

- [ ] **A35-P3-16** — Phase 3 Integration Tests `4h`
  - Schema: `demand, procurement, finance, lookups` | FSD: §13.5, §13.6 | Depends: A35-P3-01–15 | Tests: T-C5-01–T-C5-10, T-C6-01–T-C6-05
  - T-C5-01: SO confirmed in AED (base=PKR) — line_total_base = PKR 457,800. T-C5-02: SO in PKR (same as base) — exchange_rate=1.0, identical amounts. T-C5-03: Quotation rate locked at SENT. T-C5-04: SO rate locked at CONFIRMED. T-C5-05: Rate immutable after lock (rate changes on Oct 8 don't affect Oct 7 SO). T-C5-06: Currency propagation SQ → SO. T-C5-07: JPY rounding (0dp). T-C5-08: BHD rounding (3dp). T-C5-09: Header totals match line sums. T-C5-10: No rate exists at confirmation → 400. T-C6-01: Direct conversion 1,000 AED → PKR. T-C6-02: Cross-currency AED → EUR via PKR. T-C6-03: Same-currency conversion (no lookup). T-C6-04: Conversion with historical rate. T-C6-05: GetBaseCurrencyIdAsync per domain.

---

## Phase 4 — Exchange Accounting: Realized & Unrealized Differences (C7)

**Priority:** P2 — Final Phase | **Estimate:** 32 Hours (4 Days) | **FSD:** §8

> **⚠️ Sequenced:** Depends on Phase 3 (dual-amount storage and CurrencyService must be in place for exchange difference calculations).

### Track 4A — Exchange Difference Service

- [ ] **A35-P4-01** — ExchangeDifferenceService — Realized Gain/Loss Calculation `4h`
  - Schema: `finance` | FSD: §8.1, BR-C7-01–BR-C7-03, BR-C7-05 | Depends: A35-P3-06, A35-P3-07 | Tests: T-C8-01–T-C8-05
  - IExchangeDifferenceService with CalculateRealized(invoiceAmountCurrency, invoiceExchangeRate, paymentExchangeRate, baseCurrencyDecimalPlaces) → ExchangeDifferenceResult. BR-C7-01: difference = (paymentAmountCurrency × paymentRate) − (invoiceAmountCurrency × invoiceRate). BR-C7-02: positive = gain, negative = loss. BR-C7-03: partial payments — calculate difference proportionally on partial amount only: invoiceAmountCurrency = partial payment amount in transaction currency. BR-C7-05: same-currency (exchange_rate = 1.0) → skip, return 0 difference. ExchangeDifferenceResult: { InvoiceAmountBase, PaymentAmountBase, Difference, Type (Gain/Loss/None), GainLossAccountCode (from org settings) }.

- [ ] **A35-P4-02** — ExchangeRevaluationJob (Hangfire) — Unrealized Gain/Loss `4h`
  - Schema: `finance, lookups` | FSD: §8.2, BR-C7-04 | Depends: A35-P3-07 | Tests: T-C8-06–T-C8-08
  - Hangfire recurring job: ExchangeRevaluationJob.ExecuteAsync(orgId, revaluationDate). (1) Get all current active rates via GetActiveRatesAsync(orgId). (2) Query open receivables: SalesInvoices WHERE status = POSTED AND AmountDue > 0 AND CurrencyId != BaseCurrencyId (foreign-currency only). (3) For each: revaluedBase = AmountDue × currentRate, originalBase = AmountDue × invoiceExchangeRate. unrealizedDifference = revaluedBase − originalBase. (4) If difference ≠ 0 → create revaluation journal entry (Dr/Cr Unrealized Gain/Loss accounts from org settings). (5) Same logic for open payables (unpaid purchase invoices). BR-C7-04: revalue at current rate vs. booked rate. BR-C7-05: skip same-currency documents. Schedule: configurable per tenant (default: monthly last day). Register in Hangfire DI.

- [ ] **A35-P4-03** — Integration with Payment Posting — Realized Exchange Difference `2h`
  - Schema: `finance` | FSD: §8.1, §8.3 | Depends: A35-P4-01 | Tests: T-C8-01–T-C8-05
  - In PaymentService.PostAsync: (1) Get invoice's locked exchange_rate. (2) Get current rate on payment date via LockRateAsync. (3) Call ExchangeDifferenceService.CalculateRealized. (4) Set payment.ExchangeDifference = result.Difference. (5) Create journal entry: full payment → Dr Bank (paymentBase), Cr AR (invoiceBase), Cr/Dr Exchange Gain/Loss (difference). Partial payment → calculate on partial amount only (§8.3): proportional gain/loss on AED 4,000 of AED 10,000 invoice. Remaining balance stays at invoice rate until next payment or revaluation. Set payment.AmountCurrency, payment.AmountBase from conversion.

### Track 4B — Phase 4 Tests

- [ ] **A35-P4-04** — Phase 4 Unit Tests — Exchange Difference Scenarios `2h`
  - Schema: `finance` | FSD: §13.7 | Depends: A35-P4-01–03 | Tests: T-C8-01–T-C8-08
  - T-C8-01: Realized gain — Invoice AED 10,550 at 76.30, payment at 76.45 → gain PKR 1,582.50. T-C8-02: Realized loss — Invoice EUR 5,000 at 316.48, payment at 315.90 → loss PKR 2,900. T-C8-03: No difference — same rate. T-C8-04: Same-currency invoice → no exchange calc. T-C8-05: Partial payment gain — AED 4,000 of 10,000 at rate diff → PKR 800 gain. T-C8-06: Unrealized gain — open AR EUR 5,000 at 316.48, current 317.00 → PKR 2,600. T-C8-07: Unrealized loss — open AP USD 10,000 at 278.05, current 277.50 → PKR 5,500. T-C8-08: Revaluation skips same-currency.

- [ ] **A35-P4-05** — End-to-End Test — Full Multi-Currency Cycle `4h`
  - Schema: `demand, finance, lookups, tenant` | FSD: Appendix A | Depends: A35-P4-01–04 | Tests: —
  - **Scenario A (Appendix A):** Org with sale_base=PKR, purchase_base=USD. (1) Create customer Al Rashid Trading (default AED). (2) Create Sale Inquiry → currency auto-fills AED. (3) Create Quotation from inquiry → AED inherited. (4) Send quotation → rate locked. (5) Create SO from quotation → AED inherited. (6) Confirm SO → rate locked at 76.30, *_base calculated in PKR. (7) Post Sales Invoice → rate locked at posting date. (8) Receive payment 7 days later at rate 76.45 → realized gain calculated, journal entry posted. Verify all intermediate amounts, rate locking, propagation. **Scenario B:** Single-currency org (all PKR) — domestic SO in PKR → exchange_rate=1.0, no conversion, no exchange difference. **Scenario C:** PO in USD with purchase_base=USD → exchange_rate=1.0, no conversion needed (same as base).

---

## Cross-Cutting & Permissions

### Permission Claims Setup

- [ ] **A35-X-01** — Seed Permission Claims & Role Assignments `2h`
  - Schema: `auth` | FSD: §10.2 | Depends: — | Tests: —
  - Seed 5 new permission claims: `currencies.read` (assigned to all roles), `currencies.manage` (ADMIN), `currency-rates.read` (all roles), `currency-rates.manage` (ADMIN, FINANCE_MANAGER), `org-currency-settings.manage` (ADMIN). Add to role-permission seed data. Update permission documentation. Verify tenant provisioning includes new claims.

### Services Registry

- [ ] **A35-X-02** — DI Registration for All New Services `1h`
  - Schema: — (infrastructure) | FSD: §14.6 | Depends: All service implementations | Tests: —
  - Register in Program.cs / ServiceCollectionExtensions: ICurrencyService → CurrencyService (Scoped). IExchangeDifferenceService → ExchangeDifferenceService (Scoped). ExchangeRevaluationJob → Hangfire recurring job registration. Register CurrencyRateService, OrgCurrencySettingsService. Configure Hangfire for ExchangeRevaluationJob with tenant-aware scheduling.

---

## Summary by Migration

| Migration | Phase | Task(s) | Schema |
|---|---|---|---|
| M1 — CreateCurrencies | 1 | A35-P1-01 | lookups |
| M2 — CreateCurrencyRates | 1 | A35-P1-06 | lookups |
| M3 — CreateOrganizationCurrencySettings | 2 | A35-P2-01 | tenant |
| M4 — AddBusinessPartnerCurrencyDefaults | 2 | A35-P2-06 | suppliers |
| M5 — AddCurrencyToSaleDocuments | 3 | A35-P3-01 | demand |
| M6 — AddCurrencyToPurchaseDocuments | 3 | A35-P3-02 | procurement |
| M7 — AddCurrencyToFinanceDocuments | 3 | A35-P3-03 | finance |
| M8 — SeedCurrenciesAndInitialRates | 1 | A35-P1-13 | lookups, tenant |

## Summary by API Endpoint

| # | Endpoint | Phase | Task |
|---|---|---|---|
| 1 | GET /api/currencies | 1 | A35-P1-04 |
| 2 | POST /api/currencies | 1 | A35-P1-04 |
| 3 | PUT /api/currencies/{id} | 1 | A35-P1-04 |
| 4 | GET /api/currency-rates | 1 | A35-P1-10 |
| 5 | GET /api/currency-rates/active | 1 | A35-P1-10 |
| 6 | GET /api/currency-rates/{currencyId}?date= | 1 | A35-P1-10 |
| 7 | POST /api/currency-rates | 1 | A35-P1-10 |
| 8 | PUT /api/currency-rates/{id} | 1 | A35-P1-10 |
| 9 | GET /api/currency-rates/history/{currencyId} | 1 | A35-P1-10 |
| 10 | GET /api/organization/currency-settings | 2 | A35-P2-04 |
| 11 | PUT /api/organization/currency-settings | 2 | A35-P2-04 |
| 12 | POST /api/currency/convert | 3 | A35-P3-08 |

## Summary by Business Rule

| Rule ID | Change | Task |
|---|---|---|
| BR-C1-01–05 | C1 Currencies | A35-P1-03 |
| BR-C2-01–07 | C2 Exchange Rates | A35-P1-08, A35-P1-09 |
| BR-C3-01–03 | C3 Org Config | A35-P2-03 |
| BR-C4-01–03 | C4 BP Defaults | A35-P2-08 |
| BR-C5-01–07 | C5 Dual-Amount | A35-P3-04–06, A35-P3-09–12 |
| BR-C6-01–02 | C6 Currency Service | A35-P3-07 |
| BR-C7-01–05 | C7 Exchange Diff | A35-P4-01–02 |

## Dependency Graph

```
Phase 1 (8 days) ─────────────────────────────────────────────────────────►
  A35-P1-01 (M1: Currencies)
    └── A35-P1-02 (Entity)
         └── A35-P1-03 (CurrencyService CRUD)
              └── A35-P1-04 (API #1–3)
                   └── A35-P1-05 (UI: Currencies List)
    └── A35-P1-06 (M2: CurrencyRates)
         └── A35-P1-07 (Entity)
              └── A35-P1-08 (RateService SERIALIZABLE)
                   └── A35-P1-09 (GetRateAsync)
                        └── A35-P1-10 (API #4–9)
                             └── A35-P1-11 (UI: Exchange Rates Panel)
                                  └── A35-P1-12 (UI: Add/Edit Rate Dialog)
    └── A35-P1-13 (M8: Seed Data)
    └── A35-P1-14 (Tenant Provisioning)
    └── A35-P1-15 (Phase 1 Tests)

Phase 2 (5 days) ─── depends on Phase 1 ─────────────────────────────────►
  A35-P2-01 (M3: OrgCurrencySettings)
    └── A35-P2-02 (Entity)
         └── A35-P2-03 (OrgCurrencySettingsService)
              └── A35-P2-04 (API #10–11)
                   └── A35-P2-05 (UI: Currency Config Page)
  A35-P2-06 (M4: BP Defaults)
    └── A35-P2-07 (Entity)
         └── A35-P2-08 (BP Service Validation)
              └── A35-P2-09 (UI: BP Currency Tab)
  A35-P2-10 (Phase 2 Tests)

Phase 3 (10 days) ─── depends on Phase 2 ────────────────────────────────►
  A35-P3-01 (M5: Sale Docs) ── A35-P3-02 (M6: Purchase Docs) ── A35-P3-03 (M7: Finance Docs)
    └── A35-P3-04–06 (Entity Updates)
         └── A35-P3-07 (ICurrencyService Full)
              └── A35-P3-08 (API #12: Convert)
              └── A35-P3-09 (SO Rate Locking)
              └── A35-P3-10 (PO Rate Locking)
              └── A35-P3-11 (SQ Rate Locking)
         └── A35-P3-12 (Currency Propagation)
              └── A35-P3-13 (UI: SO Form)
              └── A35-P3-14 (UI: PO Form)
         └── A35-P3-15 (Modified Existing APIs)
  A35-P3-16 (Phase 3 Integration Tests)

Phase 4 (4 days) ─── depends on Phase 3 ─────────────────────────────────►
  A35-P4-01 (ExchangeDifferenceService)
    └── A35-P4-03 (Payment Integration)
  A35-P4-02 (ExchangeRevaluationJob)
  A35-P4-04 (Phase 4 Unit Tests)
  A35-P4-05 (End-to-End Test)

Cross-Cutting ─── anytime ────────────────────────────────────────────────►
  A35-X-01 (Permission Claims) — do during Phase 1
  A35-X-02 (DI Registration) — do after all services implemented
```
