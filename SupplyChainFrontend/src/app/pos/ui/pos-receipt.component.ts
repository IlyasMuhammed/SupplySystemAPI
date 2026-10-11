import { DatePipe } from '@angular/common';
import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { TooltipModule } from 'primeng/tooltip';
import { PosOrders } from '../core/pos-orders.service';
import { LocalOrder } from '../core/pos.models';
import { PosLabelPipe, PosMoneyPipe, PosQtyPipe } from './pos-money.pipe';

/** A38 §17.3 — the receipt (HTML, printed with window.print()); the number switches from the offline one once synced. */
@Component({
    selector: 'pos-receipt',
    standalone: true,
    imports: [DatePipe, TooltipModule, PosMoneyPipe, PosQtyPipe, PosLabelPipe],
    styles: [
        `
            .paper { width: min(380px, 100%); margin: 0 auto; background: var(--sms-surface); border: 1px solid var(--sms-border); border-radius: var(--sms-radius); padding: 1.1rem; font-family: ui-monospace, SFMono-Regular, Consolas, monospace; font-size: 13px; }
            .c { text-align: center; }
            .ln { display: flex; justify-content: space-between; gap: .5rem; }
            hr { border: 0; border-top: 1px dashed var(--sms-border); margin: .5rem 0; }
            .b { font-weight: 700; font-size: 15px; }
            .pre { white-space: pre-line; }
        `
    ],
    template: `
        <div class="pos-page">
            @if (order(); as o) {
                <div class="row wrap">
                    <span class="pos-pill" [class.ok]="o.syncStatus === 'synced'" [class.wn]="o.syncStatus === 'pending' || o.syncStatus === 'syncing'" [class.er]="o.syncStatus === 'error'" data-testid="receipt-sync">
                        {{ o.syncStatus === 'synced' ? 'Synced' : o.syncStatus === 'error' ? 'Sync failed' : 'Waiting to sync' }}
                    </span>
                    @for (f of o.flags ?? []; track f) { <span class="pos-pill wn">{{ f | posLabel }}</span> }
                    <span class="grow"></span>
                    <button class="pos-btn" type="button" (click)="print()" data-testid="receipt-print"><i class="pi pi-print"></i> Print</button>
                    <span pTooltip="Coming soon" tooltipPosition="bottom"><button class="pos-btn" type="button" disabled data-testid="receipt-email"><i class="pi pi-envelope"></i> Email</button></span>
                    <button class="pos-btn primary lg" type="button" (click)="newSale()" data-testid="receipt-new"><i class="pi pi-plus"></i> New sale</button>
                </div>
                @if (o.lastError && o.syncStatus === 'error') { <p class="err-text">{{ o.lastError }}</p> }

                <div class="paper pos-receipt-paper" data-testid="receipt-paper">
                    <div class="c b">{{ o.receipt.storeName }}</div>
                    @if (o.receipt.header) { <div class="c pre">{{ o.receipt.header }}</div> }
                    <hr />
                    <div class="ln"><span>No.</span><span data-testid="receipt-number">{{ o.orderNumber || o.payload.offlineNumber }}</span></div>
                    <div class="ln"><span>Date</span><span>{{ o.createdAtLocal | date: 'dd MMM yyyy HH:mm' }}</span></div>
                    <div class="ln"><span>Till</span><span>{{ o.receipt.terminalName }}</span></div>
                    <div class="ln"><span>Cashier</span><span>{{ o.receipt.cashierName }}</span></div>
                    <div class="ln"><span>Customer</span><span>{{ o.receipt.customerName }}</span></div>
                    <hr />
                    @for (l of o.receipt.lines; track $index) {
                        <div>{{ l.name }}</div>
                        <div class="ln"><span>&nbsp;&nbsp;{{ l.quantity | posQty }} × {{ l.unitPrice | posMoney }}@if (l.discountAmount > 0) { (−{{ l.discountAmount | posMoney }}) }</span><span>{{ l.lineTotal | posMoney }}</span></div>
                    }
                    <hr />
                    <div class="ln"><span>Subtotal</span><span>{{ o.receipt.subtotal | posMoney }}</span></div>
                    @if (o.receipt.discountAmount > 0) { <div class="ln"><span>Discount</span><span>−{{ o.receipt.discountAmount | posMoney }}</span></div> }
                    <div class="ln"><span>Tax {{ o.receipt.taxPercent }}%{{ o.receipt.taxInclusive ? ' incl.' : '' }}</span><span>{{ o.receipt.taxTotal | posMoney }}</span></div>
                    <div class="ln b"><span>TOTAL</span><span data-testid="receipt-total">{{ o.receipt.grandTotal | posMoney: o.receipt.currencyCode }}</span></div>
                    <hr />
                    @for (p of o.receipt.payments; track $index) {
                        <div class="ln"><span>{{ p.method | posLabel }}@if (p.reference) { ({{ p.reference }}) }</span><span>{{ p.amount | posMoney }}</span></div>
                    }
                    @if (o.receipt.changeTotal > 0) { <div class="ln b"><span>Change</span><span>{{ o.receipt.changeTotal | posMoney }}</span></div> }
                    @if (o.receipt.creditAmount > 0) { <div class="ln"><span>On account</span><span>{{ o.receipt.creditAmount | posMoney }}</span></div> }
                    @if (o.receipt.footer) { <hr /><div class="c pre">{{ o.receipt.footer }}</div> }
                    @if (!o.orderNumber) { <hr /><div class="c">Offline receipt — final number assigned on sync</div> }
                </div>
            } @else {
                <p class="muted">Receipt not found on this device.</p>
                <button class="pos-btn primary" type="button" (click)="newSale()">New sale</button>
            }
        </div>
    `
})
export class PosReceiptComponent implements OnInit, OnDestroy {
    private readonly route = inject(ActivatedRoute);
    private readonly router = inject(Router);
    private readonly orders = inject(PosOrders);

    readonly order = signal<LocalOrder | null>(null);
    private timer?: ReturnType<typeof setInterval>;

    ngOnInit(): void {
        const id = this.route.snapshot.paramMap.get('localId') ?? '';
        void this.load(id);
        // Pick up the server number once the background push lands.
        this.timer = setInterval(() => {
            if (this.order()?.syncStatus === 'synced') clearInterval(this.timer);
            else void this.load(id);
        }, 3000);
    }

    ngOnDestroy(): void {
        clearInterval(this.timer);
    }

    private async load(id: string): Promise<void> {
        this.order.set((await this.orders.get(id)) ?? null);
    }

    print(): void {
        window.print();
    }

    newSale(): void {
        void this.router.navigate(['/pos/sell']);
    }
}
