import { Component, OnDestroy, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TableLazyLoadEvent, TableModule } from 'primeng/table';
import { InputTextModule } from 'primeng/inputtext';
import { InputIconModule } from 'primeng/inputicon';
import { IconFieldModule } from 'primeng/iconfield';
import { TagModule } from 'primeng/tag';
import { ToastModule } from 'primeng/toast';
import { TooltipModule } from 'primeng/tooltip';
import { MessageService } from 'primeng/api';

import { POS_ROUTE, PosAdminService, PosStore, posError } from '../../../services/pos-admin.service';
import { AuthService } from '../../service/auth.service';
import { FLOW } from '../../../shared/flow';
import { StoreFormDialogComponent } from '../store-form-dialog/store-form-dialog.component';

type StatusFilter = 'ACTIVE' | 'INACTIVE' | '';

/** A38 FSD §17.7.1 — POS stores (API-CONTRACT §2). */
@Component({
  selector: 'app-pos-store-list',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule, ButtonModule, TableModule, InputTextModule, InputIconModule, IconFieldModule,
    TagModule, ToastModule, TooltipModule, StoreFormDialogComponent, ...FLOW
  ],
  template: `
<p-toast></p-toast>

<sf-page [crumbs]="['Point of Sale', 'Stores']">
  <div sfCmd class="sf-cmd" role="toolbar" aria-label="Commands">
    <p-button *ngIf="canManage" label="New store" icon="pi pi-plus" (onClick)="showCreate = true" data-testid="new-store"></p-button>
    <span class="sf-sep"></span>
    <p-button icon="pi pi-filter-slash" label="Reset filters" [text]="true" (onClick)="resetFilters()" data-testid="reset"></p-button>
  </div>

  <div class="sf-body">
    <div class="sf-lr-head">
      <div>
        <h1>Stores <span class="sf-count" data-testid="store-count">{{ totalRecords }}</span></h1>
        <div class="sf-sub">Shops that sell over the counter, the warehouse they sell from and their tills</div>
      </div>
      <div class="sf-filters">
        <button *ngFor="let s of statusOptions" type="button" class="sf-chip" [class.on]="status === s.value"
                (click)="setStatus(s.value)" [attr.data-testid]="'status-chip-' + (s.value || 'ALL')">{{ s.label }}</button>
      </div>
    </div>

    <div class="sf-filters">
      <p-iconfield class="sf-search">
        <p-inputicon styleClass="pi pi-search"></p-inputicon>
        <input pInputText type="text" placeholder="Search code, name or city…" [(ngModel)]="search"
               (ngModelChange)="onSearchChange()" data-testid="store-search" />
      </p-iconfield>
    </div>

    <div class="sf-card p0">
      <p-table [value]="stores" [loading]="isLoading" [lazy]="true" [totalRecords]="totalRecords" [rows]="pageSize"
               (onLazyLoad)="onLazyLoad($event)" [paginator]="true" [rowsPerPageOptions]="[25, 50, 100]"
               [tableStyle]="{ 'min-width': '56rem' }" data-testid="store-table">
        <ng-template pTemplate="header">
          <tr>
            <th>Code</th><th>Name</th><th>Warehouse</th><th class="text-right">Terminals</th>
            <th class="text-right">Active shifts</th><th>City</th><th>Status</th>
          </tr>
        </ng-template>
        <ng-template pTemplate="body" let-s>
          <tr data-testid="store-row">
            <td><a class="sf-mono" [routerLink]="[route, 'stores', s.uuid]"><strong>{{ s.code }}</strong></a></td>
            <td>{{ s.name }}</td>
            <td>{{ s.warehouseName }}</td>
            <td class="text-right">{{ s.terminalCount }}</td>
            <td class="text-right"><span [class.sf-mu]="!s.activeShiftCount">{{ s.activeShiftCount }}</span></td>
            <td>{{ s.city || '—' }}</td>
            <td><p-tag [value]="s.isActive ? 'Active' : 'Inactive'" [severity]="s.isActive ? 'success' : 'secondary'"></p-tag></td>
          </tr>
        </ng-template>
        <ng-template pTemplate="emptymessage">
          <tr><td colspan="7" class="sf-empty">No stores yet.</td></tr>
        </ng-template>
      </p-table>
    </div>
  </div>
</sf-page>

<app-pos-store-form-dialog [(visible)]="showCreate" (saved)="onCreated($event)"></app-pos-store-form-dialog>
  `,
  providers: [MessageService]
})
export class PosStoreListComponent implements OnDestroy {
  private readonly service = inject(PosAdminService);
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly messages = inject(MessageService);

  readonly route = POS_ROUTE;
  readonly statusOptions: { label: string; value: StatusFilter }[] = [
    { label: 'Active', value: 'ACTIVE' }, { label: 'Inactive', value: 'INACTIVE' }, { label: 'All', value: '' }
  ];

  stores: PosStore[] = [];
  totalRecords = 0;
  page = 1;
  pageSize = 25;
  isLoading = true;
  search = '';
  status: StatusFilter = '';
  showCreate = false;

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  get canManage(): boolean { return this.auth.hasPermission('POS_STORE_MANAGE'); }

  ngOnDestroy(): void { if (this.searchTimer) clearTimeout(this.searchTimer); }

  load(): void {
    this.isLoading = true;
    this.service.getStores({
      search: this.search.trim() || null,
      isActive: this.status === '' ? null : this.status === 'ACTIVE',
      page: this.page, pageSize: this.pageSize
    }).subscribe({
      next: res => {
        this.isLoading = false;
        this.stores = res.result?.data ?? [];
        this.totalRecords = res.result?.totalRecords ?? 0;
      },
      error: err => {
        this.isLoading = false;
        this.stores = [];
        this.totalRecords = 0;
        this.messages.add({ severity: 'error', summary: 'Error', detail: posError(err, 'Failed to load stores.') });
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

  setStatus(value: StatusFilter): void { this.status = value; this.applyFilters(); }

  resetFilters(): void { this.search = ''; this.status = ''; this.applyFilters(); }

  onCreated(store: PosStore): void {
    this.messages.add({ severity: 'success', summary: 'Created', detail: 'Store created.' });
    if (store?.uuid) this.router.navigate([POS_ROUTE, 'stores', store.uuid]);
    else this.load();
  }
}
