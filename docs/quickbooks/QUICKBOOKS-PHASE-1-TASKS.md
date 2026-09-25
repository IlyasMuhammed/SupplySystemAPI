# QuickBooks Online Integration — Phase 1 — Task List

Execution backlog for Phase 1 of the **QuickBooks Integration Feature & API Matrix** (features #1–13,
#35, #39, #41–44, #50–59, #66, plus one capability the matrix is missing — see D-4 below).

- **Backend:** new `src/SMS.Modules.Accounting`, touching `SMS.Shared`, `SMS.Modules.{Suppliers,Inventory,Tenancy}`
- **Frontend:** `SupplyChainFrontend/src/app/pages/accounting/**`, `src/app/services/accounting*.service.ts`
- **Sibling backlogs, same working agreement and conventions:**
  [`../scm-commercial/ADDENDUM-29-TASKS.md`](../scm-commercial/ADDENDUM-29-TASKS.md) ·
  [`../logistics-rebuild/TASKS.md`](../logistics-rebuild/TASKS.md)

**Read Part A and Part B before starting any task.** Part A records what was verified in this
codebase on 2026-09-24 and corrects several assumptions the matrix makes. Part B describes how a
QuickBooks company is actually connected — what we register with Intuit once, and what each
customer's admin does — which is the prerequisite the matrix's feature #1 compresses into three
words.

---

## Working agreement

1. Tasks are implemented **one at a time**, in order, using this file's `QB-P1-<n>` ids.
2. When a task is done: stop, list what changed and how to verify it.
3. User checks it and says to move on.
4. Do not start the next task until told to.

**Status:** `TODO` · `IN PROGRESS` · `DONE` · `BLOCKED` · `SKIPPED`

---

## Part A — Reality check against this codebase

Verified by reading the code, not assumed. Where the matrix or a task row says the old thing, this
table wins.

### A.1 — What already exists and must be reused, not rebuilt

The single most important finding: **`SMS.Modules.Logistics` already contains a complete,
production third-party integration framework**, built for couriers, that covers matrix features
#50–59 almost one-for-one. Phase 1 reuses its design (and in three cases, promotes its code into
`SMS.Shared`). It does not reinvent any of it.

| Matrix feature | Already exists as | Where |
|---|---|---|
| #51 Command Ledger, #52 Idempotency | `ICarrierCommandLedger`, `CarrierCommandClaim`, `LedgerDecision` with `Proceed / AlreadySucceeded / AlreadyRefused / InFlight / NeedsResolution / KeyRetired`, 5-minute lease + 2-minute grace, `AttemptCount` | `Logistics/Couriers/CarrierCommandLedger.cs` |
| #53 Request Fingerprint | `CourierRequestFingerprint` | `Logistics/Couriers/CourierRequestFingerprint.cs` |
| #50 Sync Queue, #54 Retry, #58 Background Sweep | `ConsignmentBookingService` (executor) + `IConsignmentBookingScheduler` (Hangfire) + `ConsignmentBookingJob` + `ConsignmentBookingSweepJob` on `*/5 * * * *` | `Logistics/Couriers/Booking/` |
| #56 Manual Resolution | `LedgerDecisionKind.NeedsResolution` + `KeyRetired` — the "a person must check with the provider" path is already modelled | `Logistics/Couriers/CarrierCommandLedger.cs` |
| #4, #5 token/secret storage | `ICarrierCredentialVault` over `IEncryptionService` / `AesEncryptionService` (registered by `TryAdd` in both Logistics and Suppliers) | `Logistics/Couriers/CarrierCredentialVault.cs` |
| Provider abstraction | `ICourierProvider` + `ICourierProviderRegistry` resolved by `Carrier.ProviderKey`; `CourierOutcome { Succeeded, Refused, Failed, Unsupported }` | `Logistics/Couriers/` |
| Testing without a sandbox | `SimulatorCourierProvider`, config-gated by `Logistics:CourierSimulator:Enabled` | `Logistics/Couriers/Simulator/` |
| #69 Webhooks (Phase 2) | `ICourierWebhookReceiver`, `CarrierWebhookService` — anonymous receiver precedent | `Logistics/Couriers/Tracking/` |
| #59 Token Refresh Job | Hangfire `RecurringJob.AddOrUpdate` in `UseLogisticsModule` | `ILogisticsModule.cs` |
| Outbound HTTP | Named `HttpClient` per provider, 60s timeout, singleton adapter | `ILogisticsModule.cs` line 133 |

