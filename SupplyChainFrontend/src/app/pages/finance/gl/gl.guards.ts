import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { CanActivateFn, Router, UrlTree } from '@angular/router';
import { Observable, catchError, map, of, throwError } from 'rxjs';

import {
  AccountingService, FEATURE_GENERAL_LEDGER, FINANCE_SETUP_REQUIRED, GL_SETUP_ROUTE
} from '../../../services/accounting.service';
import { ModuleService } from '../../../services/module.service';
import { TenantService } from '../../service/tenant.service';

/**
 * A42 — the GL pages need FEATURE_GENERAL_LEDGER (sub-feature of MODULE_FINANCE). Waits for the tenant when the route
 * is opened before the shell loaded it; off → access denied.
 */
export const glFeatureGuard: CanActivateFn = (): boolean | UrlTree | Observable<boolean | UrlTree> => {
  const tenant = inject(TenantService);
  const modules = inject(ModuleService, { optional: true });
  const router = inject(Router);
  const decide = () => (tenant.isSuperAdmin() || (modules ? modules.isFeatureEnabled(FEATURE_GENERAL_LEDGER) : tenant.hasFeature(FEATURE_GENERAL_LEDGER)))
    ? true : router.createUrlTree(['/portal/access-denied']);
  if (tenant.tenant()) return decide();
  return tenant.loadCurrent().pipe(map(() => decide()), catchError(() => of(router.createUrlTree(['/portal/access-denied']))));
};

/**
 * A44 (D-10, D-80) — the Posting page serves both targets: it opens with FEATURE_GENERAL_LEDGER **or** MODULE_INTEGRATION
 * (an organization that only sends to QuickBooks still has a posting log). Neither → access denied.
 */
export const POSTING_FEATURES = [FEATURE_GENERAL_LEDGER, 'MODULE_INTEGRATION'];

export const postingFeatureGuard: CanActivateFn = (): boolean | UrlTree | Observable<boolean | UrlTree> => {
  const tenant = inject(TenantService);
  const modules = inject(ModuleService, { optional: true });
  const router = inject(Router);
  const on = (code: string) => modules ? modules.isFeatureEnabled(code) : tenant.hasFeature(code);
  const decide = () => (tenant.isSuperAdmin() || POSTING_FEATURES.some(on)) ? true : router.createUrlTree(['/portal/access-denied']);
  if (tenant.tenant()) return decide();
  return tenant.loadCurrent().pipe(map(() => decide()), catchError(() => of(router.createUrlTree(['/portal/access-denied']))));
};

/**
 * D-4 — the GL dashboard opens only once setup is Complete; otherwise the wizard, with a banner naming the next step
 * (`?gate=1&next=<step>`). A failed status read lets the page open (it shows its own error / the API's 403 redirect).
 */
export const glSetupCompleteGuard: CanActivateFn = (): Observable<boolean | UrlTree> => {
  const api = inject(AccountingService);
  const router = inject(Router);
  return api.getSetupStatus().pipe(
    map(r => {
      const s = r?.result;
      if (!s || s.status === 'Complete') return true;
      return router.createUrlTree([GL_SETUP_ROUTE], { queryParams: { gate: 1, next: s.nextStep } });
    }),
    catchError(() => of(true)));
};

/**
 * D-4 — a 403 `{ error: "FINANCE_SETUP_REQUIRED", redirectUrl }` from a post-setup GL endpoint sends the user to the
 * wizard (same banner as the guard). The error is passed on so the page can stop its spinner.
 */
export const glSetupRequiredInterceptor: HttpInterceptorFn = (req, next) => {
  const router = inject(Router, { optional: true });
  return next(req).pipe(
    catchError((err: unknown) => {
      if (err instanceof HttpErrorResponse && err.status === 403 && isSetupRequired(err.error)) {
        const target = typeof err.error?.redirectUrl === 'string' && err.error.redirectUrl.startsWith('/') ? err.error.redirectUrl : GL_SETUP_ROUTE;
        router?.navigate([target], { queryParams: { gate: 1 } });
      }
      return throwError(() => err);
    })
  );
};

function isSetupRequired(body: any): boolean {
  return !!body && (body.error === FINANCE_SETUP_REQUIRED || body.errorCode === FINANCE_SETUP_REQUIRED);
}
