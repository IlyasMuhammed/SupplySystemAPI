import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { TooltipModule } from 'primeng/tooltip';
import { Tender, addTender, changeTotal, isFullyTendered, quickAmounts, remainingDue, round2, tenderedNet } from '../core/pos-calculator';
import { PosApi } from '../core/pos-api.service';
import { PosAuth } from '../core/pos-auth.service';
import { PosCart, newId } from '../core/pos-cart.service';
import { PosConnectivity } from '../core/pos-connectivity.service';
import { errorMessage, isNetworkError } from '../core/pos-http.service';
import { PosOrders } from '../core/pos-orders.service';
import { CreditDecision, basicCreditCheck, offlineCreditCheck, onlineCreditCheck } from '../core/pos-payment-rules';
import { ManagerRequiredError, PosReturnFlow } from '../core/pos-return-flow.service';
import { MSG, isCreditExpired, orderKey, storeCreditTender } from '../core/pos-return-rules';
import { POS_PERM, PaymentMethod, StoreCredit } from '../core/pos.models';
import { PosState } from '../core/pos-state.service';
import { PosStoreCredits } from '../core/pos-store-credit.service';
import { PosLabelPipe, PosMoneyPipe } from './pos-money.pipe';

const NEEDS_REFERENCE: PaymentMethod[] = ['CARD', 'MOBILE', 'BANK_TRANSFER', 'CHEQUE'];

/**
 * A38 FSD §17.3 — payment: methods enabled by the terminal config, amount prefilled with what is due, quick cash amounts,
 * change, split tenders, card/transfer references and ON_ACCOUNT (named customer allowed POS credit; live credit check
 * online, synced balance + store tolerance offline). Confirm saves the order to Dexie and pushes it.
 * A39: STORE_CREDIT tender (online only; by number / scan or the customer's list; ≤ min(balance, remaining); no change),
 * and exchange mode (`?exchange={orderKey}`): pays the positive net of the exchange in progress.
 */
