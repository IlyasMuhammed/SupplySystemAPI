# SMS Task Register — FSD Addendum 39

## POS Returns, Exchanges & Credit Notes

| Property | Value |
|---|---|
| Document Ref | SMS-FSD-ADD-039 |
| Total Changes | 12 (C1–C12) |
| Migrations | 14 (M039.1–M039.14) |
| New Tables | 4 |
| New View | 1 |
| Modified Tables | 5 |
| API Endpoints | 24 |
| Permissions | 10 new |
| Test Scenarios | 35 |
| Dev Phases | 7 (26 days) |
| Depends On | SMS-FSD-ADD-038 (POS Module) |

---

## Phase 1 — Return Reasons + Return Policies (3 days)

### M039.1 — Create `pos.ReturnReasons` Table

- [ ] Create table `pos.ReturnReasons` with all 14 columns (`return_reason_id` PK IDENTITY, `org_id`, `code` VARCHAR(20), `name` NVARCHAR(100), `description`, `requires_note`, `requires_manager`, `applies_to` TINYINT, `restocking_fee_override` DECIMAL(5,2) NULL, `sort_order`, `is_active`, `created_at`, `created_by`, `modified_at`, `modified_by`)
- [ ] Add FK `org_id → tenant.Organizations(org_id)`
- [ ] Add FK `created_by → auth.Users(user_id)`
- [ ] Add CHECK constraint `CK_ReturnReasons_AppliesTo CHECK (applies_to IN (0,1,2))`
- [ ] Add UNIQUE constraint `UQ_ReturnReasons_OrgCode (org_id, code)`
- [ ] Add index `IX_ReturnReasons_OrgActive (org_id, is_active, sort_order)`

### M039.2 — Create `pos.ReturnPolicies` Table

- [ ] Create table `pos.ReturnPolicies` with all 21 columns (`return_policy_id` PK IDENTITY, `org_id`, `store_id` NULL, `name`, `return_window_days` NULL, `exchange_window_days` NULL, `require_receipt`, `allow_no_receipt_exchange`, `allow_cross_store_return`, `cross_store_requires_manager`, `restocking_fee_pct` DECIMAL(5,2), `max_return_no_auth` DECIMAL(18,4) NULL, `refund_to_original_method`, `allow_cash_refund`, `allow_credit_note_refund`, `credit_note_expiry_days` NULL, `is_active`, `created_at`, `created_by`, `modified_at`, `modified_by`, `row_version` ROWVERSION)
- [ ] Add FK `org_id → tenant.Organizations(org_id)`
- [ ] Add FK `store_id → pos.Stores(store_id)`
- [ ] Add FK `created_by → auth.Users(user_id)`
- [ ] Add filtered UNIQUE index `UQ_ReturnPolicies_OrgStore (org_id, store_id) WHERE store_id IS NOT NULL`
- [ ] Add index `IX_ReturnPolicies_OrgActive (org_id, is_active)`

### M039.3 — ALTER `pos.Stores` — Add `return_policy_id` FK

- [ ] Add column `return_policy_id BIGINT NULL` to `pos.Stores`
- [ ] Add FK `return_policy_id → pos.ReturnPolicies(return_policy_id)`

### M039.8 — Seed Default Return Reasons

- [ ] Seed 9 default return reasons per tenant: `DEFECTIVE`, `WRONG_ITEM`, `WRONG_SIZE`, `NOT_AS_EXPECTED`, `CHANGED_MIND`, `DUPLICATE`, `PRICE_MATCH`, `WARRANTY`, `OTHER`
- [ ] Set `requires_note = true` on: `NOT_AS_EXPECTED`, `PRICE_MATCH`, `WARRANTY`, `OTHER`
- [ ] Set `requires_manager = true` on: `PRICE_MATCH`, `WARRANTY`
- [ ] All reasons default `applies_to = 0` (Both), `is_active = 1`

### M039.9 — Seed Default Return Policy (Org-Wide)

- [ ] Insert org-wide default policy (`store_id = NULL`) per tenant with: `return_window_days = 30`, `require_receipt = true`, `allow_cross_store_return = true`, `cross_store_requires_manager = true`, `restocking_fee_pct = 0`, `allow_cash_refund = true`, `allow_credit_note_refund = true`, `refund_to_original_method = false`

### C1 — ReturnReasonService

- [ ] Create `ReturnReasonService` class
- [ ] Implement `GetActiveReasonsAsync(long orgId)` — list active reasons sorted by `sort_order`
- [ ] Implement `CreateReasonAsync(CreateReturnReasonDto dto)` — validate unique `code` per org
- [ ] Implement `UpdateReasonAsync(long id, UpdateReturnReasonDto dto)` — validate code uniqueness if changed
- [ ] Implement `SetStatusAsync(long id, bool isActive)` — activate/deactivate
- [ ] Filter by `applies_to` for return-only vs exchange-only vs both

### C2 — ReturnPolicyService

- [ ] Create `ReturnPolicyService` class
- [ ] Implement `GetEffectivePolicyAsync(long orgId, long storeId)` — store-specific → org default → throw if none
- [ ] Implement `CreateOrgDefaultPolicyAsync(CreatePolicyDto dto)` — `store_id = NULL`
- [ ] Implement `UpsertStorePolicyAsync(long storeId, UpsertPolicyDto dto)` — create/update store-specific override
- [ ] Implement `DeleteStorePolicyAsync(long storeId)` — remove store override, fallback to org default
- [ ] Implement `ListPoliciesAsync(long orgId)` — org-wide + all store-specific

### Phase 1 — API Endpoints

