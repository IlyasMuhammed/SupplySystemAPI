import { Injectable, OnDestroy, computed, inject, signal } from '@angular/core';
import { Subscription } from 'rxjs';
import { PosApi } from './pos-api.service';
import { PosConnectivity } from './pos-connectivity.service';
import { CFG, PosDb } from './pos-db';
import { PosHttpError, errorMessage, isNetworkError } from './pos-http.service';
import { CatalogCategory, CatalogItem, LocalOrder, LocalReturn, ReturnSyncRequest, SyncCustomer, SyncOrder, SyncResult } from './pos.models';
import { PosState } from './pos-state.service';
import { PosTokens } from './pos-tokens.service';

export const STALE_AFTER_MS = 24 * 60 * 60 * 1000; // POS-SYN-07
export const STOCK_EVERY_MS = 5 * 60 * 1000;
export const RETRY_TICK_MS = 15 * 1000;
export const PROBE_EVERY_MS = 30 * 1000;
export const BATCH_SIZE = 50;

/** Backoff after n failed attempts: 5 s, 10 s, 20 s … capped at 5 minutes. */
export function backoffMs(attempts: number): number {
    return Math.min(5 * 60 * 1000, 5000 * Math.pow(2, Math.max(0, attempts - 1)));
}

/** A failure that should be retried automatically (no answer, server error, auth to be refreshed). */
export function isRetryable(e: unknown): boolean {
    return isNetworkError(e) || (e instanceof PosHttpError && (e.retryable || e.status === 401 || e.status === 403));
}

/**
 * A38 D-14 / POS-SYN — the sync engine.
 * Pull: config, catalog and customers (delta with `nextSince` + `hasMore` loops; full on first activation or ForceSync FULL),
 * staff PIN hashes, and the store stock snapshot every 5 minutes while online.
 * Push: orders immediately when online and, after a reconnect, every queued order in one chronological batch (§5 batch);
 * cash movements and cashier sessions by `localId` (idempotent). Failures that can heal retry with backoff; refusals are
 * marked `error` and listed on the settings screen for a manual retry.
 */
@Injectable({ providedIn: 'root' })
export class PosSync implements OnDestroy {
    private readonly api = inject(PosApi);
    private readonly db = inject(PosDb);
    private readonly state = inject(PosState);
    private readonly conn = inject(PosConnectivity);
    private readonly tokens = inject(PosTokens);

    readonly pulling = signal(false);
    readonly pushing = signal(false);
    readonly lastPullAt = signal<string | null>(null);
    readonly lastStockAt = signal<string | null>(null);
    readonly pendingCount = signal(0);
    readonly errorCount = signal(0);
    /** Bumped after catalog / stock / customer changes so screens re-read Dexie. */
    readonly catalogVersion = signal(0);
    readonly stockVersion = signal(0);
    readonly customersVersion = signal(0);
    /** A39: bumped after return reasons / returns / store credits change. */
    readonly reasonsVersion = signal(0);
    readonly returnsVersion = signal(0);
    readonly storeCreditVersion = signal(0);
    readonly lastError = signal<string | null>(null);
    /** Progress text during a full sync. */
    readonly progress = signal<string | null>(null);

    private readonly now = signal(Date.now());
    readonly stale = computed(() => {
        const last = this.lastPullAt();
        return !last || this.now() - new Date(last).getTime() > STALE_AFTER_MS;
    });

    private pushLock: Promise<void> | null = null;
    private pullLock: Promise<void> | null = null;
    private timers: ReturnType<typeof setInterval>[] = [];
    private sub?: Subscription;
    private started = false;

    ngOnDestroy(): void {
        this.stop();
    }

    /** Restores counters and starts the timers (called by the POS shell once a terminal is bound). */
    async start(): Promise<void> {
        if (this.started) return;
        this.started = true;
        // A push interrupted by a crash/reload leaves rows in `syncing`.
        await this.db.orders.where('syncStatus').equals('syncing').modify({ syncStatus: 'pending' });
        await this.db.returns.where('syncStatus').equals('syncing').modify({ syncStatus: 'pending' });
        this.lastPullAt.set((await this.db.getConfig<string>(CFG.lastPullAt)) ?? null);
        this.lastStockAt.set((await this.db.getConfig<string>(CFG.lastStockAt)) ?? null);
        await this.refreshCounts();

        this.sub = this.conn.reconnected$.subscribe(() => void this.onReconnect());
        this.timers.push(setInterval(() => this.now.set(Date.now()), 60_000));
        this.timers.push(setInterval(() => void this.tick(), RETRY_TICK_MS));
        this.timers.push(setInterval(() => void (this.conn.online() ? this.pullStock().catch(() => {}) : undefined), STOCK_EVERY_MS));
        this.timers.push(setInterval(() => void this.probe(), PROBE_EVERY_MS));
    }

