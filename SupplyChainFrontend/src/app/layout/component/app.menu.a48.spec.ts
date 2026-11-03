import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, Routes, provideRouter } from '@angular/router';
import { AppMenu } from './app.menu';
import { AuthService } from '../../pages/service/auth.service';
import { CurrentTenant } from '../../pages/service/tenant.service';
import pageRoutes from '../../pages/pages.routes';
import { ArDashboardComponent } from '../../pages/finance/ar-ap/ar-dashboard/ar-dashboard.component';
import { CreditControlComponent } from '../../pages/finance/ar-ap/credit-control/credit-control.component';
import { CustomerStatementComponent } from '../../pages/finance/ar-ap/customer-statement/customer-statement.component';
import { DunningProceduresComponent } from '../../pages/finance/ar-ap/dunning-procedures/dunning-procedures.component';
import { DunningRunsComponent } from '../../pages/finance/ar-ap/dunning-runs/dunning-runs.component';
import { ApDashboardComponent } from '../../pages/finance/ar-ap/ap-dashboard/ap-dashboard.component';
import { PaymentSchedulesComponent } from '../../pages/finance/ar-ap/payment-schedules/payment-schedules.component';
import { PaymentRunListComponent } from '../../pages/finance/ar-ap/payment-runs/payment-run-list.component';
import { PaymentRunWizardComponent } from '../../pages/finance/ar-ap/payment-runs/payment-run-wizard.component';
import { MatchingListComponent } from '../../pages/finance/ar-ap/matching/matching-list.component';
import { InvoiceMatchingComponent } from '../../pages/finance/ar-ap/matching/invoice-matching.component';
import { ApSettingsComponent } from '../../pages/finance/ar-ap/ap-settings/ap-settings.component';
import { VendorStatementComponent } from '../../pages/finance/ar-ap/vendor-statement/vendor-statement.component';
import { CreditMemosComponent } from '../../pages/finance/ar-ap/credit-memos/credit-memos.component';
import { DebitMemosComponent } from '../../pages/finance/ar-ap/debit-memos/debit-memos.component';

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
  id: 'org-1', orgCode: 'ORG1', orgName: 'Org One', plan: 'STANDARD', enabledFeatureCodes: features, isSuperAdmin: false, roleName: 'Accountant', permissions: []
});

function find(items: any[], label: string): any {
  for (const i of items ?? []) {
    if (i.label === label) return i;
    const inner = find(i.items, label);
    if (inner) return inner;
  }
  return undefined;
}

