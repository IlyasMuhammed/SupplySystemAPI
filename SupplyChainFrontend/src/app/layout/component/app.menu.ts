import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { MenuItem } from 'primeng/api';
import { AppMenuitem } from './app.menuitem';
import { AuthService } from '../../pages/service/auth.service';

/** MenuItem extended with an optional permission-code list for filtering. */
type NavItem = MenuItem & { permRequired?: string[]; items?: NavItem[] };

@Component({
    selector: 'app-menu',
    standalone: true,
    imports: [CommonModule, AppMenuitem, RouterModule],
    template: `<ul class="layout-menu">
        <ng-container *ngFor="let item of model; let i = index">
            <li app-menuitem *ngIf="!item.separator" [item]="item" [index]="i" [root]="true"></li>
            <li *ngIf="item.separator" class="menu-separator"></li>
        </ng-container>
    </ul> `
})
export class AppMenu implements OnInit {
    model: MenuItem[] = [];

    constructor(private authService: AuthService) {}

    ngOnInit() {
        const full: NavItem[] = [

            // ── Home (always visible) ─────────────────────────────────────────
            {
                label: 'Home',
                items: [
                    {
                        label: 'Dashboard',
                        icon: 'pi pi-fw pi-home',
                        routerLink: ['/portal/dashboard']
                    },
                ]
            },

            // ── Procurement ───────────────────────────────────────────────────
            {
                label: 'Procurement',
                items: [
                    {
                        label: 'Requisitions',
                        icon: 'pi pi-fw pi-file-edit',
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
                        label: 'Quotations (RFQ)',
                        icon: 'pi pi-fw pi-envelope',
                        items: [
                            { label: 'New Quotation', icon: 'pi pi-fw pi-plus',
                              routerLink: ['/portal/pages/demand/quotations/create'],
                              permRequired: ['RFQ_CREATE', 'RFQ_MANAGE'] },
                            { label: 'All Quotations', icon: 'pi pi-fw pi-list',
                              routerLink: ['/portal/pages/demand/quotations'],
                              permRequired: ['RFQ_VIEW', 'RFQ_CREATE', 'RFQ_MANAGE'] }
                        ]
                    },
                    {
                        label: 'Purchase Orders',
                        icon: 'pi pi-fw pi-shopping-cart',
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
                        items: [
                            { label: 'New Supplier', icon: 'pi pi-fw pi-plus',
                              routerLink: ['/portal/pages/suppliers/supplier-create'],
                              permRequired: ['SUPPLIER_CREATE', 'SUPPLIER_MANAGE'] },
                            { label: 'All Suppliers', icon: 'pi pi-fw pi-list',
                              routerLink: ['/portal/pages/suppliers/supplier-list'],
                              permRequired: ['SUPPLIER_VIEW', 'SUPPLIER_CREATE', 'SUPPLIER_EDIT', 'SUPPLIER_MANAGE'] },
                            { label: 'Supplier Scorecard', icon: 'pi pi-fw pi-star',
                              routerLink: ['/portal/pages/suppliers/supplier-scorecard'],
                              permRequired: ['SUPPLIER_VIEW', 'SUPPLIER_EDIT', 'SUPPLIER_MANAGE'] }
                        ]
                    }
                ]
            },

            // ── Warehouse & Inventory ─────────────────────────────────────────
            {
                label: 'Warehouse & Inventory',
                items: [
                    {
                        label: 'Products',
                        icon: 'pi pi-fw pi-box',
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
                        label: 'Warehouses',
                        icon: 'pi pi-fw pi-warehouse',
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
                        permRequired: ['STOCK_ADJUST']
                    },
                    {
                        label: 'Reorder Alerts',
                        icon: 'pi pi-fw pi-bell',
                        routerLink: ['/portal/pages/inventory/reorder-alerts'],
                        permRequired: ['REORDER_MANAGE', 'INVENTORY_VIEW']
                    }
                ]
            },

            // ── Logistics ─────────────────────────────────────────────────────
            {
                label: 'Logistics',
                items: [
                    {
                        label: 'Shipments',
                        icon: 'pi pi-fw pi-send',
                        items: [
                            { label: 'New Shipment', icon: 'pi pi-fw pi-plus',
                              routerLink: ['/portal/pages/logistics/shipments/create'],
                              permRequired: ['DELIVERY_TRACK'] },
                            { label: 'All Shipments', icon: 'pi pi-fw pi-list',
                              routerLink: ['/portal/pages/logistics/shipments'],
                              permRequired: ['DELIVERY_TRACK'] }
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
                              permRequired: ['DELIVERY_TRACK'] }
                        ]
                    }
                ]
            },

            // ── Finance ───────────────────────────────────────────────────────
            {
                label: 'Finance',
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
                              permRequired: ['INVOICE_VIEW', 'INVOICE_PROCESS'] }
                        ]
                    },
                    {
                        label: 'Payments',
                        icon: 'pi pi-fw pi-credit-card',
                        items: [
                            { label: 'Record Payment', icon: 'pi pi-fw pi-plus',
                              routerLink: ['/portal/pages/finance/payments/create'],
                              permRequired: ['PAYMENT_PROCESS'] },
                            { label: 'All Payments', icon: 'pi pi-fw pi-list',
                              routerLink: ['/portal/pages/finance/payments'],
                              permRequired: ['PAYMENT_VIEW', 'PAYMENT_PROCESS'] },
                            { label: 'Payment History (Legacy)', icon: 'pi pi-fw pi-history',
                              routerLink: ['/portal/pages/finance/payments-legacy'],
                              permRequired: ['PAYMENT_VIEW', 'PAYMENT_PROCESS'] }
                        ]
                    }
                ]
            },

            // ── Reports & Analytics ───────────────────────────────────────────
            {
                label: 'Reports & Analytics',
                permRequired: ['REPORT_VIEW', 'REPORT_EXPORT', 'AUDIT_LOG_VIEW'],
                items: [
                    {
                        label: 'KPI Dashboard',
                        icon: 'pi pi-fw pi-chart-bar',
                        routerLink: ['/portal/pages/reports/kpi-dashboard'],
                        permRequired: ['REPORT_VIEW', 'REPORT_EXPORT']
                    },
                    {
                        label: 'Procurement',
                        icon: 'pi pi-fw pi-shopping-cart',
                        items: [
                            { label: 'Supplier Performance', icon: 'pi pi-fw pi-star',
                              routerLink: ['/portal/pages/reports/supplier-performance'],
                              permRequired: ['REPORT_VIEW', 'REPORT_EXPORT'] },
                            { label: 'PO Summary & Spend', icon: 'pi pi-fw pi-chart-pie',
                              routerLink: ['/portal/pages/reports/po-reports'],
                              permRequired: ['REPORT_VIEW', 'REPORT_EXPORT'] },
                            { label: 'Pending Approvals', icon: 'pi pi-fw pi-clock',
                              routerLink: ['/portal/pages/reports/pending-approvals'],
                              permRequired: ['REPORT_VIEW', 'REPORT_EXPORT'] }
                        ]
                    },
                    {
                        label: 'Inventory & Warehouse',
                        icon: 'pi pi-fw pi-box',
                        items: [
                            { label: 'Stock Levels & Valuation', icon: 'pi pi-fw pi-database',
                              routerLink: ['/portal/pages/reports/inventory-reports'],
                              permRequired: ['REPORT_VIEW', 'REPORT_EXPORT'] },
                            { label: 'GRN Variance', icon: 'pi pi-fw pi-exchange',
                              routerLink: ['/portal/pages/reports/grn-variance'],
                              permRequired: ['REPORT_VIEW', 'REPORT_EXPORT'] },
                            { label: 'Shipment Tracker', icon: 'pi pi-fw pi-truck',
                              routerLink: ['/portal/pages/reports/shipment-tracker'],
                              permRequired: ['REPORT_VIEW', 'REPORT_EXPORT'] }
                        ]
                    },
                    {
                        label: 'Finance',
                        icon: 'pi pi-fw pi-wallet',
                        items: [
                            { label: 'Invoice Aging & Payments', icon: 'pi pi-fw pi-file-invoice',
                              routerLink: ['/portal/pages/reports/finance-reports'],
                              permRequired: ['REPORT_VIEW', 'REPORT_EXPORT'] }
                        ]
                    },
                    {
                        label: 'Audit & Compliance',
                        icon: 'pi pi-fw pi-shield',
                        items: [
                            { label: 'Audit Trail', icon: 'pi pi-fw pi-list',
                              routerLink: ['/portal/pages/reports/audit-trail'],
                              permRequired: ['AUDIT_LOG_VIEW', 'REPORT_VIEW'] },
                            { label: 'User Activity', icon: 'pi pi-fw pi-users',
                              routerLink: ['/portal/pages/reports/user-activity'],
                              permRequired: ['AUDIT_LOG_VIEW', 'REPORT_VIEW'] }
                        ]
                    },
                    {
                        label: 'Material Reports',
                        icon: 'pi pi-fw pi-box',
                        items: [
                            { label: 'Issue Register',      icon: 'pi pi-fw pi-file-edit',
                              routerLink: ['/portal/pages/reports/material-issue-register'],
                              permRequired: ['REPORT_VIEW'] },
                            { label: 'Consumption Report',  icon: 'pi pi-fw pi-chart-bar',
                              routerLink: ['/portal/pages/reports/material-consumption-reports'],
                              permRequired: ['REPORT_VIEW'] },
                            { label: 'Stock Movement / Ledger', icon: 'pi pi-fw pi-database',
                              routerLink: ['/portal/pages/reports/stock-reports'],
                              permRequired: ['INVENTORY_VIEW', 'REPORT_VIEW'] },
                            { label: 'Returns / Wastage / Reserved', icon: 'pi pi-fw pi-exclamation-triangle',
                              routerLink: ['/portal/pages/reports/material-ops-reports'],
                              permRequired: ['REPORT_VIEW'] }
                        ]
                    }
                ]
            },

            // ── Approvals (all authenticated users) ───────────────────────────
            {
                label: 'Approvals',
                items: [
                    {
                        label: 'My Inbox',
                        icon: 'pi pi-fw pi-inbox',
                        routerLink: ['/portal/pages/workflow-inbox']
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
                              permRequired: ['USER_MANAGE'] },
                            { label: 'Roles', icon: 'pi pi-fw pi-shield',
                              routerLink: ['/portal/pages/admin/roles'],
                              permRequired: ['SYSTEM_CONFIGURE'] }
                        ]
                    },
                    {
                        label: 'Master Data',
                        icon: 'pi pi-fw pi-database',
                        items: [
                            { label: 'Countries', icon: 'pi pi-fw pi-globe',
                              routerLink: ['/portal/pages/countries'],
                              permRequired: ['SYSTEM_CONFIGURE'] },
                            { label: 'Cities', icon: 'pi pi-fw pi-map-marker',
                              routerLink: ['/portal/pages/cities/cities-list'],
                              permRequired: ['SYSTEM_CONFIGURE'] },
                            { label: 'Currencies', icon: 'pi pi-fw pi-dollar',
                              routerLink: ['/portal/pages/currencies'],
                              permRequired: ['SYSTEM_CONFIGURE'] },
                            { label: 'Payment Terms', icon: 'pi pi-fw pi-calendar',
                              routerLink: ['/portal/pages/payment-terms'],
                              permRequired: ['SYSTEM_CONFIGURE'] }
                        ]
                    },
                    {
                        label: 'System',
                        icon: 'pi pi-fw pi-cog',
                        items: [
                            { label: 'Lookup Types', icon: 'pi pi-fw pi-tags',
                              routerLink: ['/portal/pages/lookup-types/lookup-types-list'],
                              permRequired: ['SYSTEM_CONFIGURE'] },
                            { label: 'Lookup Values', icon: 'pi pi-fw pi-tag',
                              routerLink: ['/portal/pages/lookup-values/lookup-values-list'],
                              permRequired: ['SYSTEM_CONFIGURE'] }
                        ]
                    },
                    {
                        label: 'Workflow Engine',
                        icon: 'pi pi-fw pi-sitemap',
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
            }

            ,

            // ── Material Management ───────────────────────────────────────────
            {
                label: 'Material Management',
                items: [
                    {
                        label: 'Projects',
                        icon: 'pi pi-fw pi-briefcase',
                        items: [
                            { label: 'New Project', icon: 'pi pi-fw pi-plus',
                              routerLink: ['/portal/pages/material/projects/new'] },
                            { label: 'All Projects', icon: 'pi pi-fw pi-list',
                              routerLink: ['/portal/pages/material/projects'] }
                        ]
                    },
                    {
                        label: 'Issue Requests (MIR)',
                        icon: 'pi pi-fw pi-file-export',
                        items: [
                            { label: 'New MIR', icon: 'pi pi-fw pi-plus',
                              routerLink: ['/portal/pages/material/mir/create'] },
                            { label: 'All MIRs', icon: 'pi pi-fw pi-list',
                              routerLink: ['/portal/pages/material/mir'] }
                        ]
                    },
                    {
                        label: 'Issue Vouchers (MIV)',
                        icon: 'pi pi-fw pi-receipt',
                        items: [
                            { label: 'All MIVs', icon: 'pi pi-fw pi-list',
                              routerLink: ['/portal/pages/material/miv'] }
                        ]
                    },
                    {
                        label: 'Batch / Serial Trace',
                        icon: 'pi pi-fw pi-search',
                        routerLink: ['/portal/pages/material/batch-serial/trace']
                    },
                    {
                        label: 'Department Cost Ledger',
                        icon: 'pi pi-fw pi-wallet',
                        routerLink: ['/portal/pages/material/cost-ledger/departments']
                    },
                    {
                        label: 'Material Returns',
                        icon: 'pi pi-fw pi-replay',
                        routerLink: ['/portal/pages/material/returns']
                    },
                    {
                        label: 'Wastage Records',
                        icon: 'pi pi-fw pi-trash',
                        routerLink: ['/portal/pages/material/wastage']
                    },
                    {
                        label: 'Consumption Register',
                        icon: 'pi pi-fw pi-chart-bar',
                        routerLink: ['/portal/pages/material/consumption-register']
                    }
                ]
            }

        ];

        this.model = this.filterItems(full);
    }

    private filterItems(items: NavItem[]): NavItem[] {
        const result: NavItem[] = [];
        for (const item of items) {
            if (item.separator) { result.push(item); continue; }

            // Check permission on this item
            if (item.permRequired?.length && !this.authService.hasAnyPermission(...item.permRequired)) {
                continue;
            }

            // Recursively filter children
            if (item.items?.length) {
                const filtered = this.filterItems(item.items);
                if (filtered.length === 0) continue;  // hide parent with no visible children
                result.push({ ...item, items: filtered });
            } else {
                result.push(item);
            }
        }
        return result;
    }
}
