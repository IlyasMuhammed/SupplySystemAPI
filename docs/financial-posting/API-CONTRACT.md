# Addendum 44 — contract

Decisions: [ADDENDUM-44-ANALYSIS.md](ADDENDUM-44-ANALYSIS.md). Owners in brackets. Anything here may be refined by its owner; record the change as a D-line in the analysis and tell the agents who consume it.

## 1. Shared contracts — `src/SMS.Shared/Finance/Posting/` [CORE, first deliverable]

```csharp
namespace SMS.Shared.Finance.Posting;

public enum FinDocType { SalesInvoice, CreditNote, Payment, PurchaseBill, BillPayment, VendorCredit,
    POSSale, POSReturn, SalesReceipt, RefundReceipt, SalesReturn, PurchaseReturn,
    InventoryAdjustment, ProductionCost, ProductionCompletion, ManualJournal, BankFee, Expense, DepositSlip, OpeningBalance }
public enum PartnerType { Customer, Vendor }
public enum PostingStatus { Success, PartialFailure, NoTargetConfigured, DuplicateSkipped, ValidationFailed, ModuleNotLicensed }
public enum TargetPostingStatus { Posted, Queued, Held, Failed, Skipped }

public sealed class FinancialTransaction
{
    public FinDocType   DocumentType         { get; init; }
    public Guid         SourceDocumentId     { get; init; }        // the SCM row's Uuid
    public string       SourceDocumentNumber { get; init; } = "";
    public string       SourceModule         { get; init; } = "";  // "finance" | "pos" | "suppliers" | "inventory"
    public DateTime     TransactionDate      { get; init; }
    public Guid?        PartnerId            { get; init; }        // business partner Uuid
    public PartnerType? PartnerType          { get; init; }
    public string?      PartnerName          { get; init; }
    public string       CurrencyCode         { get; init; } = "";
    public decimal?     ExchangeRate         { get; init; }        // 1 doc ccy = rate × base; null = base currency
    public string?      Description          { get; init; }
    public IReadOnlyList<FinancialTransactionLine> Lines { get; init; } = [];
    public FinancialDocumentMetadata? Metadata { get; init; }
    public string IdempotencyKey => $"{SourceModule}:{DocumentType}:{SourceDocumentId}";
}

public sealed class FinancialTransactionLine
{
    public int      LineNumber  { get; init; }
    public string?  AccountRole { get; init; }   // A42 default key, e.g. "DEFAULT_RECEIVABLE"
    public string?  AccountCode { get; init; }   // explicit leaf code; wins over AccountRole
    public decimal  Debit       { get; init; }   // document currency
    public decimal  Credit      { get; init; }
    public string?  TaxCode     { get; init; }
    public decimal? TaxAmount   { get; init; }
    public string?  Label       { get; init; }
    public Guid?    PartnerId   { get; init; }
    public string?  CostCenter  { get; init; }
    public string?  Project     { get; init; }
}

public sealed class FinancialDocumentMetadata
{
    public DateTime? DueDate        { get; init; }
    public string?   PaymentTerms   { get; init; }
    public string?   Reference      { get; init; }
    public string?   WarehouseCode  { get; init; }
    public Guid?     LinkedDocumentId { get; init; }
    public IReadOnlyDictionary<string, string>? Properties { get; init; }
    /// The provider-neutral document (D-4): one of the Shared payloads below. External targets build from it.
    public IAccountingDocument? Document { get; init; }
}

public interface IAccountingDocument { string ExternalId { get; } }   // SalesInvoicePayload, BillPayload and the new payloads implement it

public sealed class VoidRequest  { Guid SourceDocumentId; string SourceModule; FinDocType DocumentType; string SourceDocumentNumber; string Reason; int? VoidedBy;
                                   string IdempotencyKey => $"{SourceModule}:{DocumentType}:{SourceDocumentId}:void"; }   // (init props)

public sealed class PostingResult { PostingStatus Status; string? Message; Guid SourceDocumentId; FinDocType DocumentType; List<TargetResult> TargetResults; }
public sealed class TargetResult  { string TargetCode /* "internal_gl" | "quickbooks" */; TargetPostingStatus Status; string? InternalId; string? ExternalId; string? Error; }

public interface IFinancialPostingGateway
{
    /// After the caller's commit. Never throws (returns Failed results); idempotent by key (D-13).
    Task<PostingResult> PostAsync(FinancialTransaction transaction, CancellationToken ct = default);
    Task<PostingResult> VoidAsync(VoidRequest request, CancellationToken ct = default);
    /// Thin helper: PostAsync of a CreditNote / VendorCredit / RefundReceipt transaction.
    Task<PostingResult> IssueCreditAsync(FinancialTransaction credit, CancellationToken ct = default);
    /// D-26 supplier reversal: GL reversal + external flag-for-accountant (no external transaction).
    Task<PostingResult> ReverseAsync(VoidRequest request, CancellationToken ct = default);
}

// Entity sync (C11)
public enum SyncEntityType { Customer, Vendor, Product }          // Account sync = deferred (accounts are mapped, not pushed)
public enum SyncAction { Create, Update, Deactivate, Delete }
public sealed class EntitySyncRequest
{
    public SyncEntityType EntityType { get; init; }
    public SyncAction     Action     { get; init; }
    public Guid           EntityId   { get; init; }    // partner Uuid / variant Uuid
    public string?        IdempotencyKey { get; init; } // logged only (D-13)
    public EntitySnapshot? Snapshot  { get; init; }    // FSD fields, informational
    public IAccountingDocument Document { get; init; } = null!;   // CustomerPayload | VendorPayload | ItemPayload (D-4)
    public string InitiatedBy { get; init; } = "system";
}
public sealed class EntitySnapshot { /* FSD §15.3 fields, all optional */ }
public enum EntitySyncOutcome { Queued, NoTargetConfigured, ModuleNotLicensed, DuplicateSkipped, ValidationFailed, Held }
public sealed class EntitySyncResult { bool Success; EntitySyncOutcome Outcome; int TargetsQueued; string? ErrorMessage; }
public sealed class BatchEntitySyncResult { int TotalRequests; int Queued; int DuplicatesSkipped; List<string> Errors; }

public interface IEntitySyncGateway
{
    Task<EntitySyncResult> PushAsync(EntitySyncRequest request, CancellationToken ct = default);          // never throws
    Task<BatchEntitySyncResult> PushBatchAsync(IReadOnlyList<EntitySyncRequest> requests, CancellationToken ct = default);
}

// Target seams (registered by the module that owns the target; the gateway enumerates them)
public interface IFinancialPostingTarget
{
    string TargetCode { get; }                                   // "internal_gl" | "quickbooks"
    Task<bool> IsActiveAsync(CancellationToken ct = default);    // ambient org
    Task<TargetResult> PostAsync(FinancialTransaction tx, CancellationToken ct = default);
    Task<TargetResult> VoidAsync(VoidRequest request, CancellationToken ct = default);
    Task<TargetResult> ReverseAsync(VoidRequest request, CancellationToken ct = default);
}
public interface IEntitySyncTarget
{
    string TargetCode { get; }
    Task<bool> IsActiveAsync(CancellationToken ct = default);
    Task<EntitySyncResult> PushAsync(EntitySyncRequest request, CancellationToken ct = default);
}

// Account mapping seam (D-16) — Accounting implements, Integration reads
public interface IExternalAccountMapReader
{
    /// systemType "QUICKBOOKS_ONLINE". Returns leaf code → (externalAccountCode, name) and default key → externalAccountCode.
    Task<ExternalAccountMap> GetAsync(string systemType, CancellationToken ct = default);
    Task<IReadOnlyList<ChartAccountRef>> ListLeafAccountsAsync(CancellationToken ct = default);   // for auto-map/coverage
    Task<int> UpsertMappingsAsync(string systemType, IReadOnlyList<AccountMappingUpsert> rows, CancellationToken ct = default);
}
```
`Null*` implementations (TryAdd) for hosts/tests without the modules. Gateway dispatches to every active target; one target's failure never stops another (→ PartialFailure).

