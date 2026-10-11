import { Injectable, InjectionToken, inject } from '@angular/core';
import Dexie, { Table } from 'dexie';
import {
    CatalogCategory,
    CatalogItem,
    ConfigEntry,
    LocalCashMovement,
    LocalOrder,
    LocalReturn,
    LocalSession,
    ReturnReason,
    StaffPin,
    StoreCredit,
    SyncCustomer,
    SyncLogEntry
} from './pos.models';

/** IndexedDB name; tests provide a unique one per spec. */
export const POS_DB_NAME = new InjectionToken<string>('POS_DB_NAME', { providedIn: 'root', factory: () => 'sms-pos' });

export interface StockRow {
    variantUuid: string;
    available: number;
}

/** A38 schema (version 1). */
export const POS_DB_V1 = {
    catalog: 'variantUuid, barcode, sku, categoryId, isFavorite',
    categories: 'id',
    stock: 'variantUuid',
    customers: 'uuid, code, name',
    orders: 'localId, syncStatus, createdAtLocal, shiftUuid',
    cashMovements: 'localId, syncStatus, createdAtLocal, shiftUuid',
    sessions: 'localId, syncStatus, shiftUuid',
    config: 'key',
    syncLog: '++id, at',
    staffPins: 'userId'
};

/** A39 tables added by version 2: return reasons, the store-credit cache and the return/exchange queue. */
export const POS_DB_V2_ADDED = {
    returnReasons: 'uuid, code, sortOrder',
    storeCredits: 'uuid, number, customerUuid, status',
    returns: 'localId, syncStatus, createdAtLocal, shiftUuid, originalOrderUuid, originalOrderLocalId'
};

/**
 * A38 D-14 — the device-local store of the POS PWA (`sms-pos`). The service worker caches only the app shell; every piece
 * of data the terminal needs offline lives here: catalog, stock snapshot, customers, the pending order / cash-movement /
 * session queues, configuration, a sync log and the store staff's PIN hashes (bound terminals only, wiped on unbind).
 */
export class PosDatabase extends Dexie {
    catalog!: Table<CatalogItem, string>;
    categories!: Table<CatalogCategory, number>;
    stock!: Table<StockRow, string>;
    customers!: Table<SyncCustomer, string>;
    orders!: Table<LocalOrder, string>;
    cashMovements!: Table<LocalCashMovement, string>;
    sessions!: Table<LocalSession, string>;
    config!: Table<ConfigEntry, string>;
    syncLog!: Table<SyncLogEntry, number>;
    staffPins!: Table<StaffPin, number>;
    // A39 (v2)
    returnReasons!: Table<ReturnReason, string>;
    storeCredits!: Table<StoreCredit, string>;
    returns!: Table<LocalReturn, string>;

    constructor(name: string) {
        super(name);
        this.version(1).stores(POS_DB_V1);
        // A39: v2 only adds tables — every v1 table and row is kept (Dexie carries earlier versions' stores forward).
        this.version(2).stores(POS_DB_V2_ADDED);
    }

    async getConfig<T>(key: string): Promise<T | undefined> {
        const row = await this.config.get(key);
        return row?.value as T | undefined;
    }

    async setConfig(key: string, value: unknown): Promise<void> {
        await this.config.put({ key, value });
    }

    async removeConfig(key: string): Promise<void> {
        await this.config.delete(key);
    }

    /** Appends to the sync log, keeping the newest 500 entries. */
    async log(kind: string, ok: boolean, message: string): Promise<void> {
        try {
            await this.syncLog.add({ at: new Date().toISOString(), kind, ok, message });
            const count = await this.syncLog.count();
            if (count > 500) {
                const oldest = await this.syncLog.orderBy('id').limit(count - 500).primaryKeys();
                await this.syncLog.bulkDelete(oldest);
            }
        } catch {
            /* logging never breaks a sync */
        }
    }
}

/** Config keys. */
export const CFG = {
    terminalConfig: 'terminalConfig',
    syncConfig: 'syncConfig',
    catalogSince: 'catalogSince',
    customersSince: 'customersSince',
    lastPullAt: 'lastPullAt',
    lastStockAt: 'lastStockAt',
    lastFullSyncAt: 'lastFullSyncAt',
    enabledModules: 'enabledModules',
    currentShift: 'currentShift',
    // A39
    reasonsSince: 'reasonsSince',
    returnPolicy: 'returnPolicy'
} as const;

@Injectable({ providedIn: 'root' })
export class PosDb extends PosDatabase {
    constructor() {
        super(inject(POS_DB_NAME));
    }
}
