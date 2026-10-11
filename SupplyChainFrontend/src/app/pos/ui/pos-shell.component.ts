import { Component, OnDestroy, OnInit, ViewEncapsulation, computed, inject, signal } from '@angular/core';
import { NavigationEnd, Router, RouterOutlet } from '@angular/router';
import { SwUpdate } from '@angular/service-worker';
import { MessageService } from 'primeng/api';
import { ToastModule } from 'primeng/toast';
import { filter } from 'rxjs';
import { PosAuth } from '../core/pos-auth.service';
import { PosBoot } from '../core/pos-boot.service';
import { PosConnectivity } from '../core/pos-connectivity.service';
import { POS_TOAST_KEY } from '../core/pos-hub.service';
import { PosState } from '../core/pos-state.service';
import { PosSync } from '../core/pos-sync.service';
import { PosTopbarComponent } from './pos-topbar.component';

/**
 * A38 — root of the full-screen `/pos` tree (no portal layout). Checks MODULE_POS itself (friendly page when off),
 * links the "SMS POS" web manifest, shows the stale-data (POS-SYN-07) and app-update banners and the POS toasts.
 */
@Component({
    selector: 'pos-shell',
    standalone: true,
    imports: [RouterOutlet, ToastModule, PosTopbarComponent],
    encapsulation: ViewEncapsulation.None,
    styleUrls: ['./pos.scss'],
    template: `
        <div class="pos-root" data-testid="pos-root">
            <p-toast [key]="toastKey" position="top-center"></p-toast>
            @if (boot.moduleStatus() === 'disabled') {
                <div class="pos-center" data-testid="pos-unlicensed">
                    <div class="pos-card">
                        <div class="pos-brand"><i class="pi pi-shop"></i> SMS POS</div>
                        <h1>Point of Sale is not switched on</h1>
                        <p class="muted">Your organization does not have the Point of Sale module enabled. An administrator can switch it on in Settings › Modules (it needs Inventory, Customers and Finance).</p>
                        <div class="row">
                            <button class="pos-btn" type="button" (click)="recheck()">Check again</button>
                            <a class="pos-btn ghost" href="/portal/dashboard">Back to the portal</a>
                        </div>
                    </div>
                </div>
            } @else {
                @if (showTopbar()) {
                    <pos-topbar></pos-topbar>
                }
                @if (updateReady()) {
                    <div class="pos-banner" data-testid="pos-update"><i class="pi pi-download"></i> A new version of the POS is ready. <button class="pos-btn" type="button" (click)="reload()">Reload</button></div>
                }
                @if (showStale()) {
                    <div class="pos-banner wn" data-testid="pos-stale">
                        <i class="pi pi-exclamation-triangle"></i>
                        Prices, stock and customers were last refreshed {{ lastPullText() }}. Connect to the network to sync — selling continues with the data on this device.
                    </div>
                }
                <main class="pos-main"><router-outlet></router-outlet></main>
            }
        </div>
    `
})
export class PosShellComponent implements OnInit, OnDestroy {
    readonly boot = inject(PosBoot);
    readonly auth = inject(PosAuth);
    readonly state = inject(PosState);
    readonly sync = inject(PosSync);
    readonly conn = inject(PosConnectivity);
    private readonly router = inject(Router);
    private readonly sw = inject(SwUpdate, { optional: true });
    private readonly messages = inject(MessageService);

    readonly toastKey = POS_TOAST_KEY;
    readonly updateReady = signal(false);
    private readonly url = signal(this.router.url);
    private added: HTMLElement[] = [];

    readonly showTopbar = computed(() => this.auth.signedIn() && !!this.state.terminal() && !/^\/pos\/?(\?.*)?$/.test(this.url()));
    readonly showStale = computed(() => !!this.state.terminal() && this.auth.signedIn() && this.sync.stale());
    readonly lastPullText = computed(() => {
        const at = this.sync.lastPullAt();
        if (!at) return 'never';
        const h = Math.floor((Date.now() - new Date(at).getTime()) / 3_600_000);
        return h >= 48 ? `${Math.floor(h / 24)} days ago` : `${h} hours ago`;
    });

    constructor() {
        this.router.events.pipe(filter(e => e instanceof NavigationEnd)).subscribe(e => this.url.set((e as NavigationEnd).urlAfterRedirects));
    }

    ngOnInit(): void {
        this.linkManifest();
        this.preloadScreens();
        void this.boot.checkModule();
        if (this.sw?.isEnabled) {
            this.sw.versionUpdates.pipe(filter(e => e.type === 'VERSION_READY')).subscribe(() => this.updateReady.set(true));
            this.sw.unrecoverable.subscribe(() => this.updateReady.set(true));
        }
    }

    ngOnDestroy(): void {
        this.added.forEach(el => el.remove());
        this.added = [];
    }

    async recheck(): Promise<void> {
        const s = await this.boot.checkModule();
        if (s === 'disabled') this.messages.add({ key: POS_TOAST_KEY, severity: 'warn', summary: 'Still switched off', detail: 'Point of Sale is still not enabled for your organization.' });
    }

    reload(): void {
        document.location.reload();
    }

    /**
     * Loads every POS screen's chunk once while online, so the service worker has cached all of them (its lazy group)
     * before the terminal ever goes offline.
     */
    private preloadScreens(): void {
        setTimeout(() => {
            void Promise.all([
                import('./pos-sell.component'),
                import('./pos-payment.component'),
                import('./pos-receipt.component'),
                import('./pos-shift.component'),
                import('./pos-shift-close.component'),
                import('./pos-history.component'),
                import('./pos-settings.component'),
                // A39
                import('./pos-return-lookup.component'),
                import('./pos-return-items.component'),
                import('./pos-return-refund.component'),
                import('./pos-exchange.component'),
                import('./pos-return-receipt.component'),
                import('./pos-credit-note.component')
            ]).catch(() => {});
        }, 3000);
    }

    /** The "SMS POS" manifest only applies to the POS (the portal stays a normal site). */
    private linkManifest(): void {
        if (typeof document === 'undefined' || document.querySelector('link[rel="manifest"]')) return;
        const link = document.createElement('link');
        link.rel = 'manifest';
        link.href = 'manifest.webmanifest';
        const theme = document.createElement('meta');
        theme.name = 'theme-color';
        theme.content = '#13233a'; // matches --sms-navy (meta tags cannot read CSS variables)
        document.head.append(link, theme);
        this.added.push(link, theme);
    }
}
