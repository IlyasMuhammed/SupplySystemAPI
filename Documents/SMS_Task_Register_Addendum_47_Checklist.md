# SMS Task Register — Addendum 47: General Ledger Journal Engine

| Property | Value |
|---|---|
| Addendum | A47 — General Ledger Journal Engine |
| Total Tasks | 348 |
| Date | 2026-10-24 |
| Status | All Pending |
| Depends On | A42 (Chart of Accounts), A44 (Financial Posting Gateway) |

---

## Legend

- [ ] Pending
- `BE` = Backend | `FE` = Frontend | `DB` = Database | `QA` = Testing | `DOC` = Documentation
- Priority: `P0` = Blocker | `P1` = High | `P2` = Medium | `P3` = Low
- Effort in days (d) or hours (h)

---

## Phase 1 — Foundation (Entities, Migrations, Journal Types)

### 1.1 Migration M1 — CreateJournalTypes (C1)

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 001 | Create EF Core entity class `JournalType` in SMS.Finance.Entities | BE | P0 | 2h | [ ] |
| 002 | Add `JournalType` properties: id, org_id, code, name, type, sequence_prefix, default_debit_account_id, default_credit_account_id | BE | P0 | 1h | [ ] |
| 003 | Add `JournalType` properties: requires_partner, auto_post, is_system, is_active | BE | P0 | 1h | [ ] |
| 004 | Add `JournalType` audit properties: created_at, created_by, modified_at, modified_by | BE | P0 | 0.5h | [ ] |
| 005 | Add `JournalType` type enum: Sale=0, Purchase=1, Cash=2, Bank=3, Miscellaneous=4, Opening=5, Closing=6 | BE | P0 | 0.5h | [ ] |
| 006 | Create EF Core configuration class `JournalTypeConfiguration` | BE | P0 | 2h | [ ] |
| 007 | Configure UQ_JournalTypes_OrgCode unique index (org_id, code) | DB | P0 | 0.5h | [ ] |
| 008 | Configure IX_JournalTypes_OrgType index (org_id, type) | DB | P0 | 0.5h | [ ] |
| 009 | Configure IX_JournalTypes_Active filtered index (org_id, is_active WHERE is_active = 1) | DB | P0 | 0.5h | [ ] |
| 010 | Add CHECK constraint: type BETWEEN 0 AND 6 | DB | P0 | 0.5h | [ ] |
| 011 | Add CHECK constraint: code uppercase alphanumeric max 10 chars | DB | P2 | 0.5h | [ ] |
| 012 | Register `JournalType` in `FinanceDbContext.OnModelCreating()` | BE | P0 | 0.5h | [ ] |
| 013 | Create EF Core migration `CreateJournalTypes` | DB | P0 | 1h | [ ] |
| 014 | Verify migration generates correct DDL against SQL Server 2022 | QA | P0 | 0.5h | [ ] |
| 015 | Add `DbSet<JournalType> JournalTypes` to `FinanceDbContext` | BE | P0 | 0.5h | [ ] |

### 1.2 Migration M8 — Seed Journal Types (C1)

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 016 | Create seed data method for 7 system journal types (SALE, PURCH, CASH, BANK, MISC, OPEN, CLOSE) | BE | P0 | 1h | [ ] |
| 017 | Seed SALE type: code=SALE, name=Sales Journal, type=0, prefix=SALE, requires_partner=true, is_system=true | DB | P0 | 0.5h | [ ] |
| 018 | Seed PURCH type: code=PURCH, name=Purchase Journal, type=1, prefix=PURCH, requires_partner=true, is_system=true | DB | P0 | 0.5h | [ ] |
| 019 | Seed CASH type: code=CASH, name=Cash Journal, type=2, prefix=CASH, is_system=true | DB | P0 | 0.5h | [ ] |
| 020 | Seed BANK type: code=BANK, name=Bank Journal, type=3, prefix=BANK, is_system=true | DB | P0 | 0.5h | [ ] |
| 021 | Seed MISC type: code=MISC, name=Miscellaneous Journal, type=4, prefix=JV, is_system=true | DB | P0 | 0.5h | [ ] |
| 022 | Seed OPEN type: code=OPEN, name=Opening Journal, type=5, prefix=OPEN, auto_post=true, is_system=true | DB | P0 | 0.5h | [ ] |
| 023 | Seed CLOSE type: code=CLOSE, name=Closing Journal, type=6, prefix=CLOSE, auto_post=true, is_system=true | DB | P0 | 0.5h | [ ] |
| 024 | Create migration `SeedJournalTypesForExistingTenants` — seed for orgs with CoA setup complete | DB | P1 | 1h | [ ] |
| 025 | Integrate seed into CoA setup activation flow (A42) for new tenants | BE | P1 | 1h | [ ] |
| 026 | Verify only one Opening type and one Closing type per org_id | QA | P1 | 0.5h | [ ] |

### 1.3 Migration M2 — AlterFiscalPeriodStatus (C2)

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 027 | Drop existing CK_status constraint on finance.FiscalPeriods | DB | P0 | 0.5h | [ ] |
| 028 | Add new CK_status constraint: status IN ('Open', 'AdjustmentOnly', 'Closed', 'Locked') | DB | P0 | 0.5h | [ ] |
| 029 | Update `FiscalPeriod` entity class to include `AdjustmentOnly` status option | BE | P0 | 0.5h | [ ] |
| 030 | Update `FiscalPeriodStatus` enum/constants to include `AdjustmentOnly` | BE | P0 | 0.5h | [ ] |
| 031 | Create EF Core migration `AlterFiscalPeriodStatus` | DB | P0 | 0.5h | [ ] |
| 032 | Verify existing data is not affected (all current statuses remain valid) | QA | P0 | 0.5h | [ ] |
| 033 | Update A42 FiscalPeriod validation logic to recognize AdjustmentOnly | BE | P1 | 1h | [ ] |

