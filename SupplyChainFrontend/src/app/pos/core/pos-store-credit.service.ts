import { Injectable, inject } from '@angular/core';
import { PosApi } from './pos-api.service';
import { PosConnectivity } from './pos-connectivity.service';
import { PosDb } from './pos-db';
import { MSG } from './pos-return-rules';
import { StoreCredit } from './pos.models';
import { PosSync } from './pos-sync.service';

/**
 * A39 D-2 — store credit on the till. Fetched on demand (per customer or by number) and cached in Dexie `storeCredits`
 * for display; redemption is online only, so the cache is never used to accept a tender.
 */
@Injectable({ providedIn: 'root' })
export class PosStoreCredits {
    private readonly api = inject(PosApi);
    private readonly db = inject(PosDb);
    private readonly conn = inject(PosConnectivity);
    private readonly sync = inject(PosSync);

    /** The customer's ACTIVE store credits (online; offline ⇒ the cached ones, flagged by the caller as display only). */
    async forCustomer(customerUuid: string): Promise<StoreCredit[]> {
        if (!this.conn.online()) return (await this.db.storeCredits.where('customerUuid').equals(customerUuid).toArray()).filter(c => c.status === 'ACTIVE');
        const list = (await this.api.customerCreditNotes(customerUuid, 'ACTIVE')) ?? [];
        await this.cache(list);
        return list;
    }

    /** Looks a note up by its number (scanned barcode or typed). Online only. */
    async byNumber(number: string): Promise<StoreCredit> {
        if (!this.conn.online()) throw new Error(MSG.creditOffline);
        const n = number.trim().toUpperCase();
        if (!n) throw new Error('Enter or scan the credit note number.');
        const c = await this.api.creditNoteByNumber(n);
        await this.cache([c]);
        return c;
    }

    async cache(list: StoreCredit[]): Promise<void> {
        if (!list.length) return;
        try {
            await this.db.storeCredits.bulkPut(list);
            this.sync.storeCreditVersion.update(v => v + 1);
        } catch {
            /* cache only */
        }
    }

    /** Hub CreditNoteIssued / Redeemed / Expired: keep the cached copy in step (unknown notes are added when issued). */
    async applyEvent(kind: 'ISSUED' | 'REDEEMED' | 'EXPIRED', p: { creditNoteUuid: string; number?: string; customerUuid?: string | null; amount?: number; expiresAt?: string | null; remainingBalance?: number; expiredAmount?: number }): Promise<void> {
        const existing = await this.db.storeCredits.get(p.creditNoteUuid);
        if (kind === 'ISSUED') {
            if (!existing && p.number) {
                const now = new Date().toISOString();
                await this.db.storeCredits.put({
                    uuid: p.creditNoteUuid,
                    number: p.number,
                    customerUuid: p.customerUuid ?? null,
                    originalAmount: p.amount ?? 0,
                    remainingBalance: p.amount ?? 0,
                    redeemedAmount: 0,
                    currencyCode: '',
                    status: 'ACTIVE',
                    issuedAtStoreName: '',
                    issuedByName: '',
                    issuedAt: now,
                    expiresAt: p.expiresAt ?? null,
                    modifiedAt: now
                });
            }
        } else if (existing) {
            if (kind === 'REDEEMED') {
                const balance = p.remainingBalance ?? existing.remainingBalance;
                await this.db.storeCredits.update(p.creditNoteUuid, { remainingBalance: balance, status: balance > 0 ? 'ACTIVE' : 'FULLY_REDEEMED', lastUsedAt: new Date().toISOString() });
            } else {
                await this.db.storeCredits.update(p.creditNoteUuid, { status: 'EXPIRED' });
            }
        }
        this.sync.storeCreditVersion.update(v => v + 1);
    }
}
