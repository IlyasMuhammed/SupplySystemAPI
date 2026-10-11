import {
    MSG,
    ReturnableLine,
    allowedRefundMethods,
    daysSince,
    defaultRefundMethod,
    effectiveFeePct,
    exchangeBlock,
    exchangeNet,
    fromLocalOrder,
    fromLookup,
    lineReturnAmount,
    managerReasons,
    managerReasonsFromError,
    offlineCreditNumber,
    orderReturnBlock,
    originalRefundMethod,
    qualifiesAsManager,
    reasonsFor,
    restockingFee,
    returnSummary,
    storeCodeOfReceipt,
    storeCreditTender,
    windowView
} from './pos-return-rules';
import { policy, reason } from './pos-testing';
import { LocalOrder, LocalReturn, StoreCredit } from './pos.models';

const line = (key: string, quantity: number, lineTotal: number, returnable = quantity, refundIfAll = lineTotal): ReturnableLine => ({
    key,
    lineUuid: key,
    localLineId: null,
    lineNumber: 1,
    variantUuid: `v-${key}`,
    productName: `Item ${key}`,
    sku: key.toUpperCase(),
    quantity,
    returnedQuantity: quantity - returnable,
    returnableQuantity: returnable,
    unitPrice: 0,
    taxPercent: 17,
    lineTotal,
    refundIfAllReturned: refundIfAll
});

