import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule }                  from '@angular/common';
import { Router, RouterModule }          from '@angular/router';
import { forkJoin, of, Subscription }    from 'rxjs';
import { catchError, debounceTime }      from 'rxjs/operators';
import { ChartModule }                   from 'primeng/chart';
import { TagModule }                     from 'primeng/tag';
import { ButtonModule }                  from 'primeng/button';
import { SkeletonModule }                from 'primeng/skeleton';
import { DemandService, PoListItemModel }      from '../../services/demand.service';
import { WarehouseService, GrnListItemModel }  from '../../services/warehouse.service';
import { SupplierService }                     from '../../services/supplier.service';
import { InventoryService, ReorderAlertModel } from '../../services/inventory.service';
import { LayoutService }                       from '../../layout/service/layout.service';

interface StatusBand {
    label: string;
    count: number;
    hex: string;
    textClass: string;
}

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
      <div class="live-pill" *ngIf="!loading">
        <span class="live-dot"></span>Live
      </div>
      <p-button label="Refresh" icon="pi pi-refresh" size="small" [outlined]="true"
                severity="secondary" [loading]="loading" (onClick)="loadAll()" />
    </div>
  </div>

  <div class="dash-body">

    <!-- ── Hero metric strip ──────────────────────────────────────────────── -->
    <div class="hero-row">

      <!-- Purchase Orders -->
      <div class="hero-card" (click)="go('/portal/pages/demand/purchase-orders')">
        <div class="hc-icon ic-blue"><i class="pi pi-shopping-cart"></i></div>
        <div class="hc-body">
          <ng-container *ngIf="!loading; else hcSkel">
            <div class="hc-value">{{ totalPos | number }}</div>
            <div class="hc-label">Purchase Orders</div>
            <div class="hc-sub"><span class="hc-accent hca-blue">{{ sentPos }}</span> in delivery</div>
          </ng-container>
          <ng-template #hcSkel>
            <p-skeleton width="4rem" height="1.8rem" styleClass="mb-1" />
            <p-skeleton width="7rem" height="0.65rem" />
          </ng-template>
        </div>
      </div>

      <!-- Active Suppliers -->
      <div class="hero-card" (click)="go('/portal/pages/suppliers/supplier-list')">
        <div class="hc-icon ic-green"><i class="pi pi-building"></i></div>
        <div class="hc-body">
          <ng-container *ngIf="!loading; else hcSkel2">
            <div class="hc-value">{{ approvedSuppliers | number }}</div>
            <div class="hc-label">Active Suppliers</div>
            <div class="hc-sub"><span class="hc-accent hca-green">{{ totalSuppliers }}</span> registered</div>
          </ng-container>
          <ng-template #hcSkel2>
            <p-skeleton width="4rem" height="1.8rem" styleClass="mb-1" />
            <p-skeleton width="7rem" height="0.65rem" />
          </ng-template>
        </div>
      </div>

      <!-- Products -->
      <div class="hero-card" (click)="go('/portal/pages/inventory/products')">
        <div class="hc-icon ic-purple"><i class="pi pi-box"></i></div>
        <div class="hc-body">
          <ng-container *ngIf="!loading; else hcSkel3">
            <div class="hc-value">{{ totalProducts | number }}</div>
            <div class="hc-label">Products</div>
            <div class="hc-sub">
              <span class="hc-accent" [class.hca-orange]="reorderAlerts.length > 0" [class.hca-purple]="reorderAlerts.length === 0">
                {{ reorderAlerts.length }}
              </span>
              {{ reorderAlerts.length === 1 ? 'reorder alert' : 'reorder alerts' }}
            </div>
          </ng-container>
          <ng-template #hcSkel3>
            <p-skeleton width="4rem" height="1.8rem" styleClass="mb-1" />
            <p-skeleton width="7rem" height="0.65rem" />
          </ng-template>
        </div>
      </div>

      <!-- Pending Actions -->
      <div class="hero-card" [class.hc-alert]="!loading && pendingActions > 0"
           (click)="go('/portal/pages/reports/pending-approvals')">
        <div class="hc-icon" [class.ic-orange]="!loading && pendingActions > 0"
             [class.ic-slate]="loading || pendingActions === 0">
          <i class="pi pi-bell"></i>
        </div>
        <div class="hc-body">
          <ng-container *ngIf="!loading; else hcSkel4">
            <div class="hc-value" [class.hv-alert]="pendingActions > 0">{{ pendingActions | number }}</div>
            <div class="hc-label">Pending Actions</div>
            <div class="hc-sub">Approvals awaiting action</div>
          </ng-container>
          <ng-template #hcSkel4>
            <p-skeleton width="4rem" height="1.8rem" styleClass="mb-1" />
            <p-skeleton width="7rem" height="0.65rem" />
          </ng-template>
        </div>
      </div>

    </div>

    <!-- ── Row 2: PO Donut + Recent POs ──────────────────────────────────── -->
    <div class="dash-grid dash-grid-5-7 mb-section">

      <!-- PO Status Chart -->
      <div class="panel">
        <div class="panel-hdr">
          <span class="ph-accent po-accent"></span>
          <span class="ph-icon po-icon"><i class="pi pi-chart-pie"></i></span>
          <span class="ph-title">PO Status Overview</span>
          <a [routerLink]="['/portal/pages/demand/purchase-orders']" class="ph-link">View all</a>
        </div>

        <ng-container *ngIf="loading">
          <div class="chart-skel-wrap">
            <p-skeleton shape="circle" size="9rem" />
          </div>
          <div class="chart-skel-rows">
            <p-skeleton width="80%" height="0.7rem" />
            <p-skeleton width="60%" height="0.7rem" />
            <p-skeleton width="70%" height="0.7rem" />
          </div>
        </ng-container>

        <ng-container *ngIf="!loading && !poChartData">
          <div class="empty-state">
            <i class="pi pi-chart-pie empty-icon"></i>
            <p class="empty-text">No purchase orders yet</p>
            <a [routerLink]="['/portal/pages/demand/purchase-orders/create']" class="ph-link">Create first PO</a>
          </div>
        </ng-container>

        <div *ngIf="!loading && poChartData" class="chart-donut-wrap">
          <p-chart type="doughnut" [data]="poChartData" [options]="poChartOptions"
                   [style]="{'width':'100%','height':'220px'}" />
        </div>

        <!-- Custom HTML legend — never clipped by canvas height -->
        <div *ngIf="!loading && poBands.length > 0" class="chart-legend">
          <div *ngFor="let b of poBands" class="legend-item">
            <span class="legend-dot" [style.background]="b.hex"></span>
            <span class="legend-label">{{ b.label }}</span>
            <span class="legend-count" [style.color]="b.hex">{{ b.count }}</span>
          </div>
        </div>

        <div *ngIf="!loading" class="chart-footer">
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
          <div class="cstat">
            <span class="cstat-val">{{ totalGrns | number }}</span>
            <span class="cstat-lbl">GRN Records</span>
          </div>
        </div>
      </div>

      <!-- Recent Purchase Orders -->
      <div class="panel">
        <div class="panel-hdr">
          <span class="ph-accent po-accent"></span>
          <span class="ph-icon po-icon"><i class="pi pi-shopping-cart"></i></span>
          <span class="ph-title">Recent Purchase Orders</span>
          <a [routerLink]="['/portal/pages/demand/purchase-orders']" class="ph-link">View all</a>
        </div>

        <ng-container *ngIf="loading">
          <div *ngFor="let _ of skeleton5" class="list-skel-row">
            <div class="list-skel-left">
              <p-skeleton width="8rem" height="0.78rem" styleClass="mb-2" />
              <p-skeleton width="12rem" height="0.65rem" />
            </div>
            <div class="list-skel-right">
              <p-skeleton width="5.5rem" height="0.78rem" styleClass="mb-2" />
              <p-skeleton width="4rem" height="1.2rem" borderRadius="4px" />
            </div>
          </div>
        </ng-container>

        <div *ngIf="!loading && recentPos.length === 0" class="empty-state">
          <i class="pi pi-shopping-cart empty-icon"></i>
          <p class="empty-text">No purchase orders yet</p>
        </div>

        <ul *ngIf="!loading && recentPos.length > 0" class="item-list">
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

    <!-- ── Row 3: Recent GRNs + Pipeline + Reorder ───────────────────────── -->
    <div class="dash-grid dash-grid-6-6">

      <!-- Recent GRNs -->
      <div class="panel">
        <div class="panel-hdr">
          <span class="ph-accent grn-accent"></span>
          <span class="ph-icon grn-icon"><i class="pi pi-truck"></i></span>
          <span class="ph-title">Recent GRN Records</span>
          <a [routerLink]="['/portal/pages/warehouse/grn']" class="ph-link">View all</a>
        </div>

        <ng-container *ngIf="loading">
          <div *ngFor="let _ of skeleton5" class="list-skel-row">
            <div class="list-skel-left">
              <p-skeleton width="7rem" height="0.78rem" styleClass="mb-2" />
              <p-skeleton width="11rem" height="0.65rem" />
            </div>
            <div class="list-skel-right">
              <p-skeleton width="4rem" height="0.65rem" styleClass="mb-2" />
              <p-skeleton width="5rem" height="1.2rem" borderRadius="4px" />
            </div>
          </div>
        </ng-container>

        <div *ngIf="!loading && recentGrns.length === 0" class="empty-state">
          <i class="pi pi-truck empty-icon"></i>
          <p class="empty-text">No goods receipts yet</p>
        </div>

        <ul *ngIf="!loading && recentGrns.length > 0" class="item-list">
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

      <!-- Right column: Pipeline + Reorder stacked -->
      <div class="right-col">

        <!-- GRN Pipeline -->
        <div class="panel">
          <div class="panel-hdr">
            <span class="ph-accent pipeline-accent"></span>
            <span class="ph-icon pipeline-icon"><i class="pi pi-sliders-h"></i></span>
            <span class="ph-title">GRN Pipeline</span>
          </div>

          <ng-container *ngIf="loading">
            <div *ngFor="let _ of skeleton4" class="pipeline-skel-row">
              <p-skeleton width="7rem" height="0.65rem" />
              <p-skeleton width="100%" height="0.45rem" />
              <p-skeleton width="1.25rem" height="0.65rem" />
            </div>
          </ng-container>

          <div *ngIf="!loading && grnBands.length === 0" class="empty-state empty-sm">
            <i class="pi pi-chart-bar empty-icon"></i>
            <p class="empty-text">No GRN data yet</p>
          </div>

          <div *ngIf="!loading && grnBands.length > 0" class="pipeline-list">
            <div *ngFor="let band of grnBands" class="pipeline-row">
              <span class="pipeline-lbl">{{ band.label }}</span>
              <div class="pipeline-track">
                <div class="pipeline-fill"
                     [style.width.%]="totalGrns > 0 ? (band.count / totalGrns * 100) : 0"
                     [style.background]="band.hex"></div>
              </div>
              <span class="pipeline-cnt" [style.color]="band.hex">{{ band.count }}</span>
            </div>
          </div>
        </div>

        <!-- Reorder Alerts -->
        <div class="panel" *ngIf="!loading && reorderAlerts.length > 0">
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
            grid-template-columns: repeat(4, 1fr);
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

        .hc-body { flex: 1; min-width: 0; }
        .hc-value { font-size: 2rem; font-weight: 800; color: #111827; line-height: 1; letter-spacing: -.03em; margin-bottom: .25rem; }
        .hc-value.hv-alert { color: #f97316; }
        .hc-label { font-size: .75rem; font-weight: 700; color: #6b7280; text-transform: uppercase; letter-spacing: .05em; margin-bottom: .2rem; }
        .hc-sub   { font-size: .76rem; color: #9ca3af; }
        .hc-accent { font-weight: 700; }
        .hc-accent.hca-blue   { color: #0891b2; }
        .hc-accent.hca-green  { color: #16a34a; }
        .hc-accent.hca-orange { color: #f97316; }
        .hc-accent.hca-purple { color: #7c3aed; }

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

        /* ── Chart ───────────────────────────────────────────────────────────── */
        .chart-skel-wrap  { display: flex; justify-content: center; padding: 2rem 0 1rem; }
        .chart-skel-rows  { display: flex; flex-direction: column; gap: .6rem; padding: .5rem 0 0; }
        .chart-donut-wrap { width: 100%; }

        /* Custom legend below the donut */
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

        /* ── Skeleton rows ───────────────────────────────────────────────────── */
        .list-skel-row   { display: flex; justify-content: space-between; align-items: center; padding: .75rem 0; border-bottom: 1px solid #f8fafc; }
        .list-skel-row:last-child { border-bottom: none; }
        .list-skel-left  { display: flex; flex-direction: column; gap: .4rem; }
        .list-skel-right { display: flex; flex-direction: column; align-items: flex-end; gap: .4rem; }

        /* ── Pipeline ────────────────────────────────────────────────────────── */
        .pipeline-list     { display: flex; flex-direction: column; gap: .85rem; }
        .pipeline-row      { display: flex; align-items: center; gap: .75rem; }
        .pipeline-lbl      { font-size: .73rem; font-weight: 600; color: #64748b; flex-shrink: 0; width: 7.5rem; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
        .pipeline-track    { flex: 1; height: 7px; background: #f1f5f9; border-radius: 4px; overflow: hidden; }
        .pipeline-fill     { height: 100%; border-radius: 4px; transition: width .7s cubic-bezier(.4,0,.2,1); }
        .pipeline-cnt      { font-size: .73rem; font-weight: 700; flex-shrink: 0; width: 1.5rem; text-align: right; }
        .pipeline-skel-row { display: flex; align-items: center; gap: .75rem; margin-bottom: 1rem; }

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
            .hero-row      { grid-template-columns: repeat(2, 1fr); }
            .dash-grid-6-6 { grid-template-columns: 1fr; }
        }
        @media (max-width: 640px) {
            .dash-hdr   { padding: 1.25rem 1.25rem 2.25rem; }
            .dash-body  { padding: 0 1rem 2rem; }
            .hero-row   { grid-template-columns: 1fr 1fr; margin-top: -1.2rem; }
            .hc-value   { font-size: 1.6rem; }
            .dash-title { font-size: 1.3rem; }
        }
    `]
})
export class Dashboard implements OnInit, OnDestroy {

    today = new Date();
    loading = true;

    // KPI numbers
    totalPos           = 0;
    sentPos            = 0;
    approvedSuppliers  = 0;
    totalSuppliers     = 0;
    totalProducts      = 0;
    totalPrs           = 0;
    totalGrns          = 0;
    pendingActions     = 0;

    // Lists
    recentPos:      PoListItemModel[]   = [];
    recentGrns:     GrnListItemModel[]  = [];
    reorderAlerts:  ReorderAlertModel[] = [];

    // Chart
    poChartData:    any = null;
    poChartOptions: any = null;

    // Status bands
    poBands:  StatusBand[] = [];
    grnBands: StatusBand[] = [];

    // Template helpers
    skeleton5 = [1, 2, 3, 4, 5];
    skeleton4 = [1, 2, 3, 4];

    private layoutSub!: Subscription;

    constructor(
        private demandSvc:    DemandService,
        private warehouseSvc: WarehouseService,
        private supplierSvc:  SupplierService,
        private inventorySvc: InventoryService,
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
        const safe = <T>(obs: any) => (obs as any).pipe(catchError(() => of(null)));

        forkJoin({
            totalPos:      safe(this.demandSvc.getPos({ pageSize: 1 })),
            recentPos:     safe(this.demandSvc.getPos({ pageSize: 5 })),
            sentPos:       safe(this.demandSvc.getPos({ status: 'SENT',               pageSize: 1 })),
            partialPos:    safe(this.demandSvc.getPos({ status: 'PARTIALLY_RECEIVED', pageSize: 1 })),
            draftPos:      safe(this.demandSvc.getPos({ status: 'DRAFT',             pageSize: 1 })),
            pendingPos:    safe(this.demandSvc.getPos({ status: 'PENDING_APPROVAL',  pageSize: 1 })),
            approvedPos:   safe(this.demandSvc.getPos({ status: 'APPROVED',          pageSize: 1 })),
            receivedPos:   safe(this.demandSvc.getPos({ status: 'RECEIVED',          pageSize: 1 })),
            partInvPos:    safe(this.demandSvc.getPos({ status: 'PARTIALLY_INVOICED', pageSize: 1 })),
            closedPos:     safe(this.demandSvc.getPos({ status: 'CLOSED',            pageSize: 1 })),
            rejectedPos:   safe(this.demandSvc.getPos({ status: 'REJECTED',          pageSize: 1 })),
            cancelledPos:  safe(this.demandSvc.getPos({ status: 'CANCELLED',         pageSize: 1 })),
            pendingPrs:    safe(this.demandSvc.getPrs({ status: 'SUBMITTED',         pageSize: 1 })),
            totalPrs:      safe(this.demandSvc.getPrs({ pageSize: 1 })),
            approvedSup:   safe(this.supplierSvc.getSuppliers({ status: 'ACTIVE',    pageSize: 1 })),
            totalSup:      safe(this.supplierSvc.getSuppliers({ pageSize: 1 })),
            totalProducts: safe(this.inventorySvc.getProducts({ pageSize: 1 })),
            recentGrns:    safe(this.warehouseSvc.getGrns({ pageSize: 5 })),
            totalGrns:     safe(this.warehouseSvc.getGrns({ pageSize: 1 })),
            grnQc:         safe(this.warehouseSvc.getGrns({ status: 'PENDING_QC',       pageSize: 1 })),
            grnFin:        safe(this.warehouseSvc.getGrns({ status: 'PENDING_FINANCE',  pageSize: 1 })),
            grnAppr:       safe(this.warehouseSvc.getGrns({ status: 'PENDING_APPROVAL', pageSize: 1 })),
            grnApproved:   safe(this.warehouseSvc.getGrns({ status: 'APPROVED',         pageSize: 1 })),
            grnDraft:      safe(this.warehouseSvc.getGrns({ status: 'DRAFT',            pageSize: 1 })),
            grnRejected:   safe(this.warehouseSvc.getGrns({ status: 'REJECTED',         pageSize: 1 })),
            reorderAlerts: safe(this.inventorySvc.getReorderAlerts()),
        }).subscribe((r: any) => {
            const n = (res: any) => res?.result?.totalRecords ?? 0;

            this.totalPos          = n(r.totalPos);
            this.sentPos           = n(r.sentPos) + n(r.partialPos);
            this.approvedSuppliers = n(r.approvedSup);
            this.totalSuppliers    = n(r.totalSup);
            this.totalProducts     = n(r.totalProducts);
            this.totalPrs          = n(r.totalPrs);
            this.totalGrns         = n(r.totalGrns);
            this.recentPos         = r.recentPos?.result?.data  ?? [];
            this.recentGrns        = r.recentGrns?.result?.data ?? [];
            this.reorderAlerts     = r.reorderAlerts?.result    ?? [];
            this.pendingActions    = n(r.pendingPrs) + n(r.grnQc) + n(r.grnFin) + n(r.grnAppr);

            this.poBands = [
                { label: 'Draft',              count: n(r.draftPos),    hex: '#94a3b8', textClass: 'text-slate-400'   },
                { label: 'Pending Approval',   count: n(r.pendingPos),  hex: '#eab308', textClass: 'text-yellow-500'  },
                { label: 'Approved',           count: n(r.approvedPos), hex: '#3b82f6', textClass: 'text-blue-500'    },
                { label: 'Sent',               count: n(r.sentPos),     hex: '#06b6d4', textClass: 'text-cyan-500'    },
                { label: 'Partial Receipt',    count: n(r.partialPos),  hex: '#f97316', textClass: 'text-orange-500'  },
                { label: 'Received',           count: n(r.receivedPos), hex: '#22c55e', textClass: 'text-green-500'   },
                { label: 'Partially Invoiced', count: n(r.partInvPos),  hex: '#a855f7', textClass: 'text-purple-500'  },
                { label: 'Closed',             count: n(r.closedPos),   hex: '#64748b', textClass: 'text-slate-500'   },
                { label: 'Rejected',           count: n(r.rejectedPos), hex: '#dc2626', textClass: 'text-red-600'     },
                { label: 'Cancelled',          count: n(r.cancelledPos),hex: '#ef4444', textClass: 'text-red-400'     },
            ].filter(b => b.count > 0);

            this.grnBands = [
                { label: 'Draft',            count: n(r.grnDraft),    hex: '#94a3b8', textClass: 'text-slate-400'  },
                { label: 'Pending QC',       count: n(r.grnQc),       hex: '#ca8a04', textClass: 'text-yellow-600' },
                { label: 'Pending Finance',  count: n(r.grnFin),      hex: '#f97316', textClass: 'text-orange-500' },
                { label: 'Pending Approval', count: n(r.grnAppr),     hex: '#f59e0b', textClass: 'text-amber-500'  },
                { label: 'Approved',         count: n(r.grnApproved), hex: '#22c55e', textClass: 'text-green-500'  },
                { label: 'Rejected',         count: n(r.grnRejected), hex: '#ef4444', textClass: 'text-red-500'    },
            ].filter(b => b.count > 0);

            this.buildChart();
            this.loading = false;
        });
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

    getPoSeverity(status: string): 'success' | 'info' | 'warn' | 'danger' | 'secondary' {
        const m: Record<string, 'success' | 'info' | 'warn' | 'danger' | 'secondary'> = {
            DRAFT:               'secondary',
            APPROVED:            'info',
            SENT:                'info',
            PARTIALLY_RECEIVED:  'warn',
            RECEIVED:            'success',
            CLOSED:              'secondary',
            CANCELLED:           'danger'
        };
        return m[status] ?? 'secondary';
    }

    getGrnSeverity(status: string): 'success' | 'info' | 'warn' | 'danger' | 'secondary' {
        const m: Record<string, 'success' | 'info' | 'warn' | 'danger' | 'secondary'> = {
            DRAFT:             'secondary',
            PENDING_QC:        'warn',
            PENDING_FINANCE:   'warn',
            PENDING_APPROVAL:  'warn',
            APPROVED:          'success',
            REJECTED:          'danger'
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