### 1.4 Migration M3 — CreateJournalEntries (C3)

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 034 | Create EF Core entity class `JournalEntry` in SMS.Finance.Entities | BE | P0 | 2h | [ ] |
| 035 | Add `JournalEntry` core properties: id, org_id, entry_number, journal_type_id, entry_date, document_date | BE | P0 | 1h | [ ] |
| 036 | Add `JournalEntry` fiscal properties: fiscal_year_id, fiscal_period_id | BE | P0 | 0.5h | [ ] |
| 037 | Add `JournalEntry` description properties: reference, description | BE | P0 | 0.5h | [ ] |
| 038 | Add `JournalEntry` source document properties: source_document_type, source_document_id, source_module | BE | P0 | 0.5h | [ ] |
| 039 | Add `JournalEntry` currency properties: currency_id, exchange_rate | BE | P0 | 0.5h | [ ] |
| 040 | Add `JournalEntry` status properties: status (0=Draft, 1=Posted, 2=Reversed), posted_at, posted_by | BE | P0 | 1h | [ ] |
| 041 | Add `JournalEntry` reversal properties: reversed_by_entry_id, reversal_of_entry_id, reversal_reason | BE | P0 | 0.5h | [ ] |
| 042 | Add `JournalEntry` idempotency/flags: idempotency_key, is_opening_entry, is_closing_entry | BE | P0 | 0.5h | [ ] |
| 043 | Add `JournalEntry` totals: total_debit, total_credit, line_count | BE | P0 | 0.5h | [ ] |
| 044 | Add `JournalEntry` audit: created_at, created_by, updated_at, updated_by | BE | P0 | 0.5h | [ ] |
| 045 | Create EF Core configuration class `JournalEntryConfiguration` | BE | P0 | 2h | [ ] |
| 046 | Configure UQ_JournalEntries_OrgId_EntryNumber unique index | DB | P0 | 0.5h | [ ] |
| 047 | Configure UQ_JournalEntries_OrgId_IdempotencyKey unique index | DB | P0 | 0.5h | [ ] |
| 048 | Configure IX_JournalEntries_OrgId_Status index | DB | P0 | 0.5h | [ ] |
| 049 | Configure IX_JournalEntries_OrgId_EntryDate index | DB | P0 | 0.5h | [ ] |
| 050 | Configure IX_JournalEntries_JournalTypeId index | DB | P0 | 0.5h | [ ] |
| 051 | Configure IX_JournalEntries_FiscalPeriodId index | DB | P0 | 0.5h | [ ] |
| 052 | Configure IX_JournalEntries_FiscalYearId index | DB | P0 | 0.5h | [ ] |
| 053 | Configure IX_JournalEntries_SourceDoc index (org_id, source_document_type, source_document_id) | DB | P0 | 0.5h | [ ] |
| 054 | Configure IX_JournalEntries_PostedAt filtered index (WHERE status = 1) | DB | P1 | 0.5h | [ ] |
| 055 | Add CK_JE_Status CHECK constraint: status IN (0, 1, 2) | DB | P0 | 0.5h | [ ] |
| 056 | Add CK_JE_Balanced CHECK constraint: ABS(total_debit - total_credit) < 0.01 | DB | P0 | 0.5h | [ ] |
| 057 | Configure FK journal_type_id → finance.JournalTypes.id | DB | P0 | 0.5h | [ ] |
| 058 | Configure FK fiscal_year_id → finance.FiscalYears.id | DB | P0 | 0.5h | [ ] |
| 059 | Configure FK fiscal_period_id → finance.FiscalPeriods.id | DB | P0 | 0.5h | [ ] |
| 060 | Configure self-referencing FK reversed_by_entry_id → JournalEntries.id | DB | P0 | 0.5h | [ ] |
| 061 | Configure self-referencing FK reversal_of_entry_id → JournalEntries.id | DB | P0 | 0.5h | [ ] |
| 062 | Add navigation property `JournalType` on `JournalEntry` | BE | P1 | 0.5h | [ ] |
| 063 | Add navigation property `FiscalYear` on `JournalEntry` | BE | P1 | 0.5h | [ ] |
| 064 | Add navigation property `FiscalPeriod` on `JournalEntry` | BE | P1 | 0.5h | [ ] |
| 065 | Add navigation property `Lines` (ICollection<JournalEntryLine>) on `JournalEntry` | BE | P0 | 0.5h | [ ] |
| 066 | Register `JournalEntry` in `FinanceDbContext.OnModelCreating()` | BE | P0 | 0.5h | [ ] |
| 067 | Add `DbSet<JournalEntry> JournalEntries` to `FinanceDbContext` | BE | P0 | 0.5h | [ ] |
| 068 | Create EF Core migration `CreateJournalEntries` | DB | P0 | 1h | [ ] |
| 069 | Verify migration generates correct DDL against SQL Server 2022 | QA | P0 | 0.5h | [ ] |

### 1.5 Migration M4 — CreateJournalEntryLines (C4)

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 070 | Create EF Core entity class `JournalEntryLine` in SMS.Finance.Entities | BE | P0 | 2h | [ ] |
| 071 | Add `JournalEntryLine` core properties: id, org_id, journal_entry_id, line_number, account_id | BE | P0 | 1h | [ ] |
| 072 | Add `JournalEntryLine` partner properties: partner_id, partner_type | BE | P0 | 0.5h | [ ] |
| 073 | Add `JournalEntryLine` amount properties: description, debit, credit | BE | P0 | 0.5h | [ ] |
| 074 | Add `JournalEntryLine` multi-currency properties: amount_currency, currency_id, exchange_rate | BE | P0 | 0.5h | [ ] |
| 075 | Add `JournalEntryLine` local amount properties: local_debit, local_credit | BE | P0 | 0.5h | [ ] |
| 076 | Add `JournalEntryLine` dimensional properties: cost_center_id, project_id | BE | P1 | 0.5h | [ ] |
| 077 | Add `JournalEntryLine` reconciliation properties: reconciled, reconcile_group_id, clearing_document_id, clearing_date, amount_residual | BE | P0 | 1h | [ ] |
| 078 | Add `JournalEntryLine` tax properties: tax_code_id, tax_amount | BE | P1 | 0.5h | [ ] |
| 079 | Add `JournalEntryLine` audit: created_at | BE | P0 | 0.5h | [ ] |
| 080 | Create EF Core configuration class `JournalEntryLineConfiguration` | BE | P0 | 2h | [ ] |
| 081 | Configure IX_JEL_Entry index (journal_entry_id) | DB | P0 | 0.5h | [ ] |
| 082 | Configure IX_JEL_Account index (org_id, account_id) | DB | P0 | 0.5h | [ ] |
| 083 | Configure IX_JEL_AccountClearing index (org_id, account_id, clearing_date) | DB | P0 | 0.5h | [ ] |
| 084 | Configure IX_JEL_Partner index (org_id, partner_id, partner_type, reconciled) | DB | P0 | 0.5h | [ ] |
| 085 | Configure IX_JEL_Reconcile filtered index (WHERE reconcile_group_id IS NOT NULL) | DB | P1 | 0.5h | [ ] |
| 086 | Configure IX_JEL_OpenItems filtered index (WHERE reconciled = 0 AND (debit > 0 OR credit > 0)) | DB | P1 | 0.5h | [ ] |
| 087 | Configure IX_JEL_CostCenter filtered index (WHERE cost_center_id IS NOT NULL) | DB | P2 | 0.5h | [ ] |
| 088 | Add CK_JEL_DebitOrCredit CHECK constraint | DB | P0 | 0.5h | [ ] |
| 089 | Add partner_type CHECK: IN ('Customer', 'Vendor') OR NULL | DB | P0 | 0.5h | [ ] |
| 090 | Configure FK journal_entry_id → finance.JournalEntries.id (CASCADE DELETE) | DB | P0 | 0.5h | [ ] |
| 091 | Configure FK account_id → finance.ChartOfAccounts.id | DB | P0 | 0.5h | [ ] |
| 092 | Configure FK reconcile_group_id → finance.ReconcileGroups.id | DB | P0 | 0.5h | [ ] |
| 093 | Configure FK clearing_document_id → finance.JournalEntries.id | DB | P0 | 0.5h | [ ] |
| 094 | Add navigation property `JournalEntry` on `JournalEntryLine` | BE | P1 | 0.5h | [ ] |
| 095 | Add navigation property `Account` (ChartOfAccounts) on `JournalEntryLine` | BE | P1 | 0.5h | [ ] |
| 096 | Register `JournalEntryLine` in `FinanceDbContext.OnModelCreating()` | BE | P0 | 0.5h | [ ] |
| 097 | Add `DbSet<JournalEntryLine> JournalEntryLines` to `FinanceDbContext` | BE | P0 | 0.5h | [ ] |
| 098 | Create EF Core migration `CreateJournalEntryLines` | DB | P0 | 1h | [ ] |
| 099 | Verify migration generates correct DDL against SQL Server 2022 | QA | P0 | 0.5h | [ ] |

### 1.6 Migration M5 — CreateJournalEntrySequences (C5)

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 100 | Create EF Core entity class `JournalEntrySequence` in SMS.Finance.Entities | BE | P0 | 1h | [ ] |
| 101 | Add `JournalEntrySequence` properties: id, org_id, journal_type_id, fiscal_year_id, current_sequence | BE | P0 | 0.5h | [ ] |
| 102 | Add `JournalEntrySequence` audit: created_at, updated_at | BE | P0 | 0.5h | [ ] |
| 103 | Create EF Core configuration class `JournalEntrySequenceConfiguration` | BE | P0 | 1h | [ ] |
| 104 | Configure UQ_JES_OrgTypeYear unique index (org_id, journal_type_id, fiscal_year_id) | DB | P0 | 0.5h | [ ] |
| 105 | Configure FK journal_type_id → finance.JournalTypes.id | DB | P0 | 0.5h | [ ] |
| 106 | Configure FK fiscal_year_id → finance.FiscalYears.id | DB | P0 | 0.5h | [ ] |
| 107 | Register `JournalEntrySequence` in `FinanceDbContext.OnModelCreating()` | BE | P0 | 0.5h | [ ] |
| 108 | Add `DbSet<JournalEntrySequence> JournalEntrySequences` to `FinanceDbContext` | BE | P0 | 0.5h | [ ] |
| 109 | Create EF Core migration `CreateJournalEntrySequences` | DB | P0 | 0.5h | [ ] |

