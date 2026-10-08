import { Component, computed } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { MenuItem } from 'primeng/api';
import { AppMenuitem } from './app.menuitem';
import { AuthService } from '../../pages/service/auth.service';
import { TenantService } from '../../pages/service/tenant.service';

/**
 * MenuItem extended with:
 * - permRequired: role-permission gate — the user needs at least one of these JWT permissions.
 * - alsoRequired: a second permission gate that must pass too (any one of these). Used where an item
 *   keeps a group-level gate it used to inherit from a group it was moved out of.
 * - featureCode: org feature-toggle gate (MT-005) — GET /api/tenant/current's enabledFeatureCodes. A list
 *   means every listed feature must be enabled (an item moved out of a gated group keeps that group's code).
 * - superAdminOnly: visible only to Super Admin, bypassing both gates above entirely (Super Admin
 *   is outside org scope, per MT-005).
 *
 * Visibility (see filterItems): Super Admin sees every item unfiltered, plus the superAdminOnly
 * group; everyone else needs every feature enabled AND the permission gates passed. A group whose
 * children are all hidden is hidden too.
 *
 * The groups follow the work: sales → production → procurement → warehouse → fulfilment → finance,
 * then reports (in the same order) and administration.
 */
type NavItem = MenuItem & {
    permRequired?: string[];
    alsoRequired?: string[];
    featureCode?: string | string[];
    superAdminOnly?: boolean;
    items?: NavItem[];
};

/** The permissions the Reports group used to require of every report in it. */
const REPORT_PERMS = ['REPORT_VIEW', 'REPORT_EXPORT', 'AUDIT_LOG_VIEW'];

@Component({
    selector: 'app-menu',
    standalone: true,
    imports: [CommonModule, AppMenuitem, RouterModule],
    template: `<ul class="layout-menu">
        <ng-container *ngFor="let item of model(); let i = index">
            <li app-menuitem *ngIf="!item.separator" [item]="item" [index]="i" [root]="true"></li>
            <li *ngIf="item.separator" class="menu-separator"></li>
        </ng-container>
    </ul> `
})
export class AppMenu {
    constructor(private authService: AuthService, private tenantService: TenantService) {}