    stop(): void {
        this.timers.forEach(t => clearInterval(t));
        this.timers = [];
        this.sub?.unsubscribe();
        this.started = false;
    }

    /** First activation (never fully synced) ⇒ full sync, otherwise delta. Never throws. */
    async initialSync(): Promise<void> {
        const lastFull = await this.db.getConfig<string>(CFG.lastFullSyncAt);
        await this.sync(lastFull ? 'DELTA' : 'FULL');
    }

    /** Pull everything that changed (or everything, for FULL) and push the queues. Never throws. */
    async sync(type: 'FULL' | 'DELTA' = 'DELTA'): Promise<void> {
        if (!this.conn.online()) return;
        await this.pushAll();
        await this.pull(type === 'FULL');
    }

    // ── pull ──────────────────────────────────────────────────────────────────

    pull(full: boolean): Promise<void> {
        if (this.pullLock) return this.pullLock;
        this.pullLock = this.doPull(full).finally(() => (this.pullLock = null));
        return this.pullLock;
    }

    private async doPull(full: boolean): Promise<void> {
        const terminal = this.state.terminal();
        if (!terminal) return;
        this.pulling.set(true);
        this.lastError.set(null);
        try {
            this.progress.set('Configuration…');
            await this.pullConfig(terminal.uuid);
            this.progress.set('Return policy…');
            await this.pullReturnSetup(terminal.uuid, full);
            this.progress.set('Products…');
            await this.pullCatalog(terminal.uuid, full);
            this.progress.set('Customers…');
            await this.pullCustomers(terminal.uuid, full);
            this.progress.set('Staff…');
            await this.pullUsers(terminal.uuid);
            this.progress.set('Stock…');
            await this.pullStock();
            const at = new Date().toISOString();
            await this.db.setConfig(CFG.lastPullAt, at);
            if (full) await this.db.setConfig(CFG.lastFullSyncAt, at);
            this.lastPullAt.set(at);
            this.now.set(Date.now());
            await this.db.log(full ? 'FULL_SYNC' : 'DELTA_SYNC', true, 'Pulled configuration, products, customers, staff and stock.');
        } catch (e) {
            const msg = errorMessage(e, 'Sync failed.');
            this.lastError.set(msg);
            await this.db.log(full ? 'FULL_SYNC' : 'DELTA_SYNC', false, msg);
        } finally {
            this.pulling.set(false);
            this.progress.set(null);
        }
    }

    async pullConfig(terminalUuid: string): Promise<void> {
        const cfg = await this.api.syncConfig(terminalUuid);
        await this.state.setSyncConfig(cfg);
    }

    /**
     * A39 §5 — return reasons (delta on `since`, full = replace) and the store's effective return policy. A refusal
     * (no policy, module/permission off, endpoint not there yet) is logged and keeps what the device has — except that
     * "No return policy configured" clears the cached policy. Network errors propagate like every other pull.
     */
    async pullReturnSetup(terminalUuid: string, full: boolean): Promise<void> {
        try {
            let since = full ? null : ((await this.db.getConfig<string>(CFG.reasonsSince)) ?? null);
            const page = await this.api.syncReturnReasons(since);
            const reasons = page?.reasons ?? [];
            await this.db.transaction('rw', this.db.returnReasons, this.db.config, async () => {
                if (since === null) await this.db.returnReasons.clear();
                if (reasons.length) await this.db.returnReasons.bulkPut(reasons);
                since = page?.nextSince ?? page?.serverTime ?? since;
                if (since) await this.db.setConfig(CFG.reasonsSince, since);
            });
            this.reasonsVersion.update(v => v + 1);
        } catch (e) {
            if (isNetworkError(e)) throw e;
            await this.db.log('RETURN_REASONS', false, errorMessage(e));
        }
        try {
            const policy = await this.api.syncReturnPolicy(terminalUuid);
            await this.state.setReturnPolicy(policy ?? null);
        } catch (e) {
            if (isNetworkError(e)) throw e;
            if (e instanceof PosHttpError && /no return policy/i.test(e.message)) await this.state.setReturnPolicy(null);
            await this.db.log('RETURN_POLICY', false, errorMessage(e));
        }
    }