- [ ] `GET /api/pos/return-reasons` — list active return reasons (auth: `pos.order.return`)
- [ ] `POST /api/pos/return-reasons` — create return reason (auth: `pos.return.reason.manage`)
- [ ] `PUT /api/pos/return-reasons/{id}` — update return reason (auth: `pos.return.reason.manage`)
- [ ] `PATCH /api/pos/return-reasons/{id}/status` — activate/deactivate (auth: `pos.return.reason.manage`)
- [ ] `GET /api/pos/stores/{storeId}/return-policy` — get effective policy with fallback (auth: `pos.store.view`)
- [ ] `PUT /api/pos/stores/{storeId}/return-policy` — create/update store-specific policy (auth: `pos.return.policy.manage`)
- [ ] `GET /api/pos/return-policies` — list all policies (auth: `pos.return.policy.manage`)
- [ ] `POST /api/pos/return-policies` — create org-wide default (auth: `pos.return.policy.manage`)
- [ ] `DELETE /api/pos/stores/{storeId}/return-policy` — remove store override (auth: `pos.return.policy.manage`)

### Phase 1 — EF Core Configuration

- [ ] Create `ReturnReason` entity class with all properties
- [ ] Create `ReturnPolicy` entity class with all properties + `row_version` concurrency token
- [ ] Create `ReturnReasonConfiguration : IEntityTypeConfiguration<ReturnReason>` — indexes, FKs, CHECK, HasQueryFilter org_id
- [ ] Create `ReturnPolicyConfiguration : IEntityTypeConfiguration<ReturnPolicy>` — indexes, FKs, filtered UQ, HasQueryFilter org_id
- [ ] Add `DbSet<ReturnReason> ReturnReasons` to `PosDbContext`
- [ ] Add `DbSet<ReturnPolicy> ReturnPolicies` to `PosDbContext`
- [ ] Update `Store` entity — add `ReturnPolicyId` nullable FK + navigation property

### Phase 1 — DTOs & Validation

- [ ] Create `CreateReturnReasonDto` with FluentValidation (`code` max 20, `name` max 100, `applies_to` ∈ {0,1,2})
- [ ] Create `UpdateReturnReasonDto`
- [ ] Create `ReturnReasonResponseDto`
- [ ] Create `CreatePolicyDto` / `UpsertPolicyDto` with FluentValidation (`restocking_fee_pct` 0–100, `return_window_days` ≥ 0)
- [ ] Create `ReturnPolicyResponseDto`

---

## Phase 2 — Order Extensions + Same-Store Return Flow (5 days)

### M039.4 — ALTER `pos.PosOrders` — Add Return Extension Columns

- [ ] Add column `original_store_id BIGINT NULL` to `pos.PosOrders`
- [ ] Add column `return_reason_id BIGINT NULL` to `pos.PosOrders`
- [ ] Add column `authorized_by BIGINT NULL` to `pos.PosOrders`
- [ ] Add column `authorized_at DATETIME2(2) NULL` to `pos.PosOrders`
- [ ] Add column `credit_note_id BIGINT NULL` to `pos.PosOrders` (FK added after M039.10)
- [ ] Add column `restocking_fee_amount DECIMAL(18,4) NOT NULL DEFAULT 0` to `pos.PosOrders`
- [ ] Add column `is_cross_store BIT NOT NULL DEFAULT 0` to `pos.PosOrders`
- [ ] Add FK `original_store_id → pos.Stores(store_id)`
- [ ] Add FK `return_reason_id → pos.ReturnReasons(return_reason_id)`
- [ ] Add FK `authorized_by → auth.Users(user_id)`
- [ ] Add filtered index `IX_PosOrders_OriginalStore (original_store_id) WHERE original_store_id IS NOT NULL`
- [ ] Add filtered index `IX_PosOrders_ReturnReason (return_reason_id) WHERE return_reason_id IS NOT NULL`

### M039.5 — ALTER `pos.PosOrderLines` — Add `returned_qty`

- [ ] Add column `returned_qty DECIMAL(18,4) NOT NULL DEFAULT 0` to `pos.PosOrderLines`
- [ ] Verify application-level calculation: `returnable_qty = quantity - returned_qty`

### M039.14 — Create `pos.vw_ReturnableOrderLines` View + Indexes

- [ ] Create view `pos.vw_ReturnableOrderLines` joining `PosOrders` + `PosOrderLines` — columns: `order_id`, `org_id`, `store_id`, `order_number`, `customer_id`, `order_date`, `line_id`, `product_id`, `product_name`, `product_sku`, `original_qty`, `returned_qty`, `returnable_qty`, `unit_price`, `tax_percentage`, `line_total`, `return_status` (None/Partial/Full)
- [ ] Filter: `order_type = 0 (SALE) AND status IN (1,4) AND is_return_line = 0 AND quantity > returned_qty`

### C7 — PosReturnService (Same-Store Return Flow)

