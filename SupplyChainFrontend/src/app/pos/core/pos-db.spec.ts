import Dexie from 'dexie';
import { POS_DB_V1, PosDatabase } from './pos-db';

describe('A39 Dexie v2 upgrade (sms-pos)', () => {
    const name = `sms-pos-upgrade-${Date.now()}-${Math.random().toString(36).slice(2)}`;

    afterEach(async () => {
        await Dexie.delete(name);
    });

    it('opens an A38 (v1) database as v2 without losing any row and adds the A39 tables', async () => {
        // An A38 device: only version 1 exists.
        const v1 = new Dexie(name);
        v1.version(1).stores(POS_DB_V1);
        await v1.open();
        await v1.table('orders').put({ localId: 'o1', syncStatus: 'pending', createdAtLocal: '2026-10-10T09:00:00Z', shiftUuid: 'sh-1', payload: { localId: 'o1' } });
        await v1.table('catalog').put({ variantUuid: 'v-1', sku: 'A', barcode: '1', categoryId: 1, isFavorite: 1 });
        await v1.table('staffPins').put({ userId: 7, name: 'Sara', pinHash: 'AQ==' });
        await v1.table('config').put({ key: 'catalogSince', value: 'cursor-1' });
        await v1.table('syncLog').add({ at: 'x', kind: 'K', ok: true, message: 'm' });
        expect(v1.verno).toBe(1);
        v1.close();

        const db = new PosDatabase(name);
        await db.open();
        expect(db.verno).toBe(2);
        expect((await db.orders.get('o1'))?.syncStatus).toBe('pending');
        expect(await db.catalog.where('barcode').equals('1').count()).toBe(1);
        expect(await db.staffPins.count()).toBe(1);
        expect(await db.getConfig('catalogSince')).toBe('cursor-1');
        expect(await db.syncLog.count()).toBe(1);

        // new tables with their indexes
        await db.returnReasons.put({ uuid: 'r1', code: 'DEFECTIVE', name: 'Defective', requiresNote: false, requiresManager: false, appliesTo: 'BOTH', sortOrder: 1, isActive: true, modifiedAt: '' });
        await db.storeCredits.put({ uuid: 'cn1', number: 'CN-S1-1', customerUuid: 'c-1', status: 'ACTIVE' } as never);
        await db.returns.put({ localId: 'ret1', syncStatus: 'pending', createdAtLocal: 'x', shiftUuid: 'sh-1', originalOrderLocalId: 'o1' } as never);
        expect(await db.storeCredits.where('customerUuid').equals('c-1').count()).toBe(1);
        expect(await db.returns.where('originalOrderLocalId').equals('o1').count()).toBe(1);
        expect(await db.returnReasons.orderBy('sortOrder').count()).toBe(1);
        db.close();
    });
});
