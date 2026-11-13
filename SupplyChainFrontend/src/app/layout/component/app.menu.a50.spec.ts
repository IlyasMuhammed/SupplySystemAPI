import { signal } from '@angular/core';
import { AppMenu } from './app.menu';
import { CurrentTenant } from '../../pages/service/tenant.service';
import { organizationSettingsGroup } from '../../pages/onboarding/onboarding.nav';
import onboardingRoutes from '../../pages/onboarding/onboarding.routes';
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

/**
 * A50 D-29 — the one shared Settings › Organization group (FE-A50 entries 1–2, FE-B50 entries 3–6), first under
 * Administration › Settings, and the super admin's System Administration › Setup Defaults.
 */
describe('AppMenu — A50 Settings › Organization', () => {
  let tenants: FakeTenantService;
  let auth: FakeAuthService;
  let menu: AppMenu;

  beforeEach(() => {
    tenants = new FakeTenantService();
    auth = new FakeAuthService();
    menu = new AppMenu(auth as any, tenants as any);
  });

  it('the shared definition lists the six entries in the agreed order, with their routes and codes', () => {
    const g = organizationSettingsGroup();
    expect(g.label).toBe('Organization');
    expect(g.items!.map(i => i.label)).toEqual(['Setup Wizard', 'Default Data', 'Data Import', 'Document Templates', 'Email Templates', 'Sample Data']);
    const rows = g.items!.map((i: any) => [i.routerLink[0], i.permRequired]);
    expect(rows).toEqual([
      ['/portal/pages/onboarding/setup', ['SETUP_WIZARD_RUN']],
      ['/portal/pages/onboarding/default-data', ['SETUP_WIZARD_RUN']],
      ['/portal/pages/settings/import', ['IMPORT_PROFILE_VIEW']],
      ['/portal/pages/onboarding/document-templates', ['DOC_TEMPLATE_MANAGE']],
      ['/portal/pages/onboarding/email-templates', ['EMAIL_TEMPLATE_MANAGE']],
      ['/portal/pages/onboarding/sample-data', ['SAMPLE_DATA_MANAGE']]
    ]);
  });

  it('is the first group under Administration › Settings for an org admin holding the codes', () => {
    auth.permissions = ['SETUP_WIZARD_RUN', 'DOC_TEMPLATE_MANAGE', 'EMAIL_TEMPLATE_MANAGE', 'SAMPLE_DATA_MANAGE', 'IMPORT_PROFILE_VIEW'];
    tenants.tenant.set(tenant(['MODULE_DATA_IMPORT']));
    const settings = find(find(menu.model(), 'Administration').items, 'Settings');
    expect(settings.items[0].label).toBe('Organization');
    expect(settings.items[0].items.map((i: any) => i.label))
      .toEqual(['Setup Wizard', 'Default Data', 'Data Import', 'Document Templates', 'Email Templates', 'Sample Data']);
  });

  it('shows only what the user may open; the group hides when nothing is left', () => {
    auth.permissions = ['SETUP_WIZARD_RUN'];
    tenants.tenant.set(tenant([]));
    const org = find(menu.model(), 'Organization');
    expect(org.items.map((i: any) => i.label)).toEqual(['Setup Wizard', 'Default Data']);

    auth.permissions = ['USER_MANAGE'];
    tenants.tenant.set(tenant([]));
    expect(find(menu.model(), 'Organization')).toBeUndefined();
    expect(find(menu.model(), 'Setup Wizard')).toBeUndefined();
  });

  it('Data Import there needs the import module too', () => {
    auth.permissions = ['IMPORT_PROFILE_VIEW'];
    tenants.tenant.set(tenant([]));
    expect(find(menu.model(), 'Organization')).toBeUndefined();
  });

  it('super admin: System Administration › Setup Defaults, and no organization menus', () => {
    tenants.tenant.set(tenant([], true));
    const sys = find(menu.model(), 'System Administration');
    const entry = find(sys.items, 'Setup Defaults');
    expect(entry.routerLink).toEqual(['/portal/pages/system/setup-defaults']);
    expect(find(menu.model(), 'Organization')).toBeUndefined();
  });

  it('org users never see Setup Defaults', () => {
    auth.permissions = ['SETUP_WIZARD_RUN', 'SYSTEM_CONFIGURE'];
    tenants.tenant.set(tenant(['MODULE_MASTER_DATA']));
    expect(find(menu.model(), 'Setup Defaults')).toBeUndefined();
  });

  it('routes: setup + default-data are guarded lazy routes; setup-defaults sit under system/ (platform scope)', () => {
    const paths = (onboardingRoutes as any[]).map(r => r.path);
    expect(paths).toContain('setup');
    expect(paths).toContain('default-data');
    for (const p of ['setup', 'default-data']) {
      const r = (onboardingRoutes as any[]).find(x => x.path === p);
      expect(r.canActivate?.length).toBe(1);
      expect(typeof r.loadComponent).toBe('function');
    }
    const top = (pagesRoutes as any[]).map(r => r.path);
    expect(top).toContain('onboarding');
    expect(top).toContain('system/setup-defaults');
    expect(top).toContain('system/setup-defaults/:uuid');
  });
});
