import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { PosConnectivity } from '../core/pos-connectivity.service';
import { errorMessage } from '../core/pos-http.service';
import { ManagerRequiredError, PosReturnFlow } from '../core/pos-return-flow.service';
import { MSG, defaultRefundMethod, orderKey, originalRefundMethod } from '../core/pos-return-rules';
import { ManagerReason, PaymentMethod } from '../core/pos.models';
import { PosState } from '../core/pos-state.service';
import { PosManagerAuthComponent } from './pos-manager-auth.component';
import { PosLabelPipe, PosMoneyPipe } from './pos-money.pipe';

const NEEDS_REFERENCE: PaymentMethod[] = ['CARD', 'MOBILE', 'BANK_TRANSFER', 'CHEQUE'];

export function refundIcon(m: string): string {
    return (
        ({ CASH: 'pi-money-bill', CARD: 'pi-credit-card', MOBILE: 'pi-mobile', BANK_TRANSFER: 'pi-building-columns', CHEQUE: 'pi-file-edit', ON_ACCOUNT: 'pi-book', STORE_CREDIT: 'pi-ticket' }) as Record<string, string>
    )[m] ?? 'pi-wallet';
}

/**
 * A39 §8 `/pos/return/:orderUuid/refund` — the refund method, governed by the store's policy (original method only /
 * cash allowed / store credit allowed / on account for credit sales), then the submission: online straight to the server,
 * offline saved on the device and synced later. Also settles an exchange whose net is a refund.
 */
@Component({
    selector: 'pos-return-refund',
    standalone: true,
    imports: [FormsModule, RouterLink, PosMoneyPipe, PosLabelPipe, PosManagerAuthComponent],
    template: `
        <div class="pos-page" data-testid="pos-return-refund">
            <div class="row wrap">
                <a class="pos-btn" [routerLink]="back()"><i class="pi pi-arrow-left"></i> {{ exchange() ? 'Exchange' : 'Items' }}</a>
                <h1 class="grow">{{ exchange() ? 'Exchange refund' : 'Refund' }} · <span class="mono">{{ flow.order()?.orderNumber }}</span></h1>
            </div>

            <div class="pos-tiles">
                @if (exchange()) {
                    <div class="pos-kpi"><span class="l">Returning</span><span class="v">{{ flow.summary().refund | posMoney }}</span></div>
                    <div class="pos-kpi"><span class="l">New items</span><span class="v">{{ flow.newTotals().grandTotal | posMoney }}</span></div>
                } @else {
                    <div class="pos-kpi"><span class="l">Return subtotal</span><span class="v">{{ flow.summary().subtotal | posMoney }}</span></div>
                    <div class="pos-kpi"><span class="l">Restocking fee</span><span class="v">{{ flow.summary().fee | posMoney }}</span>@if (flow.summary().feePct) { <span class="s">{{ flow.summary().feePct }}%</span> }</div>
                }
                <div class="pos-kpi ok"><span class="l">To refund</span><span class="v" data-testid="refund-amount">{{ flow.refundAmount() | posMoney: state.currency() }}</span></div>
            </div>

            <div class="pos-card stack">
                <h2>Refund method</h2>
                @if (policyNote()) { <p class="faint" data-testid="refund-policy-note">{{ policyNote() }}</p> }
                <div class="row wrap" role="radiogroup" aria-label="Refund method">
                    @for (m of flow.refundMethods(); track m) {
                        <button class="pos-btn lg" type="button" [class.on]="flow.refundMethod() === m" (click)="pick(m)" [attr.data-testid]="'refund-method-' + m">
                            <i class="pi" [class]="icon(m)"></i> {{ m === 'STORE_CREDIT' ? 'Store credit' : (m | posLabel) }}
                        </button>
                    } @empty {
                        <p class="err-text" data-testid="refund-none">{{ noMethods }}</p>
                    }
                </div>
                @if (needsReference()) {
                    <div><label class="pos-lbl" for="refund-ref">Reference (optional)</label>
                        <input id="refund-ref" class="pos-input" maxlength="100" [ngModel]="flow.refundReference()" (ngModelChange)="flow.refundReference.set($event)" data-testid="refund-ref" /></div>
                }
                @if (flow.refundMethod() === 'STORE_CREDIT') {
                    <p class="muted" data-testid="refund-store-credit-note">
                        A store credit note of {{ flow.refundAmount() | posMoney: state.currency() }} is issued
                        {{ creditOwner() ? 'to ' + creditOwner() : 'to the bearer (walk-in sale)' }}{{ expiryText() }}. It can be redeemed at any store while online.
                    </p>
                }
                @if (!conn.online()) { <p class="faint"><i class="pi pi-ban"></i> Offline — the {{ exchange() ? 'exchange' : 'return' }} is kept on this device and sent when the connection returns.</p> }
                @if (error()) { <p class="err-text" data-testid="refund-error">{{ error() }}</p> }
                <button class="pos-btn ok lg block" type="button" (click)="confirm()" [disabled]="busy() || !flow.refundMethod()" data-testid="refund-confirm">
                    @if (busy()) { <i class="pi pi-spin pi-spinner"></i> }
                    Confirm {{ exchange() ? 'exchange' : 'return' }} — refund {{ flow.refundAmount() | posMoney: state.currency() }}
                </button>
            </div>
        </div>

        <pos-manager-auth [visible]="authOpen()" [reasons]="authReasons()" [currency]="state.currency()" (authorized)="onAuthorized()" (cancelled)="authOpen.set(false)"></pos-manager-auth>
    `
})
export class PosReturnRefundComponent implements OnInit {
    readonly flow = inject(PosReturnFlow);
    readonly state = inject(PosState);
    readonly conn = inject(PosConnectivity);
    private readonly route = inject(ActivatedRoute);
    private readonly router = inject(Router);

