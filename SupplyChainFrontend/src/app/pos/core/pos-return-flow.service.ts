import { Injectable, computed, inject, signal } from '@angular/core';
import { Tender, calculateOrder, tenderError } from './pos-calculator';
import { PosApi } from './pos-api.service';
import { PosAuth } from './pos-auth.service';
import { CartLine, itemLabel, newId } from './pos-cart.service';
import { PosConnectivity } from './pos-connectivity.service';
import { PosDb } from './pos-db';
import { PosHttpError, isNetworkError } from './pos-http.service';
import {
    MSG,
    PolicyView,
    ReturnableOrder,
    allowedRefundMethods,
    exchangeBlock,
    exchangeNet,
    fromLocalOrder,
    fromLookup,
    lineReturnAmount,
    managerReasons,
    managerReasonsFromError,
    noteError,
    offlineCreditNumber,
    orderReturnBlock,
    qualifiesAsManager,
    quantityError,
    reasonsFor,
    returnSummary,
    storeCodeOfReceipt,
    storeCreditCustomer
} from './pos-return-rules';
import {
    CatalogItem,
    ExchangeRequest,
    LocalOrder,
    LocalReturn,
    LocalReturnReceipt,
    ManagerReason,
    POS_PERM,
    PaymentMethod,
    PosOrder,
    PosOrderListItem,
    ReturnReason,
    ReturnRequest,
    SyncPayment
} from './pos.models';
import { PosShifts } from './pos-shift.service';
import { PosState } from './pos-state.service';
import { PosSync } from './pos-sync.service';

export type FlowMode = 'RETURN' | 'EXCHANGE';

export interface FlowAuthorization {
    token: string | null;
    managerUserId: number;
    managerName: string;
    offline: boolean;
    /** Amount the authorization covers (online tokens carry a ceiling). */
    amount: number;
    reasons: ManagerReason[];
    expiresAt: number | null;
}

/** The server (or the rules) want a manager: the screen opens the authorization pad and submits again. */
export class ManagerRequiredError extends Error {
    constructor(readonly reasons: ManagerReason[]) {
        super(MSG.managerRequired);
        this.name = 'ManagerRequiredError';
    }
}

/** The server refused the return/exchange while online (nothing was kept). */
export class ReturnRefusedError extends Error {
    constructor(message: string) {
        super(message);
        this.name = 'ReturnRefusedError';
    }
}

/**
 * A39 C10 — the return / exchange in progress on this till (lookup → items + reason → [manager] → refund or exchange cart →
 * payment) and its submission. Online: `orders/lookup`, `returns/authorize`, `orders/{uuid}/return|exchange`. Offline: only
 * sales kept on this device, same store, PIN checked against the cached hashes, saved locally and pushed later through
 * `returns/sync`. Every submitted return is written to Dexie `returns` first (the receipt reads it from there).
 */
@Injectable({ providedIn: 'root' })
export class PosReturnFlow {
    private readonly api = inject(PosApi);
    private readonly db = inject(PosDb);
    private readonly state = inject(PosState);
    private readonly auth = inject(PosAuth);
    private readonly conn = inject(PosConnectivity);
    private readonly shifts = inject(PosShifts);
    private readonly sync = inject(PosSync);

    readonly order = signal<ReturnableOrder | null>(null);
    readonly policy = signal<PolicyView | null>(null);
    readonly mode = signal<FlowMode>('RETURN');
    readonly qty = signal<Record<string, number>>({});
    readonly reasonUuid = signal<string | null>(null);
    readonly note = signal('');
    readonly authorization = signal<FlowAuthorization | null>(null);
    readonly refundMethod = signal<PaymentMethod | null>(null);
    readonly refundReference = signal('');
    /** Exchange: the NEW ITEMS (no discounts on an exchange). */
    readonly newLines = signal<CartLine[]>([]);
    readonly reasons = signal<ReturnReason[]>([]);
    /** Stable while the flow is open: a retry after a timeout is idempotent. */
    private localId = newId();

