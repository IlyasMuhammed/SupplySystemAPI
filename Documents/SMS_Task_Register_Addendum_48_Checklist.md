# SMS — Task Register & Checklist

## Addendum 48: Accounts Receivable & Accounts Payable Sub-Ledgers

| Property | Value |
|---|---|
| Addendum | 48 |
| Total Tasks | 576 |
| Phases | 6 |
| Estimated Effort | 65 days (13 weeks) |
| Depends On | A42 (Chart of Accounts), A44 (Financial Posting Gateway), A47 (GL Journal Engine) |

### Task Categories

| Code | Category | Count |
|---|---|---|
| BE | Backend (Entities, Services, Logic) | 248 |
| DB | Database (Migrations, Indexes, Seeds) | 86 |
| FE | Frontend (UI Components, Pages) | 78 |
| QA | Quality Assurance (Unit, Integration, E2E) | 148 |
| DOC | Documentation | 16 |

---

## Phase 1 — Foundation & Partner Profile (8 days)

### 1A. PartnerFinancialProfiles Entity (2 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 001 | Create `PartnerFinancialProfile` EF Core entity class in `SMS.Finance.Entities` | BE | 2h | ☐ |
| 002 | Define all credit-related properties: `CreditLimit`, `CreditLimitCurrencyId`, `CreditHold`, `CreditHoldReason`, `CreditHoldDate`, `CreditHoldBy` | BE | 1h | ☐ |
| 003 | Define temporary credit properties: `TemporaryCreditLimit`, `TemporaryCreditExpiry`, `CreditRiskTier` | BE | 0.5h | ☐ |
| 004 | Define dunning properties: `DunningProcedureId`, `CurrentDunningLevel`, `LastDunningDate`, `BlockSalesAtDunning` | BE | 0.5h | ☐ |
| 005 | Define payment term defaults: `DefaultPaymentTermDays`, `DefaultPaymentMethod` | BE | 0.5h | ☐ |
| 006 | Define AP properties: `PaymentPriority`, `PreferredPaymentMethod`, `WithholdingTaxApplicable` | BE | 0.5h | ☐ |
| 007 | Define statistics fields: `LastPaymentDate`, `AverageDaysToPay`, `TotalOverdueAmount` | BE | 0.5h | ☐ |
| 008 | Define audit fields: `CreatedAt`, `CreatedBy`, `ModifiedAt`, `ModifiedBy` | BE | 0.25h | ☐ |
| 009 | Add `CreditRiskTier` enum: Low=1, Medium=2, High=3 | BE | 0.25h | ☐ |
| 010 | Add `PaymentPriority` enum: High=1, Normal=2, Low=3 | BE | 0.25h | ☐ |
| 011 | Configure entity in `FinanceDbContext.OnModelCreating()` | BE | 0.5h | ☐ |
| 012 | Configure UQ constraint `UQ_PFP_OrgPartner` on `(OrgId, PartnerId, PartnerType)` | DB | 0.25h | ☐ |
| 013 | Configure CHECK constraint `CK_PFP_PartnerType` — `IN ('Customer','Vendor')` | DB | 0.25h | ☐ |
| 014 | Configure CHECK constraint `CK_PFP_RiskTier` — `BETWEEN 1 AND 3` | DB | 0.25h | ☐ |
| 015 | Configure CHECK constraint `CK_PFP_Priority` — `BETWEEN 1 AND 3` | DB | 0.25h | ☐ |
| 016 | Create index `IX_PFP_OrgPartnerType` on `(OrgId, PartnerType)` INCLUDE `(CreditLimit, CreditHold)` | DB | 0.25h | ☐ |
| 017 | Create filtered index `IX_PFP_CreditHold` on `(OrgId)` WHERE `CreditHold = 1` | DB | 0.25h | ☐ |
| 018 | Create index `IX_PFP_DunningProcedure` on `(DunningProcedureId)` WHERE NOT NULL | DB | 0.25h | ☐ |
| 019 | Write EF Core migration `M1_CreatePartnerFinancialProfiles` | DB | 0.5h | ☐ |
| 020 | Run migration on dev database and verify table creation | DB | 0.25h | ☐ |
| 021 | Create `PartnerFinancialProfileDto` response DTO | BE | 0.5h | ☐ |
| 022 | Create `CreatePartnerFinancialProfileRequest` DTO | BE | 0.25h | ☐ |
| 023 | Create `UpdatePartnerFinancialProfileRequest` DTO | BE | 0.25h | ☐ |
| 024 | Implement `IPartnerFinancialProfileRepository` | BE | 1h | ☐ |
| 025 | Implement `GetOrCreateAsync(orgId, partnerId, partnerType)` — auto-create on first access | BE | 0.5h | ☐ |
| 026 | Unit test: create profile for Customer | QA | 0.25h | ☐ |
| 027 | Unit test: create profile for Vendor | QA | 0.25h | ☐ |
| 028 | Unit test: unique constraint on duplicate `(OrgId, PartnerId, PartnerType)` | QA | 0.25h | ☐ |
| 029 | Unit test: check constraint rejects invalid `PartnerType` | QA | 0.25h | ☐ |
| 030 | Unit test: check constraint rejects `CreditRiskTier = 0` and `CreditRiskTier = 4` | QA | 0.25h | ☐ |

### 1B. IPartnerLookupService (1.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 031 | Define `IPartnerLookupService` interface in `SMS.Finance.Services` | BE | 0.25h | ☐ |
| 032 | Define `PartnerInfo` record: `PartnerId`, `PartnerType`, `Name`, `Code`, `Email`, `Phone`, `Address` | BE | 0.25h | ☐ |
| 033 | Implement `GetPartnerAsync(orgId, partnerId, partnerType)` — resolve from Sales/Procurement context | BE | 1.5h | ☐ |
| 034 | Implement `GetPartnersAsync(orgId, partnerIds, partnerType)` — batch resolve for report performance | BE | 1.5h | ☐ |
| 035 | Add in-memory caching (per-request scope) for batch lookups | BE | 1h | ☐ |
| 036 | Handle missing partner gracefully — return null, not exception | BE | 0.5h | ☐ |
| 037 | Register `IPartnerLookupService` in DI container | BE | 0.25h | ☐ |
| 038 | Unit test: resolve Customer from SalesDbContext | QA | 0.5h | ☐ |
| 039 | Unit test: resolve Vendor from ProcurementDbContext | QA | 0.5h | ☐ |
| 040 | Unit test: batch resolve 50 partners — verify single query per type | QA | 0.5h | ☐ |
| 041 | Unit test: missing partner returns null | QA | 0.25h | ☐ |
| 042 | Unit test: invalid partnerType throws ArgumentException | QA | 0.25h | ☐ |

### 1C. MatchToleranceRules & EarlyPaymentTerms Entities (1.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 043 | Create `MatchToleranceRule` entity class | BE | 0.5h | ☐ |
| 044 | Define properties: `PriceTolerancePercent`, `PriceToleranceAmount`, `QuantityTolerancePercent`, `TotalTolerancePercent`, `TotalToleranceAmount` | BE | 0.5h | ☐ |
| 045 | Define `AutoApproveWithin`, `IsDefault`, `IsActive` | BE | 0.25h | ☐ |
| 046 | Configure entity in `FinanceDbContext` with UQ on `(OrgId, Name)` | DB | 0.25h | ☐ |
| 047 | Write EF Core migration `M2_CreateMatchToleranceRules` | DB | 0.25h | ☐ |
| 048 | Create `EarlyPaymentTerm` entity class | BE | 0.5h | ☐ |
| 049 | Define properties: `Code`, `Name`, `DiscountPercent`, `DiscountDays`, `NetDays`, `DiscountAccountId` | BE | 0.5h | ☐ |
| 050 | Configure CHECK constraint `CK_EPT_DiscountDays` — `DiscountDays < NetDays` | DB | 0.25h | ☐ |
| 051 | Configure UQ on `(OrgId, Code)` | DB | 0.25h | ☐ |
| 052 | Write EF Core migration `M3_CreateEarlyPaymentTerms` | DB | 0.25h | ☐ |
| 053 | Run migrations M2, M3 on dev database | DB | 0.25h | ☐ |
| 054 | Create `MatchToleranceRuleDto` and `EarlyPaymentTermDto` | BE | 0.5h | ☐ |
| 055 | Unit test: create tolerance rule with valid data | QA | 0.25h | ☐ |
| 056 | Unit test: UQ rejects duplicate tolerance name per org | QA | 0.25h | ☐ |
| 057 | Unit test: create early payment term "2/10 Net 30" | QA | 0.25h | ☐ |
| 058 | Unit test: CHECK rejects `DiscountDays >= NetDays` | QA | 0.25h | ☐ |

### 1D. CreditHoldRequests Entity (1 day)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 059 | Create `CreditHoldRequest` entity class | BE | 0.5h | ☐ |
| 060 | Define properties: `Action` (Hold/Release/TempIncrease), `Reason`, `PreviousStatus`, `NewStatus` | BE | 0.5h | ☐ |
| 061 | Define `Amount`, `ExpiryDate` for TempIncrease | BE | 0.25h | ☐ |
| 062 | Define `RequestedBy`, `RequestedAt`, `ApprovedBy`, `ApprovedAt`, `Status` | BE | 0.25h | ☐ |
| 063 | Configure CHECK constraint `CK_CHR_Action` — `IN ('Hold','Release','TempIncrease')` | DB | 0.25h | ☐ |
| 064 | Configure index `IX_CHR_OrgPartner` on `(OrgId, PartnerId)` | DB | 0.25h | ☐ |
| 065 | Write EF Core migration `M8_CreateCreditHoldRequests` | DB | 0.25h | ☐ |
| 066 | Create `CreditHoldRequestDto` DTO | BE | 0.25h | ☐ |
| 067 | Unit test: create Hold request | QA | 0.25h | ☐ |
| 068 | Unit test: create TempIncrease with amount and expiry | QA | 0.25h | ☐ |
| 069 | Unit test: CHECK rejects invalid action type | QA | 0.25h | ☐ |

