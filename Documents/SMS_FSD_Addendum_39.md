# SUPPLY MANAGEMENT SYSTEM — Functional Specification Document

## FSD Addendum 39 — POS Returns, Exchanges & Credit Notes

| Property | Value |
|---|---|
| Document ID | SMS-FSD-ADD-039 |
| Version | 1.0 |
| Date | 2026-10-11 |
| Status | Draft |
| Author | System Architect |
| Classification | Internal — Development |
| Depends On | SMS-FSD-ADD-038 (Point of Sale Module) |
| Architecture | .NET 8 / EF Core 8 / SQL Server 2022 / React 18 PWA / Dexie.js / SignalR / Hangfire |
| Change Items | 12 changes (C1–C12) |
| Impact | 14 migrations, 4 new tables, 1 new view, 5 modified tables, 24 API endpoints, 10 permissions, 35 test scenarios |

---

## Table of Contents

1. [Purpose & Scope](#1-purpose--scope)
2. [C1 — Return Reason Catalog](#2-c1--return-reason-catalog)
3. [C2 — Return Policy Configuration](#3-c2--return-policy-configuration)
4. [C3 — Order Return Tracking Extensions](#4-c3--order-return-tracking-extensions)
5. [C4 — Credit Notes](#5-c4--credit-notes)
6. [C5 — Credit Note Transactions](#6-c5--credit-note-transactions)
7. [C6 — Payment Method Extension (Credit Note Redemption)](#7-c6--payment-method-extension)
8. [C7 — Same-Store Return Flow](#8-c7--same-store-return-flow)
9. [C8 — Cross-Store Return Flow](#9-c8--cross-store-return-flow)
10. [C9 — Exchange Transaction Flow](#10-c9--exchange-transaction-flow)
11. [C10 — Return & Credit Note PWA Integration](#11-c10--return--credit-note-pwa-integration)
12. [C11 — Return Finance Integration](#12-c11--return-finance-integration)
13. [C12 — SignalR Return Events](#13-c12--signalr-return-events)
14. [Database Migrations](#14-database-migrations)
15. [API Endpoints](#15-api-endpoints)
16. [Permission Claims](#16-permission-claims)
17. [POS Return Screen Designs](#17-pos-return-screen-designs)
18. [Business Rules](#18-business-rules)
19. [Test Scenarios](#19-test-scenarios)
20. [Development Phases](#20-development-phases)
21. [Future Enhancements](#21-future-enhancements)

---

## 1. Purpose & Scope

**This addendum extends the POS module (Addendum 38) with a complete return, exchange and credit note system. It introduces configurable return policies per store, structured return reason tracking, cross-store return capability, store credit (credit notes), and the PWA screens that cashiers and supervisors use to process these transactions. The design follows Odoo's return model — returns create reverse orders with negative lines, exchanges combine return lines and new sale lines in a single transaction, and refunds can be issued as cash, to the original payment method, or as a reusable credit note.**

### 1.1 Design Principles

- **Return as Reverse Order** — A return creates a new `PosOrder` with `order_type = RETURN` (already defined in Addendum 38) linked to the original via `original_order_id`. Return lines carry negative quantities. This preserves a complete audit trail and reuses the existing order processing pipeline.
- **Exchange as Single Transaction** — An exchange order (`order_type = EXCHANGE`) contains both negative (returned) and positive (new purchase) lines. The customer settles only the net difference. One receipt, one journal entry, one stock adjustment batch.
- **Partial Return Tracking** — Each original order line tracks `returned_qty` so the system knows exactly how much has been returned across potentially multiple return transactions. `returnable_qty = quantity - returned_qty`.
- **Return Policy Enforcement** — Stores configure return windows, authorization thresholds, restocking fees, and cross-store rules. The system enforces these before processing and allows supervisor override when needed.
- **Credit Note as Store Credit** — When a refund is issued as a credit note, it creates a reusable balance that the customer can spend at any store in the organization. Credit notes have optional expiry and a full transaction log.
- **Cross-Store Returns Require Online** — Looking up an order from another store requires a server query. The system blocks cross-store returns when the terminal is offline. Same-store returns work offline when the original order exists in the local Dexie database.

### 1.2 Relationship to Addendum 38

This addendum **extends** the following Addendum 38 structures:

| Addendum 38 Element | What This Addendum Does |
|---|---|
| `pos.PosOrders.order_type` (0=Sale, 1=Return, 2=Exchange) | Defines the full return and exchange flows for types 1 and 2 |
| `pos.PosOrders.original_order_id` FK | Adds `original_store_id` for cross-store tracking |
| `pos.PosOrders.return_reason` (free text) | Replaces with structured `return_reason_id` FK to new lookup table |
| `pos.PosOrders.status` (3=Returned, 4=PartialReturn) | Defines when these statuses are set on the original order |
| `pos.PosOrderLines.is_return_line` + `original_line_id` | Adds `returned_qty` on the original line for partial tracking |
| `pos.PosPayments.is_refund` flag | Adds credit note as payment method (method=6) |
| `pos.Shifts.total_cash_refunds` / `total_card_refunds` | Adds `total_credit_note_issued` and `total_credit_note_redeemed` |
| `POST /api/pos/orders/{id}/return` endpoint | Enriches with policy checks, reason, authorization, credit note |
| `POS_RETURNS` feature flag (Toggleable) | Adds `POS_CREDIT_NOTES` feature flag |
| `pos.order.return` permission | Adds 10 new return/credit-note permissions |
| Return stock flow (`POS_RETURN` movement type) | Extends for cross-store warehouse routing |
| Return journal entry (reversal) | Extends for cross-store inter-store reconciliation |
| PWA offline return (⚠ if original in local DB) | Formalizes same-store offline vs cross-store online rules |

### 1.3 Target Scope

| Included | Excluded (Future) |
|---|---|
| Same-store full and partial returns | Advanced warranty/guarantee tracking |
| Cross-store returns (online only) | Manufacturer return authorization (RMA) |
| Same-store and cross-store exchanges | Automated restocking/return-to-vendor flow |
| Credit note issuance and redemption | Credit note transfer between customers |
| Return policy per store (window, fees, auth) | Dynamic return scoring / fraud ML |
| Manager authorization for high-value returns | Return shipping / pickup scheduling |
| Return reason catalog | Automated return reason analysis / reporting |
| Offline same-store returns | Offline cross-store returns |
| Credit note redemption (online only) | Offline credit note redemption |

### 1.4 Changes Summary

| Change ID | Title | Category |
|---|---|---|
| C1 | Return Reason Catalog | New table — `pos.ReturnReasons` |
| C2 | Return Policy Configuration | New table — `pos.ReturnPolicies`, modify `pos.Stores` |
| C3 | Order Return Tracking Extensions | Modify `pos.PosOrders`, `pos.PosOrderLines` |
| C4 | Credit Notes | New table — `pos.CreditNotes` |
| C5 | Credit Note Transactions | New table — `pos.CreditNoteTransactions` |
| C6 | Payment Method Extension | Modify `pos.PosPayments`, `pos.Shifts` |
| C7 | Same-Store Return Flow | Service logic, stock reversal, same-store finance |
| C8 | Cross-Store Return Flow | Order lookup, authorization, inter-store reconciliation |
| C9 | Exchange Transaction Flow | Mixed positive/negative lines, net settlement |
| C10 | Return & Credit Note PWA Integration | Dexie schema, return/exchange screens, offline rules |
| C11 | Return Finance Integration | Return JE, exchange JE, cross-store JE, credit note liability |
| C12 | SignalR Return Events | New hub events for returns and credit notes |

---

## 2. C1 — Return Reason Catalog

### 2.1 Rationale

Addendum 38 stores return reason as free text (`pos.PosOrders.return_reason`). For analytics, reporting, and policy enforcement, reasons must be structured. A lookup table provides consistent categorization while still allowing a free-text note per return.

### 2.2 Entity

#### 2.2.1 `pos.ReturnReasons`

| Column | Type | Constraints | Description |
|---|---|---|---|
| `return_reason_id` | BIGINT | PK, IDENTITY | Auto-increment PK |
| `org_id` | BIGINT | NOT NULL, FK → tenant.Organizations | Tenant |
| `code` | VARCHAR(20) | NOT NULL | Reason code: `DEFECTIVE`, `WRONG_ITEM`, `SIZE`, etc. |
| `name` | NVARCHAR(100) | NOT NULL | Display name: "Defective / Damaged" |
| `description` | NVARCHAR(300) | NULL | Detailed description for cashier guidance |
| `requires_note` | BIT | NOT NULL, DEFAULT 0 | Forces cashier to enter a note when selecting this reason |
| `requires_manager` | BIT | NOT NULL, DEFAULT 0 | Requires supervisor authorization when this reason is selected |
| `applies_to` | TINYINT | NOT NULL, DEFAULT 0 | 0=Both (Return+Exchange), 1=ReturnOnly, 2=ExchangeOnly |
| `restocking_fee_override` | DECIMAL(5,2) | NULL | Override store-level restocking fee for this reason (NULL = use store default). 0 = waive fee |
| `sort_order` | INT | NOT NULL, DEFAULT 0 | Display sort order |
| `is_active` | BIT | NOT NULL, DEFAULT 1 | |
| `created_at` | DATETIME2(2) | NOT NULL, DEFAULT SYSUTCDATETIME() | |
| `created_by` | BIGINT | NOT NULL, FK → auth.Users | |
| `modified_at` | DATETIME2(2) | NOT NULL, DEFAULT SYSUTCDATETIME() | |
| `modified_by` | BIGINT | NULL, FK → auth.Users | |

**Indexes:**
- PK: `return_reason_id`
- UQ: `(org_id, code)` — unique reason code per tenant
- IX: `(org_id, is_active, sort_order)` — active reasons sorted

**Seeded Default Reasons** (M039.8):

| Code | Name | Requires Note | Requires Manager |
|---|---|---|---|
| `DEFECTIVE` | Defective / Damaged | No | No |
| `WRONG_ITEM` | Wrong Item Received | No | No |
| `WRONG_SIZE` | Wrong Size / Fit | No | No |
| `NOT_AS_EXPECTED` | Not As Expected | Yes | No |
| `CHANGED_MIND` | Changed Mind | No | No |
| `DUPLICATE` | Duplicate Purchase | No | No |
| `PRICE_MATCH` | Price Match / Better Price Found | Yes | Yes |
| `WARRANTY` | Warranty Claim | Yes | Yes |
| `OTHER` | Other | Yes | No |

---

## 3. C2 — Return Policy Configuration

### 3.1 Rationale

Stores need configurable return policies to control return windows, authorization thresholds, restocking fees, and cross-store rules. A policy can be set at the organization level (default for all stores) or overridden per store. This follows Odoo's approach where return settings are configurable per point of sale.

### 3.2 Entity

#### 3.2.1 `pos.ReturnPolicies`

| Column | Type | Constraints | Description |
|---|---|---|---|
| `return_policy_id` | BIGINT | PK, IDENTITY | Auto-increment PK |
| `org_id` | BIGINT | NOT NULL, FK → tenant.Organizations | Tenant |
| `store_id` | BIGINT | NULL, FK → pos.Stores | NULL = org-wide default policy. Non-NULL = store-specific override |
| `name` | NVARCHAR(100) | NOT NULL | Policy name: "Standard 30-Day Return", "Final Sale — No Returns" |
| `return_window_days` | INT | NULL | Days after purchase within which returns are accepted. NULL = unlimited |
| `exchange_window_days` | INT | NULL | Days for exchange. NULL = same as `return_window_days`. 0 = exchanges not allowed |
| `require_receipt` | BIT | NOT NULL, DEFAULT 1 | Whether original receipt/order reference is required |
| `allow_no_receipt_exchange` | BIT | NOT NULL, DEFAULT 0 | Allow exchange without receipt (for store credit only) |
| `allow_cross_store_return` | BIT | NOT NULL, DEFAULT 1 | Whether this store accepts returns from other stores |
| `cross_store_requires_manager` | BIT | NOT NULL, DEFAULT 1 | Cross-store returns require manager authorization |
| `restocking_fee_pct` | DECIMAL(5,2) | NOT NULL, DEFAULT 0.00 | Percentage deducted from refund (0 = no fee) |
| `max_return_no_auth` | DECIMAL(18,4) | NULL | Maximum return amount without manager authorization. NULL = no limit |
| `refund_to_original_method` | BIT | NOT NULL, DEFAULT 0 | Force refund to same payment method as original sale |
| `allow_cash_refund` | BIT | NOT NULL, DEFAULT 1 | Allow cash refund |
| `allow_credit_note_refund` | BIT | NOT NULL, DEFAULT 1 | Allow credit note as refund method |
| `credit_note_expiry_days` | INT | NULL | Days until credit note expires. NULL = no expiry |
| `is_active` | BIT | NOT NULL, DEFAULT 1 | |
| `created_at` | DATETIME2(2) | NOT NULL, DEFAULT SYSUTCDATETIME() | |
| `created_by` | BIGINT | NOT NULL, FK → auth.Users | |
| `modified_at` | DATETIME2(2) | NOT NULL, DEFAULT SYSUTCDATETIME() | |
| `modified_by` | BIGINT | NULL, FK → auth.Users | |
| `row_version` | ROWVERSION | NOT NULL | Optimistic concurrency |

**Indexes:**
- PK: `return_policy_id`
- UQ: `(org_id, store_id)` WHERE store_id IS NOT NULL — one override per store
- IX: `(org_id, is_active)` — active policies

**Policy Resolution:**

```csharp
public async Task<ReturnPolicy> GetEffectivePolicyAsync(long orgId, long storeId)
{
    // 1. Try store-specific policy
    var storePolicy = await _db.ReturnPolicies
        .FirstOrDefaultAsync(p => p.OrgId == orgId && p.StoreId == storeId && p.IsActive);
    if (storePolicy != null) return storePolicy;
    
    // 2. Fall back to org-wide default (store_id IS NULL)
    var orgPolicy = await _db.ReturnPolicies
        .FirstOrDefaultAsync(p => p.OrgId == orgId && p.StoreId == null && p.IsActive);
    if (orgPolicy != null) return orgPolicy;
    
    // 3. No policy configured — returns disallowed
    throw new BusinessException("No return policy configured. Contact admin.");
}
```

### 3.3 Store Modification

**Add FK column to `pos.Stores`:**

| Column | Type | Constraints | Description |
|---|---|---|---|
| `return_policy_id` | BIGINT | NULL, FK → pos.ReturnPolicies | Store-specific return policy override. NULL = use org default |

> **Note:** This is a convenience FK for quick lookup. The policy resolution logic (above) handles the fallback chain. The FK is set automatically when a store-specific ReturnPolicy is created.

---

## 4. C3 — Order Return Tracking Extensions

### 4.1 Rationale

Addendum 38 defined the basic return columns (`original_order_id`, `return_reason`, `is_return_line`, `original_line_id`). This addendum extends them for cross-store tracking, structured reasons, manager authorization, and partial return accounting.

### 4.2 PosOrders Modifications

**New columns added to `pos.PosOrders`:**

| Column | Type | Constraints | Description |
|---|---|---|---|
| `original_store_id` | BIGINT | NULL, FK → pos.Stores | For cross-store returns: the store where the original sale happened |
| `return_reason_id` | BIGINT | NULL, FK → pos.ReturnReasons | Structured return reason (replaces free-text for primary categorization) |
| `authorized_by` | BIGINT | NULL, FK → auth.Users | Manager who authorized the return (when required) |
| `authorized_at` | DATETIME2(2) | NULL | When authorization was given |
| `credit_note_id` | BIGINT | NULL, FK → pos.CreditNotes | Credit note issued for this return (if refund = credit note) |
| `restocking_fee_amount` | DECIMAL(18,4) | NOT NULL, DEFAULT 0 | Computed restocking fee deducted from refund |
| `is_cross_store` | BIT | NOT NULL, DEFAULT 0 | Flag: original sale was at a different store |

> **Note:** The existing `return_reason` NVARCHAR(300) column is retained for free-text notes alongside the structured `return_reason_id`. When a ReturnReason has `requires_note = true`, the cashier must enter text in `return_reason`.

**New indexes:**
- IX: `(original_store_id)` WHERE original_store_id IS NOT NULL — cross-store return queries
- IX: `(credit_note_id)` WHERE credit_note_id IS NOT NULL — credit note lookup
- IX: `(return_reason_id)` WHERE return_reason_id IS NOT NULL — reason analytics

### 4.3 PosOrderLines Modifications

**New column added to `pos.PosOrderLines`:**

| Column | Type | Constraints | Description |
|---|---|---|---|
| `returned_qty` | DECIMAL(18,4) | NOT NULL, DEFAULT 0 | Total quantity returned across all return orders. Updated when return orders are created against this line |

**Computed returnable quantity:**

```sql
-- Not a persisted computed column — calculated at application level
returnable_qty = quantity - returned_qty
```

**Business rule:** When `returned_qty = quantity`, the line is fully returned. When `returned_qty > 0 AND returned_qty < quantity`, the line is partially returned. `returned_qty` can never exceed `quantity`.

### 4.4 Status Transition on Original Order

When a return is processed against an original sale order, the original order's status updates:

```
Original order (status = Completed)
       │
       ├── All lines fully returned? (every line: returned_qty = quantity)
       │     YES → original.status = Returned (3)
       │
       └── Some lines partially/fully returned, others untouched?
             YES → original.status = PartialReturn (4)
```

---

## 5. C4 — Credit Notes

### 5.1 Rationale

A credit note is store credit issued to a customer as an alternative to a cash/card refund. Credit notes are reusable — the customer can spend the balance across multiple future purchases at any store in the organization. This reduces cash outflow for the retailer and encourages repeat business. Odoo implements store credit similarly through customer credit notes.

### 5.2 Entity

#### 5.2.1 `pos.CreditNotes`

| Column | Type | Constraints | Description |
|---|---|---|---|
| `credit_note_id` | BIGINT | PK, IDENTITY | Auto-increment PK |
| `org_id` | BIGINT | NOT NULL, FK → tenant.Organizations | Tenant |
| `credit_note_number` | VARCHAR(30) | NOT NULL | Auto-generated: `CN-{store_code}-{YYYYMMDD}-{seq}` |
| `customer_id` | BIGINT | NULL, FK → customers.Customers | Customer who holds this credit. NULL = bearer credit note (anyone with the number can redeem) |
| `return_order_id` | BIGINT | NULL, FK → pos.PosOrders | The return order that generated this credit note. NULL = manually issued |
| `original_amount` | DECIMAL(18,4) | NOT NULL | Amount originally issued |
| `remaining_balance` | DECIMAL(18,4) | NOT NULL | Current balance (decremented on each redemption) |
| `currency_code` | VARCHAR(3) | NOT NULL | Currency (matches org currency) |
| `status` | TINYINT | NOT NULL, DEFAULT 0 | 0=Active, 1=FullyRedeemed, 2=Expired, 3=Cancelled |
| `issued_at_store_id` | BIGINT | NOT NULL, FK → pos.Stores | Store where the credit note was issued |
| `issued_by` | BIGINT | NOT NULL, FK → auth.Users | Cashier/supervisor who issued |
| `issued_at` | DATETIME2(2) | NOT NULL, DEFAULT SYSUTCDATETIME() | Issuance timestamp |
| `expires_at` | DATETIME2(2) | NULL | Expiration date. NULL = never expires |
| `redeemed_amount` | DECIMAL(18,4) | NOT NULL, DEFAULT 0 | Running total of all redemptions |
| `last_used_at` | DATETIME2(2) | NULL | Last redemption timestamp |
| `last_used_at_store_id` | BIGINT | NULL, FK → pos.Stores | Last store where it was used |
| `note` | NVARCHAR(300) | NULL | Issuance note |
| `local_id` | VARCHAR(50) | NULL | PWA-generated UUID for offline creation + dedup |
| `is_synced` | BIT | NOT NULL, DEFAULT 0 | Whether synced to backend |
| `created_at` | DATETIME2(2) | NOT NULL, DEFAULT SYSUTCDATETIME() | |
| `modified_at` | DATETIME2(2) | NOT NULL, DEFAULT SYSUTCDATETIME() | |
| `row_version` | ROWVERSION | NOT NULL | Optimistic concurrency |

**Indexes:**
- PK: `credit_note_id`
- UQ: `(org_id, credit_note_number)` — unique credit note number per tenant
- UQ: `(org_id, local_id)` WHERE local_id IS NOT NULL — offline dedup
- IX: `(customer_id, status)` — customer's active credit notes
- IX: `(org_id, status, expires_at)` — expiry job scan
- IX: `(return_order_id)` WHERE return_order_id IS NOT NULL — linked return order

**Credit Note Number Generation (Backend):**

```csharp
var today = DateOnly.FromDateTime(DateTime.UtcNow);
var seq = await _db.CreditNotes
    .Where(cn => cn.IssuedAtStoreId == storeId 
           && cn.CreatedAt.Date == today.ToDateTime(TimeOnly.MinValue))
    .CountAsync() + 1;
return $"CN-{storeCode}-{today:yyyyMMdd}-{seq:D4}";
```

**Credit Note Lifecycle:**

```
Issue (status = Active)
    │
    ├── Partial Redeem → remaining_balance decremented, status stays Active
    │
    ├── Full Redeem → remaining_balance = 0, status = FullyRedeemed (1)
    │
    ├── Expires (Hangfire job) → status = Expired (2), remaining_balance preserved
    │
    └── Admin Cancel → status = Cancelled (3), remaining_balance preserved
```

---

## 6. C5 — Credit Note Transactions

### 6.1 Rationale

Every change to a credit note's balance is logged in a transaction table for audit trail, reconciliation, and dispute resolution.

### 6.2 Entity

#### 6.2.1 `pos.CreditNoteTransactions`

| Column | Type | Constraints | Description |
|---|---|---|---|
| `transaction_id` | BIGINT | PK, IDENTITY | Auto-increment PK |
| `org_id` | BIGINT | NOT NULL, FK → tenant.Organizations | Tenant |
| `credit_note_id` | BIGINT | NOT NULL, FK → pos.CreditNotes | Parent credit note |
| `transaction_type` | TINYINT | NOT NULL | 0=Issue, 1=Redeem, 2=Expire, 3=Cancel, 4=Adjust |
| `amount` | DECIMAL(18,4) | NOT NULL | Positive for Issue/Adjust-up, negative for Redeem/Adjust-down |
| `balance_after` | DECIMAL(18,4) | NOT NULL | Credit note remaining_balance after this transaction |
| `pos_order_id` | BIGINT | NULL, FK → pos.PosOrders | The order this transaction relates to (issue = return order, redeem = sale order) |
| `store_id` | BIGINT | NOT NULL, FK → pos.Stores | Store where transaction occurred |
| `performed_by` | BIGINT | NOT NULL, FK → auth.Users | User who performed |
| `performed_at` | DATETIME2(2) | NOT NULL, DEFAULT SYSUTCDATETIME() | |
| `note` | NVARCHAR(300) | NULL | Transaction note |
| `local_id` | VARCHAR(50) | NULL | Offline dedup ID |
| `is_synced` | BIT | NOT NULL, DEFAULT 0 | |
| `created_at` | DATETIME2(2) | NOT NULL, DEFAULT SYSUTCDATETIME() | |

**Indexes:**
- PK: `transaction_id`
- IX: `(credit_note_id, performed_at)` — transaction history per credit note
- IX: `(pos_order_id)` WHERE pos_order_id IS NOT NULL — transactions by order
- IX: `(store_id, performed_at)` — store transaction log
- UQ: `(org_id, local_id)` WHERE local_id IS NOT NULL — offline dedup

---

## 7. C6 — Payment Method Extension

### 7.1 Rationale

Credit note redemption is a payment method. Adding `CreditNote(6)` to the existing payment method enum allows credit notes to be used alongside cash, card, and other methods in split payments.

### 7.2 PosPayments Modifications

**New column added to `pos.PosPayments`:**

| Column | Type | Constraints | Description |
|---|---|---|---|
| `credit_note_id` | BIGINT | NULL, FK → pos.CreditNotes | When method = CreditNote(6), the credit note being redeemed |

**Expand method CHECK constraint:**

```sql
-- Original (Addendum 38):  method IN (0,1,2,3,4,5)
-- Updated:                  method IN (0,1,2,3,4,5,6)
ALTER TABLE pos.PosPayments DROP CONSTRAINT CK_PosPayments_Method;
ALTER TABLE pos.PosPayments ADD CONSTRAINT CK_PosPayments_Method CHECK (method IN (0,1,2,3,4,5,6));
```

**Updated Payment Method Enum:**

| Value | Name | Description |
|---|---|---|
| 0 | Cash | Cash payment |
| 1 | Card | Credit/debit card |
| 2 | MobileWallet | Mobile/digital wallet |
| 3 | BankTransfer | Direct bank transfer |
| 4 | Credit | On-account (customer AR) |
| 5 | Cheque | Cheque payment |
| **6** | **CreditNote** | **Store credit note redemption** |

**New index:**
- IX: `(credit_note_id)` WHERE credit_note_id IS NOT NULL — credit note payment lookup

### 7.3 Shift Modifications

**New columns added to `pos.Shifts`:**

| Column | Type | Constraints | Description |
|---|---|---|---|
| `total_credit_note_issued` | DECIMAL(18,4) | NOT NULL, DEFAULT 0 | Total value of credit notes issued as refunds during this shift |
| `total_credit_note_redeemed` | DECIMAL(18,4) | NOT NULL, DEFAULT 0 | Total value of credit notes redeemed as payment during this shift |

**Updated Expected Cash Formula (no change):**

Credit note transactions do not affect the cash drawer. The expected cash formula from Addendum 38 remains unchanged:

```
expected_cash_closing = opening_float + total_cash_sales + total_cash_in 
                      - total_cash_out - total_cash_refunds
```

Credit notes operate outside the cash flow — issuance creates a liability, redemption clears it.

---

## 8. C7 — Same-Store Return Flow

### 8.1 Rationale

The most common return scenario: a customer bought items at Store A and returns them at Store A. The original order is available locally (in Dexie if offline, or via server API if online). This flow covers full returns, partial returns, and the refund settlement.

### 8.2 Return Processing Flow

```
Cashier: "I want to return items"
       │
       ▼
┌──────────────────────────────────┐
│  1. ORDER LOOKUP                  │
│                                   │
│  Scan receipt barcode  ─── OR ─── │
│  Enter receipt number             │
│  Search by customer               │
│                                   │
│  → Fetch original order           │
│    (local DB if offline,          │
│     server API if online)         │
└──────────┬───────────────────────┘
           │
           ▼
┌──────────────────────────────────┐
│  2. POLICY CHECK                  │
│                                   │
│  a. Is return_window_days set?    │
│     YES → Is original order       │
│            within window?          │
│       NO  → "Return window expired │
│              (30 days). Manager    │
│              override required."   │
│                                   │
│  b. Is order fully returned?      │
│     YES → "All items already      │
│            returned."  BLOCK      │
│                                   │
│  c. Is order voided?              │
│     YES → "Cannot return voided   │
│            order."  BLOCK         │
└──────────┬───────────────────────┘
           │
           ▼
┌──────────────────────────────────┐
│  3. SELECT ITEMS TO RETURN        │
│                                   │
│  Show original order lines with:  │
│  • Product name, qty, price       │
│  • returned_qty (already returned)│
│  • returnable_qty (available)     │
│                                   │
│  Cashier selects lines + qty      │
│  to return (max = returnable_qty) │
└──────────┬───────────────────────┘
           │
           ▼
┌──────────────────────────────────┐
│  4. RETURN REASON                 │
│                                   │
│  Select reason from dropdown      │
│  (pos.ReturnReasons where         │
│   applies_to IN (0,1))            │
│                                   │
│  If reason.requires_note:         │
│    Show note text field (required) │
│                                   │
│  If reason.requires_manager:      │
│    Prompt for manager PIN         │
└──────────┬───────────────────────┘
           │
           ▼
┌──────────────────────────────────┐
│  5. AUTHORIZATION CHECK           │
│                                   │
│  a. Return amount > policy.       │
│     max_return_no_auth?           │
│     YES → Manager PIN required    │
│                                   │
│  b. Return reason requires mgr?  │
│     YES → Manager PIN required    │
│                                   │
│  Manager enters PIN:              │
│    → Verify pos_role = Supervisor │
│    → Record authorized_by,        │
│      authorized_at                │
└──────────┬───────────────────────┘
           │
           ▼
┌──────────────────────────────────┐
│  6. RESTOCKING FEE CALCULATION    │
│                                   │
│  fee_pct = reason.restocking_fee_ │
│            override ?? policy.    │
│            restocking_fee_pct     │
│                                   │
│  If fee_pct > 0:                  │
│    restocking_fee = return_subtotal│
│                   × (fee_pct/100) │
│    Show: "Restocking fee (5%):    │
│           -$X.XX"                 │
│    refund_amount = return_total   │
│                  - restocking_fee │
└──────────┬───────────────────────┘
           │
           ▼
┌──────────────────────────────────┐
│  7. REFUND METHOD                 │
│                                   │
│  Options (per policy):            │
│  ┌─────────────────────────────┐  │
│  │ [💵 Cash]  ← allowed?       │  │
│  │ [💳 Original Method]        │  │
│  │ [🎫 Credit Note]            │  │
│  └─────────────────────────────┘  │
│                                   │
│  If policy.refund_to_original_    │
│  method = true:                   │
│    Auto-select original method.   │
│    Show info: "Refund to original │
│    payment method (Card ****4242)"│
│                                   │
│  If Cash selected:                │
│    Cash refund from register      │
│                                   │
│  If CreditNote selected:          │
│    Create pos.CreditNotes record  │
│    Display/print credit note      │
└──────────┬───────────────────────┘
           │
           ▼
┌──────────────────────────────────┐
│  8. CREATE RETURN ORDER           │
│                                   │
│  PosOrder:                        │
│    order_type = RETURN (1)        │
│    original_order_id = original.id│
│    return_reason_id = selected    │
│    return_reason = free-text note │
│    authorized_by = manager (if)   │
│    restocking_fee_amount = fee    │
│    status = Completed (1)         │
│                                   │
│  PosOrderLines (negative qty):    │
│    quantity = -return_qty         │
│    is_return_line = true          │
│    original_line_id = orig.line_id│
│    (prices snapshot from original)│
│                                   │
│  PosPayments (refund):            │
│    is_refund = true               │
│    amount = refund_amount         │
│    method = selected method       │
│    credit_note_id = (if CN)       │
│                                   │
│  → Update original lines:         │
│    returned_qty += return_qty     │
│                                   │
│  → Update original order status:  │
│    Returned(3) or PartialReturn(4)│
│                                   │
│  → Update shift totals:           │
│    total_cash_refunds (if cash)   │
│    total_card_refunds (if card)   │
│    total_credit_note_issued (if CN)│
│    refund_count++                 │
│                                   │
│  → Update cashier session:        │
│    refund_total += refund_amount  │
└──────────┬───────────────────────┘
           │
           ▼
┌──────────────────────────────────┐
│  9. POST-RETURN ACTIONS           │
│                                   │
│  a. Stock return:                 │
│     PosStockService.ReturnStockAsync│
│     → StockMovement (POS_RETURN,  │
│       direction=IN, warehouse =   │
│       current store's warehouse)  │
│                                   │
│  b. Finance:                      │
│     PosJournalService             │
│     .CreateReturnJournalAsync     │
│     → Reverse journal entry       │
│                                   │
│  c. If credit note issued:        │
│     CreditNoteTransaction(Issue)  │
│                                   │
│  d. Receipt: Generate return      │
│     receipt with original ref     │
└──────────────────────────────────┘
```

### 8.3 Service Implementation

```csharp
public class PosReturnService
{
    public async Task<ReturnResult> ProcessReturnAsync(ProcessReturnDto dto)
    {
        // 1. Load original order with lines
        var original = await _db.PosOrders
            .Include(o => o.Lines)
            .Include(o => o.Store)
            .FirstOrDefaultAsync(o => o.OrderId == dto.OriginalOrderId 
                                   && o.OrgId == _tenant.OrgId);
        
        if (original == null) throw new NotFoundException("Original order not found");
        if (original.Status == OrderStatus.Voided) 
            throw new BusinessException("Cannot return a voided order");
        if (original.Status == OrderStatus.Returned)
            throw new BusinessException("Order is already fully returned");
        
        // 2. Get effective return policy
        var policy = await _policyService.GetEffectivePolicyAsync(
            _tenant.OrgId, dto.ProcessingStoreId);
        
        // 3. Check return window
        if (policy.ReturnWindowDays.HasValue)
        {
            var deadline = original.CreatedAt.AddDays(policy.ReturnWindowDays.Value);
            if (DateTime.UtcNow > deadline && !dto.ManagerOverride)
                throw new PolicyException("Return window expired", 
                    requiresManagerOverride: true);
        }
        
        // 4. Validate return lines
        foreach (var returnLine in dto.ReturnLines)
        {
            var originalLine = original.Lines
                .First(l => l.LineId == returnLine.OriginalLineId);
            var returnable = originalLine.Quantity - originalLine.ReturnedQty;
            if (returnLine.Quantity > returnable)
                throw new BusinessException(
                    $"Cannot return {returnLine.Quantity} of '{originalLine.ProductName}'. " +
                    $"Only {returnable} returnable.");
        }
        
        // 5. Calculate restocking fee
        var reason = await _db.ReturnReasons.FindAsync(dto.ReturnReasonId);
        var feePct = reason?.RestockingFeeOverride ?? policy.RestockingFeePct;
        var returnSubtotal = CalculateReturnSubtotal(dto.ReturnLines, original);
        var restockingFee = returnSubtotal * (feePct / 100m);
        var refundAmount = returnSubtotal - restockingFee;
        
        // 6. Authorization check
        if (RequiresAuthorization(refundAmount, policy, reason) && !dto.ManagerOverride)
            throw new PolicyException("Manager authorization required",
                requiresManagerOverride: true);
        
        // 7. Create return order (transaction scope)
        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            var returnOrder = CreateReturnOrder(original, dto, refundAmount, restockingFee);
            _db.PosOrders.Add(returnOrder);
            
            // Update original line returned_qty
            foreach (var rl in dto.ReturnLines)
            {
                var origLine = original.Lines.First(l => l.LineId == rl.OriginalLineId);
                origLine.ReturnedQty += rl.Quantity;
            }
            
            // Update original order status
            UpdateOriginalOrderStatus(original);
            
            // Credit note (if selected)
            CreditNote creditNote = null;
            if (dto.RefundMethod == RefundMethod.CreditNote)
            {
                creditNote = await _creditNoteService.IssueCreditNoteAsync(
                    original.CustomerId, refundAmount, returnOrder, dto.ProcessingStoreId);
                returnOrder.CreditNoteId = creditNote.CreditNoteId;
            }
            
            // Payment (refund)
            var refundPayment = CreateRefundPayment(returnOrder, dto.RefundMethod, 
                refundAmount, creditNote);
            _db.PosPayments.Add(refundPayment);
            
            await _db.SaveChangesAsync();
            
            // Stock return
            await _stockService.ReturnStockAsync(returnOrder);
            
            // Finance journal
            await _journalService.CreateReturnJournalAsync(returnOrder);
            
            // Shift + session totals
            await _shiftService.UpdateReturnTotalsAsync(returnOrder);
            
            await tx.CommitAsync();
            
            return new ReturnResult(returnOrder, creditNote);
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }
}
```

---

## 9. C8 — Cross-Store Return Flow

### 9.1 Rationale

Customers expect to return items at any branch of a retail chain. Cross-store returns require the original order to be looked up from the central server (the returning store doesn't have it locally). Stock goes into the returning store's warehouse. Financial reconciliation requires inter-store journal entries.

### 9.2 Cross-Store Differences

| Aspect | Same-Store Return | Cross-Store Return |
|---|---|---|
| Order lookup | Local Dexie DB or server | Server only (online required) |
| Policy check | Processing store's policy | Processing store's `allow_cross_store_return` |
| Authorization | Per policy thresholds | Always requires manager (if policy says so) |
| Stock return | → Original store's warehouse | → Processing store's warehouse |
| Finance | Simple reversal | Inter-store reconciliation JE |
| Offline | ⚠ If order in local DB | ❌ Always requires online |

### 9.3 Order Lookup API

```
GET /api/pos/orders/lookup?receipt={orderNumber}

Response:
{
  "found": true,
  "order": {
    "orderId": 1052,
    "orderNumber": "POS-STR001-20261005-0042",
    "storeId": 1,
    "storeName": "Downtown Store",
    "storeCode": "STR001",
    "createdAt": "2026-10-05T14:30:00Z",
    "grandTotal": 150.00,
    "customerId": 45,
    "customerName": "Sarah Mitchell",
    "status": "Completed",
    "isSameStore": false,
    "daysSincePurchase": 6,
    "lines": [
      {
        "lineId": 2001,
        "productId": 42,
        "productName": "Arabica Coffee 250g",
        "productSku": "COF-ARB-250",
        "quantity": 3,
        "returnedQty": 0,
        "returnableQty": 3,
        "unitPrice": 14.99,
        "lineTotal": 44.97
      },
      {
        "lineId": 2002,
        "productId": 55,
        "productName": "Croissant Butter",
        "productSku": "BAK-CRO-001",
        "quantity": 2,
        "returnedQty": 1,
        "returnableQty": 1,
        "unitPrice": 3.50,
        "lineTotal": 7.00
      }
    ]
  },
  "policy": {
    "withinReturnWindow": true,
    "returnWindowDays": 30,
    "crossStoreAllowed": true,
    "crossStoreRequiresManager": true,
    "restockingFeePct": 0,
    "allowCashRefund": true,
    "allowCreditNoteRefund": true
  }
}
```

### 9.4 Cross-Store Stock Movement

```
Cross-Store Return at Store B (original sale at Store A):

For each return line:
    PosStockService.ReturnStockAsync(
        warehouseId: StoreB.WarehouseId,    // ← stock goes to PROCESSING store
        productId:   line.ProductId,
        quantity:    |line.Quantity|,
        reference:   $"RET-{returnOrder.OrderNumber}",
        movementType: StockMovementType.PosReturn
    )
    
    → Creates inventory.StockMovements:
        warehouse_id = Store B's warehouse
        movement_type = 'POS_RETURN'
        direction = 'IN'
        reference_note = "Cross-store return. Original: POS-STR001-20261005-0042 (Downtown Store)"

Note: Store A's warehouse is NOT adjusted. The item is now physically 
at Store B. If the business wants to rebalance stock between warehouses, 
that's an inter-warehouse transfer (existing INVENTORY module flow) — 
a separate manual or scheduled process.
```

### 9.5 Cross-Store Financial Reconciliation

See [C11 — Return Finance Integration](#12-c11--return-finance-integration) for the inter-store journal entry pattern.

---

## 10. C9 — Exchange Transaction Flow

### 10.1 Rationale

An exchange is a return and a new purchase combined into one transaction. The customer returns items and picks new items in a single flow, paying (or receiving refund for) only the net difference. This is handled as a single PosOrder with `order_type = EXCHANGE(2)`, containing both negative (return) and positive (new sale) lines.

### 10.2 Exchange Flow

```
Cashier: "Customer wants to exchange items"
       │
       ▼
  Steps 1–5: Same as return flow (lookup, policy, select items, reason, auth)
       │
       ▼
┌──────────────────────────────────────┐
│  6. ADD NEW ITEMS                     │
│                                       │
│  After selecting items to return,     │
│  the cashier adds new items to the    │
│  cart from the product grid.          │
│                                       │
│  Cart shows:                          │
│  ─────────────────────────────────    │
│  RETURNING:                           │
│    Blue Shirt    ×−1    −$25.00       │
│    Belt          ×−1    −$12.00       │
│                                       │
│  NEW ITEMS:                           │
│    Red Shirt     ×+1    +$28.00       │
│    Jacket        ×+1    +$40.00       │
│                                       │
│  ─────────────────────────────────    │
│  Return subtotal:        −$37.00      │
│  Restocking fee (5%):    +$1.85       │
│  New items subtotal:     +$68.00      │
│  Tax on new items (10%): +$6.80       │
│  ─────────────────────────────────    │
│  NET AMOUNT:             +$39.65      │
│  (Customer pays $39.65)               │
└──────────┬───────────────────────────┘
           │
           ▼
┌──────────────────────────────────────┐
│  7. NET SETTLEMENT                    │
│                                       │
│  Three scenarios:                     │
│                                       │
│  A) Net > 0 (customer pays more):     │
│     → Normal payment screen           │
│     → Cash/Card/CreditNote accepted   │
│                                       │
│  B) Net = 0 (even exchange):          │
│     → No payment needed               │
│     → "Even exchange" confirmation    │
│                                       │
│  C) Net < 0 (customer gets refund):   │
│     → Refund method selection          │
│     → Cash/Original/CreditNote        │
│                                       │
│  Note: Split payment on exchanges     │
│  is allowed (e.g., pay difference     │
│  part cash, part credit note)         │
└──────────┬───────────────────────────┘
           │
           ▼
┌──────────────────────────────────────┐
│  8. CREATE EXCHANGE ORDER             │
│                                       │
│  PosOrder:                            │
│    order_type = EXCHANGE (2)          │
│    original_order_id = original.id    │
│    return_reason_id = selected        │
│    subtotal = new_items_subtotal      │
│    grand_total = net_amount           │
│    restocking_fee_amount = fee        │
│                                       │
│  PosOrderLines:                       │
│    Line 1: Blue Shirt  qty=−1  (ret) │
│      is_return_line = true            │
│      original_line_id = orig.line_id  │
│    Line 2: Belt        qty=−1  (ret) │
│      is_return_line = true            │
│    Line 3: Red Shirt   qty=+1  (new) │
│      is_return_line = false           │
│    Line 4: Jacket      qty=+1  (new) │
│      is_return_line = false           │
│                                       │
│  PosPayments:                         │
│    If net > 0: normal payment         │
│    If net < 0: refund payment         │
│    If net = 0: no payment record      │
│                                       │
│  → Update original returned_qty      │
│  → Update original order status       │
│  → Stock: return IN + sale OUT        │
│  → Finance: single combined JE       │
└──────────────────────────────────────┘
```

### 10.3 Exchange Net Amount Calculation

```
return_subtotal  = SUM(return_lines: |qty| × unit_price)
restocking_fee   = return_subtotal × (fee_pct / 100)
net_return_value = return_subtotal - restocking_fee

new_subtotal     = SUM(new_lines: qty × unit_price)
new_discount     = (order discount applied to new lines only)
new_tax          = SUM(new_lines: tax_amount)
new_total        = new_subtotal - new_discount + new_tax

net_amount       = new_total - net_return_value

If net_amount > 0 → customer pays
If net_amount < 0 → customer gets refund of |net_amount|
If net_amount = 0 → even exchange
```

### 10.4 Exchange Stock Movement

An exchange generates **two** sets of stock movements within the same transaction:

```
For each RETURN line (is_return_line = true):
    StockMovement: direction=IN, type=POS_RETURN, qty=|line.qty|
    → Stock returned to processing store's warehouse

For each NEW SALE line (is_return_line = false):
    StockMovement: direction=OUT, type=POS_SALE, qty=line.qty
    → Stock deducted from processing store's warehouse
```

---

## 11. C10 — Return & Credit Note PWA Integration

### 11.1 Dexie.js Schema Extensions

```typescript
// New tables added to PosDatabase (extending Addendum 38 schema)
class PosDatabase extends Dexie {
  // ... existing tables from Addendum 38 ...
  returnReasons!: Table<LocalReturnReason>;
  creditNotes!: Table<LocalCreditNote>;

  constructor() {
    super('sms-pos');
    this.version(2).stores({
      // existing stores unchanged ...
      products: 'productId, barcode, categoryId, *name, isFavorite, modifiedAt',
      customers: 'customerId, code, *name, *phone, modifiedAt',
      orders: 'localId, serverId, shiftId, sessionId, syncStatus, createdAt',
      config: 'key',
      syncLog: '++id, type, timestamp',
      // NEW tables for Addendum 39:
      returnReasons: 'returnReasonId, code, sortOrder, modifiedAt',
      creditNotes: 'creditNoteId, creditNoteNumber, customerId, status, modifiedAt'
    });
  }
}
```

```typescript
interface LocalReturnReason {
  returnReasonId: number;
  code: string;
  name: string;
  description?: string;
  requiresNote: boolean;
  requiresManager: boolean;
  appliesTo: number;      // 0=Both, 1=ReturnOnly, 2=ExchangeOnly
  restockingFeeOverride?: number;
  sortOrder: number;
  modifiedAt: string;
}

interface LocalCreditNote {
  creditNoteId: number;
  creditNoteNumber: string;
  customerId?: number;
  originalAmount: number;
  remainingBalance: number;
  status: number;          // 0=Active, 1=Redeemed, 2=Expired, 3=Cancelled
  issuedAtStoreName: string;
  issuedAt: string;
  expiresAt?: string;
  modifiedAt: string;
}
```

### 11.2 SyncEngine Extensions

```typescript
class SyncEngine {
  // ... existing methods from Addendum 38 ...
  
  async pullReturnReasons(): Promise<void> {
    const since = await this.getLastSync('returnReasons');
    const response = await api.get(`/api/sync/pos-return-reasons?since=${since}`);
    
    await posDb.returnReasons.bulkPut(
      response.data.reasons.map(mapReturnReason)
    );
    await this.setLastSync('returnReasons', response.data.syncTimestamp);
  }
  
  async pullCreditNotes(customerId?: number): Promise<void> {
    // Only sync credit notes for the currently selected customer (on-demand)
    if (!customerId) return;
    
    const response = await api.get(
      `/api/pos/customers/${customerId}/credit-notes?status=active`
    );
    
    await posDb.creditNotes.bulkPut(
      response.data.creditNotes.map(mapCreditNote)
    );
  }
  
  async pullChanges(): Promise<void> {
    // Extend existing pullChanges from Addendum 38
    const since = this.lastSyncTimestamp || '1970-01-01T00:00:00Z';
    
    const [catalog, customers, reasons] = await Promise.all([
      api.get(`/api/sync/catalog?since=${since}`),
      api.get(`/api/sync/customers?since=${since}`),
      api.get(`/api/sync/pos-return-reasons?since=${since}`)  // NEW
    ]);
    
    await posDb.transaction('rw', 
      posDb.products, posDb.customers, posDb.returnReasons,
      async () => {
        // ... existing upserts ...
        if (reasons.data.reasons.length > 0) {
          await posDb.returnReasons.bulkPut(
            reasons.data.reasons.map(mapReturnReason)
          );
        }
      }
    );
  }
}
```

### 11.3 Offline Capability Matrix (Return-Specific)

| Feature | Online | Offline | Notes |
|---|---|---|---|
| Same-store return | ✅ | ⚠ | Only if original order exists in local Dexie DB |
| Cross-store return | ✅ | ❌ | Requires server lookup |
| Same-store exchange | ✅ | ⚠ | Same as same-store return |
| Cross-store exchange | ✅ | ❌ | Requires server lookup |
| Return reason selection | ✅ | ✅ | Reasons cached in Dexie |
| Manager PIN for auth | ✅ Server verify | ✅ Cached hash | Same as Addendum 38 PIN model |
| Cash refund | ✅ | ✅ | Recorded locally |
| Card refund | ✅ | ✅ | Recorded locally (manual) |
| Credit note issuance | ✅ | ✅ | Created locally, synced later. Offline CN number: `CN-{store}-OFF-{uuid_short}` |
| Credit note redemption | ✅ | ❌ | Requires real-time balance verification to prevent double-spend |
| Return receipt | ✅ | ✅ | Generated locally |
| Credit note display/print | ✅ | ✅ | From local data |

### 11.4 PWA Application Routes (New)

| Route | Screen | Auth |
|---|---|---|
| `/pos/return` | Order lookup for return | Active cashier session + `pos.order.return` |
| `/pos/return/{orderId}` | Return item selection + reason | Active cashier session |
| `/pos/return/{orderId}/refund` | Refund method selection | Active cashier session |
| `/pos/exchange/{orderId}` | Exchange flow (return items + add new items) | Active cashier session |
| `/pos/credit-note/{id}` | Credit note display/print | Active cashier session |
| `/pos/history` | Order history with return filter (extended) | Supervisor |

---

## 12. C11 — Return Finance Integration

### 12.1 Same-Store Return Journal

When a return is processed at the same store as the original sale, the journal entry reverses the original:

```
Same-Store Return Order #POS-STR001-20261010-R003
Return Total: $50.00 (Subtotal $45.00 + Tax $5.00)
Restocking Fee: $0.00
Refund: Cash $50.00

Journal Entry:
  ┌──────────────────────────────────────────────────────────┐
  │ Date: 2026-10-10                                          │
  │ Reference: POS-STR001-20261010-R003                       │
  │ Source: POS Return                                        │
  │                                                           │
  │ Debit                              │  Credit              │
  │ ─────────────────────────────────────────────────────────│
  │ Sales Revenue              45.00   │                      │
  │ Tax Payable                 5.00   │                      │
  │                                    │ Cash Account    50.00│
  └──────────────────────────────────────────────────────────┘
```

### 12.2 Same-Store Return with Restocking Fee

```
Return Total: $100.00 (Subtotal $90.00 + Tax $10.00)
Restocking Fee (5%): $5.00
Net Refund: $95.00 (Cash)

Journal Entry:
  ┌──────────────────────────────────────────────────────────┐
  │ Debit                              │  Credit              │
  │ ─────────────────────────────────────────────────────────│
  │ Sales Revenue              90.00   │                      │
  │ Tax Payable                10.00   │                      │
  │                                    │ Cash Account    95.00│
  │                                    │ Restocking Fee   5.00│
  │                                    │  (Other Income)      │
  └──────────────────────────────────────────────────────────┘
```

### 12.3 Return with Credit Note Issuance

```
Return Total: $75.00 (Subtotal $68.00 + Tax $7.00)
Refund: Credit Note CN-STR001-20261010-0001

Journal Entry:
  ┌──────────────────────────────────────────────────────────┐
  │ Debit                              │  Credit              │
  │ ─────────────────────────────────────────────────────────│
  │ Sales Revenue              68.00   │                      │
  │ Tax Payable                 7.00   │                      │
  │                                    │ Credit Note     75.00│
  │                                    │  Liability           │
  │                                    │ (Current Liabilities)│
  └──────────────────────────────────────────────────────────┘

When Credit Note Redeemed on future purchase:
  ┌──────────────────────────────────────────────────────────┐
  │ Debit                              │  Credit              │
  │ ─────────────────────────────────────────────────────────│
  │ Credit Note Liability      75.00   │                      │
  │                                    │ Sales Revenue   68.18│
  │                                    │ Tax Payable      6.82│
  └──────────────────────────────────────────────────────────┘
```

### 12.4 Cross-Store Return Journal (Inter-Store Reconciliation)

When Store B processes a return for a sale originally made at Store A:

```
Cross-Store Return at Store B
Original Sale: POS-STR001-20261005-0042 at Store A ($50.00)
Return Processed at Store B. Refund: Cash $50.00

Two Journal Entries:

JE 1: At Store B (processing store) — cash leaves register
  ┌──────────────────────────────────────────────────────────┐
  │ Cost Center: Store B                                      │
  │                                                           │
  │ Debit                              │  Credit              │
  │ ─────────────────────────────────────────────────────────│
  │ Inter-Store Receivable     50.00   │                      │
  │   (due from Store A)               │ Cash Account    50.00│
  │                                    │   (Store B cash)     │
  └──────────────────────────────────────────────────────────┘

JE 2: At Store A (original store) — revenue reversal
  ┌──────────────────────────────────────────────────────────┐
  │ Cost Center: Store A                                      │
  │                                                           │
  │ Debit                              │  Credit              │
  │ ─────────────────────────────────────────────────────────│
  │ Sales Revenue              45.00   │                      │
  │ Tax Payable                 5.00   │                      │
  │                                    │ Inter-Store     50.00│
  │                                    │  Payable             │
  │                                    │  (due to Store B)    │
  └──────────────────────────────────────────────────────────┘

The Inter-Store Receivable (B) and Inter-Store Payable (A) net to zero
during periodic inter-store reconciliation.
```

### 12.5 Exchange Journal Entry

An exchange generates a single combined journal entry:

```
Exchange at Store A
Return: Blue Shirt ($25.00 subtotal + $2.50 tax = $27.50)
New:    Red Shirt  ($28.00 subtotal + $2.80 tax = $30.80)
Net:    Customer pays $3.30 (Cash)

Journal Entry:
  ┌──────────────────────────────────────────────────────────┐
  │ Debit                              │  Credit              │
  │ ─────────────────────────────────────────────────────────│
  │ Cash Account               3.30   │                      │
  │ Sales Revenue (return)    25.00   │                      │
  │ Tax Payable (return)       2.50   │                      │
  │                                    │ Sales Revenue   28.00│
  │                                    │   (new sale)         │
  │                                    │ Tax Payable      2.80│
  │                                    │   (new sale)         │
  └──────────────────────────────────────────────────────────┘

  Net effect:
    Cash +$3.30 | Revenue +$3.00 | Tax +$0.30
```

### 12.6 Account Mapping Extension

New GL accounts needed in POS account mapping (store configuration):

| Account | Type | Description |
|---|---|---|
| Credit Note Liability | Current Liability | Outstanding credit note balances |
| Restocking Fee Income | Other Income | Revenue from restocking fees |
| Inter-Store Receivable | Current Asset | Due from other stores (cross-store returns) |
| Inter-Store Payable | Current Liability | Due to other stores (cross-store returns) |

```csharp
public class PosAccountMapping
{
    // Existing (from Addendum 38):
    public long SalesRevenueAccountId { get; set; }
    public long TaxPayableAccountId { get; set; }
    public long CostOfGoodsSoldAccountId { get; set; }
    public Dictionary<PaymentMethod, long> PaymentAccountMap { get; set; }
    
    // New (Addendum 39):
    public long CreditNoteLiabilityAccountId { get; set; }
    public long RestockingFeeIncomeAccountId { get; set; }
    public long InterStoreReceivableAccountId { get; set; }
    public long InterStorePayableAccountId { get; set; }
}
```

---

## 13. C12 — SignalR Return Events

### 13.1 New Server-to-Client Events

Added to `PosHub` (extending Addendum 38 §12):

| Event | Group | Payload | Trigger |
|---|---|---|---|
| `ReturnProcessed` | `store_{id}` | `{ returnOrderId, originalOrderId, originalStoreId, refundAmount, refundMethod }` | Return order completed |
| `CreditNoteIssued` | `store_{id}` | `{ creditNoteId, creditNoteNumber, customerId, amount, expiresAt }` | Credit note created |
| `CreditNoteRedeemed` | `store_{id}` | `{ creditNoteId, redeemedAmount, remainingBalance, orderId }` | Credit note used in payment |
| `CreditNoteExpired` | `store_{id}` | `{ creditNoteId, creditNoteNumber, customerId, expiredAmount }` | Hangfire expiry job |
| `ReturnPolicyChanged` | `store_{id}` | `{ storeId, policySnapshot }` | Admin updates return policy |

### 13.2 Hangfire Jobs (New)

| Job | Schedule | Description |
|---|---|---|
| `CreditNoteExpiryJob` | Daily 00:30 UTC | Scans `pos.CreditNotes` where `status = Active AND expires_at < GETUTCDATE()`. Sets status = Expired. Creates CreditNoteTransaction(Expire). Sends `CreditNoteExpired` SignalR event. |
| `CrossStoreReconciliationReport` | Weekly (Monday 06:00 UTC) | Generates inter-store receivable/payable aging report for all stores with outstanding cross-store return balances. Sends notification to Finance module. |

---

## 14. Database Migrations

| Migration | Description | Schema | Depends On |
|---|---|---|---|
| M039.1 | Create `pos.ReturnReasons` table | pos | — |
| M039.2 | Create `pos.ReturnPolicies` table | pos | — |
| M039.3 | ALTER `pos.Stores` — add `return_policy_id` FK | pos | M039.2 |
| M039.4 | ALTER `pos.PosOrders` — add return extension columns | pos | M039.1 |
| M039.5 | ALTER `pos.PosOrderLines` — add `returned_qty` | pos | — |
| M039.6 | ALTER `pos.PosPayments` — add `credit_note_id`, expand method CHECK | pos | — |
| M039.7 | ALTER `pos.Shifts` — add credit note totals | pos | — |
| M039.8 | Seed default return reasons | pos | M039.1 |
| M039.9 | Seed default return policy (org-wide) | pos | M039.2 |
| M039.10 | Create `pos.CreditNotes` table | pos | — |
| M039.11 | Create `pos.CreditNoteTransactions` table | pos | M039.10 |
| M039.12 | Seed return/credit-note permissions in `auth.Permissions` | auth | — |
| M039.13 | Add `POS_CREDIT_NOTES` + `POS_CROSS_STORE_RETURNS` to `tenant.ModuleFeatures` | tenant | — |
| M039.14 | Create `pos.vw_ReturnableOrderLines` view + return-specific indexes | pos | M039.4, M039.5 |

### Migration SQL — Key Migrations

#### M039.1 — Return Reasons Table

```sql
CREATE TABLE pos.ReturnReasons (
    return_reason_id       BIGINT IDENTITY(1,1) PRIMARY KEY,
    org_id                 BIGINT         NOT NULL,
    code                   VARCHAR(20)    NOT NULL,
    name                   NVARCHAR(100)  NOT NULL,
    description            NVARCHAR(300)  NULL,
    requires_note          BIT            NOT NULL DEFAULT 0,
    requires_manager       BIT            NOT NULL DEFAULT 0,
    applies_to             TINYINT        NOT NULL DEFAULT 0,
    restocking_fee_override DECIMAL(5,2)  NULL,
    sort_order             INT            NOT NULL DEFAULT 0,
    is_active              BIT            NOT NULL DEFAULT 1,
    created_at             DATETIME2(2)   NOT NULL DEFAULT SYSUTCDATETIME(),
    created_by             BIGINT         NOT NULL,
    modified_at            DATETIME2(2)   NOT NULL DEFAULT SYSUTCDATETIME(),
    modified_by            BIGINT         NULL,
    CONSTRAINT UQ_ReturnReasons_OrgCode UNIQUE (org_id, code),
    CONSTRAINT FK_ReturnReasons_Org FOREIGN KEY (org_id) REFERENCES tenant.Organizations(org_id),
    CONSTRAINT FK_ReturnReasons_CreatedBy FOREIGN KEY (created_by) REFERENCES auth.Users(user_id),
    CONSTRAINT CK_ReturnReasons_AppliesTo CHECK (applies_to IN (0,1,2))
);

CREATE INDEX IX_ReturnReasons_OrgActive ON pos.ReturnReasons (org_id, is_active, sort_order);
```

#### M039.2 — Return Policies Table

```sql
CREATE TABLE pos.ReturnPolicies (
    return_policy_id            BIGINT IDENTITY(1,1) PRIMARY KEY,
    org_id                      BIGINT         NOT NULL,
    store_id                    BIGINT         NULL,
    name                        NVARCHAR(100)  NOT NULL,
    return_window_days          INT            NULL,
    exchange_window_days        INT            NULL,
    require_receipt             BIT            NOT NULL DEFAULT 1,
    allow_no_receipt_exchange   BIT            NOT NULL DEFAULT 0,
    allow_cross_store_return    BIT            NOT NULL DEFAULT 1,
    cross_store_requires_manager BIT           NOT NULL DEFAULT 1,
    restocking_fee_pct          DECIMAL(5,2)   NOT NULL DEFAULT 0.00,
    max_return_no_auth          DECIMAL(18,4)  NULL,
    refund_to_original_method   BIT            NOT NULL DEFAULT 0,
    allow_cash_refund           BIT            NOT NULL DEFAULT 1,
    allow_credit_note_refund    BIT            NOT NULL DEFAULT 1,
    credit_note_expiry_days     INT            NULL,
    is_active                   BIT            NOT NULL DEFAULT 1,
    created_at                  DATETIME2(2)   NOT NULL DEFAULT SYSUTCDATETIME(),
    created_by                  BIGINT         NOT NULL,
    modified_at                 DATETIME2(2)   NOT NULL DEFAULT SYSUTCDATETIME(),
    modified_by                 BIGINT         NULL,
    row_version                 ROWVERSION     NOT NULL,
    CONSTRAINT FK_ReturnPolicies_Org FOREIGN KEY (org_id) REFERENCES tenant.Organizations(org_id),
    CONSTRAINT FK_ReturnPolicies_Store FOREIGN KEY (store_id) REFERENCES pos.Stores(store_id),
    CONSTRAINT FK_ReturnPolicies_CreatedBy FOREIGN KEY (created_by) REFERENCES auth.Users(user_id)
);

CREATE UNIQUE INDEX UQ_ReturnPolicies_OrgStore 
    ON pos.ReturnPolicies (org_id, store_id) WHERE store_id IS NOT NULL;
CREATE INDEX IX_ReturnPolicies_OrgActive ON pos.ReturnPolicies (org_id, is_active);
```

#### M039.4 — PosOrders Return Extensions

```sql
ALTER TABLE pos.PosOrders ADD
    original_store_id        BIGINT         NULL,
    return_reason_id         BIGINT         NULL,
    authorized_by            BIGINT         NULL,
    authorized_at            DATETIME2(2)   NULL,
    credit_note_id           BIGINT         NULL,
    restocking_fee_amount    DECIMAL(18,4)  NOT NULL DEFAULT 0,
    is_cross_store           BIT            NOT NULL DEFAULT 0;

ALTER TABLE pos.PosOrders ADD
    CONSTRAINT FK_PosOrders_OriginalStore 
        FOREIGN KEY (original_store_id) REFERENCES pos.Stores(store_id),
    CONSTRAINT FK_PosOrders_ReturnReason 
        FOREIGN KEY (return_reason_id) REFERENCES pos.ReturnReasons(return_reason_id),
    CONSTRAINT FK_PosOrders_AuthorizedBy 
        FOREIGN KEY (authorized_by) REFERENCES auth.Users(user_id);
-- CreditNote FK added after M039.10 creates the table

CREATE INDEX IX_PosOrders_OriginalStore 
    ON pos.PosOrders (original_store_id) WHERE original_store_id IS NOT NULL;
CREATE INDEX IX_PosOrders_ReturnReason 
    ON pos.PosOrders (return_reason_id) WHERE return_reason_id IS NOT NULL;
```

#### M039.5 — PosOrderLines returned_qty

```sql
ALTER TABLE pos.PosOrderLines ADD
    returned_qty DECIMAL(18,4) NOT NULL DEFAULT 0;
```

#### M039.10 — Credit Notes Table

```sql
CREATE TABLE pos.CreditNotes (
    credit_note_id           BIGINT IDENTITY(1,1) PRIMARY KEY,
    org_id                   BIGINT         NOT NULL,
    credit_note_number       VARCHAR(30)    NOT NULL,
    customer_id              BIGINT         NULL,
    return_order_id          BIGINT         NULL,
    original_amount          DECIMAL(18,4)  NOT NULL,
    remaining_balance        DECIMAL(18,4)  NOT NULL,
    currency_code            VARCHAR(3)     NOT NULL,
    status                   TINYINT        NOT NULL DEFAULT 0,
    issued_at_store_id       BIGINT         NOT NULL,
    issued_by                BIGINT         NOT NULL,
    issued_at                DATETIME2(2)   NOT NULL DEFAULT SYSUTCDATETIME(),
    expires_at               DATETIME2(2)   NULL,
    redeemed_amount          DECIMAL(18,4)  NOT NULL DEFAULT 0,
    last_used_at             DATETIME2(2)   NULL,
    last_used_at_store_id    BIGINT         NULL,
    note                     NVARCHAR(300)  NULL,
    local_id                 VARCHAR(50)    NULL,
    is_synced                BIT            NOT NULL DEFAULT 0,
    created_at               DATETIME2(2)   NOT NULL DEFAULT SYSUTCDATETIME(),
    modified_at              DATETIME2(2)   NOT NULL DEFAULT SYSUTCDATETIME(),
    row_version              ROWVERSION     NOT NULL,
    CONSTRAINT UQ_CreditNotes_OrgNumber UNIQUE (org_id, credit_note_number),
    CONSTRAINT FK_CreditNotes_Org FOREIGN KEY (org_id) REFERENCES tenant.Organizations(org_id),
    CONSTRAINT FK_CreditNotes_Customer FOREIGN KEY (customer_id) REFERENCES customers.Customers(customer_id),
    CONSTRAINT FK_CreditNotes_ReturnOrder FOREIGN KEY (return_order_id) REFERENCES pos.PosOrders(order_id),
    CONSTRAINT FK_CreditNotes_IssuedStore FOREIGN KEY (issued_at_store_id) REFERENCES pos.Stores(store_id),
    CONSTRAINT FK_CreditNotes_IssuedBy FOREIGN KEY (issued_by) REFERENCES auth.Users(user_id),
    CONSTRAINT FK_CreditNotes_LastStore FOREIGN KEY (last_used_at_store_id) REFERENCES pos.Stores(store_id),
    CONSTRAINT CK_CreditNotes_Status CHECK (status IN (0,1,2,3))
);

CREATE INDEX IX_CreditNotes_CustomerStatus ON pos.CreditNotes (customer_id, status);
CREATE INDEX IX_CreditNotes_OrgStatusExpiry ON pos.CreditNotes (org_id, status, expires_at);
CREATE INDEX IX_CreditNotes_ReturnOrder ON pos.CreditNotes (return_order_id) 
    WHERE return_order_id IS NOT NULL;
CREATE UNIQUE INDEX UQ_CreditNotes_OrgLocalId 
    ON pos.CreditNotes (org_id, local_id) WHERE local_id IS NOT NULL;

-- Now add the FK from PosOrders to CreditNotes
ALTER TABLE pos.PosOrders ADD
    CONSTRAINT FK_PosOrders_CreditNote 
        FOREIGN KEY (credit_note_id) REFERENCES pos.CreditNotes(credit_note_id);
CREATE INDEX IX_PosOrders_CreditNote 
    ON pos.PosOrders (credit_note_id) WHERE credit_note_id IS NOT NULL;
```

#### M039.11 — Credit Note Transactions Table

```sql
CREATE TABLE pos.CreditNoteTransactions (
    transaction_id   BIGINT IDENTITY(1,1) PRIMARY KEY,
    org_id           BIGINT         NOT NULL,
    credit_note_id   BIGINT         NOT NULL,
    transaction_type TINYINT        NOT NULL,
    amount           DECIMAL(18,4)  NOT NULL,
    balance_after    DECIMAL(18,4)  NOT NULL,
    pos_order_id     BIGINT         NULL,
    store_id         BIGINT         NOT NULL,
    performed_by     BIGINT         NOT NULL,
    performed_at     DATETIME2(2)   NOT NULL DEFAULT SYSUTCDATETIME(),
    note             NVARCHAR(300)  NULL,
    local_id         VARCHAR(50)    NULL,
    is_synced        BIT            NOT NULL DEFAULT 0,
    created_at       DATETIME2(2)   NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT FK_CNTxn_Org FOREIGN KEY (org_id) REFERENCES tenant.Organizations(org_id),
    CONSTRAINT FK_CNTxn_CreditNote FOREIGN KEY (credit_note_id) REFERENCES pos.CreditNotes(credit_note_id),
    CONSTRAINT FK_CNTxn_Order FOREIGN KEY (pos_order_id) REFERENCES pos.PosOrders(order_id),
    CONSTRAINT FK_CNTxn_Store FOREIGN KEY (store_id) REFERENCES pos.Stores(store_id),
    CONSTRAINT FK_CNTxn_PerformedBy FOREIGN KEY (performed_by) REFERENCES auth.Users(user_id),
    CONSTRAINT CK_CNTxn_Type CHECK (transaction_type IN (0,1,2,3,4))
);

CREATE INDEX IX_CNTxn_CreditNote ON pos.CreditNoteTransactions (credit_note_id, performed_at);
CREATE INDEX IX_CNTxn_Order ON pos.CreditNoteTransactions (pos_order_id) 
    WHERE pos_order_id IS NOT NULL;
CREATE INDEX IX_CNTxn_Store ON pos.CreditNoteTransactions (store_id, performed_at);
CREATE UNIQUE INDEX UQ_CNTxn_OrgLocalId 
    ON pos.CreditNoteTransactions (org_id, local_id) WHERE local_id IS NOT NULL;
```

#### M039.14 — Returnable Order Lines View

```sql
CREATE VIEW pos.vw_ReturnableOrderLines AS
SELECT
    o.order_id,
    o.org_id,
    o.store_id,
    o.order_number,
    o.customer_id,
    o.created_at      AS order_date,
    l.line_id,
    l.product_id,
    l.product_name,
    l.product_sku,
    l.quantity         AS original_qty,
    l.returned_qty,
    l.quantity - l.returned_qty AS returnable_qty,
    l.unit_price,
    l.tax_percentage,
    l.line_total,
    CASE 
        WHEN l.returned_qty = 0 THEN 'None'
        WHEN l.returned_qty < l.quantity THEN 'Partial'
        ELSE 'Full'
    END AS return_status
FROM pos.PosOrders o
INNER JOIN pos.PosOrderLines l ON o.order_id = l.order_id
WHERE o.order_type = 0          -- Original sales only
  AND o.status IN (1, 4)        -- Completed or PartialReturn
  AND l.is_return_line = 0      -- Not a return line itself
  AND l.quantity > l.returned_qty; -- Has returnable quantity
```

---

## 15. API Endpoints

### 15.1 Return Reason API

| # | Method | Endpoint | Auth | Description |
|---|---|---|---|---|
| 1 | GET | `/api/pos/return-reasons` | pos.order.return | List active return reasons for current tenant |
| 2 | POST | `/api/pos/return-reasons` | pos.return.reason.manage | Create return reason |
| 3 | PUT | `/api/pos/return-reasons/{id}` | pos.return.reason.manage | Update return reason |
| 4 | PATCH | `/api/pos/return-reasons/{id}/status` | pos.return.reason.manage | Activate/deactivate reason |

### 15.2 Return Policy API

| # | Method | Endpoint | Auth | Description |
|---|---|---|---|---|
| 5 | GET | `/api/pos/stores/{storeId}/return-policy` | pos.store.view | Get effective return policy for store (resolved with fallback) |
| 6 | PUT | `/api/pos/stores/{storeId}/return-policy` | pos.return.policy.manage | Create/update store-specific return policy |
| 7 | GET | `/api/pos/return-policies` | pos.return.policy.manage | List all return policies (org-wide + store-specific) |
| 8 | POST | `/api/pos/return-policies` | pos.return.policy.manage | Create org-wide default policy |
| 9 | DELETE | `/api/pos/stores/{storeId}/return-policy` | pos.return.policy.manage | Remove store override (falls back to org default) |

### 15.3 Order Lookup & Returnable Lines API

| # | Method | Endpoint | Auth | Description |
|---|---|---|---|---|
| 10 | GET | `/api/pos/orders/lookup?receipt={orderNumber}` | pos.order.return | Lookup order by receipt number (cross-store capable). Returns order detail, returnable lines, and applicable policy |
| 11 | GET | `/api/pos/orders/{id}/returnable-lines` | pos.order.return | Get returnable lines for an order (with returned_qty, returnable_qty) |

### 15.4 Return & Exchange Processing API

| # | Method | Endpoint | Auth | Description |
|---|---|---|---|---|
| 12 | POST | `/api/pos/orders/{id}/return` | pos.order.return | Process return (enhanced: policy check, reason, auth, credit note) |
| 13 | POST | `/api/pos/orders/{id}/exchange` | pos.order.return | Process exchange (return items + new items, net settlement) |
| 14 | POST | `/api/pos/returns/sync` | pos.order.return | Sync offline return/exchange order from PWA |
| 15 | POST | `/api/pos/returns/authorize` | pos.return.override | Manager authorization for policy-exceeding returns |

### 15.5 Credit Note API

| # | Method | Endpoint | Auth | Description |
|---|---|---|---|---|
| 16 | POST | `/api/pos/credit-notes` | pos.credit_note.issue | Issue credit note (standalone, not from return) |
| 17 | GET | `/api/pos/credit-notes/{id}` | pos.credit_note.view | Get credit note details |
| 18 | GET | `/api/pos/credit-notes/lookup?number={cn_number}` | pos.credit_note.view | Lookup credit note by number (for redemption) |
| 19 | GET | `/api/pos/customers/{id}/credit-notes` | pos.credit_note.view | List customer's credit notes (active, redeemed, expired) |
| 20 | POST | `/api/pos/credit-notes/{id}/redeem` | pos.credit_note.redeem | Redeem credit note (as payment on an order) |
| 21 | PATCH | `/api/pos/credit-notes/{id}/cancel` | pos.credit_note.cancel | Cancel active credit note (admin action) |
| 22 | GET | `/api/pos/credit-notes/{id}/transactions` | pos.credit_note.view | Credit note transaction history |

### 15.6 Sync Extensions

| # | Method | Endpoint | Auth | Description |
|---|---|---|---|---|
| 23 | GET | `/api/sync/pos-return-reasons?since={iso}` | pos.order.create | Delta sync return reasons to PWA |
| 24 | GET | `/api/sync/pos-return-policy?storeId={id}` | pos.order.create | Sync effective return policy to PWA (cached locally) |

---

## 16. Permission Claims

### 16.1 New Permissions

| Code | Module | Description |
|---|---|---|
| `pos.return.cross_store` | POS | Process cross-store returns (return items from another store) |
| `pos.return.override` | POS | Override return policy (expired window, exceeded threshold) |
| `pos.return.policy.manage` | POS | Create and manage return policies |
| `pos.return.reason.manage` | POS | Manage return reason catalog |
| `pos.return.reports` | POS | View return analytics and reports |
| `pos.credit_note.issue` | POS | Issue credit notes (standalone or from return) |
| `pos.credit_note.redeem` | POS | Redeem credit notes as payment |
| `pos.credit_note.cancel` | POS | Cancel active credit notes |
| `pos.credit_note.view` | POS | View credit note details and history |
| `pos.credit_note.reports` | POS | View credit note reports and aging |

> **Note:** The existing `pos.order.return` permission (Addendum 38) continues to control basic same-store return access.

### 16.2 Default Role Mapping

| Permission | Cashier | Supervisor | Store Manager | Admin |
|---|---|---|---|---|
| pos.order.return (existing) | ❌ | ✅ | ✅ | ✅ |
| pos.return.cross_store | ❌ | ✅ | ✅ | ✅ |
| pos.return.override | ❌ | ❌ | ✅ | ✅ |
| pos.return.policy.manage | ❌ | ❌ | ❌ | ✅ |
| pos.return.reason.manage | ❌ | ❌ | ❌ | ✅ |
| pos.return.reports | ❌ | ✅ | ✅ | ✅ |
| pos.credit_note.issue | ❌ | ✅ | ✅ | ✅ |
| pos.credit_note.redeem | ✅ | ✅ | ✅ | ✅ |
| pos.credit_note.cancel | ❌ | ❌ | ✅ | ✅ |
| pos.credit_note.view | ❌ | ✅ | ✅ | ✅ |
| pos.credit_note.reports | ❌ | ❌ | ✅ | ✅ |

---

## 17. POS Return Screen Designs

### 17.1 Order Lookup Screen

**Route:** `/pos/return`

```
┌─────────────────────────────────────────────────────────────────┐
│ ← Back                Return / Exchange                         │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │ 🔍 Scan receipt barcode or enter receipt number...        │  │
│  └───────────────────────────────────────────────────────────┘  │
│                                                                 │
│  ── OR search by customer ──────────────────────────────────    │
│                                                                 │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │ 👤 Search customer name or phone...                       │  │
│  └───────────────────────────────────────────────────────────┘  │
│                                                                 │
│  Recent Orders (this store):                                    │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │ POS-STR001-20261010-0042  │ Sarah Mitchell │ $150.00  [→]│  │
│  │ 2026-10-10 14:30          │ 3 items        │ Completed    │  │
│  ├───────────────────────────────────────────────────────────┤  │
│  │ POS-STR001-20261010-0038  │ Walk-In        │  $42.22  [→]│  │
│  │ 2026-10-10 13:15          │ 4 items        │ Completed    │  │
│  ├───────────────────────────────────────────────────────────┤  │
│  │ POS-STR001-20261009-0055  │ James Taylor   │ $200.00  [→]│  │
│  │ 2026-10-09 16:45          │ 2 items        │ PartialRet   │  │
│  └───────────────────────────────────────────────────────────┘  │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

### 17.2 Return Item Selection Screen

**Route:** `/pos/return/{orderId}`

```
┌─────────────────────────────────────────────────────────────────┐
│ ← Back              Return Items                                │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  Original Order: POS-STR001-20261010-0042                       │
│  Date: 2026-10-10 14:30  │  Store: Downtown Store               │
│  Customer: Sarah Mitchell                                       │
│  ┌────────────────────────────────────┐                         │
│  │ ⓘ Return window: 24 days remaining │                         │
│  └────────────────────────────────────┘                         │
│                                                                 │
│  Select items to return:                                        │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │ ☑  Arabica Coffee 250g                                    │  │
│  │    Purchased: 3  │  Already returned: 0  │  Returnable: 3 │  │
│  │    Unit price: $14.99                                      │  │
│  │    Return qty: [▼ 2 ▲]               Subtotal: $29.98     │  │
│  ├───────────────────────────────────────────────────────────┤  │
│  │ ☐  Croissant Butter                                       │  │
│  │    Purchased: 2  │  Already returned: 1  │  Returnable: 1 │  │
│  │    Unit price: $3.50                                       │  │
│  │    Return qty: [ — ]                                       │  │
│  ├───────────────────────────────────────────────────────────┤  │
│  │ ☑  Oat Milk Latte                                         │  │
│  │    Purchased: 1  │  Already returned: 0  │  Returnable: 1 │  │
│  │    Unit price: $5.50                                       │  │
│  │    Return qty: [  1  ]               Subtotal: $5.50      │  │
│  └───────────────────────────────────────────────────────────┘  │
│                                                                 │
│  ── Return Reason ──────────────────────────────────────────    │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │ Reason: [ Defective / Damaged                         ▾ ] │  │
│  │ Note:   [ Items arrived damaged in packaging...        ]  │  │
│  └───────────────────────────────────────────────────────────┘  │
│                                                                 │
│  ─────────────────────────────────────────────────────────────  │
│  Return subtotal:                                    $35.48     │
│  Restocking fee (0%):                                 $0.00     │
│  Tax reversal:                                       −$3.55     │
│  ─────────────────────────────────────────────────────────────  │
│  REFUND AMOUNT:                                      $35.48     │
│                                                                 │
│  ┌─────────────┐  ┌───────────────────┐  ┌─────────────────┐  │
│  │ 🔄 Exchange │  │ ↩ Process Return  │  │    Cancel       │  │
│  │ (add items) │  │                   │  │                 │  │
│  └─────────────┘  └───────────────────┘  └─────────────────┘  │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

Tapping **"🔄 Exchange"** transitions to the exchange flow: the selected return items carry over and the product grid opens for the customer to pick new items.

### 17.3 Refund Method Selection Screen

**Route:** `/pos/return/{orderId}/refund`

```
┌─────────────────────────────────────────────────────────────────┐
│ ← Back              Select Refund Method                        │
├─────────────────────────────────────────────────────────────────┤
│                                                                 │
│  Refund Amount: $35.48                                          │
│  Original Payment: Cash ($100.00), Card ($50.00)                │
│                                                                 │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │                                                           │  │
│  │  ┌─────────────────────────────────────────────────────┐  │  │
│  │  │  💵  Cash Refund                                    │  │  │
│  │  │  Refund $35.48 from register                        │  │  │
│  │  └─────────────────────────────────────────────────────┘  │  │
│  │                                                           │  │
│  │  ┌─────────────────────────────────────────────────────┐  │  │
│  │  │  💳  Refund to Original Method                      │  │  │
│  │  │  Card ending ****4242                                │  │  │
│  │  │  (Recorded — actual reversal processed separately)  │  │  │
│  │  └─────────────────────────────────────────────────────┘  │  │
│  │                                                           │  │
│  │  ┌─────────────────────────────────────────────────────┐  │  │
│  │  │  🎫  Store Credit (Credit Note)                     │  │  │
│  │  │  Issue credit note for $35.48                        │  │  │
│  │  │  Valid for: No expiry (per policy)                   │  │  │
│  │  │  Redeemable at: All stores                           │  │  │
│  │  └─────────────────────────────────────────────────────┘  │  │
│  │                                                           │  │
│  └───────────────────────────────────────────────────────────┘  │
│                                                                 │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │                                                           │  │
│  │              ✓  Confirm Refund  ($35.48)                 │  │
│  │                                                           │  │
│  └───────────────────────────────────────────────────────────┘  │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

### 17.4 Exchange Screen (Return Items + New Items)

```
┌─────────────────────────────────────────────────────────────────────────────────────┐
│ ┌───────────────────────────────────────────────────────────────────────────────┐   │
│ │  🟢 Online  │  Downtown Store  │  Exchange Mode                              │   │
│ │             │  Original: POS-STR001-20261010-0042                             │   │
│ └───────────────────────────────────────────────────────────────────────────────┘   │
│                                                                                     │
│ ┌──────────────────────────────────────────┐  ┌──────────────────────────────────┐  │
│ │                                          │  │  EXCHANGE CART                    │  │
│ │  ┌─────────────────────────────────────┐ │  │                                  │  │
│ │  │ 🔍 Search or scan new items...      │ │  │  ── RETURNING ──────────────     │  │
│ │  └─────────────────────────────────────┘ │  │  ┌──────────────────────────────┐│  │
│ │                                          │  │  │ ↩ Arabica Coffee 250g  ×−2  ││  │
│ │  Product grid (same as selling screen)   │  │  │   $14.99 ea      −$29.98    ││  │
│ │                                          │  │  └──────────────────────────────┘│  │
│ │  ┌────────┐ ┌────────┐ ┌────────┐       │  │  ┌──────────────────────────────┐│  │
│ │  │ ☕     │ │ 🥐     │ │ 🧁     │       │  │  │ ↩ Oat Milk Latte       ×−1  ││  │
│ │  │Arabica │ │Crois-  │ │Choco   │       │  │  │   $5.50 ea       −$5.50     ││  │
│ │  │Coffee  │ │sant    │ │Muffin  │       │  │  └──────────────────────────────┘│  │
│ │  │$14.99  │ │$3.50   │ │$4.00   │       │  │                                  │  │
│ │  └────────┘ └────────┘ └────────┘       │  │  ── NEW ITEMS ──────────────     │  │
│ │  ┌────────┐ ┌────────┐ ┌────────┐       │  │  ┌──────────────────────────────┐│  │
│ │  │ 🥛     │ │ 🫖     │ │ 🍞     │       │  │  │ + Dark Choco Bar       ×+3  ││  │
│ │  │Oat Milk│ │Green   │ │Sour-   │       │  │  │   $2.50 ea       +$7.50     ││  │
│ │  │Latte   │ │Tea     │ │dough   │       │  │  └──────────────────────────────┘│  │
│ │  │$5.50   │ │$3.00   │ │Loaf    │       │  │  ┌──────────────────────────────┐│  │
│ │  └────────┘ └────────┘ │$6.00   │       │  │  │ + Green Tea            ×+2  ││  │
│ │                        └────────┘       │  │  │   $3.00 ea       +$6.00     ││  │
│ │                                          │  │  └──────────────────────────────┘│  │
│ │                                          │  │                                  │  │
│ │                                          │  │──────────────────────────────────│  │
│ │                                          │  │  Return credit:      −$35.48    │  │
│ │                                          │  │  Restocking fee:       $0.00     │  │
│ │                                          │  │  New items:           +$13.50    │  │
│ │                                          │  │  Tax on new (10%):    +$1.35     │  │
│ │                                          │  │  ─────────────────────────────   │  │
│ │                                          │  │  NET REFUND:         −$20.63    │  │
│ │                                          │  │  (Customer receives $20.63)      │  │
│ │                                          │  │                                  │  │
│ │                                          │  │  ┌──────────────────────────────┐│  │
│ │                                          │  │  │    ✓ Complete Exchange       ││  │
│ │                                          │  │  └──────────────────────────────┘│  │
│ └──────────────────────────────────────────┘  └──────────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────────────────────────┘
```

### 17.5 Credit Note Display

```
┌─────────────────────────────────────────────────────────────────┐
│                                                                 │
│  ┌───────────────────────────────────────────────────────────┐  │
│  │                    STORE CREDIT NOTE                       │  │
│  │                                                           │  │
│  │  ┌─────────────────────────────────────────────────────┐  │  │
│  │  │         ████████████████████████████████             │  │  │
│  │  │         ██  CN-STR001-20261010-0001  ██             │  │  │
│  │  │         ████████████████████████████████             │  │  │
│  │  │              (Scannable barcode)                     │  │  │
│  │  └─────────────────────────────────────────────────────┘  │  │
│  │                                                           │  │
│  │  Credit Note #: CN-STR001-20261010-0001                   │  │
│  │  Issued to:     Sarah Mitchell                            │  │
│  │  Amount:        $35.48                                    │  │
│  │  Balance:       $35.48                                    │  │
│  │  Issued at:     Downtown Store                            │  │
│  │  Issued on:     2026-10-10 14:45                          │  │
│  │  Expires:       Never                                     │  │
│  │  Status:        ● Active                                  │  │
│  │                                                           │  │
│  │  Redeemable at any store location.                        │  │
│  │  Present this credit note at time of purchase.            │  │
│  │                                                           │  │
│  │  Return ref: POS-STR001-20261010-R003                     │  │
│  │                                                           │  │
│  └───────────────────────────────────────────────────────────┘  │
│                                                                 │
│  ┌───────────────┐  ┌───────────────┐  ┌─────────────────────┐ │
│  │  🖨 Print      │  │  📧 Email     │  │  ✓ Done             │ │
│  └───────────────┘  └───────────────┘  └─────────────────────┘ │
│                                                                 │
└─────────────────────────────────────────────────────────────────┘
```

### 17.6 Return Receipt

```
┌─────────────────────────────────────────────────┐
│              DOWNTOWN STORE                      │
│           123 Main St, Suite 100                 │
│          City, State 12345                       │
│           Tel: +1-555-0100                       │
│                                                  │
│  ──────────── RETURN RECEIPT ──────────────      │
│                                                  │
│  Return #:  POS-STR001-20261010-R003             │
│  Date:      2026-10-10 14:45                     │
│  Cashier:   Ahmed K.                             │
│  Original:  POS-STR001-20261010-0042             │
│  Customer:  Sarah Mitchell                       │
│                                                  │
│  RETURNED ITEMS:                                 │
│  ────────────────────────────────────────────    │
│  Arabica Coffee 250g  ×2   @$14.99   −$29.98    │
│  Oat Milk Latte       ×1   @$5.50    −$5.50     │
│  ────────────────────────────────────────────    │
│  Subtotal:                           −$35.48     │
│  Tax reversal (10%):                 −$3.55      │
│  Restocking fee:                      $0.00      │
│  ────────────────────────────────────────────    │
│  REFUND TOTAL:                       −$35.48     │
│                                                  │
│  REFUND METHOD:                                  │
│  Store Credit: CN-STR001-20261010-0001           │
│  Credit Note Balance: $35.48                     │
│                                                  │
│  Reason: Defective / Damaged                     │
│                                                  │
│  ────────────────────────────────────────────    │
│  Thank you for shopping with us.                 │
│  Credit note redeemable at any location.         │
│                                                  │
└─────────────────────────────────────────────────┘
```

### 17.7 Back-Office: Return Policy Configuration

**Route:** `/settings/pos/stores/{id}` → **Returns** tab (new tab added to store detail)

```
┌─────────────────────────────────────────────────────────────────┐
│  Store: Downtown Store (STR001)                                  │
│  ┌──────┬──────────────┬───────────┬───────┬──────┬──────────┐  │
│  │ Gen. │ Configuration│ Terminals │ Staff │Shifts│ Returns  │  │
│  └──────┴──────────────┴───────────┴───────┴──────┴──────────┘  │
│                                                                  │
│  Return Policy: ☑ Use store-specific policy                      │
│                 ☐ Use organization default                       │
│                                                                  │
│  ┌────────────────────────────────────────────────────────────┐  │
│  │  Policy Name:         [ Standard 30-Day Return       ]     │  │
│  │                                                            │  │
│  │  ── Return Window ──                                       │  │
│  │  Return window (days):      [ 30      ]  (blank=unlimited) │  │
│  │  Exchange window (days):    [ 30      ]  (blank=same)      │  │
│  │                                                            │  │
│  │  ── Requirements ──                                        │  │
│  │  ☑ Require receipt for returns                             │  │
│  │  ☐ Allow no-receipt exchange (credit note only)            │  │
│  │                                                            │  │
│  │  ── Cross-Store ──                                         │  │
│  │  ☑ Accept returns from other stores                        │  │
│  │  ☑ Require manager authorization for cross-store           │  │
│  │                                                            │  │
│  │  ── Fees ──                                                │  │
│  │  Restocking fee (%):        [ 0.00    ]                    │  │
│  │                                                            │  │
│  │  ── Authorization ──                                       │  │
│  │  Max return without manager: [ 500.00  ]  (blank=no limit) │  │
│  │                                                            │  │
│  │  ── Refund Methods ──                                      │  │
│  │  ☐ Force refund to original payment method                 │  │
│  │  ☑ Allow cash refund                                       │  │
│  │  ☑ Allow credit note refund                                │  │
│  │  Credit note expiry (days): [        ]  (blank=no expiry)  │  │
│  └────────────────────────────────────────────────────────────┘  │
│                                                                  │
│  ┌──────────────┐                    ┌─────────────────────────┐│
│  │    Cancel     │                    │    Save Return Policy   ││
│  └──────────────┘                    └─────────────────────────┘│
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

---

## 18. Business Rules

### 18.1 Return Rules

| Rule ID | Rule | Description |
|---|---|---|
| POS-RET-01 | Return window enforcement | If `return_window_days` is set on the effective policy, returns are blocked after `original_order.created_at + window`. Manager can override with `pos.return.override` permission |
| POS-RET-02 | Exchange window separate | If `exchange_window_days` is set, it overrides `return_window_days` for exchange transactions. NULL = same as return window. 0 = exchanges not allowed |
| POS-RET-03 | Receipt required | If `require_receipt = true`, the original order must be identified (by order number, receipt scan, or customer lookup). No anonymous returns |
| POS-RET-04 | Returnable quantity limit | `return_qty` for any line cannot exceed `quantity - returned_qty` (the returnable quantity) |
| POS-RET-05 | Fully returned block | If all lines of the original order are fully returned (`returned_qty = quantity` for every line), no further returns are allowed against this order |
| POS-RET-06 | Voided order block | Returns cannot be processed against voided orders (`status = Voided`) |
| POS-RET-07 | Return against return block | Returns cannot be processed against orders with `order_type = RETURN` or `EXCHANGE`. Only original `SALE` orders are valid targets |
| POS-RET-08 | Reason required | A `return_reason_id` must be selected for every return/exchange. If the reason has `requires_note = true`, `return_reason` text is mandatory |
| POS-RET-09 | Manager auth for reason | If the selected reason has `requires_manager = true`, manager PIN authorization is required regardless of amount |
| POS-RET-10 | Manager auth for amount | If `max_return_no_auth` is set and the refund amount exceeds it, manager authorization is required |
| POS-RET-11 | Restocking fee calculation | `restocking_fee = return_subtotal × (fee_pct / 100)`. The reason's `restocking_fee_override` takes precedence over the policy's `restocking_fee_pct`. Override value of 0 waives the fee |
| POS-RET-12 | Return any shift | Returns can be processed in any active shift, not limited to the shift of the original sale (carried forward from Addendum 38 POS-ORD-08) |
| POS-RET-13 | Original status update | After a return is processed, the original order's status is updated to `Returned(3)` if fully returned, or `PartialReturn(4)` if partially returned |
| POS-RET-14 | Return order completed immediately | Return orders are created with `status = Completed(1)`. They do not go through Draft → Completed lifecycle |

### 18.2 Cross-Store Rules

| Rule ID | Rule | Description |
|---|---|---|
| POS-XST-01 | Online required | Cross-store returns require the terminal to be online. The original order must be looked up from the central server |
| POS-XST-02 | Policy check | The **processing store's** return policy `allow_cross_store_return` must be `true`. If not, cross-store returns are blocked at this store |
| POS-XST-03 | Manager authorization | If `cross_store_requires_manager = true`, manager PIN is required for all cross-store returns regardless of amount |
| POS-XST-04 | Stock to processing store | Returned stock goes into the processing store's warehouse, not the original store's warehouse |
| POS-XST-05 | Inter-store finance | Cross-store returns create two journal entries: a receivable at the processing store and a revenue reversal + payable at the original store |
| POS-XST-06 | Return window applies | The original store's (or org-wide) return window still applies. The processing store's policy only controls whether cross-store returns are accepted |

### 18.3 Exchange Rules

| Rule ID | Rule | Description |
|---|---|---|
| POS-EXC-01 | Mixed lines | An exchange order contains both negative (return) and positive (new sale) lines in the same order. `is_return_line` distinguishes them |
| POS-EXC-02 | Net settlement | The customer pays or receives only the net difference: `new_total - (return_subtotal - restocking_fee)` |
| POS-EXC-03 | Net positive | If `net > 0`, customer pays the difference using any accepted payment method (Cash, Card, CreditNote, etc.) |
| POS-EXC-04 | Net negative | If `net < 0`, the customer receives a refund of `|net|` via the selected refund method |
| POS-EXC-05 | Net zero | If `net = 0`, no payment is recorded. The exchange is an even swap |
| POS-EXC-06 | Exchange window | Uses `exchange_window_days` from policy if set, otherwise `return_window_days` |
| POS-EXC-07 | New items from any category | The new items in an exchange can be any products available on POS. They are not restricted to the same category as the returned items |
| POS-EXC-08 | Dual stock movement | Exchange generates both `POS_RETURN` (IN) and `POS_SALE` (OUT) stock movements in the same transaction |

### 18.4 Credit Note Rules

| Rule ID | Rule | Description |
|---|---|---|
| POS-CN-01 | Customer optional | If `customer_id` is set, only that customer can redeem. If NULL, it's a bearer credit note — anyone with the number can use it |
| POS-CN-02 | Cross-store redemption | Credit notes can be redeemed at any store in the organization |
| POS-CN-03 | Partial redemption | A credit note can be used partially. Remaining balance is preserved for future use |
| POS-CN-04 | Online redemption only | Credit note redemption requires online connectivity for real-time balance verification. This prevents double-spend across multiple offline terminals |
| POS-CN-05 | Expiry enforcement | If `expires_at` is set and current date exceeds it, the credit note cannot be redeemed. Status is set to Expired(2) by the Hangfire daily job |
| POS-CN-06 | Cancellation | Only active credit notes with remaining balance can be cancelled. Cancelled credit notes are not refundable. Requires `pos.credit_note.cancel` permission |
| POS-CN-07 | Immutable after redemption | A fully redeemed (`remaining_balance = 0`) credit note cannot be cancelled or adjusted |
| POS-CN-08 | Transaction audit | Every change to a credit note's balance (issue, redeem, expire, cancel, adjust) creates a `CreditNoteTransaction` record |
| POS-CN-09 | Split payment | Credit notes can be combined with other payment methods in a split payment (e.g., $20 from credit note + $10 cash) |
| POS-CN-10 | No change from CN | If a credit note payment exceeds the order total, the excess stays on the credit note (no cash change from credit note). The payment amount is capped at `remaining_balance` or order remaining, whichever is less |

### 18.5 Refund Rules

| Rule ID | Rule | Description |
|---|---|---|
| POS-REF-01 | Refund method per policy | Available refund methods are governed by the return policy: `allow_cash_refund`, `allow_credit_note_refund`, `refund_to_original_method` |
| POS-REF-02 | Force original method | If `refund_to_original_method = true`, the refund is automatically processed to the same payment method as the original sale. Cashier cannot choose |
| POS-REF-03 | Cash refund from register | Cash refunds deduct from the shift's cash drawer. The shift `total_cash_refunds` and `expected_cash_closing` are updated |
| POS-REF-04 | Card refund recorded | Card refunds are recorded (reference noted) but not processed through a gateway (Version 1). The shift `total_card_refunds` is updated |
| POS-REF-05 | Credit note liability | Credit note refunds create a liability in the finance module. The liability is cleared when the credit note is redeemed |

---

## 19. Test Scenarios

| Test ID | Scenario | Expected Result | Category |
|---|---|---|---|
| TS-039-01 | Same-store full return (all lines, cash refund) | Return order created, original status = Returned(3), cash refunded, stock IN, revenue reversed | Return |
| TS-039-02 | Same-store partial return (2 of 3 items) | Return order created, original status = PartialReturn(4), returned_qty updated on 2 lines | Return |
| TS-039-03 | Second partial return on same order | Further items returned, returned_qty accumulates, original stays PartialReturn or becomes Returned | Return |
| TS-039-04 | Return blocked — all items already returned | Error: "All items in this order have already been returned" | Validation |
| TS-039-05 | Return blocked — voided order | Error: "Cannot return a voided order" | Validation |
| TS-039-06 | Return blocked — return window expired | Error: "Return window expired (30 days)". Manager override available | Policy |
| TS-039-07 | Return with manager override on expired window | Manager PIN accepted, return processed with `authorized_by` recorded | Policy |
| TS-039-08 | Return blocked — qty exceeds returnable | Error: "Cannot return 5 of 'Coffee'. Only 3 returnable" | Validation |
| TS-039-09 | Return with "Defective" reason (no note required) | Return processed, `return_reason_id` set, no note validation | Reason |
| TS-039-10 | Return with "Other" reason (note required) | Note field is mandatory. Blank note → validation error | Reason |
| TS-039-11 | Return with "Price Match" reason (manager required) | Manager PIN prompt shown regardless of amount. Recorded in `authorized_by` | Reason |
| TS-039-12 | Return exceeding max_return_no_auth threshold | Manager PIN required for $600 return when threshold = $500 | Policy |
| TS-039-13 | Return with 5% restocking fee | Refund = return_subtotal × 0.95. Fee recorded in `restocking_fee_amount`. JE includes fee income | Fee |
| TS-039-14 | Return with reason-level fee override (0% — waived) | Restocking fee = 0 even though store policy has 5%. Reason's `restocking_fee_override = 0` wins | Fee |
| TS-039-15 | Refund as credit note | Credit note created, `CreditNoteTransaction(Issue)` logged, JE debits revenue + credits CN Liability | CreditNote |
| TS-039-16 | Refund forced to original method | Policy `refund_to_original_method = true`. Cashier cannot choose. Card refund auto-selected | Policy |
| TS-039-17 | Cross-store return (online) | Original order from Store A returned at Store B. Manager PIN required. Stock → Store B warehouse. Inter-store JE created | CrossStore |
| TS-039-18 | Cross-store return blocked (offline) | Terminal offline → error: "Cross-store returns require internet connectivity" | CrossStore |
| TS-039-19 | Cross-store return blocked (policy disallows) | Store B `allow_cross_store_return = false` → error: "This store does not accept returns from other locations" | CrossStore |
| TS-039-20 | Exchange — net positive (customer pays) | Return $30 + buy $50 = customer pays $20. One order with mixed lines. Payment recorded | Exchange |
| TS-039-21 | Exchange — net negative (customer refunded) | Return $50 + buy $30 = customer refunded $20. Refund method selection shown | Exchange |
| TS-039-22 | Exchange — even swap (net = $0) | Return $40 + buy $40. No payment recorded. Exchange completed | Exchange |
| TS-039-23 | Exchange with restocking fee | Return $100 (5% fee = $5), buy $80. Net = $80 - $95 = customer refunded $15 | Exchange |
| TS-039-24 | Cross-store exchange | Return items from Store A at Store B + buy new items at Store B. Manager auth + inter-store JE | Exchange |
| TS-039-25 | Credit note redemption (full) | CN balance $35, order $30. CN payment = $30, remaining = $5. CN status stays Active | CreditNote |
| TS-039-26 | Credit note redemption (exact) | CN balance $35, order $35. CN fully used. Status → FullyRedeemed(1) | CreditNote |
| TS-039-27 | Credit note split payment | Order $50. Pay $35 from CN + $15 cash. Both payments recorded | CreditNote |
| TS-039-28 | Credit note redemption blocked (expired) | CN expired → error: "This credit note has expired" | CreditNote |
| TS-039-29 | Credit note redemption blocked (offline) | Terminal offline → error: "Credit note redemption requires internet connectivity" | CreditNote |
| TS-039-30 | Credit note cancelled by admin | Active CN cancelled. Status → Cancelled(3). Transaction logged. Balance preserved (non-refundable) | CreditNote |
| TS-039-31 | Offline same-store return (order in local DB) | Return processed offline, stored in Dexie, synced when online | Offline |
| TS-039-32 | Offline same-store return (order NOT in local DB) | Error: "Original order not found locally. Connect to internet to search" | Offline |
| TS-039-33 | Return stock movement verification | After return: `StockMovement(POS_RETURN, IN)` exists. Product stock level increased at store warehouse | Stock |
| TS-039-34 | Return finance journal verification | After cash return: JE debits Sales Revenue + Tax Payable, credits Cash Account | Finance |
| TS-039-35 | Credit note expiry Hangfire job | CN with `expires_at` in the past → status set to Expired. `CreditNoteExpired` SignalR event sent | Hangfire |

---

## 20. Development Phases

### 20.1 Phase Overview

| Phase | Focus | Days | Key Deliverables |
|---|---|---|---|
| Phase 1 | Return Reasons + Return Policies | 3 | M039.1–M039.3, M039.8–M039.9, ReturnReasonService, ReturnPolicyService, API endpoints 1–9 |
| Phase 2 | Order Extensions + Same-Store Return Flow | 5 | M039.4–M039.5, M039.14, PosReturnService, return stock + finance, API endpoints 10–15 |
| Phase 3 | Credit Notes + Credit Note Transactions | 4 | M039.6–M039.7, M039.10–M039.11, CreditNoteService, API endpoints 16–22 |
| Phase 4 | Cross-Store Return + Exchange Flow | 4 | Cross-store order lookup, inter-store JE, exchange flow, PosExchangeService |
| Phase 5 | PWA Integration (Return + Exchange Screens) | 5 | Dexie schema v2, return screens, exchange screen, credit note display, offline handling |
| Phase 6 | SignalR Events + Hangfire Jobs + Sync | 2 | Return events, CN expiry job, sync endpoints 23–24, reconciliation report |
| Phase 7 | Permissions + Back-Office UI + Testing | 3 | M039.12–M039.13, return policy config screen, return reports, E2E tests |
| **Total** | | **26 days** | **14 migrations, 4 new tables, 1 view, 5 modified tables, 24 endpoints** |

### 20.2 Phase Dependencies

```
Phase 1 (Return Reasons + Policies)
    │
    ▼
Phase 2 (Order Extensions + Same-Store Return)
    │
    ├──────────────────────────────┐
    ▼                              ▼
Phase 3 (Credit Notes)         Phase 4 (Cross-Store + Exchange)
    │                              │
    └──────────┬───────────────────┘
               ▼
         Phase 5 (PWA Integration)
               │
               ▼
         Phase 6 (SignalR + Hangfire + Sync)
               │
               ▼
         Phase 7 (Permissions + Admin UI + Testing)
```

---

## 21. Future Enhancements

| # | Feature | Description |
|---|---|---|
| 1 | Offline credit note redemption | Cache credit notes locally with tolerance threshold (similar to offline credit limits). Risk: double-spend across terminals |
| 2 | Return fraud scoring | ML-based scoring to flag suspicious return patterns (high frequency, high value, serial returners) |
| 3 | Manufacturer RMA integration | Return-to-vendor flow for defective goods. Creates purchase return orders in Procurement module |
| 4 | Return shipping labels | Generate return shipping labels for e-commerce channel returns processed at POS |
| 5 | Automated inter-store stock rebalancing | After cross-store returns, auto-generate inter-warehouse transfer orders to move stock back to the original store |
| 6 | Warranty tracking | Link returns to product warranty records. Auto-validate warranty period before accepting return |
| 7 | Credit note transfer | Allow customers to transfer credit note balance to another customer |
| 8 | Return analytics dashboard | Top return reasons, return rate by product/store, cost of returns, credit note aging report |
| 9 | No-receipt return with ID | Allow returns without receipt by looking up customer's purchase history. Issue credit note only (no cash) |
| 10 | Configurable return eligibility by product | Product-level return rules (non-returnable, exchange-only, shortened window for perishables) |

---

## Appendix A — Entity Relationship Summary (Addendum 39 Additions)

```
pos.ReturnReasons (NEW)
  └── M:1 → tenant.Organizations

pos.ReturnPolicies (NEW)
  ├── M:1 → tenant.Organizations
  └── M:1 → pos.Stores (nullable — NULL = org default)

pos.CreditNotes (NEW)
  ├── M:1 → tenant.Organizations
  ├── M:1 → customers.Customers (nullable — bearer CN)
  ├── M:1 → pos.PosOrders (return_order_id — nullable)
  ├── M:1 → pos.Stores (issued_at_store)
  ├── M:1 → pos.Stores (last_used_at_store — nullable)
  └── 1:M → pos.CreditNoteTransactions (NEW)
              ├── M:1 → pos.PosOrders (nullable)
              └── M:1 → pos.Stores

pos.PosOrders (MODIFIED — 7 columns added)
  ├── M:1 → pos.Stores (original_store_id — nullable, cross-store)
  ├── M:1 → pos.ReturnReasons (return_reason_id — nullable)
  ├── M:1 → auth.Users (authorized_by — nullable)
  └── M:1 → pos.CreditNotes (credit_note_id — nullable)

pos.PosOrderLines (MODIFIED — returned_qty added)

pos.PosPayments (MODIFIED — credit_note_id + method=6 added)
  └── M:1 → pos.CreditNotes (credit_note_id — nullable)

pos.Shifts (MODIFIED — 2 credit note total columns added)

pos.Stores (MODIFIED — return_policy_id FK added)
  └── M:1 → pos.ReturnPolicies (nullable)
```

---

## Appendix B — Return Type Decision Matrix

```
                      ┌─────────────────────┐
                      │  Customer wants to   │
                      │  return items        │
                      └──────────┬──────────┘
                                 │
                    ┌────────────┼────────────┐
                    │                         │
            Same store as              Different store
            original sale              from original sale
                    │                         │
                    │                   ┌─────┴──────┐
                    │                   │Online?      │
                    │                   ├─────────────┤
                    │                   │YES          │NO → Block
                    │                   └──────┬──────┘
                    │                          │
                    └────────────┬─────────────┘
                                 │
                    ┌────────────┼────────────┐
                    │                         │
              Just returning          Also buying
              items                   new items
                    │                         │
              order_type =            order_type =
              RETURN (1)              EXCHANGE (2)
                    │                         │
                    │                    Net amount?
                    │              ┌─────┼──────┐
                    │              │     │      │
                    │            >0    =0     <0
                    │          (pay)  (even) (refund)
                    │              │     │      │
                    └──────────────┴─────┴──────┘
                                 │
                          Select refund
                          method (if applicable)
                    ┌────────┬───────┬──────────┐
                    │        │       │          │
                  Cash    Card   Credit    Original
                refund   refund   Note     Method
```

---

*End of FSD Addendum 39*
