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
import { TooltipModule } from 'primeng/tooltip';
import { ToastModule } from 'primeng/toast';
import { DropdownModule } from 'primeng/dropdown';
import { MessageService } from 'primeng/api';
import {
  SupplierService,
  SupplierListItemModel,
  SupplierListFilter
} from '../../../services/supplier.service';

@Component({
  selector: 'app-supplier-list',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    TableModule, ButtonModule, ToolbarModule,
    InputTextModule, InputIconModule, IconFieldModule,
    TagModule, TooltipModule, ToastModule, DropdownModule
  ],
  templateUrl: './supplier-list.component.html',
  styleUrls: ['./supplier-list.component.scss'],
  providers: [MessageService]
})
export class SupplierListComponent implements OnInit {
  suppliers: SupplierListItemModel[] = [];
  totalRecords = 0;
  currentPage = 1;
  pageSize = 20;
  isLoading = true;

  searchText = '';
  selectedStatus = '';
  selectedCountry = '';

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  statusOptions = [
    { label: 'All Statuses', value: '' },
    { label: 'Pending',     value: 'PENDING' },
    { label: 'Active',      value: 'ACTIVE' },
    { label: 'Inactive',    value: 'INACTIVE' },
    { label: 'Rejected',    value: 'REJECTED' },
    { label: 'Blacklisted', value: 'BLACKLISTED' },
    { label: 'Suspended',   value: 'SUSPENDED' }
  ];

  constructor(
    private supplierService: SupplierService,
    private messageService: MessageService
  ) {}

  get activeSuppliersCount(): number {
    return this.suppliers.filter(s => s.status === 'ACTIVE').length;
  }

  get pendingSuppliersCount(): number {
    return this.suppliers.filter(s => s.status === 'PENDING').length;
  }

  ngOnInit() {
    this.loadSuppliers();
  }

  loadSuppliers() {
    this.isLoading = true;
    const filter: SupplierListFilter = {
      page: this.currentPage,
      pageSize: this.pageSize,
      search: this.searchText || undefined,
      status: this.selectedStatus || undefined,
      country: this.selectedCountry || undefined
    };

    this.supplierService.getSuppliers(filter).subscribe({
      next: (response) => {
        this.isLoading = false;
        if (response.success && response.result) {
          this.suppliers = response.result.data ?? [];
          this.totalRecords = response.result.totalRecords ?? 0;
        } else {
          this.suppliers = [];
          this.totalRecords = 0;
        }
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load suppliers' });
      }
    });
  }

  onSearchChange() {
    if (this.searchTimer) clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => {
      this.currentPage = 1;
      this.loadSuppliers();
    }, 400);
  }

  onFilterChange() {
    this.currentPage = 1;
    this.loadSuppliers();
  }

  onPageChange(event: { first: number; rows: number }) {
    this.currentPage = Math.floor(event.first / event.rows) + 1;
    this.pageSize = event.rows;
    this.loadSuppliers();
  }

  resetFilters() {
    this.searchText = '';
    this.selectedStatus = '';
    this.selectedCountry = '';
    this.currentPage = 1;
    this.loadSuppliers();
  }

  getStatusSeverity(status: string | undefined): 'success' | 'danger' | 'warn' | 'secondary' | 'info' | 'contrast' {
    switch (status) {
      case 'ACTIVE':      return 'success';
      case 'PENDING':     return 'warn';
      case 'REJECTED':    return 'danger';
      case 'BLACKLISTED': return 'danger';
      case 'SUSPENDED':   return 'secondary';
      case 'INACTIVE':    return 'secondary';
      default:            return 'info';
    }
  }
}
