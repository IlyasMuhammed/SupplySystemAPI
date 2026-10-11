import { Component, OnDestroy, OnInit, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterModule } from '@angular/router';
import { ButtonModule } from 'primeng/button';
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
  PosReturnsAdminService, STORE_CREDIT_ROUTE, STORE_CREDIT_STATUSES, StoreCredit, StoreCreditStatus
} from '../../../services/pos-returns-admin.service';
import { POS_ROUTE, PosAdminService, posError } from '../../../services/pos-admin.service';
import { CustomerService } from '../../../services/customer.service';
import { AuthService } from '../../service/auth.service';
import { ModuleService } from '../../../services/module.service';
import { FLOW, flowLabel, flowTone } from '../../../shared/flow';
import { storeOptions$ } from '../shift-list/shift-list.component';
import { StoreCreditIssueDialogComponent } from '../store-credit-issue-dialog/store-credit-issue-dialog.component';

/** A store credit row with its display bits resolved (built in load(), not in the template). */
export interface StoreCreditRow extends StoreCredit {
  holder: string;
  statusLabel: string;
  statusTone: string;
  expiryLabel: string;
  /** Active and expiring within 30 days. */
  expiringSoon: boolean;
}

const DAY = 24 * 60 * 60 * 1000;

export function storeCreditStatusLabel(s: string): string { return s === 'FULLY_REDEEMED' ? 'Used up' : flowLabel(s); }
export function storeCreditStatusTone(s: string): string { return s === 'FULLY_REDEEMED' ? '' : flowTone(s); }

export function storeCreditRow(c: StoreCredit, now = Date.now()): StoreCreditRow {
  const exp = c.expiresAt ? new Date(c.expiresAt).getTime() : NaN;
  const days = isNaN(exp) ? null : Math.ceil((exp - now) / DAY);
  const active = c.status === 'ACTIVE';
  return {
    ...c,
    holder: c.customerName || 'Bearer',
    statusLabel: storeCreditStatusLabel(c.status),
    statusTone: storeCreditStatusTone(c.status),
    expiryLabel: days == null ? 'Never' : !active ? '' : days < 0 ? 'past due' : days === 0 ? 'today' : `in ${days} day${days === 1 ? '' : 's'}`,
    expiringSoon: active && days != null && days <= 30
  };
}

/**
 * A39 — `/portal/pages/pos/credit-notes`: every POS store credit (CN-…) with status / customer / store / search filters,
 * balance, expiry and status; "Issue store credit" (POS_CREDIT_NOTE_ISSUE + FEATURE_POS_CREDIT_NOTES). Route: POS_CREDIT_NOTE_VIEW.
 */
