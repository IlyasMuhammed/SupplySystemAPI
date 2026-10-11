import { Injectable, computed, inject, signal } from '@angular/core';
import { Subject } from 'rxjs';
import { PosApi } from './pos-api.service';
import { PosConnectivity } from './pos-connectivity.service';
import { PosDb } from './pos-db';
import { PosHttp, PosHttpError, isNetworkError } from './pos-http.service';
import { isValidPinShape, verifyIdentityV3Hash } from './pos-pin';
import { POS_PERM, PosRole } from './pos.models';
import { PosState } from './pos-state.service';
import { PosTokens } from './pos-tokens.service';

export interface PosCashier {
    userId: number;
    name: string;
    posRole: PosRole;
    permissions: string[];
    signedInAt: string;
    /** How the PIN was checked: by the server, or against the cached hash. */
    mode: 'online' | 'offline';
}

export interface PinLockState {
    failures: number;
    lockedUntil: number | null;
}

export type PinResult = { ok: true; switched: boolean; cashier: PosCashier } | { ok: false; message: string };

export const PIN_MAX_FAILURES = 5;
export const PIN_LOCK_MS = 5 * 60 * 1000;

const K_CASHIER_USER = 'sms-pos.cashier.user';
const K_PIN_LOCK = 'sms-pos.pinLock';

/**
 * A38 §3 / D-4 / POS-CSH — who is at the till.
 * Online, `POST api/pos/auth/pin` (device token) returns the cashier's own token pair, so every business call runs with the
 * cashier's permissions. Offline, the PIN is checked against the cached Identity v3 hashes of the store staff (WebCrypto
 * PBKDF2). 5 consecutive failures lock the PIN pad on this device for 5 minutes (the server keeps its own lock, POS-CSH-07).
 * Signing in as a different cashier is a switch: the cart is cleared (POS-CSH-05) via `cashierChanged$`.
 */
@Injectable({ providedIn: 'root' })
export class PosAuth {
    private readonly api = inject(PosApi);
    private readonly http = inject(PosHttp);
    private readonly tokens = inject(PosTokens);
    private readonly db = inject(PosDb);
    private readonly state = inject(PosState);
    private readonly conn = inject(PosConnectivity);

    readonly cashier = signal<PosCashier | null>(restoreCashier());
    /** A cashier restored after a reload starts locked: the same PIN resumes (cart kept), another PIN switches. */
    readonly locked = signal(!!this.cashier());
    readonly pinLock = signal<PinLockState>(restorePinLock());
    readonly signedIn = computed(() => !!this.cashier() && !this.locked());

    /** Emits the new cashier (or null) whenever the person at the till changes — not on lock/unlock by the same person. */
    readonly cashierChanged$ = new Subject<PosCashier | null>();

    has(perm: string): boolean {
        return this.cashier()?.permissions.includes(perm) ?? false;
    }

    hasDeviceSignIn(): boolean {
        return !!this.tokens.device();
    }

    /** Supervisor signs this device in with email + password (existing login API); the portal's tokens are untouched. */
    async signInDevice(email: string, password: string): Promise<void> {
        const res = await this.http.login(email.trim(), password);
        const name = [res.user?.firstName, res.user?.lastName].filter(Boolean).join(' ') || email;
        this.tokens.setDevice({ accessToken: res.accessToken, refreshToken: res.refreshToken }, { email: email.trim(), name, signedInAt: new Date().toISOString() });
        this.http.deviceSignInRequired.set(false);
    }

    /** Remaining lock in ms (0 when not locked). */
    lockRemainingMs(now = Date.now()): number {
        const until = this.pinLock().lockedUntil;
        return until && until > now ? until - now : 0;
    }

    async signInWithPin(pin: string): Promise<PinResult> {
        if (this.lockRemainingMs() > 0) {
            return { ok: false, message: `Too many wrong PINs. Try again at ${timeOf(this.pinLock().lockedUntil!)}.` };
        }
        if (!isValidPinShape(pin)) return { ok: false, message: 'PIN must be 4 to 6 digits.' };
        const terminal = this.state.terminal();
        if (!terminal) return { ok: false, message: 'This device is not bound to a terminal.' };

        let cashier: PosCashier | null = null;
        if (this.conn.online()) {
            try {
                const res = await this.api.pinSignIn(terminal.uuid, pin);
                this.tokens.setCashier({ accessToken: res.accessToken, refreshToken: res.refreshToken });
                this.http.cashierTokenExpired.set(false);
                cashier = { ...res.user, signedInAt: new Date().toISOString(), mode: 'online' };
            } catch (e) {
                if (!isNetworkError(e) && !(e instanceof PosHttpError && e.retryable)) {
                    const msg = e instanceof PosHttpError ? e.message : 'Wrong PIN.';
                    // The server's lockout message carries its time. Only a wrong PIN counts toward the local 5
                    // (POSA: "Invalid PIN."), not "not staff", "inactive" or permission refusals.
                    if (/locked/i.test(msg)) return { ok: false, message: msg };
                    if (!/invalid pin|wrong pin/i.test(msg) && e instanceof PosHttpError && e.status !== 401) return { ok: false, message: msg };
                    this.registerFailure();
                    return { ok: false, message: this.failureMessage(msg) };
                }
                // unreachable → fall through to the offline check
            }
        }

        if (!cashier) {
            cashier = await this.verifyOffline(pin);
            if (!cashier) {
                this.registerFailure();
                return { ok: false, message: this.failureMessage('Wrong PIN.') };
            }
            if (!cashier.permissions.includes(POS_PERM.ORDER_CREATE)) {
                return { ok: false, message: 'This staff member may not use the POS.' };
            }
            this.tokens.setCashier(null); // offline: business calls fall back to the device token (pushes only)
        }

        this.resetFailures();
        const previous = this.cashier();
        const switched = !previous || previous.userId !== cashier.userId;
        this.cashier.set(cashier);
        persistCashier(cashier);
        this.locked.set(false);
        if (switched) this.cashierChanged$.next(cashier);
        return { ok: true, switched, cashier };
    }