    async pullCatalog(terminalUuid: string, full: boolean): Promise<number> {
        let since = full ? null : ((await this.db.getConfig<string>(CFG.catalogSince)) ?? null);
        const isFull = since === null;
        const all: CatalogItem[] = [];
        let categories: CatalogCategory[] | null = null;
        let guard = 0;
        for (;;) {
            const page = await this.api.syncCatalog(terminalUuid, since);
            if (page.categories) categories = page.categories;
            if (isFull) all.push(...(page.items ?? []));
            else if (page.items?.length) await this.db.catalog.bulkPut(page.items);
            since = page.nextSince ?? page.serverTime ?? since;
            if (!isFull) await this.db.setConfig(CFG.catalogSince, since);
            if (!page.hasMore || ++guard > 1000) break;
        }
        await this.db.transaction('rw', this.db.catalog, this.db.categories, this.db.config, async () => {
            if (isFull) {
                await this.db.catalog.clear();
                await this.db.catalog.bulkPut(all);
                await this.db.setConfig(CFG.catalogSince, since);
            }
            if (categories) {
                await this.db.categories.clear();
                await this.db.categories.bulkPut(categories);
            }
        });
        this.catalogVersion.update(v => v + 1);
        return isFull ? all.length : -1;
    }

    async pullCustomers(terminalUuid: string, full: boolean): Promise<void> {
        let since = full ? null : ((await this.db.getConfig<string>(CFG.customersSince)) ?? null);
        const isFull = since === null;
        const all: SyncCustomer[] = [];
        let guard = 0;
        for (;;) {
            const page = await this.api.syncCustomers(terminalUuid, since);
            if (isFull) all.push(...(page.customers ?? []));
            else if (page.customers?.length) await this.db.customers.bulkPut(page.customers);
            since = page.nextSince ?? page.serverTime ?? since;
            if (!isFull) await this.db.setConfig(CFG.customersSince, since);
            if (!page.hasMore || ++guard > 1000) break;
        }
        if (isFull) {
            await this.db.transaction('rw', this.db.customers, this.db.config, async () => {
                await this.db.customers.clear();
                await this.db.customers.bulkPut(all);
                await this.db.setConfig(CFG.customersSince, since);
            });
        }
        this.customersVersion.update(v => v + 1);
    }

    /** Staff PIN hashes for the offline check (bound terminals only, §7). A refusal keeps the previous cache. */
    async pullUsers(terminalUuid: string): Promise<void> {
        try {
            const users = await this.api.syncUsers(terminalUuid, this.tokens.deviceUuid());
            await this.db.transaction('rw', this.db.staffPins, async () => {
                await this.db.staffPins.clear();
                await this.db.staffPins.bulkPut(users ?? []);
            });
        } catch (e) {
            if (isNetworkError(e)) throw e;
            await this.db.log('USERS', false, errorMessage(e));
        }
    }

    async pullStock(): Promise<void> {
        const terminal = this.state.terminal();
        if (!terminal) return;
        const snap = await this.api.syncStock(terminal.uuid);
        await this.db.transaction('rw', this.db.stock, async () => {
            await this.db.stock.clear();
            await this.db.stock.bulkPut(snap.items ?? []);
        });
        const at = new Date().toISOString();
        await this.db.setConfig(CFG.lastStockAt, at);
        this.lastStockAt.set(at);
        this.stockVersion.update(v => v + 1);
    }

    // ── push ──────────────────────────────────────────────────────────────────

    /** Push sessions, cash movements and orders that are due. Single-flight; never throws. */
    pushAll(includeErrors = false): Promise<void> {
        if (this.pushLock) return this.pushLock;
        this.pushLock = this.doPushAll(includeErrors).finally(() => (this.pushLock = null));
        return this.pushLock;
    }