    readonly reason = computed(() => this.reasons().find(r => r.uuid === this.reasonUuid()) ?? null);
    readonly reasonOptions = computed(() => reasonsFor(this.reasons(), this.mode()));
    readonly crossStore = computed(() => !!this.order() && !this.order()!.isSameStore);
    readonly summary = computed(() => returnSummary(this.order()?.lines ?? [], this.qty(), this.policy()?.policy ?? null, this.reason()));
    readonly newTotals = computed(() =>
        calculateOrder({ lines: this.newLines(), orderDiscountType: 'NONE', orderDiscountValue: 0, taxPercent: this.state.taxPercent(), taxInclusive: this.state.taxInclusive() })
    );
    readonly exchange = computed(() => exchangeNet(this.newTotals().grandTotal, this.summary().refund));
    /** What leaves the till as a refund: return ⇒ the refund; exchange ⇒ |net| when net < 0. */
    readonly refundAmount = computed(() => (this.mode() === 'RETURN' ? this.summary().refund : Math.max(0, -this.exchange().net)));
    /** Amount checked against maxReturnNoAuth and sent to `returns/authorize`: the return value (refund before netting). */
    readonly authAmount = computed(() => this.summary().refund);
    readonly managerNeeded = computed<ManagerReason[]>(() => {
        const p = this.policy();
        if (!p) return [];
        return managerReasons({ reason: this.reason(), amount: this.authAmount(), policy: p, kind: this.mode(), crossStore: this.crossStore() });
    });
    readonly authValid = computed(() => {
        const a = this.authorization();
        if (!a) return false;
        if (a.expiresAt && a.expiresAt < Date.now()) return false;
        return this.authAmount() <= a.amount + 0.001;
    });
    readonly refundMethods = computed<PaymentMethod[]>(() => {
        const o = this.order();
        if (!o) return [];
        return allowedRefundMethods({
            policy: this.policy()?.policy ?? null,
            payments: o.payments,
            enabled: this.state.paymentMethods(),
            creditNotesEnabled: this.state.creditNotesEnabled()
        });
    });

    // ── lifecycle ────────────────────────────────────────────────────────────

    reset(): void {
        this.order.set(null);
        this.policy.set(null);
        this.mode.set('RETURN');
        this.qty.set({});
        this.reasonUuid.set(null);
        this.note.set('');
        this.authorization.set(null);
        this.refundMethod.set(null);
        this.refundReference.set('');
        this.newLines.set([]);
        this.localId = newId();
    }

    /** Starts a flow on a looked-up order (keeps nothing of the previous one). */
    async begin(found: { order: ReturnableOrder; policy: PolicyView }, mode: FlowMode = 'RETURN'): Promise<void> {
        this.reset();
        this.order.set(found.order);
        this.policy.set(found.policy);
        this.mode.set(mode);
        await this.loadReasons();
    }

    setMode(mode: FlowMode): void {
        if (this.mode() === mode) return;
        this.mode.set(mode);
        if (this.reason() && !this.reasonOptions().some(r => r.uuid === this.reasonUuid())) this.reasonUuid.set(null);
        this.authorization.set(null);
    }

    async loadReasons(): Promise<void> {
        this.reasons.set(await this.db.returnReasons.toArray());
    }

    // ── lookup ───────────────────────────────────────────────────────────────

