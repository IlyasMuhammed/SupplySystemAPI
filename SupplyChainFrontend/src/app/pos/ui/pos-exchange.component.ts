import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { MessageService } from 'primeng/api';
import { DialogModule } from 'primeng/dialog';
import { itemLabel } from '../core/pos-cart.service';
import { PosConnectivity } from '../core/pos-connectivity.service';
import { PosDb } from '../core/pos-db';
import { errorMessage } from '../core/pos-http.service';
import { POS_TOAST_KEY } from '../core/pos-hub.service';
import { ManagerRequiredError, PosReturnFlow } from '../core/pos-return-flow.service';
import { orderKey } from '../core/pos-return-rules';
import { CatalogItem, ManagerReason } from '../core/pos.models';
import { PosState } from '../core/pos-state.service';
import { PosManagerAuthComponent } from './pos-manager-auth.component';
import { PosMoneyPipe, PosQtyPipe } from './pos-money.pipe';
import { PosProductGridComponent } from './pos-product-grid.component';

/**
 * A39 §8 `/pos/exchange/:orderUuid` — the exchange cart: the selling grid on the left; RETURNING (negative lines, less the
 * restocking fee) and NEW ITEMS on the right with the net settlement (D-6): PAY → the payment screen (STORE_CREDIT
 * included), EVEN → confirm here, REFUND → the refund method screen.
 */
