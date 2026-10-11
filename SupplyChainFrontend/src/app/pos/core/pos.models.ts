// A38 — Point of Sale PWA. Shapes of docs/pos/API-CONTRACT.md (§2–§8) plus the device-local records kept in Dexie.

export interface ApiResponse<T> {
    success: boolean;
    message: string;
    result: T;
}

export interface Paginated<T> {
    data: T[];
    totalRecords: number;
    page: number;
    pageSize: number;
    totalPages?: number;
}

export type DiscountType = 'NONE' | 'PERCENT' | 'AMOUNT';
/** A39: STORE_CREDIT (redeem / refund as store credit) and EXCHANGE (internal tender of an exchange, never chosen). */
export type PaymentMethod = 'CASH' | 'CARD' | 'MOBILE' | 'BANK_TRANSFER' | 'CHEQUE' | 'ON_ACCOUNT' | 'STORE_CREDIT' | 'EXCHANGE';
export type OrderType = 'SALE' | 'RETURN' | 'EXCHANGE';
export type PosRole = 'CASHIER' | 'SUPERVISOR';
export type ShiftStatus = 'DRAFT' | 'OPEN' | 'CLOSING' | 'CLOSED' | 'RECONCILED' | 'DISPUTED';
export type CashDirection = 'IN' | 'OUT';
export type CashReason = 'FLOAT_TOP_UP' | 'SAFE_DROP' | 'PETTY_CASH' | 'CHANGE_RUN' | 'OTHER';
export type SyncStatus = 'pending' | 'syncing' | 'synced' | 'error';

export const ALL_PAYMENT_METHODS: PaymentMethod[] = ['CASH', 'CARD', 'MOBILE', 'BANK_TRANSFER', 'CHEQUE', 'ON_ACCOUNT'];
export const CASH_REASONS: CashReason[] = ['FLOAT_TOP_UP', 'SAFE_DROP', 'PETTY_CASH', 'CHANGE_RUN', 'OTHER'];

/** §9 permission codes the PWA checks. */
export const POS_PERM = {
    ORDER_CREATE: 'POS_ORDER_CREATE',
    ORDER_VIEW: 'POS_ORDER_VIEW',
    ORDER_VOID: 'POS_ORDER_VOID',
    ORDER_RETURN: 'POS_ORDER_RETURN',
    ORDER_DISCOUNT: 'POS_ORDER_DISCOUNT',
    SHIFT_OPEN: 'POS_SHIFT_OPEN',
    SHIFT_CLOSE: 'POS_SHIFT_CLOSE',
    SHIFT_VIEW: 'POS_SHIFT_VIEW',
    CASH_MOVEMENT: 'POS_CASH_MOVEMENT',
    TERMINAL_MANAGE: 'POS_TERMINAL_MANAGE',
    // A39 D-9
    RETURN_CROSS_STORE: 'POS_RETURN_CROSS_STORE',
    RETURN_OVERRIDE: 'POS_RETURN_OVERRIDE',
    CREDIT_NOTE_ISSUE: 'POS_CREDIT_NOTE_ISSUE',
    CREDIT_NOTE_REDEEM: 'POS_CREDIT_NOTE_REDEEM',
    CREDIT_NOTE_VIEW: 'POS_CREDIT_NOTE_VIEW'
} as const;

export const MODULE_POS = 'MODULE_POS';
/** A39 feature codes (read from the cached `tenant/modules/enabled` answer). */
export const FEATURE_POS_CREDIT_NOTES = 'FEATURE_POS_CREDIT_NOTES';
export const FEATURE_POS_CROSS_STORE_RETURNS = 'FEATURE_POS_CROSS_STORE_RETURNS';

// ── §2 ──────────────────────────────────────────────────────────────────────────
export interface PosStore {
    uuid: string;
    code: string;
    name: string;
    warehouseUuid: string;
    warehouseName?: string;
    addressLine1?: string | null;
    addressLine2?: string | null;
    city?: string | null;
    stateProvince?: string | null;
    postalCode?: string | null;
    country?: string | null;
    phone?: string | null;
    email?: string | null;
    timezone: string;
    currencyCode: string;
    taxInclusivePricing: boolean;
    defaultTaxCodeUuid?: string | null;
    defaultTaxCode?: string | null;
    defaultTaxPercent: number;
    receiptHeader?: string | null;
    receiptFooter?: string | null;
    offlineCreditTolerancePct: number;
    autoCloseShiftHours: number;
    varianceThreshold: number;
    isActive: boolean;
}

