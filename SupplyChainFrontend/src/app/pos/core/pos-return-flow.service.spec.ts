import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { PosAuth } from './pos-auth.service';
import { PosConnectivity } from './pos-connectivity.service';
import { PosDb } from './pos-db';
import { createIdentityV3Hash } from './pos-pin';
import { ManagerRequiredError, PosReturnFlow, ReturnRefusedError } from './pos-return-flow.service';
import { MSG } from './pos-return-rules';
import { PosState } from './pos-state.service';
import { catalogItem, clearPosStorage, dropDb, fakeJwt, ok, policy, posTestProviders, reason, syncConfig, waitForRequest } from './pos-testing';
import { PosTokens } from './pos-tokens.service';
import { EffectivePolicy, LocalOrder, OrderLookup, Shift } from './pos.models';

function lookup(overrides: { isSameStore?: boolean; policy?: Partial<EffectivePolicy> & Record<string, unknown> } = {}): OrderLookup {
    return {
        found: true,
        order: {
            uuid: 'o-1',
            orderNumber: 'POS-S1-20261001-0001',
            storeUuid: 'store-1',
            storeName: 'Main Store',
            storeCode: 'S1',
            createdAt: '2026-10-09T10:00:00Z',
            grandTotal: 234,
            customerUuid: 'c-1',
            customerName: 'Ali Traders',
            isWalkIn: false,
            status: 'COMPLETED',
            orderType: 'SALE',
            isSameStore: overrides.isSameStore ?? true,
            daysSincePurchase: 2,
            payments: [{ method: 'CARD', amount: 234 }],
            lines: [{ lineUuid: 'l-1', variantUuid: 'v-1', productName: 'Tea', sku: 'TEA', quantity: 2, returnedQuantity: 0, returnableQuantity: 2, unitPrice: 100, taxPercent: 17, lineTotal: 234, refundIfAllReturned: 234 }]
        },
        policy: { ...policy(), withinReturnWindow: true, withinExchangeWindow: true, windowDaysRemaining: 28, crossStoreAllowed: true, requiresManagerForCrossStore: false, ...overrides.policy }
    };
}

function deviceSale(): LocalOrder {
    return {
        localId: 'sale-1',
        syncStatus: 'synced',
        orderUuid: 'o-9',
        orderNumber: 'POS-S1-20261009-0007',
        createdAtLocal: new Date(Date.now() - 2 * 86_400_000).toISOString(),
        shiftUuid: 'sh-0',
        attempts: 0,
        payload: { localId: 'sale-1', customerUuid: null, offlineNumber: 'POS-S1-OFF-SALE0001', lines: [{ localLineId: 'll-1', variantUuid: 'v-1', quantity: 2, unitPrice: 100, discountType: 'NONE', discountValue: 0 }], payments: [] },
        receipt: {
            storeName: 'Main Store', storeCode: 'S1', terminalName: 'Till 1', cashierName: 'Sara', customerName: 'Walk-in customer', currencyCode: 'PKR', taxInclusive: false, taxPercent: 17,
            lines: [{ name: 'Tea', sku: 'TEA', quantity: 2, unitPrice: 100, discountAmount: 0, lineTotal: 234 }], subtotal: 200, discountAmount: 0, taxTotal: 34, grandTotal: 234,
            payments: [{ method: 'CASH', amount: 234, changeGiven: 0 }], changeTotal: 0, creditAmount: 0
        }
    } as unknown as LocalOrder;
}

const settle = () => new Promise(r => setTimeout(r, 20));