@Component({
  selector: 'app-pos-store-credit-list',
  standalone: true,
  imports: [
    CommonModule, FormsModule, RouterModule, ButtonModule, DropdownModule, IconFieldModule, InputIconModule, InputTextModule, TableModule,
    ToastModule, TooltipModule, StoreCreditIssueDialogComponent, ...FLOW
  ],
  template: `
<p-toast></p-toast>
<sf-page [crumbs]="['Point of Sale', 'Store credit']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button *ngIf="canIssue" label="Issue store credit" icon="pi pi-plus" (onClick)="issueOpen = true" data-testid="issue-credit"></p-button>
    <p-button icon="pi pi-refresh" label="Refresh" [text]="true" (onClick)="load()"></p-button>
    <span class="sf-sep"></span>
    <p-button icon="pi pi-filter-slash" label="Clear filters" [text]="true" (onClick)="resetFilters()" data-testid="reset"></p-button>
  </div>

  <div class="sf-body">
    <div class="sf-lr-head">
      <div>
        <h1>Store credit <span class="sf-count" data-testid="credit-count">{{ totalRecords }}</span></h1>
        <div class="sf-sub">Vouchers (CN-…) given back at the till instead of money — not Finance credit notes (SCN-…)</div>
      </div>
      <div class="sf-filters">
        <button type="button" class="sf-chip" [class.on]="!status" (click)="setStatus('')" data-testid="status-chip-ALL">All</button>
        <button *ngFor="let s of statuses" type="button" class="sf-chip" [class.on]="status === s.value" (click)="setStatus(s.value)"
                [attr.data-testid]="'status-chip-' + s.value">{{ s.label }}</button>
      </div>
    </div>

    <div class="sf-card sf-empty sf-sm" *ngIf="!featureOn" data-testid="feature-off">
      Store credit is switched off for the organization: tills cannot issue or take it. Existing store credit is still listed.
    </div>

    <div class="sf-filters">
      <p-iconfield class="sf-search">
        <p-inputicon styleClass="pi pi-search"></p-inputicon>
        <input pInputText type="text" [(ngModel)]="search" (ngModelChange)="onSearchChange()" placeholder="Search number or customer…"
               data-testid="search" />
      </p-iconfield>
      <p-dropdown [options]="storeOptions" [(ngModel)]="storeUuid" optionLabel="label" optionValue="value" [showClear]="true"
                  placeholder="All stores" (onChange)="applyFilters()" appendTo="body" data-testid="store-filter"></p-dropdown>
      <p-dropdown [options]="customerOptions" [(ngModel)]="customerUuid" optionLabel="label" optionValue="value" [showClear]="true"
                  [filter]="true" filterBy="label" placeholder="All customers" (onChange)="applyFilters()" appendTo="body"
                  data-testid="customer-filter"></p-dropdown>
    </div>

    <div class="sf-card p0">
      <p-table [value]="rows" [loading]="isLoading" [lazy]="true" [totalRecords]="totalRecords" [rows]="pageSize"
               (onLazyLoad)="onLazyLoad($event)" [paginator]="true" [rowsPerPageOptions]="[25, 50, 100]"
               [tableStyle]="{ 'min-width': '72rem' }" data-testid="credit-table">
        <ng-template pTemplate="header">
          <tr><th>Store credit</th><th>Customer</th><th>Issued</th><th class="text-right">Original</th><th class="text-right">Used</th>
              <th class="text-right">Balance</th><th>Expires</th><th>Status</th><th>From return</th></tr>
        </ng-template>
        <ng-template pTemplate="body" let-c>
          <tr data-testid="credit-row">
            <td><a class="sf-mono" [routerLink]="[creditRoute, c.uuid]"><strong>{{ c.number }}</strong></a></td>
            <td>{{ c.holder }}<span *ngIf="!c.customerUuid" class="sf-pill" pTooltip="Anyone holding the number can spend it">Bearer</span></td>
            <td>{{ c.issuedAt | date:'dd MMM yyyy' }}<div class="sf-mu sf-sm">{{ c.issuedAtStoreName }} · {{ c.issuedByName }}</div></td>
            <td class="text-right">{{ c.originalAmount | number:'1.2-2' }}</td>
            <td class="text-right">{{ c.redeemedAmount | number:'1.2-2' }}</td>
            <td class="text-right"><strong data-testid="balance">{{ c.remainingBalance | number:'1.2-2' }}</strong> <span class="sf-mu sf-sm">{{ c.currencyCode }}</span></td>
            <td data-testid="expiry">{{ c.expiresAt ? (c.expiresAt | date:'dd MMM yyyy') : 'Never' }}
              <div *ngIf="c.expiresAt && c.expiryLabel" class="sf-sm" [class.sf-mu]="!c.expiringSoon" [class.scl-soon]="c.expiringSoon">{{ c.expiryLabel }}</div></td>
            <td><span class="sf-pill" [ngClass]="c.statusTone" data-testid="status">{{ c.statusLabel }}</span></td>
            <td><a *ngIf="c.returnOrderUuid; else standalone" class="sf-mono" [routerLink]="[posRoute, 'orders', c.returnOrderUuid]">{{ c.returnOrderNumber }}</a>
              <ng-template #standalone><span class="sf-mu">Issued directly</span></ng-template></td>
          </tr>
        </ng-template>
        <ng-template pTemplate="emptymessage">
          <tr><td colspan="9" class="sf-empty">No store credit found.</td></tr>
        </ng-template>
      </p-table>
    </div>
  </div>
</sf-page>

<app-pos-store-credit-issue-dialog [(visible)]="issueOpen" [customerPreset]="customerUuid" [storePreset]="storeUuid"
                                   (issued)="onIssued($event)"></app-pos-store-credit-issue-dialog>
  `,
  styles: [`.scl-soon { color: var(--sms-warn); }`],
  providers: [MessageService]
})
export class PosStoreCreditListComponent implements OnInit, OnDestroy {
  private readonly service = inject(PosReturnsAdminService);
  private readonly pos = inject(PosAdminService);
  private readonly customers = inject(CustomerService);
  private readonly auth = inject(AuthService);
  private readonly modules = inject(ModuleService);
  private readonly activated = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly messages = inject(MessageService);