@Component({
    selector: 'pos-exchange',
    standalone: true,
    imports: [FormsModule, RouterLink, DialogModule, PosMoneyPipe, PosQtyPipe, PosProductGridComponent, PosManagerAuthComponent],
    styles: [
        `
            :host { display: block; height: 100%; }
            .ex { display: grid; grid-template-columns: minmax(0, 1fr) 420px; height: 100%; min-height: 0; }
            .products { display: flex; flex-direction: column; min-height: 0; padding: .75rem; gap: .6rem; }
            .cart { display: flex; flex-direction: column; min-height: 0; border-left: 1px solid var(--sms-border); background: var(--sms-surface); }
            .head { padding: .6rem .75rem; border-bottom: 1px solid var(--sms-border); display: flex; align-items: center; gap: .5rem; }
            .lines { flex: 1 1 auto; overflow: auto; min-height: 0; }
            .sec { padding: .45rem .75rem; font-size: .75rem; font-weight: 700; letter-spacing: .06em; color: var(--sms-text-muted); background: var(--sms-surface-2); border-bottom: 1px solid var(--sms-border-soft); }
            .line { padding: .5rem .75rem; border-bottom: 1px solid var(--sms-border-soft); display: flex; gap: .5rem; align-items: center;
                .nm { flex: 1 1 auto; min-width: 0; font-weight: 600; } .sub { font-size: .8rem; color: var(--sms-text-muted); font-weight: 400; }
                .tot { font-weight: 700; font-variant-numeric: tabular-nums; } .neg { color: var(--sms-danger); } }
            .ctl { display: flex; gap: .3rem; align-items: center; }
            .totals { border-top: 1px solid var(--sms-border); padding: .6rem .75rem; display: grid; grid-template-columns: 1fr auto; gap: .2rem .75rem; font-variant-numeric: tabular-nums;
                .big { font-size: 1.3rem; font-weight: 750; } }
            .act { padding: 0 .75rem .75rem; display: flex; flex-direction: column; gap: .5rem; }
            .empty { padding: 1rem; color: var(--sms-text-faint); text-align: center; }
            @media (max-width: 899.98px) {
                .ex { grid-template-columns: 1fr; grid-template-rows: minmax(260px, 45vh) auto; height: auto; }
                .cart { border-left: 0; border-top: 1px solid var(--sms-border); }
            }
        `
    ],
    template: `
        <div class="ex" data-testid="pos-exchange">
            <section class="products" aria-label="New items">
                <div class="row">
                    <a class="pos-btn" [routerLink]="['/pos/return', key()]"><i class="pi pi-arrow-left"></i> Items</a>
                    <h1 class="grow">Exchange · <span class="mono">{{ flow.order()?.orderNumber }}</span></h1>
                </div>
                <pos-product-grid testIdPrefix="ex-" (picked)="add($event.item)" (code)="code($event)"></pos-product-grid>
            </section>

            <aside class="cart" aria-label="Exchange cart" data-testid="ex-cart">
                <div class="head"><i class="pi pi-user"></i><span class="grow">{{ flow.order()?.customerName || 'Walk-in customer' }}</span></div>
                <div class="lines">
                    <div class="sec">RETURNING</div>
                    @for (l of returning(); track l.key) {
                        <div class="line" [attr.data-testid]="'ex-ret-' + l.sku">
                            <span class="nm">{{ l.name }}<br /><span class="sub">{{ l.quantity | posQty }} returned</span></span>
                            <span class="tot neg">−{{ l.amount | posMoney }}</span>
                        </div>
                    }
                    @if (flow.summary().fee > 0) {
                        <div class="line"><span class="nm">Restocking fee <span class="sub">({{ flow.summary().feePct }}%)</span></span><span class="tot" data-testid="ex-fee">+{{ flow.summary().fee | posMoney }}</span></div>
                    }
                    <div class="sec">NEW ITEMS</div>
                    @for (l of flow.newLines(); track l.localLineId; let i = $index) {
                        <div class="line" [attr.data-testid]="'ex-new-' + l.sku">
                            <span class="nm">{{ l.name }}<br /><span class="sub">{{ l.quantity | posQty }} × {{ l.unitPrice | posMoney }}</span></span>
                            <span class="ctl">
                                <button class="pos-btn" type="button" (click)="flow.setNewQty(l.localLineId, l.quantity - 1)" [disabled]="l.isWeighable" aria-label="Less"><i class="pi pi-minus"></i></button>
                                <button class="pos-btn" type="button" (click)="flow.setNewQty(l.localLineId, l.quantity + 1)" [disabled]="l.isWeighable" aria-label="More"><i class="pi pi-plus"></i></button>
                                <button class="pos-btn ghost" type="button" (click)="flow.setNewQty(l.localLineId, 0)" aria-label="Remove"><i class="pi pi-trash"></i></button>
                            </span>
                            <span class="tot">{{ newLineTotal(i) | posMoney }}</span>
                        </div>
                    } @empty {
                        <div class="empty">Tap or scan the items the customer takes instead.</div>
                    }
                </div>
                <div class="totals" data-testid="ex-totals">
                    <span class="muted">Returning value</span><span data-testid="ex-return-value">−{{ flow.summary().refund | posMoney }}</span>
                    <span class="muted">New items (tax {{ state.taxPercent() }}%{{ state.taxInclusive() ? ' incl.' : '' }})</span><span data-testid="ex-new-total">{{ flow.newTotals().grandTotal | posMoney }}</span>
                    <span class="big">{{ netLabel() }}</span><span class="big" data-testid="ex-net">{{ absNet() | posMoney: state.currency() }}</span>
                </div>
                <div class="act">
                    @if (error()) { <p class="err-text" data-testid="ex-error">{{ error() }}</p> }
                    @if (!conn.online()) { <p class="faint"><i class="pi pi-ban"></i> Offline — store credit cannot be used to pay; the exchange syncs later.</p> }
                    <button class="pos-btn primary lg block" type="button" (click)="settle()" [disabled]="busy() || !flow.newLines().length" data-testid="ex-settle">
                        @if (busy()) { <i class="pi pi-spin pi-spinner"></i> }
                        @switch (flow.exchange().settlement) {
                            @case ('PAY') { PAY {{ flow.exchange().net | posMoney: state.currency() }} }
                            @case ('REFUND') { Refund {{ absNet() | posMoney: state.currency() }} }
                            @default { Confirm even exchange }
                        }
                    </button>
                </div>
            </aside>
        </div>

        <p-dialog [visible]="!!weighItem()" (onHide)="weighItem.set(null)" [modal]="true" [header]="weighItem() ? label(weighItem()!) : ''" [style]="{ width: 'min(380px, 95vw)' }">
            <div class="stack">
                <label class="pos-lbl" for="ex-weight">Quantity ({{ weighItem()?.uomCode || 'kg' }})</label>
                <input id="ex-weight" class="pos-input" type="number" inputmode="decimal" step="0.001" min="0" [(ngModel)]="weighQty" (keyup.enter)="confirmWeight()" autofocus />
                <button class="pos-btn primary lg block" type="button" [disabled]="!weighQty || weighQty <= 0" (click)="confirmWeight()">Add</button>
            </div>
        </p-dialog>

        <pos-manager-auth [visible]="authOpen()" [reasons]="authReasons()" [currency]="state.currency()" (authorized)="onAuthorized()" (cancelled)="authOpen.set(false)"></pos-manager-auth>
    `
})
export class PosExchangeComponent implements OnInit {
    readonly flow = inject(PosReturnFlow);
    readonly state = inject(PosState);
    readonly conn = inject(PosConnectivity);
    private readonly db = inject(PosDb);
    private readonly route = inject(ActivatedRoute);
    private readonly router = inject(Router);
    private readonly messages = inject(MessageService);