- [ ] Create `PosReturnService` class
- [ ] Implement `ProcessReturnAsync(ProcessReturnDto dto)` — full orchestration method
- [ ] Step 1 — Load original order with lines, validate not null
- [ ] Step 2 — Validate order status: block if `Voided`, block if `Returned` (fully returned)
- [ ] Step 3 — Validate `order_type = SALE(0)` only — block returns against returns/exchanges (POS-RET-07)
- [ ] Step 4 — Get effective return policy via `ReturnPolicyService.GetEffectivePolicyAsync`
- [ ] Step 5 — Check return window: `original.CreatedAt + policy.ReturnWindowDays` vs `UtcNow`. Throw `PolicyException(requiresManagerOverride: true)` if expired
- [ ] Step 6 — Validate each return line: `returnLine.Quantity ≤ originalLine.Quantity - originalLine.ReturnedQty`
- [ ] Step 7 — Calculate restocking fee: `reason.RestockingFeeOverride ?? policy.RestockingFeePct`. Apply: `restockingFee = returnSubtotal × (feePct / 100)`. `refundAmount = returnSubtotal - restockingFee`
- [ ] Step 8 — Authorization check: `RequiresAuthorization(refundAmount, policy, reason)` — checks `max_return_no_auth` threshold + `reason.requires_manager`
- [ ] Step 9 — Begin transaction scope (`BeginTransactionAsync`)
- [ ] Step 10 — Create return `PosOrder`: `order_type = RETURN(1)`, `original_order_id`, `return_reason_id`, `return_reason` (free text), `authorized_by/at` (if applicable), `restocking_fee_amount`, `status = Completed(1)`
- [ ] Step 11 — Create return `PosOrderLines`: negative quantities (`qty = -return_qty`), `is_return_line = true`, `original_line_id`, prices snapshot from original
- [ ] Step 12 — Update original line `returned_qty += return_qty` for each returned line
- [ ] Step 13 — Update original order status: `Returned(3)` if all lines fully returned, `PartialReturn(4)` if partially
- [ ] Step 14 — Create `PosPayment`: `is_refund = true`, `amount = refundAmount`, `method = selected`
- [ ] Step 15 — Call `PosStockService.ReturnStockAsync` — create `StockMovement(POS_RETURN, IN)` at processing store warehouse
- [ ] Step 16 — Call `PosJournalService.CreateReturnJournalAsync` — reverse revenue JE
- [ ] Step 17 — Call `PosShiftService.UpdateReturnTotalsAsync` — update `total_cash_refunds`, `total_card_refunds`, `refund_count`, cashier session totals
- [ ] Step 18 — Commit transaction, return `ReturnResult`
- [ ] Step 19 — Rollback on exception

### C7 — PosStockService Extension (Return Stock)

- [ ] Implement `ReturnStockAsync(PosOrder returnOrder)` — for each return line, create `StockMovement`: `warehouse_id = processing store warehouse`, `movement_type = POS_RETURN`, `direction = IN`, `quantity = |line.Quantity|`
- [ ] Reference note: `"Return: {returnOrder.OrderNumber}"` (same-store) or `"Cross-store return. Original: {originalOrder.OrderNumber} ({originalStore.Name})"` (cross-store)

### C7 — PosJournalService Extension (Return Journal)

- [ ] Implement `CreateReturnJournalAsync(PosOrder returnOrder)` — same-store return reversal JE
- [ ] Debit Sales Revenue (return subtotal), Debit Tax Payable (tax)
- [ ] Credit Cash Account / Card Account / Credit Note Liability (based on refund method)
- [ ] If restocking fee > 0: Credit Restocking Fee Income

### Phase 2 — API Endpoints

- [ ] `GET /api/pos/orders/lookup?receipt={orderNumber}` — lookup order by receipt (cross-store capable), return order detail + returnable lines + applicable policy (auth: `pos.order.return`)
- [ ] `GET /api/pos/orders/{id}/returnable-lines` — returnable lines with `returned_qty`, `returnable_qty` (auth: `pos.order.return`)
- [ ] `POST /api/pos/orders/{id}/return` — process return with policy check, reason, auth, credit note (auth: `pos.order.return`)
- [ ] `POST /api/pos/returns/authorize` — manager authorization for policy-exceeding returns (auth: `pos.return.override`)
- [ ] `POST /api/pos/returns/sync` — sync offline return order from PWA (auth: `pos.order.return`)

### Phase 2 — DTOs & Validation

- [ ] Create `ProcessReturnDto`: `OriginalOrderId`, `ProcessingStoreId`, `ReturnReasonId`, `ReturnReason` (free text), `RefundMethod` enum, `ManagerOverride` bool, `ManagerPin`, `ReturnLines[]`
- [ ] Create `ReturnLineDto`: `OriginalLineId`, `Quantity` (positive value, will be negated)
- [ ] Create `ReturnResult`: `ReturnOrder`, `CreditNote` (nullable)
- [ ] Create `OrderLookupResponseDto`: `Found`, `Order` (detail + lines with returnable qty), `Policy` snapshot
- [ ] Create `ReturnableLineDto`: `LineId`, `ProductId`, `ProductName`, `ProductSku`, `Quantity`, `ReturnedQty`, `ReturnableQty`, `UnitPrice`, `LineTotal`

### Phase 2 — EF Core Updates

- [ ] Update `PosOrder` entity — add 7 new properties + navigation properties (`OriginalStore`, `ReturnReason`, `AuthorizedByUser`, `CreditNote`)
- [ ] Update `PosOrderLine` entity — add `ReturnedQty` property
- [ ] Update `PosOrderConfiguration` — configure new FKs, filtered indexes
- [ ] Update `PosOrderLineConfiguration` — add `ReturnedQty` column config

### Phase 2 — Business Rules Verification

