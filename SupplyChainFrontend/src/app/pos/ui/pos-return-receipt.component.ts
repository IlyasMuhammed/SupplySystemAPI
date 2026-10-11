import { DatePipe } from '@angular/common';
import { Component, OnDestroy, OnInit, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { PosReturnFlow } from '../core/pos-return-flow.service';
import { LocalReturn } from '../core/pos.models';
import { PosBarcodeComponent } from './pos-barcode.component';
import { PosLabelPipe, PosMoneyPipe, PosQtyPipe } from './pos-money.pipe';

/** Shared look of the A38 receipt paper (monospace slip, 80 mm when printed). */
export const RECEIPT_STYLES = `
    .paper { width: min(380px, 100%); margin: 0 auto; background: var(--sms-surface); border: 1px solid var(--sms-border); border-radius: var(--sms-radius); padding: 1.1rem; font-family: ui-monospace, SFMono-Regular, Consolas, monospace; font-size: 13px; }
    .c { text-align: center; }
    .ln { display: flex; justify-content: space-between; gap: .5rem; }
    hr { border: 0; border-top: 1px dashed var(--sms-border); margin: .5rem 0; }
    .b { font-weight: 700; font-size: 15px; }
    .pre { white-space: pre-line; }
    .sec { font-weight: 700; letter-spacing: .06em; margin-top: .25rem; }
`;

/**
 * A39 §8 — the return / exchange receipt (HTML, window.print()): returned lines, restocking fee, new items, net, refund
 * method or payments, and the store credit issued with its scannable number. Read from Dexie `returns`; the number
 * switches from "offline" to the server's once synced.
 */
@Component({
    selector: 'pos-return-receipt',
    standalone: true,
    imports: [DatePipe, PosMoneyPipe, PosQtyPipe, PosLabelPipe, PosBarcodeComponent],
    styles: [RECEIPT_STYLES],
    template: `
        <div class="pos-page">
            @if (ret(); as r) {
                <div class="row wrap">
                    <span class="pos-pill" [class.ok]="r.syncStatus === 'synced'" [class.wn]="r.syncStatus === 'pending' || r.syncStatus === 'syncing'" [class.er]="r.syncStatus === 'error'" data-testid="ret-receipt-sync">
                        {{ r.syncStatus === 'synced' ? 'Synced' : r.syncStatus === 'error' ? 'Sync failed' : 'Waiting to sync' }}
                    </span>
                    @for (f of r.flags ?? []; track f) { <span class="pos-pill wn">{{ f | posLabel }}</span> }
                    <span class="grow"></span>
                    <button class="pos-btn" type="button" (click)="print()" data-testid="ret-receipt-print"><i class="pi pi-print"></i> Print</button>
                    @if (r.receipt.storeCredit) {
                        <button class="pos-btn" type="button" (click)="openCredit(r)" data-testid="ret-receipt-credit"><i class="pi pi-ticket"></i> Store credit</button>
                    }
                    <button class="pos-btn" type="button" (click)="another()"><i class="pi pi-replay"></i> Another return</button>
                    <button class="pos-btn primary lg" type="button" (click)="newSale()" data-testid="ret-receipt-new"><i class="pi pi-plus"></i> New sale</button>
                </div>
                @if (r.lastError && r.syncStatus === 'error') { <p class="err-text">{{ r.lastError }}</p> }

                <div class="paper pos-receipt-paper" data-testid="ret-receipt-paper">
                    <div class="c b">{{ r.receipt.storeName }}</div>
                    @if (r.receipt.header) { <div class="c pre">{{ r.receipt.header }}</div> }
                    <div class="c b" style="margin-top: .3rem">{{ r.kind === 'EXCHANGE' ? 'EXCHANGE' : 'RETURN' }}</div>
                    <hr />
                    <div class="ln"><span>No.</span><span data-testid="ret-receipt-number">{{ r.orderNumber || 'Offline' }}</span></div>
                    <div class="ln"><span>Original</span><span>{{ r.originalOrderNumber }}</span></div>
                    <div class="ln"><span>Date</span><span>{{ r.createdAtLocal | date: 'dd MMM yyyy HH:mm' }}</span></div>
                    <div class="ln"><span>Till</span><span>{{ r.receipt.terminalName }}</span></div>
                    <div class="ln"><span>Cashier</span><span>{{ r.receipt.cashierName }}</span></div>
                    <div class="ln"><span>Customer</span><span>{{ r.receipt.customerName }}</span></div>
                    <div class="ln"><span>Reason</span><span>{{ r.receipt.reasonName }}</span></div>
                    @if (r.receipt.note) { <div class="pre">{{ r.receipt.note }}</div> }
                    @if (r.receipt.authorizedByName) { <div class="ln"><span>Authorized</span><span>{{ r.receipt.authorizedByName }}</span></div> }
                    <hr />
                    <div class="sec">RETURNED</div>
                    @for (l of r.receipt.returnLines; track $index) {
                        <div>{{ l.name }}</div>
                        <div class="ln"><span>&nbsp;&nbsp;{{ l.quantity | posQty }} returned</span><span>−{{ l.amount | posMoney }}</span></div>
                    }
                    @if (r.receipt.restockingFee > 0) { <div class="ln"><span>Restocking fee</span><span>+{{ r.receipt.restockingFee | posMoney }}</span></div> }
                    <div class="ln"><span>Returned value</span><span>−{{ r.receipt.returnValue | posMoney }}</span></div>
                    @if (r.kind === 'EXCHANGE') {
                        <hr />
                        <div class="sec">NEW ITEMS</div>
                        @for (l of r.receipt.newLines; track $index) {
                            <div>{{ l.name }}</div>
                            <div class="ln"><span>&nbsp;&nbsp;{{ l.quantity | posQty }} × {{ l.unitPrice | posMoney }}</span><span>{{ l.lineTotal | posMoney }}</span></div>
                        }
                        <div class="ln"><span>New items</span><span>{{ r.receipt.newTotal | posMoney }}</span></div>
                        <hr />
                        <div class="ln b"><span>{{ r.receipt.net > 0 ? 'PAID' : r.receipt.net < 0 ? 'REFUNDED' : 'EVEN' }}</span><span data-testid="ret-receipt-net">{{ abs(r.receipt.net) | posMoney: r.receipt.currencyCode }}</span></div>
                        @for (p of r.receipt.payments; track $index) {
                            <div class="ln"><span>{{ p.method | posLabel }}@if (p.reference) { ({{ p.reference }}) }</span><span>{{ p.amount | posMoney }}</span></div>
                        }
                        @if (change(r) > 0) { <div class="ln b"><span>Change</span><span>{{ change(r) | posMoney }}</span></div> }
                    } @else {
                        <hr />
                        <div class="ln b"><span>REFUND</span><span data-testid="ret-receipt-refund">{{ r.receipt.refundAmount | posMoney: r.receipt.currencyCode }}</span></div>
                    }
                    @if (r.receipt.refundMethod && r.receipt.refundAmount > 0) {
                        <div class="ln"><span>Refunded by</span><span>{{ r.receipt.refundMethod | posLabel }}@if (r.receipt.refundReference) { ({{ r.receipt.refundReference }}) }</span></div>
                    }
                    @if (r.receipt.storeCredit; as sc) {
                        <hr />
                        <div class="c sec">STORE CREDIT</div>
                        <div class="ln b"><span>Amount</span><span>{{ sc.amount | posMoney: r.receipt.currencyCode }}</span></div>
                        <div class="ln"><span>For</span><span>{{ sc.customerName || 'Bearer' }}</span></div>
                        @if (sc.expiresAt) { <div class="ln"><span>Valid until</span><span>{{ sc.expiresAt | date: 'dd MMM yyyy' }}</span></div> }
                        <pos-barcode [value]="sc.number"></pos-barcode>
                        @if (!sc.uuid) { <div class="c faint">Offline number — redeemable once synced</div> }
                    }
                    @if (r.receipt.footer) { <hr /><div class="c pre">{{ r.receipt.footer }}</div> }
                    @if (!r.orderNumber) { <hr /><div class="c">Offline receipt — final number assigned on sync</div> }
                </div>
            } @else {
                <p class="muted">Receipt not found on this device.</p>
                <button class="pos-btn primary" type="button" (click)="newSale()">New sale</button>
            }
        </div>
    `
})
export class PosReturnReceiptComponent implements OnInit, OnDestroy {
    private readonly route = inject(ActivatedRoute);
    private readonly router = inject(Router);
    private readonly flow = inject(PosReturnFlow);

    readonly ret = signal<LocalReturn | null>(null);
    private timer?: ReturnType<typeof setInterval>;

    ngOnInit(): void {
        const id = this.route.snapshot.paramMap.get('localId') ?? '';
        void this.load(id);
        this.flow.reset(); // the flow is done; a new return starts clean
        this.timer = setInterval(() => {
            if (this.ret()?.syncStatus === 'synced') clearInterval(this.timer);
            else void this.load(id);
        }, 3000);
    }

    ngOnDestroy(): void {
        clearInterval(this.timer);
    }

    private async load(id: string): Promise<void> {
        this.ret.set((await this.flow.get(id)) ?? null);
    }

    abs(v: number): number {
        return Math.abs(v);
    }

    change(r: LocalReturn): number {
        return r.receipt.payments.reduce((s, p) => s + (p.changeGiven || 0), 0);
    }

    print(): void {
        window.print();
    }

    openCredit(r: LocalReturn): void {
        void this.router.navigate(['/pos/credit-note', r.receipt.storeCredit?.uuid || r.localId]);
    }

    another(): void {
        void this.router.navigate(['/pos/return']);
    }

    newSale(): void {
        void this.router.navigate(['/pos/sell']);
    }
}
