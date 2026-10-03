# QuickBooks Online Integration: Implementation Plan

Scope: **Customers, Products, Vendors, Sales Invoices and Supplier Invoices (Bills)**, pushed one
way to QuickBooks Online (QBO). QBO is read only to match existing records and to load reference
data (accounts, tax codes, terms, currencies).

**Shape:** `SMS.Modules.Integration` is a **QuickBooks gateway**. Callers send complete payloads,
and the gateway owns OAuth, tokens, mapping, validation, retries and every call to Intuit. It has
no knowledge of SCM's tables.

| Decision | Choice |
|---|---|
| Deployment | A normal module inside `SMS.API` (like Finance). Not a separate app |
| Who owns the screens | **SCM's Angular app** (Option A). The gateway only hosts APIs |
| Screen endpoints (admin) | **JWT** + `INTEGRATION_*` permissions: a person is acting |
| Data endpoints (push customers/items/invoices…) | **Tenant id + API key**: a system is acting |
| How SCM's own modules call the gateway | **In-process**, through `IQuickBooksGateway` in `SMS.Shared`. The same payloads and pipeline as the HTTP data endpoints, with no key and no loopback HTTP call |
| QBO client | **QuickBooks Online .NET SDK** (`IppDotNetSdkForQuickBooksApiV3`) |
| Simulator | None. A scripted fake `IAccountingProvider` exists in the test project only. Real work runs against the Intuit sandbox |

This plan **supersedes** the module name, transport and simulator parts of
[`QUICKBOOKS-PHASE-1-TASKS.md`](./QUICKBOOKS-PHASE-1-TASKS.md). That file's Part A (codebase reality
check), Part B (the connect journey and connection states) and
[`QBO-CREATE-CUSTOMER-INTEGRATION-REPORT.md`](./QBO-CREATE-CUSTOMER-INTEGRATION-REPORT.md) (customer
field mapping, gaps G-1 to G-5, validation V-1 to V-8) still apply.

## Implementation status (2026-10-01)

QBI-01 to QBI-34 are built and tested, including two end-to-end classes through the real API host on
LocalDB with only Intuit faked (`tests/SMS.Integration.Tests/QuickBooks/QuickBooksJourneyTests`,
`QuickBooksApiKeyTests` — run each class on its own). QBI-00, QBI-35 and QBI-36 need a person: Intuit keys, a browser sign-in to
the sandbox, and the accountant's sign-off.

- **Decisions D-1…D-9 were implemented as recommended** (not yet confirmed by the user): home currency
  only; `(Code)` suffix for name clashes; NonInventory/Service items; tax mapping screen; gross lines +
  one discount line; OnlyWhenReferenced scope; ISSUED / CANCELLED→void / Approved triggers; payments
  not synced; sparse updates. **D-10** (bill tax) is a configurable `DefaultPurchaseTaxCodeId` setting.
