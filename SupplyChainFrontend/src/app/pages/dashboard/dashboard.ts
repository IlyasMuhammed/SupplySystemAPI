import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule }                  from '@angular/common';
import { Router, RouterModule }          from '@angular/router';
import { forkJoin, Observable, of, Subscription } from 'rxjs';
import { catchError, debounceTime, map } from 'rxjs/operators';
import { ChartModule }                   from 'primeng/chart';
import { TagModule }                     from 'primeng/tag';
import { ButtonModule }                  from 'primeng/button';
import { SkeletonModule }                from 'primeng/skeleton';
import { DemandService, PoListItemModel }      from '../../services/demand.service';
import { WarehouseService, GrnListItemModel }  from '../../services/warehouse.service';
import { InventoryService, ReorderAlertModel } from '../../services/inventory.service';
import { SaleOrderService, SaleOrderModel }    from '../../services/sale-order.service';
import {
    CurrencyAmount, DashboardSummary, DashboardSummaryService, StatusCount, countOf, totalOf
} from '../../services/dashboard-summary.service';
import { AuthService }                         from '../service/auth.service';
import { LayoutService }                       from '../../layout/service/layout.service';

interface StatusBand {
    label: string;
    count: number;
    hex: string;
}

/** One hero card: a headline number, what it is, and the one line under it. */
interface HeroCard {
    key: string;
    label: string;
    value: string;
    sub: string;
    icon: string;
    tone: 'blue' | 'green' | 'purple' | 'orange' | 'slate' | 'teal' | 'rose' | 'indigo';
    route: string;
    alert?: boolean;
}

interface AttentionView { key: string; label: string; count: number; icon: string; route: string; danger: boolean; }

/** The queues the server can report on, as the page names and links them. Order = display order. */
const ATTENTION: Record<string, { label: string; icon: string; route: string; danger?: boolean }> = {
    SO_LATE:                   { label: 'Sale orders past their delivery date', icon: 'pi pi-clock',              route: '/portal/pages/sales/orders', danger: true },
    DELIVERY_LATE:             { label: 'Deliveries past their promised date',  icon: 'pi pi-truck',              route: '/portal/pages/logistics/deliveries', danger: true },
    PROD_LATE:                 { label: 'Production orders running late',       icon: 'pi pi-cog',                route: '/portal/pages/manufacturing/production-orders', danger: true },
    AR_OVERDUE:                { label: 'Customer invoices overdue',            icon: 'pi pi-wallet',             route: '/portal/pages/finance/sales-invoices', danger: true },
    AP_OVERDUE:                { label: 'Supplier invoices overdue',            icon: 'pi pi-credit-card',        route: '/portal/pages/finance/invoices', danger: true },
    DELIVERY_PENDING_APPROVAL: { label: 'Deliveries awaiting dispatch approval', icon: 'pi pi-check-square',      route: '/portal/pages/logistics/deliveries' },
    DELIVERY_ON_HOLD:          { label: 'Deliveries on hold',                   icon: 'pi pi-pause',              route: '/portal/pages/logistics/deliveries' },
    SO_DRAFT:                  { label: 'Draft sale orders to confirm',         icon: 'pi pi-file-edit',          route: '/portal/pages/sales/orders' },
    INQUIRY_NEW:               { label: 'Customer inquiries to review',         icon: 'pi pi-inbox',              route: '/portal/pages/sales/inquiries' },
    PROD_MATERIAL_PENDING:     { label: 'Production waiting for materials',     icon: 'pi pi-box',                route: '/portal/pages/manufacturing/purchase-required' },
    BOM_SUBMITTED:             { label: 'BOMs awaiting approval',               icon: 'pi pi-sitemap',            route: '/portal/pages/inventory/products' },
    PR_SUBMITTED:              { label: 'Requisitions awaiting approval',       icon: 'pi pi-file',               route: '/portal/pages/demand/requisitions' },
    PO_PENDING_APPROVAL:       { label: 'Purchase orders awaiting approval',    icon: 'pi pi-shopping-cart',      route: '/portal/pages/demand/purchase-orders' },
    GRN_PENDING_QC:            { label: 'GRNs awaiting QC',                     icon: 'pi pi-search',             route: '/portal/pages/warehouse/grn' },
    GRN_PENDING_FINANCE:       { label: 'GRNs awaiting finance',                icon: 'pi pi-money-bill',         route: '/portal/pages/warehouse/grn' },
    GRN_PENDING_APPROVAL:      { label: 'GRNs awaiting approval',               icon: 'pi pi-verified',           route: '/portal/pages/warehouse/grn' },
    MIR_PENDING_APPROVAL:      { label: 'Material requests awaiting approval',  icon: 'pi pi-list-check',         route: '/portal/pages/material/mir' },
    AP_VARIANCE:               { label: 'Supplier invoices with a match variance', icon: 'pi pi-exclamation-triangle', route: '/portal/pages/finance/invoices' }
};

/** Status → label and colour, per document type, in pipeline order. Statuses not listed are shown after, in grey. */
const BANDS = {
    po: [
        ['DRAFT', 'Draft', '#94a3b8'], ['PENDING_APPROVAL', 'Pending Approval', '#eab308'], ['APPROVED', 'Approved', '#3b82f6'],
        ['SENT', 'Sent', '#06b6d4'], ['PARTIALLY_RECEIVED', 'Partial Receipt', '#f97316'], ['RECEIVED', 'Received', '#22c55e'],
        ['PARTIALLY_INVOICED', 'Partially Invoiced', '#a855f7'], ['CLOSED', 'Closed', '#64748b'], ['REJECTED', 'Rejected', '#dc2626'],
        ['CANCELLED', 'Cancelled', '#ef4444']
    ],
    grn: [
        ['DRAFT', 'Draft', '#94a3b8'], ['PENDING_QC', 'Pending QC', '#ca8a04'], ['PENDING_FINANCE', 'Pending Finance', '#f97316'],
        ['PENDING_APPROVAL', 'Pending Approval', '#f59e0b'], ['APPROVED', 'Approved', '#22c55e'], ['REJECTED', 'Rejected', '#ef4444']
    ],
    delivery: [
        ['DRAFT', 'Draft', '#94a3b8'], ['RELEASED', 'Released', '#60a5fa'], ['PICKING', 'Picking', '#3b82f6'], ['PICKED', 'Picked', '#6366f1'],
        ['PACKED', 'Packed', '#8b5cf6'], ['STAGED', 'Staged', '#a855f7'], ['PENDING_APPROVAL', 'Pending Approval', '#eab308'],
        ['ON_HOLD', 'On Hold', '#f97316'], ['GOODS_ISSUED', 'Goods Issued', '#06b6d4'], ['IN_TRANSIT', 'In Transit', '#0891b2'],
        ['PARTIALLY_DELIVERED', 'Partially Delivered', '#14b8a6'], ['DELIVERED', 'Delivered', '#22c55e'], ['CLOSED', 'Closed', '#64748b'],
        ['SHORT_CLOSED', 'Short Closed', '#78716c'], ['CANCELLED', 'Cancelled', '#ef4444']
    ],
    production: [
        ['DRAFT', 'Draft', '#94a3b8'], ['PLANNED', 'Planned', '#60a5fa'], ['MATERIAL_PENDING', 'Material Pending', '#f97316'],
        ['READY', 'Ready', '#3b82f6'], ['IN_PROGRESS', 'In Progress', '#8b5cf6'], ['QUALITY_INSPECTION', 'Quality Inspection', '#eab308'],
        ['COMPLETED', 'Completed', '#22c55e'], ['CLOSED', 'Closed', '#64748b'], ['CANCELLED', 'Cancelled', '#ef4444']
    ]
} as const;