- [ ] POS-RET-01: Return window enforcement with manager override
- [ ] POS-RET-02: Exchange window separate from return window
- [ ] POS-RET-03: Receipt required per policy
- [ ] POS-RET-04: Returnable quantity limit (`return_qty ≤ quantity - returned_qty`)
- [ ] POS-RET-05: Fully returned block (all lines `returned_qty = quantity`)
- [ ] POS-RET-06: Voided order block
- [ ] POS-RET-07: Return-against-return block (only SALE orders targetable)
- [ ] POS-RET-08: Reason required + note mandatory if `requires_note`
- [ ] POS-RET-09: Manager auth for reason if `requires_manager`
- [ ] POS-RET-10: Manager auth for amount exceeding `max_return_no_auth`
- [ ] POS-RET-11: Restocking fee calculation with reason-level override
- [ ] POS-RET-12: Return any shift (not limited to original sale's shift)
- [ ] POS-RET-13: Original status update (Returned / PartialReturn)
- [ ] POS-RET-14: Return order created with `status = Completed(1)` immediately

---

## Phase 3 — Credit Notes + Credit Note Transactions (4 days)

### M039.10 — Create `pos.CreditNotes` Table

- [ ] Create table `pos.CreditNotes` with all 22 columns (`credit_note_id` PK IDENTITY, `org_id`, `credit_note_number` VARCHAR(30), `customer_id` NULL, `return_order_id` NULL, `original_amount`, `remaining_balance`, `currency_code` VARCHAR(3), `status` TINYINT DEFAULT 0, `issued_at_store_id`, `issued_by`, `issued_at`, `expires_at` NULL, `redeemed_amount` DEFAULT 0, `last_used_at` NULL, `last_used_at_store_id` NULL, `note` NULL, `local_id` NULL, `is_synced` DEFAULT 0, `created_at`, `modified_at`, `row_version` ROWVERSION)
- [ ] Add FK `org_id → tenant.Organizations(org_id)`
- [ ] Add FK `customer_id → customers.Customers(customer_id)`
- [ ] Add FK `return_order_id → pos.PosOrders(order_id)`
- [ ] Add FK `issued_at_store_id → pos.Stores(store_id)`
- [ ] Add FK `issued_by → auth.Users(user_id)`
- [ ] Add FK `last_used_at_store_id → pos.Stores(store_id)`
- [ ] Add CHECK constraint `CK_CreditNotes_Status CHECK (status IN (0,1,2,3))`
- [ ] Add UNIQUE constraint `UQ_CreditNotes_OrgNumber (org_id, credit_note_number)`
- [ ] Add filtered UNIQUE index `UQ_CreditNotes_OrgLocalId (org_id, local_id) WHERE local_id IS NOT NULL`
- [ ] Add index `IX_CreditNotes_CustomerStatus (customer_id, status)`
- [ ] Add index `IX_CreditNotes_OrgStatusExpiry (org_id, status, expires_at)` — expiry job scan
- [ ] Add filtered index `IX_CreditNotes_ReturnOrder (return_order_id) WHERE return_order_id IS NOT NULL`

### M039.10 (cont.) — Add CreditNote FK to PosOrders

- [ ] Add FK `pos.PosOrders.credit_note_id → pos.CreditNotes(credit_note_id)`
- [ ] Add filtered index `IX_PosOrders_CreditNote (credit_note_id) WHERE credit_note_id IS NOT NULL`

### M039.11 — Create `pos.CreditNoteTransactions` Table

- [ ] Create table `pos.CreditNoteTransactions` with all 14 columns (`transaction_id` PK IDENTITY, `org_id`, `credit_note_id`, `transaction_type` TINYINT, `amount`, `balance_after`, `pos_order_id` NULL, `store_id`, `performed_by`, `performed_at`, `note` NULL, `local_id` NULL, `is_synced` DEFAULT 0, `created_at`)
- [ ] Add FK `org_id → tenant.Organizations(org_id)`
- [ ] Add FK `credit_note_id → pos.CreditNotes(credit_note_id)`
- [ ] Add FK `pos_order_id → pos.PosOrders(order_id)`
- [ ] Add FK `store_id → pos.Stores(store_id)`
- [ ] Add FK `performed_by → auth.Users(user_id)`
- [ ] Add CHECK constraint `CK_CNTxn_Type CHECK (transaction_type IN (0,1,2,3,4))`
- [ ] Add index `IX_CNTxn_CreditNote (credit_note_id, performed_at)`
- [ ] Add filtered index `IX_CNTxn_Order (pos_order_id) WHERE pos_order_id IS NOT NULL`
- [ ] Add index `IX_CNTxn_Store (store_id, performed_at)`
- [ ] Add filtered UNIQUE index `UQ_CNTxn_OrgLocalId (org_id, local_id) WHERE local_id IS NOT NULL`

### M039.6 — ALTER `pos.PosPayments` — Add `credit_note_id` + Expand Method CHECK

- [ ] Add column `credit_note_id BIGINT NULL` to `pos.PosPayments`
- [ ] Add FK `credit_note_id → pos.CreditNotes(credit_note_id)`
- [ ] Drop existing CHECK constraint `CK_PosPayments_Method`
- [ ] Add updated CHECK constraint `CK_PosPayments_Method CHECK (method IN (0,1,2,3,4,5,6))` — adds `CreditNote(6)`
- [ ] Add filtered index `IX_PosPayments_CreditNote (credit_note_id) WHERE credit_note_id IS NOT NULL`

### M039.7 — ALTER `pos.Shifts` — Add Credit Note Totals

- [ ] Add column `total_credit_note_issued DECIMAL(18,4) NOT NULL DEFAULT 0` to `pos.Shifts`
- [ ] Add column `total_credit_note_redeemed DECIMAL(18,4) NOT NULL DEFAULT 0` to `pos.Shifts`
- [ ] Verify expected cash formula unchanged: CN transactions do not affect cash drawer

### C4 + C5 — CreditNoteService

- [ ] Create `CreditNoteService` class
- [ ] Implement `IssueCreditNoteAsync(long? customerId, decimal amount, PosOrder returnOrder, long storeId)` — create CN with auto-generated number `CN-{storeCode}-{YYYYMMDD}-{seq:D4}`
- [ ] Implement `LookupByNumberAsync(string creditNoteNumber)` — lookup for redemption
- [ ] Implement `GetByIdAsync(long id)` — full detail with transaction history
- [ ] Implement `GetCustomerCreditNotesAsync(long customerId, CreditNoteStatus? status)` — list customer's CNs
- [ ] Implement `RedeemAsync(long creditNoteId, decimal amount, PosOrder order, long storeId)` — validate: status = Active, balance ≥ amount, not expired. Decrement `remaining_balance`, increment `redeemed_amount`, update `last_used_at/store`. If `remaining_balance = 0` set `status = FullyRedeemed(1)`. Create `CreditNoteTransaction(Redeem)`
- [ ] Implement `CancelAsync(long creditNoteId, string note)` — validate: status = Active, `remaining_balance > 0`. Set `status = Cancelled(3)`. Create `CreditNoteTransaction(Cancel)`
- [ ] Implement `IssueStandaloneAsync(IssueCreditNoteDto dto)` — manual issuance (not from return)
- [ ] Implement `GetTransactionsAsync(long creditNoteId)` — transaction audit log
- [ ] Online-only redemption enforcement: throw if terminal offline
- [ ] Credit note expiry check on redemption: `expires_at != null && DateTime.UtcNow > expires_at → throw`
- [ ] POS-CN-10: No change from CN — payment capped at `min(remainingBalance, orderRemaining)`

### C6 — Payment Method Extension

- [ ] Update `PaymentMethod` enum — add `CreditNote = 6`
- [ ] Update payment processing logic to handle `CreditNote` method: call `CreditNoteService.RedeemAsync`
- [ ] Support split payment with CN + other methods (POS-CN-09)

### Phase 3 — API Endpoints

- [ ] `POST /api/pos/credit-notes` — standalone issuance (auth: `pos.credit_note.issue`)
- [ ] `GET /api/pos/credit-notes/{id}` — credit note details (auth: `pos.credit_note.view`)
- [ ] `GET /api/pos/credit-notes/lookup?number={cn_number}` — lookup by number for redemption (auth: `pos.credit_note.view`)
- [ ] `GET /api/pos/customers/{id}/credit-notes` — customer's credit notes with status filter (auth: `pos.credit_note.view`)
- [ ] `POST /api/pos/credit-notes/{id}/redeem` — redeem as payment (auth: `pos.credit_note.redeem`)
- [ ] `PATCH /api/pos/credit-notes/{id}/cancel` — cancel active CN (auth: `pos.credit_note.cancel`)
- [ ] `GET /api/pos/credit-notes/{id}/transactions` — transaction history (auth: `pos.credit_note.view`)

### Phase 3 — EF Core Configuration

- [ ] Create `CreditNote` entity class with all properties + `row_version` concurrency token
- [ ] Create `CreditNoteTransaction` entity class with all properties
- [ ] Create `CreditNoteConfiguration : IEntityTypeConfiguration<CreditNote>` — indexes, FKs, CHECK, HasQueryFilter org_id
- [ ] Create `CreditNoteTransactionConfiguration : IEntityTypeConfiguration<CreditNoteTransaction>` — indexes, FKs, HasQueryFilter org_id
- [ ] Add `DbSet<CreditNote> CreditNotes` to `PosDbContext`
- [ ] Add `DbSet<CreditNoteTransaction> CreditNoteTransactions` to `PosDbContext`
- [ ] Update `PosPayment` entity — add `CreditNoteId` nullable FK + navigation property
- [ ] Update `Shift` entity — add `TotalCreditNoteIssued`, `TotalCreditNoteRedeemed` properties

### Phase 3 — DTOs & Validation

- [ ] Create `IssueCreditNoteDto` with FluentValidation (`original_amount > 0`, `currency_code` required 3-char)
- [ ] Create `RedeemCreditNoteDto` (`amount > 0`)
- [ ] Create `CancelCreditNoteDto` (`note` optional)
- [ ] Create `CreditNoteResponseDto` (includes balance, status, expiry, issued store)
- [ ] Create `CreditNoteTransactionResponseDto`
- [ ] Create `CustomerCreditNotesResponseDto` (list with status filter)

### Phase 3 — Business Rules Verification

- [ ] POS-CN-01: Customer optional — NULL = bearer credit note
- [ ] POS-CN-02: Cross-store redemption at any store in org
- [ ] POS-CN-03: Partial redemption preserves remaining balance
- [ ] POS-CN-04: Online-only redemption (prevents double-spend)
- [ ] POS-CN-05: Expiry enforcement on redemption
- [ ] POS-CN-06: Only Active CNs with balance can be cancelled
- [ ] POS-CN-07: FullyRedeemed CNs cannot be cancelled/adjusted
- [ ] POS-CN-08: Every balance change creates CreditNoteTransaction
- [ ] POS-CN-09: Split payment (CN + other methods)
- [ ] POS-CN-10: No cash change from CN — excess stays on CN

---

## Phase 4 — Cross-Store Return + Exchange Flow (4 days)

### C8 — Cross-Store Return Flow

- [ ] Implement cross-store order lookup in `GET /api/pos/orders/lookup` — query all stores' orders by receipt number, return `isSameStore` flag + `daysSincePurchase`
- [ ] Validate processing store policy: `allow_cross_store_return = true` (POS-XST-02)
- [ ] Enforce manager authorization if `cross_store_requires_manager = true` (POS-XST-03)
- [ ] Apply original store's return window (POS-XST-06) — the processing store policy only controls acceptance
- [ ] Set `is_cross_store = true` + `original_store_id` on return order
- [ ] Cross-store stock routing: stock IN → processing store's warehouse, not original (POS-XST-04)
- [ ] Block cross-store returns when terminal offline (POS-XST-01)
- [ ] Create `POST /api/pos/orders/{id}/exchange` endpoint for exchange processing (auth: `pos.order.return`)

### C8 — Cross-Store Financial Reconciliation (Inter-Store JE)

- [ ] Implement `CreateCrossStoreReturnJournalAsync` — two journal entries:
  - [ ] JE 1 (processing store / Store B): Debit Inter-Store Receivable, Credit Cash Account
  - [ ] JE 2 (original store / Store A): Debit Sales Revenue + Tax Payable, Credit Inter-Store Payable
- [ ] Inter-Store Receivable (B) and Inter-Store Payable (A) net to zero during periodic reconciliation

### C9 — PosExchangeService

- [ ] Create `PosExchangeService` class (or extend `PosReturnService`)
- [ ] Implement `ProcessExchangeAsync(ProcessExchangeDto dto)` — steps 1–5 same as return, then add new items
- [ ] Mixed lines: negative (return) lines with `is_return_line = true` + positive (new sale) lines with `is_return_line = false`
- [ ] Net amount calculation: `net = new_total - (return_subtotal - restocking_fee)`
- [ ] Net > 0 → normal payment screen (customer pays difference)
- [ ] Net = 0 → even exchange, no payment record
- [ ] Net < 0 → refund method selection (customer receives |net|)
- [ ] Exchange order: `order_type = EXCHANGE(2)`, all lines in single order
- [ ] Dual stock movement: `POS_RETURN(IN)` for return lines + `POS_SALE(OUT)` for new lines
- [ ] Combined exchange journal entry: single JE with return reversal + new sale
- [ ] Update original `returned_qty` + status same as return flow
- [ ] Cross-store exchange: same cross-store rules + inter-store JE

### Phase 4 — DTOs

- [ ] Create `ProcessExchangeDto`: extends `ProcessReturnDto` + `NewItems[]` (product_id, quantity, unit_price, discount, tax)
- [ ] Create `ExchangeNewItemDto`: `ProductId`, `Quantity`, `UnitPrice`, `DiscountAmount`, `TaxPercentage`
- [ ] Create `ExchangeResult`: `ExchangeOrder`, `CreditNote` (nullable), `NetAmount`, `SettlementType` (Pay/Even/Refund)

### Phase 4 — Exchange Business Rules Verification

- [ ] POS-EXC-01: Mixed positive/negative lines in single order
- [ ] POS-EXC-02: Net settlement calculation
- [ ] POS-EXC-03: Net positive — customer pays
- [ ] POS-EXC-04: Net negative — customer refunded
- [ ] POS-EXC-05: Net zero — even swap
- [ ] POS-EXC-06: Exchange window from policy
- [ ] POS-EXC-07: New items from any category (no restriction)
- [ ] POS-EXC-08: Dual stock movement in same transaction

### Phase 4 — Cross-Store Business Rules Verification

- [ ] POS-XST-01: Online required
- [ ] POS-XST-02: Processing store's `allow_cross_store_return` policy check
- [ ] POS-XST-03: Manager authorization for cross-store
- [ ] POS-XST-04: Stock to processing store
- [ ] POS-XST-05: Inter-store finance (dual JE)
- [ ] POS-XST-06: Original store's return window applies

---

## Phase 5 — PWA Integration (Return + Exchange Screens) (5 days)

### C10 — Dexie.js Schema Extensions

- [ ] Add `returnReasons` table to Dexie schema v2: indexes on `returnReasonId`, `code`, `sortOrder`, `modifiedAt`
- [ ] Add `creditNotes` table to Dexie schema v2: indexes on `creditNoteId`, `creditNoteNumber`, `customerId`, `status`, `modifiedAt`
- [ ] Create `LocalReturnReason` TypeScript interface (9 fields)
- [ ] Create `LocalCreditNote` TypeScript interface (11 fields)

### C10 — SyncEngine Extensions

- [ ] Implement `pullReturnReasons()` — delta sync from `/api/sync/pos-return-reasons?since={iso}`, bulkPut to `returnReasons` table
- [ ] Implement `pullCreditNotes(customerId?)` — on-demand sync from `/api/pos/customers/{id}/credit-notes?status=active`, bulkPut to `creditNotes` table
- [ ] Extend `pullChanges()` — add return reasons to parallel fetch alongside catalog + customers
- [ ] Handle Dexie version upgrade (v1 → v2) for existing installations

### C10 — PWA Return Screens

- [ ] **Order Lookup Screen** (`/pos/return`) — receipt scan/number input, customer search, recent orders list. Auth: active cashier session + `pos.order.return`
- [ ] **Return Item Selection Screen** (`/pos/return/{orderId}`) — original order display, line checkboxes with returnable_qty, return qty spinner (max = returnable_qty), return reason dropdown (from local Dexie), note field (required if reason.requiresNote), return summary (subtotal, restocking fee, tax reversal, refund amount), "Exchange" / "Process Return" / "Cancel" actions
- [ ] **Refund Method Selection Screen** (`/pos/return/{orderId}/refund`) — Cash Refund, Refund to Original Method (shows card last 4), Store Credit (Credit Note) options per policy. Confirm Refund button
- [ ] **Exchange Screen** (`/pos/exchange/{orderId}`) — split layout: left = product grid (same as selling screen), right = exchange cart with RETURNING section (negative lines) + NEW ITEMS section (positive lines). Net amount calculation at bottom. "Complete Exchange" button
- [ ] **Credit Note Display** (`/pos/credit-note/{id}`) — barcode (scannable), CN number, customer, amount/balance, store, date, expiry, status, return reference. Print / Email / Done buttons
- [ ] **Order History Extension** (`/pos/history`) — add return filter, show return status badges (Returned, PartialReturn), link to return orders

### C10 — Offline Handling

- [ ] Same-store return: allowed if original order exists in local Dexie DB (⚠ conditional)
- [ ] Cross-store return: blocked offline — show "Cross-store returns require internet connectivity"
- [ ] Same-store exchange: same offline rules as same-store return
- [ ] Cross-store exchange: blocked offline
- [ ] Return reason selection: always works offline (cached in Dexie)
- [ ] Manager PIN: cached hash works offline (same as Addendum 38 model)
- [ ] Cash/card refund: recorded locally offline
- [ ] Credit note issuance: works offline — generate offline CN number `CN-{store}-OFF-{uuid_short}`, sync later
- [ ] Credit note redemption: blocked offline — show "Credit note redemption requires internet connectivity"
- [ ] Return receipt: generated locally offline

### C10 — PWA Mappers

- [ ] Create `mapReturnReason(serverDto → LocalReturnReason)` mapper
- [ ] Create `mapCreditNote(serverDto → LocalCreditNote)` mapper
- [ ] Create `mapReturnOrderForSync(localReturn → SyncReturnDto)` mapper for offline return sync

---

## Phase 6 — SignalR Events + Hangfire Jobs + Sync (2 days)

### C12 — SignalR Return Events

- [ ] Add `ReturnProcessed` event to `PosHub` — group: `store_{id}`, payload: `returnOrderId`, `originalOrderId`, `originalStoreId`, `refundAmount`, `refundMethod`
- [ ] Add `CreditNoteIssued` event — group: `store_{id}`, payload: `creditNoteId`, `creditNoteNumber`, `customerId`, `amount`, `expiresAt`
- [ ] Add `CreditNoteRedeemed` event — group: `store_{id}`, payload: `creditNoteId`, `redeemedAmount`, `remainingBalance`, `orderId`
- [ ] Add `CreditNoteExpired` event — group: `store_{id}`, payload: `creditNoteId`, `creditNoteNumber`, `customerId`, `expiredAmount`
- [ ] Add `ReturnPolicyChanged` event — group: `store_{id}`, payload: `storeId`, `policySnapshot`

### C12 — Hangfire Jobs

- [ ] Create `CreditNoteExpiryJob` — schedule: daily 00:30 UTC. Scan `pos.CreditNotes WHERE status = Active AND expires_at < GETUTCDATE()`. Set `status = Expired(2)`. Create `CreditNoteTransaction(Expire)`. Send `CreditNoteExpired` SignalR event
- [ ] Create `CrossStoreReconciliationReport` — schedule: weekly Monday 06:00 UTC. Generate inter-store receivable/payable aging report. Send notification to Finance module

### Phase 6 — Sync Endpoints

- [ ] `GET /api/sync/pos-return-reasons?since={iso}` — delta sync return reasons (auth: `pos.order.create`)
- [ ] `GET /api/sync/pos-return-policy?storeId={id}` — sync effective policy for PWA caching (auth: `pos.order.create`)

### Phase 6 — SignalR Event Triggering

- [ ] Fire `ReturnProcessed` after `ProcessReturnAsync` commits
- [ ] Fire `CreditNoteIssued` after CN issuance (from return or standalone)
- [ ] Fire `CreditNoteRedeemed` after `RedeemAsync` commits
- [ ] Fire `ReturnPolicyChanged` after policy create/update/delete

---

## Phase 7 — Permissions + Back-Office UI + Testing (3 days)

### M039.12 — Seed Permissions

- [ ] Seed `pos.return.cross_store` — "Process cross-store returns"
- [ ] Seed `pos.return.override` — "Override return policy"
- [ ] Seed `pos.return.policy.manage` — "Create and manage return policies"
- [ ] Seed `pos.return.reason.manage` — "Manage return reason catalog"
- [ ] Seed `pos.return.reports` — "View return analytics and reports"
- [ ] Seed `pos.credit_note.issue` — "Issue credit notes"
- [ ] Seed `pos.credit_note.redeem` — "Redeem credit notes as payment"
- [ ] Seed `pos.credit_note.cancel` — "Cancel active credit notes"
- [ ] Seed `pos.credit_note.view` — "View credit note details and history"
- [ ] Seed `pos.credit_note.reports` — "View credit note reports and aging"

### M039.12 — Default Role Mapping

- [ ] Cashier: `pos.credit_note.redeem` only
- [ ] Supervisor: `pos.order.return` (existing), `pos.return.cross_store`, `pos.return.reports`, `pos.credit_note.issue`, `pos.credit_note.redeem`, `pos.credit_note.view`
- [ ] Store Manager: all above + `pos.return.override`, `pos.credit_note.cancel`, `pos.credit_note.reports`
- [ ] Admin: all permissions

### M039.13 — Feature Flags

- [ ] Add `POS_CREDIT_NOTES` to `tenant.ModuleFeatures` (Toggleable)
- [ ] Add `POS_CROSS_STORE_RETURNS` to `tenant.ModuleFeatures` (Toggleable)
- [ ] Wire feature flag checks: credit note UI/API gated behind `POS_CREDIT_NOTES`
- [ ] Wire feature flag checks: cross-store return UI/API gated behind `POS_CROSS_STORE_RETURNS`

### C11 — Finance Account Mapping Extension

- [ ] Add `CreditNoteLiabilityAccountId` to `PosAccountMapping` (Current Liability)
- [ ] Add `RestockingFeeIncomeAccountId` to `PosAccountMapping` (Other Income)
- [ ] Add `InterStoreReceivableAccountId` to `PosAccountMapping` (Current Asset)
- [ ] Add `InterStorePayableAccountId` to `PosAccountMapping` (Current Liability)
- [ ] Back-office: extend store POS account mapping UI to include 4 new accounts

### Back-Office UI — Return Policy Configuration

- [ ] Add "Returns" tab to `/settings/pos/stores/{id}` store detail page
- [ ] Toggle: "Use store-specific policy" vs "Use organization default"
- [ ] Policy form: policy name, return/exchange window days, require receipt, allow no-receipt exchange, cross-store toggle + manager requirement, restocking fee %, max return without auth, refund method options (force original, allow cash, allow CN), CN expiry days
- [ ] Save / Cancel actions
- [ ] Org-wide default policy config accessible from POS settings root

### Back-Office UI — Return Reason Management

- [ ] Return reasons CRUD screen at `/settings/pos/return-reasons`
- [ ] Data grid: code, name, requires note, requires manager, applies to, sort order, active toggle
- [ ] Create/edit dialog with all fields
- [ ] Drag-and-drop or sort_order management

### Refund Rules Verification

- [ ] POS-REF-01: Refund methods governed by policy settings
- [ ] POS-REF-02: Force original method when `refund_to_original_method = true`
- [ ] POS-REF-03: Cash refund deducts from shift cash drawer
- [ ] POS-REF-04: Card refund recorded but not gateway-processed (v1)
- [ ] POS-REF-05: Credit note refund creates liability in finance

---

## Test Scenarios (35 total)

### Return Tests (TS-039-01 to TS-039-14)

- [ ] TS-039-01: Same-store full return — all lines, cash refund. Return order created, original status = Returned(3), cash refunded, stock IN, revenue reversed
- [ ] TS-039-02: Same-store partial return — 2 of 3 items. Original status = PartialReturn(4), returned_qty updated on 2 lines
- [ ] TS-039-03: Second partial return on same order. returned_qty accumulates, original stays PartialReturn or becomes Returned
- [ ] TS-039-04: Return blocked — all items already returned. Error: "All items in this order have already been returned"
- [ ] TS-039-05: Return blocked — voided order. Error: "Cannot return a voided order"
- [ ] TS-039-06: Return blocked — window expired. Error: "Return window expired (30 days)". Manager override available
- [ ] TS-039-07: Manager override on expired window. Manager PIN accepted, return processed with `authorized_by` recorded
- [ ] TS-039-08: Return blocked — qty exceeds returnable. Error: "Cannot return 5 of 'Coffee'. Only 3 returnable"
- [ ] TS-039-09: Return with "Defective" reason (no note required). return_reason_id set, no note validation
- [ ] TS-039-10: Return with "Other" reason (note required). Blank note → validation error
- [ ] TS-039-11: Return with "Price Match" reason (manager required). Manager PIN prompt regardless of amount
- [ ] TS-039-12: Return exceeding max_return_no_auth ($600 return, threshold $500). Manager PIN required
- [ ] TS-039-13: Return with 5% restocking fee. Refund = subtotal × 0.95. Fee in `restocking_fee_amount`. JE includes fee income
- [ ] TS-039-14: Return with reason-level fee override (0% waived). Fee = 0 despite store policy 5%. Reason override wins

### Credit Note Tests (TS-039-15, TS-039-25 to TS-039-30)

- [ ] TS-039-15: Refund as credit note. CN created, Transaction(Issue) logged, JE debits revenue + credits CN Liability
- [ ] TS-039-16: Refund forced to original method. Policy `refund_to_original_method = true`. Cashier cannot choose
- [ ] TS-039-25: CN redemption full — CN $35, order $30. Payment = $30, remaining = $5. Status stays Active
- [ ] TS-039-26: CN redemption exact — CN $35, order $35. Fully used. Status → FullyRedeemed(1)
- [ ] TS-039-27: CN split payment — order $50 = $35 CN + $15 cash. Both payments recorded
- [ ] TS-039-28: CN redemption blocked — expired. Error: "This credit note has expired"
- [ ] TS-039-29: CN redemption blocked — offline. Error: "Credit note redemption requires internet connectivity"
- [ ] TS-039-30: CN cancelled by admin. Status → Cancelled(3). Transaction logged. Balance preserved

### Cross-Store Tests (TS-039-17 to TS-039-19)

- [ ] TS-039-17: Cross-store return online. Store A order returned at Store B. Manager PIN. Stock → Store B. Inter-store JE
- [ ] TS-039-18: Cross-store return blocked offline. Error: "Cross-store returns require internet connectivity"
- [ ] TS-039-19: Cross-store return blocked by policy. `allow_cross_store_return = false`. Error: "This store does not accept returns from other locations"

### Exchange Tests (TS-039-20 to TS-039-24)

- [ ] TS-039-20: Exchange net positive — return $30 + buy $50 = customer pays $20. Mixed lines. Payment recorded
- [ ] TS-039-21: Exchange net negative — return $50 + buy $30 = customer refunded $20. Refund method shown
- [ ] TS-039-22: Exchange even swap — return $40 + buy $40. No payment recorded
- [ ] TS-039-23: Exchange with restocking fee — return $100 (5% = $5), buy $80. Net = -$15 refund
- [ ] TS-039-24: Cross-store exchange — return from Store A at Store B + buy new at B. Manager auth + inter-store JE

### Offline + Integration Tests (TS-039-31 to TS-039-35)

- [ ] TS-039-31: Offline same-store return (order in local DB). Processed offline, stored in Dexie, synced when online
- [ ] TS-039-32: Offline same-store return (order NOT in local DB). Error: "Original order not found locally. Connect to internet to search"
- [ ] TS-039-33: Return stock movement verification. StockMovement(POS_RETURN, IN) exists. Product stock increased at store warehouse
- [ ] TS-039-34: Return finance journal verification. JE debits Sales Revenue + Tax Payable, credits Cash Account
- [ ] TS-039-35: CN expiry Hangfire job. Expired CN status set to Expired. CreditNoteExpired SignalR event sent

---

## Summary Counters

| Category | Count | Status |
|---|---|---|
| Migration tasks | 36 | ⬜ Pending |
| Service implementation tasks | 48 | ⬜ Pending |
| API endpoint tasks | 24 | ⬜ Pending |
| EF Core / Entity tasks | 22 | ⬜ Pending |
| DTO / Validation tasks | 18 | ⬜ Pending |
| PWA / Frontend tasks | 20 | ⬜ Pending |
| SignalR / Hangfire tasks | 9 | ⬜ Pending |
| Permission / Feature flag tasks | 16 | ⬜ Pending |
| Back-office UI tasks | 6 | ⬜ Pending |
| Business rule verifications | 34 | ⬜ Pending |
| Test scenario verifications | 35 | ⬜ Pending |
| **Total** | **268** | ⬜ |

---

*Generated from SMS-FSD-ADD-039 v1.0 — POS Returns, Exchanges & Credit Notes*
