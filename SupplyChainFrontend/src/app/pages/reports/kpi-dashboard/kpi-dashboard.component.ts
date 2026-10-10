import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { forkJoin, of } from 'rxjs';
import { catchError } from 'rxjs/operators';
import { ButtonModule } from 'primeng/button';
import { SkeletonModule } from 'primeng/skeleton';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { ChartModule } from 'primeng/chart';
import { MessageService } from 'primeng/api';
import { ReportsService, KpiDashboardModel, KpiMetric, OperationsKpiModel } from '../../../services/reports.service';
import { FLOW } from '../../../shared/flow';

/** Everything the page reads a KPI from: the original KPIs and the operations KPIs. */
export interface KpiSources {
  core: KpiDashboardModel | null;
  ops:  OperationsKpiModel | null;
}

export type KpiStatus = 'good' | 'warn' | 'bad' | 'none';

export interface KpiCard {
  id:             string;
  label:          string;
  subtitle:       string;
  icon:           string;
  iconClass:      string;
  format:         'days' | 'percent' | 'count' | 'times';
  target:         number;
  higherIsBetter: boolean;
  good:           (v: number) => boolean;
  warn:           (v: number) => boolean;
  /** The value, or null when there was nothing to measure. */
  value:          (s: KpiSources) => number | null;
  /** How many documents the value was worked out from, when the server says. */
  basis?:         (s: KpiSources) => number | null;
}

export interface KpiSection {
  id:     string;
  title:  string;
  icon:   string;
  color:  string;
  /** Null when the user may not see this area — the section is then left out entirely. */
  available: (s: KpiSources) => boolean;
  cards:  KpiCard[];
}

const metric = (m: KpiMetric | null | undefined) => m?.value ?? null;
const basis  = (m: KpiMetric | null | undefined) => (m ? m.basis : null);

@Component({
  selector: 'app-kpi-dashboard',
  standalone: true,
  imports: [CommonModule, ButtonModule, SkeletonModule, ToastModule, TooltipModule, ChartModule, ...FLOW],
  templateUrl: './kpi-dashboard.component.html',
  styleUrls: ['./kpi-dashboard.component.scss'],
  providers: [MessageService]
})
export class KpiDashboardComponent implements OnInit {
  sources:      KpiSources = { core: null, ops: null };
  isLoading     = true;
  isRefreshing  = false;
  lastUpdated:  Date | null = null;

  /** The rolling window of the operations KPIs (sales, fulfilment, manufacturing, receivables). */
  windowDays    = 90;
  readonly windows = [30, 90, 180, 365];

  donutData:    any = null;
  donutOptions: any = null;

  private readonly CACHE_KEY = 'kpi_dashboard_v2';
  private readonly CACHE_TTL = 5 * 60 * 1000;