describe('A39 return rules', () => {
    describe('restocking fee and refund (D-5)', () => {
        it('rounds the fee half away from zero on the tax-inclusive total', () => {
            expect(restockingFee(100, 15)).toBe(15);
            expect(restockingFee(10.52, 15)).toBe(1.58);
            expect(restockingFee(11.7, 15)).toBe(1.76); // POSR's example: refund 9.94
            expect(restockingFee(33.33, 12.5)).toBe(4.17);
            expect(restockingFee(0.1, 25)).toBe(0.03);
            expect(restockingFee(100, 0)).toBe(0);
            expect(restockingFee(0, 10)).toBe(0);
        });

        it("the reason's override wins over the policy; 0 waives the fee", () => {
            expect(effectiveFeePct(policy({ restockingFeePct: 10 }), reason())).toBe(10);
            expect(effectiveFeePct(policy({ restockingFeePct: 10 }), reason({ restockingFeeOverride: 5 }))).toBe(5);
            expect(effectiveFeePct(policy({ restockingFeePct: 10 }), reason({ restockingFeeOverride: 0 }))).toBe(0);
            expect(effectiveFeePct(null, null)).toBe(0);
        });

        it('summary = Σ line shares (remainder rule) − fee', () => {
            const lines = [line('a', 3, 10.52), line('b', 2, 234)];
            const qty = { a: 1, b: 2 };
            expect(returnSummary(lines, qty, policy({ restockingFeePct: 10 }), reason())).toEqual({ subtotal: 237.51, feePct: 10, fee: 23.75, refund: 213.76, itemCount: 3 });
            expect(returnSummary(lines, qty, policy({ restockingFeePct: 10 }), reason({ restockingFeeOverride: 0 })).refund).toBe(237.51);
            expect(returnSummary(lines, qty, policy({ restockingFeePct: 10 }), reason({ restockingFeeOverride: 5 }))).toEqual(jasmine.objectContaining({ fee: 11.88, refund: 225.63 }));
        });

        it('E9: the last remaining units refund exactly what is left of the line', () => {
            expect(lineReturnAmount(line('e2', 3, 10.52, 3, 10.52), 1)).toBe(3.51);
            expect(lineReturnAmount(line('e2', 3, 10.52, 2, 7.01), 1)).toBe(3.51);
            expect(lineReturnAmount(line('e2', 3, 10.52, 1, 3.5), 1)).toBe(3.5);
            expect(lineReturnAmount(line('e2', 3, 10.52, 2, 7.01), 2)).toBe(7.01);
            expect(lineReturnAmount(line('e2', 3, 10.52), 0)).toBe(0);
        });
    });

    describe('refund methods by policy', () => {
        const payments = [
            { method: 'CASH', amount: 50 },
            { method: 'CARD', amount: 30 },
            { method: 'MOBILE', amount: 40 }
        ];
        const enabled = ['CASH', 'CARD', 'MOBILE', 'ON_ACCOUNT'] as const;

        it('original method = the largest non-cash tender, else CASH', () => {
            expect(originalRefundMethod(payments)).toBe('MOBILE');
            expect(originalRefundMethod([{ method: 'CASH', amount: 10 }])).toBe('CASH');
            expect(originalRefundMethod([{ method: 'EXCHANGE', amount: 99 }, { method: 'CARD', amount: 1 }])).toBe('CARD');
        });

        it('refundToOriginalMethod: only the original method (+ store credit when allowed)', () => {
            const p = policy({ refundToOriginalMethod: true });
            expect(allowedRefundMethods({ policy: p, payments, enabled: [...enabled], creditNotesEnabled: true })).toEqual(['MOBILE', 'STORE_CREDIT']);
            expect(allowedRefundMethods({ policy: p, payments, enabled: [...enabled], creditNotesEnabled: false })).toEqual(['MOBILE']);
            // a cash sale under a no-cash policy can only become store credit
            expect(allowedRefundMethods({ policy: { ...p, allowCashRefund: false }, payments: [{ method: 'CASH', amount: 100 }], enabled: [...enabled], creditNotesEnabled: true })).toEqual(['STORE_CREDIT']);
        });

        it('otherwise the terminal methods: cash only when allowed, on account only for credit sales', () => {
            expect(allowedRefundMethods({ policy: policy({ allowCashRefund: false }), payments, enabled: [...enabled], creditNotesEnabled: true })).toEqual(['CARD', 'MOBILE', 'STORE_CREDIT']);
            expect(allowedRefundMethods({ policy: policy({ allowCreditNoteRefund: false }), payments: [{ method: 'ON_ACCOUNT', amount: 100 }], enabled: [...enabled], creditNotesEnabled: true })).toEqual([
                'CASH',
                'CARD',
                'MOBILE',
                'ON_ACCOUNT'
            ]);
            expect(allowedRefundMethods({ policy: null, payments, enabled: [...enabled], creditNotesEnabled: true })).toEqual([]);
        });

        it('preselects the original method when allowed', () => {
            expect(defaultRefundMethod(['CASH', 'MOBILE', 'STORE_CREDIT'], payments)).toBe('MOBILE');
            expect(defaultRefundMethod(['STORE_CREDIT'], payments)).toBe('STORE_CREDIT');
            expect(defaultRefundMethod([], payments)).toBeNull();
        });
    });

    describe('windows', () => {
        it('return window and exchange window (exchange ?? return days, 0 = no exchanges)', () => {
            const v = windowView(policy({ returnWindowDays: 30 }), 10);
            expect(v).toEqual(jasmine.objectContaining({ withinReturnWindow: true, withinExchangeWindow: true, exchangesBlocked: false, windowDaysRemaining: 20 }));
            const late = windowView(policy({ returnWindowDays: 30 }), 31);
            expect(late.withinReturnWindow).toBeFalse();
            expect(late.withinExchangeWindow).toBeFalse();
            expect(windowView(policy({ returnWindowDays: 30, exchangeWindowDays: 45 }), 40).withinExchangeWindow).toBeTrue();
            const noEx = windowView(policy({ exchangeWindowDays: 0 }), 0);
            expect(noEx.exchangesBlocked).toBeTrue();
            expect(noEx.withinExchangeWindow).toBeFalse();
            expect(exchangeBlock(noEx)).toBe(MSG.noExchanges);
            expect(windowView(policy({ returnWindowDays: null }), 999).withinReturnWindow).toBeTrue();
            expect(exchangeBlock(windowView(null, 1))).toBe(MSG.noPolicy);
        });

        it('counts full days since the purchase', () => {
            const now = Date.parse('2026-10-11T12:00:00Z');
            expect(daysSince('2026-10-11T08:00:00Z', now)).toBe(0);
            expect(daysSince('2026-10-01T11:00:00Z', now)).toBe(10);
        });
    });

    describe('manager authorization', () => {
        const view = (o: Partial<ReturnType<typeof windowView>> = {}) => ({ ...windowView(policy({ maxReturnNoAuth: 500 }), 1), ...o });

        it('lists every reason a manager is needed', () => {
            expect(managerReasons({ reason: reason(), amount: 500, policy: view(), kind: 'RETURN', crossStore: false })).toEqual([]);
            expect(managerReasons({ reason: reason({ requiresManager: true }), amount: 500.01, policy: view({ withinReturnWindow: false }), kind: 'RETURN', crossStore: false })).toEqual(['REASON', 'AMOUNT', 'WINDOW']);
            expect(managerReasons({ reason: reason(), amount: 1, policy: view({ requiresManagerForCrossStore: true }), kind: 'RETURN', crossStore: true })).toEqual(['CROSS_STORE']);
            // exchanges check the exchange window
            expect(managerReasons({ reason: reason(), amount: 1, policy: view({ withinReturnWindow: false, withinExchangeWindow: true }), kind: 'EXCHANGE', crossStore: false })).toEqual([]);
        });

        it('offline: only a supervisor with the override permission (cross-store permission for cross-store only)', () => {
            expect(qualifiesAsManager({ posRole: 'SUPERVISOR', permissions: ['POS_RETURN_OVERRIDE'] }, ['AMOUNT'])).toBeTrue();
            expect(qualifiesAsManager({ posRole: 'CASHIER', permissions: ['POS_RETURN_OVERRIDE'] }, ['AMOUNT'])).toBeFalse();
            expect(qualifiesAsManager({ posRole: 'SUPERVISOR', permissions: ['POS_RETURN_CROSS_STORE'] }, ['CROSS_STORE'])).toBeTrue();
            expect(qualifiesAsManager({ posRole: 'SUPERVISOR', permissions: ['POS_RETURN_CROSS_STORE'] }, ['CROSS_STORE', 'WINDOW'])).toBeFalse();
        });

        it("reads the server's requiresManager answer", () => {
            expect(managerReasonsFromError({ success: false, message: MSG.managerRequired, result: { requiresManager: true, reasons: ['AMOUNT'] } })).toEqual(['AMOUNT']);
            expect(managerReasonsFromError({ success: false, message: 'Return window expired (30 days).', result: { requiresManager: true } })).toEqual(['WINDOW']);
            expect(managerReasonsFromError({ success: false, message: 'x' })).toBeNull();
        });
    });

    describe('exchange net settlement (D-6)', () => {
        it('PAY / EVEN / REFUND', () => {
            expect(exchangeNet(150, 100)).toEqual({ net: 50, settlement: 'PAY' });
            expect(exchangeNet(100, 100)).toEqual({ net: 0, settlement: 'EVEN' });
            expect(exchangeNet(80, 100.01)).toEqual({ net: -20.01, settlement: 'REFUND' });
        });
    });

    describe('store credit tender (D-2)', () => {
        const note = (o: Partial<StoreCredit> = {}): StoreCredit => ({
            uuid: 'cn-1',
            number: 'CN-S1-20261011-0001',
            customerUuid: null,
            originalAmount: 100,
            remainingBalance: 100,
            redeemedAmount: 0,
            currencyCode: 'PKR',
            status: 'ACTIVE',
            issuedAtStoreName: 'Main',
            issuedByName: 'Sara',
            issuedAt: '2026-10-01T00:00:00Z',
            expiresAt: null,
            modifiedAt: '',
            ...o
        });
        const now = Date.parse('2026-10-11T00:00:00Z');

        it('is capped at min(balance, remaining) and gives no change', () => {
            expect(storeCreditTender({ note: note(), online: true, orderCustomerUuid: null, due: 60, now })).toEqual({ amount: 60 });
            expect(storeCreditTender({ note: note(), online: true, orderCustomerUuid: null, due: 150, now })).toEqual({ amount: 100 });
            expect(storeCreditTender({ note: note(), online: true, orderCustomerUuid: null, due: 150, used: 70, now })).toEqual({ amount: 30 });
            expect(storeCreditTender({ note: note(), online: true, orderCustomerUuid: null, due: 150, requested: 120, now })).toEqual({ error: jasmine.stringContaining('at most 100.00') } as never);
            expect(storeCreditTender({ note: note(), online: true, orderCustomerUuid: null, due: 150, requested: 40, now })).toEqual({ amount: 40 });
        });

        it('is refused offline, expired, used up or for another customer', () => {
            expect(storeCreditTender({ note: note(), online: false, orderCustomerUuid: null, due: 50, now })).toEqual({ error: MSG.creditOffline });
            expect(storeCreditTender({ note: note({ expiresAt: '2026-10-10T00:00:00Z' }), online: true, orderCustomerUuid: null, due: 50, now })).toEqual({ error: MSG.creditExpired });
            expect(storeCreditTender({ note: note({ status: 'EXPIRED' }), online: true, orderCustomerUuid: null, due: 50, now })).toEqual({ error: MSG.creditExpired });
            expect(storeCreditTender({ note: note({ status: 'FULLY_REDEEMED', remainingBalance: 0 }), online: true, orderCustomerUuid: null, due: 50, now })).toEqual({ error: MSG.creditNoBalance });
            expect(storeCreditTender({ note: note({ status: 'CANCELLED' }), online: true, orderCustomerUuid: null, due: 50, now })).toEqual({ error: MSG.creditCancelled });
            expect(storeCreditTender({ note: note(), online: true, orderCustomerUuid: null, due: 50, used: 100, now })).toEqual({ error: MSG.creditNoBalance });
            expect(storeCreditTender({ note: note({ customerUuid: 'c-1' }), online: true, orderCustomerUuid: 'c-2', due: 50, now })).toEqual({ error: MSG.creditOtherCustomer });
            expect(storeCreditTender({ note: note({ customerUuid: 'c-1' }), online: true, orderCustomerUuid: 'c-1', due: 50, now })).toEqual({ amount: 50 });
        });

        it('numbers offline-issued credit CN-{store}-OFF-{short}', () => {
            expect(offlineCreditNumber('S1', 'ab12cd34-0000-4000-8000-000000000000')).toBe('CN-S1-OFF-AB12CD34');
        });
    });

    describe('orders and reasons', () => {
        it('refuses voided, non-sale and fully returned orders with the contract wording', () => {
            const lines = [line('a', 1, 10)];
            expect(orderReturnBlock({ status: 'VOIDED', orderType: 'SALE', lines })).toBe(MSG.voided);
            expect(orderReturnBlock({ status: 'COMPLETED', orderType: 'RETURN', lines })).toBe(MSG.onlySales);
            expect(orderReturnBlock({ status: 'RETURNED', orderType: 'SALE', lines: [line('a', 1, 10, 0)] })).toBe(MSG.allReturned);
            expect(orderReturnBlock({ status: 'PARTIALLY_RETURNED', orderType: 'SALE', lines })).toBeNull();
        });

        it('reads the store code of a receipt number', () => {
            expect(storeCodeOfReceipt('POS-S2-20261011-0001')).toBe('S2');
            expect(storeCodeOfReceipt('pos-s1-off-ab12cd34')).toBe('S1');
            expect(storeCodeOfReceipt('12345')).toBeNull();
        });

        it('offers active reasons that apply, by sort order', () => {
            const all = [
                reason({ uuid: '1', name: 'B', sortOrder: 2 }),
                reason({ uuid: '2', name: 'A', sortOrder: 1, appliesTo: 'EXCHANGE' }),
                reason({ uuid: '3', name: 'C', sortOrder: 3, appliesTo: 'RETURN' }),
                reason({ uuid: '4', name: 'D', sortOrder: 0, isActive: false })
            ];
            expect(reasonsFor(all, 'RETURN').map(r => r.uuid)).toEqual(['1', '3']);
            expect(reasonsFor(all, 'EXCHANGE').map(r => r.uuid)).toEqual(['2', '1']);
        });

        it('maps the server lookup and its policy', () => {
            const res = fromLookup({
                found: true,
                order: {
                    uuid: 'o-1', orderNumber: 'POS-S2-20261001-0004', storeUuid: 'store-2', storeName: 'Mall', storeCode: 'S2', createdAt: '2026-10-01T10:00:00Z', grandTotal: 117,
                    customerUuid: 'c-1', customerName: 'Ali', isWalkIn: false, status: 'COMPLETED', orderType: 'SALE', isSameStore: false, daysSincePurchase: 10,
                    payments: [{ method: 'CARD', amount: 117 }],
                    lines: [{ lineUuid: 'l-1', variantUuid: 'v-1', productName: 'Tea', sku: 'T', quantity: 1, returnedQuantity: 0, returnableQuantity: 1, unitPrice: 100, taxPercent: 17, lineTotal: 117, refundIfAllReturned: 117 }]
                },
                policy: { ...policy({ exchangeWindowDays: 0 }), withinReturnWindow: true, withinExchangeWindow: false, windowDaysRemaining: 20, crossStoreAllowed: true, requiresManagerForCrossStore: true }
            })!;
            expect(res.order.lines[0].key).toBe('l-1');
            expect(res.order.isSameStore).toBeFalse();
            expect(res.policy).toEqual(jasmine.objectContaining({ crossStoreAllowed: true, requiresManagerForCrossStore: true, exchangesBlocked: true, windowDaysRemaining: 20 }));
            expect(fromLookup({ found: false })).toBeNull();
        });

        it('builds an offline returnable order from a device sale and its earlier device returns', () => {
            const sale = {
                localId: 'sale-1',
                syncStatus: 'pending',
                createdAtLocal: '2026-10-09T10:00:00Z',
                shiftUuid: 'sh-1',
                payload: { localId: 'sale-1', customerUuid: null, offlineNumber: 'POS-S1-OFF-AAAA0000', lines: [{ localLineId: 'll-a', variantUuid: 'v-a', quantity: 3, unitPrice: 3.33 }, { localLineId: 'll-b', variantUuid: 'v-b', quantity: 1, unitPrice: 50 }] },
                receipt: {
                    storeName: 'Main', storeCode: 'S1', customerName: 'Walk-in customer', taxPercent: 17, grandTotal: 69.02,
                    lines: [{ name: 'Tea', sku: 'T', quantity: 3, lineTotal: 10.52 }, { name: 'Mug', sku: 'M', quantity: 1, lineTotal: 58.5 }],
                    payments: [{ method: 'CASH', amount: 100, changeGiven: 30.98 }]
                }
            } as unknown as LocalOrder;
            const prior = [{ syncStatus: 'pending', lines: [{ localLineId: 'll-a', quantity: 1, amount: 3.51 }] }] as unknown as LocalReturn[];
            const { order, policy: view } = fromLocalOrder(sale, prior, policy({ returnWindowDays: 1 }), 'store-1', Date.parse('2026-10-11T12:00:00Z'));
            expect(order.source).toBe('local');
            expect(order.orderNumber).toBe('POS-S1-OFF-AAAA0000');
            expect(order.isWalkIn).toBeTrue();
            expect(order.status).toBe('PARTIALLY_RETURNED');
            expect(order.payments).toEqual([{ method: 'CASH', amount: 69.02, reference: null }]);
            expect(order.lines[0]).toEqual(jasmine.objectContaining({ key: 'll-a', returnableQuantity: 2, refundIfAllReturned: 7.01 }));
            expect(lineReturnAmount(order.lines[0], 2)).toBe(7.01);
            expect(view.withinReturnWindow).toBeFalse(); // 2 days > 1-day window
            expect(view.crossStoreAllowed).toBeFalse();
        });
    });
});
