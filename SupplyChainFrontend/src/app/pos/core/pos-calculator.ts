// A38 API-CONTRACT §1 — the totals calculator, mirrored exactly from the server (which stays the source of truth).
// Everything is computed on integers (cents, thousandths of a unit, millionths of a percent) so the result is the same as
// C# `decimal` with Math.Round(x, 2, MidpointRounding.AwayFromZero) — binary floating point would misround x.xx5 cases.

import { DiscountType, PaymentMethod } from './pos.models';

export interface CalcLineInput {
    quantity: number;
    unitPrice: number;
    discountType: DiscountType;
    discountValue: number;
}

export interface CalcOrderInput {
    lines: CalcLineInput[];
    orderDiscountType: DiscountType;
    orderDiscountValue: number;
    taxPercent: number;
    taxInclusive: boolean;
}

export interface CalcLineResult {
    gross: number;
    lineDiscount: number;
    net: number;
    /** Share of the order discount allocated to this line. */
    allocatedDiscount: number;
    /** net − allocated order discount (the amount the tax is computed on). */
    netAfter: number;
    /** line discount + allocated order discount (PosOrderLine.discountAmount). */
    discountAmount: number;
    /** Exclusive amount after all discounts (PosOrderLine.netAmount). */
    netAmount: number;
    taxAmount: number;
    lineTotal: number;
}

export interface CalcResult {
    lines: CalcLineResult[];
    subtotal: number;
    discountAmount: number;
    taxTotal: number;
    grandTotal: number;
}

// ── integer helpers ─────────────────────────────────────────────────────────────

/** Integer division rounding half away from zero (b > 0). Exact for |a| < 2^53. */
export function divRound(a: number, b: number): number {
    if (b <= 0) throw new Error('divRound: divisor must be positive');
    const sign = a < 0 ? -1 : 1;
    const abs = Math.abs(a);
    const q = Math.floor(abs / b);
    const r = abs - q * b;
    return sign * (r * 2 >= b ? q + 1 : q);
}

/** Decimal value → integer number of 10^-places units, tolerant of binary noise (1.005 → 1005 for places 3). */
export function toUnits(value: number, places: number): number {
    if (!isFinite(value)) return 0;
    return Math.round(Number(`${value}e${places}`)) || 0;
}

const cents = (v: number) => toUnits(v, 2);
const fromCents = (c: number) => (c === 0 ? 0 : c / 100);

/** Round to 2 dp, midpoint away from zero (Math.Round(x, 2, AwayFromZero)). */
export function round2(value: number): number {
    if (!isFinite(value)) return 0;
    const sign = value < 0 ? -1 : 1;
    const r = Math.round(Number(`${Math.abs(value)}e2`));
    return r === 0 ? 0 : (sign * r) / 100;
}

/** Discount in cents of `baseCents` (PERCENT ⇒ round(base × v / 100); AMOUNT ⇒ min(v, base); NONE ⇒ 0). */
function discountCents(baseCents: number, type: DiscountType, value: number): number {
    if (!value || value <= 0 || baseCents <= 0) return 0;
    if (type === 'PERCENT') {
        const pct = Math.min(toUnits(value, 4), 100 * 10_000); // percent in 1/10000ths, capped at 100 %
        return divRound(baseCents * pct, 100 * 10_000);
    }
    if (type === 'AMOUNT') return Math.min(cents(value), baseCents);
    return 0;
}

// ── §1 ──────────────────────────────────────────────────────────────────────────

export function calculateOrder(input: CalcOrderInput): CalcResult {
    const rateU = toUnits(input.taxPercent || 0, 4); // percent × 10 000

    // 1. Per line
    const raw = input.lines.map(l => {
        // POSB: quantities ≤ 4 decimals, prices ≤ 2.
        const grossC = divRound(toUnits(l.quantity, 4) * cents(l.unitPrice), 10_000);
        const lineDiscC = discountCents(grossC, l.discountType, l.discountValue);
        return { grossC, lineDiscC, netC: grossC - lineDiscC };
    });

    // 2. Subtotal + order discount allocated proportionally to net; the remainder goes on the last line whose net is not
    //    zero (POSB D-41..: a zero-net last line never receives a negative share).
    const subtotalC = raw.reduce((s, l) => s + l.netC, 0);
    const orderDiscC = discountCents(subtotalC, input.orderDiscountType, input.orderDiscountValue);
    const alloc: number[] = raw.map(() => 0);
    if (orderDiscC > 0 && subtotalC > 0 && raw.length > 0) {
        let last = raw.length - 1;
        while (last > 0 && raw[last].netC === 0) last--;
        let given = 0;
        for (let i = 0; i < last; i++) {
            alloc[i] = divRound(orderDiscC * raw[i].netC, subtotalC);
            given += alloc[i];
        }
        alloc[last] = orderDiscC - given;
    }

    // 3. Tax
    const lines: CalcLineResult[] = raw.map((l, i) => {
        const netAfterC = l.netC - alloc[i];
        let taxC: number;
        let lineTotalC: number;
        let netAmountC: number;
        if (input.taxInclusive) {
            // round(netAfter − netAfter / (1 + rate/100)) = round(netAfter × rate / (100 + rate))
            taxC = rateU > 0 ? divRound(netAfterC * rateU, 100 * 10_000 + rateU) : 0;
            lineTotalC = netAfterC;
            netAmountC = netAfterC - taxC;
        } else {
            taxC = rateU > 0 ? divRound(netAfterC * rateU, 100 * 10_000) : 0;
            lineTotalC = netAfterC + taxC;
            netAmountC = netAfterC;
        }
        return {
            gross: fromCents(l.grossC),
            lineDiscount: fromCents(l.lineDiscC),
            net: fromCents(l.netC),
            allocatedDiscount: fromCents(alloc[i]),
            netAfter: fromCents(netAfterC),
            discountAmount: fromCents(l.lineDiscC + alloc[i]),
            netAmount: fromCents(netAmountC),
            taxAmount: fromCents(taxC),
            lineTotal: fromCents(netAmountC + taxC)
        };
    });

    const taxTotalC = lines.reduce((s, l) => s + cents(l.taxAmount), 0);
    const grandC = input.taxInclusive ? subtotalC - orderDiscC : subtotalC - orderDiscC + taxTotalC;

    return {
        lines,
        subtotal: fromCents(subtotalC),
        discountAmount: fromCents(orderDiscC),
        taxTotal: fromCents(taxTotalC),
        grandTotal: fromCents(grandC)
    };
}