  readonly sections: KpiSection[] = [
    {
      id: 'sales', title: 'Sales', icon: 'pi pi-chart-line', color: 'var(--sms-violet)',
      available: s => !!s.ops?.sales,
      cards: [
        {
          id: 'quoteWin', label: 'Quote Win Rate', subtitle: 'Decided quotations the customer accepted',
          icon: 'pi pi-thumbs-up', iconClass: 'ic-blue-purple', format: 'percent', target: 40, higherIsBetter: true,
          good: v => v >= 40, warn: v => v >= 25,
          value: s => metric(s.ops?.sales?.quoteWinRate), basis: s => basis(s.ops?.sales?.quoteWinRate)
        },
        {
          id: 'inquiryConversion', label: 'Inquiry Conversion', subtitle: 'Decided inquiries that were quoted',
          icon: 'pi pi-inbox', iconClass: 'ic-purple-pink', format: 'percent', target: 70, higherIsBetter: true,
          good: v => v >= 70, warn: v => v >= 50,
          value: s => metric(s.ops?.sales?.inquiryConversionRate), basis: s => basis(s.ops?.sales?.inquiryConversionRate)
        },
        {
          id: 'cancellation', label: 'Order Cancellations', subtitle: 'Placed sale orders later cancelled',
          icon: 'pi pi-ban', iconClass: 'ic-red-pink', format: 'percent', target: 5, higherIsBetter: false,
          good: v => v <= 5, warn: v => v <= 10,
          value: s => metric(s.ops?.sales?.orderCancellationRate), basis: s => basis(s.ops?.sales?.orderCancellationRate)
        },
        {
          id: 'lateOpen', label: 'Late Open Orders', subtitle: 'Open orders past their expected delivery date',
          icon: 'pi pi-clock', iconClass: 'ic-red-orange', format: 'percent', target: 5, higherIsBetter: false,
          good: v => v <= 5, warn: v => v <= 15,
          value: s => metric(s.ops?.sales?.lateOpenOrderRate), basis: s => basis(s.ops?.sales?.lateOpenOrderRate)
        }
      ]
    },
    {
      id: 'fulfilment', title: 'Fulfilment', icon: 'pi pi-truck', color: 'var(--sms-primary-text)',
      available: s => !!s.ops?.fulfilment,
      cards: [
        {
          id: 'onTimeShip', label: 'On-Time Shipment', subtitle: 'Deliveries shipped by their promised date',
          icon: 'pi pi-send', iconClass: 'ic-teal-green', format: 'percent', target: 95, higherIsBetter: true,
          good: v => v >= 95, warn: v => v >= 85,
          value: s => metric(s.ops?.fulfilment?.onTimeShipmentRate), basis: s => basis(s.ops?.fulfilment?.onTimeShipmentRate)
        },
        {
          id: 'inFull', label: 'Delivered In Full', subtitle: 'Completed deliveries with every line in full',
          icon: 'pi pi-check-square', iconClass: 'ic-green-teal', format: 'percent', target: 98, higherIsBetter: true,
          good: v => v >= 98, warn: v => v >= 90,
          value: s => metric(s.ops?.fulfilment?.inFullRate), basis: s => basis(s.ops?.fulfilment?.inFullRate)
        },
        {
          id: 'orderToShip', label: 'Order-to-Ship Time', subtitle: 'Avg days from sale order to goods issue',
          icon: 'pi pi-stopwatch', iconClass: 'ic-blue-cyan', format: 'days', target: 3, higherIsBetter: false,
          good: v => v <= 3, warn: v => v <= 7,
          value: s => metric(s.ops?.fulfilment?.orderToShipDays), basis: s => basis(s.ops?.fulfilment?.orderToShipDays)
        }
      ]
    },
    {
      id: 'manufacturing', title: 'Manufacturing', icon: 'pi pi-cog', color: 'var(--sms-warn)',
      available: s => !!s.ops?.manufacturing,
      cards: [
        {
          id: 'prodOnTime', label: 'On-Time Completion', subtitle: 'Production orders finished by their required date',
          icon: 'pi pi-calendar', iconClass: 'ic-orange-yellow', format: 'percent', target: 90, higherIsBetter: true,
          good: v => v >= 90, warn: v => v >= 75,
          value: s => metric(s.ops?.manufacturing?.onTimeCompletionRate), basis: s => basis(s.ops?.manufacturing?.onTimeCompletionRate)
        },
        {
          id: 'planAttainment', label: 'Plan Attainment', subtitle: 'Produced ÷ planned quantity',
          icon: 'pi pi-bullseye', iconClass: 'ic-blue-purple', format: 'percent', target: 95, higherIsBetter: true,
          good: v => v >= 95, warn: v => v >= 85,
          value: s => metric(s.ops?.manufacturing?.planAttainment), basis: s => basis(s.ops?.manufacturing?.planAttainment)
        },
        {
          id: 'yield', label: 'First-Pass Yield', subtitle: 'Accepted ÷ produced at quality inspection',
          icon: 'pi pi-verified', iconClass: 'ic-green-dark', format: 'percent', target: 98, higherIsBetter: true,
          good: v => v >= 98, warn: v => v >= 93,
          value: s => metric(s.ops?.manufacturing?.firstPassYield), basis: s => basis(s.ops?.manufacturing?.firstPassYield)
        },
        {
          id: 'prodCycle', label: 'Production Cycle Time', subtitle: 'Avg days from start to finish',
          icon: 'pi pi-hourglass', iconClass: 'ic-pink-red', format: 'days', target: 5, higherIsBetter: false,
          good: v => v <= 5, warn: v => v <= 10,
          value: s => metric(s.ops?.manufacturing?.cycleTimeDays), basis: s => basis(s.ops?.manufacturing?.cycleTimeDays)
        }
      ]
    },
    {
      id: 'receivables', title: 'Receivables', icon: 'pi pi-wallet', color: 'var(--sms-teal)',
      available: s => !!s.ops?.receivables,
      cards: [
        {
          id: 'dso', label: 'Days Sales Outstanding', subtitle: 'Days of sales still waiting to be collected',
          icon: 'pi pi-money-bill', iconClass: 'ic-teal-green', format: 'days', target: 45, higherIsBetter: false,
          good: v => v <= 45, warn: v => v <= 60,
          value: s => metric(s.ops?.receivables?.daysSalesOutstanding), basis: s => basis(s.ops?.receivables?.daysSalesOutstanding)
        },
        {
          id: 'overdueAr', label: 'Overdue Receivables', subtitle: 'Share of what customers owe that is past due',
          icon: 'pi pi-exclamation-triangle', iconClass: 'ic-red-orange', format: 'percent', target: 10, higherIsBetter: false,
          good: v => v <= 10, warn: v => v <= 25,
          value: s => metric(s.ops?.receivables?.overdueRate), basis: s => basis(s.ops?.receivables?.overdueRate)
        }
      ]
    },
    {
      id: 'procurement', title: 'Procurement', icon: 'pi pi-shopping-cart', color: 'var(--sms-violet)',
      available: s => !!s.core,
      cards: [
        {
          id: 'poCycle', label: 'PO Cycle Time', subtitle: 'Avg days from PR to PO creation',
          icon: 'pi pi-clock', iconClass: 'ic-blue-purple', format: 'days', target: 5, higherIsBetter: false,
          good: v => v <= 5, warn: v => v <= 10, value: s => s.core?.poCycleTimeDays ?? null
        },
        {
          id: 'supplierOtd', label: 'Supplier On-Time Delivery', subtitle: 'GRNs received on or before the PO delivery date',
          icon: 'pi pi-truck', iconClass: 'ic-teal-green', format: 'percent', target: 90, higherIsBetter: true,
          good: v => v >= 90, warn: v => v >= 75, value: s => s.core?.supplierOnTimeDeliveryRate ?? null
        },
        {
          id: 'poFill', label: 'PO Fill Rate', subtitle: 'PO lines fully received',
          icon: 'pi pi-check-square', iconClass: 'ic-pink-red', format: 'percent', target: 95, higherIsBetter: true,
          good: v => v >= 95, warn: v => v >= 80, value: s => s.core?.poFillRate ?? null
        },
        {
          id: 'budget', label: 'Budget Variance', subtitle: 'Actual PO spend vs requisition estimate',
          icon: 'pi pi-chart-line', iconClass: 'ic-red-orange', format: 'percent', target: 5, higherIsBetter: false,
          good: v => v <= 5, warn: v => v <= 15, value: s => s.core?.budgetVariancePercent ?? null
        }
      ]
    },
    {
      id: 'inventory', title: 'Inventory & Receiving', icon: 'pi pi-box', color: 'var(--sms-ok)',
      available: s => !!s.core,
      cards: [
        {
          id: 'turnover', label: 'Stock Turnover', subtitle: 'Annual spend ÷ avg inventory value',
          icon: 'pi pi-sync', iconClass: 'ic-blue-cyan', format: 'times', target: 4, higherIsBetter: true,
          good: v => v >= 4, warn: v => v >= 2, value: s => s.core?.stockTurnoverRatio ?? null
        },
        {
          id: 'accuracy', label: 'Inventory Accuracy', subtitle: 'Items without rejected adjustments',
          icon: 'pi pi-database', iconClass: 'ic-green-teal', format: 'percent', target: 98, higherIsBetter: true,
          good: v => v >= 98, warn: v => v >= 95, value: s => s.core?.inventoryAccuracy ?? null
        },
        {
          id: 'reorder', label: 'Reorder Triggers', subtitle: 'Items currently below reorder point',
          icon: 'pi pi-exclamation-triangle', iconClass: 'ic-orange-yellow', format: 'count', target: 0, higherIsBetter: false,
          good: v => v === 0, warn: v => v <= 5, value: s => s.core ? s.core.reorderTriggerCount : null
        },
        {
          id: 'grnReject', label: 'GRN Rejection Rate', subtitle: 'Qty rejected ÷ qty received',
          icon: 'pi pi-times-circle', iconClass: 'ic-red-pink', format: 'percent', target: 2, higherIsBetter: false,
          good: v => v <= 2, warn: v => v <= 5, value: s => s.core?.grnRejectionRate ?? null
        }
      ]
    },
    {
      id: 'payables', title: 'Payables', icon: 'pi pi-credit-card', color: 'var(--sms-warn)',
      available: s => !!s.core,
      cards: [
        {
          id: 'invoiceProcessing', label: 'Invoice Processing', subtitle: 'Avg days from receipt to approval',
          icon: 'pi pi-file-edit', iconClass: 'ic-purple-pink', format: 'days', target: 3, higherIsBetter: false,
          good: v => v <= 3, warn: v => v <= 7, value: s => s.core?.invoiceProcessingTimeDays ?? null
        },
        {
          id: 'threeWay', label: '3-Way Match Rate', subtitle: 'Invoices matched to PO + GRN',
          icon: 'pi pi-verified', iconClass: 'ic-green-dark', format: 'percent', target: 90, higherIsBetter: true,
          good: v => v >= 90, warn: v => v >= 75, value: s => s.core?.threeWayMatchRate ?? null
        }
      ]
    }
  ];