    private async doPushAll(includeErrors: boolean): Promise<void> {
        if (!this.conn.online()) return;
        this.pushing.set(true);
        try {
            await this.pushSessions();
            await this.pushCashMovements();
            await this.pushOrders(includeErrors);
            await this.pushReturns(includeErrors);
        } catch {
            /* each step records its own failures */
        } finally {
            this.pushing.set(false);
            await this.refreshCounts();
        }
    }

    private due<T extends { syncStatus: string; nextAttemptAt?: number | null }>(row: T, includeErrors: boolean): boolean {
        if (row.syncStatus === 'error') return includeErrors;
        return row.syncStatus === 'pending' && (!row.nextAttemptAt || row.nextAttemptAt <= Date.now());
    }

    async pushOrders(includeErrors = false): Promise<void> {
        const rows = (await this.db.orders.orderBy('createdAtLocal').toArray()).filter(o => this.due(o, includeErrors));
        if (!rows.length) return;
        for (let i = 0; i < rows.length; i += BATCH_SIZE) {
            const chunk = rows.slice(i, i + BATCH_SIZE);
            const payloads = await Promise.all(chunk.map(o => this.payloadFor(o)));
            await this.db.orders.bulkUpdate(chunk.map(o => ({ key: o.localId, changes: { syncStatus: 'syncing' as const } })));
            try {
                const results = chunk.length === 1 ? [await this.api.syncOrder(payloads[0])] : await this.api.syncOrders(payloads);
                for (const o of chunk) {
                    const r = results.find(x => x.localId === o.localId);
                    if (r) await this.applyResult(o, r);
                    else await this.failOrder(o, new Error('No result returned for this order.'), true);
                }
            } catch (e) {
                for (const o of chunk) await this.failOrder(o, e, isRetryable(e));
                if (isNetworkError(e)) return;
            }
        }
    }

    /** Fills the server session uuid when the session row has synced. */
    private async payloadFor(o: LocalOrder): Promise<SyncOrder> {
        if (o.payload.sessionUuid || !o.sessionLocalId) return o.payload;
        const s = await this.db.sessions.get(o.sessionLocalId);
        return s?.uuid ? { ...o.payload, sessionUuid: s.uuid } : o.payload;
    }

    async applyResult(o: LocalOrder, r: SyncResult): Promise<void> {
        if (r.success) {
            await this.db.orders.update(o.localId, {
                syncStatus: 'synced',
                orderUuid: r.orderUuid ?? null,
                orderNumber: r.orderNumber ?? null,
                flags: r.flags ?? [],
                warnings: r.warnings ?? [],
                syncedAt: new Date().toISOString(),
                lastError: null,
                nextAttemptAt: null
            });
            await this.db.log('ORDER', true, `${o.payload.offlineNumber ?? o.localId} → ${r.orderNumber ?? 'synced'}${r.warnings?.length ? ` (${r.warnings.join('; ')})` : ''}`);
        } else {
            await this.failOrder(o, new Error(r.error || 'The server refused this order.'), false);
        }
    }

    private async failOrder(o: LocalOrder, e: unknown, retry: boolean): Promise<void> {
        const attempts = (o.attempts ?? 0) + 1;
        const msg = errorMessage(e);
        await this.db.orders.update(o.localId, {
            syncStatus: retry ? 'pending' : 'error',
            attempts,
            nextAttemptAt: retry ? Date.now() + backoffMs(attempts) : null,
            lastError: msg
        });
        await this.db.log('ORDER', false, `${o.payload.offlineNumber ?? o.localId}: ${msg}`);
    }

    /**
     * A39 — offline returns / exchanges through `POST returns/sync` (idempotent on localId), after the sales (an offline
     * return of an offline sale needs that sale on the server first: it waits while the sale is still queued).
     */
    async pushReturns(includeErrors = false): Promise<void> {
        const rows = (await this.db.returns.orderBy('createdAtLocal').toArray()).filter(r => this.due(r, includeErrors));
        for (const r of rows) {
            let body: ReturnSyncRequest | null;
            try {
                body = await this.resolveReturn(r);
            } catch (e) {
                await this.failReturn(r, e, isRetryable(e));
                if (isNetworkError(e)) return;
                continue;
            }
            if (!body) continue; // original sale still queued
            await this.db.returns.update(r.localId, { syncStatus: 'syncing' });
            try {
                const res = await this.api.syncReturn(body);
                await this.applyReturnResult(r, res);
            } catch (e) {
                await this.failReturn(r, e, isRetryable(e));
                if (isNetworkError(e)) return;
            }
        }
        this.returnsVersion.update(v => v + 1);
    }

