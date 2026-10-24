import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, Routes, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AppMenu } from './app.menu';
import { AuthService } from '../../pages/service/auth.service';
import { CurrentTenant, TenantService } from '../../pages/service/tenant.service';
import { ModuleService } from '../../services/module.service';
import pageRoutes from '../../pages/pages.routes';
import { glFeatureGuard } from '../../pages/finance/gl/gl.guards';
import { GlJournalEntriesComponent } from '../../pages/finance/gl/journal-entries/gl-journal-entries.component';
import { GlJournalEntryFormComponent } from '../../pages/finance/gl/journal-entries/gl-journal-entry-form.component';
import { GlJournalEntryDetailComponent } from '../../pages/finance/gl/journal-entries/gl-journal-entry-detail.component';
import { GlJournalTypesComponent } from '../../pages/finance/gl/journal-types/gl-journal-types.component';
import { GlReconciliationComponent } from '../../pages/finance/gl/reconciliation/gl-reconciliation.component';
import { GlTrialBalanceComponent } from '../../pages/finance/gl/trial-balance/gl-trial-balance.component';
import { GlPeriodCloseComponent } from '../../pages/finance/gl/period-close/gl-period-close.component';

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

const GL = ['MODULE_FINANCE', 'FEATURE_GENERAL_LEDGER'];

/** A47 D-20 / D-15 — the journal-engine entries in Finance › General Ledger and their routes. */
describe('AppMenu + routes — A47 journal engine', () => {
  let tenants: FakeTenantService;
  let auth: FakeAuthService;
  let menu: AppMenu;

  beforeEach(() => {
    tenants = new FakeTenantService();
    auth = new FakeAuthService();
    menu = new AppMenu(auth as any, tenants as any);
    tenants.tenant.set(tenant(GL));
  });

  it('an accountant with the view codes sees the five A47 entries with their links', () => {
    auth.permissions = ['GL_VIEW', 'GL_RECONCILE_VIEW', 'GL_REPORTS_VIEW', 'GL_PERIOD_CLOSE_VIEW'];
    const gl = find(menu.model(), 'General Ledger');
    const links: Record<string, string> = {
      'Journal Entries': 'journal-entries', 'Journal Types': 'journal-types', Reconciliation: 'reconciliation',
      'Trial Balance': 'trial-balance', 'Period Close': 'period-close'
    };
    for (const [label, path] of Object.entries(links)) {
      expect(find(gl.items, label)?.routerLink).withContext(label).toEqual(['/portal/pages/finance/gl/' + path]);
    }
  });

  it('a role holding only GL_REPORTS_VIEW sees the group with just the trial balance', () => {
    auth.permissions = ['GL_REPORTS_VIEW'];
    const gl = find(menu.model(), 'General Ledger');
    expect(gl.items.map((i: any) => i.label)).toEqual(['Trial Balance']);
  });

  it('GL_JOURNAL_CREATE alone opens journal entries and types, nothing of A42', () => {
    auth.permissions = ['GL_JOURNAL_CREATE'];
    const gl = find(menu.model(), 'General Ledger');
    expect(gl.items.map((i: any) => i.label)).toEqual(['Journal Entries', 'Journal Types']);
  });

  it('hidden without FEATURE_GENERAL_LEDGER', () => {
    auth.permissions = ['GL_VIEW', 'GL_REPORTS_VIEW'];
    tenants.tenant.set(tenant(['MODULE_FINANCE']));
    expect(find(menu.model(), 'Trial Balance')).toBeUndefined();
  });

  it('routes: component, feature guard and the contract codes', () => {
    const routes = pageRoutes as Routes;
    const byPath = (p: string) => routes.find(r => r.path === p)!;
    const fake = { isAuthenticated: () => true, perms: [] as string[],
      hasAnyPermission(...c: string[]) { return c.some(x => this.perms.includes(x)); } };
    TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: AuthService, useValue: fake },
      { provide: TenantService, useValue: { tenant: () => null, loadCurrent: () => of(null) } },
      { provide: ModuleService, useValue: { isFeatureEnabled: () => true } }] });
    spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    const allows = (path: string, perms: string[]) => {
      fake.perms = perms;
      return TestBed.runInInjectionContext(() => (byPath(path).canActivate![0] as any)({}, {}));
    };
    const cases: [string, any, string[], string[]][] = [
      ['finance/gl/journal-entries', GlJournalEntriesComponent, ['GL_VIEW', 'GL_JOURNAL_REVERSE', 'GL_SETUP_MANAGE'], ['GL_REPORTS_VIEW']],
      ['finance/gl/journal-entries/new', GlJournalEntryFormComponent, ['GL_JOURNAL_CREATE'], ['GL_VIEW', 'GL_JOURNAL_EDIT']],
      ['finance/gl/journal-entries/:uuid', GlJournalEntryDetailComponent, ['GL_VIEW', 'GL_JOURNAL_POST'], ['GL_RECONCILE_VIEW']],
      ['finance/gl/journal-entries/:uuid/edit', GlJournalEntryFormComponent, ['GL_JOURNAL_EDIT'], ['GL_JOURNAL_CREATE', 'GL_VIEW']],
      ['finance/gl/journal-types', GlJournalTypesComponent, ['GL_VIEW', 'GL_JOURNAL_CREATE'], ['GL_REPORTS_VIEW']],
      ['finance/gl/reconciliation', GlReconciliationComponent, ['GL_RECONCILE_VIEW', 'GL_RECONCILE_EXECUTE'], ['GL_VIEW']],
      ['finance/gl/trial-balance', GlTrialBalanceComponent, ['GL_REPORTS_VIEW'], ['GL_VIEW']],
      ['finance/gl/period-close', GlPeriodCloseComponent, ['GL_PERIOD_CLOSE_VIEW', 'GL_PERIOD_CLOSE_EXECUTE', 'GL_PERIOD_CLOSE_ADMIN'], ['GL_VIEW', 'GL_SETUP_MANAGE']]
    ];
    for (const [path, component, yes, no] of cases) {
      expect(byPath(path)?.component).withContext(path).toBe(component);
      expect(byPath(path).canActivate).withContext(path).toContain(glFeatureGuard);
      for (const p of yes) expect(allows(path, [p])).withContext(`${path} ${p}`).toBeTrue();
      for (const p of no) expect(allows(path, [p])).withContext(`${path} ${p}`).toBeFalse();
    }
    // "new" is matched before ":uuid".
    expect(routes.findIndex(r => r.path === 'finance/gl/journal-entries/new'))
      .toBeLessThan(routes.findIndex(r => r.path === 'finance/gl/journal-entries/:uuid'));
  });
});
