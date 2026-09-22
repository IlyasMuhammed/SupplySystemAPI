import { Component, OnDestroy } from '@angular/core';
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
import { CalendarModule } from 'primeng/calendar';
import { CheckboxModule } from 'primeng/checkbox';
import { MessageService } from 'primeng/api';

import {
  CustomerPaymentService, CustomerPaymentListItemModel, CustomerPaymentFilter, CUSTOMER_PAYMENT_METHODS
} from '../../../../services/customer-payment.service';
import { AuthService } from '../../../service/auth.service';
import { formatCode } from '../../../../shared/format-code';
import { toDateOnly } from '../../../../shared/date-only';
import { PAYMENT_STATUS_OPTIONS, PAYMENT_STATUS_SEVERITY, Severity } from '../../receivables/receivables.shared';

@Component({
  selector: 'app-customer-payment-list',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    TableModule, ButtonModule, InputTextModule, InputIconModule, IconFieldModule,
    TagModule, TooltipModule, ToastModule, DropdownModule, CalendarModule, CheckboxModule
  ],
  templateUrl: './customer-payment-list.component.html',
  styleUrls: ['./customer-payment-list.component.scss'],
  providers: [MessageService]
})
export class CustomerPaymentListComponent implements OnDestroy {
  payments: CustomerPaymentListItemModel[] = [];
  totalRecords = 0;
  currentPage  = 1;
  pageSize     = 20;
  isLoading    = true;

  searchText     = '';
  selectedStatus = '';
  selectedMethod = '';
  onAccountOnly  = false;
  dateFrom: Date | null = null;
  dateTo: Date | null = null;

  statusOptions = PAYMENT_STATUS_OPTIONS;
  methodOptions = [{ label: 'All Methods', value: '' }, ...CUSTOMER_PAYMENT_METHODS.map(m => ({ label: m.label, value: m.value }))];

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  constructor(
    private paymentService: CustomerPaymentService,
    public authService: AuthService,
    private messageService: MessageService
  ) {}

  get canRecord(): boolean { return this.authService.hasPermission('CUSTOMER_PAYMENT_RECORD'); }

  ngOnDestroy() {
    if (this.searchTimer) clearTimeout(this.searchTimer);
  }

  load() {
    this.isLoading = true;

    const filter: CustomerPaymentFilter = {
      page: this.currentPage,
      pageSize: this.pageSize,
      search: this.searchText || undefined,
      status: this.selectedStatus || undefined,
      method: this.selectedMethod || undefined,
      // "Only money on account" is a filter when ticked and no opinion when not: false would mean fully applied.
      unallocated: this.onAccountOnly ? true : undefined,
      dateFrom: this.dateFrom ? toDateOnly(this.dateFrom) : undefined,
      dateTo: this.dateTo ? toDateOnly(this.dateTo) : undefined
    };

    this.paymentService.getPayments(filter).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) {
          this.payments     = res.result.data ?? [];
          this.totalRecords = res.result.totalRecords ?? 0;
        } else {
          this.payments = [];
          this.totalRecords = 0;
        }
      },
      error: (err) => {
        this.isLoading = false;
        this.payments = [];
        this.totalRecords = 0;
        this.messageService.add({
          severity: 'error', summary: 'Error', detail: err?.error?.message ?? 'Failed to load customer payments.'
        });
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
    this.selectedMethod = '';
    this.onAccountOnly = false;
    this.dateFrom = null;
    this.dateTo = null;
    this.currentPage = 1;
    this.load();
  }

  getStatusSeverity(status: string): Severity {
    return PAYMENT_STATUS_SEVERITY[status] ?? 'secondary';
  }

  /** The cheque number or bank reference: whichever identifies the receipt on the customer's side. */
  reference(p: CustomerPaymentListItemModel): string {
    return p.chequeNumber || p.bankReference || '—';
  }

  formatStatus(code?: string | null): string { return formatCode(code); }
}
