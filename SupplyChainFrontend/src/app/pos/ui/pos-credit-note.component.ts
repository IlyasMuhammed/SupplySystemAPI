import { DatePipe } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { PosApi } from '../core/pos-api.service';
import { PosConnectivity } from '../core/pos-connectivity.service';
import { PosDb } from '../core/pos-db';
import { PosState } from '../core/pos-state.service';
import { PosStoreCredits } from '../core/pos-store-credit.service';
import { PosBarcodeComponent } from './pos-barcode.component';
import { PosLabelPipe, PosMoneyPipe } from './pos-money.pipe';
import { RECEIPT_STYLES } from './pos-return-receipt.component';

/** What the voucher shows (from the server, the device cache or a return issued on this device). */
export interface CreditView {
    number: string;
    amount: number;
    balance: number;
    status: string;
    customerName?: string | null;
    issuedAt?: string | null;
    issuedAtStoreName?: string | null;
    expiresAt?: string | null;
    returnOrderNumber?: string | null;
    offline: boolean;
}

/**
 * A39 §8 `/pos/credit-note/:uuid` — the store credit voucher to show or print: amount, balance, owner (or bearer), expiry
 * and the number as a scannable Code 128 barcode. `:uuid` is the server uuid, or the localId of the return that issued it
 * on this device while offline.
 */
@Component({
    selector: 'pos-credit-note',
    standalone: true,
    imports: [DatePipe, PosMoneyPipe, PosLabelPipe, PosBarcodeComponent],
    styles: [RECEIPT_STYLES, `.amt { font-size: 26px; font-weight: 800; text-align: center; margin: .4rem 0; }`],
    template: `
        <div class="pos-page">
            <div class="row wrap">
                <button class="pos-btn" type="button" (click)="back()"><i class="pi pi-arrow-left"></i> Back</button>
                <h1 class="grow">Store credit</h1>
                @if (credit()) { <button class="pos-btn primary" type="button" (click)="print()" data-testid="cn-print"><i class="pi pi-print"></i> Print</button> }
            </div>
            @if (error()) { <p class="err-text" data-testid="cn-error">{{ error() }}</p> }
            @if (credit(); as c) {
                <div class="paper pos-receipt-paper" data-testid="cn-paper">
                    <div class="c b">{{ state.store()?.name }}</div>
                    <div class="c sec">STORE CREDIT</div>
                    <hr />
                    <div class="amt" data-testid="cn-balance">{{ c.balance | posMoney: state.currency() }}</div>
                    @if (c.balance !== c.amount) { <div class="c">of {{ c.amount | posMoney }} issued</div> }
                    <div class="c"><span class="pos-pill" [class.ok]="c.status === 'ACTIVE'" [class.er]="c.status !== 'ACTIVE'" data-testid="cn-status">{{ c.status | posLabel }}</span></div>
                    <hr />
                    <div class="ln"><span>For</span><span>{{ c.customerName || 'Bearer' }}</span></div>
                    @if (c.issuedAt) { <div class="ln"><span>Issued</span><span>{{ c.issuedAt | date: 'dd MMM yyyy' }}{{ c.issuedAtStoreName ? ' · ' + c.issuedAtStoreName : '' }}</span></div> }
                    @if (c.returnOrderNumber) { <div class="ln"><span>Return</span><span>{{ c.returnOrderNumber }}</span></div> }
                    <div class="ln"><span>Valid until</span><span>{{ c.expiresAt ? (c.expiresAt | date: 'dd MMM yyyy') : 'No expiry' }}</span></div>
                    <hr />
                    <pos-barcode [value]="c.number"></pos-barcode>
                    <hr />
                    <div class="c">Present this number at any of our stores. No cash value; no change is given.</div>
                    @if (c.offline) { <div class="c faint">Offline number — redeemable once this till has synced.</div> }
                </div>
            } @else if (!error()) {
                <p class="muted"><i class="pi pi-spin pi-spinner"></i> Loading…</p>
            }
        </div>
    `
})
export class PosCreditNoteComponent implements OnInit {
    readonly state = inject(PosState);
    private readonly route = inject(ActivatedRoute);
    private readonly router = inject(Router);
    private readonly db = inject(PosDb);
    private readonly api = inject(PosApi);
    private readonly conn = inject(PosConnectivity);
    private readonly credits = inject(PosStoreCredits);

    readonly credit = signal<CreditView | null>(null);
    readonly error = signal<string | null>(null);

    async ngOnInit(): Promise<void> {
        const id = this.route.snapshot.paramMap.get('uuid') ?? '';
        try {
            const v = await this.resolve(id);
            if (v) this.credit.set(v);
            else this.error.set('This store credit is not on this device.');
        } catch {
            this.error.set('This store credit could not be loaded.');
        }
    }

    private async resolve(id: string): Promise<CreditView | null> {
        // 1. Issued by a return on this device (offline number until synced).
        const byLocal = await this.db.returns.get(id);
        const fromReturn = byLocal ?? (await this.db.returns.filter(r => r.receipt.storeCredit?.uuid === id).first());
        if (this.conn.online() && (fromReturn?.receipt.storeCredit?.uuid || !fromReturn)) {
            // 2. The server's current balance (needs POS_CREDIT_NOTE_VIEW).
            const uuid = fromReturn?.receipt.storeCredit?.uuid ?? id;
            try {
                const c = await this.api.creditNote(uuid);
                await this.credits.cache([c]);
                return { number: c.number, amount: c.originalAmount, balance: c.remainingBalance, status: c.status, customerName: c.customerName, issuedAt: c.issuedAt, issuedAtStoreName: c.issuedAtStoreName, expiresAt: c.expiresAt, returnOrderNumber: c.returnOrderNumber, offline: false };
            } catch {
                /* fall back to what the device knows */
            }
        }
        // 3. The device cache.
        const cached = await this.db.storeCredits.get(id);
        if (cached) {
            return { number: cached.number, amount: cached.originalAmount, balance: cached.remainingBalance, status: cached.status, customerName: cached.customerName, issuedAt: cached.issuedAt, issuedAtStoreName: cached.issuedAtStoreName, expiresAt: cached.expiresAt, returnOrderNumber: cached.returnOrderNumber, offline: false };
        }
        const sc = fromReturn?.receipt.storeCredit;
        if (sc && fromReturn) {
            return { number: sc.number, amount: sc.amount, balance: sc.balance, status: 'ACTIVE', customerName: sc.customerName, issuedAt: fromReturn.createdAtLocal, issuedAtStoreName: fromReturn.receipt.storeName, expiresAt: sc.expiresAt, returnOrderNumber: fromReturn.orderNumber, offline: !sc.uuid };
        }
        return null;
    }

    print(): void {
        window.print();
    }

    back(): void {
        if (history.length > 1) history.back();
        else void this.router.navigate(['/pos/sell']);
    }
}
