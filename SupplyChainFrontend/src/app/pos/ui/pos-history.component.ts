import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { DialogModule } from 'primeng/dialog';
import { PosApi } from '../core/pos-api.service';
import { PosAuth } from '../core/pos-auth.service';
import { PosConnectivity } from '../core/pos-connectivity.service';
import { errorMessage } from '../core/pos-http.service';
import { PosOrders } from '../core/pos-orders.service';
import { canReturn, canVoid } from '../core/pos-return';
import { PosReturnFlow } from '../core/pos-return-flow.service';
import { LocalOrder, LocalReturn, OrderType, POS_PERM, PosOrder, PosOrderListItem } from '../core/pos.models';
import { PosState } from '../core/pos-state.service';
import { PosSync } from '../core/pos-sync.service';
import { PosLabelPipe, PosMoneyPipe, PosQtyPipe } from './pos-money.pipe';

export type HistoryFilter = 'ALL' | OrderType;

export const HISTORY_FILTERS: { key: HistoryFilter; label: string }[] = [
    { key: 'ALL', label: 'All' },
    { key: 'SALE', label: 'Sales' },
    { key: 'RETURN', label: 'Returns' },
    { key: 'EXCHANGE', label: 'Exchanges' }
];

/** Device-side rows shown by a filter (sales are LocalOrder, returns / exchanges LocalReturn). */
export function filterLocal(filter: HistoryFilter, sales: LocalOrder[], returns: LocalReturn[]): { sales: LocalOrder[]; returns: LocalReturn[] } {
    return {
        sales: filter === 'ALL' || filter === 'SALE' ? sales : [],
        returns: filter === 'ALL' ? returns : filter === 'SALE' ? [] : returns.filter(r => r.kind === filter)
    };
}

/**
 * A38 §17 / §5 — orders of this terminal's shift: queued (device) and synced (server, POS_ORDER_VIEW), detail and void
 * (POS_ORDER_VOID, same shift). A39: return / exchange filters and badges, the device's unsynced returns, return details
 * (reason, fee, authorization, store credit) and Return / Exchange buttons into the A39 flow (`/pos/return/:uuid`).
 */
