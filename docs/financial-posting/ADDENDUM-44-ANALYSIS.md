# Addendum 44 — Financial Posting Gateway & QuickBooks Integration: analysis & decisions

Source: SMS-FSD-ADD-044 v1.0 + task register A44-0001..0578, received 2026-10-16. Contract: [API-CONTRACT.md](API-CONTRACT.md). Status: [ADDENDUM-44-TASKS.md](ADDENDUM-44-TASKS.md).
User said "start working as per attached tasks and FSD … open multiple agents" → recommended defaults adopted (standing rule: decide, record here, build; ask only for irreversible live-data or security loss).

**Read Part A before writing any code.** The FSD was written as if SMS had no QuickBooks integration and no GL. Both exist and are live (QuickBooks verified end-to-end on the sandbox 2026-10-13/14). A44 is therefore built as a **provider-neutral posting layer over what exists, plus the real gaps** — never a second QuickBooks stack.

## Part A — spec vs. this repo

| Spec says | Reality here | Consequence |
|---|---|---|
| React 18 | Angular 19 + PrimeNG, SMS Flow `sf-*` kit + `--sms-*` tokens | All UI = Angular (D-30) |
| `SMS.Core/Contracts/Finance`, `SMS.Finance`, `SMS.Sales`, `SMS.Purchase` | Shared kernel is `SMS.Shared`; modules `SMS.Modules.Finance` (invoices, payments, notes, ledgers), `SMS.Modules.Accounting` (A42 GL), `SMS.Modules.Integration` (QuickBooks), `SMS.Modules.Pos`, `Suppliers`, `Inventory` | D-2 |
| `OrgId int`, `SourceDocumentId int` | `Guid OrganizationId` (ambient `ITenantContext`), int Id + Guid Uuid; routes use uuids | D-1 |
| New `FinanceDbContext` with all tables | Finance module already has a `FinanceDbContext` (schema `finance`); Accounting has `AccountingDbContext` (schema `finance`); Integration has `IntegrationDbContext` (schema `integration`) | New tables go to the context that owns the concern (D-2); migration history = shared `dbo.__EFMigrationsHistory` like every module (A44-0041 deviation) |
| `ExternalSystemConnections` with tokens, DPAPI encryption | `integration.Connections` + `CredentialVault` (AES `IEncryptionService`), one connection per org, pinned app (A-keys 2026-10-13) | Reuse; no new table (D-6) |
| OAuth client_id/secret **in app configuration, not DB** (A44-0176) | User decided on 2026-10-13: keys **in the DB** (platform row, super admin UI) + per-org own app behind FEATURE_QBO_OWN_APP; config is only the last fallback | **Deviation D-7 — the user's explicit decision wins** |
| `IQuickBooksApiClient` with HttpClient, 401 refresh | Intuit SDK `QboClient` + `TokenManager` (refresh before expiry, daily refresh job), error translator | Reuse (D-6) |
| `FinancialPostingOutbox` processor every 30 s, backoff 30s×4ⁿ, 5 retries, DeadLetter | Integration outbox: 1-min job, backoff 1m…24h, 8 attempts, Failed+NeedsResolution = dead letter, leases, command ledger, dependency waits | Keep the proven policy; add dead-letter view/bulk retry/skip and an **immediate kick** on enqueue (D-11) |
| `SyncLogs` | `integration.SyncLog` (per call, redacted request/response, 90-day retention) + `EntityMaps` (SCM id ↔ QBO id, state) | Reuse; spec SyncLogs = these two (D-12) |
| `ExternalEntityMappings` | `integration.EntityMaps` + `MatchCandidates` (Link / CreateNew / Skip) | Reuse; add remote-only import + unlink (D-17) |
| `ExternalAccountMappings` | A42 `finance.ExternalSystemMappings` (CoA account → external code, QUICKBOOKS_ONLINE) — never consumed; Integration has only 4 default account ids in settings | Integration consumes A42 mappings through a Shared seam; QBO account import + auto-map by name/number (D-16) |
| `ExternalTaxMappings` | `integration.TaxCodeMappings` (by code, else by rate) validated against the QBO reference snapshot | Reuse; add "import & suggest" with exempt/zero handling (D-18) |
| `ExternalProviderDefaults`, `ExternalDocumentOverrides` (Class, Department, Terms, PaymentMethod, DepositTo) | None (only partner-level terms) | **New** in Integration (D-15) |
| `SyncControlConfigs` per document type, hold/release | Six `AutoPush*` bools + POS-only hold/release (`PosSales`) | **New** table replacing the bools (migrated), hold generalised (D-14) |
| Internal GL target writes JournalEntries | A42 has `JournalEntries/Lines` (OPENING_BALANCE only), CoA, fiscal periods, `IFiscalPeriodGuard`, 16 accounting defaults; no posting service | **New** internal GL target in Accounting (D-8, D-9) |
| `TenantFinanceConfigs` (InternalAccountingEnabled, AutoPostToGL…) | None | **New** in Accounting (D-8) |
| QBO Invoice / Bill / void invoice | Built and live | Unchanged behaviour (D-3) |
| QBO Payment, BillPayment, CreditMemo, SalesReceipt, RefundReceipt, VendorCredit | **Not built** | **New** kinds (D-19..D-24) |
| SCM sources for those | CustomerPayment (RECEIVED/BOUNCED/REVERSED, allocations to invoices), SupplierPayment (DRAFT/APPROVED/POSTED/CANCELLED/BOUNCED, lines to bills), POS credit note = SalesInvoice CREDIT_NOTE (SCN), CustomerRefund (append-only), supplier CreditNote (CN) + DebitNote (DN) | Sources exist → wire them (D-25) |
| Expense, ManualJournal, InventoryAdjustment, ProductionCost postings | No expense module, no manual journals, inventory/production values exist but are not accounting documents | Enum values only; deferred exactly as FSD §29 (D-28) |
| `journal_entry_push` sync mode | — | Deferred (D-29) |
| Licence check "FINANCE module" | MODULE_FINANCE is always on; GL = FEATURE_GENERAL_LEDGER; QuickBooks = MODULE_INTEGRATION | D-10 |
| Same-transaction GL posting via `TransactionScope` across contexts | Separate DbContexts; Azure SQL; ambient TransactionScope over two contexts escalates/breaks | D-9 (idempotent after-commit posting with its own retry) |

## Decisions (adopted)

