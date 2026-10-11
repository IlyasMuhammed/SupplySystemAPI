import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { round2 } from '../core/pos-calculator';
import { PosApi } from '../core/pos-api.service';
import { PosAuth } from '../core/pos-auth.service';
import { PosConnectivity } from '../core/pos-connectivity.service';
import { errorMessage } from '../core/pos-http.service';
import { Shift } from '../core/pos.models';
import { PosShifts } from '../core/pos-shift.service';
import { PosState } from '../core/pos-state.service';
import { PosSync } from '../core/pos-sync.service';
import { PosLabelPipe, PosMoneyPipe } from './pos-money.pipe';

/** Live variance of a count against the expected cash. */
export function cashVariance(counted: number | null, expected: number): number | null {
    return counted === null || counted === undefined || isNaN(counted) ? null : round2(counted - expected);
}

/**
 * A38 FSD §17.6 — closing a shift (POS_SHIFT_CLOSE, online): every queued sale/cash movement must be synced first, the
 * supervisor counts the drawer, sees the live variance against the expected cash and submits (a note is required when the
 * variance exceeds the threshold — the server then marks the shift DISPUTED). Resume returns to selling.
 */
@Component({
    selector: 'pos-shift-close',
    standalone: true,
    imports: [FormsModule, RouterLink, PosMoneyPipe, PosLabelPipe],
    template: `
        <div class="pos-page" style="max-width: 640px" data-testid="pos-shift-close">
            @if (result(); as r) {
                <div class="pos-card stack">
                    <h1>Shift {{ r.shiftNumber }} closed</h1>
                    <span class="pos-pill" [class.ok]="r.status === 'RECONCILED'" [class.wn]="r.status === 'DISPUTED'" data-testid="close-result">{{ r.status | posLabel }}</span>
                    <div class="pos-kv">
                        <span>Expected cash</span><span>{{ r.expectedCashClosing | posMoney: state.currency() }}</span>
                        <span>Counted</span><span>{{ r.actualCashClosing | posMoney: state.currency() }}</span>
                        <span>Variance</span><span>{{ r.cashVariance | posMoney }}</span>
                    </div>
                    @if (r.status === 'DISPUTED') { <p class="muted">The variance is above the threshold; a store manager reconciles it in the back office.</p> }
                    <button class="pos-btn primary lg" type="button" (click)="done()" data-testid="close-done">Done — sign out</button>
                </div>
            } @else {
            @if (state.shift(); as s) {
                <div class="row"><a class="pos-btn" routerLink="/pos/shift"><i class="pi pi-arrow-left"></i></a><h1 class="grow">Close {{ s.shiftNumber }}</h1></div>
                @if (s.status === 'OPEN') {
                    <div class="pos-card stack">
                        <p class="muted">The shift is still open. Start closing to stop new sales on this terminal.</p>
                        <button class="pos-btn danger lg" type="button" (click)="startClose()" [disabled]="busy() || !conn.online()">Start closing</button>
                    </div>
                } @else {
                    @if (sync.pendingCount() > 0 || sync.errorCount() > 0) {
                        <div class="pos-banner wn" data-testid="close-pending">
                            <i class="pi pi-sync"></i> {{ sync.pendingCount() }} queued, {{ sync.errorCount() }} failed — everything must reach the server before closing.
                            <button class="pos-btn" type="button" (click)="syncNow()" [disabled]="busy() || !conn.online()">Sync now</button>
                        </div>
                    }
                    <div class="pos-tiles">
                        <div class="pos-kpi"><span class="l">Expected cash</span><span class="v" data-testid="close-expected">{{ s.expectedCashClosing | posMoney }}</span>
                            <span class="s">Float {{ s.openingFloat | posMoney }} + cash {{ s.totalCashSales | posMoney }} + in {{ s.totalCashIn | posMoney }} − out {{ s.totalCashOut | posMoney }} − refunds {{ s.totalCashRefunds | posMoney }}</span></div>
                        <div class="pos-kpi" [class.ok]="variance() !== null && !overThreshold()" [class.er]="overThreshold()"><span class="l">Variance</span>
                            <span class="v" data-testid="close-variance">{{ variance() === null ? '—' : (variance()! > 0 ? '+' : '') + (variance() | posMoney) }}</span>
                            <span class="s">Threshold ± {{ s.varianceThreshold | posMoney }}</span></div>
                    </div>
                    <div class="pos-card stack">
                        <div><label class="pos-lbl" for="cl-count">Counted cash in the drawer</label>
                            <input id="cl-count" class="pos-input" style="font-size: 1.4rem" type="number" inputmode="decimal" step="0.01" min="0" [ngModel]="counted()" (ngModelChange)="counted.set($event)" data-testid="close-count" /></div>
                        <div><label class="pos-lbl" for="cl-note">Note {{ overThreshold() ? '(required — variance above threshold)' : '(optional)' }}</label>
                            <textarea id="cl-note" class="pos-input" rows="2" maxlength="500" [ngModel]="note()" (ngModelChange)="note.set($event)" data-testid="close-note"></textarea></div>
                        @if (error()) { <p class="err-text" data-testid="close-error">{{ error() }}</p> }
                        <div class="row">
                            <button class="pos-btn" type="button" (click)="resume()" [disabled]="busy() || !conn.online()" data-testid="close-resume">Resume selling</button>
                            <span class="grow"></span>
                            <button class="pos-btn primary lg" type="button" (click)="submit()" [disabled]="!canSubmit()" data-testid="close-submit">Close shift</button>
                        </div>
                    </div>
                }
            } @else {
                <p class="muted">There is no shift on this terminal.</p>
            }
            }
        </div>
    `
})
export class PosShiftCloseComponent implements OnInit {
    readonly state = inject(PosState);
    readonly sync = inject(PosSync);
    readonly conn = inject(PosConnectivity);
    private readonly shifts = inject(PosShifts);
    private readonly auth = inject(PosAuth);
    private readonly api = inject(PosApi);
    private readonly router = inject(Router);