### 1.7 Migration M6 — CreateReconcileGroups (C8)

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 110 | Create EF Core entity class `ReconcileGroup` in SMS.Finance.Entities | BE | P0 | 1h | [ ] |
| 111 | Add `ReconcileGroup` properties: id, org_id, group_number, reconcile_type, amount, currency_id | BE | P0 | 0.5h | [ ] |
| 112 | Add `ReconcileGroup` audit: reconciled_at, created_by | BE | P0 | 0.5h | [ ] |
| 113 | Create EF Core configuration class `ReconcileGroupConfiguration` | BE | P0 | 1h | [ ] |
| 114 | Configure IX_ReconcileGroups_OrgId index | DB | P0 | 0.5h | [ ] |
| 115 | Configure UQ_ReconcileGroups_GroupNumber unique index (org_id, group_number) | DB | P0 | 0.5h | [ ] |
| 116 | Register `ReconcileGroup` in `FinanceDbContext.OnModelCreating()` | BE | P0 | 0.5h | [ ] |
| 117 | Add `DbSet<ReconcileGroup> ReconcileGroups` to `FinanceDbContext` | BE | P0 | 0.5h | [ ] |
| 118 | Create EF Core migration `CreateReconcileGroups` | DB | P0 | 0.5h | [ ] |

### 1.8 Migration M7 — ChartOfAccounts Trigger (C6)

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 119 | Create SQL trigger on JournalEntryLines INSERT to set has_transactions = 1 on ChartOfAccounts | DB | P1 | 1h | [ ] |
| 120 | Create EF Core migration `AddChartOfAccountsHasTransactions` | DB | P1 | 0.5h | [ ] |
| 121 | Verify trigger fires correctly on JournalEntryLine insert | QA | P1 | 0.5h | [ ] |
| 122 | Verify trigger does not fire on Draft entries (only on PostAsync CoA update) | QA | P1 | 0.5h | [ ] |

### 1.9 Migration Execution & Verification

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 123 | Verify migration dependency order: M1 → M2 → M3 → M6 → M4 → M5 → M7 → M8 | DB | P0 | 0.5h | [ ] |
| 124 | Run all 8 migrations against clean database | QA | P0 | 1h | [ ] |
| 125 | Run all 8 migrations against database with existing A42 + A44 data | QA | P0 | 1h | [ ] |
| 126 | Verify no cross-schema foreign keys exist in any migration | QA | P0 | 0.5h | [ ] |
| 127 | Verify all new tables are in `finance` schema | QA | P0 | 0.5h | [ ] |
| 128 | Verify rollback (Down) works for each migration | QA | P1 | 1h | [ ] |
| 129 | Verify multi-tenant data isolation (org_id on all new tables) | QA | P0 | 0.5h | [ ] |

### 1.10 FinanceDbContext Registration

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 130 | Register all 5 new entity DbSets in FinanceDbContext | BE | P0 | 0.5h | [ ] |
| 131 | Verify no entities reference AppDbContext | QA | P0 | 0.5h | [ ] |
| 132 | Verify EF Core model snapshot is consistent | QA | P0 | 0.5h | [ ] |
| 133 | Add OrgId query filter on all new entities for multi-tenancy | BE | P0 | 1h | [ ] |

---

## Phase 2 — Core Engine (Create, Validate, Post, List)

### 2.1 Sequence Generation Service (C5)

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 134 | Create `IJournalEntrySequenceService` interface in SMS.Finance.Interfaces | BE | P0 | 0.5h | [ ] |
| 135 | Define `GenerateEntryNumberAsync(int orgId, int journalTypeId, int fiscalYearId)` method | BE | P0 | 0.5h | [ ] |
| 136 | Create `JournalEntrySequenceService` implementation in SMS.Finance.Services | BE | P0 | 2h | [ ] |
| 137 | Implement row-level locking (UPDLOCK, HOLDLOCK) for concurrent safety | BE | P0 | 2h | [ ] |
| 138 | Implement auto-creation of sequence row for first entry of type/year combination | BE | P0 | 1h | [ ] |
| 139 | Implement entry number formatting: {prefix}/{fiscal_year}/{sequence:D4} | BE | P0 | 0.5h | [ ] |
| 140 | Register `IJournalEntrySequenceService` in DI container | BE | P0 | 0.5h | [ ] |
| 141 | Unit test: First SALE entry → SALE/2026/0001 (TS-SQ-001) | QA | P0 | 0.5h | [ ] |
| 142 | Unit test: Second SALE entry → SALE/2026/0002 (TS-SQ-002) | QA | P0 | 0.5h | [ ] |
| 143 | Unit test: Independent sequences per type (TS-SQ-003) | QA | P0 | 0.5h | [ ] |
| 144 | Unit test: Concurrent sequence generation — no duplicates (TS-SQ-004) | QA | P0 | 1h | [ ] |
| 145 | Unit test: Deleted draft does not backfill sequence (TS-SQ-005) | QA | P1 | 0.5h | [ ] |

### 2.2 Journal Entry DTOs

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 146 | Create `CreateJournalEntryRequest` DTO | BE | P0 | 1h | [ ] |
| 147 | Create `CreateJournalEntryLineRequest` DTO | BE | P0 | 0.5h | [ ] |
| 148 | Create `UpdateJournalEntryRequest` DTO | BE | P0 | 0.5h | [ ] |
| 149 | Create `JournalEntryResult` DTO (Success, EntryId, EntryNumber, ErrorCode, Errors) | BE | P0 | 0.5h | [ ] |
| 150 | Create `JournalEntryDetailDto` DTO (full entry with lines) | BE | P0 | 1h | [ ] |
| 151 | Create `JournalEntryListDto` DTO (list item summary) | BE | P0 | 0.5h | [ ] |
| 152 | Create `JournalEntryLineDto` DTO (line detail for display) | BE | P0 | 0.5h | [ ] |
| 153 | Create `JournalEntryFilter` DTO (paginated filter) | BE | P0 | 1h | [ ] |
| 154 | Create `PagedResult<T>` generic if not already present | BE | P2 | 0.5h | [ ] |
| 155 | Add FluentValidation validators for `CreateJournalEntryRequest` | BE | P1 | 1h | [ ] |
| 156 | Add FluentValidation validators for `CreateJournalEntryLineRequest` | BE | P1 | 0.5h | [ ] |

