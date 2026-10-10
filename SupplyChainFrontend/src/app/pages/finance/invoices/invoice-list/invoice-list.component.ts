import { Component, OnInit, computed, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TableModule } from 'primeng/table';
import { InputTextModule } from 'primeng/inputtext';
import { InputIconModule } from 'primeng/inputicon';
import { IconFieldModule } from 'primeng/iconfield';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';
import { ToastModule } from 'primeng/toast';
import { DropdownModule } from 'primeng/dropdown';
import { MessageService } from 'primeng/api';
import { TableLazyLoadEvent } from 'primeng/table';
import { FinanceService, InvoiceListItemModel, InvoiceFilter, INVOICE_MATCH_STATUSES } from '../../../../services/finance.service';
import { QboSyncBadgeComponent } from '../../../../shared/components/qbo-sync-badge/qbo-sync-badge.component';
import { QboSyncStatusStore } from '../../../../shared/components/qbo-sync-badge/qbo-sync-status.store';
import { AuthService } from '../../../service/auth.service';
import { FLOW } from '../../../../shared/flow';

@Component({
  selector: 'app-invoice-list',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    TableModule, ButtonModule, InputTextModule,
    InputIconModule, IconFieldModule, TagModule,
    TooltipModule, ToastModule, DropdownModule, QboSyncBadgeComponent,
    ...FLOW
  ],
  templateUrl: './invoice-list.component.html',
  styleUrls: ['./invoice-list.component.scss'],
  providers: [MessageService]
})
export class InvoiceListComponent implements OnInit {
  invoices: InvoiceListItemModel[] = [];
  totalRecords = 0;
  currentPage  = 1;
  pageSize     = 20;
  isLoading    = true;

  searchText      = '';
  selectedMatch   = '';
  selectedPayment = '';

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  /** The QuickBooks column shows only for organizations with the integration and users who may see it. */
  private readonly qboStore = inject(QboSyncStatusStore);
  readonly qboAvailable = computed(() => this.qboStore.isAvailable());

  matchOptions = [
    { label: 'All Match Status',  value: '' },
    ...INVOICE_MATCH_STATUSES.map(s => ({ label: s, value: s as string }))
  ];

  paymentOptions = [
    { label: 'All Payment Status', value: '' },
    { label: 'Unpaid',             value: 'Unpaid' },
    { label: 'Scheduled',          value: 'Scheduled' },
    { label: 'Partial',            value: 'Partial' },
    { label: 'Paid',               value: 'Paid' },
    { label: 'Overdue',            value: 'Overdue' }
  ];

  constructor(
    private financeService: FinanceService,
    private messageService: MessageService,
    private authService: AuthService
  ) {}

  /** Creating an invoice needs INVOICE_PROCESS on the server (and on the create route); INVOICE_VIEW only reads. */
  get canCreate(): boolean { return this.authService.hasPermission('INVOICE_PROCESS'); }

  ngOnInit() { this.load(); }

  load() {
    this.isLoading = true;
    const filter: InvoiceFilter = {
      page: this.currentPage, pageSize: this.pageSize,
      search: this.searchText || undefined,
      matchStatus: this.selectedMatch || undefined,
      paymentStatus: this.selectedPayment || undefined
    };
    this.financeService.getInvoices(filter).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) {
          this.invoices     = res.result.data ?? [];
          this.totalRecords = res.result.totalRecords ?? 0;
        } else { this.invoices = []; this.totalRecords = 0; }
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load invoices.' });
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

  resetFilters() { this.searchText = ''; this.selectedMatch = ''; this.selectedPayment = ''; this.currentPage = 1; this.load(); }

  getMatchSeverity(s: string): 'success' | 'danger' | 'warn' | 'info' | 'secondary' | 'contrast' {
    switch (s) {
      case 'Matched':  return 'success';
      case 'Approved': return 'success';
      case 'Rejected': return 'danger';
      case 'Variance': return 'warn';
      case 'Reversed': return 'contrast';
      case 'Pending':  return 'secondary';
      default:         return 'secondary';
    }
  }

  getPaymentSeverity(s: string): 'success' | 'danger' | 'warn' | 'info' | 'secondary' {
    switch (s) {
      case 'Paid': case 'FULLY_PAID':          return 'success';
      case 'Overdue':   return 'danger';
      case 'Partial': case 'PARTIALLY_PAID':   return 'warn';
      case 'Scheduled': return 'info';
      case 'Unpaid':    return 'secondary';
      default:          return 'secondary';
    }
  }
}
