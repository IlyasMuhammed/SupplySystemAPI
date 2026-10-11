import { DatePipe } from '@angular/common';
import { Component, OnInit, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { PosAuth } from '../core/pos-auth.service';
import { PosBoot } from '../core/pos-boot.service';
import { PosConnectivity } from '../core/pos-connectivity.service';
import { PosDb } from '../core/pos-db';
import { PosHttp, errorMessage } from '../core/pos-http.service';
import { LocalCashMovement, LocalOrder, LocalReturn, POS_PERM, SyncLogEntry } from '../core/pos.models';
import { PosState } from '../core/pos-state.service';
import { PosSync } from '../core/pos-sync.service';
import { PosTokens } from '../core/pos-tokens.service';
import { PosLabelPipe, PosMoneyPipe } from './pos-money.pipe';

/** A38 — device & sync: terminal identity, queue + failures with retry, manual / full sync, sync log, device sign-in, unbind. */
@Component({
    selector: 'pos-settings',
    standalone: true,
    imports: [FormsModule, RouterLink, DatePipe, PosMoneyPipe, PosLabelPipe],
    template: `
        <div class="pos-page" data-testid="pos-settings">
            <div class="row"><a class="pos-btn" routerLink="/pos/sell"><i class="pi pi-arrow-left"></i></a><h1 class="grow">Device &amp; sync</h1></div>

            <div class="pos-card stack">
                <h2>This device</h2>
                <div class="pos-kv">
                    <span>Store</span><span>{{ state.store()?.name }} ({{ state.store()?.code }})</span>
                    <span>Terminal</span><span>{{ state.terminal()?.name }} ({{ state.terminal()?.code }})</span>
                    <span>Device id</span><span class="mono">{{ deviceUuid }}</span>
                    <span>Signed in by</span><span>{{ tokens.deviceInfo()?.name || '—' }} {{ tokens.deviceInfo()?.email ? '(' + tokens.deviceInfo()?.email + ')' : '' }}</span>
                    <span>Currency</span><span>{{ state.currency() }} · tax {{ state.taxPercent() }}% {{ state.taxInclusive() ? 'included' : 'added' }}</span>
                </div>
                @if (http.deviceSignInRequired() || !tokens.device()) {
                    <div class="pos-banner wn">The device sign-in has expired. A supervisor signs in again to keep syncing.</div>
                    <div class="row wrap">
                        <input class="pos-input" style="max-width: 260px" type="email" placeholder="Email" [(ngModel)]="email" />
                        <input class="pos-input" style="max-width: 200px" type="password" placeholder="Password" [(ngModel)]="password" (keyup.enter)="signInDevice()" />
                        <button class="pos-btn primary" type="button" (click)="signInDevice()" [disabled]="busy() || !email || !password || !conn.online()">Sign in</button>
                    </div>
                }
            </div>

            <div class="pos-card stack">
                <div class="row wrap"><h2 class="grow">Sync</h2>
                    <button class="pos-btn" type="button" (click)="syncNow()" [disabled]="busy() || !conn.online()" data-testid="settings-sync"><i class="pi pi-sync"></i> Sync now</button>
                    <button class="pos-btn" type="button" (click)="fullSync()" [disabled]="busy() || !conn.online()" data-testid="settings-full-sync">Full sync</button>
                    @if (sync.errorCount() > 0) { <button class="pos-btn" type="button" (click)="retry()" [disabled]="busy() || !conn.online()" data-testid="settings-retry">Retry failed</button> }
                </div>
                <div class="pos-kv">
                    <span>Connection</span><span>{{ conn.online() ? 'Online' : 'Offline' }} · live updates {{ conn.hubConnected() ? 'connected' : 'not connected' }}</span>
                    <span>Last data refresh</span><span>{{ sync.lastPullAt() ? (sync.lastPullAt() | date: 'dd MMM HH:mm') : 'never' }} @if (sync.stale()) { <span class="pos-pill wn">Stale</span> }</span>
                    <span>Last stock snapshot</span><span>{{ sync.lastStockAt() ? (sync.lastStockAt() | date: 'dd MMM HH:mm') : 'never' }}</span>
                    <span>Queued</span><span>{{ sync.pendingCount() }}</span>
                    <span>Failed</span><span [class.err-text]="sync.errorCount() > 0">{{ sync.errorCount() }}</span>
                </div>
                @if (sync.pulling()) { <p class="faint"><i class="pi pi-spin pi-spinner"></i> {{ sync.progress() }}</p> }
                @if (sync.lastError()) { <p class="err-text">{{ sync.lastError() }}</p> }
                @if (failedOrders().length || failedCash().length || failedReturns().length) {
                    <table class="pos-table" data-testid="settings-failed">
                        <thead><tr><th>Item</th><th class="num">Amount</th><th>Error</th></tr></thead>
                        <tbody>
                            @for (o of failedOrders(); track o.localId) { <tr><td class="mono">{{ o.payload.offlineNumber }}</td><td class="num">{{ o.receipt.grandTotal | posMoney }}</td><td class="err-text">{{ o.lastError }}</td></tr> }
                            @for (r of failedReturns(); track r.localId) { <tr><td>{{ r.kind | posLabel }} of <span class="mono">{{ r.originalOrderNumber }}</span></td><td class="num">{{ r.receipt.net | posMoney }}</td><td class="err-text">{{ r.lastError }}</td></tr> }
                            @for (m of failedCash(); track m.localId) { <tr><td>Cash {{ m.direction }} · {{ m.reason | posLabel }}</td><td class="num">{{ m.amount | posMoney }}</td><td class="err-text">{{ m.lastError }}</td></tr> }
                        </tbody>
                    </table>
                }
            </div>

            <div class="pos-card">
                <h2>Sync log</h2>
                <table class="pos-table" data-testid="settings-log">
                    <thead><tr><th>Time</th><th>What</th><th>Result</th></tr></thead>
                    <tbody>
                        @for (l of log(); track l.id) {
                            <tr><td>{{ l.at | date: 'dd MMM HH:mm:ss' }}</td><td>{{ l.kind | posLabel }}</td><td [class.err-text]="!l.ok">{{ l.message }}</td></tr>
                        } @empty { <tr><td colspan="3" class="faint">Nothing yet.</td></tr> }
                    </tbody>
                </table>
            </div>

            <div class="pos-card stack">
                <h2>Binding</h2>
                <p class="muted">Unbinding releases the terminal and wipes this device's data, including the cached staff PIN hashes. Queued sales must be synced first.</p>
                @if (error()) { <p class="err-text" data-testid="settings-error">{{ error() }}</p> }
                <div class="row wrap">
                    @if (canUnbind()) {
                        <button class="pos-btn danger" type="button" (click)="unbind()" [disabled]="busy() || !conn.online()" data-testid="settings-unbind">Unbind this device</button>
                    } @else {
                        <span class="faint">Unbinding needs the "manage terminals" permission (store manager PIN).</span>
                    }
                </div>
            </div>
        </div>
    `
})
export class PosSettingsComponent implements OnInit {
    readonly state = inject(PosState);
    readonly sync = inject(PosSync);
    readonly conn = inject(PosConnectivity);
    readonly tokens = inject(PosTokens);
    readonly http = inject(PosHttp);
    private readonly auth = inject(PosAuth);
    private readonly boot = inject(PosBoot);
    private readonly db = inject(PosDb);
    private readonly router = inject(Router);

    readonly deviceUuid = this.tokens.deviceUuid();
    readonly busy = signal(false);
    readonly error = signal<string | null>(null);
    readonly log = signal<SyncLogEntry[]>([]);
    readonly failedOrders = signal<LocalOrder[]>([]);
    readonly failedCash = signal<LocalCashMovement[]>([]);
    readonly failedReturns = signal<LocalReturn[]>([]);
    readonly canUnbind = computed(() => this.auth.has(POS_PERM.TERMINAL_MANAGE));

    email = this.tokens.deviceInfo()?.email ?? '';
    password = '';

    constructor() {
        effect(() => {
            this.sync.pendingCount();
            this.sync.errorCount();
            this.sync.lastPullAt();
            void this.reload();
        });
    }

    ngOnInit(): void {
        void this.reload();
    }

    async reload(): Promise<void> {
        const [log, fo, fc, fr] = await Promise.all([
            this.db.syncLog.orderBy('id').reverse().limit(50).toArray(),
            this.db.orders.where('syncStatus').equals('error').toArray(),
            this.db.cashMovements.where('syncStatus').equals('error').toArray(),
            this.db.returns.where('syncStatus').equals('error').toArray()
        ]);
        this.log.set(log);
        this.failedOrders.set(fo);
        this.failedCash.set(fc);
        this.failedReturns.set(fr);
    }

    async syncNow(): Promise<void> {
        await this.run(() => this.sync.sync('DELTA'));
    }

    async fullSync(): Promise<void> {
        await this.run(() => this.sync.sync('FULL'));
    }

    async retry(): Promise<void> {
        await this.run(() => this.sync.retryErrors());
    }

    async signInDevice(): Promise<void> {
        await this.run(async () => {
            await this.auth.signInDevice(this.email, this.password);
            this.password = '';
        });
    }

    async unbind(): Promise<void> {
        if (!confirm('Unbind this device from the terminal and wipe its data?')) return;
        await this.run(async () => {
            await this.boot.unbind();
            await this.router.navigate(['/pos']);
        });
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
            await this.reload();
        }
    }
}
