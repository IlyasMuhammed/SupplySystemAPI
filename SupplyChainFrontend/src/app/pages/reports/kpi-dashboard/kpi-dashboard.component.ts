import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ButtonModule } from 'primeng/button';
import { SkeletonModule } from 'primeng/skeleton';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { ChartModule } from 'primeng/chart';
import { MessageService } from 'primeng/api';
import { ReportsService, KpiDashboardModel } from '../../../services/reports.service';

export interface KpiCard {
  key:            keyof KpiDashboardModel;
  label:          string;
  subtitle:       string;
  icon:           string;
  iconClass:      string;
  format:         'number' | 'percent' | 'count' | 'times';
  good:           (v: number) => boolean;
  warn:           (v: number) => boolean;
  target:         number;
  higherIsBetter: boolean;
}

@Component({
  selector: 'app-kpi-dashboard',
  standalone: true,
  imports: [CommonModule, ButtonModule, SkeletonModule, ToastModule, TooltipModule, ChartModule],
  templateUrl: './kpi-dashboard.component.html',
  styleUrls: ['./kpi-dashboard.component.scss'],
  providers: [MessageService]
})
export class KpiDashboardComponent implements OnInit {
  kpis:         KpiDashboardModel | null = null;
  isLoading     = true;
  isRefreshing  = false;
  lastUpdated:  Date | null = null;

  donutData:    any = null;
  donutOptions: any = null;

  private readonly CACHE_KEY = 'kpi_dashboard_v1';
  private readonly CACHE_TTL = 5 * 60 * 1000;

  readonly procurementCards: KpiCard[] = [
    {
      key: 'poCycleTimeDays', label: 'PO Cycle Time', subtitle: 'Avg days from PR to PO creation',
      icon: 'pi pi-clock', iconClass: 'ic-blue-purple',
      format: 'number', target: 5, higherIsBetter: false,
      good: v => v <= 5, warn: v => v <= 10
    },
    {
      key: 'supplierOnTimeDeliveryRate', label: 'On-Time Delivery', subtitle: 'Supplier deliveries on or before due date',
      icon: 'pi pi-truck', iconClass: 'ic-teal-green',
      format: 'percent', target: 90, higherIsBetter: true,
      good: v => v >= 90, warn: v => v >= 75
    },
    {
      key: 'poFillRate', label: 'PO Fill Rate', subtitle: 'PO lines fully received',
      icon: 'pi pi-check-square', iconClass: 'ic-pink-red',
      format: 'percent', target: 95, higherIsBetter: true,
      good: v => v >= 95, warn: v => v >= 80
    },
  ];

  readonly inventoryCards: KpiCard[] = [
    {
      key: 'stockTurnoverRatio', label: 'Stock Turnover', subtitle: 'Annual spend ÷ avg inventory value',
      icon: 'pi pi-sync', iconClass: 'ic-blue-cyan',
      format: 'times', target: 4, higherIsBetter: true,
      good: v => v >= 4, warn: v => v >= 2
    },
    {
      key: 'inventoryAccuracy', label: 'Inventory Accuracy', subtitle: 'Items without rejected adjustments',
      icon: 'pi pi-database', iconClass: 'ic-green-teal',
      format: 'percent', target: 98, higherIsBetter: true,
      good: v => v >= 98, warn: v => v >= 95
    },
    {
      key: 'reorderTriggerCount', label: 'Reorder Triggers', subtitle: 'Items currently below reorder point',
      icon: 'pi pi-exclamation-triangle', iconClass: 'ic-orange-yellow',
      format: 'count', target: 0, higherIsBetter: false,
      good: v => v === 0, warn: v => v <= 5
    },
  ];

  readonly financeCards: KpiCard[] = [
    {
      key: 'invoiceProcessingTimeDays', label: 'Invoice Processing', subtitle: 'Avg days from receipt to approval',
      icon: 'pi pi-file-edit', iconClass: 'ic-purple-pink',
      format: 'number', target: 3, higherIsBetter: false,
      good: v => v <= 3, warn: v => v <= 7
    },
    {
      key: 'threeWayMatchRate', label: '3-Way Match Rate', subtitle: 'Invoices matched to PO + GRN',
      icon: 'pi pi-verified', iconClass: 'ic-green-dark',
      format: 'percent', target: 90, higherIsBetter: true,
      good: v => v >= 90, warn: v => v >= 75
    },
    {
      key: 'budgetVariancePercent', label: 'Budget Variance', subtitle: 'Actual spend vs estimated budget',
      icon: 'pi pi-chart-line', iconClass: 'ic-red-orange',
      format: 'percent', target: 5, higherIsBetter: false,
      good: v => v <= 5, warn: v => v <= 15
    },
    {
      key: 'grnRejectionRate', label: 'GRN Rejection Rate', subtitle: 'Qty rejected ÷ qty received',
      icon: 'pi pi-times-circle', iconClass: 'ic-red-pink',
      format: 'percent', target: 2, higherIsBetter: false,
      good: v => v <= 2, warn: v => v <= 5
    },
  ];

