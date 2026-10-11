import { Injectable, inject } from '@angular/core';
import { Tender, changeTotal, creditAmount, tenderError } from './pos-calculator';
import { PosApi } from './pos-api.service';
import { PosAuth } from './pos-auth.service';
import { PosCart, newId } from './pos-cart.service';
import { PosConnectivity } from './pos-connectivity.service';
import { PosDb } from './pos-db';
import { PosHttpError } from './pos-http.service';
import { POS_PERM, LocalOrder, SyncOrder } from './pos.models';
import { MSG } from './pos-return-rules';
import { PosShifts } from './pos-shift.service';
import { PosState } from './pos-state.service';
import { PosSync } from './pos-sync.service';

/** `POS-{store}-OFF-{short}` — the temporary number printed until the server assigns the real one (D-7). */
export function offlineNumber(storeCode: string, localId: string): string {
    return `POS-${storeCode}-OFF-${localId.replace(/-/g, '').slice(0, 8).toUpperCase()}`;
}

export interface CompleteSaleInput {
    tenders: Tender[];
    /** The ON_ACCOUNT credit check ran against cached data (offline). */
    offlineCredit: boolean;
}

/** Thrown when the server refused the sale while online (the order was not kept). */
export class SaleRefusedError extends Error {
    constructor(message: string) {
        super(message);
        this.name = 'SaleRefusedError';
    }
}

/**
 * A38 §5 / §17.3 — completes a sale: the order is written to Dexie first (it is never lost), then pushed.
 * Online, the push is awaited so a server refusal (credit limit, discount permission …) comes back to the payment screen
 * and the order is dropped; when the push cannot reach the server the order stays queued as an offline order.
 */
@Injectable({ providedIn: 'root' })
export class PosOrders {
    private readonly db = inject(PosDb);
    private readonly api = inject(PosApi);
    private readonly cart = inject(PosCart);
    private readonly state = inject(PosState);
    private readonly auth = inject(PosAuth);
    private readonly shifts = inject(PosShifts);
    private readonly sync = inject(PosSync);
    private readonly conn = inject(PosConnectivity);

    buildOrder(input: CompleteSaleInput): LocalOrder {
        const terminal = this.state.terminal();
        const store = this.state.store();
        const shift = this.state.shift();
        const cashier = this.auth.cashier();
        if (!terminal || !store) throw new Error('This device is not bound to a terminal.');
        if (!shift || shift.status !== 'OPEN') throw new Error('There is no open shift on this terminal.');
        if (!cashier) throw new Error('Sign in with your PIN first.');
        if (this.cart.isEmpty()) throw new Error('The cart is empty.');
        if (this.cart.hasDiscount() && !cashier.permissions.includes(POS_PERM.ORDER_DISCOUNT)) {
            throw new Error('You are not allowed to give discounts.');
        }
        const totals = this.cart.totals();
        const tenderProblem = tenderError(totals.grandTotal, input.tenders);
        if (tenderProblem) throw new Error(tenderProblem);
        // A39 POS-CN-04: store credit is redeemed online only.
        if (input.tenders.some(t => t.method === 'STORE_CREDIT') && !this.conn.online()) throw new Error(MSG.creditOffline);

        const localId = newId();
        const now = new Date().toISOString();
        const lines = this.cart.lines();
        const customer = this.cart.customer();
        const online = this.conn.online();

        const payload: SyncOrder = {
            localId,
            terminalUuid: terminal.uuid,
            shiftUuid: shift.uuid,
            sessionUuid: null,
            // POSB: lets the server attach an offline order to the offline session it does not know yet.
            sessionLocalId: this.shifts.sessionLocalId(),
            offlineNumber: offlineNumber(store.code, localId),
            createdAtLocal: now,
            isOffline: !online,
            customerUuid: customer?.uuid ?? null,
            lines: lines.map(l => ({
                localLineId: l.localLineId,
                variantUuid: l.variantUuid,
                quantity: l.quantity,
                unitPrice: l.unitPrice,
                discountType: l.discountType,
                discountValue: l.discountValue,
                barcode: l.barcode ?? null,
                note: l.note ?? null
            })),
            orderDiscountType: this.cart.orderDiscountType(),
            orderDiscountValue: this.cart.orderDiscountValue(),
            note: this.cart.note().trim() || null,
            payments: input.tenders.map(t => ({
                localId: t.localId,
                method: t.method,
                amount: t.amount,
                changeGiven: t.changeGiven,
                reference: t.reference ?? null,
                ...(t.method === 'STORE_CREDIT' ? { creditNoteUuid: t.creditNoteUuid ?? null } : {})
            })),
            offlineCredit: input.offlineCredit,
            clientTotals: { subtotal: totals.subtotal, discountAmount: totals.discountAmount, taxTotal: totals.taxTotal, grandTotal: totals.grandTotal },
            cashierUserId: cashier.userId
        };

        return {
            localId,
            syncStatus: 'pending',
            createdAtLocal: now,
            shiftUuid: shift.uuid,
            payload,
            sessionLocalId: this.shifts.sessionLocalId(),
            attempts: 0,
            receipt: {
                storeName: store.name,
                storeCode: store.code,
                terminalName: terminal.name,
                cashierName: cashier.name,
                customerName: customer?.name ?? 'Walk-in customer',
                currencyCode: this.state.currency(),
                taxInclusive: this.state.taxInclusive(),
                taxPercent: this.state.taxPercent(),
                header: store.receiptHeader ?? null,
                footer: store.receiptFooter ?? null,
                lines: lines.map((l, i) => ({
                    name: l.name,
                    sku: l.sku,
                    quantity: l.quantity,
                    unitPrice: l.unitPrice,
                    discountAmount: totals.lines[i].discountAmount,
                    lineTotal: totals.lines[i].lineTotal
                })),
                subtotal: totals.subtotal,
                discountAmount: totals.discountAmount,
                taxTotal: totals.taxTotal,
                grandTotal: totals.grandTotal,
                payments: input.tenders.map(t => ({ method: t.method, amount: t.amount, changeGiven: t.changeGiven, reference: t.reference ?? null })),
                changeTotal: changeTotal(input.tenders),
                creditAmount: creditAmount(input.tenders)
            }
        };
    }