    // Static structure — the underlying nav tree never changes mid-session, only its filtered
    // visibility does (see `model`), so this is built once rather than in ngOnInit.
    private readonly fullMenu: NavItem[] = [

        // ── Home ──────────────────────────────────────────────────────────
        // What most people open first: the dashboard, their approvals, the KPIs.
        {
            label: 'Home',
            items: [
                {
                    label: 'Dashboard',
                    icon: 'pi pi-fw pi-home',
                    routerLink: ['/portal/dashboard']
                },
                // All authenticated users — the inbox shows only their own tasks.
                {
                    label: 'My Inbox',
                    icon: 'pi pi-fw pi-inbox',
                    routerLink: ['/portal/pages/workflow-inbox'],
                    featureCode: 'MODULE_WORKFLOW_ENGINE'
                },
                {
                    label: 'KPI Dashboard',
                    icon: 'pi pi-fw pi-chart-bar',
                    routerLink: ['/portal/pages/reports/kpi-dashboard'],
                    featureCode: 'MODULE_REPORTS',
                    permRequired: ['REPORT_VIEW', 'REPORT_EXPORT']
                }
            ]
        },

        // ── Sales (Addendum 29) ───────────────────────────────────────────
        // Order-to-cash starts here. Sale orders live in the Demand module, so the same feature gate as procurement.
        {
            label: 'Sales',
            featureCode: 'MODULE_DEMAND',
            items: [
                // A32-PB-08 — customer inquiries, first step of the pre-order chain; same code as the route.
                { label: 'Inquiries', icon: 'pi pi-fw pi-inbox',
                  routerLink: ['/portal/pages/sales/inquiries'],
                  featureCode: 'MODULE_DEMAND',
                  permRequired: ['SALE_INQUIRY_VIEW'] },
                // A32-PC-10 — seller-side quotations (not the procurement RFQ quotations); same code as the route.
                { label: 'Customer Quotations', icon: 'pi pi-fw pi-file-edit',
                  routerLink: ['/portal/pages/sales/quotations'],
                  permRequired: ['SALE_QUOTATION_VIEW'] },
                {
                    label: 'Sale Orders',
                    icon: 'pi pi-fw pi-shopping-bag',
                    items: [
                        { label: 'All Sale Orders', icon: 'pi pi-fw pi-list',
                          routerLink: ['/portal/pages/sales/orders'],
                          permRequired: ['SALE_ORDER_VIEW', 'SALE_ORDER_CREATE', 'SALE_ORDER_EDIT', 'SALE_ORDER_CONFIRM'] }
                    ]
                }
            ]
        },

        // ── Manufacturing (Addendum 30) ────────────────────────────────────
        // What is made to order or for stock, and what it needs bought. Its reports are under Reports.
        {
            label: 'Manufacturing',
            featureCode: 'MODULE_MANUFACTURING',
            items: [
                // A31-C4/A31-PB-09 — "Bills of Materials" removed from the nav; BOM management is
                // now inline on each product's own "Bill of Materials" tab.
                {
                    label: 'Production Orders',
                    icon: 'pi pi-fw pi-cog',
                    items: [
                        { label: 'New Order', icon: 'pi pi-fw pi-plus',
                          routerLink: ['/portal/pages/manufacturing/production-orders/new'],
                          permRequired: ['PROD_CREATE'] },
                        { label: 'All Orders', icon: 'pi pi-fw pi-list',
                          routerLink: ['/portal/pages/manufacturing/production-orders'],
                          permRequired: ['PROD_VIEW'] },
                        // A31 C9 — consolidated per-product view of what production needs bought.
                        { label: 'Purchase Required', icon: 'pi pi-fw pi-shopping-cart',
                          routerLink: ['/portal/pages/manufacturing/purchase-required'],
                          permRequired: ['SUPPLY_VIEW'] }
                    ]
                }
            ]
        },

        // ── Procurement ───────────────────────────────────────────────────
        // Requisitions/Quotations/POs are backed by the Demand module (MODULE_DEMAND) — the
        // ticket's own catalog names MODULE_PROCUREMENT as an unimplemented placeholder ("functionality
        // currently lives under Demand"), so gating on it would hide this group for every plan tier
        // that has MODULE_DEMAND on but MODULE_PROCUREMENT off (i.e. everyone except Enterprise).
        {
            label: 'Procurement',
            items: [
                {
                    label: 'Requisitions',
                    icon: 'pi pi-fw pi-file-edit',
                    featureCode: 'MODULE_DEMAND',
                    items: [
                        { label: 'New Requisition', icon: 'pi pi-fw pi-plus',
                          routerLink: ['/portal/pages/demand/requisitions/create'],
                          permRequired: ['REQUISITION_CREATE'] },
                        { label: 'All Requisitions', icon: 'pi pi-fw pi-list',
                          routerLink: ['/portal/pages/demand/requisitions'],
                          permRequired: ['REQUISITION_VIEW_OWN', 'REQUISITION_VIEW_ALL', 'REQUISITION_CREATE', 'REQUISITION_APPROVE'] }
                    ]
                },
                {
                    // Quotes suppliers send us. Not the Sales "Customer Quotations" (quotes we send).
                    label: 'Supplier Quotes (RFQ)',
                    icon: 'pi pi-fw pi-envelope',
                    featureCode: 'MODULE_DEMAND',
                    items: [
                        { label: 'New RFQ', icon: 'pi pi-fw pi-plus',
                          routerLink: ['/portal/pages/demand/quotations/create'],
                          permRequired: ['RFQ_CREATE', 'RFQ_MANAGE'] },
                        { label: 'All Supplier Quotes', icon: 'pi pi-fw pi-list',
                          routerLink: ['/portal/pages/demand/quotations'],
                          permRequired: ['RFQ_VIEW', 'RFQ_CREATE', 'RFQ_MANAGE'] }
                    ]
                },
                {
                    label: 'Purchase Orders',
                    icon: 'pi pi-fw pi-shopping-cart',
                    featureCode: 'MODULE_DEMAND',
                    items: [
                        { label: 'New Purchase Order', icon: 'pi pi-fw pi-plus',
                          routerLink: ['/portal/pages/demand/purchase-orders/create'],
                          permRequired: ['PO_CREATE'] },
                        { label: 'All Purchase Orders', icon: 'pi pi-fw pi-list',
                          routerLink: ['/portal/pages/demand/purchase-orders'],
                          permRequired: ['PO_VIEW', 'PO_CREATE', 'PO_EDIT', 'PO_APPROVE'] }
                    ]
                },
                {
                    label: 'Suppliers',
                    icon: 'pi pi-fw pi-building',
                    featureCode: 'MODULE_SUPPLIERS',
                    items: [
                        { label: 'New Supplier', icon: 'pi pi-fw pi-plus',
                          routerLink: ['/portal/pages/suppliers/supplier-create'],
                          permRequired: ['SUPPLIER_CREATE', 'SUPPLIER_MANAGE'] },
                        { label: 'All Suppliers', icon: 'pi pi-fw pi-list',
                          routerLink: ['/portal/pages/suppliers/supplier-list'],
                          permRequired: ['SUPPLIER_VIEW', 'SUPPLIER_CREATE', 'SUPPLIER_EDIT', 'SUPPLIER_MANAGE'] },
                        { label: 'Supplier Scorecard', icon: 'pi pi-fw pi-star',
                          routerLink: ['/portal/pages/suppliers/supplier-scorecard'],
                          featureCode: 'SCREEN_SUPPLIER_SCORECARD',
                          permRequired: ['SUPPLIER_VIEW', 'SUPPLIER_EDIT', 'SUPPLIER_MANAGE'] },
                        // Purchase prices per supplier — not the carriers' freight tariffs.
                        { label: 'Supplier Price Lists', icon: 'pi pi-fw pi-tags',
                          routerLink: ['/portal/pages/suppliers/rate-cards'],
                          permRequired: ['SUPPLIER_MANAGE'] },
                        { label: 'New Business Partner', icon: 'pi pi-fw pi-plus-circle',
                          routerLink: ['/portal/pages/suppliers/partner-create'],
                          permRequired: ['SUPPLIER_CREATE', 'SUPPLIER_MANAGE'] },
                        { label: 'Business Partners', icon: 'pi pi-fw pi-sitemap',
                          routerLink: ['/portal/pages/suppliers/partner-list'],
                          permRequired: ['SUPPLIER_VIEW', 'SUPPLIER_CREATE', 'SUPPLIER_EDIT', 'SUPPLIER_MANAGE'] }
                    ]
                }
            ]
        },

        // ── Warehouse & Inventory ─────────────────────────────────────────
        // Its ledger is under Reports → Inventory & Warehouse.
        {
            label: 'Warehouse & Inventory',
            items: [
                {
                    label: 'Products',
                    icon: 'pi pi-fw pi-box',
                    featureCode: 'MODULE_INVENTORY',
                    items: [
                        { label: 'New Product', icon: 'pi pi-fw pi-plus',
                          routerLink: ['/portal/pages/inventory/products/new'],
                          permRequired: ['STOCK_MANAGE'] },
                        { label: 'All Products', icon: 'pi pi-fw pi-list',
                          routerLink: ['/portal/pages/inventory/products'],
                          permRequired: ['INVENTORY_VIEW', 'STOCK_MANAGE'] },
                        { label: 'Categories', icon: 'pi pi-fw pi-tags',
                          routerLink: ['/portal/pages/inventory/categories'],
                          permRequired: ['INVENTORY_VIEW', 'STOCK_MANAGE'] },
                        { label: 'Sub-Categories', icon: 'pi pi-fw pi-list',
                          routerLink: ['/portal/pages/inventory/sub-categories'],
                          permRequired: ['INVENTORY_VIEW', 'STOCK_MANAGE'] }
                    ]
                },
                {
                    label: 'Allocations',
                    icon: 'pi pi-fw pi-share-alt',
                    featureCode: 'MODULE_INVENTORY',
                    items: [
                        { label: 'Allocation Dashboard', icon: 'pi pi-fw pi-chart-bar',
                          routerLink: ['/portal/pages/inventory/allocations'],
                          permRequired: ['ALLOCATION_VIEW'] }
                    ]
                },
                {
                    label: 'Warehouses',
                    icon: 'pi pi-fw pi-warehouse',
                    featureCode: 'MODULE_INVENTORY',
                    items: [
                        { label: 'New Warehouse', icon: 'pi pi-fw pi-plus',
                          routerLink: ['/portal/pages/inventory/warehouses'],
                          queryParams: { action: 'create' },
                          permRequired: ['STOCK_MANAGE'] },
                        { label: 'All Warehouses', icon: 'pi pi-fw pi-list',
                          routerLink: ['/portal/pages/inventory/warehouses'],
                          permRequired: ['INVENTORY_VIEW', 'STOCK_MANAGE'] }
                    ]
                },
                {
                    label: 'Goods Receipts',
                    icon: 'pi pi-fw pi-truck',
                    featureCode: 'MODULE_WAREHOUSE',
                    items: [
                        { label: 'New GRN', icon: 'pi pi-fw pi-plus',
                          routerLink: ['/portal/pages/warehouse/grn/create'],
                          permRequired: ['GOODS_RECEIVE'] },
                        { label: 'All GRNs', icon: 'pi pi-fw pi-list',
                          routerLink: ['/portal/pages/warehouse/grn'],
                          permRequired: ['GOODS_RECEIVE', 'GRN_APPROVE', 'GRN_FINANCE_APPROVE', 'GRN_QC_CONFIRM'] }
                    ]
                },
                {
                    label: 'Supplier Returns',
                    icon: 'pi pi-fw pi-reply',
                    featureCode: 'MODULE_WAREHOUSE',
                    items: [
                        { label: 'New Return Order', icon: 'pi pi-fw pi-plus',
                          routerLink: ['/portal/pages/warehouse/sro/create'],
                          permRequired: ['GOODS_RECEIVE', 'WAREHOUSE_TRANSFER'] },
                        { label: 'All Return Orders', icon: 'pi pi-fw pi-list',
                          routerLink: ['/portal/pages/warehouse/sro'],
                          permRequired: ['GOODS_RECEIVE', 'WAREHOUSE_TRANSFER'] }
                    ]
                },
                {
                    label: 'Stock Adjustments',
                    icon: 'pi pi-fw pi-arrows-v',
                    routerLink: ['/portal/pages/inventory/stock-adjustments'],
                    featureCode: 'MODULE_INVENTORY',
                    permRequired: ['STOCK_ADJUST']
                },
                {
                    label: 'Reorder Alerts',
                    icon: 'pi pi-fw pi-bell',
                    routerLink: ['/portal/pages/inventory/reorder-alerts'],
                    featureCode: 'MODULE_INVENTORY',
                    permRequired: ['REORDER_MANAGE', 'INVENTORY_VIEW']
                }
            ]
        },

        // ── Material Management ───────────────────────────────────────────
        // Issuing stock to projects and departments. Its registers and ledgers are under Reports → Material.
        {
            label: 'Material Management',
            featureCode: 'MODULE_MIR',
            items: [
                {
                    label: 'Projects',
                    icon: 'pi pi-fw pi-briefcase',
                    items: [
                        { label: 'New Project', icon: 'pi pi-fw pi-plus',
                          routerLink: ['/portal/pages/material/projects/new'],
                          permRequired: ['MATERIAL_MANAGE'] },
                        { label: 'All Projects', icon: 'pi pi-fw pi-list',
                          routerLink: ['/portal/pages/material/projects'],
                          permRequired: ['MATERIAL_VIEW', 'MATERIAL_MANAGE'] }
                    ]
                },
                {
                    label: 'Issue Requests (MIR)',
                    icon: 'pi pi-fw pi-file-export',
                    items: [
                        { label: 'New MIR', icon: 'pi pi-fw pi-plus',
                          routerLink: ['/portal/pages/material/mir/create'],
                          permRequired: ['MATERIAL_MANAGE'] },
                        { label: 'All MIRs', icon: 'pi pi-fw pi-list',
                          routerLink: ['/portal/pages/material/mir'],
                          permRequired: ['MATERIAL_VIEW', 'MATERIAL_MANAGE'] }
                    ]
                },
                {
                    label: 'Issue Vouchers (MIV)',
                    icon: 'pi pi-fw pi-receipt',
                    items: [
                        { label: 'All MIVs', icon: 'pi pi-fw pi-list',
                          routerLink: ['/portal/pages/material/miv'],
                          permRequired: ['MATERIAL_VIEW', 'MATERIAL_MANAGE'] }
                    ]
                },
                {
                    label: 'Material Returns',
                    icon: 'pi pi-fw pi-replay',
                    routerLink: ['/portal/pages/material/returns'],
                    permRequired: ['MATERIAL_VIEW', 'MATERIAL_MANAGE']
                },
                {
                    label: 'Wastage Records',
                    icon: 'pi pi-fw pi-trash',
                    routerLink: ['/portal/pages/material/wastage'],
                    permRequired: ['MATERIAL_VIEW', 'MATERIAL_MANAGE']
                }
            ]
        },

        // ── Logistics ─────────────────────────────────────────────────────
        {
            label: 'Logistics',
            featureCode: 'MODULE_LOGISTICS',
            items: [
                {
                    label: 'Deliveries',
                    icon: 'pi pi-fw pi-truck',
                    items: [
                        { label: 'New Delivery', icon: 'pi pi-fw pi-plus',
                          routerLink: ['/portal/pages/logistics/deliveries/create'],
                          permRequired: ['DELIVERY_CREATE'] },
                        { label: 'All Deliveries', icon: 'pi pi-fw pi-list',
                          routerLink: ['/portal/pages/logistics/deliveries'],
                          permRequired: ['DELIVERY_VIEW'] },
                        // The pack station is reached from a delivery — it needs one — so only
                        // picking earns a menu entry of its own: a picker starts from the queue.
                        { label: 'Picking', icon: 'pi pi-fw pi-list-check',
                          routerLink: ['/portal/pages/logistics/picking'],
                          permRequired: ['PICKING'] }
                    ]
                },
                // Tracking what is out on the road. Same codes as each route's guard.
                {
                    label: 'Tracking',
                    icon: 'pi pi-fw pi-map',
                    items: [
                        { label: 'Exception Queue', icon: 'pi pi-fw pi-exclamation-triangle',
                          routerLink: ['/portal/pages/logistics/exceptions'],
                          permRequired: ['DELIVERY_VIEW'] },
                        { label: 'Proof of Delivery', icon: 'pi pi-fw pi-check-circle',
                          routerLink: ['/portal/pages/logistics/proof-of-delivery'],
                          permRequired: ['DELIVERY_VIEW'] },
                        { label: 'Carrier Scorecard', icon: 'pi pi-fw pi-star',
                          routerLink: ['/portal/pages/logistics/carrier-scorecard'],
                          permRequired: ['DELIVERY_VIEW'] }
                    ]
                },
                {
                    label: 'Carriers',
                    icon: 'pi pi-fw pi-car',
                    items: [
                        { label: 'New Carrier', icon: 'pi pi-fw pi-plus',
                          routerLink: ['/portal/pages/logistics/carriers/create'],
                          permRequired: ['DELIVERY_TRACK'] },
                        { label: 'All Carriers', icon: 'pi pi-fw pi-list',
                          routerLink: ['/portal/pages/logistics/carriers'],
                          permRequired: ['DELIVERY_TRACK'] },
                        { label: 'Shipping Rules', icon: 'pi pi-fw pi-sliders-h',
                          routerLink: ['/portal/pages/logistics/shipping-rules'],
                          permRequired: ['SHIPPING_RULE_MANAGE'] }
                    ]
                },
                // What carriers bill us and the cash they hold for us. Same codes as each route's guard.
                {
                    label: 'Freight Settlement',
                    icon: 'pi pi-fw pi-money-bill',
                    items: [
                        { label: 'Carrier Invoices', icon: 'pi pi-fw pi-file',
                          routerLink: ['/portal/pages/logistics/settlement/carrier-invoices'],
                          permRequired: ['FREIGHT_INVOICE_VIEW'] },
                        { label: 'Match Queue', icon: 'pi pi-fw pi-link',
                          routerLink: ['/portal/pages/logistics/settlement/match-queue'],
                          permRequired: ['FREIGHT_INVOICE_VIEW'] },
                        { label: 'COD Reconciliation', icon: 'pi pi-fw pi-wallet',
                          routerLink: ['/portal/pages/logistics/settlement/cod'],
                          permRequired: ['FREIGHT_INVOICE_VIEW'] },
                        { label: 'Freight Accruals', icon: 'pi pi-fw pi-calculator',
                          routerLink: ['/portal/pages/logistics/settlement/freight-accruals'],
                          permRequired: ['SHIPMENT_RATE_VIEW'] }
                    ]
                },
                // The old shipments table, read-only. New movements are deliveries (above).
                { label: 'Shipments (Legacy)', icon: 'pi pi-fw pi-history',
                  routerLink: ['/portal/pages/logistics/shipments'],
                  permRequired: ['DELIVERY_TRACK'] }
            ]
        },

        // ── Finance ───────────────────────────────────────────────────────
        // The ledgers are under Reports → Finance.
        {
            label: 'Finance',
            featureCode: 'MODULE_FINANCE',
            items: [
                {
                    label: 'Invoices',
                    icon: 'pi pi-fw pi-file-invoice',
                    items: [
                        { label: 'New Invoice', icon: 'pi pi-fw pi-plus',
                          routerLink: ['/portal/pages/finance/invoices/create'],
                          permRequired: ['INVOICE_PROCESS'] },
                        { label: 'All Invoices', icon: 'pi pi-fw pi-list',
                          routerLink: ['/portal/pages/finance/invoices'],
                          permRequired: ['INVOICE_VIEW'] }
                    ]
                },
                {
                    label: 'Payments',
                    icon: 'pi pi-fw pi-credit-card',
                    items: [
                        { label: 'Record Payment', icon: 'pi pi-fw pi-plus',
                          routerLink: ['/portal/pages/finance/payments/create'],
                          permRequired: ['PAYMENT_PROCESS'] },
                        // Legacy payment history is reached from All Payments (and old invoices), not the menu.
                        { label: 'All Payments', icon: 'pi pi-fw pi-list',
                          routerLink: ['/portal/pages/finance/payments'],
                          permRequired: ['PAYMENT_VIEW', 'PAYMENT_PROCESS'] }
                    ]
                },

                // ── Receivables (Addendum 29): what customers are billed and what they pay ──
                {
                    label: 'Sales Invoices',
                    icon: 'pi pi-fw pi-file',
                    routerLink: ['/portal/pages/finance/sales-invoices'],
                    permRequired: ['SALES_INVOICE_VIEW', 'SALES_INVOICE_MANAGE']
                },
                {
                    label: 'Customer Payments',
                    icon: 'pi pi-fw pi-wallet',
                    items: [
                        { label: 'Record Payment', icon: 'pi pi-fw pi-plus',
                          routerLink: ['/portal/pages/finance/customer-payments/new'],
                          permRequired: ['CUSTOMER_PAYMENT_RECORD'] },
                        { label: 'All Customer Payments', icon: 'pi pi-fw pi-list',
                          routerLink: ['/portal/pages/finance/customer-payments'],
                          permRequired: ['CUSTOMER_PAYMENT_VIEW', 'CUSTOMER_PAYMENT_RECORD'] }
                    ]
                }
            ]
        },

        // ── Reports & Analytics ───────────────────────────────────────────
        // Every report and ledger, in the same order as the groups above. No group-level gate: each
        // item carries the feature and permission gates it had where it came from, so moving it here
        // changed nobody's access (Reports' own items keep MODULE_REPORTS; a ledger moved from Finance
        // keeps MODULE_FINANCE, and so on).
        {
            label: 'Reports & Analytics',
            items: [
                {
                    label: 'Sales',
                    icon: 'pi pi-fw pi-shopping-bag',
                    items: [
                        { label: 'Sales Reports', icon: 'pi pi-fw pi-chart-bar',
                          routerLink: ['/portal/pages/reports/sales-reports'],
                          featureCode: 'MODULE_REPORTS',
                          permRequired: ['REPORT_VIEW', 'REPORT_EXPORT'] }
                    ]
                },
                {
                    label: 'Manufacturing',
                    icon: 'pi pi-fw pi-cog',
                    items: [
                        { label: 'Manufacturing Reports', icon: 'pi pi-fw pi-chart-line',
                          routerLink: ['/portal/pages/reports/manufacturing'],
                          featureCode: 'MODULE_MANUFACTURING',
                          permRequired: ['PROD_LEDGER_VIEW'] }
                    ]
                },
                {
                    label: 'Procurement',
                    icon: 'pi pi-fw pi-shopping-cart',
                    items: [
                        { label: 'Supplier Performance', icon: 'pi pi-fw pi-star',
                          routerLink: ['/portal/pages/reports/supplier-performance'],
                          featureCode: 'MODULE_REPORTS',
                          permRequired: ['REPORT_VIEW', 'REPORT_EXPORT'] },
                        { label: 'PO Summary & Spend', icon: 'pi pi-fw pi-chart-pie',
                          routerLink: ['/portal/pages/reports/po-reports'],
                          featureCode: 'MODULE_REPORTS',
                          permRequired: ['REPORT_VIEW', 'REPORT_EXPORT'] },
                        // Everything waiting organization-wide — "My Inbox" (Home) is your own tasks.
                        { label: 'Approval Backlog (All)', icon: 'pi pi-fw pi-clock',
                          routerLink: ['/portal/pages/reports/pending-approvals'],
                          featureCode: 'MODULE_REPORTS',
                          permRequired: ['REPORT_VIEW', 'REPORT_EXPORT'] }
                    ]
                },
                {
                    label: 'Inventory & Warehouse',
                    icon: 'pi pi-fw pi-box',
                    items: [
                        { label: 'Stock Levels & Valuation', icon: 'pi pi-fw pi-database',
                          routerLink: ['/portal/pages/reports/inventory-reports'],
                          featureCode: 'MODULE_REPORTS',
                          permRequired: ['REPORT_VIEW', 'REPORT_EXPORT'] },
                        // Was under Reports → Material Reports, which needed MODULE_MIR and a report permission
                        // on top of its own; it keeps both.
                        { label: 'Stock Movement / Ledger', icon: 'pi pi-fw pi-database',
                          routerLink: ['/portal/pages/reports/stock-reports'],
                          featureCode: ['MODULE_REPORTS', 'MODULE_MIR'],
                          permRequired: ['INVENTORY_VIEW', 'REPORT_VIEW'],
                          alsoRequired: REPORT_PERMS },
                        { label: 'Master Product Ledger', icon: 'pi pi-fw pi-sitemap',
                          routerLink: ['/portal/pages/inventory/master-product-ledger'],
                          featureCode: 'FEATURE_MASTER_LEDGERS',
                          permRequired: ['INVENTORY_VIEW', 'STOCK_MANAGE'] },
                        { label: 'GRN Variance', icon: 'pi pi-fw pi-exchange',
                          routerLink: ['/portal/pages/reports/grn-variance'],
                          featureCode: 'MODULE_REPORTS',
                          permRequired: ['REPORT_VIEW', 'REPORT_EXPORT'] }
                    ]
                },
                {
                    label: 'Material',
                    icon: 'pi pi-fw pi-box',
                    items: [
                        { label: 'Issue Register', icon: 'pi pi-fw pi-file-edit',
                          routerLink: ['/portal/pages/reports/material-issue-register'],
                          featureCode: ['MODULE_REPORTS', 'MODULE_MIR'],
                          permRequired: ['REPORT_VIEW'] },
                        // Line by line per issue request — the totals are Consumption (Summary), below.
                        { label: 'Consumption (Detail)', icon: 'pi pi-fw pi-list',
                          routerLink: ['/portal/pages/material/consumption-register'],
                          featureCode: 'MODULE_MIR',
                          permRequired: ['MATERIAL_VIEW', 'MATERIAL_MANAGE'] },
                        { label: 'Consumption (Summary)', icon: 'pi pi-fw pi-chart-bar',
                          routerLink: ['/portal/pages/reports/material-consumption-reports'],
                          featureCode: ['MODULE_REPORTS', 'MODULE_MIR'],
                          permRequired: ['REPORT_VIEW'] },
                        { label: 'Returns / Wastage / Reserved', icon: 'pi pi-fw pi-exclamation-triangle',
                          routerLink: ['/portal/pages/reports/material-ops-reports'],
                          featureCode: ['MODULE_REPORTS', 'MODULE_MIR'],
                          permRequired: ['REPORT_VIEW'] },
                        { label: 'Batch / Serial Trace', icon: 'pi pi-fw pi-search',
                          routerLink: ['/portal/pages/material/batch-serial/trace'],
                          featureCode: 'MODULE_MIR',
                          permRequired: ['MATERIAL_VIEW', 'MATERIAL_MANAGE'] },
                        { label: 'Department Cost Ledger', icon: 'pi pi-fw pi-wallet',
                          routerLink: ['/portal/pages/material/cost-ledger/departments'],
                          featureCode: 'MODULE_MIR',
                          permRequired: ['MATERIAL_VIEW', 'MATERIAL_MANAGE'] }
                    ]
                },
                {
                    label: 'Finance',
                    icon: 'pi pi-fw pi-wallet',
                    items: [
                        { label: 'Invoice Aging & Payments', icon: 'pi pi-fw pi-file-invoice',
                          routerLink: ['/portal/pages/reports/finance-reports'],
                          featureCode: 'MODULE_REPORTS',
                          permRequired: ['REPORT_VIEW', 'REPORT_EXPORT'] },
                        { label: 'Master Payables Ledger', icon: 'pi pi-fw pi-book',
                          routerLink: ['/portal/pages/finance/master-ledger'],
                          featureCode: ['MODULE_FINANCE', 'FEATURE_MASTER_LEDGERS'],
                          permRequired: ['PAYMENT_VIEW', 'INVOICE_VIEW'] },
                        { label: 'Customer Ledger', icon: 'pi pi-fw pi-book',
                          routerLink: ['/portal/pages/finance/customer-ledger'],
                          featureCode: 'MODULE_FINANCE',
                          permRequired: ['CUSTOMER_LEDGER_VIEW'] },
                        // A35-E-06 — realized / unrealized exchange differences; same codes as the route (contract §7.1).
                        { label: 'Exchange Differences', icon: 'pi pi-fw pi-sort-alt',
                          routerLink: ['/portal/pages/finance/exchange-differences'],
                          featureCode: 'MODULE_FINANCE',
                          permRequired: ['INVOICE_VIEW', 'SALES_INVOICE_VIEW', 'CUSTOMER_PAYMENT_VIEW', 'PAYMENT_VIEW', 'EXCHANGE_REVALUATION_RUN'] }
                    ]
                },
                {
                    label: 'Audit & Compliance',
                    icon: 'pi pi-fw pi-shield',
                    items: [
                        { label: 'Audit Trail', icon: 'pi pi-fw pi-list',
                          routerLink: ['/portal/pages/reports/audit-trail'],
                          featureCode: ['MODULE_REPORTS', 'SCREEN_AUDIT_LOG'],
                          permRequired: ['AUDIT_LOG_VIEW', 'REPORT_VIEW'] },
                        { label: 'User Activity', icon: 'pi pi-fw pi-users',
                          routerLink: ['/portal/pages/reports/user-activity'],
                          featureCode: ['MODULE_REPORTS', 'SCREEN_AUDIT_LOG'],
                          permRequired: ['AUDIT_LOG_VIEW', 'REPORT_VIEW'] }
                    ]
                }
            ]
        },

        // ── Administration ────────────────────────────────────────────────
        {
            label: 'Administration',
            items: [
                {
                    label: 'Users & Access',
                    icon: 'pi pi-fw pi-users',
                    items: [
                        { label: 'Users', icon: 'pi pi-fw pi-user',
                          routerLink: ['/portal/pages/users'],
                          featureCode: 'SCREEN_USER_MANAGEMENT',
                          permRequired: ['USER_MANAGE'] },
                        { label: 'Roles', icon: 'pi pi-fw pi-shield',
                          routerLink: ['/portal/pages/admin/roles'],
                          featureCode: 'SCREEN_ROLE_MANAGEMENT',
                          permRequired: ['USER_MANAGE'] }
                    ]
                },
                {
                    label: 'Master Data',
                    icon: 'pi pi-fw pi-database',
                    featureCode: 'MODULE_MASTER_DATA',
                    items: [
                        { label: 'Countries', icon: 'pi pi-fw pi-globe',
                          routerLink: ['/portal/pages/countries'],
                          permRequired: ['SYSTEM_CONFIGURE', 'LOCATION_MANAGE'] },
                        { label: 'Cities', icon: 'pi pi-fw pi-map-marker',
                          routerLink: ['/portal/pages/cities/cities-list'],
                          permRequired: ['SYSTEM_CONFIGURE', 'LOCATION_MANAGE'] },
                        // The global ISO currency catalog. The organization's own currencies (formatting, active)
                        // are Settings → Finance Setup → Currencies (A35).
                        { label: 'Currency Catalog', icon: 'pi pi-fw pi-dollar',
                          routerLink: ['/portal/pages/currencies'],
                          permRequired: ['SYSTEM_CONFIGURE'] },
                        { label: 'Payment Terms', icon: 'pi pi-fw pi-calendar',
                          routerLink: ['/portal/pages/payment-terms'],
                          permRequired: ['SYSTEM_CONFIGURE'] }
                    ]
                },
                // Grouped by what they configure. The sub-groups carry no gates of their own.
                {
                    label: 'Settings',
                    icon: 'pi pi-fw pi-cog',
                    items: [
                        {
                            label: 'Sales & Fulfilment',
                            icon: 'pi pi-fw pi-shopping-bag',
                            items: [
                                { label: 'Sale Order Settings', icon: 'pi pi-fw pi-sliders-h',
                                  routerLink: ['/portal/pages/sale-order-settings'],
                                  featureCode: 'MODULE_DEMAND',
                                  permRequired: ['SALE_ORDER_CONFIG_READ'] },
                                // A33 — same codes as the route; api/fulfillment-routes needs MODULE_LOGISTICS.
                                { label: 'Fulfillment Routes', icon: 'pi pi-fw pi-directions',
                                  routerLink: ['/portal/pages/logistics/fulfillment-routes'],
                                  featureCode: 'MODULE_LOGISTICS',
                                  permRequired: ['FULFILLMENT_ROUTE_VIEW', 'FULFILLMENT_ROUTE_MANAGE'] },
                                // A34-PB-08 — same codes as the route; api/lead-time/* needs MODULE_INVENTORY.
                                { label: 'Lead Time Defaults', icon: 'pi pi-fw pi-clock',
                                  routerLink: ['/portal/pages/lead-time-defaults'],
                                  featureCode: 'MODULE_INVENTORY',
                                  permRequired: ['LEAD_TIME_DEFAULTS_MANAGE', 'INVENTORY_VIEW'] },
                                // A32 C5 — why inquiry/quotation lines are declined; same code as the route.
                                { label: 'Rejection Reasons', icon: 'pi pi-fw pi-ban',
                                  routerLink: ['/portal/pages/sales/rejection-reasons'],
                                  featureCode: 'MODULE_DEMAND',
                                  permRequired: ['SALE_REJECTION_REASON_MANAGE'] }
                            ]
                        },
                        {
                            label: 'Finance Setup',
                            icon: 'pi pi-fw pi-wallet',
                            items: [
                                // Any one of these, as the routes: FINANCE_SETUP_MANAGE maintains them; INVOICE_VIEW
                                // opens them read-only for finance viewers. The server gates every write on MANAGE.
                                { label: 'Tax Codes', icon: 'pi pi-fw pi-percentage',
                                  routerLink: ['/portal/pages/finance-setup/tax-codes'],
                                  featureCode: 'MODULE_FINANCE',
                                  permRequired: ['FINANCE_SETUP_MANAGE', 'INVOICE_VIEW'] },
                                // A35 — same codes as the routes (the *_VIEW codes every role holds would put these in
                                // everyone's menu; the MANAGE code, finance setup and finance viewers open them).
                                { label: 'Currencies', icon: 'pi pi-fw pi-dollar',
                                  routerLink: ['/portal/pages/finance-setup/currencies'],
                                  featureCode: 'MODULE_FINANCE',
                                  permRequired: ['CURRENCY_MANAGE', 'FINANCE_SETUP_MANAGE', 'INVOICE_VIEW'] },
                                { label: 'Exchange Rates', icon: 'pi pi-fw pi-arrow-right-arrow-left',
                                  routerLink: ['/portal/pages/finance-setup/exchange-rates'],
                                  featureCode: 'MODULE_FINANCE',
                                  permRequired: ['CURRENCY_RATE_MANAGE', 'FINANCE_SETUP_MANAGE', 'INVOICE_VIEW'] },
                                { label: 'Currency Configuration', icon: 'pi pi-fw pi-globe',
                                  routerLink: ['/portal/pages/finance-setup/currency-configuration'],
                                  featureCode: 'MODULE_FINANCE',
                                  permRequired: ['ORG_CURRENCY_SETTINGS_MANAGE', 'FINANCE_SETUP_MANAGE', 'INVOICE_VIEW'] }
                            ]
                        },
                        {
                            label: 'Documents',
                            icon: 'pi pi-fw pi-file',
                            items: [
                                { label: 'Purchase Order Templates', icon: 'pi pi-fw pi-file-edit',
                                  routerLink: ['/portal/pages/po-document-template'],
                                  featureCode: 'SCREEN_PO_DOCUMENT_TEMPLATE',
                                  permRequired: ['PO_TEMPLATE_MANAGE'] }
                            ]
                        },
                        {
                            label: 'System',
                            icon: 'pi pi-fw pi-server',
                            items: [
                                { label: 'Lookup Types', icon: 'pi pi-fw pi-tags',
                                  routerLink: ['/portal/pages/lookup-types/lookup-types-list'],
                                  featureCode: 'MODULE_MASTER_DATA',
                                  permRequired: ['SYSTEM_CONFIGURE'] },
                                { label: 'Lookup Values', icon: 'pi pi-fw pi-tag',
                                  routerLink: ['/portal/pages/lookup-values/lookup-values-list'],
                                  featureCode: 'MODULE_MASTER_DATA',
                                  permRequired: ['SYSTEM_CONFIGURE'] },
                                { label: 'Portal Settings', icon: 'pi pi-fw pi-link',
                                  routerLink: ['/portal/pages/portal-settings'],
                                  permRequired: ['SYSTEM_CONFIGURE'] }
                            ]
                        },
                        {
                            label: 'Integrations',
                            icon: 'pi pi-fw pi-sync',
                            items: [
                                { label: 'QuickBooks Integration', icon: 'pi pi-fw pi-sync',
                                  routerLink: ['/portal/pages/integrations/quickbooks'],
                                  featureCode: 'MODULE_INTEGRATION',
                                  permRequired: ['INTEGRATION_VIEW'] }
                            ]
                        }
                    ]
                },
                {
                    label: 'Workflow Engine',
                    icon: 'pi pi-fw pi-sitemap',
                    featureCode: 'SCREEN_WORKFLOW_CONFIG',
                    items: [
                        { label: 'Workflow Definitions', icon: 'pi pi-fw pi-list',
                          routerLink: ['/portal/pages/workflow-admin/workflows'],
                          permRequired: ['WORKFLOW_ADMIN'] },
                        { label: 'New Workflow', icon: 'pi pi-fw pi-plus',
                          routerLink: ['/portal/pages/workflow-admin/workflows/new'],
                          permRequired: ['WORKFLOW_ADMIN'] }
                    ]
                }
            ]
        },

        // ── System Administration (Super Admin only — outside org scope) ──
        // Only "Organizations" links to a real page today; the per-org Feature Configuration
        // screen is reached from there (organizations/:id/features), not via a standalone
        // "Features" page, so no separate "Features" item is added here yet.
        {
            label: 'System Administration',
            superAdminOnly: true,
            items: [
                { label: 'Organizations', icon: 'pi pi-fw pi-sitemap',
                  routerLink: ['/portal/pages/organizations'] }
            ]
        }
    ];

