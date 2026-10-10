import { Component, OnDestroy, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { Router, RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TableModule, TableLazyLoadEvent } from 'primeng/table';
import { InputTextModule } from 'primeng/inputtext';
import { InputIconModule } from 'primeng/inputicon';
import { IconFieldModule } from 'primeng/iconfield';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';
import { ToastModule } from 'primeng/toast';
import { DropdownModule } from 'primeng/dropdown';
import { MessageService } from 'primeng/api';

import {
  CUSTOMER_TYPE_OPTIONS, CustomerFilter, CustomerListItem, CustomerService, CustomerSortField, CustomerType,
  customerTypeLabel, customersCsv
} from '../../../services/customer.service';
import { AuthService } from '../../service/auth.service';
import { ModuleService } from '../../../services/module.service';
import { CustomerFormDialogComponent } from '../customer-form-dialog/customer-form-dialog.component';
import { FLOW } from '../../../shared/flow';

const SORT_FIELDS: readonly CustomerSortField[] = ['code', 'name', 'customerType', 'creditLimit', 'balance', 'status'];

/** A37 §15.3 — the Customer Master list (API-CONTRACT §5). */
@Component({
  selector: 'app-customer-list',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule, TableModule, ButtonModule, InputTextModule, InputIconModule, IconFieldModule,
    TagModule, TooltipModule, ToastModule, DropdownModule, CustomerFormDialogComponent, ...FLOW
  ],
  templateUrl: './customer-list.component.html',
  providers: [MessageService]
})
export class CustomerListComponent implements OnDestroy {
  private readonly service = inject(CustomerService);
  private readonly auth = inject(AuthService);
  private readonly modules = inject(ModuleService);
  private readonly router = inject(Router);
  private readonly messages = inject(MessageService);

  customers: CustomerListItem[] = [];
  totalRecords = 0;
  page = 1;
  pageSize = 25;
  isLoading = true;
  isExporting = false;

  search = '';
  type: CustomerType | '' = '';
  status: 'ACTIVE' | 'INACTIVE' | '' = 'ACTIVE';
  sortField: CustomerSortField | null = null;
  sortOrder: 'asc' | 'desc' | null = null;

  showCreate = false;

  readonly typeLabel = customerTypeLabel;
  readonly typeOptions = [{ label: 'All types', value: '' }, ...CUSTOMER_TYPE_OPTIONS];
  readonly statusOptions: { label: string; value: 'ACTIVE' | 'INACTIVE' | '' }[] = [
    { label: 'Active', value: 'ACTIVE' }, { label: 'Inactive', value: 'INACTIVE' }, { label: 'All', value: '' }
  ];

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  get canCreate(): boolean { return this.auth.hasPermission('CUSTOMER_CREATE'); }
  get creditEnabled(): boolean { return this.modules.isFeatureEnabled('FEATURE_CREDIT_MANAGEMENT'); }

  ngOnDestroy(): void { if (this.searchTimer) clearTimeout(this.searchTimer); }

  private get filter(): CustomerFilter {
    return {
      search: this.search.trim() || undefined, type: this.type, status: this.status,
      sortField: this.sortField, sortOrder: this.sortOrder
    };
  }

  load(): void {
    this.isLoading = true;
    this.service.getCustomers({ ...this.filter, page: this.page, pageSize: this.pageSize }).subscribe({
      next: res => {
        this.isLoading = false;
        this.customers = res.result?.data ?? [];
        this.totalRecords = res.result?.totalRecords ?? 0;
      },
      error: err => {
        this.isLoading = false;
        this.customers = [];
        this.totalRecords = 0;
        this.messages.add({ severity: 'error', summary: 'Error', detail: err?.error?.message || 'Failed to load customers.' });
      }
    });
  }

  onLazyLoad(event: TableLazyLoadEvent): void {
    this.pageSize = event.rows ?? this.pageSize;
    this.page = Math.floor((event.first ?? 0) / this.pageSize) + 1;
    const field = (Array.isArray(event.sortField) ? event.sortField[0] : event.sortField) as CustomerSortField | undefined;
    this.sortField = field && SORT_FIELDS.includes(field) ? field : null;
    this.sortOrder = this.sortField ? (event.sortOrder === -1 ? 'desc' : 'asc') : null;
    this.load();
  }

  onSearchChange(): void {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => this.applyFilters(), 400);
  }

  applyFilters(): void { this.page = 1; this.load(); }

  setStatus(value: 'ACTIVE' | 'INACTIVE' | ''): void { this.status = value; this.applyFilters(); }

  resetFilters(): void {
    this.search = ''; this.type = ''; this.status = 'ACTIVE';
    this.applyFilters();
  }

  open(c: CustomerListItem): void { this.router.navigate(['/portal/pages/customers', c.uuid]); }

  onCreated(uuid: string): void {
    this.messages.add({ severity: 'success', summary: 'Created', detail: 'Customer created.' });
    if (uuid) this.router.navigate(['/portal/pages/customers', uuid]);
    else this.load();
  }

  /** CSV of every customer the current filter matches (all pages). */
  exportCsv(): void {
    if (this.isExporting) return;
    this.isExporting = true;
    this.service.getAllCustomers(this.filter).subscribe({
      next: rows => {
        this.isExporting = false;
        this.download(customersCsv(rows), `customers-${new Date().toISOString().slice(0, 10)}.csv`);
      },
      error: err => {
        this.isExporting = false;
        this.messages.add({ severity: 'error', summary: 'Export failed', detail: err?.error?.message || 'The customers could not be exported.' });
      }
    });
  }

  /** Separate for tests. */
  download(csv: string, fileName: string): void {
    const blob = new Blob(['﻿' + csv], { type: 'text/csv;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    a.click();
    URL.revokeObjectURL(url);
  }
}
