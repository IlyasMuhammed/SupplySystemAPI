# Addendum 38 — API contract (backend ⇄ PWA ⇄ back office)

`ApiResponse<T> { success, message, result }`, camelCase, **UUIDs in routes**, codes are strings, money 2dp in org sale-base currency.
Errors: 400 rule (message user-facing), 403 permission / `MODULE_NOT_LICENSED`, 404, 409 stale `rowVersion`/state. Decisions: [ADDENDUM-38-ANALYSIS.md](ADDENDUM-38-ANALYSIS.md).
All `api/pos/*` controllers: `[RequiresFeature(ModuleCodes.Pos)]` (+ sub-feature where noted). Hub: `/hubs/pos`.

## 0. Codes
- Shift `status`: `OPEN CLOSING CLOSED RECONCILED DISPUTED` (`DRAFT` reserved, unused — opening creates OPEN).
- Cash movement `direction`: `IN OUT`; `reason`: `FLOAT_TOP_UP SAFE_DROP PETTY_CASH CHANGE_RUN OTHER`.
- Staff `posRole`: `CASHIER SUPERVISOR`.
- Order `orderType`: `SALE RETURN`; `status`: `COMPLETED VOIDED RETURNED PARTIALLY_RETURNED`; `paymentStatus`: `PAID CREDIT_SALE PARTIAL_CREDIT REFUNDED`.
- Discount type: `NONE PERCENT AMOUNT`.
- Payment `method`: `CASH CARD MOBILE BANK_TRANSFER CHEQUE ON_ACCOUNT`.
- Order `flags[]`: `OFFLINE`, `OFFLINE_CREDIT`, `CREDIT_OVER_LIMIT`, `NEGATIVE_STOCK`, `PRICE_CHANGED`, `DISCOUNT_UNAUTHORIZED`.
- Posting state per order: `stockPosted`, `financePosted` (bool) + `postingError?` — the order is saved first; stock/finance posting are idempotent and retried by `PosPostingRetryJob` (every 15 min) on failure.

## 1. Totals calculator (server = source of truth; the PWA mirrors it exactly)
Round = 2dp, midpoint away from zero. Rate = store default sales tax code %.
1. Per line: `gross = round(qty × unitPrice)`; `lineDiscount = PERCENT ? round(gross × v/100) : AMOUNT ? min(v, gross) : 0`; `net = gross − lineDiscount`.
2. `subtotal = Σ net`. Order discount: `PERCENT ? round(subtotal × v/100) : AMOUNT ? min(v, subtotal) : 0` = `discountAmount`; allocated to lines proportionally to `net`, remainder on the last line → `netAfter`.
3. **Exclusive store**: `tax = round(netAfter × rate/100)`; `lineTotal = netAfter + tax`; `grandTotal = subtotal − discountAmount + taxTotal`.
   **Inclusive store**: `tax = round(netAfter − netAfter / (1 + rate/100))`; `lineTotal = netAfter`; `grandTotal = subtotal − discountAmount` (taxTotal informational).
4. Line DTO keeps `netAmount` = exclusive amount after all discounts (`netAfter` or `netAfter − tax`), `taxAmount`, `lineTotal` (= netAmount + taxAmount).
5. Tender: `Σ(amount − changeGiven) = grandTotal` (±0.01). Only CASH may have `changeGiven > 0`; CARD/MOBILE/BANK_TRANSFER/CHEQUE never exceed the remaining balance (POS-PAY-03). ON_ACCOUNT covers what remains (credit sale).

## 2. Stores, terminals, staff (POS-A)
| Route | Permission | Body → Result |
|---|---|---|
| `GET api/pos/stores?search&isActive&page&pageSize` | POS_STORE_VIEW | `Paginated<Store>` |
| `GET api/pos/stores/{uuid}` | POS_STORE_VIEW | `Store` |
| `POST api/pos/stores` | POS_STORE_MANAGE | `StoreUpsert` → `Store` |
| `PUT api/pos/stores/{uuid}` | POS_STORE_MANAGE | `StoreUpsert + rowVersion` → `Store` |
| `PATCH api/pos/stores/{uuid}/status` | POS_STORE_MANAGE | `{ isActive }` → `Store` (deactivate: refused while a shift is OPEN/CLOSING; deactivates terminals) |
| `GET api/pos/stores/{uuid}/terminals` | POS_TERMINAL_VIEW | `Terminal[]` |
| `POST api/pos/stores/{uuid}/terminals` | POS_TERMINAL_MANAGE | `{ code, name }` → `Terminal` |
| `PUT api/pos/terminals/{uuid}` | POS_TERMINAL_MANAGE | `{ code, name, isActive, rowVersion }` → `Terminal` |
| `POST api/pos/terminals/{uuid}/bind` | POS_TERMINAL_MANAGE **or** POS_SHIFT_OPEN | `{ deviceUuid }` → `TerminalConfig` (refused if bound to another device: "Unbind first") |
| `POST api/pos/terminals/{uuid}/unbind` | POS_TERMINAL_MANAGE | → `Terminal` |
| `POST api/pos/terminals/{uuid}/unlock` | POS_SHIFT_OPEN | clears PIN lockout → `Terminal` |
| `GET api/pos/terminals/by-device/{deviceUuid}` | any POS_* | `TerminalConfig` or 404 |
| `GET api/pos/terminals/{uuid}/status` | POS_TERMINAL_VIEW | `{ isOnline, lastSeenAt, lastSyncAt, deviceBound, activeShiftUuid?, pinLockedUntil? }` |
| `GET api/pos/terminals/available` | any POS_* | terminals of stores where the caller is staff (or all for POS_STORE_VIEW) |
| `GET api/pos/stores/{uuid}/staff` | POS_STORE_VIEW | `StaffMember[]` |
| `GET api/pos/stores/{uuid}/staff/candidates?search` | POS_STORE_MANAGE | `{ userId, name, email, roleName }[]` (active org users not yet staff) |
| `PUT api/pos/stores/{uuid}/staff/{userId}` | POS_STORE_MANAGE | `{ posRole, pin?: string }` → `StaffMember` (pin 4–6 digits, unique within the store) |
| `DELETE api/pos/stores/{uuid}/staff/{userId}` | POS_STORE_MANAGE | — |
```ts
Store { uuid; code; name; warehouseUuid; warehouseName; addressLine1?; addressLine2?; city?; stateProvince?; postalCode?; country?;
  phone?; email?; timezone; currencyCode /*org sale base, read-only*/; taxInclusivePricing; defaultTaxCodeUuid?; defaultTaxCode?; defaultTaxPercent;
  receiptHeader?; receiptFooter?; offlineCreditTolerancePct; autoCloseShiftHours; varianceThreshold; isActive;
  terminalCount; activeShiftCount; createdAt; modifiedAt; rowVersion }
StoreUpsert = Store minus (uuid, warehouseName, currencyCode, defaultTaxCode, defaultTaxPercent, counts, dates, rowVersion)
Terminal { uuid; storeUuid; storeName; code; name; deviceBound; deviceUuid?; lastSeenAt?; lastSyncAt?; isOnline /*seen < 15 min*/;
  isActive; activeShiftUuid?; activeShiftNumber?; pinLockedUntil?; rowVersion }
TerminalConfig { terminal: Terminal; store: Store; serverTime }
StaffMember { userId; name; email; roleName; posRole; hasPin; isActive }
```
400 texts: `"Warehouse is already linked to store {code}."` · `"Close all shifts before deactivating the store."` · `"PIN must be 4 to 6 digits."` · `"This PIN is already used by another staff member of this store."` · `"Terminal code already exists in this store."`.