export interface PosTerminal {
    uuid: string;
    storeUuid: string;
    storeName: string;
    code: string;
    name: string;
    deviceBound: boolean;
    deviceUuid?: string | null;
    lastSeenAt?: string | null;
    lastSyncAt?: string | null;
    isOnline: boolean;
    isActive: boolean;
    activeShiftUuid?: string | null;
    activeShiftNumber?: string | null;
    pinLockedUntil?: string | null;
}

export interface TerminalConfig {
    terminal: PosTerminal;
    store: PosStore;
    serverTime: string;
}

// ── §3 ──────────────────────────────────────────────────────────────────────────
export interface PinSignIn {
    accessToken: string;
    refreshToken: string;
    user: { userId: number; name: string; posRole: PosRole; permissions: string[] };
}

export interface CashierSession {
    uuid: string;
    shiftUuid: string;
    terminalUuid: string;
    userId: number;
    userName: string;
    startedAt: string;
    endedAt?: string | null;
    isActive: boolean;
    cashSales: number;
    cardSales: number;
    mobileSales: number;
    creditSales: number;
    refundTotal: number;
    orderCount: number;
}

// ── §4 ──────────────────────────────────────────────────────────────────────────
export interface Shift {
    uuid: string;
    shiftNumber: string;
    storeUuid: string;
    storeName: string;
    terminalUuid: string;
    terminalName: string;
    status: ShiftStatus;
    openedByName: string;
    openedAt: string;
    closedByName?: string | null;
    closedAt?: string | null;
    openingFloat: number;
    totalCashSales: number;
    totalCardSales: number;
    totalMobileSales: number;
    totalOtherSales: number;
    totalCreditSales: number;
    totalCashRefunds: number;
    totalOtherRefunds: number;
    totalCashIn: number;
    totalCashOut: number;
    expectedCashClosing: number;
    actualCashClosing?: number | null;
    cashVariance?: number | null;
    varianceThreshold: number;
    varianceNote?: string | null;
    /** POSA: when start-close ran. */
    closingStartedAt?: string | null;
    /** POSA: append-only reconcile log. */
    reconcileNotes?: string | null;
    orderCount: number;
    refundCount: number;
    grossTotal: number;
    netTotal: number;
    /** A39 D-10. */
    totalStoreCreditIssued?: number;
    totalStoreCreditRedeemed?: number;
    rowVersion?: string;
}

export interface CashMovement {
    uuid: string;
    direction: CashDirection;
    amount: number;
    reason: CashReason;
    note?: string | null;
    performedByName: string;
    performedAt: string;
    localId?: string | null;
}

export interface ShiftDetail extends Shift {
    sessions: CashierSession[];
    cashMovements: CashMovement[];
}

export interface ShiftSummary {
    shift: Shift;
    byMethod: { method: string; sales: number; refunds: number; net: number }[];
    byCashier: CashierSession[];
}

// ── §5 ──────────────────────────────────────────────────────────────────────────
export interface SyncOrderLine {
    localLineId: string;
    variantUuid: string;
    quantity: number;
    unitPrice: number;
    discountType: DiscountType;
    discountValue: number;
    barcode?: string | null;
    note?: string | null;
}

export interface SyncPayment {
    localId: string;
    method: PaymentMethod;
    amount: number;
    changeGiven: number;
    reference?: string | null;
    /** A39: the redeemed store credit (method STORE_CREDIT). */
    creditNoteUuid?: string | null;
}

