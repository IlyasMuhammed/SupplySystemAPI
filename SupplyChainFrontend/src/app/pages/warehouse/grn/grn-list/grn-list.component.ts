import { Component, OnInit } from '@angular/core';
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
import { ConfirmDialogModule } from 'primeng/confirmdialog';
import { MessageService, ConfirmationService } from 'primeng/api';
import { TableLazyLoadEvent } from 'primeng/table';
import { WarehouseService, GrnListItemModel, GrnListFilter } from '../../../../services/warehouse.service';

@Component({
  selector: 'app-grn-list',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    TableModule, ButtonModule, InputTextModule,
    InputIconModule, IconFieldModule, TagModule,
    TooltipModule, ToastModule, DropdownModule, ConfirmDialogModule
  ],
  templateUrl: './grn-list.component.html',
  styleUrls: ['./grn-list.component.scss'],
  providers: [MessageService, ConfirmationService]
})
export class GrnListComponent implements OnInit {
  grns: GrnListItemModel[] = [];
  totalRecords = 0;
  currentPage  = 1;
  pageSize     = 20;
  isLoading    = true;

  searchText     = '';
  selectedStatus = '';

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  statusOptions = [
    { label: 'All Statuses',      value: '' },
    { label: 'Draft',             value: 'DRAFT' },
    { label: 'Pending QC',        value: 'PENDING_QC' },
    { label: 'Pending Finance',   value: 'PENDING_FINANCE' },
    { label: 'Pending Approval',  value: 'PENDING_APPROVAL' },
    { label: 'Approved',          value: 'APPROVED' },
    { label: 'Rejected',          value: 'REJECTED' }
  ];

  constructor(
    private warehouseService: WarehouseService,
    private messageService: MessageService,
    private confirmationService: ConfirmationService
  ) {}

  get draftCount():          number { return this.grns.filter(g => g.status === 'DRAFT').length; }
  get pendingQcCount():      number { return this.grns.filter(g => g.status === 'PENDING_QC').length; }
  get pendingApprovalCount():number { return this.grns.filter(g => g.status === 'PENDING_FINANCE' || g.status === 'PENDING_APPROVAL').length; }
  get approvedCount():       number { return this.grns.filter(g => g.status === 'APPROVED').length; }

  ngOnInit() { this.load(); }

  load() {
    this.isLoading = true;
    const filter: GrnListFilter = {
      page: this.currentPage, pageSize: this.pageSize,
      search: this.searchText || undefined,
      status: this.selectedStatus || undefined
    };
    this.warehouseService.getGrns(filter).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) {
          this.grns = res.result.data ?? [];
          this.totalRecords = res.result.totalRecords ?? 0;
        } else { this.grns = []; this.totalRecords = 0; }
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load GRNs.' });
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

  resetFilters() { this.searchText = ''; this.selectedStatus = ''; this.currentPage = 1; this.load(); }

  confirmDelete(grn: GrnListItemModel) {
    this.confirmationService.confirm({
      message: `Delete GRN <strong>${grn.grnNumber}</strong>? This cannot be undone.`,
      header: 'Delete GRN',
      icon: 'pi pi-exclamation-triangle',
      acceptButtonStyleClass: 'p-button-danger',
      accept: () => this.deleteGrn(grn)
    });
  }

  deleteGrn(grn: GrnListItemModel) {
    this.warehouseService.deleteGrn(grn.uuid).subscribe({
      next: () => {
        this.messageService.add({ severity: 'success', summary: 'Deleted', detail: `GRN ${grn.grnNumber} deleted.` });
        this.load();
      },
      error: (err) => {
        this.messageService.add({ severity: 'error', summary: 'Error', detail: err?.error?.message || 'Failed to delete GRN.' });
      }
    });
  }

  getStatusSeverity(s: string): 'success' | 'danger' | 'warn' | 'secondary' | 'info' | 'contrast' {
    switch (s) {
      case 'APPROVED':          return 'success';
      case 'PENDING_QC':        return 'info';
      case 'PENDING_FINANCE':   return 'warn';
      case 'PENDING_APPROVAL':  return 'info';
      case 'REJECTED':          return 'danger';
      case 'DRAFT':             return 'secondary';
      default:                  return 'secondary';
    }
  }
}
