import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { PosConnectivity } from '../core/pos-connectivity.service';
import { errorMessage } from '../core/pos-http.service';
import { FlowMode, PosReturnFlow } from '../core/pos-return-flow.service';
import { MANAGER_REASON_TEXT, exchangeBlock, orderKey } from '../core/pos-return-rules';
import { ManagerReason } from '../core/pos.models';
import { PosState } from '../core/pos-state.service';
import { PosManagerAuthComponent } from './pos-manager-auth.component';
import { PosLabelPipe, PosMoneyPipe, PosQtyPipe } from './pos-money.pipe';

/**
 * A39 §8 `/pos/return/:orderUuid` — items + reason: returnable quantities per line, the reason (a note when it requires
 * one), the live summary (subtotal, restocking fee per D-5 incl. the reason's override, refund with the A38 remainder rule),
 * and manager authorization when the rules need it. Continues to the refund method (return) or the exchange cart.
 */
@Component({
    selector: 'pos-return-items',
    standalone: true,
    imports: [FormsModule, RouterLink, DatePipe, PosMoneyPipe, PosQtyPipe, PosLabelPipe, PosManagerAuthComponent],
    styles: [
        `
            .reasons { display: grid; grid-template-columns: repeat(auto-fill, minmax(170px, 1fr)); gap: .5rem; }
            .reasons .pos-btn { justify-content: flex-start; text-align: left; }
            .qty { display: flex; align-items: center; gap: .3rem; justify-content: flex-end; .v { min-width: 3rem; text-align: center; font-weight: 650; } }
            .sum { display: grid; grid-template-columns: 1fr auto; gap: .3rem 1rem; font-variant-numeric: tabular-nums; .big { font-size: 1.35rem; font-weight: 750; } }
            .layout { display: grid; grid-template-columns: minmax(0, 1fr) 320px; gap: 1rem; align-items: start; }
            @media (max-width: 899.98px) { .layout { grid-template-columns: 1fr; } }
        `
    ],
    template: `
        <div class="pos-page" data-testid="pos-return-items">
            <div class="row wrap">
                <a class="pos-btn" routerLink="/pos/return"><i class="pi pi-arrow-left"></i> Find another</a>
                <h1 class="grow">{{ flow.mode() === 'EXCHANGE' ? 'Exchange' : 'Return' }} · <span class="mono">{{ o()?.orderNumber }}</span></h1>
                <div class="row" role="radiogroup" aria-label="Return or exchange">
                    <button class="pos-btn" type="button" [class.on]="flow.mode() === 'RETURN'" (click)="setMode('RETURN')" data-testid="ret-mode-return"><i class="pi pi-replay"></i> Return</button>
                    <button class="pos-btn" type="button" [class.on]="flow.mode() === 'EXCHANGE'" (click)="setMode('EXCHANGE')" [disabled]="!!exchangeBlocked()" [title]="exchangeBlocked() || ''" data-testid="ret-mode-exchange"><i class="pi pi-sync"></i> Exchange</button>
                </div>
            </div>
            @if (loadError()) { <p class="err-text" data-testid="ret-load-error">{{ loadError() }}</p> }

            @if (o(); as o) {
                <div class="pos-card pos-kv">
                    <span>Sold</span><span>{{ o.createdAt | date: 'dd MMM yyyy HH:mm' }} · {{ o.daysSincePurchase }} day{{ o.daysSincePurchase === 1 ? '' : 's' }} ago</span>
                    <span>Store</span><span>{{ o.storeName }} @if (!o.isSameStore) { <span class="pos-pill wn" data-testid="ret-cross-store">Other store</span> } @if (o.source === 'local') { <span class="pos-pill">This till</span> }</span>
                    <span>Customer</span><span>{{ o.customerName || 'Walk-in customer' }}</span>
                    <span>Paid</span><span>@for (p of o.payments; track $index) { {{ p.method | posLabel }} {{ p.amount | posMoney }}{{ $last ? '' : ' · ' }} }</span>
                    <span>Window</span>
                    <span data-testid="ret-window">
                        @if (windowOk()) {
                            <span class="pos-pill ok">Within the {{ flow.mode() === 'EXCHANGE' ? 'exchange' : 'return' }} window</span>
                            @if (p()?.windowDaysRemaining !== null && p()?.windowDaysRemaining !== undefined && flow.mode() === 'RETURN') { <span class="faint"> {{ p()?.windowDaysRemaining }} days left</span> }
                        } @else {
                            <span class="pos-pill er">Window expired — manager needed</span>
                        }
                    </span>
                </div>

                <div class="layout">
                    <div class="stack">
                        <div class="pos-card">
                            <table class="pos-table" data-testid="ret-lines">
                                <thead><tr><th>Item</th><th class="num">Sold</th><th class="num">Returnable</th><th class="num" style="width: 170px">Return</th><th class="num">Amount</th></tr></thead>
                                <tbody>
                                    @for (l of o.lines; track l.key) {
                                        <tr [class.faint]="l.returnableQuantity <= 0">
                                            <td>{{ l.productName }} <span class="faint">{{ l.sku }}</span></td>
                                            <td class="num">{{ l.quantity | posQty }}</td>
                                            <td class="num">{{ l.returnableQuantity | posQty }}</td>
                                            <td>
                                                @if (l.returnableQuantity > 0) {
                                                    <div class="qty">
                                                        <button class="pos-btn" type="button" (click)="step(l.key, -1)" [disabled]="!(flow.qty()[l.key] > 0)" aria-label="Less" [attr.data-testid]="'ret-minus-' + l.sku"><i class="pi pi-minus"></i></button>
                                                        <span class="v" [attr.data-testid]="'ret-qty-' + l.sku">{{ (flow.qty()[l.key] || 0) | posQty }}</span>
                                                        <button class="pos-btn" type="button" (click)="step(l.key, 1)" [disabled]="(flow.qty()[l.key] || 0) >= l.returnableQuantity" aria-label="More" [attr.data-testid]="'ret-plus-' + l.sku"><i class="pi pi-plus"></i></button>
                                                        <button class="pos-btn ghost" type="button" (click)="flow.setQty(l.key, l.returnableQuantity)" [attr.data-testid]="'ret-all-' + l.sku">All</button>
                                                    </div>
                                                } @else { <span class="faint">Returned</span> }
                                            </td>
                                            <td class="num">{{ flow.lineAmount(l.key) | posMoney }}</td>
                                        </tr>
                                    }
                                </tbody>
                            </table>
                        </div>

                        <div class="pos-card stack">
                            <h2>Reason</h2>
                            <div class="reasons" role="radiogroup" aria-label="Reason">
                                @for (r of flow.reasonOptions(); track r.uuid) {
                                    <button class="pos-btn" type="button" [class.on]="flow.reasonUuid() === r.uuid" (click)="flow.reasonUuid.set(r.uuid)" [attr.data-testid]="'ret-reason-' + r.code">
                                        <span class="grow">{{ r.name }}</span>
                                        @if (r.requiresManager) { <i class="pi pi-shield" title="Needs a manager"></i> }
                                        @if (r.requiresNote) { <i class="pi pi-pencil" title="Needs a note"></i> }
                                    </button>
                                } @empty {
                                    <p class="faint">No return reasons on this device yet — they arrive with the next sync.</p>
                                }
                            </div>
                            <div>
                                <label class="pos-lbl" for="ret-note">Note{{ flow.reason()?.requiresNote ? ' (required)' : '' }}</label>
                                <textarea id="ret-note" class="pos-input" rows="2" maxlength="400" [ngModel]="flow.note()" (ngModelChange)="flow.note.set($event)" data-testid="ret-note"></textarea>
                            </div>
                        </div>
                    </div>

                    <div class="pos-card stack" data-testid="ret-summary">
                        <h2>Summary</h2>
                        <div class="sum">
                            <span class="muted">Items</span><span>{{ flow.summary().itemCount | posQty }}</span>
                            <span class="muted">Return subtotal</span><span data-testid="ret-subtotal">{{ flow.summary().subtotal | posMoney }}</span>
                            <span class="muted">Restocking fee{{ flow.summary().feePct ? ' (' + flow.summary().feePct + '%)' : '' }}</span><span data-testid="ret-fee">{{ flow.summary().fee ? '−' : '' }}{{ flow.summary().fee | posMoney }}</span>
                            <span class="big">{{ flow.mode() === 'EXCHANGE' ? 'Credit' : 'Refund' }}</span><span class="big" data-testid="ret-refund">{{ flow.summary().refund | posMoney: state.currency() }}</span>
                        </div>
                        @if (flow.managerNeeded().length) {
                            <div class="pos-banner wn" data-testid="ret-manager-needed">
                                <i class="pi pi-shield"></i>
                                <span>Manager needed: {{ managerText(flow.managerNeeded()) }}@if (flow.authValid()) { — <strong>authorized by {{ flow.authorization()?.managerName }}</strong> }</span>
                            </div>
                        }
                        @if (error()) { <p class="err-text" data-testid="ret-items-error">{{ error() }}</p> }
                        <button class="pos-btn primary lg block" type="button" (click)="next()" [disabled]="busy()" data-testid="ret-next">
                            {{ flow.mode() === 'EXCHANGE' ? 'Choose new items' : 'Refund' }} <i class="pi pi-arrow-right"></i>
                        </button>
                    </div>
                </div>
            }
        </div>

        <pos-manager-auth [visible]="authOpen()" [reasons]="authReasons()" [currency]="state.currency()" (authorized)="onAuthorized()" (cancelled)="authOpen.set(false)"></pos-manager-auth>
    `
})
export class PosReturnItemsComponent implements OnInit {
    readonly flow = inject(PosReturnFlow);
    readonly state = inject(PosState);
    readonly conn = inject(PosConnectivity);
    private readonly route = inject(ActivatedRoute);
    private readonly router = inject(Router);