    /**
     * Receipt number (scanned or typed). Online: `orders/lookup` (any store of the organization), falling back to a sale on
     * this device that has not reached the server yet. Offline: sales on this device only; another store's receipt ⇒ the
     * cross-store message.
     */
    async lookupReceipt(receipt: string): Promise<{ order: ReturnableOrder; policy: PolicyView }> {
        const r = receipt.trim();
        if (!r) throw new Error('Scan or enter the receipt number.');
        const store = this.state.store();
        if (!store) throw new Error('This device is not bound to a terminal.');
        if (this.conn.online()) {
            try {
                const res = fromLookup(await this.api.lookupOrder(r, store.uuid));
                if (res) return res;
            } catch (e) {
                if (!isNetworkError(e) && !(e instanceof PosHttpError && (e.retryable || e.status === 404 || e.status === 401))) throw e;
            }
        }
        const local = await this.findLocal(r);
        if (local) return this.fromLocal(local);
        if (!this.conn.online()) {
            const code = storeCodeOfReceipt(r);
            if (code && code !== store.code.toUpperCase()) throw new Error(MSG.crossStoreOffline);
            throw new Error('This receipt is not on this device. Returns of other receipts need a connection.');
        }
        throw new Error(`Receipt ${r} was not found.`);
    }

    /** Re-opens a flow from its route key (after a reload): `local-{localId}` or a server order uuid. */
    async loadKey(key: string): Promise<{ order: ReturnableOrder; policy: PolicyView }> {
        if (key.startsWith('local-')) {
            const o = await this.db.orders.get(key.slice(6));
            if (!o) throw new Error('This sale is not on this device.');
            return this.fromLocal(o);
        }
        const store = this.state.store();
        if (!store) throw new Error('This device is not bound to a terminal.');
        if (!this.conn.online()) {
            const o = await this.db.orders.filter(x => x.orderUuid === key).first();
            if (o) return this.fromLocal(o);
            throw new Error('This sale is not on this device. Reconnect to return it.');
        }
        const res = fromLookup(await this.api.returnableLines(key, store.uuid));
        if (!res) throw new Error('The order was not found.');
        return res;
    }

    /** Sales of this device, newest first (offline lookup, "recent orders", customer search offline). */
    async recentLocalSales(customerUuid?: string | null, limit = 20): Promise<LocalOrder[]> {
        const all = await this.db.orders.orderBy('createdAtLocal').reverse().toArray();
        return all.filter(o => o.syncStatus !== 'error' && (!customerUuid || o.payload.customerUuid === customerUuid)).slice(0, limit);
    }

    /** Synced sales of a customer (online, POS_ORDER_VIEW): searched by customer code. */
    async serverSalesOf(customerCode: string): Promise<PosOrderListItem[]> {
        const res = await this.api.orders({ search: customerCode, orderType: 'SALE', page: 1, pageSize: 20 });
        return (res.data ?? []).filter(o => o.orderType === 'SALE');
    }

    private async findLocal(receipt: string): Promise<LocalOrder | null> {
        const r = receipt.trim().toUpperCase();
        const all = await this.db.orders.toArray();
        return all.find(o => (o.orderNumber ?? '').toUpperCase() === r || (o.payload.offlineNumber ?? '').toUpperCase() === r || o.localId === receipt.trim()) ?? null;
    }

    async fromLocal(o: LocalOrder): Promise<{ order: ReturnableOrder; policy: PolicyView }> {
        const prior = await this.db.returns.where('originalOrderLocalId').equals(o.localId).toArray();
        const byUuid = o.orderUuid ? await this.db.returns.where('originalOrderUuid').equals(o.orderUuid).toArray() : [];
        const seen = new Set(prior.map(p => p.localId));
        return fromLocalOrder(o, [...prior, ...byUuid.filter(p => !seen.has(p.localId))], this.state.returnPolicy(), this.state.store()?.uuid ?? '');
    }

    /** Why this order cannot go through the return flow at all, or null. */
    blockOf(found: { order: ReturnableOrder; policy: PolicyView }): string | null {
        const b = orderReturnBlock(found.order);
        if (b) return b;
        if (!found.policy.policy) return MSG.noPolicy;
        if (!found.order.isSameStore) {
            if (!this.conn.online() || found.order.source === 'local') return MSG.crossStoreOffline;
            if (!found.policy.crossStoreAllowed) return MSG.crossStoreRefused;
        }
        return null;
    }

    // ── items ────────────────────────────────────────────────────────────────

