// A39 — returns, exchanges and store credit on the till: pure rules (the server re-checks everything).
//   D-5  restocking fee = round(return total (incl. tax) × fee% / 100), the reason's override wins, 0 waives;
//        refund = return total − fee; each line's share follows the A38 remainder rule before the fee.
//   §3   manager needed: reason.requiresManager · refund > maxReturnNoAuth · window expired · cross-store + crossStoreRequiresManager.
//   §3   refund methods: refundToOriginalMethod ⇒ the original's largest non-cash tender (else CASH); allowCashRefund;
//        allowCreditNoteRefund (+ FEATURE_POS_CREDIT_NOTES) ⇒ STORE_CREDIT; ON_ACCOUNT only for credit sales.
//   D-6  exchange net = new total − (return total − fee); > 0 PAY · = 0 EVEN · < 0 REFUND. Exchange window = exchange ?? return days, 0 = none.
//   D-2  store credit tender: online only, ACTIVE, not expired, bound customer, ≤ min(balance, remaining), no change.

import { divRound, round2, toUnits } from './pos-calculator';
import {
    EffectivePolicy,
    ExchangeSettlement,
    LocalOrder,
    LocalReturn,
    LookupLine,
    LookupPolicy,
    ManagerReason,
    OrderLookup,
    PaymentMethod,
    ReturnPolicy,
    ReturnReason,
    StoreCredit
} from './pos.models';

export const MSG = {
    voided: 'Cannot return a voided order.',
    allReturned: 'All items in this order have already been returned.',
    onlySales: 'Only sales can be returned.',
    windowExpired: (n: number) => `Return window expired (${n} days).`,
    tooMany: (q: number, name: string, r: number) => `Cannot return ${q} of '${name}'. Only ${r} returnable.`,
    noteRequired: 'A note is required for this reason.',
    managerRequired: 'Manager authorization required.',
    crossStoreRefused: 'This store does not accept returns from other locations.',
    crossStoreOffline: 'Cross-store returns require internet connectivity.',
    noPolicy: 'No return policy configured. Contact your administrator.',
    noExchanges: "Exchanges are not allowed under this store's policy.",
    creditExpired: 'This credit note has expired.',
    creditOffline: 'Credit note redemption requires internet connectivity.',
    creditOtherCustomer: 'This credit note belongs to another customer.',
    // POSR details
    creditCancelled: 'This credit note was cancelled.',
    creditNoBalance: 'This credit note has no balance left.',
    authInvalid: 'The manager authorization is invalid or has expired.',
    authExceeded: 'The refund exceeds the amount the manager authorized.',
    refundMethodRefused: "Refund method not allowed by the store's return policy."
} as const;

const DAY_MS = 24 * 60 * 60 * 1000;

// ── the order being returned (online lookup or a sale on this device) ─────────────

export interface ReturnableLine {
    /** Server line uuid, else the device's localLineId. */
    key: string;
    lineUuid: string | null;
    localLineId: string | null;
    lineNumber: number;
    variantUuid: string;
    productName: string;
    sku: string;
    quantity: number;
    returnedQuantity: number;
    returnableQuantity: number;
    unitPrice: number;
    taxPercent: number;
    lineTotal: number;
    refundIfAllReturned: number;
}

export interface ReturnableOrder {
    source: 'server' | 'local';
    uuid: string | null;
    localId: string | null;
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
    orderType: string;
    isSameStore: boolean;
    daysSincePurchase: number;
    payments: { method: string; amount: number; reference?: string | null }[];
    lines: ReturnableLine[];
}

export interface PolicyView {
    policy: EffectivePolicy | null;
    withinReturnWindow: boolean;
    withinExchangeWindow: boolean;
    /** Exchange window is 0 (or no policy): no exchanges at all. */
    exchangesBlocked: boolean;
    windowDaysRemaining: number | null;
    crossStoreAllowed: boolean;
    requiresManagerForCrossStore: boolean;
}

/** Route key of an order: the server uuid, or `local-{localId}` for a sale known only to this device. */
export function orderKey(o: Pick<ReturnableOrder, 'uuid' | 'localId'>): string {
    return o.uuid ?? `local-${o.localId}`;
}

