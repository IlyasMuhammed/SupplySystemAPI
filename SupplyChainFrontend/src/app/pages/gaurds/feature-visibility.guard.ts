import { inject } from '@angular/core';
import { CanActivateChildFn, Router, UrlTree } from '@angular/router';
import { Observable, catchError, map, of } from 'rxjs';

import { AppMenu, NavItem } from '../../layout/component/app.menu';
import { FeatureState, TenantService } from '../service/tenant.service';

/**
 * A51 P4 (spec Feature 8) - the route side of the feature visibility guard, applied once on the /portal shell
 * (canActivateChild) instead of on hundreds of routes: the menu is already the route -> feature map (an item's routerLink
 * and its featureCode list), so the guard reads that same map. Typing the URL of a feature that is hidden from the
 * organization, ComingSoon, or in neither its plan nor its licences sends the user to the dashboard.
 *
 * Deliberately NOT blocked: a feature the organization is licensed for but that is switched off or in its grace period (the
 * module screens and the API's MODULE_NOT_LICENSED handling own that), and every URL the menu does not name (existing
 * per-route guards keep those). The API refuses the same features with a 403 whatever the UI does. Super admin and an API that
 * sends no featureStates are never blocked here.
 */
export const DASHBOARD_URL = '/portal/dashboard';

type RouteFeatures = { link: string; features: string[] };

let routeFeatures: RouteFeatures[] | null = null;

function linkOf(item: NavItem): string | null {
  const link = Array.isArray(item.routerLink) ? item.routerLink[0] : item.routerLink;
  return typeof link === 'string' && link ? link : null;
}

/** Every menu leaf that names a route, with the features it is gated by (empty = ungated). Built once. */
export function menuRouteFeatures(): RouteFeatures[] {
  if (routeFeatures) return routeFeatures;
  const result: RouteFeatures[] = [];
  const walk = (items: NavItem[], inherited: string[]) => {
    for (const item of items) {
      const own = item.featureCode === undefined ? [] : ([] as string[]).concat(item.featureCode);
      const features = [...inherited, ...own];
      const link = linkOf(item);
      if (link && !item.superAdminOnly && !item.items?.length) result.push({ link, features });
      if (item.items?.length) walk(item.items as NavItem[], features);
    }
  };
  walk(AppMenu.FULL_MENU.filter(i => !i.superAdminOnly), []);
  return (routeFeatures = result);
}

/** The feature sets of the menu entries that own this path (the longest matching link(s)); empty when the menu names none. */
export function featuresForPath(path: string): string[][] {
  let best = 0;
  let found: string[][] = [];
  for (const entry of menuRouteFeatures()) {
    if (path !== entry.link && !path.startsWith(entry.link + '/')) continue;
    if (entry.link.length > best) { best = entry.link.length; found = []; }
    if (entry.link.length === best) found.push(entry.features);
  }
  return found;
}

function blocked(state: FeatureState | undefined): boolean {
  if (!state) return false;
  if (!state.isVisible) return true;
  if (state.usable) return false;
  return state.status === 'ComingSoon' || !state.includedInPlan;
}

/** True when the organization may not open this URL because of feature visibility. */
export function isPathBlocked(path: string, tenant: TenantService): boolean {
  if (tenant.isSuperAdmin() || !tenant.tenant()?.featureStates?.length) return false;
  const owners = featuresForPath(path);
  if (owners.length === 0) return false;
  // Reachable through any entry whose features are all open => allowed (two entries may share a link).
  return owners.every(features => features.some(code => blocked(tenant.featureState(code))));
}

export const featureVisibilityGuard: CanActivateChildFn = (_route, routerState): boolean | UrlTree | Observable<boolean | UrlTree> => {
  const tenant = inject(TenantService);
  const router = inject(Router);
  const path = routerState.url.split(/[?#]/)[0];
  const decide = () => (isPathBlocked(path, tenant) ? router.parseUrl(DASHBOARD_URL) : true);
  if (path === DASHBOARD_URL || tenant.tenant()) return decide();
  // Opened before the shell loaded the tenant (a pasted URL): wait for it; if it cannot load, the API still enforces.
  return tenant.loadCurrent().pipe(map(() => decide()), catchError(() => of(true)));
};
