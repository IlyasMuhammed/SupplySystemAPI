import { signal } from '@angular/core';
import { Routes } from '@angular/router';
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

/** A38 — the "Point of Sale" menu group (featureCode MODULE_POS; each item gated by its own POS_* code). */
describe('AppMenu — A38 Point of Sale', () => {
    let tenantService: FakeTenantService;
    let authService: FakeAuthService;
    let menu: AppMenu;

    const posGroup = () => menu.model().find((g: any) => g.label === 'Point of Sale') as any;
    const labels = () => (posGroup()?.items ?? []).map((i: any) => i.label);

    beforeEach(() => {
        tenantService = new FakeTenantService();
        authService = new FakeAuthService();
        menu = new AppMenu(authService as any, tenantService as any);
    });

    it('hidden without MODULE_POS even with every POS permission', () => {
        authService.permissions = ['POS_STORE_VIEW', 'POS_SHIFT_VIEW', 'POS_ORDER_VIEW', 'POS_ORDER_CREATE'];
        tenantService.tenant.set(tenant([]));
        expect(posGroup()).toBeUndefined();
    });

    it('a store manager sees Stores, Shifts, POS Orders and Open POS', () => {
        authService.permissions = ['POS_STORE_VIEW', 'POS_SHIFT_VIEW', 'POS_ORDER_VIEW', 'POS_ORDER_CREATE'];
        tenantService.tenant.set(tenant(['MODULE_POS']));
        expect(labels()).toEqual(['Stores', 'Shifts', 'POS Orders', 'Open POS']);
        const items = posGroup().items;
        expect(items[0].routerLink).toEqual(['/portal/pages/pos/stores']);
        expect(items[1].routerLink).toEqual(['/portal/pages/pos/shifts']);
        expect(items[2].routerLink).toEqual(['/portal/pages/pos/orders']);
    });

    it('a cashier (POS_ORDER_CREATE only) sees just "Open POS", which opens /pos in a new tab', () => {
        authService.permissions = ['POS_ORDER_CREATE'];
        tenantService.tenant.set(tenant(['MODULE_POS']));
        expect(labels()).toEqual(['Open POS']);
        const open = posGroup().items[0];
        expect(open.url).toBe('/pos');
        expect(open.target).toBe('_blank');
        expect(open.routerLink).toBeUndefined();
    });

    it('each item follows its own code; no POS code hides the group', () => {
        authService.permissions = ['POS_SHIFT_VIEW'];
        tenantService.tenant.set(tenant(['MODULE_POS']));
        expect(labels()).toEqual(['Shifts']);
        authService.permissions = ['CUSTOMER_VIEW'];
        tenantService.tenant.set(tenant(['MODULE_POS']));
        expect(posGroup()).toBeUndefined();
    });

    it('the routes exist and are guarded', () => {
        const routes = (pageRoutes as Routes).filter(r => r.path?.startsWith('pos/'));
        expect(routes.map(r => r.path)).toEqual([
            'pos/stores', 'pos/stores/:uuid', 'pos/shifts', 'pos/shifts/:uuid', 'pos/orders', 'pos/orders/:uuid',
            // A39 (app.menu.a39.spec.ts)
            'pos/return-policy', 'pos/return-reasons', 'pos/credit-notes', 'pos/credit-notes/:uuid'
        ]);
        expect(routes.every(r => (r.canActivate?.length ?? 0) === 1)).toBeTrue();
    });
});