export function fromLookup(l: OrderLookup): { order: ReturnableOrder; policy: PolicyView } | null {
    if (!l.found || !l.order) return null;
    const o = l.order;
    const order: ReturnableOrder = {
        source: 'server',
        uuid: o.uuid,
        localId: null,
        orderNumber: o.orderNumber,
        storeUuid: o.storeUuid,
        storeName: o.storeName,
        storeCode: o.storeCode,
        createdAt: o.createdAt,
        grandTotal: o.grandTotal,
        customerUuid: o.customerUuid ?? null,
        customerName: o.customerName,
        isWalkIn: !!o.isWalkIn,
        status: o.status,
        orderType: o.orderType,
        isSameStore: o.isSameStore,
        daysSincePurchase: o.daysSincePurchase,
        payments: o.payments ?? [],
        lines: (o.lines ?? []).map((x: LookupLine, i) => ({
            key: x.lineUuid,
            lineUuid: x.lineUuid,
            localLineId: null,
            lineNumber: i + 1,
            variantUuid: x.variantUuid,
            productName: x.productName,
            sku: x.sku,
            quantity: x.quantity,
            returnedQuantity: x.returnedQuantity,
            returnableQuantity: x.returnableQuantity,
            unitPrice: x.unitPrice,
            taxPercent: x.taxPercent,
            lineTotal: x.lineTotal,
            refundIfAllReturned: x.refundIfAllReturned
        }))
    };
    const p: LookupPolicy | null = l.policy ?? null;
    const policy: PolicyView = p
        ? {
              policy: p,
              withinReturnWindow: p.withinReturnWindow,
              withinExchangeWindow: p.withinExchangeWindow,
              exchangesBlocked: exchangeWindowDays(p) === 0,
              windowDaysRemaining: p.windowDaysRemaining ?? null,
              crossStoreAllowed: p.crossStoreAllowed,
              requiresManagerForCrossStore: p.requiresManagerForCrossStore
          }
        : noPolicyView();
    return { order, policy };
}

function noPolicyView(): PolicyView {
    return { policy: null, withinReturnWindow: false, withinExchangeWindow: false, exchangesBlocked: true, windowDaysRemaining: null, crossStoreAllowed: false, requiresManagerForCrossStore: false };
}

/** Full days since the purchase. */
export function daysSince(createdAt: string, now = Date.now()): number {
    const t = new Date(createdAt).getTime();
    return isFinite(t) ? Math.max(0, Math.floor((now - t) / DAY_MS)) : 0;
}

export function exchangeWindowDays(p: Pick<ReturnPolicy, 'exchangeWindowDays' | 'returnWindowDays'>): number | null {
    return p.exchangeWindowDays ?? p.returnWindowDays ?? null;
}

/** The window rule for a purchase `days` old: null days = no limit; within = days ≤ window. */
export function windowView(policy: EffectivePolicy | null, days: number): PolicyView {
    if (!policy) return noPolicyView();
    const ret = policy.returnWindowDays ?? null;
    const exc = exchangeWindowDays(policy);
    return {
        policy,
        withinReturnWindow: ret === null || days <= ret,
        withinExchangeWindow: exc === null || (exc > 0 && days <= exc),
        exchangesBlocked: exc === 0,
        windowDaysRemaining: ret === null ? null : Math.max(0, ret - days),
        // Offline returns are same-store only.
        crossStoreAllowed: false,
        requiresManagerForCrossStore: false
    };
}

/**
 * A sale made on this device, as a returnable order (offline lookup). Returned quantities and amounts come from the
 * returns this device has recorded against it (the server re-checks at sync).
 */
