import { signal } from '@angular/core';
import { AppMenu } from './app.menu';
import { CurrentTenant } from '../../pages/service/tenant.service';

class FakeTenantService {
  tenant = signal<CurrentTenant | null>(null);
  hasFeature(code: string): boolean { return this.tenant()?.enabledFeatureCodes.includes(code) ?? false; }
  isSuperAdmin(): boolean { return false; }
}

class FakeAuthService {
  permissions: string[] = [];
  hasAnyPermission(...codes: string[]): boolean { return codes.some(c => this.permissions.includes(c)); }
}

const tenant = (features: string[]): CurrentTenant => ({
  id: 'org-1', orgCode: 'ORG1', orgName: 'Org One', plan: 'STANDARD', enabledFeatureCodes: features, isSuperAdmin: false,
  roleName: 'Accountant', permissions: []
});

function find(items: any[], label: string): any {
  for (const i of items ?? []) {
    if (i.label === label) return i;
    const inner = find(i.items, label);
    if (inner) return inner;
  }
  return undefined;
}

/** A44 D-34 / D-80 — Settings › Integrations: QuickBooks Logs (the page's Logs tab) and the posting log. */
describe('AppMenu — A44 entries', () => {
  let tenants: FakeTenantService;
  let auth: FakeAuthService;
  let menu: AppMenu;

  beforeEach(() => {
    tenants = new FakeTenantService();
    auth = new FakeAuthService();
    menu = new AppMenu(auth as any, tenants as any);
  });

  it('"QuickBooks Logs" opens the QuickBooks page on its Logs tab, for INTEGRATION_VIEW with the integration on', () => {
    auth.permissions = ['INTEGRATION_VIEW'];
    tenants.tenant.set(tenant(['MODULE_INTEGRATION']));
    const integrations = find(menu.model(), 'Integrations');
    expect(integrations.items.map((i: any) => i.label)).toEqual(['QuickBooks Integration', 'QuickBooks Logs', 'Posting Log']);
    const logs = find(integrations.items, 'QuickBooks Logs');
    expect(logs.routerLink).toEqual(['/portal/pages/integrations/quickbooks']);
    expect(logs.queryParams).toEqual({ tab: 'logs' });
    expect(find(integrations.items, 'Posting Log').routerLink).toEqual(['/portal/pages/finance/gl/posting']);
  });

  it('hidden without the integration module', () => {
    auth.permissions = ['INTEGRATION_VIEW', 'GL_VIEW'];
    tenants.tenant.set(tenant(['MODULE_FINANCE']));
    expect(find(menu.model(), 'QuickBooks Logs')).toBeUndefined();
    expect(find(menu.model(), 'Posting Log')).toBeUndefined();
  });

  it('a GL tenant also has Posting under Finance › General Ledger', () => {
    auth.permissions = ['GL_VIEW'];
    tenants.tenant.set(tenant(['MODULE_FINANCE', 'FEATURE_GENERAL_LEDGER']));
    expect(find(find(menu.model(), 'General Ledger').items, 'Posting').routerLink).toEqual(['/portal/pages/finance/gl/posting']);
  });
});
