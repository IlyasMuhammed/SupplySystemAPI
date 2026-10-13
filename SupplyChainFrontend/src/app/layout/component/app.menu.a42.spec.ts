import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, Routes, provideRouter } from '@angular/router';
import { of } from 'rxjs';
import { AuthService } from '../../pages/service/auth.service';
import { AppMenu } from './app.menu';
import { CurrentTenant, TenantService } from '../../pages/service/tenant.service';
import { AccountingService, SetupStatus } from '../../services/accounting.service';
import pageRoutes from '../../pages/pages.routes';
import { GlDashboardComponent } from '../../pages/finance/gl/dashboard/gl-dashboard.component';
import { GlSetupWizardComponent } from '../../pages/finance/gl/setup-wizard/gl-setup-wizard.component';
import { GlAccountsComponent } from '../../pages/finance/gl/accounts/gl-accounts.component';
import { GlFiscalYearsComponent } from '../../pages/finance/gl/fiscal-years/gl-fiscal-years.component';
import { GlDefaultsComponent } from '../../pages/finance/gl/defaults/gl-defaults.component';
import { GlOpeningBalancesComponent } from '../../pages/finance/gl/opening-balances/gl-opening-balances.component';
import { GlIntegrationsComponent } from '../../pages/finance/gl/integrations/gl-integrations.component';
import { GlSetupStatusComponent } from '../../pages/finance/gl/setup-status/gl-setup-status.component';
import { glFeatureGuard, glSetupCompleteGuard } from '../../pages/finance/gl/gl.guards';

class FakeTenantService {
    tenant = signal<CurrentTenant | null>(null);
    hasFeature(code: string): boolean { return this.tenant()?.enabledFeatureCodes.includes(code) ?? false; }
    isSuperAdmin(): boolean { return this.tenant()?.isSuperAdmin ?? false; }
}

class FakeAuthService {
    permissions: string[] = [];
    hasAnyPermission(...codes: string[]): boolean { return codes.some((c) => this.permissions.includes(c)); }
}