    /** Builds the `returns/sync` body: original order uuid and line uuids resolved, session uuid filled. Null = wait. */
    async resolveReturn(r: LocalReturn): Promise<ReturnSyncRequest | null> {
        let originalOrderUuid = r.originalOrderUuid ?? null;
        if (!originalOrderUuid && r.originalOrderLocalId) {
            const o = await this.db.orders.get(r.originalOrderLocalId);
            if (!o) throw new Error('The original sale is no longer on this device.');
            if (o.syncStatus === 'error') throw new Error('The original sale was refused by the server, so this return cannot be sent.');
            if (o.syncStatus !== 'synced' || !o.orderUuid) return null;
            originalOrderUuid = o.orderUuid;
        }
        if (!originalOrderUuid) throw new Error('The original sale is unknown.');
        let lines = r.lines;
        if (lines.some(l => !l.originalLineUuid)) {
            const server = await this.api.orderAuto(originalOrderUuid);
            lines = lines.map(l => {
                if (l.originalLineUuid) return l;
                const sameVariant = server.lines.filter(sl => sl.variantUuid === l.variantUuid && !sl.originalLineUuid);
                const match = sameVariant.find(sl => sl.lineNumber === l.lineNumber) ?? (sameVariant.length === 1 ? sameVariant[0] : undefined);
                if (!match) throw new Error(`Line ${l.lineNumber ?? '?'} of the original sale was not found on the server.`);
                return { ...l, originalLineUuid: match.uuid };
            });
            await this.db.returns.update(r.localId, { lines, originalOrderUuid });
        } else if (originalOrderUuid !== r.originalOrderUuid) {
            await this.db.returns.update(r.localId, { originalOrderUuid });
        }
        let request = r.request;
        if (!request.sessionUuid && r.sessionLocalId) {
            const s = await this.db.sessions.get(r.sessionLocalId);
            if (s?.uuid) request = { ...request, sessionUuid: s.uuid };
        }
        return { ...request, originalOrderUuid, lines: lines.map(l => ({ originalLineUuid: l.originalLineUuid!, quantity: l.quantity })) } as ReturnSyncRequest;
    }

    async applyReturnResult(r: LocalReturn, res: SyncResult): Promise<void> {
        if (!res.success) {
            await this.failReturn(r, new Error(res.error || 'The server refused this return.'), false);
            return;
        }
        await this.db.returns.update(r.localId, {
            syncStatus: 'synced',
            orderUuid: res.orderUuid ?? null,
            orderNumber: res.orderNumber ?? null,
            flags: res.flags ?? [],
            warnings: res.warnings ?? [],
            syncedAt: new Date().toISOString(),
            lastError: null,
            nextAttemptAt: null
        });
        await this.db.log(r.kind, true, `${r.originalOrderNumber} → ${res.orderNumber ?? 'synced'}${res.warnings?.length ? ` (${res.warnings.join('; ')})` : ''}`);
        // The store credit issued offline gets its server number: read it from the return order (best effort).
        if (r.receipt.storeCredit && res.orderUuid) {
            try {
                const ret = await this.api.orderAuto(res.orderUuid);
                if (ret.storeCredit) {
                    const sc = ret.storeCredit;
                    await this.db.returns.update(r.localId, { receipt: { ...r.receipt, storeCredit: { ...r.receipt.storeCredit, uuid: sc.uuid, number: sc.number, amount: sc.amount, balance: sc.balance, expiresAt: sc.expiresAt ?? null } } });
                }
            } catch {
                /* the offline number stays on the device */
            }
        }
    }

    private async failReturn(r: LocalReturn, e: unknown, retry: boolean): Promise<void> {
        const attempts = (r.attempts ?? 0) + 1;
        const msg = errorMessage(e);
        await this.db.returns.update(r.localId, {
            syncStatus: retry ? 'pending' : 'error',
            attempts,
            nextAttemptAt: retry ? Date.now() + backoffMs(attempts) : null,
            lastError: msg
        });
        await this.db.log(r.kind, false, `${r.originalOrderNumber}: ${msg}`);
    }