### 2.3 IJournalEntryService — CreateAsync (C6)

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 157 | Create `IJournalEntryService` interface in SMS.Finance.Interfaces | BE | P0 | 1h | [ ] |
| 158 | Define `CreateAsync(CreateJournalEntryRequest)` method signature | BE | P0 | 0.5h | [ ] |
| 159 | Create `JournalEntryService` implementation in SMS.Finance.Services | BE | P0 | 1h | [ ] |
| 160 | Implement Step 1: Idempotency check — return existing entry if key matches | BE | P0 | 1h | [ ] |
| 161 | Implement Step 2: Journal type validation — exists, active, same org | BE | P0 | 1h | [ ] |
| 162 | Implement Step 2b: requires_partner enforcement per line | BE | P0 | 0.5h | [ ] |
| 163 | Implement Step 3: Fiscal period resolution from entry_date | BE | P0 | 1h | [ ] |
| 164 | Implement Step 3b: Period status check (Open or AdjustmentOnly for opening/closing) | BE | P0 | 1h | [ ] |
| 165 | Implement Step 4: Double-entry validation (sum debits = sum credits within 0.01) | BE | P0 | 1h | [ ] |
| 166 | Implement Step 4b: Minimum 2 lines validation | BE | P0 | 0.5h | [ ] |
| 167 | Implement Step 4c: Each line debit XOR credit validation | BE | P0 | 0.5h | [ ] |
| 168 | Implement Step 5: Account validation — Level 4, active, same org | BE | P0 | 1h | [ ] |
| 169 | Implement Step 6: Currency validation — exists, exchange_rate when foreign | BE | P0 | 1h | [ ] |
| 170 | Implement Step 7: Local amount computation (local_debit, local_credit, amount_residual) | BE | P0 | 1h | [ ] |
| 171 | Call `IJournalEntrySequenceService.GenerateEntryNumberAsync()` for entry_number | BE | P0 | 0.5h | [ ] |
| 172 | Map request to `JournalEntry` entity with auto-computed fields | BE | P0 | 1h | [ ] |
| 173 | Map request lines to `JournalEntryLine` entities with line_number assignment | BE | P0 | 1h | [ ] |
| 174 | Compute and set total_debit, total_credit, line_count on header | BE | P0 | 0.5h | [ ] |
| 175 | Add entity to FinanceDbContext (do not SaveChanges — gateway manages transaction) | BE | P0 | 0.5h | [ ] |
| 176 | Register `IJournalEntryService` in DI container | BE | P0 | 0.5h | [ ] |
| 177 | Unit test: Balanced 2-line entry → success (TS-JE-001) | QA | P0 | 0.5h | [ ] |
| 178 | Unit test: Unbalanced entry → rejected (TS-JE-002) | QA | P0 | 0.5h | [ ] |
| 179 | Unit test: Both debit and credit on same line → rejected (TS-JE-003) | QA | P0 | 0.5h | [ ] |
| 180 | Unit test: Entry in Closed period → rejected (TS-JE-004) | QA | P0 | 0.5h | [ ] |
| 181 | Unit test: Entry in Locked period → rejected (TS-JE-005) | QA | P0 | 0.5h | [ ] |
| 182 | Unit test: Regular entry in AdjustmentOnly period → rejected (TS-JE-006) | QA | P0 | 0.5h | [ ] |
| 183 | Unit test: Closing entry in AdjustmentOnly period → success (TS-JE-007) | QA | P0 | 0.5h | [ ] |
| 184 | Unit test: Duplicate idempotency key → returns existing (TS-JE-008) | QA | P0 | 0.5h | [ ] |
| 185 | Unit test: Level 3 account → rejected (TS-JE-009) | QA | P0 | 0.5h | [ ] |
| 186 | Unit test: Inactive account → rejected (TS-JE-010) | QA | P0 | 0.5h | [ ] |
| 187 | Unit test: SALE type without partner → rejected (TS-JE-011) | QA | P0 | 0.5h | [ ] |
| 188 | Unit test: Single line entry → rejected (TS-JE-012) | QA | P0 | 0.5h | [ ] |
| 189 | Unit test: Date outside any fiscal period → rejected (TS-JE-013) | QA | P0 | 0.5h | [ ] |
| 190 | Unit test: Foreign currency without rate → rejected (TS-JE-014) | QA | P0 | 0.5h | [ ] |
| 191 | Unit test: Foreign currency with rate → local amounts computed (TS-JE-015) | QA | P0 | 0.5h | [ ] |

### 2.4 IJournalEntryService — PostAsync (C6)

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 192 | Define `PostAsync(int orgId, int journalEntryId, string postedBy)` method | BE | P0 | 0.5h | [ ] |
| 193 | Implement status check: must be Draft (status = 0) | BE | P0 | 0.5h | [ ] |
| 194 | Implement re-validation of period status (still Open/AdjustmentOnly) | BE | P0 | 0.5h | [ ] |
| 195 | Implement status transition: set status = 1, posted_at, posted_by | BE | P0 | 0.5h | [ ] |
| 196 | Implement ChartOfAccounts.current_balance update per line | BE | P0 | 2h | [ ] |
| 197 | Implement ChartOfAccounts.has_transactions = true per line | BE | P0 | 0.5h | [ ] |
| 198 | Raise `JournalEntryPostedEvent` domain event | BE | P1 | 1h | [ ] |
| 199 | Unit test: Post a Draft entry → Posted (TS-JE-016) | QA | P0 | 0.5h | [ ] |
| 200 | Unit test: Post already-Posted → rejected (TS-JE-017) | QA | P0 | 0.5h | [ ] |
| 201 | Unit test: Post Reversed entry → rejected (TS-JE-018) | QA | P0 | 0.5h | [ ] |
| 202 | Unit test: Post when period Locked → rejected (TS-JE-019) | QA | P0 | 0.5h | [ ] |
| 203 | Unit test: Verify CoA balance updated after posting (TS-JE-020) | QA | P0 | 1h | [ ] |
| 204 | Unit test: Verify has_transactions flag set (TS-JE-021) | QA | P0 | 0.5h | [ ] |

### 2.5 IJournalEntryService — CreateAndPostAsync (C6)

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 205 | Define `CreateAndPostAsync(CreateJournalEntryRequest)` method | BE | P0 | 0.5h | [ ] |
| 206 | Implement as CreateAsync + PostAsync in single transaction | BE | P0 | 1h | [ ] |
| 207 | Unit test: CreateAndPost → entry in Posted status directly | QA | P0 | 0.5h | [ ] |

### 2.6 IJournalEntryService — UpdateDraft / DeleteDraft (C6)

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 208 | Define `UpdateDraftAsync(int orgId, int id, UpdateJournalEntryRequest)` method | BE | P0 | 0.5h | [ ] |
| 209 | Implement update with status = Draft check (reject if Posted/Reversed) | BE | P0 | 1h | [ ] |
| 210 | Implement re-validation of all business rules on update | BE | P0 | 1h | [ ] |
| 211 | Implement recalculation of total_debit, total_credit, line_count | BE | P0 | 0.5h | [ ] |
| 212 | Define `DeleteDraftAsync(int orgId, int id)` method | BE | P0 | 0.5h | [ ] |
| 213 | Implement delete with status = Draft check | BE | P0 | 0.5h | [ ] |
| 214 | Implement cascade delete of lines on draft delete | BE | P0 | 0.5h | [ ] |
| 215 | Unit test: Update a Draft entry → success | QA | P0 | 0.5h | [ ] |
| 216 | Unit test: Update a Posted entry → rejected (BR-JE-008) | QA | P0 | 0.5h | [ ] |
| 217 | Unit test: Delete a Draft entry → success | QA | P0 | 0.5h | [ ] |
| 218 | Unit test: Delete a Posted entry → rejected (BR-JE-008) | QA | P0 | 0.5h | [ ] |

### 2.7 IJournalEntryService — Query Methods (C6)

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 219 | Define `GetByIdAsync(int orgId, int id)` method | BE | P0 | 0.5h | [ ] |
| 220 | Implement GetByIdAsync with eager loading of Lines, JournalType, Account names | BE | P0 | 1h | [ ] |
| 221 | Define `ListAsync(int orgId, JournalEntryFilter filter)` method | BE | P0 | 0.5h | [ ] |
| 222 | Implement ListAsync with all filter options (type, period, status, date range, account, partner, module, search) | BE | P0 | 2h | [ ] |
| 223 | Implement pagination (Page, PageSize) and sorting (SortBy, SortDirection) | BE | P0 | 1h | [ ] |
| 224 | Map entities to DTOs (JournalEntryDetailDto, JournalEntryListDto) | BE | P0 | 1h | [ ] |
| 225 | Unit test: GetByIdAsync returns entry with all lines | QA | P0 | 0.5h | [ ] |
| 226 | Unit test: ListAsync with status filter | QA | P0 | 0.5h | [ ] |
| 227 | Unit test: ListAsync with date range filter | QA | P0 | 0.5h | [ ] |
| 228 | Unit test: ListAsync pagination | QA | P0 | 0.5h | [ ] |