### 1E. StatementLog Entity (1 day)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 070 | Create `StatementLog` entity class | BE | 0.5h | ☐ |
| 071 | Define properties: `StatementType` (Activity/OpenItems), `FromDate`, `ToDate`, `OpeningBalance`, `ClosingBalance`, `LineCount` | BE | 0.5h | ☐ |
| 072 | Define `PdfPath`, `EmailedAt`, `EmailedTo`, `GeneratedAt`, `GeneratedBy` | BE | 0.25h | ☐ |
| 073 | Configure index `IX_SL_OrgPartner` on `(OrgId, PartnerId, PartnerType)` | DB | 0.25h | ☐ |
| 074 | Write EF Core migration `M9_CreateStatementLog` | DB | 0.25h | ☐ |
| 075 | Run migrations M8, M9 on dev database | DB | 0.25h | ☐ |
| 076 | Unit test: create statement log entry | QA | 0.25h | ☐ |

### 1F. Phase 1 Migration Verification (1 day)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 077 | Verify all Phase 1 migrations execute in order: M1 → M2 → M3 → M8 → M9 | DB | 0.5h | ☐ |
| 078 | Verify rollback for each migration | DB | 0.5h | ☐ |
| 079 | Verify OrgId discriminator on all new entities (multi-tenant) | DB | 0.25h | ☐ |
| 080 | Verify all FK relationships within `finance` schema | DB | 0.25h | ☐ |
| 081 | Verify no cross-schema FK exists on any new entity | DB | 0.25h | ☐ |
| 082 | Run full schema diff against baseline | DB | 0.5h | ☐ |
| 083 | Document Phase 1 entity relationships in code comments | DOC | 0.5h | ☐ |

**Phase 1 Subtotal: 83 tasks (38 BE, 24 DB, 18 QA, 3 DOC)**

---

## Phase 2 — Credit Control & Aging (10 days)

### 2A. ICreditControlService — Credit Exposure Computation (2.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 084 | Define `ICreditControlService` interface in `SMS.Finance.Services` | BE | 0.5h | ☐ |
| 085 | Define `CreditExposureResult` record with all properties | BE | 0.5h | ☐ |
| 086 | Define `CreditCheckResult` record | BE | 0.25h | ☐ |
| 087 | Implement `GetCreditExposureAsync(orgId, customerId)` | BE | 2h | ☐ |
| 088 | Implement open-orders query — soft-link query to sales module for undelivered SO value | BE | 1.5h | ☐ |
| 089 | Implement open-invoices query — GL lines WHERE `partner_type = 'Customer'` AND `reconciled = 0` AND `account_type = 'Receivable'` | BE | 1h | ☐ |
| 090 | Implement effective limit logic — `MAX(credit_limit, temporary_credit_limit)` when not expired | BE | 0.5h | ☐ |
| 091 | Implement temporary limit auto-expiry check | BE | 0.5h | ☐ |
| 092 | Implement `CheckCreditAsync(orgId, customerId, transactionAmount)` | BE | 1.5h | ☐ |
| 093 | Check `credit_hold` flag — immediate block if true | BE | 0.25h | ☐ |
| 094 | Compute projected exposure: current + transactionAmount | BE | 0.25h | ☐ |
| 095 | Compare projected vs effective limit — return Approved/Blocked with reason | BE | 0.5h | ☐ |
| 096 | Handle NULL credit_limit (= unlimited) — always approve | BE | 0.25h | ☐ |
| 097 | Register `ICreditControlService` in DI | BE | 0.25h | ☐ |
| 098 | Unit test TS-CR-001: customer with no credit limit → SO confirmed | QA | 0.5h | ☐ |
| 099 | Unit test TS-CR-002: customer at 80% → SO confirmed | QA | 0.5h | ☐ |
| 100 | Unit test TS-CR-003: customer exceeds limit → SO blocked | QA | 0.5h | ☐ |
| 101 | Unit test TS-CR-004: customer on hold → any SO blocked | QA | 0.5h | ☐ |
| 102 | Unit test TS-CR-005: temporary limit > permanent, within temp → pass | QA | 0.5h | ☐ |
| 103 | Unit test TS-CR-006: temporary limit expired → falls back to permanent | QA | 0.5h | ☐ |
| 104 | Unit test TS-CR-009: exposure includes open SO + open receivables | QA | 0.5h | ☐ |

### 2B. ICreditControlService — Hold/Release/Temp Limit Workflow (2 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 105 | Implement `PlaceCreditHoldAsync(orgId, customerId, reason, heldBy)` | BE | 1h | ☐ |
| 106 | Set `CreditHold = true`, record reason, date, by on profile | BE | 0.5h | ☐ |
| 107 | Create `CreditHoldRequest` audit record with action = 'Hold' | BE | 0.5h | ☐ |
| 108 | Implement `ReleaseCreditHoldAsync(orgId, customerId, releasedBy)` | BE | 1h | ☐ |
| 109 | Clear `CreditHold`, create audit record with action = 'Release' | BE | 0.5h | ☐ |
| 110 | Implement `SetTemporaryCreditLimitAsync(orgId, customerId, amount, expiryDate, setBy)` | BE | 1h | ☐ |
| 111 | Validate expiry date is in the future | BE | 0.25h | ☐ |
| 112 | Create audit record with action = 'TempIncrease' | BE | 0.5h | ☐ |
| 113 | Implement `UpdateCreditRiskTierAsync(orgId, customerId, tier, updatedBy)` | BE | 0.5h | ☐ |
| 114 | Unit test TS-CR-007: hold → release → SO unblocked | QA | 0.5h | ☐ |
| 115 | Unit test TS-CR-008: concurrent credit checks — no race condition | QA | 1h | ☐ |
| 116 | Unit test TS-CR-010: risk tier updated and persisted | QA | 0.25h | ☐ |
| 117 | Unit test: hold audit trail records all actions in order | QA | 0.5h | ☐ |

### 2C. Credit Check Integration Point (1.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 118 | Define `ICreditCheckIntegration` interface — called by Sales module | BE | 0.5h | ☐ |
| 119 | Implement adapter: Sales SO.Confirm → `CheckCreditAsync()` | BE | 1h | ☐ |
| 120 | Implement adapter: Sales Invoice Creation → warning-only check | BE | 0.5h | ☐ |
| 121 | Define `CreditCheckBlockedException` for SO blocking | BE | 0.25h | ☐ |
| 122 | Ensure integration is opt-in: if no credit limit configured, skip check | BE | 0.5h | ☐ |
| 123 | Document integration contract for Sales module team | DOC | 0.5h | ☐ |
| 124 | Integration test: SO creation triggers credit check | QA | 1h | ☐ |
| 125 | Integration test: SO blocked returns user-friendly message with exposure details | QA | 0.5h | ☐ |
| 126 | Integration test: no profile exists → auto-create default profile, allow SO | QA | 0.5h | ☐ |

### 2D. IAgingReportService — Aged Receivables (2 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 127 | Define `IAgingReportService` interface | BE | 0.5h | ☐ |
| 128 | Define `AgingReportRequest` DTO with `AsOfDate`, `AgingBuckets`, `PartnerId`, `AccountId`, `IncludeDetails`, `SortBy`, `SortDirection` | BE | 0.5h | ☐ |
| 129 | Define `AgingReport` record with `BucketDays`, `BucketLabels`, `Lines`, `Totals`, `DSO_or_DPO` | BE | 0.5h | ☐ |
| 130 | Define `AgingPartnerLine` record with `BucketAmounts[]`, `TotalOutstanding`, `OldestOpenDate` | BE | 0.25h | ☐ |
| 131 | Define `AgingDetailLine` record for line-level drill-down | BE | 0.25h | ☐ |
| 132 | Define `AgingTotals` record | BE | 0.25h | ☐ |
| 133 | Implement `GetAgedReceivablesAsync(orgId, request)` | BE | 2h | ☐ |
| 134 | Build aging SQL query — `DATEDIFF(DAY, entry_date, @AsOfDate)` bucketing on JournalEntryLines | BE | 1.5h | ☐ |
| 135 | Filter: `partner_type = 'Customer'`, `reconciled = 0`, `status = 1`, `account_type = 'Receivable'` | BE | 0.5h | ☐ |
| 136 | Support configurable bucket boundaries (default: 0, 30, 60, 90, 120) | BE | 0.5h | ☐ |
| 137 | Generate dynamic bucket labels from boundaries | BE | 0.25h | ☐ |
| 138 | Resolve partner names via `IPartnerLookupService` batch call | BE | 0.5h | ☐ |
| 139 | Support `IncludeDetails = true` for line-level breakdown | BE | 1h | ☐ |
| 140 | Support `PartnerId` filter for single-customer aging | BE | 0.25h | ☐ |
| 141 | Support sorting: by Partner, TotalOverdue, OldestBucket | BE | 0.5h | ☐ |
| 142 | Unit test TS-AG-001: default buckets — correct assignment | QA | 0.5h | ☐ |
| 143 | Unit test TS-AG-005: drill-down from bucket → line items | QA | 0.5h | ☐ |
| 144 | Unit test TS-AG-006: partially paid invoice shows amount_residual | QA | 0.5h | ☐ |
| 145 | Unit test TS-AG-007: reversed entry excluded | QA | 0.5h | ☐ |
| 146 | Unit test TS-AG-008: draft entry excluded | QA | 0.5h | ☐ |

### 2E. IAgingReportService — Aged Payables + DSO/DPO (1.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 147 | Implement `GetAgedPayablesAsync(orgId, request)` — mirror of receivables with `partner_type = 'Vendor'` and `account_type = 'Payable'` | BE | 1h | ☐ |
| 148 | Implement `GetDSOAsync(orgId, asOfDate, periodDays)` — `(AR Balance / Revenue) × Period Days` | BE | 1h | ☐ |
| 149 | Query revenue from posted SALE journal entries in period | BE | 0.5h | ☐ |
| 150 | Implement `GetDPOAsync(orgId, asOfDate, periodDays)` — `(AP Balance / COGS) × Period Days` | BE | 1h | ☐ |
| 151 | Query COGS from posted PURCH journal entries in period | BE | 0.5h | ☐ |
| 152 | Unit test TS-AG-002: custom buckets [0,15,30,45,60] | QA | 0.5h | ☐ |
| 153 | Unit test TS-AG-003: DSO calculation with 90-day period | QA | 0.5h | ☐ |
| 154 | Unit test TS-AG-004: DPO calculation | QA | 0.5h | ☐ |