    readonly busy = signal(false);
    readonly error = signal<string | null>(null);
    readonly authOpen = signal(false);
    readonly authReasons = signal<ManagerReason[]>([]);
    readonly noMethods = MSG.refundMethodRefused;
    readonly exchange = computed(() => this.flow.mode() === 'EXCHANGE');
    readonly back = computed(() => {
        const o = this.flow.order();
        const key = o ? orderKey(o) : '';
        return this.exchange() ? ['/pos/exchange', key] : ['/pos/return', key];
    });
    readonly creditOwner = computed(() => {
        const o = this.flow.order();
        return o && !o.isWalkIn && o.customerUuid ? o.customerName : null;
    });
    readonly expiryText = computed(() => {
        const d = this.flow.policy()?.policy?.creditNoteExpiryDays;
        return d ? `, valid for ${d} days` : '';
    });
    readonly policyNote = computed(() => {
        const p = this.flow.policy()?.policy;
        const o = this.flow.order();
        if (!p || !o) return null;
        if (p.refundToOriginalMethod) return `Store policy: refunds go back to the original payment method (${originalRefundMethod(o.payments).replace(/_/g, ' ').toLowerCase()}).`;
        if (!p.allowCashRefund) return 'Store policy: no cash refunds.';
        return null;
    });

    ngOnInit(): void {
        const o = this.flow.order();
        const key = this.route.snapshot.paramMap.get('orderUuid') ?? '';
        if (!o || orderKey(o) !== key || this.flow.itemsError()) {
            void this.router.navigate(['/pos/return', key]);
            return;
        }
        if (!this.flow.refundMethod() || !this.flow.refundMethods().includes(this.flow.refundMethod()!)) {
            this.flow.refundMethod.set(defaultRefundMethod(this.flow.refundMethods(), o.payments));
        }
    }

    icon = refundIcon;

    needsReference(): boolean {
        const m = this.flow.refundMethod();
        return !!m && NEEDS_REFERENCE.includes(m);
    }

    pick(m: PaymentMethod): void {
        this.error.set(null);
        this.flow.refundMethod.set(m);
    }

    async confirm(): Promise<void> {
        this.busy.set(true);
        this.error.set(null);
        try {
            const saved = this.exchange() ? await this.flow.submitExchange([]) : await this.flow.submitReturn();
            await this.router.navigate(['/pos/return-receipt', saved.localId]);
        } catch (e) {
            if (e instanceof ManagerRequiredError) {
                this.authReasons.set(e.reasons);
                this.authOpen.set(true);
            } else {
                this.error.set(errorMessage(e, 'The return could not be completed.'));
            }
        } finally {
            this.busy.set(false);
        }
    }

    async onAuthorized(): Promise<void> {
        this.authOpen.set(false);
        await this.confirm();
    }
}