### 2.8 Journal Entry API Endpoints (C6)

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 229 | Create `JournalEntriesController` in SMS.Finance.Controllers | BE | P0 | 1h | [ ] |
| 230 | Implement GET /api/v1/finance/journal-entries (list with filters) | BE | P0 | 1h | [ ] |
| 231 | Implement GET /api/v1/finance/journal-entries/{id} (detail with lines) | BE | P0 | 0.5h | [ ] |
| 232 | Implement POST /api/v1/finance/journal-entries (create draft) | BE | P0 | 1h | [ ] |
| 233 | Implement PUT /api/v1/finance/journal-entries/{id} (update draft) | BE | P0 | 1h | [ ] |
| 234 | Implement DELETE /api/v1/finance/journal-entries/{id} (delete draft) | BE | P0 | 0.5h | [ ] |
| 235 | Implement POST /api/v1/finance/journal-entries/{id}/post (post entry) | BE | P0 | 0.5h | [ ] |
| 236 | Add Finance.View authorization on GET endpoints | BE | P0 | 0.5h | [ ] |
| 237 | Add Finance.JournalEntry.Create authorization on POST create | BE | P0 | 0.5h | [ ] |
| 238 | Add Finance.JournalEntry.Edit authorization on PUT | BE | P0 | 0.5h | [ ] |
| 239 | Add Finance.JournalEntry.Delete authorization on DELETE | BE | P0 | 0.5h | [ ] |
| 240 | Add Finance.JournalEntry.Post authorization on POST post | BE | P0 | 0.5h | [ ] |
| 241 | Add API error response mapping for validation errors | BE | P1 | 1h | [ ] |

### 2.9 Journal Types API Endpoints (C1)

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 242 | Create `JournalTypesController` in SMS.Finance.Controllers | BE | P0 | 1h | [ ] |
| 243 | Implement GET /api/v1/finance/journal-types (list) | BE | P0 | 0.5h | [ ] |
| 244 | Implement GET /api/v1/finance/journal-types/{id} (detail) | BE | P0 | 0.5h | [ ] |
| 245 | Implement POST /api/v1/finance/journal-types (create custom) | BE | P0 | 1h | [ ] |
| 246 | Implement PUT /api/v1/finance/journal-types/{id} (update) | BE | P0 | 1h | [ ] |
| 247 | Implement PATCH /api/v1/finance/journal-types/{id}/deactivate | BE | P0 | 0.5h | [ ] |
| 248 | Add Finance.Admin authorization on mutation endpoints | BE | P0 | 0.5h | [ ] |
| 249 | Validate: cannot delete system types (is_system = true), only deactivate | BE | P0 | 0.5h | [ ] |
| 250 | Validate: only one Opening type and one Closing type per org | BE | P0 | 0.5h | [ ] |

### 2.10 Journal Entry UI (FE)

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 251 | Create Journal Entry List page at /finance/journal-entries | FE | P0 | 2h | [ ] |
| 252 | Implement filter bar: Type dropdown, Period dropdown, Status dropdown, Date range, Search | FE | P0 | 2h | [ ] |
| 253 | Implement paginated data table with columns: Entry#, Type, Date, Amount, Status | FE | P0 | 2h | [ ] |
| 254 | Implement status badges: Draft (blue), Posted (green), Reversed (red) | FE | P1 | 1h | [ ] |
| 255 | Implement "+ Manual Entry" button opening create form | FE | P0 | 1h | [ ] |
| 256 | Create Journal Entry Create/Edit form (header + line items) | FE | P0 | 4h | [ ] |
| 257 | Implement dynamic line items: add/remove rows, account picker, debit/credit | FE | P0 | 3h | [ ] |
| 258 | Implement running totals (debit total, credit total, difference) on form | FE | P0 | 1h | [ ] |
| 259 | Implement balance warning when debit ≠ credit | FE | P1 | 0.5h | [ ] |
| 260 | Create Journal Entry Detail/View panel (read-only for Posted/Reversed) | FE | P0 | 2h | [ ] |
| 261 | Implement Post button on Draft entry detail | FE | P0 | 0.5h | [ ] |
| 262 | Implement Reverse button on Posted entry detail with reason dialog | FE | P0 | 1h | [ ] |
| 263 | Add source document link navigation (soft link to source module) | FE | P2 | 1h | [ ] |

### 2.11 Journal Types UI (FE)

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 264 | Create Journal Types management page at /finance/settings/journal-types | FE | P0 | 2h | [ ] |
| 265 | Implement data table: Code, Name, Prefix, Status, Actions | FE | P0 | 1h | [ ] |
| 266 | Implement "+ New" button for creating custom journal types | FE | P0 | 1h | [ ] |
| 267 | Implement Edit form: code, name, prefix, default accounts, requires_partner, auto_post | FE | P0 | 2h | [ ] |
| 268 | Implement system type indicator (lock icon, edit-only, no delete) | FE | P1 | 0.5h | [ ] |
| 269 | Implement deactivate confirmation dialog | FE | P1 | 0.5h | [ ] |

---

## Phase 3 — Reversal & Reconciliation

### 3.1 Journal Entry Reversal Service (C7)

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 270 | Define `ReverseAsync(int orgId, int id, string reason, DateTime? reversalDate, string reversedBy)` in IJournalEntryService | BE | P0 | 0.5h | [ ] |
| 271 | Implement original entry validation: must be Posted, not already reversed | BE | P0 | 1h | [ ] |
| 272 | Implement reversal date resolution and period validation | BE | P0 | 1h | [ ] |
| 273 | Implement mirror entry creation: swap debits ↔ credits on all lines | BE | P0 | 2h | [ ] |
| 274 | Implement auto-post of reversal entry (no Draft state) | BE | P0 | 1h | [ ] |
| 275 | Implement original entry status update: status = 2, reversed_by_entry_id set | BE | P0 | 0.5h | [ ] |
| 276 | Implement reversal_of_entry_id and idempotency_key = "reversal:{id}" | BE | P0 | 0.5h | [ ] |
| 277 | Implement CoA balance rollback (net zero via swapped amounts) | BE | P0 | 1h | [ ] |
| 278 | Implement auto-reconciliation of original and reversal lines on same account | BE | P0 | 2h | [ ] |
| 279 | Implement POST /api/v1/finance/journal-entries/{id}/reverse endpoint | BE | P0 | 0.5h | [ ] |
| 280 | Add Finance.JournalEntry.Reverse authorization | BE | P0 | 0.5h | [ ] |
| 281 | Unit test: Reverse Posted entry → success (TS-RV-001) | QA | P0 | 1h | [ ] |
| 282 | Unit test: Reverse already-Reversed → rejected (TS-RV-002) | QA | P0 | 0.5h | [ ] |
| 283 | Unit test: Reverse Draft → rejected (TS-RV-003) | QA | P0 | 0.5h | [ ] |
| 284 | Unit test: Reverse into Closed period → rejected (TS-RV-004) | QA | P0 | 0.5h | [ ] |
| 285 | Unit test: Verify auto-reconciliation of reversal lines (TS-RV-005) | QA | P0 | 1h | [ ] |
| 286 | Unit test: Reversal reason stored correctly (TS-RV-006) | QA | P0 | 0.5h | [ ] |