// ── §1.5 tenders ────────────────────────────────────────────────────────────────

export interface Tender {
    localId: string;
    method: PaymentMethod;
    amount: number;
    changeGiven: number;
    reference?: string | null;
    /** A39: STORE_CREDIT tender — the redeemed note (and its number, for the receipt). */
    creditNoteUuid?: string | null;
    creditNoteNumber?: string | null;
}

/** Σ(amount − changeGiven), in money. */
export function tenderedNet(tenders: Tender[]): number {
    return fromCents(tenders.reduce((s, t) => s + cents(t.amount) - cents(t.changeGiven), 0));
}

/** What is still to be paid (never negative). */
export function remainingDue(grandTotal: number, tenders: Tender[]): number {
    return fromCents(Math.max(0, cents(grandTotal) - cents(tenderedNet(tenders))));
}

/** The tender list balances the order: Σ(amount − change) = grandTotal (± 0.01). */
export function isFullyTendered(grandTotal: number, tenders: Tender[]): boolean {
    return Math.abs(cents(grandTotal) - cents(tenderedNet(tenders))) <= 1;
}

/**
 * Adds a tender (§1.5 / POS-PAY-03): only CASH may exceed the remaining balance (the excess becomes change); every other
 * method is refused above it. ON_ACCOUNT always covers exactly what remains. Returns the new list or an error message.
 */
export function addTender(
    grandTotal: number,
    tenders: Tender[],
    method: PaymentMethod,
    amount: number,
    localId: string,
    reference?: string | null
): { tenders: Tender[] } | { error: string } {
    const remainingC = cents(remainingDue(grandTotal, tenders));
    if (remainingC <= 0) return { error: 'The order is already fully paid.' };
    let amountC = method === 'ON_ACCOUNT' ? remainingC : cents(amount);
    if (!(amountC > 0)) return { error: 'Enter an amount greater than zero.' };
    let changeC = 0;
    if (amountC > remainingC) {
        if (method !== 'CASH') return { error: `A ${method} payment of ${(amountC / 100).toFixed(2)} exceeds the remaining balance of ${(remainingC / 100).toFixed(2)}.` };
        changeC = amountC - remainingC;
    }
    const tender: Tender = { localId, method, amount: fromCents(amountC), changeGiven: fromCents(changeC), reference: reference || null };
    return { tenders: [...tenders, tender] };
}

/**
 * The server's tender check (POSB, in the order sent), with its wording; null when the tenders are acceptable.
 * Change only on CASH; non-cash may not exceed what is still open before it (+0.005); Σ(amount − change) = total ± 0.01.
 */
export function tenderError(grandTotal: number, tenders: Tender[]): string | null {
    let openC = cents(grandTotal);
    for (const t of tenders) {
        const a = cents(t.amount);
        const ch = cents(t.changeGiven);
        if (ch > 0 && t.method !== 'CASH') return 'Only cash payments can give change.';
        if (t.method !== 'CASH' && a > openC) {
            return `A ${t.method} payment of ${(a / 100).toFixed(2)} exceeds the remaining balance of ${(Math.max(0, openC) / 100).toFixed(2)}.`;
        }
        openC -= a - ch;
    }
    if (!isFullyTendered(grandTotal, tenders)) {
        return `Payments (${tenderedNet(tenders).toFixed(2)}) do not match the order total (${round2(grandTotal).toFixed(2)}).`;
    }
    return null;
}

/** Total change handed back. */
export function changeTotal(tenders: Tender[]): number {
    return fromCents(tenders.reduce((s, t) => s + cents(t.changeGiven), 0));
}

/** Amount put on the customer's account. */
export function creditAmount(tenders: Tender[]): number {
    return fromCents(tenders.filter(t => t.method === 'ON_ACCOUNT').reduce((s, t) => s + cents(t.amount), 0));
}

/**
 * Quick cash amounts for the payment screen: the next round notes ABOVE the due amount, every one a multiple of 50
 * (50, 100, 500, 1000, 5000 steps), at most five. Always strictly greater than what is due — a due amount that is itself a
 * multiple of 50 gets the next one up (300 → 350, 400, 500, 1000, 5000). Paying the exact amount stays the prefilled value
 * of the amount box ("Add cash").
 */
export function quickAmounts(due: number): number[] {
    if (due <= 0) return [];
    const out = new Set<number>();
    for (const step of [50, 100, 500, 1000, 5000]) {
        out.add((Math.floor(round2(due) / step) + 1) * step); // the smallest multiple of `step` above the due amount
        if (out.size >= 5) break;
    }
    return [...out].sort((a, b) => a - b);
}
