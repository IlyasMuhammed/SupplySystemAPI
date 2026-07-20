import { Component, EventEmitter, Input, OnChanges, Output, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router } from '@angular/router';
import { SidebarModule } from 'primeng/sidebar';
import { ChartModule } from 'primeng/chart';
import { TagModule } from 'primeng/tag';
import {
  ScorecardService,
  SupplierScorecardDetailModel
} from '../../../../services/scorecard.service';

// Each dimension's max raw points (FSD Section 4.1 / SC-004) — used to normalise the radar chart
// axes to a common 0-100 scale so dimensions with different maxima (e.g. Price maxes at 15,
// Delivery at 25) are visually comparable.
const DIMENSION_MAX: Record<string, number> = {
  Delivery: 25, Quantity: 25, Quality: 25, Price: 15, Documentation: 10
};

@Component({
  selector: 'app-scorecard-detail-panel',
  standalone: true,
  imports: [CommonModule, SidebarModule, ChartModule, TagModule],
  templateUrl: './scorecard-detail-panel.component.html',
  styleUrls: ['./scorecard-detail-panel.component.scss']
})
export class ScorecardDetailPanelComponent implements OnChanges {
  @Input() supplierId: string | null = null;
  @Input() visible = false;
  @Output() visibleChange = new EventEmitter<boolean>();

  isLoading  = false;
  loadFailed = false;
  detail: SupplierScorecardDetailModel | null = null;

  radarData: any = null;
  radarOptions: any = null;
  lineData: any = null;
  lineOptions: any = null;

  constructor(private scorecard: ScorecardService, private router: Router) {}

  ngOnChanges(changes: SimpleChanges) {
    if (changes['visible'] && this.visible && this.supplierId) {
      this.load();
    }
  }

  onVisibleChange(v: boolean) {
    this.visible = v;
    this.visibleChange.emit(v);
  }

  close() {
    this.visible = false;
    this.visibleChange.emit(false);
  }

  load() {
    if (!this.supplierId) return;
    this.isLoading  = true;
    this.loadFailed = false;
    this.detail     = null;

    this.scorecard.getSupplierDetail(this.supplierId).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) {
          this.detail = res.result;
          this.buildCharts();
        } else {
          this.loadFailed = true;
        }
      },
      error: () => {
        this.isLoading  = false;
        this.loadFailed = true;
      }
    });
  }

  gradeSeverity(grade: string): 'success' | 'info' | 'warn' | 'danger' {
    switch (grade) {
      case 'A': return 'success';
      case 'B': return 'info';
      case 'C': return 'warn';
      default:  return 'danger'; // D, F
    }
  }

  trendIcon(trend: string | null): string {
    if (trend === 'IMPROVING') return 'pi pi-arrow-up';
    if (trend === 'DECLINING') return 'pi pi-arrow-down';
    return 'pi pi-minus';
  }

  trendClass(trend: string | null): string {
    if (trend === 'IMPROVING') return 'trend-up';
    if (trend === 'DECLINING') return 'trend-down';
    return 'trend-flat';
  }

  goToGrn(grnId: string) {
    this.close();
    this.router.navigate(['/portal/pages/warehouse/grn', grnId]);
  }

  private buildCharts() {
    if (!this.detail) return;
    const d = this.detail;
    const documentStyle = getComputedStyle(document.documentElement);
    const textColor = documentStyle.getPropertyValue('--text-color') || '#334155';
    const surfaceBorder = documentStyle.getPropertyValue('--surface-border') || '#e2e8f0';

    const dims = [
      { label: 'Delivery',      value: d.deliveryScore,      max: DIMENSION_MAX['Delivery'] },
      { label: 'Quantity',      value: d.quantityScore,      max: DIMENSION_MAX['Quantity'] },
      { label: 'Quality',       value: d.qualityScore,       max: DIMENSION_MAX['Quality'] },
      { label: 'Price',         value: d.priceScore,         max: DIMENSION_MAX['Price'] },
      { label: 'Documentation', value: d.documentationScore, max: DIMENSION_MAX['Documentation'] }
    ];

    this.radarData = {
      labels: dims.map(x => x.label),
      datasets: [{
        label: 'Score (% of max)',
        data: dims.map(x => x.max > 0 ? Math.round((x.value / x.max) * 100) : 0),
        backgroundColor: 'rgba(59, 130, 246, 0.2)',
        borderColor: '#3b82f6',
        pointBackgroundColor: '#3b82f6',
        pointBorderColor: '#3b82f6'
      }]
    };
    this.radarOptions = {
      responsive: true,
      maintainAspectRatio: false,
      scales: {
        r: {
          min: 0, max: 100,
          angleLines: { color: surfaceBorder },
          grid: { color: surfaceBorder },
          pointLabels: { color: textColor },
          ticks: { display: false }
        }
      },
      plugins: { legend: { display: false } }
    };

    this.lineData = {
      labels: d.trendHistory.map(p => this.formatPeriod(p.periodStart, p.periodEnd)),
      datasets: [{
        label: 'Composite Score',
        data: d.trendHistory.map(p => p.compositeScore),
        fill: false,
        borderColor: '#6366f1',
        backgroundColor: '#6366f1',
        tension: 0.3
      }]
    };
    this.lineOptions = {
      responsive: true,
      maintainAspectRatio: false,
      scales: {
        y: { min: 0, max: 100, ticks: { color: textColor }, grid: { color: surfaceBorder } },
        x: { ticks: { color: textColor }, grid: { display: false } }
      },
      plugins: { legend: { display: false } }
    };
  }

  private formatPeriod(start: string, end: string): string {
    const s = new Date(start);
    return s.toLocaleDateString(undefined, { month: 'short', year: '2-digit' });
  }
}