- **D-1 Conventions.** Guid `OrganizationId` from the ambient tenant, int `Id` + Guid `Uuid`, uuids in routes and in contracts (`SourceDocumentId` is the source document's **Guid uuid**), ApiResponse envelope, camelCase JSON, enums as strings, guarded idempotent migrations (API start migrates SMSGlobal — every DDL behind IF NOT EXISTS). Tests on LocalDB / in-memory only; **never touch SMSGlobal**.
- **D-2 Where things live.**
  - Contracts (FSD C1/C11/C17): `src/SMS.Shared/Finance/Posting/` — `IFinancialPostingGateway`, `IEntitySyncGateway`, DTOs, enums, and the target seams `IFinancialPostingTarget` / `IEntitySyncTarget`, plus `NullFinancialPostingGateway` / `NullEntitySyncGateway` registered with TryAdd by callers.
  - Gateway router + internal GL target + TenantFinanceConfig + posting log: `SMS.Modules.Accounting` (`Posting/`), tables in schema `finance`.
  - QuickBooks target (external), sync control, provider defaults, mappings, coverage: `SMS.Modules.Integration` (schema `integration`). It implements `IFinancialPostingTarget` and `IEntitySyncTarget`.
  - Callers (FSD C10): Finance, Pos (through Finance's CounterSaleService), Suppliers, Inventory.
- **D-3 No regression of what is live.** Invoice/bill/customer/vendor/item sync, matching, dry-run/live, dependency waits, POS hold, fingerprints, command ledger, void on cancel, flag-for-accountant: behaviour and tests stay green. Existing `IQuickBooksGateway` stays as the QuickBooks target's own API and `IQuickBooksSource` stays as the pull-back used by dependencies/reconciliation/"Push now".
- **D-4 Canonical transaction carries the document.** `FinancialTransaction` = the FSD's identity, partner, Dr/Cr `Lines` (for the GL) **plus** `Metadata.Document`, a provider-neutral document payload (`IAccountingDocument`: the existing Shared `SalesInvoicePayload`, `BillPayload`, and the new `PaymentPayload`, `BillPaymentPayload`, `CreditMemoPayload`, `SalesReceiptPayload`, `RefundReceiptPayload`, `VendorCreditPayload`). External targets build native documents from `Document`; the GL target uses `Lines`. Core modules still know nothing about QuickBooks — the payloads are SCM-shaped (SCM codes, SCM uuids, amounts), not QBO-shaped.
- **D-5 Account roles, not codes.** Modules do not know a tenant's chart. A line carries `AccountRole` = an A42 default key (`DEFAULT_RECEIVABLE`, `DEFAULT_REVENUE`, `DEFAULT_TAX_PAYABLE`…, the 16 keys of `DefaultKeys` in Accounting `GlCodes.cs`, see API-CONTRACT) and optionally `AccountCode` (explicit leaf code wins). Accounting resolves role → account. Lines are in document currency with the transaction `ExchangeRate` (to base); the GL posts base amounts, rounding difference to `DEFAULT_ROUNDING`.
- **D-6 Reuse connection/OAuth/client.** Spec C3 `ExternalSystemConnections`, C8 API client/OAuth/token refresh/disconnect/test = existing Integration code. `/api/finance/connections*` are **not** added; the existing `api/integrations/quickbooks/connect|connection|test` are the routes (API-CONTRACT route map). `EntitySyncEnabled` = the Customer/Vendor/Item sync-control rows (D-14).
- **D-7 App keys stay in the database** (user decision 2026-10-13) — A44-0176/0192 marked ✕ with this reason.
- **D-8 TenantFinanceConfig.** Accounting table `finance.TenantFinanceConfigs` (OrganizationId unique): `InternalAccountingEnabled` (default **false**), `AutoPostToGL` (default **true**: post Posted; false = create Draft journals for review), `DefaultCurrencyCode` (read-only mirror of the org's sale base when null), `FiscalYearStartMonth` (default 1, informational — A42 fiscal years rule). Lazy per-org init. API `GET/PUT api/finance/posting/config` (GL_SETUP_MANAGE to write, GL_VIEW to read). Internal GL only posts when enabled **and** A42 setup status is Complete; otherwise the GL target answers Skipped("GL_NOT_READY").
- **D-9 Atomicity (deviation from §6.4/BR-05).** No cross-context TransactionScope. Callers call the gateway **after** their own commit (as today); every posting is idempotent by key and recorded in `finance.FinancialPostings` (one row per org+key, payload JSON, per-target status). The GL target posts in Accounting's own transaction; on failure (closed period, unmapped role, missing default) the row stays `Pending`/`Failed` with the reason and the Accounting job retries (spec backoff 30s, 2m, 8m, 32m, 2h; 5 tries → `DeadLetter`). A gateway failure never fails the caller (BR-03/BR-04 behaviour). T-21..T-23 are rewritten as: source commit independent of GL; GL journal + posting row atomic; external queue atomic with the map row.
- **D-10 Licensing.** Gateway: MODULE_FINANCE is always on, so `ModuleNotLicensed` is returned only when **no** target module is licensed (neither FEATURE_GENERAL_LEDGER nor MODULE_INTEGRATION). GL target active = FEATURE_GENERAL_LEDGER on + InternalAccountingEnabled + setup Complete. QuickBooks target active = MODULE_INTEGRATION on + a connected connection. No target active → `NoTargetConfigured`.
- **D-11 Outbox policy.** Integration keeps its backoff (1m…24h) and 8 attempts (proven against Intuit throttling); FSD numbers apply to the new Accounting GL queue only. Added: "dead letter" = Failed/NeedsResolution view with **bulk retry** and **skip (dismiss)**; and an **immediate kick** — after a commit that queued work, `BackgroundJob.Enqueue(SyncOutboxJob)` (debounced per connection, `DisableConcurrentExecution` already guards) so a sale reaches QuickBooks in seconds instead of 1–2 minutes (finding from the 2026-10-14 test).
- **D-12 Sync log.** Spec `SyncLogs` = existing `SyncLog` (calls) + `EntityMaps` (document ↔ external id/state). New read API `GET api/finance/posting/log` (Accounting `FinancialPostings`, both targets' status per document) gives the cross-target view the FSD dashboard wants.
- **D-13 Idempotency keys.** `{SourceModule}:{DocumentType}:{SourceDocumentUuid}`; void `…:void`; credit `{SourceModule}:{CreditDocType}:{CreditUuid}`. A **re-post of the same key with a changed payload** (an edited approved bill, a re-issued POS sale) is an update, not a duplicate: Accounting compares a payload fingerprint; same fingerprint → `DuplicateSkipped`; different → GL reverses the old journal and posts the new one, external target upserts (existing fingerprint logic). Entity sync keys are ignored for dedupe because EntityMaps fingerprints already dedupe; `EntitySyncRequest.IdempotencyKey` is accepted and logged only.
- **D-14 Sync control (C18).** New `integration.SyncControlConfigs` (ConnectionId, DocumentType string, IsEnabled, HoldWhenDisabled, DisplayLabel, ModifiedBy/At), unique (ConnectionId, DocumentType). Document types: Customer, Vendor, Item, SalesInvoice, CreditNote, Payment, POSSale, POSReturn, PurchaseBill, BillPayment, VendorCredit. Migration **copies today's AutoPush\* values** for existing connections (AutoPushPosSales → POSSale/POSReturn enabled=value, hold=true); the bool columns stay (read by nothing; dropped in a later addendum). New connections are seeded with the FSD §22.3 defaults **except PurchaseBill/BillPayment/VendorCredit**, which follow the spec (off, no hold). No row = enabled (BR-29). Hold = store payload, nothing queued, map shows `HELD` (generalised from PosSales); enabling releases all held (BR-31) through the same release path as today's POS release. Settings UI moves the six switches into a Sync control table.
- **D-15 Provider defaults (C6).** `integration.ProviderDefaults` (ConnectionId, SettingKey, SettingValue, Description) + `integration.DocumentOverrides` (ConnectionId, DocumentType, SettingKey, SettingValue). Keys: `ClassRef`, `DepartmentRef` (QBO "Location"), `SalesTermRef`, `PaymentMethodRef.{CASH|CHEQUE|BANK_TRANSFER|CARD|ONLINE|MOBILE}`, `DepositToAccountRef.{method}` (customer payments), `BankAccountRef.{method}` (bill payments), `RefundFromAccountRef`. Values validated against the reference snapshot, which gains Classes, Departments, PaymentMethods (and Class/Location tracking preferences; a Class/Department value is only sent when the company tracks it). Overrides win over defaults (BR-12). Enrichment happens in the QuickBooks target's object builder — never in callers.
- **D-16 Account mapping (C5).** SCM CoA → QBO account = A42 `ExternalSystemMappings` (SystemType QUICKBOOKS_ONLINE, ExternalAccountCode = QBO Account Id). New Shared seam `IExternalAccountMapReader` (Accounting implements) so Integration can read it. New Integration endpoints: import QBO accounts (from the reference snapshot) and **auto-map by AcctNum, then exact name, then normalised name** into A42 mappings (writes through `IExternalMappingService`, `IsAutoMapped`-style note in its sync status). Use in QBO documents: the four settings accounts stay authoritative; when a settings account is empty, the A42 mapping of the matching default (`DEFAULT_REVENUE` → income, `DEFAULT_COGS`/`DEFAULT_PURCHASE` → expense, `DEFAULT_DISCOUNT_GIVEN` → discount, discount, freight) is used; payment deposit/bank accounts fall back to the mappings of `DEFAULT_CASH`/`DEFAULT_BANK`. GL account mappings are not required for document push (QBO derives its ledger from items) — coverage reports them as information, not blocking.
- **D-17 Entity mapping (C5/C13).** Import QBO Customers/Vendors/Items as **remote-only candidates** (new `integration.RemoteEntities`: ConnectionId, Kind, RemoteId, Name, extra keys, ImportedAt; unique ConnectionId+Kind+RemoteId) — never creates SCM entities (BR-18). Link = existing `linkremote` + Matching Link; **Unlink** new (`POST sync/items/{id}/unlink`: clears RemoteId/SyncToken, state NotSynced, record kept in QBO, audit) — refused while documents synced against it are open (409 with count). Coverage endpoint per kind: SCM active total vs mapped (RemoteId) vs skipped vs pending.
- **D-18 Tax mapping (C15).** Keep `TaxCodeMappings`. New `POST tax-mappings/import`: for each SCM tax code (and rate seen) suggest a QBO code by rate + name; exempt/0 % → QBO `NON` when the company has it (US), else a 0 % code whose name contains "exempt"/"zero" matching the SCM code's name; only **unambiguous** suggestions are saved, the rest returned for the admin. Validator already reports all unmapped codes at once.
- **D-19 Customer payment → QBO Payment.** Source: `CustomerPayment` RECEIVED (back office and POS tenders not covered by D-22). Lines = allocations → `LinkedTxn` to the invoices' QBO ids (dependency: customer + every allocated invoice synced); unallocated amount = unapplied credit in QBO. DepositTo/PaymentMethod from D-15. STORE_CREDIT and EXCHANGE methods move no cash → not sent (logged). BOUNCED/REVERSED → **void** the QBO Payment. Re-allocation = update.
- **D-20 Supplier payment → QBO BillPayment.** Source: `SupplierPayment` POSTED (STANDARD lines → LinkedTxn bills; ADVANCE_PAYMENT → BillPayment without links (vendor credit balance); PURCHASE_RETURN_SETTLEMENT → not sent, settled by the VendorCredit of D-24). PayType Check (bank) or CreditCard by method; BankAccountRef from D-15. CANCELLED/BOUNCED after POSTED → void. Legacy single-invoice `Payment` entity: not sent (residual).
- **D-21 POS sale → SalesReceipt when fully paid at the till.** If every tender of a POS sale is cash-like (not ON_ACCOUNT/STORE_CREDIT/EXCHANGE): one QBO **SalesReceipt** (no AR, deposit account by tender method — one receipt per sale, deposit account of the largest tender, others noted). Otherwise the POS invoice goes as today (Invoice) and its tenders as Payments (D-19). Fixes why POS was held ("payments are not synced"); the POSSale sync-control default stays off + hold.
- **D-22 POS void.** SalesReceipt → void; Invoice → void (existing) and its Payments void.
- **D-23 Credit note / POS return.** SCN credit note (`SalesInvoice` status CREDIT_NOTE): when its refund tenders are all cash-like → **RefundReceipt** (credit + cash out in one, RefundFromAccount from D-15); otherwise → **CreditMemo** (ON_ACCOUNT = applied to the original invoice through a zero-sum Payment linking both, so the invoice balance in QBO matches SCM; STORE_CREDIT = unapplied credit); mixed tenders → CreditMemo + the cash part as RefundReceipt is **not** done — mixed → CreditMemo and the map is flagged for the accountant (`FlagForAccountant`, reason lists the cash refunds). Back-office customer credit notes don't exist in SCM (residual R-3).
- **D-24 Supplier credit/debit note → QBO VendorCredit.** Supplier `CreditNote` (CN) on create; supplier `DebitNote` (DN) when ISSUED (DRAFT not sent; DISPUTED/WRITTEN_OFF after sending → flag for accountant). Lines by account (D-16 expense default) — notes carry no item lines in SCM. Application to bills: not linked in QBO v1 (QBO applies vendor credits in BillPayment; residual R-4).
- **D-25 Callers (C10).** Finance, Suppliers, Inventory publishers switch from `IQuickBooksGateway` to `IFinancialPostingGateway` (documents) and `IEntitySyncGateway` (partners/items), **after commit, never throwing**, with the same trigger points as today (invoice issue/cancel, bill approve/edit/reverse, partner/variant save/import, CRM convert) plus new: customer payment record/allocate/bounce/reverse, supplier payment post/cancel/bounce, credit note create, debit note issue/status, POS sale/return/void. `IQuickBooksSource` implementations stay and also serve the new kinds (pull-back by uuid / changed-since).
- **D-26 Void / credit (C17).** `VoidAsync(VoidRequest)` → GL reversing journal + external void (invoice, payment, bill payment, sales receipt, refund receipt). PurchaseBill reversal = flag-only (existing FlagForAccountant, BR-26) + GL reversal. `IssueCreditAsync` = `PostAsync` of the credit document type (thin helper). Never synced externally → external no-op, GL reversal still made (BR-25/T-60).
- **D-27 Pre-sync validation & coverage (C16).** Existing `PayloadValidator` + dependency waits are the pre-sync validator (all errors at once — QA verifies); new `GET api/integrations/quickbooks/coverage` (customers, vendors, items, tax codes, accounts, provider-defaults completeness, readiness + blocking issues) and `POST api/integrations/quickbooks/validation/check` (dry validation of one stored map or a supplied posting, no Intuit call). Preflight gains the new checks (deposit accounts for enabled Payment, bank accounts for BillPayment, class/department when tracking on).
- **D-28 Deferred (as FSD §29).** Expense sync, inventory/production postings, manual journal sync, reconciliation engine, financial reporting dashboard, Xero/Tally/ERPNext adapters, QBO Desktop, supplier reversal automation, inbound entity creation.
- **D-29 journal_entry_push mode deferred.** Document push only.
- **D-30 Frontend.** Angular, SMS Flow kit. QuickBooks page gains tabs: **Sync control** (replaces the six switches, held counts, release confirm), **Defaults** (class/department/terms/payment methods/deposit & bank accounts, per document type overrides), **Accounts** (A42 CoA ↔ QBO accounts, import, auto-map, unmapped warning), **Coverage** (readiness + per-kind bars + blocking issues + links); Sync tab gains dead-letter filter, bulk retry, skip, unlink, new kinds; Match tab gains "Import from QuickBooks" (remote-only list + link). Finance › General Ledger gains **Posting** page (`/portal/pages/finance/gl/posting`): Internal GL toggles (D-8), posting log (D-12) with target statuses and retry. SCM pages: QuickBooks badge on customer payment, supplier payment, credit/debit note detail pages that exist (no new note pages — R-3).
- **D-31 Permissions.** Existing `INTEGRATION_VIEW` / `INTEGRATION_MANAGE` for everything under `api/integrations/quickbooks`; GL_VIEW / GL_SETUP_MANAGE for `api/finance/posting/*`. No new permission codes.
- **D-33 Retry button on SCM screens (user request 2026-10-16).**
  - **Where:** wherever a document can go to QuickBooks and the org is connected: sales invoice, supplier invoice, customer payment, supplier payment, partner, product, plus any detail page FE finds for POS orders or supplier credit/debit notes.
  - **When it shows:** the QuickBooks badge shows a visible inline button (not only the caret menu) when the record is:
    - NotSynced, including "never reached QuickBooks" (no map) → "Send to QuickBooks";
    - Failed, Blocked or NeedsResolution → "Retry".
  - **What a click does:**
    1. If an A44 posting exists for the document (`GET api/finance/posting/by-source/{uuid}`), retry its failed targets the user may act on (D-41 per-target rule).
    2. Then ask the QuickBooks source to push it (`POST sync/push`), which also covers documents that never reached the gateway.
    3. The badge refreshes, and a toast gives the outcome: queued / skipped in matching / no permission / the error message.
  - **Permissions:** the button needs INTEGRATION_MANAGE. The GL retry part needs GL_SETUP_MANAGE; without it, that part is quietly not attempted.
- **D-34 QuickBooks logs page (user request 2026-10-16).**
  - **Where:** new tab **Logs** on the QuickBooks page, plus a menu entry "QuickBooks Logs" under Integrations. It lists every call to QuickBooks across all records, from `integration.SyncLog`.
  - **Filters:** failed only (the default), kind, operation, outcome, date range, and search over document number, name, error and Intuit tid.
  - **Columns:** time, document (links to the SCM page and to the Sync item), operation, outcome, error code + message, duration, Intuit tid.
  - **Row detail:** the redacted request/response JSON.
  - **Row actions:** Retry (the D-33 action), and Open in QuickBooks when synced.
  - **API:** `GET api/integrations/quickbooks/sync/log` (paged) and `GET …/sync/log/{id}`, both INTEGRATION_VIEW. Secrets are never returned: the Redactor is applied at write time.
- **D-35 Refund account per payment method (user request 2026-10-16).** The Defaults tab edits `RefundFromAccountRef.{METHOD}` for each SCM method (CASH, CHEQUE, BANK_TRANSFER, CARD, ONLINE, MOBILE) as well as the base `RefundFromAccountRef`, the same way it already handles deposit and bank accounts.
  - **Lookup order:** method-specific key → base key → the method's deposit account.
  - **Owners:** QBO's builder applies this lookup (CTRL already defines the keys); FE adds the editor.
- **D-36 Journals in the posting detail (user request 2026-10-16, closes R-FE-1).** The Posting page detail shows the posting's GL journals from `GET api/finance/posting/log/{uuid}` → `journals`:
  - each journal's number, type, date, status, reversal links and totals;
  - its lines (account, memo, debit, credit, balanced total);
  - an explicit note when there is no GL journal.
  Frontend only.
- **D-32 Testing.** Unit tests per module; LocalDB HTTP E2E in `tests/SMS.Integration.Tests/FinancialPosting/` (one class at a time, short artifacts path junction under %TEMP%); FakeQuickBooks extended for the new objects; final **live** sandbox run of the full flow (user requires live, not dry run) by QA against local API + SMS_Dev (`Data__mainOrg` override) — never SMSGlobal.

## Agent decision ranges
CORE D-40..D-49 · QBO D-50..D-59 · CTRL D-60..D-69 · SRC D-70..D-79 · FE D-80..D-84 · QA D-85..D-89. Append below, one line each.

## Decisions by agents

- **D-70 (SRC) Two routes, one builder.**
  - Finance's after-commit publishers send a `FinancialTransaction` to `IFinancialPostingGateway`. This covers sales invoice issue and cancel, bill approve, edit and reverse, customer payment record, allocate and bounce, supplier payment post, bounce and cancel, credit note create, debit note create and write-off, and POS sale, return and void.
  - When the gateway is `NullFinancialPostingGateway`, or a hand-built rig passes none, the same payload goes straight to `IQuickBooksGateway`, as before A44. A host without Accounting therefore loses nothing.
  - Every `IQuickBooksSource` (`PushAsync` / `PushAllAsync`: "Push now", dependencies, release, reconciliation) always calls `IQuickBooksGateway` directly (D-3, REV-3), tenant-scoped explicitly (security audit Q1).
  - Code is in `src/SMS.Modules.Finance/Integration/Posting/` (`PostingSupport`).
  - Publishers never throw: each logs a warning with the document's uuid.
  - `Metadata.Document` for invoices and bills is exactly `SalesInvoicePayloadFactory.Build` / `BillPayloadFactory.Build`, so fingerprints are unchanged.
  - Credit documents (CreditNote, RefundReceipt, VendorCredit) go through `IssueCreditAsync`.
- **D-71 (SRC) Document types, keys and properties.**
  - **Document types:**
    - SalesInvoice (back office: module `finance`; POS: `pos`);
    - SalesReceipt (`pos`);
    - CreditNote = credit memo (`pos`);
    - RefundReceipt (`pos`);
    - Payment (back office `finance`; POS tender `pos`);
    - PurchaseBill, BillPayment, VendorCredit (`finance`).
  - POSSale/POSReturn are **not** used as FinDocTypes: they are sync-control types only. QBO derives them from `SourceType:"POS"`.
  - **QuickBooks kind** for each FinDocType (`PostingSupport.KindOf`): SalesInvoice→SalesInvoice, SalesReceipt→SalesReceipt, CreditNote→CreditMemo, RefundReceipt→RefundReceipt, Payment→Payment, PurchaseBill→Bill, BillPayment→BillPayment, VendorCredit→VendorCredit.
  - **Voids** carry the type the document was posted under (REV-9).
  - **`Metadata.Properties` keys:**
    - `FlagForAccountant` (text): send, then flag. Used on a mixed POS refund.
    - `AppliedAmount` (invariant `0.00`): how much of a credit memo is applied to its original invoice. It can be less than the memo total.
  - **Bill reversal:** `ReverseAsync(PurchaseBill)`, with `Reason` = the existing accountant note. A written-off debit note uses `ReverseAsync(VendorCredit)`.
- **D-72 (SRC) GL line conventions.**
  - Amounts are rounded to the cent (AwayFromZero); zero lines are dropped and negative amounts go to the other side. `GlLines.Build` puts a residue of at most 0.05 on DEFAULT_ROUNDING and refuses anything larger.
  - Revenue = grand total − tax + header discount, so the lines balance by construction.
  - Tax is split by tax code, in proportion to each code's lines (POS line tax amounts where stored; else qty × price × (1 − disc%) × tax%), with the remainder on the heaviest code.
  - Header discount → DEFAULT_DISCOUNT_GIVEN. This is POS discounts, plus any manual excess on back-office invoices.
  - Money role: CASH → DEFAULT_CASH; every other method → DEFAULT_BANK.
  - Realized exchange difference (SCM base, + gain / − loss) → DEFAULT_EXCHANGE_GAIN_LOSS as `difference ÷ rate` in document currency. The receivable / payable line takes the rest.
  - Bills are built from the bill payload's lines plus header tax (DEFAULT_TAX_RECEIVABLE with the bill's tax code), not from `Invoice.TotalAmount`. A note deducted from the invoice is posted as its own vendor credit and must not be counted twice.
  - POS return GL: refunds in money add Dr RECEIVABLE / Cr CASH-or-BANK, so the GL equals the customer ledger whatever goes to QuickBooks.
  - Store credit and exchange refunds stay on the receivable: there is no liability role.
- **D-73 (SRC) POS shape rules (D-21..D-23, REV-8, REV-11).**
  - **Sale:** a sales receipt if the POS tender payments (SourceUuid set) allocated to the sale are all cash-like (CASH, CHEQUE, BANK_TRANSFER, CARD, ONLINE, MOBILE) and add up to the grand total. Allocations count whatever the payment's status: a bounced cheque keeps its allocation, so the type never flips. Anything else is an invoice, with each cash-like tender as a Payment. STORE_CREDIT and EXCHANGE tenders are never posted.
  - **Return:** a refund receipt if every refund (CustomerRefund) is cash-like and covers the total. With no money refunded, it is a credit memo, applied to the original when the sale was an invoice and `CreditedAmount > 0`. Mixed → credit memo plus `FlagForAccountant`, listing the money refunds.
  - The original is linked (`OriginalInvoiceExternalId`) only when it went as an invoice.
  - **Void:** the credit note CounterSaleService writes with source number `"VOID …"` (`PosDocuments.VoidPrefix`). The type is read before the reversals; the void SCN itself is never posted.
  - **Pre-A44 POS sales** that the QuickBooks gateway already knows as SalesInvoice (`GetStatusAsync`, held or sent) stay invoices. The SalesReceipt source skips them; the SalesInvoice source keeps serving them.
    - REV-20: "knows" means a map that really carries an invoice (`PosDocuments.IsLegacyInvoice`): a RemoteId, any State past NotSynced, or the hold marker POS_SALES_HELD / HELD.
    - An empty NotSynced placeholder, as `POST sync/push` creates for any id, does **not** count. Pushing a receipt sale as SalesInvoice therefore sends no Invoice, and the receipt still goes.
  - **What the SalesInvoice source now skips:** POS credit notes and new receipt-shaped POS sales. It voids voided POS sales.
  - **Bounced till tender of a receipt sale (REV-15):** on the bounce itself, never on pull-back.
    - GL: a Payment posting `pos:Payment:{tender}` — Dr RECEIVABLE / Cr BANK, no Document, so external targets skip it.
    - QuickBooks: `FlagForAccountantAsync(SalesReceipt)` once the receipt is synced.
- **D-74 (SRC) Opening balances (A40) are never posted.** Imported opening invoices and bills are skipped by both routes, as the QuickBooks path did before. Payment and BillPayment applications to them are left out of the payload's applications (that amount is unapplied in QuickBooks) and listed in the private note ("Also settles opening balance …"). The GL still credits the receivable / debits the payable in full.
- **D-75 (SRC) Supplier payments and notes.**
  - **Supplier payments:** a BillPayment is posted at POSTED; BOUNCED, or CANCELLED with `PostedAt`, → void. DRAFT and APPROVED post nothing.
  - **Method mapping:** BANK_TRANSFER→BANK_TRANSFER, ONLINE_WIRE→ONLINE, CHEQUE→CHEQUE, CASH→CASH, anything else → BANK_TRANSFER. The reference is the cheque number.
  - **ADVANCE_PAYMENT:** no applications. **PURCHASE_RETURN_SETTLEMENT:** never posted.
  - **Notes** (CN on create; DN on create, since a DN is created ISSUED):
    - each is one "Other" line with no tax, and DocNumber = the supplier's CN number / the DN number;
    - currency and rate come from the invoice the note names; with no invoice, the organization's purchase base.
  - **Debit note statuses:**
    - WRITTEN_OFF → `ReverseAsync` (GL reversal + QuickBooks flag; the reason says "adjust it in QuickBooks by hand");
    - DISPUTED posts nothing — a deviation from D-24's "flag": a dispute moves nothing in SCM's supplier ledger and may still end SETTLED;
    - the pull-back sends ISSUED, ACKNOWLEDGED, DISPUTED and SETTLED notes only.
  - **QA-5:** `POST api/supplier-payments` validates the supplier before anything else uses it:
    - an empty `SupplierId` → 400 "SupplierId is required.";
    - a missing or blank `SupplierName` → 400 "SupplierName is required.";
    - a name over 200 characters → 400.
    The name is stored trimmed. It used to reach the insert and fail as a 500 (pre-existing).
  - **QA-6:** new `POST api/customer-payments/{uuid}/bounce` with body `{ "reason"?: string ≤ 200 }`. This makes D-19 "bounced → void" reachable.
    - Same gate and permission as recording a payment: MODULE_FINANCE, CUSTOMER_PAYMENT_RECORD.
    - Calls `CustomerPaymentService.BounceAsync`, whose own rules answer: not a cheque → 400, not RECEIVED → 409, unknown or another organization's payment → 404.
    - Success → 200 with `CustomerPaymentBounced`.
    - After the commit, the existing hook sends `VoidAsync(Payment)`, or for a till tender of a sales receipt the REV-15 posting.
- **D-76 (SRC-MD) Master-data publishers on IEntitySyncGateway.** `PartnerQuickBooksPublisher` (Suppliers) and `VariantQuickBooksPublisher` (Inventory) keep their names, public methods and every caller/trigger unchanged (BusinessPartnerService, CustomerService, SuppliersService, CrmAccountGateway, Supplier/CustomerImportTarget; InventoryService product/variant save, variant added, delete with before-delete snapshot, ProductImportTarget), but send through `IEntitySyncGateway.PushAsync` after the commit instead of `IQuickBooksGateway`. Payloads are built by the sources' new `PartnerQuickBooksSource.BuildRoles` / `VariantQuickBooksSource.Build*Async` — the same code the pull-back `PushAsync`/`PushAllAsync` uses (those stay on `IQuickBooksGateway`, tenant-scoped, D-3) — so `Document` is byte-identical JSON to before (tests prove it per trigger) and fingerprints do not change. Both modules register `NullEntitySyncGateway` with TryAdd; Accounting's `RemoveAll`+`AddScoped` replaces it. **REV-16 fallback (as Finance D-70):** while the gateway is still the `NullEntitySyncGateway` (host without Accounting's posting layer), each built payload goes straight to `IQuickBooksGateway` through the source (`SendBuiltAsync` / `SendPayloadAsync`), exactly as before A44 — no edit is lost.
- **D-77 (SRC-MD) Request fields.** One request per role (partner customer → `Customer`, payee → `Vendor`, customer first) and per variant (`Product`). `Action`: inactive payload (deleted, blacklisted, rejected, INACTIVE, product/variant soft-deleted) → `Deactivate`; hard-deleted variant (sent from the before-delete snapshot) → `Delete`; everything else `Update` — `Create` is never sent because targets upsert by uuid and the publisher cannot tell a first save from an edit without changing every caller. `EntityId` = partner/variant uuid; `IdempotencyKey` = `{EntityType}:{Action}:{EntityId}:{UtcNow.Ticks}` (one tick value per publish call, logged only, D-13); `Snapshot` filled from the payload (partner: name, email, phone, active, company, tax id, currency; item: "Name - Variant", SKU, sales price, purchase cost, active); `InitiatedBy` = "system".
- **D-78 (SRC-MD) Imports stay per row.** Import targets keep calling the publisher once per imported row / product after the batch commit (unchanged triggers), i.e. `PushAsync` per request, not `PushBatchAsync`: same behaviour and logging as the editors, and one failing row never stops the next. `PushBatchAsync` is not used by these modules.
- **D-79 (SRC-MD) Never throw, log by outcome.** The publisher catches around the read/build (one warning with the record id, as before) and around each `PushAsync` (warning with the id; a hard-delete failure says "retire that item there by hand"), so one role/variant failing never stops the next. Results: Success, NoTargetConfigured, ModuleNotLicensed, DuplicateSkipped, Held → Debug; ValidationFailed → Information (as the gateway's Invalid was); any other unsuccessful outcome → Warning.
- **D-50 (QBO) Sync control in the gateway; hold generalised.**
  - Every upsert asks `ISyncControlReader` about `SyncControlDocumentTypes.For(kind, sourceType)`: Skip = Disabled (was "auto-push off"), Hold = stored, nothing queued, hold marker on the map, Process = as before.
  - Voids go unless the type is Skip.
  - Until CTRL's reader is registered, `LegacySyncControlReader` (TryAdd) reads the six AutoPush* bools as before. Payment/CreditNote follow sales invoices; BillPayment/VendorCredit follow bills.
  - Marker: `POS_SALES_HELD` for POSSale/POSReturn (compatible with existing data and the UI), `HELD` for other types. Marker + kind identify the type (`Core/Sync/SyncHolds.cs`).
  - `IHeldRecordReleaser` = `HeldRecordReleaser`:
    - release re-asks the SCM source by kind, and re-admits the stored payload for other callers or kinds no source owns;
    - a held map that the source answers as another kind of the same type (pre-A44 POS invoice → SalesReceipt) is closed — Voided if never sent, flagged if sent (REV-11);
    - HoldQueued holds queued-but-unsent upserts.
  - `PosSales.*` keep their signatures and delegate.
  - D-11 kick: `IOutboxKick` (`HangfireOutboxKick`, singleton) schedules `SyncOutboxJob` 2 s after the first enqueue of a connection; requests within 3 s ride on it. No-op without Hangfire; the 1-minute recurring job stays.
- **D-51 (QBO) Void per kind.**
  - QBO void: Invoice, Payment, SalesReceipt. These are the only kinds the SDK's `Void` supports — IL-checked: Payment/SalesReceipt via `?operation=update&include=void`, Invoice via `operation=void`.
  - BillPayment and RefundReceipt **delete** (`?operation=delete`; QBO's audit log keeps it), because the SDK cannot void them.
  - CreditMemo, VendorCredit and Bill are never undone by the integration (a credit may be applied, a bill paid). `IQuickBooksGateway.VoidAsync` flags them for the accountant.
  - A document never sent is voided locally.
- **D-52 (QBO) Payment identity.** QBO payments have no DocNumber: SCM's number goes as `PaymentRefNum`. The unknown-outcome lookup queries the payments of the TxnDate and filters by PaymentRefNum (+ customer). The other new kinds are found by DocNumber (+ customer/vendor filter).
- **D-53 (QBO) Credit memo ApplyToOriginal.** On **create only**, the provider creates the CreditMemo, then a zero-total Payment with LinkedTxn lines Invoice + CreditMemo for the memo total. If the application fails, the create still succeeds and `RemoteRecord.Note` becomes the map's warning ("apply by hand"). See R-QBO-1.
- **D-54 (QBO) Walk-in customer and targets.**
  - A SalesReceipt / CreditMemo / RefundReceipt without a customer goes under the map `Customer "POS-WALK-IN"` ("POS Walk-in").
  - With a `PosDefaultCustomerRef` default, that map is linked (Adopted) to that QBO customer and never updated. Otherwise it is created once (a duplicate name is adopted).
  - `QuickBooksPostingTarget` maps the result as follows:
    - Synced → Posted; Pending/Waiting → Queued; held → Held;
    - Disabled/NotConnected → Skipped; Invalid → Failed ("VALIDATION: …").
  - Void/Reverse find the kind from the existing map (POSSale: SalesReceipt, then SalesInvoice; POSReturn: RefundReceipt, then CreditMemo). Never synced → Skipped `NEVER_SYNCED`.
  - Reverse = flag only.
  - `QuickBooksEntitySyncTarget`: Deactivate/Delete send the record inactive.
  - Active = MODULE_INTEGRATION (via `IModuleGate` when registered) + usable connection.
- **D-55 (QBO) Provider defaults use.**
  - The builder takes `IProviderDefaultsReader` values as given. **CTRL's reader returns ClassRef/DepartmentRef only when the company tracks classes / locations.**
  - Class goes on the header of sales documents and on the lines of bills and vendor credits. Department goes on the header.
  - SalesTermRef goes on invoices.
  - D-16 fallbacks when a method key is empty: deposit/bank/refund account → the A42 mapping of DEFAULT_CASH (CASH) or DEFAULT_BANK. Vendor-credit lines → DefaultExpenseAccountId, else DEFAULT_PURCHASE.
  - Customer payments without a deposit account go to Undeposited Funds.
  - Bill payments with no account are Blocked `BANK_ACCOUNT_MISSING` (re-validated).
  - PayType: CARD → CreditCard (CCAccountRef), otherwise Check (BankAccountRef).
- **D-55a (QBO) Not-for-sale products in sales documents (QA-1, found live: QBO 2390 on SINV-20261016-0002).** SCM sends a non-saleable product as `IsSold=false`, so its QBO item has no income account. The item payload is not changed (D-3).
  - **Fix-up:** the builder lists the items of an Invoice / SalesReceipt / CreditMemo / RefundReceipt that SCM created as not for sale and that are in QBO (`SaleableItems`). Before the document's write, the executor sends a sparse Item update with only `IncomeAccountRef` (`RemoteItemIncomeAccountFix`).
  - **The account:** settings DefaultIncomeAccountId, else the A42 DEFAULT_REVENUE mapping.
  - **After a fix-up:** the item map's warning becomes "Sold on {doc}; income account added in QuickBooks.", which also marks the item fixed. The item's fingerprint is unchanged, so the item is not re-sent.
  - **No account at all:** the document is Blocked `ITEM_NOT_SALEABLE` with "Product X is not set up for sale in QuickBooks: set a default income account or mark the product saleable." The code is re-validatable.
  - A failed fix-up call fails the document like any provider call (retry / report).
  - In a dry run the fix-up only adds a warning.
  - See R-QBO-4.
- **D-56 (QBO-SDK) Void wire format, proven on the loopback.** SDK 14.7.1.6 `DataService.Void`: Invoice → `?operation=void`; Payment and SalesReceipt → `?operation=update&include=void`; every other entity throws `IdsException` "Only entitites of type SalesReceipt and Payment are supported" **before any request** (pinned by `Sdk_void_url_per_entity` / `Sdk_void_refuses_other_entities_before_any_request`). The SDK sends the object as given, so the provider now sets `sparse = true` on the Payment/SalesReceipt void probe (body = Id + SyncToken + sparse, as Intuit's void request is documented); without it the call is a full update missing CustomerRef/Line. BillPayment/RefundReceipt delete reaches `?operation=delete` with Id + SyncToken (D-51 confirmed). CreditMemo/VendorCredit: QuickBooks' REST API can delete them, the SDK cannot void them; kept **refused without a call** (D-51 — a credit may already be applied).
- **D-57 (QBO-SDK) No server-side idempotency.** The SDK adds a fresh random `requestid` to every call, so a retried create is never de-duplicated by Intuit; the unknown-outcome rule (Transient → Find by DocNumber / Payment by TxnDate+PaymentRefNum before any retry) is the only protection and is covered for all six kinds. The PaymentRefNum match is trimmed and case-insensitive; the day query is capped at 1000 payments (MAXRESULTS) — a busier day can miss the match (residual R-QBO-SDK-1).
- **D-58 (QBO-SDK) Class on sales lines.** QuickBooks reads a sales form's class from the **header** (`ClassRef`) when the company tracks one class per transaction, and from each `SalesItemLineDetail.ClassRef` when it tracks classes per line (the header one is then not used). `RemoteSalesLine.ClassId` (optional, null = not sent → invoice output unchanged, existing tests green) and its mapping on the line were added so the builder can fill the lines when the per-line preference is on; today the builder sets the header only (QBO's call, see R-QBO-SDK-2). Bills and vendor credits already carry class on the lines.
- **D-59 (QBO-SDK) Mapper files and shapes.** Mappers split per entity family with QBO's class/method names kept: `QboSalesDocumentMapper.cs` (SalesReceipt, CreditMemo, RefundReceipt — reuse `QboInvoiceMapper.SalesLine/DiscountLine`, byte-identical lines proven), `QboPaymentMapper.cs` (Payment, CreditApplication, BillPayment), `QboVendorCreditMapper.cs` (reuses `QboBillMapper.ToLine`, byte-identical lines proven); A44 provider operations in `QuickBooksAccountingProvider.Documents.cs` (class made `partial`). Wire facts pinned: Payment lines are `{Amount, LinkedTxn[{TxnId, TxnType:"Invoice"}]}` with no DetailType and the whole receipt in `TotalAmt`; BillPayment's xs:choice serializes as `CheckPayment.BankAccountRef` / `CreditCardPayment.CCAccountRef` next to `PayType`; RefundReceipt's refund account is `DepositToAccountRef` (QBO refuses it missing: 2020 → Refused with field DepositToAccountRef); the credit application applies min(QuickBooks' memo `TotalAmt`, SCM's `ApplyAmount`), the QBO total when ApplyAmount is 0 (QBO's change, partial applications) and a refused (6000 over-balance) or lost application keeps the memo and sets `RemoteRecord.Note`. Error translation needed no new codes: 6000 business validation → Refused (final), 610 on create → Refused, 6140 → Duplicate, 5010 on void → StaleObject, 610 on void/delete → NotFound.
- **D-40 (CORE) Posting API gate.** `PostingController` (`api/finance/posting/*`) does not derive from `AccountingControllerBase`: it carries `[RequiresFeature(FEATURE_GENERAL_LEDGER, MODULE_INTEGRATION)]` (any-of), so a QuickBooks-only organization sees its posting log. Licence inside the gateway and job uses `IModuleGate` (built over the tenant snapshot, works in jobs; no gate registered = licensed).
- **D-41 (CORE) Posting permissions.** Config GET `GL_VIEW|GL_ACCOUNT_MANAGE|GL_SETUP_MANAGE`, PUT `GL_SETUP_MANAGE`. Log/detail/summary also accept `INTEGRATION_VIEW|INTEGRATION_MANAGE` (read rule unchanged). Retry/skip enter with `GL_SETUP_MANAGE|INTEGRATION_MANAGE` but act **per target** (REV-18, segregation of duties): `internal_gl` needs `GL_SETUP_MANAGE`, every external target (`quickbooks`) needs `INTEGRATION_MANAGE` — neither code touches the other side. A named target the caller may not touch, or an open set with none touchable → 403 `You may not retry or skip any target of this posting.`; without `targetCode` only the touchable open targets are acted on and the message names the rest (`Skipped: quickbooks. Not skipped (no permission): internal_gl.`). No new codes (D-31 kept).
- **D-42 (CORE) Posting log storage.** `finance.FinancialPostings` (unique Org+IdempotencyKey; Kind Post/Void/Reverse; TransactionJson = the DTO without `Metadata.Document`; DocumentJson + `DocumentPayloadType` = the concrete Shared payload's class name, read back only through an allowlist of non-abstract `IAccountingDocument` classes of SMS.Shared) + `finance.FinancialPostingTargets` (Pending/Posted/Queued/Held/Failed/DeadLetter/Skipped, Attempts, NextAttemptAt, LastError, InternalId, ExternalId, PostedFingerprint). Fingerprint = SHA-256 of transaction JSON + payload type + document JSON. Void/Reverse rows are keyed `…:void` (both kinds; one per document), fingerprint = key only (the reason is not part of it). A Pending row is written before any target is called.
- **D-43 (CORE) GL journals for postings.** EntryType = FinDocType upper snake (`JournalEntryTypes.For`), Reference = document number, Description = tx.Description; extra columns SourceModule, SourceDocumentUuid, IdempotencyKey, **ReversesEntryId** (self-FK). "Current" journal of a document = latest with its key, not a reversal and not reversed. Update = reversing journal (same key, dated the new transaction date) + new journal; a Draft is just deleted and replaced. Void/Reverse = reversing journal under `…:void`, dated `VoidDate ?? today`, always Posted (even with AutoPostToGL off); void of a Draft deletes it (Skipped `DRAFT_DISCARDED`); never posted → Skipped `NOT_POSTED` (T-60). Base = amount × rate rounded 2 dp (no rate: as given); diff → DEFAULT_ROUNDING (unassigned → Failed `UNMAPPED_ROLE`). Foreign currency without a rate → Failed `EXCHANGE_RATE_MISSING`. Draft lines set HasTransactions but move no balance. Balances move with one atomic `UPDATE … CurrentBalance = CurrentBalance + @d` per account inside the transaction (REV-1); the posting's internal_gl row is saved in the same transaction (T-22); `sp_getapplock` on the document key serialises post / update / void of one document (REV-5). Error codes in `lastError`: GL_NOT_READY, GL_DISABLED, NO_LINES, NOT_POSTED, DRAFT_DISCARDED, PERIOD_CLOSED, UNMAPPED_ROLE, ACCOUNT_NOT_FOUND, EXCHANGE_RATE_MISSING, VOIDED, VOIDED_BEFORE_POSTING, TARGET_INACTIVE.
- **D-44 (CORE, REV-3) Dedupe only the GL.** Same key + same fingerprint: the internal GL is called only when it has no row yet; **external targets are always called again** (their map fingerprint dedupes, and re-sends are how held release, "Push now" and reconnects work — D-3). `DuplicateSkipped` is returned only when nothing was called. Changed fingerprint → every active target, attempts reset.
- **D-45 (CORE, REV-2/REV-13) Void wins.** A void/reverse marks the original's open (Pending/Failed/DeadLetter) target rows Skipped `VOIDED_BEFORE_POSTING`; a retry or late re-post of a Post whose `…:void` row exists is Skipped `VOIDED` (also re-checked under the GL lock).
- **D-46 (CORE) Account map reader.** `IExternalAccountMapReader` reads active mappings of active leaves (by code; by default key via the default's account). Upsert validates all rows first (leaf, active, own org, code 1–100; else 400 with every problem), null code deletes, unchanged rows are not counted; new rows `SyncDirection = Outbound`; a written row gets SyncStatus Success, LastSyncedAt now and the caller's note in `SyncErrorMessage` (A42 has no note column). System type case-insensitive, unknown → 400.
- **D-47 (CORE, REV-6) Retry policy.** D-9 backoff after failure n = 30 s, 2 min, 8 min, 32 min, 2 h; the 6th failure (initial try + 5 retries) → DeadLetter (not retried automatically; manual retry resets attempts). Job `finance-posting-retry` every minute: due Failed rows + Pending rows untouched for 5 min, claimed by conditional UPDATE (10-min lease), one DI scope per org inside HangfireTenantScope, orgs with neither GL nor Integration skipped. The gateway ignores the caller's CancellationToken (runs after the caller's commit). A target that is no longer active at retry → Skipped `TARGET_INACTIVE`. A throwing target → change tracker cleared, row re-read, Failed recorded (REV-4). Unexpected gateway errors → `PartialFailure` with message (Shared has no "Failed" posting status).
- **D-48 (CORE) EntitySyncOutcome.Failed** added (Shared, additive): the entity router returns it for exceptions / null results; ValidationFailed only for refused requests (no document / id / org). ModuleNotLicensed (MODULE_INTEGRATION off) and NoTargetConfigured are `Success = true`. Several targets: Success = all succeeded, TargetsQueued summed, Outcome = first failure's, else DuplicateSkipped when all were, else Queued.
- **D-49 (CORE) Config + readiness.** GL target active = FEATURE_GENERAL_LEDGER on + setup Complete + InternalAccountingEnabled (D-10 strict); at call time it re-checks and answers Skipped `GL_NOT_READY`/`GL_DISABLED`. Enabling while setup is incomplete is allowed (saved, `glActive` false, `notReadyReason`). GET config never writes (`modifiedAt` null until the first PUT, REV-7); `defaultCurrencyCode` is read-only (stored, else sale base, else current fiscal year's reporting currency). Program.cs calls `UseAccountingPostingGateway()` after the last module so the real IFinancialPostingGateway / IEntitySyncGateway / IExternalAccountMapReader replace any Null* TryAdded by any module.
- **D-60 (CTRL) Sync-control defaults.** A connection's first settings row (`ConnectionAccessor.GetOrCreateSettingsAsync`, i.e. the first connect) seeds 11 rows. Customer, Vendor, Item, SalesInvoice, CreditNote and Payment are on. POSSale and POSReturn are off with hold. PurchaseBill, BillPayment and VendorCredit are off without hold (D-14's exception). The old switches are set to match, so a new company's AutoPushBills is now false.
- **D-61 (CTRL) Migration copy.** Customer, Vendor, Item, SalesInvoice and PurchaseBill take their AutoPush value. CreditNote and Payment start **off, no hold**, like BillPayment and VendorCredit. This is a lead decision on 2026-10-16 for live-data safety: SCM never sent them before, so accountants may have keyed them in QuickBooks by hand. An admin turns them on under Sync control; new connections keep the D-60 seed. (This supersedes REV-10's "follow sales invoices".) Turning a type on later sends no history. The gateway and revalidator skip anything dated before DocumentStartDate for every A44 kind (`SyncPayloads.DocumentDateOf`). Nothing was held while the type was off, and reconciliation asks only for changes since the last complete run (test `Turning_a_type_on_later_…`). "Sync all" exists only for the five original kinds. POSSale and POSReturn = AutoPushPosSales && AutoPushSalesInvoices, with hold = AutoPushSalesInvoices (REV-14). BillPayment and VendorCredit are off. Rows are inserted only where none exist (re-runnable, admin rows kept). A Settings table without AutoPushPosSales copies POS as off. The constants are run as-is by a LocalDB test.
- **D-62 (CTRL) Old switches kept in step (R-1).** A sync-control change writes its AutoPush bool (POSSale → AutoPushPosSales). The old `PUT settings` writes only the switches the request changed onto their rows (pos/invoices → POSSale + POSReturn = pos && invoices), so a save from the old screen never undoes a Sync-control change.
- **D-63 (CTRL) Release confirmation.** `PUT sync-control/{type}` that turns on a type with held records and `confirmRelease:false` → 409, result `{documentType, heldCount}`, nothing changed. With confirmation (or the old settings save, which always released POS) → `IHeldRecordReleaser.ReleaseAsync`. Disabling with hold → `HoldQueuedAsync`. Disabling without hold leaves already-queued entries to go. The hourly reconciliation releases every enabled type. Release failures are logged, never thrown.
- **D-64 (CTRL) 409 with data.** `IntegrationConflictException(message, detail)` → `ApiResponse{success:false, message, result:detail}` with status 409. Used for release confirmation and unlink refusal.
- **D-65 (CTRL) Provider defaults reader.** Defaults overlaid by the document type's overrides. ClassRef/DepartmentRef are returned only when the cached preferences say tracking is on (unknown = left out; the screen warns). Values are validated against the snapshot (account types: deposit Bank/Other Current Asset; bank Bank/Credit Card; refund Bank/OCA/Credit Card). The walk-in customer must be imported or linked. Saving re-validates Blocked records (QBO request).
- **D-66 (CTRL) Reference snapshot.** New optional kinds Classes, Departments and PaymentMethods (a failure is logged, as for TaxRate), plus Preferences tracking flags and Account.AcctNum. Changed only in reference code inside QBO's files: `QuickBooksAccountingProvider.GetReferenceDataAsync`, `QboReferenceMapper`, the `QboQueryBuilder.SelectPage` whitelist, and the reference records in `AccountingProviderContracts`.
- **D-67 (CTRL) No-row fallback.** A type without a row follows `LegacySyncControlReader.Decide` (the old switches; Payment/CreditNote ← sales invoices, BillPayment/VendorCredit ← bills). Enabled (BR-29) applies only when there is no settings row at all. Only hand-made data reaches this, because the migration and the seed give every connection all 11 rows.
- **D-68 (CTRL) Preflight/coverage posting checks.** These look only at payment methods actually used by stored payloads. DEPOSIT_ACCOUNTS: Warn in preflight, warning in coverage (QuickBooks uses Undeposited Funds). BANK_ACCOUNTS: Warn in preflight, **blocking** in coverage. CLASS_DEFAULT / DEPARTMENT_DEFAULT: Warn when tracking is on and no default is set. All are listed only when there is a problem, so the existing preflight list is unchanged. The A42 DEFAULT_CASH/DEFAULT_BANK mapping counts as an account. Empty settings accounts fall back to `IEffectiveAccountsResolver` in preflight.
- **D-69 (CTRL) Import / unlink / dead letter.** Remote import replaces the kind's set (rows no longer in QuickBooks are removed) and never writes anywhere. Link works like matching's Link and asks the source for an SCM record the gateway hasn't seen yet. Unlink is master data only and is refused (409, count + examples) while synced, non-voided documents of the same source system reference the record. A payload that can't be read counts as a reference. Unlink drops CommandClaims and MatchCandidates of the map. Bulk retry takes at most 500 per call and reuses Retry's rules (skipped / flagged / held → listed with the reason). Skip → `DISMISSED`, state NotSynced/Synced/Voided by remote id and last operation, no audit row (the map keeps the user and time in LastError). The remote endpoints are on a separate `RemoteEntityController` so `MatchingController`'s constructor is unchanged.
- **D-69a (CTRL, D-34) Call log API.** `GET sync/log` lists `integration.SyncLog` left-joined to `EntityMaps` for the current organization's connection.
  - The connection filter is explicit, as for the sync items.
  - Seek: the existing `IX_SyncLog_ConnectionId_CreatedAt`, newest first. Every other filter is residual over at most 90 days (retention), so **no new index or migration**. Filtering on ConnectionId also avoids the tenant-filter OR scan.
  - `failedOnly` (default true) = Outcome not in (Succeeded, DryRun).
  - `unresolvedOnly` (default true, QA-7) = the record's current map state is not Synced or Voided.
    - Calls with **no record** (matching scans, remote imports) are simply left out. Chosen over "older than the latest successful scan of the same kind", because those rows carry no kind and their failure was shown on the screen that made the call.
    - `unresolvedOnly=false` gives the full history. Each item still carries the record's current `state`.
  - `kind` uses the map, so calls with no record (matching scans) never match a kind.
  - A date-only `to` covers the whole day. `pageSize` is capped at 200.
  - The list leaves out bodies; `GET sync/log/{id}` returns them as stored.
  - Redaction is applied at write time by all three SyncLog writers: SyncExecutor's two log paths and MatchingService.ReadAllAsync, through `Redactor.Redact` on message, request and response. No other code adds SyncLog rows.
- **D-69b (CTRL, REV-20) "Send to QuickBooks" for a never-seen record.** `POST sync/push` asks every source serving the kind, for every SyncKind. `SyncKindNames` accepts the names and path forms.
  - **Master data** (Customer, Vendor, Item): a missing map is created as a placeholder with `RequestedAt` set, as before, because document dependencies rely on it.
  - **Documents:** no placeholder is ever saved. The map exists only if the source actually sends the document through the gateway; it then gets `RequestedAt`.
  - Ids the source did not send are returned in `notFound`, and nothing is created for them (FE: "Nothing to send for this document"). `requested` counts only the documents that arrived.
  - Records skipped in matching are returned in `skippedInMatching`.
  - Tested for all 11 kinds and for a payment the source then sends.
- **D-80 (FE) Posting page access.** Route `finance/gl/posting` = `permissionGuard(GL_VIEW|GL_ACCOUNT_MANAGE|GL_SETUP_MANAGE|INTEGRATION_VIEW|INTEGRATION_MANAGE)` + `postingFeatureGuard` (FEATURE_GENERAL_LEDGER **or** MODULE_INTEGRATION, mirrors D-40/D-41). Menu: "Posting" in Finance › General Ledger (GL tenants) **and** "Posting Log" under Settings › Integrations (MODULE_INTEGRATION; the GL group is hidden for QuickBooks-only orgs). On the page: GL switches card only with a GL permission (config GET needs one) and only when the GL is licensed (`glFeatureEnabled`/`notReadyReason`); Save = GL_SETUP_MANAGE; retry/skip = GL_SETUP_MANAGE or INTEGRATION_MANAGE. "Not ready" banner from `notReadyReason = SETUP_INCOMPLETE` with a link to the wizard.
- **D-81 (FE) The six switches.** The Mappings tab probes `GET sync-control`: when it answers, the "Send automatically" switches, the POS switch and the "payments are not sent" note are replaced by a pointer to the Sync control tab; on a 404 (older server) they stay and keep working. The settings PUT still sends the AutoPush values it loaded (unchanged; R-1 columns). The probe stays as the fallback instead of deleting the markup, so an FE ahead of its API never loses the switches.
- **D-82 (FE) New tabs open when connected.** Accounts, Defaults, Sync control and Coverage are not setup-checklist steps: they unlock as soon as QuickBooks is connected (setup order unchanged; `unlockedTabs` untouched). Order: Connection, Preflight, Mappings, Accounts, Defaults, Sync control, Initial sync, Match existing, Sync, Coverage, API clients. Coverage "Fix on …" buttons use the issue's `tab` (setup → Preflight, tax → Mappings), else its area/code.
- **D-83 (FE) New kinds on the Sync tab and badges.** Cards for Payment … VendorCredit appear once a kind has records; "Sync all" only for the five backfill kinds. "Dead letter" filter = `deadLetter=true` (no state). Skip on Failed/NeedsResolution rows, Unlink on linked Customer/Vendor/Item rows, both confirmed; the 409 message is shown as the server words it. Held = `NotSynced` + code ending `HELD` (`HELD`, `POS_SALES_HELD`): tagged "Held" (amber) on rows and badges. Badges: customer payment detail (Payment), supplier payment detail (BillPayment); the sales-invoice detail badge takes `alsoKinds` (POS invoice: SalesInvoice + SalesReceipt; credit note: CreditMemo + RefundReceipt) and shows the first kind the gateway holds (one lookup per kind, shared). No badge on the bill's deduction rows (notes have no page; R-3).
- **D-84 (FE) Tolerant reading.** Services unwrap the envelope and accept either the array or the wrapping object where the contract had left it open (sync-control `rows`, remote list paged `data`, summary list/object, counts flat or a `counts` map); the Coverage bars are derived on the page from `entities` + `taxCodes` + `providerDefaults` + `accounts` (accounts shown as information, only when `glAvailable`). Amounts 2 dp (`money()`).

- **D-49a (CORE, D-33) Postings by source document.** `GET api/finance/posting/by-source/{sourceUuid}` returns the caller's organization's postings whose SourceDocumentUuid matches (the document's Post first, then its Void/Reverse), in the log list-item shape, each target with `canAct` = target open (Pending/Failed/DeadLetter) **and** the caller holds that target's manage code (D-41 per-target rule). `canAct` is null in every other response. Read permission as the log (D-41). No posting → 200 with `[]` (never 404). FE retries with `POST log/{uuid}/retry` + `{ targetCode }` for each `canAct` target.
- **FE notes on D-33..D-36, REV-19, REV-20** (the FE range D-80..D-84 is used up).
  - **D-33:** shared `QboRetryService`. The badge's inline button shows only on a live connection, and only where the badge's inline action is on (off on the three list pages).
    - It shows when the user holds INTEGRATION_SYNC (push), or when by-source has a target with `canAct` (REV-19). Otherwise it is hidden.
    - Steps: retry each `canAct` target `{ targetCode }`, then `sync/push` only with SYNC. Push answer `notFound` → an info toast "Nothing to send for this document.", and the badge stays as it is.
    - Badges on: sales invoice, supplier invoice, customer payment, supplier payment, partner, product, and the **POS order detail** (its invoice and its credit note). Supplier credit/debit notes have no page (R-3).
  - **REV-20:** with candidate kinds, the badge acts on the kind the gateway holds the record as (a held record counts), else on the kind the `Post` posting was made as: SalesReceipt / RefundReceipt / CreditNote→CreditMemo / SalesInvoice. When the kind is still unknown, there is no Send and no Push now. The Logs tab uses the row's own kind.
  - **D-34:** Logs tab (`?tab=logs`), with a "QuickBooks Logs" menu entry. The tab follows `?tab=` changes while the page is open. Failed only by default. A row expands to its bodies (loaded once, copy button). "Sync item" opens the Sync tab filtered on the record. Retry uses the same gate as the badge: by-source is asked once per document, only without SYNC.
  - **QA-7:** the Logs tab has a "Hide recovered" toggle, **on by default**. It sends `unresolvedOnly` explicitly both ways (the server defaults to true, D-69a), and the server does the filtering; there is no client-side filter. With the toggle off, the full history shows, and a failed call whose record is now Synced or Voided gets a "Recovered" chip. A succeeded call never gets one.
  - **D-35:** `RefundFromAccountRef.{M}` rows. The placeholder shows what is used while a row is empty (base → that method's deposit), and the hint states the lookup order.
  - **D-36:** a "Journals and lines" drawer on each log row, full width on phones, each journal its own card with its lines in a horizontally scrolling table. Shows number, type, date, Draft/Posted, "Reverses", "Reversed by" (worked out from the other journals), totals, and lines with a balanced total row. CORE's `journals[]` has **no lines yet**, so the drawer shows a journal's lines when they are sent, and otherwise the posting's own `lines`, labelled "as the document sent them" (document currency, accounts by role). "No general ledger journal — internal accounting is off or not set up." when there is no GL target, it was skipped GL_NOT_READY / GL_DISABLED / TARGET_INACTIVE, or `glActive` is false; otherwise "not posted yet (status)".

- **D-49b (CORE, QA-2 + D-36) Posting log follows QuickBooks; journal lines in detail.** Integration never calls Accounting: before the log list page, detail, by-source and summary are read, and on every retry-job run, the org's `quickbooks` rows still Queued/Held or mirrored Failed/DeadLetter (≤ 500, newest first) are looked up with `IQuickBooksGateway.GetStatusAsync` — one call per kind; kind from the stored payload type (SalesInvoicePayload → SalesInvoice, PaymentPayload → Payment, …; else from the document type), record id = the payload's `externalId` (else the source uuid); a void uses its original's. Mapping, persisted: Synced → Posted + `externalId` = QuickBooks id + new `externalDocNumber` (column `FinancialPostingTargets.ExternalDocNumber`, guarded migration `A44_PostingExternalDocNumber`); Voided → Posted (on a post, note `Voided in QuickBooks.`); **REV option B:** Failed/Blocked → `Failed` and NeedsResolution → `DeadLetter`, both **mirrored** (new column `MirroredFromTarget`, guarded migration `A44_PostingMirroredFlag`; `nextAttemptAt` null, attempts untouched, `lastError` = `QuickBooks {state}: {code}: {message}`). The Accounting retry job never re-sends or dead-letters a mirrored row — Integration's outbox owns those retries (D-11); a manual retry (posting page, D-33 button, Logs tab) re-dispatches it and clears the flag (any Accounting dispatch clears it). Mirrored rows keep being refreshed: Synced → Posted (flag cleared); Pending/InProgress/WaitingOnDependency → Queued (flag cleared). For non-mirrored Queued/Held rows: Pending/InProgress/WaitingOnDependency/NotSynced/DryRunOk → unchanged; a void's record still Synced → unchanged. A lookup error is logged and changes nothing. Detail `journals[]` gain `lines` (lineNumber, accountCode, accountName, description = memo, debit, credit — base currency), `reversesJournalNumber` and `reversedByJournalNumber` (one extra query).
## Residuals (open)
- R-FE-1 (D-36) The journal drawer shows journal lines only when `GET log/{uuid}` sends `journals[].lines` (`accountCode, accountName, description|memo, debit, credit`). CORE's model has none yet, so the posting's own lines are shown instead. CORE: please add them.
- R-FE-2 `POST validation/check` (D-27) has no button yet; the Sync tab's attempt log + Resolve cover the same need for now.
- R-FE-3 "Only in QuickBooks" (Match tab) and the POS walk-in customer picker load the first 200 remote records (`pageSize=200`) and search within them; records beyond 200 are not listed until server-side search/paging is wired in.
- R-CORE-1 Draft journals (AutoPostToGL off) have no review/post UI or endpoint yet (journal UI is a later addendum); they block period close and setup reset like any non-opening journal.
- R-CORE-2 Once any A44 journal exists, A42 setup reset is refused (non-opening journals, D-17) — intended, but it means a GL turned on by mistake needs manual cleanup.
- R-CORE-3 A void before the original reached an *external* target skips that target's open original row (D-45); the external void is still dispatched and the external target decides (QBO: no map → no-op).
- R-CORE-4 JE numbers come from the Logistics counter; the local fallback (no generator) is not concurrency-safe (only tests/hosts without Logistics).
- R-1 Bool `AutoPush*` columns kept (unused) until a later cleanup migration.
- R-2 Legacy single-invoice supplier `Payment` not sent to QBO.
- R-3 No back-office customer credit note / debit note document in SCM (only POS SCN).
- R-4 Vendor credits not auto-applied to bills in QBO.
- R-5 Store credit expiry/cancel/adjust post nothing (POS-only, A39).
- R-SRC-1 Bounced till tender of a sales-receipt sale (REV-15):
  - The GL reopens the receivable: a document-less Payment `pos:Payment:{tender}` (Dr RECEIVABLE / Cr BANK).
  - In QuickBooks the receipt is only flagged for the accountant, and only once it is synced (flagging an unsent one would drop it).
  - A receipt not yet sent is sent as paid; the accountant records the returned payment by hand.
- R-SRC-2 An EXCHANGE refund leaves an unapplied credit memo, and the exchange's new sale an open invoice, in QuickBooks: EXCHANGE payments move no money and are not sent (D-19). The accountant applies one to the other.
- R-SRC-3 Pre-A44 POS sales that stay invoices (D-73) do not get their cash tenders sent as Payments: the tenders are skipped as receipt-shaped. Those invoices stay open in QuickBooks unless paid by hand.
- R-SRC-4 There is no GL backfill or reconciliation: the hourly reconciliation feeds QuickBooks only. A document whose `PostAsync` never reached the gateway, or one from before the GL was enabled, has no journal.
- R-SRC-5 A carried-forward supplier note applied to an invoice later is not linked to the bill in QuickBooks (R-4). After `SupplierNotes.DeductAsync` lowers `Invoice.TotalAmount`, a reconciliation re-push of the bill can be refused as a total mismatch (pre-existing behaviour).
- R-QBO-1 Credit-memo application to the invoice is made only when the create call itself succeeds (D-53). A memo linked by lookup after an unknown outcome is not applied; the accountant applies it.
- R-QBO-2 D-16 account fallbacks apply to the new kinds only. Invoices, bills and items still require the four settings accounts (D-3: unchanged).
- R-QBO-3 Payment LinkedTxn amounts are not checked against the invoice's open balance in QBO; an over-application is QBO's refusal (Failed/NeedsResolution).
- R-QBO-4 (D-55a) The income-account fix-up knows "no income account" only for items SCM created (stored payload `IsSold=false`, not adopted). An adopted QBO item without an income account still gets QBO's 2390 refusal. The "fixed" marker is the item map's warning: if a later item sync clears it, the next sale repeats the fix-up — one harmless sparse update. The item payload and its fingerprint are untouched (D-3), so the item map still says not for sale.
- R-QBO-SDK-1 Unknown-outcome lookup of a Payment reads at most 1000 payments of its TxnDate (D-57).
- R-QBO-SDK-2 Per-line class tracking: the mapper supports `RemoteSalesLine.ClassId` but the builder sends the class at the header only; companies tracking classes per line get no class on sales documents until the builder fills the lines (D-58). Needs the Class-tracking-per-line preference in the reference snapshot (CTRL).
- R-QBO-SDK-3 Void/delete wire formats are proven against the SDK on loopback and Intuit's documented request shapes, not yet on the live sandbox (QA's live run, D-32).

## QA — E2E test plan, results and findings (agent QA)

**Where:** `tests/SMS.Integration.Tests/FinancialPosting/` on `SapWebApplicationFactory` (real Program.cs host, throwaway LocalDB `SMS_SAP_E2E_*`, only Intuit faked by `FakeQuickBooksCompany`/`FakeQuickBooksAuth`, every recurring job at "31 Feb" so the tests run the jobs). Every test creates its **own organization** through the platform create path (`Mods.OrgAsync`), so per-org isolation is built in and the classes are independent. Bodies are anonymous objects and answers are read as JSON (`SapKit`/`J`), so the classes compile whatever the builders' C# models look like. Run one class at a time: `dotnet test tests\SMS.Integration.Tests --filter FullyQualifiedName~<Class>` (artifacts path under a `%TEMP%` junction when SNI complains).

**Source of the T/BR numbers.** The FSD text (SMS-FSD-ADD-044) is not in the repo; the T-groups below follow the ranges the register and this analysis use (T-01..08 gateway, T-21..23 atomicity, T-40..44 entity import, T-45..49 tax, T-50..54 coverage, T-55..61 void/credit, T-62..68 sync control). T-09..T-20 and T-24..T-39 are mapped by register phase (C8/C9 document mapping and journals; C7/C8/C11–C14 outbox, client, entity sync). BR numbers are mapped where this analysis names them; the rest are grouped by area and marked "by area" — the owner of the FSD should confirm those.

### Classes (QA)
| Class | Covers |
|---|---|
| `FpKit.cs` | helpers: GL-ready org (US_GENERAL → FY → skip opening → Complete), posting config, QBO connect/settings/live per org (own realm), jobs (DependencyJob, SyncOutboxJob, Accounting GL retry job by reflection), gateway called in-process under `HangfireTenantScope`, journal/map/posting-row SQL reads, fake-record JSON reads (no dependency on QBO's Remote* types) |
| `PostingGatewayE2ETests` | T-01..T-08, D-8, D-10, D-13, BR-01, BR-03/04, per-org isolation, GL_VIEW vs GL_SETUP_MANAGE on `api/finance/posting/*` |
| `GlPostingE2ETests` | T-09..T-20 (GL side), T-21 (D-9), T-55..T-61, closed period → PERIOD_CLOSED → retry, update-on-change (D-13), AutoPostToGL=false → Draft, GL off → nothing |
| `QuickBooksDocumentsE2ETests` | T-09..T-20 (QBO side) for Payment, BillPayment, VendorCredit (CN, DN), SalesReceipt / CreditMemo / RefundReceipt (POS), voids (D-19..D-24, D-26), dependencies (payment waits for invoice), provider defaults in the built document (D-15, BR-12) |
| `SyncControlE2ETests` | T-62..T-68, D-14, BR-29, BR-31, INTEGRATION_VIEW vs INTEGRATION_MANAGE |
| `CoverageAndMappingE2ETests` | T-40..T-54, D-16, D-17, D-18, D-27, BR-18, dead letter (retry-failed / skip, D-11) |
| `FinancialJourneyE2ETests` | regression journey: sale order → invoice issue → customer payment → QBO (fake) Invoice + linked Payment, with GL on (balanced journals, AR nets to 0) and GL off (QBO unchanged, no journals); D-3 |

### T mapping
| T | What | Coverage (E2E class / unit owner) | D change |
|---|---|---|---|
| T-01..T-08 | gateway: no target → NoTargetConfigured; GL only; QBO only; both; one target fails → PartialFailure; ModuleNotLicensed; same key+payload → DuplicateSkipped; never throws | E2E `PostingGatewayE2ETests` (QA); unit CORE | D-10 (licensing = no target module licensed), D-13 |
| T-09..T-20 | per document: SalesInvoice, CreditNote, Payment, SalesReceipt, RefundReceipt, PurchaseBill, BillPayment, VendorCredit → GL lines by role (§3) and QBO document | E2E `GlPostingE2ETests`, `QuickBooksDocumentsE2ETests`; unit CORE (rules, rounding), QBO (object builder), SRC (line building) | D-4, D-5, D-19..D-24; COGS not posted (D-28) |
| T-21 | **rewritten (D-9):** the source commit never depends on the GL | E2E `GlPostingE2ETests` (invoice issues while the period is closed; posting row Pending PERIOD_CLOSED) | D-9 |
| T-22 | **rewritten:** GL journal + posting row atomic | unit CORE; E2E asserts no journal while a row is Pending/Failed | D-9 |
| T-23 | **rewritten:** external queue atomic with the map row | unit QBO (existing gateway tests) | D-9 |
| T-24..T-39 | outbox, retries, client/OAuth, entity sync gateway | existing `QuickBooksJourneyTests` + Integration unit suite (regression); E2E entity sync via partner/item save in the journey; immediate kick probed in `FinancialJourneyE2ETests` | D-6, D-7 (✕ config keys), D-11 |
| T-40..T-44 | import QBO customers/vendors/items as remote-only; list unlinked; link; unlink (refused while open docs) | E2E `CoverageAndMappingE2ETests` | D-17 |
| T-45..T-49 | tax import & suggest, exempt/zero, ambiguous left for the admin, validator lists all unmapped | E2E `CoverageAndMappingE2ETests` | D-18 |
| T-50..T-54 | coverage per kind, readiness + blocking issues, validation/check without an Intuit call, preflight gains deposit/bank checks | E2E `CoverageAndMappingE2ETests` | D-27 |
| T-55..T-61 | void invoice / payment / bill payment / sales receipt; bill reversal = flag + GL reversal; never-synced void → GL only; IssueCredit | E2E `GlPostingE2ETests` + `QuickBooksDocumentsE2ETests` | D-26 |
| T-62..T-68 | sync control list/defaults, disable+hold → HELD, disable no hold → skipped, enable releases all held (confirmRelease), explicit release, no row = enabled, permissions | E2E `SyncControlE2ETests`; unit CTRL/QBO | D-14 |

### BR mapping
| BR | Rule | Coverage |
|---|---|---|
| BR-01 | Σdebit = Σcredit (0.001) else ValidationFailed | `PostingGatewayE2ETests` (unbalanced synthetic posting), every GL assertion checks balance |
| BR-03, BR-04 | a gateway/target failure never fails the caller | `GlPostingE2ETests` (closed period), `PostingGatewayE2ETests` (QBO revoked → GL still posts) |
| BR-05 | same-transaction posting | **changed by D-9** → after-commit + retry; tested as T-21 |
| BR-12 | document overrides win over provider defaults | `QuickBooksDocumentsE2ETests` (DepositTo override for Payment) |
| BR-13 | no cross-schema FKs | unit/migration review (CTRL/CORE); E2E asserts nothing |
| BR-18 | import never creates SCM entities | `CoverageAndMappingE2ETests` (partner count unchanged after import) |
| BR-25 | never-synced document void → external no-op, GL reversal still made | `GlPostingE2ETests` |
| BR-26 | supplier bill reversal = flag for the accountant + GL reversal | `GlPostingE2ETests` + existing `QuickBooksTaxAndCurrencyE2ETests` §7 |
| BR-29 | no sync-control row = enabled | `SyncControlE2ETests` (row deleted in SQL → processed) |
| BR-31 | enabling a held type releases all held | `SyncControlE2ETests` |
| others (BR-02, 06–11, 14–17, 19–24, 27, 28, 30, 32, 33) | by area (FSD text not in repo) | gateway/GL rules → `PostingGatewayE2ETests`/`GlPostingE2ETests`; mappings/defaults → `CoverageAndMappingE2ETests`/`QuickBooksDocumentsE2ETests`; control → `SyncControlE2ETests` |

Added on request: `PostingApiE2ETests` (CORE HTTP surface, QuickBooks-only org with INTEGRATION_VIEW, PERIOD_CLOSED → retry job, DeadLetter after the 6th failure), `EntitySyncE2ETests` (customer / supplier / variant save + variant hard delete through IEntitySyncGateway reach QuickBooks; payload JSON + fingerprint byte-identical to the pre-A44 IQuickBooksSource path), `SyncLogAndBySourceE2ETests` (D-33..D-35 by-source, push of an unmapped document, call-log isolation / failedOnly / redaction / permissions, REV-20 receipt-shaped POS sale pushed as SalesInvoice).
QA decisions: **D-85** FakeQuickBooks reference data gains accounts 5 Checking (Bank), 6 Savings (Bank), 7 Undeposited Funds, 8 Company Visa (Credit Card) for deposit/bank/refund defaults (existing tests only assert 1–4). **D-86** the dead-letter E2E forces Failed/NeedsResolution by SQL — the fake adopts duplicates through Find and cannot refuse.

### Results (2026-10-16)
E2E (LocalDB, one class at a time): PostingGateway 5/5 · PostingApi 3/3 · GlPosting 4/4 · QuickBooksDocuments 4/4 · SyncControl 3/3 · CoverageAndMapping 4/4 · FinancialJourney 2/2 · EntitySync 1/1 · SyncLogAndBySource 3/3 — **29/29**.
Regression E2E: QuickBooksAppKeys 2/2, QuickBooksApiKey 1/1, QuickBooksTaxAndCurrency 1/1, SalesFlow 2/2, PurchaseFlow 2/2, SupplierNoteApplication 6/6, SupplierInvoiceHardening 8/8, GlSetup 6/6, GlChart 8/8, GlAccess 4/4, PosE2E 8/8, PosReturns 8/8, Crm 3/3; **QuickBooksJourneyTests 0/1 (QA-4, stale assertion)**.
Unit suites: Integration 1241/1241 · Accounting 156/156 · Finance 2297/2297 · Suppliers 307/310 (3 skipped) · Inventory 549/549 · Pos 116/116 · Demand 961/962 (1 skipped) · Auth 381/381 · Tenancy 181/181.
Live (SMS_Dev ↔ Intuit sandbox "Sandbox Company GB 72c4", realm 9341458300527927, platform app; API on LocalDB only — log checked, 0 SMSGlobal/Azure references):

| SCM | QBO | Evidence |
|---|---|---|
| SINV-20261016-0002 (GBP 30.00, Thames Valley Supplies Ltd) | Invoice **186** | first Create **Refused 2390** "no income account associated with the item" (QA-1) → product made saleable → Item 35 sparse update with IncomeAccountRef 66 → `sync/items/{id}/retry` → Synced |
| CPAY-20261016-0001 (CHEQUE 30.00, allocated 30) | Payment **187** | Synced 5 s after recording (immediate kick, D-11); request `LinkedTxn[{TxnId:186,TxnType:Invoice}]`, response `UnappliedAmt 0` → invoice balance 0; no CHEQUE deposit default → Undeposited Funds (83) |
| POS-A4410804-20261016-0001 / SINV-20261016-0003 (cash 30.00) | SalesReceipt **188** | DepositToAccountRef 81 (Current); POS void → `Void` `{"Id":"188","SyncToken":"0","sparse":true}` Succeeded, response TotalAmt 0, "Voided"; SCN-20261016-0001 not sent (correct) |
| Item Chair Seat Cushion | Item **35** | Create (no income account), 2 sparse updates |
| by-source / sync/log | — | unknown uuid → 200 `[]`; failedOnly shows the 2390 refusal; Payment call detail: request 242 / response 567 chars, no token/secret/Bearer; unknown id → 404 |

Not done live: customer-payment bounce → Payment void (QA-6: no endpoint); BillPayment create/delete (SMS_Dev bills Blocked `PURCHASE_BASE_NOT_HOME`: purchase base PKR vs QBO home GBP — configuration); REV-20 live check (deferred until the fix, per lead).

### Findings
- **QA-1 (QBO / SRC-MD, medium; surfaced live).** `src/SMS.Modules.Inventory/Integration/VariantPayloadFactory.cs:35` sends `IsSold = product.IsSaleable`; a product with IsSaleable=false is created in QuickBooks without an IncomeAccountRef, yet Demand sells it and Finance issues the invoice → QuickBooks refuses the invoice (2390). Either send the income account whenever an item is used on a sales document (gateway) or refuse selling a non-saleable product (Demand). Live: SINV-20261016-0002.
- **QA-2 (CORE / QBO, medium).** The posting log's `quickbooks` target stays `Queued` with `externalId` null after the outbox outcome: live by-source showed SINV-20261016-0002, CPAY-20261016-0001 and the SalesReceipt "Queued" while their maps went Failed→Synced (QBO 186/187/188). D-12's cross-target view is stale for QuickBooks; nothing writes the outbox result back to `finance.FinancialPostingTargets`.
- **QA-3 (CTRL, low).** Coverage `warnings` lists `DEPOSIT_ACCOUNTS` twice (area Preflight + area Defaults) — `CoverageAndMappingE2ETests` PROBE output.
- **QA-4 (CTRL, test).** `tests/SMS.Integration.Tests/QuickBooks/QuickBooksJourneyTests.cs:41` expects 15 `integration` tables; the A44 migration makes 19 (SyncControlConfigs, ProviderDefaults, DocumentOverrides, RemoteEntities). The class stops there, so the rest of the QBI-34 journey is not run by it (its ground is covered by the FinancialPosting classes). Update the count to 19.
- **QA-5 (Finance, low, pre-existing).** `POST api/supplier-payments` with `SupplierName` null → 500 (NOT NULL on finance.supplier_payments.SupplierName) instead of 400.
- **QA-6 (SRC, gap).** No HTTP endpoint bounces/reverses a customer payment (`ICustomerPaymentService.BounceAsync` is unreachable), so D-19 "BOUNCED → void the QBO Payment" can't be exercised end to end; the Payment void request shape is proven on loopback (QBO-SDK) and live through the identical SalesReceipt void.
- Info: D-75 (DISPUTED posts nothing; WRITTEN_OFF flags) is what the tests check. D-11 kick works live (5 s); in the test host it lands within the Hangfire delayed-job poll (≤ 15 s).
- **QA-7 (CTRL / lead, design question).** `sync/log?failedOnly=true` filters calls by outcome (D-69a), so a refused call stays listed after its record was retried and synced (row `state` = Synced) — the 2390 refusal of SINV-20261016-0002 is still listed. If the Logs tab should drop recovered records, filter on the map state or add an "open only" flag.

### Final live run after the code freeze (2026-10-16, SMS_Dev only, log checked)
| Check | SCM | QBO | Result |
|---|---|---|---|
| QA-1 | SINV-20261016-0004 (GBP 48; "Chair Steel Frame", IsSaleable=false, never in QBO before) | Item **36**, Invoice **189** | item created without income account, then sparse `{"IncomeAccountRef":{"value":"66"},"Id":"36","sparse":true}` before the invoice create; invoice Synced first time — fixed |
| QA-6 / D-19 | CPAY-20261016-0003 (cheque 48 → SINV-…-0004), `POST customer-payments/{uuid}/bounce` | Payment **190** | create linked to 189; bounce → Void `{"Id":"190","SyncToken":"0","sparse":true}`, response TotalAmt 0, `Line: []` (link to 189 removed → invoice open again); SCM invoice ISSUED, balance 48 — fixed |
| REV-20 | POS-A4410804-20261016-0002 / SINV-20261016-0005 (cash 30) | SalesReceipt **191** | `sync/push {SalesInvoice}` → `requested 0, notFound [uuid]`; no SalesInvoice map, no Invoice call — fixed |
| QA-2 | by-source of the above + SINV-…-0002 / CPAY-…-0001 | — | quickbooks target `Posted` with externalId 189 / 190 (+ Void row 190) / 191 / 186 / 187 — fixed |
| sync/log | — | — | the earlier 2390 refusal is still in failedOnly (state Synced) — QA-7 |

Final E2E (one class at a time): QuickBooksJourneyTests 1/1 (run twice, step 4 clean both times), every FinancialPosting class 29/29 (CoverageAndMapping once hit a LocalDB "Execution Timeout Expired" at PO approve while other suites ran in parallel; 4/4 on its own re-run), SalesFlow 2/2, PurchaseFlow 2/2.

- SMS_Dev data left by the live run: draft SINV-20261016-0001 (Dell i5 — not issuable: InventoryItems shows 13 on hand but the product ledger holds 0, a pre-existing SMS_Dev inconsistency), approved bill 1c73fe7a (Blocked, currency config), POS store A4410804 + terminal + open shift, GBP selling rule 25 and IsSaleable/IsAvailableForPos on "Chair Seat Cushion", sync control Payment/BillPayment/POSSale on, deposit/bank defaults → account 81.

## Review (REV)

Independent review of A44 code as it lands (read-only; findings are sent to the owner by message). Severity H/M/L. Status: open · fixed (verified by REV) · accepted (residual, reason).

**Round 1 — 2026-10-16 (early: Shared contracts, Accounting gateway + GL target, Finance posting builders, Integration migration/sync control, mapper diffs).**
D-3 check: `Payloads.cs` only adds `: IAccountingDocument` (ExternalId already existed), so customer, vendor, item, invoice and bill JSON/fingerprints are unchanged. The invoice/bill mappers only add Class/Department/SalesTerm refs, which are null without defaults. The invoice document = the unchanged `SalesInvoicePayloadFactory.Build`.

| # | Owner | Sev | Where | Finding / fix | Status |
|---|---|---|---|---|---|
| REV-1 | CORE | H | `Accounting/Posting/InternalGlPostingTarget.cs` Move() | `ChartOfAccount.CurrentBalance` is a tracked read-modify-write with no concurrency token, so concurrent postings lose updates. Fix: atomic `ExecuteUpdate` (`+= delta`) inside the transaction, or rowversion + retry | fixed (R2: `MoveBalancesAsync` ExecuteUpdate) |
| REV-2 | CORE | H | GL `ReverseCoreAsync` (NOT_POSTED) + retry | A void that arrives while the original's GL row is Failed/Pending is Skipped; the later retry posts the original, leaving a journal for a cancelled document. Fix: skip the Post when a void exists; mark the open original row Skipped on void | fixed (R2: D-45 SkipOpenOriginalTargets + VOIDED check in retry and GL post; see REV-13) |
| REV-3 | CORE (+QBO/SRC) | H | `FinancialPostingGateway.cs` same-fingerprint branch | DuplicateSkipped never calls the quickbooks target, so a held release, "Push now" or a reconnect re-send of an unchanged payload never reaches QBO (D-3, BR-31). Fix: dedupe internal_gl only and always dispatch quickbooks (it dedupes by map fingerprint) | fixed (R2: D-44 `DedupesHere` = internal_gl only; sources/release also go direct to IQuickBooksGateway) |
| REV-4 | CORE | M | gateway `DiscardUnsavedChanges` | After a target throws past an inner SaveChanges (ReverseJournalAsync), the rolled-back balances stay tracked as Unchanged; the next posting in the scope saves stale balances. Fix: `ChangeTracker.Clear()` + reload, or one scope per posting | fixed (R2: Clear + re-read; job = one scope per org) |
| REV-5 | CORE | M | gateway / retry job | Nothing serialises concurrent dispatches of one target row (job vs. manual retry vs. live update), so two journals or a double reversal are possible. Fix: rowversion on FinancialPostingTargets or applock on the key; the job claims rows | fixed (R2: sp_getapplock on the original key in GL post and void; conditional-UPDATE claim in PostingRetryJob) |
| REV-6 | CORE (+SRC) | M | gateway ct | The request's CancellationToken after commit: an aborted request loses the posting (no row) or leaves it Pending. Fix: `CancellationToken.None` for the gateway's writes; the job sweeps stale Pending | fixed (R2: D-47; Pending older than 5 min retried) |
| REV-7 | CORE | L | `TenantFinanceConfigService.GetAsync` | A GET with GL_VIEW inserts the config row, contrary to its own doc comment. Align (prefer no write on GET) | fixed (R2) |
| REV-8 | SRC | H | `Finance/Integration/Posting/SalesPostings.cs`, `PosDocuments.cs` | The POS type (SalesInvoice vs SalesReceipt, CreditNote vs RefundReceipt) is re-derived per post and is part of the key. A re-post after tenders/refunds change posts the sale under two keys: double revenue in the GL, Invoice + SalesReceipt in QBO. Fix: decide once (existing posting/map) or void the old key first; confirm tenders commit before the hook | fixed (R2: tenders/refunds committed in the same save before PublishSaleAsync/PublishReturnAsync; the shape inputs are immutable afterwards; test `PostingFlowTests.A_bounced_cheque_tender_keeps_the_sale_a_receipt_…`) |
| REV-9 | SRC | M | POS void path | The void must carry the same DocumentType as the post (SalesReceipt / RefundReceipt), else the GL answers NOT_POSTED and the journal stays | fixed (R2: the void reads ShapeSalesAsync before the reversals) |
| REV-10 | CTRL | M | `Integration/Migrations/20261016122637_A44_SyncControlAndDefaults.cs` CreditNote/Payment rows | Seeded enabled for every existing connection even with AutoPushSalesInvoices off, so payments start flowing on deploy and wait forever on unsynced invoices. Contradicts QBO `LegacySyncControlReader`. Fix: follow AutoPushSalesInvoices; align `SyncControlDefaults.Legacy` no-row fallback for the 4 new types | fixed (R2, D-67) |
| REV-11 | QBO + SRC | M | `Core/Sync/HeldRecords.cs` ReleaseAsync | POS sales held before A44 are SalesInvoice maps; if the source now shapes them as SalesReceipt, the old map stays held forever or both documents are sent. Agree one rule; close the superseded map | fixed (R2: SRC keeps legacy uuids as invoices; QBO `SyncHolds.CloseSupersededAsync`) |
| REV-12 | QBO | L | `QboInvoiceMapper.cs`, `QboBillMapper.cs` | BOM + mojibake in doc comments (`â†”`, `â€”`). Re-save as UTF-8 (no string literal affected; src grepped) | fixed (R2, re-grepped) |

Checked OK in round 1: Integration A44 migration DDL fully guarded, the copy is NOT-EXISTS guarded and re-runnable, Down() drops only the new tables; gateway posting lookups are org-predicated under IgnoreQueryFilters; PostingSerializer document type allowlist (no arbitrary type load); BR-01 validation; GlLines and the base conversion balance via DEFAULT_ROUNDING; the POS tender/refund queries are org-scoped.

**Round 2 — 2026-10-16 (mid: round 1 fixes, Accounting migration, PostingController, retry job, QBO targets/hold/kick/walk-in, CTRL controllers, Finance/Suppliers/Inventory callers, FE service routes).**

| # | Owner | Sev | Where | Finding / fix | Status |
|---|---|---|---|---|---|
| REV-13 | CORE | L | `InternalGlPostingTarget.cs` PostAsync | The ":void exists" check runs before `LockAsync`; a void racing in between gets NOT_POSTED, and the post then writes a journal for a voided document. Fix: re-check inside the lock | fixed (R3: the check is repeated after LockAsync) |
| REV-14 | CTRL | L | A44 Integration migration, POSSale/POSReturn | HoldWhenDisabled = 1 always, but the legacy behaviour holds only when AutoPushSalesInvoices is on (else skip), so orgs with invoices off would hold their POS history. Fix: hold = AutoPushSalesInvoices | fixed (R2) |
| REV-15 | SRC | M | `CustomerPaymentPostings.cs` receipt-tender skip | A bounced/reversed cheque/card tender of a SalesReceipt sale posts nothing: the GL keeps the bank debit, AR is not re-debited, and the QBO receipt stays paid. Fix: a GL-only Dr AR / Cr BANK for the tender + flag the QBO SalesReceipt; or record a residual | fixed (R3: `ReceiptTenderBouncedAsync`, D-73, GL-only posting under pos:Payment:{tender}; flags the receipt only when it is Synced/NeedsResolution) |
| REV-16 | SRC-MD | L | `PartnerQuickBooksPublisher`, `VariantQuickBooksPublisher` | No direct-QBO fallback when the host has no Accounting (NullEntitySyncGateway), unlike Finance's D-70. The real host is unaffected. Fix, or accept with a D-line | fixed (R3: `RouterPresent` → direct source send, D-76) |
| REV-17 | FE (or whoever created them) | L | `SupplyChainFrontend/` root | Five stray empty files (ADDENDUM-44-ANALYSIS.md, ADDENDUM-44-TASKS.md, GlPostingE2ETests.cs, PostingGatewayE2ETests.cs, SyncAdminService.cs) from shell redirects; delete them before commit | fixed (R3: FE deleted the five; the lead deleted two older 0-byte leftovers) |
| REV-18 | CORE | L | `PostingLogService` retry/skip | Any manage code could retry/skip any target, so an integration-only admin could Skip an internal_gl posting (segregation of duties). Fix: internal_gl needs GL_SETUP_MANAGE, external targets need INTEGRATION_MANAGE; with no targetCode, act only on touchable targets, 403 when none | fixed (R3: `CanTouch`/`Touchable`) |
| REV-19 | FE | L | `qbo-sync-badge` D-33 button, Logs Retry | The button is gated on INTEGRATION_MANAGE, but `POST sync/push` requires INTEGRATION_SYNC, so a 403 is possible. Fix (lead): show with INTEGRATION_SYNC or a canAct posting target; each step only if permitted | fixed (R3: `canPush` = INTEGRATION_SYNC, posting step via server canAct; specs `qbo-sync-badge.d33`, `qbo-retry.service`) |
| REV-20 | SRC (+FE, CTRL) | H | `SalesInvoiceQuickBooksSource.ActionsAsync` / `PosDocumentPostings` legacy rule × `SyncAdminService.PushAsync` placeholder | Any SalesInvoice map counts as "pre-A44 legacy", including the empty placeholder that sync/push now creates. "Send to QuickBooks" on a POS cash sale's invoice page therefore sends an Invoice on top of its SalesReceipt: double revenue in QBO, and later receipt pull-backs skipped. Fix: legacy only when the map really carries an invoice (state/RemoteId/held code); FE badge uses the posted kind; CTRL creates no placeholder for documents | fixed (R3: `PosDocuments.IsLegacyInvoice`; `PushAsync` creates no document placeholder → `notFound`; badge `alsoKinds`/effectiveKind, no Send when the kind is unknown) |
| REV-21 | CORE | M | QA-2 `QuickBooksStatusRefresher` × `PostingRetryJob` | A quickbooks row the refresher marks Failed from a QBO state is re-dispatched by the GL retry backoff: up to 5 extra admissions or Intuit calls per record, on top of Integration's own outbox policy. Recommended (option B): mirrored rows are not auto-retried (marker the job excludes); manual retry only; Integration owns dead-lettering | fixed (R3: `MirroredFromTarget` + guarded migration `A44_PostingMirroredFlag`; the job excludes mirrored rows; an Accounting dispatch clears the flag) |

Residuals accepted by the lead:
- **REV-R1 (D-61).** Existing connections start Payment/CreditNote **off, no hold**. While off, their QBO invoices stay open there. Turning it on does not back-fill past payments, which were skipped rather than held: an admin "Sync all" of the Payment kind does that. This is intentional, to avoid double-counting receipts already entered by hand.

Checked OK in round 2:
- **Accounting migration `A44_FinancialPosting`:** every table, column, index and FK is guarded and additive.
- **PostingController (D-41):** every action is permission-gated, and config PUT requires GL_SETUP_MANAGE.
- **PostingRetryJob:** runs per org under HangfireTenantScope, with a fresh scope and a row claim.
- **QBO gateway:**
  - sync-control Skip/Hold replaces AutoPush/PosSales in place;
  - voids still go while a type is held;
  - the kick fires only on Pending/WaitingOnDependency.
- **Dependencies:** they include documents (invoice, bill, original invoice).
- **Void per kind (D-51):** void Invoice/Payment/SalesReceipt, delete BillPayment/RefundReceipt, flag CreditMemo/VendorCredit/Bill.
- **CTRL endpoints:** VIEW for reads, MANAGE for writes.
- **Frontend:** the service routes match the backend.
- **Payment GL lines:** they balance, with realized FX on EXCHANGE_GAIN_LOSS.
- **Callers:** they publish after commit, on the same triggers as before plus the new ones; partner/variant payloads still come from the unchanged source code (D-3).
- **ExternalAccountMapReader / PostingLogService:** queries are org-predicated.

**Round 3 (final) — 2026-10-16, after "code frozen".**
- **Status:** REV-1..REV-21 are all fixed and verified in code. REV-R1 is accepted by the lead.
- **Re-verified at the end:**
  - The three Accounting migrations (`A44_FinancialPosting`, `A44_PostingExternalDocNumber`, `A44_PostingMirroredFlag`) are guarded by OBJECT_ID/COL_LENGTH and additive.
  - The Integration migration `A44_SyncControlAndDefaults` matches today's switches, with D-61's choices: Payment/CreditNote off; POS hold = AutoPushSalesInvoices.
  - Option B (REV-21) is in place.
  - The REV-20 three-part fix is in place.
  - The REV-19 gating is in place.
  - The REV-18 per-target permissions are in place.
  - The by-source and sync/log endpoints are org-scoped and permission-gated, and the logs are redacted at write.
  - The D-35 refund-account lookup order is correct.
  - QA-1/3/4/5/6 have landed.
  - The ExternalId "D"-format contract holds across all SRC payloads, and STORE_CREDIT/EXCHANGE are never sent.
- **Unit suites, run one at a time on the frozen tree:**

  | Suite | Result |
  |---|---|
  | Accounting | 161/161 |
  | Integration | 1246/1246 |
  | Finance | 2311/2311 |
  | Suppliers | 307 passed, 3 skipped |
  | Inventory | 549/549 |
  | Pos | 116/116 |
  | Tenancy | 181/181 |
  | Demand | 961 passed, 1 skipped |
  | Auth | 381/381 |
  | Frontend `ng test` | 3267/3267 |

- **Not reviewed by REV:** the QA live-sandbox run is QA's own sign-off. Its open items are QA's.

**REV sign-off — 2026-10-16.** No open review findings and no blockers from code review.


## A47 changes (journal engine, agent GW, 2026-10-24)

Addendum 47 (docs/journal-engine/ADDENDUM-47-ANALYSIS.md, C11, D-19, D-GW-1..8) moved the `internal_gl` target onto the
journal engine. The gateway contract (`IFinancialPostingGateway`, `FinancialTransaction`, `VoidRequest`, target result codes)
did not change. What changed inside `InternalGlPostingTarget`:

- **Every journal goes through `JournalEntryWriter.CreateAsync`** (lines already in base, `AmountsInBase`): the post, the
  update (reversal + new journal) and the void / reverse. The A44 steps before it stay: role → account (explicit code wins),
  2 dp base conversion with the rounding line on DEFAULT_ROUNDING, PERIOD_CLOSED / EXCHANGE_RATE_MISSING / UNMAPPED_ROLE /
  ACCOUNT_NOT_FOUND checks, REV-2 / REV-5 / REV-13 (application lock per document, void wins), the internal_gl row saved in the
  same transaction (T-22), the fingerprint no-op, Draft when AutoPostToGL is off (a draft is deleted and re-created on update,
  deleted on void).
- **Journal types and numbers (D-12, D-7).** Each journal gets a journal type (SALE, PURCH, CASH when a line is on DEFAULT_CASH
  else BANK, MISC, OPEN) and a per-type number `{prefix}/{fiscal year}/{seq:D4}` — e.g. `SALE/2026/0001`, `BANK/2026/0003`,
  `JV/2026/0001` — from the concurrency-safe sequence table. **R-CORE-4 is closed.** Journals written before A47 keep their
  `JE-YYYY-NNNNN` numbers (BR-JE-010). A refused post consumes no number.
- **Reversals are marked and reconciled (D-4, BR-RV-008).** An update or a void marks the original `Reversed` with
  `ReversedByEntryId`; per account the original's lines and the reversal's lines are reconciled (Full group `REC/{year}/{seq}`,
  cleared by the reversal on its date), unless a line of that account was already reconciled by hand. The keys stay A44's
  (document key for the update reversal, `key:void` for the void). The open A44 pairs from before are fixed by the guarded data
  migration `A47_GatewayReversalReconcile` (D-GW-5).
- **Partner and line detail (D-13, D-GW-3).** The document's partner goes on its receivable / payable lines (a line's own
  `PartnerId` wins); tax code, tax amount, cost centre and project are stored per line; the header keeps the document currency
  and rate, each line its signed document-currency amount (`AmountCurrency`). The journal type's RequiresPartner is not enforced
  on gateway journals.
- **Engine refusals (task 411).** A journal-engine refusal is a Failed result, retried as before, with the A44 code:
  `PERIOD_CLOSED: {date} is not in an open fiscal period. (JE-00x …)`, `ACCOUNT_NOT_FOUND: JE-009/010 …`,
  `EXCHANGE_RATE_MISSING: JE-012 …`, else `JOURNAL_INVALID: JE-0xx …`. The other targets (QuickBooks) go on regardless.
- **R-CORE-1 is closed** by the A47 journal-entry pages (a source draft can be posted there, not edited or deleted).