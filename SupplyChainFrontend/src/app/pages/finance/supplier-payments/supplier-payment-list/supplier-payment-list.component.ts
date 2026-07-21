import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TableModule, TableLazyLoadEvent } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';
import { ToastModule } from 'primeng/toast';
import { DropdownModule } from 'primeng/dropdown';
import { CalendarModule } from 'primeng/calendar';
import { MessageService } from 'primeng/api';
import { FinanceService, SupplierPaymentListItemModel, SupplierPaymentFilter } from '../../../../services/finance.service';

@Component({
  selector: 'app-supplier-payment-list',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    TableModule, ButtonModule, TagModule,
    TooltipModule, ToastModule, DropdownModule, CalendarModule
  ],
  templateUrl: './supplier-payment-list.component.html',
  styleUrls: ['./supplier-payment-list.component.scss'],
  providers: [MessageService]
})
export class SupplierPaymentListComponent implements OnInit {
  payments: SupplierPaymentListItemModel[] = [];
  totalRecords = 0;
  currentPage  = 1;
  pageSize     = 20;
  isLoading    = true;

  selectedStatus = '';
  selectedMethod = '';
  dateFrom: Date | null = null;
  dateTo: Date | null = null;

  statusOptions = [
    { label: 'All Statuses', value: '' },
    { label: 'Draft',        value: 'DRAFT' },
    { label: 'Approved',     value: 'APPROVED' },
    { label: 'Posted',       value: 'POSTED' },
    { label: 'Cancelled',    value: 'CANCELLED' },
    { label: 'Bounced',      value: 'BOUNCED' }
  ];

  methodOptions = [
    { label: 'All Methods',  value: '' },
    { label: 'Bank Transfer', value: 'BANK_TRANSFER' },
    { label: 'Online Wire',   value: 'ONLINE_WIRE' },
    { label: 'Cheque',        value: 'CHEQUE' },
    { label: 'Cash',          value: 'CASH' }
  ];

  constructor(
    private financeService: FinanceService,
    private messageService: MessageService
  ) {}

  ngOnInit() { this.load(); }

  load() {
    this.isLoading = true;
    const filter: SupplierPaymentFilter = {
      page: this.currentPage, pageSize: this.pageSize,
      status: this.selectedStatus || undefined,
      method: this.selectedMethod || undefined,
      dateFrom: this.dateFrom ? this.toIsoDate(this.dateFrom) : undefined,
      dateTo:   this.dateTo   ? this.toIsoDate(this.dateTo)   : undefined
    };
    this.financeService.getSupplierPayments(filter).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) {
          this.payments     = res.result.data ?? [];
          this.totalRecords = res.result.totalRecords ?? 0;
        } else { this.payments = []; this.totalRecords = 0; }
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load payments.' });
      }
    });
  }

  onFilterChange() { this.currentPage = 1; this.load(); }

  onPageChange(event: TableLazyLoadEvent) {
    this.currentPage = Math.floor((event.first ?? 0) / (event.rows ?? this.pageSize)) + 1;
    this.pageSize    = event.rows ?? this.pageSize;
    this.load();
  }

  resetFilters() {
    this.selectedStatus = ''; this.selectedMethod = '';
    this.dateFrom = null; this.dateTo = null;
    this.currentPage = 1; this.load();
  }

  getStatusSeverity(s: string): 'success' | 'danger' | 'warn' | 'info' | 'secondary' {
    switch (s) {
      case 'POSTED':    return 'success';
      case 'APPROVED':  return 'info';
      case 'BOUNCED':   return 'warn';
      case 'CANCELLED': return 'danger';
      case 'DRAFT':     return 'secondary';
      default:          return 'secondary';
    }
  }

  private toIsoDate(d: Date): string {
    return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
  }
}
