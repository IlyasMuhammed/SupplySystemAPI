import { signal } from '@angular/core';
import { AppMenu } from './app.menu';
import { CurrentTenant } from '../../pages/service/tenant.service';

// Duck-typed test doubles — AppMenu only ever calls these specific members, and its `model` is a
// plain computed() signal, so constructing it directly (bypassing TestBed/HttpClient/Router) is
// both simpler and a more direct test of the actual filtering logic (MT-005 acceptance criteria).
class FakeTenantService {
    tenant = signal<CurrentTenant | null>(null);
    hasFeature(code: string): boolean {
        return this.tenant()?.enabledFeatureCodes.includes(code) ?? false;
    }
    isSuperAdmin(): boolean {
        return this.tenant()?.isSuperAdmin ?? false;
    }
}

class FakeAuthService {
    permissions: string[] = [];
    hasAnyPermission(...codes: string[]): boolean {
        return codes.some((c) => this.permissions.includes(c));
    }
}

function baseTenant(overrides: Partial<CurrentTenant> = {}): CurrentTenant {
    return {
        id: 'org-1',
        orgCode: 'ORG1',
        orgName: 'Org One',
        plan: 'ENTERPRISE',
        enabledFeatureCodes: [],
        isSuperAdmin: false,
        roleName: 'Requester',
        permissions: [],
        ...overrides
    };
}

/** Flattens the filtered menu tree into a flat set of labels for easy assertions. */
function allLabels(items: any[]): string[] {
    const labels: string[] = [];
    for (const item of items) {
        if (item.label) labels.push(item.label);
        if (item.items?.length) labels.push(...allLabels(item.items));
    }
    return labels;
}

describe('AppMenu filtering (MT-005)', () => {
    let tenantService: FakeTenantService;
    let authService: FakeAuthService;
    let menu: AppMenu;

    beforeEach(() => {
        tenantService = new FakeTenantService();
        authService = new FakeAuthService();
        menu = new AppMenu(authService as any, tenantService as any);
    });

    it('renders nothing before GET /api/tenant/current resolves', () => {
        expect(menu.model()).toEqual([]);
    });

    it('hides Finance menu items when MODULE_FINANCE is disabled for the org', () => {
        authService.permissions = ['INVOICE_VIEW', 'PAYMENT_VIEW'];
        tenantService.tenant.set(baseTenant({
            enabledFeatureCodes: ['MODULE_DEMAND'], // Finance deliberately absent
            permissions: authService.permissions
        }));

        expect(allLabels(menu.model())).not.toContain('Finance');
    });

    it('shows Finance menu items once the (same) user is in an org with MODULE_FINANCE enabled', () => {
        authService.permissions = ['INVOICE_VIEW', 'PAYMENT_VIEW'];

        // First org: Finance disabled.
        tenantService.tenant.set(baseTenant({ enabledFeatureCodes: [], permissions: authService.permissions }));
        expect(allLabels(menu.model())).not.toContain('Finance');

        // Same user, different org (matches AuthService.logout()+re-login clearing/refreshing
        // TenantService's signal): Finance enabled this time.
        tenantService.tenant.set(baseTenant({ enabledFeatureCodes: ['MODULE_FINANCE'], permissions: authService.permissions }));
        expect(allLabels(menu.model())).toContain('Finance');
    });

    it('shows System Administration only to Super Admin, not to a regular Admin', () => {
        tenantService.tenant.set(baseTenant({ isSuperAdmin: true, enabledFeatureCodes: [] }));
        expect(allLabels(menu.model())).toContain('System Administration');

        tenantService.tenant.set(baseTenant({
            isSuperAdmin: false,
            roleName: 'Organization Admin',
            enabledFeatureCodes: ['MODULE_MASTER_DATA']
        }));
        expect(allLabels(menu.model())).not.toContain('System Administration');
    });

    it('Super Admin sees every module regardless of any org feature toggle', () => {
        tenantService.tenant.set(baseTenant({ isSuperAdmin: true, enabledFeatureCodes: [] }));

        const labels = allLabels(menu.model());
        expect(labels).toContain('Finance');
        expect(labels).toContain('Logistics');
        expect(labels).toContain('Material Management');
    });

    it('a Requester with no procurement permissions sees no Procurement menu, even with the feature enabled', () => {
        authService.permissions = []; // no REQUISITION_*/RFQ_*/PO_* grants
        tenantService.tenant.set(baseTenant({
            roleName: 'Requester',
            enabledFeatureCodes: ['MODULE_DEMAND'],
            permissions: authService.permissions
        }));

        expect(allLabels(menu.model())).not.toContain('Procurement');
    });

    it('a Procurement Manager in the same org sees the Procurement menu', () => {
        authService.permissions = ['PO_CREATE', 'PO_VIEW'];
        tenantService.tenant.set(baseTenant({
            roleName: 'Procurement Manager',
            enabledFeatureCodes: ['MODULE_DEMAND'],
            permissions: authService.permissions
        }));

        const labels = allLabels(menu.model());
        expect(labels).toContain('Procurement');
        expect(labels).toContain('Purchase Orders');
    });

    it('Organization Admin is a real permission holder, not a blanket bypass — sees only what its default grants (USER_MANAGE, PO_TEMPLATE_MANAGE) justify', () => {
        authService.permissions = ['USER_MANAGE', 'PO_TEMPLATE_MANAGE']; // Org Admin's actual defaults
        tenantService.tenant.set(baseTenant({
            roleName: 'Organization Admin',
            isSuperAdmin: false,
            enabledFeatureCodes: ['MODULE_FINANCE', 'MODULE_DEMAND', 'MODULE_SUPPLIERS', 'SCREEN_USER_MANAGEMENT'],
            permissions: authService.permissions
        }));

        const labels = allLabels(menu.model());
        expect(labels).toContain('Users');
        expect(labels).not.toContain('Finance');
        expect(labels).not.toContain('Procurement');
        expect(labels).not.toContain('System Administration'); // still not Super Admin
    });
});
