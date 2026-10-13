import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, Routes, provideRouter } from '@angular/router';
import { AuthService } from '../../pages/service/auth.service';
import { AppMenu } from './app.menu';
import { CurrentTenant } from '../../pages/service/tenant.service';
import pageRoutes from '../../pages/pages.routes';
import { ImportDashboardComponent } from '../../pages/settings/import/import-dashboard/import-dashboard.component';
import { ImportMigrationComponent } from '../../pages/settings/import/import-migration/import-migration.component';
import { ImportSessionDetailComponent } from '../../pages/settings/import/import-session-detail/import-session-detail.component';
import { ImportWizardComponent } from '../../pages/settings/import/import-wizard/import-wizard.component';

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
    id: 'org-1', orgCode: 'ORG1', orgName: 'Org One', plan: 'STANDARD', enabledFeatureCodes: features, isSuperAdmin: false,
    roleName: 'Finance Manager', permissions: []
});

function find(items: any[], label: string): any {
    for (const i of items ?? []) {
        if (i.label === label) return i;
        const inner = find(i.items, label);
        if (inner) return inner;
    }
    return undefined;
}

/** A40 — Settings › System › "Data Import": IMPORT_PROFILE_VIEW + MODULE_DATA_IMPORT; the four routes guarded. */
describe('AppMenu — A40 Data Import', () => {
    let tenantService: FakeTenantService;
    let authService: FakeAuthService;
    let menu: AppMenu;

    beforeEach(() => {
        tenantService = new FakeTenantService();
        authService = new FakeAuthService();
        menu = new AppMenu(authService as any, tenantService as any);
    });

    it('shows "Data Import" under Settings › System with IMPORT_PROFILE_VIEW and MODULE_DATA_IMPORT', () => {
        authService.permissions = ['IMPORT_PROFILE_VIEW'];
        tenantService.tenant.set(tenant(['MODULE_DATA_IMPORT']));
        const system = find(menu.model(), 'System');
        expect(system.items.map((i: any) => i.label)).toEqual(['Data Import']);
        expect(system.items[0].routerLink).toEqual(['/portal/pages/settings/import']);
    });

    it('hidden without the permission or the module', () => {
        authService.permissions = ['IMPORT_UPLOAD'];
        tenantService.tenant.set(tenant(['MODULE_DATA_IMPORT']));
        expect(find(menu.model(), 'Data Import')).toBeUndefined();
        authService.permissions = ['IMPORT_PROFILE_VIEW'];
        tenantService.tenant.set(tenant([]));
        expect(find(menu.model(), 'Data Import')).toBeUndefined();
    });

    it('the routes: dashboard + migration on IMPORT_PROFILE_VIEW, wizard on IMPORT_UPLOAD, session on UPLOAD or MANAGE', () => {
        const routes = pageRoutes as Routes;
        const byPath = (p: string) => routes.find(r => r.path === p)!;
        expect(byPath('settings/import').component).toBe(ImportDashboardComponent);
        expect(byPath('settings/import/migration').component).toBe(ImportMigrationComponent);
        expect(byPath('settings/import/sessions/:uuid').component).toBe(ImportSessionDetailComponent);
        expect(byPath('settings/import/:entityType').component).toBe(ImportWizardComponent);
        // "migration" and "sessions" must come before ":entityType".
        const idx = (p: string) => routes.findIndex(r => r.path === p);
        expect(idx('settings/import/migration')).toBeLessThan(idx('settings/import/:entityType'));
        expect(idx('settings/import/sessions/:uuid')).toBeLessThan(idx('settings/import/:entityType'));

        const auth = { isAuthenticated: () => true, perms: [] as string[],
                       hasAnyPermission(...c: string[]) { return c.some(x => this.perms.includes(x)); } };
        TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: AuthService, useValue: auth }] });
        spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
        const allows = (path: string, perms: string[]) => {
            auth.perms = perms;
            const guard = byPath(path).canActivate![0] as any;
            return TestBed.runInInjectionContext(() => guard({}, {}));
        };
        expect(allows('settings/import', ['IMPORT_PROFILE_VIEW'])).toBeTrue();
        expect(allows('settings/import', ['IMPORT_UPLOAD'])).toBeFalse();
        expect(allows('settings/import/migration', ['IMPORT_PROFILE_VIEW'])).toBeTrue();
        expect(allows('settings/import/:entityType', ['IMPORT_UPLOAD'])).toBeTrue();
        expect(allows('settings/import/:entityType', ['IMPORT_PROFILE_VIEW'])).toBeFalse();
        expect(allows('settings/import/sessions/:uuid', ['IMPORT_MANAGE'])).toBeTrue();
        expect(allows('settings/import/sessions/:uuid', ['IMPORT_UPLOAD'])).toBeTrue();
        expect(allows('settings/import/sessions/:uuid', ['IMPORT_EXECUTE'])).toBeFalse();
    });
});