## 2. New provider-neutral document payloads — `src/SMS.Shared/Integration/QuickBooks/Payloads.cs` [QBO owns; SRC builds them]

Same style as today (ExternalId = SCM uuid, SCM codes, amounts in document currency, `ExchangeRate`/`ExchangeRateCurrencyCode` optional, `[JsonIgnore(WhenWritingNull)]` for optional fields so fingerprints are stable). Existing `CustomerPayload`, `VendorPayload`, `ItemPayload`, `SalesInvoicePayload`, `BillPayload` gain `: IAccountingDocument` (no field changes).

```csharp
PaymentPayload        { ExternalId; DocNumber; CustomerExternalId; TxnDate; CurrencyCode; decimal Amount; string Method /*CASH|CHEQUE|BANK_TRANSFER|CARD|ONLINE|MOBILE*/;
                        string? Reference; List<PaymentApplicationPayload> Applications /* InvoiceExternalId, Amount */; string? PrivateNote; ExchangeRate?; ExchangeRateCurrencyCode?;
                        string? SourceType /* "POS" when a POS tender */ }
BillPaymentPayload    { ExternalId; DocNumber; VendorExternalId; TxnDate; CurrencyCode; decimal Amount; string Method; string? Reference;
                        List<BillPaymentApplicationPayload> Applications /* BillExternalId, Amount */; string? PrivateNote; ExchangeRate?; ExchangeRateCurrencyCode? }
SalesReceiptPayload   { ExternalId; DocNumber; CustomerExternalId?; TxnDate; CurrencyCode; List<SalesInvoiceLinePayload> Lines; HeaderDiscountAmount; ExpectedTaxAmount; ExpectedTotal;
                        string Method /* tender that decides the deposit account */; string? PrivateNote; ExchangeRate?; ExchangeRateCurrencyCode?; string SourceType = "POS" }
CreditMemoPayload     { ExternalId; DocNumber; CustomerExternalId; TxnDate; CurrencyCode; List<SalesInvoiceLinePayload> Lines; HeaderDiscountAmount; ExpectedTaxAmount; ExpectedTotal;
                        string? OriginalInvoiceExternalId; bool ApplyToOriginal /* ON_ACCOUNT */; string? PrivateNote; ExchangeRate?; ExchangeRateCurrencyCode?; string? SourceType }
RefundReceiptPayload  { ExternalId; DocNumber; CustomerExternalId?; TxnDate; CurrencyCode; List<SalesInvoiceLinePayload> Lines; HeaderDiscountAmount; ExpectedTaxAmount; ExpectedTotal;
                        string Method; string? OriginalInvoiceExternalId; string? PrivateNote; ExchangeRate?; ExchangeRateCurrencyCode?; string? SourceType }
VendorCreditPayload   { ExternalId; DocNumber; VendorExternalId; TxnDate; CurrencyCode; List<BillLinePayload> Lines; ExpectedTaxAmount; ExpectedTotal; string? PrivateNote;
                        string SourceKind /* "CREDIT_NOTE" | "DEBIT_NOTE" */; ExchangeRate?; ExchangeRateCurrencyCode? }
```
`SyncKind` gains: `Payment, BillPayment, SalesReceipt, CreditMemo, RefundReceipt, VendorCredit` (string column, max 20 — no migration). `IQuickBooksGateway` gains `Upsert{Payment,BillPayment,SalesReceipt,CreditMemo,RefundReceipt,VendorCredit}Async` and `VoidAsync(SyncKind kind, string externalId)` (default interface methods returning Disabled so fakes compile).

Dependencies: Payment → customer + each applied invoice; BillPayment → vendor + each applied bill; SalesReceipt/CreditMemo/RefundReceipt → customer (if any) + items (+ original invoice when applied); VendorCredit → vendor + items.

## 3. GL posting rules [CORE in Accounting; SRC builds the lines]

Roles (A42 `DefaultKeys`): DEFAULT_RECEIVABLE, DEFAULT_PAYABLE, DEFAULT_CASH, DEFAULT_BANK, DEFAULT_REVENUE, DEFAULT_COGS, DEFAULT_TAX_RECEIVABLE, DEFAULT_TAX_PAYABLE, DEFAULT_INVENTORY, DEFAULT_PURCHASE, DEFAULT_DISCOUNT_GIVEN, DEFAULT_DISCOUNT_RECEIVED, DEFAULT_RETAINED_EARNINGS, DEFAULT_OPENING_BALANCE, DEFAULT_EXCHANGE_GAIN_LOSS, DEFAULT_ROUNDING.

| Doc | Dr | Cr |
|---|---|---|
| SalesInvoice issue | RECEIVABLE (gross) ; DISCOUNT_GIVEN (header discount) | REVENUE (net of line discounts) ; TAX_PAYABLE |
| SalesInvoice cancel | reversing journal of the issue | |
| CreditNote (POS SCN) | REVENUE ; TAX_PAYABLE | RECEIVABLE |
| Payment (customer) | CASH (CASH method) / BANK (others) ; EXCHANGE_GAIN_LOSS (loss) | RECEIVABLE ; EXCHANGE_GAIN_LOSS (gain) |
| SalesReceipt (POS cash sale) | CASH / BANK | REVENUE ; TAX_PAYABLE |
| RefundReceipt | REVENUE ; TAX_PAYABLE | CASH / BANK |
| PurchaseBill approve | INVENTORY (goods) / PURCHASE (freight, other) ; TAX_RECEIVABLE | PAYABLE |
| PurchaseBill reverse | reversing journal | |
| BillPayment | PAYABLE | BANK / CASH ; EXCHANGE_GAIN_LOSS as above |
| VendorCredit (CN / DN) | PAYABLE | PURCHASE ; TAX_RECEIVABLE |

COGS on sales is **not** posted in A44 (inventory postings deferred, D-28). Balance check: Σdebit = Σcredit within 0.001 (BR-01) → `ValidationFailed`. Journal `EntryType` = FinDocType upper snake (`SALES_INVOICE`, `PAYMENT`, …), `Reference` = SourceDocumentNumber, `SourceModule`/`SourceDocumentUuid`/`IdempotencyKey` columns added to `finance.JournalEntries` (guarded migration). Posting date must pass `IFiscalPeriodGuard` (else Pending "PERIOD_CLOSED", retried).

## 4. Routes

### Spec route → existing route (no new endpoint)
| Spec | Here |
|---|---|
| `POST /api/finance/connections/QuickBooksOnline/connect`, `/callback`, `DELETE /connections/{id}`, `POST /connections/{id}/test`, `GET /connections` | `POST api/integrations/quickbooks/connect`, `GET …/callback`, `DELETE …/connection`, `POST …/test`, `GET …/connection` |
| `GET /api/finance/sync/summary`, `/sync/logs`, `/sync/outbox`, `POST /sync/outbox/{id}/retry` | `GET api/integrations/quickbooks/sync/summary`, `/sync/items`, `/sync/items/{id}/log`, `POST /sync/items/{id}/retry` |
| `GET/POST /api/finance/mappings/tax/{provider}` | `GET/PUT api/integrations/quickbooks/tax-mappings` |
| `/api/finance/mappings/entities/*`, `/entity-sync/*/link` | `api/integrations/quickbooks/matching/*`, `POST sync/items/{id}/resolve` (linkremote) |
| `/api/finance/mappings/accounts/*` (CRUD) | A42 `api/finance/external-mappings` (existing CRUD/CSV) |

