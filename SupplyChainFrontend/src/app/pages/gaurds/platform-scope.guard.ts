import { inject } from '@angular/core';
import { CanActivateChildFn, Router, RouterStateSnapshot } from '@angular/router';
import { catchError, map, of } from 'rxjs';
import { TenantService } from '../service/tenant.service';

/** What the platform super admin may open: tenant management, platform settings, and their own profile. */
const PLATFORM_PATHS = [
  '/portal/pages/organizations',
  '/portal/pages/system',
  '/portal/pages/profile',
  '/portal/access-denied'
];

export const PLATFORM_HOME = '/portal/pages/organizations';

export function isPlatformPath(url: string): boolean {
  const path = url.split(/[?#]/)[0];
  return PLATFORM_PATHS.some(p => path === p || path.startsWith(p + '/'));
}

/**
 * The platform super admin manages tenants only. Everything else under /portal (sales, stock, finance, HR, an
 * organization's users and settings...) is the organizations' own business, so a super admin who opens one of those
 * addresses is sent to the organizations list. Everyone else passes untouched.
 */
export const platformScopeGuard: CanActivateChildFn = (_route, state: RouterStateSnapshot) => {
  const tenant = inject(TenantService);
  const router = inject(Router);

  const decide = () => !tenant.isSuperAdmin() || isPlatformPath(state.url) ? true : router.createUrlTree([PLATFORM_HOME]);

  // The tenant profile arrives after the shell loads; wait for it so the first navigation is judged on who is signed in.
  if (tenant.tenant()) return decide();
  return tenant.loadCurrent().pipe(map(() => decide()), catchError(() => of(true)));
};