### 3.2 Reconciliation Service (C8)

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 287 | Create `IReconciliationService` interface in SMS.Finance.Interfaces | BE | P0 | 0.5h | [ ] |
| 288 | Create `ReconciliationService` implementation | BE | P0 | 1h | [ ] |
| 289 | Create `ReconcileRequest` DTO (lineIds list) | BE | P0 | 0.5h | [ ] |
| 290 | Create `ReconcileResult` DTO (groupId, type, amount) | BE | P0 | 0.5h | [ ] |
| 291 | Create `OpenItemDto` DTO (line details for matching UI) | BE | P0 | 0.5h | [ ] |
| 292 | Create `ReconcileSuggestion` DTO | BE | P0 | 0.5h | [ ] |
| 293 | Implement `ReconcileAsync`: validate all lines same account, Posted, same org | BE | P0 | 1h | [ ] |
| 294 | Implement Full reconciliation logic (sum within 0.01 → reconciled = true) | BE | P0 | 2h | [ ] |
| 295 | Implement Partial reconciliation logic (reduce amount_residual proportionally) | BE | P0 | 2h | [ ] |
| 296 | Implement ReconcileGroup creation with auto-generated group_number | BE | P0 | 1h | [ ] |
| 297 | Implement setting clearing_document_id and clearing_date on fully cleared lines | BE | P0 | 1h | [ ] |
| 298 | Implement `UnreconcileAsync`: restore amount_residual, clear reconciliation fields, delete group | BE | P0 | 2h | [ ] |
| 299 | Implement `GetOpenItemsAsync`: unreconciled lines by account and optional partner | BE | P0 | 1h | [ ] |
| 300 | Implement `SuggestMatchesAsync`: find opposite-side lines that minimize residual | BE | P0 | 2h | [ ] |
| 301 | Register `IReconciliationService` in DI container | BE | P0 | 0.5h | [ ] |
| 302 | Unit test: Full reconciliation 1000 + (-1000) (TS-RC-001) | QA | P0 | 0.5h | [ ] |
| 303 | Unit test: Partial reconciliation 1000 + (-600) (TS-RC-002) | QA | P0 | 1h | [ ] |
| 304 | Unit test: Multi-invoice reconciliation (TS-RC-003) | QA | P0 | 1h | [ ] |
| 305 | Unit test: Unreconcile restores residual (TS-RC-004) | QA | P0 | 1h | [ ] |
| 306 | Unit test: Different accounts → rejected (TS-RC-005) | QA | P0 | 0.5h | [ ] |
| 307 | Unit test: Draft entry lines → rejected (TS-RC-006) | QA | P0 | 0.5h | [ ] |
| 308 | Unit test: Suggest matches (TS-RC-007) | QA | P1 | 1h | [ ] |

### 3.3 Reconciliation API Endpoints (C8)

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 309 | Create `ReconciliationController` in SMS.Finance.Controllers | BE | P0 | 1h | [ ] |
| 310 | Implement GET /api/v1/finance/reconciliation/open-items | BE | P0 | 0.5h | [ ] |
| 311 | Implement POST /api/v1/finance/reconciliation/suggest | BE | P0 | 0.5h | [ ] |
| 312 | Implement POST /api/v1/finance/reconciliation/reconcile | BE | P0 | 0.5h | [ ] |
| 313 | Implement POST /api/v1/finance/reconciliation/{groupId}/unreconcile | BE | P0 | 0.5h | [ ] |
| 314 | Add Finance.Reconcile.View and Finance.Reconcile.Execute authorization | BE | P0 | 0.5h | [ ] |

### 3.4 Reconciliation UI (FE)

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 315 | Create Reconciliation page at /finance/reconciliation | FE | P0 | 2h | [ ] |
| 316 | Implement account selector and optional partner filter | FE | P0 | 1h | [ ] |
| 317 | Implement open items list with checkboxes for selection | FE | P0 | 2h | [ ] |
| 318 | Implement running reconciliation total (selected debit vs credit vs residual) | FE | P0 | 1h | [ ] |
| 319 | Implement "Suggest Matches" button and suggestion panel | FE | P1 | 2h | [ ] |
| 320 | Implement "Reconcile" button with confirmation | FE | P0 | 1h | [ ] |
| 321 | Implement "Unreconcile" action on reconcile group | FE | P0 | 1h | [ ] |

---

## Phase 4 — Reports & Period Close

### 4.1 Trial Balance Service (C9)

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 322 | Create `ITrialBalanceService` interface in SMS.Finance.Interfaces | BE | P0 | 0.5h | [ ] |
| 323 | Create `TrialBalanceService` implementation | BE | P0 | 1h | [ ] |
| 324 | Create `TrialBalanceRequest` DTO (orgId, fromDate, toDate, filters) | BE | P0 | 0.5h | [ ] |
| 325 | Create `TrialBalanceReport` DTO (lines, totals, IsBalanced) | BE | P0 | 0.5h | [ ] |
| 326 | Create `TrialBalanceLineDto` DTO (opening/period/closing debit/credit) | BE | P0 | 0.5h | [ ] |
| 327 | Create `AccountBalanceDto` DTO | BE | P0 | 0.5h | [ ] |
| 328 | Create `AccountLedgerLineDto` DTO | BE | P0 | 0.5h | [ ] |
| 329 | Implement `GenerateAsync`: full trial balance SQL query with opening/period/closing columns | BE | P0 | 3h | [ ] |
| 330 | Implement opening balance computation (all posted lines before fromDate) | BE | P0 | 1h | [ ] |
| 331 | Implement IncludeClosingEntries filter | BE | P0 | 0.5h | [ ] |
| 332 | Implement optional cost_center_id and project_id filters | BE | P1 | 1h | [ ] |
| 333 | Implement `GetAccountBalanceAsync`: single account balance as of date | BE | P0 | 1h | [ ] |
| 334 | Implement `GetAccountLedgerAsync`: paginated line-by-line ledger with running balance | BE | P0 | 2h | [ ] |
| 335 | Register `ITrialBalanceService` in DI container | BE | P0 | 0.5h | [ ] |
| 336 | Unit test: Trial balance debit = credit (TS-TB-001) | QA | P0 | 1h | [ ] |
| 337 | Unit test: Opening balances included (TS-TB-002) | QA | P0 | 0.5h | [ ] |
| 338 | Unit test: Closing entries excluded by default (TS-TB-003) | QA | P0 | 0.5h | [ ] |
| 339 | Unit test: Single account balance (TS-TB-004) | QA | P0 | 0.5h | [ ] |
| 340 | Unit test: Account ledger with running balance (TS-TB-005) | QA | P0 | 0.5h | [ ] |
| 341 | Unit test: Draft entries excluded (TS-TB-006) | QA | P0 | 0.5h | [ ] |

### 4.2 Trial Balance API Endpoints (C9)

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 342 | Create `TrialBalanceController` (or extend existing FinanceReportsController) | BE | P0 | 0.5h | [ ] |
| 343 | Implement GET /api/v1/finance/trial-balance | BE | P0 | 0.5h | [ ] |
| 344 | Implement GET /api/v1/finance/accounts/{id}/balance | BE | P0 | 0.5h | [ ] |
| 345 | Implement GET /api/v1/finance/accounts/{id}/ledger | BE | P0 | 0.5h | [ ] |
| 346 | Add Finance.Reports.View authorization | BE | P0 | 0.5h | [ ] |

### 4.3 Trial Balance UI (FE)

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 347 | Create Trial Balance page at /finance/reports/trial-balance | FE | P0 | 2h | [ ] |
| 348 | Implement period selector (fiscal year + period or date range) | FE | P0 | 1h | [ ] |
| 349 | Implement Include Opening / Include Closing checkboxes | FE | P0 | 0.5h | [ ] |
| 350 | Implement Generate button and loading state | FE | P0 | 0.5h | [ ] |
| 351 | Implement trial balance data table: Code, Account, Debit, Credit, Balance | FE | P0 | 2h | [ ] |
| 352 | Implement total row with balanced/unbalanced indicator | FE | P0 | 1h | [ ] |
| 353 | Implement drill-down: click account → account ledger | FE | P1 | 2h | [ ] |
| 354 | Implement Export CSV button | FE | P1 | 1h | [ ] |
| 355 | Implement negative amounts in parentheses formatting | FE | P2 | 0.5h | [ ] |