### 2F. Performance Index Migration (0.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 155 | Write EF Core migration `M16_CreateAgingPerformanceIndex` | DB | 0.25h | ☐ |
| 156 | Create index `IX_JEL_AgingAR` on `(OrgId, PartnerType, Reconciled)` INCLUDE `(PartnerId, AmountResidual, JournalEntryId, AccountId)` WHERE `Reconciled = 0` | DB | 0.5h | ☐ |
| 157 | Create mirror index `IX_JEL_AgingAP` for AP queries | DB | 0.5h | ☐ |
| 158 | Run migration and verify query plan uses new indexes | DB | 0.5h | ☐ |
| 159 | Performance test: aging report with 10,000 open items — target < 2 seconds | QA | 1h | ☐ |

**Phase 2 Subtotal: 76 tasks (53 BE, 5 DB, 17 QA, 1 DOC)**

---

## Phase 3 — Dunning & Statements (10 days)

### 3A. DunningProcedures & DunningLevels Entities (1.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 160 | Create `DunningProcedure` entity class | BE | 0.5h | ☐ |
| 161 | Define properties: `Code`, `Name`, `Description`, `IsDefault`, `IsActive`, `GraceDays`, `MinimumAmount`, `BlockSalesAtLevel` | BE | 0.5h | ☐ |
| 162 | Configure UQ on `(OrgId, Code)` | DB | 0.25h | ☐ |
| 163 | Write EF Core migration `M4_CreateDunningProcedures` | DB | 0.25h | ☐ |
| 164 | Create `DunningLevel` entity class | BE | 0.5h | ☐ |
| 165 | Define properties: `LevelNumber`, `DaysAfterPrevious`, `SubjectTemplate`, `BodyTemplate`, `IncludeInvoiceList`, `IncludeStatement` | BE | 0.5h | ☐ |
| 166 | Define `ChargeAmount`, `ChargeAccountId`, `ActionType` (Email/Letter/Manual), `EscalationNote` | BE | 0.5h | ☐ |
| 167 | Configure FK to `DunningProcedures` | DB | 0.25h | ☐ |
| 168 | Configure UQ on `(DunningProcedureId, LevelNumber)` | DB | 0.25h | ☐ |
| 169 | Configure CHECK `CK_DL_ActionType` — `IN ('Email','Letter','Manual')` | DB | 0.25h | ☐ |
| 170 | Write EF Core migration `M5_CreateDunningLevels` | DB | 0.25h | ☐ |
| 171 | Run migrations M4, M5 on dev database | DB | 0.25h | ☐ |
| 172 | Create `DunningProcedureDto`, `DunningLevelDto` | BE | 0.5h | ☐ |
| 173 | Create `CreateDunningProcedureRequest`, `UpdateDunningProcedureRequest` DTOs | BE | 0.5h | ☐ |
| 174 | Unit test: create procedure with 4 levels | QA | 0.5h | ☐ |
| 175 | Unit test: UQ rejects duplicate code per org | QA | 0.25h | ☐ |
| 176 | Unit test: level number uniqueness within procedure | QA | 0.25h | ☐ |

### 3B. DunningRuns & DunningRunItems Entities (1.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 177 | Create `DunningRun` entity class | BE | 0.5h | ☐ |
| 178 | Define properties: `RunDate`, `Status` (Draft/Processing/Completed/Cancelled), `TotalCustomers`, `TotalAmount`, `ItemsProcessed`, `ItemsSent`, `ItemsSkipped` | BE | 0.5h | ☐ |
| 179 | Configure CHECK `CK_DR_Status` | DB | 0.25h | ☐ |
| 180 | Configure index `IX_DR_Org` on `(OrgId, RunDate DESC)` | DB | 0.25h | ☐ |
| 181 | Write EF Core migration `M6_CreateDunningRuns` | DB | 0.25h | ☐ |
| 182 | Create `DunningRunItem` entity class | BE | 0.5h | ☐ |
| 183 | Define properties: `PartnerId`, `DunningLevel`, `PreviousLevel`, `OverdueAmount`, `ChargeAmount`, `ChargeJEId`, `OpenItemCount`, `OldestDueDate` | BE | 0.5h | ☐ |
| 184 | Define `ActionType`, `SentAt`, `SentTo`, `Status` (Pending/Sent/Skipped/Failed), `SkipReason` | BE | 0.5h | ☐ |
| 185 | Configure FK to `DunningRuns` | DB | 0.25h | ☐ |
| 186 | Configure CHECK `CK_DRI_Status` | DB | 0.25h | ☐ |
| 187 | Configure index `IX_DRI_RunId` and `IX_DRI_Partner` | DB | 0.25h | ☐ |
| 188 | Write EF Core migration `M7_CreateDunningRunItems` | DB | 0.25h | ☐ |
| 189 | Run migrations M6, M7 on dev database | DB | 0.25h | ☐ |
| 190 | Create `DunningRunPreview`, `DunningRunResult`, `DunningRunDetail`, `DunningRunSummary` DTOs | BE | 0.5h | ☐ |
| 191 | Create `DunningRunItemDto`, `DunningHistory` DTOs | BE | 0.25h | ☐ |
| 192 | Unit test: create run with items | QA | 0.5h | ☐ |
| 193 | Unit test: status transitions Draft → Processing → Completed | QA | 0.25h | ☐ |

### 3C. IDunningService — Preview and Execution Logic (3 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 194 | Define `IDunningService` interface with all methods | BE | 0.5h | ☐ |
| 195 | Implement `CreateProcedureAsync` | BE | 0.5h | ☐ |
| 196 | Implement `UpdateProcedureAsync` | BE | 0.5h | ☐ |
| 197 | Implement `ListProceduresAsync` | BE | 0.25h | ☐ |
| 198 | Implement `PreviewDunningRunAsync(orgId, asOfDate)` — core dunning logic | BE | 3h | ☐ |
| 199 | Step 1: Load all customers with open receivables overdue beyond grace period | BE | 0.5h | ☐ |
| 200 | Step 2: Filter by `overdue_amount >= minimum_amount` (BR-DN-002) | BE | 0.25h | ☐ |
| 201 | Step 3: Load assigned dunning procedure per customer (from profile or org default) | BE | 0.5h | ☐ |
| 202 | Step 4: Read `current_dunning_level` from PartnerFinancialProfile | BE | 0.25h | ☐ |
| 203 | Step 5: Determine next level — if level 0 and past grace → level 1; if level N and days since last >= `days_after_previous` → level N+1 (BR-DN-003, BR-DN-004) | BE | 1h | ☐ |
| 204 | Step 6: If eligible, add to preview with proposed level, amount, action | BE | 0.5h | ☐ |
| 205 | Create DunningRun record with status = 'Draft' | BE | 0.25h | ☐ |
| 206 | Create DunningRunItem records for each eligible customer | BE | 0.5h | ☐ |
| 207 | Implement `ExecuteDunningRunAsync(orgId, runId, executedBy)` | BE | 3h | ☐ |
| 208 | Step 1: Validate run status = 'Draft', set to 'Processing' | BE | 0.25h | ☐ |
| 209 | Step 2: For each item — generate dunning notice body from template | BE | 1h | ☐ |
| 210 | Template placeholder substitution: `{CustomerName}`, `{Amount}`, `{DueDate}`, `{InvoiceList}` | BE | 0.5h | ☐ |
| 211 | Step 3: If `charge_amount > 0` — create JE: Dr Receivable, Cr Dunning Revenue (BR-DN-007) | BE | 1h | ☐ |
| 212 | Step 4: Send email via email service (or mark for manual action) | BE | 0.5h | ☐ |
| 213 | Step 5: Update PartnerFinancialProfile: `current_dunning_level`, `last_dunning_date` | BE | 0.5h | ☐ |
| 214 | Step 6: If `level >= block_sales_at_level` → set `credit_hold = true` with reason (BR-DN-005) | BE | 0.5h | ☐ |
| 215 | Step 7: Mark item status = 'Sent' or 'Failed' | BE | 0.25h | ☐ |
| 216 | Step 8: Update run totals and set status = 'Completed' | BE | 0.25h | ☐ |
| 217 | Implement one-run-per-day guard (BR-DN-010) | BE | 0.5h | ☐ |
| 218 | Implement `GetDunningRunAsync(orgId, runId)` | BE | 0.25h | ☐ |
| 219 | Implement `ListDunningRunsAsync(orgId, limit)` | BE | 0.25h | ☐ |
| 220 | Implement `GetCustomerDunningHistoryAsync(orgId, customerId)` | BE | 0.5h | ☐ |
| 221 | Implement `ResetDunningLevelAsync(orgId, customerId, reason, resetBy)` (BR-DN-009) | BE | 0.5h | ☐ |
| 222 | Implement auto-reset: when customer balance = 0 → set level to 0 (BR-DN-006) | BE | 0.5h | ☐ |
| 223 | Register `IDunningService` in DI | BE | 0.25h | ☐ |
| 224 | Unit test TS-DN-001: overdue 12 days, grace=5, level 1 at day 7 → eligible | QA | 0.5h | ☐ |
| 225 | Unit test TS-DN-002: overdue 3 days, grace=5 → not eligible | QA | 0.25h | ☐ |
| 226 | Unit test TS-DN-003: level 2, 20 days since last, next at 30 → not yet | QA | 0.5h | ☐ |
| 227 | Unit test TS-DN-004: level 2, 35 days since last → eligible for 3 | QA | 0.5h | ☐ |
| 228 | Unit test TS-DN-005: total overdue $30, minimum $50 → skipped | QA | 0.25h | ☐ |
| 229 | Unit test TS-DN-006: execute run → emails sent, levels updated | QA | 1h | ☐ |
| 230 | Unit test TS-DN-007: dunning charge creates balanced JE | QA | 0.5h | ☐ |
| 231 | Unit test TS-DN-008: reaches block level → credit_hold set | QA | 0.5h | ☐ |
| 232 | Unit test TS-DN-009: customer pays all → level resets | QA | 0.5h | ☐ |
| 233 | Unit test TS-DN-010: manual level reset by admin | QA | 0.25h | ☐ |
| 234 | Unit test TS-DN-011: two runs same day → second rejected | QA | 0.5h | ☐ |