  get allCards(): KpiCard[] {
    return [...this.procurementCards, ...this.inventoryCards, ...this.financeCards];
  }

  get goodCount(): number  { return this.kpis ? this.allCards.filter(c => c.good(this.getValue(c))).length : 0; }
  get warnCount(): number  { return this.kpis ? this.allCards.filter(c => !c.good(this.getValue(c)) && c.warn(this.getValue(c))).length : 0; }
  get badCount():  number  { return this.kpis ? this.allCards.filter(c => !c.good(this.getValue(c)) && !c.warn(this.getValue(c))).length : 0; }
  get healthScore(): number { return this.kpis ? Math.round((this.goodCount / this.allCards.length) * 100) : 0; }

  constructor(private reports: ReportsService, private msg: MessageService) {}

  ngOnInit(): void {
    const cached = this.readCache();
    if (cached) {
      this.kpis = cached;
      this.isLoading = false;
      this.buildCharts();
      this.isRefreshing = true;
      this.fetchFresh();
    } else {
      this.load();
    }
  }

  load(): void {
    this.isLoading = true;
    this.fetchFresh();
  }

  private fetchFresh(): void {
    this.reports.getKpis().subscribe({
      next: r => {
        this.isLoading    = false;
        this.isRefreshing = false;
        if (r.success) {
          this.kpis = r.result;
          this.lastUpdated = new Date();
          this.writeCache(r.result);
          this.buildCharts();
        }
      },
      error: () => {
        this.isLoading    = false;
        this.isRefreshing = false;
        if (!this.kpis) {
          this.msg.add({ severity: 'error', summary: 'Error', detail: 'Failed to load KPIs' });
        }
      }
    });
  }

  private readCache(): KpiDashboardModel | null {
    try {
      const raw = sessionStorage.getItem(this.CACHE_KEY);
      if (!raw) return null;
      const { data, ts } = JSON.parse(raw);
      return Date.now() - ts < this.CACHE_TTL ? (data as KpiDashboardModel) : null;
    } catch { return null; }
  }

  private writeCache(data: KpiDashboardModel): void {
    try {
      sessionStorage.setItem(this.CACHE_KEY, JSON.stringify({ data, ts: Date.now() }));
    } catch { /* ignore quota errors */ }
  }

  getValue(card: KpiCard): number {
    return this.kpis ? (this.kpis[card.key] as number) : 0;
  }

  formatValue(card: KpiCard): string {
    const v = this.getValue(card);
    switch (card.format) {
      case 'percent': return v.toFixed(1) + '%';
      case 'times':   return v.toFixed(1) + 'x';
      case 'count':   return v.toString();
      default:        return v.toFixed(1);
    }
  }

  formatTarget(card: KpiCard): string {
    switch (card.format) {
      case 'percent': return card.target + '%';
      case 'times':   return card.target + 'x';
      case 'count':   return card.target === 0 ? 'Zero' : card.target.toString();
      default:        return card.target + ' days';
    }
  }

  statusClass(card: KpiCard): string {
    const v = this.getValue(card);
    if (card.good(v)) return 'status-good';
    if (card.warn(v)) return 'status-warn';
    return 'status-bad';
  }

  statusLabel(card: KpiCard): string {
    const v = this.getValue(card);
    if (card.good(v)) return 'On Track';
    if (card.warn(v)) return 'Watch';
    return 'Alert';
  }

  statusIcon(card: KpiCard): string {
    const v = this.getValue(card);
    if (card.good(v)) return 'pi pi-check-circle';
    if (card.warn(v)) return 'pi pi-info-circle';
    return 'pi pi-exclamation-circle';
  }

  progressPct(card: KpiCard): number {
    const v = this.getValue(card);
    if (card.format === 'count') {
      if (v === 0) return 100;
      return Math.max(0, Math.round((1 - v / 10) * 100));
    }
    if (card.higherIsBetter) {
      return Math.min(100, Math.round((v / card.target) * 100));
    } else {
      if (v === 0) return 100;
      if (v <= card.target) return 100;
      return Math.max(0, Math.round((card.target / v) * 100));
    }
  }

  private buildCharts(): void {
    const good = this.goodCount;
    const warn = this.warnCount;
    const bad  = this.badCount;

    this.donutData = {
      labels: ['On Track', 'Watch', 'Alert'],
      datasets: [{
        data: [good, warn, bad],
        backgroundColor: ['#10b981', '#f59e0b', '#ef4444'],
        borderColor: ['#ffffff', '#ffffff', '#ffffff'],
        borderWidth: 3,
        hoverOffset: 10
      }]
    };
    this.donutOptions = {
      responsive: false,
      maintainAspectRatio: false,
      cutout: '74%',
      plugins: {
        legend: { display: false },
        tooltip: {
          backgroundColor: '#1e293b', titleColor: '#f8fafc', bodyColor: '#cbd5e1',
          padding: 14, cornerRadius: 12, boxPadding: 4,
          callbacks: { label: (ctx: any) => ` ${ctx.raw} KPI${ctx.raw !== 1 ? 's' : ''} — ${ctx.label}` }
        }
      }
    };
  }
}
