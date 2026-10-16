# SMS — Task Register Checklist

## FSD Addendum 44 — Financial Posting Gateway & QuickBooks Integration

| Property | Value |
|---|---|
| Source Document | SMS-FSD-ADD-044 v1.0 |
| Generated | 2026-10-16 |
| Total Tasks | 578 |
| Phases | 8 |
| Change Items Covered | C1–C18 |

---

## How to Use This Register

- Each task has a unique ID: `A44-XXXX`
- Status values: `[ ]` = Not Started, `[~]` = In Progress, `[✓]` = Done, `[—]` = Skipped / N/A
- Priority: **P0** = Blocker, **P1** = Must Have, **P2** = Should Have, **P3** = Nice to Have
- Every task belongs to a Change Item (C1–C18) and a Development Phase (1–8)

---

## Phase 1 — Gateway Foundation (C1, C2, C4) — 2 Weeks

### 1.1 C1 — Financial Posting Gateway Contract

#### 1.1.1 Enums

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0001 | Create `FinDocType` enum in `SMS.Core/Contracts/Finance/FinDocType.cs` | P0 | 1 | [ ] |
| A44-0002 | Add values: SalesInvoice, PurchaseBill, Payment, BillPayment, CreditNote, SalesReturn, POSSale, POSReturn, InventoryAdjustment, ProductionCost, ProductionCompletion, ManualJournal, PurchaseReturn, BankFee, Expense, VendorCredit, RefundReceipt, SalesReceipt, DepositSlip | P0 | 1 | [ ] |
| A44-0003 | Create `PostingOutcome` enum in `SMS.Core/Contracts/Finance/PostingOutcome.cs` | P0 | 1 | [ ] |
| A44-0004 | Add values: Committed, Queued, DuplicateSkipped, ModuleNotLicensed, NoTargetConfigured, ValidationFailed | P0 | 1 | [ ] |

#### 1.1.2 DTOs

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0005 | Create `FinancialTransaction` DTO in `SMS.Core/Contracts/Finance/FinancialTransaction.cs` | P0 | 1 | [ ] |
| A44-0006 | Add properties: OrgId (int), SourceModule (string), DocumentType (FinDocType), SourceDocumentId (int), SourceDocumentNumber (string) | P0 | 1 | [ ] |
| A44-0007 | Add properties: TransactionDate (DateTime), CurrencyCode (string), ExchangeRate (decimal?), Description (string) | P0 | 1 | [ ] |
| A44-0008 | Add properties: PartnerId (int?), PartnerType (string?), PartnerName (string?) | P0 | 1 | [ ] |
| A44-0009 | Add properties: Lines (List\<FinancialTransactionLine\>), Metadata (FinancialDocumentMetadata?) | P0 | 1 | [ ] |
| A44-0010 | Add computed property: IdempotencyKey => `{SourceModule}:{DocumentType}:{SourceDocumentId}` | P0 | 1 | [ ] |
| A44-0011 | Create `FinancialTransactionLine` DTO in same namespace | P0 | 1 | [ ] |
| A44-0012 | Add properties: LineNumber (int), AccountCode (string), Description (string), DebitAmount (decimal), CreditAmount (decimal), TaxCode (string?), TaxAmount (decimal?) | P0 | 1 | [ ] |
| A44-0013 | Create `FinancialDocumentMetadata` DTO | P1 | 1 | [ ] |
| A44-0014 | Add properties: DueDate, PaymentTerms, ReferenceNumber, WarehouseCode, custom property bag | P1 | 1 | [ ] |
| A44-0015 | Create `PostingResult` DTO in `SMS.Core/Contracts/Finance/PostingResult.cs` | P0 | 1 | [ ] |
| A44-0016 | Add properties: Success (bool), Outcome (PostingOutcome), InternalJournalId (int?), OutboxEntryIds (List\<int\>), ErrorMessage (string?) | P0 | 1 | [ ] |

#### 1.1.3 Interface

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0017 | Create `IFinancialPostingGateway` interface in `SMS.Core/Contracts/Finance/` | P0 | 1 | [ ] |
| A44-0018 | Add method: `Task<PostingResult> PostAsync(FinancialTransaction transaction)` | P0 | 1 | [ ] |
| A44-0019 | Add method: `Task<PostingResult> ReverseAsync(FinancialTransaction reversal)` | P0 | 1 | [ ] |
| A44-0020 | Add XML documentation on interface and both methods | P1 | 1 | [ ] |

### 1.2 C2 — Finance DbContext (Separation-Ready)

#### 1.2.1 DbContext Class

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0021 | Create `FinanceDbContext` class in `SMS.Finance/Data/FinanceDbContext.cs` | P0 | 1 | [ ] |
| A44-0022 | Configure default schema `"finance"` in `OnModelCreating()` | P0 | 1 | [ ] |
| A44-0023 | Add `DbSet<TenantFinanceConfig>` property | P0 | 1 | [ ] |
| A44-0024 | Add `DbSet<ExternalSystemConnection>` property | P0 | 1 | [ ] |
| A44-0025 | Add `DbSet<ExternalAccountMapping>` property | P0 | 1 | [ ] |
| A44-0026 | Add `DbSet<ExternalEntityMapping>` property | P0 | 1 | [ ] |
| A44-0027 | Add `DbSet<ExternalProviderDefault>` property | P0 | 1 | [ ] |
| A44-0028 | Add `DbSet<ExternalDocumentOverride>` property | P0 | 1 | [ ] |
| A44-0029 | Add `DbSet<FinancialPostingOutbox>` property | P0 | 1 | [ ] |
| A44-0030 | Add `DbSet<SyncLog>` property | P0 | 1 | [ ] |
| A44-0031 | Add `DbSet<JournalEntry>` property | P0 | 1 | [ ] |
| A44-0032 | Add `DbSet<JournalEntryLine>` property | P0 | 1 | [ ] |
| A44-0033 | Add `DbSet<EntitySyncOutbox>` property | P0 | 1 | [ ] |
| A44-0034 | Add `DbSet<ExternalTaxMapping>` property | P0 | 1 | [ ] |
| A44-0035 | Add `DbSet<SyncControlConfig>` property | P0 | 1 | [ ] |
| A44-0036 | Apply multi-tenant query filter (OrgId) to all entities | P0 | 1 | [ ] |

#### 1.2.2 Cross-Schema Rules

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0037 | Verify FinanceDbContext has ZERO foreign keys to `sales`, `purchase`, `pos`, `inventory`, `crm` schemas | P0 | 1 | [ ] |
| A44-0038 | Store source document identity as plain values: SourceDocumentId (int) + SourceDocumentType (string) + SourceModule (string) — not FKs | P0 | 1 | [ ] |
| A44-0039 | All DTOs in SMS.Core are serializable (no entity references, no DbContext dependencies) | P0 | 1 | [ ] |
| A44-0040 | Register `FinanceDbContext` in DI with `AddDbContext<FinanceDbContext>()` pointing to same SQL Server connection string | P0 | 1 | [ ] |
| A44-0041 | Verify `FinanceDbContext` uses separate migration history table `__FinanceMigrationsHistory` | P1 | 1 | [ ] |

### 1.3 C4 — Financial Posting Gateway Implementation

#### 1.3.1 Target Resolver

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0042 | Create `FinancialPostingTargetResolver` service in `SMS.Finance/Services/` | P0 | 1 | [ ] |
| A44-0043 | Add method: `GetActiveTargetsAsync(int orgId)` — returns list of enabled targets (internal GL + external connections) | P0 | 1 | [ ] |
| A44-0044 | Check `TenantFinanceConfigs.InternalAccountingEnabled` for internal GL target | P0 | 1 | [ ] |
| A44-0045 | Check `ExternalSystemConnections` for active external targets | P0 | 1 | [ ] |
| A44-0046 | Return target list with Provider, ConnectionId, IsInternalGL flags | P0 | 1 | [ ] |

#### 1.3.2 Gateway Service

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0047 | Create `FinancialPostingGateway` class implementing `IFinancialPostingGateway` | P0 | 1 | [ ] |
| A44-0048 | Inject dependencies: FinanceDbContext, FinancialPostingTargetResolver, IModuleActivationService, ILogger | P0 | 1 | [ ] |
| A44-0049 | Implement license check: return `ModuleNotLicensed` if FINANCE module not active for tenant | P0 | 1 | [ ] |
| A44-0050 | Implement balance validation: verify total debits = total credits within 0.001 tolerance | P0 | 1 | [ ] |
| A44-0051 | Implement idempotency check: lookup by IdempotencyKey, return `DuplicateSkipped` if exists | P0 | 1 | [ ] |
| A44-0052 | Implement target resolution: call resolver, return `NoTargetConfigured` if empty | P0 | 1 | [ ] |
| A44-0053 | For internal GL target: create JournalEntry + JournalEntryLines in same DbContext transaction | P0 | 1 | [ ] |
| A44-0054 | For external targets: create FinancialPostingOutbox entry per provider | P0 | 1 | [ ] |
| A44-0055 | Wrap internal GL + outbox writes in single SaveChangesAsync() for atomicity | P0 | 1 | [ ] |
| A44-0056 | Implement `ReverseAsync()` — create reversing journal entries | P1 | 1 | [ ] |

#### 1.3.3 DI Registration

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0057 | Register `IFinancialPostingGateway` → `FinancialPostingGateway` as Scoped in DI | P0 | 1 | [ ] |
| A44-0058 | Register `FinancialPostingTargetResolver` as Scoped in DI | P0 | 1 | [ ] |
| A44-0059 | Create `FinanceServiceCollectionExtensions.AddFinanceModule()` extension method | P1 | 1 | [ ] |

---

## Phase 2 — Configuration & Mapping (C3, C5, C6) — 2 Weeks

### 2.1 C3 — Tenant Finance Configuration & External System Connections

#### 2.1.1 TenantFinanceConfigs Entity

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0060 | Create `TenantFinanceConfig` entity in `SMS.Finance/Entities/` | P0 | 2 | [ ] |
| A44-0061 | Add property: Id (int, PK, IDENTITY) | P0 | 2 | [ ] |
| A44-0062 | Add property: OrgId (int, required) | P0 | 2 | [ ] |
| A44-0063 | Add property: InternalAccountingEnabled (bool, default false) | P0 | 2 | [ ] |
| A44-0064 | Add property: AutoPostToGL (bool, default false) | P1 | 2 | [ ] |
| A44-0065 | Add property: DefaultCurrencyCode (nvarchar(3)) | P1 | 2 | [ ] |
| A44-0066 | Add property: FiscalYearStartMonth (int, default 1) | P2 | 2 | [ ] |
| A44-0067 | Implement IMultiTenant interface | P0 | 2 | [ ] |

