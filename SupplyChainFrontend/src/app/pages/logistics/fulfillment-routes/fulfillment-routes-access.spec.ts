import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { CanActivateFn, Route, Router, Routes } from '@angular/router';
import routes from '../../pages.routes';
import { AppMenu } from '../../../layout/component/app.menu';
import { AuthService } from '../../service/auth.service';
import { FulfillmentRoutesComponent } from './fulfillment-routes.component';

/**
 * A33-PA-07 — Settings → Fulfillment Routes is routed and guarded exactly as the contract says (§2: the page opens with
 * FULFILLMENT_ROUTE_VIEW or FULFILLMENT_ROUTE_MANAGE), and the menu entry sits next to Sale Order Settings, shown only
 * to those codes in organizations with MODULE_LOGISTICS (api/fulfillment-routes is [RequiresFeature("MODULE_LOGISTICS")]).
 */
describe('A33 fulfillment routes — route and menu', () => {
  const flat = routes as Routes;
  const find = (path: string) => flat.find(r => r.path === path);

  function guardCodes(route: Route | undefined): string[] {
    const asked: string[] = [];
    TestBed.resetTestingModule().configureTestingModule({
      providers: [
        { provide: AuthService, useValue: { isAuthenticated: () => true, hasAnyPermission: (...codes: string[]) => { asked.push(...codes); return true; } } },
        { provide: Router, useValue: { navigate: () => Promise.resolve(true) } }
      ]
    });
    for (const guard of (route?.canActivate ?? []) as CanActivateFn[]) {
      TestBed.runInInjectionContext(() => guard({} as any, {} as any));
    }
    return asked;
  }

  it('routes logistics/fulfillment-routes to the list, guarded by VIEW or MANAGE', () => {
    expect(find('logistics/fulfillment-routes')?.component).toBe(FulfillmentRoutesComponent);
    expect(guardCodes(find('logistics/fulfillment-routes'))).toEqual(['FULFILLMENT_ROUTE_VIEW', 'FULFILLMENT_ROUTE_MANAGE']);
  });

  function settingsItems(permissions: string[], features = ['MODULE_LOGISTICS', 'MODULE_DEMAND']): any[] {
    const tenant = signal({ id: 'o', orgCode: 'O', orgName: 'O', plan: 'ENTERPRISE', enabledFeatureCodes: features, isSuperAdmin: false, roleName: 'X', permissions });
    const menu = new AppMenu(
      { hasAnyPermission: (...codes: string[]) => codes.some(c => permissions.includes(c)) } as any,
      { tenant, hasFeature: (c: string) => features.includes(c), isSuperAdmin: () => false } as any
    );
    // Settings is split into sub-groups; the sales and fulfilment settings sit together in one of them.
    let found: any[] = [];
    const walk = (items: any[]) => items.forEach(i => { if (i.label === 'Sales & Fulfilment' && i.items) found = i.items; else if (i.items) walk(i.items); });
    walk(menu.model());
    return found;
  }

  it('shows Fulfillment Routes under Settings, right after Sale Order Settings, linking to the route', () => {
    const items = settingsItems(['FULFILLMENT_ROUTE_VIEW', 'SALE_ORDER_CONFIG_READ']);
    const labels = items.map(i => i.label);
    expect(labels.indexOf('Fulfillment Routes')).toBe(labels.indexOf('Sale Order Settings') + 1);
    expect(items.find(i => i.label === 'Fulfillment Routes').routerLink).toEqual(['/portal/pages/logistics/fulfillment-routes']);
  });

  it('shows it to VIEW or MANAGE holders in a Logistics org only', () => {
    const has = (perms: string[], features?: string[]) => settingsItems(perms, features).some(i => i.label === 'Fulfillment Routes');
    expect(has(['FULFILLMENT_ROUTE_VIEW'])).toBeTrue();
    expect(has(['FULFILLMENT_ROUTE_MANAGE'])).toBeTrue();
    expect(has(['FULFILLMENT_ROUTE_ASSIGN'])).toBeFalse();
    expect(has(['SALE_ORDER_VIEW', 'INVENTORY_VIEW'])).toBeFalse();
    expect(has(['FULFILLMENT_ROUTE_VIEW'], ['MODULE_DEMAND'])).toBeFalse();
  });
});
