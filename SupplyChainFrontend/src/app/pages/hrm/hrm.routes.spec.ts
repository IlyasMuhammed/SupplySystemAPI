import { signal } from '@angular/core';

import routes from './hrm.routes';
import pageRoutes from '../pages.routes';
import { HRM_GROUPS, HRM_PAGES, HRM_ROUTE, hrmMenuGroup } from './core/hrm.nav';
import { AppMenu } from '../../layout/component/app.menu';

class FakeTenant {
  tenant = signal<any>(null);
  hasFeature(code: string): boolean { return this.tenant()?.enabledFeatureCodes.includes(code) ?? false; }
  isSuperAdmin(): boolean { return false; }
}
class FakeAuth {
  permissions: string[] = [];
  hasAnyPermission(...codes: string[]): boolean { return codes.some(c => this.permissions.includes(c)); }
}

const tenant = (features: string[]) => ({ id: 'o', orgCode: 'O', orgName: 'O', plan: 'X', enabledFeatureCodes: features, isSuperAdmin: false, roleName: 'R', permissions: [] });
function find(items: any[], label: string): any {
  for (const i of items ?? []) { if (i.label === label) return i; const inner = find(i.items, label); if (inner) return inner; }
  return undefined;
}
const allLabels = (items: any[]): string[] => (items ?? []).flatMap(i => [i.label, ...allLabels(i.items)]);