    readonly o = this.flow.order;
    readonly p = this.flow.policy;
    readonly busy = signal(false);
    readonly error = signal<string | null>(null);
    readonly loadError = signal<string | null>(null);
    readonly authOpen = signal(false);
    readonly authReasons = signal<ManagerReason[]>([]);

    readonly windowOk = computed(() => {
        const p = this.p();
        return !!p && (this.flow.mode() === 'EXCHANGE' ? p.withinExchangeWindow : p.withinReturnWindow);
    });
    readonly exchangeBlocked = computed(() => {
        const p = this.p();
        return p ? exchangeBlock(p) : null;
    });

    async ngOnInit(): Promise<void> {
        const key = this.route.snapshot.paramMap.get('orderUuid') ?? '';
        const mode = this.route.snapshot.queryParamMap.get('mode') === 'EXCHANGE' ? 'EXCHANGE' : null;
        const cur = this.o();
        if (!cur || orderKey(cur) !== key) {
            try {
                const found = await this.flow.loadKey(key);
                const block = this.flow.blockOf(found);
                if (block) {
                    this.loadError.set(block);
                    return;
                }
                await this.flow.begin(found, 'RETURN');
            } catch (e) {
                this.loadError.set(errorMessage(e, 'The sale could not be loaded.'));
                return;
            }
        } else {
            await this.flow.loadReasons();
        }
        if (mode && !this.exchangeBlocked()) this.flow.setMode(mode);
    }

    setMode(m: FlowMode): void {
        this.error.set(null);
        this.flow.setMode(m);
    }

    step(key: string, by: number): void {
        this.error.set(null);
        this.flow.setQty(key, (this.flow.qty()[key] ?? 0) + by);
    }

    managerText(r: ManagerReason[]): string {
        return r.map(x => MANAGER_REASON_TEXT[x].toLowerCase()).join('; ');
    }

    next(): void {
        this.error.set(null);
        const exchange = this.flow.mode() === 'EXCHANGE';
        const problem = exchange ? (this.exchangeBlocked() ?? this.flow.itemsError()) : this.flow.itemsError();
        if (problem) {
            this.error.set(problem);
            return;
        }
        const need = this.flow.managerNeeded();
        if (need.length && !this.flow.authValid()) {
            this.authReasons.set(need);
            this.authOpen.set(true);
            return;
        }
        this.go();
    }

    onAuthorized(): void {
        this.authOpen.set(false);
        this.go();
    }

    private go(): void {
        const key = orderKey(this.o()!);
        if (this.flow.mode() === 'EXCHANGE') void this.router.navigate(['/pos/exchange', key]);
        else void this.router.navigate(['/pos/return', key, 'refund']);
    }
}
