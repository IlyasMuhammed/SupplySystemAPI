import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { CanActivateFn, Route, Router, Routes } from '@angular/router';
import routes from '../pages.routes';
import { AppMenu } from '../../layout/component/app.menu';
import { AuthService } from '../service/auth.service';
import { LeadTimeDefaultsComponent } from './lead-time-defaults.component';

/**
 * A34-PB-08 — Settings → Lead Time Defaults is guarded exactly as API-CONTRACT §2 says
 * (`permissionGuard(P.LEAD_TIME_DEFAULTS_MANAGE, P.INVENTORY_VIEW)`: both codes can GET the defaults on the server;
 * the PUT needs MANAGE), and the menu entry follows Sale Order Settings, shown in MODULE_INVENTORY organizations
 * (api/lead-time/* is [RequiresFeature("MODULE_INVENTORY")]).
 */
describe('A34 lead time defaults — route and menu', () => {
  const find = (path: string) => (routes as Routes).find(r => r.path === path);

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

  it('routes lead-time-defaults to the page, guarded by LEAD_TIME_DEFAULTS_MANAGE or INVENTORY_VIEW', () => {
    expect(find('lead-time-defaults')?.component).toBe(LeadTimeDefaultsComponent);
    expect(guardCodes(find('lead-time-defaults'))).toEqual(['LEAD_TIME_DEFAULTS_MANAGE', 'INVENTORY_VIEW']);
  });

  function settingsItems(permissions: string[], features = ['MODULE_INVENTORY', 'MODULE_LOGISTICS', 'MODULE_DEMAND']): any[] {
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

  it('lists Lead Time Defaults under Settings after Sale Order Settings (and its A33 neighbour Fulfillment Routes)', () => {
    const items = settingsItems(['SALE_ORDER_CONFIG_READ', 'FULFILLMENT_ROUTE_VIEW', 'INVENTORY_VIEW']);
    const labels = items.map(i => i.label);
    expect(labels.indexOf('Lead Time Defaults')).toBe(labels.indexOf('Fulfillment Routes') + 1);
    expect(labels.indexOf('Lead Time Defaults')).toBeGreaterThan(labels.indexOf('Sale Order Settings'));
    expect(items.find(i => i.label === 'Lead Time Defaults').routerLink).toEqual(['/portal/pages/lead-time-defaults']);
  });

  it('follows Sale Order Settings directly when Fulfillment Routes is hidden', () => {
    const labels = settingsItems(['SALE_ORDER_CONFIG_READ', 'INVENTORY_VIEW'], ['MODULE_INVENTORY', 'MODULE_DEMAND']).map(i => i.label);
    expect(labels.indexOf('Lead Time Defaults')).toBe(labels.indexOf('Sale Order Settings') + 1);
  });

  it('shows it to MANAGE or INVENTORY_VIEW holders in an Inventory organization only', () => {
    const has = (perms: string[], features?: string[]) => settingsItems(perms, features).some(i => i.label === 'Lead Time Defaults');
    expect(has(['LEAD_TIME_DEFAULTS_MANAGE'])).toBeTrue();
    expect(has(['INVENTORY_VIEW'])).toBeTrue();
    expect(has(['SALE_ORDER_VIEW', 'STOCK_ADJUST'])).toBeFalse();
    expect(has(['INVENTORY_VIEW'], ['MODULE_DEMAND'])).toBeFalse();
  });
});
