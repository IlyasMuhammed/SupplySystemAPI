import { Injectable, NgZone, inject } from '@angular/core';
import * as signalR from '@microsoft/signalr';
import { MessageService } from 'primeng/api';
import { Router } from '@angular/router';
import { Subject } from 'rxjs';
import { environment } from '../../../environments/environment';
import { PosAuth } from './pos-auth.service';
import { PosConnectivity } from './pos-connectivity.service';
import { PosDb } from './pos-db';
import { PosHttp } from './pos-http.service';
import { PosState } from './pos-state.service';
import { PosStoreCredits } from './pos-store-credit.service';
import { PosSync } from './pos-sync.service';

export const POS_TOAST_KEY = 'pos';
export const HEARTBEAT_MS = 60_000;

/** Minimal surface of a SignalR connection — lets specs drive the handlers without a server. */
export interface HubLike {
    on(name: string, handler: (...args: any[]) => void): void;
    invoke(name: string, ...args: unknown[]): Promise<unknown>;
    start(): Promise<void>;
    stop(): Promise<void>;
    onreconnected(cb: () => void): void;
    onreconnecting(cb: () => void): void;
    onclose(cb: () => void): void;
}

/**
 * A38 §8 — the `/hubs/pos` client. Joins the terminal's groups on every (re)connect, sends a heartbeat every 60 s and
 * applies the server's pushes to Dexie: prices, stock, product changes, customer balances, config changes, ForceSync,
 * ShiftAlert (toast) and ForceLogout. A reconnect triggers a delta sync.
 */
@Injectable({ providedIn: 'root' })
export class PosHub {
    private readonly zone = inject(NgZone);
    private readonly http = inject(PosHttp);
    private readonly db = inject(PosDb);
    private readonly state = inject(PosState);
    private readonly sync = inject(PosSync);
    private readonly auth = inject(PosAuth);
    private readonly conn = inject(PosConnectivity);
    private readonly router = inject(Router);
    private readonly messages = inject(MessageService);
    private readonly credits = inject(PosStoreCredits);

    /** ForceLogout { reason: "UNBOUND" } — PosBoot wipes the device. */
    readonly unbound$ = new Subject<void>();

    private hub: HubLike | null = null;
    private heartbeat?: ReturnType<typeof setInterval>;
    private terminalUuid: string | null = null;

    /** Event handlers still running (they write to IndexedDB asynchronously). */
    private readonly inFlight = new Set<Promise<unknown>>();

    /** Runs a handler's promise to completion, swallowing its error (a push never breaks the hub). */
    private track(p: Promise<unknown>): Promise<unknown> {
        const t = p.catch(() => {}).finally(() => this.inFlight.delete(t));
        this.inFlight.add(t);
        return t;
    }

    /** Resolves once every hub event handler started so far (and any it started) has finished — lets specs wait deterministically. */
    async whenIdle(): Promise<void> {
        while (this.inFlight.size) await Promise.all([...this.inFlight]);
    }