export function fromLocalOrder(o: LocalOrder, priorReturns: LocalReturn[], policy: EffectivePolicy | null, storeUuid: string, now = Date.now()): { order: ReturnableOrder; policy: PolicyView } {
    const qtyBack: Record<string, number> = {};
    const amountBack: Record<string, number> = {};
    for (const r of priorReturns) {
        if (r.syncStatus === 'error') continue;
        for (const l of r.lines) {
            const k = l.localLineId ?? l.originalLineUuid ?? '';
            qtyBack[k] = round4((qtyBack[k] ?? 0) + l.quantity);
            amountBack[k] = round2((amountBack[k] ?? 0) + l.amount);
        }
    }
    const lines: ReturnableLine[] = o.payload.lines.map((pl, i) => {
        const rl = o.receipt.lines[i];
        const returned = qtyBack[pl.localLineId] ?? 0;
        const lineTotal = rl?.lineTotal ?? 0;
        return {
            key: pl.localLineId,
            lineUuid: null,
            localLineId: pl.localLineId,
            lineNumber: i + 1,
            variantUuid: pl.variantUuid,
            productName: rl?.name ?? pl.variantUuid,
            sku: rl?.sku ?? '',
            quantity: pl.quantity,
            returnedQuantity: returned,
            returnableQuantity: Math.max(0, round4(pl.quantity - returned)),
            unitPrice: pl.unitPrice,
            taxPercent: o.receipt.taxPercent,
            lineTotal,
            refundIfAllReturned: round2(lineTotal - (amountBack[pl.localLineId] ?? 0))
        };
    });
    const days = daysSince(o.createdAtLocal, now);
    const order: ReturnableOrder = {
        source: 'local',
        uuid: o.orderUuid ?? null,
        localId: o.localId,
        orderNumber: o.orderNumber || o.payload.offlineNumber || o.localId,
        storeUuid,
        storeName: o.receipt.storeName,
        storeCode: o.receipt.storeCode,
        createdAt: o.createdAtLocal,
        grandTotal: o.receipt.grandTotal,
        customerUuid: o.payload.customerUuid ?? null,
        customerName: o.receipt.customerName,
        isWalkIn: !o.payload.customerUuid,
        status: lines.every(l => l.returnableQuantity <= 0) ? 'RETURNED' : lines.some(l => l.returnedQuantity > 0) ? 'PARTIALLY_RETURNED' : 'COMPLETED',
        orderType: 'SALE',
        isSameStore: true,
        daysSincePurchase: days,
        payments: o.receipt.payments.map(p => ({ method: p.method, amount: round2(p.amount - (p.changeGiven ?? 0)), reference: p.reference ?? null })),
        lines
    };
    return { order, policy: windowView(policy, days) };
}

/** Why the order cannot be returned at all (exact contract wording), or null. */
export function orderReturnBlock(o: Pick<ReturnableOrder, 'status' | 'orderType' | 'lines'>): string | null {
    if (o.status === 'VOIDED') return MSG.voided;
    if (o.orderType !== 'SALE') return MSG.onlySales;
    if (o.status === 'RETURNED' || !o.lines.some(l => l.returnableQuantity > 0)) return MSG.allReturned;
    return null;
}

/** Store code inside a receipt number `POS-{store}-…` / `RET-{store}-…`, or null. */
export function storeCodeOfReceipt(receipt: string): string | null {
    const m = /^(?:POS|RET|EXC)-([^-]+)-/i.exec(receipt.trim());
    return m ? m[1].toUpperCase() : null;
}

// ── amounts ───────────────────────────────────────────────────────────────────

/**
 * Refund share of `qty` units of a line (A38 remainder rule): round(lineTotal × qty / quantity), except that taking the
 * line's last returnable units refunds exactly what is left (`refundIfAllReturned`).
 */
export function lineReturnAmount(line: Pick<ReturnableLine, 'quantity' | 'lineTotal' | 'returnableQuantity' | 'refundIfAllReturned'>, qty: number): number {
    if (!(qty > 0) || !(line.quantity > 0)) return 0;
    if (qty >= line.returnableQuantity) return round2(line.refundIfAllReturned);
    return round2((line.lineTotal * qty) / line.quantity);
}

export function returnLinesTotal(lines: ReturnableLine[], qty: Record<string, number>): number {
    return round2(lines.reduce((s, l) => s + lineReturnAmount(l, qty[l.key] ?? 0), 0));
}

/** The fee % that applies: the reason's override wins (0 waives), else the policy's. */
export function effectiveFeePct(policy: Pick<ReturnPolicy, 'restockingFeePct'> | null, reason: Pick<ReturnReason, 'restockingFeeOverride'> | null): number {
    const o = reason?.restockingFeeOverride;
    if (o !== null && o !== undefined) return Math.max(0, Math.min(100, o));
    return Math.max(0, Math.min(100, policy?.restockingFeePct ?? 0));
}

/** round(total × pct / 100), 2 dp away from zero, computed on integers. */
export function restockingFee(total: number, pct: number): number {
    if (!(total > 0) || !(pct > 0)) return 0;
    const c = divRound(toUnits(total, 2) * toUnits(Math.min(pct, 100), 4), 100 * 10_000);
    return c / 100;
}

export interface ReturnSummary {
    /** Σ line shares (incl. tax). */
    subtotal: number;
    feePct: number;
    fee: number;
    /** subtotal − fee. */
    refund: number;
    itemCount: number;
}

export function returnSummary(lines: ReturnableLine[], qty: Record<string, number>, policy: Pick<ReturnPolicy, 'restockingFeePct'> | null, reason: Pick<ReturnReason, 'restockingFeeOverride'> | null): ReturnSummary {
    const subtotal = returnLinesTotal(lines, qty);
    const feePct = effectiveFeePct(policy, reason);
    const fee = restockingFee(subtotal, feePct);
    return { subtotal, feePct, fee, refund: round2(subtotal - fee), itemCount: lines.reduce((s, l) => s + ((qty[l.key] ?? 0) > 0 ? qty[l.key] : 0), 0) };
}

