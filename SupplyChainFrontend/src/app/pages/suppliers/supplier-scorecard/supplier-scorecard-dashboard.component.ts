import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TableModule } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { CalendarModule } from 'primeng/calendar';
import { SkeletonModule } from 'primeng/skeleton';
import { ToastModule } from 'primeng/toast';
import { MessageService } from 'primeng/api';
import {
  ScorecardService,
  SupplierScorecardRankingItem
} from '../../../services/scorecard.service';
import { ScorecardDetailPanelComponent } from './scorecard-detail-panel/scorecard-detail-panel.component';

type Preset = '3m' | '6m' | '12m' | 'custom';

interface GradeCount { grade: string; count: number; }

@Component({
  selector: 'app-supplier-scorecard-dashboard',
  standalone: true,
  imports: [
    CommonModule, FormsModule, ButtonModule, TableModule, TagModule,
    CalendarModule, SkeletonModule, ToastModule, ScorecardDetailPanelComponent
  ],
  templateUrl: './supplier-scorecard-dashboard.component.html',
  styleUrls: ['./supplier-scorecard-dashboard.component.scss'],
  providers: [MessageService]
})
export class SupplierScorecardDashboardComponent implements OnInit {
  isLoading = true;
  isRecalculating = false;
  suppliers: SupplierScorecardRankingItem[] = [];
  periodStart: Date | null = null;
  periodEnd: Date | null = null;

  activePreset: Preset = '3m';
  customStart: Date | null = null;
  customEnd: Date | null = null;

  panelVisible = false;
  selectedSupplierId: string | null = null;

  constructor(private scorecard: ScorecardService, private msg: MessageService) {}

  ngOnInit(): void {
    this.applyPreset('3m');
  }

  applyPreset(preset: Preset): void {
    this.activePreset = preset;
    const end = new Date();
    const start = new Date();
    const months = preset === '3m' ? 3 : preset === '6m' ? 6 : preset === '12m' ? 12 : 3;
    start.setMonth(start.getMonth() - months);
    this.load(start, end);
  }

  applyCustomRange(): void {
    if (!this.customStart || !this.customEnd) return;
    this.activePreset = 'custom';
    this.load(this.customStart, this.customEnd);
  }

  private load(start: Date, end: Date): void {
    this.isLoading = true;
    this.periodStart = start;
    this.periodEnd = end;

    this.scorecard.getRanking({
      periodStart: this.toIsoDate(start),
      periodEnd:   this.toIsoDate(end)
    }).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) {
          this.suppliers = res.result.suppliers;
        } else {
          this.msg.add({ severity: 'error', summary: 'Error', detail: 'Failed to load scorecard data' });
        }
      },
      error: () => {
        this.isLoading = false;
        this.msg.add({ severity: 'error', summary: 'Error', detail: 'Failed to load scorecard data' });
      }
    });
  }

  recalculate(): void {
    this.isRecalculating = true;
    this.scorecard.recalculateAll().subscribe({
      next: (res) => {
        this.isRecalculating = false;
        if (res.success) {
          this.msg.add({
            severity: 'success', summary: 'Recalculated',
            detail: `${res.result.suppliersRecalculated} supplier(s) recalculated for the last period.`
          });
          if (this.periodStart && this.periodEnd) this.load(this.periodStart, this.periodEnd);
        }
      },
      error: () => {
        this.isRecalculating = false;
        this.msg.add({ severity: 'error', summary: 'Error', detail: 'Recalculation failed' });
      }
    });
  }

  openDetail(supplier: SupplierScorecardRankingItem): void {
    this.selectedSupplierId = supplier.supplierId;
    this.panelVisible = true;
  }

  // ── Summary cards (derived client-side from the already-fetched ranking) ────

  get totalSuppliersScored(): number {
    return this.suppliers.length;
  }

  get averageCompositeScore(): number {
    if (!this.suppliers.length) return 0;
    return Math.round((this.suppliers.reduce((sum, s) => sum + s.compositeScore, 0) / this.suppliers.length) * 10) / 10;
  }

  get gradeDistribution(): GradeCount[] {
    const order = ['A', 'B', 'C', 'D', 'F'];
    const counts = new Map<string, number>();
    for (const s of this.suppliers) counts.set(s.grade, (counts.get(s.grade) ?? 0) + 1);
    return order.filter(g => counts.has(g)).map(g => ({ grade: g, count: counts.get(g)! }));
  }

  // A flat (0.0) delta is neither an improvement nor a decline, so it must not win either card —
  // otherwise the one supplier with any delta data at all (even a flat one) trivially "wins" both.
  get mostImproved(): SupplierScorecardRankingItem | null {
    const candidates = this.suppliers.filter(s => s.scoreDelta !== null && s.scoreDelta > 0);
    if (!candidates.length) return null;
    return candidates.reduce((best, s) => (s.scoreDelta! > best.scoreDelta! ? s : best));
  }

  get mostDeclined(): SupplierScorecardRankingItem | null {
    const candidates = this.suppliers.filter(s => s.scoreDelta !== null && s.scoreDelta < 0);
    if (!candidates.length) return null;
    return candidates.reduce((worst, s) => (s.scoreDelta! < worst.scoreDelta! ? s : worst));
  }

  // ── Presentation helpers ──────────────────────────────────────────────────

  gradeSeverity(grade: string): 'success' | 'info' | 'warn' | 'danger' {
    switch (grade) {
      case 'A': return 'success';
      case 'B': return 'info';
      case 'C': return 'warn';
      default:  return 'danger'; // D, F
    }
  }

  gradeClass(grade: string): string {
    switch (grade) {
      case 'A': return 'grade-a';
      case 'B': return 'grade-b';
      case 'C': return 'grade-c';
      case 'D': return 'grade-d';
      default:  return 'grade-f';
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

  private toIsoDate(d: Date): string {
    return d.toISOString().slice(0, 10);
  }
}
