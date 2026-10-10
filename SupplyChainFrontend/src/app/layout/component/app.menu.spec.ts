import { signal } from '@angular/core';
import { Routes } from '@angular/router';
import { AppMenu } from './app.menu';
import { CurrentTenant } from '../../pages/service/tenant.service';
import pageRoutes from '../../pages/pages.routes';

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

    // ── T-21: the delivery cockpit's menu entry ───────────────────────────────

    it('shows Deliveries to a user with DELIVERY_VIEW in a logistics-enabled org', () => {
        authService.permissions = ['DELIVERY_VIEW'];
        tenantService.tenant.set(baseTenant({
            enabledFeatureCodes: ['MODULE_LOGISTICS'],
            permissions: authService.permissions
        }));

        const labels = allLabels(menu.model());
        expect(labels).toContain('Logistics');
        expect(labels).toContain('Deliveries');
        expect(labels).toContain('All Deliveries');
    });

    it('hides Deliveries from a user who only has the legacy shipment permission', () => {
        // DELIVERY_TRACK grants the old carrier and shipment screens. The rebuilt cockpit reads
        // a different model and is granted separately, so it must not come along for the ride.
        authService.permissions = ['DELIVERY_TRACK'];
        tenantService.tenant.set(baseTenant({
            enabledFeatureCodes: ['MODULE_LOGISTICS'],
            permissions: authService.permissions
        }));

        const labels = allLabels(menu.model());
        expect(labels).toContain('Shipments (Legacy)');
        expect(labels).not.toContain('Deliveries');
    });

    it('hides Deliveries when the org has no logistics module, whatever the permission', () => {
        authService.permissions = ['DELIVERY_VIEW'];
        tenantService.tenant.set(baseTenant({
            enabledFeatureCodes: [],
            permissions: authService.permissions
        }));

        expect(allLabels(menu.model())).not.toContain('Deliveries');
    });

    // ── Menu hygiene: no dead links, no unreachable screens ──────────────────

    /** Every routerLink in the menu tree (Super Admin sees all of it unfiltered). */
    function allLinks(items: any[]): string[] {
        const links: string[] = [];
        for (const item of items) {
            if (item.routerLink) links.push(item.routerLink[0]);
            if (item.items?.length) links.push(...allLinks(item.items));
        }
        return links;
    }

    it('every menu link points at a route that exists', () => {
        tenantService.tenant.set(baseTenant({ isSuperAdmin: true, enabledFeatureCodes: [] }));
        const paths = new Set((pageRoutes as Routes).map(r => r.path));

        const dead = allLinks(menu.model())
            .filter(link => link.startsWith('/portal/pages/'))
            .map(link => link.substring('/portal/pages/'.length))
            .filter(path => !paths.has(path));

        expect(dead).toEqual([]);
    });

    it('reaches the logistics tracking and freight settlement screens from the menu', () => {
        authService.permissions = ['DELIVERY_VIEW', 'FREIGHT_INVOICE_VIEW', 'SHIPMENT_RATE_VIEW', 'SHIPPING_RULE_MANAGE', 'DELIVERY_TRACK'];
        // A37 — the Tracking screens need only Logistics (FEATURE_SHIPMENT_TRACKING guards consignments, not these).
        tenantService.tenant.set(baseTenant({ enabledFeatureCodes: ['MODULE_LOGISTICS'], permissions: authService.permissions }));

        const labels = allLabels(menu.model());
        for (const label of ['Exception Queue', 'Proof of Delivery', 'Carrier Scorecard', 'Shipping Rules',
                             'Carrier Invoices', 'Match Queue', 'COD Reconciliation', 'Freight Accruals']) {
            expect(labels).withContext(label).toContain(label);
        }
    });

    it('no longer offers to create a legacy shipment, the shipment tracker or the legacy payment history', () => {
        tenantService.tenant.set(baseTenant({ isSuperAdmin: true, enabledFeatureCodes: [] }));

        const labels = allLabels(menu.model());
        const links = allLinks(menu.model());
        expect(labels).not.toContain('New Shipment');
        expect(labels).not.toContain('Shipment Tracker');
        expect(labels).not.toContain('Payment History (Legacy)');
        expect(links).not.toContain('/portal/pages/finance/payments-legacy');
    });

    it('names the two kinds of quotation, price list and consumption view apart', () => {
        tenantService.tenant.set(baseTenant({ isSuperAdmin: true, enabledFeatureCodes: [] }));

        const labels = allLabels(menu.model());
        expect(labels).toContain('Supplier Quotes (RFQ)');
        expect(labels).toContain('Customer Quotations');
        expect(labels).toContain('Supplier Price Lists');
        expect(labels).toContain('Approval Backlog (All)');
        expect(labels).toContain('Consumption (Detail)');
        expect(labels).toContain('Consumption (Summary)');
    });

    // ── Layout: follows the flow of the work ──────────────────────────────

    /** The children's labels of the top-level group with this label. */
    const childrenOf = (label: string) =>
        menu.model().find((g: any) => g.label === label)?.items?.map((i: any) => i.label) ?? [];

    it('orders the groups sales → production → procurement → warehouse → fulfilment → finance, then reports and administration', () => {
        tenantService.tenant.set(baseTenant({ isSuperAdmin: true, enabledFeatureCodes: [] }));

        expect(menu.model().map((g: any) => g.label)).toEqual([
            'Home', 'Sales', 'Manufacturing', 'Services', 'Procurement', 'Warehouse & Inventory', 'Material Management',
            'Logistics', 'Finance', 'Reports & Analytics', 'Administration', 'System Administration'
        ]);
    });

    it('puts My Inbox and the KPI Dashboard under Home', () => {
        tenantService.tenant.set(baseTenant({ isSuperAdmin: true, enabledFeatureCodes: [] }));

        expect(childrenOf('Home')).toEqual(['Dashboard', 'My Inbox', 'KPI Dashboard']);
        expect(allLabels(menu.model()).filter(l => l === 'KPI Dashboard').length).toBe(1);
        expect(allLabels(menu.model())).not.toContain('Approvals');
    });

    it('keeps every report and ledger under Reports, sectioned in the same order as the groups', () => {
        tenantService.tenant.set(baseTenant({ isSuperAdmin: true, enabledFeatureCodes: [] }));

        expect(childrenOf('Reports & Analytics')).toEqual(
            ['Sales', 'Manufacturing', 'Procurement', 'Inventory & Warehouse', 'Material', 'Finance', 'Audit & Compliance']);

        const reports = allLabels(menu.model().filter((g: any) => g.label === 'Reports & Analytics'));
        for (const ledger of ['Manufacturing Reports', 'Master Product Ledger', 'Master Payables Ledger', 'Customer Ledger',
                              'Department Cost Ledger', 'Batch / Serial Trace', 'Consumption (Detail)', 'Stock Movement / Ledger']) {
            expect(reports).withContext(ledger).toContain(ledger);
        }
        // …and nowhere else.
        for (const group of ['Manufacturing', 'Warehouse & Inventory', 'Material Management', 'Finance']) {
            const labels = allLabels(menu.model().filter((g: any) => g.label === group));
            expect(labels).not.toContain('Manufacturing Reports');
            expect(labels).not.toContain('Master Product Ledger');
            expect(labels).not.toContain('Master Payables Ledger');
            expect(labels).not.toContain('Customer Ledger');
            expect(labels).not.toContain('Department Cost Ledger');
        }
    });

    it('splits Settings into sub-groups', () => {
        tenantService.tenant.set(baseTenant({ isSuperAdmin: true, enabledFeatureCodes: [] }));

        const admin = menu.model().find((g: any) => g.label === 'Administration')!;
        const settings = admin.items!.find((i: any) => i.label === 'Settings')!;
        expect(settings.items!.map((i: any) => i.label)).toEqual(['Sales & Fulfilment', 'Finance Setup', 'Documents', 'System', 'Integrations']);
    });

    // ── Moving an item kept its access ────────────────────────────────────

    it('still shows the Customer Ledger to a finance user with no report permission', () => {
        // The Reports group used to require REPORT_VIEW of everything in it; the ledger never needed that.
        authService.permissions = ['CUSTOMER_LEDGER_VIEW'];
        tenantService.tenant.set(baseTenant({ enabledFeatureCodes: ['MODULE_FINANCE'], permissions: authService.permissions }));

        expect(allLabels(menu.model())).toContain('Customer Ledger');
    });

    it('still hides the Customer Ledger when the org has no finance module', () => {
        authService.permissions = ['CUSTOMER_LEDGER_VIEW'];
        tenantService.tenant.set(baseTenant({ enabledFeatureCodes: ['MODULE_REPORTS'], permissions: authService.permissions }));

        expect(allLabels(menu.model())).not.toContain('Customer Ledger');
    });

    it('needs every listed feature for an item with several', () => {
        authService.permissions = ['PAYMENT_VIEW'];

        tenantService.tenant.set(baseTenant({ enabledFeatureCodes: ['MODULE_FINANCE'], permissions: authService.permissions }));
        expect(allLabels(menu.model())).not.toContain('Master Payables Ledger');

        tenantService.tenant.set(baseTenant({ enabledFeatureCodes: ['MODULE_FINANCE', 'FEATURE_MASTER_LEDGERS'], permissions: authService.permissions }));
        expect(allLabels(menu.model())).toContain('Master Payables Ledger');
    });

    it('applies an item\'s second permission gate as well as its first', () => {
        // Stock Movement kept the report permission it inherited from the Reports group.
        const features = ['MODULE_REPORTS', 'MODULE_MIR'];

        authService.permissions = ['INVENTORY_VIEW'];
        tenantService.tenant.set(baseTenant({ enabledFeatureCodes: features, permissions: authService.permissions }));
        expect(allLabels(menu.model())).not.toContain('Stock Movement / Ledger');

        authService.permissions = ['INVENTORY_VIEW', 'REPORT_EXPORT'];
        tenantService.tenant.set(baseTenant({ enabledFeatureCodes: features, permissions: authService.permissions }));
        expect(allLabels(menu.model())).toContain('Stock Movement / Ledger');
    });

    // ── A37: module sub-features, Settings › Modules, refresh ─────────────

    it('hides RFQs, picking, tracking and supplier returns when their sub-feature is switched off', () => {
        authService.permissions = ['RFQ_VIEW', 'PO_VIEW', 'PICKING', 'DELIVERY_VIEW', 'GOODS_RECEIVE'];
        const modulesOnly = ['MODULE_DEMAND', 'MODULE_LOGISTICS', 'MODULE_WAREHOUSE'];
        tenantService.tenant.set(baseTenant({ enabledFeatureCodes: modulesOnly, permissions: authService.permissions }));

        let labels = allLabels(menu.model());
        expect(labels).toContain('Purchase Orders');
        expect(labels).toContain('All Deliveries');
        expect(labels).toContain('Goods Receipts');
        expect(labels).toContain('Exception Queue');
        for (const hidden of ['Supplier Quotes (RFQ)', 'Picking', 'Supplier Returns']) {
            expect(labels).withContext(hidden).not.toContain(hidden);
        }

        tenantService.tenant.set(baseTenant({
            enabledFeatureCodes: [...modulesOnly, 'FEATURE_RFQ_MANAGEMENT', 'FEATURE_PICK_LISTS', 'FEATURE_SHIPMENT_TRACKING', 'FEATURE_PURCHASE_RETURNS'],
            permissions: authService.permissions
        }));
        labels = allLabels(menu.model());
        for (const shown of ['Supplier Quotes (RFQ)', 'Picking', 'Supplier Returns']) {
            expect(labels).withContext(shown).toContain(shown);
        }
    });

    it('keeps Customers under Sales when Demand is off, hiding only the Demand items', () => {
        authService.permissions = ['CUSTOMER_VIEW', 'SALE_INQUIRY_VIEW', 'SALE_QUOTATION_VIEW', 'SALE_ORDER_VIEW'];
        tenantService.tenant.set(baseTenant({ enabledFeatureCodes: ['MODULE_CUSTOMERS'], permissions: authService.permissions }));

        const labels = allLabels(menu.model());
        expect(labels).toContain('Sales');
        expect(labels).toContain('Customers');
        for (const hidden of ['Inquiries', 'Customer Quotations', 'Sale Orders', 'All Sale Orders']) {
            expect(labels).withContext(hidden).not.toContain(hidden);
        }
    });

    it('offers Settings › System › Modules to MODULES_VIEW holders only', () => {
        authService.permissions = ['MODULES_VIEW'];
        tenantService.tenant.set(baseTenant({ enabledFeatureCodes: [], permissions: authService.permissions }));
        expect(allLabels(menu.model())).toContain('Modules');

        authService.permissions = ['USER_MANAGE'];
        tenantService.tenant.set(baseTenant({ enabledFeatureCodes: ['SCREEN_USER_MANAGEMENT'], permissions: authService.permissions }));
        expect(allLabels(menu.model())).not.toContain('Modules');
    });

    it('re-evaluates when ModuleService reloads', () => {
        const version = signal(0);
        let calls = 0;
        const fakeModules = { version: () => { calls++; return version(); } };
        const withModules = new AppMenu(authService as any, tenantService as any, fakeModules as any);
        tenantService.tenant.set(baseTenant({ enabledFeatureCodes: [] }));
        withModules.model();
        const before = calls;
        version.set(1);
        withModules.model();
        expect(calls).toBeGreaterThan(before);
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
