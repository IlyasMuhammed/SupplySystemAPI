import { Component, OnDestroy } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TableModule, TableLazyLoadEvent } from 'primeng/table';
import { InputTextModule } from 'primeng/inputtext';
import { InputIconModule } from 'primeng/inputicon';
import { IconFieldModule } from 'primeng/iconfield';
import { TooltipModule } from 'primeng/tooltip';
import { ToastModule } from 'primeng/toast';
import { SelectModule } from 'primeng/select';
import { DatePickerModule } from 'primeng/datepicker';
import { AutoCompleteModule, AutoCompleteCompleteEvent } from 'primeng/autocomplete';
import { MessageService } from 'primeng/api';

import {
  SalesPreorderService, SaleInquiryFilter, SaleInquiryListItem, SaleInquiryStatus, SALE_INQUIRY_STATUSES
} from '../../../../services/sales-preorder.service';
import { BusinessPartnerService, BusinessPartnerModel } from '../../../../services/business-partner.service';
import { AuthService } from '../../../service/auth.service';
import { INQUIRY_STATUS_COLOR, StatusColor, displayDate, statusLabel, writeDate } from '../sale-inquiry.shared';
import { FLOW } from '../../../../shared/flow';

/**
 * A32-PB-08 — Sales → Inquiries (spec §10.1). Number, customer, received date, status badge in the spec's colours,
 * line count; filters by status, customer and received-date range. "+ New Inquiry" needs SALE_INQUIRY_CREATE.
 */
@Component({
  selector: 'app-sale-inquiry-list',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    TableModule, ButtonModule, InputTextModule, InputIconModule, IconFieldModule,
    TooltipModule, ToastModule, SelectModule, DatePickerModule, AutoCompleteModule, ...FLOW
  ],
  templateUrl: './sale-inquiry-list.component.html',
  styleUrls: ['../sale-inquiry.shared.scss', './sale-inquiry-list.component.scss'],
  providers: [MessageService]
})
export class SaleInquiryListComponent implements OnDestroy {
  inquiries: SaleInquiryListItem[] = [];
  totalRecords = 0;
  currentPage  = 1;
  pageSize     = 20;
  isLoading    = true;

  searchText = '';
  selectedStatus: SaleInquiryStatus | '' = '';
  customer: BusinessPartnerModel | null = null;
  customerSuggestions: BusinessPartnerModel[] = [];
  receivedFrom: Date | null = null;
  receivedTo: Date | null = null;

  readonly statusOptions: { label: string; value: SaleInquiryStatus | '' }[] = [
    { label: 'All Statuses', value: '' },
    ...SALE_INQUIRY_STATUSES.map(s => ({ label: statusLabel(s), value: s }))
  ];

  readonly displayDate = displayDate;
  readonly statusLabel = statusLabel;

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  constructor(
    private service: SalesPreorderService,
    private partnerService: BusinessPartnerService,
    private authService: AuthService,
    private messageService: MessageService
  ) {}

  get canCreate(): boolean { return this.authService.hasPermission('SALE_INQUIRY_CREATE'); }

  ngOnDestroy() {
    if (this.searchTimer) clearTimeout(this.searchTimer);
  }

  // The lazy table fires onLazyLoad as it initialises — that is the first load.
  load() {
    this.isLoading = true;

    const filter: SaleInquiryFilter = {
      page: this.currentPage,
      pageSize: this.pageSize,
      search: this.searchText.trim() || undefined,
      status: this.selectedStatus || undefined,
      partnerId: this.customer?.uuid || undefined,
      receivedFrom: writeDate(this.receivedFrom) ?? undefined,
      receivedTo: writeDate(this.receivedTo) ?? undefined
    };

    this.service.getInquiries(filter).subscribe({
      next: (res) => {
        this.isLoading = false;
        this.inquiries    = res.success ? (res.result?.data ?? []) : [];
        this.totalRecords = res.success ? (res.result?.totalRecords ?? 0) : 0;
      },
      error: () => {
        this.isLoading = false;
        this.inquiries = [];
        this.totalRecords = 0;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load sale inquiries.' });
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
    this.searchTimer = setTimeout(() => this.onFilterChange(), 400);
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
    this.customer = null;
    this.receivedFrom = null;
    this.receivedTo = null;
    this.onFilterChange();
  }

  statusColor(status: SaleInquiryStatus): StatusColor { return INQUIRY_STATUS_COLOR[status] ?? 'grey'; }
}