#### 2.1.2 TenantFinanceConfigs EF Core Configuration

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0068 | Map to table `TenantFinanceConfigs` in `finance` schema | P0 | 2 | [ ] |
| A44-0069 | Configure unique index on OrgId | P0 | 2 | [ ] |
| A44-0070 | Configure multi-tenant query filter | P0 | 2 | [ ] |

#### 2.1.3 ExternalSystemConnections Entity

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0071 | Create `ExternalSystemConnection` entity in `SMS.Finance/Entities/` | P0 | 2 | [ ] |
| A44-0072 | Add property: Id (int, PK) | P0 | 2 | [ ] |
| A44-0073 | Add property: OrgId (int) | P0 | 2 | [ ] |
| A44-0074 | Add property: Provider (ExternalSystemType enum) | P0 | 2 | [ ] |
| A44-0075 | Add property: DisplayName (nvarchar(200)) | P1 | 2 | [ ] |
| A44-0076 | Add property: AccessTokenEncrypted (nvarchar(max)) | P0 | 2 | [ ] |
| A44-0077 | Add property: RefreshTokenEncrypted (nvarchar(max)) | P0 | 2 | [ ] |
| A44-0078 | Add property: TokenExpiresAtUtc (datetime2) | P0 | 2 | [ ] |
| A44-0079 | Add property: RealmId (nvarchar(100)) — QB company identifier | P0 | 2 | [ ] |
| A44-0080 | Add property: IsActive (bit, default true) | P0 | 2 | [ ] |
| A44-0081 | Add property: EntitySyncEnabled (bit, default true) | P0 | 2 | [ ] |
| A44-0082 | Add property: ConnectedAtUtc (datetime2) | P1 | 2 | [ ] |
| A44-0083 | Add property: LastSyncAtUtc (datetime2, nullable) | P1 | 2 | [ ] |
| A44-0084 | Create `ExternalSystemType` enum: QuickBooksOnline, Xero, Tally, ERPNext | P0 | 2 | [ ] |

#### 2.1.4 ExternalSystemConnections EF Core Configuration

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0085 | Map to table `ExternalSystemConnections` in `finance` schema | P0 | 2 | [ ] |
| A44-0086 | Configure unique index on (OrgId, Provider) | P0 | 2 | [ ] |
| A44-0087 | Configure Data Protection API encryption for AccessTokenEncrypted and RefreshTokenEncrypted | P0 | 2 | [ ] |

#### 2.1.5 Migrations M1–M2

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0088 | Write EF Core migration `AddTenantFinanceConfigs` (M1) | P0 | 2 | [ ] |
| A44-0089 | Verify M1 creates table with all columns and indexes | P0 | 2 | [ ] |
| A44-0090 | Write EF Core migration `AddExternalSystemConnections` (M2) | P0 | 2 | [ ] |
| A44-0091 | Verify M2 creates table with all columns, indexes, and encrypted token columns | P0 | 2 | [ ] |
| A44-0092 | Verify both migrations roll back cleanly (Down() method) | P1 | 2 | [ ] |

### 2.2 C5 — External Account & Entity Mapping Tables

#### 2.2.1 ExternalAccountMappings Entity

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0093 | Create `ExternalAccountMapping` entity in `SMS.Finance/Entities/` | P0 | 2 | [ ] |
| A44-0094 | Add properties: Id (int), OrgId (int), SmsAccountCode (nvarchar(20)), Provider (ExternalSystemType) | P0 | 2 | [ ] |
| A44-0095 | Add properties: ExternalAccountId (nvarchar(100)), ExternalAccountName (nvarchar(200)) | P0 | 2 | [ ] |
| A44-0096 | Add property: IsAutoMapped (bit) — flag for auto-map by name matches | P1 | 2 | [ ] |

#### 2.2.2 ExternalAccountMappings EF Core Configuration

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0097 | Map to table `ExternalAccountMappings` in `finance` schema | P0 | 2 | [ ] |
| A44-0098 | Configure unique index on (OrgId, SmsAccountCode, Provider) | P0 | 2 | [ ] |
| A44-0099 | Configure index on (OrgId, Provider) for provider-filtered queries | P1 | 2 | [ ] |

#### 2.2.3 ExternalEntityMappings Entity

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0100 | Create `ExternalEntityMapping` entity in `SMS.Finance/Entities/` | P0 | 2 | [ ] |
| A44-0101 | Add properties: Id (int), OrgId (int), EntityType (nvarchar(50): "Customer"/"Vendor"/"Product"), Provider (ExternalSystemType) | P0 | 2 | [ ] |
| A44-0102 | Add properties: InternalEntityId (int), ExternalEntityId (nvarchar(100)), ExternalEntityName (nvarchar(200)) | P0 | 2 | [ ] |
| A44-0103 | Add property: SyncDirection (nvarchar(20): "Outbound"/"Inbound"/"Bidirectional") | P1 | 2 | [ ] |
| A44-0104 | Add property: LastSyncedAtUtc (datetime2, nullable) | P1 | 2 | [ ] |

#### 2.2.4 ExternalEntityMappings EF Core Configuration

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0105 | Map to table `ExternalEntityMappings` in `finance` schema | P0 | 2 | [ ] |
| A44-0106 | Configure unique index on (OrgId, EntityType, InternalEntityId, Provider) | P0 | 2 | [ ] |
| A44-0107 | Configure index on (OrgId, EntityType, Provider) for coverage queries | P1 | 2 | [ ] |

#### 2.2.5 Migrations M3–M4

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0108 | Write EF Core migration `AddExternalAccountMappings` (M3) | P0 | 2 | [ ] |
| A44-0109 | Verify M3 creates table with all columns and indexes | P0 | 2 | [ ] |
| A44-0110 | Write EF Core migration `AddExternalEntityMappings` (M4) | P0 | 2 | [ ] |
| A44-0111 | Verify M4 creates table with all columns and indexes | P0 | 2 | [ ] |

### 2.3 C6 — Provider-Specific Defaults & Enrichment Layer

#### 2.3.1 ExternalProviderDefaults Entity

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0112 | Create `ExternalProviderDefault` entity in `SMS.Finance/Entities/` | P0 | 2 | [ ] |
| A44-0113 | Add properties: Id (int), OrgId (int), Provider (ExternalSystemType) | P0 | 2 | [ ] |
| A44-0114 | Add properties: SettingKey (nvarchar(100)), SettingValue (nvarchar(500)) | P0 | 2 | [ ] |
| A44-0115 | Add property: Description (nvarchar(500), nullable) — display label for UI | P2 | 2 | [ ] |

#### 2.3.2 ExternalProviderDefaults EF Core Configuration

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0116 | Map to table `ExternalProviderDefaults` in `finance` schema | P0 | 2 | [ ] |
| A44-0117 | Configure unique index on (OrgId, Provider, SettingKey) | P0 | 2 | [ ] |

#### 2.3.3 ExternalDocumentOverrides Entity

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0118 | Create `ExternalDocumentOverride` entity in `SMS.Finance/Entities/` | P0 | 2 | [ ] |
| A44-0119 | Add properties: Id (int), OrgId (int), Provider (ExternalSystemType), DocumentType (nvarchar(50)) | P0 | 2 | [ ] |
| A44-0120 | Add properties: SettingKey (nvarchar(100)), SettingValue (nvarchar(500)) | P0 | 2 | [ ] |

#### 2.3.4 ExternalDocumentOverrides EF Core Configuration

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0121 | Map to table `ExternalDocumentOverrides` in `finance` schema | P0 | 2 | [ ] |
| A44-0122 | Configure unique index on (OrgId, Provider, DocumentType, SettingKey) | P0 | 2 | [ ] |

#### 2.3.5 ProviderEnrichmentService

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0123 | Create `ProviderEnrichmentService` in `SMS.Finance/Services/` | P0 | 2 | [ ] |
| A44-0124 | Add method: `EnrichAsync(FinancialTransaction, ExternalSystemType) → EnrichedFinancialPayload` | P0 | 2 | [ ] |
| A44-0125 | Load provider defaults for the tenant and provider | P0 | 2 | [ ] |
| A44-0126 | Load document-type overrides (overrides win over defaults) | P0 | 2 | [ ] |
| A44-0127 | Resolve SMS account codes to external account IDs from ExternalAccountMappings | P0 | 2 | [ ] |
| A44-0128 | Resolve SMS entity IDs (Customer/Vendor) to external entity IDs from ExternalEntityMappings | P0 | 2 | [ ] |
| A44-0129 | Create `EnrichedFinancialPayload` DTO with resolved IDs and provider-specific fields | P0 | 2 | [ ] |
| A44-0130 | Register ProviderEnrichmentService as Scoped in DI | P0 | 2 | [ ] |

#### 2.3.6 Migrations M5–M6

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0131 | Write EF Core migration `AddExternalProviderDefaults` (M5) | P0 | 2 | [ ] |
| A44-0132 | Write EF Core migration `AddExternalDocumentOverrides` (M6) | P0 | 2 | [ ] |
| A44-0133 | Verify M5 and M6 create tables with correct indexes | P0 | 2 | [ ] |

---

## Phase 3 — Outbox & QuickBooks (C7, C8) — 3 Weeks

### 3.1 C7 — Financial Posting Outbox & Background Processor