/** Checks a quantity against what is returnable (exact server wording), or null. */
export function quantityError(line: Pick<ReturnableLine, 'productName' | 'returnableQuantity'>, qty: number): string | null {
    return qty > line.returnableQuantity ? MSG.tooMany(qty, line.productName, line.returnableQuantity) : null;
}

/** A reason that needs a note gets one. */
export function noteError(reason: Pick<ReturnReason, 'requiresNote'> | null, note: string | null | undefined): string | null {
    return reason?.requiresNote && !(note ?? '').trim() ? MSG.noteRequired : null;
}

/** Reasons offered for a return / an exchange (active, matching `appliesTo`, by sortOrder). */
export function reasonsFor(all: ReturnReason[], kind: 'RETURN' | 'EXCHANGE'): ReturnReason[] {
    return all.filter(r => r.isActive && (r.appliesTo === 'BOTH' || r.appliesTo === kind)).sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name));
}

// ── manager authorization ─────────────────────────────────────────────────────

export function managerReasons(input: {
    reason: Pick<ReturnReason, 'requiresManager'> | null;
    amount: number;
    policy: PolicyView;
    kind: 'RETURN' | 'EXCHANGE';
    crossStore: boolean;
}): ManagerReason[] {
    const out: ManagerReason[] = [];
    const p = input.policy.policy;
    if (input.reason?.requiresManager) out.push('REASON');
    if (p && p.maxReturnNoAuth !== null && p.maxReturnNoAuth !== undefined && input.amount > p.maxReturnNoAuth) out.push('AMOUNT');
    const within = input.kind === 'EXCHANGE' ? input.policy.withinExchangeWindow : input.policy.withinReturnWindow;
    if (!within) out.push('WINDOW');
    if (input.crossStore && input.policy.requiresManagerForCrossStore) out.push('CROSS_STORE');
    return out;
}

export const MANAGER_REASON_TEXT: Record<ManagerReason, string> = {
    REASON: 'The return reason needs a manager',
    AMOUNT: 'The amount is above the limit without authorization',
    WINDOW: 'The return window has passed',
    CROSS_STORE: 'The sale was made in another store'
};

/** Manager on this device (offline): SUPERVISOR with POS_RETURN_OVERRIDE, or POS_RETURN_CROSS_STORE when only cross-store needs it. */
export function qualifiesAsManager(staff: { posRole: string; permissions: string[] }, reasons: ManagerReason[]): boolean {
    if (staff.posRole !== 'SUPERVISOR') return false;
    if (staff.permissions.includes('POS_RETURN_OVERRIDE')) return true;
    return reasons.length > 0 && reasons.every(r => r === 'CROSS_STORE') && staff.permissions.includes('POS_RETURN_CROSS_STORE');
}

/** Manager reasons from a server refusal (`result.requiresManager`, `result.reasons`), or null when it is another error. */
export function managerReasonsFromError(body: unknown): ManagerReason[] | null {
    const result = (body as { result?: { requiresManager?: boolean; reasons?: ManagerReason[] } } | null)?.result;
    if (!result?.requiresManager) return null;
    return result.reasons?.length ? result.reasons : ['WINDOW'];
}

// ── refund methods ────────────────────────────────────────────────────────────

const NON_REFUNDABLE: string[] = ['EXCHANGE'];

/** The original sale's largest non-cash tender method, else CASH (ties: the first). */
export function originalRefundMethod(payments: { method: string; amount: number }[]): PaymentMethod {
    let best: { method: string; amount: number } | null = null;
    for (const p of payments) {
        if (p.method === 'CASH' || NON_REFUNDABLE.includes(p.method) || !(p.amount > 0)) continue;
        if (!best || p.amount > best.amount) best = p;
    }
    return (best?.method as PaymentMethod) ?? 'CASH';
}

export function isCreditSale(payments: { method: string }[]): boolean {
    return payments.some(p => p.method === 'ON_ACCOUNT');
}

/**
 * Refund methods the policy allows for this order, in display order.
 * refundToOriginalMethod ⇒ only the original method (dropped when it is CASH and cash refunds are off); otherwise the
 * terminal's methods (CASH only with allowCashRefund, ON_ACCOUNT only for credit sales). STORE_CREDIT is added when the
 * policy allows store-credit refunds and the feature is on.
 */