@Component({
    selector: 'app-dashboard',
    standalone: true,
    imports: [CommonModule, RouterModule, ChartModule, TagModule, ButtonModule, SkeletonModule],
    template: `
<div class="dash-page">

  <!-- ── Dark gradient header ──────────────────────────────────────────────── -->
  <div class="dash-hdr">
    <div class="dash-hdr-left">
      <div class="dash-icon-box"><i class="pi pi-th-large"></i></div>
      <div>
        <h1 class="dash-title">Supply Chain Dashboard</h1>
        <p class="dash-subtitle">{{ today | date:'EEEE, d MMMM yyyy' }}</p>
      </div>
    </div>
    <div class="dash-hdr-right">
      <div class="live-pill" *ngIf="!loading && !failed">
        <span class="live-dot"></span>Live
      </div>
      <p-button label="Refresh" icon="pi pi-refresh" size="small" [outlined]="true"
                severity="secondary" [loading]="loading" (onClick)="loadAll()" />
    </div>
  </div>

  <div class="dash-body">

    <!-- ── Hero metric strip ──────────────────────────────────────────────── -->
    <div class="hero-row">
      <ng-container *ngIf="loading">
        <div *ngFor="let _ of skeleton4" class="hero-card">
          <p-skeleton shape="circle" size="52px" />
          <div class="hc-body">
            <p-skeleton width="4rem" height="1.8rem" styleClass="mb-1" />
            <p-skeleton width="7rem" height="0.65rem" />
          </div>
        </div>
      </ng-container>

      <ng-container *ngIf="!loading">
        <div *ngFor="let c of heroCards" class="hero-card" [class.hc-alert]="c.alert"
             (click)="go(c.route)" [attr.data-testid]="'hero-' + c.key">
          <div class="hc-icon" [ngClass]="'ic-' + c.tone"><i [class]="c.icon"></i></div>
          <div class="hc-body">
            <div class="hc-value" [class.hv-alert]="c.alert">{{ c.value }}</div>
            <div class="hc-label">{{ c.label }}</div>
            <div class="hc-sub">{{ c.sub }}</div>
          </div>
        </div>
      </ng-container>
    </div>

    <div *ngIf="!loading && failed" class="panel mb-section load-error" data-testid="dashboard-error">
      <i class="pi pi-exclamation-circle"></i>
      The dashboard could not be loaded. Check your connection and press Refresh.
    </div>

    <!-- ── Needs attention ──────────────────────────────────────────────── -->
    <div class="panel mb-section" *ngIf="!loading && attention.length > 0" data-testid="attention-panel">
      <div class="panel-hdr">
        <span class="ph-accent reorder-accent"></span>
        <span class="ph-icon reorder-icon"><i class="pi pi-bell"></i></span>
        <span class="ph-title">Needs Attention</span>
        <span class="alert-badge">{{ attentionTotal }}</span>
      </div>
      <div class="attention-grid">
        <a *ngFor="let a of attention" class="attention-item" [class.att-danger]="a.danger"
           [routerLink]="a.route" [attr.data-testid]="'attention-' + a.key">
          <i [class]="a.icon"></i>
          <span class="att-label">{{ a.label }}</span>
          <span class="att-count">{{ a.count | number }}</span>
        </a>
      </div>
    </div>

    <!-- ── Sales: pipeline + recent orders ─────────────────────────────── -->
    <div class="dash-grid dash-grid-6-6 mb-section" *ngIf="!loading && summary?.sales">
      <div class="panel" data-testid="sales-pipeline">
        <div class="panel-hdr">
          <span class="ph-accent sales-accent"></span>
          <span class="ph-icon sales-icon"><i class="pi pi-chart-line"></i></span>
          <span class="ph-title">Sales Pipeline</span>
          <a [routerLink]="['/portal/pages/sales/orders']" class="ph-link">Sale orders</a>
        </div>
        <div class="funnel">
          <div *ngFor="let f of salesFunnel" class="funnel-row" (click)="go(f.route)">
            <span class="funnel-lbl">{{ f.label }}</span>
            <div class="pipeline-track">
              <div class="pipeline-fill" [style.width.%]="funnelMax > 0 ? (f.count / funnelMax * 100) : 0"
                   [style.background]="f.hex"></div>
            </div>
            <span class="pipeline-cnt" [style.color]="f.hex">{{ f.count }}</span>
          </div>
        </div>
        <div class="chart-footer" *ngIf="summary?.sales?.saleOrders">
          <div class="cstat">
            <span class="cstat-val">{{ summary?.sales?.ordersThisMonth ?? 0 | number }}</span>
            <span class="cstat-lbl">Orders this month</span>
          </div>
          <div class="cstat-div"></div>
          <div class="cstat">
            <span class="cstat-val" [class.text-danger]="(summary?.sales?.lateSaleOrders ?? 0) > 0">{{ summary?.sales?.lateSaleOrders ?? 0 | number }}</span>
            <span class="cstat-lbl">Past delivery date</span>
          </div>
          <div class="cstat-div"></div>
          <div class="cstat">
            <span class="cstat-val">{{ summary?.sales?.activeCustomers ?? 0 | number }}</span>
            <span class="cstat-lbl">Active customers</span>
          </div>
        </div>
      </div>

      <div class="panel" *ngIf="canSeeSaleOrders">
        <div class="panel-hdr">
          <span class="ph-accent sales-accent"></span>
          <span class="ph-icon sales-icon"><i class="pi pi-shopping-bag"></i></span>
          <span class="ph-title">Recent Sale Orders</span>
          <a [routerLink]="['/portal/pages/sales/orders']" class="ph-link">View all</a>
        </div>
        <div *ngIf="recentSaleOrders.length === 0" class="empty-state">
          <i class="pi pi-shopping-bag empty-icon"></i>
          <p class="empty-text">No sale orders yet</p>
        </div>
        <ul *ngIf="recentSaleOrders.length > 0" class="item-list">
          <li *ngFor="let so of recentSaleOrders; let last = last"
              class="item-row" [class.item-last]="last"
              (click)="go('/portal/pages/sales/orders/' + so.uuid)">
            <div class="item-main">
              <p class="item-title">{{ so.soNumber }}</p>
              <p class="item-sub">Ordered {{ so.orderDate | date:'d MMM yyyy' }}<span *ngIf="so.expectedDeliveryDate"> · due {{ so.expectedDeliveryDate | date:'d MMM' }}</span></p>
            </div>
            <div class="item-right">
              <p class="item-amount">{{ so.grandTotal | number:'1.0-0' }}</p>
              <p-tag [value]="formatStatus(so.status)" [severity]="getSoSeverity(so.status)" styleClass="text-xs" />
            </div>
          </li>
        </ul>
      </div>
    </div>

    <!-- ── Operations: deliveries + production ──────────────────────────── -->
    <div class="dash-grid dash-grid-6-6 mb-section" *ngIf="!loading && (summary?.deliveries || productionBands.length || bomBands.length)">
      <div class="panel" *ngIf="summary?.deliveries as d" data-testid="delivery-panel">
        <div class="panel-hdr">
          <span class="ph-accent grn-accent"></span>
          <span class="ph-icon grn-icon"><i class="pi pi-truck"></i></span>
          <span class="ph-title">Outbound Deliveries</span>
          <a [routerLink]="['/portal/pages/logistics/deliveries']" class="ph-link">View all</a>
        </div>
        <div *ngIf="deliveryBands.length === 0" class="empty-state empty-sm">
          <i class="pi pi-truck empty-icon"></i>
          <p class="empty-text">No deliveries yet</p>
        </div>
        <ng-container *ngTemplateOutlet="bandList; context: { $implicit: deliveryBands, total: deliveryTotal }"></ng-container>
        <div class="chart-footer">
          <div class="cstat">
            <span class="cstat-val">{{ d.shippedThisMonth | number }}</span>
            <span class="cstat-lbl">Shipped this month</span>
          </div>
          <div class="cstat-div"></div>
          <div class="cstat">
            <span class="cstat-val" [class.text-danger]="d.late > 0">{{ d.late | number }}</span>
            <span class="cstat-lbl">Late</span>
          </div>
          <div class="cstat-div"></div>
          <div class="cstat">
            <span class="cstat-val">{{ d.inboundExpected | number }}</span>
            <span class="cstat-lbl">Inbound expected</span>
          </div>
        </div>
      </div>

      <div class="panel" *ngIf="summary?.production as p" data-testid="production-panel">
        <div class="panel-hdr">
          <span class="ph-accent pipeline-accent"></span>
          <span class="ph-icon pipeline-icon"><i class="pi pi-cog"></i></span>
          <span class="ph-title">Production</span>
          <a [routerLink]="['/portal/pages/manufacturing/production-orders']" class="ph-link">View all</a>
        </div>
        <ng-container *ngIf="p.orders">
          <div *ngIf="productionBands.length === 0" class="empty-state empty-sm">
            <i class="pi pi-cog empty-icon"></i>
            <p class="empty-text">No production orders yet</p>
          </div>
          <ng-container *ngTemplateOutlet="bandList; context: { $implicit: productionBands, total: productionTotal }"></ng-container>
        </ng-container>
        <div class="chart-footer">
          <div class="cstat" *ngIf="p.orders">
            <span class="cstat-val" [class.text-danger]="(p.late ?? 0) > 0">{{ p.late ?? 0 | number }}</span>
            <span class="cstat-lbl">Late</span>
          </div>
          <div class="cstat-div" *ngIf="p.orders"></div>
          <div class="cstat" *ngIf="p.orders">
            <span class="cstat-val">{{ p.openMakeToOrder ?? 0 | number }}</span>
            <span class="cstat-lbl">Made to order (open)</span>
          </div>
          <div class="cstat-div" *ngIf="p.orders && p.boms"></div>
          <div class="cstat" *ngIf="p.boms">
            <span class="cstat-val">{{ activeBoms | number }}</span>
            <span class="cstat-lbl">Active BOMs</span>
          </div>
        </div>
      </div>
    </div>

    <!-- ── Money: receivables + payables, per currency ─────────────────── -->
    <div class="dash-grid dash-grid-6-6 mb-section" *ngIf="!loading && (summary?.receivables || summary?.payables)">
      <div class="panel" *ngIf="summary?.receivables as ar" data-testid="receivables-panel">
        <ng-container *ngTemplateOutlet="moneyPanel; context: { $implicit: ar, title: 'Receivables', icon: 'pi pi-wallet', route: '/portal/pages/finance/sales-invoices', who: 'customers owe' }"></ng-container>
      </div>
      <div class="panel" *ngIf="summary?.payables as ap" data-testid="payables-panel">
        <ng-container *ngTemplateOutlet="moneyPanel; context: { $implicit: ap, title: 'Payables', icon: 'pi pi-credit-card', route: '/portal/pages/finance/invoices', who: 'we owe suppliers' }"></ng-container>
      </div>
    </div>

    <!-- ── Procurement: PO donut + recent POs ───────────────────────────── -->
    <div class="dash-grid dash-grid-5-7 mb-section" *ngIf="!loading && summary?.procurement">

      <div class="panel">
        <div class="panel-hdr">
          <span class="ph-accent po-accent"></span>
          <span class="ph-icon po-icon"><i class="pi pi-chart-pie"></i></span>
          <span class="ph-title">PO Status Overview</span>
          <a [routerLink]="['/portal/pages/demand/purchase-orders']" class="ph-link">View all</a>
        </div>

        <ng-container *ngIf="!poChartData">
          <div class="empty-state">
            <i class="pi pi-chart-pie empty-icon"></i>
            <p class="empty-text">No purchase orders yet</p>
            <a [routerLink]="['/portal/pages/demand/purchase-orders/create']" class="ph-link">Create first PO</a>
          </div>
        </ng-container>

        <div *ngIf="poChartData" class="chart-donut-wrap">
          <p-chart type="doughnut" [data]="poChartData" [options]="poChartOptions"
                   [style]="{'width':'100%','height':'220px'}" />
        </div>

        <div *ngIf="poBands.length > 0" class="chart-legend">
          <div *ngFor="let b of poBands" class="legend-item">
            <span class="legend-dot" [style.background]="b.hex"></span>
            <span class="legend-label">{{ b.label }}</span>
            <span class="legend-count" [style.color]="b.hex">{{ b.count }}</span>
          </div>
        </div>

        <div class="chart-footer">
          <div class="cstat">
            <span class="cstat-val">{{ totalPos | number }}</span>
            <span class="cstat-lbl">Total POs</span>
          </div>
          <div class="cstat-div"></div>
          <div class="cstat">
            <span class="cstat-val">{{ totalPrs | number }}</span>
            <span class="cstat-lbl">Requisitions</span>
          </div>
          <div class="cstat-div"></div>
          <div class="cstat" *ngIf="summary?.procurement?.totalSuppliers != null">
            <span class="cstat-val">{{ summary?.procurement?.activeSuppliers ?? 0 | number }}</span>
            <span class="cstat-lbl">Active suppliers</span>
          </div>
        </div>
      </div>

      <div class="panel">
        <div class="panel-hdr">
          <span class="ph-accent po-accent"></span>
          <span class="ph-icon po-icon"><i class="pi pi-shopping-cart"></i></span>
          <span class="ph-title">Recent Purchase Orders</span>
          <a [routerLink]="['/portal/pages/demand/purchase-orders']" class="ph-link">View all</a>
        </div>

        <div *ngIf="recentPos.length === 0" class="empty-state">
          <i class="pi pi-shopping-cart empty-icon"></i>
          <p class="empty-text">No purchase orders yet</p>
        </div>

        <ul *ngIf="recentPos.length > 0" class="item-list">
          <li *ngFor="let po of recentPos; let last = last"
              class="item-row" [class.item-last]="last"
              (click)="go('/portal/pages/demand/purchase-orders/' + po.uuid)">
            <div class="item-main">
              <p class="item-title">{{ po.poNumber }}</p>
              <p class="item-sub">{{ po.supplierName }}</p>
            </div>
            <div class="item-right">
              <p class="item-amount">PKR {{ po.totalAmount | number:'1.0-0' }}</p>
              <p-tag [value]="formatStatus(po.status)" [severity]="getPoSeverity(po.status)" styleClass="text-xs" />
            </div>
          </li>
        </ul>
      </div>

    </div>

    <!-- ── Receiving: recent GRNs + pipeline + reorder ──────────────────── -->
    <div class="dash-grid dash-grid-6-6" *ngIf="!loading && (summary?.receiving || reorderAlerts.length > 0)">

      <div class="panel" *ngIf="summary?.receiving">
        <div class="panel-hdr">
          <span class="ph-accent grn-accent"></span>
          <span class="ph-icon grn-icon"><i class="pi pi-download"></i></span>
          <span class="ph-title">Recent GRN Records</span>
          <a [routerLink]="['/portal/pages/warehouse/grn']" class="ph-link">View all</a>
        </div>

        <div *ngIf="recentGrns.length === 0" class="empty-state">
          <i class="pi pi-download empty-icon"></i>
          <p class="empty-text">No goods receipts yet</p>
        </div>

        <ul *ngIf="recentGrns.length > 0" class="item-list">
          <li *ngFor="let grn of recentGrns; let last = last"
              class="item-row" [class.item-last]="last"
              (click)="go('/portal/pages/warehouse/grn/' + grn.uuid)">
            <div class="item-main">
              <p class="item-title">{{ grn.grnNumber }}</p>
              <p class="item-sub">{{ grn.supplierName }}</p>
            </div>
            <div class="item-right">
              <p class="item-date">{{ grn.receivedAt | date:'d MMM yyyy' }}</p>
              <p-tag [value]="formatStatus(grn.status)" [severity]="getGrnSeverity(grn.status)" styleClass="text-xs" />
            </div>
          </li>
        </ul>
      </div>

      <div class="right-col">
        <div class="panel" *ngIf="summary?.receiving">
          <div class="panel-hdr">
            <span class="ph-accent pipeline-accent"></span>
            <span class="ph-icon pipeline-icon"><i class="pi pi-sliders-h"></i></span>
            <span class="ph-title">GRN Pipeline</span>
          </div>
          <div *ngIf="grnBands.length === 0" class="empty-state empty-sm">
            <i class="pi pi-chart-bar empty-icon"></i>
            <p class="empty-text">No GRN data yet</p>
          </div>
          <ng-container *ngTemplateOutlet="bandList; context: { $implicit: grnBands, total: totalGrns }"></ng-container>
        </div>

        <div class="panel" *ngIf="reorderAlerts.length > 0">
          <div class="panel-hdr">
            <span class="ph-accent reorder-accent"></span>
            <span class="ph-icon reorder-icon"><i class="pi pi-exclamation-triangle"></i></span>
            <span class="ph-title">Reorder Alerts</span>
            <span class="alert-badge">{{ reorderAlerts.length }}</span>
          </div>
          <ul class="item-list">
            <li *ngFor="let a of reorderAlerts.slice(0,4); let last = last"
                class="item-row" [class.item-last]="last"
                (click)="go('/portal/pages/inventory/products/' + a.productId)">
              <div class="item-main">
                <p class="item-title">{{ a.productName }}</p>
                <p class="item-sub">{{ a.warehouseName }}</p>
              </div>
              <div class="item-right">
                <p class="item-alert-val">{{ a.qtyOnHand | number:'1.0-2' }}</p>
                <p class="item-date">min {{ a.reorderPoint | number:'1.0-2' }}</p>
              </div>
            </li>
          </ul>
          <a *ngIf="reorderAlerts.length > 4"
             [routerLink]="['/portal/pages/inventory/reorder-alerts']"
             class="link-block">
            View all {{ reorderAlerts.length }} alerts →
          </a>
        </div>
      </div>
    </div>

  </div>
</div>

<!-- A status pipeline as bars, widths relative to the list's own total. -->
<ng-template #bandList let-bands let-total="total">
  <div *ngIf="bands.length > 0" class="pipeline-list">
    <div *ngFor="let band of bands" class="pipeline-row">
      <span class="pipeline-lbl">{{ band.label }}</span>
      <div class="pipeline-track">
        <div class="pipeline-fill" [style.width.%]="total > 0 ? (band.count / total * 100) : 0"
             [style.background]="band.hex"></div>
      </div>
      <span class="pipeline-cnt" [style.color]="band.hex">{{ band.count }}</span>
    </div>
  </div>
</ng-template>

<!-- Receivables or payables: outstanding and overdue per currency — never summed across currencies. -->
<ng-template #moneyPanel let-m let-title="title" let-icon="icon" let-route="route" let-who="who">
  <div class="panel-hdr">
    <span class="ph-accent money-accent"></span>
    <span class="ph-icon money-icon"><i [class]="icon"></i></span>
    <span class="ph-title">{{ title }}</span>
    <a [routerLink]="route" class="ph-link">View all</a>
  </div>
  <div *ngIf="m.outstanding.length === 0" class="empty-state empty-sm">
    <i class="pi pi-check-circle empty-icon"></i>
    <p class="empty-text">Nothing outstanding</p>
  </div>
  <table *ngIf="m.outstanding.length > 0" class="money-table">
    <thead>
      <tr><th>Currency</th><th class="num">Outstanding</th><th class="num">Overdue</th></tr>
    </thead>
    <tbody>
      <tr *ngFor="let row of m.outstanding">
        <td class="cur">{{ row.currency }}</td>
        <td class="num">{{ row.amount | number:'1.0-0' }} <span class="muted-sm">({{ row.count }})</span></td>
        <td class="num" [class.text-danger]="overdueFor(m.overdue, row.currency) > 0">
          {{ overdueFor(m.overdue, row.currency) | number:'1.0-0' }}
        </td>
      </tr>
    </tbody>
  </table>
  <p class="money-note" *ngIf="m.outstanding.length > 0">What {{ who }}, by currency. Numbers in brackets are invoice counts.</p>
</ng-template>
    `,
    styles: [`
        :host { display: block; }

        /* ── Page shell ──────────────────────────────────────────────────────── */
        .dash-page {
            min-height: 100vh;
            background: linear-gradient(135deg, #faf7f3 0%, #ede6db 100%);
            display: flex;
            flex-direction: column;
        }

        /* ── Dark gradient header ────────────────────────────────────────────── */
        .dash-hdr {
            background: linear-gradient(135deg, #0f172a 0%, #1e3a5f 60%, #0c2340 100%);
            padding: 1.75rem 2.5rem 2.75rem;
            display: flex;
            justify-content: space-between;
            align-items: center;
            flex-wrap: wrap;
            gap: 1rem;
        }
        .dash-hdr-left { display: flex; align-items: center; gap: 1.1rem; }
        .dash-hdr-right { display: flex; align-items: center; gap: 1rem; }

        .dash-icon-box {
            width: 52px; height: 52px; border-radius: 14px;
            background: rgba(255,255,255,.13); backdrop-filter: blur(6px);
            display: flex; align-items: center; justify-content: center;
        }
        .dash-icon-box i { font-size: 1.5rem; color: #93c5fd; }

        .dash-title    { font-size: 1.7rem; font-weight: 800; color: #f8fafc; margin: 0 0 .2rem; letter-spacing: -.02em; }
        .dash-subtitle { font-size: .88rem; color: #94a3b8; margin: 0; }

        .live-pill {
            display: flex; align-items: center; gap: .4rem;
            background: rgba(52,211,153,.15); border: 1px solid rgba(52,211,153,.35);
            border-radius: 20px; padding: .3rem .75rem;
            font-size: .75rem; font-weight: 700; color: #34d399; letter-spacing: .05em;
        }
        .live-dot {
            width: 7px; height: 7px; border-radius: 50%; background: #34d399;
            animation: blink 1.6s ease-in-out infinite;
        }
        @keyframes blink {
            0%, 100% { opacity: 1; transform: scale(1); }
            50%       { opacity: .4; transform: scale(1.3); }
        }

        /* ── Body ───────────────────────────────────────────────────────────── */
        .dash-body { padding: 0 2.5rem 3rem; flex: 1; }

        /* ── Hero metric strip ───────────────────────────────────────────────── */
        .hero-row {
            display: grid;
            grid-template-columns: repeat(auto-fill, minmax(240px, 1fr));
            gap: 1.25rem;
            margin-top: -1.5rem;
            margin-bottom: 1.75rem;
            position: relative;
            z-index: 10;
        }
        .hero-card {
            background: #fff;
            border-radius: 18px;
            padding: 1.4rem 1.5rem;
            box-shadow: 0 12px 30px rgba(0,0,0,.13);
            display: flex;
            align-items: center;
            gap: 1rem;
            cursor: pointer;
            transition: transform .2s ease, box-shadow .2s ease;
            border: 1px solid transparent;
        }
        .hero-card:hover { transform: translateY(-3px); box-shadow: 0 18px 40px rgba(0,0,0,.18); }
        .hero-card.hc-alert { border-color: #fed7aa; }

        .hc-icon {
            width: 52px; height: 52px; border-radius: 14px; flex-shrink: 0;
            display: flex; align-items: center; justify-content: center;
        }
        .hc-icon i { font-size: 1.4rem; color: #fff; }
        .hc-icon.ic-blue   { background: linear-gradient(135deg, #60a5fa, #1d4ed8); }
        .hc-icon.ic-green  { background: linear-gradient(135deg, #4ade80, #15803d); }
        .hc-icon.ic-purple { background: linear-gradient(135deg, #c084fc, #7c3aed); }
        .hc-icon.ic-orange { background: linear-gradient(135deg, #fb923c, #c2410c); }
        .hc-icon.ic-slate  { background: linear-gradient(135deg, #94a3b8, #475569); }
        .hc-icon.ic-teal   { background: linear-gradient(135deg, #2dd4bf, #0f766e); }
        .hc-icon.ic-rose   { background: linear-gradient(135deg, #fb7185, #be123c); }
        .hc-icon.ic-indigo { background: linear-gradient(135deg, #818cf8, #4338ca); }

        .hc-body { flex: 1; min-width: 0; }
        .hc-value { font-size: 1.85rem; font-weight: 800; color: #111827; line-height: 1; letter-spacing: -.03em; margin-bottom: .25rem; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .hc-value.hv-alert { color: #f97316; }
        .hc-label { font-size: .75rem; font-weight: 700; color: #6b7280; text-transform: uppercase; letter-spacing: .05em; margin-bottom: .2rem; }
        .hc-sub   { font-size: .76rem; color: #9ca3af; }

        /* ── Grid layouts ────────────────────────────────────────────────────── */
        .dash-grid       { display: grid; gap: 1.25rem; }
        .dash-grid-5-7   { grid-template-columns: 5fr 7fr; }
        .dash-grid-6-6   { grid-template-columns: 1fr 1fr; }
        .mb-section      { margin-bottom: 1.25rem; }

        /* ── Panel ───────────────────────────────────────────────────────────── */
        .panel {
            background: #fff;
            border-radius: 18px;
            padding: 1.5rem 1.75rem;
            box-shadow: 0 4px 16px rgba(0,0,0,.07);
        }
        .panel-hdr {
            display: flex;
            align-items: center;
            gap: .6rem;
            margin-bottom: 1.25rem;
        }
        .ph-accent { display: block; width: 4px; height: 22px; border-radius: 2px; flex-shrink: 0; }
        .ph-icon   { width: 32px; height: 32px; border-radius: 8px; display: flex; align-items: center; justify-content: center; flex-shrink: 0; }
        .ph-icon i { font-size: .9rem; }
        .ph-title  { font-size: .82rem; font-weight: 700; color: #1e293b; text-transform: uppercase; letter-spacing: .04em; flex: 1; }
        .ph-link   { font-size: .78rem; font-weight: 600; color: #3b82f6; text-decoration: none; white-space: nowrap; }
        .ph-link:hover { text-decoration: underline; }

        /* Accent colour sets */
        .po-accent       { background: linear-gradient(180deg, #3b82f6, #0891b2); }
        .po-icon         { background: #eff6ff; }
        .po-icon i       { color: #3b82f6; }

        .grn-accent      { background: linear-gradient(180deg, #10b981, #06b6d4); }
        .grn-icon        { background: #ecfdf5; }
        .grn-icon i      { color: #10b981; }

        .pipeline-accent { background: linear-gradient(180deg, #f59e0b, #f97316); }
        .pipeline-icon   { background: #fffbeb; }
        .pipeline-icon i { color: #f59e0b; }

        .reorder-accent  { background: linear-gradient(180deg, #ef4444, #f97316); }
        .reorder-icon    { background: #fef2f2; }
        .reorder-icon i  { color: #ef4444; }

        .sales-accent    { background: linear-gradient(180deg, #8b5cf6, #6366f1); }
        .sales-icon      { background: #f5f3ff; }
        .sales-icon i    { color: #8b5cf6; }

        .money-accent    { background: linear-gradient(180deg, #14b8a6, #0d9488); }
        .money-icon      { background: #f0fdfa; }
        .money-icon i    { color: #0d9488; }

        .text-danger { color: #dc2626 !important; }

        /* ── Needs attention ─────────────────────────────────────────────────── */
        .attention-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(260px, 1fr)); gap: .6rem; }
        .attention-item {
            display: flex; align-items: center; gap: .65rem;
            padding: .65rem .85rem; border-radius: 10px;
            background: #fffbeb; border: 1px solid #fde68a;
            text-decoration: none; color: #78350f; transition: background .12s ease;
        }
        .attention-item:hover { background: #fef3c7; }
        .attention-item.att-danger { background: #fef2f2; border-color: #fecaca; color: #7f1d1d; }
        .attention-item.att-danger:hover { background: #fee2e2; }
        .attention-item i { font-size: .95rem; }
        .att-label { flex: 1; font-size: .8rem; font-weight: 600; }
        .att-count { font-size: .95rem; font-weight: 800; }

        .load-error { display: flex; align-items: center; gap: .6rem; color: #b91c1c; font-weight: 600; font-size: .875rem; }

        /* ── Funnel ─────────────────────────────────────────────────────────── */
        .funnel { display: flex; flex-direction: column; gap: .85rem; }
        .funnel-row { display: flex; align-items: center; gap: .75rem; cursor: pointer; }
        .funnel-lbl { font-size: .73rem; font-weight: 600; color: #64748b; flex-shrink: 0; width: 9.5rem; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }

        /* ── Money ───────────────────────────────────────────────────────────── */
        .money-table { width: 100%; border-collapse: collapse; font-size: .85rem; }
        .money-table th { text-align: left; font-size: .7rem; text-transform: uppercase; letter-spacing: .04em; color: #94a3b8; font-weight: 700; padding: 0 0 .5rem; }
        .money-table td { padding: .5rem 0; border-top: 1px solid #f1f5f9; color: #1e293b; }
        .money-table .num { text-align: right; font-variant-numeric: tabular-nums; }
        .money-table .cur { font-weight: 700; }
        .muted-sm { color: #94a3b8; font-size: .72rem; }
        .money-note { font-size: .72rem; color: #94a3b8; margin: .75rem 0 0; }

        /* ── Chart ───────────────────────────────────────────────────────────── */
        .chart-donut-wrap { width: 100%; }

        .chart-legend {
            display: flex; flex-wrap: wrap; gap: .4rem .85rem;
            margin: .75rem 0 .25rem; padding-top: .75rem;
            border-top: 1px solid #f1f5f9;
        }
        .legend-item  { display: flex; align-items: center; gap: .35rem; }
        .legend-dot   { width: 9px; height: 9px; border-radius: 50%; flex-shrink: 0; }
        .legend-label { font-size: .72rem; color: #374151; font-weight: 600; }
        .legend-count { font-size: .72rem; font-weight: 700; }
        .chart-footer     { display: flex; justify-content: space-around; text-align: center; padding-top: 1rem; margin-top: 1rem; border-top: 1px solid #f1f5f9; }
        .cstat-val  { display: block; font-size: 1.25rem; font-weight: 800; color: #111827; }
        .cstat-lbl  { display: block; font-size: .7rem; color: #9ca3af; margin-top: .15rem; }
        .cstat-div  { width: 1px; background: #f1f5f9; }

        /* ── Item list ───────────────────────────────────────────────────────── */
        .item-list  { list-style: none; margin: 0; padding: 0; }
        .item-row   {
            display: flex; align-items: center; justify-content: space-between;
            gap: 1rem; padding: .65rem .35rem;
            cursor: pointer; border-bottom: 1px solid #f8fafc;
            border-radius: 8px; transition: background .12s ease;
        }
        .item-row:hover { background: #f8fafc; }
        .item-row.item-last { border-bottom: none; }
        .item-main  { flex: 1; min-width: 0; }
        .item-title { font-size: .875rem; font-weight: 600; color: #1e293b; margin: 0 0 .2rem; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .item-sub   { font-size: .73rem; color: #94a3b8; margin: 0; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .item-right { text-align: right; flex-shrink: 0; }
        .item-amount    { font-size: .875rem; font-weight: 600; color: #1e293b; margin: 0 0 .3rem; }
        .item-date      { font-size: .7rem; color: #9ca3af; margin: 0 0 .3rem; }
        .item-alert-val { font-size: .875rem; font-weight: 700; color: #ef4444; margin: 0 0 .2rem; }

        /* ── Pipeline ────────────────────────────────────────────────────────── */
        .pipeline-list     { display: flex; flex-direction: column; gap: .85rem; }
        .pipeline-row      { display: flex; align-items: center; gap: .75rem; }
        .pipeline-lbl      { font-size: .73rem; font-weight: 600; color: #64748b; flex-shrink: 0; width: 8.5rem; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .pipeline-track    { flex: 1; height: 7px; background: #f1f5f9; border-radius: 4px; overflow: hidden; }
        .pipeline-fill     { height: 100%; border-radius: 4px; transition: width .7s cubic-bezier(.4,0,.2,1); }
        .pipeline-cnt      { font-size: .73rem; font-weight: 700; flex-shrink: 0; width: 2rem; text-align: right; }

        /* ── Reorder alerts ──────────────────────────────────────────────────── */
        .alert-badge { background: #f97316; color: #fff; font-size: .68rem; font-weight: 700; padding: .15rem .55rem; border-radius: 12px; }
        .right-col   { display: flex; flex-direction: column; gap: 1.25rem; }

        .link-block { display: block; text-align: center; font-size: .78rem; font-weight: 600; color: #3b82f6; text-decoration: none; margin-top: .75rem; padding-top: .75rem; border-top: 1px solid #f1f5f9; }
        .link-block:hover { text-decoration: underline; }

        /* ── Empty states ────────────────────────────────────────────────────── */
        .empty-state { display: flex; flex-direction: column; align-items: center; justify-content: center; padding: 3rem 1rem; gap: .75rem; }
        .empty-state.empty-sm { padding: 1.5rem 1rem; }
        .empty-icon  { font-size: 2.5rem; color: #d1d5db; }
        .empty-text  { font-size: .875rem; color: #9ca3af; margin: 0; }

        /* ── Responsive ──────────────────────────────────────────────────────── */
        @media (max-width: 1280px) {
            .dash-grid-5-7 { grid-template-columns: 1fr; }
        }
        @media (max-width: 1024px) {
            .dash-grid-6-6 { grid-template-columns: 1fr; }
        }
        @media (max-width: 640px) {
            .dash-hdr   { padding: 1.25rem 1rem 2.25rem; }
            .dash-body  { padding: 0 1rem 2rem; }
            .hero-row   { grid-template-columns: 1fr; margin-top: -1.2rem; }
            .hc-value   { font-size: 1.6rem; }
            .dash-title { font-size: 1.3rem; }
            .panel      { padding: 1.25rem 1rem; }
            .funnel-lbl, .pipeline-lbl { width: 6.5rem; }
        }
    `]
})
export class Dashboard implements OnInit, OnDestroy {