    readonly busy = signal(false);
    readonly error = signal<string | null>(null);
    readonly authOpen = signal(false);
    readonly authReasons = signal<ManagerReason[]>([]);
    readonly weighItem = signal<CatalogItem | null>(null);
    weighQty: number | null = null;
    label = itemLabel;

    readonly key = computed(() => {
        const o = this.flow.order();
        return o ? orderKey(o) : (this.route.snapshot.paramMap.get('orderUuid') ?? '');
    });
    readonly returning = computed(() => {
        const o = this.flow.order();
        if (!o) return [];
        return o.lines.filter(l => (this.flow.qty()[l.key] ?? 0) > 0).map(l => ({ key: l.key, sku: l.sku, name: l.productName, quantity: this.flow.qty()[l.key], amount: this.flow.lineAmount(l.key) }));
    });
    readonly absNet = computed(() => Math.abs(this.flow.exchange().net));
    readonly netLabel = computed(() => {
        const s = this.flow.exchange().settlement;
        return s === 'PAY' ? 'Customer pays' : s === 'REFUND' ? 'Refund to customer' : 'Even exchange';
    });

    ngOnInit(): void {
        const o = this.flow.order();
        const key = this.route.snapshot.paramMap.get('orderUuid') ?? '';
        if (!o || orderKey(o) !== key || this.flow.itemsError()) {
            void this.router.navigate(['/pos/return', key]);
            return;
        }
        this.flow.setMode('EXCHANGE');
    }

    newLineTotal(i: number): number {
        return this.flow.newTotals().lines[i]?.lineTotal ?? 0;
    }

    add(item: CatalogItem): void {
        this.error.set(null);
        if (item.isWeighable) {
            this.weighItem.set(item);
            this.weighQty = null;
            return;
        }
        this.flow.addNew(item, 1);
    }

    confirmWeight(): void {
        const item = this.weighItem();
        if (item && this.weighQty && this.weighQty > 0) this.flow.addNew(item, this.weighQty);
        this.weighItem.set(null);
    }

    /** Typed / scanned code: barcode first, then SKU. */
    async code(c: string): Promise<void> {
        const v = c.trim();
        let item = await this.db.catalog.where('barcode').equals(v).first();
        if (!item) item = await this.db.catalog.where('sku').equalsIgnoreCase(v).first();
        if (!item || !item.isActive) {
            this.messages.add({ key: POS_TOAST_KEY, severity: 'warn', summary: 'Not found', detail: `No product with barcode or SKU "${v}".`, life: 4000 });
            return;
        }
        this.add(item);
    }

    async settle(): Promise<void> {
        this.error.set(null);
        const problem = this.flow.exchangeError();
        if (problem) {
            this.error.set(problem);
            return;
        }
        const s = this.flow.exchange().settlement;
        if (s === 'PAY') {
            await this.router.navigate(['/pos/payment'], { queryParams: { exchange: this.key() } });
            return;
        }
        if (s === 'REFUND') {
            await this.router.navigate(['/pos/return', this.key(), 'refund']);
            return;
        }
        this.busy.set(true);
        try {
            const saved = await this.flow.submitExchange([]);
            await this.router.navigate(['/pos/return-receipt', saved.localId]);
        } catch (e) {
            if (e instanceof ManagerRequiredError) {
                this.authReasons.set(e.reasons);
                this.authOpen.set(true);
            } else this.error.set(errorMessage(e, 'The exchange could not be completed.'));
        } finally {
            this.busy.set(false);
        }
    }

    async onAuthorized(): Promise<void> {
        this.authOpen.set(false);
        await this.settle();
    }
}