`CourierOutcome`'s doc comment is the design brief for this phase, verbatim: *"a carrier saying 'no'
is an answer, and a call that never completed is not… Collapsing them into one failure is how a
booking retry ends up putting two labels on one box."* Replace *label on a box* with *invoice in a
company's books* and nothing else changes.

### A.2 — Other platform facts

| Assumption | Reality |
|---|---|
| React frontend | Angular 19 + PrimeNG 19, `SupplyChainFrontend/`. Pages in `src/app/pages/<feature>/`, services in `src/app/services/<name>.service.ts`, permission checks via `AuthService.hasPermission(CODE)`, toasts via PrimeNG `MessageService` |
| Module layout | One project per module with its own `DbContext`, schema and migrations. `IXModule.cs` exposes `AddXModule(IServiceCollection, IConfiguration)` and `UseXModule(IApplicationBuilder)`; `Use…` calls `db.Database.Migrate()` and registers Hangfire recurring jobs. Connection string is `configuration["Data:mainOrg"]`, always with `EnableRetryOnFailure(3, 500ms, null)` |
| Cross-module FKs | Bare `Guid` (`.UUID`), never `.Id`, never a real FK. Resolved at the app layer |
| Tenant scoping | `ITenantScopedEntity` + global query filter. **Known issue:** the filter's `IsSuperAdmin OR org = @o` shape defeats index seeks — a cross-org sweep must not rely on it. Copy how `InvoiceOverdueJob` and `ConsignmentBookingSweepJob` scope without a user context |
| Settings storage | Two separate things: `Tenancy.OrganizationSettings` (single row per org, one typed column per knob) and per-module config services. The closest precedent for this phase's settings screen is `SaleOrderConfigService` + `SaleOrderConfigRules` + `SaleOrderConfigController` in Demand, which also has an audit model (`SaleOrderConfigAuditModel`) — settings changes are recorded |
| Feature gating | A per-org feature-flag system exists: `FeatureDefinition`, `PlanFeatureTemplate`, `OrganizationFeature`, `TenancyService.UpdateFeaturesAsync`, frontend `features.service.ts` |
| Permissions | `SMS.Shared/Authorization/PermissionCodes.cs`, `SCREAMING_SNAKE` constants, defined alongside the feature that gates them (not pre-declared) |
| Domain events | MediatR events in `SMS.WorkflowEngine/Events/`: `DocumentSubmittedEvent`, `DocumentApprovedEvent`, `DocumentCancelledEvent`, `DocumentReissuedEvent`, `DocumentRecalledEvent`, `DocumentRejectedEvent`. **Confirm in QB-P1-20 whether BusinessPartner saves raise anything** — partners are master data and may not go through the workflow engine at all |
| Startup migrations | Every API start runs every module's migrations against the shared `SMSGlobal` Azure DB, which has drifted. New migrations must be idempotent; test on LocalDB only |
| AutoMapper / FluentValidation | Registered globally but used almost nowhere. Hand-written DTO mapping in the service layer is the real convention |

### A.3 — Data facts that constrain the design