    /** Overridable in specs. */
    createConnection(): HubLike {
        return new signalR.HubConnectionBuilder()
            .withUrl(`${environment.apiOrigin}/hubs/pos`, {
                accessTokenFactory: () => this.http.deviceAccessToken(),
                transport: signalR.HttpTransportType.WebSockets | signalR.HttpTransportType.LongPolling
            })
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000, 60000])
            .configureLogging(signalR.LogLevel.Warning)
            .build() as unknown as HubLike;
    }

    get connected(): boolean {
        return this.conn.hubConnected();
    }

    async connect(terminalUuid: string): Promise<void> {
        if (this.hub && this.terminalUuid === terminalUuid) return;
        await this.disconnect();
        this.terminalUuid = terminalUuid;
        const hub = this.createConnection();
        this.hub = hub;
        this.register(hub);
        hub.onreconnecting(() => this.zone.run(() => this.conn.setHub(false)));
        hub.onreconnected(() =>
            this.zone.run(() =>
                this.track(
                    (async () => {
                        this.conn.setHub(true);
                        await this.join();
                        void this.sync.sync('DELTA');
                    })()
                )
            )
        );
        hub.onclose(() => this.zone.run(() => this.conn.setHub(false)));
        try {
            await hub.start();
            this.zone.run(() => this.conn.setHub(true));
            await this.join();
        } catch {
            this.conn.setHub(false);
            // withAutomaticReconnect only covers drops after a successful start; retry the first start later.
            setTimeout(() => {
                if (this.hub === hub) {
                    this.hub = null;
                    void this.connect(terminalUuid);
                }
            }, 30_000);
        }
        clearInterval(this.heartbeat);
        this.heartbeat = setInterval(() => void this.sendHeartbeat(), HEARTBEAT_MS);
    }

    async disconnect(): Promise<void> {
        clearInterval(this.heartbeat);
        const hub = this.hub;
        this.hub = null;
        this.terminalUuid = null;
        this.conn.setHub(false);
        if (hub) {
            try {
                await hub.stop();
            } catch {
                /* ignore */
            }
        }
    }

    private async join(): Promise<void> {
        if (!this.hub || !this.terminalUuid) return;
        try {
            await this.hub.invoke('JoinTerminal', this.terminalUuid);
        } catch {
            /* the module may be off — pushes simply won't arrive */
        }
    }

    async sendHeartbeat(): Promise<void> {
        if (!this.hub || !this.terminalUuid || !this.conn.hubConnected()) return;
        try {
            await this.hub.invoke('Heartbeat', this.terminalUuid);
        } catch {
            /* ignore */
        }
    }

    /** Wires every §8 event. Public for specs. */
    register(hub: HubLike): void {
        const on = (name: string, fn: (payload: any) => Promise<void> | void) =>
            hub.on(name, (payload: any) => this.zone.run(() => void this.track(Promise.resolve(fn(payload ?? {})))));

        on('PriceChanged', async (p: { variantUuid: string; newPrice: number }) => {
            await this.db.catalog.update(p.variantUuid, { price: p.newPrice });
            this.sync.catalogVersion.update(v => v + 1);
        });
        on('StockUpdated', async (p: { variantUuid: string; available: number }) => {
            await this.db.stock.put({ variantUuid: p.variantUuid, available: p.available });
            this.sync.stockVersion.update(v => v + 1);
        });
        on('ProductUpdated', async () => {
            const t = this.state.terminal();
            if (t) await this.sync.pullCatalog(t.uuid, false);
        });
        on('ProductDeactivated', async (p: { variantUuid: string }) => {
            await this.db.catalog.update(p.variantUuid, { isActive: false });
            this.sync.catalogVersion.update(v => v + 1);
        });
        on('CustomerBalanceChanged', async (p: { customerUuid: string; balance: number; creditLimit: number }) => {
            await this.db.customers.update(p.customerUuid, { balance: p.balance, creditLimit: p.creditLimit });
            this.sync.customersVersion.update(v => v + 1);
        });
        on('ConfigChanged', async () => {
            const t = this.state.terminal();
            if (!t) return;
            await this.sync.pullConfig(t.uuid);
            await this.sync.pullUsers(t.uuid);
            await this.sync.pullReturnSetup(t.uuid, false); // A39: reasons + policy travel with the config
        });
        // ── A39 §6 ──
        on('ReturnPolicyChanged', async (p: { storeUuid?: string }) => {
            const t = this.state.terminal();
            if (!t || (p.storeUuid && p.storeUuid !== t.storeUuid && p.storeUuid !== this.state.store()?.uuid)) return;
            await this.sync.pullReturnSetup(t.uuid, false);
        });
        on('ReturnProcessed', () => {
            this.sync.returnsVersion.update(v => v + 1);
        });
        on('CreditNoteIssued', (p: { creditNoteUuid: string; number: string; customerUuid?: string | null; amount: number; expiresAt?: string | null }) =>
            this.credits.applyEvent('ISSUED', p)
        );
        on('CreditNoteRedeemed', (p: { creditNoteUuid: string; redeemedAmount: number; remainingBalance: number; orderUuid?: string }) => this.credits.applyEvent('REDEEMED', p));
        on('CreditNoteExpired', (p: { creditNoteUuid: string; number?: string; customerUuid?: string | null; expiredAmount?: number }) => this.credits.applyEvent('EXPIRED', p));
        on('ForceSync', async (p: { syncType?: 'FULL' | 'DELTA' }) => {
            // POSA: syncType defaults to FULL.
            const type = p.syncType === 'DELTA' ? 'DELTA' : 'FULL';
            await this.db.log('FORCE_SYNC', true, `Requested by the back office (${type}).`);
            await this.sync.sync(type);
        });
        on('ShiftAlert', (p: { message?: string; severity?: string }) => {
            this.messages.add({ key: POS_TOAST_KEY, severity: toastSeverity(p.severity), summary: 'Shift', detail: p.message ?? '', life: 10000 });
        });
        on('ForceLogout', async (p: { reason?: string }) => {
            this.auth.signOut();
            if (p.reason === 'UNBOUND') {
                // POSA: the terminal was unbound in the back office — the device must forget it, PIN hashes first.
                await this.db.staffPins.clear();
                this.unbound$.next();
                this.messages.add({ key: POS_TOAST_KEY, severity: 'warn', summary: 'Terminal unbound', detail: 'This device was unbound from its terminal in the back office.', life: 10000 });
            } else {
                this.messages.add({ key: POS_TOAST_KEY, severity: 'warn', summary: 'Signed out', detail: p.reason || 'Signed out by the back office.', life: 10000 });
            }
            await this.router.navigate(['/pos']);
        });
    }
}

function toastSeverity(s: string | undefined): 'info' | 'warn' | 'error' | 'success' {
    const v = (s ?? '').toLowerCase();
    if (v.startsWith('err') || v === 'critical' || v === 'danger') return 'error';
    if (v.startsWith('warn')) return 'warn';
    if (v === 'success') return 'success';
    return 'info';
}