    setQty(key: string, value: number): void {
        const line = this.order()?.lines.find(l => l.key === key);
        if (!line) return;
        const q = Math.max(0, Math.min(Number(value) || 0, line.returnableQuantity));
        this.qty.update(m => ({ ...m, [key]: q }));
    }

    lineAmount(key: string): number {
        const l = this.order()?.lines.find(x => x.key === key);
        return l ? lineReturnAmount(l, this.qty()[key] ?? 0) : 0;
    }

    /** First problem with the item step (exact server wording where it exists), or null. */
    itemsError(): string | null {
        const o = this.order();
        if (!o) return 'Look the receipt up first.';
        const picked = o.lines.filter(l => (this.qty()[l.key] ?? 0) > 0);
        if (!picked.length) return 'Pick at least one item to return.';
        for (const l of picked) {
            const e = quantityError(l, this.qty()[l.key]);
            if (e) return e;
        }
        if (!this.reason()) return 'Pick a reason.';
        return noteError(this.reason(), this.note());
    }

    // ── exchange cart ────────────────────────────────────────────────────────

    addNew(item: CatalogItem, quantity = 1): void {
        const qty = item.isWeighable ? Math.round(quantity * 1000) / 1000 : Math.max(1, Math.round(quantity));
        const existing = this.newLines().find(l => l.variantUuid === item.variantUuid && !l.isWeighable);
        if (existing) {
            this.setNewQty(existing.localLineId, existing.quantity + qty);
            return;
        }
        this.newLines.update(ls => [
            ...ls,
            { localLineId: newId(), variantUuid: item.variantUuid, name: itemLabel(item), sku: item.sku, barcode: item.barcode ?? null, uomCode: item.uomCode ?? null, unitPrice: item.price, quantity: qty, discountType: 'NONE', discountValue: 0, note: null, isWeighable: item.isWeighable }
        ]);
    }

    setNewQty(localLineId: string, quantity: number): void {
        if (!(quantity > 0)) {
            this.newLines.update(ls => ls.filter(l => l.localLineId !== localLineId));
            return;
        }
        this.newLines.update(ls => ls.map(l => (l.localLineId === localLineId ? { ...l, quantity: l.isWeighable ? Math.round(quantity * 1000) / 1000 : Math.round(quantity) } : l)));
    }

    exchangeError(): string | null {
        const p = this.policy();
        if (!p) return MSG.noPolicy;
        return exchangeBlock(p) ?? this.itemsError() ?? (this.newLines().length ? null : 'Add the new items the customer takes.');
    }

    // ── manager authorization ───────────────────────────────────────────────

    /**
     * Checks a manager's PIN. Online (server order): `returns/authorize` ⇒ one-time token. Offline (or a sale known only to
     * this device): the cached PIN hashes; the manager must be a SUPERVISOR with POS_RETURN_OVERRIDE (or
     * POS_RETURN_CROSS_STORE when only cross-store needs it). Throws with the message to show.
     */
    async authorize(pin: string, reasons: ManagerReason[]): Promise<FlowAuthorization> {
        const o = this.order();
        const terminal = this.state.terminal();
        if (!o || !terminal) throw new Error('Look the receipt up first.');
        const amount = this.authAmount();
        if (this.conn.online() && o.source === 'server' && o.uuid) {
            try {
                const res = await this.api.authorizeReturn({ terminalUuid: terminal.uuid, originalOrderUuid: o.uuid, amount, managerUserId: null, pin });
                const a: FlowAuthorization = {
                    token: res.authorizationToken,
                    managerUserId: res.managerUserId,
                    managerName: res.managerName,
                    offline: false,
                    amount,
                    reasons,
                    expiresAt: res.expiresAt ? new Date(res.expiresAt).getTime() : null
                };
                this.authorization.set(a);
                return a;
            } catch (e) {
                if (!isNetworkError(e)) throw e;
                if (o.isSameStore === false) throw new Error(MSG.crossStoreOffline);
                // unreachable ⇒ the offline check below
            }
        }
        const res = await this.auth.verifyStaffPinOffline(pin);
        if (!res.ok) throw new Error(res.message);
        if (!qualifiesAsManager(res.staff, reasons)) throw new Error(`${res.staff.name} cannot authorize returns (a supervisor with the return-override permission is needed).`);
        const a: FlowAuthorization = { token: null, managerUserId: res.staff.userId, managerName: res.staff.name, offline: true, amount, reasons, expiresAt: null };
        this.authorization.set(a);
        return a;
    }