@Component({
    selector: 'pos-payment',
    standalone: true,
    imports: [FormsModule, RouterLink, DatePipe, TooltipModule, PosMoneyPipe, PosLabelPipe],
    template: `
        <div class="pos-page" data-testid="pos-payment">
            <div class="row">
                @if (exchangeKey()) {
                    <a class="pos-btn" [routerLink]="['/pos/exchange', exchangeKey()]"><i class="pi pi-arrow-left"></i> Exchange</a>
                    <h1 class="grow">Exchange payment</h1>
                } @else {
                    <a class="pos-btn" routerLink="/pos/sell"><i class="pi pi-arrow-left"></i> Cart</a>
                    <h1 class="grow">Payment</h1>
                }
                <span class="muted">{{ customerName() }}</span>
            </div>
            <div class="pos-tiles">
                <div class="pos-kpi"><span class="l">{{ exchangeKey() ? 'Net to pay' : 'Total' }}</span><span class="v" data-testid="pay-total">{{ total() | posMoney: currency() }}</span>
                    <span class="s">@if (exchangeKey()) { new {{ flow.newTotals().grandTotal | posMoney }} − returned {{ flow.summary().refund | posMoney }} } @else { {{ cart.itemCount() }} items }</span></div>
                <div class="pos-kpi"><span class="l">Paid</span><span class="v">{{ paid() | posMoney }}</span></div>
                <div class="pos-kpi" [class.wn]="due() > 0"><span class="l">Due</span><span class="v" data-testid="pay-due">{{ due() | posMoney }}</span></div>
                <div class="pos-kpi ok"><span class="l">Change</span><span class="v" data-testid="pay-change">{{ change() | posMoney }}</span></div>
            </div>

            @if (due() > 0) {
                <div class="pos-card stack">
                    <div class="row wrap" role="radiogroup" aria-label="Payment method">
                        @for (m of methods(); track m) {
                            <button class="pos-btn lg" type="button" [class.on]="method() === m" (click)="selectMethod(m)" [attr.data-testid]="'pay-method-' + m"
                                [disabled]="(m === 'ON_ACCOUNT' && !!creditBlock()) || (m === 'STORE_CREDIT' && !conn.online())"
                                [pTooltip]="m === 'ON_ACCOUNT' ? creditBlock() || '' : m === 'STORE_CREDIT' && !conn.online() ? offlineCredit : ''">
                                <i class="pi" [class]="icon(m)"></i> {{ m === 'STORE_CREDIT' ? 'Store credit' : (m | posLabel) }}
                            </button>
                        }
                    </div>
                    @if (showStoreCredit() && !conn.online()) { <p class="faint" data-testid="pay-sc-offline">{{ offlineCredit }}</p> }

                    @if (method() === 'STORE_CREDIT') {
                        <div class="stack" data-testid="pay-sc">
                            <div>
                                <label class="pos-lbl" for="sc-number">Credit note number</label>
                                <div class="row">
                                    <input id="sc-number" class="pos-input grow" placeholder="Scan or type CN-…" autocomplete="off" [(ngModel)]="scNumber" (keyup.enter)="findCredit()" data-testid="pay-sc-number" />
                                    <button class="pos-btn" type="button" (click)="findCredit()" [disabled]="busy() || !scNumber.trim()" data-testid="pay-sc-find"><i class="pi pi-search"></i></button>
                                </div>
                            </div>
                            @if (customerCredits().length) {
                                <div class="pos-list" data-testid="pay-sc-list">
                                    @for (c of customerCredits(); track c.uuid) {
                                        <button class="pos-btn block" type="button" [class.on]="scNote()?.uuid === c.uuid" (click)="pickCredit(c)">
                                            <span class="mono">{{ c.number }}</span><span>{{ c.remainingBalance | posMoney }}@if (c.expiresAt) { <span class="faint"> · until {{ c.expiresAt | date: 'dd MMM yyyy' }}</span> }</span>
                                        </button>
                                    }
                                </div>
                            }
                            @if (scNote(); as n) {
                                <div class="pos-kv" data-testid="pay-sc-note">
                                    <span>Note</span><span class="mono">{{ n.number }}</span>
                                    <span>Balance</span><span>{{ n.remainingBalance - usedOn(n.uuid) | posMoney: currency() }}</span>
                                    @if (n.customerName) { <span>Customer</span><span>{{ n.customerName }}</span> }
                                    @if (n.expiresAt) { <span>Expires</span><span>{{ n.expiresAt | date: 'dd MMM yyyy' }}</span> }
                                </div>
                                <div>
                                    <label class="pos-lbl" for="pay-amount">Amount (no change is given)</label>
                                    <input id="pay-amount" class="pos-input" style="font-size: 1.4rem" type="number" inputmode="decimal" step="0.01" min="0" [(ngModel)]="amount" (keyup.enter)="add()" data-testid="pay-amount" />
                                </div>
                            }
                        </div>
                    } @else if (method() !== 'ON_ACCOUNT') {
                        <div>
                            <label class="pos-lbl" for="pay-amount">Amount</label>
                            <input id="pay-amount" class="pos-input" style="font-size: 1.4rem" type="number" inputmode="decimal" step="0.01" min="0" [(ngModel)]="amount" (keyup.enter)="add()" data-testid="pay-amount" />
                        </div>
                        @if (method() === 'CASH') {
                            <div class="row wrap">
                                @for (q of quick(); track q) {
                                    <button class="pos-btn" type="button" (click)="amount = q; add()" [attr.data-testid]="'pay-quick-' + q">{{ q | posMoney }}</button>
                                }
                            </div>
                        }
                    } @else {
                        <p class="muted">The remaining {{ due() | posMoney: currency() }} goes on {{ cart.customer()?.name }}'s account.</p>
                    }
                    @if (needsReference()) {
                        <div>
                            <label class="pos-lbl" for="pay-ref">{{ method() === 'ON_ACCOUNT' ? 'Customer ID (required for credit)' : 'Reference (optional)' }}</label>
                            <input id="pay-ref" class="pos-input" maxlength="100" [(ngModel)]="reference" data-testid="pay-ref" />
                        </div>
                    }
                    @if (error()) { <p class="err-text" data-testid="pay-error">{{ error() }}</p> }
                    @if (warning()) { <p class="pos-pill wn" data-testid="pay-warning">{{ warning() }}</p> }
                    <button class="pos-btn primary lg" type="button" (click)="add()" [disabled]="busy() || (method() === 'STORE_CREDIT' && !scNote())" data-testid="pay-add">
                        @if (busy()) { <i class="pi pi-spin pi-spinner"></i> } Add {{ method() === 'STORE_CREDIT' ? 'store credit' : (method() | posLabel) }}
                    </button>
                </div>
            }

            @if (tenders().length) {
                <div class="pos-card">
                    <table class="pos-table">
                        <thead><tr><th>Method</th><th>Reference</th><th class="num">Amount</th><th class="num">Change</th><th></th></tr></thead>
                        <tbody>
                            @for (t of tenders(); track t.localId) {
                                <tr data-testid="pay-tender">
                                    <td>{{ t.method === 'STORE_CREDIT' ? 'Store credit' : (t.method | posLabel) }}</td><td class="faint">{{ t.reference }}</td>
                                    <td class="num">{{ t.amount | posMoney }}</td><td class="num">{{ t.changeGiven ? (t.changeGiven | posMoney) : '' }}</td>
                                    <td class="num"><button class="pos-btn ghost" type="button" (click)="removeTender(t)" [disabled]="busy()" aria-label="Remove"><i class="pi pi-times"></i></button></td>
                                </tr>
                            }
                        </tbody>
                    </table>
                </div>
            }

            @if (complete()) {
                @if (error()) { <p class="err-text" data-testid="pay-error">{{ error() }}</p> }
                <button class="pos-btn ok lg block" type="button" (click)="confirm()" [disabled]="busy()" data-testid="pay-confirm">
                    @if (busy()) { <i class="pi pi-spin pi-spinner"></i> } {{ exchangeKey() ? 'Complete exchange' : 'Complete sale' }}{{ change() > 0 ? ' — change ' : '' }}@if (change() > 0) { {{ change() | posMoney: currency() }} }
                </button>
            }
            @if (!conn.online()) { <p class="faint"><i class="pi pi-ban"></i> Offline — the {{ exchangeKey() ? 'exchange' : 'sale' }} is kept on this device and sent when the connection returns.</p> }
        </div>
    `
})
export class PosPaymentComponent implements OnInit {
    readonly cart = inject(PosCart);
    readonly state = inject(PosState);
    readonly conn = inject(PosConnectivity);
    readonly flow = inject(PosReturnFlow);
    private readonly api = inject(PosApi);
    private readonly auth = inject(PosAuth);
    private readonly orders = inject(PosOrders);
    private readonly credits = inject(PosStoreCredits);
    private readonly router = inject(Router);
    private readonly route = inject(ActivatedRoute);