describe('A49 routes and menu', () => {
  it('every page has a lazy, guarded route: a menu entry cannot point nowhere', () => {
    for (const p of HRM_PAGES) {
      const r = routes.find(x => x.path === p.path);
      expect(r).withContext(p.path).toBeDefined();
      expect(r!.loadComponent).withContext(`${p.path} component`).toBeDefined();
      expect(r!.canActivate?.length).withContext(`${p.path} guards`).toBe(p.feature ? 2 : 1);
    }
  });

  it('no eager component: the HRM chunk is behind loadChildren and the module gate; setup sits outside it', () => {
    const hrm = (pageRoutes as any[]).flatMap(r => r.children ?? [r]).find(r => r.path === 'hrm');
    expect(hrm.loadChildren).toBeDefined();
    expect(hrm.canActivate.length).toBe(1);
    const setup = (pageRoutes as any[]).flatMap(r => r.children ?? [r]).find(r => r.path === 'hrm/setup');
    expect(setup.loadComponent).toBeDefined();
    expect(routes.every(r => !r.component)).toBeTrue();
  });

  it('each menu entry carries its page\'s codes and sub-feature', () => {
    const menu = hrmMenuGroup();
    expect(menu.featureCode).toBe('MODULE_HRM');
    const flat = menu.items.flatMap((g: any) => g.items);
    for (const p of HRM_PAGES) {
      const item = flat.find((i: any) => i.routerLink[0] === `${HRM_ROUTE}/${p.path}`);
      expect(item).withContext(p.path).toBeDefined();
      expect(item.permRequired).toEqual(p.perms);
      expect(item.featureCode).toBe(p.feature);
    }
  });

  it('sensitive pages open only for their sensitive codes, never for a general HR read', () => {
    const perms = (path: string) => HRM_PAGES.find(p => p.path === path)!.perms;
    expect(perms('payroll/runs')).toEqual(['HRM_PAYROLL_VIEW']);
    expect(perms('lifecycle/loans')).toEqual(['HRM_LOAN_VIEW']);
    expect(perms('lifecycle/tax-certificates')).toEqual(['HRM_TAX_CERT_VIEW']);
  });

  it('every group holds pages; no page path appears twice', () => {
    expect(HRM_GROUPS.every(g => g.pages.length > 0)).toBeTrue();
    expect(new Set(HRM_PAGES.map(p => p.path)).size).toBe(HRM_PAGES.length);
  });

  describe('AppMenu', () => {
    let t: FakeTenant;
    let a: FakeAuth;
    let menu: AppMenu;
    beforeEach(() => { t = new FakeTenant(); a = new FakeAuth(); menu = new AppMenu(a as any, t as any); });

    it('MODULE_HRM off: no group, whatever the permissions', () => {
      a.permissions = ['HRM_EMPLOYEE_VIEW', 'HRM_PAYROLL_VIEW'];
      t.tenant.set(tenant(['MODULE_FINANCE']));
      expect(find(menu.model(), 'HR & Payroll')).toBeUndefined();
    });

    it('on: shows what the role holds, and a sub-feature off hides its group', () => {
      a.permissions = ['HRM_EMPLOYEE_VIEW', 'HRM_PAYROLL_VIEW', 'HRM_SELF_SERVICE'];
      t.tenant.set(tenant(['MODULE_HRM', 'FEATURE_HRM_LEAVE']));
      const hrm = find(menu.model(), 'HR & Payroll');
      expect(find(hrm.items, 'Employees')).toBeDefined();
      expect(find(hrm.items, 'Branches')).toBeUndefined();
      expect(find(hrm.items, 'Payroll Runs')).withContext('payroll sub-feature is off').toBeUndefined();
      expect(find(hrm.items, 'My Leave')).toBeDefined();
      expect(find(hrm.items, 'My Payslips')).toBeUndefined();
      t.tenant.set(tenant(['MODULE_HRM', 'FEATURE_HRM_PAYROLL']));
      expect(find(find(menu.model(), 'HR & Payroll').items, 'Payroll Runs')).toBeDefined();
    });

    it('no HR permission at all: no empty group', () => {
      t.tenant.set(tenant(['MODULE_HRM', 'FEATURE_HRM_PAYROLL']));
      expect(find(menu.model(), 'HR & Payroll')).toBeUndefined();
    });

    it('cost center mapping needs QuickBooks and HRM both (331)', () => {
      a.permissions = ['HRM_ACCOUNT_MAPPING_VIEW'];
      t.tenant.set(tenant(['MODULE_HRM']));
      expect(allLabels(menu.model())).not.toContain('Cost Center Mapping');
      t.tenant.set(tenant(['MODULE_HRM', 'MODULE_INTEGRATION']));
      expect(allLabels(menu.model())).toContain('Cost Center Mapping');
    });

    // BR-G8-002, task 089
    const FINANCE = ['AR_VIEW', 'AP_VIEW', 'INVOICE_VIEW', 'SALES_INVOICE_VIEW', 'GL_VIEW', 'CUSTOMER_PAYMENT_VIEW'];

    it('P2 (no source modules): AR and AP are hidden, General Ledger stays', () => {
      a.permissions = FINANCE;
      t.tenant.set(tenant(['MODULE_FINANCE', 'FEATURE_GENERAL_LEDGER']));
      const labels = allLabels(menu.model());
      for (const hidden of ['Receivables (AR)', 'Payables (AP)', 'Sales Invoices', 'Customer Payments', 'Invoices']) expect(labels).withContext(hidden).not.toContain(hidden);
      expect(labels).toContain('General Ledger');
    });

    it('P2-S (Customers and Suppliers, no Demand): AR and AP stay, for manual service invoices', () => {
      a.permissions = FINANCE;
      t.tenant.set(tenant(['MODULE_FINANCE', 'MODULE_CUSTOMERS', 'MODULE_SUPPLIERS']));
      const labels = allLabels(menu.model());
      expect(labels).toContain('Receivables (AR)');
      expect(labels).toContain('Payables (AP)');
    });

    it('each side follows its own source', () => {
      a.permissions = FINANCE;
      t.tenant.set(tenant(['MODULE_FINANCE', 'MODULE_POS']));
      expect(allLabels(menu.model())).toContain('Receivables (AR)');
      expect(allLabels(menu.model())).not.toContain('Payables (AP)');
      t.tenant.set(tenant(['MODULE_FINANCE', 'MODULE_SUPPLIERS']));
      expect(allLabels(menu.model())).not.toContain('Receivables (AR)');
      expect(allLabels(menu.model())).toContain('Payables (AP)');
    });
  });
});