    readonly model = computed<NavItem[]>(() => {
        const tenant = this.tenantService.tenant();
        // Nothing renders until GET /api/tenant/current resolves — avoids a flash of the wrong
        // (fully-open or fully-closed) sidebar while it's in flight.
        if (!tenant) return [];

        return this.filterItems(this.fullMenu, {
            isSuperAdmin: tenant.isSuperAdmin
        });
    });

    private filterItems(items: NavItem[], ctx: { isSuperAdmin: boolean }): NavItem[] {
        const result: NavItem[] = [];
        for (const item of items) {
            if (item.separator) { result.push(item); continue; }

            // Super Admin is outside org scope: sees every non-superAdminOnly item unfiltered, plus
            // the superAdminOnly System Administration group. Everyone else never sees that group.
            if (item.superAdminOnly) {
                if (!ctx.isSuperAdmin) continue;
            } else if (!ctx.isSuperAdmin) {
                const features = item.featureCode === undefined ? [] : ([] as string[]).concat(item.featureCode);
                if (features.some(code => !this.tenantService.hasFeature(code))) continue;

                // Org Admin is a real permission holder like anyone else now (USER_MANAGE,
                // PO_TEMPLATE_MANAGE by default) — no special-case bypass. Menu visibility must
                // match what the route guard and backend actually allow, or the menu lies.
                if (item.permRequired?.length && !this.authService.hasAnyPermission(...item.permRequired)) {
                    continue;
                }
                if (item.alsoRequired?.length && !this.authService.hasAnyPermission(...item.alsoRequired)) {
                    continue;
                }
            }

            if (item.items?.length) {
                const filtered = this.filterItems(item.items, ctx);
                if (filtered.length === 0) continue;  // hide parent with no visible children
                result.push({ ...item, items: filtered });
            } else {
                result.push(item);
            }
        }
        return result;
    }
}