#### 3.1.1 FinancialPostingOutbox Entity

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0134 | Create `FinancialPostingOutbox` entity in `SMS.Finance/Entities/` | P0 | 3 | [ ] |
| A44-0135 | Add properties: Id (int, PK), OrgId (int), SourceModule (string), DocumentType (string) | P0 | 3 | [ ] |
| A44-0136 | Add properties: SourceDocumentId (int), Provider (string), ConnectionId (int) | P0 | 3 | [ ] |
| A44-0137 | Add property: PayloadJson (nvarchar(max)) — serialized FinancialTransaction | P0 | 3 | [ ] |
| A44-0138 | Add property: IdempotencyKey (nvarchar(200)) | P0 | 3 | [ ] |
| A44-0139 | Add property: Status (OutboxStatus enum: Pending, Processing, Completed, Failed, DeadLetter, Held) | P0 | 3 | [ ] |
| A44-0140 | Add properties: RetryCount (int, default 0), MaxRetries (int, default 5) | P0 | 3 | [ ] |
| A44-0141 | Add properties: ErrorMessage (nvarchar(max), nullable), ExternalId (nvarchar(200), nullable) | P0 | 3 | [ ] |
| A44-0142 | Add properties: CreatedAtUtc (datetime2), ProcessedAtUtc (datetime2?), NextRetryAtUtc (datetime2?) | P0 | 3 | [ ] |

#### 3.1.2 FinancialPostingOutbox EF Core Configuration

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0143 | Map to table `FinancialPostingOutbox` in `finance` schema | P0 | 3 | [ ] |
| A44-0144 | Configure index on (OrgId, Status, NextRetryAtUtc) for processor query | P0 | 3 | [ ] |
| A44-0145 | Configure unique index on (OrgId, IdempotencyKey) | P0 | 3 | [ ] |
| A44-0146 | Configure index on (OrgId, SourceModule, SourceDocumentId) | P1 | 3 | [ ] |

#### 3.1.3 OutboxProcessor (Hangfire)

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0147 | Create `OutboxProcessor` class in `SMS.Finance/Jobs/` | P0 | 3 | [ ] |
| A44-0148 | Register as Hangfire recurring job (every 30 seconds) | P0 | 3 | [ ] |
| A44-0149 | Implement: query Pending entries where NextRetryAtUtc <= UtcNow, order by CreatedAtUtc | P0 | 3 | [ ] |
| A44-0150 | Set Status = Processing before calling adapter | P0 | 3 | [ ] |
| A44-0151 | On success: set Status = Completed, populate ExternalId, set ProcessedAtUtc | P0 | 3 | [ ] |
| A44-0152 | On failure: increment RetryCount, set ErrorMessage | P0 | 3 | [ ] |
| A44-0153 | Implement exponential backoff: 30s × 4^(retryCount-1) → 30s, 2m, 8m, 32m, 2h | P0 | 3 | [ ] |
| A44-0154 | On max retries exceeded: set Status = DeadLetter | P0 | 3 | [ ] |
| A44-0155 | Write SyncLog entry on completion or dead letter | P0 | 3 | [ ] |
| A44-0156 | Handle concurrent processing: skip entries already Processing | P1 | 3 | [ ] |
| A44-0157 | Add batch size limit (process max 50 entries per cycle) | P1 | 3 | [ ] |

#### 3.1.4 Migration M7

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0158 | Write EF Core migration `AddFinancialPostingOutbox` (M7) | P0 | 3 | [ ] |
| A44-0159 | Verify M7 creates table with all columns and 3 indexes | P0 | 3 | [ ] |

### 3.2 C8 — QuickBooks Online Integration Target

#### 3.2.1 QuickBooks API Client

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0160 | Create `IQuickBooksApiClient` interface in `SMS.Finance/Services/QuickBooks/` | P0 | 3 | [ ] |
| A44-0161 | Add methods: GetAsync, PostAsync, QueryAsync (QB query language) | P0 | 3 | [ ] |
| A44-0162 | Create `QuickBooksApiClient` implementation | P0 | 3 | [ ] |
| A44-0163 | Configure HttpClient with base URL `https://quickbooks.api.intuit.com/v3/company/{realmId}` | P0 | 3 | [ ] |
| A44-0164 | Add Authorization header with Bearer token from ExternalSystemConnections | P0 | 3 | [ ] |
| A44-0165 | Implement 401 handler: refresh token automatically, retry once | P0 | 3 | [ ] |
| A44-0166 | Implement rate limit handling (500 req/min per realm): detect 429, backoff | P1 | 3 | [ ] |
| A44-0167 | Add request/response logging to SyncLogs | P1 | 3 | [ ] |
| A44-0168 | Register IQuickBooksApiClient as Scoped in DI | P0 | 3 | [ ] |

#### 3.2.2 OAuth 2.0 Flow

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0169 | Create `QuickBooksOAuthController` in `SMS.Finance/Controllers/` | P0 | 3 | [ ] |
| A44-0170 | Implement `POST /api/finance/connections/QuickBooksOnline/connect` — generate OAuth URL with client_id, redirect_uri, scope=com.intuit.quickbooks.accounting | P0 | 3 | [ ] |
| A44-0171 | Implement `POST /api/finance/connections/QuickBooksOnline/callback` — exchange auth code for access+refresh tokens | P0 | 3 | [ ] |
| A44-0172 | Store tokens encrypted in ExternalSystemConnections | P0 | 3 | [ ] |
| A44-0173 | Store RealmId from callback | P0 | 3 | [ ] |
| A44-0174 | Implement token refresh: `POST https://oauth.platform.intuit.com/oauth2/v1/tokens/bearer` | P0 | 3 | [ ] |
| A44-0175 | Implement disconnect: revoke token, deactivate connection | P0 | 3 | [ ] |
| A44-0176 | Store OAuth client_id and client_secret in app configuration (not in DB) | P0 | 3 | [ ] |
| A44-0177 | Configure QB sandbox vs production environment toggle | P1 | 3 | [ ] |

#### 3.2.3 QuickBooks Target Adapter

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0178 | Create `QuickBooksTarget` class in `SMS.Finance/Services/QuickBooks/` | P0 | 3 | [ ] |
| A44-0179 | Implement document type mapping: SalesInvoice → QB Invoice | P0 | 3 | [ ] |
| A44-0180 | Implement document type mapping: PurchaseBill → QB Bill | P0 | 3 | [ ] |
| A44-0181 | Implement document type mapping: Payment → QB Payment | P0 | 3 | [ ] |
| A44-0182 | Implement document type mapping: BillPayment → QB BillPayment | P0 | 3 | [ ] |
| A44-0183 | Implement document type mapping: POSSale → QB SalesReceipt | P1 | 3 | [ ] |
| A44-0184 | Build QB Invoice JSON payload: Line items with AccountRef, ItemRef, TaxCodeRef, Amount | P0 | 3 | [ ] |
| A44-0185 | Build QB Bill JSON payload: Line items with AccountRef, Amount | P0 | 3 | [ ] |
| A44-0186 | Build QB Payment JSON payload: TotalAmt, CustomerRef, linked invoices | P0 | 3 | [ ] |
| A44-0187 | Handle QB SyncToken for updates (fetch current, include in POST) | P0 | 3 | [ ] |
| A44-0188 | Parse QB API response: extract Id (ExternalId), SyncToken | P0 | 3 | [ ] |
| A44-0189 | Handle QB API error responses: parse Fault → extract Message and Detail | P0 | 3 | [ ] |

#### 3.2.4 Seed Data & Configuration

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0190 | Write seed migration `SeedQuickBooksProviderDefaults` (M10) | P1 | 3 | [ ] |
| A44-0191 | Seed default QB setting keys: ClassRef, DeptRef, TermRef, TrackingCategoryRef | P1 | 3 | [ ] |
| A44-0192 | Add QB-specific configuration section in appsettings.json | P0 | 3 | [ ] |

---

## Phase 4 — Monitoring & Integration (C9, C10) — 2 Weeks

### 4.1 C9 — Sync Log & Reconciliation Dashboard

#### 4.1.1 SyncLogs Entity

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0193 | Create `SyncLog` entity in `SMS.Finance/Entities/` | P0 | 4 | [ ] |
| A44-0194 | Add properties: Id (long, PK), OrgId (int), Provider (string) | P0 | 4 | [ ] |
| A44-0195 | Add properties: DocumentType (string), SmsDocumentId (int), ExternalDocumentId (string?) | P0 | 4 | [ ] |
| A44-0196 | Add properties: Direction (string: "Outbound"/"Inbound"), Status (string: "Success"/"Failed"/"DeadLetter"/"FlaggedForReview") | P0 | 4 | [ ] |
| A44-0197 | Add properties: SyncedAtUtc (datetime2), DurationMs (int?), Details (nvarchar(max)?) | P0 | 4 | [ ] |

#### 4.1.2 SyncLogs EF Core Configuration

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0198 | Map to table `SyncLogs` in `finance` schema | P0 | 4 | [ ] |
| A44-0199 | Configure index on (OrgId, SyncedAtUtc DESC) for dashboard query | P0 | 4 | [ ] |
| A44-0200 | Configure index on (OrgId, Status) for summary counts | P0 | 4 | [ ] |
| A44-0201 | Configure index on (OrgId, DocumentType, SmsDocumentId) for entity sync log queries | P0 | 4 | [ ] |

#### 4.1.3 Migration M8

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0202 | Write EF Core migration `AddSyncLogs` (M8) | P0 | 4 | [ ] |
| A44-0203 | Verify M8 creates table with all columns and 3 indexes | P0 | 4 | [ ] |

#### 4.1.4 Migration M9

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0204 | Write EF Core migration `AddJournalEntries` (M9) — basic GL structure | P1 | 4 | [ ] |
| A44-0205 | Create `finance.JournalEntries` table (Id, OrgId, JournalNumber, TransactionDate, Description, SourceModule, SourceDocumentId, IdempotencyKey, PostedAtUtc) | P1 | 4 | [ ] |
| A44-0206 | Create `finance.JournalEntryLines` table (Id, JournalEntryId, LineNumber, AccountCode, Description, DebitAmount, CreditAmount) | P1 | 4 | [ ] |

#### 4.1.5 Sync Log APIs

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0207 | Create `SyncController` in `SMS.Finance/Controllers/` | P0 | 4 | [ ] |
| A44-0208 | Implement `GET /api/finance/sync/logs` — filterable by provider, status, date range, paginated | P0 | 4 | [ ] |
| A44-0209 | Implement `GET /api/finance/sync/summary` — counts by status (Synced, Pending, Failed, DeadLetter) | P0 | 4 | [ ] |
| A44-0210 | Implement `GET /api/finance/sync/outbox` — view pending/failed/dead-letter outbox entries | P0 | 4 | [ ] |
| A44-0211 | Implement `POST /api/finance/sync/outbox/{id}/retry` — reset dead-letter to Pending | P0 | 4 | [ ] |
| A44-0212 | Implement `POST /api/finance/sync/outbox/{id}/skip` — dismiss dead-letter entry | P1 | 4 | [ ] |