    today   = new Date();
    loading = true;
    failed  = false;

    summary: DashboardSummary | null = null;

    heroCards:   HeroCard[]      = [];
    attention:   AttentionView[] = [];
    attentionTotal = 0;

    salesFunnel: (StatusBand & { route: string })[] = [];
    funnelMax = 0;

    poBands:         StatusBand[] = [];
    grnBands:        StatusBand[] = [];
    deliveryBands:   StatusBand[] = [];
    productionBands: StatusBand[] = [];
    bomBands:        StatusBand[] = [];
    totalPos = 0; totalPrs = 0; totalGrns = 0; deliveryTotal = 0; productionTotal = 0; activeBoms = 0;

    // Lists
    recentPos:        PoListItemModel[]   = [];
    recentGrns:       GrnListItemModel[]  = [];
    recentSaleOrders: SaleOrderModel[]    = [];
    reorderAlerts:    ReorderAlertModel[] = [];
    canSeeSaleOrders = false;

    // Chart
    poChartData:    any = null;
    poChartOptions: any = null;

    skeleton4 = [1, 2, 3, 4];

    private layoutSub!: Subscription;

    constructor(
        private dashboardSvc: DashboardSummaryService,
        private demandSvc:    DemandService,
        private warehouseSvc: WarehouseService,
        private inventorySvc: InventoryService,
        private saleOrderSvc: SaleOrderService,
        private auth:         AuthService,
        public  layoutSvc:    LayoutService,
        private router:       Router
    ) {}

