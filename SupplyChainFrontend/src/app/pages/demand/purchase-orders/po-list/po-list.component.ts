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
import { MessageService } from 'primeng/api';
import { TableLazyLoadEvent } from 'primeng/table';
import { DemandService, PoListItemModel, PoListFilter } from '../../../../services/demand.service';
import { FLOW } from '../../../../shared/flow';

import { DemoBadgeComponent } from '../../../../shared/components/demo-badge/demo-badge.component';

@Component({
  selector: 'app-po-list',
  standalone: true,
  imports: [
    CommonModule, RouterModule, FormsModule,
    TableModule, ButtonModule, InputTextModule,
    InputIconModule, IconFieldModule, TagModule,
    TooltipModule, ToastModule, DropdownModule,
    DemoBadgeComponent, ...FLOW
  ],
  templateUrl: './po-list.component.html',
  styleUrls: ['./po-list.component.scss'],
  providers: [MessageService]
})
export class PoListComponent implements OnInit {
  pos: PoListItemModel[] = [];
  totalRecords = 0;
  currentPage  = 1;
  pageSize     = 20;
  isLoading    = true;

  searchText     = '';
  selectedStatus = '';

  private searchTimer: ReturnType<typeof setTimeout> | null = null;

  statusOptions = [
    { label: 'All Statuses', value: '' },
    { label: 'Draft',        value: 'DRAFT' },
    { label: 'Approved',     value: 'APPROVED' },
    { label: 'Closed',       value: 'CLOSED' },
    { label: 'Cancelled',    value: 'CANCELLED' }
  ];

  constructor(private demandService: DemandService, private messageService: MessageService) {}

  get draftCount(): number    { return this.pos.filter(p => p.status === 'DRAFT').length; }
  get approvedCount(): number { return this.pos.filter(p => p.status === 'APPROVED').length; }
  get totalValue(): number    { return this.pos.reduce((s, p) => s + p.totalAmount, 0); }

  ngOnInit() { this.load(); }

  load() {
    this.isLoading = true;
    const filter: PoListFilter = {
      page: this.currentPage, pageSize: this.pageSize,
      search: this.searchText || undefined,
      status: this.selectedStatus || undefined
    };
    this.demandService.getPos(filter).subscribe({
      next: (res) => {
        this.isLoading = false;
        if (res.success && res.result) {
          this.pos = res.result.data ?? []; this.totalRecords = res.result.totalRecords ?? 0;
        } else { this.pos = []; this.totalRecords = 0; }
      },
      error: () => {
        this.isLoading = false;
        this.messageService.add({ severity: 'error', summary: 'Error', detail: 'Failed to load purchase orders.' });
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

  getStatusSeverity(s: string): 'success' | 'danger' | 'warn' | 'secondary' | 'info' | 'contrast' {
    switch (s) {
      case 'APPROVED':  return 'success';
      case 'CLOSED':    return 'info';
      case 'DRAFT':     return 'secondary';
      case 'CANCELLED': return 'danger';
      default:          return 'secondary';
    }
  }
}
