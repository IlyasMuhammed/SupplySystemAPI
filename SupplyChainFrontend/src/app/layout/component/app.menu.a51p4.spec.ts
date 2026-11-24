import { signal } from '@angular/core';
import { AppMenu } from './app.menu';
import { CurrentTenant, FeatureState } from '../../pages/service/tenant.service';

class FakeTenantService {
  tenant = signal<CurrentTenant | null>(null);
  hasFeature(code: string): boolean { return this.tenant()?.enabledFeatureCodes.includes(code) ?? false; }
  isSuperAdmin(): boolean { return this.tenant()?.isSuperAdmin ?? false; }
}

class FakeAuthService {
  permissions: string[] = [];
  hasAnyPermission(...codes: string[]): boolean { return codes.some(c => this.permissions.includes(c)); }
}

const st = (code: string, over: Partial<FeatureState> = {}): FeatureState =>
  ({ code, status: 'Live', isVisible: true, usable: true, includedInPlan: true, ...over });

const tenant = (features: string[], states?: FeatureState[]): CurrentTenant => ({
  id: 'org-1', orgCode: 'ORG1', orgName: 'Org One', plan: 'STANDARD', enabledFeatureCodes: features, featureStates: states,
  isSuperAdmin: false, roleName: 'Organization Admin', permissions: []
});

function find(items: any[], label: string): any {
  for (const i of items ?? []) {
    if (i.label === label) return i;
    const inner = find(i.items, label);
    if (inner) return inner;
  }
  return undefined;
}

/** A51 P4 - hidden -> not rendered; visible + ComingSoon -> greyed with a badge and no link; not in plan -> hidden. */
describe('AppMenu - A51 P4 feature visibility', () => {
  let tenants: FakeTenantService;
  let auth: FakeAuthService;
  let menu: AppMenu;

  beforeEach(() => {
    tenants = new FakeTenantService();
    auth = new FakeAuthService();
    menu = new AppMenu(auth as any, tenants as any);
    auth.permissions = ['POS_STORE_VIEW', 'INTEGRATION_VIEW'];
  });

  it('a usable feature renders as before', () => {
    tenants.tenant.set(tenant(['MODULE_POS'], [st('MODULE_POS')]));
    const pos = find(menu.model(), 'Point of Sale');
    expect(pos.disabled).toBeUndefined();
    expect(find(pos.items, 'Stores').routerLink).toEqual(['/portal/pages/pos/stores']);
  });

  it('visible + ComingSoon + in the plan: a disabled "Coming Soon" entry with no link and no children', () => {
    tenants.tenant.set(tenant([], [st('MODULE_POS', { status: 'ComingSoon', usable: false })]));
    const pos = find(menu.model(), 'Point of Sale');
    expect(pos).toBeTruthy();
    expect(pos.disabled).toBeTrue();
    expect(pos.badge).toBe('Coming Soon');
    expect(pos.comingSoon).toBeTrue();
    expect(pos.routerLink).toBeUndefined();
    expect(pos.items).toBeUndefined();
  });

  it('a ComingSoon group the user has no permission for stays hidden', () => {
    auth.permissions = [];
    tenants.tenant.set(tenant([], [st('MODULE_POS', { status: 'ComingSoon', usable: false })]));
    expect(find(menu.model(), 'Point of Sale')).toBeUndefined();
  });

  it('hidden from the organization (IsVisible false, not grandfathered): not rendered', () => {
    tenants.tenant.set(tenant([], [st('MODULE_POS', { status: 'ComingSoon', usable: false, isVisible: false })]));
    expect(find(menu.model(), 'Point of Sale')).toBeUndefined();
  });

  it('ComingSoon but not in the organization plan: hidden, not greyed', () => {
    tenants.tenant.set(tenant([], [st('MODULE_POS', { status: 'ComingSoon', usable: false, includedInPlan: false })]));
    expect(find(menu.model(), 'Point of Sale')).toBeUndefined();
  });

  it('QuickBooks under Settings > Integrations is hidden when the plan lacks the feature, once, never twice', () => {
    tenants.tenant.set(tenant(['MODULE_INTEGRATION'], [st('MODULE_INTEGRATION')]));
    const integrations = find(menu.model(), 'Integrations');
    expect(integrations.items.filter((i: any) => i.label === 'QuickBooks Integration').length).toBe(1);

    tenants.tenant.set(tenant([], [st('MODULE_INTEGRATION', { usable: false, includedInPlan: false })]));
    expect(find(menu.model(), 'QuickBooks Integration')).toBeUndefined();
  });

  it('an API without featureStates behaves exactly as before', () => {
    tenants.tenant.set(tenant(['MODULE_POS']));
    expect(find(menu.model(), 'Point of Sale').disabled).toBeUndefined();
    tenants.tenant.set(tenant([]));
    expect(find(menu.model(), 'Point of Sale')).toBeUndefined();
  });
});