    go(path: string) { this.router.navigateByUrl(path); }

    ngOnInit() {
        this.loadAll();
        this.layoutSub = this.layoutSvc.configUpdate$
            .pipe(debounceTime(25))
            .subscribe(() => { if (this.poBands.length) this.buildChart(); });
    }

    loadAll() {
        this.loading = true;
        this.failed  = false;
        this.canSeeSaleOrders = this.auth.hasPermission('SALE_ORDER_VIEW');

        // The counts come in one call; the "recent" lists are fetched only for areas the user can open, so nobody
        // gets a 403 for a panel the page was going to hide anyway.
        const list = <T>(allowed: boolean, call: () => Observable<any>, pick: (r: any) => T, empty: T): Observable<T> =>
            allowed ? call().pipe(map(pick), catchError(() => of(empty))) : of(empty);

        forkJoin({
            summary:       this.dashboardSvc.getSummary().pipe(map(r => r?.result ?? null), catchError(() => of(null))),
            recentPos:     list(this.auth.hasPermission('PO_VIEW'), () => this.demandSvc.getPos({ pageSize: 5 }), r => r?.result?.data ?? [], [] as PoListItemModel[]),
            recentGrns:    list(this.auth.hasAnyPermission('INVENTORY_VIEW', 'GRN_APPROVE', 'GRN_QC_CONFIRM', 'GRN_FINANCE_APPROVE'),
                                () => this.warehouseSvc.getGrns({ pageSize: 5 }), r => r?.result?.data ?? [], [] as GrnListItemModel[]),
            recentSos:     list(this.canSeeSaleOrders, () => this.saleOrderSvc.getSaleOrders({ pageSize: 5 }), r => r?.result?.data ?? [], [] as SaleOrderModel[]),
            reorderAlerts: list(this.auth.hasPermission('INVENTORY_VIEW'), () => this.inventorySvc.getReorderAlerts(), r => r?.result ?? [], [] as ReorderAlertModel[])
        }).subscribe(r => {
            this.summary          = r.summary;
            this.failed           = r.summary === null;
            this.recentPos        = r.recentPos;
            this.recentGrns       = r.recentGrns;
            this.recentSaleOrders = r.recentSos;
            this.reorderAlerts    = r.reorderAlerts;

            this.build();
            this.loading = false;
        });
    }