#### 4.1.6 Sync Status Dashboard UI

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0213 | Create React component: SyncStatusDashboard at `/settings/finance/integrations/sync-status` | P0 | 4 | [ ] |
| A44-0214 | Build summary tiles: Synced (count, 24h), Pending, Failed, Dead Letter | P0 | 4 | [ ] |
| A44-0215 | Build Recent Activity table: Doc#, Type, QB ID, Status, Time | P0 | 4 | [ ] |
| A44-0216 | Add "View All Logs" and "Export Report" actions | P1 | 4 | [ ] |
| A44-0217 | Add retry/skip actions on failed entries inline | P0 | 4 | [ ] |

### 4.2 C10 — Core Module Integration Points

#### 4.2.1 Caller Pattern

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0218 | Inject `IFinancialPostingGateway` into `SalesInvoiceService` | P0 | 4 | [ ] |
| A44-0219 | Call `PostAsync()` after invoice is issued (not on draft save) | P0 | 4 | [ ] |
| A44-0220 | Build FinancialTransaction from SalesInvoice: map header + lines to DTO | P0 | 4 | [ ] |
| A44-0221 | Log PostingResult but DO NOT fail invoice save if gateway returns error | P0 | 4 | [ ] |
| A44-0222 | Inject `IFinancialPostingGateway` into `PurchaseBillService` | P0 | 4 | [ ] |
| A44-0223 | Call `PostAsync()` after bill is approved | P0 | 4 | [ ] |
| A44-0224 | Inject `IFinancialPostingGateway` into `POSSaleService` | P1 | 4 | [ ] |
| A44-0225 | Call `PostAsync()` after POS sale is completed (FinDocType = POSSale) | P1 | 4 | [ ] |
| A44-0226 | Inject `IFinancialPostingGateway` into `PaymentService` (Customer payments) | P1 | 4 | [ ] |
| A44-0227 | Inject `IFinancialPostingGateway` into `SupplierPaymentService` (Bill payments) | P1 | 4 | [ ] |

#### 4.2.2 Domain Event Catalog

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0228 | Document domain event: `SalesInvoiceIssued` → triggers financial posting | P0 | 4 | [ ] |
| A44-0229 | Document domain event: `SalesInvoiceCancelled` → triggers void | P0 | 4 | [ ] |
| A44-0230 | Document domain event: `PurchaseBillApproved` → triggers financial posting | P0 | 4 | [ ] |
| A44-0231 | Document domain event: `PurchaseBillReversed` → triggers flag-only void | P0 | 4 | [ ] |
| A44-0232 | Document domain event: `POSSaleCompleted` → triggers POS posting | P1 | 4 | [ ] |
| A44-0233 | Document domain event: `CustomerPaymentReceived` → triggers payment posting | P1 | 4 | [ ] |
| A44-0234 | Document domain event: `SupplierPaymentMade` → triggers bill payment posting | P1 | 4 | [ ] |
| A44-0235 | Document domain event: `CreditNoteIssued` → triggers credit memo | P1 | 4 | [ ] |
| A44-0236 | Document domain event: `InventoryAdjustmentPosted` → triggers inventory posting | P2 | 4 | [ ] |

#### 4.2.3 Finance Settings UI

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0237 | Create React component: FinanceSettingsPanel at `/settings/finance/integrations` | P0 | 4 | [ ] |
| A44-0238 | Build Internal Accounting section: Enable Internal GL toggle, Auto-Post to GL toggle, Default Currency dropdown | P0 | 4 | [ ] |
| A44-0239 | Build External Connections section: list connected providers with status, Disconnect/Test/Configure actions | P0 | 4 | [ ] |
| A44-0240 | Show provider cards: QuickBooks Online (with Realm ID, last sync), Xero (placeholder), Tally (placeholder) | P0 | 4 | [ ] |

#### 4.2.4 Account Mapping UI

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0241 | Create React component: AccountMappingPanel at `/settings/finance/integrations/quickbooks/mappings` | P0 | 4 | [ ] |
| A44-0242 | Build two-column mapping table: SMS Account → QuickBooks Account (dropdown) | P0 | 4 | [ ] |
| A44-0243 | Add "Auto-Map by Name" button — calls auto-map API | P1 | 4 | [ ] |
| A44-0244 | Show unmapped account count with warning | P0 | 4 | [ ] |
| A44-0245 | Add Save Mappings button with confirmation | P0 | 4 | [ ] |

#### 4.2.5 Configuration APIs

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0246 | Create `FinanceConfigController` in `SMS.Finance/Controllers/` | P0 | 4 | [ ] |
| A44-0247 | Implement `GET /api/finance/config` | P0 | 4 | [ ] |
| A44-0248 | Implement `PUT /api/finance/config` | P0 | 4 | [ ] |
| A44-0249 | Implement `GET /api/finance/connections` | P0 | 4 | [ ] |
| A44-0250 | Implement `DELETE /api/finance/connections/{id}` | P0 | 4 | [ ] |
| A44-0251 | Implement `POST /api/finance/connections/{id}/test` — ping QB API | P1 | 4 | [ ] |

#### 4.2.6 Mapping APIs

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0252 | Create `MappingController` in `SMS.Finance/Controllers/` | P0 | 4 | [ ] |
| A44-0253 | Implement `GET /api/finance/mappings/accounts/{provider}` | P0 | 4 | [ ] |
| A44-0254 | Implement `POST /api/finance/mappings/accounts/{provider}` | P0 | 4 | [ ] |
| A44-0255 | Implement `DELETE /api/finance/mappings/accounts/{id}` | P0 | 4 | [ ] |
| A44-0256 | Implement `POST /api/finance/mappings/accounts/{provider}/auto-map` | P1 | 4 | [ ] |
| A44-0257 | Implement `GET /api/finance/mappings/entities/{provider}` | P0 | 4 | [ ] |
| A44-0258 | Implement `POST /api/finance/mappings/entities/{provider}` | P0 | 4 | [ ] |

#### 4.2.7 Provider Defaults APIs

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0259 | Implement `GET /api/finance/provider-defaults/{provider}` | P1 | 4 | [ ] |
| A44-0260 | Implement `PUT /api/finance/provider-defaults/{provider}` | P1 | 4 | [ ] |
| A44-0261 | Implement `GET /api/finance/document-overrides/{provider}` | P1 | 4 | [ ] |
| A44-0262 | Implement `PUT /api/finance/document-overrides/{provider}` | P1 | 4 | [ ] |

---

## Phase 5 — Entity Sync (C11, C12, C13, C14) — 2 Weeks

### 5.1 C11 — Entity Sync Gateway Contract

#### 5.1.1 Enums

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0263 | Create `SyncEntityType` enum in `SMS.Core/Contracts/Finance/`: Customer, Vendor, Product, Account | P0 | 5 | [ ] |
| A44-0264 | Create `SyncAction` enum: Create, Update, Delete, Deactivate | P0 | 5 | [ ] |

#### 5.1.2 DTOs

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0265 | Create `EntitySyncRequest` DTO in `SMS.Core/Contracts/Finance/` | P0 | 5 | [ ] |
| A44-0266 | Add properties: OrgId, EntityType (SyncEntityType), EntityId (int), Action (SyncAction), Snapshot (EntitySnapshot) | P0 | 5 | [ ] |
| A44-0267 | Add computed property: IdempotencyKey => `{EntityType}:{EntityId}:{Action}:{Timestamp}` | P0 | 5 | [ ] |
| A44-0268 | Create `EntitySnapshot` DTO with common entity properties | P0 | 5 | [ ] |
| A44-0269 | Add EntitySnapshot properties: Name, Email, Phone, Address, TaxId, IsActive | P0 | 5 | [ ] |
| A44-0270 | Add EntitySnapshot product-specific properties: Sku, UnitPrice, IncomeAccountCode, ExpenseAccountCode, AssetAccountCode | P0 | 5 | [ ] |
| A44-0271 | Create `EntitySyncResult` DTO: Success, Outcome, ErrorMessage | P0 | 5 | [ ] |

#### 5.1.3 Interface

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0272 | Create `IEntitySyncGateway` interface in `SMS.Core/Contracts/Finance/` | P0 | 5 | [ ] |
| A44-0273 | Add method: `Task<EntitySyncResult> PushAsync(EntitySyncRequest request)` | P0 | 5 | [ ] |
| A44-0274 | Add method: `Task<EntitySyncResult> PushBatchAsync(IEnumerable<EntitySyncRequest> requests)` | P1 | 5 | [ ] |
| A44-0275 | Add XML documentation on interface and methods | P1 | 5 | [ ] |

### 5.2 C12 — Entity Sync Gateway Implementation

#### 5.2.1 Target Resolver

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0276 | Create `EntitySyncTargetResolver` in `SMS.Finance/Services/` | P0 | 5 | [ ] |
| A44-0277 | Query ExternalSystemConnections where IsActive=true AND EntitySyncEnabled=true | P0 | 5 | [ ] |
| A44-0278 | Return list of active provider targets | P0 | 5 | [ ] |

#### 5.2.2 Gateway Service

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0279 | Create `EntitySyncGateway` implementing `IEntitySyncGateway` | P0 | 5 | [ ] |
| A44-0280 | Implement license check (FINANCE module) | P0 | 5 | [ ] |
| A44-0281 | Implement idempotency check on EntitySyncOutbox | P0 | 5 | [ ] |
| A44-0282 | Implement target resolution using EntitySyncTargetResolver | P0 | 5 | [ ] |
| A44-0283 | Write EntitySyncOutbox entry per active provider (always async) | P0 | 5 | [ ] |
| A44-0284 | Implement PushBatchAsync: iterate requests, skip duplicates, return aggregate result | P1 | 5 | [ ] |
| A44-0285 | Register IEntitySyncGateway → EntitySyncGateway as Scoped in DI | P0 | 5 | [ ] |
| A44-0286 | Register EntitySyncTargetResolver as Scoped in DI | P0 | 5 | [ ] |

### 5.3 C13 — QuickBooks Master Data Sync Adapter

