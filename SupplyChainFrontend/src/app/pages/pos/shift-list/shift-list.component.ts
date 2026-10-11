import { Component, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { CalendarModule } from 'primeng/calendar';
import { DropdownModule } from 'primeng/dropdown';
import { TableLazyLoadEvent, TableModule } from 'primeng/table';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';
import { catchError, map, of } from 'rxjs';

import {
  POS_ROUTE, PosAdminService, PosShift, SHIFT_STATUSES, ShiftStatus, posError, shiftVarianceExceeded
} from '../../../services/pos-admin.service';
import { FLOW, flowLabel, flowTone } from '../../../shared/flow';
import { ymd } from '../../services/service-orders/service-order-shared';

export interface ShiftListRow extends PosShift { varianceHigh: boolean; }

/** A38 FSD §17.7.3 — every POS shift with store / status / date filters; a variance beyond the threshold is highlighted. */
@Component({
  selector: 'app-pos-shift-list',
  standalone: true,
  imports: [CommonModule, RouterModule, FormsModule, ButtonModule, CalendarModule, DropdownModule, TableModule, ToastModule, TooltipModule, ...FLOW],
  template: `
<p-toast></p-toast>

<sf-page [crumbs]="['Point of Sale', 'Shifts']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button icon="pi pi-refresh" label="Refresh" [text]="true" (onClick)="load()"></p-button>
    <span class="sf-sep"></span>
    <p-button icon="pi pi-filter-slash" label="Clear filters" [text]="true" (onClick)="resetFilters()" data-testid="reset"></p-button>
  </div>

  <div class="sf-body">
    <div class="sf-lr-head">
      <div>
        <h1>Shifts <span class="sf-count" data-testid="shift-count">{{ totalRecords }}</span></h1>
        <div class="sf-sub">Till sessions from opening float to cash count, and what is still in dispute</div>
      </div>
      <div class="sf-filters">
        <button type="button" class="sf-chip" [class.on]="!status" (click)="setStatus('')" data-testid="status-chip-ALL">All</button>
        <button *ngFor="let s of statuses" type="button" class="sf-chip" [class.on]="status === s"
                (click)="setStatus(s)" [attr.data-testid]="'status-chip-' + s">{{ label(s) }}</button>
      </div>
    </div>

    <div class="sf-filters">
      <p-dropdown [options]="storeOptions" [(ngModel)]="storeUuid" optionLabel="label" optionValue="value" [showClear]="true"
                  placeholder="All stores" (onChange)="applyFilters()" appendTo="body" data-testid="store-filter"></p-dropdown>
      <p-calendar [(ngModel)]="dateRange" selectionMode="range" [readonlyInput]="true" [showIcon]="true" [showButtonBar]="true"
                  dateFormat="dd M yy" placeholder="Opened between" (ngModelChange)="onDateChange()" appendTo="body"
                  data-testid="dates"></p-calendar>
    </div>

    <div class="sf-card p0">
      <p-table [value]="shifts" [loading]="isLoading" [lazy]="true" [totalRecords]="totalRecords" [rows]="pageSize"
               (onLazyLoad)="onLazyLoad($event)" [paginator]="true" [rowsPerPageOptions]="[25, 50, 100]"
               [tableStyle]="{ 'min-width': '72rem' }" data-testid="shift-table">
        <ng-template pTemplate="header">
          <tr>
            <th>Shift</th><th>Store</th><th>Terminal</th><th>Opened</th><th>Closed</th><th class="text-right">Orders</th>
            <th class="text-right">Net sales</th><th class="text-right">Expected cash</th><th class="text-right">Counted</th>
            <th class="text-right">Variance</th><th>Status</th>
          </tr>
        </ng-template>
        <ng-template pTemplate="body" let-s>
          <tr data-testid="shift-row" [attr.data-variance-high]="s.varianceHigh">
            <td><a class="sf-mono" [routerLink]="[route, 'shifts', s.uuid]"><strong>{{ s.shiftNumber }}</strong></a></td>
            <td>{{ s.storeName }}</td>
            <td>{{ s.terminalName }}</td>
            <td>{{ s.openedAt | date:'dd MMM yyyy, HH:mm' }}<div class="sf-mu sf-sm">{{ s.openedByName }}</div></td>
            <td>{{ s.closedAt ? (s.closedAt | date:'dd MMM, HH:mm') : '—' }}</td>
            <td class="text-right">{{ s.orderCount }}</td>
            <td class="text-right">{{ s.netTotal | number:'1.2-2' }}</td>
            <td class="text-right">{{ s.expectedCashClosing | number:'1.2-2' }}</td>
            <td class="text-right">{{ s.actualCashClosing != null ? (s.actualCashClosing | number:'1.2-2') : '—' }}</td>
            <td class="text-right">
              <span *ngIf="s.cashVariance != null; else noVar" [class.sl-var]="s.varianceHigh"
                    [pTooltip]="s.varianceHigh ? 'Beyond the threshold of ' + (s.varianceThreshold | number:'1.2-2') : ''"
                    data-testid="variance">{{ s.cashVariance | number:'1.2-2' }}</span>
              <ng-template #noVar><span class="sf-mu">—</span></ng-template>
            </td>
            <td><span class="sf-pill" [ngClass]="tone(s.status)" data-testid="status">{{ label(s.status) }}</span></td>
          </tr>
        </ng-template>
        <ng-template pTemplate="emptymessage">
          <tr><td colspan="11" class="sf-empty">No shifts found.</td></tr>
        </ng-template>
      </p-table>
    </div>
  </div>
</sf-page>
  `,
  styles: [`.sl-var { color: var(--sms-danger); font-weight: 600; }`],
  providers: [MessageService]
})
export class PosShiftListComponent implements OnInit {
  private readonly service = inject(PosAdminService);
  private readonly activated = inject(ActivatedRoute);
  private readonly messages = inject(MessageService);

  readonly route = POS_ROUTE;
  readonly statuses = SHIFT_STATUSES;
  readonly tone = flowTone;
  readonly label = flowLabel;

  shifts: ShiftListRow[] = [];
  totalRecords = 0;
  page = 1;
  pageSize = 25;
  isLoading = true;

  status: ShiftStatus | '' = '';
  storeUuid: string | null = null;
  dateRange: Date[] | null = null;
  storeOptions: { label: string; value: string }[] = [];

  ngOnInit(): void {
    // ?store=… from the store page's "all shifts" link; ?status=DISPUTED from elsewhere.
    const qp = this.activated.snapshot.queryParamMap;
    this.storeUuid = qp.get('store');
    const st = qp.get('status') as ShiftStatus | null;
    if (st && SHIFT_STATUSES.includes(st)) this.status = st;
    storeOptions$(this.service).subscribe(o => this.storeOptions = o);
  }

  load(): void {
    this.isLoading = true;
    const [from, to] = this.dateRange ?? [];
    this.service.getShifts({
      storeUuid: this.storeUuid, status: this.status, from: ymd(from) ?? null, to: ymd(to ?? from) ?? null,
      page: this.page, pageSize: this.pageSize
    }).subscribe({
      next: res => {
        this.isLoading = false;
        this.shifts = (res.result?.data ?? []).map(s => ({ ...s, varianceHigh: shiftVarianceExceeded(s) }));
        this.totalRecords = res.result?.totalRecords ?? 0;
      },
      error: err => {
        this.isLoading = false;
        this.shifts = [];
        this.totalRecords = 0;
        this.messages.add({ severity: 'error', summary: 'Error', detail: posError(err, 'Failed to load shifts.') });
      }
    });
  }

  onLazyLoad(event: TableLazyLoadEvent): void {
    this.pageSize = event.rows ?? this.pageSize;
    this.page = Math.floor((event.first ?? 0) / this.pageSize) + 1;
    this.load();
  }

  onDateChange(): void {
    if (this.dateRange && this.dateRange.length === 2 && !this.dateRange[1]) return;
    this.applyFilters();
  }

  applyFilters(): void { this.page = 1; this.load(); }

  setStatus(s: ShiftStatus | ''): void { this.status = s; this.applyFilters(); }

  resetFilters(): void { this.status = ''; this.storeUuid = null; this.dateRange = null; this.applyFilters(); }
}

/** Every store (first 200) for a filter dropdown. */
export function storeOptions$(service: PosAdminService) {
  return service.getStores({ page: 1, pageSize: 200 }).pipe(
    map(res => (res.result?.data ?? []).map(s => ({ label: `${s.name} (${s.code})`, value: s.uuid }))),
    catchError(() => of([] as { label: string; value: string }[]))
  );
}
