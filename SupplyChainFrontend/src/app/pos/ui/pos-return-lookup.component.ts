import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { PosAuth } from '../core/pos-auth.service';
import { PosConnectivity } from '../core/pos-connectivity.service';
import { PosDb } from '../core/pos-db';
import { errorMessage } from '../core/pos-http.service';
import { isWalkIn } from '../core/pos-payment-rules';
import { PosReturnFlow } from '../core/pos-return-flow.service';
import { orderKey } from '../core/pos-return-rules';
import { LocalOrder, POS_PERM, PosOrderListItem, SyncCustomer } from '../core/pos.models';
import { PosState } from '../core/pos-state.service';
import { PosMoneyPipe } from './pos-money.pipe';

/**
 * A39 §8 `/pos/return` — find the sale to return or exchange: scan / type the receipt number, search a customer and pick
 * one of their sales, or pick a recent sale of this device. Online any store of the organization (cross-store rules apply);
 * offline only sales kept on this device.
 */
@Component({
    selector: 'pos-return-lookup',
    standalone: true,
    imports: [FormsModule, RouterLink, DatePipe, PosMoneyPipe],
    template: `
        <div class="pos-page" data-testid="pos-return-lookup">
            <div class="row wrap">
                <a class="pos-btn" routerLink="/pos/sell"><i class="pi pi-arrow-left"></i> Sell</a>
                <h1 class="grow">Return or exchange</h1>
                <span class="pos-pill" [class.ok]="conn.online()" [class.wn]="!conn.online()">{{ conn.online() ? 'Online — any store' : 'Offline — sales of this device only' }}</span>
            </div>

            <div class="pos-card stack">
                <label class="pos-lbl" for="ret-receipt">Receipt number</label>
                <div class="row">
                    <input id="ret-receipt" class="pos-input grow" style="font-size: 1.2rem" placeholder="Scan the receipt barcode or type POS-…" autocomplete="off"
                        [(ngModel)]="receipt" (keyup.enter)="find()" autofocus data-testid="ret-receipt" />
                    <button class="pos-btn primary lg" type="button" (click)="find()" [disabled]="busy() || !receipt.trim()" data-testid="ret-find">
                        @if (busy()) { <i class="pi pi-spin pi-spinner"></i> } @else { <i class="pi pi-search"></i> } Find
                    </button>
                </div>
                @if (error()) { <p class="err-text" data-testid="ret-lookup-error">{{ error() }}</p> }
            </div>

            <div class="pos-card stack">
                <h2>Find by customer</h2>
                <input class="pos-input" type="search" placeholder="Name, code or phone" [(ngModel)]="customerQuery" (ngModelChange)="searchCustomers()" data-testid="ret-customer-search" />
                @if (customerResults().length && !customer()) {
                    <div class="pos-list">
                        @for (c of customerResults(); track c.uuid) {
                            <button class="pos-btn block" type="button" (click)="pickCustomer(c)"><span><strong>{{ c.name }}</strong> <span class="faint">{{ c.code }} {{ c.mobile || c.phone || '' }}</span></span></button>
                        }
                    </div>
                }
                @if (customer(); as c) {
                    <div class="row"><strong class="grow">{{ c.name }}</strong><button class="pos-btn ghost" type="button" (click)="clearCustomer()" aria-label="Clear customer"><i class="pi pi-times"></i></button></div>
                    <table class="pos-table" data-testid="ret-customer-orders">
                        <thead><tr><th>Receipt</th><th>Date</th><th>Store</th><th class="num">Total</th></tr></thead>
                        <tbody>
                            @for (o of customerServerOrders(); track o.uuid) {
                                <tr class="click" (click)="open(o.orderNumber)"><td class="mono">{{ o.orderNumber }}</td><td>{{ o.createdAt | date: 'dd MMM yyyy' }}</td><td>{{ o.storeName }}</td><td class="num">{{ o.grandTotal | posMoney }}</td></tr>
                            }
                            @for (o of customerLocalOrders(); track o.localId) {
                                <tr class="click" (click)="openLocal(o)"><td class="mono">{{ o.orderNumber || o.payload.offlineNumber }}</td><td>{{ o.createdAtLocal | date: 'dd MMM yyyy' }}</td><td>{{ o.receipt.storeName }} <span class="faint">(this till)</span></td><td class="num">{{ o.receipt.grandTotal | posMoney }}</td></tr>
                            }
                            @if (!customerServerOrders().length && !customerLocalOrders().length) { <tr><td colspan="4" class="faint">No sales found{{ conn.online() && canView() ? '' : ' on this device' }}.</td></tr> }
                        </tbody>
                    </table>
                    @if (customerError()) { <p class="faint">{{ customerError() }}</p> }
                }
            </div>

            <div class="pos-card">
                <h2>Recent sales on this till</h2>
                <table class="pos-table" data-testid="ret-recent">
                    <thead><tr><th>Receipt</th><th>Time</th><th>Customer</th><th class="num">Total</th></tr></thead>
                    <tbody>
                        @for (o of recent(); track o.localId) {
                            <tr class="click" (click)="openLocal(o)"><td class="mono">{{ o.orderNumber || o.payload.offlineNumber }}</td><td>{{ o.createdAtLocal | date: 'dd MMM HH:mm' }}</td><td>{{ o.receipt.customerName }}</td><td class="num">{{ o.receipt.grandTotal | posMoney }}</td></tr>
                        } @empty { <tr><td colspan="4" class="faint">No sales on this device yet.</td></tr> }
                    </tbody>
                </table>
            </div>
        </div>
    `
})
export class PosReturnLookupComponent implements OnInit {
    readonly conn = inject(PosConnectivity);
    readonly state = inject(PosState);
    private readonly auth = inject(PosAuth);
    private readonly flow = inject(PosReturnFlow);
    private readonly db = inject(PosDb);
    private readonly router = inject(Router);