  readonly posRoute = POS_ROUTE;
  readonly creditRoute = STORE_CREDIT_ROUTE;
  readonly statuses = STORE_CREDIT_STATUSES.map(s => ({ value: s, label: storeCreditStatusLabel(s) }));

  rows: StoreCreditRow[] = [];
  totalRecords = 0;
  page = 1;
  pageSize = 25;
  isLoading = true;

  search = '';
  status: StoreCreditStatus | '' = '';
  storeUuid: string | null = null;
  customerUuid: string | null = null;
  storeOptions: { label: string; value: string }[] = [];
  customerOptions: { label: string; value: string }[] = [];

  issueOpen = false;
  featureOn = true;
  canIssue = false;

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  ngOnInit(): void {
    this.featureOn = this.modules.isFeatureEnabled('FEATURE_POS_CREDIT_NOTES');
    this.canIssue = this.featureOn && this.auth.hasPermission('POS_CREDIT_NOTE_ISSUE');
    const qp = this.activated.snapshot.queryParamMap;
    this.customerUuid = qp.get('customer');
    this.storeUuid = qp.get('store');
    this.status = (qp.get('status') as StoreCreditStatus | null) ?? '';
    storeOptions$(this.pos).subscribe(o => this.storeOptions = o);
    if (this.auth.hasPermission('CUSTOMER_VIEW')) {
      this.customers.getCustomers({ status: 'ACTIVE', page: 1, pageSize: 200 }).pipe(
        map(res => (res.result?.data ?? []).map(c => ({ label: `${c.name} (${c.code})`, value: c.uuid }))),
        catchError(() => of([] as { label: string; value: string }[]))
      ).subscribe(o => this.customerOptions = o);
    }
  }

  ngOnDestroy(): void { if (this.searchTimer) clearTimeout(this.searchTimer); }

  load(): void {
    this.isLoading = true;
    this.service.getStoreCredits({
      status: this.status, customerUuid: this.customerUuid, storeUuid: this.storeUuid, search: this.search.trim() || null,
      page: this.page, pageSize: this.pageSize
    }).subscribe({
      next: res => {
        this.isLoading = false;
        const now = Date.now();
        this.rows = (res.result?.data ?? []).map(c => storeCreditRow(c, now));
        this.totalRecords = res.result?.totalRecords ?? 0;
      },
      error: err => {
        this.isLoading = false;
        this.rows = [];
        this.totalRecords = 0;
        this.messages.add({ severity: 'error', summary: 'Error', detail: posError(err, 'Failed to load store credit.') });
      }
    });
  }

  onLazyLoad(event: TableLazyLoadEvent): void {
    this.pageSize = event.rows ?? this.pageSize;
    this.page = Math.floor((event.first ?? 0) / this.pageSize) + 1;
    this.load();
  }

  onSearchChange(): void {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => this.applyFilters(), 400);
  }

  applyFilters(): void { this.page = 1; this.load(); }

  setStatus(s: StoreCreditStatus | ''): void { this.status = s; this.applyFilters(); }

  resetFilters(): void {
    this.search = ''; this.status = ''; this.storeUuid = null; this.customerUuid = null;
    this.applyFilters();
  }

  onIssued(c: StoreCredit): void {
    this.messages.add({ severity: 'success', summary: 'Issued', detail: `Store credit ${c.number} issued.` });
    this.router.navigate([STORE_CREDIT_ROUTE, c.uuid]);
  }
}