    // ── submit ───────────────────────────────────────────────────────────────

    /** Return: refund `refundAmount()` by `refundMethod()`. */
    submitReturn(): Promise<LocalReturn> {
        return this.submit('RETURN', []);
    }

    /** Exchange: tenders cover a positive net (PAY); a negative net is refunded by `refundMethod()`. */
    submitExchange(tenders: Tender[] = []): Promise<LocalReturn> {
        return this.submit('EXCHANGE', tenders);
    }

    private async submit(kind: FlowMode, tenders: Tender[]): Promise<LocalReturn> {
        const o = this.order();
        const policy = this.policy();
        const terminal = this.state.terminal();
        const store = this.state.store();
        const shift = this.state.shift();
        const cashier = this.auth.cashier();
        if (!o || !policy) throw new Error('Look the receipt up first.');
        if (!terminal || !store) throw new Error('This device is not bound to a terminal.');
        if (!shift || shift.status !== 'OPEN') throw new Error('There is no open shift on this terminal.');
        if (!cashier) throw new Error('Sign in with your PIN first.');
        if (!cashier.permissions.includes(POS_PERM.ORDER_RETURN)) throw new Error('You are not allowed to process returns.');
        const block = this.blockOf({ order: o, policy });
        if (block) throw new Error(block);
        const problem = kind === 'EXCHANGE' ? this.exchangeError() : this.itemsError();
        if (problem) throw new Error(problem);
        const need = this.managerNeeded();
        if (need.length && !this.authValid()) throw new ManagerRequiredError(need);

        const summary = this.summary();
        const ex = this.exchange();
        const newTotal = kind === 'EXCHANGE' ? this.newTotals().grandTotal : 0;
        const refundAmount = kind === 'RETURN' ? summary.refund : Math.max(0, -ex.net);
        const methods = this.refundMethods();
        let refundMethod = this.refundMethod();
        if (refundAmount > 0) {
            if (!refundMethod) throw new Error('Pick a refund method.');
            if (!methods.includes(refundMethod)) throw new Error(MSG.refundMethodRefused);
        } else {
            // Nothing to refund (exchange PAY/EVEN): any allowed method satisfies the request shape.
            refundMethod = refundMethod && methods.includes(refundMethod) ? refundMethod : (methods[0] ?? 'CASH');
        }
        if (kind === 'EXCHANGE' && ex.net > 0) {
            const t = tenderError(ex.net, tenders);
            if (t) throw new Error(t);
        }
        const usesStoreCredit = tenders.some(t => t.method === 'STORE_CREDIT');
        if (usesStoreCredit && !this.conn.online()) throw new Error(MSG.creditOffline);

        const sessionLocalId = this.shifts.sessionLocalId();
        const session = sessionLocalId ? await this.db.sessions.get(sessionLocalId) : undefined;
        const auth = this.authValid() ? this.authorization() : null;
        const now = new Date().toISOString();
        const localId = this.localId;
        const picked = o.lines.filter(l => (this.qty()[l.key] ?? 0) > 0);
        const creditCustomer = storeCreditCustomer(o);

        const base: ReturnRequest = {
            localId,
            terminalUuid: terminal.uuid,
            shiftUuid: shift.uuid,
            sessionUuid: session?.uuid ?? null,
            reasonUuid: this.reasonUuid()!,
            note: this.note().trim() || null,
            authorizationToken: auth?.token ?? null,
            lines: [],
            refundMethod: refundMethod!,
            refundReference: this.refundReference().trim() || null,
            storeCreditCustomerUuid: creditCustomer
        };
        const payments: SyncPayment[] = tenders.map(t => ({ localId: t.localId, method: t.method, amount: t.amount, changeGiven: t.changeGiven, reference: t.reference ?? null, creditNoteUuid: t.creditNoteUuid ?? null }));
        const exchangePart =
            kind === 'EXCHANGE'
                ? {
                      newLines: this.newLines().map(l => ({ localLineId: l.localLineId, variantUuid: l.variantUuid, quantity: l.quantity, unitPrice: l.unitPrice, discountType: 'NONE' as const, discountValue: 0, barcode: l.barcode ?? null, note: null })),
                      orderDiscountType: 'NONE' as const,
                      orderDiscountValue: 0,
                      payments
                  }
                : {};

        const issuesCredit = refundMethod === 'STORE_CREDIT' && refundAmount > 0;
        const expiryDays = policy.policy?.creditNoteExpiryDays ?? null;
        const receipt: LocalReturnReceipt = {
            storeName: store.name,
            storeCode: store.code,
            terminalName: terminal.name,
            cashierName: cashier.name,
            customerName: o.customerName || 'Walk-in customer',
            currencyCode: this.state.currency(),
            header: store.receiptHeader ?? null,
            footer: store.receiptFooter ?? null,
            reasonName: this.reason()?.name ?? '',
            note: base.note,
            authorizedByName: auth?.managerName ?? null,
            returnLines: picked.map(l => ({ name: l.productName, sku: l.sku, quantity: this.qty()[l.key], amount: lineReturnAmount(l, this.qty()[l.key]) })),
            newLines:
                kind === 'EXCHANGE'
                    ? this.newLines().map((l, i) => ({ name: l.name, sku: l.sku, quantity: l.quantity, unitPrice: l.unitPrice, lineTotal: this.newTotals().lines[i]?.lineTotal ?? 0 }))
                    : [],
            returnTotal: summary.subtotal,
            restockingFee: summary.fee,
            returnValue: summary.refund,
            newTotal,
            net: kind === 'EXCHANGE' ? ex.net : -summary.refund,
            settlement: kind === 'EXCHANGE' ? ex.settlement : null,
            refundMethod: refundAmount > 0 ? refundMethod : null,
            refundAmount,
            refundReference: base.refundReference,
            payments: tenders.map(t => ({ method: t.method, amount: t.amount, changeGiven: t.changeGiven, reference: t.reference ?? null })),
            storeCredit: issuesCredit
                ? {
                      uuid: null,
                      number: offlineCreditNumber(store.code, localId),
                      amount: refundAmount,
                      balance: refundAmount,
                      expiresAt: expiryDays ? new Date(Date.now() + expiryDays * 86_400_000).toISOString() : null,
                      customerName: creditCustomer ? o.customerName : null
                  }
                : null
        };

        const online = this.conn.online() && o.source === 'server' && !!o.uuid;
        const record: LocalReturn = {
            localId,
            kind,
            syncStatus: online ? 'syncing' : 'pending',
            createdAtLocal: now,
            shiftUuid: shift.uuid,
            isOffline: !online,
            originalOrderUuid: o.uuid,
            originalOrderLocalId: o.localId,
            originalOrderNumber: o.orderNumber,
            request: {
                ...base,
                ...exchangePart,
                kind,
                createdAtLocal: now,
                isOffline: !online,
                authorizedByUserId: auth?.managerUserId ?? null,
                cashierUserId: cashier.userId,
                sessionLocalId,
                // POSR: the number printed offline; the server's number replaces it and lookup finds either.
                storeCreditOfflineNumber: issuesCredit ? receipt.storeCredit!.number : null
            } as LocalReturn['request'],
            lines: picked.map(l => ({ originalLineUuid: l.lineUuid, localLineId: l.localLineId, lineNumber: l.lineNumber, variantUuid: l.variantUuid, quantity: this.qty()[l.key], amount: lineReturnAmount(l, this.qty()[l.key]) })),
            receipt,
            attempts: 0,
            sessionLocalId
        };
        await this.db.returns.put(record);

        if (online) {
            const lines = picked.map(l => ({ originalLineUuid: l.lineUuid!, quantity: this.qty()[l.key] }));
            try {
                const res: PosOrder =
                    kind === 'EXCHANGE'
                        ? await this.api.exchangeOrder(o.uuid!, { ...base, ...(exchangePart as Omit<ExchangeRequest, keyof ReturnRequest>), lines })
                        : await this.api.returnOrder(o.uuid!, { ...base, lines });
                await this.applyServerOrder(record, res);
            } catch (e) {
                if (e instanceof PosHttpError && !e.retryable && e.status !== 401) {
                    await this.db.returns.delete(localId);
                    const reasons = managerReasonsFromError(e.body);
                    if (reasons) {
                        this.authorization.set(null);
                        throw new ManagerRequiredError(reasons);
                    }
                    // POSR: tokens are single-use, 5 minutes, capped — authorize again.
                    if (e.message === MSG.authInvalid || e.message === MSG.authExceeded) {
                        const prev = this.authorization()?.reasons ?? [];
                        this.authorization.set(null);
                        throw new ManagerRequiredError(prev.length ? prev : need.length ? need : ['AMOUNT']);
                    }
                    throw new ReturnRefusedError(e.message);
                }
                // Could not reach the server.
                if (!o.isSameStore) {
                    await this.db.returns.delete(localId);
                    throw new Error(MSG.crossStoreOffline);
                }
                // A store-credit redemption must not turn into an offline order (the server refuses it): keep it queued as
                // an online request with the same localId (idempotent replay).
                const asOffline = !usesStoreCredit;
                await this.db.returns.update(localId, {
                    syncStatus: 'pending',
                    isOffline: asOffline,
                    request: { ...record.request, isOffline: asOffline, authorizedByUserId: auth?.managerUserId ?? null },
                    attempts: 1,
                    nextAttemptAt: Date.now() + 5000
                });
            }
        }

        await this.sync.refreshCounts();
        this.sync.returnsVersion.update(v => v + 1);
        if (this.conn.online()) void this.sync.pushAll();
        const saved = (await this.db.returns.get(localId)) ?? record;
        this.localId = newId();
        return saved;
    }

