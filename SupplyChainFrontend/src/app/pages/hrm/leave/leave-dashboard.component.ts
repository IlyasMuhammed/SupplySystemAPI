import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
import { SelectModule } from 'primeng/select';

import { FLOW } from '../../../shared/flow';
import { HrmApi, rowsOf } from '../core/hrm.api';
import { HrmLookups } from '../core/hrm-lookups.service';
import { hrmError } from '../core/hrm.shared';
import { toDateOnly } from '../../../shared/date-only';

export interface CalDay { date: string; day: number; names: string[]; }

/** Weeks of a month as a flat list with leading blanks (Sunday first): who is away on each date. */
export function monthGrid(year: number, month: number, rows: { fromDate: string; toDate: string; employeeName: string }[]): (CalDay | null)[] {
  const first = new Date(year, month - 1, 1);
  const days = new Date(year, month, 0).getDate();
  const out: (CalDay | null)[] = Array(first.getDay()).fill(null);
  for (let d = 1; d <= days; d++) {
    const iso = toDateOnly(new Date(year, month - 1, d));
    out.push({ date: iso, day: d, names: rows.filter(r => r.fromDate.slice(0, 10) <= iso && r.toDate.slice(0, 10) >= iso).map(r => r.employeeName) });
  }
  return out;
}

/** Task 155 — team leave as a calendar. The endpoint gives names and dates only, by design. */
@Component({
  selector: 'app-hrm-leave-dashboard',
  standalone: true,
  imports: [CommonModule, FormsModule, RouterModule, ButtonModule, SelectModule, ...FLOW],
  providers: [HrmLookups],
  styleUrls: ['../../finance/gl/gl.scss', '../core/hrm.scss'],
  template: `
<sf-page [crumbs]="['HR', 'Leave', 'Team calendar']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button icon="pi pi-chevron-left" [text]="true" ariaLabel="Previous month" (onClick)="shift(-1)" data-testid="prev"></p-button>
    <p-button icon="pi pi-chevron-right" [text]="true" ariaLabel="Next month" (onClick)="shift(1)" data-testid="next"></p-button>
    <span class="sf-end"><p-select [options]="departments" optionLabel="label" optionValue="value" [(ngModel)]="departmentUuid" (ngModelChange)="load()" placeholder="All departments" ariaLabel="Department" [showClear]="true" [filter]="true" appendTo="body" data-testid="dept"></p-select></span>
  </div>
  <div class="sf-body">
    <div class="sf-lr-head"><div><h1>{{ title }}</h1><div class="sf-sub">Who is away. Approved leave in your scope.</div></div></div>
    <div class="gl-note er" *ngIf="error" role="alert" data-testid="error"><i class="pi pi-exclamation-triangle"></i><div>{{ error }}</div></div>
    <div class="sf-card">
      <div class="hrm-cal" data-testid="calendar">
        <div class="dow" *ngFor="let d of dow">{{ d }}</div>
        <ng-container *ngFor="let c of grid">
          <div class="day blank" *ngIf="!c"></div>
          <div class="day" *ngIf="c" [attr.data-testid]="'day-' + c.date"><b>{{ c.day }}</b><span *ngFor="let n of c.names" [title]="n">{{ n }}</span></div>
        </ng-container>
      </div>
    </div>
  </div>
</sf-page>`
})
export class LeaveDashboardComponent implements OnInit {
  private readonly api = inject(HrmApi);
  private readonly lookups = inject(HrmLookups);

  readonly dow = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];
  year = new Date().getFullYear();
  month = new Date().getMonth() + 1;
  grid: (CalDay | null)[] = [];
  error = '';
  departments: { label: string; value: any }[] = [];
  departmentUuid: string | null = null;

  get title(): string { return new Date(this.year, this.month - 1, 1).toLocaleDateString('en-GB', { month: 'long', year: 'numeric' }); }

  ngOnInit(): void {
    this.lookups.load('departments').subscribe(l => { this.departments = l; });
    this.load();
  }

  shift(d: number): void {
    const x = new Date(this.year, this.month - 1 + d, 1);
    this.year = x.getFullYear();
    this.month = x.getMonth() + 1;
    this.load();
  }

  load(): void {
    const from = toDateOnly(new Date(this.year, this.month - 1, 1));
    const to = toDateOnly(new Date(this.year, this.month, 0));
    this.error = '';
    this.api.get<any>('hrm/leave/calendar', { from, to, departmentUuid: this.departmentUuid }).subscribe({
      next: r => { this.grid = monthGrid(this.year, this.month, rowsOf(r)); },
      error: err => { this.grid = monthGrid(this.year, this.month, []); this.error = hrmError(err, 'The calendar could not be loaded.'); }
    });
  }
}