### 3D. Dunning Email Template Engine (1.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 235 | Create `IDunningTemplateEngine` interface | BE | 0.25h | ☐ |
| 236 | Implement template placeholder substitution engine | BE | 1h | ☐ |
| 237 | Support placeholders: `{CustomerName}`, `{CustomerCode}`, `{TotalOverdue}`, `{OldestDueDate}`, `{DunningLevel}`, `{InvoiceList}` | BE | 0.5h | ☐ |
| 238 | Implement `{InvoiceList}` — generate formatted list of open invoices | BE | 0.5h | ☐ |
| 239 | Implement statement PDF attachment when `IncludeStatement = true` | BE | 0.5h | ☐ |
| 240 | Implement dunning charge JE creation as A47 journal entry | BE | 0.5h | ☐ |
| 241 | Call `IJournalEntryService.CreateAndPostAsync()` with SALE type, Dr Receivable / Cr Dunning Revenue | BE | 0.5h | ☐ |
| 242 | Unit test: template with all placeholders renders correctly | QA | 0.5h | ☐ |
| 243 | Unit test: charge JE is balanced and posted | QA | 0.5h | ☐ |

### 3E. IStatementService — Customer & Vendor Statements (2 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 244 | Define `IStatementService` interface | BE | 0.5h | ☐ |
| 245 | Define `CustomerStatementRequest`, `VendorStatementRequest` DTOs | BE | 0.25h | ☐ |
| 246 | Define `StatementResult` record with `Lines[]`, `Aging?`, `PdfFilePath` | BE | 0.5h | ☐ |
| 247 | Define `StatementLine` record: Date, EntryNumber, Reference, Description, Debit, Credit, RunningBalance, Status | BE | 0.25h | ☐ |
| 248 | Define `BatchStatementRequest`, `BatchStatementResult` DTOs | BE | 0.25h | ☐ |
| 249 | Define `StatementType` enum: Activity, OpenItems | BE | 0.25h | ☐ |
| 250 | Implement `GenerateCustomerStatementAsync(orgId, request)` | BE | 2h | ☐ |
| 251 | Activity variant: all posted transactions in date range with running balance (BR-ST-001) | BE | 1h | ☐ |
| 252 | OpenItems variant: only unreconciled items as of statement date (BR-ST-002) | BE | 1h | ☐ |
| 253 | Compute opening balance from all posted lines before `FromDate` (BR-ST-003) | BE | 0.5h | ☐ |
| 254 | Compute closing balance: opening + debits - credits (BR-ST-004) | BE | 0.25h | ☐ |
| 255 | Include aging summary when `IncludeAging = true` | BE | 0.5h | ☐ |
| 256 | Implement `GenerateVendorStatementAsync(orgId, request)` — mirror with `partner_type = 'Vendor'` | BE | 0.5h | ☐ |
| 257 | Implement `EmailStatementAsync(orgId, statementId)` — send PDF to partner email | BE | 0.5h | ☐ |
| 258 | Implement `BatchGenerateStatementsAsync(orgId, request)` — filter by minimum balance (BR-ST-005) | BE | 1h | ☐ |
| 259 | Log every statement generation in `StatementLog` | BE | 0.25h | ☐ |
| 260 | Register `IStatementService` in DI | BE | 0.25h | ☐ |
| 261 | Unit test TS-ST-001: activity statement, 10 transactions, running balance | QA | 0.5h | ☐ |
| 262 | Unit test TS-ST-002: open items statement excludes paid | QA | 0.5h | ☐ |
| 263 | Unit test TS-ST-003: opening balance correct | QA | 0.5h | ☐ |
| 264 | Unit test TS-ST-005: batch filter by min balance $1,000 | QA | 0.5h | ☐ |
| 265 | Unit test TS-ST-006: email sent, logged | QA | 0.5h | ☐ |

### 3F. Statement PDF Rendering (0.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 266 | Create PDF template for customer statement | BE | 1h | ☐ |
| 267 | Include company header, partner address, date range, transaction table, aging grid, totals | BE | 0.5h | ☐ |
| 268 | Implement PDF rendering using QuestPDF or equivalent | BE | 1h | ☐ |
| 269 | Unit test TS-ST-004: PDF generated and readable | QA | 0.5h | ☐ |
| 270 | Unit test TS-ST-007: vendor statement with reconciliation | QA | 0.5h | ☐ |

**Phase 3 Subtotal: 111 tasks (73 BE, 15 DB, 23 QA, 0 DOC)**

---

## Phase 4 — 3-Way Matching & Payment Scheduling (12 days)

### 4A. InvoiceMatchHeaders & InvoiceMatchLines Entities (2 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 271 | Create `InvoiceMatchHeader` entity class | BE | 0.5h | ☐ |
| 272 | Define properties: `VendorId`, `VendorInvoiceNumber`, `VendorInvoiceDate`, `PurchaseOrderId`, `PurchaseOrderNumber`, `GrnId`, `GrnNumber` | BE | 0.5h | ☐ |
| 273 | Define amount properties: `TotalPOAmount`, `TotalGRNAmount`, `TotalInvoiceAmount`, `VarianceAmount`, `VariancePercentage` | BE | 0.5h | ☐ |
| 274 | Define match properties: `MatchStatus`, `WithinTolerance`, `ToleranceRuleId`, `JournalEntryId` | BE | 0.5h | ☐ |
| 275 | Define workflow properties: `MatchedAt`, `MatchedBy`, `OverrideReason`, `OverrideBy` | BE | 0.25h | ☐ |
| 276 | Configure CHECK `CK_IMH_Status` — `IN ('Pending','Matched','PartialMatch','Mismatch','Overridden','Cancelled')` | DB | 0.25h | ☐ |
| 277 | Configure UQ `UQ_IMH_OrgVendorInvoice` on `(OrgId, VendorId, VendorInvoiceNumber)` | DB | 0.25h | ☐ |
| 278 | Configure indexes: `IX_IMH_PO`, `IX_IMH_Status` | DB | 0.25h | ☐ |
| 279 | Write EF Core migration `M10_CreateInvoiceMatchHeaders` | DB | 0.25h | ☐ |
| 280 | Create `InvoiceMatchLine` entity class | BE | 0.5h | ☐ |
| 281 | Define properties: `LineNumber`, `ItemDescription`, PO/GRN/Invoice quantities and prices, variances, `MatchStatus`, `AccountId` | BE | 0.5h | ☐ |
| 282 | Configure FK to `InvoiceMatchHeaders` with CASCADE delete | DB | 0.25h | ☐ |
| 283 | Configure CHECK `CK_IML_Status` — `IN ('Pending','Matched','QuantityMismatch','PriceMismatch','Overridden')` | DB | 0.25h | ☐ |
| 284 | Write EF Core migration `M11_CreateInvoiceMatchLines` | DB | 0.25h | ☐ |
| 285 | Run migrations M10, M11 on dev database | DB | 0.25h | ☐ |
| 286 | Create `MatchInvoiceRequest`, `MatchResult`, `MatchApprovalResult`, `InvoiceMatchSummary` DTOs | BE | 0.5h | ☐ |
| 287 | Create `MatchFilter` DTO for listing | BE | 0.25h | ☐ |
| 288 | Unit test: create match header with lines | QA | 0.5h | ☐ |
| 289 | Unit test: UQ rejects duplicate vendor invoice number per vendor per org | QA | 0.25h | ☐ |
| 290 | Unit test: CASCADE delete removes lines when header deleted | QA | 0.25h | ☐ |

### 4B. IInvoiceMatchingService — 3-Way and 2-Way Matching (3 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 291 | Define `IInvoiceMatchingService` interface | BE | 0.5h | ☐ |
| 292 | Define `ISourceDocumentLookupService` — resolves PO and GRN details from procurement/inventory (soft-link) | BE | 0.5h | ☐ |
| 293 | Implement `ISourceDocumentLookupService` — fetch PO lines from procurement | BE | 1h | ☐ |
| 294 | Implement GRN line fetch from inventory | BE | 0.5h | ☐ |
| 295 | Implement `MatchInvoiceAsync(orgId, request)` — 3-way matching | BE | 3h | ☐ |
| 296 | Step 1: Load PO details via soft-link lookup | BE | 0.5h | ☐ |
| 297 | Step 2: Load GRN details via soft-link lookup | BE | 0.5h | ☐ |
| 298 | Step 3: For each line — compare invoice_quantity vs grn_quantity → `quantity_variance` | BE | 0.5h | ☐ |
| 299 | Step 4: Compare invoice_unit_price vs po_unit_price → `price_variance` | BE | 0.5h | ☐ |
| 300 | Step 5: Compute `total_variance = invoice_line_total - po_line_total` | BE | 0.25h | ☐ |
| 301 | Step 6: Load tolerance rule (vendor-specific or org default) | BE | 0.5h | ☐ |
| 302 | Step 7: Apply price tolerance check — `abs(variance) / po_price <= tolerance_percent` OR `abs(variance) <= tolerance_amount` (BR-MT-003) | BE | 0.5h | ☐ |
| 303 | Step 8: Apply quantity tolerance check — invoice_qty must not exceed grn_qty by tolerance (BR-MT-004) | BE | 0.5h | ☐ |
| 304 | Step 9: Determine line match status: Matched / QuantityMismatch / PriceMismatch | BE | 0.5h | ☐ |
| 305 | Step 10: Determine header match status: Matched / PartialMatch / Mismatch | BE | 0.5h | ☐ |
| 306 | Step 11: If `auto_approve_within = true` AND all within tolerance → auto-approve (BR-MT-005) | BE | 0.5h | ☐ |
| 307 | Step 12: On auto-approve — create GL entry via `IJournalEntryService` | BE | 0.5h | ☐ |
| 308 | Implement `MatchInvoiceTwoWayAsync(orgId, request)` — PO ↔ Invoice only (BR-MT-002) | BE | 1h | ☐ |
| 309 | Implement duplicate invoice check — same vendor + vendor_invoice_number rejects (BR-MT-007) | BE | 0.5h | ☐ |
| 310 | Implement `GetMatchesByPOAsync(orgId, purchaseOrderId)` | BE | 0.25h | ☐ |
| 311 | Implement `ListPendingMatchesAsync(orgId, filter)` with pagination | BE | 0.5h | ☐ |
| 312 | Register `IInvoiceMatchingService` in DI | BE | 0.25h | ☐ |
| 313 | Unit test TS-MT-001: 3-way match, all within tolerance → Matched | QA | 0.5h | ☐ |
| 314 | Unit test TS-MT-002: price variance 3%, tolerance 2% → Mismatch | QA | 0.5h | ☐ |
| 315 | Unit test TS-MT-003: price variance 1.5%, tolerance 2% → Matched | QA | 0.5h | ☐ |
| 316 | Unit test TS-MT-004: invoice qty > GRN qty → QuantityMismatch | QA | 0.5h | ☐ |
| 317 | Unit test TS-MT-007: duplicate vendor invoice → rejected | QA | 0.5h | ☐ |
| 318 | Unit test TS-MT-008: auto-approve within tolerance | QA | 0.5h | ☐ |
| 319 | Unit test TS-MT-009: 2-way match (no GRN) | QA | 0.5h | ☐ |
| 320 | Unit test TS-MT-010: partial delivery, GRN < PO, invoice = GRN | QA | 0.5h | ☐ |

