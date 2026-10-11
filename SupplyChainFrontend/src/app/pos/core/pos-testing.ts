// Spec helpers for the A38 POS (imported by *.spec.ts only).
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, TestRequest, provideHttpClientTesting } from '@angular/common/http/testing';
import { EnvironmentProviders, Provider } from '@angular/core';
import { provideRouter } from '@angular/router';
import { MessageService } from 'primeng/api';
import { POS_DB_NAME, PosDb } from './pos-db';
import { CatalogItem, EffectivePolicy, PosStore, PosTerminal, ReturnReason, SyncConfig, SyncCustomer } from './pos.models';

/** Fresh IndexedDB per spec + HTTP testing backend + router + toasts. */
export function posTestProviders(): (Provider | EnvironmentProviders)[] {
    return [
        { provide: POS_DB_NAME, useValue: `sms-pos-test-${Date.now()}-${Math.random().toString(36).slice(2)}` },
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
        MessageService
    ];
}

/** Clears every POS key in local/session storage (device tokens, cashier, PIN lock, device id). */
export function clearPosStorage(): void {
    for (const s of [localStorage, sessionStorage]) {
        Object.keys(s)
            .filter(k => k.startsWith('sms-pos.'))
            .forEach(k => s.removeItem(k));
    }
}

export async function dropDb(db: PosDb): Promise<void> {
    db.close();
    await db.delete();
}

/** An unsigned JWT with the given claims (exp defaults to one hour ahead). */
export function fakeJwt(claims: Record<string, unknown> = {}): string {
    const enc = (o: unknown) => btoa(JSON.stringify(o)).replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_');
    return `${enc({ alg: 'none', typ: 'JWT' })}.${enc({ exp: Math.floor(Date.now() / 1000) + 3600, ...claims })}.sig`;
}

/** Waits (real time) until a request matching `predicate` is pending, then returns it. */
export async function waitForRequest(http: HttpTestingController, predicate: (url: string, method: string) => boolean, timeoutMs = 3000): Promise<TestRequest> {
    const start = Date.now();
    for (;;) {
        const found = http.match(r => predicate(r.urlWithParams, r.method));
        if (found.length) return found[0];
        if (Date.now() - start > timeoutMs) throw new Error('No matching request was made.');
        await new Promise(r => setTimeout(r, 5));
    }
}

export const ok = <T>(result: T) => ({ success: true, message: '', result });

export function store(overrides: Partial<PosStore> = {}): PosStore {
    return {
        uuid: 'store-1',
        code: 'S1',
        name: 'Main Store',
        warehouseUuid: 'wh-1',
        timezone: 'Asia/Karachi',
        currencyCode: 'PKR',
        taxInclusivePricing: false,
        defaultTaxPercent: 17,
        offlineCreditTolerancePct: 10,
        autoCloseShiftHours: 24,
        varianceThreshold: 100,
        isActive: true,
        ...overrides
    };
}

export function terminal(overrides: Partial<PosTerminal> = {}): PosTerminal {
    return { uuid: 'term-1', storeUuid: 'store-1', storeName: 'Main Store', code: 'T1', name: 'Till 1', deviceBound: true, isOnline: true, isActive: true, ...overrides };
}

export function syncConfig(storeOverrides: Partial<PosStore> = {}): SyncConfig {
    const s = store(storeOverrides);
    return {
        serverTime: new Date().toISOString(),
        terminal: terminal(),
        store: s,
        taxCode: { uuid: 'tax-1', code: 'GST', percent: s.defaultTaxPercent },
        paymentMethods: ['CASH', 'CARD', 'MOBILE', 'ON_ACCOUNT'],
        features: { returns: true, creditSales: true, barcode: true, multiStore: false },
        currency: { code: 'PKR', decimals: 2 }
    };
}

export function catalogItem(overrides: Partial<CatalogItem> = {}): CatalogItem {
    return {
        variantUuid: 'v-1',
        productUuid: 'p-1',
        sku: 'SKU-1',
        name: 'Tea',
        barcode: '8901234567890',
        categoryId: 1,
        categoryName: 'Drinks',
        price: 100,
        isWeighable: false,
        isFavorite: false,
        sortOrder: 0,
        isActive: true,
        modifiedAt: '2026-10-01T00:00:00Z',
        ...overrides
    };
}

/** A39: an effective return policy (org default, 30-day window, no fee, cash + store credit allowed). */
export function policy(overrides: Partial<EffectivePolicy> = {}): EffectivePolicy {
    return {
        uuid: 'pol-1',
        name: 'Default',
        returnWindowDays: 30,
        exchangeWindowDays: null,
        requireReceipt: true,
        allowNoReceiptExchange: false,
        allowCrossStoreReturn: false,
        crossStoreRequiresManager: true,
        restockingFeePct: 0,
        maxReturnNoAuth: null,
        refundToOriginalMethod: false,
        allowCashRefund: true,
        allowCreditNoteRefund: true,
        creditNoteExpiryDays: 180,
        isActive: true,
        source: 'ORG_DEFAULT',
        ...overrides
    };
}

/** A39: a return reason (DEFECTIVE, no note, no manager, no fee override). */
export function reason(overrides: Partial<ReturnReason> = {}): ReturnReason {
    return { uuid: 'r-def', code: 'DEFECTIVE', name: 'Defective', requiresNote: false, requiresManager: false, appliesTo: 'BOTH', restockingFeeOverride: null, sortOrder: 1, isActive: true, modifiedAt: '', ...overrides };
}

export function customer(overrides: Partial<SyncCustomer> = {}): SyncCustomer {
    return {
        uuid: 'c-1',
        code: 'C-0001',
        name: 'Ali Traders',
        creditLimit: 1000,
        balance: 0,
        isActive: true,
        isSystem: false,
        allowPosCredit: true,
        requiresIdForCredit: false,
        modifiedAt: '2026-10-01T00:00:00Z',
        ...overrides
    };
}