  /** The sections this user can see, in display order. */
  get visibleSections(): KpiSection[] {
    return this.sections.filter(s => s.available(this.sources));
  }

  get allCards(): KpiCard[] {
    return this.visibleSections.flatMap(s => s.cards);
  }

  /** KPIs with something to measure — the health score is about these, never about empty areas. */
  get measuredCards(): KpiCard[] { return this.allCards.filter(c => c.value(this.sources) !== null); }

  get goodCount(): number { return this.allCards.filter(c => this.status(c) === 'good').length; }
  get warnCount(): number { return this.allCards.filter(c => this.status(c) === 'warn').length; }
  get badCount():  number { return this.allCards.filter(c => this.status(c) === 'bad').length; }
  get noDataCount(): number { return this.allCards.filter(c => this.status(c) === 'none').length; }
  get healthScore(): number {
    const measured = this.measuredCards.length;
    return measured ? Math.round((this.goodCount / measured) * 100) : 0;
  }

  get hasData(): boolean { return !!(this.sources.core || this.sources.ops); }

  constructor(private reports: ReportsService, private msg: MessageService) {}

  ngOnInit(): void {
    const cached = this.readCache();
    if (cached) {
      this.sources   = cached;
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

  setWindow(days: number): void {
    if (days === this.windowDays) return;
    this.windowDays   = days;
    this.isRefreshing = true;
    this.fetchFresh();
  }

  private fetchFresh(): void {
    // Two reads: the original KPIs and the operations KPIs. Either may fail on its own; the page shows what came back.
    forkJoin({
      core: this.reports.getKpis().pipe(catchError(() => of(null))),
      ops:  this.reports.getOperationsKpis(this.windowDays).pipe(catchError(() => of(null)))
    }).subscribe(r => {
      this.isLoading    = false;
      this.isRefreshing = false;

      const core = r.core?.success ? r.core.result : null;
      const ops  = r.ops?.success  ? r.ops.result  : null;

      if (!core && !ops) {
        if (!this.hasData) this.msg.add({ severity: 'error', summary: 'Error', detail: 'Failed to load KPIs' });
        return;
      }

      this.sources     = { core, ops };
      this.lastUpdated = new Date();
      this.writeCache(this.sources);
      this.buildCharts();
    });
  }

  private readCache(): KpiSources | null {
    try {
      const raw = sessionStorage.getItem(this.CACHE_KEY);
      if (!raw) return null;
      const { data, ts, window } = JSON.parse(raw);
      return Date.now() - ts < this.CACHE_TTL && window === this.windowDays ? (data as KpiSources) : null;
    } catch { return null; }
  }

  private writeCache(data: KpiSources): void {
    try {
      sessionStorage.setItem(this.CACHE_KEY, JSON.stringify({ data, ts: Date.now(), window: this.windowDays }));
    } catch { /* ignore quota errors */ }
  }

  // ── Per-card ──────────────────────────────────────────────────────────────

  getValue(card: KpiCard): number | null {
    return card.value(this.sources);
  }

  getBasis(card: KpiCard): number | null {
    return card.basis ? card.basis(this.sources) : null;
  }

  status(card: KpiCard): KpiStatus {
    const v = this.getValue(card);
    if (v === null) return 'none';
    if (card.good(v)) return 'good';
    if (card.warn(v)) return 'warn';
    return 'bad';
  }

  formatValue(card: KpiCard): string {
    const v = this.getValue(card);
    if (v === null) return '—';
    switch (card.format) {
      case 'percent': return v.toFixed(1) + '%';
      case 'times':   return v.toFixed(1) + 'x';
      case 'count':   return v.toString();
      default:        return v.toFixed(1) + 'd';
    }
  }

  formatTarget(card: KpiCard): string {
    const sign = card.format === 'count' ? '' : card.higherIsBetter ? '≥ ' : '≤ ';
    switch (card.format) {
      case 'percent': return sign + card.target + '%';
      case 'times':   return sign + card.target + 'x';
      case 'count':   return card.target === 0 ? 'Zero' : card.target.toString();
      default:        return sign + card.target + ' days';
    }
  }

  statusClass(card: KpiCard): string { return 'status-' + this.status(card); }

  statusLabel(card: KpiCard): string {
    return { good: 'On Track', warn: 'Watch', bad: 'Alert', none: 'No data' }[this.status(card)];
  }

  statusIcon(card: KpiCard): string {
    return {
      good: 'pi pi-check-circle', warn: 'pi pi-info-circle', bad: 'pi pi-exclamation-circle', none: 'pi pi-minus-circle'
    }[this.status(card)];
  }

  basisText(card: KpiCard): string {
    const n = this.getBasis(card);
    if (n === null) return '';
    return n === 0 ? 'Nothing in this period yet' : `Based on ${n.toLocaleString()} record${n === 1 ? '' : 's'}`;
  }

  progressPct(card: KpiCard): number {
    const v = this.getValue(card);
    if (v === null) return 0;
    if (card.format === 'count') {
      if (v === 0) return 100;
      return Math.max(0, Math.round((1 - v / 10) * 100));
    }
    if (card.higherIsBetter) {
      return card.target > 0 ? Math.min(100, Math.round((v / card.target) * 100)) : 100;
    }
    if (v <= card.target) return 100;
    return Math.max(0, Math.round((card.target / v) * 100));
  }

  sectionHealth(section: KpiSection): { good: number; measured: number } {
    const measured = section.cards.filter(c => this.status(c) !== 'none');
    return { good: measured.filter(c => this.status(c) === 'good').length, measured: measured.length };
  }

  private buildCharts(): void {
    // Chart.js cannot read CSS vars: take the SMS Flow tokens' values now. A chart built before a light/dark switch
    // keeps these until it is built again (next load/refresh).
    const css = getComputedStyle(document.documentElement);
    const token = (name: string, fallback: string) => css.getPropertyValue(name).trim() || fallback;
    const surface = token('--sms-surface', '#ffffff');
    this.donutData = {
      labels: ['On Track', 'Watch', 'Alert'],
      datasets: [{
        data: [this.goodCount, this.warnCount, this.badCount],
        backgroundColor: ['#1d7a4a', '#c27a00', '#b3261e'],
        borderColor: [surface, surface, surface],
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
          backgroundColor: token('--sms-navy', '#13233a'), titleColor: '#ffffff', bodyColor: token('--sms-navy-text', '#c3cfdc'),
          padding: 14, cornerRadius: 12, boxPadding: 4,
          callbacks: { label: (ctx: any) => ` ${ctx.raw} KPI${ctx.raw !== 1 ? 's' : ''} — ${ctx.label}` }
        }
      }
    };
  }
}