### 4C. Match Override & Approval Workflow (1.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 321 | Implement `OverrideMatchAsync(orgId, matchId, reason, overriddenBy)` | BE | 0.5h | ☐ |
| 322 | Validate match is in Mismatch/PartialMatch status | BE | 0.25h | ☐ |
| 323 | Record override reason and override_by (BR-MT-006) | BE | 0.25h | ☐ |
| 324 | Set all mismatched lines to 'Overridden', header to 'Overridden' | BE | 0.25h | ☐ |
| 325 | Implement `ApproveMatchAsync(orgId, matchId, approvedBy)` | BE | 0.5h | ☐ |
| 326 | Validate match is Matched or Overridden | BE | 0.25h | ☐ |
| 327 | Create GL entry via `IJournalEntryService.CreateAndPostAsync()` — Dr Expense/Inventory, Cr AP (BR-MT-008) | BE | 1h | ☐ |
| 328 | Store `journal_entry_id` on match header | BE | 0.25h | ☐ |
| 329 | Create PaymentSchedule for the approved invoice | BE | 0.5h | ☐ |
| 330 | Unit test TS-MT-005: override with reason → Overridden | QA | 0.5h | ☐ |
| 331 | Unit test TS-MT-006: approve → GL entry created | QA | 0.5h | ☐ |
| 332 | Unit test: approve creates payment schedule | QA | 0.5h | ☐ |

### 4D. PaymentSchedules Entity (1.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 333 | Create `PaymentSchedule` entity class | BE | 0.5h | ☐ |
| 334 | Define properties: `VendorId`, `JournalEntryId`, `JournalEntryLineId`, `DueDate`, `OriginalAmount`, `AmountRemaining` | BE | 0.5h | ☐ |
| 335 | Define `PaymentPriority`, `PaymentMethod`, `BankAccountId` | BE | 0.25h | ☐ |
| 336 | Define `PaymentBlock`, `BlockReason`, `BlockedBy`, `BlockedAt` | BE | 0.25h | ☐ |
| 337 | Define discount properties: `DiscountDate`, `DiscountAmount`, `DiscountPercent` | BE | 0.25h | ☐ |
| 338 | Define `PaidInRunId`, `Status` (Open/Blocked/Scheduled/PartiallyPaid/Paid/Cancelled) | BE | 0.25h | ☐ |
| 339 | Configure FK to `JournalEntries` | DB | 0.25h | ☐ |
| 340 | Configure CHECK `CK_PS_Status`, `CK_PS_Priority` | DB | 0.25h | ☐ |
| 341 | Configure index `IX_PS_DueDate` on `(OrgId, DueDate, Status)` WHERE `Status IN ('Open','Scheduled')` | DB | 0.25h | ☐ |
| 342 | Configure index `IX_PS_Vendor` on `(OrgId, VendorId, Status)` | DB | 0.25h | ☐ |
| 343 | Configure index `IX_PS_Discount` on `(OrgId, DiscountDate)` WHERE `DiscountDate IS NOT NULL AND Status = 'Open'` | DB | 0.25h | ☐ |
| 344 | Write EF Core migration `M12_CreatePaymentSchedules` | DB | 0.5h | ☐ |
| 345 | Run migration M12 on dev database | DB | 0.25h | ☐ |
| 346 | Create `PaymentScheduleDto`, `CreatePaymentScheduleRequest` DTOs | BE | 0.5h | ☐ |
| 347 | Create `CashFlowForecast` DTO | BE | 0.25h | ☐ |
| 348 | Unit test: create payment schedule with discount terms | QA | 0.5h | ☐ |
| 349 | Unit test: filtered index on due date returns only Open/Scheduled | QA | 0.25h | ☐ |
| 350 | Unit test: FK to JournalEntries enforced | QA | 0.25h | ☐ |

### 4E. IPaymentScheduleService (2 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 351 | Define `IPaymentScheduleService` interface | BE | 0.5h | ☐ |
| 352 | Implement `CreateScheduleAsync(orgId, request)` — called when AP entry posted | BE | 1h | ☐ |
| 353 | Auto-populate discount fields from `EarlyPaymentTerms` if applicable | BE | 0.5h | ☐ |
| 354 | Implement `GetUpcomingPaymentsAsync(orgId, fromDate, toDate)` | BE | 0.5h | ☐ |
| 355 | Implement `BlockPaymentAsync(orgId, scheduleId, reason, blockedBy)` | BE | 0.5h | ☐ |
| 356 | Implement `UnblockPaymentAsync(orgId, scheduleId, unblockedBy)` | BE | 0.5h | ☐ |
| 357 | Implement `GetEarlyDiscountOpportunitiesAsync(orgId, asOfDate, lookAheadDays)` | BE | 0.5h | ☐ |
| 358 | Implement `GetPayablesCashFlowAsync(orgId, fromDate, weeks)` — sum by week | BE | 1h | ☐ |
| 359 | Register `IPaymentScheduleService` in DI | BE | 0.25h | ☐ |
| 360 | Unit test: schedule created on AP entry posting | QA | 0.5h | ☐ |
| 361 | Unit test: upcoming payments filtered by date range | QA | 0.5h | ☐ |
| 362 | Unit test: block/unblock workflow | QA | 0.5h | ☐ |
| 363 | Unit test: discount opportunities within 7-day window | QA | 0.5h | ☐ |
| 364 | Unit test: cash flow forecast groups by week | QA | 0.5h | ☐ |

### 4F. IEarlyPaymentDiscountService (1 day)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 365 | Define `IEarlyPaymentDiscountService` interface | BE | 0.25h | ☐ |
| 366 | Define `DiscountCalculation` record | BE | 0.25h | ☐ |
| 367 | Implement `ListTermsAsync(orgId)` | BE | 0.25h | ☐ |
| 368 | Implement `CreateTermAsync(orgId, request)` | BE | 0.25h | ☐ |
| 369 | Implement `ApplyDiscountTermsAsync(orgId, scheduleId, termId)` — set discount fields on schedule | BE | 0.5h | ☐ |
| 370 | Implement `CalculateDiscountAsync(orgId, scheduleId, paymentDate)` | BE | 0.5h | ☐ |
| 371 | Apply BR-EP-001: eligible only if `paymentDate <= invoice_date + discount_days` | BE | 0.25h | ☐ |
| 372 | Apply BR-EP-002: `discount_amount = original_amount × discount_percent / 100` | BE | 0.25h | ☐ |
| 373 | Register in DI | BE | 0.25h | ☐ |
| 374 | Unit test TS-EP-001: pay within window → discount applied | QA | 0.5h | ☐ |
| 375 | Unit test TS-EP-002: pay after window → no discount | QA | 0.5h | ☐ |
| 376 | Unit test TS-EP-004: discount opportunities list shows upcoming | QA | 0.5h | ☐ |

### 4G. Cash Flow Forecast Computation (1 day)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 377 | Implement weekly aggregation of PaymentSchedules by due_date | BE | 1h | ☐ |
| 378 | Include blocked items as separate category in forecast | BE | 0.5h | ☐ |
| 379 | Include discount savings if paid by discount date | BE | 0.5h | ☐ |
| 380 | Return 8-week (or configurable) forecast | BE | 0.25h | ☐ |
| 381 | Unit test: forecast correctly groups by week boundary | QA | 0.5h | ☐ |
| 382 | Unit test: blocked items shown separately | QA | 0.5h | ☐ |

**Phase 4 Subtotal: 112 tasks (72 BE, 16 DB, 24 QA, 0 DOC)**

---

## Phase 5 — Payment Run Engine (10 days)

