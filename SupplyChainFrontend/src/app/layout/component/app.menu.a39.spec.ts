import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, Routes, provideRouter } from '@angular/router';
import { AuthService } from '../../pages/service/auth.service';
import { AppMenu } from './app.menu';
import { CurrentTenant } from '../../pages/service/tenant.service';
import pageRoutes from '../../pages/pages.routes';

class FakeTenantService {
    tenant = signal<CurrentTenant | null>(null);
    hasFeature(code: string): boolean { return this.tenant()?.enabledFeatureCodes.includes(code) ?? false; }
    isSuperAdmin(): boolean { return this.tenant()?.isSuperAdmin ?? false; }
}

class FakeAuthService {
    permissions: string[] = [];
    hasAnyPermission(...codes: string[]): boolean { return codes.some((c) => this.permissions.includes(c)); }
}

const tenant = (features: string[]): CurrentTenant => ({
    id: 'org-1', orgCode: 'ORG1', orgName: 'Org One', plan: 'ENTERPRISE', enabledFeatureCodes: features, isSuperAdmin: false,
    roleName: 'Store Manager', permissions: []
});

/** A39 — Point of Sale gains "Store credit" (POS_CREDIT_NOTE_VIEW + FEATURE_POS_CREDIT_NOTES), "Return reasons", "Return policy". */
describe('AppMenu — A39 returns & store credit', () => {
    let tenantService: FakeTenantService;
    let authService: FakeAuthService;
    let menu: AppMenu;

    const posGroup = () => menu.model().find((g: any) => g.label === 'Point of Sale') as any;
    const labels = () => (posGroup()?.items ?? []).map((i: any) => i.label);
    const item = (label: string) => (posGroup()?.items ?? []).find((i: any) => i.label === label);

    beforeEach(() => {
        tenantService = new FakeTenantService();
        authService = new FakeAuthService();
        menu = new AppMenu(authService as any, tenantService as any);
    });

    it('a store manager with every A39 code sees the three items in order, linking to the A39 routes', () => {
        authService.permissions = ['POS_STORE_VIEW', 'POS_SHIFT_VIEW', 'POS_ORDER_VIEW', 'POS_ORDER_CREATE',
                                   'POS_CREDIT_NOTE_VIEW', 'POS_RETURN_REASON_MANAGE', 'POS_RETURN_POLICY_MANAGE'];
        tenantService.tenant.set(tenant(['MODULE_POS', 'FEATURE_POS_CREDIT_NOTES']));
        expect(labels()).toEqual(['Stores', 'Shifts', 'POS Orders', 'Store credit', 'Return reasons', 'Return policy', 'Open POS']);
        expect(item('Store credit').routerLink).toEqual(['/portal/pages/pos/credit-notes']);
        expect(item('Return reasons').routerLink).toEqual(['/portal/pages/pos/return-reasons']);
        expect(item('Return policy').routerLink).toEqual(['/portal/pages/pos/return-policy']);
    });

    it('Store credit needs FEATURE_POS_CREDIT_NOTES and POS_CREDIT_NOTE_VIEW', () => {
        authService.permissions = ['POS_CREDIT_NOTE_VIEW'];
        tenantService.tenant.set(tenant(['MODULE_POS']));
        expect(posGroup()).toBeUndefined();
        tenantService.tenant.set(tenant(['MODULE_POS', 'FEATURE_POS_CREDIT_NOTES']));
        expect(labels()).toEqual(['Store credit']);
        authService.permissions = ['POS_CREDIT_NOTE_REDEEM'];
        tenantService.tenant.set(tenant(['MODULE_POS', 'FEATURE_POS_CREDIT_NOTES']));
        expect(posGroup()).toBeUndefined();
    });

    it('Return reasons / Return policy follow their own manage codes', () => {
        authService.permissions = ['POS_RETURN_REASON_MANAGE'];
        tenantService.tenant.set(tenant(['MODULE_POS']));
        expect(labels()).toEqual(['Return reasons']);
        authService.permissions = ['POS_RETURN_POLICY_MANAGE'];
        tenantService.tenant.set(tenant(['MODULE_POS']));
        expect(labels()).toEqual(['Return policy']);
        tenantService.tenant.set(tenant([]));
        expect(posGroup()).toBeUndefined();
    });

    it('the A39 routes are guarded by the same codes', () => {
        const expected: Record<string, string> = {
            'pos/return-policy': 'POS_RETURN_POLICY_MANAGE', 'pos/return-reasons': 'POS_RETURN_REASON_MANAGE',
            'pos/credit-notes': 'POS_CREDIT_NOTE_VIEW', 'pos/credit-notes/:uuid': 'POS_CREDIT_NOTE_VIEW'
        };
        const routes = (pageRoutes as Routes).filter(r => (r.path ?? '') in expected);
        expect(routes.length).toBe(4);
        const auth = { isAuthenticated: () => true, perms: [] as string[],
                       hasAnyPermission(...c: string[]) { return c.some(x => this.perms.includes(x)); } };
        TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: AuthService, useValue: auth }] });
        spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
        for (const r of routes) {
            expect(r.canActivate?.length).toBe(1);
            const guard = r.canActivate![0] as any;
            auth.perms = [expected[r.path!]];
            expect(TestBed.runInInjectionContext(() => guard({}, {}))).withContext(r.path!).toBeTrue();
            auth.perms = ['POS_ORDER_VIEW'];
            expect(TestBed.runInInjectionContext(() => guard({}, {}))).withContext(r.path!).toBeFalse();
        }
    });
});
