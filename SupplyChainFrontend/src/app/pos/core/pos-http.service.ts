import { HttpBackend, HttpClient, HttpErrorResponse, HttpHeaders, HttpParams } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { TimeoutError, firstValueFrom, timeout } from 'rxjs';
import { environment } from '../../../environments/environment';
import { ApiResponse } from './pos.models';
import { PosConnectivity } from './pos-connectivity.service';
import { PosTokens, TokenPair, isTokenExpired } from './pos-tokens.service';

/** No answer from the API (offline, DNS, timeout). Retryable. */
export class PosNetworkError extends Error {
    readonly network = true;
    constructor(message = 'The server could not be reached.') {
        super(message);
        this.name = 'PosNetworkError';
    }
}

/** The API answered with an error (or `success: false`). `status` 0 is never used here. */
export class PosHttpError extends Error {
    constructor(
        readonly status: number,
        message: string,
        readonly body?: unknown
    ) {
        super(message);
        this.name = 'PosHttpError';
    }
    /** 5xx / 408 / 429 — worth retrying later. */
    get retryable(): boolean {
        return this.status >= 500 || this.status === 408 || this.status === 429;
    }
}

export function isNetworkError(e: unknown): e is PosNetworkError {
    return e instanceof PosNetworkError;
}

export function errorMessage(e: unknown, fallback = 'Something went wrong.'): string {
    if (e instanceof PosHttpError || e instanceof PosNetworkError) return e.message || fallback;
    if (e instanceof Error && e.message) return e.message;
    return fallback;
}

/** Which token a call carries: the device's, the PIN-signed-in cashier's, cashier-else-device, or none. */
export type PosAuth = 'device' | 'cashier' | 'auto' | 'none';

export interface PosRequestOptions {
    body?: unknown;
    params?: Record<string, string | number | boolean | null | undefined>;
    auth?: PosAuth;
    headers?: Record<string, string>;
    timeoutMs?: number;
    /** Return the raw body instead of unwrapping ApiResponse.result. */
    raw?: boolean;
}

/**
 * A38 — the POS's own HTTP layer. Uses HttpBackend directly, so the portal's global interceptors (which attach and rotate
 * the portal's `accessToken`) never run for POS calls: POS calls carry the cashier's or the device's token from PosTokens,
 * refresh them on expiry / 401 (one refresh in flight per pair, refresh tokens rotate), unwrap `ApiResponse<T>`, and feed
 * PosConnectivity.
 */
@Injectable({ providedIn: 'root' })
export class PosHttp {
    private readonly http = new HttpClient(inject(HttpBackend));
    private readonly tokens = inject(PosTokens);
    private readonly conn = inject(PosConnectivity);
    readonly base = environment.apiUrl;

    /** Set when the device's refresh token was refused — a supervisor must sign this device in again. */
    readonly deviceSignInRequired = signal(false);
    /** Set when the cashier's refresh token was refused — the cashier must re-enter the PIN for online calls. */
    readonly cashierTokenExpired = signal(false);

    private refreshing = new Map<'device' | 'cashier', Promise<TokenPair | null>>();

    get<T>(path: string, opts: PosRequestOptions = {}): Promise<T> {
        return this.request<T>('GET', path, opts);
    }
    post<T>(path: string, body?: unknown, opts: PosRequestOptions = {}): Promise<T> {
        return this.request<T>('POST', path, { ...opts, body: body ?? {} });
    }
    put<T>(path: string, body?: unknown, opts: PosRequestOptions = {}): Promise<T> {
        return this.request<T>('PUT', path, { ...opts, body: body ?? {} });
    }
    patch<T>(path: string, body?: unknown, opts: PosRequestOptions = {}): Promise<T> {
        return this.request<T>('PATCH', path, { ...opts, body: body ?? {} });
    }

    async request<T>(method: string, path: string, opts: PosRequestOptions = {}): Promise<T> {
        const auth = opts.auth ?? 'auto';
        const kind = this.kindFor(auth);
        let token = kind ? await this.validToken(kind) : null;
        if (auth === 'cashier' && !token) throw new PosHttpError(401, 'Your session has expired. Sign in with your PIN again.');

        try {
            return await this.send<T>(method, path, opts, token);
        } catch (e) {
            if (e instanceof PosHttpError && e.status === 401 && kind) {
                const pair = await this.refresh(kind);
                token = pair?.accessToken ?? null;
                if (!token && auth === 'auto' && kind === 'cashier') token = await this.validToken('device');
                if (!token) throw e;
                return this.send<T>(method, path, opts, token);
            }
            throw e;
        }
    }