    readonly currency = this.state.currency;
    readonly offlineCredit = MSG.creditOffline;
    /** A39: the exchange being paid (route key), or null for a normal sale. */
    readonly exchangeKey = signal<string | null>(null);
    readonly showStoreCredit = computed(() => this.state.creditNotesEnabled() && this.auth.has(POS_PERM.CREDIT_NOTE_REDEEM));
    readonly methods = computed<PaymentMethod[]>(() => {
        const base = this.exchangeKey() ? this.state.paymentMethods().filter(m => m !== 'ON_ACCOUNT') : this.state.paymentMethods();
        return this.showStoreCredit() ? [...base, 'STORE_CREDIT'] : base;
    });
    readonly tenders = signal<Tender[]>([]);
    readonly method = signal<PaymentMethod>('CASH');
    readonly busy = signal(false);
    readonly error = signal<string | null>(null);
    readonly warning = signal<string | null>(null);
    private offlineCreditFlag = false;

    amount: number | null = null;
    reference = '';
    private creditRequiresId = false;

    // store credit
    scNumber = '';
    readonly scNote = signal<StoreCredit | null>(null);
    readonly customerCredits = signal<StoreCredit[]>([]);

    readonly total = computed(() => (this.exchangeKey() ? Math.max(0, this.flow.exchange().net) : this.cart.totals().grandTotal));
    readonly paid = computed(() => tenderedNet(this.tenders()));
    readonly due = computed(() => remainingDue(this.total(), this.tenders()));
    readonly change = computed(() => changeTotal(this.tenders()));
    readonly complete = computed(() => isFullyTendered(this.total(), this.tenders()) && this.total() > 0);
    readonly quick = computed(() => quickAmounts(this.due()));
    readonly customerUuid = computed(() => {
        if (!this.exchangeKey()) return this.cart.customer()?.uuid ?? null;
        const o = this.flow.order();
        return o && !o.isWalkIn ? o.customerUuid : null;
    });
    readonly customerName = computed(() => (this.exchangeKey() ? this.flow.order()?.customerName || 'Walk-in customer' : this.cart.customer()?.name || 'Walk-in customer'));
    /** Why ON_ACCOUNT cannot be used (before any credit lookup), or null. */
    readonly creditBlock = computed(() => {
        const d = basicCreditCheck(this.cart.customer(), this.state.features().creditSales);
        return d.ok ? null : (d.message ?? 'Not available');
    });

