import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { DialogModule } from 'primeng/dialog';
import { PosApi } from '../core/pos-api.service';
import { PosAuth } from '../core/pos-auth.service';
import { PosConnectivity } from '../core/pos-connectivity.service';
import { errorMessage } from '../core/pos-http.service';
import { CASH_REASONS, CashDirection, CashMovement, CashReason, CashierSession, LocalCashMovement, POS_PERM, ShiftDetail } from '../core/pos.models';
import { PosShifts } from '../core/pos-shift.service';
import { PosState } from '../core/pos-state.service';
import { PosSync } from '../core/pos-sync.service';
import { PosLabelPipe, PosMoneyPipe } from './pos-money.pipe';

/**
 * A38 FSD §17.5 — the terminal's shift: open with a float (POS_SHIFT_OPEN, online), live tiles, cashier sessions,
 * cash in/out (POS_CASH_MOVEMENT — queued, works offline) and start-close (POS_SHIFT_CLOSE).
 */
@Component({
    selector: 'pos-shift',
    standalone: true,
    imports: [FormsModule, RouterLink, DatePipe, DialogModule, PosMoneyPipe, PosLabelPipe],
    template: `
        <div class="pos-page" data-testid="pos-shift">
            @if (!state.shift()) {
                <div class="pos-card stack" style="max-width: 480px">
                    <h1>No open shift</h1>
                    @if (canOpen()) {
                        <p class="muted">Count the opening float in the drawer and open the shift for {{ state.terminal()?.name }}.</p>
                        <div><label class="pos-lbl" for="sh-float">Opening float</label>
                            <input id="sh-float" class="pos-input" type="number" inputmode="decimal" step="0.01" min="0" [(ngModel)]="openingFloat" data-testid="shift-float" /></div>
                        <div><label class="pos-lbl" for="sh-thr">Variance threshold</label>
                            <input id="sh-thr" class="pos-input" type="number" inputmode="decimal" step="0.01" min="0" [(ngModel)]="threshold" /></div>
                        @if (error()) { <p class="err-text" data-testid="shift-error">{{ error() }}</p> }
                        <button class="pos-btn primary lg" type="button" (click)="open()" [disabled]="busy() || openingFloat === null || openingFloat < 0 || !conn.online()" data-testid="shift-open">Open shift</button>
                        @if (!conn.online()) { <p class="faint">Opening a shift needs a connection.</p> }
                    } @else {
                        <p class="muted" data-testid="shift-ask">A supervisor has to open the shift before selling. Lock the till so a supervisor can sign in with their PIN.</p>
                    }
                    <div class="row"><button class="pos-btn" type="button" (click)="refresh()" [disabled]="busy()"><i class="pi pi-refresh"></i> Refresh</button></div>
                </div>
            } @else {
                @let s = state.shift()!;
                <div class="row wrap">
                    <div class="grow">
                        <h1><span class="mono">{{ s.shiftNumber }}</span> <span class="pos-pill" [class.ok]="s.status === 'OPEN'" [class.wn]="s.status === 'CLOSING'">{{ s.status | posLabel }}</span></h1>
                        <div class="faint">Opened by {{ s.openedByName }} · {{ s.openedAt | date: 'dd MMM HH:mm' }}</div>
                    </div>
                    <button class="pos-btn" type="button" (click)="refresh()" [disabled]="busy() || !conn.online()"><i class="pi pi-refresh"></i></button>
                    @if (s.status === 'OPEN') {
                        <a class="pos-btn primary" routerLink="/pos/sell"><i class="pi pi-shopping-cart"></i> Sell</a>
                        @if (canCash()) {
                            <button class="pos-btn" type="button" (click)="openCash('IN')" data-testid="shift-cash-in"><i class="pi pi-arrow-down"></i> Cash in</button>
                            <button class="pos-btn" type="button" (click)="openCash('OUT')" data-testid="shift-cash-out"><i class="pi pi-arrow-up"></i> Cash out</button>
                        }
                        @if (canClose()) {
                            <button class="pos-btn danger" type="button" (click)="startClose()" [disabled]="busy() || !conn.online()" data-testid="shift-start-close"><i class="pi pi-stop-circle"></i> Close shift</button>
                        }
                    } @else if (s.status === 'CLOSING' && canClose()) {
                        <a class="pos-btn primary" routerLink="/pos/shift/close">Continue closing</a>
                    }
                </div>
                @if (error()) { <p class="err-text" data-testid="shift-error">{{ error() }}</p> }

                <div class="pos-tiles" data-testid="shift-tiles">
                    <div class="pos-kpi"><span class="l">Opening float</span><span class="v">{{ s.openingFloat | posMoney }}</span></div>
                    <div class="pos-kpi"><span class="l">Cash sales</span><span class="v">{{ s.totalCashSales | posMoney }}</span></div>
                    <div class="pos-kpi"><span class="l">Card</span><span class="v">{{ s.totalCardSales | posMoney }}</span></div>
                    <div class="pos-kpi"><span class="l">Mobile</span><span class="v">{{ s.totalMobileSales | posMoney }}</span></div>
                    <div class="pos-kpi"><span class="l">Other</span><span class="v">{{ s.totalOtherSales | posMoney }}</span></div>
                    <div class="pos-kpi"><span class="l">On account</span><span class="v">{{ s.totalCreditSales | posMoney }}</span></div>
                    <div class="pos-kpi"><span class="l">Refunds</span><span class="v">{{ s.totalCashRefunds + s.totalOtherRefunds | posMoney }}</span><span class="s">{{ s.refundCount }} returns</span></div>
                    @if (s.totalStoreCreditIssued !== undefined || s.totalStoreCreditRedeemed !== undefined) {
                        <div class="pos-kpi" data-testid="shift-sc-issued"><span class="l">Store credit issued</span><span class="v">{{ s.totalStoreCreditIssued ?? 0 | posMoney }}</span></div>
                        <div class="pos-kpi" data-testid="shift-sc-redeemed"><span class="l">Store credit redeemed</span><span class="v">{{ s.totalStoreCreditRedeemed ?? 0 | posMoney }}</span></div>
                    }
                    <div class="pos-kpi"><span class="l">Cash in / out</span><span class="v">{{ s.totalCashIn - s.totalCashOut | posMoney }}</span><span class="s">+{{ s.totalCashIn | posMoney }} / −{{ s.totalCashOut | posMoney }}</span></div>
                    <div class="pos-kpi ok"><span class="l">Expected cash</span><span class="v" data-testid="shift-expected">{{ s.expectedCashClosing | posMoney: state.currency() }}</span></div>
                    <div class="pos-kpi"><span class="l">Orders</span><span class="v">{{ s.orderCount }}</span><span class="s">Net {{ s.netTotal | posMoney }}</span></div>
                </div>
                @if (sync.pendingCount() > 0) { <p class="pos-pill wn">{{ sync.pendingCount() }} sales / cash movements not synced yet — the totals above do not include them.</p> }

                <div class="pos-card">
                    <h2>Cashiers</h2>
                    <table class="pos-table">
                        <thead><tr><th>Cashier</th><th>Started</th><th class="num">Orders</th><th class="num">Cash</th><th class="num">Card</th><th class="num">Mobile</th><th class="num">Credit</th></tr></thead>
                        <tbody>
                            @for (c of sessions(); track c.uuid) {
                                <tr><td>{{ c.userName }} @if (c.isActive) { <span class="pos-pill ok">Active</span> }</td><td>{{ c.startedAt | date: 'HH:mm' }}</td>
                                    <td class="num">{{ c.orderCount }}</td><td class="num">{{ c.cashSales | posMoney }}</td><td class="num">{{ c.cardSales | posMoney }}</td>
                                    <td class="num">{{ c.mobileSales | posMoney }}</td><td class="num">{{ c.creditSales | posMoney }}</td></tr>
                            } @empty { <tr><td colspan="7" class="faint">{{ conn.online() ? 'No sessions yet.' : 'Available online.' }}</td></tr> }
                        </tbody>
                    </table>
                </div>

                <div class="pos-card">
                    <h2>Cash movements</h2>
                    <table class="pos-table" data-testid="shift-movements">
                        <thead><tr><th>Time</th><th>Direction</th><th>Reason</th><th>Note</th><th class="num">Amount</th><th></th></tr></thead>
                        <tbody>
                            @for (m of queued(); track m.localId) {
                                <tr><td>{{ m.createdAtLocal | date: 'HH:mm' }}</td><td>{{ m.direction }}</td><td>{{ m.reason | posLabel }}</td><td class="faint">{{ m.note }}</td>
                                    <td class="num">{{ m.amount | posMoney }}</td><td><span class="pos-pill" [class.wn]="m.syncStatus !== 'error'" [class.er]="m.syncStatus === 'error'">{{ m.syncStatus === 'error' ? 'Failed' : 'Queued' }}</span></td></tr>
                            }
                            @for (m of movements(); track m.uuid) {
                                <tr><td>{{ m.performedAt | date: 'HH:mm' }}</td><td>{{ m.direction }}</td><td>{{ m.reason | posLabel }}</td><td class="faint">{{ m.note }}</td>
                                    <td class="num">{{ m.amount | posMoney }}</td><td class="faint">{{ m.performedByName }}</td></tr>
                            }
                            @if (!queued().length && !movements().length) { <tr><td colspan="6" class="faint">None.</td></tr> }
                        </tbody>
                    </table>
                </div>
            }
        </div>

        <p-dialog [visible]="!!cashDir()" (onHide)="cashDir.set(null)" (visibleChange)="!$event && cashDir.set(null)" [modal]="true" [header]="cashDir() === 'IN' ? 'Cash in' : 'Cash out'" [style]="{ width: 'min(420px, 95vw)' }">
            <div class="stack">
                <div><label class="pos-lbl" for="cm-amount">Amount</label><input id="cm-amount" class="pos-input" type="number" inputmode="decimal" step="0.01" min="0" [(ngModel)]="cashAmount" data-testid="cash-amount" /></div>
                <div><label class="pos-lbl">Reason</label>
                    <div class="row wrap">@for (r of reasons; track r) { <button class="pos-btn" type="button" [class.on]="cashReason === r" (click)="cashReason = r">{{ r | posLabel }}</button> }</div></div>
                <div><label class="pos-lbl" for="cm-note">Note</label><input id="cm-note" class="pos-input" maxlength="200" [(ngModel)]="cashNote" /></div>
                @if (cashError()) { <p class="err-text">{{ cashError() }}</p> }
                <button class="pos-btn primary lg" type="button" (click)="saveCash()" [disabled]="!cashAmount || cashAmount <= 0" data-testid="cash-save">Save</button>
            </div>
        </p-dialog>
    `
})
export class PosShiftComponent implements OnInit {
    readonly state = inject(PosState);
    readonly conn = inject(PosConnectivity);
    readonly sync = inject(PosSync);
    private readonly auth = inject(PosAuth);
    private readonly shifts = inject(PosShifts);
    private readonly api = inject(PosApi);
    private readonly router = inject(Router);