    /** Raw login (POST api/auth/login) — never writes the portal's tokens. */
    async login(email: string, password: string): Promise<{ accessToken: string; refreshToken: string; user: { firstName?: string; lastName?: string; email?: string } }> {
        return this.send('POST', 'auth/login', { body: { email, password } }, null);
    }

    private kindFor(auth: PosAuth): 'device' | 'cashier' | null {
        if (auth === 'none') return null;
        if (auth === 'device') return 'device';
        if (auth === 'cashier') return 'cashier';
        return this.tokens.cashier() && !this.cashierTokenExpired() ? 'cashier' : 'device';
    }

    private async validToken(kind: 'device' | 'cashier'): Promise<string | null> {
        const pair = kind === 'device' ? this.tokens.device() : this.tokens.cashier();
        if (!pair) return null;
        if (!isTokenExpired(pair.accessToken)) return pair.accessToken;
        const refreshed = await this.refresh(kind);
        return refreshed?.accessToken ?? null;
    }

    /** Refreshes a pair once even when several calls ask at the same time (refresh tokens rotate). */
    refresh(kind: 'device' | 'cashier'): Promise<TokenPair | null> {
        const inFlight = this.refreshing.get(kind);
        if (inFlight) return inFlight;
        const p = this.doRefresh(kind).finally(() => this.refreshing.delete(kind));
        this.refreshing.set(kind, p);
        return p;
    }

    /** A fresh device access token (hub `accessTokenFactory`). */
    async deviceAccessToken(): Promise<string> {
        return (await this.validToken('device')) ?? '';
    }

    private async doRefresh(kind: 'device' | 'cashier'): Promise<TokenPair | null> {
        const pair = kind === 'device' ? this.tokens.device() : this.tokens.cashier();
        if (!pair?.refreshToken) return null;
        try {
            const res = await this.send<TokenPair>('POST', 'auth/refresh', { body: { refreshToken: pair.refreshToken } }, null);
            const next = { accessToken: res.accessToken, refreshToken: res.refreshToken };
            if (kind === 'device') {
                this.tokens.setDevice(next);
                this.deviceSignInRequired.set(false);
            } else {
                this.tokens.setCashier(next);
                this.cashierTokenExpired.set(false);
            }
            return next;
        } catch (e) {
            if (e instanceof PosHttpError && (e.status === 400 || e.status === 401 || e.status === 403)) {
                if (kind === 'device') this.deviceSignInRequired.set(true);
                else this.cashierTokenExpired.set(true);
            }
            return null;
        }
    }

    private async send<T>(method: string, path: string, opts: PosRequestOptions, token: string | null): Promise<T> {
        let headers = new HttpHeaders(opts.headers ?? {});
        if (token) headers = headers.set('Authorization', `Bearer ${token}`);
        let params = new HttpParams();
        for (const [k, v] of Object.entries(opts.params ?? {})) {
            if (v !== null && v !== undefined && v !== '') params = params.set(k, String(v));
        }
        const url = `${this.base}/${path.replace(/^\//, '')}`;
        let body: ApiResponse<T> | T;
        try {
            body = await firstValueFrom(
                this.http.request<ApiResponse<T>>(method, url, { body: opts.body, headers, params }).pipe(timeout(opts.timeoutMs ?? 20000))
            );
        } catch (e) {
            if (e instanceof HttpErrorResponse && e.status > 0) {
                this.conn.reportSuccess();
                const msg = (e.error && typeof e.error === 'object' && (e.error as { message?: string }).message) || statusText(e.status);
                throw new PosHttpError(e.status, msg, e.error);
            }
            // A slow answer is not an unreachable API: only a real transport failure (status 0) flips the till offline,
            // otherwise a sluggish server makes the badge flap. The caller still gets a retryable network error.
            if (!(e instanceof TimeoutError)) this.conn.reportFailure();
            throw new PosNetworkError();
        }
        this.conn.reportSuccess();
        if (opts.raw) return body as T;
        const wrapped = body as ApiResponse<T>;
        if (wrapped && typeof wrapped === 'object' && 'success' in wrapped) {
            if (!wrapped.success) throw new PosHttpError(400, wrapped.message || 'The request was refused.', wrapped);
            return wrapped.result;
        }
        return body as T;
    }
}

function statusText(status: number): string {
    if (status === 401) return 'Not signed in.';
    if (status === 403) return 'You do not have permission to do this.';
    if (status === 404) return 'Not found.';
    if (status === 409) return 'This record was changed by someone else. Reload and try again.';
    if (status >= 500) return 'The server had a problem. Try again shortly.';
    return 'The request was refused.';
}