export function allowedRefundMethods(input: {
    policy: Pick<ReturnPolicy, 'refundToOriginalMethod' | 'allowCashRefund' | 'allowCreditNoteRefund'> | null;
    payments: { method: string; amount: number }[];
    enabled: PaymentMethod[];
    creditNotesEnabled: boolean;
}): PaymentMethod[] {
    const p = input.policy;
    if (!p) return [];
    const credit = isCreditSale(input.payments);
    let out: PaymentMethod[];
    if (p.refundToOriginalMethod) {
        const orig = originalRefundMethod(input.payments);
        out = orig === 'CASH' && !p.allowCashRefund ? [] : orig === 'STORE_CREDIT' ? [] : [orig];
    } else {
        out = input.enabled.filter(m => m !== 'STORE_CREDIT' && m !== 'EXCHANGE' && (m !== 'CASH' || p.allowCashRefund) && (m !== 'ON_ACCOUNT' || credit));
    }
    if (p.allowCreditNoteRefund && input.creditNotesEnabled) out.push('STORE_CREDIT');
    return out;
}

/** The refund method preselected: the original method when allowed, else the first allowed one. */
export function defaultRefundMethod(allowed: PaymentMethod[], payments: { method: string; amount: number }[]): PaymentMethod | null {
    const orig = originalRefundMethod(payments);
    return allowed.includes(orig) ? orig : (allowed[0] ?? null);
}

// ── exchange ──────────────────────────────────────────────────────────────────

export function exchangeNet(newTotal: number, returnValue: number): { net: number; settlement: ExchangeSettlement } {
    const net = round2(newTotal - returnValue);
    return { net, settlement: net > 0 ? 'PAY' : net < 0 ? 'REFUND' : 'EVEN' };
}

/** Why an exchange is not possible (exchange window 0 / no policy), or null. Expired windows need a manager instead. */
export function exchangeBlock(policy: PolicyView): string | null {
    if (!policy.policy) return MSG.noPolicy;
    if (policy.exchangesBlocked) return MSG.noExchanges;
    return null;
}

// ── store credit ──────────────────────────────────────────────────────────────

export function isCreditExpired(c: Pick<StoreCredit, 'status' | 'expiresAt'>, now = Date.now()): boolean {
    return c.status === 'EXPIRED' || (!!c.expiresAt && new Date(c.expiresAt).getTime() < now);
}

/**
 * The amount a STORE_CREDIT tender may take, or the refusal (contract wording where it exists).
 * @param used amount of this note already in the tender list.
 */
export function storeCreditTender(input: {
    note: Pick<StoreCredit, 'status' | 'expiresAt' | 'remainingBalance' | 'customerUuid' | 'number'>;
    online: boolean;
    orderCustomerUuid: string | null;
    due: number;
    used?: number;
    requested?: number | null;
    now?: number;
}): { amount: number } | { error: string } {
    const n = input.note;
    if (!input.online) return { error: MSG.creditOffline };
    if (isCreditExpired(n, input.now)) return { error: MSG.creditExpired };
    if (n.status === 'CANCELLED') return { error: MSG.creditCancelled };
    if (n.status !== 'ACTIVE') return { error: MSG.creditNoBalance };
    if (n.customerUuid && n.customerUuid !== input.orderCustomerUuid) return { error: MSG.creditOtherCustomer };
    const balance = round2(n.remainingBalance - (input.used ?? 0));
    if (!(balance > 0)) return { error: MSG.creditNoBalance };
    if (!(input.due > 0)) return { error: 'The order is already fully paid.' };
    const cap = round2(Math.min(balance, input.due));
    const req = input.requested === null || input.requested === undefined ? cap : round2(input.requested);
    if (!(req > 0)) return { error: 'Enter an amount greater than zero.' };
    if (req > cap) return { error: `Store credit can cover at most ${cap.toFixed(2)} here (no change is given).` };
    return { amount: req };
}

/** Customer the issued store credit belongs to: the original customer, or bearer (null) for walk-in. */
export function storeCreditCustomer(order: Pick<ReturnableOrder, 'customerUuid' | 'isWalkIn'>): string | null {
    return order.isWalkIn || !order.customerUuid ? null : order.customerUuid;
}

/** Offline number of a store credit issued by an offline return (D-2). */
export function offlineCreditNumber(storeCode: string, localId: string): string {
    return `CN-${storeCode}-OFF-${localId.replace(/-/g, '').slice(0, 8).toUpperCase()}`;
}

function round4(v: number): number {
    return Math.round(v * 10_000) / 10_000;
}