- **Superseded 2026-10-01 by the SAP alignment** (`docs/finance/SAP-ALIGNMENT-PLAN.md`, S-10/S-11):
  **D-1** — with QuickBooks multicurrency ON, foreign-currency documents/parties are sent when the
  currency is active in QuickBooks and SMS has an exchange rate for the document date (sent as QBO
  `ExchangeRate`); with it OFF the home-currency-only rule stands. **D-4** — tax is mapped by SMS tax
  **code** first (`TaxCodeMapping.SourceTaxCode`), by bare percent only for lines without a code.
  **D-10** — supplier invoices now carry a purchase tax code, sent on every bill line;
  `DefaultPurchaseTaxCodeId` remains the fallback for bills with no code. After the upgrade every SMS
  tax code needs a QuickBooks mapping (the Mappings tab suggests one from the rate's existing mapping).
- **Rounding:** the gateway rounds once over the invoice, exactly like `SalesInvoiceTotals.Header`; the
  discount line absorbs QuickBooks' per-line rounding so its total equals SCM's grand total. Bills get a
  tolerance of 0.01 + 0.005 × lines. SCM sends `HeaderDiscountAmount = 0` (§4 corrected).
- **Beyond the plan:** one QuickBooks company can connect to one organization only (`realm_in_use`);
  the ledger key includes the source system; names are reserved before a create so an in-doubt create
  can never be adopted by a second record; items get a full reconciliation pass once a day (variants
  have no modified timestamp); preflight also checks QuickBooks' "Allow discount" preference; the
  base currency is compared automatically through `ICurrencyCodeLookup` (SMS.Shared → Lookups).
- **Migration:** `20260930190343_QBI_InitialIntegrationSchema` (14 tables, schema `integration`) —
  applied, re-applied (no-op), rolled back and re-applied on LocalDB. It reaches the shared database on
  the next API start.
- **Confirm in the sandbox (QBI-35):** exact-name lookups of inactive records (QuickBooks renames them
  "(deleted)"), `Sku` filtering on Item queries, backslash escaping in queries, and the provider's
  judgement calls (403 and 6190 treated as a revoked connection).

## Working agreement

1. Tasks are implemented **one at a time**, in order, by their `QBI-<n>` id.
2. When a task is done: stop, list what changed and how to verify it.
3. The user checks it and says to move on.

**Status:** `TODO` · `IN PROGRESS` · `DONE` · `BLOCKED` · `SKIPPED`

---

## 1. Codebase facts this plan is built on

Checked against the code on 2026-09-30.

| Entity / area | Where | Facts that shape the design |
|---|---|---|
| Customer / Vendor | `BusinessPartner`, [`Suppliers/Domain/SupplierEntities.cs`](../../src/SMS.Modules.Suppliers/Domain/SupplierEntities.cs) | A single row with `IsCustomer` / `IsVendor` flags. A row can be both. Names are still `SupplierName` / `SupplierCode`. `PreferredCurrency` / `PreferredPaymentTerms` are Guids pointing into Lookups |
| Product | `Product` + `ProductVariant`, [`Inventory/Domain/InventoryEntities.cs`](../../src/SMS.Modules.Inventory/Domain/InventoryEntities.cs) | **The variant is what gets bought and sold.** Every product has at least one variant. Invoices reference `VariantUuid` |
| Sales invoice | `SalesInvoice` + lines, [`Finance/Domain/SalesInvoiceEntities.cs`](../../src/SMS.Modules.Finance/Domain/SalesInvoiceEntities.cs) | Status DRAFT/ISSUED/PARTIALLY_PAID/PAID/OVERDUE/CANCELLED/**CREDIT_NOTE (a status)**. Line `TaxPercent` is a **bare decimal**. Line `DiscountPercent` + header `DiscountAmount`. `CurrencyCode` string. `InvoiceNumber` 18 chars |
| Supplier invoice | `Invoice` + `InvoiceLine`, [`Finance/Domain/FinanceEntities.cs`](../../src/SMS.Modules.Finance/Domain/FinanceEntities.cs) | `MatchStatus` …/**Approved**/Rejected. **Lines have no variant**, only `PoLineUuid?` / `GrnLineUuid?`. Carrier bills (`SourceType = CARRIER_INVOICE`) have no PO. Finance already references Demand, so it can resolve PO line → variant itself |
| Tenant resolution | [`SMS.Shared/Common/TenantContext.cs`](../../src/SMS.Shared/Common/TenantContext.cs) | The org comes from the **`organizationId` claim**. **With no authenticated principal, `IsSuperAdmin` is `true` and the tenant filter is bypassed.** Jobs use `HangfireTenantScope` |
| Module references | Suppliers → Finance, Warehouse, Demand. Finance → Demand, Warehouse, Lookups | Integration references **only `SMS.Shared` + `SMS.Modules.Tenancy`** (base currency for preflight). No SCM module references Integration |
| No exchange rates | Nothing in `src/` | Foreign-currency documents are blocked (D-1) |
| Frontend | Angular 19 + PrimeNG 19. `pages/pages.routes.ts` with `permissionGuard(P.X)`. Menu `layout/component/app.menu.ts` with `featureCode` + `permRequired`. Settings precedent `pages/sale-order-settings/` | New screens follow the same pattern |

---

## 2. Architecture

### 2.1 Overview

```
 Angular (SCM screens) ──JWT──────────► Admin controllers ─────────┐
                                                                    │
 Other project ──X-Tenant-Id + X-Api-Key──► Data controllers ───────┤
                                                                    ▼
 SCM Suppliers / Inventory / Finance ── IQuickBooksGateway ──► Gateway services
      (in-process, org from ITenantContext)                        │ validate → store payload → outbox
                                                                    ▼
                                                  Hangfire jobs → QuickBooks SDK → QBO
```

- **The data controllers are a thin HTTP layer over `IQuickBooksGateway`.** The HTTP and in-process paths run the exact same code.
- **The gateway stores the latest payload** it received for each record. Jobs push from that stored payload and never read SCM tables.
- **Nothing calls Intuit on a request thread.** A gateway call only validates and writes to the outbox, which is fast and local, so SCM can call it inside its own request.
  The exceptions are the connect flow, "Test connection", reference refresh and the matching scan, which are admin actions a user is waiting for anyway.

### 2.2 The contract in `SMS.Shared`

```
SMS.Shared/Integration/QuickBooks/
  IQuickBooksGateway.cs
      Task<GatewayResult> UpsertCustomerAsync(CustomerPayload p, CancellationToken ct)
      Task<GatewayResult> UpsertVendorAsync(VendorPayload p, CancellationToken ct)
      Task<GatewayResult> UpsertItemAsync(ItemPayload p, CancellationToken ct)
      Task<GatewayResult> UpsertSalesInvoiceAsync(SalesInvoicePayload p, CancellationToken ct)
      Task<GatewayResult> VoidSalesInvoiceAsync(string externalId, CancellationToken ct)
      Task<GatewayResult> UpsertBillAsync(BillPayload p, CancellationToken ct)
      Task<IReadOnlyList<SyncStatus>> GetStatusAsync(SyncKind kind, IReadOnlyCollection<string> externalIds, CancellationToken ct)

  IQuickBooksSource.cs               implemented by SCM modules, called BY the gateway
      SyncKind Kind { get; }
      Task PushAsync(IReadOnlyCollection<string> externalIds, CancellationToken ct)   // "send me these"
      Task PushAllAsync(DateTime? changedSince, CancellationToken ct)               // backfill / reconciliation

  Payloads/  CustomerPayload, VendorPayload, ItemPayload, SalesInvoicePayload, BillPayload, AddressPayload
  GatewayResult  { Outcome: Accepted | NotConnected | Disabled | Invalid | WaitingOnDependency,
                   State, Errors[{field, code, message}], MissingDependencies[{kind, externalId}] }
  SyncKind       Customer | Vendor | Item | SalesInvoice | Bill
  NullQuickBooksGateway              TryAdd default: returns Disabled. Keeps module tests independent
```

**Why `IQuickBooksSource` exists.** The gateway can't read SCM data, but it sometimes needs SCM to
send something:

- **Dependencies.** An invoice arrives for a customer that was never sent. The gateway holds the
  invoice as `WaitingOnDependency` and asks the source registered for `Customer` to push that id.
  External callers have no registered source, so they get the missing ids back (section 2.5).
- **"Sync all" / reconciliation.** The admin screen's backfill button and the hourly scan call
  `PushAllAsync` on every registered source.

Both are resolved through `IEnumerable<IQuickBooksSource>`, so Integration still references no SCM module.

### 2.3 Payload contract

Payloads are **neutral**, not QuickBooks-shaped. Callers send their own data, and the gateway applies
every QBO rule (name length and uniqueness, the `:` rule, vendor-name suffix, discount lines, tax
code mapping). Otherwise every calling project would have to re-implement QBO's quirks.

| Payload | Fields |
|---|---|
| `CustomerPayload` | `ExternalId`, `DisplayName`, `CompanyName`, `Code`, `Email`, `Phone`, `Fax`, `Website`, `TaxId`, `BillingAddress`, `CurrencyCode`, `PaymentTermExternalId?`, `Notes`, `IsActive` |
| `VendorPayload` | Customer fields + `AccountNumber` |
| `ItemPayload` | `ExternalId`, `Name`, `VariantName?`, `Sku`, `Description`, `Kind (Goods \| Service)`, `SalesPrice?`, `PurchaseCost?`, `IsSold`, `IsPurchased`, `IsActive` |
| `SalesInvoicePayload` | `ExternalId`, `DocNumber`, `CustomerExternalId`, `TxnDate`, `DueDate`, `CurrencyCode`, `Status (Issued \| Cancelled \| CreditNote)`, `Lines[{ ItemExternalId, Description, Quantity, UnitPrice, DiscountPercent, TaxPercent }]`, `HeaderDiscountAmount`, `ExpectedTaxAmount`, `ExpectedTotal`, `CustomerMemo`, `PrivateNote` |
| `BillPayload` | `ExternalId`, `DocNumber`, `VendorExternalId`, `TxnDate`, `DueDate`, `CurrencyCode`, `Lines[{ ItemExternalId?, Category (Goods \| Freight \| Other), Description, Quantity?, UnitPrice?, Amount, TaxPercent? }]`, `ExpectedTaxAmount`, `ExpectedTotal`, `PrivateNote` |

`Expected*` fields let the gateway check its own arithmetic against the caller's. A difference over
0.01 is **Invalid**, not silently pushed.

### 2.4 Identity and keys

- **Mapping key:** `(ConnectionId, SourceSystem, Kind, ExternalId)`.
  - `SourceSystem` is `"SCM"` for in-process calls, or the API client's name for HTTP calls. So a POS app's customer `123` never collides with SCM's.
  - Two sources pushing the *same* real customer into one QBO company will collide on `DisplayName`. That collision goes to matching (section 2.6), and nothing is created twice.
- **Upsert by `ExternalId`** makes every call idempotent. Sending the same payload twice matches the stored fingerprint, so no QBO call is made.

### 2.5 Authentication

| Endpoint group | Scheme | Org comes from | Permission |
|---|---|---|---|
| Admin (`/api/integrations/quickbooks/*`) | Existing **JWT** | `organizationId` claim | `INTEGRATION_VIEW` / `INTEGRATION_MANAGE` / `INTEGRATION_SYNC` |
| Callback (`/api/integrations/quickbooks/callback`) | **Anonymous** | The single-use `state` token only. Queries by that unique key, never a listing | — |
| Data (`/api/gateway/quickbooks/v1/*`) | **`ApiKey`** | The API client's org, set as the `organizationId` claim | The client's scopes |
| In-process `IQuickBooksGateway` | — | `ITenantContext` (request JWT, or `HangfireTenantScope` in jobs) | The caller's own module permission |

**API key rules:**

- Table `integration.ApiClients`: `Id, OrganizationId, Name, KeyPrefix (first 8 chars, for display), KeyHash (SHA-256), Scopes, IsActive, ExpiresAt, LastUsedAt, CreatedBy, CreatedAt, RevokedAt`.
- Headers `X-Tenant-Id` + `X-Api-Key`. The key identifies the client, and **the tenant header must equal the key's org** or the request gets 401.
- The key is shown **once** at creation and only its hash is stored. Up to two active keys per client allow rotation without downtime. Keys can be revoked.
- Scopes: `customers:write`, `vendors:write`, `items:write`, `invoices:write`, `bills:write`, `status:read`.
- `ApiKeyAuthenticationHandler` builds a principal with `organizationId`, `api_client_id` and a scope claim per scope, and **never** `is_super_admin`.
- **Data controllers carry `[Authorize(AuthenticationSchemes = "ApiKey")]` at class level and never `[AllowAnonymous]`.** Without an authenticated principal, `TenantContext.IsSuperAdmin` returns true and the tenant filter is bypassed (section 1). A test must prove an unauthenticated call gets 401 and not data.
- Rate limit per client (ASP.NET rate limiter). Log `api_client_id` on every call.
- Keys are managed from SCM's screens (admin, JWT, `INTEGRATION_MANAGE`).

### 2.6 How one record flows

```
Upsert(payload)
  1. connection for org?           none → NotConnected (in-process callers ignore it; HTTP gets 409)
  2. auto-push for this kind off?  → Disabled
  3. validate payload (rules per kind, D-1..D-9)       fail → Invalid + errors (HTTP 400), and the
                                                       map row is stored as Blocked so the dashboard shows it
  4. dependencies mapped?          no → WaitingOnDependency
                                        in-process: ask the IQuickBooksSource to push them
                                        HTTP: 202 with MissingDependencies. Caller sends them. Entry waits (7-day expiry)
  5. store payload + fingerprint on EntityMap; same fingerprint as last push → Accepted(Synced), no-op
  6. enqueue / merge outbox entry  → Accepted(Pending)                 HTTP 202

SyncOutboxJob (per connection, HangfireTenantScope set)
  mode DryRun → build QBO object, validate, log, state DryRunOk, send nothing
  ledger claim → provider call (create, or sparse update with SyncToken)
  Succeeded   → RemoteId + SyncToken + Synced; release dependents
  Refused     → Failed (field-level reason), no retry until a new payload arrives
  Duplicate   → matching lookup; exact match → adopt (link), otherwise NeedsResolution
  StaleObject → re-read SyncToken, re-apply, retry once
  Throttled / 5xx / timeout → outcome UNKNOWN: look up by name / DocNumber first, then back off and retry
  AuthRevoked → connection Revoked, all entries Suspended
```

### 2.7 How the SDK is used

| Need | SDK |
|---|---|
| Consent, exchange, refresh, revoke | `OAuth2Client` (`GetAuthorizationURL`, `GetBearerTokenAsync`, `RefreshTokenAsync`, `RevokeTokenAsync`) |
| Per-call context | `ServiceContext(realmId, IntuitServicesType.QBO, new OAuth2RequestValidator(token))` |
| Write / read | `DataService.Add`, `Update` (`sparse = true`), `FindById`, `Void` |
| Search | `QueryService<T>.ExecuteIdsQuery(...)` via `QboQueryBuilder` (escapes quotes) |

Rules:

1. Only `Providers/QuickBooks/` references `Intuit.Ipp.*`.
2. Tokens are refreshed **before** the `ServiceContext` is built.
3. Turn off SDK request/response logging and SDK retries (confirm the property names for the installed version).
4. Pin `MinorVersion`.
5. `DataService` is synchronous, so it is only called from jobs.

### 2.8 Project structure

```
src/SMS.Shared/Integration/QuickBooks/          contract (section 2.2), payloads, NullQuickBooksGateway
src/SMS.Shared/Authorization/PermissionCodes.cs + INTEGRATION_VIEW / _MANAGE / _SYNC

src/SMS.Modules.Integration/
├─ SMS.Modules.Integration.csproj   refs: SMS.Shared, SMS.Modules.Tenancy
│                                   pkgs: IppDotNetSdkForQuickBooksApiV3, Hangfire.Core, EF Core SqlServer
├─ IIntegrationModule.cs            Add: DbContext, options, services, ApiKey auth scheme,
│                                   services.Replace(IQuickBooksGateway → QuickBooksGateway)
│                                   Use: Migrate(), Hangfire recurring jobs
├─ Configuration/                   QuickBooksOptions, IntegrationJobOptions
├─ Data/                            IntegrationDbContext (schema "integration"), Maps/, Migrations/
├─ Domain/                          IntegrationConnection, OAuthStateToken, ApiClient, IntegrationSettings,
│                                   TaxCodeMapping, PaymentTermMapping, ReferenceSnapshot, EntityMap,
│                                   SyncOutboxEntry, SyncCommandClaim, SyncLogEntry, Enums
├─ Auth/                            ApiKeyAuthenticationHandler, ApiKeyService (create/hash/verify/revoke)
├─ Gateway/                         QuickBooksGateway : IQuickBooksGateway   (steps 1–6 of section 2.6)
│                                   Validation/ one rule set per kind
│                                   DependencyResolver (uses IEnumerable<IQuickBooksSource>)
├─ Core/                            provider-neutral, no Intuit types
│    Providers/                     IAccountingProvider, IAccountingAuthProvider, registry, ProviderOutcome, Remote DTOs
│    Connections/                   ConnectionService, OAuthStateService, TokenManager, CredentialVault
│    Reference/                     ReferenceDataService, PreflightService
│    Settings/                      IntegrationSettingsService (+ audit)
│    Matching/                      MatchingService
│    Sync/                          SyncOutbox, SyncLedger, SyncExecutor, QboObjectBuilder (payload → Remote DTO:
│                                   name rules, D-2 suffix, D-5 discount line, D-4 tax codes, account defaults)
├─ Providers/QuickBooks/            QuickBooksAuthProvider, QboServiceContextFactory, QuickBooksAccountingProvider,
│                                   QboErrorTranslator, QboQueryBuilder,
│                                   Mapping/ QboCustomerMapper, QboVendorMapper, QboItemMapper, QboInvoiceMapper, QboBillMapper
├─ Jobs/                            SyncOutboxJob, SyncSweepJob (*/5), ReconciliationJob (hourly → IQuickBooksSource.PushAllAsync),
│                                   TokenRefreshJob (daily), ReferenceRefreshJob (daily), DependencyExpiryJob,
│                                   StateTokenCleanupJob, SyncLogRetentionJob
├─ Controllers/
│    Admin/   (JWT)                 ConnectionController, CallbackController ([AllowAnonymous]), SetupController,
│                                   ApiClientsController, MatchingController, SyncController
│    Gateway/ (ApiKey)              CustomersController, VendorsController, ItemsController,
│                                   SalesInvoicesController, BillsController, StatusController
└─ Models/                          admin request/response DTOs (data endpoints reuse the Shared payloads)

SCM modules (each implements IQuickBooksSource for its kinds and calls IQuickBooksGateway on change)
  src/SMS.Modules.Suppliers/Integration/  PartnerQuickBooksSource (Customer + Vendor),
                                          PartnerPayloadFactory (BusinessPartner → Customer/VendorPayload)
  src/SMS.Modules.Inventory/Integration/  VariantQuickBooksSource (Item), VariantPayloadFactory
  src/SMS.Modules.Finance/Integration/    SalesInvoiceQuickBooksSource, BillQuickBooksSource,
                                          SalesInvoicePayloadFactory, BillPayloadFactory (PO line → variant via Demand)

tests/SMS.Modules.Integration.Tests/
   Unit: validation, QboObjectBuilder, mappers, QboQueryBuilder, error translator, TokenManager, ApiKeyService
   Fakes/ScriptedAccountingProvider.cs, FakeQuickBooksSource.cs
   LocalDB HTTP: ApiKey auth (401 cases, tenant mismatch, scopes), admin flows, gateway → outbox → executor
tests/ (existing Suppliers / Inventory / Finance test projects)
   payload factory tests + "calls the gateway on issue/cancel/save" tests against a recording fake gateway
```

### 2.9 Data model (schema `integration`)

- **IntegrationConnection**: `OrganizationId, ProviderKey, RealmId, CompanyName, Environment, Status, EncryptedAccessToken, EncryptedRefreshToken, AccessTokenExpiresAt, RefreshTokenExpiresAt, HomeCurrencyCode, MultiCurrencyEnabled, Country, ConnectedByUserId, ConnectedAt, LastRefreshAt, LastError, RowVersion`. Unique `(OrganizationId, ProviderKey)`.
  `Status`: `NotConnected, Connecting, Connected, NeedsSetup, Live, Revoked, Expired`.
- **OAuthStateToken**: `TokenHash, OrganizationId, UserId, ExpiresAt, UsedAt`.
- **ApiClient**: see section 2.5.
- **IntegrationSettings**: `Mode (DryRun/Live)`, `AutoPush{Customers,Vendors,Items,SalesInvoices,Bills}`, `ItemTypeDefault`, `DefaultIncomeAccountId`, `DefaultExpenseAccountId`, `FreightExpenseAccountId`, `DiscountAccountId`, `PartnerScope (OnlyWhenReferenced/AllActive)`, `DocumentStartDate`.
- **TaxCodeMapping**: `(ConnectionId, TaxPercent) → QboTaxCodeId`. **TaxRateSeen** view: distinct percents received in payloads, which feeds the mapping screen.
- **PaymentTermMapping**: `(ConnectionId, PaymentTermExternalId) → QboTermId`.
- **ReferenceSnapshot**: `(ConnectionId, Kind) → JSON, FetchedAt`.
- **EntityMap**: `ConnectionId, SourceSystem, Kind, ExternalId, DisplayLabel, PayloadJson, PayloadFingerprint, LastPushedFingerprint, RemoteId, RemoteSyncToken, RemoteDocNumber, LinkOrigin (Created/Adopted), State (NotSynced/Pending/InProgress/Synced/DryRunOk/Failed/Blocked/WaitingOnDependency/NeedsResolution/Voided), LastErrorCode, LastError, LastSyncedAt, LinkedByUserId, LinkedAt`. Unique `(ConnectionId, SourceSystem, Kind, ExternalId)`.
- **SyncOutboxEntry**: `EntityMapId, Operation (Upsert/Void), Status (Queued/Running/Done/Failed/Blocked/Suspended), AttemptCount, NextAttemptAt, DependsOn[]`. One open entry per EntityMap.
- **SyncCommandClaim**: ledger, the same semantics as `CarrierCommandLedger`.
- **SyncLogEntry**: `EntityMapId, Operation, RequestJson (redacted), ResponseJson (redacted), ErrorCode, Outcome, DurationMs, IntuitTid, CreatedAt`. 90-day retention.

---

## 3. Decisions required

| # | Decision | Needed by | Recommendation |
|---|---|---|---|
| **D-1** | Currency. QBO fixes a customer's/vendor's currency at creation, and SCM has no exchange rates | QBI-16 | Only **home-currency** partners and documents. Anything else is Invalid with a clear reason |
| **D-2** | QBO `DisplayName` must be unique across customers + vendors + employees | QBI-16 | Customer = name. Vendor = name if free, otherwise `Name (Code)`. The same rule applies to any clash |
| **D-3** | Item type | QBI-16 | `NonInventory` (or `Service`). SCM stays the system of record for stock and cost |
| **D-4** | Tax: SCM has bare percentages | QBI-16 | Tax mapping screen, percent → QBO TaxCode. Unmapped → Invalid. Compare QBO's calculated tax with `ExpectedTaxAmount` and flag differences. US companies refused at preflight |
| **D-5** | Discounts | QBI-16 | Gross line prices + one `DiscountLineDetail` (line discounts + header discount) on `DiscountAccountId` |
| **D-6** | Which partners/variants to push | QBI-22 | `OnlyWhenReferenced` by default. Dependencies are pulled in when an invoice needs them. `AllActive` optional |
| **D-7** | Document triggers | QBI-24/25 | Sales invoice on **ISSUED**. **CANCELLED after push → Void**. **CREDIT_NOTE → Invalid** ("not supported yet"). Bill on `MatchStatus = Approved`. Rejected after push → NeedsResolution |
| **D-8** | Payments not synced | — | Documents stay unpaid in QBO. Show this on the settings screen and tell the accountant |
| **D-9** | Edits after push | QBI-16 | Sparse update. A QBO refusal (the document was changed or paid in QBO) → NeedsResolution. Never delete and recreate |
| **D-10** | Bill tax: supplier invoice lines carry no tax percent | QBI-25 | Confirm with the accountant: one mapped code for the header tax, or require a rate per line |

---

## 4. Field mapping (gateway payload → QBO)

**Customer** → QBO `Customer`: as in the customer report sections 3–6, plus D-1/D-2.
`Code` is sent as a disambiguator and match key only.

**Vendor** → QBO `Vendor`: the same address and contact fields, plus `TaxIdentifier = TaxId`,
`AcctNum = AccountNumber`, `TermRef` via `PaymentTermMapping`, `CurrencyRef` (D-1), and `Active`.

**Item** → QBO `Item`:

| QBO | Payload |
|---|---|
| `Name` | `Name`, or `Name - VariantName` when a variant name is given. ≤ 100 chars, no `:`, unique (suffix ` (Sku)` on a clash) |
| `Sku` | `Sku` |
| `Type` | `Goods` → `NonInventory`, `Service` → `Service` (D-3) |
| `Description` / `UnitPrice` | `Description` / `SalesPrice` |
| `PurchaseDesc` / `PurchaseCost` | when `IsPurchased` |
| `IncomeAccountRef` / `ExpenseAccountRef` | settings defaults (required when `IsSold` / `IsPurchased`) |
| `Active` | `IsActive` |

**Sales invoice** → QBO `Invoice`:

| QBO | Payload |
|---|---|
| `CustomerRef` | `CustomerExternalId` → EntityMap |
| `DocNumber` | `DocNumber` (≤ 21. Needs custom txn numbers on, which preflight checks) |
| `TxnDate`, `DueDate`, `CurrencyRef` | as given (D-1) |
| `SalesItemLineDetail` per line | `ItemRef` via `ItemExternalId`, `Qty`, `UnitPrice`, `Amount = Qty × UnitPrice`, `TaxCodeRef` via TaxCodeMapping |
| `DiscountLineDetail` | Σ line discounts + `HeaderDiscountAmount` (D-5) |
| `GlobalTaxCalculation` | `TaxExcluded` |
| `CustomerMemo`, `PrivateNote` | as given |

**Bill** → QBO `Bill`: `VendorRef` via `VendorExternalId`, `DocNumber`, dates, currency.
- Lines with `ItemExternalId` → `ItemBasedExpenseLineDetail`.
- `Freight` → `AccountBasedExpenseLineDetail` on `FreightExpenseAccountId`.
- Other lines without an item → `DefaultExpenseAccountId`.
- Tax per D-10.

**SCM-side payload factories** (where the source data comes from):

| Payload field | SCM source |
|---|---|
| Customer/Vendor `ExternalId` | `BusinessPartner.UUID` |
| `DisplayName` / `Code` | `SupplierName` / `SupplierCode` |
| `CurrencyCode` | `PreferredCurrency` → `Lookups.Currency.Code` |
| `PaymentTermExternalId` | `PreferredPaymentTerms` |
| Item `ExternalId` | `ProductVariant.Uuid` |
| Item `Name` / `VariantName` | `Product.Name` / `VariantName` (only when the product has more than one variant) |
| Item `Kind` | `Product.ProductType` (service → Service) |
| `SalesPrice` / `PurchaseCost` | `SellingPrice` / `PurchasePrice` |
| `IsSold` / `IsPurchased` | `IsSaleable` / `IsPurchasable` |
| Invoice `ExternalId`, `DocNumber`, `CustomerExternalId` | `SalesInvoice.UUID`, `InvoiceNumber`, `PartnerId` |
| Invoice lines | `VariantUuid`, `Quantity`, `UnitPrice`, `DiscountPercent`, `TaxPercent` |
| `HeaderDiscountAmount` | **0 for SCM** — SCM has no header discount; `DiscountAmount` is the sum of line discounts, which travel per line. Sent as `max(0, DiscountAmount − round(Σ line discounts))` so hand-edited data still reconciles |
| `ExpectedTaxAmount`, `ExpectedTotal` | `TaxAmount`, `GrandTotal` (the gateway checks them rounding once over the invoice, exactly like `SalesInvoiceTotals.Header`) |
| Invoice `PrivateNote` | `SO {SaleOrderNumber} · DLV {DeliveryNumber}` |
| Bill `ExternalId`, `DocNumber`, `VendorExternalId` | `Invoice.UUID`, `SupplierInvoiceNo ?? InvoiceNumber`, `SupplierId` |
| Bill lines | `PoLineUuid` → Demand PO line → `VariantUuid` (Goods). Carrier bill → Freight. Else Other |
| Bill `ExpectedTaxAmount`, `ExpectedTotal`, `PrivateNote` | `TaxAmount`, `TotalAmount`, `{InvoiceNumber} · PO {PoNumber} · GRN {GrnNumber}` |

**SCM triggers** (inside each module's existing service, after its own `SaveChanges`):

| Module | When | Call |
|---|---|---|
| Suppliers | partner created/updated/activated/deactivated, and `IsCustomer`/`IsVendor` | `UpsertCustomerAsync` / `UpsertVendorAsync` (only if already mapped, or scope `AllActive`) |
| Inventory | variant or parent product edited | `UpsertItemAsync` (only if already mapped, or `AllActive`) |
| Finance | sales invoice → ISSUED, or edited while ISSUED | `UpsertSalesInvoiceAsync` |
| Finance | sales invoice → CANCELLED | `VoidSalesInvoiceAsync` |
| Finance | supplier invoice → Approved, or edited while Approved | `UpsertBillAsync` |

A gateway failure must **never** fail the SCM operation. Wrap each call, log the error, and let the
hourly reconciliation (`PushAllAsync(changedSince)`) catch anything missed.

---

## 5. Backend API

### 5.1 Admin: JWT, called by SCM Angular

Prefix `/api/integrations/quickbooks`.

| Method | Path | Permission | Purpose |
|---|---|---|---|
| GET | `/connection` | VIEW | Status, company, realm, environment, token expiries, mode |
| POST | `/connect` | MANAGE | → `{ consentUrl }` |
| GET | `/callback` | anonymous | Intuit redirect → 302 to `FrontendReturnUrl?result=connected\|error&reason=` |
| POST | `/test` | VIEW | CompanyInfo read |
| DELETE | `/connection` | MANAGE | Revoke at Intuit, clear tokens, keep maps |
| GET / POST | `/reference`, `/reference/refresh` | VIEW / MANAGE | Accounts, tax codes, terms, currencies |
| GET | `/preflight` | VIEW | Checks list |
| GET / PUT | `/settings` | VIEW / MANAGE | Audited |
| GET / PUT | `/tax-mappings` | VIEW / MANAGE | GET includes the rates seen in payloads |
| GET / PUT | `/term-mappings` | VIEW / MANAGE | |
| GET / POST / DELETE | `/api-clients`, `/api-clients/{id}/keys`, `/api-clients/{id}/keys/{keyId}` | MANAGE | Create a client, issue a key (shown once), revoke |
| POST / GET / POST | `/matching/{kind}/scan`, `/matching/{kind}`, `/matching/{kind}/confirm` | MANAGE / VIEW / MANAGE | Match stored payloads against existing QBO records |
| GET | `/sync/summary`, `/sync/items`, `/sync/items/{id}/log` | VIEW | Dashboard |
| POST | `/sync/items/{id}/retry`, `/sync/items/{id}/resolve` | SYNC / MANAGE | |
| POST | `/sync/backfill/{kind}` | MANAGE | Calls `IQuickBooksSource.PushAllAsync` |
| POST | `/sync/push` | SYNC | `{kind, externalIds[]}` → `IQuickBooksSource.PushAsync` (the "Push now" button) |
| POST | `/mode` | MANAGE | DryRun ↔ Live. Live requires preflight passed + matching confirmed |
| POST | `/status/lookup` | VIEW | Batch status for badges |

### 5.2 Data: API key, called by other systems

Prefix `/api/gateway/quickbooks/v1`. Headers `X-Tenant-Id`, `X-Api-Key`.

| Method | Path | Scope | Response |
|---|---|---|---|
| PUT | `/customers/{externalId}` | customers:write | 202 `{state}` · 400 errors · 409 not connected |
| PUT | `/vendors/{externalId}` | vendors:write | same |
| PUT | `/items/{externalId}` | items:write | same |
| PUT | `/sales-invoices/{externalId}` | invoices:write | same, or 202 `{state: WaitingOnDependency, missing: [...]}` |
| POST | `/sales-invoices/{externalId}/void` | invoices:write | 202 |
| PUT | `/bills/{externalId}` | bills:write | as invoices |
| GET | `/{kind}/{externalId}` | status:read | `{state, qboId, docNumber, lastError, lastSyncedAt}` |
| POST | `/status` | status:read | batch |

Errors use one consistent shape: `{ code, message, errors: [{ field, code, message }] }`.

---

## 6. Frontend plan (SCM Angular, Option A)

### 6.1 Placement

- **Menu:** Settings → **QuickBooks Integration** (`pi pi-fw pi-sync`), `featureCode: 'MODULE_INTEGRATION'`, `permRequired: ['INTEGRATION_VIEW']`.
- **Route:** `integrations/quickbooks`, `permissionGuard(P.INTEGRATION_VIEW)`.
- **Service:** `services/quickbooks-integration.service.ts`, admin endpoints only. **The Angular app never holds an API key.**

### 6.2 Screens (`pages/integrations/quickbooks/`)

A single page with tabs. Before setup is complete, the tabs unlock in order with a checklist.

| Tab | Contents |
|---|---|
| **Connection** | Intuit-branded Connect button. When connected: company, realm, Sandbox/Production badge, connected by/at, "Reconnect before {date}" warning (< 30 days), Test, Disconnect (confirm: "your QuickBooks data is untouched"). Revoked/Expired banner + Reconnect. Handles `?result=` after the callback |
| **Preflight** | Pass/warn/fail list: home currency vs org base currency, multicurrency, country (US refused), custom txn numbers, tax codes. Refresh button |
| **Mappings** | Default accounts (dropdowns filtered by account type), item type, partner scope, document start date, auto-push toggles. **Tax mapping** (rates seen → QBO tax code). **Payment term mapping** (SCM terms from the existing Lookups API → QBO term) |
| **Initial sync** | Step 1: "Load SCM data (dry run)" runs backfill per kind in DryRun, so the gateway holds and validates every payload. Step 2: shows the **Blocked** records with the rule and the fix ("Name is 124 chars, QuickBooks allows 100") linked to the source record. Step 3 is the Match tab |
| **Match existing** | Customers / Vendors / Items: SCM record · proposed QBO record · reason (name / tax id / email / code / SKU) · Link / Create new / Skip. "Link all exact matches". Nothing is created here |
| **Sync** | Cards per kind by state. Dry run ↔ Live switch (disabled until preflight and matching are done). Paged table with filters, "View in QuickBooks" deep link, Retry / Push now / Resolve, attempt-log drawer, Sync all |
| **API clients** | For other systems: list clients, create one (name + scopes), issue a key (shown once with a copy button and a "you won't see this again" warning), revoke, last used. `INTEGRATION_MANAGE` only |

### 6.3 Sync badge on existing pages

`shared/components/qbo-sync-badge/`:
- Takes `kind` + an id or list of ids, and makes **one** `/status/lookup` call per page.
- Shows a coloured tag with the error in a tooltip, plus a menu: **View in QuickBooks** (`app.qbo.intuit.com` or `app.sandbox.qbo.intuit.com`) and **Push now** (`INTEGRATION_SYNC`).
- Hidden when the org isn't connected or the feature is off.

Placed on:
- `suppliers/partner-detail` (customer and/or vendor badge) and `partner-list` (column)
- the inventory product/variant detail (per variant)
- `finance/sales-invoices` (detail + list)
- `finance/invoices` (detail + list)

---

## 7. Task list

Sizes: S ≈ half a day · M ≈ 1–2 days · L ≈ 3–4 days.

### Group 0: Intuit

| ID | Task | Detail | Size | Status |
|---|---|---|---|---|
| **QBI-00** | Intuit app + secrets | Sandbox company per developer, redirect URIs (local/staging/prod), scope `com.intuit.quickbooks.accounting`, `QuickBooks:*` in user-secrets / Key Vault. **Start the production app review now** | S | TODO |

### Group A: Foundation

| ID | Task | Detail | Size | Status |
|---|---|---|---|---|
| **QBI-01** | Shared contract | Section 2.2 in `SMS.Shared`: `IQuickBooksGateway`, `IQuickBooksSource`, payloads, `GatewayResult`, `SyncKind`, `NullQuickBooksGateway` (TryAdd), permission codes | M | DONE |
| **QBI-02** | Module skeleton | csproj, `IIntegrationModule`, DbContext, options + startup validation, wired into `SMS.API`, `services.Replace` of the gateway | S | DONE |
| **QBI-03** | Domain + migration | Section 2.9 entities, maps, indexes, tenant filters. Idempotent migration, LocalDB only | M | DONE |
| **QBI-04** | Permissions + feature | Role seeding by **code** (see the Supply Dept Admin role fix), `MODULE_INTEGRATION` feature | S | DONE |
| **QBI-05** | Credential vault + provider interfaces | `CredentialVault` over `IEncryptionService`. `IAccountingProvider`, `IAccountingAuthProvider`, `ProviderOutcome`. Scripted fake in tests | M | DONE |
| **QBI-06** | API key auth | `ApiClient`, `ApiKeyService` (generate, SHA-256, verify, rotate, revoke), `ApiKeyAuthenticationHandler` (org claim, scopes, no super-admin claim), tenant-header check, rate limit, scope policies. **Tests: missing/wrong key → 401, tenant mismatch → 401, missing scope → 403, never a tenant-filter bypass** | M | DONE |
### Group B: OAuth and connection

| ID | Task | Detail | Size | Status |
|---|---|---|---|---|
| **QBI-07** | SDK auth provider | `OAuth2Client` wrapper | S | DONE |
| **QBI-08** | Connect + callback | State token (single-use, hashed, 10 min, org-bound). Anonymous callback: exchange, encrypt, bind realm, CompanyInfo, `NeedsSetup`, 302. Replay fails closed. One org maps to one realm | M | DONE |
| **QBI-09** | TokenManager + refresh job | Refresh within 5 min, save rotated token atomically, `RowVersion` race handling, `invalid_grant` → Revoked, daily job + 30-day reconnect notification | M | DONE |
| **QBI-10** | Context factory, test, disconnect | `QboServiceContextFactory` (environment URL, MinorVersion, logging/retry off), `/test`, `/connection` GET/DELETE | S | DONE |
| **QBI-11** | Error translator | SDK exceptions → `ProviderOutcome`, `intuit_tid` captured. Tests from recorded sandbox faults | M | DONE |
### Group C: Reference, preflight, settings, API clients

| ID | Task | Detail | Size | Status |
|---|---|---|---|---|
| **QBI-12** | Reference snapshot + preflight | Accounts, TaxCodes, TaxRates, Terms, Currencies, Preferences, CompanyInfo. Preflight checks. Daily refresh | M | DONE |
| **QBI-13** | Settings + mappings + API clients admin | Settings (audited), tax mapping (+ rates seen), term mapping, `/api-clients` endpoints | M | DONE |
### Group D: Gateway and sync engine

| ID | Task | Detail | Size | Status |
|---|---|---|---|---|
| **QBI-14** | Outbox + ledger | Merge per EntityMap, dependencies, ledger claims | L | DONE |
| **QBI-15** | `QuickBooksGateway` | Section 2.6 steps 1–6, validation framework, dependency resolver via `IQuickBooksSource`, payload storage + fingerprint | L | DONE |
| **QBI-16** | `QboObjectBuilder` + validation rules | Payload → Remote DTO for all five kinds. D-1…D-5, D-9, name rules, V-rules, expected-total checks | L | DONE |
| **QBI-17** | Executor + jobs | Dry-run path, claim, call, outcome handling, unknown-outcome lookup, backoff, Suspend on revoke, `HangfireTenantScope` per connection, sweep, dependency expiry, redacted sync log | L | DONE |
| **QBI-18** | QBO mappers + provider | `QuickBooksAccountingProvider` + five mappers: create / sparse update / find / void | L | DONE |
| **QBI-19** | Data controllers | Section 5.2 over `IQuickBooksGateway`, error shape, HTTP status mapping | M | DONE |
### Group E: SCM callers

| ID | Task | Detail | Size | Status |
|---|---|---|---|---|
| **QBI-20** | Suppliers source | `PartnerPayloadFactory`, `PartnerQuickBooksSource`, triggers in the partner service (guarded, never failing the save) | M | DONE |
| **QBI-21** | Inventory source | `VariantPayloadFactory`, `VariantQuickBooksSource`, triggers | M | DONE |
| **QBI-22** | Matching | Scan remote Customers/Vendors/Items (paged `QueryService`), score against stored payloads, confirm → `Adopted`. Nothing is created | L | DONE |
| **QBI-23** | Reconciliation | Hourly job + `/sync/backfill` + `/sync/push` through `IQuickBooksSource` | S | DONE |
| **QBI-24** | Finance: sales invoices | `SalesInvoicePayloadFactory`, source, triggers on issue/edit/cancel (D-7) | M | DONE |
| **QBI-25** | Finance: bills | `BillPayloadFactory` (PO line → variant via Demand, freight, D-10), source, triggers on approve/edit | M | DONE |
### Group F: Frontend

| ID | Task | Detail | Size | Status |
|---|---|---|---|---|
| **QBI-26** | Service, models, route, menu | Admin endpoints only | S | DONE |
| **QBI-27** | Connection + Preflight tabs | | M | DONE |
| **QBI-28** | Mappings tab | Accounts, tax, terms, toggles | M | DONE |
| **QBI-29** | Initial sync + Match tabs | Dry-run backfill, Blocked list with fixes, match proposals, bulk link | L | DONE |
| **QBI-30** | Sync dashboard | Cards, table, actions, log drawer, mode switch | L | DONE |
| **QBI-31** | API clients tab | Create, show-once key, revoke | S | DONE |
| **QBI-32** | Sync badge + placements | Section 6.3 | M | DONE |
### Group G: Hardening and go-live

| ID | Task | Detail | Size | Status |
|---|---|---|---|---|
| **QBI-33** | Redaction + retention | 90-day log retention. Test that no token/key/secret reaches `SyncLogEntry` or Serilog | S | DONE |
| **QBI-34** | Automated tests | Unit + LocalDB HTTP: auth matrix, admin flows, gateway validation, dependency wait → release, **timeout-after-create gives no duplicate**, throttling, revoke → suspend, two orgs / two realms isolation, SCM factories + triggers. One test class at a time | L | DONE |
| **QBI-35** | Sandbox end-to-end | Connect, preflight, map, dry-run backfill, match pre-seeded records, go Live, push customers/vendors/items/invoices/bills, cancel → void, external push via API key, token refresh across expiry, disconnect → reconnect re-links | M | TODO |
| **QBI-36** | Go-live checklist | Production keys + redirect URI, dry run on the real company reviewed with the accountant, then Live | S | TODO |

**Suggested order:** 00 → 01–06 → 07–11 → 12–13 → 26–28 → 14–19 → 20–21 → 29 + 22 → 23 →
24–25 → 30–32 → 33–36.

---

## 8. Out of scope

Payments, credit notes / credit memos, vendor credits, journal entries, inventory quantity sync,
pulling QBO changes into SCM, webhooks (to QBO or back to callers), multi-currency, US automated
sales tax, and per-category account mapping. Each fits later as a new `SyncKind` + payload + mapper.