    /** Everything the template shows, from the summary. Pure: easy to reason about, easy to test. */
    private build() {
        const s = this.summary;

        this.poBands         = this.bands(BANDS.po, s?.procurement?.purchaseOrders);
        this.grnBands        = this.bands(BANDS.grn, s?.receiving?.grns);
        this.deliveryBands   = this.bands(BANDS.delivery, s?.deliveries?.outbound);
        this.productionBands = this.bands(BANDS.production, s?.production?.orders);
        this.bomBands        = (s?.production?.boms ?? []).map(b => ({ label: this.formatStatus(b.status), count: b.count, hex: '#94a3b8' }));

        this.totalPos        = totalOf(s?.procurement?.purchaseOrders);
        this.totalPrs        = totalOf(s?.procurement?.requisitions);
        this.totalGrns       = totalOf(s?.receiving?.grns);
        this.deliveryTotal   = totalOf(s?.deliveries?.outbound);
        this.productionTotal = totalOf(s?.production?.orders);
        this.activeBoms      = countOf(s?.production?.boms, 'ACTIVE');

        this.buildFunnel();
        this.buildAttention();
        this.buildHeroCards();
        this.buildChart();
    }

    private buildHeroCards() {
        const s = this.summary;
        const cards: HeroCard[] = [];
        if (!s) { this.heroCards = cards; return; }

        if (s.sales?.saleOrders) {
            const open = countOf(s.sales.saleOrders, 'CONFIRMED', 'PARTIALLY_FULFILLED');
            const late = s.sales.lateSaleOrders ?? 0;
            cards.push({ key: 'sales', label: 'Open Sale Orders', value: this.n(open), icon: 'pi pi-shopping-bag', tone: 'purple',
                route: '/portal/pages/sales/orders', alert: late > 0,
                sub: `${this.n(s.sales.ordersThisMonth ?? 0)} this month · ${this.n(late)} late` });
        } else if (s.sales?.quotations) {
            cards.push({ key: 'quotations', label: 'Quotations Sent', value: this.n(countOf(s.sales.quotations, 'SENT')),
                icon: 'pi pi-file', tone: 'purple', route: '/portal/pages/sales/quotations',
                sub: `${this.n(countOf(s.sales.quotations, 'ACCEPTED'))} accepted` });
        }

        if (s.receivables) {
            const top = this.topCurrency(s.receivables.outstanding);
            const overdue = s.receivables.overdue.reduce((n, o) => n + o.count, 0);
            cards.push({ key: 'receivables', label: 'Receivables', value: top ? `${top.currency} ${this.compact(top.amount)}` : '0',
                icon: 'pi pi-wallet', tone: 'teal', route: '/portal/pages/finance/sales-invoices', alert: overdue > 0,
                sub: overdue > 0 ? `${this.n(overdue)} overdue invoice${overdue === 1 ? '' : 's'}` : 'Nothing overdue' });
        }

        if (s.deliveries) {
            const inProgress = totalOf(s.deliveries.outbound) -
                countOf(s.deliveries.outbound, 'DELIVERED', 'CLOSED', 'SHORT_CLOSED', 'CANCELLED', 'IN_TRANSIT', 'GOODS_ISSUED', 'PARTIALLY_DELIVERED');
            cards.push({ key: 'deliveries', label: 'Deliveries In Progress', value: this.n(inProgress), icon: 'pi pi-truck', tone: 'green',
                route: '/portal/pages/logistics/deliveries', alert: s.deliveries.late > 0,
                sub: `${this.n(countOf(s.deliveries.outbound, 'IN_TRANSIT'))} in transit · ${this.n(s.deliveries.late)} late` });
        }

        if (s.production?.orders) {
            const active = totalOf(s.production.orders) - countOf(s.production.orders, 'COMPLETED', 'CLOSED', 'CANCELLED');
            const late = s.production.late ?? 0;
            cards.push({ key: 'production', label: 'Active Production', value: this.n(active), icon: 'pi pi-cog', tone: 'orange',
                route: '/portal/pages/manufacturing/production-orders', alert: late > 0,
                sub: `${this.n(s.production.openMakeToOrder ?? 0)} made to order · ${this.n(late)} late` });
        }

        if (s.procurement) {
            const open = countOf(s.procurement.purchaseOrders, 'APPROVED', 'SENT', 'PARTIALLY_RECEIVED');
            cards.push({ key: 'purchasing', label: 'Open Purchase Orders', value: this.n(open), icon: 'pi pi-shopping-cart', tone: 'blue',
                route: '/portal/pages/demand/purchase-orders',
                sub: `${this.n(countOf(s.procurement.purchaseOrders, 'SENT', 'PARTIALLY_RECEIVED'))} awaiting delivery` });
        }

        if (s.payables) {
            const top = this.topCurrency(s.payables.outstanding);
            const overdue = s.payables.overdue.reduce((n, o) => n + o.count, 0);
            cards.push({ key: 'payables', label: 'Payables', value: top ? `${top.currency} ${this.compact(top.amount)}` : '0',
                icon: 'pi pi-credit-card', tone: 'rose', route: '/portal/pages/finance/invoices', alert: overdue > 0,
                sub: overdue > 0 ? `${this.n(overdue)} overdue bill${overdue === 1 ? '' : 's'}` : 'Nothing overdue' });
        }

        if (s.inventory) {
            const alerts = this.reorderAlerts.length;
            cards.push({ key: 'inventory', label: 'Active Products', value: this.n(s.inventory.activeProducts), icon: 'pi pi-box', tone: 'indigo',
                route: alerts > 0 ? '/portal/pages/inventory/reorder-alerts' : '/portal/pages/inventory/products', alert: alerts > 0,
                sub: `${this.n(alerts)} reorder alert${alerts === 1 ? '' : 's'} · ${this.n(s.inventory.manufacturedProducts)} manufactured` });
        }

        cards.push({ key: 'attention', label: 'Needs Attention', value: this.n(this.attentionTotal), icon: 'pi pi-bell',
            tone: this.attentionTotal > 0 ? 'orange' : 'slate', route: '/portal/pages/reports/pending-approvals',
            alert: this.attentionTotal > 0, sub: `${this.attention.length} queue${this.attention.length === 1 ? '' : 's'} waiting` });

        this.heroCards = cards;
    }

