import { Component, computed, inject } from '@angular/core';
import { Router, RouterLink, RouterLinkActive } from '@angular/router';
import { TooltipModule } from 'primeng/tooltip';
import { PosAuth } from '../core/pos-auth.service';
import { PosConnectivity } from '../core/pos-connectivity.service';
import { PosState } from '../core/pos-state.service';
import { PosSync } from '../core/pos-sync.service';
import { PosLabelPipe } from './pos-money.pipe';

/** §17.2 top bar: online state, store, terminal, shift, cashier, sync queue, navigation and lock. */
@Component({
    selector: 'pos-topbar',
    standalone: true,
    imports: [RouterLink, RouterLinkActive, TooltipModule, PosLabelPipe],
    template: `
        <header class="pos-top" data-testid="pos-topbar">
            <div class="row">
                <i class="pi pi-shop" style="color: var(--sms-on-primary); font-size: 1.2rem"></i>
                <div>
                    <div class="t-name" data-testid="pos-store">{{ state.store()?.name }}</div>
                    <div class="t-sub">{{ state.terminal()?.name }}</div>
                </div>
            </div>
            @if (state.shift(); as s) {
                <span class="pos-pill hide-sm" [class.ok]="s.status === 'OPEN'" [class.wn]="s.status === 'CLOSING'" data-testid="pos-shift-pill">{{ s.shiftNumber }} · {{ s.status | posLabel }}</span>
            } @else {
                <span class="pos-pill wn hide-sm">No shift</span>
            }
            <span class="grow"></span>
            <span class="pos-pill" [class.ok]="conn.online()" [class.er]="!conn.online()" data-testid="pos-online" [pTooltip]="hubTip()" tooltipPosition="bottom">
                <i class="pi" [class.pi-wifi]="conn.online()" [class.pi-ban]="!conn.online()"></i>{{ conn.online() ? 'Online' : 'Offline' }}
            </span>
            @if (sync.pendingCount() > 0 || sync.errorCount() > 0) {
                <a class="pos-pill" [class.wn]="!sync.errorCount()" [class.er]="sync.errorCount() > 0" routerLink="/pos/settings" data-testid="pos-queue">
                    <i class="pi pi-sync"></i>{{ sync.pendingCount() }} queued@if (sync.errorCount()) { · {{ sync.errorCount() }} failed }
                </a>
            }
            <span class="t-sub hide-sm" data-testid="pos-cashier"><i class="pi pi-user"></i> {{ auth.cashier()?.name }}</span>
            <nav class="row">
                <a class="pos-btn" routerLink="/pos/sell" routerLinkActive="on" aria-label="Sell"><i class="pi pi-shopping-cart"></i><span class="hide-sm">Sell</span></a>
                <a class="pos-btn" routerLink="/pos/shift" routerLinkActive="on" [routerLinkActiveOptions]="{ exact: true }" aria-label="Shift"><i class="pi pi-clock"></i><span class="hide-sm">Shift</span></a>
                <a class="pos-btn" routerLink="/pos/history" routerLinkActive="on" aria-label="History"><i class="pi pi-history"></i><span class="hide-sm">History</span></a>
                <a class="pos-btn" routerLink="/pos/settings" routerLinkActive="on" aria-label="Settings"><i class="pi pi-cog"></i></a>
                <button class="pos-btn" type="button" (click)="lock()" data-testid="pos-lock" pTooltip="Lock / switch cashier" tooltipPosition="bottom" aria-label="Lock"><i class="pi pi-lock"></i></button>
            </nav>
        </header>
    `
})
export class PosTopbarComponent {
    readonly state = inject(PosState);
    readonly auth = inject(PosAuth);
    readonly conn = inject(PosConnectivity);
    readonly sync = inject(PosSync);
    private readonly router = inject(Router);

    readonly hubTip = computed(() => (this.conn.hubConnected() ? 'Live updates connected' : 'Live updates not connected'));

    lock(): void {
        this.auth.lock();
        void this.router.navigate(['/pos']);
    }
}
