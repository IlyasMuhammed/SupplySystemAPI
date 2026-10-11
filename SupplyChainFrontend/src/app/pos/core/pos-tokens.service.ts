import { Injectable, signal } from '@angular/core';

export interface TokenPair {
    accessToken: string;
    refreshToken: string;
}

export interface DeviceSignInInfo {
    email: string;
    name: string;
    signedInAt: string;
}

const K_DEVICE = 'sms-pos.device.tokens';
const K_DEVICE_INFO = 'sms-pos.device.user';
const K_CASHIER = 'sms-pos.cashier.tokens';
const K_DEVICE_UUID = 'sms-pos.deviceUuid';

function read<T>(storage: Storage | undefined, key: string): T | null {
    try {
        const raw = storage?.getItem(key);
        return raw ? (JSON.parse(raw) as T) : null;
    } catch {
        return null;
    }
}

function write(storage: Storage | undefined, key: string, value: unknown): void {
    try {
        if (value === null || value === undefined) storage?.removeItem(key);
        else storage?.setItem(key, JSON.stringify(value));
    } catch {
        /* storage full / blocked: tokens stay in memory */
    }
}

/** JWT payload, or null. */
export function jwtPayload(token: string | null | undefined): Record<string, unknown> | null {
    if (!token) return null;
    try {
        const part = token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/');
        return JSON.parse(decodeURIComponent(escape(atob(part))));
    } catch {
        return null;
    }
}

/** Expired (or within `skewMs` of expiring). Tokens without `exp` never expire client-side. */
export function isTokenExpired(token: string | null | undefined, skewMs = 5000): boolean {
    const p = jwtPayload(token);
    if (!p) return true;
    const exp = p['exp'];
    return typeof exp === 'number' ? exp * 1000 <= Date.now() + skewMs : false;
}

/**
 * A38 D-4 — the POS keeps its own tokens and never touches the portal's `accessToken` / `refreshToken`:
 *  - **device** pair: the supervisor who signed this device in (bind, sync pulls, hub, PIN sign-in calls) — localStorage;
 *  - **cashier** pair: returned by `POST api/pos/auth/pin` for the PIN-signed-in cashier — sessionStorage + memory,
 *    so closing the browser always requires a PIN again.
 */
@Injectable({ providedIn: 'root' })
export class PosTokens {
    private readonly local = typeof localStorage !== 'undefined' ? localStorage : undefined;
    private readonly session = typeof sessionStorage !== 'undefined' ? sessionStorage : undefined;

    readonly device = signal<TokenPair | null>(read<TokenPair>(this.local, K_DEVICE));
    readonly deviceInfo = signal<DeviceSignInInfo | null>(read<DeviceSignInInfo>(this.local, K_DEVICE_INFO));
    readonly cashier = signal<TokenPair | null>(read<TokenPair>(this.session, K_CASHIER));

    /** Stable id of this browser profile (§2 bind / by-device). */
    deviceUuid(): string {
        let id: string | null = null;
        try {
            id = this.local?.getItem(K_DEVICE_UUID) ?? null;
        } catch {
            id = null;
        }
        if (!id) {
            id = typeof crypto !== 'undefined' && 'randomUUID' in crypto ? crypto.randomUUID() : fallbackUuid();
            try {
                this.local?.setItem(K_DEVICE_UUID, id);
            } catch {
                /* ignore */
            }
        }
        return id;
    }

    setDevice(pair: TokenPair | null, info?: DeviceSignInInfo | null): void {
        this.device.set(pair);
        write(this.local, K_DEVICE, pair);
        if (info !== undefined) {
            this.deviceInfo.set(info);
            write(this.local, K_DEVICE_INFO, info);
        }
    }

    setCashier(pair: TokenPair | null): void {
        this.cashier.set(pair);
        write(this.session, K_CASHIER, pair);
    }

    clearAll(): void {
        this.setCashier(null);
        this.setDevice(null, null);
    }
}

function fallbackUuid(): string {
    const b = new Uint8Array(16);
    crypto.getRandomValues(b);
    b[6] = (b[6] & 0x0f) | 0x40;
    b[8] = (b[8] & 0x3f) | 0x80;
    const h = [...b].map(x => x.toString(16).padStart(2, '0')).join('');
    return `${h.slice(0, 8)}-${h.slice(8, 12)}-${h.slice(12, 16)}-${h.slice(16, 20)}-${h.slice(20)}`;
}
