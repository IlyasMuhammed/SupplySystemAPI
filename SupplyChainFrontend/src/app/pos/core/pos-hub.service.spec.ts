import { TestBed } from '@angular/core/testing';
import { MessageService } from 'primeng/api';
import { PosAuth } from './pos-auth.service';
import { PosBoot } from './pos-boot.service';
import { PosConnectivity } from './pos-connectivity.service';
import { PosDb } from './pos-db';
import { HubLike, PosHub } from './pos-hub.service';
import { PosState } from './pos-state.service';
import { PosSync } from './pos-sync.service';
import { catalogItem, clearPosStorage, customer, dropDb, posTestProviders, syncConfig } from './pos-testing';

class FakeHub implements HubLike {
    handlers = new Map<string, (p: unknown) => void>();
    invoked: { name: string; args: unknown[] }[] = [];
    reconnected?: () => void;
    on(name: string, h: (p: unknown) => void) {
        this.handlers.set(name, h);
    }
    async invoke(name: string, ...args: unknown[]) {
        this.invoked.push({ name, args });
        return undefined;
    }
    async start() {}
    async stop() {}
    onreconnected(cb: () => void) {
        this.reconnected = cb;
    }
    onreconnecting() {}
    onclose() {}
    emit(name: string, payload: unknown) {
        this.handlers.get(name)!(payload);
    }
}

// No sleeps or polling: every handler's IndexedDB work is awaited through `hub.whenIdle()` (the FakeHub replaces SignalR).

