import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { PosApi } from '../core/pos-api.service';
import { PosAuth } from '../core/pos-auth.service';
import { PosBoot } from '../core/pos-boot.service';
import { PosConnectivity } from '../core/pos-connectivity.service';
import { errorMessage } from '../core/pos-http.service';
import { PosHttp } from '../core/pos-http.service';
import { POS_PERM, PosTerminal } from '../core/pos.models';
import { PosShifts } from '../core/pos-shift.service';
import { PosState } from '../core/pos-state.service';
import { PosSync } from '../core/pos-sync.service';
import { PosTokens } from '../core/pos-tokens.service';
import { PinPadComponent } from './pin-pad.component';

type Step = 'loading' | 'device' | 'pick' | 'syncing' | 'pin';

/**
 * `/pos` — device sign-in (supervisor, existing login API) → terminal auto-identify by device or picker + bind →
 * first sync → PIN pad. Also the lock screen: the same PIN resumes, another PIN switches cashier (cart cleared).
 */
@Component({
    selector: 'pos-start',
    standalone: true,
    imports: [FormsModule, PinPadComponent],
    template: `
        <div class="pos-center">
            <div class="pos-card" data-testid="pos-start">
                <div class="pos-brand"><i class="pi pi-shop"></i> SMS POS
                    <span class="grow"></span>
                    <span class="pos-pill" [class.ok]="conn.online()" [class.er]="!conn.online()">{{ conn.online() ? 'Online' : 'Offline' }}</span>
                </div>

                @switch (step()) {
                    @case ('loading') {
                        <p class="muted"><i class="pi pi-spin pi-spinner"></i> Starting…</p>
                    }
                    @case ('device') {
                        <div class="stack" data-testid="pos-device-login">
                            <h1>Sign in this device</h1>
                            <p class="muted">A supervisor or store manager signs in once to connect this device to a terminal. Cashiers then use their PIN.</p>
                            <div><label class="pos-lbl" for="pos-email">Email</label><input id="pos-email" class="pos-input" type="email" autocomplete="username" [(ngModel)]="email" /></div>
                            <div><label class="pos-lbl" for="pos-pw">Password</label><input id="pos-pw" class="pos-input" type="password" autocomplete="current-password" [(ngModel)]="password" (keyup.enter)="signInDevice()" /></div>
                            @if (error()) { <p class="err-text" data-testid="pos-error">{{ error() }}</p> }
                            <button class="pos-btn primary lg block" type="button" [disabled]="busy() || !email || !password || !conn.online()" (click)="signInDevice()">Sign in</button>
                            @if (!conn.online()) { <p class="faint">Signing a device in needs a connection.</p> }
                        </div>
                    }
                    @case ('pick') {
                        <div class="stack" data-testid="pos-terminal-picker">
                            <h1>Choose this device's terminal</h1>
                            <p class="muted">This device is not bound yet. Signed in as {{ tokens.deviceInfo()?.name }}.</p>
                            <div class="pos-list">
                                @for (t of terminals(); track t.uuid) {
                                    <button class="pos-btn" type="button" [disabled]="busy() || !t.isActive || (t.deviceBound && t.deviceUuid !== deviceUuid)" (click)="bind(t)">
                                        <span><strong>{{ t.name }}</strong> <span class="faint">{{ t.code }} · {{ t.storeName }}</span></span>
                                        <span class="faint">{{ !t.isActive ? 'Inactive' : t.deviceBound && t.deviceUuid !== deviceUuid ? 'Bound to another device' : 'Use' }}</span>
                                    </button>
                                } @empty {
                                    <p class="muted">No terminals are available to you. Ask a store manager to add one (Point of Sale › Stores › Terminals) and to add you to the store's staff.</p>
                                }
                            </div>
                            @if (error()) { <p class="err-text" data-testid="pos-error">{{ error() }}</p> }
                            <div class="row"><button class="pos-btn" type="button" (click)="loadTerminals()">Refresh</button><span class="grow"></span><button class="pos-btn ghost" type="button" (click)="signOutDevice()">Sign out device</button></div>
                        </div>
                    }
                    @case ('syncing') {
                        <div class="stack">
                            <h1>Preparing the terminal</h1>
                            <p class="muted"><i class="pi pi-spin pi-spinner"></i> Downloading {{ sync.progress() || 'data' }}</p>
                        </div>
                    }
                    @case ('pin') {
                        <div class="stack" data-testid="pos-pin">
                            <div>
                                <h1>{{ auth.locked() ? 'Locked' : 'Enter your PIN' }}</h1>
                                <p class="muted">{{ state.store()?.name }} · {{ state.terminal()?.name }}</p>
                                @if (auth.locked()) { <p class="faint">{{ auth.cashier()?.name }} can resume with their PIN; another PIN switches cashier and clears the cart.</p> }
                            </div>
                            @if (http.deviceSignInRequired()) { <p class="pos-pill wn">The device sign-in expired — a supervisor should sign the device in again (Settings).</p> }
                            <pos-pin-pad [disabled]="busy() || lockedOut()" [busy]="busy()" (entered)="signIn($event)"></pos-pin-pad>
                            @if (error()) { <p class="err-text" data-testid="pos-error">{{ error() }}</p> }
                        </div>
                    }
                }
            </div>
        </div>
    `
})
export class PosStartComponent implements OnInit, OnDestroy {
    readonly auth = inject(PosAuth);
    readonly state = inject(PosState);
    readonly sync = inject(PosSync);
    readonly conn = inject(PosConnectivity);
    readonly tokens = inject(PosTokens);
    readonly http = inject(PosHttp);
    private readonly boot = inject(PosBoot);
    private readonly api = inject(PosApi);
    private readonly shifts = inject(PosShifts);
    private readonly router = inject(Router);