export interface SyncOrder {
    localId: string;
    terminalUuid: string;
    shiftUuid: string;
    sessionUuid?: string | null;
    /** POSB: the cashier session's localId (the server resolves it, or creates a closed session, for offline orders). */
    sessionLocalId?: string | null;
    offlineNumber?: string | null;
    createdAtLocal: string;
    isOffline: boolean;
    customerUuid?: string | null;
    lines: SyncOrderLine[];
    orderDiscountType: DiscountType;
    orderDiscountValue: number;
    note?: string | null;
    payments: SyncPayment[];
    offlineCredit: boolean;
    clientTotals: { subtotal: number; discountAmount: number; taxTotal: number; grandTotal: number };
    /** The PIN-signed-in cashier (honoured by the server, POSB; the pusher is recorded separately as syncedBy). */
    cashierUserId?: number | null;
}

export interface SyncResult {
    localId: string;
    success: boolean;
    orderUuid?: string | null;
    orderNumber?: string | null;
    status?: string | null;
    flags?: string[] | null;
    warnings?: string[] | null;
    error?: string | null;
}

export interface PosOrderLine {
    uuid: string;
    lineNumber: number;
    variantUuid: string;
    productName: string;
    variantName?: string | null;
    sku: string;
    barcode?: string | null;
    quantity: number;
    unitPrice: number;
    discountType: DiscountType;
    discountValue: number;
    discountAmount: number;
    netAmount: number;
    taxPercent: number;
    taxAmount: number;
    lineTotal: number;
    originalLineUuid?: string | null;
    returnedQuantity: number;
    returnableQuantity: number;
    note?: string | null;
}

export interface PosPayment {
    uuid: string;
    method: PaymentMethod;
    amount: number;
    changeGiven: number;
    reference?: string | null;
    isRefund: boolean;
    createdAt: string;
    creditNoteUuid?: string | null;
}

export interface PosOrder {
    uuid: string;
    orderNumber: string;
    localId: string;
    offlineNumber?: string | null;
    orderType: OrderType;
    status: string;
    storeUuid: string;
    storeName: string;
    terminalUuid: string;
    terminalName: string;
    shiftUuid: string;
    shiftNumber: string;
    cashierName: string;
    customerUuid: string;
    customerCode: string;
    customerName: string;
    isWalkIn: boolean;
    lines: PosOrderLine[];
    payments: PosPayment[];
    subtotal: number;
    discountType: DiscountType;
    discountValue: number;
    discountAmount: number;
    taxTotal: number;
    grandTotal: number;
    paidAmount: number;
    changeAmount: number;
    balanceDue: number;
    paymentStatus: string;
    taxInclusive: boolean;
    taxPercent: number;
    originalOrderUuid?: string | null;
    originalOrderNumber?: string | null;
    returnReason?: string | null;
    voidReason?: string | null;
    note?: string | null;
    isOffline: boolean;
    flags: string[];
    createdAt: string;
    createdAtLocal: string;
    returns: { uuid: string; orderNumber: string; grandTotal: number }[];
    // A39 §3
    returnReasonUuid?: string | null;
    returnReasonName?: string | null;
    authorizedByName?: string | null;
    authorizedAt?: string | null;
    restockingFeeAmount?: number;
    isCrossStore?: boolean;
    originalStoreName?: string | null;
    storeCredit?: { uuid: string; number: string; amount: number; balance: number; expiresAt?: string | null } | null;
    netAmount?: number | null;
    settlement?: ExchangeSettlement | null;
}

export interface PosOrderListItem {
    uuid: string;
    orderNumber: string;
    orderType: OrderType;
    status: string;
    storeName: string;
    terminalName: string;
    cashierName: string;
    customerName: string;
    grandTotal: number;
    paymentStatus: string;
    flags: string[];
    createdAt: string;
    isOffline: boolean;
}

// ── A39 §1–§4 returns, exchanges, store credit ─────────────────────────────────
export type ReasonAppliesTo = 'BOTH' | 'RETURN' | 'EXCHANGE';
export type ManagerReason = 'REASON' | 'AMOUNT' | 'WINDOW' | 'CROSS_STORE';
export type ExchangeSettlement = 'PAY' | 'EVEN' | 'REFUND';
export type StoreCreditStatus = 'ACTIVE' | 'FULLY_REDEEMED' | 'EXPIRED' | 'CANCELLED';

