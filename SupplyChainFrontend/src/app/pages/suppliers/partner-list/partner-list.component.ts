import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TableModule } from 'primeng/table';
import { ToolbarModule } from 'primeng/toolbar';
import { InputTextModule } from 'primeng/inputtext';
import { InputIconModule } from 'primeng/inputicon';
import { IconFieldModule } from 'primeng/iconfield';
import { TagModule } from 'primeng/tag';
import { TabViewModule } from 'primeng/tabview';
import { ToastModule } from 'primeng/toast';
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { MessageService, ConfirmationService } from 'primeng/api';
import {
  BusinessPartnerService,
  BusinessPartnerModel,
  BusinessPartnerFilter
} from '../../../services/business-partner.service';

// Addendum 29 §1.6 — "type filter tabs (All/Vendors/Customers/Carriers/Service Providers)". Each
// tab is a pure filter selector, not separate content — the same table below is re-queried on
// tab change, matching how SupplierListComponent's dropdown filters already re-query on change.
type PartnerTypeTab = 'ALL' | 'VENDOR' | 'CUSTOMER' | 'CARRIER' | 'SERVICE_PROVIDER';

@Component({
  selector: 'app-partner-list',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    TableModule, ButtonModule, ToolbarModule,
    InputTextModule, InputIconModule, IconFieldModule,
    TagModule, TabViewModule, ToastModule, ConfirmDialogModule
  ],
  templateUrl: './partner-list.component.html',
  styleUrls: ['./partner-list.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class PartnerListComponent implements OnInit {
  partners: BusinessPartnerModel[] = [];
  totalRecords = 0;
  currentPage = 1;
  pageSize = 20;
  isLoading = true;

  searchText = '';
  activeTabIndex = 0;

  private readonly tabs: { label: string; icon: string; value: PartnerTypeTab }[] = [
    { label: 'All',               icon: 'pi pi-th-large',   value: 'ALL' },
    { label: 'Vendors',           icon: 'pi pi-truck',      value: 'VENDOR' },
    { label: 'Customers',         icon: 'pi pi-users',      value: 'CUSTOMER' },
    { label: 'Carriers',          icon: 'pi pi-send',       value: 'CARRIER' },
    { label: 'Service Providers', icon: 'pi pi-wrench',     value: 'SERVICE_PROVIDER' }
  ];

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  constructor(
    private partnerService: BusinessPartnerService,
    private messageService: MessageService,
    private confirmationService: ConfirmationService
  ) {}

  get tabOptions() { return this.tabs; }

  ngOnInit() {
    this.loadPartners();
  }

  private currentTabFilterFlags(): Partial<BusinessPartnerFilter> {
    switch (this.tabs[this.activeTabIndex].value) {
      case 'VENDOR':           return { isVendor: true };
      case 'CUSTOMER':         return { isCustomer: true };
      case 'CARRIER':          return { isCarrier: true };
      case 'SERVICE_PROVIDER': return { isServiceProvider: true };
      default:                 return {};
    }
  }

  loadPartners() {
    this.isLoading = true;
    const filter: BusinessPartnerFilter = {
      page: this.currentPage,
      pageSize: this.pageSize,
      search: this.searchText || undefined,
      ...this.currentTabFilterFlags()
    };

    this.partnerService.getPartners(filter).subscribe({
      next: (response) => {
        this.isLoading = false;
        if (response.success && response.result) {
          this.partners = response.result.data ?? [];
          this.totalRecords = response.result.totalRecords ?? 0;
        } else {
          this.partners = [];
          this.totalRecords = 0;
        }
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load business partners' });
      }
    });
  }

  onTabChange(index: number) {
    this.activeTabIndex = index;
    this.currentPage = 1;
    this.loadPartners();
  }

  onSearchChange() {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => {
      this.currentPage = 1;
      this.loadPartners();
    }, 400);
  }

  onPageChange(event: { first: number; rows: number }) {
    this.currentPage = Math.floor(event.first / event.rows) + 1;
    this.pageSize = event.rows;
    this.loadPartners();
  }

  resetFilters() {
    this.searchText = '';
    this.activeTabIndex = 0;
    this.currentPage = 1;
    this.loadPartners();
  }

  confirmDelete(partner: BusinessPartnerModel) {
    this.confirmationService.confirm({
      message: `Delete business partner "${partner.companyName}"? This cannot be undone.`,
      header: 'Confirm Delete',
      icon: 'pi pi-exclamation-triangle',
      acceptButtonStyleClass: 'p-button-danger',
      rejectButtonStyleClass: 'p-button-text',
      accept: () => {
        // Always populated on a row the backend actually returned from a list read.
        this.partnerService.deletePartner(partner.uuid!).subscribe({
          next: (res) => {
            if (res.success) {
              this.messageService.add({ severity: 'success', summary: 'Deleted', detail: 'Business partner deleted.' });
              this.loadPartners();
            } else {
              this.messageService.add({ severity: 'error', summary: 'Error', detail: res.message || 'Delete failed.' });
            }
          },
          error: (err) => {
            this.messageService.add({
              severity: 'error', summary: 'Error',
              detail: err?.error?.message || 'Delete failed — it may be referenced by an existing document.'
            });
          }
        });
      }
    });
  }

  partnerTypeTags(p: BusinessPartnerModel): { label: string; severity: 'success' | 'info' | 'warn' | 'help' }[] {
    const tags: { label: string; severity: 'success' | 'info' | 'warn' | 'help' }[] = [];
    if (p.isVendor)          tags.push({ label: 'Vendor',           severity: 'success' });
    if (p.isCustomer)        tags.push({ label: 'Customer',         severity: 'info' });
    if (p.isCarrier)         tags.push({ label: 'Carrier',          severity: 'warn' });
    if (p.isServiceProvider) tags.push({ label: 'Service Provider', severity: 'help' });
    return tags;
  }
}
