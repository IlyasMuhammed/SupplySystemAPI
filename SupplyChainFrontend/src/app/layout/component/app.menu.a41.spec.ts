import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Router, Routes, provideRouter } from '@angular/router';
import { AuthService } from '../../pages/service/auth.service';
import { AppMenu } from './app.menu';
import { CurrentTenant } from '../../pages/service/tenant.service';
import pageRoutes from '../../pages/pages.routes';
import { LeadListComponent } from '../../pages/crm/leads/lead-list.component';
import { LeadDetailComponent } from '../../pages/crm/leads/lead-detail.component';
import { OpportunityListComponent } from '../../pages/crm/opportunities/opportunity-list.component';
import { OpportunityDetailComponent } from '../../pages/crm/opportunities/opportunity-detail.component';
import { CrmActivityListComponent } from '../../pages/crm/activities/activity-list.component';
import { CampaignListComponent } from '../../pages/crm/campaigns/campaign-list.component';
import { CrmSettingsComponent } from '../../pages/crm/settings/crm-settings.component';

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
    roleName: 'Sales Rep', permissions: []
});

function find(items: any[], label: string): any {
    for (const i of items ?? []) {
        if (i.label === label) return i;
        const inner = find(i.items, label);
        if (inner) return inner;
    }
    return undefined;
}

const REP = ['CRM_VIEW_LEADS', 'CRM_VIEW_OPPORTUNITIES', 'CRM_VIEW_ACTIVITIES', 'CRM_VIEW_CAMPAIGNS'];

/** A41 — the CRM group (MODULE_CRM), Campaigns behind FEATURE_CRM_CAMPAIGN_TRACKING, CRM Settings on CRM_ADMIN; the seven routes guarded. */
describe('AppMenu — A41 CRM', () => {
    let tenantService: FakeTenantService;
    let authService: FakeAuthService;
    let menu: AppMenu;

    beforeEach(() => {
        tenantService = new FakeTenantService();
        authService = new FakeAuthService();
        menu = new AppMenu(authService as any, tenantService as any);
    });

    it('shows Leads, Opportunities, Activities and Campaigns to a sales rep on a CRM tenant', () => {
        authService.permissions = REP;
        tenantService.tenant.set(tenant(['MODULE_CRM', 'FEATURE_CRM_CAMPAIGN_TRACKING']));
        const crm = find(menu.model(), 'CRM');
        expect(crm.items.map((i: any) => i.label)).toEqual(['Leads', 'Opportunities', 'Activities', 'Campaigns']);
        expect(find(crm.items, 'Leads').routerLink).toEqual(['/portal/pages/crm/leads']);
        expect(find(crm.items, 'Opportunities').routerLink).toEqual(['/portal/pages/crm/opportunities']);
    });

    it('hides Campaigns when campaign tracking is off', () => {
        authService.permissions = REP;
        tenantService.tenant.set(tenant(['MODULE_CRM']));
        expect(find(menu.model(), 'CRM').items.map((i: any) => i.label)).toEqual(['Leads', 'Opportunities', 'Activities']);
    });

    it('hides the whole group without MODULE_CRM, and each entry without its permission', () => {
        authService.permissions = REP;
        tenantService.tenant.set(tenant([]));
        expect(find(menu.model(), 'Leads')).toBeUndefined();
        authService.permissions = ['CRM_VIEW_LEADS'];
        tenantService.tenant.set(tenant(['MODULE_CRM', 'FEATURE_CRM_CAMPAIGN_TRACKING']));
        expect(find(menu.model(), 'CRM').items.map((i: any) => i.label)).toEqual(['Leads']);
    });

    it('CRM Settings sits under Settings › Sales & Fulfilment for CRM_ADMIN on a CRM tenant only', () => {
        authService.permissions = ['CRM_ADMIN'];
        tenantService.tenant.set(tenant(['MODULE_CRM']));
        expect(find(menu.model(), 'Sales & Fulfilment').items.map((i: any) => i.label)).toContain('CRM Settings');
        expect(find(menu.model(), 'CRM Settings').routerLink).toEqual(['/portal/pages/crm/settings']);
        tenantService.tenant.set(tenant([]));
        expect(find(menu.model(), 'CRM Settings')).toBeUndefined();
        authService.permissions = REP;
        tenantService.tenant.set(tenant(['MODULE_CRM']));
        expect(find(menu.model(), 'CRM Settings')).toBeUndefined();
    });

    it('the routes use the same codes as the menu', () => {
        const routes = pageRoutes as Routes;
        const byPath = (p: string) => routes.find(r => r.path === p)!;
        expect(byPath('crm/leads').component).toBe(LeadListComponent);
        expect(byPath('crm/leads/:uuid').component).toBe(LeadDetailComponent);
        expect(byPath('crm/opportunities').component).toBe(OpportunityListComponent);
        expect(byPath('crm/opportunities/:uuid').component).toBe(OpportunityDetailComponent);
        expect(byPath('crm/activities').component).toBe(CrmActivityListComponent);
        expect(byPath('crm/campaigns').component).toBe(CampaignListComponent);
        expect(byPath('crm/settings').component).toBe(CrmSettingsComponent);

        const auth = { isAuthenticated: () => true, perms: [] as string[],
                       hasAnyPermission(...c: string[]) { return c.some(x => this.perms.includes(x)); } };
        TestBed.configureTestingModule({ providers: [provideRouter([]), { provide: AuthService, useValue: auth }] });
        spyOn(TestBed.inject(Router), 'navigate').and.resolveTo(true);
        const allows = (path: string, perms: string[]) => {
            auth.perms = perms;
            const guard = byPath(path).canActivate![0] as any;
            return TestBed.runInInjectionContext(() => guard({}, {}));
        };
        expect(allows('crm/leads', ['CRM_VIEW_LEADS'])).toBeTrue();
        expect(allows('crm/leads/:uuid', ['CRM_VIEW_OPPORTUNITIES'])).toBeFalse();
        expect(allows('crm/opportunities/:uuid', ['CRM_VIEW_OPPORTUNITIES'])).toBeTrue();
        expect(allows('crm/activities', ['CRM_VIEW_ACTIVITIES'])).toBeTrue();
        expect(allows('crm/campaigns', ['CRM_MANAGE_CAMPAIGNS'])).toBeTrue();
        expect(allows('crm/campaigns', ['CRM_VIEW_LEADS'])).toBeFalse();
        expect(allows('crm/settings', ['CRM_ADMIN'])).toBeTrue();
        expect(allows('crm/settings', ['CRM_VIEW_LEADS'])).toBeFalse();
    });
});
