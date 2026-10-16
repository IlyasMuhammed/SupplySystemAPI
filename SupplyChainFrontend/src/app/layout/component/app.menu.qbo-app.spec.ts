import { signal } from '@angular/core';
import { Routes } from '@angular/router';
import { AppMenu } from './app.menu';
import { CurrentTenant } from '../../pages/service/tenant.service';
import pageRoutes from '../../pages/pages.routes';
import { QuickBooksAppSettingsComponent } from '../../pages/system/quickbooks-app/quickbooks-app-settings.component';

class FakeTenantService {
    tenant = signal<CurrentTenant | null>(null);
    hasFeature(code: string): boolean { return this.tenant()?.enabledFeatureCodes.includes(code) ?? false; }
    isSuperAdmin(): boolean { return this.tenant()?.isSuperAdmin ?? false; }
}

class FakeAuthService {
    permissions: string[] = [];
    hasAnyPermission(...codes: string[]): boolean { return codes.some((c) => this.permissions.includes(c)); }
}

const tenant = (isSuperAdmin: boolean): CurrentTenant => ({
    id: 'org-1', orgCode: 'ORG1', orgName: 'Org One', plan: 'ENTERPRISE', enabledFeatureCodes: ['MODULE_INTEGRATION'], isSuperAdmin,
    roleName: 'Admin', permissions: []
});

/** docs/quickbooks/APP-CREDENTIALS.md — System Administration › QuickBooks App: super admins only. */
describe('AppMenu — QuickBooks App (platform keys)', () => {
    let tenantService: FakeTenantService;
    let authService: FakeAuthService;
    let menu: AppMenu;

    beforeEach(() => {
        tenantService = new FakeTenantService();
        authService = new FakeAuthService();
        menu = new AppMenu(authService as any, tenantService as any);
    });

    const group = () => menu.model().find((g: any) => g.label === 'System Administration');

    it('is under System Administration for a super admin', () => {
        tenantService.tenant.set(tenant(true));
        expect(group()!.items!.map((i: any) => i.label)).toEqual(['Organizations', 'QuickBooks App']);
        expect(group()!.items![1].routerLink).toEqual(['/portal/pages/system/quickbooks-app']);
    });

    it('is not shown to an organization admin, even one holding INTEGRATION_MANAGE', () => {
        authService.permissions = ['INTEGRATION_MANAGE', 'INTEGRATION_VIEW'];
        tenantService.tenant.set(tenant(false));
        expect(group()).toBeUndefined();
    });

    it('the route is guarded and points at the page', () => {
        const route = (pageRoutes as Routes).find(r => r.path === 'system/quickbooks-app')!;
        expect(route.component).toBe(QuickBooksAppSettingsComponent);
        expect(route.canActivate?.length).toBe(1);
    });
});
