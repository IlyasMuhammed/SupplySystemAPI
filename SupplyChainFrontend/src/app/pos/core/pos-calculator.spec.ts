import {
    Tender,
    addTender,
    calculateOrder,
    changeTotal,
    creditAmount,
    divRound,
    isFullyTendered,
    quickAmounts,
    remainingDue,
    round2,
    tenderError,
    tenderedNet
} from './pos-calculator';
import { DiscountType } from './pos.models';

const line = (quantity: number, unitPrice: number, discountType: DiscountType = 'NONE', discountValue = 0) => ({ quantity, unitPrice, discountType, discountValue });

// API-CONTRACT appendix (POSB, = tests/SMS.Modules.Pos.Tests/CalculatorTests.cs). Rate 17 % throughout.
interface Example {
    id: string;
    inclusive: boolean;
    lines: ReturnType<typeof line>[];
    order?: [DiscountType, number];
    gross: number[];
    lineDisc: number[];
    share: number[];
    netAmount: number[];
    tax: number[];
    lineTotal: number[];
    subtotal: number;
    discountAmount: number;
    taxTotal: number;
    grandTotal: number;
}

const EXAMPLES: Example[] = [
    { id: 'E1', inclusive: false, lines: [line(2, 10), line(1, 5.99)], gross: [20, 5.99], lineDisc: [0, 0], share: [0, 0], netAmount: [20, 5.99], tax: [3.4, 1.02], lineTotal: [23.4, 7.01], subtotal: 25.99, discountAmount: 0, taxTotal: 4.42, grandTotal: 30.41 },
    { id: 'E2', inclusive: false, lines: [line(3, 3.33, 'PERCENT', 10)], gross: [9.99], lineDisc: [1], share: [0], netAmount: [8.99], tax: [1.53], lineTotal: [10.52], subtotal: 8.99, discountAmount: 0, taxTotal: 1.53, grandTotal: 10.52 },
    { id: 'E3', inclusive: false, lines: [line(2, 10), line(1, 5.99)], order: ['PERCENT', 10], gross: [20, 5.99], lineDisc: [0, 0], share: [2, 0.6], netAmount: [18, 5.39], tax: [3.06, 0.92], lineTotal: [21.06, 6.31], subtotal: 25.99, discountAmount: 2.6, taxTotal: 3.98, grandTotal: 27.37 },
    { id: 'E4', inclusive: false, lines: [line(1, 3.33), line(1, 3.33), line(1, 3.33)], order: ['AMOUNT', 5], gross: [3.33, 3.33, 3.33], lineDisc: [0, 0, 0], share: [1.67, 1.67, 1.66], netAmount: [1.66, 1.66, 1.67], tax: [0.28, 0.28, 0.28], lineTotal: [1.94, 1.94, 1.95], subtotal: 9.99, discountAmount: 5, taxTotal: 0.84, grandTotal: 5.83 },
    { id: 'E5', inclusive: true, lines: [line(1, 11.7), line(2, 4.99)], gross: [11.7, 9.98], lineDisc: [0, 0], share: [0, 0], netAmount: [10, 8.53], tax: [1.7, 1.45], lineTotal: [11.7, 9.98], subtotal: 21.68, discountAmount: 0, taxTotal: 3.15, grandTotal: 21.68 },
    { id: 'E6', inclusive: true, lines: [line(1, 11.7, 'AMOUNT', 1), line(2, 4.99)], order: ['PERCENT', 5], gross: [11.7, 9.98], lineDisc: [1, 0], share: [0.53, 0.5], netAmount: [8.69, 8.1], tax: [1.48, 1.38], lineTotal: [10.17, 9.48], subtotal: 20.68, discountAmount: 1.03, taxTotal: 2.86, grandTotal: 19.65 },
    { id: 'E7', inclusive: false, lines: [line(1.5, 2.35, 'PERCENT', 50)], gross: [3.53], lineDisc: [1.77], share: [0], netAmount: [1.76], tax: [0.3], lineTotal: [2.06], subtotal: 1.76, discountAmount: 0, taxTotal: 0.3, grandTotal: 2.06 }
];