## 3. PIN sign-in & cashier sessions (POS-A)
| Route | Auth | Body → Result |
|---|---|---|
| `POST api/pos/auth/pin` | signed in (device token, same org) | `{ terminalUuid, userId?, pin }` → `PinSignIn` |
| `POST api/pos/sessions` | POS_ORDER_CREATE | `{ shiftUuid, localId? }` → `CashierSession` (same user's active session resumes; another user's active session on the terminal is ended — POS-CSH-04) |
| `PATCH api/pos/sessions/{uuid}/end` | POS_ORDER_CREATE | → `CashierSession` |
```ts
PinSignIn { accessToken; refreshToken; user: { userId; name; posRole; permissions: string[] } }
CashierSession { uuid; shiftUuid; terminalUuid; userId; userName; startedAt; endedAt?; isActive;
  cashSales; cardSales; mobileSales; creditSales; refundTotal; orderCount }
```
`userId` omitted ⇒ the PIN identifies the staff member of the terminal's store. 5 consecutive failures on a terminal ⇒ locked 5 minutes (`"Terminal locked until HH:mm."`, POS-CSH-07). The user must be active staff of the store with POS_ORDER_CREATE. Tokens are the cashier's own (normal JWT + refresh).

## 4. Shifts & cash movements (POS-A)
| Route | Permission | Body → Result |
|---|---|---|
| `POST api/pos/terminals/{uuid}/shifts` | POS_SHIFT_OPEN | `{ openingFloat, varianceThreshold? }` → `Shift` (status OPEN; "Terminal already has an active shift.") |
| `GET api/pos/terminals/{uuid}/shifts/current` | POS_ORDER_CREATE or POS_SHIFT_VIEW | `Shift` or 404 |
| `GET api/pos/shifts/{uuid}` | POS_SHIFT_VIEW or (POS_ORDER_CREATE and own terminal) | `ShiftDetail` |
| `PATCH api/pos/shifts/{uuid}/start-close` | POS_SHIFT_CLOSE | OPEN → CLOSING |
| `PATCH api/pos/shifts/{uuid}/resume` | POS_SHIFT_CLOSE | CLOSING → OPEN |
| `PATCH api/pos/shifts/{uuid}/close` | POS_SHIFT_CLOSE | `{ actualCashClosing, varianceNote? }` → CLOSED → RECONCILED (\|variance\| ≤ threshold) or DISPUTED |
| `PATCH api/pos/shifts/{uuid}/reconcile` | POS_SHIFT_RECONCILE | `{ note }` (required) DISPUTED → RECONCILED |
| `PATCH api/pos/shifts/{uuid}/note` | POS_SHIFT_RECONCILE | `{ note }` append-only on RECONCILED (POS-SHF-10) |
| `GET api/pos/shifts?storeUuid&terminalUuid&status&from&to&page&pageSize` | POS_SHIFT_VIEW | `Paginated<Shift>` (newest first) |
| `GET api/pos/shifts/{uuid}/summary` | POS_SHIFT_VIEW or own terminal | `ShiftSummary` |
| `POST api/pos/shifts/{uuid}/cash-movements` | POS_CASH_MOVEMENT | `{ direction, amount>0, reason, note?, localId? }` → `CashMovement` (OPEN only; localId idempotent) |
| `GET api/pos/shifts/{uuid}/cash-movements` | as GET shift | `CashMovement[]` |
```ts
Shift { uuid; shiftNumber; storeUuid; storeName; terminalUuid; terminalName; status; openedByName; openedAt; closedByName?; closedAt?;
  reconciledByName?; reconciledAt?; openingFloat; totalCashSales; totalCardSales; totalMobileSales; totalOtherSales; totalCreditSales;
  totalCashRefunds; totalOtherRefunds; totalCashIn; totalCashOut; expectedCashClosing; actualCashClosing?; cashVariance?;
  varianceThreshold; varianceNote?; orderCount; refundCount; grossTotal; netTotal; rowVersion }
ShiftDetail extends Shift { sessions: CashierSession[]; cashMovements: CashMovement[] }
ShiftSummary { shift: Shift; byMethod: { method; sales; refunds; net }[]; byCashier: CashierSession[] }
CashMovement { uuid; direction; amount; reason; note?; performedByName; performedAt; localId? }
```
`expectedCashClosing = openingFloat + totalCashSales + totalCashIn − totalCashOut − totalCashRefunds`, recomputed server-side from orders/payments/movements on every change. Shift number `SH-{storeCode}-{yyyyMMdd}-{seq:00}` (store timezone date).