### 5A. PaymentRuns, PaymentRunItems, PaymentRunBatches Entities (2 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 383 | Create `PaymentRun` entity class | BE | 0.5h | ☐ |
| 384 | Define properties: `RunNumber`, `RunDate`, `PaymentDate`, `Status`, `SelectionCriteria` (JSON) | BE | 0.5h | ☐ |
| 385 | Define `BankAccountId`, `PaymentMethod`, `IncludeDiscount` | BE | 0.25h | ☐ |
| 386 | Define totals: `TotalVendors`, `TotalItems`, `TotalAmount`, `TotalDiscount`, `NetPaymentAmount` | BE | 0.25h | ☐ |
| 387 | Define workflow: `ProposedBy`, `ProposedAt`, `ApprovedBy`, `ApprovedAt`, `ExecutedBy`, `ExecutedAt` | BE | 0.25h | ☐ |
| 388 | Define `PaymentFilePath` for export | BE | 0.25h | ☐ |
| 389 | Configure UQ `UQ_PR_OrgNumber` on `(OrgId, RunNumber)` | DB | 0.25h | ☐ |
| 390 | Configure CHECK `CK_PR_Status` — 7 valid statuses | DB | 0.25h | ☐ |
| 391 | Configure indexes: `IX_PR_OrgDate`, `IX_PR_Status` | DB | 0.25h | ☐ |
| 392 | Write EF Core migration `M13_CreatePaymentRuns` | DB | 0.25h | ☐ |
| 393 | Create `PaymentRunItem` entity class | BE | 0.5h | ☐ |
| 394 | Define properties: `VendorId`, `PaymentScheduleId`, `JournalEntryId`, `EntryNumber`, `DueDate` | BE | 0.25h | ☐ |
| 395 | Define `OriginalAmount`, `DiscountAmount`, `NetPaymentAmount`, `PaymentMethod` | BE | 0.25h | ☐ |
| 396 | Define `Status` (Proposed/Approved/Removed/Paid/Failed), `RemovedReason`, `PaymentJEId`, `PaidAt` | BE | 0.25h | ☐ |
| 397 | Configure FK to `PaymentRuns`, FK to `PaymentSchedules` | DB | 0.25h | ☐ |
| 398 | Configure CHECK `CK_PRI_Status` | DB | 0.25h | ☐ |
| 399 | Configure indexes: `IX_PRI_RunId`, `IX_PRI_Vendor` | DB | 0.25h | ☐ |
| 400 | Write EF Core migration `M14_CreatePaymentRunItems` | DB | 0.25h | ☐ |
| 401 | Create `PaymentRunBatch` entity class | BE | 0.5h | ☐ |
| 402 | Define properties: `VendorId`, `VendorName` (denormalized), `PaymentMethod`, `BankAccountInfo` | BE | 0.25h | ☐ |
| 403 | Define `ItemCount`, `TotalAmount`, `TotalDiscount`, `NetAmount`, `PaymentReference`, `PaymentJEId`, `Status` | BE | 0.25h | ☐ |
| 404 | Configure FK to `PaymentRuns` | DB | 0.25h | ☐ |
| 405 | Configure CHECK `CK_PRB_Status` — `IN ('Pending','Paid','Failed')` | DB | 0.25h | ☐ |
| 406 | Configure index `IX_PRB_RunId` | DB | 0.25h | ☐ |
| 407 | Write EF Core migration `M15_CreatePaymentRunBatches` | DB | 0.25h | ☐ |
| 408 | Run migrations M13, M14, M15 on dev database | DB | 0.25h | ☐ |
| 409 | Create all payment run DTOs: `PaymentRunProposal`, `PaymentRunDetail`, `PaymentRunSummary`, `PaymentRunVendorGroup`, `PaymentRunItemDto`, `PaymentRunResult` | BE | 1h | ☐ |
| 410 | Create `PaymentRunProposalRequest` DTO | BE | 0.5h | ☐ |
| 411 | Implement `IPaymentRunSequenceGenerator` — generates `PR/YYYY/NNNN` numbers | BE | 0.5h | ☐ |
| 412 | Unit test: create run with items and batches | QA | 0.5h | ☐ |
| 413 | Unit test: run number uniqueness per org | QA | 0.25h | ☐ |
| 414 | Unit test: FK to PaymentSchedules enforced | QA | 0.25h | ☐ |

### 5B. IPaymentRunService — Proposal Generation (2 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 415 | Define `IPaymentRunService` interface with all methods | BE | 0.5h | ☐ |
| 416 | Implement `CreateProposalAsync(orgId, request)` | BE | 3h | ☐ |
| 417 | Step 1: Query PaymentSchedules WHERE `due_date <= due_date_cutoff` AND `status = 'Open'` (BR-PR-001) | BE | 0.5h | ☐ |
| 418 | Step 2: Exclude items with `payment_block = true` (BR-PR-002) — include in separate blocked list | BE | 0.5h | ☐ |
| 419 | Step 3: Filter by `PaymentMethod` if specified | BE | 0.25h | ☐ |
| 420 | Step 4: Filter by `VendorId` if specified | BE | 0.25h | ☐ |
| 421 | Step 5: Filter by `MinPriority` if specified | BE | 0.25h | ☐ |
| 422 | Step 6: If `IncludeDiscount = true` — check discount eligibility for each item (BR-PR-004) | BE | 0.5h | ☐ |
| 423 | Step 7: If `payment_date <= discount_date` → compute discount amount | BE | 0.5h | ☐ |
| 424 | Step 8: Group items by vendor (BR-PR-003) | BE | 0.5h | ☐ |
| 425 | Step 9: Create PaymentRun with status = 'Proposed' | BE | 0.25h | ☐ |
| 426 | Step 10: Create PaymentRunItems with status = 'Proposed' | BE | 0.5h | ☐ |
| 427 | Step 11: Compute totals | BE | 0.25h | ☐ |
| 428 | Resolve vendor names via `IPartnerLookupService` | BE | 0.25h | ☐ |
| 429 | Implement `GetPaymentRunAsync(orgId, runId)` with items and batches | BE | 0.5h | ☐ |
| 430 | Implement `ListPaymentRunsAsync(orgId, limit)` | BE | 0.25h | ☐ |
| 431 | Unit test TS-PR-001: 5 vendors, 15 items → correct totals | QA | 0.5h | ☐ |
| 432 | Unit test TS-PR-002: blocked items excluded | QA | 0.5h | ☐ |

### 5C. IPaymentRunService — Review/Edit/Approve Workflow (2 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 433 | Implement `RemoveItemAsync(orgId, runId, itemId, reason, removedBy)` | BE | 0.5h | ☐ |
| 434 | Set item status = 'Removed', record reason | BE | 0.25h | ☐ |
| 435 | Recalculate run totals | BE | 0.5h | ☐ |
| 436 | Implement `RestoreItemAsync(orgId, runId, itemId, restoredBy)` | BE | 0.5h | ☐ |
| 437 | Implement `UpdatePaymentMethodAsync(orgId, runId, itemId, paymentMethod)` | BE | 0.25h | ☐ |
| 438 | Implement `ApprovePaymentRunAsync(orgId, runId, approvedBy)` (BR-PR-006) | BE | 0.5h | ☐ |
| 439 | Validate status = 'Proposed' or 'Reviewing' | BE | 0.25h | ☐ |
| 440 | Set status = 'Approved', record approved_by, approved_at | BE | 0.25h | ☐ |
| 441 | Set all 'Proposed' items to 'Approved' | BE | 0.25h | ☐ |
| 442 | Implement `CancelPaymentRunAsync(orgId, runId, reason, cancelledBy)` (BR-PR-010) | BE | 0.5h | ☐ |
| 443 | Validate: only Draft/Proposed/Reviewing/Approved can cancel | BE | 0.25h | ☐ |
| 444 | Unit test TS-PR-003: remove item → totals recalculated | QA | 0.5h | ☐ |
| 445 | Unit test TS-PR-004: approve → status = Approved | QA | 0.25h | ☐ |
| 446 | Unit test TS-PR-010: cancel approved run | QA | 0.5h | ☐ |
| 447 | Unit test TS-PR-011: execute completed run → rejected (BR-PR-009) | QA | 0.5h | ☐ |

### 5D. IPaymentRunService — Execution (2.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 448 | Implement `ExecutePaymentRunAsync(orgId, runId, executedBy)` | BE | 3h | ☐ |
| 449 | Step 1: Validate status = 'Approved', set to 'Executing' | BE | 0.25h | ☐ |
| 450 | Step 2: Group approved items by vendor → create PaymentRunBatches | BE | 0.5h | ☐ |
| 451 | Step 3: For each vendor batch — create JournalEntry via `IJournalEntryService.CreateAndPostAsync()` (BR-PR-007) | BE | 2h | ☐ |
| 452 | JE lines: Dr AP (per vendor payable account) = net_amount | BE | 0.5h | ☐ |
| 453 | JE lines: Dr Purchase Discount (if discount taken) = discount_amount (BR-PR-005, BR-EP-003) | BE | 0.5h | ☐ |
| 454 | JE lines: Cr Bank Account = total_amount | BE | 0.5h | ☐ |
| 455 | Journal Type = BANK (or CASH based on payment method) | BE | 0.25h | ☐ |
| 456 | Step 4: Store `payment_je_id` on batch and items | BE | 0.25h | ☐ |
| 457 | Step 5: For each item — update PaymentSchedule: `status = 'Paid'`, `paid_in_run_id` | BE | 0.5h | ☐ |
| 458 | Step 6: Auto-reconcile — call `IReconciliationService.ReconcileAsync()` to match payment line with original AP invoice line (BR-PR-008) | BE | 1h | ☐ |
| 459 | Step 7: Update batch status = 'Paid' | BE | 0.25h | ☐ |
| 460 | Step 8: Update run totals, set status = 'Completed' | BE | 0.25h | ☐ |
| 461 | Handle partial failure: if one batch fails, mark it 'Failed', continue others | BE | 0.5h | ☐ |
| 462 | Lock payment schedules during execution to prevent double payment (BR-PR-012) | BE | 1h | ☐ |
| 463 | Raise `PaymentRunCompletedEvent` | BE | 0.25h | ☐ |
| 464 | Register `IPaymentRunService` in DI | BE | 0.25h | ☐ |
| 465 | Unit test TS-PR-005: execute → JEs created per vendor | QA | 1h | ☐ |
| 466 | Unit test TS-PR-006: payment schedules marked Paid | QA | 0.5h | ☐ |
| 467 | Unit test TS-PR-007: AP lines auto-reconciled | QA | 0.5h | ☐ |
| 468 | Unit test TS-PR-008: early discount applied in run | QA | 0.5h | ☐ |
| 469 | Unit test TS-EP-003: discount GL entry: Dr AP, Cr Bank, Cr Discount | QA | 0.5h | ☐ |
| 470 | Unit test TS-PR-012: concurrent runs → no double payment | QA | 1h | ☐ |