    private buildFunnel() {
        const s = this.summary?.sales;
        const rows: (StatusBand & { route: string })[] = [];
        if (s?.inquiries)
            rows.push({ label: 'Inquiries open', count: countOf(s.inquiries, 'RECEIVED', 'UNDER_REVIEW', 'REVIEW_COMPLETE'), hex: '#a78bfa', route: '/portal/pages/sales/inquiries' });
        if (s?.quotations) {
            rows.push({ label: 'Quotations sent', count: countOf(s.quotations, 'SENT'), hex: '#8b5cf6', route: '/portal/pages/sales/quotations' });
            rows.push({ label: 'Quotations accepted', count: countOf(s.quotations, 'ACCEPTED'), hex: '#7c3aed', route: '/portal/pages/sales/quotations' });
        }
        if (s?.saleOrders) {
            rows.push({ label: 'Orders in draft', count: countOf(s.saleOrders, 'DRAFT'), hex: '#94a3b8', route: '/portal/pages/sales/orders' });
            rows.push({ label: 'Orders confirmed', count: countOf(s.saleOrders, 'CONFIRMED'), hex: '#6366f1', route: '/portal/pages/sales/orders' });
            rows.push({ label: 'Partly fulfilled', count: countOf(s.saleOrders, 'PARTIALLY_FULFILLED'), hex: '#f97316', route: '/portal/pages/sales/orders' });
            rows.push({ label: 'Fulfilled / invoiced', count: countOf(s.saleOrders, 'FULFILLED', 'INVOICED'), hex: '#22c55e', route: '/portal/pages/sales/orders' });
        }
        this.salesFunnel = rows;
        this.funnelMax   = Math.max(0, ...rows.map(r => r.count));
    }

