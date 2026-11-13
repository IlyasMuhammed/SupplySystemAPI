import { inject } from '@angular/core';
import { CanActivateFn, Router, UrlTree } from '@angular/router';
import { Observable, catchError, map, of } from 'rxjs';

import { ModuleService } from '../../../services/module.service';
import { TenantService } from '../../service/tenant.service';
import { MODULE_HRM } from './hrm.nav';

/**
 * A49 — the module gate of the menu, on the route: every listed feature must be on (contract §0: module AND sub-feature).
 * Waits for the tenant when the route is opened before the shell loaded it; off → access denied. Same shape as glFeatureGuard.
 */
export const hrmFeatureGuard = (...codes: string[]): CanActivateFn => (): boolean | UrlTree | Observable<boolean | UrlTree> => {
  const tenant = inject(TenantService);
  const modules = inject(ModuleService, { optional: true });
  const router = inject(Router);
  const on = (code: string) => (modules ? modules.isFeatureEnabled(code) : tenant.hasFeature(code));
  const decide = () => (tenant.isSuperAdmin() || codes.every(on)) ? true : router.createUrlTree(['/portal/access-denied']);
  if (tenant.tenant()) return decide();
  return tenant.loadCurrent().pipe(map(() => decide()), catchError(() => of(router.createUrlTree(['/portal/access-denied']))));
};

/** Any one of these features on (the controller's `[RequiresFeature(a, b)]`, e.g. shifts and holidays: attendance or leave). */
export const hrmAnyFeatureGuard = (...codes: string[]): CanActivateFn => (): boolean | UrlTree | Observable<boolean | UrlTree> => {
  const tenant = inject(TenantService);
  const modules = inject(ModuleService, { optional: true });
  const router = inject(Router);
  const on = (code: string) => (modules ? modules.isFeatureEnabled(code) : tenant.hasFeature(code));
  const decide = () => (tenant.isSuperAdmin() || codes.some(on)) ? true : router.createUrlTree(['/portal/access-denied']);
  if (tenant.tenant()) return decide();
  return tenant.loadCurrent().pipe(map(() => decide()), catchError(() => of(router.createUrlTree(['/portal/access-denied']))));
};

export const hrmModuleGuard = hrmFeatureGuard(MODULE_HRM);
