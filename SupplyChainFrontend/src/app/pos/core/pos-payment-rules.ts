// A38 §5 / D-12 / FSD §9.3 — ON_ACCOUNT (credit sale) rules on the till. The server re-checks everything at sync.

import { round2 } from './pos-calculator';
import { CustomerCredit, SyncCustomer } from './pos.models';

export const WALK_IN_CODE = 'C-WALKIN';

/** null (no customer picked), the system walk-in customer, or code C-WALKIN. */
export function isWalkIn(c: Pick<SyncCustomer, 'isSystem' | 'code'> | null | undefined): boolean {
    const code = (c?.code ?? '').toUpperCase();
    return !c || !!c.isSystem || code === WALK_IN_CODE || code === 'WALK_IN';
}

export interface CreditDecision {
    ok: boolean;
    message?: string;
    /** Allowed only thanks to the offline tolerance — the server will flag CREDIT_OVER_LIMIT. */
    overLimit?: boolean;
    /** The customer requires an ID for credit; the cashier enters it as the tender reference. */
    requiresId?: boolean;
}

function money(v: number): string {
    return round2(v).toFixed(2);
}

/** Checks that need no credit data: feature on, a named customer allowed POS credit. */
export function basicCreditCheck(customer: SyncCustomer | null, creditSalesEnabled: boolean): CreditDecision {
    if (!creditSalesEnabled) return { ok: false, message: 'Credit sales are not enabled.' };
    if (!customer) return { ok: false, message: 'Pick the customer first — a credit sale needs a named customer.' };
    if (isWalkIn(customer)) return { ok: false, message: 'Walk-In customers cannot use credit.' };
    if (!customer.allowPosCredit) return { ok: false, message: 'This customer is not allowed to buy on credit at the POS.' };
    return { ok: true, requiresId: customer.requiresIdForCredit };
}

/** Online: the live balance from GET api/pos/customers/{uuid}/credit. */
export function onlineCreditCheck(credit: CustomerCredit, amount: number): CreditDecision {
    if (credit.isWalkIn) return { ok: false, message: 'Walk-In customers cannot use credit.' };
    if (!credit.allowPosCredit) return { ok: false, message: 'This customer is not allowed to buy on credit at the POS.' };
    if (credit.maxSingleCreditOrder != null && amount > credit.maxSingleCreditOrder) {
        return { ok: false, message: `Credit for one order cannot exceed ${money(credit.maxSingleCreditOrder)}.` };
    }
    if (amount > round2(credit.available)) {
        return { ok: false, message: `Credit limit exceeded. Limit: ${money(credit.creditLimit)}. Balance: ${money(credit.balance)}. This order: ${money(amount)}.` };
    }
    return { ok: true, requiresId: credit.requiresIdForCredit };
}

/**
 * Offline: the synced balance against the offline limit (override, else credit limit) raised by the store's
 * offline tolerance %. Within the limit ⇒ ok; above the limit but within the tolerance ⇒ ok + overLimit; beyond ⇒ refused.
 * A limit of 0 (or none) means no credit offline.
 */
export function offlineCreditCheck(customer: SyncCustomer, amount: number, tolerancePct: number): CreditDecision {
    const basic = basicCreditCheck(customer, true);
    if (!basic.ok) return basic;
    if (customer.maxSingleCreditOrder != null && amount > customer.maxSingleCreditOrder) {
        return { ok: false, message: `Credit for one order cannot exceed ${money(customer.maxSingleCreditOrder)}.` };
    }
    const limit = customer.offlineCreditLimitOverride ?? customer.creditLimit ?? 0;
    if (!(limit > 0)) return { ok: false, message: 'This customer has no credit limit, so credit is not possible offline.' };
    const exposure = round2((customer.balance ?? 0) + amount);
    const tolerated = round2(limit * (1 + Math.max(0, tolerancePct || 0) / 100));
    if (exposure > tolerated) {
        return {
            ok: false,
            message: `Credit limit exceeded (offline). Limit: ${money(limit)}${tolerancePct ? ` + ${tolerancePct}% tolerance` : ''}. Balance: ${money(customer.balance)}. This order: ${money(amount)}.`
        };
    }
    return { ok: true, overLimit: exposure > limit, requiresId: customer.requiresIdForCredit };
}