    receipt = '';
    customerQuery = '';
    readonly busy = signal(false);
    readonly error = signal<string | null>(null);
    readonly recent = signal<LocalOrder[]>([]);
    readonly customerResults = signal<SyncCustomer[]>([]);
    readonly customer = signal<SyncCustomer | null>(null);
    readonly customerServerOrders = signal<PosOrderListItem[]>([]);
    readonly customerLocalOrders = signal<LocalOrder[]>([]);
    readonly customerError = signal<string | null>(null);
    readonly canView = computed(() => this.auth.has(POS_PERM.ORDER_VIEW));

    ngOnInit(): void {
        this.flow.reset();
        void this.flow.recentLocalSales(null, 20).then(r => this.recent.set(r));
    }

    async find(): Promise<void> {
        await this.open(this.receipt);
    }

    async openLocal(o: LocalOrder): Promise<void> {
        // Prefer the server's view (returnable quantities across all tills) when it knows the sale.
        if (o.orderNumber && this.conn.online()) return this.open(o.orderNumber);
        this.busy.set(true);
        this.error.set(null);
        try {
            await this.go(await this.flow.fromLocal(o));
        } catch (e) {
            this.error.set(errorMessage(e));
        } finally {
            this.busy.set(false);
        }
    }

    async open(receipt: string): Promise<void> {
        this.busy.set(true);
        this.error.set(null);
        try {
            await this.go(await this.flow.lookupReceipt(receipt));
        } catch (e) {
            this.error.set(errorMessage(e, 'The receipt could not be looked up.'));
        } finally {
            this.busy.set(false);
        }
    }

    private async go(found: Awaited<ReturnType<PosReturnFlow['lookupReceipt']>>): Promise<void> {
        const block = this.flow.blockOf(found);
        if (block) {
            this.error.set(block);
            return;
        }
        await this.flow.begin(found, 'RETURN');
        await this.router.navigate(['/pos/return', orderKey(found.order)]);
    }

    async searchCustomers(): Promise<void> {
        const q = this.customerQuery.trim().toLowerCase();
        this.customer.set(null);
        if (q.length < 2) {
            this.customerResults.set([]);
            return;
        }
        const all = await this.db.customers.toArray();
        this.customerResults.set(
            all
                .filter(c => c.isActive && !isWalkIn(c))
                .filter(c => c.name.toLowerCase().includes(q) || c.code.toLowerCase().includes(q) || (c.phone ?? '').includes(q) || (c.mobile ?? '').includes(q))
                .sort((a, b) => a.name.localeCompare(b.name))
                .slice(0, 20)
        );
    }

    async pickCustomer(c: SyncCustomer): Promise<void> {
        this.customer.set(c);
        this.customerError.set(null);
        this.customerServerOrders.set([]);
        const local = await this.flow.recentLocalSales(c.uuid, 20);
        if (this.conn.online() && this.canView()) {
            try {
                const server = await this.flow.serverSalesOf(c.code);
                this.customerServerOrders.set(server);
                const known = new Set(server.map(s => s.orderNumber));
                this.customerLocalOrders.set(local.filter(o => !o.orderNumber || !known.has(o.orderNumber)));
                return;
            } catch (e) {
                this.customerError.set(errorMessage(e, 'Synced sales could not be loaded.'));
            }
        } else if (this.conn.online()) {
            this.customerError.set('Sales from other tills are listed with the "view POS orders" permission — the receipt number always works.');
        }
        this.customerLocalOrders.set(local);
    }

    clearCustomer(): void {
        this.customer.set(null);
        this.customerQuery = '';
        this.customerResults.set([]);
    }
}