### 4.4 Period Close Service (C10)

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 356 | Create `IPeriodCloseService` interface in SMS.Finance.Interfaces | BE | P0 | 0.5h | [ ] |
| 357 | Create `PeriodCloseService` implementation | BE | P0 | 1h | [ ] |
| 358 | Create `CloseResult`, `YearEndCloseResult`, `YearEndPreview`, `PeriodCloseStatusDto` DTOs | BE | P0 | 1h | [ ] |
| 359 | Implement `ClosePeriodAsync`: Open → AdjustmentOnly or Locked | BE | P0 | 1h | [ ] |
| 360 | Implement draft entry check: reject lock if unposted drafts exist in period | BE | P0 | 1h | [ ] |
| 361 | Implement `ReopenPeriodAsync`: Locked → Open (reject if Closed) | BE | P0 | 1h | [ ] |
| 362 | Implement `FinalClosePeriodAsync`: Locked → Closed (irreversible) | BE | P0 | 0.5h | [ ] |
| 363 | Implement `GetPeriodStatusesAsync`: all periods in a year with entry counts | BE | P0 | 1h | [ ] |
| 364 | Implement `PreviewYearEndCloseAsync`: compute P&L balances without creating entry | BE | P0 | 2h | [ ] |
| 365 | Implement `CloseYearAsync` Step 1: Pre-checks (all periods Locked/Closed, no drafts) | BE | P0 | 1h | [ ] |
| 366 | Implement `CloseYearAsync` Step 2: Compute P&L balances (Revenue, Expense, Net Income) | BE | P0 | 2h | [ ] |
| 367 | Implement `CloseYearAsync` Step 3: Generate closing JE (Revenue Dr, Expense Cr, Retained Earnings) | BE | P0 | 3h | [ ] |
| 368 | Implement `CloseYearAsync` Step 4: Auto-post closing entry | BE | P0 | 0.5h | [ ] |
| 369 | Implement `CloseYearAsync` Step 5: Mark fiscal_year.is_closed = true | BE | P0 | 0.5h | [ ] |
| 370 | Implement `CloseYearAsync` Step 6: Final-close all periods → Closed | BE | P0 | 0.5h | [ ] |
| 371 | Implement idempotency: yearclose:{fiscal_year_id} prevents duplicate close | BE | P0 | 0.5h | [ ] |
| 372 | Raise `FiscalYearClosedEvent` domain event | BE | P1 | 0.5h | [ ] |
| 373 | Register `IPeriodCloseService` in DI container | BE | P0 | 0.5h | [ ] |
| 374 | Unit test: Lock period → success (TS-PC-001) | QA | P0 | 0.5h | [ ] |
| 375 | Unit test: Lock period with drafts → rejected (TS-PC-002) | QA | P0 | 0.5h | [ ] |
| 376 | Unit test: Reopen Locked → success (TS-PC-003) | QA | P0 | 0.5h | [ ] |
| 377 | Unit test: Reopen Closed → rejected (TS-PC-004) | QA | P0 | 0.5h | [ ] |
| 378 | Unit test: Final-close Locked → Closed (TS-PC-005) | QA | P0 | 0.5h | [ ] |
| 379 | Unit test: Year-end close full success (TS-PC-006) | QA | P0 | 2h | [ ] |
| 380 | Unit test: Year-end close with Open period → rejected (TS-PC-007) | QA | P0 | 0.5h | [ ] |
| 381 | Unit test: Duplicate year-end close → idempotent (TS-PC-008) | QA | P0 | 0.5h | [ ] |
| 382 | Unit test: Preview year-end close (TS-PC-009) | QA | P0 | 1h | [ ] |
| 383 | Unit test: Post to closed fiscal year → rejected (TS-PC-010) | QA | P0 | 0.5h | [ ] |
| 384 | Unit test: Year-end close with net loss (TS-PC-011) | QA | P0 | 0.5h | [ ] |

### 4.5 Period Close API Endpoints (C10)

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 385 | Create `PeriodCloseController` in SMS.Finance.Controllers | BE | P0 | 0.5h | [ ] |
| 386 | Implement GET /api/v1/finance/period-close/{fiscalYearId}/status | BE | P0 | 0.5h | [ ] |
| 387 | Implement POST /api/v1/finance/period-close/{periodId}/close | BE | P0 | 0.5h | [ ] |
| 388 | Implement POST /api/v1/finance/period-close/{periodId}/reopen | BE | P0 | 0.5h | [ ] |
| 389 | Implement POST /api/v1/finance/period-close/{periodId}/final-close | BE | P0 | 0.5h | [ ] |
| 390 | Implement GET /api/v1/finance/year-end-close/{fiscalYearId}/preview | BE | P0 | 0.5h | [ ] |
| 391 | Implement POST /api/v1/finance/year-end-close/{fiscalYearId}/execute | BE | P0 | 0.5h | [ ] |
| 392 | Add Finance.PeriodClose.View, Execute, Admin authorization | BE | P0 | 0.5h | [ ] |

### 4.6 Period Close UI (FE)

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 393 | Create Period Close page at /finance/period-close | FE | P0 | 2h | [ ] |
| 394 | Implement fiscal year selector dropdown | FE | P0 | 0.5h | [ ] |
| 395 | Implement period status table: Period, Entries, Drafts, Status, Action | FE | P0 | 2h | [ ] |
| 396 | Implement Lock/Reopen/Close action buttons per period | FE | P0 | 1h | [ ] |
| 397 | Implement draft entry warning banner | FE | P1 | 0.5h | [ ] |
| 398 | Implement Year-End Close section with Retained Earnings account selector | FE | P0 | 1h | [ ] |
| 399 | Implement "Preview Closing Entry" button and preview dialog | FE | P0 | 2h | [ ] |
| 400 | Implement "Execute Year-End Close" button with confirmation | FE | P0 | 1h | [ ] |
| 401 | Implement post-close summary dialog (net income, closing entry number) | FE | P1 | 1h | [ ] |

---

## Phase 5 — Gateway Integration & Testing

### 5.1 Gateway Integration Enhancement (C11)

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 402 | Create `IJournalTypeResolver` interface | BE | P0 | 0.5h | [ ] |
| 403 | Create `JournalTypeResolver` implementation: map DocumentType → JournalType code | BE | P0 | 1h | [ ] |
| 404 | Map SaleInvoice / SaleCreditNote → SALE | BE | P0 | 0.5h | [ ] |
| 405 | Map PurchaseInvoice / PurchaseCreditNote → PURCH | BE | P0 | 0.5h | [ ] |
| 406 | Map Payment / Receipt → CASH or BANK (by payment method) | BE | P0 | 1h | [ ] |
| 407 | Map InventoryAdjustment / ManualJournalEntry → MISC | BE | P0 | 0.5h | [ ] |
| 408 | Map OpeningBalance → OPEN, YearEndClosing → CLOSE | BE | P0 | 0.5h | [ ] |
| 409 | Enhance `JournalEntryBuilder` to delegate to `IJournalEntryService.CreateAndPostAsync()` | BE | P0 | 2h | [ ] |
| 410 | Build `CreateJournalEntryRequest` from `FinancialTransaction` canonical DTO | BE | P0 | 1h | [ ] |
| 411 | Handle `JournalEntryPostingException` — map GL Engine errors to gateway error format | BE | P0 | 1h | [ ] |
| 412 | Add `GetEntityByIdAsync` internal method for gateway (returns tracked entity, not DTO) | BE | P0 | 0.5h | [ ] |
| 413 | Register `IJournalTypeResolver` in DI container | BE | P0 | 0.5h | [ ] |
| 414 | Verify gateway contract `IFinancialPostingGateway.PostAsync()` is unchanged | QA | P0 | 0.5h | [ ] |

### 5.2 Integration Tests

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 415 | Integration test: Sale Invoice → Gateway → GL Engine → SALE JE (TS-GW-001) | QA | P0 | 2h | [ ] |
| 416 | Integration test: Purchase Invoice → Gateway → GL Engine → PURCH JE (TS-GW-002) | QA | P0 | 1h | [ ] |
| 417 | Integration test: Parallel posting (internal GL + QB outbox) (TS-GW-003) | QA | P0 | 2h | [ ] |
| 418 | Integration test: Internal GL disabled, QB only (TS-GW-004) | QA | P0 | 1h | [ ] |
| 419 | Integration test: Period Closed → GL fails, QB outbox still written (TS-GW-005) | QA | P0 | 2h | [ ] |
| 420 | Integration test: Duplicate idempotency via gateway (TS-GW-006) | QA | P0 | 1h | [ ] |