    ngOnInit(): void {
        const ex = this.route.snapshot.queryParamMap.get('exchange');
        if (ex) {
            const o = this.flow.order();
            if (!o || orderKey(o) !== ex || this.flow.exchange().settlement !== 'PAY') {
                void this.router.navigate(o ? ['/pos/exchange', ex] : ['/pos/return']);
                return;
            }
            this.exchangeKey.set(ex);
        } else if (this.cart.isEmpty()) {
            void this.router.navigate(['/pos/sell']);
            return;
        }
        this.method.set(this.methods()[0] ?? 'CASH');
        this.amount = this.due();

        // "Cash exact" / "Card" on the selling screen: pay the whole total with that one tender and finish the sale. Any refusal
        // (a manager is needed, a reference is missing, ...) simply leaves this page open with the message, as if typed by hand.
        const quick = this.route.snapshot.queryParamMap.get('quick');
        if (!ex && (quick === 'CASH' || quick === 'CARD') && this.methods().includes(quick)) {
            this.selectMethod(quick);
            void this.quickComplete();
        }
    }

    private async quickComplete(): Promise<void> {
        await this.add();
        if (this.complete() && !this.error()) await this.confirm();
    }

    icon(m: PaymentMethod): string {
        return (
            { CASH: 'pi-money-bill', CARD: 'pi-credit-card', MOBILE: 'pi-mobile', BANK_TRANSFER: 'pi-building-columns', CHEQUE: 'pi-file-edit', ON_ACCOUNT: 'pi-book', STORE_CREDIT: 'pi-ticket' } as Record<string, string>
        )[m] ?? 'pi-wallet';
    }

    needsReference(): boolean {
        return NEEDS_REFERENCE.includes(this.method()) || (this.method() === 'ON_ACCOUNT' && this.creditRequiresId);
    }

    selectMethod(m: PaymentMethod): void {
        this.method.set(m);
        this.error.set(null);
        this.warning.set(null);
        this.reference = '';
        this.amount = this.due();
        this.creditRequiresId = m === 'ON_ACCOUNT' && !!this.cart.customer()?.requiresIdForCredit;
        if (m === 'STORE_CREDIT') {
            this.scNote.set(null);
            this.scNumber = '';
            void this.loadCustomerCredits();
        }
    }

    // ── A39 store credit ────────────────────────────────────────────────────

    usedOn(uuid: string): number {
        return round2(this.tenders().filter(t => t.creditNoteUuid === uuid).reduce((s, t) => s + t.amount, 0));
    }

    private async loadCustomerCredits(): Promise<void> {
        this.customerCredits.set([]);
        const c = this.customerUuid();
        if (!c || !this.conn.online()) return;
        try {
            const list = await this.credits.forCustomer(c);
            this.customerCredits.set(list.filter(n => !isCreditExpired(n) && n.remainingBalance > 0));
        } catch {
            /* the number still works */
        }
    }

    async findCredit(): Promise<void> {
        this.error.set(null);
        this.busy.set(true);
        try {
            this.pickCredit(await this.credits.byNumber(this.scNumber));
        } catch (e) {
            this.scNote.set(null);
            this.error.set(e instanceof Error && /not found/i.test(e.message) ? `Credit note ${this.scNumber.trim().toUpperCase()} was not found.` : errorMessage(e));
        } finally {
            this.busy.set(false);
        }
    }

    pickCredit(c: StoreCredit): void {
        this.error.set(null);
        const check = storeCreditTender({ note: c, online: this.conn.online(), orderCustomerUuid: this.customerUuid(), due: this.due(), used: this.usedOn(c.uuid) });
        if ('error' in check) {
            this.scNote.set(null);
            this.error.set(check.error);
            return;
        }
        this.scNote.set(c);
        this.scNumber = c.number;
        this.amount = check.amount;
    }