@Component({
    selector: 'pos-history',
    standalone: true,
    imports: [FormsModule, DatePipe, DialogModule, PosMoneyPipe, PosQtyPipe, PosLabelPipe],
    template: `
        <div class="pos-page" data-testid="pos-history">
            <div class="row wrap">
                <h1 class="grow">Orders · {{ state.shift()?.shiftNumber || 'no shift' }}</h1>
                <input class="pos-input" style="max-width: 280px" type="search" placeholder="Order no. or customer" [(ngModel)]="search" (keyup.enter)="load()" />
                <button class="pos-btn" type="button" (click)="load()" [disabled]="busy()" aria-label="Refresh"><i class="pi pi-refresh"></i></button>
            </div>
            <div class="row wrap" role="radiogroup" aria-label="Order type">
                @for (f of filters; track f.key) {
                    <button class="pos-btn" type="button" [class.on]="filter() === f.key" (click)="setFilter(f.key)" [attr.data-testid]="'history-filter-' + f.key">{{ f.label }}</button>
                }
                <span class="grow"></span>
                @if (canStartReturn()) { <button class="pos-btn" type="button" (click)="router.navigate(['/pos/return'])" data-testid="history-new-return"><i class="pi pi-replay"></i> Return / exchange</button> }
            </div>

            @if (localSales().length || localReturns().length) {
                <div class="pos-card">
                    <h2>On this device, not synced yet</h2>
                    <table class="pos-table" data-testid="history-local">
                        <thead><tr><th>Number</th><th>Time</th><th>Customer</th><th class="num">Total</th><th>Status</th></tr></thead>
                        <tbody>
                            @for (o of localSales(); track o.localId) {
                                <tr class="click" (click)="openReceipt(o)">
                                    <td class="mono">{{ o.payload.offlineNumber }}</td><td>{{ o.createdAtLocal | date: 'HH:mm' }}</td><td>{{ o.receipt.customerName }}</td>
                                    <td class="num">{{ o.receipt.grandTotal | posMoney }}</td>
                                    <td><span class="pos-pill" [class.wn]="o.syncStatus !== 'error'" [class.er]="o.syncStatus === 'error'" [title]="o.lastError || ''">{{ o.syncStatus | posLabel }}</span></td>
                                </tr>
                            }
                            @for (r of localReturns(); track r.localId) {
                                <tr class="click" (click)="openReturnReceipt(r)" [attr.data-testid]="'history-local-return-' + r.kind">
                                    <td class="mono">{{ r.originalOrderNumber }} <span class="pos-pill" [class.in]="r.kind === 'RETURN'" [class.wn]="r.kind === 'EXCHANGE'">{{ r.kind === 'EXCHANGE' ? 'Exchange' : 'Return' }}</span></td>
                                    <td>{{ r.createdAtLocal | date: 'HH:mm' }}</td><td>{{ r.receipt.customerName }}</td>
                                    <td class="num">{{ r.receipt.net | posMoney }}</td>
                                    <td><span class="pos-pill" [class.wn]="r.syncStatus !== 'error'" [class.er]="r.syncStatus === 'error'" [title]="r.lastError || ''">{{ r.syncStatus | posLabel }}</span></td>
                                </tr>
                            }
                        </tbody>
                    </table>
                </div>
            }

            <div class="pos-card">
                @if (!canView()) {
                    <p class="muted">Viewing synced orders needs the "view POS orders" permission. Receipts of this device's sales stay available above.</p>
                } @else if (!conn.online()) {
                    <p class="muted">Synced orders are shown when the terminal is online.</p>
                }
                <table class="pos-table" data-testid="history-server">
                    <thead><tr><th>Number</th><th>Time</th><th>Cashier</th><th>Customer</th><th>Status</th><th class="num">Total</th></tr></thead>
                    <tbody>
                        @for (o of orders(); track o.uuid) {
                            <tr class="click" (click)="openDetail(o)" [class.sel]="detail()?.uuid === o.uuid">
                                <td class="mono">{{ o.orderNumber }}
                                    @if (o.orderType === 'RETURN') { <span class="pos-pill in" data-testid="history-badge-return">Return</span> }
                                    @if (o.orderType === 'EXCHANGE') { <span class="pos-pill wn" data-testid="history-badge-exchange">Exchange</span> }</td>
                                <td>{{ o.createdAt | date: 'HH:mm' }}</td><td>{{ o.cashierName }}</td><td>{{ o.customerName }}</td>
                                <td><span class="pos-pill" [class.ok]="o.status === 'COMPLETED'" [class.er]="o.status === 'VOIDED'" [class.wn]="o.status.includes('RETURNED')">{{ o.status | posLabel }}</span>
                                    @for (f of o.flags; track f) { <span class="pos-pill wn">{{ f | posLabel }}</span> }</td>
                                <td class="num">{{ o.grandTotal | posMoney }}</td>
                            </tr>
                        } @empty {
                            @if (canView() && conn.online()) { <tr><td colspan="6" class="faint">No orders yet.</td></tr> }
                        }
                    </tbody>
                </table>
                @if (hasMore()) { <button class="pos-btn block" type="button" (click)="more()" [disabled]="busy()">Load more</button> }
            </div>
            @if (error()) { <p class="err-text" data-testid="history-error">{{ error() }}</p> }
        </div>

        <!-- detail -->
        <p-dialog [visible]="!!detail()" (onHide)="detail.set(null)" [modal]="true" [header]="detail()?.orderNumber ?? ''" [style]="{ width: 'min(720px, 96vw)' }">
            @if (detail(); as d) {
                <div class="stack">
                    <div class="pos-kv">
                        <span>Type</span><span>{{ d.orderType | posLabel }}@if (d.isCrossStore) { <span class="pos-pill wn">Cross-store{{ d.originalStoreName ? ' · ' + d.originalStoreName : '' }}</span> }</span>
                        <span>Status</span><span>{{ d.status | posLabel }} · {{ d.paymentStatus | posLabel }}</span>
                        <span>Time</span><span>{{ d.createdAt | date: 'dd MMM HH:mm' }}</span>
                        <span>Cashier</span><span>{{ d.cashierName }}</span>
                        <span>Customer</span><span>{{ d.customerName }}</span>
                        @if (d.originalOrderNumber) { <span>Original</span><span>{{ d.originalOrderNumber }}</span> }
                        @if (d.returnReasonName || d.returnReason) { <span>Reason</span><span>{{ d.returnReasonName || d.returnReason }}</span> }
                        @if (d.restockingFeeAmount) { <span>Restocking fee</span><span>{{ d.restockingFeeAmount | posMoney }}</span> }
                        @if (d.authorizedByName) { <span>Authorized by</span><span>{{ d.authorizedByName }}{{ d.authorizedAt ? ' · ' : '' }}{{ d.authorizedAt | date: 'HH:mm' }}</span> }
                        @if (d.settlement) { <span>Settlement</span><span>{{ d.settlement | posLabel }}@if (d.netAmount !== null && d.netAmount !== undefined) { · {{ d.netAmount | posMoney }} }</span> }
                        @if (d.storeCredit; as sc) {
                            <span>Store credit</span>
                            <span><button class="pos-btn ghost" type="button" (click)="openCredit(sc.uuid)" data-testid="detail-store-credit"><i class="pi pi-ticket"></i> {{ sc.number }} · {{ sc.amount | posMoney }} (balance {{ sc.balance | posMoney }})</button></span>
                        }
                        @if (d.voidReason) { <span>Void reason</span><span>{{ d.voidReason }}</span> }
                        @if (d.flags.length) { <span>Flags</span><span>@for (f of d.flags; track f) { <span class="pos-pill wn">{{ f | posLabel }}</span> }</span> }
                    </div>
                    <table class="pos-table">
                        <thead><tr><th>Item</th><th class="num">Qty</th><th class="num">Price</th><th class="num">Total</th><th class="num">Returned</th></tr></thead>
                        <tbody>
                            @for (l of d.lines; track l.uuid) {
                                <tr><td>{{ l.productName }} <span class="faint">{{ l.sku }}</span></td><td class="num">{{ l.quantity | posQty }}</td>
                                    <td class="num">{{ l.unitPrice | posMoney }}</td><td class="num">{{ l.lineTotal | posMoney }}</td><td class="num">{{ l.returnedQuantity | posQty }}</td></tr>
                            }
                        </tbody>
                    </table>
                    <div class="pos-kv">
                        <span>Total</span><span><strong>{{ d.grandTotal | posMoney: state.currency() }}</strong></span>
                        @for (p of d.payments; track p.uuid) { <span>{{ p.isRefund ? 'Refund ' : '' }}{{ p.method | posLabel }}</span><span>{{ p.amount | posMoney }}@if (p.reference) { <span class="faint"> {{ p.reference }}</span> }</span> }
                    </div>
                    @if (detailError()) { <p class="err-text" data-testid="detail-error">{{ detailError() }}</p> }
                    <div class="row wrap">
                        @if (canVoidThis()) { <button class="pos-btn danger" type="button" (click)="voidOpen.set(true)" [disabled]="!conn.online()" data-testid="detail-void">Void</button> }
                        @if (canReturnThis()) {
                            <button class="pos-btn" type="button" (click)="startReturn(d, 'RETURN')" [disabled]="!conn.online()" data-testid="detail-return"><i class="pi pi-replay"></i> Return items</button>
                            <button class="pos-btn" type="button" (click)="startReturn(d, 'EXCHANGE')" [disabled]="!conn.online()" data-testid="detail-exchange"><i class="pi pi-sync"></i> Exchange</button>
                        }
                        <span class="grow"></span>
                        @if (!conn.online()) { <span class="faint">Void needs a connection; offline returns start from Return / exchange.</span> }
                    </div>
                </div>
            }
        </p-dialog>

        <!-- void -->
        <p-dialog [visible]="voidOpen()" (onHide)="voidOpen.set(false)" [modal]="true" header="Void order" [style]="{ width: 'min(420px, 95vw)' }">
            <div class="stack">
                <p class="muted">Voiding reverses the whole sale (stock back, payments refunded).</p>
                <label class="pos-lbl" for="void-reason">Reason</label>
                <textarea id="void-reason" class="pos-input" rows="2" maxlength="300" [(ngModel)]="voidReason" data-testid="void-reason"></textarea>
                <button class="pos-btn danger lg" type="button" (click)="doVoid()" [disabled]="busy() || !voidReason.trim()" data-testid="void-confirm">Void order</button>
            </div>
        </p-dialog>
    `
})
export class PosHistoryComponent implements OnInit {
    readonly state = inject(PosState);
    readonly conn = inject(PosConnectivity);
    readonly router = inject(Router);
    private readonly auth = inject(PosAuth);
    private readonly api = inject(PosApi);
    private readonly ordersSvc = inject(PosOrders);
    private readonly flow = inject(PosReturnFlow);
    private readonly sync = inject(PosSync);