#### 5.3.1 Adapter Interface & Implementation

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0287 | Create `IEntitySyncAdapter` interface in `SMS.Finance/Services/` | P0 | 5 | [ ] |
| A44-0288 | Add methods: SyncCustomerAsync, SyncVendorAsync, SyncProductAsync, SyncAccountAsync | P0 | 5 | [ ] |
| A44-0289 | Add method: ImportEntitiesAsync(entityType) — import from QB | P0 | 5 | [ ] |
| A44-0290 | Create `QuickBooksMasterDataAdapter` implementing IEntitySyncAdapter | P0 | 5 | [ ] |

#### 5.3.2 Customer Sync

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0291 | Implement SyncCustomerAsync: build QB Customer JSON from EntitySnapshot | P0 | 5 | [ ] |
| A44-0292 | Map: Name → DisplayName, Email → PrimaryEmailAddr, Phone → PrimaryPhone, Address → BillAddr | P0 | 5 | [ ] |
| A44-0293 | Handle Create vs Update: check ExternalEntityMappings for existing QB ID | P0 | 5 | [ ] |
| A44-0294 | On Create: POST /customer, store new mapping in ExternalEntityMappings | P0 | 5 | [ ] |
| A44-0295 | On Update: GET /customer/{id} for SyncToken, then POST with sparse update | P0 | 5 | [ ] |

#### 5.3.3 Vendor Sync

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0296 | Implement SyncVendorAsync: build QB Vendor JSON from EntitySnapshot | P0 | 5 | [ ] |
| A44-0297 | Map: Name → DisplayName, Email → PrimaryEmailAddr, Phone → PrimaryPhone, TaxId → TaxIdentifier | P0 | 5 | [ ] |
| A44-0298 | Handle Create vs Update same pattern as Customer | P0 | 5 | [ ] |

#### 5.3.4 Product Sync

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0299 | Implement SyncProductAsync: build QB Item JSON from EntitySnapshot | P0 | 5 | [ ] |
| A44-0300 | Map: Name → Name, Sku → Sku (if supported), UnitPrice → UnitPrice | P0 | 5 | [ ] |
| A44-0301 | Resolve IncomeAccountCode → QB IncomeAccountRef via ExternalAccountMappings | P0 | 5 | [ ] |
| A44-0302 | Resolve ExpenseAccountCode → QB ExpenseAccountRef via ExternalAccountMappings | P0 | 5 | [ ] |
| A44-0303 | Resolve AssetAccountCode → QB AssetAccountRef via ExternalAccountMappings | P1 | 5 | [ ] |
| A44-0304 | Set QB Item Type = "Inventory" or "NonInventory" based on SMS product type | P0 | 5 | [ ] |
| A44-0305 | Fail with retryable error if account mappings missing (BR-21) | P0 | 5 | [ ] |

#### 5.3.5 Initial Import

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0306 | Implement ImportEntitiesAsync for Customer: `SELECT * FROM Customer` with pagination (maxResults=100, startPosition) | P0 | 5 | [ ] |
| A44-0307 | Implement ImportEntitiesAsync for Vendor: `SELECT * FROM Vendor` with pagination | P0 | 5 | [ ] |
| A44-0308 | Implement ImportEntitiesAsync for Product: `SELECT * FROM Item` with pagination | P0 | 5 | [ ] |
| A44-0309 | Implement ImportEntitiesAsync for Account: `SELECT * FROM Account` — creates ExternalAccountMappings (not ExternalEntityMappings) | P0 | 5 | [ ] |
| A44-0310 | On import: create ExternalEntityMapping with InternalEntityId = 0 (unlinked) | P0 | 5 | [ ] |
| A44-0311 | Skip entities that already have mappings (avoid duplicates) | P0 | 5 | [ ] |
| A44-0312 | Return ImportResult: TotalFound, MappingsCreated, AlreadyExist | P0 | 5 | [ ] |

### 5.4 C14 — Entity Sync Outbox & Background Jobs

#### 5.4.1 EntitySyncOutbox Entity

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0313 | Create `EntitySyncOutbox` entity in `SMS.Finance/Entities/` | P0 | 5 | [ ] |
| A44-0314 | Add properties: Id (int, PK), OrgId (int), EntityType (string), EntityId (int) | P0 | 5 | [ ] |
| A44-0315 | Add properties: Action (string: "Create"/"Update"/"Delete"), Provider (string), ConnectionId (int) | P0 | 5 | [ ] |
| A44-0316 | Add property: PayloadJson (nvarchar(max)) — serialized EntitySnapshot | P0 | 5 | [ ] |
| A44-0317 | Add property: IdempotencyKey (nvarchar(200)) | P0 | 5 | [ ] |
| A44-0318 | Add property: Status (OutboxStatus) | P0 | 5 | [ ] |
| A44-0319 | Add properties: RetryCount (int), MaxRetries (int), ErrorMessage (string?), ExternalEntityId (string?) | P0 | 5 | [ ] |
| A44-0320 | Add properties: CreatedAtUtc, ProcessedAtUtc, NextRetryAtUtc | P0 | 5 | [ ] |

#### 5.4.2 EntitySyncOutbox EF Core Configuration

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0321 | Map to table `EntitySyncOutbox` in `finance` schema | P0 | 5 | [ ] |
| A44-0322 | Configure index on (OrgId, Status, NextRetryAtUtc) | P0 | 5 | [ ] |
| A44-0323 | Configure unique index on (OrgId, IdempotencyKey) | P0 | 5 | [ ] |
| A44-0324 | Configure index on (OrgId, EntityType, EntityId) | P1 | 5 | [ ] |

#### 5.4.3 EntitySyncOutboxProcessor (Hangfire)

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0325 | Create `EntitySyncOutboxProcessor` in `SMS.Finance/Jobs/` | P0 | 5 | [ ] |
| A44-0326 | Register as Hangfire recurring job (every 30 seconds) — separate from financial outbox | P0 | 5 | [ ] |
| A44-0327 | Implement: query Pending entries, route to correct adapter method by EntityType | P0 | 5 | [ ] |
| A44-0328 | On success: update ExternalEntityMappings, write SyncLog | P0 | 5 | [ ] |
| A44-0329 | Implement same exponential backoff as financial outbox (30s, 2m, 8m, 32m, 2h) | P0 | 5 | [ ] |
| A44-0330 | On dead letter: write SyncLog with dead-letter status | P0 | 5 | [ ] |

#### 5.4.4 Caller Patterns

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0331 | Inject `IEntitySyncGateway` into `CustomerService` | P0 | 5 | [ ] |
| A44-0332 | Call `PushAsync()` after customer create/update with SyncAction.Create/Update | P0 | 5 | [ ] |
| A44-0333 | Build EntitySnapshot from Customer entity | P0 | 5 | [ ] |
| A44-0334 | Inject `IEntitySyncGateway` into `VendorService` (SupplierService) | P0 | 5 | [ ] |
| A44-0335 | Call `PushAsync()` after vendor create/update | P0 | 5 | [ ] |
| A44-0336 | Inject `IEntitySyncGateway` into `ProductService` | P0 | 5 | [ ] |
| A44-0337 | Call `PushAsync()` after product variant create/update | P0 | 5 | [ ] |

#### 5.4.5 Migrations M11–M13

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0338 | Write EF Core migration `AddEntitySyncOutbox` (M11) | P0 | 5 | [ ] |
| A44-0339 | Verify M11 creates table with all columns and 3 indexes | P0 | 5 | [ ] |
| A44-0340 | Write EF Core migration `AddEntitySyncEnabledToConnections` (M12) — add EntitySyncEnabled column | P0 | 5 | [ ] |
| A44-0341 | Write EF Core migration `AddSyncLogEntityTypeSupport` (M13) — add index for entity sync queries | P0 | 5 | [ ] |

#### 5.4.6 Entity Sync APIs

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0342 | Create `EntitySyncController` in `SMS.Finance/Controllers/` | P0 | 5 | [ ] |
| A44-0343 | Implement `POST /api/finance/entity-sync/push` — manually trigger entity sync | P0 | 5 | [ ] |
| A44-0344 | Implement `POST /api/finance/entity-sync/{provider}/import` — import entities from QB | P0 | 5 | [ ] |
| A44-0345 | Implement `GET /api/finance/entity-sync/{provider}/coverage` — entity mapping coverage report | P0 | 5 | [ ] |
| A44-0346 | Implement `GET /api/finance/entity-sync/outbox` — view entity sync outbox | P0 | 5 | [ ] |
| A44-0347 | Implement `POST /api/finance/entity-sync/outbox/{id}/retry` | P0 | 5 | [ ] |
| A44-0348 | Implement `POST /api/finance/entity-sync/outbox/{id}/skip` | P1 | 5 | [ ] |
| A44-0349 | Implement `GET /api/finance/entity-sync/logs` — entity sync logs | P0 | 5 | [ ] |
| A44-0350 | Implement `POST /api/finance/entity-sync/{provider}/link` — link imported entity to SMS entity | P0 | 5 | [ ] |
| A44-0351 | Implement `DELETE /api/finance/entity-sync/{provider}/link/{mappingId}` — unlink entity | P1 | 5 | [ ] |

#### 5.4.7 Entity Sync UI

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0352 | Create React component: EntityMappingPanel at `/settings/finance/integrations/quickbooks/entity-mappings` | P0 | 5 | [ ] |
| A44-0353 | Build tab navigation: Customers, Vendors, Products, Accounts | P0 | 5 | [ ] |
| A44-0354 | Build coverage progress bar per entity type (% mapped) | P0 | 5 | [ ] |
| A44-0355 | Build entity mapping table: SMS Entity → QB Entity (dropdown), Status | P0 | 5 | [ ] |
| A44-0356 | Show imported-but-unlinked entities with link action | P0 | 5 | [ ] |
| A44-0357 | Add "Import from QuickBooks" and "Auto-Link by Name" buttons | P1 | 5 | [ ] |
| A44-0358 | Create React component: EntitySyncStatusPanel at `/settings/finance/integrations/entity-sync-status` | P0 | 5 | [ ] |
| A44-0359 | Build summary tiles: Synced (all time), Pending, Failed, Dead Letter | P0 | 5 | [ ] |
| A44-0360 | Build recent entity sync activity table | P0 | 5 | [ ] |
| A44-0361 | Add Entity Sync ON/OFF toggle | P0 | 5 | [ ] |

---