describe('A38 PosHub (/hubs/pos)', () => {
    let hub: PosHub;
    let fake: FakeHub;
    let db: PosDb;

    beforeEach(async () => {
        clearPosStorage();
        TestBed.configureTestingModule({ providers: posTestProviders() });
        hub = TestBed.inject(PosHub);
        db = TestBed.inject(PosDb);
        TestBed.inject(PosState).syncConfig.set(syncConfig());
        fake = new FakeHub();
        spyOn(hub, 'createConnection').and.returnValue(fake);
        await hub.connect('term-1');
    });

    afterEach(async () => {
        await hub.disconnect();
        await dropDb(db);
        clearPosStorage();
    });

    it('joins the terminal on connect and sends heartbeats', async () => {
        expect(fake.invoked[0]).toEqual({ name: 'JoinTerminal', args: ['term-1'] });
        expect(TestBed.inject(PosConnectivity).hubConnected()).toBeTrue();
        await hub.sendHeartbeat();
        expect(fake.invoked.at(-1)).toEqual({ name: 'Heartbeat', args: ['term-1'] });
    });

    it('applies PriceChanged, StockUpdated, ProductDeactivated and CustomerBalanceChanged to Dexie', async () => {
        await db.catalog.put(catalogItem({ variantUuid: 'v-1', price: 100 }));
        await db.customers.put(customer({ uuid: 'c-1', balance: 0 }));
        const sync = TestBed.inject(PosSync);
        const v = sync.catalogVersion();
        fake.emit('PriceChanged', { variantUuid: 'v-1', oldPrice: 100, newPrice: 120 });
        fake.emit('StockUpdated', { variantUuid: 'v-1', available: -1 });
        await hub.whenIdle();
        fake.emit('ProductDeactivated', { variantUuid: 'v-1' });
        fake.emit('CustomerBalanceChanged', { customerUuid: 'c-1', balance: 250, creditLimit: 900 });
        await hub.whenIdle();
        const item = (await db.catalog.get('v-1'))!;
        expect(item.price).toBe(120);
        expect(item.isActive).toBeFalse();
        expect((await db.stock.get('v-1'))!.available).toBe(-1);
        expect(await db.customers.get('c-1')).toEqual(jasmine.objectContaining({ balance: 250, creditLimit: 900 }));
        expect(sync.catalogVersion()).toBeGreaterThan(v);
    });

    it('ForceSync runs a sync of the requested type (default FULL)', async () => {
        const spy = spyOn(TestBed.inject(PosSync), 'sync').and.resolveTo();
        fake.emit('ForceSync', { syncType: 'DELTA' });
        fake.emit('ForceSync', {});
        await hub.whenIdle();
        expect(spy.calls.allArgs()).toEqual([['DELTA'], ['FULL']]);
    });

    it('ShiftAlert shows a toast', () => {
        const add = spyOn(TestBed.inject(MessageService), 'add');
        fake.emit('ShiftAlert', { message: 'Shift open for 13 hours', severity: 'Warning' });
        expect(add).toHaveBeenCalledWith(jasmine.objectContaining({ key: 'pos', severity: 'warn', detail: 'Shift open for 13 hours' }));
    });

    it('ForceLogout signs the cashier out', async () => {
        const auth = TestBed.inject(PosAuth);
        auth.cashier.set({ userId: 7, name: 'Sara', posRole: 'CASHIER', permissions: [], signedInAt: '', mode: 'online' });
        fake.emit('ForceLogout', { reason: 'Shift closed in the back office' });
        await hub.whenIdle();
        expect(auth.cashier()).toBeNull();
    });

    it('ForceLogout UNBOUND wipes the cached PIN hashes and the terminal', async () => {
        TestBed.inject(PosBoot); // subscribes to unbound$
        await db.staffPins.put({ userId: 7, name: 'Sara', posRole: 'CASHIER', pinHash: 'AQ==', permissions: [] });
        fake.emit('ForceLogout', { reason: 'UNBOUND' });
        await hub.whenIdle();
        const boot = TestBed.inject(PosBoot);
        expect(boot.pendingWipe).not.toBeNull();
        await boot.pendingWipe;
        expect(await db.staffPins.count()).toBe(0);
        expect(TestBed.inject(PosState).terminal()).toBeNull();
    });

    it('A39: ReturnPolicyChanged of this store (and ConfigChanged) refresh reasons + policy; another store is ignored', async () => {
        const sync = TestBed.inject(PosSync);
        const spy = spyOn(sync, 'pullReturnSetup').and.resolveTo();
        spyOn(sync, 'pullConfig').and.resolveTo();
        spyOn(sync, 'pullUsers').and.resolveTo();
        fake.emit('ReturnPolicyChanged', { storeUuid: 'store-2' });
        await hub.whenIdle();
        expect(spy).not.toHaveBeenCalled();
        fake.emit('ReturnPolicyChanged', { storeUuid: 'store-1' });
        await hub.whenIdle();
        expect(spy).toHaveBeenCalledOnceWith('term-1', false);
        fake.emit('ConfigChanged', {});
        await hub.whenIdle();
        expect(spy).toHaveBeenCalledTimes(2);
    });

    it('A39: CreditNoteIssued / Redeemed / Expired keep the store-credit cache in step', async () => {
        fake.emit('CreditNoteIssued', { creditNoteUuid: 'cn-1', number: 'CN-S1-20261011-0001', customerUuid: 'c-1', amount: 100, expiresAt: null });
        await hub.whenIdle();
        expect(await db.storeCredits.get('cn-1')).toEqual(jasmine.objectContaining({ number: 'CN-S1-20261011-0001', remainingBalance: 100, status: 'ACTIVE' }));
        fake.emit('CreditNoteRedeemed', { creditNoteUuid: 'cn-1', redeemedAmount: 100, remainingBalance: 0, orderUuid: 'o-1' });
        await hub.whenIdle();
        expect(await db.storeCredits.get('cn-1')).toEqual(jasmine.objectContaining({ remainingBalance: 0, status: 'FULLY_REDEEMED' }));
        fake.emit('CreditNoteIssued', { creditNoteUuid: 'cn-2', number: 'CN-2', customerUuid: null, amount: 5 });
        await hub.whenIdle();
        fake.emit('CreditNoteExpired', { creditNoteUuid: 'cn-2', number: 'CN-2', customerUuid: null, expiredAmount: 5 });
        await hub.whenIdle();
        expect((await db.storeCredits.get('cn-2'))!.status).toBe('EXPIRED');
    });

    it('A39: ReturnProcessed bumps the returns version (history refresh)', () => {
        const sync = TestBed.inject(PosSync);
        const v = sync.returnsVersion();
        fake.emit('ReturnProcessed', { returnOrderUuid: 'r', originalOrderUuid: 'o', originalStoreUuid: 'store-1', refundAmount: 1, refundMethod: 'CASH' });
        expect(sync.returnsVersion()).toBe(v + 1);
    });

    it('re-joins and runs a delta sync after a reconnect', async () => {
        const spy = spyOn(TestBed.inject(PosSync), 'sync').and.resolveTo();
        fake.invoked = [];
        fake.reconnected!();
        await hub.whenIdle();
        expect(fake.invoked[0].name).toBe('JoinTerminal');
        expect(spy).toHaveBeenCalledWith('DELTA');
    });
});
