// A38 §5 return (POSB rule) — the refund per original line:
//   round(lineTotal × qty / originalQty, 2, AwayFromZero),
//   except when the return takes the line's LAST remaining units: exactly what is left
//   (lineTotal − Σ lineTotal already returned on that line).
// E9: line 3 × 3.33 −10 % (lineTotal 10.52) returned 1 + 1 + 1 ⇒ 3.51 / 3.51 / 3.50. Refunds must equal the total exactly.

import { round2 } from './pos-calculator';
import { PaymentMethod, PosOrder, PosOrderLine } from './pos.models';

type ReturnLine = Pick<PosOrderLine, 'uuid' | 'quantity' | 'lineTotal' | 'returnableQuantity'>;

/**
 * @param alreadyReturned Σ lineTotal of earlier RETURN lines against this line (from the order's `returns`).
 */
export function returnLineAmount(line: Omit<ReturnLine, 'uuid'>, qty: number, alreadyReturned = 0): number {
    if (!(qty > 0) || !(line.quantity > 0)) return 0;
    const remaining = line.returnableQuantity ?? line.quantity;
    if (qty >= remaining) return round2(line.lineTotal - alreadyReturned);
    return round2((line.lineTotal * qty) / line.quantity);
}

export function returnTotal(lines: ReturnLine[], qty: Record<string, number>, alreadyReturned: Record<string, number> = {}): number {
    return round2(lines.reduce((s, l) => s + returnLineAmount(l, qty[l.uuid] ?? 0, alreadyReturned[l.uuid] ?? 0), 0));
}

/** Σ |lineTotal| of RETURN orders' lines per original line uuid. */
export function returnedAmounts(returnOrders: Pick<PosOrder, 'lines'>[]): Record<string, number> {
    const out: Record<string, number> = {};
    for (const r of returnOrders) {
        for (const l of r.lines) {
            if (!l.originalLineUuid) continue;
            out[l.originalLineUuid] = round2((out[l.originalLineUuid] ?? 0) + Math.abs(l.lineTotal));
        }
    }
    return out;
}

/** Refund methods: the enabled ones; ON_ACCOUNT only when the sale was (partly) on credit. */
export function refundMethods(order: Pick<PosOrder, 'paymentStatus'>, enabled: PaymentMethod[]): PaymentMethod[] {
    const credit = order.paymentStatus === 'CREDIT_SALE' || order.paymentStatus === 'PARTIAL_CREDIT';
    return enabled.filter(m => m !== 'ON_ACCOUNT' || credit);
}

/** Void allowed: a completed sale of the terminal's current shift (server re-checks). */
export function canVoid(order: Pick<PosOrder, 'orderType' | 'status' | 'shiftUuid'>, currentShiftUuid: string | null | undefined): boolean {
    return order.orderType === 'SALE' && order.status === 'COMPLETED' && !!currentShiftUuid && order.shiftUuid === currentShiftUuid;
}

export function canReturn(order: Pick<PosOrder, 'orderType' | 'status' | 'lines'>): boolean {
    return order.orderType === 'SALE' && (order.status === 'COMPLETED' || order.status === 'PARTIALLY_RETURNED') && order.lines.some(l => l.returnableQuantity > 0);
}