    async pushCashMovements(): Promise<void> {
        const rows = (await this.db.cashMovements.orderBy('createdAtLocal').toArray()).filter(m => this.due(m, false));
        for (const m of rows) {
            try {
                const res = await this.api.addCashMovement(m.shiftUuid, { direction: m.direction, amount: m.amount, reason: m.reason, note: m.note ?? null, localId: m.localId });
                await this.db.cashMovements.update(m.localId, { syncStatus: 'synced', uuid: res?.uuid ?? null, lastError: null });
                await this.db.log('CASH', true, `${m.direction} ${m.amount} (${m.reason})`);
            } catch (e) {
                const retry = isRetryable(e);
                const attempts = (m.attempts ?? 0) + 1;
                await this.db.cashMovements.update(m.localId, {
                    syncStatus: retry ? 'pending' : 'error',
                    attempts,
                    nextAttemptAt: retry ? Date.now() + backoffMs(attempts) : null,
                    lastError: errorMessage(e)
                });
                await this.db.log('CASH', false, errorMessage(e));
                if (isNetworkError(e)) return;
            }
        }
    }

    async pushSessions(): Promise<void> {
        const rows = (await this.db.sessions.toArray()).filter(s => this.due(s, false));
        for (const s of rows) {
            try {
                const res = await this.api.startSession(s.shiftUuid, s.localId);
                await this.db.sessions.update(s.localId, { syncStatus: 'synced', uuid: res.uuid, lastError: null });
            } catch (e) {
                const retry = isRetryable(e);
                const attempts = (s.attempts ?? 0) + 1;
                await this.db.sessions.update(s.localId, {
                    syncStatus: retry ? 'pending' : 'error',
                    attempts,
                    nextAttemptAt: retry ? Date.now() + backoffMs(attempts) : null,
                    lastError: errorMessage(e)
                });
                if (isNetworkError(e)) return;
            }
        }
    }

    /** Put refused rows back in the queue (settings screen "Retry"). */
    async retryErrors(): Promise<void> {
        const reset = { syncStatus: 'pending' as const, nextAttemptAt: null, attempts: 0 };
        await this.db.orders.where('syncStatus').equals('error').modify(reset);
        await this.db.cashMovements.where('syncStatus').equals('error').modify(reset);
        await this.db.sessions.where('syncStatus').equals('error').modify(reset);
        await this.db.returns.where('syncStatus').equals('error').modify(reset);
        await this.pushAll();
    }

    async refreshCounts(): Promise<void> {
        const [po, eo, pc, ec, pr, er] = await Promise.all([
            this.db.orders.where('syncStatus').anyOf('pending', 'syncing').count(),
            this.db.orders.where('syncStatus').equals('error').count(),
            this.db.cashMovements.where('syncStatus').anyOf('pending', 'syncing').count(),
            this.db.cashMovements.where('syncStatus').equals('error').count(),
            this.db.returns.where('syncStatus').anyOf('pending', 'syncing').count(),
            this.db.returns.where('syncStatus').equals('error').count()
        ]);
        this.pendingCount.set(po + pc + pr);
        this.errorCount.set(eo + ec + er);
    }

    // ── triggers ──────────────────────────────────────────────────────────────

    /** Back online: push the whole queue (chronological batch), then a delta pull. */
    async onReconnect(): Promise<void> {
        await this.db.log('CONNECTIVITY', true, 'Back online.');
        await this.pushAll();
        await this.pull(false);
    }

    /** Retry tick: due queue items while online. */
    async tick(): Promise<void> {
        if (!this.conn.online()) return;
        const due = await this.db.orders.where('syncStatus').equals('pending').count();
        const dueCash = await this.db.cashMovements.where('syncStatus').equals('pending').count();
        const dueSess = await this.db.sessions.where('syncStatus').equals('pending').count();
        const dueRet = await this.db.returns.where('syncStatus').equals('pending').count();
        if (due + dueCash + dueSess + dueRet > 0) await this.pushAll();
    }

    /** While the API is marked unreachable but the browser says online, try a light call to find out. */
    async probe(): Promise<void> {
        const terminal = this.state.terminal();
        if (!terminal || this.conn.online() || !this.conn.browserOnline()) return;
        try {
            await this.pullConfig(terminal.uuid); // success flips apiReachable → reconnected$ → onReconnect
        } catch {
            /* still offline */
        }
    }
}
