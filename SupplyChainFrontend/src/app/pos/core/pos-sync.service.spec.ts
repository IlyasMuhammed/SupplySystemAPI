import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { PosConnectivity } from './pos-connectivity.service';
import { CFG, PosDb } from './pos-db';
import { LocalOrder, LocalReturn, SyncOrder } from './pos.models';
import { PosState } from './pos-state.service';
import { PosSync, STALE_AFTER_MS, backoffMs } from './pos-sync.service';
import { catalogItem, clearPosStorage, customer, dropDb, fakeJwt, ok, policy, posTestProviders, reason, syncConfig, waitForRequest } from './pos-testing';
import { PosTokens } from './pos-tokens.service';

function order(localId: string, createdAtLocal: string): LocalOrder {
    const payload = { localId, createdAtLocal, terminalUuid: 'term-1', shiftUuid: 'sh-1', isOffline: true, lines: [], payments: [], offlineNumber: `POS-S1-OFF-${localId}` } as unknown as SyncOrder;
    return { localId, syncStatus: 'pending', createdAtLocal, shiftUuid: 'sh-1', payload, receipt: {} as LocalOrder['receipt'], attempts: 0 };
}

describe('A38 PosSync (sync engine)', () => {
    let sync: PosSync;
    let db: PosDb;
    let http: HttpTestingController;
    let conn: PosConnectivity;

    beforeEach(() => {
        clearPosStorage();
        TestBed.configureTestingModule({ providers: posTestProviders() });
        sync = TestBed.inject(PosSync);
        db = TestBed.inject(PosDb);
        http = TestBed.inject(HttpTestingController);
        conn = TestBed.inject(PosConnectivity);
        TestBed.inject(PosState).syncConfig.set(syncConfig());
        TestBed.inject(PosTokens).setDevice({ accessToken: fakeJwt(), refreshToken: 'r' });
    });

    afterEach(async () => {
        sync.stop();
        http.verify();
        await dropDb(db);
        clearPosStorage();
    });

    describe('pull', () => {
        it('full catalog sync follows hasMore / nextSince and replaces the table', async () => {
            await db.catalog.put(catalogItem({ variantUuid: 'stale', sku: 'OLD' }));
            const p = sync.pullCatalog('term-1', true);
            const r1 = await waitForRequest(http, u => u.includes('/pos/sync/catalog'));
            expect(r1.request.params.get('since')).toBeNull();
            expect(r1.request.params.get('terminalUuid')).toBe('term-1');
            r1.flush(ok({ serverTime: 't', nextSince: 'cursor-1', hasMore: true, items: [catalogItem({ variantUuid: 'a', sku: 'A' })], categories: [{ id: 1, name: 'Drinks', sortOrder: 0 }] }));
            const r2 = await waitForRequest(http, u => u.includes('since=cursor-1'));
            r2.flush(ok({ serverTime: 't2', nextSince: 'cursor-2', hasMore: false, items: [catalogItem({ variantUuid: 'b', sku: 'B' })], categories: [{ id: 1, name: 'Drinks', sortOrder: 0 }] }));
            await p;
            expect((await db.catalog.toArray()).map(i => i.variantUuid).sort()).toEqual(['a', 'b']);
            expect(await db.getConfig(CFG.catalogSince)).toBe('cursor-2');
            expect(await db.categories.count()).toBe(1);
        });

        it('delta sync sends the stored cursor and upserts', async () => {
            await db.setConfig(CFG.customersSince, 'c-cursor');
            await db.customers.put(customer({ uuid: 'keep' }));
            const p = sync.pullCustomers('term-1', false);
            const r = await waitForRequest(http, u => u.includes('/pos/sync/customers'));
            expect(r.request.params.get('since')).toBe('c-cursor');
            r.flush(ok({ serverTime: 't', nextSince: 'c-next', hasMore: false, customers: [customer({ uuid: 'new', balance: 50 })] }));
            await p;
            expect(await db.customers.count()).toBe(2);
            expect(await db.getConfig(CFG.customersSince)).toBe('c-next');
        });

        it('stock snapshot replaces the stock table', async () => {
            await db.stock.put({ variantUuid: 'gone', available: 3 });
            const p = sync.pullStock();
            (await waitForRequest(http, u => u.includes('/pos/sync/stock'))).flush(ok({ serverTime: 't', items: [{ variantUuid: 'a', available: -2 }] }));
            await p;
            expect(await db.stock.toArray()).toEqual([{ variantUuid: 'a', available: -2 }]);
            expect(sync.lastStockAt()).not.toBeNull();
        });

        it('staff PIN hashes are fetched with the X-Pos-Device header', async () => {
            const p = sync.pullUsers('term-1');
            const r = await waitForRequest(http, u => u.includes('/pos/sync/users'));
            expect(r.request.headers.get('X-Pos-Device')).toBe(TestBed.inject(PosTokens).deviceUuid());
            r.flush(ok([{ userId: 7, name: 'Sara', posRole: 'CASHIER', pinHash: 'AQ==', permissions: [] }]));
            await p;
            expect(await db.staffPins.count()).toBe(1);
        });

        it('A39: pulls return reasons (delta cursor) and the effective return policy', async () => {
            await db.setConfig(CFG.reasonsSince, 'r-cursor');
            const p = sync.pullReturnSetup('term-1', false);
            const r1 = await waitForRequest(http, u => u.includes('/pos/sync/return-reasons'));
            expect(r1.request.params.get('since')).toBe('r-cursor');
            r1.flush(ok({ serverTime: 't', nextSince: 'r-next', reasons: [reason(), reason({ uuid: 'r2', code: 'OTHER', isActive: false })] }));
            const r2 = await waitForRequest(http, u => u.includes('/pos/sync/return-policy'));
            expect(r2.request.params.get('terminalUuid')).toBe('term-1');
            r2.flush(ok(policy({ restockingFeePct: 15, source: 'STORE' })));
            await p;
            expect(await db.returnReasons.count()).toBe(2);
            expect(await db.getConfig(CFG.reasonsSince)).toBe('r-next');
            const state = TestBed.inject(PosState);
            expect(state.returnPolicy()?.restockingFeePct).toBe(15);
            expect((await db.getConfig<{ source: string }>(CFG.returnPolicy))?.source).toBe('STORE');
        });

        it('A39: "No return policy configured" clears the cached policy; other refusals keep the device data', async () => {
            const state = TestBed.inject(PosState);
            await state.setReturnPolicy(policy());
            await db.returnReasons.put(reason());
            const p = sync.pullReturnSetup('term-1', false);
            (await waitForRequest(http, u => u.includes('/pos/sync/return-reasons'))).flush({ success: false, message: 'Forbidden' }, { status: 403, statusText: 'Forbidden' });
            (await waitForRequest(http, u => u.includes('/pos/sync/return-policy'))).flush({ success: false, message: 'No return policy configured. Contact your administrator.' }, { status: 400, statusText: 'Bad Request' });
            await p;
            expect(await db.returnReasons.count()).toBe(1);
            expect(state.returnPolicy()).toBeNull();
        });
    });

    describe('A39 returns queue', () => {
        function localReturn(overrides: Partial<LocalReturn> = {}): LocalReturn {
            return {
                localId: 'ret-1',
                kind: 'RETURN',
                syncStatus: 'pending',
                createdAtLocal: '2026-10-10T12:00:00Z',
                shiftUuid: 'sh-1',
                isOffline: true,
                originalOrderUuid: null,
                originalOrderLocalId: 'o1',
                originalOrderNumber: 'POS-S1-OFF-O1',
                request: { localId: 'ret-1', kind: 'RETURN', terminalUuid: 'term-1', shiftUuid: 'sh-1', reasonUuid: 'r-def', refundMethod: 'CASH', createdAtLocal: '2026-10-10T12:00:00Z', isOffline: true, authorizedByUserId: 9 } as LocalReturn['request'],
                lines: [{ originalLineUuid: null, localLineId: 'll-2', lineNumber: 2, variantUuid: 'v-2', quantity: 1, amount: 50 }],
                receipt: { storeCredit: null } as unknown as LocalReturn['receipt'],
                attempts: 0,
                ...overrides
            };
        }

        it('waits while the original sale is still queued', async () => {
            await db.orders.put(order('o1', '2026-10-10T09:00:00Z'));
            await db.returns.put(localReturn());
            await sync.pushReturns();
            http.expectNone(r => r.url.includes('/pos/returns/sync'));
            expect((await db.returns.get('ret-1'))!.syncStatus).toBe('pending');
        });

        it('resolves the server line uuids of a device sale, then pushes returns/sync', async () => {
            await db.orders.put({ ...order('o1', '2026-10-10T09:00:00Z'), syncStatus: 'synced', orderUuid: 'srv-o1' });
            await db.returns.put(localReturn());
            const p = sync.pushReturns();
            (await waitForRequest(http, (u, m) => m === 'GET' && u.endsWith('/pos/orders/srv-o1'))).flush(
                ok({ uuid: 'srv-o1', lines: [{ uuid: 'L1', lineNumber: 1, variantUuid: 'v-1' }, { uuid: 'L2', lineNumber: 2, variantUuid: 'v-2' }] })
            );
            const r = await waitForRequest(http, u => u.endsWith('/pos/returns/sync'));
            expect(r.request.body).toEqual(
                jasmine.objectContaining({ kind: 'RETURN', originalOrderUuid: 'srv-o1', isOffline: true, authorizedByUserId: 9, lines: [{ originalLineUuid: 'L2', quantity: 1 }] })
            );
            r.flush(ok({ localId: 'ret-1', success: true, orderUuid: 'ret-u', orderNumber: 'RET-S1-20261010-0001', flags: ['AUTHORIZED_OFFLINE'] }));
            await p;
            const saved = (await db.returns.get('ret-1'))!;
            expect(saved.syncStatus).toBe('synced');
            expect(saved.orderNumber).toBe('RET-S1-20261010-0001');
            expect(saved.flags).toEqual(['AUTHORIZED_OFFLINE']);
            expect(saved.lines[0].originalLineUuid).toBe('L2');
            await sync.refreshCounts();
            expect(sync.pendingCount()).toBe(0);
        });

        it('a refused return is marked error and counted', async () => {
            await db.returns.put(localReturn({ originalOrderUuid: 'srv-o1', lines: [{ originalLineUuid: 'L2', variantUuid: 'v-2', quantity: 1, amount: 50 }] }));
            const p = sync.pushReturns();
            (await waitForRequest(http, u => u.endsWith('/pos/returns/sync'))).flush(ok({ localId: 'ret-1', success: false, error: 'Cannot return 1 of \'Mug\'. Only 0 returnable.' }));
            await p;
            const saved = (await db.returns.get('ret-1'))!;
            expect(saved.syncStatus).toBe('error');
            expect(saved.lastError).toContain('Only 0 returnable');
            await sync.refreshCounts();
            expect(sync.errorCount()).toBe(1);
        });
    });

    describe('push', () => {
        it('pushes a single order with POST sync and stores the server number', async () => {
            await db.orders.put(order('o1', '2026-10-10T09:00:00Z'));
            const p = sync.pushOrders();
            const r = await waitForRequest(http, (u, m) => m === 'POST' && u.endsWith('/pos/orders/sync'));
            r.flush(ok({ localId: 'o1', success: true, orderUuid: 'u1', orderNumber: 'POS-S1-20261010-0001', flags: ['OFFLINE'] }));
            await p;
            const o = (await db.orders.get('o1'))!;
            expect(o.syncStatus).toBe('synced');
            expect(o.orderNumber).toBe('POS-S1-20261010-0001');
            expect(o.flags).toEqual(['OFFLINE']);
        });

        it('pushes the queue as one chronological batch; a refused order is marked error, the rest succeed', async () => {
            await db.orders.bulkPut([order('late', '2026-10-10T10:00:00Z'), order('early', '2026-10-10T08:00:00Z'), order('mid', '2026-10-10T09:00:00Z')]);
            const p = sync.pushOrders();
            const r = await waitForRequest(http, u => u.endsWith('/pos/orders/sync/batch'));
            expect(r.request.body.orders.map((o: SyncOrder) => o.localId)).toEqual(['early', 'mid', 'late']);
            r.flush(
                ok([
                    { localId: 'early', success: true, orderNumber: 'N1' },
                    { localId: 'mid', success: false, error: 'Variant not found.' },
                    { localId: 'late', success: true, orderNumber: 'N3' }
                ])
            );
            await p;
            expect((await db.orders.get('early'))!.syncStatus).toBe('synced');
            const mid = (await db.orders.get('mid'))!;
            expect(mid.syncStatus).toBe('error');
            expect(mid.lastError).toBe('Variant not found.');
            expect((await db.orders.get('late'))!.syncStatus).toBe('synced');
            await sync.refreshCounts();
            expect(sync.errorCount()).toBe(1);
        });

        it('keeps orders pending with backoff when the server is unreachable', async () => {
            await db.orders.put(order('o1', '2026-10-10T09:00:00Z'));
            const before = Date.now();
            const p = sync.pushOrders();
            (await waitForRequest(http, u => u.endsWith('/pos/orders/sync'))).error(new ProgressEvent('error'));
            await p;
            const o = (await db.orders.get('o1'))!;
            expect(o.syncStatus).toBe('pending');
            expect(o.attempts).toBe(1);
            expect(o.nextAttemptAt!).toBeGreaterThanOrEqual(before + backoffMs(1));
            expect(conn.online()).toBeFalse();
            // not due yet → nothing is sent
            conn.reportSuccess();
            await sync.pushOrders();
            http.expectNone(r => r.url.includes('/pos/orders/sync'));
        });

        it('a 5xx keeps the order pending; retryErrors re-queues refused ones', async () => {
            await db.orders.put({ ...order('o1', '2026-10-10T09:00:00Z'), syncStatus: 'error', lastError: 'x' });
            const p = sync.retryErrors();
            (await waitForRequest(http, u => u.endsWith('/pos/orders/sync'))).flush({ success: false, message: 'boom' }, { status: 500, statusText: 'Server Error' });
            await p;
            expect((await db.orders.get('o1'))!.syncStatus).toBe('pending');
        });

        it('fills the session uuid once the cashier session has synced', async () => {
            await db.sessions.put({ localId: 'sess-l', shiftUuid: 'sh-1', userId: 7, userName: 'Sara', startedAt: '', syncStatus: 'synced', uuid: 'sess-u', attempts: 0 });
            await db.orders.put({ ...order('o1', '2026-10-10T09:00:00Z'), sessionLocalId: 'sess-l' });
            const p = sync.pushOrders();
            const r = await waitForRequest(http, u => u.endsWith('/pos/orders/sync'));
            expect(r.request.body.sessionUuid).toBe('sess-u');
            r.flush(ok({ localId: 'o1', success: true }));
            await p;
        });

        it('pushes queued cash movements with their localId', async () => {
            await db.cashMovements.put({ localId: 'cm1', shiftUuid: 'sh-1', syncStatus: 'pending', direction: 'OUT', amount: 500, reason: 'SAFE_DROP', createdAtLocal: 'x', attempts: 0 });
            const p = sync.pushCashMovements();
            const r = await waitForRequest(http, u => u.endsWith('/pos/shifts/sh-1/cash-movements'));
            expect(r.request.body).toEqual(jasmine.objectContaining({ direction: 'OUT', amount: 500, reason: 'SAFE_DROP', localId: 'cm1' }));
            r.flush(ok({ uuid: 'cm-u' }));
            await p;
            expect((await db.cashMovements.get('cm1'))!.syncStatus).toBe('synced');
        });

        it('does nothing while offline', async () => {
            conn.setBrowser(false);
            await db.orders.put(order('o1', '2026-10-10T09:00:00Z'));
            await sync.pushAll();
            http.expectNone(() => true);
        });
    });

    describe('state', () => {
        it('backs off 5 s, 10 s, 20 s … up to 5 minutes', () => {
            expect([1, 2, 3, 10].map(backoffMs)).toEqual([5000, 10000, 20000, 300000]);
        });

        it('is stale when the last pull is older than 24 h (POS-SYN-07)', () => {
            expect(sync.stale()).toBeTrue(); // never pulled
            sync.lastPullAt.set(new Date(Date.now() - 60_000).toISOString());
            expect(sync.stale()).toBeFalse();
            sync.lastPullAt.set(new Date(Date.now() - STALE_AFTER_MS - 60_000).toISOString());
            expect(sync.stale()).toBeTrue();
        });
    });
});