    private buildAttention() {
        const byKey = new Map((this.summary?.attention ?? []).map(a => [a.key, a.count]));
        // In the page's own priority order; a key the page does not know yet is still shown, by its code.
        const known = Object.entries(ATTENTION)
            .filter(([key]) => byKey.has(key))
            .map(([key, meta]) => ({ key, count: byKey.get(key)!, label: meta.label, icon: meta.icon, route: meta.route, danger: !!meta.danger }));
        const unknown = [...byKey.entries()]
            .filter(([key]) => !(key in ATTENTION))
            .map(([key, count]) => ({ key, count, label: this.formatStatus(key), icon: 'pi pi-info-circle', route: '/portal/pages/reports/pending-approvals', danger: false }));

        this.attention      = [...known, ...unknown];
        this.attentionTotal = this.attention.reduce((n, a) => n + a.count, 0);
    }

    private bands(order: readonly (readonly [string, string, string])[], counts: StatusCount[] | null | undefined): StatusBand[] {
        const list = counts ?? [];
        const known = order.map(([status, label, hex]) => ({ label, hex, count: countOf(list, status) }));
        const extra = list.filter(c => !order.some(([status]) => status === c.status))
                          .map(c => ({ label: this.formatStatus(c.status), hex: '#94a3b8', count: c.count }));
        return [...known, ...extra].filter(b => b.count > 0);
    }