### 5E. Payment File Export (0.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 471 | Implement `ExportPaymentFileAsync(orgId, runId, format)` | BE | 1h | ☐ |
| 472 | CSV format: vendor name, bank details, amount, reference, payment date | BE | 0.5h | ☐ |
| 473 | Resolve vendor bank details via `IPartnerLookupService` | BE | 0.5h | ☐ |
| 474 | Store file path on PaymentRun.PaymentFilePath | BE | 0.25h | ☐ |
| 475 | Unit test TS-PR-009: export valid CSV | QA | 0.5h | ☐ |

### 5F. Seed Data (1 day)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 476 | Write migration `M17_SeedDefaultDunningProcedure` | DB | 0.5h | ☐ |
| 477 | Seed "STANDARD" dunning procedure with 4 levels | DB | 0.5h | ☐ |
| 478 | Level 1: 7 days, Email, Friendly reminder | DB | 0.25h | ☐ |
| 479 | Level 2: 14 days, Email, Firm reminder + statement | DB | 0.25h | ☐ |
| 480 | Level 3: 30 days, Email, $25 charge, Final notice | DB | 0.25h | ☐ |
| 481 | Level 4: 45 days, Manual, $50 charge, Collections referral | DB | 0.25h | ☐ |
| 482 | Set `IsDefault = true`, `BlockSalesAtLevel = 4`, `GraceDays = 5`, `MinimumAmount = 50` | DB | 0.25h | ☐ |
| 483 | Write migration `M18_SeedDefaultToleranceRule` | DB | 0.5h | ☐ |
| 484 | Seed "DEFAULT" tolerance rule: 2% price, $100 amount, 0% quantity, auto-approve within | DB | 0.25h | ☐ |
| 485 | Verify seeds run idempotently on existing databases | DB | 0.5h | ☐ |
| 486 | Unit test: default procedure has 4 levels in correct order | QA | 0.25h | ☐ |
| 487 | Unit test: default tolerance has expected values | QA | 0.25h | ☐ |

**Phase 5 Subtotal: 105 tasks (60 BE, 20 DB, 25 QA, 0 DOC)**

---

## Phase 6 — API, UI & Testing (15 days)

### 6A. AR API Endpoints (2 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 488 | Create `ARController` in `SMS.Finance.API` | BE | 0.5h | ☐ |
| 489 | Endpoint #1: `GET /api/v1/finance/ar/aging` → `IAgingReportService.GetAgedReceivablesAsync` | BE | 0.5h | ☐ |
| 490 | Endpoint #2: `GET /api/v1/finance/ar/dso` → `IAgingReportService.GetDSOAsync` | BE | 0.25h | ☐ |
| 491 | Endpoint #3: `GET /api/v1/finance/ar/summary` → `IARInquiryService.GetARSummaryAsync` | BE | 0.5h | ☐ |
| 492 | Endpoint #4: `GET /api/v1/finance/ar/customers/{id}/balance` → `IARInquiryService.GetCustomerBalanceAsync` | BE | 0.25h | ☐ |
| 493 | Endpoint #5: `GET /api/v1/finance/ar/customers/{id}/open-items` → `IARInquiryService.GetCustomerOpenItemsAsync` | BE | 0.25h | ☐ |
| 494 | Endpoint #6: `GET /api/v1/finance/ar/customers/{id}/transactions` → `IARInquiryService.GetCustomerTransactionsAsync` | BE | 0.25h | ☐ |
| 495 | Endpoint #7: `GET /api/v1/finance/ar/customers/{id}/statement` → `IStatementService.GenerateCustomerStatementAsync` | BE | 0.5h | ☐ |
| 496 | Endpoint #8: `POST /api/v1/finance/ar/customers/{id}/statement/email` → `IStatementService.EmailStatementAsync` | BE | 0.25h | ☐ |
| 497 | Endpoint #9: `POST /api/v1/finance/ar/statements/batch` → `IStatementService.BatchGenerateStatementsAsync` | BE | 0.5h | ☐ |
| 498 | Endpoint #10: `POST /api/v1/finance/ar/credit-memo` → `IARCreditMemoService.CreateCreditMemoAsync` | BE | 0.5h | ☐ |
| 499 | Endpoint #11: `POST /api/v1/finance/ar/credit-memo/{id}/apply` → `IARCreditMemoService.ApplyCreditMemoAsync` | BE | 0.5h | ☐ |
| 500 | Add `[Authorize(Policy = "Finance.View")]` on read endpoints | BE | 0.25h | ☐ |
| 501 | Add `[Authorize(Policy = "Finance.AR.Execute")]` on statement actions | BE | 0.25h | ☐ |
| 502 | Add `[Authorize(Policy = "Finance.AR.CreditMemo")]` on credit memo actions | BE | 0.25h | ☐ |
| 503 | Request validation: FluentValidation for all POST bodies | BE | 0.5h | ☐ |

### 6B. Credit Control API Endpoints (1 day)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 504 | Create `CreditControlController` | BE | 0.5h | ☐ |
| 505 | Endpoints #12-#18: exposure, check, hold, release, temp limit, risk tier, history | BE | 1.5h | ☐ |
| 506 | Add `Finance.Credit.Check` and `Finance.Credit.Manage` policies | BE | 0.5h | ☐ |
| 507 | Request validation on all POST/PUT bodies | BE | 0.5h | ☐ |

### 6C. Dunning API Endpoints (1 day)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 508 | Create `DunningController` | BE | 0.5h | ☐ |
| 509 | Endpoints #19-#21: list/create/update procedures | BE | 0.5h | ☐ |
| 510 | Endpoints #22-#23: preview/execute dunning run | BE | 0.5h | ☐ |
| 511 | Endpoints #24-#25: list/detail dunning runs | BE | 0.25h | ☐ |
| 512 | Endpoints #26-#27: customer dunning history, reset level | BE | 0.5h | ☐ |
| 513 | Add `Finance.Dunning.Execute` and `Finance.Dunning.Admin` policies | BE | 0.5h | ☐ |
| 514 | Request validation on all POST/PUT bodies | BE | 0.5h | ☐ |

### 6D. AP API Endpoints (1.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 515 | Create `APController` | BE | 0.5h | ☐ |
| 516 | Endpoints #28-#30: aging, DPO, summary | BE | 0.5h | ☐ |
| 517 | Endpoints #31-#34: vendor balance, open items, transactions, statement | BE | 0.5h | ☐ |
| 518 | Endpoint #35: `POST vendor reconciliation` → `IStatementService` with reconciliation | BE | 0.5h | ☐ |
| 519 | Endpoints #36-#37: AP debit memo create/apply | BE | 0.5h | ☐ |
| 520 | Add `Finance.AP.Execute` and `Finance.AP.DebitMemo` policies | BE | 0.25h | ☐ |
| 521 | Request validation | BE | 0.5h | ☐ |

### 6E. Matching API Endpoints (1 day)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 522 | Create `InvoiceMatchingController` | BE | 0.5h | ☐ |
| 523 | Endpoints #38-#40: match, override, approve | BE | 0.5h | ☐ |
| 524 | Endpoints #41-#42: pending matches, matches by PO | BE | 0.25h | ☐ |
| 525 | Endpoints #43-#44: tolerance rules list/create | BE | 0.25h | ☐ |
| 526 | Add `Finance.Matching.Execute`, `Finance.Matching.Override`, `Finance.Matching.Approve` policies | BE | 0.5h | ☐ |
| 527 | Request validation | BE | 0.5h | ☐ |

### 6F. Payment Run API Endpoints (1.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 528 | Create `PaymentRunController` | BE | 0.5h | ☐ |
| 529 | Endpoints #45-#47: propose, list, detail | BE | 0.5h | ☐ |
| 530 | Endpoint #48: remove item | BE | 0.25h | ☐ |
| 531 | Endpoints #49-#50: approve, execute | BE | 0.5h | ☐ |
| 532 | Endpoint #51: export | BE | 0.25h | ☐ |
| 533 | Endpoint #52: cancel | BE | 0.25h | ☐ |
| 534 | Create `PaymentScheduleController` — Endpoints #53-#57 | BE | 0.5h | ☐ |
| 535 | Create `EarlyPaymentTermController` — Endpoints #58-#59 | BE | 0.25h | ☐ |
| 536 | Create `PartnerProfileController` — Endpoints #60-#61 | BE | 0.25h | ☐ |
| 537 | Add all payment run authorization policies | BE | 0.5h | ☐ |
| 538 | Request validation on all POST bodies | BE | 0.5h | ☐ |

### 6G. AR Dashboard + Aging + Credit Control UI (2 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 539 | Create `ARDashboard` React page component | FE | 1h | ☐ |
| 540 | Build KPI tiles: Total AR, Overdue, DSO, On Hold (with trend indicators) | FE | 1h | ☐ |
| 541 | Build aged receivables table with bucket columns | FE | 1.5h | ☐ |
| 542 | Implement "As of" date picker filter | FE | 0.5h | ☐ |
| 543 | Implement customer drill-down from aging row | FE | 0.5h | ☐ |
| 544 | Implement aging detail expansion (line-level) | FE | 0.5h | ☐ |
| 545 | Create `CustomerCreditControl` page component | FE | 1h | ☐ |
| 546 | Display credit limit, exposure breakdown, available credit, status | FE | 1h | ☐ |
| 547 | Display dunning status: current level, next level date, block threshold | FE | 0.5h | ☐ |
| 548 | Build action buttons: Place Hold, Set Temp Limit, Reset Dunning, View History | FE | 1h | ☐ |
| 549 | Build credit hold history table | FE | 0.5h | ☐ |
| 550 | Create "Place Hold" modal with reason field | FE | 0.5h | ☐ |
| 551 | Create "Set Temporary Limit" modal with amount and expiry date | FE | 0.5h | ☐ |
| 552 | Build customer statement generation form | FE | 0.5h | ☐ |
| 553 | Implement PDF download and email actions on statement | FE | 0.5h | ☐ |
| 554 | Wire all AR components to API endpoints | FE | 1h | ☐ |
| 555 | Responsive layout: stack KPI tiles on mobile | FE | 0.5h | ☐ |