    readonly busy = signal(false);
    readonly error = signal<string | null>(null);
    readonly sessions = signal<CashierSession[]>([]);
    readonly movements = signal<CashMovement[]>([]);
    readonly queued = signal<LocalCashMovement[]>([]);
    readonly canOpen = computed(() => this.auth.has(POS_PERM.SHIFT_OPEN));
    readonly canClose = computed(() => this.auth.has(POS_PERM.SHIFT_CLOSE));
    readonly canCash = computed(() => this.auth.has(POS_PERM.CASH_MOVEMENT));
    readonly reasons = CASH_REASONS;

    openingFloat: number | null = null;
    threshold: number | null = null;

    readonly cashDir = signal<CashDirection | null>(null);
    readonly cashError = signal<string | null>(null);
    cashAmount: number | null = null;
    cashReason: CashReason = 'OTHER';
    cashNote = '';

    ngOnInit(): void {
        this.threshold = this.state.store()?.varianceThreshold ?? null;
        void this.refresh();
    }

    async refresh(): Promise<void> {
        this.busy.set(true);
        this.error.set(null);
        try {
            const shift = await this.shifts.refreshCurrent();
            if (shift && this.conn.online()) {
                try {
                    const d: ShiftDetail = await this.api.shiftDetail(shift.uuid);
                    const { sessions, cashMovements, ...plain } = d;
                    await this.state.setShift({ ...shift, ...plain });
                    this.sessions.set(sessions ?? []);
                    this.movements.set(cashMovements ?? []);
                } catch {
                    /* tiles from the current-shift call are enough */
                }
            }
            await this.loadQueued();
        } finally {
            this.busy.set(false);
        }
    }

