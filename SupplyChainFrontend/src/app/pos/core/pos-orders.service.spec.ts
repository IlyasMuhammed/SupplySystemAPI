import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Tender } from './pos-calculator';
import { PosAuth } from './pos-auth.service';
import { PosCart } from './pos-cart.service';
import { PosConnectivity } from './pos-connectivity.service';
import { PosDb } from './pos-db';
import { PosOrders, SaleRefusedError, offlineNumber } from './pos-orders.service';
import { PosState } from './pos-state.service';
import { catalogItem, clearPosStorage, customer, dropDb, fakeJwt, ok, posTestProviders, syncConfig, waitForRequest } from './pos-testing';
import { PosTokens } from './pos-tokens.service';
import { Shift } from './pos.models';

describe('A38 PosOrders (complete sale)', () => {
    let orders: PosOrders;
    let cart: PosCart;
    let db: PosDb;
    let http: HttpTestingController;
    let conn: PosConnectivity;
    let auth: PosAuth;

    const cash = (amount: number): Tender[] => [{ localId: 't1', method: 'CASH', amount, changeGiven: 0 }];

    beforeEach(() => {
        clearPosStorage();
        TestBed.configureTestingModule({ providers: posTestProviders() });
        orders = TestBed.inject(PosOrders);
        cart = TestBed.inject(PosCart);
        db = TestBed.inject(PosDb);
        http = TestBed.inject(HttpTestingController);
        conn = TestBed.inject(PosConnectivity);
        auth = TestBed.inject(PosAuth);
        const state = TestBed.inject(PosState);
        state.syncConfig.set(syncConfig({ defaultTaxPercent: 0 }));
        state.shift.set({ uuid: 'sh-1', status: 'OPEN', shiftNumber: 'SH-S1-20261010-01' } as Shift);
        TestBed.inject(PosTokens).setDevice({ accessToken: fakeJwt(), refreshToken: 'r' });
        auth.cashier.set({ userId: 7, name: 'Sara', posRole: 'CASHIER', permissions: ['POS_ORDER_CREATE'], signedInAt: '', mode: 'offline' });
        auth.locked.set(false);
        cart.add(catalogItem({ price: 100 }), 2);
    });

    afterEach(async () => {
        http.verify();
        await dropDb(db);
        clearPosStorage();
    });

    it('numbers offline orders POS-{store}-OFF-{short}', () => {
        expect(offlineNumber('S1', 'ab12cd34-0000-4000-8000-000000000000')).toBe('POS-S1-OFF-AB12CD34');
    });

    it('offline: saves the order to Dexie as pending, flags it offline and clears the cart', async () => {
        conn.setBrowser(false);
        cart.customer.set(customer());
        const o = await orders.completeSale({ tenders: cash(200), offlineCredit: false });
        const stored = (await db.orders.get(o.localId))!;
        expect(stored.syncStatus).toBe('pending');
        expect(stored.payload.isOffline).toBeTrue();
        expect(stored.payload.offlineNumber).toMatch(/^POS-S1-OFF-[0-9A-F]{8}$/);
        expect(stored.payload.customerUuid).toBe('c-1');
        expect(stored.payload.cashierUserId).toBe(7);
        expect('sessionLocalId' in stored.payload).toBeTrue();
        expect(stored.payload.clientTotals).toEqual({ subtotal: 200, discountAmount: 0, taxTotal: 0, grandTotal: 200 });
        expect(stored.payload.lines[0]).toEqual(jasmine.objectContaining({ variantUuid: 'v-1', quantity: 2, unitPrice: 100, discountType: 'NONE' }));
        expect(stored.receipt.customerName).toBe('Ali Traders');
        expect(cart.isEmpty()).toBeTrue();
    });

    it('online: pushes immediately and keeps the server number', async () => {
        const p = orders.completeSale({ tenders: cash(200), offlineCredit: false });
        const r = await waitForRequest(http, u => u.endsWith('/pos/orders/sync'));
        expect(r.request.body.isOffline).toBeFalse();
        r.flush(ok({ localId: r.request.body.localId, success: true, orderNumber: 'POS-S1-20261010-0001' }));
        const o = await p;
        expect(o.syncStatus).toBe('synced');
        expect(o.orderNumber).toBe('POS-S1-20261010-0001');
    });

    it('online: a refusal comes back to the payment screen and the order is not kept', async () => {
        const p = orders.completeSale({ tenders: cash(200), offlineCredit: false });
        (await waitForRequest(http, u => u.endsWith('/pos/orders/sync'))).flush({ success: false, message: 'Credit limit exceeded. Limit: 1. Balance: 0. This order: 200.' }, { status: 400, statusText: 'Bad Request' });
        await expectAsync(p).toBeRejectedWith(jasmine.any(SaleRefusedError));
        expect(await db.orders.count()).toBe(0);
        expect(cart.isEmpty()).toBeFalse();
    });

    it('online but unreachable: the sale becomes an offline order', async () => {
        const p = orders.completeSale({ tenders: cash(200), offlineCredit: false });
        (await waitForRequest(http, u => u.endsWith('/pos/orders/sync'))).error(new ProgressEvent('error'));
        const o = await p;
        expect(o.syncStatus).toBe('pending');
        expect(o.payload.isOffline).toBeTrue();
        expect(cart.isEmpty()).toBeTrue();
    });

    it('refuses unbalanced tenders and discounts without POS_ORDER_DISCOUNT', async () => {
        conn.setBrowser(false);
        await expectAsync(orders.completeSale({ tenders: cash(150), offlineCredit: false })).toBeRejectedWithError('Payments (150.00) do not match the order total (200.00).');
        cart.setOrderDiscount('PERCENT', 10);
        await expectAsync(orders.completeSale({ tenders: cash(180), offlineCredit: false })).toBeRejectedWithError('You are not allowed to give discounts.');
    });

    it('A39: a STORE_CREDIT tender is refused offline (POS-CN-04)', async () => {
        conn.setBrowser(false);
        const tenders: Tender[] = [{ localId: 't1', method: 'STORE_CREDIT', amount: 200, changeGiven: 0, creditNoteUuid: 'cn-1' }];
        await expectAsync(orders.completeSale({ tenders, offlineCredit: false })).toBeRejectedWithError('Credit note redemption requires internet connectivity.');
        expect(await db.orders.count()).toBe(0);
    });

    it('A39: sends creditNoteUuid; an unreachable server keeps the redemption an online request (replayed, not offline)', async () => {
        const tenders: Tender[] = [
            { localId: 't1', method: 'STORE_CREDIT', amount: 150, changeGiven: 0, creditNoteUuid: 'cn-1', reference: 'CN-S1-1' },
            { localId: 't2', method: 'CASH', amount: 50, changeGiven: 0 }
        ];
        const p = orders.completeSale({ tenders, offlineCredit: false });
        const r = await waitForRequest(http, u => u.endsWith('/pos/orders/sync'));
        expect(r.request.body.payments[0]).toEqual(jasmine.objectContaining({ method: 'STORE_CREDIT', amount: 150, creditNoteUuid: 'cn-1' }));
        expect('creditNoteUuid' in r.request.body.payments[1]).toBeFalse();
        r.error(new ProgressEvent('error'));
        const o = await p;
        expect(o.syncStatus).toBe('pending');
        expect(o.payload.isOffline).toBeFalse();
    });

    it('needs an OPEN shift', async () => {
        TestBed.inject(PosState).shift.set({ uuid: 'sh-1', status: 'CLOSING' } as Shift);
        await expectAsync(orders.completeSale({ tenders: cash(200), offlineCredit: false })).toBeRejectedWithError('There is no open shift on this terminal.');
    });
});