### New endpoints
| Route | Owner | Permission |
|---|---|---|
| `GET/PUT api/finance/posting/config` | CORE | GL_VIEW / GL_SETUP_MANAGE |
| `GET api/finance/posting/log?status&docType&from&to&page` ; `GET …/log/{uuid}` ; `POST …/log/{uuid}/retry` ; `POST …/log/{uuid}/skip` | CORE | GL_VIEW / GL_SETUP_MANAGE |
| `GET api/finance/posting/summary` (counts by target × status) | CORE | GL_VIEW |
| `GET api/integrations/quickbooks/sync-control` ; `PUT …/sync-control/{documentType}` `{ isEnabled, holdWhenDisabled, confirmRelease }` → `{ row, released }` ; `POST …/sync-control/{documentType}/release` | CTRL | INTEGRATION_VIEW / INTEGRATION_MANAGE |
| `GET/PUT api/integrations/quickbooks/provider-defaults` ; `GET/PUT …/document-overrides` | CTRL | same |
| `GET api/integrations/quickbooks/accounts` (A42 leaves × QBO accounts × mapping) ; `POST …/accounts/auto-map` ; `PUT …/accounts` (batch) | CTRL | same |
| `POST api/integrations/quickbooks/tax-mappings/import` | CTRL | MANAGE |
| `POST api/integrations/quickbooks/matching/{kind}/import` ; `GET …/matching/{kind}/remote?linked=false` ; `POST …/matching/{kind}/remote/{remoteId}/link` `{ externalId }` | CTRL | MANAGE / VIEW |
| `POST api/integrations/quickbooks/sync/items/{id}/unlink` ; `POST …/sync/items/retry-failed` `{ kind? }` ; `POST …/sync/items/{id}/skip` | CTRL | MANAGE |
| `GET api/integrations/quickbooks/coverage` ; `POST …/validation/check` `{ kind, externalId }` | CTRL | VIEW |

Sync summary/items/badge accept the new kinds (QBO). Frontend models follow these shapes; each owner appends exact request/response JSON under "Details" below when built.

