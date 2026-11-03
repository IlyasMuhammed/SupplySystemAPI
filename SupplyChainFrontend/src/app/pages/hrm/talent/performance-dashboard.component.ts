import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { SelectModule } from 'primeng/select';

import { FLOW } from '../../../shared/flow';
import { HrmApi } from '../core/hrm.api';
import { HrmLookups } from '../core/hrm-lookups.service';
import { Opt, hrmError } from '../core/hrm.shared';

/** `{ total, submitted, acknowledged }` → percent submitted or further (a whole number). */
export function completionPercent(c: { total?: number; submitted?: number; acknowledged?: number } | null | undefined): number {
  const total = Number(c?.total ?? 0);
  if (!total) return 0;
  return Math.round((Math.min(total, Number(c?.submitted ?? 0) + Number(c?.acknowledged ?? 0)) / total) * 100);
}

/** The answer's distribution: `{ "4": 3 }` or `[{ rating, count }]`, both read, sorted by rating. */
export function distributionRows(d: unknown): { rating: string; count: number; pct: number }[] {
  const rows = Array.isArray(d)
    ? d.map((x: any) => ({ rating: String(x.rating ?? x.bucket), count: Number(x.count ?? 0) }))
    : Object.entries((d ?? {}) as Record<string, number>).map(([rating, count]) => ({ rating, count: Number(count) }));
  const max = Math.max(1, ...rows.map(r => r.count));
  return rows.sort((a, b) => a.rating.localeCompare(b.rating, undefined, { numeric: true })).map(r => ({ ...r, pct: Math.round((r.count / max) * 100) }));
}

/** Task 223 — cycle completion and how the ratings fall. */
@Component({
  selector: 'app-hrm-performance-dashboard',
  standalone: true,
  imports: [CommonModule, FormsModule, SelectModule, ...FLOW],
  providers: [HrmLookups],
  styleUrls: ['../../finance/gl/gl.scss', '../core/hrm.scss'],
  template: `
<sf-page [crumbs]="['HR', 'Performance', 'Dashboard']">
  <div class="sf-body">
    <div class="sf-lr-head"><div><h1>Performance dashboard</h1></div>
      <p-select [options]="cycles" optionLabel="label" optionValue="value" [(ngModel)]="cycleUuid" (ngModelChange)="load()" placeholder="Review cycle" ariaLabel="Review cycle" appendTo="body" data-testid="cycle"></p-select></div>
    <div class="gl-note er" *ngIf="error" role="alert" data-testid="error"><i class="pi pi-exclamation-triangle"></i><div>{{ error }}</div></div>
    <div class="sf-card sf-empty" *ngIf="!cycleUuid && !error">Choose a review cycle.</div>
    <ng-container *ngIf="data">
      <div class="sf-grid sf-tiles">
        <div class="sf-tile"><span class="l">Completion</span><span class="v" data-testid="pct">{{ percent }}%</span></div>
        <div class="sf-tile"><span class="l">Reviews</span><span class="v">{{ data.completion?.total ?? 0 }}</span></div>
        <div class="sf-tile"><span class="l">Submitted</span><span class="v">{{ data.completion?.submitted ?? 0 }}</span></div>
        <div class="sf-tile"><span class="l">Acknowledged</span><span class="v">{{ data.completion?.acknowledged ?? 0 }}</span></div>
      </div>
      <section class="sf-card"><div class="sf-ch"><h2>Rating distribution</h2></div>
        <div *ngFor="let b of bars" class="hrm-bar-row" [attr.data-testid]="'bar-' + b.rating"><span>Rating {{ b.rating }}</span><div><div class="hrm-bar" [style.width.%]="b.pct"></div></div><span>{{ b.count }}</span></div>
        <div class="sf-empty" *ngIf="!bars.length">No ratings yet.</div>
      </section>
    </ng-container>
  </div>
</sf-page>`
})
export class PerformanceDashboardComponent implements OnInit {
  private readonly api = inject(HrmApi);
  private readonly lookups = inject(HrmLookups);

  cycles: Opt[] = [];
  cycleUuid: string | null = null;
  data: any = null;
  bars: { rating: string; count: number; pct: number }[] = [];
  percent = 0;
  error = '';

  ngOnInit(): void {
    this.lookups.load('cycles').subscribe(l => { this.cycles = l; if (l.length && !this.cycleUuid) { this.cycleUuid = l[0].value; this.load(); } });
  }

  load(): void {
    if (!this.cycleUuid) { this.data = null; return; }
    this.error = '';
    this.api.get<any>('hrm/performance/dashboard', { cycleUuid: this.cycleUuid }).subscribe({
      next: d => { this.data = d; this.percent = completionPercent(d?.completion); this.bars = distributionRows(d?.ratingDistribution ?? d?.distribution); },
      error: err => { this.data = null; this.error = hrmError(err, 'The dashboard could not be loaded.'); }
    });
  }
}
