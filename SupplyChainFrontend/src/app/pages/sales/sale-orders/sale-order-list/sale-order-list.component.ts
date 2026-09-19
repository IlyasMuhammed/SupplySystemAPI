import { Component, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
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

import { SaleOrderService, SaleOrderModel, SaleOrderFilter } from '../../../../services/sale-order.service';

type Severity = 'success' | 'info' | 'warn' | 'danger' | 'secondary' | 'contrast';

/** Severity for every sale order status the server can return. Exported for the detail page. */
export const SALE_ORDER_STATUS_SEVERITY: Record<string, Severity> = {
  DRAFT:               'secondary',
  CONFIRMED:           'info',
  PARTIALLY_FULFILLED: 'warn',
  FULFILLED:           'success',
  INVOICED:            'success',
  CLOSED:              'success',
  CANCELLED:           'danger'
};

/** Turns PARTIALLY_FULFILLED into "Partially Fulfilled". */
export function formatCode(code?: string | null): string {
  if (!code) return '';
  return code
    .split('_')
    .map(word => word.charAt(0) + word.slice(1).toLowerCase())
    .join(' ');
}

@Component({
  selector: 'app-sale-order-list',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    TableModule, ButtonModule, InputTextModule, InputIconModule, IconFieldModule,
    TagModule, TooltipModule, ToastModule, DropdownModule
  ],
  templateUrl: './sale-order-list.component.html',
  styleUrls: ['./sale-order-list.component.scss'],
  providers: [MessageService]
})
export class SaleOrderListComponent implements OnInit, OnDestroy {
  orders: SaleOrderModel[] = [];
  totalRecords = 0;
  currentPage  = 1;
  pageSize     = 20;
  isLoading    = true;

  searchText     = '';
  selectedStatus = '';

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  statusOptions = [
    { label: 'All Statuses',        value: '' },
    { label: 'Draft',               value: 'DRAFT' },
    { label: 'Confirmed',           value: 'CONFIRMED' },
    { label: 'Partially Fulfilled', value: 'PARTIALLY_FULFILLED' },
    { label: 'Fulfilled',           value: 'FULFILLED' },
    { label: 'Invoiced',            value: 'INVOICED' },
    { label: 'Closed',              value: 'CLOSED' },
    { label: 'Cancelled',           value: 'CANCELLED' }
  ];

  constructor(
    private saleOrderService: SaleOrderService,
    private messageService: MessageService
  ) {}

  ngOnInit() {
    // The lazy table fires (onLazyLoad) once as it initialises — that is the first load.
  }

  ngOnDestroy() {
    if (this.searchTimer) clearTimeout(this.searchTimer);
  }

  load() {
    this.isLoading = true;

    const filter: SaleOrderFilter = {
      page: this.currentPage,
      pageSize: this.pageSize,
      search: this.searchText || undefined,
      status: this.selectedStatus || undefined
    };

    this.saleOrderService.getSaleOrders(filter).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) {
          this.orders       = res.result.data ?? [];
          this.totalRecords = res.result.totalRecords ?? 0;
        } else {
          this.orders = [];
          this.totalRecords = 0;
        }
      },
      error: () => {
        this.isLoading = false;
        this.orders = [];
        this.totalRecords = 0;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load sale orders.' });
      }
    });
  }

  onSearchChange() {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => { this.currentPage = 1; this.load(); }, 400);
  }

  onFilterChange() { this.currentPage = 1; this.load(); }

  onPageChange(event: TableLazyLoadEvent) {
    this.currentPage = Math.floor((event.first ?? 0) / (event.rows ?? this.pageSize)) + 1;
    this.pageSize    = event.rows ?? this.pageSize;
    this.load();
  }

  resetFilters() {
    this.searchText = '';
    this.selectedStatus = '';
    this.currentPage = 1;
    this.load();
  }

  getStatusSeverity(status: string): Severity {
    return SALE_ORDER_STATUS_SEVERITY[status] ?? 'secondary';
  }

  formatStatus(code?: string | null): string { return formatCode(code); }
}
