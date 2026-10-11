import { Component, OnDestroy, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { ActivatedRoute, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { CalendarModule } from 'primeng/calendar';
import { DropdownModule } from 'primeng/dropdown';
import { IconFieldModule } from 'primeng/iconfield';
import { InputIconModule } from 'primeng/inputicon';
import { InputTextModule } from 'primeng/inputtext';
import { TableLazyLoadEvent, TableModule } from 'primeng/table';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';
import { catchError, map, of } from 'rxjs';

import {
  POS_FLAGS, POS_ORDER_STATUSES, POS_ROUTE, PosAdminService, PosOrderListItem, PosOrderStatus, PosOrderType, posError, posFlagLabel, posFlagTone
} from '../../../services/pos-admin.service';
import { CustomerService } from '../../../services/customer.service';
import { AuthService } from '../../service/auth.service';
import { FLOW, flowLabel, flowTone } from '../../../shared/flow';
import { ymd } from '../../services/service-orders/service-order-shared';
import { storeOptions$ } from '../shift-list/shift-list.component';

interface Option<T = string> { label: string; value: T; }

/** One list row with its flags already resolved (built in load(), not in the template). */
export interface OrderRow extends PosOrderListItem {
  flagPills: { code: string; label: string; tone: string; hint: string }[];
  postingLabel: string;
  postingTone: string;
}

export function orderRow(o: PosOrderListItem): OrderRow {
  const flagPills = (o.flags ?? []).map(code => ({ code, label: posFlagLabel(code), tone: posFlagTone(code), hint: POS_FLAGS[code]?.hint ?? '' }));
  const posted = o.stockPosted && o.financePosted;
  const none = !o.stockPosted && !o.financePosted;
  return {
    ...o, flagPills,
    postingLabel: posted ? 'Posted' : none ? 'Not posted' : o.stockPosted ? 'Stock only' : 'Finance only',
    postingTone: posted ? 'ok' : 'wn'
  };
}

/** A38 FSD §17.7.4 — every POS sale and return with store / terminal / date / customer / status / type / search filters. */
@Component({
  selector: 'app-pos-order-list',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule, ButtonModule, CalendarModule, DropdownModule, IconFieldModule, InputIconModule, InputTextModule,
    TableModule, ToastModule, TooltipModule, ...FLOW
  ],
  template: `
<p-toast></p-toast>

<sf-page [crumbs]="['Point of Sale', 'POS orders']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button icon="pi pi-refresh" label="Refresh" [text]="true" (onClick)="load()"></p-button>
    <span class="sf-sep"></span>
    <p-button icon="pi pi-filter-slash" label="Clear filters" [text]="true" (onClick)="resetFilters()" data-testid="reset"></p-button>
  </div>

  <div class="sf-body">
    <div class="sf-lr-head">
      <div>
        <h1>POS orders <span class="sf-count" data-testid="order-count">{{ totalRecords }}</span></h1>
        <div class="sf-sub">Counter sales and returns from every till, with what needs a supervisor's look</div>
      </div>
      <div class="sf-filters">
        <button type="button" class="sf-chip" [class.on]="!status" (click)="setStatus('')" data-testid="status-chip-ALL">All</button>
        <button *ngFor="let s of statuses" type="button" class="sf-chip" [class.on]="status === s"
                (click)="setStatus(s)" [attr.data-testid]="'status-chip-' + s">{{ label(s) }}</button>
      </div>
    </div>

    <div class="sf-filters">
      <p-iconfield class="sf-search">
        <p-inputicon styleClass="pi pi-search"></p-inputicon>
        <input pInputText type="text" [(ngModel)]="search" (ngModelChange)="onSearchChange()" placeholder="Search order number…"
               data-testid="search" />
      </p-iconfield>
      <p-dropdown [options]="storeOptions" [(ngModel)]="storeUuid" optionLabel="label" optionValue="value" [showClear]="true"
                  placeholder="All stores" (onChange)="onStoreChange()" appendTo="body" data-testid="store-filter"></p-dropdown>
      <p-dropdown [options]="terminalOptions" [(ngModel)]="terminalUuid" optionLabel="label" optionValue="value" [showClear]="true"
                  [disabled]="!storeUuid" placeholder="All terminals" (onChange)="applyFilters()" appendTo="body" data-testid="terminal-filter"></p-dropdown>
      <p-dropdown [options]="customerOptions" [(ngModel)]="customerUuid" optionLabel="label" optionValue="value" [showClear]="true"
                  [filter]="true" filterBy="label" placeholder="All customers" (onChange)="applyFilters()" appendTo="body"
                  data-testid="customer-filter"></p-dropdown>
      <p-dropdown [options]="typeOptions" [(ngModel)]="orderType" optionLabel="label" optionValue="value" (onChange)="applyFilters()"
                  appendTo="body" data-testid="type-filter"></p-dropdown>
      <p-calendar [(ngModel)]="dateRange" selectionMode="range" [readonlyInput]="true" [showIcon]="true" [showButtonBar]="true"
                  dateFormat="dd M yy" placeholder="Sold between" (ngModelChange)="onDateChange()" appendTo="body" data-testid="dates"></p-calendar>
      <span *ngIf="shiftUuid" class="sf-chip on" data-testid="shift-filter">One shift
        <i class="pi pi-times" role="button" aria-label="Clear the shift filter" (click)="clearShift()"></i></span>
    </div>

    <div class="sf-card p0">
      <p-table [value]="orders" [loading]="isLoading" [lazy]="true" [totalRecords]="totalRecords" [rows]="pageSize"
               (onLazyLoad)="onLazyLoad($event)" [paginator]="true" [rowsPerPageOptions]="[25, 50, 100]"
               [tableStyle]="{ 'min-width': '80rem' }" data-testid="order-table">
        <ng-template pTemplate="header">
          <tr>
            <th>Order</th><th>Date</th><th>Store / terminal</th><th>Cashier</th><th>Customer</th><th class="text-right">Total</th>
            <th>Payment</th><th>Status</th><th>Flags</th><th>Posting</th>
          </tr>
        </ng-template>
        <ng-template pTemplate="body" let-o>
          <tr data-testid="order-row">
            <td>
              <a class="sf-mono" [routerLink]="[route, 'orders', o.uuid]"><strong>{{ o.orderNumber }}</strong></a>
              <span *ngIf="o.orderType === 'RETURN'" class="sf-pill vi">Return</span>
              <span *ngIf="o.orderType === 'EXCHANGE'" class="sf-pill te" data-testid="exchange-pill">Exchange</span>
            </td>
            <td>{{ o.createdAt | date:'dd MMM yyyy, HH:mm' }}</td>
            <td>{{ o.storeName }}<div class="sf-mu sf-sm">{{ o.terminalName }}</div></td>
            <td>{{ o.cashierName }}</td>
            <td>{{ o.customerName }}</td>
            <td class="text-right">{{ o.grandTotal | number:'1.2-2' }}</td>
            <td><span class="sf-pill" [ngClass]="tone(o.paymentStatus)">{{ label(o.paymentStatus) }}</span></td>
            <td><span class="sf-pill" [ngClass]="tone(o.status)" data-testid="status">{{ label(o.status) }}</span></td>
            <td data-testid="flags">
              <span *ngFor="let f of o.flagPills" class="sf-pill" [ngClass]="f.tone" [pTooltip]="f.hint" [attr.data-flag]="f.code">{{ f.label }}</span>
              <span *ngIf="!o.flagPills.length" class="sf-mu">—</span>
            </td>
            <td><span class="sf-pill" [ngClass]="o.postingTone" data-testid="posting"
                      [pTooltip]="'Stock ' + (o.stockPosted ? 'posted' : 'pending') + ' · finance ' + (o.financePosted ? 'posted' : 'pending')">{{ o.postingLabel }}</span></td>
          </tr>
        </ng-template>
        <ng-template pTemplate="emptymessage">
          <tr><td colspan="10" class="sf-empty">No POS orders found.</td></tr>
        </ng-template>
      </p-table>
    </div>
  </div>
</sf-page>
  `,
  providers: [MessageService]
})
export class PosOrderListComponent implements OnInit, OnDestroy {
  private readonly service = inject(PosAdminService);
  private readonly customers = inject(CustomerService);
  private readonly auth = inject(AuthService);
  private readonly activated = inject(ActivatedRoute);
  private readonly messages = inject(MessageService);

  readonly route = POS_ROUTE;
  readonly statuses = POS_ORDER_STATUSES;
  readonly tone = flowTone;
  readonly label = flowLabel;
  readonly typeOptions: Option<PosOrderType | ''>[] = [
    { label: 'Sales and returns', value: '' }, { label: 'Sales', value: 'SALE' }, { label: 'Returns', value: 'RETURN' },
    { label: 'Exchanges', value: 'EXCHANGE' }
  ];

  orders: OrderRow[] = [];
  totalRecords = 0;
  page = 1;
  pageSize = 25;
  isLoading = true;

  search = '';
  status: PosOrderStatus | '' = '';
  orderType: PosOrderType | '' = '';
  storeUuid: string | null = null;
  terminalUuid: string | null = null;
  customerUuid: string | null = null;
  shiftUuid: string | null = null;
  dateRange: Date[] | null = null;

  storeOptions: Option[] = [];
  terminalOptions: Option[] = [];
  customerOptions: Option[] = [];

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  ngOnInit(): void {
    const qp = this.activated.snapshot.queryParamMap;
    this.shiftUuid = qp.get('shift');
    this.storeUuid = qp.get('store');
    this.customerUuid = qp.get('customer');
    this.search = qp.get('search') ?? '';   // e.g. from a POS sales invoice: the order number
    storeOptions$(this.service).subscribe(o => this.storeOptions = o);
    if (this.storeUuid) this.loadTerminals();
    if (this.auth.hasPermission('CUSTOMER_VIEW')) {
      this.customers.getCustomers({ status: 'ACTIVE', page: 1, pageSize: 200 }).pipe(
        map(res => (res.result?.data ?? []).map(c => ({ label: `${c.name} (${c.code})`, value: c.uuid }))),
        catchError(() => of([] as Option[]))
      ).subscribe(o => this.customerOptions = o);
    }
  }

  ngOnDestroy(): void { if (this.searchTimer) clearTimeout(this.searchTimer); }

  load(): void {
    this.isLoading = true;
    const [from, to] = this.dateRange ?? [];
    this.service.getOrders({
      storeUuid: this.storeUuid, terminalUuid: this.terminalUuid, shiftUuid: this.shiftUuid, customerUuid: this.customerUuid,
      status: this.status, orderType: this.orderType, from: ymd(from) ?? null, to: ymd(to ?? from) ?? null,
      search: this.search.trim() || null, page: this.page, pageSize: this.pageSize
    }).subscribe({
      next: res => {
        this.isLoading = false;
        this.orders = (res.result?.data ?? []).map(orderRow);
        this.totalRecords = res.result?.totalRecords ?? 0;
      },
      error: err => {
        this.isLoading = false;
        this.orders = [];
        this.totalRecords = 0;
        this.messages.add({ severity: 'error', summary: 'Error', detail: posError(err, 'Failed to load POS orders.') });
      }
    });
  }

  private loadTerminals(): void {
    this.terminalOptions = [];
    if (!this.storeUuid || !this.auth.hasAnyPermission('POS_TERMINAL_VIEW', 'POS_TERMINAL_MANAGE')) return;
    this.service.getTerminals(this.storeUuid).subscribe({
      next: res => this.terminalOptions = (res.result ?? []).map(t => ({ label: `${t.name} (${t.code})`, value: t.uuid })),
      error: () => this.terminalOptions = []
    });
  }

  onLazyLoad(event: TableLazyLoadEvent): void {
    this.pageSize = event.rows ?? this.pageSize;
    this.page = Math.floor((event.first ?? 0) / this.pageSize) + 1;
    this.load();
  }

  onStoreChange(): void {
    this.terminalUuid = null;
    this.loadTerminals();
    this.applyFilters();
  }

  onSearchChange(): void {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => this.applyFilters(), 400);
  }

  onDateChange(): void {
    if (this.dateRange && this.dateRange.length === 2 && !this.dateRange[1]) return;
    this.applyFilters();
  }

  applyFilters(): void { this.page = 1; this.load(); }

  setStatus(s: PosOrderStatus | ''): void { this.status = s; this.applyFilters(); }

  clearShift(): void { this.shiftUuid = null; this.applyFilters(); }

  resetFilters(): void {
    this.search = ''; this.status = ''; this.orderType = ''; this.storeUuid = null; this.terminalUuid = null;
    this.customerUuid = null; this.shiftUuid = null; this.dateRange = null; this.terminalOptions = [];
    this.applyFilters();
  }
}