    async add(): Promise<void> {
        this.error.set(null);
        const m = this.method();
        if (m === 'STORE_CREDIT') {
            const n = this.scNote();
            if (!n) {
                this.error.set('Find the credit note first.');
                return;
            }
            const check = storeCreditTender({ note: n, online: this.conn.online(), orderCustomerUuid: this.customerUuid(), due: this.due(), used: this.usedOn(n.uuid), requested: this.amount });
            if ('error' in check) {
                this.error.set(check.error);
                return;
            }
            const res = addTender(this.total(), this.tenders(), 'STORE_CREDIT', check.amount, newId(), n.number);
            if ('error' in res) {
                this.error.set(res.error);
                return;
            }
            const last = res.tenders[res.tenders.length - 1];
            this.tenders.set([...res.tenders.slice(0, -1), { ...last, creditNoteUuid: n.uuid, creditNoteNumber: n.number }]);
            this.scNote.set(null);
            this.scNumber = '';
            this.amount = this.due() || null;
            if (this.due() > 0) this.method.set(this.methods()[0] ?? 'CASH');
            return;
        }
        if (m === 'ON_ACCOUNT') {
            const decision = await this.checkCredit(this.due());
            if (!decision.ok) {
                this.error.set(decision.message ?? 'Credit is not possible.');
                return;
            }
            if (decision.requiresId && !this.reference.trim()) {
                this.creditRequiresId = true;
                this.error.set('This customer must show an ID for credit — enter it as the reference.');
                return;
            }
            if (decision.overLimit) this.warning.set('Over the credit limit, within the offline tolerance — the back office will be notified.');
        }
        const res = addTender(this.total(), this.tenders(), m, this.amount ?? 0, newId(), this.reference.trim() || null);
        if ('error' in res) {
            this.error.set(res.error);
            return;
        }
        this.tenders.set(res.tenders);
        this.reference = '';
        this.amount = this.due() || null;
        if (this.due() > 0 && m === 'ON_ACCOUNT') this.method.set('CASH');
    }

    removeTender(t: Tender): void {
        this.tenders.update(ts => ts.filter(x => x.localId !== t.localId));
        if (t.method === 'ON_ACCOUNT') {
            this.offlineCreditFlag = false;
            this.warning.set(null);
        }
        this.amount = this.due();
    }

    /** ON_ACCOUNT: basic rules, then the live credit check online, else the synced balance with the store's tolerance. */
    async checkCredit(amount: number): Promise<CreditDecision> {
        const customer = this.cart.customer();
        const basic = basicCreditCheck(customer, this.state.features().creditSales);
        if (!basic.ok || !customer) return basic;
        this.busy.set(true);
        try {
            if (this.conn.online()) {
                try {
                    const credit = await this.api.customerCredit(customer.uuid);
                    this.offlineCreditFlag = false;
                    return onlineCreditCheck(credit, amount);
                } catch (e) {
                    if (!isNetworkError(e)) return { ok: false, message: errorMessage(e, 'The credit check failed.') };
                }
            }
            this.offlineCreditFlag = true;
            return offlineCreditCheck(customer, amount, this.state.store()?.offlineCreditTolerancePct ?? 0);
        } finally {
            this.busy.set(false);
        }
    }

    async confirm(): Promise<void> {
        this.busy.set(true);
        this.error.set(null);
        try {
            if (this.exchangeKey()) {
                const saved = await this.flow.submitExchange(this.tenders());
                await this.router.navigate(['/pos/return-receipt', saved.localId]);
                return;
            }
            const hasCredit = this.tenders().some(t => t.method === 'ON_ACCOUNT');
            const order = await this.orders.completeSale({ tenders: this.tenders(), offlineCredit: hasCredit && this.offlineCreditFlag });
            await this.router.navigate(['/pos/receipt', order.localId]);
        } catch (e) {
            if (e instanceof ManagerRequiredError) this.error.set(`${e.message} Go back to the exchange to authorize.`);
            else this.error.set(errorMessage(e, this.exchangeKey() ? 'The exchange could not be completed.' : 'The sale could not be completed.'));
        } finally {
            this.busy.set(false);
        }
    }
}