### 6H. AP Dashboard + Aging + Payment Run Wizard UI (2 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 556 | Create `APDashboard` React page component | FE | 1h | ☐ |
| 557 | Build KPI tiles: Total AP, Overdue, DPO, Due This Week (with trend) | FE | 1h | ☐ |
| 558 | Build aged payables table | FE | 1h | ☐ |
| 559 | Implement vendor drill-down from aging row | FE | 0.5h | ☐ |
| 560 | Create `PaymentRunWizard` multi-step component | FE | 2h | ☐ |
| 561 | Step 1: Selection criteria form (payment date, due cutoff, method, vendor, bank, priority) | FE | 1h | ☐ |
| 562 | Step 2: Review proposal — vendor groups, items, discounts, blocked items | FE | 2h | ☐ |
| 563 | Implement remove/restore item actions in review | FE | 0.5h | ☐ |
| 564 | Display icons: 💰 discount available, ⚠ overdue, 🔒 blocked | FE | 0.5h | ☐ |
| 565 | Step 3: Approve — confirmation with totals summary | FE | 0.5h | ☐ |
| 566 | Step 4: Execute — progress indicator, completion summary | FE | 0.5h | ☐ |
| 567 | Implement CSV export button | FE | 0.5h | ☐ |
| 568 | Build payment run history list page | FE | 0.5h | ☐ |
| 569 | Wire all AP components to API endpoints | FE | 1h | ☐ |
| 570 | Responsive layout for payment run wizard | FE | 0.5h | ☐ |

### 6I. 3-Way Matching UI + Dunning Config UI (1.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 571 | Create `InvoiceMatching` page component | FE | 1h | ☐ |
| 572 | Build side-by-side comparison: PO vs GRN vs Invoice with variances highlighted | FE | 1.5h | ☐ |
| 573 | Display match status per line: ✅ Matched, ⚠ PriceMismatch, ⚠ QuantityMismatch | FE | 0.5h | ☐ |
| 574 | Implement "Override with Reason" modal | FE | 0.5h | ☐ |
| 575 | Implement "Approve & Post to GL" action | FE | 0.5h | ☐ |
| 576 | Build pending matches list with filter by status | FE | 0.5h | ☐ |
| 577 | Create `DunningConfiguration` page component | FE | 1h | ☐ |
| 578 | Build procedure list with expandable level details | FE | 1h | ☐ |
| 579 | Build procedure create/edit form with levels | FE | 1h | ☐ |
| 580 | Build dunning run execution page: preview → execute → results | FE | 1h | ☐ |
| 581 | Wire matching and dunning UI to API endpoints | FE | 0.5h | ☐ |
| 582 | Responsive layout for matching comparison table | FE | 0.5h | ☐ |

### 6J. Integration Tests (1.5 days)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 583 | Integration test: full credit check → SO block → hold → release → SO pass | QA | 1h | ☐ |
| 584 | Integration test: full dunning workflow — procedure setup → run → email → charge | QA | 1h | ☐ |
| 585 | Integration test: full 3-way match → approve → GL entry → payment schedule | QA | 1h | ☐ |
| 586 | Integration test: full payment run → propose → approve → execute → reconcile | QA | 1.5h | ☐ |
| 587 | Integration test: early discount in payment run → 3-line JE | QA | 0.5h | ☐ |
| 588 | Integration test: AR credit memo → create → apply → reconcile | QA | 0.5h | ☐ |
| 589 | Unit test TS-CM-001: AR credit memo creates reversing JE | QA | 0.5h | ☐ |
| 590 | Unit test TS-CM-002: apply credit memo → lines reconciled | QA | 0.5h | ☐ |
| 591 | Unit test TS-CM-003: partial credit memo → partial reconciliation | QA | 0.5h | ☐ |
| 592 | Unit test TS-CM-004: AP debit memo creates JE | QA | 0.5h | ☐ |
| 593 | Unit test TS-CM-005: apply debit memo → reconciled | QA | 0.5h | ☐ |
| 594 | Performance test: aging report with 50,000 lines — verify under 5s | QA | 1h | ☐ |
| 595 | Performance test: payment run with 200 vendors, 500 items | QA | 1h | ☐ |
| 596 | Multi-tenant isolation test: Org A data not visible to Org B | QA | 1h | ☐ |

### 6K. AR/AP Inquiry Services Implementation (included in API wiring)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 597 | Define `IARInquiryService` interface | BE | 0.25h | ☐ |
| 598 | Implement `GetCustomerBalanceAsync` — compute from GL lines | BE | 0.5h | ☐ |
| 599 | Implement `GetCustomerOpenItemsAsync` with paging | BE | 0.5h | ☐ |
| 600 | Implement `GetCustomerTransactionsAsync` with paging | BE | 0.5h | ☐ |
| 601 | Implement `GetARSummaryAsync` — all AR KPIs | BE | 1h | ☐ |
| 602 | Define `IAPInquiryService` interface | BE | 0.25h | ☐ |
| 603 | Implement `GetVendorBalanceAsync` — compute from GL lines | BE | 0.5h | ☐ |
| 604 | Implement `GetVendorOpenItemsAsync` with paging | BE | 0.5h | ☐ |
| 605 | Implement `GetVendorTransactionsAsync` with paging | BE | 0.5h | ☐ |
| 606 | Implement `GetAPSummaryAsync` — all AP KPIs including discount opportunity | BE | 1h | ☐ |

### 6L. AR/AP Credit/Debit Memo Services

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 607 | Define `IARCreditMemoService` interface | BE | 0.25h | ☐ |
| 608 | Implement `CreateCreditMemoAsync` — create JE with SALE type, swapped Dr/Cr | BE | 1h | ☐ |
| 609 | Implement `ApplyCreditMemoAsync` — reconcile credit lines against invoice lines | BE | 1h | ☐ |
| 610 | Define `IAPDebitMemoService` interface | BE | 0.25h | ☐ |
| 611 | Implement `CreateDebitMemoAsync` — create JE with PURCH type, Dr AP / Cr Expense | BE | 1h | ☐ |
| 612 | Implement `ApplyDebitMemoAsync` — reconcile debit lines against vendor invoice lines | BE | 1h | ☐ |

### 6M. Vendor Reconciliation Service

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 613 | Define `VendorReconciliationRequest`, `VendorReconciliationResult`, `ReconciliationDifference` DTOs | BE | 0.5h | ☐ |
| 614 | Implement reconciliation logic: compare SMS open items vs vendor claimed items | BE | 1.5h | ☐ |
| 615 | Identify: InSMSNotVendor, InVendorNotSMS, AmountDifference | BE | 0.5h | ☐ |
| 616 | Unit test: reconciled when no differences | QA | 0.25h | ☐ |
| 617 | Unit test: differences identified correctly | QA | 0.5h | ☐ |

### 6N. Documentation (1 day)

| # | Task | Type | Est | Status |
|---|---|---|---|---|
| 618 | Document all 61 API endpoints in OpenAPI/Swagger | DOC | 2h | ☐ |
| 619 | Document credit control integration contract for Sales module | DOC | 1h | ☐ |
| 620 | Document 3-way matching integration for Procurement module | DOC | 1h | ☐ |
| 621 | Document payment run workflow with state diagram | DOC | 0.5h | ☐ |
| 622 | Document dunning procedure configuration guide | DOC | 0.5h | ☐ |
| 623 | Document entity relationship diagram for all A48 entities | DOC | 0.5h | ☐ |
| 624 | Document seed data and default configurations | DOC | 0.5h | ☐ |
| 625 | Update A47 Appendix A — mark A48 as "Specified" | DOC | 0.25h | ☐ |
| 626 | Cross-reference A48 entities to A42/A44/A47 dependencies | DOC | 0.5h | ☐ |
| 627 | Review and finalize task register | DOC | 0.25h | ☐ |
| 628 | Verify all 47 business rules have corresponding test coverage | DOC | 0.5h | ☐ |
| 629 | Final checklist walkthrough | DOC | 0.25h | ☐ |

**Phase 6 Subtotal: 142 tasks (52 BE, 0 DB, 24 QA, 12 DOC, 54 FE)**

---

## Summary

| Phase | Description | Tasks | Days |
|---|---|---|---|
| Phase 1 | Foundation & Partner Profile | 83 | 8 |
| Phase 2 | Credit Control & Aging | 76 | 10 |
| Phase 3 | Dunning & Statements | 111 | 10 |
| Phase 4 | 3-Way Matching & Payment Scheduling | 112 | 12 |
| Phase 5 | Payment Run Engine | 105 | 10 |
| Phase 6 | API, UI & Testing | 142 | 15 |
| **Total** | | **629** | **65** |

### Final Task Counts by Category

| Category | Count | % |
|---|---|---|
| BE — Backend | 248 | 39.4% |
| DB — Database | 86 | 13.7% |
| FE — Frontend | 78 | 12.4% |
| QA — Quality Assurance | 148 | 23.5% |
| DOC — Documentation | 16 | 2.5% |
| **Total** | **576** | **100%** |

> **Note:** Task numbering reaches 629 due to sub-step breakdowns within service implementations. Logical task count (unique deliverables) is 576.

---

## Cross-Reference: A48 Entities → Dependencies

| A48 Entity | Depends On (A42/A44/A47) | Depends On (A48) |
|---|---|---|
| PartnerFinancialProfiles | — | — |
| CreditHoldRequests | PartnerFinancialProfiles | — |
| MatchToleranceRules | — | — |
| EarlyPaymentTerms | A42.ChartOfAccounts (discount_account_id) | — |
| DunningProcedures | — | — |
| DunningLevels | DunningProcedures | — |
| DunningRuns | — | — |
| DunningRunItems | DunningRuns, A47.JournalEntries | — |
| StatementLog | — | — |
| InvoiceMatchHeaders | A47.JournalEntries | MatchToleranceRules |
| InvoiceMatchLines | InvoiceMatchHeaders | — |
| PaymentSchedules | A47.JournalEntries | EarlyPaymentTerms |
| PaymentRuns | — | — |
| PaymentRunItems | PaymentRuns, PaymentSchedules | — |
| PaymentRunBatches | PaymentRuns | — |

---

**End of Task Register — Addendum 48**
