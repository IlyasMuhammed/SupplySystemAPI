import { Component, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { RouterModule } from '@angular/router';
import { FormsModule } from '@angular/forms';
import { ButtonModule } from 'primeng/button';
import { TableModule } from 'primeng/table';
import { TagModule } from 'primeng/tag';
import { TooltipModule } from 'primeng/tooltip';
import { ToastModule } from 'primeng/toast';
import { DropdownModule } from 'primeng/dropdown';
import { InputTextModule } from 'primeng/inputtext';
import { InputIconModule } from 'primeng/inputicon';
import { IconFieldModule } from 'primeng/iconfield';
import { MessageService } from 'primeng/api';
import { TableLazyLoadEvent } from 'primeng/table';
import { MaterialService, MirListItem, MirListFilter } from '../../../../services/material.service';

@Component({
  selector: 'app-mir-list',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    TableModule, ButtonModule, TagModule,
    TooltipModule, ToastModule, DropdownModule,
    InputTextModule, InputIconModule, IconFieldModule
  ],
  templateUrl: './mir-list.component.html',
  styleUrls: ['./mir-list.component.scss'],
  providers: [MessageService]
})
export class MirListComponent implements OnInit {
  mirs: MirListItem[] = [];
  totalRecords = 0;
  currentPage  = 1;
  pageSize     = 20;
  isLoading    = true;

  filter: MirListFilter = { page: 1, pageSize: 20 };

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  statusOptions = [
    { label: 'All Statuses',      value: '' },
    { label: 'Draft',             value: 'DRAFT' },
    { label: 'Pending Approval',  value: 'PENDING_APPROVAL' },
    { label: 'Approved',          value: 'APPROVED' },
    { label: 'Rejected',          value: 'REJECTED' },
    { label: 'Issued',            value: 'ISSUED' },
    { label: 'Cancelled',         value: 'CANCELLED' }
  ];

  typeOptions = [
    { label: 'All Types',    value: '' },
    { label: 'Project',      value: 'PROJECT' },
    { label: 'Department',   value: 'DEPARTMENT' },
    { label: 'Maintenance',  value: 'MAINTENANCE' }
  ];

  constructor(
    private materialService: MaterialService,
    private messageService: MessageService
  ) {}

  ngOnInit() { this.load(); }

  load() {
    this.isLoading = true;
    const f: MirListFilter = {
      page:        this.currentPage,
      pageSize:    this.pageSize,
      search:      this.filter.search      || undefined,
      status:      this.filter.status      || undefined,
      requestType: this.filter.requestType || undefined
    };
    this.materialService.getMirs(f).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) {
          this.mirs         = res.result.data         ?? [];
          this.totalRecords = res.result.totalRecords ?? 0;
        } else {
          this.mirs = []; this.totalRecords = 0;
        }
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load material issue requests.' });
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
    this.filter      = { page: 1, pageSize: 20 };
    this.currentPage = 1;
    this.load();
  }

  getStatusSeverity(s: string): 'success' | 'danger' | 'warn' | 'secondary' | 'info' | 'contrast' {
    switch (s) {
      case 'DRAFT':            return 'secondary';
      case 'PENDING_APPROVAL': return 'warn';
      case 'APPROVED':         return 'success';
      case 'REJECTED':         return 'danger';
      case 'ISSUED':           return 'info';
      case 'CANCELLED':        return 'danger';
      default:                 return 'secondary';
    }
  }

  getStatusLabel(s: string): string {
    const map: Record<string, string> = {
      DRAFT: 'Draft', PENDING_APPROVAL: 'Pending Approval',
      APPROVED: 'Approved', REJECTED: 'Rejected',
      ISSUED: 'Issued', CANCELLED: 'Cancelled'
    };
    return map[s] ?? s;
  }

  getTypeSeverity(t: string): 'success' | 'danger' | 'warn' | 'secondary' | 'info' | 'contrast' {
    switch (t) {
      case 'PROJECT':     return 'info';
      case 'DEPARTMENT':  return 'warn';
      case 'MAINTENANCE': return 'secondary';
      default:            return 'secondary';
    }
  }

  getTypeLabel(t: string): string {
    const map: Record<string, string> = { PROJECT: 'Project', DEPARTMENT: 'Department', MAINTENANCE: 'Maintenance' };
    return map[t] ?? t;
  }

  getPriorityLabel(p: string): string {
    const map: Record<string, string> = { LOW: 'Low', MEDIUM: 'Medium', HIGH: 'High', URGENT: 'Urgent' };
    return map[p] ?? p;
  }
}