export interface ReturnReason {
    uuid: string;
    code: string;
    name: string;
    description?: string | null;
    requiresNote: boolean;
    requiresManager: boolean;
    appliesTo: ReasonAppliesTo;
    restockingFeeOverride?: number | null;
    sortOrder: number;
    isActive: boolean;
    modifiedAt: string;
}

export interface ReturnPolicy {
    uuid: string;
    storeUuid?: string | null;
    storeName?: string | null;
    name: string;
    returnWindowDays?: number | null;
    exchangeWindowDays?: number | null;
    requireReceipt: boolean;
    allowNoReceiptExchange: boolean;
    allowCrossStoreReturn: boolean;
    crossStoreRequiresManager: boolean;
    restockingFeePct: number;
    maxReturnNoAuth?: number | null;
    refundToOriginalMethod: boolean;
    allowCashRefund: boolean;
    allowCreditNoteRefund: boolean;
    creditNoteExpiryDays?: number | null;
    isActive: boolean;
    rowVersion?: string;
    modifiedAt?: string;
}

export interface EffectivePolicy extends ReturnPolicy {
    source: 'STORE' | 'ORG_DEFAULT';
}

export interface LookupPolicy extends EffectivePolicy {
    withinReturnWindow: boolean;
    withinExchangeWindow: boolean;
    windowDaysRemaining?: number | null;
    crossStoreAllowed: boolean;
    requiresManagerForCrossStore: boolean;
}

export interface LookupLine {
    lineUuid: string;
    variantUuid: string;
    productName: string;
    sku: string;
    quantity: number;
    returnedQuantity: number;
    returnableQuantity: number;
    unitPrice: number;
    taxPercent: number;
    lineTotal: number;
    /** Remaining refundable amount of this line (remainder rule). */
    refundIfAllReturned: number;
}

export interface OrderLookup {
    found: boolean;
    order?: {
        uuid: string;
        orderNumber: string;
        storeUuid: string;
        storeName: string;
        storeCode: string;
        createdAt: string;
        grandTotal: number;
        customerUuid: string | null;
        customerName: string;
        isWalkIn: boolean;
        status: string;
        orderType: OrderType;
        isSameStore: boolean;
        daysSincePurchase: number;
        payments: { method: string; amount: number; reference?: string | null }[];
        lines: LookupLine[];
    } | null;
    policy?: LookupPolicy | null;
}

export interface AuthorizeResult {
    authorizationToken: string;
    managerUserId: number;
    managerName: string;
    expiresAt: string;
}

/** A39 §3 — `POST orders/{uuid}/return` (online). */
export interface ReturnRequest {
    localId: string;
    terminalUuid: string;
    shiftUuid: string;
    sessionUuid?: string | null;
    reasonUuid: string;
    note?: string | null;
    authorizationToken?: string | null;
    lines: { originalLineUuid: string; quantity: number }[];
    refundMethod: string;
    refundReference?: string | null;
    storeCreditCustomerUuid?: string | null;
}

/** A39 §3 — `POST orders/{uuid}/exchange` (online). */
export interface ExchangeRequest extends ReturnRequest {
    newLines: SyncOrderLine[];
    orderDiscountType: DiscountType;
    orderDiscountValue: number;
    payments: SyncPayment[];
}

/** A39 §3 — `POST returns/sync` (offline returns / exchanges, idempotent on localId). */
export type ReturnSyncRequest = (ReturnRequest | ExchangeRequest) & {
    kind: 'RETURN' | 'EXCHANGE';
    originalOrderUuid: string;
    createdAtLocal: string;
    isOffline: boolean;
    authorizedByUserId?: number | null;
    /** FEPOS extras (as on SyncOrder): the PIN-signed-in cashier and the cashier session's local id. */
    cashierUserId?: number | null;
    sessionLocalId?: string | null;
    /** POSR: `CN-{store}-OFF-…` printed on the offline receipt when the refund is store credit. */
    storeCreditOfflineNumber?: string | null;
};

