import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, UrlTree } from '@angular/router';
import { PosAuth } from './pos-auth.service';
import { PosDb } from './pos-db';
import { posCashierGuard, posInitGuard, posOpenShiftGuard, posPermissionGuard } from './pos.guards';
import { PosState } from './pos-state.service';
import { clearPosStorage, dropDb, posTestProviders, syncConfig } from './pos-testing';
import { Shift } from './pos.models';

const run = (guard: typeof posCashierGuard, data: Record<string, unknown> = {}) =>
    TestBed.runInInjectionContext(() => guard({ data } as unknown as ActivatedRouteSnapshot, {} as RouterStateSnapshot));

const urlOf = (r: unknown) => TestBed.inject(Router).serializeUrl(r as UrlTree);

describe('A38 POS guards', () => {
    let state: PosState;
    let auth: PosAuth;

    beforeEach(() => {
        clearPosStorage();
        TestBed.configureTestingModule({ providers: posTestProviders() });
        state = TestBed.inject(PosState);
        auth = TestBed.inject(PosAuth);
    });

    afterEach(async () => {
        await dropDb(TestBed.inject(PosDb));
        clearPosStorage();
    });

    const signIn = (permissions: string[] = ['POS_ORDER_CREATE']) =>
        auth.cashier.set({ userId: 7, name: 'Sara', posRole: 'CASHIER', permissions, signedInAt: '', mode: 'online' });

    it('init guard restores the device state and lets the tree activate', async () => {
        expect(await run(posInitGuard)).toBeTrue();
        expect(state.loaded()).toBeTrue();
    });

    it('cashier guard sends an unbound device or a missing / locked cashier to /pos', () => {
        expect(urlOf(run(posCashierGuard))).toBe('/pos');
        state.syncConfig.set(syncConfig());
        expect(urlOf(run(posCashierGuard))).toBe('/pos');
        signIn();
        expect(run(posCashierGuard)).toBeTrue();
        auth.lock();
        expect(urlOf(run(posCashierGuard))).toBe('/pos');
    });

    it('open-shift guard sends to the shift screen unless the shift is OPEN', () => {
        expect(urlOf(run(posOpenShiftGuard))).toBe('/pos/shift');
        state.shift.set({ uuid: 's', status: 'CLOSING' } as Shift);
        expect(urlOf(run(posOpenShiftGuard))).toBe('/pos/shift');
        state.shift.set({ uuid: 's', status: 'OPEN' } as Shift);
        expect(run(posOpenShiftGuard)).toBeTrue();
    });

    it('permission guard needs one of the route permissions', () => {
        signIn(['POS_ORDER_CREATE']);
        expect(urlOf(run(posPermissionGuard, { posPermissions: ['POS_SHIFT_CLOSE'] }))).toBe('/pos/sell');
        signIn(['POS_ORDER_CREATE', 'POS_SHIFT_CLOSE']);
        expect(run(posPermissionGuard, { posPermissions: ['POS_SHIFT_CLOSE'] })).toBeTrue();
        expect(run(posPermissionGuard, {})).toBeTrue();
    });
});
