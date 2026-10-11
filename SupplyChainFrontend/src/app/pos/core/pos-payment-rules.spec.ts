import { basicCreditCheck, isWalkIn, offlineCreditCheck, onlineCreditCheck } from './pos-payment-rules';
import { customer } from './pos-testing';

describe('A38 ON_ACCOUNT rules', () => {
    it('recognises the walk-in customer', () => {
        expect(isWalkIn(null)).toBeTrue();
        expect(isWalkIn(customer({ isSystem: true }))).toBeTrue();
        expect(isWalkIn(customer({ code: 'c-walkin' }))).toBeTrue();
        expect(isWalkIn(customer())).toBeFalse();
    });

    describe('basic', () => {
        it('needs the feature, a named customer and allowPosCredit', () => {
            expect(basicCreditCheck(customer(), false).message).toBe('Credit sales are not enabled.');
            expect(basicCreditCheck(null, true).ok).toBeFalse();
            expect(basicCreditCheck(customer({ isSystem: true }), true).message).toBe('Walk-In customers cannot use credit.');
            expect(basicCreditCheck(customer({ allowPosCredit: false }), true).ok).toBeFalse();
            expect(basicCreditCheck(customer({ requiresIdForCredit: true }), true)).toEqual({ ok: true, requiresId: true });
        });
    });

    describe('online (live credit endpoint)', () => {
        const credit = { balance: 700, creditLimit: 1000, available: 300, allowPosCredit: true, maxSingleCreditOrder: null, requiresIdForCredit: false, isWalkIn: false };

        it('allows up to the available credit', () => {
            expect(onlineCreditCheck(credit, 300).ok).toBeTrue();
        });

        it('refuses above it with the server wording', () => {
            expect(onlineCreditCheck(credit, 300.01).message).toBe('Credit limit exceeded. Limit: 1000.00. Balance: 700.00. This order: 300.01.');
        });

        it('applies the single-order maximum and walk-in rule', () => {
            expect(onlineCreditCheck({ ...credit, maxSingleCreditOrder: 200 }, 250).ok).toBeFalse();
            expect(onlineCreditCheck({ ...credit, isWalkIn: true }, 10).message).toBe('Walk-In customers cannot use credit.');
        });
    });

    describe('offline tolerance (cached balance, store %)', () => {
        it('allows within the limit', () => {
            expect(offlineCreditCheck(customer({ balance: 600, creditLimit: 1000 }), 400, 10)).toEqual({ ok: true, overLimit: false, requiresId: false });
        });

        it('allows over the limit within the tolerance and flags it', () => {
            const d = offlineCreditCheck(customer({ balance: 600, creditLimit: 1000 }), 500, 10); // 1100 ≤ 1100
            expect(d.ok).toBeTrue();
            expect(d.overLimit).toBeTrue();
        });

        it('refuses beyond the tolerance', () => {
            const d = offlineCreditCheck(customer({ balance: 600, creditLimit: 1000 }), 500.01, 10);
            expect(d.ok).toBeFalse();
            expect(d.message).toContain('Credit limit exceeded (offline)');
        });

        it('uses the offline limit override when set', () => {
            expect(offlineCreditCheck(customer({ balance: 0, creditLimit: 1000, offlineCreditLimitOverride: 200 }), 250, 0).ok).toBeFalse();
            expect(offlineCreditCheck(customer({ balance: 0, creditLimit: 1000, offlineCreditLimitOverride: 200 }), 200, 0).ok).toBeTrue();
        });

        it('refuses credit offline without a limit, and for walk-ins', () => {
            expect(offlineCreditCheck(customer({ creditLimit: 0 }), 1, 50).ok).toBeFalse();
            expect(offlineCreditCheck(customer({ isSystem: true }), 1, 50).ok).toBeFalse();
        });

        it('respects maxSingleCreditOrder', () => {
            expect(offlineCreditCheck(customer({ maxSingleCreditOrder: 100 }), 150, 10).ok).toBeFalse();
        });
    });
});