    readonly step = signal<Step>('loading');
    readonly busy = signal(false);
    readonly error = signal<string | null>(null);
    readonly terminals = signal<PosTerminal[]>([]);
    private readonly tick = signal(Date.now());
    readonly lockedOut = computed(() => {
        this.tick();
        return this.auth.lockRemainingMs() > 0;
    });
    readonly deviceUuid = this.tokens.deviceUuid();

    email = '';
    password = '';

    private timer?: ReturnType<typeof setInterval>;

    ngOnInit(): void {
        this.timer = setInterval(() => this.tick.set(Date.now()), 5000);
        void this.continue();
    }

    ngOnDestroy(): void {
        clearInterval(this.timer);
    }

    /** Walks the start-up steps from wherever this device is. */
    async continue(): Promise<void> {
        this.error.set(null);
        if (!this.tokens.device() && !this.state.terminal()) {
            this.step.set('device');
            return;
        }
        this.step.set('loading');
        await this.boot.checkModule();
        if (this.boot.moduleStatus() === 'disabled') return;
        const tc = await this.boot.identifyTerminal();
        if (!tc) {
            if (!this.tokens.device()) {
                this.step.set('device');
                return;
            }
            this.step.set('pick');
            await this.loadTerminals();
            return;
        }
        if (!this.boot.activated()) {
            this.step.set('syncing');
            await this.boot.activate();
        }
        if (this.auth.signedIn()) {
            await this.routeAfterSignIn();
            return;
        }
        this.step.set('pin');
    }

    async signInDevice(): Promise<void> {
        this.busy.set(true);
        this.error.set(null);
        try {
            await this.auth.signInDevice(this.email, this.password);
            this.password = '';
            await this.continue();
        } catch (e) {
            this.error.set(errorMessage(e, 'Sign-in failed.'));
        } finally {
            this.busy.set(false);
        }
    }

    async loadTerminals(): Promise<void> {
        try {
            this.terminals.set(await this.api.availableTerminals());
        } catch (e) {
            this.error.set(errorMessage(e, 'Could not load the terminals.'));
        }
    }

    async bind(t: PosTerminal): Promise<void> {
        this.busy.set(true);
        this.error.set(null);
        try {
            await this.boot.bind(t.uuid);
            await this.continue();
        } catch (e) {
            this.error.set(errorMessage(e, 'Could not bind this terminal.'));
        } finally {
            this.busy.set(false);
        }
    }

    signOutDevice(): void {
        this.boot.signOutDevice();
        this.step.set('device');
    }

    async signIn(pin: string): Promise<void> {
        this.busy.set(true);
        this.error.set(null);
        try {
            const res = await this.auth.signInWithPin(pin);
            if (!res.ok) {
                this.error.set(res.message);
                return;
            }
            await this.routeAfterSignIn();
        } finally {
            this.busy.set(false);
        }
    }

    /** OPEN shift ⇒ selling; CLOSING ⇒ the close screen; none ⇒ the shift screen (open it, or ask a supervisor). */
    async routeAfterSignIn(): Promise<void> {
        const shift = await this.shifts.refreshCurrent();
        if (shift?.status === 'OPEN') {
            await this.shifts.ensureSession();
            await this.router.navigate(['/pos/sell']);
        } else if (shift?.status === 'CLOSING' && this.auth.has(POS_PERM.SHIFT_CLOSE)) {
            await this.router.navigate(['/pos/shift/close']);
        } else {
            await this.router.navigate(['/pos/shift']);
        }
    }
}