| Fact | Where | Consequence |
|---|---|---|
| `Organization.BaseCurrency` is `Guid?` → `Lookups.Currency.Id`, read via `IOrganizationCurrencyService.GetBaseCurrencyIdAsync` | `Tenancy/Domain/TenancyEntities.cs:22` | This is the only home-currency signal we have. QB-P1-11 compares it against QBO's |
| `BusinessPartner.PreferredCurrency` is `Guid?` | `Suppliers/Domain/SupplierEntities.cs:43` | **A QBO Customer's currency is fixed at creation and cannot change once transactions exist.** This field is what we pin it from. If it is null, we must not guess |
| `Lookups.Currency` is `{ Id, Name, Code, Symbol }` | `Lookups/Domain/LookupEntities.cs:21-27` | No rate, no decimal places |
| **No exchange rate exists anywhere in `src/`** — `ExchangeRate`, `FxRate`, `ConversionRate` all return zero hits | — | Phase 1 does not need one (no transactions are pushed). Phase 2 cannot start without one. Recorded here so it is not discovered late |
| `BusinessPartner` has `IsVendor / IsCustomer / IsCarrier / IsServiceProvider` with `PartnerType` derived, including `PartnerType.Both` | `Suppliers/Domain/PartnerCode.cs:60-73` | The dual-role case is first-class in our model and **does not exist in QBO** — Customer and Vendor are separate entities. One partner row maps to up to two remote records. The mapping table must allow this from day one (QB-P1-15) |
| Matching fields available: `SupplierName`, `Email`, `PrimaryContactEmail` | `Suppliers/Domain/SupplierEntities.cs` | These are what QB-P1-16 matches on. There is no tax-registration field — confirm before relying on one |
| `Lookups.PaymentTerm` is `{ Id, Name, Days }` | `Lookups/Domain/LookupEntities.cs:36-41` | Maps cleanly to QBO `Term` (#42) |
| `SalesInvoice.CurrencyCode` is a `string` defaulted to `"PKR"`, independent of both `Organization.BaseCurrency` and `BusinessPartner.PreferredCurrency` | `Finance/Domain/SalesInvoiceEntities.cs:79` | Phase 2 problem, but QB-P1-11 should report the inconsistency so it is visible early |

---

## Part B — How a company actually connects

The rest of this document describes what we build. This part describes what a *person* does, and
what has to exist before they can do it. Everything in B.1 happens once, by us; everything in B.2
happens per customer.

> **Verify B.1 against current Intuit developer documentation before starting QB-P1-00.** The
> specifics below reflect how Intuit's OAuth has worked for several years, but key details — the
> production-credential gate in particular — change, and the whole phase depends on them.

### B.1 — What we do once, as the software vendor

QuickBooks does not work like a username and password a customer types into SCM. We register **one
application** with Intuit, and every customer connects *their* company to *our* application.

1. **Create an Intuit Developer account** at `developer.intuit.com`. This is ours as a product
   company, not per-tenant. A sandbox QuickBooks company comes with it.
2. **Create the app.** It gets two independent key pairs — **Development** (sandbox only) and
   **Production** (real companies). Each pair is a **Client ID** and **Client Secret**.
3. **Register redirect URIs.** Intuit matches these *exactly*, and they must be HTTPS (localhost is
   the only exception). We need one per environment — local, staging, production — registered up
   front, because an unregistered URI fails at consent with an error the end user cannot act on.
4. **Select scopes.** `com.intuit.quickbooks.accounting` is what this phase needs. Adding scopes
   later forces every connected customer to re-consent, so decide once.
5. **Start Intuit's app review early.** Production credentials are gated behind an assessment of the
   app. It has lead time measured in weeks, it is not a formality, and it is entirely independent of
   our code. **Begin it in week one and build against sandbox meanwhile** — this is the single
   likeliest cause of a Phase 1 that is finished but cannot go live.

**Where the secrets live.** The Client ID and Secret identify *SCM as a product* and are the same
for every tenant, so they belong in application configuration and the platform secret store —
**not** in the per-organization credential vault. The vault holds only what is genuinely per-tenant:
that company's access token, refresh token and `realmId`. Getting this split wrong is how one
customer's misconfiguration takes down everyone's connection.

**Why the redirect URI being shared matters.** One registered callback URL serves every tenant, and
Intuit's redirect carries no authentication of ours. That is precisely why the `state` token in
QB-P1-06 must be single-use, short-lived and org-bound: it is the *only* thing that tells the
callback which organization is connecting. This is not defensive extra credit — without it the
endpoint cannot function at all.

### B.2 — What a customer's admin does

The journey, as they experience it. QB-P1-24 builds this as a guided first-run flow, not a page of
disconnected buttons.

| Step | What they see | What happens underneath |
|---|---|---|
| 1 | **Settings → Accounting Integration**, showing "Not connected" and a **Connect to QuickBooks** button (Intuit supplies a required branded button) | — |
| 2 | They click Connect and land on **Intuit's own sign-in page** | We mint a single-use state token, build the consent URL, redirect |
| 3 | They sign in **with their QuickBooks credentials — never ours, and we never see them** — and choose which company to connect if they have several | Intuit handles this entirely |
| 4 | A consent screen naming our app and what it may access | Scope granted in B.1 step 4 |
| 5 | They are returned to SCM | Intuit redirects to our callback with `code`, `state` and `realmId`. We resolve the org from `state`, exchange the code for tokens, encrypt and store them, bind the realm |
| 6 | **"Connected to «their company name»"**, plus what we found: home currency, multicurrency on or off, tax codes, chart of accounts, payment terms | The preflight of QB-P1-10, run immediately |
| 7 | **The currency verdict** — either "compatible" or a refusal explaining precisely why | QB-P1-11. See D-1; this is the gate that cannot be deferred |
| 8 | **"We found 143 customers already in your QuickBooks"** — a review screen matching them to ours, with bulk-confirm for exact matches | QB-P1-16 / QB-P1-17. **Nothing has been written to their books yet** |
| 9 | They confirm, and choose **Dry run** or **Live** | QB-P1-25. Per D-5, dry run builds and validates everything and sends nothing |
| 10 | Ongoing: connection status, token health, sync dashboard, **Disconnect** | Disconnect removes our side only; their accounting data is untouched and the mapping is kept so reconnecting the same realm re-adopts rather than re-creates |

**One SCM organization connects to exactly one QuickBooks company.** A customer running several
companies needs several SCM organizations, and the unique constraint in QB-P1-15 should say so.

### B.3 — The connection states, and what breaks them

Token behaviour drives most of what the user will ever see go wrong, so the connection is a state
machine, not a boolean:

- `NOT_CONNECTED` · `CONNECTING` (state token issued, awaiting callback) · `CONNECTED` ·
  `NEEDS_REVIEW` (connected, adoption not yet confirmed) · `ARMED` (live) · `REVOKED` · `EXPIRED`

The two that are easy to miss:

- **`REVOKED`** — the customer can disconnect our app from inside QuickBooks at any time, without
  telling us. The next call fails with an auth error. That is a *revocation*, not a transient
  failure: outbox entries go to `SUSPENDED` and the UI asks them to reconnect. Retrying is pointless
  and fills the error queue with noise.
- **`EXPIRED`** — the refresh token has a long but finite life (on the order of 100 days) and
  **rotates on every use**, so a company that syncs nothing for a season silently dies. This is why
  QB-P1-08's refresh job runs on a schedule independent of sync activity, and why the rotated token
  must be persisted atomically — write the new one and fail to commit, and the connection is gone
  with no way back but reconnecting.

---

## Decisions required before the tasks they gate

These are product decisions, not engineering ones. Each names the task it blocks.

| ID | Decision | Blocks | Why it cannot be deferred |
|---|---|---|---|
| **D-1** | **Is an organization single-currency or genuinely multi-currency?** | QB-P1-11, QB-P1-18 | A QBO Customer's currency is set at creation and immutable once transactions exist. Pushing customers *is* choosing every customer's currency permanently. If single-currency: declare it, refuse to connect when QBO's home currency differs, done. If multi-currency: `BusinessPartner.PreferredCurrency` becomes mandatory for customers, and Phase 2 needs FX rates |
| **D-2** | **Item granularity: one QBO item per variant, or per product category?** | QB-P1-22, QB-P1-23 | Per-variant dumps the whole catalogue into the customer's item list, most of it never appearing on an invoice, and QBO item lists are painful to clean up. Per-category gives the accountant a readable P&L and makes COGS (#49) far easier. SCM already owns SKU-level truth via the margin and product-ledger reports. **Recommendation: default to category, make it a setting** |
| **D-3** | **What happens when the accountant edits a record in QBO?** | QB-P1-21 | Matrix says "No master overwrite", which is right, but then the two records silently diverge and nothing fails. Options: report-only, report-and-alert, or SCM re-pushes on next change. **Recommendation: report-only in Phase 1** |
| **D-4** | **Adoption: how are links to a pre-existing QBO company established?** | QB-P1-16, QB-P1-17 | **Not in the matrix at all.** A connecting company already has customers, vendors and items entered by their accountant. Pushing our list creates duplicates of records that already exist — the commonest way these integrations lose trust on day one. Phase 1 must match-and-link first and create only as a fallback, with a human confirming ambiguous matches |
| **D-5** | **Does Phase 1 go live, or stop at dry run?** | QB-P1-25 | Master data is the low-stakes rehearsal for the reliability machinery. A wrong customer is a deleted row; a wrong invoice is an accountant's afternoon. Recommendation: Phase 1 goes live for customers and vendors, items stay lazy and therefore inert until Phase 2 |
| **D-6** | **Confirm QBO's idempotency mechanism against current Intuit docs.** | QB-P1-12 | Matrix #52 already flags this. QBO has no general idempotency-key header; the usual technique is putting our own document number in `DocNumber` and querying by it after a timeout. For Phase 1's master data the equivalent is query-by-`DisplayName` before create. Settle it in writing before QB-P1-12 |

---

## Phase 1 — scope

**In:** matrix #1–13, #35, #39, #41–44, #50–59, #66, plus adoption (D-4).
**Out:** every transaction feature — #14–34, #36–38, #40, #45–49, #60–65, #67–70. No invoice, bill,
payment, credit note or tax mapping is built, pushed or read in this phase.

Columns match the A29 backlog: **ID · Task Title · Description · Matrix Ref · Depends On · Est. Hours · Status**.

### Group 0 — Intuit application and environments

Not a coding task, and the only one on this list that cannot be unblocked by writing code. It comes
first because nothing else can reach QuickBooks without it, and because its long pole runs
independently of everything else.

| ID | Task Title | Description | Matrix Ref | Depends On | Est. Hours | Status |
|---|---|---|---|---|---|---|
| **QB-P1-00** | Intuit developer app, credentials and environments | Per **Part B.1**: create the Intuit Developer account and app; capture Development and Production Client ID / Secret; register the exact redirect URIs for local, staging and production; select `com.intuit.quickbooks.accounting`. Plumb client credentials through application configuration and the platform secret store — **never** the per-org vault (B.1) — with an `Accounting:QuickBooks:Environment` switch for sandbox vs production and a documented sandbox company for development. **Open Intuit's production app review in the same week**; it has weeks of lead time, is independent of our code, and is the likeliest reason a finished Phase 1 cannot go live. Record its status in this row as it progresses | #1, #4 | — | 6 | TODO |

### Group A — Module foundation

| ID | Task Title | Description | Matrix Ref | Depends On | Est. Hours | Status |
|---|---|---|---|---|---|---|
| **QB-P1-01** | `SMS.Modules.Accounting` skeleton | New project, `AccountingDbContext`, schema `accounting`, `IAccountingModule` with `AddAccountingModule` / `UseAccountingModule`, `IDesignTimeDbContextFactory`, registration in `Program.cs`. Follow `ILogisticsModule.cs` exactly, including `EnableRetryOnFailure(3, 500ms, null)`. Named after the capability, not the vendor — QuickBooks is one provider | — | 00 | 4 | TODO |
| **QB-P1-02** | Permissions and feature flag | Add `ACCOUNTING_CONNECTION_MANAGE`, `ACCOUNTING_MAPPING_MANAGE`, `ACCOUNTING_SYNC_VIEW`, `ACCOUNTING_SYNC_RESOLVE` to `PermissionCodes.cs`. Register org feature `ACCOUNTING_INTEGRATION` in the feature catalogue so the whole module is switchable per tenant. Seed onto Supply Dept Admin | #1 | 01 | 3 | TODO |

### Group B — Provider abstraction

| ID | Task Title | Description | Matrix Ref | Depends On | Est. Hours | Status |
|---|---|---|---|---|---|---|
| **QB-P1-03** | `IAccountingProvider` + registry | Mirror `ICourierProvider` / `ICourierProviderRegistry`: adapters self-register, resolved by `AccountingConnection.ProviderKey`. Reuse the `CourierOutcome` shape — `Succeeded / Refused / Failed / Unsupported` — under an accounting name. **`Refused` and `Failed` must stay distinct**; that distinction is the whole reliability design | — | 01 | 4 | TODO |
| **QB-P1-04** | Connection entity + credential vault | `AccountingConnection` (org, provider key, realm id, status, connected-by, connected-at). `IAccountingCredentialVault` over the existing `IEncryptionService`, registered with `TryAdd` as Logistics and Suppliers both do. Tokens never leave the vault in plaintext and never reach a log | #3, #4, #5 | 03 | 5 | TODO |
| **QB-P1-05** | Simulator provider | In-process fake QBO company: customers, vendors, items, accounts, tax codes, currencies, preferences, plus injectable timeouts, refusals and duplicate-name collisions. Config-gated exactly like `Logistics:CourierSimulator:Enabled`. **Everything downstream is built and tested against this before an Intuit sandbox exists**, and it is how the retry tests in QB-P1-28 stay deterministic | — | 03 | 8 | TODO |

### Group C — OAuth and connection lifecycle

| ID | Task Title | Description | Matrix Ref | Depends On | Est. Hours | Status |
|---|---|---|---|---|---|---|
| **QB-P1-06** | OAuth start + state token | `POST /api/accounting/connect` issues a single-use, short-TTL, org-bound state token and returns the Intuit consent URL. The token is the only thing carrying tenant identity into the callback | #1 | 04 | 5 | TODO |
| **QB-P1-07** | Anonymous callback | `GET /api/accounting/callback` — **anonymous, no JWT**, resolves the org from the state token alone, exchanges the code, stores tokens encrypted, binds `realmId`. Follow `CarrierWebhookService`'s precedent for an anonymous endpoint that checks the organization itself rather than relying on the feature filter. Replay of a used state token must fail closed | #1, #3 | 06 | 6 | TODO |
| **QB-P1-08** | Token refresh job | Refresh before expiry, persist the rotated refresh token atomically, Hangfire recurring job registered in `UseAccountingModule`. Independent of any sync activity — an org that pushes nothing for a month must still hold a live connection | #5, #59 | 07 | 5 | TODO |
| **QB-P1-09** | Test and disconnect | `POST /api/accounting/test` does a cheap authenticated read. `DELETE /api/accounting/connection` removes our side only — no QBO data is touched, and the mapping table is retained so reconnecting the same realm re-adopts rather than re-creates | #2, #6 | 07 | 3 | TODO |

### Group D — Connection profile (reference data)

| ID | Task Title | Description | Matrix Ref | Depends On | Est. Hours | Status |
|---|---|---|---|---|---|---|
| **QB-P1-10** | Preflight reader + profile snapshot | One operation, not six: read `Preferences`, `CompanyCurrency`, `TaxCode`, `Account`, `Term`, `PaymentMethod` and store them as a single `ConnectionProfile` row with a captured-at timestamp. Refreshed on reconnect and on a daily Hangfire job. Everything downstream reads the snapshot, not the API | #35, #39, #41, #42, #43, #44 | 05, 07 | 8 | TODO |
| **QB-P1-11** | Currency compatibility gate | Compare the profile's home currency and multicurrency flag against `Organization.BaseCurrency`. Per **D-1**: if single-currency and they differ, refuse to arm the connection with an explanation. If multi-currency, require `BusinessPartner.PreferredCurrency` on every customer before it can be pushed. Also report how many `SalesInvoice` rows carry a `CurrencyCode` inconsistent with the org base — read-only, but it is the Phase 2 blocker and should be visible now | #43, #44 | 10 | 4 | TODO |

### Group E — Reliability layer

| ID | Task Title | Description | Matrix Ref | Depends On | Est. Hours | Status |
|---|---|---|---|---|---|---|
| **QB-P1-12** | Command ledger + fingerprint | Port `CarrierCommandLedger` and `CourierRequestFingerprint` to accounting commands, keeping the six `LedgerDecisionKind` values and the lease-plus-grace model. Where the shapes are identical, promote to `SMS.Shared` rather than copy — decide which in the write-up. Per **D-6**, the pre-call existence check for master data is query-by-`DisplayName` | #51, #52, #53 | 03 | 8 | TODO |
| **QB-P1-13** | Outbox + executor + sweep | `AccountingOutboxEntry` (PENDING / IN-FLIGHT / SUCCEEDED / REFUSED / NEEDS-RESOLUTION / SUSPENDED), an enqueue service, a Hangfire job per entry and a `*/5` sweep, mirroring `ConsignmentBookingService` / `Job` / `SweepJob`. **Work queues even when the integration is off or disconnected** — entries go to SUSPENDED, never discarded, so a disabled week is recoverable. The sweep must scope tenants the way `ConsignmentBookingSweepJob` does, not through the global filter | #50, #54, #58 | 12 | 8 | TODO |
| **QB-P1-14** | Sync log | Request, response, status, duration and error per attempt, with credentials and tokens redacted at the boundary rather than filtered later. Retention policy set now, not after it fills | #57 | 13 | 5 | TODO |

### Group F — Mapping and adoption

| ID | Task Title | Description | Matrix Ref | Depends On | Est. Hours | Status |
|---|---|---|---|---|---|---|
| **QB-P1-15** | Mapping table | `AccountingEntityMap`: local uuid, local kind, provider key, realm id, remote id, remote sync token, last-pushed fingerprint, link state, linked-by, linked-at. **Unique on (org, provider, realm, local uuid, remote kind)** — not on local uuid alone, because a `PartnerType.Both` row legitimately maps to both a QBO Customer and a QBO Vendor | #11, #12, #13 | 04 | 5 | TODO |
| **QB-P1-16** | Match-and-adopt service | Before creating anything, search the connected company: exact name, then email, then a normalised-name comparison. Classify `ExactSingle` (link silently), `Ambiguous` (queue for a human), `None` (create). Handles QBO's unique-`DisplayName` constraint as a first-class outcome, not an error. **This is the task that stops Phase 1 duplicating the accountant's existing records** | D-4 | 15, 10 | 8 | TODO |
| **QB-P1-17** | Link review API + screen | List unlinked and ambiguous partners with candidate matches; confirm, force-create, or unlink. Bulk-confirm the exact matches. Nothing is created in QBO until this screen has been through once — an integration that silently writes into someone's books on connect is the failure mode this prevents | D-4 | 16 | 10 | TODO |

### Group G — Customers and vendors

| ID | Task Title | Description | Matrix Ref | Depends On | Est. Hours | Status |
|---|---|---|---|---|---|---|
| **QB-P1-18** | Customer push | Create and update QBO `Customer` from `BusinessPartner` where `IsCustomer`. Currency pinned per **D-1** and never changed afterwards. A `PartnerType.Both` row produces a Customer here and a Vendor in QB-P1-19, two map rows, one partner | #7, #11 | 13, 16 | 8 | TODO |
| **QB-P1-19** | Vendor push | Same for `IsVendor` → QBO `Vendor`. Note the open A29 finding that vendor-ness is not enforced on POs and invoices; this task surfaces the affected rows but does not fix them | #8, #13, #27 | 18 | 5 | TODO |
| **QB-P1-20** | Change detection and enqueue | Enqueue on partner create and update, using the stored fingerprint so an unchanged save is a no-op rather than a call. Establish first whether `BusinessPartner` saves raise any MediatR event (Part A.2) — if not, enqueue from `BusinessPartnerService`, and say so in the write-up | #7, #8, #53 | 18, 19 | 5 | TODO |
| **QB-P1-21** | Drift detection sweep | Daily read-back comparing remote records against ours; report differences per **D-3**, never auto-overwrite. This is the only thing that makes "No master overwrite" observable rather than merely true | #7, #8 | 20 | 6 | TODO |

### Group H — Items

| ID | Task Title | Description | Matrix Ref | Depends On | Est. Hours | Status |
|---|---|---|---|---|---|---|
| **QB-P1-22** | Item granularity setting | Implement **D-2** as a setting on the connection: per-variant or per-category, plus the category-to-item mapping when per-category. Follow `SaleOrderConfigService` / `SaleOrderConfigRules` including server-side validation and the audit trail | #9, #12 | 15 | 5 | TODO |
| **QB-P1-23** | Item resolver (lazy) | `EnsureItemAsync(variantUuid)` — resolves through the mapping table, adopts an existing QBO item by name, creates a non-inventory item only when nothing matches. **Nothing calls it in Phase 1**; it exists, is tested against the simulator, and is what Phase 2's invoice push will call. Respects the #45–47 boundary: QBO is never given an inventory-valued item | #9, #10, #12, #47 | 22, 16 | 6 | TODO |

### Group I — Frontend

| ID | Task Title | Description | Matrix Ref | Depends On | Est. Hours | Status |
|---|---|---|---|---|---|---|
| **QB-P1-24** | Connect wizard and connection settings | **The screen that answers "how do I connect QuickBooks", and the one an admin judges the whole integration by.** Two modes over one route. *First run* is the guided flow of **Part B.2** — Intuit's branded Connect button, the return from consent, the preflight findings, the currency verdict, the adoption review, and the choice of dry run or live — where each step only unlocks once the previous one passes, so nobody can arm a connection they have not reviewed. *Ongoing* is the settings view: connection state per **B.3** (including `REVOKED` and `EXPIRED`, each with the one action that fixes it), company name and realm, token health, test / reconnect / disconnect, the profile snapshot, and the item-granularity setting. Follow `sale-order-settings` — its options carry `description`, `recommended` and `caution` text, and this screen needs that more, not less | #1, #2, #6, #43, #44 | 09, 11, 17, 22 | 12 | TODO |
| **QB-P1-25** | Sync dashboard and error queue | Counts by outbox state, the failed and needs-resolution queues with their ledger explanation, per-entry log, retry and retire-key actions behind `ACCOUNTING_SYNC_RESOLVE`. Per **D-5**, carries the arm/disarm control for going live | #55, #56, #66 | 14 | 10 | TODO |
| **QB-P1-26** | Mapping browser | Customers, vendors and items with link state — linked, unlinked, ambiguous, conflicting — search, and manual link or unlink. Shares components with QB-P1-17 | #11, #12, #13 | 17 | 8 | TODO |

### Group J — Tests and sandbox

| ID | Task Title | Description | Matrix Ref | Depends On | Est. Hours | Status |
|---|---|---|---|---|---|---|
| **QB-P1-27** | Integration tests vs simulator | LocalDB HTTP tests as in `tests/SMS.Modules.*.Tests`: connect, preflight, adopt, link, push, drift, disconnect and reconnect. Includes multi-tenant isolation — two orgs, two realms, no leakage | — | 21, 23 | 8 | TODO |
| **QB-P1-28** | Reliability tests | The cases the ledger exists for: timeout then retry creates nothing twice; two workers claim one entry and one waits; a refusal is not retried; a changed source produces a new fingerprint and a new key; a `NeedsResolution` entry stays stuck until a person acts. Driven by the simulator's injectable faults | #51, #52, #53, #54 | 27 | 6 | TODO |
| **QB-P1-29** | Intuit sandbox smoke run | First contact with a real QBO sandbox: OAuth round trip, token refresh over an expiry boundary, preflight against a real company, adopt against pre-seeded customers. Record every difference from the simulator and fix the simulator, not the test | all | 28 | 5 | TODO |

**Total: 30 tasks, ~176 hours.**

---

## Ordering rationale

Four things drive the sequence.

**Group 0 comes first because it is the only task code cannot unblock.** Everything else is ours to
finish; Intuit's production review is not, and it runs on its own clock. Start it in week one and
develop against the sandbox, or Phase 1 lands complete and unable to connect a real company.

**The reliability layer comes before the first push, on purpose.** Groups E and F are built before
anything is written to a customer's QuickBooks. This is the argument for doing master data first at
all: it is the only chance to debug idempotency, leasing and retry where the cost of a duplicate is
an extra customer row rather than a duplicate invoice in a real company's accounts. Same machinery,
same bugs, different blast radius. By Phase 2 it is proven code.

**The currency gate comes before the first customer.** QB-P1-11 sits ahead of QB-P1-18 because a
QBO Customer's currency cannot be changed after the fact. Phase 1 does not defer the currency
question — it is the phase that answers it permanently.

**Adoption comes before creation.** QB-P1-16 and QB-P1-17 precede every push. Nothing is created in
the connected company until a human has reviewed what already exists there.

## What Phase 2 inherits, and what it still needs

Inherits: module, provider abstraction, OAuth, vault, ledger, outbox, sweep, log, mapping, adoption,
dashboard, simulator and the item resolver.

Still blocked on SCM-side work that is **not** in this phase:

1. **Exchange rates** — nothing in `src/` records one. Required before any transaction in a
   non-home currency can be pushed.
2. **Tax as a code, not a percentage** — `SalesInvoiceLine.TaxPercent` is a bare decimal, so
   zero-rated, exempt and out-of-scope are indistinguishable. Matrix #36 has nothing to map from
   until this exists.
3. **Credit-note modelling** — `CREDIT_NOTE` is a value in `SalesInvoiceStatuses` alongside `PAID`
   and `OVERDUE`, so one field carries both document type and lifecycle. QBO needs a separate
   `CreditMemo` entity with its own numbering.
4. **Self-reconciliation** — `AmountPaid` and `BalanceDue` are stored rather than computed. Confirm
   they equal the sum of allocations across the estate before comparing anything to QBO, or internal
   drift will present as integration failure.