class FakeAccounting {
    setupState = signal<SetupStatus | null>(null);
    loads = 0;
    ensureSetupState(): void { this.loads++; }
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
const ITEMS = ['Dashboard', 'Chart of Accounts', 'Fiscal Years', 'Defaults', 'Opening Balances', 'Integrations', 'Setup Status'];

/** A42 D-26 — Finance › General Ledger (FEATURE_GENERAL_LEDGER + GL_VIEW); first entry "Set up general ledger" until Complete. */
describe('AppMenu — A42 General Ledger', () => {
    let tenantService: FakeTenantService;
    let authService: FakeAuthService;
    let accounting: FakeAccounting;
    let menu: AppMenu;

    beforeEach(() => {
        tenantService = new FakeTenantService();
        authService = new FakeAuthService();
        accounting = new FakeAccounting();
        menu = new AppMenu(authService as any, tenantService as any, undefined, accounting as any);
    });

    it('shows the group under Finance with its seven entries for GL_VIEW on a GL tenant', () => {
        authService.permissions = ['GL_VIEW'];
        tenantService.tenant.set(tenant(GL));
        const gl = find(find(menu.model(), 'Finance').items, 'General Ledger');
        expect(gl.items.map((i: any) => i.label)).toEqual(ITEMS);
        expect(find(gl.items, 'Chart of Accounts').routerLink).toEqual(['/portal/pages/finance/gl/accounts']);
        expect(find(gl.items, 'Setup Status').routerLink).toEqual(['/portal/pages/finance/gl/setup-status']);
        expect(accounting.loads).toBe(1);
    });

    it('a custom role holding only a GL manage code sees the group too', () => {
        for (const code of ['GL_ACCOUNT_MANAGE', 'GL_SETUP_MANAGE']) {
            authService.permissions = [code];
            tenantService.tenant.set(tenant([...GL]));
            expect(find(menu.model(), 'General Ledger')).withContext(code).toBeDefined();
        }
    });

    it('hidden without FEATURE_GENERAL_LEDGER or without GL_VIEW', () => {
        authService.permissions = ['GL_VIEW', 'INVOICE_VIEW'];
        tenantService.tenant.set(tenant(['MODULE_FINANCE']));
        expect(find(menu.model(), 'General Ledger')).toBeUndefined();
        authService.permissions = ['INVOICE_VIEW'];
        tenantService.tenant.set(tenant(GL));
        expect(find(menu.model(), 'General Ledger')).toBeUndefined();
        expect(accounting.loads).toBe(0);
    });

    it('"Set up general ledger" (→ wizard) while setup is not Complete; Dashboard once it is', () => {
        authService.permissions = ['GL_VIEW'];
        tenantService.tenant.set(tenant(GL));
        accounting.setupState.set({ status: 'FiscalYearSet' } as SetupStatus);
        const first = find(menu.model(), 'General Ledger').items[0];
        expect(first.label).toBe('Set up general ledger');
        expect(first.routerLink).toEqual(['/portal/pages/finance/gl/setup']);
        accounting.setupState.set({ status: 'Complete' } as SetupStatus);
        const done = find(menu.model(), 'General Ledger').items[0];
        expect(done.label).toBe('Dashboard');
        expect(done.routerLink).toEqual(['/portal/pages/finance/gl']);
    });

    it('works without the accounting service (other specs build the menu with two arguments)', () => {
        const plain = new AppMenu(authService as any, tenantService as any);
        authService.permissions = ['GL_VIEW'];
        tenantService.tenant.set(tenant(GL));
        expect(find(plain.model(), 'General Ledger').items[0].label).toBe('Dashboard');
    });

    it('the routes: same component, GL_VIEW + the feature guard; the dashboard also needs setup Complete', () => {
        const routes = pageRoutes as Routes;
        const byPath = (p: string) => routes.find(r => r.path === p)!;
        const expected: [string, any][] = [
            ['finance/gl', GlDashboardComponent], ['finance/gl/setup', GlSetupWizardComponent], ['finance/gl/accounts', GlAccountsComponent],
            ['finance/gl/fiscal-years', GlFiscalYearsComponent], ['finance/gl/defaults', GlDefaultsComponent],
            ['finance/gl/opening-balances', GlOpeningBalancesComponent], ['finance/gl/integrations', GlIntegrationsComponent],
            ['finance/gl/setup-status', GlSetupStatusComponent]
        ];
        for (const [path, component] of expected) {
            expect(byPath(path).component).withContext(path).toBe(component);
            expect(byPath(path).canActivate).withContext(path).toContain(glFeatureGuard);
        }
        expect(byPath('finance/gl').canActivate).toContain(glSetupCompleteGuard);
        expect(byPath('finance/gl/setup').canActivate).not.toContain(glSetupCompleteGuard);

        const auth = { isAuthenticated: () => true, perms: [] as string[],
                       hasAnyPermission(...c: string[]) { return c.some(x => this.perms.includes(x)); } };
        TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: AuthService, useValue: auth },
            { provide: TenantService, useValue: { tenant: () => null, loadCurrent: () => of(null) } }] });
        spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
        const allows = (path: string, perms: string[]) => {
            auth.perms = perms;
            const guard = byPath(path).canActivate![0] as any;
            return TestBed.runInInjectionContext(() => guard({}, {}));
        };
        for (const [path] of expected) {
            expect(allows(path, ['GL_VIEW'])).withContext(path).toBeTrue();
            expect(allows(path, ['GL_ACCOUNT_MANAGE'])).withContext(path + ' account manage').toBeTrue();
            expect(allows(path, ['GL_SETUP_MANAGE'])).withContext(path + ' setup manage').toBeTrue();
            expect(allows(path, ['INVOICE_VIEW'])).withContext(path).toBeFalse();
        }
        expect(AccountingService).toBeDefined();
    });
});