## Phase 6 — Tax, Validation & Sync Control (C15, C16, C18) — 2 Weeks

### 6.1 C15 — Tax Code Mapping

#### 6.1.1 ExternalTaxMapping Entity

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0362 | Create `ExternalTaxMapping` entity in `SMS.Finance/Entities/` | P0 | 6 | [ ] |
| A44-0363 | Add properties: Id (int, PK), OrgId (int), SmsTaxCode (nvarchar(50)), SmsTaxRate (decimal) | P0 | 6 | [ ] |
| A44-0364 | Add property: Provider (ExternalSystemType) | P0 | 6 | [ ] |
| A44-0365 | Add properties: ExternalTaxCodeId (nvarchar(100)), ExternalTaxCodeName (nvarchar(200)) | P0 | 6 | [ ] |
| A44-0366 | Add properties: IsAutoMapped (bit), IsActive (bit, default true) | P0 | 6 | [ ] |

#### 6.1.2 ExternalTaxMapping EF Core Configuration

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0367 | Map to table `ExternalTaxMappings` in `finance` schema | P0 | 6 | [ ] |
| A44-0368 | Configure unique index on (OrgId, SmsTaxCode, Provider) | P0 | 6 | [ ] |
| A44-0369 | Configure index on (OrgId, Provider) | P1 | 6 | [ ] |

#### 6.1.3 TaxMappingService

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0370 | Create `ITaxMappingService` interface in `SMS.Finance/Services/` | P0 | 6 | [ ] |
| A44-0371 | Add method: `ResolveExternalTaxCodeAsync(orgId, smsTaxCode, provider) → string?` | P0 | 6 | [ ] |
| A44-0372 | Add method: `ImportTaxCodesFromProviderAsync(orgId, provider) → TaxImportResult` | P0 | 6 | [ ] |
| A44-0373 | Create `TaxMappingService` implementing ITaxMappingService | P0 | 6 | [ ] |
| A44-0374 | Implement ResolveExternalTaxCodeAsync: lookup by OrgId + SmsTaxCode + Provider + IsActive | P0 | 6 | [ ] |
| A44-0375 | Implement ImportTaxCodesFromProviderAsync: query QB `SELECT * FROM TaxCode`, create mappings | P0 | 6 | [ ] |
| A44-0376 | Handle TAX-EXEMPT / 0% rate auto-mapping to QB "NON" | P1 | 6 | [ ] |
| A44-0377 | Skip already-existing mappings during import | P0 | 6 | [ ] |
| A44-0378 | Register ITaxMappingService as Scoped in DI | P0 | 6 | [ ] |

#### 6.1.4 Enrichment Integration

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0379 | Extend ProviderEnrichmentService (C6) to resolve tax codes via ITaxMappingService | P0 | 6 | [ ] |
| A44-0380 | For each transaction line with TaxCode: resolve to ExternalTaxCodeId | P0 | 6 | [ ] |
| A44-0381 | Set line.ExternalTaxCodeId in EnrichedFinancialPayload | P0 | 6 | [ ] |

#### 6.1.5 Tax Mapping APIs

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0382 | Implement `GET /api/finance/mappings/tax/{provider}` — list tax code mappings | P0 | 6 | [ ] |
| A44-0383 | Implement `POST /api/finance/mappings/tax/{provider}` — create/update tax mapping | P0 | 6 | [ ] |
| A44-0384 | Implement `DELETE /api/finance/mappings/tax/{id}` — delete tax mapping | P0 | 6 | [ ] |
| A44-0385 | Implement `POST /api/finance/mappings/tax/{provider}/import` — import from QB | P0 | 6 | [ ] |

#### 6.1.6 Tax Mapping UI

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0386 | Create React component: TaxCodeMappingPanel at `/settings/finance/integrations/quickbooks/tax-mappings` | P0 | 6 | [ ] |
| A44-0387 | Build mapping table: SMS Tax Code + Rate → QB Tax Code (dropdown), Status | P0 | 6 | [ ] |
| A44-0388 | Show imported-but-unlinked QB tax codes in separate section | P0 | 6 | [ ] |
| A44-0389 | Add "Import Tax Codes from QuickBooks" button | P0 | 6 | [ ] |
| A44-0390 | Show unmapped tax code warning | P0 | 6 | [ ] |

#### 6.1.7 Migration M14

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0391 | Write EF Core migration `AddExternalTaxMappings` (M14) | P0 | 6 | [ ] |
| A44-0392 | Verify M14 creates table with all columns and unique index | P0 | 6 | [ ] |

### 6.2 C16 — Pre-Sync Validation & Reference Checks

#### 6.2.1 Validation Service

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0393 | Create `IPreSyncValidator` interface in `SMS.Finance/Services/` | P0 | 6 | [ ] |
| A44-0394 | Add method: `ValidateFinancialPostingAsync(FinancialPostingOutbox, ExternalSystemType) → ValidationResult` | P0 | 6 | [ ] |
| A44-0395 | Add method: `ValidateEntitySyncAsync(EntitySyncOutbox, ExternalSystemType) → ValidationResult` | P0 | 6 | [ ] |
| A44-0396 | Create `PreSyncValidator` implementing IPreSyncValidator | P0 | 6 | [ ] |

#### 6.2.2 Financial Posting Validation

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0397 | Deserialize FinancialTransaction from outbox PayloadJson | P0 | 6 | [ ] |
| A44-0398 | Check 1: Verify all AccountCodes in transaction lines are mapped in ExternalAccountMappings | P0 | 6 | [ ] |
| A44-0399 | Check 2: Verify Partner (Customer/Vendor) is mapped in ExternalEntityMappings | P0 | 6 | [ ] |
| A44-0400 | Check 3: Verify all TaxCodes in transaction lines are mapped in ExternalTaxMappings | P0 | 6 | [ ] |
| A44-0401 | Check 4: Log warning for unusual currency codes (non-blocking) | P2 | 6 | [ ] |
| A44-0402 | Collect ALL errors in one pass — report all missing references at once | P0 | 6 | [ ] |
| A44-0403 | Create `ValidationResult` DTO: IsValid, Errors (List\<string\>), CheckedAt | P0 | 6 | [ ] |

#### 6.2.3 Entity Sync Validation

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0404 | Deserialize EntitySnapshot from outbox PayloadJson | P0 | 6 | [ ] |
| A44-0405 | For Product entities: verify IncomeAccountCode mapped in ExternalAccountMappings | P0 | 6 | [ ] |
| A44-0406 | For Product entities: verify ExpenseAccountCode mapped in ExternalAccountMappings | P0 | 6 | [ ] |

#### 6.2.4 Integration with Outbox Processors

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0407 | Inject IPreSyncValidator into OutboxProcessor (C7) | P0 | 6 | [ ] |
| A44-0408 | Call ValidateFinancialPostingAsync BEFORE adapter call | P0 | 6 | [ ] |
| A44-0409 | On validation failure: set Status=Failed with all error messages, schedule retry | P0 | 6 | [ ] |
| A44-0410 | Inject IPreSyncValidator into EntitySyncOutboxProcessor (C14) | P0 | 6 | [ ] |
| A44-0411 | Call ValidateEntitySyncAsync BEFORE adapter call | P0 | 6 | [ ] |
| A44-0412 | Register IPreSyncValidator as Scoped in DI | P0 | 6 | [ ] |

#### 6.2.5 Coverage API

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0413 | Create `MappingCoverageReport` DTO: Provider, Accounts (CoverageDetail), Customers, Vendors, Products, TaxCodes, ReadyForSync, BlockingIssues | P0 | 6 | [ ] |
| A44-0414 | Create `CoverageDetail` DTO: TotalInSms, Mapped, Unmapped, CoveragePercent | P0 | 6 | [ ] |
| A44-0415 | Implement `GET /api/finance/mappings/coverage/{provider}` — comprehensive coverage report | P0 | 6 | [ ] |
| A44-0416 | Implement `POST /api/finance/validation/check` — dry-run validation on a pending document | P1 | 6 | [ ] |

#### 6.2.6 Pre-Sync Validation UI

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0417 | Create React component: ValidationReportPanel at `/settings/finance/integrations/quickbooks/validation` | P0 | 6 | [ ] |
| A44-0418 | Build overall readiness indicator: READY / NOT READY (with blocking issue count) | P0 | 6 | [ ] |
| A44-0419 | Build coverage progress bars per mapping type (Accounts, Customers, Vendors, Products, Tax Codes) | P0 | 6 | [ ] |
| A44-0420 | List blocking issues (specific unmapped items) | P0 | 6 | [ ] |
| A44-0421 | Add "Refresh Coverage" and "Go to Account/Entity/Tax Mapping" navigation links | P1 | 6 | [ ] |

### 6.3 C18 — Sync Control Configuration & POS Hold/Release

#### 6.3.1 SyncControlConfig Entity

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0422 | Create `SyncControlConfig` entity in `SMS.Finance/Entities/` | P0 | 6 | [ ] |
| A44-0423 | Add properties: Id (int, PK), OrgId (int), Provider (ExternalSystemType), DocumentType (nvarchar(50)) | P0 | 6 | [ ] |
| A44-0424 | Add property: IsEnabled (bit) | P0 | 6 | [ ] |
| A44-0425 | Add property: HoldWhenDisabled (bit) — when true + disabled, writes Held instead of skip | P0 | 6 | [ ] |
| A44-0426 | Add properties: DisplayLabel (nvarchar(100)?), UpdatedAtUtc (datetime2), UpdatedBy (string) | P1 | 6 | [ ] |

#### 6.3.2 SyncControlConfig EF Core Configuration

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0427 | Map to table `SyncControlConfigs` in `finance` schema | P0 | 6 | [ ] |
| A44-0428 | Configure unique index on (OrgId, Provider, DocumentType) | P0 | 6 | [ ] |