## 5. Orders & payments (POS-B)
| Route | Permission | Body → Result |
|---|---|---|
| `POST api/pos/orders/sync` | POS_ORDER_CREATE | `SyncOrder` → `SyncResult` (idempotent on `localId`) |
| `POST api/pos/orders/sync/batch` | POS_ORDER_CREATE | `{ orders: SyncOrder[] }` → `SyncResult[]` (processed in `createdAtLocal` order; one failure does not stop the rest) |
| `GET api/pos/orders/{uuid}` | POS_ORDER_VIEW (or own order with POS_ORDER_CREATE) | `PosOrder` |
| `GET api/pos/orders/by-local/{localId}` | POS_ORDER_CREATE | `PosOrder` |
| `GET api/pos/orders?storeUuid&terminalUuid&shiftUuid&customerUuid&status&orderType&from&to&search&page&pageSize` | POS_ORDER_VIEW | `Paginated<PosOrderListItem>` |
| `PATCH api/pos/orders/{uuid}/void` | POS_ORDER_VOID | `{ reason }` → `PosOrder` (same shift, shift OPEN/CLOSING, sale not returned) |
| `POST api/pos/orders/{uuid}/return` | POS_ORDER_RETURN + FEATURE_POS_RETURNS | `ReturnRequest` → `PosOrder` (the RETURN order) |
| `GET api/pos/customers/{uuid}/credit` | POS_ORDER_CREATE | `{ balance; creditLimit; available; allowPosCredit; maxSingleCreditOrder?; requiresIdForCredit; isWalkIn }` |
```ts
SyncOrder { localId; terminalUuid; shiftUuid; sessionUuid?; offlineNumber?; createdAtLocal; isOffline; customerUuid? /*null ⇒ walk-in*/;
  lines: { localLineId; variantUuid; quantity; unitPrice; discountType; discountValue; barcode?; note? }[];
  orderDiscountType; orderDiscountValue; note?;
  payments: { localId; method; amount; changeGiven; reference? }[];
  offlineCredit: boolean; clientTotals: { subtotal; discountAmount; taxTotal; grandTotal } }
SyncResult { localId; success; orderUuid?; orderNumber?; status?; flags?: string[]; warnings?: string[]; error? }
PosOrder { uuid; orderNumber; localId; offlineNumber?; orderType; status; storeUuid; storeName; terminalUuid; terminalName; shiftUuid; shiftNumber;
  sessionUuid?; cashierUserId; cashierName; customerUuid; customerCode; customerName; isWalkIn;
  lines: PosOrderLine[]; payments: PosPayment[];
  subtotal; discountType; discountValue; discountAmount; taxTotal; grandTotal; paidAmount; changeAmount; balanceDue; paymentStatus;
  taxInclusive; taxCode?; taxPercent; originalOrderUuid?; originalOrderNumber?; returnReason?; voidReason?; note?;
  isOffline; flags: string[]; stockPosted; financePosted; postingError?;
  salesInvoiceUuid?; salesInvoiceNumber?; creditNoteUuid?; creditNoteNumber?; createdAt; syncedAt; createdAtLocal; returns: { uuid; orderNumber; grandTotal }[] }
PosOrderLine { uuid; lineNumber; variantUuid; productName; variantName?; sku; barcode?; quantity; unitPrice; discountType; discountValue;
  discountAmount /*line + allocated order discount*/; netAmount; taxPercent; taxAmount; lineTotal; originalLineUuid?; returnedQuantity; returnableQuantity; note? }
PosPayment { uuid; method; amount; changeGiven; reference?; isRefund; customerPaymentUuid?; createdAt }
PosOrderListItem { uuid; orderNumber; orderType; status; storeName; terminalName; cashierName; customerName; grandTotal; paymentStatus; flags; createdAt; isOffline; financePosted; stockPosted }
ReturnRequest { localId; reason; shiftUuid? /*POSB*/; lines: { originalLineUuid; quantity }[]; refunds: { method; amount; reference? }[] }
```
Server rules: shift must be OPEN (or CLOSING for an order whose `createdAtLocal` precedes start-close; offline orders for an already closed shift are accepted and booked to it — totals recomputed, a CLOSED/RECONCILED shift gets flag via note); variant must be active, POS-available (`IsAvailableForPos`) and product saleable — offline orders are accepted with a warning instead of refused; price ≠ current catalog price ⇒ `PRICE_CHANGED` flag (cashier's price stands, POS-ORD-02); discount without POS_ORDER_DISCOUNT of the session user ⇒ 400 online / `DISCOUNT_UNAUTHORIZED` flag offline; ON_ACCOUNT needs FEATURE_POS_CREDIT_SALES, a non-walk-in customer with `allowPosCredit` (400 `"Walk-In customers cannot use credit."`, `"Credit limit exceeded. Limit: X. Balance: Y. This order: Z."` online; offline over limit ⇒ accepted + `CREDIT_OVER_LIMIT` + notification to store supervisors); totals recomputed with §1 — client totals differing > 0.01 ⇒ server totals win + warning. Return: quantity ≤ remaining returnable (`"Return quantity cannot exceed the original quantity."`); refunds Σ = return grand total; ON_ACCOUNT refund only for credit sales (reduces balance). Void: refused if returned or another shift (`"Orders can only be voided within the same shift."`).
Order number `POS-{storeCode}-{yyyyMMdd}-{seq:0000}` (store-timezone date of server receipt); return orders `RET-{storeCode}-{yyyyMMdd}-{seq:0000}`.

## 6. POS catalog / customer settings (POS-B)
| Route | Permission | Body → Result |
|---|---|---|
| `GET api/pos/products/{productUuid}/settings` | POS_PRODUCT_SETTINGS | `ProductPosSettings` (defaults when none) |
| `PUT api/pos/products/{productUuid}/settings` | POS_PRODUCT_SETTINGS | `ProductPosSettings` (upsert) |
| `POST api/pos/products/settings/bulk` | POS_PRODUCT_SETTINGS | `{ items: (ProductPosSettings & { productUuid })[] }` → `{ updated }` |
| `GET api/pos/customers/{customerUuid}/settings` | POS_CUSTOMER_SETTINGS | `CustomerPosSettings` |
| `PUT api/pos/customers/{customerUuid}/settings` | POS_CUSTOMER_SETTINGS | `CustomerPosSettings` (walk-in: allowPosCredit must be false) |
```ts
ProductPosSettings { posName?; posCategoryId? /*inventory product category id; null = product's category*/; color? /*#RRGGBB*/; imageUrl?;
  isWeighable; isFavorite; sortOrder; isPosActive /*default true*/; modifiedAt? }
CustomerPosSettings { allowPosCredit; offlineCreditLimitOverride?; maxSingleCreditOrder?; requiresIdForCredit; notes?; modifiedAt? }
```
Barcode and selling price stay on the variant (existing variant editor).

## 7. PWA sync (POS-B) — all need POS_ORDER_CREATE and a terminal of the caller's org
| Route | Result |
|---|---|
| `GET api/pos/sync/config?terminalUuid` | `{ serverTime; terminal: Terminal; store: Store; taxCode: { uuid?; code?; percent }; paymentMethods: string[]; features: { returns; creditSales; barcode; multiStore }; currency: { code; decimals: 2 } }` |
| `GET api/pos/sync/catalog?terminalUuid&since&limit=500` | `{ serverTime; nextSince; hasMore; items: CatalogItem[]; categories: { id; name; sortOrder }[] }` (items changed — variant, product or POS settings — strictly after `since`) |
| `GET api/pos/sync/stock?terminalUuid` | `{ serverTime; items: { variantUuid; available }[] }` (store warehouse snapshot, may be negative) |
| `GET api/pos/sync/customers?terminalUuid&since&limit=500` | `{ serverTime; nextSince; hasMore; customers: SyncCustomer[] }` |
| `GET api/pos/sync/users?terminalUuid` | `{ userId; name; posRole; pinHash; permissions: string[] }[]` — **only for a bound terminal; device must send header `X-Pos-Device: {deviceUuid}`** |
```ts
CatalogItem { variantUuid; productUuid; sku; name; posName?; variantName?; barcode?; categoryId?; categoryName?; uomCode?; price; imageUrl?; color?;
  isWeighable; isFavorite; sortOrder; isActive /*usable on POS now*/; modifiedAt }
SyncCustomer { uuid; code; name; customerType; phone?; mobile?; email?; creditLimit; balance; isActive; isSystem;
  allowPosCredit; offlineCreditLimitOverride?; maxSingleCreditOrder?; requiresIdForCredit; modifiedAt }
```
`pinHash` is the ASP.NET Identity v3 hash (base64: 0x01 | prf(UInt32 BE: 1=SHA256, 2=SHA512) | iterCount | saltLen | salt | subkey) — the PWA verifies with WebCrypto PBKDF2.

## 8. Real-time `/hubs/pos` (POS-B)
Client → server: `JoinTerminal(terminalUuid)` (validates org + module; joins `org_{org}_store_{storeUuid}` and `org_{org}_terminal_{terminalUuid}`), `Heartbeat(terminalUuid)` (updates `lastSeenAt`).
Server → client (store group unless noted): `PriceChanged { variantUuid, oldPrice, newPrice }`, `StockUpdated { variantUuid, available }`, `ProductUpdated { variantUuid }`, `ProductDeactivated { variantUuid }`, `CustomerBalanceChanged { customerUuid, balance, creditLimit }`, `ConfigChanged { configType }`, terminal group: `ForceSync { syncType: 'FULL'|'DELTA' }`, `ShiftAlert { message, severity }`, `ForceLogout { reason }`.
Admin triggers: `POST api/pos/terminals/{uuid}/force-sync { syncType }`, `POST api/pos/terminals/{uuid}/force-logout { reason }` (POS_TERMINAL_MANAGE).

## 9. Permissions & roles (XMOD)
`POS_STORE_VIEW POS_STORE_MANAGE POS_TERMINAL_VIEW POS_TERMINAL_MANAGE POS_SHIFT_OPEN POS_SHIFT_CLOSE POS_SHIFT_RECONCILE POS_SHIFT_VIEW POS_ORDER_CREATE POS_ORDER_VIEW POS_ORDER_VOID POS_ORDER_RETURN POS_ORDER_DISCOUNT POS_CASH_MOVEMENT POS_PRODUCT_SETTINGS POS_CUSTOMER_SETTINGS POS_REPORTS_VIEW`.
Built-in roles: **POS_CASHIER** (ORDER_CREATE), **POS_SUPERVISOR** (+ ORDER_VIEW, DISCOUNT, VOID, RETURN, SHIFT_OPEN/CLOSE/VIEW, CASH_MOVEMENT, TERMINAL_VIEW, STORE_VIEW), **STORE_MANAGER** (+ SHIFT_RECONCILE, TERMINAL_MANAGE, PRODUCT/CUSTOMER_SETTINGS, REPORTS_VIEW, CUSTOMER_VIEW). Org Admin / System Admin: all.

## 10. Module (XMOD)
`MODULE_POS` available; depends on MODULE_INVENTORY, MODULE_CUSTOMERS, MODULE_FINANCE; not in BASIC; default in STANDARD/ENTERPRISE for new orgs; existing orgs unlicensed until the super admin licenses it. Sub-features: core `FEATURE_POS_ORDERS FEATURE_POS_SHIFTS FEATURE_POS_CASHIER_PIN FEATURE_POS_PAYMENTS FEATURE_POS_OFFLINE`; toggleable `FEATURE_POS_RETURNS FEATURE_POS_CREDIT_SALES FEATURE_POS_BARCODE FEATURE_POS_MULTI_STORE` (multi-store off ⇒ one active store max); coming soon `FEATURE_POS_ANALYTICS FEATURE_POS_PROMOTIONS FEATURE_POS_LOYALTY`.

## 11. Frontend routes
PWA (full screen, outside the portal layout): `/pos` (terminal + sign-in + PIN), `/pos/sell`, `/pos/payment`, `/pos/receipt/:localId`, `/pos/shift`, `/pos/shift/close`, `/pos/history`, `/pos/settings`.
Back office (portal, menu group "Point of Sale"): `/portal/pages/pos/stores`, `/portal/pages/pos/stores/:uuid` (tabs General, Configuration, Terminals, Staff, Shifts, Reports), `/portal/pages/pos/shifts`, `/portal/pages/pos/shifts/:uuid`, `/portal/pages/pos/orders`, `/portal/pages/pos/orders/:uuid`; product detail gets a "POS" tab; customer detail gets a "POS" section; menu item "Open POS" → `/pos` in a new tab.

## Details fixed by the backend (POSA)
Decisions D-28..D-34 in the analysis doc. What the frontends see:
- **StoreUpsert**: `isActive?` (create: default true; PUT: null = unchanged, a change runs the PATCH rules), `rowVersion?` (PUT; null skips the check). Optional numbers default to `offlineCreditTolerancePct` 0 (0–100), `autoCloseShiftHours` 12 (1–168), `varianceThreshold` 0. `defaultTaxCodeUuid` omitted ⇒ Finance's default sales code. `timezone` IANA or Windows id, default `UTC`, unknown ⇒ 400. Code is upper-cased (≤ 20, letters/digits/`-`/`_`). Other 400s: `"Store code {code} already exists."`, `"Warehouse not found."`, `"The warehouse cannot be changed once the store has shifts."`, `"Multi-store is not enabled for your organization: only one active store is allowed."`. `currencyCode` may be null when the org has no sale base.
- **Terminal**: `rowVersion` changes only on back-office edits (PUT, bind, unbind, store deactivation) — never on heartbeats, sync or PIN failures (D-28). Code upper-cased. `PUT` with `isActive:false` while a shift is OPEN/CLOSING ⇒ 400 `"Close the terminal's shift before deactivating it."`. Bind ⇒ 400 `"The terminal is bound to another device. Unbind first."` / `"This device is already bound to terminal {code}. Unbind it first."`; with POS_SHIFT_OPEN only, the caller must be active staff of the store (403). Re-binding the same device is idempotent. `by-device` 404 = `"This device is not bound to a terminal."`.
- **Staff**: candidates ≤ 50 (active users not active staff). `DELETE` is soft (`isActive:false`) and ends the user's active sessions in the store; `PUT` re-activates. The PIN clash message also applies to a clash in another store the user works in (one PIN per user, D-29). `posRole` must be CASHIER/SUPERVISOR.
- **PIN sign-in** 400 texts: `"PIN must be 4 to 6 digits."`, `"Invalid PIN."` (counts), `"Terminal locked until HH:mm."` (store-local time; also while locked even with the right PIN), `"You are not staff of this store."`, `"You do not have permission to sell on POS."`, `"The user is inactive."`, `"The terminal is inactive."`. Only wrong PINs count toward the 5. `user.posRole` = the store_staff role.
- **Sessions**: start needs an OPEN shift (`"The shift is not open."`) and active store staff. The previous cashier's session on the terminal ends (internal `endReason` SWITCHED); end by another user needs POS_SHIFT_CLOSE (403). Session totals: `cashSales/cardSales/mobileSales/creditSales` = net tenders of non-voided sales, `refundTotal` = refund payments, `orderCount` = non-voided sales.
- **Shift DTO** gains `closingStartedAt?` and `reconcileNotes?` (append-only log, lines `[yyyy-MM-dd HH:mm UTC] name: text`). `close` works from OPEN or CLOSING, `actualCashClosing` required (≥ 0); within threshold ⇒ RECONCILED with `reconciledByName` = the closer. `reconcile` note goes into `reconcileNotes`; `note` allowed on RECONCILED and DISPUTED. Closing ends the shift's sessions. `current` 404 = `"The terminal has no active shift."`. List `status` accepts a comma list; `to` without a time is inclusive of that day.
- **Totals** (`expectedCashClosing` per §4): sales exclude VOIDED orders; `totalOtherSales` = BANK_TRANSFER + CHEQUE; `totalCreditSales` = ON_ACCOUNT; refunds are payments with `isRefund` (positive amounts) — CASH ⇒ `totalCashRefunds`, any other method (incl. ON_ACCOUNT) ⇒ `totalOtherRefunds`; `orderCount`/`grossTotal` over non-voided SALE orders, `refundCount` = RETURN orders, `netTotal = grossTotal − Σ return grandTotal`. A late order on a closed shift recomputes totals and `cashVariance`, status unchanged. Summary `byMethod` always lists CASH CARD MOBILE BANK_TRANSFER CHEQUE ON_ACCOUNT.
- **Cash movements**: direction/reason case-insensitive, amount > 0 with ≤ 2 decimals, active store staff (or POS_STORE_MANAGE); a `localId` already used on another shift ⇒ 409.
- **Hub**: connections without MODULE_POS or any POS_* permission are aborted. Every connection is also in `org_{org}_pos`: `PriceChanged`, `ProductUpdated`, `ProductDeactivated`, `CustomerBalanceChanged` go there (org-wide); `StockUpdated`, `ConfigChanged { configType: STORE|TERMINAL|STAFF }` to the store group. Unbind ⇒ `ForceLogout { reason: "UNBOUND" }` (wipe cached PINs). `force-logout` also ends the terminal's cashier session; `force-sync` `syncType` defaults to FULL. `JoinTerminal` on an unknown/other-org terminal throws `HubException("Terminal not found.")`.
- **Stale-shift alert**: notification type `POS_STALE_SHIFT`, entity `PosShift`, link `/portal/pages/pos/shifts/{uuid}`.

## Details fixed by the frontend (FEBO)
Back office in `SupplyChainFrontend/src/app/pages/pos/*` + `services/pos-admin.service.ts` (never touches the PWA files).
- **Store create** sends the server's defaults explicitly (tolerance 0, auto-close 12 h, variance 0), the browser's IANA timezone, `isActive:true`; tax code empty ⇒ omitted ⇒ Finance default. Warehouse picker = `GET api/warehouses` active ones (uuid); tax picker = `GET api/finance/tax-codes?side=SALES` filtered to usage SALES/BOTH. Every 400 is shown verbatim; 409 ⇒ "Someone else changed this store. Reload and try again."
- **Store edit / Configuration** both `PUT` the full upsert built from the loaded store (`rowVersion` + current `isActive`, so status is never changed by a PUT; activate/deactivate uses `PATCH …/status` behind a confirm). Auto-close input 1–168.
- **Terminals**: "online" = server `isOnline` **or** `lastSeenAt` < 15 min ago (client clock); "Locked" when `pinLockedUntil` is in the future, Unlock offered only then and only with POS_SHIFT_OPEN. Unbind behind a confirm; Force sync asks DELTA/FULL (default DELTA in the UI); Force logout requires a reason. Terminal `PUT` sends the list row's `rowVersion`. No live refresh (no hub in the back office) — the list reloads after each action.
- **Staff**: the PIN is optional on add/edit (empty ⇒ `pin` omitted ⇒ unchanged); when typed it must be 4–6 digits and typed twice (client check, input masked/numeric/max 6). Remove = `DELETE` (soft); inactive members stay listed, Remove hidden, the edit action becomes "Reactivate" (`PUT`). PIN clash texts (incl. another store) shown verbatim.
- **Store Reports tab** = per-day totals (local date of `openedAt`) of `GET api/pos/shifts?storeUuid&from&to&pageSize=500`; >500 shifts ⇒ a "pick a shorter range" note (no paging). Default range: last 7 days.
- **Shift detail**: tiles from the Shift, by-method from `/summary` (`byCashier` from summary, falling back to `sessions`), movements from the detail. "Reconcile" on DISPUTED and "Add note" on DISPUTED/RECONCILED, both POS_SHIFT_RECONCILE, note required. Shows `varianceNote` as "Closing note" and `reconcileNotes` as the note log. Variance highlight = |cashVariance| > varianceThreshold (lists and detail).
- **Orders list** query params `?shift=`, `?store=`, `?customer=` pre-filter (used by the shift page's "Orders" link). Terminal filter needs a store and POS_TERMINAL_VIEW/MANAGE; customer filter = first 200 active customers, only with CUSTOMER_VIEW. Posting pill: Posted / Stock only / Finance only / Not posted.
- **Order detail**: Void shown for SALE + COMPLETED with POS_ORDER_VOID (same-shift rule left to the server). Return shown for SALE + COMPLETED/PARTIALLY_RETURNED with a returnable line, POS_ORDER_RETURN and FEATURE_POS_RETURNS. The return dialog applies POSB's rule: each line refunds round(lineTotal × qty / quantity, 2, away from zero), except returning a line's last remaining units refunds exactly lineTotal − already refunded (summed from the earlier return orders' lines by `originalLineUuid`, fetched when the dialog opens); Σ refunds must equal the total exactly. `shiftUuid` = a picked OPEN shift of the store (default: the original terminal's), sent only when the user has POS_SHIFT_VIEW and an open shift exists. ON_ACCOUNT refund offered only for CREDIT_SALE/PARTIAL_CREDIT; default refund method = the sale's first non-refund tender. `localId` = `crypto.randomUUID()` per submit. Sales invoice and credit note (SCN-…, a sales invoice with status CREDIT_NOTE) both link to `/portal/pages/finance/sales-invoices/{uuid}` (needs SALES_INVOICE_VIEW/MANAGE; number only otherwise).
- **Sales invoice pages (after FIN)**: `saleOrderUuid`/`saleOrderNumber`/line `soLineUuid` typed nullable; new optional `sourceType`, `sourceNumber`, `originalInvoiceUuid`, `originalInvoiceNumber` (used if the server sends it, else "the original invoice"), `returnedAmount`, `creditedAmount`, line `discountAmount`/`taxAmount`. List: the "Order" column shows the SO number, or a POS pill + `sourceNumber` linking to `/portal/pages/pos/orders?search={number}` (the POS orders list reads `?search=`), or "—"; credit notes get a "Credit note" pill and a "credits {original}" link. Detail: no sale-order link without `saleOrderUuid`; a POS invoice resolves the order uuid with `GET api/pos/orders?search={sourceNumber}` (exact number match, only with POS_ORDER_VIEW) and links to it, otherwise to the searched list; credit notes read "Credit note · credits {original}" with stages Issued → Credited; a "Returned" KPI when `returnedAmount > 0`; line discount/tax amounts shown under the % when present; **Cancel is hidden for `sourceType` POS** (the server refuses it).
- **Product POS tab**: visible with MODULE_POS (TenantService feature codes, like the other module tabs), `isSaleable` (missing = true) and POS_PRODUCT_SETTINGS; loads when opened. Colour = native picker + `#RRGGBB` text, saved upper-case; category list = active inventory product categories (`id`). Barcode / selling price / variant POS availability are read-only from the default variant with a pointer to the Variants tab.
- **Customer POS section**: with MODULE_POS + POS_CUSTOMER_SETTINGS. Walk-in (`isSystem` or WALK_IN): allow-credit disabled and always sent false. With allow-credit off the credit-only fields are disabled and saved as null/false. A note is shown when FEATURE_POS_CREDIT_SALES is off.
- **Menu** group "Point of Sale" (featureCode MODULE_POS) after Services: Stores (POS_STORE_VIEW), Shifts (POS_SHIFT_VIEW), POS Orders (POS_ORDER_VIEW), Open POS (POS_ORDER_CREATE, `url:/pos`, `target:_blank`). Routes guarded with the same codes.

## Details fixed by the frontend (FEPOS)
PWA in `SupplyChainFrontend/src/app/pos/*` (+ `ngsw-config.json`, `public/manifest.webmanifest`, `public/pos-icons/`).
- **Tokens.** The "device token" (§3) is the token pair of the supervisor who signs the device in on `/pos` with `POST api/auth/login` (localStorage `sms-pos.device.tokens`, refreshed with `api/auth/refresh`, one refresh in flight). The cashier pair from `POST api/pos/auth/pin` lives in sessionStorage `sms-pos.cashier.tokens`. POS calls go through `HttpBackend` (the portal interceptors never run) and never read or write the portal's `accessToken`/`refreshToken`. Device token: `tenant/modules/enabled`, `terminals/by-device|available|{uuid}/bind`, `sync/*`, `auth/pin`, the hub. Cashier token: shifts (open/current/detail/start-close/resume/close), orders list/detail/void/return. Cashier-else-device: `orders/sync[/batch]`, `sessions`, `cash-movements`, `customers/{uuid}/credit` — a cashier signed in **offline** has no token, so the queue is pushed with the device token. ⚠ The device token carries the supervisor's permissions for as long as its refresh token lives (7 days rolling).
- **`SyncOrder.cashierUserId`** = the PIN-signed-in cashier at sale time (honoured by POSB). Every order also sends **`sessionLocalId`** (the cashier session's localId on this device, or null); `sessionUuid` is filled at push time once that session (`POST api/pos/sessions` with `localId`, queued offline) has synced.
- **Calculator** mirrors POSB exactly: worked examples E1–E8 are asserted in `pos-calculator.spec.ts`. The order-discount remainder goes on the last line with a non-zero net, and quantities are computed to 4 decimals. The payment screen and `completeSale` use the server's tender wording (`tenderError`: change only on cash, non-cash ≤ open balance in the order sent, Σ = total ± 0.01).
- **Online sale** = the order is written to Dexie, then `POST orders/sync` is awaited (15 s). `success:false` or 400/403/409 ⇒ the sale is dropped and the message shown on the payment screen (cart kept). No answer / 5xx / 401 ⇒ kept, `isOffline` set to true before the retry. `offlineCredit` = true only when the ON_ACCOUNT check ran on cached data.
- **Queue.** More than one due order ⇒ `sync/batch` in `createdAtLocal` order, chunks of 50; one ⇒ `sync`. No answer / 5xx / 401 / 403 ⇒ pending with backoff 5 s·2ⁿ⁻¹ (≤ 5 min, retry tick 15 s); `success:false` / other 4xx ⇒ `error`, listed under `/pos/settings` with a manual Retry. Reconnect (browser `online`, first successful request after failures, hub reconnect) ⇒ push all, then delta pull.
- **Pulls.** Cursor = `nextSince ?? serverTime`; loops while `hasMore`. Full (first activation, ForceSync FULL, "Full sync") replaces catalog/customers atomically; categories replaced from the last page. Order: config → catalog → customers → users → stock. Stock snapshot every 5 min online; `StockUpdated` between. A `sync/users` refusal keeps the previous cache. Stale banner when the last successful pull is > 24 h old (or never).
- **Wipe.** PIN hashes, catalog, customers, stock, config and the already-synced queues are wiped on Unbind (refused while anything is queued/failed), on `ForceLogout { reason: "UNBOUND" }` and when `by-device` answers 404; the device sign-in is kept except on a local Unbind.
- **PIN.** `userId` is never sent (PIN identifies the staff member). Offline: every cached hash is tried (prf 0/1/2 supported). Local lock: 5 wrong PINs (server `"Invalid PIN."` or offline mismatch) ⇒ 5 min on this device, independent of the server lock (whose message is shown as is). Lock keeps the cashier; the same PIN resumes with the cart, another PIN switches (cart cleared, POS-CSH-05). A reload restores the cashier locked.
- **ON_ACCOUNT.** Needs `features.creditSales`, a picked customer that is not walk-in (`customerUuid` null, `isSystem`, or code `C-WALKIN`/`WALK_IN`) with `allowPosCredit`. Online: `available`, `maxSingleCreditOrder`, server wording. Offline: `balance + amount ≤ (offlineCreditLimitOverride ?? creditLimit) × (1 + store.offlineCreditTolerancePct/100)`; a limit ≤ 0 ⇒ no credit offline; above the limit but within the tolerance ⇒ allowed with a warning (server flags CREDIT_OVER_LIMIT). `requiresIdForCredit` ⇒ the ID is entered as the ON_ACCOUNT payment `reference`.
- **Numbers.** Offline number `POS-{storeCode}-OFF-{first 8 hex of localId, upper-case}`; the receipt switches to `orderNumber` once synced.
- **Returns** (history screen) follow the POSB rule: `round(lineTotal × qty / originalQty)`, except that taking a line's last remaining units (`qty ≥ returnableQuantity`) refunds exactly `lineTotal − Σ already returned`. The already-returned amounts are read from the order's `returns[]` (`GET orders/{uuid}` of each RETURN, Σ |lineTotal| per `originalLineUuid`); E9 is asserted in `pos-return.spec.ts`. Refunds must equal the total exactly. `shiftUuid` = this terminal's current shift. `localId` stays stable while the dialog is open, so a retry after a timeout is idempotent. Void is offered for SALE + COMPLETED of the current shift.
- **Shift close** in the PWA always goes OPEN → start-close → close (stops sales first); Close is disabled while anything is queued or failed; a note is required when |variance| > threshold (UI rule; the server only marks DISPUTED).
- **Hub.** `ForceSync` without `syncType` ⇒ FULL. `ConfigChanged` (any type) ⇒ pull config + users. `ProductUpdated` ⇒ catalog delta. Heartbeat every 60 s while connected; a failed first start retries after 30 s.
- **Module.** `GET api/tenant/modules/enabled` with the device token decides MODULE_POS (403 ⇒ off); cached for offline; unknown ⇒ allowed.
- **PWA.** Service worker registered in production builds only; navigation is network-first (`freshness`) so the portal stays current; JS chunks cached lazily — the POS shell preloads every POS screen once online. The manifest ("SMS POS", start `/pos`) is linked only while the POS shell is open. API data is never cached by the service worker.

## Details fixed by the backend (POSB)
Decisions D-41..D-50 in the analysis doc. What the frontends see:
- **Calculator (§1)**: exactly as written, plus: the order-discount remainder goes on the **last line whose net is not zero**; AMOUNT discounts are capped (`min`); PERCENT ≤ 100; AMOUNT values ≤ 2 decimals; quantities ≤ 4 decimals; unit prices ≥ 0, ≤ 2 decimals. Tender check runs **in the order sent**: CARD/MOBILE/BANK_TRANSFER/CHEQUE/ON_ACCOUNT may not exceed what is still open before them (+0.005). Worked examples: appendix below.
- **`SyncOrder.cashierUserId`: honoured.** Accepted when that user is active staff of the terminal's store, active, with POS_ORDER_CREATE ⇒ the order's cashier (also for the discount-permission check); otherwise the pushing user + warning `"User {id} cannot sell in this store; the order is booked to the user who synced it."`. The pushing user is kept as `syncedByUserId` / `syncedByName` on PosOrder (column `pos.orders.SyncedByUserId`, migration `A38_PosOrderSyncedBy`). Without `cashierUserId`: the known session's user, else the pushing user. The pushing user must be active store staff (or POS_STORE_MANAGE) — 403 `"You are not staff of this store."`.
- **New `SyncOrder.sessionLocalId?`** (the offline session's id). Session of a synced order: a known `sessionUuid` of this shift wins; **online** with an unknown one ⇒ 400 `"The cashier session does not belong to this shift."`. **Offline**: the session with `sessionLocalId` on this shift, else the cashier's latest session on the shift, else a new **closed** session for the cashier (started = ended = `createdAtLocal`; the terminal's live session is never displaced) — idempotent on `sessionLocalId`.
- `createdAtLocal` = ISO-8601 with offset (send UTC `Z`); missing ⇒ server time.
- **Online 400 vs offline accept** (offline = `isOffline:true`): shift CLOSING with `createdAtLocal` after start-close ⇒ 400 `"The shift is closing: no new sales can be made on it."` (offline: accepted); CLOSED/RECONCILED/DISPUTED ⇒ 400 `"The shift is not open."` (offline: booked to it, warning, a `System: late offline order …` line appended to the shift's `reconcileNotes`, totals + variance recomputed, status unchanged). Unusable variant ⇒ `"{name} is not available for sale on POS."` (offline: warning). Inactive customer ⇒ `"The customer is inactive."` (offline: warning). Discount without POS_ORDER_DISCOUNT ⇒ `"You do not have permission to give discounts."` (offline: `DISCOUNT_UNAUTHORIZED`). Tender mismatch / POS-PAY-03 ⇒ 400 (offline: warning; Finance gets the tenders normalised — excess taken off the last tenders, shortfall booked ON_ACCOUNT on the order's customer). **Always 400**: unknown terminal/shift (404), a shift of another terminal, unknown customer (`"Customer not found."`), unknown variant (`"Product variant {uuid} was not found."`), malformed lines/payments (unknown method, amount ≤ 0, change on non-cash, change > amount), no lines / no payments, > 500 lines, a payment `localId` already used by another order. Batch ≤ 100 orders; results in **request order**, processed in `createdAtLocal` order; an unexpected error answers `"The order could not be saved. Try again."`. Replaying a localId answers the stored order (`warnings: []`); the localId of a RETURN ⇒ 409.
- **PRICE_CHANGED**: a line's `unitPrice` ≠ the variant's current catalog `price`; the cashier's price is kept.
- **Credit (ON_ACCOUNT)** checks in order: FEATURE_POS_CREDIT_SALES (`"Credit sales are not enabled."`), walk-in (`"Walk-In customers cannot use credit."`; walk-in = `isSystem` or code `C-WALKIN`/`WALK_IN`), `allowPosCredit` (`"This customer is not allowed to buy on credit at the POS."`), `maxSingleCreditOrder` (`"Credit for one order cannot exceed X."`), limit `balance + onAccount > creditLimit` ⇒ `"Credit limit exceeded. Limit: X. Balance: Y. This order: Z."` (credit limit 0 = no credit). Offline: limit = `round((offlineCreditLimitOverride ?? creditLimit) × (1 + offlineCreditTolerancePct/100))`, balance = the server's at sync time (earlier orders of the batch included); any breach ⇒ accepted + `CREDIT_OVER_LIMIT` + warning + notification `POS_CREDIT_OVER_LIMIT` (entity `PosOrder`, link `/portal/pages/pos/orders/{uuid}`) to the store's SUPERVISOR staff. Every offline order with ON_ACCOUNT gets `OFFLINE_CREDIT`. `paymentStatus`: no ON_ACCOUNT ⇒ PAID, ON_ACCOUNT ≥ total ⇒ CREDIT_SALE, else PARTIAL_CREDIT. `paidAmount` = Σ tendered non-ON_ACCOUNT amounts (before change), `changeAmount` = Σ change, `balanceDue` = the ON_ACCOUNT amount. After an ON_ACCOUNT sale / void / return the hub pushes `CustomerBalanceChanged` (org group).
- **Postings**: saved first; `stockPosted` / `financePosted` / `postingError` (`"Stock: …" | "Finance: …"`); `PosPostingRetryJob` retries with backoff 15 min × 2^(attempts−1) (≤ 1 day). `NEGATIVE_STOCK` from the stock posting; the first time ⇒ notification `POS_NEGATIVE_STOCK` to the store's supervisors. A sale for 0.00 is not invoiced. `customerPaymentUuid` filled per tender.
- **Void**: reason required (≤ 500). 400s: `"Only a sale can be voided."`, `"The order is already voided."`, `"A returned order cannot be voided."`, `"Orders can only be voided within the same shift."` (the order's shift must still be OPEN/CLOSING). A sale already in Finance is voided in Finance **first**: Finance's refusals (a return, a back-office payment) come back as 400 with Finance's text and the order stays COMPLETED. Stock: POS_VOID.
- **Return**: `ReturnRequest.shiftUuid?` = the OPEN shift (same store) paying the refund; omitted ⇒ the original terminal's OPEN shift, none ⇒ 400 `"Open a shift on the terminal to refund this return."`. Reason required. Lines grouped by `originalLineUuid`; `"Return quantity cannot exceed the original quantity."`.
  **Return amount per line** = `round(lineTotal × qty / originalQty)` (2dp, away from zero; tax the same from the line's `taxAmount`; `netAmount = total − tax`) — **except when the return takes the line's last remaining units: then exactly what is left** (`lineTotal − Σ lineTotal already returned on that line`). Finance refuses crediting a line beyond what it was sold for, so the frontends' estimate must use the same remainder rule (their current "whole line = lineTotal" is the same thing when nothing was returned before). Example — line 3 × 3.33, 10 % (E2: lineTotal 10.52, tax 1.53): return 1 ⇒ **3.51** (tax 0.51, net 3.00); later 1 ⇒ **3.51**; the last 1 ⇒ **3.50** (tax 0.51, net 2.99); Σ 10.52. All 3 at once ⇒ 10.52.
  Return total = Σ line amounts; `refunds` must equal it **exactly** (`"Refunds (X) must equal the return total (Y)."`). ON_ACCOUNT refunds only when the sale had ON_ACCOUNT (`"On-account refunds are only possible for credit sales."`) and up to the on-account amount not yet refunded on account (`"On-account refunds cannot exceed the credit still open on this sale (X)."`). The RETURN order: `RET-…`, `status` COMPLETED, `paymentStatus` REFUNDED, cashier = caller, refunds as payments with `isRefund:true`; the original becomes PARTIALLY_RETURNED / RETURNED. Replaying the return's `localId` answers the same RETURN; the localId of another order ⇒ 409.
- **Order reads**: `GET {uuid}` / `by-local` = POS_ORDER_VIEW, or POS_ORDER_CREATE as the order's cashier or active staff of its store (else 403). List: `status`/`orderType` accept comma lists; `search` = number, customer name/code, offline number, exact localId; `to` without a time is inclusive; newest first.
- **Settings (§6)**: unknown product/customer ⇒ 404. `color` `#RRGGBB` (stored upper-case); `posName` ≤ 100; `imageUrl` ≤ 500; `posCategoryId` must be an active category (`"Unknown POS category."`). Every PUT / bulk bumps `modifiedAt`, even without a change, and pushes `ProductUpdated`/`ProductDeactivated` for the product's variants. Bulk ≤ 500 items, each with `productUuid`, no duplicates, all-or-nothing. Customer: walk-in with `allowPosCredit:true` ⇒ 400 `"Walk-In customers cannot use credit."`; amounts ≥ 0; notes ≤ 1000.
- **Sync (§7)**: `since` ISO-8601 UTC; `limit` default 500, max 1000. Catalog and customers merge the master delta with the POS settings changes: when either source fills its page, the page ends at the earlier of their last timestamps (= `nextSince`, `hasMore:true`) and holds everything of both up to and including it (ties included); otherwise `hasMore:false`, `nextSince` = latest `modifiedAt` sent (or `since` when nothing changed). An item's `modifiedAt` = later of the master row and its settings. Catalog `price` = the variant's `SellingPrice` (price lists not applied, D-48). `isActive` = variant + product active, saleable, available for POS, settings `isPosActive`. `categories[].sortOrder` = name order. Catalog pulls stamp `lastSyncAt`. `users`: 403 `"This device is not bound to the terminal."` unless bound and `X-Pos-Device` = its device; only active staff with a PIN holding POS_ORDER_CREATE. `config.paymentMethods` omits ON_ACCOUNT while credit sales are off.
- **Hub (§8)**: `StockUpdated` to every **active** store on the changed warehouse, with the current available (no row = 0). A variant gone or not usable ⇒ `ProductDeactivated`, else `ProductUpdated`.
- **Negative stock report**: daily 06:00 UTC, notification `POS_NEGATIVE_STOCK_REPORT` (entity `PosStore`, link `/portal/pages/pos/stores/{uuid}`) per store to its supervisors (none ⇒ nothing), up to 10 SKUs listed.

## Appendix — calculator worked examples
Server tests: `tests/SMS.Modules.Pos.Tests/CalculatorTests.cs` (the PWA asserts the same numbers). Rate 17 % throughout; `share` = allocated order discount; values per line, in line order.

| # | Store | Lines (qty × price, line discount) | Order disc. | gross | line disc. | share | netAmount | tax | lineTotal | subtotal | discountAmount | taxTotal | grandTotal |
|---|---|---|---|---|---|---|---|---|---|---|---|---|---|
| E1 | excl. | 2 × 10.00; 1 × 5.99 | — | 20.00; 5.99 | 0; 0 | 0; 0 | 20.00; 5.99 | 3.40; 1.02 | 23.40; 7.01 | 25.99 | 0.00 | 4.42 | **30.41** |
| E2 | excl. | 3 × 3.33, 10 % | — | 9.99 | 1.00 | 0 | 8.99 | 1.53 | 10.52 | 8.99 | 0.00 | 1.53 | **10.52** |
| E3 | excl. | 2 × 10.00; 1 × 5.99 | 10 % | 20.00; 5.99 | 0; 0 | 2.00; 0.60 | 18.00; 5.39 | 3.06; 0.92 | 21.06; 6.31 | 25.99 | 2.60 | 3.98 | **27.37** |
| E4 | excl. | 1 × 3.33; 1 × 3.33; 1 × 3.33 | AMOUNT 5.00 | 3.33 each | 0 | 1.67; 1.67; 1.66 | 1.66; 1.66; 1.67 | 0.28; 0.28; 0.28 | 1.94; 1.94; 1.95 | 9.99 | 5.00 | 0.84 | **5.83** |
| E5 | incl. | 1 × 11.70; 2 × 4.99 | — | 11.70; 9.98 | 0; 0 | 0; 0 | 10.00; 8.53 | 1.70; 1.45 | 11.70; 9.98 | 21.68 | 0.00 | 3.15 | **21.68** |
| E6 | incl. | 1 × 11.70 AMOUNT 1.00; 2 × 4.99 | 5 % | 11.70; 9.98 | 1.00; 0 | 0.53; 0.50 | 8.69; 8.10 | 1.48; 1.38 | 10.17; 9.48 | 20.68 | 1.03 | 2.86 | **19.65** |
| E7 | excl. | 1.5 × 2.35, 50 % (midpoints) | — | 3.53 | 1.77 | 0 | 1.76 | 0.30 | 2.06 | 1.76 | 0.00 | 0.30 | **2.06** |

Line DTO `discountAmount` = line disc. + share (E6: 1.53; 0.50). Inclusive: `tax = round(netAfter − netAfter / 1.17)`, `netAmount = netAfter − tax`, `lineTotal = netAfter`. Edge: AMOUNT 5.00 on a 2.00 line ⇒ 2.00; order AMOUNT 10.00 on subtotal 3.00 ⇒ 3.00, grand 0.00.

**E8 tenders** (grand 27.37 = E3): CASH 30.00 change 2.63 ✓ · CARD 30.00 ✗ "A CARD payment of 30.00 exceeds the remaining balance of 27.37." · CASH 20.00 + ON_ACCOUNT 7.37 ✓ (PARTIAL_CREDIT) · CASH 27.36 ✓ (±0.01) · CASH 27.35 ✗ "Payments (27.35) do not match the order total (27.37)." · CARD 30.00 change 2.63 ✗ "Only cash payments can give change."

**E9 return shares** (line of E2, 3 units, total 10.52, tax 1.53): 1 ⇒ 3.51 / tax 0.51 · 1 more ⇒ 3.51 / 0.51 · last 1 ⇒ 3.50 / 0.51.