    readonly filters = HISTORY_FILTERS;
    readonly filter = signal<HistoryFilter>('ALL');
    readonly busy = signal(false);
    readonly error = signal<string | null>(null);
    private readonly localAllSales = signal<LocalOrder[]>([]);
    private readonly localAllReturns = signal<LocalReturn[]>([]);
    readonly localSales = computed(() => filterLocal(this.filter(), this.localAllSales(), this.localAllReturns()).sales);
    readonly localReturns = computed(() => filterLocal(this.filter(), this.localAllSales(), this.localAllReturns()).returns);
    readonly orders = signal<PosOrderListItem[]>([]);
    readonly hasMore = signal(false);
    private page = 1;
    search = '';

    readonly detail = signal<PosOrder | null>(null);
    readonly detailError = signal<string | null>(null);
    readonly voidOpen = signal(false);
    voidReason = '';

    readonly canView = computed(() => this.auth.has(POS_PERM.ORDER_VIEW));
    readonly canStartReturn = computed(() => this.auth.has(POS_PERM.ORDER_RETURN) && this.state.features().returns && this.state.shift()?.status === 'OPEN');
    readonly canVoidThis = computed(() => {
        const d = this.detail();
        return !!d && this.auth.has(POS_PERM.ORDER_VOID) && canVoid(d, this.state.shift()?.uuid);
    });
    readonly canReturnThis = computed(() => {
        const d = this.detail();
        return !!d && this.canStartReturn() && canReturn(d);
    });