### 5.3 End-to-End Tests

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 421 | E2E: Full accounting cycle — create → post → trial balance → period close → year-end | QA | P0 | 4h | [ ] |
| 422 | E2E: Create manual JE → Post → Verify trial balance reflects new entry | QA | P0 | 2h | [ ] |
| 423 | E2E: Sale invoice posting → JE created → Reconcile with payment JE | QA | P0 | 2h | [ ] |
| 424 | E2E: Reverse a posted entry → Verify CoA balances restored → Verify auto-reconciliation | QA | P0 | 2h | [ ] |
| 425 | E2E: Lock all periods → Year-end close → Verify closing JE → Verify P&L zeroed | QA | P0 | 3h | [ ] |
| 426 | E2E: Multi-currency entry → Verify local amounts → Verify trial balance in reporting currency | QA | P1 | 2h | [ ] |

### 5.4 Performance Tests

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 427 | Performance: Trial balance with 10K+ journal entries | QA | P1 | 1h | [ ] |
| 428 | Performance: Account ledger with 5K+ lines per account | QA | P1 | 1h | [ ] |
| 429 | Performance: Concurrent sequence generation (10 parallel requests) | QA | P1 | 1h | [ ] |
| 430 | Performance: Journal entry list with complex filters on 50K+ entries | QA | P2 | 1h | [ ] |
| 431 | Performance: Year-end close with 100+ revenue/expense accounts | QA | P2 | 1h | [ ] |

### 5.5 Security Tests

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 432 | Security: Finance.View cannot create/post/reverse journal entries | QA | P0 | 0.5h | [ ] |
| 433 | Security: Finance.JournalEntry.Create cannot post or reverse | QA | P0 | 0.5h | [ ] |
| 434 | Security: Finance.JournalEntry.Post cannot create or reverse | QA | P0 | 0.5h | [ ] |
| 435 | Security: Finance.PeriodClose.Execute cannot final-close | QA | P0 | 0.5h | [ ] |
| 436 | Security: Finance.PeriodClose.Admin required for year-end close | QA | P0 | 0.5h | [ ] |
| 437 | Security: Finance.Admin required for journal type CRUD | QA | P0 | 0.5h | [ ] |
| 438 | Security: Tenant isolation — org_id filter on all queries | QA | P0 | 1h | [ ] |
| 439 | Security: Cannot access journal entries from another org_id | QA | P0 | 0.5h | [ ] |

### 5.6 Business Rule Validation Tests

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 440 | BR-JE-001: Double-entry balance enforcement | QA | P0 | 0.5h | [ ] |
| 441 | BR-JE-002: Minimum 2 lines | QA | P0 | 0.5h | [ ] |
| 442 | BR-JE-003: Debit XOR credit per line | QA | P0 | 0.5h | [ ] |
| 443 | BR-JE-004: Period must be Open or AdjustmentOnly | QA | P0 | 0.5h | [ ] |
| 444 | BR-JE-005: AdjustmentOnly accepts only opening/closing entries | QA | P0 | 0.5h | [ ] |
| 445 | BR-JE-006: Account must be Level 4 and active | QA | P0 | 0.5h | [ ] |
| 446 | BR-JE-007: Idempotency key uniqueness | QA | P0 | 0.5h | [ ] |
| 447 | BR-JE-008: Posted entries immutable | QA | P0 | 0.5h | [ ] |
| 448 | BR-JE-009: Only Draft can be posted | QA | P0 | 0.5h | [ ] |
| 449 | BR-JE-010: Entry number immutable | QA | P0 | 0.5h | [ ] |
| 450 | BR-JE-011: requires_partner enforcement | QA | P0 | 0.5h | [ ] |
| 451 | BR-JE-012: Exchange rate required for foreign currency | QA | P0 | 0.5h | [ ] |
| 452 | BR-JE-013: Local amount computation correctness | QA | P0 | 0.5h | [ ] |
| 453 | BR-RV-001 through BR-RV-008: All reversal rules | QA | P0 | 2h | [ ] |
| 454 | BR-RC-001 through BR-RC-008: All reconciliation rules | QA | P0 | 2h | [ ] |
| 455 | BR-PC-001 through BR-PC-009: All period close rules | QA | P0 | 2h | [ ] |
| 456 | BR-TB-001 through BR-TB-005: All trial balance rules | QA | P0 | 1h | [ ] |

---

## Phase 6 — Documentation & Cleanup

| # | Task | Type | Priority | Effort | Status |
|---|---|---|---|---|---|
| 457 | Document all new API endpoints in Swagger/OpenAPI annotations | DOC | P1 | 2h | [ ] |
| 458 | Document IJournalEntryService interface with XML doc comments | DOC | P1 | 1h | [ ] |
| 459 | Document IReconciliationService interface with XML doc comments | DOC | P1 | 0.5h | [ ] |
| 460 | Document ITrialBalanceService interface with XML doc comments | DOC | P1 | 0.5h | [ ] |
| 461 | Document IPeriodCloseService interface with XML doc comments | DOC | P1 | 0.5h | [ ] |
| 462 | Add error code catalog for GL Engine (JE-001 through JE-015) | DOC | P1 | 1h | [ ] |
| 463 | Update A44 addendum reference to note A47 enhancements to JournalEntryBuilder | DOC | P2 | 0.5h | [ ] |
| 464 | Verify all new permissions are registered in permission seed data | BE | P0 | 1h | [ ] |
| 465 | Code review: Verify no cross-schema references in any service/entity | QA | P0 | 1h | [ ] |
| 466 | Code review: Verify all services use FinanceDbContext only | QA | P0 | 0.5h | [ ] |
| 467 | Code review: Verify org_id filtering on all queries | QA | P0 | 0.5h | [ ] |
| 468 | Smoke test: Full GL workflow on staging environment | QA | P0 | 2h | [ ] |

---

## Summary

| Phase | Tasks | Effort (days) |
|---|---|---|
| Phase 1 — Foundation (Entities, Migrations, Journal Types) | #001 – #133 | 7.5 |
| Phase 2 — Core Engine (Create, Validate, Post, List) | #134 – #269 | 10 |
| Phase 3 — Reversal & Reconciliation | #270 – #321 | 10 |
| Phase 4 — Reports & Period Close | #322 – #401 | 12 |
| Phase 5 — Gateway Integration & Testing | #402 – #456 | 10.5 |
| Phase 6 — Documentation & Cleanup | #457 – #468 | — |
| **Total** | **468 tasks** | **50 days (10 weeks)** |

---

## Task Counts by Type

| Type | Count |
|---|---|
| BE (Backend) | 218 |
| FE (Frontend) | 62 |
| DB (Database) | 73 |
| QA (Testing) | 103 |
| DOC (Documentation) | 12 |
| **Total** | **468** |

---

## Cross-References

| Entity / Service | Addendum | Status |
|---|---|---|
| finance.ChartOfAccounts | A42 | Exists — modified (current_balance update, has_transactions) |
| finance.FiscalYears | A42 | Exists — referenced (is_closed set on year-end close) |
| finance.FiscalPeriods | A42 | Exists — modified (AdjustmentOnly status added) |
| finance.AccountingDefaults | A42 | Exists — referenced (DEFAULT_RETAINED_EARNINGS) |
| finance.TenantFinanceConfigs | A44 | Exists — referenced (internal_accounting_enabled) |
| finance.FinancialPostingOutbox | A44 | Exists — unchanged |
| IFinancialPostingGateway | A44 | Exists — contract unchanged |
| IJournalEntryBuilder | A44 | Exists — implementation enhanced (C11) |
| FinanceDbContext | A44 | Exists — 5 new DbSets added |
| finance.JournalTypes | **A47 NEW** | Created in M1 |
| finance.JournalEntries | **A47 NEW** | Created in M3 |
| finance.JournalEntryLines | **A47 NEW** | Created in M4 |
| finance.JournalEntrySequences | **A47 NEW** | Created in M5 |
| finance.ReconcileGroups | **A47 NEW** | Created in M6 |

---

**End of Task Register — Addendum 47**