describe('A39 PosReturnFlow (returns, exchanges, manager authorization)', () => {
    let flow: PosReturnFlow;
    let db: PosDb;
    let http: HttpTestingController;
    let conn: PosConnectivity;
    let state: PosState;

    beforeEach(async () => {
        clearPosStorage();
        TestBed.configureTestingModule({ providers: posTestProviders() });
        flow = TestBed.inject(PosReturnFlow);
        db = TestBed.inject(PosDb);
        http = TestBed.inject(HttpTestingController);
        conn = TestBed.inject(PosConnectivity);
        state = TestBed.inject(PosState);
        state.syncConfig.set(syncConfig());
        state.shift.set({ uuid: 'sh-1', status: 'OPEN', shiftNumber: 'SH-1' } as Shift);
        state.returnPolicy.set(policy());
        const tokens = TestBed.inject(PosTokens);
        tokens.setDevice({ accessToken: fakeJwt(), refreshToken: 'r' });
        tokens.setCashier({ accessToken: fakeJwt(), refreshToken: 'r' });
        const auth = TestBed.inject(PosAuth);
        auth.cashier.set({ userId: 7, name: 'Sara', posRole: 'SUPERVISOR', permissions: ['POS_ORDER_CREATE', 'POS_ORDER_RETURN'], signedInAt: '', mode: 'online' });
        auth.locked.set(false);
        await db.returnReasons.bulkPut([reason(), reason({ uuid: 'r-pm', code: 'PRICE_MATCH', name: 'Price match', requiresNote: true, requiresManager: true, sortOrder: 7 })]);
    });

    afterEach(async () => {
        http.verify();
        await dropDb(db);
        clearPosStorage();
    });

    async function beginServer(l = lookup()): Promise<void> {
        const p = flow.lookupReceipt('POS-S1-20261001-0001');
        const r = await waitForRequest(http, u => u.includes('/pos/orders/lookup'));
        expect(r.request.params.get('receipt')).toBe('POS-S1-20261001-0001');
        expect(r.request.params.get('storeUuid')).toBe('store-1');
        r.flush(ok(l));
        const found = await p;
        expect(flow.blockOf(found)).toBeNull();
        await flow.begin(found);
    }

    describe('lookup', () => {
        it('online: orders/lookup of the processing store', async () => {
            await beginServer();
            expect(flow.order()!.source).toBe('server');
            expect(flow.order()!.uuid).toBe('o-1');
            expect(flow.reasonOptions().length).toBe(2);
        });

        it('online but unknown to the server: falls back to a sale kept on this device', async () => {
            await db.orders.put({ ...deviceSale(), syncStatus: 'pending', orderUuid: null, orderNumber: null });
            const p = flow.lookupReceipt('POS-S1-OFF-SALE0001');
            (await waitForRequest(http, u => u.includes('/pos/orders/lookup'))).flush(ok({ found: false }));
            const found = await p;
            expect(found.order.source).toBe('local');
            expect(found.order.localId).toBe('sale-1');
        });

        it('offline: only sales on this device; another store’s receipt is cross-store', async () => {
            conn.setBrowser(false);
            await db.orders.put(deviceSale());
            const found = await flow.lookupReceipt('pos-s1-20261009-0007');
            expect(found.order.source).toBe('local');
            expect(found.order.uuid).toBe('o-9');
            await expectAsync(flow.lookupReceipt('POS-S2-20261009-0001')).toBeRejectedWithError(MSG.crossStoreOffline);
            await expectAsync(flow.lookupReceipt('POS-S1-20261009-0099')).toBeRejectedWithError(/not on this device/);
            http.expectNone(() => true);
        });

        it('blocks cross-store returns the processing store does not accept', async () => {
            const p = flow.lookupReceipt('x');
            (await waitForRequest(http, u => u.includes('/pos/orders/lookup'))).flush(ok(lookup({ isSameStore: false, policy: { crossStoreAllowed: false } })));
            expect(flow.blockOf(await p)).toBe(MSG.crossStoreRefused);
        });
    });

    describe('return', () => {
        it('online: manager token from returns/authorize goes with the return; the server order is stored as synced', async () => {
            await beginServer(lookup({ policy: { maxReturnNoAuth: 50, restockingFeePct: 10 } }));
            flow.setQty('l-1', 1);
            flow.reasonUuid.set('r-def');
            flow.refundMethod.set('CARD');
            expect(flow.summary()).toEqual(jasmine.objectContaining({ subtotal: 117, fee: 11.7, refund: 105.3 }));
            expect(flow.managerNeeded()).toEqual(['AMOUNT']);
            await expectAsync(flow.submitReturn()).toBeRejectedWith(jasmine.any(ManagerRequiredError));

            const a = flow.authorize('1234', ['AMOUNT']);
            const ar = await waitForRequest(http, u => u.endsWith('/pos/returns/authorize'));
            expect(ar.request.body).toEqual({ terminalUuid: 'term-1', originalOrderUuid: 'o-1', amount: 105.3, managerUserId: null, pin: '1234' });
            ar.flush(ok({ authorizationToken: 'tok-1', managerUserId: 9, managerName: 'Maria', expiresAt: new Date(Date.now() + 300_000).toISOString() }));
            await a;
            expect(flow.authValid()).toBeTrue();

            const p = flow.submitReturn();
            const r = await waitForRequest(http, (u, m) => m === 'POST' && u.endsWith('/pos/orders/o-1/return'));
            expect(r.request.headers.get('Authorization')).toContain('Bearer');
            expect(r.request.body).toEqual(
                jasmine.objectContaining({ terminalUuid: 'term-1', shiftUuid: 'sh-1', reasonUuid: 'r-def', authorizationToken: 'tok-1', refundMethod: 'CARD', lines: [{ originalLineUuid: 'l-1', quantity: 1 }], storeCreditCustomerUuid: 'c-1' })
            );
            r.flush(ok({ uuid: 'ret-1', orderNumber: 'RET-S1-20261011-0001', flags: [], restockingFeeAmount: 11.7, authorizedByName: 'Maria' }));
            const saved = await p;
            await settle();
            http.match(() => true).forEach(x => x.flush(ok([]))); // background push of other queues (none)
            expect(saved.syncStatus).toBe('synced');
            expect(saved.orderNumber).toBe('RET-S1-20261011-0001');
            expect(saved.receipt).toEqual(jasmine.objectContaining({ refundAmount: 105.3, restockingFee: 11.7, authorizedByName: 'Maria', refundMethod: 'CARD' }));
        });

        it('a server "Manager authorization required." becomes a manager prompt; nothing is kept', async () => {
            await beginServer();
            flow.setQty('l-1', 2);
            flow.reasonUuid.set('r-def');
            flow.refundMethod.set('CARD');
            const p = flow.submitReturn();
            (await waitForRequest(http, u => u.endsWith('/pos/orders/o-1/return'))).flush(
                { success: false, message: MSG.managerRequired, result: { requiresManager: true, reasons: ['WINDOW'] } },
                { status: 400, statusText: 'Bad Request' }
            );
            const err = await p.catch(e => e);
            expect(err).toEqual(jasmine.any(ManagerRequiredError));
            expect((err as ManagerRequiredError).reasons).toEqual(['WINDOW']);
            expect(await db.returns.count()).toBe(0);
        });

        it('a used / expired manager token asks for the manager again', async () => {
            await beginServer(lookup({ policy: { maxReturnNoAuth: 50 } }));
            flow.setQty('l-1', 1);
            flow.reasonUuid.set('r-def');
            flow.refundMethod.set('CARD');
            flow.authorization.set({ token: 'old', managerUserId: 9, managerName: 'Maria', offline: false, amount: 117, reasons: ['AMOUNT'], expiresAt: Date.now() + 60_000 });
            const p = flow.submitReturn();
            const r = await waitForRequest(http, u => u.endsWith('/pos/orders/o-1/return'));
            expect(r.request.body.authorizationToken).toBe('old');
            r.flush({ success: false, message: MSG.authInvalid }, { status: 400, statusText: 'Bad Request' });
            const err = await p.catch(e => e);
            expect(err).toEqual(jasmine.any(ManagerRequiredError));
            expect((err as ManagerRequiredError).reasons).toEqual(['AMOUNT']);
            expect(flow.authorization()).toBeNull();
        });

        it('other refusals come back as ReturnRefusedError with the server text', async () => {
            await beginServer();
            flow.setQty('l-1', 1);
            flow.reasonUuid.set('r-def');
            flow.refundMethod.set('CARD');
            const p = flow.submitReturn();
            (await waitForRequest(http, u => u.endsWith('/pos/orders/o-1/return'))).flush({ success: false, message: MSG.refundMethodRefused }, { status: 400, statusText: 'Bad Request' });
            await expectAsync(p).toBeRejectedWith(new ReturnRefusedError(MSG.refundMethodRefused));
        });

        it('checks the note, the reason and the refund method before calling the server', async () => {
            await beginServer(lookup({ policy: { refundToOriginalMethod: true } }));
            flow.setQty('l-1', 5); // clamped to the returnable 2
            expect(flow.qty()['l-1']).toBe(2);
            await expectAsync(flow.submitReturn()).toBeRejectedWithError('Pick a reason.');
            flow.reasonUuid.set('r-pm');
            expect(flow.itemsError()).toBe(MSG.noteRequired);
            flow.reasonUuid.set('r-def');
            expect(flow.refundMethods()).toEqual(['CARD', 'STORE_CREDIT']);
            flow.refundMethod.set('CASH');
            await expectAsync(flow.submitReturn()).toBeRejectedWithError(MSG.refundMethodRefused);
        });

        it('server unreachable: the return is kept and queued as an offline return', async () => {
            await beginServer();
            flow.setQty('l-1', 1);
            flow.reasonUuid.set('r-def');
            flow.refundMethod.set('CASH');
            const p = flow.submitReturn();
            (await waitForRequest(http, u => u.endsWith('/pos/orders/o-1/return'))).error(new ProgressEvent('error'));
            const saved = await p;
            expect(saved.syncStatus).toBe('pending');
            expect(saved.isOffline).toBeTrue();
            expect(saved.request.isOffline).toBeTrue();
            expect(saved.lines[0].originalLineUuid).toBe('l-1');
        });

        it('offline: a device sale, the manager PIN checked against the cached hash, saved for returns/sync', async () => {
            conn.setBrowser(false);
            state.returnPolicy.set(policy({ returnWindowDays: 0 }));
            const salt = new Uint8Array([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16]);
            await db.staffPins.bulkPut([
                { userId: 9, name: 'Maria', posRole: 'SUPERVISOR', pinHash: await createIdentityV3Hash('4321', salt, 1000), permissions: ['POS_RETURN_OVERRIDE'] },
                { userId: 8, name: 'Omar', posRole: 'CASHIER', pinHash: await createIdentityV3Hash('5555', salt, 1000), permissions: ['POS_ORDER_CREATE'] }
            ]);
            await db.orders.put(deviceSale());
            await flow.begin(await flow.lookupReceipt('POS-S1-20261009-0007'));
            flow.setQty('ll-1', 1);
            flow.reasonUuid.set('r-def');
            expect(flow.managerNeeded()).toEqual(['WINDOW']);
            await expectAsync(flow.authorize('5555', ['WINDOW'])).toBeRejectedWithError(/cannot authorize returns/);
            await expectAsync(flow.authorize('0000', ['WINDOW'])).toBeRejectedWithError(/Wrong PIN/);
            const a = await flow.authorize('4321', ['WINDOW']);
            expect(a).toEqual(jasmine.objectContaining({ offline: true, managerUserId: 9, token: null }));

            expect(flow.refundMethods()).toContain('STORE_CREDIT');
            flow.refundMethod.set('STORE_CREDIT');
            const saved = await flow.submitReturn();
            expect(saved.syncStatus).toBe('pending');
            expect(saved.isOffline).toBeTrue();
            expect(saved.originalOrderUuid).toBe('o-9');
            expect(saved.originalOrderLocalId).toBe('sale-1');
            expect(saved.lines).toEqual([jasmine.objectContaining({ originalLineUuid: null, localLineId: 'll-1', lineNumber: 1, quantity: 1, amount: 117 })]);
            expect(saved.request).toEqual(jasmine.objectContaining({ kind: 'RETURN', isOffline: true, authorizedByUserId: 9, authorizationToken: null, refundMethod: 'STORE_CREDIT', storeCreditCustomerUuid: null, cashierUserId: 7 }));
            expect(saved.receipt.storeCredit).toEqual(jasmine.objectContaining({ uuid: null, amount: 117, customerName: null }));
            expect(saved.receipt.storeCredit!.number).toMatch(/^CN-S1-OFF-[0-9A-F]{8}$/);
            expect(saved.request.storeCreditOfflineNumber).toBe(saved.receipt.storeCredit!.number);
            http.expectNone(() => true);
        });
    });

    describe('exchange', () => {
        it('PAY: new items − return value, paid by tenders through orders/{uuid}/exchange', async () => {
            await beginServer();
            flow.setMode('EXCHANGE');
            flow.setQty('l-1', 1);
            flow.reasonUuid.set('r-def');
            flow.addNew(catalogItem({ variantUuid: 'v-2', sku: 'MUG', price: 200 }));
            expect(flow.newTotals().grandTotal).toBe(234);
            expect(flow.exchange()).toEqual({ net: 117, settlement: 'PAY' });
            await expectAsync(flow.submitExchange([])).toBeRejectedWithError(/do not match/);

            const p = flow.submitExchange([{ localId: 't1', method: 'CASH', amount: 120, changeGiven: 3 }]);
            const r = await waitForRequest(http, u => u.endsWith('/pos/orders/o-1/exchange'));
            expect(r.request.body).toEqual(
                jasmine.objectContaining({
                    lines: [{ originalLineUuid: 'l-1', quantity: 1 }],
                    newLines: [jasmine.objectContaining({ variantUuid: 'v-2', quantity: 1, unitPrice: 200, discountType: 'NONE' })],
                    orderDiscountType: 'NONE',
                    payments: [jasmine.objectContaining({ method: 'CASH', amount: 120, changeGiven: 3 })]
                })
            );
            r.flush(ok({ uuid: 'ex-1', orderNumber: 'RET-S1-20261011-0002', flags: [], settlement: 'PAY', netAmount: 117 }));
            const saved = await p;
            await settle();
            http.match(() => true).forEach(x => x.flush(ok([])));
            expect(saved.kind).toBe('EXCHANGE');
            expect(saved.receipt).toEqual(jasmine.objectContaining({ net: 117, settlement: 'PAY', newTotal: 234, returnValue: 117, refundAmount: 0 }));
        });

        it('REFUND / EVEN settlements and the store-credit offline block', async () => {
            await beginServer();
            flow.setMode('EXCHANGE');
            flow.setQty('l-1', 2);
            flow.reasonUuid.set('r-def');
            flow.addNew(catalogItem({ variantUuid: 'v-2', price: 100 }));
            expect(flow.exchange()).toEqual({ net: -117, settlement: 'REFUND' });
            expect(flow.refundAmount()).toBe(117);
            flow.addNew(catalogItem({ variantUuid: 'v-2', price: 100 }));
            expect(flow.exchange()).toEqual({ net: 0, settlement: 'EVEN' });
            flow.addNew(catalogItem({ variantUuid: 'v-2', price: 100 }));
            conn.setBrowser(false);
            await expectAsync(flow.submitExchange([{ localId: 't', method: 'STORE_CREDIT', amount: 117, changeGiven: 0, creditNoteUuid: 'cn-1' }])).toBeRejectedWithError(MSG.creditOffline);
        });

        it('exchange window 0 blocks exchanges', async () => {
            await beginServer(lookup({ policy: { exchangeWindowDays: 0, withinExchangeWindow: false } }));
            flow.setMode('EXCHANGE');
            expect(flow.exchangeError()).toBe(MSG.noExchanges);
        });
    });
});