describe('A38 §1 calculator — POSB worked examples (API-CONTRACT appendix)', () => {
    for (const ex of EXAMPLES) {
        it(`${ex.id} matches the server`, () => {
            const r = calculateOrder({ lines: ex.lines, orderDiscountType: ex.order?.[0] ?? 'NONE', orderDiscountValue: ex.order?.[1] ?? 0, taxPercent: 17, taxInclusive: ex.inclusive });
            expect(r.lines.map(l => l.gross)).withContext('gross').toEqual(ex.gross);
            expect(r.lines.map(l => l.lineDiscount)).withContext('line disc.').toEqual(ex.lineDisc);
            expect(r.lines.map(l => l.allocatedDiscount)).withContext('share').toEqual(ex.share);
            expect(r.lines.map(l => l.netAmount)).withContext('netAmount').toEqual(ex.netAmount);
            expect(r.lines.map(l => l.taxAmount)).withContext('tax').toEqual(ex.tax);
            expect(r.lines.map(l => l.lineTotal)).withContext('lineTotal').toEqual(ex.lineTotal);
            expect(r.lines.map(l => l.discountAmount)).withContext('DTO discountAmount').toEqual(ex.lineDisc.map((d, i) => round2(d + ex.share[i])));
            expect([r.subtotal, r.discountAmount, r.taxTotal, r.grandTotal]).toEqual([ex.subtotal, ex.discountAmount, ex.taxTotal, ex.grandTotal]);
        });
    }

    it('E6 DTO discountAmount = line discount + share (1.53; 0.50)', () => {
        const r = calculateOrder({ lines: EXAMPLES[5].lines, orderDiscountType: 'PERCENT', orderDiscountValue: 5, taxPercent: 17, taxInclusive: true });
        expect(r.lines.map(l => l.discountAmount)).toEqual([1.53, 0.5]);
    });

    it('edges: AMOUNT 5.00 on a 2.00 line ⇒ 2.00; order AMOUNT 10.00 on 3.00 ⇒ 3.00, grand 0.00', () => {
        expect(calculateOrder({ lines: [line(1, 2, 'AMOUNT', 5)], orderDiscountType: 'NONE', orderDiscountValue: 0, taxPercent: 17, taxInclusive: false }).lines[0].lineDiscount).toBe(2);
        const r = calculateOrder({ lines: [line(1, 3)], orderDiscountType: 'AMOUNT', orderDiscountValue: 10, taxPercent: 17, taxInclusive: false });
        expect(r.discountAmount).toBe(3);
        expect(r.grandTotal).toBe(0);
    });

    it('puts the order-discount remainder on the last line whose net is not zero', () => {
        const r = calculateOrder({ lines: [line(1, 3.33), line(1, 3.33), line(1, 5, 'AMOUNT', 5)], orderDiscountType: 'AMOUNT', orderDiscountValue: 1, taxPercent: 0, taxInclusive: false });
        expect(r.lines.map(l => l.allocatedDiscount)).toEqual([0.5, 0.5, 0]);
    });

    describe('E8 tenders (grand 27.37 = E3)', () => {
        const t = (method: Tender['method'], amount: number, changeGiven = 0): Tender => ({ localId: method + amount, method, amount, changeGiven });
        it('accepts CASH 30.00 with change 2.63', () => {
            const r = addTender(27.37, [], 'CASH', 30, 'a') as { tenders: Tender[] };
            expect(r.tenders[0].changeGiven).toBe(2.63);
            expect(tenderError(27.37, r.tenders)).toBeNull();
        });
        it('refuses CARD 30.00 with the server wording', () => {
            expect(tenderError(27.37, [t('CARD', 30)])).toBe('A CARD payment of 30.00 exceeds the remaining balance of 27.37.');
            expect(addTender(27.37, [], 'CARD', 30, 'a')).toEqual({ error: 'A CARD payment of 30.00 exceeds the remaining balance of 27.37.' });
        });
        it('accepts CASH 20.00 + ON_ACCOUNT 7.37', () => {
            expect(tenderError(27.37, [t('CASH', 20), t('ON_ACCOUNT', 7.37)])).toBeNull();
        });
        it('accepts CASH 27.36 (±0.01) and refuses CASH 27.35', () => {
            expect(tenderError(27.37, [t('CASH', 27.36)])).toBeNull();
            expect(tenderError(27.37, [t('CASH', 27.35)])).toBe('Payments (27.35) do not match the order total (27.37).');
        });
        it('refuses change on a card', () => {
            expect(tenderError(27.37, [t('CARD', 30, 2.63)])).toBe('Only cash payments can give change.');
        });
    });
});

