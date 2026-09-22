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
import { forkJoin, of } from 'rxjs';
import { catchError } from 'rxjs/operators';

import { SaleOrderService, SaleOrderModel, SaleOrderFilter } from '../../../../services/sale-order.service';
import { BusinessPartnerService } from '../../../../services/business-partner.service';
import { AuthService } from '../../../service/auth.service';
import { formatCode } from '../../../../shared/format-code';

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

export { formatCode };

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

  /** Customer names by partner id, asked for once each. An id with no name yet, or none to be had, shows as a dash. */
  customerNames: Record<string, string> = {};
  private askedFor = new Set<string>();

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
    private partnerService: BusinessPartnerService,
    public authService: AuthService,
    private messageService: MessageService
  ) {}

  get canCreate(): boolean { return this.authService.hasPermission('SALE_ORDER_CREATE'); }

  /** Only a draft can be edited, and only by someone allowed to. */
  canEdit(order: SaleOrderModel): boolean {
    return order.status === 'DRAFT' && this.authService.hasPermission('SALE_ORDER_EDIT');
  }

  customerName(order: SaleOrderModel): string {
    return this.customerNames[order.partnerId] ?? '—';
  }

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
          this.loadCustomerNames(this.orders);
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

  /** One lookup per customer on the page that has not been looked up yet; a failed one leaves a dash and is not retried. */
  private loadCustomerNames(orders: SaleOrderModel[]) {
    const wanted = [...new Set(orders.map(o => o.partnerId).filter(id => !!id && !this.askedFor.has(id)))];
    if (wanted.length === 0) return;
    wanted.forEach(id => this.askedFor.add(id));

    forkJoin(wanted.map(id => this.partnerService.getPartnerById(id).pipe(catchError(() => of(null))))).subscribe(results => {
      results.forEach((res, i) => {
        const name = res?.result?.companyName;
        if (name) this.customerNames[wanted[i]] = name;
      });
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