    constructor() {
        // A39: a return synced or processed elsewhere refreshes the lists.
        effect(() => {
            this.sync.returnsVersion();
            untracked(() => void this.loadLocal());
        });
    }

    ngOnInit(): void {
        void this.load();
    }

    setFilter(f: HistoryFilter): void {
        this.filter.set(f);
        void this.load();
    }

    async load(): Promise<void> {
        this.page = 1;
        await this.fetch(false);
    }

    async more(): Promise<void> {
        this.page++;
        await this.fetch(true);
    }

    private async loadLocal(): Promise<void> {
        const shift = this.state.shift();
        this.localAllSales.set(shift ? (await this.ordersSvc.localForShift(shift.uuid)).filter(o => o.syncStatus !== 'synced') : []);
        this.localAllReturns.set(shift ? (await this.flow.localForShift(shift.uuid)).filter(r => r.syncStatus !== 'synced') : []);
    }

    private async fetch(append: boolean): Promise<void> {
        const shift = this.state.shift();
        const terminal = this.state.terminal();
        this.error.set(null);
        await this.loadLocal();
        if (!this.canView() || !this.conn.online() || !terminal) {
            if (!append) this.orders.set([]);
            return;
        }
        this.busy.set(true);
        try {
            const f = this.filter();
            const res = await this.api.orders({ terminalUuid: terminal.uuid, shiftUuid: shift?.uuid, search: this.search.trim() || undefined, orderType: f === 'ALL' ? undefined : f, page: this.page, pageSize: 30 });
            const rows = f === 'ALL' ? res.data : res.data.filter(o => o.orderType === f);
            this.orders.set(append ? [...this.orders(), ...rows] : rows);
            this.hasMore.set(res.page * res.pageSize < res.totalRecords);
        } catch (e) {
            this.error.set(errorMessage(e, 'Orders could not be loaded.'));
        } finally {
            this.busy.set(false);
        }
    }

    openReceipt(o: LocalOrder): void {
        void this.router.navigate(['/pos/receipt', o.localId]);
    }

    openReturnReceipt(r: LocalReturn): void {
        void this.router.navigate(['/pos/return-receipt', r.localId]);
    }

    openCredit(uuid: string): void {
        this.detail.set(null);
        void this.router.navigate(['/pos/credit-note', uuid]);
    }

    async openDetail(o: PosOrderListItem): Promise<void> {
        this.detailError.set(null);
        try {
            this.detail.set(await this.api.order(o.uuid));
        } catch (e) {
            this.error.set(errorMessage(e, 'The order could not be loaded.'));
        }
    }

    async doVoid(): Promise<void> {
        const d = this.detail();
        if (!d) return;
        this.busy.set(true);
        this.detailError.set(null);
        try {
            this.detail.set(await this.api.voidOrder(d.uuid, this.voidReason.trim()));
            this.voidOpen.set(false);
            this.voidReason = '';
            await this.load();
        } catch (e) {
            this.voidOpen.set(false);
            this.detailError.set(errorMessage(e, 'The order could not be voided.'));
        } finally {
            this.busy.set(false);
        }
    }

    /** A39: the return / exchange flow on this sale (returnable lines and policy come from the server). */
    startReturn(d: PosOrder, mode: 'RETURN' | 'EXCHANGE'): void {
        this.detail.set(null);
        this.flow.reset();
        void this.router.navigate(['/pos/return', d.uuid], { queryParams: mode === 'EXCHANGE' ? { mode: 'EXCHANGE' } : {} });
    }
}