#### 6.3.3 SyncControlService

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0429 | Create `ISyncControlService` interface in `SMS.Finance/Services/` | P0 | 6 | [ ] |
| A44-0430 | Add method: `CheckAsync(orgId, provider, documentType) → SyncControlDecision` | P0 | 6 | [ ] |
| A44-0431 | Add method: `ReleaseHeldEntriesAsync(orgId, provider, documentType) → int` | P0 | 6 | [ ] |
| A44-0432 | Add method: `SeedDefaultsAsync(orgId, provider)` | P0 | 6 | [ ] |
| A44-0433 | Create `SyncControlService` implementing ISyncControlService | P0 | 6 | [ ] |
| A44-0434 | Implement CheckAsync: no config row = enabled (backward compatible) | P0 | 6 | [ ] |
| A44-0435 | Create `SyncControlDecision` DTO: IsEnabled, HoldWhenDisabled, Action (Process/Hold/Skip) | P0 | 6 | [ ] |
| A44-0436 | Implement ReleaseHeldEntriesAsync: update FinancialPostingOutbox + EntitySyncOutbox entries from Held → Pending | P0 | 6 | [ ] |
| A44-0437 | Implement SeedDefaultsAsync: seed default configs (SalesInvoice=enabled, POS=disabled+hold, etc.) | P0 | 6 | [ ] |
| A44-0438 | Register ISyncControlService as Scoped in DI | P0 | 6 | [ ] |

#### 6.3.4 OutboxStatus Enum Extension

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0439 | Add `Held` value to OutboxStatus enum | P0 | 6 | [ ] |
| A44-0440 | Update OutboxProcessor: skip entries with Status = Held | P0 | 6 | [ ] |
| A44-0441 | Update EntitySyncOutboxProcessor: skip entries with Status = Held | P0 | 6 | [ ] |

#### 6.3.5 Gateway Integration

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0442 | Inject ISyncControlService into FinancialPostingGateway | P0 | 6 | [ ] |
| A44-0443 | Call CheckAsync before writing outbox entry | P0 | 6 | [ ] |
| A44-0444 | On SyncAction.Skip: do not create outbox entry (silent skip) | P0 | 6 | [ ] |
| A44-0445 | On SyncAction.Hold: create outbox entry with Status = Held | P0 | 6 | [ ] |
| A44-0446 | Call SeedDefaultsAsync from OAuth callback when a new provider is first connected | P0 | 6 | [ ] |

#### 6.3.6 Sync Control APIs

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0447 | Implement `GET /api/finance/sync-control/{provider}` — list sync control configs | P0 | 6 | [ ] |
| A44-0448 | Implement `PUT /api/finance/sync-control/{provider}/{documentType}` — update enable/disable and hold mode | P0 | 6 | [ ] |
| A44-0449 | On PUT with isEnabled transitioning false → true and HoldWhenDisabled = true: auto-call ReleaseHeldEntriesAsync | P0 | 6 | [ ] |
| A44-0450 | Implement `POST /api/finance/sync-control/{provider}/{documentType}/release` — manual release of held entries | P0 | 6 | [ ] |

#### 6.3.7 Sync Control UI

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0451 | Create React component: SyncControlPanel at `/settings/finance/integrations/quickbooks/sync-control` | P0 | 6 | [ ] |
| A44-0452 | Build per-document-type rows: Document Type, Sync toggle (ON/OFF), Hold toggle (ON/OFF), Status badge | P0 | 6 | [ ] |
| A44-0453 | Show held entry count per document type (e.g., "○ Held (47)") | P0 | 6 | [ ] |
| A44-0454 | Show info banner: "Enabling a held type will release N queued transactions" | P1 | 6 | [ ] |
| A44-0455 | Add Save Configuration button with confirmation when releasing held entries | P0 | 6 | [ ] |

#### 6.3.8 Migrations M15–M16

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0456 | Write EF Core migration `AddSyncControlConfigs` (M15) | P0 | 6 | [ ] |
| A44-0457 | Verify M15 creates table with unique index | P0 | 6 | [ ] |
| A44-0458 | Write EF Core migration `AddOutboxHeldStatus` (M16) — update check constraint on Status columns | P0 | 6 | [ ] |
| A44-0459 | Verify M16 adds Held to allowed values in both outbox tables | P0 | 6 | [ ] |

---

## Phase 7 — Document Lifecycle (C17) — 1 Week

### 7.1 C17 — Document Lifecycle Gateway (Void / Cancel / Reversal)

#### 7.1.1 Extended Gateway Interface

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0460 | Add `VoidAsync(VoidRequest request)` method to `IFinancialPostingGateway` | P0 | 7 | [ ] |
| A44-0461 | Add `IssueCreditAsync(CreditRequest request)` method to `IFinancialPostingGateway` | P0 | 7 | [ ] |

#### 7.1.2 Request DTOs

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0462 | Create `VoidRequest` DTO: OrgId, SourceModule, DocumentType, SourceDocumentId, Reason, VoidedBy | P0 | 7 | [ ] |
| A44-0463 | Add computed IdempotencyKey: `{module}:{docType}:{docId}:void` | P0 | 7 | [ ] |
| A44-0464 | Create `CreditRequest` DTO: OrgId, SourceModule, OriginalDocumentType, OriginalDocumentId, CreditDocumentType, CreditDocumentId, CreditAmount, Lines, IssuedBy | P0 | 7 | [ ] |
| A44-0465 | Add computed IdempotencyKey: `{module}:{creditDocType}:{creditDocId}` | P0 | 7 | [ ] |

#### 7.1.3 VoidAsync Implementation

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0466 | Implement license check and idempotency check in VoidAsync | P0 | 7 | [ ] |
| A44-0467 | Lookup external document ID from SyncLogs for void target — if never synced, skip external void | P0 | 7 | [ ] |
| A44-0468 | Handle supplier invoice reversal (PurchaseBill from Purchase module): flag-only, no QB call | P0 | 7 | [ ] |
| A44-0469 | Write SyncLog entry with Status = "FlaggedForReview" for supplier reversals | P0 | 7 | [ ] |
| A44-0470 | For all other document types: write outbox entry with DocumentType = "{type}:Void" | P0 | 7 | [ ] |
| A44-0471 | Create reversing journal entry in internal GL for all voids | P0 | 7 | [ ] |

#### 7.1.4 IssueCreditAsync Implementation

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0472 | Implement IssueCreditAsync: validate credit amount and lines | P0 | 7 | [ ] |
| A44-0473 | Write outbox entry with CreditDocumentType (CreditNote, VendorCredit, RefundReceipt) | P0 | 7 | [ ] |
| A44-0474 | Create credit/debit journal entries in internal GL | P0 | 7 | [ ] |

#### 7.1.5 QB Void Adapter Extension

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0475 | Add `VoidDocumentAsync(orgId, documentType, externalId)` method to QuickBooksTarget | P0 | 7 | [ ] |
| A44-0476 | Fetch current SyncToken via GET before void | P0 | 7 | [ ] |
| A44-0477 | Map document types: SalesInvoice:Void → `POST /invoice?operation=void` | P0 | 7 | [ ] |
| A44-0478 | Map: PurchaseBill:Void → `POST /bill?operation=void` | P0 | 7 | [ ] |
| A44-0479 | Map: Payment:Void → `POST /payment?operation=void` | P0 | 7 | [ ] |
| A44-0480 | Map: BillPayment:Void → `POST /billpayment?operation=void` | P0 | 7 | [ ] |

#### 7.1.6 QB Credit Adapter Extension

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0481 | Add credit memo creation: build QB CreditMemo JSON, `POST /creditmemo` | P0 | 7 | [ ] |
| A44-0482 | Add vendor credit creation: build QB VendorCredit JSON, `POST /vendorcredit` | P0 | 7 | [ ] |
| A44-0483 | Add refund receipt creation: build QB RefundReceipt JSON, `POST /refundreceipt` | P1 | 7 | [ ] |

#### 7.1.7 FinDocType Enum Extension

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0484 | Verify all 19 FinDocType values exist in enum (including Expense, VendorCredit, RefundReceipt, SalesReceipt, DepositSlip) | P0 | 7 | [ ] |

#### 7.1.8 Document Lifecycle APIs

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0485 | Implement `POST /api/finance/gateway/void` — void a synced document | P0 | 7 | [ ] |
| A44-0486 | Implement `POST /api/finance/gateway/credit` — issue credit memo | P0 | 7 | [ ] |
| A44-0487 | Implement `GET /api/finance/sync/flagged-reviews` — list flagged supplier reversals | P0 | 7 | [ ] |
| A44-0488 | Implement `POST /api/finance/sync/flagged-reviews/{id}/resolve` — mark reviewed | P0 | 7 | [ ] |

#### 7.1.9 Core Module Void Callers

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0489 | Add VoidAsync() call in SalesInvoiceService on invoice cancellation | P0 | 7 | [ ] |
| A44-0490 | Add VoidAsync() call in PurchaseBillService on bill reversal (flag-only) | P0 | 7 | [ ] |
| A44-0491 | Add IssueCreditAsync() call in CreditNoteService on credit note creation | P1 | 7 | [ ] |
| A44-0492 | Add VoidAsync() call in PaymentService on payment void | P1 | 7 | [ ] |

---

## Phase 8 — Testing & Stabilization — 2 Weeks

### 8.1 Gateway Routing Tests (T-01 to T-08)

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0493 | T-01: No finance license → PostAsync returns ModuleNotLicensed, invoice still posts | P0 | 8 | [ ] |
| A44-0494 | T-02: No targets configured → returns NoTargetConfigured | P0 | 8 | [ ] |
| A44-0495 | T-03: Internal GL only → JournalEntry created, no outbox entry | P0 | 8 | [ ] |
| A44-0496 | T-04: QuickBooks only → no JournalEntry, one outbox entry | P0 | 8 | [ ] |
| A44-0497 | T-05: Both internal + QB → JournalEntry AND outbox entry | P0 | 8 | [ ] |
| A44-0498 | T-06: Unbalanced entry → ValidationFailed | P0 | 8 | [ ] |
| A44-0499 | T-07: Duplicate posting → DuplicateSkipped | P0 | 8 | [ ] |
| A44-0500 | T-08: Multiple external providers → one outbox entry per provider | P0 | 8 | [ ] |

### 8.2 Outbox Processing Tests (T-09 to T-15)

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0501 | T-09: Successful QB push → Completed, ExternalId populated | P0 | 8 | [ ] |
| A44-0502 | T-10: QB 401 → token refresh, retry succeeds | P0 | 8 | [ ] |
| A44-0503 | T-11: QB 500 → Failed, retryCount++, scheduled for retry | P0 | 8 | [ ] |
| A44-0504 | T-12: Max retries exceeded → DeadLetter | P0 | 8 | [ ] |
| A44-0505 | T-13: Dead letter manual retry → reset to Pending | P0 | 8 | [ ] |
| A44-0506 | T-14: Unmapped account → clear error message | P0 | 8 | [ ] |
| A44-0507 | T-15: Unmapped customer → clear error message | P0 | 8 | [ ] |

