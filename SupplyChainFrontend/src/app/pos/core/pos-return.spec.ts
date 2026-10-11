import { canReturn, canVoid, refundMethods, returnLineAmount, returnTotal, returnedAmounts } from './pos-return';
import { cashVariance } from '../ui/pos-shift-close.component';
import { LocalOrder, LocalReturn, PosOrderLine } from './pos.models';
import { filterLocal } from '../ui/pos-history.component';

const l = (uuid: string, quantity: number, lineTotal: number, returnableQuantity = quantity) =>
    ({ uuid, quantity, lineTotal, returnableQuantity }) as PosOrderLine;

describe('A38 returns, void and close helpers', () => {
    it('E9: a 10.52 line returned in three parts refunds 3.51 / 3.51 / 3.50 (last units get what remains)', () => {
        expect(returnLineAmount(l('e2', 3, 10.52, 3), 1, 0)).toBe(3.51);
        expect(returnLineAmount(l('e2', 3, 10.52, 2), 1, 3.51)).toBe(3.51);
        expect(returnLineAmount(l('e2', 3, 10.52, 1), 1, 7.02)).toBe(3.5);
        expect(returnLineAmount(l('e2', 3, 10.52, 3), 3, 0)).toBe(10.52);
    });

    it('2 then the last 1 also adds up to the line total', () => {
        expect(returnLineAmount(l('e2', 3, 10.52, 3), 2, 0)).toBe(7.01);
        expect(returnLineAmount(l('e2', 3, 10.52, 1), 1, 7.01)).toBe(3.51);
    });

    it('sums lines and reads earlier returns per original line', () => {
        expect(returnLineAmount(l('a', 3, 100), 0)).toBe(0);
        expect(returnTotal([l('a', 3, 100), l('b', 1, 52.91)], { a: 1, b: 1 })).toBe(86.24);
        const prior = returnedAmounts([
            { lines: [{ originalLineUuid: 'e2', lineTotal: 3.51 } as PosOrderLine] },
            { lines: [{ originalLineUuid: 'e2', lineTotal: -3.51 } as PosOrderLine, { originalLineUuid: null, lineTotal: 9 } as PosOrderLine] }
        ]);
        expect(prior).toEqual({ e2: 7.02 });
        expect(returnTotal([l('e2', 3, 10.52, 1)], { e2: 1 }, prior)).toBe(3.5);
    });

    it('offers ON_ACCOUNT refunds only for credit sales', () => {
        const all = ['CASH', 'CARD', 'ON_ACCOUNT'] as const;
        expect(refundMethods({ paymentStatus: 'PAID' }, [...all])).toEqual(['CASH', 'CARD']);
        expect(refundMethods({ paymentStatus: 'PARTIAL_CREDIT' }, [...all])).toEqual([...all]);
    });

    it('voids only completed sales of the current shift', () => {
        expect(canVoid({ orderType: 'SALE', status: 'COMPLETED', shiftUuid: 's1' }, 's1')).toBeTrue();
        expect(canVoid({ orderType: 'SALE', status: 'COMPLETED', shiftUuid: 's1' }, 's2')).toBeFalse();
        expect(canVoid({ orderType: 'SALE', status: 'PARTIALLY_RETURNED', shiftUuid: 's1' }, 's1')).toBeFalse();
        expect(canVoid({ orderType: 'RETURN', status: 'COMPLETED', shiftUuid: 's1' }, 's1')).toBeFalse();
    });

    it('returns while something is returnable', () => {
        expect(canReturn({ orderType: 'SALE', status: 'COMPLETED', lines: [l('a', 1, 10)] })).toBeTrue();
        expect(canReturn({ orderType: 'SALE', status: 'PARTIALLY_RETURNED', lines: [l('a', 2, 10, 0)] })).toBeFalse();
        expect(canReturn({ orderType: 'SALE', status: 'VOIDED', lines: [l('a', 1, 10)] })).toBeFalse();
    });

    it('A39 history filters split device sales and returns / exchanges', () => {
        const sales = [{ localId: 's' }] as LocalOrder[];
        const rets = [{ localId: 'r', kind: 'RETURN' }, { localId: 'e', kind: 'EXCHANGE' }] as LocalReturn[];
        expect(filterLocal('ALL', sales, rets)).toEqual({ sales, returns: rets });
        expect(filterLocal('SALE', sales, rets)).toEqual({ sales, returns: [] });
        expect(filterLocal('RETURN', sales, rets).returns.map(r => r.localId)).toEqual(['r']);
        expect(filterLocal('EXCHANGE', sales, rets)).toEqual({ sales: [], returns: [rets[1]] });
    });

    it('computes the live close variance', () => {
        expect(cashVariance(1050.5, 1000)).toBe(50.5);
        expect(cashVariance(990, 1000)).toBe(-10);
        expect(cashVariance(null, 1000)).toBeNull();
    });
});