    buildChart() {
        if (!this.poBands.length) { this.poChartData = null; return; }

        this.poChartData = {
            labels: this.poBands.map(b => b.label),
            datasets: [{
                data:            this.poBands.map(b => b.count),
                backgroundColor: this.poBands.map(b => b.hex),
                borderColor:     '#ffffff',
                borderWidth:     3,
                hoverOffset:     8
            }]
        };

        this.poChartOptions = {
            responsive:          true,
            maintainAspectRatio: false,
            cutout:              '72%',
            plugins: {
                legend: { display: false },
                tooltip: {
                    backgroundColor: '#1f2937',
                    titleColor:      '#f9fafb',
                    bodyColor:       '#d1d5db',
                    padding:         12,
                    cornerRadius:    10,
                    callbacks: {
                        label: (ctx: any) => {
                            const pct = this.totalPos > 0 ? Math.round(ctx.raw / this.totalPos * 100) : 0;
                            return ` ${ctx.label}: ${ctx.raw} (${pct}%)`;
                        }
                    }
                }
            }
        };
    }

    overdueFor(overdue: CurrencyAmount[], currency: string): number {
        return overdue.find(o => o.currency === currency)?.amount ?? 0;
    }

    /** The currency with the most outstanding — the hero card's headline. The panel shows all of them. */
    private topCurrency(rows: CurrencyAmount[]): CurrencyAmount | null {
        return rows.length ? [...rows].sort((a, b) => b.amount - a.amount)[0] : null;
    }

    private n(value: number): string { return value.toLocaleString(); }

    /** 1,250,000 → 1.25M — a hero card has room for a headline, not a ledger figure. */
    private compact(value: number): string {
        const abs = Math.abs(value);
        if (abs >= 1_000_000_000) return (value / 1_000_000_000).toFixed(2).replace(/\.?0+$/, '') + 'B';
        if (abs >= 1_000_000)     return (value / 1_000_000).toFixed(2).replace(/\.?0+$/, '') + 'M';
        if (abs >= 10_000)        return (value / 1_000).toFixed(1).replace(/\.0$/, '') + 'K';
        return Math.round(value).toLocaleString();
    }

    getPoSeverity(status: string): 'success' | 'info' | 'warn' | 'danger' | 'secondary' {
        const m: Record<string, 'success' | 'info' | 'warn' | 'danger' | 'secondary'> = {
            DRAFT: 'secondary', APPROVED: 'info', SENT: 'info', PARTIALLY_RECEIVED: 'warn',
            RECEIVED: 'success', CLOSED: 'secondary', CANCELLED: 'danger'
        };
        return m[status] ?? 'secondary';
    }

    getSoSeverity(status: string): 'success' | 'info' | 'warn' | 'danger' | 'secondary' {
        const m: Record<string, 'success' | 'info' | 'warn' | 'danger' | 'secondary'> = {
            DRAFT: 'secondary', CONFIRMED: 'info', PARTIALLY_FULFILLED: 'warn', FULFILLED: 'success',
            INVOICED: 'success', CLOSED: 'secondary', CANCELLED: 'danger'
        };
        return m[status] ?? 'secondary';
    }

    getGrnSeverity(status: string): 'success' | 'info' | 'warn' | 'danger' | 'secondary' {
        const m: Record<string, 'success' | 'info' | 'warn' | 'danger' | 'secondary'> = {
            DRAFT: 'secondary', PENDING_QC: 'warn', PENDING_FINANCE: 'warn', PENDING_APPROVAL: 'warn',
            APPROVED: 'success', REJECTED: 'danger'
        };
        return m[status] ?? 'secondary';
    }

    formatStatus(s: string): string {
        return s.split('_').map(w => {
            if (w === 'QC') return 'QC';
            return w.charAt(0) + w.slice(1).toLowerCase();
        }).join(' ');
    }

    ngOnDestroy() {
        this.layoutSub?.unsubscribe();
    }
}
