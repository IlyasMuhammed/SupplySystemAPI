# SMS — Functional Specification Document

## Addendum 48: Accounts Receivable & Accounts Payable Sub-Ledgers

| Property | Value |
|---|---|
| Addendum | 48 |
| Title | Accounts Receivable & Accounts Payable Sub-Ledgers |
| Status | Draft — Pending Approval |
| Author | Muhammad Ilyas |
| Date | 2026-10-25 |
| Depends On | A42 (Chart of Accounts), A44 (Financial Posting Gateway), A47 (GL Journal Engine) |
| Schema | `finance` (all entities within existing FinanceDbContext) |
| Design Heritage | SAP S/4HANA FI-AR/FI-AP rigor + Odoo 17 simplicity |

---

## Table of Contents

1. [Purpose & Scope](#1-purpose--scope)
2. [Background & Context](#2-background--context)
3. [Design Principles](#3-design-principles)
4. [Part A — Accounts Receivable Sub-Ledger](#4-part-a--accounts-receivable-sub-ledger)
   - C1: Partner Financial Profile
   - C2: Credit Limit & Exposure Engine
   - C3: Aged Receivables Report
   - C4: Customer Statement of Account
   - C5: Dunning / Payment Follow-Up Engine
   - C6: AR Open Items & Balance Inquiry
   - C7: AR Credit Memo Workflow
5. [Part B — Accounts Payable Sub-Ledger](#5-part-b--accounts-payable-sub-ledger)
   - C8: Aged Payables Report
   - C9: 3-Way Matching Engine (PO ↔ GRN ↔ Invoice)
   - C10: Payment Scheduling & Due Date Management
   - C11: Automatic Payment Run Engine
   - C12: AP Open Items & Balance Inquiry
   - C13: Vendor Statement & Reconciliation
   - C14: AP Debit Memo Workflow
   - C15: Early Payment Discount Engine
6. [Database Migrations](#6-database-migrations)
7. [API Endpoints](#7-api-endpoints)
8. [UI Wireframes](#8-ui-wireframes)
9. [Business Rules](#9-business-rules)
10. [Test Scenarios](#10-test-scenarios)
11. [Development Phases](#11-development-phases)
12. [Appendix A — Deferred Roadmap](#12-appendix-a--deferred-roadmap)

---

## 1. Purpose & Scope

### 1.1 Architectural Position

```
┌─────────────────────────────────────────────────────────────────────┐
│                         SMS Module Boundary                        │
│                                                                    │
│  ┌────────────┐  ┌──────────────┐  ┌────────────┐  ┌───────────┐  │
│  │   Sales    │  │ Procurement  │  │  Inventory  │  │    POS    │  │
│  │  (sales.)  │  │  (procure.)  │  │   (inv.)    │  │  (pos.)   │  │
│  └─────┬──────┘  └──────┬───────┘  └──────┬──────┘  └─────┬─────┘  │
│        │                │                 │               │        │
│        ▼                ▼                 ▼               ▼        │
│  ┌─────────────────────────────────────────────────────────────┐   │
│  │          A44 — Financial Posting Gateway                    │   │
│  │         IFinancialPostingGateway.PostAsync()                │   │
│  └────────────────────────┬────────────────────────────────────┘   │
│                           │                                        │
│                           ▼                                        │
│  ┌─────────────────────────────────────────────────────────────┐   │
│  │          A47 — GL Journal Engine (finance.)                 │   │
│  │    JournalEntries + JournalEntryLines + Reconciliation      │   │
│  └────────────────────────┬────────────────────────────────────┘   │
│                           │                                        │
│                           ▼                                        │
│  ┌─────────────────────────────────────────────────────────────┐   │
│  │    ★ A48 — AR & AP Sub-Ledgers (finance.) ← THIS ADDENDUM  │   │
│  │                                                             │   │
│  │  ┌──────────────────────┐  ┌──────────────────────────┐     │   │
│  │  │  AR Sub-Ledger       │  │  AP Sub-Ledger           │     │   │
│  │  │  • Credit Control    │  │  • 3-Way Matching        │     │   │
│  │  │  • Customer Aging    │  │  • Payment Run           │     │   │
│  │  │  • Statements        │  │  • Vendor Aging          │     │   │
│  │  │  • Dunning           │  │  • Payment Scheduling    │     │   │
│  │  │  • Open Items        │  │  • Early Pay Discount    │     │   │
│  │  └──────────────────────┘  └──────────────────────────────┘  │   │
│  └─────────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────────┘
```

### 1.2 Separation Guarantee

Consistent with A47, all AR/AP entities and services:

- Reside within `finance` schema and `FinanceDbContext`
- Reference customers/vendors via **soft-link** `partner_id` (plain int, no cross-schema FK)
- Reference sales/purchase documents via **soft-link** `source_document_type` + `source_document_id` + `source_module`
- No cross-schema JOINs — partner names resolved via application-level lookup service
- No cross-schema foreign keys

### 1.3 Scope Summary

| # | Change Item | Type | Entities |
|---|---|---|---|
| C1 | Partner Financial Profile | New Entity | PartnerFinancialProfiles |
| C2 | Credit Limit & Exposure Engine | New Service + Entity | CreditLimits, CreditHoldRequests |
| C3 | Aged Receivables Report | New Service | (computed from JournalEntryLines) |
| C4 | Customer Statement of Account | New Service | CustomerStatements |
| C5 | Dunning / Payment Follow-Up Engine | New Entities + Service | DunningProcedures, DunningLevels, DunningRuns, DunningRunItems |
| C6 | AR Open Items & Balance Inquiry | New Service | (computed from JournalEntryLines) |
| C7 | AR Credit Memo Workflow | New Service | (creates JournalEntries via A47) |
| C8 | Aged Payables Report | New Service | (computed from JournalEntryLines) |
| C9 | 3-Way Matching Engine | New Entities + Service | InvoiceMatchHeaders, InvoiceMatchLines |
| C10 | Payment Scheduling & Due Date Management | New Entity | PaymentSchedules |
| C11 | Automatic Payment Run Engine | New Entities + Service | PaymentRuns, PaymentRunItems, PaymentRunBatches |
| C12 | AP Open Items & Balance Inquiry | New Service | (computed from JournalEntryLines) |
| C13 | Vendor Statement & Reconciliation | New Service | VendorStatements |
| C14 | AP Debit Memo Workflow | New Service | (creates JournalEntries via A47) |
| C15 | Early Payment Discount Engine | New Entity + Service | EarlyPaymentTerms |

**New Entities: 14** | **New Services: 12** | **Modified Entities: 1** (PartnerFinancialProfiles added)

---

## 2. Background & Context

### 2.1 Current State (Post-A47)

The GL Journal Engine (A47) provides the data foundation:

| A47 Component | AR/AP Usage |
|---|---|
| `JournalEntryLines.partner_id` + `partner_type` | Links every GL line to a Customer or Vendor |
| `JournalEntryLines.amount_residual` | Tracks how much of each line remains unpaid |
| `IReconciliationService` | Matches invoices ↔ payments at line level |
| `ReconcileGroups` | Groups matched lines together |
| SALE / PURCH journal types | Classifies entries as AR or AP |
| `ITrialBalanceService.GetAccountLedgerAsync` | Ledger drill-down per account |

**What's missing:** Business intelligence and automation — aging analysis, credit control, dunning, payment runs, 3-way matching, statements.

### 2.2 Design Heritage — SAP vs Odoo Feature Mapping

| Domain | SAP S/4HANA Approach | Odoo 17 Approach | SMS A48 Approach |
|---|---|---|---|
| **Aging** | Fiori analytical apps with DSO/DBT KPIs | Simple built-in report with 4 buckets | Configurable buckets + DSO/DPO KPIs |
| **Credit Limit** | Multi-segment (company code, credit control area), credit exposure = orders + deliveries + invoices | Single field per partner, block at SO confirm | Single limit per partner per org, exposure = open SO + open invoices (no delivery tracking in SMS) |
| **Credit Hold** | Full workflow with authorized users, documented decisions, temporary increases | Simple block/unblock | Workflow with request → approve/reject + temporary increases with expiry |
| **Dunning** | Multi-level procedure (F150), dunning charges, block at level | Follow-up levels with email templates, automated schedule | Multi-level with configurable templates, charges optional, sales block at threshold level |
| **Customer Statement** | Correspondence F.27 with multiple variants | Built-in with portal access | PDF generation + email, open-items and full-activity variants |
| **3-Way Matching** | MIRO — separate logistics invoice verification | Auto-fill from PO, manual verification | Automated PO ↔ GRN ↔ Vendor Invoice matching with tolerance |
| **Payment Run** | F110 — full automatic payment program with bank selection, payment methods, house bank | Batch payments with bank file export | Payment proposal → review → approve → execute, bank journal selection, payment file CSV export |
| **Early Discount** | Cash discount terms auto-applied | Payment terms with discount | Configurable terms per vendor, auto-detect in payment run |
| **Vendor Statement** | Correspondence, reconciliation statement | Community modules only | PDF generation, open-items reconciliation format |

---

## 3. Design Principles

| # | Principle | Description |
|---|---|---|
| DP-1 | **Sub-Ledger Is a View, Not a Copy** | AR/AP balances are computed from GL lines filtered by `partner_type` + receivable/payable account — no separate balance table that can desynchronize |
| DP-2 | **Partner Soft-Link Only** | All partner references are plain `int` with no cross-schema FK; partner name/details resolved via `IPartnerLookupService` |
| DP-3 | **Document Soft-Link Only** | Source documents (SO, PO, Invoice) referenced by type + id + module string — no cross-schema FK |
| DP-4 | **All Within FinanceDbContext** | Every new entity registered in `FinanceDbContext`, `finance` schema only |
| DP-5 | **Credit Exposure Is Real-Time** | Credit exposure computed on demand from open items, never cached in a table that can drift |
| DP-6 | **Aging Is Computed, Not Stored** | Aging buckets computed from `entry_date` and current date at query time |
| DP-7 | **Payment Run Is Auditable** | Every payment run creates a full audit trail: proposal → approval → execution → journal entries |
| DP-8 | **3-Way Match Before Posting** | Vendor invoices validated against PO and GRN quantities/amounts before GL posting; tolerances configurable |
| DP-9 | **Dunning History Is Permanent** | Every dunning action recorded; dunning level on partner profile only moves forward unless manually reset |
| DP-10 | **Early Discount Is GL-Aware** | Discount taken posts to a separate discount GL account, not as a write-off to the expense account |

---

## 4. Part A — Accounts Receivable Sub-Ledger

### C1: Partner Financial Profile

A per-partner, per-org financial profile that centralizes AR/AP behavior settings. Soft-linked to the partner by `partner_id` + `partner_type`.

#### Entity: PartnerFinancialProfiles

```sql
CREATE TABLE finance.PartnerFinancialProfiles (
    id                      INT IDENTITY(1,1) PRIMARY KEY,
    org_id                  INT NOT NULL,
    partner_id              INT NOT NULL,           -- soft-link, no FK
    partner_type            NVARCHAR(10) NOT NULL,  -- 'Customer' | 'Vendor'

    -- Credit settings (AR)
    credit_limit            DECIMAL(18,2) NULL,     -- NULL = no limit
    credit_limit_currency_id INT NULL,
    credit_hold             BIT NOT NULL DEFAULT 0,
    credit_hold_reason      NVARCHAR(500) NULL,
    credit_hold_date        DATETIME2 NULL,
    credit_hold_by          NVARCHAR(100) NULL,
    temporary_credit_limit  DECIMAL(18,2) NULL,
    temporary_credit_expiry DATETIME2 NULL,
    credit_risk_tier        TINYINT NOT NULL DEFAULT 1, -- 1=Low, 2=Medium, 3=High

    -- Payment behavior (AR)
    dunning_procedure_id    INT NULL,               -- FK to DunningProcedures
    current_dunning_level   TINYINT NOT NULL DEFAULT 0,
    last_dunning_date       DATETIME2 NULL,
    block_sales_at_dunning  BIT NOT NULL DEFAULT 0,

    -- Payment terms defaults
    default_payment_term_days INT NULL,              -- e.g., 30
    default_payment_method  NVARCHAR(50) NULL,       -- 'BankTransfer','Check','Cash'

    -- AP settings
    payment_priority        TINYINT NOT NULL DEFAULT 2, -- 1=High, 2=Normal, 3=Low
    preferred_payment_method NVARCHAR(50) NULL,
    withholding_tax_applicable BIT NOT NULL DEFAULT 0,

    -- Statistics (updated by background jobs, not authoritative — queries compute real-time)
    last_payment_date       DATETIME2 NULL,
    average_days_to_pay     INT NULL,
    total_overdue_amount    DECIMAL(18,2) NULL,

    -- Audit
    created_at              DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    created_by              NVARCHAR(100) NOT NULL,
    modified_at             DATETIME2 NULL,
    modified_by             NVARCHAR(100) NULL,

    CONSTRAINT UQ_PFP_OrgPartner UNIQUE (org_id, partner_id, partner_type),
    CONSTRAINT CK_PFP_PartnerType CHECK (partner_type IN ('Customer', 'Vendor')),
    CONSTRAINT CK_PFP_RiskTier CHECK (credit_risk_tier BETWEEN 1 AND 3),
    CONSTRAINT CK_PFP_Priority CHECK (payment_priority BETWEEN 1 AND 3)
);

CREATE INDEX IX_PFP_OrgPartnerType ON finance.PartnerFinancialProfiles
    (org_id, partner_type) INCLUDE (credit_limit, credit_hold);
CREATE INDEX IX_PFP_CreditHold ON finance.PartnerFinancialProfiles
    (org_id) WHERE credit_hold = 1;
CREATE INDEX IX_PFP_DunningProcedure ON finance.PartnerFinancialProfiles
    (dunning_procedure_id) WHERE dunning_procedure_id IS NOT NULL;
```

#### Service: IPartnerLookupService

Resolves partner names and details from `sales.Customers` / `procurement.Vendors` without cross-schema FK. All AR/AP services use this for display.

```csharp
public interface IPartnerLookupService
{
    Task<PartnerInfo?> GetPartnerAsync(int orgId, int partnerId, string partnerType);
    Task<Dictionary<int, PartnerInfo>> GetPartnersAsync(int orgId, IEnumerable<int> partnerIds, string partnerType);
}

public record PartnerInfo(
    int PartnerId,
    string PartnerType,
    string Name,
    string Code,
    string? Email,
    string? Phone,
    string? Address
);
```

> **Implementation Note:** Uses `IServiceProvider` to resolve the appropriate repository (SalesDbContext for Customers, ProcurementDbContext for Vendors) — the lookup crosses schema boundaries at the application layer, never at the database layer.

---

### C2: Credit Limit & Exposure Engine

**Design heritage:** SAP's real-time credit exposure concept + Odoo's simplicity of single-limit enforcement.

#### Credit Exposure Calculation

Credit exposure for a customer is computed in real-time (DP-5), never cached:

```
Credit Exposure = Open Sales Order Value (undelivered)
                + Open Invoice Value (unpaid GL lines where partner_type = 'Customer'
                    AND account is receivable AND reconciled = false)
```

```csharp
public interface ICreditControlService
{
    /// <summary>Compute real-time credit exposure for a customer</summary>
    Task<CreditExposureResult> GetCreditExposureAsync(int orgId, int customerId);

    /// <summary>Check if a transaction can proceed (called by Sales module before SO confirmation)</summary>
    Task<CreditCheckResult> CheckCreditAsync(int orgId, int customerId, decimal transactionAmount);

    /// <summary>Place customer on credit hold</summary>
    Task<Result> PlaceCreditHoldAsync(int orgId, int customerId, string reason, string heldBy);

    /// <summary>Release credit hold</summary>
    Task<Result> ReleaseCreditHoldAsync(int orgId, int customerId, string releasedBy);

    /// <summary>Set temporary credit limit increase with expiry</summary>
    Task<Result> SetTemporaryCreditLimitAsync(int orgId, int customerId, decimal amount, DateTime expiryDate, string setBy);

    /// <summary>Update credit risk tier</summary>
    Task<Result> UpdateCreditRiskTierAsync(int orgId, int customerId, CreditRiskTier tier, string updatedBy);
}
```

#### DTOs

```csharp
public record CreditExposureResult(
    int CustomerId,
    string CustomerName,
    decimal CreditLimit,
    decimal? TemporaryCreditLimit,
    DateTime? TemporaryCreditExpiry,
    decimal OpenOrdersAmount,
    decimal OpenInvoicesAmount,
    decimal TotalExposure,           // OpenOrders + OpenInvoices
    decimal AvailableCredit,         // EffectiveLimit - TotalExposure
    bool IsOverLimit,
    bool IsOnHold,
    CreditRiskTier RiskTier
);

public record CreditCheckResult(
    bool Approved,
    string? BlockReason,             // null if approved
    decimal CreditLimit,
    decimal CurrentExposure,
    decimal RequestedAmount,
    decimal ProjectedExposure,       // Current + Requested
    decimal AvailableCredit
);

public enum CreditRiskTier { Low = 1, Medium = 2, High = 3 }
```

#### Credit Check Integration Point

The Sales module calls `ICreditControlService.CheckCreditAsync()` at two points:

1. **Sales Order Confirmation** — block if `ProjectedExposure > EffectiveLimit` or `IsOnHold`
2. **Invoice Creation** — warning only (order was already approved)

```
Sales Module                          Finance Module (A48)
─────────                            ─────────────────
SO.Confirm()                          
  │                                   
  ├──► ICreditControlService          
  │    .CheckCreditAsync(orgId,       
  │     customerId, soTotal)          
  │         │                         
  │         ├─ Read PartnerFinancialProfile
  │         ├─ Compute open orders (soft-link query to sales)
  │         ├─ Compute open invoices (GL lines, finance schema)
  │         ├─ Check credit_hold flag
  │         ├─ Check temporary limit + expiry
  │         │                         
  │    ◄────┤ CreditCheckResult       
  │         │  { Approved: false,     
  │         │    BlockReason: "Exceeds limit by 5,000" }
  │                                   
  ├──► Block SO / Allow with override 
```

#### Entity: CreditHoldRequests (Audit Trail)

```sql
CREATE TABLE finance.CreditHoldRequests (
    id              INT IDENTITY(1,1) PRIMARY KEY,
    org_id          INT NOT NULL,
    partner_id      INT NOT NULL,
    action          NVARCHAR(20) NOT NULL,  -- 'Hold','Release','TempIncrease'
    reason          NVARCHAR(500) NOT NULL,
    previous_status NVARCHAR(100) NULL,
    new_status      NVARCHAR(100) NOT NULL,
    amount          DECIMAL(18,2) NULL,     -- for TempIncrease
    expiry_date     DATETIME2 NULL,         -- for TempIncrease
    requested_by    NVARCHAR(100) NOT NULL,
    requested_at    DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    approved_by     NVARCHAR(100) NULL,
    approved_at     DATETIME2 NULL,
    status          NVARCHAR(20) NOT NULL DEFAULT 'Completed', -- 'Pending','Completed','Rejected'

    CONSTRAINT CK_CHR_Action CHECK (action IN ('Hold','Release','TempIncrease'))
);

CREATE INDEX IX_CHR_OrgPartner ON finance.CreditHoldRequests (org_id, partner_id);
```

---

### C3: Aged Receivables Report

**Design heritage:** SAP's Fiori AR Aging Analysis + Odoo's built-in aged receivables report.

Aging is computed at query time from GL lines (DP-6), not stored.

```csharp
public interface IAgingReportService
{
    /// <summary>Generate aged receivables report with configurable buckets</summary>
    Task<AgingReport> GetAgedReceivablesAsync(int orgId, AgingReportRequest request);

    /// <summary>Generate aged payables report with configurable buckets</summary>
    Task<AgingReport> GetAgedPayablesAsync(int orgId, AgingReportRequest request);

    /// <summary>Get DSO (Days Sales Outstanding) for AR</summary>
    Task<decimal> GetDSOAsync(int orgId, DateTime asOfDate, int periodDays = 365);

    /// <summary>Get DPO (Days Payable Outstanding) for AP</summary>
    Task<decimal> GetDPOAsync(int orgId, DateTime asOfDate, int periodDays = 365);
}
```

#### DTOs

```csharp
public record AgingReportRequest(
    DateTime AsOfDate,
    int[]? AgingBuckets = null,          // default: [0, 30, 60, 90, 120]
    int? PartnerId = null,               // filter to single partner
    int? AccountId = null,               // filter to specific receivable/payable account
    bool IncludeDetails = false,         // include line-level breakdown
    string? SortBy = "TotalOverdue",     // Partner, TotalOverdue, OldestBucket
    string? SortDirection = "Desc"
);

public record AgingReport(
    DateTime AsOfDate,
    string ReportType,                   // "Receivable" or "Payable"
    int[] BucketDays,                    // e.g., [0, 30, 60, 90, 120]
    string[] BucketLabels,               // e.g., ["Current","1-30","31-60","61-90","91-120","120+"]
    List<AgingPartnerLine> Lines,
    AgingTotals Totals,
    decimal DSO_or_DPO,
    decimal TotalOutstanding
);

public record AgingPartnerLine(
    int PartnerId,
    string PartnerName,
    string PartnerCode,
    decimal[] BucketAmounts,             // one per bucket + overflow
    decimal TotalOutstanding,
    DateTime? OldestOpenDate,
    int? DaysBeyondTerms,
    List<AgingDetailLine>? Details       // populated when IncludeDetails = true
);

public record AgingDetailLine(
    int JournalEntryId,
    string EntryNumber,
    DateTime EntryDate,
    DateTime DueDate,
    string? Reference,
    decimal OriginalAmount,
    decimal AmountResidual,
    int DaysOverdue,
    string BucketLabel
);

public record AgingTotals(
    decimal[] BucketTotals,
    decimal GrandTotal,
    int TotalPartners,
    int TotalOpenItems
);
```

#### SQL Computation Pattern

```sql
-- Core aging query (parameterized, runs against JournalEntryLines)
SELECT
    jel.partner_id,
    CASE
        WHEN DATEDIFF(DAY, je.entry_date, @AsOfDate) <= 0 THEN 0    -- Current
        WHEN DATEDIFF(DAY, je.entry_date, @AsOfDate) <= 30 THEN 1   -- 1-30
        WHEN DATEDIFF(DAY, je.entry_date, @AsOfDate) <= 60 THEN 2   -- 31-60
        WHEN DATEDIFF(DAY, je.entry_date, @AsOfDate) <= 90 THEN 3   -- 61-90
        WHEN DATEDIFF(DAY, je.entry_date, @AsOfDate) <= 120 THEN 4  -- 91-120
        ELSE 5                                                        -- 120+
    END AS bucket_index,
    SUM(jel.amount_residual) AS bucket_amount
FROM finance.JournalEntryLines jel
INNER JOIN finance.JournalEntries je ON je.id = jel.journal_entry_id
INNER JOIN finance.ChartOfAccounts coa ON coa.id = jel.account_id
WHERE jel.org_id = @OrgId
  AND jel.partner_type = 'Customer'          -- AR; 'Vendor' for AP
  AND jel.reconciled = 0                     -- open items only
  AND je.status = 1                          -- posted only
  AND coa.account_type = 'Receivable'        -- receivable accounts; 'Payable' for AP
  AND je.entry_date <= @AsOfDate
GROUP BY jel.partner_id,
    CASE
        WHEN DATEDIFF(DAY, je.entry_date, @AsOfDate) <= 0 THEN 0
        WHEN DATEDIFF(DAY, je.entry_date, @AsOfDate) <= 30 THEN 1
        WHEN DATEDIFF(DAY, je.entry_date, @AsOfDate) <= 60 THEN 2
        WHEN DATEDIFF(DAY, je.entry_date, @AsOfDate) <= 90 THEN 3
        WHEN DATEDIFF(DAY, je.entry_date, @AsOfDate) <= 120 THEN 4
        ELSE 5
    END;
```

**Performance:** Uses the existing `IX_JEL_OpenItems` index from A47. For large datasets, add a covering index:

```sql
CREATE INDEX IX_JEL_AgingAR ON finance.JournalEntryLines
    (org_id, partner_type, reconciled)
    INCLUDE (partner_id, amount_residual, journal_entry_id, account_id)
    WHERE reconciled = 0;
```

---

### C4: Customer Statement of Account

Two statement variants (matching SAP's correspondence and Odoo's statement/follow-up):

```csharp
public interface IStatementService
{
    /// <summary>Generate customer statement (all activity or open items only)</summary>
    Task<StatementResult> GenerateCustomerStatementAsync(int orgId, CustomerStatementRequest request);

    /// <summary>Generate vendor statement</summary>
    Task<StatementResult> GenerateVendorStatementAsync(int orgId, VendorStatementRequest request);

    /// <summary>Email statement to partner</summary>
    Task<Result> EmailStatementAsync(int orgId, int statementId);

    /// <summary>Batch generate and email statements</summary>
    Task<BatchStatementResult> BatchGenerateStatementsAsync(int orgId, BatchStatementRequest request);
}
```

#### DTOs

```csharp
public record CustomerStatementRequest(
    int CustomerId,
    DateTime FromDate,
    DateTime ToDate,
    StatementType Type = StatementType.Activity,  // Activity = all, OpenItems = unpaid only
    bool IncludeAging = true,
    string OutputFormat = "PDF"                    // PDF, JSON
);

public enum StatementType { Activity, OpenItems }

public record StatementResult(
    int StatementId,
    int PartnerId,
    string PartnerName,
    string PartnerCode,
    DateTime FromDate,
    DateTime ToDate,
    StatementType Type,
    decimal OpeningBalance,
    decimal ClosingBalance,
    decimal TotalDebits,
    decimal TotalCredits,
    List<StatementLine> Lines,
    AgingTotals? Aging,
    string? PdfFilePath,
    DateTime GeneratedAt
);

public record StatementLine(
    DateTime Date,
    string EntryNumber,
    string Reference,
    string Description,
    decimal? Debit,
    decimal? Credit,
    decimal RunningBalance,
    string Status                  // "Open", "Partially Paid", "Paid"
);

public record BatchStatementRequest(
    DateTime FromDate,
    DateTime ToDate,
    StatementType Type,
    int? MinimumBalance = null,    // only partners with balance >= this
    bool SendEmail = false
);
```

#### Entity: CustomerStatements (Audit Log)

```sql
CREATE TABLE finance.StatementLog (
    id              INT IDENTITY(1,1) PRIMARY KEY,
    org_id          INT NOT NULL,
    partner_id      INT NOT NULL,
    partner_type    NVARCHAR(10) NOT NULL,
    statement_type  NVARCHAR(20) NOT NULL,     -- 'Activity','OpenItems'
    from_date       DATE NOT NULL,
    to_date         DATE NOT NULL,
    opening_balance DECIMAL(18,2) NOT NULL,
    closing_balance DECIMAL(18,2) NOT NULL,
    line_count      INT NOT NULL,
    pdf_path        NVARCHAR(500) NULL,
    emailed_at      DATETIME2 NULL,
    emailed_to      NVARCHAR(200) NULL,
    generated_at    DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    generated_by    NVARCHAR(100) NOT NULL
);

CREATE INDEX IX_SL_OrgPartner ON finance.StatementLog (org_id, partner_id, partner_type);
```

---

### C5: Dunning / Payment Follow-Up Engine

**Design heritage:** SAP's multi-level dunning procedure (F150) + Odoo's tiered follow-up with email templates.

#### Entity: DunningProcedures

```sql
CREATE TABLE finance.DunningProcedures (
    id              INT IDENTITY(1,1) PRIMARY KEY,
    org_id          INT NOT NULL,
    code            NVARCHAR(20) NOT NULL,
    name            NVARCHAR(100) NOT NULL,
    description     NVARCHAR(500) NULL,
    is_default      BIT NOT NULL DEFAULT 0,
    is_active       BIT NOT NULL DEFAULT 1,
    grace_days      INT NOT NULL DEFAULT 0,         -- days after due before dunning starts
    minimum_amount  DECIMAL(18,2) NOT NULL DEFAULT 0, -- minimum overdue to trigger dunning
    block_sales_at_level TINYINT NULL,               -- block new sales at this dunning level
    created_at      DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    created_by      NVARCHAR(100) NOT NULL,
    modified_at     DATETIME2 NULL,
    modified_by     NVARCHAR(100) NULL,

    CONSTRAINT UQ_DP_OrgCode UNIQUE (org_id, code)
);
```

#### Entity: DunningLevels

```sql
CREATE TABLE finance.DunningLevels (
    id                      INT IDENTITY(1,1) PRIMARY KEY,
    org_id                  INT NOT NULL,
    dunning_procedure_id    INT NOT NULL,
    level_number            TINYINT NOT NULL,           -- 1, 2, 3, 4...
    days_after_previous     INT NOT NULL,               -- days after previous level (or due date for level 1)
    subject_template        NVARCHAR(200) NOT NULL,
    body_template           NVARCHAR(MAX) NOT NULL,     -- supports {CustomerName}, {Amount}, {DueDate}, {InvoiceList} placeholders
    include_invoice_list    BIT NOT NULL DEFAULT 1,
    include_statement       BIT NOT NULL DEFAULT 0,     -- attach open items statement
    charge_amount           DECIMAL(18,2) NULL,         -- optional dunning charge
    charge_account_id       INT NULL,                   -- GL account for dunning revenue
    action_type             NVARCHAR(20) NOT NULL DEFAULT 'Email', -- 'Email','Letter','Manual'
    escalation_note         NVARCHAR(500) NULL,         -- internal note for collections team

    CONSTRAINT FK_DL_Procedure FOREIGN KEY (dunning_procedure_id) REFERENCES finance.DunningProcedures(id),
    CONSTRAINT UQ_DL_ProcedureLevel UNIQUE (dunning_procedure_id, level_number),
    CONSTRAINT CK_DL_ActionType CHECK (action_type IN ('Email','Letter','Manual'))
);
```

#### Entity: DunningRuns

```sql
CREATE TABLE finance.DunningRuns (
    id              INT IDENTITY(1,1) PRIMARY KEY,
    org_id          INT NOT NULL,
    run_date        DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    status          NVARCHAR(20) NOT NULL DEFAULT 'Draft', -- 'Draft','Processing','Completed','Cancelled'
    total_customers INT NOT NULL DEFAULT 0,
    total_amount    DECIMAL(18,2) NOT NULL DEFAULT 0,
    items_processed INT NOT NULL DEFAULT 0,
    items_sent      INT NOT NULL DEFAULT 0,
    items_skipped   INT NOT NULL DEFAULT 0,
    executed_by     NVARCHAR(100) NOT NULL,
    completed_at    DATETIME2 NULL,

    CONSTRAINT CK_DR_Status CHECK (status IN ('Draft','Processing','Completed','Cancelled'))
);

CREATE INDEX IX_DR_Org ON finance.DunningRuns (org_id, run_date DESC);
```

#### Entity: DunningRunItems

```sql
CREATE TABLE finance.DunningRunItems (
    id                  INT IDENTITY(1,1) PRIMARY KEY,
    org_id              INT NOT NULL,
    dunning_run_id      INT NOT NULL,
    partner_id          INT NOT NULL,
    dunning_level       TINYINT NOT NULL,
    previous_level      TINYINT NOT NULL,
    overdue_amount      DECIMAL(18,2) NOT NULL,
    charge_amount       DECIMAL(18,2) NULL,
    charge_je_id        INT NULL,               -- JE created for dunning charge
    open_item_count     INT NOT NULL,
    oldest_due_date     DATE NOT NULL,
    action_type         NVARCHAR(20) NOT NULL,
    sent_at             DATETIME2 NULL,
    sent_to             NVARCHAR(200) NULL,
    status              NVARCHAR(20) NOT NULL DEFAULT 'Pending', -- 'Pending','Sent','Skipped','Failed'
    skip_reason         NVARCHAR(200) NULL,

    CONSTRAINT FK_DRI_Run FOREIGN KEY (dunning_run_id) REFERENCES finance.DunningRuns(id),
    CONSTRAINT CK_DRI_Status CHECK (status IN ('Pending','Sent','Skipped','Failed'))
);

CREATE INDEX IX_DRI_RunId ON finance.DunningRunItems (dunning_run_id);
CREATE INDEX IX_DRI_Partner ON finance.DunningRunItems (org_id, partner_id);
```

#### Service: IDunningService

```csharp
public interface IDunningService
{
    // Configuration
    Task<DunningProcedure> CreateProcedureAsync(int orgId, CreateDunningProcedureRequest request);
    Task<DunningProcedure> UpdateProcedureAsync(int orgId, int procedureId, UpdateDunningProcedureRequest request);
    Task<List<DunningProcedureDto>> ListProceduresAsync(int orgId);

    // Dunning Run
    Task<DunningRunPreview> PreviewDunningRunAsync(int orgId, DateTime asOfDate);
    Task<DunningRunResult> ExecuteDunningRunAsync(int orgId, int dunningRunId, string executedBy);
    Task<DunningRunDetail> GetDunningRunAsync(int orgId, int runId);
    Task<List<DunningRunSummary>> ListDunningRunsAsync(int orgId, int? limit = 20);

    // Per-Customer
    Task<DunningHistory> GetCustomerDunningHistoryAsync(int orgId, int customerId);
    Task<Result> ResetDunningLevelAsync(int orgId, int customerId, string reason, string resetBy);
}
```

#### Dunning Run Logic

```
PreviewDunningRunAsync(orgId, asOfDate):
  1. Load all customers with open receivables where:
     - amount_residual > 0
     - je.entry_date + payment_terms + grace_days < asOfDate
     - amount > minimum_amount for assigned procedure
  2. For each customer:
     a. Determine assigned DunningProcedure (from PartnerFinancialProfile, or org default)
     b. Read current_dunning_level
     c. Determine next level:
        - If current_level = 0: eligible for Level 1 if days_overdue >= grace_days
        - If current_level = N: eligible for Level N+1 if days since last_dunning_date >= days_after_previous
     d. If eligible, add to preview with proposed level, amount, action
  3. Return DunningRunPreview (list of proposed items for review)

ExecuteDunningRunAsync(orgId, runId, executedBy):
  1. Load draft dunning run with all items
  2. For each item:
     a. Generate dunning notice (email body from template, substitute placeholders)
     b. If charge_amount > 0: create JournalEntry (Dr: Customer receivable, Cr: Dunning revenue)
     c. Send email (or mark for manual action)
     d. Update PartnerFinancialProfile: current_dunning_level, last_dunning_date
     e. If level >= block_sales_at_level: set credit_hold = true, reason = "Dunning Level {N}"
     f. Mark item as Sent/Failed
  3. Update run status = Completed
```

---

### C6: AR Open Items & Balance Inquiry

```csharp
public interface IARInquiryService
{
    /// <summary>Get customer balance as of a date</summary>
    Task<PartnerBalanceResult> GetCustomerBalanceAsync(int orgId, int customerId, DateTime? asOfDate = null);

    /// <summary>Get all open items for a customer</summary>
    Task<PagedResult<OpenItemDto>> GetCustomerOpenItemsAsync(int orgId, CustomerOpenItemsFilter filter);

    /// <summary>Get customer transaction history</summary>
    Task<PagedResult<PartnerTransactionDto>> GetCustomerTransactionsAsync(int orgId, PartnerTransactionFilter filter);

    /// <summary>Get AR summary KPIs</summary>
    Task<ARSummaryKPIs> GetARSummaryAsync(int orgId, DateTime asOfDate);
}
```

#### DTOs

```csharp
public record PartnerBalanceResult(
    int PartnerId,
    string PartnerName,
    decimal TotalReceivable,       // all open debit lines
    decimal TotalCredits,          // all open credit lines (unapplied payments, credit memos)
    decimal NetBalance,
    DateTime AsOfDate,
    int OpenItemCount,
    DateTime? OldestOpenDate,
    int? DaysOldest
);

public record OpenItemDto(
    int JournalEntryLineId,
    int JournalEntryId,
    string EntryNumber,
    DateTime EntryDate,
    DateTime? DueDate,
    string? Reference,
    string Description,
    string JournalType,
    decimal OriginalAmount,
    decimal AmountResidual,
    int DaysOutstanding,
    bool IsOverdue,
    string? SourceDocumentType,
    int? SourceDocumentId
);

public record ARSummaryKPIs(
    decimal TotalOutstanding,
    decimal TotalOverdue,
    decimal TotalCurrent,
    int TotalCustomersWithBalance,
    int TotalCustomersOverdue,
    decimal DSO,
    decimal DaysBeyondTerms,
    decimal CollectionRate           // payments received / invoiced this period
);
```

---

### C7: AR Credit Memo Workflow

Creates reversing entries through the A47 GL engine, linked back to the original invoice.

```csharp
public interface IARCreditMemoService
{
    /// <summary>Create a credit memo against a customer invoice</summary>
    Task<CreditMemoResult> CreateCreditMemoAsync(int orgId, CreateCreditMemoRequest request);

    /// <summary>Apply credit memo to open invoices (partial or full)</summary>
    Task<Result> ApplyCreditMemoAsync(int orgId, int creditMemoJEId, List<CreditApplication> applications);
}

public record CreateCreditMemoRequest(
    int CustomerId,
    int? OriginalInvoiceJEId,         // link to original; null for standalone credit
    string Reason,
    List<CreditMemoLineRequest> Lines,
    string CreatedBy
);

public record CreditMemoLineRequest(
    int AccountId,
    decimal Amount,
    string? Description
);

public record CreditApplication(
    int InvoiceJELineId,              // the open receivable line to reduce
    decimal Amount                    // amount of credit to apply
);
```

> **Implementation:** Delegates to `IJournalEntryService.CreateAndPostAsync()` with journal type = SALE, swapped debit/credit (Cr: Receivable, Dr: Revenue/Returns), then calls `IReconciliationService.ReconcileAsync()` to match credit memo lines against original invoice lines.

---

## 5. Part B — Accounts Payable Sub-Ledger

### C8: Aged Payables Report

Uses the same `IAgingReportService.GetAgedPayablesAsync()` defined in C3, with `partner_type = 'Vendor'` and `account_type = 'Payable'`.

Adds DPO (Days Payable Outstanding) as the AP counterpart to DSO:

```
DPO = (Accounts Payable Balance / Cost of Goods Sold) × Days in Period
```

No additional entities required — shares `IAgingReportService` with AR.

---

### C9: 3-Way Matching Engine (PO ↔ GRN ↔ Invoice)

**Design heritage:** SAP's MIRO logistics invoice verification + Odoo's auto-fill from PO.

#### Entity: InvoiceMatchHeaders

```sql
CREATE TABLE finance.InvoiceMatchHeaders (
    id                      INT IDENTITY(1,1) PRIMARY KEY,
    org_id                  INT NOT NULL,
    vendor_id               INT NOT NULL,           -- soft-link
    vendor_invoice_number   NVARCHAR(50) NOT NULL,
    vendor_invoice_date     DATE NOT NULL,
    purchase_order_id       INT NOT NULL,            -- soft-link to procurement.PurchaseOrders
    purchase_order_number   NVARCHAR(50) NOT NULL,
    grn_id                  INT NULL,                -- soft-link to inventory.GoodsReceivedNotes
    grn_number              NVARCHAR(50) NULL,
    total_po_amount         DECIMAL(18,2) NOT NULL,
    total_grn_amount        DECIMAL(18,2) NULL,
    total_invoice_amount    DECIMAL(18,2) NOT NULL,
    match_status            NVARCHAR(20) NOT NULL DEFAULT 'Pending',
    -- 'Pending','Matched','PartialMatch','Mismatch','Overridden','Cancelled'
    variance_amount         DECIMAL(18,2) NULL,
    variance_percentage     DECIMAL(5,2) NULL,
    within_tolerance        BIT NULL,
    tolerance_rule_id       INT NULL,
    journal_entry_id        INT NULL,                -- FK to JournalEntries (created on match approval)
    matched_at              DATETIME2 NULL,
    matched_by              NVARCHAR(100) NULL,
    override_reason         NVARCHAR(500) NULL,
    override_by             NVARCHAR(100) NULL,
    created_at              DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    created_by              NVARCHAR(100) NOT NULL,

    CONSTRAINT CK_IMH_Status CHECK (match_status IN ('Pending','Matched','PartialMatch','Mismatch','Overridden','Cancelled'))
);

CREATE UNIQUE INDEX UQ_IMH_OrgVendorInvoice ON finance.InvoiceMatchHeaders (org_id, vendor_id, vendor_invoice_number);
CREATE INDEX IX_IMH_PO ON finance.InvoiceMatchHeaders (org_id, purchase_order_id);
CREATE INDEX IX_IMH_Status ON finance.InvoiceMatchHeaders (org_id, match_status);
```

#### Entity: InvoiceMatchLines

```sql
CREATE TABLE finance.InvoiceMatchLines (
    id                  INT IDENTITY(1,1) PRIMARY KEY,
    org_id              INT NOT NULL,
    match_header_id     INT NOT NULL,
    line_number         INT NOT NULL,
    item_description    NVARCHAR(200) NOT NULL,
    po_quantity         DECIMAL(18,4) NOT NULL,
    po_unit_price       DECIMAL(18,4) NOT NULL,
    po_line_total       DECIMAL(18,2) NOT NULL,
    grn_quantity        DECIMAL(18,4) NULL,
    invoice_quantity    DECIMAL(18,4) NOT NULL,
    invoice_unit_price  DECIMAL(18,4) NOT NULL,
    invoice_line_total  DECIMAL(18,2) NOT NULL,
    quantity_variance   DECIMAL(18,4) NULL,     -- invoice_qty - grn_qty (or po_qty)
    price_variance      DECIMAL(18,4) NULL,     -- invoice_price - po_price
    total_variance      DECIMAL(18,2) NULL,     -- invoice_total - po_total
    match_status        NVARCHAR(20) NOT NULL DEFAULT 'Pending',
    account_id          INT NULL,               -- GL account for this line

    CONSTRAINT FK_IML_Header FOREIGN KEY (match_header_id) REFERENCES finance.InvoiceMatchHeaders(id) ON DELETE CASCADE,
    CONSTRAINT CK_IML_Status CHECK (match_status IN ('Pending','Matched','QuantityMismatch','PriceMismatch','Overridden'))
);
```

#### Entity: MatchToleranceRules

```sql
CREATE TABLE finance.MatchToleranceRules (
    id                      INT IDENTITY(1,1) PRIMARY KEY,
    org_id                  INT NOT NULL,
    name                    NVARCHAR(100) NOT NULL,
    price_tolerance_percent DECIMAL(5,2) NOT NULL DEFAULT 0,  -- e.g., 2.00 = 2%
    price_tolerance_amount  DECIMAL(18,2) NOT NULL DEFAULT 0, -- absolute amount
    quantity_tolerance_percent DECIMAL(5,2) NOT NULL DEFAULT 0,
    total_tolerance_percent DECIMAL(5,2) NOT NULL DEFAULT 0,
    total_tolerance_amount  DECIMAL(18,2) NOT NULL DEFAULT 0,
    auto_approve_within     BIT NOT NULL DEFAULT 0,  -- auto-match if within tolerance
    is_default              BIT NOT NULL DEFAULT 0,
    is_active               BIT NOT NULL DEFAULT 1,

    CONSTRAINT UQ_MTR_OrgName UNIQUE (org_id, name)
);
```

#### Service: IInvoiceMatchingService

```csharp
public interface IInvoiceMatchingService
{
    /// <summary>Perform 3-way match: PO ↔ GRN ↔ Vendor Invoice</summary>
    Task<MatchResult> MatchInvoiceAsync(int orgId, MatchInvoiceRequest request);

    /// <summary>Perform 2-way match: PO ↔ Vendor Invoice (when GRN not required)</summary>
    Task<MatchResult> MatchInvoiceTwoWayAsync(int orgId, MatchInvoiceRequest request);

    /// <summary>Override a mismatch with reason</summary>
    Task<Result> OverrideMatchAsync(int orgId, int matchId, string reason, string overriddenBy);

    /// <summary>Approve match and create GL entry</summary>
    Task<MatchApprovalResult> ApproveMatchAsync(int orgId, int matchId, string approvedBy);

    /// <summary>Get match status for a PO</summary>
    Task<List<InvoiceMatchSummary>> GetMatchesByPOAsync(int orgId, int purchaseOrderId);

    /// <summary>List pending matches for review</summary>
    Task<PagedResult<InvoiceMatchSummary>> ListPendingMatchesAsync(int orgId, MatchFilter filter);
}
```

#### 3-Way Matching Logic

```
MatchInvoiceAsync(orgId, request):
  1. Load PO details from procurement (via soft-link lookup service)
  2. Load GRN details from inventory (via soft-link lookup service)
  3. Load vendor invoice lines from request
  4. For each line:
     a. Compare invoice_quantity vs grn_quantity → quantity_variance
     b. Compare invoice_unit_price vs po_unit_price → price_variance
     c. Compare invoice_line_total vs po_line_total → total_variance
     d. Load applicable tolerance rule (vendor-specific or org default)
     e. Determine line match status:
        - Matched: all variances within tolerance
        - QuantityMismatch: quantity outside tolerance
        - PriceMismatch: price outside tolerance
  5. Determine header match status:
     - Matched: all lines matched
     - PartialMatch: some lines matched, some mismatched
     - Mismatch: header-level variance outside tolerance
  6. If auto_approve_within = true AND all within tolerance:
     → auto-approve and create GL entry via A47 gateway
  7. Return MatchResult with line-by-line comparison
```

---

### C10: Payment Scheduling & Due Date Management

```sql
CREATE TABLE finance.PaymentSchedules (
    id                  INT IDENTITY(1,1) PRIMARY KEY,
    org_id              INT NOT NULL,
    vendor_id           INT NOT NULL,           -- soft-link
    journal_entry_id    INT NOT NULL,           -- FK to the AP journal entry
    journal_entry_line_id INT NOT NULL,         -- FK to the specific payable line
    due_date            DATE NOT NULL,
    original_amount     DECIMAL(18,2) NOT NULL,
    amount_remaining    DECIMAL(18,2) NOT NULL,
    payment_priority    TINYINT NOT NULL DEFAULT 2,
    payment_method      NVARCHAR(50) NULL,
    bank_account_id     INT NULL,               -- preferred bank for this payment
    payment_block       BIT NOT NULL DEFAULT 0,
    block_reason        NVARCHAR(200) NULL,
    blocked_by          NVARCHAR(100) NULL,
    blocked_at          DATETIME2 NULL,
    discount_date       DATE NULL,              -- pay by this date for early discount
    discount_amount     DECIMAL(18,2) NULL,
    discount_percent    DECIMAL(5,2) NULL,
    paid_in_run_id      INT NULL,               -- FK to PaymentRuns when paid
    status              NVARCHAR(20) NOT NULL DEFAULT 'Open',
    -- 'Open','Blocked','Scheduled','PartiallyPaid','Paid','Cancelled'

    CONSTRAINT FK_PS_JE FOREIGN KEY (journal_entry_id) REFERENCES finance.JournalEntries(id),
    CONSTRAINT CK_PS_Status CHECK (status IN ('Open','Blocked','Scheduled','PartiallyPaid','Paid','Cancelled')),
    CONSTRAINT CK_PS_Priority CHECK (payment_priority BETWEEN 1 AND 3)
);

CREATE INDEX IX_PS_DueDate ON finance.PaymentSchedules (org_id, due_date, status) WHERE status IN ('Open','Scheduled');
CREATE INDEX IX_PS_Vendor ON finance.PaymentSchedules (org_id, vendor_id, status);
CREATE INDEX IX_PS_Discount ON finance.PaymentSchedules (org_id, discount_date) WHERE discount_date IS NOT NULL AND status = 'Open';
```

#### Service: IPaymentScheduleService

```csharp
public interface IPaymentScheduleService
{
    /// <summary>Create payment schedule when AP entry is posted</summary>
    Task<PaymentSchedule> CreateScheduleAsync(int orgId, CreatePaymentScheduleRequest request);

    /// <summary>Get upcoming payments by due date</summary>
    Task<List<PaymentScheduleDto>> GetUpcomingPaymentsAsync(int orgId, DateTime fromDate, DateTime toDate);

    /// <summary>Block/unblock a payment</summary>
    Task<Result> BlockPaymentAsync(int orgId, int scheduleId, string reason, string blockedBy);
    Task<Result> UnblockPaymentAsync(int orgId, int scheduleId, string unblockedBy);

    /// <summary>Get payments eligible for early discount (discount_date approaching)</summary>
    Task<List<PaymentScheduleDto>> GetEarlyDiscountOpportunitiesAsync(int orgId, DateTime asOfDate, int lookAheadDays = 7);

    /// <summary>Cash flow forecast: sum of upcoming payables by week</summary>
    Task<CashFlowForecast> GetPayablesCashFlowAsync(int orgId, DateTime fromDate, int weeks = 8);
}
```

---

### C11: Automatic Payment Run Engine

**Design heritage:** SAP F110 automatic payment program + Odoo batch payments.

#### Entity: PaymentRuns

```sql
CREATE TABLE finance.PaymentRuns (
    id                  INT IDENTITY(1,1) PRIMARY KEY,
    org_id              INT NOT NULL,
    run_number          NVARCHAR(30) NOT NULL,
    run_date            DATE NOT NULL,
    payment_date        DATE NOT NULL,          -- actual payment value date
    status              NVARCHAR(20) NOT NULL DEFAULT 'Draft',
    -- 'Draft','Proposed','Reviewing','Approved','Executing','Completed','Cancelled'
    selection_criteria  NVARCHAR(MAX) NULL,     -- JSON of filters used
    bank_account_id     INT NULL,               -- source bank account
    payment_method      NVARCHAR(50) NULL,      -- 'BankTransfer','Check','Cash'
    include_discount    BIT NOT NULL DEFAULT 1, -- consider early payment discounts
    total_vendors       INT NOT NULL DEFAULT 0,
    total_items         INT NOT NULL DEFAULT 0,
    total_amount        DECIMAL(18,2) NOT NULL DEFAULT 0,
    total_discount      DECIMAL(18,2) NOT NULL DEFAULT 0,
    net_payment_amount  DECIMAL(18,2) NOT NULL DEFAULT 0,
    proposed_by         NVARCHAR(100) NOT NULL,
    proposed_at         DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    approved_by         NVARCHAR(100) NULL,
    approved_at         DATETIME2 NULL,
    executed_by         NVARCHAR(100) NULL,
    executed_at         DATETIME2 NULL,
    payment_file_path   NVARCHAR(500) NULL,     -- exported CSV/bank file

    CONSTRAINT UQ_PR_OrgNumber UNIQUE (org_id, run_number),
    CONSTRAINT CK_PR_Status CHECK (status IN ('Draft','Proposed','Reviewing','Approved','Executing','Completed','Cancelled'))
);

CREATE INDEX IX_PR_OrgDate ON finance.PaymentRuns (org_id, run_date DESC);
CREATE INDEX IX_PR_Status ON finance.PaymentRuns (org_id, status);
```

#### Entity: PaymentRunItems

```sql
CREATE TABLE finance.PaymentRunItems (
    id                      INT IDENTITY(1,1) PRIMARY KEY,
    org_id                  INT NOT NULL,
    payment_run_id          INT NOT NULL,
    vendor_id               INT NOT NULL,           -- soft-link
    payment_schedule_id     INT NOT NULL,            -- FK to PaymentSchedules
    journal_entry_id        INT NOT NULL,            -- the original AP journal entry
    entry_number            NVARCHAR(50) NOT NULL,
    due_date                DATE NOT NULL,
    original_amount         DECIMAL(18,2) NOT NULL,
    discount_amount         DECIMAL(18,2) NOT NULL DEFAULT 0,
    net_payment_amount      DECIMAL(18,2) NOT NULL,  -- original - discount
    payment_method          NVARCHAR(50) NULL,
    status                  NVARCHAR(20) NOT NULL DEFAULT 'Proposed',
    -- 'Proposed','Approved','Removed','Paid','Failed'
    removed_reason          NVARCHAR(200) NULL,
    payment_je_id           INT NULL,                -- JE created for the payment
    paid_at                 DATETIME2 NULL,

    CONSTRAINT FK_PRI_Run FOREIGN KEY (payment_run_id) REFERENCES finance.PaymentRuns(id),
    CONSTRAINT FK_PRI_Schedule FOREIGN KEY (payment_schedule_id) REFERENCES finance.PaymentSchedules(id),
    CONSTRAINT CK_PRI_Status CHECK (status IN ('Proposed','Approved','Removed','Paid','Failed'))
);

CREATE INDEX IX_PRI_RunId ON finance.PaymentRunItems (payment_run_id);
CREATE INDEX IX_PRI_Vendor ON finance.PaymentRunItems (org_id, vendor_id);
```

#### Entity: PaymentRunBatches

```sql
CREATE TABLE finance.PaymentRunBatches (
    id                  INT IDENTITY(1,1) PRIMARY KEY,
    org_id              INT NOT NULL,
    payment_run_id      INT NOT NULL,
    vendor_id           INT NOT NULL,
    vendor_name         NVARCHAR(200) NOT NULL,     -- denormalized for payment file
    payment_method      NVARCHAR(50) NOT NULL,
    bank_account_info   NVARCHAR(500) NULL,         -- vendor's bank details (from vendor master soft-link)
    item_count          INT NOT NULL,
    total_amount        DECIMAL(18,2) NOT NULL,
    total_discount      DECIMAL(18,2) NOT NULL DEFAULT 0,
    net_amount          DECIMAL(18,2) NOT NULL,
    payment_reference   NVARCHAR(100) NULL,
    payment_je_id       INT NULL,                   -- single JE for all items to this vendor
    status              NVARCHAR(20) NOT NULL DEFAULT 'Pending',

    CONSTRAINT FK_PRB_Run FOREIGN KEY (payment_run_id) REFERENCES finance.PaymentRuns(id),
    CONSTRAINT CK_PRB_Status CHECK (status IN ('Pending','Paid','Failed'))
);

CREATE INDEX IX_PRB_RunId ON finance.PaymentRunBatches (payment_run_id);
```

#### Service: IPaymentRunService

```csharp
public interface IPaymentRunService
{
    // Proposal
    Task<PaymentRunProposal> CreateProposalAsync(int orgId, PaymentRunProposalRequest request);
    Task<PaymentRunDetail> GetPaymentRunAsync(int orgId, int runId);
    Task<List<PaymentRunSummary>> ListPaymentRunsAsync(int orgId, int? limit = 20);

    // Review & Edit
    Task<Result> RemoveItemAsync(int orgId, int runId, int itemId, string reason, string removedBy);
    Task<Result> RestoreItemAsync(int orgId, int runId, int itemId, string restoredBy);
    Task<Result> UpdatePaymentMethodAsync(int orgId, int runId, int itemId, string paymentMethod);

    // Approval & Execution
    Task<Result> ApprovePaymentRunAsync(int orgId, int runId, string approvedBy);
    Task<PaymentRunResult> ExecutePaymentRunAsync(int orgId, int runId, string executedBy);

    // Export
    Task<string> ExportPaymentFileAsync(int orgId, int runId, string format = "CSV");

    // Cancel
    Task<Result> CancelPaymentRunAsync(int orgId, int runId, string reason, string cancelledBy);
}
```

#### DTOs

```csharp
public record PaymentRunProposalRequest(
    DateTime PaymentDate,
    DateTime DueDateCutoff,             // include items due on or before this date
    string? PaymentMethod = null,       // filter by method; null = all
    int? VendorId = null,               // filter to single vendor; null = all
    int? BankAccountId = null,          // source bank
    bool IncludeDiscountItems = true,   // include items with early discount opportunity
    PaymentPriority? MinPriority = null  // 1=High only, 2=High+Normal, 3=All
);

public record PaymentRunProposal(
    int RunId,
    string RunNumber,
    DateTime PaymentDate,
    int TotalVendors,
    int TotalItems,
    decimal TotalAmount,
    decimal TotalDiscount,
    decimal NetPaymentAmount,
    List<PaymentRunVendorGroup> VendorGroups
);

public record PaymentRunVendorGroup(
    int VendorId,
    string VendorName,
    int ItemCount,
    decimal TotalAmount,
    decimal TotalDiscount,
    decimal NetAmount,
    List<PaymentRunItemDto> Items
);

public record PaymentRunItemDto(
    int ItemId,
    int ScheduleId,
    string EntryNumber,
    DateTime DueDate,
    decimal OriginalAmount,
    decimal DiscountAmount,
    decimal NetPaymentAmount,
    int DaysUntilDue,
    bool HasDiscount,
    DateTime? DiscountDate,
    string Status
);
```

#### Payment Run Execution Logic

```
ExecutePaymentRunAsync(orgId, runId, executedBy):
  1. Validate: status = 'Approved'
  2. Group approved items by vendor → PaymentRunBatches
  3. For each vendor batch:
     a. Create JournalEntry via IJournalEntryService.CreateAndPostAsync():
        - Journal Type: BANK (or CASH)
        - Lines:
          Dr: Accounts Payable (per vendor payable account)     = net_amount
          Dr: Purchase Discount (if discount taken)             = discount_amount
          Cr: Bank Account                                      = total_amount
     b. Store payment_je_id on batch and items
     c. For each item in batch:
        - Update PaymentSchedule.status = 'Paid', paid_in_run_id
        - Call IReconciliationService.ReconcileAsync() to match payment line
          with original AP invoice line
     d. Update batch status = 'Paid'
  4. Generate payment file (CSV) with vendor bank details
  5. Update run: status = 'Completed', total counts
  6. Raise PaymentRunCompletedEvent
```

---

### C12: AP Open Items & Balance Inquiry

```csharp
public interface IAPInquiryService
{
    Task<PartnerBalanceResult> GetVendorBalanceAsync(int orgId, int vendorId, DateTime? asOfDate = null);
    Task<PagedResult<OpenItemDto>> GetVendorOpenItemsAsync(int orgId, VendorOpenItemsFilter filter);
    Task<PagedResult<PartnerTransactionDto>> GetVendorTransactionsAsync(int orgId, PartnerTransactionFilter filter);
    Task<APSummaryKPIs> GetAPSummaryAsync(int orgId, DateTime asOfDate);
}

public record APSummaryKPIs(
    decimal TotalOutstanding,
    decimal TotalOverdue,
    decimal TotalCurrent,
    decimal TotalDueThisWeek,
    decimal TotalDueThisMonth,
    int TotalVendorsWithBalance,
    decimal DPO,
    decimal DiscountOpportunityAmount    // available discounts if paid by discount date
);
```

---

### C13: Vendor Statement & Reconciliation

Uses the same `IStatementService` defined in C4, with `partner_type = 'Vendor'`.

Adds a **reconciliation statement** variant — comparing SMS open items against vendor's claimed balance:

```csharp
public record VendorReconciliationRequest(
    int VendorId,
    DateTime AsOfDate,
    decimal VendorClaimedBalance,
    List<VendorClaimedItem>? VendorItems = null   // optional list from vendor's statement
);

public record VendorClaimedItem(
    string Reference,
    DateTime Date,
    decimal Amount
);

public record VendorReconciliationResult(
    int VendorId,
    string VendorName,
    DateTime AsOfDate,
    decimal SMSBalance,
    decimal VendorClaimedBalance,
    decimal Difference,
    List<ReconciliationDifference> Differences,
    bool IsReconciled
);

public record ReconciliationDifference(
    string Type,                // "InSMSNotVendor", "InVendorNotSMS", "AmountDifference"
    string? EntryNumber,
    string? Reference,
    DateTime? Date,
    decimal? SMSAmount,
    decimal? VendorAmount,
    decimal? Difference
);
```

---

### C14: AP Debit Memo Workflow

Mirror of C7 for the AP side — creates reversing entries against vendor invoices.

```csharp
public interface IAPDebitMemoService
{
    Task<DebitMemoResult> CreateDebitMemoAsync(int orgId, CreateDebitMemoRequest request);
    Task<Result> ApplyDebitMemoAsync(int orgId, int debitMemoJEId, List<DebitApplication> applications);
}

public record CreateDebitMemoRequest(
    int VendorId,
    int? OriginalInvoiceJEId,
    string Reason,
    List<DebitMemoLineRequest> Lines,
    string CreatedBy
);
```

> **Implementation:** Creates JournalEntry with journal type = PURCH, swapped amounts (Dr: AP, Cr: Expense/Inventory), then reconciles against original vendor invoice lines.

---

### C15: Early Payment Discount Engine

```sql
CREATE TABLE finance.EarlyPaymentTerms (
    id                  INT IDENTITY(1,1) PRIMARY KEY,
    org_id              INT NOT NULL,
    code                NVARCHAR(20) NOT NULL,
    name                NVARCHAR(100) NOT NULL,
    -- e.g., "2/10 Net 30" = 2% discount if paid within 10 days, net due in 30
    discount_percent    DECIMAL(5,2) NOT NULL,
    discount_days       INT NOT NULL,               -- pay within N days for discount
    net_days            INT NOT NULL,               -- full payment due in N days
    discount_account_id INT NULL,                   -- GL account for discount taken (Dr) / given (Cr)
    is_active           BIT NOT NULL DEFAULT 1,
    created_at          DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),

    CONSTRAINT UQ_EPT_OrgCode UNIQUE (org_id, code),
    CONSTRAINT CK_EPT_DiscountDays CHECK (discount_days < net_days)
);
```

#### Service: IEarlyPaymentDiscountService

```csharp
public interface IEarlyPaymentDiscountService
{
    Task<List<EarlyPaymentTermDto>> ListTermsAsync(int orgId);
    Task<EarlyPaymentTermDto> CreateTermAsync(int orgId, CreateEarlyPaymentTermRequest request);

    /// <summary>Apply discount terms to a payment schedule (called when AP entry posted)</summary>
    Task<Result> ApplyDiscountTermsAsync(int orgId, int paymentScheduleId, int earlyPaymentTermId);

    /// <summary>Calculate discount for a payment date</summary>
    Task<DiscountCalculation> CalculateDiscountAsync(int orgId, int paymentScheduleId, DateTime paymentDate);
}

public record DiscountCalculation(
    decimal OriginalAmount,
    decimal DiscountPercent,
    decimal DiscountAmount,
    decimal NetPaymentAmount,
    DateTime DiscountDeadline,
    bool IsEligible,              // paymentDate <= discountDeadline
    int DaysUntilDeadline
);
```

---

## 6. Database Migrations

| # | Migration | Depends On | Entity |
|---|---|---|---|
| M1 | CreatePartnerFinancialProfiles | — | PartnerFinancialProfiles |
| M2 | CreateMatchToleranceRules | — | MatchToleranceRules |
| M3 | CreateEarlyPaymentTerms | — | EarlyPaymentTerms |
| M4 | CreateDunningProcedures | — | DunningProcedures |
| M5 | CreateDunningLevels | M4 | DunningLevels |
| M6 | CreateDunningRuns | — | DunningRuns |
| M7 | CreateDunningRunItems | M6 | DunningRunItems |
| M8 | CreateCreditHoldRequests | — | CreditHoldRequests |
| M9 | CreateStatementLog | — | StatementLog |
| M10 | CreateInvoiceMatchHeaders | — | InvoiceMatchHeaders |
| M11 | CreateInvoiceMatchLines | M10 | InvoiceMatchLines |
| M12 | CreatePaymentSchedules | A47 JournalEntries | PaymentSchedules |
| M13 | CreatePaymentRuns | — | PaymentRuns |
| M14 | CreatePaymentRunItems | M12, M13 | PaymentRunItems |
| M15 | CreatePaymentRunBatches | M13 | PaymentRunBatches |
| M16 | CreateAgingPerformanceIndex | A47 JournalEntryLines | IX_JEL_AgingAR, IX_JEL_AgingAP |
| M17 | SeedDefaultDunningProcedure | M4, M5 | Default 4-level dunning procedure |
| M18 | SeedDefaultToleranceRule | M2 | Default 2% / $100 tolerance |

**Execution Order:** M1 → M2 → M3 → M4 → M5 → M6 → M7 → M8 → M9 → M10 → M11 → M12 → M13 → M14 → M15 → M16 → M17 → M18

---

## 7. API Endpoints

### 7.1 AR Endpoints

| # | Method | Endpoint | Description | Auth Role |
|---|---|---|---|---|
| 1 | GET | /api/v1/finance/ar/aging | Aged receivables report | Finance.View |
| 2 | GET | /api/v1/finance/ar/dso | Days Sales Outstanding | Finance.View |
| 3 | GET | /api/v1/finance/ar/summary | AR summary KPIs | Finance.View |
| 4 | GET | /api/v1/finance/ar/customers/{id}/balance | Customer balance | Finance.View |
| 5 | GET | /api/v1/finance/ar/customers/{id}/open-items | Customer open items | Finance.View |
| 6 | GET | /api/v1/finance/ar/customers/{id}/transactions | Customer transactions | Finance.View |
| 7 | GET | /api/v1/finance/ar/customers/{id}/statement | Generate customer statement | Finance.View |
| 8 | POST | /api/v1/finance/ar/customers/{id}/statement/email | Email customer statement | Finance.AR.Execute |
| 9 | POST | /api/v1/finance/ar/statements/batch | Batch generate statements | Finance.AR.Execute |
| 10 | POST | /api/v1/finance/ar/credit-memo | Create AR credit memo | Finance.AR.CreditMemo |
| 11 | POST | /api/v1/finance/ar/credit-memo/{id}/apply | Apply credit memo | Finance.AR.CreditMemo |

### 7.2 Credit Control Endpoints

| # | Method | Endpoint | Description | Auth Role |
|---|---|---|---|---|
| 12 | GET | /api/v1/finance/credit/{customerId}/exposure | Credit exposure | Finance.View |
| 13 | POST | /api/v1/finance/credit/{customerId}/check | Credit check | Finance.Credit.Check |
| 14 | POST | /api/v1/finance/credit/{customerId}/hold | Place credit hold | Finance.Credit.Manage |
| 15 | POST | /api/v1/finance/credit/{customerId}/release | Release credit hold | Finance.Credit.Manage |
| 16 | POST | /api/v1/finance/credit/{customerId}/temporary-limit | Set temporary limit | Finance.Credit.Manage |
| 17 | PUT | /api/v1/finance/credit/{customerId}/risk-tier | Update risk tier | Finance.Credit.Manage |
| 18 | GET | /api/v1/finance/credit/{customerId}/history | Credit hold history | Finance.View |

### 7.3 Dunning Endpoints

| # | Method | Endpoint | Description | Auth Role |
|---|---|---|---|---|
| 19 | GET | /api/v1/finance/dunning/procedures | List dunning procedures | Finance.View |
| 20 | POST | /api/v1/finance/dunning/procedures | Create dunning procedure | Finance.Admin |
| 21 | PUT | /api/v1/finance/dunning/procedures/{id} | Update dunning procedure | Finance.Admin |
| 22 | POST | /api/v1/finance/dunning/runs/preview | Preview dunning run | Finance.Dunning.Execute |
| 23 | POST | /api/v1/finance/dunning/runs/{id}/execute | Execute dunning run | Finance.Dunning.Execute |
| 24 | GET | /api/v1/finance/dunning/runs | List dunning runs | Finance.View |
| 25 | GET | /api/v1/finance/dunning/runs/{id} | Dunning run detail | Finance.View |
| 26 | GET | /api/v1/finance/dunning/customers/{id}/history | Customer dunning history | Finance.View |
| 27 | POST | /api/v1/finance/dunning/customers/{id}/reset | Reset dunning level | Finance.Dunning.Admin |

### 7.4 AP Endpoints

| # | Method | Endpoint | Description | Auth Role |
|---|---|---|---|---|
| 28 | GET | /api/v1/finance/ap/aging | Aged payables report | Finance.View |
| 29 | GET | /api/v1/finance/ap/dpo | Days Payable Outstanding | Finance.View |
| 30 | GET | /api/v1/finance/ap/summary | AP summary KPIs | Finance.View |
| 31 | GET | /api/v1/finance/ap/vendors/{id}/balance | Vendor balance | Finance.View |
| 32 | GET | /api/v1/finance/ap/vendors/{id}/open-items | Vendor open items | Finance.View |
| 33 | GET | /api/v1/finance/ap/vendors/{id}/transactions | Vendor transactions | Finance.View |
| 34 | GET | /api/v1/finance/ap/vendors/{id}/statement | Generate vendor statement | Finance.View |
| 35 | POST | /api/v1/finance/ap/vendors/{id}/reconciliation | Vendor reconciliation | Finance.AP.Execute |
| 36 | POST | /api/v1/finance/ap/debit-memo | Create AP debit memo | Finance.AP.DebitMemo |
| 37 | POST | /api/v1/finance/ap/debit-memo/{id}/apply | Apply debit memo | Finance.AP.DebitMemo |

### 7.5 3-Way Matching Endpoints

| # | Method | Endpoint | Description | Auth Role |
|---|---|---|---|---|
| 38 | POST | /api/v1/finance/matching/match | Perform 3-way match | Finance.Matching.Execute |
| 39 | POST | /api/v1/finance/matching/{id}/override | Override mismatch | Finance.Matching.Override |
| 40 | POST | /api/v1/finance/matching/{id}/approve | Approve match | Finance.Matching.Approve |
| 41 | GET | /api/v1/finance/matching/pending | List pending matches | Finance.View |
| 42 | GET | /api/v1/finance/matching/po/{poId} | Matches by PO | Finance.View |
| 43 | GET | /api/v1/finance/matching/tolerances | List tolerance rules | Finance.View |
| 44 | POST | /api/v1/finance/matching/tolerances | Create tolerance rule | Finance.Admin |

### 7.6 Payment Run Endpoints

| # | Method | Endpoint | Description | Auth Role |
|---|---|---|---|---|
| 45 | POST | /api/v1/finance/payment-runs/propose | Create payment proposal | Finance.PaymentRun.Propose |
| 46 | GET | /api/v1/finance/payment-runs | List payment runs | Finance.View |
| 47 | GET | /api/v1/finance/payment-runs/{id} | Payment run detail | Finance.View |
| 48 | DELETE | /api/v1/finance/payment-runs/{id}/items/{itemId} | Remove item from run | Finance.PaymentRun.Edit |
| 49 | POST | /api/v1/finance/payment-runs/{id}/approve | Approve payment run | Finance.PaymentRun.Approve |
| 50 | POST | /api/v1/finance/payment-runs/{id}/execute | Execute payment run | Finance.PaymentRun.Execute |
| 51 | GET | /api/v1/finance/payment-runs/{id}/export | Export payment file | Finance.PaymentRun.Execute |
| 52 | POST | /api/v1/finance/payment-runs/{id}/cancel | Cancel payment run | Finance.PaymentRun.Admin |

### 7.7 Payment Schedule & Discount Endpoints

| # | Method | Endpoint | Description | Auth Role |
|---|---|---|---|---|
| 53 | GET | /api/v1/finance/payment-schedules/upcoming | Upcoming payments | Finance.View |
| 54 | GET | /api/v1/finance/payment-schedules/discounts | Discount opportunities | Finance.View |
| 55 | POST | /api/v1/finance/payment-schedules/{id}/block | Block payment | Finance.AP.Execute |
| 56 | POST | /api/v1/finance/payment-schedules/{id}/unblock | Unblock payment | Finance.AP.Execute |
| 57 | GET | /api/v1/finance/payment-schedules/cash-flow | Payables cash flow forecast | Finance.View |
| 58 | GET | /api/v1/finance/early-payment-terms | List early payment terms | Finance.View |
| 59 | POST | /api/v1/finance/early-payment-terms | Create early payment term | Finance.Admin |

### 7.8 Partner Financial Profile Endpoints

| # | Method | Endpoint | Description | Auth Role |
|---|---|---|---|---|
| 60 | GET | /api/v1/finance/partner-profiles/{partnerId} | Get partner financial profile | Finance.View |
| 61 | PUT | /api/v1/finance/partner-profiles/{partnerId} | Update partner financial profile | Finance.Admin |

**Total: 61 API endpoints**

---

## 8. UI Wireframes

### 8.1 AR Dashboard

```
┌─────────────────────────────────────────────────────────────────────┐
│  Accounts Receivable                                    [Export ▼]  │
├─────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  ┌───────────┐  ┌───────────┐  ┌───────────┐  ┌───────────┐       │
│  │ Total AR  │  │  Overdue  │  │    DSO    │  │  On Hold  │       │
│  │  $247,500 │  │  $82,300  │  │  38 days  │  │  3 custs  │       │
│  │  ▲ 12%    │  │  ▼ 5%    │  │  ▼ 2 days │  │           │       │
│  └───────────┘  └───────────┘  └───────────┘  └───────────┘       │
│                                                                     │
│  ┌──────────────────────────────────────────────────────────────┐  │
│  │  Aged Receivables                    As of: [2026-10-25 ▼]  │  │
│  ├────────────┬─────────┬────────┬────────┬────────┬───────────┤  │
│  │ Customer   │ Current │  1-30  │ 31-60  │ 61-90  │   90+    │  │
│  ├────────────┼─────────┼────────┼────────┼────────┼───────────┤  │
│  │ Acme Corp  │  12,000 │  8,500 │  5,200 │  3,100 │   1,800  │  │
│  │ Beta Ltd   │  25,000 │ 15,000 │  7,500 │      — │       —  │  │
│  │ Gamma Inc  │   8,200 │  4,100 │      — │      — │       —  │  │
│  │ Delta Co   │       — │      — │  9,800 │  6,500 │  14,200  │  │
│  ├────────────┼─────────┼────────┼────────┼────────┼───────────┤  │
│  │ TOTAL      │  45,200 │ 27,600 │ 22,500 │  9,600 │  16,000  │  │
│  └────────────┴─────────┴────────┴────────┴────────┴───────────┘  │
│                                                                     │
│  [Generate Statements]  [Run Dunning]  [Credit Review]             │
└─────────────────────────────────────────────────────────────────────┘
```

### 8.2 Customer Credit Control

```
┌─────────────────────────────────────────────────────────────────────┐
│  Credit Control — Acme Corp                         [← Back to AR] │
├─────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  Credit Limit:     $100,000          Risk Tier:  ● Medium          │
│  Temp Limit:       $120,000 (expires 2026-11-30)                   │
│  Current Exposure: $ 87,500                                        │
│  ├── Open Orders:   $35,000                                        │
│  └── Open Invoices: $52,500                                        │
│  Available Credit: $ 32,500          Status: ● Active              │
│                                                                     │
│  ┌──────────────────────────────────────────────────────────────┐  │
│  │  Dunning Status                                              │  │
│  │  Current Level: 2 of 4          Last Dunning: 2026-10-10    │  │
│  │  Next Dunning:  Level 3 in 6 days                           │  │
│  │  Block Sales:   At Level 4                                   │  │
│  └──────────────────────────────────────────────────────────────┘  │
│                                                                     │
│  [Place Hold]  [Set Temp Limit]  [Reset Dunning]  [View History]   │
│                                                                     │
│  ── Credit Hold History ────────────────────────────────────────── │
│  │ Date       │ Action       │ Reason          │ By           │    │
│  │ 2026-09-15 │ Hold         │ 60+ days overdue│ J. Smith     │    │
│  │ 2026-09-20 │ Release      │ Payment received│ M. Johnson   │    │
│  │ 2026-10-01 │ TempIncrease │ Large order     │ J. Smith     │    │
└─────────────────────────────────────────────────────────────────────┘
```

### 8.3 AP Dashboard & Payment Run

```
┌─────────────────────────────────────────────────────────────────────┐
│  Accounts Payable                                    [Export ▼]     │
├─────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  ┌───────────┐  ┌───────────┐  ┌───────────┐  ┌───────────┐       │
│  │ Total AP  │  │  Overdue  │  │    DPO    │  │ Due This  │       │
│  │  $183,200 │  │  $28,500  │  │  42 days  │  │   Week    │       │
│  │  ▲ 8%     │  │  ▼ 12%   │  │  ▲ 3 days │  │  $34,700  │       │
│  └───────────┘  └───────────┘  └───────────┘  └───────────┘       │
│                                                                     │
│  ┌──────────────────────────────────────────────────────────────┐  │
│  │  Aged Payables                       As of: [2026-10-25 ▼]  │  │
│  ├────────────┬─────────┬────────┬────────┬────────┬───────────┤  │
│  │ Vendor     │ Current │  1-30  │ 31-60  │ 61-90  │   90+    │  │
│  ├────────────┼─────────┼────────┼────────┼────────┼───────────┤  │
│  │ Supplier A │  30,000 │ 12,000 │      — │      — │       —  │  │
│  │ Supplier B │  18,500 │  9,200 │  6,800 │      — │       —  │  │
│  │ Supplier C │       — │  5,400 │  3,200 │  8,100 │  11,700  │  │
│  ├────────────┼─────────┼────────┼────────┼────────┼───────────┤  │
│  │ TOTAL      │  48,500 │ 26,600 │ 10,000 │  8,100 │  11,700  │  │
│  └────────────┴─────────┴────────┴────────┴────────┴───────────┘  │
│                                                                     │
│  [Create Payment Run]  [View Pending Matches]  [Discount Opps]     │
└─────────────────────────────────────────────────────────────────────┘
```

### 8.4 Payment Run Wizard

```
┌─────────────────────────────────────────────────────────────────────┐
│  Payment Run — PR/2026/0042                Step: [1]─[2]─[3]─[4]  │
├─────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  Step 2: Review Proposal                                           │
│                                                                     │
│  Payment Date: 2026-10-28     Due Cutoff: 2026-10-31              │
│  Bank Account: Operating Account - Bank ABC                        │
│  Vendors: 12    Items: 47    Total: $78,340    Discount: $1,230   │
│                                                                     │
│  ┌──────────────────────────────────────────────────────────────┐  │
│  │ ☑ Supplier A (4 items)                          $18,500     │  │
│  │   ├ INV-2026-0089  Due: Oct 25  $8,200           [Remove]   │  │
│  │   ├ INV-2026-0094  Due: Oct 28  $5,100  💰$102   [Remove]   │  │
│  │   ├ INV-2026-0101  Due: Oct 30  $3,400           [Remove]   │  │
│  │   └ INV-2026-0103  Due: Oct 31  $1,800           [Remove]   │  │
│  │                                                              │  │
│  │ ☑ Supplier B (3 items)                          $12,600     │  │
│  │   ├ INV-2026-0078  Due: Oct 22  $6,200  ⚠ 3d late          │  │
│  │   ├ INV-2026-0091  Due: Oct 28  $4,200           [Remove]   │  │
│  │   └ INV-2026-0099  Due: Oct 31  $2,200           [Remove]   │  │
│  │                                                              │  │
│  │ ☐ Supplier C (2 items) — BLOCKED                $8,100      │  │
│  │   ├ INV-2026-0065  Due: Oct 15  $5,400  🔒 Disputed        │  │
│  │   └ INV-2026-0072  Due: Oct 20  $2,700  🔒 Disputed        │  │
│  └──────────────────────────────────────────────────────────────┘  │
│                                                                     │
│  💰 = Early discount available    ⚠ = Overdue    🔒 = Blocked     │
│                                                                     │
│             [◁ Back]    [Approve & Execute ▷]    [Export CSV]       │
└─────────────────────────────────────────────────────────────────────┘
```

### 8.5 3-Way Matching

```
┌─────────────────────────────────────────────────────────────────────┐
│  Invoice Matching — Supplier A / INV-V-2026-0089       [← Back]   │
├─────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  PO: PO-2026-0234     GRN: GRN-2026-0567     Status: ⚠ Mismatch  │
│                                                                     │
│  ┌────────┬──────────────────┬──────────┬──────────┬──────────┐   │
│  │  Line  │ Item             │ PO Qty×$ │ GRN Qty  │ Inv Qty×$│   │
│  ├────────┼──────────────────┼──────────┼──────────┼──────────┤   │
│  │  1     │ Widget A         │ 100×$25  │ 100      │ 100×$25  │   │
│  │        │                  │  $2,500  │          │  $2,500  │   │
│  │        │ Status: ✅ Matched                                │   │
│  ├────────┼──────────────────┼──────────┼──────────┼──────────┤   │
│  │  2     │ Widget B         │ 200×$15  │ 195      │ 200×$15.50│  │
│  │        │                  │  $3,000  │          │  $3,100  │   │
│  │        │ Status: ⚠ Price variance $0.50/unit (+3.3%)       │   │
│  │        │ Tolerance: 2% / $100 — EXCEEDED                   │   │
│  ├────────┼──────────────────┼──────────┼──────────┼──────────┤   │
│  │  3     │ Widget C         │  50×$40  │  50      │  50×$40  │   │
│  │        │                  │  $2,000  │          │  $2,000  │   │
│  │        │ Status: ✅ Matched                                │   │
│  ├────────┼──────────────────┼──────────┼──────────┼──────────┤   │
│  │ TOTAL  │                  │  $7,500  │          │  $7,600  │   │
│  │        │ Variance: $100 (1.33%)                            │   │
│  └────────┴──────────────────┴──────────┴──────────┴──────────┘   │
│                                                                     │
│  [Override with Reason]    [Reject]    [Approve & Post to GL]      │
└─────────────────────────────────────────────────────────────────────┘
```

### 8.6 Dunning Configuration

```
┌─────────────────────────────────────────────────────────────────────┐
│  Dunning Procedures                                    [+ New]     │
├─────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  ┌──────────────────────────────────────────────────────────────┐  │
│  │ STANDARD — Standard Dunning (Default)           ● Active    │  │
│  │ Grace Days: 5    Min Amount: $50    Block Sales: Level 4    │  │
│  │                                                              │  │
│  │  Level │ Days After │ Action │ Charge │ Template             │  │
│  │  ──────┼────────────┼────────┼────────┼──────────────────── │  │
│  │    1   │    7       │ Email  │   —    │ Friendly reminder    │  │
│  │    2   │   14       │ Email  │   —    │ Firm reminder + PDF  │  │
│  │    3   │   30       │ Email  │ $25    │ Final notice         │  │
│  │    4   │   45       │ Manual │ $50    │ Collections referral │  │
│  └──────────────────────────────────────────────────────────────┘  │
│                                                                     │
│  ┌──────────────────────────────────────────────────────────────┐  │
│  │ GENTLE — Gentle Follow-Up (Key Accounts)        ● Active    │  │
│  │ Grace Days: 14   Min Amount: $500   Block Sales: Never      │  │
│  │                                                              │  │
│  │  Level │ Days After │ Action │ Charge │ Template             │  │
│  │  ──────┼────────────┼────────┼────────┼──────────────────── │  │
│  │    1   │   14       │ Email  │   —    │ Courtesy notice      │  │
│  │    2   │   30       │ Email  │   —    │ Gentle follow-up     │  │
│  │    3   │   60       │ Manual │   —    │ Account review       │  │
│  └──────────────────────────────────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────────┘
```

---

## 9. Business Rules

### 9.1 Credit Control Rules

| # | Rule | Description |
|---|---|---|
| BR-CR-001 | Credit limit enforcement | SO confirmation blocked when projected exposure > effective credit limit |
| BR-CR-002 | Effective limit | = MAX(credit_limit, temporary_credit_limit) when temporary not expired; otherwise credit_limit |
| BR-CR-003 | Credit hold blocks all | When credit_hold = true, all new SOs blocked regardless of limit |
| BR-CR-004 | Exposure includes orders | Credit exposure = open SO value + open receivable GL lines |
| BR-CR-005 | Temporary limit expiry | System clears temporary_credit_limit when current date > temporary_credit_expiry |
| BR-CR-006 | Hold audit trail | Every hold/release/temp-increase recorded in CreditHoldRequests |
| BR-CR-007 | Risk tier defaults | New customers default to Medium (2); upgrade/downgrade requires Finance.Credit.Manage |

### 9.2 Aging Rules

| # | Rule | Description |
|---|---|---|
| BR-AG-001 | Open items only | Aging includes only posted, unreconciled lines (status = 1, reconciled = 0) |
| BR-AG-002 | Bucket assignment | Based on DATEDIFF(DAY, entry_date, as_of_date) |
| BR-AG-003 | DSO formula | DSO = (AR Balance / Revenue in Period) × Days in Period |
| BR-AG-004 | DPO formula | DPO = (AP Balance / COGS in Period) × Days in Period |
| BR-AG-005 | Default buckets | [Current, 1-30, 31-60, 61-90, 91-120, 120+] |
| BR-AG-006 | Custom buckets | User may override bucket boundaries; minimum 3 buckets |

### 9.3 Dunning Rules

| # | Rule | Description |
|---|---|---|
| BR-DN-001 | Grace period | No dunning before grace_days after due date |
| BR-DN-002 | Minimum amount | Skip customer if total overdue < minimum_amount |
| BR-DN-003 | Level progression | Customer moves to next level only if days_after_previous elapsed since last dunning |
| BR-DN-004 | Level never skips | Must progress 1 → 2 → 3 → 4; cannot jump to level 3 from level 1 |
| BR-DN-005 | Sales block | When current_dunning_level >= block_sales_at_level and block_sales_at_dunning = true → set credit_hold |
| BR-DN-006 | Payment resets | When all customer open items cleared (balance = 0), reset current_dunning_level to 0 |
| BR-DN-007 | Charge creates JE | Dunning charge creates journal entry: Dr Receivable, Cr Dunning Revenue |
| BR-DN-008 | Run immutable | Completed dunning run items cannot be changed |
| BR-DN-009 | Manual reset | Dunning level can only be manually reset by Finance.Dunning.Admin role |
| BR-DN-010 | One run per day | Maximum one dunning run per org per day |

### 9.4 Statement Rules

| # | Rule | Description |
|---|---|---|
| BR-ST-001 | Activity statement | Shows all transactions (debits + credits) in date range with running balance |
| BR-ST-002 | Open items statement | Shows only unreconciled items as of statement date |
| BR-ST-003 | Opening balance | Computed from all posted lines before from_date |
| BR-ST-004 | Closing balance | opening_balance + debits - credits in period |
| BR-ST-005 | Batch minimum | Batch statement generation can filter by minimum balance |

### 9.5 Matching Rules

| # | Rule | Description |
|---|---|---|
| BR-MT-001 | 3-way match | Invoice quantity ≤ GRN quantity, Invoice price within tolerance of PO price |
| BR-MT-002 | 2-way match | Invoice quantity and price within tolerance of PO (GRN not required) |
| BR-MT-003 | Price tolerance | abs(invoice_price - po_price) / po_price ≤ tolerance_percent OR abs difference ≤ tolerance_amount |
| BR-MT-004 | Quantity tolerance | Invoice quantity must not exceed GRN quantity by more than tolerance_percent |
| BR-MT-005 | Auto-approve | If all lines within tolerance and auto_approve_within = true → match and create GL entry automatically |
| BR-MT-006 | Override requires reason | Mismatch override requires text reason and Finance.Matching.Override role |
| BR-MT-007 | Duplicate invoice | Same vendor + vendor_invoice_number rejects as duplicate |
| BR-MT-008 | Match before GL | Vendor invoice must be matched (or overridden) before GL posting |

### 9.6 Payment Run Rules

| # | Rule | Description |
|---|---|---|
| BR-PR-001 | Proposal includes due items | Includes PaymentSchedules where due_date ≤ due_date_cutoff AND status = 'Open' |
| BR-PR-002 | Blocked items excluded | Items with payment_block = true excluded from proposal (shown separately) |
| BR-PR-003 | One payment per vendor | Items grouped by vendor into single payment per vendor |
| BR-PR-004 | Discount auto-applied | If include_discount = true and payment_date ≤ discount_date → apply discount |
| BR-PR-005 | Discount GL posting | Discount posts to dedicated GL account: Dr AP (full), Cr Bank (net), Cr Discount (diff) |
| BR-PR-006 | Approval required | Payment run must be approved before execution |
| BR-PR-007 | Run creates JEs | Each vendor batch creates one JournalEntry via A47 engine |
| BR-PR-008 | Auto-reconcile | Payment JE lines auto-reconciled with original AP invoice lines |
| BR-PR-009 | Run immutable after execute | Completed/Cancelled runs cannot be modified |
| BR-PR-010 | Cancel before execute only | Only Draft/Proposed/Reviewing/Approved runs can be cancelled |

### 9.7 Early Payment Discount Rules

| # | Rule | Description |
|---|---|---|
| BR-EP-001 | Discount eligibility | Payment date must be ≤ invoice_date + discount_days |
| BR-EP-002 | Discount calculation | discount_amount = original_amount × discount_percent / 100 |
| BR-EP-003 | Separate GL account | Discount taken posts to discount GL account, not written off to expense |
| BR-EP-004 | Discount days < net days | discount_days must always be less than net_days |

**Total: 47 business rules**

---

## 10. Test Scenarios

### 10.1 Credit Control Tests

| # | Scenario | Expected |
|---|---|---|
| TS-CR-001 | Customer with no credit limit → SO confirmed | Pass — no limit means unlimited |
| TS-CR-002 | Customer at 80% of limit → SO confirmed | Pass — within limit |
| TS-CR-003 | Customer at 100% + new SO exceeds limit → SO blocked | Block with message |
| TS-CR-004 | Customer on credit hold → any SO blocked | Block regardless of amount |
| TS-CR-005 | Temporary limit > permanent limit, within temp → SO confirmed | Pass — uses higher temp limit |
| TS-CR-006 | Temporary limit expired → falls back to permanent | Uses permanent limit |
| TS-CR-007 | Place hold → release → verify SO unblocked | Full workflow |
| TS-CR-008 | Concurrent credit checks on same customer | No race condition |
| TS-CR-009 | Credit exposure includes open SO + open receivables | Correct sum |
| TS-CR-010 | Risk tier updated and persisted | Stored on profile |

### 10.2 Aging Tests

| # | Scenario | Expected |
|---|---|---|
| TS-AG-001 | Aged receivables with default buckets | Correct bucket assignment |
| TS-AG-002 | Aged payables with custom buckets [0,15,30,45,60] | Custom bucket assignment |
| TS-AG-003 | DSO calculation with 90-day period | Correct DSO value |
| TS-AG-004 | DPO calculation | Correct DPO value |
| TS-AG-005 | Drill-down from bucket → line items | Correct items shown |
| TS-AG-006 | Partially paid invoice appears in aging with residual | amount_residual shown |
| TS-AG-007 | Reversed entry excluded from aging | Not included |
| TS-AG-008 | Draft entry excluded from aging | Not included |

### 10.3 Dunning Tests

| # | Scenario | Expected |
|---|---|---|
| TS-DN-001 | Customer overdue 12 days, grace = 5, level 1 at day 7 → eligible | Level 1 proposed |
| TS-DN-002 | Customer overdue 3 days, grace = 5 → not eligible | Skipped |
| TS-DN-003 | Customer at level 2, 20 days since last → level 3 at day 30 → not eligible | Not yet |
| TS-DN-004 | Customer at level 2, 35 days since last → eligible for level 3 | Level 3 proposed |
| TS-DN-005 | Customer total overdue = $30, minimum = $50 → skipped | Skipped |
| TS-DN-006 | Execute dunning run → emails sent, levels updated | Full workflow |
| TS-DN-007 | Dunning charge creates balanced JE | Dr Receivable, Cr Revenue |
| TS-DN-008 | Customer reaches block_sales_at_level → credit_hold set | Auto-hold applied |
| TS-DN-009 | Customer pays all → level resets to 0 | Auto-reset |
| TS-DN-010 | Manual level reset by admin | Level set to 0 |
| TS-DN-011 | Two dunning runs same day → second rejected | BR-DN-010 enforced |

### 10.4 Statement Tests

| # | Scenario | Expected |
|---|---|---|
| TS-ST-001 | Activity statement for customer with 10 transactions | All 10 shown with running balance |
| TS-ST-002 | Open items statement excludes paid invoices | Only unpaid shown |
| TS-ST-003 | Opening balance correctly computed | Sum of all lines before from_date |
| TS-ST-004 | Statement PDF generated and downloadable | Valid PDF |
| TS-ST-005 | Batch statement for customers with balance > $1,000 | Correct filter |
| TS-ST-006 | Email statement to customer | Email sent, logged |
| TS-ST-007 | Vendor statement with reconciliation differences | Differences identified |

### 10.5 Matching Tests

| # | Scenario | Expected |
|---|---|---|
| TS-MT-001 | 3-way match: all lines match within tolerance | Status = Matched |
| TS-MT-002 | Price variance 3%, tolerance 2% → Mismatch | Status = PriceMismatch |
| TS-MT-003 | Price variance 1.5%, tolerance 2% → Matched | Within tolerance |
| TS-MT-004 | Invoice quantity > GRN quantity → Mismatch | QuantityMismatch |
| TS-MT-005 | Override mismatch with reason → Overridden | Status = Overridden |
| TS-MT-006 | Approve match → GL entry created | JE posted via A47 |
| TS-MT-007 | Duplicate vendor invoice number → rejected | BR-MT-007 |
| TS-MT-008 | Auto-approve within tolerance | Auto-matched and posted |
| TS-MT-009 | 2-way match (no GRN) | Matches PO ↔ Invoice only |
| TS-MT-010 | Partial delivery: GRN qty < PO qty, invoice = GRN qty | Matched |

### 10.6 Payment Run Tests

| # | Scenario | Expected |
|---|---|---|
| TS-PR-001 | Create proposal with 5 vendors, 15 items → correct totals | Proposal created |
| TS-PR-002 | Blocked items excluded from proposal | Not included |
| TS-PR-003 | Remove item from proposal → totals recalculated | Updated |
| TS-PR-004 | Approve payment run → status = Approved | Status change |
| TS-PR-005 | Execute payment run → JEs created per vendor | One JE per vendor |
| TS-PR-006 | Execute → payment schedules marked Paid | Status updated |
| TS-PR-007 | Execute → AP lines auto-reconciled with payment | Reconciled |
| TS-PR-008 | Early discount applied in payment run | Discount JE line posted |
| TS-PR-009 | Export payment file as CSV | Valid CSV with bank details |
| TS-PR-010 | Cancel approved run → status = Cancelled | No JEs created |
| TS-PR-011 | Execute already-completed run → rejected | Idempotent |
| TS-PR-012 | Concurrent payment runs → no double payment | Schedule locked |

### 10.7 Credit Memo / Debit Memo Tests

| # | Scenario | Expected |
|---|---|---|
| TS-CM-001 | AR credit memo against invoice → reversing JE created | Dr Revenue, Cr Receivable |
| TS-CM-002 | Apply credit memo to invoice → lines reconciled | Residual reduced |
| TS-CM-003 | Partial credit memo → partial reconciliation | Residual partially reduced |
| TS-CM-004 | AP debit memo against vendor invoice | Dr Payable, Cr Expense |
| TS-CM-005 | Apply debit memo to vendor invoice → reconciled | Lines matched |

### 10.8 Early Payment Discount Tests

| # | Scenario | Expected |
|---|---|---|
| TS-EP-001 | Pay within discount window → discount applied | net_amount = original - discount |
| TS-EP-002 | Pay after discount window → no discount | Full amount |
| TS-EP-003 | Discount GL entry: Dr AP, Cr Bank, Cr Discount Account | Balanced 3-line JE |
| TS-EP-004 | Discount opportunities list shows upcoming deadlines | Correct list |

**Total: 62 test scenarios**

---

## 11. Development Phases

### Phase 1 — Foundation & Partner Profile (8 days)

| Task | Effort |
|---|---|
| 1A. PartnerFinancialProfiles entity, migration, seed | 2 days |
| 1B. IPartnerLookupService implementation | 1.5 days |
| 1C. MatchToleranceRules + EarlyPaymentTerms entities | 1.5 days |
| 1D. CreditHoldRequests entity | 1 day |
| 1E. StatementLog entity | 1 day |
| 1F. All Phase 1 migrations (M1-M3, M8-M9) | 1 day |

### Phase 2 — Credit Control & Aging (10 days)

| Task | Effort |
|---|---|
| 2A. ICreditControlService — credit exposure computation | 2.5 days |
| 2B. ICreditControlService — hold/release/temp limit workflow | 2 days |
| 2C. Credit check integration point (callable by Sales) | 1.5 days |
| 2D. IAgingReportService — aged receivables | 2 days |
| 2E. IAgingReportService — aged payables + DSO/DPO | 1.5 days |
| 2F. Performance index migration (M16) | 0.5 days |

### Phase 3 — Dunning & Statements (10 days)

| Task | Effort |
|---|---|
| 3A. DunningProcedures + DunningLevels entities, migrations (M4-M5) | 1.5 days |
| 3B. DunningRuns + DunningRunItems entities, migrations (M6-M7) | 1.5 days |
| 3C. IDunningService — preview and execution logic | 3 days |
| 3D. Dunning email template engine + charge JE creation | 1.5 days |
| 3E. IStatementService — customer and vendor statement generation | 2 days |
| 3F. Statement PDF rendering + email delivery | 0.5 days |

### Phase 4 — 3-Way Matching & Payment Scheduling (12 days)

| Task | Effort |
|---|---|
| 4A. InvoiceMatchHeaders + InvoiceMatchLines entities, migrations (M10-M11) | 2 days |
| 4B. IInvoiceMatchingService — 3-way and 2-way matching | 3 days |
| 4C. Match override and approval workflow | 1.5 days |
| 4D. PaymentSchedules entity, migration (M12) | 1.5 days |
| 4E. IPaymentScheduleService — schedule creation, block/unblock | 2 days |
| 4F. IEarlyPaymentDiscountService — terms and calculations | 1 day |
| 4G. Cash flow forecast computation | 1 day |

### Phase 5 — Payment Run Engine (10 days)

| Task | Effort |
|---|---|
| 5A. PaymentRuns + PaymentRunItems + PaymentRunBatches entities, migrations (M13-M15) | 2 days |
| 5B. IPaymentRunService — proposal generation | 2 days |
| 5C. IPaymentRunService — review/edit/approve workflow | 2 days |
| 5D. IPaymentRunService — execution (JE creation, reconciliation) | 2.5 days |
| 5E. Payment file export (CSV) | 0.5 days |
| 5F. Seed default tolerance + dunning procedure (M17-M18) | 1 day |

### Phase 6 — API, UI & Testing (15 days)

| Task | Effort |
|---|---|
| 6A. AR API endpoints (1-11) | 2 days |
| 6B. Credit control API endpoints (12-18) | 1 day |
| 6C. Dunning API endpoints (19-27) | 1 day |
| 6D. AP API endpoints (28-37) | 1.5 days |
| 6E. Matching API endpoints (38-44) | 1 day |
| 6F. Payment run API endpoints (45-59) | 1.5 days |
| 6G. AR dashboard + aging + credit control UI | 2 days |
| 6H. AP dashboard + aging + payment run wizard UI | 2 days |
| 6I. 3-way matching UI + dunning config UI | 1.5 days |
| 6J. Unit tests (62 scenarios) | 1.5 days |

### Summary

| Phase | Description | Days |
|---|---|---|
| Phase 1 | Foundation & Partner Profile | 8 |
| Phase 2 | Credit Control & Aging | 10 |
| Phase 3 | Dunning & Statements | 10 |
| Phase 4 | 3-Way Matching & Payment Scheduling | 12 |
| Phase 5 | Payment Run Engine | 10 |
| Phase 6 | API, UI & Testing | 15 |
| **Total** | | **65 days (13 weeks)** |

---

## 12. Appendix A — Deferred Roadmap

| Addendum | Feature | Reason |
|---|---|---|
| A49 | Bank Reconciliation | Requires bank feed integration, statement import, auto-matching |
| A50 | Multi-Currency Revaluation | Requires unrealized gain/loss computation, revaluation JEs |
| A51 | Tax Engine | Requires tax code configuration, tax computation, tax reporting |
| A52 | Financial Statements (P&L, Balance Sheet) | Requires account grouping, reporting dimensions |
| A53 | Payment File Formats (SEPA, ACH, NACHA) | Requires format-specific generators, bank-specific customization |
| A54 | Withholding Tax on Payment | Requires tax authority configuration, certificate generation |
| A55 | Customer Self-Service Portal | Requires external-facing UI, online payment gateway integration |

---

## Cross-Reference: Entity Dependencies

```
A42 ChartOfAccounts ◄────────── A48 AgingReport (account_type filter)
A42 FiscalPeriods ◄──────────── A48 StatementService (period boundaries)
A44 IFinancialPostingGateway ──► A48 (unchanged — existing pipeline)
A47 JournalEntries ◄──────────── A48 PaymentSchedules (FK)
A47 JournalEntryLines ◄───────── A48 AgingReport (partner_type, amount_residual)
A47 IReconciliationService ◄───── A48 PaymentRun (auto-reconcile)
A47 IJournalEntryService ◄─────── A48 PaymentRun, DunningCharge, CreditMemo (creates JEs)
A48 PartnerFinancialProfiles ──► A48 CreditControl, Dunning (central profile)
A48 DunningProcedures ──────────► A48 DunningRuns (procedure reference)
A48 PaymentSchedules ──────────► A48 PaymentRunItems (schedule reference)
A48 InvoiceMatchHeaders ──────► A48 InvoiceMatchLines (CASCADE)
A48 PaymentRuns ────────────────► A48 PaymentRunItems, PaymentRunBatches (run reference)
```

---

**End of Addendum 48**