export interface StoreCredit {
    uuid: string;
    number: string;
    customerUuid?: string | null;
    customerName?: string | null;
    returnOrderUuid?: string | null;
    returnOrderNumber?: string | null;
    originalAmount: number;
    remainingBalance: number;
    redeemedAmount: number;
    currencyCode: string;
    status: StoreCreditStatus;
    issuedAtStoreName: string;
    issuedByName: string;
    issuedAt: string;
    expiresAt?: string | null;
    lastUsedAt?: string | null;
    lastUsedAtStoreName?: string | null;
    note?: string | null;
    modifiedAt: string;
}

export interface StoreCreditTxn {
    uuid: string;
    type: 'ISSUE' | 'REDEEM' | 'EXPIRE' | 'CANCEL' | 'ADJUST';
    amount: number;
    balanceAfter: number;
    orderUuid?: string | null;
    orderNumber?: string | null;
    storeName: string;
    performedByName: string;
    performedAt: string;
    note?: string | null;
}

export interface StoreCreditDetail extends StoreCredit {
    transactions: StoreCreditTxn[];
}

export interface ReasonsPage {
    serverTime: string;
    nextSince: string | null;
    reasons: ReturnReason[];
}

export interface CustomerCredit {
    balance: number;
    creditLimit: number;
    available: number;
    allowPosCredit: boolean;
    maxSingleCreditOrder?: number | null;
    requiresIdForCredit: boolean;
    isWalkIn: boolean;
}

// ── §7 ──────────────────────────────────────────────────────────────────────────
export interface SyncConfig {
    serverTime: string;
    terminal: PosTerminal;
    store: PosStore;
    taxCode: { uuid?: string | null; code?: string | null; percent: number };
    paymentMethods: string[];
    /** A39 (POSR): `creditNotes`, `crossStoreReturns` added. */
    features: { returns: boolean; creditSales: boolean; barcode: boolean; multiStore: boolean; creditNotes?: boolean; crossStoreReturns?: boolean };
    currency: { code: string; decimals: number };
}

export interface CatalogItem {
    variantUuid: string;
    productUuid: string;
    sku: string;
    name: string;
    posName?: string | null;
    variantName?: string | null;
    barcode?: string | null;
    categoryId?: number | null;
    categoryName?: string | null;
    uomCode?: string | null;
    price: number;
    imageUrl?: string | null;
    color?: string | null;
    isWeighable: boolean;
    isFavorite: boolean;
    sortOrder: number;
    isActive: boolean;
    modifiedAt: string;
}

export interface CatalogCategory {
    id: number;
    name: string;
    sortOrder: number;
}

export interface CatalogPage {
    serverTime: string;
    nextSince: string | null;
    hasMore: boolean;
    items: CatalogItem[];
    categories: CatalogCategory[];
}

export interface SyncCustomer {
    uuid: string;
    code: string;
    name: string;
    customerType?: string | null;
    phone?: string | null;
    mobile?: string | null;
    email?: string | null;
    creditLimit: number;
    balance: number;
    isActive: boolean;
    isSystem: boolean;
    allowPosCredit: boolean;
    offlineCreditLimitOverride?: number | null;
    maxSingleCreditOrder?: number | null;
    requiresIdForCredit: boolean;
    modifiedAt: string;
}

export interface CustomerPage {
    serverTime: string;
    nextSince: string | null;
    hasMore: boolean;
    customers: SyncCustomer[];
}

export interface StockSnapshot {
    serverTime: string;
    items: { variantUuid: string; available: number }[];
}

export interface StaffPin {
    userId: number;
    name: string;
    posRole: PosRole;
    pinHash: string;
    permissions: string[];
}

// ── Device-local records (Dexie) ────────────────────────────────────────────────

/** A sale as kept on the device: the payload to push plus what the receipt needs. */
export interface LocalOrder {
    localId: string;
    syncStatus: SyncStatus;
    createdAtLocal: string;
    shiftUuid: string;
    payload: SyncOrder;
    /** Receipt data captured at sale time. */
    receipt: LocalReceipt;
    attempts: number;
    nextAttemptAt?: number | null;
    lastError?: string | null;
    orderUuid?: string | null;
    orderNumber?: string | null;
    flags?: string[];
    warnings?: string[];
    syncedAt?: string | null;
    /** Local id of the cashier session (resolved to the server uuid at push time). */
    sessionLocalId?: string | null;
}

