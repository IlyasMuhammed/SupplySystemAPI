import { Injectable, inject, signal } from '@angular/core';
import { PosApi } from './pos-api.service';
import { PosAuth } from './pos-auth.service';
import { PosConnectivity } from './pos-connectivity.service';
import { PosDb } from './pos-db';
import { PosHttpError } from './pos-http.service';
import { CashDirection, CashReason, LocalCashMovement, LocalSession, Shift } from './pos.models';
import { PosState } from './pos-state.service';
import { PosSync } from './pos-sync.service';
import { newId } from './pos-cart.service';

const K_SESSION = 'sms-pos.sessionLocalId';

/**
 * A38 §3–§4 — the terminal's shift and the signed-in cashier's session.
 * Opening / closing a shift is online only (the server owns the totals); cash movements and cashier sessions work
 * offline: they are queued in Dexie with a `localId` (idempotent on the server) and pushed by PosSync.
 */
@Injectable({ providedIn: 'root' })
export class PosShifts {
    private readonly api = inject(PosApi);
    private readonly db = inject(PosDb);
    private readonly state = inject(PosState);
    private readonly auth = inject(PosAuth);
    private readonly sync = inject(PosSync);
    private readonly conn = inject(PosConnectivity);

    readonly sessionLocalId = signal<string | null>(readSession());

    /** Reloads the current shift (online); offline keeps the cached one. 404 ⇒ no shift. */
    async refreshCurrent(): Promise<Shift | null> {
        const terminal = this.state.terminal();
        if (!terminal) return null;
        if (!this.conn.online()) return this.state.shift();
        try {
            const shift = await this.api.currentShift(terminal.uuid);
            await this.state.setShift(shift);
            return shift;
        } catch (e) {
            if (e instanceof PosHttpError && e.status === 404) {
                await this.state.setShift(null);
                return null;
            }
            return this.state.shift();
        }
    }

    async open(openingFloat: number, varianceThreshold?: number | null): Promise<Shift> {
        const terminal = this.state.terminal();
        if (!terminal) throw new Error('This device is not bound to a terminal.');
        const shift = await this.api.openShift(terminal.uuid, openingFloat, varianceThreshold);
        await this.state.setShift(shift);
        await this.ensureSession();
        return shift;
    }

    async startClose(): Promise<Shift> {
        const shift = this.requireShift();
        const next = await this.api.startClose(shift.uuid);
        await this.state.setShift(next);
        return next;
    }

    async resume(): Promise<Shift> {
        const shift = this.requireShift();
        const next = await this.api.resumeShift(shift.uuid);
        await this.state.setShift(next);
        return next;
    }

    /** Close with the counted cash. The shift leaves the terminal (CLOSED → RECONCILED / DISPUTED). */
    async close(actualCash: number, note?: string | null): Promise<Shift> {
        const shift = this.requireShift();
        const closed = await this.api.closeShift(shift.uuid, actualCash, note);
        await this.state.setShift(null);
        this.setSession(null);
        return closed;
    }

    /**
     * Makes sure the signed-in cashier has a session on the current OPEN shift (POS-CSH-04): reuses this device's session
     * for the same person and shift, otherwise queues a new one (pushed now when online).
     */
    async ensureSession(): Promise<string | null> {
        const shift = this.state.shift();
        const cashier = this.auth.cashier();
        if (!shift || shift.status !== 'OPEN' || !cashier) return null;
        const current = this.sessionLocalId();
        if (current) {
            const row = await this.db.sessions.get(current);
            if (row && row.shiftUuid === shift.uuid && row.userId === cashier.userId) return current;
        }
        const existing = (await this.db.sessions.where('shiftUuid').equals(shift.uuid).toArray()).find(s => s.userId === cashier.userId);
        const localId = existing?.localId ?? newId();
        if (!existing) {
            const row: LocalSession = {
                localId,
                shiftUuid: shift.uuid,
                userId: cashier.userId,
                userName: cashier.name,
                startedAt: new Date().toISOString(),
                syncStatus: 'pending',
                attempts: 0
            };
            await this.db.sessions.put(row);
        } else if (existing.syncStatus === 'synced') {
            // A switch back to an earlier cashier: the server resumes the same user's session (idempotent on localId).
            await this.db.sessions.update(localId, { syncStatus: 'pending', nextAttemptAt: null });
        }
        this.setSession(localId);
        void this.sync.pushAll();
        return localId;
    }

    /** Ends the cashier's server session (best effort, online only). */
    async endSession(): Promise<void> {
        const localId = this.sessionLocalId();
        this.setSession(null);
        if (!localId || !this.conn.online()) return;
        const row = await this.db.sessions.get(localId);
        if (row?.uuid) {
            try {
                await this.api.endSession(row.uuid);
            } catch {
                /* the next cashier's session ends it server-side anyway */
            }
        }
    }

    /** Queues a cash in/out for the current shift — works offline (§4 localId idempotent). */
    async addCashMovement(direction: CashDirection, amount: number, reason: CashReason, note?: string | null): Promise<LocalCashMovement> {
        const shift = this.requireShift();
        if (shift.status !== 'OPEN') throw new Error('Cash movements are only possible while the shift is open.');
        if (!(amount > 0)) throw new Error('Enter an amount greater than zero.');
        const row: LocalCashMovement = {
            localId: newId(),
            shiftUuid: shift.uuid,
            syncStatus: 'pending',
            direction,
            amount,
            reason,
            note: note?.trim() || null,
            createdAtLocal: new Date().toISOString(),
            attempts: 0
        };
        await this.db.cashMovements.put(row);
        await this.sync.refreshCounts();
        if (this.conn.online()) void this.sync.pushAll();
        return row;
    }

    async queuedCashMovements(shiftUuid: string): Promise<LocalCashMovement[]> {
        return (await this.db.cashMovements.where('shiftUuid').equals(shiftUuid).toArray()).filter(m => m.syncStatus !== 'synced');
    }

    private requireShift(): Shift {
        const shift = this.state.shift();
        if (!shift) throw new Error('There is no open shift on this terminal.');
        return shift;
    }

    private setSession(localId: string | null): void {
        this.sessionLocalId.set(localId);
        try {
            if (localId) sessionStorage.setItem(K_SESSION, localId);
            else sessionStorage.removeItem(K_SESSION);
        } catch {
            /* ignore */
        }
    }
}

function readSession(): string | null {
    try {
        return sessionStorage.getItem(K_SESSION);
    } catch {
        return null;
    }
}