/** A48 D-18 / D-14 — Finance › Receivables (AR) and Payables (AP), and their routes (contract §0.2 codes). */
describe('AppMenu + routes — A48 AR & AP sub-ledgers', () => {
  let tenants: FakeTenantService;
  let auth: FakeAuthService;
  let menu: AppMenu;

  beforeEach(() => {
    tenants = new FakeTenantService();
    auth = new FakeAuthService();
    menu = new AppMenu(auth as any, tenants as any);
    // A49 G-8 — the AR and AP entries need a source module; these tests are about the permission gates.
    tenants.tenant.set(tenant(['MODULE_FINANCE', 'MODULE_DEMAND']));
  });

  it('an auditor (AR_VIEW + AP_VIEW) sees every A48 entry with its link', () => {
    auth.permissions = ['AR_VIEW', 'AP_VIEW'];
    const ar = find(menu.model(), 'Receivables (AR)');
    const ap = find(menu.model(), 'Payables (AP)');
    expect(ar.items.map((i: any) => i.routerLink[0])).toEqual(['/portal/pages/finance/ar', '/portal/pages/finance/ar/credit', '/portal/pages/finance/ar/statements',
      '/portal/pages/finance/ar/credit-memos', '/portal/pages/finance/ar/dunning/runs', '/portal/pages/finance/ar/dunning/procedures']);
    expect(ap.items.map((i: any) => i.label)).toEqual(['AP Dashboard', 'Invoice Matching', 'Payment Schedules', 'Payment Runs', 'Vendor Statements', 'Debit Memos']);
    expect(find(menu.model(), 'AR & AP Settings').routerLink).toEqual(['/portal/pages/finance/ap/settings']);
  });

  it('a role with only PAYMENT_RUN_PROPOSE sees schedules and runs; nothing of AR', () => {
    auth.permissions = ['PAYMENT_RUN_PROPOSE'];
    expect(find(menu.model(), 'Receivables (AR)')).toBeUndefined();
    expect(find(menu.model(), 'Payables (AP)').items.map((i: any) => i.label)).toEqual(['Payment Schedules', 'Payment Runs']);
  });

  it('hidden without MODULE_FINANCE', () => {
    auth.permissions = ['AR_VIEW', 'AP_VIEW'];
    tenants.tenant.set(tenant([]));
    expect(find(menu.model(), 'AR Dashboard')).toBeUndefined();
  });

  it('routes: component and the contract codes', () => {
    const routes = pageRoutes as Routes;
    const byPath = (p: string) => routes.find(r => r.path === p)!;
    const fake = { isAuthenticated: () => true, perms: [] as string[], hasAnyPermission(...c: string[]) { return c.some(x => this.perms.includes(x)); } };
    TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: AuthService, useValue: fake }] });
    spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
    const allows = (path: string, perms: string[]) => {
      fake.perms = perms;
      return TestBed.runInInjectionContext(() => (byPath(path).canActivate![0] as any)({}, {}));
    };
    const cases: [string, any, string[], string[]][] = [
      ['finance/ar', ArDashboardComponent, ['AR_VIEW'], ['AP_VIEW', 'CREDIT_MANAGE']],
      ['finance/ar/credit', CreditControlComponent, ['AR_VIEW', 'CREDIT_MANAGE'], ['AP_VIEW', 'CREDIT_OVERRIDE', 'CREDIT_CHECK']],
      ['finance/ar/credit/:uuid', CreditControlComponent, ['CREDIT_MANAGE'], ['DUNNING_EXECUTE', 'CREDIT_CHECK']],
      ['finance/ar/statements', CustomerStatementComponent, ['AR_VIEW', 'AR_STATEMENT_SEND'], ['AP_VIEW']],
      ['finance/ar/dunning/procedures', DunningProceduresComponent, ['AR_VIEW', 'DUNNING_EXECUTE', 'DUNNING_ADMIN', 'FINANCE_AR_AP_ADMIN'], ['AP_VIEW']],
      ['finance/ar/dunning/runs', DunningRunsComponent, ['AR_VIEW', 'DUNNING_EXECUTE', 'DUNNING_ADMIN'], ['FINANCE_AR_AP_ADMIN']],
      ['finance/ap', ApDashboardComponent, ['AP_VIEW'], ['AR_VIEW', 'MATCH_EXECUTE']],
      ['finance/ap/payment-schedules', PaymentSchedulesComponent, ['AP_VIEW', 'AP_EXECUTE', 'PAYMENT_RUN_PROPOSE', 'PAYMENT_RUN_ADMIN'], ['MATCH_EXECUTE']],
      ['finance/ap/payment-runs', PaymentRunListComponent, ['AP_VIEW', 'PAYMENT_RUN_EXECUTE'], ['AP_EXECUTE']],
      ['finance/ap/payment-runs/new', PaymentRunWizardComponent, ['PAYMENT_RUN_PROPOSE'], ['AP_VIEW', 'PAYMENT_RUN_APPROVE', 'FINANCE_AR_AP_ADMIN']],
      ['finance/ap/payment-runs/:uuid', PaymentRunWizardComponent, ['AP_VIEW', 'PAYMENT_RUN_APPROVE'], ['AR_VIEW']],
      ['finance/ap/matching', MatchingListComponent, ['AP_VIEW', 'MATCH_EXECUTE', 'MATCH_OVERRIDE', 'MATCH_APPROVE'], ['AP_EXECUTE']],
      ['finance/ap/matching/:uuid', InvoiceMatchingComponent, ['MATCH_APPROVE'], ['PAYMENT_RUN_PROPOSE']],
      ['finance/ap/settings', ApSettingsComponent, ['AP_VIEW', 'MATCH_EXECUTE', 'FINANCE_AR_AP_ADMIN', 'AR_VIEW', 'CREDIT_MANAGE'], ['DUNNING_EXECUTE', 'PAYMENT_RUN_PROPOSE']],
      ['finance/ap/vendor-statement', VendorStatementComponent, ['AP_VIEW'], ['AR_VIEW', 'AP_EXECUTE']],
      ['finance/ap/vendor-statement/:uuid', VendorStatementComponent, ['AP_VIEW'], ['MATCH_EXECUTE']],
      ['finance/ar/credit-memos', CreditMemosComponent, ['AR_VIEW', 'AR_CREDIT_MEMO'], ['AP_VIEW', 'CREDIT_MANAGE']],
      ['finance/ap/debit-memos', DebitMemosComponent, ['AP_VIEW', 'AP_DEBIT_MEMO'], ['AR_VIEW', 'AP_EXECUTE']]
    ];
    for (const [path, component, yes, no] of cases) {
      expect(byPath(path)?.component).withContext(path).toBe(component);
      for (const p of yes) expect(allows(path, [p])).withContext(`${path} ${p}`).toBeTrue();
      for (const p of no) expect(allows(path, [p])).withContext(`${path} ${p}`).toBeFalse();
    }
    expect(routes.findIndex(r => r.path === 'finance/ap/payment-runs/new')).toBeLessThan(routes.findIndex(r => r.path === 'finance/ap/payment-runs/:uuid'));
  });
});