### 8.3 Enrichment Tests (T-16 to T-20)

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0508 | T-16: Provider defaults exist → QB Class, Dept, Terms attached | P0 | 8 | [ ] |
| A44-0509 | T-17: Document-type override → POS sale uses override ClassRef | P0 | 8 | [ ] |
| A44-0510 | T-18: No defaults configured → empty ProviderFields, QB doc created without optional fields | P0 | 8 | [ ] |
| A44-0511 | T-19: Account mappings complete → all codes resolved | P0 | 8 | [ ] |
| A44-0512 | T-20: Partial mappings → clear error per unmapped account | P0 | 8 | [ ] |

### 8.4 Transaction Atomicity Tests (T-21 to T-23)

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0513 | T-21: GL insert fails → rollback all (invoice unposted, no outbox) | P0 | 8 | [ ] |
| A44-0514 | T-22: Outbox insert fails → rollback all (invoice unposted, no GL) | P0 | 8 | [ ] |
| A44-0515 | T-23: SaveChanges succeeds → invoice + GL + outbox in one commit | P0 | 8 | [ ] |

### 8.5 Entity Sync Gateway Tests (T-24 to T-31)

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0516 | T-24: No finance license → ModuleNotLicensed, customer still saves | P0 | 8 | [ ] |
| A44-0517 | T-25: No providers with EntitySyncEnabled → NoTargetConfigured | P0 | 8 | [ ] |
| A44-0518 | T-26: QB connected, entity sync enabled → one outbox entry | P0 | 8 | [ ] |
| A44-0519 | T-27: QB connected, entity sync disabled → NoTargetConfigured | P0 | 8 | [ ] |
| A44-0520 | T-28: Duplicate sync request → DuplicateSkipped | P0 | 8 | [ ] |
| A44-0521 | T-29: Multiple providers → one outbox entry per provider | P0 | 8 | [ ] |
| A44-0522 | T-30: Batch push 50 entities → 50 entries, duplicates skipped | P1 | 8 | [ ] |
| A44-0523 | T-31: Entity + financial in same transaction → both outbox types committed atomically | P0 | 8 | [ ] |

### 8.6 Entity Sync Outbox Processing Tests (T-32 to T-39)

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0524 | T-32: Successful customer push → Completed, mapping created, SyncLog written | P0 | 8 | [ ] |
| A44-0525 | T-33: Successful vendor push | P0 | 8 | [ ] |
| A44-0526 | T-34: Successful product push → IncomeAccountRef resolved | P0 | 8 | [ ] |
| A44-0527 | T-35: Product with unmapped income account → retryable error | P0 | 8 | [ ] |
| A44-0528 | T-36: QB 401 on entity push → token refresh, retry | P0 | 8 | [ ] |
| A44-0529 | T-37: QB DisplayName conflict → entry fails, retryable | P0 | 8 | [ ] |
| A44-0530 | T-38: Max retries → DeadLetter | P0 | 8 | [ ] |
| A44-0531 | T-39: Customer update → fetch SyncToken, sparse update | P0 | 8 | [ ] |

### 8.7 Initial Import Tests (T-40 to T-44)

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0532 | T-40: Import QB customers → paginated fetch, mappings with InternalEntityId=0 | P0 | 8 | [ ] |
| A44-0533 | T-41: Import with existing mappings → skipped, count reported | P0 | 8 | [ ] |
| A44-0534 | T-42: Import QB items → mappings created, no SMS Products created | P0 | 8 | [ ] |
| A44-0535 | T-43: Import QB accounts → ExternalAccountMappings created | P0 | 8 | [ ] |
| A44-0536 | T-44: User links imported entity → InternalEntityId updated | P0 | 8 | [ ] |

### 8.8 Tax Code Mapping Tests (T-45 to T-49)

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0537 | T-45: Import QB tax codes → ExternalTaxMapping entries created with empty SmsTaxCode | P0 | 8 | [ ] |
| A44-0538 | T-46: Map SMS tax code to QB → enrichment resolves correctly | P0 | 8 | [ ] |
| A44-0539 | T-47: TAX-EXEMPT auto-maps to QB NON | P0 | 8 | [ ] |
| A44-0540 | T-48: Unmapped tax code → outbox failure with retryable error | P0 | 8 | [ ] |
| A44-0541 | T-49: Tax code enrichment during posting → ExternalTaxCodeId resolved | P0 | 8 | [ ] |

### 8.9 Pre-Sync Validation Tests (T-50 to T-54)

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0542 | T-50: All references mapped → IsValid=true | P0 | 8 | [ ] |
| A44-0543 | T-51: Multiple missing references → all 3+ errors reported at once | P0 | 8 | [ ] |
| A44-0544 | T-52: Validation failure in outbox → Failed with all errors, retry scheduled | P0 | 8 | [ ] |
| A44-0545 | T-53: Product with unmapped accounts → validator reports both | P0 | 8 | [ ] |
| A44-0546 | T-54: Coverage API → returns counts for all entity types | P0 | 8 | [ ] |

### 8.10 Document Lifecycle Tests (T-55 to T-61)

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0547 | T-55: Void sales invoice → outbox with SalesInvoice:Void, QB void called | P0 | 8 | [ ] |
| A44-0548 | T-56: Void purchase bill (supplier reversal) → flag-only, SyncLog FlaggedForReview | P0 | 8 | [ ] |
| A44-0549 | T-57: Void customer payment → QB void with SyncToken | P0 | 8 | [ ] |
| A44-0550 | T-58: Issue sales credit note → QB CreditMemo created | P0 | 8 | [ ] |
| A44-0551 | T-59: Issue vendor credit → QB VendorCredit created | P0 | 8 | [ ] |
| A44-0552 | T-60: Void document never synced → no-op for external, GL reversal still created | P0 | 8 | [ ] |
| A44-0553 | T-61: Void duplicate idempotency → DuplicateSkipped | P0 | 8 | [ ] |

### 8.11 Sync Control Tests (T-62 to T-68)

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0554 | T-62: Document type disabled (skip) → no outbox entry | P0 | 8 | [ ] |
| A44-0555 | T-63: Document type disabled (hold) → outbox entry with Held status | P0 | 8 | [ ] |
| A44-0556 | T-64: Enable held type → all Held entries released to Pending | P0 | 8 | [ ] |
| A44-0557 | T-65: No config row → processed normally (backward compatible) | P0 | 8 | [ ] |
| A44-0558 | T-66: Seed defaults on connect → configs created | P0 | 8 | [ ] |
| A44-0559 | T-67: POS hold accumulation → 100 Held entries, 0 processed | P0 | 8 | [ ] |
| A44-0560 | T-68: POS release batch → all held entries move to Pending | P0 | 8 | [ ] |

### 8.12 Business Rule Verification

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0561 | BR-01: Verify balanced debits/credits validation in gateway | P0 | 8 | [ ] |
| A44-0562 | BR-02: Verify idempotency key dedup in financial posting | P0 | 8 | [ ] |
| A44-0563 | BR-03: Verify module license check (silent return if not licensed) | P0 | 8 | [ ] |
| A44-0564 | BR-04: Verify no-target-configured behavior | P0 | 8 | [ ] |
| A44-0565 | BR-05: Verify atomic transaction (GL + source doc in one SaveChanges) | P0 | 8 | [ ] |
| A44-0566 | BR-06: Verify external calls are always via outbox (never synchronous) | P0 | 8 | [ ] |
| A44-0567 | BR-07: Verify exponential backoff: 30s, 2m, 8m, 32m, 2h | P0 | 8 | [ ] |
| A44-0568 | BR-08: Verify dead letter after 5 retries | P0 | 8 | [ ] |
| A44-0569 | BR-09: Verify OAuth tokens encrypted at rest | P0 | 8 | [ ] |
| A44-0570 | BR-10: Verify auto token refresh on 401 | P0 | 8 | [ ] |
| A44-0571 | BR-11: Verify per-entry failure (unmapped account doesn't fail entire posting) | P0 | 8 | [ ] |
| A44-0572 | BR-12: Verify provider fields come from defaults/overrides, not core code | P1 | 8 | [ ] |
| A44-0573 | BR-13: Verify FinanceDbContext has no cross-schema FKs | P0 | 8 | [ ] |
| A44-0574 | BR-14: Verify source doc identity stored as plain values | P0 | 8 | [ ] |
| A44-0575 | BR-15 to BR-22: Verify entity sync rules (gateway enforcement, async, retries, import, sparse update, account resolution) | P0 | 8 | [ ] |
| A44-0576 | BR-23 to BR-28: Verify tax mapping, pre-sync validation, void, credit rules | P0 | 8 | [ ] |
| A44-0577 | BR-29 to BR-33: Verify sync control rules (defaults, hold/release, seed) | P0 | 8 | [ ] |

### 8.13 E2E & QB Sandbox Validation

| ID | Task | Priority | Phase | Status |
|---|---|---|---|---|
| A44-0578 | E2E: Full flow — connect QB → map accounts → map entities → map tax codes → issue invoice → verify QB Invoice created | P0 | 8 | [ ] |

---

## Summary

| Phase | Change Items | Task Count | Duration |
|---|---|---|---|
| Phase 1 — Gateway Foundation | C1, C2, C4 | 59 | 2 weeks |
| Phase 2 — Configuration & Mapping | C3, C5, C6 | 74 | 2 weeks |
| Phase 3 — Outbox & QuickBooks | C7, C8 | 59 | 3 weeks |
| Phase 4 — Monitoring & Integration | C9, C10 | 70 | 2 weeks |
| Phase 5 — Entity Sync | C11, C12, C13, C14 | 99 | 2 weeks |
| Phase 6 — Tax, Validation & Sync Control | C15, C16, C18 | 98 | 2 weeks |
| Phase 7 — Document Lifecycle | C17 | 33 | 1 week |
| Phase 8 — Testing & Stabilization | All | 86 | 2 weeks |
| **Total** | **C1–C18** | **578** | **16 weeks** |

---

*End of Task Register — FSD Addendum 44*
