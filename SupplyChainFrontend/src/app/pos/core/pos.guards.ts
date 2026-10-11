import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { PosAuth } from './pos-auth.service';
import { PosBoot } from './pos-boot.service';
import { PosState } from './pos-state.service';
import { POS_PERM } from './pos.models';

/** Parent guard of the /pos tree: restores the device state from Dexie before any child guard reads it. */
export const posInitGuard: CanActivateFn = async () => {
    await inject(PosBoot).init();
    return true;
};

/** A bound terminal and a signed-in, unlocked cashier — otherwise back to the PIN screen. */
export const posCashierGuard: CanActivateFn = () => {
    const state = inject(PosState);
    const auth = inject(PosAuth);
    const router = inject(Router);
    if (!state.terminal() || !auth.signedIn()) return router.createUrlTree(['/pos']);
    return true;
};

/** Selling needs an OPEN shift — otherwise the shift screen (open it, or see why not). */
export const posOpenShiftGuard: CanActivateFn = () => {
    const state = inject(PosState);
    const router = inject(Router);
    if (state.shift()?.status !== 'OPEN') return router.createUrlTree(['/pos/shift']);
    return true;
};

/** A39: returns / exchanges need FEATURE_POS_RETURNS (config `features.returns`) and POS_ORDER_RETURN. */
export const posReturnsGuard: CanActivateFn = () => {
    const state = inject(PosState);
    const auth = inject(PosAuth);
    const router = inject(Router);
    if (state.features().returns && auth.has(POS_PERM.ORDER_RETURN)) return true;
    return router.createUrlTree(['/pos/sell']);
};

/** Any of the listed permissions (route data `posPermissions`). */
export const posPermissionGuard: CanActivateFn = route => {
    const auth = inject(PosAuth);
    const router = inject(Router);
    const perms = (route.data?.['posPermissions'] as string[] | undefined) ?? [];
    if (!perms.length || perms.some(p => auth.has(p))) return true;
    return router.createUrlTree(['/pos/sell']);
};
