import { signal } from '@angular/core';
import { AppMenu } from './app.menu';
import { CurrentTenant } from '../../pages/service/tenant.service';
import pagesRoutes from '../../pages/pages.routes';

class FakeTenantService {
  tenant = signal<CurrentTenant | null>(null);
  hasFeature(code: string): boolean { return this.tenant()?.enabledFeatureCodes.includes(code) ?? false; }
  isSuperAdmin(): boolean { return this.tenant()?.isSuperAdmin ?? false; }
}

class FakeAuthService {
  permissions: string[] = [];
  hasAnyPermission(...codes: string[]): boolean { return codes.some(c => this.permissions.includes(c)); }
}

const tenant = (features: string[], isSuperAdmin = false): CurrentTenant => ({
  id: 'org-1', orgCode: 'ORG1', orgName: 'Org One', plan: 'STANDARD', enabledFeatureCodes: features, isSuperAdmin,
  roleName: 'Organization Admin', permissions: []
});

function find(items: any[], label: string): any {
  for (const i of items ?? []) {
    if (i.label === label) return i;
    const inner = find(i.items, label);
    if (inner) return inner;
  }
  return undefined;
}

/** A51 P1 - System Administration > Countries and Industries (super admin only). */
describe('AppMenu - A51 Countries and Industries', () => {
  let tenants: FakeTenantService;
  let auth: FakeAuthService;
  let menu: AppMenu;

  beforeEach(() => {
    tenants = new FakeTenantService();
    auth = new FakeAuthService();
    menu = new AppMenu(auth as any, tenants as any);
  });

  it('super admin: System Administration lists Countries and Industries with their routes', () => {
    tenants.tenant.set(tenant([], true));
    const sys = find(menu.model(), 'System Administration');
    expect(sys.items.map((i: any) => i.label)).toEqual(['Organizations', 'Countries', 'Industries', 'Features', 'Plans', 'QuickBooks App', 'Setup Defaults']);
    expect(find(sys.items, 'Countries').routerLink).toEqual(['/portal/pages/system/countries']);
    expect(find(sys.items, 'Industries').routerLink).toEqual(['/portal/pages/system/industries']);
  });

  it('A51 P2: Features and Plans sit right after Industries with their routes', () => {
    tenants.tenant.set(tenant([], true));
    const sys = find(menu.model(), 'System Administration');
    expect(find(sys.items, 'Features').routerLink).toEqual(['/portal/pages/system/features']);
    expect(find(sys.items, 'Plans').routerLink).toEqual(['/portal/pages/system/plans']);
  });

  it('A51 P2: org users never see Features or Plans, whatever their permissions', () => {
    auth.permissions = ['SYSTEM_CONFIGURE', 'PLATFORM_SUPER_ADMIN'];
    tenants.tenant.set(tenant(['MODULE_MASTER_DATA']));
    const links: string[] = [];
    const walk = (items: any[]) => { for (const i of items ?? []) { links.push(...(i.routerLink ?? [])); walk(i.items); } };
    walk(menu.model());
    expect(links).not.toContain('/portal/pages/system/features');
    expect(links).not.toContain('/portal/pages/system/plans');
  });

  it('org users never see Countries or Industries, whatever their permissions', () => {
    auth.permissions = ['SYSTEM_CONFIGURE', 'PLATFORM_SUPER_ADMIN', 'LOCATION_MANAGE'];
    tenants.tenant.set(tenant(['MODULE_MASTER_DATA']));
    // (an organization menu already has its own lookup "Countries" item - the master is the system/ one)
    const links: string[] = [];
    const walk = (items: any[]) => { for (const i of items ?? []) { links.push(...(i.routerLink ?? [])); walk(i.items); } };
    walk(menu.model());
    expect(links).not.toContain('/portal/pages/system/countries');
    expect(links).not.toContain('/portal/pages/system/industries');
    expect(find(menu.model(), 'System Administration')).toBeUndefined();
  });

  it('routes: both pages are guarded lazy routes', () => {
    for (const p of ['system/countries', 'system/industries', 'system/features', 'system/plans']) {
      const r = (pagesRoutes as any[]).find(x => x.path === p);
      expect(r).toBeDefined();
      expect(r.canActivate?.length).toBe(1);
      expect(typeof r.loadComponent).toBe('function');
    }
  });
});
