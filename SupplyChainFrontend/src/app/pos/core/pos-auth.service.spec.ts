import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { PIN_MAX_FAILURES, PosAuth, PosCashier } from './pos-auth.service';
import { PosConnectivity } from './pos-connectivity.service';
import { PosDb } from './pos-db';
import { createIdentityV3Hash } from './pos-pin';
import { PosState } from './pos-state.service';
import { clearPosStorage, dropDb, fakeJwt, ok, posTestProviders, syncConfig, waitForRequest } from './pos-testing';
import { PosTokens } from './pos-tokens.service';

describe('A38 PosAuth (PIN sign-in)', () => {
    let auth: PosAuth;
    let db: PosDb;
    let conn: PosConnectivity;
    let http: HttpTestingController;
    let tokens: PosTokens;
    const salt = Uint8Array.from({ length: 16 }, (_, i) => i + 7);

    beforeEach(async () => {
        clearPosStorage();
        localStorage.setItem('accessToken', 'portal-access');
        localStorage.setItem('refreshToken', 'portal-refresh');
        TestBed.configureTestingModule({ providers: posTestProviders() });
        auth = TestBed.inject(PosAuth);
        db = TestBed.inject(PosDb);
        conn = TestBed.inject(PosConnectivity);
        http = TestBed.inject(HttpTestingController);
        tokens = TestBed.inject(PosTokens);
        TestBed.inject(PosState).syncConfig.set(syncConfig());
        tokens.setDevice({ accessToken: fakeJwt({ sub: 'supervisor' }), refreshToken: 'dev-r' });
        await db.staffPins.bulkPut([
            { userId: 7, name: 'Sara', posRole: 'CASHIER', pinHash: await createIdentityV3Hash('1234', salt, 1000, 1), permissions: ['POS_ORDER_CREATE'] },
            { userId: 9, name: 'Omar', posRole: 'SUPERVISOR', pinHash: await createIdentityV3Hash('5678', salt, 1000, 2), permissions: ['POS_ORDER_CREATE', 'POS_SHIFT_OPEN'] },
            { userId: 11, name: 'Viewer', posRole: 'CASHIER', pinHash: await createIdentityV3Hash('9999', salt, 1000, 1), permissions: ['POS_ORDER_VIEW'] }
        ]);
    });

    afterEach(async () => {
        http.verify();
        await dropDb(db);
        clearPosStorage();
        localStorage.removeItem('accessToken');
        localStorage.removeItem('refreshToken');
    });

    describe('offline', () => {
        beforeEach(() => conn.setBrowser(false));

        it('signs in against the cached hash and announces the new cashier', async () => {
            const seen: (PosCashier | null)[] = [];
            auth.cashierChanged$.subscribe(c => seen.push(c));
            const res = await auth.signInWithPin('5678');
            expect(res.ok).toBeTrue();
            expect(auth.cashier()).toEqual(jasmine.objectContaining({ userId: 9, name: 'Omar', mode: 'offline' }));
            expect(auth.has('POS_SHIFT_OPEN')).toBeTrue();
            expect(seen.length).toBe(1);
            expect(tokens.cashier()).toBeNull();
        });

        it('refuses staff without POS_ORDER_CREATE', async () => {
            const res = await auth.signInWithPin('9999');
            expect(res.ok).toBeFalse();
        });

        it('locks the PIN pad for 5 minutes after 5 wrong PINs', async () => {
            for (let i = 1; i < PIN_MAX_FAILURES; i++) {
                const r = await auth.signInWithPin('0000');
                expect(r.ok).toBeFalse();
                if (!r.ok) expect(r.message).toContain(`${PIN_MAX_FAILURES - i} attempt`);
            }
            const fifth = await auth.signInWithPin('0000');
            expect(fifth.ok).toBeFalse();
            if (!fifth.ok) expect(fifth.message).toContain('locked until');
            expect(auth.lockRemainingMs()).toBeGreaterThan(4 * 60 * 1000);
            const right = await auth.signInWithPin('1234');
            expect(right.ok).toBeFalse();
            expect(auth.cashier()).toBeNull();
        });

        it('lock + same PIN resumes without a switch; another PIN switches', async () => {
            await auth.signInWithPin('1234');
            auth.lock();
            expect(auth.signedIn()).toBeFalse();
            const same = await auth.signInWithPin('1234');
            expect(same.ok && same.switched).toBeFalse();
            expect(auth.signedIn()).toBeTrue();
            auth.lock();
            const other = await auth.signInWithPin('5678');
            expect(other.ok && other.switched).toBeTrue();
        });
    });

    describe('online', () => {
        it('uses POST api/pos/auth/pin with the device token and keeps the portal tokens untouched', async () => {
            const p = auth.signInWithPin('1234');
            const req = await waitForRequest(http, (u, m) => m === 'POST' && u.endsWith('/pos/auth/pin'));
            expect(req.request.headers.get('Authorization')).toBe(`Bearer ${tokens.device()!.accessToken}`);
            expect(req.request.body).toEqual({ terminalUuid: 'term-1', pin: '1234', userId: null });
            const cashierJwt = fakeJwt({ sub: '7' });
            req.flush(ok({ accessToken: cashierJwt, refreshToken: 'c-r', user: { userId: 7, name: 'Sara', posRole: 'CASHIER', permissions: ['POS_ORDER_CREATE'] } }));
            const res = await p;
            expect(res.ok).toBeTrue();
            expect(auth.cashier()!.mode).toBe('online');
            expect(tokens.cashier()).toEqual({ accessToken: cashierJwt, refreshToken: 'c-r' });
            expect(localStorage.getItem('accessToken')).toBe('portal-access');
            expect(localStorage.getItem('refreshToken')).toBe('portal-refresh');
        });

        it('counts "Invalid PIN." but not other refusals', async () => {
            let p = auth.signInWithPin('1111');
            (await waitForRequest(http, u => u.endsWith('/pos/auth/pin'))).flush({ success: false, message: 'Invalid PIN.' }, { status: 400, statusText: 'Bad Request' });
            expect((await p).ok).toBeFalse();
            expect(auth.pinLock().failures).toBe(1);

            p = auth.signInWithPin('2222');
            (await waitForRequest(http, u => u.endsWith('/pos/auth/pin'))).flush({ success: false, message: 'You are not staff of this store.' }, { status: 400, statusText: 'Bad Request' });
            const r = await p;
            expect(r.ok).toBeFalse();
            if (!r.ok) expect(r.message).toBe('You are not staff of this store.');
            expect(auth.pinLock().failures).toBe(1);
        });

        it('passes the server lockout message through', async () => {
            const p = auth.signInWithPin('1234');
            (await waitForRequest(http, u => u.endsWith('/pos/auth/pin'))).flush({ success: false, message: 'Terminal locked until 14:05.' }, { status: 400, statusText: 'Bad Request' });
            const r = await p;
            expect(!r.ok && r.message).toBe('Terminal locked until 14:05.');
        });

        it('falls back to the offline check when the server cannot be reached', async () => {
            const p = auth.signInWithPin('1234');
            (await waitForRequest(http, u => u.endsWith('/pos/auth/pin'))).error(new ProgressEvent('error'));
            const r = await p;
            expect(r.ok).toBeTrue();
            expect(auth.cashier()!.mode).toBe('offline');
            expect(conn.online()).toBeFalse();
        });
    });
});