    /** Saves the sale and pushes it. Resolves with the stored order; rejects with SaleRefusedError on an online refusal. */
    async completeSale(input: CompleteSaleInput): Promise<LocalOrder> {
        const order = this.buildOrder(input);
        // Resolve the session uuid now when it is known.
        if (order.sessionLocalId) {
            const s = await this.db.sessions.get(order.sessionLocalId);
            if (s?.uuid) order.payload.sessionUuid = s.uuid;
        }
        await this.db.orders.put(order);

        if (!order.payload.isOffline) {
            try {
                const result = await this.api.syncOrder(order.payload, 15000);
                if (!result.success) {
                    await this.db.orders.delete(order.localId);
                    throw new SaleRefusedError(result.error || 'The server refused this sale.');
                }
                await this.sync.applyResult(order, result);
            } catch (e) {
                if (e instanceof SaleRefusedError) throw e;
                // A rule refusal (400/403/409…) drops the sale; an expired session (401) or a server fault keeps it queued.
                if (e instanceof PosHttpError && !e.retryable && e.status !== 401) {
                    await this.db.orders.delete(order.localId);
                    throw new SaleRefusedError(e.message);
                }
                // Could not reach the server: keep it as an offline sale — except a store-credit redemption, which the
                // server refuses offline (A39): it stays an online request and is replayed with the same localId.
                const redeems = order.payload.payments.some(p => p.method === 'STORE_CREDIT');
                if (!redeems) order.payload = { ...order.payload, isOffline: true };
                await this.db.orders.update(order.localId, { payload: order.payload, attempts: 1, nextAttemptAt: Date.now() + 5000 });
            }
        }

        this.cart.clear();
        await this.sync.refreshCounts();
        if (order.payload.isOffline && this.conn.online()) void this.sync.pushAll();
        return (await this.db.orders.get(order.localId)) ?? order;
    }

    get(localId: string): Promise<LocalOrder | undefined> {
        return this.db.orders.get(localId);
    }

    /** Local orders of a shift, newest first (history screen shows the not-yet-synced ones). */
    async localForShift(shiftUuid: string): Promise<LocalOrder[]> {
        const rows = await this.db.orders.where('shiftUuid').equals(shiftUuid).toArray();
        return rows.sort((a, b) => b.createdAtLocal.localeCompare(a.createdAtLocal));
    }
}