    private async applyServerOrder(record: LocalReturn, res: PosOrder): Promise<void> {
        const sc = res.storeCredit;
        const receipt: LocalReturnReceipt = {
            ...record.receipt,
            authorizedByName: res.authorizedByName ?? record.receipt.authorizedByName ?? null,
            restockingFee: res.restockingFeeAmount ?? record.receipt.restockingFee,
            storeCredit: sc
                ? { uuid: sc.uuid, number: sc.number, amount: sc.amount, balance: sc.balance, expiresAt: sc.expiresAt ?? null, customerName: record.receipt.storeCredit?.customerName ?? null }
                : record.receipt.storeCredit
        };
        await this.db.returns.update(record.localId, {
            syncStatus: 'synced',
            isOffline: false,
            orderUuid: res.uuid,
            orderNumber: res.orderNumber,
            flags: res.flags ?? [],
            syncedAt: new Date().toISOString(),
            receipt,
            lastError: null
        });
    }

    get(localId: string): Promise<LocalReturn | undefined> {
        return this.db.returns.get(localId);
    }

    /** Device-side returns of a shift, newest first (history). */
    async localForShift(shiftUuid: string): Promise<LocalReturn[]> {
        const rows = await this.db.returns.where('shiftUuid').equals(shiftUuid).toArray();
        return rows.sort((a, b) => b.createdAtLocal.localeCompare(a.createdAtLocal));
    }
}