    readonly counted = signal<number | null>(null);
    readonly note = signal('');
    readonly busy = signal(false);
    readonly error = signal<string | null>(null);
    readonly result = signal<Shift | null>(null);

    readonly variance = computed(() => cashVariance(this.counted(), this.state.shift()?.expectedCashClosing ?? 0));
    readonly overThreshold = computed(() => {
        const v = this.variance();
        return v !== null && Math.abs(v) > (this.state.shift()?.varianceThreshold ?? 0);
    });
    readonly canSubmit = computed(
        () =>
            !this.busy() &&
            this.conn.online() &&
            this.counted() !== null &&
            this.counted()! >= 0 &&
            this.sync.pendingCount() === 0 &&
            this.sync.errorCount() === 0 &&
            (!this.overThreshold() || this.note().trim().length > 0)
    );

    ngOnInit(): void {
        void this.reload();
    }

    private async reload(): Promise<void> {
        await this.sync.pushAll();
        const shift = await this.shifts.refreshCurrent();
        if (shift && this.conn.online()) {
            try {
                const d = await this.api.shiftDetail(shift.uuid);
                const { sessions: _s, cashMovements: _c, ...plain } = d;
                await this.state.setShift({ ...shift, ...plain });
            } catch {
                /* keep current */
            }
        }
    }

    async syncNow(): Promise<void> {
        this.busy.set(true);
        try {
            await this.sync.pushAll(true);
            await this.reload();
        } finally {
            this.busy.set(false);
        }
    }

    async startClose(): Promise<void> {
        await this.run(async () => {
            await this.shifts.startClose();
            await this.reload();
        });
    }

    async resume(): Promise<void> {
        await this.run(async () => {
            await this.shifts.resume();
            await this.shifts.ensureSession();
            await this.router.navigate(['/pos/sell']);
        });
    }

    async submit(): Promise<void> {
        if (!this.canSubmit()) return;
        await this.run(async () => {
            const closed = await this.shifts.close(this.counted() ?? 0, this.note().trim() || null);
            this.result.set(closed);
        });
    }

    done(): void {
        this.auth.signOut();
        void this.router.navigate(['/pos']);
    }

    private async run(fn: () => Promise<void>): Promise<void> {
        this.busy.set(true);
        this.error.set(null);
        try {
            await fn();
        } catch (e) {
            this.error.set(errorMessage(e));
        } finally {
            this.busy.set(false);
        }
    }
}