export interface LocalReceipt {
    storeName: string;
    storeCode: string;
    terminalName: string;
    cashierName: string;
    customerName: string;
    currencyCode: string;
    taxInclusive: boolean;
    taxPercent: number;
    header?: string | null;
    footer?: string | null;
    lines: { name: string; sku: string; quantity: number; unitPrice: number; discountAmount: number; lineTotal: number }[];
    subtotal: number;
    discountAmount: number;
    taxTotal: number;
    grandTotal: number;
    payments: { method: PaymentMethod; amount: number; changeGiven: number; reference?: string | null; creditNoteNumber?: string | null }[];
    changeTotal: number;
    creditAmount: number;
}

/**
 * A39 — a return or exchange as kept on the device (always written first, like sales). Online ones are stored already
 * `synced` with the server's answer; offline ones are pushed through `POST returns/sync`. Lines of an original sale that
 * was made on this device may still lack the server line uuid (`localLineId` only): PosSync resolves it at push time.
 */
export interface LocalReturn {
    localId: string;
    kind: 'RETURN' | 'EXCHANGE';
    syncStatus: SyncStatus;
    createdAtLocal: string;
    shiftUuid: string;
    isOffline: boolean;
    originalOrderUuid?: string | null;
    originalOrderLocalId?: string | null;
    originalOrderNumber: string;
    /** The request without `lines` resolved (see `lines`). */
    request: Omit<ReturnSyncRequest, 'lines' | 'originalOrderUuid'>;
    lines: { originalLineUuid?: string | null; localLineId?: string | null; lineNumber?: number | null; variantUuid: string; quantity: number; amount: number }[];
    receipt: LocalReturnReceipt;
    attempts: number;
    nextAttemptAt?: number | null;
    lastError?: string | null;
    orderUuid?: string | null;
    orderNumber?: string | null;
    flags?: string[];
    warnings?: string[];
    syncedAt?: string | null;
    sessionLocalId?: string | null;
}

export interface LocalReturnReceipt {
    storeName: string;
    storeCode: string;
    terminalName: string;
    cashierName: string;
    customerName: string;
    currencyCode: string;
    header?: string | null;
    footer?: string | null;
    reasonName: string;
    note?: string | null;
    authorizedByName?: string | null;
    returnLines: { name: string; sku: string; quantity: number; amount: number }[];
    newLines: { name: string; sku: string; quantity: number; unitPrice: number; lineTotal: number }[];
    returnTotal: number;
    restockingFee: number;
    /** returnTotal − fee. */
    returnValue: number;
    newTotal: number;
    /** Exchange: newTotal − returnValue (> 0 paid, < 0 refunded). Return: −returnValue. */
    net: number;
    settlement?: ExchangeSettlement | null;
    refundMethod?: string | null;
    refundAmount: number;
    refundReference?: string | null;
    payments: { method: PaymentMethod; amount: number; changeGiven: number; reference?: string | null }[];
    storeCredit?: { uuid?: string | null; number: string; amount: number; balance: number; expiresAt?: string | null; customerName?: string | null } | null;
}

export interface LocalCashMovement {
    localId: string;
    shiftUuid: string;
    syncStatus: SyncStatus;
    direction: CashDirection;
    amount: number;
    reason: CashReason;
    note?: string | null;
    createdAtLocal: string;
    attempts: number;
    nextAttemptAt?: number | null;
    lastError?: string | null;
    uuid?: string | null;
}

export interface LocalSession {
    localId: string;
    shiftUuid: string;
    userId: number;
    userName: string;
    startedAt: string;
    syncStatus: SyncStatus;
    uuid?: string | null;
    attempts: number;
    nextAttemptAt?: number | null;
    lastError?: string | null;
}

export interface SyncLogEntry {
    id?: number;
    at: string;
    kind: string;
    ok: boolean;
    message: string;
}

export interface ConfigEntry {
    key: string;
    value: unknown;
}