## 5. File ownership (avoid edit collisions)
- **CORE:** `SMS.Shared/Finance/Posting/**`, `SMS.Modules.Accounting/**` (+ its migration), `tests/SMS.Modules.Accounting.Tests/**`.
- **QBO:** `SMS.Shared/Integration/QuickBooks/**`, Integration `Providers/**`, `Core/Sync/{SyncExecutor,QboObjectBuilder,SyncDependencies,SyncPayloads,InvoiceMath,PosSales,SyncOutbox}.cs` and new sync files, `Gateway/QuickBooksGateway.cs`, `Gateway/Validation/**`, new `Gateway/QuickBooksPostingTarget.cs` + `QuickBooksEntitySyncTarget.cs`, `tests/SMS.Modules.Integration.Tests` files it adds/changes in those areas.
- **CTRL:** Integration `Domain/**`, `Data/**`, `Migrations/**` (**only CTRL adds Integration migrations** — QBO asks CTRL for any column), `Core/Settings/**`, `Core/Reference/**`, `Core/Matching/**`, `Core/Sync/SyncAdminService.cs`, `Jobs/**`, `Controllers/Admin/**`, `Models/**`.
- **SRC:** `SMS.Modules.Finance/**` (Integration folder publishers/sources, services' after-commit hooks), `SMS.Modules.Suppliers/Integration/**` + hooks, `SMS.Modules.Inventory/Integration/**` + hooks, `SMS.Modules.Pos/**` only if needed, their tests.
- **FE:** `SupplyChainFrontend/**`.
- **QA:** `tests/SMS.Integration.Tests/FinancialPosting/**`, `tests/SMS.Integration.Tests/QuickBooks/FakeQuickBooks.cs` (coordinate with QBO), docs QA sections.
- Shared DI wiring in `src/SMS.API/Program.cs`: CORE only.

## Details (appended by owners)

### CORE — `api/finance/posting/*` (Accounting, `PostingController`)
All responses use the ApiResponse envelope `{ success, message, result }`, camelCase, enums/statuses as strings, dates `yyyy-MM-dd`, timestamps ISO UTC.
Gate (D-40): `[RequiresFeature(FEATURE_GENERAL_LEDGER, MODULE_INTEGRATION)]` (either opens it). Permissions (D-41): config GET = `GL_VIEW|GL_ACCOUNT_MANAGE|GL_SETUP_MANAGE`, config PUT = `GL_SETUP_MANAGE`; log/summary GET = `GL_VIEW|GL_ACCOUNT_MANAGE|GL_SETUP_MANAGE|INTEGRATION_VIEW|INTEGRATION_MANAGE`, retry/skip = `GL_SETUP_MANAGE|INTEGRATION_MANAGE` to enter, then **per target** (REV-18): `internal_gl` only with `GL_SETUP_MANAGE`, `quickbooks` (any external target) only with `INTEGRATION_MANAGE`.

Target statuses (`targets[].status`): `Pending` (recorded, not yet answered) · `Posted` · `Queued` (external outbox has it) · `Held` (sync control hold) · `Failed` (will be retried; `nextAttemptAt`) · `DeadLetter` (retries exhausted) · `Skipped` (target not applicable / dismissed; reason in `lastError`). Posting `kind`: `Post` · `Void` · `Reverse`.
`overallStatus` of a posting = the worst of its targets in this order: DeadLetter > Failed > Pending > Held > Queued > Posted > Skipped (no target rows → `Skipped`).

**GET `api/finance/posting/config`** →
```json
{ "success": true, "message": "Request successful", "result": {
  "internalAccountingEnabled": false, "autoPostToGL": true, "defaultCurrencyCode": "PKR", "fiscalYearStartMonth": 1,
  "glFeatureEnabled": true, "setupStatus": "Complete", "glReady": true, "glActive": false,
  "notReadyReason": null, "modifiedAt": "2026-10-16T09:00:00Z" } }
```
`glReady` = feature on and setup Complete; `glActive` = glReady and internalAccountingEnabled (the GL target takes postings). `notReadyReason` ∈ `null | "FEATURE_OFF" | "SETUP_INCOMPLETE"`. `defaultCurrencyCode` is read-only: the stored value, else the org's sale base, else the current fiscal year's reporting currency (may be null). GET never creates a row: `modifiedAt` is null until the first PUT (defaults shown).

**PUT `api/finance/posting/config`** body `{ "internalAccountingEnabled": true, "autoPostToGL": false, "fiscalYearStartMonth": 1 }` (all optional; omitted = keep) → same model, message "Posting settings saved.". `fiscalYearStartMonth` outside 1..12 → 400 "The fiscal year start month must be between 1 and 12.". Enabling while the setup is not Complete is allowed (saved; `glActive` stays false, `notReadyReason` says why).

**GET `api/finance/posting/log?status=&targetCode=&docType=&sourceModule=&kind=&from=&to=&search=&page=1&pageSize=25`** — `status` = a target status (matches postings having any target in it; `targetCode` narrows which target), `docType` = FinDocType name, `from/to` on `transactionDate`, `search` in number / partner / key, `pageSize` ≤ 200, newest `modifiedAt` first →
```json
{ "success": true, "result": { "data": [ {
    "uuid": "6f1c…", "idempotencyKey": "finance:SalesInvoice:2b9e…", "kind": "Post", "documentType": "SalesInvoice",
    "sourceModule": "finance", "sourceDocumentUuid": "2b9e…", "sourceDocumentNumber": "SI-2026-00012", "transactionDate": "2026-10-15",
    "partnerUuid": "…", "partnerName": "Acme", "currencyCode": "PKR", "totalAmount": 11800.0, "version": 1,
    "overallStatus": "Failed", "createdAt": "…", "modifiedAt": "…",
    "targets": [
      { "targetCode": "internal_gl", "status": "Failed", "attempts": 1, "nextAttemptAt": "…", "lastAttemptAt": "…",
        "lastError": "PERIOD_CLOSED: 2026-10-15 is not in an open fiscal period.", "internalId": null, "externalId": null, "postedAt": null },
      { "targetCode": "quickbooks", "status": "Queued", "attempts": 0, "nextAttemptAt": null, "lastAttemptAt": "…",
        "lastError": null, "internalId": "42", "externalId": null, "externalDocNumber": null, "mirroredFromTarget": false, "postedAt": "…", "canAct": null } ] } ],
  "totalRecords": 1, "page": 1, "pageSize": 25, "totalPages": 1, "hasNext": false, "hasPrevious": false } }
```
`totalAmount` = Σ debit of the lines (document currency); `version` = 1 + number of updates (changed payload, D-13).

**GET `api/finance/posting/log/{uuid}`** → the list item plus
```json
{ "description": "…", "exchangeRate": null, "partnerType": "Customer", "documentPayloadType": "SalesInvoicePayload",
  "lines": [ { "lineNumber": 1, "accountRole": "DEFAULT_RECEIVABLE", "accountCode": null, "debit": 11800.0, "credit": 0, "label": "…", "taxCode": null, "taxAmount": null } ],
  "journals": [ { "uuid": "…", "entryNumber": "JE-2026-00007", "entryType": "SALES_INVOICE", "entryDate": "2026-10-15", "status": "Posted",
                  "totalDebit": 11800.0, "totalCredit": 11800.0, "currencyCode": "PKR", "reversesEntryNumber": null, "isReversed": false,
                  "reversesJournalNumber": null, "reversedByJournalNumber": "JE-2026-00009",
                  "lines": [ { "lineNumber": 1, "accountCode": "1113", "accountName": "Accounts Receivable", "description": "Invoice SI-2026-00012", "debit": 11800.0, "credit": 0 },
                             { "lineNumber": 2, "accountCode": "4111", "accountName": "Sales", "description": "Invoice SI-2026-00012", "debit": 0, "credit": 10000.0 } ] } ] }
```
`journals[].lines` are in base currency (detail only; D-36). `reversesJournalNumber` / `reversedByJournalNumber` link reversals (`reversesEntryNumber` / `isReversed` kept). `journals` = every GL journal of the document (its posting key, its `:void` key and update reversals), oldest first. 404 "Posting not found." for another org's uuid.

**GET `api/finance/posting/by-source/{sourceUuid}`** (D-33 / D-49a; read permission as the log) → the postings of one SCM document, Post first then its Void/Reverse; `[]` when the document never reached the gateway (never 404):
```json
{ "success": true, "message": "Request successful", "result": [
  { "uuid": "6f1c…", "idempotencyKey": "finance:SalesInvoice:2b9e…", "kind": "Post", "documentType": "SalesInvoice", "sourceModule": "finance",
    "sourceDocumentUuid": "2b9e…", "sourceDocumentNumber": "SI-2026-00012", "transactionDate": "2026-10-15", "partnerUuid": "…", "partnerName": "Acme",
    "currencyCode": "PKR", "totalAmount": 11800.0, "version": 1, "overallStatus": "Failed", "createdAt": "…", "modifiedAt": "…",
    "targets": [
      { "targetCode": "internal_gl", "status": "Posted", "attempts": 0, "nextAttemptAt": null, "lastAttemptAt": "…", "lastError": null,
        "internalId": "JE-2026-00007", "externalId": null, "postedAt": "…", "canAct": false },
      { "targetCode": "quickbooks", "status": "Failed", "attempts": 2, "nextAttemptAt": "…", "lastAttemptAt": "…", "lastError": "…",
        "internalId": null, "externalId": null, "postedAt": null, "canAct": true } ] } ] }
```
`canAct` = the target is open (Pending / Failed / DeadLetter) and the caller holds its manage code (`internal_gl` → GL_SETUP_MANAGE, external → INTEGRATION_MANAGE). It is `null` in the list/detail responses. Retry each `canAct` target with `POST log/{uuid}/retry` `{ "targetCode": "…" }`.
**POST `api/finance/posting/log/{uuid}/retry`** body `{ "targetCode": "internal_gl" }` (optional; omitted = every Failed / DeadLetter / Pending target) → the detail model, message "Retried." — or, when open targets were left out for lack of permission, "Retried: internal_gl. Not retried (no permission): quickbooks.". The targets acted on are reset (attempts 0) and dispatched immediately. A named target the caller may not touch, or open targets of which none may be touched → 403 "You may not retry or skip any target of this posting." (`{ success: false, message, result: null }`); FE sending `{}` keeps working. Nothing retryable → 409 "Nothing to retry: no target of this posting is failed.".
**POST `api/finance/posting/log/{uuid}/skip`** body `{ "targetCode": "quickbooks", "reason": "Entered by hand in QuickBooks" }` (both optional) → the detail model, message "Skipped." (or "Skipped: quickbooks. Not skipped (no permission): internal_gl."); same per-target rule and 403 as retry. Failed / DeadLetter / Pending targets become `Skipped`, `lastError` = "Skipped by {user}: {reason}". Nothing skippable → 409 "Nothing to skip: no target of this posting is failed or pending.".

**GET `api/finance/posting/summary`** →
```json
{ "success": true, "result": {
  "targets": [ { "targetCode": "internal_gl", "pending": 0, "posted": 12, "queued": 0, "held": 0, "failed": 1, "deadLetter": 0, "skipped": 3, "total": 16 },
               { "targetCode": "quickbooks", "pending": 0, "posted": 0, "queued": 15, "held": 1, "failed": 0, "deadLetter": 0, "skipped": 0, "total": 16 } ],
  "postings": 16, "needsAttention": 1, "lastPostingAt": "2026-10-16T08:59:00Z" } }
```
`needsAttention` = postings with any Failed or DeadLetter target.

### QBO ↔ QBO-SDK handover
QBO already built (builds green): Remote* DTOs in `Core/Providers/AccountingProviderContracts.cs` — `RemoteSalesDocument` (base) → `RemoteSalesReceipt{DepositToAccountId,PaymentMethodId}`, `RemoteCreditMemo{ApplyToInvoiceId,ApplyAmount}`, `RemoteRefundReceipt{RefundFromAccountId,PaymentMethodId}`; `RemotePayment{CustomerId,ReferenceNumber,TxnDate,CurrencyCode,ExchangeRate,TotalAmount,Lines(InvoiceId,Amount),DepositToAccountId,PaymentMethodId,PrivateNote}`; `RemoteBillPayment{VendorId,DocNumber,…,TotalAmount,PayType Check|CreditCard,AccountId,Lines(BillId,Amount),DepartmentId}`; `RemoteVendorCredit{VendorId,DocNumber,…,Lines:RemoteBillLine,DepartmentId}`; Class/Department/SalesTerm ids on RemoteInvoice, DepartmentId on RemoteBill, ClassId on RemoteBillLine; `RemoteRecord.Note` (init); `RemoteLookup.CustomerRemoteId`/`TxnDate`; `IAccountingProvider.VoidAsync(ctx, SyncKind kind, remoteId, syncToken)` (default method: invoices only, else Refused `VOID_UNSUPPORTED`) + const `VoidUnsupportedCode`. Mappers in ONE file `Providers/QuickBooks/Mapping/QboDocumentMappers.cs`: `QboSalesDocumentMapper.ToSdk(RemoteSalesReceipt|RemoteCreditMemo|RemoteRefundReceipt)` + `ToRecord(SalesTransaction, what)`, `QboPaymentMapper.ToSdk(RemotePayment|RemoteBillPayment)` + `CreditApplication(customerId, invoiceId, creditMemoId, amount, txnDate, currency, rate, reference)` + `ToRecord`, `QboVendorCreditMapper.ToSdk/ToRecord`; `QboEntityMapper` dispatch (ToSdk/NewOfKind/ToRecord) covers all 11 kinds; invoice `SalesLine`/`DiscountLine` and bill `ToLine` made internal for reuse. Provider (`QuickBooksAccountingProvider.cs`): Create (CreditMemo with ApplyToInvoiceId → create + zero-total application Payment, failure → `RemoteRecord.Note`), `VoidAsync` (Invoice/Payment/SalesReceipt void; BillPayment/RefundReceipt **delete** via new `IQboClient.Delete` (default-method, SdkQboClient implements); CreditMemo/VendorCredit refused), Find (Payment: `QboQueryBuilder.FindPaymentsOn(date)` then filter PaymentRefNum + customer; others `FindTransactionByDocNumber(kind, doc)` + customer/vendor filter), deep links. **Left for QBO-SDK** (please build on the above, don't duplicate; split the one mapper file into per-entity files if you prefer, keeping the class/method names QBO calls): mapper unit tests (`tests/SMS.Modules.Integration.Tests/QuickBooks/Qbo*MapperTests.cs`), SDK wire-format/loopback checks of the 6 new objects (e.g. Payment LinkedTxn serialisation, BillPayment Check/CreditCard choice, Delete for BillPayment/RefundReceipt actually reaching `?operation=delete`), `QboErrorTranslator` codes specific to the new objects (e.g. credit application exceeding invoice balance), and any SDK quirks you find (please report as D-lines). QBO calls only: `QboEntityMapper.*`, `IAccountingProvider.Create/Update/GetById/Find/VoidAsync`.

### QBO-SDK — what was verified and changed (D-56..D-59)
- **Files:** `QboDocumentMappers.cs` split into `Mapping/QboSalesDocumentMapper.cs`, `Mapping/QboPaymentMapper.cs`, `Mapping/QboVendorCreditMapper.cs` (class/method names and output unchanged); the applied-credit-memo create and `VoidAsync` moved to `QuickBooksAccountingProvider.Documents.cs` (class `partial`).
- **Fix (D-56):** Payment/SalesReceipt void now sends `{"Id","SyncToken","sparse":true}` to `?operation=update&include=void` (the SDK sent only Id + SyncToken — a non-sparse update). Invoice void unchanged (`?operation=void`, Id + SyncToken).
- **Added (D-58):** `RemoteSalesLine.ClassId` (optional) → `SalesItemLineDetail.ClassRef`. Null = not sent: invoice/receipt/memo/refund bodies are unchanged unless the builder sets it (use it when the company tracks classes per line).
- **Wire shapes (proven on loopback with the real SDK):**

  | Object | Resource | Key body facts |
  |---|---|---|
  | Payment | `POST payment` | `TotalAmt` = whole receipt; `Line[{Amount, LinkedTxn[{TxnId, TxnType:"Invoice"}]}]`; `PaymentRefNum`; `DepositToAccountRef`, `PaymentMethodRef` |
  | Credit application | `POST payment` (after `creditmemo`) | `TotalAmt:0`; two lines, Invoice + CreditMemo, each = min(QBO memo `TotalAmt`, `ApplyAmount`) (QBO total when ApplyAmount is 0) |
  | BillPayment | `POST billpayment` | `PayType` + `CheckPayment.BankAccountRef` or `CreditCardPayment.CCAccountRef`; `Line[{Amount, LinkedTxn[{TxnType:"Bill"}]}]` |
  | SalesReceipt / CreditMemo / RefundReceipt | `POST salesreceipt` / `creditmemo` / `refundreceipt` | invoice lines byte-identical; header `ClassRef`/`DepartmentRef`; receipt and refund `DepositToAccountRef` (refund = the account paid from) |
  | VendorCredit | `POST vendorcredit` | bill lines byte-identical (account/item based, class on the line) |
  | Void | Payment/SalesReceipt `?operation=update&include=void` · BillPayment/RefundReceipt `?operation=delete` · CreditMemo/VendorCredit/Bill refused without a call |

- **Errors:** no new translator codes needed — 6000 (e.g. over the open balance) → Refused; 610 on create → Refused (field from `element`, e.g. LinkedTxn); 6140 → Duplicate; 2020 missing DepositToAccountRef → Refused with that field; 5010 on void → StaleObject; 610 on void/delete → NotFound.
- **Recovery (D-57):** the SDK adds a new random `requestid` per call, so Intuit never de-duplicates a retried create; Find-before-retry is the guard (all six kinds covered).

### QBO — payloads, gateway behaviour, targets (D-50..D-55)
**Payload JSON** (stored canonical form = camelCase, decimals without trailing zeros, enums as names, optional fields omitted when null — so fingerprints are stable; field set exactly as `SMS.Shared/Integration/QuickBooks/DocumentPayloads.cs`, unchanged from §2):
```json
{ "externalId":"6f1c…","docNumber":"RCPT-2026-0001","customerExternalId":"2b9e…","txnDate":"2026-10-15T00:00:00","currencyCode":"PKR",
  "amount":234,"method":"CASH","reference":"CHQ 1234","applications":[{"invoiceExternalId":"9a0d…","amount":234}],"sourceType":"POS" }
```
- `method` must be one of CASH, CHEQUE, BANK_TRANSFER, CARD, ONLINE, MOBILE. STORE_CREDIT/EXCHANGE are refused (`PAYMENT_METHOD_INVALID`) — **SRC must not send them** (D-19).
- `docNumber` ≤ 21 chars (QBO). Payment → `PaymentRefNum`.
- Σ`applications.amount` ≤ `amount` (else `APPLICATIONS_EXCEED_AMOUNT`). The remainder is unapplied credit in QBO; the map gets a warning.
- Same `invoiceExternalId` twice → merged into one line.
- `BillPaymentPayload` — same rules with `vendorExternalId` and `billExternalId`. No `applications` = an advance. Needs a bank account: `BankAccountRef.{METHOD}` default, else the A42 DEFAULT_BANK / DEFAULT_CASH mapping. Otherwise Blocked `BANK_ACCOUNT_MISSING`, re-validated automatically.
- Sales documents (`SalesReceiptPayload`, `CreditMemoPayload`, `RefundReceiptPayload`):
  - lines and totals checked exactly like an invoice (`InvoiceMath`; `TOTAL_MISMATCH`, tax mapping, discount account);
  - `customerExternalId` null → the "POS Walk-in" customer (D-54);
  - `sourceType:"POS"` → sync-control type POSSale (receipt) / POSReturn (memo, refund). Otherwise SalesInvoice / CreditNote. A SalesReceipt is always POSSale;
  - CreditMemo `applyToOriginal:true` needs `originalInvoiceExternalId` (`ORIGINAL_INVOICE_REQUIRED`) and waits for that invoice to be in QBO.
- `VendorCreditPayload` — bill-like line and total rules; positive amounts. Un-itemised lines → DefaultExpenseAccountId, else the DEFAULT_PURCHASE mapping.
- **ExternalId** = the SCM row's `Uuid.ToString()` (lowercase "D"). The posting target's Void/Reverse look up maps by `VoidRequest.SourceDocumentId.ToString()`, so the payload ExternalId **must** be that same string.

**Gateway answers** (`IQuickBooksGateway`, unchanged envelope `GatewayResult`):
- `Disabled` = type switched off (Skip), NotConnected, or before the start date.
- `Accepted(NotSynced)` with map `lastErrorCode` `HELD` / `POS_SALES_HELD` = held.
- `WaitingOnDependency` = customer / vendor / items / invoices / bills not in QBO yet (requested from the SCM sources; Finance's source must serve `SalesInvoice` and `Bill` by uuid — it already does).
- `VoidAsync(kind, id)`:
  - Invoice, Payment, SalesReceipt → void;
  - BillPayment, RefundReceipt → delete;
  - CreditMemo, VendorCredit, Bill → `Accepted(NeedsResolution)` flag for the accountant;
  - never sent → `Accepted(Voided)`.

**Targets** (registered in `AddIntegrationGateway`; `IEnumerable<IFinancialPostingTarget>` / `IEnumerable<IEntitySyncTarget>` members, TargetCode `quickbooks`):
- Post: dispatches on the runtime type of `Metadata.Document` (any of the 11 payloads). No document → Skipped `NO_DOCUMENT`.
- Result → status:

  | Result | Status |
  |---|---|
  | Synced | Posted |
  | Pending / Waiting | Queued |
  | held | Held |
  | Disabled | Skipped `DISABLED` |
  | NotConnected | Skipped `NOT_CONNECTED` |
  | Invalid | Failed `VALIDATION: …` |
  | voided / flagged | Skipped |

  `InternalId` = payload ExternalId; `ExternalId` = QBO id when known.
- Void: kind from the existing map (POSSale: SalesReceipt, then SalesInvoice; POSReturn: RefundReceipt, then CreditMemo; CreditNote: CreditMemo, then RefundReceipt). No map → Skipped `NEVER_SYNCED`. Done → Posted (`Error` = "FLAGGED_FOR_ACCOUNTANT: …" for non-voidable kinds).
- Reverse: flag only → Posted with `Error` "FLAGGED_FOR_ACCOUNTANT: …".
- Entity sync results:

  | Gateway answer | Outcome |
  |---|---|
  | Accepted | Queued (`TargetsQueued` 0 when out of scope) |
  | Synced | DuplicateSkipped |
  | held | Held |
  | NotConnected / Disabled | NoTargetConfigured |
  | Invalid | ValidationFailed |
  | crash / unexpected | Failed |

  Deactivate / Delete → payload sent with `isActive:false`.
- Hold/release does **not** depend on the posting gateway re-dispatching (REV-3): release is `IHeldRecordReleaser` (re-ask the source / re-admit the stored payload).

**For CTRL:**
- Register `ISyncControlReader` / `IProviderDefaultsReader` with `AddScoped` (my fallbacks are TryAdd in `AddIntegrationGateway`).
- `IProviderDefaultsReader.GetAsync` must omit `ClassRef` / `DepartmentRef` when the company's preferences do not track classes / locations (D-55).
- Keys used: `ClassRef`, `DepartmentRef`, `SalesTermRef`, `PosDefaultCustomerRef`, `PaymentMethodRef.{M}`, `DepositToAccountRef.{M}`, `BankAccountRef.{M}` (CARD = credit-card account), `RefundFromAccountRef[.{M}]`.
- Hold codes are `SyncHolds.HeldCode` ("HELD") and `PosSales.HeldCode`. Use `SyncHolds.IsHeldCode(code)` in the dashboard/admin code instead of comparing with `PosSales.HeldCode`.
- `PayloadValidator.BankAccountMissingCode` is re-validatable: call `BlockedRecordRevalidator.AfterChangeAsync` after saving defaults.
- No column needed.

**For QA (FakeQuickBooks):** `tests/SMS.Integration.Tests/QuickBooks/FakeQuickBooks.cs` gains:
- `VoidAsync(kind, …)` — Void for Payment / SalesReceipt / Invoice, delete for BillPayment / RefundReceipt (record `Active=false`, call recorded as "Void"), Refused `VOID_UNSUPPORTED` for others;
- names (= DocNumber / PaymentRefNum) for all new entities, so DocNumber duplicates and Find work;
- `Records(SyncKind.Payment)` etc. with the `RemoteEntity` built (assert LinkedTxn, accounts, PayType there).

### SRC ↔ SRC-MD handover
SRC changed nothing in SMS.Modules.Suppliers or SMS.Modules.Inventory (nor their tests) before the split; SRC-MD owns them from scratch (D-76..D-79). Finance registers its own `NullFinancialPostingGateway` TryAdd; it does not use `IEntitySyncGateway`.

### SRC — Finance callers (D-70..D-75)
**What Finance sends** (`src/SMS.Modules.Finance/Integration/Posting/`):

| Trigger (after commit) | Call | DocumentType / module | `Metadata.Document` |
|---|---|---|---|
| Sales invoice issued (back office) | PostAsync | SalesInvoice / finance | `SalesInvoicePayload` (unchanged builder) |
| Sales invoice cancelled | VoidAsync | SalesInvoice / finance | — |
| POS sale, all tenders cash-like = total | PostAsync | SalesReceipt / pos | `SalesReceiptPayload` (`sourceType:"POS"`, method = largest tender) |
| POS sale, otherwise | PostAsync ×(1 + cash-like tenders) | SalesInvoice / pos + Payment / pos | `SalesInvoicePayload` (`sourceType:"POS"`) + `PaymentPayload` (`sourceType:"POS"`) |
| POS return | IssueCreditAsync | RefundReceipt / pos or CreditNote / pos | `RefundReceiptPayload` / `CreditMemoPayload` |
| POS void | VoidAsync | SalesReceipt / pos, or SalesInvoice / pos + each cash-like Payment / pos | — |
| Customer payment recorded / allocated (same key, changed payload) | PostAsync | Payment / finance | `PaymentPayload` |
| Customer payment bounced (POS reversals: via the POS void) | VoidAsync | Payment / finance or pos | — |
| Supplier invoice approved / bill-field edit | PostAsync | PurchaseBill / finance | `BillPayload` (unchanged builder) |
| Supplier invoice reversed | ReverseAsync (`Reason` = accountant note) | PurchaseBill / finance | — |
| Supplier payment POSTED | PostAsync | BillPayment / finance | `BillPaymentPayload` |
| Supplier payment BOUNCED (or CANCELLED after posting) | VoidAsync | BillPayment / finance | — |
| Supplier credit note created; debit note created (ISSUED) | IssueCreditAsync | VendorCredit / finance | `VendorCreditPayload` (`sourceKind` CREDIT_NOTE / DEBIT_NOTE) |
| Debit note WRITTEN_OFF | ReverseAsync (`Reason` = accountant note) | VendorCredit / finance | — |

Keys follow D-13 (`{module}:{type}:{uuid}`, `…:void`). The payload `externalId` = `SourceDocumentId.ToString()` everywhere.

**Requests to QBO (not edited by SRC — your files):**
- **Void / Reverse by explicit type.** SRC's VoidRequest.DocumentType is the exact type the document was posted under: SalesReceipt, SalesInvoice, CreditNote, RefundReceipt, Payment, BillPayment, VendorCredit, PurchaseBill. It is never POSSale/POSReturn. Please map with FinDocType→SyncKind as listed in D-71 (your map-lookup fallback is fine on top).
- **`Metadata.Properties["FlagForAccountant"]`** (mixed POS refund): after the CreditMemo upsert is admitted, flag the map for the accountant with this text. Do this without dropping the queued upsert (unlike `FlagForAccountantAsync` today), e.g. as a warning/NeedsResolution after the send. This is the only way D-23 "mixed → flag" reaches QuickBooks; nothing in the payload carries it.
- **`Metadata.Properties["AppliedAmount"]`** (invariant "0.00") on a CreditMemo with `applyToOriginal:true`: this is the part of the memo SCM applied to the original invoice (`SalesInvoice.CreditedAmount`). It can be less than the memo total when the invoice's open balance was smaller; the rest stays as customer credit. D-53 applies the memo's whole `TotalAmt`, which QuickBooks refuses (6000) when that exceeds the balance. Please apply `min(AppliedAmount, open balance)`. Alternatively add an `AppliedAmount` field to `CreditMemoPayload`, and SRC will fill it.
- **VendorCredit `currencyCode`** can be the org's purchase base or `""` when the host cannot tell (a note with no invoice and no currency service). Please treat `""` as the vendor's / home currency.
- **POS walk-in:** SRC always sends the POS customer partner's uuid (the walk-in customer is a real partner in SCM). `customerExternalId` is never null.

**For CORE:** register the real `IFinancialPostingGateway` so it **replaces** Finance's TryAdd `NullFinancialPostingGateway`, whatever the module order. While Null is resolved, Finance sends straight to QuickBooks (D-70) and nothing reaches the GL. Credit types arrive through `IssueCreditAsync`. Payment allocation re-posts the same key with a changed payload: an update, D-13.

**Bounced till tender of a sales receipt (REV-15):**
- SRC posts `PostAsync(Payment / pos)` under the tender's key with **no `Metadata.Document`**: Dr RECEIVABLE / Cr BANK, GL only.
- CORE: accept a posting without a document.
- QBO: the target answers Skipped `NO_DOCUMENT` (as specified).
- SRC also calls `IQuickBooksGateway.FlagForAccountantAsync(SalesReceipt, saleUuid, reason)`, but only when that receipt's status is Synced or NeedsResolution.

**Gateway semantics callers can rely on (CORE, D-42..D-48).** Call after your commit; the CancellationToken is ignored. Same key + same payload: the GL is not posted again; **external targets are called again every time** (D-44) — so a re-send through the gateway reaches QuickBooks' own dedupe/release logic as before. DuplicateSkipped only when nothing was called. A void (…:void) skips the original's still-open target rows and blocks any later post of that document (D-45). EntitySyncOutcome.Failed (new) = exception/unexpected error; ValidationFailed = refused request (D-48). Target lastError codes: see D-43.
**QBO addendum (SRC requests, 2026-10-16):**
- New optional payload fields, omitted when null, so fingerprints are unchanged:
  - `SalesDocumentPayload.flagForAccountant` (string): the doc is sent as usual, then flagged `ACCOUNTANT_ACTION_NEEDED` with that reason once QBO has it.
  - `CreditMemoPayload.appliedAmount` (decimal): the amount applied to the original invoice, capped at the memo total.
- The QuickBooks target fills both from `Metadata.Properties["FlagForAccountant"]` and `["AppliedAmount"]` (invariant decimal), so SRC needs no change.
- VendorCredit `currencyCode: ""` → no CurrencyRef is sent (QBO uses the vendor's currency).
- Void kinds per FinDocType are as SRC listed (CreditNote → CreditMemo, then RefundReceipt).

### CTRL — sync control, defaults, accounts, tax import, import/link, dead letter, coverage (D-60..D-69)
All under `api/integrations/quickbooks`, ApiResponse envelope (`{ success, message, result }`), camelCase, VIEW for GET / `coverage` / `validation/check`, MANAGE for the rest. Models: `src/SMS.Modules.Integration/Models/PostingAdminModels.cs` (FE mirror it). 409s that carry data return `{ success:false, message, result:<detail> }`.

**Sync control (D-14)**
- `GET sync-control` → `{ rows:[{ documentType, displayLabel, isEnabled, holdWhenDisabled, action:"Process|Hold|Skip", heldCount, isDefault, modifiedBy, modifiedAt }], totalHeld }`. There are 11 rows, in `SyncControlDocumentTypes.All` order. `isDefault:true` means no row is stored and the value comes from the old switch (D-67).
- `PUT sync-control/{documentType}` (any case) with body `{ isEnabled, holdWhenDisabled, confirmRelease }` → `{ row, released, heldNow }`.
  - Turning a type on while it has held records and `confirmRelease:false` → **409**, `result:{ documentType, heldCount }`, nothing changed.
  - Turning it off with hold → queued-but-unsent records are held (`heldNow`).
  - Turning it on → held records are released (`released`).
  - The matching `autoPush*` switch is written too (R-1).
- `POST sync-control/{documentType}/release` → `{ documentType, released, stillHeld }`. Returns 409 while the type is off.
- `GET/PUT settings` behave as before. PUT writes **only the switches it changed** onto their rows: `autoPushPosSales`/`autoPushSalesInvoices` → POSSale + POSReturn = pos && invoices. GET gains `effectiveAccounts:{ incomeAccountId, incomeSource:"Settings|GeneralLedger|null", expense…, freightExpense…, discount… }`.

**Provider defaults (D-15)**
- `GET provider-defaults` → `{ classTrackingEnabled, departmentTrackingEnabled, departmentTerminology, documentTypes:[…8 document types], defaults:[{ settingKey, baseKey, method, label, referenceKind:"Class|Department|Term|PaymentMethod|Account|Customer", accountTypes:[…], settingValue, valueName, description, warning, modifiedBy, modifiedAt }] }`. There are 29 definitions: ClassRef, DepartmentRef, SalesTermRef, RefundFromAccountRef, PosDefaultCustomerRef, plus `{PaymentMethodRef|DepositToAccountRef|BankAccountRef|RefundFromAccountRef}.{CASH|CHEQUE|BANK_TRANSFER|CARD|ONLINE|MOBILE}`.
- `PUT provider-defaults` with body `{ values:[{ settingKey, settingValue, description? }] }`. An empty value removes the key, and keys not sent are left alone. The response is the GET model.
  - 400 if the id is not in the reference snapshot, is inactive, or the account type is wrong. Accepted account types: DepositTo = Bank / Other Current Asset; BankAccount = Bank / Credit Card; RefundFrom = Bank / Other Current Asset / Credit Card.
  - The walk-in customer must be an imported or linked QuickBooks customer.
  - After a save, Blocked records are re-validated.
- `GET document-overrides` → `[{ documentType, settingKey, settingValue, valueName, warning, modifiedBy, modifiedAt }]`.
- `PUT document-overrides` with body `{ overrides:[{ documentType, settingKey, settingValue }] }`. It works the same way as the defaults PUT. Customer, Vendor and Item are refused.
- Reader (`IProviderDefaultsReader`): returns the defaults, with overrides for that type winning. **ClassRef and DepartmentRef are returned only while the preferences say the company tracks them** (D-65).
- `GET reference` gains `classes`, `departments`, `paymentMethods` (ReferenceItemModel with `fullName`, `type`), accounts' `acctNum`, and `tracking:{ classTrackingPerTransaction, classTrackingPerLine, departmentTracking, departmentTerminology }`. These are empty until the next reference refresh.

**Accounts (D-16)**
- `GET accounts` → `{ glAvailable, referenceFetchedAt, totalAccounts, mapped, unmapped, invalid, accounts:[{ accountUuid, code, name, accountType, isActive, qboAccountId, qboAccountName, qboAccountType, status:"Mapped|Unmapped|Invalid", defaultKeys:[…] }], qboAccounts:[{ id, name, acctNum, type, subType, active, mappedCount }], defaultRoles:[{ defaultKey, qboAccountId, qboAccountName, usedFor }] }`.
- `POST accounts/auto-map` with body `{ overwrite:false, includeInactive:false }` (optional) → `{ mapped, matches:[{ accountUuid, code, name, qboAccountId, qboAccountName, rule:"AcctNum|ExactName|NormalizedName" }], ambiguous:[{ accountUuid, code, name, rule, candidates:[QboAccountModel] }], unmatched, accounts:<GET model> }`.
  - A rule is used only when exactly one active QBO account matches.
  - Existing mappings are never touched unless `overwrite` is set.
  - Writes go through `IExternalAccountMapReader.UpsertMappingsAsync`, with note `AUTO_MAPPED:{rule}`.
- `PUT accounts` with body `{ mappings:[{ accountUuid, qboAccountId|null }] }` → GET model. 400 for an unknown leaf, or an unknown or inactive QBO account. 409 when there is no GL chart.
- `IEffectiveAccountsResolver.ResolveAsync(IntegrationSettings)` (internal, Core/Settings/EffectiveAccounts.cs) is for the builder and preflight. Each account is the settings value, else the A42 mapping:
  - income ← DEFAULT_REVENUE
  - expense ← DEFAULT_COGS, then DEFAULT_PURCHASE
  - freight ← DEFAULT_PURCHASE, else the effective expense
  - discount ← DEFAULT_DISCOUNT_GIVEN
  - A mapped account is used only if it is active and of the right type in the snapshot.

**Tax (D-18)**
- `POST tax-mappings/import` (no body) → `{ saved, alreadyMapped, savedRows:[TaxImportRow], needsReview:[TaxImportRow], mappings:<GET tax-mappings> }`.
- TaxImportRow = `{ sourceTaxCode, sourceTaxCodeName, taxPercent, qboTaxCodeId, qboTaxCodeName, reason, candidates:[ReferenceItemModel] }`.
- Unambiguous rows are saved through the normal save. Existing mappings are never changed.

**Import / link (D-17)**
- `POST matching/{kind}/import` (kind: customers|Customer, vendors, items) → `{ kind, read, added, updated, removed, linked, unlinked, importedAt }`. Nothing is created in SCM or in QBO.
- `GET matching/{kind}/remote?linked=false&search=&page=1&pageSize=50` → `PaginatedResponse<{ remoteId, kind, name, companyName, email, taxId, accountNumber, sku, currencyCode, active, importedAt, linked, linkedItemId, linkedExternalId, linkedLabel, deepLink }>`.
- `POST matching/{kind}/remote/{remoteId}/link` with body `{ externalId }` → that row, `linked:true`.
  - The link works like matching's Link: Adopted, Synced, not pushed.
  - If the gateway doesn't have the SCM record yet, it is asked for first.
  - 400 if either side is already linked; 404 if the record is unknown.
- `POST sync/items/{id}/unlink` (master data only) → SyncItemModel (`state:"NotSynced"`). The QBO record is kept; ledger claims and match proposals are dropped.
  - **409** with `result:{ openDocuments, examples:[…] }` while synced, non-voided documents reference the record.

**Dead letter (D-11)**
- `GET sync/items?deadLetter=true` returns Failed + NeedsResolution only.
- `POST sync/items/retry-failed` with body `{ kind? }` → `{ found, requeued, skipped:[{ id, kind, displayLabel, reason }] }`. At most 500 per call. Records skipped in matching, flagged for the accountant, or still held are skipped, with the reason.
- `POST sync/items/{id}/skip` → SyncItemModel. Only Failed / NeedsResolution / Blocked records can be skipped (400 otherwise). The result is `lastErrorCode:"DISMISSED"` and `state` NotSynced, Synced or Voided. Nothing is sent until the data changes.

**Coverage & validation (D-27)**
- `GET coverage` → `{ ready, connectionStatus, mode, referenceFetchedAt, entities:[{ kind, total, mapped, skipped, pending, failed, remoteImported, remoteUnlinked, percentMapped }], taxCodes:{ total, mapped, unmapped:[…] }, accounts:{ glAvailable, total, mapped, unmapped, blocking:false }, providerDefaults:{ required, set, missing:[Issue] }, blockingIssues:[Issue], warnings:[Issue] }`.
  - Issue = `{ code, area, message, documentType, tab:"connection|setup|sync|sync-control|defaults|accounts|tax|match" }`.
  - Blocking: not connected, preflight Fail, `TAX_UNMAPPED` (a code used by stored documents), `BANK_ACCOUNTS` (bill payments on and a stored method has no account).
  - Warnings: preflight Warn, `DEPOSIT_ACCOUNTS` (goes to Undeposited Funds), `CLASS_DEFAULT` / `DEPARTMENT_DEFAULT` (tracking on, no default), `ACCOUNTS_UNMAPPED`, `ENTITIES_FAILED`, `HELD`.
- `POST validation/check` with body `{ kind, externalId, sourceSystem? }` → `{ kind, externalId, found, state, displayLabel, documentType, syncControl:"Process|Hold|Skip", valid, errors:[{ field, code, message }], warnings:[…], missingDependencies:[{ kind, externalId, label, state }] }`. It runs the gateway's PayloadValidator over the stored payload. No Intuit call is made and nothing is saved.
- Preflight adds `DEPOSIT_ACCOUNTS`, `BANK_ACCOUNTS`, `CLASS_DEFAULT`, `DEPARTMENT_DEFAULT`. These are **Warn only, listed only when there is a problem** (D-68).

**QuickBooks logs page (D-34, D-69a, INTEGRATION_VIEW)**
- `GET sync/log?failedOnly=true&unresolvedOnly=true&kind=&operation=&outcome=&from=&to=&search=&page=1&pageSize=50` → `PaginatedResponse<SyncLogItem>`, newest first, pageSize ≤ 200.
  - `failedOnly` defaults to true and means any outcome other than Succeeded or DryRun.
  - `unresolvedOnly` defaults to true (QA-7). It keeps only calls whose record's **current** state is not Synced or Voided, and leaves out calls that belong to no record (matching scans, imports). Pass `false` for the full history.
  - Outcome values: Succeeded, Refused, Duplicate, StaleObject, NotFound, Throttled, Transient, AuthRevoked, Exception, DryRun.
  - `operation` and `outcome` must match exactly.
  - `to` given as a date only covers the whole day.
  - An unknown `kind` → 400.
- SyncLogItem = `{ id, createdAt, kind, itemId, externalId, sourceSystem, displayLabel, operation, outcome, errorCode, message, durationMs, intuitTid, state, remoteId, remoteDocNumber, deepLink, requestJson:null, responseJson:null }`.
  - `kind`, `itemId`, `externalId`, `displayLabel` and `state` are null for calls that belong to no record (e.g. matching scans).
  - `state` is the record's state now, not at the time of the call.
  - `deepLink` is set only when the record is linked to a QuickBooks record.
- `GET sync/log/{id}` → the same item with `requestJson` / `responseJson`. These were redacted when written. Another organization's id → 404.
- `POST sync/push { kind, externalIds }` → `{ requested, skippedInMatching:[ids], notFound:[ids] }`, for every kind (D-69b, REV-20).
  - Customers, vendors and items the gateway has never seen get a placeholder, as before.
  - A never-seen document is created only when SCM sends it. If SCM doesn't send it, its id comes back in `notFound` ("Nothing to send for this document") and nothing is stored.
  - `requested` excludes the `notFound` ids.

### Details fixed by the frontend (FE, D-80..D-84)
The Angular models mirror CORE's Details above and CTRL's `PostingAdminModels.cs` field for field (`src/app/services/financial-posting.service.ts`, `src/app/models/quickbooks-integration.models.ts`). The FE adds or relies on these points:
- `GET sync/items?deadLetter=true` is sent **without** `state`. The other filters are unchanged.
- `GET matching/{kind}/remote` is called with `page=1&pageSize=200`. The Match tab sends `linked=false`; the walk-in customer picker sends no `linked`. Search is done in the page (R-FE-3).
- **Held** = `state:"NotSynced"` with `lastErrorCode` ending in `HELD` (`HELD`, `POS_SALES_HELD`). It is shown as "Held" on the Sync tab and on badges.
- Badge kinds come from the existing `status/lookup`, one call per kind. The sales-invoice detail asks for SalesInvoice + SalesReceipt (POS invoice), or CreditMemo + RefundReceipt (credit note), using the invoice uuid as `externalId` (QBO Details: ExternalId = the SCM row's uuid). It shows the first kind that is not `NotSynced`.
- `GET sync-control` 404 (older server) = keep the old switches on the Mappings tab (D-81). Any answer = show the Sync control tab only.
- PUT `sync-control/{type}` 409 `result.heldCount` updates the row's held count. The admin then confirms the release banner and saves again with `confirmRelease:true`.
- `POST finance/posting/log/{uuid}/retry|skip` from the Posting page are sent with an empty body `{}`, i.e. every failed target. The D-33 retry (badge, Logs tab) sends `{ targetCode }` for each by-source target with `canAct`.
- D-33 / REV-19: `POST sync/push` is called only with INTEGRATION_SYNC. A document id in the answer's `notFound` gets an info toast, and the badge is left as is.
- REV-20: badges with candidate kinds act on the gateway's kind, else on by-source `documentType` (`SalesReceipt`, `RefundReceipt`, `CreditNote` → CreditMemo, `SalesInvoice`). `POSSale` / `POSReturn` are not decisive.
- D-34: `GET sync/log` is sent with `failedOnly` explicit (default true), plus `kind`, `operation`, `outcome`, `from`/`to` (yyyy-MM-dd), `search`, `page`, `pageSize` (≤ 200). Bodies come from `GET sync/log/{id}` when a row is opened.
- **D-36 needs** `journals[].lines` = `[{ lineNumber, accountCode, accountName, description, debit, credit }]` (base currency) in `GET finance/posting/log/{uuid}`. Until then the drawer shows the posting's own `lines`.
- The posting page reads `config` only when the user holds a GL permission (D-41: config GET needs one). INTEGRATION_* users see the log only.
- The Coverage bars are derived on the page: `entities` + `taxCodes` (TaxCode) + `providerDefaults` (`required`/`set`) + `accounts` (information, only when `glAvailable`). Issue `tab` values map to page tabs: `setup` → Preflight, `tax` → Mappings, `sync` → Sync, the rest by name.

**QuickBooks status refresh (CORE, D-49b / QA-2).** Every log read (list page, detail, by-source, summary) and every retry-job run first brings the `quickbooks` rows that are still `Queued`/`Held` up to date from the QuickBooks outbox and stores the result: Synced → `Posted` with `externalId` (QuickBooks id) and `externalDocNumber`; Voided → `Posted`; Failed/Blocked → `Failed` and NeedsResolution → `DeadLetter`, both with `mirroredFromTarget: true` and `nextAttemptAt: null` — **not** retried by the Accounting job (QuickBooks' outbox retries), but `canAct` / manual `retry` still re-dispatch them (clearing the flag); a later Synced → `Posted` with the flag cleared. Each target object therefore also carries `externalDocNumber` (string or null) and `mirroredFromTarget` (bool).