    private async loadQueued(): Promise<void> {
        const s = this.state.shift();
        this.queued.set(s ? await this.shifts.queuedCashMovements(s.uuid) : []);
    }

    async open(): Promise<void> {
        this.busy.set(true);
        this.error.set(null);
        try {
            await this.shifts.open(this.openingFloat ?? 0, this.threshold);
            await this.router.navigate(['/pos/sell']);
        } catch (e) {
            this.error.set(errorMessage(e, 'The shift could not be opened.'));
        } finally {
            this.busy.set(false);
        }
    }

    openCash(dir: CashDirection): void {
        this.cashDir.set(dir);
        this.cashAmount = null;
        this.cashReason = dir === 'IN' ? 'FLOAT_TOP_UP' : 'SAFE_DROP';
        this.cashNote = '';
        this.cashError.set(null);
    }

    async saveCash(): Promise<void> {
        const dir = this.cashDir();
        if (!dir) return;
        try {
            await this.shifts.addCashMovement(dir, this.cashAmount ?? 0, this.cashReason, this.cashNote);
            this.cashDir.set(null);
            await this.loadQueued();
            setTimeout(() => void this.refresh(), 1500);
        } catch (e) {
            this.cashError.set(errorMessage(e));
        }
    }

    async startClose(): Promise<void> {
        this.busy.set(true);
        this.error.set(null);
        try {
            await this.shifts.startClose();
            await this.router.navigate(['/pos/shift/close']);
        } catch (e) {
            this.error.set(errorMessage(e, 'Closing could not start.'));
        } finally {
            this.busy.set(false);
        }
    }
}
