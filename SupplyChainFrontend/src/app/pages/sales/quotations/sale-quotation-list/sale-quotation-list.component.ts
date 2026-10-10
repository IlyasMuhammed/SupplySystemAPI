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
import { AutoCompleteModule, AutoCompleteCompleteEvent } from 'primeng/autocomplete';
import { MessageService } from 'primeng/api';

import {
  SalesPreorderService, SaleQuotationListItem, SaleQuotationFilter, SaleQuotationStatus, SALE_QUOTATION_STATUSES
} from '../../../../services/sales-preorder.service';
import { BusinessPartnerService, BusinessPartnerModel } from '../../../../services/business-partner.service';
import { AuthService } from '../../../service/auth.service';
import { formatCode } from '../../../../shared/format-code';
import { fromDateOnly, toDateOnly } from '../../../../shared/date-only';
import {
  SALE_QUOTATION_STATUS_SEVERITY, Severity, QUOTATIONS_ROUTE, INQUIRIES_ROUTE
} from '../sale-quotation.shared';
import { FLOW } from '../../../../shared/flow';

/** A32-PC-10 — sale quotations (seller-side; not the buyer-side RFQ quotations). */
@Component({
  selector: 'app-sale-quotation-list',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    TableModule, ButtonModule, InputTextModule, InputIconModule, IconFieldModule,
    TagModule, TooltipModule, ToastModule, DropdownModule, CalendarModule, AutoCompleteModule, ...FLOW
  ],
  templateUrl: './sale-quotation-list.component.html',
  styleUrls: ['./sale-quotation-list.component.scss'],
  providers: [MessageService]
})
export class SaleQuotationListComponent implements OnDestroy {
  readonly quotationsRoute = QUOTATIONS_ROUTE;
  readonly inquiriesRoute = INQUIRIES_ROUTE;

  quotations: SaleQuotationListItem[] = [];
  totalRecords = 0;
  currentPage  = 1;
  pageSize     = 20;
  isLoading    = true;

  searchText     = '';
  selectedStatus: SaleQuotationStatus | '' = '';
  /** A customer picked from the suggestions; text typed but not picked does not filter. */
  selectedCustomer: BusinessPartnerModel | string | null = null;
  customerSuggestions: BusinessPartnerModel[] = [];
  /** The valid-until range. */
  validToFrom: Date | null = null;
  validToTo: Date | null = null;

  statusOptions: { label: string; value: SaleQuotationStatus | '' }[] = [
    { label: 'All Statuses', value: '' },
    ...SALE_QUOTATION_STATUSES.map(s => ({ label: formatCode(s), value: s }))
  ];

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  constructor(
    private salesPreorderService: SalesPreorderService,
    private partnerService: BusinessPartnerService,
    public authService: AuthService,
    private messageService: MessageService
  ) {}

  get canCreate(): boolean { return this.authService.hasPermission('SALE_QUOTATION_CREATE'); }

  /** Only a draft can be changed, and only by someone allowed to. */
  canEdit(q: SaleQuotationListItem): boolean {
    return q.status === 'DRAFT' && this.authService.hasPermission('SALE_QUOTATION_EDIT');
  }

  ngOnDestroy() {
    if (this.searchTimer) clearTimeout(this.searchTimer);
  }

  private get customerId(): string | undefined {
    const c = this.selectedCustomer;
    return c && typeof c === 'object' && c.uuid ? c.uuid : undefined;
  }

  load() {
    this.isLoading = true;

    const filter: SaleQuotationFilter = {
      page: this.currentPage,
      pageSize: this.pageSize,
      search: this.searchText.trim() || undefined,
      status: this.selectedStatus || undefined,
      partnerId: this.customerId,
      validToFrom: this.validToFrom ? toDateOnly(this.validToFrom) : undefined,
      validToTo: this.validToTo ? toDateOnly(this.validToTo) : undefined
    };

    this.salesPreorderService.getQuotations(filter).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.quotations   = res.success && res.result ? res.result.data ?? [] : [];
        this.totalRecords = res.success && res.result ? res.result.totalRecords ?? 0 : 0;
      },
      error: () => {
        this.isLoading = false;
        this.quotations = [];
        this.totalRecords = 0;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load sale quotations.' });
      }
    });
  }

  searchCustomers(event: AutoCompleteCompleteEvent) {
    this.partnerService.getPartners({ isCustomer: true, search: event.query, pageSize: 20 }).subscribe({
      next: (res) => { this.customerSuggestions = res.result?.data ?? []; },
      error: () => { this.customerSuggestions = []; }
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
    this.selectedCustomer = null;
    this.validToFrom = null;
    this.validToTo = null;
    this.currentPage = 1;
    this.load();
  }

  /** "2026-10-31T00:00:00" is the 31st, wherever the reader is. */
  day(value?: string | null): Date | null {
    return value ? fromDateOnly(value) : null;
  }

  getStatusSeverity(status: string): Severity {
    return SALE_QUOTATION_STATUS_SEVERITY[status as SaleQuotationStatus] ?? 'secondary';
  }

  formatStatus(code?: string | null): string { return formatCode(code); }
}