describe('A38 §1 totals calculator', () => {
    describe('rounding (2 dp, midpoint away from zero)', () => {
        it('rounds x.xx5 away from zero despite binary floating point', () => {
            expect(round2(1.005)).toBe(1.01);
            expect(round2(2.675)).toBe(2.68);
            expect(round2(-1.005)).toBe(-1.01);
            expect(round2(0.125)).toBe(0.13);
            expect(round2(10)).toBe(10);
        });

        it('divRound rounds half away from zero on integers', () => {
            expect(divRound(5, 2)).toBe(3);
            expect(divRound(-5, 2)).toBe(-3);
            expect(divRound(4, 3)).toBe(1);
            expect(divRound(5, 3)).toBe(2);
        });
    });

    describe('exclusive store', () => {
        it('computes gross, percent line discount (5.025 → 5.03), tax per line and the grand total', () => {
            const r = calculateOrder({ lines: [line(2, 100), line(1, 50.25, 'PERCENT', 10)], orderDiscountType: 'NONE', orderDiscountValue: 0, taxPercent: 17, taxInclusive: false });
            expect(r.lines[0]).toEqual(jasmine.objectContaining({ gross: 200, lineDiscount: 0, net: 200, taxAmount: 34, netAmount: 200, lineTotal: 234 }));
            expect(r.lines[1]).toEqual(jasmine.objectContaining({ gross: 50.25, lineDiscount: 5.03, net: 45.22, taxAmount: 7.69, netAmount: 45.22, lineTotal: 52.91 }));
            expect(r.subtotal).toBe(245.22);
            expect(r.discountAmount).toBe(0);
            expect(r.taxTotal).toBe(41.69);
            expect(r.grandTotal).toBe(286.91);
        });

        it('caps an AMOUNT line discount at the line gross', () => {
            const r = calculateOrder({ lines: [line(1, 10, 'AMOUNT', 15)], orderDiscountType: 'NONE', orderDiscountValue: 0, taxPercent: 17, taxInclusive: false });
            expect(r.lines[0].lineDiscount).toBe(10);
            expect(r.lines[0].net).toBe(0);
            expect(r.grandTotal).toBe(0);
        });

        it('allocates the order discount proportionally to net with the remainder on the last line', () => {
            const r = calculateOrder({
                lines: [line(1, 100), line(1, 200), line(1, 33.33)],
                orderDiscountType: 'PERCENT',
                orderDiscountValue: 10,
                taxPercent: 0,
                taxInclusive: false
            });
            expect(r.subtotal).toBe(333.33);
            expect(r.discountAmount).toBe(33.33);
            expect(r.lines.map(l => l.allocatedDiscount)).toEqual([10, 20, 3.33]);
            expect(r.lines.map(l => l.netAfter)).toEqual([90, 180, 30]);
            expect(r.lines.map(l => l.discountAmount)).toEqual([10, 20, 3.33]);
            expect(r.grandTotal).toBe(300);
        });

        it('taxes each line after its share of the order discount', () => {
            const r = calculateOrder({ lines: [line(1, 100), line(1, 50)], orderDiscountType: 'AMOUNT', orderDiscountValue: 15, taxPercent: 17, taxInclusive: false });
            // netAfter 90 / 45 → tax 15.30 / 7.65
            expect(r.lines.map(l => l.taxAmount)).toEqual([15.3, 7.65]);
            expect(r.taxTotal).toBe(22.95);
            expect(r.grandTotal).toBe(157.95); // 150 − 15 + 22.95
        });

        it('caps an AMOUNT order discount at the subtotal', () => {
            const r = calculateOrder({ lines: [line(1, 50)], orderDiscountType: 'AMOUNT', orderDiscountValue: 80, taxPercent: 17, taxInclusive: false });
            expect(r.discountAmount).toBe(50);
            expect(r.grandTotal).toBe(0);
        });

        it('rounds weighable quantities (3 dp) × price exactly', () => {
            const r = calculateOrder({ lines: [line(1.235, 3.99), line(0.5, 0.05)], orderDiscountType: 'NONE', orderDiscountValue: 0, taxPercent: 0, taxInclusive: false });
            expect(r.lines[0].gross).toBe(4.93); // 4.92765
            expect(r.lines[1].gross).toBe(0.03); // 0.025 → away from zero
            expect(r.grandTotal).toBe(4.96);
        });

        it('handles an empty cart and a zero rate', () => {
            const r = calculateOrder({ lines: [], orderDiscountType: 'PERCENT', orderDiscountValue: 10, taxPercent: 17, taxInclusive: false });
            expect(r).toEqual({ lines: [], subtotal: 0, discountAmount: 0, taxTotal: 0, grandTotal: 0 });
        });
    });

    describe('inclusive store', () => {
        it('back-solves the tax out of the shelf price; the total is the shelf price', () => {
            const r = calculateOrder({ lines: [line(1, 117)], orderDiscountType: 'NONE', orderDiscountValue: 0, taxPercent: 17, taxInclusive: true });
            expect(r.lines[0]).toEqual(jasmine.objectContaining({ taxAmount: 17, netAmount: 100, lineTotal: 117 }));
            expect(r.taxTotal).toBe(17);
            expect(r.grandTotal).toBe(117);
        });

        it('rounds the tax per line (round(netAfter − netAfter / (1 + rate)))', () => {
            const r = calculateOrder({ lines: [line(1, 10), line(1, 0.99)], orderDiscountType: 'NONE', orderDiscountValue: 0, taxPercent: 17, taxInclusive: true });
            expect(r.lines.map(l => l.taxAmount)).toEqual([1.45, 0.14]);
            expect(r.lines.map(l => l.netAmount)).toEqual([8.55, 0.85]);
            expect(r.lines.map(l => l.lineTotal)).toEqual([10, 0.99]);
            expect(r.taxTotal).toBe(1.59);
            expect(r.grandTotal).toBe(10.99);
        });

        it('applies the order discount before the inclusive tax; taxTotal is informational', () => {
            const r = calculateOrder({ lines: [line(1, 100), line(1, 50)], orderDiscountType: 'AMOUNT', orderDiscountValue: 15, taxPercent: 17, taxInclusive: true });
            expect(r.lines.map(l => l.netAfter)).toEqual([90, 45]);
            expect(r.lines.map(l => l.taxAmount)).toEqual([13.08, 6.54]);
            expect(r.taxTotal).toBe(19.62);
            expect(r.grandTotal).toBe(135);
        });

        it('supports fractional rates', () => {
            const r = calculateOrder({ lines: [line(1, 100)], orderDiscountType: 'NONE', orderDiscountValue: 0, taxPercent: 12.5, taxInclusive: true });
            expect(r.lines[0].taxAmount).toBe(11.11); // 100 × 12.5 / 112.5 = 11.111
        });
    });

    describe('tenders (§1.5)', () => {
        let n = 0;
        const id = () => `t${++n}`;
        const total = 286.91;

        it('gives change only for cash', () => {
            const r = addTender(total, [], 'CASH', 300, id());
            expect('tenders' in r).toBeTrue();
            const tenders = (r as { tenders: Tender[] }).tenders;
            expect(tenders[0].changeGiven).toBe(13.09);
            expect(changeTotal(tenders)).toBe(13.09);
            expect(tenderedNet(tenders)).toBe(286.91);
            expect(isFullyTendered(total, tenders)).toBeTrue();
        });

        it('refuses a non-cash tender above the remaining balance (POS-PAY-03)', () => {
            const r = addTender(total, [], 'CARD', 300, id());
            expect(r).toEqual({ error: 'A CARD payment of 300.00 exceeds the remaining balance of 286.91.' });
        });

        it('splits card + cash and ON_ACCOUNT covers exactly what remains', () => {
            const card = addTender(total, [], 'CARD', 100, id(), 'AUTH-1') as { tenders: Tender[] };
            expect(remainingDue(total, card.tenders)).toBe(186.91);
            const credit = addTender(total, card.tenders, 'ON_ACCOUNT', 5, id()) as { tenders: Tender[] };
            expect(credit.tenders[1]).toEqual(jasmine.objectContaining({ method: 'ON_ACCOUNT', amount: 186.91, changeGiven: 0 }));
            expect(creditAmount(credit.tenders)).toBe(186.91);
            expect(isFullyTendered(total, credit.tenders)).toBeTrue();
            expect(addTender(total, credit.tenders, 'CASH', 1, id())).toEqual({ error: 'The order is already fully paid.' });
        });

        it('refuses zero amounts', () => {
            expect(addTender(total, [], 'CASH', 0, id())).toEqual({ error: 'Enter an amount greater than zero.' });
        });

        it('accepts a ±0.01 difference as balanced', () => {
            const t = (amount: number): Tender[] => [{ localId: 'x', method: 'CARD', amount, changeGiven: 0 }];
            expect(isFullyTendered(total, t(286.9))).toBeTrue();
            expect(isFullyTendered(total, t(286.89))).toBeFalse();
        });

        it('suggests only multiples of 50 that are greater than the amount due', () => {
            expect(quickAmounts(286.91)).toEqual([300, 500, 1000, 5000]);
            expect(quickAmounts(7)).toEqual([50, 100, 500, 1000, 5000]);
            expect(quickAmounts(0)).toEqual([]);
            for (const due of [0.01, 49.99, 50, 120.5, 286.91, 300, 999.99, 1000, 6500, 98000]) {
                for (const q of quickAmounts(due)) {
                    expect(q % 50).toBe(0);
                    expect(q).toBeGreaterThan(due);
                }
            }
        });

        it('moves to the next multiple when the amount due is already one', () => {
            expect(quickAmounts(300)).toEqual([350, 400, 500, 1000, 5000]);
            expect(quickAmounts(7000)).toEqual([7050, 7100, 7500, 8000, 10000]);
        });
    });
});