    /** Offline PIN check against the cached hashes (PIN is unique per store, so the first match is the person). */
    async verifyOffline(pin: string): Promise<PosCashier | null> {
        const staff = await this.db.staffPins.toArray();
        for (const s of staff) {
            if (await verifyIdentityV3Hash(s.pinHash, pin)) {
                return { userId: s.userId, name: s.name, posRole: s.posRole, permissions: s.permissions ?? [], signedInAt: new Date().toISOString(), mode: 'offline' };
            }
        }
        return null;
    }

    /**
     * A39 D-8 — a staff member's PIN checked against the cached hashes without signing them in (offline manager
     * authorization). Shares this device's PIN lock (5 wrong ⇒ 5 minutes).
     */
    async verifyStaffPinOffline(pin: string): Promise<{ ok: true; staff: PosCashier } | { ok: false; message: string }> {
        if (this.lockRemainingMs() > 0) return { ok: false, message: `Too many wrong PINs. Try again at ${timeOf(this.pinLock().lockedUntil!)}.` };
        if (!isValidPinShape(pin)) return { ok: false, message: 'PIN must be 4 to 6 digits.' };
        const staff = await this.verifyOffline(pin);
        if (!staff) {
            this.registerFailure();
            return { ok: false, message: this.failureMessage('Wrong PIN.') };
        }
        this.resetFailures();
        return { ok: true, staff };
    }

    /** Lock the till; the same cashier's PIN unlocks without clearing the cart. */
    lock(): void {
        if (this.cashier()) this.locked.set(true);
    }

    /** Sign the cashier out (shift close, ForceLogout, switch from the lock screen). */
    signOut(): void {
        const had = !!this.cashier();
        this.cashier.set(null);
        persistCashier(null);
        this.tokens.setCashier(null);
        this.locked.set(false);
        if (had) this.cashierChanged$.next(null);
    }

    private registerFailure(): void {
        const cur = this.pinLock();
        const failures = cur.failures + 1;
        const next: PinLockState = failures >= PIN_MAX_FAILURES ? { failures: 0, lockedUntil: Date.now() + PIN_LOCK_MS } : { failures, lockedUntil: null };
        this.pinLock.set(next);
        persistPinLock(next);
    }

    private resetFailures(): void {
        const next = { failures: 0, lockedUntil: null };
        this.pinLock.set(next);
        persistPinLock(next);
    }

    private failureMessage(base: string): string {
        const lock = this.pinLock();
        if (lock.lockedUntil) return `Too many wrong PINs. The PIN pad is locked until ${timeOf(lock.lockedUntil)}.`;
        const left = PIN_MAX_FAILURES - lock.failures;
        return `${base.replace(/\.$/, '')}. ${left} attempt${left === 1 ? '' : 's'} left.`;
    }
}

function timeOf(ms: number): string {
    const d = new Date(ms);
    return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
}

function restoreCashier(): PosCashier | null {
    try {
        const raw = sessionStorage.getItem(K_CASHIER_USER);
        return raw ? (JSON.parse(raw) as PosCashier) : null;
    } catch {
        return null;
    }
}

function persistCashier(c: PosCashier | null): void {
    try {
        if (c) sessionStorage.setItem(K_CASHIER_USER, JSON.stringify(c));
        else sessionStorage.removeItem(K_CASHIER_USER);
    } catch {
        /* ignore */
    }
}

function restorePinLock(): PinLockState {
    try {
        const raw = localStorage.getItem(K_PIN_LOCK);
        if (raw) return JSON.parse(raw) as PinLockState;
    } catch {
        /* ignore */
    }
    return { failures: 0, lockedUntil: null };
}

function persistPinLock(s: PinLockState): void {
    try {
        localStorage.setItem(K_PIN_LOCK, JSON.stringify(s));
    } catch {
        /* ignore */
    }
}
